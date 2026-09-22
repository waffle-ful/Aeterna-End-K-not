using AmongUs.GameOptions;
using EndKnot.Modules;
using Hazel;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static EndKnot.Options;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class CurseMaker : RoleBase
{
    private const int Id = 704200;
    public static List<byte> PlayerIdList = [];

    private static OptionItem KillCooldown;
    private static OptionItem CurseDistance;
    private static OptionItem NoroiTime;
    private static OptionItem DelTurn;
    public static OptionItem CanSoloWin;
    private static OptionItem KillDistanceOverride;

    private byte _curseMakerId;
    private byte ChargingTargetId;
    // 起爆がそのまま試合を終わらせたときだけ単独勝利する。原典と同じく短い猶予で自然に失効する。
    private bool CanClaimWin;
    private float ChargeTimer;
    private Dictionary<byte, int> CursedPlayers = [];

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        SetupRoleOptions(Id, TabGroup.NeutralRoles, CustomRoles.CurseMaker);

        KillCooldown = new FloatOptionItem(Id + 10, "KillCooldown", new(0f, 180f, 0.5f), 20f, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.CurseMaker])
            .SetValueFormat(OptionFormat.Seconds);

        CurseDistance = new FloatOptionItem(Id + 11, "CurseMakerCurseDistance", new(0.5f, 5f, 0.25f), 1.75f, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.CurseMaker])
            .SetValueFormat(OptionFormat.Multiplier);

        NoroiTime = new FloatOptionItem(Id + 12, "CurseMakerNoroiTime", new(0.5f, 30f, 0.5f), 3f, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.CurseMaker])
            .SetValueFormat(OptionFormat.Seconds);

        DelTurn = new IntegerOptionItem(Id + 13, "CurseMakerDelTurn", new(1, 30, 1), 4, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.CurseMaker]);

        CanSoloWin = new BooleanOptionItem(Id + 14, "CurseMakerCanSoloWin", true, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.CurseMaker]);

        // 0 = Short, 1 = Medium, 2 = Long (vanilla Int32OptionNames.KillDistance と同じ意味)
        KillDistanceOverride = new IntegerOptionItem(Id + 15, "CurseMakerKillDistance", new(0, 2, 1), 0, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.CurseMaker]);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        _curseMakerId = playerId;
        ChargingTargetId = byte.MaxValue;
        ChargeTimer = 0f;
        CursedPlayers = [];
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    // 原典どおりキルボタンは「呪いの充填を始める照準」。実際には誰も殺さない。
    // ペットに全部まとめると、充填開始で付いたクールダウンがキャンセルと起爆まで塞いでしまう。
    public override bool CanUseKillButton(PlayerControl pc) => pc.IsAlive();

    public override bool OnCheckMurder(PlayerControl killer, PlayerControl target)
    {
        if (ChargingTargetId != byte.MaxValue)
        {
            killer.Notify(GetString("CurseMakerAlreadyCharging"));
            return false;
        }

        if (CursedPlayers.ContainsKey(target.PlayerId))
        {
            killer.Notify(string.Format(GetString("CurseMakerAlreadyCursed"), target.GetRealName()));
            return false;
        }

        ChargingTargetId = target.PlayerId;
        ChargeTimer = 0f;
        SendRPCCharging();
        killer.SetKillCooldown();
        Utils.NotifyRoles(SpecifySeer: killer, SpecifyTarget: killer);
        killer.Notify(string.Format(GetString("CurseMakerCharging"), target.GetRealName()));
        return false;
    }

    public override void ApplyGameOptions(IGameOptions opt, byte id)
    {
        opt.SetVision(false);
        opt.SetInt(Int32OptionNames.KillDistance, KillDistanceOverride.GetInt());
    }

    // Pet: charge cancel, or detonation. Starting a charge is on the kill button.
    public override void OnPet(PlayerControl pc)
    {
        if (!pc.IsAlive()) return;

        // Cancel active charge
        if (ChargingTargetId != byte.MaxValue)
        {
            ChargingTargetId = byte.MaxValue;
            ChargeTimer = 0f;
            SendRPCCharging();
            Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
            pc.Notify(GetString("CurseMakerChargeCanceled"));
            return;
        }

        // Detonate if cursed players exist
        if (CursedPlayers.Count > 0)
        {
            Detonate(pc);
            return;
        }

        pc.Notify(GetString("CurseMakerNoTarget"));
    }

    private void Detonate(PlayerControl pc)
    {
        bool soloWin = CanSoloWin.GetBool();
        byte[] cursedIds = [.. CursedPlayers.Keys];
        Logger.Info($"CurseMaker {pc.GetNameWithRole()} detonates {cursedIds.Length} cursed player(s)", "CurseMaker");

        LateTask.New(() =>
        {
            if (!GameStates.IsInTask) return;

            foreach (byte id in cursedIds)
            {
                PlayerControl cursed = Utils.GetPlayerById(id);
                if (cursed != null && cursed.IsAlive())
                    cursed.Suicide(PlayerState.DeathReason.Spell, pc);
            }

            if (soloWin && GameStates.IsInTask)
            {
                // ここで勝者を確定させると「1人呪って起爆」だけで試合を奪える。
                // 勝者が決まったあとの CheckWinner で名乗り出る形にして、
                // 起爆が実際に試合を終わらせたときだけ勝てるようにする。
                CanClaimWin = true;
                LateTask.New(() => CanClaimWin = false, 2f, log: false);
            }

            pc.Suicide(PlayerState.DeathReason.Bombed);
        }, 0.1f, "CurseMaker Detonate", true);
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!GameStates.IsInTask || !pc.IsAlive() || ChargingTargetId == byte.MaxValue) return;

        PlayerControl target = Utils.GetPlayerById(ChargingTargetId);
        if (target == null || !target.IsAlive())
        {
            ChargingTargetId = byte.MaxValue;
            ChargeTimer = 0f;
            SendRPCCharging();
            Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
            // 対象を見失った失敗は通常のクールダウンを食わせず即リトライ可能にする。
            Main.AllPlayerKillCooldown[pc.PlayerId] = 0.0001f;
            pc.SyncSettings();
            return;
        }

        float dist = Vector2.Distance(pc.Pos(), target.Pos());
        if (dist <= CurseDistance.GetFloat())
        {
            ChargeTimer += Time.fixedDeltaTime;
            if (ChargeTimer >= NoroiTime.GetFloat())
            {
                CursedPlayers.TryAdd(target.PlayerId, 0);
                ChargingTargetId = byte.MaxValue;
                ChargeTimer = 0f;
                SendRPCCursed(target.PlayerId);
                SendRPCCharging();
                Utils.NotifyRoles();
                Logger.Info($"CurseMaker cursed {target.GetNameWithRole()}", "CurseMaker");
            }
        }
        else
        {
            ChargingTargetId = byte.MaxValue;
            ChargeTimer = 0f;
            SendRPCCharging();
            Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
            // 射程外に出た失敗も同様に即リトライ可能にする。
            Main.AllPlayerKillCooldown[pc.PlayerId] = 0.0001f;
            pc.SyncSettings();
        }
    }

    public override void CheckWinner(GameOverReason reason)
    {
        if (!CanClaimWin) return;
        CanClaimWin = false;
        CustomWinnerHolder.ResetAndSetWinner(CustomWinner.CurseMaker);
        CustomWinnerHolder.WinnerIds.Add(_curseMakerId);
    }

    public override void OnReportDeadBody()
    {
        CanClaimWin = false;
        ChargingTargetId = byte.MaxValue;
        ChargeTimer = 0f;

        List<byte> toRemove = [];
        foreach ((byte id, int turns) in CursedPlayers)
        {
            PlayerControl p = Utils.GetPlayerById(id);
            if (p == null || !p.IsAlive() || turns + 1 >= DelTurn.GetInt())
                toRemove.Add(id);
            else
                CursedPlayers[id] = turns + 1;
        }

        toRemove.ForEach(id => CursedPlayers.Remove(id));
        SendRPCFullSync();

        // 誰が呪われたかは伏せたまま、人数だけ全員へ知らせる。
        PlayerControl curseMaker = Utils.GetPlayerById(_curseMakerId);
        if (curseMaker != null && curseMaker.IsAlive() && CursedPlayers.Count > 0)
            Utils.SendMessage(string.Format(GetString("CurseMakerMeetingAnnounce"), CursedPlayers.Count));
    }

    private void SendRPCCharging()
    {
        if (!Utils.DoRPC) return;
        MessageWriter w = Utils.CreateRPC(CustomRPC.SyncRoleData);
        w.Write(_curseMakerId);
        w.WritePacked(1);
        w.Write(ChargingTargetId);
        Utils.EndRPC(w);
    }

    private void SendRPCCursed(byte targetId)
    {
        if (!Utils.DoRPC) return;
        MessageWriter w = Utils.CreateRPC(CustomRPC.SyncRoleData);
        w.Write(_curseMakerId);
        w.WritePacked(2);
        w.Write(targetId);
        Utils.EndRPC(w);
    }

    private void SendRPCFullSync()
    {
        if (!Utils.DoRPC) return;
        MessageWriter w = Utils.CreateRPC(CustomRPC.SyncRoleData);
        w.Write(_curseMakerId);
        w.WritePacked(3);
        w.Write(ChargingTargetId);
        w.WritePacked(CursedPlayers.Count);
        foreach ((byte id, int turns) in CursedPlayers)
        {
            w.Write(id);
            w.WritePacked(turns);
        }
        Utils.EndRPC(w);
    }

    public void ReceiveRPC(MessageReader reader)
    {
        switch (reader.ReadPackedInt32())
        {
            case 1:
                ChargingTargetId = reader.ReadByte();
                ChargeTimer = 0f;
                break;
            case 2:
                CursedPlayers.TryAdd(reader.ReadByte(), 0);
                break;
            case 3:
                ChargingTargetId = reader.ReadByte();
                ChargeTimer = 0f;
                CursedPlayers.Clear();
                int count = reader.ReadPackedInt32();
                for (int i = 0; i < count; i++)
                {
                    byte id = reader.ReadByte();
                    int turns = reader.ReadPackedInt32();
                    CursedPlayers[id] = turns;
                }
                break;
        }
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        // Show curse markers on other players visible to CurseMaker
        if (seer.PlayerId == _curseMakerId && seer.PlayerId != target.PlayerId)
        {
            if (CursedPlayers.ContainsKey(target.PlayerId)) return "<color=#554d59>†</color>";
            if (target.PlayerId == ChargingTargetId) return "<color=#554d59>◇</color>";
            return string.Empty;
        }

        if (seer.PlayerId != _curseMakerId || seer.PlayerId != target.PlayerId) return string.Empty;
        if (!hud && !seer.IsModdedClient()) return string.Empty;

        if (ChargingTargetId != byte.MaxValue)
        {
            PlayerControl t = Utils.GetPlayerById(ChargingTargetId);
            string tName = t != null ? t.GetRealName() : "?";
            float pct = NoroiTime.GetFloat() > 0f ? ChargeTimer / NoroiTime.GetFloat() * 100f : 100f;
            return string.Format(GetString("CurseMakerChargingHUD"), tName, (int)pct);
        }

        if (CursedPlayers.Count > 0)
            return string.Format(GetString("CurseMakerCursedHUD"), CursedPlayers.Count);

        return GetString("CurseMakerPetToMarkHUD");
    }
}
