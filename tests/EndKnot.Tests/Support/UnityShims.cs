// テスト専用シム — Modules/Ekm/EkmLogicRuntime.cs が触る唯一の Unity 面。
// 用途は fiber の wait 判定 (realtimeSinceStartup) とフレーム境界検出 (frameCount) だけで、
// テスト対象の検証経路 (EkrDefinition.TryParse = AST を組むだけ) では一切読まれない。
// GameShims.cs とファイルを分けているのは、C# が同一ファイル内での
// file-scoped namespace と block-scoped namespace の混在を許さないため。

namespace UnityEngine;

internal static class Time
{
    public static float realtimeSinceStartup => 0f;
    public static int frameCount => 0;
}
