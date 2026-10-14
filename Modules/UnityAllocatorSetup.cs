using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx;

namespace EndKnot.Modules;

// Unity プレイヤーのネイティブアロケータ設定 (boot.config の memorysetup-*) を上書きするファイルを用意する。
// 同梱の doorstop_config.ini が `boot_config_override=BepInEx\config\EndKnot.boot.config` を指しており、
// Doorstop は起動時にゲームの boot.config の代わりにこのファイルを Unity へ読ませる (ファイルが無ければ素通り)。
// 読まれるのは起動の最初だけなので、ここで書いた内容が効くのは次回起動から。
//
// 上書きは「置換」なので、ゲームの boot.config を毎回写してから鍵を足す (ゲーム更新で本編の行が変わっても追従する)。
//
// 足す鍵 (Unity 2022.3 プレイヤーの既定値と比べて):
//   - bucket allocator (16〜128B の小物用) は既定 4MB×1 ブロックで足りず、溢れた小物が汎用ヒープへ落ちて断片化する。
//     上限を 8 ブロックにすると実使用は 3 ブロック程度で収まる。
//   - 汎用ヒープ (main / thread) のブロックを 16MB → 4MB にして、空いたブロックを手放しやすくする。
// 同じロビーで試合を重ねた時のメモリの積み上がりが小さくなる (1 試合目は変わらない)。
public static class UnityAllocatorSetup
{
    public const string OverrideFileName = "EndKnot.boot.config";
    private const string IniValue = @"BepInEx\config\" + OverrideFileName;

    private static readonly (string Key, string Value)[] Settings =
    [
        ("memorysetup-bucket-allocator-block-count", "8"),
        ("memorysetup-main-allocator-block-size", "4194304"),
        ("memorysetup-thread-allocator-block-size", "4194304")
    ];

    private static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("EndKnot.UnityAllocator");

    private static readonly Regex OverrideLine = new(@"^[ \t]*boot_config_override[ \t]*=([^\r\n]*)", RegexOptions.Multiline);

    public static void Run()
    {
        try
        {
            string overridePath = Path.Combine(Paths.ConfigPath, OverrideFileName);
            string gameBootConfig = Path.Combine(Paths.GameRootPath, $"{Paths.ProcessName}_Data", "boot.config");

            if (!Main.UnityAllocatorTuning.Value || !File.Exists(gameBootConfig))
            {
                if (File.Exists(overridePath))
                {
                    File.Delete(overridePath);
                    Log.LogInfo("UNITYALLOC: override removed (takes effect from the next launch)");
                }

                return;
            }

            string content = BuildOverride(File.ReadAllText(gameBootConfig));
            string current = File.Exists(overridePath) ? File.ReadAllText(overridePath) : null;

            if (current != content)
            {
                // 書き込み途中で終了しても半端な内容を Unity へ渡さないよう、別名で書いてから置き換える
                string tmpPath = overridePath + ".tmp";
                File.WriteAllText(tmpPath, content);
                File.Move(tmpPath, overridePath, true);
                Log.LogInfo($"UNITYALLOC: override {(current == null ? "created" : "updated")} (takes effect from the next launch)");
            }

            EnsureDoorstopPointsHere();
        }
        catch (Exception e)
        {
            Log.LogWarning($"UNITYALLOC: failed ({e.GetType().Name}: {e.Message})");
        }
    }

    // ゲームの行を写し、こちらで設定する鍵の行だけ差し替える (既に写し済みの物を読んでも結果は同じ)。
    private static string BuildOverride(string gameBootConfig)
    {
        var lines = gameBootConfig.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => l.Length > 0 && !Settings.Any(s => l.StartsWith(s.Key + "=", StringComparison.Ordinal)))
            .ToList();

        lines.AddRange(Settings.Select(s => $"{s.Key}={s.Value}"));
        return string.Join("\n", lines) + "\n";
    }

    // doorstop_config.ini は配布 zip の物なので、DLL だけ更新された環境では行が空のまま残る。
    // 空の時だけこのファイルを指すように書き足す (利用者が別の値を入れていれば触らない)。効くのは次回起動から。
    private static void EnsureDoorstopPointsHere()
    {
        string ini = Path.Combine(Paths.GameRootPath, "doorstop_config.ini");
        if (!File.Exists(ini)) return;

        string text = File.ReadAllText(ini);
        Match m = OverrideLine.Match(text);
        if (!m.Success)
        {
            Log.LogWarning("UNITYALLOC: doorstop_config.ini has no boot_config_override line, so the allocator settings are not applied");
            return;
        }

        Group valueGroup = m.Groups[1];
        string value = valueGroup.Value.Trim();
        if (value.Length > 0)
        {
            string target = Path.GetFullPath(Path.Combine(Paths.GameRootPath, value));
            if (!string.Equals(target, Path.GetFullPath(Path.Combine(Paths.ConfigPath, OverrideFileName)), StringComparison.OrdinalIgnoreCase))
                Log.LogWarning($"UNITYALLOC: doorstop_config.ini points boot_config_override at {value}, so the allocator settings are not applied");
            return;
        }

        string tmpPath = ini + ".tmp";
        File.WriteAllText(tmpPath, text[..valueGroup.Index] + IniValue + text[(valueGroup.Index + valueGroup.Length)..]);
        File.Move(tmpPath, ini, true);
        Log.LogInfo("UNITYALLOC: doorstop_config.ini now hands the override to Unity (takes effect from the next launch)");
    }
}
