using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace EndKnot
{
    // Debug-only CNO for visually verifying <size> rendering limits on non-modded clients.
    // /sizetest スポーンで使用、/sizeclean で全消去。
    //
    // 2 モードを実装:
    //   /sizetest          → 絶対 size (WaveCannon 流: <size=N> 無単位)
    //   /sizetest percent  → パーセント size (<size=N%>)
    internal sealed class SizeTestCNO : CustomNetObject
    {
        public static readonly List<SizeTestCNO> Active = [];

        public SizeTestCNO(Vector2 position, string sizeSpec, string label)
        {
            // sizeSpec は "20" (絶対) or "600%" (パーセント) どちらでも可
            CreateNetObject(
                $"<size={sizeSpec}><font=\"VCR SDF\"><line-height=67%><color=#4488ff>○</color></line-height></font></size>",
                position);
            Active.Add(this);
        }

        public override void OnMeeting() => Despawn();

        public static int SpawnRow(Vector2 origin, bool absolute = true)
        {
            // 5 個を横並びで spawn。各間隔 4 unit (重なり防止)
            // 絶対モードの cases は ForceField 較正実測用 (50/100/200/400/800) に一時拡張中。
            // ForceField 較正完了後に (20/40/60/80/100) へ revert すること。
            (string Size, string Label)[] cases = absolute
                ? [("50", "50"), ("100", "100"), ("200", "200"), ("400", "400"), ("800", "800")]
                : [("600%", "600%"), ("800%", "800%"), ("1000%", "1000%"), ("1200%", "1200%"), ("1500%", "1500%")];

            // 間隔は絶対モードの大型 case (800) でも重ならないように広めに取る。
            // 元 4 unit 固定 → 絶対モードは 20 unit、percent モードは 4 unit 据え置き。
            float spacing = absolute ? 20f : 4f;
            for (int i = 0; i < cases.Length; i++)
            {
                Vector2 pos = origin + new Vector2(i * spacing, 0f);
                _ = new SizeTestCNO(pos, cases[i].Size, cases[i].Label);
            }

            return cases.Length;
        }

        public static int DespawnAll()
        {
            int n = Active.Count;
            Active.ToArray().Do(c => c.Despawn());
            Active.Clear();
            return n;
        }
    }
}
