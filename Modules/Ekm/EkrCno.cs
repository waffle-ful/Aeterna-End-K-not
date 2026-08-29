using System;
using UnityEngine;

namespace EndKnot.Modules.Ekm;

// cno_move/cno_despawn 系 opcode が EkrCno (テキスト) / EkrDummyCno (player-like・v1.1) のどちらにも
// 同じ呼び出しで効くようにするための抽象 (契約正典: docs/ekr-logic-spec.md §3 v1.1「cno_move / cno_despawn は
// ダミーにもそのまま効く」)。cno_show はこの interface に含めない — ダミーには no-op (EkrLogicOpcodes.CnoShow
// が `is not EkrCno` で弾く)。
internal interface IEkrSlotCno
{
    bool IsInstantiated { get; }
    void MoveToOffset(float dx, float dy);
    void Despawn();
}

// EKR logic 契約 v1 の汎用テキスト CNO (契約正典: docs/ekr-logic-spec.md §3 cno_*)。
// 既存の非 player-like ・単一文字/短文 CNO (Modules/CustomNetObject.SizeTest.cs,
// Modules/CustomNetObject.WaveCannon.cs の WaveCannonGate) と同じ Shapeshift-text 戦略に乗る。
// OnMeeting() は「置きっぱなしの CNO」については意図的に素通しする — 基底 CustomNetObject.OnMeeting() の
// 会議明け一斉復活エンジンにそのまま従う (MeetingNum ガード追加禁止・memory:
// cno-base-onmeeting-implicit-respawn-engine)。Wave 6 の発射体 (cno_launch 済み) だけがこの engine から
// 離脱する (下の OnMeeting override 参照)。
public sealed class EkrCno : CustomNetObject, IEkrSlotCno
{
    private readonly string _sprite;

    // Wave 6 (docs/ekn-wave6-contract.md §1.1): cno_launch で飛行に入った実体。以後この CNO は
    // 「消えるのが自然な弾」として扱われ、飛行終了 (壁/40u/10秒) と中断 (会議・追放演出・ゲーム終了・
    // ホルダー死亡/切断/slot 剥奪) で必ず Despawn される。一度立ったら降りない (弾は再利用しない)。
    public bool Launched { get; private set; }

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

    // IEkrSlotCno.Despawn() は引数無し。基底 Despawn(bool canPool = true) とはアリティが異なるため
    // (既定値は「実装が同じシグネチャを名乗る」ことにはならない)、明示実装で既定値付きの公開 API に委譲する。
    void IEkrSlotCno.Despawn() => Despawn();

    // ── Wave 6 (docs/ekn-wave6-contract.md §1.1): 発射体モード ────────────────────────────────

    // 飛行エンジン (EkrManager) が 0.1 秒 tick で呼ぶ。Position を書いて ForceSnapSend を立てるだけで、
    // 実送信は基底 OnFixedUpdate が ForceSnapMinInterval (0.2秒 = 5Hz) に間引く (Snowball と同じ委譲形)。
    // SnapTo ラウンド予算 (Utils.NumSnapToCallsThisRound) は消費しない — 基底の CNO 移動は
    // StartRpcImmediately + SendOption.None の直書き経路 (CustomNetObject.cs:557-589)。
    public void FlyTo(Vector2 position)
    {
        Launched = true;
        TP(position);
    }

    // 壁判定の座標系合わせ (WallRayOffset / SelfCollider) は CustomNetObject 基底へ共通化済み
    // (2026-08-29 Wave6 実機で発覚 → 兄弟スイープで全 CNO 共通の罠と判明したため昇格)。

    // 飛行中だけ 5Hz へ間引く (Snowball.ForceSnapMinInterval と同値)。置きっぱなしの CNO は cno_move が
    // ≤2/秒/slot なので基底既定 (0f = 次フレーム即送信) のままにする — ここを一律 0.2f にすると
    // cno_move の反映が最大 0.2 秒遅れる無関係な挙動変化になる。
    protected override float ForceSnapMinInterval => Launched ? 0.2f : base.ForceSnapMinInterval;

    // 会議開始 +5 秒の全 CNO 一斉 OnMeeting (Patches/PlayerControlPatch.cs:1528-1538) は、会議開始時点の
    // AllObjects スナップショットを舐める。EkrManager.FireMeetingStart は同期でそれより先に走って飛行中の
    // 弾を Despawn するが、スナップショットには載ったままなので、基底 OnMeeting() に委ねると
    // 「消したはずの弾が会議明けに復活する」(基底は Despawn 済みでも復活コルーチンを起こす)。
    // memory: cno-base-onmeeting-implicit-respawn-engine が「基底から離脱したい CNO は override して
    // Despawn だけする」と定める作法にそのまま従う (EkrDummyCno の空 override と同じ位置付け — あちらは
    // EkrManager が唯一の消滅経路なので完全に空、こちらは念のため Despawn を撃つ)。
    public override void OnMeeting()
    {
        if (Launched)
        {
            Despawn();
            return;
        }

        base.OnMeeting();
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
