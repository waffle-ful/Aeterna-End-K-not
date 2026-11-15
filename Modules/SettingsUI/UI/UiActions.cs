// Based on https://github.com/haru03741-glitch/TownOfHost_L Modules/SettingsMenu/UI/UiActions.cs (GPL-3.0),
// which is based on https://github.com/haru03741-glitch/More-Roles-Plus src/UI/UiActions.cs (GPL-3.0)
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace EndKnot.SettingsUI;

// ボタンに渡す処理の受け口。il2cpp 側へ渡す UnityAction は番号ごとに 1 つだけ作って使い回し、
// 押された時は番号から今の処理を引く (渡すたびに作ると解放されない参照が増え、
// 処理が画面の部品を捕まえていると閉じた後も残るため)。番号は画面を作り直す時にまとめて返す。
internal static class UiActions
{
    private static readonly List<Action> Handlers = new();
    private static readonly List<UnityAction> Actions = new();
    private static readonly Stack<int> Free = new();
    // 返した番号は次のフレームまで使わない。捨てたボタンは Destroy がフレームの終わりまで遅れるので、
    // 同じフレームで番号を使い回すと、そのボタンに届いた押した・外れたが新しい部品の処理を走らせてしまう
    private static readonly List<int> Returned = new();
    private static int _returnedFrame = -1;

    // handler を番号に結び、その番号の UnityAction を返す。番号は owner に控える
    public static UnityAction Take(Action handler, List<int> owner)
    {
        int i;
        if (Returned.Count > 0 && Time.frameCount > _returnedFrame)
        {
            foreach (int r in Returned) Free.Push(r);
            Returned.Clear();
        }
        if (Free.Count > 0) i = Free.Pop();
        else
        {
            i = Handlers.Count;
            Handlers.Add(null);
            int k = i;
            Actions.Add((UnityAction)(() => Run(k)));
        }
        Handlers[i] = handler;
        owner.Add(i);
        return Actions[i];
    }

    public static void Release(List<int> owner)
    {
        foreach (int i in owner)
        {
            if (Handlers[i] == null)
            {
                Logger.Warn($"ui action: slot {i} released twice", "UiActions");
                continue;
            }
            Handlers[i] = null;
            Returned.Add(i);
        }
        owner.Clear();
        _returnedFrame = Time.frameCount;
    }

    private static void Run(int i)
    {
        var h = i < Handlers.Count ? Handlers[i] : null;
        if (h == null) return;
        try { h(); }
        catch (Exception e) { Logger.Error($"ui action: {e}", "UiActions"); }
    }
}
