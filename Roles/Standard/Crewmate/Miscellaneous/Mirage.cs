using System;
using AmongUs.GameOptions;
using EndKnot.Modules;
using UnityEngine;

namespace EndKnot.Roles;

// ============================================================
// Mirage (ミラージュ) — Crewmate/Miscellaneous
//
// doc の「ドッペルゲンガー」。既存 Neutral 役職 `Doppelganger` と名前が衝突するため改名した
// (仮採用・承認待ち。候補は docs/hyperdifficult-roles-resume.md の「承認待ち」節)。
//
// ファントムボタンの3段階サイクル:
//   ① 何も無い状態で押す      → その場に「行き先」を刻む
//   ② 行き先がある状態で押す  → 自分の姿の分身を足元に出し、行き先まで歩かせる
//   ③ 分身がいる状態で押す    → 分身と位置を入れ替える (分身は消えない)
// ベントに入る / ペットを撫でる で分身と行き先をまとめて解除する (= タスクに専念できる状態へ戻る)。
//
// 基底は Wizard と同型の **desync Phantom** (Crewmate なので GetDYRole => RoleTypes.Phantom)。
// 本人だけがファントムボタンを持ち、他人からは普通のクルーに見える。
//
// ⚠️ 分身の移動は「系統B」= CNO の毎フレ TP → SnapTo 直送で、Riptide が実機 Hacking キックを
//    食らった経路そのもの。MirageClone は基底の opt-in 間引き `ForceSnapMinInterval => 0.2f` を
//    必ず返すこと。**移動する CNO は OnFixedUpdate を空 override してはいけない**
//    (基底の OnFixedUpdate が SnapTo 送信の本体。固定位置の RandomDummy/GeminiDummy/MimicDummy が
//     空 override しているのは「送信しない」ためで、動かす CNO が真似すると一切動かなくなる)。
// ============================================================
public class Mirage : RoleBase
{
    private const int Id = 707200;

    // これ以上近づいたら「到着」とみなす
    private const float ArriveEpsilon = 0.25f;

    public static bool On;

    private static OptionItem WalkSpeed;
    private static OptionItem AbilityCooldown;
    private static OptionItem MaxDistance;

    private byte MirageId = byte.MaxValue;

    private Vector2? MarkPos;
    private MirageClone Clone;
    private bool CloneArrived;

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        StartSetup(Id)
            .AutoSetupOption(ref AbilityCooldown, 15, new IntegerValueRule(0, 120, 1), OptionFormat.Seconds)
            .AutoSetupOption(ref WalkSpeed, 2.5f, new FloatValueRule(0.5f, 8f, 0.5f), OptionFormat.Multiplier)
            .AutoSetupOption(ref MaxDistance, 30f, new FloatValueRule(5f, 60f, 1f), OptionFormat.None);
    }

    public override void Init()
    {
        On = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
        MirageId = playerId;
        MarkPos = null;
        Clone = null;
        CloneArrived = false;
    }

    public override void Remove(byte playerId)
    {
        ClearAll();
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        AURoleOptions.PhantomCooldown = AbilityCooldown.GetFloat();
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        hud.AbilityButton?.OverrideText(Translator.GetString("MirageAbilityButtonText"));
    }

    public override bool OnVanish(PlayerControl pc)
    {
        if (!pc || !pc.IsAlive() || GameStates.IsMeeting) return false;

        // ③ 分身がいる → 入れ替え。位置は「1回で大きく飛ばす」— 小刻み TP は SnapTo cap を枯渇させ、
        //    他役職の TP まで無音で止める。
        if (Clone is { playerControl: not null })
        {
            Vector2 clonePos = Clone.Position;
            Vector2 selfPos = pc.Pos();

            pc.TP(clonePos);
            Clone.TP(selfPos);

            // 入れ替えたら分身は「その場に留まる」— 行き先はもう消費済み
            MarkPos = null;
            CloneArrived = true;

            pc.Notify(Translator.GetString("MirageSwapped"));
            return false;
        }

        // ① 行き先が無い → その場を刻む
        if (MarkPos == null)
        {
            MarkPos = pc.Pos();
            pc.Notify(Translator.GetString("MirageMarked"));
            return false;
        }

        // ② 行き先がある → 分身を足元に出して歩かせる
        if (Vector2.Distance(pc.Pos(), MarkPos.Value) > MaxDistance.GetFloat())
        {
            pc.Notify(Translator.GetString("MirageTooFar"));
            return false;
        }

        Clone = new MirageClone(pc.Pos(), AtlasVictimSnapshot.CaptureFrom(pc));
        CloneArrived = false;

        pc.Notify(Translator.GetString("MirageCloneSent"));
        return false;
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (Clone == null) return;

        // 実体が消えている (会議で Despawn 等) なら台帳を畳む
        if (!Clone.playerControl)
        {
            Clone = null;
            CloneArrived = false;
            return;
        }

        if (CloneArrived || MarkPos == null) return;
        if (!GameStates.IsInTask || ExileController.Instance || AntiBlackout.SkipTasks) return;

        Vector2 target = MarkPos.Value;
        Vector2 current = Clone.Position;
        float remaining = Vector2.Distance(current, target);

        if (remaining <= ArriveEpsilon)
        {
            Clone.TP(target);
            CloneArrived = true;
            return;
        }

        // 毎フレーム Position を進める。実際の送信本数は基底の ForceSnapMinInterval(0.2s) が間引く
        float step = WalkSpeed.GetFloat() * Time.fixedDeltaTime;
        Clone.TP(current + ((target - current).normalized * Math.Min(step, remaining)));
    }

    // ベントに入ると分身は消える (doc 明記)
    public override void OnEnterVent(PlayerControl pc, Vent vent)
    {
        if (Clone == null && MarkPos == null) return;

        ClearAll();
        pc.Notify(Translator.GetString("MirageDismissed"));
    }

    // ペットを撫でるとタスクモードへ戻る (= 分身と行き先を解除する)
    public override void OnPet(PlayerControl pc)
    {
        if (Clone == null && MarkPos == null) return;

        ClearAll();
        pc.Notify(Translator.GetString("MirageDismissed"));
    }

    public override void OnReportDeadBody()
    {
        ClearAll();
    }

    private void ClearAll()
    {
        Clone?.Despawn();
        Clone = null;
        MarkPos = null;
        CloneArrived = false;
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        // seer==target だけでは足りない — Utils.BuildSuffix は全役職インスタンスを舐めるので、
        // 「持ち主本人のインスタンスか」を MirageId で必ず確認する (Gemini/Safecracker と同じ形)。
        if (seer.PlayerId != MirageId || seer.PlayerId != target.PlayerId || meeting) return string.Empty;

        if (Clone is { playerControl: not null })
            return Translator.GetString(CloneArrived ? "MirageSuffix.Arrived" : "MirageSuffix.Walking");

        return MarkPos == null ? string.Empty : Translator.GetString("MirageSuffix.Marked");
    }
}

// Mirage の分身。MimicDummy と同じ「特定プレイヤーの outfit/名前を載せた player-like CNO」だが、
// ⚠️ **移動するので OnFixedUpdate を override しない** (基底のそれが SnapTo 送信の本体)。
// 代わりに毎フレ TP の強制送信を基底の opt-in 間引きへ委ねる。
internal sealed class MirageClone : CustomNetObject
{
    private readonly AtlasVictimSnapshot Snapshot;

    protected override bool IsPlayerLike => true;

    // 毎フレ TP は ForceSnapSend で base throttle をバイパスし ~50 SnapTo/s を連射する
    // → 公式 anti-cheat の kick 経路 (Riptide と同じ)。base の opt-in 間引きに委譲する。
    protected override float ForceSnapMinInterval => 0.2f;

    public MirageClone(Vector2 position, AtlasVictimSnapshot snapshot)
    {
        Snapshot = snapshot;
        CreateNetObject(string.Empty, position);
    }

    protected override void OnAfterCreate()
    {
        if (!playerControl) return;

        // ホスト側 body 描画: ApplyOutfitToCNO (Shapeshift trick) より前に SetColors + Color.white
        try
        {
            SpriteRenderer bodySprite = playerControl.cosmetics.currentBodySprite.BodySprite;
            PlayerMaterial.SetColors(Snapshot.ColorId, bodySprite);
            bodySprite.color = Color.white;
        }
        catch (Exception e) { Utils.ThrowException(e); }

        string shownName = string.IsNullOrEmpty(Snapshot.Name) ? " " : Snapshot.Name;

        DummySpawner.ApplyOutfitToCNO(playerControl, Snapshot.ColorId, Snapshot.SkinId, Snapshot.HatId, Snapshot.VisorId, Snapshot.PetId, shownName);
        RpcSetCnoName(shownName);
    }

    public override void OnMeeting()
    {
        Despawn();
    }
}
