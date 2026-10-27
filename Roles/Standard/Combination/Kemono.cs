using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Modules;
using Hazel;
using UnityEngine;

namespace EndKnot.Roles;

// コンビ役職の主役職 (ケモノ)。ニュートラルだがクルーとして数える。エンジニア基底で、会議明けごとに決まる
// ランダムな部屋へ次の会議までに入れなければ失敗になり、2 回失敗すると相方のファーリーに★で正体が割れる。
// 生き残って捕まっていなければ、ゲーム終了時に勝者へ追加で勝つ。
// 出現率とオプションは相方の分も含めてここにぶら下げる (Modules/CombinationRoles.cs の Pairs 登録)。
public class Kemono : RoleBase
{
    public static bool On;
    public override bool IsEnable => On;

    public const int FailuresToReveal = 2;

    public static byte KemonoId = byte.MaxValue;
    public static byte FurryId = byte.MaxValue;

    public static bool Captured;
    public static int Failures;
    public static bool HasRoom;
    public static SystemTypes RoomGoal;
    public static bool Entered;

    private static int Generation;

    // 会議中・追放演出中にケモノが死んだ時は、ファーリーの役職変化を会議明けまで預かる
    private static bool PendingFurryChange;
    private static long NextCheckTick;

    public static OptionItem VentCooldown;
    public static OptionItem VentMaxTime;
    public static OptionItem CaptureCooldown;
    public static OptionItem FailureDies;
    public static OptionItem RoleAfterKemonoDies;

    public override void SetupCustomOption()
    {
        StartSetup(710300)
            .AutoSetupOption(ref VentCooldown, 15f, new FloatValueRule(0f, 60f, 0.5f), OptionFormat.Seconds, overrideName: "Kemono.VentCooldown")
            .AutoSetupOption(ref VentMaxTime, 5f, new FloatValueRule(0f, 60f, 0.5f), OptionFormat.Seconds, overrideName: "Kemono.VentMaxTime")
            .AutoSetupOption(ref CaptureCooldown, 35f, new FloatValueRule(15f, 180f, 1f), OptionFormat.Seconds, overrideName: "Kemono.CaptureCooldown")
            .AutoSetupOption(ref FailureDies, false, overrideName: "Kemono.FailureDies")
            .AutoSetupOption(ref RoleAfterKemonoDies, 0, PairRoleChange.OptionNames(), overrideName: "Kemono.RoleAfterKemonoDies", noTranslation: true);

        // 乗っ取り勝利の優先度。既定 1 は通常のクルー/インポスターの勝利 (0) より上
        SoloWinOption.Create(710310, TabGroup.Combinations, CustomRoles.Kemono, defo: 1);
    }

    public override void Init()
    {
        On = false;
        KemonoId = byte.MaxValue;
        // FurryId はファーリーの Init が消す (ファーリーが先に配られた時に、ここで消すと相方を見失う)
        Captured = false;
        PendingFurryChange = false;
        Failures = 0;
        HasRoom = false;
        RoomGoal = SystemTypes.Hallway;
        Entered = false;
        NextCheckTick = 0;
        Generation++;
    }

    public override void Add(byte playerId)
    {
        On = true;
        KemonoId = playerId;
        int generation = ++Generation;

        if (!AmongUsClient.Instance.AmHost) return;

        // 部屋の一覧は船が出来てから引ける。イントロの間は決めない
        LateTask.New(() =>
        {
            // 先に会議が来ていれば、会議明けの抽選が今のターンの部屋を決めている
            if (generation == Generation && !HasRoom && !GameStates.IsMeeting && !ExileController.Instance) PickRoom();
        }, Main.CurrentMap == MapNames.Airship ? 22f : 14f, "Kemono.FirstRoom", log: false);
    }

    public override void Remove(byte playerId)
    {
        if (KemonoId == playerId) KemonoId = byte.MaxValue;
        On = KemonoId != byte.MaxValue;
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        AURoleOptions.EngineerCooldown = VentCooldown.GetFloat();
        AURoleOptions.EngineerInVentMaxTime = VentMaxTime.GetFloat() <= 0f ? 300f : VentMaxTime.GetFloat();
    }

    public override bool CanUseKillButton(PlayerControl pc) => false;

    public override bool CanUseImpostorVentButton(PlayerControl pc) => false;

    private static void PickRoom()
    {
        if (!AmongUsClient.Instance.AmHost || KemonoId == byte.MaxValue || !ShipStatus.Instance) return;

        PlayerControl pc = Utils.GetPlayerById(KemonoId);

        if (!pc || !pc.IsAlive() || !pc.Is(CustomRoles.Kemono) || Captured)
        {
            if (HasRoom)
            {
                HasRoom = false;
                SendSync();
            }

            return;
        }

        List<SystemTypes> pool = ShipStatus.Instance.AllRooms
            .Select(x => x.RoomId)
            .Where(x => x != SystemTypes.Hallway && x != SystemTypes.Outside && x != SystemTypes.Ventilation && !x.ToString().Contains("Decontamination"))
            .Where(x => !SubmergedCompatibility.IsSubmerged() || (byte)x <= 135)
            .Distinct()
            .ToList();

        if (HasRoom && pool.Count > 1) pool.Remove(RoomGoal);
        if (pool.Count == 0) return;

        RoomGoal = pool.RandomElement();
        HasRoom = true;
        Entered = false;
        SendSync();

        Logger.Info($"Kemono room: {RoomGoal}", "Kemono");
        Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost || pc.PlayerId != KemonoId || !HasRoom || Entered || Captured) return;
        if (!Main.IntroDestroyed || !GameStates.IsInTask || ExileController.Instance || !pc.IsAlive()) return;

        long now = Environment.TickCount64;
        if (now < NextCheckTick) return;
        NextCheckTick = now + 250;

        if (pc.inVent || pc.inMovingPlat || !pc.IsInRoom(RoomGoal)) return;

        Entered = true;
        SendSync();
        Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
    }

    // 会議が始まった時に、決まっていた部屋へ入れていなければ失敗にする
    public override void OnReportDeadBody()
    {
        if (KemonoId == byte.MaxValue || !HasRoom || Captured) return;

        PlayerControl pc = Utils.GetPlayerById(KemonoId);
        if (!pc || !pc.IsAlive()) return;

        if (!Entered)
        {
            Failures++;
            Logger.Info($"Kemono failed to reach {RoomGoal} (failures {Failures})", "Kemono");
        }

        HasRoom = false;
        SendSync();
    }

    public override void AfterMeetingTasks()
    {
        PickRoom();
        ApplyPendingFurryChange();
    }

    // 死んだケモノの役職クラスでも AfterMeetingTasks が来るとは限らないので、ファーリー側からも呼ぶ
    public static void ApplyPendingFurryChange()
    {
        if (!PendingFurryChange || !AmongUsClient.Instance.AmHost) return;

        PendingFurryChange = false;

        PlayerControl pc = FurryId == byte.MaxValue ? null : Utils.GetPlayerById(FurryId);
        if (Captured || !pc || !pc.IsAlive() || !pc.Is(CustomRoles.Furry)) return;

        PairRoleChange.Apply(pc, RoleAfterKemonoDies.GetValue());
    }

    // PlayerState.SetDead から呼ばれる。捕まる前にケモノが死んだら、ファーリーは別の役職へ変わる
    public static void OnAnyoneSetDead(byte deadId)
    {
        if (!On || deadId != KemonoId || Captured || !AmongUsClient.Instance.AmHost) return;

        PlayerControl furry = FurryId == byte.MaxValue ? null : Utils.GetPlayerById(FurryId);
        if (!furry || !furry.IsAlive() || !furry.Is(CustomRoles.Furry)) return;

        byte furryId = furry.PlayerId;

        // 会議中に役職の基底を入れ替えると全員分の役職 RPC が会議中に出るので、会議明けへ回す
        if (GameStates.IsMeeting || ExileController.Instance || AntiBlackout.SkipTasks)
        {
            PendingFurryChange = true;
            return;
        }

        LateTask.New(() =>
        {
            PlayerControl pc = Utils.GetPlayerById(furryId);
            if (Captured || !pc || !pc.IsAlive() || !pc.Is(CustomRoles.Furry)) return;

            PairRoleChange.Apply(pc, RoleAfterKemonoDies.GetValue());
        }, 0.3f, "Kemono.FurryChange", log: false);
    }

    public static void Capture()
    {
        if (Captured) return;

        Captured = true;
        HasRoom = false;
        SendSync();

        PlayerControl pc = KemonoId == byte.MaxValue ? null : Utils.GetPlayerById(KemonoId);
        if (!pc) return;

        pc.Notify(Utils.ColorString(Utils.GetRoleColor(CustomRoles.Furry), Translator.GetString("KemonoCaptured")), 8f);
        Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
    }

    // 部屋名・捕獲・失敗数は各クライアントが自分の手元で表示を組み立てるので、変化時だけ送る
    private static void SendSync()
    {
        if (KemonoId == byte.MaxValue) return;
        Utils.SendRPC(CustomRPC.SyncRoleData, KemonoId, Captured, Failures, HasRoom, (byte)RoomGoal, Entered);
    }

    public void ReceiveRPC(MessageReader reader)
    {
        Captured = reader.ReadBoolean();
        Failures = reader.ReadPackedInt32();
        HasRoom = reader.ReadBoolean();
        RoomGoal = (SystemTypes)reader.ReadByte();
        Entered = reader.ReadBoolean();
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (KemonoId == byte.MaxValue || meeting) return string.Empty;

        // ケモノ本人にだけ今回の部屋を見せる
        if (seer.PlayerId == KemonoId && target.PlayerId == KemonoId)
        {
            if (!seer.IsAlive() || Captured || !HasRoom) return string.Empty;
            return Utils.ColorString(Entered ? Color.green : Color.yellow, Translator.GetString(RoomGoal));
        }

        // 2 回失敗したケモノは、ファーリーから見て正体が割れる
        if (seer.PlayerId == FurryId && target.PlayerId == KemonoId && seer.IsAlive() && !Captured && Failures >= FailuresToReveal)
            return Utils.ColorString(Utils.GetRoleColor(CustomRoles.Kemono), "★");

        return string.Empty;
    }
}
