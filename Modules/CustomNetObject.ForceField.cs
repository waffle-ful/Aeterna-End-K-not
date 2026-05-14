using UnityEngine;

namespace EndKnot
{
    internal sealed class ForceFieldCNO : CustomNetObject
    {
        // 8x8 リング（外周青・中央透明）。<size=X%> で半径に応じてスケール。
        // X = clamp(radius * 40, 30, 300)。radius=3 → 120%。
        //
        // f65516be で 24×24 にスケールしたが、生成 sprite が 3072 byte 単一 packet となり
        // AU 2026 vanilla anti-cheat の ~1KB SetName 閾値を超えて host が Hacking で kick
        // される事案が発生。8×8 (~900 byte 単一 packet) に戻して回避。視覚と当たり判定の
        // 微妙な不一致は許容する (大半径での visual 縮小)。
        private static string BuildSprite(float radius)
        {
            int s = System.Math.Clamp((int)(radius * 40f), 30, 300);
            string t = $"<size={s}%><font=\"VCR SDF\"><line-height=67%>";
            const string B = "<#4488ff>█";
            const string E = "<alpha=#00>█";
            t += $"{E}{E}{B}{B}{B}{B}{E}{E}<br>";
            t += $"{E}{B}{B}{E}{E}{B}{B}{E}<br>";
            t += $"{B}{B}{E}{E}{E}{E}{B}{B}<br>";
            t += $"{B}{E}{E}{E}{E}{E}{E}{B}<br>";
            t += $"{B}{E}{E}{E}{E}{E}{E}{B}<br>";
            t += $"{B}{B}{E}{E}{E}{E}{B}{B}<br>";
            t += $"{E}{B}{B}{E}{E}{B}{B}{E}<br>";
            t += $"{E}{E}{B}{B}{B}{B}{E}{E}<br>";
            t += "</color></line-height></font></size>";
            return t;
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
