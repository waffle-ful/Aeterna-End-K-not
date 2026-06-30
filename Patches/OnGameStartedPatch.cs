using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.Data;
using AmongUs.GameOptions;
using Assets.CoreScripts;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using EndKnot.Gamemodes;
using EndKnot.Modules;
using EndKnot.Patches;
using EndKnot.Roles;
using HarmonyLib;
using Hazel;
using Il2CppSystem.Collections;
using InnerNet;
using UnityEngine;
using UnityEngine.UI;
using static EndKnot.Modules.CustomRoleSelector;
using static EndKnot.Translator;
using DateTime = Il2CppSystem.DateTime;
using Exception = System.Exception;

namespace EndKnot;

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.CoStartGame))]
internal static class ChangeRoleSettings
{
    public static bool BlockPopulateSkins;

    public static bool Prefix(AmongUsClient __instance, ref IEnumerator __result)
    {
        if (!GameStates.IsLocalGame || !HudManager.InstanceExists) return true;

        __result = CoStartGame().WrapToIl2Cpp();
        return false;

        IEnumerator<object> CoStartGame()
        {
            if (HudManager.Instance.GameMenu.IsOpen)
                HudManager.Instance.GameMenu.Close();

            UnityTelemetry.Instance.Init();
            __instance.logger.Info("Received game start: " + __instance.AmHost);
            yield return null;

            while (!HudManager.InstanceExists)
                yield return null;

            while (!PlayerControl.LocalPlayer)
                yield return null;

            PlayerControl.LocalPlayer.moveable = false;
            PlayerControl.LocalPlayer.MyPhysics.inputHandler.enabled = true;
            var objectOfType1 = Object.FindObjectOfType<PlayerCustomizationMenu>();

            if (objectOfType1)
                objectOfType1.Close(false);

            var objectOfType2 = Object.FindObjectOfType<GameSettingMenu>();

            if (objectOfType2)
                objectOfType2.Close();

            if (GameStartManager.InstanceExists)
            {
                __instance.DisconnectHandlers.Remove(GameStartManager.Instance.CastFast<IDisconnectHandler>());
                Object.Destroy(GameStartManager.Instance.gameObject);
            }

            if (LobbyInfoPane.InstanceExists)
                Object.Destroy(LobbyInfoPane.Instance.gameObject);

            if (DiscordManager.InstanceExists)
                DiscordManager.Instance.SetPlayingGame();

            if (!string.IsNullOrEmpty(DataManager.Player.Store.ActiveCosmicube))
                AmongUsClient.Instance.SetActivePodType(CosmicubeManager.Instance.GetCubeDataByID(DataManager.Player.Store.ActiveCosmicube).podId);
            else
            {
                PlayerStorageManager.CloudPlayerPrefs playerPrefs = PlayerStorageManager.Instance.PlayerPrefs;
                AmongUsClient.Instance.SetActivePodType(playerPrefs.ActivePodType);
            }

            FriendsListManager.Instance.ConfirmationScreen.Cancel();
            FriendsListManager.Instance.Ui.Close(true);
            FriendsListManager.Instance.ReparentUI();

            try { CosmeticsCache.ClearUnusedCosmetics(); }
            catch (Exception e) { Utils.ThrowException(e); }

            yield return HudManager.Instance.CoFadeFullScreen(Color.clear, Color.black);
            ++DataManager.Player.Ban.BanPoints;
            DataManager.Player.Ban.PreviousGameStartDate = DateTime.UtcNow;
            DataManager.Player.Save();

            if (__instance.AmHost)
                yield return __instance.CoStartGameHost();
            else
            {
                yield return __instance.CoStartGameClient();

                if (__instance.AmHost)
                    yield return __instance.CoStartGameHost();
            }

            for (var index = 0; index < GameData.Instance.PlayerCount; ++index)
            {
                PlayerControl player = GameData.Instance.AllPlayers[index].Object;

                if (player)
                {
                    player.moveable = true;
                    player.NetTransform.enabled = true;
                    player.MyPhysics.enabled = true;
                    player.MyPhysics.Awake();
                    player.MyPhysics.ResetMoveState();
                    player.Collider.enabled = true;
                    ShipStatus.Instance.SpawnPlayer(player, GameData.Instance.PlayerCount, true);
                }
            }

            FriendsListManager.Instance.SetRecentlyPlayed(GameData.Instance.AllPlayers);
            GameData.TimeGameStarted = Time.realtimeSinceStartup;
            int map = Mathf.Clamp(GameOptionsManager.Instance.CurrentGameOptions.MapId, 0, Constants.MapNames.Length - 1);
            string gameName = GameCode.IntToGameName(AmongUsClient.Instance.GameId);
            DebugAnalytics.Instance.Analytics.StartGame(PlayerControl.LocalPlayer.Data, GameData.Instance.PlayerCount, GameOptionsManager.Instance.CurrentGameOptions.NumImpostors, AmongUsClient.Instance.NetworkMode, (MapNames)map, GameOptionsManager.Instance.CurrentGameOptions.GameMode, gameName, ServerManager.Instance.CurrentRegion.Name, GameOptionsManager.Instance.CurrentGameOptions, GameData.Instance.AllPlayers);

            try
            {
                UnityTelemetry.Instance.StartGame(AmongUsClient.Instance.AmHost, GameData.Instance.PlayerCount, GameOptionsManager.Instance.CurrentGameOptions.NumImpostors, AmongUsClient.Instance.NetworkMode, DataManager.Player.Stats.GetStat(StatID.GamesAsImpostor), DataManager.Player.Stats.GetStat(StatID.GamesStarted), DataManager.Player.Stats.GetStat(StatID.CrewmateStreak));
                NetworkedPlayerInfo.PlayerOutfit defaultOutfit = PlayerControl.LocalPlayer.Data.DefaultOutfit;
                UnityTelemetry.Instance.StartGameCosmetics(defaultOutfit.ColorId, defaultOutfit.HatId, defaultOutfit.SkinId, defaultOutfit.PetId, defaultOutfit.VisorId, defaultOutfit.NamePlateId);
            }
            catch { }

            GameDebugCommands.AddCommands();
        }
    }

    public static void Postfix(AmongUsClient __instance)
    {
        try { LobbySharingAPI.NotifyLobbyStatusChanged(LobbyStatus.In_Game); }
        catch (Exception e) { Utils.ThrowException(e); }

        SetUpRoleTextPatch.IsInIntro = true;

        Main.OverrideWelcomeMsg = string.Empty;

        try
        {
            if (Options.CurrentGameMode is CustomGameMode.BedWars or CustomGameMode.Snowdown)
                Options.UsePets.SetValue(1);
        }
        catch (Exception e) { Utils.ThrowException(e); }

        try
        {
            new[]
            {
                RoleTypes.GuardianAngel,
                RoleTypes.Scientist,
                RoleTypes.Engineer,
                RoleTypes.Shapeshifter,
                RoleTypes.Noisemaker,
                RoleTypes.Phantom,
                RoleTypes.Tracker,
                RoleTypes.Detective,
                RoleTypes.Viper
            }.Do(x => Main.NormalOptions.roleOptions.SetRoleRate(x, 0, 0));

            if (Main.NormalOptions.MapId > 5 && !(Main.NormalOptions.MapId == 6 && SubmergedCompatibility.Loaded))
            {
                Logger.SendInGame(GetString("UnsupportedMap"), Color.red);
                ErrorText.Instance.AddError(ErrorCode.UnsupportedMap);
            }

            Main.GameEndDueToTimer = false;

            try
            {
                Main.AllRoleClasses.ForEach(x =>
                {
                    try { x.Init(); }
                    catch (Exception e) { Utils.ThrowException(e); }
                });
            }
            catch (Exception e) { Utils.ThrowException(e); }

            foreach (PlayerControl pc in Main.EnumeratePlayerControls())
            {
                if (pc.Data == null || !pc.Data.IsDead) continue;
                pc.Data.IsDead = false;
                pc.Data.SetDirtyBit(0b_1u << pc.PlayerId);
            }

            __instance.SendAllStreamedObjects();
            Main.LobbyDead.Clear();
            Main.LobbyKillers.Clear();

            Main.PlayerStates = [];

            Main.AbilityUseLimit = [];

            Main.HasJustStarted = true;

            Main.AllPlayerKillCooldown = [];
            Main.AllPlayerSpeed = [];
            Main.KillTimers = [];
            Main.SleuthMsgs = [];
            Main.SuperStarDead = [];
            Main.KilledDiseased = [];
            Main.KilledAntidote = [];
            Main.BaitAlive = [];
            Main.DontCancelVoteList = [];
            Main.LastEnteredVent = [];
            Main.LastEnteredVentLocation = [];
            Main.AfterMeetingDeathPlayers = [];
            Main.ResetCamPlayerList = [];
            Main.ClientIdList = [];
            Main.CheckShapeshift = [];
            Main.ShapeshiftTarget = [];
            Main.ShapeshiftIsAnimated = [];
            Main.LoversPlayers = [];
            Main.DiedThisRound = [];
            Main.GuesserGuessed = [];
            Main.GuesserGuessedMeeting = [];
            Main.ShieldPlayer = Options.ShieldPersonDiedFirst.GetBool() ? Main.FirstDied : string.Empty;
            Main.FirstDied = string.Empty;
            Main.MadmateNum = 0;

            Mayor.MayorUsedButtonCount = [];
            Paranoid.ParaUsedButtonCount = [];
            Vector.VectorVentCount = [];
            Cleaner.CleanerBodies = [];
            Virus.InfectedBodies = [];
            Workaholic.WorkaholicAlive = [];
            Virus.VirusNotify = [];
            Veteran.VeteranInProtect = [];
            Grenadier.GrenadierBlinding = [];
            SecurityGuard.BlockSabo = [];
            Ventguard.BlockedVents = [];
            Grenadier.MadGrenadierBlinding = [];
            Overkiller.OverkillerDeadPlayerList = [];
            Warlock.WarlockTimer = [];
            Arsonist.IsDoused = [];
            Revolutionist.IsDraw = [];
            Investigator.IsRevealed = [];
            Arsonist.ArsonistTimer = [];
            Revolutionist.RevolutionistTimer = [];
            Revolutionist.RevolutionistStart = [];
            Revolutionist.RevolutionistLastTime = [];
            Revolutionist.RevolutionistCountdown = [];
            Investigator.InvestigatorTimer = [];
            Warlock.CursedPlayers = [];
            Nemesis.NemesisRevenged = [];
            Warlock.IsCurseAndKill = [];
            Warlock.IsCursed = false;
            Forensic.ForensicNotify = [];
            Provocateur.Provoked = [];
            Crusader.ForCrusade = [];
            Godfather.GodfatherTarget = byte.MaxValue;
            Crewpostor.TasksDone = [];
            Messenger.Sent = [];
            Lazy.BeforeMeetingPositions = [];
            Introvert.TeleportAwayDelays = [];
            Onbound.NumBlocked = [];
            Blessed.ShieldActive = [];
            Talkative.NumMessagesThisMeeting = [];

            try
            {
                SabotageMapPatch.TimerTexts.Values.DoIf(x => x, x => Object.Destroy(x.gameObject));
                MapRoomDoorsUpdatePatch.DoorTimerTexts.Values.DoIf(x => x, x => Object.Destroy(x.gameObject));
            }
            catch (Exception e) { Utils.ThrowException(e); }
            
            SabotageMapPatch.TimerTexts = [];
            MapRoomDoorsUpdatePatch.DoorTimerTexts = [];
            ReportDeadBodyPatch.CanReport = [];
            ReportDeadBodyPatch.AlreadyReportedBodies = [];
            
            GuessManager.Guessers = [];
            ChatCommands.VotedToStart = [];

            Options.UsedButtonCount = 0;

            ChatCommands.Spectators.UnionWith(ChatCommands.ForcedSpectators);
            ChatCommands.ForcedSpectators.Clear();
            ChatCommands.LastSpectators.Clear();
            ChatCommands.LastSpectators.UnionWith(ChatCommands.Spectators);

            RPCHandlerPatch.RemoveExpiredWhiteList();
            
            LobbyViewSettingsPanePatch.ClearReferences();

            try
            {
                (OptionItem MinSetting, OptionItem MaxSetting) impLimits = Options.FactionMinMaxSettings[Team.Impostor];
                int optImpNum = IRandom.Instance.Next(impLimits.MinSetting.GetInt(), impLimits.MaxSetting.GetInt() + 1);
                if (GameStates.CurrentServerType == GameStates.ServerType.Vanilla) optImpNum = Math.Clamp(optImpNum, 1, 3);
                GameOptionsManager.Instance.currentNormalGameOptions.NumImpostors = optImpNum;
                GameOptionsManager.Instance.CurrentGameOptions.SetInt(Int32OptionNames.NumImpostors, optImpNum);
            }
            catch (Exception e) { Utils.ThrowException(e); }

            GameOptionsManager.Instance.currentNormalGameOptions.ConfirmImpostor = false;
            Main.RealOptionsData = new(GameOptionsManager.Instance.CurrentGameOptions);

            Main.IntroDestroyed = false;
            ShipStatusBeginPatch.RolesIsAssigned = false;
            GameEndChecker.Ended = false;
            ReportDeadBodyPatch.MeetingStarted = false;

            ShipStatusFixedUpdatePatch.ClosestVent = [];
            ShipStatusFixedUpdatePatch.CanUseClosestVent = [];

            RandomSpawn.CustomNetworkTransformHandleRpcPatch.HasSpawned = [];
            CovenBase.CovenMeetingStartPatch.MeetingNum = 0;

            AFKDetector.ShieldedPlayers.Clear();
            Main.Invisible.Clear();
            ChatCommands.MutedPlayers.Clear();
            ExtendedPlayerControl.TempExiled.Clear();
            Utils.CachedRoleSettings.Clear();

            MeetingTimeManager.Init();
            Main.DefaultCrewmateVision = Main.RealOptionsData.GetFloat(FloatOptionNames.CrewLightMod);
            Main.DefaultImpostorVision = Main.RealOptionsData.GetFloat(FloatOptionNames.ImpostorLightMod);

            Main.LastNotifyNames = [];
            Main.NumEmergencyMeetingsUsed = [];

            CheckForEndVotingPatch.EjectionText = string.Empty;

            Arsonist.CurrentDousingTarget = byte.MaxValue;
            Revolutionist.CurrentDrawTarget = byte.MaxValue;
            Main.PlayerColors = [];

            RPC.SyncAllPlayerNames();
            RPC.SyncAllClientRealNames();

            Camouflage.BlockCamouflage = false;
            Camouflage.Init();
            
            RoleResult = [];

            foreach (PlayerControl pc in Main.EnumeratePlayerControls())
            {
                foreach (PlayerControl seer in Main.EnumeratePlayerControls())
                {
                    (byte, byte) pair = (pc.PlayerId, seer.PlayerId);
                    Main.LastNotifyNames[pair] = pc.name;
                }
                
                int colorId = pc.Data.DefaultOutfit.ColorId;
                if (AmongUsClient.Instance.AmHost && Options.FormatNameMode.GetInt() == 1)
                {
                    string colorName = Palette.GetColorName(colorId);
                    string formattedColorName = char.ToUpper(colorName[0]) + colorName[1..].ToLower();
                    pc.RpcSetName(formattedColorName);
                }

                Main.PlayerStates[pc.PlayerId] = new(pc.PlayerId);
                Main.AllPlayerSpeed[pc.PlayerId] = Main.RealOptionsData.GetFloat(FloatOptionNames.PlayerSpeedMod);
                Main.AllPlayerKillCooldown[pc.PlayerId] = Options.DefaultKillCooldown + Main.CurrentMap switch
                {
                    MapNames.Polus => Options.ExtraKillCooldownOnPolus.GetFloat(),
                    MapNames.Airship => Options.ExtraKillCooldownOnAirship.GetFloat(),
                    MapNames.Fungle => Options.ExtraKillCooldownOnFungle.GetFloat(),
                    _ => 0f
                };
                Main.NumEmergencyMeetingsUsed[pc.PlayerId] = 0;
                ReportDeadBodyPatch.CanReport[pc.PlayerId] = true;
                ReportDeadBodyPatch.WaitReport[pc.PlayerId] = [];
                RoleResult[pc.PlayerId] = CustomRoles.NotAssigned;
                pc.cosmetics.nameText.text = pc.name;
                RandomSpawn.CustomNetworkTransformHandleRpcPatch.HasSpawned.Clear();
                NetworkedPlayerInfo.PlayerOutfit outfit = pc.Data.DefaultOutfit;
                Camouflage.PlayerSkins[pc.PlayerId] = new NetworkedPlayerInfo.PlayerOutfit().Set(Main.AllPlayerNames.GetValueOrDefault(pc.PlayerId, outfit.PlayerName), outfit.ColorId, outfit.HatId, outfit.SkinId, outfit.VisorId, outfit.PetId, outfit.NamePlateId);
                Main.ClientIdList.Add(pc.OwnerId);

                try { Main.PlayerColors[pc.PlayerId] = Palette.PlayerColors[colorId]; }
                catch (Exception e) { Utils.ThrowException(e); }
            }

            Main.VisibleTasksCount = true;

            if (__instance.AmHost)
            {
                RPC.SyncCustomSettingsRPC();
            }

            FallFromLadder.Reset();
            CustomSabotage.Reset();

            try
            {
                LastImpostor.Init();
                TargetArrow.Init();
                LocateArrow.Init();
                DoubleTrigger.Init();
                Workhorse.Init();
                Damocles.Initialize();
                Stressed.Init();
                Asthmatic.Init();
                DoubleShot.Init();
                Circumvent.Init();
                Commited.Init();
            }
            catch (Exception ex) { Logger.Exception(ex, "Init Roles"); }

            try
            {
                SoloPVP.Init();
                FreeForAll.Init();
                StopAndGo.Init();
                HotPotato.Init();
                CustomHnS.Init();
                Speedrun.Init();
            }
            catch (Exception e) { Utils.ThrowException(e); }

            try
            {
                CustomWinnerHolder.Reset();
                AntiBlackout.Reset();
                NameNotifyManager.Reset();
                SabotageSystemTypeUpdateSystemPatch.Initialize();
                DoorsReset.Initialize();
                GhostRolesManager.Initialize();
                RoleBlockManager.Reset();
                ChatManager.ResetHistory();
                PerSecondUpdateScheduler.Reset();
            }
            catch (Exception e) { Utils.ThrowException(e); }
            
            CustomNetObject.Reset();

            IRandom.SetInstanceById(Options.RoleAssigningAlgorithm.GetValue());

            MeetingStates.MeetingNum = 0;
            MeetingStates.MeetingCalled = false;
            MeetingStates.FirstMeeting = true;
            GameStates.AlreadyDied = false;

            Main.Instance.StartCoroutine(PopulateSkinItems());
            
            GC.Collect();
            Resources.UnloadUnusedAssets();
            GC.Collect();
        }
        catch (Exception ex)
        {
            Utils.ErrorEnd("Change Role Setting Postfix");
            Utils.ThrowException(ex);
        }

        return;

        System.Collections.IEnumerator PopulateSkinItems()
        {
            while (!ShipStatus.Instance) yield return null;
            BlockPopulateSkins = false;
            LateTask.New(() => BlockPopulateSkins = true, 0.5f, log: false);
            yield return ShipStatus.Instance.CosmeticsCache.PopulateFromPlayers();
        }
    }
}

[HarmonyPatch(typeof(CosmeticsCache), nameof(CosmeticsCache.PopulateFromPlayers))]
internal static class BlockPopulateFromPlayersPatch
{
    public static bool Prefix()
    {
        return !ChangeRoleSettings.BlockPopulateSkins;
    }
}

[HarmonyPatch]
internal static class StartGameHostPatch
{
    private static AmongUsClient AUClient;

    public static readonly Dictionary<CustomRoles, List<byte>> BasisChangingAddons = [];
    private static Dictionary<RoleTypes, int> RoleTypeNums = [];

    private static RoleOptionsCollectionV10 RoleOpt => Main.NormalOptions.roleOptions;

    private static void UpdateRoleTypeNums()
    {
        RoleTypeNums = new()
        {
            { RoleTypes.Scientist, AddScientistNum },
            { RoleTypes.Engineer, AddEngineerNum },
            { RoleTypes.Shapeshifter, AddShapeshifterNum },
            { RoleTypes.Noisemaker, AddNoisemakerNum },
            { RoleTypes.Phantom, AddPhantomNum },
            { RoleTypes.Tracker, AddTrackerNum },
            { RoleTypes.Detective, AddDetectiveNum },
            { RoleTypes.Viper, AddViperNum }
        };
    }

    private static System.Collections.IEnumerator WaitAndSmoothlyUpdate(this LoadingBarManager loadingBarManager, float startPercent, float targetPercent, float duration, string loadingText)
    {
        float startTime = Time.time;

        while (Time.time - startTime < duration)
        {
            float t = (Time.time - startTime) / duration;
            float newPercent = Mathf.Lerp(startPercent, targetPercent, t);

            try
            {
                loadingBarManager.SetLoadingPercent(newPercent, StringNames.LoadingBarGameStart);
                loadingBarManager.loadingBar.loadingText.text = loadingText;
            }
            catch (Exception e) { Utils.ThrowException(e); }

            yield return null;
        }

        try
        {
            loadingBarManager.SetLoadingPercent(targetPercent, StringNames.LoadingBarGameStart);
            loadingBarManager.loadingBar.loadingText.text = loadingText;
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.CoStartGameHost))]
    [HarmonyPrefix]
    public static bool CoStartGameHost_Prefix(AmongUsClient __instance, ref IEnumerator __result)
    {
        AUClient = __instance;
        __result = StartGameHost().WrapToIl2Cpp();
        return false;
    }

    private static System.Collections.IEnumerator StartGameHost()
    {
        // Restore the host's clean name for the game. Use TryGetValue (not the [0] indexer) and skip on a
        // missing/empty entry so we never broadcast a null/empty name at game start — which would carry the
        // blank-host-name bug into the round and can black-screen vanilla clients.
        try
        {
            if (Main.AllPlayerNames.TryGetValue(PlayerControl.LocalPlayer.PlayerId, out string hostName) && !string.IsNullOrEmpty(hostName))
                PlayerControl.LocalPlayer.RpcSetName(hostName);
        }
        catch (Exception e) { Utils.ThrowException(e); }
        
        string loadingTextText1 = GetString("LoadingBarText.1");
        LoadingBarManager loadingBarManager = LoadingBarManager.Instance;

        try
        {
            loadingBarManager.ToggleLoadingBar(true);
            loadingBarManager.SetLoadingPercent(0f, StringNames.LoadingBarGameStart);
            loadingBarManager.loadingBar.loadingText.DestroyTranslator();
            loadingBarManager.loadingBar.loadingText.text = loadingTextText1;

            var loadingBarLogo = GameObject.Find("Loading Bar Manager/Loading Bar/Canvas/Logo")?.GetComponent<Image>();

            if (loadingBarLogo)
            {
                // TODO: drop a PNG at Resources/Images/EHR-Icon.png (or rename and update this path) once an End K not logo is ready.
                // While the asset is missing, LoadSprite logs one error and returns null — the loading bar renders without a logo.
                loadingBarLogo.sprite = Utils.LoadSprite("EndKnot.Resources.Images.EHR-Icon.png", 390f);
                loadingBarLogo.SetNativeSize();
            }

            var fillImage = GameObject.Find("Loading Bar Manager/Loading Bar/Canvas/Bar/Fill")?.GetComponent<Image>();
            if (fillImage) fillImage.color = new Color(0f, 0.647f, 1f, 1f);
        }
        catch (Exception e) { Utils.ThrowException(e); }

        if (LobbyBehaviour.Instance)
        {
            MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
            writer.StartMessage(5);
            writer.Write(AUClient.GameId);
            writer.StartMessage(5);
            writer.WritePacked(LobbyBehaviour.Instance.NetId);
            writer.EndMessage();
            writer.EndMessage();
            AUClient.SendOrDisconnect(writer);
            writer.Recycle();
            AUClient.RemoveNetObject(LobbyBehaviour.Instance);
            Object.Destroy(LobbyBehaviour.Instance.gameObject);
        }
        else
        {
            Logger.Fatal($"LobbyBehaviour.Instance is null in {nameof(StartGameHostPatch)}.{nameof(StartGameHost)}", "StartGameHost");
        }

        if (!ShipStatus.Instance)
        {
            int index = Mathf.Clamp(GameOptionsManager.Instance.CurrentGameOptions.MapId, 0, Constants.MapNames.Length - 1);
            AUClient.ShipLoadingAsyncHandle = AUClient.ShipPrefabs[index].InstantiateAsync();

            while (!AUClient.ShipLoadingAsyncHandle.IsDone)
            {
                float progress = AUClient.ShipLoadingAsyncHandle.PercentComplete;
                float displayPercent = Mathf.Lerp(0f, 10f, progress);
                loadingBarManager.SetLoadingPercent(displayPercent, StringNames.LoadingBarGameStart);
                loadingBarManager.loadingBar.loadingText.text = loadingTextText1;
                yield return null;
            }

            GameObject result = AUClient.ShipLoadingAsyncHandle.Result;
            ShipStatus.Instance = result.GetComponent<ShipStatus>();
            AUClient.Spawn(ShipStatus.Instance);
            Logger.Info($"Successfully spawned ShipStatus for map {GameOptionsManager.Instance.CurrentGameOptions.MapId} ({Constants.MapNames[GameOptionsManager.Instance.CurrentGameOptions.MapId]})", "StartGameHost");
        }

        try
        {
            loadingBarManager.SetLoadingPercent(10f, StringNames.LoadingBarGameStart);
            loadingBarManager.loadingBar.loadingText.text = loadingTextText1;
        }
        catch (Exception e) { Utils.ThrowException(e); }

        DateTime start = DateTime.Now;

        while (true)
        {
            var flag = true;
            var num = 10;
            var totalSeconds = (float)(DateTime.Now - start).TotalSeconds;

            if (GameOptionsManager.Instance.CurrentGameOptions.MapId == 5 || GameOptionsManager.Instance.CurrentGameOptions.MapId == 4)
                num = 15;

            var clientsReady = 0;
            int allClientsCount = AUClient.allClients.Count;

            lock (AUClient.allClients)
            {
                // ReSharper disable once ForCanBeConvertedToForeach    <- foreach would throw an exception in case of a disconnect
                for (var index = 0; index < AUClient.allClients.Count; ++index)
                {
                    ClientData allClient = AUClient.allClients[index];

                    if (allClient.Id != AUClient.ClientId && !allClient.IsReady)
                    {
                        if (totalSeconds < (double)num)
                            flag = false;
                        else
                        {
                            AUClient.SendLateRejection(allClient.Id, DisconnectReasons.ClientTimeout);
                            allClient.IsReady = true;
                            AUClient.OnPlayerLeft(allClient, DisconnectReasons.ClientTimeout);
                        }
                    }
                    else
                        ++clientsReady;
                }
            }

            try
            {
                if (totalSeconds < (double)num)
                {
                    loadingBarManager.SetLoadingPercent(10 + (float)(totalSeconds / (double)num * 80.0), StringNames.LoadingBarGameStartWaitingPlayers);

                    int timeoutIn = num - (int)totalSeconds;
                    loadingBarManager.loadingBar.loadingText.text = string.Format(GetString("LoadingBarText.2"), clientsReady, allClientsCount, timeoutIn);
                }
            }
            catch (Exception e) { Utils.ThrowException(e); }

            if (!flag)
                yield return new WaitForEndOfFrame();
            else
                break;
        }

        AUClient.SendClientReady();
        yield return AssignRoles();
    }

    private static System.Collections.IEnumerator AssignRoles()
    {
        if (AmongUsClient.Instance.IsGameOver || GameStates.IsLobby || GameEndChecker.Ended) yield break;
        
        Options.AutoSetFactionMinMaxSettings();

        RpcSetRoleReplacer.Initialize();

        SelectCustomRoles();
        SelectAddonRoles();
        CalculateVanillaRoleCount();

        UpdateRoleTypeNums();

        foreach ((RoleTypes roleTypes, int roleNum) in RoleTypeNums)
            RoleOpt.SetRoleRate(roleTypes, roleNum, roleNum > 0 ? 100 : RoleOpt.GetChancePerGame(roleTypes));

        Statistics.OnRoleSelectionComplete();

        #region BasisChangingAddonsSetup

        try
        {
            BasisChangingAddons.Clear();

            var random = IRandom.Instance;

            if (Options.CurrentGameMode == CustomGameMode.Standard)
            {
                bool bloodlustSpawn = random.Next(100) < (Options.CustomAdtRoleSpawnRate.TryGetValue(CustomRoles.Bloodlust, out IntegerOptionItem option0) ? option0.GetFloat() : 0) && CustomRoles.Bloodlust.IsEnable() && Options.RoleSubCategoryLimits[RoleOptionType.Neutral_Killing][2].GetInt() > 0;
                bool physicistSpawn = random.Next(100) < (Options.CustomAdtRoleSpawnRate.TryGetValue(CustomRoles.Physicist, out IntegerOptionItem option1) ? option1.GetFloat() : 0) && CustomRoles.Physicist.IsEnable();
                bool nimbleSpawn = random.Next(100) < (Options.CustomAdtRoleSpawnRate.TryGetValue(CustomRoles.Nimble, out IntegerOptionItem option2) ? option2.GetFloat() : 0) && CustomRoles.Nimble.IsEnable();
                bool finderSpawn = random.Next(100) < (Options.CustomAdtRoleSpawnRate.TryGetValue(CustomRoles.Finder, out IntegerOptionItem option3) ? option3.GetFloat() : 0) && CustomRoles.Finder.IsEnable();
                bool noisySpawn = random.Next(100) < (Options.CustomAdtRoleSpawnRate.TryGetValue(CustomRoles.Noisy, out IntegerOptionItem option4) ? option4.GetFloat() : 0) && CustomRoles.Noisy.IsEnable();
                bool examinerSpawn = random.Next(100) < (Options.CustomAdtRoleSpawnRate.TryGetValue(CustomRoles.Examiner, out IntegerOptionItem option5) ? option5.GetFloat() : 0) && CustomRoles.Examiner.IsEnable();
                bool venomSpawn = random.Next(100) < (Options.CustomAdtRoleSpawnRate.TryGetValue(CustomRoles.Venom, out IntegerOptionItem option6) ? option6.GetFloat() : 0) && CustomRoles.Venom.IsEnable();

                if (Options.EveryoneCanVent.GetBool())
                {
                    nimbleSpawn = false;
                    physicistSpawn = false;
                    finderSpawn = false;
                    noisySpawn = false;
                    examinerSpawn = false;
                }

                HashSet<byte> bloodlustList = [], nimbleList = [], physicistList = [], finderList = [], noisyList = [], examinerList = [], venomList = [];
                bool hasBanned = Main.NeverSpawnTogetherCombos.TryGetValue(OptionItem.CurrentPreset, out Dictionary<CustomRoles, List<CustomRoles>> banned);

                if (nimbleSpawn || physicistSpawn || finderSpawn || noisySpawn || bloodlustSpawn || examinerSpawn || venomSpawn)
                {
                    foreach (PlayerControl player in Main.EnumeratePlayerControls())
                    {
                        if (IsBasisChangingPlayer(player.PlayerId, CustomRoles.Bloodlust)) continue;

                        KeyValuePair<byte, CustomRoles> kp = RoleResult.FirstOrDefault(x => x.Key == player.PlayerId);

                        bool bloodlustBanned = hasBanned && banned.Any(x => x.Key == kp.Value && x.Value.Contains(CustomRoles.Bloodlust));
                        bool nimbleBanned = hasBanned && banned.Any(x => x.Key == kp.Value && x.Value.Contains(CustomRoles.Nimble));
                        bool physicistBanned = hasBanned && banned.Any(x => x.Key == kp.Value && x.Value.Contains(CustomRoles.Physicist));
                        bool finderBanned = hasBanned && banned.Any(x => x.Key == kp.Value && x.Value.Contains(CustomRoles.Finder));
                        bool noisyBanned = hasBanned && banned.Any(x => x.Key == kp.Value && x.Value.Contains(CustomRoles.Noisy));
                        bool examinerBanned = hasBanned && banned.Any(x => x.Key == kp.Value && x.Value.Contains(CustomRoles.Examiner));
                        bool venomBanned = hasBanned && banned.Any(x => x.Key == kp.Value && x.Value.Contains(CustomRoles.Venom));

                        if (kp.Value.IsCrewmate())
                        {
                            if (!bloodlustBanned && !kp.Value.IsTaskBasedCrewmate()) bloodlustList.Add(player.PlayerId);
                            if (!nimbleBanned) nimbleList.Add(player.PlayerId);

                            if (kp.Value.GetRoleTypes() == RoleTypes.Crewmate)
                            {
                                if (!physicistBanned) physicistList.Add(player.PlayerId);
                                if (!finderBanned) finderList.Add(player.PlayerId);
                                if (!noisyBanned) noisyList.Add(player.PlayerId);
                                
                                if (!examinerBanned && !kp.Value.UsesMeetingShapeshift())
                                    examinerList.Add(player.PlayerId);
                            }
                        }

                        if (kp.Value.IsImpostor() && kp.Value.GetRoleTypes() == RoleTypes.Impostor && !kp.Value.IncompatibleWithVenom() && !venomBanned)
                            venomList.Add(player.PlayerId);
                    }
                }

                Dictionary<CustomRoles, (bool SpawnFlag, HashSet<byte> RoleList)> roleSpawnMapping = new()
                {
                    { CustomRoles.Bloodlust, (bloodlustSpawn, bloodlustList) },
                    { CustomRoles.Nimble, (nimbleSpawn, nimbleList) },
                    { CustomRoles.Examiner, (examinerSpawn, examinerList) },
                    { CustomRoles.Finder, (finderSpawn, finderList) },
                    { CustomRoles.Physicist, (physicistSpawn, physicistList) },
                    { CustomRoles.Noisy, (noisySpawn, noisyList) },
                    { CustomRoles.Venom, (venomSpawn, venomList) }
                };

                for (var i = 0; i < roleSpawnMapping.Count; i++)
                {
                    (CustomRoles addon, (bool SpawnFlag, HashSet<byte> RoleList) value) = roleSpawnMapping.ElementAt(i);
                    if (value.RoleList.Count == 0) value.SpawnFlag = false;
                    if (!value.SpawnFlag) value.RoleList.Clear();

                    if (Main.GM.Value) value.RoleList.Remove(0);
                    value.RoleList.ExceptWith(ChatCommands.Spectators);

                    if (Main.AlwaysSpawnTogetherCombos.TryGetValue(OptionItem.CurrentPreset, out Dictionary<CustomRoles, List<CustomRoles>> combos) && combos.Values.Any(l => l.Contains(addon)))
                    {
                        HashSet<CustomRoles> roles = combos.Where(x => x.Value.Contains(addon)).Select(x => x.Key).ToHashSet();
                        HashSet<byte> players = RoleResult.Where(x => roles.Contains(x.Value) && x.Value.IsCrewmate() && (addon != CustomRoles.Bloodlust || x.Value.IsTasklessCrewmate()) && (addon == CustomRoles.Nimble || x.Value.GetRoleTypes() == RoleTypes.Crewmate) && !IsBasisChangingPlayer(x.Key, CustomRoles.Bloodlust)).Select(x => x.Key).ToHashSet();

                        if (players.Count > 0)
                        {
                            value.RoleList = players;
                            value.SpawnFlag = true;
                            roleSpawnMapping[addon] = value;
                        }
                    }

                    if (Main.SetAddOns.Values.Any(x => x.Contains(addon)))
                    {
                        value.SpawnFlag = true;
                        HashSet<byte> newRoleList = Main.SetAddOns.Where(x => x.Value.Contains(addon)).Select(x => x.Key).ToHashSet();
                        if (value.RoleList.Count != 1 || value.RoleList.First() != newRoleList.First()) value.RoleList = newRoleList;

                        roleSpawnMapping[addon] = value;
                    }
                }

                foreach ((CustomRoles addon, (bool spawnFlag, HashSet<byte> roleList)) in roleSpawnMapping)
                {
                    if (spawnFlag)
                    {
                        foreach ((CustomRoles otherAddon, (bool otherSpawnFlag, _)) in roleSpawnMapping)
                        {
                            if (otherAddon != addon && otherSpawnFlag && BasisChangingAddons.TryGetValue(otherAddon, out List<byte> otherList))
                                roleList.ExceptWith(otherList);
                        }

                        BasisChangingAddons[addon] = roleList.Shuffle().Take(addon.GetCount()).ToList();
                    }
                }
            }
        }
        catch (Exception e) { Utils.ThrowException(e); }

        #endregion

        // Start CustomRpcSender
        RpcSetRoleReplacer.StartReplace();

        // Assign roles and create role maps for desync roles
        RpcSetRoleReplacer.AssignDesyncRoles();
        RpcSetRoleReplacer.SendRpcForDesync();

        // Assign roles and create role maps for normal roles
        RpcSetRoleReplacer.AssignNormalRoles();
        RpcSetRoleReplacer.SendRpcForNormal();

        // Send all RPCs
        RpcSetRoleReplacer.Release();

        try
        {
            if (RoleResult.ContainsValue(CustomRoles.DoubleAgent))
            {
                foreach ((byte targetId, CustomRoles targetRole) in RoleResult)
                {
                    if (targetRole != CustomRoles.DoubleAgent) continue;

                    PlayerControl target = Utils.GetPlayerById(targetId);
                    if (target == null) continue;

                    foreach ((byte seerId, CustomRoles seerRole) in RoleResult)
                    {
                        if (seerId == targetId || !seerRole.IsImpostor()) continue;

                        PlayerControl seer = Utils.GetPlayerById(seerId);
                        if (seer == null) continue;

                        target.RpcSetRoleDesync(RoleTypes.Impostor, seer.OwnerId, setRoleMap: true);
                    }
                }
            }
        }
        catch (Exception e) { Utils.ThrowException(e); }

        try
        {
            foreach (PlayerControl pc in Main.EnumeratePlayerControls())
            {
                if (!Main.PlayerStates.ContainsKey(pc.PlayerId)) Main.PlayerStates[pc.PlayerId] = new PlayerState(pc.PlayerId);
                
                if (Main.PlayerStates[pc.PlayerId].MainRole != CustomRoles.NotAssigned) continue;

                CustomRoles role = Enum.TryParse($"{pc.Data.Role.Role}EndKnot", out CustomRoles parsedRole) ? parsedRole : CustomRoles.NotAssigned;
                if (role == CustomRoles.NotAssigned) Logger.SendInGame(string.Format(GetString("Error.InvalidRoleAssignment"), pc?.Data?.PlayerName), Color.red);

                Main.PlayerStates[pc.PlayerId].SetMainRole(role);
            }

            foreach (KeyValuePair<byte, CustomRoles> kv in RoleResult)
            {
                if (kv.Value.IsDesyncRole() || IsBasisChangingPlayer(kv.Key, CustomRoles.Bloodlust)) continue;

                if (!Main.PlayerStates.ContainsKey(kv.Key)) Main.PlayerStates[kv.Key] = new PlayerState(kv.Key);

                Main.PlayerStates[kv.Key].SetMainRole(kv.Value);
            }

            if (Options.CurrentGameMode != CustomGameMode.Standard)
            {
                foreach (KeyValuePair<byte, PlayerState> pair in Main.PlayerStates)
                    ExtendedPlayerControl.RpcSetCustomRole(pair.Key, pair.Value.MainRole);

                goto EndOfSelectRolePatch;
            }

            BasisChangingAddons.Do(x => x.Value.Do(y => Main.PlayerStates[y].SetSubRole(x.Key)));

            var overrideLovers = false;

            if (Main.SetAddOns.Count(x => x.Value.Contains(CustomRoles.Lovers)) == 2)
            {
                Main.LoversPlayers.Clear();
                Main.IsLoversDead = false;
                overrideLovers = true;
                Logger.Warn("Lovers overridden by host's pre-set add-ons", "CustomRoleSelector");
            }

            foreach (KeyValuePair<byte, List<CustomRoles>> item in Main.SetAddOns)
            {
                if (Main.PlayerStates.TryGetValue(item.Key, out PlayerState state))
                {
                    foreach (CustomRoles role in item.Value)
                    {
                        if (role is CustomRoles.Nimble or CustomRoles.Physicist or CustomRoles.Bloodlust or CustomRoles.Finder or CustomRoles.Noisy or CustomRoles.Examiner or CustomRoles.Venom) continue;

                        state.SetSubRole(role);
                        if (overrideLovers && role == CustomRoles.Lovers) Main.LoversPlayers.Add(Utils.GetPlayerById(item.Key));

                        if (role.IsGhostRole()) GhostRolesManager.SpecificAssignGhostRole(item.Key, role, true);
                    }
                }
            }

            if (!overrideLovers && CustomRoles.Lovers.IsEnable() && (RoleResult.ContainsValue(CustomRoles.Hater) ? -1 : IRandom.Instance.Next(1, 100)) <= Lovers.LoverSpawnChances.GetInt()) AssignLoversRolesFromList();

            // Add-on assignment
            var aapc = Main.EnumerateAlivePlayerControls().Shuffle();
            if (Main.GM.Value) aapc.Remove(PlayerControl.LocalPlayer);
            aapc.RemoveAll(x => ChatCommands.Spectators.Contains(x.PlayerId));

            Dictionary<PlayerControl, int> addonNum = aapc.ToDictionary(x => x, _ => 0);

            AddonRolesList
                .Except(BasisChangingAddons.Keys)
                .Where(x => x.IsEnable() && aapc.Any(p => CustomRolesHelper.CheckAddonConflict(x, p)))
                .SelectMany(x => Enumerable.Repeat(x, Math.Clamp(x.GetCount(), 0, aapc.Count)))
                .Where(x => IRandom.Instance.Next(1, 100) <= (Options.CustomAdtRoleSpawnRate.TryGetValue(x, out IntegerOptionItem sc) ? sc.GetFloat() : 0))
                .OrderBy(x => Options.CustomAdtRoleSpawnRate.TryGetValue(x, out IntegerOptionItem sc) && sc.GetInt() == 100 ? IRandom.Instance.Next(100) : IRandom.Instance.Next(100, 1000))
                .Select(x =>
                {
                    PlayerControl suitablePlayer = aapc.OrderBy(p => addonNum[p]).FirstOrDefault(p => CustomRolesHelper.CheckAddonConflict(x, p));
                    if (suitablePlayer != null) addonNum[suitablePlayer]++;
                    return (Role: x, SuitablePlayer: suitablePlayer);
                })
                .DoIf(x => x.SuitablePlayer != null, x => Main.PlayerStates[x.SuitablePlayer.PlayerId].SetSubRole(x.Role));


            foreach (PlayerState state in Main.PlayerStates.Values)
            {
                if (Main.NeverSpawnTogetherCombos.TryGetValue(OptionItem.CurrentPreset, out Dictionary<CustomRoles, List<CustomRoles>> neverList) && neverList.TryGetValue(state.MainRole, out List<CustomRoles> bannedAddonList))
                    bannedAddonList.ForEach(state.RemoveSubRole);

                if (Main.AlwaysSpawnTogetherCombos.TryGetValue(OptionItem.CurrentPreset, out Dictionary<CustomRoles, List<CustomRoles>> alwaysList) && alwaysList.TryGetValue(state.MainRole, out List<CustomRoles> addonList))
                    addonList.ForEach(x => state.SetSubRole(x));

                if (!state.MainRole.IsImpostor() && !(state.MainRole == CustomRoles.Traitor && Traitor.CanGetImpostorOnlyAddons.GetBool()))
                    state.SubRoles.RemoveAll(x => x.IsImpOnlyAddon());

                if (state.SubRoles.Contains(CustomRoles.BananaMan))
                    Utils.RpcChangeSkin(state.Player, new());
                
                if (state.SubRoles.Contains(CustomRoles.Venom))
                    state.SubRoles.FindAll(x => x.IncompatibleWithVenom()).ForEach(state.RemoveSubRole);
            }

            foreach (KeyValuePair<byte, PlayerState> pair in Main.PlayerStates)
            {
                ExtendedPlayerControl.RpcSetCustomRole(pair.Key, pair.Value.MainRole);

                StringBuilder sb = new();

                foreach (CustomRoles subRole in pair.Value.SubRoles)
                {
                    ExtendedPlayerControl.RpcSetCustomRole(pair.Key, subRole);
                    sb.Append(subRole).Append(", ");
                }

                if (sb.Length > 0)
                {
                    sb.Remove(sb.Length - 2, 2);
                    Logger.Info($"{Main.AllPlayerNames.GetValueOrDefault(pair.Key, "Someone")} has sub roles: {sb}", "SelectRolesPatch");
                }
            }

            foreach (PlayerControl pc in Main.EnumeratePlayerControls())
            {
                try
                {
                    if (pc.Data.Role.Role == RoleTypes.Shapeshifter)
                        Main.CheckShapeshift[pc.PlayerId] = false;

                    if (pc.AmOwner && pc.GetCustomRole().GetDYRole() is RoleTypes.Shapeshifter or RoleTypes.Phantom)
                    {
                        foreach (PlayerControl target in Main.EnumeratePlayerControls())
                        {
                            target.Data.Role.CanBeKilled = true;

                            target.cosmetics.SetNameColor(Color.white);
                            target.Data.Role.NameColor = Color.white;
                        }
                    }
                }
                catch (Exception ex) { Logger.Error(ex.ToString(), "OnGameStartedPatch Add methods"); }
            }

            try
            {
                Stressed.Add();
                Asthmatic.Add();
                Circumvent.Add();
                Dynamo.Add();
                Spurt.Add();
                Allergic.Init();
                Reroll.Init();
                Lovers.Init();
                EndKnot.Roles.Stack.Apply();
                Twins.Init();
                LastNeutral.Init();
                SlowStarter.Init();
                LateTask.New(Tired.Reset, 7f, log: false);
                LateTask.New(OneWolf.ApplyDesync, 1.5f, log: false);
            }
            catch (Exception e) { Utils.ThrowException(e); }

            LateTask.New(CustomTeamManager.InitializeCustomTeamPlayers, 4f, log: false);

            if (overrideLovers) Logger.Msg(Main.LoversPlayers.Join(x => x?.GetRealName()), "Lovers");

            EndOfSelectRolePatch:

            switch (Options.CurrentGameMode)
            {
                case CustomGameMode.HideAndSeek:
                    CustomHnS.StartSeekerBlindTime();
                    goto default;
                case CustomGameMode.CaptureTheFlag:
                    CaptureTheFlag.Init();
                    goto default;
                case CustomGameMode.NaturalDisasters:
                    NaturalDisasters.OnGameStart();
                    break;
                case CustomGameMode.KingOfTheZones:
                    KingOfTheZones.Init();
                    goto default;
                case CustomGameMode.Quiz:
                    Quiz.Init();
                    goto default;
                case CustomGameMode.BedWars:
                    BedWars.Initialize();
                    goto default;
                case CustomGameMode.Deathrace:
                    Deathrace.Init();
                    goto default;
                case CustomGameMode.Snowdown:
                    Snowdown.Init();
                    goto default;
                default:
                    if (Options.IntegrateNaturalDisasters.GetBool()) goto case CustomGameMode.NaturalDisasters;
                    break;
            }

            HudManager.Instance.SetHudActive(true);

            foreach (PlayerControl pc in Main.EnumeratePlayerControls())
                pc.ResetKillCooldown(false);


            foreach (KeyValuePair<RoleTypes, int> roleType in RoleTypeNums)
            {
                var roleNum = 0;
                roleNum -= roleType.Value;
                RoleOpt.SetRoleRate(roleType.Key, roleNum, RoleOpt.GetChancePerGame(roleType.Key));
            }


            switch (Options.CurrentGameMode)
            {
                case CustomGameMode.Standard:
                    GameEndChecker.SetPredicateToNormal();
                    break;
                case CustomGameMode.SoloPVP:
                    GameEndChecker.SetPredicateToSoloPVP();
                    break;
                case CustomGameMode.FFA:
                    GameEndChecker.SetPredicateToFFA();
                    break;
                case CustomGameMode.StopAndGo:
                    GameEndChecker.SetPredicateToStopAndGo();
                    break;
                case CustomGameMode.HotPotato:
                    GameEndChecker.SetPredicateToHotPotato();
                    break;
                case CustomGameMode.Speedrun:
                    GameEndChecker.SetPredicateToSpeedrun();
                    break;
                case CustomGameMode.HideAndSeek:
                    GameEndChecker.SetPredicateToHideAndSeek();
                    break;
                case CustomGameMode.CaptureTheFlag:
                    GameEndChecker.SetPredicateToCaptureTheFlag();
                    break;
                case CustomGameMode.NaturalDisasters:
                    GameEndChecker.SetPredicateToNaturalDisasters();
                    break;
                case CustomGameMode.RoomRush:
                    GameEndChecker.SetPredicateToRoomRush();
                    break;
                case CustomGameMode.KingOfTheZones:
                    GameEndChecker.SetPredicateToKingOfTheZones();
                    break;
                case CustomGameMode.Quiz:
                    GameEndChecker.SetPredicateToQuiz();
                    break;
                case CustomGameMode.TheMindGame:
                    GameEndChecker.SetPredicateToTheMindGame();
                    break;
                case CustomGameMode.BedWars:
                    GameEndChecker.SetPredicateToBedWars();
                    break;
                case CustomGameMode.Deathrace:
                    GameEndChecker.SetPredicateToDeathrace();
                    break;
                case CustomGameMode.Mingle:
                    GameEndChecker.SetPredicateToMingle();
                    break;
                case CustomGameMode.Snowdown:
                    GameEndChecker.SetPredicateToSnowdown();
                    break;
            }

            // Add players with unclassified roles to the list of players who require ResetCam.
            Main.ResetCamPlayerList.UnionWith(Main.PlayerStates.Where(p => (p.Value.MainRole.IsDesyncRole() && !p.Key.GetPlayer().UsesPetInsteadOfKill()) || p.Value.SubRoles.Contains(CustomRoles.Bloodlust)).Select(p => p.Key));
            Utils.CountAlivePlayers(true);

            LateTask.New(() =>
            {
                Main.SetRoles = [];
                Main.SetAddOns = [];
                ChatCommands.DraftResult = [];
                ChatCommands.DraftRoles = [];

                if (Main.LoversPlayers.Count == 0) Main.LoversPlayers = Main.EnumeratePlayerControls().Where(x => x.Is(CustomRoles.Lovers) || x.GetCustomRole() is CustomRoles.LovingCrewmate or CustomRoles.LovingImpostor).ToList();
            }, 7f, log: false);

            if (Main.CurrentMap == MapNames.Airship && AmongUsClient.Instance.AmHost && Main.GM.Value) LateTask.New(() => PlayerControl.LocalPlayer.NetTransform.SnapTo(new(15.5f, 0.0f), (ushort)(PlayerControl.LocalPlayer.NetTransform.lastSequenceId + 8)), 15f, "GM Auto-TP Failsafe"); // TP to Main Hall

            LateTask.New(() => Main.HasJustStarted = false, 12f, "HasJustStarted to false");
        }
        catch (Exception ex)
        {
            Utils.ErrorEnd("Select Role Postfix");
            Utils.ThrowException(ex);
            yield break;
        }
        
        
        LoadingBarManager loadingBarManager = LoadingBarManager.Instance;
        yield return loadingBarManager.WaitAndSmoothlyUpdate(90f, 95f, 1f, GetString("LoadingBarText.1"));

        foreach (PlayerControl pc in PlayerControl.AllPlayerControls)
        {
            if (!pc.Data)
                while (!pc.Data)
                    yield return null;

            pc.Data.Disconnected = true;
        }
        
        Utils.SendGameData();

        Logger.Info("Successfully set everyone's data as Disconnected", "StartGameHost");

        yield return loadingBarManager.WaitAndSmoothlyUpdate(95f, 100f, 1f, GetString("LoadingBarText.1"));
        loadingBarManager.ToggleLoadingBar(false);

        Main.EnumeratePlayerControls().Do(SetRoleSelf);

        RpcSetRoleReplacer.EndReplace();


        yield return new WaitForSecondsRealtime(1.2f);

        foreach (PlayerControl pc in PlayerControl.AllPlayerControls)
        {
            if (!pc || !pc.Data) continue;

            bool disconnected = Main.PlayerStates.TryGetValue(pc.PlayerId, out var state) && state.IsDead && state.deathReason == PlayerState.DeathReason.Disconnected;
            pc.Data.Disconnected = disconnected;
        }
        
        Utils.SendGameData();
    }

    private static bool IsBasisChangingPlayer(byte id, CustomRoles role)
    {
        return BasisChangingAddons.TryGetValue(role, out List<byte> list) && list.Contains(id);
    }

    private static void AssignDesyncRole(CustomRoles role, PlayerControl player, Dictionary<byte, CustomRpcSender> senders, Dictionary<(byte, byte), (RoleTypes, CustomRoles)> rolesMap, RoleTypes baseRole, RoleTypes hostBaseRole = RoleTypes.Crewmate)
    {
        try
        {
            if (player == null) return;

            byte hostId = PlayerControl.LocalPlayer.PlayerId;
            bool isHost = player.PlayerId == hostId;

            Main.PlayerStates[player.PlayerId].SetMainRole(role);

            RoleTypes selfRole = isHost ? baseRole == RoleTypes.Shapeshifter ? RoleTypes.Shapeshifter : hostBaseRole : baseRole;
            RoleTypes othersRole = isHost ? RoleTypes.Crewmate : RoleTypes.Scientist;

            // Set Desync role for self and for others
            foreach (PlayerControl target in Main.EnumeratePlayerControls())
            {
                try
                {
                    RoleTypes targetRoleType = othersRole;
                    CustomRoles targetCustomRole = RoleResult.GetValueOrDefault(target.PlayerId, CustomRoles.CrewmateEndKnot);

                    if (targetCustomRole.GetVNRole() is CustomRoles.Noisemaker) targetRoleType = RoleTypes.Noisemaker;

                    rolesMap[(player.PlayerId, target.PlayerId)] = player.PlayerId != target.PlayerId ? (targetRoleType, targetCustomRole) : (selfRole, role);
                }
                catch (Exception e) { Utils.ThrowException(e); }
            }

            // Set Desync role for others
            foreach (PlayerControl seer in Main.EnumeratePlayerControls())
            {
                try
                {
                    if (player.PlayerId != seer.PlayerId)
                        rolesMap[(seer.PlayerId, player.PlayerId)] = (othersRole, role);
                }
                catch (Exception e) { Utils.ThrowException(e); }
            }


            RpcSetRoleReplacer.OverriddenSenderList.Add(senders[player.PlayerId]);

            // Set role for host, but not self
            // canOverride should be false for the host during assign
            if (!isHost) player.SetRole(othersRole, false);

            Logger.Info($"Registered Role: {player.Data?.PlayerName} => {role} : RoleType for self => {selfRole}, for others => {othersRole}", "AssignDesyncRoles");
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    private static void MakeDesyncSender(Dictionary<byte, CustomRpcSender> senders, Dictionary<(byte, byte), (RoleTypes, CustomRoles)> rolesMap)
    {
        try
        {
            foreach (PlayerControl seer in Main.EnumeratePlayerControls())
            {
                foreach (PlayerControl target in Main.EnumeratePlayerControls())
                {
                    try
                    {
                        if (seer.PlayerId == target.PlayerId || target.AmOwner) continue;

                        if (rolesMap.TryGetValue((seer.PlayerId, target.PlayerId), out (RoleTypes, CustomRoles) roleMap))
                        {
                            int targetClientId = target.OwnerId;
                            if (targetClientId == -1) continue;

                            RoleTypes roleType = roleMap.Item1;
                            CustomRpcSender sender = senders[seer.PlayerId];
                            if (sender.RpcSetRole(seer, roleType, targetClientId))
                                RpcSetRoleReplacer.SenderRpcCount[sender] = RpcSetRoleReplacer.SenderRpcCount.GetValueOrDefault(sender) + 1;
                        }
                    }
                    catch (Exception e) { Utils.ThrowException(e); }
                }
            }
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    private static void SetRoleSelf(PlayerControl target)
    {
        try
        {
            if (!target) return;

            int targetClientId = target.OwnerId;
            if (targetClientId == -1) return;

            RoleTypes roleType = RpcSetRoleReplacer.RoleMap.TryGetValue((target.PlayerId, target.PlayerId), out (RoleTypes RoleType, CustomRoles CustomRole) roleMap)
                ? roleMap.RoleType
                : RpcSetRoleReplacer.StoragedData[target.PlayerId];

            RoleTypes displayRole = roleType;

            if (Options.CurrentGameMode == CustomGameMode.Standard)
            {
                if (target.Is(Team.Crewmate) && roleType is not (RoleTypes.Crewmate or RoleTypes.Scientist or RoleTypes.Engineer or RoleTypes.Noisemaker or RoleTypes.Tracker or RoleTypes.Detective or RoleTypes.CrewmateGhost or RoleTypes.GuardianAngel))
                    displayRole = RoleTypes.Crewmate;

                if (target.Is(Team.Impostor) && roleType is not (RoleTypes.Impostor or RoleTypes.Shapeshifter or RoleTypes.Phantom or RoleTypes.Viper or RoleTypes.ImpostorGhost))
                    displayRole = RoleTypes.Impostor;

                if (target.Is(CustomRoles.Bloodlust))
                {
                    roleType = RoleTypes.Impostor;
                    displayRole = RoleTypes.Impostor;
                }
                else if (target.Is(CustomRoles.DoubleAgent))
                    displayRole = RoleTypes.Crewmate;
            }

            if (displayRole != roleType) RpcSetRoleReplacer.OverriddenTeamRevealScreen[target.PlayerId] = roleType;
            target.RpcSetRoleDesync(displayRole, targetClientId);
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    private static void AssignLoversRolesFromList()
    {
        try
        {
            if (CustomRoles.Lovers.IsEnable() && !RoleResult.ContainsValue(CustomRoles.Romantic))
            {
                Main.LoversPlayers.Clear();
                Main.IsLoversDead = false;
                AssignLoversRoles();
            }
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    private static void AssignLoversRoles(int rawCount = -1)
    {
        try
        {
            if (Lovers.LegacyLovers.GetBool())
            {
                Main.LoversPlayers = Main.EnumeratePlayerControls().Where(x => x.GetCustomRole() is CustomRoles.LovingCrewmate or CustomRoles.LovingImpostor).Take(2).ToList();
                return;
            }

            List<PlayerControl> allPlayers = Main.EnumeratePlayerControls().Where(pc => (!Main.NeverSpawnTogetherCombos.TryGetValue(OptionItem.CurrentPreset, out Dictionary<CustomRoles, List<CustomRoles>> bannedCombos) || bannedCombos.All(x => !pc.Is(x.Key) || !x.Value.Contains(CustomRoles.Lovers))) && !pc.Is(CustomRoles.GM) && (!pc.HasSubRole() || pc.GetCustomSubRoles().Count < Options.NoLimitAddonsNumMax.GetInt()) && pc.GetCustomRole() is not (CustomRoles.Altruist or CustomRoles.Provocateur or CustomRoles.Dictator or CustomRoles.DoubleAgent or CustomRoles.Nuker or CustomRoles.Bomber or CustomRoles.Curser or CustomRoles.Hater or CustomRoles.God) && (!pc.IsCrewmate() || Lovers.CrewCanBeInLove.GetBool()) && (!pc.GetCustomRole().IsNeutral() || Lovers.NeutralCanBeInLove.GetBool()) && (!pc.Is(CustomRoleTypes.Coven) || Lovers.CovenCanBeInLove.GetBool()) && (!pc.IsImpostor() || Lovers.ImpCanBeInLove.GetBool())).ToList();
            const CustomRoles role = CustomRoles.Lovers;
            int count = Math.Clamp(rawCount, 0, allPlayers.Count);
            if (rawCount == -1) count = Math.Clamp(role.GetCount(), 0, allPlayers.Count);

            if (count <= 0) return;

            for (var i = 0; i < count; i++)
            {
                PlayerControl player = allPlayers.RandomElement();
                Main.LoversPlayers.Add(player);
                allPlayers.Remove(player);
                Main.PlayerStates[player.PlayerId].SetSubRole(role);
                Logger.Info($"Add-on assigned: {player.Data?.PlayerName} = {player.GetCustomRole()} + {role}", "Assign Lovers");
            }

            RPC.SyncLoversPlayers();
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    // https://github.com/0xDrMoe/TownofHost-Enhanced/blob/41566d58e4217c38542df5b91f507045a6394908/Patches/onGameStartedPatch.cs#L667
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcSetRole))]
    [HarmonyPriority(Priority.High)]
    public static class RpcSetRoleReplacer
    {
        public static bool BlockSetRole;
        private static Dictionary<byte, CustomRpcSender> Senders = [];
        public static Dictionary<byte, RoleTypes> StoragedData = [];
        public static Dictionary<(byte SeerID, byte TargetID), (RoleTypes RoleType, CustomRoles CustomRole)> RoleMap = [];
        public static List<CustomRpcSender> OverriddenSenderList = [];
        public static Dictionary<byte, RoleTypes> OverriddenTeamRevealScreen = [];

        // --- /burst intro-armed config (set from lobby chat; fires the client-authority forge burst at the next
        // game's intro, which is the timing that matches the real kick — unlike an in-game /burst which fires now).
        // Stays armed across games until /burst off. Takes precedence over debug_burst.txt. ---
        public static int ArmedBurstCount;    // > 0 = armed
        public static bool ArmedBurstProtect; // ProtectPlayer vs MurderPlayer/FailedProtected
        public static bool ArmedBurstNone;    // SendOption.None vs Reliable

        // --- Assignment-burst instrumentation ---
        // When RecordSetRoles is on (only during role assignment), every CustomRpcSender.RpcSetRole
        // appends (player, role, targetClientId) here. Used to measure the assignment burst's RPC count
        // and reconstruct each client's effective view (last-write-wins) for behavior-diffing builds.
        // A passing 2-player local test is NOT evidence of a fix; this lets us read the actual count.
        public static bool RecordSetRoles;
        public static readonly List<(byte Player, ushort Role, int TargetClientId)> SetRoleLog = [];

        // Per-sender SetRole RPC count, so Release() can pace each sender through DataFlagRateLimiter with
        // the correct cost (calls). On the official server the assignment burst (hundreds of reliable
        // GameDataTo RPCs in one frame) exceeds the ~23 RPC/sec rate limit and self-kicks the host;
        // routing the flush through the limiter spreads it out. Identical RPCs/order = no behavior change.
        public static readonly Dictionary<CustomRpcSender, int> SenderRpcCount = [];

        public static void Initialize()
        {
            BlockSetRole = true;
            Senders = [];
            RoleMap = [];
            StoragedData = [];
            OverriddenSenderList = [];
            OverriddenTeamRevealScreen = [];
        }

        // ReSharper disable once MemberHidesStaticFromOuterClass
        // ReSharper disable once UnusedMember.Global
        public static bool Prefix()
        {
            return !BlockSetRole;
        }

        public static void StartReplace()
        {
            try
            {
                SetRoleLog.Clear();
                SenderRpcCount.Clear();
                RecordSetRoles = true;

                foreach (PlayerControl pc in Main.EnumeratePlayerControls())
                {
                    try
                    {
                        Senders[pc.PlayerId] = CustomRpcSender.Create($"{pc.name}'s SetRole Sender", SendOption.Reliable)
                            .StartMessage(pc.OwnerId);
                    }
                    catch (Exception e) { Utils.ThrowException(e); }
                }
            }
            catch (Exception e) { Utils.ThrowException(e); }
        }

        public static void AssignDesyncRoles()
        {
            // Assign desync roles
            try
            {
                foreach ((byte playerId, CustomRoles role) in RoleResult)
                {
                    try
                    {
                        if (role.IsDesyncRole() || IsBasisChangingPlayer(playerId, CustomRoles.Bloodlust))
                            AssignDesyncRole(role, Utils.GetPlayerById(playerId), Senders, RoleMap, ForceImp(playerId) ? RoleTypes.Impostor : role.GetDYRole());
                    }
                    catch (Exception e) { Utils.ThrowException(e); }
                }
            }
            catch (Exception e) { Utils.ThrowException(e); }

            return;

            bool ForceImp(byte id) => IsBasisChangingPlayer(id, CustomRoles.Bloodlust) || (Options.CurrentGameMode == CustomGameMode.Speedrun && Speedrun.CanKill.Contains(id));
        }

        public static void SendRpcForDesync()
        {
            try { MakeDesyncSender(Senders, RoleMap); }
            catch (Exception e) { Utils.ThrowException(e); }
        }

        public static void AssignNormalRoles()
        {
            try
            {
                List<byte> doneIds = [];

                try
                {
                    foreach ((byte playerId, CustomRoles role) in RoleResult)
                    {
                        try
                        {
                            PlayerControl player = Utils.GetPlayerById(playerId);
                            if (player == null || role.IsDesyncRole()) continue;

                            if (Options.CurrentGameMode == CustomGameMode.Speedrun && Speedrun.CanKill.Contains(playerId)) continue;

                            RoleTypes roleType = role.GetRoleTypes();

                            if (BasisChangingAddons.FindFirst(x => x.Value.Contains(playerId), out KeyValuePair<CustomRoles, List<byte>> kvp))
                            {
                                if (kvp.Key == CustomRoles.Bloodlust) continue;

                                roleType = kvp.Key switch
                                {
                                    CustomRoles.Nimble => RoleTypes.Engineer,
                                    CustomRoles.Physicist => RoleTypes.Scientist,
                                    CustomRoles.Finder => RoleTypes.Tracker,
                                    CustomRoles.Noisy => RoleTypes.Noisemaker,
                                    CustomRoles.Examiner => RoleTypes.Detective,
                                    CustomRoles.Venom => RoleTypes.Viper,
                                    _ => roleType
                                };
                            }

                            if (Options.EveryoneCanVent.GetBool() && (roleType == RoleTypes.Crewmate || (Options.OverrideOtherCrewBasedRoles.GetBool() && roleType is RoleTypes.Scientist or RoleTypes.Detective or RoleTypes.Noisemaker or RoleTypes.Tracker)))
                                roleType = RoleTypes.Engineer;

                            StoragedData[playerId] = roleType;
                            doneIds.Add(playerId);

                            foreach (PlayerControl target in Main.EnumeratePlayerControls())
                            {
                                try
                                {
                                    if (RoleResult.TryGetValue(target.PlayerId, out CustomRoles targetRole) && targetRole.IsDesyncRole() && !target.IsHost()) continue;
                                    RoleMap[(target.PlayerId, playerId)] = (roleType, role);
                                }
                                catch (Exception e) { Utils.ThrowException(e); }
                            }

                            if (playerId != PlayerControl.LocalPlayer.PlayerId)
                            {
                                // canOverride should be false for the host during assign
                                player.SetRole(roleType, false);
                            }

                            Logger.Info($"Set original role type => {player.GetRealName()}: {role} => {role.GetRoleTypes()}", "AssignNormalRoles");
                        }
                        catch (Exception e) { Utils.ThrowException(e); }
                    }
                }
                catch (Exception e) { Utils.ThrowException(e); }

                try
                {
                    foreach (PlayerControl pc in Main.EnumeratePlayerControls())
                    {
                        try
                        {
                            if (!doneIds.Contains(pc.PlayerId))
                            {
                                StoragedData[pc.PlayerId] = RoleTypes.Crewmate;

                                foreach (PlayerControl target in Main.EnumeratePlayerControls())
                                {
                                    try
                                    {
                                        if (RoleResult.TryGetValue(target.PlayerId, out CustomRoles targetRole) && targetRole.IsDesyncRole() && !target.IsHost()) continue;
                                        RoleMap[(target.PlayerId, pc.PlayerId)] = (RoleTypes.Crewmate, CustomRoles.CrewmateEndKnot);
                                    }
                                    catch (Exception e) { Utils.ThrowException(e); }
                                }
                            }
                        }
                        catch (Exception e) { Utils.ThrowException(e); }
                    }
                }
                catch (Exception e) { Utils.ThrowException(e); }
            }
            catch (Exception e) { Utils.ThrowException(e); }
        }

        public static void SendRpcForNormal()
        {
            foreach ((byte targetId, CustomRpcSender sender) in Senders)
            {
                try
                {
                    PlayerControl target = Utils.GetPlayerById(targetId);
                    if (OverriddenSenderList.Contains(sender)) continue;

                    if (sender.CurrentState != CustomRpcSender.State.InRootMessage) throw new InvalidOperationException("A CustomRpcSender had Invalid State.");

                    foreach ((byte seerId, RoleTypes roleType) in StoragedData)
                    {
                        try
                        {
                            if (targetId == seerId || targetId == PlayerControl.LocalPlayer.PlayerId) continue;

                            PlayerControl seer = Utils.GetPlayerById(seerId);
                            if (seer == null || target == null) continue;

                            int targetClientId = target.OwnerId;
                            if (targetClientId == -1) continue;

                            if (sender.RpcSetRole(seer, roleType, targetClientId, false))
                                SenderRpcCount[sender] = SenderRpcCount.GetValueOrDefault(sender) + 1;
                        }
                        catch (Exception e) { Utils.ThrowException(e); }
                    }

                    sender.EndMessage();
                }
                catch (Exception e) { Utils.ThrowException(e); }
            }
        }

        public static void Release()
        {
            try
            {
                BlockSetRole = false;

                // Budget gate: the proven kick fires on THIS burst. Log its size (before SetRoleSelf adds
                // the smaller self-role pass) so a real game can show whether it stays under the official
                // server's ~23 reliable-RPC/sec budget without needing a full 13-player official lobby.
                if (RecordSetRoles)
                    Logger.Info($"Release burst = {SetRoleLog.Count} SetRole RPCs (broadcast={SetRoleLog.Count(x => x.TargetClientId < 0)}), server={GameStates.CurrentServerType}, clients={Main.EnumeratePlayerControls().Count(x => x.OwnerId >= 0)}", "SetRoleBurst");

                foreach (CustomRpcSender sender in Senders.Values)
                {
                    try
                    {
                        CustomRpcSender captured = sender;
                        int calls = SenderRpcCount.GetValueOrDefault(captured);

                        // Pace each flush through the rate limiter so the one-frame assignment burst spreads
                        // under the official server's reliable-RPC/sec cap (the proven Hacking-kick trigger).
                        // Same RPCs, same per-client views, same relative order — just sent later. No new role
                        // state lands on any client, so the desync isolation (and team-reveal) is unaffected.
                        // Enqueue gates on CurrentServerType: on custom/modded servers it runs immediately,
                        // so behavior there is byte-for-byte identical to the previous direct SendMessage().
                        DataFlagRateLimiter.Enqueue(() => captured.SendMessage(), SendOption.Reliable, calls);
                    }
                    catch (Exception e) { Utils.ThrowException(e); }
                }

                InjectDebugBurst();
            }
            catch (Exception e) { Utils.ThrowException(e); }
        }

        // TEST AID — validate the kick fix with only 1-2 real clients instead of a full lobby.
        // Reads EndKnot_DATA/debug_burst.txt (next to AlwaysCombos.json). First token = count N (>0).
        // Remaining tokens (any order, case-insensitive) select what to fire:
        //   (none)   -> N host-authority SetRole RPCs through the rate limiter (proven-exempt control: 203-burst did NOT kick)
        //   direct   -> SetRole burst with a direct flush, bypassing the limiter
        //   murder   -> N client-authority MurderPlayer(FailedProtected) forged onto the vanilla client's NetId (= the shield shape)
        //   protect  -> N client-authority ProtectPlayer forged onto the vanilla client's NetId (= the intro ability-reset shape)
        //   none     -> send the murder/protect burst on SendOption.None instead of Reliable (TOHP's channel)
        //   absent / "0" -> off (zero cost — File.Exists short-circuits)
        // DECISIVE EXPERIMENT (isolates Reliable-vs-None at fixed burst size, the one variable the 203-SetRole test couldn't move):
        //   "200 murder"      => expect kick (if a Reliable client-authority burst is the trigger)
        //   "200 murder none" => expect NO kick (validates the "downgrade to None" fix); same for "200 protect[ none]".
        // All forges are harmless: FailedProtected = no real death; ProtectPlayer(self,0) = brief self-shield. SetRole dummies are idempotent.
        private static void InjectDebugBurst()
        {
            try
            {
                // Source 1: /burst armed from lobby chat — fires the client-authority forge burst at intro (real-kick timing).
                if (ArmedBurstCount > 0)
                {
                    Logger.Info($"DebugBurst: ARMED intro burst firing ({ArmedBurstCount} {(ArmedBurstProtect ? "Protect" : "Murder")} {(ArmedBurstNone ? "None" : "Reliable")})", "SetRoleBurst");
                    FireClientAuthForgeBurst(ArmedBurstCount, ArmedBurstProtect, ArmedBurstNone ? SendOption.None : SendOption.Reliable);
                    return;
                }

                // Source 2: debug_burst.txt (also supports the host-authority SetRole control path).
                string path = $"{Main.DataPath}/EndKnot_DATA/debug_burst.txt";
                if (!System.IO.File.Exists(path)) return;

                string[] parts = System.IO.File.ReadAllText(path).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || !int.TryParse(parts[0], out int count) || count <= 0) return;

                HashSet<string> flags = parts.Skip(1).Select(p => p.ToLowerInvariant()).ToHashSet();
                bool bypassThrottle = flags.Contains("direct");
                bool useNone = flags.Contains("none");
                bool protectMode = flags.Contains("protect");

                // Client-authority forge test (= the /burst command's payload). See FireClientAuthForgeBurst.
                if (flags.Contains("murder") || protectMode)
                {
                    FireClientAuthForgeBurst(count, protectMode, useNone ? SendOption.None : SendOption.Reliable);
                    return;
                }

                PlayerControl target = Main.EnumeratePlayerControls().FirstOrDefault(p => p != null && !p.AmOwner && p.OwnerId >= 0 && !p.IsModdedClient());
                if (target == null)
                {
                    Logger.Warn("DebugBurst: no non-modded client to target (need a vanilla joiner)", "SetRoleBurst");
                    return;
                }

                RoleTypes role = target.GetRoleTypes();
                Logger.Info($"DebugBurst: injecting {count} dummy SetRole RPCs to client {target.OwnerId} (bypassThrottle={bypassThrottle})", "SetRoleBurst");

                CustomRpcSender sender = CustomRpcSender.Create("DebugBurst", SendOption.Reliable, log: false).StartMessage(target.OwnerId);
                for (var i = 0; i < count; i++) sender.RpcSetRole(target, role, target.OwnerId, noRpcForSelf: false);
                sender.EndMessage();

                if (bypassThrottle)
                    sender.SendMessage(); // direct flush = old un-throttled path; should reproduce the Hacking kick at high count
                else
                    DataFlagRateLimiter.Enqueue(() => sender.SendMessage(), SendOption.Reliable, count); // paced; host should survive
            }
            catch (Exception e) { Utils.ThrowException(e); }
        }

        // Forges `count` client-authority RPCs onto the first non-modded (vanilla) client's own NetId:
        // MurderPlayer/FailedProtected (the shield shape, ExtendedPlayerControl.cs:906) by default, or ProtectPlayer
        // (the intro ability-reset shape, :1097) when protectMode. SendOption (Reliable vs None) is the one variable the
        // host-authority 203-SetRole burst test could not move. Shared by debug_burst.txt and the /burst chat command.
        // Both forges are harmless: FailedProtected = no real death, ProtectPlayer(self,0) = 0s self-shield. Returns a status string.
        public static string FireClientAuthForgeBurst(int count, bool protectMode, SendOption sendOpt)
        {
            PlayerControl target = Main.EnumeratePlayerControls().FirstOrDefault(p => p != null && !p.AmOwner && p.OwnerId >= 0 && !p.IsModdedClient());
            if (target == null) return "DebugBurst: no non-modded (vanilla) client connected to target";

            byte rpc = (byte)(protectMode ? RpcCalls.ProtectPlayer : RpcCalls.MurderPlayer);
            string channel = sendOpt == SendOption.None ? "None" : "Reliable";
            Logger.Info($"DebugBurst: forging {count} {(protectMode ? "ProtectPlayer" : "MurderPlayer/FailedProtected")} RPCs onto client {target.OwnerId}'s NetId ({channel})", "SetRoleBurst");

            for (var i = 0; i < count; i++)
            {
                MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(target.NetId, rpc, sendOpt, target.OwnerId);
                writer.WriteNetObject(target);
                writer.Write(protectMode ? 0 : (int)MurderResultFlags.FailedProtected);
                AmongUsClient.Instance.FinishRpcImmediately(writer);
            }

            return $"DebugBurst: forged {count} {(protectMode ? "ProtectPlayer" : "MurderPlayer")} onto client {target.OwnerId} ({channel}). Watch for a Hacking kick.";
        }

        // Logs the assignment burst's SetRole RPC count (budget gate) and each client's reconstructed
        // effective view (correctness gate). Reading these from a real multi-client game tells us
        // whether the burst stays under the official-server ~23 RPC/sec budget and whether a later
        // collapse change is behavior-preserving (diff the per-client views across builds).
        private static void DumpAssignmentInstrumentation()
        {
            try
            {
                if (!RecordSetRoles) return;
                RecordSetRoles = false;

                int total = SetRoleLog.Count;
                int broadcasts = SetRoleLog.Count(x => x.TargetClientId < 0);
                int perClient = total - broadcasts;

                Logger.Info($"total SetRole RPCs={total} (broadcast={broadcasts}, per-client={perClient}), server={GameStates.CurrentServerType}, clients={Main.EnumeratePlayerControls().Count(x => x.OwnerId >= 0)}", "SetRoleBurst");

                // Reconstruct each client's effective view (last-write-wins, in send order).
                var view = new Dictionary<int, Dictionary<byte, ushort>>();

                foreach ((byte player, ushort role, int targetClientId) in SetRoleLog)
                {
                    if (targetClientId < 0)
                    {
                        foreach (PlayerControl pc in Main.EnumeratePlayerControls())
                        {
                            int cid = pc.OwnerId;
                            if (cid < 0) continue;
                            if (!view.TryGetValue(cid, out Dictionary<byte, ushort> m)) view[cid] = m = [];
                            m[player] = role;
                        }
                    }
                    else
                    {
                        if (!view.TryGetValue(targetClientId, out Dictionary<byte, ushort> m)) view[targetClientId] = m = [];
                        m[player] = role;
                    }
                }

                foreach ((int cid, Dictionary<byte, ushort> m) in view.OrderBy(x => x.Key))
                {
                    string s = string.Join(",", m.OrderBy(x => x.Key).Select(x => $"{x.Key}:{(RoleTypes)x.Value}"));
                    Logger.Info($"client {cid} sees => {s}", "SetRoleBurst");
                }

                SetRoleLog.Clear();
            }
            catch (Exception e) { Utils.ThrowException(e); }
        }

        public static void EndReplace()
        {
            try
            {
                // Runs after SetRoleSelf (1174), so the log now holds the full assignment SetRole pass
                // (Release burst + per-client desync + self-roles). Dump the reconstructed per-client views.
                DumpAssignmentInstrumentation();

                Senders = null;
                OverriddenSenderList = null;
                StoragedData = null;
            }
            catch (Exception e) { Utils.ThrowException(e); }
        }

        public static void SetActualSelfRolesAfterOverride()
        {
            foreach ((byte id, RoleTypes roleTypes) in OverriddenTeamRevealScreen)
            {
                PlayerControl pc = id.GetPlayer();
                if (pc == null || !pc.IsAlive()) continue;

                int targetClientId = pc.OwnerId;
                if (targetClientId == -1) continue;

                RoleTypes actualRoleType = BasisChangingAddons.FindFirst(x => x.Value.Contains(id), out KeyValuePair<CustomRoles, List<byte>> kvp)
                    ? kvp.Key switch
                    {
                        CustomRoles.Bloodlust when roleTypes is RoleTypes.Crewmate or RoleTypes.Engineer or RoleTypes.Scientist or RoleTypes.Tracker or RoleTypes.Noisemaker => RoleTypes.Impostor,
                        CustomRoles.Nimble when roleTypes is RoleTypes.Crewmate or RoleTypes.Scientist or RoleTypes.Noisemaker or RoleTypes.Tracker => RoleTypes.Engineer,
                        CustomRoles.Physicist when roleTypes == RoleTypes.Crewmate => RoleTypes.Scientist,
                        CustomRoles.Finder when roleTypes is RoleTypes.Crewmate or RoleTypes.Scientist or RoleTypes.Noisemaker => RoleTypes.Tracker,
                        CustomRoles.Noisy when roleTypes == RoleTypes.Crewmate => RoleTypes.Noisemaker,
                        CustomRoles.Examiner when roleTypes is RoleTypes.Crewmate or RoleTypes.Scientist or RoleTypes.Noisemaker => RoleTypes.Detective,
                        CustomRoles.Venom when roleTypes == RoleTypes.Impostor => RoleTypes.Viper,
                        _ => roleTypes
                    }
                    : roleTypes;

                pc.RpcSetRoleDesync(actualRoleType, targetClientId);

                LateTask.New(() =>
                {
                    pc.RpcResetAbilityCooldown();
                    pc.SetKillCooldown(10f);
                }, 0.2f, log: false);
            }

            OverriddenTeamRevealScreen = null;
        }
    }
}
