using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine.SceneManagement;

namespace EndKnot.Modules;

// Based on Mini.RegionInstall by duikbo (GPL-3.0): https://github.com/miniduikboot/Mini.RegionInstall
//
// Reads the same BepInEx/config/at.duikbo.regioninstall.cfg so upgrading from that plugin keeps a
// user's custom regions. [General] Regions is either the full ServerManager.JsonServerData JSON or
// a bare IRegionInfo[] array (both Newtonsoft-serialized with $type, so DnsRegionInfo /
// StaticHttpRegionInfo / StaticRegionInfo deserialize to the right concrete class); RemoveRegions
// is a comma-separated list of region names to drop.
//
// Applied on every "MainMenu" scene load, not just the first: returning from a game re-runs the
// remove+add so a session that reset ServerManager's list keeps the custom regions too. By the
// time Unity raises sceneLoaded for that scene, MainMenuManager.Awake (and with it
// PatchPhases.MenuGate, which the ServerManager patches below are queued behind) has already run,
// and ServerManager has already loaded its own server file, so there is nothing to race here.
public static class RegionInstaller
{
    private const string PluginGuid = "at.duikbo.regioninstall";
    private const string ConfigFileName = "at.duikbo.regioninstall.cfg";

    private static string _regionsJson;
    private static string _removeRegionsCsv;
    private static Dictionary<string, IRegionInfo> _installedByName;
    private static bool _subscribed;
    private static bool _coexistLogged;

    public static void Install()
    {
        string path = Path.Combine(BepInEx.Paths.ConfigPath, ConfigFileName);
        if (!File.Exists(path)) return;

        try { (_regionsJson, _removeRegionsCsv) = ReadConfig(path); }
        catch (Exception e)
        {
            Logger.Error($"region install: config read failed: {e}", "RegionInstaller");
            return;
        }

        if (string.IsNullOrEmpty(_regionsJson) && string.IsNullOrEmpty(_removeRegionsCsv)) return;

        _subscribed = true;
        SceneManager.add_sceneLoaded((Action<Scene, LoadSceneMode>)OnSceneLoaded);
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!_subscribed || scene.name != "MainMenu") return;

        try { Apply(); }
        catch (Exception e) { Logger.Error($"region install: apply failed: {e}", "RegionInstaller"); }
    }

    // Evaluated here rather than in Install(): Install() can run from inside the plugin load loop
    // (synchronous Defer), before the chainloader has registered every plugin. By the time a scene
    // has loaded, the plugin list is final.
    private static bool OldPluginLoaded()
    {
        try { return IL2CPPChainloader.Instance.Plugins.ContainsKey(PluginGuid); }
        catch (Exception e)
        {
            Logger.Error($"region install: plugin list check failed: {e}", "RegionInstaller");
            return false;
        }
    }

    private static void Apply()
    {
        if (OldPluginLoaded())
        {
            if (!_coexistLogged) Logger.Info("region install: Mini.RegionInstall is still loaded, leaving regions to it", "RegionInstaller");
            _coexistLogged = true;
            return;
        }

        ServerManager serverMngr = ServerManager.Instance;
        if (serverMngr == null) return;

        if (!string.IsNullOrEmpty(_removeRegionsCsv))
        {
            string[] rm = _removeRegionsCsv.Split(',');
            serverMngr.AvailableRegions = serverMngr.AvailableRegions
                .Where(r => Array.FindIndex(rm, name => name.Equals(r.Name, StringComparison.OrdinalIgnoreCase)) == -1)
                .ToArray();
        }

        if (string.IsNullOrEmpty(_regionsJson)) return;

        IRegionInfo[] parsed;
        try { parsed = ParseRegions(_regionsJson); }
        catch (Exception e)
        {
            Logger.Error($"region install: Regions parse failed: {e}", "RegionInstaller");
            return;
        }

        if (parsed.Length == 0) return;

        IRegionInfo currentRegion = serverMngr.CurrentRegion;
        var installed = new Dictionary<string, IRegionInfo>();

        foreach (IRegionInfo region in parsed)
        {
            if (region == null) continue;

            // AddOrUpdateRegion replaces same-named regions with a new instance; keep tracking the
            // current selection by name so it can be restored below instead of pointing at a
            // region object ServerManager no longer holds.
            if (currentRegion != null && region.Name.Equals(currentRegion.Name, StringComparison.OrdinalIgnoreCase))
                currentRegion = region;

            serverMngr.AddOrUpdateRegion(region);
            installed[region.Name] = region;
        }

        if (currentRegion != null) serverMngr.SetRegion(currentRegion);

        _installedByName = installed;
    }

    private static IRegionInfo[] ParseRegions(string regions)
    {
        string trimmed = regions.TrimStart();
        if (trimmed.Length == 0) return Array.Empty<IRegionInfo>();

        switch (trimmed[0])
        {
            case '{':
                var data = JsonConvert.DeserializeObject<ServerManager.JsonServerData>(regions, new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto });
                return data?.Regions?.ToArray() ?? Array.Empty<IRegionInfo>();
            case '[':
                return ParseRegions("{\"CurrentRegionIdx\":0,\"Regions\":" + regions + "}");
            default:
                Logger.Error("region install: could not detect Regions format", "RegionInstaller");
                return Array.Empty<IRegionInfo>();
        }
    }

    // ServerManager can reselect a stale IRegionInfo for the current region name on its own (e.g.
    // ReselectServer, or LoadServers running again later) which would otherwise revert to the
    // vanilla server list entry instead of the installed override.
    private static void CorrectCurrentRegion(ServerManager instance)
    {
        if (_installedByName == null) return;

        IRegionInfo region = instance.CurrentRegion;
        if (region == null) return;

        if (_installedByName.TryGetValue(region.Name, out IRegionInfo installed))
            instance.CurrentRegion = installed;
    }

    [HarmonyPatch(typeof(ServerManager), nameof(ServerManager.ReselectServer))]
    private static class ReselectServerPatch
    {
        private static void Prefix(ServerManager __instance)
        {
            try { CorrectCurrentRegion(__instance); }
            catch (Exception e) { Logger.Error($"region install: ReselectServer correction failed: {e}", "RegionInstaller"); }
        }
    }

    [HarmonyPatch(typeof(ServerManager), nameof(ServerManager.LoadServers))]
    private static class LoadServersPatch
    {
        private static void Postfix(ServerManager __instance)
        {
            try
            {
                // Nothing installed (no config, or the old plugin owns the regions): leave ServerManager alone.
                if (_installedByName == null) return;

                CorrectCurrentRegion(__instance);

                // DnsRegionInfo.Servers is only populated after PopulateServers runs, which may
                // not have happened yet for a freshly-installed region; an empty array here just
                // means CurrentUdpServer stays whatever ServerManager itself already picked.
                if (__instance.CurrentRegion?.Servers != null && __instance.CurrentRegion.Servers.Length > 0)
                    __instance.CurrentUdpServer = __instance.CurrentRegion.Servers[0];
            }
            catch (Exception e) { Logger.Error($"region install: LoadServers correction failed: {e}", "RegionInstaller"); }
        }
    }

    private static (string regions, string removeRegions) ReadConfig(string path)
    {
        string regions = null, removeRegions = null;
        string section = null;

        foreach (string rawLine in File.ReadAllLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;

            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                section = line.Substring(1, line.Length - 2);
                continue;
            }

            if (section != "General") continue;

            int eq = line.IndexOf('=');
            if (eq < 0) continue;

            string key = line.Substring(0, eq).Trim();
            string value = Unescape(line.Substring(eq + 1).Trim());

            if (key == "Regions") regions = value;
            else if (key == "RemoveRegions") removeRegions = value;
        }

        return (regions, removeRegions);
    }

    // BepInEx.Configuration.TomlTypeConverter's string unescaping, reproduced read-only: the
    // config file stores quotes/backslashes escaped so the ini "key = value" split and section
    // parsing stay unambiguous, and ConfigEntry<string>.Value unescapes them back on load.
    private static string Unescape(string txt)
    {
        if (string.IsNullOrEmpty(txt)) return txt;

        var sb = new StringBuilder(txt.Length);
        int i = 0;

        while (i < txt.Length)
        {
            int next = txt.IndexOf('\\', i);
            if (next < 0 || next == txt.Length - 1) next = txt.Length;

            sb.Append(txt, i, next - i);
            if (next >= txt.Length) break;

            char c = txt[next + 1];
            switch (c)
            {
                case '0': sb.Append('\0'); break;
                case 'a': sb.Append('\a'); break;
                case 'b': sb.Append('\b'); break;
                case 't': sb.Append('\t'); break;
                case 'n': sb.Append('\n'); break;
                case 'v': sb.Append('\v'); break;
                case 'f': sb.Append('\f'); break;
                case 'r': sb.Append('\r'); break;
                case '\'': sb.Append('\''); break;
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                default: sb.Append('\\').Append(c); break;
            }

            i = next + 2;
        }

        return sb.ToString();
    }
}
