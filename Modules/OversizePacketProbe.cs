using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Hazel;

namespace EndKnot.Modules;

// 公式鯖は単一メッセージが大きすぎると host を切断する。分割の関所 (SendOrDisconnect の Prefix) には
// 「何バイトだったか」しか残っていないので、閾値を超えた 1 本だけ中身を復号し、併せて
// マネージドの呼び出し元も控える。どの送信路が特大の 1 本を作ったのかは、この 2 つが揃わないと決まらない。
//
// 送信には一切触らない (読むだけ)。閾値以下は長さの比較 1 回で返る。
public static class OversizePacketProbe
{
    // 分割の検知点 (1000B) より上に置く。通常の分割対象まで吐くと、本当に異常な 1 本が埋もれる。
    private const int DumpThreshold = 2000;

    // 1 セッションの上限。壊れた送信路は毎フレーム同じものを作るので、上限を切らないとログが流れる。
    private const int MaxDumps = 8;

    private static int Dumps;

    public static void Inspect(MessageWriter msg)
    {
        if (msg == null || Dumps >= MaxDumps) return;

        int len;
        try { len = msg.Length; }
        catch { return; }

        if (len < DumpThreshold) return;

        Dumps++;

        try { Logger.Warn($"OVERSIZE #{Dumps} len={len} opt={msg.SendOption}{Decode(msg)}", "OversizePacketProbe"); }
        catch (Exception e) { Logger.Warn($"OVERSIZE #{Dumps} len={len} decode failed: {e.Message}", "OversizePacketProbe"); }

        try { Logger.Warn($"OVERSIZE #{Dumps} from:{Caller()}", "OversizePacketProbe"); }
        catch { /* 呼び出し元が取れなくても中身の復号は残す */ }
    }

    // 子メッセージを種類別に数えて 1 行にまとめる。個々を全部並べると 11KB 級では読めないので、
    // 「どの種類が何本で合計何バイトか」まで畳む。
    private static string Decode(MessageWriter msg)
    {
        MessageReader reader = null, m = null, sub = null;

        try
        {
            var sb = new StringBuilder();
            reader = MessageReader.Get(msg.ToByteArray(false));
            var tops = 0;

            while (reader.Position < reader.Length)
            {
                m = reader.ReadMessage();
                tops++;

                if (m.Tag is 5 or 6)
                {
                    m.ReadInt32();
                    int target = m.Tag == 6 ? m.ReadPackedInt32() : -1;

                    var kinds = new SortedDictionary<string, (int count, int bytes)>();
                    var children = 0;

                    while (m.Position < m.Length)
                    {
                        sub = m.ReadMessage();
                        children++;
                        int subLen = sub.Length;
                        string key;

                        switch (sub.Tag)
                        {
                            case 1:
                                key = $"data{sub.ReadPackedUInt32()}";
                                break;
                            case 2:
                            {
                                uint netId = sub.ReadPackedUInt32();
                                byte callId = sub.ReadByte();
                                key = $"rpc{netId}:{callId}";
                                break;
                            }
                            case 4:
                                key = "spawn";
                                break;
                            case 5:
                                key = $"despawn{sub.ReadPackedUInt32()}";
                                break;
                            default:
                                key = $"t{sub.Tag}";
                                break;
                        }

                        kinds.TryGetValue(key, out (int count, int bytes) agg);
                        kinds[key] = (agg.count + 1, agg.bytes + subLen);

                        sub.Recycle();
                        sub = null;
                    }

                    sb.Append($" tag{m.Tag}(to={target} len={m.Length} children={children}):");
                    foreach (KeyValuePair<string, (int count, int bytes)> k in kinds)
                        sb.Append($" {k.Key}x{k.Value.count}/{k.Value.bytes}B");
                }
                else
                    sb.Append($" tag{m.Tag}(len={m.Length})");

                m.Recycle();
                m = null;
            }

            reader.Recycle();
            reader = null;
            return $" tops={tops}{sb}";
        }
        catch
        {
            try { sub?.Recycle(); m?.Recycle(); reader?.Recycle(); } catch { /* best-effort */ }
            throw;
        }
    }

    // 送信路の特定に要るのは「誰が SendOrDisconnect を呼んだか」だけなので、関所自身の枠を飛ばして
    // 上から数フレームを型名.メソッド名で並べる。
    private static string Caller()
    {
        var trace = new StackTrace(2, false);
        var sb = new StringBuilder();
        int frames = Math.Min(trace.FrameCount, 12);

        for (var i = 0; i < frames; i++)
        {
            System.Reflection.MethodBase method = trace.GetFrame(i)?.GetMethod();
            if (method == null) continue;

            sb.Append(' ');
            sb.Append(method.DeclaringType?.Name ?? "?");
            sb.Append('.');
            sb.Append(method.Name);
        }

        return sb.Length > 0 ? sb.ToString() : " (unavailable)";
    }
}
