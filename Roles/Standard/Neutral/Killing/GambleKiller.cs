using System.Collections;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Modules;
using UnityEngine;

namespace EndKnot.Roles;

// ============================================================
// GambleKiller (ギャンブルキラー) — Neutral/Killing
//
// コンセプト:
//   キルするたびに SelfDestructChance (既定 50%) で自爆する。生き延びるたびに、まだ持っていない
//   能力をランダムに1つ獲得する。獲得を重ねるほど手が付けられなくなるが、その前に自分が消し飛ぶ。
//
// 陣営登録は ProbabilityKing (Id 707000) と完全に同じ場所に入れている
//   (IsNK / GetDYRole=Impostor / CountTypes / CustomWinner / Utils.HasTasks / RoleHtmlColors)。
//
// 付与能力は「doc が明示している 6 種」の有限プール。プールの中身自体は仕様確認待ちで、
// 増やす場合は Ability enum に足して GrantAbility の説明文とセットで追加すればよい。
//
// ⚠️ ダミーは会議で Despawn したら**張り直さない**。Atlas と DummySpawner が既に「会議明け10秒 +
//   順送り」の窓を共有しており、3 役職目を同じ窓に乗せると公式鯖の送信密度が未検証領域に入る
//   (BUG-20260803-07 の較正は単一 CNO 役職の負荷が前提)。こちらはキル/静止で自然に再取得される。
// ============================================================
public class GambleKiller : RoleBase
{
    private const int Id = 707100;

    /// <summary>付与されうる能力。doc 明示の 6 種。</summary>
    public enum Ability
    {
        SpeedUp,
        WideVision,
        Overkill,
        NoBody,
        DummyOnKill,
        DummyOnStop
    }

    private static readonly Ability[] AllAbilities = System.Enum.GetValues<Ability>();

    public static List<byte> PlayerIdList = [];

    private static OptionItem KillCooldown;
    private static OptionItem CanVent;
    private static OptionItem HasImpostorVision;
    private static OptionItem SelfDestructChance;
    private static OptionItem SpeedBonus;
    private static OptionItem MaxDummies;
    private static OptionItem StopTimeForDummy;

    private byte GambleKillerId = byte.MaxValue;

    /// <summary>獲得済みの能力。per-instance (RoleBase は保持者ごとに別インスタンス)。</summary>
    public readonly HashSet<Ability> Acquired = [];

    private readonly List<MimicDummy> Dummies = [];

    private Vector2 LastPosition;
    private float StopTimer;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        StartSetup(Id)
            .AutoSetupOption(ref KillCooldown, 25f, new FloatValueRule(0f, 180f, 0.5f), OptionFormat.Seconds)
            .AutoSetupOption(ref CanVent, true)
            .AutoSetupOption(ref HasImpostorVision, false)
            .AutoSetupOption(ref SelfDestructChance, 50, new IntegerValueRule(0, 100, 5), OptionFormat.Percent)
            .AutoSetupOption(ref SpeedBonus, 0.4f, new FloatValueRule(0.1f, 1f, 0.1f), OptionFormat.Multiplier)
            .AutoSetupOption(ref MaxDummies, 2, new IntegerValueRule(1, 5, 1), OptionFormat.Times)
            .AutoSetupOption(ref StopTimeForDummy, 8f, new FloatValueRule(1f, 30f, 1f), OptionFormat.Seconds);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        GambleKillerId = playerId;
        Acquired.Clear();
        Dummies.Clear();
        StopTimer = 0f;

        PlayerControl pc = Utils.GetPlayerById(playerId);
        LastPosition = pc ? pc.Pos() : Vector2.zero;
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
        DespawnAllDummies();
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    public override bool CanUseImpostorVentButton(PlayerControl pc)
    {
        return CanVent.GetBool();
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        // 視野は「能力で獲得したら広がる」。既定の HasImpostorVision は素の状態の設定。
        opt.SetVision(HasImpostorVision.GetBool() || Acquired.Contains(Ability.WideVision));
    }

    public override void OnMurder(PlayerControl killer, PlayerControl target)
    {
        if (target == null || target.PlayerId >= 200) return;

        // ── 獲得済み能力の発動 (自爆判定より先。自爆しても「そのキルの効果」は出る) ──
        if (Acquired.Contains(Ability.NoBody))
            Cleaner.CleanerBodies.Add(target.PlayerId);

        if (Acquired.Contains(Ability.Overkill))
            ApplyOverkill(target);

        if (Acquired.Contains(Ability.DummyOnKill) && CanPlaceDummy())
            PlaceDummy(target.Pos(), AtlasVictimSnapshot.CaptureFrom(target));

        // ── 自爆抽選 ──
        if (IRandom.Instance.Next(100) < SelfDestructChance.GetInt())
        {
            killer.Notify(Translator.GetString("GambleKillerBlewUp"));

            // ⚠️ DeathReason.Gambled は使わない。名前は合っているが実体は当て推量ゲーム用で
            //    (GuessManager が「役職を当てられて死んだ」に使う)、表示は "Guessed"/「推測された」、
            //    さらに Statistics が「正解数」として集計してしまう。
            //    能力発動での自滅は Misfire (誤爆) が既存慣習。
            killer.Suicide(PlayerState.DeathReason.Misfire);
            return;
        }

        GrantRandomAbility(killer);
    }

    /// <summary>生き延びた褒美。まだ持っていない能力を1つランダムに与える。</summary>
    private void GrantRandomAbility(PlayerControl pc)
    {
        Ability[] remaining = AllAbilities.Where(x => !Acquired.Contains(x)).ToArray();

        if (remaining.Length == 0)
        {
            pc.Notify(Translator.GetString("GambleKillerSurvivedNoMore"));
            return;
        }

        Ability granted = remaining[IRandom.Instance.Next(remaining.Length)];
        Acquired.Add(granted);

        switch (granted)
        {
            case Ability.SpeedUp:
                // ⚠️ 一発 write のみ。毎フレーム書くと他役職/アドオンの速度指定と last-writer-wins で潰し合う。
                // 上限 3.0 は vanilla の移動速度スライダー上限。青天井にすると当たり判定をすり抜ける
                if (Main.AllPlayerSpeed.ContainsKey(pc.PlayerId))
                    Main.AllPlayerSpeed[pc.PlayerId] = Mathf.Min(Main.AllPlayerSpeed[pc.PlayerId] + SpeedBonus.GetFloat(), 3f);

                pc.MarkDirtySettings();
                break;

            case Ability.WideVision:
                pc.MarkDirtySettings();
                break;
        }

        pc.Notify(string.Format(Translator.GetString("GambleKillerGained"), Translator.GetString($"GambleKiller.{granted}")));
        Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
    }

    /// <summary>Butcher と同じ「ばらばらの偽死体をばら撒く」処理。ペースは FakeBodyBurst の共有較正値。</summary>
    private static void ApplyOverkill(PlayerControl target)
    {
        if (!Main.IntroDestroyed || !GameStates.IsInTask || ExileController.Instance || AntiBlackout.SkipTasks) return;
        if (target.Is(CustomRoles.Disregarded)) return;

        Main.PlayerStates[target.PlayerId].deathReason = PlayerState.DeathReason.Dismembered;
        Vector2 ops = target.Pos();

        LateTask.New(() =>
        {
            if (!Butcher.ButcherDeadPlayerList.Contains(target.PlayerId))
                Butcher.ButcherDeadPlayerList.Add(target.PlayerId);

            Main.Instance.StartCoroutine(SpawnFakeDeadBodies());
            return;

            IEnumerator SpawnFakeDeadBodies()
            {
                var rd = IRandom.Instance;
                int count = FakeBodyBurst.BodyCount;
                FakeBodyBurst.LogBurst("GambleKiller", count);

                for (var i = 0; i < count; i++)
                {
                    if (GameStates.IsEnded || GameStates.IsMeeting) yield break;

                    Vector2 location = new(ops.x + ((float)(rd.Next(0, 201) - 100) / 100), ops.y + ((float)(rd.Next(0, 201) - 100) / 100));
                    location += new Vector2(0, 0.3636f);

                    Utils.RpcCreateDeadBody(location, (byte)target.CurrentOutfit.ColorId, target);

                    yield return new WaitForSecondsRealtime(FakeBodyBurst.CurrentSpacingSeconds);
                }
            }
        }, 0.05f, "GambleKiller Overkill");
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!Acquired.Contains(Ability.DummyOnStop)) return;
        if (!Main.IntroDestroyed || !GameStates.IsInTask || ExileController.Instance || AntiBlackout.SkipTasks || !pc.IsAlive()) return;

        Vector2 current = pc.Pos();

        // 0.1u 以上動いたら「移動中」とみなしてタイマーを畳む (床の微振動で誤判定しないための余裕)
        if (Vector2.Distance(current, LastPosition) > 0.1f)
        {
            LastPosition = current;
            StopTimer = 0f;
            return;
        }

        StopTimer += Time.fixedDeltaTime;
        if (StopTimer < StopTimeForDummy.GetFloat()) return;

        // 湧かせたら必ずタイマーを畳む — 立ち止まったままでも連続で湧かないようにする最低間隔になる
        StopTimer = 0f;

        if (!CanPlaceDummy()) return;

        PlaceDummy(current, AtlasVictimSnapshot.CaptureFrom(pc));
        pc.Notify(Translator.GetString("GambleKillerDummySummoned"));
    }

    /// <summary>生きているダミーだけを数え、死んだ参照は台帳から落とす。</summary>
    private bool CanPlaceDummy()
    {
        Dummies.RemoveAll(d => d == null || !d.playerControl);
        return Dummies.Count < MaxDummies.GetInt();
    }

    private void PlaceDummy(Vector2 position, AtlasVictimSnapshot snapshot)
    {
        if (GameStates.IsEnded || GameStates.IsMeeting) return;

        Dummies.Add(new MimicDummy(position, snapshot, GambleKillerId));
    }

    private void DespawnAllDummies()
    {
        Dummies.ToArray().Do(d => d?.Despawn());
        Dummies.Clear();
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        string acquired = Acquired.Count == 0
            ? Translator.GetString("GambleKillerNoAbilityYet")
            : string.Join(" ", Acquired.Select(x => Translator.GetString($"GambleKiller.{x}.Short")));

        return Utils.ColorString(Utils.GetRoleColor(CustomRoles.GambleKiller), $"({Acquired.Count}/{AllAbilities.Length}) {acquired}");
    }
}
