using System.Linq;
using UnityEngine;

namespace EndKnot;

// 配信者向け: GM ホスト/途中死亡ホストが観戦中に画面が固定で退屈にならないよう、
// 生存プレイヤーを一定間隔で自動的に順繰り追従する。
public static class StreamerCamera
{
    private static float Timer;
    private static int Index;
    private static byte? CurrentTargetId;
    private static bool Following;

    public static bool CanFollow =>
        AmongUsClient.Instance.AmHost &&
        GameStates.IsInTask &&
        !GameStates.IsEnded &&
        !ExileController.Instance &&
        !PlayerControl.LocalPlayer.IsAlive() &&
        Options.SpectatorAutoCam.GetBool() &&
        HudManager.InstanceExists &&
        !HudManager.Instance.Chat.IsOpenOrOpening;

    public static void OnFixedUpdate()
    {
        try
        {
            if (!HudManager.InstanceExists || !HudManager.Instance.PlayerCam) return;

            if (!CanFollow)
            {
                if (Following)
                {
                    HudManager.Instance.PlayerCam.Target = PlayerControl.LocalPlayer;
                    HudManager.Instance.PlayerCam.SnapToTarget();
                    Following = false;
                    CurrentTargetId = null;
                }

                return;
            }

            PlayerControl current = CurrentTargetId.HasValue ? Utils.GetPlayerById(CurrentTargetId.Value) : null;
            bool currentAlive = current != null && current.IsAlive();

            Timer += Time.fixedDeltaTime;

            if (!currentAlive || Timer >= Options.SpectatorAutoCamInterval.GetInt())
            {
                Timer = 0f;

                var alive = Main.AllAlivePlayerControls.OrderBy(x => x.PlayerId).ToArray();
                if (alive.Length == 0) return;

                if (!currentAlive) Index = 0;
                else Index = (Index + 1) % alive.Length;

                PlayerControl next = alive[Index % alive.Length];

                // 生存者が1人だけの場合など next が現在と同一なら再スナップしない (カクつき防止)。
                if (!CurrentTargetId.HasValue || next.PlayerId != CurrentTargetId.Value)
                {
                    HudManager.Instance.PlayerCam.Target = next;
                    HudManager.Instance.PlayerCam.SnapToTarget();
                    CurrentTargetId = next.PlayerId;
                }

                Following = true;
            }
        }
        catch { }
    }
}
