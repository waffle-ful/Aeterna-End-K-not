#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace EndKnot.Modules;

internal static class EmbeddedDeps
{
    private static bool _installed;
    private static readonly BepInEx.Logging.ManualLogSource Log =
        BepInEx.Logging.Logger.CreateLogSource("EndKnot.EmbeddedLoader");

    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        Log.LogInfo("Embedded dependency resolver installed.");

        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
    }

    // 同じ名前を二度ロードすると型が別物になるので、解決は 1 本ずつ・結果は使い回す
    // (JIT は主スレッド以外からもここへ来る)。
    private static readonly object Gate = new();
    private static readonly System.Collections.Generic.Dictionary<string, Assembly> Loaded = new(StringComparer.OrdinalIgnoreCase);

    // 埋め込んだ依存 DLL を今のスレッドで全部読み込んでおく。以後、別スレッドの JIT が
    // 解決ハンドラへ入って来ることが無くなる。
    public static void Preload()
    {
        const string prefix = "EndKnot.Resources.Libs.";

        foreach (string name in Assembly.GetExecutingAssembly().GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;

            try { Assembly.Load(new AssemblyName(name[prefix.Length..^4])); }
            catch (Exception e) { Log.LogWarning($"Preload failed for {name}: {e.Message}"); }
        }
    }

    private static Assembly? Resolve(object? sender, ResolveEventArgs args)
    {
        lock (Gate) return ResolveLocked(args);
    }

    private static Assembly? ResolveLocked(ResolveEventArgs args)
    {
        var requestedName = new AssemblyName(args.Name).Name + ".dll";
        if (Loaded.TryGetValue(requestedName, out Assembly? cached)) return cached;

        var asm = Assembly.GetExecutingAssembly();
        var resName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(requestedName, StringComparison.OrdinalIgnoreCase));

        if (resName == null)
            return null;

        using var stream = asm.GetManifestResourceStream(resName);
        if (stream == null)
            return null;

        using var ms = new MemoryStream();
        stream.CopyTo(ms);

        var loaded = Assembly.Load(ms.ToArray());
        Loaded[requestedName] = loaded;

        Log.LogInfo($"Loaded embedded assembly: {loaded.GetName().Name}");

        return loaded;
    }
}
