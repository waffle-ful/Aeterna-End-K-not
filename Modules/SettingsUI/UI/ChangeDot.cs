// Based on https://github.com/haru03741-glitch/TownOfHost_L Modules/SettingsMenu/UI/ChangeDot.cs (GPL-3.0),
// which is based on https://github.com/haru03741-glitch/More-Roles-Plus src/UI/ChangeDot.cs (GPL-3.0)
using UnityEngine;

namespace EndKnot.SettingsUI;

// 初期値から変えた所の印 = 黒の縁の橙の丸 (カードと左の列のボタンの右上の角)
internal static class ChangeDot
{
    public static readonly Color Color = new(1f, 0.6f, 0.24f, 1f);
    public const float Size = 0.15f;

    public static SpriteRenderer Make(Transform parent, Vector3 localPos, int layer, float size = Size)
    {
        var rim = Slider.Disc(parent, UiScreen.BadgeName, UiAssets.Circle, size, Color.black, layer);
        rim.transform.localPosition = localPos;
        Slider.Disc(rim.transform, "Dot", UiAssets.Circle, 0.68f, Color, layer);
        return rim;
    }
}
