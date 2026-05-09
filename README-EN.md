# End K not

[日本語](README.md)

[![Discord](https://img.shields.io/badge/Discord-join-5865F2?logo=discord&logoColor=white)](https://discord.gg/sEYAFzD3a)

## About this mod

**End K not** is an unofficial personal fork of [Endless Host Roles (EHR)](https://github.com/Gurge44/EndlessHostRoles) for Among Us. On top of EHR's **450+ roles and 16 game modes**, End K not ports roles from [TownOfHost-K (TOHK)](https://github.com/KYMario/TownOfHost-K) onto EHR's RoleBase system.

Only the lobby host needs to install the mod — other players can join and enjoy the additional roles without installing anything.

This mod is unofficial and is **not affiliated with or endorsed by Innersloth**. **Please do not contact Innersloth regarding any issues with this mod.**

> [!WARNING]
> End K not is in **alpha**. Some roles are untested and several features are works-in-progress. Please report bugs and suggestions on [GitHub Issues](../../issues) or our [Discord](https://discord.gg/sEYAFzD3a).

Supported Among Us version: **2026.3.31**

## Features

- **EHR + TOHK role merge** — EHR's full role catalog plus a growing set of roles ported from TOHK and re-implemented on RoleBase
- **Calamity-themed main menu** *(work in progress)* — A custom Calamity-style title screen
- **BGM system** — Replaceable background music for menu / lobby / in-task / climax / meeting / result. Default tracks bundled
- **External communication reduced** — Achievements API, online presets, news fetching, and other upstream EHR network calls are disabled. Exceptions: update checks against this mod's GitHub releases, and gameplay features such as Bard and the anagram command that fetch public data from third-party APIs
- **GPL-3.0 open source** — Full source available; you may study, modify, and redistribute under GPL-3.0

## Installation

1. Install [BepInEx IL2CPP](https://github.com/BepInEx/BepInEx) into your Among Us folder
2. Download the latest `EndKnot.dll` from [Releases](../../releases)
3. Place `EndKnot.dll` in `Among Us/BepInEx/plugins/`
4. Launch Among Us

## BGM customization

Hosts can replace the bundled music with their own:

- Location: `Among Us/BepInEx/resources/BGM/`
- Supported formats: `.ogg` / `.mp3` / `.wav`
- Supported slots: `menu` / `lobby` / `intask` / `climax` / `meeting` / `result`
- Example filenames: `menu.ogg`, `lobby.mp3`

Edit `bgm_titles.json` to control title / author display while a BGM plays. Files in the disk folder take priority; if a slot has no disk file, the bundled track plays instead.

## Community

- **Discord**: https://discord.gg/sEYAFzD3a — bug reports, questions, general chat (preferred)
- **Issues**: [GitHub Issues](../../issues) — may take a while to respond
- [`CODE_OF_CONDUCT.md`](./CODE_OF_CONDUCT.md) | [`CONTRIBUTING.md`](./CONTRIBUTING.md) | [`SECURITY.md`](./SECURITY.md) | [`SUPPORT.md`](./SUPPORT.md)

## License

This project is licensed under the **GNU General Public License v3.0**. See [`LICENSE`](./LICENSE) for details.

End K not is a derivative of [Endless Host Roles](https://github.com/Gurge44/EndlessHostRoles). **Modifications since April 2026** were made by waffle-ful; the modification history is tracked in this repository's git log and [`CHANGELOG.md`](./CHANGELOG.md), in compliance with GPL-3.0 §5.

## Credits

- **[Endless Host Roles](https://github.com/Gurge44/EndlessHostRoles)** (Gurge44 et al.) — base mod, GPL-3.0
- **[TownOfHost-K](https://github.com/KYMario/TownOfHost-K)** (KYMario et al.) — source of ported roles, GPL-3.0
- **[SuperNewRoles](https://github.com/SuperNewRoles/SuperNewRoles)** (SuperNewRoles team) — WaveCannon design reference, GPL-3.0
- **[TownOfHost-Pko](https://github.com/satokazoku/TownOfHost-Pko)** (satokazoku et al.) — WaveCannon design reference, GPL-3.0
- **[Town Of Host](https://github.com/tukasa0001/TownOfHost)** (tukasa0001 et al.) — root of the TOH lineage, README format reference
- **[Town Of Host_ForE](https://github.com/AsumuAkaguma/TownOfHost_ForE)** — README structure reference

For per-role porting credits, see [`CHANGELOG.md`](./CHANGELOG.md) and individual commit messages.

---

Among Us is © 2018–2026 Innersloth LLC. End K not is not affiliated with or endorsed by Innersloth. Portions of the materials used are property of Innersloth LLC.
