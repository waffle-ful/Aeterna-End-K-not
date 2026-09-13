# gen-ekr-roles.ps1 — 役職メーカー (EKR) 用の「見える役職名」一覧を editor/src/generated/ekr-roles.ts と
# Modules/Ekm/EkrRoleCatalog.cs へ焼く (tools/gen-ekr-addons.ps1 の役職版)。
# 入力: Roles/Standard/{Crewmate,Impostor,Neutral}/**/*.cs (RoleBase 実装) / CustomRoles.cs (enum 名の正典) /
#       Resources/Lang/{ja_JP,en_US}.jsonc の "<Name>"。陣営は最上位ディレクトリで決める。
# 除外: Addons / Ekm / Ghost / Coven (役職メーカーの陣営値に coven が無いため) / ゲームモード専用 (Gamemodes/ 配下)。
# 出力: BOM 無し UTF-8 / LF。役職が増減したらこのスクリプトを回して両方の生成物をコミットする。
# C# 側の集合 (EkrRoleCatalog.All) との一致は tests/EndKnot.Tests の RoleCatalog_MatchesGeneratedTs が検査する。
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$roleRoot = Join-Path $root 'Roles/Standard'
$outPath = Join-Path $root 'editor/src/generated/ekr-roles.ts'
$csOutPath = Join-Path $root 'Modules/Ekm/EkrRoleCatalog.cs'

function Read-Lang($path) {
    $raw = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
    $map = @{}
    foreach ($m in [regex]::Matches($raw, '^\s*"([A-Za-z0-9_]+)":\s*"((?:[^"\\]|\\.)*)"', 'Multiline')) {
        if (-not $map.ContainsKey($m.Groups[1].Value)) { $map[$m.Groups[1].Value] = $m.Groups[2].Value }
    }
    return $map
}
$ja = Read-Lang (Join-Path $root 'Resources/Lang/ja_JP.jsonc')
$en = Read-Lang (Join-Path $root 'Resources/Lang/en_US.jsonc')

# enum 名の正典 (クラス名と綴りが違う役職があるため、id は enum メンバの綴りで焼く)。
$enumSrc = [IO.File]::ReadAllText((Join-Path $root 'CustomRoles.cs'), [Text.Encoding]::UTF8)
$enumCanonical = @{}
foreach ($m in [regex]::Matches($enumSrc, '(?m)^\s*([A-Za-z_][A-Za-z0-9_]*)\s*(?:=.*)?,?\s*(?://.*)?$')) {
    $enumCanonical[$m.Groups[1].Value.ToLowerInvariant()] = $m.Groups[1].Value
}

$teams = [ordered]@{ Crewmate = 'crewmate'; Impostor = 'impostor'; Neutral = 'neutral' }
$entries = @()
$skipped = @()
foreach ($dirName in $teams.Keys) {
    $dir = Join-Path $roleRoot $dirName
    Get-ChildItem -Path $dir -Recurse -Filter *.cs | ForEach-Object {
        $src = [IO.File]::ReadAllText($_.FullName, [Text.Encoding]::UTF8)
        foreach ($cm in [regex]::Matches($src, '(?m)^\s*(?:public|internal)?\s*(?:sealed\s+|abstract\s+)?class\s+(\w+)\s*:\s*RoleBase\b')) {
            $className = $cm.Groups[1].Value
            $key = $className.ToLowerInvariant()
            if (-not $enumCanonical.ContainsKey($key)) { $skipped += "$className ($($_.Name))"; continue }
            $name = $enumCanonical[$key]
            $entries += [pscustomobject]@{
                id = $name; team = $teams[$dirName]
                ja = if ($ja.ContainsKey($name)) { $ja[$name] } else { $name }
                en = if ($en.ContainsKey($name)) { $en[$name] } else { $name }
            }
        }
    }
}
# バニラ素の役職はクラスを持たないので固定で足す (ゴースト役職 GuardianAngel は除外)。
# 「*EndKnot」のリメイク基底はモッド内部の土台なので一覧に出さない。
$vanilla = @(
    @{ id = 'Crewmate'; team = 'crewmate' }, @{ id = 'Engineer'; team = 'crewmate' }, @{ id = 'Noisemaker'; team = 'crewmate' },
    @{ id = 'Scientist'; team = 'crewmate' }, @{ id = 'Tracker'; team = 'crewmate' }, @{ id = 'Detective'; team = 'crewmate' },
    @{ id = 'Impostor'; team = 'impostor' }, @{ id = 'Phantom'; team = 'impostor' }, @{ id = 'Shapeshifter'; team = 'impostor' }, @{ id = 'Viper'; team = 'impostor' }
)
foreach ($v in $vanilla) {
    if (-not $enumCanonical.ContainsKey($v.id.ToLowerInvariant())) { continue }
    $entries += [pscustomobject]@{
        id = $v.id; team = $v.team
        ja = if ($ja.ContainsKey($v.id)) { $ja[$v.id] } else { $v.id }
        en = if ($en.ContainsKey($v.id)) { $en[$v.id] } else { $v.id }
    }
}
$entries = @($entries | Where-Object { -not $_.id.EndsWith('EndKnot') })

# 同名クラスが複数ファイルにある場合は最初の 1 件 (id の一意性を守る)。
$seen = @{}
$entries = @($entries | Where-Object { if ($seen.ContainsKey($_.id)) { $false } else { $seen[$_.id] = $true; $true } } | Sort-Object team, id)
if ($skipped.Count -gt 0) { Write-Host ("gen-ekr-roles: skipped (enum に無いクラス) = " + ($skipped -join ', ')) }

function Esc($s) { return ($s -replace '\\', '\\\\' -replace '"', '\"') }

$sb = New-Object Text.StringBuilder
[void]$sb.Append("// 生成物 — 手で編集しない。tools/gen-ekr-roles.ps1 が焼く (入力 = Roles/Standard/{Crewmate,Impostor,Neutral} + 言語ファイル)。`n")
[void]$sb.Append("// 役職の追加/削除時は生成器を回してコミットする。C# 側との一致は EndKnot.Tests が検査する。`n`n")
[void]$sb.Append("export type RoleCatalogTeam = `"crewmate`" | `"impostor`" | `"neutral`";`n`n")
[void]$sb.Append("export interface RoleMeta {`n  id: string;`n  team: RoleCatalogTeam;`n  ja: string;`n  en: string;`n}`n`n")
[void]$sb.Append("export const ROLE_META: readonly RoleMeta[] = [`n")
foreach ($e in $entries) {
    [void]$sb.Append("  { id: `"$($e.id)`", team: `"$($e.team)`", ja: `"$(Esc $e.ja)`", en: `"$(Esc $e.en)`" },`n")
}
[void]$sb.Append("] as const;`n`n")
[void]$sb.Append("export const ROLE_VALUES = ROLE_META.map((r) => r.id) as readonly string[];`n")
[void]$sb.Append("export const ROLE_BY_ID: ReadonlyMap<string, RoleMeta> = new Map(ROLE_META.map((r) => [r.id, r]));`n")

$utf8NoBom = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($outPath, $sb.ToString(), $utf8NoBom)
Write-Host ("gen-ekr-roles: {0} roles -> {1}" -f $entries.Count, $outPath)

# C# 側のカタログ。tests/EndKnot.Tests がソース共有方式で直接コンパイルするため Unity/BepInEx 依存ゼロにする
# (陣営は CustomRolesHelper でなくここに焼いた値が正 — 生成器がディレクトリから決める)。
$csSb = New-Object Text.StringBuilder
[void]$csSb.Append("// 生成物 — 手で編集しない。tools/gen-ekr-roles.ps1 が焼く (入力 = Roles/Standard/{Crewmate,Impostor,Neutral} + CustomRoles.cs)。`n")
[void]$csSb.Append("// 役職の追加/削除時は生成器を回してコミットする。TS 側との一致は tests/EndKnot.Tests が検査する。`n")
[void]$csSb.Append("// Unity/BepInEx への依存を持たない (テストプロジェクトがソース共有方式で直接コンパイルするため)。`n`n")
[void]$csSb.Append("using System.Collections.Generic;`n`n")
[void]$csSb.Append("namespace EndKnot.Modules.Ekm;`n`n")
[void]$csSb.Append("// 見える役職名に使える役職 = CustomRoles 名 (enum 名そのもの・大小区別) → 陣営 (crewmate / impostor / neutral)。`n")
[void]$csSb.Append("internal static class EkrRoleCatalog`n{`n")
[void]$csSb.Append("    public static readonly Dictionary<string, string> All = new()`n    {`n")
foreach ($e in $entries) {
    [void]$csSb.Append("        [`"$($e.id)`"] = `"$($e.team)`",`n")
}
[void]$csSb.Append("    };`n}`n")

[IO.File]::WriteAllText($csOutPath, $csSb.ToString(), $utf8NoBom)
Write-Host ("gen-ekr-roles: {0} roles -> {1}" -f $entries.Count, $csOutPath)
