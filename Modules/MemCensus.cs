using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace EndKnot.Modules;

// ゲームサイクル毎の Unity オブジェクト型別 census。1ゲームあたり ~50〜150MB の
// ネイティブ破棄漏れの犯人型を特定するための計器。ロビー復帰の 8 秒後に 1 回だけ
// Resources.FindObjectsOfTypeAll の型別カウントを Health.log に CENSUS 行で残す。
// サイクル間で単調に増える型 = 破棄漏れの型。毎秒走査系 (UiAnomalyWatch) と違い
// 1 サイクル 1 回なので膨張への寄与は無視できる。
public static class MemCensus
{
    private static long _lastRunTs;
    private const int TopTexOwnerCount = 8;

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
            if (src == "lobby") _lastRunTs = now; // 手動発火(bridge/manual)は自動発火の30s抑制を消費しない
            HealthLog.NoteOp("MemCensus");

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
            // DXT 圧縮アトラスを 4〜8 倍過大計上 → texMB=835 という偽の主犯を
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

            // AudioClip の native 常駐概算 MB。PCM 展開済み (DecompressOnLoad かつ Loaded) のみ
            // samples×channels×4 バイトで加算する。CompressedInMemory は clip.samples が展開後の
            // サンプル数を返すため合算すると圧縮前提の AssetBundle BGM を数十倍過大計上する。
            try
            {
                var auds = Resources.FindObjectsOfTypeAll(Il2CppType.Of<AudioClip>());
                double totalAudBytes = 0;
                int audCompressed = 0, audUnloaded = 0;

                foreach (var o in auds)
                {
                    AudioClip a = o != null ? o.TryCast<AudioClip>() : null;
                    if (a == null) continue;

                    try
                    {
                        if (a.loadType == AudioClipLoadType.Streaming || a.loadState != AudioDataLoadState.Loaded)
                            audUnloaded++;
                        else if (a.loadType == AudioClipLoadType.CompressedInMemory)
                            audCompressed++;
                        else if (a.loadType == AudioClipLoadType.DecompressOnLoad)
                            totalAudBytes += (double)a.samples * a.channels * 4;
                    }
                    catch { }
                }

                sb.Append(" audMB=").Append((long)(totalAudBytes / (1024 * 1024)))
                  .Append(" audCmp=").Append(audCompressed)
                  .Append(" audUnl=").Append(audUnloaded);
            }
            catch { sb.Append(" audMB=?"); }

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
            TopAudioClips(10, now, src);

            // il2cpp (Boehm) 側の型別生存オブジェクト帰属。CENSUS と同じ発火点・同じ間引きに乗せる。
            // 自動発火 (src=lobby) は既定 OFF — 手動発火 (manual/bridge) は設定に関係なく常に走る。
            bool boehmAllowed = src != "lobby" || (Main.EnableBoehmCensus is { Value: true });
            if (boehmAllowed)
                try { BoehmCensus.RunNow(src); } catch (Exception e) { Logger.Warn($"boehm census hook failed: {e.Message}", "MemCensus"); }

            AttributeTopTextureOwners(TopTexOwnerCount, now, src);
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

    // AudioClip の実消費上位。CompressedInMemory は clip.samples が展開後サンプル数を返すため
    // PCM 換算をそのまま出すと過大表示になる — desc 先頭に ~ を付け MB は 0.0 のまま区別できるようにする。
    private static void TopAudioClips(int top, long now, string src)
    {
        try
        {
            var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<AudioClip>());
            if (arr == null) return;

            var list = new List<(string Desc, double Bytes)>(256);

            foreach (var o in arr)
            {
                AudioClip a = o != null ? o.TryCast<AudioClip>() : null;
                if (a == null) continue;

                try
                {
                    string n = string.IsNullOrEmpty(a.name) ? "<noname>" : a.name.Replace(' ', '_');
                    string desc = $"{n}_{a.loadType}_{a.loadState}_{a.frequency}Hz_{a.channels}ch";

                    if (a.loadType == AudioClipLoadType.CompressedInMemory)
                        list.Add(("~" + desc, 0));
                    else if (a.loadType == AudioClipLoadType.DecompressOnLoad && a.loadState == AudioDataLoadState.Loaded)
                        list.Add((desc, (double)a.samples * a.channels * 4));
                    else
                        list.Add((desc, 0));
                }
                catch { }
            }

            var sb = new StringBuilder("CENSUSTOP kind=aud t=").Append(now).Append(" src=").Append(src).Append(' ');

            foreach (var kv in list.Where(x => !x.Desc.StartsWith('~')).OrderByDescending(x => x.Bytes).Take(top))
                sb.Append(kv.Desc).Append('x').Append((kv.Bytes / (1024 * 1024)).ToString("0.0")).Append("MB ");

            // 圧縮クリップは PCM 換算が無いので順位に乗らない。別枠で loadState を見えるようにする。
            foreach (var kv in list.Where(x => x.Desc.StartsWith('~')).Take(top))
                sb.Append(kv.Desc).Append(' ');

            HealthLog.Note(sb.ToString().TrimEnd());
        }
        catch (Exception e) { Logger.Warn($"census top aud failed: {e.Message}", "MemCensus"); }
    }

    // CENSUSTOP kind=tex の上位テクスチャは「何 MB か」までしか分からず、常駐の犯人 (どの
    // GameObject が保持しているか) には到達できない。上位テクスチャを使う Sprite と、その Sprite を
    // 表示している SpriteRenderer/Image のヒエラルキーパスを逆引きして 1 テクスチャ 1 行で残す。
    private static void AttributeTopTextureOwners(int top, long now, string src)
    {
        try
        {
            var texArr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Texture2D>());
            if (texArr == null) return;

            var texList = new List<(Texture2D Tex, double Bytes)>(256);
            for (int i = 0; i < texArr.Length; i++)
            {
                var o = texArr[i];
                Texture2D t = o != null ? o.TryCast<Texture2D>() : null;
                if (t != null) texList.Add((t, EstimateBytes(t)));
            }

            var topTex = texList.OrderByDescending(x => x.Bytes).Take(top).ToList();
            if (topTex.Count == 0) return;

            var spriteArr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Sprite>());
            var spritesByTexId = new Dictionary<int, List<Sprite>>(256);

            if (spriteArr != null)
            {
                for (int i = 0; i < spriteArr.Length; i++)
                {
                    var o = spriteArr[i];
                    Sprite s = o != null ? o.TryCast<Sprite>() : null;
                    Texture2D st = s != null ? s.texture : null;
                    if (st == null) continue;

                    int id = st.GetInstanceID();
                    if (!spritesByTexId.TryGetValue(id, out List<Sprite> list)) spritesByTexId[id] = list = new List<Sprite>();
                    list.Add(s);
                }
            }

            var srArr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<SpriteRenderer>());
            var imgArr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<UnityEngine.UI.Image>());

            foreach ((Texture2D tex, double bytes) in topTex)
            {
                string texName = string.IsNullOrEmpty(tex.name) ? "<noname>" : tex.name.Replace(' ', '_');
                var sb = new StringBuilder("CENSUSREF src=").Append(src).Append(" t=").Append(now).Append(" tex=").Append(texName);

                if (!spritesByTexId.TryGetValue(tex.GetInstanceID(), out List<Sprite> sprites) || sprites.Count == 0)
                {
                    sb.Append(" sprites=0 owner=none");
                    HealthLog.Note(sb.ToString());
                    continue;
                }

                sb.Append(" sprites=").Append(sprites.Count).Append(':');
                int listed = Math.Min(sprites.Count, 8);
                for (int i = 0; i < listed; i++)
                {
                    string sn = string.IsNullOrEmpty(sprites[i].name) ? "<noname>" : sprites[i].name.Replace(' ', '_');
                    sb.Append(sn);
                    if (i < listed - 1) sb.Append(',');
                }

                string owner = FindSpriteOwnerPath(sprites, srArr, imgArr);
                sb.Append(" owner=").Append(owner);
                HealthLog.Note(sb.ToString());
            }
        }
        catch (Exception e) { Logger.Warn($"census tex owner attribution failed: {e.Message}", "MemCensus"); }
    }

    // Sprite を表示している SpriteRenderer/Image を手動 for ループで探す (Il2Cpp オブジェクト配列に
    // マネージドラムダを渡すと呼び出し毎に GCHandle が漏れるため LINQ を使わない)。
    private static string FindSpriteOwnerPath(List<Sprite> sprites, UnityEngine.Object[] srArr, UnityEngine.Object[] imgArr)
    {
        for (int i = 0; i < sprites.Count; i++)
        {
            Sprite sp = sprites[i];

            if (srArr != null)
            {
                for (int j = 0; j < srArr.Length; j++)
                {
                    var o = srArr[j];
                    SpriteRenderer sr = o != null ? o.TryCast<SpriteRenderer>() : null;
                    if (sr != null && sr.sprite == sp)
                    {
                        string path = HierarchyPath(sr.gameObject, 3);
                        return sr.gameObject.GetComponent<PowerTools.SpriteAnim>() != null ? path + "+anim" : path;
                    }
                }
            }

            if (imgArr != null)
            {
                for (int j = 0; j < imgArr.Length; j++)
                {
                    var o = imgArr[j];
                    UnityEngine.UI.Image img = o != null ? o.TryCast<UnityEngine.UI.Image>() : null;
                    if (img != null && img.sprite == sp) return HierarchyPath(img.gameObject, 3);
                }
            }
        }

        return "none";
    }

    // 親3段までのヒエラルキーパス。census 目的の帰属なので、それ以上遡っても読みやすさが落ちるだけ。
    private static string HierarchyPath(GameObject go, int maxDepth)
    {
        if (go == null) return "none";

        var names = new List<string>(maxDepth);
        Transform t = go.transform;
        int depth = 0;

        while (t != null && depth < maxDepth)
        {
            names.Add(t.name);
            t = t.parent;
            depth++;
        }

        names.Reverse();
        return string.Join("/", names).Replace(' ', '_');
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
