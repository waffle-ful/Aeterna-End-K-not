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
        // 較正中 (2026-05-16 三段階目): mult 15→30→45 と上げても比率 2x→1.5x→1.5x で
        // 45 → 1.5x が改善せず。線形応答 vs 頭打ちを切り分けるため一気に 150 まで上げる。
        //   - 視覚が線形なら overshoot して見える → 計算式正しい、後で半分に戻す
        //   - 視覚が頭打ちなら mult 45 と同じ大きさ → W グリッド方式に切替
        private static string BuildSprite(float radius)
        {
            int s = (int)(radius * 150f);
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
