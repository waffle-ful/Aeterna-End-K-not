namespace EndKnot.Roles;

// 何もできない役職。他の役職が役目を失った時の変化先専用で、抽選には出ない。
// 勝敗計算から外れる (CountTypes.OutOfGame) ので、どの陣営の勝利にも含まれない。
public class Emptiness : RoleBase
{
    public static bool On;
    public override bool IsEnable => On;

    public override void SetupCustomOption() { }

    public override void Init()
    {
        On = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
    }

    public override void Remove(byte playerId)
    {
        On = false;
    }

    public override bool CanUseKillButton(PlayerControl pc) => false;

    public override bool CanUseImpostorVentButton(PlayerControl pc) => false;

    public override bool CanUseSabotage(PlayerControl pc) => false;
}
