using System;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace EndKnot.Patches;

// Live preview line for the mod-settings search box: every text change recounts what the query would hit
// and draws one line above the box — hit count, the best-matching name, and Tab to accept that name into
// the field. Deliberately not a name list: the search results are produced by the same matcher, so a list
// only restated what pressing Enter was about to show. The accepted-on-Tab text comes from a managed
// snapshot (TopSuggestion), never from reading a TMP back — a freed/reused IL2CPP TMP returns its
// GameObject name from get_text (the PlaceHolderText leak family).
//
// Everything here is decoration: any failure hides the list and leaves the search box itself working.
//
// The bare [HarmonyPatch] on the class is load-bearing: PatchAll(Assembly) skips types that carry no
// type-level Harmony attribute, so a class with only method-level ones is never registered — silently,
// with no exception and no log (that is exactly how TextBoxPatch stays off Android, see Main.cs).
[HarmonyPatch]
public static class OptionSearchSuggestPatch
{
    // ⚠️ The backdrop was originally a TMP <mark> tag — and the mark quads render ON TOP of the glyphs
    // here (2026-08-15 screenshot: every glyph uniformly dimmed to ~7% through the #ee black), which
    // read as "the preview stopped appearing / only the background shows". The band is now a separate
    // SpriteRenderer child drawn explicitly BEHIND the text (own sortingOrder + z offset), the only
    // arrangement whose draw order we control.
    private const float BandAlpha = 0.93f;

    // How far above the input line the preview sits, and how wide a rect it gets to draw into.
    private const float LineRise = 0.62f;
    // 8 sits comfortably past this menu's own widest TMP rects (5.7 for an option name, 6.0 for the BGM
    // credit) without shifting the origin further than it has to — the widening moves the left edge, and
    // the line's position is the other thing being tuned here.
    private const float LineWidth = 8f;

    private static TextMeshPro ListText;
    private static int ListId;
    private static string TopSuggestion = "";

    // The search box's text as this patch last saw it through the SetText ARGUMENTS — committed half
    // plus the live IME composition. This is the text the user is looking at, and it is the ONLY safe
    // managed copy of it: the composition cannot be read back out of the field (IL2CPP compoText
    // read-back is the known 0x80131506 crash family). The Enter-to-search handler consumes it too, so
    // the preview and the actual search can never disagree about what was searched (they used to: the
    // search read only the committed text, so an Enter during composition found nothing while the
    // preview above the box was showing hits).
    public static string CurrentQueryText { get; private set; } = "";

    // Set on every text change; the pump computes at most once per few frames so IME bursts and fast
    // typing collapse into one full-menu scan instead of one per event.
    private static bool QueryDirty;
    private static int LastComputeFrame;
    private static int LastHealFrame;

    // Identity guard: Unity fake-null AND the instance ID recorded at creation, so a freed-then-reused
    // IL2CPP slot (which passes `if (!tmp)`) is still rejected.
    private static bool Valid(TextMeshPro tmp, int wantId)
    {
        if (!tmp || wantId == 0) return false;

        try { return tmp.GetInstanceID() == wantId; }
        catch { return false; }
    }

    private static bool IsSearchBox(TextBoxTMP textBox)
    {
        FreeChatInputField field = GameSettingMenuPatch.InputField;
        return field && field.textArea == textBox;
    }

    // The query is taken from the SetText ARGUMENTS, not read back from the field: the committed text
    // alone misses the whole IME composition (a Japanese query lives in inputCompo until committed, so
    // reading only the committed text kept the list hidden for the entire typing session), and reading
    // IL2CPP compoText back is the known 0x80131506 crash family. The arguments are plain managed
    // strings carrying both halves.
    [HarmonyPatch(typeof(TextBoxTMP), nameof(TextBoxTMP.SetText))]
    [HarmonyPostfix]
    public static void OnSearchTextChanged(TextBoxTMP __instance, [HarmonyArgument(0)] string input, [HarmonyArgument(1)] string inputCompo = "")
    {
        try
        {
            if (!IsSearchBox(__instance)) return;

            string text = $"{input}{inputCompo}".Trim();
            if (text == CurrentQueryText) return;

            CurrentQueryText = text;
            QueryDirty = true;
        }
        catch (Exception e)
        {
            Logger.Error($"search suggestions failed: {e}", "OptionSearchSuggest");
            Hide();
        }
    }

    // Runs every frame the settings menu is open (driven from the ChatController.Update postfix that
    // already restyles the search box). Two jobs: fold the pending text change into ONE preview scan
    // per few frames, and re-show a line that something else hid or destroyed while a query is still
    // in the box (menu reopen, tab rebuilds, HudManager resets — the "the preview just stopped
    // appearing" family, whichever member of it fires).
    public static void Pump()
    {
        try
        {
            FreeChatInputField field = GameSettingMenuPatch.InputField;
            if (!field || !field.gameObject.activeInHierarchy) return;

            if (QueryDirty)
            {
                if (Time.frameCount - LastComputeFrame < 6) return; // ~0.1s coalescing window

                QueryDirty = false;
                LastComputeFrame = Time.frameCount;
                Refresh(field, CurrentQueryText);
            }
            else if (CurrentQueryText.Length > 0 && (!Valid(ListText, ListId) || !ListText.enabled || !ListText.gameObject.activeInHierarchy))
            {
                // Self-heal is deliberately slow (once a second): if Refresh cannot bring the line
                // back (no template yet, mid-rebuild), this must not turn into a per-frame retry.
                if (Time.frameCount - LastHealFrame < 60) return;

                LastHealFrame = Time.frameCount;
                Refresh(field, CurrentQueryText);
            }
            else if (CurrentQueryText.Length > 0 && Valid(ListText, ListId) && ListText.enabled)
            {
                // While the line is on screen, keep its glyph color asserted — the same every-frame
                // repaint FixDarkThemeForSearchBar already does for the field's own text, and for the
                // same reason: chat-derived TMPs get repainted dark by surviving helpers, and a black
                // glyph on the black band reads as "only the background shows".
                ListText.color = Color.white;
            }
        }
        catch (Exception e)
        {
            // The pump runs every frame and the self-heal retries every second — an error state must
            // not become a red wall in the console (nor per-frame log I/O). One line per ~5s.
            if (Time.frameCount - LastPumpErrorFrame > 300)
            {
                LastPumpErrorFrame = Time.frameCount;
                Logger.Error($"search suggestion pump failed: {e}", "OptionSearchSuggest");
            }

            Hide();
        }
    }

    private static int LastPumpErrorFrame = int.MinValue;

    // Vanilla Clear() bypasses SetText, so the list from the pre-search text would survive the search
    // otherwise (the same gap the chat ghost had — see TextBoxPatch.OnClear).
    [HarmonyPatch(typeof(TextBoxTMP), nameof(TextBoxTMP.Clear))]
    [HarmonyPostfix]
    public static void OnSearchCleared(TextBoxTMP __instance)
    {
        try
        {
            if (IsSearchBox(__instance)) NotifyCleared();
        }
        catch { }
    }

    // One line of render state for the MenuPerf 5-second dump: whether the suggestion line thinks it
    // is on screen, and the visual properties that decide whether the glyphs are actually readable.
    // This is what pins down any FUTURE "the line is computed but I can't see it" report from a log.
    public static string DebugState()
    {
        try
        {
            if (!Valid(ListText, ListId)) return "suggest=none";
            if (!ListText.enabled || !ListText.gameObject.activeInHierarchy) return "suggest=hidden";

            Color c = ListText.color;
            return $"suggest=shown chars={ListText.text.Length} col=({c.r:F1},{c.g:F1},{c.b:F1},{c.a:F1}) size={ListText.fontSize} auto={ListText.enableAutoSizing} mat={(ListText.fontSharedMaterial ? ListText.fontSharedMaterial.name : "<null>")}";
        }
        catch (Exception e)
        {
            return $"suggest=err({e.Message})";
        }
    }

    // For clear paths that may not raise TextBoxTMP.Clear (FreeChatInputField.Clear is a native
    // wrapper whose delegation to textArea.Clear is not guaranteed — the codebase's convention is to
    // call .textArea.Clear() directly, see ChatCommandPatch). A stale snapshot here is not cosmetic:
    // Enter-to-search reads it FIRST, so an empty box would silently re-run the previous query and
    // the search could never be exited.
    public static void NotifyCleared()
    {
        CurrentQueryText = "";
        QueryDirty = false;
        Hide();
    }

    // Tab pressed while the settings menu is open (wired in ControlPatch next to the Enter=search key).
    public static void AcceptTop()
    {
        try
        {
            FreeChatInputField field = GameSettingMenuPatch.InputField;
            if (!field || TopSuggestion.Length == 0 || !Valid(ListText, ListId) || !ListText.enabled) return;

            TextBoxPatch.SetChatFieldText(field.textArea, TopSuggestion);
        }
        catch (Exception e)
        {
            Logger.Error($"accepting search suggestion failed: {e}", "OptionSearchSuggest");
        }
    }

    // Recorded at build time so the one-shot diagnostic can report whether the font swap actually happened,
    // and how the borrowed row is configured (it draws the same mix correctly).
    private static string FontSwapNote = "";
    private static string BorrowNote = "";

    // One-shot diagnostic: proves in the log whether the list was ever computed AND drawn this process
    // (typing-rate logging would reintroduce the log-I/O hitches the perf workstream removed).
    private static bool LoggedFirstShow;

    private static void Refresh(FreeChatInputField field, string text)
    {
        if (text.Length == 0)
        {
            Hide();
            return;
        }

        long t0 = MenuPerfProbe.Stamp();
        OptionSearch.SearchPreview preview = OptionSearch.PreviewQuery(text);
        MenuPerfProbe.Preview(t0);

        TopSuggestion = preview.Hits > 0 ? preview.Top : "";

        if (!EnsureList(field)) return;

        // One line, because the old name list duplicated the search results outright (same matcher). What
        // is left is the part the results cannot give before Enter: whether this query hits anything.
        // Readability over the settings rows comes from the sprite band behind the line (see EnsureList).
        var sb = new StringBuilder();

        if (preview.Hits == 0)
            sb.Append($" <#ff8f8f>{Translator.GetString("SearchPreviewNoHit")}</color> ");
        else
        {
            sb.Append($" <#7fd6ff>{FormatCount(preview.Hits)}</color>");
            sb.Append($"  <#ffd966>▶ {preview.Top}</color>");
            if (preview.Hits > 1) sb.Append($" <#999999>{Translator.GetString("SearchPreviewMore")}</color>");
            sb.Append($"  <#999999>{Translator.GetString("SearchSuggestTabHint")}</color> ");
        }

        ListText.text = sb.ToString();
        ListText.enabled = true;
        ListText.gameObject.SetActive(true);

        if (!LoggedFirstShow)
        {
            LoggedFirstShow = true;
            var rect = ListText.GetComponent<RectTransform>();
            string rectInfo = rect ? $"rect={rect.rect}, pivot={rect.pivot}, sizeDelta={rect.sizeDelta}" : "rect=<none>";
            string fontInfo = $"font={(ListText.font ? ListText.font.name : "<null>")}, size={ListText.fontSize}, autoSize={ListText.enableAutoSizing}, {FontSwapNote}, {BorrowNote}";

            // Material vs font-asset mismatch draws glyphs at the wrong weight AND the wrong size — the
            // "small and dim" half of the symptom that a size explanation alone cannot account for.
            string matInfo;
            try { matInfo = $"mat={(ListText.fontSharedMaterial ? ListText.fontSharedMaterial.name : "<null>")}, fontOwnMat={(ListText.font && ListText.font.material ? ListText.font.material.name : "<null>")}"; }
            catch (Exception me) { matInfo = $"mat=<probe failed: {me.Message}>"; }
            Logger.Info($"suggestion line first shown: {preview.Hits} hit(s), localPos={ListText.transform.localPosition}, worldPos={ListText.transform.position}, scale={ListText.transform.localScale}, {rectInfo}, {fontInfo}, {matInfo}, align={ListText.alignment}, overflow={ListText.overflowMode}, chars={ListText.text.Length}", "OptionSearchSuggest");
        }
    }

    private static void Hide()
    {
        TopSuggestion = "";

        if (Valid(ListText, ListId))
        {
            ListText.text = "";
            ListText.enabled = false;
            ListText.gameObject.SetActive(false); // the band sprite is a child — hiding the GO hides both
        }
        else
        {
            ListText = null;
            ListId = 0;
        }
    }

    // The hit count, with its digits folded to full width when the surrounding text is not ASCII.
    //
    // The menu's fonts are Latin-only (Barlow / Brook), so Japanese is drawn from a fallback atlas — and
    // glyphs that cross that boundary come out at a different size AND a different weight (the "13" and
    // "[Tab]" that looked small and dim next to full-size kana). Swapping the font asset did not move the
    // symptom, so the fix is to stop straddling the boundary: keep a CJK line entirely non-ASCII. The
    // count is the only ASCII the line generates, and the localized wording tells us which side we are on.
    private static string FormatCount(int count)
    {
        string format = Translator.GetString("SearchPreviewHits");
        string digits = count.ToString();

        var formatIsAscii = true;
        foreach (char c in format)
        {
            if (c <= 0x7F) continue;

            formatIsAscii = false;
            break;
        }

        if (!formatIsAscii)
        {
            var wide = new StringBuilder(digits.Length);
            foreach (char c in digits) wide.Append(c is >= '0' and <= '9' ? (char)(c - '0' + '０') : c);
            digits = wide.ToString();
        }

        return string.Format(format, digits);
    }

    // The font an already-built settings row draws with — the one the menu proves can render a Japanese +
    // ASCII mix at one size. Null when no row exists yet (the list then keeps the field's own font).
    private static TMP_FontAsset BorrowMenuFont()
    {
        try
        {
            foreach (var row in ModGameOptionsMenu.BehaviourList)
            {
                if (!row.Value) continue;

                var tmp = row.Value.GetComponentInChildren<TextMeshPro>();
                if (!tmp || !tmp.font) continue;

                // Recorded for the diagnostic: this row renders the same Japanese + ASCII mix correctly,
                // so whatever differs between it and our line is the remaining suspect.
                BorrowNote = $"srcAutoSize={tmp.enableAutoSizing}, srcSize={tmp.fontSize}, srcMat={(tmp.fontSharedMaterial ? tmp.fontSharedMaterial.name : "<null>")}";
                return tmp.font;
            }
        }
        catch (Exception e)
        {
            Logger.Warn($"could not borrow a settings-row font: {e.Message}", "OptionSearchSuggest");
        }

        return null;
    }

    // The list TMP is a child of the search field, so it lives and dies (and hides on menu close) with
    // it — no per-HudManager Reset needed. Built lazily and idempotently; a failure here only costs the
    // suggestions, never the search box.
    private static bool EnsureList(FreeChatInputField field)
    {
        // No reparenting on reuse: the list is a descendant of the cached search field, and menu reopen
        // moves the WHOLE field subtree (SetupExtendedUI reparents field.transform) — pulling the list
        // under a different parent here would reinterpret its local position in a new coordinate space.
        if (Valid(ListText, ListId)) return true;

        TextMeshPro template = field.textArea ? field.textArea.outputText : null;
        if (!template) return false;

        // Cloned next to the input line itself so its position is relative to the text the user sees,
        // not to a container whose layout we'd be guessing at. Still a descendant of the search field,
        // so it lives, dies and hides with it.
        // Sweep husks first: a failed earlier creation leaves a dead "SearchSuggestText" object behind,
        // and stacking clones next to the input line must never accumulate.
        Transform parent = template.transform.parent;

        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            Transform child = parent.GetChild(i);
            if (child && child.name == "SearchSuggestText") UnityEngine.Object.DestroyImmediate(child.gameObject);
        }

        ListText = UnityEngine.Object.Instantiate(template, parent);
        ListText.name = "SearchSuggestText";

        // The clone carries every OTHER component its template had. The chat output text ships with
        // helper behaviours that repaint their host (that is exactly why FixDarkThemeForSearchBar has
        // to force outputText white EVERY frame) — and a cloned repainter runs on OUR line too, which
        // showed up as "only the black band renders, the glyphs are gone" (black-on-black) once one of
        // them fired. Strip the clone down to what a TMP needs to render.
        // ⚠️ IL2CPP: GetComponents<Component>() returns wrappers whose MANAGED type is Component, so a
        // C# `is TextMeshPro` check is false for everything — the keep-list must use TryCast (the
        // interop-side type check), or this loop destroys the TMP itself and the line never renders
        // again (2026-08-15 regression: "予測が全く出ない").
        foreach (Component comp in ListText.GetComponents<Component>())
        {
            try
            {
                if (comp.TryCast<Transform>() != null) continue;
                if (comp.TryCast<TextMeshPro>() != null) continue;
                if (comp.TryCast<MeshRenderer>() != null) continue;
                if (comp.TryCast<MeshFilter>() != null) continue;
                if (comp.TryCast<CanvasRenderer>() != null) continue;

                UnityEngine.Object.DestroyImmediate(comp);
            }
            catch
            {
                // e.g. a RequireComponent dependency — leaving it is strictly better than failing the strip
            }
        }

        // Cloned children (caret pipe and friends) draw stale chat chrome next to the line — drop them.
        for (int i = ListText.transform.childCount - 1; i >= 0; i--)
        {
            try { UnityEngine.Object.DestroyImmediate(ListText.transform.GetChild(i).gameObject); }
            catch { }
        }

        // If the strip somehow took the TMP with it, fail the build loudly instead of caching a husk —
        // the next Refresh retries from a clean slate.
        if (!ListText)
        {
            Logger.Warn("suggestion line clone did not survive component strip", "OptionSearchSuggest");
            ListText = null;
            ListId = 0;
            return false;
        }

        // Just above the input line (the box sits at the bottom of the menu, so a downward dropdown would
        // leave the screen), clear of the field's own frame.
        Vector3 pos = template.transform.localPosition + new Vector3(0f, LineRise, -0.5f);

        // The settings menu's TMPs are laid out inside an explicit rect (see the sizeDelta calls in
        // GameOptionsMenuPatch), and the clone inherits the search field's narrow one — a line longer than
        // that rect loses BOTH ends instead of overflowing. Widen it, and push the origin by exactly what
        // the widening moved the left edge (pivot-dependent) so the line keeps where it starts.
        var rect = ListText.GetComponent<RectTransform>();

        if (rect)
        {
            Vector2 size = rect.sizeDelta;

            if (size.x < LineWidth)
            {
                pos.x += (LineWidth - size.x) * rect.pivot.x;
                rect.sizeDelta = new Vector2(LineWidth, size.y);
            }
        }

        ListText.transform.localPosition = pos;

        // The search field's own font (GameOptionsMenuPatch swaps in plusLabel's) cannot draw Japanese and
        // ASCII from one atlas, so TMP resolves one of them through a fallback asset whose scale does not
        // match — that is why "13" and "[Tab]" came out tiny while the kana around them were full size
        // (nothing else in one TMP changes glyph size except a <size> tag, and none is used here). The
        // settings rows mix the two correctly ("10秒"), so borrow the font they draw with.
        TMP_FontAsset menuFont = BorrowMenuFont();
        FontSwapNote = menuFont ? $"borrowed={menuFont.name}, was={(ListText.font ? ListText.font.name : "<null>")}" : "borrow=<none available>";
        if (menuFont) ListText.font = menuFont;

        // The template runs auto-size (srcAutoSize=True in the field diagnostics); a fixed rect plus
        // auto-size is a whole axis of "glyphs computed to an unreadable size" failure the line does
        // not need — pin the size that the first working render measured.
        ListText.enableAutoSizing = false;
        ListText.fontSize = 2.1f;

        ListText.transform.localScale = template.transform.localScale * 0.85f;
        ListText.alignment = TextAlignmentOptions.BottomLeft;
        ListText.verticalAlignment = VerticalAlignmentOptions.Bottom;
        ListText.overflowMode = TextOverflowModes.Overflow;
        ListText.enableWordWrapping = false;
        ListText.richText = true;
        ListText.color = Color.white;
        ListText.sortingOrder = 500;
        ListText.text = "";

        // The backdrop: an explicit sprite BEHIND the glyphs (lower sortingOrder AND further from the
        // camera), sized to the line's rect. Replaces the <mark> tag, whose quads covered the glyphs.
        var band = new GameObject("SearchSuggestBand");
        band.transform.SetParent(ListText.transform, false);
        band.transform.localPosition = new(LineWidth / 2f, -0.02f, 0.05f);
        band.transform.localScale = new(LineWidth + 0.2f, 0.55f, 1f);
        var bandRenderer = band.AddComponent<SpriteRenderer>();
        bandRenderer.sprite = BandSprite();
        bandRenderer.color = new(0f, 0f, 0f, BandAlpha);
        bandRenderer.sortingOrder = 499;

        ListId = ListText.GetInstanceID();
        return true;
    }

    private static Sprite BandSpriteCache;

    // 4x4 solid white, tinted by the renderer. Non-readable per the repo texture convention; FullRect
    // is mandatory for sprites created from non-readable textures. DontUnloadUnusedAsset + the fake-null
    // recheck below guard against scene-transition UnloadUnusedAssets silently killing the cache.
    private static Sprite BandSprite()
    {
        if (BandSpriteCache) return BandSpriteCache;

        var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
        var px = new Color32[16];
        for (var i = 0; i < px.Length; i++) px[i] = new(255, 255, 255, 255);
        tex.SetPixels32(px);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;

        BandSpriteCache = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f, 0, SpriteMeshType.FullRect);
        BandSpriteCache.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return BandSpriteCache;
    }
}
