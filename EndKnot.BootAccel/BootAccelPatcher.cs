using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using BepInEx.Logging;
using BepInEx.Preloader.Core.Patching;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace EndKnot.BootAccel
{
    // BepInEx preloader patcher that removes two costs the loader pays again on every single
    // start, even when nothing on disk has changed:
    //   * the brute-force byte scan of the whole GameAssembly image looking for Class::Init
    //   * the MD5 of every plugin DLL, recomputed before the plugin type cache is consulted
    //
    // Both results are stored in BepInEx/cache/endknot-bootaccel.json, keyed by file size and
    // last-write time, and replayed on the next boot.
    //
    // Invariants:
    //   * Any doubt runs the original. A cached value is served only on an exact size+mtime
    //     match of the file it was taken from; an unknown key, an unexpected method shape, an
    //     ambiguous stream or any exception all fall through to the unmodified method. Serving
    //     a wrong hash would make BepInEx accept stale type metadata, which is far worse than
    //     being slow.
    //   * Nothing here may stop the boot. Every step is wrapped; a failure costs speed only.
    //   * Placing a file named endknot-bootaccel.disabled in BepInEx/cache turns everything off.
    [PatcherPluginInfo(PatcherGuid, "End K not Boot Accelerator", PatcherVersion)]
    public class BootAccelPatcher : BasePatcher
    {
        internal const string PatcherGuid = "endknot.bootaccel";

        // Versioned independently of the mod: this assembly is loaded by the preloader long
        // before the plugin exists and changes on its own schedule.
        internal const string PatcherVersion = "1.0.0";

        private const string CacheFileName = "endknot-bootaccel.json";
        private const string DisableFileName = "endknot-bootaccel.disabled";

        private static ManualLogSource _log;

        private static int _subscribed;
        private static int _summaryEmitted;

        // Counters read by the summary. The chainloader raises Finished on whichever thread
        // finished loading plugins, so the summary must never read [ThreadStatic] state.
        private static int _classInitCalls;
        private static int _classInitMisses;
        private static int _hashHits;
        private static int _hashTotal;
        private static long _accelTicks;

        private static ManualLogSource AccelLog
        {
            get { return _log ?? (_log = Logger.CreateLogSource("BootAccel")); }
        }

        internal static void Warn(string message)
        {
            try { AccelLog.LogWarning(message); }
            catch { }
        }

        public override void Initialize()
        {
            try
            {
                string cacheDir = null;
                try { cacheDir = BepInEx.Paths.CachePath; }
                catch { }

                if (cacheDir != null && File.Exists(Path.Combine(cacheDir, DisableFileName)))
                {
                    AccelLog.LogInfo(DisableFileName + " present; boot proceeds unaccelerated");
                    return;
                }

                if (cacheDir != null)
                {
                    AccelCache.Load(Path.Combine(cacheDir, CacheFileName));
                    // One line per boot so a miss can be told apart from an unreadable cache.
                    AccelLog.LogInfo("cache: sigs=" + AccelCache.Sigs.Count + " hashes=" + AccelCache.Hashes.Count
                        + " gameAssembly=" + AccelCache.GameAssemblySize + "/" + AccelCache.GameAssemblyMtimeTicks);
                }

                if (!InstallAll(new Harmony(PatcherGuid)))
                {
                    // Nothing got patched, so no accelerated call will ever arrive to hook the
                    // chainloader. Report here so the boot still produces exactly one summary.
                    EmitSummary();
                }
            }
            catch (Exception ex)
            {
                Warn("initialize failed, boot proceeds unaccelerated: " + ex);
            }
        }

        // Last preloader-stage checkpoint. Plugin discovery (and with it the plugin hashing)
        // happens after this, so this only commits whatever is still dirty by now; the hashes
        // are committed from the chainloader's Finished event instead.
        public override void Finalizer()
        {
            try { AccelCache.Save(); }
            catch { }
        }

        // Returns true when at least one target was patched.
        private static bool InstallAll(Harmony harmony)
        {
            bool any = false;

            if (ClassInitAccel.Resolve())
            {
                any |= Patch(harmony, ClassInitAccel.TargetFindSignature, typeof(ClassInitAccel),
                    "FindSignatureInModule_Prefix", "FindSignatureInModule_Postfix");

                if (ClassInitAccel.TargetSetup != null)
                    Patch(harmony, ClassInitAccel.TargetSetup, typeof(ClassInitAccel), null, "Setup_Postfix");
            }

            if (HashAccel.Resolve())
                any |= Patch(harmony, HashAccel.Target, typeof(HashAccel), "HashStream_Prefix", "HashStream_Postfix");

            return any;
        }

        private static bool Patch(Harmony harmony, MethodBase target, Type owner, string prefix, string postfix)
        {
            try
            {
                harmony.Patch(target,
                    prefix == null ? null : new HarmonyMethod(AccessTools.Method(owner, prefix)),
                    postfix == null ? null : new HarmonyMethod(AccessTools.Method(owner, postfix)));
                return true;
            }
            catch (Exception ex)
            {
                // A failed patch means that operation stays at full cost; it must never stop the boot.
                Warn("patch failed for " + (target != null ? target.Name : "<null>") + ": " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        // ---- summary ----

        // The one line per boot is emitted from the chainloader's Finished event: the plugin
        // hashes run long after the signature scan, so any earlier point would report half the
        // boot. Instance does not exist yet when this patcher initialises, hence the lazy
        // subscribe from the first accelerated call.
        internal static void EnsureSummaryScheduled()
        {
            if (Interlocked.CompareExchange(ref _subscribed, 1, 0) != 0) return;
            try
            {
                IL2CPPChainloader chainloader = IL2CPPChainloader.Instance;
                if (chainloader == null)
                {
                    // Too early; a later call retries.
                    Interlocked.Exchange(ref _subscribed, 0);
                    return;
                }

                chainloader.Finished += EmitSummary;
            }
            catch (Exception ex)
            {
                // Left marked as subscribed so a permanently broken hook warns once, not per call.
                Warn("summary hook failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void EmitSummary()
        {
            if (Interlocked.Exchange(ref _summaryEmitted, 1) != 0) return;
            try
            {
                // took = wall time spent inside the accelerated methods, cached and uncached
                // alike, so a miss boot and a hit boot are directly comparable.
                double ms = Interlocked.Read(ref _accelTicks) * 1000.0 / Stopwatch.Frequency;
                AccelLog.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "classinit={0} hash={1}/{2} took={3}ms",
                    _classInitCalls > 0 && _classInitMisses == 0 ? "hit" : "miss",
                    _hashHits, _hashTotal,
                    ms.ToString("F1", CultureInfo.InvariantCulture)));
            }
            catch { }

            // Everything this patcher can learn in a boot is known by now, including the plugin
            // hashes, which are produced after the preloader's last checkpoint. The summary is
            // logged first so a failed write cannot swallow it.
            try { AccelCache.Save(); }
            catch { }
        }

        internal static void CountClassInit(bool served)
        {
            Interlocked.Increment(ref _classInitCalls);
            if (!served) Interlocked.Increment(ref _classInitMisses);
        }

        internal static void CountHash(bool served)
        {
            Interlocked.Increment(ref _hashTotal);
            if (served) Interlocked.Increment(ref _hashHits);
        }

        internal static void AddElapsed(long startTimestamp)
        {
            if (startTimestamp == 0) return;
            Interlocked.Add(ref _accelTicks, Stopwatch.GetTimestamp() - startTimestamp);
        }
    }
}
