#if ANDROID
using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Epic.OnlineServices;
using Il2CppInterop.Runtime;
using Connect = Epic.OnlineServices.Connect;

namespace EndKnot.Modules.Android
{
    internal static class ItchLogin
    {
        public const string KeyFileName = "EndKnot.ItchApiKey.txt";
        private const int MaxAttempts = 3; // EOSManager は失敗ごとに LoginWithCorrectPlatformImpl を呼び直す → 3 回でバニラ (Google→ゲスト) へ譲る
        private static int attempts;
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("EndKnot.ItchLogin");

        public static string ReadKey()
        {
            try
            {
                var path = Path.Combine(Paths.ConfigPath, KeyFileName);
                if (!File.Exists(path)) { Log.LogWarning("[ItchLogin] key file missing: " + path); return null; }
                var key = File.ReadAllText(path).Trim().Trim('﻿');
                Log.LogMessage("[ItchLogin] key file found, length=" + key.Length);
                return key.Length == 0 ? null : key;
            }
            catch (Exception e) { Log.LogError("[ItchLogin] key read failed: " + e.Message); return null; }
        }

        private static unsafe void InvokeConnectLogin(Connect.ConnectInterface connect, Connect.LoginOptions opts, Connect.OnLoginCallback cb)
        {
            var fi = typeof(Connect.ConnectInterface).GetField("NativeMethodInfoPtr_Login_Public_Void_byref_LoginOptions_Object_OnLoginCallback_0", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (fi == null) throw new InvalidOperationException("Connect.Login native method pointer not found in interop (EOS SDK / interop version mismatch)");
            var method = (IntPtr)fi.GetValue(null);
            IntPtr* args = stackalloc IntPtr[3];
            args[0] = IL2CPP.il2cpp_object_unbox(IL2CPP.Il2CppObjectBaseToPtrNotNull(opts));
            args[1] = IntPtr.Zero;
            args[2] = IL2CPP.Il2CppObjectBaseToPtr(cb);
            IntPtr exc = IntPtr.Zero;
            IL2CPP.il2cpp_runtime_invoke(method, IL2CPP.Il2CppObjectBaseToPtrNotNull(connect), (void**)args, ref exc);
            Il2CppInterop.Runtime.Il2CppException.RaiseExceptionIfNecessary(exc);
        }

        [HarmonyPatch(typeof(EOSManager), nameof(EOSManager.LoginWithCorrectPlatformImpl))]
        internal static class LoginPatch
        {
            public static bool Prefix(EOSManager __instance, [HarmonyArgument(0)] Connect.OnLoginCallback successCallbackIn)
            {
                var key = ReadKey();
                if (key == null) { Log.LogWarning("[ItchLogin] no key → vanilla (Google) path"); return true; }
                if (++attempts > MaxAttempts) { Log.LogWarning("[ItchLogin] itch login failed " + MaxAttempts + " times → vanilla path (guest)"); return true; }
                try
                {
                    // Il2CppSystem.Nullable<T>(T) は interop の boxed 値型 (class 扱い) で参照ブランチに落ちて中身を壊すため、
                    // value/hasValue のフィールド直書きで組む (value setter は boxed 値型を CopyBlock する)
                    var cred = new Connect.Credentials();
                    cred._Token_k__BackingField = new Utf8String(key);
                    cred._Type_k__BackingField = ExternalCredentialType.ItchioKey;
                    var nullable = new Il2CppSystem.Nullable<Connect.Credentials>();
                    nullable.value = cred;
                    nullable.hasValue = true;
                    var opts = new Connect.LoginOptions();
                    opts._Credentials_k__BackingField = nullable;
                    // 注意: Nullable<T>.value / Value の getter は値型 T を二重 box して壊れたオブジェクトを返す (Il2CppInterop の不具合・実機 SIGBUS 済) — 読み戻さない
                    Log.LogMessage("[ItchLogin] credentials set hasValue=" + opts._Credentials_k__BackingField.hasValue);
                    var connect = __instance.PlatformInterface.GetConnectInterface();
                    // interop の ConnectInterface.Login(ref LoginOptions) は boxed 構造体を unbox せずヘッダのポインタを渡す (実機で UserLoginInfo 側が
                    // ゴミ判定され SIGSEGV)。unbox した値ポインタで il2cpp_runtime_invoke を直接呼ぶ
                    InvokeConnectLogin(connect, opts, successCallbackIn);
                    Log.LogMessage("[ItchLogin] Connect.Login(ItchioKey) issued");
                    return false;
                }
                catch (Exception e) { Log.LogError("[ItchLogin] Connect.Login threw, falling back: " + e); return true; }
            }
        }

        [HarmonyPatch(typeof(EOSManager), nameof(EOSManager.EndFinalPartsOfLoginFlowFullAccount))]
        internal static class FullAccountPatch
        {
            public static void Postfix(EOSManager __instance) { attempts = 0; Log.LogMessage("[ItchLogin] FULL account login. FriendCode=" + __instance.FriendCode + " PUID=" + __instance.ProductUserId); }
        }

        [HarmonyPatch(typeof(EOSManager), nameof(EOSManager.EndFinalPartsOfLoginFlowTempAccount))]
        internal static class TempAccountPatch
        {
            public static void Postfix(EOSManager __instance) => Log.LogWarning("[ItchLogin] TEMP (guest) account login. FriendCode=" + __instance.FriendCode);
        }

        // EOSConnectPlatformLoginCallback(ref LoginCallbackInfo) は ref 構造体引数なので Harmony パッチ不可 (引数無し Postfix でも実機 SIGSEGV)
        [HarmonyPatch(typeof(AdsMenu), nameof(AdsMenu.OnEnable))]
        internal static class AdsMenuPatch
        {
            public static void Postfix(AdsMenu __instance) => Log.LogMessage("[Ads] AdsMenu.OnEnable adButton=" + (__instance.adButton != null) + " active=" + (__instance.adButton != null && __instance.adButton.activeSelf) + " ShowAdsScreen=" + AmongUs.Data.Legacy.LegacySaveManager.ShowAdsScreen);
        }
    }
}
#endif
