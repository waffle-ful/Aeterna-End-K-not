using System;
using System.Collections.Generic;
using System.Text;
using Hazel;
using UnityEngine;

namespace EndKnot.Modules;

// ゲーム開始の送信窓 (全員 Disconnected の set → 間隔 → 本人役職 → 復元) を 1 ゲーム 1 行に落とす計器。
// 送信ゼロ・ホストローカルログのみ。
//
// バニラ客のイントロが起動しない事象は「本人役職の SetRole を受けた瞬間に全員 Disconnected=true」という
// 客側の起動条件を外すと起きる。set と本人役職の間に実時間の間隔を空けることで大半は消えたが、残りが
// なぜ外れるのかは「中継での順序入れ替わり」と「set パケットの遅延/欠落」のどちらでも説明が付く。
// この 2 つは、窓の各段でホスト→サーバー間のリンク統計 (再送数 / 未 ACK 在庫 / ACK 無し ping 数 / RTT) を
// 並べれば分かれる — 後者なら発症ゲームで再送が立つ。
//
// ⚠️ ホストが見えるのはホスト→サーバー区間だけで、サーバー→客区間の欠落は原理的に観測できない。
//    再送ゼロのまま発症した場合、「中継での順序入れ替わり」と「サーバー→客の欠落」は区別が付かない。
//
// 静的状態は開始送信コルーチンが同時に 1 本しか走らない前提で持っている。
public static class StartWindowProbe
{
    private static int _gameSeq;
    private static float _beginTs;
    private static int _setChunks; // set の分割数は後続の SendGameData に上書きされるので、その場で控える
    private static readonly List<string> Phases = [];

    // 開始窓 [begin, restore+3s] にワイヤへ乗った全パケットを復号し、NetworkedPlayerInfo の Data ブロックの
    // flags (bit0=Disconnected / bit2=IsDead) をホストローカルに残す計器。「ログしない送信路が窓内に
    // Disconnected=false の Data を流していないか」を 1 ゲームで白黒付けるためのもの。送信には触らない。
    private static float _captureUntil;
    private static float _wireT0; // NPIWIRE の経過秒の基準 (Report 後も窓の残り 3 秒を同じ基準で出す)
    private static readonly Dictionary<uint, byte> NpiNetIds = [];
    private static bool Capturing => Time.realtimeSinceStartup < _captureUntil;

    /// <summary>開始コルーチンの送信段に入る直前に呼ぶ。</summary>
    public static void BeginGame()
    {
        _gameSeq++;
        _beginTs = Time.realtimeSinceStartup;
        _setChunks = 0;
        Phases.Clear();
        _captureUntil = _beginTs + 40f; // 窓が途中で終わっても取り続けない安全弁 (劣化時のドレイン待ち込みで最大 ~30s)
        _wireT0 = _beginTs;
        NpiNetIds.Clear();

        try
        {
            foreach (NetworkedPlayerInfo info in GameData.Instance.AllPlayers)
                if (info) NpiNetIds[info.NetId] = info.PlayerId;
        }
        catch { }

        MarkPhase("begin");
    }

    /// <summary>SendOrDisconnect の関所から毎回呼ぶ。窓の外では bool 1 つの比較で返る。</summary>
    public static void Inspect(MessageWriter msg)
    {
        if (!Capturing || msg == null) return;

        MessageReader reader = null, m = null, sub = null;

        try
        {
            var sb = new StringBuilder();
            reader = MessageReader.Get(msg.ToByteArray(false));

            while (reader.Position < reader.Length)
            {
                m = reader.ReadMessage();

                if (m.Tag is 5 or 6)
                {
                    m.ReadInt32();
                    int target = m.Tag == 6 ? m.ReadPackedInt32() : -1;
                    sb.Append($" tag{m.Tag}(to={target}):");

                    while (m.Position < m.Length)
                    {
                        sub = m.ReadMessage();

                        switch (sub.Tag)
                        {
                            case 1:
                            {
                                uint netId = sub.ReadPackedUInt32();

                                if (NpiNetIds.TryGetValue(netId, out byte pid))
                                    sb.Append($" npi{pid}={DecodeNpiFlags(sub)}");
                                else
                                    sb.Append($" data{netId}");

                                break;
                            }
                            case 2:
                            {
                                uint netId = sub.ReadPackedUInt32();
                                byte callId = sub.ReadByte();
                                sb.Append($" rpc{netId}:{callId}");
                                break;
                            }
                            case 4: sb.Append(" spawn"); break;
                            case 5: sb.Append($" despawn{sub.ReadPackedUInt32()}"); break;
                            default: sb.Append($" t{sub.Tag}"); break;
                        }

                        sub.Recycle();
                        sub = null;
                    }
                }
                else
                    sb.Append($" tag{m.Tag}(len={m.Length})");

                m.Recycle();
                m = null;
            }

            reader.Recycle();
            reader = null;
            Logger.Info($"NPIWIRE +{Time.realtimeSinceStartup - _wireT0:F2}s len={msg.Length} opt={msg.SendOption}{sb}", "StartWindowProbe");
        }
        catch (Exception e)
        {
            Logger.Info($"NPIWIRE decode failed: {e.Message}", "StartWindowProbe");
            try { sub?.Recycle(); m?.Recycle(); reader?.Recycle(); } catch { }
        }
    }

    // NetworkedPlayerInfo.Serialize(initialState=false) の並び (2026.8.18 逆アセンブルで確認):
    // byte playerId / packed clientId / byte outfitCount / [byte type, string name, packed color, string×5, byte×5]×n /
    // packed level / byte flags (bit0=Disconnected, bit2=IsDead) / ...
    private static string DecodeNpiFlags(MessageReader r)
    {
        try
        {
            r.ReadByte();
            r.ReadPackedUInt32();
            int outfits = r.ReadByte();

            for (var i = 0; i < outfits; i++)
            {
                r.ReadByte();
                r.ReadString();
                r.ReadPackedUInt32();
                for (var k = 0; k < 5; k++) r.ReadString();
                for (var k = 0; k < 5; k++) r.ReadByte();
            }

            r.ReadPackedUInt32();
            byte flags = r.ReadByte();
            return $"{((flags & 1) != 0 ? "D" : "c")}{((flags & 4) != 0 ? "x" : "a")}";
        }
        catch { return "?"; }
    }

    /// <summary>送信窓の節目でリンク統計のスナップショットを取る (set / gap / roles / restore)。</summary>
    public static void MarkPhase(string label)
    {
        if (_beginTs <= 0f) return;

        if (label == "set") _setChunks = Utils.LastSendGameDataChunks;

        try
        {
            string stats = HealthLog.TryGetNetStats(out int resent, out int relSent, out int ackd, out int pNoAck, out int ping)
                ? $"rsnd{resent}/unack{relSent - ackd}/pNoAck{pNoAck}/ping{ping}"
                : "nostats";

            Phases.Add($"{label}=+{Time.realtimeSinceStartup - _beginTs:F2}s({stats})");
        }
        catch { }
    }

    /// <summary>復元がワイヤに乗った後に呼ぶ。窓が途中で終わった時のために finally からも呼ばれるので、
    /// 2 回目以降は何もしない。途中終了のゲームは揃っている段だけを出す — 行が丸ごと消えると、
    /// 送信が飛んだ疑いのある一番見たい標本が無音で抜ける。</summary>
    public static void Report()
    {
        if (_beginTs <= 0f) return;

        try
        {
            Logger.Info($"STARTWINDOW game={_gameSeq} chunks={_setChunks} {string.Join(" ", Phases)}", "StartWindowProbe");
        }
        catch { }

        _captureUntil = Time.realtimeSinceStartup + 1f; // 復元の後は 1 秒だけ (以降はイントロ中の通常送信で、毎パケット復号する価値が無い)
        _beginTs = 0f;
    }
}
