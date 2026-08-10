# End K not

[日本語](README.md)

<p align="center">
  <a href="../../releases/latest/download/EndKnotInstaller.exe"><img src=".github/download-button-en.png" alt="Download the latest version" width="70%"></a>
</p>

<p align="center">
  <b>↑ Click to grab the installer. Close Among Us, run it, and you're done.</b><br>
  <sub>Prefer to unzip it yourself, or want an older build? Head to the <a href="../../releases/latest">releases page</a>.<br>
  Windows will show a blue warning on first run — <a href="#if-windows-shows-a-blue-warning">here's how to get past it</a>.</sub>
</p>

[![Discord](https://img.shields.io/badge/Discord-join-5865F2?logo=discord&logoColor=white)](https://discord.gg/sEYAFzD3a)

## About this mod

**End K not** is an unofficial personal fork of [Endless Host Roles (EHR)](https://github.com/Gurge44/EndlessHostRoles) for Among Us. It currently implements **673 roles**.

Only the lobby host needs to install the mod — other players can join and enjoy the additional roles without installing anything. It works fully on both official and custom servers.

This mod is unofficial and is **not affiliated with or endorsed by Innersloth**. **Please do not contact Innersloth regarding any issues with this mod.**

> [!WARNING]
> End K not is in **alpha**. Some roles are untested and several features are works-in-progress. Please report bugs and suggestions on [GitHub Issues](../../issues) or our [Discord](https://discord.gg/sEYAFzD3a).

Supported Among Us version: **2026.3.31**

## Features

On top of EHR's role engine, End K not adds its own original feature set for **streaming, long-running hosting, and original content.**

### 🎥 Streaming & long-running hosting

- **Per-crew text-to-speech (VOICEVOX integration)** — Reads each player's chat aloud in a distinct voice. It drives a locally-installed copy of [VOICEVOX](https://voicevox.hiroshiba.jp/); the audio plays only on the host's machine (your stream) and is never sent to the game. Voices can also be pinned per player name or friend code. *(See [Credits](#credits) for the attribution required when streaming.)*
- **Auto re-host & crash self-recovery** — If the host is kicked or dropped by the official server, End K not automatically re-creates a new lobby with the same region and settings. And if Among Us itself crashes or hangs, the bundled external watchdog detects it and relaunches the game to restore the lobby — so it keeps running unattended through 24-hour soaks and long streams.
- **BGM system** — Replaceable background music for menu / lobby / in-task / climax / meeting / result. Default tracks bundled.
- **YouTube live chat overlay & auto-posting** — Displays your YouTube live chat on top of the game screen while streaming, and auto-posts in-game commentary events (kills, meetings, wins, etc.) back to that live chat to keep viewers engaged.
- **Viewer intervention system** — Lets viewers interfere with the game via `!`-prefixed live chat commands, gated by a point economy. Includes `!大地震` (big earthquake — closes all doors, cuts power, and randomly teleports players), `!天の声` (voice of heaven — broadcasts a viewer's message to all players), and `!偽死体` (fake corpse — spawns a fake dead body near a living player).
- **AI commentary companion** — A separate AI process (Gemini Live) receives live game events and provides real-time commentary through a 2D portrait / 3D avatar with lip-sync. Topic rotation and repetition suppression keep the commentary fresh during long streams.
- **On-screen lobby code bubble** — A draggable IMGUI overlay that keeps your lobby code visible on stream at all times.

### 🏚️ Original content

- **Backrooms lobby** — A special Backrooms-themed lobby presentation, with asymmetric rendering that looks different for the modded host versus non-modded joiners.
- **EKM custom map editor** — A dedicated editor for building your own custom maps is bundled ([`editor/`](./editor)); maps you create can be loaded in-game *(work in progress)*.
- **Riptide** — An original End K not Impostor role: a giant wave sweeps across the entire map and anyone caught in it is wiped out, accelerating with every meeting.
- **Lobby decorations** — Place decorations such as hot springs and portals in the lobby.

### 🎨 UI & policy

- **Calamity-themed main menu** *(work in progress)* — A custom Calamity-style title screen (referencing [CalamityModPublic](https://github.com/CalamityTeam/CalamityModPublic)).
- **External communication reduced** — Achievements API, online presets, news fetching, and other upstream EHR network calls are disabled. Exceptions: update checks against this mod's GitHub releases, and gameplay features such as Bard and the anagram command that fetch public data from third-party APIs.
- **GPL-3.0 open source** — Full source available; you may study, modify, and redistribute under GPL-3.0.

## Role list

**673 roles in total.** Breakdown by faction:

| Faction | Count |
|---------|-------|
| Impostor | 166 (vanilla 4 + remake 4 + custom 158) |
| Crewmate | 176 (vanilla 6 + remake 7 + custom 163) |
| Neutral | 134 |
| Coven | 21 |
| Game mode exclusive | 28 |
| Other | 2 (GM / Convict) |
| Sub-roles (add-ons) | 146 |

▶ **[See the full list of all 673 roles (`ROLES-EN.md`)](./ROLES-EN.md)**

Use `/r <role name>` or `/myrole` in-game to read each role's effects and settings.

### Featured roles

| Role | Faction | What it does |
|---|---|---|
| **Riptide** | Impostor | Raises a wave with every meeting, sweeping players away to drown. |
| **Dossun** | Impostor | Places a giant block that follows your own movement — crush or knock back anyone in the way. |
| **WordKiller** | Impostor | Kills anyone who says the forbidden word. Turns the whole conversation into a minefield. |
| **Crosswind** | Impostor | Vanishes, then blasts everyone sideways with a gust of wind. |
| **Gemini** | Crewmate | Stand still and you leave a copy of yourself behind — same colour, same name, same everything. |
| **Supernova** | Neutral | Triggers a supernova explosion. |

These are End K not originals you can only play here — look for the ⭐ marks in [`ROLES-EN.md`](./ROLES-EN.md) to find more of them.


## Commands

Over 110 chat commands are available for hosts, moderators, and all players. See [`COMMANDS.md`](./COMMANDS.md) for the full list. Run `/help` in-game to see only the commands available in your current context.

## Installation

**Only the host needs the mod.** Other players join without installing anything.

### Installer (easiest, recommended)

1. **[Download `EndKnotInstaller.exe`](../../releases/latest/download/EndKnotInstaller.exe)**
2. **Fully close Among Us**, then run it
3. It auto-detects Steam / Epic and handles the whole install (updates work the same way)

#### If Windows shows a blue warning

The installer isn't code-signed, so the first run brings up a blue "**Windows protected your PC**" screen. That isn't a malware detection — it appears for **every unsigned exe distributed by an individual**.

1. Click "**More info**"
2. Click the "**Run anyway**" button that appears

If you'd rather verify the file first, run this in PowerShell:

```powershell
Get-FileHash "$env:USERPROFILE\Downloads\EndKnotInstaller.exe"
```

If the value matches the `sha256:` shown next to `EndKnotInstaller.exe` on the [releases page](../../releases/latest), the file is untampered.

> [!TIP]
> If you'd still rather not run an exe, the manual zip install below gets you the same thing.

### Manual zip install

1. Download the zip for your store from [Releases](../../releases)
   - Steam: `EndKnot-<version>_Steam.zip`
   - Epic: `EndKnot-<version>_Epic.zip`
2. **Fully close Among Us**
3. Extract **everything** from the zip into your Among Us installation folder, overwriting existing files
   - Steam: `<Steam>\steamapps\common\Among Us\`
   - Epic: `C:\Program Files\Epic Games\AmongUs\`
4. Launch Among Us

The zip bundles BepInEx, the config files, and the custom-region mod, so nothing else is required.

> [!IMPORTANT]
> **When updating, delete the `Among Us\BepInEx\interop\` folder** before extracting the new version. A stale interop folder can prevent the game from starting.

### Switching back to vanilla

Rename `winhttp.dll` in your `Among Us` folder to `winhttp.dll.disabled`. This fully disables the mod and the game launches as plain Among Us. Rename it back to re-enable.

### DLL-only update (for existing installs)

If you already have End K not installed, just overwrite `Among Us\BepInEx\plugins\EndKnot.dll` with the one from Releases and delete `Among Us\BepInEx\interop\`.

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

## Team

End K not is run by the following members.

| Member | Role | Link |
|---|---|---|
| Chinese-made Waffle (中国産わっふる) | Development & maintenance | [YouTube](https://www.youtube.com/@wafflewafflewafflewafflewaffle) |
| Tora-kun no Ie (トラ君の家) | Outreach & PR | [YouTube](https://www.youtube.com/@taizen-q3j) |
| Chaco (チャコ) | Outreach & PR | — |

> The code modification history can be traced through this repository's git log and [`CHANGELOG.md`](./CHANGELOG.md).

## Funding & Donations

End K not is a **free, GPL-3.0 mod**. All features are and will remain free, and donations are never required.

Any donations are **not personal income for the developers** — they are used solely for the mod's actual running costs (server, domain, API usage, commissioned assets, and so on). The spending rules and monthly finances are published for transparency.

▶ **[Funding rules and financial reports (`FUNDING.md`)](./FUNDING.md)**

## License

This project is licensed under the **GNU General Public License v3.0**. See [`LICENSE`](./LICENSE) for details.

End K not is a derivative of [Endless Host Roles](https://github.com/Gurge44/EndlessHostRoles). **Modifications since April 2026** were made by waffle-ful; the modification history is tracked in this repository's git log and [`CHANGELOG.md`](./CHANGELOG.md), in compliance with GPL-3.0 §5.

## Credits

- **[Endless Host Roles](https://github.com/Gurge44/EndlessHostRoles)** (Gurge44 et al.) — base mod, GPL-3.0
- **[TownOfHost-K](https://github.com/KYMario/TownOfHost-K)** (KYMario et al.) — source of many roles, streaming-support features, and the official-server packet-splitting safety net, GPL-3.0
- **[SuperNewRoles](https://github.com/SuperNewRoles/SuperNewRoles)** (SuperNewRoles team) — WaveCannon design reference, GPL-3.0
- **[TownOfHost-Pko](https://github.com/satokazoku/TownOfHost-Pko)** (satokazoku et al.) — Many roles, WaveCannon design reference, GPL-3.0
- **[Town Of Host](https://github.com/tukasa0001/TownOfHost)** (tukasa0001 et al.) — root of the TOH lineage
- **[Town Of Host_ForE](https://github.com/AsumuAkaguma/TownOfHost_ForE)** — BGM customization feature
- **[Town of Host: Enhanced (TOHE)](https://github.com/EnhancedNetwork/TownofHost-Enhanced)** (The Enhanced Network team) — source of many roles, GPL-3.0

### Music Credits

BGM by **DM DOKURO**
- [DM DOKURO YouTube Channel](https://www.youtube.com/@DMDOKURO)

BGM by **自称芸術家みーさん (Miisan)**
- [HURT RECORD](https://www.hurtrecord.com/bgm/46/zero-no-heya.html)

### Sound Effect Credits

Some sound effects use material from:
- On-Jin ～音人～ ([https://on-jin.com/](https://on-jin.com/))

### VOICEVOX (text-to-speech)

The per-crew read-aloud feature uses **[VOICEVOX](https://voicevox.hiroshiba.jp/)**, a free Japanese text-to-speech software. End K not bundles no voice data — it synthesizes at runtime through the VOICEVOX installed on the host's PC.

> [!IMPORTANT]
> **If you publish the generated audio in a stream or recording, you must credit both VOICEVOX and the character(s) used.**
> Example: `VOICEVOX:ずんだもん (Zundamon)`
> Each character has its own individual terms of use, so please review the [VOICEVOX terms](https://voicevox.hiroshiba.jp/term/) and each character's terms.

Which characters are used depends on the VOICEVOX voices the host has installed (the installed voices and their IDs are written to `BepInEx/config/EndKnot_VoiceVox_Speakers.txt`). Credits for all VOICEVOX characters are listed below:

四国めたん (Shikoku Metan) / ずんだもん (Zundamon) / 春日部つむぎ (Kasukabe Tsumugi) / 雨晴はう (Amehare Hau) / 波音リツ (Namine Ritsu) / 玄野武宏 (Kurono Takehiro) / 白上虎太郎 (Shirakami Kotaro) / 青山龍星 (Aoyama Ryusei) / 冥鳴ひまり (Meimei Himari) / 九州そら (Kyushu Sora) / もち子さん (Mochiko-san) / 剣崎雌雄 (Kenzaki Mesuo) / WhiteCUL / 後鬼 (Goki) / No.7 / ちび式じい (Chibishiki-jii) / 櫻歌ミコ (Ouka Miko) / 小夜/SAYO / ナースロボ＿タイプＴ (Nurserobo Type-T) / †聖騎士 紅桜† (Holy Knight Benizakura) / 雀松朱司 (Suzumatsu Akashi) / 麒ヶ島宗麟 (Kigashima Sorin) / 春歌ナナ (Haruka Nana) / 猫使アル (Nekotsuka Aru) / 猫使ビィ (Nekotsuka Bii) / 中国うさぎ (Chugoku Usagi) / 栗田まろん (Kurita Maron) / あいえるたん (Aierutan) / 満別花丸 (Manbetsu Hanamaru) / 琴詠ニア (Kotoyomi Nia) / Voidoll / ぞん子 (Zonko) / 中部つるぎ (Chubu Tsurugi) / 離途 (Rito) / 黒沢冴白 (Kurosawa Saehaku) / ユーレイちゃん (Yurei-chan) / 東北ずん子 (Tohoku Zunko) / 東北きりたん (Tohoku Kiritan) / 東北イタコ (Tohoku Itako) / あんこもん (Ankomon) / 夜語トバリ (Yogatari Tobari) / 暁記ミタマ (Akatsuki Mitama) / 里石ユカ (Satoishi Yuka)

For per-role porting credits, see [`CHANGELOG.md`](./CHANGELOG.md) and individual commit messages.

---

Among Us is © 2018–2026 Innersloth LLC. End K not is not affiliated with or endorsed by Innersloth. Portions of the materials used are property of Innersloth LLC.
