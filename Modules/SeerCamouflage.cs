using System.Collections.Generic;
using Hazel;
using InnerNet;

namespace EndKnot.Modules;

/// <summary>
/// 決めた人の画面でだけ、他の全員を同じ見た目 (灰色・装飾なし・名前なし) にする (ホストのみ)。
///
/// 本編の客は、プレイヤーのデータ (NetworkedPlayerInfo) を受けても見た目を描き直さない。描き直すのは変身の電文を受けた時。
/// そこで見る人の客にだけ、宛先指定 (tag6) で「見た目を替えたデータ + 自分への変身 (演出なし)」を送る。
/// データはホストの手元の物を一時的に替えて書き出し、すぐ元へ戻す (ホストの手元と他の客には何も変わらない)。
/// 名前は名前の組み立て (NotifyRoles) で隠す。ホストが見る人の時は、手元で見た目だけ描き替える。
///
/// 限り: 本物の変身・会議・他の見た目の変更が見る人の客で描き直されると、その人だけ元の見た目に戻る。
/// 使う側は会議の前に <see cref="End"/> を呼び、キルの後など要所で <see cref="Refresh"/> を呼ぶ。
/// </summary>
public static class SeerCamouflage
{
    // 1 回の送りに詰める大きさの上限 (公式鯖の 1 メッセージの上限より十分小さく)
    private const int ChunkLimit = 800;
    // 同じ見る人へ続けて送る時の間 (同じ宛先へ同種の電文を短い間に重ねない)
    private const float ChunkGap = 0.25f;

    private static readonly HashSet<byte> Seers = [];
    // 見る人ごとの送りの世代。新しい送りを始めたら、前の送りの遅れて出る分は捨てる (灰色と元の見た目の追い越し)
    private static readonly Dictionary<byte, int> Generation = [];
    // 鬼の体の差し替えの窓の間に頼まれた送り (見る人 → 見分けられなくするか)。窓が明けてから送る
    private static readonly Dictionary<byte, bool> Pending = [];
    private static NetworkedPlayerInfo.PlayerOutfit UnknownOutfit => new NetworkedPlayerInfo.PlayerOutfit().Set("", 15, "", "", "", "", "");

    public static bool IsActive(byte seerId) => Seers.Count > 0 && Seers.Contains(seerId);

    public static void Init()
    {
        Seers.Clear();
        Generation.Clear();
        Pending.Clear();
    }

    /// <summary>seer の画面で、他の全員を見分けられなくする</summary>
    public static void Begin(PlayerControl seer)
    {
        if (seer == null || !AmongUsClient.Instance.AmHost || !Seers.Add(seer.PlayerId)) return;
        Logger.Info($"見分けられなくする: {seer.GetNameWithRole()}", nameof(SeerCamouflage));
        Request(seer, disguise: true);
        Utils.NotifyRoles(SpecifySeer: seer);
    }

    /// <summary>seer の画面の見た目を元へ戻す</summary>
    public static void End(PlayerControl seer)
    {
        if (seer == null || !Seers.Remove(seer.PlayerId)) return;
        Logger.Info($"見分けを戻す: {seer.GetNameWithRole()}", nameof(SeerCamouflage));
        Request(seer, disguise: false);
        Utils.NotifyRoles(SpecifySeer: seer);
    }

    /// <summary>見分けられなくしている seer へ送り直す (他の送りで見た目が描き直された時)</summary>
    public static void Refresh(PlayerControl seer)
    {
        if (seer == null || !IsActive(seer.PlayerId)) return;
        Request(seer, disguise: true);
    }

    private static void Request(PlayerControl seer, bool disguise)
    {
        int gen = Generation.GetValueOrDefault(seer.PlayerId) + 1;
        Generation[seer.PlayerId] = gen;

        // 窓の間は GameManager が無いか、かくれんぼの物なので送らない
        if (SeekerBody.InWindow)
        {
            Pending[seer.PlayerId] = disguise;
            return;
        }

        Pending.Remove(seer.PlayerId);
        Send(seer, disguise, gen);
    }

    private static void FlushPending()
    {
        if (Pending.Count == 0 || SeekerBody.InWindow || !AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return;

        foreach ((byte seerId, bool disguise) in new List<KeyValuePair<byte, bool>>(Pending))
        {
            Pending.Remove(seerId);
            PlayerControl seer = Utils.GetPlayerById(seerId);
            if (seer != null) Send(seer, disguise, Generation.GetValueOrDefault(seerId));
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.FixedUpdate))]
    private static class TickPatch
    {
        public static void Postfix() => FlushPending();
    }

    private static void Send(PlayerControl seer, bool disguise, int gen)
    {
        if (seer.AmOwner)
        {
            SetLocally(seer, disguise);
            return;
        }

        if (seer.OwnerId < 0 || seer.Data == null || seer.Data.Disconnected) return;
        var chunks = new List<MessageWriter>();
        MessageWriter current = null;

        foreach (PlayerControl target in Main.AllPlayerControls)
        {
            if (target.PlayerId == seer.PlayerId || target.Data == null || target.Data.Disconnected) continue;
            // 死んだ人は見る人の画面に出ない。戻す時だけデータを書き戻す (忘我の間に死んだ人の分)
            if (disguise && !target.IsAlive()) continue;
            // 本人に死を隠している人のデータは送らない (データには死んだかどうかも入る)
            if (EndKnot.Roles.Akazukin.IsDeathConcealed(target.PlayerId)) continue;

            if (current == null)
            {
                current = MessageWriter.Get(SendOption.Reliable);
                current.StartMessage(6);
                current.Write(AmongUsClient.Instance.GameId);
                current.WritePacked(seer.OwnerId);
            }

            WriteTarget(current, target, disguise);

            if (current.Length >= ChunkLimit)
            {
                current.EndMessage();
                chunks.Add(current);
                current = null;
            }
        }

        if (current != null)
        {
            current.EndMessage();
            chunks.Add(current);
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            MessageWriter chunk = chunks[i];
            byte seerId = seer.PlayerId;

            void SendChunk()
            {
                if (Generation.GetValueOrDefault(seerId) != gen)
                {
                    chunk.Recycle();
                    return;
                }

                DataFlagRateLimiter.Enqueue(() =>
                {
                    AmongUsClient.Instance.SendOrDisconnect(chunk);
                    chunk.Recycle();
                }, cleanup: chunk.Recycle);
            }

            if (i == 0) SendChunk();
            else LateTask.New(SendChunk, ChunkGap * i, log: false);
        }
    }

    // target のデータ (見た目を替えた物か今の物) と、描き直しのための変身の電文を書く
    private static void WriteTarget(MessageWriter writer, PlayerControl target, bool disguise)
    {
        NetworkedPlayerInfo data = target.Data;
        Dictionary<PlayerOutfitType, NetworkedPlayerInfo.PlayerOutfit> saved = null;

        if (disguise)
        {
            saved = [];

            foreach (var kv in data.Outfits)
                saved[kv.Key] = kv.Value;

            data.Outfits[PlayerOutfitType.Default] = UnknownOutfit;
            if (saved.ContainsKey(PlayerOutfitType.Shapeshifted)) data.Outfits[PlayerOutfitType.Shapeshifted] = UnknownOutfit;
        }

        writer.StartMessage(1);
        writer.WritePacked(data.NetId);
        NetworkedPlayerInfoSerializePatch.IntentionalSends++;

        try { data.Serialize(writer, false); }
        finally
        {
            NetworkedPlayerInfoSerializePatch.IntentionalSends--;

            if (saved != null)
            {
                foreach (var kv in saved)
                    data.Outfits[kv.Key] = kv.Value;
            }
        }

        writer.EndMessage();

        if (!target.IsAlive()) return;

        // 描き直し。本物の変身中の人は、戻す時は変身先へ変身し直す
        PlayerControl shiftTarget = !disguise && target.IsShifted() ? Utils.GetPlayerById(target.shapeshiftTargetPlayerId) : null;
        writer.StartMessage(2);
        writer.WritePacked(target.NetId);
        writer.Write((byte)RpcCalls.Shapeshift);
        writer.WriteNetObject(shiftTarget ?? target);
        writer.Write(false);
        writer.EndMessage();
    }

    // ホストが見る人の時: 手元で見た目だけ描き替える (データは替えない)
    private static void SetLocally(PlayerControl seer, bool disguise)
    {
        foreach (PlayerControl target in Main.AllPlayerControls)
        {
            if (target.PlayerId == seer.PlayerId || target.Data == null || !target.IsAlive()) continue;

            try
            {
                if (disguise) target.RawSetOutfit(UnknownOutfit, PlayerOutfitType.Default);
                else
                {
                    PlayerOutfitType type = target.IsShifted() ? PlayerOutfitType.Shapeshifted : PlayerOutfitType.Default;
                    if (!target.Data.Outfits.TryGetValue(type, out NetworkedPlayerInfo.PlayerOutfit outfit)) (type, outfit) = (PlayerOutfitType.Default, target.Data.DefaultOutfit);
                    target.RawSetOutfit(outfit, type);
                }
            }
            catch (System.Exception e) { Utils.ThrowException(e); }
        }
    }
}
