using UnityEngine;

namespace EndKnot.Modules.Ekm;

// EKR logic 契約 v1.3 の field (crowd-control) 実体用テキスト CNO。
// 見た目・生成戦略は EkrCno と同じ (Shapeshift-text 戦略・<size=N> 絶対値のみ) だが、
// EkrCno は sealed かつ「OnMeeting() を意図的に override しない」
// (会議明け自動復活エンジンに乗る) 契約を持つクラスなので、field 専用に別クラスを起こす — field は
// 「持続終了・会議開始・ホルダー死亡で実体ごと消滅」で、会議明け自動復活エンジンには乗せない
// (spec §3 v1.3・EkrDummyCno.OnMeeting 空 override と同型の方針)。
internal sealed class EkrFieldCno : CustomNetObject, IEkrSlotCno
{
    private readonly string _sprite;

    public bool IsInstantiated => playerControl;

    public EkrFieldCno(Vector2 position, string colorHex)
    {
        // 見た目は実装裁量 (spec §3)。EkrCno の size マッピング (8..56) の中間値相当を固定で使う。
        _sprite = $"<size=32><color={colorHex}>◉</color></size>";
        CreateNetObject(_sprite, position);
    }

    // spec §3 v1.3: field は会議明け自動復活エンジンに乗せない — EkrManager が会議開始と同時に
    // 一括で Despawn する (EkrDummyCno.OnMeeting 空 override と同じ方針・二重管理防止)。
    public override void OnMeeting() { }

    // field opcode に cno_move は存在しないため呼ばれない想定 (IEkrSlotCno の実装として no-op にしておく)。
    public void MoveToOffset(float dx, float dy) { }

    void IEkrSlotCno.Despawn() => Despawn();
}
