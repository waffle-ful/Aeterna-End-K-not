using System;
using System.Collections.Generic;
using UnityEngine;

namespace EndKnot.Modules;

// 常駐する IMGUI 部品をまとめて 1 つの OnGUI から描き、どの部品も描かない間は OnGUI ごと止める。
// OnGUI を持つ有効な MonoBehaviour が 1 つでもあると、描く物が無くても本編の IMGUI の処理が毎フレーム走り
// 約 0.06ms/フレームかかる (2026-10-25 実測: 1 つでも 8 つでもほぼ同じ)。
// 部品は自分の OnGUI を持たず、Awake で描画メソッドと「今描くか」の判定を登録する。判定は 1 秒に 10 回
// (Tick) と、部品が表示を切り替えた時 (Refresh) に読む。描く順は登録順 (= AddComponent の順) で、
// 操作の多い部品を先に登録すると、後ろの部品が出たり消えたりしても先の部品のコントロール ID がずれない。
public class ImguiHost : MonoBehaviour
{
    private const float PollSeconds = 0.1f;

    private static readonly List<(MonoBehaviour Owner, Action Draw, Func<bool> Wants)> Entries = new(8);
    private static readonly Color White = new(1f, 1f, 1f, 1f);
    private static ImguiHost _instance;
    private static float _nextPoll;
    private static bool _on = true;

    public static void Add(MonoBehaviour owner, Action draw, Func<bool> wants)
    {
        Entries.Add((owner, draw, wants));
        Refresh();
    }

    // ModManager.LateUpdate (全シーンで毎フレーム) から呼ぶ。
    public static void Tick()
    {
        float now = Time.unscaledTime;
        if (now < _nextPoll) return;
        _nextPoll = now + PollSeconds;
        Refresh();
    }

    public static void Refresh()
    {
        if (!Il2Direct.Alive(_instance)) return;

        var any = false;

        for (var i = 0; i < Entries.Count; i++)
        {
            (MonoBehaviour owner, _, Func<bool> wants) = Entries[i];

            if (!Il2Direct.Alive(owner))
            {
                Entries.RemoveAt(i--);
                continue;
            }

            // 無効にされた部品は描かない (部品ごとの OnGUI だった頃に本編がしていたのと同じ)
            if (!owner.isActiveAndEnabled) continue;

            // 判定が投げる部品は描く側に回す (描画側の例外は OnGUI がログに出す)
            try { any |= wants(); }
            catch { any = true; }
        }

        if (any == _on) return;
        _on = any;
        _instance.enabled = any;
    }

    private void Awake()
    {
        _instance = this;
        _on = true;
        ImguiNoLayout.Apply(this);
    }

    private void OnGUI()
    {
        for (var i = 0; i < Entries.Count; i++)
        {
            (MonoBehaviour owner, Action draw, _) = Entries[i];

            if (!Il2Direct.Alive(owner))
            {
                Entries.RemoveAt(i--);
                continue;
            }

            if (!owner.isActiveAndEnabled) continue;

            try { draw(); }
            catch (Exception e) { Utils.ThrowException(e); }

            // 部品ごとの OnGUI だった頃は本編が呼び出しのたびに色を戻していたので、次の部品へ持ち越さない
            GUI.color = White;
        }
    }
}
