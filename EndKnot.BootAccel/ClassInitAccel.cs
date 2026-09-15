using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace EndKnot.BootAccel
{
    // Seam: Il2CppInterop.Runtime.MemoryUtils.FindSignatureInModule(ProcessModule, SignatureDefinition) -> nint
    //
    // This is the brute-force byte scan itself: a one-byte-at-a-time walk over every committed,
    // readable region of the GameAssembly image (~57 MB) per signature. Both Class::Init
    // signatures miss on this build, so the module is scanned end to end twice before BepInEx
    // falls back to the mono_class_instance_size export.
    //
    // The scan result is cached per signature, including a miss (found=false). Replaying a
    // cached zero makes InjectorHelpers.FindClassInit run its own substitute lookup exactly as
    // it would have, so the export-fallback semantics are preserved by construction instead of
    // being re-implemented here. MemoryUtils and SignatureDefinition are internal types, hence
    // the object[] __args signature.
    internal static class ClassInitAccel
    {
        internal static MethodInfo TargetFindSignature;
        internal static MethodInfo TargetSetup;

        private static FieldInfo _fClassInit;
        private static FieldInfo _fIl2CppHandle;

        private static bool _gameAsmChecked;

        [ThreadStatic] private static string _key;
        [ThreadStatic] private static bool _skipped;
        [ThreadStatic] private static IntPtr _base;
        [ThreadStatic] private static long _t0;

        internal static bool Resolve()
        {
            Assembly asm = FindAssembly("Il2CppInterop.Runtime");
            if (asm == null) { BootAccelPatcher.Warn("Il2CppInterop.Runtime not loaded; Class::Init accel disabled"); return false; }

            Type tMem = asm.GetType("Il2CppInterop.Runtime.MemoryUtils", false);
            Type tInj = asm.GetType("Il2CppInterop.Runtime.Injection.InjectorHelpers", false);
            if (tMem == null || tInj == null) { BootAccelPatcher.Warn("MemoryUtils/InjectorHelpers type not found"); return false; }

            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            TargetFindSignature = tMem.GetMethod("FindSignatureInModule", All);
            TargetSetup = tInj.GetMethod("Setup", All, null, Type.EmptyTypes, null);
            _fClassInit = tInj.GetField("ClassInit", All);
            _fIl2CppHandle = tInj.GetField("Il2CppHandle", All);

            if (TargetFindSignature == null || TargetFindSignature.ReturnType != typeof(IntPtr) || TargetFindSignature.GetParameters().Length != 2)
            {
                BootAccelPatcher.Warn("FindSignatureInModule signature mismatch; Class::Init accel disabled");
                TargetFindSignature = null;
                return false;
            }

            return true;
        }

        private static Assembly FindAssembly(string name)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                if (string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase)) return a;
            return null;
        }

        // 32bit プロセスでは IntPtr.ToInt64() が符号拡張するため、2GB 超のアドレスが負値になる。
        // RVA は差分計算なので、引く前に必ず符号なしの線形アドレスへ正規化する。
        private static long Linear(IntPtr p)
        {
            return IntPtr.Size == 4 ? (long)(uint)unchecked((int)p.ToInt64()) : p.ToInt64();
        }

        // 逆変換。32bit で表現できない値は「キャッシュがこのプロセスに合わない」ので失敗を返し、
        // 呼び出し側は元のスキャンへ落ちる。
        private static bool TryPointer(long linear, out IntPtr p)
        {
            if (IntPtr.Size == 4)
            {
                if (linear < 0L || linear > 0xFFFFFFFFL) { p = IntPtr.Zero; return false; }
                p = new IntPtr(unchecked((int)(uint)linear));
                return true;
            }

            p = new IntPtr(linear);
            return true;
        }

        // Stable, ASCII-safe key for a SignatureDefinition. pattern/mask carry embedded NULs and
        // non-ASCII, so both are hex-encoded; fields are read reflectively to survive field churn.
        private static string SigKey(object sigDef)
        {
            if (sigDef == null) return null;

            var sb = new StringBuilder(96);
            FieldInfo[] fields = sigDef.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Array.Sort(fields, (a, b) => string.CompareOrdinal(a.Name, b.Name));

            foreach (FieldInfo f in fields)
            {
                object v = f.GetValue(sigDef);
                sb.Append(f.Name).Append('=');
                var s = v as string;
                if (s != null) { foreach (char c in s) sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); }
                else sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                sb.Append(';');
            }

            return sb.ToString();
        }

        public static bool FindSignatureInModule_Prefix(object[] __args, ref IntPtr __result)
        {
            _key = null;
            _skipped = false;
            _base = IntPtr.Zero;
            _t0 = Stopwatch.GetTimestamp();

            BootAccelPatcher.EnsureSummaryScheduled();

            try
            {
                var module = __args != null && __args.Length > 0 ? __args[0] as ProcessModule : null;
                if (module == null) return true;
                _base = module.BaseAddress;

                if (!_gameAsmChecked)
                {
                    _gameAsmChecked = true;
                    try
                    {
                        var fi = new FileInfo(module.FileName);
                        if (fi.Exists) AccelCache.EnsureGameAssembly(fi.Length, fi.LastWriteTimeUtc.Ticks);
                        else AccelCache.EnsureGameAssembly(-1, -1);
                    }
                    catch { AccelCache.EnsureGameAssembly(-1, -1); }
                }

                _key = SigKey(__args.Length > 1 ? __args[1] : null);
                if (_key == null) return true;

                SigEntry e;
                if (AccelCache.Sigs.TryGetValue(_key, out e))
                {
                    IntPtr cached = IntPtr.Zero;
                    if (e.Found && !TryPointer(Linear(_base) + e.Rva, out cached)) return true;

                    __result = e.Found ? cached : IntPtr.Zero;
                    _skipped = true;
                    return false;
                }
            }
            catch (Exception ex)
            {
                BootAccelPatcher.Warn("classinit prefix: " + ex.GetType().Name + ": " + ex.Message);
                _key = null;
                _skipped = false;
                return true;
            }

            return true;
        }

        public static void FindSignatureInModule_Postfix(IntPtr __result)
        {
            try
            {
                BootAccelPatcher.AddElapsed(_t0);
                BootAccelPatcher.CountClassInit(_skipped);
                if (_skipped || _key == null) return;

                AccelCache.Sigs[_key] = __result == IntPtr.Zero
                    ? new SigEntry { Found = false, Rva = 0 }
                    : new SigEntry { Found = true, Rva = Linear(__result) - Linear(_base) };
                AccelCache.MarkDirty();
            }
            catch (Exception ex)
            {
                BootAccelPatcher.Warn("classinit postfix: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Runs after every dobby hook and FindClassInit() inside InjectorHelpers.Setup().
        // Records what Class::Init actually resolved to, so the cache file carries a readable
        // account of the resolution the accelerated boot reproduced.
        public static void Setup_Postfix()
        {
            try
            {
                string kind = "unresolved";
                long rva = -1;
                IntPtr ptr = IntPtr.Zero;

                if (_fClassInit != null)
                {
                    var d = _fClassInit.GetValue(null) as Delegate;
                    if (d != null) ptr = Marshal.GetFunctionPointerForDelegate(d);
                }

                if (ptr != IntPtr.Zero)
                {
                    if (_base != IntPtr.Zero) rva = Linear(ptr) - Linear(_base);
                    kind = "sig";

                    IntPtr handle = _fIl2CppHandle != null ? (IntPtr)_fIl2CppHandle.GetValue(null) : IntPtr.Zero;
                    if (handle != IntPtr.Zero)
                    {
                        string[] subs = { "mono_class_instance_size", "mono_class_setup_vtable", "il2cpp_class_has_references" };
                        foreach (string s in subs)
                        {
                            IntPtr addr;
                            if (NativeLibrary.TryGetExport(handle, s, out addr) && addr == ptr) { kind = "export:" + s; break; }
                        }
                    }
                }

                // The resolution is identical on every boot of the same image, so recording it
                // counts as a change only when it actually differs; an accelerated boot must not
                // rewrite the file just to restate what it read from it.
                if (AccelCache.ResolvedKind != kind || AccelCache.ClassInitRva != rva)
                {
                    AccelCache.ResolvedKind = kind;
                    AccelCache.ClassInitRva = rva;
                    AccelCache.MarkDirty();
                }

                // Both signature scans are behind us here, and they are the most expensive thing
                // this patcher elides, so whatever they produced is committed to disk at this
                // point rather than riding on the rest of the boot.
                AccelCache.Save();
            }
            catch (Exception ex)
            {
                BootAccelPatcher.Warn("setup postfix: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
