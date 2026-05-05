using UnityEngine;

namespace EndKnot.Modules.CalamityMenu;

/// <summary>
/// Calamity 風メインメニューの全グローバル状態。
/// kill switch (<see cref="Active"/> = false) を切れば全機能無効化され、既存 EHR メニューが復活する。
/// クレーム対策の保険として残す。詳細は docs/calamity-menu-plan.md。
/// </summary>
public static class CalamityMenuState
{
    /// <summary>
    /// false にすると Calamity メニュー全機能が無効化され、既存 EHR の MainMenuManagerPatch /
    /// TitleLogoPatch が通常通り走る。緊急復旧用の kill switch。
    /// </summary>
    public static bool Active = true;

    /// <summary>自前 root GameObject (EndKnotMenuRoot) への参照。Phase 2 で初期化。</summary>
    public static GameObject Root;

    /// <summary>VanillaSuppressor 多重実行防止フラグ。</summary>
    public static bool VanillaSuppressed;

    /// <summary>Phase 6 の遷移演出で RightPanel を元位置に戻すための保存値。</summary>
    public static Vector3 RightPanelOriginalPos;
}
