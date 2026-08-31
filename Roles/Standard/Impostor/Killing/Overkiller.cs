using System.Collections.Generic;
using EndKnot.Modules;

namespace EndKnot.Roles;

// ⚠️ DEPRECATED / TOMBSTONE — 復活させないこと。
// Overkiller と Butcher は同一役職。上流が Overkiller→Butcher へ改名したのを追いきれず、
// このフォークが旧名のまま独自保持したうえ後日 Butcher も取り込んで二重移植してしまった残骸。
// Butcher が正規版。Overkiller だけ偽死体バーストが SendOption.None + rate-gate バイパスで、
// 公式鯖に host を reason=Hacking で自己DC させる真因だった。
// Butcher に一本化するため Overkiller を無効化する。
//
// enum スロット CustomRoles.Overkiller は残す — ID は永続・全クライアント同期で、消すと後続が全ズレする。
// SetupCustomOption を空にすることでメニュー非表示＋選出プールから除外される
// (GetRoleSpawnMode が未登録役職に 0 を返し、CustomRoleSelector が chance==0 をスキップするため)。
// 危険な OnCheckMurder/偽死体コルーチンは撤去済み — 万一デバッグ等で強制付与されても無害な素のキラー。
// OverkillerDeadPlayerList は PlayerControlPatch / OnGameStartedPatch が参照するため残置 (常に空)。
internal class Overkiller : RoleBase
{
    public static bool On;

    public static List<byte> OverkillerDeadPlayerList = [];
    public override bool IsEnable => On;

    public override void SetupCustomOption() { }

    public override void Add(byte playerId)
    {
        On = true;
    }

    public override void Init()
    {
        On = false;
    }
}
