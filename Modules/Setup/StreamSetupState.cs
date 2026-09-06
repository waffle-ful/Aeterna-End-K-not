using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using EndKnot.Modules.Companion;

namespace EndKnot.Modules.Setup;

// StreamSetupGUI (Modules/Setup/StreamSetupGUI.cs) が描くだけの状態を持つクラス。
// 検出・保存・疎通確認はすべて重い処理 (プロセス起動 / レジストリ書込 / HTTP) を伴うため、
// 呼び出しはすべて別スレッドへ逃がし、結果をここの volatile フィールドへ書き戻す。
//
// Translator.GetString は IL2CPP 側の TranslationController シングルトンに触るため
// メインスレッド専用。ここのワーカーは言語非依存の状態 (enum + 数値/バージョン文字列) だけを
// 書き込み、実際の文言解決は StreamSetupGUI がメインスレッドの Update で Dirty フラグを見て行う。
internal static class StreamSetupState
{
    internal enum PythonState { Unknown, Bundled, Ok, TooOld, NotFound }
    internal enum KeyState { Unknown, NotSet, Set }
    internal enum KeyErrorKind { None, ClipboardInvalid, SaveFailed }
    internal enum KeyVerifyKind { None, Ok, Invalid, Unknown, NoKey }
    internal enum VoiceVoxState { Unknown, Ok, NotRunning }

    // メインスレッドがこのフラグを見て、下の状態から表示文字列を組み直す (組み直したら false に戻す)。
    internal static volatile bool Dirty = true;

    // ── Python ──
    internal static volatile PythonState PythonStatus = PythonState.Unknown;
    internal static volatile string PythonVersion = ""; // 言語非依存 (例 "3.12.4")
    internal static volatile bool PythonBusy;

    // ── Gemini API キー ──
    internal static volatile KeyState KeyStatus = KeyState.Unknown;
    internal static volatile string KeyMasked = ""; // 言語非依存 (例 "AIza…xyz")
    internal static volatile string KeyScope = "";
    internal static volatile bool KeyElsewhereHint;
    internal static volatile KeyErrorKind KeyError = KeyErrorKind.None;
    internal static volatile KeyVerifyKind KeyVerifyResult = KeyVerifyKind.None;
    internal static volatile bool KeySaving;
    internal static volatile bool KeyVerifying;

    // ── VOICEVOX ──
    internal static volatile VoiceVoxState VoiceVoxStatus = VoiceVoxState.Unknown;
    internal static volatile string VoiceVoxVersion = ""; // 言語非依存
    internal static volatile bool VoiceVoxBusy;

    // メインスレッド (StreamSetupGUI.Update) が消費するクリップボードクリア要求。
    // 保存ワーカーはバックグラウンドスレッドで動くため、Unity API 呼び出しをここへ持ち越す。
    internal static volatile bool ClipboardClearPending;

    private static readonly HttpClient GeminiClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly HttpClient VoiceVoxClient = new() { Timeout = TimeSpan.FromSeconds(2) };

    // AIza で始まり全体 34〜49 文字 (プレフィックス4 + 本体30〜45) の Gemini API キー形式。
    private static readonly Regex KeyPattern = new(@"^AIza[A-Za-z0-9_\-]{30,45}$", RegexOptions.Compiled);

    // ウィンドウを開いた時・保存が成功した時に呼ぶ。Python / キー / VOICEVOX をまとめて再検出する。
    internal static void Refresh()
    {
        new Thread(RefreshWorker) { IsBackground = true, Name = "EndKnotStreamSetupRefresh" }.Start();
    }

    private static void RefreshWorker()
    {
        try { RefreshPython(); }
        catch (Exception e) { Logger.Warn($"Python detect failed: {e.Message}", "StreamSetupState"); }

        try { RefreshKey(); }
        catch (Exception e) { Logger.Warn($"Key detect failed: {e.Message}", "StreamSetupState"); }

        try { RefreshVoiceVox(); }
        catch (Exception e) { Logger.Warn($"VoiceVox detect failed: {e.Message}", "StreamSetupState"); }

        Dirty = true;
    }

    // ── Python ──

    private static void RefreshPython()
    {
        PythonBusy = true;
        try
        {
            // 将来の同梱 Python (companion-onboarding-plan.md A2 (B-1)) の置き場所。存在すればそれを
            // 使う前提なので、実行して確かめるまでもなく検出済み扱いにする。
            string bundled = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Main.DataPath, "EndKnot_DATA", "companion", "python", "python.exe"));

            if (System.IO.File.Exists(bundled))
            {
                PythonStatus = PythonState.Bundled;
                PythonVersion = "";
                return;
            }

            // Microsoft Store のエイリアス版 python.exe は --version を実行しても起動せず exit code
            // が 0 にならない (Store が開くだけの偽物) ので、実際に通るかどうかで判定する。
            if (TryRunVersion("python", "--version", out string output) ||
                TryRunVersion("py", "-3 --version", out output))
            {
                Match m = Regex.Match(output, @"Python (\d+)\.(\d+)(\.(\d+))?");
                if (m.Success)
                {
                    int major = int.Parse(m.Groups[1].Value);
                    int minor = int.Parse(m.Groups[2].Value);
                    PythonVersion = m.Groups[3].Success ? $"{major}.{minor}{m.Groups[3].Value}" : $"{major}.{minor}";
                    PythonStatus = major > 3 || (major == 3 && minor >= 10) ? PythonState.Ok : PythonState.TooOld;
                    return;
                }
            }

            PythonStatus = PythonState.NotFound;
            PythonVersion = "";
        }
        finally { PythonBusy = false; }
    }

    // python --version / py -3 --version を無ウィンドウで実行し、標準出力+標準エラーをまとめて返す。
    // 出力は1行のみで巨大化しないため、WaitForExit 後にまとめて読んでもパイプ詰まりの心配はない。
    private static bool TryRunVersion(string fileName, string arguments, out string output)
    {
        output = "";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var p = Process.Start(psi);
            if (p == null) return false;

            if (!p.WaitForExit(5000))
            {
                try { p.Kill(); }
                catch { }

                return false;
            }

            output = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
            return p.ExitCode == 0 && output.Length > 0;
        }
        catch { return false; }
    }

    // ── Gemini API キー ──

    private static void RefreshKey()
    {
        if (CompanionLauncher.TryGetApiKey(out string key, out string scope))
        {
            KeyStatus = KeyState.Set;
            KeyScope = scope;
            KeyMasked = MaskKey(key);

            bool userHasKey;
            try { userHasKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY", EnvironmentVariableTarget.User)); }
            catch { userHasKey = false; }

            KeyElsewhereHint = (scope == "process" || scope == "machine") && !userHasKey;
        }
        else
        {
            KeyStatus = KeyState.NotSet;
            KeyScope = "";
            KeyMasked = "";
            KeyElsewhereHint = false;
        }
    }

    private static string MaskKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length < 8) return "****";
        return key.Substring(0, 4) + "…" + key.Substring(key.Length - 3, 3);
    }

    // クリップボードの中身は StreamSetupGUI 側 (メインスレッドの OnGUI) で読んで渡す。ここでは
    // 形式検証以降の重い処理 (レジストリ書込・相棒再起動) だけを別スレッドで行う。
    // 保存中の多重クリック (連打・両方の保存系ボタンの同時押下) でワーカーが重複起動しないよう
    // KeySaving を最初にチェックする。
    internal static void SaveKeyFromClipboard(string rawClipboard)
    {
        if (KeySaving) return;

        string candidate = rawClipboard?.Trim() ?? "";
        if (!KeyPattern.IsMatch(candidate))
        {
            KeyError = KeyErrorKind.ClipboardInvalid;
            Dirty = true;
            return;
        }

        KeyError = KeyErrorKind.None;
        KeySaving = true;
        new Thread(() => SaveKeyWorker(candidate)) { IsBackground = true, Name = "EndKnotStreamSetupSaveKey" }.Start();
    }

    private static void SaveKeyWorker(string key)
    {
        try
        {
            // User スコープへの書込は WM_SETTINGCHANGE のブロードキャストで数秒ブロックしうるため、
            // 呼び出し元はこのメソッドを必ずバックグラウンドスレッドから呼ぶこと。
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", key, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", key);
            ClipboardClearPending = true;
            Logger.Info("GEMINI_API_KEY saved to user scope", "StreamSetupState");
            CompanionLauncher.RestartNow();
        }
        catch (Exception e)
        {
            KeyError = KeyErrorKind.SaveFailed;
            Logger.Warn($"SaveKey failed: {e.Message}", "StreamSetupState");
        }
        finally
        {
            KeySaving = false;
            Refresh();
        }
    }

    internal static void VerifyKey()
    {
        if (KeyVerifying) return;

        if (!CompanionLauncher.TryGetApiKey(out string key, out _))
        {
            KeyVerifyResult = KeyVerifyKind.NoKey;
            Dirty = true;
            return;
        }

        KeyVerifying = true;
        new Thread(() => VerifyKeyWorker(key)) { IsBackground = true, Name = "EndKnotStreamSetupVerifyKey" }.Start();
    }

    private static void VerifyKeyWorker(string key)
    {
        try
        {
            // URL にキーを含むため、例外メッセージも含めてこの文字列を一切ログへ出さない。
            string url = $"https://generativelanguage.googleapis.com/v1beta/models?key={key}";
            using HttpResponseMessage resp = GeminiClient.GetAsync(url).GetAwaiter().GetResult();
            int code = (int)resp.StatusCode;

            KeyVerifyResult = code switch
            {
                200 => KeyVerifyKind.Ok,
                400 or 401 or 403 => KeyVerifyKind.Invalid,
                _ => KeyVerifyKind.Unknown
            };
        }
        catch (Exception e)
        {
            KeyVerifyResult = KeyVerifyKind.Unknown;
            Logger.Warn($"VerifyKey network error: {e.GetType().Name}", "StreamSetupState");
        }
        finally
        {
            KeyVerifying = false;
            Dirty = true;
        }
    }

    // ── VOICEVOX ──

    private static void RefreshVoiceVox()
    {
        VoiceVoxBusy = true;
        try
        {
            string baseUrl = (Main.VoiceVoxEngineUrl?.Value ?? "http://127.0.0.1:50021").TrimEnd('/');

            using HttpResponseMessage resp = VoiceVoxClient.GetAsync(baseUrl + "/version").GetAwaiter().GetResult();
            if (resp.IsSuccessStatusCode)
            {
                string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                VoiceVoxStatus = VoiceVoxState.Ok;
                VoiceVoxVersion = Sanitize(body.Trim().Trim('"'));
            }
            else
            {
                VoiceVoxStatus = VoiceVoxState.NotRunning;
                VoiceVoxVersion = "";
            }
        }
        catch
        {
            VoiceVoxStatus = VoiceVoxState.NotRunning;
            VoiceVoxVersion = "";
        }
        finally { VoiceVoxBusy = false; }
    }

    // ローカルエンジンとはいえ応答文字列はプロセス外から来るので、TMP/GUI 表示前に無害化する。
    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("<", "＜").Replace("\n", " ").Replace("\r", "");
    }
}
