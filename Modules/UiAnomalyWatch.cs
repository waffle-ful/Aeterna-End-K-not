using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;

namespace EndKnot.Modules;

// 観測専用・クラッシュしない UI 異常ロガー。
//
// 背景: TextBoxPatch のコマンド補助 TMP (PlaceHolderText / CommandInfoText / AdditionalInfoText) は
// 長命 static で、世代を跨ぐと freed-then-reused した IL2CPP スロットに化ける。Unity の fake-null
// (if (!x)) は「破棄」は捕まえるが「解放後に別オブジェクトへ再利用された」スロットは生存扱いするため、
// 生の `.text =` 書き込みが別 TMP に化けて「太字＋装飾タグ付きのホスト名」を描き込んだり、設定の
// チャット欄 deep-clone で複製されて「そこかしこ」に増えたりする。クラッシュしないので従来ログに残らない。
//
// このウォッチャは「stale な可能性のある static wrapper から文字列を一切読まない」ことで自分自身が
// 0x80131506 で落ちないようにしつつ、シーングラフを 1 秒に 1 回走査して異常を検出し記録する。
// - 文字列(.text/.name)を読むのは「今フレーム live 列挙で返ってきた要素」のみ。vanilla チャット欄の text フィールドは
//   live でも dangling String* を持ちうる（実クラッシュ実績あり）ため、検証付き生読み取り(SafeReadChatText)でのみ読む。
// - static フィールドには Unity fake-null の bool 判定と、生成時(=確実に live)に控えた GetInstanceID()(純 int)しか触らない。
// ネットワーク送信ゼロ(AC 安全)。1/sec ゲート + 立ち上がりエッジ de-dup で低負荷・低ノイズ。
public static class UiAnomalyWatch
{
    // ---- VirtualQuery helpers (Windows only; Android path は SafeReadChatText 冒頭でガード) ----

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public uint __alignment1;   // x64 パディング
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint __alignment2;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, IntPtr dwLength);

    private static bool IsReadable(IntPtr addr, int size)
    {
        if (addr == IntPtr.Zero) return false;
        try
        {
            long cur = addr.ToInt64();
            long end = cur + size;
            IntPtr mbiSize = (IntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
            while (cur < end)
            {
                if (VirtualQuery((IntPtr)cur, out MEMORY_BASIC_INFORMATION mbi, mbiSize) == IntPtr.Zero) return false;
                if (mbi.State != 0x1000) return false;          // MEM_COMMIT
                if ((mbi.Protect & 0x101) != 0) return false;   // PAGE_NOACCESS | PAGE_GUARD
                if ((mbi.Protect & 0xEE) == 0) return false;    // 読取権限なし
                cur = mbi.BaseAddress.ToInt64() + mbi.RegionSize.ToInt64();
            }
            return true;
        }
        catch { return false; }
    }

    // ---- 検証付き il2cpp 文字列読み取り ----
    // TextFieldOffset: -1=未解決, -2=恒久無効(解決失敗 or Android)
    private static int TextFieldOffset = -1;
    private static IntPtr StringKlass;

    private static unsafe string SafeReadChatText(TextBoxTMP area, out string bad)
    {
        // Android では VirtualQuery が存在しないため SafeReadChatText 全体を無効化
        if (OperatingSystem.IsAndroid())
        {
            TextFieldOffset = -2;
            bad = null;
            return null;
        }

        if (TextFieldOffset == -2) { bad = null; return null; }

        // フィールドオフセット解決（初回のみ）
        if (TextFieldOffset == -1)
        {
            IntPtr klass = Il2CppClassPointerStore<TextBoxTMP>.NativeClassPtr;
            IntPtr field = IL2CPP.GetIl2CppField(klass, "text");
            if (field == IntPtr.Zero)
            {
                TextFieldOffset = -2;
                Logger.Warn("SafeReadChatText: failed to resolve TextBoxTMP.text field offset", "UIAnomaly");
            }
            else
            {
                TextFieldOffset = (int)IL2CPP.il2cpp_field_get_offset(field);
            }
        }

        if (TextFieldOffset == -2) { bad = null; return null; }

        // StringKlass 解決（初回のみ）
        if (StringKlass == IntPtr.Zero)
        {
            IntPtr probe = IL2CPP.ManagedStringToIl2Cpp("x");
            if (probe != IntPtr.Zero)
                StringKlass = *(IntPtr*)probe;
        }

        // Step 2: wrapper から native ポインタを取得
        IntPtr objPtr;
        try { objPtr = area.Pointer; }
        catch { bad = "wrapper"; return null; }
        if (objPtr == IntPtr.Zero) { bad = "objnull"; return null; }

        // Step 3: text フィールドスロットが読取可能か
        if (!IsReadable(objPtr + TextFieldOffset, 8)) { bad = "objmem"; return null; }

        // Step 4: String* を読み取る
        IntPtr strPtr = *(IntPtr*)((byte*)objPtr + TextFieldOffset);
        if (strPtr == IntPtr.Zero) { bad = null; return null; } // フィールドが null = 正常

        // Step 5: アライメント確認（有効な il2cpp ヒープポインタは 8 バイトアラインド）
        if (((long)strPtr & 7) != 0) { bad = "align"; return null; }

        // Step 6: String オブジェクト先頭 24 バイトが読取可能か
        if (!IsReadable(strPtr, 24)) { bad = "unmapped"; return null; }

        // Step 7: klass ポインタが String klass と一致するか（遊離スロットの直接証拠）
        if (StringKlass != IntPtr.Zero)
        {
            IntPtr k = *(IntPtr*)(byte*)strPtr;
            if (k != StringKlass) { bad = "klass"; return null; }
        }

        // Step 8: length フィールド（x64: klass 8 + monitor 8 + int32 length @16）
        int len = *(int*)((byte*)strPtr + 16);
        if (len < 0 || len > 4096) { bad = $"len:{len}"; return null; }
        if (len == 0) { bad = null; return ""; }

        // Step 9: UTF-16 文字データが読取可能か（chars @20）
        if (!IsReadable(strPtr + 20, len * 2)) { bad = "chars"; return null; }

        // Step 10: マネージド文字列を構築して返す
        bad = null;
        return new string((char*)((byte*)strPtr + 20), 0, len);
    }

    // ---- 定数・共有状態 ----

    private const char Sentinel = '￫';   // AdditionalInfoText の ID リスト区切り。実行時ほぼ専用の高精度カナリア
    private const int ScanCap = 800;          // 列挙上限(GC 暴走防止)
    private const long RewarnSeconds = 30;    // 同一署名の再記録抑制(立ち上がりエッジで一度だけ)

    private static readonly Dictionary<string, int> CreatedIds = new();  // 生成時(=live)に控えた instance id
    private static readonly Dictionary<string, long> LastWarned = new();

    // FindObjectsOfType<TMP_Text>(true) はシーン全体を走査し IL2CPP プロキシを大量に確保するため native ws を
    // 押し上げる。既定はモジュールごと OFF(Main.EnableUiAnomalyWatch)だが、診断で有効化した時も churn を抑えるよう
    // 実走査は 10s に1回へ間引く(全体ゲートとの二重防御)。
    private static long LastScanTs;
    private const long ScanThrottleSeconds = 10;

    // ShowCommandHelp の各 Object.Instantiate 直後(オブジェクトは確実に live)に呼ぶ。
    public static void RecordCreation(string name, int instanceId)
    {
        try { CreatedIds[name] = instanceId; }
        catch { }
    }

    // TextBoxPatch.Reset()(新しい HudManager ごと)で呼ぶ。世代境界で baseline と de-dup をクリア。
    public static void OnReset()
    {
        CreatedIds.Clear();
        LastWarned.Clear();
    }

    // FixedUpdateCaller から ShouldRunUpdate ゲートで 1/sec 呼ぶ。
    public static void Scan()
    {
        try
        {
            // 実走査は 10s に1回(native churn 抑制の二重防御。全体の有効化は Main.EnableUiAnomalyWatch)。
            long nowTs = Utils.TimeStamp;
            if (nowTs - LastScanTs < ScanThrottleSeconds) return;
            LastScanTs = nowTs;

            // BackroomsLobby が Renderer(抽象)で実証済みのシグネチャ。基底 TMP_Text で TextMeshPro と
            // TextMeshProUGUI の両方を拾い、(true) で非アクティブ overlay / DontDestroyOnLoad キャッシュも対象に。
            // FindObjectsOfType はシーン上のオブジェクトのみ返す(prefab/asset は含まない)ので読み取りは安全。
            TMP_Text[] all = Object.FindObjectsOfType<TMP_Text>(true);
            if (all == null) return;

            bool chatOpen = ChatIsOpen();
            bool scanContent = chatOpen || CreatedIds.Count > 0; // foreign の .text 読みは overlay 存在時 / チャット中のみ(perf)

            int phLive = 0, phClone = 0, ciLive = 0, aiLive = 0;
            int phId = 0, ciId = 0, aiId = 0;
            bool wrongCi = false, wrongAi = false;
            string literalName = null, literalHead = null;
            int sentinelHits = 0;
            string sentinelHost = null;

            int n = all.Length;
            if (n > ScanCap) n = ScanCap;

            var liveNames = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);

            for (var i = 0; i < n; i++)
            {
                TMP_Text t = all[i];
                if (!t) continue; // Unity fake-null: 破棄保留を除外

                string name;
                try { name = t.name; } // live 列挙要素の .name は安全
                catch { continue; }

                if (name == null) continue;
                if (liveNames.Count < 512) liveNames.Add(name);

                bool isPh = name.StartsWith("PlaceHolderText", StringComparison.Ordinal);
                bool isCi = name == "CommandInfoText";
                bool isAi = name == "AdditionalInfoText";

                if (isPh)
                {
                    phLive++;
                    if (name.EndsWith("(Clone)", StringComparison.Ordinal)) phClone++;
                    else phId = SafeId(t);
                }
                else if (isCi) { ciLive++; ciId = SafeId(t); }
                else if (isAi) { aiLive++; aiId = SafeId(t); }

                if ((isCi || isAi) && !chatOpen)
                {
                    bool active;
                    try { active = t.gameObject.activeInHierarchy; }
                    catch { active = false; }

                    if (active)
                    {
                        if (isCi) wrongCi = true;
                        else wrongAi = true;
                    }
                }

                if (!scanContent) continue;

                string text;
                try { text = t.text; } // live 列挙要素の .text は安全
                catch { continue; }

                if (string.IsNullOrEmpty(text)) continue;

                if (literalName == null && HasTag(text) && !SafeRich(t))
                {
                    literalName = name;
                    literalHead = Head(text);
                }

                if (!isAi && text.IndexOf(Sentinel) >= 0)
                {
                    sentinelHits++;
                    sentinelHost = name;
                }
            }

            string state = SafeState();

            if (phLive > 1) Emit($"kind=DUP name=PlaceHolderText live={phLive} clones={phClone}", state);
            if (ciLive > 1) Emit($"kind=DUP name=CommandInfoText live={ciLive}", state);
            if (aiLive > 1) Emit($"kind=DUP name=AdditionalInfoText live={aiLive}", state);

            if (CreatedIds.ContainsKey("PlaceHolderText") && phLive == 0) Emit("kind=DANGLE name=PlaceHolderText", state);
            if (CreatedIds.ContainsKey("CommandInfoText") && ciLive == 0) Emit("kind=DANGLE name=CommandInfoText", state);
            if (CreatedIds.ContainsKey("AdditionalInfoText") && aiLive == 0) Emit("kind=DANGLE name=AdditionalInfoText", state);

            if (CreatedIds.TryGetValue("PlaceHolderText", out int wp) && phClone == 0 && phId != 0 && phId != wp) Emit($"kind=DRIFT name=PlaceHolderText want={wp} found={phId}", state);
            if (CreatedIds.TryGetValue("CommandInfoText", out int wc) && ciLive == 1 && ciId != 0 && ciId != wc) Emit($"kind=DRIFT name=CommandInfoText want={wc} found={ciId}", state);
            if (CreatedIds.TryGetValue("AdditionalInfoText", out int wa) && aiLive == 1 && aiId != 0 && aiId != wa) Emit($"kind=DRIFT name=AdditionalInfoText want={wa} found={aiId}", state);

            if (wrongCi) Emit("kind=WRONGTIME name=CommandInfoText chatClosed=1", state);
            if (wrongAi) Emit("kind=WRONGTIME name=AdditionalInfoText chatClosed=1", state);

            if (literalName != null) Emit($"kind=LITERALTAG name={literalName} richText=0 head=\"{literalHead}\"", state);
            if (sentinelHits > 0) Emit($"kind=LEAK sentinel=ffeb host={sentinelHost} hits={sentinelHits}", state);

            if (scanContent) ScanLiveChatInput(state, liveNames);
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    // 生のチャット入力欄(vanilla 管理の live 参照, ?. 厳禁)に補助 TMP の中身が漏れていないか。
    // text フィールドは SafeReadChatText で検証付き読み取りし、dangling String* によるクラッシュを防ぐ。
    private static void ScanLiveChatInput(string state, System.Collections.Generic.HashSet<string> liveNames)
    {
        try
        {
            if (!HudManager.InstanceExists) return;

            HudManager hud = HudManager.Instance;
            if (!hud) return;

            ChatController chat = hud.Chat;
            if (!chat) return;

            FreeChatInputField field = chat.freeChatField;
            if (!field) return;

            TextBoxTMP area = field.textArea;
            if (!area) return;

            string fname;
            try { fname = area.name; }
            catch { fname = "?"; }

            int fid = SafeId(area);
            string alias = "?";
            foreach (KeyValuePair<string, int> kv in CreatedIds)
                if (kv.Value == fid) { alias = kv.Key; break; }

            string ct = SafeReadChatText(area, out string bad);

            if (bad != null)
            {
                // 旧実装はここで area.text を直接 marshal しており、dangling String* で 0x80131506 即死した
                // (TextBoxPatch.SafeChatText のコメント参照)。クラッシュの代わりに証拠を記録して撤退する。
                Emit($"kind=CHATINPUT-BADSTR why={bad} fieldName={fname} fieldId={fid}", state);
                return;
            }

            if (ct == null) return;

            if (string.IsNullOrEmpty(ct)) return;

            string trimmed = ct.Trim();
            bool sentinel = ct.IndexOf(Sentinel) >= 0;
            bool hasName = ct.Contains("PlaceHolderText");
            bool hasTag = ct.Contains("<b>");
            bool nameLeak = trimmed.Length > 0 && (trimmed == fname || (liveNames != null && liveNames.Contains(trimmed)));
            bool tokenLeak = LooksLikeLeakedIdentifier(trimmed);

            if (sentinel || hasName || hasTag || nameLeak || tokenLeak)
            {
                string mark = sentinel ? "ffeb" : hasName ? "name" : nameLeak ? "livename" : tokenLeak ? "token" : "tag";

                // 決め手: この入力欄参照(freeChatField.textArea)が本物か、stale で補助オーバーレイに
                // すり替わっているかを name で判別する。fieldName=="PlaceHolderText" なら参照自体が
                // オーバーレイに化けている(=slot reuse で get_text が名前を返す)証拠。real な入力欄なら
                // ここには本来の名前が出る。head= に実際の文字列(≤60, エスケープ)を残して SetText 注入と切り分ける。
                Emit($"kind=CHATINPUT len={ct.Length} mark={mark} fieldName={fname} fieldId={fid} aliasesOverlay={alias} head=\"{Head(ct)}\"", state);
            }
        }
        catch { }
    }

    private static bool LooksLikeLeakedIdentifier(string s)
    {
        if (s == null || s.Length < 8 || s[0] == '/') return false;
        foreach (char c in s)
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))) return false;
        return true;
    }

    private static void Emit(string body, string state)
    {
        long now = Utils.TimeStamp;

        if (LastWarned.TryGetValue(body, out long last) && now - last < RewarnSeconds) return; // 立ち上がりエッジで一度だけ
        LastWarned[body] = now;

        HealthLog.Note($"UIANOM {body} state={state} t={now}"); // 即 flush・ウォッチドッグ tail 可
        Logger.Warn($"UI anomaly: {body} state={state}", "UIAnomaly"); // color tag 付き log.html ミラー
    }

    private static bool ChatIsOpen()
    {
        try
        {
            if (!HudManager.InstanceExists) return false;

            HudManager hud = HudManager.Instance;
            if (!hud) return false;

            ChatController chat = hud.Chat;
            if (!chat) return false;

            return chat.IsOpenOrOpening;
        }
        catch { return false; }
    }

    private static bool HasTag(string s) => s.IndexOf('<') >= 0 && (s.Contains("<b>") || s.Contains("<color") || s.Contains("<size"));

    private static int SafeId(UnityEngine.Object t)
    {
        try { return t.GetInstanceID(); }
        catch { return 0; }
    }

    private static bool SafeRich(TMP_Text t)
    {
        try { return t.richText; }
        catch { return true; }
    }

    private static string Head(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Replace("\"", "'");
        return s.Length > 60 ? s[..60] : s;
    }

    private static string SafeState()
    {
        try
        {
            if (GameStates.InGame) return GameStates.IsMeeting ? "Meeting" : "InTask";
            if (GameStates.IsLobby) return "Lobby";
            return "Menu";
        }
        catch { return "?"; }
    }
}
