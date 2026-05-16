using UnityEngine;

namespace EndKnot
{
    internal sealed class SandboxBlock : CustomNetObject
    {
        public byte OwnerId { get; }

        // 視覚 world radius → font_size 逆関数。TMP <size=N> 絶対モードのサブリニア応答補正。
        // K/P は ForceField 較正と同パラメータ (memory: tmp-size-n-sublinear)。
        private const float K = 0.05f;
        private const float P = 0.9f;

        public SandboxBlock(Vector2 position, byte ownerId, float visualRadius)
        {
            OwnerId = ownerId;
            Position = position;
            // 旧仕様: 固定 <size=380%>▣ → BlockRadius option を変えても視覚追従せず、
            // また当たり判定 (BlockRadius + PlayerColliderRadius) との差で「一回り大きい当たり判定」
            // に見える問題があった。視覚半径を当たり判定半径と同期 (= プレイヤー体端で止まる位置を描画)。
            // 旧 6×6 グラデーション (~430 byte packet) を ~50 byte に圧縮した方針はそのまま維持。
            int s = Mathf.Max(1, (int)Mathf.Pow(visualRadius / K, 1f / P));
            CreateNetObject($"<size={s}><color=#888888>▣</color></size>", position);
        }

        public override void OnMeeting() => Despawn();
    }
}
