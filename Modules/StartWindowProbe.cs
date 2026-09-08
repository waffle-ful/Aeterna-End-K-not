using System.Collections.Generic;
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

    /// <summary>開始コルーチンの送信段に入る直前に呼ぶ。</summary>
    public static void BeginGame()
    {
        _gameSeq++;
        _beginTs = Time.realtimeSinceStartup;
        _setChunks = 0;
        Phases.Clear();
        MarkPhase("begin");
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

        _beginTs = 0f;
    }
}
