using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace EndKnot.Modules;

// 当たり判定のホストローカル可視化 (デバッグ用)。/hitbox でトグル、既定 OFF。
// 役職の Kill チェック関数が「判定に実際に使っている値そのもの」を毎 fixed update 渡して描く
// ことで、可視化と判定のズレを原理的に排除する (見えているズレ = CNO 見た目とのズレそのもの)。
// 描画は SpriteRenderer のローカル矩形のみでネットワーク送信ゼロ (EkmShadow の可視化ヘルパーと同流儀)。
// TTL 方式: Draw* が呼ばれ続ける限り表示され、呼ばれなくなった形状は Tick が自動で消す (消し忘れ防止)。
public static class HitboxDebug
{
    public static bool Enabled;

    private const float Ttl = 0.2f;
    private const int SortingOrder = 150;
    private static readonly Color FillColor = new(1f, 0.1f, 0.1f, 0.25f);
    private static readonly Dictionary<string, Entry> Entries = [];

    private sealed class Entry
    {
        public GameObject Go;
        public float LastDrawTime;
    }

    // 方向付きビーム矩形: origin から direction に沿って backReach 後方〜 forwardLength 前方、半厚 halfWidth。
    // WaveCannon.CheckBeamKills / SuperCannonShot.CheckLineKills の判定式と同じパラメータをそのまま渡す。
    public static void DrawBeamRect(string key, Vector2 origin, Vector2 direction, float backReach, float forwardLength, float halfWidth)
    {
        if (!Enabled) return;

        float totalLen = backReach + forwardLength;
        Vector2 center = origin + direction * ((forwardLength - backReach) / 2f);
        float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
        DrawQuad(key, center, new Vector2(totalLen, halfWidth * 2f), angle);
    }

    // 軸平行の水平帯: Dynamic 掃引ビーム用。
    public static void DrawBand(string key, float centerX, float y, float halfLength, float halfWidth)
    {
        if (!Enabled) return;
        DrawQuad(key, new Vector2(centerX, y), new Vector2(halfLength * 2f, halfWidth * 2f), 0f);
    }

    // 円 (爆発範囲等): 24 分割の輪郭線で近似。
    public static void DrawCircle(string key, Vector2 center, float radius)
    {
        if (!Enabled) return;

        const int segments = 24;
        for (var i = 0; i < segments; i++)
        {
            float mid = Mathf.PI * 2f * (i + 0.5f) / segments;
            float segLen = 2f * radius * Mathf.Sin(Mathf.PI / segments);
            Vector2 segCenter = center + new Vector2(Mathf.Cos(mid), Mathf.Sin(mid)) * radius;
            float angle = mid * Mathf.Rad2Deg + 90f;
            DrawQuad($"{key}.c{i}", segCenter, new Vector2(segLen * 1.05f, 0.08f), angle);
        }
    }

    // 毎 fixed update に FixedUpdateCaller から呼ばれ、TTL 切れの形状を破棄する。
    public static void Tick()
    {
        if (Entries.Count == 0) return;

        List<string> stale = Entries.Where(kv => Time.time - kv.Value.LastDrawTime > Ttl).Select(kv => kv.Key).ToList();
        foreach (string key in stale)
        {
            if (Entries[key].Go != null) Object.Destroy(Entries[key].Go);
            Entries.Remove(key);
        }
    }

    public static void Clear()
    {
        foreach (Entry e in Entries.Values)
            if (e.Go != null)
                Object.Destroy(e.Go);
        Entries.Clear();
    }

    private static void DrawQuad(string key, Vector2 center, Vector2 size, float angleDeg)
    {
        if (!Entries.TryGetValue(key, out Entry entry) || entry.Go == null)
        {
            var go = new GameObject($"HitboxDebug.{key}");
            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = MarkerSprite;
            sr.color = FillColor;
            sr.sortingOrder = SortingOrder;
            entry = new Entry { Go = go };
            Entries[key] = entry;
        }

        Transform t = entry.Go.transform;
        t.position = new Vector3(center.x, center.y, 0f);
        t.rotation = Quaternion.Euler(0f, 0f, angleDeg);
        t.localScale = new Vector3(size.x, size.y, 1f);
        entry.LastDrawTime = Time.time;
    }

    private static Sprite _markerSprite;

    private static Sprite MarkerSprite
    {
        get
        {
            if (_markerSprite != null) return _markerSprite;

            Texture2D tex = new(4, 4, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            Color[] pixels = new Color[16];
            for (var i = 0; i < 16; i++) pixels[i] = Color.white;
            tex.SetPixels(pixels);
            tex.Apply();
            _markerSprite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);
            _markerSprite.hideFlags |= HideFlags.HideAndDontSave;
            return _markerSprite;
        }
    }
}
