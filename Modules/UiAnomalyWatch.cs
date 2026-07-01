using System;
using System.Collections.Generic;
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
// - 文字列(.text/.name)を読むのは「今フレーム live 列挙で返ってきた要素」と「生きている vanilla チャット欄」だけ。
// - static フィールドには Unity fake-null の bool 判定と、生成時(=確実に live)に控えた GetInstanceID()(純 int)しか触らない。
// ネットワーク送信ゼロ(AC 安全)。1/sec ゲート + 立ち上がりエッジ de-dup で低負荷・低ノイズ。
public static class UiAnomalyWatch
{
    private const char Sentinel = '￫';   // AdditionalInfoText の ID リスト区切り。実行時ほぼ専用の高精度カナリア
    private const int ScanCap = 800;          // 列挙上限(GC 暴走防止)
    private const long RewarnSeconds = 30;    // 同一署名の再記録抑制(立ち上がりエッジで一度だけ)

    private static readonly Dictionary<string, int> CreatedIds = new();  // 生成時(=live)に控えた instance id
    private static readonly Dictionary<string, long> LastWarned = new();

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

    // 生のチャット入力欄(vanilla 管理の live 参照, ?. 厳禁で Unity-null チェーン)に補助 TMP の中身が漏れていないか。
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

            string ct;
            try { ct = area.text; } // live vanilla オブジェクト → 安全
            catch { return; }

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
