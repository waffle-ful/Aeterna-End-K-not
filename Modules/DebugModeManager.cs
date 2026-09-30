namespace EndKnot;

public static class DebugModeManager
{
#if DEBUG
    public static bool AmDebugger => true;
#elif ENDKNOT_PUBLIC_RELEASE
    // 公開ビルドは利用者本人が開発者リストに居る時だけ開く。フレンドコードはログイン後に決まるので、
    // 起動時に判定する項目 (コンソール等) は閉じたまま、ログイン以降に参照される項目だけが開く。
    public static bool AmDebugger { get; private set; }
#else
    public static bool AmDebugger { get; } = LocalDevOverride.HasAnyCodes();
#endif

    public static void OnAccountResolved(string friendCode)
    {
#if ENDKNOT_PUBLIC_RELEASE
        bool dev = friendCode.IsLocalDev();
        if (dev == AmDebugger) return;
        AmDebugger = dev;
        Logger.Info($"AmDebugger={dev}", "DebugModeManager");
#endif
    }
}
