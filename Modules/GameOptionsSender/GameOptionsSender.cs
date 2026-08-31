using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using AmongUs.GameOptions;
using Hazel;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using InnerNet;
using UnityEngine;
using static EndKnot.GameStates;

namespace EndKnot.Modules;

public abstract class GameOptionsSender
{
    protected abstract bool IsDirty { get; set; }

    /// <summary>
    /// 🔴 送信が呼び先の内部ガードで丸ごと捨てられる窓かどうか。真の間は dirty を落とさずに次周回へ持ち越す。
    /// ここで無条件に dirty を落とすと「送信されないまま dirty だけ消費」= 対象クライアント (バニラ客含む) だけ
    /// 速度/視界が古い値のまま固まり、無関係な次の dirty イベントが同じプレイヤーで起きるまで直らない無音 desync
    /// になる (例外もログも出ない)。MarkDirtySettings() を呼ぶ全コード (259 箇所) に効く共通の欠陥なので、
    /// 呼び出し側ごとの再マークではなくここで止める。
    /// memory: skiptasks-window-outlives-exilecontroller
    /// </summary>
    protected virtual bool SendSuppressed => false;

    private Il2CppStructArray<byte> BuildOptionArray()
    {
        IGameOptions opt = BuildSendableGameOptions();
        var currentGameMode = AprilFoolsMode.IsAprilFoolsModeToggledOn ? opt.AprilFoolsOnMode : opt.GameMode;

        // option => byte[]
        MessageWriter writer = MessageWriter.Get();
        writer.Write(opt.Version);
        writer.StartMessage(0);
        writer.Write((byte)currentGameMode);

        if (opt.TryCast(out NormalGameOptionsV11 normalOpt))
            NormalGameOptionsV11.Serialize(writer, normalOpt);
        else if (opt.TryCast(out HideNSeekGameOptionsV11 hnsOpt))
            HideNSeekGameOptionsV11.Serialize(writer, hnsOpt);
        else
            Logger.Error("Option cast failed", ToString());

        writer.EndMessage();

        Il2CppStructArray<byte> optionArray = writer.ToByteArray(false);
        writer.Recycle();
        return optionArray;
    }

    protected virtual void SendGameOptions()
    {
        Il2CppStructArray<byte> optionArray = BuildOptionArray();
        SendOptionsArray(optionArray);
    }

    protected virtual IEnumerator SendGameOptionsAsync()
    {
        Il2CppStructArray<byte> optionArray = BuildOptionArray();
        // マネージド IEnumerator を yield return で直接ネストしない (下の ShouldYieldFrame コメント参照)。
        // 手動ポンプなら子の yield 値 (null) がそのまま通り、ラッパーが生成されない。
        IEnumerator inner = SendOptionsArrayAsync(optionArray);
        while (inner.MoveNext()) yield return inner.Current;
    }

    private void SendOptionsArray(Il2CppStructArray<byte> optionArray)
    {
        // ロビー/セッション起動直後は GameManager.Instance / LogicComponents が未構築の瞬間がある (NRE 源)
        GameManager gm = GameManager.Instance;
        if (gm == null || gm.LogicComponents == null) return;
        int count = gm.LogicComponents.Count;

        for (byte i = 0; i < count; i++)
        {
            Il2CppSystem.Object logicComponent = gm.LogicComponents[i];
            if (logicComponent != null && logicComponent.TryCast<LogicOptions>(out _)) SendOptionsArray(optionArray, i);
        }
    }

    private IEnumerator SendOptionsArrayAsync(Il2CppStructArray<byte> optionArray)
    {
        GameManager gm = GameManager.Instance;
        if (gm == null || gm.LogicComponents == null) yield break;
        int count = gm.LogicComponents.Count;

        for (byte i = 0; i < count; i++)
        {
            // yield を跨ぐため毎周で取り直す (シーン遷移で破棄されうる)
            gm = GameManager.Instance;
            if (gm == null || gm.LogicComponents == null || i >= gm.LogicComponents.Count) yield break;
            Il2CppSystem.Object logicComponent = gm.LogicComponents[i];
            if (logicComponent != null && logicComponent.TryCast<LogicOptions>(out _)) SendOptionsArray(optionArray, i);

            if (ShouldYieldFrame())
            {
                yield return null;
                OnFrameResumed();
            }
        }
    }

    protected abstract void SendOptionsArray(Il2CppStructArray<byte> optionArray, byte logicOptionsIndex);

    public abstract IGameOptions BuildGameOptions();

    protected IGameOptions BuildSendableGameOptions()
    {
        return SanitizeForOfficialServer(BuildGameOptions());
    }

    protected static IGameOptions SanitizeForOfficialServer(IGameOptions opt)
    {
        if (CurrentServerType != ServerType.Vanilla || opt == null || !opt.TryCast(out NormalGameOptionsV11 normalOpt))
            return opt;

        int originalMaxPlayers = normalOpt.MaxPlayers;
        int originalImpostors = normalOpt.NumImpostors;
        int originalKillDistance = normalOpt.KillDistance;
        float originalPlayerSpeed = normalOpt.PlayerSpeedMod;
        bool changed = false;

        if (normalOpt.MaxPlayers > 15)
        {
            normalOpt.SetInt(Int32OptionNames.MaxPlayers, 15);
            changed = true;
        }

        int impostors = Mathf.Clamp(normalOpt.NumImpostors, 1, 3);
        if (impostors != normalOpt.NumImpostors)
        {
            normalOpt.SetInt(Int32OptionNames.NumImpostors, impostors);
            changed = true;
        }

        int killDistance = Mathf.Clamp(normalOpt.KillDistance, 0, 2);
        if (killDistance != normalOpt.KillDistance)
        {
            normalOpt.SetInt(Int32OptionNames.KillDistance, killDistance);
            changed = true;
        }

        float playerSpeed = Mathf.Clamp(normalOpt.PlayerSpeedMod, Main.MinSpeed, 3f);
        if (!Mathf.Approximately(playerSpeed, normalOpt.PlayerSpeedMod))
        {
            normalOpt.SetFloat(FloatOptionNames.PlayerSpeedMod, playerSpeed);
            changed = true;
        }

        if (changed)
        {
            Logger.Warn(
                $"Clamped outgoing official game options: MaxPlayers={originalMaxPlayers}->{normalOpt.MaxPlayers}, NumImpostors={originalImpostors}->{normalOpt.NumImpostors}, KillDistance={originalKillDistance}->{normalOpt.KillDistance}, PlayerSpeedMod={originalPlayerSpeed:0.###}->{normalOpt.PlayerSpeedMod:0.###}",
                nameof(GameOptionsSender));
        }

        return normalOpt.CastFast<IGameOptions>();
    }

    protected virtual bool AmValid()
    {
        return true;
    }

    #region Static

    public static readonly List<GameOptionsSender> AllSenders = [new NormalGameOptionsSender()];

    protected static MessageWriter PackedWriter;
    protected static int PackedWriterMessages;

    public static IEnumerator SendDirtyGameOptionsContinuously()
    {
        try
        {
            while (GameStates.InGame || GameStates.IsLobby)
            {
                float cycleStart = Time.realtimeSinceStartup;

                if (GameStates.InGame)
                {
                    PackedWriterMessages = 0;
                    PackedWriter = MessageWriter.Get(SendOption.Reliable);
                    PackedWriter.StartMessage(26);
                    PackedWriter.WritePacked(AmongUsClient.Instance.GameId);
                }

                for (var index = 0; index < AllSenders.Count; index++)
                {
                    if (ShouldYieldFrame())
                    {
                        yield return null;
                        OnFrameResumed();
                    }

                    // 分割閾値は公式鯖 kick 上限 (~1024) に対するヘッダ余裕込みで SafeChunkLength (800) に揃える
                    // (旧値 1000 は RPC.cs SyncCustomSettingsRPC と同じ独立マジックナンバーの兄弟だった)
                    if (PackedWriter != null && (PackedWriter.Length > CustomRpcSender.SafeChunkLength || PackedWriterMessages >= AmongUsClient.Instance.GetMaxMessagePackingLimit()))
                    {
                        PackedWriter.EndMessage();
                        EarlyWarning.OnPacket("GameOptionsSender.PackedFlush", PackedWriter.Length, PackedWriter.Length, "Reliable");
                        var qa = DataFlagRateLimiter.Enqueue(() => AmongUsClient.Instance.SendOrDisconnect(PackedWriter));
                        while (!qa.Done) yield return null;
                        PackedWriterMessages = 0;
                        if (qa.Dropped) break;
                        PackedWriter.Clear(SendOption.Reliable);
                        PackedWriter.StartMessage(26);
                        PackedWriter.WritePacked(AmongUsClient.Instance.GameId);
                    }

                    if (ShouldYieldFrame())
                    {
                        yield return null;
                        OnFrameResumed();
                    }

                    if (index >= AllSenders.Count) break;
                    GameOptionsSender sender = AllSenders[index];

                    if (sender == null || !sender.AmValid())
                    {
                        AllSenders.RemoveAt(index);
                        index--;
                        continue;
                    }

                    if (sender.IsDirty)
                    {
                        // 送信が捨てられる窓は dirty を保持したまま見送る (窓明けの周回で送り直される)
                        if (sender.SendSuppressed) continue;

                        IEnumerator send = sender.SendGameOptionsAsync();
                        while (send.MoveNext()) yield return send.Current;
                    }

                    sender.IsDirty = false;
                }

                if (ShouldYieldFrame())
                {
                    yield return null;
                    OnFrameResumed();
                }

                if (PackedWriterMessages > 0 && PackedWriter != null)
                {
                    PackedWriter.EndMessage();
                    EarlyWarning.OnPacket("GameOptionsSender.PackedFlush", PackedWriter.Length, PackedWriter.Length, "Reliable");
                    var qaFinal = DataFlagRateLimiter.Enqueue(() => AmongUsClient.Instance.SendOrDisconnect(PackedWriter));
                    while (!qaFinal.Done) yield return null;
                }

                PackedWriter?.Recycle();
                PackedWriter = null;
                PackedWriterMessages = 0;

                ForceWaitFrame = true;
                if (ShouldYieldFrame())
                {
                    yield return null;
                    OnFrameResumed();
                }

                // 最小周期ゲート: 旧実装はネスト yield の副作用で1周~0.37秒に偶発スロットルされており、
                // それが毎フレーム dirty を立てる書き手 (Spurt/Dynamo の MarkDirtySettings) の送信要求を
                // 隠蔽していた。リーク修正でループが設計速度に戻ったため、PackedFlush が Reliable 予算
                // (DataFlagRateLimiter 23/s) を独占して critical RPC を飢えさせないよう明示的に間引く。
                while (Time.realtimeSinceStartup - cycleStart < MinCycleIntervalSeconds) yield return null;
            }
        }
        finally
        {
            ActiveCoroutine = null;
            PackedWriter?.Recycle();
            PackedWriter = null;
            PackedWriterMessages = 0;
        }
    }

    // ⚠️ ここで「IEnumerator を返すヘルパーを yield return でネストする」形に戻してはいけない
    // (BUG-20260706-01 round11)。マネージド IEnumerator を Il2Cpp コルーチンから yield すると、
    // BepInEx が Il2CppManagedEnumerator + strong GCHandle を1回ごとに生成し、それが永久に解放
    // されない (実測: 8分で2.5万個・全 live・~50個/秒 = 慢性メモリ膨張の主因)。
    // 判定は bool で返し、呼び出し側で `yield return null` → OnFrameResumed() を書く。
    protected static bool ShouldYieldFrame()
    {
        if (ForceWaitFrame || Stopwatch.ElapsedMilliseconds >= FrameBudget)
        {
            ForceWaitFrame = false;
            Stopwatch.Reset();
            return true;
        }

        return false;
    }

    protected static void OnFrameResumed()
    {
        Stopwatch.Start();
    }

    public static Coroutine ActiveCoroutine;
    private static readonly Stopwatch Stopwatch = new();
    private const int FrameBudget = 3; // in milliseconds
    private const float MinCycleIntervalSeconds = 0.2f;
    protected static bool ForceWaitFrame;

    #endregion
}
