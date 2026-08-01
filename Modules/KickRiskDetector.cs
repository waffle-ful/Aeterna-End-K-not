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
    private static readonly Dictionary<uint, int> IdentityOwner = []; // 同一性 netId → 所有プレイヤーの client id (NPI の OwnerId は -2 なので pc 側を使う)
    private static readonly HashSet<uint> AllPlayerNetIds = []; // 上 + PlayerPhysics + CNT (P3 の対象)
    private static readonly HashSet<int> LiveClientIds = [];
    private static double SnapshotAt = double.NegativeInfinity;

    // --- オブジェクト族 (spawn が「自分自身」を宣言しているだけかの判定用) ---
    // 族 = 1 回の spawn メッセージが宣言する netId のまとまり。本体 {PlayerControl, PlayerPhysics, CNT} と
    // 同一性オブジェクト単体 {NetworkedPlayerInfo} の 2 種類があり、族の代表 netId で引く。
    private static readonly Dictionary<uint, uint> NetIdFamily = []; // netId → 族の代表 netId
    private static readonly Dictionary<uint, HashSet<uint>> Families = []; // 代表 netId → 族の全 netId
    private static readonly Dictionary<uint, int> FamilyOwner = []; // 代表 netId → 族の実所有クライアント id

    /// <summary>1 つの spawn が宣言した netId (走査中の作業用・毎回 Clear)。</summary>
    private static readonly List<uint> DeclaredNetIds = [];

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

                // P4 と同じ理由で「所有クライアントが接続中」まで確認する (退出者の残留ローカルオブジェクト対策)。
                if (PlayerControlNetIds.Contains(netId) && (!IdentityOwner.TryGetValue(netId, out int dataOwner) || Utils.GetClientById(dataOwner) != null))
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

                DeclaredNetIds.Clear();

                for (var i = 0; i < compCount && i < 16; i++)
                {
                    if (!TryReadPacked(buf, end, ref pos, out uint netId)) return;

                    DeclaredNetIds.Add(netId);
                    SpawnedThisConnection.Add(netId);

                    // コンポーネント本体 (1 サブメッセージ) を読み飛ばす
                    if (pos + 3 > end) return;
                    int len = buf[pos] | (buf[pos + 1] << 8);
                    pos += 3 + len;
                    if (len < 0 || pos > end) return;
                }

                // ⚠️ **そのオブジェクト自身の spawn は合法** (2026-08-01 に誤検知として確定)。
                // ホストは新規クライアントの入室時、既存オブジェクト全部の spawn を tag6 で流し直す (vanilla 経路)。
                // 送信時点でその netId/ownerId はローカルに実在するため、素朴に照合すると自分自身に必ず当たる
                // (実測: 入室 4 回に対し検出 4 回・P3:P2 = 1029:259 ≈ 4:1 = 1 プレイヤーあたり netId 4 個 : spawn 1 本)。
                // サーバーが禁じているのは「**他の**オブジェクトが他人の netId / ownerId を騙る」ことなので、
                // 宣言 netId 群が既知の族と**ちょうど一致**するかで 1 ビット分離する
                // (部分一致で許すと、保護 netId を 1 つだけ名乗る合成 spawn = P3 の陽性アーム 25B を取り逃がす)。
                uint selfFamily = MatchFamily();

                if (selfFamily == 0 || !FamilyOwner.TryGetValue(selfFamily, out int trueOwner) || trueOwner != ownerId)
                {
                    // P4 と同じ理由で実クライアント表も引く (LiveClientIds は退出者の残留オブジェクト由来で腐りうる)。
                    if (LiveClientIds.Contains(ownerId) && Utils.GetClientById(ownerId) != null)
                        Report("P2", $"Spawn(tag4) declares ownerId={ownerId}, which is a CONNECTED client", packetLen);
                }

                if (selfFamily == 0)
                {
                    foreach (uint netId in DeclaredNetIds)
                    {
                        if (AllPlayerNetIds.Contains(netId))
                            Report("P3", $"Spawn(tag4) re-declares protected netId={netId} (a live player's object)", packetLen);
                    }
                }

                break;
            }

            // Despawn — 生きているプレイヤーの同一性オブジェクト (P4) / 未 spawn netId のブロードキャスト (P5)。
            case 5:
            {
                if (!TryReadPacked(buf, end, ref pos, out uint netId)) return;

                if (IdentityNetIds.Contains(netId))
                {
                    // ⚠️ 退出クライアントの後片付け despawn は合法 (サーバーの保護表は切断と共に引かれる) だが、
                    // スナップショットはローカルオブジェクト由来で退出後も残る — "Repeat Despawn"
                    // (PlayerJoinAndLeftPatch, 離脱 2.5 秒後) はホスト側 .Despawn() を呼ばないため
                    // 実測 15/29 で自己ヒットしていた。送信時点で所有クライアントがまだ接続中のときだけ違法。
                    if (!IdentityOwner.TryGetValue(netId, out int owner) || Utils.GetClientById(owner) != null)
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
        IdentityOwner.Clear();
        AllPlayerNetIds.Clear();
        LiveClientIds.Clear();
        NetIdFamily.Clear();
        Families.Clear();
        FamilyOwner.Clear();

        // PlayerId>=200 は CNO (Main.EnumeratePlayerControls が除外済み)。CNO は保護対象ではない。
        foreach (PlayerControl pc in Main.EnumeratePlayerControls())
        {
            if (!pc || pc.OwnerId < 0) continue;

            LiveClientIds.Add(pc.OwnerId);
            PlayerControlNetIds.Add(pc.NetId);
            IdentityNetIds.Add(pc.NetId);
            IdentityOwner[pc.NetId] = pc.OwnerId;
            AllPlayerNetIds.Add(pc.NetId);

            // 本体族: 1 本の spawn メッセージが {PlayerControl, PlayerPhysics, CNT} をまとめて宣言する。
            HashSet<uint> body = [pc.NetId];

            try
            {
                if (pc.MyPhysics)
                {
                    AllPlayerNetIds.Add(pc.MyPhysics.NetId);
                    body.Add(pc.MyPhysics.NetId);
                }

                if (pc.NetTransform)
                {
                    AllPlayerNetIds.Add(pc.NetTransform.NetId);
                    body.Add(pc.NetTransform.NetId);
                }

                // ⚠️ GameData 未登録の PlayerControl では Data のゲッター自体が例外を投げる
                // (2026-08-01 に /nest で踏んだ)。`!= null` では防げないので try で囲う。
                if (pc.Data != null)
                {
                    IdentityNetIds.Add(pc.Data.NetId);
                    IdentityOwner[pc.Data.NetId] = pc.OwnerId;
                    AllPlayerNetIds.Add(pc.Data.NetId);

                    // 同一性族: NetworkedPlayerInfo は別 spawn で単体宣言される (ownerId は -2)。
                    uint dataNetId = pc.Data.NetId;
                    Families[dataNetId] = [dataNetId];
                    FamilyOwner[dataNetId] = pc.Data.OwnerId;
                    NetIdFamily[dataNetId] = dataNetId;
                }
            }
            catch { /* best-effort */ }

            Families[pc.NetId] = body;
            FamilyOwner[pc.NetId] = pc.OwnerId;
            foreach (uint netId in body) NetIdFamily[netId] = pc.NetId;
        }
    }

    /// <summary><see cref="DeclaredNetIds"/> が既知の族と**ちょうど一致**するならその族の代表 netId を返す
    /// (0 = 不一致 = 「自分自身の spawn」ではない)。netId は 1 始まりなので 0 をセンチネルに使える。</summary>
    private static uint MatchFamily()
    {
        if (DeclaredNetIds.Count == 0) return 0;
        if (!NetIdFamily.TryGetValue(DeclaredNetIds[0], out uint family)) return 0;
        if (!Families.TryGetValue(family, out HashSet<uint> members)) return 0;
        if (members.Count != DeclaredNetIds.Count) return 0;

        for (var i = 0; i < DeclaredNetIds.Count; i++)
        {
            if (!members.Contains(DeclaredNetIds[i])) return 0;
        }

        return family;
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
