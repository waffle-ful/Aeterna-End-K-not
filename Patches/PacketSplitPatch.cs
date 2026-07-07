// The chokepoint packet-splitting approach here is adapted, with thanks, from
// Town Of Host-K by KYMario (GPL-3.0) — https://github.com/KYMario/TownOfHost-K
using System;
using HarmonyLib;
using Hazel;
using InnerNet;

namespace EndKnot.Patches;

// 関所型の最終防衛ライン: CustomRpcSender 等の呼び出し元分割 (SafeChunkLength=800) を
// すり抜けた stream 直書き経路も、ここで一括して分割する。公式鯖 anti-cheat は単一チャンク
// ~1024 byte 超で host を reason=Hacking キックするため、閾値超のみ Tag5/6 を Flag 境界で
// SafeChunkLength(=800) 単位に再分割して送り直す。閾値以下は毎回コストゼロで素通り。
[HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.SendOrDisconnect))]
internal static class PacketSplitPatch
{
    // このサイズを超えたパケットのみ分割処理に入る (vanilla 標準の通常 traffic には触れない)。
    // 公式 anti-cheat の実測: 902 は安全 / 1221 で kick。活性化後は内部を SafeChunkLength まで刻む。
    private const int DetectThreshold = 1000;

    // WriteMessage が各メッセージ前に書くヘッダ (ushort length + byte tag)
    private const int MsgHeader = 3;

    public static bool Prefix(InnerNetClient __instance, MessageWriter msg)
    {
        if (msg.Length <= DetectThreshold) return true;

        Logger.Info($"Large Packet({msg.Length}), SendOption:{msg.SendOption}", "PacketSplitPatch");

        var writer = MessageWriter.Get(msg.SendOption);
        var reader = MessageReader.Get(msg.ToByteArray(false));
        var sentAny = false;

        try
        {
            // Tag レベルの処理。writer には常に「閉じた完全なメッセージ」だけが溜まる。
            while (reader.Position < reader.Length)
            {
                var partMsg = reader.ReadMessage();
                bool divide = partMsg.Tag is 5 or 6 && partMsg.Length > CustomRpcSender.SafeChunkLength;

                // 書き込む前にフラッシュ判定 (post-write だと最大 2×閾値まで膨らむため)。
                // 分割対象は DivideLargeMessage を空 writer から始めたいので、先に溜まりを吐く。
                if (writer.Length > 0 && (divide || writer.Length + MsgHeader + partMsg.Length > CustomRpcSender.SafeChunkLength))
                {
                    Send(__instance, writer);
                    sentAny = true;
                    writer.Clear(writer.SendOption);
                }

                if (divide)
                    sentAny |= DivideLargeMessage(__instance, writer, partMsg);
                else
                    WriteMessage(writer, partMsg);

                partMsg.Recycle();
            }

            // 残り (閉じた完全メッセージのみ。空エンベロープは生成しない設計なので Length>0 で送ってよい)
            if (writer.Length > 0)
            {
                Send(__instance, writer);
                sentAny = true;
            }
        }
        catch (Exception e)
        {
            Logger.Error($"packet split failed: {e}", "PacketSplitPatch");
            writer.Recycle();
            reader.Recycle();
            // まだ1つも送っていなければ vanilla の通常送信にフォールバック。
            // 途中まで送っていたら二重送信を避けて suppress する。
            return !sentAny;
        }

        writer.Recycle();
        reader.Recycle();
        return false;
    }

    // partMsg (Tag5/6 の巨大 GameData) を Flag 境界で SafeChunkLength 単位に刻んで送る。
    // 呼び出し前に writer は空である前提。最後のチャンク (常に 1 個以上の Flag を含む) は
    // writer に残して呼び出し元のフラッシュに委ねる。戻り値: 途中で Send したか。
    private static bool DivideLargeMessage(InnerNetClient __instance, MessageWriter writer, MessageReader partMsg)
    {
        var tag = partMsg.Tag;
        var gameId = partMsg.ReadInt32();
        var clientId = -1;
        var sentAny = false;

        writer.StartMessage(tag);
        writer.Write(gameId);
        if (tag == 6)
        {
            clientId = partMsg.ReadPackedInt32();
            writer.WritePacked(clientId);
        }

        var hasSub = false;

        // Flag 単位の処理
        while (partMsg.Position < partMsg.Length)
        {
            var subMsg = partMsg.ReadMessage();

            // 現エンベロープに最低 1 Flag 入っている時だけ分割 (空 GameData 送信を避ける)。
            // 単一 Flag が単体で 800 超のケースは分割不能なのでそのまま (既知の残課題)。
            if (hasSub && writer.Length + MsgHeader + subMsg.Length > CustomRpcSender.SafeChunkLength)
            {
                writer.EndMessage();
                Send(__instance, writer);
                sentAny = true;

                writer.Clear(writer.SendOption);
                writer.StartMessage(tag);
                writer.Write(gameId);
                if (tag == 6) writer.WritePacked(clientId);
                hasSub = false;
            }

            WriteMessage(writer, subMsg);
            hasSub = true;
            subMsg.Recycle();
        }

        writer.EndMessage();
        return sentAny;
    }

    private static void WriteMessage(MessageWriter writer, MessageReader reader)
    {
        writer.Write((ushort)reader.Length);
        writer.Write(reader.Tag);
        writer.Write(reader.ReadBytes(reader.Length));
    }

    private static void Send(InnerNetClient __instance, MessageWriter writer)
    {
        var err = __instance.connection.Send(writer);
        if (err != SendErrors.None)
            Logger.Info($"SendMessage Error={err}", "PacketSplitPatch");
    }
}
