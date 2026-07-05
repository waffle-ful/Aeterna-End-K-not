using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EndKnot.Modules;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace EndKnot.Patches;

// 全ポップアップ/メッセージ捕捉レイヤー。
// 「ログに拾えないメッセージがある」= 検知の穴。EOS 認証失敗 (「Among Us サーバーで認証できませんでした…」=
// StringNames.ErrorNotAuthenticated) は DisconnectReasons を伴わず GenericPopup だけで出るため、
// 切断理由ベースの検知では一切拾えなかった。ここで GenericPopup / DisconnectPopup の表示テキストを
// 署名非依存で読み取り (最長 TMP テキスト=本文。短いボタンラベルを誤取得しない)、HealthLog に MSG 行として残す。
// 認証失敗パターンなら AutoRestart へ回して番犬経由のプロセス再起動で復帰する。
internal static class MessageCapture
{
    private static string _lastText;
    private static long _lastTextTs;

    public static void Capture(string source, string text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            string flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
            if (flat.Length == 0) return;

            // 同一テキストの短時間重複を抑制 (ポップアップの再表示や複数フック経由の二重捕捉)。
            long now = Utils.TimeStamp;
            if (flat == _lastText && now - _lastTextTs < 3) return;
            _lastText = flat;
            _lastTextTs = now;

            HealthLog.RecordMessage(source, flat);

            if (LooksLikeAuthFailure(flat))
                AutoRestart.OnAuthFailureMessage(source, flat);
        }
        catch { }
    }

    // EN + JA の「オンライン認証不能」フレーズ。誤爆を避けるため authentication/認証 系に限定する。
    // (通常プレイ中に出る他ポップアップで再起動が誤発火しないよう、AutoRestart 側でも option/uptime/番犬 で多重ガード済み)
    private static bool LooksLikeAuthFailure(string text)
    {
        string lower = text.ToLowerInvariant();
        return lower.Contains("authenticate") || lower.Contains("not authorized")
            || text.Contains("認証") || text.Contains("アカウントに接続");
    }

    // 子 TMP の中で最長のテキストを返す (本文はボタンラベルより長い前提)。TextMeshPro / TextMeshProUGUI 両対応。
    private static string ReadLongestText(Component root)
    {
        string best = null;

        try
        {
            foreach (TextMeshPro t in root.GetComponentsInChildren<TextMeshPro>(true))
                if (!string.IsNullOrEmpty(t.text) && (best == null || t.text.Length > best.Length)) best = t.text;
        }
        catch { }

        try
        {
            foreach (TextMeshProUGUI t in root.GetComponentsInChildren<TextMeshProUGUI>(true))
                if (!string.IsNullOrEmpty(t.text) && (best == null || t.text.Length > best.Length)) best = t.text;
        }
        catch { }

        return best;
    }

    // GenericPopup.Show の全オーバーロードを Postfix (署名非依存)。認証エラー・更新通知・各種メッセージがここを通る。
    [HarmonyPatch]
    internal static class GenericPopupShowPatch
    {
        // ReSharper disable once UnusedMember.Local
        private static IEnumerable<MethodBase> TargetMethods() =>
            typeof(GenericPopup).GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name == "Show");

        // ReSharper disable once UnusedMember.Local
        private static void Postfix(GenericPopup __instance)
        {
            if (__instance == null) return;
            MessageCapture.Capture("GenericPopup", ReadLongestText(__instance));
        }
    }

    // DisconnectPopup.DoShow を Postfix。切断/kick/エラー系の表示文言を捕捉 (理由ベースの DC 行を補完)。
    [HarmonyPatch(typeof(DisconnectPopup), nameof(DisconnectPopup.DoShow))]
    internal static class DisconnectPopupShowPatch
    {
        // ReSharper disable once UnusedMember.Local
        private static void Postfix(DisconnectPopup __instance)
        {
            if (__instance == null) return;
            MessageCapture.Capture("DisconnectPopup", ReadLongestText(__instance));
        }
    }
}
