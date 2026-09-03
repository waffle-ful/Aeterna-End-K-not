# End K not

[日本語](README.md)

<p align="center">
  <a href="../../releases/latest/download/EndKnotInstaller.exe"><img src=".github/download-button-en.png" alt="Download the latest version" width="70%"></a>
</p>

<p align="center">
  <b>↑ Click to grab the installer. Close Among Us, run it, and you're done.</b><br>
  <sub>Prefer to unzip it yourself, or want an older build? Head to the <a href="../../releases/latest">releases page</a>.<br>
  Windows will show a blue warning on first run; <a href="#if-windows-shows-a-blue-warning">getting past it takes two clicks</a>.</sub>
</p>

[![Discord](https://img.shields.io/badge/Discord-join-5865F2?logo=discord&logoColor=white)](https://discord.gg/sEYAFzD3a)

---

## Your usual Among Us night ends here.

### Only the host installs anything.

End K not runs off **the host's client alone.** Everyone else joins vanilla and still plays all **682 roles.** You never have to say "install this first, then come back," so nobody has to set anything up before they can join you. Official servers and custom servers both run the full feature set.

### Your lobby doesn't die.

Dropped by the server? End K not **re-creates the lobby automatically**, same region, same settings. If Among Us itself crashes or hangs, the bundled external watchdog notices, relaunches the game, and restores the lobby. Leave it running for a full day and it keeps going. You won't have to end a night early because the connection gave out.

### Your viewers never get bored.

End K not reads each player's chat aloud in **its own voice**, so your audience can tell who spoke without watching the screen. Viewers **reach into the game itself** with `!` commands from live chat. An **AI commentary companion**, a 2D portrait with a lip-synced 3D avatar, calls the kills, the meetings, and the wins in real time. Your chat gets a seat at the controls.

### Roles like these are waiting.

- **Riptide** — A giant wave sweeps the entire map. Caught in it, you're gone. It gets faster with every meeting.
- **WordKiller** — Kills anyone who says the forbidden word. The conversation itself becomes a minefield.
- **Gemini** — Stand still and a copy of you stays where you were. Same colour, same name, identical to you.
- **Crosswind** — Vanishes, then blasts everyone sideways with a gust of wind.
- **Dossun** — Places a giant block that moves with you. Crush them, or knock them flying.
- **Supernova** — A star that detonates the moment you stand still. Take everyone nearby with you, and if you last until the end, you shove the real winner aside and take the win alone.

### And trying it costs you nothing.

Run the installer. It works out whether you're on Steam or Epic and handles the rest. If it isn't for you, rename one file (`winhttp.dll`) and you're back to **plain Among Us.** Getting in and getting out both take seconds.

> **682 roles · 110+ chat commands · host-only install · completely free**

---

## About this mod

**End K not** is an unofficial personal fork of [Endless Host Roles (EHR)](https://github.com/Gurge44/EndlessHostRoles) for Among Us. It ships **682 roles**.

Only the lobby host installs the mod. Everyone else joins and plays the extra roles with nothing installed. Official servers and custom servers both run the full feature set.

This mod is unofficial and is **not affiliated with or endorsed by Innersloth**. **Please do not contact Innersloth regarding any issues with this mod.**

> [!WARNING]
> End K not is in **beta**. Some roles are untested and several features are works-in-progress. Please report bugs and suggestions on [GitHub Issues](../../issues) or our [Discord](https://discord.gg/sEYAFzD3a).

Supported Among Us version: **2026.8.18**

## Features

On top of EHR's role engine, End K not adds features for **streaming, long-running hosting, and presentation.**

> Most of the roles come from EHR and earlier mods such as the TownOfHost lineage, either inherited directly or reimplemented with reference to them. Credits for each project are collected under [Credits](#credits).

### 🎥 Streaming & long-running hosting

- **Per-crew text-to-speech (VOICEVOX integration)** — Reads each player's chat aloud in its own voice. The host's own copy of [VOICEVOX](https://voicevox.hiroshiba.jp/) does the speaking, so the audio stays on the host's machine (your stream) and never reaches the game. You can pin a voice to a player name or a friend code. *(See [Credits](#credits) for the attribution required when streaming.)*
- **Auto re-host & crash self-recovery** — When the official server kicks or drops the host, End K not builds a new lobby with the same region and the same settings. If Among Us crashes or hangs, the bundled external watchdog relaunches the game and restores the lobby, so a stream keeps running unattended for a full day.
- **BGM system** — Replaceable background music for menu / lobby / in-task / climax / meeting / result. Default tracks bundled.
- **YouTube live chat overlay & auto-posting** — Draws your YouTube live chat over the game screen, and posts in-game events (kills, meetings, wins) back to that chat so viewers who look away still follow the round.
- **Viewer intervention system** — Lets viewers interfere with the game via `!`-prefixed live chat commands, gated by a point economy. Includes `!大地震` (big earthquake — closes all doors, cuts power, and randomly teleports players), `!天の声` (voice of heaven — broadcasts a viewer's message to all players), and `!偽死体` (fake corpse — spawns a fake dead body near a living player).
- **AI commentary companion** — A separate AI process (Gemini Live) takes live game events and commentates through a 2D portrait with a lip-synced 3D avatar. It rotates through topics, so a long stream doesn't circle back to the same three remarks.
- **On-screen lobby code bubble** — A small draggable bubble that keeps your lobby code on screen for the whole stream.

### 🏚️ Lobby presentation & worlds

- **Backrooms lobby** — A Backrooms-themed lobby. The host and the players who joined without the mod see different things.
- **EKM custom map editor** — A dedicated editor for building custom maps is bundled ([`editor/`](./editor)); maps you create can be loaded in-game *(work in progress)*.
- **Riptide** — A loud Impostor role. A giant wave sweeps the whole map, kills anyone it catches, and speeds up after every meeting.
- **Lobby decorations** — Place decorations such as hot springs and portals in the lobby.

### 🎨 UI & policy

- **Calamity-themed main menu** *(work in progress)* — A custom Calamity-style title screen (referencing [CalamityModPublic](https://github.com/CalamityTeam/CalamityModPublic)).
- **GPL-3.0 open source** — Full source available; you may study, modify, and redistribute under GPL-3.0.

## Role list

**682 roles in total.** Breakdown by faction:

| Faction | Count |
|---------|-------|
| Impostor | 170 (vanilla 4 + remake 4 + custom 162) |
| Crewmate | 178 (vanilla 6 + remake 7 + custom 165) |
| Neutral | 137 |
| Coven | 21 |
| Game mode exclusive | 28 |
| Other | 2 (GM / Convict) |
| Sub-roles (add-ons) | 146 |

▶ **[See the full list of all 682 roles (`ROLES-EN.md`)](./ROLES-EN.md)**

Use `/r <role name>` or `/myrole` in-game to read each role's effects and settings. A picked-for-spectacle shortlist sits [at the top of this page](#roles-like-these-are-waiting).

## Commands

End K not adds over 110 chat commands across the host, moderator, and player tiers. See [`COMMANDS.md`](./COMMANDS.md) for the full list. Run `/help` in-game to see only the commands available in your current context.

## Installation

**Only the host needs the mod.** Other players join without installing anything.

### Installer (easiest, recommended)

1. **[Download `EndKnotInstaller.exe`](../../releases/latest/download/EndKnotInstaller.exe)**
2. **Fully close Among Us**, then run it
3. It auto-detects Steam / Epic and handles the whole install (updates work the same way)

#### If Windows shows a blue warning

The installer isn't code-signed, so the first run brings up a blue "**Windows protected your PC**" screen. Windows shows that screen for any unsigned exe an individual distributes. It does not mean a scanner found something in the file.

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

Rename `winhttp.dll` in your `Among Us` folder to `winhttp.dll.disabled`. This disables the mod and the game launches as plain Among Us. Rename it back to re-enable.

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

Donations are **not personal income for the developers.** They cover the mod's running costs (server, domain, API usage, commissioned assets), and we publish the spending rules along with where the money went each month.

▶ **[Funding rules and financial reports (`FUNDING.md`)](./FUNDING.md)**

## License

This project is licensed under the **GNU General Public License v3.0**. See [`LICENSE`](./LICENSE) for details.

End K not is a derivative of [Endless Host Roles](https://github.com/Gurge44/EndlessHostRoles). **Modifications since April 2026** were made by waffle-ful; the modification history is tracked in this repository's git log and [`CHANGELOG.md`](./CHANGELOG.md), in compliance with GPL-3.0 §5.

## Credits

> **The vast majority of this mod's roles and features come from earlier mods.** Huge thanks to the developers of the projects below. This list covers both the projects this fork referenced directly and the projects credited by the mods it builds on. You can trace which role came from where through this repository's git log; port commits record the upstream source and Co-authored-by lines.

- **[au.libhalt.net](https://au.libhalt.net/)** — Mad Jester
- **[AutoRejoin](https://github.com/Maxi0fc/AutoRejoin)** (Maxi0fc) — auto rejoin
- **[BetterAmongUs](https://github.com/D1GQ/BetterAmongUs)** (D1GQ, GPL-3.0) — the modded-client support flag list
- **[Calamity Mod (Terraria)](https://github.com/CalamityTeam/CalamityModPublic)** (Calamity Team) — reference for the main menu theme and visual design
- **[CrowdedMod](https://github.com/CrowdedMods/CrowdedMod)** (andry08 / CrowdedMods, MIT) — large-lobby support
- **[Endless Host Roles](https://github.com/Gurge44/EndlessHostRoles)** (Gurge44 et al., GPL-3.0) — base mod: the role engine, the large majority of roles, and an ongoing source of bug fixes
- **[ExtremeRoles](https://github.com/yukieiji/ExtremeRoles)** (yukieiji) — Assassin, Merlin, Airship patches
- **[Lotus (LotusContinued)](https://github.com/Lotus-AU/LotusContinued)** / [NikoCat233 fork](https://github.com/NikoCat233/LotusContinued) (GPL-3.0) — reference for the main menu rework, object helper code, ideas for Alchemist / Chameleon / Escapist / Necromancer / Deathknight / Romantic and its variants / Vengeance, auto play again, some tab icons, settings-conflict detection with host warnings, whitelist join restriction
- **[MalumMenu](https://github.com/scp222thj/MalumMenu)** (scp222thj, GPL-3.0) — player position dots on the minimap (cited by Town Of Next: Edited as a source for its vent map)
- **[Mini.RegionInstall](https://github.com/miniduikboot/Mini.RegionInstall)** (miniduikboot, GPL-3.0) — custom region installer (shipped in the release packages)
- **[MiraAPI](https://github.com/All-Of-Us-Mods/MiraAPI)** (All-Of-Us-Mods, LGPL-2.1) — role info tab code, UI sprites (two next-page buttons, two checkmarks), double task panel
- **[More Gamemodes](https://github.com/Rabek009/MoreGamemodes)** (Rabek009) — Custom Net Objects (CNO), chat control and clearing, ShipStatus and vote/ejection handling, vent interaction blocking, main menu image code
- **[Nebula on the Ship (NoS)](https://github.com/Dolly1016/Nebula)** (Dolly1016 et al., GPL-3.0; NebulaAPI is LGPL-3.0) — Mirage and other roles, Doctor, Sniper, memory-optimization and add-on API design reference
- **[Reactor](https://github.com/NuclearPowered/Reactor)** / [XtraCube fork](https://github.com/XtraCube/Reactor) — modded handshake, compiler-generated object and state machine wrappers, disabling the 5s timeout on custom servers
- **[Revolutionary Host Roles](https://github.com/sansaaaaai/Revolutionary-host-roles)** (sansaaaaai) — settings menu rework, custom buttons, Reloader, Staff, Incender
- **[Stellar Roles](https://github.com/Mr-Fluuff/StellarRolesAU)** (Mr-Fluuff) — many role ideas, some custom button images
- **[Submerged](https://github.com/SubmergedAmongUs/Submerged)** (SubmergedAmongUs) — map select button handling for Submerged
- **[SuperNewRoles](https://github.com/SuperNewRoles/SuperNewRoles)** / [ykundesu repo](https://github.com/ykundesu/SuperNewRoles) (SuperNewRoles team, GPL-3.0) — WaveCannon, credentials display, switch horse mode, search mod game, custom buttons, Libra, Meeting Sheriff, Toilet Fan, Evil Gambler, Penguin, Mad Suicide
- **[template-unity](https://github.com/vpmedia/template-unity)** (vpmedia, MIT) — reference for the Mersenne Twister implementation
- **[TheOtherRoles](https://github.com/TheOtherRolesAU/TheOtherRoles)** — Camouflager, Guesser, and more
- **[TheOtherRoles-GM](https://github.com/yukinogatari/TheOtherRoles-GM)** (yukinogatari) — several roles
- **TOR_GM_Haoming_Edition** — Evil Tracker, Schrödinger's Cat, and more
- **[TOHEX / TONEX](https://github.com/TOHEX-Official/TownOfHostEdited-Xi)** — Swapper, storing message history
- **[TOU-Mira](https://github.com/AU-Avengers/TOU-Mira)** (AU-Avengers, GPL-3.0) — dleks map selection, kill button cooldown display, HudManager work, role info tab; also cited by Town Of Next: Edited as a source for its vent network map
- **[Town Of Host](https://github.com/tukasa0001/TownOfHost)** (tukasa0001 et al.) — the root of the whole lineage; random spawn and sabotage handling
- **[Town Of Host-H](https://github.com/Hyz-sui/TownOfHost-H)** (Hyz-sui) — reference for the 10.24 update
- **[Town Of Host-K](https://github.com/KYMario/TownOfHost-K)** (KYMario et al., GPL-3.0) — source of many ported roles, streaming-support features, the official-server packet-splitting safety net, device usage time limits
- **[Town Of Host Re-Edited](https://github.com/Loonie-Toons/)** — EHR's fork origin; PhantomRolePatch
- **[Town Of Host_ForE](https://github.com/AsumuAkaguma/TownOfHost_ForE)** (AsumuAkaguma, GPL-3.0) — BGM customization, part of the comment-fetching code, chat character-type restriction (WordLimit), meeting start reason notification
- **[Town Of Host_Y](https://github.com/Yumenopai/TownOfHost_Y)** (Yumenopai) — AntiAdminer / CursedWolf / Workaholic / Greedy / Stalker / Ignitor / Rabbit, role display during meetings and meeting extensions, attribute names, game announcement changes, settings UI, role basis changing mid-game
- **[Town of Host: Enhanced (TOHE)](https://github.com/EnhancedNetwork/TownofHost-Enhanced)** (The Enhanced Network team, GPL-3.0) — many roles, various patches, friend-code matching on join
- **[Town Of Host Edited / Town Of Next](https://github.com/KARPED1EM/TownOfNext)** / [TownOfHostEdited](https://github.com/KARPED1EM/TownOfHostEdited) / [TownOfNext](https://github.com/TownOfNext/TownOfNext) (KARPED1EM, GPL-3.0) — EHR is a continuation of TOHE; chat message character limit, main menu animations, input patches, text box; also cited by Town Of Next: Edited as a source for its vent network map
- **[Town Of Next: Edited (TONE)](https://github.com/qin-qwq/TownofNext-Edited)** (qin-qwq, GPL-3.0) — vent network map display. TONE itself cites [TownOfNext](https://github.com/TownOfNext/TownOfNext), [TOU-Mira](https://github.com/AU-Avengers/TOU-Mira) and [MalumMenu](https://github.com/scp222thj/MalumMenu) as sources for this feature
- **[Town-Of-Moss](https://github.com/Koke1024/Town-Of-Moss)** (Koke1024) — reactor meltdown boost
- **[Town Of Us - Reactivated](https://github.com/eDonnes124/Town-Of-Us-R)** (eDonnes124, GPL-3.0) — the Submerged compatibility layer, host meeting display
- **[TownOfHost-Optimized](https://github.com/Limeau/TownofHost-Optimized)** (Limeau) — role ideas (Tank, Deadlined, Journalist, Grappler, Negotiator, Hypnotist, etc.)
- **[TownOfHost-Pko](https://github.com/satokazoku/TownOfHost-Pko)** (satokazoku et al., GPL-3.0) — source of many ported roles, WaveCannon design reference, direct numeric input for settings, consecutive-join kick, auto abort
- **[TownOfHost-TheOtherRoles](https://github.com/music-discussion/TownOfHost-TheOtherRoles)** / [discus-sions repo](https://github.com/discus-sions/TownOfHost-TheOtherRoles) (music-discussion) — many role ideas, exile confirm, split RPC packs
- **[TownOfHost-hamo](https://github.com/rar006/TownOfHost-hamo)** (rar006, GPL-3.0) — coming-out feature (`/co`, `/aco`, `/colist`)
- **[TownOfHostPlus](https://github.com/SkullCreeper/TownOfHostPlus)** (SkullCreeper) — Marshall / Poisoner / Necroview / Sidekick
- **[TownOfPlus](https://github.com/tugaru1975/TownOfPlus)** (tugaru1975) — zoom
- **[UnityDoorstop](https://github.com/NeighTools/UnityDoorstop)** (NeighTools, LGPL-2.1) — `winhttp.dll` / `doorstop_config.ini` used for packaging
- **[Vanilla Enhancements](https://github.com/xChipseq/VanillaEnhancements)** (xChipseq, GPL-3.0) — meeting screen patches

### Thanks to the developers and translators

**[Endless Host Roles](https://github.com/Gurge44/EndlessHostRoles)**

- Developers: Gurge44
- Contributors: Dx / PH_Gaming / TommyXL / Drakos / PEPPERcula
- Special Thanks: Seleneous / thewhiskas27 / HyperAtill / Sil
- Translators: Dx (PT-BR) / PH_Gaming (PT-BR) / Tomix (PT-BR) / HyperAtill (RU) / ABoringCat (ZH-CN) / Reborn (ZH-CN) / Pomelo (ZH-TW) / Polan (JP) / DoArc (ES) / Kurma (ID) / Gurge44 (HU) / Æ (KO)

> This mod's `Resources/Lang/` is inherited from EHR, so the translators' work above is included as-is.

**[TownOfHost-K](https://github.com/KYMario/TownOfHost-K)**

- Developers: 暇な人 KY/けーわい / タイガー / 夜藍 / ねむa / はろん
- Supporter: りぃりぃ

### Bundled third-party software

Some components are embedded into `EndKnot.dll`, others ship inside the release packages. All of them are the work of their respective authors and are licensed separately from End K not itself (GPL-3.0). The full list and license texts live in [`THIRD-PARTY-NOTICES.md`](./THIRD-PARTY-NOTICES.md).

- NVorbis (MIT) — Ogg Vorbis decoding
- NLayer (MIT) — MP3 decoding
- BepInEx / Il2CppInterop / Unity Doorstop (LGPL-2.1) — the mod loading stack
- Mini.RegionInstall (GPL-3.0, by duikbo) — custom region installer

### Music Credits

BGM by **自称芸術家みーさん (Miisan)**
- [HURT RECORD](https://www.hurtrecord.com/bgm/46/zero-no-heya.html)

BGM by **もっぴーさうんど (Moppy Sound)**, **こおろぎ (Kohrogi)**, **蒲鉾さちこ (Kamaboko Sachiko)**
- [DOVA-SYNDROME](https://dova-s.jp/)

### Sound Effect Credits

Some sound effects use material from:
- On-Jin ～音人～ ([https://on-jin.com/](https://on-jin.com/))
- [DOVA-SYNDROME](https://dova-s.jp/)
- [Pixabay](https://pixabay.com/)

### Video Asset Credits

- [みりんの動画素材 (Miirriin)](https://miirriin.com/)

### VOICEVOX (text-to-speech)

The per-crew read-aloud feature uses **[VOICEVOX](https://voicevox.hiroshiba.jp/)**, a free Japanese text-to-speech software. End K not bundles no voice data; it synthesizes at runtime through the VOICEVOX installed on the host's PC.

> [!IMPORTANT]
> **If you publish the generated audio in a stream or recording, you must credit both VOICEVOX and the character(s) used.**
> Example: `VOICEVOX:ずんだもん (Zundamon)`
> Each character has its own individual terms of use, so please review the [VOICEVOX terms](https://voicevox.hiroshiba.jp/term/) and each character's terms.

Which characters are used depends on the VOICEVOX voices the host has installed (the installed voices and their IDs are written to `BepInEx/config/EndKnot_VoiceVox_Speakers.txt`). Credits for all VOICEVOX characters are listed below:

四国めたん (Shikoku Metan) / ずんだもん (Zundamon) / 春日部つむぎ (Kasukabe Tsumugi) / 雨晴はう (Amehare Hau) / 波音リツ (Namine Ritsu) / 玄野武宏 (Kurono Takehiro) / 白上虎太郎 (Shirakami Kotaro) / 青山龍星 (Aoyama Ryusei) / 冥鳴ひまり (Meimei Himari) / 九州そら (Kyushu Sora) / もち子さん (Mochiko-san) / 剣崎雌雄 (Kenzaki Mesuo) / WhiteCUL / 後鬼 (Goki) / No.7 / ちび式じい (Chibishiki-jii) / 櫻歌ミコ (Ouka Miko) / 小夜/SAYO / ナースロボ＿タイプＴ (Nurserobo Type-T) / †聖騎士 紅桜† (Holy Knight Benizakura) / 雀松朱司 (Suzumatsu Akashi) / 麒ヶ島宗麟 (Kigashima Sorin) / 春歌ナナ (Haruka Nana) / 猫使アル (Nekotsuka Aru) / 猫使ビィ (Nekotsuka Bii) / 中国うさぎ (Chugoku Usagi) / 栗田まろん (Kurita Maron) / あいえるたん (Aierutan) / 満別花丸 (Manbetsu Hanamaru) / 琴詠ニア (Kotoyomi Nia) / Voidoll / ぞん子 (Zonko) / 中部つるぎ (Chubu Tsurugi) / 離途 (Rito) / 黒沢冴白 (Kurosawa Saehaku) / ユーレイちゃん (Yurei-chan) / 東北ずん子 (Tohoku Zunko) / 東北きりたん (Tohoku Kiritan) / 東北イタコ (Tohoku Itako) / あんこもん (Ankomon) / 夜語トバリ (Yogatari Tobari) / 暁記ミタマ (Akatsuki Mitama) / 里石ユカ (Satoishi Yuka)

For per-role porting credits, see [`CHANGELOG.md`](./CHANGELOG.md) and individual commit messages.

---

Among Us is © 2018–2026 Innersloth LLC. End K not is not affiliated with or endorsed by Innersloth. Portions of the materials used are property of Innersloth LLC.
