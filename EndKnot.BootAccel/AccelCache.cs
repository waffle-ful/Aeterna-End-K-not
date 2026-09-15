using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EndKnot.BootAccel
{
    internal sealed class SigEntry
    {
        internal bool Found;   // did the original scan return non-zero?
        internal long Rva;     // result - module.BaseAddress (only meaningful when Found)
    }

    internal sealed class HashEntry
    {
        internal long Size;
        internal long MtimeTicks;
        internal string Hash;
    }

    // One in-memory model with a single writer, so the plugin-hash write cannot clobber the
    // Class::Init data written earlier in the same boot. Mutations only mark the model dirty;
    // the file is rewritten at a few checkpoints (see Save), never once per entry. A boot that
    // dies before the next checkpoint simply leaves the missing entries uncached and the next
    // boot re-derives them, which costs speed only.
    internal static class AccelCache
    {
        internal const int Version = 1;

        internal static string Path;

        internal static long GameAssemblySize;
        internal static long GameAssemblyMtimeTicks;
        internal static string ResolvedKind = "unknown";
        internal static long ClassInitRva = -1;

        internal static readonly Dictionary<string, SigEntry> Sigs = new Dictionary<string, SigEntry>(StringComparer.Ordinal);
        internal static readonly Dictionary<string, HashEntry> Hashes = new Dictionary<string, HashEntry>(StringComparer.OrdinalIgnoreCase);

        private static readonly object Gate = new object();
        private static bool _loaded;
        private static bool _dirty;

        // Every mutation of the fields above is announced here; until then Save writes nothing.
        internal static void MarkDirty()
        {
            _dirty = true;
        }

        internal static void Load(string path)
        {
            Path = path;
            _loaded = true;
            try
            {
                if (!File.Exists(path)) return;
                var root = Json.Obj(Json.Parse(File.ReadAllText(path, Encoding.UTF8)));
                if (root == null) return;
                if (Json.GetLong(root, "v", -1) != Version) return; // format change == total miss

                var ga = Json.Obj(root["gameAssembly"]);
                GameAssemblySize = Json.GetLong(ga, "gameAssemblySize", 0);
                GameAssemblyMtimeTicks = Json.GetLong(ga, "gameAssemblyMtimeTicks", 0);
                ResolvedKind = Json.GetStr(ga, "resolvedKind") ?? "unknown";
                ClassInitRva = Json.GetLong(ga, "classInitRva", -1);

                var sigs = Json.Arr(root.ContainsKey("sigs") ? root["sigs"] : null);
                if (sigs != null)
                    foreach (var o in sigs)
                    {
                        var e = Json.Obj(o);
                        string k = Json.GetStr(e, "key");
                        if (string.IsNullOrEmpty(k)) continue;
                        Sigs[k] = new SigEntry { Found = Json.GetBool(e, "found", false), Rva = Json.GetLong(e, "rva", 0) };
                    }

                var hashes = Json.Arr(root.ContainsKey("hashes") ? root["hashes"] : null);
                if (hashes != null)
                    foreach (var o in hashes)
                    {
                        var e = Json.Obj(o);
                        string p = Json.GetStr(e, "path");
                        string h = Json.GetStr(e, "hash");
                        if (string.IsNullOrEmpty(p) || string.IsNullOrEmpty(h)) continue;
                        Hashes[p] = new HashEntry
                        {
                            Size = Json.GetLong(e, "size", -1),
                            MtimeTicks = Json.GetLong(e, "mtimeTicks", -1),
                            Hash = h
                        };
                    }
            }
            catch (Exception ex)
            {
                // A corrupt/unreadable cache is a total miss, never a boot failure.
                Sigs.Clear();
                Hashes.Clear();
                GameAssemblySize = 0;
                GameAssemblyMtimeTicks = 0;
                ResolvedKind = "unknown";
                ClassInitRva = -1;
                BootAccelPatcher.Warn("cache load failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Called once the real GameAssembly.dll identity is known. Drops signature results
        // (and the recorded Class::Init resolution) when the native image changed.
        internal static void EnsureGameAssembly(long size, long mtimeTicks)
        {
            if (GameAssemblySize == size && GameAssemblyMtimeTicks == mtimeTicks) return;
            MarkDirty();
            Sigs.Clear();
            ResolvedKind = "unknown";
            ClassInitRva = -1;
            GameAssemblySize = size;
            GameAssemblyMtimeTicks = mtimeTicks;
        }

        // Writes only when something changed since the last write, so the call sites can be
        // placed by phase (signature scan done / plugin hashing done / preloader done) without
        // the file being rewritten once per cached entry.
        internal static void Save()
        {
            if (!_loaded || string.IsNullOrEmpty(Path)) return;
            try
            {
                lock (Gate)
                {
                    if (!_dirty) return;

                    var sb = new StringBuilder(4096);
                    sb.Append('{');
                    Json.Prop(sb, "v", Version); sb.Append(',');

                    Json.Str(sb, "gameAssembly"); sb.Append(":{");
                    Json.Prop(sb, "gameAssemblySize", GameAssemblySize); sb.Append(',');
                    Json.Prop(sb, "gameAssemblyMtimeTicks", GameAssemblyMtimeTicks); sb.Append(',');
                    Json.Prop(sb, "classInitRva", ClassInitRva); sb.Append(',');
                    Json.Prop(sb, "resolvedKind", ResolvedKind);
                    sb.Append("},");

                    Json.Str(sb, "sigs"); sb.Append(":[");
                    bool first = true;
                    foreach (var kv in Sigs)
                    {
                        if (!first) sb.Append(','); first = false;
                        sb.Append('{');
                        Json.Prop(sb, "key", kv.Key); sb.Append(',');
                        Json.Prop(sb, "found", kv.Value.Found); sb.Append(',');
                        Json.Prop(sb, "rva", kv.Value.Rva);
                        sb.Append('}');
                    }
                    sb.Append("],");

                    Json.Str(sb, "hashes"); sb.Append(":[");
                    first = true;
                    foreach (var kv in Hashes)
                    {
                        if (!first) sb.Append(','); first = false;
                        sb.Append('{');
                        Json.Prop(sb, "path", kv.Key); sb.Append(',');
                        Json.Prop(sb, "size", kv.Value.Size); sb.Append(',');
                        Json.Prop(sb, "mtimeTicks", kv.Value.MtimeTicks); sb.Append(',');
                        Json.Prop(sb, "hash", kv.Value.Hash);
                        sb.Append('}');
                    }
                    sb.Append("]}");

                    string dir = System.IO.Path.GetDirectoryName(Path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    string tmp = Path + ".tmp";
                    File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                    File.Move(tmp, Path, true);
                    // Cleared only after the file is actually in place: a failed write stays
                    // dirty so the next checkpoint retries it.
                    _dirty = false;
                }
            }
            catch (Exception ex)
            {
                BootAccelPatcher.Warn("cache save failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
