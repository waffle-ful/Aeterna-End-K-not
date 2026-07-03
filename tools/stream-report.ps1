<#
.SYNOPSIS
    stream-report (End K not) - 配信後の振り返りレポートツール。

.DESCRIPTION
    配信(ホスト)を終えたあとに、その日のログから
      §1 試合タイムライン (EndKnot-Timeline.log)
      §2 エラー/例外ダイジェスト (log.html: ライブ + 当日 dump)
      §3 キーワード発言抽出 (ReceiveChat / SendChat)
    の3セクションを日本語でまとめて出力します。

    読むログ:
      1) Timeline : <Desktop>\EndKnot_Logs\EndKnot-Timeline.log (sid= プレフィックス付き追記式)
      2) ライブ   : <GameDir>\BepInEx\log.html
         GameDir は -GameDir > local.props の <AmongUsPath> > 既定候補 の順で解決。
      3) dump     : <Desktop>\EndKnot_Logs\<yyyy-MM-dd_HH.mm.ss>\EndKnot-v*-LOG.html
         (対象日のフォルダのみ。dump は同一プロセスの累積スナップショットなので重複除去して結合)

    同内容の Markdown を <Desktop>\EndKnot_Logs\StreamReport-<Date>.md に保存します(-NoSave で抑止)。
    レポートツールなので常に exit 0 です(ログが無くても結果として報告)。

.PARAMETER Date
    対象日 (yyyy-MM-dd)。既定は今日。
.PARAMETER GameDir
    Among Us インストールフォルダ(BepInEx が入っている場所)。
.PARAMETER Keywords
    §3 の抽出キーワード(カンマ区切り、大文字小文字無視)。既定は「バグ/ラグ/フリーズ」系の定番セット。
.PARAMETER Detail
    §2 の代表行の表示上限を 3 行 → 10 行に拡張。
.PARAMETER NoSave
    Markdown の保存を行わない。
.PARAMETER HealthDir
    Timeline / dump フォルダの場所。既定は <Desktop>\EndKnot_Logs (テスト用オーバーライド)。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/stream-report.ps1
    powershell -File tools/stream-report.ps1 -Date 2026-07-01 -Detail
    powershell -File tools/stream-report.ps1 -Keywords "落ち,重い" -NoSave
#>
[CmdletBinding()]
param(
    [string]$Date,
    [string]$GameDir,
    [string]$Keywords = 'バグ,bug,ばぐ,おかしい,動かない,うごかない,壊れ,こわれ,ラグ,らぐ,固まっ,かたまっ,フリーズ,freeze,broken,glitch,チート,cheat',
    [switch]$Detail,
    [switch]$NoSave,
    [string]$HealthDir
)

$ErrorActionPreference = 'Stop'

# 絵文字はコードポイントから組み立てる(BOM 事故に強くするため)
$C_RED  = [char]::ConvertFromUtf32(0x1F534)   # red circle
$C_YEL  = [char]::ConvertFromUtf32(0x1F7E1)   # yellow circle
$C_GRN  = [char]::ConvertFromUtf32(0x1F7E2)   # green circle
$C_OK   = [char]::ConvertFromUtf32(0x2705)    # check mark

$MaxRep = 3
if ($Detail) { $MaxRep = 10 }

# ---------------------------------------------------------------- 出力ビルダー (コンソール + Markdown 同時)
$script:Md = New-Object 'System.Collections.Generic.List[string]'
$script:InFence = $false

function Out-Body {
    param([string]$s)
    Write-Host $s
    $script:Md.Add($s)
}

function Out-Head {
    param([string]$t)
    Write-Host ''
    Write-Host ('--- {0} ---' -f $t)
    if ($script:InFence) { $script:Md.Add('```'); $script:InFence = $false }
    $script:Md.Add('')
    $script:Md.Add('## ' + $t)
    $script:Md.Add('```text')
    $script:InFence = $true
}

function Close-Fence {
    if ($script:InFence) { $script:Md.Add('```'); $script:InFence = $false }
}

function Cap-Text {
    param([string]$s, [int]$Max = 200)
    if ($s.Length -gt $Max) { return $s.Substring(0, $Max) + '...' }
    return $s
}

# ---------------------------------------------------------------- 対象日の決定
$TargetDate = [datetime]::MinValue
if ($Date) {
    if (-not [datetime]::TryParseExact($Date, 'yyyy-MM-dd', $null,
            [System.Globalization.DateTimeStyles]::None, [ref]$TargetDate)) {
        Write-Host ('-Date の形式が不正です: "{0}" (yyyy-MM-dd で指定してください)' -f $Date)
        exit 0
    }
}
else {
    $TargetDate = (Get-Date).Date
}
$TargetDate = $TargetDate.Date
$DateStr = $TargetDate.ToString('yyyy-MM-dd')

# ---------------------------------------------------------------- 入力パス解決
if (-not $HealthDir) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    $HealthDir = Join-Path $desktop 'EndKnot_Logs'
}

$repoRoot = Split-Path -Parent $PSScriptRoot

# GameDir 解決: -GameDir > local.props > 既定候補 (log-doctor と同じ)
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

$timelinePath = Join-Path $HealthDir 'EndKnot-Timeline.log'
$logHtmlPath = $null
if ($GameDir) { $logHtmlPath = Join-Path $GameDir 'BepInEx\log.html' }

# 当日の dump フォルダ (<yyyy-MM-dd_HH.mm.ss>) を名前で拾う
$dumpHtmls = New-Object 'System.Collections.Generic.List[object]'   # Path / Anchor(datetime) / Name
if (Test-Path -LiteralPath $HealthDir) {
    $folderRegex = [regex]::new('^(\d{4}-\d{2}-\d{2})_(\d{2})\.(\d{2})\.(\d{2})$')
    foreach ($dir in (Get-ChildItem -LiteralPath $HealthDir -Directory | Sort-Object Name)) {
        $fm = $folderRegex.Match($dir.Name)
        if (-not $fm.Success) { continue }
        if ($fm.Groups[1].Value -ne $DateStr) { continue }
        $anchor = [datetime]::ParseExact($fm.Groups[1].Value, 'yyyy-MM-dd', $null)
        $anchor = $anchor.AddHours([int]$fm.Groups[2].Value).AddMinutes([int]$fm.Groups[3].Value).AddSeconds([int]$fm.Groups[4].Value)
        foreach ($f in (Get-ChildItem -LiteralPath $dir.FullName -Filter 'EndKnot-v*-LOG.html' -File)) {
            $dumpHtmls.Add([pscustomobject]@{ Path = $f.FullName; Anchor = $anchor; Name = ('dump {0}' -f $dir.Name) })
        }
    }
}

# ---------------------------------------------------------------- 台帳表示
Out-Body ('# End K not 配信振り返りレポート ({0})' -f $DateStr)
Out-Body ''
Out-Body ('生成時刻   : {0}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))
Out-Body ('対象日     : {0}' -f $DateStr)
Out-Body ('Health dir : {0}' -f $HealthDir)
if ($GameDir) { Out-Body ('GameDir    : {0} ({1})' -f $GameDir, $gameDirSource) }
else          { Out-Body  'GameDir    : 解決できず (-GameDir で指定してください)' }

Out-Head 'ログ台帳'
function Out-Ledger {
    param([string]$Name, [string]$Path)
    if ($Path -and (Test-Path -LiteralPath $Path)) {
        $fi = Get-Item -LiteralPath $Path
        Out-Body ('  [OK] {0,-30} {1,10} KB  最終更新 {2}' -f $Name, ([Math]::Round($fi.Length / 1KB, 1)), $fi.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))
    }
    else {
        $p = $Path
        if (-not $p) { $p = '(GameDir 未解決)' }
        Out-Body ('  [--] {0,-30} 見つかりません: {1}' -f $Name, $p)
    }
}
Out-Ledger -Name 'EndKnot-Timeline.log' -Path $timelinePath
Out-Ledger -Name 'log.html (ライブ)' -Path $logHtmlPath
if ($dumpHtmls.Count -eq 0) {
    Out-Body ('  [--] 対象日の dump フォルダなし ({0}\{1}_*)' -f $HealthDir, $DateStr)
}
foreach ($d in $dumpHtmls) { Out-Ledger -Name $d.Name -Path $d.Path }

# ---------------------------------------------------------------- log.html パース
# 各エントリ: <div class='log-entry LEVEL'> 本文 </div>。本文は [HH:mm:ss][tag]... 形式。
# 日付は載っていないので、アンカー時刻(dump=フォルダ名 / ライブ=最終更新)から逆順に
# 日跨ぎを検出して絶対時刻を復元する。
$EntryRegex = [regex]::new("<div class='log-entry (\w+)'>\s*(.*?)\s*</div>",
    [System.Text.RegularExpressions.RegexOptions]::Singleline)

function Parse-LogHtml {
    # 戻り値: @{ Time(datetime); Level; Text } のリスト (時刻復元できた行のみ)
    param([string]$Path, [datetime]$Anchor)
    $result = New-Object 'System.Collections.Generic.List[object]'
    $raw = New-Object 'System.Collections.Generic.List[object]'
    $html = ''
    try { $html = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8) }
    catch { return $result }
    foreach ($m in $EntryRegex.Matches($html)) {
        $level = $m.Groups[1].Value.ToLowerInvariant()
        $text = $m.Groups[2].Value -replace '<br\s*/?>', ' '
        $text = ($text -replace '\s+', ' ').Trim()
        try { $text = [System.Net.WebUtility]::HtmlDecode($text) } catch { }
        $tm = [regex]::Match($text, '^\[(\d{2}):(\d{2}):(\d{2})\]')
        if (-not $tm.Success) { continue }
        $tod = New-Object TimeSpan(([int]$tm.Groups[1].Value), ([int]$tm.Groups[2].Value), ([int]$tm.Groups[3].Value))
        $raw.Add([pscustomobject]@{ Tod = $tod; Level = $level; Text = $text; Time = [datetime]::MinValue })
    }
    # 逆順ウォークで日付を割り当てる (アンカーより数分先までは同日として許容)
    $curDate = $Anchor.Date
    $prevTime = $Anchor.AddMinutes(10)
    for ($i = $raw.Count - 1; $i -ge 0; $i--) {
        $t = $curDate + $raw[$i].Tod
        while ($t -gt $prevTime) { $curDate = $curDate.AddDays(-1); $t = $curDate + $raw[$i].Tod }
        $raw[$i].Time = $t
        $prevTime = $t
    }
    foreach ($e in $raw) { $result.Add($e) }
    return $result
}

# dump は同一プロセスの累積スナップショット(古い dump ⊂ 新しい dump ⊂ ライブ)なので、
# 「時刻|レベル|本文|ファイル内での同文出現回数」をキーに重複除去する
# (同一秒に同じエラーが連発するフラッド行は正しく件数を保ったまま、スナップショット間の重複だけ消える)。
$seenKeys = New-Object 'System.Collections.Generic.HashSet[string]'
$allEntries = New-Object 'System.Collections.Generic.List[object]'

$htmlSources = New-Object 'System.Collections.Generic.List[object]'
foreach ($d in $dumpHtmls) { $htmlSources.Add($d) }
if ($logHtmlPath -and (Test-Path -LiteralPath $logHtmlPath)) {
    $anchor = (Get-Item -LiteralPath $logHtmlPath).LastWriteTime
    $htmlSources.Add([pscustomobject]@{ Path = $logHtmlPath; Anchor = $anchor; Name = 'log.html (ライブ)' })
}

foreach ($src in $htmlSources) {
    $entries = Parse-LogHtml -Path $src.Path -Anchor $src.Anchor
    $localCount = @{}
    foreach ($e in $entries) {
        if ($e.Time.Date -ne $TargetDate) { continue }
        $baseKey = '{0:yyyy-MM-dd HH:mm:ss}|{1}|{2}' -f $e.Time, $e.Level, $e.Text
        if (-not $localCount.ContainsKey($baseKey)) { $localCount[$baseKey] = 0 }
        $localCount[$baseKey]++
        $key = '{0}#{1}' -f $baseKey, $localCount[$baseKey]
        if (-not $seenKeys.Add($key)) { continue }
        $allEntries.Add($e)
    }
}
$allEntries = @($allEntries | Sort-Object Time)

# ---------------------------------------------------------------- Timeline パース (§1)
# 全行 "sid=<unix> " プレフィックス。GAMESTART/GAMEEND には t= が無いので、
# 同一セッション内の直前アンカー(SESSION/DC/ANOM live の t=)から推定し「~」を付けて表す。
$tlEvents = New-Object 'System.Collections.Generic.List[object]'   # Time / Approx / Kind / Body / Sid
if (Test-Path -LiteralPath $timelinePath) {
    $tlLines = @()
    try { $tlLines = [System.IO.File]::ReadAllLines($timelinePath, [System.Text.Encoding]::UTF8) }
    catch { }
    $lastAnchor = @{}   # sid -> unix
    foreach ($line in $tlLines) {
        $lm = [regex]::Match($line, '^sid=(\d+)\s+(\S+)\s*(.*)$')
        if (-not $lm.Success) { continue }
        $sid = [long]$lm.Groups[1].Value
        $kind = $lm.Groups[2].Value
        $body = $lm.Groups[3].Value
        if (-not $lastAnchor.ContainsKey($sid)) { $lastAnchor[$sid] = $sid }

        $unix = -1
        $approx = $false
        $tm = [regex]::Match($line, '\bt=(\d{9,})\b')
        if ($tm.Success) { $unix = [long]$tm.Groups[1].Value }
        elseif ($kind -eq 'SESSION') { $unix = $sid }
        else { $unix = $lastAnchor[$sid]; $approx = $true }
        if (-not $approx) { $lastAnchor[$sid] = $unix }

        $time = [DateTimeOffset]::FromUnixTimeSeconds($unix).ToLocalTime().DateTime
        $tlEvents.Add([pscustomobject]@{ Time = $time; Approx = $approx; Kind = $kind; Body = $body; Sid = $sid; Unix = $unix })
    }
}

# GAMEEND の推定時刻を「対応する GAMESTART 推定 + dur」に補正し、試合区間リストを作る
$games = New-Object 'System.Collections.Generic.List[object]'   # No / Start / End / Gm / Players / Winner / Open
$openGame = $null
$lastGameEnd = $null
foreach ($ev in $tlEvents) {
    if ($ev.Kind -eq 'GAMESTART') {
        $gm = '?'; $players = '?'
        $m = [regex]::Match($ev.Body, 'gm=(\S+)\s+players=(\d+)')
        if ($m.Success) { $gm = $m.Groups[1].Value; $players = $m.Groups[2].Value }
        $openGame = [pscustomobject]@{
            No = 0; Start = $ev.Time; End = [datetime]::MinValue
            Gm = $gm; Players = $players; Winner = ''; Open = $true; Sid = $ev.Sid
        }
        $games.Add($openGame)
    }
    elseif ($ev.Kind -eq 'GAMEEND') {
        if ($openGame -and $openGame.Open -and $openGame.Sid -eq $ev.Sid) {
            $dur = 0
            $dm = [regex]::Match($ev.Body, '\bdur=(\d+)\b')
            if ($dm.Success) { $dur = [int]$dm.Groups[1].Value }
            $openGame.End = $openGame.Start.AddSeconds($dur)
            $wm = [regex]::Match($ev.Body, '\bwinner=(\S+)')
            if ($wm.Success) { $openGame.Winner = $wm.Groups[1].Value }
            # GAMEEND 行の表示時刻も推定終了時刻に補正
            $ev.Time = $openGame.End
            $lastGameEnd = [pscustomobject]@{ Sid = $ev.Sid; Time = $openGame.End }
            $openGame.Open = $false
            $openGame = $null
        }
    }
    elseif ($ev.Kind -eq 'ANOM' -and $ev.Approx) {
        # GAMEEND 直後の "ANOM game" 行は、補正済みの試合終了時刻を引き継ぐ
        if ($lastGameEnd -and $lastGameEnd.Sid -eq $ev.Sid -and $ev.Body.StartsWith('game')) {
            $ev.Time = $lastGameEnd.Time
        }
    }
    elseif (-not $ev.Approx) {
        # 実時刻アンカー(DC 等)が来たら、終了記録の無い進行中試合の上限として使う
        if ($openGame -and $openGame.Open -and $openGame.Sid -eq $ev.Sid -and $ev.Time -gt $openGame.Start) {
            $openGame.End = $ev.Time
        }
    }
}

# 対象日のイベント/試合に絞る
$dayEvents = @($tlEvents | Where-Object { $_.Time.Date -eq $TargetDate })
$dayGames = @($games | Where-Object { $_.Start.Date -eq $TargetDate })
$no = 0
foreach ($g in $dayGames) { $no++; $g.No = $no }

$MatchSlackSec = 120
function Get-GameLabel {
    # ある時刻がどの試合中かを返す(±slack 秒の緩衝つき)。試合外は「ロビー/メニュー中」。
    param([datetime]$t)
    foreach ($g in $dayGames) {
        $end = $g.End
        if ($end -eq [datetime]::MinValue) { $end = $g.Start.AddHours(1) }
        if ($t -ge $g.Start.AddSeconds(-$MatchSlackSec) -and $t -le $end.AddSeconds($MatchSlackSec)) {
            return ('試合#{0} ({1} {2:HH:mm}頃)' -f $g.No, $g.Gm, $g.Start)
        }
    }
    return 'ロビー/メニュー中'
}

# ---------------------------------------------------------------- §1 試合タイムライン
Out-Head '§1 試合タイムライン (EndKnot-Timeline.log)'
if (-not (Test-Path -LiteralPath $timelinePath)) {
    Out-Body '  Timeline ログが見つからないため、このセクションは出せません。'
}
elseif ($dayEvents.Count -eq 0) {
    Out-Body ('  対象日 {0} の Timeline 記録はありません。' -f $DateStr)
}
else {
    $gameEndCount = 0; $anomCount = 0; $dcCount = 0
    foreach ($ev in $dayEvents) {
        $timeStr = $ev.Time.ToString('HH:mm:ss')
        if ($ev.Approx -and $ev.Kind -ne 'GAMEEND') { $timeStr = '~' + $timeStr } # GAMEEND は dur 補正済み
        elseif ($ev.Kind -eq 'GAMEEND') { $timeStr = '~' + $timeStr }
        elseif ($ev.Kind -eq 'GAMESTART') { $timeStr = '~' + $timeStr }

        $mark = '  '
        $desc = ''
        switch ($ev.Kind) {
            'SESSION' {
                $ver = ''
                $vm = [regex]::Match($ev.Body, 'ver=(\S+)')
                if ($vm.Success) { $ver = $vm.Groups[1].Value }
                $desc = ('セッション開始 (ver {0})' -f $ver)
            }
            'GAMESTART' {
                $g = $dayGames | Where-Object { $_.Sid -eq $ev.Sid -and ([Math]::Abs(($_.Start - $ev.Time).TotalSeconds) -lt 1) } | Select-Object -First 1
                $noStr = ''
                if ($g) { $noStr = ('試合#{0} ' -f $g.No) }
                $gm = '?'; $players = '?'
                $m = [regex]::Match($ev.Body, 'gm=(\S+)\s+players=(\d+)')
                if ($m.Success) { $gm = $m.Groups[1].Value; $players = $m.Groups[2].Value }
                $roles = ''
                $rm = [regex]::Match($ev.Body, 'roles=\[([^\]]*)\]')
                if ($rm.Success) { $roles = '  roles: ' + (Cap-Text $rm.Groups[1].Value 120) }
                $desc = ('{0}開始 gm={1} players={2}{3}' -f $noStr, $gm, $players, $roles)
            }
            'GAMEEND' {
                $gameEndCount++
                $winner = '?'; $dur = '?'; $meetings = '?'; $flags = ''; $exTags = ''
                $wm = [regex]::Match($ev.Body, '\bwinner=(\S+)')
                if ($wm.Success) { $winner = $wm.Groups[1].Value }
                $dm = [regex]::Match($ev.Body, '\bdur=(\d+)\b')
                if ($dm.Success) { $dur = $dm.Groups[1].Value }
                $mm = [regex]::Match($ev.Body, '\bmeetings=(\d+)\b')
                if ($mm.Success) { $meetings = $mm.Groups[1].Value }
                $fm = [regex]::Match($ev.Body, 'flags=\[([^\]]*)\]')
                if ($fm.Success) { $flags = $fm.Groups[1].Value.Trim() }
                $em = [regex]::Match($ev.Body, 'exTags=\[([^\]]*)\]')
                if ($em.Success) { $exTags = $em.Groups[1].Value.Trim() }
                $desc = ('終了 winner={0} dur={1}s meetings={2}' -f $winner, $dur, $meetings)
                if ($flags) { $mark = $C_RED; $desc += ('  flags=[{0}]' -f $flags) }
                else        { $mark = $C_GRN }
                if ($exTags) { $desc += ('  exTags=[{0}]' -f (Cap-Text $exTags 100)) }
            }
            'DC' {
                $dcCount++
                $reason = '?'; $intentional = ''
                $rm = [regex]::Match($ev.Body, 'reason=(\S+)')
                if ($rm.Success) { $reason = $rm.Groups[1].Value }
                $im = [regex]::Match($ev.Body, 'intentional=(\d)')
                if ($im.Success -and $im.Groups[1].Value -eq '0') { $intentional = ' (意図しない切断)'; $mark = $C_YEL }
                $sm = [regex]::Match($ev.Body, 'str="([^"]*)"')
                $str = ''
                if ($sm.Success -and $sm.Groups[1].Value.Trim().Length -gt 0) { $str = '  ' + (Cap-Text $sm.Groups[1].Value.Trim() 80) }
                $desc = ('切断 reason={0}{1}{2}' -f $reason, $intentional, $str)
            }
            'ANOM' {
                $anomCount++
                $mark = $C_RED
                $desc = ('異常検知 {0}' -f (Cap-Text $ev.Body 160))
            }
            default {
                $mark = $C_YEL
                $desc = ('{0} {1}' -f $ev.Kind, (Cap-Text $ev.Body 160))
            }
        }
        Out-Body ('  {0} {1,-9} {2}' -f $mark, $timeStr, $desc)
    }
    Out-Body ''
    Out-Body ('  試合 {0} 件 (完了 {1} / 終了記録なし {2}) / 異常検知 {3} 件 / 切断 {4} 件' -f `
        $dayGames.Count, $gameEndCount, (@($dayGames | Where-Object { $_.Open }).Count), $anomCount, $dcCount)
    Out-Body '  ※「~」付き時刻は Timeline のアンカーからの推定値です。'
}

# ---------------------------------------------------------------- §2 エラー/例外ダイジェスト
Out-Head '§2 エラー/例外ダイジェスト (log.html: ライブ + dump)'
$errorEntries = @($allEntries | Where-Object { $_.Level -eq 'error' -or $_.Level -eq 'fatal' })
if ($htmlSources.Count -eq 0) {
    Out-Body '  log.html が1つも見つからないため、このセクションは出せません。'
}
elseif ($errorEntries.Count -eq 0) {
    Out-Body ('  {0} 対象日のエラー/例外行はありません。' -f $C_OK)
}
else {
    # タグ(本文先頭の最後の [..] グループ)別に集計
    $tagMap = @{}
    foreach ($e in $errorEntries) {
        $tag = '(タグなし)'
        $gm = [regex]::Match($e.Text, '^\[\d{2}:\d{2}:\d{2}\]((?:\[[^\]]*\])+)')
        if ($gm.Success) {
            $groups = [regex]::Matches($gm.Groups[1].Value, '\[([^\]]*)\]')
            if ($groups.Count -gt 0) { $tag = $groups[$groups.Count - 1].Groups[1].Value }
        }
        if (-not $tagMap.ContainsKey($tag)) {
            $tagMap[$tag] = [pscustomobject]@{
                Count = 0; First = [datetime]::MaxValue; Last = [datetime]::MinValue
                Fatal = 0
                Samples = New-Object 'System.Collections.Generic.List[string]'
                Where = @{}
            }
        }
        $info = $tagMap[$tag]
        $info.Count++
        if ($e.Level -eq 'fatal') { $info.Fatal++ }
        if ($e.Time -lt $info.First) { $info.First = $e.Time }
        if ($e.Time -gt $info.Last) { $info.Last = $e.Time }
        if ($info.Samples.Count -lt $MaxRep) { $info.Samples.Add(('{0} {1}' -f $e.Time.ToString('HH:mm:ss'), (Cap-Text $e.Text 200))) }
        $where = Get-GameLabel -t $e.Time
        if (-not $info.Where.ContainsKey($where)) { $info.Where[$where] = 0 }
        $info.Where[$where]++
    }

    Out-Body ('  エラー/例外 {0} 件 / {1} タグ (件数順)' -f $errorEntries.Count, $tagMap.Count)
    foreach ($tag in ($tagMap.Keys | Sort-Object { $tagMap[$_].Count } -Descending)) {
        $info = $tagMap[$tag]
        $mark = $C_YEL
        if ($info.Fatal -gt 0) { $mark = $C_RED }
        Out-Body ''
        Out-Body ('  {0} [{1}] {2}件{3}  初出 {4} / 最終 {5}' -f $mark, $tag, $info.Count, `
            $(if ($info.Fatal -gt 0) { " (うち fatal $($info.Fatal))" } else { '' }), `
            $info.First.ToString('HH:mm:ss'), $info.Last.ToString('HH:mm:ss'))
        $whereStr = ($info.Where.Keys | Sort-Object { $info.Where[$_] } -Descending | ForEach-Object { '{0} x{1}' -f $_, $info.Where[$_] }) -join ', '
        Out-Body ('      発生箇所: {0}' -f $whereStr)
        foreach ($s in $info.Samples) { Out-Body ('      {0}' -f $s) }
        if ($info.Count -gt $info.Samples.Count) {
            Out-Body ('      ...ほか {0} 件 (-Detail で代表行を10行に拡張)' -f ($info.Count - $info.Samples.Count))
        }
    }
}

# ---------------------------------------------------------------- §3 キーワード発言抽出
Out-Head '§3 キーワード発言抽出 (ReceiveChat / SendChat)'
$keywordList = @($Keywords.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_.Length -gt 0 })
Out-Body ('  キーワード: {0}' -f ($keywordList -join ', '))

$chatHits = New-Object 'System.Collections.Generic.List[object]'
$chatTotal = 0
foreach ($e in $allEntries) {
    $speaker = $null; $msg = $null
    $rm = [regex]::Match($e.Text, '^\[\d{2}:\d{2}:\d{2}\]\[ReceiveChat\]\(([^)]*)\)\s(.*)$')
    if ($rm.Success) {
        # RPC.cs 形式: (friendCode|puid) 名前(役職): 本文
        $rest = $rm.Groups[2].Value
        $idx = $rest.IndexOf('): ')
        if ($idx -ge 0) {
            $who = $rest.Substring(0, $idx + 1)
            $msg = $rest.Substring($idx + 3)
        }
        else {
            $idx = $rest.IndexOf(': ')
            if ($idx -lt 0) { continue }
            $who = $rest.Substring(0, $idx)
            $msg = $rest.Substring($idx + 2)
        }
        $speaker = ($who -replace '\([^()]*\)$', '').Trim()
    }
    else {
        $sm = [regex]::Match($e.Text, '^\[\d{2}:\d{2}:\d{2}\]\[SendChat\](.*)$')
        if (-not $sm.Success) { continue }
        $speaker = 'ホスト(自分)'
        $msg = $sm.Groups[1].Value.Trim()
    }
    if ($null -eq $msg) { continue }
    if ($msg.StartsWith('/')) { continue }   # コマンド行は除外
    $chatTotal++
    $hit = $false
    foreach ($kw in $keywordList) {
        if ($msg.IndexOf($kw, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { $hit = $true; break }
    }
    if (-not $hit) { continue }
    $chatHits.Add([pscustomobject]@{ Time = $e.Time; Speaker = $speaker; Msg = $msg })
}

if ($htmlSources.Count -eq 0) {
    Out-Body '  log.html が1つも見つからないため、このセクションは出せません。'
}
elseif ($chatHits.Count -eq 0) {
    Out-Body ('  {0} 走査したチャット {1} 件の中にキーワード一致はありませんでした。' -f $C_OK, $chatTotal)
}
else {
    Out-Body ('  走査したチャット {0} 件中、一致 {1} 件:' -f $chatTotal, $chatHits.Count)
    Out-Body ''
    foreach ($h in $chatHits) {
        Out-Body ('  {0} {1,-9} {2}: {3}' -f $C_YEL, $h.Time.ToString('HH:mm:ss'), $h.Speaker, $h.Msg)
        Out-Body ('      ({0})' -f (Get-GameLabel -t $h.Time))
    }
}

Close-Fence

# ---------------------------------------------------------------- Markdown 保存
Write-Host ''
if ($NoSave) {
    Write-Host '(-NoSave 指定のため Markdown は保存しません)'
}
else {
    $mdPath = Join-Path $HealthDir ('StreamReport-{0}.md' -f $DateStr)
    try {
        if (-not (Test-Path -LiteralPath $HealthDir)) {
            New-Item -ItemType Directory -Force -Path $HealthDir | Out-Null
        }
        $utf8Bom = New-Object System.Text.UTF8Encoding($true)
        [System.IO.File]::WriteAllLines($mdPath, $script:Md, $utf8Bom)
        Write-Host ('Markdown 保存: {0}' -f $mdPath)
    }
    catch {
        Write-Host ('Markdown の保存に失敗しました: {0}' -f $_.Exception.Message)
    }
}

exit 0
