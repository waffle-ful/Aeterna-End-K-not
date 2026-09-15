using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Hook;
using BepInEx.Unity.IL2CPP.Logging;
using HarmonyLib;

namespace EndKnot.BootAccel
{
    // Moves the chainloader's plugin loading (BaseChainloader.Execute: plugin discovery and every
    // plugin's Load) off the path to the first rendered frame.
    //
    // Stock BepInEx runs Execute synchronously inside its il2cpp_runtime_invoke detour, at the
    // very first Internal_ActiveSceneChanged, i.e. before Unity has presented a single frame.
    // Time spent there pushes the main menu back; once the first frame is past, the same work no
    // longer moves menu arrival at all (measured: displacing Execute a further three seconds
    // leaves it unchanged). So the win is in clearing the first frame, and there is no reason to
    // wait any longer than that.
    //
    // This patch replicates the detour callback's scene-change handling (Unity log source +
    // interop preload) but skips Execute there, keeps the detour alive, and runs Execute once
    // Time.frameCount has advanced two frames past the scene change. A time fallback runs Execute
    // at the next invoke after LateLoadFallbackMs in case the frame count never moves.
    //
    // Compatibility note: plugins now load after Unity's own scene-change listeners for the first
    // scene, and after the first frame. Anything that patched those listeners for scene #1 would
    // miss it; End K not does not, and other plugins are not supported by this loader anyway.
    // Kill switch: a file named endknot-lateload.disabled in BepInEx/cache.
    internal static class LateLoadAccel
    {
        internal const string DisableFileName = "endknot-lateload.disabled";
        private const int LateLoadFallbackMs = 3000;

        internal static MethodBase Target;

        private static FieldInfo _originalInvokeField;
        private static PropertyInfo _detourProperty;
        private static FieldInfo _configUnityLoggingField;
        private static MethodInfo _preloadInteropMethod;

        private static int _state; // 0 = waiting for scene change, 1 = armed (waiting for frame 2), 2 = done
        private static int _mainThreadId;
        private static Stopwatch _sinceScene;

        // UnityEngine.Time.frameCount through its il2cpp internal call: no interop assembly is
        // needed at patcher level, and one native call per runtime_invoke is cheap enough for the
        // one or two frames this stays armed.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int FrameCountFn();
        private static FrameCountFn _frameCount;
        private static int _frameAtScene;

        internal static bool Resolve()
        {
            try
            {
                Type chainloader = typeof(IL2CPPChainloader);
                const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

                Target = chainloader.GetMethod("OnInvokeMethod", flags, null,
                    new[] { typeof(IntPtr), typeof(IntPtr), typeof(IntPtr), typeof(IntPtr) }, null);
                _originalInvokeField = chainloader.GetField("originalInvoke", flags);
                _detourProperty = chainloader.GetProperty("RuntimeInvokeDetour", flags);
                _configUnityLoggingField = chainloader.GetField("ConfigUnityLogging", flags);

                Type interopManager = chainloader.Assembly.GetType("BepInEx.Unity.IL2CPP.Il2CppInteropManager");
                _preloadInteropMethod = interopManager?.GetMethod("PreloadInteropAssemblies", flags);

                if (Target == null || _originalInvokeField == null || _detourProperty == null || _preloadInteropMethod == null)
                {
                    BootAccelPatcher.Warn("lateload: chainloader shape not recognised; plugins load at the stock point");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                BootAccelPatcher.Warn("lateload: resolve failed: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        // Harmony prefix on IL2CPPChainloader.OnInvokeMethod. Returning true lets the stock
        // callback run (its own name check finds nothing to do once the scene change has been
        // consumed here, so it just forwards to the original il2cpp_runtime_invoke).
        public static bool OnInvokeMethod_Prefix(IntPtr method, IntPtr obj, IntPtr parameters, IntPtr exc, ref IntPtr __result)
        {
            if (_state == 2) return true;

            try
            {
                // Thousands of invokes pass here during engine init and the armed frames; only the
                // name Internal_ActiveSceneChanged matters, so the string is materialised only when
                // the first byte can match.
                IntPtr namePtr = Il2CppInterop.Runtime.IL2CPP.il2cpp_method_get_name(method);
                bool mayBeSceneChange = namePtr != IntPtr.Zero && Marshal.ReadByte(namePtr) == (byte)'I';
                string name = mayBeSceneChange ? Marshal.PtrToStringAnsi(namePtr) : null;

                if (_state == 0)
                {
                    if (name != "Internal_ActiveSceneChanged") return true;

                    _mainThreadId = Environment.CurrentManagedThreadId;
                    _sinceScene = Stopwatch.StartNew();
                    _frameCount = ResolveFrameCount();
                    _frameAtScene = _frameCount != null ? _frameCount() : 0;
                    _state = 1;
                    OnSceneChanged();
                    __result = CallOriginal(method, obj, parameters, exc);
                    return false;
                }

                // armed: wait for the second frame on the thread that raised the scene change
                if (Environment.CurrentManagedThreadId != _mainThreadId) return true;

                // Unity raises Internal_ActiveSceneChanged more than once before the first frame;
                // the stock callback would run Execute on any of them, so forward those here.
                if (name == "Internal_ActiveSceneChanged" && (_frameCount == null || _frameCount() < _frameAtScene + 2))
                {
                    __result = CallOriginal(method, obj, parameters, exc);
                    return false;
                }

                bool trigger = false;

                // Two frames past the scene change: the frame the scene was activated in has been
                // presented, which is the whole condition worth waiting for.
                if (_frameCount != null && _frameCount() >= _frameAtScene + 2) trigger = true;

                if (!trigger && _sinceScene.ElapsedMilliseconds > LateLoadFallbackMs) trigger = true;
                if (!trigger) return true;

                _state = 2;
                long waited = _sinceScene.ElapsedMilliseconds;
                RunExecute(waited);
                __result = CallOriginal(method, obj, parameters, exc);
                DisposeDetour();
                return false;
            }
            catch (Exception ex)
            {
                // Never leave the boot without plugins: run the stock path from here on.
                BootAccelPatcher.Warn("lateload: prefix failed, falling back to stock behaviour: " + ex.GetType().Name + ": " + ex.Message);
                if (_state == 1)
                {
                    _state = 2;
                    try { RunExecute(-1); } catch { }
                    DisposeDetour();
                }
                return true;
            }
        }

        private static FrameCountFn ResolveFrameCount()
        {
            try
            {
                IntPtr p = Il2CppInterop.Runtime.IL2CPP.il2cpp_resolve_icall("UnityEngine.Time::get_frameCount()");
                if (p == IntPtr.Zero) { BootAccelPatcher.Warn("lateload: Time.frameCount icall not found; using the time fallback"); return null; }
                return Marshal.GetDelegateForFunctionPointer<FrameCountFn>(p);
            }
            catch (Exception ex)
            {
                BootAccelPatcher.Warn("lateload: frameCount resolve failed: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        private static void OnSceneChanged()
        {
            try
            {
                bool unityLogging = true;
                if (_configUnityLoggingField != null)
                {
                    object entry = _configUnityLoggingField.GetValue(null);
                    if (entry is BepInEx.Configuration.ConfigEntry<bool> typed) unityLogging = typed.Value;
                }

                if (unityLogging)
                    Logger.Sources.Add(new IL2CPPUnityLogSource());

                _preloadInteropMethod.Invoke(null, null);
            }
            catch (Exception ex)
            {
                BootAccelPatcher.Warn("lateload: scene-change setup failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void RunExecute(long waitedMs)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                IL2CPPChainloader.Instance.Execute();
            }
            catch (Exception ex)
            {
                BootAccelPatcher.AccelLog.LogFatal("Unable to execute IL2CPP chainloader (late load)");
                BootAccelPatcher.AccelLog.LogError(ex);
            }

            BootAccelPatcher.AccelLog.LogInfo("lateload: chainloader Execute ran " + (waitedMs < 0 ? "via fallback" : waitedMs + " ms after the first scene change")
                + " (frame " + _frameAtScene + " -> " + (_frameCount != null ? _frameCount() : -1) + "), took " + sw.ElapsedMilliseconds + " ms");
        }

        private static IntPtr CallOriginal(IntPtr method, IntPtr obj, IntPtr parameters, IntPtr exc)
        {
            var original = (Delegate)_originalInvokeField.GetValue(null);
            return (IntPtr)original.DynamicInvoke(method, obj, parameters, exc);
        }

        private static void DisposeDetour()
        {
            try
            {
                var detour = _detourProperty.GetValue(null) as INativeDetour;
                detour?.Dispose();
            }
            catch (Exception ex)
            {
                BootAccelPatcher.Warn("lateload: detour dispose failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
