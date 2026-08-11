using System;
using System.IO;
using EndKnot.Modules.Ekm;
using Xunit;

namespace EndKnot.Tests;

// Resources/EkRoles/*.ekrole.json — 埋込出荷役職 (役職メーカー製・DLL 同梱) の定義検証。
// 実機では EkrManager.LoadEmbeddedRoles が起動時にパースし、失敗はログ warn 1行で無音スキップになる
// (役職がメニューから消えるだけで気付きにくい) ため、同梱定義は全数ここで先に落とす。
public class EkrEmbeddedRolesTests
{
    private static string EmbeddedRolesDir => Path.Combine(AppContext.BaseDirectory, "embedded-roles");

    [Fact]
    public void AllEmbeddedRoleDefinitions_AreAcceptedByValidator()
    {
        string[] files = Directory.GetFiles(EmbeddedRolesDir, "*.ekrole.json");
        Assert.NotEmpty(files); // 1件も無い = csproj のコピー item が壊れている (テスト自体の無音死を防ぐ)

        foreach (string path in files)
        {
            string json = File.ReadAllText(path);
            Assert.True(EkrDefinition.TryParse(json, out EkrDefinition def, out string error), $"{Path.GetFileName(path)}: {error}");
            Assert.False(string.IsNullOrWhiteSpace(def.Name));
        }
    }

    // ファイル名の stem = CustomRoles enum 名の契約 (LoadEmbeddedRoles の照合キー)。enum 本体は
    // Unity 依存でテストへ include できないため、ここでは「スロット枠の名前 (EkmCustomRoleN) を
    // 埋込に使っていない」ことだけを機械検証する (取り違えると起動時にユーザースロットが乗っ取られる)。
    [Fact]
    public void EmbeddedRoleFileNames_DoNotShadowUserSlots()
    {
        foreach (string path in Directory.GetFiles(EmbeddedRolesDir, "*.ekrole.json"))
        {
            string stem = Path.GetFileName(path).Replace(".ekrole.json", "");
            Assert.False(stem.StartsWith("EkmCustomRole", StringComparison.OrdinalIgnoreCase), $"{stem} はユーザースロット名と衝突します");
        }
    }
}
