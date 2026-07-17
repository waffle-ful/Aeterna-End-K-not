using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Modules;
using EndKnot.Patches;
using Hazel;

namespace EndKnot;

public static class AntiBlackout
{
    public static bool SkipTasks;
    private static Dictionary<(byte SeerID, byte TargetID), (RoleTypes RoleType, CustomRoles CustomRole)> CachedRoleMap = [];

    // 役職ジャグリング窓 (SkipTasks) 中にダミーインポスター役を務めているプレイヤー (TOHK: dummyImpostorPlayer 相当)
    private static byte DummyImpId = byte.MaxValue;

    // Optimally, there's 1 living impostor and at least 2 living crewmates in everyone's POV.
    // We force this to prevent black screens after meetings.
    public static void SetOptimalRoleTypes()
    {
        // If there are only 2 or fewer players in the game in total, there's nothing we can do.
        if (CustomWinnerHolder.WinnerTeam != CustomWinner.Default || PlayerControl.AllPlayerControls.Count <= 2) return;

        SkipTasks = true;
        CachedRoleMap = StartGameHostPatch.RpcSetRoleReplacer.RoleMap.ToDictionary(x => (x.Key.SeerID, x.Key.TargetID), x => (x.Value.RoleType, x.Value.CustomRole));

        var players = Main.AllAlivePlayerControlsToArray;
        if (CheckForEndVotingPatch.TempExiledPlayer) players = players.Where(x => x.PlayerId != CheckForEndVotingPatch.TempExiledPlayer.PlayerId).ToArray();
        PlayerControl dummyImp = players.OrderByDescending(x => x.GetCustomRole() is not (CustomRoles.DetectiveEndKnot or CustomRoles.Detective) && !x.Is(CustomRoles.Examiner)).ThenByDescending(x => x.IsModdedClient()).MinBy(x => x.PlayerId);

        if (players.Length == 2)
        {
            // There are only 2 players alive. We need to revive 1 dead player to have 2 living crewmates.
            PlayerControl revived = Main.EnumeratePlayerControls().Where(x => !x.IsAlive() && !x.Data.Disconnected && x != CheckForEndVotingPatch.TempExiledPlayer?.Object).MaxBy(x => x.PlayerId);

            // The black screen cannot be prevented if there are no players to revive in this case.
            if (!revived)
            {
                // Fix the black screen manually for each player after the ejection screen.
                if (CheckForEndVotingPatch.TempExiledPlayer) CheckForEndVotingPatch.TempExiledPlayer.Object.FixBlackScreen();
                players.Do(x => x.FixBlackScreen());

                // Don't skip tasks since we couldn't set the optimal roles.
                SkipTasks = false;
                CachedRoleMap = [];
                return;
            }

            revived.RpcSetRoleGlobal(RoleTypes.Crewmate);
        }

        DummyImpId = dummyImp ? dummyImp.PlayerId : byte.MaxValue;
        dummyImp.RpcSetRoleGlobal(RoleTypes.Impostor);
        players.Without(dummyImp).Where(x => x.GetCustomRole() is not (CustomRoles.DetectiveEndKnot or CustomRoles.Detective) && !x.Is(CustomRoles.Examiner)).Do(x => x.RpcSetRoleGlobal(RoleTypes.Crewmate));
        
        Main.EnumeratePlayerControls().DoIf(x => !x.IsAlive() && x.Data && x.Data.IsDead && (!x.AmOwner || !Utils.TempReviveHostRunning), x => x.RpcSetRoleGlobal(GhostRolesManager.AssignedGhostRoles.TryGetValue(x.PlayerId, out var ghostRole) ? ghostRole.Instance.RoleTypes : RoleTypes.CrewmateGhost));
    }

    // After the ejection screen, we revert the role types to their actual values.
    public static void RevertToActualRoleTypes()
    {
        if (CachedRoleMap.Count == 0 || GameStates.IsEnded)
        {
            SkipTasks = false;
            DummyImpId = byte.MaxValue;
            ExileControllerWrapUpPatch.AfterMeetingTasks();
            return;
        }

        // Set the temporarily revived crewmate back to dead.
        //foreach (PlayerControl pc in Main.EnumeratePlayerControls())
        //{
        //    try
        //    {
        //        if (pc.AmOwner && Utils.TempReviveHostRunning) continue;

        //        NetworkedPlayerInfo data = pc.Data;

        //        if (data != null && !data.IsDead && !data.Disconnected && !pc.IsAlive())
        //        {
        //            data.IsDead = true;
        //            data.SendGameData();
        //        }
        //    }
        //    catch (Exception e) { Utils.ThrowException(e); }
        //}

        // Reset the role types for all players.
        // First group all entries by target.
        foreach (var targetGroup in CachedRoleMap.GroupBy(x => x.Key.TargetID))
        {
            try
            {
                byte targetId = targetGroup.Key;
                PlayerControl target = targetId.GetPlayer();
                if (!target) continue;

                // Compute the role every seer should see.
                Dictionary<byte, RoleTypes> rolesForSeers = [];

                foreach (var entry in targetGroup)
                {
                    byte seerId = entry.Key.SeerID;

                    RoleTypes role = target.IsAlive() && !Main.AfterMeetingDeathPlayers.ContainsKey(targetId) && Main.LastVotedPlayerInfo != target.Data
                        ? entry.Value.RoleType
                        : GhostRolesManager.AssignedGhostRoles.TryGetValue(targetId, out var ghostRole)
                            ? ghostRole.Instance.RoleTypes
                            : seerId == targetId &&
                              !(target.Is(CustomRoleTypes.Impostor) && Options.DeadImpCantSabotage.GetBool()) &&
                              Main.PlayerStates.TryGetValue(targetId, out var state) &&
                              state.Role.CanUseSabotage(target)
                                ? RoleTypes.ImpostorGhost
                                : RoleTypes.CrewmateGhost;

                    rolesForSeers[seerId] = role;
                }

                // First set them to the role they're most commonly seen as.
                RoleTypes globalRole = rolesForSeers.GroupBy(x => x.Value).MaxBy(g => g.Count()).Key;
                target.RpcSetRoleGlobal(globalRole);

                LateTask.New(() =>
                {
                    // Only send desync RPCs where needed. Often this will just be 1 additional RPC or none.
                    foreach ((byte seerId, RoleTypes roleTypes) in rolesForSeers)
                    {
                        try
                        {
                            if (roleTypes == globalRole) continue;

                            PlayerControl seer = seerId.GetPlayer();

                            if (!seer || (seerId == targetId && seer.AmOwner && Utils.TempReviveHostRunning))
                                continue;

                            target.RpcSetRoleDesync(roleTypes, seer.OwnerId);
                        }
                        catch (Exception e) { Utils.ThrowException(e); }
                    }
                }, 0.2f, "Set Desync Roles");
            }
            catch (Exception e) { Utils.ThrowException(e); }
        }

        // Reset the role map to the original values.
        StartGameHostPatch.RpcSetRoleReplacer.RoleMap = CachedRoleMap.ToDictionary(x => (x.Key.SeerID, x.Key.TargetID), x => (x.Value.RoleType, x.Value.CustomRole));
        CachedRoleMap = [];

        LateTask.New(() =>
        {
            var elapsedSeconds = (int)ExileControllerWrapUpPatch.Stopwatch.Elapsed.TotalSeconds;
            var sender = CustomRpcSender.Create("Exile Dead Players After Meeting", SendOption.Reliable);
            var hasValue = false;

            foreach (PlayerControl pc in Main.EnumeratePlayerControls())
            {
                try
                {
                    if (pc.IsAlive())
                    {
                        // Due to the role base change, we need to reset the cooldowns for abilities.
                        if (!Utils.ShouldNotApplyAbilityCooldownAfterMeeting(pc))
                            pc.RpcResetAbilityCooldown();

                        if (Main.AllPlayerKillCooldown.TryGetValue(pc.PlayerId, out float kcd))
                        {
                            float time = kcd - elapsedSeconds;
                            if (time > 0) pc.SetKillCooldown(time);
                        }
                        else
                            pc.SetKillCooldown();
                    }
                    else
                    {
                        if (pc.AmOwner && Utils.TempReviveHostRunning) continue;

                        // Ensure that the players who are considered dead by the mod are actually dead in the game.
                        sender.RpcExiled(pc);
                        hasValue = true;

                        if (GhostRolesManager.AssignedGhostRoles.TryGetValue(pc.PlayerId, out var ghostRole) && ghostRole.Instance.RoleTypes == RoleTypes.GuardianAngel)
                            pc.AddAbilityCD(ghostRole.Instance.Cooldown);
                    }
                }
                catch (Exception e) { Utils.ThrowException(e); }
            }

            sender.SendMessage(dispose: !hasValue);

            // Only execute AfterMeetingTasks after everything is reset.
            LateTask.New(() =>
            {
                SkipTasks = false;
                DummyImpId = byte.MaxValue;
                ExileControllerWrapUpPatch.AfterMeetingTasks();
            }, 1f, "Reset SkipTasks after SetRealPlayerRoles");
        }, 0.4f, "SetRealPlayerRoles - Reset Cooldowns");
    }

    // TOHK AntiBlackout.OnDisconnect 移植: 役職ジャグリング窓 (SkipTasks) 中にダミーインポスター役が
    // 切断すると、vanilla クライアント視点の生存インポスターが消えて追放画面明けに全滅判定 → 暗転する。
    // 切断を検知したら即座に別の生存者へダミーインポスターを付け直す。
    public static void OnDisconnect(PlayerControl player)
    {
        if (NetworkedPlayerInfoSerializePatch.KillSwitchActive) return;
        if (!AmongUsClient.Instance.AmHost || !SkipTasks || !player || player.PlayerId != DummyImpId) return;
        if (CustomWinnerHolder.WinnerTeam != CustomWinner.Default) return;

        DummyImpId = byte.MaxValue;

        var players = Main.AllAlivePlayerControlsToArray.Where(x => x.PlayerId != player.PlayerId).ToArray();
        if (CheckForEndVotingPatch.TempExiledPlayer) players = players.Where(x => x.PlayerId != CheckForEndVotingPatch.TempExiledPlayer.PlayerId).ToArray();

        // MinBy は手前の OrderBy を無視して全体から最小を取るため使わない (Detective/Examiner 除外を保ったまま PlayerId で tie-break)
        PlayerControl newDummy = players.OrderByDescending(x => x.GetCustomRole() is not (CustomRoles.DetectiveEndKnot or CustomRoles.Detective) && !x.Is(CustomRoles.Examiner)).ThenByDescending(x => x.IsModdedClient()).ThenBy(x => x.PlayerId).FirstOrDefault();

        if (!newDummy)
        {
            Logger.Warn("Dummy impostor disconnected during the juggling window, but no replacement is available", "AntiBlackout");
            return;
        }

        DummyImpId = newDummy.PlayerId;
        newDummy.RpcSetRoleGlobal(RoleTypes.Impostor);
        Logger.Warn($"Dummy impostor disconnected during the juggling window — reassigned to {newDummy.GetRealName()} (ID {newDummy.PlayerId})", "AntiBlackout");
    }

    public static void Reset()
    {
        Logger.Info("==Reset==", "AntiBlackout");
        CachedRoleMap = [];
        SkipTasks = false;
        DummyImpId = byte.MaxValue;
    }
}