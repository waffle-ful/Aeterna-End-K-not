using System;
using System.Diagnostics;
using System.Text;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

namespace EndKnot.Modules;

// バニラ資産のうち、圧縮も縮小もされずに常駐している巨大テクスチャを実行時に作り替えて常駐メモリを削る。
//
// 対象は MainMenu シーンと共に読み込まれる木槌演出 (JudgeGavel) の hammerSlam_01..06 — 1866x1271 RGBA32 が
// 6 枚で計約 54MB を、メインメニュー到達から終了まで握り続ける (CENSUSTOP kind=tex の恒常首位)。
// 一瞬しか映らない演出用で、半分の解像度 + BC3 (DXT5) にしても見た目の差は出ない。
//
// 方式 = 「同じ Texture2D オブジェクトを縮小・圧縮版に再初期化する」。
//  - 駒はアニメーションクリップ (SpriteRenderer.sprite のキーフレーム) から Sprite 経由で参照されており、
//    Sprite→Texture の参照は差し替えられない。Destroy すると演出ごと壊れる。
//  - Texture2D は同一オブジェクトのまま Reinitialize で寸法と形式を変えられる。Sprite のメッシュ UV は
//    正規化座標で焼き込まれているので、テクスチャ全面 1 枚のスプライトなら縮小後もそのまま正しく貼れる。
//  - 元テクスチャは非 readable のため公開 API の Reinitialize は例外を投げる。ネイティブ側の
//    ReinitializeWithTextureFormatImpl (readable ガードの内側) を直接呼び、GPU 側だけを差し替える。
//    CPU 側のピクセルは元々持っていない (非 readable) ので失うものは無い。
//  - 新しい内容は Graphics.Blit で GPU 縮小 → ReadPixels → Compress で作った小テクスチャから
//    Graphics.CopyTexture (GPU→GPU、同寸・同形式) で流し込む。
//
// 失敗時は元テクスチャに一切触らず (再初期化は小テクスチャ完成後にしか呼ばない)、Health.log に理由を残す。
// メインメニュー到達ごとに再走査するが、既に圧縮形式になっているものは飛ばすので冪等。
//
// ⚠️ 実機結果 (2026-09-02・Windows/D3D11): ReinitializeWithTextureFormatImpl は非 readable テクスチャに対して
// ネイティブ側でも false を返す (6 枚とも `reinit ok=False`、元は無傷)。この経路では削減できないため
// 設定 SlimVanillaTextures は既定 OFF。残る手段は木槌演出そのものを縮小スプライトで自前再生し原本を
// Resources.UnloadAsset する方式 (アニメクリップの再現が要る) で、必要になった時に別途設計する。
// なお非 readable テクスチャは PC では GPU メモリのみに常駐するため、削減の主な受益者は Android ホスト。
public static class TextureSlimmer
{
    private static readonly string[] TargetPrefixes = ["hammerSlam_"];
    private const int MinWidth = 1024; // これより小さいものは触らない (誤爆防止)
    private static bool _unsupportedLogged;

    public static void RunOnce()
    {
        if (Main.SlimVanillaTextures == null || !Main.SlimVanillaTextures.Value) return;

        try
        {
            if (SystemInfo.copyTextureSupport == CopyTextureSupport.None)
            {
                if (!_unsupportedLogged)
                {
                    _unsupportedLogged = true;
                    HealthLog.Note($"TEXSLIM skip reason=nocopytexture t={Utils.TimeStamp}");
                }

                return;
            }

            var sw = Stopwatch.StartNew();
            bool dxt = SystemInfo.SupportsTextureFormat(TextureFormat.DXT5);
            var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Texture2D>());
            int done = 0, skipped = 0, failed = 0;
            long savedBytes = 0;
            var names = new StringBuilder();

            for (int i = 0; i < arr.Length; i++)
            {
                Object o = arr[i];
                Texture2D t = o != null ? o.TryCast<Texture2D>() : null;
                if (t == null) continue;

                string name = t.name;
                if (!IsTarget(name)) continue;

                TextureFormat fmt = t.format;

                // 既に圧縮/縮小済み (前回のメニュー到達で処理済み) は飛ばす
                if (fmt is not (TextureFormat.RGBA32 or TextureFormat.ARGB32) || t.width < MinWidth)
                {
                    skipped++;
                    continue;
                }

                int w1 = t.width, h1 = t.height;
                long before = EstimateBytes(w1, h1, fmt);
                string result = Slim(t, dxt, out int w2, out int h2, out TextureFormat fmt2);

                if (result == null)
                {
                    done++;
                    long after = EstimateBytes(w2, h2, fmt2);
                    savedBytes += Math.Max(0, before - after);
                    names.Append(name).Append(':').Append(w1).Append('x').Append(h1).Append("->").Append(w2).Append('x').Append(h2).Append(fmt2).Append(' ');
                }
                else
                {
                    failed++;
                    names.Append(name).Append(":FAIL(").Append(result).Append(") ");
                }
            }

            if (done + failed > 0)
                HealthLog.Note($"TEXSLIM done={done} failed={failed} skipped={skipped} savedMB={savedBytes / 1048576} ms={sw.ElapsedMilliseconds} {names.ToString().TrimEnd()} t={Utils.TimeStamp}");

            Logger.Info($"slimmed {done} textures (failed {failed}, skipped {skipped}), saved ~{savedBytes / 1048576}MB in {sw.ElapsedMilliseconds}ms", "TextureSlimmer");
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
        }
    }

    private static bool IsTarget(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;

        foreach (string p in TargetPrefixes)
            if (name.StartsWith(p, StringComparison.Ordinal)) return true;

        return false;
    }

    // 成功なら null、失敗なら理由。元テクスチャは小テクスチャの完成後にしか触らない。
    private static string Slim(Texture2D src, bool dxt, out int w2, out int h2, out TextureFormat fmt2)
    {
        // BC 系は 4 の倍数寸法が要る。半分に落として 4 で切り捨てる。
        w2 = Math.Max(4, (src.width / 2) & ~3);
        h2 = Math.Max(4, (src.height / 2) & ~3);
        fmt2 = TextureFormat.RGBA32;

        Texture2D small = null;
        RenderTexture rt = null;
        RenderTexture prevActive = null;
        bool activeSwapped = false;

        try
        {
            rt = RenderTexture.GetTemporary(w2, h2, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            if (rt == null) return "rt";

            Graphics.Blit(src, rt);

            prevActive = RenderTexture.active;
            RenderTexture.active = rt;
            activeSwapped = true;

            small = new Texture2D(w2, h2, TextureFormat.RGBA32, false);
            small.ReadPixels(new Rect(0, 0, w2, h2), 0, 0);
            small.Apply(false, false);

            RenderTexture.active = prevActive;
            activeSwapped = false;
            RenderTexture.ReleaseTemporary(rt);
            rt = null;

            if (dxt)
            {
                // RGBA32 (アルファ有り) からの Compress は DXT5 になる。実際の結果形式を採用する。
                small.Compress(true);
                small.Apply(false, false);
            }

            fmt2 = small.format;

            // 元オブジェクトを同寸・同形式で作り替える (非 readable ガードの内側の実装を直接呼ぶ)。
            bool ok = src.ReinitializeWithTextureFormatImpl(w2, h2, fmt2, false);
            if (!ok || src.width != w2 || src.height != h2 || src.format != fmt2)
                return $"reinit ok={ok} {src.width}x{src.height} {src.format}";

            Graphics.CopyTexture(small, src);
            return null;
        }
        catch (Exception e)
        {
            return e.GetType().Name + ":" + e.Message;
        }
        finally
        {
            try
            {
                if (activeSwapped) RenderTexture.active = prevActive;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (small != null) Object.Destroy(small);
            }
            catch { }
        }
    }

    private static long EstimateBytes(int w, int h, TextureFormat fmt)
    {
        double bpp = fmt switch
        {
            TextureFormat.DXT1 or TextureFormat.BC4 => 0.5,
            TextureFormat.DXT5 or TextureFormat.BC5 or TextureFormat.BC7 => 1,
            TextureFormat.RGB24 => 3,
            TextureFormat.Alpha8 or TextureFormat.R8 => 1,
            _ => 4
        };

        return (long)(w * (double)h * bpp);
    }
}
