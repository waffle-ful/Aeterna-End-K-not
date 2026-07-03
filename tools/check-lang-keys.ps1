<#
.SYNOPSIS
    Lang key sync checker (End K not) - deterministic, JSON-parser-free.

.DESCRIPTION
    Verifies that Resources/Lang/*.jsonc stay in sync with en_US.jsonc.
    en_US.jsonc is the canonical key/command-form set.

    Why not ConvertFrom-Json: PowerShell 5.1's ConvertFrom-Json chokes on
    escape sequences present in the real lang data, so this script uses a
    plain line scan instead. Assumptions (verified against the real files):
      - flat structure, one "Key": "value", per line
      - values never span lines
      - comments are // lines and /* */ blocks

    FAIL rules (exit 1):
      r1  a CommandForms.* key present in en_US is missing from the target lang.
          (Patches/ChatCommandPatch.cs matches the CURRENT language's value
          only, so a missing key silently kills the command.)
      r2  the target lang's CommandForms value (split on ',', trim, lowercase)
          does not contain every en_US form (= english alias dropped).
      r3  an en_US CommandForms form contains an uppercase letter, whitespace,
          or is empty (input is ToLower'ed, so it could never match).
      r4  duplicate keys inside one file (last write silently wins).
          Checked for every parsed file.

    WARN rules (exit 0, or exit 1 with -Strict):
      - keys present in en_US but missing from ja_JP (non-CommandForms)
      - keys present only in ja_JP (orphans)

.PARAMETER Strict
    Treat WARN findings as failures (exit 1).
.PARAMETER LangDir
    Lang directory. Default: <script dir>/../Resources/Lang
.PARAMETER AllLangs
    Extend r1/r2 (and r4) to every *.jsonc in the lang dir, not just ja_JP.

.EXAMPLE
    powershell -File tools/check-lang-keys.ps1
    powershell -File tools/check-lang-keys.ps1 -Strict -AllLangs
#>
[CmdletBinding()]
param(
    [switch]$Strict,
    [string]$LangDir,
    [switch]$AllLangs
)

$ErrorActionPreference = 'Stop'
$sw = [System.Diagnostics.Stopwatch]::StartNew()

# Emoji built from code points so this file stays pure ASCII
# (a stripped BOM can never corrupt it under Windows PowerShell 5.1).
$MARK_OK   = [char]::ConvertFromUtf32(0x2705)                       # check mark
$MARK_FAIL = [char]::ConvertFromUtf32(0x274C)                       # cross mark
$MARK_WARN = [char]::ConvertFromUtf32(0x26A0) + [char]0xFE0F        # warning sign

if (-not $LangDir) {
    $LangDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'Resources\Lang'
}
if (-not (Test-Path -LiteralPath $LangDir)) {
    Write-Host "$MARK_FAIL Lang directory not found: $LangDir"
    exit 1
}

$enPath = Join-Path $LangDir 'en_US.jsonc'
if (-not (Test-Path -LiteralPath $enPath)) {
    Write-Host "$MARK_FAIL en_US.jsonc not found in: $LangDir"
    exit 1
}

$keyRegex = [regex]::new('^\s*"((?:[^"\\]|\\.)+)"\s*:', [System.Text.RegularExpressions.RegexOptions]::Compiled)
$kvRegex  = [regex]::new('^\s*"((?:[^"\\]|\\.)+)"\s*:\s*"((?:[^"\\]|\\.)*)"', [System.Text.RegularExpressions.RegexOptions]::Compiled)

function Parse-LangFile {
    param([string]$Path)

    $lines = [System.IO.File]::ReadAllLines($Path)
    $keys      = New-Object 'System.Collections.Generic.Dictionary[string,int]'   # key -> first line number
    $dups      = New-Object 'System.Collections.Generic.List[string]'             # "key (line A, dup line B)"
    $cmdForms  = @{}                                                              # CommandForms key -> raw value (last wins, like the loader)
    $inBlock   = $false

    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]

        if ($inBlock) {
            $close = $line.IndexOf('*/')
            if ($close -lt 0) { continue }
            $line = $line.Substring($close + 2)
            $inBlock = $false
        }

        if ($line.TrimStart().StartsWith('//')) { continue }

        # strip inline /* ... */ (possibly several); open without close spills over
        while ($true) {
            $open = $line.IndexOf('/*')
            if ($open -lt 0) { break }
            $close = $line.IndexOf('*/', $open + 2)
            if ($close -lt 0) {
                $line = $line.Substring(0, $open)
                $inBlock = $true
                break
            }
            $line = $line.Substring(0, $open) + $line.Substring($close + 2)
        }

        $m = $keyRegex.Match($line)
        if (-not $m.Success) { continue }
        $key = $m.Groups[1].Value

        if ($keys.ContainsKey($key)) {
            $dups.Add(('{0} (line {1}, dup line {2})' -f $key, $keys[$key], ($i + 1)))
        }
        else {
            $keys[$key] = $i + 1
        }

        if ($key.StartsWith('CommandForms.', [System.StringComparison]::Ordinal)) {
            $vm = $kvRegex.Match($line)
            if ($vm.Success) { $cmdForms[$key] = $vm.Groups[2].Value }
        }
    }

    [pscustomobject]@{
        Name     = [System.IO.Path]::GetFileName($Path)
        Keys     = $keys
        Dups     = $dups
        CmdForms = $cmdForms
    }
}

function Get-FormSet {
    # value -> set of forms (split ',', trim, lowercase, drop empties)
    param([string]$Raw)
    $set = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($seg in $Raw.Split(',')) {
        $f = $seg.Trim().ToLowerInvariant()
        if ($f.Length -gt 0) { [void]$set.Add($f) }
    }
    , $set
}

function Write-KeyList {
    # indented key listing, capped at $Max entries
    param([System.Collections.IList]$Items, [int]$Max = 30)
    $shown = [Math]::Min($Items.Count, $Max)
    for ($i = 0; $i -lt $shown; $i++) {
        Write-Host ('     - {0}' -f $Items[$i])
    }
    if ($Items.Count -gt $Max) {
        Write-Host ('     ...and {0} more' -f ($Items.Count - $Max))
    }
}

# ---------------------------------------------------------------- parse
$en = Parse-LangFile -Path $enPath

$targets = @()
if ($AllLangs) {
    $targets = Get-ChildItem -LiteralPath $LangDir -Filter '*.jsonc' |
        Where-Object { $_.Name -ne 'en_US.jsonc' } |
        Sort-Object Name |
        ForEach-Object { Parse-LangFile -Path $_.FullName }
}
else {
    $jaPath = Join-Path $LangDir 'ja_JP.jsonc'
    if (-not (Test-Path -LiteralPath $jaPath)) {
        Write-Host "$MARK_FAIL ja_JP.jsonc not found in: $LangDir"
        exit 1
    }
    $targets = @(Parse-LangFile -Path $jaPath)
}
$ja = $targets | Where-Object { $_.Name -eq 'ja_JP.jsonc' } | Select-Object -First 1

$failCount = 0
$warnCount = 0

Write-Host '=== check-lang-keys ==='
Write-Host ('Lang dir : {0}' -f $LangDir)
Write-Host ('Canonical: en_US.jsonc ({0} keys)  Targets: {1}' -f $en.Keys.Count, (($targets | ForEach-Object { $_.Name }) -join ', '))
Write-Host ''

# ------------------------------------------------- r3: en_US form hygiene
$r3 = New-Object 'System.Collections.Generic.List[string]'
foreach ($key in ($en.CmdForms.Keys | Sort-Object)) {
    foreach ($seg in $en.CmdForms[$key].Split(',')) {
        if ($seg.Trim().Length -eq 0) {
            $r3.Add(('{0}: empty form segment in "{1}"' -f $key, $en.CmdForms[$key]))
        }
        elseif ($seg -cmatch '\p{Lu}' -or $seg -match '\s') {
            $r3.Add(('{0}: form ''{1}'' contains uppercase or whitespace (can never match ToLower''ed input)' -f $key, $seg))
        }
    }
}
if ($r3.Count -gt 0) {
    $failCount += $r3.Count
    Write-Host ("$MARK_FAIL r3: en_US CommandForms hygiene - {0} violation(s)" -f $r3.Count)
    Write-KeyList -Items $r3 -Max 100
}
else {
    Write-Host ("$MARK_OK r3: en_US CommandForms hygiene ({0} keys clean)" -f $en.CmdForms.Count)
}

# --------------------------------- r4: duplicate keys (every parsed file)
foreach ($file in (@($en) + $targets)) {
    if ($file.Dups.Count -gt 0) {
        $failCount += $file.Dups.Count
        Write-Host ("$MARK_FAIL r4: duplicate keys in {0} - {1} duplicate(s)" -f $file.Name, $file.Dups.Count)
        Write-KeyList -Items $file.Dups -Max 100
    }
    else {
        Write-Host ("$MARK_OK r4: no duplicate keys in {0} ({1} keys)" -f $file.Name, $file.Keys.Count)
    }
}

# ------------------------- r1/r2: CommandForms coverage per target lang
foreach ($t in $targets) {
    $r1 = New-Object 'System.Collections.Generic.List[string]'
    $r2 = New-Object 'System.Collections.Generic.List[string]'

    foreach ($key in ($en.CmdForms.Keys | Sort-Object)) {
        if (-not $t.Keys.ContainsKey($key)) {
            $r1.Add($key)
            continue
        }
        if (-not $t.CmdForms.ContainsKey($key)) {
            # key line exists but the value could not be extracted
            $r1.Add(('{0} (value not parseable as a string)' -f $key))
            continue
        }
        $enSet = Get-FormSet -Raw $en.CmdForms[$key]
        $tSet  = Get-FormSet -Raw $t.CmdForms[$key]
        foreach ($form in ($enSet | Sort-Object)) {
            if (-not $tSet.Contains($form)) {
                $r2.Add(('{0}: missing english form ''{1}''' -f $key, $form))
            }
        }
    }

    if ($r1.Count -gt 0) {
        $failCount += $r1.Count
        Write-Host ("$MARK_FAIL r1: CommandForms keys missing from {0} - {1} key(s)" -f $t.Name, $r1.Count)
        Write-KeyList -Items $r1 -Max 100
    }
    else {
        Write-Host ("$MARK_OK r1: all {0} CommandForms keys present in {1}" -f $en.CmdForms.Count, $t.Name)
    }

    if ($r2.Count -gt 0) {
        $failCount += $r2.Count
        Write-Host ("$MARK_FAIL r2: english command forms missing in {0} - {1} form(s)" -f $t.Name, $r2.Count)
        Write-KeyList -Items $r2 -Max 100
    }
    else {
        Write-Host ("$MARK_OK r2: {0} covers every en_US command form" -f $t.Name)
    }
}

# --------------------------------------- WARN: key diff (ja_JP only)
if ($null -ne $ja) {
    $missingInJa = New-Object 'System.Collections.Generic.List[string]'
    foreach ($key in $en.Keys.Keys) {
        if ($key.StartsWith('CommandForms.', [System.StringComparison]::Ordinal)) { continue } # r1 territory
        if (-not $ja.Keys.ContainsKey($key)) { $missingInJa.Add($key) }
    }
    $orphans = New-Object 'System.Collections.Generic.List[string]'
    foreach ($key in $ja.Keys.Keys) {
        if (-not $en.Keys.ContainsKey($key)) { $orphans.Add($key) }
    }

    if ($missingInJa.Count -gt 0) {
        $warnCount += $missingInJa.Count
        Write-Host ("$MARK_WARN warn: keys in en_US but missing from ja_JP - {0} key(s)" -f $missingInJa.Count)
        Write-KeyList -Items $missingInJa -Max 30
    }
    else {
        Write-Host ("$MARK_OK warn: ja_JP has every en_US key")
    }

    if ($orphans.Count -gt 0) {
        $warnCount += $orphans.Count
        Write-Host ("$MARK_WARN warn: orphan keys only in ja_JP - {0} key(s)" -f $orphans.Count)
        Write-KeyList -Items $orphans -Max 30
    }
    else {
        Write-Host ("$MARK_OK warn: no orphan keys in ja_JP")
    }
}

# ---------------------------------------------------------------- summary
$sw.Stop()
Write-Host ''
if ($failCount -gt 0) {
    Write-Host ("$MARK_FAIL SUMMARY: {0} FAIL, {1} WARN - lang keys out of sync ({2:n2}s)" -f $failCount, $warnCount, $sw.Elapsed.TotalSeconds)
    exit 1
}
if ($warnCount -gt 0 -and $Strict) {
    Write-Host ("$MARK_FAIL SUMMARY: 0 FAIL, {0} WARN - failing due to -Strict ({1:n2}s)" -f $warnCount, $sw.Elapsed.TotalSeconds)
    exit 1
}
if ($warnCount -gt 0) {
    Write-Host ("$MARK_WARN SUMMARY: 0 FAIL, {0} WARN ({1:n2}s)" -f $warnCount, $sw.Elapsed.TotalSeconds)
    exit 0
}
Write-Host ("$MARK_OK SUMMARY: lang keys fully in sync ({0:n2}s)" -f $sw.Elapsed.TotalSeconds)
exit 0
