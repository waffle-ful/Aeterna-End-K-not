using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Gamemodes;
using UnityEngine;

namespace EndKnot.Modules.Audience;

// Tier 1 干渉の実行部。すべて既存 RPC / サボタージュ経路のみ使用しホストローカルで完結する。
public static class AudienceInterventions
{
    // playerId -> 期限切れ TimeStamp。ApplyGameOptions の per-player 注入フックから毎回参照する。
    private static readonly Dictionary<byte, long> CursedVisionUntil = [];
    private static readonly Dictionary<byte, long> BlessedVisionUntil = [];

    private const float CursedVision = 0.5f;
    private const float BlessedVision = 2f;
    private const float BlessedSpeedIncrease = 0.2f;
    private const float CurseKillCooldownPenalty = 5f;

    // DoBless の速度復元 LateTask がゲームを跨がないようにする世代トークン。
    private static int GameGeneration;

    public static void ResetForNewGame()
    {
        CursedVisionUntil.Clear();
        BlessedVisionUntil.Clear();
        GameGeneration++;
    }

    // Modules/GameOptionsSender/PlayerGameOptionsSender.cs の per-player 注入ブロックから呼ばれる。
    public static void OnAnyoneApplyGameOptions(IGameOptions opt, byte playerId)
    {
        long now = Utils.TimeStamp;

        if (CursedVisionUntil.TryGetValue(playerId, out long curseExpire))
        {
            if (now < curseExpire)
            {
                opt.SetVision(false);
                opt.SetFloat(FloatOptionNames.CrewLightMod, CursedVision);
                opt.SetFloat(FloatOptionNames.ImpostorLightMod, CursedVision);
            }
            else
                CursedVisionUntil.Remove(playerId);
        }

        if (BlessedVisionUntil.TryGetValue(playerId, out long blessExpire))
        {
            if (now < blessExpire)
            {
                opt.SetVision(false);
                opt.SetFloat(FloatOptionNames.CrewLightMod, BlessedVision);
                opt.SetFloat(FloatOptionNames.ImpostorLightMod, BlessedVision);
            }
            else
                BlessedVisionUntil.Remove(playerId);
        }
    }

    public static bool DoBlackout()
    {
        if (!TrySabotage(SystemTypes.Electrical)) return false;
        return true;
    }

    public static bool DoReactor()
    {
        MapNames map = Main.CurrentMap;
        SystemTypes sabo = map switch
        {
            MapNames.Polus => SystemTypes.Laboratory,
            MapNames.Airship => SystemTypes.HeliSabotage,
            _ => SystemTypes.Reactor
        };

        return TrySabotage(sabo);
    }

    public static bool DoComms()
    {
        return TrySabotage(SystemTypes.Comms);
    }

    private static bool TrySabotage(SystemTypes sabo)
    {
        if (!ShipStatus.Instance) return false;
        if (Utils.IsActive(sabo)) return false;

        ShipStatus.Instance.RpcUpdateSystem(sabo, 128);
        return true;
    }

    public static bool DoDoors()
    {
        if (!ShipStatus.Instance) return false;
        if (!ShipStatus.Instance.Systems.ContainsKey(SystemTypes.Doors)) return false;

        List<OpenableDoor> doors = ShipStatus.Instance.AllDoors.ToList();
        if (doors.Count == 0) return false;

        SystemTypes room = doors.RandomElement().Room;
        List<OpenableDoor> roomDoors = doors.Where(d => d.Room == room).ToList();
        if (roomDoors.Count == 0) roomDoors = doors;

        foreach (OpenableDoor door in roomDoors)
            ShipStatus.Instance.RpcUpdateSystem(SystemTypes.Doors, (byte)door.Id);

        return true;
    }

    public static bool DoCurse(byte targetId)
    {
        PlayerControl target = Utils.GetPlayerById(targetId);
        if (!target || !target.IsAlive()) return false;

        float duration = AudienceOptions.CurseDuration.GetFloat();
        CursedVisionUntil[targetId] = Utils.TimeStamp + (long)duration;
        target.MarkDirtySettings();

        if (target.CanUseKillButton() && Main.AllPlayerKillCooldown.TryGetValue(targetId, out float kcd))
        {
            float newKcd = kcd + CurseKillCooldownPenalty;
            Main.AllPlayerKillCooldown[targetId] = newKcd;
            LateTask.New(() => target.SetKillCooldown(newKcd), 0.2f, log: false);
        }

        target.Notify(Translator.GetString("Audience.Notify.Cursed"));
        return true;
    }

    public static bool DoBless(byte targetId)
    {
        PlayerControl target = Utils.GetPlayerById(targetId);
        if (!target || !target.IsAlive()) return false;

        float duration = AudienceOptions.BlessDuration.GetFloat();
        BlessedVisionUntil[targetId] = Utils.TimeStamp + (long)duration;

        float baseSpeed = Main.AllPlayerSpeed.TryGetValue(targetId, out float currentSpeed)
            ? currentSpeed
            : Main.RealOptionsData.GetFloat(FloatOptionNames.PlayerSpeedMod);

        Main.AllPlayerSpeed[targetId] = baseSpeed + BlessedSpeedIncrease;
        target.MarkDirtySettings();

        int gen = GameGeneration;
        LateTask.New(() =>
        {
            if (!target || gen != GameGeneration || !GameStates.InGame) return;
            Main.AllPlayerSpeed[targetId] = Main.RealOptionsData.GetFloat(FloatOptionNames.PlayerSpeedMod);
            target.MarkDirtySettings();
        }, duration, log: false);

        target.Notify(Translator.GetString("Audience.Notify.Blessed"));
        return true;
    }

    public static bool DoMeteor()
    {
        if (Options.CurrentGameMode != CustomGameMode.NaturalDisasters && !Options.IntegrateNaturalDisasters.GetBool()) return false;
        if (!ShipStatus.Instance) return false;

        List<PlayerControl> aapc = Main.AllAlivePlayerControlsToList;
        if (aapc.Count == 0) return false;

        Vector2 position = aapc.RandomElement().Pos();
        NaturalDisasters.FixedUpdatePatch.AddPreparingDisaster(position, "Meteor", null);
        return true;
    }
}
