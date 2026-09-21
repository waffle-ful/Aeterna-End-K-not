using System.Collections.Generic;
using System.Linq;
using EndKnot.Modules;
using Hazel;
using UnityEngine;

namespace EndKnot;

public static class NameNotifyManager
{
    public static Dictionary<byte, Dictionary<string, long>> Notifies = [];
    private static long LastUpdate;

    /// <summary>
    ///     宛先ごとの「最後に実送信した合図」。毎フレーム判定で攻める役職 (陰陽師 / Torpedo / 人形 /
    ///     ケミスト) に当たると同じ文面が秒 50 回届くが、ホスト側の名前は変わらないので
    ///     <see cref="Utils.NotifyRoles" /> は空エンベロープになって捨てられる。一方
    ///     <see cref="SendRPC(byte,string,long,bool,SendOption)" /> は dedup が無く、しかも宛先指定なしの
    ///     <b>全員宛ブロードキャスト</b>なので、モッド客が1人でも居ると Reliable が秒 50 本ワイヤに出る。
    ///     文面が変わらない間は下の間隔まで間引く。
    /// </summary>
    private static readonly Dictionary<byte, (string Text, float Time, SendOption Option)> LastSent = [];

    /// <summary>同じ文面を送り直す最短間隔 (秒)。弾いた合図の <c>ResetBlockedAttackerCooldown</c> と揃えている。</summary>
    private const float ResendMinInterval = 1f;

    /// <summary>この長さ未満で消える合図は間引かない (間引くとモッド客側だけ先に消えてちらつく)。</summary>
    private const float ThrottleMinDuration = 2f;

    public static void Reset()
    {
        Notifies = [];
        LastSent.Clear();
    }

    public static void Notify(this PlayerControl pc, string text, float time = 6f, bool overrideAll = false, bool log = true, SendOption sendOption = SendOption.Reliable)
    {
        if (!AmongUsClient.Instance.AmHost || !pc) return;
        if (!GameStates.IsInTask) return;

        text = text.Trim();
        if (!text.Contains("<color=") && !text.Contains("</color>") && !text.Contains("<#")) text = Utils.ColorString(Color.white, text);
        if (!text.Contains("<size=")) text = $"<size=1.9>{text}</size>";

        long expireTS = Utils.TimeStamp + (long)time;

        if (overrideAll || !Notifies.TryGetValue(pc.PlayerId, out Dictionary<string, long> notifies))
            Notifies[pc.PlayerId] = new() { { text, expireTS } };
        else
            notifies[text] = expireTS;

        // 上で expireTS は更新済みなので、間引いてもホスト側の表示は途切れない。
        // overrideAll は「他の合図を消す」意味を持つので間引かない。
        if (!overrideAll && time >= ThrottleMinDuration
            && LastSent.TryGetValue(pc.PlayerId, out (string Text, float Time, SendOption Option) last)
            && last.Text == text && last.Option == sendOption
            && Time.time - last.Time < ResendMinInterval)
            return;

        // overrideAll は表示をまるごと差し替えるので、間引きの記録は残さない。
        // 残すと「差し替えの直後に来た同じ文面」が 1 秒弱だけ届かなくなる。
        if (overrideAll) LastSent.Remove(pc.PlayerId);
        else LastSent[pc.PlayerId] = (text, Time.time, sendOption);

        if (pc.IsNonHostModdedClient()) SendRPC(pc.PlayerId, text, expireTS, overrideAll, sendOption);
        Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc, SendOption: sendOption);
        if (log) Logger.Info($"New name notify for {pc.GetNameWithRole().RemoveHtmlTags()}: {text} ({time}s)", "Name Notify");
    }

    public static void OnFixedUpdate()
    {
        if (!GameStates.IsInTask)
        {
            Reset();
            return;
        }

        long now = Utils.TimeStamp;
        if (now == LastUpdate) return;
        LastUpdate = now;

        List<byte> toNotify = [];

        foreach ((byte id, Dictionary<string, long> notifies) in Notifies)
        {
            List<string> toRemove = [];

            notifies.DoIf(x => x.Value <= now, x => toRemove.Add(x.Key));

            toRemove.ForEach(x => notifies.Remove(x));
            if (toRemove.Count > 0) toNotify.Add(id);
        }

        if (toNotify.Count == 0) return;

        toNotify.ToValidPlayers().ForEach(x => Utils.NotifyRoles(SpecifySeer: x, SpecifyTarget: x));
    }

    public static bool GetNameNotify(PlayerControl player, out string name)
    {
        name = string.Empty;
        if (!Notifies.TryGetValue(player.PlayerId, out Dictionary<string, long> notifies)) return false;

        name = string.Join('\n', notifies.OrderBy(x => x.Value).Select(x => x.Key));
        return true;
    }

    private static void SendRPC(byte playerId, string text, long expireTS, bool overrideAll, SendOption sendOption) // Only sent when adding a new notification
    {
        if (!AmongUsClient.Instance.AmHost) return;

        MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.SyncNameNotify, sendOption);
        writer.Write(playerId);
        writer.Write(text);
        writer.Write(expireTS.ToString());
        writer.Write(overrideAll);
        EarlyWarning.OnPacket("SyncNameNotify", writer.Length, writer.Length, sendOption.ToString());
        AmongUsClient.Instance.FinishRpcImmediately(writer);
    }

    public static void SendRPC(CustomRpcSender sender, PlayerControl player, string text, long expireTS, bool overrideAll)
    {
        if (!AmongUsClient.Instance.AmHost || player.OwnerId < 0) return;

        // pre-write 見積り分割: text は任意長 (チャット全文+装飾タグ) で、
        // StartRpc の分割チェックは書く前の累積長しか見ないため「累積<500 + 大きい text」が単一チャンク
        // ~1024 (公式 kick 閾値) 超に合体し得る。さらに RpcSetName が sender.checkLength=false にした後は
        // そのチェックすら効かない。書く前に見積りで溢れるなら現 stream を doneStreams へ退避して分割する。
        int estimatedSize = 16 + HazelExtensions.GetStringWriteSize(text) + HazelExtensions.GetStringWriteSize(expireTS.ToString());
        // 退避する中身が無いのに分割すると空のチャンクが doneStreams に積まれる。packed sender の
        // 「空」は tag26 ヘッダぶんだけ長いので、そのぶんを見込んだ閾値で判定する。
        if (sender.stream.Length > (sender.packed ? CustomRpcSender.EmptyPackedStreamLength : 10) && sender.stream.Length + estimatedSize > CustomRpcSender.SafeChunkLength)
        {
            switch (sender.CurrentState)
            {
                case CustomRpcSender.State.InRootMessage:
                case CustomRpcSender.State.InRootPackedMessage:
                    sender.EndMessage(startNew: true);
                    break;
                case CustomRpcSender.State.Ready:
                    sender.FlushCurrentStream();
                    break;
            }
        }

        sender.AutoStartRpc(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.SyncNameNotify, sender.packed || sender.currentRpcTarget >= 0 ? player.OwnerId : -1);
        sender.Write(player.PlayerId);
        sender.Write(text);
        sender.Write(expireTS.ToString());
        sender.Write(overrideAll);
        sender.EndRpc();
    }

    public static void ReceiveRPC(MessageReader reader)
    {
        if (AmongUsClient.Instance.AmHost) return;

        byte playerId = reader.ReadByte();
        string text = reader.ReadString();
        long expireTS = long.Parse(reader.ReadString());
        bool overrideAll = reader.ReadBoolean();

        if (overrideAll || !Notifies.TryGetValue(playerId, out Dictionary<string, long> notifies))
            Notifies[playerId] = new() { { text, expireTS } };
        else
            notifies[text] = expireTS;

        Logger.Info($"New name notify for {Main.AllPlayerNames[playerId]}: {text} ({expireTS - Utils.TimeStamp}s)", "Name Notify");
    }
}