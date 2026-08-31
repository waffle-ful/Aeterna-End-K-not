using System;
using System.Collections.Generic;
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

    // plan §7 Tier 1 #2: 説明文2欄。TS 側 (role-fixtures.test.ts) が同じファイルの同じ値を見ている。
    [Fact]
    public void FullCourseFixture_ExposesDescriptions()
    {
        string json = File.ReadAllText(FixturePath("role-full-course.ekrole.json"));
        Assert.True(EkrDefinition.TryParse(json, out EkrDefinition def, out string error), error);

        Assert.Equal("影を渡り歩き、触れた者の運命を書き換える", def.Description);
        Assert.Contains("\n", def.DescriptionLong);
        Assert.StartsWith("影を渡り歩く役職です。", def.DescriptionLong);
    }

    [Fact]
    public void Descriptions_AreOptional_AndClampedToTheContractLimits()
    {
        // 説明文なし = 旧い役職コード (欠落は空文字へ収束し、ゲーム側は既定文言のまま)
        Assert.True(EkrDefinition.TryParse(Wrap(""), out EkrDefinition plain, out string error), error);
        Assert.Equal("", plain.Description);
        Assert.Equal("", plain.DescriptionLong);

        // 短文は改行を空白へ潰して1行にし、上限 80 / 400 で切る
        string longDesc = new('あ', 100);
        string longDescLong = new('い', 500);
        string json = $$"""
                        {"ekr":1,"name":"テスト","description":"1行目\n2行目","descriptionLong":"  前後の空白は落ちる  "}
                        """;
        Assert.True(EkrDefinition.TryParse(json, out EkrDefinition def, out error), error);
        Assert.Equal("1行目 2行目", def.Description);
        Assert.Equal("前後の空白は落ちる", def.DescriptionLong);

        string clampJson = $$"""
                             {"ekr":1,"name":"テスト","description":"{{longDesc}}","descriptionLong":"{{longDescLong}}"}
                             """;
        Assert.True(EkrDefinition.TryParse(clampJson, out EkrDefinition clamped, out error), error);
        Assert.Equal(80, clamped.Description.Length);
        Assert.Equal(400, clamped.DescriptionLong.Length);
    }

    // 非モッド客の名前ペイロードに載るのは InfoLong の「最初の \n\n の手前」だけなので、
    // 組み立てが見出し構造を失うと長い説明が役職マークを押し出す。
    [Fact]
    public void BuildInfoLongOverride_AlwaysKeepsTheHeadlineParagraphShort()
    {
        string body = new('あ', 400);

        // 短文あり: 短文が見出しになる
        Assert.True(EkrDefinition.TryParse($$"""
                                             {"ekr":1,"name":"t","description":"短い説明","descriptionLong":"{{body}}"}
                                             """, out EkrDefinition both, out string error), error);
        string builtBoth = both.BuildInfoLongOverride();
        Assert.Equal("短い説明", builtBoth.Split("\n\n")[0]);
        Assert.EndsWith(body, builtBoth);

        // 短文なし: 詳細の1行目 (≤80字) が見出しになる
        Assert.True(EkrDefinition.TryParse($$"""
                                             {"ekr":1,"name":"t","descriptionLong":"{{body}}"}
                                             """, out EkrDefinition longOnly, out error), error);
        Assert.True(longOnly.BuildInfoLongOverride().Split("\n\n")[0].Length <= 80);

        // 詳細なし: 短文をそのまま流用 (見出しだけの形)
        Assert.True(EkrDefinition.TryParse("""
                                           {"ekr":1,"name":"t","description":"短い説明"}
                                           """, out EkrDefinition shortOnly, out error), error);
        Assert.Equal("短い説明", shortOnly.BuildInfoLongOverride());

        // 両方なし: 空 (呼び出し側が ClearRuntimeOverride して既定文言へ戻す)
        Assert.True(EkrDefinition.TryParse("""{"ekr":1,"name":"t"}""", out EkrDefinition none, out error), error);
        Assert.Equal("", none.BuildInfoLongOverride());
    }

    // 連続改行は空白1つへ畳む (TS の /[\r\n]+/g と同一)。1文字ずつ置換すると両側の切り詰め位置がズレる。
    [Fact]
    public void Description_CollapsesConsecutiveNewlinesIntoOneSpace()
    {
        const string json = """
                            {"ekr":1,"name":"テスト","description":"あ\r\nい\n\nう"}
                            """;

        Assert.True(EkrDefinition.TryParse(json, out EkrDefinition def, out string error), error);
        Assert.Equal("あ い う", def.Description);
    }

    // 上限ちょうどで TMP タグを切ると、閉じていない断片が後続テキストまで壊す (CNO/名前クランプと同型)。
    [Fact]
    public void Descriptions_DropTheUnterminatedTagFragmentWhenClamped()
    {
        // 80字目がタグの途中に落ちるように、76字の本文 + 開きタグを置く
        string body = new('あ', 76);
        string json = $$"""
                        {"ekr":1,"name":"テスト","description":"{{body}}<color=#ff0000>強調</color>"}
                        """;

        Assert.True(EkrDefinition.TryParse(json, out EkrDefinition def, out string error), error);
        Assert.Equal(body, def.Description);
        Assert.DoesNotContain("<", def.Description);
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

    // spec §1: 整数フィールドは小数点表記でも整数と等価なら受理 (`2.0` = `2`)。
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

    // vote_block/vote_swap/exile はどの rule 配下でも検証 reject しない
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

    // ── Wave 3 (docs/ekn-wave3-contract.md) ────────────────────────────────────────────────────

    // §1: 共有 fixture に載せた3イベントが C# 側でも同じ値まで通ること (TS↔C# の drift 検出網)。
    [Fact]
    public void FullCourseFixture_ExposesWave3Triggers()
    {
        string json = File.ReadAllText(FixturePath("role-full-course.ekrole.json"));
        Assert.True(EkrDefinition.TryParse(json, out EkrDefinition def, out string error), error);

        EkrRule onVar = Assert.Single(def.ParsedLogic.Rules, r => r.When == "on_var");
        Assert.Equal("うらみ", onVar.VarName);
        Assert.Equal("ge", onVar.Cmp);
        Assert.Equal(7, onVar.CmpValue);
        Assert.True(onVar.IsStateTrigger);

        EkrRule onAlive = Assert.Single(def.ParsedLogic.Rules, r => r.When == "on_alive_count");
        Assert.Null(onAlive.VarName);
        Assert.Equal("le", onAlive.Cmp);
        Assert.Equal(3, onAlive.CmpValue);
        Assert.True(onAlive.IsStateTrigger);

        EkrRule onVentExit = Assert.Single(def.ParsedLogic.Rules, r => r.When == "on_vent_exit");
        Assert.False(onVentExit.IsStateTrigger);
    }

    // ── Wave 4 (docs/ekn-wave4-contract.md) ────────────────────────────────────────────────────

    // §1〜§3: 共有 fixture に載せた5イベントが C# 側でも同じ値まで通ること (TS↔C# の drift 検出網)。
    // TS 側 (role-fixtures.test.ts「Wave 4 の新5イベントの rule 形が保持される」) と対になる。
    [Fact]
    public void FullCourseFixture_ExposesWave4Triggers()
    {
        string json = File.ReadAllText(FixturePath("role-full-course.ekrole.json"));
        Assert.True(EkrDefinition.TryParse(json, out EkrDefinition def, out string error), error);

        // on_near: radius 必須・who 省略。⚠️ C# は欠落を "anyone" へ焼き込む (TS は AST に載せない)
        // — この非対称は契約 §1.2 の既定値規定どおりで、rolecode ラウンドトリップには出ない。
        EkrRule onNear = Assert.Single(def.ParsedLogic.Rules, r => r.When == "on_near");
        Assert.Equal("small", onNear.Radius);
        Assert.Equal("anyone", onNear.Who);

        // on_far: radius / who とも必須 ("anyone" は文書 reject)
        EkrRule onFar = Assert.Single(def.ParsedLogic.Rules, r => r.When == "on_far");
        Assert.Equal("medium", onFar.Radius);
        Assert.Equal("linked", onFar.Who);

        // 部屋2種: 追加フィールドなし (ctx 無しイベント)
        foreach (string when in new[] { "on_room_enter", "on_room_exit" })
        {
            EkrRule room = Assert.Single(def.ParsedLogic.Rules, r => r.When == when);
            Assert.Null(room.Radius);
            Assert.Null(room.Who);
            Assert.NotEmpty(room.Do);
        }

        // on_linked_death: cause を任意で受ける (on_death と同じ8バケット)
        EkrRule onLinkedDeath = Assert.Single(def.ParsedLogic.Rules, r => r.When == "on_linked_death");
        Assert.Equal("kill", onLinkedDeath.Cause);
        Assert.Null(onLinkedDeath.Radius);
    }


    // ── Wave 5 (docs/ekn-wave5-contract.md §1〜§3): 持続効果と変換先スロット指名 ────────────────

    // 共有 fixture から Wave 5 語彙が AST まで通ること。TS 側 (role-fixtures.test.ts の
    // 「effect_give の4種と recruit.slot が保持される」) と同じファイルの同じ値を読むので、
    // 片側だけ実装が抜けるとどちらかが落ちる。
    [Fact]
    public void FullCourseFixture_ExposesWave5Vocabulary()
    {
        string json = File.ReadAllText(FixturePath("role-full-course.ekrole.json"));
        Assert.True(EkrDefinition.TryParse(json, out EkrDefinition def, out string error), error);

        var effects = new List<EkrNode>();

        foreach (EkrRule rule in def.ParsedLogic.Rules)
            CollectOps(rule.Do, "effect_give", effects);

        // 4 kind すべてを1回以上 (movement 3種 + vision 1種)
        var kinds = new HashSet<string>();
        foreach (EkrNode n in effects) kinds.Add(n.EffectKind);
        Assert.Equal(new HashSet<string> { "haste", "slow", "freeze", "blind" }, kinds);

        // freeze は上限 10 秒 (§1) — fixture は境界を割る値で持つ
        EkrNode freeze = effects.Find(n => n.EffectKind == "freeze");
        Assert.NotNull(freeze);
        Assert.Equal(8f, freeze.Seconds);
        Assert.Equal("nearest", freeze.Target);

        // target 受理集合は単数セレクタ全種 (self / linked とも通る)
        Assert.Contains(effects, n => n.Target == "self");
        Assert.Contains(effects, n => n.Target == "linked");

        // recruit.slot は IntArg に入る (0 = 省略)
        var recruits = new List<EkrNode>();

        foreach (EkrRule rule in def.ParsedLogic.Rules)
            CollectOps(rule.Do, "recruit", recruits);

        EkrNode recruit = Assert.Single(recruits);
        Assert.Equal(3, recruit.IntArg);
    }

    private static void CollectOps(List<EkrNode> nodes, string op, List<EkrNode> into)
    {
        foreach (EkrNode n in nodes)
        {
            if (n.Op == op) into.Add(n);
            if (n.Then != null) CollectOps(n.Then, op, into);
            if (n.Else != null) CollectOps(n.Else, op, into);
        }
    }

    // §1/§3: effect_give の必須3フィールドと kind 別 seconds 上限 (TS 側 roledef.test.ts と同じ表)。
    [Theory]
    [InlineData("{\"op\":\"effect_give\",\"target\":\"ctx\",\"kind\":\"haste\",\"seconds\":10}", true)]
    [InlineData("{\"op\":\"effect_give\",\"target\":\"self\",\"kind\":\"slow\",\"seconds\":1}", true)]
    [InlineData("{\"op\":\"effect_give\",\"target\":\"linked\",\"kind\":\"blind\",\"seconds\":30}", true)]
    [InlineData("{\"op\":\"effect_give\",\"target\":\"nearest\",\"kind\":\"freeze\",\"seconds\":10}", true)]
    // 3フィールドとも必須 (既定を作らない)
    [InlineData("{\"op\":\"effect_give\",\"kind\":\"haste\",\"seconds\":10}", false)]
    [InlineData("{\"op\":\"effect_give\",\"target\":\"ctx\",\"seconds\":10}", false)]
    [InlineData("{\"op\":\"effect_give\",\"target\":\"ctx\",\"kind\":\"haste\"}", false)]
    // 未知 kind / 複数セレクタ / kind 別の上限超過・下限割れ
    [InlineData("{\"op\":\"effect_give\",\"target\":\"ctx\",\"kind\":\"shield\",\"seconds\":10}", false)]
    [InlineData("{\"op\":\"effect_give\",\"target\":\"all\",\"kind\":\"haste\",\"seconds\":10}", false)]
    [InlineData("{\"op\":\"effect_give\",\"target\":\"ctx\",\"kind\":\"haste\",\"seconds\":31}", false)]
    [InlineData("{\"op\":\"effect_give\",\"target\":\"ctx\",\"kind\":\"freeze\",\"seconds\":11}", false)]
    [InlineData("{\"op\":\"effect_give\",\"target\":\"ctx\",\"kind\":\"freeze\",\"seconds\":0}", false)]
    // §2: recruit.slot は任意・整数 1..18 のみ (整数等価トークンは既存規則どおり受理)
    [InlineData("{\"op\":\"recruit\",\"target\":\"ctx\"}", true)]
    [InlineData("{\"op\":\"recruit\",\"target\":\"ctx\",\"slot\":1}", true)]
    [InlineData("{\"op\":\"recruit\",\"target\":\"ctx\",\"slot\":18}", true)]
    [InlineData("{\"op\":\"recruit\",\"target\":\"ctx\",\"slot\":3.0}", true)]
    [InlineData("{\"op\":\"recruit\",\"target\":\"ctx\",\"slot\":0}", false)]
    [InlineData("{\"op\":\"recruit\",\"target\":\"ctx\",\"slot\":19}", false)]
    [InlineData("{\"op\":\"recruit\",\"target\":\"ctx\",\"slot\":2.5}", false)]
    [InlineData("{\"op\":\"recruit\",\"target\":\"ctx\",\"slot\":\"3\"}", false)]
    public void Wave5Ops_MatchTheContract(string nodeJson, bool shouldAccept)
    {
        string json = Wrap("\"logic\":{\"version\":1,\"rules\":[{\"when\":\"on_pet\",\"do\":[" + nodeJson + "]}]}");
        bool ok = EkrDefinition.TryParse(json, out _, out string error);
        Assert.True(ok == shouldAccept, shouldAccept ? error : "本来 reject されるべき node が受理されました: " + nodeJson);
    }

    // §2: recruit.slot 省略時は IntArg = 0 のまま (「自分と同じ役職」の後方互換パス)。
    [Fact]
    public void RecruitSlot_DefaultsToZeroWhenOmitted()
    {
        string json = Wrap("\"logic\":{\"version\":1,\"rules\":[{\"when\":\"on_pet\",\"do\":[{\"op\":\"recruit\",\"target\":\"ctx\"}]}]}");
        Assert.True(EkrDefinition.TryParse(json, out EkrDefinition def, out string error), error);
        Assert.Equal(0, def.ParsedLogic.Rules[0].Do[0].IntArg);
    }

    // §1.2/§1.3: じょうたいトリガの必須フィールドと、他イベントへの付着 reject (slot と同じ厳格側)。
    [Theory]
    [InlineData("{\"when\":\"on_var\",\"var\":\"v\",\"cmp\":\"eq\",\"value\":0,\"do\":[{\"op\":\"stop\"}]}", true)]
    [InlineData("{\"when\":\"on_var\",\"var\":\"v\",\"cmp\":\"le\",\"value\":-5,\"do\":[{\"op\":\"stop\"}]}", true)]
    // on_var の value に範囲は無い (契約が定めていない — 独自の上下限を足すと TS 側と非対称になる)
    [InlineData("{\"when\":\"on_var\",\"var\":\"v\",\"cmp\":\"ge\",\"value\":100000,\"do\":[{\"op\":\"stop\"}]}", true)]
    // 未定義の変数 / 不正な cmp / 式は受理しない
    [InlineData("{\"when\":\"on_var\",\"var\":\"なし\",\"cmp\":\"eq\",\"value\":0,\"do\":[{\"op\":\"stop\"}]}", false)]
    [InlineData("{\"when\":\"on_var\",\"var\":\"v\",\"cmp\":\"lt\",\"value\":0,\"do\":[{\"op\":\"stop\"}]}", false)]
    [InlineData("{\"when\":\"on_var\",\"var\":\"v\",\"cmp\":\"eq\",\"value\":{\"e\":\"lit\",\"v\":0},\"do\":[{\"op\":\"stop\"}]}", false)]
    // var / cmp / value の欠落
    [InlineData("{\"when\":\"on_var\",\"cmp\":\"eq\",\"value\":0,\"do\":[{\"op\":\"stop\"}]}", false)]
    [InlineData("{\"when\":\"on_var\",\"var\":\"v\",\"value\":0,\"do\":[{\"op\":\"stop\"}]}", false)]
    // on_alive_count は var 不可・value は 1..15
    [InlineData("{\"when\":\"on_alive_count\",\"cmp\":\"le\",\"value\":3,\"do\":[{\"op\":\"stop\"}]}", true)]
    [InlineData("{\"when\":\"on_alive_count\",\"cmp\":\"ge\",\"value\":1,\"do\":[{\"op\":\"stop\"}]}", true)]
    [InlineData("{\"when\":\"on_alive_count\",\"cmp\":\"le\",\"value\":15,\"do\":[{\"op\":\"stop\"}]}", true)]
    // 整数等価トークン (spec §1) — TS は JSON.parse 後に表記を区別できないので C# も受理側
    [InlineData("{\"when\":\"on_alive_count\",\"cmp\":\"le\",\"value\":3.0,\"do\":[{\"op\":\"stop\"}]}", true)]
    [InlineData("{\"when\":\"on_var\",\"var\":\"v\",\"cmp\":\"eq\",\"value\":2.0,\"do\":[{\"op\":\"stop\"}]}", true)]
    [InlineData("{\"when\":\"on_var\",\"var\":\"v\",\"cmp\":\"eq\",\"value\":1.5,\"do\":[{\"op\":\"stop\"}]}", false)]
    [InlineData("{\"when\":\"on_alive_count\",\"var\":\"v\",\"cmp\":\"le\",\"value\":3,\"do\":[{\"op\":\"stop\"}]}", false)]
    [InlineData("{\"when\":\"on_alive_count\",\"cmp\":\"le\",\"value\":0,\"do\":[{\"op\":\"stop\"}]}", false)]
    [InlineData("{\"when\":\"on_alive_count\",\"cmp\":\"le\",\"value\":16,\"do\":[{\"op\":\"stop\"}]}", false)]
    // 他イベントへの付着は文書 reject
    [InlineData("{\"when\":\"on_pet\",\"var\":\"v\",\"do\":[{\"op\":\"stop\"}]}", false)]
    [InlineData("{\"when\":\"on_pet\",\"cmp\":\"eq\",\"do\":[{\"op\":\"stop\"}]}", false)]
    [InlineData("{\"when\":\"on_pet\",\"value\":1,\"do\":[{\"op\":\"stop\"}]}", false)]
    // on_vent_exit は追加フィールド無し
    [InlineData("{\"when\":\"on_vent_exit\",\"do\":[{\"op\":\"stop\"}]}", true)]
    public void StateTriggerRules_MatchTheContract(string ruleJson, bool shouldAccept)
    {
        string json = Wrap("\"logic\":{\"version\":1,\"variables\":[{\"name\":\"v\",\"init\":0}],\"rules\":[" + ruleJson + "]}");
        bool ok = EkrDefinition.TryParse(json, out _, out string error);
        Assert.True(ok == shouldAccept, shouldAccept ? error : "本来 reject されるべき rule が受理されました: " + ruleJson);
    }

    // §1.1: cancel_attack / cancel_vote の配置 reject は新イベント配下にも自動で効く (白名単の補集合)。
    [Fact]
    public void CancelOps_StayRejectedUnderTheNewEvents()
    {
        Assert.False(EkrDefinition.TryParse(
            Wrap("\"logic\":{\"version\":1,\"variables\":[{\"name\":\"v\",\"init\":0}],\"rules\":[{\"when\":\"on_var\",\"var\":\"v\",\"cmp\":\"eq\",\"value\":0,\"do\":[{\"op\":\"cancel_attack\"}]}]}"),
            out _, out _));

        Assert.False(EkrDefinition.TryParse(
            Wrap("\"logic\":{\"version\":1,\"rules\":[{\"when\":\"on_vent_exit\",\"do\":[{\"op\":\"cancel_vote\"}]}]}"),
            out _, out _));
    }

    // §3: progress は自由テキスト。タグは全角化・16字でクランプ・空は reject・キーごと無しは後方互換。
    [Fact]
    public void Progress_MatchesTheContract()
    {
        string json = File.ReadAllText(FixturePath("role-full-course.ekrole.json"));
        Assert.True(EkrDefinition.TryParse(json, out EkrDefinition fixtureDef, out string error), error);
        Assert.Equal("うらみ{うらみ}", fixtureDef.ProgressText);

        // 無し = 表示なし (完全後方互換)
        Assert.True(EkrDefinition.TryParse(Wrap("\"canVent\":true"), out EkrDefinition none, out error), error);
        Assert.Equal("", none.ProgressText);

        // TMP タグは書けない (notify.text と同一規約)
        Assert.True(EkrDefinition.TryParse(Wrap("\"progress\":{\"text\":\"<b>あ</b>\"}"), out EkrDefinition tagged, out error), error);
        Assert.DoesNotContain('<', tagged.ProgressText);
        Assert.DoesNotContain('>', tagged.ProgressText);

        // 超過はクランプ (reject にしない = description と同じ寛容側)
        Assert.True(EkrDefinition.TryParse(Wrap("\"progress\":{\"text\":\"" + new string('あ', 40) + "\"}"), out EkrDefinition clamped, out error), error);
        Assert.Equal(16, clamped.ProgressText.Length);

        // キーを書いた以上は表示宣言 — text 欠落・空は reject
        Assert.False(EkrDefinition.TryParse(Wrap("\"progress\":{}"), out _, out _));
        Assert.False(EkrDefinition.TryParse(Wrap("\"progress\":{\"text\":\"   \"}"), out _, out _));
    }

    // §4.1: hostOptions のキー表・重複・var: の参照整合性・min/max の付着規則。
    [Fact]
    public void HostOptions_MatchTheContract()
    {
        string json = File.ReadAllText(FixturePath("role-full-course.ekrole.json"));
        Assert.True(EkrDefinition.TryParse(json, out EkrDefinition fixtureDef, out string error), error);
        Assert.Equal(3, fixtureDef.ParsedHostOptions.Count);
        Assert.Equal("shield.count", fixtureDef.ParsedHostOptions[0].Key);
        Assert.False(fixtureDef.ParsedHostOptions[0].IsVar);
        Assert.True(fixtureDef.ParsedHostOptions[2].IsVar);
        Assert.Equal("うらみ", fixtureDef.ParsedHostOptions[2].VarName);
        Assert.Equal(0, fixtureDef.ParsedHostOptions[2].Min);
        Assert.Equal(20, fixtureDef.ParsedHostOptions[2].Max);

        // 無し = 露出なし (後方互換)
        Assert.True(EkrDefinition.TryParse(Wrap("\"canVent\":true"), out EkrDefinition none, out error), error);
        Assert.Empty(none.ParsedHostOptions);

        // 未対応キー / 重複キー / label 範囲外
        Assert.False(EkrDefinition.TryParse(Wrap("\"hostOptions\":[{\"key\":\"nope\",\"label\":\"あ\"}]"), out _, out _));
        Assert.False(EkrDefinition.TryParse(Wrap("\"hostOptions\":[{\"key\":\"vision\",\"label\":\"あ\"},{\"key\":\"vision\",\"label\":\"い\"}]"), out _, out _));
        Assert.False(EkrDefinition.TryParse(Wrap("\"hostOptions\":[{\"key\":\"vision\",\"label\":\"\"}]"), out _, out _));

        // 固定キーへの min/max 付着は reject (値域は契約側の既存定義が正)
        Assert.False(EkrDefinition.TryParse(Wrap("\"hostOptions\":[{\"key\":\"vision\",\"label\":\"あ\",\"min\":0,\"max\":1}]"), out _, out _));

        // key は trim しない (TS 側と同一 — 片側だけ寛容だと非対称になる)
        Assert.False(EkrDefinition.TryParse(Wrap("\"hostOptions\":[{\"key\":\" vision \",\"label\":\"あ\"}]"), out _, out _));

        // 固定キー6種すべてが受理されること (TS 側 roledef.test.ts と同じ網)
        foreach (string fixedKey in EkrHostOption.FixedKeys)
            Assert.True(EkrDefinition.TryParse(Wrap($"\"hostOptions\":[{{\"key\":\"{fixedKey}\",\"label\":\"あ\"}}]"), out _, out error), $"{fixedKey}: {error}");

        // label は 24字ちょうどまで受理・25字は reject (クランプではなく reject 側)・空白のみも reject
        Assert.True(EkrDefinition.TryParse(Wrap("\"hostOptions\":[{\"key\":\"vision\",\"label\":\"" + new string('あ', 24) + "\"}]"), out _, out error), error);
        Assert.False(EkrDefinition.TryParse(Wrap("\"hostOptions\":[{\"key\":\"vision\",\"label\":\"" + new string('あ', 25) + "\"}]"), out _, out _));
        Assert.False(EkrDefinition.TryParse(Wrap("\"hostOptions\":[{\"key\":\"vision\",\"label\":\"   \"}]"), out _, out _));

        // label の TMP タグは全角化される
        Assert.True(EkrDefinition.TryParse(Wrap("\"hostOptions\":[{\"key\":\"vision\",\"label\":\"<b>あ</b>\"}]"), out EkrDefinition taggedLabel, out error), error);
        Assert.DoesNotContain('<', Assert.Single(taggedLabel.ParsedHostOptions).Label);

        // var: は宣言済み変数のみ + min/max 必須 + min<max
        const string varsLogic = "\"logic\":{\"version\":1,\"variables\":[{\"name\":\"v\",\"init\":2}]," + MinimalRule + "},";
        Assert.True(EkrDefinition.TryParse(Wrap(varsLogic + "\"hostOptions\":[{\"key\":\"var:v\",\"label\":\"あ\",\"min\":1,\"max\":9}]"), out EkrDefinition varOk, out error), error);
        Assert.Equal("v", Assert.Single(varOk.ParsedHostOptions).VarName);

        Assert.False(EkrDefinition.TryParse(Wrap(varsLogic + "\"hostOptions\":[{\"key\":\"var:none\",\"label\":\"あ\",\"min\":1,\"max\":9}]"), out _, out _));
        Assert.False(EkrDefinition.TryParse(Wrap(varsLogic + "\"hostOptions\":[{\"key\":\"var:v\",\"label\":\"あ\"}]"), out _, out _));
        Assert.False(EkrDefinition.TryParse(Wrap(varsLogic + "\"hostOptions\":[{\"key\":\"var:v\",\"label\":\"あ\",\"min\":9,\"max\":1}]"), out _, out _));
        Assert.False(EkrDefinition.TryParse(Wrap(varsLogic + "\"hostOptions\":[{\"key\":\"var:v\",\"label\":\"あ\",\"min\":5,\"max\":5}]"), out _, out _)); // min == max も reject
        Assert.False(EkrDefinition.TryParse(Wrap(varsLogic + "\"hostOptions\":[{\"key\":\"var:v\",\"label\":\"あ\",\"min\":-10000,\"max\":9}]"), out _, out _));

        // logic 自体が無ければ var: は必ず reject (参照できる変数が存在しない)
        Assert.False(EkrDefinition.TryParse(Wrap("\"hostOptions\":[{\"key\":\"var:v\",\"label\":\"あ\",\"min\":1,\"max\":9}]"), out _, out _));

        // 8件ちょうどは受理・9件目は reject (上限8枠)
        var vars = new System.Text.StringBuilder("\"logic\":{\"version\":1,\"variables\":[");
        for (var i = 0; i < 9; i++) vars.Append(i > 0 ? "," : "").Append("{\"name\":\"v").Append(i).Append("\",\"init\":0}");
        vars.Append("],").Append(MinimalRule).Append("},");

        Assert.True(EkrDefinition.TryParse(Wrap(vars + BuildHostOptions(8)), out EkrDefinition eight, out error), error);
        Assert.Equal(8, eight.ParsedHostOptions.Count);
        Assert.False(EkrDefinition.TryParse(Wrap(vars + BuildHostOptions(9)), out _, out _));
        return;

        static string BuildHostOptions(int count)
        {
            var sb = new System.Text.StringBuilder("\"hostOptions\":[");
            for (var i = 0; i < count; i++) sb.Append(i > 0 ? "," : "").Append("{\"key\":\"var:v").Append(i).Append("\",\"label\":\"あ\",\"min\":0,\"max\":9}");
            return sb.Append(']').ToString();
        }
    }

    // ── Wave 4 (docs/ekn-wave4-contract.md) ────────────────────────────────────────────────────

    // §1/§6: on_near/on_far の radius (必須)/who (on_near 任意・on_far 必須で anyone 不可)、
    // 他イベントへの付着 reject (slot と同じ対称検査)。§3.3: on_linked_death の cause 受理と、
    // cause が on_death / on_linked_death 以外では従来どおり reject されること。
    [Theory]
    // on_near: radius 必須・who 任意 (既定 anyone)
    [InlineData("{\"when\":\"on_near\",\"radius\":\"small\",\"do\":[{\"op\":\"stop\"}]}", true)]
    [InlineData("{\"when\":\"on_near\",\"radius\":\"medium\",\"who\":\"linked\",\"do\":[{\"op\":\"stop\"}]}", true)]
    [InlineData("{\"when\":\"on_near\",\"radius\":\"large\",\"who\":\"anyone\",\"do\":[{\"op\":\"stop\"}]}", true)]
    [InlineData("{\"when\":\"on_near\",\"radius\":\"large\",\"who\":\"saved2\",\"do\":[{\"op\":\"stop\"}]}", true)]
    [InlineData("{\"when\":\"on_near\",\"do\":[{\"op\":\"stop\"}]}", false)] // radius 欠落
    [InlineData("{\"when\":\"on_near\",\"radius\":\"huge\",\"do\":[{\"op\":\"stop\"}]}", false)] // radius 不正値
    [InlineData("{\"when\":\"on_near\",\"radius\":\"small\",\"who\":\"self\",\"do\":[{\"op\":\"stop\"}]}", false)] // who 不正値
    // on_far: radius/who とも必須・anyone は文書 reject
    [InlineData("{\"when\":\"on_far\",\"radius\":\"medium\",\"who\":\"linked\",\"do\":[{\"op\":\"stop\"}]}", true)]
    [InlineData("{\"when\":\"on_far\",\"radius\":\"small\",\"who\":\"saved1\",\"do\":[{\"op\":\"stop\"}]}", true)]
    [InlineData("{\"when\":\"on_far\",\"radius\":\"medium\",\"do\":[{\"op\":\"stop\"}]}", false)] // who 欠落
    [InlineData("{\"when\":\"on_far\",\"who\":\"linked\",\"do\":[{\"op\":\"stop\"}]}", false)] // radius 欠落
    [InlineData("{\"when\":\"on_far\",\"radius\":\"medium\",\"who\":\"anyone\",\"do\":[{\"op\":\"stop\"}]}", false)] // anyone reject
    // radius/who の他イベント付着は文書 reject (対称検査)
    [InlineData("{\"when\":\"on_pet\",\"radius\":\"small\",\"do\":[{\"op\":\"stop\"}]}", false)]
    [InlineData("{\"when\":\"on_pet\",\"who\":\"linked\",\"do\":[{\"op\":\"stop\"}]}", false)]
    [InlineData("{\"when\":\"on_cno_touch\",\"slot\":1,\"radius\":\"small\",\"do\":[{\"op\":\"stop\"}]}", false)]
    // on_linked_death: cause は任意受理・kind の語彙や他フィールドは reject
    [InlineData("{\"when\":\"on_linked_death\",\"do\":[{\"op\":\"stop\"}]}", true)]
    [InlineData("{\"when\":\"on_linked_death\",\"cause\":\"kill\",\"do\":[{\"op\":\"stop\"}]}", true)]
    [InlineData("{\"when\":\"on_linked_death\",\"cause\":\"vote\",\"do\":[{\"op\":\"stop\"}]}", true)]
    [InlineData("{\"when\":\"on_linked_death\",\"cause\":\"force\",\"do\":[{\"op\":\"stop\"}]}", false)] // kind の語彙は cause では使えない
    [InlineData("{\"when\":\"on_linked_death\",\"radius\":\"small\",\"do\":[{\"op\":\"stop\"}]}", false)]
    // cause は on_death / on_linked_death 以外では従来どおり reject
    [InlineData("{\"when\":\"on_near\",\"radius\":\"small\",\"cause\":\"kill\",\"do\":[{\"op\":\"stop\"}]}", false)]
    // 部屋イベントは追加フィールド無し
    [InlineData("{\"when\":\"on_room_enter\",\"do\":[{\"op\":\"stop\"}]}", true)]
    [InlineData("{\"when\":\"on_room_exit\",\"do\":[{\"op\":\"stop\"}]}", true)]
    [InlineData("{\"when\":\"on_room_enter\",\"radius\":\"small\",\"do\":[{\"op\":\"stop\"}]}", false)]
    public void Wave4RuleFields_MatchTheContract(string ruleJson, bool shouldAccept)
    {
        string json = Wrap("\"logic\":{\"version\":1,\"rules\":[" + ruleJson + "]}");
        bool ok = EkrDefinition.TryParse(json, out _, out string error);
        Assert.True(ok == shouldAccept, shouldAccept ? error : "本来 reject されるべき rule が受理されました: " + ruleJson);
    }

    // §1.2: who 省略は "anyone" としてパース時に焼き込まれる (notify.target の既定 self と同じ方式)。
    [Fact]
    public void OnNear_WhoOmitted_DefaultsToAnyone()
    {
        string json = Wrap("\"logic\":{\"version\":1,\"rules\":[" +
                           "{\"when\":\"on_near\",\"radius\":\"small\",\"do\":[{\"op\":\"stop\"}]}," +
                           "{\"when\":\"on_far\",\"radius\":\"large\",\"who\":\"saved1\",\"do\":[{\"op\":\"stop\"}]}]}");

        Assert.True(EkrDefinition.TryParse(json, out EkrDefinition def, out string error), error);

        EkrRule near = Assert.Single(def.ParsedLogic.Rules, r => r.When == "on_near");
        Assert.Equal("small", near.Radius);
        Assert.Equal("anyone", near.Who);

        EkrRule far = Assert.Single(def.ParsedLogic.Rules, r => r.When == "on_far");
        Assert.Equal("large", far.Radius);
        Assert.Equal("saved1", far.Who);
    }

    // §3/§4: link (self/linked 不可)・unlink (引数なし)・recruit (self 不可・linked 可) の受理集合。
    [Theory]
    [InlineData("{\"op\":\"link\",\"target\":\"ctx\"}", true)]
    [InlineData("{\"op\":\"link\",\"target\":\"saved1\"}", true)]
    [InlineData("{\"op\":\"link\",\"target\":\"nearest\"}", true)]
    [InlineData("{\"op\":\"link\",\"target\":\"random\"}", true)]
    [InlineData("{\"op\":\"link\",\"target\":\"self\"}", false)]
    [InlineData("{\"op\":\"link\",\"target\":\"linked\"}", false)] // 「つないだ人と つなぐ」は恒等 — reject
    [InlineData("{\"op\":\"link\",\"target\":\"all\"}", false)]
    [InlineData("{\"op\":\"link\"}", false)] // target 必須
    [InlineData("{\"op\":\"unlink\"}", true)]
    [InlineData("{\"op\":\"recruit\",\"target\":\"ctx\"}", true)]
    [InlineData("{\"op\":\"recruit\",\"target\":\"linked\"}", true)]
    [InlineData("{\"op\":\"recruit\",\"target\":\"saved2\"}", true)]
    [InlineData("{\"op\":\"recruit\",\"target\":\"nearest\"}", true)]
    [InlineData("{\"op\":\"recruit\",\"target\":\"self\"}", false)]
    [InlineData("{\"op\":\"recruit\",\"target\":\"all\"}", false)]
    [InlineData("{\"op\":\"recruit\"}", false)] // target 必須
    [InlineData("{\"op\":\"addon_give\",\"target\":\"ctx\"}", false)] // 未知 op は従来どおり reject (Wave 5 送り §0)
    public void Wave4Ops_MatchTheContract(string opJson, bool shouldAccept)
    {
        bool ok = EkrDefinition.TryParse(LogicWithOp(opJson), out _, out string error);
        Assert.True(ok == shouldAccept, shouldAccept ? error : "本来 reject されるべき op が受理されました: " + opJson);
    }

    // §3.4: linked セレクタは「saved1/2 が受理されるすべての箇所」(Single/Other/Multi の3受理面) に追加。
    // notify は TS 側が TARGET_SINGLE_VALUES からの導出で構造的に受理するため C# も受理 (検証パリティ)。
    // at/to (空間セレクタ) には追加しない (§3.4 明示除外)。
    [Theory]
    [InlineData("{\"op\":\"kill\",\"target\":\"linked\"}", true)]
    [InlineData("{\"op\":\"teleport_other\",\"target\":\"linked\",\"to\":\"marker1\"}", true)]
    [InlineData("{\"op\":\"inspect\",\"target\":\"linked\",\"depth\":\"team\"}", true)]
    [InlineData("{\"op\":\"reveal\",\"target\":\"linked\"}", true)]
    [InlineData("{\"op\":\"arrow_show\",\"target\":\"linked\",\"seconds\":10}", true)]
    [InlineData("{\"op\":\"remember\",\"slot\":1,\"target\":\"linked\"}", true)]
    [InlineData("{\"op\":\"vote_block\",\"target\":\"linked\"}", true)]
    [InlineData("{\"op\":\"exile\",\"target\":\"linked\"}", true)]
    [InlineData("{\"op\":\"teleport\",\"to\":\"linked\"}", false)] // 空間セレクタには追加しない (§3.4)
    [InlineData("{\"op\":\"marker_save\",\"slot\":1,\"at\":\"linked\"}", false)]
    [InlineData("{\"op\":\"notify\",\"text\":\"a\",\"seconds\":2,\"target\":\"linked\"}", true)] // MultiSelectors にも追加 (TS 導出構造とのパリティ)
    public void LinkedSelector_MatchesTheContract(string opJson, bool shouldAccept)
    {
        bool ok = EkrDefinition.TryParse(LogicWithOp(opJson), out _, out string error);
        Assert.True(ok == shouldAccept, shouldAccept ? error : "本来 reject されるべき op が受理されました: " + opJson);
    }

    // §6 (確認のみの項): cancel_attack/cancel_vote の配置 reject は Wave 4 の新イベント配下にも自動で効く。
    [Fact]
    public void CancelOps_StayRejectedUnderTheWave4Events()
    {
        Assert.False(EkrDefinition.TryParse(
            Wrap("\"logic\":{\"version\":1,\"rules\":[{\"when\":\"on_near\",\"radius\":\"small\",\"do\":[{\"op\":\"cancel_attack\"}]}]}"),
            out _, out _));

        Assert.False(EkrDefinition.TryParse(
            Wrap("\"logic\":{\"version\":1,\"rules\":[{\"when\":\"on_linked_death\",\"do\":[{\"op\":\"cancel_vote\"}]}]}"),
            out _, out _));
    }

    // ── Wave 6 (docs/ekn-wave6-contract.md §1〜§4): とばすもの + 残イベント2種 ──────────────────

    // 契約 §9-3 のテンプレギャラリー見本 (ゆきだま / こおりのたま / ビームふう) を含む TS 側 fixture が
    // 全数 C# パーサを通ること。見本は「作者がそのまま真似する組み方」なので、片側だけ通る形が混じると
    // 「エディタでは作れるのにゲームで開けない」を出荷することになる。
    [Theory]
    [InlineData("role-yukidama-showcase.ekrole.json")]
    [InlineData("role-koorinotama-showcase.ekrole.json")]
    [InlineData("role-beam-showcase.ekrole.json")]
    public void TemplateGalleryFixtures_AreAcceptedByCSharpValidator(string fileName)
    {
        string json = File.ReadAllText(FixturePath(fileName));
        Assert.True(EkrDefinition.TryParse(json, out EkrDefinition def, out string error), error);

        var launches = new List<EkrNode>();

        foreach (EkrRule rule in def.ParsedLogic.Rules)
            CollectOps(rule.Do, "cno_launch", launches);

        Assert.NotEmpty(launches);
        Assert.Contains(def.ParsedLogic.Rules, r => r.When == "on_cno_touch"); // 命中は既存イベントへ合流
    }

    // 共有 fixture から Wave 6 語彙が AST まで通ること。TS 側 (role-fixtures.test.ts の 24 イベント
    // 網羅アサーション) と同じファイルの同じ値を読むので、片側だけ実装が抜けるとどちらかが落ちる。
    [Fact]
    public void FullCourseFixture_ExposesWave6Vocabulary()
    {
        string json = File.ReadAllText(FixturePath("role-full-course.ekrole.json"));
        Assert.True(EkrDefinition.TryParse(json, out EkrDefinition def, out string error), error);

        // 新イベント2種が rule として通っている (フィルタ無しで受理される)
        Assert.Single(def.ParsedLogic.Rules, r => r.When == "on_sabotage");
        Assert.Single(def.ParsedLogic.Rules, r => r.When == "on_revive");

        var launches = new List<EkrNode>();

        foreach (EkrRule rule in def.ParsedLogic.Rules)
            CollectOps(rule.Do, "cno_launch", launches);

        EkrNode launch = Assert.Single(launches);
        Assert.Equal(2, launch.Slot);
        Assert.Equal("move", launch.LaunchDir);
        Assert.Equal("medium", launch.LaunchSpeed); // 省略 = 既定を焼き込む (正準形と対応)

        // 契約 §1.1: cno_launch は on_death 起点 fiber の白名単に載る (「死に際に弾をはなつ」) —
        // fixture はその形 (on_death 配下の cno_spawn → cno_launch) で持っている。
        EkrRule deathRule = Assert.Single(def.ParsedLogic.Rules, r => r.When == "on_death" && r.Do.Exists(n => n.Op == "cno_launch"));
        Assert.Contains(deathRule.Do, n => n.Op == "cno_spawn" && n.Slot == 2);
    }

    // §1/§4: cno_launch の必須フィールド (slot / dir) と任意フィールド (speed) の受理表。
    // TS 側 (roledef.test.ts) と同じ表を持つので、片側だけ実装が抜けるとどちらかが落ちる。
    [Theory]
    [InlineData("{\"op\":\"cno_launch\",\"slot\":1,\"dir\":\"move\"}", true)]
    [InlineData("{\"op\":\"cno_launch\",\"slot\":3,\"dir\":\"ctx\",\"speed\":\"fast\"}", true)]
    [InlineData("{\"op\":\"cno_launch\",\"slot\":2,\"dir\":\"marker1\",\"speed\":\"slow\"}", true)]
    [InlineData("{\"op\":\"cno_launch\",\"slot\":1,\"dir\":\"marker4\",\"speed\":\"medium\"}", true)]
    // slot / dir とも必須
    [InlineData("{\"op\":\"cno_launch\",\"dir\":\"move\"}", false)]
    [InlineData("{\"op\":\"cno_launch\",\"slot\":1}", false)]
    // slot は 1..3 (CNO スロット語彙)
    [InlineData("{\"op\":\"cno_launch\",\"slot\":0,\"dir\":\"move\"}", false)]
    [InlineData("{\"op\":\"cno_launch\",\"slot\":4,\"dir\":\"move\"}", false)]
    // 未知の dir / speed は文書 reject (marker5 は語彙外・"self" は向きではない)
    [InlineData("{\"op\":\"cno_launch\",\"slot\":1,\"dir\":\"self\"}", false)]
    [InlineData("{\"op\":\"cno_launch\",\"slot\":1,\"dir\":\"marker5\"}", false)]
    [InlineData("{\"op\":\"cno_launch\",\"slot\":1,\"dir\":\"move\",\"speed\":\"veryfast\"}", false)]
    [InlineData("{\"op\":\"cno_launch\",\"slot\":1,\"dir\":\"move\",\"speed\":4}", false)]
    public void CnoLaunch_MatchesTheContract(string nodeJson, bool shouldAccept)
    {
        string json = Wrap("\"logic\":{\"version\":1,\"rules\":[{\"when\":\"on_pet\",\"do\":[" + nodeJson + "]}]}");
        bool ok = EkrDefinition.TryParse(json, out _, out string error);
        Assert.True(ok == shouldAccept, shouldAccept ? error : "本来 reject されるべき node が受理されました: " + nodeJson);
    }

    // §1: speed 省略時はパース時に "medium" を焼き込む (実行側で既定値を再解釈しない — 正準形が
    // 既定値を書き出さない側と対応する)。
    [Fact]
    public void CnoLaunchSpeed_DefaultsToMediumWhenOmitted()
    {
        string json = Wrap("\"logic\":{\"version\":1,\"rules\":[{\"when\":\"on_pet\",\"do\":[{\"op\":\"cno_launch\",\"slot\":1,\"dir\":\"move\"}]}]}");
        Assert.True(EkrDefinition.TryParse(json, out EkrDefinition def, out string error), error);

        EkrNode launch = def.ParsedLogic.Rules[0].Do[0];
        Assert.Equal("move", launch.LaunchDir);
        Assert.Equal("medium", launch.LaunchSpeed);
        Assert.Equal(1, launch.Slot);
    }

    // §2/§3: 新イベント2種は rule フィルタを持たない (on_cno_touch の slot のような必須フィールドは無い)。
    // 他イベント専用フィルタの付着は既存の厳格側に自然合流する。
    [Theory]
    [InlineData("{\"when\":\"on_sabotage\",\"do\":[{\"op\":\"notify\",\"text\":\"a\",\"seconds\":3}]}", true)]
    [InlineData("{\"when\":\"on_revive\",\"do\":[{\"op\":\"notify\",\"text\":\"a\",\"seconds\":3}]}", true)]
    // slot / cause / kind / radius は付けられない (専用イベント以外は reject)
    [InlineData("{\"when\":\"on_sabotage\",\"slot\":1,\"do\":[{\"op\":\"stop\"}]}", false)]
    [InlineData("{\"when\":\"on_revive\",\"cause\":\"kill\",\"do\":[{\"op\":\"stop\"}]}", false)]
    [InlineData("{\"when\":\"on_sabotage\",\"kind\":\"kill\",\"do\":[{\"op\":\"stop\"}]}", false)]
    // 綴り違いは未知イベントとして文書 reject
    [InlineData("{\"when\":\"on_sabotages\",\"do\":[{\"op\":\"stop\"}]}", false)]
    [InlineData("{\"when\":\"on_revived\",\"do\":[{\"op\":\"stop\"}]}", false)]
    public void Wave6Events_MatchTheContract(string ruleJson, bool shouldAccept)
    {
        string json = Wrap("\"logic\":{\"version\":1,\"rules\":[" + ruleJson + "]}");
        bool ok = EkrDefinition.TryParse(json, out _, out string error);
        Assert.True(ok == shouldAccept, shouldAccept ? error : "本来 reject されるべき rule が受理されました: " + ruleJson);
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

    // ── Wave 7 (docs/ekn-wave7-contract.md §1,§2): 勝利条件 (win / win_join) ─────────────────────

    private static string WrapTeam(string team, string logicJson)
    {
        return "{\"ekr\":1,\"name\":\"t\",\"color\":\"#112233\",\"team\":\"" + team + "\"," + logicJson + "}";
    }

    // §1/§4: win の受理表。target は任意 (既定 self)・単数セレクタ全種を受理・複数形は reject。
    // neutral 限定は次の Theory が担う。TS 側 (roledef.test.ts の Wave 7 describe) と同じ表。
    [Theory]
    [InlineData("{\"op\":\"win\"}", true)]
    [InlineData("{\"op\":\"win\",\"target\":\"self\"}", true)]
    [InlineData("{\"op\":\"win\",\"target\":\"ctx\"}", true)]
    [InlineData("{\"op\":\"win\",\"target\":\"linked\"}", true)]
    [InlineData("{\"op\":\"win\",\"target\":\"saved1\"}", true)]
    [InlineData("{\"op\":\"win\",\"target\":\"saved2\"}", true)]
    [InlineData("{\"op\":\"win\",\"target\":\"nearest\"}", true)]
    [InlineData("{\"op\":\"win\",\"target\":\"random\"}", true)]
    [InlineData("{\"op\":\"win\",\"target\":\"all\"}", false)]
    [InlineData("{\"op\":\"win\",\"target\":\"room\"}", false)]
    [InlineData("{\"op\":\"win\",\"target\":\"everyone\"}", false)]
    public void Win_TargetMatchesTheContract(string nodeJson, bool shouldAccept)
    {
        string json = WrapTeam("neutral", "\"logic\":{\"version\":1,\"rules\":[{\"when\":\"on_pet\",\"do\":[" + nodeJson + "]}]}");
        bool ok = EkrDefinition.TryParse(json, out _, out string error);
        Assert.True(ok == shouldAccept, shouldAccept ? error : "本来 reject されるべき node が受理されました: " + nodeJson);
    }

    // §1: win は neutral 文書限定 (crewmate/impostor は文書 reject)。if の then/else に埋めても
    // ContainsWinOp の再帰探索が検出する。win_join は全陣営で受理される (§2)。
    [Theory]
    [InlineData("neutral", "{\"op\":\"win\"}", true)]
    [InlineData("crewmate", "{\"op\":\"win\"}", false)]
    [InlineData("impostor", "{\"op\":\"win\"}", false)]
    [InlineData("crewmate", "{\"op\":\"if\",\"cond\":{\"e\":\"lit\",\"v\":1},\"then\":[{\"op\":\"win\"}]}", false)]
    [InlineData("crewmate", "{\"op\":\"if\",\"cond\":{\"e\":\"lit\",\"v\":1},\"then\":[{\"op\":\"stop\"}],\"else\":[{\"op\":\"win\"}]}", false)]
    [InlineData("neutral", "{\"op\":\"if\",\"cond\":{\"e\":\"lit\",\"v\":1},\"then\":[{\"op\":\"win\"}]}", true)]
    [InlineData("neutral", "{\"op\":\"win_join\"}", true)]
    [InlineData("crewmate", "{\"op\":\"win_join\"}", true)]
    [InlineData("impostor", "{\"op\":\"win_join\"}", true)]
    public void Win_IsNeutralOnly_WinJoinIsNot(string team, string nodeJson, bool shouldAccept)
    {
        string json = WrapTeam(team, "\"logic\":{\"version\":1,\"rules\":[{\"when\":\"on_pet\",\"do\":[" + nodeJson + "]}]}");
        bool ok = EkrDefinition.TryParse(json, out _, out string error);
        Assert.True(ok == shouldAccept, shouldAccept ? error : "本来 reject されるべき文書が受理されました: team=" + team + " node=" + nodeJson);
    }

    // §1/§2: target 省略時はパース時に "self" を焼き込む (cno_launch.speed の medium と同じ —
    // 実行側で既定値を再解釈しない)。
    [Fact]
    public void WinTarget_DefaultsToSelfWhenOmitted()
    {
        string json = WrapTeam("neutral", "\"logic\":{\"version\":1,\"rules\":[{\"when\":\"on_pet\",\"do\":[{\"op\":\"win\"},{\"op\":\"win_join\"}]}]}");
        Assert.True(EkrDefinition.TryParse(json, out EkrDefinition def, out string error), error);

        Assert.Equal("self", def.ParsedLogic.Rules[0].Do[0].Target);
        Assert.Equal("self", def.ParsedLogic.Rules[0].Do[1].Target);
    }

    // 契約 §6 のテンプレギャラリー見本2本 (あつめや / コバンザメ) が C# パーサを通ること。
    // あつめやは win を含む唯一の neutral fixture = win の C# パース網羅を担う。
    [Theory]
    [InlineData("role-collector-showcase.ekrole.json", "win")]
    [InlineData("role-parasite-showcase.ekrole.json", "win_join")]
    public void Wave7TemplateGalleryFixtures_AreAcceptedByCSharpValidator(string fileName, string winOp)
    {
        string json = File.ReadAllText(FixturePath(fileName));
        Assert.True(EkrDefinition.TryParse(json, out EkrDefinition def, out string error), error);

        var wins = new List<EkrNode>();

        foreach (EkrRule rule in def.ParsedLogic.Rules)
            CollectOps(rule.Do, winOp, wins);

        Assert.NotEmpty(wins);
    }

    // 共有 fixture から Wave 7 語彙 (win_join) が AST まで通ること。TS 側 (role-fixtures.test.ts の
    // op 網羅アサーション) と同じファイルの同じ値を読む。win は crewmate 文書に置けないので
    // full-course には無い (上の Wave7TemplateGalleryFixtures が担う)。
    [Fact]
    public void FullCourseFixture_ExposesWave7Vocabulary()
    {
        string json = File.ReadAllText(FixturePath("role-full-course.ekrole.json"));
        Assert.True(EkrDefinition.TryParse(json, out EkrDefinition def, out string error), error);

        var joins = new List<EkrNode>();

        foreach (EkrRule rule in def.ParsedLogic.Rules)
            CollectOps(rule.Do, "win_join", joins);

        // on_death 配下に「明示 self」と「省略 → self 焼き込み」の2形を持つ (どちらも Target=self)
        Assert.Equal(2, joins.Count);
        Assert.All(joins, n => Assert.Equal("self", n.Target));
        Assert.Single(def.ParsedLogic.Rules, r => r.When == "on_death" && r.Do.Exists(n => n.Op == "win_join"));
    }
}
