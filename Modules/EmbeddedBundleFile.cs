using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using UnityEngine;

namespace EndKnot.Modules;

// 埋め込み AssetBundle を、DLL ファイルの中の位置から直接開く。
// LoadFromMemory はバンドル全体の写しをネイティブ側に持ち続けるが、ファイルから開けば必要な所だけ
// 読まれる。開けなかった時は null を返すので、呼び出し側は LoadFromMemory へ戻ること。
internal static class EmbeddedBundleFile
{
    private static readonly byte[] Signature = "UnityFS"u8.ToArray();

    private static bool _scanned;
    private static string _path;
    private static Dictionary<string, long> _offsets;

    internal static AssetBundle TryOpen(string resourceName)
    {
#if ANDROID
        return null;
#else
        try
        {
            if (!_scanned) Scan();
            if (_offsets == null || !_offsets.TryGetValue(resourceName, out long offset)) return null;
            if (!HasSignature(offset)) return null;

            return AssetBundle.LoadFromFile(_path, 0u, (ulong)offset);
        }
        catch (Exception e)
        {
            Logger.Warn($"{resourceName}: open from file failed ({e.GetType().Name}: {e.Message})", "EmbeddedBundleFile");
            return null;
        }
#endif
    }

#if !ANDROID
    // 埋め込みリソースは DLL の中に無圧縮で並んでいる。名前ごとの開始位置を 1 回だけ引いておく。
    private static void Scan()
    {
        _scanned = true;

        string path = Assembly.GetExecutingAssembly().Location;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using PEReader pe = new(fs);

        CorHeader cor = pe.PEHeaders.CorHeader;
        if (cor == null || !pe.PEHeaders.TryGetDirectoryOffset(cor.ResourcesDirectory, out int start)) return;

        MetadataReader md = pe.GetMetadataReader();
        Dictionary<string, long> offsets = new(StringComparer.Ordinal);

        foreach (ManifestResourceHandle handle in md.ManifestResources)
        {
            ManifestResource res = md.GetManifestResource(handle);
            if (!res.Implementation.IsNil) continue;

            string name = md.GetString(res.Name);
            if (!name.EndsWith(".bundle", StringComparison.Ordinal)) continue;

            // 各リソースの先頭 4 バイトは長さ
            offsets[name] = start + res.Offset + 4;
        }

        _path = path;
        _offsets = offsets;
    }

    private static bool HasSignature(long offset)
    {
        using FileStream fs = new(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(offset, SeekOrigin.Begin);

        Span<byte> head = stackalloc byte[Signature.Length];
        return fs.Read(head) == head.Length && head.SequenceEqual(Signature);
    }
#endif
}
