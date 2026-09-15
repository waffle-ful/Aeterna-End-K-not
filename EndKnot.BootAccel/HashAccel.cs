using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace EndKnot.BootAccel
{
    // Seam: BepInEx.Utility.HashStream(Stream) -> string
    //
    // TypeLoader.FindPluginTypes does File.ReadAllBytes(dll) -> MemoryStream -> HashStream(ms),
    // and uses the returned MD5 purely as the validity key of its plugin type cache. Only the
    // MD5 is elided here; the ReadAllBytes already happened before the call.
    //
    // HashStream is public, so an unrelated caller may hand over a stream that cannot be
    // attributed to a file. Every unresolved case runs the original: serving a hash for the
    // wrong file would make BepInEx accept stale cached type metadata, which is far worse than
    // being slow.
    internal static class HashAccel
    {
        internal static MethodInfo Target;

        private sealed class Candidate
        {
            internal string Path;
            internal long Size;
            internal long MtimeTicks;
            internal bool Attributed;
        }

        private static List<Candidate> _files;

        [ThreadStatic] private static Candidate _pending;
        [ThreadStatic] private static bool _skipped;
        [ThreadStatic] private static long _t0;

        internal static bool Resolve()
        {
            Type t = typeof(BepInEx.Utility);
            Target = t.GetMethod("HashStream", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Stream) }, null);
            if (Target == null || Target.ReturnType != typeof(string))
            {
                BootAccelPatcher.Warn("Utility.HashStream signature mismatch; hash accel disabled");
                Target = null;
                return false;
            }

            return true;
        }

        private static void EnsureFiles()
        {
            if (_files != null) return;
            _files = new List<Candidate>();
            try
            {
                // Same enumeration TypeLoader.FindPluginTypes performs, so ordering lines up.
                string dir = Path.GetFullPath(BepInEx.Paths.PluginPath);
                if (!Directory.Exists(dir)) return;

                foreach (string f in Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories))
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        _files.Add(new Candidate { Path = f, Size = fi.Length, MtimeTicks = fi.LastWriteTimeUtc.Ticks });
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                BootAccelPatcher.Warn("plugin enumeration failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Invariant: a cached hash is written only when the file is identified unambiguously by
        // size. A stream is attributed to a candidate only when exactly one not-yet-attributed
        // file has that length; two files of equal size make both permanently unattributable and
        // the original runs for them. Enumeration order carries no weight, because a stream
        // arriving out of order would otherwise store its MD5 under a same-sized neighbour's
        // path and the next boot would serve that hash for the wrong file.
        //
        // A candidate stays marked once attributed even if the hash is then discarded, so no file
        // is ever attributed twice; the error always falls on the side of not caching.
        private static Candidate Attribute(Stream stream)
        {
            EnsureFiles();
            if (_files == null || _files.Count == 0) return null;

            long length = stream.Length;
            int found = -1;
            for (int i = 0; i < _files.Count; i++)
            {
                if (_files[i].Attributed || _files[i].Size != length) continue;
                if (found >= 0) return null; // ambiguous
                found = i;
            }

            if (found < 0) return null;
            // HashStream is public, so a same-sized stream from an unrelated caller could arrive
            // first. Size alone cannot tell them apart; the leading bytes can. A mismatch leaves
            // the candidate unattributed so the real file can still claim it later.
            if (!HeadMatches(stream, _files[found].Path)) return null;
            _files[found].Attributed = true;
            return _files[found];
        }

        private const int HeadBytes = 512;

        private static bool HeadMatches(Stream stream, string path)
        {
            var fromStream = new byte[HeadBytes];
            var fromFile = new byte[HeadBytes];
            int n;
            try
            {
                n = ReadFully(stream, fromStream);
            }
            finally
            {
                stream.Position = 0; // the original hashes from the current position
            }
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (ReadFully(fs, fromFile) != n) return false;
            }
            for (int i = 0; i < n; i++)
                if (fromStream[i] != fromFile[i]) return false;
            return true;
        }

        private static int ReadFully(Stream s, byte[] buf)
        {
            int total = 0;
            while (total < buf.Length)
            {
                int r = s.Read(buf, total, buf.Length - total);
                if (r <= 0) break;
                total += r;
            }
            return total;
        }

        public static bool HashStream_Prefix(Stream stream, ref string __result)
        {
            _pending = null;
            _skipped = false;
            _t0 = Stopwatch.GetTimestamp();

            BootAccelPatcher.EnsureSummaryScheduled();

            try
            {
                // Position 0 matters: the original hashes from the current position forward, while
                // attribution here goes by total Length. TypeLoader always hands over a fresh
                // stream at 0, so anything else is a caller we cannot account for.
                if (stream == null || !stream.CanSeek || stream.Position != 0) return true;

                Candidate c = Attribute(stream);
                if (c == null) return true;
                _pending = c;

                HashEntry e;
                if (AccelCache.Hashes.TryGetValue(c.Path, out e) && e.Size == c.Size && e.MtimeTicks == c.MtimeTicks && !string.IsNullOrEmpty(e.Hash))
                {
                    __result = e.Hash;
                    _skipped = true;
                    return false;
                }
            }
            catch (Exception ex)
            {
                BootAccelPatcher.Warn("hash prefix: " + ex.GetType().Name + ": " + ex.Message);
                _pending = null;
                _skipped = false;
                return true;
            }

            return true;
        }

        public static void HashStream_Postfix(string __result)
        {
            try
            {
                BootAccelPatcher.AddElapsed(_t0);
                BootAccelPatcher.CountHash(_skipped);
                if (_skipped) return;

                Candidate c = _pending;
                if (c == null || string.IsNullOrEmpty(__result)) return;

                AccelCache.Hashes[c.Path] = new HashEntry { Size = c.Size, MtimeTicks = c.MtimeTicks, Hash = __result };
                // Plugin DLLs are hashed one after another; the file is written once, after the
                // last of them, from the chainloader checkpoint.
                AccelCache.MarkDirty();
            }
            catch (Exception ex)
            {
                BootAccelPatcher.Warn("hash postfix: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
