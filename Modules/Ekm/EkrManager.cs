using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using UnityEngine;

namespace EndKnot.Modules.Ekm;

// R1 の per-holder ランタイム状態。EkrManager が生成/破棄/Pump を管理し、
// EkrLogicOpcodes (アクション opcode 実装) が直接参照してレートバケット・CNO スロットを読み書きする。
// 「1スロット複数人前提 (Maximum=15)」— キーは常に playerId (byte) であって slot ではない。
internal sealed class EkrHolderState
{
    public readonly Dictionary<string, float> Variables = new();
    public readonly List<EkrFiber> Fibers = [];

    // cno_spawn/cno_move/cno_despawn/cno_show/dummy_spawn の slot 引数 (1..3) に対応。index = slot - 1。
    // IEkrSlotCno 抽象で EkrCno (テキスト) / EkrDummyCno (player-like・v1.1) のどちらも同じ配列に入る
    // (v1.1 「dummy_spawn の slot は cno_spawn と共有」)。
    public readonly IEkrSlotCno[] CnoSlots = new IEkrSlotCno[3];
    public readonly float[] LastCnoMoveTime = [-1f, -1f, -1f];

    // v1.2 (marker_save): per-holder 位置メモリ 4 スロット。会議をまたいで保持、
    // ゲーム開始時 (= InitRuntime が state を作り直すタイミング) に自然に全消去される。
    public readonly Vector2?[] Markers = new Vector2?[4];

    // v1.2 (§3 portal_place): CnoSlots (3 枠) とは別の専用 2 枠 (index 0=a, 1=b)。実体は EkrCno 系のみ
    // (IEkrSlotCno は cno_move/despawn 共有インターフェースそのものだが、portal はロジック op から
    // cno_move/despawn の対象にはならない — 直接 EkrManager のポータル専用アクセサ経由でのみ操作する)。
    public readonly IEkrSlotCno[] Portals = new IEkrSlotCno[2];
    public float LastPortalPlaceTime = -1f;

    // v1.2 (§2 on_cno_touch / §5 ポータル warp): 接触ラッチ・デバウンス状態。index は CnoSlots/Portals と対応。
    // 「ラッチ中 (latched.Contains(playerId))」= enter 済み・出るまで再発火しない。
    public readonly HashSet<byte>[] TouchLatched = [[], [], []];
    public readonly Dictionary<byte, float>[] TouchLastFireTime = [new(), new(), new()];
    public readonly HashSet<byte>[] PortalLatched = [[], []];
    // ポータルの CD は「入った側」ではなく player 単位 (spec §3: per-player warp CD 3秒) — 両側で共有する
    // 単一の辞書にする (side ごとに分けると、A→B→(3秒待たず)A→B の高速往復を止められない)。
    public readonly Dictionary<byte, float> PortalLastWarpTime = new();

    // v1.2 (2026-08-10): センサー実体の「非実体→実体」遷移 (初回 spawn / 会議明け復活 /
    // ポータル移設) を検出してラッチ/デバウンスを作り直すための前回ポーリング時の実体化状態。
    // 復活後に旧ラッチが残ると「半径内スポーンで enter が永久不発」「切断者の残留エントリを
    // PlayerId 再利用者が無音継承」の2事故になる。
    public readonly bool[] TouchSensorWasLive = new bool[3];
    public readonly bool[] PortalSensorWasLive = new bool[2];

    public int AbortCount;
    public bool LogicDisabled;
    public bool GameStartFired;

    public float LastSecondFireTime = -1f;

    // Wave 1 (spec §3 notify): 複数対象は「1人ごとに個別課金・超過分は静かにドロップ」なので、
    // バケットは per-(ホルダー, 受け取り手) にする。単数対象 (既定 self) のときは自分1件だけの
    // 辞書になるので、v1.3 までの float 1本と挙動は同じ。
    public readonly Dictionary<byte, float> LastNotifyTime = new();
    // notify が会議中に呼ばれたときだけ使う専用バケット (通常より粗い間隔)。
    // Utils.SendMessage はワールド名札と違い「呼ぶたびにチャット欄へ1行追加」なので、
    // LastNotifyTime (1秒間隔) をそのまま共用すると1回の会議で数十行のスパムになりうる。
    // EkrLogicOpcodes.Notify() 参照。
    public readonly Dictionary<byte, float> LastMeetingNotifyTime = new();

    // ── Wave 1 (remember / passives) ────────────────────────────
    // おぼえた人 2 スロット (byte.MaxValue = 未保存)。死亡・切断は参照時に検証して自動失効させる
    // (ここを能動的に掃除する常駐処理は作らない — 「壊れた参照は静かに no-op」規約 §3 参照整合性3原則)。
    public readonly byte[] Saved = [byte.MaxValue, byte.MaxValue];

    // passives.shield の残数 / passives.doom の残秒。どちらもゲーム開始 (InitRuntime) でリセット。
    public int ShieldRemaining;
    public int DoomRemaining;
    public float LastDoomTickTime = -1f;

    // passives.speedMult の適用状態。opcode 側の速度ブースト (SpeedBoostActive) とは別枠で、
    // こちらは「役職を持っている間ずっと」効く常時倍率。復元は一時速度ブーストの復元レース対策として
    // 「凍結中スキップ + 捕捉フラグ」の2点セット。
    public bool PassiveSpeedApplied;
    public float PassiveSpeedBaseline;

    // 最後に「生きていたときの」座標。passives.corpse=vanish はホルダーをマップ外へ飛ばしてから
    // 死体を作るため、on_death 起点 fiber の self 解決 (身代わりダミー/偽死体) がマップ外になる。
    // 死後の self 解決はこのスナップショットを使う (spec §2 v1.1 の正当ユースを壊さないため)。
    public bool HasLastLivePosition;
    public Vector2 LastLivePosition;
    public float LastKillTime = -1f;
    public float LastCnoSpawnTime = -1f;
    // spec §5 (2026-08-09): cno_show は cno_spawn と共用せず独自の ≤1/3秒/ホルダー バケット
    // (despawn→respawn の fan-out 未課金コストを織り込んで spawn より厳しくする)。
    public float LastCnoShowTime = -1f;
    // v1.1: dummy_spawn ≤1/3秒/ホルダー・corpse_spawn ≤1/2秒/ホルダー (spec §5)。
    public float LastDummySpawnTime = -1f;
    public float LastCorpseSpawnTime = -1f;
    // v1.3: field ≤1/2秒/ホルダー (spec §5 — CNO 生成系防御3点の per-holder レート枠)。
    public float LastFieldPlaceTime = -1f;

    // Wave 5: effect_give ≤1/2秒/ホルダー。
    public float LastEffectGiveTime = -1f;

    // Wave 6: cno_launch ≤1/2秒/ホルダー。
    public float LastCnoLaunchTime = -1f;

    // Wave 6 (契約 §1.1 dir:"move"): ホルダーの「最後の移動方向」を採るための 2 点履歴。Snowdown の
    // 実績形 (Gamemodes/Snowdown.cs:286-299 — 0.01u 超動いたときだけ前へ送る) をそのまま採り、止まって
    // いる間は最後に動いた向きを保持する。更新は TickPassives の生存分岐に相乗り (既に毎 FixedUpdate
    // 走っている唯一の per-holder 経路 — 専用の毎フレーム経路は作らない spec §5)。
    // ⚠️ 契約 §6 のアンカー表は「near/far ポーラーの位置キャッシュに相乗り」と書いているが、
    // そのポーラーは毎回 Pos() を読み直しており位置キャッシュを持たない (2026-08-29 実装時に確認)。
    public bool MoveHistPrimed;
    public Vector2 MoveHistLast;
    public Vector2 MoveHistLastLast;

    public bool SpeedBoostActive;
    public int SpeedGen;
    public float SpeedBaseline;

    public float? KillCooldownOverride;

    // ── Wave 2 ────────────────────────────────────────────────────

    // §2.2 reveal: 恒久に見えるようになった target の playerId 集合。ゲーム開始 (state 作り直し) でリセット。
    public readonly HashSet<byte> Revealed = [];

    // §3.1 vote_weight_set: passives.voteWeight の実行時オーバーライド (per-holder 永続)。null = 未オーバーライド。
    public int? VoteWeightOverride;

    // §2.3 矢印: 同時 ≤4本/ホルダー (両種合算)・seconds 経過で自動 Remove。
    // 人矢印は targetId をキーに (TargetArrow は playerId ペアなので float 等価比較の心配が無い)。
    public readonly Dictionary<byte, float> ArrowTargetExpiry = new();
    // 場所矢印は Add に使った厳密な Vector3 を Remove にもそのまま渡す必要があるため位置ごと保持する。
    public readonly List<(UnityEngine.Vector3 Pos, float ExpireAt)> ArrowMarks = [];
    public float LastArrowTime = -1f;

    // §3.2 vote_block: この会議に限り target の票を無効化した回数 (≤1/会議/ホルダー)。会議境界でリセット。
    public bool VoteBlockUsedThisMeeting;

    // §3.3 vote_swap: この会議で予約を使ったか (≤1/会議/ホルダー)。会議境界でリセット。
    public bool VoteSwapUsedThisMeeting;

    // §1.2 on_meeting_pick: /pick 連打デデュープ (≤1/秒/ホルダー)。
    public float LastMeetingPickTime = -1f;

    // ── Wave 3: じょうたいトリガのエッジ発火エンジン ──────────────

    // この state の持ち主のスロット。フラッシュ点 (PumpMeetingFibers 等) は playerId しか持たないので、
    // 定義を引くために state 側に控える (Runtime の値だけを舐める経路から GetDefinition を引けるように)。
    public CustomRoles Slot;

    // per-(holder, rule) の前回真偽値 = 武装状態。index は def.ParsedLogic.Rules の添字。
    // 「真へ遷移した瞬間だけ発火」なので、真のまま張り付いている間は再発火しない (§1.1)。
    public bool[] EdgeArmed = [];

    // このホルダーの定義に on_alive_count ルールが1つでもあるか (フラッシュ毎の生存数評価の要否)。
    public bool HasAliveCountRule;

    // 直近のフラッシュ以降に書き換えられた変数名。fiber から Pump 直後に回収する (EkrFiber.WrittenVars)。
    // 連鎖起点 (FromVarChain) の fiber による書込みは Chain 側へ分けて積む — そちらは武装遷移だけ
    // 起こして新規発火は生まない (§1.1 深さ1)。
    public readonly HashSet<string> PendingVarWrites = [];
    public readonly HashSet<string> PendingChainVarWrites = [];

    // §3 進捗テキスト: 最後に名札へ載せた置換後の文字列。null = まだ一度も評価していない
    // (最初の評価では送らず種を置くだけ — ゲーム開始時の通常の NotifyRoles で既に出ているため)。
    public string LastProgressSent;

    // §4 ホスト露出を反映した実効値。**InitRuntime で1回だけ焼き込む** — オプションの読みをゲーム開始
    // 時点の1箇所に集約し、ゲーム中のオプション変更は次ゲームからにする (既存役職と同じ規約)。
    public float EffectiveSpeedMult = 1f;
    public int EffectiveVoteWeight = 1;

    // ── Wave 4 ────────────────────────────────────────────────────

    // §3.1 link: つないだ人 (byte.MaxValue = なし — Chainbinder の paired byte フィールド型)。
    // 1ホルダー1本・再実行は張り替え・会議をまたいで保持・ゲーム開始 (state 作り直し) で全消去。
    // 失効は saved1/2 と同じ lazy 方式 (相手の死亡 = FireDeath が on_linked_death 発火後に解消 /
    // 切断・参照時無効 = 無音で解消)。ホルダー自身の死亡・役職剥奪でも解消 (state ごと消える)。
    public byte LinkedId = byte.MaxValue;

    // §1 on_near: per-(rule, 相手) のラッチと発火時刻 (TouchLatched / TouchLastFireTime の rule 軸版 —
    // 複数の on_near rule が別 radius/who を持てるため slot でなく rule index がキーになる)。長さは
    // def.ParsedLogic.Rules と EnsureProximityArrays が突き合わせる (RebuildEdgeArming と同じ長さ
    // 再確認 — 作り直しは「現状真偽で焼く」= 差し替え/会議明け直後の一斉発火を起こさない)。
    public HashSet<byte>[] NearLatched = [];
    public Dictionary<byte, float>[] NearLastFireTime = [];

    // §1.2 who フィルタ付き on_near の監視相手 (byte.MaxValue = 監視なし)。FarWatchedId と同じ差分検出で、
    // 監視相手の張り替え時に「既に radius 内ならラッチ済み扱い (発火しない)」を焼く — §1.1 の
    // 「設置時に半径内へ既にいる者は発火なしでラッチ」(PrimeTouchSensor) を参照確立にも適用する。
    public byte[] NearWatchedId = [];

    // §1.3 on_far: per-rule の「一度 radius 内へ入った」武装と、いま監視している相手 (byte.MaxValue =
    // 監視なし)。監視相手の張り替え (link/remember の再実行) を前回値との差分で検出し、武装を現状真偽
    // から焼き直す — リンク成立時に既に遠くても発火しない (§1.3 初期武装方針)。
    public bool[] FarArmed = [];
    public byte[] FarWatchedId = [];

    // §2 部屋追跡: 前回ポーリングの部屋 (null = 部屋でない [廊下/屋外/ベント/死者])。RoomPrimed=false の
    // 間は「次のポーリングの部屋を武装済み開始として焼く」だけで発火しない (ゲーム開始と会議明けの
    // 再配置で発火させないための番)。死亡でクリア (FireDeath — 死は部屋替えではない)。
    public SystemTypes? PrevRoom;
    public bool RoomPrimed;

    // §5 recruit: per-holder ≤1/10秒 のスタンプ (EKR 全体 ≤1/5秒 は EkrManager._lastGlobalRecruitTime)。
    public float LastRecruitTime = -1f;
}

// EKN 役職メーカー R0 の実行時マネージャ。
// CustomRoles.EkmCustomRole1..10 の10スロットへ
// EkrDefinition (ノーコード役職定義) を束縛する。ロビー内でのみ束縛操作を許可する
// (試合中の束縛変更はゲームエンド判定等の静的 switch と整合しなくなるため禁止)。
public static class EkrManager
{
    public const string CodePrefix = "EKR1.";

    // スロット番号 (1始まり) の並び順。R2 で陣営別スロットを追記した — crew 1〜10 / impostor 11〜13 /
    // neutral 14〜18。⚠️ **並べ替え禁止** (/role set の番号と _bindings.json のキーが乗っている)。
    public static readonly CustomRoles[] Slots =
    [
        CustomRoles.EkmCustomRole1,
        CustomRoles.EkmCustomRole2,
        CustomRoles.EkmCustomRole3,
        CustomRoles.EkmCustomRole4,
        CustomRoles.EkmCustomRole5,
        CustomRoles.EkmCustomRole6,
        CustomRoles.EkmCustomRole7,
        CustomRoles.EkmCustomRole8,
        CustomRoles.EkmCustomRole9,
        CustomRoles.EkmCustomRole10,
        CustomRoles.EkmImpRole1,
        CustomRoles.EkmImpRole2,
        CustomRoles.EkmImpRole3,
        CustomRoles.EkmNeuRole1,
        CustomRoles.EkmNeuRole2,
        CustomRoles.EkmNeuRole3,
        CustomRoles.EkmNeuRole4,
        CustomRoles.EkmNeuRole5
    ];

    // slot -> 束縛中の定義。ロビーでのみ変更する (Bind/Unbind)。試合中の per-round リセット対象外。
    private static readonly Dictionary<CustomRoles, EkrDefinition> Bound = [];

    // slot -> 束縛元のファイル名。ReloadLibrary 時にディスクの最新定義へ追随させるための再解決キー
    // (これが無いと、束縛後に .ekrole.json を手編集しても旧オブジェクト参照が残り続ける)。
    private static readonly Dictionary<CustomRoles, string> BoundFiles = [];

    // enum 範囲比較の O(1) 判定。⚠️ これは「ユーザースロット10個」限定の判定 — 束縛 UI (/role set|unset)
    // と _bindings.json 永続化だけがこれを使う。「EkrDefinition で動く役職か」の判定 (特別扱い arm・
    // Fire ガード等) は IsEkrRole を使うこと (埋込出荷役職も含むため)。
    public static bool IsSlot(CustomRoles role)
    {
        return role is >= CustomRoles.EkmCustomRole1 and <= CustomRoles.EkmCustomRole10
            or >= CustomRoles.EkmImpRole1 and <= CustomRoles.EkmNeuRole5;
    }

    // ── R2: 陣営 ────────────────────────────────────────────────────
    // ⚠️ **陣営はスロット種が静的に決める** — 束縛された定義の team は読まない。理由は EmbeddedRoles と
    // 同じで、GetRoleOptionType (メニュー構築) が束縛/埋込ロードより前に走るため。動的に読むと未束縛の
    // 瞬間に誤分類が確定してしまう。定義側の team は「そのスロットに入れてよいか」の検証にだけ使う。
    // IsCrewmate() 等は毎フレーム級で呼ばれるので、この判定は辞書1回だけ・GetDefinition を挟まない。
    // ⚠️ 遅延生成 (`static readonly ... = Build()` にしない) — EmbeddedRoleTeams はこのファイルの
    // **後ろ**で宣言されており、静的フィールド初期化子は記述順に走るので、即時初期化にすると
    // まだ null の EmbeddedRoleTeams を読んで型初期化例外になる。
    private static Dictionary<CustomRoles, EkrTeam> _slotTeams;

    private static Dictionary<CustomRoles, EkrTeam> SlotTeams => _slotTeams ??= BuildSlotTeams();

    private static Dictionary<CustomRoles, EkrTeam> BuildSlotTeams()
    {
        var map = new Dictionary<CustomRoles, EkrTeam>();

        for (CustomRoles r = CustomRoles.EkmCustomRole1; r <= CustomRoles.EkmCustomRole10; r++) map[r] = EkrTeam.Crewmate;
        for (CustomRoles r = CustomRoles.EkmImpRole1; r <= CustomRoles.EkmImpRole3; r++) map[r] = EkrTeam.Impostor;
        for (CustomRoles r = CustomRoles.EkmNeuRole1; r <= CustomRoles.EkmNeuRole5; r++) map[r] = EkrTeam.Neutral;

        foreach ((CustomRoles role, EkrTeam team) in EmbeddedRoleTeams) map[role] = team;

        return map;
    }

    // EKR 役職の陣営。EKR でない役職を渡した場合は Crewmate を返す (呼び出し側は IsEkrRole で先に絞る)。
    public static EkrTeam GetTeam(CustomRoles role)
    {
        return SlotTeams.TryGetValue(role, out EkrTeam team) ? team : EkrTeam.Crewmate;
    }

    // IsCrewmate() 系の排除法から呼ばれる = 毎フレーム級。辞書1回で答えが出る形にしておく。
    public static bool IsEkrImpostor(CustomRoles role) => SlotTeams.TryGetValue(role, out EkrTeam team) && team == EkrTeam.Impostor;

    public static bool IsEkrNeutral(CustomRoles role) => SlotTeams.TryGetValue(role, out EkrTeam team) && team == EkrTeam.Neutral;

    // neutral のサブカテゴリだけは定義依存 (契約 §1: canKill から導出)。未束縛スロットは canKill=false
    // 扱い = Neutral_Benign に落ちる — 未束縛は GetRoleSpawnMode ガードで出現不能なので実害は無い。
    public static bool IsEkrNeutralKilling(CustomRoles role) => IsEkrNeutral(role) && GetDefinition(role) is { CanKill: true };

    // ── R2: 偽装 ────────────────────────────────────────────────────
    // passives.disguise の陣営。null = 偽装なし (EKR 以外も null)。
    // ⚠️ 効くのは**表示層だけ**で、しかも「本来見えていたものを隠す/差し替える」向きだけ
    // (DoubleAgent と同じ向き)。
    // 見えていない相手に新しく見せる向き — たとえばクルー陣営の EKR が impostor 偽装しても
    // 本物のインポスターの仲間一覧には現れない — はやらない。
    public static EkrTeam? GetDisguiseTeam(CustomRoles role)
    {
        return IsEkrRole(role) ? GetDefinition(role)?.ParsedPassives?.DisguiseTeam : null;
    }

    // 「その陣営として見られているか」。相互認識の抑止判定に使う (偽装先が指定陣営でなければ隠す)。
    public static bool IsDisguisedAwayFrom(CustomRoles role, EkrTeam team)
    {
        EkrTeam? disguise = GetDisguiseTeam(role);
        return disguise.HasValue && disguise.Value != team;
    }

    // ── 埋込出荷役職 (DLL 同梱 Resources/EkRoles/<EnumName>.ekrole.json) ─────────────────
    // 「役職メーカーで開発して、そのまま本体の正式役職として出荷する」レーン。起動時に Bound へ恒久
    // 束縛され、以後はユーザースロットと完全に同じ評価経路に乗る (選出・IsEnable・Fire 系・opcode 予算)。
    // /role set|unset の対象外・_bindings.json にも載らない (BoundFiles に入れない)。
    //
    // ⚠️ メンバーシップは **コンパイル時静的** に保つ (LoadEmbeddedRoles での実行時 Add にしない)。
    // 理由: ①GetRoleOptionType はメニュー構築 (OptionHolder.Load コルーチン) 時 =
    // LoadEmbeddedRoles より前に評価されるため、実行時登録だと Neutral_Benign へ誤分類される
    // ②JSON パース失敗時に IsEkrRole が false になると GetRoleSpawnMode の「未束縛 EKR 役職は出現率0」
    // 安全網の管轄から漏れ、定義なしの役職が湧きうる。静的メンバーシップなら破損時も「未束縛 = 湧かない」
    // へ正しく倒れる。新しい埋込役職の追加 = enum + クラス (EkrDefinedRoles.cs) + json + ここ の4点セット
    // (neutral 陣営で出す場合は CountTypes / CustomWinner への追記も要るので5点セット)。
    //
    // ⚠️ R2: 陣営もここで**コンパイル時に**決める (json の team は束縛時の一致検証にしか使わない)。
    // 実行時に json から読むと、メニュー構築が LoadEmbeddedRoles より前に走る分だけ誤分類が確定する。
    private static readonly Dictionary<CustomRoles, EkrTeam> EmbeddedRoleTeams = new()
    {
        [CustomRoles.EkrShowcase] = EkrTeam.Crewmate
    };

    // 「EkrDefinition で動く役職」の全集合判定 (ユーザースロット + 埋込出荷役職)。
    // SlotTeams は両方を収めているので、辞書1回で済ませる (この判定も毎フレーム級で呼ばれる)。
    public static bool IsEkrRole(CustomRoles role)
    {
        return SlotTeams.ContainsKey(role);
    }

    // Options.Load 完了時 (OptionHolder.PostLoadTasks) に1回呼ぶ。再入しても安全 (Bound 上書き+Set 追加のみ)。
    // 対応するロールクラス (EkmTemplateRole 派生・型名 = enum 名) と enum エントリは通常の役職追加と同じく
    // 手で登録する — ここはリソース名→enum 名の照合と定義の束縛だけを行う。
    public static void LoadEmbeddedRoles()
    {
        const string prefix = "EndKnot.Resources.EkRoles.";
        const string suffix = ".ekrole.json";

        try
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();

            foreach (string resName in assembly.GetManifestResourceNames())
            {
                if (!resName.StartsWith(prefix) || !resName.EndsWith(suffix)) continue;

                string stem = resName[prefix.Length..^suffix.Length];

                // 照合先は静的な EmbeddedRoleTeams のキーのみ — 任意の enum 名を受けると、既存役職名のファイルを
                // 置いただけでその役職を無警告で乗っ取れてしまう (ユーザースロット名のシャドウも同時に防ぐ)。
                if (!Enum.TryParse(stem, out CustomRoles role) || !EmbeddedRoleTeams.ContainsKey(role))
                {
                    Logger.Warn($"[EkrManager] Embedded role file {resName} does not match a registered embedded role (enum + EkrDefinedRoles.cs + EmbeddedRoleTeams map) — skipped", "EkrManager");
                    continue;
                }

                using Stream stream = assembly.GetManifestResourceStream(resName);
                if (stream == null) continue;

                using var reader = new StreamReader(stream, Encoding.UTF8);
                string json = reader.ReadToEnd();

                if (!EkrDefinition.TryParse(json, out EkrDefinition def, out string error))
                {
                    Logger.Error($"[EkrManager] Embedded role {stem} failed to parse: {error}", "EkrManager");
                    continue;
                }

                Bound[role] = def;

                // 表示は定義が正 (役職メーカーが名前と色の出所)。lang の同名キーは未ロード環境向けの保険表示。
                Translator.SetRuntimeOverride(role.ToString(), def.Name);
                ApplyDescriptionOverrides(role, def);
                Main.RoleHtmlColors[role] = def.Color;
                Main.InitRoleColors(); // GetRoleColor が読むのは InitRoleColors 後のテーブル (Bind() と同順序)

                // 出現率は通常役職と同じ扱い (既定 0%・ホストがメニューで上げる)。色だけオプション表示へ反映。
                if (Options.CustomRoleSpawnChances != null && Options.CustomRoleSpawnChances.TryGetValue(role, out var opt))
                    opt.SetColor(Utils.GetRoleColor(role));

                Logger.Info($"[EkrManager] Embedded role loaded: {role} = {def.Name}", "EkrManager");
            }

            // メンバー登録済みなのに定義が束縛できなかった役職 (リソース欠落/パース失敗)。IsEkrRole は静的に
            // true のままなので GetRoleSpawnMode の「未束縛 = 出現率0」安全網に正しく落ちる — 湧きはしないが
            // メニューには出るため、開発者が気付けるようここで明示的に警告する。
            foreach (CustomRoles role in EmbeddedRoleTeams.Keys)
                if (!Bound.ContainsKey(role))
                    Logger.Warn($"[EkrManager] Embedded role {role} has no loadable definition (missing or invalid Resources/EkRoles/{role}.ekrole.json) — it stays unbound and will never spawn", "EkrManager");
        }
        catch (Exception ex)
        {
            Logger.Error($"[EkrManager] LoadEmbeddedRoles failed: {ex}", "EkrManager");
        }
    }

    // slot -> 現在そのロールが割り当てられているプレイヤー。RoleBase.Init/Add/Remove から更新。
    private static readonly Dictionary<CustomRoles, HashSet<byte>> PlayersBySlot = [];

    // ディスク上のライブラリ (import 済みの役職コード一覧)。
    private static readonly List<(string FileName, EkrDefinition Def)> Library = [];

    public static readonly string RolesPath = BuildRolesPath();

    private static string BuildRolesPath()
    {
        try
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(docs)) return null;
            return docs.Replace('\\', '/') + "/EndKnot/EKRoles/";
        }
        catch
        {
            return null;
        }
    }

    public static void EnsureFolder()
    {
        if (string.IsNullOrEmpty(RolesPath)) return;
        try
        {
            if (!Directory.Exists(RolesPath)) Directory.CreateDirectory(RolesPath);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[EkrManager] Could not create EKRoles folder ({RolesPath}): {ex.Message}", "EkrManager");
        }
    }

    // ── ライブラリ (ディスク上の *.ekrole.json) ─────────────────────────────

    public static void ReloadLibrary()
    {
        Library.Clear();
        EnsureFolder();
        if (string.IsNullOrEmpty(RolesPath) || !Directory.Exists(RolesPath)) return;

        foreach (string path in Directory.GetFiles(RolesPath, "*.ekrole.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string json = File.ReadAllText(path);
                if (!EkrDefinition.TryParse(json, out EkrDefinition def, out string error))
                {
                    Logger.Warn($"[EkrManager] Skipping invalid role file {Path.GetFileName(path)}: {error}", "EkrManager");
                    continue;
                }

                Library.Add((Path.GetFileName(path), def));
            }
            catch (Exception ex)
            {
                Logger.Warn($"[EkrManager] Could not read role file {path}: {ex.Message}", "EkrManager");
            }
        }

        // 束縛済みスロットをディスクの最新定義へ追随させる (手編集や再 import の反映)。
        // ファイルが消えた/壊れた場合は旧定義のまま維持する (束縛が無言で外れて湧かなくなる事故を避ける)。
        // ReloadLibrary は /role コマンド (ロビー限定) からしか呼ばれないので、試合中に定義が差し替わることはない。
        // Library はこの関数の呼び出しごとに毎回作り直されるため、参照比較は毎回不一致になり Bind() が
        // 呼ばれ得る — 台帳内容は変わらないので、無駄な再保存を避けるため書き戻し抑制ガードをかける
        // (RestoreBindings の _suppressSave と同じもの)。
        _suppressSave = true;

        try
        {
            foreach ((CustomRoles slot, string fileName) in BoundFiles.ToArray())
            {
                foreach ((string fn, EkrDefinition def) in Library)
                {
                    if (fn != fileName) continue;

                    // R2: 手編集で陣営が変わっていたら追随させない (旧定義のまま維持 = 束縛が黙って
                    // 別陣営に化けるのを防ぐ)。作者が陣営を変えたいときは合うスロットへ入れ直す。
                    if (!TeamMatches(slot, def))
                    {
                        Logger.Warn($"[EkrManager] Slot {slot} keeps its old definition: {fileName} now declares team={def.ParsedTeam} but the slot is {GetTeam(slot)}.", "EkrManager");
                        break;
                    }

                    if (!ReferenceEquals(def, Bound.GetValueOrDefault(slot))) Bind(slot, def, fileName);
                    break;
                }
            }
        }
        finally
        {
            _suppressSave = false;
        }
    }

    public static IReadOnlyList<(string FileName, EkrDefinition Def)> ListLibrary()
    {
        return Library;
    }

    public static bool TryImportCode(string code, out string savedFileName, out string error)
    {
        savedFileName = null;

        if (!EkmCodec.TryDecode(code, out string json, out error, CodePrefix))
            return false;

        if (!EkrDefinition.TryParse(json, out EkrDefinition def, out error))
            return false;

        EnsureFolder();
        if (string.IsNullOrEmpty(RolesPath))
        {
            error = "保存先フォルダ (Documents/EndKnot/EKRoles) を用意できませんでした";
            return false;
        }

        string baseName = SanitizeFileName(def.Name);
        string fileName = $"{baseName}.ekrole.json";
        string fullPath = RolesPath + fileName;

        // 同名は連番で回避 (上書きしない)。
        int n = 2;
        while (File.Exists(fullPath))
        {
            fileName = $"{baseName}_{n}.ekrole.json";
            fullPath = RolesPath + fileName;
            n++;
        }

        try
        {
            File.WriteAllText(fullPath, json, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            error = $"役職コードの保存に失敗しました ({ex.Message})";
            return false;
        }

        savedFileName = fileName;
        ReloadLibrary();
        return true;
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "custom_role";
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (char c in name)
            sb.Append(invalid.Contains(c) || c is ' ' or '.' ? '_' : c);
        string result = sb.ToString().Trim('_');
        return result.Length == 0 ? "custom_role" : result;
    }

    // ── スロット束縛 (ロビーのみ) ────────────────────────────────────────────

    public static bool TryAssign(int libraryIndex1Based, int slotNumber1Based, out string error)
    {
        error = null;

        if (!GameStates.IsLobby)
        {
            error = "役職の割り当てはロビーでのみ変更できます";
            return false;
        }

        if (slotNumber1Based < 1 || slotNumber1Based > Slots.Length)
        {
            error = $"スロット番号は1〜{Slots.Length}で指定してください";
            return false;
        }

        if (libraryIndex1Based < 1 || libraryIndex1Based > Library.Count)
        {
            error = "その番号の役職コードが見つかりません (/role list で確認してください)";
            return false;
        }

        CustomRoles slot = Slots[slotNumber1Based - 1];
        (string fileName, EkrDefinition def) = Library[libraryIndex1Based - 1];

        // R2: スロット種と役職コードの陣営が一致しないと束縛できない (陣営はスロット側が静的に持つため、
        // 不一致のまま入れると「クルーのスロットに入れたインポスター役職」が黙ってクルーとして動く)。
        if (!TeamMatches(slot, def))
        {
            error = $"この役職コードは「{TeamLabel(def.ParsedTeam)}」の役職なので、スロット{slotNumber1Based}({TeamLabel(GetTeam(slot))}用)には入れられません。/role list で陣営に合ったスロット番号を確認してください";
            return false;
        }

        Bind(slot, def, fileName);
        return true;
    }

    private static bool TeamMatches(CustomRoles slot, EkrDefinition def) => def != null && def.ParsedTeam == GetTeam(slot);

    // /role list とエラー文言で使う陣営の日本語表記。
    public static string TeamLabel(EkrTeam team)
    {
        return team switch
        {
            EkrTeam.Impostor => "インポスター",
            EkrTeam.Neutral => "ニュートラル",
            _ => "クルーメイト"
        };
    }

    public static bool TryUnassign(int slotNumber1Based, out string error)
    {
        error = null;

        if (!GameStates.IsLobby)
        {
            error = "役職の割り当てはロビーでのみ変更できます";
            return false;
        }

        if (slotNumber1Based < 1 || slotNumber1Based > Slots.Length)
        {
            error = $"スロット番号は1〜{Slots.Length}で指定してください";
            return false;
        }

        Unbind(Slots[slotNumber1Based - 1]);
        return true;
    }

    // slot 束縛をゲーム再起動をまたいで永続化するファイル。EKRoles フォルダ直下に
    // 置くが、ReloadLibrary の `*.ekrole.json` スキャンには "_bindings.json" は一致しないため拾われない
    // (先頭の `_` はスキャン対象拡張子と衝突しないことの確認用の意図的な命名)。
    private static string BindingsFilePath => string.IsNullOrEmpty(RolesPath) ? null : RolesPath + "_bindings.json";

    // RestoreBindings (と ReloadLibrary の再解決ループ) が内部で Bind() を呼ぶ間は台帳を書き戻さない。
    // これが無いと、復元中にファイルが見つからず skip したスロットの
    // 記録が「見つかったスロットだけの再保存」で消え、ユーザーがファイルを元に戻しても二度と
    // 復活しなくなる (「ファイルを戻せば次回復活する」という設計要件を壊す)。
    private static bool _suppressSave;

    // Bind/Unbind の全ミューテーション経路から呼ぶ。書き込み失敗はログ警告のみ (ゲームを止めない —
    // 束縛自体はメモリ上には反映済みなので、今回のセッションの動作には影響しない)。
    private static void SaveBindings()
    {
        if (_suppressSave) return;

        string path = BindingsFilePath;
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            EnsureFolder();

            var slots = new Dictionary<string, string>();
            foreach ((CustomRoles slot, string fileName) in BoundFiles) slots[slot.ToString()] = fileName;

            // Wave 3 (契約 §4.2): ホスト露出の同定子。これを保存しないと、再起動のたびに復元 Bind が
            // 「初回束縛」と判定してホストが調整した値を既定値で塗り潰す。
            var hostOptionSignatures = new Dictionary<string, string>();
            foreach ((CustomRoles slot, string signature) in HostOptionSignatures) hostOptionSignatures[slot.ToString()] = signature;

            var root = new { ekrBindings = 1, slots, hostOptionSignatures };
            string json = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Logger.Warn($"[EkrManager] Could not save slot bindings: {ex.Message}", "EkrManager");
        }
    }

    // ゲーム再起動後、役職選出より前に必ず1回呼ぶこと (Options.GetRoleSpawnMode の Bound ゲートより前)。
    // Library (ReloadLibrary が既に populate 済みである前提) からファイル名を解決して Bind() する。
    // ファイルが消えている/壊れているスロットはそのスロットだけスキップしログする (出現率オプションの
    // 保存値には触れない — ユーザーがファイルを元に戻せば次回の ReloadLibrary で自然に復活する)。
    public static void RestoreBindings()
    {
        string path = BindingsFilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        try
        {
            string json = File.ReadAllText(path);
            using JsonDocument doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("slots", out JsonElement slotsElem) || slotsElem.ValueKind != JsonValueKind.Object)
                return;

            // Wave 3 (契約 §4.2): 露出の同定子は Bind より**先に**読み込む — 後だと復元 Bind が
            // 「初回」と判定してホストの保存値を既定値で塗り潰す。旧形式のファイル (このキーが無い) は
            // 空のまま = 初回扱いで既定値が入る (Wave 3 より前に束縛したスロットには露出が無いので無害)。
            if (doc.RootElement.TryGetProperty("hostOptionSignatures", out JsonElement sigElem) && sigElem.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in sigElem.EnumerateObject())
                {
                    if (!Enum.TryParse(prop.Name, out CustomRoles sigSlot) || !IsSlot(sigSlot)) continue;

                    string signature = prop.Value.GetString();
                    if (!string.IsNullOrEmpty(signature)) HostOptionSignatures[sigSlot] = signature;
                }
            }

            _suppressSave = true;

            try
            {
                foreach (JsonProperty prop in slotsElem.EnumerateObject())
                {
                    // キーは Bind 直後に enum 名 (slot.ToString()) で書き出したもの限定。数値文字列を
                    // 手編集で入れても Enum.TryParse は数値を許容してしまうが、IsSlot ガードで弾かれる。
                    if (!Enum.TryParse(prop.Name, out CustomRoles slot) || !IsSlot(slot)) continue;

                    string fileName = prop.Value.GetString();
                    if (string.IsNullOrEmpty(fileName)) continue;

                    bool found = false;

                    foreach ((string fn, EkrDefinition def) in Library)
                    {
                        if (fn != fileName) continue;

                        // R2: 束縛後にファイルを別陣営の役職コードへ差し替えられていた場合はここで落とす
                        // (TryAssign と同じ規則。復元経路が唯一の抜け道になるのを塞ぐ)。
                        if (!TeamMatches(slot, def))
                        {
                            Logger.Warn($"[EkrManager] Could not restore slot {slot} <- {fileName}: the role code is team={def.ParsedTeam} but the slot is {GetTeam(slot)}. The slot stays unbound.", "EkrManager");
                            found = true;
                            break;
                        }

                        Bind(slot, def, fileName);
                        found = true;
                        break;
                    }

                    // ここに来るのは「ファイルが消えた」だけでなく「JSON が壊れていて ReloadLibrary が
                    // 既に読み込みをスキップした」場合も含む (詳細な理由はその時点で別途 warn 済み)。
                    if (!found)
                        Logger.Warn($"[EkrManager] Could not restore slot {slot} <- {fileName}: file is missing, or was skipped as invalid (see the warning above). The slot stays unbound; fix or restore the file and it will bind again next launch.", "EkrManager");
                }
            }
            finally
            {
                _suppressSave = false;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[EkrManager] Could not restore slot bindings: {ex.Message}", "EkrManager");
        }
    }

    // ── Wave 3: ホスト露出オプション ────────────────────────────
    //
    // slot -> 前登録済みの 8 枠 (Id+2..Id+9)。各スロットの SetupCustomOption から一度だけ登録される
    // (EkmTemplateRole.SetupHostOptionPool)。Bind 時に「名前・表示・既定値」だけを差し替える。
    private static readonly Dictionary<CustomRoles, OptionItem[]> HostOptionPool = [];

    // slot -> 前回この枠に流し込んだ役職コードの同定子。**同一コードの再束縛ではホストの保存値を
    // 尊重する** (plan §7 Tier 1 #1 の方針に従う) ため、既定値の流し込みは同定子が変わったときだけ。
    // _bindings.json へ一緒に保存する — 保存しないと再起動のたびに復元 Bind が既定値で塗り潰す。
    private static readonly Dictionary<CustomRoles, string> HostOptionSignatures = [];

    internal static void RegisterHostOptionPool(CustomRoles slot, OptionItem[] pool)
    {
        HostOptionPool[slot] = pool;
    }

    // 「同じ役職コードか」の同定子。ファイル名 + 露出宣言の中身 (キー/範囲) — ラベルだけ書き換えた
    // 場合は同じ扱いにしない方が安全側なのでラベルも含める。
    private static string BuildHostOptionSignature(string fileName, EkrDefinition def)
    {
        var sb = new StringBuilder(fileName);

        foreach (EkrHostOption ho in def.ParsedHostOptions)
            sb.Append('|').Append(ho.Key).Append(':').Append(ho.Label).Append(':').Append(ho.Min).Append('-').Append(ho.Max);

        return sb.ToString();
    }

    // 束縛中の役職コードが宣言した露出を 8 枠へ反映する。宣言の並び順 = 枠の割当順。
    // flowDefaults: 既定値を全プリセットへ流し込むか (初回束縛 / 別の役職コードへの差し替え時のみ true)。
    private static void ApplyHostOptions(CustomRoles slot, EkrDefinition def, bool flowDefaults)
    {
        if (!HostOptionPool.TryGetValue(slot, out OptionItem[] pool)) return;

        List<EkrHostOption> declared = def?.ParsedHostOptions ?? [];

        for (var i = 0; i < pool.Length; i++)
        {
            OptionItem opt = pool[i];
            string key = $"{slot}HostOpt{i}";

            // 未使用枠は Clear + hidden (説明文の Set-or-Clear と同じ対称 — 解除しないと別の役職コードへ
            // 差し替えたときに前のラベルが残る)。
            if (i >= declared.Count)
            {
                Translator.ClearRuntimeOverride(key);
                opt.SetHidden(true);
                continue;
            }

            EkrHostOption ho = declared[i];
            Translator.SetRuntimeOverride(key, ho.Label);
            opt.SetHidden(false);

            if (!flowDefaults || opt is not FloatOptionItem floatOpt) continue;

            // 出現率の書き込みと同じ定型: SetAllValues (全プリセット) + SetValue (現在値で締める)。
            // 片方だけだとプリセット切替で無音の不整合になる。
            //
            // ⚠️ index 化の前に**必ず枠の値域へクランプする**。FloatValueRule.RepeatIndex は範囲外の
            // インデックスを 0 側へ丸めず modulo で折り返すので (負→maxIndex / 超過→余り)、作者が
            // 枠の外の初期値を書いていると無関係な値が既定値として焼き付く。
            float rawDefault = ResolveHostOptionDefault(def, ho);
            int index = floatOpt.Rule.GetNearestIndex(Math.Clamp(rawDefault, floatOpt.Rule.MinValue, floatOpt.Rule.MaxValue));
            opt.SetAllValues(Enumerable.Repeat(index, OptionItem.NumPresets).ToArray());
            opt.SetValue(index);
        }
    }

    // 露出キーに対応する「役職コード側が書いている値」= ホストが触る前の既定値。
    private static float ResolveHostOptionDefault(EkrDefinition def, EkrHostOption ho)
    {
        if (ho.IsVar)
        {
            EkrVariable v = def.ParsedLogic?.Variables?.Find(x => x.Name == ho.VarName);
            return v?.Init ?? 0f;
        }

        return ho.Key switch
        {
            "shield.count" => def.ParsedPassives.ShieldCount,
            "doom.seconds" => def.ParsedPassives.DoomSeconds,
            "speedMult" => def.ParsedPassives.SpeedMult,
            "voteWeight" => def.ParsedPassives.VoteWeight,
            "killCooldown" => def.KillCooldown,
            "vision" => def.VisionMultiplier,
            _ => 0f
        };
    }

    // 消費側の唯一の入口。null = そのキーは露出されていない (= 役職コードの値をそのまま使う)。
    // ⚠️ 生インデックスを返す GetValue() ではなく GetFloat() を使うこと (契約 §4.2)。
    private static float? GetHostOptionValue(CustomRoles slot, string key)
    {
        EkrDefinition def = GetDefinition(slot);
        if (def == null || def.ParsedHostOptions.Count == 0) return null;
        if (!HostOptionPool.TryGetValue(slot, out OptionItem[] pool)) return null;

        for (var i = 0; i < def.ParsedHostOptions.Count && i < pool.Length; i++)
        {
            if (def.ParsedHostOptions[i].Key != key) continue;
            return pool[i].GetFloat();
        }

        return null;
    }

    // 「露出されていればホスト値をキーごとの契約範囲へクランプして、されていなければ役職コードの値」。
    // 枠の値域は全キー共通の広め固定なので、キーごとの範囲保証はここが唯一の砦 (ホストが 3.5 を
    // 入れても shield は 3 に丸まる = 範囲逸脱で壊れない側)。
    private static float HostOptionOr(CustomRoles slot, string key, float fallback, float min, float max)
    {
        float? value = GetHostOptionValue(slot, key);
        return value.HasValue ? Math.Clamp(value.Value, min, max) : fallback;
    }

    // EkmTemplateRole から読む2キー。per-holder state があればそれを信じ (InitRuntime で焼き込み済み)、
    // 無い経路 (ApplyGameOptions が Add より先に走る等) では同じ計算を live に行う。
    public static float GetEffectiveKillCooldown(CustomRoles slot, float fallback) => HostOptionOr(slot, "killCooldown", fallback, 1f, 300f);

    public static float GetEffectiveVision(CustomRoles slot, float fallback) => HostOptionOr(slot, "vision", fallback, 0.1f, 3f);

    // 説明文の実行時上書き (plan §7 Tier 1 #2)。Info/InfoLong は ExtendedPlayerControl.GetRoleInfo が読む
    // 2キーで、頂上の役職パネル・イントロ・/h r・オプションメニューのツールチップが全部ここへ集約されている
    // (個別の表示サイトへ書き足さないこと)。空欄はキーごと解除して lang の既定文言へ戻す — 解除しないと
    // 別の役職コードへ差し替えたときに前の説明が残る無音不整合になる。
    private static void ApplyDescriptionOverrides(CustomRoles role, EkrDefinition def)
    {
        SetOrClear($"{role}Info", def.Description);
        // 詳細文は「見出し + 空行 + 本文」へ組み立ててから渡す (組み立て規則と理由は BuildInfoLongOverride)。
        // 詳細文が空なら短文を流用する (片方だけ書いた作者を救う)。両方空なら既定文言へ戻る。
        SetOrClear($"{role}InfoLong", def.BuildInfoLongOverride());
        return;

        static void SetOrClear(string key, string value)
        {
            if (value.Length > 0) Translator.SetRuntimeOverride(key, value);
            else Translator.ClearRuntimeOverride(key);
        }
    }

    private static void Bind(CustomRoles slot, EkrDefinition def, string fileName)
    {
        Bound[slot] = def;
        BoundFiles[slot] = fileName;

        // 表示名の実行時上書き (RoleBase.StartSetup 系・GetRoleName 等が共通で読む翻訳キー = 型名 = enum 名)。
        Translator.SetRuntimeOverride(slot.ToString(), def.Name);
        ApplyDescriptionOverrides(slot, def);

        // Wave 3 (契約 §4.2): ホスト露出の 8 枠を差し替える。既定値の流し込みは「初回束縛」と
        // 「別の役職コードへの差し替え」のときだけ (同一コードの再束縛はホストの保存値を尊重する)。
        string hostOptionSignature = BuildHostOptionSignature(fileName, def);
        bool flowHostOptionDefaults = !HostOptionSignatures.TryGetValue(slot, out string prevSignature) || prevSignature != hostOptionSignature;
        HostOptionSignatures[slot] = hostOptionSignature;
        ApplyHostOptions(slot, def, flowHostOptionDefaults);

        // 色: Main.RoleHtmlColors が正典 (Main.cs RoleHtmlColors 辞書)。
        Main.RoleHtmlColors[slot] = def.Color;
        Main.InitRoleColors();

        // 束縛 = 「次のゲームで使う」宣言なので、出現率オプションを 100% にしてメニューにも出す。
        // 出現率はプリセット別配列 (OptionItem.AllValues) なので全プリセットへ反映する — 現在プリセットだけ
        // 書くと、ホストがプリセットを切り替えた瞬間「束縛表示は残るのに出現率 0 で湧かない」無音不整合になる。
        // 保存されても安全: 未束縛スロットは Options.GetRoleSpawnMode のガードで常に 0 扱いになる。
        if (Options.CustomRoleSpawnChances != null && Options.CustomRoleSpawnChances.TryGetValue(slot, out var opt))
        {
            opt.SetColor(Utils.GetRoleColor(slot));
            opt.SetHidden(false);
            opt.SetAllValues(Enumerable.Repeat(Options.Rates.Length - 1, OptionItem.NumPresets).ToArray());
            opt.SetValue(Options.Rates.Length - 1); // SetAllValues は同期/保存を発火しないため、現在値の SetValue で締める
        }

        SaveBindings();
    }

    private static void Unbind(CustomRoles slot)
    {
        Bound.Remove(slot);
        BoundFiles.Remove(slot);
        Translator.ClearRuntimeOverride(slot.ToString());
        Translator.ClearRuntimeOverride($"{slot}Info");
        Translator.ClearRuntimeOverride($"{slot}InfoLong");

        // Wave 3 (契約 §4.2): 露出枠は全部 Clear + hidden へ戻す (Set-or-Clear 対称)。同定子も捨てる —
        // 次に同じ役職コードを束縛し直したときは「初回」として既定値が入る。
        HostOptionSignatures.Remove(slot);
        ApplyHostOptions(slot, null, flowDefaults: false);

        if (Options.CustomRoleSpawnChances != null && Options.CustomRoleSpawnChances.TryGetValue(slot, out var opt))
        {
            opt.SetAllValues(new int[OptionItem.NumPresets]);
            opt.SetValue(0);
            opt.SetHidden(true);
        }

        SaveBindings();
    }

    // /role set でスロット省略時に使う最初の空きスロット (1..10)。空きが無ければ 0。
    // R2: 陣営を指定すると、その陣営のスロットの中から空きを探す (/role set のスロット省略時に、
    // 役職コードの陣営に合わないスロットを掴んで束縛エラーになるのを防ぐ)。
    public static int FirstFreeSlotNumber(EkrTeam? team = null)
    {
        for (int i = 0; i < Slots.Length; i++)
        {
            if (team.HasValue && GetTeam(Slots[i]) != team.Value) continue;

            if (!Bound.ContainsKey(Slots[i]))
                return i + 1;
        }

        return 0;
    }

    // /role list の陣営別サマリ用。1始まりのスロット番号の範囲を返す。
    public static (int First, int Last) SlotRange(EkrTeam team)
    {
        var first = 0;
        var last = 0;

        for (int i = 0; i < Slots.Length; i++)
        {
            if (GetTeam(Slots[i]) != team) continue;

            if (first == 0) first = i + 1;
            last = i + 1;
        }

        return (first, last);
    }

    public static EkrDefinition GetDefinition(CustomRoles slot)
    {
        return Bound.GetValueOrDefault(slot);
    }

    // 束縛中の役職コードが on_pet ルールを持つか (CustomRolesHelper.PetActivatedAbility 用)。
    // OnPet override は中間基底 EkmTemplateRole 宣言のため、リフレクションの「直接の型が宣言」判定に
    // 乗らない — ペットボタン活性・ペットアニメキャンセルの経路はこちらで判定する。
    public static bool HasOnPetLogic(CustomRoles slot)
    {
        EkrDefinition def = GetDefinition(slot);
        if (def?.ParsedLogic == null) return false;

        foreach (EkrRule rule in def.ParsedLogic.Rules)
            if (rule.When == "on_pet")
                return true;

        return false;
    }

    public static bool IsBound(CustomRoles slot)
    {
        return Bound.ContainsKey(slot);
    }

    // Wave 2: 束縛中の役職コードが on_meeting_vote ルールを持つか。
    // CustomRolesHelper.CancelsVote() の EKR arm が読む。述語は
    // 「cancel_vote の有無」ではなく「on_meeting_vote ルールの有無」— cancel_vote を使わない定義
    // (「投票した人をおぼえる」だけ等) でも OnVote 呼び出し口 (MeetingHudPatch.cs:1610) を
    // 通さないとイベントが永久に発火しない。HasOnPetLogic と同型の静的導出。
    public static bool HasOnMeetingVoteLogic(CustomRoles slot)
    {
        EkrDefinition def = GetDefinition(slot);
        if (def?.ParsedLogic == null) return false;

        foreach (EkrRule rule in def.ParsedLogic.Rules)
            if (rule.When == "on_meeting_vote")
                return true;

        return false;
    }

    // Wave 2: 束縛中の役職コードが on_meeting_pick ルールを持つか (EkrManager.PickMsg / 将来のボタン表示判定用)。
    public static bool HasOnMeetingPickLogic(CustomRoles slot)
    {
        EkrDefinition def = GetDefinition(slot);
        if (def?.ParsedLogic == null) return false;

        foreach (EkrRule rule in def.ParsedLogic.Rules)
            if (rule.When == "on_meeting_pick")
                return true;

        return false;
    }

    // ── per-round プレイヤー追跡 (RoleBase.Init/Add/Remove から呼ばれる) ────────

    public static void ResetSlot(CustomRoles slot)
    {
        // v1.3: crowd-control の帰属判定は set を空にする前に採る (下の _cc クリア条件で使う)。
        // このスロットの保持者のものである場合に加え、どのスロットの保持者でもなくなった孤児も断つ
        // (ラウンド境界で前ラウンドの保持者が既に全 set から消えていると、帰属チェックだけでは
        // 誰もクリアできず新ラウンドへ持ち越される — 元の無条件クリアが守っていたケース)。
        bool ccShouldClear = false;

        if (_cc != null)
        {
            bool ownedBySomeSlot = false;

            foreach (HashSet<byte> owners in PlayersBySlot.Values)
            {
                if (!owners.Contains(_cc.HolderId)) continue;

                ownedBySomeSlot = true;
                break;
            }

            ccShouldClear = !ownedBySomeSlot || (PlayersBySlot.TryGetValue(slot, out HashSet<byte> mine) && mine.Contains(_cc.HolderId));
        }

        // Wave 5 (契約 §1.3 解除タイミング④): このスロットの保持者が掛けた持続効果を解除・復元する。
        // set.Clear() より前に呼ぶ (帰属判定が PlayersBySlot を読むため — ccShouldClear と同じ理由)。
        ClearEffectsForSlot(slot);

        if (PlayersBySlot.TryGetValue(slot, out HashSet<byte> set)) set.Clear();
        else PlayersBySlot[slot] = [];

        // R1: LastMeetingEndNum は slot キー (playerId キーではない) なので、per-player の
        // Add/Remove サイクルでは自然に片付かない。MeetingStates.MeetingNum は新しいラウンドの
        // 開始時に 0 へリセットされる (Patches/OnGameStartedPatch.cs) ため、ここで前ラウンドの
        // 値を残すと「前ラウンドの最終値と偶然一致する会議番号」で on_meeting_end が誤って
        // 重複排除されてしまう (ラウンド境界の取りこぼし)。Init() (=ResetSlot) はラウンド毎に
        // 必ず1回呼ばれるので、ここで確実に破棄する。
        LastMeetingEndNum.Remove(slot);

        // Wave 7 (契約 §2): win_join の便乗ラッチもラウンド境界で自スロット分を破棄 (前の試合のラッチが
        // 次の試合の fold に混入しないように — Wave 6 pitfall #2 と同型のゲームまたぎ持ち越し対策)。
        WinJoinLatchBySlot.Remove(slot);

        // v1.3: crowd-control (drag/field) は EKR 全体の static シングルトン。新ラウンド開始の主経路
        // (OnGameStartedPatch の PlayerStates 差し替え) は Role.Remove() を呼ばないため、前ラウンド稼働中の
        // まま持ち越すと HolderId が新ラウンドの別人として解決されうる (EndAt 経過で
        // 自己回収はするが、ここで確実に断つ)。実体 CNO はゲーム終了時の CNO 一斉破棄で片付いているので
        // Despawn は呼ばず参照だけ捨てる。
        //
        // ⚠ ただし ResetSlot は Init() 経由で「ゲーム中いつでも」呼ばれうる (GameState.SetMainRole の
        // `if (!role.RoleExist(true)) Role.Init();` — 役職変更持ち役職が未使用スロットへ再配役したとき)。
        // 無条件クリアだと無関係スロットの稼働中 field を参照ごと捨てて孤児 CNO 化させ、≤10 上限が
        // 静かに破れる。帰属するときだけ断つ — TeardownRuntime の HolderId/CtxId
        // チェックと同じ非対称の解消。ラウンド境界では前ラウンドの保持者が set に残っているので通る。
        if (ccShouldClear)
        {
            _cc = null;
            _ccPendingDespawn.Clear();
            _lastCcTickTime = -1f;
        }

        // Wave 6 (契約 §2): サボの per-系統デバウンスをラウンド境界でも捨てる。会議境界
        // (FireMeetingStart) だけでクリアしていると、**会議が一度も起きずに終わった試合**の最終サボ成立
        // 時刻が次の試合へ持ち越され、同じ系統のサボが 5 秒以内に起きるとその試合の最初の on_sabotage が
        // 無音でドロップする。ここは _cc のような帰属判定を要しない —
        // 単なるデバウンス辞書なので、早めに捨てても「1 回余分に発火しうる」安全側にしか振れない。
        LastSabotageFireTime.Clear();

        // Wave 6 (契約 §1.1 中断: slot 剥奪): 飛行台帳も同じ非対称で片付ける — このスロットの保持者の
        // ものと、どのスロットの保持者でもなくなった孤児だけを断つ (無関係スロットの飛行中の弾を
        // 巻き添えにしない)。_cc と違い実体の後始末が要る (EndFlight が slot 台帳ごと Despawn する)。
        for (int i = _flights.Count - 1; i >= 0; i--)
        {
            EkrFlightState f = _flights[i];
            var ownedBySomeSlot = false;

            foreach (HashSet<byte> owners in PlayersBySlot.Values)
            {
                if (!owners.Contains(f.HolderId)) continue;

                ownedBySomeSlot = true;
                break;
            }

            if (!ownedBySomeSlot || (set != null && set.Contains(f.HolderId))) EndFlight(f, "slot-unowned");
        }
    }

    public static void AddPlayer(CustomRoles slot, byte playerId)
    {
        if (!PlayersBySlot.TryGetValue(slot, out HashSet<byte> set)) PlayersBySlot[slot] = set = [];
        set.Add(playerId);
        InitRuntime(slot, playerId);
    }

    public static void RemovePlayer(CustomRoles slot, byte playerId)
    {
        if (PlayersBySlot.TryGetValue(slot, out HashSet<byte> set)) set.Remove(playerId);
        TeardownRuntime(playerId);
    }

    public static bool HasPlayers(CustomRoles slot)
    {
        return PlayersBySlot.TryGetValue(slot, out HashSet<byte> set) && set.Count > 0;
    }

    // ── R1: per-holder ロジックランタイム ──────────────

    private static readonly Dictionary<byte, EkrHolderState> Runtime = [];

    // spec §5 (2026-08-09):「全体 ≤10体」は導出型で数える — 手動カウンタ (増減の対称性が崩れると
    // 片方向リークで無音に上限が機能しなくなる構造を持つ) を廃止し、CanOccupyCnoSlot() の呼び出し毎に
    // 全ホルダーの CnoSlots から都度数え直す。実体化前 (pending) の slot も「予約済み」として数える —
    // spawn コルーチンは既に起動済みでいずれ実体化するため、実体化後だけ数えると瞬間的に 10 体超の
    // pending が積み上がりうる (安全側の解釈)。
    //
    // 既知の受容挙動: これと下記の「実体化前は cno_despawn/同一slot再spawn をドロップ」を組み合わせると、
    // 3 slot 全てが実体化待ちの間はそのホルダーの CNO 系操作が (despawn も re-spawn も) 全て no-op になる
    // (spawn コルーチン完了まで自己解決する一時停止 — バグではない)。全ホルダー合算の 10 体上限も同様に
    // 起動直後のバーストで一時的に pending だけで埋まりうる (最大 ~30秒の spawn 遅延ぶん)。
    private const int MaxGlobalCno = 10;

    private static int CountLiveCno()
    {
        int count = 0;

        foreach (EkrHolderState state in Runtime.Values)
        {
            for (int i = 0; i < state.CnoSlots.Length; i++)
                if (state.CnoSlots[i] != null) count++;

            // v1.2: ポータル実体も「EKR 全体 ≤10 体」の導出カウントに含む (spec §5 P6 整合)。
            for (int i = 0; i < state.Portals.Length; i++)
                if (state.Portals[i] != null) count++;
        }

        // v1.3: field の実体も「EKR 全体 ≤10 体」の導出カウントに含む (spec §5 P6 整合)。crowd-control は
        // EKR 全体で同時1本だが、遅延 Despawn 待ちは 1 秒窓の間に複数重なりうる (下記 _ccPendingDespawn の
        // コメント参照)。遅延待ちの実体も数える (過小カウント側に振れると ≤10 上限が実質破れるため)。
        if (_cc?.FieldCno != null) count++;
        count += _ccPendingDespawn.Count;

        return count;
    }

    // Init() (RoleBase.Init、role.RoleExist(true)==false のときに1回) の直後、Add(playerId) から
    // 呼ばれる。def が未束縛/logic 無しでも空の state は持たせておく (SetKillCooldown 等が
    // GetHolderState 経由で参照するため — logic の有無に関わらず一貫して引ける方が呼び出し側が単純になる)。
    private static void InitRuntime(CustomRoles slot, byte playerId)
    {
        var state = new EkrHolderState { Slot = slot };

        EkrDefinition def = GetDefinition(slot);

        if (def?.ParsedLogic != null)
        {
            foreach (EkrVariable v in def.ParsedLogic.Variables)
                state.Variables[v.Name] = v.Init;

            // Wave 3 (契約 §4.1 `var:`): ホストが初期値を変えている変数を上書きする。エッジ発火の初期
            // 武装評価より**前**に置くこと (後だと武装が旧初期値で焼かれる)。
            foreach (EkrHostOption ho in def.ParsedHostOptions)
            {
                if (!ho.IsVar) continue;

                float? hostValue = GetHostOptionValue(slot, ho.Key);
                if (hostValue.HasValue) state.Variables[ho.VarName] = Math.Clamp(hostValue.Value, ho.Min, ho.Max);
            }

            // Wave 3 (契約 §1.1): 初期化時点で条件を評価し、その真偽をそのまま初期武装状態にする。
            // 初期値が既に条件を満たしていても「遷移」ではないので発火しない (武装済み開始)。
            // 「最初から満たしているなら即実行」は作者が on_game_start + if で組める。
            RebuildEdgeArming(state, def.ParsedLogic.Rules);
        }

        // Wave 1 (spec §1.1): shield 残数 / doom 残時間はゲーム開始でリセット。ここ (Add=役職付与) が
        // 唯一の初期化点 — state 自体が作り直されるので「前ラウンドの残数の持ち越し」は構造的に起きない。
        EkrPassives passives = def?.ParsedPassives ?? EkrPassives.Default;

        // Wave 3 (契約 §4.2): 露出されているキーはホストのオプション現在値を、されていなければ役職コード
        // 側の値を使う。読みはこの1点に集約する。⚠️ shield / doom の 0 は「無効」なので下限を 0 にして
        // ホストが切れるようにしてある (契約表の 1..9 / 30..600 は「有効時の値域」の意味)。
        state.ShieldRemaining = (int)Math.Round(HostOptionOr(slot, "shield.count", passives.ShieldCount, 0f, 9f));
        state.DoomRemaining = (int)Math.Round(HostOptionOr(slot, "doom.seconds", passives.DoomSeconds, 0f, 600f));
        state.EffectiveSpeedMult = HostOptionOr(slot, "speedMult", passives.SpeedMult, 0.5f, 3f);
        state.EffectiveVoteWeight = (int)Math.Round(HostOptionOr(slot, "voteWeight", passives.VoteWeight, 0f, 3f));

        Runtime[playerId] = state;
    }

    // Role.Remove(playerId) は役職の入れ替わり (ラウンド境界含む) 全経路で呼ばれる唯一の解体点
    // (SetMainRole が新役職を割り当てる直前に旧役職インスタンスへ必ず投げる)。CNO の後始末もここに集約する。
    //
    // ラウンド境界以外でも起きる (Randomizer/Imitator/Amnesiac 等の役職再割当て) ため、speed ブースト中に
    // ここへ来ると EkrLogicOpcodes.Speed() の遅延復元タスクが GetHolderState(playerId)==null で早期 return し、
    // Main.AllPlayerSpeed が永久にブースト値のまま固定される (一時速度ブーストの復元レースと同型の破棄経路)。
    // teardown 時点で即座に復元することで防ぐ。
    private static void TeardownRuntime(byte playerId)
    {
        // v1.3 (spec §5 crowd-control エンジン): ホルダー/ctx いずれかの死亡・切断・役職剥奪でも即解除。
        if (_cc != null && (_cc.HolderId == playerId || _cc.CtxId == playerId)) StopCrowdControl();

        // Wave 6 (契約 §1.1 中断): 切断・役職剥奪では launch 時の生死に関わらず飛行を断つ
        // (Runtime.Remove の後だと EndFlight が slot 台帳を辿れず実体が孤児化するので、必ず先に呼ぶ)。
        StopFlightsForHolder(playerId);

        if (!Runtime.Remove(playerId, out EkrHolderState state)) return;

        // Wave 2 (spec §2.3): 役職剥奪でも矢印は片付ける (ポータルと同じ「持っている実体は手放す」規約)。
        HideArrows(state, playerId);

        if (state.SpeedBoostActive)
        {
            // 凍結中 (他の役職の SetDark/ノックバック等が MinSpeed を敷いている) は触らない — 復元すると
            // 相手側の凍結を巻き戻してしまう。ここで諦めて放置すると、この state は teardown 済みで
            // 誰も再試行しないまま「相手の凍結解除がブースト値を復元先として控えたまま解除」→ 永久高速固定
            // になる (一時速度ブーストの復元レースと同型)。凍結が抜けるまで再試行する。
            if (Mathf.Approximately(Main.AllPlayerSpeed.GetValueOrDefault(playerId), Main.MinSpeed))
                RetryRestoreSpeed(playerId, state.SpeedBaseline, retriesLeft: 30);
            else
            {
                Main.AllPlayerSpeed[playerId] = state.SpeedBaseline;
                PlayerControl pc = playerId.GetPlayer();
                if (pc) pc.MarkDirtySettings();
            }

            state.SpeedBoostActive = false;
        }

        // Wave 1 (spec §1.1「解除 = 役職剥奪・死亡・ゲーム終了時に必ず復元」)。opcode 側の復元の後に
        // 行う — opcode の baseline は「パッシブ適用後の値」なので、順序が逆だとパッシブ倍率が残る。
        if (state.PassiveSpeedApplied)
        {
            state.PassiveSpeedApplied = false;
            RestorePassiveSpeed(playerId, state.PassiveSpeedBaseline, retriesLeft: 30);
        }

        for (int i = 0; i < state.CnoSlots.Length; i++)
        {
            IEkrSlotCno cno = state.CnoSlots[i];
            if (cno == null) continue;
            state.CnoSlots[i] = null;

            // spec §5 孤児コルーチン防止方針: 実体化前 (playerControl 未生成) は Despawn を呼んでも
            // 基底 spawn コルーチンは止まらず、いずれ勝手に実体化して追跡外のまま居座る。実体化を
            // 待って遅延 Despawn を再試行する。
            if (cno.IsInstantiated) cno.Despawn();
            else RetryDespawnUninstantiated(cno, retriesLeft: 5);
        }

        // v1.2 (spec §3): 役職剥奪 (Teardown) で両側消滅。CnoSlots と同じ孤児コルーチン防止方針に従う。
        for (int i = 0; i < state.Portals.Length; i++)
        {
            IEkrSlotCno portal = state.Portals[i];
            if (portal == null) continue;
            state.Portals[i] = null;

            if (portal.IsInstantiated) portal.Despawn();
            else RetryDespawnUninstantiated(portal, retriesLeft: 5);
        }
    }

    // teardown 時点で凍結中だった speed ブーストの復元を、凍結が解けるまで再試行する。playerId は
    // この呼び出し後に他の役職・別の EKR スロットへ再割当てされうるため、EkrHolderState には依存せず
    // baseline を値渡しで持ち回る。ただし再試行の間に「同じ playerId が新しい EKR speed ブーストを
    // 開始している」ケースがありうる — 復元直前に新しい持ち主がブーストを
    // 管理していないか確認し、していればこの再試行は諦める (新しい持ち主の責務に譲る。でないと
    // 新しいブーストを古い baseline で踏み潰してしまう)。
    private static void RetryRestoreSpeed(byte playerId, float baseline, int retriesLeft)
    {
        if (GameStates.IsEnded) return;
        if (Runtime.TryGetValue(playerId, out EkrHolderState newState) && newState.SpeedBoostActive) return;

        if (Mathf.Approximately(Main.AllPlayerSpeed.GetValueOrDefault(playerId), Main.MinSpeed))
        {
            if (retriesLeft <= 0) return; // 諦める (相手の凍結解除に委ねる)
            LateTask.New(() => RetryRestoreSpeed(playerId, baseline, retriesLeft - 1), 1f, log: false);
            return;
        }

        Main.AllPlayerSpeed[playerId] = baseline;
        PlayerControl pc = playerId.GetPlayer();
        if (pc) pc.MarkDirtySettings();
    }

    // Wave 1: passives.speedMult の復元。RetryRestoreSpeed と同型 (凍結中スキップ + 再試行) だが、
    // 「新しい持ち主が既に速度を管理しているなら譲る」判定にパッシブ側のフラグも含める。
    private static void RestorePassiveSpeed(byte playerId, float baseline, int retriesLeft)
    {
        if (GameStates.IsEnded) return;
        if (Runtime.TryGetValue(playerId, out EkrHolderState newState) && (newState.SpeedBoostActive || newState.PassiveSpeedApplied)) return;

        if (Mathf.Approximately(Main.AllPlayerSpeed.GetValueOrDefault(playerId), Main.MinSpeed))
        {
            if (retriesLeft <= 0) return; // 諦める (相手の凍結解除に委ねる)
            LateTask.New(() => RestorePassiveSpeed(playerId, baseline, retriesLeft - 1), 1f, log: false);
            return;
        }

        Main.AllPlayerSpeed[playerId] = baseline;
        PlayerControl pc = playerId.GetPlayer();
        if (pc) pc.MarkDirtySettings();
    }

    // 実体化前に teardown された CNO を、実体化を待って回収する (spec §5)。cno はどの EkrHolderState にも
    // 属さなくなった後 (teardown 済み) の生存インスタンスをそのまま直接持ち回る — 他の誰にも参照されて
    // いないので新しい持ち主との衝突を考える必要はない (speed のケースと異なる点)。EkrCno/EkrDummyCno の
    // どちらでも呼ばれる (TeardownRuntime のテキスト CNO 回収と DespawnDummySlots のダミー回収が共用)。
    private static void RetryDespawnUninstantiated(IEkrSlotCno cno, int retriesLeft)
    {
        if (GameStates.IsEnded) return;

        if (!cno.IsInstantiated)
        {
            if (retriesLeft <= 0) return; // 通常あり得ない長さの spawn 遅延 (既知の最大30秒待ちより十分な余裕)
            LateTask.New(() => RetryDespawnUninstantiated(cno, retriesLeft - 1), 25f, log: false);
            return;
        }

        cno.Despawn();
    }

    // EkrLogicOpcodes 用の内部アクセサ (レートバケット/CNO スロットの直接読み書き)。未追跡/disable 済みは null。
    internal static EkrHolderState GetHolderState(byte playerId)
    {
        return Runtime.TryGetValue(playerId, out EkrHolderState state) && !state.LogicDisabled ? state : null;
    }

    internal static float? GetKillCooldownOverride(byte playerId)
    {
        return Runtime.TryGetValue(playerId, out EkrHolderState state) ? state.KillCooldownOverride : null;
    }

    internal static bool CanOccupyCnoSlot() => CountLiveCno() < MaxGlobalCno;

    // 上限チェック (CanOccupyCnoSlot) を先に済ませたあとにだけ呼ぶこと。EkrCno/EkrDummyCno どちらも渡せる。
    internal static void OccupyCnoSlot(EkrHolderState state, int slotIndex1Based, IEkrSlotCno cno)
    {
        state.CnoSlots[slotIndex1Based - 1] = cno;
    }

    // cno_despawn opcode から直接呼ばれる他、cno_spawn/dummy_spawn の「同一 slot への再 spawn」でも
    // 「消してから作る」の消す側として使われる (v1.1: dummy_spawn の slot は cno_spawn と共有)。
    // 実体化前 (playerControl 未生成) の CNO は spec §5 の孤児コルーチン防止方針によりドロップ (no-op) する
    // — slot は占有されたまま維持される (「まだ出ていないものは変えられない」)。
    // cno_spawn/dummy_spawn 側は「既存占有者が未実体化なら release を試みる前に spawn ごと諦める」を別途行う
    // (EkrLogicOpcodes.CnoSpawn/DummySpawn 参照 — ここで release が no-op になっただけでは新規 occupy を防げない)。
    internal static void ReleaseCnoSlot(EkrHolderState state, int slotIndex1Based)
    {
        int idx = slotIndex1Based - 1;
        IEkrSlotCno existing = state.CnoSlots[idx];
        if (existing == null) return;
        if (!existing.IsInstantiated) return;

        state.CnoSlots[idx] = null;
        state.TouchLatched[idx].Clear();
        state.TouchLastFireTime[idx].Clear();
        existing.Despawn();
    }

    // ── v1.2: ポータル (portal_place) の専用 2 枠アクセサ (idx: 0=a, 1=b) ─────────────────────
    // CnoSlots と同じ「実体化前は release しない」規約 (spec §5 孤児コルーチン防止方針)。呼び出し元
    // (EkrLogicOpcodes.PortalPlace) が cno_spawn と同じ順序 (existing 未実体化なら諦める→上限チェック→
    // release→occupy) で使う。

    internal static void OccupyPortalSlot(EkrHolderState state, int idx, IEkrSlotCno portal)
    {
        state.Portals[idx] = portal;
    }

    internal static void ReleasePortalSlot(EkrHolderState state, int idx)
    {
        IEkrSlotCno existing = state.Portals[idx];
        if (existing == null) return;
        if (!existing.IsInstantiated) return;

        state.Portals[idx] = null;
        state.PortalLatched[idx].Clear();
        existing.Despawn();
    }

    // EkrDummyCno.OnKilled から呼ばれる (spec §5) — ペット/キルボタン経由の撃破は EkrLogicOpcodes を
    // 経由しないため、Despawn 済み実体を slot 台帳から外す専用の入口が要る。ここで外さないと
    // 全体≤10体の導出カウント (CountLiveCno) が永久に埋まる。テキスト CNO (cno_despawn 等) は呼び出し元
    // (EkrLogicOpcodes) が既に ReleaseCnoSlot/OccupyCnoSlot で台帳を直接操作しているため、この経路が
    // 要るのはロジック外の要因 (ダミー撃破) で CNO が消えるケースだけ。
    internal static void NotifyCnoGone(CustomNetObject cno)
    {
        foreach (EkrHolderState state in Runtime.Values)
        {
            for (int i = 0; i < state.CnoSlots.Length; i++)
            {
                if (!ReferenceEquals(state.CnoSlots[i], cno)) continue;
                state.CnoSlots[i] = null;
                return; // 1つの CNO は同時に1つの slot にしか居ない
            }

            // v1.2: ポータルは撃破不可 (IKillableDummy 非実装) だが、将来の呼び出し元追加に備えて対称に扱う。
            for (int i = 0; i < state.Portals.Length; i++)
            {
                if (!ReferenceEquals(state.Portals[i], cno)) continue;
                state.Portals[i] = null;
                return;
            }
        }
    }

    // ── R1: イベント発火 (RoleBase フック → EkmTemplateRole の薄い呼び出し先) ──────

    // requiredSlot: on_cno_touch (v1.2) 専用のフィルタ (rule.Slot と一致するものだけ発火)。他イベントは null。
    // filter: R2 の on_death cause 用 (rule.Cause が指定されているものは一致したときだけ発火)。
    // Wave 4 では on_linked_death も同じ cause フィルタに乗る (契約 §3.3)。
    // onlyRuleIndex: Wave 4 の近接ポーラー (on_near/on_far) 専用 — 複数 rule が別 radius/who を持ち
    // ラッチが per-rule なので、発火をラッチが立ったその 1 rule にスコープする (FireCnoTouch の
    // requiredSlot と同じ思想の rule 軸版)。他イベントは null (全 rule 走査)。
    // ⚠️ on_attacked の kind は同期プロローグ側 (FireAttackedPrologue) が別途フィルタする。
    private static void FireEvent(CustomRoles slot, byte holderId, string eventName, byte ctxId, int? requiredSlot = null, string filter = null, int? onlyRuleIndex = null)
    {
        if (!Runtime.TryGetValue(holderId, out EkrHolderState state) || state.LogicDisabled) return;

        // spec §2 死亡時の意味論 (2026-08-09): 死後の新規イベントは on_death 以外発火しない
        // (会議系イベントも含む — 死者はもう何も観測しない)。on_death 自体はホルダーが死亡確定した
        // 瞬間に発火するものなのでこのゲートから除外する。
        if (eventName != "on_death")
        {
            PlayerControl holderPc = holderId.GetPlayer();
            if (!holderPc || !holderPc.IsAlive()) return;
        }

        EkrDefinition def = GetDefinition(slot);
        if (def?.ParsedLogic == null) return;

        List<EkrRule> rules = def.ParsedLogic.Rules;

        for (var i = 0; i < rules.Count; i++)
        {
            EkrRule rule = rules[i];
            if (onlyRuleIndex.HasValue && i != onlyRuleIndex.Value) continue; // Wave 4: 近接ポーラーの rule 単位スコープ
            if (rule.When != eventName) continue;
            if (requiredSlot.HasValue && rule.Slot != requiredSlot.Value) continue;
            if (rule.Cause != null && rule.Cause != filter) continue; // R2: on_death の死因フィルタ (未指定 = 全死因)
            if (state.Fibers.Count >= EkmLogicRuntime.MaxFibersPerHolder) continue; // spec §5: 超過は新規発火をドロップ

            var context = new EkrActionContext { HolderId = holderId, CtxId = ctxId, Slot = slot };
            state.Fibers.Add(EkmLogicRuntime.Spawn(rule.Do, state.Variables, context, EkrActionSink.InOpcodeKill));
        }
    }

    public static void FirePet(CustomRoles slot, PlayerControl pc) => FireEvent(slot, pc.PlayerId, "on_pet", byte.MaxValue);

    // v1.2 (spec §2 on_cno_touch): slotNumber1Based は接触した「自分の CNO/ダミー」の slot (1..3)。
    // ctx = 触れた人。呼び出し元は PollCnoTouchIfDue (0.25秒ポーリングエンジン) のみ。
    public static void FireCnoTouch(CustomRoles slot, byte holderId, int slotNumber1Based, byte toucherId) =>
        FireEvent(slot, holderId, "on_cno_touch", toucherId, slotNumber1Based);

    public static void FireKill(CustomRoles slot, PlayerControl killer, PlayerControl victim) =>
        FireEvent(slot, killer.PlayerId, "on_kill", victim ? victim.PlayerId : byte.MaxValue);

    public static void FireTaskComplete(CustomRoles slot, PlayerControl pc) => FireEvent(slot, pc.PlayerId, "on_task_complete", byte.MaxValue);

    public static void FireVentEnter(CustomRoles slot, PlayerControl pc) => FireEvent(slot, pc.PlayerId, "on_vent_enter", byte.MaxValue);

    // Wave 3: ベントから出たとき。FireVentEnter と完全対称。
    // ⚠️ enter とのペアは保証しない — enter は妨害ゲートを通過したときだけ発火する一方、追い出し
    // (RpcBootFromVent) 経由の exit は enter 無しで飛んでくる (作者向け tooltip にも明記済み)。
    public static void FireVentExit(CustomRoles slot, PlayerControl pc) => FireEvent(slot, pc.PlayerId, "on_vent_exit", byte.MaxValue);

    // target の死亡確定時 (spec: 自分が死んだとき・ctx=キルした人 [いれば])。Utils.AfterPlayerDeathTasks から
    // 呼ぶ想定 — target 自身が EKR ホルダーかどうかは呼び出し前提を置かず、ここで判定する。
    // R2: DeathReason (~90種) を 8 バケットへ畳む。
    // 語彙を粗くしているのは、作者が覚えられる粒度に留めるため — 表に無い死因は "other" に落ちる
    // (新しい DeathReason が上流から増えても壊れない側)。⚠️ TS 側 (roledef.ts) と同じ綴り。
    public static string DeathCauseBucket(PlayerState.DeathReason reason)
    {
        return reason switch
        {
            PlayerState.DeathReason.Kill or PlayerState.DeathReason.Bite or PlayerState.DeathReason.Sniped
                or PlayerState.DeathReason.Shot or PlayerState.DeathReason.Mauled or PlayerState.DeathReason.Dismembered
                or PlayerState.DeathReason.LossOfHead or PlayerState.DeathReason.Dragged or PlayerState.DeathReason.Swooped
                or PlayerState.DeathReason.Revenge or PlayerState.DeathReason.Retribution or PlayerState.DeathReason.Execution
                or PlayerState.DeathReason.Destroyed or PlayerState.DeathReason.Demolished or PlayerState.DeathReason.WipedOut
                or PlayerState.DeathReason.Crushed or PlayerState.DeathReason.Erased or PlayerState.DeathReason.Censored
                or PlayerState.DeathReason.Eaten or PlayerState.DeathReason.Consumed or PlayerState.DeathReason.Scavenged => "kill",

            PlayerState.DeathReason.Vote or PlayerState.DeathReason.Trialed or PlayerState.DeathReason.DidntVote
                or PlayerState.DeathReason.SkippedVote => "vote",

            PlayerState.DeathReason.Misfire or PlayerState.DeathReason.Misguess or PlayerState.DeathReason.Assumed
                or PlayerState.DeathReason.Gambled => "guess",

            PlayerState.DeathReason.Bombed or PlayerState.DeathReason.Torched or PlayerState.DeathReason.Kamikazed
                or PlayerState.DeathReason.Quantization => "bomb",

            PlayerState.DeathReason.Poison or PlayerState.DeathReason.Curse or PlayerState.DeathReason.Spell
                or PlayerState.DeathReason.Infected or PlayerState.DeathReason.Diseased or PlayerState.DeathReason.Allergy
                or PlayerState.DeathReason.LossOfBlood or PlayerState.DeathReason.Stung => "poison-curse",

            PlayerState.DeathReason.Meteor or PlayerState.DeathReason.Lava or PlayerState.DeathReason.Tornado
                or PlayerState.DeathReason.Lightning or PlayerState.DeathReason.Drowned or PlayerState.DeathReason.RiptideKilled
                or PlayerState.DeathReason.Sunken or PlayerState.DeathReason.Collapsed or PlayerState.DeathReason.Fall
                or PlayerState.DeathReason.OutOfOxygen or PlayerState.DeathReason.Trapped or PlayerState.DeathReason.Stoned => "environment",

            PlayerState.DeathReason.Suicide or PlayerState.DeathReason.FollowingSuicide or PlayerState.DeathReason.Sacrifice
                or PlayerState.DeathReason.Overtired or PlayerState.DeathReason.Ashamed or PlayerState.DeathReason.PissedOff => "suicide",

            _ => "other"
        };
    }

    public static void FireDeath(PlayerControl target, PlayerControl killer, PlayerState.DeathReason deathReason)
    {
        if (!target) return;

        // v1.3 (spec §5 crowd-control エンジン): ホルダー/ctx いずれかの死亡でも即解除。target が EKR
        // ホルダーかどうかに関わらず判定する (drag の ctx は EKR ホルダーである必要が無いため — 「相手」は
        // 任意の生存プレイヤー)。
        if (_cc != null && (_cc.HolderId == target.PlayerId || _cc.CtxId == target.PlayerId)) StopCrowdControl();

        // Wave 6 (契約 §1.1 中断): ホルダーの死亡で飛行中の弾は消える — ただし**ここでは撃たない**。
        // EndFlight は Despawn() (Object.Destroy/RemoveNetObject) を伴うので、キルパイプラインの同期
        // コールスタックには乗せない (FireMeetingStart と同じ規約)。実際の停止は次の FixedUpdate で
        // PumpFlightsIfDue が同じ述語 (AbortOnHolderDeath && !IsAlive) で撃つ — EKR 役職は
        // NeedsUpdateAfterDeath に載っているので死後も Pump は回り続ける (最大 0.1 秒の遅れ)。
        // 「死に際に弾をはなつ」(on_death 起点 fiber) で撃たれた弾はこの死の後に生まれるので対象外
        // (AbortOnHolderDeath のラッチ — EkrFlightState のコメント参照)。

        string causeBucket = DeathCauseBucket(deathReason);

        // Wave 4 (契約 §3.3): on_linked_death — 死んだのはホルダーではなく「つないだ相手」でも発火する
        // 必要があるため、IsEkrRole(target) の早期 return より**前** (_cc チェックと同じ位置取り)。
        // 発火 → LinkedId 解消の順 (§3.3)。死んだホルダー側は FireEvent の死後ゲート (:on_death 以外は
        // 発火しない) が黙らせる。あわせて死者を参照する近接ラッチ/far 武装をここで掃除する (stale
        // エントリを PlayerId 再利用者が無音継承しない — PortalLastWarpTime の会議境界掃除と同じ発想)。
        // FireEvent は fiber を spawn するだけ (Runtime 辞書は不変) なので直接列挙で安全。
        foreach ((byte holderId, EkrHolderState hs) in Runtime)
        {
            for (var i = 0; i < hs.NearLatched.Length; i++)
            {
                hs.NearLatched[i].Remove(target.PlayerId);
                hs.NearLastFireTime[i].Remove(target.PlayerId);
            }

            for (var i = 0; i < hs.FarWatchedId.Length; i++)
            {
                if (hs.FarWatchedId[i] != target.PlayerId) continue;
                hs.FarArmed[i] = false; // 相手の死亡は武装解除のみ・発火しない (契約 §1.3 — 死は on_linked_death の領分)
                hs.FarWatchedId[i] = byte.MaxValue;
            }

            if (hs.LinkedId != target.PlayerId) continue;

            FireEvent(hs.Slot, holderId, "on_linked_death", target.PlayerId, filter: causeBucket);
            hs.LinkedId = byte.MaxValue;
        }

        CustomRoles slot = target.GetCustomRole();
        if (!IsEkrRole(slot)) return;

        // spec §2 死亡時の意味論 (2026-08-09): 死亡で走行中 fiber を全キャンセル → その後
        // on_death を発火する (この fiber だけは死後も実行可 — 「死んだら爆発」演出のため)。FireEvent
        // 側の「on_death 以外は死後発火しない」ゲートとセットで、以後この保持者は on_death 起点の
        // fiber しか持たなくなる。
        if (Runtime.TryGetValue(target.PlayerId, out EkrHolderState state))
        {
            state.Fibers.Clear();

            // Wave 4 (契約 §3.1/§2): ホルダー自身の死亡でリンク解消 (以後の on_death 起点 fiber の
            // "linked" 参照は no-op — 死後も生き残る参照は saved1/2 だけ)。部屋追跡もクリアする —
            // 死亡による部屋→null で exit を発火させない (§2「死は部屋替えではない」)。蘇生時は
            // RoomPrimed=false が「次のポーリングの部屋を焼くだけ」を保証する。
            state.LinkedId = byte.MaxValue;
            state.PrevRoom = null;
            state.RoomPrimed = false;

            // Wave 2 (spec §2.3): 死亡でも矢印は片付ける (Teardown を経ない死亡経路の唯一の消滅点)。
            HideArrows(state, target.PlayerId);

            // Wave 1 (spec §1.1「解除 = 剥奪・死亡・ゲーム終了で必ず復元」)。役職を保持したまま死亡する
            // 経路 (Teardown を通らない) の復元点。opcode 側ブースト → パッシブの順で戻す。
            if (state.SpeedBoostActive)
            {
                state.SpeedBoostActive = false;
                RetryRestoreSpeed(target.PlayerId, state.SpeedBaseline, retriesLeft: 30);
            }

            if (state.PassiveSpeedApplied)
            {
                state.PassiveSpeedApplied = false;
                RestorePassiveSpeed(target.PlayerId, state.PassiveSpeedBaseline, retriesLeft: 30);
            }

            // v1.2 (spec §3): 「ホルダー死亡/役職剥奪で両側消滅」。役職剥奪側は TeardownRuntime が担当、
            // こちらはホルダーが役職を保持したまま死亡する経路 (Teardown を経ない) の唯一の消滅点。
            // fiber キャンセルとは無関係 (on_death 起点 fiber の実行可否は EkrActionSink 側の判定であり、
            // ポータル消滅とは別規約 — cno_*/dummy_spawn/corpse_spawn は on_death からも実行できるが、
            // ポータルという「既に置かれた実体」は死亡と同時に片付ける)。
            for (int i = 0; i < state.Portals.Length; i++)
            {
                IEkrSlotCno portal = state.Portals[i];
                if (portal == null) continue;
                state.Portals[i] = null;
                state.PortalLatched[i].Clear();

                if (portal.IsInstantiated) portal.Despawn();
                else RetryDespawnUninstantiated(portal, retriesLeft: 5);
            }
        }

        FireEvent(slot, target.PlayerId, "on_death", killer ? killer.PlayerId : byte.MaxValue, filter: causeBucket);
    }

    // ── Wave 1: on_attacked ────────────────────────────────────────
    // PlayerControlPatch.cs の RpcCheckAndMurder 一点関門 (Role.OnCheckMurderAsTarget) から
    // EkmTemplateRole 経由で呼ばれる。戻り値 false = この攻撃は不成立。
    //
    // 順序 (spec §1.1,§2):
    //   ① passives.shield の残数消費判定 (発火より前)
    //   ② on_attacked fiber を同期プロローグで即時実行 (最初の wait か終端まで同期・命令数予算は通常適用)
    //   ③ shield 消費 OR cancel_attack なら不成立
    // 攻撃の成立/不成立に関わらず ② は必ず走る (「防いだ上で通知/反撃」を組めるようにするため)。

    // 同期プロローグ中に kill(target:"self") 等でこの関所へ再入するのを防ぐ (無限再帰ガード)。
    private static readonly HashSet<byte> AttackedInProgress = [];

    // 打診デデュープ (spec §2 on_attacked に明文化済み・2026-08-11)。
    // `RpcCheckAndMurder(target, check: true)` の「当たるか試すだけ」の打診がこの関所を通るが、
    // その打診元には **毎 FixedUpdate 走る周期経路** が実在する (Torpedo のダッシュ命中判定
    // Torpedo.cs:176 / Sharpshooter の構え中 Sharpshooter.cs:127 — どちらも OnFixedUpdate)。
    // 素直に「1打診 = 1発火」にすると、EKR ホルダーが Torpedo の爆風半径に立っているだけで
    // 毎フレーム on_attacked が発火し、まもり9回が 0.2 秒で溶け、fiber 枠 (≤8) を独占して
    // その保持者の他イベント (on_pet/on_second) が全部無音でドロップする。
    // → 同一 (被害者, 攻撃者) の判定を 1 秒キャッシュし、窓の中は「同じ1回の攻撃」として
    //    前回の結論をそのまま返す (打診と実キルで結論が食い違わないよう結果ごと覚える)。
    //
    // R2: キーに Kind を足す。Magnet/Bloodlust の再打診で種別が混線する
    // ため必要だが、**種別ごとに独立枠を持つ = 同一被害者への発火は最悪 4種/秒まで増える**。
    // まもり (shield) は kind:"kill" でしか減らないので数え上げ防御は無傷。fiber 枠 (≤8) の側は
    // 「同じ相手からの別種別の攻撃が1秒に4回来る」状況でしか増えず、実在する周期経路 (Torpedo/
    // Sharpshooter の毎 FixedUpdate 打診) はすべて kill 種別なのでそこは従来どおり 1/秒で止まる。
    private const float AttackedDedupeSeconds = 1f;
    private static readonly Dictionary<(byte Victim, byte Killer, string Kind), (float Time, bool Allow)> RecentAttackDecisions = [];

    public static bool FireAttacked(CustomRoles slot, PlayerControl target, PlayerControl killer, string kind = "kill")
    {
        if (!target || !killer) return true;
        if (!Runtime.TryGetValue(target.PlayerId, out EkrHolderState state)) return true;
        if (!target.IsAlive()) return true; // spec §2: on_attacked は生存中のみ
        if (!AttackedInProgress.Add(target.PlayerId)) return true; // 再入 — 素通し (二重消費させない)

        try
        {
            float nowTs = Time.realtimeSinceStartup;
            var dedupeKey = (target.PlayerId, killer.PlayerId, kind);

            if (RecentAttackDecisions.TryGetValue(dedupeKey, out (float Time, bool Allow) recent) && nowTs - recent.Time < AttackedDedupeSeconds)
                return recent.Allow;

            bool blocked = false;

            // ① まもり (spec §1.1): 消費判定は発火より前。自傷 (kill target:"self" 等) は消費させない
            //    — 既存の数え上げ式まもり役職 (CursedWolf.OnCheckMurderAsTarget) と同じ方針。
            //    R2 (契約 §3b): まもりは **kind:"kill" にだけ**効く (推測や間接死から守るのは、作者が
            //    `on_attacked kind:… → cancel_attack` で組む — エンジンの既定を広げない)。
            if (kind == "kill" && state.ShieldRemaining > 0 && killer.PlayerId != target.PlayerId)
            {
                state.ShieldRemaining--;
                blocked = true;
                Logger.Info($"EKR shield consumed by {target.GetRealName()} ({state.ShieldRemaining} left)", "EkrManager");
            }

            // ② on_attacked (同期プロローグ)
            bool canceled = FireAttackedPrologue(slot, target.PlayerId, killer.PlayerId, state, kind);

            // spec §2: プロローグ実行後にターゲットの生存を再検査する。
            // プロローグ内の副作用 (kill(target:"self") 等) で本人が既に死んでいたら、canceled の
            // 有無に関わらず関所は false — 死亡済みプレイヤーへ killer.Kill が走ると MurderPlayer /
            // FireDeath が二重発火する。
            bool aliveAfterPrologue = target.IsAlive();

            bool allow = !blocked && !canceled && aliveAfterPrologue;
            RecentAttackDecisions[dedupeKey] = (nowTs, allow);

            if (allow) return true;

            // ③ 不成立。関所側 (PlayerControlPatch) が「SomeSortOfProtection」通知を出すので、
            //    ここでは CD の面倒だけ見る (既存の防御系役職と同じ作法)。
            //    R2: キル打診以外 (間接死/強制キル/推測) はキルボタンの CD 経路ではないので触らない。
            if (kind == "kill") killer.SetKillCooldown();
            return false;
        }
        finally
        {
            AttackedInProgress.Remove(target.PlayerId);
        }
    }

    // 同期プロローグ本体。戻り値 = cancel_attack が実行されたか。
    private static bool FireAttackedPrologue(CustomRoles slot, byte holderId, byte killerId, EkrHolderState state, string kind)
    {
        if (state.LogicDisabled) return false;

        EkrDefinition def = GetDefinition(slot);
        if (def?.ParsedLogic == null) return false;

        var canceled = false;

        foreach (EkrRule rule in def.ParsedLogic.Rules)
        {
            if (rule.When != "on_attacked") continue;
            if (rule.Kind != null && rule.Kind != kind) continue; // R2: 攻撃の種別フィルタ (未指定 = 全種)
            if (state.Fibers.Count >= EkmLogicRuntime.MaxFibersPerHolder) continue; // spec §5: 超過はドロップ

            var context = new EkrActionContext
            {
                HolderId = holderId,
                CtxId = killerId,
                Slot = slot,
                AllowCancelAttack = true // 最初の wait までの間だけ有効 (spec §2)
            };

            // spec §5 kill 連鎖ガード (Wave 1 拡張): kill opcode 起因で発火した on_attacked の中では
            // kill は no-op (深さ1)。反射 vs 反射のピンポンはこれで構造的に終端する。
            EkrFiber fiber = EkmLogicRuntime.Spawn(rule.Do, state.Variables, context, EkrActionSink.InOpcodeKill);

            // 「最初の wait に当たるか終端に達するまで同期的に走る」= EkmLogicRuntime.Pump そのもの。
            // per-fiber 500 命令は通常どおり効く。ただし spec §2 により
            // **EKR 全体のフレーム予算 (2000/フレーム) の停止対象外** — 防御は死亡に直結する唯一の
            // イベントで、他ホルダーの on_second 負荷でプロローグが1命令も走れず無音死 + Abort 累積
            // (3回で logic 自動 disable) まで食らうのは「静かにドロップ」の設計意図を超える。
            // 実行した命令はフレーム集計へは通常どおり加算される。頻度は上の打診デデュープが締める。
            bool keep = EkmLogicRuntime.Pump(fiber, EkrActionSink.Instance, ignoreFrameBudget: true);
            DrainFiberWrites(state, fiber);

            context.AllowCancelAttack = false; // 以後 (wait 後の継続) の cancel_attack は no-op
            if (context.CancelAttack) canceled = true;

            if (keep)
            {
                // プロローグ中の kill(self) 等で死亡し fiber が全キャンセルされていたら復活させない。
                PlayerControl holderPc = holderId.GetPlayer();
                if (holderPc && holderPc.IsAlive() && Runtime.TryGetValue(holderId, out EkrHolderState now) && ReferenceEquals(now, state) && !state.LogicDisabled)
                    state.Fibers.Add(fiber);
            }
            else if (fiber.Aborted) NoteFiberAbort(slot, state);
        }

        // Wave 3 (契約 §1.1 評価点③): 同期プロローグ直後の評価。評価自体は同期だが、ここで発火する
        // fiber は通常の非同期 spawn — プロローグ化しない (攻撃解決スタックで走る fiber を増やさない)。
        FlushStateEdges(state, holderId);

        return canceled;
    }

    // 命令数超過による打ち切りの per-holder 計数 (spec §5: 累計3回で当該ホルダーの logic 自動 disable)。
    // Pump() のインライン処理と同じ挙動を、プロローグ経路からも使えるように切り出したもの。
    private static void NoteFiberAbort(CustomRoles slot, EkrHolderState state)
    {
        state.AbortCount++;
        if (state.AbortCount < 3 || state.LogicDisabled) return;

        state.LogicDisabled = true;
        state.Fibers.Clear();
        PlayerControl.LocalPlayer.Notify(string.Format(Translator.GetString("EkrLogicAutoDisabled"), Translator.GetString(slot.ToString())), 10f);
    }

    // ── Wave 2: on_meeting_vote ──────────────────────────────
    // EkmTemplateRole.OnVote (MeetingHudPatch.cs:1610 の CastVote 関門) から呼ばれる。on_attacked と
    // 同じ「同期プロローグ」構造 — fiber は最初の wait まで同期実行され、cancel_vote が有効なのは
    // その間だけ。戻り値 = cancel_vote が実際に実行されたか (呼び出し元が Main.DontCancelVoteList へ
    // 積むかどうかを決める — 「ひと会議に1回だけ有効」はその既存機構に乗る・予算はここでは持たない)。
    public static bool FireMeetingVote(CustomRoles slot, PlayerControl voter, PlayerControl target)
    {
        if (!voter || !target) return false;
        if (!voter.IsAlive()) return false;
        if (!Runtime.TryGetValue(voter.PlayerId, out EkrHolderState state) || state.LogicDisabled) return false;

        EkrDefinition def = GetDefinition(slot);
        if (def?.ParsedLogic == null) return false;

        var canceled = false;

        foreach (EkrRule rule in def.ParsedLogic.Rules)
        {
            if (rule.When != "on_meeting_vote") continue;
            if (state.Fibers.Count >= EkmLogicRuntime.MaxFibersPerHolder) continue; // spec §5: 超過はドロップ

            var context = new EkrActionContext
            {
                HolderId = voter.PlayerId,
                CtxId = target.PlayerId,
                Slot = slot,
                AllowCancelVote = true // 最初の wait までの間だけ有効 (spec §1.1)
            };

            EkrFiber fiber = EkmLogicRuntime.Spawn(rule.Do, state.Variables, context, EkrActionSink.InOpcodeKill);
            bool keep = EkmLogicRuntime.Pump(fiber, EkrActionSink.Instance, ignoreFrameBudget: true);
            DrainFiberWrites(state, fiber);

            context.AllowCancelVote = false; // 以後 (wait 後の継続) の cancel_vote は no-op
            if (context.CancelVote) canceled = true;

            if (keep) state.Fibers.Add(fiber);
            else if (fiber.Aborted) NoteFiberAbort(slot, state);
        }

        // Wave 3 (契約 §1.1 評価点④): 投票プロローグ直後の評価 (攻撃プロローグと同型)。
        FlushStateEdges(state, voter.PlayerId);

        return canceled;
    }

    // ── Wave 2: on_meeting_pick ──────────────────────────────
    // 入力2系統 (会議ボタン [Modules/Ekm/EkrMeetingButton.cs・CustomRPC.EkrMeetingPick] /
    // /pick チャットコマンド) を1イベントに統合。発火デデュープ ≤1/秒/ホルダー (チャット連打/連打対策・
    // TryGateMeetingPick が両入力の共通関所)。

    private static void FireMeetingPick(CustomRoles slot, byte holderId, byte pickedId) =>
        FireEvent(slot, holderId, "on_meeting_pick", pickedId);

    // 会議ボタン (Judge.cs:240-281 と同型) と /pick チャットコマンドの共通関所。両方とも「対象生存・
    // GameStates.IsVoting 相当・ホルダー生存・on_meeting_pick ルール保持・≤1/秒/ホルダー」を通過してから
    // FireMeetingPick へ合流する (契約の「チャットコマンドの糖衣」規約)。呼び出し元ごとに要る前段の
    // ゲート (メッセージの prefix 判定・ボタンの表示条件) だけを外側で行う。
    // 戻り値: false = ゲートで弾かれた (呼び出し元がエラーメッセージ等を出す余地がある)。
    private static bool TryGateMeetingPick(PlayerControl pc, out CustomRoles slot, out EkrHolderState state)
    {
        slot = default;
        state = null;

        if (!AmongUsClient.Instance.AmHost || !pc) return false;
        if (!GameStates.IsMeeting || !MeetingHud.Instance || MeetingHud.Instance.state is MeetingHud.MeetingStates.Results or MeetingHud.MeetingStates.Proceeding) return false;
        if (!pc.IsAlive()) return false;

        slot = pc.GetCustomRole();
        if (!IsEkrRole(slot) || !HasOnMeetingPickLogic(slot)) return false;

        if (!Runtime.TryGetValue(pc.PlayerId, out state) || state.LogicDisabled) return false;

        float now = Time.realtimeSinceStartup;
        if (state.LastMeetingPickTime >= 0f && now - state.LastMeetingPickTime < 1f) return false; // spec: ≤1/秒/ホルダー

        return true;
    }

    // 別名の正典は lang の CommandForms.Pick (ja では "pick,えらぶ")。リテラル "/pick" 決め打ちにすると
    // 翻訳された別名が丸ごと無音死する。Command.IsThisCommand と同じ「先頭トークンの
    // 完全一致」で照合する — GuessManager.CheckCommand の StartsWith+Replace 方式は前方一致の自己衝突を
    // 起こす既知の欠陥型なので踏襲しない。
    private static string[] PickCommandForms => EndKnot.Command.AllCommands.Find(x => x.Key == "Pick")?.CommandForms ?? ["pick"];

    // /pick <番号> — GuessManager.CheckCommand と同じ「消費したら true」規約。
    public static bool PickMsg(PlayerControl pc, string msg)
    {
        if (!AmongUsClient.Instance.AmHost || !pc) return false;

        string m = (msg ?? "").Trim();
        if (!m.StartsWith('/')) return false;

        // 日本語 IME での全角スペース区切り ("/えらぶ　1") も区切りとして受ける — 半角のみで切ると別名一致に
        // 失敗して通常 dispatch の空アクションへ落ち、リテラル照合時代と同じ無音死が別入力で残る。
        string head = m.ToLower().TrimStart('/').Split(' ', '　')[0];
        if (!PickCommandForms.Any(head.Equals)) return false;

        // 別名 "pick" は Choose (Changeling / Pawn の役職選択) とも重複している。EKR 役職を持たない
        // 人の入力まで消費すると、早期チェインは通常 dispatch より前に走るので相手のコマンドが無音死する。
        // ゲート落ちで消費するのは「EKR ホルダー本人の入力」だけに限る (誤爆を通常チャットへ漏らさない設計意図)。
        if (!IsEkrRole(pc.GetCustomRole())) return false;

        if (!TryGateMeetingPick(pc, out CustomRoles slot, out EkrHolderState state)) return true; // 消費はする (無言で通常チャットへ流さない)

        string rest = m[(m.Split(' ', '　')[0].Length)..].Trim();

        if (!byte.TryParse(rest, out byte targetId))
        {
            Utils.SendMessage(Translator.GetString("EkrPickUsage"), pc.PlayerId);
            return true;
        }

        PlayerControl target = targetId.GetPlayer();

        if (!target)
        {
            Utils.SendMessage(Translator.GetString("EkrPickPlayerNotFound"), pc.PlayerId);
            return true;
        }

        state.LastMeetingPickTime = Time.realtimeSinceStartup;
        FireMeetingPick(slot, pc.PlayerId, target.PlayerId);
        return true;
    }

    // 会議ボタン (EkrMeetingButton.OnClick のホストローカル分岐 / EkrMeetingButton.ReceiveRPC の両方から
    // 呼ばれる)。§6 方針: RPC 受信側はホストのみが処理し、送信者が実際に on_meeting_pick を持つ生存
    // ホルダーであることをここで再検証する (クライアント申告を信用しない) — TryGateMeetingPick が
    // それを担う。ボタンはクリック演出のみなので、対象不在時もチャットへエラーは出さない (無音 no-op)。
    internal static void HandleMeetingPickButton(PlayerControl pc, byte targetId)
    {
        if (!TryGateMeetingPick(pc, out CustomRoles slot, out EkrHolderState state)) return;

        PlayerControl target = targetId.GetPlayer();
        if (!target) return;

        state.LastMeetingPickTime = Time.realtimeSinceStartup;
        FireMeetingPick(slot, pc.PlayerId, target.PlayerId);
    }

    // reporter が EKR ホルダーのときだけ発火 (spec: 自分が通報者になったとき・ctx=死体の主)。
    public static void FireReport(PlayerControl reporter, PlayerControl bodyOwner)
    {
        if (!reporter) return;

        CustomRoles slot = reporter.GetCustomRole();
        if (!IsEkrRole(slot)) return;

        FireEvent(slot, reporter.PlayerId, "on_report", bodyOwner ? bodyOwner.PlayerId : byte.MaxValue);
    }

    // ── Wave 7: 「いっしょにかたせる」(win_join) の便乗ラッチ ─────

    // per-slot キー (ResetSlot がラウンド境界で自スロット分だけ捨てる — LastMeetingEndNum と同じ作法。
    // LastSabotageFireTime 型の全体 Clear にしないのは、ラッチが「1 回余分に発火しても安全」なデバウンス
    // ではなく巻き添え消去がそのまま機能欠落になるため)。会議境界では捨てない — ゲーム終了まで持ち越す
    // のが本義 (契約 §2)。終了時の合流 (WinnerIds への追加) は CheckGameEndPatch の per-player fold。
    private static readonly Dictionary<CustomRoles, HashSet<byte>> WinJoinLatchBySlot = new();

    public static void LatchWinJoin(CustomRoles slot, byte playerId)
    {
        if (!WinJoinLatchBySlot.TryGetValue(slot, out HashSet<byte> set))
            WinJoinLatchBySlot[slot] = set = [];

        set.Add(playerId);
    }

    // CheckGameEndPatch の fold 用 — 全スロット横断で「この人はラッチ済みか」。slot は表示帰属用
    // (AdditionalWinners = CustomRoles キャストで「かたせた側の EKR 役職名」を勝敗画面に出す)。
    // 複数スロットからラッチされていたら最初に見つかった 1 つでよい (勝者追加は HashSet で冪等)。
    public static bool TryGetWinJoinSlot(byte playerId, out CustomRoles slot)
    {
        foreach ((CustomRoles s, HashSet<byte> set) in WinJoinLatchBySlot)
        {
            if (!set.Contains(playerId)) continue;

            slot = s;
            return true;
        }

        slot = default;
        return false;
    }

    // ── Wave 6: サボタージュ成立と蘇生 ─────────────────────

    // §2: 同種サボの連打 (リアクター連続押し等) で fiber 起票が暴れないための per-系統デバウンス。
    // fiber を起票する前 (エンジン側) で落とす。
    private const float SabotageDebounceSeconds = 5f;
    private static readonly Dictionary<int, float> LastSabotageFireTime = new();

    // §2: グローバル型 — 起こした人が EKR ホルダーかどうかに関わらず、全ホルダーへ ctx=起こした人で配る
    // (FireDeath/FireReport の fan-out と同型)。呼び出し口はサボ成立の一点関門
    // (Patches/SabotageSystemPatch.cs の `if (allow)` 直下) — 却下されたサボ打診では発火しない。
    // ⚠️ 既知のカバレッジ穴 (契約 §2 で受容): カスタムサボ (GrabOxygenMask の個別 Deteriorate) だけは
    // CheckSabotage の成立分岐を通らないため発火しない。**Submerged は穴ではない** — 契約 §2 は
    // 「Submerged 経路も発火しない」と書いているが、SabotageSystemPatch.cs:447 は
    // `return CheckSabotage(...)` でこの関門をきちんと通る (2026-08-29 実コード確認・
    // 契約側の記述誤り)。
    public static void FireSabotage(PlayerControl player, SystemTypes systemTypes)
    {
        if (!player) return;

        float now = Time.realtimeSinceStartup;
        var key = (int)systemTypes;

        if (LastSabotageFireTime.TryGetValue(key, out float last) && now - last < SabotageDebounceSeconds) return;
        LastSabotageFireTime[key] = now;

        foreach ((CustomRoles slot, HashSet<byte> holders) in PlayersBySlot)
        {
            if (holders.Count == 0) continue;

            EkrDefinition def = GetDefinition(slot);
            if (def?.ParsedLogic == null) continue;

            // FireEvent は fiber を spawn するだけ (Runtime 辞書は不変) なので直接列挙で安全。
            foreach (byte holderId in holders) FireEvent(slot, holderId, "on_sabotage", player.PlayerId);
        }
    }

    // §3: ホルダー限定・ctx 無し。蘇生させた人は RpcRevive のシグネチャに存在しないため渡せない
    // (ExtendedPlayerControl.cs の一点関門 → RoleBase.OnRevived → EkmTemplateRole の override)。
    // 変数・progress・passives は蘇生で初期化しない (蘇生は役職の Init ではない — 契約 §3)。
    // ⚠️ 既知の取りこぼし (契約 §3 で受容): RpcRevive を通さない手書き蘇生からは発火しない。
    public static void FireRevive(PlayerControl pc)
    {
        if (!pc) return;

        CustomRoles slot = pc.GetCustomRole();
        if (!IsEkrRole(slot)) return;

        // Wave 6 (契約 §1.1 dir:"move"): 移動履歴を蘇生でプライムし直す。死亡中はサンプラが止まるので
        // 履歴は「死ぬ直前の移動方向」のまま残り (これは on_death 起点の cno_launch にとって正しい値)、
        // 蘇生でプレイヤーは別の場所へ再配置される。プライムを畳まないと、蘇生後の最初のサンプルが
        // 「死んだ場所 → 生き返った場所」という無関係なベクトルを移動方向として焼いてしまう。
        // false にしておくと次の生存 tick が現在地で 2 点とも
        // 引き直し、実際に歩くまでは方向が定まらない = no-op になる (契約どおりの安全側)。
        if (Runtime.TryGetValue(pc.PlayerId, out EkrHolderState reviveState)) reviveState.MoveHistPrimed = false;

        FireEvent(slot, pc.PlayerId, "on_revive", byte.MaxValue);
    }

    // 会議開始 (ボタン/通報どちらでも1回・spec §2)。全 EKR ホルダー共通の「走行中 fiber は全キャンセル」
    // を先に行ってから on_meeting_start を発火する (キャンセル後に発火 — 新しく生える fiber は対象外)。
    // fiber キャンセルは純管理メモリ操作 (Il2Cpp 側へは触らない) なのでここでインラインに行うが、
    // v1.1 のダミー slot 一括掃除 (DespawnDummySlots) は Despawn() が Object.Destroy/RemoveNetObject を
    // 呼ぶため、この関数の呼び出し元 (PlayerControlPatch.AfterReportTasks) が抱える他の PlayerControl
    // 走査と同じ synchronous コールスタックに乗せない — 基底 CNO の OnMeeting() 自体も同じ理由で
    // LateTask 5f 遅延になっている (PlayerControlPatch.cs:1501)。それに倣い 1 秒遅延で呼ぶ
    // (dummy_spawn は会議中 Execute() の IsMeeting ゲートで no-op なので、
    // この 1 秒の間に slot を奪われる心配は無い — 台帳が 1 秒長く「占有中」と数えるだけで ≤10 上限は
    // 緩まない方向にしか振れない)。
    public static void FireMeetingStart()
    {
        // v1.1 (2026-08-09): 会議開始時点でも dummy_spawn の10秒ゲート起点を前進させる —
        // 「会議開始→追放演出→会議明けスイープ」の全 span を単一の危険窓としてカバーする
        // (EkrActionSink.Execute の ExileController ゲートとの二重防御)。会議明けには
        // FireMeetingEndForSlot が起点を改めて再セットする。
        LastMeetingEndTime = Time.realtimeSinceStartup;

        // v1.3 (spec §3,§5): 会議開始 (追放演出突入含む) で drag/field は即停止・解除 (持ち越しはしない)。
        StopCrowdControl();

        // Wave 6 (中断): 飛行中の弾は会議開始で消す。
        // ⚠️ ここで同期に停止しない — EndFlight は ReleaseCnoSlot 経由で Despawn()
        // (Object.Destroy/RemoveNetObject) を呼ぶので、この関数の呼び出し元 (AfterReportTasks) の
        // 同期コールスタックには乗せられない (DespawnDummySlots が 1 秒遅延になっているのと同じ規約)。
        // 実際の停止は次の FixedUpdate で PumpFlightsIfDue の会議ガードが撃つ (そちらは安全な文脈)。
        // 下の LateTask はホルダー不在等で Pump が回らないときの取りこぼし止め。
        // 会議開始 +5 秒の全 CNO 一斉 OnMeeting より先にどちらかが必ず走るが、仮に間に合わなくても
        // EkrCno.OnMeeting の Launched 分岐が復活を止める (三重防御)。

        // Wave 5 (契約 §1.3 解除タイミング③): 会議開始で持続効果は全解除・会議明けへ持ち越さない
        // (Grenadier の ReportDeadBody クリアと同じ慣例)。
        ClearAllEffects();

        // Wave 1: on_attacked の打診デデュープ窓は会議境界を跨いで持ち越す意味が無い (切断者の
        // PlayerId 再利用で他人の結論を継承しないよう、PortalLastWarpTime と同じ作法で捨てる)。
        RecentAttackDecisions.Clear();

        // Wave 6 (契約 §2): サボの per-系統デバウンスは会議境界で捨てる (会議明けは新しいタスクフェーズ —
        // 前フェーズの残り時間で最初の1回が無音死しないように)。
        LastSabotageFireTime.Clear();

        // Wave 2 (spec §3): vote_block/vote_swap/exile は会議スコープの状態 (trap 10 — Init() 経由の
        // ラウンド境界リセットではなく、実際の会議境界であるここで捨てる)。
        VoteBlockedThisMeeting.Clear();
        _voteSwapReservation = null;
        _exileUsedThisMeeting = false;

        foreach (EkrHolderState state in Runtime.Values)
        {
            state.Fibers.Clear();
            // ポータル warp CD の残留エントリ掃除 (切断者の 3 秒 CD を PlayerId 再利用者が継承しない
            // ように会議境界で毎回捨てる — センサー実体はどのみち会議で消えるので CD 継続の意味がない)。
            state.PortalLastWarpTime.Clear();
            state.VoteBlockUsedThisMeeting = false;
            state.VoteSwapUsedThisMeeting = false;

            // Wave 4 (契約 §1.1/§2): 近接ラッチ/far 武装/部屋追跡は会議境界で捨て、会議明け最初の
            // ポーリングで「現状真偽」から作り直す (respawn の再配置を歩行と誤認して一斉発火しない —
            // 空配列は EnsureProximityArrays の長さ再確認が拾って再プライムする)。リンク (LinkedId) は
            // 会議をまたいで保持 (§3.1 — marker/remember と同じ)。
            state.NearLatched = [];
            state.NearLastFireTime = [];
            state.NearWatchedId = [];
            state.FarArmed = [];
            state.FarWatchedId = [];
            state.PrevRoom = null;
            state.RoomPrimed = false;
        }

        LateTask.New(() =>
        {
            foreach (EkrHolderState state in Runtime.Values) DespawnDummySlots(state);

            // Wave 6: 飛行中の弾の後始末 (上のブロックコメント参照 — 同期コールスタックに Despawn を
            // 乗せない規約でここへ回している)。
            StopAllFlights();
        }, 1f, "EkrManager.DespawnDummies", log: false);

        // PlayersBySlot はユーザースロット + 埋込出荷役職の両方をキーに持つ (AddPlayer 経由でしか増えない)
        // ので、Slots 配列でなくこちらを走査する — 埋込役職を反復から漏らすと会議イベントが無音死する。
        foreach ((CustomRoles slot, HashSet<byte> holders) in PlayersBySlot)
        {
            if (holders.Count == 0) continue;

            EkrDefinition def = GetDefinition(slot);
            if (def?.ParsedLogic == null) continue;

            foreach (byte holderId in holders) FireEvent(slot, holderId, "on_meeting_start", byte.MaxValue);
        }
    }

    // v1.1 spec §3「ダミーは会議で消える」の唯一の消滅経路。EkrDummyCno.OnMeeting() は意図的に空 override
    // なので (二重管理防止・EkrDummyCno.cs 参照)、ここが実際に片付ける唯一の場所になる。テキスト CNO
    // (EkrCno) は対象外 — 従来どおり基底 OnMeeting() の会議明け自動復活エンジンに任せる。
    private static void DespawnDummySlots(EkrHolderState state)
    {
        for (int i = 0; i < state.CnoSlots.Length; i++)
        {
            if (state.CnoSlots[i] is not EkrDummyCno dummy) continue;

            if (dummy.IsInstantiated)
            {
                state.CnoSlots[i] = null;
                dummy.Despawn();
            }
            else
            {
                // 実体化前は slot を保持したまま短間隔 (1秒) で回収を再試行する。
                // TeardownRuntime の RetryDespawnUninstantiated (25秒間隔) と違い、この
                // state は会議中も Runtime に生き続けるため、slot を先に null にすると CountLiveCno()
                // が下振れして ≤10 上限が過収容を許し、その CNO は誰にも追跡されないまま会議明けに
                // 出現してしまう (「会議で消える」約束も破れる)。slot を握ったまま数え続ければ上限は
                // 安全側にしか振れない。会議中はプレイヤーには MeetingHud しか見えないため、実体化→
                // 次リトライまでの最大1秒間ワールドに存在しても見た目の約束は破れない。
                RetryDespawnDummySlot(state, i, dummy, retriesLeft: 30);
            }
        }
    }

    // DespawnDummySlots 専用の実体化待ち回収。slot の解放は Despawn が実際に成功した時点で行う。
    // retriesLeft 30 × 1秒 = 既知の最大 spawn 遅延 (~30秒) をカバー。
    private static void RetryDespawnDummySlot(EkrHolderState state, int index, EkrDummyCno dummy, int retriesLeft)
    {
        if (GameStates.IsEnded) return;

        // Teardown (slot を null 化して RetryDespawnUninstantiated へ回す)・撃破 (NotifyCnoGone)・
        // 別の何かが slot を差し替えた場合はそちらの回収に任せて手を引く (二重管理防止)。
        if (state.CnoSlots[index] != dummy) return;

        if (!dummy.IsInstantiated)
        {
            if (retriesLeft <= 0) return; // 通常あり得ない長さの spawn 遅延 — Teardown/Init の全体掃除に任せる
            LateTask.New(() => RetryDespawnDummySlot(state, index, dummy, retriesLeft - 1), 1f, log: false);
            return;
        }

        state.CnoSlots[index] = null;
        dummy.Despawn();
    }

    // 会議中は RoleBase.OnFixedUpdate (→Pump) が呼ばれないため、MeetingHud.Update 側から毎フレーム
    // 呼んで fiber を進める (spec §3 は会議中も notify [チャット私信] を有効と規定 — Execute 側の
    // IsMeeting ガードがアクション no-op を保証するので安全。wait 中の fiber は WakeAt 経過後に
    // ここで再開し、会議明け後は通常の Pump が引き継ぐ)。命令数の Abort 計数は通常 Pump と違い
    // 省略する — do ≤64 制約下の会議中実行で 500 命令/fiber には構造的に届かない。
    public static void PumpMeetingFibers()
    {
        foreach ((byte holderId, EkrHolderState state) in Runtime)
        {
            if (state.LogicDisabled) continue;

            EkrFiber[] snapshot = state.Fibers.ToArray();

            for (var i = 0; i < snapshot.Length; i++) // 前方 = 生えた順 (通常 Pump と同じ FIFO 規約)
            {
                EkrFiber fiber = snapshot[i];
                if (!state.Fibers.Contains(fiber)) continue;

                bool keep = EkmLogicRuntime.Pump(fiber, EkrActionSink.Instance);
                DrainFiberWrites(state, fiber); // 捨てる判定より前に回収する (Pump 側と同じ理由)
                if (!keep) state.Fibers.Remove(fiber);
            }

            // Wave 3 (契約 §1.1 評価点②): 会議中も変数は動くのでエッジ判定も回す。
            // アクション op 側は既存の会議中白名単 (spec §3) が守る。
            FlushStateEdges(state, holderId);
        }
    }

    // 会議明け・タスク再開時 (spec: on_meeting_end)。RoleBase.AfterMeetingTasks() はシグネチャに
    // プレイヤーを持たず、10スロット共有シングルトンの1インスタンスが「保持者の人数ぶん」呼ばれる
    // (Utils.AfterMeetingTasks が全プレイヤーをループし、各プレイヤーの Role.AfterMeetingTasks() を呼ぶため)。
    // ここで会議番号ベースの重複排除をしないと、同じ会議明けで保持者数² 回 fiber が湧く。
    private static readonly Dictionary<CustomRoles, int> LastMeetingEndNum = [];

    // v1.1: dummy_spawn の「会議明けから10秒間はドロップ」ゲート (spec §5) が読む EKR 全体共通の時刻。
    // 会議開始 (FireMeetingStart) と会議明け (FireMeetingEndForSlot) の両方で前進する。
    // Time.realtimeSinceStartup は起動からの単調増加値。ゲーム境界で reset しない設計 — 理論上の失敗
    // 方向は「前ゲームの最終会議終了から10秒以内に次ゲームの intro が明ける」ときの誤ドロップ (許可漏れ)
    // だが、ロビー→キャラ選択→イントロのオーバーヘッドが常に10秒を大きく超えるため実質到達不能。
    // ResetSlot 等でここを触らないこと。
    internal static float LastMeetingEndTime = -1f;

    public static void FireMeetingEndForSlot(CustomRoles slot)
    {
        LastMeetingEndTime = Time.realtimeSinceStartup;

        int meetingNum = MeetingStates.MeetingNum;
        if (LastMeetingEndNum.TryGetValue(slot, out int last) && last == meetingNum) return;
        LastMeetingEndNum[slot] = meetingNum;

        if (!PlayersBySlot.TryGetValue(slot, out HashSet<byte> holders)) return;

        foreach (byte holderId in holders) FireEvent(slot, holderId, "on_meeting_end", byte.MaxValue);
    }

    // 毎 FixedUpdate、保持者ごとに1回呼ぶ (EkmTemplateRole.OnFixedUpdate から)。on_game_start の
    // 立ち上がり検出・on_second の 1Hz 間引き・fiber の手動ポンプ (常駐コルーチン禁止) をここでまとめて行う。
    public static void Pump(CustomRoles slot, PlayerControl pc)
    {
        // v1.2: EKR 全体で1本のポーリングエンジン (自己スロットリング — 0.25秒に満たない呼び出しは
        // 内部で即 return する)。ホルダーごとに毎 FixedUpdate 呼ばれる Pump に相乗りさせる (専用の
        // 毎フレーム経路を新しく作らない・spec §5「専用の毎フレーム経路を作らない」)。
        PollCnoTouchIfDue();

        // Wave 4: 対人近接 (on_near/on_far) と部屋変化 (on_room_enter/exit) のポーラー — PollCnoTouchIfDue
        // と同型の 0.25s 自己スロットリング相乗り。
        PollProximityIfDue();

        // v1.3: crowd-control (drag/field) の 1.0 秒 tick も同じ相乗り駆動 (自己スロットリング)。
        PumpCrowdControlIfDue();

        // Wave 5: 持続効果 (effect_give) の期限管理 — 同じ 0.25s 自己スロットリング相乗り。
        PollEffectsIfDue();

        // Wave 6: 発射体 (cno_launch) の 0.1 秒 tick も同じ相乗り駆動。
        PumpFlightsIfDue();

        if (!Runtime.TryGetValue(pc.PlayerId, out EkrHolderState state)) return;

        // Wave 2 (spec §2.3): seconds 経過の矢印は毎フレームここで自動 Remove する (専用ポーリング無し)。
        ExpireArrowsIfDue(state, pc.PlayerId);

        EkrDefinition def = GetDefinition(slot);

        // Wave 1: パッシブ層は logic の有無にも LogicDisabled にも依らず常に適用する
        // (spec §1.1 は「logic 無しでも passives 単独で可」・logic の暴走 auto-disable は
        // 「ブロックを止める」処置であって常時とくせいを剥奪する処置ではない)。
        TickPassives(pc, state, def?.ParsedPassives ?? EkrPassives.Default);

        if (state.LogicDisabled) return;
        if (def?.ParsedLogic == null) return;

        if (!state.GameStartFired && Main.IntroDestroyed)
        {
            state.GameStartFired = true;
            FireEvent(slot, pc.PlayerId, "on_game_start", byte.MaxValue);
        }

        // spec §2: on_second は「タスク中・自分生存中のみ」1Hz。
        if (Main.IntroDestroyed && GameStates.IsInTask && pc.IsAlive())
        {
            float now = Time.realtimeSinceStartup;

            if (state.LastSecondFireTime < 0f || now - state.LastSecondFireTime >= 1f)
            {
                state.LastSecondFireTime = now;
                FireEvent(slot, pc.PlayerId, "on_second", byte.MaxValue);
            }
        }

        // kill(target:"self") はキルパイプラインが同期的なので、この fiber を pump している最中に
        // 自分の on_death (spec §2: 死亡で fiber を全キャンセル→発火) が同一コールスタックで
        // state.Fibers を Clear()+再構築することがある。添字ベースの反復だと範囲外アクセスや
        // 「新しく生えた on_death fiber を誤って削除する」事故になるため、この tick で処理すべき
        // fiber を先にスナップショットし、各要素を pump する直前に「まだ生きているか」を再確認する
        // (Clear 済みならその fiber はこの tick ではもう進めない — 「全キャンセル」を壊さないため)。
        // 反復は前方 = 生えた順 (FIFO)。同一 tick で複数の fiber が生えるイベント (契約 §2 の部屋直遷移
        // exit→enter) の実行順を、発火順とそのまま一致させるため (逆順反復のせいで
        // enter→exit に転倒していた)。再入安全性は「反復方向」ではなく上のスナップショット + 直前の
        // 生存再確認 + 参照による削除が担保しているので、方向は自由に選べる。
        EkrFiber[] snapshot = state.Fibers.ToArray();

        for (var i = 0; i < snapshot.Length; i++)
        {
            EkrFiber fiber = snapshot[i];
            if (!state.Fibers.Contains(fiber)) continue; // 再入で既に Clear 済み — この tick はもう進めない

            bool keep = EkmLogicRuntime.Pump(fiber, EkrActionSink.Instance);

            // Wave 3: 回収は「捨てるかどうか」の判定より **前**。終了/打ち切りの直前に書かれた変数を
            // 落とすと、その書込みだけ on_var が無音で鳴らなくなる。
            DrainFiberWrites(state, fiber);

            if (keep) continue;

            state.Fibers.Remove(fiber); // 再入で既に居ない場合は no-op (添字ではなく参照で消す)
            if (!fiber.Aborted) continue;

            state.AbortCount++;

            if (state.AbortCount >= 3 && !state.LogicDisabled)
            {
                state.LogicDisabled = true;
                state.Fibers.Clear();
                PlayerControl.LocalPlayer.Notify(string.Format(Translator.GetString("EkrLogicAutoDisabled"), Translator.GetString(slot.ToString())), 10f);
                break;
            }
        }

        // Wave 3 (契約 §1.1 評価点①): fiber pump の切れ目。ここまでに溜まった変数書込みと生存数から
        // じょうたいトリガのエッジを判定する。
        FlushStateEdges(state, pc.PlayerId);

        // Wave 3 (契約 §3): 進捗テキストが変わっていたら名札の再送を予約する (notify/inspect/reveal と
        // 同じ per-holder ≤1/秒バケットを共有・新バケットを作らない)。
        TickProgressText(state, pc);
    }

    // ── Wave 3: じょうたいトリガのエッジ発火エンジン ────────────
    //
    // 意味論: 条件が **偽→真に遷移した瞬間に1回だけ**発火する (レベル発火にしない)。再武装は条件が
    // 偽に戻ったとき。武装状態は per-(holder, rule) で、InitRuntime で「その時点の真偽」に初期化する
    // (初期値が既に条件を満たしていても発火しない = 武装済み開始)。
    //
    // 評価点は fiber pump の切れ目4箇所だけ (Pump / PumpMeetingFibers / 攻撃プロローグ / 投票プロローグ)。
    // 「この pump で書かれた変数名」は fiber 側 (EkrFiber.WrittenVars) が記録し、ここが回収して評価する
    // — EkmLogicRuntime に「エッジ発火」という役職語彙を持ち込まないための境界 (同ファイル冒頭の層規律)。

    // fiber を1回 Pump した直後に必ず呼ぶ (Done/Aborted で捨てる fiber の書込みも拾うため、
    // 「捨てるかどうか」を判定するより前に回収すること — 順序を逆にすると最後の書込みが消える)。
    private static void DrainFiberWrites(EkrHolderState state, EkrFiber fiber)
    {
        if (fiber.WrittenVars.Count == 0) return;

        // §1.1 深さ1: 連鎖起点 (じょうたいトリガ由来) の fiber の書込みは別枠へ積む。
        (fiber.FromVarChain ? state.PendingChainVarWrites : state.PendingVarWrites).UnionWith(fiber.WrittenVars);
        fiber.WrittenVars.Clear();
    }

    // §1.1 の初期武装評価。EdgeArmed をルール数ぶん作り直し、各じょうたいトリガの現在の真偽を焼く。
    private static void RebuildEdgeArming(EkrHolderState state, List<EkrRule> rules)
    {
        state.EdgeArmed = new bool[rules.Count];
        state.HasAliveCountRule = false;

        var aliveCount = -1;

        for (var i = 0; i < rules.Count; i++)
        {
            EkrRule rule = rules[i];
            if (!rule.IsStateTrigger) continue;

            float actual;

            if (rule.When == "on_alive_count")
            {
                state.HasAliveCountRule = true;
                if (aliveCount < 0) aliveCount = Main.AllAlivePlayerControlsCount;
                actual = aliveCount;
            }
            else actual = state.Variables.GetValueOrDefault(rule.VarName);

            state.EdgeArmed[i] = EkmLogicRuntime.CompareValue(actual, rule.Cmp, rule.CmpValue);
        }
    }

    // 4つのフラッシュ点から呼ぶ。書込みが無く on_alive_count ルールも無ければ即 return (常時コストは
    // 辞書1個の Count 参照だけ — 送信ゼロ・ローカル演算のみなので予算対象外 §5)。
    private static void FlushStateEdges(EkrHolderState state, byte holderId)
    {
        bool anyWrites = state.PendingVarWrites.Count > 0 || state.PendingChainVarWrites.Count > 0;

        if (state.LogicDisabled || (!anyWrites && !state.HasAliveCountRule))
        {
            state.PendingVarWrites.Clear();
            state.PendingChainVarWrites.Clear();
            return;
        }

        List<EkrRule> rules = GetDefinition(state.Slot)?.ParsedLogic?.Rules;

        if (rules == null)
        {
            state.PendingVarWrites.Clear();
            state.PendingChainVarWrites.Clear();
            return;
        }

        // 束縛の差し替え (ReloadLibrary) でルール数が変わっていたら武装を作り直す。長さを信じて添字を
        // 引くと IndexOutOfRange になるため、フラッシュ側でも毎回長さを確認する (InitRuntime だけに
        // 頼らない)。作り直しは「今の真偽で武装済み開始」= 差し替え直後の一斉発火を起こさない側。
        if (state.EdgeArmed.Length != rules.Count) RebuildEdgeArming(state, rules);

        int aliveCount = state.HasAliveCountRule ? Main.AllAlivePlayerControlsCount : 0;

        for (var i = 0; i < rules.Count; i++)
        {
            EkrRule rule = rules[i];
            if (!rule.IsStateTrigger) continue;

            float actual;
            var chainOnly = false;

            if (rule.When == "on_alive_count")
            {
                // §1.3: 生存数の変化点は死亡/追放/切断/蘇生の4系統に散っており、切断は死亡イベントを
                // 通らない。書込みフックを列挙する方式は必ず漏れるので、フラッシュ毎に素直に数え直す。
                actual = aliveCount;
            }
            else
            {
                bool normal = state.PendingVarWrites.Contains(rule.VarName);
                bool chain = state.PendingChainVarWrites.Contains(rule.VarName);
                if (!normal && !chain) continue; // 書かれていない = 遷移しようがない (write-trigger)

                chainOnly = !normal;
                actual = state.Variables.GetValueOrDefault(rule.VarName);
            }

            bool now = EkmLogicRuntime.CompareValue(actual, rule.Cmp, rule.CmpValue);
            bool wasArmed = state.EdgeArmed[i];
            state.EdgeArmed[i] = now; // 武装遷移は発火の可否と独立に必ず反映する (§1.1)

            if (!now || wasArmed) continue;
            if (chainOnly) continue; // §1.1 深さ1: 連鎖起点の書込みは再武装/武装解除だけ効かせ発火は生まない

            SpawnStateTriggerFiber(state, holderId, rule);
        }

        state.PendingVarWrites.Clear();
        state.PendingChainVarWrites.Clear();
    }

    // §1.1: エッジが立ったルールの fiber を生やす。プロローグ直後の評価でも「通常の非同期 spawn」に
    // する (同期性が要る op [cancel_attack/cancel_vote] は on_var 配下で検証 reject されるため、
    // 攻撃解決スタックで走る fiber を増やす理由が構造的に無い)。
    private static void SpawnStateTriggerFiber(EkrHolderState state, byte holderId, EkrRule rule)
    {
        // §1.1 死後ゲート: FireEvent の「死後は on_death 以外発火しない」に乗せる。
        PlayerControl holderPc = holderId.GetPlayer();
        if (!holderPc || !holderPc.IsAlive()) return;

        // §1.1 / §5: cap 超過の発火は静かにドロップされる (武装遷移は上で済んでいるので、偽へ戻れば
        // ちゃんと再武装される)。ドロップされた発火はやり直さない — 既存イベントと同じ規約。
        if (state.Fibers.Count >= EkmLogicRuntime.MaxFibersPerHolder) return;

        var context = new EkrActionContext { HolderId = holderId, CtxId = byte.MaxValue, Slot = state.Slot };
        state.Fibers.Add(EkmLogicRuntime.Spawn(rule.Do, state.Variables, context, EkrActionSink.InOpcodeKill, fromVarChain: true));
    }

    // ── Wave 3: 進捗テキスト ────────────────────────────────────

    // 置換後の最終文字列の上限。変数値の膨張に対する安全弁 (置換前の 16字検査だけでは
    // 「{a}{b}{c}」型で膨らむ)。
    private const int ProgressRenderedMax = 24;

    // EkmTemplateRole.GetProgressText から呼ぶ表示本体。**加算型** — 呼び出し元が base の
    // ability-limit + タスク数の後ろへ足す (EKR はタスク持ち crew が基本形なのでタスク数を消さない)。
    // ⚠️ Utils.GetProgressText は共有 StringBuilder を使い回すので、ここから直接にも間接的にも
    // 呼んではいけない (再入で他の役職の進捗表示が壊れる)。
    public static string BuildProgressText(CustomRoles slot, byte playerId)
    {
        EkrDefinition def = GetDefinition(slot);
        if (def == null || def.ProgressText.Length == 0) return string.Empty;

        EkrHolderState state = GetHolderState(playerId);
        string text = EkrActionSink.SubstituteVariables(def.ProgressText, state?.Variables);
        if (text.Length > ProgressRenderedMax) text = text[..ProgressRenderedMax];

        // 色は役職色固定 (作者に色/サイズを触らせない構造ガード)。先頭の半角スペースで前の項と離す。
        return " " + Utils.ColorString(Utils.GetRoleColor(slot), text);
    }

    // Pump 末尾から毎 FixedUpdate 呼ぶ。進捗が実際に変わったときだけ名札の再送を予約する。
    // 予算: notify(self)/inspect/reveal と**同一の per-holder ≤1/秒バケットを共有**する (新バケット
    // 禁止)。再送は seer=target=保持者本人のみ (targets=1) — 進捗テキストは自分の名札の一部で、
    // 他人の画面には既存の役職テキスト表示経路でしか出ないため、ここで全員へ撒く理由が無い
    // (18スロット全束縛の最悪ケースでも identity nests は 18/秒 = 安全式 targets×体数/秒≤20 の内側)。
    // バケットが埋まっていたら送らずに戻る = 次のフラッシュで再試行され、**送るのは常に最新値**なので
    // 中間値の欠落は起きない (最終値保証)。会議中は Pump 自体が回らないので明示送信もしない。
    // 補足: NotifyRoles はモッドクライアント宛の指定送信を自前で早期 return するので、実際に
    // パケットが出るのは**非モッド客がホルダーのとき**だけ (ホスト自身の表示は毎フレームのローカル描画)。
    private static void TickProgressText(EkrHolderState state, PlayerControl pc)
    {
        EkrDefinition def = GetDefinition(state.Slot);
        if (def == null || def.ProgressText.Length == 0) return;
        if (!pc || !pc.IsAlive() || !Main.IntroDestroyed) return;

        string current = BuildProgressText(state.Slot, pc.PlayerId);

        // 初回は種を置くだけ (ゲーム開始時の通常の NotifyRoles で既に表示済み)。
        if (state.LastProgressSent == null)
        {
            state.LastProgressSent = current;
            return;
        }

        if (current == state.LastProgressSent) return;

        float now = Time.realtimeSinceStartup;
        if (state.LastNotifyTime.TryGetValue(pc.PlayerId, out float last) && now - last < 1f) return;

        state.LastNotifyTime[pc.PlayerId] = now;
        state.LastProgressSent = current;
        Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
    }

    // ── Wave 1: パッシブ層 ────────────────────────────────────────
    // 毎 FixedUpdate、保持者ごとに1回 Pump から呼ばれる。ここは logic の有無/停止に依存しない。

    private static void TickPassives(PlayerControl pc, EkrHolderState state, EkrPassives passives)
    {
        bool alive = pc.IsAlive();

        // 最後に生きていた座標のスナップショット (corpse=vanish が死体をマップ外へ飛ばすため —
        // EkrLogicOpcodes.ResolveSelfPosition の死後フォールバック用)。
        if (alive)
        {
            Vector2 livePos = pc.Pos();
            state.LastLivePosition = livePos;
            state.HasLastLivePosition = true;

            // Wave 6 (契約 §1.1 dir:"move"): 移動方向の 2 点履歴。Snowdown.cs:286-299 と同じく
            // 「0.01u 超動いたときだけ前へ送る」— 止まっている間は最後に動いた向きが残る。
            if (!state.MoveHistPrimed)
            {
                state.MoveHistPrimed = true;
                state.MoveHistLast = livePos;
                state.MoveHistLastLast = livePos;
            }
            else if (!FastVector2.DistanceWithinRange(livePos, state.MoveHistLast, 0.01f))
            {
                state.MoveHistLastLast = state.MoveHistLast;
                state.MoveHistLast = livePos;
            }
        }

        if (!Main.IntroDestroyed) return;

        // speedMult: AllPlayerSpeed 一発 write + MarkDirtySettings (spec §1.1)。捕捉は1回だけ (捕捉フラグ)。
        // 捕捉時に他役職が MinSpeed で凍結中だとその凍結値を「本来の速度」として掴んでしまうため、
        // 凍結中はゲーム既定値を baseline にする。
        // Wave 3 (契約 §4): 倍率は InitRuntime で焼いた実効値 (ホスト露出があればホストの値)。
        bool hasSpeed = state.EffectiveSpeedMult is < 0.999f or > 1.001f;

        // Wave 5: EKR の持続効果 (movement) が乗っている間は捕捉を遅らせる。効果で歪んだ値を
        // 「本来の速度」として掴むと、効果が切れたあとも歪みが passive baseline に残り続ける
        // (効果側の第3 writer 保護が復元を降ろすので自然回復しない)。到達経路は「recruit した相手に
        // 同じ fiber で effect_give する」— 新ホルダーの初回 TickPassives が効果の値を掴む窓。
        // 効果が切れた次の tick で通常どおり捕捉される (MinSpeed 凍結ガードと同じ「遅らせる」処置)。
        if (hasSpeed && !state.PassiveSpeedApplied && alive && !HasMovementEffect(pc.PlayerId))
        {
            float current = Main.AllPlayerSpeed.GetValueOrDefault(pc.PlayerId, Main.RealOptionsData.GetFloat(AmongUs.GameOptions.FloatOptionNames.PlayerSpeedMod));

            state.PassiveSpeedBaseline = Mathf.Approximately(current, Main.MinSpeed)
                ? Main.RealOptionsData.GetFloat(AmongUs.GameOptions.FloatOptionNames.PlayerSpeedMod)
                : current;

            state.PassiveSpeedApplied = true;
            Main.AllPlayerSpeed[pc.PlayerId] = state.PassiveSpeedBaseline * state.EffectiveSpeedMult;
            pc.MarkDirtySettings();
        }

        // doom: タスク中のみ進行・会議 (追放演出含む) で一時停止・0 到達で自殺死亡 (spec §1.1)。
        if (!passives.HasDoom || !alive) return;

        if (!GameStates.IsInTask || ExileController.Instance)
        {
            state.LastDoomTickTime = -1f; // 一時停止 — 再開時に基準時刻を取り直す (会議ぶんは進めない)
            return;
        }

        float now = Time.realtimeSinceStartup;

        if (state.LastDoomTickTime < 0f)
        {
            state.LastDoomTickTime = now;
            return;
        }

        if (now - state.LastDoomTickTime < 1f) return;
        state.LastDoomTickTime = now;
        state.DoomRemaining--;

        if (state.DoomRemaining > 0) return;

        // Suicide() 自身が IsInTask/ExileController を再チェックするので二重に安全。
        // on_death は Utils.AfterPlayerDeathTasks → FireDeath 経由で通常どおり発火する。
        // ⚠ Suicide() が no-op で返る局面 (Veteran 護衛中など保護役職の分岐) では DoomRemaining が
        // ≤0 のまま毎秒ここへ来て再試行する — 意図した挙動 (いずれ保護が切れて成立する)。
        pc.Suicide(PlayerState.DeathReason.Overtired);
    }

    // 残り秒数 (GetProgressText 等の表示側が使えるように公開しておく。0 = doom 無し)。
    public static int GetDoomRemaining(byte playerId)
    {
        return Runtime.TryGetValue(playerId, out EkrHolderState state) ? state.DoomRemaining : 0;
    }

    // ── Wave 1: パッシブの派生ルックアップ (spec §1.1) ───────────────────────────────────────
    // ⚠ 可変レジストリ (HashSet<byte> 等) を新設しない — ResetSlot は Init() 経由でゲーム中いつでも
    // 発火しうるため、EKR 全体の可変 static を持つと v1.3 の `_cc` と同じ孤児化事故を招く。
    // 役職からの都度引きなら「解除 = 剥奪・死亡・ゲーム終了で必ず復元」が構造的に無料になる。
    internal static EkrPassives GetPassivesFor(byte playerId)
    {
        PlayerControl pc = playerId.GetPlayer();
        if (!pc) return null;

        CustomRoles role = pc.GetCustomRole();
        return IsEkrRole(role) ? GetDefinition(role)?.ParsedPassives : null;
    }

    // ReportDeadBody の通報不可チェーン (Patches/PlayerControlPatch.cs) から呼ぶ。
    // vanish も含む: 死体を逃がす SnapTo は sender 経由なので**リモート客にしか効かず**、ホスト自身の
    // ローカル DeadBody は死亡地点に湧く (ExtendedPlayerControl.DoKill は MurderPlayer をローカル先行
    // 実行する)。ホストだけが見えて通報できる死体、という非対称を潰すために通報も止める。
    public static bool IsCorpseUnreportable(byte playerId)
    {
        return GetPassivesFor(playerId)?.Corpse is "noReport" or "vanish";
    }

    // ExtendedPlayerControl.Kill の Main.Invisible 分岐 (死体をマップ外へ逃がす既存経路) から呼ぶ。
    public static bool HasVanishingCorpse(byte playerId)
    {
        return GetPassivesFor(playerId)?.Corpse == "vanish";
    }

    // MeetingHudPatch の票数中央 switch 2箇所から呼ぶ (既定 1)。
    // Wave 2 (spec §3.1 vote_weight_set): per-holder のランタイムオーバーライドを passives より優先する
    // (「実行時オーバーライド」— 集計 switch への新規配線は不要、この 1 点を差せば Wave 1 の2箇所 arm が
    // そのまま効く = 無効化先勝ちも自動維持)。
    public static int GetVoteWeight(byte playerId)
    {
        if (Runtime.TryGetValue(playerId, out EkrHolderState state))
        {
            // vote_weight_set の実行時オーバーライドが最優先。無ければ InitRuntime で焼いた実効値
            // (Wave 3 のホスト露出があればホストの値が入っている)。
            return state.VoteWeightOverride ?? state.EffectiveVoteWeight;
        }

        return GetPassivesFor(playerId)?.VoteWeight ?? 1;
    }

    // EkrLogicOpcodes.VoteWeightSet から呼ぶ。予算なし・ローカル状態のみ (spec §3.1)。
    internal static void SetVoteWeightOverride(byte playerId, int value)
    {
        if (Runtime.TryGetValue(playerId, out EkrHolderState state)) state.VoteWeightOverride = value;
    }

    // ── Wave 2: reveal ────────────────────────────────────────
    // KnowRole override (EkmTemplateRole・4表示系を1点で拾う集約) が読む。seer/target 両方の playerId
    // だけで判定する — 集約側は Main.PlayerStates.Values.Any(x => x.Role.KnowRole(seer, target)) の
    // 全 PlayerState 総なめなので、this や x には一切依存しないこと。
    internal static bool HasRevealed(byte seerId, byte targetId)
    {
        return Runtime.TryGetValue(seerId, out EkrHolderState state) && state.Revealed.Contains(targetId);
    }

    internal static void Reveal(byte seerId, byte targetId)
    {
        if (Runtime.TryGetValue(seerId, out EkrHolderState state)) state.Revealed.Add(targetId);
    }

    // ── Wave 2: vote_block ────────────────────────────────────────
    // 「この会議のみ」target の票を無効化する集合。MeetingHudPatch の2箇所 (site1 canVote / site2 voteNum)
    // が読む。会議境界 (FireMeetingStart) でリセット — ResetSlot では触らない (trap 10: Init() はゲーム中
    // いつでも発火しうるので、会議スコープの状態をラウンド境界の関数で管理しない)。
    private static readonly HashSet<byte> VoteBlockedThisMeeting = [];

    public static bool IsVoteBlocked(byte targetId) => VoteBlockedThisMeeting.Contains(targetId);

    // EkrLogicOpcodes.VoteBlock から呼ぶ。予算 (≤1/会議/ホルダー) はホルダー側の使用済みフラグで強制する。
    internal static bool TryVoteBlock(byte holderId, byte targetId)
    {
        if (!Runtime.TryGetValue(holderId, out EkrHolderState state) || state.VoteBlockUsedThisMeeting) return false;

        state.VoteBlockUsedThisMeeting = true;
        VoteBlockedThisMeeting.Add(targetId);
        return true;
    }

    // ── Wave 2: vote_swap ────────────────────────────────────────
    // EKR 全体で同時1件/会議 (複数ホルダーの swap 連鎖は結果が順序依存になるため後着は静かにドロップ)。
    // 予約は「この会議の集計に swap を予約する」宣言 — 実際の入れ替えは MeetingHudPatch の
    // ManipulateVotingResult ディスパッチから ApplyVoteSwap が1回だけ読む。
    private static (byte HolderId, byte Saved1Id, byte Saved2Id)? _voteSwapReservation;

    // EkrLogicOpcodes.VoteSwap から呼ぶ。saved1/saved2 の失効判定 (死亡/切断/未保存) はここでは行わない
    // — 予約時点では有効でも集計時に失効しうるため、消費側 (ApplyVoteSwap) で改めて検証する。
    internal static bool TryReserveVoteSwap(byte holderId, byte saved1Id, byte saved2Id)
    {
        if (_voteSwapReservation.HasValue) return false; // EKR 全体で同時1件

        if (!Runtime.TryGetValue(holderId, out EkrHolderState state) || state.VoteSwapUsedThisMeeting) return false;

        state.VoteSwapUsedThisMeeting = true;
        _voteSwapReservation = (holderId, saved1Id, saved2Id);
        return true;
    }

    // MeetingHudPatch.cs の ManipulateVotingResult ディスパッチから1回だけ呼ぶ (Swapper.ManipulateVotingResult
    // と同じ呼び出し形)。saved1/saved2 いずれかが失効していれば no-op (spec §3.3)。内部票と表示票の両方を
    // 書き換える (Swapper.cs:203-220 と同じ二重書き換え規約 — 片方だけの書き換え禁止)。
    public static void ApplyVoteSwap(Dictionary<byte, int> votingData, MeetingHud.VoterState[] states)
    {
        if (!_voteSwapReservation.HasValue) return;

        (byte holderId, byte t1, byte t2) = _voteSwapReservation.Value;

        PlayerControl p1 = t1.GetPlayer();
        PlayerControl p2 = t2.GetPlayer();
        if (!p1 || !p2 || !p1.IsAlive() || !p2.IsAlive() || p1.Data == null || p1.Data.Disconnected || p2.Data == null || p2.Data.Disconnected) return;

        int count1 = votingData.GetValueOrDefault(t1, 0);
        int count2 = votingData.GetValueOrDefault(t2, 0);
        votingData[t1] = count2;
        votingData[t2] = count1;

        List<byte> votedFor1 = [];
        List<byte> votedFor2 = [];

        foreach (MeetingHud.VoterState st in states)
        {
            if (st.VotedForId == t1) votedFor1.Add(st.VoterId);
            else if (st.VotedForId == t2) votedFor2.Add(st.VoterId);
        }

        for (var i = 0; i < states.Length; i++)
        {
            if (votedFor1.Contains(states[i].VoterId)) states[i].VotedForId = t2;
            else if (votedFor2.Contains(states[i].VoterId)) states[i].VotedForId = t1;
        }

        Logger.Info($"EKR vote_swap: {t1} <-> {t2} (by {holderId})", "EkrManager");
    }

    // ── Wave 2: exile ─────────────────────────────────────────
    // エンジンのハード制限は「1会議1回」のみ (発動で会議が終わるため構造的に自明)。ゲーム単位の
    // 回数上限は掛けない (作者がブロックで組む)。
    private static bool _exileUsedThisMeeting;

    internal static bool TryConsumeExile()
    {
        if (_exileUsedThisMeeting) return false;
        _exileUsedThisMeeting = true;
        return true;
    }

    // ── Wave 2: 矢印3 op の per-holder 帳簿 ───────────────────
    // 予算: arrow_show+arrow_mark 合算 ≤1/秒/ホルダー + 同時 ≤4本/ホルダー (両種合算)。レートは
    // ここでは強制しない (EkrLogicOpcodes 側が LastArrowTime を見て消費前に判定する) — ここは
    // 「台帳への登録・期限切れの自動 Remove・全消し」だけを担当する。

    // target 矢印を (再) 登録する。戻り値 = 新規カウントとして扱うか (4本上限の判定用・再発行は false)。
    internal static bool RegisterArrowTarget(EkrHolderState state, byte targetId, float seconds)
    {
        bool isNew = !state.ArrowTargetExpiry.ContainsKey(targetId);
        state.ArrowTargetExpiry[targetId] = Time.realtimeSinceStartup + seconds;
        return isNew;
    }

    internal static bool RegisterArrowMark(EkrHolderState state, Vector2 pos, float seconds)
    {
        Vector3 pos3 = pos;
        float expireAt = Time.realtimeSinceStartup + seconds;

        for (int i = 0; i < state.ArrowMarks.Count; i++)
        {
            if (state.ArrowMarks[i].Pos != pos3) continue;
            state.ArrowMarks[i] = (pos3, expireAt);
            return false; // 既存の再発行 (spec §2.3: カウント不変)
        }

        state.ArrowMarks.Add((pos3, expireAt));
        return true;
    }

    internal static int CountActiveArrows(EkrHolderState state) => state.ArrowTargetExpiry.Count + state.ArrowMarks.Count;

    // Pump() から毎 FixedUpdate 呼ぶ (専用の毎フレーム経路を新設しない — spec §5 の一般原則に倣う)。
    // 期限切れの矢印だけを基盤 (TargetArrow/LocateArrow) から Remove する。
    private static void ExpireArrowsIfDue(EkrHolderState state, byte holderId)
    {
        if (state.ArrowTargetExpiry.Count > 0)
        {
            float now = Time.realtimeSinceStartup;
            List<byte> expired = null;

            foreach (KeyValuePair<byte, float> kv in state.ArrowTargetExpiry)
            {
                if (kv.Value > now) continue;
                (expired ??= []).Add(kv.Key);
            }

            if (expired != null)
            {
                foreach (byte targetId in expired)
                {
                    state.ArrowTargetExpiry.Remove(targetId);
                    TargetArrow.Remove(holderId, targetId);
                }
            }
        }

        if (state.ArrowMarks.Count == 0) return;

        float now2 = Time.realtimeSinceStartup;

        for (int i = state.ArrowMarks.Count - 1; i >= 0; i--)
        {
            if (state.ArrowMarks[i].ExpireAt > now2) continue;
            Vector3 pos = state.ArrowMarks[i].Pos;
            state.ArrowMarks.RemoveAt(i);
            LocateArrow.Remove(holderId, pos);
        }
    }

    // arrow_hide: ホルダーの EKR 矢印 (両種) を全消し。TargetArrow/
    // LocateArrow は playerId 単位の共有ストアだが、1人のプレイヤーは同時に1役職しか持てないため
    // 「この seer の矢印は全部この EKR ロジックが出したもの」が常に成り立つ (他ロールとの混線は無い)。
    internal static void HideArrows(EkrHolderState state, byte holderId)
    {
        state.ArrowTargetExpiry.Clear();
        state.ArrowMarks.Clear();
        TargetArrow.RemoveAllTarget(holderId);
        LocateArrow.RemoveAllTarget(holderId);
    }

    // ── R1: EKR 全体の cross-holder レート予算 (spec §3 2026-08-09 追記) ──────────

    // teleport は Utils.TP の共有 SnapTo トークンバケットに乗っている。ホルダー毎の ≤1/2秒だけでは
    // Maximum=15 で全ホルダーが同時に撃つと共有 cap を枯渇させ、EKR 以外の TP 系能力まで巻き込んで
    // 止めてしまう (公式サーバーの SnapTo 本数上限と同型の懸念)。EKR 全体で ≤2/秒に鎖をかける。
    private static readonly List<float> _recentTeleportTimes = [];

    internal static bool TryConsumeGlobalTeleportBudget()
    {
        float now = Time.realtimeSinceStartup;
        _recentTeleportTimes.RemoveAll(t => now - t >= 1f);

        if (_recentTeleportTimes.Count >= 2) return false;

        _recentTeleportTimes.Add(now);
        return true;
    }

    // v1.1 (2026-08-09): CNO を生成/再生成する op (cno_spawn/dummy_spawn/cno_show) の
    // cross-holder レート予算 (spec §5)。per-holder interval と全体 ≤10 体 (在庫の天井) だけでは、
    // on_second のロックステップ (全ホルダーの LastSecondFireTime 初期値が共通 -1f → 同一フレームで
    // 発火し続ける) や lint L9 推奨形 (会議明け wait 10.5) の WakeAt 同刻で、複数ホルダーの spawn が
    // 同一窓に束なるのを止められない。spawn 1体には ReserveFanoutBudget 未課金の付帯送信
    // (spawn broadcast ≈4 nests + player-like は outfit ≈4 nests) がぶら下がるため、DummySpawner の
    // 実績式 (targets+8)/12 秒/体 (安全実績域 targets×体数/秒 ≤20 nests/s) を
    // そのまま EKR 全体の最小 spawn 間隔として強制する (TryConsumeGlobalTeleportBudget と同型の鎖)。
    // 超過は静かにドロップ (spec §5 の既存原則 — 作者には per-holder レートと区別が付かないが、
    // cross-holder 干渉は作者に制御不能なので lint では教えない)。
    private static float _lastGlobalCnoSpawnTime = -1f;

    internal static bool TryConsumeGlobalCnoSpawnBudget()
    {
        int fanoutTargets = 0;
        foreach (PlayerControl pc in Main.EnumeratePlayerControls())
            if (!pc.AmOwner) fanoutTargets++;

        float interval = Mathf.Max(0.5f, (fanoutTargets + 8) / 12f);
        float now = Time.realtimeSinceStartup;
        if (_lastGlobalCnoSpawnTime >= 0f && now - _lastGlobalCnoSpawnTime < interval) return false;

        _lastGlobalCnoSpawnTime = now;
        return true;
    }

    // 複数対象 notify の cross-holder 予算 (spec §5 に明文化済み・2026-08-11)。
    // Wave 1 の notify は複数対象 (all/room) を受理する唯一の op。受け取り手1人につき
    // Utils.NotifyRoles(SpecifySeer, SpecifyTarget) = RpcSetName 1本なので、満員での target:"all" は
    // 1フレームに14本の identity 送信になる。per-(ホルダー,受け取り手) バケットは「同じ人へ連投
    // しない」ことしか保証せず、複数ホルダーが同一フレームに撃つのを止められない
    // (on_second のロックステップで同時発火が既定・v1.1 で cno_spawn に同じ穴があった)。
    // EKR 全体で「複数対象 notify は 1 秒に 1 回」に締める。単数対象 (既定 self) は対象外。
    private static float _lastGlobalNotifyBroadcastTime = -1f;

    internal static bool TryConsumeGlobalNotifyBroadcastBudget()
    {
        float now = Time.realtimeSinceStartup;
        if (_lastGlobalNotifyBroadcastTime >= 0f && now - _lastGlobalNotifyBroadcastTime < 1f) return false;

        _lastGlobalNotifyBroadcastTime = now;
        return true;
    }

    // ── v1.2: 接触判定エンジン (spec §2 on_cno_touch / §3,§5 ポータル warp) ──────────────────────
    // 0.25秒ポーリング (毎フレーム判定経路を作らない・spec §5)。進入 0.8u / 退出 1.0u のヒステリシス。
    // Pump() から毎 FixedUpdate 呼ばれるが、この関数自身が 0.25秒に満たない呼び出しを即 return する
    // ことで実質1本のポーリングにする (呼び出し元がホルダー数ぶん重複しても中身は1回しか走らない)。

    private const float TouchPollInterval = 0.25f;
    private const float TouchEnterRadius = 0.8f;
    private const float TouchExitRadius = 1.0f;
    private const float TouchDebounceSeconds = 1f;
    private const float PortalWarpCooldownSeconds = 3f;

    private static float _lastTouchPollTime = -1f;

    private static void PollCnoTouchIfDue()
    {
        float now = Time.realtimeSinceStartup;
        if (_lastTouchPollTime >= 0f && now - _lastTouchPollTime < TouchPollInterval) return;
        _lastTouchPollTime = now;

        // 会議中/ロビーは世界座標が意味を持たない (プレイヤーは MeetingHud/ロビー UI にいる)。
        // spec は on_cno_touch を「タスク中のみ」とは明記していないが、判定対象そのものが存在しない
        // 期間なので実装上の安全ガードとして間引く (誤検出防止・過剰な no-op ループの回避)。
        if (!GameStates.IsInTask) return;

        IReadOnlyList<PlayerControl> livePlayers = Main.AllAlivePlayerControls;
        if (livePlayers.Count == 0) return;

        // fiber 実行が Teardown (Runtime.Remove) を誘発しても列挙を壊さないようスナップショットで回す
        // (PumpMeetingFibers の Fibers.ToArray() と同じ対応)。
        foreach ((byte holderId, EkrHolderState state) in Runtime.ToArray())
        {
            if (state.LogicDisabled) continue;

            CustomRoles? holderSlot = null;

            // ── on_cno_touch: 自分の CNO/ダミー (CnoSlots 1..3) ──
            for (int i = 0; i < state.CnoSlots.Length; i++)
            {
                if (state.CnoSlots[i] is not CustomNetObject cno || !cno.playerControl)
                {
                    state.TouchSensorWasLive[i] = false;
                    continue;
                }

                Vector2 sensorPos = cno.Position;

                // 実体化の立ち上がり (会議明け復活・張り直し) でラッチ/デバウンスを作り直す。方針は設置時
                // と同じ「その時点で半径内にいる者は発火なしでラッチ済み」(PrimeTouchSensor)。
                if (!state.TouchSensorWasLive[i])
                {
                    state.TouchSensorWasLive[i] = true;
                    PrimeTouchSensor(state, i, sensorPos, false);
                }

                HashSet<byte> latched = state.TouchLatched[i];

                foreach (PlayerControl pc in livePlayers)
                {
                    float dist = Vector2.Distance(pc.Pos(), sensorPos);
                    bool inside = latched.Contains(pc.PlayerId);

                    if (!inside && dist <= TouchEnterRadius)
                    {
                        latched.Add(pc.PlayerId);

                        float lastFire = state.TouchLastFireTime[i].GetValueOrDefault(pc.PlayerId, -1f);
                        if (lastFire >= 0f && now - lastFire < TouchDebounceSeconds) continue;
                        state.TouchLastFireTime[i][pc.PlayerId] = now;

                        holderSlot ??= SlotForHolder(holderId);
                        if (holderSlot.HasValue) FireCnoTouch(holderSlot.Value, holderId, i + 1, pc.PlayerId);
                    }
                    else if (inside && dist >= TouchExitRadius)
                    {
                        latched.Remove(pc.PlayerId);
                    }
                }
            }

            // ── ポータル warp: 両側設置済みのときだけ判定 ──
            // (立ち上がり検出は片側だけ実体化済みの間も行う — 相方が遅れて実体化した瞬間に旧ラッチで
            //  すり抜けないように、warp 判定より先に per-side でプライムしておく)
            for (int side = 0; side < 2; side++)
            {
                if (state.Portals[side] is not CustomNetObject liveCheck || !liveCheck.playerControl)
                {
                    state.PortalSensorWasLive[side] = false;
                }
                else if (!state.PortalSensorWasLive[side])
                {
                    state.PortalSensorWasLive[side] = true;
                    PrimeTouchSensor(state, side, liveCheck.Position, true);
                }
            }

            for (int side = 0; side < 2; side++)
            {
                if (state.Portals[side] is not CustomNetObject sensorCno || !sensorCno.playerControl) continue;
                if (state.Portals[1 - side] is not CustomNetObject otherCno || !otherCno.playerControl) continue;

                Vector2 sensorPos = sensorCno.Position;
                Vector2 destination = otherCno.Position;
                HashSet<byte> latched = state.PortalLatched[side];

                foreach (PlayerControl pc in livePlayers)
                {
                    float dist = Vector2.Distance(pc.Pos(), sensorPos);
                    bool inside = latched.Contains(pc.PlayerId);

                    if (!inside && dist <= TouchEnterRadius)
                    {
                        latched.Add(pc.PlayerId);
                        TryWarpThroughPortal(state, pc, destination);
                    }
                    else if (inside && dist >= TouchExitRadius)
                    {
                        latched.Remove(pc.PlayerId);
                    }
                }
            }
        }
    }

    // slot -> 保持者の逆引き (PlayersBySlot は slot キー)。EKR は Maximum=15・10 slot なので毎回スキャンでも軽い。
    private static CustomRoles? SlotForHolder(byte holderId)
    {
        // Slots 配列でなく PlayersBySlot を走査する (埋込出荷役職の保持者も逆引きできるように)。
        foreach ((CustomRoles slot, HashSet<byte> holders) in PlayersBySlot)
            if (holders.Contains(holderId))
                return slot;

        return null;
    }

    // spec §3: warp の TP は teleport と同じ EKR 全体 ≤2/秒予算を消費。予算枯渇時はその接触は消滅
    // (ラッチ済み扱い・リトライしない — latch は呼び出し前の enter 検出時点で既に立っている)。
    private static void TryWarpThroughPortal(EkrHolderState state, PlayerControl pc, Vector2 destination)
    {
        if (!pc.IsAlive()) return;

        float now = Time.realtimeSinceStartup;
        if (state.PortalLastWarpTime.TryGetValue(pc.PlayerId, out float last) && now - last < PortalWarpCooldownSeconds) return;

        if (!TryConsumeGlobalTeleportBudget()) return;

        state.PortalLastWarpTime[pc.PlayerId] = now;
        Utils.TP(pc.NetTransform, destination, minInterval: 0f);

        PrelatchTouchSensorsNear(pc.PlayerId, destination);
    }

    // v1.2 (spec §2): EKR 起因の TP (teleport/teleport_other/ポータル warp) で移動したプレイヤーは、
    // 着地点で半径内の全接触センサーにラッチ済み扱い — ポータル間 ping-pong 無限ループの構造的回避。
    // teleport/teleport_other (EkrLogicOpcodes) とポータル warp (上記 TryWarpThroughPortal) の3経路から呼ぶ。
    internal static void PrelatchTouchSensorsNear(byte playerId, Vector2 landedPos)
    {
        foreach ((byte holderId, EkrHolderState state) in Runtime)
        {
            for (int i = 0; i < state.CnoSlots.Length; i++)
            {
                if (state.CnoSlots[i] is not CustomNetObject cno || !cno.playerControl) continue;
                if (Vector2.Distance(cno.Position, landedPos) <= TouchEnterRadius) state.TouchLatched[i].Add(playerId);
            }

            for (int side = 0; side < 2; side++)
            {
                if (state.Portals[side] is not CustomNetObject cno || !cno.playerControl) continue;
                if (Vector2.Distance(cno.Position, landedPos) <= TouchEnterRadius) state.PortalLatched[side].Add(playerId);
            }

            // Wave 4 (契約 §1.1): EKR 起因の TP では on_near も発火させない — 動いた本人を、着地点から
            // 各 on_near rule の進入半径内にいるホルダーのラッチへ登録する (歩いて入り直したときだけ
            // 発火する)。teleport/teleport_other/pull/drag/field/ポータル warp の全 TP 経路がこの1関数を
            // 経由するため、呼び出し点を増やさずに全経路へ波及する。
            if (holderId == playerId) continue; // 自分は自分の on_near の対象外

            List<EkrRule> rules = GetDefinition(state.Slot)?.ParsedLogic?.Rules;
            if (rules == null) continue;

            PlayerControl holderPc = holderId.GetPlayer();
            if (!holderPc || !holderPc.IsAlive()) continue;

            Vector2 holderPos = holderPc.Pos();
            float distToHolder = Vector2.Distance(holderPos, landedPos);
            var ensured = false;

            for (var i = 0; i < rules.Count; i++)
            {
                if (rules[i].When != "on_near") continue;
                if (distToHolder > ProximityRadius(rules[i].Radius)) continue;

                if (!ensured)
                {
                    EnsureProximityArrays(state, rules, holderPos);
                    ensured = true;
                }

                state.NearLatched[i].Add(playerId);
            }
        }
    }

    // v1.2 (spec §2): 設置時に半径内へ既にいるプレイヤーはラッチ済み扱い (placer self-grab 既知型の
    // 構造的回避)。cno_spawn/dummy_spawn (idx=CnoSlots index) とportal_place (idx=Portals index) の
    // 両方から呼ぶ (isPortal で対象配列を切り替える)。
    internal static void PrimeTouchSensor(EkrHolderState state, int idx, Vector2 pos, bool isPortal)
    {
        HashSet<byte> latched = isPortal ? state.PortalLatched[idx] : state.TouchLatched[idx];
        latched.Clear();
        if (!isPortal) state.TouchLastFireTime[idx].Clear();

        foreach (PlayerControl pc in Main.AllAlivePlayerControls)
            if (Vector2.Distance(pc.Pos(), pos) <= TouchEnterRadius) latched.Add(pc.PlayerId);
    }

    // ── Wave 4: 対人近接/部屋変化ポーラー ────────────────────
    // PollCnoTouchIfDue と同型の Pump ライダー (0.25s グローバル自己スロットリング・送信ゼロ・
    // ローカル演算のみで予算対象外 §5)。on_near/on_far のラッチ/武装は per-(holder, rule) — 複数 rule が
    // 別 radius/who を持てるため、発火は FireEvent の onlyRuleIndex でその 1 rule にスコープする。

    private const float ProximityDebounceSeconds = 1f; // 発火間デバウンス / per-(rule, 相手) — TouchDebounceSeconds と同値 (契約 §1.1)
    private const float ProximityHysteresis = 0.5f; // radius で進入発火・radius+0.5 超で再武装 (契約 §1.2)

    private static float _lastProximityPollTime = -1f;

    // 契約 §1.2: radius tier の実値 small=1.5u / medium=3.0u / large=5.0u。field の 3/5/7u とは
    // **別スケール** (あちらはゾーン、こちらは対人 — 同語別値は TS 側 tooltip で明示)。
    private static float ProximityRadius(string tier)
    {
        return tier switch { "small" => 1.5f, "large" => 5f, _ => 3f };
    }

    private static void PollProximityIfDue()
    {
        float now = Time.realtimeSinceStartup;
        if (_lastProximityPollTime >= 0f && now - _lastProximityPollTime < TouchPollInterval) return;
        _lastProximityPollTime = now;

        // 会議中/ロビーは PollCnoTouchIfDue と同じ理由で間引く。追放演出中も止める — 演出中に
        // respawn 前の座標で「現状真偽」を焼くと、会議明けの再配置が歩行と誤認されて部屋 enter /
        // on_near が一斉発火する (契約 §2「会議明けスポーンで発火しない」を守る側)。
        // AntiBlackout の役職ジャグリング窓 (SkipTasks) も止める — ExileController は WrapUp 完了で先に
        // 消えるのに SkipTasks は RevertToActualRoleTypes (+2秒) まで残るため、この窓だけポーラーが
        // 動いて「会議中の座標」を現状真偽として焼き、直後の BeforeMeetingPositions 復元 TP を歩行と
        // 誤認する余地があった。RoomPrimed=false のままなので窓明け最初のポーリングが
        // 焼き直す = 発火しない側で再武装される。
        if (!GameStates.IsInTask || ExileController.Instance || AntiBlackout.SkipTasks) return;

        IReadOnlyList<PlayerControl> livePlayers = Main.AllAlivePlayerControls;

        // FireEvent は fiber を spawn するだけだが、PollCnoTouchIfDue と同じスナップショット規約で回す
        // (将来 fiber 同期実行が入っても列挙が壊れない側)。
        foreach ((byte holderId, EkrHolderState state) in Runtime.ToArray())
        {
            if (state.LogicDisabled) continue;

            List<EkrRule> rules = GetDefinition(state.Slot)?.ParsedLogic?.Rules;
            if (rules == null) continue;

            var hasNearFar = false;
            var hasRoom = false;

            foreach (EkrRule rule in rules)
            {
                if (rule.When is "on_near" or "on_far") hasNearFar = true;
                else if (rule.When is "on_room_enter" or "on_room_exit") hasRoom = true;
            }

            if (!hasNearFar && !hasRoom) continue;

            PlayerControl holderPc = holderId.GetPlayer();

            // 契約 §1.1/§2: ホルダー生存中のみ。死んだホルダーも Pump は回り続ける
            // (NeedsUpdateAfterDeath) ので明示ガード必須。
            if (!holderPc || !holderPc.IsAlive()) continue;

            if (hasRoom) TrackRoomChange(state, holderId, holderPc);

            if (!hasNearFar) continue;

            Vector2 holderPos = holderPc.Pos();
            EnsureProximityArrays(state, rules, holderPos);

            for (var i = 0; i < rules.Count; i++)
            {
                EkrRule rule = rules[i];

                if (rule.When == "on_near") StepNearRule(state, holderId, holderPos, i, rule, livePlayers, now);
                else if (rule.When == "on_far") StepFarRule(state, holderId, holderPos, i, rule);
            }
        }
    }

    // 近接ラッチ/far 武装の配列を rule 数と突き合わせる (RebuildEdgeArming :2099 と同じ長さ再確認 —
    // 束縛差し替えで長さが変わったら「現状真偽」で作り直し、差し替え直後の一斉発火を起こさない)。
    // FireMeetingStart が配列を空へ戻すことで、会議明け最初のポーリングもここを通って再プライムされる。
    private static void EnsureProximityArrays(EkrHolderState state, List<EkrRule> rules, Vector2 holderPos)
    {
        if (state.NearLatched.Length == rules.Count) return;

        int n = rules.Count;
        state.NearLatched = new HashSet<byte>[n];
        state.NearLastFireTime = new Dictionary<byte, float>[n];
        state.NearWatchedId = new byte[n];
        state.FarArmed = new bool[n];
        state.FarWatchedId = new byte[n];

        for (var i = 0; i < n; i++)
        {
            state.NearLatched[i] = [];
            state.NearLastFireTime[i] = new Dictionary<byte, float>();
            state.NearWatchedId[i] = byte.MaxValue; // 次のポーリングが参照を確立して現状真偽を焼く (§1.2)
            state.FarWatchedId[i] = byte.MaxValue; // 次のポーリングが参照を確立して現状真偽を焼く (§1.3)
        }

        // on_near: いま進入半径内にいる生存者はラッチ済み扱い (PrimeTouchSensor の「設置時に半径内へ
        // 既にいる者は発火なしでラッチ」と同じ方針を rule 軸へ適用)。
        foreach (PlayerControl pc in Main.AllAlivePlayerControls)
        {
            float dist = Vector2.Distance(pc.Pos(), holderPos);

            for (var i = 0; i < n; i++)
            {
                if (rules[i].When != "on_near") continue;
                if (dist <= ProximityRadius(rules[i].Radius)) state.NearLatched[i].Add(pc.PlayerId);
            }
        }
    }

    // on_near (契約 §1.2): 相手が radius 内へ入った瞬間に1回 (per-(rule, 相手) ラッチ + 1秒デバウンス)。
    // who フィルタ (linked/saved1/saved2) は**その1人だけ**を監視 — 失効中 = 監視対象なし = 何も起きない。
    private static void StepNearRule(EkrHolderState state, byte holderId, Vector2 holderPos, int ruleIndex, EkrRule rule, IReadOnlyList<PlayerControl> livePlayers, float now)
    {
        if (rule.Who is "linked" or "saved1" or "saved2")
        {
            byte watchedId = ResolveWatchedId(state, rule.Who);

            if (watchedId == byte.MaxValue || watchedId == holderId)
            {
                state.NearWatchedId[ruleIndex] = byte.MaxValue;
                return;
            }

            if (state.NearWatchedId[ruleIndex] != watchedId)
            {
                // 参照の (再) 確立 — 既に radius 内にいる相手はラッチ済み扱いで発火しない (§1.1 の
                // PrimeTouchSensor 方針を参照確立にも適用)。歩いて出入りし直したときだけ発火する。
                state.NearWatchedId[ruleIndex] = watchedId;
                state.NearLatched[ruleIndex].Clear();

                PlayerControl w0 = watchedId.GetPlayer();
                if (w0 && Vector2.Distance(w0.Pos(), holderPos) <= ProximityRadius(rule.Radius))
                    state.NearLatched[ruleIndex].Add(watchedId);
            }

            PlayerControl watched = watchedId.GetPlayer();
            if (watched) StepNearCandidate(state, holderId, holderPos, ruleIndex, rule, watched, now);

            return;
        }

        // "anyone": 自分以外の全生存実プレイヤー (死者・ダミー CNO は発火させない §1.1)。
        foreach (PlayerControl pc in livePlayers)
        {
            if (pc.PlayerId == holderId) continue;
            StepNearCandidate(state, holderId, holderPos, ruleIndex, rule, pc, now);
        }
    }

    private static void StepNearCandidate(EkrHolderState state, byte holderId, Vector2 holderPos, int ruleIndex, EkrRule rule, PlayerControl other, float now)
    {
        float radius = ProximityRadius(rule.Radius);
        float dist = Vector2.Distance(other.Pos(), holderPos);
        HashSet<byte> latched = state.NearLatched[ruleIndex];
        bool inside = latched.Contains(other.PlayerId);

        if (!inside && dist <= radius)
        {
            latched.Add(other.PlayerId);

            Dictionary<byte, float> lastFire = state.NearLastFireTime[ruleIndex];
            float last = lastFire.GetValueOrDefault(other.PlayerId, -1f);
            if (last >= 0f && now - last < ProximityDebounceSeconds) return;
            lastFire[other.PlayerId] = now;

            FireEvent(state.Slot, holderId, "on_near", other.PlayerId, onlyRuleIndex: ruleIndex);
        }
        else if (inside && dist >= radius + ProximityHysteresis) latched.Remove(other.PlayerId);
    }

    // on_far (契約 §1.3): 逆ヒステリシス — armed (一度 radius 内へ入った) の状態で radius+0.5 を超えた
    // 瞬間に1回発火・radius 内へ戻ったら再武装。参照の (再) 確立時は現状真偽を焼く (link/remember した
    // 時点で既に遠くにいても発火しない)。相手の死亡・切断・失効は武装解除のみ (発火しない — 死は
    // on_linked_death の領分。死亡側の解除は FireDeath が即時に行い、ここは lazy 側の防御)。
    private static void StepFarRule(EkrHolderState state, byte holderId, Vector2 holderPos, int ruleIndex, EkrRule rule)
    {
        byte watchedId = ResolveWatchedId(state, rule.Who);

        if (watchedId == byte.MaxValue || watchedId == holderId)
        {
            state.FarArmed[ruleIndex] = false;
            state.FarWatchedId[ruleIndex] = byte.MaxValue;
            return;
        }

        if (state.FarWatchedId[ruleIndex] != watchedId)
        {
            state.FarWatchedId[ruleIndex] = watchedId;
            state.FarArmed[ruleIndex] = false; // 参照の (再) 確立 — 現状真偽から焼き直す (§1.3)
        }

        PlayerControl watched = watchedId.GetPlayer();
        if (!watched) return; // ResolveWatchedId 直後の消滅 (通常到達しない防御)

        float radius = ProximityRadius(rule.Radius);
        float dist = Vector2.Distance(watched.Pos(), holderPos);

        if (!state.FarArmed[ruleIndex])
        {
            if (dist <= radius) state.FarArmed[ruleIndex] = true; // 一度近づいた — 武装 (発火はしない)
        }
        else if (dist >= radius + ProximityHysteresis)
        {
            state.FarArmed[ruleIndex] = false; // 再武装は radius 内へ戻ったとき (上の分岐が担う)
            FireEvent(state.Slot, holderId, "on_far", watchedId, onlyRuleIndex: ruleIndex);
        }
    }

    // who (linked/saved1/saved2) の監視対象解決。失効 (未設定/死亡/切断) は lazy に解消して番兵を返す
    // (EkrLogicOpcodes.ResolveSaved/ResolveLinked と同じ参照整合性3原則②)。
    // ⚠️ linked の「死亡」だけは lazy 解消の対象外 (契約 §3.1「相手の死亡 = on_linked_death 発火後に解消」)。
    // 追放死は WrapUpPostfix (ExilePatch.cs:72) が SetDead を同期で立てるのに対し FireDeath は同 :132 の
    // LateTask (+3.5秒)。近接ポーラーのゲート (ExileController/AntiBlackout.SkipTasks) は +2秒で開くため、
    // その差の窓 (約1.5秒 = 0.25秒間隔で約6回) でここが LinkedId を先に消すと、FireDeath の一致判定
    // (hs.LinkedId != target.PlayerId で continue) が外れて on_linked_death が無音で落ちる
    // (キル死は AfterPlayerDeathTasks が同期なので隙が無く再現しない)。死亡は「今は
    // 解決できない」として番兵だけ返し (発火はしない)、解消の権限は FireDeath へ一本化する。
    // 切断/消滅は FireDeath が !disconnect ゲートで拾わないので従来どおりここで lazy 解消する。
    private static byte ResolveWatchedId(EkrHolderState state, string who)
    {
        byte id = who switch
        {
            "linked" => state.LinkedId,
            "saved1" => state.Saved[0],
            "saved2" => state.Saved[1],
            _ => byte.MaxValue
        };

        if (id == byte.MaxValue) return byte.MaxValue;

        PlayerControl pc = id.GetPlayer();
        if (pc && pc.IsAlive() && pc.Data != null && !pc.Data.Disconnected) return id;

        // linked かつ「実体はあり接続もしているが死んでいるだけ」= FireDeath 待ち。ここでは消さない。
        if (who == "linked" && pc && pc.Data != null && !pc.Data.Disconnected) return byte.MaxValue;

        switch (who)
        {
            case "linked": state.LinkedId = byte.MaxValue; break;
            case "saved1": state.Saved[0] = byte.MaxValue; break;
            case "saved2": state.Saved[1] = byte.MaxValue; break;
        }

        return byte.MaxValue;
    }

    // 部屋変化 (契約 §2): per-holder の GetPlainShipRoom()?.RoomId 前回値比較 (Satellite.cs:87-89 の
    // per-holder 版)。null = 部屋でない (廊下/屋外/ベント/死者)。null→A = enter / A→null = exit /
    // A→B 直遷移 = exit(A) → enter(B) の順に同一ポーリング内で両方発火。ベント・TP・追い出しでも
    // 部屋が変われば発火する (on_near の TP プレラッチとは意図的に非対称 §2)。会議開始で PrevRoom を
    // リセットし (FireMeetingStart)、会議明け最初のポーリングは現在の部屋を武装済み開始として焼くだけ。
    private static void TrackRoomChange(EkrHolderState state, byte holderId, PlayerControl holderPc)
    {
        PlainShipRoom room = holderPc.GetPlainShipRoom();
        SystemTypes? current = room ? room.RoomId : (SystemTypes?)null;

        if (!state.RoomPrimed)
        {
            state.RoomPrimed = true;
            state.PrevRoom = current;
            return;
        }

        if (current == state.PrevRoom) return;

        SystemTypes? prev = state.PrevRoom;
        state.PrevRoom = current;

        if (prev != null) FireEvent(state.Slot, holderId, "on_room_exit", byte.MaxValue);
        if (current != null) FireEvent(state.Slot, holderId, "on_room_enter", byte.MaxValue);
    }

    // ── Wave 4: link / unlink (予算なし・ローカル状態のみ) ──

    internal static void Link(EkrHolderState state, byte targetId)
    {
        state.LinkedId = targetId; // 再実行 = 張り替え (旧リンクは無言で解消・§3.1 portal_place の「移設」方針と同型)
        ResetFarArmingForLinked(state);
    }

    internal static void Unlink(EkrHolderState state)
    {
        state.LinkedId = byte.MaxValue;
        ResetFarArmingForLinked(state);
    }

    // link/unlink で「linked を見ている on_far」の武装を張り直す (§1.3: 初期武装は参照成立後の現状真偽)。
    // FarWatchedId を番兵へ戻せば、次のポーリングが参照を再確立して現状を焼く — 同一人物への再 link でも
    // 必ず焼き直す (前回値の差分検出だけでは同一人物の張り替えを見逃す)。
    private static void ResetFarArmingForLinked(EkrHolderState state)
    {
        List<EkrRule> rules = GetDefinition(state.Slot)?.ParsedLogic?.Rules;
        if (rules == null) return;

        int n = Math.Min(rules.Count, state.FarArmed.Length);

        for (var i = 0; i < n; i++)
        {
            if (rules[i].When != "on_far" || rules[i].Who != "linked") continue;
            state.FarArmed[i] = false;
            state.FarWatchedId[i] = byte.MaxValue;
        }
    }

    // ── Wave 4: recruit (相手を自分と同じ EKR 役職へ変換) ──────

    private const float RecruitPerHolderInterval = 10f; // §5: per-holder ≤1/10秒
    private const float RecruitGlobalInterval = 5f; // §5: EKR 全体 ≤1/5秒 (SetRole バーストの頻度の砦)

    // _lastGlobalCnoSpawnTime と同じく Time.realtimeSinceStartup (単調時計) との差分比較のみで
    // リセット不要 — ResetSlot からは触らない (trap: init_fires_midgame_slot_reset_clobbers_global)。
    private static float _lastGlobalRecruitTime = -1f;

    // Wave 5: slotNumber1Based は変換先スロットの指名 (1..18)。
    // 0 = 省略 = 現行どおり「自分と同じ役職」(完全後方互換)。解決だけが増え、レート/2呼び固定順・
    // 他の no-op 条件は一切変えない。
    internal static void TryRecruit(EkrHolderState state, byte holderId, PlayerControl targetPc, int slotNumber1Based = 0)
    {
        // no-op 条件 (すべて予算不消費・§4): 死者/切断/自分自身/既に同スロット。
        if (!targetPc || !targetPc.IsAlive() || targetPc.Data == null || targetPc.Data.Disconnected) return;
        if (targetPc.PlayerId == holderId) return;

        CustomRoles slot = state.Slot;

        if (slotNumber1Based > 0)
        {
            // 範囲外は文書検証 (1..18) で既に落ちているが、Slots の長さ変更に備えて実行時も守る。
            if (slotNumber1Based > Slots.Length) return;

            CustomRoles named = Slots[slotNumber1Based - 1];

            // Wave 5 §2: ロビーで未束縛のスロットへの変換は無音 no-op (予算不消費)。定義が無いスロットへ
            // 変換すると「何のロジックも持たない役職」になってしまうため。
            if (!IsBound(named)) return;

            slot = named;
        }

        if (targetPc.GetCustomRole() == slot) return; // 既に同スロット — 変換総数はプレイヤー数で自然有界 (§4)

        // 勧誘者自身が消えていたら変換しない (下の Init() 不変条件を切断エッジでも守る防波堤)。
        if (!holderId.GetPlayer()) return;

        // 🔴 会議明けの「役職ジャグリング窓」(AntiBlackout.SkipTasks) も no-op に含める (契約 §4 の意図)。
        // 共通の meetingOrExile ゲート (EkrLogicOpcodes.cs) は IsMeeting か ExileController.Instance しか
        // 見ないが、ExileController は WrapUp 完了で先に消えるのに SkipTasks は RevertToActualRoleTypes
        // (WrapUp の +2秒後) まで真のまま残る — この窓だけ recruit がすり抜ける。素通りさせると
        // RpcSetCustomRole は即時・RpcChangeRoleBasis だけ DelayBasisChange で最低1秒遅れて着弾し、
        // 2呼びが分裂する (§4「RpcChangeRoleBasis の会議/追放中コルーチン遅延に仕事をさせない」の破れ)。
        if (AntiBlackout.SkipTasks) return;

        // レートゲート (§5): 超過は静かにドロップ。スタンプは変換が実際に進むときだけ更新する
        // (ゲートで落ちた呼び出しが枠を消費しない側)。
        float now = Time.realtimeSinceStartup;
        if (state.LastRecruitTime >= 0f && now - state.LastRecruitTime < RecruitPerHolderInterval) return;
        if (_lastGlobalRecruitTime >= 0f && now - _lastGlobalRecruitTime < RecruitGlobalInterval) return;

        state.LastRecruitTime = now;
        _lastGlobalRecruitTime = now;

        // §4: 変換 = 既存2呼びの固定順。RpcSetCustomRole は**インスタンス拡張** (ExtendedPlayerControl.cs
        // の extension — static byte 版はホスト状態を書かないので使用禁止) → RpcChangeRoleBasis (変換前の
        // RoleMap を読んで旧基底を解決するため順序逆転禁止 — Jackal/Necromancer/ChatCommandPatch の3前例と
        // 同順)。変換後の追加 Utils.NotifyRoles は呼ばない — SetMainRole が内蔵の NotifyRoles ペアを既に
        // 発行する (§5 二重払い禁止・Jackal 型の「SetMainRole に任せる」側)。
        //
        // 🔴 不変条件 (2026-08-27 Wave 5 で改訂)。
        //
        // Wave 4 までは「recruit は `if (!role.RoleExist(true)) Role.Init()` (GameState.SetMainRole) の
        // ResetSlot mid-game 罠を構造的に踏まない」と書いてあった (変換先が常に勧誘者自身のスロットで
        // RoleExist(true) が真になるため)。**Wave 5 の slot 指名でこの前提は偽になった** — 保持者ゼロの
        // スロットを指名した変換は mid-game で Role.Init() → EkrManager.ResetSlot を走らせる初のケース。
        //
        // これは**仕様として受容**する: 前任者がいないスロットの per-slot 状態 (変数・saved・marker・
        // リンク・CNO・crowd-control 帰属・持続効果) が新品で始まるのは正しい挙動。過去に保持者がいて
        // 全員死んだスロットは RoleExist(true) (countDead) が真のままなので Init() は走らず、死んだ前任者
        // 時代の状態が残る — これも現行意味論の踏襲 (変えない)。
        //
        // ⚠️ したがって ResetSlot 側は「mid-game に呼ばれうる」前提で書くこと (無条件の全体クリアを
        // 足さない — ClearEffectsForSlot / ccShouldClear の帰属判定がその実装)。
        // 上の GetPlayer() ガードは勧誘者切断の同フレーム競合だけを塞ぐ。
        targetPc.RpcSetCustomRole(slot);
        targetPc.RpcChangeRoleBasis(slot);

        Logger.Info($"EKR recruit: {targetPc.GetRealName()} => {slot} (by holder {holderId})", "EkrManager");
    }


    // ── Wave 5: 持続効果エンジン ─────────────────────────────────
    //
    // effect_give は「相手に一定時間だけ効く状態」を付ける op。対象は EKR ホルダーとは限らないので、
    // 台帳は EkrHolderState ではなく **per-target の static テーブル** に持つ (キー = (targetId, channel))。
    //
    // チャンネルは2本だけ (§1.2): movement (haste/slow/freeze 共有) と vision (blind)。同じチャンネルへの
    // 再適用は**後勝ち上書き**でスタックしない — 期限も新しい効果のものになり、切れたら「素の値」へ戻る。
    // ホルダー跨ぎも同一チャンネル (別ホルダーの haste 中に freeze が来たら freeze が勝つ)。
    //
    // 送信面 (§1.3): 新しい送信種はゼロ。実費は対象1人の SyncSettings 再送 (MarkDirtySettings) だけで、
    // バニラ客にもそのまま効く既存経路に乗る。movement は Main.AllPlayerSpeed への書き込み、vision は
    // 書き込みすら無い宣言型 (PlayerGameOptionsSender が HasBlindEffect を読む) なので復元問題が構造的に無い。
    //
    // 期限管理は PollCnoTouchIfDue と同族の 0.25s Pump ライダー (専用の毎フレーム経路を作らない)。
    // 期限粒度 ±0.25s は仕様。

    internal const int EffectChannelMovement = 0;
    internal const int EffectChannelVision = 1;

    private sealed class EkrEffectEntry
    {
        public string Kind;
        public float EndAt;

        // movement のみ: 捕捉した素の速度と、自分が実際に書き込んだ値 (第3の writer 保護に使う)。
        public float Baseline;
        public float Written;

        public byte HolderId; // ログ用 (付与元)
    }

    private static readonly Dictionary<(byte TargetId, int Channel), EkrEffectEntry> Effects = [];

    // §1.4 予算: per-holder ≤1/2秒 + EKR 全体 ≤2/秒 (teleport 系と同じ2段構え・超過は静かにドロップ)。
    private const float EffectPerHolderInterval = 2f;
    private static readonly List<float> _recentEffectTimes = [];

    internal static bool TryConsumeEffectBudget(EkrHolderState state)
    {
        float now = Time.realtimeSinceStartup;

        if (state.LastEffectGiveTime >= 0f && now - state.LastEffectGiveTime < EffectPerHolderInterval) return false;

        _recentEffectTimes.RemoveAll(t => now - t >= 1f);
        if (_recentEffectTimes.Count >= 2) return false;

        state.LastEffectGiveTime = now;
        _recentEffectTimes.Add(now);
        return true;
    }

    // 設定同期の dirty マーク。🔴 AntiBlackout.SkipTasks 中は PlayerGameOptionsSender.SendOptionsArray が
    // 早期 return するのに、GameOptionsSender の送信ループは送信の成否に関わらず IsDirty を落とす
    // (GameOptionsSender.cs の "sender.IsDirty = false;" は SendGameOptionsAsync の外) — この窓で立てた
    // dirty は**無音で捨てられ、再送されない**。効果の適用/解除がホスト側だけ進んで対象クライアント
    // (バニラ客含む) に届かない desync になるため、窓が閉じるまで 1 秒間隔で再マークする。
    // 送信そのものは既存のバッチ+PacketRateGate 経路に乗るので、再マークの実費はフラグ1本だけ。
    // (TryRecruit が同じ窓を no-op で避けている側)
    private static void MarkSettingsDirty(PlayerControl pc, int retriesLeft = 5)
    {
        if (!pc) return;

        pc.MarkDirtySettings();

        if (!AntiBlackout.SkipTasks || retriesLeft <= 0) return;

        byte playerId = pc.PlayerId;

        LateTask.New(() =>
        {
            if (GameStates.IsEnded) return;

            PlayerControl retry = playerId.GetPlayer();
            if (retry) MarkSettingsDirty(retry, retriesLeft - 1);
        }, 1f, log: false);
    }

    // vision チャンネルの宣言型読み取りフック (PlayerGameOptionsSender が呼ぶ)。
    internal static bool HasBlindEffect(byte playerId)
    {
        return Effects.ContainsKey((playerId, EffectChannelVision));
    }

    // movement チャンネルが埋まっているか (passives.speedMult の baseline 捕捉を遅らせる判定に使う)。
    internal static bool HasMovementEffect(byte playerId)
    {
        return Effects.ContainsKey((playerId, EffectChannelMovement));
    }

    // §1.1 実効値 (固定・作者には開けない)。haste ×1.5 / slow ×0.5 / freeze = MinSpeed。
    private static float EffectSpeedValue(string kind, float baseline)
    {
        return kind switch
        {
            "haste" => baseline * 1.5f,
            "slow" => baseline * 0.5f,
            _ => Main.MinSpeed
        };
    }

    internal static void ApplyEffect(byte targetId, string kind, float seconds, byte holderId)
    {
        PlayerControl target = targetId.GetPlayer();
        if (!target || !target.IsAlive() || target.Data == null || target.Data.Disconnected) return;

        float endAt = Time.realtimeSinceStartup + seconds;

        if (kind == "blind")
        {
            Effects[(targetId, EffectChannelVision)] = new EkrEffectEntry { Kind = kind, EndAt = endAt, HolderId = holderId };
            MarkSettingsDirty(target);
            Logger.Info($"EKR effect: {target.GetRealName()} <= {kind} {seconds}s (by holder {holderId})", "EkrManager");
            return;
        }

        // movement: baseline を捕捉する前に、同じ Main.AllPlayerSpeed キーを持つ既存の書き手を畳む
        // (§1.2 後勝ち)。畳まないと「前の効果で歪んだ値」を素の速度として捕捉し、期限切れの復元で
        // 歪みが永続化する (一時速度ブーストの復元レースの系)。
        ClearEffect(targetId, EffectChannelMovement, "overwrite");
        FoldSpeedBoost(targetId);

        float current = Main.AllPlayerSpeed.GetValueOrDefault(targetId, Main.RealOptionsData.GetFloat(AmongUs.GameOptions.FloatOptionNames.PlayerSpeedMod));

        // 捕捉時に他役職が MinSpeed で凍結中だと、その凍結値を「本来の速度」として捕捉してしまい、
        // 復元で永久凍結固定になる (speed op と同じ罠・EkrLogicOpcodes.cs の捕捉側ガードと同型)。
        float baseline = Mathf.Approximately(current, Main.MinSpeed)
            ? Main.RealOptionsData.GetFloat(AmongUs.GameOptions.FloatOptionNames.PlayerSpeedMod)
            : current;

        float value = EffectSpeedValue(kind, baseline);

        Effects[(targetId, EffectChannelMovement)] = new EkrEffectEntry { Kind = kind, EndAt = endAt, Baseline = baseline, Written = value, HolderId = holderId };

        Main.AllPlayerSpeed[targetId] = value;
        MarkSettingsDirty(target);

        Logger.Info($"EKR effect: {target.GetRealName()} <= {kind} {seconds}s (by holder {holderId})", "EkrManager");
    }

    // 効果の解除 + 復元。解除は常に世界へ書き戻す (ゲーム終了時の掃除も次ラウンドの ResetSlot →
    // ClearEffectsForSlot が担うので、「復元しない解除」の呼び出し口は存在しない)。
    private static void ClearEffect(byte targetId, int channel, string reason)
    {
        if (!Effects.Remove((targetId, channel), out EkrEffectEntry entry)) return;

        PlayerControl pc = targetId.GetPlayer();

        // 実機検証の計器 (付与側のログと対になる解除ログ) — 会議開始の一括解除が自然失効と
        // 区別できず実機で分離に失敗したため、解除の理由と時刻を残す。
        Logger.Info($"EKR effect clear: {(pc ? pc.GetRealName() : targetId.ToString())} => {entry.Kind} ({reason})", "EkrManager");

        if (channel == EffectChannelVision)
        {
            // 宣言型なので「テーブルから消して再送」だけ (§1.3 — 復元問題が構造的に無い)。
            MarkSettingsDirty(pc);
            return;
        }

        float current = Main.AllPlayerSpeed.GetValueOrDefault(targetId, entry.Written);

        // 第3の writer 保護 (§1.3): 自分が書いた値のままでなければ、既に誰かが上書きしている —
        // 触ると相手の効果を巻き戻してしまうので何もしない。
        if (!Mathf.Approximately(current, entry.Written)) return;

        Main.AllPlayerSpeed[targetId] = entry.Baseline;
        MarkSettingsDirty(pc);
    }

    // movement チャンネルの外部解除口 (speed op が同じキーへ書く前に呼ぶ — 後勝ちの対称側)。
    internal static void ClearMovementEffect(byte targetId)
    {
        ClearEffect(targetId, EffectChannelMovement, "speed-op");
    }

    // speed op のブースト (per-holder・EkrHolderState 側) を畳んで素の速度へ戻す。effect_give が同じ
    // Main.AllPlayerSpeed キーへ書く前に呼ぶ — 畳まないと speed の遅延復元が effect の値を踏み潰し、
    // その後 effect の復元が「第3の writer 保護」で降りて歪みが残る (2つの書き手が baseline を交換する事故)。
    private static void FoldSpeedBoost(byte playerId)
    {
        if (!Runtime.TryGetValue(playerId, out EkrHolderState state) || !state.SpeedBoostActive) return;

        state.SpeedGen++; // 進行中の遅延復元タスクを stale 化する (世代不一致で降りる)
        state.SpeedBoostActive = false;

        // 凍結中は触らない (speed op の復元側と同じ方針 — 相手の凍結を解除してしまう)。
        if (Mathf.Approximately(Main.AllPlayerSpeed.GetValueOrDefault(playerId), Main.MinSpeed)) return;

        Main.AllPlayerSpeed[playerId] = state.SpeedBaseline;
    }

    // スロット単位の解除 (§1.3 の解除タイミング④)。⚠️ **無条件の全解除にしないこと** — ResetSlot は
    // ゲーム中いつでも呼ばれうる (Wave 5 の recruit slot 指名で「保持者ゼロのスロットへの変換」が
    // mid-game Init() を走らせる初のケースになった)。無条件クリアだと無関係なホルダーが掛けた効果まで
    // 巻き添えで消える。帰属するものだけ断つ — _cc (crowd-control) の ccShouldClear と同じ非対称の解消で、
    // 「このスロットの保持者が付与元」に加えて「どのスロットの保持者でもなくなった孤児」も回収する
    // (ラウンド境界では前ラウンドの保持者が set に残っているので前者で通る)。
    internal static void ClearEffectsForSlot(CustomRoles slot)
    {
        if (Effects.Count == 0) return;

        PlayersBySlot.TryGetValue(slot, out HashSet<byte> mine);

        foreach ((byte TargetId, int Channel) key in Effects.Keys.ToArray())
        {
            if (!Effects.TryGetValue(key, out EkrEffectEntry entry)) continue;

            bool ownedBySomeSlot = false;

            foreach (HashSet<byte> owners in PlayersBySlot.Values)
            {
                if (!owners.Contains(entry.HolderId)) continue;

                ownedBySomeSlot = true;
                break;
            }

            if (ownedBySomeSlot && (mine == null || !mine.Contains(entry.HolderId))) continue;

            ClearEffect(key.TargetId, key.Channel, "slot");
        }
    }

    // 全効果の解除 (§1.3 の解除タイミング③④: 会議開始 / ゲーム終了・スロット束縛差し替え)。
    internal static void ClearAllEffects()
    {
        if (Effects.Count == 0) return;

        foreach ((byte targetId, int channel) in Effects.Keys.ToArray())
            ClearEffect(targetId, channel, "all");

        _recentEffectTimes.Clear();
    }

    // 0.25s Pump ライダー (PollCnoTouchIfDue と同型の自己スロットリング)。期限切れと、対象の
    // 死亡・切断 (§1.3 解除タイミング②) をここで回収する。付与元ホルダーの死は見ない — 効果は
    // 期限まで残る (§1.3・「死に際に相手を凍らせる」演出を成立させる)。
    private const float EffectPollInterval = 0.25f;
    private static float _lastEffectPollTime = -1f;

    private static void PollEffectsIfDue()
    {
        if (Effects.Count == 0) return;

        float now = Time.realtimeSinceStartup;
        if (_lastEffectPollTime >= 0f && now - _lastEffectPollTime < EffectPollInterval) return;
        _lastEffectPollTime = now;

        List<(byte TargetId, int Channel)> expired = null;

        foreach (KeyValuePair<(byte TargetId, int Channel), EkrEffectEntry> kv in Effects)
        {
            PlayerControl pc = kv.Key.TargetId.GetPlayer();
            bool gone = !pc || !pc.IsAlive() || pc.Data == null || pc.Data.Disconnected;

            if (!gone && kv.Value.EndAt > now) continue;

            (expired ??= []).Add(kv.Key);
        }

        if (expired == null) return;

        foreach ((byte targetId, int channel) in expired) ClearEffect(targetId, channel, "poll");
    }

    // ── v1.3: crowd-control エンジン (drag/field の共有枠・spec §3,§5) ──────────────────────────
    // EKR 全体で同時1本 (drag/field 合算)。所有 fiber とは切り離したエンジン側の単一スロットで、
    // 「tick 間隔・per-tick TP 上限 (field のみ 5人・ラウンドロビン)・発動あたり TP 総予算
    // (drag≤55/field≤45)」の3点セットを SuperCannonShot.PullTick から移植する。tick の TP は fiber 側
    // teleport の EKR 全体 ≤2/秒予算とは別勘定 (このエンジン自身の3点セットが締める)。
    //
    // ⚠ tick 間隔とゲートは drag / field で**意図的に非対称** (2026-08-14 実機の体感)。
    //   drag = Penguin 型 (0.2秒 tick + ホルダー移動ゲート)。1回でホルダーの現在位置へ飛ばす型なので、
    //          「つかまれている」感を出すには追従頻度そのものが要る。
    //   field = 1.0秒 tick + 1.6u デッドゾーン据え置き。per-tick 5人を 5Hz で撃つと 25/s = 約250本/10秒窓
    //          (公式鯖の SnapTo 本数予算 ≒358本/10秒窓の70%) で致死域に触れる。加えて field は段階引き寄せ
    //          なので、1.6u デッドゾーンが None 降格の空撃ち回避として効いている
    //          (閾値未満の TP は移動量ゼロの None に降格されるのに cap だけ消費するため)。
    //   ⇒ ここは意図的な非対称なので、field / SuperCannonShot.PullTick へ 0.2秒を
    //     横展開しないこと。

    private sealed class EkrCrowdControlState
    {
        public byte HolderId;
        public byte CtxId = byte.MaxValue; // drag のみ使用。field は対象を毎 tick 半径で都度決めるので不要。
        public bool IsField;
        public float EndAt;
        public int Spent;
        public int Budget;
        public int Rotation; // field のみ: ラウンドロビン公平化 (PullTick と同型)

        // drag のみ: 前回 snap した時点のホルダー位置 (Penguin.LastDragSnapPos と同型)。番兵は「初回 tick を
        // 無条件で発火させる」ため遠方に置く (= seconds:1 でも必ず1発打つ)。
        public Vector2 LastDragSnapPos = new(-9999f, -9999f);

        // field のみ
        public IEkrSlotCno FieldCno;
        public float Radius;
        public float PullDistance;
    }

    private static EkrCrowdControlState _cc;

    // StopCrowdControl の遅延 Despawn 待ちの field 実体。CountLiveCno はこれも数える —
    // 「実在するのに数えない」過小カウント側 (≤10 上限にとって危険側) に振れないための参照保持
    // (DespawnDummySlots の pending 台帳保持と同じ方針)。
    //
    // ⚠ 単一スロットではなくリスト。crowd-control 自体は同時1本だが、遅延窓 (1秒) の中で
    // 「A 停止 → B 起動 → B 停止」が連鎖しうる (CanOccupyCnoSlot は pending も数えるので B の spawn は通る)。
    // 単一 static だと後着の B が A を上書きし、A が二度と Despawn されない孤児 CNO になる
    // (= ≤10 上限が 1 体ずつ静かに狭まる片方向リーク)。
    private static readonly List<IEkrSlotCno> _ccPendingDespawn = [];

    private const float CcTickInterval = 1f;             // field の tick 間隔
    private const float CcDragTickInterval = 0.2f;       // drag の tick 間隔 (~5/s = Penguin.DragSnapInterval と同値)
    private const float CcDragHolderMoveGate = 0.3f;     // drag: ホルダーがこれ未満しか動いていない tick は撃たない (Penguin と同値)
    private const float CcDeadzone = 1.6f; // spec §5: field の最短ゲート (下回る tick はスキップ・予算不消費)
    private const int CcFieldPerTickCap = 5;
    private const int CcDragBudget = 55;   // 0.2秒 tick × 最長10秒 = 50 + 余裕
    private const int CcFieldBudget = 45;

    private static float _lastCcTickTime = -1f;

    // 起動を認める共有 SnapTo 残量の下限。Utils.TP は 80..99 帯でも true を返す (SendOption.None へ降格する
    // だけ) ため、枯渇間際に始めた drag/field は「予算は減るのに客へ確実には届かない」空撃ちになり、加えて
    // 他役職の TP まで枯らす。EKR field(45) と SuperCannonShot
    // BlackHole(45) が同一ラウンドで加算されるケースの防波堤も兼ねる。
    // ⚠ 判定は「起動時のみ」— 稼働中に閾値へ達しても途中で畳まない。周期 TP を中断すると引き寄せ途中の
    // 位置で止まって効果が意味不明になるうえ、能力と CD は既に消費済みで取り返せない。
    // 稼働中の枯渇は Utils.TP 側の 100 到達 (false 返し = 予算不消費) が自然に受け止める。
    private const int CcMaxSnapToPressureToStart = 60;

    internal static bool SnapToBudgetAllowsCrowdControl()
        => GameStates.CurrentServerType != GameStates.ServerType.Vanilla || Utils.NumSnapToCallsThisRound < CcMaxSnapToPressureToStart;

    internal static bool IsCrowdControlActive => _cc != null;

    // 早期ガード (IsCrowdControlActive) と TryStartField 呼び出しの間に他の何かが割り込んで _cc が
    // 埋まった場合の後始末 (単一スレッド実行のこのコードベースでは通常到達しない防御的経路)。
    // 実体化前でも後でも孤児コルーチン防止方針 (spec §5) に従って回収する。
    internal static void RetryDespawnOrphanFieldCno(IEkrSlotCno cno)
    {
        if (cno.IsInstantiated) cno.Despawn();
        else RetryDespawnUninstantiated(cno, retriesLeft: 5);
    }

    // drag opcode から呼ぶ。稼働中なら静かにドロップ (spec §5「稼働中の新規起動は静かにドロップ」)。
    internal static bool TryStartDrag(byte holderId, byte ctxId, float seconds)
    {
        if (_cc != null) return false;
        if (!SnapToBudgetAllowsCrowdControl()) return false;

        // 前セッションの最終 tick 時刻を持ち越すと 1 発目が最大 1 秒遅れ、seconds:1 の drag/field が
        // 1 tick も打たずに終わる。開始直後に 1 発目を打つ。
        _lastCcTickTime = -1f;

        _cc = new EkrCrowdControlState
        {
            HolderId = holderId,
            CtxId = ctxId,
            IsField = false,
            EndAt = Time.realtimeSinceStartup + seconds,
            Budget = CcDragBudget
        };

        return true;
    }

    // field opcode から呼ぶ。fieldCno は呼び出し元 (EkrLogicOpcodes.Field) が CNO 生成系防御3点
    // (TryConsumeGlobalCnoSpawnBudget 課金・会議/追放中 no-op・全体≤10体) を通過させた後に渡す。
    // 稼働中なら静かにドロップ — その場合 fieldCno は呼び出し元が孤児コルーチン防止方針に従って
    // 回収すること (実体化前なら RetryDespawnUninstantiated 相当・実体化済みなら即 Despawn)。
    internal static bool TryStartField(byte holderId, IEkrSlotCno fieldCno, float radius, float pullDistance, float seconds)
    {
        if (_cc != null) return false;
        if (!SnapToBudgetAllowsCrowdControl()) return false;

        _lastCcTickTime = -1f; // 開始直後に 1 発目を打つ (TryStartDrag と同じ)

        _cc = new EkrCrowdControlState
        {
            HolderId = holderId,
            IsField = true,
            EndAt = Time.realtimeSinceStartup + seconds,
            Budget = CcFieldBudget,
            FieldCno = fieldCno,
            Radius = radius,
            PullDistance = pullDistance
        };

        return true;
    }

    // 会議開始 (追放演出突入含む)・ホルダー/ctx の死亡切断・持続終了のいずれかから呼ぶ (spec §3,§5)。
    // tick 停止 (_cc = null) は同期で行うが、CNO の実 Despawn は 1 秒遅延 — 呼び出し元の 1 つが
    // FireMeetingStart (= PlayerControlPatch.AfterReportTasks の同期コールスタック) で、そこに
    // Object.Destroy/RemoveNetObject を乗せない規約 (DespawnDummySlots と同じ・上のコメント参照) を
    // 全呼び出し元へ一律適用する (経路ごとに分けると会議経路だけ漏れる)。
    private static void StopCrowdControl()
    {
        if (_cc == null) return;

        EkrCrowdControlState cc = _cc;
        _cc = null;

        if (cc.IsField && cc.FieldCno != null)
        {
            // クロージャは static ではなくこのローカルを掴む (static を読み直すと後着の停止で上書きされ、
            // 先着の実体が孤児化する — 上の _ccPendingDespawn のコメント参照)。
            IEkrSlotCno pending = cc.FieldCno;
            _ccPendingDespawn.Add(pending);

            LateTask.New(() =>
            {
                _ccPendingDespawn.Remove(pending);

                // spec §5: 実体化前に持続終了した pending は遅延 Despawn で回収 (孤児コルーチン既知型・
                // TeardownRuntime の CnoSlots 回収と同じ方針)。retry へ渡した後は既存の受容残差
                // (「teardown-while-pending の孤児1体は10体カウント外」) と同じ扱い。
                if (pending.IsInstantiated) pending.Despawn();
                else RetryDespawnUninstantiated(pending, retriesLeft: 5);
            }, 1f, "EKR-CC-FieldDespawn");
        }
    }

    // Pump() から毎 FixedUpdate 相乗りで呼ばれる (自己スロットリング — spec §5「専用の毎フレーム経路を
    // 作らない」)。会議中/追放演出中は即停止 (FireMeetingStart の明示停止と二重防御・タイミング競合対策)。
    private static void PumpCrowdControlIfDue()
    {
        if (_cc == null) return;

        // 同ファイルの他の遅延処理 (RetryDespawnUninstantiated / RetryRestoreSpeed) と同じ規約 —
        // 勝利演出中に TP tick を続けない。
        if (GameStates.IsEnded)
        {
            StopCrowdControl();
            return;
        }

        if (GameStates.IsMeeting || ExileController.Instance)
        {
            StopCrowdControl();
            return;
        }

        float now = Time.realtimeSinceStartup;

        if (now >= _cc.EndAt)
        {
            StopCrowdControl();
            return;
        }

        // tick 間隔は drag / field で非対称 (上のブロックコメント参照)。稼働は同時1本なので単一の
        // _lastCcTickTime で足りる (起動時に -1f リセット済み = セッション跨ぎの混線なし)。
        float tickInterval = _cc.IsField ? CcTickInterval : CcDragTickInterval;
        if (_lastCcTickTime >= 0f && now - _lastCcTickTime < tickInterval) return;
        _lastCcTickTime = now;

        PlayerControl holderPc = _cc.HolderId.GetPlayer();
        if (!holderPc || !holderPc.IsAlive())
        {
            StopCrowdControl();
            return;
        }

        if (_cc.IsField) TickField(_cc);
        else TickDrag(_cc, holderPc);
    }

    // drag: 発火時の ctx を毎 tick ホルダーの現在位置へ TP する (Penguin 型・spec §3)。部分移動ではなく
    // 1回で現在位置まで飛ばす — SuperCannonShot.PullTick のような段階的な引き寄せではない点が field との違い。
    private static void TickDrag(EkrCrowdControlState cc, PlayerControl holderPc)
    {
        PlayerControl ctxPc = cc.CtxId.GetPlayer();
        if (!ctxPc || !ctxPc.IsAlive())
        {
            StopCrowdControl();
            return;
        }

        if (cc.Spent >= cc.Budget) return; // 予算超過は静かにドロップ (稼働自体は seconds 経過まで維持)

        Vector2 dest = holderPc.Pos();
        Vector2 from = ctxPc.Pos();

        // ゲートは「対象との距離」ではなく「ホルダーが前回 snap から動いた量」で持つ (Penguin.cs:406 と同型)。
        // 0.2秒 tick では ctx は常にホルダーの至近にいるので、距離デッドゾーン (field の CcDeadzone) を
        // 使うと全 tick が空振りして引きずりが成立しない (2026-08-14 実機: 10秒アームで TP 5発だけ)。
        // ホルダーが止まっている間は撃たない = 送信量は「ホルダーが実際に歩いた分」に比例する。
        // ⚠ 至近距離の TP は Utils.TP 内で SendOption.None へ降格する (非モッド客への到達はベストエフォート)。
        //   これは Penguin のドラッグと同じ挙動で、ペンギン並みの追従感を採る (2026-08-14)。
        if (Vector2.Distance(dest, cc.LastDragSnapPos) < CcDragHolderMoveGate) return; // 予算不消費
        cc.LastDragSnapPos = dest;

        // 壁越えは引かない (TickField / SuperCannonShot.PullTick と同じ方針 — 壁内へ埋め込むと非モッドが
        // スタックする)。着地点はホルダーの現在位置なので通常は歩ける場所だが、ホルダーが直前に vent や
        // 移動床で飛んだ直後のフレームでは経路が壁を貫きうる。3兄弟で1つだけ防御が欠けていた。
        // レイは足元空間で撃つ — Pos() は見た目の中心で足元より約0.36u上にあり、そのままだと壁の上下端
        // 0.36u 帯で誤判定する (歩ける経路のブロック / 足元では塞がる壁のすり抜け両方向)。
        Vector2 rayOff = ctxPc.WallRayOffset();
        if (PhysicsHelpers.AnythingBetween(from + rayOff, dest + rayOff, Constants.ShipOnlyMask, false)) return;

        if (Utils.TP(ctxPc.NetTransform, dest, minInterval: 0f)) // 成功時のみ消費 (spec §5)
        {
            cc.Spent++;
            PrelatchTouchSensorsNear(ctxPc.PlayerId, dest); // spec §3 意味論: drag/field の tick TP にも適用
        }
    }

    // field: 中心 (フィールド実体) の半径内にいる生存プレイヤーを 1.0秒 tick で中心へ部分的に引き寄せる
    // (SuperCannonShot.PullTick 移植・spec §3,§5)。ホルダー自身は対象外。per-tick 上限5人・ラウンドロビン公平化。
    private static void TickField(EkrCrowdControlState cc)
    {
        if (cc.FieldCno is not CustomNetObject fieldCno || !fieldCno.playerControl) return; // 実体化前は何もしない

        Vector2 center = fieldCno.Position;

        var candidates = new List<PlayerControl>();

        // 毎秒ループなので yield 版 (Main.EnumerateAlivePlayerControls) は使わない —
        // ネスト管理 IEnumerator は呼び出し毎に strong GCHandle を残す。
        // 同ファイルの PollCnoTouchIfDue / PrimeTouchSensor と同じくキャッシュ済みリストを使う。
        foreach (PlayerControl pc in Main.AllAlivePlayerControls)
        {
            if (pc.PlayerId == cc.HolderId) continue; // ホルダー自身は引き寄せ対象外 (spec §3)
            if (Vector2.Distance(pc.Pos(), center) <= cc.Radius) candidates.Add(pc);
        }

        if (candidates.Count == 0) return;

        int pulled = 0;

        for (int i = 0; i < candidates.Count && pulled < CcFieldPerTickCap && cc.Spent < cc.Budget; i++)
        {
            PlayerControl pc = candidates[(cc.Rotation + i) % candidates.Count];

            Vector2 pos = pc.Pos();
            float dist = Vector2.Distance(pos, center);
            if (dist < CcDeadzone) continue; // 予算不消費 (None降格既知型回避)

            // spec §5 の「引き寄せ TP は全段 1.5u 超」は実際に TP する移動量 (step) への保証 —
            // dist ∈ [1.6, 2.3) では min() が dist-0.8 (<1.5) 側を選び None 降格の空撃ちになるため、
            // 下限 1.6u (安全マージン込み) でクランプする。step ≤ dist なのでオーバーシュートはしない
            // (dist=1.6 なら中心ちょうどに着地 → 次 tick は deadzone スキップで収束)。
            float step = Mathf.Max(Mathf.Min(cc.PullDistance, dist - (CcDeadzone / 2f)), CcDeadzone);
            Vector2 newPos = pos + ((center - pos).normalized * step);

            // 壁越えは引かない (PullTick と同じ方針 — 壁内へ埋め込むと非モッドがスタックする)。
            // レイは足元空間で撃つ — 移植元 PullTick は GetTruePosition() で正しかったのに移植時に
            // Pos() (見た目の中心) へ取り違えていた退行の修正 (2026-08-29)。TP 先 (newPos) の
            // 座標系は transform 空間のままにする (足元基準を渡すと 0.36u 沈む — 壁チェック=足元 / TP先=Pos())。
            Vector2 rayOff = pc.WallRayOffset();
            if (PhysicsHelpers.AnythingBetween(pos + rayOff, newPos + rayOff, Constants.ShipOnlyMask, false)) continue;

            if (Utils.TP(pc.NetTransform, newPos, minInterval: 0f)) // 成功時のみ消費 (spec §5)
            {
                cc.Spent++;
                pulled++;
                PrelatchTouchSensorsNear(pc.PlayerId, newPos);
            }
        }

        cc.Rotation = (cc.Rotation + CcFieldPerTickCap) % candidates.Count;
    }

    // ── Wave 6: 飛行エンジン (cno_launch) ────────────────────────
    // crowd-control (_cc) と同族の「fiber 外 static + Pump 相乗り + 即時停止」3点セット。ただし
    // crowd-control が**プレイヤーを TP する** (= SnapTo ラウンド予算を消費する) のに対し、こちらは
    // **CNO を動かすだけ**なので予算次元がまったく別 — 送信は CustomNetObject の
    // StartRpcImmediately + SendOption.None 直書き経路 (CustomNetObject.cs:557-589) で、
    // Utils.NumSnapToCallsThisRound を一切消費しない (契約 §7)。実費は位置 update 5Hz × ≤2本 = ≤10 msg/s。
    //
    // ⚠️ 「飛行台帳」は CnoSlots の**別勘定ではない** — 弾は launch 後も CnoSlots に居座り続ける
    // (CountLiveCno が数える側に残す)。この台帳はパラメータ (向き/速さ/飛距離/寿命) だけを持つ。
    // 二重台帳にすると _ccPendingDespawn のコメントが警告する「実在するのに数えない」過小カウントを
    // 作り直すことになる (契約 §7)。

    private sealed class EkrFlightState
    {
        public byte HolderId;
        public EkrCno Cno;
        public int SlotIndex; // 0-based (CnoSlots / TouchLatched の index)

        public Vector2 Dir;   // 正規化済み。launch 時に1回だけ確定し、以後は追尾しない (契約 §1.1)
        public float Speed;   // u/s (tier 実値)
        public float Travelled;

        // 実体化待ち (pending) の間は動かさず、実体化してから飛び始める。契約 §1.1 は
        // 「cno_spawn(at:self) → cno_launch の2ブロック」を基本形と定めており、同一 fiber の直後は
        // まだ spawn コルーチンが走っていない (DataFlagRateLimiter のキュー待ち) ため、pending を
        // no-op にすると基本形そのものが動かない。
        public bool Moving;

        // Moving 中 = 飛行寿命 (10秒)。pending 中 = 実体化待ちの打ち切り (5秒)。
        public float ExpireAt;

        // launch 時にホルダーが生きていたか。契約 §8 は「ホルダー死亡で飛行中の弾は消える」(#11) と
        // 「on_death 起点 fiber から cno_launch できる」(#11) の両方を凍結しているため、死亡を無条件の
        // 中断条件にすると後者が到達不能になる。launch 時点の生死をラッチして両立させる
        // (死んでから撃った弾は「その死」では止まらない)。
        public bool AbortOnHolderDeath;
    }

    private static readonly List<EkrFlightState> _flights = [];

    private const float FlightTickInterval = 0.1f;      // 契約 §1.1 (送信は基底が 0.2秒へ間引く)
    private const float FlightMaxDistance = 40f;        // 契約 §1.1 消滅②
    private const float FlightLifetimeSeconds = 10f;    // 契約 §1.1 消滅③
    private const float FlightPendingTimeoutSeconds = 5f; // 実体化待ちの打ち切り (飛行枠を明け渡す)
    private const int MaxGlobalFlights = 2;             // 契約 §1.2 (per-holder は 1)

    private static float _lastFlightTickTime = -1f;
    private static float _lastGlobalLaunchTime = -1f;

    // 契約 §1.1: 速度 tier の実値 (数値は作者に開けない)。medium = Snowdown.SnowballThrowSpeed と同値。
    internal static float FlightSpeedFor(string tier)
    {
        return tier switch { "slow" => 2f, "fast" => 6f, _ => 4f };
    }

    // 契約 §1.2: EKR 全体 ≤2/秒 (effect_give の全体バケットと同じ2段構えの外側)。
    internal static bool TryConsumeGlobalLaunchBudget()
    {
        float now = Time.realtimeSinceStartup;
        if (_lastGlobalLaunchTime >= 0f && now - _lastGlobalLaunchTime < 0.5f) return false;

        _lastGlobalLaunchTime = now;
        return true;
    }

    // 契約 §1.2: 同時飛行 EKR 全体 ≤2・per-holder ≤1。母数は飛行台帳そのもの (AllObjects は数えない)。
    internal static bool CanStartFlight(byte holderId)
    {
        if (_flights.Count >= MaxGlobalFlights) return false;

        foreach (EkrFlightState f in _flights)
            if (f.HolderId == holderId)
                return false;

        return true;
    }

    // cno_launch opcode から呼ぶ。呼び出し元が「slot に EkrCno が居る・dir が解決できた・レート予算を
    // 通過した」を確認済みであること。dir は正規化済みの非ゼロベクトル。
    internal static void StartFlight(byte holderId, EkrCno cno, int slotNumber1Based, Vector2 dir, string speedTier, bool holderAlive)
    {
        // 台帳が空だった = 前回の tick 時刻は「前の弾が飛んでいた頃」のもの。持ち越すと最初の tick の
        // Δt が「前の飛行が終わってから今までの実時間」になり、弾が1フレームでマップ外まで吹き飛ぶ
        // (crowd-control が TryStartDrag/TryStartField で _lastCcTickTime を -1 に戻すのと同じ理由)。
        if (_flights.Count == 0) _lastFlightTickTime = -1f;

        _flights.Add(new EkrFlightState
        {
            HolderId = holderId,
            Cno = cno,
            SlotIndex = slotNumber1Based - 1,
            Dir = dir,
            Speed = FlightSpeedFor(speedTier),
            ExpireAt = Time.realtimeSinceStartup + FlightPendingTimeoutSeconds,
            AbortOnHolderDeath = holderAlive
        });
    }

    // 飛行の終了 (壁/距離/寿命) と中断 (会議・死亡・剥奪) の共通後始末。飛び始めた弾は必ず消える
    // (契約 §1.1「弾は消えるのが自然」) — 実体化待ちのまま終わったものは「まだ飛んでいない置きっぱなしの
    // CNO」なので slot に残す (Launched が立っていないので基底の会議明け復活エンジンにもそのまま乗る)。
    // Wave 6 実機検証用の計器 (2026-08-29)。EKR の発火はログに出ないため、飛行の開始/実体化/終了理由が
    // 外から一切見えず「create と despawn が同じ秒に出る」以上のことが分からなかった。ログ1行で
    // 「どこで終わったか」を二値判定できるようにする (送信ゼロ・per-flight で最大数行/発)。
    internal static void FlightLog(string line) => Logger.Info(line, "EKR.Flight");

    private static void EndFlight(EkrFlightState flight, string reason = "?")
    {
        FlightLog($"end slot={flight.SlotIndex + 1} holder={flight.HolderId} reason={reason} moving={flight.Moving} travelled={flight.Travelled:F2} dir=({flight.Dir.x:F2},{flight.Dir.y:F2}) speed={flight.Speed}");

        _flights.Remove(flight);

        if (!flight.Moving) return;

        // slot 台帳がまだこの実体を指しているときだけ片付ける。作者が on_cno_touch → cno_despawn を
        // 組んでいた場合は既に slot から外れて Despawn 済みなので、ここは何もしない。
        if (!Runtime.TryGetValue(flight.HolderId, out EkrHolderState state) ||
            !ReferenceEquals(state.CnoSlots[flight.SlotIndex], flight.Cno)) return;

        if (flight.Cno.IsInstantiated)
        {
            ReleaseCnoSlot(state, flight.SlotIndex + 1);
            return;
        }

        // ここへ来る = 飛び始めた (= 一度は実体化した) のに今は未実体化 → 別経路が既に Despawn 済み
        // (基底の会議一斉 OnMeeting が先に走った等)。ReleaseCnoSlot は「pending は触らない」規約で
        // 早期 return するため、そのままだと slot が永久占有になり全体 ≤10 の導出カウントが 1 体ずつ
        // 静かに狭まる (_ccPendingDespawn のコメントが警告している片方向リークと同型)。台帳だけ外す。
        state.CnoSlots[flight.SlotIndex] = null;
        state.TouchLatched[flight.SlotIndex].Clear();
        state.TouchLastFireTime[flight.SlotIndex].Clear();
    }

    // 会議開始 (追放演出突入含む)・ゲーム終了・ランタイム破棄から呼ぶ (契約 §1.1 中断)。
    internal static void StopAllFlights()
    {
        for (int i = _flights.Count - 1; i >= 0; i--) EndFlight(_flights[i], "stop-all");
    }

    // ホルダーの切断・役職剥奪から呼ぶ (TeardownRuntime)。死亡は launch 時の生死ラッチを見る必要が
    // あるので、この関数ではなく PumpFlightsIfDue の tick 側が撃つ (FireDeath のコメント参照)。
    private static void StopFlightsForHolder(byte holderId)
    {
        for (int i = _flights.Count - 1; i >= 0; i--)
            if (_flights[i].HolderId == holderId)
                EndFlight(_flights[i], "holder-teardown");
    }

    // Pump() から毎 FixedUpdate 相乗りで呼ばれる (自己スロットリング 0.1秒 — 専用の毎フレーム経路を
    // 作らない spec §5)。PumpCrowdControlIfDue と同じ中断3点 (終了演出/会議・追放演出) を先に見る。
    private static void PumpFlightsIfDue()
    {
        if (_flights.Count == 0) return;

        if (GameStates.IsEnded || GameStates.IsMeeting || ExileController.Instance || AntiBlackout.SkipTasks)
        {
            StopAllFlights();
            return;
        }

        float now = Time.realtimeSinceStartup;
        if (_lastFlightTickTime >= 0f && now - _lastFlightTickTime < FlightTickInterval) return;

        // フレーム落ち・ロード停止で Δt が跳ねても弾がワープしないよう上限を切る (壁判定は線分なので
        // すり抜けはしないが、1 tick で数十 u 進むと「見えないまま消えた」になる)。
        float dt = _lastFlightTickTime < 0f ? FlightTickInterval : Mathf.Min(now - _lastFlightTickTime, 0.5f);
        _lastFlightTickTime = now;

        for (int i = _flights.Count - 1; i >= 0; i--)
        {
            EkrFlightState f = _flights[i];

            if (!Runtime.TryGetValue(f.HolderId, out EkrHolderState state) ||
                !ReferenceEquals(state.CnoSlots[f.SlotIndex], f.Cno))
            {
                // 台帳から外れた (作者が cno_despawn した / 束縛が消えた) — 弾はもう他所の管理下。
                _flights.RemoveAt(i);
                continue;
            }

            PlayerControl holderPc = f.HolderId.GetPlayer();

            if (!holderPc || holderPc.Data == null || holderPc.Data.Disconnected ||
                (f.AbortOnHolderDeath && !holderPc.IsAlive()))
            {
                EndFlight(f, "holder-gone");
                continue;
            }

            if (!f.Cno.IsInstantiated)
            {
                // 実体化待ち。打ち切り時刻を過ぎたら飛行枠を明け渡す (CNO は置きっぱなしとして slot に残る)。
                if (now >= f.ExpireAt) _flights.RemoveAt(i);
                continue;
            }

            if (!f.Moving)
            {
                f.Moving = true;
                f.ExpireAt = now + FlightLifetimeSeconds; // 寿命は「飛び始めてから」10秒 (契約 §1.1 消滅③)
                FlightLog($"moving slot={f.SlotIndex + 1} at=({f.Cno.Position.x:F2},{f.Cno.Position.y:F2}) dir=({f.Dir.x:F2},{f.Dir.y:F2}) speed={f.Speed}");

                // 🔴 実体化の立ち上がりで静止ポーラー (PollCnoTouchIfDue) がラッチを作り直すのを、
                // 飛び始める前に先回りして押さえる。PrimeTouchSensor は latch と debounce を **Clear** する
                // ので、飛行が始まった後に「今の弾の位置」で作り直されると
                //   ① 発射時ラッチ (射手の自己命中防止・契約 §1.1) が消える
                //   ② その瞬間たまたま弾の近くにいた人が「発火なしでラッチ済み」になり、最初の1人が無音で飲まれる
                // の2事故になる。ポーラー (0.25秒) と飛行 tick (0.1秒) は独立に自己スロットリングするので
                // 順序は保証されない = 実際に起こりうる競合。ここで撃つ Prime は弾がまだ動く前なので
                // 位置は spawn 時と同じ (cno_move 済みならその位置) = 意味は変わらない。
                PrimeTouchSensor(state, f.SlotIndex, f.Cno.Position, isPortal: false);
                state.TouchSensorWasLive[f.SlotIndex] = true;
            }
            else if (now >= f.ExpireAt)
            {
                EndFlight(f, "lifetime");
                continue;
            }

            Vector2 from = f.Cno.Position;
            float step = f.Speed * dt;
            Vector2 to = from + (f.Dir * step);

            // 壁で消える (契約 §1.1 消滅①)。⚠️ レイは **CNO の見た目位置ではなくコライダー空間**で撃つ —
            // 船コライダーは GetTruePosition (足元) 基準に敷かれているのに CNO.Position は pc.Pos()
            // (transform = 見た目の中心) 由来で約 0.36u 上にあり、そのまま撃つと通路の上壁を早取りして
            // 弾が即死する (2026-08-29 実機実測: 自分が直前に歩いて通った地点で travelled=0.00)。
            // 自分のコライダーを除外する overload を使うのは Sandbox / ForceFielder / Car と同じ作法
            // (ray が自身のコライダーに即ヒットするのを防ぐ)。
            Vector2 rayOffset = f.Cno.WallRayOffset;
            Vector2 rayFrom = from + rayOffset;
            Vector2 rayTo = to + rayOffset;
            Collider2D cnoCollider = f.Cno.SelfCollider;

            bool blocked = cnoCollider
                ? PhysicsHelpers.AnythingBetween(cnoCollider, rayFrom, rayTo, Constants.ShipOnlyMask, false)
                : PhysicsHelpers.AnythingBetween(rayFrom, rayTo, Constants.ShipOnlyMask, false);

            if (blocked)
            {
                FlightLog($"wall slot={f.SlotIndex + 1} from=({from.x:F2},{from.y:F2}) to=({to.x:F2},{to.y:F2}) ray=({rayFrom.x:F2},{rayFrom.y:F2})->({rayTo.x:F2},{rayTo.y:F2}) off=({rayOffset.x:F2},{rayOffset.y:F2}) step={step:F3}");
                EndFlight(f, "wall");
                continue;
            }

            f.Travelled += step;
            f.Cno.FlyTo(to);

            // 命中は既存 on_cno_touch へ合流 (契約 §1.1)。静止 CNO の 0.25秒ポーラーと同じラッチ/
            // デバウンス構造をそのまま使い、進入判定だけを点距離から**移動線分との距離**へ差し替える
            // (fast 6u/s は 0.25秒ポーリングだと 1.5u 進んですり抜けるため — トンネリング対策)。
            SweepCnoTouch(f, state, from, to, now);

            if (f.Travelled >= FlightMaxDistance) EndFlight(f, "distance"); // 消滅② 総飛距離
        }
    }

    // 飛行中の弾の掃引接触判定。PollCnoTouchIfDue の on_cno_touch 分岐と同じ状態 (TouchLatched /
    // TouchLastFireTime) を共有するので、静止ポーラーと二重発火しない (ラッチ済みは再発火しない)。
    private static void SweepCnoTouch(EkrFlightState flight, EkrHolderState state, Vector2 from, Vector2 to, float now)
    {
        int idx = flight.SlotIndex;
        HashSet<byte> latched = state.TouchLatched[idx];
        CustomRoles? holderSlot = null;

        // 毎 tick ループなので yield 版は使わない (ネスト管理 IEnumerator は呼び出し毎に strong GCHandle をリークする)。
        foreach (PlayerControl pc in Main.AllAlivePlayerControls)
        {
            Vector2 p = pc.Pos();
            bool inside = latched.Contains(pc.PlayerId);

            if (!inside)
            {
                if (DistanceToSegment(p, from, to) > TouchEnterRadius) continue;

                latched.Add(pc.PlayerId);

                float lastFire = state.TouchLastFireTime[idx].GetValueOrDefault(pc.PlayerId, -1f);
                if (lastFire >= 0f && now - lastFire < TouchDebounceSeconds) continue;
                state.TouchLastFireTime[idx][pc.PlayerId] = now;

                holderSlot ??= SlotForHolder(flight.HolderId);
                if (holderSlot.HasValue) FireCnoTouch(holderSlot.Value, flight.HolderId, idx + 1, pc.PlayerId);
            }
            else if (Vector2.Distance(p, to) >= TouchExitRadius)
            {
                // 退出は静止ポーラーと同じ「今の位置からの点距離」で判定する (貫通弾が通り過ぎたら
                // ラッチが外れ、次の弾/次の周回で再び当たれる)。
                latched.Remove(pc.PlayerId);
            }
        }
    }

    // 点と線分の距離。飛行 tick の掃引判定でしか使わないので Unity の汎用 API は使わず素で書く。
    private static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lenSq = ab.sqrMagnitude;
        if (lenSq < 0.000001f) return Vector2.Distance(point, a);

        float t = Mathf.Clamp01(Vector2.Dot(point - a, ab) / lenSq);
        return Vector2.Distance(point, a + (ab * t));
    }
}
