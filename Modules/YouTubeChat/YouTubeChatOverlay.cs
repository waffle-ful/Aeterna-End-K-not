using System.Collections.Generic;

namespace EndKnot.Modules.YouTubeChat;

// YouTube ライブチャットの「モデル」層。
// Manager.OnMessage は worker thread から発火するので、ここでは buffer に積むだけにして
// 実描画は YouTubeChatBubble（IMGUI, main thread）が buffer を読み出して行う。
// すべて host 画面ローカル（RPC / NetObject 不使用）。
public static class YouTubeChatOverlay
{
    private static readonly Queue<string> messages = new();
    private static readonly object bufferLock = new();
    private static bool subscribed;

    public static void EnsureSubscribed()
    {
        if (subscribed) return;
        YouTubeChatManager.OnMessage += HandleMessage;
        YouTubeChatManager.OnStatusChanged += HandleStatus;
        subscribed = true;
    }

    // /yt clear などから呼ばれる。メッセージ buffer のみクリア（バブルの位置・開閉は保持）。
    public static void Reset()
    {
        lock (bufferLock) messages.Clear();
    }

    public static int LineCount
    {
        get { lock (bufferLock) return messages.Count; }
    }

    // OnGUI から毎フレーム呼ばれるので、確保済みリストへ詰め直してアロケーションを避ける。
    public static void CopyLinesInto(List<string> dst)
    {
        dst.Clear();
        lock (bufferLock)
        {
            foreach (string line in messages) dst.Add(line);
        }
    }

    private static void HandleMessage(string author, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // IMGUI/TMP のリッチテキストとして安全にするため半角不等号を全角へ潰す（tag 注入防止）。
        string safeText = Sanitize(text);
        string safeAuthor = Sanitize(author);

        string line = YouTubeChatOptions.ShowAuthor != null && YouTubeChatOptions.ShowAuthor.GetBool() && !string.IsNullOrEmpty(safeAuthor)
            ? $"<color=#FFAA00>{safeAuthor}</color>: {safeText}"
            : safeText;

        int max = YouTubeChatOptions.DisplayCount?.GetInt() ?? 5;

        lock (bufferLock)
        {
            messages.Enqueue(line);
            while (messages.Count > max) messages.Dequeue();
        }
    }

    private static void HandleStatus(string status)
    {
        // 現状は無視。将来的にエラー表示などに使う想定。
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Replace("<", "＜").Replace("\n", " ").Replace("\r", " ");
    }
}
