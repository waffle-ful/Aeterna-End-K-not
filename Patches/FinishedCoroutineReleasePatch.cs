using System.Collections;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using HarmonyLib;

namespace EndKnot;

// マネージドの IEnumerator を StartCoroutine に渡すと Il2CppManagedEnumerator で包まれ、その包みは
// コルーチンが終わった後も strong GCHandle で生き残る。包みが中身の IEnumerator を握ったままだと、
// 状態機械が閉包ごと掴んでいたもの (前の試合の HudManager / GameStartManager / EndGameManager /
// IntroCutscene と、そこから辿れる il2cpp オブジェクト一式) が回収されず、試合のたびに積み上がる
// (2026-10-14 実測: 回収後の il2cpp ヒープが 1 試合 +4MB で単調増・HudManager が試合数ぶん生存)。
// 包みを作る時点で中身を 1 枚挟み、終わったら中身を手放させる。残る包みは数十バイトの殻だけになる。
// MoveNext 側へのパッチは il2cpp からの呼び出し口を素通りするので、管理コードから呼ばれる生成側で挟む。
[HarmonyPatch(typeof(Il2CppManagedEnumerator), MethodType.Constructor, typeof(IEnumerator))]
internal static class FinishedCoroutineReleasePatch
{
    private static bool _logged;

    internal static bool Prepare() => Main.ReleaseFinishedCoroutines.Value;

    public static void Prefix(ref IEnumerator __0)
    {
        if (__0 is null or ReleasingEnumerator) return;
        __0 = new ReleasingEnumerator(__0);
    }

    private sealed class ReleasingEnumerator(IEnumerator inner) : IEnumerator
    {
        private IEnumerator _inner = inner;

        public object Current => _inner?.Current;

        public bool MoveNext()
        {
            if (_inner == null) return false;

            try
            {
                if (_inner.MoveNext()) return true;
            }
            catch
            {
                // 例外で止まったコルーチンも Unity からは二度と呼ばれないので、ここで手放す
                _inner = null;
                throw;
            }

            _inner = null;

            if (!_logged)
            {
                _logged = true;
                Logger.Info("first finished coroutine released", "CoroutineRelease");
            }

            return false;
        }

        public void Reset() => _inner?.Reset();
    }
}
