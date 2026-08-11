using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using UnityEngine;

namespace EndKnot.Modules.Ekm;

// R1 (docs/ekr-logic-spec.md) の per-holder ランタイム状態。EkrManager が生成/破棄/Pump を管理し、
// EkrLogicOpcodes (アクション opcode 実装) が直接参照してレートバケット・CNO スロットを読み書きする。
// 「1スロット複数人前提 (Maximum=15)」— キーは常に playerId (byte) であって slot ではない。
internal sealed class EkrHolderState
{
    public readonly Dictionary<string, float> Variables = new();
    public readonly List<EkrFiber> Fibers = [];

    // cno_spawn/cno_move/cno_despawn/cno_show/dummy_spawn の slot 引数 (1..3) に対応。index = slot - 1。
    // IEkrSlotCno 抽象で EkrCno (テキスト) / EkrDummyCno (player-like・v1.1) のどちらも同じ配列に入る
    // (契約正典: docs/ekr-logic-spec.md §3 v1.1 「dummy_spawn の slot は cno_spawn と共有」)。
    public readonly IEkrSlotCno[] CnoSlots = new IEkrSlotCno[3];
    public readonly float[] LastCnoMoveTime = [-1f, -1f, -1f];

    // v1.2 (docs/ekr-logic-spec.md §3 marker_save): per-holder 位置メモリ 4 スロット。会議をまたいで保持、
    // ゲーム開始時 (= InitRuntime が state を作り直すタイミング) に自然に全消去される。
    public readonly Vector2?[] Markers = new Vector2?[4];

    // v1.2 (§3 portal_place): CnoSlots (3 枠) とは別の専用 2 枠 (index 0=a, 1=b)。実体は EkrCno 系のみ
    // (IEkrSlotCno は cno_move/despawn 共有インターフェースそのものだが、portal はロジック op から
    // cno_move/despawn の対象にはならない — 直接 EkrManager のポータル専用アクセサ経由でのみ操作する)。
    public readonly IEkrSlotCno[] Portals = new IEkrSlotCno[2];
    public float LastPortalPlaceTime = -1f;

    // v1.2 (§2 on_cno_touch / §5 ポータル warp): 接触ラッチ・デバウンス状態。index は CnoSlots/Portals と対応。
    // 「ラッチ中 (latched.Contains(playerId))」= enter 済み・出るまで再発火しない。
    public readonly HashSet<byte>[] TouchLatched = [[], [], []];
    public readonly Dictionary<byte, float>[] TouchLastFireTime = [new(), new(), new()];
    public readonly HashSet<byte>[] PortalLatched = [[], []];
    // ポータルの CD は「入った側」ではなく player 単位 (spec §3: per-player warp CD 3秒) — 両側で共有する
    // 単一の辞書にする (side ごとに分けると、A→B→(3秒待たず)A→B の高速往復を止められない)。
    public readonly Dictionary<byte, float> PortalLastWarpTime = new();

    // v1.2 監査修正 (2026-08-10): センサー実体の「非実体→実体」遷移 (初回 spawn / 会議明け復活 /
    // ポータル移設) を検出してラッチ/デバウンスを作り直すための前回ポーリング時の実体化状態。
    // 復活後に旧ラッチが残ると「半径内スポーンで enter が永久不発」「切断者の残留エントリを
    // PlayerId 再利用者が無音継承」の2事故になる (pitfall 監査指摘)。
    public readonly bool[] TouchSensorWasLive = new bool[3];
    public readonly bool[] PortalSensorWasLive = new bool[2];

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
    // v1.1: dummy_spawn ≤1/3秒/ホルダー・corpse_spawn ≤1/2秒/ホルダー (spec §5)。
    public float LastDummySpawnTime = -1f;
    public float LastCorpseSpawnTime = -1f;
    // v1.3: field ≤1/2秒/ホルダー (spec §5 — CNO 生成系防御3点の per-holder レート枠)。
    public float LastFieldPlaceTime = -1f;

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
        // Library はこの関数の呼び出しごとに毎回作り直されるため、参照比較は毎回不一致になり Bind() が
        // 呼ばれ得る — 台帳内容は変わらないので、無駄な再保存を避けるため書き戻し抑制ガードをかける
        // (advisor 指摘・2026-08-10。RestoreBindings の _suppressSave と同じもの)。
        _suppressSave = true;

        try
        {
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
        finally
        {
            _suppressSave = false;
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

    // slot 束縛をゲーム再起動をまたいで永続化するファイル (docs 裁定 2026-08-10)。EKRoles フォルダ直下に
    // 置くが、ReloadLibrary の `*.ekrole.json` スキャンには "_bindings.json" は一致しないため拾われない
    // (先頭の `_` はスキャン対象拡張子と衝突しないことの確認用の意図的な命名)。
    private static string BindingsFilePath => string.IsNullOrEmpty(RolesPath) ? null : RolesPath + "_bindings.json";

    // RestoreBindings (と ReloadLibrary の再解決ループ) が内部で Bind() を呼ぶ間は台帳を書き戻さない
    // (advisor 指摘・2026-08-10)。これが無いと、復元中にファイルが見つからず skip したスロットの
    // 記録が「見つかったスロットだけの再保存」で消え、ユーザーがファイルを元に戻しても二度と
    // 復活しなくなる (「ファイルを戻せば次回復活する」という設計要件を壊す)。
    private static bool _suppressSave;

    // Bind/Unbind の全ミューテーション経路から呼ぶ。書き込み失敗はログ警告のみ (ゲームを止めない —
    // 束縛自体はメモリ上には反映済みなので、今回のセッションの動作には影響しない)。
    private static void SaveBindings()
    {
        if (_suppressSave) return;

        string path = BindingsFilePath;
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            EnsureFolder();

            var slots = new Dictionary<string, string>();
            foreach ((CustomRoles slot, string fileName) in BoundFiles) slots[slot.ToString()] = fileName;

            var root = new { ekrBindings = 1, slots };
            string json = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Logger.Warn($"[EkrManager] Could not save slot bindings: {ex.Message}", "EkrManager");
        }
    }

    // ゲーム再起動後、役職選出より前に必ず1回呼ぶこと (Options.GetRoleSpawnMode の Bound ゲートより前)。
    // Library (ReloadLibrary が既に populate 済みである前提) からファイル名を解決して Bind() する。
    // ファイルが消えている/壊れているスロットはそのスロットだけスキップしログする (出現率オプションの
    // 保存値には触れない — ユーザーがファイルを元に戻せば次回の ReloadLibrary で自然に復活する)。
    public static void RestoreBindings()
    {
        string path = BindingsFilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        try
        {
            string json = File.ReadAllText(path);
            using JsonDocument doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("slots", out JsonElement slotsElem) || slotsElem.ValueKind != JsonValueKind.Object)
                return;

            _suppressSave = true;

            try
            {
                foreach (JsonProperty prop in slotsElem.EnumerateObject())
                {
                    // キーは Bind 直後に enum 名 (slot.ToString()) で書き出したもの限定。数値文字列を
                    // 手編集で入れても Enum.TryParse は数値を許容してしまうが、IsSlot ガードで弾かれる。
                    if (!Enum.TryParse(prop.Name, out CustomRoles slot) || !IsSlot(slot)) continue;

                    string fileName = prop.Value.GetString();
                    if (string.IsNullOrEmpty(fileName)) continue;

                    bool found = false;

                    foreach ((string fn, EkrDefinition def) in Library)
                    {
                        if (fn != fileName) continue;
                        Bind(slot, def, fileName);
                        found = true;
                        break;
                    }

                    // ここに来るのは「ファイルが消えた」だけでなく「JSON が壊れていて ReloadLibrary が
                    // 既に読み込みをスキップした」場合も含む (詳細な理由はその時点で別途 warn 済み)。
                    if (!found)
                        Logger.Warn($"[EkrManager] Could not restore slot {slot} <- {fileName}: file is missing, or was skipped as invalid (see the warning above). The slot stays unbound; fix or restore the file and it will bind again next launch.", "EkrManager");
                }
            }
            finally
            {
                _suppressSave = false;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[EkrManager] Could not restore slot bindings: {ex.Message}", "EkrManager");
        }
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

        SaveBindings();
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

        SaveBindings();
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
        // v1.3: crowd-control の帰属判定は set を空にする前に採る (下の _cc クリア条件で使う)。
        // このスロットの保持者のものである場合に加え、どのスロットの保持者でもなくなった孤児も断つ
        // (ラウンド境界で前ラウンドの保持者が既に全 set から消えていると、帰属チェックだけでは
        // 誰もクリアできず新ラウンドへ持ち越される — 元の無条件クリアが守っていたケース)。
        bool ccShouldClear = false;

        if (_cc != null)
        {
            bool ownedBySomeSlot = false;

            foreach (HashSet<byte> owners in PlayersBySlot.Values)
            {
                if (!owners.Contains(_cc.HolderId)) continue;

                ownedBySomeSlot = true;
                break;
            }

            ccShouldClear = !ownedBySomeSlot || (PlayersBySlot.TryGetValue(slot, out HashSet<byte> mine) && mine.Contains(_cc.HolderId));
        }

        if (PlayersBySlot.TryGetValue(slot, out HashSet<byte> set)) set.Clear();
        else PlayersBySlot[slot] = [];

        // R1: LastMeetingEndNum は slot キー (playerId キーではない) なので、per-player の
        // Add/Remove サイクルでは自然に片付かない。MeetingStates.MeetingNum は新しいラウンドの
        // 開始時に 0 へリセットされる (Patches/OnGameStartedPatch.cs) ため、ここで前ラウンドの
        // 値を残すと「前ラウンドの最終値と偶然一致する会議番号」で on_meeting_end が誤って
        // 重複排除されてしまう (ラウンド境界の取りこぼし)。Init() (=ResetSlot) はラウンド毎に
        // 必ず1回呼ばれるので、ここで確実に破棄する。
        LastMeetingEndNum.Remove(slot);

        // v1.3: crowd-control (drag/field) は EKR 全体の static シングルトン。新ラウンド開始の主経路
        // (OnGameStartedPatch の PlayerStates 差し替え) は Role.Remove() を呼ばないため、前ラウンド稼働中の
        // まま持ち越すと HolderId が新ラウンドの別人として解決されうる (監査指摘 2026-08-11 — EndAt 経過で
        // 自己回収はするが、ここで確実に断つ)。実体 CNO はゲーム終了時の CNO 一斉破棄で片付いているので
        // Despawn は呼ばず参照だけ捨てる。
        //
        // ⚠ ただし ResetSlot は Init() 経由で「ゲーム中いつでも」呼ばれうる (GameState.SetMainRole の
        // `if (!role.RoleExist(true)) Role.Init();` — 役職変更持ち役職が未使用スロットへ再配役したとき)。
        // 無条件クリアだと無関係スロットの稼働中 field を参照ごと捨てて孤児 CNO 化させ、≤10 上限が
        // 静かに破れる (監査指摘 2026-08-11)。帰属するときだけ断つ — TeardownRuntime の HolderId/CtxId
        // チェックと同じ非対称の解消。ラウンド境界では前ラウンドの保持者が set に残っているので通る。
        if (ccShouldClear)
        {
            _cc = null;
            _ccPendingDespawn.Clear();
            _lastCcTickTime = -1f;
        }
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
        {
            for (int i = 0; i < state.CnoSlots.Length; i++)
                if (state.CnoSlots[i] != null) count++;

            // v1.2: ポータル実体も「EKR 全体 ≤10 体」の導出カウントに含む (spec §5 P6 整合)。
            for (int i = 0; i < state.Portals.Length; i++)
                if (state.Portals[i] != null) count++;
        }

        // v1.3: field の実体も「EKR 全体 ≤10 体」の導出カウントに含む (spec §5 P6 整合)。crowd-control は
        // EKR 全体で同時1本だが、遅延 Despawn 待ちは 1 秒窓の間に複数重なりうる (下記 _ccPendingDespawn の
        // コメント参照)。遅延待ちの実体も数える (過小カウント側に振れると ≤10 上限が実質破れるため)。
        if (_cc?.FieldCno != null) count++;
        count += _ccPendingDespawn.Count;

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
        // v1.3 (spec §5 crowd-control エンジン): ホルダー/ctx いずれかの死亡・切断・役職剥奪でも即解除。
        if (_cc != null && (_cc.HolderId == playerId || _cc.CtxId == playerId)) StopCrowdControl();

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
            IEkrSlotCno cno = state.CnoSlots[i];
            if (cno == null) continue;
            state.CnoSlots[i] = null;

            // spec §5 孤児コルーチン防止裁定: 実体化前 (playerControl 未生成) は Despawn を呼んでも
            // 基底 spawn コルーチンは止まらず、いずれ勝手に実体化して追跡外のまま居座る。実体化を
            // 待って遅延 Despawn を再試行する。
            if (cno.IsInstantiated) cno.Despawn();
            else RetryDespawnUninstantiated(cno, retriesLeft: 5);
        }

        // v1.2 (spec §3): 役職剥奪 (Teardown) で両側消滅。CnoSlots と同じ孤児コルーチン防止裁定に従う。
        for (int i = 0; i < state.Portals.Length; i++)
        {
            IEkrSlotCno portal = state.Portals[i];
            if (portal == null) continue;
            state.Portals[i] = null;

            if (portal.IsInstantiated) portal.Despawn();
            else RetryDespawnUninstantiated(portal, retriesLeft: 5);
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
    // いないので新しい持ち主との衝突を考える必要はない (speed のケースと異なる点)。EkrCno/EkrDummyCno の
    // どちらでも呼ばれる (TeardownRuntime のテキスト CNO 回収と DespawnDummySlots のダミー回収が共用)。
    private static void RetryDespawnUninstantiated(IEkrSlotCno cno, int retriesLeft)
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

    // 上限チェック (CanOccupyCnoSlot) を先に済ませたあとにだけ呼ぶこと。EkrCno/EkrDummyCno どちらも渡せる。
    internal static void OccupyCnoSlot(EkrHolderState state, int slotIndex1Based, IEkrSlotCno cno)
    {
        state.CnoSlots[slotIndex1Based - 1] = cno;
    }

    // cno_despawn opcode から直接呼ばれる他、cno_spawn/dummy_spawn の「同一 slot への再 spawn」でも
    // 「消してから作る」の消す側として使われる (v1.1: dummy_spawn の slot は cno_spawn と共有)。
    // 実体化前 (playerControl 未生成) の CNO は spec §5 の孤児コルーチン防止裁定によりドロップ (no-op) する
    // — slot は占有されたまま維持される (「まだ出ていないものは変えられない」)。
    // cno_spawn/dummy_spawn 側は「既存占有者が未実体化なら release を試みる前に spawn ごと諦める」を別途行う
    // (EkrLogicOpcodes.CnoSpawn/DummySpawn 参照 — ここで release が no-op になっただけでは新規 occupy を防げない)。
    internal static void ReleaseCnoSlot(EkrHolderState state, int slotIndex1Based)
    {
        int idx = slotIndex1Based - 1;
        IEkrSlotCno existing = state.CnoSlots[idx];
        if (existing == null) return;
        if (!existing.IsInstantiated) return;

        state.CnoSlots[idx] = null;
        state.TouchLatched[idx].Clear();
        state.TouchLastFireTime[idx].Clear();
        existing.Despawn();
    }

    // ── v1.2: ポータル (portal_place) の専用 2 枠アクセサ (idx: 0=a, 1=b) ─────────────────────
    // CnoSlots と同じ「実体化前は release しない」規約 (spec §5 孤児コルーチン防止裁定)。呼び出し元
    // (EkrLogicOpcodes.PortalPlace) が cno_spawn と同じ順序 (existing 未実体化なら諦める→上限チェック→
    // release→occupy) で使う。

    internal static void OccupyPortalSlot(EkrHolderState state, int idx, IEkrSlotCno portal)
    {
        state.Portals[idx] = portal;
    }

    internal static void ReleasePortalSlot(EkrHolderState state, int idx)
    {
        IEkrSlotCno existing = state.Portals[idx];
        if (existing == null) return;
        if (!existing.IsInstantiated) return;

        state.Portals[idx] = null;
        state.PortalLatched[idx].Clear();
        existing.Despawn();
    }

    // EkrDummyCno.OnKilled から呼ばれる (spec §5) — ペット/キルボタン経由の撃破は EkrLogicOpcodes を
    // 経由しないため、Despawn 済み実体を slot 台帳から外す専用の入口が要る。ここで外さないと
    // 全体≤10体の導出カウント (CountLiveCno) が永久に埋まる。テキスト CNO (cno_despawn 等) は呼び出し元
    // (EkrLogicOpcodes) が既に ReleaseCnoSlot/OccupyCnoSlot で台帳を直接操作しているため、この経路が
    // 要るのはロジック外の要因 (ダミー撃破) で CNO が消えるケースだけ。
    internal static void NotifyCnoGone(CustomNetObject cno)
    {
        foreach (EkrHolderState state in Runtime.Values)
        {
            for (int i = 0; i < state.CnoSlots.Length; i++)
            {
                if (!ReferenceEquals(state.CnoSlots[i], cno)) continue;
                state.CnoSlots[i] = null;
                return; // 1つの CNO は同時に1つの slot にしか居ない
            }

            // v1.2: ポータルは撃破不可 (IKillableDummy 非実装) だが、将来の呼び出し元追加に備えて対称に扱う。
            for (int i = 0; i < state.Portals.Length; i++)
            {
                if (!ReferenceEquals(state.Portals[i], cno)) continue;
                state.Portals[i] = null;
                return;
            }
        }
    }

    // ── R1: イベント発火 (RoleBase フック → EkmTemplateRole の薄い呼び出し先) ──────

    // requiredSlot: on_cno_touch (v1.2) 専用のフィルタ (rule.Slot と一致するものだけ発火)。他イベントは null。
    private static void FireEvent(CustomRoles slot, byte holderId, string eventName, byte ctxId, int? requiredSlot = null)
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
            if (requiredSlot.HasValue && rule.Slot != requiredSlot.Value) continue;
            if (state.Fibers.Count >= EkmLogicRuntime.MaxFibersPerHolder) continue; // spec §5: 超過は新規発火をドロップ

            var context = new EkrActionContext { HolderId = holderId, CtxId = ctxId, Slot = slot };
            state.Fibers.Add(EkmLogicRuntime.Spawn(rule.Do, state.Variables, context, EkrActionSink.InOpcodeKill));
        }
    }

    public static void FirePet(CustomRoles slot, PlayerControl pc) => FireEvent(slot, pc.PlayerId, "on_pet", byte.MaxValue);

    // v1.2 (spec §2 on_cno_touch): slotNumber1Based は接触した「自分の CNO/ダミー」の slot (1..3)。
    // ctx = 触れた人。呼び出し元は PollCnoTouchIfDue (0.25秒ポーリングエンジン) のみ。
    public static void FireCnoTouch(CustomRoles slot, byte holderId, int slotNumber1Based, byte toucherId) =>
        FireEvent(slot, holderId, "on_cno_touch", toucherId, slotNumber1Based);

    public static void FireKill(CustomRoles slot, PlayerControl killer, PlayerControl victim) =>
        FireEvent(slot, killer.PlayerId, "on_kill", victim ? victim.PlayerId : byte.MaxValue);

    public static void FireTaskComplete(CustomRoles slot, PlayerControl pc) => FireEvent(slot, pc.PlayerId, "on_task_complete", byte.MaxValue);

    public static void FireVentEnter(CustomRoles slot, PlayerControl pc) => FireEvent(slot, pc.PlayerId, "on_vent_enter", byte.MaxValue);

    // target の死亡確定時 (spec: 自分が死んだとき・ctx=キルした人 [いれば])。Utils.AfterPlayerDeathTasks から
    // 呼ぶ想定 — target 自身が EKR ホルダーかどうかは呼び出し前提を置かず、ここで判定する。
    public static void FireDeath(PlayerControl target, PlayerControl killer)
    {
        if (!target) return;

        // v1.3 (spec §5 crowd-control エンジン): ホルダー/ctx いずれかの死亡でも即解除。target が EKR
        // ホルダーかどうかに関わらず判定する (drag の ctx は EKR ホルダーである必要が無いため — 「相手」は
        // 任意の生存プレイヤー)。
        if (_cc != null && (_cc.HolderId == target.PlayerId || _cc.CtxId == target.PlayerId)) StopCrowdControl();

        CustomRoles slot = target.GetCustomRole();
        if (!IsSlot(slot)) return;

        // spec §2 死亡時の意味論 (2026-08-09 監査裁定): 死亡で走行中 fiber を全キャンセル → その後
        // on_death を発火する (この fiber だけは死後も実行可 — 「死んだら爆発」演出のため)。FireEvent
        // 側の「on_death 以外は死後発火しない」ゲートとセットで、以後この保持者は on_death 起点の
        // fiber しか持たなくなる。
        if (Runtime.TryGetValue(target.PlayerId, out EkrHolderState state))
        {
            state.Fibers.Clear();

            // v1.2 (spec §3): 「ホルダー死亡/役職剥奪で両側消滅」。役職剥奪側は TeardownRuntime が担当、
            // こちらはホルダーが役職を保持したまま死亡する経路 (Teardown を経ない) の唯一の消滅点。
            // fiber キャンセルとは無関係 (on_death 起点 fiber の実行可否は EkrActionSink 側の判定であり、
            // ポータル消滅とは別規約 — cno_*/dummy_spawn/corpse_spawn は on_death からも実行できるが、
            // ポータルという「既に置かれた実体」は死亡と同時に片付ける)。
            for (int i = 0; i < state.Portals.Length; i++)
            {
                IEkrSlotCno portal = state.Portals[i];
                if (portal == null) continue;
                state.Portals[i] = null;
                state.PortalLatched[i].Clear();

                if (portal.IsInstantiated) portal.Despawn();
                else RetryDespawnUninstantiated(portal, retriesLeft: 5);
            }
        }

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
    // fiber キャンセルは純管理メモリ操作 (Il2Cpp 側へは触らない) なのでここでインラインに行うが、
    // v1.1 のダミー slot 一括掃除 (DespawnDummySlots) は Despawn() が Object.Destroy/RemoveNetObject を
    // 呼ぶため、この関数の呼び出し元 (PlayerControlPatch.AfterReportTasks) が抱える他の PlayerControl
    // 走査と同じ synchronous コールスタックに乗せない — 基底 CNO の OnMeeting() 自体も同じ理由で
    // LateTask 5f 遅延になっている (PlayerControlPatch.cs:1501)。それに倣い 1 秒遅延で呼ぶ
    // (advisor 指摘・2026-08-09。dummy_spawn は会議中 Execute() の IsMeeting ゲートで no-op なので、
    // この 1 秒の間に slot を奪われる心配は無い — 台帳が 1 秒長く「占有中」と数えるだけで ≤10 上限は
    // 緩まない方向にしか振れない)。
    public static void FireMeetingStart()
    {
        // v1.1 監査追記 (2026-08-09): 会議開始時点でも dummy_spawn の10秒ゲート起点を前進させる —
        // 「会議開始→追放演出→会議明けスイープ」の全 span を単一の危険窓としてカバーする
        // (EkrActionSink.Execute の ExileController ゲートとの二重防御)。会議明けには
        // FireMeetingEndForSlot が起点を改めて再セットする。
        LastMeetingEndTime = Time.realtimeSinceStartup;

        // v1.3 (spec §3,§5): 会議開始 (追放演出突入含む) で drag/field は即停止・解除 (持ち越しはしない)。
        StopCrowdControl();

        foreach (EkrHolderState state in Runtime.Values)
        {
            state.Fibers.Clear();
            // ポータル warp CD の残留エントリ掃除 (切断者の 3 秒 CD を PlayerId 再利用者が継承しない
            // ように会議境界で毎回捨てる — センサー実体はどのみち会議で消えるので CD 継続の意味がない)。
            state.PortalLastWarpTime.Clear();
        }

        LateTask.New(() =>
        {
            foreach (EkrHolderState state in Runtime.Values) DespawnDummySlots(state);
        }, 1f, "EkrManager.DespawnDummies", log: false);

        foreach (CustomRoles slot in Slots)
        {
            if (!PlayersBySlot.TryGetValue(slot, out HashSet<byte> holders) || holders.Count == 0) continue;

            EkrDefinition def = GetDefinition(slot);
            if (def?.ParsedLogic == null) continue;

            foreach (byte holderId in holders) FireEvent(slot, holderId, "on_meeting_start", byte.MaxValue);
        }
    }

    // v1.1 spec §3「ダミーは会議で消える」の唯一の消滅経路。EkrDummyCno.OnMeeting() は意図的に空 override
    // なので (二重管理防止・EkrDummyCno.cs 参照)、ここが実際に片付ける唯一の場所になる。テキスト CNO
    // (EkrCno) は対象外 — 従来どおり基底 OnMeeting() の会議明け自動復活エンジンに任せる。
    private static void DespawnDummySlots(EkrHolderState state)
    {
        for (int i = 0; i < state.CnoSlots.Length; i++)
        {
            if (state.CnoSlots[i] is not EkrDummyCno dummy) continue;

            if (dummy.IsInstantiated)
            {
                state.CnoSlots[i] = null;
                dummy.Despawn();
            }
            else
            {
                // 実体化前は slot を保持したまま短間隔 (1秒) で回収を再試行する (完成前監査指摘・
                // 2026-08-09)。TeardownRuntime の RetryDespawnUninstantiated (25秒間隔) と違い、この
                // state は会議中も Runtime に生き続けるため、slot を先に null にすると CountLiveCno()
                // が下振れして ≤10 上限が過収容を許し、その CNO は誰にも追跡されないまま会議明けに
                // 出現してしまう (「会議で消える」約束も破れる)。slot を握ったまま数え続ければ上限は
                // 安全側にしか振れない。会議中はプレイヤーには MeetingHud しか見えないため、実体化→
                // 次リトライまでの最大1秒間ワールドに存在しても見た目の約束は破れない。
                RetryDespawnDummySlot(state, i, dummy, retriesLeft: 30);
            }
        }
    }

    // DespawnDummySlots 専用の実体化待ち回収。slot の解放は Despawn が実際に成功した時点で行う。
    // retriesLeft 30 × 1秒 = 既知の最大 spawn 遅延 (~30秒) をカバー。
    private static void RetryDespawnDummySlot(EkrHolderState state, int index, EkrDummyCno dummy, int retriesLeft)
    {
        if (GameStates.IsEnded) return;

        // Teardown (slot を null 化して RetryDespawnUninstantiated へ回す)・撃破 (NotifyCnoGone)・
        // 別の何かが slot を差し替えた場合はそちらの回収に任せて手を引く (二重管理防止)。
        if (state.CnoSlots[index] != dummy) return;

        if (!dummy.IsInstantiated)
        {
            if (retriesLeft <= 0) return; // 通常あり得ない長さの spawn 遅延 — Teardown/Init の全体掃除に任せる
            LateTask.New(() => RetryDespawnDummySlot(state, index, dummy, retriesLeft - 1), 1f, log: false);
            return;
        }

        state.CnoSlots[index] = null;
        dummy.Despawn();
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

    // v1.1: dummy_spawn の「会議明けから10秒間はドロップ」ゲート (spec §5) が読む EKR 全体共通の時刻。
    // 会議開始 (FireMeetingStart) と会議明け (FireMeetingEndForSlot) の両方で前進する。
    // Time.realtimeSinceStartup は起動からの単調増加値。ゲーム境界で reset しない設計 — 理論上の失敗
    // 方向は「前ゲームの最終会議終了から10秒以内に次ゲームの intro が明ける」ときの誤ドロップ (許可漏れ)
    // だが、ロビー→キャラ選択→イントロのオーバーヘッドが常に10秒を大きく超えるため実質到達不能
    // (完成前監査で記述方向を訂正・2026-08-09)。ResetSlot 等でここを触らないこと。
    internal static float LastMeetingEndTime = -1f;

    public static void FireMeetingEndForSlot(CustomRoles slot)
    {
        LastMeetingEndTime = Time.realtimeSinceStartup;

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
        // v1.2: EKR 全体で1本のポーリングエンジン (自己スロットリング — 0.25秒に満たない呼び出しは
        // 内部で即 return する)。ホルダーごとに毎 FixedUpdate 呼ばれる Pump に相乗りさせる (専用の
        // 毎フレーム経路を新しく作らない・spec §5「専用の毎フレーム経路を作らない」)。
        PollCnoTouchIfDue();

        // v1.3: crowd-control (drag/field) の 1.0 秒 tick も同じ相乗り駆動 (自己スロットリング)。
        PumpCrowdControlIfDue();

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

    // v1.1 監査追記 (2026-08-09): CNO を生成/再生成する op (cno_spawn/dummy_spawn/cno_show) の
    // cross-holder レート予算 (spec §5)。per-holder interval と全体 ≤10 体 (在庫の天井) だけでは、
    // on_second のロックステップ (全ホルダーの LastSecondFireTime 初期値が共通 -1f → 同一フレームで
    // 発火し続ける) や lint L9 推奨形 (会議明け wait 10.5) の WakeAt 同刻で、複数ホルダーの spawn が
    // 同一窓に束なるのを止められない。spawn 1体には ReserveFanoutBudget 未課金の付帯送信
    // (spawn broadcast ≈4 nests + player-like は outfit ≈4 nests) がぶら下がるため、DummySpawner の
    // 実績式 (targets+8)/12 秒/体 (BUG-20260803-07 の修正・安全実績域 targets×体数/秒 ≤20 nests/s) を
    // そのまま EKR 全体の最小 spawn 間隔として強制する (TryConsumeGlobalTeleportBudget と同型の鎖)。
    // 超過は静かにドロップ (spec §5 の既存原則 — 作者には per-holder レートと区別が付かないが、
    // cross-holder 干渉は作者に制御不能なので lint では教えない)。
    private static float _lastGlobalCnoSpawnTime = -1f;

    internal static bool TryConsumeGlobalCnoSpawnBudget()
    {
        int fanoutTargets = 0;
        foreach (PlayerControl pc in Main.EnumeratePlayerControls())
            if (!pc.AmOwner) fanoutTargets++;

        float interval = Mathf.Max(0.5f, (fanoutTargets + 8) / 12f);
        float now = Time.realtimeSinceStartup;
        if (_lastGlobalCnoSpawnTime >= 0f && now - _lastGlobalCnoSpawnTime < interval) return false;

        _lastGlobalCnoSpawnTime = now;
        return true;
    }

    // ── v1.2: 接触判定エンジン (spec §2 on_cno_touch / §3,§5 ポータル warp) ──────────────────────
    // 0.25秒ポーリング (毎フレーム判定経路を作らない・spec §5)。進入 0.8u / 退出 1.0u のヒステリシス。
    // Pump() から毎 FixedUpdate 呼ばれるが、この関数自身が 0.25秒に満たない呼び出しを即 return する
    // ことで実質1本のポーリングにする (呼び出し元がホルダー数ぶん重複しても中身は1回しか走らない)。

    private const float TouchPollInterval = 0.25f;
    private const float TouchEnterRadius = 0.8f;
    private const float TouchExitRadius = 1.0f;
    private const float TouchDebounceSeconds = 1f;
    private const float PortalWarpCooldownSeconds = 3f;

    private static float _lastTouchPollTime = -1f;

    private static void PollCnoTouchIfDue()
    {
        float now = Time.realtimeSinceStartup;
        if (_lastTouchPollTime >= 0f && now - _lastTouchPollTime < TouchPollInterval) return;
        _lastTouchPollTime = now;

        // 会議中/ロビーは世界座標が意味を持たない (プレイヤーは MeetingHud/ロビー UI にいる)。
        // spec は on_cno_touch を「タスク中のみ」とは明記していないが、判定対象そのものが存在しない
        // 期間なので実装上の安全ガードとして間引く (誤検出防止・過剰な no-op ループの回避)。
        if (!GameStates.IsInTask) return;

        IReadOnlyList<PlayerControl> livePlayers = Main.AllAlivePlayerControls;
        if (livePlayers.Count == 0) return;

        // fiber 実行が Teardown (Runtime.Remove) を誘発しても列挙を壊さないようスナップショットで回す
        // (PumpMeetingFibers の Fibers.ToArray() と同じ裁定・pitfall 監査指摘)。
        foreach ((byte holderId, EkrHolderState state) in Runtime.ToArray())
        {
            if (state.LogicDisabled) continue;

            CustomRoles? holderSlot = null;

            // ── on_cno_touch: 自分の CNO/ダミー (CnoSlots 1..3) ──
            for (int i = 0; i < state.CnoSlots.Length; i++)
            {
                if (state.CnoSlots[i] is not CustomNetObject cno || !cno.playerControl)
                {
                    state.TouchSensorWasLive[i] = false;
                    continue;
                }

                Vector2 sensorPos = cno.Position;

                // 実体化の立ち上がり (会議明け復活・張り直し) でラッチ/デバウンスを作り直す。裁定は設置時
                // と同じ「その時点で半径内にいる者は発火なしでラッチ済み」(PrimeTouchSensor)。
                if (!state.TouchSensorWasLive[i])
                {
                    state.TouchSensorWasLive[i] = true;
                    PrimeTouchSensor(state, i, sensorPos, false);
                }

                HashSet<byte> latched = state.TouchLatched[i];

                foreach (PlayerControl pc in livePlayers)
                {
                    float dist = Vector2.Distance(pc.Pos(), sensorPos);
                    bool inside = latched.Contains(pc.PlayerId);

                    if (!inside && dist <= TouchEnterRadius)
                    {
                        latched.Add(pc.PlayerId);

                        float lastFire = state.TouchLastFireTime[i].GetValueOrDefault(pc.PlayerId, -1f);
                        if (lastFire >= 0f && now - lastFire < TouchDebounceSeconds) continue;
                        state.TouchLastFireTime[i][pc.PlayerId] = now;

                        holderSlot ??= SlotForHolder(holderId);
                        if (holderSlot.HasValue) FireCnoTouch(holderSlot.Value, holderId, i + 1, pc.PlayerId);
                    }
                    else if (inside && dist >= TouchExitRadius)
                    {
                        latched.Remove(pc.PlayerId);
                    }
                }
            }

            // ── ポータル warp: 両側設置済みのときだけ判定 ──
            // (立ち上がり検出は片側だけ実体化済みの間も行う — 相方が遅れて実体化した瞬間に旧ラッチで
            //  すり抜けないように、warp 判定より先に per-side でプライムしておく)
            for (int side = 0; side < 2; side++)
            {
                if (state.Portals[side] is not CustomNetObject liveCheck || !liveCheck.playerControl)
                {
                    state.PortalSensorWasLive[side] = false;
                }
                else if (!state.PortalSensorWasLive[side])
                {
                    state.PortalSensorWasLive[side] = true;
                    PrimeTouchSensor(state, side, liveCheck.Position, true);
                }
            }

            for (int side = 0; side < 2; side++)
            {
                if (state.Portals[side] is not CustomNetObject sensorCno || !sensorCno.playerControl) continue;
                if (state.Portals[1 - side] is not CustomNetObject otherCno || !otherCno.playerControl) continue;

                Vector2 sensorPos = sensorCno.Position;
                Vector2 destination = otherCno.Position;
                HashSet<byte> latched = state.PortalLatched[side];

                foreach (PlayerControl pc in livePlayers)
                {
                    float dist = Vector2.Distance(pc.Pos(), sensorPos);
                    bool inside = latched.Contains(pc.PlayerId);

                    if (!inside && dist <= TouchEnterRadius)
                    {
                        latched.Add(pc.PlayerId);
                        TryWarpThroughPortal(state, pc, destination);
                    }
                    else if (inside && dist >= TouchExitRadius)
                    {
                        latched.Remove(pc.PlayerId);
                    }
                }
            }
        }
    }

    // slot -> 保持者の逆引き (PlayersBySlot は slot キー)。EKR は Maximum=15・10 slot なので毎回スキャンでも軽い。
    private static CustomRoles? SlotForHolder(byte holderId)
    {
        foreach (CustomRoles slot in Slots)
            if (PlayersBySlot.TryGetValue(slot, out HashSet<byte> holders) && holders.Contains(holderId))
                return slot;

        return null;
    }

    // spec §3: warp の TP は teleport と同じ EKR 全体 ≤2/秒予算を消費。予算枯渇時はその接触は消滅
    // (ラッチ済み扱い・リトライしない — latch は呼び出し前の enter 検出時点で既に立っている)。
    private static void TryWarpThroughPortal(EkrHolderState state, PlayerControl pc, Vector2 destination)
    {
        if (!pc.IsAlive()) return;

        float now = Time.realtimeSinceStartup;
        if (state.PortalLastWarpTime.TryGetValue(pc.PlayerId, out float last) && now - last < PortalWarpCooldownSeconds) return;

        if (!TryConsumeGlobalTeleportBudget()) return;

        state.PortalLastWarpTime[pc.PlayerId] = now;
        Utils.TP(pc.NetTransform, destination, minInterval: 0f);

        PrelatchTouchSensorsNear(pc.PlayerId, destination);
    }

    // v1.2 (spec §2): EKR 起因の TP (teleport/teleport_other/ポータル warp) で移動したプレイヤーは、
    // 着地点で半径内の全接触センサーにラッチ済み扱い — ポータル間 ping-pong 無限ループの構造的回避。
    // teleport/teleport_other (EkrLogicOpcodes) とポータル warp (上記 TryWarpThroughPortal) の3経路から呼ぶ。
    internal static void PrelatchTouchSensorsNear(byte playerId, Vector2 landedPos)
    {
        foreach (EkrHolderState state in Runtime.Values)
        {
            for (int i = 0; i < state.CnoSlots.Length; i++)
            {
                if (state.CnoSlots[i] is not CustomNetObject cno || !cno.playerControl) continue;
                if (Vector2.Distance(cno.Position, landedPos) <= TouchEnterRadius) state.TouchLatched[i].Add(playerId);
            }

            for (int side = 0; side < 2; side++)
            {
                if (state.Portals[side] is not CustomNetObject cno || !cno.playerControl) continue;
                if (Vector2.Distance(cno.Position, landedPos) <= TouchEnterRadius) state.PortalLatched[side].Add(playerId);
            }
        }
    }

    // v1.2 (spec §2): 設置時に半径内へ既にいるプレイヤーはラッチ済み扱い (placer self-grab 既知型の
    // 構造的回避)。cno_spawn/dummy_spawn (idx=CnoSlots index) とportal_place (idx=Portals index) の
    // 両方から呼ぶ (isPortal で対象配列を切り替える)。
    internal static void PrimeTouchSensor(EkrHolderState state, int idx, Vector2 pos, bool isPortal)
    {
        HashSet<byte> latched = isPortal ? state.PortalLatched[idx] : state.TouchLatched[idx];
        latched.Clear();
        if (!isPortal) state.TouchLastFireTime[idx].Clear();

        foreach (PlayerControl pc in Main.AllAlivePlayerControls)
            if (Vector2.Distance(pc.Pos(), pos) <= TouchEnterRadius) latched.Add(pc.PlayerId);
    }

    // ── v1.3: crowd-control エンジン (drag/field の共有枠・spec §3,§5) ──────────────────────────
    // EKR 全体で同時1本 (drag/field 合算)。所有 fiber とは切り離したエンジン側の単一スロットで、
    // 「1.0秒 tick・per-tick TP 上限 (field のみ 5人・ラウンドロビン)・発動あたり TP 総予算
    // (drag≤15/field≤45)」の3点セットを SuperCannonShot.PullTick から移植する。tick の TP は fiber 側
    // teleport の EKR 全体 ≤2/秒予算とは別勘定 (このエンジン自身の3点セットが締める)。

    private sealed class EkrCrowdControlState
    {
        public byte HolderId;
        public byte CtxId = byte.MaxValue; // drag のみ使用。field は対象を毎 tick 半径で都度決めるので不要。
        public bool IsField;
        public float EndAt;
        public int Spent;
        public int Budget;
        public int Rotation; // field のみ: ラウンドロビン公平化 (PullTick と同型)

        // field のみ
        public IEkrSlotCno FieldCno;
        public float Radius;
        public float PullDistance;
    }

    private static EkrCrowdControlState _cc;

    // StopCrowdControl の遅延 Despawn 待ちの field 実体。CountLiveCno はこれも数える —
    // 「実在するのに数えない」過小カウント側 (≤10 上限にとって危険側) に振れないための参照保持
    // (DespawnDummySlots の pending 台帳保持と同じ裁定)。
    //
    // ⚠ 単一スロットではなくリスト。crowd-control 自体は同時1本だが、遅延窓 (1秒) の中で
    // 「A 停止 → B 起動 → B 停止」が連鎖しうる (CanOccupyCnoSlot は pending も数えるので B の spawn は通る)。
    // 単一 static だと後着の B が A を上書きし、A が二度と Despawn されない孤児 CNO になる
    // (= ≤10 上限が 1 体ずつ静かに狭まる片方向リーク・監査指摘 2026-08-11)。
    private static readonly List<IEkrSlotCno> _ccPendingDespawn = [];

    private const float CcTickInterval = 1f;
    private const float CcDeadzone = 1.6f; // spec §5: 最短ゲート (下回る tick はスキップ・予算不消費)
    private const int CcFieldPerTickCap = 5;
    private const int CcDragBudget = 15;
    private const int CcFieldBudget = 45;

    private static float _lastCcTickTime = -1f;

    // 起動を認める共有 SnapTo 残量の下限。Utils.TP は 80..99 帯でも true を返す (SendOption.None へ降格する
    // だけ) ため、枯渇間際に始めた drag/field は「予算は減るのに客へ確実には届かない」空撃ちになり、加えて
    // 他役職の TP まで枯らす (memory: multiplayer_pull_tp_cap_budget)。EKR field(45) と SuperCannonShot
    // BlackHole(45) が同一ラウンドで加算されるケースの防波堤も兼ねる。
    // ⚠ 判定は「起動時のみ」— 稼働中に閾値へ達しても途中で畳まない。周期 TP を中断すると引き寄せ途中の
    // 位置で止まって効果が意味不明になるうえ、能力と CD は既に消費済みで取り返せない (監査指摘 2026-08-11)。
    // 稼働中の枯渇は Utils.TP 側の 100 到達 (false 返し = 予算不消費) が自然に受け止める。
    private const int CcMaxSnapToPressureToStart = 60;

    internal static bool SnapToBudgetAllowsCrowdControl()
        => GameStates.CurrentServerType != GameStates.ServerType.Vanilla || Utils.NumSnapToCallsThisRound < CcMaxSnapToPressureToStart;

    internal static bool IsCrowdControlActive => _cc != null;

    // 早期ガード (IsCrowdControlActive) と TryStartField 呼び出しの間に他の何かが割り込んで _cc が
    // 埋まった場合の後始末 (単一スレッド実行のこのコードベースでは通常到達しない防御的経路)。
    // 実体化前でも後でも孤児コルーチン防止裁定 (spec §5) に従って回収する。
    internal static void RetryDespawnOrphanFieldCno(IEkrSlotCno cno)
    {
        if (cno.IsInstantiated) cno.Despawn();
        else RetryDespawnUninstantiated(cno, retriesLeft: 5);
    }

    // drag opcode から呼ぶ。稼働中なら静かにドロップ (spec §5「稼働中の新規起動は静かにドロップ」)。
    internal static bool TryStartDrag(byte holderId, byte ctxId, float seconds)
    {
        if (_cc != null) return false;
        if (!SnapToBudgetAllowsCrowdControl()) return false;

        // 前セッションの最終 tick 時刻を持ち越すと 1 発目が最大 1 秒遅れ、seconds:1 の drag/field が
        // 1 tick も打たずに終わる (監査指摘 2026-08-11)。開始直後に 1 発目を打つ。
        _lastCcTickTime = -1f;

        _cc = new EkrCrowdControlState
        {
            HolderId = holderId,
            CtxId = ctxId,
            IsField = false,
            EndAt = Time.realtimeSinceStartup + seconds,
            Budget = CcDragBudget
        };

        return true;
    }

    // field opcode から呼ぶ。fieldCno は呼び出し元 (EkrLogicOpcodes.Field) が CNO 生成系防御3点
    // (TryConsumeGlobalCnoSpawnBudget 課金・会議/追放中 no-op・全体≤10体) を通過させた後に渡す。
    // 稼働中なら静かにドロップ — その場合 fieldCno は呼び出し元が孤児コルーチン防止裁定に従って
    // 回収すること (実体化前なら RetryDespawnUninstantiated 相当・実体化済みなら即 Despawn)。
    internal static bool TryStartField(byte holderId, IEkrSlotCno fieldCno, float radius, float pullDistance, float seconds)
    {
        if (_cc != null) return false;
        if (!SnapToBudgetAllowsCrowdControl()) return false;

        _lastCcTickTime = -1f; // 開始直後に 1 発目を打つ (TryStartDrag と同じ・監査指摘 2026-08-11)

        _cc = new EkrCrowdControlState
        {
            HolderId = holderId,
            IsField = true,
            EndAt = Time.realtimeSinceStartup + seconds,
            Budget = CcFieldBudget,
            FieldCno = fieldCno,
            Radius = radius,
            PullDistance = pullDistance
        };

        return true;
    }

    // 会議開始 (追放演出突入含む)・ホルダー/ctx の死亡切断・持続終了のいずれかから呼ぶ (spec §3,§5)。
    // tick 停止 (_cc = null) は同期で行うが、CNO の実 Despawn は 1 秒遅延 — 呼び出し元の 1 つが
    // FireMeetingStart (= PlayerControlPatch.AfterReportTasks の同期コールスタック) で、そこに
    // Object.Destroy/RemoveNetObject を乗せない規約 (DespawnDummySlots と同じ・上のコメント参照) を
    // 全呼び出し元へ一律適用する (経路ごとに分けると会議経路だけ漏れる — 監査指摘 2026-08-11)。
    private static void StopCrowdControl()
    {
        if (_cc == null) return;

        EkrCrowdControlState cc = _cc;
        _cc = null;

        if (cc.IsField && cc.FieldCno != null)
        {
            // クロージャは static ではなくこのローカルを掴む (static を読み直すと後着の停止で上書きされ、
            // 先着の実体が孤児化する — 上の _ccPendingDespawn のコメント参照)。
            IEkrSlotCno pending = cc.FieldCno;
            _ccPendingDespawn.Add(pending);

            LateTask.New(() =>
            {
                _ccPendingDespawn.Remove(pending);

                // spec §5: 実体化前に持続終了した pending は遅延 Despawn で回収 (孤児コルーチン既知型・
                // TeardownRuntime の CnoSlots 回収と同じ裁定)。retry へ渡した後は既存の受容残差
                // (「teardown-while-pending の孤児1体は10体カウント外」) と同じ扱い。
                if (pending.IsInstantiated) pending.Despawn();
                else RetryDespawnUninstantiated(pending, retriesLeft: 5);
            }, 1f, "EKR-CC-FieldDespawn");
        }
    }

    // Pump() から毎 FixedUpdate 相乗りで呼ばれる (自己スロットリング — spec §5「専用の毎フレーム経路を
    // 作らない」)。会議中/追放演出中は即停止 (FireMeetingStart の明示停止と二重防御・タイミング競合対策)。
    private static void PumpCrowdControlIfDue()
    {
        if (_cc == null) return;

        // 同ファイルの他の遅延処理 (RetryDespawnUninstantiated / RetryRestoreSpeed) と同じ規約 —
        // 勝利演出中に TP tick を続けない (監査指摘 2026-08-11)。
        if (GameStates.IsEnded)
        {
            StopCrowdControl();
            return;
        }

        if (GameStates.IsMeeting || ExileController.Instance)
        {
            StopCrowdControl();
            return;
        }

        float now = Time.realtimeSinceStartup;

        if (now >= _cc.EndAt)
        {
            StopCrowdControl();
            return;
        }

        if (_lastCcTickTime >= 0f && now - _lastCcTickTime < CcTickInterval) return;
        _lastCcTickTime = now;

        PlayerControl holderPc = _cc.HolderId.GetPlayer();
        if (!holderPc || !holderPc.IsAlive())
        {
            StopCrowdControl();
            return;
        }

        if (_cc.IsField) TickField(_cc);
        else TickDrag(_cc, holderPc);
    }

    // drag: 発火時の ctx を毎 tick ホルダーの現在位置へ TP する (Penguin 型・spec §3)。部分移動ではなく
    // 1回で現在位置まで飛ばす — SuperCannonShot.PullTick のような段階的な引き寄せではない点が field との違い。
    private static void TickDrag(EkrCrowdControlState cc, PlayerControl holderPc)
    {
        PlayerControl ctxPc = cc.CtxId.GetPlayer();
        if (!ctxPc || !ctxPc.IsAlive())
        {
            StopCrowdControl();
            return;
        }

        if (cc.Spent >= cc.Budget) return; // 予算超過は静かにドロップ (稼働自体は seconds 経過まで維持)

        Vector2 dest = holderPc.Pos();
        Vector2 from = ctxPc.Pos();
        if (Vector2.Distance(from, dest) < CcDeadzone) return; // 予算不消費 (None降格既知型回避)

        // 壁越えは引かない (TickField / SuperCannonShot.PullTick と同じ裁定 — 壁内へ埋め込むと非モッドが
        // スタックする)。着地点はホルダーの現在位置なので通常は歩ける場所だが、ホルダーが直前に vent や
        // 移動床で飛んだ直後のフレームでは経路が壁を貫きうる。3兄弟で1つだけ防御が欠けていた (監査指摘 2026-08-11)。
        if (PhysicsHelpers.AnythingBetween(from, dest, Constants.ShipOnlyMask, false)) return;

        if (Utils.TP(ctxPc.NetTransform, dest, minInterval: 0f)) // 成功時のみ消費 (spec §5)
        {
            cc.Spent++;
            PrelatchTouchSensorsNear(ctxPc.PlayerId, dest); // spec §3 意味論: drag/field の tick TP にも適用
        }
    }

    // field: 中心 (フィールド実体) の半径内にいる生存プレイヤーを 1.0秒 tick で中心へ部分的に引き寄せる
    // (SuperCannonShot.PullTick 移植・spec §3,§5)。ホルダー自身は対象外。per-tick 上限5人・ラウンドロビン公平化。
    private static void TickField(EkrCrowdControlState cc)
    {
        if (cc.FieldCno is not CustomNetObject fieldCno || !fieldCno.playerControl) return; // 実体化前は何もしない

        Vector2 center = fieldCno.Position;

        var candidates = new List<PlayerControl>();

        // 毎秒ループなので yield 版 (Main.EnumerateAlivePlayerControls) は使わない —
        // ネスト管理 IEnumerator は呼び出し毎に strong GCHandle を残す (memory: nested_managed_enumerator_gchandle_leak)。
        // 同ファイルの PollCnoTouchIfDue / PrimeTouchSensor と同じくキャッシュ済みリストを使う (監査指摘 2026-08-11)。
        foreach (PlayerControl pc in Main.AllAlivePlayerControls)
        {
            if (pc.PlayerId == cc.HolderId) continue; // ホルダー自身は引き寄せ対象外 (spec §3)
            if (Vector2.Distance(pc.Pos(), center) <= cc.Radius) candidates.Add(pc);
        }

        if (candidates.Count == 0) return;

        int pulled = 0;

        for (int i = 0; i < candidates.Count && pulled < CcFieldPerTickCap && cc.Spent < cc.Budget; i++)
        {
            PlayerControl pc = candidates[(cc.Rotation + i) % candidates.Count];

            Vector2 pos = pc.Pos();
            float dist = Vector2.Distance(pos, center);
            if (dist < CcDeadzone) continue; // 予算不消費 (None降格既知型回避)

            // spec §5 の「引き寄せ TP は全段 1.5u 超」は実際に TP する移動量 (step) への保証 —
            // dist ∈ [1.6, 2.3) では min() が dist-0.8 (<1.5) 側を選び None 降格の空撃ちになるため、
            // 下限 1.6u (安全マージン込み) でクランプする。step ≤ dist なのでオーバーシュートはしない
            // (dist=1.6 なら中心ちょうどに着地 → 次 tick は deadzone スキップで収束)。監査指摘 2026-08-11。
            float step = Mathf.Max(Mathf.Min(cc.PullDistance, dist - (CcDeadzone / 2f)), CcDeadzone);
            Vector2 newPos = pos + ((center - pos).normalized * step);

            // 壁越えは引かない (PullTick と同じ裁定 — 壁内へ埋め込むと非モッドがスタックする)
            if (PhysicsHelpers.AnythingBetween(pos, newPos, Constants.ShipOnlyMask, false)) continue;

            if (Utils.TP(pc.NetTransform, newPos, minInterval: 0f)) // 成功時のみ消費 (spec §5)
            {
                cc.Spent++;
                pulled++;
                PrelatchTouchSensorsNear(pc.PlayerId, newPos);
            }
        }

        cc.Rotation = (cc.Rotation + CcFieldPerTickCap) % candidates.Count;
    }
}
