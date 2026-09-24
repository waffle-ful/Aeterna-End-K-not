// Lists the Unity engine internal calls (ICalls) that the mod can reach on Android and that the
// game's own libunity.so does not register.
//
// The game ships a libunity built with engine code stripping, so only the ICalls its own managed
// code uses are registered. Il2CppInterop resolves the ICall behind an unstripped Unity method
// lazily and, when the engine does not have it, substitutes a stub that throws
// NotSupportedException the moment it is called. Nothing fails at load time; a path breaks only
// when it is actually taken (even a log line that reads such a property).
//
// Method: every method body in the mod assembly is a root. Calls are followed into the interop
// assemblies, and each interop method that loads an ICall delegate field is mapped (through the
// ldstr "Type::Method" in the declaring type's static constructor) to its ICall signature. The
// registered set is read from the stripped libunity.so as "UnityEngine.*::*" strings.
//
// Usage:
//   audit  --mod <EndKnot.dll> --interop <dir> --libunity <libunity.so> [--baseline <file>] [--report <file>]
//   probe  --interop <dir> --libunity <libunity.so> --pattern <regex> [--full <unstripped libunity.so>]
//          (--full adds the ICalls a complete engine build has, e.g. modules the game strips entirely)
using System.Text;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

var opts = ParseArgs(args);
if (opts.Mode == null) return Usage();

var registered = ReadRegisteredIcalls(opts.Get("libunity"));
var interopDir = opts.Get("interop");
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(interopDir);
string coreDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(interopDir))!, "core");
if (Directory.Exists(coreDir)) resolver.AddSearchDirectory(coreDir);
var readerParams = new ReaderParameters { AssemblyResolver = resolver };

var interopNames = new HashSet<string>(Directory.GetFiles(interopDir, "*.dll").Select(Path.GetFileNameWithoutExtension));
var fieldToIcall = MapIcallFields(interopDir, readerParams);

return opts.Mode switch
{
    "probe" => Probe(opts.Get("pattern"), registered, fieldToIcall, opts.GetOrNull("full")),
    "audit" => Audit(opts, registered, fieldToIcall, interopNames, resolver, readerParams),
    _ => Usage()
};

static int Usage()
{
    Console.Error.WriteLine("usage: audit --mod <dll> --interop <dir> --libunity <so> [--baseline <file>] [--report <file>]");
    Console.Error.WriteLine("       probe --interop <dir> --libunity <so> --pattern <regex> [--full <unstripped so>]");
    return 2;
}

static int Probe(string pattern, HashSet<string> registered, Dictionary<string, string> fieldToIcall, string fullLibunity)
{
    var re = new Regex(pattern, RegexOptions.IgnoreCase);
    var candidates = fieldToIcall.Values.Concat(registered);
    if (fullLibunity != null) candidates = candidates.Concat(ReadRegisteredIcalls(fullLibunity));
    var known = new SortedSet<string>(candidates.Where(s => re.IsMatch(s)), StringComparer.Ordinal);
    int present = 0;
    foreach (string icall in known)
    {
        bool has = registered.Contains(icall);
        if (has) present++;
        Console.WriteLine($"{(has ? "present" : "MISSING")}  {icall}");
    }
    Console.WriteLine($"-- {known.Count} ICalls match, {present} registered in the game's libunity, {known.Count - present} missing");
    return 0;
}

static int Audit(Options opts, HashSet<string> registered, Dictionary<string, string> fieldToIcall,
    HashSet<string> interopNames, IAssemblyResolver resolver, ReaderParameters readerParams)
{
    var mod = AssemblyDefinition.ReadAssembly(opts.Get("mod"), readerParams);
    // Roots are tracked per top-level mod type (not per method) so the walk stays linear in the
    // number of mod types; the baseline is keyed the same way.
    var reached = new Dictionary<string, SortedSet<string>>(); // ICall -> root types
    var seen = new Dictionary<string, HashSet<string>>();       // interop method -> root types already followed
    var queue = new Queue<(MethodDefinition Method, string Root)>();

    foreach (var type in mod.MainModule.GetTypes())
    foreach (var method in type.Methods)
        Scan(method, RootType(type.FullName), false);

    while (queue.Count > 0)
    {
        var (method, root) = queue.Dequeue();
        Scan(method, root, true);
    }

    var missing = reached.Where(kv => !registered.Contains(kv.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();

    // A baseline entry is "<ICall> | <root type>", for pairs known not to run on Android.
    var baseline = new HashSet<string>();
    string baselinePath = opts.GetOrNull("baseline");
    if (baselinePath != null && File.Exists(baselinePath))
    {
        foreach (string raw in File.ReadAllLines(baselinePath))
        {
            string line = raw.Split('#')[0].Trim();
            if (line.Length > 0) baseline.Add(Regex.Replace(line, @"\s*\|\s*", " | "));
        }
    }

    var report = new StringBuilder();
    var unexpected = new List<string>();
    report.AppendLine($"ICalls reachable from the mod: {reached.Count}, not registered in the game's libunity: {missing.Count}");
    foreach (var (icall, roots) in missing)
    {
        report.AppendLine($"MISSING {icall}");
        foreach (string root in roots)
        {
            string pair = $"{icall} | {root}";
            bool known = baseline.Contains(pair);
            if (!known) unexpected.Add(pair);
            report.AppendLine($"    {(known ? "baseline" : "NEW     ")} <- {root}");
        }
    }

    string reportPath = opts.GetOrNull("report");
    if (reportPath != null) File.WriteAllText(reportPath, report.ToString());
    else Console.Write(report);

    foreach (string pair in unexpected.Distinct().OrderBy(s => s, StringComparer.Ordinal))
        Console.WriteLine($"NEW  {pair}");
    Console.WriteLine(unexpected.Count == 0
        ? $"OK - {missing.Count} missing ICalls, all reached only from baseline entries"
        : $"FAIL - {unexpected.Distinct().Count()} new ICall/caller pairs the game's libunity cannot serve");
    return unexpected.Count == 0 ? 0 : 1;

    void Scan(MethodDefinition method, string root, bool isInterop)
    {
        if (!method.HasBody) return;
        foreach (var ins in method.Body.Instructions)
        {
            if (isInterop && ins.OpCode == OpCodes.Ldsfld && ins.Operand is FieldReference field
                && fieldToIcall.TryGetValue(field.FullName, out string icall))
            {
                if (!reached.TryGetValue(icall, out var roots)) reached[icall] = roots = new SortedSet<string>(StringComparer.Ordinal);
                roots.Add(root);
            }

            if (ins.Operand is not MethodReference callee) continue;
            string scope = callee.DeclaringType.Scope switch
            {
                ModuleDefinition md => md.Assembly.Name.Name,
                AssemblyNameReference an => an.Name,
                _ => null
            };
            if (scope == null || !interopNames.Contains(scope)) continue;

            MethodDefinition target;
            try { target = callee.Resolve(); }
            catch (AssemblyResolutionException) { continue; }
            if (target == null) continue;

            if (!seen.TryGetValue(target.FullName, out var followed)) seen[target.FullName] = followed = new HashSet<string>();
            if (followed.Add(root)) queue.Enqueue((target, root));
        }
    }
}

// Top-level type of a (possibly nested) mod type, e.g. "EndKnot.Modules.MemCensus/<>c" -> "EndKnot.Modules.MemCensus".
static string RootType(string typeFullName) => typeFullName.Split('/')[0];

// Interop wraps each unstripped extern in a delegate field initialised in the static constructor
// from IL2CPP.ResolveICall("<Type>::<Method>").
static Dictionary<string, string> MapIcallFields(string interopDir, ReaderParameters readerParams)
{
    var map = new Dictionary<string, string>();
    foreach (string file in Directory.GetFiles(interopDir, "*.dll"))
    {
        AssemblyDefinition asm;
        try { asm = AssemblyDefinition.ReadAssembly(file, readerParams); }
        catch (BadImageFormatException) { continue; }

        foreach (var type in asm.MainModule.GetTypes())
        foreach (var method in type.Methods)
        {
            if (!method.HasBody) continue;
            string pending = null;
            foreach (var ins in method.Body.Instructions)
            {
                if (ins.OpCode == OpCodes.Ldstr && ins.Operand is string s && s.Contains("::") && !s.Contains(' '))
                    pending = s;
                else if (ins.OpCode == OpCodes.Stsfld && pending != null && ins.Operand is FieldReference f && f.Name.EndsWith("DelegateField"))
                {
                    map[f.FullName] = pending;
                    pending = null;
                }
            }
        }
    }
    return map;
}

static HashSet<string> ReadRegisteredIcalls(string libunityPath)
{
    // The engine registers ICalls by their managed signature string; Latin-1 keeps every byte 1:1.
    string text = Encoding.Latin1.GetString(File.ReadAllBytes(libunityPath));
    var set = new HashSet<string>(StringComparer.Ordinal);
    foreach (Match m in Regex.Matches(text, @"(?:UnityEngine|Unity)\.[A-Za-z0-9_.]+(?:/[A-Za-z0-9_]+)?::[A-Za-z0-9_]+"))
        set.Add(m.Value);
    return set;
}

static Options ParseArgs(string[] args)
{
    var o = new Options { Mode = args.Length > 0 ? args[0] : null };
    for (int i = 1; i + 1 < args.Length; i += 2)
        if (args[i].StartsWith("--")) o.Values[args[i][2..]] = args[i + 1];
    return o;
}

internal sealed class Options
{
    public string Mode;
    public readonly Dictionary<string, string> Values = new();
    public string GetOrNull(string key) => Values.TryGetValue(key, out string v) ? v : null;
    public string Get(string key) => GetOrNull(key) ?? throw new ArgumentException($"missing --{key}");
}
