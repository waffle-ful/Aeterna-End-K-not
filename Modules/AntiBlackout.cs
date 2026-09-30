using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Modules;
using EndKnot.Patches;
using EndKnot.Roles;
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

        if (players.Length <= 2)
        {
            // Fewer than 3 players alive — revive as many dead players as needed to reach the optimal
            // "1 impostor + 2 crewmates" POV. Historically only players.Length == 2 (revive 1) was handled;
            // a post-ejection survivor count of 1 (e.g. 実生存2人の会議でその片方を吊る — 赤ずきんの捕食死亡や
            // No Game End 続行で普通に起こる) fell through with no revive AND no FixBlackScreen fallback,
            // so every unmodded client blacked out (実機 2026-08-07_02.33.53 ゲーム2)。
            // ⚠️ 捕食中の赤ずきんは偽装蘇生の頭数に使わない。RpcSetRoleGlobal(Crewmate) はホストローカルでも
            //    SetRole を実行して Data.IsDead を false に巻き戻すため、赤ずきんの復活処理が「既に生存状態」と
            //    誤認してスキップされ、速度復元・目隠し解除などが丸ごと失われる (実機 2026-08-07_03.31.35 で確定。
            //    しかも Revert 側は IsDeathConcealed 除外で触らないので巻き戻しが恒久化する)。
            PlayerControl[] revived = Main.EnumeratePlayerControls()
                .Where(x => !x.IsAlive() && !x.Data.Disconnected && x != CheckForEndVotingPatch.TempExiledPlayer?.Object && !Akazukin.IsPseudoDead(x.PlayerId))
                .OrderByDescending(x => x.PlayerId)
                .Take(3 - players.Length)
                .ToArray();

            // The black screen cannot be prevented if there aren't enough players to revive
            // (or nobody is left alive to play the dummy impostor).
            if (!dummyImp || players.Length + revived.Length < 3)
            {
                // Fix the black screen manually for each player after the ejection screen.
                if (CheckForEndVotingPatch.TempExiledPlayer) CheckForEndVotingPatch.TempExiledPlayer.Object.FixBlackScreen();
                players.Do(x => x.FixBlackScreen());

                // 捕食中の赤ずきん (クライアントは生存のまま) も会議明けの全滅判定を踏む「生存クライアント」
                // だが、ホスト帳簿では死者なので players に入らず修復から漏れて暗転する (実機 2026-08-07_03.42.59)。
                // FixBlackScreen は死者向け self-murder を撃つため使えない (本人に死が届いて隠蔽が破れる)。
                // 代わりに本人の画面にだけ「本人の帳簿で生存に見える誰か」を Impostor に desync 表示して
                // imp1+crew2 相当の POV を作り、全滅判定そのものを回避する。役職の実態は変えないので
                // 復元は不要 (本人が crew である限り Impostor 表示の視覚影響も無い)。
                foreach (byte akaId in Akazukin.PseudoDead.Keys.ToArray())
                {
                    if (!Akazukin.IsDeathConcealed(akaId)) continue;
                    PlayerControl akaPc = akaId.GetPlayer();
                    PlayerControl fakeImp = Main.EnumeratePlayerControls().FirstOrDefault(x =>
                        x.PlayerId != akaId && x != CheckForEndVotingPatch.TempExiledPlayer?.Object &&
                        (!Main.PlayerStates[x.PlayerId].IsDead || x.GetCountTypes() == CountTypes.OutOfGame));
                    if (akaPc && fakeImp) fakeImp.RpcSetRoleDesync(RoleTypes.Impostor, akaPc.OwnerId);
                }

                // Don't skip tasks since we couldn't set the optimal roles.
                SkipTasks = false;
                CachedRoleMap = [];
                return;
            }

            foreach (PlayerControl pc in revived)
                pc.RpcSetRoleGlobal(RoleTypes.Crewmate);
        }

        DummyImpId = dummyImp ? dummyImp.PlayerId : byte.MaxValue;
        dummyImp.RpcSetRoleGlobal(RoleTypes.Impostor);
        players.Without(dummyImp).Where(x => x.GetCustomRole() is not (CustomRoles.DetectiveEndKnot or CustomRoles.Detective) && !x.Is(CustomRoles.Examiner)).Do(x => x.RpcSetRoleGlobal(RoleTypes.Crewmate));
        
        // 捕食中の赤ずきん (本人に死を隠している) はゴースト役職の broadcast から除外 — 本人が
        // SetRole(CrewmateGhost) を受信するとその場でゴースト化して隠蔽が破れる。他クライアントの
        // 帳簿では既に死者なので、見た目役職を触らなくても POV 最適化 (imp1+crew2) には影響しない。
        Main.EnumeratePlayerControls().DoIf(x => !x.IsAlive() && x.Data && x.Data.IsDead && (!x.AmOwner || !Utils.TempReviveHostRunning) && !Akazukin.IsDeathConcealed(x.PlayerId), x => x.RpcSetRoleGlobal(GhostRolesManager.AssignedGhostRoles.TryGetValue(x.PlayerId, out var ghostRole) ? ghostRole.Instance.RoleTypes : RoleTypes.CrewmateGhost));
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

                // 捕食中の赤ずきんの見た目役職は AntiBlackout に一切触らせない (クローズ時の偽装からも
                // 除外済みなので、ここで復元 SetRole を broadcast すると本人がゴースト化するだけ)。
                if (Akazukin.IsDeathConcealed(targetId)) continue;

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
                }, 0.2f, "Set Desync Roles", log: false);
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

                        // 捕食中の赤ずきんは死の再確立スイープから除外 — この RpcExiled は broadcast
                        // なので、送ると本人がゴースト化して隠蔽が破れる。他クライアントへの死亡は
                        // 捕食時の targeted MurderPlayer が確立済み。
                        if (Akazukin.IsDeathConcealed(pc.PlayerId)) continue;

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