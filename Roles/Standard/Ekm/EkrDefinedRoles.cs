namespace EndKnot.Roles;

// EKR 埋込出荷役職 (役職メーカーで開発して本体へ同梱するレーン)。
// 1役職 = ①CustomRoles enum エントリ (末尾追記) ②ここに EkmTemplateRole 派生の薄いクラス
// ③Resources/EkRoles/<EnumName>.ekrole.json (DLL 埋込・EkrManager.LoadEmbeddedRoles が起動時に束縛)
// ④Lang の保険キー (<EnumName>/Info/InfoLong — 表示は定義の name/color が勝つ)。
// SetupCustomOption は各クラスで実装する (check-option-ids.ps1 が id リテラルと呼び出しの
// 同一ファイル内一致を前提にしているため — EkmCustomRoleSlots.cs と同じ規約)。
// ユーザースロットと違い HideUntilBound() は呼ばない — 起動時束縛で常にメニューに出る。
// id ブロック: 708500 から 50 刻み (708000-708450 はユーザースロット10個が予約済み)。
internal sealed class EkrShowcase : EkmTemplateRole
{
    private const int Id = 708500;
    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, Slot);
    }
}
