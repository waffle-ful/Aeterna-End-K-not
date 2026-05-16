namespace EndKnot.Modules;

// Default (empty) constants for source-built copies. The lobby-share feature
// is effectively disabled — LobbyShare.IsConfigured returns false and no
// announcements go anywhere.
//
// To enable the feature in a release build:
//   1. Copy this file to `Modules/LobbyShareSecrets.cs` (gitignored).
//   2. Fill in the real RelayUrl and HmacKey from your Cloudflare Worker deployment.
//   3. EndKnot.csproj automatically swaps the compile target — your local file
//      gets compiled instead of this default. Build, ship, done.
//
// Do NOT modify this file with real values. The .gitignore protects
// LobbyShareSecrets.cs but not LobbyShareSecrets.Default.cs.
internal static class LobbyShareSecrets
{
    internal const string RelayUrl = "";
    internal const string HmacKey = "";
    internal const string FcSalt = "EndKnotLobbyShareV1Salt";
}
