namespace EndKnot.Roles;

// EKN 役職メーカー R0 の10予約スロット。中身は EkmTemplateRole に集約済み。
// SetupCustomOption だけは各スロットで実装する (check-option-ids.ps1 が id リテラルと
// 呼び出しの同一ファイル内一致を前提にしているため)。
// id ブロック: 708000 + 50*スロット番号(0-9)、docs/role-migration-log.md 2026-08-08 予約分。
// 未束縛スロットはオプションメニューに出さない (EkrManager.Bind/Unbind が表示と出現率を制御し、
// Options.GetRoleSpawnMode のガードが選出を封じる)。
internal sealed class EkmCustomRole1 : EkmTemplateRole
{
    private const int Id = 708000;
    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, Slot);
        HideUntilBound();
    }
}

internal sealed class EkmCustomRole2 : EkmTemplateRole
{
    private const int Id = 708050;
    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, Slot);
        HideUntilBound();
    }
}

internal sealed class EkmCustomRole3 : EkmTemplateRole
{
    private const int Id = 708100;
    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, Slot);
        HideUntilBound();
    }
}

internal sealed class EkmCustomRole4 : EkmTemplateRole
{
    private const int Id = 708150;
    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, Slot);
        HideUntilBound();
    }
}

internal sealed class EkmCustomRole5 : EkmTemplateRole
{
    private const int Id = 708200;
    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, Slot);
        HideUntilBound();
    }
}

internal sealed class EkmCustomRole6 : EkmTemplateRole
{
    private const int Id = 708250;
    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, Slot);
        HideUntilBound();
    }
}

internal sealed class EkmCustomRole7 : EkmTemplateRole
{
    private const int Id = 708300;
    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, Slot);
        HideUntilBound();
    }
}

internal sealed class EkmCustomRole8 : EkmTemplateRole
{
    private const int Id = 708350;
    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, Slot);
        HideUntilBound();
    }
}

internal sealed class EkmCustomRole9 : EkmTemplateRole
{
    private const int Id = 708400;
    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, Slot);
        HideUntilBound();
    }
}

internal sealed class EkmCustomRole10 : EkmTemplateRole
{
    private const int Id = 708450;
    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, Slot);
        HideUntilBound();
    }
}

// ── R2 (docs/ekn-r2-contract.md §2): 陣営別スロット ────────────────────────────────────
// 上の10スロットはクルー専用のまま (オプションのタブは構築時に固定されるので、束縛後に陣営を
// 変えることはできない)。陣営ごとに別スロットを用意して、束縛時に役職コードの team と突き合わせる。
// id ブロック: 709000 + 50*n (708500〜708999 は埋込出荷役職用に空けてある)。

internal sealed class EkmImpRole1 : EkmTemplateRole
{
    private const int Id = 709000;
    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.ImpostorRoles, Slot);
        HideUntilBound();
    }
}

internal sealed class EkmImpRole2 : EkmTemplateRole
{
    private const int Id = 709050;
    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.ImpostorRoles, Slot);
        HideUntilBound();
    }
}

internal sealed class EkmImpRole3 : EkmTemplateRole
{
    private const int Id = 709100;
    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.ImpostorRoles, Slot);
        HideUntilBound();
    }
}

internal sealed class EkmNeuRole1 : EkmTemplateRole
{
    private const int Id = 709150;
    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.NeutralRoles, Slot);
        HideUntilBound();
    }
}

internal sealed class EkmNeuRole2 : EkmTemplateRole
{
    private const int Id = 709200;
    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.NeutralRoles, Slot);
        HideUntilBound();
    }
}

internal sealed class EkmNeuRole3 : EkmTemplateRole
{
    private const int Id = 709250;
    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.NeutralRoles, Slot);
        HideUntilBound();
    }
}

internal sealed class EkmNeuRole4 : EkmTemplateRole
{
    private const int Id = 709300;
    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.NeutralRoles, Slot);
        HideUntilBound();
    }
}

internal sealed class EkmNeuRole5 : EkmTemplateRole
{
    private const int Id = 709350;
    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.NeutralRoles, Slot);
        HideUntilBound();
    }
}
