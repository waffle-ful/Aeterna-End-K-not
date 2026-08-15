using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace EndKnot.Modules;

// ゲームサイクル毎の Unity オブジェクト型別 census。BUG-20260706-01 (1ゲームあたり ~50〜150MB の
// ネイティブ破棄漏れ) の犯人型を特定するための計器。ロビー復帰の 8 秒後に 1 回だけ
// Resources.FindObjectsOfTypeAll の型別カウントを Health.log に CENSUS 行で残す。
// サイクル間で単調に増える型 = 破棄漏れの型。毎秒走査系 (UiAnomalyWatch) と違い
// 1 サイクル 1 回なので膨張への寄与は無視できる。
public static class MemCensus
{
    private static long _lastRunTs;

    // HealthLog の STATE 遷移 (→Lobby) から呼ばれる。ロビー生成 (Backrooms 等) が落ち着く 8 秒後に実施。
    public static void ScheduleAfterLobbyEnter()
    {
        LateTask.New(() => Run("lobby"), 8f, log: false);
    }

    // /census コマンド用の手動スナップショット (メニュー開閉前後などの 1-bit A/B に使う)
    public static void RunNow(string reason)
    {
        Run(reason);
    }

    private static void Run(string src)
    {
        try
        {
            long now = Utils.TimeStamp;
            if (src == "lobby" && now - _lastRunTs < 30) return; // 遷移バタつきによる多重発火ガード
            _lastRunTs = now;

            var sb = new StringBuilder("CENSUS t=").Append(now).Append(" src=").Append(src);
            Append<Texture2D>(sb, "tex");
            Append<RenderTexture>(sb, "rt");
            Append<Sprite>(sb, "spr");
            Append<Material>(sb, "mat");
            Append<Mesh>(sb, "mesh");
            Append<AudioClip>(sb, "aud");
            Append<GameObject>(sb, "go");

            // Texture2D の native 概算 MB。フォーマット別バイト/px + ミップ 4/3 倍で換算する。
            // ⚠️ 旧実装は全テクスチャを RGBA32 (4B/px) 換算しており、Alpha8 のフォントアトラスや
            // DXT 圧縮アトラスを 4〜8 倍過大計上 → BUG-20260810-03 で texMB=835 という偽の主犯を
            // 作った (実測の Backrooms 帰属は ~11MB)。数値の連続性は旧ログと比較不可になる点に注意。
            try
            {
                var texs = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Texture2D>());
                double totalBytes = 0;

                foreach (var o in texs)
                {
                    Texture2D t = o != null ? o.TryCast<Texture2D>() : null;
                    if (t != null) totalBytes += EstimateBytes(t);
                }

                sb.Append(" texMB=").Append((long)(totalBytes / (1024 * 1024)));
            }
            catch { sb.Append(" texMB=?"); }

            // AllOptions が実行中に伸びると index キーの行キャッシュ (BehaviourList/CategoryHeaderList) が
            // ずれ、メニューを開くたびに末尾分の行が旧個体を残したまま再生成される。成長の有無と犯人名を直接記録する。
            try
            {
                var all = OptionItem.AllOptions;
                sb.Append(" opt=").Append(all.Count).Append(" optTail=");
                for (int i = Math.Max(0, all.Count - 5); i < all.Count; i++)
                    sb.Append(all[i].Name.Replace(' ', '_')).Append(i == all.Count - 1 ? "" : ",");
            }
            catch { sb.Append(" opt=?"); }

            HealthLog.Note(sb.ToString());

            // 名前レベル attribution: 型カウントだけでは「どのコードの生成物か」に到達できないため、
            // 名前ヒストグラム上位をあわせて残す。サイクル間 diff で単調増加する名前 = 破棄漏れの生成元。
            TopNames<GameObject>("go", 20, now, src);
            TopNames<Material>("mat", 10, now, src);
            TopNames<Sprite>("spr", 10, now, src);
            TopTextures(10, now, src);
        }
        catch (Exception e) { Logger.Warn($"census failed: {e.Message}", "MemCensus"); }
    }

    private static void TopNames<T>(string kind, int top, long now, string src) where T : UnityEngine.Object
    {
        try
        {
            var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>());
            if (arr == null) return;

            var counts = new Dictionary<string, int>(512);

            foreach (var o in arr)
            {
                if (o == null) continue;
                string n = o.name;
                if (string.IsNullOrEmpty(n)) n = "<noname>";

                // "(Clone)" と連番 " (12)" を剥がして同一生成元をまとめる
                n = n.Replace("(Clone)", "").TrimEnd();
                int paren = n.LastIndexOf(" (", StringComparison.Ordinal);
                if (paren > 0 && n.EndsWith(")") && int.TryParse(n.Substring(paren + 2, n.Length - paren - 3), out _))
                    n = n.Substring(0, paren);

                counts[n] = counts.GetValueOrDefault(n) + 1;
            }

            var sb = new StringBuilder("CENSUSTOP kind=").Append(kind).Append(" t=").Append(now).Append(" src=").Append(src).Append(' ');

            foreach (var kv in counts.OrderByDescending(x => x.Value).Take(top))
                sb.Append(kv.Key.Replace(' ', '_')).Append('x').Append(kv.Value).Append(' ');

            HealthLog.Note(sb.ToString().TrimEnd());
        }
        catch (Exception e) { Logger.Warn($"census top {kind} failed: {e.Message}", "MemCensus"); }
    }

    // テクスチャの実消費上位。名前+寸法+フォーマット+推定MBを残す — 型カウント/合計値だけでは
    // 「どのテクスチャが重いか」に到達できず、次のメモリスパイク調査で毎回コードを掘る羽目になる。
    private static void TopTextures(int top, long now, string src)
    {
        try
        {
            var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Texture2D>());
            if (arr == null) return;

            var list = new List<(string Desc, double Bytes)>(256);

            foreach (var o in arr)
            {
                Texture2D t = o != null ? o.TryCast<Texture2D>() : null;
                if (t == null) continue;

                string n = string.IsNullOrEmpty(t.name) ? "<noname>" : t.name.Replace(' ', '_');
                list.Add(($"{n}_{t.width}x{t.height}_{t.format}", EstimateBytes(t)));
            }

            var sb = new StringBuilder("CENSUSTOP kind=tex t=").Append(now).Append(" src=").Append(src).Append(' ');

            foreach (var kv in list.OrderByDescending(x => x.Bytes).Take(top))
                sb.Append(kv.Desc).Append('x').Append((kv.Bytes / (1024 * 1024)).ToString("0.0")).Append("MB ");

            HealthLog.Note(sb.ToString().TrimEnd());
        }
        catch (Exception e) { Logger.Warn($"census top tex failed: {e.Message}", "MemCensus"); }
    }

    private static double EstimateBytes(Texture2D t)
    {
        double bytes = (double)t.width * t.height * BytesPerPixel(t.format);
        if (t.mipmapCount > 1) bytes *= 4.0 / 3.0;
        return bytes;
    }

    private static double BytesPerPixel(TextureFormat f) => f switch
    {
        TextureFormat.Alpha8 or TextureFormat.R8 => 1,
        TextureFormat.RGB565 or TextureFormat.RGBA4444 or TextureFormat.ARGB4444 or TextureFormat.R16 or TextureFormat.RHalf or TextureFormat.RG16 => 2,
        TextureFormat.RGB24 => 3,
        TextureFormat.DXT1 or TextureFormat.BC4 => 0.5,
        TextureFormat.DXT5 or TextureFormat.BC5 or TextureFormat.BC7 or TextureFormat.BC6H => 1,
        TextureFormat.RGHalf or TextureFormat.RFloat or TextureFormat.RG32 => 4,
        TextureFormat.RGBAHalf or TextureFormat.RGFloat => 8,
        TextureFormat.RGBAFloat => 16,
        _ => 4, // 未知/RGBA32系は 4B/px
    };

    private static void Append<T>(StringBuilder sb, string key) where T : UnityEngine.Object
    {
        try
        {
            var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>());
            sb.Append(' ').Append(key).Append('=').Append(arr != null ? arr.Length : -1);
        }
        catch { sb.Append(' ').Append(key).Append("=?"); }
    }
}
