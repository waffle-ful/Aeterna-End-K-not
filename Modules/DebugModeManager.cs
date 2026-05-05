namespace EndKnot;

public static class DebugModeManager
{
#if DEBUG
    public static bool AmDebugger => true;
#else
    public static bool AmDebugger { get; } = LocalDevOverride.HasAnyCodes();
#endif
}
