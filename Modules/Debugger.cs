using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using EndKnot.Modules;
using EndKnot.Patches;
using UnityEngine;
using LogLevel = BepInEx.Logging.LogLevel;

namespace EndKnot;

internal static class Logger
{
    private static bool IsEnable;
    private static readonly List<string> DisableList = [];
    private static readonly StringBuilder LogText = new();
#if DEBUG
    public static bool IsAlsoInGame;
#endif

    private static readonly Dictionary<string, DateTime> NowDetailedErrorLog = [];

    private static readonly Dictionary<string, int> ExceptionTags = [];

    public static void Enable()
    {
        IsEnable = true;
    }

    /*
        public static void Disable()
        {
            IsEnable = false;
        }

        public static void Enable(string tag, bool toGame = false)
        {
            DisableList.Remove(tag);

            if (toGame && !SendToGameList.Contains(tag))
                SendToGameList.Add(tag);
            else
                SendToGameList.Remove(tag);
        }
    */

    public static void Disable(string tag)
    {
        if (!DisableList.Contains(tag)) DisableList.Add(tag);
    }

    public static void SendInGame(string text, Color? textColor = null)
    {
        if (!IsEnable) return;

        NotificationPopper np = NotificationPopperPatch.Instance;

        if (np)
        {
            Warn(text, "SendInGame");

            LobbyNotificationMessage newMessage = Object.Instantiate(np.notificationMessageOrigin, Vector3.zero, Quaternion.identity, np.transform);
            newMessage.transform.localPosition = new(0f, 0f, -2f);
            text = "<font=\"Barlow-Black SDF\" material=\"Barlow-Black Outline\">" + text + "</font>";
            newMessage.SetUp(text, np.settingsChangeSprite, textColor ?? np.settingsChangeColor, (Action)(() => np.OnMessageDestroy(newMessage)));
            np.ShiftMessages();
            np.AddMessageToQueue(newMessage);

            SoundManager.Instance.PlaySoundImmediate(np.settingsChangeSound, false);
        }
    }

    private static void SendToFile(string text, LogLevel level = LogLevel.Info, string tag = "", bool escapeCRLF = true, int lineNumber = 0, string fileName = "", bool multiLine = false)
    {
        if (!IsEnable || DisableList.Contains(tag) || (level == LogLevel.Debug && !DebugModeManager.AmDebugger)) return;

#if DEBUG
        if (IsAlsoInGame) SendInGame($"[{tag}]{text}");
#endif

        LogText.Clear();
        DateTime now = DateTime.Now;

        TestBridge.RecordLog(level, tag, text); // 全レベルリング(grep/wait marker 用)。ブリッジ OFF 時は即 return する軽量フック

        if (level is LogLevel.Error or LogLevel.Fatal)
        {
            ExceptionTags[tag] = ExceptionTags.GetValueOrDefault(tag) + 1;
            TestBridge.RecordError(tag, text); // ブリッジ OFF 時は即 return する軽量フック
        }

        if (level is LogLevel.Error or LogLevel.Fatal && !multiLine && (!NowDetailedErrorLog.TryGetValue(tag, out DateTime dt) || dt.AddSeconds(3) < now))
        {
            var t = now.ToString("HH:mm:ss");
            StackFrame stack = new(2);
            string className = stack.GetMethod()?.ReflectedType?.Name;
            string memberName = stack.GetMethod()?.Name;
            LogText.Append($"[{t}][{className}.{memberName}({Path.GetFileName(fileName)}:{lineNumber})][{tag}]{text}");
            NowDetailedErrorLog[tag] = now;
        }
        else
        {
            if (escapeCRLF && (text.Contains('\n') || text.Contains('\r')))
            {
                text = text.Replace("\r", "\\r").Replace("\n", "\\n");
            }

            var t = now.ToString("HH:mm:ss");
            LogText.Append($"[{t}][{tag}]{text}");

            if (level == LogLevel.Message) NowDetailedErrorLog.Clear();
        }

        if (!Main.Loaded)
        {
            LateTask.New(() => CustomLogger.Instance.Log(level.ToString(), LogText, multiLine), 1f, "Log Retry", log: false);
            return;
        }

        CustomLogger.Instance.Log(level.ToString(), LogText, multiLine);
    }

    public static void Test(object content, string tag = "======= Test =======", bool escapeCRLF = true, [CallerLineNumber] int lineNumber = 0, [CallerFilePath] string fileName = "", bool multiLine = false)
    {
        SendToFile(content.ToString(), LogLevel.Debug, tag, escapeCRLF, lineNumber, fileName, multiLine);
    }

    public static void Info(string text, string tag, bool escapeCRLF = true, [CallerLineNumber] int lineNumber = 0, [CallerFilePath] string fileName = "", bool multiLine = false)
    {
        SendToFile(text, LogLevel.Info, tag, escapeCRLF, lineNumber, fileName, multiLine);
    }

    public static void Warn(string text, string tag, bool escapeCRLF = true, [CallerLineNumber] int lineNumber = 0, [CallerFilePath] string fileName = "", bool multiLine = false)
    {
        SendToFile(text, LogLevel.Warning, tag, escapeCRLF, lineNumber, fileName, multiLine);
    }

    public static void Error(string text, string tag, bool escapeCRLF = true, [CallerLineNumber] int lineNumber = 0, [CallerFilePath] string fileName = "", bool multiLine = false)
    {
        SendToFile(text, LogLevel.Error, tag, escapeCRLF, lineNumber, fileName, multiLine);
    }

    public static void Fatal(string text, string tag, bool escapeCRLF = true, [CallerLineNumber] int lineNumber = 0, [CallerFilePath] string fileName = "", bool multiLine = false)
    {
        SendToFile(text, LogLevel.Fatal, tag, escapeCRLF, lineNumber, fileName, multiLine);
    }

    public static void Msg(string text, string tag, bool escapeCRLF = true, [CallerLineNumber] int lineNumber = 0, [CallerFilePath] string fileName = "", bool multiLine = false)
    {
        SendToFile(text, LogLevel.Message, tag, escapeCRLF, lineNumber, fileName, multiLine);
    }

    public static void Exception(Exception ex, string tag, [CallerLineNumber] int lineNumber = 0, [CallerFilePath] string fileName = "", bool multiLine = false)
    {
        SendToFile(ex.ToString(), LogLevel.Error, tag, false, lineNumber, fileName);
    }

    public static void CurrentMethod([CallerLineNumber] int lineNumber = 0, [CallerFilePath] string fileName = "")
    {
        StackFrame stack = new(1);
        MethodBase method = stack.GetMethod();
        Msg($"\"{method?.ReflectedType?.Name}.{method?.Name}\" Called in \"{Path.GetFileName(fileName)}({lineNumber})\"", "Method");
    }

    public static LogHandler Handler(string tag)
    {
        return new(tag);
    }

    public static void ResetExceptionTags() => ExceptionTags.Clear();

    public static string GetExceptionTagsSummary()
    {
        if (ExceptionTags.Count == 0) return string.Empty;
        return string.Join(",", ExceptionTags
            .OrderByDescending(kv => kv.Value)
            .Take(10)
            .Select(kv => $"{kv.Key}:{kv.Value}"));
    }

    // 全タグ合計(top10 に入らない新規タグの増分も含む)。例外洪水検知のトリガー用。
    public static int GetExceptionTagsTotal()
    {
        try { return ExceptionTags.Sum(kv => kv.Value); }
        catch { return 0; }
    }

    public static void FlushNow()
    {
        try { CustomLogger.Instance.Finish(false); }
        catch { }
    }
}

public class CustomLogger
{
    public static readonly string LOGFilePath = Path.Combine(Main.DataPath, OperatingSystem.IsAndroid() ? "EndKnot_Logs" : "BepInEx", "log.html");

    private const string HtmlHeader =
        """
        <!DOCTYPE html>
        <html lang='en'>
        <head>
          <meta charset='UTF-8'>
          <meta name='viewport' content='width=device-width, initial-scale=1.0'>
          <title>EndKnot Log File</title>
          <style>
              body { font-family: Arial, sans-serif; background-color: #1e1e1e; color: #aaaaaa; margin: 0; padding: 1rem; font-family: "Roboto Mono", "Consolas", "Courier New", monospace; }
              .log-entry { margin: 0; padding: 0; border-radius: 5px; letter-spacing: 0.1rem; }
              .info { background-color: transparent; }
              .warning { background-color: #ffff44; color: black; }
              .error { background-color: red; color: black; border-radius: 10px; margin: 1rem; }
              .fatal { background: linear-gradient(to bottom, #ff9999, #cc0000); color: black; border: 3px solid yellow; border-radius: 15px; padding: 1rem; }
              .debug { background-color: gray; color: white; }
              .message { color: aqua; }
          </style>
        </head>
        <body>
          <h1>EndKnot Log File</h1>
          <div id='log-container'>

        """;

    private const string HtmlFooter =
        """
             </div>
            </body>
            </html>
        """;

    private static CustomLogger PrivateInstance;
    private float timer = 1f;

    private readonly StringBuilder Builder;

    // 遊休 flush の監視はプロセスに1本だけ張る。
    // ⚠️ インスタンスごとに StartCoroutine してはいけない: コルーチンが正常終了しても BepInEx の
    //    Il2CppManagedEnumerator ラッパーは strong GCHandle で永久保持されるため、そのインスタンスと
    //    StringBuilder / Char[] バッファごと二度と回収されない。Finish は毎回 PrivateInstance を null に
    //    するので「毎秒 new + StartCoroutine」になっており、125分で 2,353 個・Char[] 18.3MB が滞留していた
    //    (2026-08-04 のヒープダンプ + gcroot で実測)。監視を static の常駐1本へ移せば、使い捨てられた
    //    インスタンスは何にも掴まれず素直に GC される。
    private static bool InactivityWatcherStarted;

    private CustomLogger()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LOGFilePath) ?? throw new InvalidOperationException());
        if (!File.Exists(LOGFilePath)) File.WriteAllText(LOGFilePath, HtmlHeader);
        else if (Options.IsLoaded && new FileInfo(LOGFilePath).Length > 4 * 1024 * 1024) // 4 MB
        {
            ClearLog(false);
            PrivateInstance ??= new();
            LateTask.New(() => Logger.SendInGame("The size of the log file exceeded 4 MB and was dumped."), 0.1f, log: false);
        }

        Builder = new();

        // 立った後に印を付ける。先に印を付けると、張り損ねたときに二度と誰も張り直さず flush が止まる。
        if (!InactivityWatcherStarted)
        {
            Main.Instance.StartCoroutine(InactivityCheck());
            InactivityWatcherStarted = true;
        }
    }

    public static CustomLogger Instance => PrivateInstance ??= new();

    public static void ClearLog(bool check = true)
    {
        // The log directory may not exist yet (Android / first launch); writing into it here
        // throws out of the chainloader-Finished handler. The CustomLogger constructor creates
        // it later, so simply skipping is safe.
        if (!Directory.Exists(Path.GetDirectoryName(LOGFilePath))) return;

        if (!check || (File.Exists(LOGFilePath) && new FileInfo(LOGFilePath).Length > 0))
        {
            PrivateInstance?.Finish();
            // moveHtml: 直下で log.html を新ヘッダ上書きするため、コピーでなく rename で退避させる (truncate 競合防止 + 同期コピー分のヒッチ削減)。
            try { Utils.DumpLog(false, false, moveHtml: true); } catch (Exception e) { LateTask.New(() => Logger.Fatal(e.ToString(), "ClearLog.DumpLog"), 0.1f); }
        }

        File.WriteAllText(LOGFilePath, HtmlHeader);
    }

    public void Log(string level, StringBuilder message, bool multiLine = false)
    {
        if (multiLine) message.Replace("\\n", "<br>");

        bool hasB = false, hasU = false, hasI = false, hasS = false;
        for (int i = 0; i < message.Length - 2; i++)
        {
            if (message[i] != '<') continue;
            char c = message[i + 1];
            switch (c)
            {
                case 'b':
                    hasB = true;
                    break;
                case 'u':
                    hasU = true;
                    break;
                case 'i':
                    hasI = true;
                    break;
                case 's':
                    hasS = true;
                    break;
            }
        }
        if (hasB) message.Append("</b>");
        if (hasU) message.Append("</u>");
        if (hasI) message.Append("</i>");
        if (hasS) message.Append("</s>");

        Builder.Append($"""
                        <div class='log-entry {level.ToLower()}'>
                            {message}
                        </div>
                        """);

#if DEBUG
        Finish(false);
#endif
    }

    // プロセスに1本だけ常駐し、現行インスタンスの遊休を見張って flush する。
    // 終わらない (= ラッパーの strong GCHandle も1個だけ) ことが要点。インスタンスは掴まない。
    private static IEnumerator InactivityCheck()
    {
        while (true)
        {
            CustomLogger instance = PrivateInstance;

            if (instance != null)
            {
                instance.timer -= Time.deltaTime;

                if (instance.timer <= 0f)
                {
                    instance.Finish(false);
                    // Finish は書き出す物が無いと PrivateInstance を残したまま早期 return する。
                    // タイマーを戻さないとそのインスタンスを毎フレーム叩き続けるうえ、
                    // 次に溜まったログを流す担当が誰も居なくなる。
                    instance.timer = 1f;
                }
            }

            yield return null;
        }
    }

    public void Finish(bool dump = true)
    {
        var append = Builder.ToString();
        if (string.IsNullOrWhiteSpace(append)) return;
        if (dump) append += HtmlFooter;
        try { File.AppendAllText(LOGFilePath, append); }
        catch (IOException) { return; } // log.html が別ハンドルに握られている瞬間の排他違反は握りつぶす (Builder 温存で次回 flush に持ち越し)
        Builder.Clear();
        PrivateInstance = null;
    }
}