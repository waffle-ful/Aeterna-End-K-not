using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using InnerNet;
using UnityEngine;

namespace EndKnot.Modules;

// Harmony.PatchAll(Assembly) walks every class in the assembly and JITs a detour for each
// target method, which is the single biggest chunk of Main.Load's startup cost. Most of that
// cost buys nothing at boot time: patches whose targets are all vanilla in-game types (HUD,
// meeting, sabotage, ...) can't possibly run before a lobby exists. Phase 1 patches everything
// else during Load; phase 2 patches the deferred classes a few at a time once the main menu is
// already interactive, with a synchronous safety net (EnsureComplete) guaranteeing every patch
// is in place before a lobby can be hosted or joined.
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

        if (!Main.DeferredPatching.Value)
        {
            harmony.PatchAll(asm);
            _complete = true;
            return;
        }

        var sw = Stopwatch.StartNew();
        int patched = 0;

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

        sw.Stop();
        _deferredTotal = _deferred.Count;
        Logger.Info($"phase1 {patched} classes in {sw.ElapsedMilliseconds} ms, deferred {_deferred.Count}", "PatchPhases");
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

    // Called every menu frame. Spends up to budgetMs patching deferred classes, oldest first,
    // until the queue drains or the boot-time ceiling (CeilingSeconds since the first Pump call)
    // is reached, at which point the rest are patched synchronously via EnsureComplete.
    public static void Pump(float budgetMs)
    {
        if (_complete) return;

        if (_firstPumpRealtime < 0f)
        {
            _firstPumpRealtime = Time.realtimeSinceStartup;
            _phase2Stopwatch = Stopwatch.StartNew();
            BootTimeline.Mark("patch2.begin");
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

        if (_deferred.Count == 0)
            Complete("drained");
    }

    // Synchronous safety net: patch every remaining deferred class right now. Cheap no-op once
    // phase 2 is already complete. Called from the sites that can reach a hosted/joined game
    // before the menu pump has had a chance to drain the queue on its own.
    public static void EnsureComplete(string reason)
    {
        if (_complete) return;

        _phase2Stopwatch ??= Stopwatch.StartNew();

        while (_deferred.Count > 0)
            PatchOne(_deferred.Dequeue());

        Complete(reason);
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
        Logger.Info($"phase2 done {_patchedInPhase2} classes in {Phase2Ms} ms over {Phase2Frames} frames reason={reason}", "PatchPhases");
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
