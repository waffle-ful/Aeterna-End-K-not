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

    // ── 個別の契約ケース ──────────────────────────────────────────────

    private static string Wrap(string passivesAndLogic)
    {
        return "{\"ekr\":1,\"name\":\"t\",\"color\":\"#112233\",\"team\":\"crewmate\"," + passivesAndLogic + "}";
    }

    private const string MinimalRule = "\"rules\":[{\"when\":\"on_pet\",\"do\":[{\"op\":\"stop\"}]}]";

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
