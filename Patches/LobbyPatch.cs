using System;
using EndKnot.Modules;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace EndKnot;

//[HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.FixedUpdate))]
public static class LobbyFixedUpdatePatch
{
    private static GameObject Paint;
    private static SpriteRenderer LeftEngineSR;
    private static SpriteRenderer RightEngineSR;
    private static float NextFindTime;

    public static void Postfix()
    {
        try
        {
            // Backrooms ロビーではバニラ船ごと非表示 — GameObject.Find は非アクティブを見つけられないので、
            // 旧実装はシーン全走査 ×3 を毎 tick 空振りし続けていた。塗装対象が無い以上、丸ごとスキップする。
            if (Main.BackroomsEnabled?.Value ?? true) return;

            if (!Paint)
            {
                // 見つかるまでの探索も 1 秒に 1 回で十分 (Find はシーン全走査)
                if (Time.unscaledTime < NextFindTime) return;
                NextFindTime = Time.unscaledTime + 1f;

                GameObject leftBox = GameObject.Find("Leftbox");

                if (leftBox)
                {
                    Paint = Object.Instantiate(leftBox, leftBox.transform.parent.transform);
                    Paint.name = "Lobby Paint";
                    Paint.transform.localPosition = new(0.042f, -2.59f, -10.5f);
                    var renderer = Paint.GetComponent<SpriteRenderer>();
                    renderer.sprite = Utils.LoadSprite("EndKnot.Resources.Images.LobbyPaint.png", 290f);
                }
            }

            if (!LeftEngineSR || !RightEngineSR)
            {
                if (Time.unscaledTime < NextFindTime) return;
                NextFindTime = Time.unscaledTime + 1f;

                var leftEngine = GameObject.Find("LeftEngine");
                if (leftEngine) LeftEngineSR = leftEngine.GetComponent<SpriteRenderer>();

                var rightEngine = GameObject.Find("RightEngine");
                if (rightEngine) RightEngineSR = rightEngine.GetComponent<SpriteRenderer>();
            }
            else
            {
                LeftEngineSR.color = Color.cyan;
                RightEngineSR.color = Color.cyan;
            }
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }
}

[HarmonyPatch(typeof(HostInfoPanel), nameof(HostInfoPanel.SetUp))]
public static class HostInfoPanelSetUpPatch
{
    private static TextMeshPro HostText;

    public static bool Prefix(HostInfoPanel __instance)
    {
        return GameStates.IsLobby && __instance.player.ColorId != byte.MaxValue;
    }

    public static void Postfix(HostInfoPanel __instance)
    {
        try
        {
            if (!HostText) HostText = __instance.content.transform.FindChild("Name").GetComponent<TextMeshPro>();

            string name = AmongUsClient.Instance.GetHost().PlayerName.Split('\n')[^1];
            if (name == string.Empty) return;

            string text = AmongUsClient.Instance.AmHost
                ? Translator.GetString("YouAreHostSuffix")
                : name;

            HostText.text = Utils.ColorString(Palette.PlayerColors[__instance.player.ColorId], text);
        }
        catch { }
    }
}

[HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.Start))]
internal static class LobbyBehaviourStartPatch
{
    // Mirror the menu BGM pattern exactly:
    //   frame 1 of Update: SilenceVanillaAudio → SetLobbyBGM (once)
    //   frame 2+         : SilenceVanillaAudio only, until 2.5s elapses
    internal static bool SilencePending;
    private static bool _bgmStarted;
    private static float _silenceUntil;

    // ロビー入場フレーム 1.8 秒級ストールの帰属計測。Prefix→Postfix 到達までが
    // バニラ LobbyBehaviour.Start 本体、それ以降が mod の入場時仕事の取り分。
    private static System.Diagnostics.Stopwatch _enterSw;

    public static void Prefix()
    {
        _enterSw = System.Diagnostics.Stopwatch.StartNew();
    }

    public static void Postfix()
    {
        long vanillaMs = _enterSw?.ElapsedMilliseconds ?? -1;
        var postSw = System.Diagnostics.Stopwatch.StartNew();

        EndKnot.Modules.Media.LoadingScreenVideo.Hide();

        // 新 LobbyBehaviour 起動 → 前 session の stale Backrooms state を捨てる
        // (main menu 経由で復帰した時に dead Unity ref が残るバグ対応 — 2026-05-22 v3)
        try { BackroomsLobby.OnLobbyReload(); }
        catch (Exception ex) { Logger.Warn($"OnLobbyReload failed: {ex.Message}", "BackroomsGen"); }

        long reloadMs = postSw.ElapsedMilliseconds;

        // BackroomsEnabled が OFF なら船隠し + 自動入室を全 skip (バニラロビーのまま)
        bool backroomsEnabled = Main.BackroomsEnabled?.Value ?? true;

        // バニラ船を即時に隠す (LocalPlayer 非依存)。procgen + vision mesh は LocalPlayer 必須なので
        // LateTask に後置するが、「ロビー入室後にバニラ船が約 1 秒見える」ラグはこの即時 disable で消える (2026-05-22)
        if (backroomsEnabled)
        {
            try { BackroomsLobby.HideVanillaShipImmediate(); }
            catch (Exception ex) { Logger.Warn($"HideVanillaShipImmediate failed: {ex.Message}", "BackroomsGen"); }
        }

        long hideShipMs = postSw.ElapsedMilliseconds - reloadMs;
        EndKnot.Modules.HealthLog.Note($"TRANSIT phase=lobbyenter vanillaMs={vanillaMs} reloadMs={reloadMs} hideShipMs={hideShipMs} t={Utils.TimeStamp}");

        if (AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost)
        {
            LateTask.New(() =>
            {
                try
                {
                    if (AmongUsClient.Instance == null || PlayerControl.LocalPlayer == null) return;
                    foreach (PlayerControl pc in Main.EnumeratePlayerControls())
                    {
                        if (pc == null || pc.Data == null || !pc.Data.IsDead) continue;
                        pc.Data.IsDead = false;
                        pc.Data.SetDirtyBit(0b_1u << pc.PlayerId);
                    }
                    AmongUsClient.Instance.SendAllStreamedObjects();
                    Main.LobbyDead.Clear();
                    Main.LobbyKillers.Clear();
                    EndKnot.Modules.RPC.SyncLobbyState();
                }
                catch (Exception ex) { Logger.Warn($"LobbyBehaviour.Start reset failed: {ex.Message}", "LobbyKill"); }
            }, 1.0f, log: false);

            // 配信者モードを一括 ON した直後なら、コメント取得の URL 設定を促す案内を 1 度だけ出す。
            LateTask.New(StreamerMode.ConsumeUrlHintIfHost, 2f, "StreamerMode.UrlHint", log: false);
        }

        // Auto-enter Backrooms — モッドクライアント全員 (host も guest も) ローカル視界が Backrooms に置き換わる。
        // TP しないので非モッドクライアントには影響なし。seed は GameId 由来でモッド client 間で同じ layout
        //
        // LocalPlayer の準備時刻は環境差があり (Start 直後〜数秒)、固定遅延だと外す。0.3s 毎に
        // 自己再スケジュールするポーリング方式で、LocalPlayer が揃った瞬間に発火 (最大 16 回 = 5s 試行)
        if (backroomsEnabled)
        {
            _autoEnterAttempts = 0;
            LateTask.New(TryAutoEnterBackrooms, 0.3f, log: false);
        }

        if (!(Main.EnableBGM?.Value ?? false)) return;
        SilencePending = true;
        _bgmStarted = false;
        _silenceUntil = Time.realtimeSinceStartup + 2.5f;
    }

    private static int _autoEnterAttempts;
    private const int MaxAutoEnterAttempts = 16; // 0.3s × 16 ≈ 5s 上限

    private static void TryAutoEnterBackrooms()
    {
        try
        {
            _autoEnterAttempts++;
            if (LobbyBehaviour.Instance == null) return; // lobby を出た = 中止
            if (!(Main.BackroomsEnabled?.Value ?? true)) return; // ポーリング中に OFF へ切替 = 中止
            if (PlayerControl.LocalPlayer == null)
            {
                if (_autoEnterAttempts < MaxAutoEnterAttempts)
                    LateTask.New(TryAutoEnterBackrooms, 0.3f, log: false);
                else
                    Logger.Warn($"Auto-enter Backrooms gave up: LocalPlayer null after {_autoEnterAttempts} attempts", "BackroomsGen");
                return;
            }

            uint seed = AmongUsClient.Instance != null ? unchecked((uint)AmongUsClient.Instance.GameId) : 0u;
            if (seed == 0u) seed = (uint)UnityEngine.Random.Range(1, int.MaxValue);
            BackroomsLobby.EnterBackrooms(seed, byte.MaxValue, silent: true);
        }
        catch (Exception ex) { Logger.Warn($"Auto-enter Backrooms failed: {ex.Message}", "BackroomsGen"); }
    }

    internal static void Tick()
    {
        if (!SilencePending) return;

        if (SoundManager.Instance != null)
        {
            BGMManager.SilenceVanillaAudio();

            if (!_bgmStarted)
            {
                _bgmStarted = true;
                _silenceUntil = Time.realtimeSinceStartup + 2.5f;
                // LobbyBehaviour.Start は OnGameJoined の初回 SetLobbyBGM より少し後に走るので、
                // ここで鳴らし直す。BGMManager 側で「同スロットが鳴っていれば何もしない／
                // 鳴っていなければ同じ曲を鳴らし直す」ので、二重抽選も無音化も起きない。
                BGMManager.SetLobbyBGM();
            }
        }

        if (Time.realtimeSinceStartup >= _silenceUntil)
            SilencePending = false;
    }
}

// ロビーから抜けた瞬間に Backrooms アンビエントを止める。GO は DontDestroyOnLoad では
// ないのでシーン unload で死ぬが、Among Us は同一シーン内で LobbyBehaviour を destroy する
// 経路 (= /bbexit ではなく実際の退室で AudioSource だけ動的に消す) があるため belt-and-suspenders。
[HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.OnDestroy))]
internal static class LobbyBehaviourOnDestroyPatch
{
    public static void Postfix()
    {
        try { BackroomsAmbient.Stop(); }
        catch (Exception ex) { Logger.Warn($"BackroomsAmbient.Stop on lobby destroy failed: {ex.Message}", "BackroomsAmbient"); }
    }
}

// https://github.com/SuperNewRoles/SuperNewRoles/blob/master/SuperNewRoles/Patches/LobbyBehaviourPatch.cs
// FixedUpdateCaller が lobbyBehaviour 存在時に毎 tick 呼ぶ (LobbyFixedUpdatePatch と同じ dispatcher 集約)。
// Harmony 属性を生かすと Update 経由と dispatcher 経由の二重実行になるため属性側を外してある。
//[HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.Update))]
internal static class LobbyBehaviourUpdatePatch
{
    private static ISoundPlayer MapThemeSound;
    private static float nextMapThemeCheckTime;

    // ⚠️ ここで soundPlayers.Find(<マネージドラムダ>) を使ってはいけない。
    // Il2Cpp の List<T>.Find は Il2CppSystem.Predicate<T> を取るため、マネージド Func を渡すと
    // DelegateSupport が呼び出しのたびに Il2CppToMonoDelegateReference + strong GCHandle を生成し、
    // それが永久に解放されない (ラムダ自体は Roslyn がキャッシュするので使い回されるが、変換はキャッシュ
    // されない)。この Postfix は毎フレーム走るため実測で 54個/秒・54分で 174,142 個が滞留していた。
    // 手動走査なら interop 変換自体が発生しない (BGMManager.cs の soundPlayers 走査と同じ型)。
    private static ISoundPlayer FindMapTheme(SoundManager soundManager)
    {
        var players = soundManager.soundPlayers;

        for (var i = 0; i < players.Count; i++)
        {
            ISoundPlayer p = players[i];
            if (p != null && p.Name.Equals("MapTheme")) return p;
        }

        return null;
    }
    public static void Postfix(LobbyBehaviour __instance)
    {
        // 自動再ホスト直後などシーン再構築中は SoundManager.Instance / soundPlayers が
        // 一過性に null になり、無ガードだと毎フレーム NRE が FixedUpdateCaller まで伝播して
        // 残りの毎フレーム処理を丸ごと中断する。他のロビー系パッチと同様に必ずガードする。
        try
        {
            // When custom BGM is active, keep MapTheme suppressed via the Start-initiated window.
            if (Main.EnableBGM?.Value ?? false)
            {
                LobbyBehaviourStartPatch.Tick();

                // Tick's silence window closes after 2.5s — but AU 2026.3.31 can
                // re-arm MapTheme later (post player-spawn handshake). Mirror the
                // Ambience GO idempotent-every-frame pattern: target MapTheme only
                // so we don't kill our own AudioSource sitting in soundPlayers.
                // StopNamedSound は不在時 no-op なので、事前の FindMapTheme (soundPlayer ごとの
                // p.Name 取得 = 毎フレーム managed string 確保) を挟む必要はない。呼び出し自体の
                // interop 文字列変換も毎フレームは無駄なので 0.25s に間引く (再アームの黙らせが
                // 最大 0.25s 遅れるだけ)。
                if (Time.unscaledTime >= nextMapThemeCheckTime)
                {
                    nextMapThemeCheckTime = Time.unscaledTime + 0.25f;
                    SoundManager sm = SoundManager.Instance;
                    if (sm?.soundPlayers != null)
                        sm.StopNamedSound("MapTheme");
                }

                return;
            }

            // BGM disabled: honour the vanilla LobbyMusic option.
            // FindMapTheme の p.Name 走査は毎フレームだと string 確保が積もるので 0.25s に間引く
            // (再生開始/停止の反応が最大 0.25s 遅れるだけで、聴感上は分からない)。
            if (Time.unscaledTime < nextMapThemeCheckTime) return;
            nextMapThemeCheckTime = Time.unscaledTime + 0.25f;

            SoundManager soundManager = SoundManager.Instance;
            if (soundManager?.soundPlayers == null) return;

            MapThemeSound = FindMapTheme(soundManager);

            if (!Main.LobbyMusic.Value)
            {
                if (MapThemeSound == null) return;
                soundManager.StopNamedSound("MapTheme");
            }
            else
            {
                if (MapThemeSound != null) return;
                soundManager.CrossFadeSound("MapTheme", __instance.MapTheme, 0.5f);
            }
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }
}
