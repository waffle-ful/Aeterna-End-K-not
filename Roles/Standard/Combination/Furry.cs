using EndKnot.Modules;
using AmongUs.GameOptions;
using Hazel;

namespace EndKnot.Roles;

// コンビ役職の相方 (ファーリー)。ニュートラルだがクルーとして数える。desync Impostor 基底で、キルボタンは「捕まえる」。
// 1 ターンに 1 回だけ使え、誰も死なない。相手がケモノなら捕獲成功。
// ケモノを捕まえたまま生き残ってゲームが終わると、本来の勝者を押しのけて単独で勝つ。
// 出現率とオプションはケモノ (Kemono.cs) 側に集約している。
public class Furry : RoleBase
{
    public static bool On;
    public override bool IsEnable => On;

    // このターンの「捕まえる」を使い切ったか
    public static bool UsedThisTurn;

    public override void SetupCustomOption() { }

    public override void Init()
    {
        On = false;
        UsedThisTurn = false;
        Kemono.FurryId = byte.MaxValue;
    }

    public override void Add(byte playerId)
    {
        On = true;
        Kemono.FurryId = playerId;
    }

    public override void Remove(byte playerId)
    {
        if (Kemono.FurryId == playerId) Kemono.FurryId = byte.MaxValue;
        On = Kemono.FurryId != byte.MaxValue;
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        opt.SetVision(false);
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = Kemono.CaptureCooldown.GetFloat();
    }

    public override bool CanUseKillButton(PlayerControl pc)
    {
        return pc.IsAlive() && !UsedThisTurn;
    }

    public override bool CanUseSabotage(PlayerControl pc) => false;

    public override bool CanUseImpostorVentButton(PlayerControl pc) => false;

    public override bool OnCheckMurder(PlayerControl killer, PlayerControl target)
    {
        if (UsedThisTurn) return false;

        // CanUseKillButton() が false になると SetKillCooldown() は何もしないので、フラグを立てる前に長いクールダウンを入れる
        killer.SetKillCooldown(999f, target);
        UsedThisTurn = true;
        SendSync(killer.PlayerId);

        if (target.PlayerId == Kemono.KemonoId && target.Is(CustomRoles.Kemono))
        {
            Logger.Info($"{killer.GetNameWithRole().RemoveHtmlTags()} captured the Kemono", "Furry");
            Kemono.Capture();
            killer.Notify(Utils.ColorString(Utils.GetRoleColor(CustomRoles.Furry), Translator.GetString("FurryCaptureSuccess")), 8f);
        }
        else
        {
            Logger.Info($"{killer.GetNameWithRole().RemoveHtmlTags()} failed to capture ({target.GetNameWithRole().RemoveHtmlTags()})", "Furry");
            killer.Notify(Utils.ColorString(Utils.GetRoleColor(CustomRoles.Furry), Translator.GetString("FurryCaptureFail")), 6f);
            if (Kemono.FailureDies.GetBool()) killer.Suicide();
        }

        return false;
    }

    public override void AfterMeetingTasks()
    {
        Kemono.ApplyPendingFurryChange();

        if (!UsedThisTurn) return;

        UsedThisTurn = false;
        if (Kemono.FurryId == byte.MaxValue) return;

        // 捕まえた時の 999 秒が残らないよう、会議明けの汎用処理の順番に依らず戻す
        Main.AllPlayerKillCooldown[Kemono.FurryId] = Kemono.CaptureCooldown.GetFloat();
        SendSync(Kemono.FurryId);
    }

    private static void SendSync(byte furryId)
    {
        Utils.SendRPC(CustomRPC.SyncRoleData, furryId, UsedThisTurn);
    }

    public void ReceiveRPC(MessageReader reader)
    {
        UsedThisTurn = reader.ReadBoolean();
    }

    // ケモノを捕まえたまま生き残っていれば、本来の勝者を押しのけて単独勝利する
    public override void CheckWinner(GameOverReason reason)
    {
        if (!Kemono.Captured || Kemono.FurryId == byte.MaxValue) return;

        PlayerControl pc = Utils.GetPlayerById(Kemono.FurryId);
        if (!pc || !pc.IsAlive() || !pc.Is(CustomRoles.Furry)) return;

        CustomWinnerHolder.ResetAndSetAndChWinner(CustomWinner.Furry, pc.PlayerId, addWin: false, overrideRole: CustomRoles.Kemono);
    }
}
