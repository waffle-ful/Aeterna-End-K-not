using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
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
    private static string _packPath;
    private static Dictionary<string, long> _packOffsets;

    internal static AssetBundle TryOpen(string resourceName)
    {
#if ANDROID
        return null;
#else
        try
        {
            if (!_scanned) Scan();
            if (!TryLocate(resourceName, out string path, out long offset)) return null;
            if (!HasSignature(path, offset)) return null;

            return AssetBundle.LoadFromFile(path, 0u, (ulong)offset);
        }
        catch (Exception e)
        {
            Logger.Warn($"{resourceName}: open from file failed ({e.GetType().Name}: {e.Message})", "EmbeddedBundleFile");
            return null;
        }
#endif
    }

    // TryOpen の非同期版。バイト列を il2cpp 側へ写さないので、ゲーム側 GC ヒープが一時的な大きな配列の分だけ広がることもない
    internal static AssetBundleCreateRequest TryOpenAsync(string resourceName)
    {
#if ANDROID
        return null;
#else
        try
        {
            if (!_scanned) Scan();
            if (!TryLocate(resourceName, out string path, out long offset)) return null;
            if (!HasSignature(path, offset)) return null;

            return AssetBundle.LoadFromFileAsync(path, 0u, (ulong)offset);
        }
        catch (Exception e)
        {
            Logger.Warn($"{resourceName}: async open from file failed ({e.GetType().Name}: {e.Message})", "EmbeddedBundleFile");
            return null;
        }
#endif
    }

#if !ANDROID
    // 埋め込みリソースは DLL の中に無圧縮で並んでいる。名前ごとの開始位置を 1 回だけ引いておく。
    // End K not 自身と、同梱 BGM の EndKnot.Music.dll (MusicPack) の両方を見る。
    private static void Scan()
    {
        _scanned = true;

        string path = Assembly.GetExecutingAssembly().Location;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            _offsets = ScanFile(path, out CorHeader cor, out long probeRel);
            if (_offsets != null)
            {
                _path = path;
                _probeRel = probeRel;
                _resRva = cor.ResourcesDirectory.RelativeVirtualAddress;
                _resSize = cor.ResourcesDirectory.Size;
            }
        }

        string packPath = MusicPack.PackPath;
        if (File.Exists(packPath))
        {
            _packOffsets = ScanFile(packPath, out _, out _);
            if (_packOffsets != null) _packPath = packPath;
        }
    }

    private static Dictionary<string, long> ScanFile(string path, out CorHeader cor, out long probeRel)
    {
        probeRel = -1;

        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using PEReader pe = new(fs);

        cor = pe.PEHeaders.CorHeader;
        if (cor == null || !pe.PEHeaders.TryGetDirectoryOffset(cor.ResourcesDirectory, out int start)) return null;

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
            if (probeRel < 0) probeRel = res.Offset + 4;
        }

        return offsets;
    }

    private static bool TryLocate(string resourceName, out string path, out long offset)
    {
        if (_offsets != null && _offsets.TryGetValue(resourceName, out offset)) { path = _path; return true; }
        if (_packOffsets != null && _packOffsets.TryGetValue(resourceName, out offset)) { path = _packPath; return true; }

        path = null;
        offset = 0;
        return false;
    }

    private static int _resRva, _resSize;
    private static long _probeRel = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualUnlock(IntPtr address, UIntPtr size);

    // GetManifestResourceStream はマップ済みの DLL イメージをそのまま読むので、一度読んだ埋め込みリソース
    // (翻訳・画像・音など) のページがプロセスの常駐メモリに残り続ける。ファイル由来の書き換えていないページなので、
    // 常駐から外しても次に読む時にファイルキャッシュから戻るだけ。ロックしていないページに VirtualUnlock を
    // 呼ぶと常駐から外れる (戻り値は失敗になる)。外したバイト数の目安 (リソース区間の大きさ) を返す
    internal static long TrimResourcePages()
    {
        try
        {
            if (!_scanned) Scan();
            if (_resRva <= 0 || _resSize <= 0 || _probeRel < 0) return 0;

            IntPtr image = Marshal.GetHINSTANCE(typeof(EmbeddedBundleFile).Module);
            if (image == IntPtr.Zero || image == new IntPtr(-1)) return 0;

            // イメージとして RVA 通りに配置されているかを、最初のバンドルの署名で確かめてから触る
            IntPtr resources = IntPtr.Add(image, _resRva);
            for (int i = 0; i < Signature.Length; i++)
                if (Marshal.ReadByte(resources, (int)_probeRel + i) != Signature[i]) return 0;

            VirtualUnlock(resources, (UIntPtr)(uint)_resSize);
            return _resSize;
        }
        catch (Exception e)
        {
            Logger.Warn($"resource page trim failed ({e.GetType().Name}: {e.Message})", "EmbeddedBundleFile");
            return 0;
        }
    }

    private static bool HasSignature(string path, long offset)
    {
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(offset, SeekOrigin.Begin);

        Span<byte> head = stackalloc byte[Signature.Length];
        return fs.Read(head) == head.Length && head.SequenceEqual(Signature);
    }
#endif
}
