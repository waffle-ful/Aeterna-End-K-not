using System;
using System.IO;
using System.Text.RegularExpressions;
using BepInEx;

namespace EndKnot.Modules;

// ゲーム側 (il2cpp) と mod 側 (CoreCLR) の GC ヒープに持たせる余裕を減らす。どちらも起動時に環境変数からしか
// 読まれないので、Doorstop の [Environment] に書いて次回起動から効かせる。
//   - GC_FREE_SPACE_DIVISOR=6: Boehm GC は回収後の空きがヒープの 1/N を下回るとヒープを広げ、広げた分は Windows では
//     OS へ返さない。既定の N=3 だと生きている物が約 42MB でもヒープは 66MB まで広がる。6 で試合中のヒープが約 10MB
//     小さくなり、回収は 1 分あたり 6 回から 8 回になる (実測)。
//   - DOTNET_GCgen0MaxBudget=0x400000: CoreCLR は CPU の L3 の大きさから gen0 の割り当て枠を決め、L3 が大きい CPU では
//     次の GC までに数十 MB のゴミ置き場を確保したまま常駐させる。上限 4MB で常駐が 6〜8MB 減り、gen0 GC は
//     5 秒あたり 1.2 回から 1.9 回になる (実測)。
public static class GcHeapSetup
{
    private static readonly (string Key, string Value)[] Settings =
    [
        ("GC_FREE_SPACE_DIVISOR", "6"),
        ("DOTNET_GCgen0MaxBudget", "0x400000")
    ];

    private static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("EndKnot.GcHeap");

    private static readonly Regex EnvironmentHeader = new(@"^[ \t]*\[Environment\][ \t]*(\r?\n|$)", RegexOptions.Multiline);

    public static void Run()
    {
        try
        {
            string ini = Path.Combine(Paths.GameRootPath, "doorstop_config.ini");
            if (!File.Exists(ini)) return;

            string text = File.ReadAllText(ini);
            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            bool enabled = Main.CompactGcHeaps.Value;
            string updated = text;

            foreach ((string key, string value) in Settings)
            {
                Match line = new Regex(@"^[ \t]*" + key + @"[ \t]*=([^\r\n]*)(\r?\n)?", RegexOptions.Multiline).Match(updated);

                if (enabled)
                {
                    // 利用者が自分で値を入れていればそのまま使う
                    if (line.Success) continue;

                    Match header = EnvironmentHeader.Match(updated);
                    if (header.Success)
                    {
                        int at = header.Index + header.Length;
                        string lead = header.Groups[1].Value.Length == 0 ? nl : "";
                        updated = updated[..at] + lead + $"{key}={value}{nl}" + updated[at..];
                    }
                    else
                        updated = updated + (updated.EndsWith('\n') ? "" : nl) + $"[Environment]{nl}{key}={value}{nl}";
                }
                else if (line.Success && line.Groups[1].Value.Trim() == value)
                    updated = updated.Remove(line.Index, line.Length);
            }

            if (updated == text) return;

            // 書き込み途中で終了しても半端な ini を Doorstop へ渡さないよう、別名で書いてから置き換える
            string tmpPath = ini + ".tmp";
            File.WriteAllText(tmpPath, updated);
            File.Move(tmpPath, ini, true);
            Log.LogInfo($"GCHEAP: GC settings {(enabled ? "added to" : "removed from")} doorstop_config.ini (takes effect from the next launch)");
        }
        catch (Exception e)
        {
            Log.LogWarning($"GCHEAP: failed ({e.GetType().Name}: {e.Message})");
        }
    }
}
