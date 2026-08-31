using System;
using EndKnot.Roles;
using UnityEngine;

namespace EndKnot.Modules.Ekm;

// EKR logic 契約 v1.1 の dummy_spawn 用 player-like CNO。
// 参考実装: Roles/Standard/Impostor/Killing/DummySpawner.cs の RandomDummy。既存コードは一切改変せず、
// 外見ランダムプールと OnAfterCreate の呼び出し順序だけを踏襲する。
internal sealed class EkrDummyCno : CustomNetObject, IKillableDummy, IEkrSlotCno
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

    private readonly string _name;
    private readonly bool _killable;
    private readonly int _colorId;
    private readonly string _skinId;
    private readonly string _hatId;
    private readonly string _visorId;
    private readonly string _petId;

    protected override bool IsPlayerLike => true;

    // cno_move opcode (dx/dy) はこのアンカーからの絶対オフセット (EkrCno と同型・spec §3 準拠)。
    public Vector2 SpawnAnchor { get; }

    public bool IsInstantiated => playerControl;

    public EkrDummyCno(Vector2 position, string name, bool killable)
    {
        SpawnAnchor = position;
        // EkrLogicDef.TryParseNode 側で空文字は既に "Dummy" に置換済み (spec §3) — ここは防御的な再チェック。
        _name = string.IsNullOrEmpty(name) ? "Dummy" : name;
        _killable = killable;

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

        // RandomDummy と同じ順序: SetColors+Color.white は ApplyOutfitToCNO より前に置く
        // (Shapeshift が cosmetics 内部状態を確定させる前に body color を上書きしないと host 画面に反映されない)。
        try
        {
            var bodySprite = playerControl.cosmetics.currentBodySprite.BodySprite;
            PlayerMaterial.SetColors(_colorId, bodySprite);
            bodySprite.color = Color.white;
        }
        catch (Exception e) { Utils.ThrowException(e); }

        DummySpawner.ApplyOutfitToCNO(playerControl, _colorId, _skinId, _hatId, _visorId, _petId, _name);

        // ホスト画面側の名札 (客への名前同期は上の ApplyOutfitToCNO の Shapeshift スナップショットで済む)。
        RpcSetCnoName(_name);
    }

    // ⚠️ OnFixedUpdate は override しない (RandomDummy の「固定位置」override をそのまま持ち込まない)。
    // 基底 CustomNetObject.OnFixedUpdate() が ForceSnapSend フラグを見て実際に SnapTo を送信する唯一の
    // 場所であり、TP() (MoveToOffset が呼ぶ) は Position を書き換えてフラグを立てるだけで自分では何も
    // 送らない。空 override にすると cno_move が Position だけ動かして誰にも見えず、GetKillableTarget の
    // 距離判定 (Position 基準) が実際の見た目と食い違う無音バグになる (spec §3 v1.1「cno_move はダミーにも
    // そのまま効く」)。EkrCno も同じ理由で override していない — 同じ全体≤10体予算を共有するので送信頻度も
    // 既存の CNO と同じ枠に収まる。

    // spec §3 v1.1: 「ダミーは会議で消える」— 基底の会議明け自動復活エンジンには乗せない。実際の消滅は
    // EkrManager.FireMeetingStart() (会議開始と同時・fiber 全キャンセルと同タイミング) が唯一の経路として
    // 一括で行う (Despawn + slot 台帳のクリア)。ここで Despawn を呼ぶと、PlayerControlPatch の全 CNO
    // 一斉 OnMeeting 呼び出し (会議開始 5 秒後) が「EkrManager が既に slot から外した後の、もう追跡されて
    // いないインスタンス」に対して基底 OnMeeting() 相当の再 Despawn+新規復活コルーチン起動を投げることに
    // なり、slot 台帳と無関係に会議明けで復活する孤立ダミーを生む。空にして管理元を EkrManager 側だけに
    // 一本化する。
    public override void OnMeeting() { }

    public void MoveToOffset(float dx, float dy)
    {
        TP(SpawnAnchor + new Vector2(dx, dy));
    }

    void IEkrSlotCno.Despawn() => Despawn();

    // ── IKillableDummy (spec §3: killable なダミーはキル権限持ちがペット/キルボタンで撃破できる) ──────

    public bool CanBeKilledBy(PlayerControl killer)
    {
        return _killable && killer && killer.IsAlive() && killer.CanUseKillButton();
    }

    public void OnKilled(PlayerControl killer)
    {
        Main.AllAlivePlayerControlsToList.Do(p => p.KillFlash());
        killer.SetKillCooldown();

        // 撃破時に slot 台帳から外す (spec §5) — ここで外さないと Despawn 済み実体が slot に残り、
        // 全体≤10体の導出カウントが永久に埋まる。
        EkrManager.NotifyCnoGone(this);
        Despawn();
    }
}
