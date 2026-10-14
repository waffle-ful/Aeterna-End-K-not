using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

namespace EndKnot.Modules;

// ゲームに同梱のコントローラー定義 (Rewired の HardwareJoystickMap 167 個) は、機種ごとの対応表を
// 全プラットフォーム分 (Linux / OSX / Android / WebGL / PS4 / Switch …) 抱えたまま常駐し、
// il2cpp ヒープ上でポインタを含む生存物 約 8.6MB を占める。il2cpp の GC 停止はこの量に比例するので
// (実測: 1MB ≒ 0.45ms)、Windows で読まれない機種の対応表への参照を外す。外すのは
// Platform_* 型のフィールドだけで、共通の要素定義と Windows の入力経路 (RawInput / DirectInput /
// XInput / WGI / SDL2 / 内部ドライバ / Windows の予備 / UWP / GameCore) は残す。
// 参照を外すだけなので書き込みバリアは要らないが、incremental GC が動いている構成では念のため触らない。
internal static class RewiredMapTrim
{
    private static readonly HashSet<string> KeepWindows =
    [
        "rawInput", "directInput", "xInput", "windowsWGI", "sdl2_Windows", "fallback_Windows", "internalDriver",
        "windowsUWP", "fallback_WindowsUWP", "gameCore"
    ];

    private static bool _done;

    internal static bool Enabled => OperatingSystem.IsWindows() && (Main.TrimForeignControllerMaps?.Value ?? false);

    internal static unsafe void RunOnce()
    {
        if (_done || !Enabled) return;

        try { if (GcUafProbe.IsIncrementalGc()) { _done = true; return; } }
        catch { _done = true; return; }

        try
        {
            IntPtr klass = IL2CPP.GetIl2CppClass("Rewired_Core.dll", "Rewired.Data.Mapping", "HardwareJoystickMap");
            if (klass == IntPtr.Zero) { _done = true; return; }

            var maps = UnityEngine.Resources.FindObjectsOfTypeAll(Il2CppType.TypeFromPointer(klass));
            if (maps.Length == 0) return; // 定義の読み込み前。次のメニュー到達で試し直す
            _done = true;
            var offsets = new List<int>();
            var names = new List<string>();

            for (IntPtr k = klass; k != IntPtr.Zero; k = IL2CPP.il2cpp_class_get_parent(k))
            {
                IntPtr iter = IntPtr.Zero, f;

                while ((f = IL2CPP.il2cpp_class_get_fields(k, ref iter)) != IntPtr.Zero)
                {
                    if ((IL2CPP.il2cpp_field_get_flags(f) & 0x10) != 0) continue; // static

                    IntPtr fk = IL2CPP.il2cpp_class_from_type(IL2CPP.il2cpp_field_get_type(f));
                    if (fk == IntPtr.Zero || IL2CPP.il2cpp_class_is_valuetype(fk)) continue;

                    string typeName = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(fk)) ?? "";
                    if (!typeName.StartsWith("Platform_", StringComparison.Ordinal)) continue;

                    string name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_field_get_name(f)) ?? "";
                    if (KeepWindows.Contains(name)) continue;

                    offsets.Add((int)IL2CPP.il2cpp_field_get_offset(f));
                    names.Add(name);
                }
            }

            int cleared = 0;

            foreach (UnityEngine.Object m in maps)
            {
                IntPtr obj = m.Pointer;
                if (obj == IntPtr.Zero) continue;

                foreach (int off in offsets)
                {
                    IntPtr* slot = (IntPtr*)((byte*)obj + off);
                    if (*slot == IntPtr.Zero) continue;
                    *slot = IntPtr.Zero;
                    cleared++;
                }
            }

            Logger.Info($"trimmed {cleared} platform tables from {maps.Length} controller maps ({string.Join(",", names)})", "RewiredMapTrim");
            HealthLog.Note($"RWTRIM maps={maps.Length} fields={offsets.Count} cleared={cleared}");
        }
        catch (Exception e)
        {
            _done = true;
            Utils.ThrowException(e);
        }
    }
}
