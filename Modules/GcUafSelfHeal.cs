using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using TMPro;
using Twitch;
using UnityEngine;
using UnityEngine.Events;

namespace EndKnot.Modules;

// GC use-after-free (coreclr AV) の自己修復オーケストレータ (docs/coreclr-av-chat-uaf-resume.md §1e が正典)。
// 起動時に GcUafProbe.Probe() で VULNERABLE (interop フィールドに write barrier が無い状態) を検知したら
// BepInEx.cfg の ScanMethodRefs を BepInEx.Configuration.ConfigFile.CoreConfig 経由で true にし、生成済み
// interop の hash マーカーを消して次回起動で barrier 付き interop を再生成させる。
//
// 世代 ID (gen) = interop/assembly-hash.txt の「中身 + 最終更新時刻」の複合文字列。中身だけだと
// 再生成後も同じ値になり得て世代が区別できないため、mtime を混ぜて「このファイルは前回と同一実体か」を判定する。
// マーカーは state=SAFE|HEALED|GAVE_UP / gen=<世代ID> / attempts=N の3行。
//   - SAFE   : この世代は安全確認済み → 次回以降 gen 一致なら probe すら回さない。
//   - HEALED : 修復を試みた (成功したかはまだ未確認、次回起動の probe で判定)。
//   - GAVE_UP: 2 回修復してもなお VULNERABLE → これ以上は自動修復せず、gen 一致なら probe もスキップ。
// attempts は gen の一致を問わず読む (HEALED からの持ち越しを許す) — cfg 書換や hash 削除が途中で失敗しても
// カウントだけは必ず進むようにし、無限リトライにならないことを保証する。
public static class GcUafSelfHeal
{
    private const string MarkerFileName = "EndKnot.GcUafStatus.txt";
    private const int MaxHealAttempts = 2;

    // 検証用 (実際の書換は CoreConfig 経由。ディスクへ反映されたかの確認だけに使う)。
    private static readonly Regex ScanMethodRefsLine = new(@"^\s*ScanMethodRefs\s*=", RegexOptions.Compiled);

    private static bool _done;
    private static GenericPopup _popup;

    // HealthLog のセッション開始行用: マーカーファイルを直接読む (RunOnce は起動 3 秒後の LateTask なので、
    // それより早く走る HealthLog の EnsureInit からはインメモリ state を参照できない — ファイル読みが正)。
    public static string GetMarkerState()
    {
        try
        {
            string markerPath = Path.Combine(Paths.ConfigPath, MarkerFileName);
            Dictionary<string, string> marker = ReadMarker(markerPath);
            return marker.TryGetValue("state", out string state) ? state : "none";
        }
        catch { return "none"; }
    }

    public static void RunOnce()
    {
        if (_done) return;
        _done = true;

        try
        {
            string markerPath = Path.Combine(Paths.ConfigPath, MarkerFileName);
            string hashPath = Path.Combine(Paths.BepInExRootPath, "interop", "assembly-hash.txt");
            string currentGen = ComputeGen(hashPath);
            Dictionary<string, string> marker = ReadMarker(markerPath);

            bool sameGen = marker.TryGetValue("gen", out string markerGen) && markerGen == currentGen;
            string markerState = marker.TryGetValue("state", out string ms) ? ms : null;

            if (sameGen && markerState is "SAFE" or "GAVE_UP")
                return; // 判定済み世代と一致 → probe すら回さない (通常時オーバーヘッドゼロ)。

            int? collected = GcUafProbe.Probe();
            if (collected == null)
            {
                Logger.Error("GcUafSelfHeal: probe failed, skipping this boot", "GcUafSelfHeal");
                return;
            }

            if (collected == 0)
            {
                WriteMarker(markerPath, currentGen, "SAFE", 0);
                return;
            }

            // VULNERABLE — attempts は gen 不問で読む (HEALED からの持ち越しを許して確実に頭打ちさせる)。
            int attempts = 0;
            if (marker.TryGetValue("attempts", out string attemptsStr) && int.TryParse(attemptsStr, out int parsed))
                attempts = parsed;

            if (attempts >= MaxHealAttempts)
            {
                WriteMarker(markerPath, currentGen, "GAVE_UP", attempts);
                Logger.Error($"GCUAF-SELFHEAL: still VULNERABLE after {attempts} heal attempts — giving up, manual fix needed", "GcUafSelfHeal");
                ShowPopup(Translator.GetString("GcUafHeal.FailedBody"));
                return;
            }

            // ファイル操作より先にマーカーを書く — 以降どのステップで失敗/例外になっても試行回数だけは残る。
            WriteMarker(markerPath, currentGen, "HEALED", attempts + 1);

            if (Heal(hashPath)) ShowPopup(Translator.GetString("GcUafHeal.AppliedBody"));
        }
        catch (Exception e)
        {
            Logger.Error($"GcUafSelfHeal failed: {e.Message}", "GcUafSelfHeal");
        }
    }

    // cfg 書換 (CoreConfig 経由) + interop hash 削除。マーカーは呼び出し元 (RunOnce) が既に書き終えている。
    private static bool Heal(string hashPath)
    {
        try
        {
            // BepInEx は起動時に読み込んだ ConfigFile.CoreConfig をメモリ上の真実源として保持し、
            // どこかで Save() が走るとメモリの値でディスクを上書きする。File 直書きだと後発の Save() で
            // 消されるため、CoreConfig.Bind 経由で値そのものを書き換える (SaveOnConfigSet=true で即反映)。
            // 既存エントリがあれば Bind は同一インスタンスを返す (defaultValue は無視される)。
            ConfigEntry<bool> entry = ConfigFile.CoreConfig.Bind("IL2CPP", "ScanMethodRefs", true);
            entry.Value = true;

            string cfgPath = Paths.BepInExConfigPath;
            bool verified = false;
            if (File.Exists(cfgPath))
                foreach (string line in File.ReadAllLines(cfgPath))
                    if (ScanMethodRefsLine.IsMatch(line) && line.Contains("true", StringComparison.OrdinalIgnoreCase))
                        verified = true;

            if (!verified)
            {
                Logger.Error($"GcUafSelfHeal: verification of ScanMethodRefs=true failed on disk ({cfgPath})", "GcUafSelfHeal");
                return false;
            }

            try { if (File.Exists(hashPath)) File.Delete(hashPath); }
            catch (Exception e)
            {
                Logger.Error($"GcUafSelfHeal: failed to delete interop hash marker: {e.Message}", "GcUafSelfHeal");
                return false;
            }

            Logger.Warn("GCUAF-SELFHEAL: applied fix via CoreConfig (ScanMethodRefs=true, interop hash cleared for regen)", "GcUafSelfHeal");
            return true;
        }
        catch (Exception e)
        {
            Logger.Error($"GcUafSelfHeal: heal failed: {e.Message}", "GcUafSelfHeal");
            return false;
        }
    }

    // 世代 ID = ファイルの中身 + 最終更新時刻。中身だけだと再生成後も同一になり得るため mtime で世代を分ける。
    private static string ComputeGen(string hashPath)
    {
        try
        {
            if (!File.Exists(hashPath)) return "missing";
            string content = File.ReadAllText(hashPath).Trim();
            long mtimeTicks = File.GetLastWriteTimeUtc(hashPath).Ticks;
            return $"{content}:{mtimeTicks}";
        }
        catch { return "missing"; }
    }

    private static Dictionary<string, string> ReadMarker(string path)
    {
        var dict = new Dictionary<string, string>();
        try
        {
            if (!File.Exists(path)) return dict;
            foreach (string line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                dict[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }
        catch { }

        return dict;
    }

    private static void WriteMarker(string path, string gen, string state, int attempts)
    {
        try
        {
            File.WriteAllLines(path, new[]
            {
                $"gen={gen}",
                $"state={state}",
                $"attempts={attempts}"
            });
        }
        catch (Exception e) { Logger.Error($"GcUafSelfHeal: failed writing status marker: {e.Message}", "GcUafSelfHeal"); }
    }

    // ModUpdater.ShowPopup と同じ GenericPopup パターンの自前版 (StreamerMode.cs の初回オンボーディング
    // ポップアップを複製 — 既存コードは触らない)。更新ポップアップの保留中は割り込まず少し待つ。
    private static void ShowPopup(string body, int tries = 0)
    {
        if (ModUpdater.UpdatePopupPending && tries < 20)
        {
            LateTask.New(() => ShowPopup(body, tries + 1), 1f, "GcUafSelfHeal.PopupWait", log: false);
            return;
        }

        try
        {
            // probe + 更新ポップアップ待ちで最大 ~25 秒遅延しうる。その間にユーザーがロビーへ進んでいたら
            // メインメニューはもう存在しない — 出さずスキップ (AutoRehost.cs / BGMInfoDisplay.cs と同じ判定)。
            if (Object.FindObjectOfType<MainMenuManager>() == null) return;
            if (TwitchManager.Instance == null) return;

            if (_popup == null)
            {
                _popup = Object.Instantiate(TwitchManager.Instance.TwitchPopup);
                _popup.name = "GcUafHealPopup";
            }

            _popup.Show(body);
            Transform button = _popup.transform.FindChild("ExitGame");
            if (!button) return;

            button.gameObject.SetActive(true);
            var tt = button.GetChild(0).GetComponent<TextTranslatorTMP>();
            if (tt)
            {
                tt.TargetText = StringNames.Close;
                tt.ResetText();
            }

            var pb = button.GetComponent<PassiveButton>();
            pb.OnClick = new();
            GenericPopup popup = _popup;
            pb.OnClick.AddListener((UnityAction)(() => popup.Close()));
        }
        catch (Exception e) { Logger.Error($"GcUafSelfHeal: popup failed: {e.Message}", "GcUafSelfHeal"); }
    }
}
