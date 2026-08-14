using System;
using System.IO;
using EndKnot.Modules.Ekm;
using Xunit;

namespace EndKnot.Tests;

// Modules/Ekm/EkrDefinition.cs + EkmLogicRuntime.cs + EkrPassives.cs — EKR 役職コードの契約テスト
// (正典: docs/ekr-logic-spec.md)。
//
// 主目的は **TS (editor/src/roledef.ts) と C# の検証規則の非対称を機械検出する網**。
// editor/tests/fixtures/ の実 fixture をそのまま食わせるので、TS 側だけが通る/C# 側だけが通る形の
// 差分 (Wave 1 で実際に検出された整数トークン `2.0` 等) は以後ここで落ちる。
// ⚠ fixture ファイルは TS 側の資材なので編集しないこと。
public class EkrDefinitionTests
{
    private static string FixturePath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "fixtures", fileName);
    }

    // ── TS fixture の相互運用 ────────────────────────────────────────────

    [Fact]
    public void FullCourseFixture_IsAcceptedByCSharpValidator()
    {
        string json = File.ReadAllText(FixturePath("role-full-course.ekrole.json"));

        Assert.True(EkrDefinition.TryParse(json, out EkrDefinition def, out string error), error);
        Assert.NotNull(def.ParsedLogic);
        Assert.NotEmpty(def.ParsedLogic.Rules);
    }

    [Fact]
    public void FullCourseFixture_ExposesPassivesAndOnAttacked()
    {
        string json = File.ReadAllText(FixturePath("role-full-course.ekrole.json"));
        Assert.True(EkrDefinition.TryParse(json, out EkrDefinition def, out string error), error);

        // passives 6キーが AST まで通っていること (§1.1)
        EkrPassives p = def.ParsedPassives;
        Assert.NotNull(p);
        Assert.True(p.HasSpeed);
        Assert.True(p.HasShield);
        Assert.True(p.HasDoom);
        Assert.True(p.KillDistance is >= 0 and <= 2);
        Assert.NotEqual("normal", p.Corpse);
        Assert.NotEqual(1, p.VoteWeight);

        // on_attacked ルールが受理されていること (§2)
        Assert.Contains(def.ParsedLogic.Rules, r => r.When == "on_attacked");
    }

    // R2 (docs/ekn-r2-contract.md §3b,§4): 共有 fixture に載せた kind / cause / disguise が
    // C# 側でも同じ値まで通ること (TS↔C# の drift 検出網)。
    [Fact]
    public void FullCourseFixture_ExposesR2KindCauseAndDisguise()
    {
        string json = File.ReadAllText(FixturePath("role-full-course.ekrole.json"));
        Assert.True(EkrDefinition.TryParse(json, out EkrDefinition def, out string error), error);

        Assert.Equal("force", Assert.Single(def.ParsedLogic.Rules, r => r.When == "on_attacked").Kind);
        Assert.Equal("poison-curse", Assert.Single(def.ParsedLogic.Rules, r => r.When == "on_death").Cause);
        Assert.Equal(EkrTeam.Neutral, def.ParsedPassives.DisguiseTeam);
    }

    // ── 個別の契約ケース ──────────────────────────────────────────────

    private static string Wrap(string passivesAndLogic)
    {
        return "{\"ekr\":1,\"name\":\"t\",\"color\":\"#112233\",\"team\":\"crewmate\"," + passivesAndLogic + "}";
    }

    private const string MinimalRule = "\"rules\":[{\"when\":\"on_pet\",\"do\":[{\"op\":\"stop\"}]}]";

    // R2 (docs/ekn-r2-contract.md §1): team は3値受理。madmate / coven は対象外。
    // TS 側 (roledef.ts SUPPORTED_TEAMS) と同じ集合・同じ既定であること。
    [Theory]
    [InlineData("crewmate", true, EkrTeam.Crewmate)]
    [InlineData("impostor", true, EkrTeam.Impostor)]
    [InlineData("neutral", true, EkrTeam.Neutral)]
    [InlineData("Impostor", true, EkrTeam.Impostor)] // 大文字小文字は正規化される
    [InlineData("madmate", false, EkrTeam.Crewmate)]
    [InlineData("coven", false, EkrTeam.Crewmate)]
    [InlineData("", false, EkrTeam.Crewmate)]
    public void Team_AcceptsThreeValuesOnly(string team, bool expectOk, EkrTeam expected)
    {
        string json = "{\"ekr\":1,\"name\":\"t\",\"color\":\"#112233\",\"team\":\"" + team + "\"}";

        Assert.Equal(expectOk, EkrDefinition.TryParse(json, out EkrDefinition def, out _));
        if (expectOk) Assert.Equal(expected, def.ParsedTeam);
    }

    // R2 (契約 §3b): kind は on_attacked 専用 / cause は on_death 専用。他イベントに付いていたら
    // slot と同じく文書 reject (TS 側 readRuleFilter と同じ規則)。
    [Theory]
    [InlineData("\"when\":\"on_attacked\",\"kind\":\"force\"", true)]
    [InlineData("\"when\":\"on_attacked\",\"kind\":\"vote\"", false)] // cause の語彙は kind では使えない
    [InlineData("\"when\":\"on_pet\",\"kind\":\"kill\"", false)] // イベント違い
    [InlineData("\"when\":\"on_death\",\"cause\":\"poison-curse\"", true)]
    [InlineData("\"when\":\"on_death\",\"cause\":\"force\"", false)] // kind の語彙は cause では使えない
    [InlineData("\"when\":\"on_pet\",\"cause\":\"kill\"", false)] // イベント違い
    public void RuleFilters_AreEventScoped(string ruleHead, bool expectOk)
    {
        string json = Wrap("\"logic\":{\"version\":1,\"rules\":[{" + ruleHead + ",\"do\":[{\"op\":\"stop\"}]}]}");

        Assert.Equal(expectOk, EkrDefinition.TryParse(json, out _, out _));
    }

    // team キーそのものが無い場合は既定の crewmate (後方互換 — R0/R1 の役職コードがそのまま動く)。
    [Fact]
    public void Team_Omitted_DefaultsToCrewmate()
    {
        Assert.True(EkrDefinition.TryParse("{\"ekr\":1,\"name\":\"t\"}", out EkrDefinition def, out string error), error);
        Assert.Equal(EkrTeam.Crewmate, def.ParsedTeam);
    }

    // spec §1: `logic.variables: null` は型不一致として文書 reject (「省略扱い」にしない)。
    [Fact]
    public void Logic_VariablesNull_IsRejected()
    {
        string json = Wrap("\"logic\":{\"version\":1,\"variables\":null," + MinimalRule + "}");

        Assert.False(EkrDefinition.TryParse(json, out _, out string error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    // spec §1 (2026-08-11 裁定): 整数フィールドは小数点表記でも整数と等価なら受理 (`2.0` = `2`)。
    // TS は JSON.parse 後にトークン表記を区別できないので、C# 側を合わせるのが唯一の整合方向。
    [Fact]
    public void IntegerFields_DecimalNotation_IsAccepted()
    {
        string json = Wrap(
            "\"passives\":{\"voteWeight\":2.0,\"shield\":{\"count\":3.0},\"doom\":{\"seconds\":60.0}}," +
            "\"logic\":{\"version\":1.0,\"rules\":[{\"when\":\"on_pet\",\"do\":[" +
            "{\"op\":\"cno_spawn\",\"slot\":1.0,\"text\":\"a\",\"size\":8.0,\"at\":\"self\"}]}]}");

        Assert.True(EkrDefinition.TryParse(json, out EkrDefinition def, out string error), error);
        Assert.Equal(2, def.ParsedPassives.VoteWeight);
        Assert.Equal(3, def.ParsedPassives.ShieldCount);
        Assert.Equal(60, def.ParsedPassives.DoomSeconds);
        Assert.Equal(1, def.ParsedLogic.Rules[0].Do[0].Slot);
        Assert.Equal(8, def.ParsedLogic.Rules[0].Do[0].Size);
    }

    // 非整数は従来どおり reject (上の緩和が「なんでも通る」への退化になっていないこと)。
    [Fact]
    public void IntegerFields_NonIntegerValue_IsRejected()
    {
        string json = Wrap("\"passives\":{\"voteWeight\":2.5}," + "\"logic\":{\"version\":1," + MinimalRule + "}");

        Assert.False(EkrDefinition.TryParse(json, out _, out _));
    }

    // spec §1.1: shield は `{ "count": 1..9 }`。count 欠落 (空オブジェクト) は文書 reject。
    [Fact]
    public void Passives_ShieldWithoutCount_IsRejected()
    {
        string json = Wrap("\"passives\":{\"shield\":{}}");

        Assert.False(EkrDefinition.TryParse(json, out _, out string error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    // spec §3: cancel_attack は on_attacked 以外の rule 配下 (if の入れ子含む) に現れたら文書 reject。
    [Fact]
    public void CancelAttack_OutsideOnAttacked_IsRejected()
    {
        string json = Wrap("\"logic\":{\"version\":1,\"rules\":[{\"when\":\"on_pet\",\"do\":[" +
                           "{\"op\":\"if\",\"cond\":{\"e\":\"lit\",\"v\":1},\"then\":[{\"op\":\"cancel_attack\"}]}]}]}");

        Assert.False(EkrDefinition.TryParse(json, out _, out _));
    }

    [Fact]
    public void CancelAttack_InsideOnAttacked_IsAccepted()
    {
        string json = Wrap("\"logic\":{\"version\":1,\"rules\":[{\"when\":\"on_attacked\",\"do\":[" +
                           "{\"op\":\"cancel_attack\"},{\"op\":\"kill\",\"target\":\"ctx\"}]}]}");

        Assert.True(EkrDefinition.TryParse(json, out _, out string error), error);
    }

    // spec §2: slot フィールドを持てるのは on_cno_touch だけ (on_attacked に付けたら reject)。
    [Fact]
    public void OnAttacked_WithSlotField_IsRejected()
    {
        string json = Wrap("\"logic\":{\"version\":1,\"rules\":[{\"when\":\"on_attacked\",\"slot\":1,\"do\":[" +
                           "{\"op\":\"cancel_attack\"}]}]}");

        Assert.False(EkrDefinition.TryParse(json, out _, out _));
    }

    // ── Wave 1 統一セレクタの受理集合 (spec §3) ────────────────────────────
    // TS 側 (blocks-role.ts のドロップダウン / roledef.ts のミラー) が広がった/狭まったときに
    // C# だけ取り残される事故を落とす網。単/複の型規律もここで固定する。

    private static string LogicWithOp(string opJson)
    {
        return Wrap("\"logic\":{\"version\":1,\"rules\":[{\"when\":\"on_pet\",\"do\":[" + opJson + "]}]}");
    }

    [Theory]
    // kill は単数セレクタのみ (§3 型規律)
    [InlineData("{\"op\":\"kill\",\"target\":\"saved1\"}", true)]
    [InlineData("{\"op\":\"kill\",\"target\":\"nearest\"}", true)]
    [InlineData("{\"op\":\"kill\",\"target\":\"random\"}", true)]
    [InlineData("{\"op\":\"kill\",\"target\":\"all\"}", false)]
    [InlineData("{\"op\":\"kill\",\"target\":\"room\"}", false)]
    // notify は複数セレクタを受理する唯一の op
    [InlineData("{\"op\":\"notify\",\"text\":\"a\",\"seconds\":2,\"target\":\"all\"}", true)]
    [InlineData("{\"op\":\"notify\",\"text\":\"a\",\"seconds\":2,\"target\":\"room\"}", true)]
    [InlineData("{\"op\":\"notify\",\"text\":\"a\",\"seconds\":2}", true)] // target 省略 = self
    [InlineData("{\"op\":\"notify\",\"text\":\"a\",\"seconds\":2,\"target\":\"nonsense\"}", false)]
    // 空間セレクタ cno1..3 (Wave 1 追加)
    [InlineData("{\"op\":\"teleport\",\"to\":\"cno1\"}", true)]
    [InlineData("{\"op\":\"teleport_other\",\"target\":\"saved2\",\"to\":\"cno3\"}", true)]
    [InlineData("{\"op\":\"teleport_other\",\"target\":\"self\",\"to\":\"self\"}", false)] // 自分は teleport の役目
    // remember (slot 1..2・単数セレクタ)
    [InlineData("{\"op\":\"remember\",\"slot\":2,\"target\":\"nearest\"}", true)]
    [InlineData("{\"op\":\"remember\",\"slot\":3,\"target\":\"ctx\"}", false)]
    [InlineData("{\"op\":\"remember\",\"slot\":1,\"target\":\"all\"}", false)]
    public void SelectorVocabulary_MatchesContract(string opJson, bool shouldAccept)
    {
        bool ok = EkrDefinition.TryParse(LogicWithOp(opJson), out _, out string error);
        Assert.True(ok == shouldAccept, shouldAccept ? error : "本来 reject されるべき op が受理されました: " + opJson);
    }

    // ── Wave 2 (docs/ekn-wave2-contract.md): 新イベント2種+新 op 10種 ────────────────────────────

    [Fact]
    public void FullCourseFixture_ExposesWave2Rules()
    {
        string json = File.ReadAllText(FixturePath("role-full-course.ekrole.json"));
        Assert.True(EkrDefinition.TryParse(json, out EkrDefinition def, out string error), error);

        Assert.Contains(def.ParsedLogic.Rules, r => r.When == "on_meeting_vote");
        Assert.Contains(def.ParsedLogic.Rules, r => r.When == "on_meeting_pick");
    }

    // spec §1.3: cancel_vote は on_meeting_vote 以外の rule 配下に現れたら文書 reject (cancel_attack と同じ厳格側)。
    [Fact]
    public void CancelVote_OutsideOnMeetingVote_IsRejected()
    {
        string json = Wrap("\"logic\":{\"version\":1,\"rules\":[{\"when\":\"on_pet\",\"do\":[{\"op\":\"cancel_vote\"}]}]}");
        Assert.False(EkrDefinition.TryParse(json, out _, out _));
    }

    [Fact]
    public void CancelVote_InsideOnMeetingVote_IsAccepted()
    {
        string json = Wrap("\"logic\":{\"version\":1,\"rules\":[{\"when\":\"on_meeting_vote\",\"do\":[{\"op\":\"cancel_vote\"}]}]}");
        Assert.True(EkrDefinition.TryParse(json, out _, out string error), error);
    }

    // TS coordinator 裁定 (2026-08-11): vote_block/vote_swap/exile はどの rule 配下でも検証 reject しない
    // (リンタ L18 の警告のみ・実行時に会議中でなければ no-op)。
    [Theory]
    [InlineData("{\"op\":\"vote_block\",\"target\":\"nearest\"}")]
    [InlineData("{\"op\":\"vote_swap\"}")]
    [InlineData("{\"op\":\"exile\",\"target\":\"self\"}")]
    public void MeetingOnlyOps_AnyRulePlacement_IsAccepted(string opJson)
    {
        Assert.True(EkrDefinition.TryParse(LogicWithOp(opJson), out _, out string error), error);
    }

    // spec §2.1: inspect の self 不可・noise は depth="role" のみ受理 (TS と同じ「noise>0 かつ depth=team」判定)。
    [Theory]
    [InlineData("{\"op\":\"inspect\",\"target\":\"self\",\"depth\":\"team\"}", false)]
    [InlineData("{\"op\":\"inspect\",\"target\":\"nearest\",\"depth\":\"team\"}", true)]
    [InlineData("{\"op\":\"inspect\",\"target\":\"nearest\",\"depth\":\"team\",\"noise\":0}", true)] // noise:0 は team 併用でも許容
    [InlineData("{\"op\":\"inspect\",\"target\":\"nearest\",\"depth\":\"team\",\"noise\":2}", false)]
    [InlineData("{\"op\":\"inspect\",\"target\":\"nearest\",\"depth\":\"role\",\"noise\":2}", true)]
    [InlineData("{\"op\":\"inspect\",\"target\":\"nearest\",\"depth\":\"role\",\"failChance\":50}", true)]
    [InlineData("{\"op\":\"inspect\",\"target\":\"nearest\",\"depth\":\"role\",\"noise\":6}", false)] // 範囲外 (0..5)
    public void Inspect_MatchesContract(string opJson, bool shouldAccept)
    {
        bool ok = EkrDefinition.TryParse(LogicWithOp(opJson), out _, out string error);
        Assert.True(ok == shouldAccept, shouldAccept ? error : "本来 reject されるべき op が受理されました: " + opJson);
    }

    // spec §2.2/§2.3/§3.2: self 不可の対象セレクタ (reveal/arrow_show/vote_block) と self 可の exile。
    [Theory]
    [InlineData("{\"op\":\"reveal\",\"target\":\"self\"}", false)]
    [InlineData("{\"op\":\"reveal\",\"target\":\"ctx\"}", true)]
    [InlineData("{\"op\":\"arrow_show\",\"target\":\"self\",\"seconds\":10}", false)]
    [InlineData("{\"op\":\"arrow_show\",\"target\":\"ctx\",\"seconds\":10}", true)]
    [InlineData("{\"op\":\"vote_block\",\"target\":\"self\"}", false)]
    [InlineData("{\"op\":\"vote_block\",\"target\":\"ctx\"}", true)]
    [InlineData("{\"op\":\"exile\",\"target\":\"self\"}", true)]
    [InlineData("{\"op\":\"exile\",\"target\":\"ctx\"}", true)]
    public void Wave2SelfSelectorRestrictions_MatchContract(string opJson, bool shouldAccept)
    {
        bool ok = EkrDefinition.TryParse(LogicWithOp(opJson), out _, out string error);
        Assert.True(ok == shouldAccept, shouldAccept ? error : "本来 reject されるべき op が受理されました: " + opJson);
    }

    // spec §2.3: arrow_mark の at 受理値 (ctx/marker1-4/cno1-3) と秒数範囲 (5..600)。
    [Theory]
    [InlineData("{\"op\":\"arrow_mark\",\"at\":\"ctx\",\"seconds\":60}", true)]
    [InlineData("{\"op\":\"arrow_mark\",\"at\":\"marker2\",\"seconds\":600}", true)]
    [InlineData("{\"op\":\"arrow_mark\",\"at\":\"cno1\",\"seconds\":5}", true)]
    [InlineData("{\"op\":\"arrow_mark\",\"at\":\"ctx\",\"seconds\":4}", false)] // 範囲外
    [InlineData("{\"op\":\"arrow_mark\",\"at\":\"self\",\"seconds\":10}", false)] // self は arrow_mark.at に無い
    public void ArrowMark_MatchesContract(string opJson, bool shouldAccept)
    {
        bool ok = EkrDefinition.TryParse(LogicWithOp(opJson), out _, out string error);
        Assert.True(ok == shouldAccept, shouldAccept ? error : "本来 reject されるべき op が受理されました: " + opJson);
    }

    // spec §3.1: vote_weight_set.value は 0..3。
    [Theory]
    [InlineData("{\"op\":\"vote_weight_set\",\"value\":0}", true)]
    [InlineData("{\"op\":\"vote_weight_set\",\"value\":3}", true)]
    [InlineData("{\"op\":\"vote_weight_set\",\"value\":4}", false)]
    [InlineData("{\"op\":\"vote_weight_set\",\"value\":-1}", false)]
    public void VoteWeightSet_MatchesContract(string opJson, bool shouldAccept)
    {
        bool ok = EkrDefinition.TryParse(LogicWithOp(opJson), out _, out string error);
        Assert.True(ok == shouldAccept, shouldAccept ? error : "本来 reject されるべき op が受理されました: " + opJson);
    }

    // arrow_hide / vote_swap は引数なし。
    [Fact]
    public void NoArgOps_AreAccepted()
    {
        Assert.True(EkrDefinition.TryParse(LogicWithOp("{\"op\":\"arrow_hide\"}"), out _, out string e1), e1);
        Assert.True(EkrDefinition.TryParse(LogicWithOp("{\"op\":\"vote_swap\"}"), out _, out string e2), e2);
    }

    // passives 無しでも R0 動作 (完全後方互換) — ParsedPassives は既定インスタンスで非 null。
    [Fact]
    public void NoPassives_UsesDefaults()
    {
        Assert.True(EkrDefinition.TryParse(Wrap("\"canVent\":true"), out EkrDefinition def, out string error), error);

        Assert.NotNull(def.ParsedPassives);
        Assert.False(def.ParsedPassives.HasSpeed);
        Assert.False(def.ParsedPassives.HasShield);
        Assert.False(def.ParsedPassives.HasDoom);
        Assert.Equal(1, def.ParsedPassives.VoteWeight);
        Assert.Equal("normal", def.ParsedPassives.Corpse);
        Assert.Equal(-1, def.ParsedPassives.KillDistance);
    }
}
