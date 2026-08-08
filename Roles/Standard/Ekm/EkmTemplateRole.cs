using System;
using AmongUs.GameOptions;
using EndKnot.Modules.Ekm;

namespace EndKnot.Roles;

// EKN ノーコード役職メーカー R0 の共通実体 (計画正典: docs/ekn-api-plan.md)。
// 10個の予約スロット (CustomRoles.EkmCustomRole1..10) はこのクラスの「スロット番号だけ持つ薄い派生」。
// per-slot の状態は static フィールドで持たない (基底クラスの static は全スロットで1個しか無いため、
// 誤って混ざる) — 必ず EkrManager に CustomRoles (Slot) をキーとして持たせる。
public abstract class EkmTemplateRole : RoleBase
{
    // RoleBase.IsThisRole 等と同じ規約: 派生クラスの型名 = CustomRoles enum 名 (EkmCustomRole1..10)。
    protected CustomRoles Slot => Enum.Parse<CustomRoles>(GetType().Name, true);

    public override bool IsEnable => EkrManager.HasPlayers(Slot);

    // SetupCustomOption は各スロットクラス側で実装する (check-option-ids.ps1 は
    // `Options.SetupRoleOptions(<id>` と同一ファイル内の `const int Id = <literal>` を突き合わせて
    // 衝突検査するため、id リテラルと呼び出しは同じファイルに置く必要がある)。
    // R0 は最小構成: 役職ヘッダ (出現率) + Maximum のみ。数値パラメータは役職コード (JSON) 側が持つので
    // ホストオプション化しない (ekn-api-plan §決定事項6)。

    // 未束縛スロットをオプションメニューから隠す (各スロットの SetupCustomOption 末尾から呼ぶ)。
    // 表示と出現率の復帰は EkrManager.Bind/Unbind が行い、選出の安全網は Options.GetRoleSpawnMode。
    protected void HideUntilBound()
    {
        if (Options.CustomRoleSpawnChances != null && Options.CustomRoleSpawnChances.TryGetValue(Slot, out var opt))
            opt.SetHidden(!EkrManager.IsBound(Slot));
    }

    public override void Init()
    {
        EkrManager.ResetSlot(Slot);
    }

    public override void Add(byte playerId)
    {
        EkrManager.AddPlayer(Slot, playerId);
    }

    public override void Remove(byte playerId)
    {
        EkrManager.RemovePlayer(Slot, playerId);
    }

    public override void SetKillCooldown(byte id)
    {
        EkrDefinition def = EkrManager.GetDefinition(Slot);
        Main.AllPlayerKillCooldown[id] = def is { CanKill: true } ? def.KillCooldown : Options.AdjustedDefaultKillCooldown;
    }

    public override bool CanUseKillButton(PlayerControl pc)
    {
        EkrDefinition def = EkrManager.GetDefinition(Slot);
        return def is { CanKill: true } && pc.IsAlive();
    }

    public override bool CanUseImpostorVentButton(PlayerControl pc)
    {
        EkrDefinition def = EkrManager.GetDefinition(Slot);
        return def is { CanVent: true };
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        EkrDefinition def = EkrManager.GetDefinition(Slot);
        if (def == null) return;

        if (Math.Abs(def.VisionMultiplier - 1f) > 0.001f)
        {
            opt.SetVision(false);
            opt.SetFloat(FloatOptionNames.CrewLightMod, def.VisionMultiplier);
        }
    }
}
