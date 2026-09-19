using AmongUs.Data;
using EndKnot.Modules;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using InnerNet;
using System;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot;

/*[HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.MakePublic))]
internal static class MakePublicPatch
{
    public static bool Prefix()
    {
        if (ModUpdater.IsBroken || (ModUpdater.HasUpdate && ModUpdater.ForceUpdate) || !VersionChecker.IsSupported)
        {
            var message = string.Empty;
            if (!VersionChecker.IsSupported) message = GetString("UnsupportedVersion");
            if (ModUpdater.IsBroken) message = GetString("ModBrokenMessage");
            if (ModUpdater.HasUpdate) message = GetString("CanNotJoinPublicRoomNoLatest");
            Logger.Info(message, "MakePublicPatch");
            Logger.SendInGame(message, Color.red);
            return false;
        }

        return true;
    }
}*/

[HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.StartRpcImmediately))]
static class StartRpcImmediatelyPatch
{
    public static void Postfix(uint targetNetId, byte callId, Hazel.SendOption option, int targetClientId = -1)
    {
        if (callId is 21 or 44 or 45 or 104) return;
        Logger.Info($"Starting RPC: {callId} ({RPC.GetRpcName(callId)}) as {Main.CachedAllPlayerControls().FirstOrDefault(x => x.NetId == targetNetId)?.GetRealName() ?? targetNetId.ToString()} with SendOption {option} to {Utils.GetClientById(targetClientId)?.Character?.GetRealName() ?? targetClientId.ToString()}", "StartRpcImmediately");
    }
}

[HarmonyPatch(typeof(MMOnlineManager), nameof(MMOnlineManager.Start))]
// ReSharper disable once InconsistentNaming
internal static class MMOnlineManagerStartPatch
{
    public static void Postfix()
    {
        if (!((ModUpdater.HasUpdate && ModUpdater.ForceUpdate) || ModUpdater.IsBroken)) return;

        GameObject obj = GameObject.Find("FindGameButton");

        if (obj)
        {
            obj.SetActive(false);
            TextMeshPro textObj = Object.Instantiate(obj.transform.FindChild("Text_TMP").GetComponent<TextMeshPro>());
            textObj.transform.position = new(1f, -0.3f, 0);
            textObj.name = "CanNotJoinPublic";

            string message = ModUpdater.IsBroken
                ? $"<size=2>{Utils.ColorString(Color.red, GetString("ModBrokenMessage"))}</size>"
                : $"<size=2>{Utils.ColorString(Color.red, GetString("CanNotJoinPublicRoomNoLatest"))}</size>";

            LateTask.New(() => { textObj.text = message; }, 0.01f, "CanNotJoinPublic");
        }
    }
}

[HarmonyPatch(typeof(SplashManager), nameof(SplashManager.Update))]
internal static class SplashLogoAnimatorPatch
{
    public static void Prefix(SplashManager __instance)
    {
        __instance.sceneChanger.AllowFinishLoadingScene();
        __instance.startedSceneLoad = true;
        BootTimeline.Mark("splash.update");

        // PatchPhases.LateSplashWork=false (or forced off by env var): 元の挙動どおり、火の prewarm
        // とパッチ pump をこのスプラッシュフレームから直接進める -- CalamityFire.Prewarm() 単体で
        // 約270ms、続く最初の PatchPhases.Pump 呼び出しで約110msのメインスレッド停止がこの1フレームに
        // 集中する。true の場合はどちらも SplashLateWork.Tick (opts.prelude.end 以降にだけ動く) へ委ね、
        // CalamityFire.Tick() も SplashLateWork 側が毎フレーム回すので、ここでは何もしない。
        if (!EndKnot.Modules.PatchPhases.LateSplashWork)
        {
            EndKnot.Patches.CalamityMenu.CalamityFire.Prewarm();
            // InnerNetClient.FixedUpdate はメニュー到達後に初めて回るため、スプラッシュ中の準備進行 (テクスチャ生成) はここから進める。
            EndKnot.Patches.CalamityMenu.CalamityFire.Tick();
            // スプラッシュは静止画なので、後回しにしたパッチの適用をここで進めておくと
            // メニュー到達時の分割適用 (フレーム落ち) を減らせる。
            EndKnot.Modules.PatchPhases.Pump(30f);
        }
    }
}

// EOSManager.Update はスプラッシュからメニューまで毎フレーム回るので、遅延 prewarm/pump の駆動源に使う。
// EosBootMarksPatch (Main.EosBootMarks でオフにできる) とは別のパッチクラスにしてあるのは、その設定が
// オフでも遅延させた prewarm/pump は動き続けなければならないため。
[HarmonyPatch(typeof(EOSManager), nameof(EOSManager.Update))]
internal static class SplashLateWorkPatch
{
    public static void Postfix() => SplashLateWork.Tick();
}

// PatchPhases.LateSplashWork=true のときの splash 時 prewarm/pump 駆動。opts.prelude.end の Mark から
// メニュー到達までの主スレッドが空いている区間だけで、CalamityFire.Prewarm() とパッチ pump を進める。
// opts.prelude.end が何らかの理由で刻まれない起動 (オプション prelude の例外など) でも prewarm/pump が
// 置き去りにならないよう、最初の Tick から FallbackSeconds 経過で待たずに始める。
internal static class SplashLateWork
{
    private const float FallbackSeconds = 6f;

    private static bool _started;
    private static bool _fireErrored;
    private static bool _pumpErrored;
    private static float _firstTickRealtime = -1f;

    public static void Tick()
    {
        if (!EndKnot.Modules.PatchPhases.LateSplashWork) return;
        if (BootTimeline.MenuReached) return;

        float now = UnityEngine.Time.realtimeSinceStartup;
        if (_firstTickRealtime < 0f) _firstTickRealtime = now;
        if (!BootTimeline.PreludeEnded && now - _firstTickRealtime < FallbackSeconds) return;

        try
        {
            if (!_started)
            {
                _started = true;
                BootTimeline.Mark(BootTimeline.PreludeEnded ? "latework.begin" : "latework.fallback");
                EndKnot.Patches.CalamityMenu.CalamityFire.Prewarm();
            }

            EndKnot.Patches.CalamityMenu.CalamityFire.Tick();
        }
        catch (Exception e)
        {
            if (!_fireErrored) { _fireErrored = true; Logger.Error(e.ToString(), "SplashLateWork.Fire"); }
        }

        try { EndKnot.Modules.PatchPhases.Pump(30f); }
        catch (Exception e)
        {
            if (!_pumpErrored) { _pumpErrored = true; Logger.Error(e.ToString(), "SplashLateWork.Pump"); }
        }
    }
}

[HarmonyPatch(typeof(EOSManager), nameof(EOSManager.IsAllowedOnline))]
internal static class RunLoginPatch
{
    public const int ClickCount = 0;

    public static void Prefix(ref bool canOnline)
    {
        if (DebugModeManager.AmDebugger) canOnline = true;

        try { ModUpdater.ShowAvailableUpdate(); }
        catch (Exception error) { Logger.Error(error.ToString(), "ModUpdater.ShowAvailableUpdate"); }
    }
}

[HarmonyPatch(typeof(BanMenu), nameof(BanMenu.SetVisible))]
internal static class BanMenuSetVisiblePatch
{
    public static bool Prefix(BanMenu __instance, bool show)
    {
        if (!AmongUsClient.Instance.AmHost) return true;

        show &= PlayerControl.LocalPlayer && PlayerControl.LocalPlayer.Data != null;
        __instance.BanButton.gameObject.SetActive(AmongUsClient.Instance.CanBan());
        __instance.KickButton.gameObject.SetActive(AmongUsClient.Instance.CanKick());
        __instance.MenuButton.gameObject.SetActive(show);
        return false;
    }
}

[HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.CanBan))]
internal static class InnerNetClientCanBanPatch
{
    public static bool Prefix(InnerNetClient __instance, ref bool __result)
    {
        __result = __instance.AmHost;
        return false;
    }
}

[HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.KickPlayer))]
internal static class KickPlayerPatch
{
    public static bool Prefix( /*InnerNetClient __instance,*/ int clientId, bool ban)
    {
        if (!AmongUsClient.Instance.AmHost && !OnGameJoinedPatch.JoiningGame) return true;

        if (AmongUsClient.Instance.ClientId == clientId)
        {
            Logger.SendInGame($"Game Attempting to {(ban ? "Ban" : "Kick")} Host, Blocked the attempt.", Color.red);
            return false;
        }

        if (ban) BanManager.AddBanPlayer(AmongUsClient.Instance.GetRecentClient(clientId));

        return true;
    }
}

[HarmonyPatch(typeof(ResolutionManager), nameof(ResolutionManager.SetResolution))]
internal static class SetResolutionManager
{
    public static void Postfix()
    {
        if (MainMenuManagerPatch.UpdateButton)
            MainMenuManagerPatch.UpdateButton.transform.localPosition = MainMenuManagerPatch.Template.transform.localPosition + new Vector3(0.25f, 0.75f);
    }
}

[HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.SendAllStreamedObjects))]
internal static class InnerNetObjectSerializePatch
{
    public static void Prefix()
    {
        if (!AmongUsClient.Instance.AmHost || GameOptionsSender.ActiveCoroutine != null) return;
        GameOptionsSender.ActiveCoroutine = Main.Instance.StartCoroutine(GameOptionsSender.SendDirtyGameOptionsContinuously());
    }
}

// https://github.com/Rabek009/MoreGamemodes/blob/master/Patches/ClientPatch.cs
[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.CheckOnlinePermissions))]
static class CheckOnlinePermissionsPatch
{
    public static void Prefix()
    {
        DataManager.Player.Ban.banPoints = 0f;
    }
}

[HarmonyPatch]
internal static class AuthTimeoutPatch
{
    // From Reactor.gg
    // https://github.com/NuclearPowered/Reactor/blob/master/Reactor/Patches/Miscellaneous/CustomServersPatch.cs
    [HarmonyPatch(typeof(AuthManager), nameof(AuthManager.CoConnect))]
    [HarmonyPatch(typeof(AuthManager), nameof(AuthManager.CoWaitForNonce))]
    [HarmonyPrefix]
    public static bool CoWaitforNonce_Prefix()
    {
        return GameStates.CurrentServerTypeInCreateMenu is GameStates.ServerType.Vanilla or GameStates.ServerType.Local;
    }

    // If you don't patch this, you still need to wait for 5 s.
    // I have no idea why this is happening
    [HarmonyPatch]
    public static class EnableUdpPatch
    {
        public static MethodBase TargetMethod()
        {
            return Utils.GetStateMachineMoveNext<AmongUsClient>(nameof(AmongUsClient.CoJoinOnlinePublicGame))!;
        }

        public static void Prefix(Il2CppObjectBase __instance)
        {
            var stateMachine = new StateMachineWrapper<AmongUsClient>(__instance);

            // Skip to state 1 which just calls CoJoinOnlineGameDirect
            if (stateMachine.State == 0 && !ServerManager.Instance.IsHttp)
            {
                stateMachine.State = 1;
                var lambdaType = stateMachine.GetParameter<Il2CppObjectBase>("__8__1").GetType();
                var newDisplayClass = Activator.CreateInstance(lambdaType);
                if (newDisplayClass == null)
                {
                    throw new InvalidOperationException($"Could not create display class of type '{lambdaType}'.");
                }

                var displayClass = new CompilerGeneratedObjectWrapper(newDisplayClass);
                displayClass.SetField("matchmakerToken", string.Empty);

                stateMachine.SetParameter("__8__1", newDisplayClass);
            }
        }
    }
}