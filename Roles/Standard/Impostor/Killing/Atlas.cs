using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Modules;
using EndKnot.Patches;
using UnityEngine;

namespace EndKnot.Roles;

// ============================================================
// Atlas (アトラス) — Impostor/Killing
//
// コンセプト:
//   「死はない」。キルした相手はいなくならない — その姿を「ストック」として貯めておき、
//   好きなタイミングで自分の足元にその姿をしたダミー (MimicDummy) を設置できる。
//   ダミーは IKillableDummy を実装し、キル権限を持つ者のキル/ペットで破壊可能。破壊されると
//   偽の死体が残る。
//
// テンプレ:
//   - CNO 本体・ペット撃破の入口・偽死体演出は DummySpawner.cs の RandomDummy (IKillableDummy 実装)
//     から輸入。
//   - 特定プレイヤーの outfit/名前をそのまま載せる CNO の作り方は
//     Roles/Standard/Crewmate/Miscellaneous/Gemini.cs の GeminiDummy から輸入。
//   - 発動トリガ (ペット/Shapeshift/Vanish の三系統) は
//     Roles/Standard/Impostor/Concealing/EvilBusker.cs と同じ形。
//
// per-player 状態を static Dictionary<byte,T> で持つ理由:
//   会議明けの張り直しを DummySpawner.SpawnAllDummies と同じ「保持者をまたいだ通し番号での
//   順送り」で行う必要があるため (公式鯖キック対策)。Gemini/EvilBusker のような
//   per-instance フィールドだと、Atlas を複数人が持ったときに各人が 0 から順送りを始めてしまい、
//   ピーク送信密度が保持者数倍になってしまう。
// ============================================================
public class Atlas : RoleBase
{
    private const int Id = 706700;

    // ダミー 1 体ごとの生成間隔の下限。DummySpawner.SpawnGapSeconds と同じ 0.4 秒
    // (TOHK/TOHP の SpawnQueue 由来)。
    private const float SpawnGapSeconds = 0.4f;

    // ペット経路の能力クールダウン (秒)。ホスト設定オプションには**しない**(仕様上 5 個の
    // オプションで確定済み — KillCooldown/CanVent/ImpostorVision/MaxDummies/MaxStock)。
    // Shapeshift/Vanish 経路は AURoleOptions 側の 5f が自然な床になる (Thanos/Blockade の
    // 固定 5 と同じ床)ので、ペット経路もここに揃える。
    // ⚠️ これは単なる「見た目を揃える」ための値ではない: 0 のまま放置すると
    // Utils.AddAbilityCD の `cd <= 3` ガードで登録自体が無効化され、Main.AbilityCD が
    // 一切セットされずペット経路の連打が完全無制限になる。ApplyOutfitToCNO の
    // Data×2+Shapeshift (≈4 nests/体) は CNO の fan-out 予算 (ReserveFanoutBudget) に
    // 課金されないため (DummySpawner.cs:170-173 参照)、無制限だと MaxStock 分のダミーが
    // 同一フレームで一気に設置されうる — 公式鯖キック要因になる。
    internal const int FixedAbilityCooldown = 5;

    private static List<byte> PlayerIdList = [];

    // キルした相手の姿を「ストック」として貯める。先頭 (index 0) が最古 = 次に使われる。
    private static Dictionary<byte, List<AtlasVictimSnapshot>> Stock = [];

    // 現在設置中のダミー。会議中は各 MimicDummy が自分で Despawn するだけでリストからは外さない —
    // 参照は RespawnAllDummies が個別に (古い死骸を除去 → 新しい実体を追加で) 差し替える。
    // Count は常に「予約枠 (見えている/張り直し待ちを問わない)」として正しく保つ。
    private static Dictionary<byte, List<MimicDummy>> PlacedDummies = [];

    private static int LastRespawnMeeting = -1;

    private static OptionItem KillCooldown;
    private static OptionItem CanVent;
    private static OptionItem ImpostorVision;
    private static OptionItem MaxDummies;
    private static OptionItem MaxStock;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        StartSetup(Id)
            .AutoSetupOption(ref KillCooldown, 30f, new FloatValueRule(0f, 180f, 0.5f), OptionFormat.Seconds)
            .AutoSetupOption(ref CanVent, true)
            .AutoSetupOption(ref ImpostorVision, true)
            .AutoSetupOption(ref MaxDummies, 3, new IntegerValueRule(1, 10, 1), OptionFormat.Times)
            .AutoSetupOption(ref MaxStock, 3, new IntegerValueRule(1, 10, 1), OptionFormat.Times);
    }

    public override void Init()
    {
        PlayerIdList = [];
        Stock = [];
        PlacedDummies = [];
        LastRespawnMeeting = -1;
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        Stock[playerId] = [];
        PlacedDummies[playerId] = [];
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
        if (PlacedDummies.TryGetValue(playerId, out var dummies))
        {
            dummies.ToArray().Do(d => d?.Despawn());
            dummies.Clear();
        }
        PlacedDummies.Remove(playerId);
        Stock.Remove(playerId);
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    public override bool CanUseImpostorVentButton(PlayerControl pc)
    {
        return CanVent.GetBool();
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        opt.SetVision(ImpostorVision.GetBool());

        float cd = FixedAbilityCooldown;
        if (IntroCutsceneDestroyPatch.PreventKill) cd = Mathf.Max(cd, 12f);

        if (Options.UsePhantomBasis.GetBool())
            AURoleOptions.PhantomCooldown = cd;
        else if (!Options.UsePets.GetBool())
        {
            AURoleOptions.ShapeshifterCooldown = cd;
            AURoleOptions.ShapeshifterDuration = 1f;
        }
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        if (Options.UsePets.GetBool() && !Options.UsePhantomBasis.GetBool())
            hud.PetButton?.OverrideText(Translator.GetString("AtlasAbilityButtonText"));
        else
            hud.AbilityButton?.OverrideText(Translator.GetString("AtlasAbilityButtonText"));
    }

    public override void OnPet(PlayerControl pc)
    {
        TryDeployDummy(pc);
    }

    public override bool OnVanish(PlayerControl pc)
    {
        TryDeployDummy(pc);
        return false;
    }

    public override bool OnShapeshift(PlayerControl shapeshifter, PlayerControl target, bool shapeshifting)
    {
        if (!shapeshifting) return true;
        shapeshifter.RpcRejectShapeshift();
        TryDeployDummy(shapeshifter);
        return false;
    }

    private static void TryDeployDummy(PlayerControl pc)
    {
        if (!pc || !pc.IsAlive() || ReportDeadBodyPatch.MeetingStarted || GameStates.IsMeeting) return;

        if (!Stock.TryGetValue(pc.PlayerId, out var stock) || stock.Count == 0)
        {
            pc.Notify(Translator.GetString("AtlasNoStock"));
            return;
        }

        if (!PlacedDummies.TryGetValue(pc.PlayerId, out var dummies)) return;

        if (dummies.Count >= MaxDummies.GetInt())
        {
            pc.Notify(Translator.GetString("AtlasMaxDummies"));
            return;
        }

        AtlasVictimSnapshot snapshot = stock[0];
        stock.RemoveAt(0);

        dummies.Add(new MimicDummy(pc.Pos(), snapshot, pc.PlayerId));

        Logger.Info($"{pc.GetNameWithRole().RemoveHtmlTags()} が Atlas のダミーを設置 (現在 {dummies.Count}/{MaxDummies.GetInt()} 体, 残ストック {stock.Count})", "Atlas");
    }

    public override void OnMurder(PlayerControl killer, PlayerControl target)
    {
        if (!killer.Is(CustomRoles.Atlas) || target == null || target.PlayerId >= 200) return;
        if (!Stock.TryGetValue(killer.PlayerId, out var stock)) return;

        // ストック上限に達している場合は新しい姿を捨てる (先着優先)。「誰の姿を持っているか」は
        // ゲームプレイ上ほぼ等価なので、既にキューにある分を追い出す必要はないと判断した。
        if (stock.Count >= MaxStock.GetInt())
        {
            Logger.Info($"Atlas stock is full, discarding {target.GetNameWithRole().RemoveHtmlTags()}'s appearance", "Atlas");
            return;
        }

        stock.Add(AtlasVictimSnapshot.CaptureFrom(target));
    }

    // ⚠️ OnCheckMurderAsTarget は採用しない。DummySpawner の同名オーバーライドは「キル成立」ではなく
    // 「キル試行 (チェック)」の時点で発火するため、Veteran の反撃やシールド等でブロックされただけの
    // 攻撃でも無条件にダミーが全消しされてしまう (false positive)。Atlas は「死はない」がテーマなので、
    // 本人が本当に死んだ後もダミーは残ってよく、次の会議明けの RespawnAllDummies の生存チェック
    // (下記) が確実に片付ける — 死亡直後の即時クリアより一貫性がある。
    public override void AfterMeetingTasks()
    {
        if (!AmongUsClient.Instance.AmHost) return;

        // Utils.AfterMeetingTasks() は Atlas を持つプレイヤーごとにこのインスタンスメソッドを
        // 呼ぶため、Atlas 複数人所持時は同じ会議内で複数回発火しうる。MeetingNum で重複実行を防ぐ
        // (DummySpawner.AfterMeetingTasks と同型)。
        int currentMeeting = MeetingStates.MeetingNum;
        if (LastRespawnMeeting == currentMeeting) return;
        LastRespawnMeeting = currentMeeting;

        // ⚠️ 遅延は 10 秒から縮めないこと。会議明けは追放スイープ (SetRole 全員分 + Desync +
        // ReactorFlash + NotifyRoles) が task phase 開始後 ~4 秒まで走り、レートゲートのドレインが
        // さらに ~6 秒続く。DummySpawner.AfterMeetingTasks と同じ実測対策値。
        LateTask.New(RespawnAllDummies, 10f, "Atlas.AfterMeeting");
    }

    private static void RespawnAllDummies()
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (GameStates.IsEnded || GameStates.IsMeeting) return;

        // 順送りスロットは Atlas 保持者をまたいで通し番号にする (DummySpawner.SpawnAllDummies と
        // 同型 — 保持者ごとに 0 から振り直すと「全員の i 体目」が同じティックに重なり、
        // ピークの送信密度が保持者数の倍数になる)。
        int slot = 0;
        int fanoutTargets = Main.EnumeratePlayerControls().Count(p => !p.AmOwner);
        float spawnGap = Math.Max(SpawnGapSeconds, (fanoutTargets + 8) / 12f);

        foreach (byte id in PlayerIdList.ToArray())
        {
            if (!PlacedDummies.TryGetValue(id, out var dummies) || dummies.Count == 0) continue;

            var pc = Utils.GetPlayerById(id);
            if (pc == null || !pc.IsAlive())
            {
                // 保持者が死亡/切断済みなら張り直さず片付ける (「死はない」は保持者が生きている間だけ)。
                // ⚠️ 参照を捨てるだけでは実体が world に残る。下と同じ理由で、会議明けの張り直し待ち
                // (10 秒) のあいだに手動設置された「生きたダミー」がここに混ざりうるため、
                // 必ず Despawn してから捨てる (Remove(byte) と同型)。
                dummies.ToArray().Do(d => d?.Despawn());
                dummies.Clear();
                continue;
            }

            byte ownerId = id;

            // ⚠️ ここで dummies を Clear しない。会議中に各 MimicDummy.OnMeeting() が Despawn 済みだが
            // リストへの参照は残したまま個別に差し替える — 一括 Clear すると張り直し待ちの間だけ
            // Count が 0 に見えてしまい、TryDeployDummy の上限チェックを素通りして
            // 張り直し完了時に MaxDummies を超えてしまう (Count は常に「予約枠」として正しく保つ)。
            foreach (MimicDummy old in dummies.ToArray())
            {
                if (old == null) continue;

                // ⚠️ 会議明けの張り直し待ち (10 秒) のあいだも手動設置はできるので、このリストには
                // 「会議で Despawn 済みの予約枠」と「たった今設置された生きたダミー」が混在しうる。
                // 生きている方まで張り直すと、実体を Despawn しないまま参照だけ差し替えることになり、
                // 追跡外のダミーが world に残って MaxDummies が無音で破られる (Count は変わらないのに
                // 実体だけ増える)。生きているものは何もしなくてよい。
                // 生存判定は CustomNetObject.GetKillableTarget と同じ playerControl の fake-null チェック
                // (Despawn が Object.Destroy するため、破棄後は暗黙の bool 変換が false になる)。
                if (old.playerControl) continue;

                Vector2 pos = old.Position;
                AtlasVictimSnapshot snapshot = old.Snapshot;
                int idx = slot++;

                LateTask.New(() =>
                {
                    if (GameStates.IsEnded || GameStates.IsMeeting) return;

                    var owner = Utils.GetPlayerById(ownerId);
                    if (owner == null || !owner.IsAlive()) return; // 死亡していれば次サイクルの Clear に任せる

                    if (!PlacedDummies.TryGetValue(ownerId, out var current)) return;

                    current.Remove(old);
                    current.Add(new MimicDummy(pos, snapshot, ownerId));
                }, idx * spawnGap, "Atlas.Respawn", log: false);
            }
        }
    }

    /// <summary>撃破されたダミーを保持者の台帳から外す (DummySpawner.ForgetDummy と同型)。</summary>
    internal static void ForgetDummy(MimicDummy dummy)
    {
        foreach (List<MimicDummy> list in PlacedDummies.Values)
            list.Remove(dummy);
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        int stockCount = Stock.TryGetValue(playerId, out var s) ? s.Count : 0;
        int placedCount = PlacedDummies.TryGetValue(playerId, out var d) ? d.Count(x => x?.playerControl != null) : 0;

        if (stockCount == 0 && placedCount == 0) return string.Empty;

        return Utils.ColorString(Palette.ImpostorRed, $"(stock:{stockCount} dummy:{placedCount})");
    }
}

/// <summary>キル時点での被害者の外見スナップショット。ダミー設置まで「ストック」として保持する。</summary>
internal readonly struct AtlasVictimSnapshot
{
    internal readonly int ColorId;
    internal readonly string SkinId;
    internal readonly string HatId;
    internal readonly string VisorId;
    internal readonly string PetId;
    internal readonly string Name;

    private AtlasVictimSnapshot(int colorId, string skinId, string hatId, string visorId, string petId, string name)
    {
        ColorId = colorId;
        SkinId = skinId;
        HatId = hatId;
        VisorId = visorId;
        PetId = petId;
        Name = name;
    }

    internal static AtlasVictimSnapshot CaptureFrom(PlayerControl target)
    {
        int colorId = 0;
        string skinId = string.Empty, hatId = string.Empty, visorId = string.Empty, petId = string.Empty;

        try
        {
            NetworkedPlayerInfo.PlayerOutfit outfit = target.Data.Outfits[PlayerOutfitType.Default];
            colorId = outfit.ColorId;
            skinId = outfit.SkinId;
            hatId = outfit.HatId;
            visorId = outfit.VisorId;
            petId = outfit.PetId;
        }
        catch (Exception e) { Utils.ThrowException(e); }

        return new AtlasVictimSnapshot(colorId, skinId, hatId, visorId, petId, target.GetRealName());
    }
}

// Atlas が設置する「被害者そっくりのダミー」。GeminiDummy (特定プレイヤーの outfit/名前を載せる
// player-like CNO の作り方) + RandomDummy (IKillableDummy: ペット/キルボタンで破壊可能、偽死体を
// 残す) のハイブリッド。
internal sealed class MimicDummy : CustomNetObject, IKillableDummy
{
    internal readonly byte OwnerId;
    internal readonly AtlasVictimSnapshot Snapshot;

    protected override bool IsPlayerLike => true;

    public MimicDummy(Vector2 position, AtlasVictimSnapshot snapshot, byte ownerId)
    {
        OwnerId = ownerId;
        Snapshot = snapshot;

        CreateNetObject(string.Empty, position);
    }

    protected override void OnAfterCreate()
    {
        if (!playerControl) return;

        // ホスト側 body 描画: ApplyOutfitToCNO (Shapeshift trick) より前に SetColors + Color.white を
        // 入れる (DummySpawner.RandomDummy / Gemini.GeminiDummy と同じ順序 — 後ろに置くとホスト側で
        // body が描画されない現象を再現確認済み)。
        try
        {
            SpriteRenderer bodySprite = playerControl.cosmetics.currentBodySprite.BodySprite;
            PlayerMaterial.SetColors(Snapshot.ColorId, bodySprite);
            bodySprite.color = Color.white;
        }
        catch (Exception e) { Utils.ThrowException(e); }

        // 空文字列は Among Us 側で「プレイヤー非表示」扱いされた前例があるため最低限の代替を入れる
        string shownName = string.IsNullOrEmpty(Snapshot.Name) ? " " : Snapshot.Name;

        // 非モッドへの outfit + 名前の同期 (Shapeshift trick)
        DummySpawner.ApplyOutfitToCNO(playerControl, Snapshot.ColorId, Snapshot.SkinId, Snapshot.HatId, Snapshot.VisorId, Snapshot.PetId, shownName);

        // ホスト画面側の名札
        RpcSetCnoName(shownName);
    }

    // 固定位置のため OnFixedUpdate は空 override
    protected override void OnFixedUpdate() { }

    public override void OnMeeting() => Despawn();

    // 撹乱が役職の目的なので、クルーが歩いてペットを押すだけで全消しできてはいけない —
    // キル権限を持つ者だけに絞る (RandomDummy.CanBeKilledBy と同型)。
    // ⚠️ Atlas 本人は除外する。ダミーは自分の足元に設置するため、除外しないと設置直後にもう一度
    // ペットを押した際 (次のダミーを置くつもりでも)、PetActionsPatch の
    // killable-dummy チェックが OnPet より先に走り、至近距離の自分のダミーを拾って誤って
    // 破壊してしまう (OnPet は一度も呼ばれない)。
    public bool CanBeKilledBy(PlayerControl killer)
    {
        return killer && killer.IsAlive() && killer.PlayerId != OwnerId && killer.CanUseKillButton();
    }

    public void OnKilled(PlayerControl killer)
    {
        Logger.Info($"{killer.GetNameWithRole().RemoveHtmlTags()} が Atlas のダミーを破壊した", "Atlas");

        Atlas.ForgetDummy(this);
        // 死体はダミーが立っていた場所に、ダミー自身の色で残す (Despawn より先に Position を使う)。
        SpawnDummyCorpse(killer, Position, Snapshot.ColorId);
        Despawn();

        Main.AllAlivePlayerControlsToList.Do(p => p.KillFlash());
        killer.SetKillCooldown();
        Utils.NotifyRoles(SpecifySeer: killer, SpecifyTarget: killer);
    }
}
