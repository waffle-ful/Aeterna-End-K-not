using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace EndKnot.Modules;

// https://github.com/tukasa0001/TownOfHost/blob/main/Modules/OptionSaver.cs
public static class OptionSaver
{
    private static readonly DirectoryInfo SaveDataDirectoryInfo = new($"{Main.DataPath}/EndKnot_DATA/SaveData/");
    private static readonly FileInfo OptionSaverFileInfo = new($"{SaveDataDirectoryInfo.FullName}/Options.json");
    private static readonly LogHandler Logger = EndKnot.Logger.Handler(nameof(OptionSaver));
    private static readonly FileInfo DefaultPresetFileInfo = new($"{SaveDataDirectoryInfo.FullName}/DefaultPreset.txt");
    private static int DefaultPresetNumber;

    // 設定は 6 千項目超で 1.5MB 前後の JSON になり、項目を 1 つ変えるたびに書き直す。文字列を経由すると
    // その倍の大きさの文字列と、UTF-8 への変換で ArrayPool から借りた MB 級の作業配列 (スレッドに残り続ける) が出るので、
    // UTF-8 のまま組み立てて書く。オプションの型情報は使い回す
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public static readonly int Version;

    public static int GetDefaultPresetNumber()
    {
        if (DefaultPresetFileInfo.Exists)
        {
            string presetNmber = File.ReadAllText(DefaultPresetFileInfo.FullName);
            if (int.TryParse(presetNmber, out int number) && number is >= 0 and <= OptionItem.NumPresets - 1) return number;
        }

        return 0;
    }

    public static void Initialize()
    {
        if (!SaveDataDirectoryInfo.Exists)
        {
            SaveDataDirectoryInfo.Create();
            SaveDataDirectoryInfo.Attributes |= FileAttributes.Hidden;
        }

        if (!OptionSaverFileInfo.Exists) OptionSaverFileInfo.Create().Dispose();
        if (!DefaultPresetFileInfo.Exists) DefaultPresetFileInfo.Create().Dispose();
    }

    private static SerializableOptionsData GenerateOptionsData()
    {
        Dictionary<int, int> singleOptions = [];
        Dictionary<int, int[]> presetOptions = [];

        foreach (OptionItem option in OptionItem.AllOptions)
        {
            if (option.IsSingleValue)
            {
                if (!singleOptions.TryAdd(option.Id, option.SingleValue))
                    EndKnot.Logger.Warn($"Duplicate SingleOption ID: {option.Id}", "Options Load");
            }
            else if (!presetOptions.TryAdd(option.Id, option.AllValues))
                EndKnot.Logger.Warn($"Duplicate preset option ID: {option.Id}", "Options Load");
        }

        DefaultPresetNumber = singleOptions[0];

        return new()
        {
            Version = Version,
            SingleOptions = singleOptions,
            PresetOptions = presetOptions
        };
    }

    private static void LoadOptionsData(SerializableOptionsData serializableOptionsData)
    {
        if (serializableOptionsData.Version != Version)
        {
            Logger.Info($"Loaded option version {serializableOptionsData.Version} does not match current version {Version}, overwriting with default value");
            Save();
            return;
        }

        Dictionary<int, int> singleOptions = serializableOptionsData.SingleOptions;
        Dictionary<int, int[]> presetOptions = serializableOptionsData.PresetOptions;

        foreach ((int id, int value) in singleOptions)
        {
            if (OptionItem.FastOptions.TryGetValue(id, out OptionItem optionItem))
                optionItem.SetValue(value, doSave: false);
        }

        foreach ((int id, int[] values) in presetOptions)
        {
            if (OptionItem.FastOptions.TryGetValue(id, out OptionItem optionItem))
                optionItem.SetAllValues(values);
        }
    }

    public static void Save()
    {
        if (AmongUsClient.Instance && !AmongUsClient.Instance.AmHost) return;

        // 全部組み立て終えてから書く (途中で失敗しても前の保存を壊さない)
        var buffer = new ArrayBufferWriter<byte>(256 * 1024);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            JsonSerializer.Serialize(writer, GenerateOptionsData(), SerializerOptions);

        using (var fs = new FileStream(OptionSaverFileInfo.FullName, FileMode.Create, FileAccess.Write, FileShare.Read))
            fs.Write(buffer.WrittenSpan);
        File.WriteAllText(DefaultPresetFileInfo.FullName, DefaultPresetNumber.ToString());
    }

    public static void Load()
    {
        byte[] data = File.ReadAllBytes(OptionSaverFileInfo.FullName);
        ReadOnlySpan<byte> json = data;
        if (json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF) json = json[3..]; // バイト列からの逆シリアル化は BOM を読み飛ばさない

        if (json.Length <= 0)
        {
            Logger.Info("Save default value as option data is empty");
            Save();
            return;
        }

        LoadOptionsData(JsonSerializer.Deserialize<SerializableOptionsData>(json, SerializerOptions));
    }

    public class SerializableOptionsData
    {
        public int Version { get; init; }
        public Dictionary<int, int> SingleOptions { get; init; }
        public Dictionary<int, int[]> PresetOptions { get; init; }
    }
}