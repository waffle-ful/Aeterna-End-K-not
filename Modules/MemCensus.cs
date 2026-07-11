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

            // Texture2D の native 概算 (RGBA32 換算 MB)。犯人がテクスチャ系かの当たりを付ける
            try
            {
                var texs = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Texture2D>());
                long px = 0;

                foreach (var o in texs)
                {
                    Texture2D t = o != null ? o.TryCast<Texture2D>() : null;
                    if (t != null)
                        px += (long)t.width * t.height;
                }

                sb.Append(" texMB=").Append(px * 4 / (1024 * 1024));
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
