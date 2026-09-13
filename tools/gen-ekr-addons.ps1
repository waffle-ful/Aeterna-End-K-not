# gen-ekr-addons.ps1 — 役職メーカー (EKR) 用のアドオン一覧を editor/src/generated/ekr-addons.ts と
# Modules/Ekm/EkrAddonCatalog.cs へ焼く。
# 入力: Roles/Standard/Addons/**/*.cs (IAddon 実装 + AddonTypes 群) / Resources/Lang/{ja_JP,en_US}.jsonc の "<Name>" と "<Name>Info"。
# 出力: BOM 無し UTF-8 / LF。アドオンが増減したらこのスクリプトを回して両方の生成物をコミットする。
# C# 側の集合 (EkrAddonCatalog.All) との一致は tests/EndKnot.Tests の AddonCatalog_MatchesGeneratedTs が検査する。
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$addonDir = Join-Path $root 'Roles/Standard/Addons'
$outPath = Join-Path $root 'editor/src/generated/ekr-addons.ts'
$csOutPath = Join-Path $root 'Modules/Ekm/EkrAddonCatalog.cs'

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

# クラス名の綴りと CustomRoles enum メンバの綴りが1件だけ食い違う (YouTuber クラス / Youtuber メンバ)。
# id は enum メンバの綴りで焼く必要がある (C# 側の検証が enum 名との完全一致・大小区別のため)。
$enumSrc = [IO.File]::ReadAllText((Join-Path $root 'CustomRoles.cs'), [Text.Encoding]::UTF8)
$enumCanonical = @{}
foreach ($m in [regex]::Matches($enumSrc, '(?m)^\s*([A-Za-z_][A-Za-z0-9_]*)\s*(?:=.*)?,?\s*(?://.*)?$')) {
    $enumCanonical[$m.Groups[1].Value.ToLowerInvariant()] = $m.Groups[1].Value
}

# 固定表 (契約 §3 / §8): エンジン側の途中付与禁止・基底変更・クライアント描画依存。
# clientOnly は非モッド客に効果が届く既存経路 (options sender のビジョン/速度・SetName の色タグ・
# ホスト側の報告/キル判定) を1件ずつソースで確認して確定した — Blind だけが対象
# (LocateArrow/TargetArrow というモッド側だけの矢印表示を止める効果のため)。
$midGameForbidden = @('Egoist','Workhorse','Cleansed','Busy','Lovers','Stressed','Lazy','Rascal','LastImpostor')
$basisChanging = @('Physicist','Finder','Noisy','Examiner','Nimble','Bloodlust','Venom')
$clientOnly = @('Blind')

$entries = @()
Get-ChildItem -Path $addonDir -Recurse -Filter *.cs | ForEach-Object {
    $src = [IO.File]::ReadAllText($_.FullName, [Text.Encoding]::UTF8)
    $cm = [regex]::Match($src, 'class\s+(\w+)\s*:\s*IAddon')
    if (-not $cm.Success) { return }
    $className = $cm.Groups[1].Value
    $name = if ($enumCanonical.ContainsKey($className.ToLowerInvariant())) { $enumCanonical[$className.ToLowerInvariant()] } else { $className }
    $tm = [regex]::Match($src, 'AddonTypes\s+Type\s*=>\s*AddonTypes\.(\w+)')
    $group = if ($tm.Success) { $tm.Groups[1].Value } else { 'Mixed' }
    $entries += [pscustomobject]@{
        id = $name; group = $group
        ja = if ($ja.ContainsKey($name)) { $ja[$name] } else { $name }
        en = if ($en.ContainsKey($name)) { $en[$name] } else { $name }
        info = if ($ja.ContainsKey("${name}Info")) { $ja["${name}Info"] } else { '' }
        infoEn = if ($en.ContainsKey("${name}Info")) { $en["${name}Info"] } else { '' }
    }
}
$entries = $entries | Sort-Object group, id

function Esc($s) { return ($s -replace '\\', '\\\\' -replace '"', '\"') }

$sb = New-Object Text.StringBuilder
[void]$sb.Append("// 生成物 — 手で編集しない。tools/gen-ekr-addons.ps1 が焼く (入力 = Roles/Standard/Addons + 言語ファイル)。`n")
[void]$sb.Append("// アドオンの追加/削除時は生成器を回してコミットする。C# 側との一致は EndKnot.Tests が検査する。`n`n")
[void]$sb.Append("export type AddonGroup = `"Helpful`" | `"Harmful`" | `"ImpOnly`" | `"Mixed`";`n`n")
[void]$sb.Append("export interface AddonMeta {`n  id: string;`n  group: AddonGroup;`n  ja: string;`n  en: string;`n  info: string;`n  infoEn: string;`n  midGameForbidden: boolean;`n  basisChanging: boolean;`n  clientOnly: boolean;`n}`n`n")
[void]$sb.Append("export const ADDON_META: readonly AddonMeta[] = [`n")
foreach ($e in $entries) {
    $mg = if ($midGameForbidden -contains $e.id) { 'true' } else { 'false' }
    $bc = if ($basisChanging -contains $e.id) { 'true' } else { 'false' }
    $co = if ($clientOnly -contains $e.id) { 'true' } else { 'false' }
    [void]$sb.Append("  { id: `"$($e.id)`", group: `"$($e.group)`", ja: `"$(Esc $e.ja)`", en: `"$(Esc $e.en)`", info: `"$(Esc $e.info)`", infoEn: `"$(Esc $e.infoEn)`", midGameForbidden: $mg, basisChanging: $bc, clientOnly: $co },`n")
}
[void]$sb.Append("] as const;`n`n")
[void]$sb.Append("export const ADDON_VALUES = ADDON_META.map((a) => a.id) as readonly string[];`n")
[void]$sb.Append("export const ADDON_BY_ID: ReadonlyMap<string, AddonMeta> = new Map(ADDON_META.map((a) => [a.id, a]));`n")

$utf8NoBom = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($outPath, $sb.ToString(), $utf8NoBom)
Write-Host ("gen-ekr-addons: {0} addons -> {1}" -f $entries.Count, $outPath)

# C# 側のカタログ (Modules/Ekm/EkrAddonCatalog.cs)。tests/EndKnot.Tests がソース共有方式で直接コンパイルする
# ため、Unity/BepInEx 依存ゼロにする (IAddon 実装 (Roles/Standard/Addons/**) は Options 静的クラス経由で
# Unity に依存するので、あちらへの反射をこのアセンブリ内に直接持たない — 生成した文字列一覧を単一の
# ソースにする)。
$csSb = New-Object Text.StringBuilder
[void]$csSb.Append("// 生成物 — 手で編集しない。tools/gen-ekr-addons.ps1 が焼く (入力 = Roles/Standard/Addons + CustomRoles.cs)。`n")
[void]$csSb.Append("// アドオンの追加/削除時は生成器を回してコミットする。TS 側との一致は tests/EndKnot.Tests が検査する。`n")
[void]$csSb.Append("// Unity/BepInEx への依存を持たない (テストプロジェクトがソース共有方式で直接コンパイルするため)。`n`n")
[void]$csSb.Append("using System.Collections.Generic;`n`n")
[void]$csSb.Append("namespace EndKnot.Modules.Ekm;`n`n")
[void]$csSb.Append("// つけられるアドオン = IAddon 実装 121 種の CustomRoles 名 (enum 名そのもの・大小区別)。`n")
[void]$csSb.Append("internal static class EkrAddonCatalog`n{`n")
[void]$csSb.Append("    public static readonly HashSet<string> All =`n    [`n")
foreach ($e in $entries) {
    [void]$csSb.Append("        `"$($e.id)`",`n")
}
[void]$csSb.Append("    ];`n}`n")

[IO.File]::WriteAllText($csOutPath, $csSb.ToString(), $utf8NoBom)
Write-Host ("gen-ekr-addons: {0} addons -> {1}" -f $entries.Count, $csOutPath)
