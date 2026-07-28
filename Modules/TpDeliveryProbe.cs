using System;
using System.Collections.Generic;
using Hazel;
using UnityEngine;

namespace EndKnot.Modules;

// TP 着弾検証計器: リモートプレイヤーへ SnapTo を送った直後、そのクライアントからの位置エコーが
// ホストに「受理」されたかで、SnapTo が実際に届いたかを統計判定する (公式鯖がレート超過を
// 無言ドロップした場合の可視化)。
//
// 一次判定は sid の前進: Utils.TP は SnapTo でホスト側 lastSequenceId を +328 へ進め、RPC には
// さらに +8 した sid を載せる。着弾したクライアントはその sid 起点でエコーを返しホスト側
// lastSequenceId を前進させるが、未着弾クライアントのエコーは旧 sid のまま sid ゲートで棄却され
// 続け、lastSequenceId は武装時の値から一切動かない (自然追い付きは +328 を埋める必要があり
// 数秒〜十秒級 = 3.5秒の判定窓内には起き得ない。cap100 実機実験 2026-07-21 の「数秒間の位置乖離
// →自己回復」はこの追い付き遅延の観測)。
// ⚠️ pc.Pos() を一次判定に使ってはいけない: 受信キューが空だと transform.position (=ホストが
// SnapTo 済みの転送先) にフォールバックするため、未着弾のケースほど「着弾」に見える
// (2026-07-28 pitfall 監査で検出)。位置の相対距離比較 (マージン 1.0u) は受信キューが実データを
// 持つときの二次確認にのみ使う。
//
// どちらとも言えないサンプルは ambiguous として捨てる (AFKDetector の位置判定誤検知類型
// = 正当な理由で想定位置に居ないケースを白黒つけない)。転送距離 5u 未満は位置確認が混ざるため
// 武装しない。送信ゼロ・ホストローカルの観測専用レイヤー (EarlyWarning と同方針)。
//
// 計器の盲点 (仕様):
// - 計装は Utils.TP のみ。Freeze/RevertSnapToFreeze・RpcMakeInvisible の点滅 TP・Phantom 系など
//   他の SnapTo 経路は対象外。
// - 1.2秒未満間隔で同一プレイヤーを連続 TP する引きずり型は、probe が熟す前に上書きされ続け
//   「チェーン最後の 1 hop」だけがサンプルになる (バースト毎に 1 サンプルは必ず取れる)。
// - none= バケットは cap 80-99 帯の None 降格のみ (距離 <1.5u の None 降格は 5u 未満なので武装外)。
//   none=0/0 は「None 送信が無かった」ことを意味しない。
public static class TpDeliveryProbe
{
    private const float MinDecidableDistance = 5f;   // これ未満の TP は元位置/転送先が数秒の歩行で混ざるため武装しない
    private const float EvalMinAgeSeconds = 1.2f;    // ping + 補間が落ち着くまで待つ
    private const float EvalMaxAgeSeconds = 3.5f;    // これを超えたら窓を逃した (フレームストール等) として破棄
    private const float VerdictMarginUnits = 1f;
    private const long StatLineIntervalSeconds = 120;
    private const long DropAlarmWindowSeconds = 120;
    private const long DropAlarmDedupeSeconds = 60;

    private static readonly Dictionary<byte, Probe> Probes = new();

    // 累積カウンタ (セッション通算)。rel=Reliable / none=None降格帯 (80-99) の別集計
    private static int _deliveredReliable, _undeliveredReliable;
    private static int _deliveredNone, _undeliveredNone;
    private static int _ambiguous;

    // 無言ドロップ警報用のローリング窓 (Reliable の decided のみ)
    private static long _windowStartTs;
    private static int _windowDelivered, _windowUndelivered;

    private static long _lastStatLineTs;
    private static int _lastStatDecidedCount;
    private static long _lastDropAlarmTs;

    /// <summary>Utils.TP の送信成功直後に呼ぶ。origin は SnapTo 適用前のホスト側位置。</summary>
    public static void Arm(PlayerControl pc, Vector2 origin, Vector2 dest, SendOption sendOption)
    {
        try
        {
            if (pc == null || pc.AmOwner) return;
            if (pc.GetClient() == null) return; // 実クライアントの居ないボットは位置エコーが無く常に「着弾」に見える
            if (Vector2.Distance(origin, dest) < MinDecidableDistance) return;

            Probes[pc.PlayerId] = new Probe
            {
                Origin = origin,
                Dest = dest,
                ArmedAt = Time.time,
                ArmedSid = pc.NetTransform.lastSequenceId, // SnapTo 適用後 = 旧sid+328。エコー受理でのみ前進する
                Reliable = sendOption == SendOption.Reliable
            };
        }
        catch { }
    }

    /// <summary>1/sec 呼び出し (EarlyWarning.Tick から)。熟した probe を判定し、統計行と警報を出す。</summary>
    public static void Tick()
    {
        try
        {
            if (Probes.Count > 0) EvaluateRipeProbes();
            EmitStatLineIfDue();
        }
        catch { }
    }

    private static void EvaluateRipeProbes()
    {
        List<byte> done = null;

        foreach (KeyValuePair<byte, Probe> kv in Probes)
        {
            Probe probe = kv.Value;
            float age = Time.time - probe.ArmedAt;
            if (age < EvalMinAgeSeconds) continue;

            (done ??= []).Add(kv.Key);

            // 窓を逃した/位置ストリームが信用できない状態は白黒つけず捨てる。
            // IsAlive はポストゲームロビーの GM/観戦ホストで常に false (memory: isalive-false-in-postgame-lobby)
            // なので、ロビー中 (Backrooms の TP は正規サポート対象) は短絡して生存判定を見ない。
            PlayerControl pc = kv.Key.GetPlayer();

            if (age > EvalMaxAgeSeconds || GameStates.IsMeeting || pc == null || (!GameStates.IsLobby && !pc.IsAlive()) || pc.inVent || pc.GetClient() == null)
            {
                _ambiguous++;
                continue;
            }

            // 一次判定 = sid の前進 (ヘッダコメント参照)。武装時から一切動いていない = クライアントの
            // エコーが 1 本も受理されていない = SnapTo 未着弾 (他のホスト側 SnapTo 経路も走っていない)。
            var nt = pc.NetTransform;

            if (nt == null)
            {
                _ambiguous++;
                continue;
            }

            if (nt.lastSequenceId == probe.ArmedSid)
            {
                Count(probe.Reliable, delivered: false, pc);
                continue;
            }

            // sid が動いた場合、着弾エコーか別のホスト側 SnapTo (Freeze/invis 点滅等も +328 する) かは
            // sid 単体では区別できない。受信キューに実データがあるときだけ位置の相対距離で二次確認し、
            // キュー空/pause (= Pos() が transform フォールバックする状態) は白黒つけない。
            if (nt.isPaused || nt.incomingPosQueue == null || nt.incomingPosQueue.Count == 0)
            {
                _ambiguous++;
                continue;
            }

            Vector2 pos = pc.Pos();
            float dDest = Vector2.Distance(pos, probe.Dest);
            float dOrigin = Vector2.Distance(pos, probe.Origin);

            if (dDest + VerdictMarginUnits < dOrigin) Count(probe.Reliable, delivered: true);
            else if (dOrigin + VerdictMarginUnits < dDest) Count(probe.Reliable, delivered: false, pc);
            else _ambiguous++;
        }

        if (done != null)
            foreach (byte id in done)
                Probes.Remove(id);
    }

    private static void Count(bool reliable, bool delivered, PlayerControl pc = null)
    {
        long now = Utils.TimeStamp;

        if (!reliable)
        {
            if (delivered) _deliveredNone++;
            else _undeliveredNone++;

            return;
        }

        if (now - _windowStartTs > DropAlarmWindowSeconds)
        {
            _windowStartTs = now;
            _windowDelivered = 0;
            _windowUndelivered = 0;
        }

        if (delivered)
        {
            _deliveredReliable++;
            _windowDelivered++;
            return;
        }

        _undeliveredReliable++;
        _windowUndelivered++;
        Logger.Warn($"Reliable SnapTo likely not delivered to {pc?.GetRealName()} (id {pc?.PlayerId})", "TpProbe");

        // 無言ドロップ警報: 窓内で未着弾2件以上かつ decided の過半 (単発のパケロスと区別する)
        if (_windowUndelivered >= 2 && _windowUndelivered * 2 >= _windowUndelivered + _windowDelivered && now - _lastDropAlarmTs >= DropAlarmDedupeSeconds)
        {
            _lastDropAlarmTs = now;
            HealthLog.NoteAnom($"WARN kind=tpdrop windowDel={_windowDelivered} windowUndel={_windowUndelivered} refill={Utils.SnapToRefillSecondsPerToken} t={now}");
        }
    }

    // 新しい decided があるときだけ 120 秒毎に累積統計を Timeline へ流す。
    // refill 併記はキック事後検証用 (「その時の refill 値」を Timeline 単体で追えるように)。
    private static void EmitStatLineIfDue()
    {
        int decided = _deliveredReliable + _undeliveredReliable + _deliveredNone + _undeliveredNone;
        long now = Utils.TimeStamp;

        if (decided == _lastStatDecidedCount || now - _lastStatLineTs < StatLineIntervalSeconds) return;

        _lastStatDecidedCount = decided;
        _lastStatLineTs = now;
        HealthLog.NoteAnom($"TPPROBE rel={_deliveredReliable}/{_undeliveredReliable} none={_deliveredNone}/{_undeliveredNone} ambig={_ambiguous} refill={Utils.SnapToRefillSecondsPerToken} t={now}");
    }

    public static string GetStatsForTpDbg()
    {
        return $"probe rel={_deliveredReliable}/{_undeliveredReliable} none={_deliveredNone}/{_undeliveredNone} ambig={_ambiguous} armed={Probes.Count}";
    }

    private class Probe
    {
        public Vector2 Origin { get; init; }
        public Vector2 Dest { get; init; }
        public float ArmedAt { get; init; }
        public ushort ArmedSid { get; init; }
        public bool Reliable { get; init; }
    }
}
