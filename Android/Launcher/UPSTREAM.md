# Upstream: FusionCore

This directory is a `git subtree` of [FusionCore](https://github.com/All-Of-Us-Mods/FusionCore)
(All-Of-Us-Mods, GPL-3.0), rebranded as the End K not launcher for Android.

- Upstream repository: https://github.com/All-Of-Us-Mods/FusionCore
- Imported commit: `e3aa9b4` (`e3aa9b4ffc3545d545810161f4401bf6cffc29ab`, upstream `main`)
- Imported on: 2026-09-20 (upstream `main` re-checked on 2026-09-25: still `e3aa9b4`, nothing to pull)

## Pulling upstream changes

```powershell
git subtree pull --prefix=Android/Launcher https://github.com/All-Of-Us-Mods/FusionCore.git main --squash
```

## Rules for local changes

- The Java package name `dev.allofus.fusioncore`, its directory layout, and the AGP `namespace`
  are **never renamed**. Only `applicationId` differs from upstream. Keeping the package intact
  is what makes `git subtree pull` merge cleanly.
- Keep local edits small and additive (branding strings, storage root, single-target selection,
  build metadata). Anything that upstream could take back should be sent there as a pull request.
- `.github/workflows/build.yml` from upstream is intentionally removed here; it does not apply
  to a subtree inside this repository.
- `fusionApp/src/main/jniLibs/arm64-v8a/libdobby.so` is rebuilt from Dobby `f4643b8d` with the
  page-boundary fix in `third_party/dobby/` (see its README); upstream ships an unpatched binary.

## Running under arm64 translation (x86_64 emulators)

Support for x86_64 hosts that run the arm64 code through a binary translator follows upstream
`e49843b` ("translated ARM64 support"): on such a host every hook goes through a trampoline in the
pool next to `libil2cpp.so` (`fusion/src/hooking/safehook.cpp`). The host is recognised from the
primary ABI property, because the translator reports an arm machine to `uname()`.

The bundled assets differ from upstream as follows:

- `fusionApp/src/main/assets/BepInEx-arm64.zip`: `core/MonoMod.Core.dll`, `MonoMod.Utils.dll`,
  `MonoMod.RuntimeDetour.dll` and `MonoMod.Iced.dll` are the ones shipped in upstream `ab1c510`
  (MonoMod.Core 1.4.0-alpha.dev, MonoMod.Utils 25.0.15-alpha.dev, MonoMod.RuntimeDetour
  25.4.0-alpha.dev); these flush the instruction cache after writing code, which translated hosts
  need. The rest of the zip is unchanged.
- `MonoMod.Core.dll` in that zip and `System.Diagnostics.Process.dll` in
  `fusionApp/src/main/assets/dotnet-arm64.zip` (both MIT) are patched by `tools/AndroidDotnetPatch`
  (run it on both zips; it is idempotent): when `FUSION_TRANSLATED` is set, a read-only mapping
  of a library counts as a loaded module (under translation no arm64 library is mapped
  executable), and MonoMod's Android helper library lists `libfusionunwind.so`
  (`fusionApp/src/main/jni/unwind/`) as a dependency for the unwinder.
