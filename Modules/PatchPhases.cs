using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using HarmonyLib.Public.Patching;
using InnerNet;
using MonoMod.Cil;
using MonoMod.Utils;
using System.IO;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using UnityEngine;

namespace EndKnot.Modules;

// Harmony.PatchAll(Assembly) walks every class in the assembly and JITs a detour for each
// target method, which is the single biggest chunk of Main.Load's startup cost. Most of that
// cost buys nothing at boot time: patches whose targets are all vanilla in-game types (HUD,
// meeting, sabotage, ...) can't possibly run before a lobby exists. Phase 1 patches everything
// else during Load; phase 2 patches the deferred classes a few at a time once the main menu is
// already interactive, with a synchronous safety net (EnsureComplete) guaranteeing every patch
// is in place before a lobby can be hosted or joined.
//
// Second lever (BatchedPatching): Harmony rebuilds and re-detours a target method every time
// one more patch class touches it, so a method patched by N classes is compiled N times, each
// build larger than the last. The MethodPatcher decorator below (installed through Harmony's
// public ResolvePatcher hook) swallows those intermediate rebuilds while a phase is collecting
// its classes and compiles each target exactly once when the phase flushes.
//
// Third lever (DelegateTypeCache): for every il2cpp target, Il2CppInterop asks HarmonyX's
// DelegateTypeFactory for a delegate type matching the native-to-managed trampoline, and that
// factory builds a brand-new Cecil assembly and Assembly.Load()s it per call (~13 ms each, and a
// permanent extra assembly in the process). Measured on this mod it was 97% of the whole patch
// cost. The prefix below serves those types from one shared Reflection.Emit module keyed by
// signature instead.
public static class PatchPhases
{
    // Type.Name (not full name) of vanilla types that are only ever exercised once a lobby
    // exists. A class is only eligible for deferral if every Harmony target it declares is in
    // this set (see Classify) -- anything else (menu code, networking, unresolved targets) stays
    // in phase 1 so it is guaranteed active before the main menu appears.
    public static readonly HashSet<string> GameTypeNames = new()
    {
        "AbilityButton", "ActionButton", "AirshipStatus", "ChatBubble", "ChatController",
        "ChatManager", "Console", "CrewmateGhostRole", "CustomNetworkTransform", "DeconSystem",
        "DoorsSystemType", "ElectricTask", "ElectricalDoors", "EmergencyMinigame",
        "EndGameManager", "EndGameNavigation", "ExileController", "FreeChatInputField",
        "GameData", "GameManager", "GameOptionsMenu", "GameSettingMenu", "GameStartManager",
        "HauntMenuMinigame", "HeliSabotageSystem", "HideAndSeekManager", "HostInfoPanel",
        "HudManager", "IGameOptionsExtensions", "ImpostorGhostRole", "ImpostorRole",
        "InfectedOverlay", "IntroCutscene", "KillButton", "LifeSuppSystemType",
        "LobbyBehaviour", "LobbyViewSettingsPane", "LogicGameFlowHnS", "LogicGameFlowNormal",
        "LogicOptions", "LogicRoleSelectionNormal", "MapBehaviour", "MapRoom",
        "MeetingHud", "MeetingIntroAnimation", "MovingPlatformBehaviour",
        "MushroomMixupSabotageSystem", "NetworkedPlayerInfo", "NormalGameManager",
        "NormalGameOptionsV11", "NumberOption", "OneWayShadows", "OptionBehaviour",
        "PhantomRole", "PlayerControl", "PlayerPhysics", "PlayerVoteArea", "PolusShipStatus",
        "ReactorSystemType", "RoleManager", "SabotageButton", "SabotageSystemType",
        "SecurityCameraSystemType", "ShapeshifterMinigame", "ShipStatus",
        "SurveillanceMinigame", "SwitchSystem", "TaskAddButton", "TaskAdderGame",
        "TaskPanelBehaviour", "ToggleOption", "Vent", "VentButton", "VentilationSystem",
        "VitalsMinigame", "VoteBanSystem",
    };

    private const float CeilingSeconds = 15f;

    private static readonly Queue<Type> _deferred = new();

    // Targets whose compile was swallowed by the batching decorator and still has to run.
    private static readonly Queue<BatchingPatcher> _pendingCompile = new();
    private static readonly HashSet<BatchingPatcher> _pendingSet = new();
    private static readonly List<BatchingPatcher> _allPatchers = new();
    private static bool _collecting;
    private static bool _resolverInstalled;
    private static string _phase1Profile;
    private static bool _factoryWarned;
    private static readonly List<string> _traces = new();

    // ENDKNOT_PATCHPROF=1: additionally time the sub-steps of each compile (IL copy / Harmony
    // manipulate / JIT / trampoline / delegate type / function pointer) by replaying them once
    // more up front, and dump one line per target to <EndKnot_Logs>/EndKnot-PatchProf.txt.
    // Doubles the compile cost -- measurement only. The native detour itself (Dobby) measured at
    // ~0.5 ms per target and is not replayed.
    private static readonly bool DetailProfile = Environment.GetEnvironmentVariable("ENDKNOT_PATCHPROF") == "1";

    private static Harmony _harmony;
    private static bool _complete;
    private static int _patchedInPhase2;
    private static int _deferredTotal;
    private static Stopwatch _phase2Stopwatch;
    private static float _firstPumpRealtime = -1f;

    public static bool IsComplete => _complete;
    public static int DeferredCount => _deferredTotal;
    public static long Phase2Ms { get; private set; }
    public static int Phase2Frames { get; private set; }

    public static void RunPhase1(Harmony harmony, Assembly asm)
    {
        _harmony = harmony;
        DelegateTypeCache.Install(harmony);
        InstallResolver();

        if (!Main.DeferredPatching.Value)
        {
            BeginCollect();
            harmony.PatchAll(asm);
            EndCollect();
            FlushAll("phase1-all");
            _complete = true;
            _phase1Profile = Profile("phase1-all");
            Logger.Info(_phase1Profile, "PatchPhases");
            DumpDetail();
            return;
        }

        var sw = Stopwatch.StartNew();
        int patched = 0;

        BeginCollect();

        foreach (Type type in AccessTools.GetTypesFromAssembly(asm))
        {
            if (Classify(type))
            {
                _deferred.Enqueue(type);
                continue;
            }

            harmony.CreateClassProcessor(type).Patch();
            patched++;
        }

        EndCollect();
        long classMs = sw.ElapsedMilliseconds;
        FlushAll("phase1");

        sw.Stop();
        _deferredTotal = _deferred.Count;
        _phase1Profile = $"phase1 {patched} classes in {sw.ElapsedMilliseconds} ms (classes {classMs} ms), deferred {_deferred.Count} | {Profile("phase1")}";
    }

    // True iff every Harmony target this class declares (class-level and method-level) resolves
    // to a name in GameTypeNames. A class with no [HarmonyPatch] at all is not a patch class
    // (matches PatchClassProcessor's own unannotated-type no-op) and is never deferred -- it goes
    // through phase 1's harmless CreateClassProcessor(type).Patch() call same as today. A class
    // that resolves its target via TargetMethod/TargetMethods/Prepare instead of a typeof also
    // stays in phase 1, because that target can't be read back from attributes alone.
    private static bool Classify(Type t)
    {
        object[] classAttrs = t.GetCustomAttributes(typeof(HarmonyPatch), true);
        if (classAttrs.Length == 0) return false;

        var declaringTypes = new List<Type>();

        foreach (object attr in classAttrs)
            declaringTypes.Add(((HarmonyPatch)attr).info.declaringType);

        const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        foreach (MethodInfo method in t.GetMethods(flags))
        {
            foreach (object attr in method.GetCustomAttributes(typeof(HarmonyPatch), true))
                declaringTypes.Add(((HarmonyPatch)attr).info.declaringType);
        }

        // 安全弁 (HostJoinGate) 自身は必ず段階 1。
        if (t == typeof(HostJoinGate)) return false;

        // クラス属性が typeof 無しの bare でメソッド側に対象型を書く慣用句も、typed な対象が
        // 全てゲーム側なら後回しにできる。typed な対象が 1 つも無い (TargetMethod 等) なら段階 1。
        int typed = 0;
        foreach (Type declaringType in declaringTypes)
        {
            if (declaringType == null) continue;
            typed++;
            if (!GameTypeNames.Contains(declaringType.Name)) return false;
        }

        return typed > 0;
    }

    // Called every splash/menu frame. With batching, the first call processes every deferred
    // class at once (attribute resolution only -- cheap -- with the compiles swallowed into
    // _pendingCompile) and each call then spends up to budgetMs compiling pending targets,
    // oldest first. Without batching the classes themselves are patched under the budget. Either
    // way the loop runs until the queues drain or the boot-time ceiling (CeilingSeconds since the
    // first Pump call) is reached, at which point the rest is done synchronously via
    // EnsureComplete.
    public static void Pump(float budgetMs)
    {
        if (_complete) return;

        if (_firstPumpRealtime < 0f)
        {
            _firstPumpRealtime = Time.realtimeSinceStartup;
            _phase2Stopwatch = Stopwatch.StartNew();
            BootTimeline.Mark("patch2.begin");
            _factoryWarned = false;
            if (Batching) ProcessDeferredClasses();
        }

        Phase2Frames++;

        if (Time.realtimeSinceStartup - _firstPumpRealtime >= CeilingSeconds)
        {
            EnsureComplete("ceiling");
            return;
        }

        var budget = Stopwatch.StartNew();

        while (_deferred.Count > 0 && budget.Elapsed.TotalMilliseconds < budgetMs)
            PatchOne(_deferred.Dequeue());

        while (_pendingCompile.Count > 0 && budget.Elapsed.TotalMilliseconds < budgetMs)
            CompileOne(_pendingCompile.Dequeue());

        if (_deferred.Count == 0 && _pendingCompile.Count == 0)
            Complete("drained");
    }

    // Synchronous safety net: patch every remaining deferred class right now. Cheap no-op once
    // phase 2 is already complete. Called from the sites that can reach a hosted/joined game
    // before the menu pump has had a chance to drain the queue on its own.
    public static void EnsureComplete(string reason)
    {
        if (_complete) return;

        _phase2Stopwatch ??= Stopwatch.StartNew();

        if (Batching) ProcessDeferredClasses();

        while (_deferred.Count > 0)
            PatchOne(_deferred.Dequeue());

        FlushAll(reason);
        Complete(reason);
    }

    private static bool Batching => _resolverInstalled && Main.BatchedPatching.Value;

    private static void ProcessDeferredClasses()
    {
        if (_deferred.Count == 0) return;

        var sw = Stopwatch.StartNew();
        int n = _deferred.Count;
        BeginCollect();

        while (_deferred.Count > 0)
            PatchOne(_deferred.Dequeue());

        EndCollect();
        Logger.Info($"phase2 {n} classes resolved in {sw.ElapsedMilliseconds} ms, {_pendingCompile.Count} targets pending", "PatchPhases");
    }

    private static void PatchOne(Type type)
    {
        try
        {
            _harmony.CreateClassProcessor(type).Patch();
            _patchedInPhase2++;
        }
        catch (Exception e)
        {
            Logger.Error($"deferred patch failed: {type.FullName}: {e}", "PatchPhases");
        }
    }

    private static void Complete(string reason)
    {
        if (_complete) return;

        _complete = true;
        Phase2Ms = _phase2Stopwatch?.ElapsedMilliseconds ?? 0;

        BootTimeline.Mark("patch2.end");
        if (_phase1Profile != null) Logger.Info(_phase1Profile, "PatchPhases");
        Logger.Info($"phase2 done {_patchedInPhase2} classes in {Phase2Ms} ms over {Phase2Frames} frames reason={reason}", "PatchPhases");
        Logger.Info(Profile("phase2"), "PatchPhases");
        DumpDetail();
    }

    // ---- batching decorator -------------------------------------------------------------

    private static void InstallResolver()
    {
        if (_resolverInstalled) return;

        try
        {
            PatchManager.ResolvePatcher += OnResolvePatcher;
            _resolverInstalled = true;
        }
        catch (Exception e)
        {
            Logger.Error($"batching resolver install failed, falling back to per-class compiles: {e}", "PatchPhases");
        }
    }

    // Runs after Il2CppInterop's own resolver (subscription order), so args.MethodPatcher already
    // holds the il2cpp detour patcher for game methods. Managed originals (no patcher chosen yet)
    // are left alone and take Harmony's default path unbatched.
    private static void OnResolvePatcher(object sender, PatchManager.PatcherResolverEventArgs args)
    {
        if (args.MethodPatcher == null || args.MethodPatcher is BatchingPatcher) return;

        var wrapper = new BatchingPatcher(args.Original, args.MethodPatcher);
        _allPatchers.Add(wrapper);
        args.MethodPatcher = wrapper;
    }

    private static void BeginCollect() => _collecting = Batching;

    private static void EndCollect() => _collecting = false;

    private static void FlushAll(string reason)
    {
        if (_pendingCompile.Count == 0) return;

        var sw = Stopwatch.StartNew();
        int n = _pendingCompile.Count;

        while (_pendingCompile.Count > 0)
            CompileOne(_pendingCompile.Dequeue());

        Logger.Info($"flush {reason}: {n} targets compiled in {sw.ElapsedMilliseconds} ms", "PatchPhases");
    }

    private static void CompileOne(BatchingPatcher patcher)
    {
        _pendingSet.Remove(patcher);

        try
        {
            patcher.Compile();
        }
        catch (Exception e)
        {
            Logger.Error($"batched patch failed: {Describe(patcher.Original)} owners=[{string.Join(",", patcher.Owners())}]: {e}", "PatchPhases");
        }
    }

    private static string F(double v) => v.ToString("0.00");

    private static string Describe(MethodBase m) => $"{m.DeclaringType?.Name}.{m.Name}";

    private static string Profile(string tag)
    {
        int calls = 0, compiles = 0, skipped = 0;
        double ms = 0, recompileMs = 0, copyMs = 0, manipMs = 0, genMs = 0, trampMs = 0, delegMs = 0, ptrMs = 0;

        foreach (BatchingPatcher p in _allPatchers)
        {
            calls += p.Calls;
            compiles += p.Compiles;
            skipped += p.Skipped;
            ms += p.Ms;
            recompileMs += p.RecompileMs;
            copyMs += p.CopyMs;
            manipMs += p.ManipMs;
            genMs += p.GenMs;
            trampMs += p.TrampMs;
            delegMs += p.DelegMs;
            ptrMs += p.PtrMs;
        }

        var top = new StringBuilder();
        foreach (BatchingPatcher p in _allPatchers.OrderByDescending(p => p.Ms).Take(12))
            top.Append(Describe(p.Original)).Append(':').Append(p.Calls).Append('/').Append(p.Compiles).Append('/').Append(p.Ms.ToString("0")).Append("ms ");

        string detail = DetailProfile ? $" copyMs={copyMs:0} manipMs={manipMs:0} genMs={genMs:0} trampMs={trampMs:0} delegMs={delegMs:0} ptrMs={ptrMs:0} restMs={ms - copyMs - manipMs - genMs - trampMs - delegMs - ptrMs:0}" : "";
        return $"PATCHPROF {tag} batching={Batching} targets={_allPatchers.Count} detourCalls={calls} compiles={compiles} skipped={skipped} compileMs={ms:0} recompileMs={recompileMs:0} dtc={(DelegateTypeCache.Installed ? 1 : 0)}/{DelegateTypeCache.Hits}h/{DelegateTypeCache.Misses}m/{DelegateTypeCache.Fallbacks}f{detail} top=[{top.ToString().TrimEnd()}]";
    }

    private static void DumpDetail()
    {
        if (!DetailProfile) return;

        try
        {
            string basePath = OperatingSystem.IsAndroid() ? Main.DataPath : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string dir = Path.Combine(basePath, "EndKnot_Logs");
            Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            foreach (string t in _traces) sb.Append("# ").AppendLine(t);
            sb.AppendLine("target	calls	compiles	ms	copyMs	manipMs	genMs	trampMs	delegMs	ptrMs	restMs	patches	phase");
            foreach (BatchingPatcher p in _allPatchers.OrderByDescending(p => p.Ms))
                sb.Append(Describe(p.Original)).Append('	').Append(p.Calls).Append('	').Append(p.Compiles).Append('	').Append(F(p.Ms)).Append('	').Append(F(p.CopyMs)).Append('	').Append(F(p.ManipMs)).Append('	').Append(F(p.GenMs)).Append('	').Append(F(p.TrampMs)).Append('	').Append(F(p.DelegMs)).Append('	').Append(F(p.PtrMs)).Append('	').Append(F(p.Ms - p.CopyMs - p.ManipMs - p.GenMs - p.TrampMs - p.DelegMs - p.PtrMs)).Append('	').Append(p.Owners().Count()).Append('	').Append(p.Phase).AppendLine();

            File.WriteAllText(Path.Combine(dir, "EndKnot-PatchProf.txt"), sb.ToString());
        }
        catch (Exception e)
        {
            Logger.Error($"patch profile dump failed: {e.Message}", "PatchPhases");
        }
    }

    // Replacement for HarmonyLib.DelegateTypeFactory.CreateDelegateType(Type, Type[], CallingConvention?):
    // identical delegate shape (sealed MulticastDelegate subclass, runtime-implemented .ctor(object,
    // IntPtr) and Invoke, optional [UnmanagedFunctionPointer]) but emitted into one shared dynamic
    // module and reused for every method with the same signature. A delegate type is only its
    // signature, so sharing across targets is transparent to Marshal.GetFunctionPointerForDelegate.
    // Any failure falls through to the original factory for that call.
    public static class DelegateTypeCache
    {
        private static readonly Dictionary<string, Type> _types = new();
        private static ModuleBuilder _module;
        private static int _counter;
        private static bool _installed;

        public static int Hits { get; private set; }
        public static int Misses { get; private set; }
        public static int Fallbacks { get; private set; }
        public static bool Installed => _installed;

        public static void Install(Harmony harmony)
        {
            if (_installed || !Main.DelegateTypeCache.Value) return;

            try
            {
                MethodInfo target = AccessTools.Method(typeof(DelegateTypeFactory), nameof(DelegateTypeFactory.CreateDelegateType), new[] { typeof(Type), typeof(Type[]), typeof(CallingConvention?) });
                if (target == null) throw new MissingMethodException("DelegateTypeFactory.CreateDelegateType(Type, Type[], CallingConvention?)");

                harmony.Patch(target, prefix: new HarmonyMethod(typeof(DelegateTypeCache), nameof(Prefix)) { priority = Priority.First });
                _installed = true;
            }
            catch (Exception e)
            {
                Logger.Error($"delegate type cache install failed, using HarmonyX factory: {e}", "PatchPhases");
            }
        }

        private static bool Prefix(Type returnType, Type[] argTypes, CallingConvention? convention, ref Type __result)
        {
            try
            {
                var key = new StringBuilder(returnType.AssemblyQualifiedName).Append('|');
                foreach (Type t in argTypes) key.Append(t.AssemblyQualifiedName).Append(',');
                key.Append('|').Append(convention.HasValue ? ((int)convention.Value).ToString() : "-");
                string k = key.ToString();

                lock (_types)
                {
                    if (_types.TryGetValue(k, out Type cached))
                    {
                        Hits++;
                        __result = cached;
                        return false;
                    }

                    _module ??= AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("EndKnotDelegateTypes"), AssemblyBuilderAccess.Run).DefineDynamicModule("EndKnotDelegateTypes");

                    TypeBuilder tb = _module.DefineType($"EndKnotDelegate{++_counter}", TypeAttributes.Sealed | TypeAttributes.Public, typeof(MulticastDelegate));

                    if (convention.HasValue)
                    {
                        ConstructorInfo attrCtor = typeof(UnmanagedFunctionPointerAttribute).GetConstructor(new[] { typeof(CallingConvention) });
                        tb.SetCustomAttribute(new CustomAttributeBuilder(attrCtor, new object[] { convention.Value }));
                    }

                    ConstructorBuilder ctor = tb.DefineConstructor(MethodAttributes.RTSpecialName | MethodAttributes.HideBySig | MethodAttributes.Public, CallingConventions.Standard, new[] { typeof(object), typeof(IntPtr) });
                    ctor.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);

                    MethodBuilder invoke = tb.DefineMethod("Invoke", MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.Public, returnType, argTypes);
                    invoke.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);

                    Type created = tb.CreateType();
                    _types[k] = created;
                    Misses++;
                    __result = created;
                    return false;
                }
            }
            catch (Exception e)
            {
                Fallbacks++;
                if (Fallbacks <= 3) Logger.Error($"delegate type cache miss path failed, falling back: {e}", "PatchPhases");
                return true;
            }
        }
    }

    // Wraps the MethodPatcher Harmony picked for one target. While a phase is collecting, every
    // DetourTo (one per patch class touching this target) is swallowed and the target is queued
    // once; Compile() then performs the real detour with the accumulated PatchInfo. Outside a
    // collecting window (runtime Harmony.Patch/Unpatch calls) it is a transparent pass-through.
    private sealed class BatchingPatcher : MethodPatcher
    {
        private readonly MethodPatcher _inner;
        private MethodBase _lastReplacement;

        public int Calls;
        public int Compiles;
        public int Skipped;
        public double Ms;
        public double RecompileMs;
        public double CopyMs;
        public double ManipMs;
        public double GenMs;
        public double TrampMs;
        public double DelegMs;
        public double PtrMs;
        public string Phase = "";

        public BatchingPatcher(MethodBase original, MethodPatcher inner) : base(original) => _inner = inner;

        public override DynamicMethodDefinition PrepareOriginal() => _inner.PrepareOriginal();

        public override DynamicMethodDefinition CopyOriginal() => _inner.CopyOriginal();

        public override MethodBase DetourTo(MethodBase replacement)
        {
            Calls++;
            _lastReplacement = replacement;

            if (_collecting)
            {
                Skipped++;
                if (_pendingSet.Add(this)) _pendingCompile.Enqueue(this);
                return null;
            }

            return Compile();
        }

        public MethodBase Compile()
        {
            if (Phase.Length == 0) Phase = _complete || _firstPumpRealtime >= 0f ? "2" : "1";
            if (DetailProfile) MeasureSteps();

            var sw = Stopwatch.StartNew();

            try
            {
                return _inner.DetourTo(_lastReplacement);
            }
            finally
            {
                sw.Stop();
                Ms += sw.Elapsed.TotalMilliseconds;
                if (Compiles > 0) RecompileMs += sw.Elapsed.TotalMilliseconds;
                Compiles++;
            }
        }

        // Replays the pure-managed half of Il2CppDetourMethodPatcher.DetourTo (IL copy, Harmony
        // manipulate, JIT) so its cost can be split from the trampoline/delegate/native-detour rest.
        private void MeasureSteps()
        {
            try
            {
                var sw = Stopwatch.StartNew();
                DynamicMethodDefinition dmd = _inner.CopyOriginal();
                CopyMs += sw.Elapsed.TotalMilliseconds;
                if (dmd == null) return;

                sw.Restart();
                HarmonyManipulator.Manipulate(dmd.OriginalMethod, dmd.OriginalMethod.GetPatchInfo(), new ILContext(dmd.Definition));
                ManipMs += sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                MethodInfo hooked = dmd.Generate();
                GenMs += sw.Elapsed.TotalMilliseconds;

                // Il2CppDetourMethodPatcher's private trampoline pipeline, replayed step by step.
                MethodInfo gen = _inner.GetType().GetMethod("GenerateNativeToManagedTrampoline", BindingFlags.NonPublic | BindingFlags.Instance);
                if (gen == null)
                {
                    if (!_factoryWarned) { Logger.Info($"patch profile: GenerateNativeToManagedTrampoline not found on {_inner.GetType().FullName}", "PatchPhases"); _traces.Add($"patch profile: GenerateNativeToManagedTrampoline not found on {_inner.GetType().FullName}"); }
                    _factoryWarned = true;
                    return;
                }

                sw.Restart();
                object trampObj = gen.Invoke(_inner, new object[] { hooked });
                MethodInfo tramp = trampObj as MethodInfo ?? trampObj?.GetType().GetMethod("Generate", Type.EmptyTypes)?.Invoke(trampObj, null) as MethodInfo;
                TrampMs += sw.Elapsed.TotalMilliseconds;
                if (tramp == null)
                {
                    if (!_factoryWarned) { Logger.Info($"patch profile: trampoline not generated (obj={trampObj?.GetType().AssemblyQualifiedName ?? "null"})", "PatchPhases"); _traces.Add($"patch profile: trampoline not generated (obj={trampObj?.GetType().AssemblyQualifiedName ?? "null"})"); }
                    _factoryWarned = true;
                    return;
                }

                Type factoryType = null;
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        factoryType = asm.GetTypes().FirstOrDefault(t => t.Name == "DelegateTypeFactory");
                    }
                    catch (ReflectionTypeLoadException e)
                    {
                        factoryType = e.Types.FirstOrDefault(t => t != null && t.Name == "DelegateTypeFactory");
                    }

                    if (factoryType != null) break;
                }

                object factory = factoryType?.GetField("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
                                 ?? factoryType?.GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
                MethodInfo create = factoryType?.GetMethod("CreateDelegateType", new[] { typeof(MethodInfo), typeof(CallingConvention) });
                Delegate del = null;

                if (factory == null || create == null)
                {
                    if (!_factoryWarned) { Logger.Info($"patch profile: DelegateTypeFactory not resolved (inner={_inner.GetType().AssemblyQualifiedName} type={factoryType?.AssemblyQualifiedName ?? "null"} factory={factory != null} create={create != null})", "PatchPhases"); _traces.Add($"patch profile: DelegateTypeFactory not resolved (inner={_inner.GetType().AssemblyQualifiedName} type={factoryType?.AssemblyQualifiedName ?? "null"} factory={factory != null} create={create != null})"); }
                }
                else
                {
                    sw.Restart();
                    var delegateType = (Type)create.Invoke(factory, new object[] { tramp, CallingConvention.Cdecl });
                    DelegMs += sw.Elapsed.TotalMilliseconds;

                    sw.Restart();
                    del = tramp.CreateDelegate(delegateType);
                    Marshal.GetFunctionPointerForDelegate(del);
                    PtrMs += sw.Elapsed.TotalMilliseconds;
                }

                GC.KeepAlive(del);

                if (!_factoryWarned) { Logger.Info($"patch profile: full pipeline replayed for {Describe(Original)} (deleg {DelegMs:0.00} ptr {PtrMs:0.00})", "PatchPhases"); _traces.Add($"patch profile: full pipeline replayed for {Describe(Original)} (deleg {DelegMs:0.00} ptr {PtrMs:0.00})"); }
                _factoryWarned = true;
            }
            catch (Exception e)
            {
                Logger.Info($"patch profile step failed for {Describe(Original)}: {e.Message}", "PatchPhases");
                if (_traces.Count < 20) _traces.Add($"step failed {Describe(Original)}: {e}");
            }
        }

        public IEnumerable<string> Owners()
        {
            PatchInfo info = Original.GetPatchInfo();
            if (info == null) yield break;

            foreach (Patch p in info.prefixes.Concat(info.postfixes).Concat(info.transpilers).Concat(info.finalizers))
                yield return p.PatchMethod.DeclaringType?.Name + "." + p.PatchMethod.Name;
        }
    }

    // Last-resort gate: if something reaches InnerNetClient.HostGame/JoinGame before the menu
    // pump or an earlier gate has finished phase 2, finish it synchronously right here. The bare
    // class-level [HarmonyPatch] plus per-method typed attributes below (rather than a single
    // class-level typed attribute) is deliberate: Classify() treats a bare class attribute as an
    // unresolved target and always keeps this class in phase 1, so the gate itself is guaranteed
    // to be armed before any lobby can be hosted or joined.
    [HarmonyPatch]
    private static class HostJoinGate
    {
        [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.HostGame))]
        [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.JoinGame))]
        [HarmonyPrefix]
        public static void Prefix() => EnsureComplete("host|join");
    }
}
