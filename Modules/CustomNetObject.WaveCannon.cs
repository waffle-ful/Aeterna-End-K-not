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

        // 旧 6x6 ブロックアート (~510B) は 2026-07-11 の公式鯖キック調査を機に
        // TOHP と同じ「でかい★」1 文字に置き換えてスプライトを ~30B に削減した
        // (当時の「~680B サイズ制限」説は後に反証済み — 真因は自前 byte 分割の stream 破損)。
        //
        // size 指定は絶対値モード (<size=N> 無単位) を使う:
        //   <size=N%> は ~600% で飽和し 700%+ は非モッド描画破壊の報告あり (ForceField.cs / memory: tmp_tag_pitfalls)。
        //   絶対値は上限なしで線形スケールする (Riptide size=40 ≈ 53u が実描画例)。
        // 24 は旧 <size=600%> (飽和上限) の目視等価の推定値 — ビーム較正 (thickness1=30 / 警告⚠=16) の間に置いた。
        //   ズレていたら実機目視で調整可 (発射チャージ中の★がプレイヤーの 1.5〜2 倍程度が従来の見た目)。
        private const int FontSizeAbsolute = 24;

        public WaveCannonGate(Vector2 position, string borderColor = "#5e1a00", string midColor = "#ff7a00", string centerColor = "#ffaa00")
        {
            CreateNetObject($"<size={FontSizeAbsolute}><{midColor}>★", position);
        }

        public override void OnMeeting() => Despawn();
    }
}
