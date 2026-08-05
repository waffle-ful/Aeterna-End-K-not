using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace EndKnot
{
    public sealed class DummyPlayer : CustomNetObject, IKillableDummy
    {
        public static readonly Dictionary<int, DummyPlayer> ActiveDummies = new();
        public readonly string DummyName;
        private static int NextIndex;

        // true (default): 毎フレーム自前 Position に snap し続けて静的マーカーとして固定
        // false: snap-back を止めて他役職 (ForceField の eject TP 等) で吹き飛ばし可能にする
        // /dummyfree でトグル。ForceField 視覚 vs 判定の calibration test 用。
        public static bool LockPosition = true;

        // 撃破時の偽死体をダミー自身の色で出すために保持する。
        private readonly int ColorId;

        public DummyPlayer(Vector2 position, string dummyName)
        {
            DummyName = dummyName;
            int colorId = ColorId = NextIndex % Palette.PlayerColors.Length;
            CreateNetObject($"<size=150%><color=#888888>[{dummyName}]</color></size>", position);
            if (!playerControl)
            {
                Logger.Warn($"[Dummy] playerControl null after CreateNetObject (IntroDestroyed={Main.IntroDestroyed}, InGame={GameStates.InGame})", "Dummy");
                return;
            }
            ActiveDummies[Id] = this;
            LateTask.New(() =>
            {
                if (!playerControl) return;
                Logger.Info($"[Dummy] 0.5s: Visible={playerControl.Visible} pos={playerControl.GetTruePosition()}", "Dummy");
                try
                {
                    playerControl.transform.FindChild("Names")?.gameObject.SetActive(true);
                    playerControl.transform.FindChild("Names")?.FindChild("NameText_TMP")?.gameObject.SetActive(true);
                    var nt = playerControl.cosmetics?.nameText;
                    if (nt != null) nt.enabled = true;
                    var bodySprite = playerControl.cosmetics.currentBodySprite.BodySprite;
                    PlayerMaterial.SetColors(colorId, bodySprite);
                    bodySprite.color = Color.white;
                }
                catch (Exception e) { Utils.ThrowException(e); }
                playerControl.Visible = true;
            }, 0.5f);
            LateTask.New(() =>
            {
                if (!playerControl) return;
                try
                {
                    var namesTf = playerControl.transform.FindChild("Names");
                    var ntTf = namesTf?.FindChild("NameText_TMP");
                    var nt = playerControl.cosmetics?.nameText;
                    Logger.Info($"[Dummy] 2s:" +
                        $" Visible={playerControl.Visible}" +
                        $" PC.inHierarchy={playerControl.gameObject.activeInHierarchy}" +
                        $" NameText.inHierarchy={ntTf?.gameObject.activeInHierarchy}" +
                        $" nt.enabled={nt?.enabled}" +
                        $" nt.color={nt?.color}" +
                        $" text=\"{nt?.text?.Replace("\n", "\\n")}\"", "Dummy");
                }
                catch (Exception e) { Logger.Warn($"[Dummy] 2s error: {e.Message}", "Dummy"); }
            }, 2.0f);
        }

        protected override void OnFixedUpdate()
        {
            // snap-back は base の共有 throttle (全 CNO 合算で ~30 回/秒) に委譲する。ここで
            // NetTransform.SnapTo を毎フレ直呼びすると throttle を丸ごと迂回し、N 体分の毎フレ
            // Data 同期が SnapTo cap を食い潰す (公式鯖では /dummy 20 で 400-800/s 連射になる)。
            if (!playerControl || !LockPosition) return;
            base.OnFixedUpdate();
        }

        public override void OnMeeting()
        {
            Despawn();
            ActiveDummies.Remove(Id);
        }

        // Dev のテスト用マーカーなので誰でも壊せてよい (役職の設計を守る必要が無い)。
        public bool CanBeKilledBy(PlayerControl killer) => killer && killer.IsAlive();

        public void OnKilled(PlayerControl killer)
        {
            Logger.Info($"[Dummy] {DummyName} killed by {killer?.GetRealName()}", "Dummy");
            SpawnDummyCorpse(killer, Position, ColorId);
            Despawn();
            ActiveDummies.Remove(Id);
        }

        public static int SpawnBatch(int count, Vector2 origin)
        {
            int spawned = 0;
            Utils.CombineSendTimeLowering(() =>
            {
                for (int i = 0; i < count; i++)
                {
                    _ = new DummyPlayer(origin + new Vector2(i * 0.6f, 0f), $"dummy{++NextIndex}");
                    spawned++;
                }
            });
            return spawned;
        }

        public static int DespawnAll()
        {
            int n = ActiveDummies.Count;
            ActiveDummies.Values.ToArray().Do(d => d.Despawn());
            ActiveDummies.Clear();
            return n;
        }
    }
}
