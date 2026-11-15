using UnityEngine;

namespace EndKnot.Modules;

// HUD カメラに載せる自作の全画面演出 (カットイン・キル演出) の描く順。
// 同じ Sorting Layer (本編はほぼ全部 Default) の中では sortingOrder が先に比べられ、z は order が同じ物の間でしか効かない。
// z だけで手前に置いた演出は、order を上げた画面 (設定画面の中身 = SortingGroup・order 12 など) の
// 字と下地 (order 0) の間に挟まる。そこで演出は帯ごとの order で HUD より上に出し、
// 全面を覆う画面を開いている間は帯ごと下げて、その画面の奥へ回す。
internal static class ScreenOverlayOrder
{
    // 帯 (手前ほど大きい)。HUD の自作文字 (100〜1000) より上・バグ報告の窓 (30000) より下。
    public const int KillOverlay = 2000;       // 特殊キル演出 / 爆発キル演出 (本編のキル画面の子)
    public const int AudienceCutIn = 2100;     // 視聴者の干渉のカットイン
    public const int CannonCutIn = 2200;       // 波動砲のカットイン (干渉と重なったら波動砲が手前)
    public const int CannonKillOverlay = 2300; // 波動砲のキル演出

    // 画面を開いている間に下げる量。下げた後は本編の HUD・画面 (order 0) より奥になる。
    private const int Buried = -20000;

    // 演出の部品が元から持つ order (PoolablePlayer の体や複製した文字) はこの幅に収まる
    private const int LocalSpan = 500;

    // 全面を覆う画面 (ロビー設定・役職説明・歯車・バグ報告) が開いているか
    public static bool ScreenCovers
    {
        get
        {
            try
            {
                if (InGameRoleInfoMenu.Showing || BugReport.IsOpen) return true;
                GameSettingMenu gsm = GameSettingMenu.Instance;
                if (gsm && gsm.isActiveAndEnabled) return true;
                if (HudManager.InstanceExists)
                {
                    OptionsMenuBehaviour gameMenu = HudManager.Instance.GameMenu;
                    if (gameMenu && gameMenu.IsOpen) return true;
                }
            }
            catch (System.Exception e) { Utils.ThrowException(e); }

            return false;
        }
    }

    // root 以下の全部の描画物を帯へ載せる (何度呼んでも同じ結果。後から足した部品にもそのまま使える)
    public static void Apply(Transform root, int band, bool buried)
    {
        if (!root) return;
        int target = band + (buried ? Buried : 0);
        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (!r) continue;
            int o = r.sortingOrder;
            int local = Near(o, band + Buried) ? o - (band + Buried) : Near(o, band) ? o - band : o;
            r.sortingOrder = target + local;
        }
    }

    // 毎フレーム呼ぶ。画面の開け閉めが変わった時だけ帯を付け直す。
    public static void Track(Transform root, int band, ref bool buried)
    {
        bool now = OrderFor(band) != band;
        if (now == buried) return;
        buried = now;
        Apply(root, band, now);
    }

    private static int _coversFrame = -1;
    private static bool _covers;

    // 演出の途中で足す部品 (粒など) に付ける order。同じフレームの判定は使い回す。
    public static int OrderFor(int band)
    {
        int f = Time.frameCount;
        if (f != _coversFrame)
        {
            _coversFrame = f;
            _covers = ScreenCovers;
        }

        return band + (_covers ? Buried : 0);
    }

    private static bool Near(int order, int b) => order > b - LocalSpan && order < b + LocalSpan;
}
