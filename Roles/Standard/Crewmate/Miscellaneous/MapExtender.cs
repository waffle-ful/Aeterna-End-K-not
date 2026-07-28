using System;
using System.Collections.Generic;
using System.Linq;
using EndKnot.Modules;
using UnityEngine;

namespace EndKnot.Roles;

// ============================================================
// MapExtender (マップ拡張パック) — Crewmate/Miscellaneous
//
// コンセプト:
//   ペット押下で足元に「入口ポータル」を設置し、同時に
//   マップ外(上方向)の固定地点に「出口ポータル」を生成する。
//   どちらかの端点に近づいた任意のプレイヤーを反対側へ TP する
//   (PortalMaker と同型)。マップ外(判定線より上)に居続けた時間が
//   OutsideTimeLimit を超えたプレイヤー全員 (MapExtender 本人含む) を
//   Erased 死させる。
//
// マップ外判定は bbox ではなく「上端の外側」の片側閾値のみで行う。
// RandomSpawn.SpawnMap.Positions は各マップの部屋代表点の集合に
// 過ぎず歩行可能領域を網羅しないため、x も含めた bbox 判定にすると
// 正当な部屋に居るだけのプレイヤーが誤って死亡し得る。
// 出口ポータルは常に「上端の外」固定なので、正当にマップ外へ出られるのは
// 上方向だけであり、この片側判定で構造的に誤判定を防ぐ。
//
// マップ境界算出に失敗した場合 (未知マップの例外 / Positions 空 /
// LIMap) は、滞在計時・抹消死・ポータル設置を含む機能全体を無効化する
// (Riptide と同じ fail-safe 方針)。
// ============================================================
public class MapExtender : RoleBase
{
    private const int Id = 705300;

    private const float TriggerRange = 1f;       // ポータルが反応する半径
    private const float LatchReleaseRange = 2f;  // ここまで離れて初めて再度反応する
    private const float TpInterval = 5f;         // 同一プレイヤーの連続 TP 間隔
    private const int MaxPortalTpsPerRound = 30; // ラウンド SnapTo 予算のバックストップ

    public static bool On;

    public static OptionItem AbilityCooldown;
    public static OptionItem OutsideTimeLimit;

    // ---- マップ境界 (Init() で算出、ゲーム全体で共有) ----
    private static bool MapBoundsValid;
    private static float OutsideLine;         // これ以上の y は「マップ外」
    private static Vector2 ExitBasePosition;  // 出口ポータルの基準位置 (中央X, 上端+25)
    private static Vector2 SafeReturnPosition; // 蘇生者の引き戻し先 (必ずマップ内)
    private static int HolderCount;

    // ---- マップ外の滞在追跡は保持者間で共有する (static) ----
    // インスタンス単位で持つと、2人以上が MapExtender を持ったときに
    //   ・先に Suicide を呼んだ側で他方の OutsideSince が消えずに凍結し、
    //     蘇生直後にその古い since で即座に再抹消される
    //   ・救済 TP が保持者の数だけ二重に飛ぶ
    // という順序依存の事故になる。追跡対象はプレイヤーであって保持者ではないので共有が正しい。
    private static readonly Dictionary<byte, long> OutsideSince = [];
    private static readonly HashSet<byte> ErasedOutside = [];
    private static int PortalTpsThisRound;
    private static bool CapLoggedThisRound;

    // ---- per-instance fields ----
    // 座標は CNO から読まない。CustomNetObject.Position は生成コルーチン内で
    // 非同期に代入される (イントロ直後は10秒以上遅れる経路がある) ため、
    // 設置直後に読むと (0,0) を返し、マップ原点付近のプレイヤーが飛ばされる。
    private Vector2? EntryPos;
    private Vector2? ExitPos;
    private MapExtenderPortal EntryPortal;
    private MapExtenderPortal ExitPortal;
    private readonly HashSet<byte> TpLatched = [];
    private float ExitXOffset;

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        StartSetup(Id)
            .AutoSetupOption(ref AbilityCooldown, 20, new IntegerValueRule(1, 120, 1), OptionFormat.Seconds)
            .AutoSetupOption(ref OutsideTimeLimit, 20, new IntegerValueRule(0, 100, 1), OptionFormat.Seconds);
    }

    public override void Init()
    {
        On = false;
        MapBoundsValid = false;
        HolderCount = 0;
        OutsideSince.Clear();
        ErasedOutside.Clear();
        PortalTpsThisRound = 0;
        CapLoggedThisRound = false;

        try
        {
            if (Main.LIMap) return;

            var spawnMap = RandomSpawn.SpawnMap.GetSpawnMap();
            var positions = spawnMap.Positions.Values.ToList();
            if (positions.Count == 0) return;

            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (var pos in positions)
            {
                if (pos.x < minX) minX = pos.x;
                if (pos.x > maxX) maxX = pos.x;
                if (pos.y < minY) minY = pos.y;
                if (pos.y > maxY) maxY = pos.y;
            }

            OutsideLine = maxY + 15f;
            ExitBasePosition = new Vector2((minX + maxX) / 2f, maxY + 25f);

            // 蘇生者の戻し先は「中心に最も近い spawn 点」— 部屋の代表点なので必ずマップ内。
            var center = new Vector2((minX + maxX) / 2f, (minY + maxY) / 2f);
            SafeReturnPosition = positions[0];
            float bestSqr = float.MaxValue;
            foreach (var pos in positions)
            {
                float dx = pos.x - center.x, dy = pos.y - center.y;
                float sqr = dx * dx + dy * dy;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                SafeReturnPosition = pos;
            }

            MapBoundsValid = true;
        }
        catch (Exception e)
        {
            Logger.Error($"MapExtender: マップ境界計算失敗 → 機能無効化: {e.Message}", "MapExtender.Init");
            MapBoundsValid = false;
        }
    }

    public override void Add(byte playerId)
    {
        On = true;
        DespawnPortals();
        TpLatched.Clear();

        // 複数人が MapExtender を持つ場合に出口が同一座標へ重なると、
        // 同じフレームで複数インスタンスから TP されて予算を二重消費する。
        // 保持者ごとに出口の x をずらして重なりを避ける。
        ExitXOffset = HolderCount++ * 4f;
    }

    public override void OnPet(PlayerControl pc)
    {
        if (!MapBoundsValid) return;

        DespawnPortals();

        EntryPos = pc.Pos();
        ExitPos = ExitBasePosition + new Vector2(ExitXOffset, 0f);
        EntryPortal = new MapExtenderPortal(EntryPos.Value);
        ExitPortal = new MapExtenderPortal(ExitPos.Value);
        TpLatched.Clear();
    }

    public override void OnCheckPlayerPosition(PlayerControl pc)
    {
        if (EntryPos == null || ExitPos == null) return;

        // Shuffler の void 落下地点は出口ポータルと同じ「上端の外」に置かれるため、
        // 除外しないと落ちた直後にこのポータルが拾って入口へ TP し戻してしまう
        // (void なのにマップ内を歩けるのに投票と発言だけ封じられた状態になる)。
        if (Shuffler.IsInVoid(pc.PlayerId)) return;

        Vector2 entry = EntryPos.Value;
        Vector2 exit = ExitPos.Value;
        Vector2 pos = pc.Pos();

        bool nearEntry = FastVector2.DistanceWithinRange(entry, pos, TriggerRange);
        bool nearExit = !nearEntry && FastVector2.DistanceWithinRange(exit, pos, TriggerRange);

        if (!nearEntry && !nearExit)
        {
            // TP 先はポータルの中心そのものなので、着地した時点で再び反応半径の中にいる。
            // 時間クールダウンだけだとその場に立っているだけで永久に往復し続け、
            // ラウンド共有の SnapTo 予算 (80/100) を枯らして他役職の TP を無音で殺す。
            // 一度両ポータルから離れるまで再発火させないラッチで断ち切る。
            if (!FastVector2.DistanceWithinRange(entry, pos, LatchReleaseRange) &&
                !FastVector2.DistanceWithinRange(exit, pos, LatchReleaseRange))
                TpLatched.Remove(pc.PlayerId);

            return;
        }

        if (TpLatched.Contains(pc.PlayerId)) return;

        if (PortalTpsThisRound >= MaxPortalTpsPerRound)
        {
            // OnCheckPlayerPosition は lowLoad ゲート無しで毎 fixed update 走る。
            // 上限到達後はポータルの上に立っているだけで毎フレームここに落ちるので、
            // ログはラウンドに1回だけ出す。
            if (!CapLoggedThisRound)
            {
                CapLoggedThisRound = true;
                Logger.Info($"ラウンドのポータル TP 上限 ({MaxPortalTpsPerRound}) に到達 — 会議まで転送を停止", "MapExtender");
            }

            return;
        }

        // minInterval は Utils の per-player 共有ゲート。手書きの辞書と違い
        // 同役職が複数枚配られても間引きが効く。
        if (!pc.TP(nearEntry ? exit : entry, minInterval: TpInterval)) return;

        TpLatched.Add(pc.PlayerId);
        PortalTpsThisRound++;
    }

    public override void OnGlobalFixedUpdate(PlayerControl pc, bool lowLoad)
    {
        if (lowLoad || !MapBoundsValid) return;
        if (!Main.IntroDestroyed || !GameStates.InGame || GameStates.IsMeeting || ExileController.Instance || AntiBlackout.SkipTasks || !pc.IsAlive()) return;

        // Shuffler に void 送りされた人は同じ「上端の外」に居るが、あちらは会議で戻る前提の
        // 一時的な追放なので、こちらの滞在計時に載せると抹消死してしまう。計時対象から外す。
        if (Shuffler.IsInVoid(pc.PlayerId))
        {
            OutsideSince.Remove(pc.PlayerId);
            return;
        }

        bool outside = pc.Pos().y >= OutsideLine;

        // 抹消死した相手を /revive で蘇生すると、座標はマップ外のままなので
        // 計時が再武装され、脱出手段もないまま同じ周期で死に続ける。
        // 蘇生を検知したら 1 回だけマップ内へ引き戻す。
        if (ErasedOutside.Remove(pc.PlayerId))
        {
            OutsideSince.Remove(pc.PlayerId);
            if (outside) pc.TP(SafeReturnPosition);
            return;
        }

        if (!outside)
        {
            OutsideSince.Remove(pc.PlayerId);
            return;
        }

        long now = Utils.TimeStamp;

        if (!OutsideSince.TryGetValue(pc.PlayerId, out long since))
        {
            OutsideSince[pc.PlayerId] = now;
            return;
        }

        if (now - since >= OutsideTimeLimit.GetInt())
        {
            OutsideSince.Remove(pc.PlayerId);
            pc.Suicide(PlayerState.DeathReason.Erased);

            // Suicide は Veteran の防御中や Pestilence では無条件 no-op になる。
            // 実際に死んだことを確認してから救済対象に入れないと、
            // 生きたまま「蘇生扱い」でマップ内へ無料送還される安全地帯になる。
            if (!pc.IsAlive()) ErasedOutside.Add(pc.PlayerId);
        }
    }

    public override void AfterMeetingTasks()
    {
        // MapExtender 死亡時はポータルを残す (マップ外に取り残された人が戻れなくなるため)。
        // 消えるのは会議のタイミングのみ (毎ラウンド張り直し)。
        DespawnPortals();
        TpLatched.Clear();
        OutsideSince.Clear();
        PortalTpsThisRound = 0;
        CapLoggedThisRound = false;
    }

    private void DespawnPortals()
    {
        EntryPortal?.Despawn();
        ExitPortal?.Despawn();
        EntryPortal = null;
        ExitPortal = null;
        EntryPos = null;
        ExitPos = null;
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (meeting) return string.Empty;
        if (seer.PlayerId != target.PlayerId) return string.Empty;
        if (!MapBoundsValid) return string.Empty;
        if (!OutsideSince.TryGetValue(target.PlayerId, out long since)) return string.Empty;

        long remain = OutsideTimeLimit.GetInt() - (Utils.TimeStamp - since);
        if (remain < 0) remain = 0;
        return Utils.ColorString(Color.red, $" {remain}");
    }
}
