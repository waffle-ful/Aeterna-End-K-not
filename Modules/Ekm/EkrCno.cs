using System;
using UnityEngine;

namespace EndKnot.Modules.Ekm;

// EKR logic 契約 v1 の汎用テキスト CNO (契約正典: docs/ekr-logic-spec.md §3 cno_*)。
// 既存の非 player-like ・単一文字/短文 CNO (Modules/CustomNetObject.SizeTest.cs,
// Modules/CustomNetObject.WaveCannon.cs の WaveCannonGate) と同じ Shapeshift-text 戦略に乗る。
// OnMeeting() は意図的に override しない — 基底 CustomNetObject.OnMeeting() の会議明け一斉復活エンジンに
// そのまま従う (MeetingNum ガード追加禁止・memory: cno-base-onmeeting-implicit-respawn-engine)。
public sealed class EkrCno : CustomNetObject
{
    private readonly string _sprite;

    // cno_move の dx/dy は「毎回の相対移動」ではなく、この spawn 時アンカーからの絶対オフセットとして
    // 解決する (spec §3 裁定準拠)。理由: on_second 配下で毎秒呼ぶ想定 (エディタ L1 の代替案そのもの) の
    // オブジェクトが、呼ぶたびに加算される設計だと数秒でマップ外まで暴走しうる。アンカー基準なら同じ
    // (dx,dy) の再呼び出しは冪等で安全。
    public Vector2 SpawnAnchor { get; }

    // 実体化前 (spawn コルーチンが Object.Instantiate に到達する前) は基底の playerControl が null のまま。
    // 基底 spawn コルーチンは Despawn で止まらないため、実体化前の cno_show/cno_despawn/同一 slot 再
    // cno_spawn はドロップ (no-op) する (spec §5 孤児コルーチン防止裁定・2026-08-09)。
    public bool IsInstantiated => playerControl;

    public EkrCno(Vector2 position, string text, int size, string colorHex)
    {
        SpawnAnchor = position;

        // <size=N> は絶対値のみ (memory: cno_size_absolute_mode_policy — % は ~600% で飽和し 700%+ は
        // 非モッド描画破壊の報告あり)。spec の size 1..12 から実際の TMP 絶対値へのマッピングは
        // 規範化されていない実装判断: RpcChangeSprite の名前非表示プレフィックスが <size=14> を
        // 「素のネームプレート相当」の基準として使っている実績を踏まえ、8..56 のレンジに写像する。
        int renderSize = 8 + (Math.Clamp(size, 1, 12) * 4);
        _sprite = $"<size={renderSize}><color={colorHex}>{text}</color></size>";

        CreateNetObject(_sprite, position);
    }

    // 基底 TP() (SnapTo throttle 込み) にそのまま乗る。毎フレーム TP 経路ではなく cno_move opcode
    // (≤2/秒/slot) からしか呼ばれない。
    public void MoveToOffset(float dx, float dy)
    {
        TP(SpawnAnchor + new Vector2(dx, dy));
    }

    // 「見せる相手を変える」は un-hide API が基底に無いため、despawn + 同じ sprite/position で
    // 再 spawn することでしか実現できない (Hide() は一方向)。spawn と同じ費用 (per-player fan-out への
    // 再突入) だが、despawn→respawn の未課金コスト分を織り込んで spawn より厳しい独自バケット
    // (≤1/3秒/ホルダー) を呼び出し側 (EkrLogicOpcodes.CnoShow) で強制する (spec §5 2026-08-09 監査改定)。
    public void SetVisibility(bool selfOnly, PlayerControl holder)
    {
        Despawn(canPool: false);
        CreateNetObject(_sprite, Position, onlyVisibleTo: selfOnly ? holder : null);
    }
}
