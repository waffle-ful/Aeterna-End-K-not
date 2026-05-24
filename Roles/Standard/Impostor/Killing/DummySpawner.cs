using System;
using System.Collections.Generic;
using System.Linq;
using Hazel;
using UnityEngine;

namespace EndKnot.Roles;

public class DummySpawner : RoleBase
{
    private const int Id = 703600;

    private static List<byte> PlayerIdList = [];
    private static Dictionary<byte, List<RandomDummy>> SpawnedDummies = [];

    private static OptionItem KillCooldownOpt;
    private static OptionItem DummyCountOpt;
    private static OptionItem DummyKillRangeOpt;

    private static int LastSpawnedMeeting = -1;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.ImpostorRoles, CustomRoles.DummySpawner);

        KillCooldownOpt = new FloatOptionItem(Id + 10, "KillCooldown", new(0f, 60f, 0.5f), 30f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.DummySpawner])
            .SetValueFormat(OptionFormat.Seconds);

        DummyCountOpt = new IntegerOptionItem(Id + 11, "DummySpawnerDummyCount", new(1, 200, 1), 10, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.DummySpawner])
            .SetValueFormat(OptionFormat.Times);

        DummyKillRangeOpt = new FloatOptionItem(Id + 12, "DummySpawnerKillRange", new(0.5f, 5f, 0.1f), 1.5f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.DummySpawner]);
    }

    public override void Init()
    {
        PlayerIdList = [];
        SpawnedDummies = [];
        LastSpawnedMeeting = -1;
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        SpawnedDummies[playerId] = [];
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
        if (SpawnedDummies.TryGetValue(playerId, out var dummies))
        {
            dummies.ToArray().Do(d => d?.Despawn());
            dummies.Clear();
        }
        SpawnedDummies.Remove(playerId);
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldownOpt.GetFloat();
    }

    // CNO は AllPlayerControls から除外されているため vanilla キル button のターゲットにならない。
    // 代わりに「実プレイヤーをキルしようとした瞬間」に proximity 判定で近くのダミーを優先する
    public override bool OnCheckMurder(PlayerControl killer, PlayerControl target)
    {
        if (!PlayerIdList.Contains(killer.PlayerId)) return true;
        if (!SpawnedDummies.TryGetValue(killer.PlayerId, out var dummies)) return true;

        var killerPos = killer.Pos();
        float range = DummyKillRangeOpt.GetFloat();

        var dummy = dummies
            .Where(d => d?.playerControl != null)
            .OrderBy(d => Vector2.Distance(killerPos, d.Position))
            .FirstOrDefault(d => Vector2.Distance(killerPos, d.Position) <= range);

        if (dummy == null) return true;

        dummy.Despawn();
        dummies.Remove(dummy);
        Main.AllAlivePlayerControlsToList.Do(p => p.KillFlash());
        killer.SetKillCooldown(KillCooldownOpt.GetFloat());
        Utils.NotifyRoles(SpecifySeer: killer, SpecifyTarget: killer);
        return false;
    }

    public override bool OnCheckMurderAsTarget(PlayerControl killer, PlayerControl target)
    {
        if (SpawnedDummies.TryGetValue(target.PlayerId, out var dummies))
        {
            dummies.ToArray().Do(d => d?.Despawn());
            dummies.Clear();
        }
        return true;
    }

    public override void AfterMeetingTasks()
    {
        if (!AmongUsClient.Instance.AmHost) return;

        // Utils.AfterMeetingTasks() は各プレイヤーのRoleに対してこのメソッドを呼ぶため
        // 同じmeeting内で複数回発火する。MeetingNumで重複実行を防ぐ
        int currentMeeting = MeetingStates.MeetingNum;
        if (LastSpawnedMeeting == currentMeeting) return;
        LastSpawnedMeeting = currentMeeting;

        // CreateNetObject は PlayerControl.AllPlayerControls を Add するため
        // 親側の foreach (Main.EnumeratePlayerControls) を破壊する。LateTask で遅延させる
        LateTask.New(SpawnAllDummies, 1f, "DummySpawner.AfterMeeting");
    }

    private static void SpawnAllDummies()
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (GameStates.IsEnded || GameStates.IsMeeting) return;

        foreach (byte id in PlayerIdList.ToArray())
        {
            var pc = Utils.GetPlayerById(id);
            if (pc == null || !pc.IsAlive()) continue;

            if (SpawnedDummies.TryGetValue(id, out var old))
            {
                old.ToArray().Do(d => d?.Despawn());
                old.Clear();
            }

            var dummies = new List<RandomDummy>();
            SpawnedDummies[id] = dummies;
            int count = DummyCountOpt.GetInt();
            byte capturedId = id;
            List<RandomDummy> capturedList = dummies;

            // 50 個一気バーストは Among Us server の rate limit に引っかかり、
            // 非モッド側で FailedError 復元 step (CNO.PlayerId を 254 へ戻す) のパケットが drop する。
            // → CNO.PlayerId が非モッド自身の値で stuck し全 dummy が非モッドとしてレンダリング。
            // TOHP の SpawnQueue (0.4s gap) に倣い、staggered LateTask で burst を解消する
            for (int i = 0; i < count; i++)
            {
                int idx = i;
                LateTask.New(() =>
                {
                    if (GameStates.IsEnded || GameStates.IsMeeting) return;
                    var owner = Utils.GetPlayerById(capturedId);
                    if (owner == null || !owner.IsAlive()) return;
                    if (!SpawnedDummies.TryGetValue(capturedId, out var current) || current != capturedList) return;
                    capturedList.Add(new RandomDummy(GetRandomMapPosition()));
                }, idx * 0.05f, "DummySpawner.Spawn", log: false);
            }
        }
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (!SpawnedDummies.TryGetValue(playerId, out var dummies) || dummies.Count == 0)
            return string.Empty;
        int active = dummies.Count(d => d?.playerControl != null);
        return Utils.ColorString(Palette.ImpostorRed, $"(dummy:{active})");
    }

    private static Vector2 GetRandomMapPosition()
    {
        try
        {
            var spawnMap = RandomSpawn.SpawnMap.GetSpawnMap();
            if (spawnMap?.Positions == null || spawnMap.Positions.Count == 0)
            {
                Logger.Warn($"GetRandomMapPosition: spawnMap or Positions is null/empty (CurrentMap={Main.CurrentMap})", "DummySpawner");
                return Vector2.zero;
            }

            var values = spawnMap.Positions.Values.ToList();
            var basePos = values[IRandom.Instance.Next(values.Count)];
            // HashRandom requires positive values; offset by -20 after sampling 0..40
            return basePos + new Vector2(
                (IRandom.Instance.Next(0, 41) - 20) * 0.1f,
                (IRandom.Instance.Next(0, 41) - 20) * 0.1f);
        }
        catch (Exception e)
        {
            Logger.Warn($"GetRandomMapPosition failed: {e.GetType().Name}: {e.Message}", "DummySpawner");
            return Vector2.zero;
        }
    }

    // Shapeshift trick で player-like CNO に individual outfit を投入する。
    // LocalPlayer.Outfits を一時的に dummy outfit に書換 → Shapeshift RPC → 復元
    // CachedPlayerData は LocalPlayer.Data 共有なので Utils.RpcChangeSkin は使えない
    // (host の Data NetId に書き込んでしまうため)
    internal static void ApplyOutfitToCNO(PlayerControl cnoPC, int colorId,
        string skinId, string hatId, string visorId, string petId)
    {
        if (!AmongUsClient.Instance.AmHost || cnoPC == null) return;

        var localOutfit = PlayerControl.LocalPlayer.Data.Outfits[PlayerOutfitType.Default];
        string origName = localOutfit.PlayerName;
        int origColor = localOutfit.ColorId;
        string origHat = localOutfit.HatId;
        string origSkin = localOutfit.SkinId;
        string origPet = localOutfit.PetId;
        string origVisor = localOutfit.VisorId;

        var sender = CustomRpcSender.Create("DummySpawner.ApplyOutfit", SendOption.Reliable, log: false);
        var writer = sender.stream;
        sender.StartMessage();

        // TOHP-style: PlayerName は維持して見た目だけ書き換える
        localOutfit.ColorId = colorId;
        localOutfit.HatId = hatId ?? "";
        localOutfit.SkinId = skinId ?? "";
        localOutfit.PetId = petId ?? "";
        localOutfit.VisorId = visorId ?? "";

        writer.StartMessage(1);
        writer.WritePacked(PlayerControl.LocalPlayer.Data.NetId);
        PlayerControl.LocalPlayer.Data.Serialize(writer, false);
        writer.EndMessage();

        try { cnoPC.Shapeshift(PlayerControl.LocalPlayer, false); }
        catch (Exception e) { Utils.ThrowException(e); }

        sender.StartRpc(cnoPC.NetId, (byte)RpcCalls.Shapeshift)
            .WriteNetObject(PlayerControl.LocalPlayer)
            .Write(false)
            .EndRpc();

        localOutfit.PlayerName = origName;
        localOutfit.ColorId = origColor;
        localOutfit.HatId = origHat;
        localOutfit.SkinId = origSkin;
        localOutfit.PetId = origPet;
        localOutfit.VisorId = origVisor;

        writer.StartMessage(1);
        writer.WritePacked(PlayerControl.LocalPlayer.Data.NetId);
        PlayerControl.LocalPlayer.Data.Serialize(writer, false);
        writer.EndMessage();

        sender.EndMessage();
        sender.SendMessage();
    }
}

internal sealed class RandomDummy : CustomNetObject
{
    private static readonly string[] SkinIds =
    [
        "skin_Astronaut", "skin_BlackSuit", "skin_CaptainA",
        "skin_Hazmat", "skin_Military", "skin_Police",
        "skin_Science", "skin_SuitB", "skin_Wall",
        "skin_Winter", "",
    ];

    private static readonly string[] HatIds =
    [
        "hat_PaperHat", "hat_Fedora", "hat_TopHat",
        "hat_Antenna", "hat_Crown", "hat_FloppyHat",
        "hat_Eyebrows", "hat_Captain", "hat_Goggles",
        "hat_HardHat", "hat_Halo", "hat_Beanie", "",
    ];

    private static readonly string[] VisorIds =
    [
        "visor_Visor", "visor_AngryEyes", "visor_ArcticGoggles",
        "visor_BubbleVisor", "visor_CandyCorns", "visor_CoolVisor",
        "visor_FlameVisor", "visor_GreenVisor", "visor_HalfVisor",
        "visor_HorrorVisor", "visor_LobsterVisor", "visor_Mira", "",
    ];

    private static readonly string[] PetIds =
    [
        "pet_Alien", "pet_Bedcrab", "pet_Bushfriend",
        "pet_Charles", "pet_Clank", "pet_Crewmate",
        "pet_Doggy", "pet_Ellie", "pet_Hamster",
        "pet_Limegreen", "pet_Mini", "pet_Norbert",
        "pet_Squig", "pet_Stickmin", "pet_UFO", "",
    ];

    private readonly int _colorId;
    private readonly string _skinId;
    private readonly string _hatId;
    private readonly string _visorId;
    private readonly string _petId;

    protected override bool IsPlayerLike => true;

    public RandomDummy(Vector2 position)
    {
        var rng = IRandom.Instance;
        _colorId = rng.Next(0, Palette.PlayerColors.Length);
        _skinId = SkinIds[rng.Next(0, SkinIds.Length)];
        _hatId = HatIds[rng.Next(0, HatIds.Length)];
        _visorId = VisorIds[rng.Next(0, VisorIds.Length)];
        _petId = PetIds[rng.Next(0, PetIds.Length)];

        CreateNetObject(string.Empty, position);
    }

    protected override void OnAfterCreate()
    {
        if (playerControl == null) return;

        // ホスト側 body 描画: 必ず ApplyOutfitToCNO (Shapeshift trick) より「前」に
        // SetColors + Color.white を入れる。後ろに置くと host 側で body が描画されない
        // 現象を再現確認済み (Shapeshift が cosmetics 内部状態を確定させる前に
        // body color を上書きしないと反映されないと推測)
        try
        {
            var bodySprite = playerControl.cosmetics.currentBodySprite.BodySprite;
            PlayerMaterial.SetColors(_colorId, bodySprite);
            bodySprite.color = Color.white;
        }
        catch (Exception e) { Utils.ThrowException(e); }

        // 非モッドへの outfit 同期: Shapeshift trick (LocalPlayer outfit 一時書換 + Shapeshift RPC)
        DummySpawner.ApplyOutfitToCNO(playerControl, _colorId, _skinId, _hatId, _visorId, _petId);

        // 空文字列だと Among Us 側で「player 非表示」扱いされる挙動を引いた経験あり。
        // TOHP に合わせて "Dummy" を使う
        RpcSetCnoName("Dummy");
    }

    // 固定位置のため OnFixedUpdate は空 override
    protected override void OnFixedUpdate() { }

    public override void OnMeeting()
    {
        Despawn();
    }
}
