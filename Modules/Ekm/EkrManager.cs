using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace EndKnot.Modules.Ekm;

// R1 (docs/ekr-logic-spec.md) の per-holder ランタイム状態。EkrManager が生成/破棄/Pump を管理し、
// EkrLogicOpcodes (アクション opcode 実装) が直接参照してレートバケット・CNO スロットを読み書きする。
// 「1スロット複数人前提 (Maximum=15)」— キーは常に playerId (byte) であって slot ではない。
internal sealed class EkrHolderState
{
    public readonly Dictionary<string, float> Variables = new();
    public readonly List<EkrFiber> Fibers = [];

    // cno_spawn/cno_move/cno_despawn/cno_show の slot 引数 (1..3) に対応。index = slot - 1。
    public readonly EkrCno[] CnoSlots = new EkrCno[3];
    public readonly float[] LastCnoMoveTime = [-1f, -1f, -1f];

    public int AbortCount;
    public bool LogicDisabled;
    public bool GameStartFired;

    public float LastSecondFireTime = -1f;
    public float LastNotifyTime = -1f;
    // notify が会議中に呼ばれたときだけ使う専用バケット (通常より粗い間隔)。
    // Utils.SendMessage はワールド名札と違い「呼ぶたびにチャット欄へ1行追加」なので、
    // LastNotifyTime (1秒間隔) をそのまま共用すると1回の会議で数十行のスパムになりうる
    // (advisor 指摘・2026-08-09)。EkrLogicOpcodes.Notify() 参照。
    public float LastMeetingNotifyTime = -1f;
    public float LastKillTime = -1f;
    public float LastCnoSpawnTime = -1f;
    // spec §5 (2026-08-09 監査改定): cno_show は cno_spawn と共用せず独自の ≤1/3秒/ホルダー バケット
    // (despawn→respawn の fan-out 未課金コストを織り込んで spawn より厳しくする)。
    public float LastCnoShowTime = -1f;

    public bool SpeedBoostActive;
    public int SpeedGen;
    public float SpeedBaseline;

    public float? KillCooldownOverride;
}

// EKN 役職メーカー R0 の実行時マネージャ。
// 計画正典: docs/ekn-api-plan.md。CustomRoles.EkmCustomRole1..10 の10スロットへ
// EkrDefinition (ノーコード役職定義) を束縛する。ロビー内でのみ束縛操作を許可する
// (試合中の束縛変更はゲームエンド判定等の静的 switch と整合しなくなるため禁止)。
public static class EkrManager
{
    public const string CodePrefix = "EKR1.";

    public static readonly CustomRoles[] Slots =
    [
        CustomRoles.EkmCustomRole1,
        CustomRoles.EkmCustomRole2,
        CustomRoles.EkmCustomRole3,
        CustomRoles.EkmCustomRole4,
        CustomRoles.EkmCustomRole5,
        CustomRoles.EkmCustomRole6,
        CustomRoles.EkmCustomRole7,
        CustomRoles.EkmCustomRole8,
        CustomRoles.EkmCustomRole9,
        CustomRoles.EkmCustomRole10
    ];

    // slot -> 束縛中の定義。ロビーでのみ変更する (Bind/Unbind)。試合中の per-round リセット対象外。
    private static readonly Dictionary<CustomRoles, EkrDefinition> Bound = [];

    // slot -> 束縛元のファイル名。ReloadLibrary 時にディスクの最新定義へ追随させるための再解決キー
    // (これが無いと、束縛後に .ekrole.json を手編集しても旧オブジェクト参照が残り続ける)。
    private static readonly Dictionary<CustomRoles, string> BoundFiles = [];

    // enum 範囲比較の O(1) 判定 (GetRoleSpawnMode 等の高頻度経路から呼ばれる)。
    public static bool IsSlot(CustomRoles role)
    {
        return role is >= CustomRoles.EkmCustomRole1 and <= CustomRoles.EkmCustomRole10;
    }

    // slot -> 現在そのロールが割り当てられているプレイヤー。RoleBase.Init/Add/Remove から更新。
    private static readonly Dictionary<CustomRoles, HashSet<byte>> PlayersBySlot = [];

    // ディスク上のライブラリ (import 済みの役職コード一覧)。
    private static readonly List<(string FileName, EkrDefinition Def)> Library = [];

    public static readonly string RolesPath = BuildRolesPath();

    private static string BuildRolesPath()
    {
        try
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(docs)) return null;
            return docs.Replace('\\', '/') + "/EndKnot/EKRoles/";
        }
        catch
        {
            return null;
        }
    }

    public static void EnsureFolder()
    {
        if (string.IsNullOrEmpty(RolesPath)) return;
        try
        {
            if (!Directory.Exists(RolesPath)) Directory.CreateDirectory(RolesPath);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[EkrManager] Could not create EKRoles folder ({RolesPath}): {ex.Message}", "EkrManager");
        }
    }

    // ── ライブラリ (ディスク上の *.ekrole.json) ─────────────────────────────

    public static void ReloadLibrary()
    {
        Library.Clear();
        EnsureFolder();
        if (string.IsNullOrEmpty(RolesPath) || !Directory.Exists(RolesPath)) return;

        foreach (string path in Directory.GetFiles(RolesPath, "*.ekrole.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string json = File.ReadAllText(path);
                if (!EkrDefinition.TryParse(json, out EkrDefinition def, out string error))
                {
                    Logger.Warn($"[EkrManager] Skipping invalid role file {Path.GetFileName(path)}: {error}", "EkrManager");
                    continue;
                }

                Library.Add((Path.GetFileName(path), def));
            }
            catch (Exception ex)
            {
                Logger.Warn($"[EkrManager] Could not read role file {path}: {ex.Message}", "EkrManager");
            }
        }

        // 束縛済みスロットをディスクの最新定義へ追随させる (手編集や再 import の反映)。
        // ファイルが消えた/壊れた場合は旧定義のまま維持する (束縛が無言で外れて湧かなくなる事故を避ける)。
        // ReloadLibrary は /role コマンド (ロビー限定) からしか呼ばれないので、試合中に定義が差し替わることはない。
        foreach ((CustomRoles slot, string fileName) in BoundFiles.ToArray())
        {
            foreach ((string fn, EkrDefinition def) in Library)
            {
                if (fn != fileName) continue;

                if (!ReferenceEquals(def, Bound.GetValueOrDefault(slot))) Bind(slot, def, fileName);
                break;
            }
        }
    }

    public static IReadOnlyList<(string FileName, EkrDefinition Def)> ListLibrary()
    {
        return Library;
    }

    public static bool TryImportCode(string code, out string savedFileName, out string error)
    {
        savedFileName = null;

        if (!EkmCodec.TryDecode(code, out string json, out error, CodePrefix))
            return false;

        if (!EkrDefinition.TryParse(json, out EkrDefinition def, out error))
            return false;

        EnsureFolder();
        if (string.IsNullOrEmpty(RolesPath))
        {
            error = "保存先フォルダ (Documents/EndKnot/EKRoles) を用意できませんでした";
            return false;
        }

        string baseName = SanitizeFileName(def.Name);
        string fileName = $"{baseName}.ekrole.json";
        string fullPath = RolesPath + fileName;

        // 同名は連番で回避 (上書きしない)。
        int n = 2;
        while (File.Exists(fullPath))
        {
            fileName = $"{baseName}_{n}.ekrole.json";
            fullPath = RolesPath + fileName;
            n++;
        }

        try
        {
            File.WriteAllText(fullPath, json, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            error = $"役職コードの保存に失敗しました ({ex.Message})";
            return false;
        }

        savedFileName = fileName;
        ReloadLibrary();
        return true;
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "custom_role";
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (char c in name)
            sb.Append(invalid.Contains(c) || c is ' ' or '.' ? '_' : c);
        string result = sb.ToString().Trim('_');
        return result.Length == 0 ? "custom_role" : result;
    }

    // ── スロット束縛 (ロビーのみ) ────────────────────────────────────────────

    public static bool TryAssign(int libraryIndex1Based, int slotNumber1Based, out string error)
    {
        error = null;

        if (!GameStates.IsLobby)
        {
            error = "役職の割り当てはロビーでのみ変更できます";
            return false;
        }

        if (slotNumber1Based is < 1 or > 10)
        {
            error = "スロット番号は1〜10で指定してください";
            return false;
        }

        if (libraryIndex1Based < 1 || libraryIndex1Based > Library.Count)
        {
            error = "その番号の役職コードが見つかりません (/role list で確認してください)";
            return false;
        }

        CustomRoles slot = Slots[slotNumber1Based - 1];
        (string fileName, EkrDefinition def) = Library[libraryIndex1Based - 1];
        Bind(slot, def, fileName);
        return true;
    }

    public static bool TryUnassign(int slotNumber1Based, out string error)
    {
        error = null;

        if (!GameStates.IsLobby)
        {
            error = "役職の割り当てはロビーでのみ変更できます";
            return false;
        }

        if (slotNumber1Based is < 1 or > 10)
        {
            error = "スロット番号は1〜10で指定してください";
            return false;
        }

        Unbind(Slots[slotNumber1Based - 1]);
        return true;
    }

    private static void Bind(CustomRoles slot, EkrDefinition def, string fileName)
    {
        Bound[slot] = def;
        BoundFiles[slot] = fileName;

        // 表示名の実行時上書き (RoleBase.StartSetup 系・GetRoleName 等が共通で読む翻訳キー = 型名 = enum 名)。
        Translator.SetRuntimeOverride(slot.ToString(), def.Name);

        // 色: Main.RoleHtmlColors が正典 (Main.cs RoleHtmlColors 辞書)。
        Main.RoleHtmlColors[slot] = def.Color;
        Main.InitRoleColors();

        // 束縛 = 「次のゲームで使う」宣言なので、出現率オプションを 100% にしてメニューにも出す。
        // 出現率はプリセット別配列 (OptionItem.AllValues) なので全プリセットへ反映する — 現在プリセットだけ
        // 書くと、ホストがプリセットを切り替えた瞬間「束縛表示は残るのに出現率 0 で湧かない」無音不整合になる。
        // 保存されても安全: 未束縛スロットは Options.GetRoleSpawnMode のガードで常に 0 扱いになる。
        if (Options.CustomRoleSpawnChances != null && Options.CustomRoleSpawnChances.TryGetValue(slot, out var opt))
        {
            opt.SetColor(Utils.GetRoleColor(slot));
            opt.SetHidden(false);
            opt.SetAllValues(Enumerable.Repeat(Options.Rates.Length - 1, OptionItem.NumPresets).ToArray());
            opt.SetValue(Options.Rates.Length - 1); // SetAllValues は同期/保存を発火しないため、現在値の SetValue で締める
        }
    }

    private static void Unbind(CustomRoles slot)
    {
        Bound.Remove(slot);
        BoundFiles.Remove(slot);
        Translator.ClearRuntimeOverride(slot.ToString());

        if (Options.CustomRoleSpawnChances != null && Options.CustomRoleSpawnChances.TryGetValue(slot, out var opt))
        {
            opt.SetAllValues(new int[OptionItem.NumPresets]);
            opt.SetValue(0);
            opt.SetHidden(true);
        }
    }

    // /role set でスロット省略時に使う最初の空きスロット (1..10)。空きが無ければ 0。
    public static int FirstFreeSlotNumber()
    {
        for (int i = 0; i < Slots.Length; i++)
        {
            if (!Bound.ContainsKey(Slots[i]))
                return i + 1;
        }

        return 0;
    }

    public static EkrDefinition GetDefinition(CustomRoles slot)
    {
        return Bound.GetValueOrDefault(slot);
    }

    // 束縛中の役職コードが on_pet ルールを持つか (CustomRolesHelper.PetActivatedAbility 用)。
    // OnPet override は中間基底 EkmTemplateRole 宣言のため、リフレクションの「直接の型が宣言」判定に
    // 乗らない — ペットボタン活性・ペットアニメキャンセルの経路はこちらで判定する。
    public static bool HasOnPetLogic(CustomRoles slot)
    {
        EkrDefinition def = GetDefinition(slot);
        if (def?.ParsedLogic == null) return false;

        foreach (EkrRule rule in def.ParsedLogic.Rules)
            if (rule.When == "on_pet")
                return true;

        return false;
    }

    public static bool IsBound(CustomRoles slot)
    {
        return Bound.ContainsKey(slot);
    }

    // ── per-round プレイヤー追跡 (RoleBase.Init/Add/Remove から呼ばれる) ────────

    public static void ResetSlot(CustomRoles slot)
    {
        if (PlayersBySlot.TryGetValue(slot, out HashSet<byte> set)) set.Clear();
        else PlayersBySlot[slot] = [];

        // R1: LastMeetingEndNum は slot キー (playerId キーではない) なので、per-player の
        // Add/Remove サイクルでは自然に片付かない。MeetingStates.MeetingNum は新しいラウンドの
        // 開始時に 0 へリセットされる (Patches/OnGameStartedPatch.cs) ため、ここで前ラウンドの
        // 値を残すと「前ラウンドの最終値と偶然一致する会議番号」で on_meeting_end が誤って
        // 重複排除されてしまう (ラウンド境界の取りこぼし)。Init() (=ResetSlot) はラウンド毎に
        // 必ず1回呼ばれるので、ここで確実に破棄する。
        LastMeetingEndNum.Remove(slot);
    }

    public static void AddPlayer(CustomRoles slot, byte playerId)
    {
        if (!PlayersBySlot.TryGetValue(slot, out HashSet<byte> set)) PlayersBySlot[slot] = set = [];
        set.Add(playerId);
        InitRuntime(slot, playerId);
    }

    public static void RemovePlayer(CustomRoles slot, byte playerId)
    {
        if (PlayersBySlot.TryGetValue(slot, out HashSet<byte> set)) set.Remove(playerId);
        TeardownRuntime(playerId);
    }

    public static bool HasPlayers(CustomRoles slot)
    {
        return PlayersBySlot.TryGetValue(slot, out HashSet<byte> set) && set.Count > 0;
    }

    // ── R1: per-holder ロジックランタイム (docs/ekr-logic-spec.md) ──────────────

    private static readonly Dictionary<byte, EkrHolderState> Runtime = [];

    // spec §5 (2026-08-09 監査改定):「全体 ≤10体」は導出型で数える — 手動カウンタ (増減の対称性が崩れると
    // 片方向リークで無音に上限が機能しなくなる構造を持つ) を廃止し、CanOccupyCnoSlot() の呼び出し毎に
    // 全ホルダーの CnoSlots から都度数え直す。実体化前 (pending) の slot も「予約済み」として数える —
    // spawn コルーチンは既に起動済みでいずれ実体化するため、実体化後だけ数えると瞬間的に 10 体超の
    // pending が積み上がりうる (安全側の解釈)。
    //
    // 既知の受容挙動: これと下記の「実体化前は cno_despawn/同一slot再spawn をドロップ」を組み合わせると、
    // 3 slot 全てが実体化待ちの間はそのホルダーの CNO 系操作が (despawn も re-spawn も) 全て no-op になる
    // (spawn コルーチン完了まで自己解決する一時停止 — バグではない)。全ホルダー合算の 10 体上限も同様に
    // 起動直後のバーストで一時的に pending だけで埋まりうる (最大 ~30秒の spawn 遅延ぶん)。
    private const int MaxGlobalCno = 10;

    private static int CountLiveCno()
    {
        int count = 0;

        foreach (EkrHolderState state in Runtime.Values)
            for (int i = 0; i < state.CnoSlots.Length; i++)
                if (state.CnoSlots[i] != null) count++;

        return count;
    }

    // Init() (RoleBase.Init、role.RoleExist(true)==false のときに1回) の直後、Add(playerId) から
    // 呼ばれる。def が未束縛/logic 無しでも空の state は持たせておく (SetKillCooldown 等が
    // GetHolderState 経由で参照するため — logic の有無に関わらず一貫して引ける方が呼び出し側が単純になる)。
    private static void InitRuntime(CustomRoles slot, byte playerId)
    {
        var state = new EkrHolderState();

        EkrDefinition def = GetDefinition(slot);

        if (def?.ParsedLogic != null)
        {
            foreach (EkrVariable v in def.ParsedLogic.Variables)
                state.Variables[v.Name] = v.Init;
        }

        Runtime[playerId] = state;
    }

    // Role.Remove(playerId) は役職の入れ替わり (ラウンド境界含む) 全経路で呼ばれる唯一の解体点
    // (SetMainRole が新役職を割り当てる直前に旧役職インスタンスへ必ず投げる)。CNO の後始末もここに集約する。
    //
    // ラウンド境界以外でも起きる (Randomizer/Imitator/Amnesiac 等の役職再割当て) ため、speed ブースト中に
    // ここへ来ると EkrLogicOpcodes.Speed() の遅延復元タスクが GetHolderState(playerId)==null で早期 return し、
    // Main.AllPlayerSpeed が永久にブースト値のまま固定される (memory: allplayerspeed-temp-boost-restore-race
    // と同型の破棄経路)。teardown 時点で即座に復元することで防ぐ。
    private static void TeardownRuntime(byte playerId)
    {
        if (!Runtime.Remove(playerId, out EkrHolderState state)) return;

        if (state.SpeedBoostActive)
        {
            // 凍結中 (他の役職の SetDark/ノックバック等が MinSpeed を敷いている) は触らない — 復元すると
            // 相手側の凍結を巻き戻してしまう。ここで諦めて放置すると、この state は teardown 済みで
            // 誰も再試行しないまま「相手の凍結解除がブースト値を復元先として控えたまま解除」→ 永久高速固定
            // になる (memory: allplayerspeed-temp-boost-restore-race と同型)。凍結が抜けるまで再試行する。
            if (Mathf.Approximately(Main.AllPlayerSpeed.GetValueOrDefault(playerId), Main.MinSpeed))
                RetryRestoreSpeed(playerId, state.SpeedBaseline, retriesLeft: 30);
            else
            {
                Main.AllPlayerSpeed[playerId] = state.SpeedBaseline;
                PlayerControl pc = playerId.GetPlayer();
                if (pc) pc.MarkDirtySettings();
            }

            state.SpeedBoostActive = false;
        }

        for (int i = 0; i < state.CnoSlots.Length; i++)
        {
            EkrCno cno = state.CnoSlots[i];
            if (cno == null) continue;
            state.CnoSlots[i] = null;

            // spec §5 孤児コルーチン防止裁定: 実体化前 (playerControl 未生成) は Despawn を呼んでも
            // 基底 spawn コルーチンは止まらず、いずれ勝手に実体化して追跡外のまま居座る。実体化を
            // 待って遅延 Despawn を再試行する。
            if (cno.IsInstantiated) cno.Despawn();
            else RetryDespawnUninstantiated(cno, retriesLeft: 5);
        }
    }

    // teardown 時点で凍結中だった speed ブーストの復元を、凍結が解けるまで再試行する。playerId は
    // この呼び出し後に他の役職・別の EKR スロットへ再割当てされうるため、EkrHolderState には依存せず
    // baseline を値渡しで持ち回る。ただし再試行の間に「同じ playerId が新しい EKR speed ブーストを
    // 開始している」ケースがありうる (advisor 指摘・2026-08-09) — 復元直前に新しい持ち主がブーストを
    // 管理していないか確認し、していればこの再試行は諦める (新しい持ち主の責務に譲る。でないと
    // 新しいブーストを古い baseline で踏み潰してしまう)。
    private static void RetryRestoreSpeed(byte playerId, float baseline, int retriesLeft)
    {
        if (GameStates.IsEnded) return;
        if (Runtime.TryGetValue(playerId, out EkrHolderState newState) && newState.SpeedBoostActive) return;

        if (Mathf.Approximately(Main.AllPlayerSpeed.GetValueOrDefault(playerId), Main.MinSpeed))
        {
            if (retriesLeft <= 0) return; // 諦める (相手の凍結解除に委ねる)
            LateTask.New(() => RetryRestoreSpeed(playerId, baseline, retriesLeft - 1), 1f, log: false);
            return;
        }

        Main.AllPlayerSpeed[playerId] = baseline;
        PlayerControl pc = playerId.GetPlayer();
        if (pc) pc.MarkDirtySettings();
    }

    // 実体化前に teardown された CNO を、実体化を待って回収する (spec §5)。cno はどの EkrHolderState にも
    // 属さなくなった後 (teardown 済み) の生存インスタンスをそのまま直接持ち回る — 他の誰にも参照されて
    // いないので新しい持ち主との衝突を考える必要はない (speed のケースと異なる点)。
    private static void RetryDespawnUninstantiated(EkrCno cno, int retriesLeft)
    {
        if (GameStates.IsEnded) return;

        if (!cno.IsInstantiated)
        {
            if (retriesLeft <= 0) return; // 通常あり得ない長さの spawn 遅延 (既知の最大30秒待ちより十分な余裕)
            LateTask.New(() => RetryDespawnUninstantiated(cno, retriesLeft - 1), 25f, log: false);
            return;
        }

        cno.Despawn();
    }

    // EkrLogicOpcodes 用の内部アクセサ (レートバケット/CNO スロットの直接読み書き)。未追跡/disable 済みは null。
    internal static EkrHolderState GetHolderState(byte playerId)
    {
        return Runtime.TryGetValue(playerId, out EkrHolderState state) && !state.LogicDisabled ? state : null;
    }

    internal static float? GetKillCooldownOverride(byte playerId)
    {
        return Runtime.TryGetValue(playerId, out EkrHolderState state) ? state.KillCooldownOverride : null;
    }

    internal static bool CanOccupyCnoSlot() => CountLiveCno() < MaxGlobalCno;

    // 上限チェック (CanOccupyCnoSlot) を先に済ませたあとにだけ呼ぶこと。
    internal static void OccupyCnoSlot(EkrHolderState state, int slotIndex1Based, EkrCno cno)
    {
        state.CnoSlots[slotIndex1Based - 1] = cno;
    }

    // cno_despawn opcode から直接呼ばれる他、cno_spawn の「同一 slot への再 spawn」でも「消してから作る」の
    // 消す側として使われる。実体化前 (playerControl 未生成) の CNO は spec §5 の孤児コルーチン防止裁定に
    // よりドロップ (no-op) する — slot は占有されたまま維持される (「まだ出ていないものは変えられない」)。
    // cno_spawn 側は「既存占有者が未実体化なら release を試みる前に spawn ごと諦める」を別途行う
    // (EkrLogicOpcodes.CnoSpawn 参照 — ここで release が no-op になっただけでは新規 CNO の occupy を防げない)。
    internal static void ReleaseCnoSlot(EkrHolderState state, int slotIndex1Based)
    {
        int idx = slotIndex1Based - 1;
        EkrCno existing = state.CnoSlots[idx];
        if (existing == null) return;
        if (!existing.IsInstantiated) return;

        state.CnoSlots[idx] = null;
        existing.Despawn();
    }

    // ── R1: イベント発火 (RoleBase フック → EkmTemplateRole の薄い呼び出し先) ──────

    private static void FireEvent(CustomRoles slot, byte holderId, string eventName, byte ctxId)
    {
        if (!Runtime.TryGetValue(holderId, out EkrHolderState state) || state.LogicDisabled) return;

        // spec §2 死亡時の意味論 (2026-08-09 監査裁定): 死後の新規イベントは on_death 以外発火しない
        // (会議系イベントも含む — 死者はもう何も観測しない)。on_death 自体はホルダーが死亡確定した
        // 瞬間に発火するものなのでこのゲートから除外する。
        if (eventName != "on_death")
        {
            PlayerControl holderPc = holderId.GetPlayer();
            if (!holderPc || !holderPc.IsAlive()) return;
        }

        EkrDefinition def = GetDefinition(slot);
        if (def?.ParsedLogic == null) return;

        foreach (EkrRule rule in def.ParsedLogic.Rules)
        {
            if (rule.When != eventName) continue;
            if (state.Fibers.Count >= EkmLogicRuntime.MaxFibersPerHolder) continue; // spec §5: 超過は新規発火をドロップ

            var context = new EkrActionContext { HolderId = holderId, CtxId = ctxId, Slot = slot };
            state.Fibers.Add(EkmLogicRuntime.Spawn(rule.Do, state.Variables, context, EkrActionSink.InOpcodeKill));
        }
    }

    public static void FirePet(CustomRoles slot, PlayerControl pc) => FireEvent(slot, pc.PlayerId, "on_pet", byte.MaxValue);

    public static void FireKill(CustomRoles slot, PlayerControl killer, PlayerControl victim) =>
        FireEvent(slot, killer.PlayerId, "on_kill", victim ? victim.PlayerId : byte.MaxValue);

    public static void FireTaskComplete(CustomRoles slot, PlayerControl pc) => FireEvent(slot, pc.PlayerId, "on_task_complete", byte.MaxValue);

    public static void FireVentEnter(CustomRoles slot, PlayerControl pc) => FireEvent(slot, pc.PlayerId, "on_vent_enter", byte.MaxValue);

    // target の死亡確定時 (spec: 自分が死んだとき・ctx=キルした人 [いれば])。Utils.AfterPlayerDeathTasks から
    // 呼ぶ想定 — target 自身が EKR ホルダーかどうかは呼び出し前提を置かず、ここで判定する。
    public static void FireDeath(PlayerControl target, PlayerControl killer)
    {
        if (!target) return;

        CustomRoles slot = target.GetCustomRole();
        if (!IsSlot(slot)) return;

        // spec §2 死亡時の意味論 (2026-08-09 監査裁定): 死亡で走行中 fiber を全キャンセル → その後
        // on_death を発火する (この fiber だけは死後も実行可 — 「死んだら爆発」演出のため)。FireEvent
        // 側の「on_death 以外は死後発火しない」ゲートとセットで、以後この保持者は on_death 起点の
        // fiber しか持たなくなる。
        if (Runtime.TryGetValue(target.PlayerId, out EkrHolderState state)) state.Fibers.Clear();

        FireEvent(slot, target.PlayerId, "on_death", killer ? killer.PlayerId : byte.MaxValue);
    }

    // reporter が EKR ホルダーのときだけ発火 (spec: 自分が通報者になったとき・ctx=死体の主)。
    public static void FireReport(PlayerControl reporter, PlayerControl bodyOwner)
    {
        if (!reporter) return;

        CustomRoles slot = reporter.GetCustomRole();
        if (!IsSlot(slot)) return;

        FireEvent(slot, reporter.PlayerId, "on_report", bodyOwner ? bodyOwner.PlayerId : byte.MaxValue);
    }

    // 会議開始 (ボタン/通報どちらでも1回・spec §2)。全 EKR ホルダー共通の「走行中 fiber は全キャンセル」
    // を先に行ってから on_meeting_start を発火する (キャンセル後に発火 — 新しく生える fiber は対象外)。
    public static void FireMeetingStart()
    {
        foreach (EkrHolderState state in Runtime.Values) state.Fibers.Clear();

        foreach (CustomRoles slot in Slots)
        {
            if (!PlayersBySlot.TryGetValue(slot, out HashSet<byte> holders) || holders.Count == 0) continue;

            EkrDefinition def = GetDefinition(slot);
            if (def?.ParsedLogic == null) continue;

            foreach (byte holderId in holders) FireEvent(slot, holderId, "on_meeting_start", byte.MaxValue);
        }
    }

    // 会議中は RoleBase.OnFixedUpdate (→Pump) が呼ばれないため、MeetingHud.Update 側から毎フレーム
    // 呼んで fiber を進める (spec §3 は会議中も notify [チャット私信] を有効と規定 — Execute 側の
    // IsMeeting ガードがアクション no-op を保証するので安全。wait 中の fiber は WakeAt 経過後に
    // ここで再開し、会議明け後は通常の Pump が引き継ぐ)。命令数の Abort 計数は通常 Pump と違い
    // 省略する — do ≤64 制約下の会議中実行で 500 命令/fiber には構造的に届かない。
    public static void PumpMeetingFibers()
    {
        foreach (EkrHolderState state in Runtime.Values)
        {
            if (state.LogicDisabled || state.Fibers.Count == 0) continue;

            EkrFiber[] snapshot = state.Fibers.ToArray();

            for (int i = snapshot.Length - 1; i >= 0; i--)
            {
                EkrFiber fiber = snapshot[i];
                if (!state.Fibers.Contains(fiber)) continue;
                if (!EkmLogicRuntime.Pump(fiber, EkrActionSink.Instance)) state.Fibers.Remove(fiber);
            }
        }
    }

    // 会議明け・タスク再開時 (spec: on_meeting_end)。RoleBase.AfterMeetingTasks() はシグネチャに
    // プレイヤーを持たず、10スロット共有シングルトンの1インスタンスが「保持者の人数ぶん」呼ばれる
    // (Utils.AfterMeetingTasks が全プレイヤーをループし、各プレイヤーの Role.AfterMeetingTasks() を呼ぶため)。
    // ここで会議番号ベースの重複排除をしないと、同じ会議明けで保持者数² 回 fiber が湧く。
    private static readonly Dictionary<CustomRoles, int> LastMeetingEndNum = [];

    public static void FireMeetingEndForSlot(CustomRoles slot)
    {
        int meetingNum = MeetingStates.MeetingNum;
        if (LastMeetingEndNum.TryGetValue(slot, out int last) && last == meetingNum) return;
        LastMeetingEndNum[slot] = meetingNum;

        if (!PlayersBySlot.TryGetValue(slot, out HashSet<byte> holders)) return;

        foreach (byte holderId in holders) FireEvent(slot, holderId, "on_meeting_end", byte.MaxValue);
    }

    // 毎 FixedUpdate、保持者ごとに1回呼ぶ (EkmTemplateRole.OnFixedUpdate から)。on_game_start の
    // 立ち上がり検出・on_second の 1Hz 間引き・fiber の手動ポンプ (常駐コルーチン禁止) をここでまとめて行う。
    public static void Pump(CustomRoles slot, PlayerControl pc)
    {
        if (!Runtime.TryGetValue(pc.PlayerId, out EkrHolderState state) || state.LogicDisabled) return;

        EkrDefinition def = GetDefinition(slot);
        if (def?.ParsedLogic == null) return;

        if (!state.GameStartFired && Main.IntroDestroyed)
        {
            state.GameStartFired = true;
            FireEvent(slot, pc.PlayerId, "on_game_start", byte.MaxValue);
        }

        // spec §2: on_second は「タスク中・自分生存中のみ」1Hz。
        if (Main.IntroDestroyed && GameStates.IsInTask && pc.IsAlive())
        {
            float now = Time.realtimeSinceStartup;

            if (state.LastSecondFireTime < 0f || now - state.LastSecondFireTime >= 1f)
            {
                state.LastSecondFireTime = now;
                FireEvent(slot, pc.PlayerId, "on_second", byte.MaxValue);
            }
        }

        // kill(target:"self") はキルパイプラインが同期的なので、この fiber を pump している最中に
        // 自分の on_death (spec §2: 死亡で fiber を全キャンセル→発火) が同一コールスタックで
        // state.Fibers を Clear()+再構築することがある。添字ベースの反復だと範囲外アクセスや
        // 「新しく生えた on_death fiber を誤って削除する」事故になるため、この tick で処理すべき
        // fiber を先にスナップショットし、各要素を pump する直前に「まだ生きているか」を再確認する
        // (Clear 済みならその fiber はこの tick ではもう進めない — 「全キャンセル」を壊さないため)。
        EkrFiber[] snapshot = state.Fibers.ToArray();

        for (int i = snapshot.Length - 1; i >= 0; i--)
        {
            EkrFiber fiber = snapshot[i];
            if (!state.Fibers.Contains(fiber)) continue; // 再入で既に Clear 済み — この tick はもう進めない

            bool keep = EkmLogicRuntime.Pump(fiber, EkrActionSink.Instance);
            if (keep) continue;

            state.Fibers.Remove(fiber); // 再入で既に居ない場合は no-op (添字ではなく参照で消す)
            if (!fiber.Aborted) continue;

            state.AbortCount++;

            if (state.AbortCount >= 3 && !state.LogicDisabled)
            {
                state.LogicDisabled = true;
                state.Fibers.Clear();
                PlayerControl.LocalPlayer.Notify(string.Format(Translator.GetString("EkrLogicAutoDisabled"), Translator.GetString(slot.ToString())), 10f);
                break;
            }
        }
    }

    // ── R1: EKR 全体の cross-holder レート予算 (spec §3 2026-08-09 追記) ──────────

    // teleport は Utils.TP の共有 SnapTo トークンバケットに乗っている。ホルダー毎の ≤1/2秒だけでは
    // Maximum=15 で全ホルダーが同時に撃つと共有 cap を枯渇させ、EKR 以外の TP 系能力まで巻き込んで
    // 止めてしまう (memory: multiplayer-pull-tp-cap-budget と同型の懸念)。EKR 全体で ≤2/秒に鎖をかける。
    private static readonly List<float> _recentTeleportTimes = [];

    internal static bool TryConsumeGlobalTeleportBudget()
    {
        float now = Time.realtimeSinceStartup;
        _recentTeleportTimes.RemoveAll(t => now - t >= 1f);

        if (_recentTeleportTimes.Count >= 2) return false;

        _recentTeleportTimes.Add(now);
        return true;
    }
}
