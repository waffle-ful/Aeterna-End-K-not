# End K not

[English](README-EN.md)

[![Discord](https://img.shields.io/badge/Discord-join-5865F2?logo=discord&logoColor=white)](https://discord.gg/sEYAFzD3a)

## このMod について

**End K not** は、[Endless Host Roles (EHR)](https://github.com/Gurge44/EndlessHostRoles) をベースとした Among Us の非公式個人フォークです。 **600+ の役職**の役職が現在あります。

ホストのクライアントに導入するだけで動作し、他のプレイヤーは Mod を導入せずに追加役職を楽しめます。

このMod は非公式のものであり、Among Us の開発元である Innersloth は一切関与していません。**このMod の問題に関して Innersloth へ問い合わせないでください。**

> [!WARNING]
> End K not は **alpha 段階**です。未テスト役職や WIP 機能を含みます。不具合報告や提案は [GitHub Issues](../../issues) または [Discord](https://discord.gg/sEYAFzD3a) へお願いします。

対応 Among Us バージョン : **2026.3.31**

> [!IMPORTANT]
> **快適に遊ぶには、カスタムサーバー（Modded リージョン）でのプレイを推奨します。**
> 公式サーバーでは Innersloth の anti-cheat により、一部の見た目変更（ペット・スキン・カモフラージュ等）が自動的に制限されます。`aumods.org` / `duikbo.at` 系などの Modded リージョンなら、すべての役職をフルに楽しめます（詳細は下記「サーバーについて（公式サーバーでの制限）」を参照）。

## End K not の特徴

- **EHR + TOHK の役職統合** — EHR の役職セットに加えて、TOHK 由来の役職を RoleBase 化して移植中
- **Calamity(Terraria) テーマのメインメニュー** *(開発中)* — Calamity 風カスタムメインメニュー UI を実装中（[CalamityModPublic](https://github.com/CalamityTeam/CalamityModPublic) を参考）
- **BGM システム** — メニュー / ロビー / 任務中 / 会議 / 結果画面の BGM をホストが差し替え可能。デフォルト BGM 同梱
- **外部通信の無効化** — EHR 上流が行っていた実績 API・オンラインプリセット・ニュース取得などの通信を無効化。自Mod の更新確認（GitHub API）と Bard・アナグラム等の一部役職ゲーム機能を除き、外部への通信は行いません
- **GPL-3.0 オープンソース** — ソースコード全公開、改変・再配布自由

## 役職一覧

実装済みは **614 役職 + 114 サブ役職** （アドオン）。陣営ごとに整理しています。

| 陣営 | 役職数 |
|------|-------|
| インポスター | 154 (ヴァニラ4 + リメイク4 + カスタム146) |
| クルーメイト | 165 (ヴァニラ6 + リメイク7 + カスタム152) |
| ニュートラル | 129 |
| カバン (Coven) | 20 |
| ゲームモード専用 | 27 |
| その他 | 2 (GM / Convict) |
| サブ役職（アドオン） | 114 |

> [!TIP]
> ### 注目役職: 波動砲 (WaveCannon)
> Phantom 派生のインポスター役職。pet ボタンで方向を狙い、警告ラインのあと巨大なビームでマップを薙ぎ払います。当 Mod でも特に人気の役職で、SuperNewRoles と TownOfHost-Pko の波動砲設計を参考に、End K not 用にホストオンリーで再実装しました。

各役職の効果や設定はゲーム内で `/r <役職名>` または `/myrole` で確認できます。

### インポスター系 (154)

|   |   |   |   |
|---|---|---|---|
| Ambusher | Catalyst | Centralizer | Clock Blocker (ClockBlocker) |
| Exclusionary | Fabricator | Fakeshifter | Loner |
| Perplexer | Postponer | Psychopath | Venerer |
| Viper | Viper (ViperEndKnot) | アーチャー (Archer) | アーネストウルフ (EarnestWolf) |
| アノニマス (Anonymous) | アビスブリンガー (Abyssbringer) | アロガンス (Arrogance) | アンダーテイカー (Undertaker) |
| アンチアドミナー (AntiAdminer) | アンチリポーター (AntiReporter) | アンドロイド (Android) | イービルギャンブラー (EvilGambler) |
| イービルサテライト (EvilSatellite) | イービルジャンパー (EvilJumper) | イービルテラー (EvilTeller) | イービルブレンダー (EvilBlender) |
| イービルボマー (EvilBomber) | イービルマジシャン (EvilMagician) | イービルメイカー (EvilMaker) | イビルイレイサー (EvilEraser) |
| イビルゲッサー (EvilGuesser) | イビルトラッカー (EvilTracker) | インサイダー (Insider) | インヒビター (Inhibitor) |
| インポスター (Impostor) | インポスター (ImpostorEndKnot) | ヴァンパイア (Vampire) | ヴィンディケーター (Vindicator) |
| ウォーカー (Walker) | ウォーロック (Warlock) | エコー (Echo) | エスカピスト (Escapist) |
| オーグメンター (Augmenter) | オーバーキラー (Overkiller) | オーバーヒート (Overheat) | オカルティスト (Occultist) |
| カムバッカー (Comebacker) | カモフラージャー (Camouflager) | カリスマスター (CharismaStar) | ギャングスター (Gangster) |
| キャンタンカラス (Cantankerous) | ギャンブラー (Gambler) | クイックキラー (QuickKiller) | グリーディ (Greedy) |
| クリーナー (Cleaner) | クルーポスター (Crewpostor) | クロノマンサー (Chronomancer) | ゴッドファーザー (Godfather) |
| コネクトセイバー (ConnectSaver) | コンシリエーレ (Consigliere) | サイレンサー (Silencer) | サッパー (Sapper) |
| サテライト (Satellite) | サボタージャー (Saboteur) | シェイプシフター (Shapeshifter) | シェイプシフター (ShapeshifterEndKnot) |
| ジェネレーター (Generator) | シャイボーイ (Shyboy) | スイフトクロー (Swiftclaw) | スゥーパー (Swooper) |
| スカベンジャー (Scavenger) | スズメバチ (Wasp) | ステーシス (Stasis) | ステルス (Stealth) |
| スナイパー (Sniper) | スノーマン (Snowman) | スワップスター (Swapster) | ソウルキャッチャー (SoulCatcher) |
| ゾンビ (Zombie) | タイムシーフ (TimeThief) | ダズラー (Dazzler) | チェンジリング (Changeling) |
| ツイスター (Twister) | ディスパーサー (Disperser) | デクレッシェンド (Decrescendo) | デスパクト (Deathpact) |
| デュエリスト (Duellist) | テレポートキラー (TeleportKiller) | トイレファン (ToiletFan) | トラップスター (Trapster) |
| トリックスター (Trickster) | ナイスロガー (NiceLogger) | ニューカー (Nuker) | ニンジャ (Ninja) |
| ネメシス (Nemesis) | ノティファイアー (Notifier) | バウンティハンター (BountyHunter) | パペッティア (Puppeteer) |
| パラサイト (Parasite) | バルーナー (Ballooner) | ハングマン (Hangman) | ビショナリー (Visionary) |
| ヒットマン (Hitman) | ファントム (Phantom) | ファントム (PhantomEndKnot) | フリーザー (Freezer) |
| フレーマー (Framer) | プログレスキラー (ProgressKiller) | プロボウラー (ProBowler) | ペンギン (Penguin) |
| ベントオープナー (VentOpener) | ベントマスター (VentMaster) | ボマー (Bomber) | マイナー (Miner) |
| マガジナー (QuickShooter) | マフィオソ (Mafioso) | モーフィング (Morphling) | ラーカー (Lurker) |
| ラビングインポスター (LovingImpostor) | リフトメーカー (RiftMaker) | リミッター (Limiter) | ワイパー (Wiper) |
| ワイルディング (Wildling) | 陰陽師 (YinYanger) | 花火師 (Fireworker) | 偽善者 (Hypocrite) |
| 偽造者 (Forger) | 恐喝者 (Blackmailer) | 吟遊詩人 (Bard) | 催眠術師 (Hypnotist) |
| 殺人マシーン (KillingMachine) | 司書 (Librarian) | 指揮官 (Commander) | 資本主義者 (Capitalist) |
| 首謀者 (Mastermind) | 呪われた狼 (CursedWolf) | 神風 (Kamikaze) | 想定者 (Assumer) |
| 天秤 (Balancer) | **波動砲 (WaveCannon)** | 配偶者 (Consort) | 反逆者 (Renegade) |
| 評議員 (Councillor) | 腹話術師 (Ventriloquist) | 法医 (Pathologist) | 魔女 (Witch) |
| 無効主義者 (Nullifier) | 誘拐犯 (Kidnapper) | 傭兵 (Mercenary) | 落雷 (Lightning) |
| 巫女 (ShrineMaiden) | 貪食者 (Devourer) |  |  |

### クルーメイト系 (165)

|   |   |   |   |
|---|---|---|---|
| アドレナリン (Adrenaline) | アマチュアテラー (AmateurTeller) | アルチュアリスト (Altruist) | アンキロサウルス (Ankylosaurus) |
| イグナイター (Ignitor) | インサイト (Insight) | インスペクター (Inspector) | インセンダー (InSender) |
| ウィザード (Wizard) | ウィスパー (Whisperer) | うさぎ (Rabbit) | ウルトラスター (UltraStar) |
| エクスプレス (Express) | エスコート (Escort) | エニグマ (Enigma) | エレクトリック (Electric) |
| エンジニア (Engineer) | エンジニア (EngineerEndKnot) | オキシマン (Oxyman) | オブサーバー (Observer) |
| ガーディアン (Guardian) | ガードマスター (GuardMaster) | ガスプ (Gasp) | ガチョウ (Goose) |
| カメラマン (CameraMan) | カメレオン (Chameleon) | キャッチャー (Catcher) | キャプテン (Captain) |
| グラップラー (Grappler) | クルーメイト (CrewmateEndKnot) | クルセイダー (Crusader) | グレネーダー (Grenadier) |
| クレンザー (Cleanser) | コピーキャット (CopyCat) | ゴロワ (Gaulois) | シェフ (Chef) |
| シェリフ (Sheriff) | シフトガード (Shiftguard) | ジャーナリスト (Journalist) | スーパースター (SuperStar) |
| スカウト (Scout) | スキャナー (Scanner) | スニッチ (Snitch) | スパイ (Spy) |
| スピードブースター (SpeedBooster) | スピードランナー (Speedrunner) | スピリチュアリスト (Spiritualist) | スワッパー (Swapper) |
| セーフガード (Safeguard) | センサー (Sensor) | タール (Tar) | タイムマスター (TimeMaster) |
| タイムマネージャー (TimeManager) | タスクマネージャー (TaskManager) | ダブルエージェント (DoubleAgent) | ダミースポウナー (DummySpawner) |
| ディクテーター (Dictator) | ディター (Detour) | ディテクティブ (Detective) | ディテクティブ (DetectiveEndKnot) |
| テザー (Tether) | テレキネシス (Telekinetic) | テレコミュニケーション (Telecommunication) | ドアジャマー (Doorjammer) |
| トイレマスター (ToiletMaster) | ドーナツの配達員 (DonutDelivery) | ドクター (Doctor) | トラッカー (Tracker) |
| トラッカー (TrackerEndKnot) | トランスポーター (Transporter) | トランスミッター (Transmitter) | ドルイド (Druid) |
| トルネード (Tornado) | ドレイナー (Drainer) | トレースファインダー (Tracefinder) | トンネラー (Tunneler) |
| ナイスイレイサー (NiceEraser) | ナイスゲッサー (NiceGuesser) | ナイトメア (Nightmare) | ノイズメーカー (Noisemaker) |
| ノイズメーカー (NoisemakerEndKnot) | バキューム (Vacuum) | パシフィスト (Pacifist) | ハッカー (Hacker) |
| バッテリー (Battery) | パパ (Dad) | パラノイド (Paranoid) | ビーコン (Beacon) |
| ベイン (Bane) | ベテラン (Veteran) | ベントガード (Ventguard) | ポータル設置者 (PortalMaker) |
| ボディーガード (Bodyguard) | ポンコツテラー (PonkotuTeller) | マークシーカー (Markseeker) | マーシャル (Marshall) |
| ミーティングシェリフ (Judge) | ミディアム (Medium) | メイヤー (Mayor) | メディック (Medic) |
| モグラ (Mole) | ライター (Lighter) | ラプソード (Rhapsode) | ランダマイザー (Randomizer) |
| リーリー (Leery) | リコシェット (Ricochet) | 暗号解読者 (Decryptor) | 運搬者 (Carrier) |
| 衛兵 (Sentry) | 科学者 (Scientist) | 科学者 (ScientistEndKnot) | 会議マネージャー (MeetingManager) |
| 解体屋 (Demolitionist) | 看守 (Jailor) | 観察者 (Perceiver) | 君臨者 (King) |
| 警備員 (SecurityGuard) | 検死官 (Coroner) | 見張り (Lookout) | 後援者 (Benefactor) |
| 交渉人 (Negotiator) | 幸福者 (Luckey) | 行商人 (Merchant) | 支援者 (Aid) |
| 自警団員 (Vigilante) | 自動車 (Car) | 質問者 (Inquirer) | 社交家 (Socialite) |
| 守衛 (Sentinel) | 守護天使 (GuardianAngel) | 守護天使 (GuardianAngelEndKnot) | 授与者 (Bestower) |
| 助っ人 (Helper) | 召喚士 (Convener) | 証人 (Witness) | 神託者 (Oracle) |
| 尋問者 (Inquisitor) | 数学者 (Mathematician) | 整備士 (Mechanic) | 星華 (Astral) |
| 千里眼の主 (Clairvoyant) | 占い師 (FortuneTeller) | 占い師 (Teller) | 葬儀屋 (Mortician) |
| 怠け者 (LazyGuy) | 大統領 (President) | 中毒者 (Addict) | 調査官 (Investigator) |
| 庭師 (Gardener) | 統治者 (Monarch) | 独裁者 (Autocrat) | 農夫 (Farmer) |
| 副官 (Deputy) | 分析者 (Analyst) | 変身解除者 (Unshifter) | 報復者 (Retributionist) |
| 法医学者 (Forensic) | 冒険者 (Adventurer) | 模倣者 (Imitator) | 木 (Tree) |
| 預言者 (Soothsayer) | 霊能者 (Psychic) | 恋人 (LovingCrewmate) | 錬金術師 (Alchemist) |
| 狼少年 (WolfBoy) | フォースフィールダー(ForceFielder) | あかづきん (Akazukin) | ???(???) |

### ニュートラル系 (129)

|   |   |   |   |
|---|---|---|---|
| <size=75%>シリアルキラー</size> (SerialKiller) | Accumulator | Berserker | Clerk |
| Duality | Explosivist | MassMedia | Quarry |
| Sharpshooter | Slenderman | Soul Collector (SoulCollector) | Spider |
| Thanos | Thief | アーソニスト (Arsonist) | アジテーター (Agitator) |
| アモガス (Amogus) | インパーシャル (Impartial) | ヴァルチャー (Vulture) | ウイルス (Virus) |
| ウェポンマスター (WeaponMaster) | エクリプス (Eclipse) | エボルバー (Evolver) | エンダーマン (Enderman) |
| オポチュニスト (Opportunist) | カーサー (Curser) | カースメーカー (CurseMaker) | ガスライター (Gaslighter) |
| カルティスト (Cultist) | クイズマスター (QuizMaster) | グリッチ (Glitch) | コレクター (Collector) |
| サイドキック (Sidekick) | サイモン (Simon) | サンタクロース (SantaClaus) | ジェスター (Jester) |
| シフター (Shifter) | ジャガーノート (Juggernaut) | ジャッカル (Jackal) | シュレディンガーの猫 (SchrodingersCat) |
| ジンクス (Jinx) | スタースポーン (Starspawn) | ストーカー (Stalker) | スピリット (Spirit) |
| スピリットコーラー (Spiritcaller) | スプレイヤー (Sprayer) | スリ (Pickpocket) | ソウルハンター (SoulHunter) |
| ターンコート (Turncoat) | タイガー (Tiger) | タンク (Tank) | チェロキアス (Cherokious) |
| ディーラー (Dealer) | デスナイト (Deathknight) | テロリスト (Terrorist) | ドッペルゲンガー (Doppelganger) |
| トレマー (Tremor) | ネクロゲッサー (NecroGuesser) | ネクロマンサー (Necromancer) | ノートキラー (NoteKiller) |
| ノンプラス (Nonplus) | バーゲイナー (Bargainer) | バックスタバー (Backstabber) | パトローラー (Patroller) |
| バブル (Bubble) | バンカー (Banker) | ファンタズム (Specter) | フォロワー (Follower) |
| フックショット (Hookshot) | ブラッドナイト (BloodKnight) | ベクター (Vector) | ペスティレンス (Pestilence) |
| ヘックスマスター (HexMaster) | ヘッドハンター (HeadHunter) | ペリカン (Pelican) | ポイズナー (Poisoner) |
| ポーン (Pawn) | ボルテックス (Vortex) | マーベリック (Maverick) | マジシャン (Magician) |
| ミッショナー (Missioneer) | メデューサ (Medusa) | モノクローマー (Monochromer) | リモートキラー (Remotekiller) |
| ルームラッシャー (RoomRusher) | ルーレットグランジャー (RouleteGrandeur) | レイス (Wraith) | ローグ (Rogue) |
| ロマンチック (Romantic) | ワーカホリック (Workaholic) | 悪魔 (Demon) | 疫病媒介者 (PlagueBearer) |
| 化学者 (Chemist) | 革命主義者 (Revolutionist) | 感染症 (Infection) | 記憶喪失者 (Amnesiac) |
| 儀式師 (Ritualist) | 技術者 (Technician) | 菌学者 (Mycologist) | 行政官 (Magistrate) |
| 裁縫師 (Seamstress) | 山賊 (Bandit) | 侍 (Samurai) | 終末預言者 (Doomsayer) |
| 処刑人 (Executioner) | 神様 (God) | 晴れ男 (Sunnyboy) | 弾 (Tama) |
| 挑発者 (Provocateur) | 追跡者 (Pursuer) | 天気予報士 (Weatherman) | 投資家 (Investor) |
| 波動砲ジャッカル (JackalHadouHo) | 爆ぜ師 (Hater) | 復讐に燃えるロマンチック (VengefulRomantic) | 復讐者 (Vengeance) |
| 弁護士 (Lawyer) | 放火狂 (Pyromaniac) | 蜂の巣 (Beehive) | 傍聴人 (Auditor) |
| 無慈悲で冷酷なロマンチック (RuthlessRomantic) | 無謀者 (Reckless) | 模倣者 (Pulse) | 郵便配達員 (Postman) |
| 裏切り者 (Traitor) | 略奪者 (Predator) | 狼男 (Werewolf) | 藁人形 (Strawdoll) |
| 冤罪師 (Innocent) |  |  |  |

### カバン (Coven) (20)

|   |   |   |   |
|---|---|---|---|
| Empress | Moon Dancer (MoonDancer) | イリュージョニスト (Illusionist) | ウルド (Wyrd) |
| エンチャーター (Enchanter) | オーガー (Augur) | カヴンメンバー (CovenMember) | カヴンリーダー (CovenLeader) |
| サモナー (Summoner) | スペルキャスター (SpellCaster) | セイレーン (Siren) | タイムロード (Timelord) |
| バンシー (Banshee) | ブードゥーマスター (VoodooMaster) | ポウチ (Poache) | ポーションマスター (PotionMaster) |
| リーパー (Reaper) | 死神 (Death) | 女神 (Goddess) | 夢織師 (Dreamweaver) |

### ゲームモード専用 (27)

|   |   |   |   |
|---|---|---|---|
| DEATHRACE (Racer) | FREE FOR ALL (Killer) | MINGLE (MinglePlayer) | SNOWDOWN (SnowdownPlayer) |
| THE MIND GAME (TMGPlayer) | エージェント (Agent) | クイズ (QuizPlayer) | シーカー (Seeker) |
| ジェット (Jet) | ジャンパー (Jumper) | スピードランナー (Runner) | ゾーンの王者 (KOTZPlayer) |
| タスカー (Tasker) | タスキネーター (Taskinator) | ダッシャー (Dasher) | チャレンジャー (Challenger) |
| ディテクター (Detector) | トロール (Troll) | ハイダー (Hider) | フォックス (Fox) |
| プレイヤー (Potato) | ベッドウォーズ (BedWarsPlayer) | ベンター (Venter) | ルーム ラッシュ (RRPlayer) |
| ロケーター (Locator) | 旗の捕獲者 (CTFPlayer) | 自然災害 (NDPlayer) |  |

### その他 (2)

|   |   |   |   |
|---|---|---|---|
| ゲームマスター (GM) | 犯罪者 (Convict) |  |  |

### サブ役職・アドオン (114)

|   |   |   |   |
|---|---|---|---|
| Task Master (TaskMaster) | Venom | アブソーバー (Absorber) | アベンジャー (Avenger) |
| アムネジア (Amnesia) | アレルギー (Allergic) | アンカー (Anchor) | アンチテレポート (AntiTP) |
| アンチドーテ (Antidote) | アンデッド (Undead) | アンバウンド (Unbound) | アンラッキー (Unlucky) |
| いたずら好き (Mischievous) | インセイン (Insane) | ウォーデン (Warden) | ウォッチャー (Watcher) |
| うっとり (Entranced) | エイド (Aide) | エクスプレス (Flash) | エゴイスト (Egoist) |
| オートプシー (Autopsy) | おしゃべり (Talkative) | オンバウンド (Onbound) | ガーディアンエンジェル (GA) |
| クラムシー (Fool) | グロー (Glow) | ゲッサー (Guesser) | コミッテッド (Commited) |
| コンシーラー (Concealer) | コンストリクテッド (Constricted) | コンポスター (Composter) | ご病気 (Diseased) |
| サングラス (Sunglasses) | シーア (Seer) | シェード (Shade) | シグナル (Asthmatic) |
| シャイ (Shy) | ストレスを感じる (Stressed) | スパート (Spurt) | スラッギッシュ (Giant) |
| スリープ (Sleep) | スルース (Sleuth) | ソナー (Sonar) | タイアード (Tired) |
| ダイナモ (Dynamo) | タイブレイカー (Tiebreaker) | タスクカウンター (Taskcounter) | ダブルショット (DoubleShot) |
| ダモクレス (Damocles) | ディスコ (Disco) | デッドライン (Deadlined) | デッドリークォータ (DeadlyQuota) |
| トーチ (Torch) | ドジ (Clumsy) | トランスパレント (Disregarded) | ネクロビュー (Necroview) |
| ノイジー (Noisy) | ノンレポート (Oblivious) | バナニー (BananaMan) | ビジー (Busy) |
| ファインダー (Finder) | ファシリテーター (Facilitator) | ブラインダー (Bewilder) | ブラインド (Blind) |
| フラジャイル (Fragile) | ブラッドムーン (Bloodmoon) | ブロックド (Blocked) | ベアトラップ (Beartrap) |
| ベイティング (Bait) | ぼっち (Introvert) | マグネット (Magnet) | マッドメイト (Madmate) |
| ミニオン (Minion) | ミミック (Mimic) | メアー (Mare) | メッセンジャー (Messenger) |
| ユーチューバー (Youtuber) | ラスカル (Rascal) | ラストインポスター (LastImpostor) | ラッキー (Lucky) |
| ラバーズ (Lovers) | リーチ (Reach) | リスナー (Listener) | ルーキー (Rookie) |
| ルーター (Looter) | レイジー (Lazy) | ロイヤル (Loyal) | ワークホース (Workhorse) |
| 悪霊 (EvilSpirit) | 汚れている (Stained) | 加護 (Blessed) | 喜び (Compelled) |
| 器用 (Nimble) | 騎士 (Knighted) | 緊急 (Urgent) | 訓練生 (Trainee) |
| 元気いっぱい (Energetic) | 殺気 (Bloodlust) | 疾風 (Swift) | 弱者 (Underdog) |
| 呪霊 (Haunter) | 焦る (Haste) | 浄化 (Cleansed) | 調査官 (Examiner) |
| 通気阻止 (Circumvent) | 泥棒 (Stealer) | 伝染病 (Contagious) | 統合失調症 (Schizophrenic) |
| 匿名 (Hidden) | 不登校 (Truant) | 物理学者 (Physicist) | 墓石 (Gravestone) |
| 魅了した (Charmed) | 妖怪 (Phantasm) |  |  |

## コマンド一覧

ホスト/モデレーター/全員が使えるチャットコマンドを 110 種類以上実装しています。詳細は [`COMMANDS.md`](./COMMANDS.md) を参照してください。ゲーム内で `/help` を実行すると、現在の状況で使えるコマンドだけが表示されます。

## インストール

1. [BepInEx IL2CPP](https://github.com/BepInEx/BepInEx) を Among Us フォルダに導入
2. [Releases](../../releases) から最新の `EndKnot.dll` をダウンロード
3. `Among Us/BepInEx/plugins/` に配置
4. Among Us を起動

## サーバーについて（公式サーバーでの制限）

End K not の追加役職の多くは、ホストが各プレイヤーの見た目（ペット・スキン・色など）をサーバー経由で書き換えることで成立しています。**Innersloth 公式サーバー**では、2026 年のアップデートで導入された anti-cheat により、ホストが「Mod を入れていないプレイヤーに割り当てた一部の役職（内部的に役職を偽装する *desync 役職*）」の見た目を書き換えようとすると、**ホスト自身が `reason: Hacking` で切断**されてしまいます。

これはサーバー側の仕様変更であり、Mod 側のバグではありません（ベースである EHR 上流でも同じ症状が報告されています）。将来 Innersloth 側で挙動が変わる可能性があります。

公式サーバーでのこの切断を防ぐため、End K not は **公式サーバーにいる間だけ、該当プレイヤーへの見た目変更（ペット付与・スキン変更・カモフラージュなど）を自動的にスキップ**します。切断は起きませんが、その分だけ見た目に制限が出ます。

> [!IMPORTANT]
> **すべての機能をフルに使うには、カスタムサーバー（Modded リージョン）でのホストを推奨します。**
> Modded リージョン（例: `aumods.org` / `duikbo.at` 系）や自前サーバーには公式の anti-cheat が無いため、上記の制限なくペット・スキン・カモフラージュ等がそのまま動作します。リージョンの追加方法は各サーバー提供元の案内を参照してください。

## BGM のカスタマイズ

ホストが自前の楽曲に差し替えられます:

- 場所 : `Among Us/BepInEx/resources/BGM/`
- 対応形式 : `.ogg` / `.mp3` / `.wav`
- 対応スロット : `menu` / `lobby` / `intask` / `climax` / `meeting` / `result`
- ファイル名例 : `menu.ogg`、`lobby.mp3` など

`bgm_titles.json` を編集すると BGM 再生時のタイトル / 作者表示も切り替え可能です。ディスクに該当ファイルがあればそちらが優先され、無ければ同梱 BGM が再生されます。

## コミュニティ

- **Discord** : https://discord.gg/sEYAFzD3a — バグ報告・質問・雑談（推奨）
- **Issues** : [GitHub Issues](../../issues) — 確認が遅れる場合があります
- [`CODE_OF_CONDUCT.md`](./CODE_OF_CONDUCT.md) | [`CONTRIBUTING.md`](./CONTRIBUTING.md) | [`SECURITY.md`](./SECURITY.md) | [`SUPPORT.md`](./SUPPORT.md)

## ライセンス

このプロジェクトは **GNU General Public License v3.0** の下で公開されています。詳細は [`LICENSE`](./LICENSE) を参照してください。

End K not は [Endless Host Roles](https://github.com/Gurge44/EndlessHostRoles) の派生プロジェクトです。**2026 年 4 月以降の改変**は waffle-ful により行われており、改変履歴は本リポジトリの git log および [`CHANGELOG.md`](./CHANGELOG.md) で追跡できます (GPL-3.0 §5 準拠)。

## クレジット

- **[Endless Host Roles](https://github.com/Gurge44/EndlessHostRoles)** (Gurge44 他) — ベース Mod、GPL-3.0
- **[TownOfHost-K](https://github.com/KYMario/TownOfHost-K)** (KYMario 他) — 多くの役職、配信サポート機能、GPL-3.0
- **[SuperNewRoles](https://github.com/SuperNewRoles/SuperNewRoles)** (SuperNewRoles 開発チーム) — 波動砲 (WaveCannon) の設計参考、GPL-3.0
- **[TownOfHost-Pko](https://github.com/satokazoku/TownOfHost-Pko)** (satokazoku 他) — 波動砲の設計参考、GPL-3.0
- **[Town Of Host](https://github.com/tukasa0001/TownOfHost)** (tukasa0001 他) — TOH 系列の祖
- **[Town Of Host_ForE](https://github.com/AsumuAkaguma/TownOfHost_ForE)** — BGMカスタマイズ機能
- **[Town of Host: Enhanced (TOHE)](https://github.com/EnhancedNetwork/TownofHost-Enhanced)**(The Enhanced Network 開発チーム) — 多くの役職 

### Music Credits
DM DOKURO様のBGMが使われています
- [DM DOKURO YouTube Channel](https://www.youtube.com/@DMDOKURO)

自称芸術家みーさん様のBGMが使われています
- [HURT RECORD](https://www.hurtrecord.com/bgm/46/zero-no-heya.html)


---
その他については [`CHANGELOG.md`](./CHANGELOG.md) や各 commit メッセージを参照してください。

---

Among Us is © 2018–2026 Innersloth LLC. End K not は Innersloth と提携・公認されていません。Among Us の素材の一部は Innersloth LLC の財産です。
