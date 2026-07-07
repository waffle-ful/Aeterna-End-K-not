using System;
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
        LateTask.New(Run, 8f, log: false);
    }

    private static void Run()
    {
        try
        {
            long now = Utils.TimeStamp;
            if (now - _lastRunTs < 30) return; // 遷移バタつきによる多重発火ガード
            _lastRunTs = now;

            var sb = new StringBuilder("CENSUS t=").Append(now);
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

            HealthLog.Note(sb.ToString());
        }
        catch (Exception e) { Logger.Warn($"census failed: {e.Message}", "MemCensus"); }
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
