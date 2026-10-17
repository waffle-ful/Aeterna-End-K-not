using UnityEngine;

namespace EndKnot
{
    // Nova が操縦する☆。持ち主の周りを、持ち主の歩こうとした向きへ回り込む (毎 0.1 秒 TP)。
    internal sealed class NovaStar : CustomNetObject
    {
        // 毎 tick の TP(ForceSnapSend) は base throttle を素通りするので、間引きは CNO 側に一元化する (DossunBlock と同じ)
        protected override float ForceSnapMinInterval => 0.1f;
        protected override float PeriodicSnapMinInterval => 1f;

        public NovaStar(Vector2 position)
        {
            CreateNetObject("<size=12><#ffd700>☆", position);
        }

        // 寿命は役職側 (Nova.EndAll の実体化待ち Despawn) だけで持つ。ここで消すと実体化前の個体に Despawn が当たり、
        // 生成コルーチンが後から追跡外の CNO を出す。空にして基底の会議明け再生成も止める
        public override void OnMeeting() { }
    }

    // Nova が☆から撃つ矢。→ 1 文字を <rotate> で飛ぶ向きへ傾ける。操縦を始めた時点でマップ外へ先に出しておき、
    // 撃つ時に発射位置へ TP して向きを付ける。飛び終わったら退避座標へ戻して次の 1 発に使い回す。
    internal sealed class NovaArrow : CustomNetObject
    {
        // 同時に飛ぶ本数が多い時は役職側が粗くして (0.2)、☆と合わせた SnapTo の合計本数を抑える
        public float SnapInterval = 0.1f;
        protected override float ForceSnapMinInterval => SnapInterval;
        protected override float PeriodicSnapMinInterval => 1f;

        public static string Sprite(float angle, int size)
        {
            return $"<size={size}><rotate={Mathf.RoundToInt(angle)}><#ff3030>→";
        }

        public NovaArrow(Vector2 position, string sprite)
        {
            CreateNetObject(sprite, position);
        }

        // 寿命は役職側 (Nova.EndAll の実体化待ち Despawn) だけで持つ。ここで消すと実体化前の個体に Despawn が当たり、
        // 生成コルーチンが後から追跡外の CNO を出す。空にして基底の会議明け再生成も止める
        public override void OnMeeting() { }
    }
}
