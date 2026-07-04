<#
.SYNOPSIS
    Option-Id 衝突チェッカー (End K not)

.DESCRIPTION
    全 OptionItem は単一のグローバル辞書 (OptionItem.cs の FastOpts) を id で共有する。
    id が衝突すると 2 つめ以降は登録に失敗し、その役職/オプションはメニューから
    "無言で" 消える (ランタイムでは Logger.Error "Duplicate ID" が出るだけ)。
    このスクリプトはビルド前に同じ衝突を静的に検出し、さらに移植作業用に
    「次の空き id ブロック」を提案する。

    id 消費モデル (RoleBase.cs / OptionHolder.cs 実測):
      直接登録 (呼び出し側に Options.X(<id>) / new XxxOptionItem(<id>) が出る):
        - new XxxOptionItem(<id>, ...)                → 1
        - Options.Setup(Single)RoleOptions(<id>, ...) → 2 (id, id+1=Maximum)
        - Options.SetupAdtRoleOptions(<id>, ...)      → 8 (teamSpawnOptions 時最大、保守的)
        - Options.CreatePetUseSetting/...CD/...Vote   → 1
        - Options.OverrideTasksData.Create(<id>, ...) → 4
      StartSetup 登録 (現代的な fluent API・呼び出し側は handler メソッド連鎖):
        - StartSetup(<id>) + .AutoSetupOption/.CreatePetUseSetting/.CreateVoteCancellingUseSetting
          → 連続ブロック [id, id + 1 + 連鎖数 + override数*3]
          (SetupRoleOptions で id,id+1、以降 handler が ++_id で連番。
           .CreateOverrideTasksData は 1 進めて内部で 4 消費 → +3 余分)

    <id> は各ファイルの `const int NAME = <literal>` を使って
    NAME / NAME + n / NAME - n / 数値リテラル を解決する。
    解決できない式 (変数・メソッド呼び出し等) はスキップし件数だけ報告する。

    既知の割り切り:
      - 役職ブロックは慣習的に 100 間隔で確保されるため、範囲を数個多めに
        押さえても隣の役職と衝突しない (over-reserve は安全側)。
      - `new XxxOptionItem(Id, ...)` の "素の識別子" は役職内ローカルヘルパーの
        引数であることが多く、静的に const と区別できないためスキップする。
        基底 id は必ず SetupRoleOptions / StartSetup 側で捕捉されるので漏れない。
      - 1 ファイル 1 StartSetup を前提 (全 116 ファイルで成立)。

.PARAMETER SuggestFrom
    次の空きブロックを探す開始 id (既定 700000 — 移植役職の予約帯)。
.PARAMETER SuggestTo
    次の空きブロックを探す終端 id (既定 705000)。
.PARAMETER BlockSize
    空きブロック探索の 1 役職あたり確保幅 (既定 100)。
.PARAMETER SuggestCount
    提案する空きブロック数 (既定 5)。

.EXAMPLE
    pwsh tools/check-option-ids.ps1
    pwsh tools/check-option-ids.ps1 -SuggestFrom 9000 -SuggestTo 16000
#>
[CmdletBinding()]
param(
    [int]$SuggestFrom  = 700000,
    [int]$SuggestTo    = 705000,
    [int]$BlockSize    = 100,
    [int]$SuggestCount = 5
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$scanDirs = @('Roles', 'Modules', 'Gamemodes') | ForEach-Object { Join-Path $repoRoot $_ }

# id -> list of occupant descriptors ([pscustomobject]{File; Label})
$occupied  = @{}
$unresolved = 0
$fileCount  = 0

function Resolve-Id {
    param([string]$expr, [hashtable]$symbols, [bool]$allowBare)
    $expr = $expr.Trim()
    if ($expr -match '^\d+$') { return [int]$expr }
    if ($expr -match '^(\w+)\s*([+\-])\s*(\d+)$') {
        $name = $Matches[1]; $op = $Matches[2]; $n = [int]$Matches[3]
        if ($symbols.ContainsKey($name)) {
            return $(if ($op -eq '+') { $symbols[$name] + $n } else { $symbols[$name] - $n })
        }
        return $null
    }
    if ($allowBare -and $expr -match '^(\w+)$' -and $symbols.ContainsKey($expr)) { return $symbols[$expr] }
    return $null
}

function Add-Range {
    param([int]$start, [int]$span, [string]$file, [string]$label)
    for ($k = $start; $k -lt ($start + $span); $k++) {
        if (-not $occupied.ContainsKey($k)) { $occupied[$k] = @() }
        $occupied[$k] += [pscustomobject]@{ File = $file; Label = $label }
    }
}

# 直接登録パターン: group 1 = 先頭 (id) 式 / Span = 消費 id 数 / AllowBare = 素識別子を許すか
# SetupAdtRoleOptions は teamSpawnOptions で 4/8 が変わるため別扱い (下)。
$directPatterns = @(
    @{ Rx = [regex]'Options\.SetupSingleRoleOptions\s*\(\s*([^,]+?)\s*,';  Span = 2; AllowBare = $true  },
    @{ Rx = [regex]'Options\.SetupRoleOptions\s*\(\s*([^,]+?)\s*,';        Span = 2; AllowBare = $true  },
    @{ Rx = [regex]'Options\.OverrideTasksData\.Create\s*\(\s*([^,]+?)\s*,'; Span = 4; AllowBare = $true },
    @{ Rx = [regex]'Options\.Create\w+\s*\(\s*([^,)]+?)\s*[,)]';           Span = 1; AllowBare = $true  },
    @{ Rx = [regex]'new\s+\w*OptionItem\s*\(\s*([^,]+?)\s*,';              Span = 1; AllowBare = $false }
)
# SetupAdtRoleOptions(<id>, ... [, teamSpawnOptions: true]) : 4 (通常) / 8 (team時)
$adtRx = [regex]'Options\.SetupAdtRoleOptions\s*\(\s*([^,]+?)\s*,([^)]*)\)'

$startSetupRx = [regex]'StartSetup\s*\(\s*([^,)]+?)\s*[,)]'
$chainRx      = [regex]'\.(AutoSetupOption|CreatePetUseSetting|CreateVoteCancellingUseSetting)\s*\('
$overrideRx   = [regex]'\.CreateOverrideTasksData\s*\('

foreach ($dir in $scanDirs) {
    if (-not (Test-Path $dir)) { continue }
    Get-ChildItem -Path $dir -Recurse -Filter *.cs | ForEach-Object {
        $file = $_
        $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
        $fileCount++
        $rel = $file.FullName.Substring($repoRoot.Length).TrimStart('\', '/')

        # per-file symbol table: const int NAME = <literal>;
        $symbols = @{}
        foreach ($m in [regex]::Matches($text, 'const\s+int\s+(\w+)\s*=\s*(\d+)\s*;')) {
            $symbols[$m.Groups[1].Value] = [int]$m.Groups[2].Value
        }

        # --- StartSetup (連続ブロック) ---
        $ssMatches = $startSetupRx.Matches($text)
        if ($ssMatches.Count -gt 0) {
            $chainCount = $chainRx.Matches($text).Count + $overrideRx.Matches($text).Count
            $overrideExtra = $overrideRx.Matches($text).Count * 3
            $lastOffset = 1 + $chainCount + $overrideExtra   # 最終 id オフセット (base 起点)
            foreach ($m in $ssMatches) {
                $base = Resolve-Id -expr $m.Groups[1].Value -symbols $symbols -allowBare $true
                if ($null -eq $base) { $script:unresolved++; continue }
                Add-Range -start $base -span ($lastOffset + 1) -file $rel -label "StartSetup($($m.Groups[1].Value.Trim())) block"
            }
        }

        # --- SetupAdtRoleOptions (4 / 8) ---
        foreach ($m in $adtRx.Matches($text)) {
            $id = Resolve-Id -expr $m.Groups[1].Value -symbols $symbols -allowBare $true
            if ($null -eq $id) { $script:unresolved++; continue }
            $span = if ($m.Groups[2].Value -match 'teamSpawnOptions\s*:\s*true') { 8 } else { 4 }
            Add-Range -start $id -span $span -file $rel -label $m.Groups[1].Value.Trim()
        }

        # --- 直接登録 ---
        foreach ($p in $directPatterns) {
            foreach ($m in $p.Rx.Matches($text)) {
                $exprText = $m.Groups[1].Value
                $id = Resolve-Id -expr $exprText -symbols $symbols -allowBare $p.AllowBare
                if ($null -eq $id) { $script:unresolved++; continue }
                Add-Range -start $id -span $p.Span -file $rel -label $exprText.Trim()
            }
        }
    }
}

# --- collisions: 同一 id に 2 件以上の占有 = 衝突 (同一ファイル内の二重予約も実バグ) ---
# StartSetup ファイルは直接パターンに一切マッチせず範囲マークは 1 回のみのため、
# 同一ファイル内の重複マークは発生しない → 占有 2 件以上は常に真の衝突。
$collisions = $occupied.GetEnumerator() |
    Where-Object { $_.Value.Count -gt 1 } |
    Sort-Object { [int]$_.Name }

Write-Host ""
Write-Host "=== Option-Id 衝突チェック ===" -ForegroundColor Cyan
Write-Host ("scanned {0} files, {1} distinct ids occupied, {2} unresolved exprs skipped" -f $fileCount, $occupied.Count, $unresolved)
Write-Host ""

$hadCollision = $false
foreach ($c in $collisions) {
    $hadCollision = $true
    $srcs = $c.Value | Sort-Object File, Label -Unique
    Write-Host ("❌ id {0} が {1} 件で衝突:" -f $c.Name, $c.Value.Count) -ForegroundColor Red
    foreach ($occ in $srcs) {
        Write-Host ("      {0}  ({1})" -f $occ.File, $occ.Label)
    }
}
if (-not $hadCollision) {
    Write-Host ("✅ id 衝突なし (解決済み id のみ; {0} 件は静的解決不可でスキップ — module の id++ ループや計算 id は未カバー)" -f $unresolved) -ForegroundColor Green
}

# --- next free blocks ---
Write-Host ""
Write-Host ("=== 空きブロック提案  ({0}..{1}, 幅 {2}) ===" -f $SuggestFrom, $SuggestTo, $BlockSize) -ForegroundColor Cyan
$found = 0
for ($start = $SuggestFrom; $start -lt $SuggestTo -and $found -lt $SuggestCount; $start += $BlockSize) {
    $end = $start + $BlockSize - 1
    $clash = $false
    foreach ($k in $occupied.Keys) { if ($k -ge $start -and $k -le $end) { $clash = $true; break } }
    if (-not $clash) {
        Write-Host ("   空き: {0} .. {1}" -f $start, $end) -ForegroundColor Green
        $found++
    }
}
if ($found -eq 0) { Write-Host "   (指定範囲に空きブロックなし — 範囲を広げてください)" -ForegroundColor Yellow }

Write-Host ""
if ($hadCollision) {
    Write-Host "衝突が見つかりました。上記 id を別ブロックに移してください。" -ForegroundColor Red
    exit 1
}
exit 0
