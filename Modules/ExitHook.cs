using System;
using UnityEngine;

namespace EndKnot.Modules;

// 番犬 (WatchdogLauncher) と相棒アプリ (CompanionLauncher) を畳むための出口フック。
//
// 以前はこの呼び出しが ClientControlGUI.OnApplicationQuit にしか無かったが、あのコンポーネントは
// 実行時に破棄されうる (ClientOptionsPatch: ShowClientControlGUI を OFF / SwitchVanilla の Unload)。
// 破棄後にホストが × ボタンで終了すると stop-flag が書かれず、番犬が「AU が落ちた」と誤認して
// 蘇生させる無限ループになる。
//
// このコンポーネントは Main.Load で 1 度だけ足し、途中で破棄する経路を持たない。
// OnDestroy も拾うのは保険 (明示 Destroy されたら畳む)。モッドごとアンロードされる SwitchVanilla は
// BasePlugin.Unload() がコンポーネントを破棄するとは限らないので、そちらは FireNow() を明示的に
// 呼んでもらう (アンロード後は番犬を管理できないので、そこで畳むのが正しい)。
// クラッシュ/強制終了ではどちらのコールバックも呼ばれないので、番犬は従来どおり立て直す。
public class ExitHook : MonoBehaviour
{
    private static bool _fired;

    private void OnApplicationQuit()
    {
        Fire("quit");
    }

    private void OnDestroy()
    {
        Fire("destroy");
    }

    // コンポーネントの破棄に頼れない経路 (モッドのアンロード等) から明示的に畳むための入口。
    public static void FireNow(string reason)
    {
        Fire(reason);
    }

    private static void Fire(string reason)
    {
        if (_fired) return;
        _fired = true;

        try
        {
            Logger.Info($"Exit hook fired ({reason}); stopping watchdog / companion", "ExitHook");
            WatchdogLauncher.OnGameQuit();

            // 相棒アプリ (AI実況) は AU の子プロセスなので、ここで明示的に畳む。
            Companion.CompanionLauncher.OnGameQuit();
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }
}
