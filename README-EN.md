# End K not

[日本語](README.md)

[![Discord](https://img.shields.io/badge/Discord-join-5865F2?logo=discord&logoColor=white)](https://discord.gg/sEYAFzD3a)

## About this mod

**End K not** is an unofficial personal fork of [Endless Host Roles (EHR)](https://github.com/Gurge44/EndlessHostRoles) for Among Us. It currently implements **650+ roles**.

Only the lobby host needs to install the mod — other players can join and enjoy the additional roles without installing anything. It works fully on both official and custom servers.

This mod is unofficial and is **not affiliated with or endorsed by Innersloth**. **Please do not contact Innersloth regarding any issues with this mod.**

> [!WARNING]
> End K not is in **alpha**. Some roles are untested and several features are works-in-progress. Please report bugs and suggestions on [GitHub Issues](../../issues) or our [Discord](https://discord.gg/sEYAFzD3a).

Supported Among Us version: **2026.3.31**

## Features

On top of EHR's 650+ role engine, End K not adds its own original feature set for **streaming, long-running hosting, and original content.**

### 🎥 Streaming & long-running hosting

- **Per-crew text-to-speech (VOICEVOX integration)** — Reads each player's chat aloud in a distinct voice. It drives a locally-installed copy of [VOICEVOX](https://voicevox.hiroshiba.jp/); the audio plays only on the host's machine (your stream) and is never sent to the game. Voices can also be pinned per player name or friend code. *(See [Credits](#credits) for the attribution required when streaming.)*
- **Auto re-host & crash self-recovery** — If the host is kicked or dropped by the official server, End K not automatically re-creates a new lobby with the same region and settings. And if Among Us itself crashes or hangs, the bundled external watchdog detects it and relaunches the game to restore the lobby — so it keeps running unattended through 24-hour soaks and long streams.
- **BGM system** — Replaceable background music for menu / lobby / in-task / climax / meeting / result. Default tracks bundled.
- **YouTube live chat overlay** — Displays your YouTube live chat on top of the game screen while streaming.

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

**658 roles in total** (512 faction roles + 146 sub-roles/add-ons), organized by faction.

| Faction | Count |
|---------|-------|
| Impostor | 161 (vanilla 4 + remake 4 + custom 153) |
| Crewmate | 170 (vanilla 6 + remake 7 + custom 157) |
| Neutral | 131 |
| Coven | 21 |
| Game mode exclusive | 27 |
| Other | 2 (GM / Convict) |
| Sub-roles (add-ons) | 146 |

> [!TIP]
> ### Featured role: WaveCannon
> An Impostor role built on the Phantom basis. Aim with the pet button; after a warning line appears, a massive beam sweeps across the map. One of the most popular roles in this mod — re-implemented host-only for End K not, with WaveCannon designs from SuperNewRoles and TownOfHost-Pko as reference.

Use `/r <role name>` or `/myrole` in-game to check each role's effects and settings.

### Impostor (161)

|   |   |   |   |
|---|---|---|---|
| Ambusher | Catalyst | Centralizer | ClockBlocker |
| Exclusionary | Fabricator | Fakeshifter | Loner |
| Perplexer | Postponer | Psychopath | Venerer |
| Viper | ViperEndKnot | Archer | EarnestWolf |
| Anonymous | Abyssbringer | Arrogance | Undertaker |
| AntiAdminer | AntiReporter | Android | EvilGambler |
| EvilSatellite | EvilJumper | EvilTeller | EvilBlender |
| EvilBomber | EvilMagician | EvilMaker | EvilEraser |
| EvilGuesser | EvilTracker | Insider | Inhibitor |
| Impostor | ImpostorEndKnot | Vampire | Vindicator |
| Walker | Warlock | Echo | Escapist |
| Augmenter | Overkiller | Overheat | Occultist |
| Comebacker | Camouflager | CharismaStar | Gangster |
| Cantankerous | Gambler | QuickKiller | Greedy |
| Cleaner | Crewpostor | Chronomancer | Godfather |
| ConnectSaver | Consigliere | Silencer | Sapper |
| Satellite | Saboteur | Shapeshifter | ShapeshifterEndKnot |
| Generator | Shyboy | Swiftclaw | Swooper |
| Scavenger | Wasp | Stasis | Stealth |
| Sniper | Snowman | Swapster | SoulCatcher |
| Zombie | TimeThief | Dazzler | Changeling |
| Twister | Disperser | Decrescendo | Deathpact |
| Duellist | TeleportKiller | ToiletFan | Trapster |
| Trickster | NiceLogger | Nuker | Ninja |
| Nemesis | Notifier | BountyHunter | Puppeteer |
| Parasite | Ballooner | Hangman | Visionary |
| Hitman | Phantom | PhantomEndKnot | Freezer |
| Framer | ProgressKiller | ProBowler | Penguin |
| VentOpener | VentMaster | Bomber | Miner |
| QuickShooter | Mafioso | Morphling | Lurker |
| LovingImpostor | RiftMaker | Limiter | Wiper |
| Wildling | YinYanger | Fireworker | Hypocrite |
| Forger | Blackmailer | Bard | Hypnotist |
| KillingMachine | Librarian | Commander | Capitalist |
| Mastermind | CursedWolf | Kamikaze | Assumer |
| Balancer | **WaveCannon** | Consort | Renegade |
| Councillor | Ventriloquist | Pathologist | Witch |
| Nullifier | Kidnapper | Mercenary | Lightning |
| ShrineMaiden | Devourer | Butcher | Chainbinder |
| Exorcist | Frightener | Obstructer | Skinwalker |
| Riptide |  |  |  |

### Crewmate (170)

|   |   |   |   |
|---|---|---|---|
| Adrenaline | AmateurTeller | Altruist | Ankylosaurus |
| Ignitor | Insight | Inspector | InSender |
| Wizard | Whisperer | Rabbit | UltraStar |
| Express | Escort | Enigma | Electric |
| Engineer | EngineerEndKnot | Oxyman | Observer |
| Guardian | GuardMaster | Gasp | Goose |
| CameraMan | Chameleon | Catcher | Captain |
| Grappler | CrewmateEndKnot | Crusader | Grenadier |
| Cleanser | CopyCat | Gaulois | Chef |
| Sheriff | Shiftguard | Journalist | SuperStar |
| Scout | Scanner | Snitch | Spy |
| SpeedBooster | Speedrunner | Spiritualist | Swapper |
| Safeguard | Sensor | Tar | TimeMaster |
| TimeManager | TaskManager | DoubleAgent | DummySpawner |
| Dictator | Detour | Detective | DetectiveEndKnot |
| Tether | Telekinetic | Telecommunication | Doorjammer |
| ToiletMaster | DonutDelivery | Doctor | Tracker |
| TrackerEndKnot | Transporter | Transmitter | Druid |
| Tornado | Drainer | Tracefinder | Tunneler |
| NiceEraser | NiceGuesser | Nightmare | Noisemaker |
| NoisemakerEndKnot | Vacuum | Pacifist | Hacker |
| Battery | Dad | Paranoid | Beacon |
| Bane | Veteran | Ventguard | PortalMaker |
| Bodyguard | PonkotuTeller | Markseeker | Marshall |
| Judge | Medium | Mayor | Medic |
| Mole | Lighter | Rhapsode | Randomizer |
| Leery | Ricochet | Decryptor | Carrier |
| Sentry | Scientist | ScientistEndKnot | MeetingManager |
| Demolitionist | Jailor | Perceiver | King |
| SecurityGuard | Coroner | Lookout | Benefactor |
| Negotiator | Luckey | Merchant | Aid |
| Vigilante | Car | Inquirer | Socialite |
| Sentinel | GuardianAngel | GuardianAngelEndKnot | Bestower |
| Helper | Convener | Witness | Oracle |
| Inquisitor | Mathematician | Mechanic | Astral |
| Clairvoyant | FortuneTeller | Teller | Mortician |
| LazyGuy | President | Addict | Investigator |
| Gardener | Monarch | Autocrat | Farmer |
| Deputy | Analyst | Unshifter | Retributionist |
| Forensic | Adventurer | Imitator | Tree |
| Soothsayer | Psychic | LovingCrewmate | Alchemist |
| WolfBoy | ForceFielder | Akazukin | Sandbox |
| Operative | Survivor |  |  |

### Neutral (131)

|   |   |   |   |
|---|---|---|---|
| SerialKiller | Accumulator | Berserker | Clerk |
| Duality | Explosivist | MassMedia | Quarry |
| Sharpshooter | Slenderman | SoulCollector | Spider |
| Thanos | Thief | Arsonist | Agitator |
| Amogus | Impartial | Vulture | Virus |
| WeaponMaster | Eclipse | Evolver | Enderman |
| Opportunist | Curser | CurseMaker | Gaslighter |
| Cultist | QuizMaster | Glitch | Collector |
| Sidekick | Simon | SantaClaus | Jester |
| Shifter | Juggernaut | Jackal | SchrodingersCat |
| Jinx | Starspawn | Stalker | Spirit |
| Spiritcaller | Sprayer | Pickpocket | SoulHunter |
| Turncoat | Tiger | Tank | Cherokious |
| Dealer | Deathknight | Terrorist | Doppelganger |
| Tremor | NecroGuesser | Necromancer | NoteKiller |
| Nonplus | Bargainer | Backstabber | Patroller |
| Bubble | Banker | Specter | Follower |
| Hookshot | BloodKnight | Vector | Pestilence |
| HexMaster | HeadHunter | Pelican | Poisoner |
| Pawn | Vortex | Maverick | Magician |
| Missioneer | Medusa | Monochromer | Remotekiller |
| RoomRusher | RouleteGrandeur | Wraith | Rogue |
| Romantic | Workaholic | Demon | PlagueBearer |
| Chemist | Revolutionist | Infection | Amnesiac |
| Ritualist | Technician | Mycologist | Magistrate |
| Seamstress | Bandit | Samurai | Doomsayer |
| Executioner | God | Sunnyboy | Tama |
| Provocateur | Pursuer | Weatherman | Investor |
| JackalHadouHo | Hater | VengefulRomantic | Vengeance |
| Lawyer | Pyromaniac | Beehive | Auditor |
| RuthlessRomantic | Reckless | Pulse | Postman |
| Traitor | Predator | Werewolf | Strawdoll |
| Innocent | Blockade | Jackpot |  |

### Coven (21)

|   |   |   |   |
|---|---|---|---|
| Empress | MoonDancer | Illusionist | Wyrd |
| Enchanter | Augur | CovenMember | CovenLeader |
| Summoner | SpellCaster | Siren | Timelord |
| Banshee | VoodooMaster | Poache | PotionMaster |
| Reaper | Death | Goddess | Dreamweaver |
| Shadow |  |  |  |

### Game mode exclusive (27)

|   |   |   |   |
|---|---|---|---|
| DEATHRACE (Racer) | FREE FOR ALL (Killer) | MINGLE (MinglePlayer) | SNOWDOWN (SnowdownPlayer) |
| THE MIND GAME (TMGPlayer) | Agent | QuizPlayer | Seeker |
| Jet | Jumper | Runner | KOTZPlayer |
| Tasker | Taskinator | Dasher | Challenger |
| Detector | Troll | Hider | Fox |
| Potato | BedWarsPlayer | Venter | RRPlayer |
| Locator | CTFPlayer | NDPlayer |  |

### Other (2)

|   |   |   |   |
|---|---|---|---|
| GM | Convict |  |  |

### Sub-roles / Add-ons (146)

|   |   |   |   |
|---|---|---|---|
| TaskMaster | Venom | Absorber | Avenger |
| Amnesia | Allergic | Anchor | AntiTP |
| Antidote | Undead | Unbound | Unlucky |
| Mischievous | Insane | Warden | Watcher |
| Entranced | Aide | Flash | Egoist |
| Autopsy | Talkative | Onbound | GA |
| Fool | Glow | Guesser | Commited |
| Concealer | Constricted | Composter | Diseased |
| Sunglasses | Seer | Shade | Asthmatic |
| Shy | Stressed | Spurt | Giant |
| Sleep | Sleuth | Sonar | Tired |
| Dynamo | Tiebreaker | Taskcounter | DoubleShot |
| Damocles | Disco | Deadlined | DeadlyQuota |
| Torch | Clumsy | Disregarded | Necroview |
| Noisy | Oblivious | BananaMan | Busy |
| Finder | Facilitator | Bewilder | Blind |
| Fragile | Bloodmoon | Blocked | Beartrap |
| Bait | Introvert | Magnet | Madmate |
| Minion | Mimic | Mare | Messenger |
| Youtuber | Rascal | LastImpostor | Lucky |
| Lovers | Reach | Listener | Rookie |
| Looter | Lazy | Loyal | Workhorse |
| EvilSpirit | Stained | Blessed | Compelled |
| Nimble | Knighted | Urgent | Trainee |
| Energetic | Bloodlust | Swift | Underdog |
| Haunter | Haste | Cleansed | Examiner |
| Circumvent | Stealer | Contagious | Schizophrenic |
| Hidden | Truant | Physicist | Gravestone |
| Charmed | Phantasm | Amanojaku | AsistingAngel |
| Connecting | DemonicCrusher | DemonicSupporter | DemonicTracker |
| DemonicVenter | Dizzy | Elector | Entombed |
| Faction | GhostNoiseSender | Ghostbuttoner | GhostReseter |
| GhostRumour | InfoPoor | LastNeutral | MagicHand |
| MeetingAngel | Moon | News | NonReport |
| Notvoter | OneWolf | Opener | Reroll |
| Serial | SlowStarter | Stack | Transparent |
| Twins | Water |  |  |

## Commands

Over 110 chat commands are available for hosts, moderators, and all players. See [`COMMANDS.md`](./COMMANDS.md) for the full list. Run `/help` in-game to see only the commands available in your current context.

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
- **[TownOfHost-K](https://github.com/KYMario/TownOfHost-K)** (KYMario et al.) — source of many roles, streaming-support features, and the official-server packet-splitting safety net, GPL-3.0
- **[SuperNewRoles](https://github.com/SuperNewRoles/SuperNewRoles)** (SuperNewRoles team) — WaveCannon design reference, GPL-3.0
- **[TownOfHost-Pko](https://github.com/satokazoku/TownOfHost-Pko)** (satokazoku et al.) — WaveCannon design reference, GPL-3.0
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
