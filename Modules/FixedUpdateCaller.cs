using System;
using System.Linq;
using EndKnot.Gamemodes;
using EndKnot.Modules.YouTubeChat;
using EndKnot.Patches;
using HarmonyLib;
using InnerNet;

namespace EndKnot.Modules;

[HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.FixedUpdate))]
public static class FixedUpdateCaller
{
    // kill ボタンのターゲット走査 1 回分だけ有効な LocalPlayer 側の役職と真位置 (走査の前に設定し、直後に役職は null へ戻す)。
    private static RoleBehaviour KillScanLocalRole;
    private static UnityEngine.Vector2 KillScanLocalPos;

    private static bool IsValidKillTargetThisTick(PlayerControl pc)
    {
        RoleBehaviour role = KillScanLocalRole;
        return role != null ? pc.IsValidTargetForKillButton(role, KillScanLocalPos) : pc.IsValidTargetForKillButton();
    }

    private static int NonLowLoadPlayerIndex;

    private static long LastFileLoadTS;
    private static long LastAutoMessageSendTS;

    // ReSharper disable once UnusedMember.Global
    public static void Postfix()
    {
        var alloc = AllocProbe.Now(); // 系統別アロケ帰属 (詳細は AllocProbe)

        try
        {
            BootTimeline.NoteFirstTick();

            PerSecondUpdateScheduler.OnFixedUpdate();

            try { HealthLog.Tick(); }
            catch (Exception e) { Utils.ThrowException(e); }

            // UI/テキスト異常(stale/重複/別オブジェクト混線した補助 TMP)をクラッシュなしで観測・記録する。
            // 既定OFF: シーン全体の FindObjectsOfType<TMP_Text> 走査が長時間で native working set を単調に
            // 押し上げる(IL2CPP プロキシ churn)ため、UI 破損の診断時のみ EnableUiAnomalyWatch で有効化する。1/sec ゲート。
            try { if (Main.EnableUiAnomalyWatch.Value && PerSecondUpdateScheduler.ShouldRunUpdate("ui-anomaly")) UiAnomalyWatch.Scan(); }
            catch (Exception e) { Utils.ThrowException(e); }

            // クラッシュ復帰の番犬(外部ウォッチドッグ)を CrashWatchdog オプションに追従させる。
            // HUD/ローカルプレイヤー非依存でメニューからも武装できるようここで回す。1/sec ゲート。
            try { if (PerSecondUpdateScheduler.ShouldRunUpdate("watchdog-reconcile")) WatchdogLauncher.ReconcileWithOption(); }
            catch (Exception e) { Utils.ThrowException(e); }

            // AI実況相棒アプリを EnableAICommentary オプションに追従させる (ON=子プロセス起動 / OFF=停止)。1/sec ゲート。
            try { if (PerSecondUpdateScheduler.ShouldRunUpdate("companion-reconcile")) Companion.CompanionLauncher.ReconcileWithOption(); }
            catch (Exception e) { Utils.ThrowException(e); }

            // Claude 遠隔テストブリッジ(既定OFF)。コマンドファイルのポーリング+自動スクショを 1/sec ゲートで回す。
            var sub = AllocProbe.Now(); // misc の内訳 (misc.* は tickKB へ二重計上されない)
            try { if (PerSecondUpdateScheduler.ShouldRunUpdate("test-bridge")) TestBridge.Tick(); }
            catch (Exception e) { Utils.ThrowException(e); }

            sub = AllocProbe.Mark("misc.bridge", sub);

            // 当たり判定可視化 (/hitbox・既定OFF) の TTL 掃除。Enabled でなくても残存形状の破棄が要るため無条件で回す。
            try { HitboxDebug.Tick(); }
            catch (Exception e) { Utils.ThrowException(e); }

            sub = AllocProbe.Mark("misc.hitbox", sub);

            // BGM の番犬。死亡/GM ホストへのスロット切替と、外部要因で音源を失った時の復帰。1/sec ゲート。
            try { if (PerSecondUpdateScheduler.ShouldRunUpdate("bgm-watchdog")) BGMManager.Tick(); }
            catch (Exception e) { Utils.ThrowException(e); }

            sub = AllocProbe.Mark("misc.bgm", sub);

            // BGM デコードバッファ在庫 (最大 ~45MB×2) のアイドル解放。曲替えが止まって5分で発火。
            try { if (PerSecondUpdateScheduler.ShouldRunUpdate("decode-pool-trim")) CustomSoundsManager.TrimIdleDecodePool(); }
            catch (Exception e) { Utils.ThrowException(e); }

            sub = AllocProbe.Mark("misc.pool", sub);

            // チャット open/close 状態を毎フレーム overlay に反映。ローカルプレイヤー非依存で回す
            // (メニュー・非ゲーム中でもチャットが開くため、LocalPlayer ガードの中では遅すぎる)。
            try { TextBoxPatch.CheckChatOpen(); }
            catch (Exception e) { Utils.ThrowException(e); }

            AllocProbe.Mark("misc.chat", sub);
            alloc = AllocProbe.Mark("misc", alloc);

            var amongUsClient = AmongUsClient.Instance;
            var lobbyBehaviour = LobbyBehaviour.Instance;

            // ゲーム join の瞬間(シーン再構築中)は lobbyBehaviour/HudManager 配下のフィールドが一過性に
            // fake-null になり、無ガードの deref が数フレーム連続 NRE を吐く(exflood 警告の主因)。各ブロックを
            // 個別 try/catch で包み、join 窓のスパムを黙らせる(窓が抜ければ自然に正常化する)。
            try
            {
                if (lobbyBehaviour)
                {
                    LobbyFixedUpdatePatch.Postfix();
                    LobbyBehaviourUpdatePatch.Postfix(lobbyBehaviour);

                    long now = Utils.TimeStamp;

                    if (now - LastFileLoadTS > 10)
                    {
                        LastFileLoadTS = now;
                        Options.LoadUserData();
                    }

                    if (Options.EnableAutoMessage.GetBool() && now - LastAutoMessageSendTS > Options.AutoMessageSendInterval.GetInt())
                    {
                        LastAutoMessageSendTS = now;
                        TemplateManager.SendTemplate("Notification", noErr: true, importance: MessageImportance.Low);
                    }
                }
            }
            catch (Exception e)
            {
                if (OnGameJoinedPatch.JoiningGame && e is NullReferenceException) { /* join 窓の transient fake-null は黙殺 */ }
                else Utils.ThrowException(e);
            }

            alloc = AllocProbe.Mark("lobby", alloc);

            try
            {
                if (HudManager.InstanceExists)
                {
                    HudManager hudManager = HudManager.Instance;

                    HudManagerPatch.Postfix(hudManager);
                    alloc = AllocProbe.Mark("hud", alloc);
                    Zoom.Postfix();
                    alloc = AllocProbe.Mark("zoom", alloc);
                    HudSpritePatch.Postfix(hudManager);
                }
            }
            catch (Exception e)
            {
                if (OnGameJoinedPatch.JoiningGame && e is NullReferenceException) { /* join 窓の transient fake-null は黙殺 */ }
                else Utils.ThrowException(e);
            }

            alloc = AllocProbe.Mark("hudspr", alloc);

            // YouTube chat polling は HUD の有無と無関係に進める（ロビーから動かす前提）
            try { YouTubeChatManager.Tick(UnityEngine.Time.fixedDeltaTime); }
            catch (Exception e)
            {
                if (OnGameJoinedPatch.JoiningGame && e is NullReferenceException) { /* join 窓の transient fake-null は黙殺 */ }
                else Utils.ThrowException(e);
            }

            // YouTube ライブチャットへの自動投稿 (ホストローカル・HTTPのみ、RPCなし)
            try { YouTubeChatPoster.Tick(UnityEngine.Time.fixedDeltaTime); }
            catch (Exception e)
            {
                if (OnGameJoinedPatch.JoiningGame && e is NullReferenceException) { /* join 窓の transient fake-null は黙殺 */ }
                else Utils.ThrowException(e);
            }

            // 視聴者干渉 (Audience)。queue drain + キュー消化はメインスレッド専用。
            try { EndKnot.Modules.Audience.AudienceManager.Tick(); }
            catch (Exception e)
            {
                if (OnGameJoinedPatch.JoiningGame && e is NullReferenceException) { /* join 窓の transient fake-null は黙殺 */ }
                else Utils.ThrowException(e);
            }

            // ホストローカルの読み上げ (VoiceVox TTS)。メインスレッド必須なのでここで drain する。
            try { EndKnot.Modules.VoiceVox.VoiceVoxManager.Tick(); }
            catch (Exception e) { Utils.ThrowException(e); }

            // サウンドの起動時プリロード (背景スレッドで先行デコード → ここで 1 tick 1 クリップ化)。
            // 初回再生時の同期フルデコードによるフレームストール対策。
            try { CustomSoundsManager.PreloadTick(); }
            catch (Exception e) { Utils.ThrowException(e); }

            // AI実況相棒アプリ向けイベント出力層 (join/leave/chat/intervention/phase/demo)。送信ゼロ・ホストローカルのみ。
            try { EndKnot.Modules.Companion.CompanionEventEmitter.Tick(); }
            catch (Exception e)
            {
                if (OnGameJoinedPatch.JoiningGame && e is NullReferenceException) { /* join 窓の transient fake-null は黙殺 */ }
                else Utils.ThrowException(e);
            }

            try { DataFlagRateLimiter.OnFixedUpdate(); }
            catch (Exception e) { Utils.ThrowException(e); }

            try { PacketRateGate.OnFixedUpdate(); }
            catch (Exception e) { Utils.ThrowException(e); }

            // ローディング画面動画 (ホストローカル描画のみ)。シーン遷移中も生き延びる HudManager 配下なので
            // InGame/AmHost に依存せず無条件で回す。
            try { EndKnot.Modules.Media.LoadingScreenVideo.Tick(); }
            catch (Exception e) { Utils.ThrowException(e); }

            // メニュー背景の火エフェクト (ホストローカル描画のみ)。メニュー以外では即 return する。
            try { EndKnot.Patches.CalamityMenu.CalamityFire.Tick(); }
            catch (Exception e) { Utils.ThrowException(e); }

            alloc = AllocProbe.Mark("svc", alloc);

            if (!PlayerControl.LocalPlayer) return;

            var killStart = alloc;

            if (amongUsClient.IsGameStarted)
                Utils.CountAlivePlayers();

            alloc = AllocProbe.Mark("kill.count", alloc);

            try
            {
                PlayerControl lp = PlayerControl.LocalPlayer; // 1 tick 内で 1 回だけ wrapper を取る

                if (HudManager.InstanceExists && GameStates.IsInTask && !ExileController.Instance && !AntiBlackout.SkipTasks && lp.CanUseKillButton())
                {
                    Predicate<PlayerControl> predicate = amongUsClient.AmHost
                        ? Options.CurrentGameMode switch
                        {
                            CustomGameMode.BedWars => BedWars.IsNotInLocalPlayersTeam,
                            CustomGameMode.CaptureTheFlag => CaptureTheFlag.IsNotInLocalPlayersTeam,
                            CustomGameMode.KingOfTheZones => KingOfTheZones.IsNotInLocalPlayersTeam,
                            _ => IsValidKillTargetThisTick
                        }
                        : IsValidKillTargetThisTick;

                    // 走査中の全ターゲットで同じ値を読む LocalPlayer 側の役職と真位置は、ターゲット毎に取り直さず tick 内で 1 回にする。
                    KillScanLocalRole = lp.Data.Role;
                    KillScanLocalPos = lp.GetTruePosition();

                    PlayerControl closest = FastVector2.TryGetClosestPlayerInRangeTo(lp, lp.GetKillDistance(), out PlayerControl closestPlayer, predicate) ? closestPlayer : null;

                    KillScanLocalRole = null;

                    KillButton killButton = HudManager.Instance.KillButton;
                    PlayerControl currentTarget = killButton.currentTarget;

                    if (currentTarget && currentTarget != closest)
                        currentTarget.ToggleHighlight(false, RoleTeamTypes.Impostor);

                    killButton.currentTarget = closest;

                    if (closest)
                    {
                        closest.ToggleHighlight(true, RoleTeamTypes.Impostor);
                        killButton.SetEnabled();
                    }
                    else
                        killButton.SetDisabled();
                }
            }
            catch { }

            alloc = AllocProbe.Mark("kill.scan", alloc);

            try
            {
                if (amongUsClient.AmHost && GameStates.InGame && !GameStates.IsEnded)
                    FixedUpdatePatch.LoversSuicide();
            }
            catch (Exception e) { Utils.ThrowException(e); }

            alloc = AllocProbe.Mark("kill.lovers", alloc);
            alloc = AllocProbe.Mark("kill", killStart);

            bool lobby = GameStates.IsLobby;
            FixedUpdatePatch.AmHostTick = amongUsClient.AmHost; // 個別 tick 経路の getter 呼びを 1 回に畳む (FixedUpdatePatch.AmHostTick 参照)

            if (lobby || (Main.IntroDestroyed && GameStates.InGame && !GameStates.IsMeeting && !ExileController.Instance && !AntiBlackout.SkipTasks))
            {
                NonLowLoadPlayerIndex++;

                int count = PlayerControl.AllPlayerControls.Count;

                if (NonLowLoadPlayerIndex >= count)
                    NonLowLoadPlayerIndex = Math.Min(0, -(30 - count));

                CustomGameMode currentGameMode = Options.CurrentGameMode;
                //bool vanilla = GameStates.CurrentServerType == GameStates.ServerType.Vanilla;

                for (var index = 0; index < count; index++)
                {
                    try
                    {
                        // 帰属計器: pcloop の子 (pc.*) は DoPostfix 内だけを覆っていて、7 人卓で il2 の約 2/3 が
                        // 子の外に残った (2026-09-07 第35弾)。ループ本体 / Postfix 頭 / モード別 / 移動検査を個別に切る。
                        var pcCur = AllocProbe.Now();
                        PlayerControl pc = PlayerControl.AllPlayerControls[index];

                        if (!pc || pc.PlayerId >= 200) continue;

                        pcCur = AllocProbe.Mark("pcloop.iter", pcCur);
                        FixedUpdatePatch.Postfix(pc, NonLowLoadPlayerIndex != index);
                        pcCur = AllocProbe.Now();

                        if (lobby) continue;

                        // if (vanilla && NonLowLoadPlayerIndex == index)
                        //     Utils.NotifyRoles(SpecifySeer: pc, ForceLoop: true, SendOption: Hazel.SendOption.None);

                        switch (currentGameMode)
                        {
                            case CustomGameMode.CaptureTheFlag:
                                CaptureTheFlag.FixedUpdatePatch.Postfix(pc);
                                break;
                            case CustomGameMode.HotPotato:
                                HotPotato.FixedUpdatePatch.Postfix(pc);
                                break;
                            case CustomGameMode.StopAndGo:
                                StopAndGo.FixedUpdatePatch.Postfix(pc);
                                break;
                            case CustomGameMode.SoloPVP:
                                SoloPVP.FixedUpdatePatch.Postfix(pc);
                                break;
                            case CustomGameMode.Speedrun:
                                Speedrun.FixedUpdatePatch.Postfix(pc);
                                break;
                            case CustomGameMode.BedWars:
                                BedWars.FixedUpdatePatch.Postfix(pc);
                                break;
                            case CustomGameMode.Snowdown:
                                Snowdown.FixedUpdatePatch.Postfix(pc);
                                break;
                        }

                        pcCur = AllocProbe.Mark("pcloop.mode", pcCur);
                        CheckInvalidMovementPatch.Postfix(pc);
                        AllocProbe.Mark("pcloop.cim", pcCur);
                    }
                    catch (Exception e) { Utils.ThrowException(e); }
                }

                alloc = AllocProbe.Mark("pcloop", alloc);


                if (lobby) return;

                try
                {
                    switch (currentGameMode)
                    {
                        case CustomGameMode.HideAndSeek:
                            CustomHnS.FixedUpdatePatch.Postfix();
                            goto default;
                        case CustomGameMode.FFA:
                            FreeForAll.FixedUpdatePatch.Postfix();
                            goto default;
                        case CustomGameMode.KingOfTheZones:
                            KingOfTheZones.FixedUpdatePatch.Postfix();
                            goto default;
                        case CustomGameMode.NaturalDisasters:
                            NaturalDisasters.FixedUpdatePatch.Postfix();
                            break;
                        case CustomGameMode.Quiz:
                            Quiz.FixedUpdatePatch.Postfix();
                            goto default;
                        case CustomGameMode.RoomRush:
                            RoomRush.FixedUpdatePatch.Postfix();
                            goto default;
                        case CustomGameMode.Deathrace:
                            Deathrace.FixedUpdatePatch.Postfix();
                            goto default;
                        case CustomGameMode.Mingle:
                            Mingle.FixedUpdatePatch.Postfix();
                            goto default;
                        default:
                            if (Options.IntegrateNaturalDisasters.GetBool()) goto case CustomGameMode.NaturalDisasters;
                            break;
                    }
                }
                catch (Exception e) { Utils.ThrowException(e); }

                try
                {
                    if (amongUsClient.AmHost && Main.GameTimer.IsRunning && Options.EnableGameTimeLimit.GetBool() && Main.GameTimer.Elapsed.TotalSeconds > Options.GameTimeLimit.GetInt() && Options.CurrentGameMode is CustomGameMode.Standard or CustomGameMode.NaturalDisasters)
                    {
                        Main.GameTimer.Reset();
                        Main.GameEndDueToTimer = true;
                        CustomWinnerHolder.ResetAndSetWinner(CustomWinner.None);
                        
                        if (Options.CurrentGameMode == CustomGameMode.NaturalDisasters)
                            CustomWinnerHolder.WinnerIds.UnionWith(Main.EnumerateAlivePlayerControls().Select(x => x.PlayerId));
                    }
                }
                catch (Exception e) { Utils.ThrowException(e); }

                AllocProbe.Mark("tail", alloc);
            }
        }
        catch (Exception e)
        {
            if (OnGameJoinedPatch.JoiningGame && e is NullReferenceException) return; // join 窓の transient fake-null は黙殺
            Utils.ThrowException(e);
        }
        finally { AllocProbe.FrameEnd(); }
    }
}