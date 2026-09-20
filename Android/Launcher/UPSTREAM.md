# Upstream: FusionCore

This directory is a `git subtree` of [FusionCore](https://github.com/All-Of-Us-Mods/FusionCore)
(All-Of-Us-Mods, GPL-3.0), rebranded as the End K not launcher for Android.

- Upstream repository: https://github.com/All-Of-Us-Mods/FusionCore
- Imported commit: `e3aa9b4` (`e3aa9b4ffc3545d545810161f4401bf6cffc29ab`, upstream `main`)
- Imported on: 2026-09-20

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
