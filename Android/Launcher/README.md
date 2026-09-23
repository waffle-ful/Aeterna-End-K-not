# End K not Launcher (Android)

Android launcher that starts Among Us with the End K not mod loaded through BepInEx Fusion.
It is a rebranded build of [FusionCore](https://github.com/All-Of-Us-Mods/FusionCore) by
All-Of-Us-Mods, used under the GNU General Public License v3.0 (see [`LICENSE`](./LICENSE) and
[`UPSTREAM.md`](./UPSTREAM.md) for how upstream is tracked).

This project is not affiliated with or endorsed by Innersloth. Use at your own risk; Among Us must
already be installed on the device from an official store. This launcher does not ship the game.

## What it does

- Finds the installed Among Us package (`com.innersloth.spacemafia`) and launches it with the
  modding runtime injected. If it is installed, launch starts automatically after a short delay;
  tap the card to launch immediately, or open the game settings first (unstripped `libunity`
  download, launch activity override).
- Stores BepInEx files under `<external storage>/EndKnot/<package name>/` and shows them via the
  folder button.

## Requirements

- Android 8.1 (API level 27) or newer, arm64-v8a.
- "All files access" permission (requested on first start) so BepInEx files can be written.

## Build

Requirements: JDK 17, Android SDK with platform 37 and NDK `28.2.13676358`, CMake 3.22.1.

1. Create `local.properties` next to this file (it is ignored by git):

   ```properties
   sdk.dir=C:/path/to/Android/Sdk
   ```

2. Build the debug APK with the Gradle wrapper from this directory:

   ```powershell
   .\gradlew.bat :fusionApp:assembleDebug
   ```

   The APK is written to `fusionApp/build/outputs/apk/debug/`.

Release builds are signed only when `KEYSTORE_PATH`, `KEYSTORE_PASSWORD`, `KEY_ALIAS` and
`KEY_PASSWORD` are set in the environment; otherwise the release APK is left unsigned.

## Credits

- [FusionCore](https://github.com/All-Of-Us-Mods/FusionCore), [BepInExFusion](https://github.com/All-Of-Us-Mods/BepInExFusion)
  and [Il2CppInteropFusion](https://github.com/All-Of-Us-Mods/Il2CppInteropFusion) by All-Of-Us-Mods.
- Third-party licenses are listed in the repository root [`THIRD-PARTY-NOTICES.md`](../../THIRD-PARTY-NOTICES.md).
