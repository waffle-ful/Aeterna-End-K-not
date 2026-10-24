using InnerNet;

namespace EndKnot.Modules;

// 開発用のテストの口の空の版。本物の実装 (Modules/TestBridge.cs) がある時はビルドから外れる (EndKnot.csproj)。
public static class TestBridge
{
    public static void Tick() { }
    public static void OnHostSystemMessage(string title, string text) { }
    public static void OnDisconnect(DisconnectReasons reason, string stringReason) { }
    public static void OnPlayerJoined(ClientData client) { }
    public static void OnPlayerLeft(ClientData data, DisconnectReasons reason) { }
    public static void RecordError(string tag, string text) { }
    public static void RecordLog(BepInEx.Logging.LogLevel level, string tag, string text) { }
}
