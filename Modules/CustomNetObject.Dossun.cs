using UnityEngine;

namespace EndKnot
{
    // Dossun (ドッスン) が遠隔操作する巨大ブロック。移動するため OnFixedUpdate の空 override はしない
    // (base の毎フレーム RpcSnapTo(SendOption.None) にそのまま同期を任せる)。
    internal sealed class DossunBlock : CustomNetObject
    {
        // 毎フレーム TP(ForceSnapSend) が base throttle を素通りすると ~50 SnapTo/s で公式 anti-cheat の kick 経路に乗る。
        // 間引きは CNO 側に一元化する確立パターン (Snowball 0.2f / WaveCannonBeamSegment 0.1f)。
        protected override float ForceSnapMinInterval => 0.1f;

        public DossunBlock(Vector2 position)
        {
            CreateNetObject("<size=300%><line-height=70%><#5a5a6e>██</color><br><#5a5a6e>██</color></line-height></size>", position);
        }

        public override void OnMeeting() => Despawn();
    }
}
