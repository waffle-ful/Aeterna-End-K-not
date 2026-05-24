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

    public static void Postfix()
    {
        try
        {
            if (!Paint)
            {
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

    public static void Postfix()
    {
        // 新 LobbyBehaviour 起動 → 前 session の stale Backrooms state を捨てる
        // (main menu 経由で復帰した時に dead Unity ref が残るバグ対応 — 2026-05-22 v3)
        try { BackroomsLobby.OnLobbyReload(); }
        catch (Exception ex) { Logger.Warn($"OnLobbyReload failed: {ex.Message}", "BackroomsGen"); }

        // バニラ船を即時に隠す (LocalPlayer 非依存)。procgen + vision mesh は LocalPlayer 必須なので
        // LateTask に後置するが、「ロビー入室後にバニラ船が約 1 秒見える」ラグはこの即時 disable で消える (2026-05-22)
        try { BackroomsLobby.HideVanillaShipImmediate(); }
        catch (Exception ex) { Logger.Warn($"HideVanillaShipImmediate failed: {ex.Message}", "BackroomsGen"); }

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
        }

        // Auto-enter Backrooms — モッドクライアント全員 (host も guest も) ローカル視界が Backrooms に置き換わる。
        // TP しないので非モッドクライアントには影響なし。seed は GameId 由来でモッド client 間で同じ layout
        //
        // LocalPlayer の準備時刻は環境差があり (Start 直後〜数秒)、固定遅延だと外す。0.3s 毎に
        // 自己再スケジュールするポーリング方式で、LocalPlayer が揃った瞬間に発火 (最大 16 回 = 5s 試行)
        _autoEnterAttempts = 0;
        LateTask.New(TryAutoEnterBackrooms, 0.3f, log: false);

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
                BGMManager.SetLobbyBGM();
                _bgmStarted = true;
                // OGG 同期デコードで実再生まで遅延が出る場合に備え、鳴った瞬間から
                // 2.5 秒に張り直す。AU が遅れて再アームする MapTheme/ambient を潰す。
                _silenceUntil = Time.realtimeSinceStartup + 2.5f;
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
[HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.Update))]
internal static class LobbyBehaviourUpdatePatch
{
    private static Func<ISoundPlayer, bool> Lobbybgm;
    private static ISoundPlayer MapThemeSound;
    public static void Postfix(LobbyBehaviour __instance)
    {
        // When custom BGM is active, keep MapTheme suppressed via the Start-initiated window.
        if (Main.EnableBGM?.Value ?? false)
        {
            LobbyBehaviourStartPatch.Tick();

            // Tick's silence window closes after 2.5s — but AU 2026.3.31 can
            // re-arm MapTheme later (post player-spawn handshake). Mirror the
            // Ambience GO idempotent-every-frame pattern: target MapTheme only
            // so we don't kill our own AudioSource sitting in soundPlayers.
            SoundManager sm = SoundManager.Instance;
            if (sm?.soundPlayers != null)
            {
                Func<ISoundPlayer, bool> isMapTheme = x => x.Name.Equals("MapTheme");
                if (sm.soundPlayers.Find(isMapTheme) != null)
                    sm.StopNamedSound("MapTheme");
            }

            return;
        }

        // BGM disabled: honour the vanilla LobbyMusic option.
        // ReSharper disable once ConvertToLocalFunction
        Lobbybgm = x => x.Name.Equals("MapTheme");
        MapThemeSound = SoundManager.Instance.soundPlayers.Find(Lobbybgm);

        if (!Main.LobbyMusic.Value)
        {
            if (MapThemeSound == null) return;
            SoundManager.Instance.StopNamedSound("MapTheme");
        }
        else
        {
            if (MapThemeSound != null) return;
            SoundManager.Instance.CrossFadeSound("MapTheme", __instance.MapTheme, 0.5f);
        }
    }
}
