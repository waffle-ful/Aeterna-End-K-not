using UnityEngine;

namespace EndKnot
{
    internal sealed class WaveCannonWarning : CustomNetObject
    {
        // 確殺波動砲では対象へ毎フレ TP 追従する → base の opt-in 間引きで 10Hz に抑える (静的配置の従来用途は TP を呼ばず影響なし)
        protected override float ForceSnapMinInterval => 0.1f;

        public WaveCannonWarning(Vector2 position, string sprite)
        {
            CreateNetObject(sprite, position);
        }

        public override void OnMeeting() => Despawn();
    }

    internal sealed class WaveCannonBeamSegment : CustomNetObject
    {
        // ビームは Firing 中ずっと毎フレ TP で揺らす→ ForceSnapSend 連射になるので base の opt-in 間引きで 10Hz 固定(揺れを滑らかに保ちつつ anti-cheat 安全)。
        protected override float ForceSnapMinInterval => 0.1f;

        public WaveCannonBeamSegment(Vector2 position, string sprite)
        {
            CreateNetObject(sprite, position);
        }

        public override void OnMeeting() => Despawn();
    }

    internal sealed class WaveCannonGate : CustomNetObject
    {
        // 確殺波動砲では対象へ毎フレ TP 追従する → base の opt-in 間引きで 10Hz に抑える (静的配置の従来用途は TP を呼ばず影響なし)
        protected override float ForceSnapMinInterval => 0.1f;

        // 旧 6x6 ブロックアート (~510B) は公式鯖の Data 入りパケット ~680B 制限を必ず超過して
        // reason=Hacking キックされる (2026-07-11 実測、docs/wavecannon-official-kick-resume.md)。
        // TOHP と同じ「でかい★」1 文字に置き換えてスプライトを ~30B に削減した。
        // size は 600% が上限: <size=700%> 以上は非モッドクライアントで描画が壊れる (memory: tmp_tag_pitfalls)
        public WaveCannonGate(Vector2 position, string borderColor = "#5e1a00", string midColor = "#ff7a00", string centerColor = "#ffaa00")
        {
            CreateNetObject($"<size=600%><{midColor}>★", position);
        }

        public override void OnMeeting() => Despawn();
    }
}
