using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Modules;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace EndKnot.Roles;

// ============================================================
// PortalButton (ポータルボタン) — Crewmate/Miscellaneous
//
// コンセプト:
//   本物の緊急ボタンの前でペットを押すと「ボタンを持ち上げる」。持ち上げた時点で
//   本物のボタンは試合中ずっと使えなくなり、別の場所でペットを押すとそこに
//   移設されたボタン (PortalButtonMarker = 白縁+赤芯の丸) が置かれる。
//   設置されたボタンの上 (PressRange) でペットを押した生存者が緊急会議を起こす
//   (実体はホストがその人の名義で NoCheckStartMeeting を呼ぶ)。設置済みのボタンの
//   少し手前 (PressRange 外・PickupRange 内) でペットを押すと拾い直せるので、
//   何度でも移設できる (使用回数の続く限り)。
//
// 「押す」は保持者だけでなく全員 (非モッド客を含む) ができなければならないので、
// 役職の OnPet ではなく全プレイヤーのペットが通る PetActionsPatch.OnPetUse から
// IsOnButton / PressByPet を呼ぶ。踏んだだけでは何も起きない (誤爆で会議が多発した
// ため 2026-07-29 に踏む判定を撤去、範囲内では「ペットで会議」の案内だけ出す)。
//
// 本物のボタンを潰す手段は二段構え:
//   ①  ReportDeadBodyPatch の緊急会議分岐でホスト側から拒否する (実体・即時)
//   ②  PlayerGameOptionsSender で EmergencyCooldown を 3600 にする (見た目・SyncButtonMode と同じ手)
//   ラウンド途中のオプション変更が非モッド客のボタン表示へ即反映されるかは検証できない
//   (GameLibs がスタブで vanilla の実装を読めない) ため、遮断の責任は①が持つ。
//   ②が効くまでの間は「パネルは開くが押しても何も起きない」になる。
//
// 会議を跨ぐと CNO は必ず消える (基底の自動再生成は使わない)。張り直しは AfterMeetingTasks
// から LateTask 経由で行う。
// ============================================================
public class PortalButton : RoleBase
{
    private const int Id = 705700;

    private const float PickupRange = 2.5f;       // 本物のボタン / 設置済みボタンを拾える距離
    private const float PressRange = 1.5f;        // ボタンの上でペット = 押した判定。拾える距離より内側であること
    private const float PromptResetRange = 2.5f;  // ここまで離れて初めて案内をもう一度出す
    private const float RespawnDelay = 1.5f;      // AfterMeetingTasks から CNO 生成までの遅延

    public static bool On;

    // ReportDeadBodyPatch / PlayerGameOptionsSender から読む。一度立ったら試合終了まで下ろさない
    // (ボタンは「移動した」のであって、拾い直しても元の場所へは戻らない)。
    public static bool RealButtonDisabled;

    public static OptionItem AbilityCooldown;
    private static OptionItem AbilityUseLimit;
    private static OptionItem HoldTimeLimit;

    // 本物の緊急ボタンの座標。ShipStatus が出来てから初めて引けるので遅延解決してキャッシュする。
    private static bool ButtonPosResolved;
    private static bool ButtonPosValid;
    private static Vector2 RealButtonPos;

    // 同一フレームに複数の判定が通って会議が二重に起きるのを防ぐ。保持者が複数居ても
    // 会議はラウンドに1回しか起きないので static で共有する。
    private static bool MeetingTriggered;

    // 移設されたボタンも「緊急ボタン」なので、本物と同じクールタイムを共有する。
    // ラウンド開始・会議明けからの経過時間で見るため、起点の時刻だけを持つ。
    private static long EmergencyClockStartTS;

    // ---- 移設されたボタンそのもの (static = 保持者全員で共有) ----
    // ゲーム内に緊急ボタンは物理的に1個しか無いので、instance 単位で持ってはいけない。
    // instance 単位にすると、A が本物のボタンを持ち上げた時点で RealButtonDisabled (static) が立ち、
    // B からは「本物のボタン」も「A が置いたボタン」も見えなくなって能力が試合中ずっと死ぬ。
    //
    // 座標は CNO から読まない (CustomNetObject.Position は生成コルーチン内で非同期に代入されるため、
    // 設置直後に読むと (0,0) を返す)。
    private static PortalButtonMarker Marker;
    private static Vector2? MarkerPos;

    // 今ボタンを持ち歩いている人 (居なければ null)。保持者が複数居るとき、他人が持ち歩いている最中に
    // ペットを押した人へ「設置したボタンの上に立て」という的外れな案内を出さないために持つ。
    private static byte? CarrierId;

    // 「ペットで会議発動！」の案内を既に見せた人。一度 PromptResetRange まで離れないと出し直さない
    // (Notify は名前ブロードキャストなので、毎 fixed update の連射は公式鯖で危険)。
    // ボタンを生やした瞬間に近くに居た人は最初から入れておく — スポーン地点に置いた場合、
    // 会議明けに降ってきた全員へ同時に Notify が飛ぶバーストになるため。
    private static readonly HashSet<byte> PromptShown = [];

    // 会議明けの張り直しも共有物に対する1回の処理なので、保持者の人数ぶん走らせない。
    // AfterMeetingTasks は保持者ごとに呼ばれるうえ、同一会議に対して複数回呼ばれる経路もある
    // (AntiBlackout の RevertToActualRoleTypes 系)。MeetingNum で1会議1回に絞る。
    private static int LastRespawnMeetingNum = -1;

    // ---- per-instance (「今どの保持者が持ち歩いているか」だけは個人の状態) ----
    private bool Holding;
    private long HoldStartTS;

    // このインスタンスの持ち主。GetSuffix で必須 — Utils.BuildSuffix (Modules/Utils.cs:3119-3121) は
    // 全プレイヤーの役職インスタンスを舐めて GetSuffix を呼ぶので、seer==target と Holding だけでは
    // 「保持者以外の全員が自分の名前の横に保持者の残り時間を見る」ことになる。
    private byte OwnerId = byte.MaxValue;

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        StartSetup(Id)
            .AutoSetupOption(ref AbilityCooldown, 15, new IntegerValueRule(1, 120, 1), OptionFormat.Seconds)
            .AutoSetupOption(ref AbilityUseLimit, 5f, new FloatValueRule(0f, 20f, 1f), OptionFormat.Times)
            .AutoSetupOption(ref HoldTimeLimit, 30, new IntegerValueRule(5, 120, 1), OptionFormat.Seconds);
    }

    public override void Init()
    {
        On = false;
        RealButtonDisabled = false;
        ButtonPosResolved = false;
        ButtonPosValid = false;
        MeetingTriggered = false;
        PromptShown.Clear();
        EmergencyClockStartTS = Utils.TimeStamp;

        // 共有物の初期化は Init だけで行う (Add は保持者ごとに走るので、2人目の Add で
        // 1人目が置いたボタンを消してしまう)。前のゲームの CNO は基盤側が始末するので参照を捨てるだけ。
        Marker = null;
        MarkerPos = null;
        CarrierId = null;
        LastRespawnMeetingNum = -1;
    }

    public override void Add(byte playerId)
    {
        On = true;
        OwnerId = playerId;
        Holding = false;
        HoldStartTS = 0;
        playerId.SetAbilityUseLimit(AbilityUseLimit.GetFloat());
    }

    // 保持中に切断・役職剥奪で居なくなると、誰も会議を起こせないまま試合が固まる。
    // 持っていたボタンを必ずどこかに落としてから抜けさせる。
    public override void Remove(byte playerId)
    {
        if (!Holding) return;

        Holding = false;
        HoldStartTS = 0;
        CarrierId = null;

        PlayerControl pc = Utils.GetPlayerById(playerId);
        Vector2 dropPos = pc ? pc.Pos() : RealButtonPos;

        // 座標が一つも取れないなら置きようがないので諦める (この場合 ButtonPosValid も false)。
        if (!pc && !ButtonPosValid) return;

        DespawnMarker();
        SpawnMarkerAt(dropPos);
        Logger.Info($"保持者 {playerId} が離脱したため緊急ボタンを {dropPos} に落とした", "PortalButton");
    }

    // 本物の緊急ボタンの座標。ShipStatus.EmergencyButton はマップごとに置かれた SystemConsole。
    // 取れないマップ (未知のマップ / LIMap) では能力全体を無効化する (MapExtender と同じ fail-safe)。
    private static bool TryGetRealButtonPos(out Vector2 pos)
    {
        if (ButtonPosResolved)
        {
            pos = RealButtonPos;
            return ButtonPosValid;
        }

        pos = Vector2.zero;

        ShipStatus ss = ShipStatus.Instance;
        if (!ss) return false; // まだ解決しない (次のフレームで再試行)

        ButtonPosResolved = true;

        try
        {
            SystemConsole console = ss.EmergencyButton;

            if (!console)
            {
                // マップによっては EmergencyButton が張られていない可能性があるので、
                // 緊急会議のミニゲームを持つ SystemConsole を総当たりで探す。
                Il2CppArrayBase<SystemConsole> consoles = Object.FindObjectsOfType<SystemConsole>(true);
                if (consoles != null)
                    console = consoles.FirstOrDefault(x => x && x.MinigamePrefab && x.MinigamePrefab.TryCast<EmergencyMinigame>());
            }

            if (!console)
            {
                Logger.Warn("緊急ボタンの SystemConsole が見つからないため能力を無効化", "PortalButton");
                return false;
            }

            RealButtonPos = console.transform.position;
            ButtonPosValid = true;
            pos = RealButtonPos;
            return true;
        }
        catch (System.Exception e)
        {
            Logger.Error($"緊急ボタンの座標解決に失敗 → 能力を無効化: {e.Message}", "PortalButton");
            ButtonPosValid = false;
            return false;
        }
    }

    public override void OnPet(PlayerControl pc)
    {
        if (!pc.IsAlive() || !GameStates.IsInTask) return;

        if (Holding)
        {
            PlaceMarker(pc, Translator.GetString("PortalButton.Placed"));
            return;
        }

        // ボタンの真上 (PressRange 内) でのペットは PetActionsPatch が「押した」として先に食べるので、
        // ここに来るのは PressRange の外・PickupRange の内 = 少し手前に立っている場合だけ。
        bool fromMarker = MarkerPos != null && FastVector2.DistanceWithinRange(MarkerPos.Value, pc.Pos(), PickupRange);
        bool fromRealButton = !fromMarker && !RealButtonDisabled && TryGetRealButtonPos(out Vector2 buttonPos) && FastVector2.DistanceWithinRange(buttonPos, pc.Pos(), PickupRange);

        if (!fromMarker && !fromRealButton)
        {
            string reason = CarrierId != null ? "PortalButton.SomeoneElseHasIt"
                : RealButtonDisabled ? "PortalButton.NotAtPortal"
                : "PortalButton.NotAtButton";

            pc.Notify(Translator.GetString(reason));
            return;
        }

        if (pc.GetAbilityUseLimit() < 1f)
        {
            pc.Notify(Translator.GetString("PortalButton.NoUsesLeft"));
            return;
        }

        pc.RpcRemoveAbilityUse();

        if (fromMarker) DespawnMarker();

        if (fromRealButton && !RealButtonDisabled)
        {
            RealButtonDisabled = true;
            // 見た目側 (EmergencyCooldown 3600) を反映させる。実体の遮断は ReportDeadBodyPatch が持つ。
            Utils.MarkEveryoneDirtySettings();
            Logger.Info($"{pc.GetNameWithRole().RemoveHtmlTags()} が緊急ボタンを持ち上げた — 本物のボタンを封鎖", "PortalButton");
        }

        Holding = true;
        HoldStartTS = Utils.TimeStamp;
        CarrierId = pc.PlayerId;
        pc.Notify(string.Format(Translator.GetString("PortalButton.PickedUp"), HoldTimeLimit.GetInt()));
    }

    private void PlaceMarker(PlayerControl pc, string message)
    {
        Holding = false;
        HoldStartTS = 0;
        CarrierId = null;

        DespawnMarker();

        SpawnMarkerAt(pc.Pos());

        if (message.Length > 0) pc.Notify(message);
        Logger.Info($"{pc.GetNameWithRole().RemoveHtmlTags()} が緊急ボタンを {MarkerPos.Value} に設置", "PortalButton");
    }

    // ボタンを実際に生やす唯一の経路。設置・落下・会議明けの張り直しで必ずここを通し、
    // 生やした瞬間に近くに立っている人を全員ラッチしておく (会議明けにスポーンへ降ってきた
    // 全員へ案内 Notify が同時に飛ぶバーストを断ち切る)。
    private static void SpawnMarkerAt(Vector2 pos)
    {
        MarkerPos = pos;
        Marker = new PortalButtonMarker(pos);

        PromptShown.Clear();

        foreach (PlayerControl pc in Main.AllAlivePlayerControlsToArray)
        {
            if (pc.PlayerId >= 200) continue;
            if (FastVector2.DistanceWithinRange(pos, pc.Pos(), PromptResetRange))
                PromptShown.Add(pc.PlayerId);
        }
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!Holding || !GameStates.IsInTask) return;

        // 持ったまま死ぬと誰も会議を起こせなくなるので、その場に落とす。
        if (!pc.IsAlive())
        {
            PlaceMarker(pc, string.Empty);
            return;
        }

        if (Utils.TimeStamp - HoldStartTS < HoldTimeLimit.GetInt()) return;

        PlaceMarker(pc, Translator.GetString("PortalButton.AutoDropped"));
    }

    // 踏んだだけでは何も起きない。範囲に入った人へ「ペットで会議発動！」の案内を1回だけ出す。
    public override void OnCheckPlayerPosition(PlayerControl pc)
    {
        if (MarkerPos == null || MeetingTriggered) return;

        // 呼び出し側 (PlayerControlPatch) が inTask / 生存 / ExileController 無し / IntroDestroyed を
        // 既にゲートしている。会議中と CNO だけ自前で弾く (Shuffler と同じ形)。
        if (GameStates.IsMeeting) return;
        if (pc.PlayerId >= 200) return;

        Vector2 markerPos = MarkerPos.Value;
        Vector2 pos = pc.Pos();

        if (!FastVector2.DistanceWithinRange(markerPos, pos, PressRange))
        {
            // 一度ボタンから十分離れて初めて案内を出し直す (境界上での往復連射を防ぐヒステリシス)。
            if (!FastVector2.DistanceWithinRange(markerPos, pos, PromptResetRange))
                PromptShown.Remove(pc.PlayerId);

            return;
        }

        if (!PromptShown.Add(pc.PlayerId)) return;

        pc.Notify(Translator.GetString("PortalButton.PetToCall"));
    }

    // 移設されたボタンの上に立っているか。押下は保持者以外 (非モッド客を含む) もできなければ
    // ならないので、役職の OnPet ではなく全員が通る PetActionsPatch.OnPetUse から呼ばれる。
    public static bool IsOnButton(PlayerControl pc)
    {
        if (!On || MarkerPos == null || !GameStates.IsInTask || GameStates.IsMeeting) return false;

        // 会議中は CNO が消えている (PortalButtonMarker.OnMeeting) のに MarkerPos は生きたままなので、
        // 座標ではなく CNO の実在を正とする。効いてほしいのは AfterMeetingTasks が Marker=null に
        // してから LateTask (RespawnDelay) で張り直すまでの窓 — ここは追放も会議も終わっていて
        // 他のゲートが全部開くため、これが無いと「見えないボタンを押して即会議」が通ってしまう。
        // ExileController は追放カットシーン中の押下除け (OnCheckPlayerPosition 側と同じ関所)。
        if (Marker == null || ExileController.Instance) return false;

        if (!pc || pc.PlayerId >= 200 || !pc.IsAlive()) return false;

        return FastVector2.DistanceWithinRange(MarkerPos.Value, pc.Pos(), PressRange);
    }

    // ボタンを押した = 緊急会議を起こす。押せない理由はその都度伝える (押下自体が
    // PetActionsPatch 側で1秒スロットルされているので連射にはならない)。
    public static void PressByPet(PlayerControl pc)
    {
        if (MeetingTriggered) return;

        // クリティカルサボタージュ中は緊急会議を起こせない (Ghostbuttoner と同じ判定リスト)。
        if (Utils.IsActive(SystemTypes.Reactor)
            || Utils.IsActive(SystemTypes.Electrical)
            || Utils.IsActive(SystemTypes.Laboratory)
            || Utils.IsActive(SystemTypes.Comms)
            || Utils.IsActive(SystemTypes.LifeSupp)
            || Utils.IsActive(SystemTypes.HeliSabotage))
        {
            pc.Notify(Translator.GetString("PortalButton.SabotageActive"));
            return;
        }

        // 本物と同じくホストの緊急ボタンクールタイム設定に従う
        // (ラウンド開始・会議明けから設定秒数のあいだは押しても反応しない)。値は per-player の
        // オプションからではなく素のホスト設定 (RealOptionsData) から引く — ボタンを持ち上げたあとは
        // per-player 側の EmergencyCooldown が 3600 に上書きされているため (PlayerGameOptionsSender)。
        int emergencyCooldown = Main.RealOptionsData?.GetInt(Int32OptionNames.EmergencyCooldown) ?? 0;
        long elapsed = Utils.TimeStamp - EmergencyClockStartTS;

        if (elapsed < emergencyCooldown)
        {
            pc.Notify(string.Format(Translator.GetString("PortalButton.OnCooldown"), emergencyCooldown - elapsed));
            return;
        }

        // 移設されたボタンも「緊急ボタン」なので、ホストの緊急会議回数設定を消費する。
        // NoCheckStartMeeting は ReportDeadBody を通らないので加算は自前で行う
        // (ReportDeadBodyPatch:1160 と同じく未登録は 0 とみなす fail-closed)。
        int used = Main.NumEmergencyMeetingsUsed.GetValueOrDefault(pc.PlayerId);
        if (used >= Main.RealOptionsData.GetInt(Int32OptionNames.NumEmergencyMeetings))
        {
            pc.Notify(Translator.GetString("NoMoreEmergencyMeetingsLeft"));
            return;
        }

        Main.NumEmergencyMeetingsUsed[pc.PlayerId] = used + 1;

        MeetingTriggered = true;
        Logger.Info($"{pc.GetNameWithRole().RemoveHtmlTags()} が移設された緊急ボタンを押したため会議を開始", "PortalButton");
        pc.NoCheckStartMeeting(null, true);
    }

    public override void AfterMeetingTasks()
    {
        MeetingTriggered = false;

        // 張り直しは共有物に対する処理なので1会議1回に絞る。ここを絞らないと、保持者が複数居るときと
        // 同一会議で AfterMeetingTasks が複数回走る経路で、同じ座標に CNO が重複生成される。
        int meetingNum = MeetingStates.MeetingNum;
        if (LastRespawnMeetingNum == meetingNum) return;
        LastRespawnMeetingNum = meetingNum;

        // 本物の緊急ボタンと同じく、会議明けからクールタイムを数え直す。
        EmergencyClockStartTS = Utils.TimeStamp;

        // 会議で CNO は Despawn 済み (PortalButtonMarker.OnMeeting)。移設されたボタンは
        // ラウンドを跨いで残る契約なので同じ座標へ張り直す。AfterMeetingTasks は
        // Utils.AfterMeetingTasks の foreach (EnumeratePlayerControls) の中から呼ばれるため、
        // ここで CreateNetObject を直接呼ぶと親の enumerator が壊れる → LateTask で遅らせる。
        Marker = null;
        if (MarkerPos == null) return;

        Vector2 pos = MarkerPos.Value;

        LateTask.New(() =>
        {
            if (!GameStates.InGame || GameStates.IsEnded || MarkerPos == null) return;

            // ラッチの種まきは「全員がスポーンに降りきったあと」でないと意味が無いので、
            // 生成と同じタイミング (この LateTask の中) で SpawnMarkerAt にまとめて行う。
            SpawnMarkerAt(pos);
        }, RespawnDelay, "PortalButton Respawn Marker");
    }

    private static void DespawnMarker()
    {
        Marker?.Despawn();
        Marker = null;
        MarkerPos = null;
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (meeting) return string.Empty;
        // seer がこのインスタンスの持ち主本人であることまで確認する (BuildSuffix は全インスタンスを舐めるため)
        if (seer.PlayerId != OwnerId || seer.PlayerId != target.PlayerId) return string.Empty;
        if (!Holding) return string.Empty;

        long remain = HoldTimeLimit.GetInt() - (Utils.TimeStamp - HoldStartTS);
        if (remain < 0) remain = 0;
        return Utils.ColorString(Color.red, $" ({remain})");
    }
}
