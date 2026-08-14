using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace EndKnot;

// Cross-tab option search for the settings menu.
//
// A search hides nothing and moves nobody between tabs: it swaps what the settings renderer draws for
// a flat result list — every option whose name matches the query, collected from every relevant tab,
// with each hit's parent chain kept so a matched sub-option is shown under the role/section it belongs
// to. While a search is active every mod tab renders that same list; searching with an empty box (or
// reopening the menu) clears it and the normal per-tab view comes back.
public static class OptionSearch
{
    private static readonly HashSet<int> ResultIndices = [];

    // Option index -> the container the result list pulled that option's row out of. The search returns
    // every borrowed row when it ends: leaving that to "whichever tab rebuilds next" only works for the
    // classic renderer — the new role menu's reflow never re-parents anything, so a borrowed row would
    // stay orphaned under the borrower and leak on that tab's next build. Keyed by index, not by the
    // row object, because an IL2CPP wrapper fetched twice is not necessarily the same managed instance.
    private static readonly Dictionary<int, Transform> BorrowedRows = [];

    public static bool Active { get; private set; }

    // ---- fuzzy matching ----------------------------------------------------------------------------

    // Normalized option names, current language + English, keyed by option index. Names go through
    // Translator twice per option otherwise — once per keystroke of the suggestion box — so they are
    // cached until the language (vanilla or ModLanguage) or the option list itself changes.
    // The mask is a 64-bit char-class bloom of the name (bit = char & 63): a query char whose bit is
    // absent provably does not occur in the name, which lets the fuzzy tier skip most of the menu
    // without running the DP. Collisions only make a missing char look present — never a wrong skip.
    private static readonly Dictionary<int, (string Cur, string En, ulong CurMask, ulong EnMask)> NameCache = [];
    private static int NameCacheStamp = int.MinValue;

    // A parsed query: the whole query normalized, its whitespace-separated tokens normalized, and how
    // many typos the fuzzy tier tolerates (0 for short queries — edit distance 1 on a 2–3 char query
    // matches half the menu).
    public readonly record struct Query(string Norm, string[] Tokens, int FuzzyBudget, ulong Mask);

    public static Query ParseQuery(string raw)
    {
        string norm = Normalize(raw);
        string[] tokens = raw.Split([' ', '　'], StringSplitOptions.RemoveEmptyEntries).Select(Normalize).Where(t => t.Length > 0).ToArray();
        // Queries longer than the reusable DP buffers get no fuzzy tier at all — at that length the
        // substring and token tiers are the meaningful ones anyway.
        int budget = norm.Length > MaxFuzzyPatternLength ? 0 : norm.Length >= 8 ? 2 : norm.Length >= 4 ? 1 : 0;
        return new(norm, tokens, budget, CharMask(norm));
    }

    private static ulong CharMask(string s)
    {
        var mask = 0UL;
        foreach (char c in s) mask |= 1UL << (c & 63);
        return mask;
    }

    // Folds away the differences a searcher never means: case, full-width/half-width latin and digits,
    // katakana vs hiragana, whitespace and interpuncts. "きるくーる" finds "キルクールダウン".
    private static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";

        var sb = new StringBuilder(s.Length);

        foreach (char raw in s)
        {
            char c = raw;
            if (c is >= 'Ａ' and <= 'Ｚ') c = (char)(c - 'Ａ' + 'A');
            else if (c is >= 'ａ' and <= 'ｚ') c = (char)(c - 'ａ' + 'a');
            else if (c is >= '０' and <= '９') c = (char)(c - '０' + '0');
            else if (c is >= 'ァ' and <= 'ヶ') c = (char)(c - 0x60);

            if (char.IsWhiteSpace(c) || c is '・' or '･') continue;

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    // 0 = no match. Tiers, best first: plain substring of the normalized name (bonus for prefix and for
    // short names, so suggestions rank the tightest hit on top), all tokens of a multi-word query found
    // somewhere, then a fuzzy substring within the typo budget.
    public static int Score(in Query q, string target) => Score(q, target, CharMask(target));

    private static int Score(in Query q, string target, ulong targetMask)
    {
        if (q.Norm.Length == 0 || target.Length == 0) return 0;

        if (target.Contains(q.Norm))
            return 100 + (target.StartsWith(q.Norm) ? 20 : 0) + Math.Max(0, 20 - (target.Length / 2));

        if (q.Tokens.Length > 1)
        {
            var all = true;

            foreach (string token in q.Tokens)
            {
                if (target.Contains(token)) continue;

                all = false;
                break;
            }

            if (all) return 60;
        }

        if (q.FuzzyBudget > 0)
        {
            // Cheap lower bounds before the DP: a name shorter than the pattern needs at least the
            // missing length in edits, and every query char whose bit is absent from the name's char
            // bloom costs at least one edit. Both prove distance > budget without running the DP —
            // which is what makes a per-keystroke full-menu scan affordable.
            if (q.Norm.Length - target.Length > q.FuzzyBudget) return 0;

            var missing = 0;
            foreach (char c in q.Norm)
            {
                if ((targetMask & (1UL << (c & 63))) != 0) continue;
                if (++missing > q.FuzzyBudget) return 0;
            }

            int d = FuzzySubstringDistance(q.Norm, target);
            if (d <= q.FuzzyBudget) return 40 - (d * 10);
        }

        return 0;
    }

    // Reused DP rows (single-threaded: everything here runs on the main thread). Bounded by the max
    // pattern length ParseQuery allows into the fuzzy tier.
    private const int MaxFuzzyPatternLength = 64;
    private static readonly int[] FuzzyRowA = new int[MaxFuzzyPatternLength + 1];
    private static readonly int[] FuzzyRowB = new int[MaxFuzzyPatternLength + 1];

    // Minimum edit distance between the pattern and ANY substring of the text (first DP row zeroed so a
    // match may start anywhere). Names are tens of characters, so the quadratic DP is nothing.
    private static int FuzzySubstringDistance(string pattern, string text)
    {
        int m = pattern.Length;
        int[] prev = FuzzyRowA;
        int[] cur = FuzzyRowB;
        for (var j = 0; j <= m; j++) prev[j] = j;

        int best = prev[m];

        foreach (char tc in text)
        {
            cur[0] = 0;

            for (var j = 1; j <= m; j++)
            {
                int cost = pattern[j - 1] == tc ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, cur) = (cur, prev);
            if (prev[m] < best) best = prev[m];
        }

        return best;
    }

    private static int MatchScore(in Query q, int index, OptionItem option)
    {
        (string cur, string en, ulong curMask, ulong enMask) = NamesOf(index, option);
        int score = Score(q, cur, curMask);
        if (en.Length > 0) score = Math.Max(score, Score(q, en, enMask));
        return score;
    }

    // Call once per query, BEFORE iterating options: the stamp reads TranslationController through
    // IL2CPP, which is far too expensive to repeat per option per keystroke.
    private static void EnsureNameCacheFresh()
    {
        int stamp = HashCode.Combine((int)TranslationController.Instance.currentLanguage.languageID, Options.IsLoaded ? Options.ModLanguage.GetValue() : -1, OptionItem.AllOptions.Count);

        if (stamp == NameCacheStamp) return;

        NameCache.Clear();
        NameCacheStamp = stamp;
    }

    private static (string Cur, string En, ulong CurMask, ulong EnMask) NamesOf(int index, OptionItem option)
    {
        if (NameCache.TryGetValue(index, out (string Cur, string En, ulong CurMask, ulong EnMask) cached)) return cached;

        string cur = Normalize(Translator.GetString(option.Name));
        string en = Normalize(Translator.GetString(option.Name, SupportedLangs.English));
        if (en == cur) en = "";

        (string, string, ulong, ulong) entry = (cur, en, CharMask(cur), CharMask(en));
        NameCache[index] = entry;
        return entry;
    }

    // The options a search can hit: real options (not headers) on tabs relevant to the current game
    // mode, not hidden by their own visibility rules. Collapsed sections still count — collapsing is a
    // view state, not "this option is gone".
    private static IEnumerable<(int Index, OptionItem Option)> SearchableOptions(HashSet<TabGroup> relevantTabs)
    {
        for (var index = 0; index < OptionItem.AllOptions.Count; index++)
        {
            OptionItem option = OptionItem.AllOptions[index];
            if (option is TextOptionItem || !relevantTabs.Contains(option.Tab)) continue;
            if (option.IsCurrentlyHidden(checkCollapsedSection: false)) continue;

            yield return (index, option);
        }
    }

    private static HashSet<TabGroup> RelevantTabs() => Main.TabGroupValues.Where(t => t.IsRelevant() && Options.GroupedOptions.ContainsKey(t)).ToHashSet();

    // What the box shows while typing: how many options the query would hit, and the single best-matching
    // name. A full name list was dropped on purpose — Suggest and Apply run the same matcher, so every
    // listed name was already guaranteed to appear in the search results, making the list pure duplication.
    // The count is what the result toast reports (matched options, parents excluded), so the preview and
    // the search agree.
    public readonly record struct SearchPreview(int Hits, string Top);

    public static SearchPreview PreviewQuery(string rawQuery)
    {
        Query q = ParseQuery(rawQuery);
        if (q.Norm.Length == 0) return new(0, "");

        EnsureNameCacheFresh();

        var hits = 0;
        var bestScore = 0;
        var bestIndex = -1;
        var bestLength = int.MaxValue;

        foreach ((int index, OptionItem option) in SearchableOptions(RelevantTabs()))
        {
            int score = MatchScore(q, index, option);
            if (score == 0) continue;

            hits++;

            // Same ranking the result list uses: best score wins, shorter name breaks a tie (the tightest
            // hit reads as the obvious one). Compared on the cached normalized length so the display
            // translation is only fetched once, for the winner.
            int length = NamesOf(index, option).Cur.Length;
            if (score > bestScore || (score == bestScore && length < bestLength))
            {
                bestScore = score;
                bestIndex = index;
                bestLength = length;
            }
        }

        return new(hits, bestIndex >= 0 ? Translator.GetString(OptionItem.AllOptions[bestIndex].Name) : "");
    }

    public static void RememberBorrowedRow(int index, Transform home)
    {
        if (Active && home) BorrowedRows.TryAdd(index, home);
    }

    // The single question the renderer asks: does this option belong in what is currently being drawn?
    public static bool IsInView(OptionItem option, int index, TabGroup modTab)
    {
        return Active ? ResultIndices.Contains(index) : option.Tab == modTab;
    }

    public static bool IsInView(int index, TabGroup modTab)
    {
        return index >= 0 && index < OptionItem.AllOptions.Count && IsInView(OptionItem.AllOptions[index], index, modTab);
    }

    // How many matched options a single search may put on screen. Every admitted option becomes a live
    // row GameObject under one container — a one-character query admits thousands, and building those in
    // one go is a multi-second main-thread stall plus hundreds of MB of native UI that never comes back.
    // The cap keeps the list to what a person would actually scan; the toast tells them it was cut.
    public const int MaxResults = 200;

    public readonly record struct SearchResult(Dictionary<TabGroup, int> Counts, int Total, int Shown);

    // Builds the result list for the query. Returns the hit count per tab (all matches, even the ones
    // the cap dropped); an empty result leaves the current view untouched (the caller reports "no
    // results" instead of emptying the menu). Shown < Total means the cap kicked in.
    public static SearchResult Apply(string query)
    {
        var counts = new Dictionary<TabGroup, int>();
        var hits = new List<(int Index, int Score)>();

        Query q = ParseQuery(query);

        EnsureNameCacheFresh();

        foreach ((int index, OptionItem option) in SearchableOptions(RelevantTabs()))
        {
            int score = MatchScore(q, index, option);
            if (score == 0) continue;

            hits.Add((index, score));
            counts[option.Tab] = counts.GetValueOrDefault(option.Tab) + 1;
        }

        if (hits.Count == 0) return new(counts, 0, 0);

        int shown = hits.Count;

        if (hits.Count > MaxResults)
        {
            // Keep the best-scoring hits; equal scores keep menu order so the cut is deterministic.
            hits.Sort((a, b) => a.Score != b.Score ? b.Score.CompareTo(a.Score) : a.Index.CompareTo(b.Index));
            hits.RemoveRange(MaxResults, hits.Count - MaxResults);
            shown = MaxResults;
        }

        var indexOf = new Dictionary<OptionItem, int>(OptionItem.AllOptions.Count);
        for (var index = 0; index < OptionItem.AllOptions.Count; index++) indexOf[OptionItem.AllOptions[index]] = index;

        var matches = new HashSet<int>();

        foreach ((int index, _) in hits)
        {
            matches.Add(index);

            // The parent chain rides along (uncounted) so a matched sub-option is shown under the
            // role/section it belongs to.
            for (OptionItem parent = OptionItem.AllOptions[index].Parent; parent != null; parent = parent.Parent)
                if (indexOf.TryGetValue(parent, out int parentIndex)) matches.Add(parentIndex);
        }

        ResultIndices.Clear();
        ResultIndices.UnionWith(matches);
        Active = true;
        return new(counts, counts.Values.Sum(), shown);
    }

    // True when a search was actually dropped, so the caller knows a full rebuild is owed — the returned
    // rows are back under their own tab but still sit at the positions the result list gave them.
    public static bool Clear()
    {
        if (!Active)
        {
            BorrowedRows.Clear();
            return false;
        }

        Active = false;
        ResultIndices.Clear();

        foreach (KeyValuePair<int, Transform> borrowed in BorrowedRows)
        {
            Transform home = borrowed.Value;
            if (!home || !ModGameOptionsMenu.BehaviourList.TryGetValue(borrowed.Key, out OptionBehaviour row)) continue;
            if (!row || row.transform.parent == home) continue;

            row.transform.SetParent(home, false);

            // The click mask belongs to the tab, so the row needs its home tab's mask back.
            foreach (var (_, menu) in GameSettingMenuPatch.GetModSettingsTabs())
            {
                if (!menu || menu.settingsContainer != home) continue;

                row.SetClickMask(menu.ButtonClickMask);
                break;
            }
        }

        BorrowedRows.Clear();
        return true;
    }
}
