using UnityEngine;

namespace EndKnot
{
    internal sealed class SandboxBlock : CustomNetObject
    {
        public byte OwnerId { get; }

        public SandboxBlock(Vector2 position, byte ownerId)
        {
            OwnerId = ownerId;
            Position = position;
            // 単一 ▣ (入れ子四角) 文字を大 <size>% で描画。
            // 旧 6×6 グラデーション (~430 byte packet) を ~50 byte に圧縮。
            // ▣ は中央に小四角が入った構造で、平板な ■ より「ブロック」感がある
            CreateNetObject("<size=380%><color=#888888>▣</color></size>", position);
        }

        public override void OnMeeting() => Despawn();
    }
}
