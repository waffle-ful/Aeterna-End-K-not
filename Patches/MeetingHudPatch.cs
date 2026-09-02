using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Gamemodes;
using EndKnot.Modules;
using EndKnot.Modules.Ekm;
using EndKnot.Roles;
using HarmonyLib;
using Hazel;
using TMPro;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Patches;

[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.CheckForEndVoting))]
internal static class CheckForEndVotingPatch
{
    public static string EjectionText = string.Empty;
    public static NetworkedPlayerInfo TempExiledPlayer;
    public static bool ShouldSkip;

    public static bool Prefix(MeetingHud __instance)
    {
        if (!AmongUsClient.Instance.AmHost) return true;

        if (Medic.PlayerIdList.Count > 0) Medic.OnCheckMark();

        // Meeting Skip with vote counting
        ShouldSkip |= Input.GetKeyDown(KeyCode.F6);

        LogHandler voteLog = Logger.Handler("Vote");

        try
        {
            List<MeetingHud.VoterState> statesList = [];
            MeetingHud.VoterState[] states;

            foreach (PlayerVoteArea pva in __instance.playerStates)
            {
                if (!pva) continue;

                PlayerControl pc = Utils.GetPlayerById(pva.PlayerId);
                if (!pc) continue;
                // Dictators who are not dead have already voted.

                // Take the initiative to rebel
                if (pva.DidVote && pc.PlayerId == pva.VotedForId && pva.VotedForId < 253 && pc.IsAlive())
                {
                    if (Options.MadmateSpawnMode.GetInt() == 2 && Main.MadmateNum < CustomRoles.Madmate.GetCount() && pc.CanBeMadmate())
                    {
                        Main.MadmateNum++;
                        pc.RpcSetCustomRole(CustomRoles.Madmate);
                        ExtendedPlayerControl.RpcSetCustomRole(pc.PlayerId, CustomRoles.Madmate);
                        Logger.Info($"Set role: {pc.Data?.PlayerName} => {pc.GetCustomRole()} + {CustomRoles.Madmate}", $"Assign {CustomRoles.Madmate}");
                    }
                }

                if (pc.Is(CustomRoles.Dictator) && pva.DidVote && pc.PlayerId != pva.VotedForId && pva.VotedForId < 253 && pc.Data?.IsDead == false && pc.GetTaskState().CompletedTasksCount >= Dictator.MinTasksToDictate.GetInt())
                {
                    PlayerControl voteTarget = Utils.GetPlayerById(pva.VotedForId);
                    TryAddAfterMeetingDeathPlayers(PlayerState.DeathReason.Suicide, pc.PlayerId);

                    statesList.Add(new()
                    {
                        VoterId = pva.PlayerId,
                        VotedForId = pva.VotedForId
                    });

                    states = [.. statesList];

                    voteTarget.SetRealKiller(pc);
                    Main.LastVotedPlayerInfo = voteTarget.Data;
                    ExileControllerWrapUpPatch.LastExiled = voteTarget.Data;

                    if (Main.LastVotedPlayerInfo != null)
                        ConfirmEjections(Main.LastVotedPlayerInfo, false);

                    // Dictator の強制追放はバニラ Judge の「判決が下された」木槌演出で見せる (失敗時 nonce=0 → 通常追放)。
                    // ⚠️ QueueOverrule → RpcVotingComplete の隣接順序は不可分 (クライアントは queue 受信済みが gavel の前提) — 間に遅延や送信を挟まない
                    ushort dictatorGavelNonce = JudgeGavelPresenter.QueueOverrule(pc.PlayerId, pva.VotedForId);
                    __instance.RpcVotingComplete(states.ToArray(), voteTarget.Data, false, dictatorGavelNonce != 0, dictatorGavelNonce);

                    Statistics.OnVotingComplete(states.ToArray(), voteTarget.Data, false, true);

                    Logger.Info($"{voteTarget.GetNameWithRole().RemoveHtmlTags()} expelled by dictator", "Dictator");
                    CheckForDeathOnExile(PlayerState.DeathReason.Vote, pva.VotedForId);
                    Logger.Info("Dictatorship vote, forced end of meeting", "Special Phase");

                    MeetingHudRpcClosePatch.AllowClose = true;

                    return true;
                }

                if (pva.DidVote && pva.VotedForId < 253 && pc.IsAlive())
                {
                    PlayerControl voteTarget = Utils.GetPlayerById(pva.VotedForId);

                    if (!voteTarget || !voteTarget.IsAlive() || !voteTarget.Data || voteTarget.Data.Disconnected)
                    {
                        pva.UnsetVote();
                        __instance.RpcClearVote(pc.PlayerId);
                    }
                    else if (voteTarget && !pc.GetCustomRole().CancelsVote() && !pc.UsesMeetingShapeshift())
                        Main.PlayerStates[pc.PlayerId].Role.OnVote(pc, voteTarget);
                }
            }

            if (!ShouldSkip && !__instance.playerStates.All(ps =>
            {
                if (!ps || Silencer.ForSilencer.Contains(ps.PlayerId) || Shuffler.IsInVoid(ps.PlayerId) || !Main.PlayerStates.TryGetValue(ps.PlayerId, out PlayerState st) || st.IsDead || ps.AmDead || ps.DidVote) return true;
                PlayerControl targetPlayer = Utils.GetPlayerById(ps.PlayerId);
                return !targetPlayer || !targetPlayer.Data || targetPlayer.Data.Disconnected;
            })) return false;

            NetworkedPlayerInfo exiledPlayer = PlayerControl.LocalPlayer.Data;
            var tie = false;
            EjectionText = string.Empty;

            foreach (PlayerVoteArea ps in __instance.playerStates)
            {
                if (!ps) continue;

                voteLog.Info($"{ps.PlayerId,-2}{$"({Utils.GetVoteName(ps.PlayerId)})".PadRightV2(40)}:{ps.VotedForId,-3}({Utils.GetVoteName(ps.VotedForId)})");
                PlayerControl voter = Utils.GetPlayerById(ps.PlayerId);
                if (!voter || !voter.Data || voter.Data.Disconnected) continue;

                if (Options.VoteMode.GetBool())
                {
                    if (ps.VotedForId == 253 && voter.IsAlive() &&
                        !(Options.WhenSkipVoteIgnoreFirstMeeting.GetBool() && MeetingStates.FirstMeeting) &&
                        !(Options.WhenSkipVoteIgnoreNoDeadBody.GetBool() && !MeetingStates.IsExistDeadBody) &&
                        !(Options.WhenSkipVoteIgnoreEmergency.GetBool() && MeetingStates.IsEmergencyMeeting)
                        )
                    {
                        switch (Options.GetWhenSkipVote())
                        {
                            case VoteMode.Suicide:
                                TryAddAfterMeetingDeathPlayers(PlayerState.DeathReason.SkippedVote, ps.PlayerId);
                                voteLog.Info($"{voter.GetNameWithRole().RemoveHtmlTags()} Commit suicide for skipping voting");
                                break;
                            case VoteMode.SelfVote:
                                ps.VotedForId = ps.PlayerId;
                                voteLog.Info($"{voter.GetNameWithRole().RemoveHtmlTags()} Self-voting due to skipping voting");
                                break;
                        }
                    }

                    if (ps.VotedForId == 254 && voter.IsAlive())
                    {
                        switch (Options.GetWhenNonVote())
                        {
                            case VoteMode.Suicide:
                                TryAddAfterMeetingDeathPlayers(PlayerState.DeathReason.DidntVote, ps.PlayerId);
                                voteLog.Info($"{voter.GetNameWithRole().RemoveHtmlTags()} Committed suicide for not voting");
                                break;
                            case VoteMode.SelfVote:
                                ps.VotedForId = ps.PlayerId;
                                voteLog.Info($"{voter.GetNameWithRole().RemoveHtmlTags()} Self-voting due to not voting");
                                break;
                            case VoteMode.Skip:
                                ps.VotedForId = 253;
                                voteLog.Info($"{voter.GetNameWithRole().RemoveHtmlTags()} Skip for not voting");
                                break;
                        }
                    }
                }
                
                if (ps.VotedForId == 254 && voter.IsAlive() && voter.Is(CustomRoles.Compelled))
                    TryAddAfterMeetingDeathPlayers(PlayerState.DeathReason.DidntVote, ps.PlayerId);

                if (CheckRole(ps.PlayerId, CustomRoles.FortuneTeller) && FortuneTeller.HideVote.GetBool()) continue;
                if (CheckRole(ps.PlayerId, CustomRoles.EvilEraser) && EvilEraser.HideVote.GetBool()) continue;
                if (CheckRole(ps.PlayerId, CustomRoles.NiceEraser) && NiceEraser.HideVote.GetBool()) continue;
                if (CheckRole(ps.PlayerId, CustomRoles.Scout) && Scout.HideVote.GetBool()) continue;
                if (CheckRole(ps.PlayerId, CustomRoles.Oracle) && Oracle.HideVote.GetBool()) continue;
                if (ps.PlayerId == ps.VotedForId && Options.MadmateSpawnMode.GetInt() == 2 && CustomRoles.Madmate.IsEnable() && MeetingStates.FirstMeeting) continue;

                bool canVote = !(CheckRole(ps.PlayerId, CustomRoles.Glitch) && !Glitch.CanVote.GetBool());
                if (CheckRole(ps.PlayerId, CustomRoles.Shifter) && !Shifter.CanVote.GetBool()) canVote = false;
                if (((byte)ps.VotedForId).GetPlayer() != null && CheckRole(ps.VotedForId, CustomRoles.Zombie)) canVote = false;
                if (Poache.PoachedPlayers.Contains(ps.PlayerId)) canVote = false;
                if (Silencer.ForSilencer.Contains(ps.PlayerId) && Main.AllAlivePlayerControls.Count > Silencer.MaxPlayersAliveForSilencedToVote.GetInt()) canVote = false;
                if (CheckRole(ps.PlayerId, CustomRoles.Notvoter)) canVote = false;
                // Wave 2 (vote_block): 無効化グループ (Notvoter と同じ位置 —
                // 加算より前)。既に投票済みの票も含めて無効になる (Notvoter と同じ意味論)。
                if (EndKnot.Modules.Ekm.EkrManager.IsVoteBlocked(ps.PlayerId)) canVote = false;

                switch (Main.PlayerStates[ps.PlayerId].Role)
                {
                    case Adventurer { IsEnable: true } av when av.ActiveWeapons.Contains(Adventurer.Weapon.Proxy):
                        AddVote();
                        break;
                    case Amogus { IsEnable: true, ExtraVotes: > 0 } amogus:
                        Loop.Times(amogus.ExtraVotes, _ => AddVote());
                        break;
                    case ProbabilityKing { IsEnable: true, ExtraVotes: > 0 } pk:
                        Loop.Times(pk.ExtraVotes, _ => AddVote());
                        break;
                    case Dad { IsEnable: true } dad when dad.UsingAbilities.Contains(Dad.Ability.GoForMilk):
                        canVote = false;
                        break;
                    case Mayor mayor when !Mayor.MayorHideVote.GetBool():
                        Loop.Times(Mayor.MayorAdditionalVote.GetInt() + mayor.TaskVotes, _ => AddVote());
                        break;
                    // EKR passives.voteWeight (0..3・既定 1)。この switch は下の `if (canVote) AddVote();`
                    // より前なので、1票ぶんは既定の AddVote が出す — 追加は weight-1 票。
                    // ⚠ 「票のちから」は既にある票を増やす修飾であって票を作り出すものではない —
                    //    上の canVote=false 群 (Notvoter/Glitch/Shifter/Poache/Silencer 等) で票を
                    //    奪われている場合は増やさない (でないと投票不能のはずの人が複数票を投じる)。
                    case EkmTemplateRole:
                    {
                        int ekrVoteWeight = EndKnot.Modules.Ekm.EkrManager.GetVoteWeight(ps.PlayerId);
                        if (ekrVoteWeight <= 0) canVote = false;
                        else if (canVote) Loop.Times(ekrVoteWeight - 1, _ => AddVote());
                        break;
                    }
                }

                if (canVote) AddVote();

                if (CheckRole(ps.PlayerId, CustomRoles.Magistrate) && Magistrate.CallCourtNextMeeting) Loop.Times(Magistrate.ExtraVotes.GetInt(), _ => AddVote());
                if (CheckRole(ps.PlayerId, CustomRoles.Vindicator) && !Options.VindicatorHideVote.GetBool()) Loop.Times(Options.VindicatorAdditionalVote.GetInt(), _ => AddVote());
                if (CheckRole(ps.PlayerId, CustomRoles.Knighted)) AddVote();

                if (CheckRole(ps.PlayerId, CustomRoles.Schizophrenic) && Options.DualVotes.GetBool())
                {
                    int count = statesList.Count(x => x.VoterId == ps.PlayerId && x.VotedForId == ps.VotedForId);
                    Loop.Times(count, _ => AddVote());
                }

                if (CheckRole(ps.PlayerId, CustomRoles.Stealer))
                {
                    var count = (int)(Main.EnumeratePlayerControls().Count(x => x.GetRealKiller()?.PlayerId == ps.PlayerId) * Options.VotesPerKill.GetFloat());
                    Loop.Times(count, _ => AddVote());
                }

                if (CheckRole(ps.PlayerId, CustomRoles.Pickpocket))
                {
                    var count = (int)(Main.EnumeratePlayerControls().Count(x => x.GetRealKiller()?.PlayerId == ps.PlayerId) * Pickpocket.VotesPerKill.GetFloat());
                    Loop.Times(count, _ => AddVote());
                }

                continue;

                void AddVote() =>
                    statesList.Add(new()
                    {
                        VoterId = ps.PlayerId,
                        VotedForId = ps.VotedForId
                    });
            }

            Commited.OnVotingResultsShown(statesList);
            Summoner.OnMeetingEnd();
            QuizMaster.OnMeetingEnd();

            states = [.. statesList];

            Dictionary<byte, int> votingData = __instance.CustomCalculateVotes();

            Blackmailer.ManipulateVotingResult(votingData, states);
            Swapper.ManipulateVotingResult(votingData, states);
            King.ManipulateVotingResult(votingData, states);
            Balancer.ManipulateVotingResult(votingData, states);
            Dominion.ManipulateVotingResult(votingData, states);
            Assumer.OnVotingEnd(votingData);
            MeetingAngel.NegateVotes(votingData, states);
            EndKnot.Modules.Ekm.EkrManager.ApplyVoteSwap(votingData, states); // Wave 2 (spec §3.3)

            foreach (MeetingHud.VoterState voteState in states)
                Missioneer.TrackVoteReceived(voteState.VotedForId);
            
            var exileId = byte.MaxValue;
            var max = 0;
            voteLog.Info("===Decision to expel player processing begins===");

            foreach (KeyValuePair<byte, int> data in votingData)
            {
                voteLog.Info($"{data.Key} ({Utils.GetVoteName(data.Key)}): {data.Value} votes");

                if (data.Value > max)
                {
                    voteLog.Info($"{data.Key} has higher votes ({data.Value})");
                    if (Dad.OnVotedOut(data.Key)) continue;
                    if (Safecracker.OnVotedOut(data.Key)) continue;

                    exileId = data.Key;
                    max = data.Value;
                    tie = false;
                }
                else if (data.Value == max)
                {
                    voteLog.Info($"{data.Key} and {exileId} have the same number of votes ({data.Value})");
                    exileId = byte.MaxValue;
                    tie = true;
                }

                voteLog.Info($"expelID: {exileId}, maximum: {max} votes");
            }

            voteLog.Info($"Decided to eject: {exileId} ({Utils.GetVoteName(exileId)})");

            var braked = false;

            if (tie)
            {
                var target = byte.MaxValue;
                int playerNum = PlayerControl.AllPlayerControls.Count;

                foreach (KeyValuePair<byte, int> data in votingData.Where(x => x.Key < playerNum && x.Value == max))
                {
                    if (Main.TiebreakerVoteFor.Contains(data.Key))
                    {
                        if (target != byte.MaxValue)
                        {
                            target = byte.MaxValue;
                            break;
                        }

                        target = data.Key;
                    }
                }

                if (target != byte.MaxValue)
                {
                    Logger.Info("Tiebreaker overrides evicted players", "Tiebreaker Vote");
                    exiledPlayer = GameData.Instance.GetPlayerById(target);
                    tie = false;
                    braked = true;
                }
            }

            Collector.CollectAmount(votingData, __instance);

            if (Options.VoteMode.GetBool() && Options.WhenTie.GetBool() && tie)
            {
                switch ((TieMode)Options.WhenTie.GetValue())
                {
                    case TieMode.Default:
                        exiledPlayer = GameData.Instance.AllPlayers.ToArray().FirstOrDefault(info => info.PlayerId == exileId);
                        break;
                    case TieMode.All:
                        byte[] exileIds = votingData.Where(x => x.Key < 15 && x.Value == max).Select(kvp => kvp.Key).ToArray();
                        foreach (byte playerId in exileIds) Utils.GetPlayerById(playerId).SetRealKiller(null);
                        TryAddAfterMeetingDeathPlayers(PlayerState.DeathReason.Vote, exileIds);
                        exiledPlayer = null;
                        break;
                    case TieMode.Random:
                        exiledPlayer = GameData.Instance.AllPlayers.ToArray().OrderBy(_ => Guid.NewGuid()).FirstOrDefault(x => votingData.TryGetValue(x.PlayerId, out int vote) && vote == max);
                        tie = false;
                        break;
                }
            }
            else if (!braked) exiledPlayer = GameData.Instance.AllPlayers.ToArray().FirstOrDefault(info => !tie && info.PlayerId == exileId);

            exiledPlayer?.Object.SetRealKiller(null);

            Main.LastVotedPlayerInfo = exiledPlayer;
            ExileControllerWrapUpPatch.LastExiled = exiledPlayer;

            if (Main.LastVotedPlayerInfo != null)
                ConfirmEjections(Main.LastVotedPlayerInfo, braked);

            __instance.RpcVotingComplete(states.ToArray(), exiledPlayer, tie, false, 0);

            Statistics.OnVotingComplete(states.ToArray(), exiledPlayer, tie, false);

            CheckForDeathOnExile(PlayerState.DeathReason.Vote, exileId);
            Utils.CheckAndSpawnAdditionalRenegade(exiledPlayer, ejection: true);

            if (QuizMaster.On) QuizMaster.Data.NumPlayersVotedLastMeeting = __instance.playerStates.Count(x => x.DidVote);

            MeetingHudRpcClosePatch.AllowClose = true;

            return false;
        }
        catch (Exception ex)
        {
            Utils.ThrowException(ex);
            throw;
        }
    }

    // Reference: https://github.com/music-discussion/TownOfHost-TheOtherRoles
    private static void ConfirmEjections(NetworkedPlayerInfo exiledPlayer, bool tiebreaker)
    {
        if (!AmongUsClient.Instance.AmHost) return;

        if (!exiledPlayer) return;

        byte exileId = exiledPlayer.PlayerId;
        if (exileId > 254) return;

        string realName = exiledPlayer.Object.GetRealName(true);

        PlayerControl player = exiledPlayer.Object;
        CustomRoles crole = exiledPlayer.GetCustomRole();
        string coloredRole = Utils.GetDisplayRoleName(exileId, pure: true, seeTargetBetrayalAddons: true);

        if (crole == CustomRoles.LovingImpostor && !Options.ConfirmLoversOnEject.GetBool())
        {
            coloredRole = (Lovers.LovingImpostorRoleForOtherImps.GetValue() switch
            {
                0 => CustomRoles.ImpostorEndKnot,
                1 => Lovers.LovingImpostorRole,
                _ => CustomRoles.LovingImpostor
            }).ToColoredString();
        }

        if (Options.ConfirmEgoistOnEject.GetBool() && player.Is(CustomRoles.Egoist) && !Options.ImpEgoistVisibalToAllies.GetBool()) coloredRole = Utils.ColorString(Utils.GetRoleColor(CustomRoles.Egoist), $"{CustomRoles.Egoist.ToColoredString()} {coloredRole.RemoveHtmlTags()}");
        if (Options.ConfirmLoversOnEject.GetBool() && Main.LoversPlayers.Exists(x => x.PlayerId == player.PlayerId)) coloredRole = Utils.ColorString(Utils.GetRoleColor(CustomRoles.Lovers), $"{CustomRoles.Lovers.ToColoredString()} {coloredRole.RemoveHtmlTags()}");
        if (Options.RascalAppearAsMadmate.GetBool() && player.Is(CustomRoles.Rascal)) coloredRole = Utils.ColorString(Utils.GetRoleColor(CustomRoles.Madmate), GetRoleString("Mad-") + coloredRole.RemoveHtmlTags());

        var name = string.Empty;
        var impnum = 0;
        var neutralnum = 0;
        var covennum = 0;

        var decidedWinner = false;

        if (CustomRoles.Bard.RoleExist())
        {
            Bard.OnMeetingHudDestroy(ref name);
            goto EndOfSession;
        }

        foreach (PlayerControl pc in Main.EnumerateAlivePlayerControls())
        {
            if (pc == exiledPlayer.Object) continue;

            if (pc.IsImpostor() || Options.MadmateCountMode.GetValue() == 1 && pc.IsMadmate())
                impnum++;
            else if (pc.IsNeutralKiller())
                neutralnum++;
            else if (pc.Is(Team.Coven))
                covennum++;
        }

        string coloredRealName = Utils.ColorString(Main.PlayerColors[player.PlayerId], realName);

        switch (Options.CEMode.GetInt())
        {
            case 0:
                name = string.Format(GetString("PlayerExiled"), coloredRealName);
                break;
            case 1:
                if (player.Is(Team.Impostor))
                    name = string.Format(GetString("BelongTo"), coloredRealName, Utils.ColorString(Utils.GetRoleColor(CustomRoles.Impostor), GetString("TeamImpostor")));
                else if (player.IsCrewmate())
                    name = string.Format(GetString("IsGood"), coloredRealName);
                else if (player.GetCustomRole().IsNeutral() || player.IsNeutralKiller())
                    name = string.Format(GetString("BelongTo"), coloredRealName, Utils.ColorString(new(255, 171, 27, byte.MaxValue), GetString("TeamNeutral")));
                else if (player.Is(Team.Coven))
                    name = string.Format(GetString("BelongTo"), coloredRealName, Utils.ColorString(Team.Coven.GetColor(), GetString("TeamCoven")));

                break;
            case 2:
                name = string.Format(GetString("PlayerIsRole"), coloredRealName, coloredRole);

                if (Options.ShowTeamNextToRoleNameOnEject.GetBool() && !(crole.IsVanilla() || crole.ToString().EndsWith("EndKnot")))
                {
                    name += " (";
                    CustomTeamManager.CustomTeam team = CustomTeamManager.GetCustomTeam(player.PlayerId);

                    if (team != null)
                        name += Utils.ColorString(team.RoleRevealScreenBackgroundColor == "*" || !ColorUtility.TryParseHtmlString(team.RoleRevealScreenBackgroundColor, out Color color) ? Color.yellow : color, team.RoleRevealScreenTitle == "*" ? team.TeamName : team.RoleRevealScreenTitle);
                    else
                    {
                        Team playerTeam = player.GetTeam();

                        if (Forger.Forges.TryGetValue(player.PlayerId, out var forgedRole))
                            playerTeam = forgedRole.GetTeam();

                        Color color = playerTeam.GetColor();
                        string str = GetString($"Team{playerTeam}");
                        name += Utils.ColorString(color, str);
                    }

                    name += ")";
                }

                break;
        }

        if (crole == CustomRoles.Jester)
        {
            if (Options.ShowDifferentEjectionMessageForSomeRoles.GetBool()) name = string.Format(GetString("ExiledJester"), realName, coloredRole);
            decidedWinner = true;
        }

        if (Executioner.CheckExileTarget(exiledPlayer, true))
        {
            if (Options.ShowDifferentEjectionMessageForSomeRoles.GetBool())
            {
                if (decidedWinner)
                    name += string.Format(GetString("ExiledExeTargetAddBelow"));
                else
                    name = string.Format(GetString("ExiledExeTarget"), realName, coloredRole);
            }

            decidedWinner = true;
        }

        if (Main.EnumeratePlayerControls().Any(x => x.Is(CustomRoles.Innocent) && !x.IsAlive() && x.GetRealKiller()?.PlayerId == exileId))
        {
            if (!(!Options.InnocentCanWinByImp.GetBool() && crole.IsImpostor()))
            {
                if (Options.ShowDifferentEjectionMessageForSomeRoles.GetBool())
                {
                    if (decidedWinner)
                        name += string.Format(GetString("ExiledInnocentTargetAddBelow"));
                    else
                        name = string.Format(GetString("ExiledInnocentTargetInOneLine"), realName, coloredRole);
                }

                decidedWinner = true;
            }
        }

        if (tiebreaker) name += $" ({CustomRoles.Tiebreaker.ToColoredString()})";

        if (!decidedWinner)
        {
            bool showImpRemain = Options.ShowImpRemainOnEject.GetBool();
            bool showNKRemain = Options.ShowNKRemainOnEject.GetBool();
            bool showCovenRemain = Options.ShowCovenRemainOnEject.GetBool();

            if (showImpRemain || showNKRemain || showCovenRemain)
            {
                int sum = impnum + neutralnum + covennum;

                if (!showImpRemain) impnum = 0;
                if (!showNKRemain) neutralnum = 0;
                if (!showCovenRemain) covennum = 0;

                name += (impnum, neutralnum, covennum) switch
                {
                    (0, 0, 0) when sum == 0 && !Main.EnumerateAlivePlayerControls().Any(x => x.IsConverted()) && !(Options.SpawnAdditionalRenegadeOnImpsDead.GetBool() && (Options.SpawnAdditionalRenegadeWhenNKAlive.GetBool() || neutralnum == 0) && Main.AllAlivePlayerControls.Count >= Options.SpawnAdditionalRenegadeMinAlivePlayers.GetInt()) => "\n" + GetString("GG"),
                    (0, 0, 0) when sum > 0 => string.Empty,
                    _ => "\n" + Utils.GetRemainingKillers(true, excludeId: exileId)
                };
            }
        }

        if (Swapper.On && Swapper.SwapTargets != (byte.MaxValue, byte.MaxValue))
        {
            var p1 = Swapper.SwapTargets.Item1.GetPlayer();
            var p2 = Swapper.SwapTargets.Item2.GetPlayer();
            
            if (p1 != null && p2 != null && p1.IsAlive() && p2.IsAlive() && (p1.PlayerId == exileId || p2.PlayerId == exileId))
                name += $"\n{string.Format(GetString("SwapperManipulatedEjection"), CustomRoles.Swapper.ToColoredString())}";
        }

        EndOfSession:

        if (Options.CurrentGameMode == CustomGameMode.TheMindGame)
            name = TheMindGame.GetEjectionMessage(exileId);

        name = name.Replace("color=", string.Empty) + "<size=0>";
        TempExiledPlayer = exiledPlayer;
        EjectionText = name;
    }

    public static bool CheckRole(byte id, CustomRoles role)
    {
        PlayerState s = Main.PlayerStates[id];
        return role.IsAdditionRole() ? s.SubRoles.Contains(role) : s.MainRole == role;
    }

    public static void TryAddAfterMeetingDeathPlayers(PlayerState.DeathReason deathReason, params byte[] playerIds)
    {
        if (playerIds.Length == 0) return;
        Logger.Info($"{playerIds.Join(x => Main.AllPlayerNames[x])} - died with the reason: {deathReason}", "TryAddAfterMeetingDeathPlayers");
        byte[] addedIdList = playerIds.Where(id => (!Main.PlayerStates.TryGetValue(id, out PlayerState state) || state.MainRole != CustomRoles.Pestilence) && Main.AfterMeetingDeathPlayers.TryAdd(id, deathReason)).ToArray();
        CheckForDeathOnExile(deathReason, addedIdList);
    }

    private static void CheckForDeathOnExile(PlayerState.DeathReason deathReason, params byte[] playerIds)
    {
        try
        {
            if (deathReason == PlayerState.DeathReason.Vote)
            {
                if (Witch.PlayerIdList.Count > 0) Witch.OnCheckForEndVoting(playerIds);
                if (Virus.PlayerIdList.Count > 0) Virus.OnCheckForEndVoting(playerIds);
                Gaslighter.OnExile(playerIds);
                if (Wasp.On) Wasp.OnExile(playerIds);
                if (CustomRoles.SpellCaster.RoleExist()) SpellCaster.OnExile(playerIds);
            }

            foreach (byte playerId in playerIds)
            {
                try
                {
                    byte id = playerId;

                    if (CustomRoles.Lovers.IsEnable() && !Main.IsLoversDead && Main.LoversPlayers.Exists(lp => lp.PlayerId == id))
                        FixedUpdatePatch.LoversSuicide(playerId, true, true);

                    if (Main.PlayerStates.TryGetValue(id, out PlayerState state) && state.SubRoles.Contains(CustomRoles.Avenger))
                        RevengeOnExile(playerId /*, deathReason*/);
                }
                catch (Exception e) { Utils.ThrowException(e); }
            }
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    // Wave 2 (exile): EkrLogicOpcodes.Exile から呼ぶ。Dictator の強制追放
    // 経路 (上の CheckForEndVoting 内 :61-91) の一般部分をそのままなぞる — Dictator 固有の自殺コスト
    // (TryAddAfterMeetingDeathPlayers(Suicide, exiler)) は EKR exile の契約に無いので入れない。
    // CloseMeeting/RpcClose の送信パターンは一切変更しない (既往 anti-cheat 論点への配線変更禁止)。
    internal static void ForceExile(PlayerControl target, PlayerControl exiler, bool judgeGavel = false)
    {
        if (!AmongUsClient.Instance.AmHost || !target || !target.Data || !MeetingHud.Instance) return;

        var states = new[]
        {
            new MeetingHud.VoterState { VoterId = exiler ? exiler.PlayerId : target.PlayerId, VotedForId = target.PlayerId }
        };

        target.SetRealKiller(exiler);
        Main.LastVotedPlayerInfo = target.Data;
        ExileControllerWrapUpPatch.LastExiled = target.Data;

        if (Main.LastVotedPlayerInfo != null) ConfirmEjections(Main.LastVotedPlayerInfo, false);

        // ⚠️ QueueOverrule → RpcVotingComplete の隣接順序は不可分 (クライアントは queue 受信済みが gavel の前提) — 間に遅延や送信を挟まない
        ushort gavelNonce = judgeGavel ? JudgeGavelPresenter.QueueOverrule(exiler ? exiler.PlayerId : target.PlayerId, target.PlayerId) : (ushort)0;
        MeetingHud.Instance.RpcVotingComplete(states, target.Data, false, gavelNonce != 0, gavelNonce);
        Statistics.OnVotingComplete(states, target.Data, false, true);

        CheckForDeathOnExile(PlayerState.DeathReason.Vote, target.PlayerId);

        MeetingHudRpcClosePatch.AllowClose = true;

        Logger.Info($"{target.GetNameWithRole().RemoveHtmlTags()} force-exiled by EKR exile op (holder {(exiler ? exiler.PlayerId : target.PlayerId)})", "EkrManager");
    }

    private static void RevengeOnExile(byte playerId /*, PlayerState.DeathReason deathReason*/)
    {
        PlayerControl player = Utils.GetPlayerById(playerId);
        if (!player) return;

        PlayerControl target = PickRevengeTarget(player /*, deathReason*/);
        if (!target) return;

        TryAddAfterMeetingDeathPlayers(PlayerState.DeathReason.Revenge, target.PlayerId);
        target.SetRealKiller(player);
        Logger.Info($"{player.GetNameWithRole().RemoveHtmlTags()} revenged: {target.GetNameWithRole().RemoveHtmlTags()}", "RevengeOnExile");
    }

    private static PlayerControl PickRevengeTarget(PlayerControl exiledplayer /*, PlayerState.DeathReason deathReason*/)
    {
        List<PlayerControl> targetList = [];
        targetList.AddRange(Main.EnumerateAlivePlayerControls().Where(candidate => candidate != exiledplayer && !Main.AfterMeetingDeathPlayers.ContainsKey(candidate.PlayerId)));

        if (targetList.Count == 0) return null;

        PlayerControl target = targetList.RandomElement();
        return target;
    }
}

// AU 2026.8.18 のバニラ Judge (投票結果却下) の木槌演出を、mod 側の強制追放に借りるための送信ヘルパー。
// 機序 (GameAssembly 実測): クライアントの gavel は VotingComplete(wasOverruled=true) 受信時に
// ローカル judgeOverrulesQueue の先頭有効エントリ (judge/target とも生存) で表示され、役職検証は無い。
// mod は会議中の auto Data 同期を遮断している (MeetingDataBarrier) ため、queue の伝播は
// RPC 66 (QueueOverruleVotes: byte judgeId, byte targetId, ushort nonce) の手送りで行う。
// 呼び順は「QueueOverrule → RpcVotingComplete(..., true, nonce)」(共に Reliable なので順序保証)。
internal static class JudgeGavelPresenter
{
    private static ushort NonceCounter = 1; // 0 = vanilla の未初期化 sentinel なので 1 始まり

    // 成立時は nonce (>=1)、演出不成立 (judge 死亡等) は 0 を返す — 呼び元は 0 なら通常追放にフォールバック。
    public static ushort QueueOverrule(byte judgeId, byte targetId)
    {
        try
        {
            MeetingHud meeting = MeetingHud.Instance;
            if (!meeting) return 0;
            if (judgeId == targetId) return 0; // 「自分が自分を覆す」無意味演出の予防 (exiler=null の ForceExile 等)

            PlayerControl judge = Utils.GetPlayerById(judgeId);
            if (judge == null || !judge.IsAlive() || judge.Data == null || judge.Data.Disconnected) return 0;

            ushort nonce = NonceCounter++;
            if (NonceCounter == 0) NonceCounter = 1;

            meeting.SetJudgeOverrule(judgeId, targetId, nonce);

            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(meeting.NetId, (byte)RpcCalls.QueueOverruleVotes, SendOption.Reliable);
            writer.Write(judgeId);
            writer.Write(targetId);
            writer.Write(nonce);
            AmongUsClient.Instance.FinishRpcImmediately(writer);

            Logger.Info($"Judge gavel queued: judge={judgeId}, target={targetId}, nonce={nonce}", "JudgeGavelPresenter");
            return nonce;
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            return 0;
        }
    }
}

internal static class ExtendedMeetingHud
{
    public static Dictionary<byte, int> CustomCalculateVotes(this MeetingHud __instance)
    {
        Logger.Info("===The vote counting process begins===", "Vote");
        Dictionary<byte, int> dic = [];
        Main.TiebreakerVoteFor = [];
        Collector.CollectorVoteFor = [];

        foreach (PlayerVoteArea ps in __instance.playerStates)
        {
            if (!ps) continue;

            if ((byte)ps.VotedForId is not 252 and not byte.MaxValue and not 254)
            {
                var voteNum = 1;

                PlayerControl target = Utils.GetPlayerById(ps.VotedForId);

                if (target)
                {
                    if (target.Is(CustomRoles.Zombie) || (target.Is(CustomRoles.Shifter) && !Shifter.CanBeVoted.GetBool()))
                        voteNum = 0;

                    if (CheckForEndVotingPatch.CheckRole(ps.PlayerId, CustomRoles.Tiebreaker))
                    {
                        if (!Main.TiebreakerVoteFor.Contains(target.PlayerId))
                            Main.TiebreakerVoteFor.Add(target.PlayerId);
                    }

                    Collector.CollectorVotes(target, ps);
                }

                if (Poache.PoachedPlayers.Contains(ps.PlayerId)) voteNum = 0;
                if (Silencer.ForSilencer.Contains(ps.PlayerId) && Main.AllAlivePlayerControls.Count > Silencer.MaxPlayersAliveForSilencedToVote.GetInt()) voteNum = 0;
                if (CheckForEndVotingPatch.CheckRole(ps.PlayerId, CustomRoles.Notvoter)) voteNum = 0;
                // Wave 2 (spec §3.2): site1 と同方向の無効化グループ (2箇所セット)。
                if (EndKnot.Modules.Ekm.EkrManager.IsVoteBlocked(ps.PlayerId)) voteNum = 0;

                if (CheckForEndVotingPatch.CheckRole(ps.PlayerId, CustomRoles.Magistrate) && Magistrate.CallCourtNextMeeting) voteNum += Magistrate.ExtraVotes.GetInt();
                if (CheckForEndVotingPatch.CheckRole(ps.PlayerId, CustomRoles.Knighted)) voteNum += 1;
                if (CheckForEndVotingPatch.CheckRole(ps.PlayerId, CustomRoles.Glitch) && !Glitch.CanVote.GetBool()) voteNum = 0;
                if (CheckForEndVotingPatch.CheckRole(ps.PlayerId, CustomRoles.Shifter) && !Shifter.CanVote.GetBool()) voteNum = 0;
                if (CheckForEndVotingPatch.CheckRole(ps.PlayerId, CustomRoles.Vindicator)) voteNum += Options.VindicatorAdditionalVote.GetInt();
                if (CheckForEndVotingPatch.CheckRole(ps.PlayerId, CustomRoles.Schizophrenic) && Options.DualVotes.GetBool()) voteNum += voteNum;
                if (CheckForEndVotingPatch.CheckRole(ps.PlayerId, CustomRoles.Stealer)) voteNum += (int)(Main.EnumeratePlayerControls().Count(x => x.GetRealKiller()?.PlayerId == ps.PlayerId) * Options.VotesPerKill.GetFloat());
                if (CheckForEndVotingPatch.CheckRole(ps.PlayerId, CustomRoles.Pickpocket)) voteNum += (int)(Main.EnumeratePlayerControls().Count(x => x.GetRealKiller()?.PlayerId == ps.PlayerId) * Pickpocket.VotesPerKill.GetFloat());

                switch (Main.PlayerStates[ps.PlayerId].Role)
                {
                    case Adventurer { IsEnable: true } av when av.ActiveWeapons.Contains(Adventurer.Weapon.Proxy):
                        voteNum++;
                        break;
                    case Dad { IsEnable: true } dad when dad.UsingAbilities.Contains(Dad.Ability.GoForMilk):
                        voteNum = 0;
                        break;
                    case Amogus { IsEnable: true, ExtraVotes: > 0 } amogus:
                        voteNum += amogus.ExtraVotes;
                        break;
                    case ProbabilityKing { IsEnable: true, ExtraVotes: > 0 } pk:
                        voteNum += pk.ExtraVotes;
                        break;
                    case Mayor mayor:
                        voteNum += Mayor.MayorAdditionalVote.GetInt() + mayor.TaskVotes;
                        break;
                    case Survivor when Utils.AllAlivePlayersCount <= Survivor.ThirdAbility.GetInt():
                        voteNum += Survivor.AdditionalVote.GetInt();
                        break;
                    // EKR passives.voteWeight (0..3・既定 1)。ここは既に基本1票 (+他の加算修飾) が
                    // voteNum に入っているので、weight-1 を足す (voteNum=weight だと他の修飾を潰す)。
                    // ⚠ 上の `voteNum = 0` 群 (Notvoter/Glitch/Shifter) で票を奪われている場合は
                    //    増やさない — site 1 の canVote 判定と同じ合成規則 (票を作り出す修飾ではない)。
                    case EkmTemplateRole:
                    {
                        int ekrVoteWeight = EndKnot.Modules.Ekm.EkrManager.GetVoteWeight(ps.PlayerId);
                        if (ekrVoteWeight <= 0) voteNum = 0;
                        else if (voteNum > 0) voteNum += ekrVoteWeight - 1;
                        break;
                    }
                }

                if (ps.PlayerId == ps.VotedForId && Options.MadmateSpawnMode.GetInt() == 2 && CustomRoles.Madmate.IsEnable() && MeetingStates.FirstMeeting) voteNum = 0;

                dic[ps.VotedForId] = !dic.TryGetValue(ps.VotedForId, out int num) ? voteNum : num + voteNum;
            }
        }

        return dic;
    }
}

[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
internal static class MeetingHudStartPatch
{
    private static void NotifyRoleSkillOnMeetingStart()
    {
        if (!AmongUsClient.Instance.AmHost || GameStates.IsEnded) return;

        List<Message> msgToSend = [];

        if (Options.SendRoleDescriptionFirstMeeting.GetBool() && MeetingStates.FirstMeeting && Options.CurrentGameMode == CustomGameMode.Standard)
        {
            List<Message> roleDescMsgs = [];

            foreach (PlayerControl pc in Main.EnumerateAlivePlayerControls())
            {
                if (pc.IsModdedClient()) continue;

                CustomRoles role = pc.GetCustomRole();
                StringBuilder sb = new();
                StringBuilder titleSb = new();
                StringBuilder settings = new();
                settings.Append("<size=70%>");
                titleSb.Append($"{role.ToColoredString()} {Utils.GetRoleMode(role)}");
                sb.Append(pc.GetRoleInfo(true).TrimStart());
                
                if (Options.CustomRoleSpawnChances.TryGetValue(role, out StringOptionItem opt))
                    Utils.ShowChildrenSettings(opt, settings, disableColor: false);

                settings.Append("</size>");
                
                if (role.PetActivatedAbility())
                    sb.Append($"<size=1>{GetString("SupportsPetMessage")}</size>");

                string searchStr = GetString(role.ToString());
                sb.Replace(searchStr, role.ToColoredString());
                sb.Replace(searchStr.ToLower(), role.ToColoredString());

                if (settings.Length > 0) roleDescMsgs.Add(new("\n", pc.PlayerId, settings.ToString()));
                if (role.UsesPetInsteadOfKill()) roleDescMsgs.Add(new("\n", pc.PlayerId, GetString("UsesPetInsteadOfKillNotice")));
                if (pc.UsesMeetingShapeshift() && !MeetingTargetPicker.WouldGetButton(pc)) roleDescMsgs.Add(new("\n", pc.PlayerId, GetString("UsesMeetingShapeshiftNotice")));

                roleDescMsgs.Add(new(sb.ToString(), pc.PlayerId, titleSb.ToString()));

                // Send each subrole (addon) as its own titled chat box so a vanilla recipient
                // sees the addon name in the chat-bubble header instead of buried in a
                // continuation chunk whose title still shows the main role.
                foreach (CustomRoles subRole in Main.PlayerStates[pc.PlayerId].SubRoles)
                {
                    string subBody = GetString($"{subRole}InfoLong").FixRoleName(subRole);
                    string subTitle = $"{subRole.ToColoredString()} {Utils.GetRoleMode(subRole)}";
                    string searchSubStr = GetString(subRole.ToString());
                    subBody = subBody.Replace(searchSubStr, subRole.ToColoredString())
                                     .Replace(searchSubStr.ToLower(), subRole.ToColoredString());
                    roleDescMsgs.Add(new(subBody, pc.PlayerId, subTitle));
                }
            }

            LateTask.New(() =>
            {
                if (!AmongUsClient.Instance.AmHost || !GameStates.InGame || GameStates.IsEnded) return;

                roleDescMsgs.SendMultipleMessages(MessageImportance.Low);

                LateTask.New(() =>
                {
                    if (!AmongUsClient.Instance.AmHost || !GameStates.InGame || GameStates.IsEnded) return;

                    PlayerControl player = Main.EnumerateAlivePlayerControls().MinBy(x => x.PlayerId);
                    if (player == null || player.Data == null) return;

                    var sender = CustomRpcSender.Create("Fix Sender Name", SendOption.Reliable);
                    {
                        sender.AutoStartRpc(player.NetId, 6);
                        {
                            sender.Write(player.Data.NetId);
                            sender.Write(player.GetRealName(true));
                        }
                        sender.EndRpc();
                    }
                    sender.SendMessage();
                }, 1f, "Fix Sender Name");
            }, 8.5f, "Send Role Descriptions Round 1");
        }

        if (Options.MadmateSpawnMode.GetInt() == 2 && CustomRoles.Madmate.IsEnable() && MeetingStates.FirstMeeting)
            AddMsg(string.Format(GetString("Message.MadmateSelfVoteModeNotify"), GetString("MadmateSpawnMode.SelfVote")));

        if (CustomRoles.God.RoleExist() && God.NotifyGodAlive.GetBool())
            AddMsg(GetString("GodNoticeAlive"), 255, Utils.ColorString(Utils.GetRoleColor(CustomRoles.God), GetString("GodAliveTitle")));

        if (MeetingStates.FirstMeeting && CustomRoles.Workaholic.RoleExist() && Workaholic.WorkaholicGiveAdviceAlive.GetBool() && !Workaholic.WorkaholicCannotWinAtDeath.GetBool() /* && !Options.GhostIgnoreTasks.GetBool()*/)
        {
            foreach (PlayerControl pc in Main.EnumerateAlivePlayerControls().Where(x => x.Is(CustomRoles.Workaholic)).ToArray()) Workaholic.WorkaholicAlive.Add(pc.PlayerId);

            List<string> workaholicAliveList = [];
            workaholicAliveList.AddRange(Workaholic.WorkaholicAlive.Select(whId => Main.AllPlayerNames[whId]));

            string separator = TranslationController.Instance.currentLanguage.languageID is SupportedLangs.English or SupportedLangs.Russian ? "], [" : "】, 【";
            AddMsg(string.Format(GetString("WorkaholicAdviceAlive"), string.Join(separator, workaholicAliveList)), 255, Utils.ColorString(Utils.GetRoleColor(CustomRoles.Workaholic), GetString("WorkaholicAliveTitle")));
        }

        // Bait Notify
        if (MeetingStates.FirstMeeting && CustomRoles.Bait.RoleExist() && Options.BaitNotification.GetBool())
        {
            foreach (PlayerControl pc in Main.EnumerateAlivePlayerControls().Where(x => x.Is(CustomRoles.Bait)).ToArray()) Main.BaitAlive.Add(pc.PlayerId);

            List<string> baitAliveList = [];
            baitAliveList.AddRange(Main.BaitAlive.Select(whId => Main.AllPlayerNames[whId]));

            string separator = TranslationController.Instance.currentLanguage.languageID is SupportedLangs.English or SupportedLangs.Russian ? "], [" : "】, 【";
            AddMsg(string.Format(GetString("BaitAdviceAlive"), string.Join(separator, baitAliveList)), 255, Utils.ColorString(Utils.GetRoleColor(CustomRoles.Bait), GetString("BaitAliveTitle")));
        }

        var mimicMsg = string.Empty;

        foreach (PlayerControl pc in Main.EnumeratePlayerControls())
        {
            if (pc.Is(CustomRoles.Nemesis) && !pc.IsAlive()) AddMsg(GetString("NemesisDeadMsg"), pc.PlayerId);

            foreach (byte csId in Main.SuperStarDead)
            {
                if (!Options.ImpKnowSuperStarDead.GetBool() && pc.GetCustomRole().IsImpostor()) continue;
                if (!Options.NeutralKnowSuperStarDead.GetBool() && pc.GetCustomRole().IsNeutral()) continue;
                if (!Options.CovenKnowSuperStarDead.GetBool() && pc.Is(CustomRoleTypes.Coven)) continue;

                AddMsg(string.Format(GetString("SuperStarDead"), Main.AllPlayerNames[csId]), pc.PlayerId, Utils.ColorString(Utils.GetRoleColor(CustomRoles.SuperStar), GetString("SuperStarNewsTitle")));
            }

            if (Silencer.ForSilencer.Contains(pc.PlayerId))
            {
                string playername = pc.GetRealName();
                if (Doppelganger.DoppelVictim.TryGetValue(pc.PlayerId, out string value)) playername = value;
                else if (Autoscopy.OriginalNames.TryGetValue(pc.PlayerId, out string avalue)) playername = avalue;

                AddMsg(string.Format(GetString("SilencerDead"), playername), pc.PlayerId, Utils.ColorString(Utils.GetRoleColor(CustomRoles.Silencer), GetString("SilencerKillTitle")));
            }

            if (Forensic.ForensicNotify.TryGetValue(pc.PlayerId, out string value1)) AddMsg(value1, pc.PlayerId, Utils.ColorString(Utils.GetRoleColor(CustomRoles.Forensic), GetString("ForensicNoticeTitle")));
            if (Main.SleuthMsgs.TryGetValue(pc.PlayerId, out string msg)) AddMsg(msg, pc.PlayerId, Utils.ColorString(Utils.GetRoleColor(CustomRoles.Sleuth), GetString("Sleuth")));

            if (pc.Is(CustomRoles.Mimic) && !pc.IsAlive())
            {
                Main.EnumerateAlivePlayerControls()
                    .Where(x => x.GetRealKiller()?.PlayerId == pc.PlayerId)
                    .Do(x => mimicMsg += $"\n{x.GetNameWithRole(true)}");
            }

            if (Mortician.MsgToSend.TryGetValue(pc.PlayerId, out string value2)) AddMsg(value2, pc.PlayerId, Utils.ColorString(Utils.GetRoleColor(CustomRoles.Mortician), GetString("MorticianCheckTitle")));
            if (Medium.ContactPlayer.ContainsValue(pc.PlayerId)) AddMsg(string.Format(GetString("MediumNotifySelf"), Main.AllPlayerNames[Medium.ContactPlayer.FirstOrDefault(x => x.Value == pc.PlayerId).Key], pc.GetAbilityUseLimit()), pc.PlayerId, Utils.ColorString(Utils.GetRoleColor(CustomRoles.Medium), GetString("MediumTitle")));
            if (Medium.ContactPlayer.ContainsKey(pc.PlayerId) && (!Medium.OnlyReceiveMsgFromCrew.GetBool() || pc.IsCrewmate())) AddMsg(string.Format(GetString("MediumNotifyTarget"), Main.AllPlayerNames[Medium.ContactPlayer[pc.PlayerId]]), pc.PlayerId, Utils.ColorString(Utils.GetRoleColor(CustomRoles.Medium), GetString("MediumTitle")));
            if (Virus.VirusNotify.TryGetValue(pc.PlayerId, out string value3)) AddMsg(value3, pc.PlayerId, Utils.ColorString(Utils.GetRoleColor(CustomRoles.Virus), GetString("VirusNoticeTitle")));
            if (Enigma.MsgToSend.TryGetValue(pc.PlayerId, out string value4)) AddMsg(value4, pc.PlayerId, Utils.ColorString(Utils.GetRoleColor(CustomRoles.Enigma), Enigma.MsgToSendTitle[pc.PlayerId]));

            if (QuizMaster.On && QuizMaster.MessagesToSend.TryGetValue(pc.PlayerId, out string value5))
            {
                AddMsg(value5, pc.PlayerId, Utils.ColorString(Utils.GetRoleColor(CustomRoles.QuizMaster), GetString("QuizMaster.QuestionSample.Title")));
                QuizMaster.QuizMasters.Do(x => AddMsg(value5, x.QuizMasterId, Utils.ColorString(Utils.GetRoleColor(CustomRoles.QuizMaster), GetString("QuizMaster.QuestionSample.Title.Self"))));
            }
        }

        if (mimicMsg != string.Empty)
        {
            mimicMsg = GetString("MimicDeadMsg") + "\n" + mimicMsg;

            foreach (PlayerControl ipc in Main.EnumeratePlayerControls())
            {
                if (ipc.GetCustomRole().IsImpostorTeam())
                    AddMsg(mimicMsg, ipc.PlayerId, Utils.ColorString(Utils.GetRoleColor(CustomRoles.Mimic), GetString("MimicMsgTitle")));
            }
        }

        // ⚠️ 会議冒頭の一斉報道はここより「前」に積むこと。下の `msgToSend.Count > 0` は
        // このフレームで即座に評価されるため、ゲートより後で AddMsg しても
        // (他の誰かが1件も積んでいなければ) LateTask 自体がスケジュールされず、無音で消える。
        if (CustomRoles.Newscaster.IsEnable())
        {
            string newscasterBroadcast = Newscaster.BuildBroadcast();

            if (!string.IsNullOrEmpty(newscasterBroadcast))
                AddMsg(newscasterBroadcast, 255, Utils.ColorString(Utils.GetRoleColor(CustomRoles.Newscaster), GetString("NewscasterBroadcastTitle")));
        }

        if (CustomRoles.News.IsEnable())
        {
            string newsBroadcast = News.BuildBroadcast();
            if (!string.IsNullOrEmpty(newsBroadcast))
                AddMsg(newsBroadcast, 255, Utils.ColorString(Utils.GetRoleColor(CustomRoles.News), GetString("News.BroadcastTitle")));
        }

        if (msgToSend.Count > 0)
        {
            LateTask.New(() =>
            {
                if (!AmongUsClient.Instance.AmHost || !GameStates.InGame || GameStates.IsEnded) return;

                // 同一フレームで全通発射するとタイトル付き SetName が3本/frame を超え、バニラ客持ちホストが
                // reason=Hacking で蹴られ得る (会議冒頭は通知が7系統まで積もる)。役職説明と同じ間隔送出に乗せる。
                msgToSend.SendMultipleMessages(MessageImportance.High);
            }, 9f, "Meeting Start Notify");
        }

        Main.SuperStarDead.Clear();
        Forensic.ForensicNotify.Clear();
        Main.SleuthMsgs.Clear();
        Virus.VirusNotify.Clear();
        Mortician.MsgToSend.Clear();
        Enigma.MsgToSend.Clear();

        return;

        void AddMsg(string text, byte sendTo = 255, string title = "") => msgToSend.Add(new(text, sendTo, title));
    }

    public static void Prefix( /*MeetingHud __instance*/)
    {
        Logger.Info("------------Meeting Start------------", "Phase");
        GameStates.AlreadyDied |= !Utils.IsAllAlive;
        ReportDeadBodyPatch.WaitReport.SetAllValues([]);
        MeetingStates.MeetingCalled = true;
        MeetingStates.MeetingNum++;
        CheckForEndVotingPatch.TempExiledPlayer = null;
        CheckForEndVotingPatch.EjectionText = string.Empty;
        SlowStarter.OnMeetingStart();
    }

    public static void Postfix(MeetingHud __instance)
    {
        SoundManager.Instance.ChangeAmbienceVolume(0f);

        BGMManager.SetMeetingBGM();

        GuessManager.TextTemplate = Object.Instantiate(__instance.playerStates[0].NameText);
        GuessManager.TextTemplate.enabled = false;

        PlayerControl seer = PlayerControl.LocalPlayer;

        foreach (PlayerVoteArea pva in __instance.playerStates)
        {
            PlayerControl target = Utils.GetPlayerById(pva.PlayerId);
            if (!target) continue;

            bool shouldSeeTargetAddons = seer.PlayerId == target.PlayerId || new[] { seer, target }.All(x => x.Is(Team.Impostor));
            (string, Color) roleTextData = Utils.GetRoleText(seer.PlayerId, target.PlayerId, seeTargetBetrayalAddons: shouldSeeTargetAddons);
            TextMeshPro roleTextMeeting = Object.Instantiate(pva.NameText, pva.NameText.transform, true);
            roleTextMeeting.transform.localPosition = new(0f, -0.18f, 0f);
            roleTextMeeting.fontSize = 1.4f;
            roleTextMeeting.text = roleTextData.Item1;
            if (Main.VisibleTasksCount) roleTextMeeting.text += Utils.GetProgressText(target);

            roleTextMeeting.color = roleTextData.Item2;
            roleTextMeeting.gameObject.name = "RoleTextMeeting";
            roleTextMeeting.enableWordWrapping = false;

            roleTextMeeting.enabled =
                target.AmOwner ||
                (Main.VisibleTasksCount && !seer.IsAlive() && Options.GhostCanSeeOtherRoles.GetBool()) ||
                (seer.Is(CustomRoles.Mimic) && Main.VisibleTasksCount && !target.IsAlive() && Options.MimicCanSeeDeadRoles.GetBool()) ||
                (target.Is(CustomRoles.Gravestone) && Main.VisibleTasksCount && !target.IsAlive()) ||
                (Main.LoversPlayers.TrueForAll(x => x.PlayerId == target.PlayerId || x.PlayerId == seer.PlayerId) && Main.LoversPlayers.Count == 2 && Lovers.LoverKnowRoles.GetBool()) ||
                (seer.Is(CustomRoleTypes.Coven) && target.Is(CustomRoleTypes.Coven)) ||
                (target.Is(CustomRoleTypes.Impostor) && seer.Is(CustomRoleTypes.Impostor) && Options.ImpKnowAlliesRole.GetBool() && CustomTeamManager.ArentInCustomTeam(seer.PlayerId, target.PlayerId) && !seer.Is(CustomRoles.OneWolf) && !target.Is(CustomRoles.OneWolf)) ||
                (target.Is(CustomRoleTypes.Impostor) && seer.IsMadmate() && Options.MadmateKnowWhosImp.GetBool()) ||
                (target.IsMadmate() && seer.Is(CustomRoleTypes.Impostor) && Options.ImpKnowWhosMadmate.GetBool()) ||
                (target.Is(CustomRoleTypes.Impostor) && seer.Is(CustomRoles.Crewpostor) && Options.AlliesKnowCrewpostor.GetBool()) ||
                (target.Is(CustomRoleTypes.Impostor) && seer.Is(CustomRoles.Hypocrite) && Hypocrite.AlliesKnowHypocrite.GetBool()) ||
                (target.Is(CustomRoles.Crewpostor) && seer.Is(CustomRoleTypes.Impostor) && Options.CrewpostorKnowsAllies.GetBool()) ||
                (target.Is(CustomRoles.Hypocrite) && seer.Is(CustomRoleTypes.Impostor) && Hypocrite.KnowsAllies.GetBool()) ||
                (target.IsMadmate() && seer.IsMadmate() && Options.MadmateKnowWhosMadmate.GetBool()) ||
                ((target.Is(CustomRoles.Jackal) || target.Is(CustomRoles.Sidekick)) && (seer.Is(CustomRoles.Sidekick) || seer.Is(CustomRoles.Jackal))) ||
                (target.Is(CustomRoles.Workaholic) && Workaholic.WorkaholicVisibleToEveryone.GetBool()) ||
                (target.Is(CustomRoles.Doctor) && !target.HasEvilAddon() && Options.DoctorVisibleToEveryone.GetBool()) ||
                (target.Is(CustomRoles.Mayor) && Mayor.MayorRevealWhenDoneTasks.GetBool() && target.GetTaskState().IsTaskFinished) ||
                (target.Is(CustomRoles.Marshall) && Marshall.CanSeeMarshall(seer) && target.GetTaskState().IsTaskFinished) ||
                (Main.PlayerStates[target.PlayerId].deathReason == PlayerState.DeathReason.Vote && Options.SeeEjectedRolesInMeeting.GetBool()) ||
                (CustomTeamManager.AreInSameCustomTeam(target.PlayerId, seer.PlayerId) && CustomTeamManager.IsSettingEnabledForPlayerTeam(target.PlayerId, CTAOption.KnowRoles)) ||
                Main.PlayerStates.Values.Any(x => x.Role.KnowRole(seer, target)) ||
                Markseeker.PlayerIdList.Any(x => Main.PlayerStates[x].Role is Markseeker { IsEnable: true, TargetRevealed: true } ms && ms.MarkedId == target.PlayerId) ||
                seer.IsRevealedPlayer(target) ||
                (seer.Is(CustomRoles.God) && God.KnowInfo.GetValue() == 2) ||
                (seer.Is(CustomRoles.Revenant) && Revenant.KnowInfo.GetValue() == 1) ||
                seer.Is(CustomRoles.GM) ||
                Main.GodMode.Value;


            if (seer.IsAlive() && seer.IsRevealedPlayer(target) && target.Is(CustomRoles.Trickster))
            {
                roleTextMeeting.text = Investigator.RandomRole[seer.PlayerId];
                roleTextMeeting.text += Investigator.GetTaskState();
            }

            if (EvilTracker.IsTrackTarget(seer, target) && EvilTracker.CanSeeLastRoomInMeeting)
            {
                roleTextMeeting.text = EvilTracker.GetArrowAndLastRoom(seer, target);
                roleTextMeeting.enabled = true;
            }

            if (Scout.IsTrackTarget(seer, target) && Scout.CanSeeLastRoomInMeeting)
            {
                roleTextMeeting.text = Scout.GetArrowAndLastRoom(seer, target);
                roleTextMeeting.enabled = true;
            }

            string suffix = Main.PlayerStates[seer.PlayerId].Role.GetSuffix(seer, target, meeting: true);

            if (suffix.Length > 0)
            {
                if (roleTextMeeting.enabled)
                    roleTextMeeting.text += "\n";
                else
                    roleTextMeeting.text = string.Empty;

                roleTextMeeting.text += $"<#ffffff>{suffix}</color>";
                roleTextMeeting.enabled = true;
            }
            
            TextMeshPro deathReasonTextMeeting = Object.Instantiate(pva.NameText, pva.NameText.transform, true);
            deathReasonTextMeeting.transform.localPosition = new(0f, 0.18f, 0f);
            deathReasonTextMeeting.fontSize = 1.4f;
            deathReasonTextMeeting.text = Utils.GetVitalText(target.PlayerId);
            deathReasonTextMeeting.color = Utils.GetRoleColor(CustomRoles.Doctor);
            deathReasonTextMeeting.gameObject.name = "DeathReasonTextMeeting";
            deathReasonTextMeeting.enableWordWrapping = false;
            deathReasonTextMeeting.enabled = seer.KnowDeathReason(target);
            Transform child = deathReasonTextMeeting.transform.FindChild("RoleTextMeeting");
            if (child) Object.Destroy(child.gameObject);

            byte id = pva.PlayerId;

            if (Doppelganger.SwappedIDs.FindFirst(x => x.Item1 == id || x.Item2 == id, out var pair))
            {
                if (pair.Item1 == id) id = pair.Item2;
                else if (pair.Item2 == id) id = pair.Item1;
            }
            else if (Autoscopy.SwappedIDs.FindFirst(x => x.Item1 == id || x.Item2 == id, out var apair))
            {
                if (apair.Item1 == id) id = apair.Item2;
                else if (apair.Item2 == id) id = apair.Item1;
            }

            // Thanks BAU (By D1GQ) - are you happy now?
            Transform playerLevel = pva.transform.Find("PlayerLevel");
            Transform levelDisplay = Object.Instantiate(playerLevel, pva.transform);
            levelDisplay.localPosition = new(-1.21f, -0.15f, playerLevel.transform.localPosition.z);
            levelDisplay.transform.SetSiblingIndex(playerLevel.GetSiblingIndex() + 1);
            levelDisplay.gameObject.name = "PlayerId";
            levelDisplay.GetComponent<SpriteRenderer>().color = Palette.Purple;
            Transform idLabel = levelDisplay.transform.Find("LevelLabel");
            Transform idNumber = levelDisplay.transform.Find("LevelNumber");
            Object.Destroy(idLabel.GetComponent<TextTranslatorTMP>());
            idLabel.GetComponent<TextMeshPro>().text = "ID";
            idNumber.GetComponent<TextMeshPro>().text = id.ToString();
            idLabel.name = "IdLabel";
            idNumber.name = "IdNumber";
        }

        if (AmongUsClient.Instance.AmHost)
        {
            LateTask.New(() =>
            {
                if (!AmongUsClient.Instance.AmHost || !GameStates.InGame || GameStates.IsEnded) return;

                if (Options.SyncButtonMode.GetBool())
                {
                    Utils.SendMessage(string.Format(GetString("Message.SyncButtonLeft"), Options.SyncedButtonCount.GetFloat() - Options.UsedButtonCount));
                    Logger.Info("The ship has " + (Options.SyncedButtonCount.GetFloat() - Options.UsedButtonCount) + " buttons left", "SyncButtonMode");
                }

                TemplateManager.SendTemplate("OnMeeting", noErr: true, importance: MessageImportance.Low);
                if (MeetingStates.FirstMeeting) TemplateManager.SendTemplate("OnFirstMeeting", noErr: true, importance: MessageImportance.Low);
            }, 8f, log: false);

            NotifyRoleSkillOnMeetingStart();
            GhostRumour.SendReveals();

            LateTask.New(() =>
            {
                if (!AmongUsClient.Instance.AmHost || !GameStates.InGame || GameStates.IsEnded) return;

                var sender = CustomRpcSender.Create("RpcSetNameEx on meeting start", SendOption.Reliable);

                foreach (PlayerControl pc in Main.EnumeratePlayerControls())
                {
                    string name = pc.GetRealName(true);

                    foreach (PlayerControl seerPc in Main.EnumeratePlayerControls())
                    {
                        // 直書きリセットで実表示が素名に変わるため、クランプ後 dedup のキャッシュも無効化する
                        // (残すと会議後の装飾名再送が「同一クランプ名」と誤判定されて素名スタックする)
                        try
                        {
                            Main.LastNotifyNames[(pc.PlayerId, seerPc.PlayerId)] = name;
                            Main.LastSentClampedNames.Remove((pc.PlayerId, seerPc.PlayerId));
                        }
                        catch { }
                    }

                    Main.LastSentClampedNames.Remove((pc.PlayerId, byte.MaxValue));

                    sender.AutoStartRpc(pc.NetId, 6);
                    sender.Write(pc.Data.NetId);
                    sender.Write(name);
                    sender.EndRpc();
                }

                sender.SendMessage();
            }, 3f, "SetName To Chat");

            if (Options.UseMeetingShapeshift.GetBool())
            {
                LateTask.New(() =>
                {
                    if (!MeetingHud.Instance || MeetingHud.Instance.state is MeetingHud.MeetingStates.Results or MeetingHud.MeetingStates.Proceeding) return;

                    var aapc = Main.AllAlivePlayerControls;
                    bool restrictions = Options.GuesserNumRestrictions.GetBool();
                    bool meetingSSForGuessing = Options.UseMeetingShapeshiftForGuessing.GetBool();

                    foreach (PlayerControl pc in aapc)
                    {
                        // 木槌ボタンを配ってある客はシェイプシフト付与から外す (両方配ると後勝ちで木槌が消える)
                        if (MeetingTargetPicker.IsHolder(pc.PlayerId)) continue;

                        if (pc.UsesMeetingShapeshift() || (meetingSSForGuessing && !pc.IsModdedClient() && GuessManager.StartMeetingPatch.CanGuess(pc, restrictions)))
                        {
                            var sender = CustomRpcSender.Create($"RpcSetRoleDesync for meeting shapeshift ({Main.AllPlayerNames.GetValueOrDefault(pc.PlayerId, "Someone")})", SendOption.Reliable);
                            sender.RpcSetRole(pc, RoleTypes.Shapeshifter, pc.OwnerId);
                            if (!pc.IsImpostor()) aapc.DoIf(x => x.IsImpostor(), x => sender.RpcSetRole(x, RoleTypes.Crewmate, pc.OwnerId));
                            sender.SendMessage();
                        }
                    }
                }, 8f, "Set Shapeshifter Role For Meeting Use");
            }
        }

        foreach (PlayerVoteArea pva in __instance.playerStates)
        {
            if (!pva) continue;

            PlayerControl target = Utils.GetPlayerById(pva.PlayerId);
            if (!target) continue;

            if (seer.GetCustomRole().GetDYRole() is RoleTypes.Shapeshifter or RoleTypes.Phantom)
            {
                target.cosmetics.SetNameColor(Color.white);
                pva.NameText.color = Color.white;
            }

            StringBuilder sb = new();

            // Name Color Manager
            pva.NameText.text = pva.NameText.text.ApplyNameColorData(seer, target, true);

            CustomRoles seerRole = seer.GetCustomRole();

            switch (seer.GetCustomRoleTypes())
            {
                case CustomRoleTypes.Impostor:
                    if (target.Is(CustomRoles.Snitch) && target.Is(CustomRoles.Madmate) && target.GetTaskState().IsTaskFinished)
                        sb.Append(Utils.ColorString(Utils.GetRoleColor(CustomRoles.Impostor), "★"));

                    break;
                case CustomRoleTypes.Crewmate:
                    if (target.Is(CustomRoles.Marshall) && target.GetTaskState().IsTaskFinished)
                        sb.Append(Utils.ColorString(Utils.GetRoleColor(CustomRoles.Marshall), "★"));

                    sb.Append(Marshall.GetWarningMark(seer, target));
                    break;
            }

            if (seer.IsSnitchTarget()) sb.Append(Snitch.GetWarningMark(seer, target));

            switch (seerRole)
            {
                case CustomRoles.PlagueBearer when PlagueBearer.IsPlagued(seer.PlayerId, target.PlayerId):
                    sb.Append($"<color={Utils.GetRoleColorCode(CustomRoles.PlagueBearer)}>●</color>");
                    break;
                case CustomRoles.Arsonist when seer.IsDousedPlayer(target):
                    sb.Append(Utils.ColorString(Utils.GetRoleColor(CustomRoles.Arsonist), "▲"));
                    break;
                case CustomRoles.EvilTracker:
                    sb.Append(EvilTracker.GetTargetMark(seer, target));
                    break;
                case CustomRoles.Revolutionist when seer.IsDrawPlayer(target):
                    sb.Append(Utils.ColorString(Utils.GetRoleColor(CustomRoles.Revolutionist), "●"));
                    break;
                case CustomRoles.Psychic when Psychic.IsRedForPsy(target, seer) && seer.IsAlive():
                    pva.NameText.text = Utils.ColorString(Utils.GetRoleColor(CustomRoles.Impostor), pva.NameText.text);
                    break;
                case CustomRoles.Demon:
                    sb.Append(Demon.TargetMark(seer, target));
                    sb.Append(Snitch.GetWarningMark(seer, target));
                    break;
                case CustomRoles.Scout:
                    sb.Append(Scout.GetTargetMark(seer, target));
                    break;
            }

            if (Silencer.ForSilencer.Contains(target.PlayerId))
                sb.Append(Utils.ColorString(Utils.GetRoleColor(CustomRoles.Silencer), "╳"));

            if (Main.LoversPlayers.Exists(x => x.PlayerId == target.PlayerId) && (Main.LoversPlayers.Exists(x => x.PlayerId == seer.PlayerId) || !seer.IsAlive()))
                sb.Append(Utils.ColorString(Utils.GetRoleColor(CustomRoles.Lovers), "♥"));

            sb.Append(Executioner.TargetMark(seer, target));
            sb.Append(Witch.GetSpelledMark(target.PlayerId, true));
            sb.Append(Wasp.GetStungMark(target.PlayerId));
            sb.Append(SpellCaster.HasSpelledMark(seer.PlayerId) ? Utils.ColorString(Team.Coven.GetColor(), "\u25c0") : string.Empty);
            sb.Append(Commited.GetMark(seer, target));

            if (target.Is(CustomRoles.SuperStar) && Options.EveryOneKnowSuperStar.GetBool())
                sb.Append(Utils.ColorString(Utils.GetRoleColor(CustomRoles.SuperStar), "★"));

            if (Roles.Lightning.IsGhost(target))
                sb.Append(Utils.ColorString(Utils.GetRoleColor(CustomRoles.Lightning), "■"));

            if (seer.PlayerId == target.PlayerId && (Medic.InProtect(seer.PlayerId) || Medic.TempMarkProtectedList.Contains(seer.PlayerId)) && Medic.WhoCanSeeProtect.GetInt() is 0 or 2)
                sb.Append(Utils.ColorString(Utils.GetRoleColor(CustomRoles.Medic), " ●"));

            if (seer.Is(CustomRoles.Medic) && (Medic.InProtect(target.PlayerId) || Medic.TempMarkProtectedList.Contains(target.PlayerId)) && Medic.WhoCanSeeProtect.GetInt() is 0 or 1)
                sb.Append(Utils.ColorString(Utils.GetRoleColor(CustomRoles.Medic), " ●"));

            if (!seer.IsAlive() && Medic.InProtect(target.PlayerId) && !seer.Is(CustomRoles.Medic))
                sb.Append(Utils.ColorString(Utils.GetRoleColor(CustomRoles.Medic), " ●"));

            if (seer.PlayerId == target.PlayerId && Main.PlayerStates[seer.PlayerId].Role is CovenBase { HasNecronomicon: true })
                sb.Append($" <{Main.CovenColor}>♤</color>");

            sb.Append(Follower.TargetMark(seer, target));
            sb.Append(Romantic.TargetMark(seer, target));
            sb.Append(Lawyer.LawyerMark(seer, target));
            sb.Append(Infection.GetMarkOthers(seer, target));
            sb.Append(Gaslighter.GetMark(seer, target, true));

            pva.NameText.text += sb.ToString();
            pva.ColorBlindName.transform.localPosition -= new Vector3(1.35f, 0f, 0f);

            if (Magistrate.CallCourtNextMeeting)
            {
                string name = target.Is(CustomRoles.Magistrate) ? GetString("Magistrate.CourtName") : GetString("Magistrate.JuryName");
                pva.NameText.text = name;
            }
        }

        // -------------------------------------------------------------------------------------------

        CovenBase.CovenMeetingStartPatch.Postfix();
        GuessManager.StartMeetingPatch.Postfix(__instance);
        Inspector.StartMeetingPatch.Postfix(__instance);
        Judge.StartMeetingPatch.Postfix(__instance);
        MeetingTargetPicker.OnMeetingStart(); // 非モッド客へ木槌ボタンを配る (MeetingHud 生成後に配るのが安全な順序)
        EkrMeetingButton.StartMeetingPatch(__instance); // Wave 2
        Swapper.StartMeetingPatch.Postfix(__instance);
        Councillor.StartMeetingPatch.Postfix(__instance);
        Nemesis.StartMeetingPatch.Postfix(__instance);
        Imitator.StartMeetingPatch.Postfix(__instance);
        Retributionist.StartMeetingPatch.Postfix(__instance);
        Starspawn.StartMeetingPatch.Postfix(__instance);
        Ventriloquist.StartMeetingPatch.Postfix(__instance);
        Changeling.StartMeetingPatch.Postfix(__instance);
        Markseeker.StartMeetingPatch.Postfix(__instance);
        ShowHostMeetingPatch.Setup_Postfix(__instance);
        Crowded.MeetingHudStartPatch.Postfix(__instance);
        Talkative.StartMeetingPatch.Postfix();

        ChaosPotSupport.BuildRoleListFromCurrentGame();
        if (ChaosPotSupport.Enable.GetBool() && Options.CurrentGameMode == CustomGameMode.Standard)
        {
            string meetingText = ChaosPotSupport.GetMeetingScreenText();
            if (!string.IsNullOrEmpty(meetingText) && __instance.playerStates.Length > 0)
            {
                PlayerVoteArea anchor = __instance.playerStates[0];
                TextMeshPro chaosPotInfo = Object.Instantiate(anchor.NameText, anchor.PlayerIcon.transform, true);
                chaosPotInfo.gameObject.SetActive(false);
                chaosPotInfo.gameObject.name = "ChaosPotInfo";
                chaosPotInfo.transform.localPosition = new(3.13f, 1.71f, 0f);
                chaosPotInfo.transform.localScale = Vector3.one;
                chaosPotInfo.fontSize = 1.5f;
                chaosPotInfo.alignment = TextAlignmentOptions.TopLeft;
                chaosPotInfo.enableWordWrapping = false;
                chaosPotInfo.color = Color.white;
                chaosPotInfo.text = meetingText;
                chaosPotInfo.ForceMeshUpdate();
                chaosPotInfo.gameObject.SetActive(true);
            }

            // 会議冒頭通知 (Meeting Start Notify, 9f) と同一 tick に重ねない — 先頭1通同士がフレームを共有するため
            LateTask.New(ChaosPotSupport.SendChatBroadcastOnMeetingStart, 9.3f, "ChaosPotSupport Chat");
        }

        TextBoxPatch.OnMeetingStart();

        if (AmongUsClient.Instance.AmHost && Options.MeetingAutoOpenChat.GetBool())
            LateTask.New(() =>
            {
                if (!HudManager.InstanceExists || !HudManager.Instance.Chat) return;
                ChatController chat = HudManager.Instance.Chat;
                chat.SetVisible(true);
                // SetVisible はチャットUIを可視化するだけでパネルは開かない。閉じていれば Toggle で展開する。
                if (chat.IsClosedOrClosing) chat.Toggle();
            }, 0.5f, "StreamerAutoChat", log: false);
    }
}

[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Update))]
internal static class MeetingHudUpdatePatch
{
    private static int BufferTime = 10;

    private static void ClearShootButton(MeetingHud __instance, bool forceAll = false)
    {
        __instance.playerStates.DoIf(
            x => (forceAll || !Main.PlayerStates.TryGetValue(x.PlayerId, out PlayerState ps) || ps.IsDead) && x.transform.FindChild("ShootButton"),
            x => Object.Destroy(x.transform.FindChild("ShootButton").gameObject));
    }

    public static bool Prefix(MeetingHud __instance)
    {
        if (__instance.CurrentState != MeetingHud.MeetingStates.Results) return true;

        // Results 遷移直後の残り投票秒を one-shot でラッチ。
        // 直下の UpdateTimerText(MeetingProceeds) が VotingTimeLeft を 5→0 で上書きするため、
        // MeetingHud.OnDestroy 時点の値では「時間切れ終了か全員投票の早期終了か」を区別できない。
        MeetingSilentProbe.OnResultsPhase();

        __instance.discussionTimer += Time.deltaTime;

        float num4 = __instance.discussionTimer - __instance.resultsStartedAt;
        float num5 = Mathf.Max(0f, 5f - num4);
        __instance.UpdateTimerText(StringNames.MeetingProceeds, Mathf.CeilToInt(num5));

        if (AmongUsClient.Instance.AmHost && num5 <= 0f)
        {
            MeetingHudRpcClosePatch.AllowClose = true;
            __instance.state = MeetingHud.MeetingStates.Proceeding;
            __instance.RpcClose();
        }

        return false;
    }

    public static void Postfix(MeetingHud __instance)
    {
        try
        {
            MeetingStuckProbe.Update(__instance);
            MeetingSilentProbe.Update(__instance);

            // EKR logic: 会議中は RoleBase.OnFixedUpdate が止まるため、fiber はここから進める
            // (on_meeting_start の notify [チャット私信] を会議中に届かせる)。
            if (AmongUsClient.Instance.AmHost) EkrManager.PumpMeetingFibers();

            // Meeting Skip with vote counting on keystroke (F6)
            if (AmongUsClient.Instance.AmHost && Input.GetKeyDown(KeyCode.F6)) __instance.CheckForEndVoting();

            if (AmongUsClient.Instance.AmHost && Input.GetMouseButtonUp(1) && Input.GetKey(KeyCode.LeftControl))
            {
                __instance.playerStates.DoIf(x => x.HighlightedFX.enabled, x =>
                {
                    PlayerControl player = Utils.GetPlayerById(x.PlayerId);

                    if (player && player.IsAlive())
                    {
                        Main.PlayerStates[player.PlayerId].deathReason = PlayerState.DeathReason.Execution;
                        player.RpcExileV2();
                        Main.PlayerStates[player.PlayerId].SetDead();
                        Utils.AfterPlayerDeathTasks(player, true);
                        Utils.SendMessage(string.Format(GetString("Message.Executed"), player.Data.PlayerName));
                        Logger.Info($"{player.GetNameWithRole().RemoveHtmlTags()} was executed by the host", "Execution");
                        __instance.CheckForEndVoting();
                    }
                });
            }

            if (!GameStates.IsVoting && __instance.lastSecond < 1)
            {
                if (GameObject.Find("ShootButton") != null)
                    ClearShootButton(__instance, true);

                return;
            }

            BufferTime--;

            if (BufferTime < 0 && __instance.discussionTimer > 0)
            {
                BufferTime = 10;
                CustomRoles myRole = PlayerControl.LocalPlayer.GetCustomRole();

                //__instance.playerStates.Where(x => (!Main.PlayerStates.TryGetValue(x.PlayerId, out PlayerState ps) || ps.IsDead) && !x.AmDead).Do(x => x.SetDead(x.DidReport, true));

                switch (myRole)
                {
                    case CustomRoles.NiceGuesser or CustomRoles.EvilGuesser or CustomRoles.Judge or CustomRoles.Swapper or CustomRoles.Councillor or CustomRoles.Guesser when !PlayerControl.LocalPlayer.IsAlive():
                        ClearShootButton(__instance, true);
                        break;
                    case CustomRoles.Nemesis when !PlayerControl.LocalPlayer.IsAlive() && !GameObject.Find("ShootButton"):
                        Nemesis.CreateJudgeButton(__instance);
                        break;
                    // Wave 2: EKR ホルダーが会議中に死亡したら他役職と
                    // 同じ規約でボタンを強制掃除する (会議を跨いで残らないよう)。掃除側は生成側
                    // (HasOnMeetingPickLogic 込み) より広く取る (掃除条件が生成条件より狭いと取りこぼすため)。
                    case var r when EkrManager.IsEkrRole(r) && !PlayerControl.LocalPlayer.IsAlive():
                        ClearShootButton(__instance, true);
                        break;
                }

                ClearShootButton(__instance);
            }
        }
        catch (Exception ex)
        {
            Logger.Fatal(ex.ToString(), "MeetingHudUpdatePatch.Postfix");
            Logger.Warn("All Players and their info:", "Debug for Fatal Error");
            foreach (PlayerControl pc in Main.EnumeratePlayerControls()) Logger.Info($" {(pc.IsAlive() ? "Alive" : $"Dead ({Main.PlayerStates[pc.PlayerId].deathReason})")}, {Utils.GetProgressText(pc)}, {Utils.GetVitalText(pc.PlayerId)}", $"{pc.GetNameWithRole()} / {pc.PlayerId}");

            Logger.Warn("-----------------", "Debug for Fatal Error");
            Logger.SendInGame("An error occured with this meeting. Please use /dump and send the log to the developer.\nSorry for the inconvenience.", Color.red);
        }
    }
}

[HarmonyPatch(typeof(PlayerVoteArea), nameof(PlayerVoteArea.SetHighlighted))]
internal static class SetHighlightedPatch
{
    public static bool Prefix(PlayerVoteArea __instance, bool value)
    {
        if (!AmongUsClient.Instance.AmHost) return true;

        if (!__instance.HighlightedFX) return false;

        __instance.HighlightedFX.enabled = value;
        return false;
    }
}

[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.OnDestroy))]
internal static class MeetingHudOnDestroyPatch
{
    public static void Postfix(MeetingHud __instance)
    {
        try
        {
            if (__instance)
            {
                (int l, int vc, int m, int mats) = GameSettingMenuPatch.PurgeUnityEventListeners(__instance.gameObject);
                Modules.HealthLog.Note($"UIPURGE src=meeting listeners={l} valueChanged={vc} menus={m} mats={mats} t={Utils.TimeStamp}");
            }
        }
        catch (Exception e) { Logger.Warn($"meeting hud purge failed: {e.Message}", "MenuLeak"); }

        Logger.Info("------------End of meeting------------", "Phase");

        MeetingStates.FirstMeeting = false;
        ReportDeadBodyPatch.MeetingStarted = false;
        CheckForEndVotingPatch.ShouldSkip = false;

        // 会議中 write-barrier 解放 (会議中の自動 Data は TOHK 式に破棄済み — ここでのフラッシュは無い)
        NetworkedPlayerInfoSerializePatch.OnMeetingEnd();
        MeetingSilentProbe.OnMeetingEnd();

        if (!GameStates.InGame) return;

        BGMManager.SetTaskBGM();
        
        if (AmongUsClient.Instance.AmHost)
        {
            GameEndChecker.ShouldNotCheck = true;
            LateTask.New(() => GameEndChecker.ShouldNotCheck = false, 15f, "Re-enable GameEndChecker after meeting");
            
            bool meetingSS = Options.UseMeetingShapeshift.GetBool();
            bool meetingSSForGuessing = Options.UseMeetingShapeshiftForGuessing.GetBool();

            if (meetingSS && meetingSSForGuessing)
            {
                GuessManager.Data.Values.Do(x => x.Reset());
                GuessManager.Data.Clear();
            }

            AntiBlackout.SetOptimalRoleTypes();
            RandomSpawn.CustomNetworkTransformHandleRpcPatch.HasSpawned.Clear();

            Main.LastVotedPlayerInfo = null;

            if (meetingSS && !AntiBlackout.SkipTasks)
            {
                bool restrictions = Options.GuesserNumRestrictions.GetBool();

                foreach (PlayerControl pc in Main.EnumerateAlivePlayerControls())
                {
                    // 木槌ボタン側の保持者は MeetingTargetPicker.AfterMeeting が戻すので、ここでは触らない
                    if (!MeetingTargetPicker.IsHolder(pc.PlayerId) &&
                        (pc.UsesMeetingShapeshift() || (meetingSSForGuessing && !pc.IsModdedClient() && GuessManager.StartMeetingPatch.CanGuess(pc, restrictions))))
                        pc.RpcSetRoleDesync(pc.GetRoleTypes(), pc.OwnerId);

                    if (pc.IsImpostor())
                        pc.RpcSetRoleGlobal(pc.GetRoleTypes());
                }
            }
        }

        if (Main.LIMap) Main.Instance.StartCoroutine(WaitForExileFinish());

        // 毎会議終了の強制GC三連打はゲーム中の音声スタッター源 (回収実績ほぼゼロ) — 既定OFF
        if (Main.EnableAggressiveGcCleanup.Value)
        {
            GC.Collect();
            Resources.UnloadUnusedAssets();
            GC.Collect();
        }

        return;

        IEnumerator WaitForExileFinish()
        {
            while (!ExileController.Instance && !GameStates.IsEnded) yield return null;
            if (GameStates.IsEnded) yield break;

            yield return new WaitForSecondsRealtime(1f);
            if (!ExileController.Instance || GameStates.IsEnded) yield break;
            
            if (CheckForEndVotingPatch.EjectionText.EndsWith("<size=0>") && Options.CurrentGameMode is CustomGameMode.Standard or CustomGameMode.TheMindGame && CheckForEndVotingPatch.TempExiledPlayer != null)
                ExileController.Instance.completeString = CheckForEndVotingPatch.EjectionText[..^8];
            
            while (ExileController.Instance) yield return null;

            try { ExileControllerWrapUpPatch.WrapUpPostfix(CheckForEndVotingPatch.TempExiledPlayer); }
            finally { ExileControllerWrapUpPatch.WrapUpFinalizer(); }
        }
    }
}

// AU 2026.8.18: CastVote の引数が InnerNet.PlayerId (struct) 化され、interop 生成メソッドの
// メタデータと Harmony DMD の相性で「struct 引数メソッドへのパッチ」が InvalidProgramException になる。
// そのため CastVote 自体にはパッチせず、ホストに票が届く全入口 (ホスト自票=CmdCastVote /
// クライアント票=HandleRpc(RpcCalls.CastVote) / mod 内直呼び) で CastVoteChecked を通し、
// 許可された票だけ proxy の CastVote (パッチ非経由) に流す方式へ移設。
[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.CmdCastVote))]
internal static class MeetingHudCastVotePatch
{
    public static bool Prefix(MeetingHud __instance, [HarmonyArgument(0)] byte playerId, [HarmonyArgument(1)] byte suspectIdx)
    {
        if (!AmongUsClient.Instance.AmHost) return true;

        byte suspectPlayerId = ResolveSuspectIdx(__instance, suspectIdx);
        Logger.Info($"CmdCastVote: src={playerId}, suspectIdx={suspectIdx} => suspect={suspectPlayerId}", "MeetingHudCastVotePatch");
        CastVoteChecked(__instance, playerId, suspectPlayerId);
        return false;
    }

    // Confirm(suspectStateIdx) 由来の index を playerId へ解決する。範囲外は skip(253) 等の番兵値がそのまま来た形とみなす
    public static byte ResolveSuspectIdx(MeetingHud meetingHud, byte suspectIdx)
    {
        PlayerVoteArea[] states = meetingHud.playerStates;
        if (states != null && suspectIdx < states.Length) return states[suspectIdx].PlayerId;
        return suspectIdx;
    }

    // 全入口共通: EHR の投票判定を通し、許可された票だけバニラの CastVote (proxy 直呼び・DMD 非経由) に流す
    public static void CastVoteChecked(MeetingHud meetingHud, byte srcPlayerId, byte suspectPlayerId)
    {
        bool proceed = HandleCastVote(meetingHud, srcPlayerId, suspectPlayerId, out (MeetingHud MeetingHud, PlayerVoteArea SourcePVA, PlayerControl SourcePC)? cancelInfo);
        if (proceed) meetingHud.CastVote(srcPlayerId, suspectPlayerId);
        if (cancelInfo.HasValue) CleanupCanceledVote(cancelInfo.Value);
    }

    private static bool HandleCastVote(MeetingHud __instance, byte srcPlayerId, byte suspectPlayerId, out (MeetingHud MeetingHud, PlayerVoteArea SourcePVA, PlayerControl SourcePC)? cancelInfo)
    {
        cancelInfo = null;

        MeetingSilentProbe.OnCastVote(srcPlayerId);

        PlayerVoteArea pvaSrc = null;
        PlayerVoteArea pvaTarget = null;
        var skip = false;

        foreach (PlayerVoteArea t in __instance.playerStates)
        {
            if (t.PlayerId == srcPlayerId) pvaSrc = t;
            if (t.PlayerId == suspectPlayerId) pvaTarget = t;
        }

        if (!pvaSrc)
        {
            Logger.Error("Src PlayerVoteArea not found", "MeetingHudCastVotePatch.Prefix");
            return true;
        }

        if (!pvaTarget)
        {
            //Logger.Warn("Target PlayerVoteArea not found ⇒ Vote treated as a Skip", "MeetingHudCastVotePatch.Prefix");
            skip = true;
        }

        PlayerControl pcSrc = Utils.GetPlayerById(srcPlayerId);
        PlayerControl pcTarget = Utils.GetPlayerById(suspectPlayerId);

        if (!pcSrc)
        {
            Logger.Error("Src PlayerControl is null", "MeetingHudCastVotePatch.Prefix");
            return true;
        }

        if (!pcTarget)
        {
            //Logger.Warn("Target PlayerControl is null ⇒ Vote treated as a Skip", "MeetingHudCastVotePatch.Prefix");
            skip = true;
        }

        if (!pcSrc.IsAlive())
        {
            cancelInfo = (__instance, pvaSrc, pcSrc);
            return false;
        }

        if (skip && pcSrc.Is(CustomRoles.Elector))
        {
            cancelInfo = (__instance, pvaSrc, pcSrc);
            Utils.SendMessage(Translator.GetString("ElectorCancelMessage"), srcPlayerId);
            Logger.Info($"{pcSrc.GetNameWithRole()} skip vote blocked by Elector", "Vote");
            return false;
        }

        if (Options.FirstTurnMeeting.GetBool() && Options.FirstTurnMeetingNoVote.GetBool() && MeetingStates.FirstMeeting)
        {
            cancelInfo = (__instance, pvaSrc, pcSrc);
            Utils.SendMessage(Translator.GetString("FirstTurnMeetingNoVoteMessage"), srcPlayerId);
            Logger.Info($"{pcSrc.GetNameWithRole()} vote blocked by FirstTurnMeetingNoVote", "Vote");
            return false;
        }

        // Balancer 会議中は T1/T2 以外 (スキップ含む) への投票を cast 時に拒否する (TOHK CheckVoteAsVoter 準拠)。
        // tally 側の Balancer.ManipulateVotingResult がリダイレクトで結果は担保するが、投票拒否のフィードバックを出すため。
        if (Balancer.ShouldCancelVote(pcTarget))
        {
            cancelInfo = (__instance, pvaSrc, pcSrc);
            Utils.SendMessage(Translator.GetString("BalancerVoteRestricted"), srcPlayerId);
            Logger.Info($"{pcSrc.GetNameWithRole()} vote restricted by Balancer meeting", "Vote");
            return false;
        }

        var voteCanceled = false;

        var firstTurnNoAbility = Options.FirstTurnMeeting.GetBool() && Options.FirstTurnMeetingCantAbility.GetBool() && MeetingStates.FirstMeeting;

        if (!firstTurnNoAbility && !Main.DontCancelVoteList.Contains(srcPlayerId) && !skip && pcSrc.GetCustomRole().CancelsVote() && !pcSrc.UsesMeetingShapeshift() && Main.PlayerStates[srcPlayerId].Role.OnVote(pcSrc, pcTarget))
        {
            cancelInfo = (__instance, pvaSrc, pcSrc);
            voteCanceled = true;
        }

        if (Silencer.ForSilencer.Contains(srcPlayerId))
        {
            cancelInfo = (__instance, pvaSrc, pcSrc);
            voteCanceled = true;
        }

        // void に落とされた人はこの会議のあいだ投票できない。理由を返さないと
        // 「票が黙って消える」だけになりバグに見えるため、必ずメッセージを出す。
        if (Shuffler.IsInVoid(srcPlayerId))
        {
            cancelInfo = (__instance, pvaSrc, pcSrc);
            Utils.SendMessage(Translator.GetString("Shuffler.VoteBlocked"), srcPlayerId);
            voteCanceled = true;
        }

        Logger.Info($"{pcSrc.GetNameWithRole()} => {(skip ? "Skip" : pcTarget.GetNameWithRole())}{(voteCanceled ? " (Canceled)" : string.Empty)}", "Vote");

        return skip || !voteCanceled; // return false to use the vote as a trigger; skips and invalid votes are never canceled
    }

    // 旧 Postfix 相当: キャンセルされた票のクライアント側 UI (投票ロック) を解除する
    private static void CleanupCanceledVote((MeetingHud MeetingHud, PlayerVoteArea SourcePVA, PlayerControl SourcePC) info)
    {
        try
        {
            info.SourcePVA.UnsetVote();
            info.MeetingHud.SetDirtyBit(1U);
            AmongUsClient.Instance.SendAllStreamedObjects();
        }
        catch { }

        info.MeetingHud.RpcClearVote(info.SourcePC.PlayerId);
        info.MeetingHud.SetDirtyBit(1U);
        AmongUsClient.Instance.SendAllStreamedObjects();

        info.SourcePVA.VotedForId = byte.MaxValue;

        Logger.Info($"Vote for {info.SourcePC.GetNameWithRole()} canceled", "MeetingHudCastVotePatch.CleanupCanceledVote");
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcSendChatNote))]
static class SendChatNotePatch
{
    public static bool Prefix()
    {
        return !Options.DisablePlayerVotedMessage.GetBool();
    }
}

// Next 2 are from: https://github.com/xChipseq/VanillaEnhancements/blob/main/VanillaEnhancements/Patches/MeetingPatches.cs

[HarmonyPatch(typeof(PlayerVoteArea), nameof(PlayerVoteArea.SetCosmetics))]
[HarmonyPriority(Priority.First)]
static class NamePlateDarkThemePatch
{
    public static void Postfix(PlayerVoteArea __instance)
    {
        if (Main.DarkThemeForMeetingUI.Value)
            __instance.Background.color = new Color(0.1f, 0.1f, 0.1f);
    }
}

[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
static class MeetingHud_Start
{
    public static void Postfix(MeetingHud __instance)
    {
        if (!Main.DarkThemeForMeetingUI.Value) return;
        __instance.meetingContents.transform.FindChild("PhoneUI").FindChild("baseColor").GetComponent<SpriteRenderer>().color = new Color(0.01f, 0.01f, 0.01f);
        __instance.Glass.color = new Color(0.7f, 0.7f, 0.7f, 0.3f);
        __instance.SkipVoteButton.GetComponent<SpriteRenderer>().color = new Color(0.4f, 0.4f, 0.4f);

        foreach (SpriteRenderer playerMaterialColors in __instance.PlayerColoredParts)
        {
            playerMaterialColors.color = new Color(0.25f, 0.25f, 0.25f);
            PlayerMaterial.SetColors(7, playerMaterialColors);
        }
    }
}

// All below are from: https://github.com/EnhancedNetwork/TownofHost-Enhanced (with some modifications)

[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.RpcClose))]
internal static class MeetingHudRpcClosePatch
{
    public static bool AllowClose;

    // 2026-07-23〜25: 会議クローズと同一秒の公式鯖 Hacking キックの機序を実機 1-bit で確定。
    //   犯人 = vanilla CloseMeeting RPC への trailing EjectionText ペイロード付加 (これ単独でキック)。
    //   無罪 = 追放者への SetName 偽装 (色付き長名)。trailing だけ除いた追放クローズ×2 が無キック通過。
    // ⇒ trailing を送らなければ追放演出 (色付き名前) は公式鯖でも出せる。以下はその形 (実機検証済みのワイヤ)。
    // 上流 EHR の PR #657/#658 は ServerType 判定で演出ごと無効化する下位互換解 (SetName 偽装も捨てている)。
    // ⚠️ 上流 #658 の RpcSetName 化は採用しない — 会議中 Data write-barrier に discard され、
    //    かつ RpcSetName Postfix の名前キャッシュミラーで偽装文が「本名」として残るため。
    // 切り戻し: EndKnot_DATA/disable_ehr_close.txt を置くと vanilla パリティ (素通し) に戻る (再起動不要)。
    private static bool _ehrCloseDisabled;
    private static float _ehrCloseCheckedAt = float.MinValue;

    // SetName 偽装に載せられる EjectionText の上限 (UTF-8 バイト)。SetName の公式鯖上限は
    // タグ依存で ~800B 実測。長い役職名 + アドオン + 日本語で後日超えるのを避けるため安全側に置く。
    // 超過時は偽装を丸ごとスキップ (そのラウンドだけ vanilla 追放画面に劣化) — 途中クランプはタグを壊す。
    private const int MaxEjectionNameBytes = 500;

    private static bool EhrCloseEnabled
    {
        get
        {
            if (Time.realtimeSinceStartup - _ehrCloseCheckedAt > 30f)
            {
                _ehrCloseCheckedAt = Time.realtimeSinceStartup;
                bool exists;
                try { exists = System.IO.File.Exists($"{Main.DataPath}/EndKnot_DATA/disable_ehr_close.txt"); }
                catch { exists = false; }
                if (exists != _ehrCloseDisabled) Logger.Warn($"EHR-style meeting close {(exists ? "DISABLED (disable_ehr_close.txt present) — vanilla-parity RpcClose (TOHK parity)" : "enabled — SetName spoof only, no CloseMeeting trailing payload")}", "MeetingHudRpcClosePatch");
                _ehrCloseDisabled = exists;
            }

            return !_ehrCloseDisabled;
        }
    }

    public static bool Prefix(MeetingHud __instance)
    {
        Logger.Info("MeetingHud.RpcClose is being called", "MeetingHudRpcClosePatch");

        if (!AllowClose)
        {
            Logger.Fatal("MeetingHud.RpcClose called when AllowClose is false!", "MeetingHudRpcClosePatch");
            EAC.WarnHost(4);
            return false;
        }

        AllowClose = false;

        // 既定が EHR 式になったので、どちらの経路を通ったかは毎クローズ記録する
        // (差分検知の Warn だけだと既定側が無記録になり、実機検証で読むものが無くなる)。
        bool ehrClose = EhrCloseEnabled;
        Logger.Info($"Meeting close path: {(ehrClose ? "EHR (SetName spoof, no trailing payload)" : "vanilla passthrough (disable_ehr_close.txt)")}", "MeetingHudRpcClosePatch");

        if (!ehrClose) return true;

        if (Options.CurrentGameMode is CustomGameMode.Standard or CustomGameMode.TheMindGame)
        {
            if (AmongUsClient.Instance.AmClient)
                __instance.Close();

            MessageWriter writer = MessageWriter.Get(SendOption.Reliable);

            writer.StartMessage(5);
            writer.Write(AmongUsClient.Instance.GameId);

            if (CheckForEndVotingPatch.TempExiledPlayer != null)
            {
                NetworkedPlayerInfo info = CheckForEndVotingPatch.TempExiledPlayer;
                PlayerControl player = info.Object;

                if (player != null)
                {
                    string ejectionText = CheckForEndVotingPatch.EjectionText;
                    int bytes = string.IsNullOrEmpty(ejectionText) ? 0 : System.Text.Encoding.UTF8.GetByteCount(ejectionText);
                    Logger.Info($"Ejection name spoof: {bytes}B (limit {MaxEjectionNameBytes}B)", "MeetingHudRpcClosePatch");

                    if (bytes > MaxEjectionNameBytes)
                        Logger.Warn($"Ejection name spoof SKIPPED — EjectionText is {bytes}B (> {MaxEjectionNameBytes}B); falling back to the vanilla ejection screen for this round", "MeetingHudRpcClosePatch");
                    else
                    {
                        writer.StartMessage(2);
                        writer.WritePacked(player.NetId);
                        writer.Write((byte)RpcCalls.SetName);
                        writer.Write(info.NetId);
                        writer.Write(ejectionText);
                        writer.EndMessage();
                    }
                }
            }

            // ⚠️ ここに EjectionText を trailing で付けてはならない (公式鯖 Hacking 即キックの真因・実機確定)。
            // vanilla と byte 等価な CloseMeeting を送る。
            writer.StartMessage(2);
            writer.WritePacked(__instance.NetId);
            writer.Write((byte)RpcCalls.CloseMeeting);
            writer.EndMessage();

            writer.EndMessage();
            AmongUsClient.Instance.SendOrDisconnect(writer);
            writer.Recycle();

            return false;
        }

        return true;
    }
}

[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.HandleRpc))]
internal static class MeetingHudHandleRpcPatch
{
    public static bool Prefix(MeetingHud __instance, [HarmonyArgument(0)] byte callId, [HarmonyArgument(1)] MessageReader reader)
    {
        // AU 2026.8.18: CastVote メソッドへの直接パッチが不可能になったため、クライアント票は
        // ここで受けて CastVoteChecked (EHR 判定 + バニラ CastVote) へ流し、オリジナルには渡さない
        if (callId == (byte)RpcCalls.CastVote && AmongUsClient.Instance.AmHost)
        {
            byte srcPlayerId = reader.ReadByte();
            byte suspectRaw = reader.ReadByte();
            Logger.Info($"HandleRpc CastVote: src={srcPlayerId}, suspectRaw={suspectRaw}, remaining={reader.BytesRemaining}", "MeetingHudHandleRpcPatch");
            MeetingHudCastVotePatch.CastVoteChecked(__instance, srcPlayerId, suspectRaw);
            return false;
        }

        // 会議 UI の木槌ボタン (バニラ Judge basis の借用) 押下。読み取り位置を戻してオリジナルには素通しする
        // — ホストが握り潰すと客のローカル queue に残るため。
        if (callId == (byte)RpcCalls.QueueOverruleVotes && AmongUsClient.Instance.AmHost)
        {
            try
            {
                int pos = reader.Position;
                byte judgeId = reader.ReadByte();
                byte targetId = reader.ReadByte();
                reader.Position = pos;

                MeetingTargetPicker.OnOverrulePressed(judgeId, targetId);
            }
            catch (Exception e) { Utils.ThrowException(e); }
        }

        if (callId == (byte)RpcCalls.CloseMeeting)
        {
            if (AmongUsClient.Instance.AmHost)
            {
                EAC.WarnHost(4);
                Logger.Warn("MeetingHud.HandleRpc CloseMeeting is being called, impossible to receive as host.", "MeetingHudHandleRpcPatch");
                return false;
            }

            Logger.Info("Received Close Meeting Rpc", "MeetingHudHandleRpcPatch");

            if (reader.BytesRemaining > 6)
            {
                try
                {
                    string temp = reader.ReadString();

                    if (temp.Contains("<size"))
                    {
                        Logger.Info($"Read Name From Rpc: {temp}", "MeetingHudHandleRpcPatch");
                        CheckForEndVotingPatch.EjectionText = temp;
                    }
                }
                catch { }
            }
        }

        return true;
    }
}

[HarmonyPatch(typeof(ExileController), nameof(ExileController.Begin))]
internal static class ExileControllerBeginPatch
{
    public static void Postfix(ExileController __instance, [HarmonyArgument(0)] ExileController.InitProperties init)
    {
        if (CheckForEndVotingPatch.EjectionText.EndsWith("<size=0>") && Options.CurrentGameMode is CustomGameMode.Standard or CustomGameMode.TheMindGame && init is { outfit: not null })
            __instance.completeString = CheckForEndVotingPatch.EjectionText[..^8];
    }
}