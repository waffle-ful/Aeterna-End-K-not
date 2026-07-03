<#
.SYNOPSIS
    log-doctor (End K not) - ログ自動診断ツール。

.DESCRIPTION
    Among Us mod「End K not」のトラブル(kick / クラッシュ / desync / フリーズ /
    無音故障)発生後に、蓄積された既知障害シグネチャでログを自動診断し、
    「推定原因 + 対処ポインタ」を日本語で出力します。

    読むログは2系統:
      1) Health系   : <Desktop>\EndKnot_Logs\EndKnot-Health.log / .prev.log / EndKnot-Timeline.log
      2) ゲーム側   : <GameDir>\BepInEx\log.html / LogOutput.log
         GameDir は -GameDir > local.props の <AmongUsPath> > 既定候補 の順で解決。

    13ルール(🔴critical 1-3 / 🟡warn 4-10 / 🟢info 11-13)で検査し、
    各検出は 証拠行 + 推定原因 + 対処 を表示します。
    診断ツールなので常に exit 0 です(ログが無くても診断結果として報告)。

.PARAMETER GameDir
    Among Us インストールフォルダ(BepInEx が入っている場所)。
.PARAMETER Minutes
    直近N分だけを診断対象にする。0(既定)=全期間。
.PARAMETER Detail
    証拠行の表示上限を 3 行 → 10 行に拡張。
.PARAMETER HealthDir
    Health系ログのフォルダ。既定は <Desktop>\EndKnot_Logs (テスト用オーバーライド)。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/log-doctor.ps1
    powershell -File tools/log-doctor.ps1 -Minutes 30 -Detail
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [int]$Minutes = 0,
    [switch]$Detail,
    [string]$HealthDir
)

$ErrorActionPreference = 'Stop'

# 絵文字はコードポイントから組み立てる(BOM 事故に強くするため)
$C_RED  = [char]::ConvertFromUtf32(0x1F534)   # red circle
$C_YEL  = [char]::ConvertFromUtf32(0x1F7E1)   # yellow circle
$C_GRN  = [char]::ConvertFromUtf32(0x1F7E2)   # green circle
$C_OK   = [char]::ConvertFromUtf32(0x2705)    # check mark

$MaxEvidence = 3
if ($Detail) { $MaxEvidence = 10 }

$script:Findings = New-Object 'System.Collections.Generic.List[object]'

function Add-Finding {
    param(
        [ValidateSet('critical','warn','info')][string]$Severity,
        [string]$Rule,
        [string]$Title,
        [string]$Time,          # 代表タイムスタンプ(無ければ空)
        [System.Collections.IList]$Evidence,
        [string]$Cause,
        [string]$Advice,
        [bool]$NoCap = $false   # true なら証拠行の上限を適用しない(DCTX リング等)
    )
    $script:Findings.Add([pscustomobject]@{
        Severity = $Severity; Rule = $Rule; Title = $Title; Time = $Time
        Evidence = $Evidence; Cause = $Cause; Advice = $Advice; NoCap = $NoCap
    })
}

function Format-UnixTime {
    param([long]$Unix)
    try { return [DateTimeOffset]::FromUnixTimeSeconds($Unix).ToLocalTime().ToString('yyyy-MM-dd HH:mm:ss') }
    catch { return "t=$Unix" }
}

function Get-LineUnixTime {
    # 行内の t=<unix秒> を拾う。無ければ -1。
    param([string]$Line)
    $m = [regex]::Match($Line, '\bt=(\d{9,})\b')
    if ($m.Success) { return [long]$m.Groups[1].Value }
    return -1
}

# ---------------------------------------------------------------- ログ探索
$NowUnix   = [DateTimeOffset]::Now.ToUnixTimeSeconds()
$CutoffUnix = 0
$CutoffTime = [datetime]::MinValue
if ($Minutes -gt 0) {
    $CutoffUnix = $NowUnix - ([long]$Minutes * 60)
    $CutoffTime = (Get-Date).AddMinutes(-$Minutes)
}

if (-not $HealthDir) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    $HealthDir = Join-Path $desktop 'EndKnot_Logs'
}

$repoRoot = Split-Path -Parent $PSScriptRoot

# GameDir 解決: -GameDir > local.props > 既定候補
$gameDirSource = ''
if ($GameDir -and (Test-Path -LiteralPath $GameDir)) {
    $gameDirSource = '-GameDir 指定'
}
else {
    $GameDir = $null
    $localProps = Join-Path $repoRoot 'local.props'
    if (Test-Path -LiteralPath $localProps) {
        try {
            $propsText = [System.IO.File]::ReadAllText($localProps)
            $m = [regex]::Match($propsText, '<AmongUsPath>\s*(.*?)\s*</AmongUsPath>')
            if ($m.Success -and (Test-Path -LiteralPath $m.Groups[1].Value)) {
                $GameDir = $m.Groups[1].Value
                $gameDirSource = 'local.props'
            }
        }
        catch { }
    }
    if (-not $GameDir) {
        $candidates = @(
            'C:\Program Files (x86)\Steam\steamapps\common\Among Us',
            'C:\Program Files\Epic Games\AmongUs'
        )
        foreach ($c in $candidates) {
            if (Test-Path -LiteralPath $c) { $GameDir = $c; $gameDirSource = '既定候補'; break }
        }
    }
}

$logTargets = New-Object 'System.Collections.Generic.List[object]'
function Add-LogTarget {
    param([string]$Kind, [string]$Name, [string]$Path)
    $found = $false; $sizeKB = 0; $lastWrite = ''
    if ($Path -and (Test-Path -LiteralPath $Path)) {
        $fi = Get-Item -LiteralPath $Path
        $found = $true
        $sizeKB = [Math]::Round($fi.Length / 1KB, 1)
        $lastWrite = $fi.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')
    }
    $logTargets.Add([pscustomobject]@{ Kind = $Kind; Name = $Name; Path = $Path; Found = $found; SizeKB = $sizeKB; LastWrite = $lastWrite })
}

$healthLivePath = Join-Path $HealthDir 'EndKnot-Health.log'
$healthPrevPath = Join-Path $HealthDir 'EndKnot-Health.prev.log'
$timelinePath   = Join-Path $HealthDir 'EndKnot-Timeline.log'
Add-LogTarget -Kind 'Health' -Name 'EndKnot-Health.log'      -Path $healthLivePath
Add-LogTarget -Kind 'Health' -Name 'EndKnot-Health.prev.log' -Path $healthPrevPath
Add-LogTarget -Kind 'Health' -Name 'EndKnot-Timeline.log'    -Path $timelinePath

$logHtmlPath = $null; $logOutputPath = $null
if ($GameDir) {
    $logHtmlPath   = Join-Path $GameDir 'BepInEx\log.html'
    $logOutputPath = Join-Path $GameDir 'BepInEx\LogOutput.log'
}
Add-LogTarget -Kind 'Game' -Name 'log.html'      -Path $logHtmlPath
Add-LogTarget -Kind 'Game' -Name 'LogOutput.log' -Path $logOutputPath

# ---------------------------------------------------------------- 台帳表示
Write-Host '=== log-doctor (End K not ログ自動診断) ==='
Write-Host ('実行時刻   : {0}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))
if ($Minutes -gt 0) { Write-Host ('診断範囲   : 直近 {0} 分のみ' -f $Minutes) }
else                { Write-Host  '診断範囲   : 全期間' }
Write-Host ('Health dir : {0}' -f $HealthDir)
if ($GameDir) { Write-Host ('GameDir    : {0} ({1})' -f $GameDir, $gameDirSource) }
else          { Write-Host  'GameDir    : 解決できず (-GameDir で指定してください)' }
Write-Host ''
Write-Host '--- ログ台帳 ---'
foreach ($t in $logTargets) {
    if ($t.Found) {
        Write-Host ('  [OK] {0,-25} {1,10} KB  最終更新 {2}' -f $t.Name, $t.SizeKB, $t.LastWrite)
    }
    else {
        $p = $t.Path
        if (-not $p) { $p = '(GameDir 未解決)' }
        Write-Host ('  [--] {0,-25} 見つかりません: {1}' -f $t.Name, $p)
    }
}
Write-Host ''

# ---------------------------------------------------------------- Health系 読み込み
# セッション = SESSION start 行で区切る。prev(古) → live(新) の順。
# Timeline は sid= 付きの重複記録なのでルール判定には使わない(台帳のみ)。
$healthSessions = New-Object 'System.Collections.Generic.List[object]'
foreach ($hp in @($healthPrevPath, $healthLivePath)) {
    if (-not (Test-Path -LiteralPath $hp)) { continue }
    $rawLines = @()
    try { $rawLines = [System.IO.File]::ReadAllLines($hp) }
    catch { continue }

    $curLines = New-Object 'System.Collections.Generic.List[string]'
    $curName = [System.IO.Path]::GetFileName($hp)
    foreach ($line in $rawLines) {
        if ($line.StartsWith('SESSION start') -and $curLines.Count -gt 0) {
            $healthSessions.Add([pscustomobject]@{ Source = $curName; Lines = $curLines })
            $curLines = New-Object 'System.Collections.Generic.List[string]'
        }
        # -Minutes フィルタ: t= が読めて範囲外なら捨てる(読めない行は残す)
        if ($Minutes -gt 0) {
            $lt = Get-LineUnixTime -Line $line
            if ($lt -ge 0 -and $lt -lt $CutoffUnix) { continue }
        }
        $curLines.Add($line)
    }
    if ($curLines.Count -gt 0) {
        $healthSessions.Add([pscustomobject]@{ Source = $curName; Lines = $curLines })
    }
}

# ---------------------------------------------------------------- log.html 読み込み
# 各エントリ: <div class='log-entry LEVEL'> 本文 </div> (厳密XMLパースはしない)
# 本文形式 [HH:mm:ss][tag]text / エラー時 [HH:mm:ss][Class.Member(File.cs:line)][tag]text
$htmlEntries = New-Object 'System.Collections.Generic.List[object]'   # Level / Text / Time(datetime or MinValue)
if ($logHtmlPath -and (Test-Path -LiteralPath $logHtmlPath)) {
    try {
        $htmlText = [System.IO.File]::ReadAllText($logHtmlPath)
        $baseDate = (Get-Item -LiteralPath $logHtmlPath).LastWriteTime
        $entryRegex = [regex]::new("<div class='log-entry (\w+)'>\s*(.*?)\s*</div>",
            [System.Text.RegularExpressions.RegexOptions]::Singleline)
        foreach ($m in $entryRegex.Matches($htmlText)) {
            $level = $m.Groups[1].Value.ToLowerInvariant()
            $text = ($m.Groups[2].Value -replace '\s+', ' ').Trim()
            $ts = [datetime]::MinValue
            $tm = [regex]::Match($text, '^\[(\d{2}):(\d{2}):(\d{2})\]')
            if ($tm.Success) {
                # 日付は載っていないので最終更新日の日付を仮定し、未来になるなら前日とみなす
                $ts = Get-Date -Year $baseDate.Year -Month $baseDate.Month -Day $baseDate.Day `
                    -Hour ([int]$tm.Groups[1].Value) -Minute ([int]$tm.Groups[2].Value) -Second ([int]$tm.Groups[3].Value)
                if ($ts -gt $baseDate.AddMinutes(5)) { $ts = $ts.AddDays(-1) }
            }
            if ($Minutes -gt 0 -and $ts -ne [datetime]::MinValue -and $ts -lt $CutoffTime) { continue }
            $htmlEntries.Add([pscustomobject]@{ Level = $level; Text = $text; Time = $ts })
        }
    }
    catch { Write-Host ('  (log.html の読み込みに失敗: {0})' -f $_.Exception.Message) }
}

# ---------------------------------------------------------------- LogOutput.log 読み込み
# BepInEx 標準ログ。行にタイムスタンプが無いので -Minutes フィルタ対象外。
$outLines = @()
if ($logOutputPath -and (Test-Path -LiteralPath $logOutputPath)) {
    try { $outLines = [System.IO.File]::ReadAllLines($logOutputPath) }
    catch { }
}

function Cap-Text {
    param([string]$s, [int]$Max = 200)
    if ($s.Length -gt $Max) { return $s.Substring(0, $Max) + '...' }
    return $s
}

# =================================================================
# 🔴 rule 1: CLR ランタイム即死 (0x80131506 / ExecutionEngineException / Fatal error)
# =================================================================
$r1 = New-Object 'System.Collections.Generic.List[string]'
$r1Regex = '0x80131506|ExecutionEngineException|Fatal error'
foreach ($line in $outLines) {
    if ($line -match $r1Regex) { $r1.Add('LogOutput.log: ' + (Cap-Text $line)) }
}
foreach ($e in $htmlEntries) {
    if ($e.Text -match $r1Regex) { $r1.Add('log.html: ' + (Cap-Text $e.Text)) }
}
if ($r1.Count -gt 0) {
    Add-Finding -Severity critical -Rule 'rule1' -Title ('CLR ランタイム即死シグネチャ検出 ({0}件)' -f $r1.Count) -Time '' `
        -Evidence $r1 `
        -Cause 'CLR/IL2CPP レベルのプロセス即死。既知原因は3系統: (a) hot な IL2CPP メソッドへの string 引数 Harmony パッチ (b) CNO の PlayerId 200-254 がバニラ配列を踏み越え OOB (c) チャット欄テキストの dangling String* 直読み。' `
        -Advice 'memory 参照: project_messagewriter_write_patch_clr_crash / project_cno_playerid_overflows_vanilla_arrays / project_live_chatfield_gettext_fatal。直近に足した Harmony パッチ・CNO・チャットUI読取りを疑うこと。'
}

# =================================================================
# 🔴 rule 2: DC reason=Hacking (公式 anti-cheat kick) + 直前 DCTX リング
# =================================================================
foreach ($sess in $healthSessions) {
    $lines = $sess.Lines
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -notmatch '^DC reason=Hacking\b') { continue }

        $ev = New-Object 'System.Collections.Generic.List[string]'
        # 直前の連続 DCTX 行(送信リングのダンプ)を全部拾う
        $j = $i - 1
        $dctxBlock = New-Object 'System.Collections.Generic.List[string]'
        while ($j -ge 0 -and $lines[$j].StartsWith('DCTX ')) {
            $dctxBlock.Insert(0, $lines[$j])
            $j--
        }
        foreach ($d in $dctxBlock) {
            $mark = ''
            $lm = [regex]::Match($d, '\blen=(\d+)\b')
            if ($lm.Success -and [int]$lm.Groups[1].Value -ge 900) { $mark = '   <== 容疑者 (len>=900: ~1KB kick閾値に接近)' }
            $ev.Add($d + $mark)
        }
        if ($dctxBlock.Count -eq 0) { $ev.Add('(直前の DCTX 送信リングは記録なし)') }
        $ev.Add($lines[$i])

        $t = Get-LineUnixTime -Line $lines[$i]
        $timeStr = ''
        if ($t -ge 0) { $timeStr = Format-UnixTime -Unix $t }
        Add-Finding -Severity critical -Rule 'rule2' -Title ('公式 anti-cheat kick (DC reason=Hacking) [{0}]' -f $sess.Source) -Time $timeStr `
            -Evidence $ev -NoCap $true `
            -Cause '公式サーバーの anti-cheat による切断。~1KB 単一パケット閾値超過 / 不正 GUID / ロビーでの PlayerControl 系 CNO などが引き金。' `
            -Advice '対処: (1) len>=900 マークの送信を縮小する(sprite圧縮・分割送信) (2) PluginGuid は V4 UUID 必須 (3) PlayerControl-based CNO はロビーで出さない。memory 参照: project_au2026_1kb_packet_threshold / project_innersloth_uuid_pluginguid_required / project_cno_lobby_anticheat_kick。'
    }
}

# =================================================================
# 🔴 rule 3: BepInEx 未捕捉例外 (Unhandled exception ブロック)
# =================================================================
for ($i = 0; $i -lt $outLines.Count; $i++) {
    if ($outLines[$i] -notmatch 'Unhandled exception') { continue }
    $ev = New-Object 'System.Collections.Generic.List[string]'
    $ev.Add((Cap-Text $outLines[$i]))
    # 例外メッセージ行と先頭フレーム(最初の "at ..." 行)を拾う
    $firstFrame = $null
    for ($j = $i + 1; $j -lt [Math]::Min($outLines.Count, $i + 30); $j++) {
        $l = $outLines[$j].Trim()
        if ($l.Length -eq 0) { continue }
        if ($l.StartsWith('at ')) { $firstFrame = $outLines[$j]; break }
        if ($ev.Count -lt 3) { $ev.Add((Cap-Text $outLines[$j])) }
    }
    if ($firstFrame) { $ev.Add('先頭フレーム: ' + (Cap-Text $firstFrame.Trim())) }
    Add-Finding -Severity critical -Rule 'rule3' -Title '未捕捉例外 (Unhandled exception)' -Time '' `
        -Evidence $ev -NoCap $true `
        -Cause 'try/catch の外で例外が発生。先頭フレームのクラス/メソッドが直接の発生源。' `
        -Advice '先頭フレームのメソッドを起点にコードを確認。kick/切断とセットなら rule2 の結果と突き合わせること(診断は log.html と LogOutput.log の両方必須)。'
    # 同一ブロック内の再ヒットを避けるため少し飛ばす
    $i += 3
}

# =================================================================
# 🟡 rule 4: Large reliable packet (1KB kick 閾値接近)
# =================================================================
$r4Map = @{}   # name -> @{ Count; MaxLen; Sample }
foreach ($e in $htmlEntries) {
    if ($e.Text -notmatch 'Large reliable packet') { continue }
    $name = '(不明)'
    $len = 0
    $m = [regex]::Match($e.Text, 'Large reliable packet "([^"]+)"[^\(]*\((\d+) bytes\)')
    if ($m.Success) {
        $name = $m.Groups[1].Value
        $len = [int]$m.Groups[2].Value
    }
    if (-not $r4Map.ContainsKey($name)) { $r4Map[$name] = @{ Count = 0; MaxLen = 0; Sample = $e.Text } }
    $r4Map[$name].Count++
    if ($len -gt $r4Map[$name].MaxLen) { $r4Map[$name].MaxLen = $len; $r4Map[$name].Sample = $e.Text }
}
if ($r4Map.Count -gt 0) {
    $ev = New-Object 'System.Collections.Generic.List[string]'
    $total = 0
    foreach ($name in ($r4Map.Keys | Sort-Object { $r4Map[$_].MaxLen } -Descending)) {
        $info = $r4Map[$name]
        $total += $info.Count
        $ev.Add(('{0}: {1}回 / 最大 {2} bytes  例: {3}' -f $name, $info.Count, $info.MaxLen, (Cap-Text $info.Sample 120)))
    }
    Add-Finding -Severity warn -Rule 'rule4' -Title ('Large reliable packet 検出 ({0}回)' -f $total) -Time '' `
        -Evidence $ev `
        -Cause '~1KB 単一パケット閾値(公式 anti-cheat kick)への接近。chat/CNO/outfit serialization の肥大が典型。' `
        -Advice 'RPC 名ごとにペイロードを縮小(sprite の run-length 圧縮、分割送信)。memory 参照: project_au2026_1kb_packet_threshold / project_cno_sprite_mark_w_optimization。'
}

# =================================================================
# 🟡 rule 5: Too many SnapTo calls (throttle 枯渇 → desync 前兆)
# =================================================================
$r5 = New-Object 'System.Collections.Generic.List[string]'
foreach ($e in $htmlEntries) {
    if ($e.Text -match 'Too many SnapTo calls') { $r5.Add((Cap-Text $e.Text)) }
}
if ($r5.Count -gt 0) {
    Add-Finding -Severity warn -Rule 'rule5' -Title ('SnapTo throttle 枯渇 ({0}回)' -f $r5.Count) -Time '' `
        -Evidence $r5 `
        -Cause 'SnapTo のラウンド内呼び出し数が上限に到達(80で SendOption.None に降格 / 100で送信中止)。非ホストの位置 desync の前兆。' `
        -Advice '毎フレーム TP 系役職(Penguin / Goose / 竜巻 / 雪玉 / Tama / Pelican / Magician 等)の 0.2s 間引き漏れを疑う。memory 参照: project_official_server_position_desync_penguin_goose。'
}

# =================================================================
# 🟡 rule 6: NullReferenceException + Option/GetBool/GetFloat が5件以上
# =================================================================
$r6 = New-Object 'System.Collections.Generic.List[string]'
foreach ($e in $htmlEntries) {
    if ($e.Text -match 'NullReferenceException' -and $e.Text -match 'Option|GetBool|GetFloat') {
        $r6.Add((Cap-Text $e.Text))
    }
}
if ($r6.Count -ge 5) {
    Add-Finding -Severity warn -Rule 'rule6' -Title ('オプションロード未完レースの疑い (Option系 NRE {0}件)' -f $r6.Count) -Time '' `
        -Evidence $r6 `
        -Cause 'Option の Load() は非同期・ゲート無しのため、ロード完了前に参照すると null 参照が洪水になる(自動ホスト直後に多発する既知レース)。' `
        -Advice '参照側を「?.GetBool() != true」パターンで守る。memory 参照: project_option_load_race_null_fields。'
}

# =================================================================
# 🟡 rule 7: FormatException + Version (-alpha サフィックス既知罠)
# =================================================================
$r7 = New-Object 'System.Collections.Generic.List[string]'
foreach ($e in $htmlEntries) {
    if ($e.Text -match 'FormatException' -and $e.Text -match 'Version') { $r7.Add('log.html: ' + (Cap-Text $e.Text)) }
}
foreach ($line in $outLines) {
    if ($line -match 'FormatException' -and $line -match 'Version') { $r7.Add('LogOutput.log: ' + (Cap-Text $line)) }
}
if ($r7.Count -gt 0) {
    Add-Finding -Severity warn -Rule 'rule7' -Title ('Version.Parse の FormatException ({0}件)' -f $r7.Count) -Time '' `
        -Evidence $r7 `
        -Cause 'Version.Parse が "-alpha" 等のサフィックス付きバージョン文字列を食った既知罠の再発。' `
        -Advice 'パース前に Split(''-'')[0] でサフィックスを除去するのが正。memory 参照: reference_version_parse_alpha_suffix_and_chat_caret_crash。'
}

# =================================================================
# 🟡 rule 8: HB 間隔 15秒超 = フリーズ窓 (通常は5秒毎)
# =================================================================
foreach ($sess in $healthSessions) {
    $prevT = -1; $prevState = '?'
    $gaps = New-Object 'System.Collections.Generic.List[string]'
    foreach ($line in $sess.Lines) {
        $m = [regex]::Match($line, '^HB t=(\d+)\b.*?\bstate=(\S+)')
        if (-not $m.Success) { continue }
        $t = [long]$m.Groups[1].Value
        $state = $m.Groups[2].Value
        if ($prevT -ge 0) {
            $gap = $t - $prevT
            if ($gap -gt 15) {
                $gaps.Add(('{0} → {1}  (空白 {2} 秒 / 直前 state={3})' -f (Format-UnixTime $prevT), (Format-UnixTime $t), $gap, $prevState))
            }
        }
        $prevT = $t; $prevState = $state
    }
    if ($gaps.Count -gt 0) {
        Add-Finding -Severity warn -Rule 'rule8' -Title ('フリーズ窓の疑い (HB 間隔 15秒超 x{0}) [{1}]' -f $gaps.Count, $sess.Source) -Time '' `
            -Evidence $gaps `
            -Cause 'heartbeat は通常5秒毎。15秒超の空白はメインスレッドのフリーズ / 激重処理 / プロセス停止を示す。' `
            -Advice '空白の発生時刻と直前 state を手掛かりに、log.html の同時刻帯を確認(重い procgen / 大量 CNO 生成 / GC 等)。'
    }
}

# =================================================================
# 🟡 rule 9: state=Menu の HB が連続5分以上 + セッション内に DC 行 = 自動再ホスト失敗放置の疑い
# =================================================================
foreach ($sess in $healthSessions) {
    $hasDc = $false
    foreach ($line in $sess.Lines) { if ($line.StartsWith('DC ')) { $hasDc = $true; break } }
    if (-not $hasDc) { continue }

    $runStart = -1; $runEnd = -1
    $bestStart = -1; $bestEnd = -1
    foreach ($line in $sess.Lines) {
        $m = [regex]::Match($line, '^HB t=(\d+)\b.*?\bstate=(\S+)')
        if (-not $m.Success) { continue }
        $t = [long]$m.Groups[1].Value
        if ($m.Groups[2].Value -eq 'Menu') {
            if ($runStart -lt 0) { $runStart = $t }
            $runEnd = $t
        }
        else {
            if ($runStart -ge 0 -and ($runEnd - $runStart) -gt ($bestEnd - $bestStart)) { $bestStart = $runStart; $bestEnd = $runEnd }
            $runStart = -1; $runEnd = -1
        }
    }
    if ($runStart -ge 0 -and ($runEnd - $runStart) -gt ($bestEnd - $bestStart)) { $bestStart = $runStart; $bestEnd = $runEnd }

    if ($bestStart -ge 0 -and ($bestEnd - $bestStart) -ge 300) {
        $ev = New-Object 'System.Collections.Generic.List[string]'
        $ev.Add(('state=Menu の連続 HB: {0} → {1} ({2} 秒)' -f (Format-UnixTime $bestStart), (Format-UnixTime $bestEnd), ($bestEnd - $bestStart)))
        foreach ($line in $sess.Lines) {
            if ($line.StartsWith('DC ')) { $ev.Add((Cap-Text $line)) }
        }
        # log.html に AutoRehost / GiveUp 系の行があれば併記
        foreach ($e in $htmlEntries) {
            if ($e.Text -match 'AutoRehost|Rehost|GiveUp|Give up') { $ev.Add('log.html: ' + (Cap-Text $e.Text)) }
        }
        Add-Finding -Severity warn -Rule 'rule9' -Title ('自動再ホスト失敗で放置の疑い [{0}]' -f $sess.Source) -Time (Format-UnixTime $bestStart) `
            -Evidence $ev `
            -Cause '切断(DC)後に Menu 状態のまま5分以上滞留。自動再ホストが起動しなかった / GiveUp した / オプション未ロードで再ホストが失敗した可能性。' `
            -Advice 'log.html の AutoRehost / GiveUp 行で失敗理由を特定。memory 参照: project_auto_rehost_feature / project_option_load_race_null_fields。'
    }
}

# =================================================================
# 🟡 rule 10: wsMB がセッション先頭比 +800MB 超 or 絶対値 2200MB 超 = メモリリーク疑い
# =================================================================
foreach ($sess in $healthSessions) {
    $first = -1; $max = -1; $maxT = -1; $last = -1; $firstT = -1; $lastT = -1
    foreach ($line in $sess.Lines) {
        $m = [regex]::Match($line, '^HB t=(\d+)\b.*?\bwsMB=(\d+)\b')
        if (-not $m.Success) { continue }
        $t = [long]$m.Groups[1].Value
        $ws = [long]$m.Groups[2].Value
        if ($ws -le 0) { continue }
        if ($first -lt 0) { $first = $ws; $firstT = $t }
        if ($ws -gt $max) { $max = $ws; $maxT = $t }
        $last = $ws; $lastT = $t
    }
    if ($first -lt 0) { continue }
    $growth = $max - $first
    if ($growth -gt 800 -or $max -gt 2200) {
        $ev = New-Object 'System.Collections.Generic.List[string]'
        $ev.Add(('先頭 wsMB={0} ({1})' -f $first, (Format-UnixTime $firstT)))
        $ev.Add(('最大 wsMB={0} ({1})  先頭比 +{2} MB' -f $max, (Format-UnixTime $maxT), $growth))
        $ev.Add(('最終 wsMB={0} ({1})' -f $last, (Format-UnixTime $lastT)))
        Add-Finding -Severity warn -Rule 'rule10' -Title ('メモリリーク疑い (wsMB 先頭比 +{0} MB / 最大 {1} MB) [{2}]' -f $growth, $max, $sess.Source) -Time '' `
            -Evidence $ev `
            -Cause 'working set の異常成長。既往例: DontUnloadUnusedAsset な AudioClip の Destroy 漏れ。' `
            -Advice 'ロード系(BGM / sprite / CNO)の Destroy 漏れを疑う。長時間ホストなら再起動を挟みつつ、増加が始まる時刻帯の操作を特定する。'
    }
}

# =================================================================
# 🟢 rule 11: GAMEEND flags 非空 + ANOM 行 = 異常ゲーム列挙
# =================================================================
$r11 = New-Object 'System.Collections.Generic.List[string]'
$exTagTotals = @{}
foreach ($sess in $healthSessions) {
    foreach ($line in $sess.Lines) {
        if ($line.StartsWith('GAMEEND ')) {
            $fm = [regex]::Match($line, 'flags=\[([^\]]*)\]')
            if ($fm.Success -and $fm.Groups[1].Value.Trim().Length -gt 0) {
                $r11.Add((Cap-Text $line 250))
                $em = [regex]::Match($line, 'exTags=\[([^\]]*)\]')
                if ($em.Success -and $em.Groups[1].Value.Trim().Length -gt 0) {
                    foreach ($pair in $em.Groups[1].Value.Split(',')) {
                        $kv = $pair.Trim().Split(':')
                        if ($kv.Length -eq 2) {
                            $n = 0
                            if ([int]::TryParse($kv[1], [ref]$n)) {
                                if (-not $exTagTotals.ContainsKey($kv[0])) { $exTagTotals[$kv[0]] = 0 }
                                $exTagTotals[$kv[0]] += $n
                            }
                        }
                    }
                }
            }
        }
        elseif ($line.StartsWith('ANOM ')) {
            $r11.Add((Cap-Text $line 250))
        }
    }
}
if ($r11.Count -gt 0) {
    $ev = New-Object 'System.Collections.Generic.List[string]'
    foreach ($l in $r11) { $ev.Add($l) }
    if ($exTagTotals.Count -gt 0) {
        $top = $exTagTotals.Keys | Sort-Object { $exTagTotals[$_] } -Descending | Select-Object -First 3
        $topStr = ($top | ForEach-Object { '{0}:{1}' -f $_, $exTagTotals[$_] }) -join ', '
        $ev.Add(('exTags 上位: {0}' -f $topStr))
    }
    Add-Finding -Severity info -Rule 'rule11' -Title ('異常ゲーム (GAMEEND flags / ANOM) {0}件' -f $r11.Count) -Time '' `
        -Evidence $ev `
        -Cause 'flags: short=30秒未満終了 / nowinner=勝者なし / error=勝敗判定エラー / alldead=全滅 / unattributed=勝者未帰属。exTags はゲーム中の例外タグ集計。' `
        -Advice 'exTags 上位のタグ(クラス名)を起点に log.html の該当例外スタックを確認。'
}

# =================================================================
# 🟢 rule 12: UIANOM 集計 (kind 別)
# =================================================================
$r12Map = @{}
$r12Total = 0
foreach ($sess in $healthSessions) {
    foreach ($line in $sess.Lines) {
        if (-not $line.StartsWith('UIANOM')) { continue }
        $r12Total++
        $kind = '(不明)'
        $km = [regex]::Match($line, '\bkind=(\S+)')
        if ($km.Success) { $kind = $km.Groups[1].Value }
        if (-not $r12Map.ContainsKey($kind)) { $r12Map[$kind] = 0 }
        $r12Map[$kind]++
    }
}
if ($r12Total -gt 0) {
    $ev = New-Object 'System.Collections.Generic.List[string]'
    foreach ($kind in ($r12Map.Keys | Sort-Object { $r12Map[$_] } -Descending)) {
        $ev.Add(('kind={0}: {1}件' -f $kind, $r12Map[$kind]))
    }
    Add-Finding -Severity info -Rule 'rule12' -Title ('UI 異常検知 (UIANOM) {0}件' -f $r12Total) -Time '' `
        -Evidence $ev `
        -Cause 'UiAnomalyWatch がチャットUI等の異常(重複描画など)を検知した記録。' `
        -Advice '件数が多い kind から順に、発生時刻帯の操作/画面を特定する。'
}

# =================================================================
# 🟢 rule 13: EOS / relogin / token 失敗系 (log.html の error/warn)
# =================================================================
$r13 = New-Object 'System.Collections.Generic.List[string]'
foreach ($e in $htmlEntries) {
    if ($e.Level -ne 'error' -and $e.Level -ne 'warning' -and $e.Level -ne 'warn' -and $e.Level -ne 'fatal') { continue }
    if ($e.Text -match '(?i)\bEOS\b|relogin|re-login|token') { $r13.Add((Cap-Text $e.Text)) }
}
if ($r13.Count -gt 0) {
    Add-Finding -Severity info -Rule 'rule13' -Title ('EOS ログイン/トークン系のエラー ({0}件)' -f $r13.Count) -Time '' `
        -Evidence $r13 `
        -Cause 'EOS 認証トークンの失効 / 再ログイン失敗。予防再ログインは3時間毎に走る設計。' `
        -Advice '頻発しているなら再ログイン間隔の見直し。memory 参照: project_h1_eos_relogin_patch_implemented。'
}

# ---------------------------------------------------------------- 結果出力
Write-Host '--- 診断結果 ---'
$sevOrder = @{ critical = 0; warn = 1; info = 2 }
$sevMark  = @{ critical = $C_RED; warn = $C_YEL; info = $C_GRN }
$sorted = $script:Findings | Sort-Object { $sevOrder[$_.Severity] }

$critCount = 0; $warnCount = 0; $infoCount = 0
foreach ($f in $sorted) {
    switch ($f.Severity) {
        'critical' { $critCount++ }
        'warn'     { $warnCount++ }
        'info'     { $infoCount++ }
    }
    Write-Host ''
    $head = ('{0} [{1}] {2}' -f $sevMark[$f.Severity], $f.Rule, $f.Title)
    Write-Host $head
    if ($f.Time) { Write-Host ('    時刻: {0}' -f $f.Time) }
    Write-Host '    証拠:'
    $cap = $MaxEvidence
    if ($f.NoCap) { $cap = [int]::MaxValue }
    $shown = 0
    foreach ($ln in $f.Evidence) {
        if ($shown -ge $cap) { break }
        Write-Host ('      {0}' -f $ln)
        $shown++
    }
    if ($f.Evidence.Count -gt $shown) {
        Write-Host ('      ...ほか {0} 行 (-Detail で拡張)' -f ($f.Evidence.Count - $shown))
    }
    Write-Host ('    推定原因: {0}' -f $f.Cause)
    Write-Host ('    対処    : {0}' -f $f.Advice)
}

if ($sorted.Count -eq 0) {
    Write-Host ''
    Write-Host ('  {0} 異常シグネチャ検出なし' -f $C_OK)
}

Write-Host ''
Write-Host '--- サマリ ---'
if ($sorted.Count -eq 0) {
    Write-Host ('  {0} 異常シグネチャ検出なし' -f $C_OK)
}
else {
    Write-Host ('  {0} {1}件  {2} {3}件  {4} {5}件' -f $C_RED, $critCount, $C_YEL, $warnCount, $C_GRN, $infoCount)
}
exit 0
