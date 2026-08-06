using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Modules;
using UnityEngine;

namespace EndKnot.Roles;

// ============================================================
// Torpedo (魚雷) — Neutral/Killing
//
// doc の「神風」。既存 Impostor 役職 `Kamikaze` (「一緒に死ぬプレイヤーをマークする」) と名前が
// 衝突するため改名した (仮採用・承認待ち)。
//
// ファントムボタンで特攻を開始し、加速したまま突っ込む。ぶつかったプレイヤーを爆破で巻き込み、
// 壁にぶつかると自爆する。特攻そのものにも自滅の確率がある。
// 特攻を成功させる (= 巻き込みキルを規定数決める) と単独勝利。
//
// ⚠️ **「高速ダッシュ」を小刻み TP の連続で実装してはいけない。**
//   fork の既決ポリシー: 「1回で大きく飛ばす」を優先し、引きずり型は距離が同じでも SnapTo cap の
//   消費が 20 倍。さらに 1.5u 未満の TP は None 降格で届かないのに cap だけ減る。cap を枯渇させると
//   **他役職の TP まで無音で死ぬ**。よってここでは位置を一切 TP せず、
//   `Main.AllPlayerSpeed` の一時的な引き上げ**だけ**で加速を出し、当たり判定は毎フレームの距離判定で行う。
//   → 送信は「開始/終了時の速度同期」と「命中時のキル」だけになり、位置同期は vanilla のまま。
// ============================================================
public class Torpedo : RoleBase
{
    private const int Id = 707300;

    // 壁判定: 「加速しているのに実際にはほとんど進んでいない」= 何かに突っかかっている
    private const float StuckDistancePerSecond = 0.6f;
    private const float StuckGraceSeconds = 0.35f;

    public static bool On;

    private static OptionItem SelfDestructChance;
    private static OptionItem DashCooldown;
    private static OptionItem BlastRadius;
    private static OptionItem SpeedMultiplier;
    private static OptionItem DashDuration;
    private static OptionItem KillsNeededToWin;

    private byte TorpedoId = byte.MaxValue;

    private bool Dashing;
    private float DashTimer;
    private float StuckTimer;
    private Vector2 LastPosition;
    private float OriginalSpeed;

    // ⚠️ 「捕捉できたか」を OriginalSpeed > 0f で判定してはいけない。Main.AllPlayerSpeed は
    //    負値を正規の状態として扱う (反転効果: Flash/Giant/Mare) ため、開始時の速度が 0 以下だと
    //    復元が丸ごとスキップされて**永久に高速のまま**になる。専用フラグで持つ。
    private bool SpeedCaptured;

    private int SuccessfulHits;

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        StartSetup(Id)
            .AutoSetupOption(ref DashCooldown, 45, new IntegerValueRule(0, 180, 5), OptionFormat.Seconds)
            .AutoSetupOption(ref DashDuration, 4f, new FloatValueRule(1f, 15f, 0.5f), OptionFormat.Seconds)
            .AutoSetupOption(ref SpeedMultiplier, 3f, new FloatValueRule(1.5f, 5f, 0.5f), OptionFormat.Multiplier)
            .AutoSetupOption(ref BlastRadius, 2f, new FloatValueRule(2f, 10f, 0.5f), OptionFormat.None)
            .AutoSetupOption(ref SelfDestructChance, 30, new IntegerValueRule(0, 100, 5), OptionFormat.Percent)
            .AutoSetupOption(ref KillsNeededToWin, 1, new IntegerValueRule(1, 5, 1), OptionFormat.Times);
    }

    public override void Init()
    {
        On = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
        TorpedoId = playerId;
        Dashing = false;
        DashTimer = 0f;
        StuckTimer = 0f;
        SuccessfulHits = 0;
        OriginalSpeed = 0f;
    }

    public override void Remove(byte playerId)
    {
        EndDash(Utils.GetPlayerById(playerId));
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        float cd = DashCooldown.GetFloat();

        // イントロ直後の PreventKill 窓では OnVanish が呼ばれず CD だけリセットされる (初回押下が無音で不発)。
        // 窓の長さは固定 10 秒ではなく Options.StartingKillCooldown なので、それに合わせてクランプする。
        if (IntroCutsceneDestroyPatch.PreventKill)
            cd = Mathf.Max(cd, (Options.StartingKillCooldown?.GetFloat() ?? 10f) + 2f);

        AURoleOptions.PhantomCooldown = cd;
    }

    /// <summary>いま他役職 (Electric / Chef など) に速度を凍結されているか。</summary>
    private static bool IsFrozenByOthers(PlayerControl pc)
    {
        return pc && Main.AllPlayerSpeed.TryGetValue(pc.PlayerId, out float speed) && Mathf.Approximately(speed, Main.MinSpeed);
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        hud.AbilityButton?.OverrideText(Translator.GetString("TorpedoAbilityButtonText"));
    }

    public override bool OnVanish(PlayerControl pc)
    {
        if (!pc || !pc.IsAlive() || Dashing || !GameStates.IsInTask) return false;

        // 特攻そのものの自滅抽選 (doc: 「特攻をすると確率で自分自身も死んでしまう」)
        if (IRandom.Instance.Next(100) < SelfDestructChance.GetInt())
        {
            pc.Notify(Translator.GetString("TorpedoMisfire"));

            // 能力発動が空振りしての自滅は Misfire (誤爆) が既存慣習
            // (Sharpshooter / Sheriff / Romantic / Hater / SchrodingersCat と同じ)。
            // Bombed は「実際に爆風/衝突を受けた側」に取っておく。
            pc.Suicide(PlayerState.DeathReason.Misfire);
            return false;
        }

        Dashing = true;
        DashTimer = DashDuration.GetFloat();
        StuckTimer = 0f;
        LastPosition = pc.Pos();

        if (Main.AllPlayerSpeed.TryGetValue(pc.PlayerId, out float speed))
        {
            OriginalSpeed = speed;
            SpeedCaptured = true;
            Main.AllPlayerSpeed[pc.PlayerId] = Mathf.Min(speed * SpeedMultiplier.GetFloat(), 3f);
            pc.MarkDirtySettings();
        }

        pc.Notify(Translator.GetString("TorpedoLaunched"));
        return false;
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!Dashing) return;

        if (!pc || !pc.IsAlive() || !GameStates.IsInTask || ExileController.Instance || AntiBlackout.SkipTasks)
        {
            EndDash(pc);
            return;
        }

        DashTimer -= Time.fixedDeltaTime;

        if (DashTimer <= 0f)
        {
            EndDash(pc);
            pc.Notify(Translator.GetString("TorpedoSpentOut"));
            return;
        }

        Vector2 current = pc.Pos();

        // ── 命中判定 ──
        float radius = BlastRadius.GetFloat();
        // ⚠️ `Suicide` は OnCheckMurder / RpcCheckAndMurder を通らないので、そのまま撃つと
        //    メディックのシールド・ベテランの反撃・各種護衛といった保護チェーンを全部貫通する。
        //    同じ「範囲を巻き込む」設計の Chemist の手榴弾 (Chemist.cs:389-391) は
        //    `RpcCheckAndMurder(x, true)` で先に濾しているので、それに揃える。
        List<PlayerControl> victims = Main.AllAlivePlayerControlsToList
            .Where(x => x.PlayerId != pc.PlayerId && !x.Data.Disconnected && FastVector2.DistanceWithinRange(current, x.Pos(), radius) && pc.RpcCheckAndMurder(x, true))
            .ToList();

        if (victims.Count > 0)
        {
            EndDash(pc);

            foreach (PlayerControl victim in victims)
                victim.Suicide(PlayerState.DeathReason.Bombed, pc);

            SuccessfulHits += victims.Count;
            pc.Notify(string.Format(Translator.GetString("TorpedoHit"), victims.Count));

            if (SuccessfulHits >= KillsNeededToWin.GetInt())
            {
                CustomWinnerHolder.ResetAndSetWinner(CustomWinner.Torpedo);
                CustomWinnerHolder.WinnerIds.Add(pc.PlayerId);
            }

            return;
        }

        // ── 壁判定 ──
        // 加速中なのに実質進んでいない = 何かに突っかかっている。1フレームの揺らぎで誤爆しないよう
        // StuckGraceSeconds ぶん連続したときだけ自爆させる。
        // ⚠️ 位置変化だけで判定すると、他役職の凍結 (Electric 等) で止められた場合まで
        //    「壁に激突」として自爆させてしまう (誤殺 + 誤メッセージ)。凍結中は判定を止める。
        if (IsFrozenByOthers(pc))
        {
            StuckTimer = 0f;
            LastPosition = current;
            return;
        }

        float moved = Vector2.Distance(current, LastPosition);
        LastPosition = current;

        if (moved < StuckDistancePerSecond * Time.fixedDeltaTime)
        {
            StuckTimer += Time.fixedDeltaTime;

            if (StuckTimer >= StuckGraceSeconds)
            {
                EndDash(pc);
                pc.Notify(Translator.GetString("TorpedoCrashed"));
                pc.Suicide(PlayerState.DeathReason.Bombed);
            }

            return;
        }

        StuckTimer = 0f;
    }

    /// <summary>加速を必ず元へ戻す。戻し忘れると永久に高速のままになる。</summary>
    private void EndDash(PlayerControl pc)
    {
        if (!Dashing) return;

        Dashing = false;
        DashTimer = 0f;
        StuckTimer = 0f;

        // ⚠️ いま他役職に凍結されている最中なら、絶対値で上書きして凍結を解除してはいけない。
        //    凍結側 (Electric / Chef) は「凍結前の値」として**ブースト済みの値**をスナップショット
        //    しているので、ここで先に戻すと相手の遅延タスクが後からブースト値を復元してしまい、
        //    Main.AllPlayerSpeed が高速のまま恒久固定される。凍結中は相手の復元に委ねる。
        if (pc && SpeedCaptured && Main.AllPlayerSpeed.ContainsKey(pc.PlayerId) && !IsFrozenByOthers(pc))
        {
            Main.AllPlayerSpeed[pc.PlayerId] = OriginalSpeed;
            pc.MarkDirtySettings();
        }

        SpeedCaptured = false;
        OriginalSpeed = 0f;
    }

    public override void OnReportDeadBody()
    {
        EndDash(Utils.GetPlayerById(TorpedoId));
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (seer.PlayerId != TorpedoId || seer.PlayerId != target.PlayerId || meeting || !Dashing) return string.Empty;

        return string.Format(Translator.GetString("TorpedoSuffix"), (int)System.Math.Ceiling(DashTimer));
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        return Utils.ColorString(Utils.GetRoleColor(CustomRoles.Torpedo), $"({SuccessfulHits}/{KillsNeededToWin.GetInt()})");
    }
}
