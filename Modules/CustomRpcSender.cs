using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AmongUs.GameOptions;
using EndKnot.Modules;
using EndKnot.Roles;
using Hazel;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using InnerNet;
using UnityEngine;

namespace EndKnot;

public class CustomRpcSender
{
    public enum State
    {
        BeforeInit = 0, // Cannot do anything before initialization
        Ready, // Ready to send - StartMessage and SendMessage can be executed
        InRootPackedMessage, // State where only GameDataTo submessages can be started
        InRootMessage, // State between StartMessage and EndMessage - StartRpc and EndMessage can be executed
        InRpc, // State between StartRpc and EndRpc - Write and EndRpc can be executed
        Finished // Nothing can be done after sending
    }

    private readonly List<MessageWriter> doneStreams = [];
    private readonly bool isUnsafe;

    private readonly bool log;
    public readonly string name;

    private readonly OnSendDelegateType onSendDelegate;
    public readonly SendOption sendOption;

    // 0~: targetClientId (GameDataTo)
    // -1: All players (GameData)
    // -2: Not set
    public int currentRpcTarget;

    public bool packed;

    // 公式鯖 anti-cheat の kick 上限 (~1024 byte/パケット) に対する安全マージン込みの分割閾値。
    // これを超えて蓄積した stream は次の分割ポイントで別パケットに切り出す。
    public const int SafeChunkLength = 800;

    // When false, the >500 byte auto-split in StartMessage/StartPackedMessage/StartRpc is skipped.
    // RpcSetName sets this false and does its own accurate SafeChunkLength (UTF-8) chunking instead.
    //
    // ⚠️ **`sender.stream` をローカル変数にキャッシュする経路は必ず false にすること** (BUG-20260711-02 の真因)。
    // 分割 (StartRpc:458 → EndMessage(startNew:true):412-413) は `stream` を新しい writer に**差し替える**ため、
    // キャッシュ済みの参照は閉じた古い stream を指したままになり、以降の直書きが壊れたパケットを作って
    // 公式鯖に reason=Hacking で蹴られる。該当経路: CustomNetObject (2箇所) / Utils.RpcCreateDeadBody / DummySpawner。
    // これらは分割を送信後の関所 (PacketSplitPatch) に委ねる。
    public bool checkLength = true;

    private State currentState = State.BeforeInit;
    public int messages;
    public MessageWriter stream;

    private CustomRpcSender() { }

    private CustomRpcSender(string name, SendOption sendOption, bool isUnsafe, bool log)
    {
        stream = MessageWriter.Get(sendOption);

        this.name = name;
        this.isUnsafe = isUnsafe;
        this.sendOption = sendOption;
        this.log = log;
        currentRpcTarget = -2;
        packed = false;
        onSendDelegate = () => { };
        messages = 0;

        currentState = State.Ready;
        if (log) Logger.Info($"\"{name}\" is ready", "CustomRpcSender");
    }

    public State CurrentState
    {
        get => currentState;
        set
        {
            if (isUnsafe)
                currentState = value;
            else
                Logger.Warn("CurrentState can only be overwritten when isUnsafe is true", "CustomRpcSender");
        }
    }

    public static CustomRpcSender Create(string name = "No Name Sender", SendOption sendOption = SendOption.None, bool isUnsafe = false, bool log = true)
    {
        return new(name, sendOption, isUnsafe, log);
    }

    public CustomRpcSender AutoStartRpc(
        uint targetNetId,
        RpcCalls rpcCall,
        int targetClientId = -1,
        [CallerFilePath] string callerPath = "",
        [CallerLineNumber] int callerLine = 0)
    {
        // ReSharper disable ExplicitCallerInfoArgument
        return AutoStartRpc(targetNetId, (byte)rpcCall, targetClientId, callerPath, callerLine);
        // ReSharper restore ExplicitCallerInfoArgument
    }

    public CustomRpcSender AutoStartRpc(
        uint targetNetId,
        byte callId,
        int targetClientId = -1,
        [CallerFilePath] string callerPath = "",
        [CallerLineNumber] int callerLine = 0)
    {
        if (targetClientId == -2) targetClientId = -1;

        if (currentState is not State.Ready and not State.InRootPackedMessage and not State.InRootMessage)
        {
            var errorMsg = $"Tried to start RPC automatically, but State is not Ready or InRootPackedMessage or InRootMessage (in: \"{name}\", state: {currentState}) (called from {callerPath}:{callerLine})";

            if (isUnsafe)
                Logger.Warn(errorMsg, "CustomRpcSender.Warn");
            else
                throw new InvalidOperationException(errorMsg);
        }

        if (currentState == State.InRootPackedMessage && targetClientId < 0)
        {
            var errorMsg = $"Tried to start RPC automatically, but State is InRootPackedMessage and the requested targetClientId is negative. Only GameDataTo messages can be started in this state. (in: \"{name}\", state: {currentState}) (called from {callerPath}:{callerLine})";

            if (isUnsafe)
                Logger.Warn(errorMsg, "CustomRpcSender.Warn");
            else
                throw new InvalidOperationException(errorMsg);
        }

        if (currentRpcTarget != targetClientId)
        {
            // StartMessage processing
            if (currentState == State.InRootMessage)
                EndMessage(startNew: true);
            else if (messages > 0) // state is Ready or InRootPackedMessage
            {
                if (currentState == State.InRootPackedMessage)
                {
                    stream.EndMessage();
                    currentState = State.Ready;
                    doneStreams.Add(stream);
                    stream = MessageWriter.Get(sendOption);
                    messages = 0;
                    StartPackedMessage(); // assume the next message should be in a PackedGameDataTo message as well
                }
                else // state is Ready
                {
                    doneStreams.Add(stream);
                    stream = MessageWriter.Get(sendOption);
                    messages = 0;
                }
            }

            StartMessage(targetClientId);
        }

        StartRpc(targetNetId, callId);

        return this;
    }

    public void SendMessage(bool dispose = false)
    {
        if (!dispose)
        {
            if (currentState == State.InRootMessage) EndMessage();

            if (currentState == State.InRootPackedMessage)
            {
                // PackedMessage 中身が「GameId だけ書いた空状態」なら dispose 扱いに格上げ
                // (StartMessage(26) ヘッダ tag1 + length2 = 3 byte + WritePacked(GameId) ぶんが「空」の実サイズ)
                if (3 + HazelExtensions.GetPackedUIntSize((uint)AmongUsClient.Instance.GameId) >= stream.Length)
                    dispose = true;
                else
                    EndMessage();
            }

            if (currentState != State.Ready)
            {
                if (currentState == State.Finished)
                {
                    Logger.Warn($"Tried to send RPC but \"{name}\" is already Finished", "CustomRpcSender.Warn");
                    return;
                }

                var errorMsg = $"Tried to send RPC but State is not Ready (in: \"{name}\", state: {currentState})";

                if (isUnsafe)
                    Logger.Warn(errorMsg, "CustomRpcSender.Warn");
                else
                    throw new InvalidOperationException(errorMsg);
            }
        }

        if (stream.Length >= 1400 && sendOption == SendOption.Reliable && !dispose) Logger.Warn($"Large reliable packet \"{name}\" is sending ({stream.Length} bytes)", "CustomRpcSender");
        else if (log || stream.Length > 3) Logger.Info($"\"{name}\" is finished (Length: {stream.Length}, dispose: {dispose}, sendOption: {sendOption})", "CustomRpcSender");

        if (!dispose)
        {
            HealthLog.RecordHostAction(name, stream.Length, sendOption.ToString());

            // 早期警報: 分割送信された全チャンクの合計論理サイズと最大チャンク長を見て kick 閾値接近を検知する。
            int totalLen = stream.Length;
            int maxChunkLen = stream.Length;
            foreach (MessageWriter ds in doneStreams)
            {
                totalLen += ds.Length;
                if (ds.Length > maxChunkLen) maxChunkLen = ds.Length;
            }
            EarlyWarning.OnPacket(name, totalLen, maxChunkLen, sendOption.ToString());

            if (doneStreams.Count > 0)
            {
                var sb = new StringBuilder(" + Lengths: ");

                doneStreams.ForEach(x =>
                {
                    if (x.Length >= 1400 && sendOption == SendOption.Reliable) Logger.Warn($"Large reliable packet \"{name}\" is sending ({x.Length} bytes)", "CustomRpcSender");
                    else if (log || x.Length > 3) sb.Append($" | {x.Length}");

                    AmongUsClient.Instance.SendOrDisconnect(x);
                    x.Recycle();
                });

                Logger.Info(sb.ToString(), "CustomRpcSender");

                doneStreams.Clear();
            }

            AmongUsClient.Instance.SendOrDisconnect(stream);
            onSendDelegate();
        }

        if (dispose && doneStreams.Count > 0)
        {
            // dispose 時も分割チャンクとして退避済みの writer を回収しないとプールが漏れる
            doneStreams.ForEach(x => x.Recycle());
            doneStreams.Clear();
        }

        packed = false;
        currentRpcTarget = -2;
        messages = 0;
        currentState = State.Finished;
        stream.Recycle();
    }

    private CustomRpcSender Write(Action<MessageWriter> action)
    {
        if (currentState != State.InRpc)
        {
            var errorMsg = $"Tried to write RPC, but State is not InRpc (in: \"{name}\")";

            if (isUnsafe)
                Logger.Warn(errorMsg, "CustomRpcSender.Warn");
            else
                throw new InvalidOperationException(errorMsg);
        }

        action(stream);

        return this;
    }

    private delegate void OnSendDelegateType();

    #region Start/End Message

    public CustomRpcSender StartMessage(int targetClientId = -1)
    {
        if (currentState is not State.Ready and not State.InRootPackedMessage)
        {
            var errorMsg = $"Tried to start Message but State is not Ready or InRootPackedMessage (in: \"{name}\")";

            if (isUnsafe)
                Logger.Warn(errorMsg, "CustomRpcSender.Warn");
            else
                throw new InvalidOperationException(errorMsg);
        }

        if (currentState == State.InRootPackedMessage && targetClientId < 0)
        {
            var errorMsg = $"Tried to start RPC automatically, but State is InRootPackedMessage and the requested targetClientId is negative. Only GameDataTo messages can be started in this state. (in: \"{name}\")";

            if (isUnsafe)
                Logger.Warn(errorMsg, "CustomRpcSender.Warn");
            else
                throw new InvalidOperationException(errorMsg);
        }

        if (checkLength && stream.Length > 500)
        {
            if (currentState == State.InRootPackedMessage)
            {
                stream.EndMessage();
                doneStreams.Add(stream);
                stream = MessageWriter.Get(sendOption);
                messages = 0;
                currentState = State.Ready;
                StartPackedMessage();
            }
            else
            {
                doneStreams.Add(stream);
                stream = MessageWriter.Get(sendOption);
                messages = 0;
            }
        }

        if (targetClientId < 0)
        {
            // RPC for everyone
            stream.StartMessage(5);
            stream.Write(AmongUsClient.Instance.GameId);
        }
        else
        {
            // RPC (Desync) to a specific client
            stream.StartMessage(6);
            stream.Write(AmongUsClient.Instance.GameId);
            stream.WritePacked(targetClientId);
        }

        currentRpcTarget = targetClientId;
        currentState = State.InRootMessage;
        return this;
    }

    public CustomRpcSender StartPackedMessage()
    {
        if (currentState != State.Ready)
        {
            var errorMsg = $"Tried to start Message but State is not Ready (in: \"{name}\")";

            if (isUnsafe)
                Logger.Warn(errorMsg, "CustomRpcSender.Warn");
            else
                throw new InvalidOperationException(errorMsg);
        }

        if (checkLength && stream.Length > 500)
        {
            doneStreams.Add(stream);
            stream = MessageWriter.Get(sendOption);
            messages = 0;
        }

        stream.StartMessage(26);
        stream.WritePacked(AmongUsClient.Instance.GameId);

        currentRpcTarget = -2;
        currentState = State.InRootPackedMessage;
        packed = true;
        return this;
    }

    // 現在の stream を doneStreams へ退避して新しい stream を開始する (送信順は保たれる)。
    // SyncSettings / RpcChangeOutfitByData のように state machine を経由せず stream へ直書きする経路が、
    // 蓄積済みの stream に relaying して単一チャンクを kick 上限超えに膨らませるのを防ぐための分割ポイント。
    // Ready / InRootPackedMessage のときだけ有効 (message 途中では何もしない)。
    public void FlushCurrentStream()
    {
        if (currentState == State.InRootPackedMessage)
        {
            stream.EndMessage();
            doneStreams.Add(stream);
            stream = MessageWriter.Get(sendOption);
            messages = 0;
            currentState = State.Ready;
            StartPackedMessage();
        }
        else if (currentState == State.Ready && stream.Length > 0)
        {
            doneStreams.Add(stream);
            stream = MessageWriter.Get(sendOption);
            messages = 0;
        }
    }

    public CustomRpcSender EndMessage(bool startNew = false)
    {
        if (currentState is not State.InRootMessage and not State.InRootPackedMessage)
        {
            var errorMsg = $"Tried to exit Message but State is not InRootMessage or InRootPackedMessage (in: \"{name}\")";

            if (isUnsafe)
                Logger.Warn(errorMsg, "CustomRpcSender.Warn");
            else
                throw new InvalidOperationException(errorMsg);
        }

        bool wasPackedContext = packed;
        bool closingPackedRoot = currentState == State.InRootPackedMessage;

        stream.EndMessage();

        if (closingPackedRoot)
            packed = false;

        if (startNew)
        {
            if (wasPackedContext && !closingPackedRoot)
            {
                // Close outer packed root too
                stream.EndMessage();
            }

            doneStreams.Add(stream);
            stream = MessageWriter.Get(sendOption);
            messages = 0;
            
            currentState = State.Ready;
            currentRpcTarget = -2;

            if (wasPackedContext)
                StartPackedMessage();
            
            return this;
        }

        currentRpcTarget = -2;
        currentState = packed ? State.InRootPackedMessage : State.Ready;
        return this;
    }

    #endregion

    #region Start/End Rpc

    public CustomRpcSender StartRpc(uint targetNetId, RpcCalls rpcCall)
    {
        return StartRpc(targetNetId, (byte)rpcCall);
    }

    public CustomRpcSender StartRpc(
        uint targetNetId,
        byte callId)
    {
        if (currentState != State.InRootMessage)
        {
            var errorMsg = $"Tried to start RPC but State is not InRootMessage (in: \"{name}\")";

            if (isUnsafe)
                Logger.Warn(errorMsg, "CustomRpcSender.Warn");
            else
                throw new InvalidOperationException(errorMsg);
        }

        // 公式鯖 anti-cheat は単一チャンク ~1024 byte 超で host を Hacking キックする。個数上限だけだと、
        // 同一宛先へ長い SetName 等を数個積んだだけで byte 上限を越える単一チャンクに合体してしまう
        // (StartMessage/StartPackedMessage の 500 byte 分割は宛先が変わる時しか再入しないため素通り)。
        // ここで byte でも分割する。※ EndMessage が currentRpcTarget を -2 に戻す前に宛先を退避すること
        // (退避しないと desync=特定クライアント宛の続きが broadcast(tag 5) で再開し内容が漏れる)。
        if (messages >= AmongUsClient.Instance.GetMaxMessagePackingLimit() || (checkLength && stream.Length > 500))
        {
            int splitTarget = currentRpcTarget;
            EndMessage(startNew: true);
            StartMessage(splitTarget);
        }

        messages++;

        stream.StartMessage(2);
        stream.WritePacked(targetNetId);
        stream.Write(callId);

        currentState = State.InRpc;
        return this;
    }

    public CustomRpcSender EndRpc()
    {
        if (currentState != State.InRpc)
        {
            var errorMsg = $"Tried to terminate RPC but State is not InRpc (in: \"{name}\")";

            if (isUnsafe)
                Logger.Warn(errorMsg, "CustomRpcSender.Warn");
            else
                throw new InvalidOperationException(errorMsg);
        }

        stream.EndMessage();
        currentState = State.InRootMessage;
        return this;
    }

    #endregion

    // Write

    #region PublicWriteMethods

    public CustomRpcSender Write(float val)
    {
        return Write(w => w.Write(val));
    }

    public CustomRpcSender Write(string val)
    {
        return Write(w => w.Write(val));
    }

    public CustomRpcSender Write(ulong val)
    {
        return Write(w => w.Write(val));
    }

    public CustomRpcSender Write(int val)
    {
        return Write(w => w.Write(val));
    }

    public CustomRpcSender Write(uint val)
    {
        return Write(w => w.Write(val));
    }

    public CustomRpcSender Write(ushort val)
    {
        return Write(w => w.Write(val));
    }

    public CustomRpcSender Write(byte val)
    {
        return Write(w => w.Write(val));
    }

    public CustomRpcSender Write(sbyte val)
    {
        return Write(w => w.Write(val));
    }

    public CustomRpcSender Write(bool val)
    {
        return Write(w => w.Write(val));
    }

    public CustomRpcSender Write(Il2CppStructArray<byte> bytes)
    {
        return Write(w => w.Write(bytes));
    }

    public CustomRpcSender Write(Il2CppStructArray<byte> bytes, int offset, int length)
    {
        return Write(w => w.Write(bytes, offset, length));
    }

    public CustomRpcSender WriteBytesAndSize(Il2CppStructArray<byte> bytes)
    {
        return Write(w => w.WriteBytesAndSize(bytes));
    }

    public CustomRpcSender WritePacked(int val)
    {
        return Write(w => w.WritePacked(val));
    }

    public CustomRpcSender WritePacked(uint val)
    {
        return Write(w => w.WritePacked(val));
    }

    public CustomRpcSender WriteNetObject(InnerNetObject obj)
    {
        return Write(w => w.WriteNetObject(obj));
    }

    public CustomRpcSender WriteVector2(Vector2 vector2)
    {
        return Write(w => NetHelpers.WriteVector2(vector2, w));
    }

    #endregion
}

public static class CustomRpcSenderExtensions
{
    // 公式鯖 anti-cheat の kick 上限は SetName 系 GameDataTo チャンクでは ~800 byte 帯にある
    // (実測: 787B は通過 / 817B・838B・903B は reason=Hacking キック — 2026-07-11 / 2026-07-14 会議後 NotifyRoles)。
    // 旧値 800 (SafeChunkLength) は「800 超過を書込後に検知して flush」だったため 800 台のチャンクが日常的に漏れていた。
    // 750 を書込前見積もり (GetSetNameRpcSize = ラッパ込みの上限見積もり) で強制し、チャンクを 750 以下に保つ。
    public const int SetNameChunkFlushThreshold = 750;

    // seer 切替毎に積まれる GameDataTo ラッパの最大 byte (msg header 3 + packed GameId ≤5 + packed clientId ≤5)。
    // 毎件の見積もりに含めて過大側に倒す = チャンク実測が閾値を超えないことを保証する。
    public const int SetNameWrapperOverhead = 13;

    // 公式鯖の単発 SetName 超過キック対策の名前予算 (RPC ヘッダ類の余白込み。実測の裏付け: 単発 SetName 903B でキック)
    public const int NameBudget = SetNameChunkFlushThreshold - SetNameWrapperOverhead - 32;

    // NameBudget クランプの共有ヘルパ: 公式鯖のみ、予算超過時に rune 境界で末尾を切り捨て、
    // 閉じていない末尾タグを除去した文字列を返す (BUG-20260715-09 の CNO スプライトと同型の壊れ方対策)。
    // ⚠️ vanilla PlayerControl.RpcSetName 直呼び経路 (FixedUpdate タグ再送 / ScheduleDecoratedNameRestore) も
    // 必ずこれを通し、dirty-check はクランプ後の値で行うこと — クランプ前の値で比較すると
    // ミラー (=実送信名) と永遠に一致せず毎 tick 再送ループになる。
    public static string ClampNameForOfficialServer(string name, out bool clamped)
    {
        clamped = false;
        if (GameStates.CurrentServerType != GameStates.ServerType.Vanilla || string.IsNullOrEmpty(name)) return name;
        if (System.Text.Encoding.UTF8.GetByteCount(name) <= NameBudget) return name;

        var sb = new System.Text.StringBuilder();
        var bytes = 0;

        foreach (System.Text.Rune rune in name.EnumerateRunes())
        {
            int rb = rune.Utf8SequenceLength;
            if (bytes + rb > NameBudget) break;
            sb.Append(rune.ToString());
            bytes += rb;
        }

        clamped = true;
        return CustomNetObject.DropUnterminatedTag(sb.ToString());
    }

    // SetName を packed message に一括蓄積する。UTF-8 byte で正確に計算し、SetNameChunkFlushThreshold を
    // 超える手前で現 message を送って sender を作り直す (chunk 分割)。sender は ref で受けて差し替える。
    // checkLength=false にして StartMessage/StartPackedMessage の 500 byte 自動分割は無効化し、ここで管理する。
    public static void RpcSetName(ref CustomRpcSender sender, PlayerControl player, string name, PlayerControl seer = null)
    {
        bool seerIsNull = !seer;
        int targetClientId = seerIsNull ? -1 : seer.OwnerId;

        name = name.Replace("color=", string.Empty);

        switch (seerIsNull)
        {
            case true when Main.LastNotifyNames.Where(x => x.Key.Item1 == player.PlayerId).All(x => x.Value == name):
            case false when Main.LastNotifyNames[(player.PlayerId, seer.PlayerId)] == name:
                return;
            case true:
                Main.EnumeratePlayerControls().Do(x => Main.LastNotifyNames[(player.PlayerId, x.PlayerId)] = name);
                break;
            default:
                Main.LastNotifyNames[(player.PlayerId, seer.PlayerId)] = name;
                break;
        }

        // 分割不可能な単発超過ガード: 装飾名 1 件だけで安全上限を超えると chunk 分割では防げず、
        // 公式鯖に単一チャンク超過で reason=Hacking キックされる (実測: 単発 SetName 903B でキック、2026-07-14)。
        // キックより表示劣化がマシなので rune 境界でクランプ + Logger.Error で呼び出し元の修正を促す。
        // ※dedup (LastNotifyNames) の後に置くこと — 先にクランプすると元の名前と一致せず毎 tick 再送 flood になる。
        {
            int nameBytes = System.Text.Encoding.UTF8.GetByteCount(name);
            (byte, byte) clampKey = (player.PlayerId, seerIsNull ? byte.MaxValue : seer.PlayerId);
            string clamped = ClampNameForOfficialServer(name, out bool wasClamped);

            if (wasClamped)
            {
                name = clamped;

                // クランプ後 dedup: 変わったのが切り捨てられる末尾 (Sonar 矢印等の毎秒動く suffix) だけなら
                // クライアントに届く文字列は前回と完全同一 = 送るだけ帯域とレートゲート予算の無駄。
                // 上の dedup は intended 名で比較するためここをすり抜ける (2026-07-21 実測: 同一クランプ名の
                // 毎秒再送で Reliable burst が 101→425/10s まで単調増加)。タグ復元系は vanilla RpcSetName の
                // 別経路 (ScheduleDecoratedNameRestore / FixedUpdate ミラー) なのでこのスキップの影響を受けない。
                if (Main.LastSentClampedNames.TryGetValue(clampKey, out string lastClamped) && lastClamped == name) return;

                Main.LastSentClampedNames[clampKey] = name;
                Logger.Error($"SetName for player {player.PlayerId} is {nameBytes}B > {NameBudget}B — clamped to avoid official-server Hacking kick. Shrink the name decoration (role text / addons / suffix)!", "RpcSetName.NameBudget");
            }
            else
            {
                // 予算内に戻ったら (装飾が減った等) クランプ dedup を無効化 — 次に再超過した時、
                // 直前にクライアントへ届いたのは完全名なので、同一クランプ名でも1回は送り直す必要がある。
                Main.LastSentClampedNames.Remove(clampKey);
            }
        }

        // packed message は per-client (GameDataTo) 専用で、-1 broadcast (GameData) を入れると
        // AutoStartRpc の「InRootPackedMessage + targetClientId<0」ガードで throw する。
        // Car/Tree/Magistrate 等「全員に見せる自分の名前」(seer=null) は単発の非 packed message で送る。
        // ※この分岐は必ず上の dedup switch の「後」に置くこと。dedup が先にあるおかげで、
        //   名前が実際に変化した時だけ単発送信され、毎 NotifyRoles tick の flood にならない。
        if (seerIsNull && sender.packed)
        {
            CustomRpcSender broadcast = CustomRpcSender.Create(sender.name, sender.sendOption);
            broadcast.AutoStartRpc(player.NetId, RpcCalls.SetName)
                .Write(player.Data.NetId)
                .Write(name)
                .Write(false)
                .EndRpc();
            broadcast.SendMessage();
            return;
        }

        sender.checkLength = false;

        if (sender.stream.Length + GetSetNameRpcSize(player.NetId, name) > SetNameChunkFlushThreshold)
            FlushAndRecreate(ref sender);

        sender.AutoStartRpc(player.NetId, RpcCalls.SetName, targetClientId)
            .Write(player.Data.NetId)
            .Write(name)
            .Write(false)
            .EndRpc();

        // 後段ガード: 見積もりに載らないヘッダや、RpcSetName 以外から同じ sender に書かれた分の
        // 積み上がりで実測長が閾値を超えていたら、次の書込を待たずここで送ってしまう。
        if (sender.stream.Length > SetNameChunkFlushThreshold)
            FlushAndRecreate(ref sender);

        return;

        static void FlushAndRecreate(ref CustomRpcSender sender)
        {
            bool packed = sender.packed;
            sender.SendMessage();
            sender = CustomRpcSender.Create(sender.name, sender.sendOption);
            if (packed) sender.StartPackedMessage();
            sender.checkLength = false;
        }

        // SetName RPC 1 件の上限見積もり byte: GameDataTo ラッパ (SetNameWrapperOverhead)
        //   + msg header(3) + WritePacked(netId) + callId(1) + Write(Data.NetId uint=4)
        //   + 文字列(packed長さ prefix + UTF-8 bytes) + Write(bool=1)。
        // ラッパは seer が変わらない連続書込では実際には積まれないが、過大見積もり側に倒して
        // 「pre-check を通った書込でチャンクが閾値を超える」ことが起きないようにする。
        static int GetSetNameRpcSize(uint netId, string playerName)
            => SetNameWrapperOverhead + 3 + HazelExtensions.GetPackedUIntSize(netId) + 1 + 4 + HazelExtensions.GetStringWriteSize(playerName) + 1;
    }

    extension(CustomRpcSender sender)
    {
        public bool RpcSetRole(PlayerControl player, RoleTypes role, int targetClientId = -1, bool noRpcForSelf = true, bool changeRoleMap = false)
        {
            if (AmongUsClient.Instance.ClientId == targetClientId && noRpcForSelf)
            {
                player.SetRole(role);
                return false;
            }

            sender.AutoStartRpc(player.NetId, RpcCalls.SetRole, targetClientId)
                .Write((ushort)role)
                .Write(true)
                .EndRpc();

            // Assignment-burst instrumentation (no-op unless RecordSetRoles is on during role assignment).
            if (StartGameHostPatch.RpcSetRoleReplacer.RecordSetRoles)
                StartGameHostPatch.RpcSetRoleReplacer.SetRoleLog.Add((player.PlayerId, (ushort)role, targetClientId));

            if (changeRoleMap)
            {
                try
                {
                    if (targetClientId != -1) ChangeRoleMapForClient(Utils.GetClientById(targetClientId).Character.PlayerId);
                    else Main.PlayerStates.Keys.Do(ChangeRoleMapForClient);
                }
                catch (Exception e) { Utils.ThrowException(e); }

                void ChangeRoleMapForClient(byte id)
                {
                    (byte, byte) key = (id, player.PlayerId);

                    if (StartGameHostPatch.RpcSetRoleReplacer.RoleMap.TryGetValue(key, out (RoleTypes RoleType, CustomRoles CustomRole) pair))
                    {
                        pair.RoleType = role;
                        StartGameHostPatch.RpcSetRoleReplacer.RoleMap[key] = pair;
                    }
                }
            }

            return true;
        }

        public bool RpcGuardAndKill(PlayerControl killer, PlayerControl target = null, bool forObserver = false, bool fromSetKCD = false)
        {
            if (!AmongUsClient.Instance.AmHost)
            {
                StackFrame caller = new(1, false);
                MethodBase callerMethod = caller.GetMethod();
                string callerMethodName = callerMethod?.Name;
                string callerClassName = callerMethod?.DeclaringType?.FullName;
                Logger.Warn($"Modded non-host client activated RpcGuardAndKill from {callerClassName}.{callerMethodName}", "RpcGuardAndKill");
                return false;
            }

            if (!target) target = killer;

            var returnValue = false;

            // Check Observer
            if (!forObserver && !MeetingStates.FirstMeeting)
            {
                foreach (PlayerControl x in Main.EnumeratePlayerControls())
                {
                    if (x.Is(CustomRoles.Observer) && killer.PlayerId != x.PlayerId && sender.RpcGuardAndKill(x, target, true))
                        returnValue = true;
                }
            }

            // Host
            if (killer.AmOwner) killer.MurderPlayer(target, MurderResultFlags.FailedProtected);

            // Other Clients
            else
            {
                sender.AutoStartRpc(killer.NetId, RpcCalls.MurderPlayer, killer.OwnerId);
                sender.WriteNetObject(target);
                sender.Write((int)MurderResultFlags.FailedProtected);
                sender.EndRpc();

                // ここで sender.Notify を使うと flush 発火時に swap した sender をこのメソッドの呼び出し元へ
                // 返せない (RpcGuardAndKill 自体は sender を返さない) ので、独立送信の PlayerControl.Notify を使う。
                if (Options.CurrentGameMode == CustomGameMode.Standard && !MeetingStates.FirstMeeting && !AntiBlackout.SkipTasks && !ExileController.Instance && GameStates.IsInTask && killer.IsBeginner() && Main.GotShieldAnimationInfoThisGame.Add(killer.PlayerId))
                    killer.Notify(Translator.GetString("PleaseStopBeingDumb"), 10f);

                returnValue = true;
            }

            if (!fromSetKCD) killer.AddKillTimerToDict(true);

            return returnValue;
        }

        public bool SetKillCooldown(PlayerControl player, float time = -1f, PlayerControl target = null, bool forceAnime = false)
        {
            if (!player) return false;

            Logger.Info($"{player.GetNameWithRole()}'s KCD set to {(Math.Abs(time - -1f) < 0.5f ? Main.AllPlayerKillCooldown[player.PlayerId] : time)}s", "SetKCD");

            if (player.GetCustomRole().UsesPetInsteadOfKill())
            {
                if (Math.Abs(time - -1f) < 0.5f)
                    player.AddKCDAsAbilityCD();
                else
                    player.AddAbilityCD((int)Math.Round(time));

                if (player.GetCustomRole() is not CustomRoles.Necromancer and not CustomRoles.Deathknight and not CustomRoles.Renegade and not CustomRoles.Sidekick) return false;
            }

            if (!player.CanUseKillButton() && !AntiBlackout.SkipTasks && !IntroCutsceneDestroyPatch.PreventKill) return false;

            player.AddKillTimerToDict(cd: time);
            if (!target) target = player;

            if (time >= 0f)
                Main.AllPlayerKillCooldown[player.PlayerId] = time * 2;
            else
                Main.AllPlayerKillCooldown[player.PlayerId] *= 2;

            var returnValue = false;

            if (player.Is(CustomRoles.Glitch) && Main.PlayerStates[player.PlayerId].Role is Glitch gc)
            {
                gc.LastKill = Utils.TimeStamp + ((int)(time / 2) - Glitch.KillCooldown.GetInt());
                gc.KCDTimer = (int)(time / 2);
            }
            else if (forceAnime || !player.IsModdedClient() || !Options.DisableShieldAnimations.GetBool())
            {
                returnValue |= sender.SyncSettings(player);
                returnValue |= sender.RpcGuardAndKill(player, target, fromSetKCD: true);
            }
            else
            {
                time = Main.AllPlayerKillCooldown[player.PlayerId] / 2;

                if (player.AmOwner)
                    PlayerControl.LocalPlayer.SetKillTimer(time);
                else
                {
                    sender.AutoStartRpc(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.SetKillTimer, player.OwnerId);
                    sender.Write(time);
                    sender.EndRpc();

                    returnValue = true;
                }

                foreach (PlayerControl x in Main.EnumeratePlayerControls())
                {
                    if (x.Is(CustomRoles.Observer) && target.PlayerId != x.PlayerId && sender.RpcGuardAndKill(x, target, true, true))
                        returnValue = true;
                }
            }

            if (player.GetCustomRole() is not CustomRoles.Inhibitor and not CustomRoles.Saboteur)
            {
                player.ResetKillCooldown(sync: false);
                LateTask.New(player.SyncSettings, 1f, log: false);
            }

            return returnValue;
        }

        public bool RpcResetAbilityCooldown(PlayerControl target)
        {
            if (!AmongUsClient.Instance.AmHost) return false;

            Logger.Info($"Reset Ability Cooldown for {target.name} (ID: {target.PlayerId})", "RpcResetAbilityCooldown");

            if (target.Is(CustomRoles.Glitch) && Main.PlayerStates[target.PlayerId].Role is Glitch gc)
            {
                gc.LastHack = Utils.TimeStamp;
                gc.HackCDTimer = 10;

                return false;
            }

            if (target.AmOwner)
            {
                // If target is host
                target.Data.Role.SetCooldown();
                return false;
            }

            if (target.IsModdedClient())
            {
                sender.AutoStartRpc(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.ResetAbilityCooldown, target.OwnerId);
                sender.EndRpc();
                return true;
            }

            sender.AutoStartRpc(target.NetId, RpcCalls.ProtectPlayer, target.OwnerId);
            sender.WriteNetObject(target);
            sender.Write(0);
            sender.EndRpc();

            return true;
        }

        public void RpcDesyncUpdateSystem(PlayerControl target, SystemTypes systemType, int amount)
        {
            sender.AutoStartRpc(ShipStatus.Instance.NetId, RpcCalls.UpdateSystem, target.OwnerId);
            sender.Write((byte)systemType);
            sender.WriteNetObject(target);
            sender.Write((byte)amount);
            sender.EndRpc();
        }

        // setName=true の場合、内部の WriteSetNameRpcsToSender がチャンク分割で sender を flush+再生成することがある。
        // 呼び出し元は必ず newSender で握り直すこと (旧 sender は Finished 化されて以降の書込が throw する)。
        public bool Notify(PlayerControl pc, string text, out CustomRpcSender newSender, float time = 6f, bool overrideAll = false, bool log = true, bool setName = true)
        {
            newSender = sender;
            if (!AmongUsClient.Instance.AmHost || !pc) return false;
            if (!GameStates.IsInTask) return false;
            if (!text.Contains("<color=") && !text.Contains("</color>")) text = Utils.ColorString(Color.white, text);
            if (!text.Contains("<size=")) text = $"<size=1.9>{text}</size>";

            long expireTS = Utils.TimeStamp + (long)time;
            bool alreadyContainsKey = false;

            if (overrideAll || !NameNotifyManager.Notifies.TryGetValue(pc.PlayerId, out Dictionary<string, long> notifies))
                NameNotifyManager.Notifies[pc.PlayerId] = new() { { text, expireTS } };
            else
            {
                alreadyContainsKey = notifies.ContainsKey(text);
                notifies[text] = expireTS;
            }

            bool returnValue = pc.IsNonHostModdedClient();
            if (returnValue) NameNotifyManager.SendRPC(sender, pc, text, expireTS, overrideAll);

            if (alreadyContainsKey)
            {
                if (log) Logger.Info($"Extended name notify for {pc.GetNameWithRole().RemoveHtmlTags()}: {text} ({time}s)", "Name Notify");
                return returnValue;
            }

            if (setName)
            {
                returnValue |= Utils.WriteSetNameRpcsToSender(ref sender, false, false, false, false, false, false, pc, [pc], [], out bool senderWasCleared) && !senderWasCleared;
                newSender = sender;
            }

            if (log) Logger.Info($"New name notify for {pc.GetNameWithRole().RemoveHtmlTags()}: {text} ({time}s)", "Name Notify");

            return returnValue;
        }

        public bool TP(PlayerControl pc, Vector2 location, bool noCheckState = false, bool log = true)
        {
            if (!AmongUsClient.Instance.AmHost) return false;

            CustomNetworkTransform nt = pc.NetTransform;

            if (!noCheckState)
            {
                if (pc.Is(CustomRoles.AntiTP)) return false;

                if (pc.inVent || pc.inMovingPlat || pc.onLadder || !pc.IsAlive() || pc.MyPhysics.Animations.IsPlayingAnyLadderAnimation() || pc.MyPhysics.Animations.IsPlayingEnterVentAnimation())
                {
                    if (log) Logger.Warn($"Target ({pc.GetNameWithRole().RemoveHtmlTags()}) is in an un-teleportable state - Teleporting canceled", "TP");
                    return false;
                }

                if (FastVector2.DistanceWithinRange(pc.Pos(), location, 0.5f))
                {
                    if (log) Logger.Warn($"Target ({pc.GetNameWithRole().RemoveHtmlTags()}) is too close to the destination - Teleporting canceled", "TP");
                    return false;
                }
            }


            nt.SnapTo(location, (ushort)(nt.lastSequenceId + 328));
            nt.SetDirtyBit(uint.MaxValue);

            var newSid = (ushort)(nt.lastSequenceId + 8);

            sender.AutoStartRpc(nt.NetId, RpcCalls.SnapTo);
            sender.WriteVector2(location);
            sender.Write(newSid);
            sender.EndRpc();

            if (log) Logger.Info($"{pc.GetNameWithRole().RemoveHtmlTags()} => {location}", "TP");

            CheckInvalidMovementPatch.LastPosition[pc.PlayerId] = location;
            CheckInvalidMovementPatch.ExemptedPlayers.Add(pc.PlayerId);

            if (sender.sendOption == SendOption.Reliable) Utils.NumSnapToCallsThisRound++;
            return true;
        }

        public bool NotifyRolesSpecific(PlayerControl seer, PlayerControl target, out CustomRpcSender newSender, out bool senderWasCleared)
        {
            senderWasCleared = false;
            newSender = sender;
            if (!AmongUsClient.Instance.AmHost || !seer || seer.Data.Disconnected || (seer.IsModdedClient() && (seer.IsHost() || Options.CurrentGameMode == CustomGameMode.Standard)) || (!SetUpRoleTextPatch.IsInIntro && GameStates.IsLobby)) return false;
            var hasValue = Utils.WriteSetNameRpcsToSender(ref sender, false, false, false, false, false, false, seer, [seer], [target], out senderWasCleared) && !senderWasCleared;
            newSender = sender;
            return hasValue;
        }

        public bool RpcExiled(PlayerControl target, bool autoStartRpc = true, int targetClientId = -1, bool exileForHost = true)
        {
            if (exileForHost) target.Exiled();
            if (autoStartRpc) sender.AutoStartRpc(target.NetId, RpcCalls.Exiled, targetClientId).EndRpc();
            else sender.StartRpc(target.NetId, RpcCalls.Exiled).EndRpc();
            return true;
        }

        public bool RpcExileV2(PlayerControl target, bool autoStartRpc = true, int targetClientId = -1, bool exileForHost = true)
        {
            sender.RpcExiled(target, autoStartRpc, targetClientId, exileForHost);
            FixedUpdatePatch.LoversSuicide(target.PlayerId);
            return true;
        }

        public bool SyncSettings(PlayerControl player)
        {
            if (GameStates.CurrentServerType == GameStates.ServerType.Vanilla)
            {
                player.SyncSettings();
                return false;
            }

            var optionsender = GameOptionsSender.AllSenders.OfType<PlayerGameOptionsSender>().FirstOrDefault(x => x.player.PlayerId == player.PlayerId);
            if (optionsender == null) return false;

            var options = optionsender.BuildGameOptions();

            if (player.AmOwner)
            {
                foreach (GameLogicComponent com in GameManager.Instance.LogicComponents)
                {
                    if (com.TryCast(out LogicOptions lo))
                        lo.SetGameOptions(options);
                }

                GameOptionsManager.Instance.CurrentGameOptions = options;
                return false;
            }

            var logicOptions = GameManager.Instance.LogicOptions;
            var id = GameManager.Instance.LogicComponents.IndexOf(logicOptions);

            if (sender.CurrentState == CustomRpcSender.State.InRootMessage) sender.EndMessage();

            // ここから下は state machine を通さず stream へ直書きするため checkLength の分割が効かない。
            // 蓄積済みの stream に相乗りして単一チャンクが kick 上限を超えないよう、書込前に切り出しておく。
            if (sender.stream.Length > CustomRpcSender.SafeChunkLength) sender.FlushCurrentStream();

            var writer = sender.stream;

            writer.StartMessage(6);
            {
                writer.Write(AmongUsClient.Instance.GameId);
                writer.WritePacked(player.OwnerId);
                writer.StartMessage(1);
                {
                    writer.WritePacked(GameManager.Instance.NetId);
                    writer.StartMessage((byte)id);
                    {
                        writer.WriteBytesAndSize(logicOptions.gameOptionsFactory.ToBytes(options, AprilFoolsMode.IsAprilFoolsModeToggledOn));
                    }
                    writer.EndMessage();
                }
                writer.EndMessage();
            }
            writer.EndMessage();

            return true;
        }

        public bool SyncGeneralOptions(PlayerControl player)
        {
            if (!AmongUsClient.Instance.AmHost || !GameStates.IsInGame || !Utils.DoRPC) return false;

            sender.AutoStartRpc(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.SyncGeneralOptions);
            sender.Write(player.PlayerId);
            sender.WritePacked((int)player.GetCustomRole());
            sender.Write(Main.PlayerStates[player.PlayerId].IsDead);
            sender.WritePacked((int)Main.PlayerStates[player.PlayerId].deathReason);
            sender.Write(Main.AllPlayerKillCooldown[player.PlayerId]);
            sender.Write(Main.AllPlayerSpeed[player.PlayerId]);
            sender.EndRpc();

            return true;
        }
    }
}