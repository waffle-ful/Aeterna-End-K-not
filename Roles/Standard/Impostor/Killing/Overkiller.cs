using System.Collections;
using System.Collections.Generic;
using EndKnot.Modules;
using Hazel;
using UnityEngine;

namespace EndKnot.Roles;

internal class Overkiller : RoleBase
{
    public static bool On;

    public static List<byte> OverkillerDeadPlayerList = [];
    private static OptionItem KillCooldown;
    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(16900, TabGroup.ImpostorRoles, CustomRoles.Overkiller);

        KillCooldown = new FloatOptionItem(16902, "KillCooldown", new(0f, 180f, 0.5f), 30f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Overkiller])
            .SetValueFormat(OptionFormat.Seconds);
    }

    public override void Add(byte playerId)
    {
        On = true;
    }

    public override void Init()
    {
        On = false;
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        hud.KillButton?.OverrideText(Translator.GetString("OverkillerButtonText"));
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    public override bool OnCheckMurder(PlayerControl killer, PlayerControl target)
    {
        if (!killer.RpcCheckAndMurder(target, true)) return false;

        if (killer.PlayerId != target.PlayerId && !target.Is(CustomRoles.Disregarded) && Main.IntroDestroyed && GameStates.IsInTask && !ExileController.Instance && !AntiBlackout.SkipTasks)
        {
            Main.PlayerStates[target.PlayerId].deathReason = PlayerState.DeathReason.Dismembered;

            LateTask.New(() =>
            {
                if (!OverkillerDeadPlayerList.Contains(target.PlayerId))
                    OverkillerDeadPlayerList.Add(target.PlayerId);

                if (target.Is(CustomRoles.Avenger))
                {
                    target.Suicide(PlayerState.DeathReason.Dismembered, killer);

                    foreach (PlayerControl pc in Main.EnumerateAlivePlayerControls())
                        pc.Suicide(PlayerState.DeathReason.Revenge, target);

                    CustomWinnerHolder.ResetAndSetWinner(CustomWinner.None);
                    return;
                }


                RPCHandlerPatch.WhiteListFromRateLimitUntil(target.PlayerId, Utils.TimeStamp + 5);

                Vector2 ops = target.Pos();
                Vector2 originPos = killer.Pos();
                var rd = IRandom.Instance;

                Main.Instance.StartCoroutine(SpawnFakeDeadBodies());
                return;

                IEnumerator SpawnFakeDeadBodies()
                {
                    // 公式鯖 anti-cheat 緩和: 偽死体の数とペースを FakeBodyBurst で絞る (サイズ/分割は無罪・
                    // 高速 spawn 自体が host を reason=Hacking で落とすため)。kill switch で旧挙動に戻せる。
                    int count = FakeBodyBurst.BodyCount;
                    FakeBodyBurst.LogBurst("Overkiller", count);

                    for (var i = 0; i < count; i++)
                    {
                        Vector2 location = new(ops.x + ((float)(rd.Next(0, 201) - 100) / 100), ops.y + ((float)(rd.Next(0, 201) - 100) / 100));
                        location += new Vector2(0, 0.3636f);

                        Utils.RpcCreateDeadBody(location, (byte)target.CurrentOutfit.ColorId, target, SendOption.None);

                        if (FakeBodyBurst.Gentle) yield return new WaitForSecondsRealtime(FakeBodyBurst.SpacingSeconds);
                        else if (i % 4 == 0) yield return null;
                    }

                    yield return null;
                    killer.TP(originPos);
                }
            }, 0.05f, "Overkiller Murder");
        }

        return base.OnCheckMurder(killer, target);
    }
}