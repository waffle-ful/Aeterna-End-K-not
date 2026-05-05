using System;
using System.Collections.Generic;

namespace EndKnot;

// Stub: populated by LocalDevOverride.Local.cs (gitignored, not committed).
// If that file is absent, IsLocalDev always returns false.
internal static partial class LocalDevOverride
{
    static partial void PopulateCodes(HashSet<string> codes);

    private static readonly HashSet<string> Codes;

    static LocalDevOverride()
    {
        Codes = new(StringComparer.OrdinalIgnoreCase);
        PopulateCodes(Codes);
    }

    public static bool IsLocalDev(this string friendCode)
        => !string.IsNullOrEmpty(friendCode) && Codes.Contains(friendCode.Replace(':', '#'));

    // True when LocalDevOverride.Local.cs (gitignored) populated at least one code.
    // Used to gate boot-time debugger features that fire before LocalPlayer exists.
    public static bool HasAnyCodes() => Codes.Count > 0;
}
