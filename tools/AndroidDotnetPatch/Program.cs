// Patches the bundled runtime and loader assemblies so that the game's libraries are found as loaded
// modules when the arm64 runtime runs under binary translation on an x86_64 emulator.
//
// Both System.Diagnostics.Process (Process.Modules) and MonoMod.Core (its own copy of the same parser)
// build the module list from /proc/self/maps and keep only file mappings that carry both the read and the
// execute bit. Under translation every arm64 library is mapped read-only (the translator executes its own
// translated copy), so the list holds none of them: Il2CppInterop cannot find libil2cpp.so and MonoMod
// cannot find libclrjit.so.
//
// The patch changes the compiler-generated helper <TryParseMapsEntry>g__HasReadAndExecFlags in each of them
// from "r and x" to "r and (x or FUSION_TRANSLATED is set)". The launcher sets FUSION_TRANSLATED only on a
// translated host, so on arm64 devices the lists are unchanged.
//
// MonoMod.Core also embeds MonoMod's native helper for Android (exhelper_android_arm64.so). The helper takes
// the unwinder (_Unwind_*) from the process's global symbols, which on a device come from the libraries
// app_process starts with. A translated process has no such libraries, so the helper cannot be linked there.
// The patch adds libfusionunwind.so (built by the launcher, exports the unwinder) to the helper's
// dependencies. Global symbols are searched first, so on a device the helper still binds to the system copy.
//
// A patched ReadyToRun image is written as plain IL: its precompiled code is dropped and the methods are
// compiled by the JIT on first use. The patch is idempotent.
//
// Usage: AndroidDotnetPatch <in zip> <out zip>   (in and out may be the same file)
//   Patches every System.Diagnostics.Process.dll / MonoMod.Core.dll inside the zip
//   (Android/Launcher/fusionApp/src/main/assets/dotnet-arm64.zip and BepInEx-arm64.zip).
using System.IO.Compression;
using Mono.Cecil;
using Mono.Cecil.Cil;

string[] targets = ["System.Diagnostics.Process.dll", "MonoMod.Core.dll"];
const string MethodPrefix = "<TryParseMapsEntry>g__HasReadAndExecFlags";
const string EnvName = "FUSION_TRANSLATED";
const string HelperResource = "exhelper_android_arm64.so";
const string UnwindLibrary = "libfusionunwind.so";

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: AndroidDotnetPatch <in zip> <out zip>");
    return 2;
}

var inPath = Path.GetFullPath(args[0]);
var outPath = Path.GetFullPath(args[1]);

var entries = new List<(string Name, DateTimeOffset Time, byte[] Data)>();
using (var zip = ZipFile.OpenRead(inPath))
{
    foreach (var e in zip.Entries)
    {
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        entries.Add((e.FullName, e.LastWriteTime, ms.ToArray()));
    }
}

var changed = 0;
var found = 0;
for (var i = 0; i < entries.Count; i++)
{
    var name = entries[i].Name;
    if (!targets.Any(t => name == t || name.EndsWith("/" + t, StringComparison.Ordinal))) continue;
    found++;
    var patched = Patch(entries[i].Data, name);
    if (patched == null) continue;
    entries[i] = (name, entries[i].Time, patched);
    changed++;
}

if (found == 0)
{
    Console.Error.WriteLine($"none of {string.Join(", ", targets)} found in {inPath}");
    return 1;
}

if (changed == 0)
{
    if (!string.Equals(inPath, outPath, StringComparison.OrdinalIgnoreCase)) File.Copy(inPath, outPath, overwrite: true);
    return 0;
}

var temp = outPath + ".tmp";
using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
{
    foreach (var (name, time, data) in entries)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        e.LastWriteTime = time;
        using var s = e.Open();
        s.Write(data);
    }
}
File.Move(temp, outPath, overwrite: true);
Console.WriteLine($"wrote {outPath}");
return 0;

static byte[] Patch(byte[] image, string name)
{
    using var module = ModuleDefinition.ReadModule(new MemoryStream(image));
    var modified = PatchMapsParser(module, name);

    var helper = module.Resources.OfType<EmbeddedResource>().SingleOrDefault(r => r.Name == HelperResource);
    if (helper != null)
    {
        var elf = AddNeeded(helper.GetResourceData(), UnwindLibrary);
        if (elf != null)
        {
            module.Resources[module.Resources.IndexOf(helper)] = new EmbeddedResource(helper.Name, helper.Attributes, elf);
            Console.WriteLine($"added {UnwindLibrary} to the dependencies of {HelperResource} in {name}");
            modified = true;
        }
    }

    if (!modified)
    {
        Console.WriteLine($"already patched: {name}");
        return null;
    }

    // Write plain IL: drop a ReadyToRun flag and target any CPU so the runtime JITs this image.
    module.Attributes = ModuleAttributes.ILOnly;
    module.Architecture = TargetArchitecture.I386;

    var output = new MemoryStream();
    module.Write(output);
    Console.WriteLine($"patched {name} ({output.Length} bytes)");
    return output.ToArray();
}

static bool PatchMapsParser(ModuleDefinition module, string name)
{
    var method = module.Types.SelectMany(AllTypes)
        .SelectMany(t => t.Methods)
        .Single(m => m.Name.StartsWith(MethodPrefix, StringComparison.Ordinal));
    var il = method.Body.Instructions;

    if (il.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == EnvName)) return false;

    // The execute test is the last one: ... ldc.i4.s 120 ('x') ... ret. The bool it leaves on the stack is
    // the return value of that branch.
    var execChar = il.Single(i => i.OpCode == OpCodes.Ldc_I4_S && (sbyte)i.Operand == 'x');
    var ret = execChar.Next;
    while (ret != null && ret.OpCode != OpCodes.Ret) ret = ret.Next;
    if (ret == null) throw new InvalidOperationException($"{name}: no ret after the execute test");

    // Reuse the scope this assembly already references System.Environment through.
    var environment = module.GetMemberReferences()
        .Select(m => m.DeclaringType)
        .First(t => t.FullName == "System.Environment");
    var getEnv = new MethodReference("GetEnvironmentVariable", module.TypeSystem.String, environment) { HasThis = false };
    getEnv.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));

    // <execute test> ? true : Environment.GetEnvironmentVariable(EnvName) != null
    var proc = method.Body.GetILProcessor();
    var isExec = proc.Create(OpCodes.Ldc_I4_1);
    proc.InsertBefore(ret, proc.Create(OpCodes.Brtrue_S, isExec));
    proc.InsertBefore(ret, proc.Create(OpCodes.Ldstr, EnvName));
    proc.InsertBefore(ret, proc.Create(OpCodes.Call, module.ImportReference(getEnv)));
    proc.InsertBefore(ret, proc.Create(OpCodes.Ldnull));
    proc.InsertBefore(ret, proc.Create(OpCodes.Cgt_Un));
    proc.InsertBefore(ret, proc.Create(OpCodes.Ret));
    proc.InsertBefore(ret, isExec);
    method.Body.MaxStackSize = Math.Max(method.Body.MaxStackSize, 2);
    Console.WriteLine($"patched the maps parser in {name}");
    return true;
}

// Adds a DT_NEEDED entry to a 64-bit little-endian shared library without growing the file. The library
// must carry both DT_GNU_HASH and DT_HASH with the SysV hash table directly in front of the string table:
// the string table moves down over the hash table (a linker that has DT_GNU_HASH does not read it), gains
// the new name at its end, and the freed DT_HASH entry becomes the DT_NEEDED entry.
// Returns null when the library already lists the dependency.
static byte[] AddNeeded(byte[] original, string library)
{
    const long DT_NULL = 0, DT_NEEDED = 1, DT_HASH = 4, DT_STRTAB = 5, DT_STRSZ = 10, DT_GNU_HASH = 0x6ffffef5;
    const uint PT_LOAD = 1, PT_DYNAMIC = 2, SHT_PROGBITS = 1, SHT_STRTAB = 3, SHT_HASH = 5;

    var elf = (byte[])original.Clone();
    if (elf.Length < 64 || BitConverter.ToUInt32(elf, 0) != 0x464C457F || elf[4] != 2 || elf[5] != 1)
        throw new InvalidOperationException("not a 64-bit little-endian ELF");

    var phoff = BitConverter.ToInt64(elf, 0x20);
    var shoff = BitConverter.ToInt64(elf, 0x28);
    int phentsize = BitConverter.ToUInt16(elf, 0x36), phnum = BitConverter.ToUInt16(elf, 0x38);
    int shentsize = BitConverter.ToUInt16(elf, 0x3A), shnum = BitConverter.ToUInt16(elf, 0x3C);

    var loads = new List<(long VAddr, long Offset, long FileSize)>();
    long dynOffset = -1, dynSize = 0;
    for (var i = 0; i < phnum; i++)
    {
        var ph = (int)(phoff + i * phentsize);
        var type = BitConverter.ToUInt32(elf, ph);
        if (type == PT_LOAD)
            loads.Add((BitConverter.ToInt64(elf, ph + 0x10), BitConverter.ToInt64(elf, ph + 0x08), BitConverter.ToInt64(elf, ph + 0x20)));
        else if (type == PT_DYNAMIC)
            (dynOffset, dynSize) = (BitConverter.ToInt64(elf, ph + 0x08), BitConverter.ToInt64(elf, ph + 0x20));
    }
    if (dynOffset < 0) throw new InvalidOperationException("no PT_DYNAMIC");

    long ToOffset(long vaddr)
    {
        foreach (var (va, off, size) in loads)
            if (vaddr >= va && vaddr < va + size) return vaddr - va + off;
        throw new InvalidOperationException($"address 0x{vaddr:X} is not in a file-backed segment");
    }

    long strtab = -1, strsz = -1, hash = -1;
    var hasGnuHash = false;
    var hashEntry = -1;
    var needed = new List<long>();
    for (var at = (int)dynOffset; at + 16 <= dynOffset + dynSize; at += 16)
    {
        var tag = BitConverter.ToInt64(elf, at);
        var value = BitConverter.ToInt64(elf, at + 8);
        if (tag == DT_NULL) break;
        if (tag == DT_NEEDED) needed.Add(value);
        else if (tag == DT_STRTAB) strtab = value;
        else if (tag == DT_STRSZ) strsz = value;
        else if (tag == DT_GNU_HASH) hasGnuHash = true;
        else if (tag == DT_HASH) (hash, hashEntry) = (value, at);
    }
    if (strtab < 0 || strsz < 0) throw new InvalidOperationException("no string table");

    var strtabOffset = ToOffset(strtab);
    string ReadString(long offset)
    {
        var start = (int)(strtabOffset + offset);
        var end = Array.IndexOf(elf, (byte)0, start);
        return System.Text.Encoding.ASCII.GetString(elf, start, end - start);
    }
    if (needed.Any(n => ReadString(n) == library)) return null;

    if (!hasGnuHash || hashEntry < 0) throw new InvalidOperationException("needs both DT_GNU_HASH and DT_HASH");
    var hashOffset = ToOffset(hash);
    if (strtabOffset <= hashOffset || strtab - hash != strtabOffset - hashOffset)
        throw new InvalidOperationException("the hash table is not directly in front of the string table");
    var name = System.Text.Encoding.ASCII.GetBytes(library + "\0");
    var room = strtabOffset - hashOffset;
    if (name.Length > room) throw new InvalidOperationException("not enough room for the new name");

    Array.Copy(elf, strtabOffset, elf, hashOffset, strsz);
    Array.Copy(name, 0, elf, hashOffset + strsz, name.Length);
    Array.Clear(elf, (int)(hashOffset + strsz + name.Length), (int)(room - name.Length));

    void WriteDyn(long tag, long value, int at)
    {
        BitConverter.GetBytes(tag).CopyTo(elf, at);
        BitConverter.GetBytes(value).CopyTo(elf, at + 8);
    }
    for (var at = (int)dynOffset; at + 16 <= dynOffset + dynSize; at += 16)
    {
        var tag = BitConverter.ToInt64(elf, at);
        if (tag == DT_NULL) break;
        if (tag == DT_STRTAB) WriteDyn(DT_STRTAB, hash, at);
        else if (tag == DT_STRSZ) WriteDyn(DT_STRSZ, strsz + name.Length, at);
    }
    WriteDyn(DT_NEEDED, strsz, hashEntry);

    // Keep the section headers truthful for tools that read them.
    for (var i = 0; i < shnum; i++)
    {
        var sh = (int)(shoff + i * shentsize);
        var type = BitConverter.ToUInt32(elf, sh + 0x04);
        var addr = BitConverter.ToInt64(elf, sh + 0x10);
        if (type == SHT_HASH && addr == hash)
        {
            BitConverter.GetBytes(SHT_PROGBITS).CopyTo(elf, sh + 0x04);
            BitConverter.GetBytes(0L).CopyTo(elf, sh + 0x20);
        }
        else if (type == SHT_STRTAB && addr == strtab)
        {
            BitConverter.GetBytes(hash).CopyTo(elf, sh + 0x10);
            BitConverter.GetBytes(hashOffset).CopyTo(elf, sh + 0x18);
            BitConverter.GetBytes(strsz + name.Length).CopyTo(elf, sh + 0x20);
        }
    }
    return elf;
}

static IEnumerable<TypeDefinition> AllTypes(TypeDefinition type) =>
    new[] { type }.Concat(type.NestedTypes.SelectMany(AllTypes));
