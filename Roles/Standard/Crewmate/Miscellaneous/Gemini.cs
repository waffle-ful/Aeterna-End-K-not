using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Modules;
using UnityEngine;

namespace EndKnot.Roles;

// ============================================================
// Gemini (ジェミニ) — Crewmate/Miscellaneous
//
// コンセプト:
//   設定秒数その場に静止していると、立っていた場所に「自分の分身」が置き去りにされる。
//   分身 (GeminiDummy) は本人と同じ色・帽子・スキン・バイザーで、頭上には本人の名前が出る。
//   遠目には本人が突っ立っているようにしか見えないので、追跡してきた相手をやり過ごせる。
//
// 分身の正体は player-like CNO (CustomNetObject.IsPlayerLike)。
//   ⚠️ 非モッド客に body が見えるのは AU 2026 が MurderPlayer FailedError の隠蔽トリックを
//      潰した「回帰」に依存している (他の CNO では赤い人型が透けて見えるという不具合だが、
//      この役職ではそれがそのまま仕様になる)。Innersloth が回帰を直すとこの役職は見えなくなる。
//
// 静止クロックはマグロ (Roles/Standard/Neutral/Benign/Maguro.cs) と同一。
//   座標の変化だけで静止を判定すると、サボタージュ修理中など「操作しているのに動いていない」
//   時間を静止と誤検知する。マグロが持つ「一度でも動き出すまでは判定を始めない (MoveStarted)」+
//   「ラウンド開始直後は GraceTime だけ免除する」の二段構えをそのまま流用する。
//
// 置きっぱなし防止 (ラッチ):
//   静止して置いた直後もその人はまだ静止しているので、放っておくと同じ場所に無限に湧く。
//   置いた座標から RePlaceRange まで離れて初めて次の1体が置けるようにする
//   (MapExtender のポータル往復対策・PortalButton の TriggerLatched と同じ形)。
//
// 同時設置数には必ず上限を設ける。3秒間隔で無制限に置けると 50〜100 体の激重に直行する
// (TOHP DummySpawner が実用不能だった理由そのもの)。上限に達したら最古から消す。
// ============================================================
public class Gemini : RoleBase
{
    private const int Id = 705900;

    // 置いた分身からこれだけ離れて初めて次の1体を置ける
    private const float RePlaceRange = 2f;

    public static bool On;

    private static OptionItem StillTimeLimit;
    private static OptionItem GraceTime;
    private static OptionItem MaxDummies;

    // 分身は「その人の分身」なので保持者ごとに持つ (共有ワールドオブジェクトではない)。
    // 追加順に並べ、上限に達したら先頭 (最古) から消す。
    private readonly List<GeminiDummy> Dummies = [];

    // マグロと同一の静止クロック
    private float StillTimer;
    private float GraceTimer;
    private Vector2 LastPosition;
    private bool IsMoving;
    private bool MoveStarted;

    // 直近で分身を置いた座標。ここから RePlaceRange 離れるまで次を置かない
    private Vector2? LastPlacedPos;

    // このインスタンスの持ち主。GetSuffix で必須 — Utils.BuildSuffix (Modules/Utils.cs:3119-3121) は
    // 全プレイヤーの役職インスタンスを舐めて GetSuffix を呼ぶので、seer==target を見るだけでは
    // 「誰が自分の名前を見ても全ジェミニのカウンターが並ぶ」ことになる (Safecracker.cs:389 が正典の形)。
    private byte GeminiId = byte.MaxValue;

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        StartSetup(Id)
            .AutoSetupOption(ref StillTimeLimit, 3f, new FloatValueRule(3f, 20f, 1f), OptionFormat.Seconds)
            .AutoSetupOption(ref GraceTime, 5f, new FloatValueRule(0f, 60f, 1f), OptionFormat.Seconds)
            .AutoSetupOption(ref MaxDummies, 3, new IntegerValueRule(1, 10, 1), OptionFormat.Times);
    }

    public override void Init()
    {
        On = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
        GeminiId = playerId;
        Dummies.Clear();
        ResetStillClock();

        // 静止し続けるのが能力なので AFK 検知から外す (静止3秒×ラッチ待ちで簡単に
        // 10秒静止を超え、AFK 警告カウントダウン+毎秒 NotifyRoles を浴びる)。
        AFKDetector.ExemptedPlayers.Add(playerId);
    }

    // 切断・役職剥奪で抜けたら自分の分身は全部片付ける (誰も回収できない CNO を残さない)
    public override void Remove(byte playerId)
    {
        AFKDetector.ExemptedPlayers.Remove(playerId);
        DespawnAll();
    }

    private void ResetStillClock()
    {
        StillTimer = 0f;
        GraceTimer = GraceTime.GetFloat();
        IsMoving = true;
        MoveStarted = false;
        LastPlacedPos = null;
    }

    private void DespawnAll()
    {
        Dummies.ToArray().Do(d => d?.Despawn());
        Dummies.Clear();
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!Main.IntroDestroyed || !GameStates.InGame || GameStates.IsMeeting || ExileController.Instance || AntiBlackout.SkipTasks || !pc.IsAlive()) return;

        Vector2 currentPosition = pc.Pos();

        if (GraceTimer > 0f)
        {
            GraceTimer -= Time.fixedDeltaTime;
            LastPosition = currentPosition;
            return;
        }

        bool isCurrentlyMoving = Vector2.Distance(LastPosition, currentPosition) > 0.0001f;

        if (!MoveStarted && isCurrentlyMoving) MoveStarted = true;

        if (!MoveStarted)
        {
            LastPosition = currentPosition;
            return;
        }

        // 置いた場所から十分離れたらラッチを解く (次の1体が置けるようになる)
        if (LastPlacedPos != null && !FastVector2.DistanceWithinRange(LastPlacedPos.Value, currentPosition, RePlaceRange))
            LastPlacedPos = null;

        if (isCurrentlyMoving != IsMoving)
        {
            if (isCurrentlyMoving) StillTimer = 0f;
            IsMoving = isCurrentlyMoving;
        }

        if (!IsMoving)
        {
            StillTimer += Time.fixedDeltaTime;

            if (StillTimer >= StillTimeLimit.GetFloat())
            {
                // 置けても置けなくてもタイマーは畳む。ラッチ中は「離れるまで」何度でもここへ来るが、
                // 実際に生えるのは離れたあとの1回だけ。
                StillTimer = 0f;
                PlaceDummy(pc, currentPosition);
            }
        }

        LastPosition = currentPosition;
    }

    private void PlaceDummy(PlayerControl pc, Vector2 pos)
    {
        if (LastPlacedPos != null) return;

        // 会議 Despawn や試合終了で死んだ CNO の残骸を先に掃除してから数える
        Dummies.RemoveAll(d => d == null || !d.playerControl);

        int max = MaxDummies.GetInt();

        while (Dummies.Count >= max && Dummies.Count > 0)
        {
            GeminiDummy oldest = Dummies[0];
            Dummies.RemoveAt(0);
            oldest?.Despawn();
        }

        Dummies.Add(new GeminiDummy(pos, pc));
        LastPlacedPos = pos;

        pc.Notify(Translator.GetString("Gemini.DummyPlaced"));
        Logger.Info($"{pc.GetNameWithRole().RemoveHtmlTags()} が {pos} に分身を設置 (現在 {Dummies.Count}/{max} 体)", "Gemini");
    }

    // インポスターが分身に切りかかったときの受け口 (Patches/PlayerControlPatch.cs の CheckMurder から呼ばれる)。
    //
    // 非モッド客のローカル AllPlayerControls には CNO が残る (ホスト側でしか除去していない) ため、
    // 客のキルボタンは分身に普通に反応して CmdCheckMurder を投げてくる。ホストはそれを
    // 「CNO 宛て = 無効」として捨てるだけだったので完全な空振りになり、切った側に
    // 「反応しない = 偽物だ」と即座に見抜かれていた。分身にキルを吸収させて本人を逃がす。
    public static bool TryAbsorbKill(PlayerControl killer, PlayerControl target)
    {
        // 本来キルできない相手が改造クライアントから直接 CmdCheckMurder を投げて
        // 分身を壊せてしまわないよう、ホスト側でキル権限まで確認する (バニラのキルボタンは
        // インポスター基底でしか出ないので、正規の客はこの条件を必ず満たしている)。
        if (!On || !killer || !target || !killer.IsAlive() || !killer.CanUseKillButton()) return false;

        GeminiDummy dummy = CustomNetObject.AllObjects.OfType<GeminiDummy>().FirstOrDefault(d => d.playerControl == target);
        if (dummy == null) return false;

        // リストからの除去は要らない (PlaceDummy 側の掃除が Despawn 済みを弾く)。
        dummy.Despawn();

        // 手応えは「目の前で分身が消える」+ キルクールが回ること。全体 KillFlash は撃たない —
        // 誰にも気づかせずジェミニが逃げ切るための能力なので、全員への合図は仕様に反する。
        killer.SetKillCooldown();

        PlayerControl owner = Utils.GetPlayerById(dummy.OwnerId);
        if (owner && owner.IsAlive()) owner.Notify(Translator.GetString("Gemini.DummyDestroyed"));

        Logger.Info($"{killer.GetNameWithRole().RemoveHtmlTags()} がジェミニの分身を破壊した", "Gemini");
        return true;
    }

    public override void OnReportDeadBody()
    {
        // 分身の CNO は GeminiDummy.OnMeeting で自分から消える。こちらは参照だけ捨てる
        // (基底の会議後 自動再生成は使わない — 会議のたびに分身が復活すると際限なく増える)。
        Dummies.Clear();
    }

    public override void AfterMeetingTasks()
    {
        Dummies.Clear();
        ResetStillClock();
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (meeting) return string.Empty;
        // seer がこのインスタンスの持ち主本人であることまで確認する (BuildSuffix は全インスタンスを舐めるため)
        if (seer.PlayerId != GeminiId || seer.PlayerId != target.PlayerId) return string.Empty;

        // 表示経路では状態を書き換えない (掃除は PlaceDummy 側で行う)
        int alive = Dummies.Count(d => d != null && d.playerControl);
        return Utils.ColorString(Color.cyan, $" ({alive}/{MaxDummies.GetInt()})");
    }
}

// ジェミニが置く「自分の分身」。本人と同じ外見・同じ名前の player-like CNO。
// 位置は固定なので OnFixedUpdate は空 override する (基底は毎 fixed update で
// 全 CNO に RpcSnapTo を撒くため、動かないものは黙らせておく)。
internal sealed class GeminiDummy : CustomNetObject
{
    private readonly int ColorId;
    private readonly string SkinId;
    private readonly string HatId;
    private readonly string VisorId;
    private readonly string PetId;
    private readonly string DummyName;

    // キルを吸収したときに持ち主へ通知するために持つ
    internal readonly byte OwnerId;

    protected override bool IsPlayerLike => true;

    public GeminiDummy(Vector2 position, PlayerControl owner)
    {
        OwnerId = owner.PlayerId;

        try
        {
            NetworkedPlayerInfo.PlayerOutfit outfit = owner.Data.Outfits[PlayerOutfitType.Default];
            ColorId = outfit.ColorId;
            SkinId = outfit.SkinId;
            HatId = outfit.HatId;
            VisorId = outfit.VisorId;
            PetId = outfit.PetId;
        }
        catch (Exception e) { Utils.ThrowException(e); }

        DummyName = owner.GetRealName();

        CreateNetObject(string.Empty, position);
    }

    protected override void OnAfterCreate()
    {
        if (!playerControl) return;

        // ホスト側 body 描画: SetColors + Color.white は ApplyOutfitToCNO (Shapeshift trick) より前に置く
        // (DummySpawner.RandomDummy と同じ順序)。ホスト画面での renderer 有効化は基底の
        // EnsureHostVisible が Shapeshift 後に行う。
        try
        {
            SpriteRenderer bodySprite = playerControl.cosmetics.currentBodySprite.BodySprite;
            PlayerMaterial.SetColors(ColorId, bodySprite);
            bodySprite.color = Color.white;
        }
        catch (Exception e) { Utils.ThrowException(e); }

        // 空文字列だと Among Us 側で「プレイヤー非表示」扱いされた前例があるので最低限の代替を入れる
        string shownName = string.IsNullOrEmpty(DummyName) ? " " : DummyName;

        // 非モッドへの outfit + 名前の同期。名前は必ずここで渡す — RpcSetCnoName は
        // ホスト画面の nameText を書くだけで客には届かず、渡さないと全分身がホストの名前になる。
        DummySpawner.ApplyOutfitToCNO(playerControl, ColorId, SkinId, HatId, VisorId, PetId, shownName);

        // ホスト画面側の名札
        RpcSetCnoName(shownName);
    }

    protected override void OnFixedUpdate() { }

    public override void OnMeeting()
    {
        Despawn();
    }
}
