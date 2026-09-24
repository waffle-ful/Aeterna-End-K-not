using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace EndKnot.Modules.Media;

// 連番 JPG を 1 ファイルに束ねたフレームパック (.ekfp)。tools/make-framepack.ps1 が作る。
// 形式 (little endian): "EKFP" / u16 version / u16 flags (bit0 = 透明度あり) / u16 width / u16 height /
// u16 fps x100 / u16 reserved / u32 frameCount / u32 length[frameCount] / JPG 本体の連結。
// 透明度ありの JPG は 1 枚が縦 2 段 (上半分 = 色・下半分 = 透明度のグレースケール)。
public sealed class FramePack
{
    private const uint Magic = 0x5046_4B45; // "EKFP"
    private const int MaxFrames = 10_000;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public float Fps { get; private set; }
    public bool StackedAlpha { get; private set; }
    public int FrameCount => _frames.Length;
    public long TotalBytes { get; private set; }

    // JPG をあらかじめ il2cpp 配列にしておく。再生のたびに byte[] から詰め替えると、
    // 1 コマごとに il2cpp 側へ数十 KB の配列が生まれて GC を叩く。
    private Il2CppStructArray<byte>[] _frames = [];

    private static readonly Dictionary<string, FramePack> Cache = new(StringComparer.OrdinalIgnoreCase);

    public Il2CppStructArray<byte> GetFrame(int index) => _frames[index];

    /// <summary>
    /// ① BepInEx/plugins/EndKnot/Media/&lt;file&gt; (絶対パスならそのまま) → ② DLL 埋込リソース、の順で探して読む。
    /// 一度読んだものは使い回す (メニューの炎・ローディング画面は何度も出入りする)。見つからなければ null。
    /// </summary>
    public static FramePack Get(string file)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        if (Cache.TryGetValue(file, out FramePack cached)) return cached;

        try
        {
            byte[] data = ReadSource(file);
            if (data == null) return null;

            FramePack pack = Parse(data, file);
            if (pack == null) return null;

            Cache[file] = pack;
            Logger.Info($"frame pack loaded: {file} {pack.Width}x{pack.Height} {pack.FrameCount} frames @ {pack.Fps:0.##}fps alpha={pack.StackedAlpha} ({pack.TotalBytes / 1024}KB)", "FramePack");
            return pack;
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            return null;
        }
    }

    private static byte[] ReadSource(string file)
    {
        string path = Path.IsPathRooted(file) ? file : Path.Combine(BepInEx.Paths.PluginPath, "EndKnot", "Media", file);
        if (File.Exists(path)) return File.ReadAllBytes(path);
        if (Path.IsPathRooted(file)) return null;

        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"EndKnot.Resources.Media.{file}");
        if (stream == null) return null;

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static FramePack Parse(byte[] data, string label)
    {
        if (data.Length < 20 || BitConverter.ToUInt32(data, 0) != Magic)
        {
            Logger.Warn($"{label}: not a frame pack", "FramePack");
            return null;
        }

        int version = BitConverter.ToUInt16(data, 4);
        if (version != 1)
        {
            Logger.Warn($"{label}: unsupported frame pack version {version}", "FramePack");
            return null;
        }

        int flags = BitConverter.ToUInt16(data, 6);
        int width = BitConverter.ToUInt16(data, 8);
        int height = BitConverter.ToUInt16(data, 10);
        float fps = BitConverter.ToUInt16(data, 12) / 100f;
        long count = BitConverter.ToUInt32(data, 16);

        if (width <= 0 || height <= 0 || fps <= 0f || count <= 0 || count > MaxFrames || 20 + count * 4 > data.Length)
        {
            Logger.Warn($"{label}: broken frame pack header ({width}x{height} {fps}fps {count} frames)", "FramePack");
            return null;
        }

        var frames = new Il2CppStructArray<byte>[count];
        long offset = 20 + count * 4;

        for (int i = 0; i < count; i++)
        {
            int length = (int)BitConverter.ToUInt32(data, 20 + i * 4);
            if (length <= 0 || offset + length > data.Length)
            {
                Logger.Warn($"{label}: frame {i} runs past the end of the file", "FramePack");
                return null;
            }

            // (long) は必須: Android の Il2CppInterop では int を渡すとポインタ受けのコンストラクタに解決される。
            var array = new Il2CppStructArray<byte>((long)length);
            // il2cpp 配列の本体はヘッダ (ポインタ 4 つ分) の直後から始まる。インデクサで 1 バイトずつ書くと遅い。
            Marshal.Copy(data, (int)offset, IntPtr.Add(array.Pointer, IntPtr.Size * 4), length);
            frames[i] = array;
            offset += length;
        }

        return new FramePack
        {
            Width = width,
            Height = height,
            Fps = fps,
            StackedAlpha = (flags & 1) != 0,
            TotalBytes = data.Length,
            _frames = frames
        };
    }
}
