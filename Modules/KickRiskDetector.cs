using System;
using System.Collections.Generic;
using System.Diagnostics;
using Hazel;

namespace EndKnot.Modules;

/// <summary>
/// 公式サーバーで <c>reason=Hacking</c> キックを 100% 引き起こすことが 2026-08-01 の実機実験で確定した
/// 5 つの送信パターン (P1〜P5) を、送信直前に検出して**ログに残すだけ**の計器。ブロックはしない。
///
/// 目的: 本番 (11〜13 人) でキックが起きたとき、「どのパターンが混入したか」を DCRING より先に、
/// かつ**キックされずに生き残った窓でも**恒久チャネルへ残すこと。
/// 静的スイープでは本番コードに違反ゼロだったので、これが鳴るなら
/// 「動的にしか成立しない経路」(= 探していたもの) が鳴っている。
///
/// 確定ルール (詳細と実験ログは docs/official-server-model.md):
///   P1 Data(tag1)   → 生きているプレイヤーの PlayerControl netId
///   P2 Spawn(tag4)  → ownerId が接続中クライアントの id
///   P3 Spawn(tag4)  → 保護対象 netId (生きているプレイヤーの PC/Physics/CNT/NPI) の再宣言
///   P4 Despawn(t5)  → 生きているプレイヤーの PlayerControl または NetworkedPlayerInfo
///   P5 Despawn(t5)  → **ブロードキャスト**で、この接続で spawn を送っていない netId
///
/// ⚠️ P5 だけはサーバー側の知識を推測している。この接続で送信した spawn の netId を自前で覚え、
/// 再接続時にリセットする (PacketRateGate.DetectReconnect が待機 Reliable を捨てるのと同じ瞬間)。
/// = 「再接続をまたいで古い netId を Despawn する」という最有力リードを直接検出する。
/// </summary>
internal static class KickRiskDetector
{
    /// <summary>false にすると走査ごと止まる (ホットパスのコストを完全にゼロにする逃げ道)。</summary>
    public static bool Enabled = true;

    /// <summary>1 パケットあたりの走査打ち切り (計器が送信ホットパスを重くしないための保険)。</summary>
    private const int ScanCap = 128;

    /// <summary>同一パターンの再ログ間隔 (秒)。初回は即出す。</summary>
    private const double RelogSeconds = 30d;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    // --- 保護対象スナップショット (1 秒キャッシュ) ---
    private static readonly HashSet<uint> PlayerControlNetIds = [];
    private static readonly HashSet<uint> IdentityNetIds = []; // PlayerControl + NetworkedPlayerInfo (P4 の対象)
    private static readonly HashSet<uint> AllPlayerNetIds = []; // 上 + PlayerPhysics + CNT (P3 の対象)
    private static readonly HashSet<int> LiveClientIds = [];
    private static double SnapshotAt = double.NegativeInfinity;

    /// <summary>この接続で spawn メッセージを送った netId (P5 用)。</summary>
    private static readonly HashSet<uint> SpawnedThisConnection = [];

    private static readonly Dictionary<string, (int Count, double LastLog)> Seen = [];

    /// <summary>再接続でサーバー側の netId 表が作り直された = こちらの記憶も捨てる。
    /// PacketRateGate.DetectReconnect (待機 Reliable を捨てる箇所) から呼ぶ。</summary>
    public static void OnConnectionReset()
    {
        SpawnedThisConnection.Clear();
        SnapshotAt = double.NegativeInfinity;
    }

    /// <summary>関所 (SendOrDisconnect Prefix) の計装から毎パケット呼ぶ。例外は握り潰す。</summary>
    public static void Scan(MessageWriter msg)
    {
        if (!Enabled) return;

        try
        {
            // 移動同期の Unreliable ホットパスには触れない。非公式鯖にはこのルールが無い。
            if (msg.SendOption != SendOption.Reliable) return;
            if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
            if (GameStates.CurrentServerType != GameStates.ServerType.Vanilla) return;

            byte[] buf = msg.Buffer;
            if (buf == null) return;

            int headerLen = msg.SendOption == SendOption.Reliable ? 3 : 1;
            int limit = Math.Min(msg.Length, buf.Length);
            int pos = headerLen;
            var budget = ScanCap;

            // トップレベルには複数のメッセージが並びうる (PacketSplitPatch と同じ前提)。
            while (pos + 3 <= limit && budget > 0)
            {
                int subLen = buf[pos] | (buf[pos + 1] << 8);
                byte tag = buf[pos + 2];
                int body = pos + 3;
                int end = body + subLen;
                if (subLen < 0 || end > limit) break; // レイアウト不整合 → best-effort に諦める

                ScanTop(buf, body, end, tag, msg.Length, ref budget);
                pos = end;
            }
        }
        catch { /* 計器は送信を止めてはいけない */ }
    }

    // tag5 = GameData (ブロードキャスト) / tag6 = GameDataTo (宛先指定) / tag26 = パッキング用エンベロープ
    private static void ScanTop(byte[] buf, int pos, int end, byte tag, int packetLen, ref int budget)
    {
        switch (tag)
        {
            case 5:
                pos += 4; // gameId (int32)
                ScanLeaves(buf, pos, end, broadcast: true, packetLen, ref budget);
                break;
            case 6:
                pos += 4; // gameId (int32)
                if (!TryReadPacked(buf, end, ref pos, out _)) return;
                ScanLeaves(buf, pos, end, broadcast: false, packetLen, ref budget);
                break;
            case 26:
                if (!TryReadPacked(buf, end, ref pos, out _)) return;

                // 中身は tag5/tag6 のエンベロープ列。もう一段潜る。
                while (pos + 3 <= end && budget > 0)
                {
                    int len = buf[pos] | (buf[pos + 1] << 8);
                    byte t = buf[pos + 2];
                    int b = pos + 3;
                    int e = b + len;
                    if (len < 0 || e > end) break;
                    if (t is 5 or 6) ScanTop(buf, b, e, t, packetLen, ref budget);
                    pos = e;
                }

                break;
        }
    }

    private static void ScanLeaves(byte[] buf, int pos, int end, bool broadcast, int packetLen, ref int budget)
    {
        while (pos + 3 <= end && budget > 0)
        {
            int len = buf[pos] | (buf[pos + 1] << 8);
            byte t = buf[pos + 2];
            int body = pos + 3;
            int leafEnd = body + len;
            if (len < 0 || leafEnd > end) break;

            budget--;
            ScanLeaf(buf, body, leafEnd, t, broadcast, packetLen);
            pos = leafEnd;
        }
    }

    private static void ScanLeaf(byte[] buf, int pos, int end, byte tag, bool broadcast, int packetLen)
    {
        EnsureSnapshot();

        switch (tag)
        {
            // Data — 違法なのは「生きているプレイヤーの PlayerControl」だけ。
            // NetworkedPlayerInfo / CustomNetworkTransform / PlayerPhysics 宛は本番の通常トラフィックで合法。
            case 1:
            {
                if (!TryReadPacked(buf, end, ref pos, out uint netId)) return;
                if (PlayerControlNetIds.Contains(netId))
                    Report("P1", $"Data(tag1) targets a LIVE player's PlayerControl netId={netId}", packetLen);

                break;
            }

            // Spawn — ownerId が接続中クライアント (P2) / 保護対象 netId の再宣言 (P3)。
            case 4:
            {
                if (!TryReadPacked(buf, end, ref pos, out _)) return; // spawnId
                if (!TryReadPacked(buf, end, ref pos, out uint rawOwner)) return;
                var ownerId = (int)rawOwner;
                if (pos >= end) return;
                pos++; // SpawnFlags (byte)
                if (!TryReadPacked(buf, end, ref pos, out uint compCount)) return;

                if (LiveClientIds.Contains(ownerId))
                    Report("P2", $"Spawn(tag4) declares ownerId={ownerId}, which is a CONNECTED client", packetLen);

                for (var i = 0; i < compCount && i < 16; i++)
                {
                    if (!TryReadPacked(buf, end, ref pos, out uint netId)) return;

                    if (AllPlayerNetIds.Contains(netId))
                        Report("P3", $"Spawn(tag4) re-declares protected netId={netId} (a live player's object)", packetLen);

                    SpawnedThisConnection.Add(netId);

                    // コンポーネント本体 (1 サブメッセージ) を読み飛ばす
                    if (pos + 3 > end) return;
                    int len = buf[pos] | (buf[pos + 1] << 8);
                    pos += 3 + len;
                    if (len < 0 || pos > end) return;
                }

                break;
            }

            // Despawn — 生きているプレイヤーの同一性オブジェクト (P4) / 未 spawn netId のブロードキャスト (P5)。
            case 5:
            {
                if (!TryReadPacked(buf, end, ref pos, out uint netId)) return;

                if (IdentityNetIds.Contains(netId))
                {
                    Report("P4", $"Despawn targets a LIVE player's identity object netId={netId}", packetLen);
                    return;
                }

                // P5 は「サーバーがその netId を知らない」ことが条件。こちらが**この接続で spawn を送った**
                // かどうかで近似する。再接続をまたいだ古い CNO の Despawn がここに引っかかる想定。
                if (broadcast && !SpawnedThisConnection.Contains(netId))
                    Report("P5", $"broadcast Despawn of netId={netId}, which was never spawned on this connection", packetLen);

                break;
            }
        }
    }

    private static void EnsureSnapshot()
    {
        double now = Clock.Elapsed.TotalSeconds;
        if (now - SnapshotAt < 1d) return;

        SnapshotAt = now;
        PlayerControlNetIds.Clear();
        IdentityNetIds.Clear();
        AllPlayerNetIds.Clear();
        LiveClientIds.Clear();

        // PlayerId>=200 は CNO (Main.EnumeratePlayerControls が除外済み)。CNO は保護対象ではない。
        foreach (PlayerControl pc in Main.EnumeratePlayerControls())
        {
            if (!pc || pc.OwnerId < 0) continue;

            LiveClientIds.Add(pc.OwnerId);
            PlayerControlNetIds.Add(pc.NetId);
            IdentityNetIds.Add(pc.NetId);
            AllPlayerNetIds.Add(pc.NetId);

            try
            {
                if (pc.MyPhysics) AllPlayerNetIds.Add(pc.MyPhysics.NetId);
                if (pc.NetTransform) AllPlayerNetIds.Add(pc.NetTransform.NetId);

                // ⚠️ GameData 未登録の PlayerControl では Data のゲッター自体が例外を投げる
                // (2026-08-01 に /nest で踏んだ)。`!= null` では防げないので try で囲う。
                if (pc.Data != null)
                {
                    IdentityNetIds.Add(pc.Data.NetId);
                    AllPlayerNetIds.Add(pc.Data.NetId);
                }
            }
            catch { /* best-effort */ }
        }
    }

    private static void Report(string pattern, string detail, int packetLen)
    {
        double now = Clock.Elapsed.TotalSeconds;
        Seen.TryGetValue(pattern, out (int Count, double LastLog) prev);
        int count = prev.Count + 1;

        // 初回は即・以後は RelogSeconds ごと (件数を添えて) — 恒久チャネルを溢れさせない。
        if (count > 1 && now - prev.LastLog < RelogSeconds)
        {
            Seen[pattern] = (count, prev.LastLog);
            return;
        }

        Seen[pattern] = (count, now);
        string line = $"KICKRISK {pattern} #{count} len={packetLen} {detail} — this pattern is a confirmed 100% Hacking kick on official servers (docs/official-server-model.md)";
        HealthLog.NoteAnom(line);
        Logger.Warn(line, "KickRiskDetector");
    }

    /// <summary>Hazel の WritePacked (7bit varint) を値付きで読む。負値は uint として書かれている
    /// (例: ownerId=-2 → 0xFFFFFFFE の 5 バイト) ので、int へキャストして解釈する。</summary>
    private static bool TryReadPacked(byte[] buf, int end, ref int pos, out uint value)
    {
        value = 0;
        var shift = 0;

        for (var i = 0; i < 5; i++)
        {
            if (pos >= end) return false;

            byte b = buf[pos++];
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;

            shift += 7;
        }

        return false;
    }
}
