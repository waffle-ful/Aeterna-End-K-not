using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace EndKnot;

/// <summary>
/// Windows の CPU Sets API で、このプロセスの既定コア集合を「同じ最終キャッシュを共有するコア群」
/// (または効率クラス最高のコア群) に絞る実験機能 (2026-09-03)。
/// キャッシュが2つ以上に分かれた CPU (Zen 系の複数 CCX/CCD、Intel P/E 混在) で、
/// スレッドがキャッシュ境界を跨いで移動するコストを減らすのが狙い。
/// 単一キャッシュドメインの CPU では何もしない。管理者権限不要・プロセス終了で自然に解除。
/// </summary>
public static class CpuSetsOptimizer
{
    private const int ErrorInsufficientBuffer = 122;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(IntPtr information, uint bufferLength, out uint returnedLength, IntPtr process, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessDefaultCpuSets(IntPtr process, uint[] cpuSetIds, uint cpuSetIdCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessDefaultCpuSets(IntPtr process, uint[] cpuSetIds, uint cpuSetIdCount, out uint requiredIdCount);

    public readonly struct CpuSetEntry
    {
        public readonly uint Id;
        public readonly byte LogicalProcessorIndex;
        public readonly byte CoreIndex;
        public readonly byte LastLevelCacheIndex;
        public readonly byte NumaNodeIndex;
        public readonly byte EfficiencyClass;

        public CpuSetEntry(uint id, byte lp, byte core, byte llc, byte numa, byte eff)
        {
            Id = id;
            LogicalProcessorIndex = lp;
            CoreIndex = core;
            LastLevelCacheIndex = llc;
            NumaNodeIndex = numa;
            EfficiencyClass = eff;
        }
    }

    public static string CurrentMode { get; private set; } = "Off";

    /// <summary>設定文字列 (Off / Auto / Cache:N) を適用し、結果を1行で返す。</summary>
    public static string Apply(string mode)
    {
        mode = (mode ?? "Off").Trim();

        if (!OperatingSystem.IsWindows()) return "SKIP not windows";

        try
        {
            List<CpuSetEntry> sets = Enumerate();
            if (sets.Count == 0) return "SKIP no cpu set info";

            if (mode.Equals("Off", StringComparison.OrdinalIgnoreCase))
            {
                Clear();
                CurrentMode = "Off";
                return $"OFF cleared ({Describe(sets)})";
            }

            List<CpuSetEntry> chosen;
            string reason;

            if (mode.StartsWith("Cache:", StringComparison.OrdinalIgnoreCase))
            {
                if (!byte.TryParse(mode[6..], out byte llc)) return $"ERR bad mode '{mode}'";
                chosen = sets.Where(s => s.LastLevelCacheIndex == llc).ToList();
                reason = $"manual llc={llc}";
                if (chosen.Count == 0) return $"ERR llc {llc} not found ({Describe(sets)})";
            }
            else if (mode.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                (chosen, reason) = ChooseAuto(sets);
                if (chosen == null)
                {
                    Clear();
                    CurrentMode = "Off";
                    return $"SKIP {reason} ({Describe(sets)})";
                }
            }
            else return $"ERR unknown mode '{mode}' (Off / Auto / Cache:N)";

            // 2〜3 論理コアに閉じ込めると Unity + BepInEx + 常駐スレッドで逆に詰まる。
            if (chosen.Count < 4)
            {
                Clear();
                CurrentMode = "Off";
                return $"SKIP chosen set too small ({chosen.Count} < 4; {reason}; {Describe(sets)})";
            }

            uint[] ids = chosen.Select(s => s.Id).ToArray();

            if (!SetProcessDefaultCpuSets(GetCurrentProcess(), ids, (uint)ids.Length))
                return $"ERR SetProcessDefaultCpuSets failed (win32={Marshal.GetLastWin32Error()})";

            // 戻り値 true だけでは信用せず読み返す。
            uint[] readback = ReadBack();
            CurrentMode = mode;
            return $"ON {reason} cpus={chosen.Count}/{sets.Count} ids=[{string.Join(",", readback)}] ({Describe(sets)})";
        }
        catch (EntryPointNotFoundException) { return "SKIP cpu sets api unavailable (needs Windows 10 1709+)"; }
        catch (Exception e)
        {
            Logger.Error($"CpuSets apply failed\n{e}", "CpuSets", false);
            return $"ERR {e.GetType().Name}: {e.Message}";
        }
    }

    /// <summary>トポロジーの人間向け要約 (ログ・bridge の show 用)。</summary>
    public static string Show()
    {
        if (!OperatingSystem.IsWindows()) return "not windows";

        try
        {
            List<CpuSetEntry> sets = Enumerate();
            if (sets.Count == 0) return "no cpu set info";
            StringBuilder sb = new();
            sb.Append($"mode={CurrentMode} default=[{string.Join(",", ReadBack())}] ");
            sb.Append(Describe(sets));

            foreach (var g in sets.GroupBy(s => s.LastLevelCacheIndex).OrderBy(g => g.Key))
                sb.Append($" | llc{g.Key}: ids={string.Join(",", g.Select(s => s.Id))} eff={string.Join(",", g.Select(s => s.EfficiencyClass).Distinct())}");

            return sb.ToString();
        }
        catch (Exception e) { return $"ERR {e.GetType().Name}: {e.Message}"; }
    }

    private static (List<CpuSetEntry>, string) ChooseAuto(List<CpuSetEntry> sets)
    {
        // 1. 効率クラスが混在 (Intel P/E) → 最高クラスのコアだけ。
        byte maxEff = sets.Max(s => s.EfficiencyClass);
        if (sets.Any(s => s.EfficiencyClass != maxEff))
            return (sets.Where(s => s.EfficiencyClass == maxEff).ToList(), $"efficiency class {maxEff}");

        // 2. 最終キャッシュが複数 (Zen の CCX/CCD) → 論理コア数が最大のドメイン、同数なら若い番号。
        var domains = sets.GroupBy(s => s.LastLevelCacheIndex).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).ToList();
        if (domains.Count > 1)
            return (domains[0].ToList(), $"largest cache domain llc={domains[0].Key}");

        // 3. 単一ドメイン → 何もしない。
        return (null, "single cache domain, nothing to steer");
    }

    private static string Describe(List<CpuSetEntry> sets)
    {
        int llc = sets.Select(s => s.LastLevelCacheIndex).Distinct().Count();
        int eff = sets.Select(s => s.EfficiencyClass).Distinct().Count();
        int numa = sets.Select(s => s.NumaNodeIndex).Distinct().Count();
        string llcIds = string.Join(",", sets.Select(s => s.LastLevelCacheIndex).Distinct().OrderBy(x => x));
        return $"lp={sets.Count} cores={sets.Select(s => s.CoreIndex).Distinct().Count()} llcDomains={llc} llc=[{llcIds}] effClasses={eff} numa={numa}";
    }

    private static void Clear()
    {
        if (!SetProcessDefaultCpuSets(GetCurrentProcess(), null, 0))
            Logger.Warn($"clear failed (win32={Marshal.GetLastWin32Error()})", "CpuSets");
    }

    private static uint[] ReadBack()
    {
        IntPtr proc = GetCurrentProcess();
        GetProcessDefaultCpuSets(proc, null, 0, out uint required);
        if (required == 0) return Array.Empty<uint>();
        var ids = new uint[required];
        if (!GetProcessDefaultCpuSets(proc, ids, required, out required)) return Array.Empty<uint>();
        return ids.Take((int)required).ToArray();
    }

    private static List<CpuSetEntry> Enumerate()
    {
        var result = new List<CpuSetEntry>();

        // 1回目は必要サイズ問い合わせ (ERROR_INSUFFICIENT_BUFFER が正常)。
        GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint needed, IntPtr.Zero, 0);
        int err = Marshal.GetLastWin32Error();
        if (needed == 0 || err != ErrorInsufficientBuffer) return result;

        IntPtr buf = Marshal.AllocHGlobal((int)needed);

        try
        {
            if (!GetSystemCpuSetInformation(buf, needed, out uint returned, IntPtr.Zero, 0)) return result;

            // SYSTEM_CPU_SET_INFORMATION: Size u32@0, Type u32@4 (0=CpuSet), Id u32@8, Group u16@12,
            // LogicalProcessorIndex u8@14, CoreIndex u8@15, LastLevelCacheIndex u8@16,
            // NumaNodeIndex u8@17, EfficiencyClass u8@18。union を含むので生バイトで読み Size 分進める。
            var offset = 0;

            while (offset + 20 <= returned)
            {
                int size = Marshal.ReadInt32(buf, offset);
                if (size <= 0) break;
                int type = Marshal.ReadInt32(buf, offset + 4);

                if (type == 0)
                {
                    uint id = (uint)Marshal.ReadInt32(buf, offset + 8);
                    byte lp = Marshal.ReadByte(buf, offset + 14);
                    byte core = Marshal.ReadByte(buf, offset + 15);
                    byte llc = Marshal.ReadByte(buf, offset + 16);
                    byte numa = Marshal.ReadByte(buf, offset + 17);
                    byte eff = Marshal.ReadByte(buf, offset + 18);
                    result.Add(new(id, lp, core, llc, numa, eff));
                }

                offset += size;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }

        return result;
    }
}
