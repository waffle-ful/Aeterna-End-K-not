using UnityEngine;

namespace EndKnot
{
    internal sealed class ForceFieldCNO : CustomNetObject
    {
        // 単一 ○ 文字を <size> で半径に応じてスケール。
        //
        // 設計理由 (2026-05-15 fix):
        // - 8×8 ring (~900 byte packet) は AU 2026 anti-cheat の 1KB 単一 packet 閾値ぎりぎり
        // - 24×24 ring (~3072 byte packet) は確実に閾値超えで host が Hacking で kick される
        // - 単一文字 + 大 <size> は packet ~50 byte で閾値に圧倒的余裕
        //
        // 文字選択: ○ (U+25CB White Circle、AdventurerItem の Grouping と同字)。
        // 〇 (U+3007) は VCR SDF に無く fallback で size 効かない。
        //
        // size 単位の選択 (重要):
        // - <size=N%> はパーセント上限で頭打ちになり 600%+ で殆ど差が出ない (実機検証 2026-05-15)
        // - <size=N> (無単位、絶対 font size) は WaveCannon 流で線形にスケール、上限なし
        // → 絶対値 mode を採用
        //
        // 較正 (2026-05-16):
        // 線形 mult (font = radius × 50) では FieldRadius=1 で見た目と当たり判定がほぼ一致するが
        // FieldRadius=8 で当たり判定 > 見た目になる。つまり TMP <size=N> 絶対モードでも N が
        // 大きくなるとサブリニア応答 (頭打ち) する。
        //
        // モデル:  visual_world_radius ≈ K × font_size ^ P    (0 < P ≤ 1)
        //   → font_size = (visual_radius / K) ^ (1 / P)
        //
        // 目標視覚半径は ForceFielder の当たり判定半径 (radius × HitRadiusScale) と一致させる。
        // K / P は実測前の暫定値。/sizetest を 50/100/200/400/800 で打って各 visual 直径を測り
        // log-log 回帰で fit 直すこと (詳細は plans/1-8-precious-pie.md)。
        private const float K = 0.05f;
        private const float P = 0.9f;

        private static string BuildSprite(float radius)
        {
            float targetVisualRadius = radius * Roles.ForceFielder.HitRadiusScale;
            int s = Mathf.Max(1, (int)Mathf.Pow(targetVisualRadius / K, 1f / P));
            return $"<size={s}><font=\"VCR SDF\"><line-height=67%><color=#4488ff>○</color></line-height></font></size>";
        }

        public ForceFieldCNO(Vector2 position, float radius)
        {
            CreateNetObject(BuildSprite(radius), position);
        }

        public override void OnMeeting()
        {
            Despawn();
        }
    }
}
