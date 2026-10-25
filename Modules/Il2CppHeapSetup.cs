using System;
using System.IO;
using System.Text.RegularExpressions;
using BepInEx;

namespace EndKnot.Modules;

// ゲーム側 (il2cpp) の GC ヒープに持たせる空き余裕を減らす。Boehm GC は回収後の空きがヒープの 1/N を下回ると
// ヒープを広げ、広げた分は Windows では OS へ返さない。N の既定は 3 で、生きている物が約 42MB のロビーでもヒープは
// 66MB まで広がる。N=6 にすると試合中のヒープが約 10MB 小さくなり、回収は 1 分あたり 6 回から 8 回になる (実測)。
// N は Boehm の初期化時に環境変数からしか読まれないので、Doorstop の [Environment] に書いて次回起動から効かせる。
public static class Il2CppHeapSetup
{
    private const string Key = "GC_FREE_SPACE_DIVISOR";
    private const string Value = "6";

    private static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("EndKnot.Il2CppHeap");

    private static readonly Regex KeyLine = new(@"^[ \t]*" + Key + @"[ \t]*=([^\r\n]*)(\r?\n)?", RegexOptions.Multiline);
    private static readonly Regex EnvironmentHeader = new(@"^[ \t]*\[Environment\][ \t]*(\r?\n|$)", RegexOptions.Multiline);

    public static void Run()
    {
        try
        {
            string ini = Path.Combine(Paths.GameRootPath, "doorstop_config.ini");
            if (!File.Exists(ini)) return;

            string text = File.ReadAllText(ini);
            Match line = KeyLine.Match(text);
            string updated = null;

            if (Main.CompactIl2CppHeap.Value)
            {
                // 利用者が自分で値を入れていればそのまま使う
                if (line.Success) return;

                string nl = text.Contains("\r\n") ? "\r\n" : "\n";
                Match header = EnvironmentHeader.Match(text);
                if (header.Success)
                {
                    int at = header.Index + header.Length;
                    string lead = header.Groups[1].Value.Length == 0 ? nl : "";
                    updated = text[..at] + lead + $"{Key}={Value}{nl}" + text[at..];
                }
                else
                    updated = text + (text.EndsWith('\n') ? "" : nl) + $"[Environment]{nl}{Key}={Value}{nl}";
            }
            else if (line.Success && line.Groups[1].Value.Trim() == Value)
                updated = text.Remove(line.Index, line.Length);

            if (updated == null) return;

            // 書き込み途中で終了しても半端な ini を Doorstop へ渡さないよう、別名で書いてから置き換える
            string tmpPath = ini + ".tmp";
            File.WriteAllText(tmpPath, updated);
            File.Move(tmpPath, ini, true);
            Log.LogInfo($"IL2CPPHEAP: {Key} {(Main.CompactIl2CppHeap.Value ? "added to" : "removed from")} doorstop_config.ini (takes effect from the next launch)");
        }
        catch (Exception e)
        {
            Log.LogWarning($"IL2CPPHEAP: failed ({e.GetType().Name}: {e.Message})");
        }
    }
}
