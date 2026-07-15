<#
  End K not 外部ウォッチドッグ  —  24時間ソーク／配信用の「番犬」
  --------------------------------------------------------------------
  役割: Among Us(ホスト) がクラッシュ／ハング／終了したら自動で起動し直す。
        DLL の外で動く別プロセスなので、アンチチートには一切触れない。

  仕組み:
    ・End K not の HealthLog が <デスクトップ>\EndKnot_Logs\EndKnot-Health.log に
      5秒ごとに心拍(HB行)を追記している。心拍は Unity のメインループで回るので、
      プロセスがクラッシュしても「固まった(ハング)」場合でも追記が止まる。
    ・番犬はそのファイルの最終更新時刻を見張り、途切れたら異常と判断:
        - AU プロセスが消えていれば → 落ちた/終了 → 起動し直す
        - AU は生きているのに心拍が止まっていれば → ハング → 強制終了して起動し直す
    ・(再)起動後は End K not の自動ホスト＋AutoRehost が部屋を立て直す。

  使い方:
    1. いつも通り Among Us を起動して部屋を立てる（End K not 入り）。
    2. 配信を開始する。
    3. この番犬を PowerShell で実行しっぱなしにする:
         powershell -ExecutionPolicy Bypass -File "…\tools\EndKnotWatchdog.ps1"
       （右クリック→「PowerShellで実行」でも可。ウィンドウは開いたままにしておくこと）
    4. 配信が終わったら番犬のウィンドウを閉じる。

  注意:
    ・このウィンドウを閉じると番犬も止まる（MVP仕様）。恒久運用ならタスクスケジューラ化。
    ・クラッシュが連発する場合は暴走しないよう1時間あたりの再起動回数に上限を設けてある。
#>

param(
    [int]$RunSeconds = 0   # 0 = 無限 (通常運用)。>0 でその秒数後に自動終了 (テスト/ドライラン用)。
)

# =========================== 設定 (必要なら書き換え) ===========================
# AU のプロセス名（.exe は付けない）
$ProcessName          = 'Among Us'

# HealthLog の出力先。End K not と同じ算出方法（Desktop 特別フォルダ）なので通常このままでOK。
$HealthDir            = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'EndKnot_Logs'
$HealthLog            = Join-Path $HealthDir 'EndKnot-Health.log'

# 心拍(5秒毎)がこの秒数途切れたら「ハング/クラッシュ」とみなす。GCやロードの一時停止で誤検知しない余裕を持たせる。
$StaleSeconds         = 90

# 番犬の巡回間隔（秒）
$CheckIntervalSec     = 20

# (再)起動直後、この秒数は監視を猶予する（AU 起動→ホスト→心拍開始まで待つ）
$BootGraceSec         = 150

# 連続再起動の最短間隔（秒）。短時間の二重発火を防ぐ。
$RelaunchCooldownSec  = 120

# 1時間あたりの再起動上限。超えたら暴走とみなして自動再起動を一時停止し、人の対応を待つ。
$MaxRelaunchPerHour   = 12

# ブート死(起動したのに心拍を一度も出さずにプロセス消失=EOS 認証切れ等の「再起動では直らない」状態)が
# この回数連続したら、連射再起動をやめて長い間隔の再試行だけに切り替える(ホールドモード)。
$BootDeathHoldThreshold = 3

# ホールドモード中の再試行間隔(秒)。人手で EOS 認証が復旧された場合に自動で拾い直すための保険。
$BootDeathRetrySec      = 1800

# ブート死ホールドの定期再試行の前に Epic Games Launcher をフル再起動して EOS トークンの
# 自動リフレッシュを試みるか (実験枠)。EpicWebHelper の再起動だけでは回復しないことは実測済み。
$RestartEglOnBootDeathRetry = $true

# 回線死活プローブ: 異常時(再起動する前)に回線が生きているかを TCP 443 接続で確認する。
# 回線が死んでいる間は AU を起動しても必ずブート死する (EGL 自体が「接続エラー」になる実測あり) ので、
# 再起動予算(12/h)を燃やさず回復を待ち、回線が戻った瞬間に即座に立て直す。
$NetProbeEnabled = $true
$NetProbeHosts   = @('1.1.1.1', '8.8.8.8')

# ゾンビ検知: プロセス生存+心拍新鮮でも、部屋が立たないまま state=Menu がこの秒数続いたら
# 「部屋立てが詰まったゾンビ」とみなして強制再起動する。0 で無効。
# (認証死ポップアップが起動60秒以内に出ると mod 側の quit-storm ガードで Escalate されず
#  メニューに座り続ける等、mtime 鮮度だけでは拾えない実在の詰まり経路を塞ぐ。)
$ZombieMenuSeconds      = 600

# AU が最初から起動していない場合に番犬が起動するか
$LaunchIfNotRunning   = $true

# 番犬が AU を(再)起動した時、EHR 側に「前回設定で自動ホストして」と依頼するか (マーカーファイルを置く)
$AutoHostOnRelaunch   = $true

# --- 起動方法（上から順に試す。通常は空のままで自動検出に任せてよい）---
# Epic の起動URL。空なら Epic マニフェストから自動検出する。
#   手動で入れる場合の例: 'com.epicgames.launcher://apps/xxxxxxxx?action=launch&silent=true'
$EpicLaunchUrl        = ''
# 上記が無理な時に使う AU 実行ファイルの直接パス（例: 'C:\Program Files\Epic Games\AmongUs\Among Us.exe'）
$AuExePathOverride    = ''
# ==============================================================================


$ErrorActionPreference = 'Continue'
$WatchLog = Join-Path $HealthDir 'EndKnot-Watchdog.log'
$MarkerFile = Join-Path $HealthDir 'autohost_request.flag'
# ゲーム内トグル/正常終了フック（EndKnot 本体）がこのファイルを置くと、番犬は次の巡回で自滅する。
$StopFlag = Join-Path $HealthDir 'watchdog-stop.flag'
# AutoRestart が認証/回線死からの復帰でプロセスを終了する直前に置く「意図的な再起動要求」。
# 番犬自身のブートループ抑止（grace/cooldown）の対象外なので、これを見たら窓を無視して即立て直す。
$RestartFlag = Join-Path $HealthDir 'restart_request.flag'
# 認証死(EOS トークン失効)起因の再起動でだけ AutoRestart が置く「起動前に EGL を再認証して」の依頼。
# プレーン再起動はトークン未リフレッシュで必ずブート死し 150s の起動猶予を空費するため(BUG-17)、
# Start-Au はこの旗を見たら起動の前に Restart-EpicLauncher を先行実行して 1回目から復帰させる。
$EglRefreshFlag = Join-Path $HealthDir 'egl_refresh_request.flag'
$script:RelaunchTimes = New-Object System.Collections.Generic.List[datetime]
$script:LastRelaunch  = [datetime]::MinValue
$script:GraceUntil    = [datetime]::MinValue
$script:CapturedExe   = $null
$script:LastOkFileLog = [datetime]::MinValue
$script:StartedAt     = Get-Date
# ブート死ループ検知: 直近の(再)起動が心拍を出したかを launch 1回につき1回だけ判定するための状態。
$script:BootDeaths    = 0
$script:LaunchJudged  = $true
$script:LastHoldLog   = [datetime]::MinValue
# ゾンビ検知: state=Menu の継続開始時刻 (Menu 以外の state を見たらリセット)。
$script:MenuSince     = [datetime]::MinValue
# 回線死活: 直前の巡回で回線死を観測していたか (復帰エッジ検出用) と、net-down ログの間引き。
$script:NetWasDown    = $false
$script:LastNetLog    = [datetime]::MinValue
# WER ダンプ待機: WerFault がこの AU のクラッシュダンプを書いている間の kill 保留開始時刻。
$script:WerWaitSince  = [datetime]::MinValue

function Write-WatchLog {
    param([string]$Msg, [string]$Color = 'Gray', [switch]$ConsoleOnly)
    $line = "[{0:yyyy-MM-dd HH:mm:ss}] {1}" -f (Get-Date), $Msg
    Write-Host $line -ForegroundColor $Color
    if (-not $ConsoleOnly) { try { Add-Content -Path $WatchLog -Value $line -Encoding utf8 } catch { } }
}

function Get-AuProcess {
    $p = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue
    if ($p) { return @($p)[0] }
    return $null
}

# --- 死因識別 (2026-07-15) ---------------------------------------------------
# 「プロセスが消えた」だけでは crash / clean exit / 外部kill を区別できない。番犬の
# 「異常: AU プロセスが見つかりません（クラッシュ/終了）」は文言どおり両方を含むため、
# 2026-07-15 22:58 の無音死ではイベントログもダンプも無く容疑者を切れなかった。
# ExitCode が読めればこれが1発で割れる: 0 = 行儀よく終了 (Application.Quit 等 /
# AutoRestart は既存の番犬蘇生パスを Application.Quit で流用している = Modules/AutoRestart.cs:15)、
# 0xC0000005 等の非0 = 本物のクラッシュ。
# ⚠️ ExitCode は「プロセスが消えてから Get-Process」では絶対に読めない。生きているうちに
# 掴んだ Process オブジェクト (ハンドル) を保持し続ける必要がある。$p.Handle を一度読むと
# .NET がハンドルを保持し、終了後も ExitCode を読めるようになる。
$script:AuProcHandle = $null
$script:AuProcPid    = 0

function Register-AuProcessHandle {
    param($Proc)
    if (-not $Proc) { return }
    if ($script:AuProcHandle -and $script:AuProcPid -eq $Proc.Id) { return }  # 既に同一PIDを掴み済み
    try {
        $null = $Proc.Handle   # ハンドルを実体化して保持 (これをやらないと終了後 ExitCode が読めない)
        $script:AuProcHandle = $Proc
        $script:AuProcPid    = $Proc.Id
    } catch {
        $script:AuProcHandle = $null
        $script:AuProcPid    = 0
    }
}

# プロセス消失時に「どう死んだか」を1行で返す。読めない場合も理由を明示する。
function Get-AuExitDiagnosis {
    if (-not $script:AuProcHandle) { return "ExitCode=不明 (プロセスハンドル未保持 — 番犬起動前から動いていた等)" }
    try {
        if (-not $script:AuProcHandle.HasExited) { return "ExitCode=不明 (ハンドル上はまだ終了扱いでない)" }
        $code = $script:AuProcHandle.ExitCode
        $hex  = "0x{0:X8}" -f $code
        $verdict = switch ($code) {
            0          { "★正常終了 (clean exit) — クラッシュではない。Application.Quit / AutoRestart / 手動終了の系統" }
            -1073741819 { "★アクセス違反 (0xC0000005) — 本物のクラッシュ。coreclr/GameAssembly AV の既知系統" }
            -1073740791 { "★スタックバッファ破損 (0xC0000409)" }
            -1073740940 { "★ヒープ破損 (0xC0000374) — GCヒープ破損説と整合" }
            -1073741510 { "★Ctrl+C/外部終了 (0xC000013A)" }
            default    { "★異常終了 (要調査)" }
        }
        $exited = try { $script:AuProcHandle.ExitTime.ToString('HH:mm:ss') } catch { "?" }
        return "ExitCode=$code ($hex) 終了時刻=$exited : $verdict"
    } catch {
        return "ExitCode=読み取り失敗: $($_.Exception.Message)"
    }
}

# --- 死亡時スナップショット (2026-07-15) -------------------------------------
# ログは放置すると消える: log.html は数時間でロール、Health.log は次セッション起動時に
# .prev へ退避され *その次* の起動で上書き消滅、Windows イベントログは保持期間で消える。
# 自動再起動が 63 秒で走る運用では「2回目の死」が1回目の証拠を消すため、死亡を検知した
# 番犬 (プロセス外で生き残っている唯一の観測者) がその場で全部を固めて逃がす。
function Save-CrashSnapshot {
    param([string]$Reason, [string]$ExitDiag)
    try {
        $stamp = (Get-Date).ToString('yyyy-MM-dd_HH.mm.ss')
        $dest  = Join-Path $HealthDir "CrashSnapshots\$stamp"
        New-Item -ItemType Directory -Force -Path $dest | Out-Null

        # 1) 死因サマリ (最初に書く — 以降が失敗しても死因だけは残る)
        $summary = @(
            "reason   : $Reason",
            "exit     : $ExitDiag",
            "pid      : $script:AuProcPid",
            "detected : $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))",
            "note     : ExitCode=0 は clean exit (AutoRestart/Application.Quit 系) で crash ではない。非0 なら本物のクラッシュ。"
        )
        $summary | Out-File -FilePath (Join-Path $dest 'CAUSE.txt') -Encoding utf8

        # 2) MOD 側ログ一式 (Health.prev = 死んだセッションの心拍。これが最重要)
        foreach ($f in @('EndKnot-Health.log','EndKnot-Health.prev.log','EndKnot-Timeline.log','EndKnot-Timeline.prev.log','EndKnot-Watchdog.log')) {
            $src = Join-Path $HealthDir $f
            if (Test-Path $src) { Copy-Item $src -Destination $dest -Force -ErrorAction SilentlyContinue }
        }

        # 3) 直近のセッション dump (log.html) — 死んだセッション本体のログ
        try {
            Get-ChildItem $HealthDir -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match '^\d{4}-\d{2}-\d{2}_\d{2}\.\d{2}\.\d{2}$' } |
                Sort-Object LastWriteTime | Select-Object -Last 2 |
                ForEach-Object { Copy-Item $_.FullName -Destination (Join-Path $dest $_.Name) -Recurse -Force -ErrorAction SilentlyContinue }
        } catch { }

        # 4) BepInEx 側 (起動時ログ + 未捕捉例外。MOD ログには出ない層がここに出る)
        if ($script:CapturedExe) {
            $bep = Join-Path (Split-Path $script:CapturedExe -Parent) 'BepInEx\LogOutput.log'
            if (Test-Path $bep) { Copy-Item $bep -Destination $dest -Force -ErrorAction SilentlyContinue }
        }

        # 5) Windows イベントログ (保持期間で消えるため、鮮度のあるうちに固める)
        try {
            $from = (Get-Date).AddMinutes(-10); $to = (Get-Date).AddMinutes(1)
            $ev = @()
            foreach ($ln in @('Application','System')) {
                try { $ev += Get-WinEvent -FilterHashtable @{LogName=$ln; StartTime=$from; EndTime=$to} -ErrorAction Stop } catch { }
            }
            if ($ev.Count -gt 0) {
                $ev | Sort-Object TimeCreated | Format-List TimeCreated, LogName, ProviderName, Id, LevelDisplayName, Message |
                    Out-File -FilePath (Join-Path $dest 'WinEvents.txt') -Encoding utf8
            } else {
                "(±10分に Application/System イベントは1件も無し = OS はクラッシュを記録していない)" |
                    Out-File -FilePath (Join-Path $dest 'WinEvents.txt') -Encoding utf8
            }
        } catch { }

        # 6) クラッシュダンプの在処 (コピーは巨大なので一覧だけ)
        try {
            $dmp = Join-Path $HealthDir 'CrashDumps'
            if (Test-Path $dmp) {
                Get-ChildItem $dmp -Filter *.dmp -ErrorAction SilentlyContinue | Sort-Object LastWriteTime |
                    Select-Object -Last 5 FullName, Length, LastWriteTime |
                    Format-List | Out-File -FilePath (Join-Path $dest 'CrashDumps.txt') -Encoding utf8
            }
        } catch { }

        # 7) 保持数の上限 (1件 約2〜3MB)。ブート死ループ等で溜まり続けて本物の証拠が埋もれる/ディスクを食うのを防ぐ。
        try {
            Get-ChildItem (Join-Path $HealthDir 'CrashSnapshots') -Directory -ErrorAction SilentlyContinue |
                Sort-Object Name | Select-Object -SkipLast 20 |
                ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
        } catch { }

        Write-WatchLog "死亡スナップショットを保全しました: $dest" 'Cyan'
    } catch {
        Write-WatchLog "スナップショット保全に失敗: $($_.Exception.Message)" 'Yellow'
    }
}

# Epic マニフェストから Among Us の起動URLを自動検出する。
function Find-EpicLaunchUrl {
    try {
        $manDir = Join-Path $env:ProgramData 'Epic\EpicGamesLauncher\Data\Manifests'
        if (-not (Test-Path $manDir)) { return $null }
        $items = Get-ChildItem -Path $manDir -Filter '*.item' -ErrorAction SilentlyContinue
        foreach ($f in $items) {
            try {
                $m = Get-Content -Path $f.FullName -Raw -Encoding utf8 | ConvertFrom-Json
            } catch { continue }
            $name = "$($m.DisplayName)"
            $loc  = "$($m.InstallLocation)"
            if ($name -like '*Among Us*' -or $loc -like '*AmongUs*' -or $loc -like '*Among Us*') {
                if ($m.AppName) {
                    $url = "com.epicgames.launcher://apps/$($m.AppName)?action=launch&silent=true"
                    Write-WatchLog "Epic マニフェスト検出: $name -> $url" 'DarkGray'
                    return $url
                }
            }
        }
    } catch { }
    return $null
}

# 起動手段を決める。戻り値: @{ Kind = 'url'|'exe'; Value = ... } または $null
function Resolve-LaunchTarget {
    if ($EpicLaunchUrl)      { return @{ Kind = 'url'; Value = $EpicLaunchUrl } }
    $auto = Find-EpicLaunchUrl
    if ($auto)               { return @{ Kind = 'url'; Value = $auto } }
    if ($AuExePathOverride -and (Test-Path $AuExePathOverride)) { return @{ Kind = 'exe'; Value = $AuExePathOverride } }
    if ($script:CapturedExe -and (Test-Path $script:CapturedExe)) { return @{ Kind = 'exe'; Value = $script:CapturedExe } }
    return $null
}

function Start-Au {
    $target = Resolve-LaunchTarget
    if (-not $target) {
        Write-WatchLog "起動手段が見つかりません。設定の EpicLaunchUrl か AuExePathOverride を入れてください。" 'Red'
        return $false
    }

    # 認証死起因の再起動要求 (egl_refresh_request.flag)。プレーン再起動は EOS トークン未リフレッシュで
    # 必ずブート死し、番犬が 150s の起動猶予を空費してから egl-restart に至る (BUG-17)。ゲーム側が
    # authfail のときだけ置くこの旗を見て、起動の前に EGL を再認証しておき 1回目から復帰させる。
    # 旗は 1回で消費する (以降の立て直しは通常のブート死ラダーに委ねる)。マスタースイッチ
    # $RestartEglOnBootDeathRetry を尊重し、古い旗(誤発火防止)は無視して消す。
    if (Test-Path $EglRefreshFlag) {
        try { $eglAge = ((Get-Date) - (Get-Item $EglRefreshFlag).LastWriteTime).TotalSeconds } catch { $eglAge = 99999 }
        try { Remove-Item $EglRefreshFlag -Force -ErrorAction SilentlyContinue } catch { }
        if ($eglAge -le 300) {
            if ($RestartEglOnBootDeathRetry) {
                Write-WatchLog "認証死起因の再起動要求 [egl-refresh]: プレーン再起動はブート死するため、起動前に EGL を再認証します。" 'Yellow'
                Restart-EpicLauncher
            } else {
                Write-WatchLog "egl-refresh 要求を検出しましたが RestartEglOnBootDeathRetry=無効のためスキップします。" 'DarkGray'
            }
        } else {
            Write-WatchLog ("egl-refresh フラグが古い({0:N0}s)ため無視して削除しました。" -f $eglAge) 'DarkGray'
        }
    }

    try {
        if ($AutoHostOnRelaunch) {
            try { Set-Content -Path $MarkerFile -Value ([DateTime]::Now.ToString('o')) -Encoding utf8; Write-WatchLog "自動ホスト依頼マーカー作成: $MarkerFile" 'DarkGray' } catch { }
        }
        if ($target.Kind -eq 'url') {
            Write-WatchLog "AU を起動します (Epic): $($target.Value)" 'Yellow'
            Start-Process $target.Value
        } else {
            Write-WatchLog "AU を起動します (exe): $($target.Value)" 'Yellow'
            Start-Process -FilePath $target.Value
        }
        $now = Get-Date
        $script:LastRelaunch = $now
        $script:GraceUntil   = $now.AddSeconds($BootGraceSec)
        $script:RelaunchTimes.Add($now)
        $script:LaunchJudged = $false   # この launch の生死(心拍が出たか)を後で1回だけ判定する
        $script:MenuSince    = [datetime]::MinValue   # ゾンビ検知タイマーは launch ごとに仕切り直す
        return $true
    } catch {
        Write-WatchLog "起動に失敗: $($_.Exception.Message)" 'Red'
        return $false
    }
}

# 回線が生きているか。TCP 443 への実接続で判定する (ICMP はブロックされうるので使わない)。
# 2ホストとも失敗した時だけ「回線死」と判定し、プローブ自体の誤検知で番犬を止めないようにする。
function Test-InternetAlive {
    if (-not $NetProbeEnabled) { return $true }
    foreach ($h in $NetProbeHosts) {
        $c = $null
        try {
            $c = New-Object System.Net.Sockets.TcpClient
            $ar = $c.BeginConnect($h, 443, $null, $null)
            if ($ar.AsyncWaitHandle.WaitOne(3000) -and $c.Connected) { $c.Close(); return $true }
        } catch { }
        finally { if ($c) { try { $c.Close() } catch { } } }
    }
    return $false
}

# Epic Games Launcher をフル再起動して EOS トークンの自動リフレッシュを試みる (実験枠)。
# 実測済みの前提: EpicWebHelper の再起動だけでは回復しない / EGL が「開いているだけ」では不十分。
# EGL 本体の再起動でリフレッシュトークンから静かに再認証されるかは未検証 — 効けば bootdeath-hold
# からの復帰が全自動になる。効かなくても現状 (手動サインイン待ち) より悪くはならない。
function Restart-EpicLauncher {
    try {
        $exe = $null
        $egl = Get-Process -Name 'EpicGamesLauncher' -ErrorAction SilentlyContinue
        if ($egl) {
            try { $exe = @($egl)[0].Path } catch { }
            Write-WatchLog "EGL 再起動 [egl-restart]: 既存の Epic Games Launcher を終了します..." 'Yellow'
            Stop-Process -Name 'EpicGamesLauncher' -Force -ErrorAction SilentlyContinue
            Stop-Process -Name 'EpicWebHelper' -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 5
        }
        if (-not $exe) {
            $pf86 = ${env:ProgramFiles(x86)}
            if ($pf86) {
                $cand = Join-Path $pf86 'Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe'
                if (Test-Path $cand) { $exe = $cand }
            }
        }
        if ($exe) {
            Write-WatchLog "EGL 再起動 [egl-restart]: $exe を -silent で起動し、再認証を待ちます (30s)..." 'Yellow'
            Start-Process -FilePath $exe -ArgumentList '-silent'
        } else {
            Write-WatchLog "EGL 再起動 [egl-restart]: exe が特定できないためプロトコル URL で起動します..." 'Yellow'
            Start-Process 'com.epicgames.launcher://'
        }
        # EGL の起動〜バックグラウンド再認証の猶予。この後すぐ AU を立てるので短すぎると意味がない。
        Start-Sleep -Seconds 30
    } catch {
        Write-WatchLog "EGL 再起動に失敗: $($_.Exception.Message)" 'Red'
    }
}

function Stop-Au {
    try {
        Stop-Process -Name $ProcessName -Force -ErrorAction SilentlyContinue
        Write-WatchLog "ハングした AU を強制終了しました。" 'Yellow'
        Start-Sleep -Seconds 3
    } catch {
        Write-WatchLog "強制終了に失敗: $($_.Exception.Message)" 'Red'
    }
}

# 直近1時間の再起動回数が上限内か
function Test-RelaunchAllowed {
    $cutoff = (Get-Date).AddHours(-1)
    for ($i = $script:RelaunchTimes.Count - 1; $i -ge 0; $i--) {
        if ($script:RelaunchTimes[$i] -lt $cutoff) { $script:RelaunchTimes.RemoveAt($i) }
    }
    return ($script:RelaunchTimes.Count -lt $MaxRelaunchPerHour)
}

# 心拍ログの鮮度。@{ Exists; AgeSec; Fresh; LastLine; LastWrite }
function Get-HealthStatus {
    $res = @{ Exists = $false; AgeSec = [double]::PositiveInfinity; Fresh = $false; LastLine = ''; LastWrite = [datetime]::MinValue }
    if (Test-Path $HealthLog) {
        $res.Exists = $true
        try {
            $lw = (Get-Item $HealthLog).LastWriteTime
            $res.LastWrite = $lw
            $res.AgeSec = ((Get-Date) - $lw).TotalSeconds
            $res.Fresh  = ($res.AgeSec -lt $StaleSeconds)
        } catch { }
        try { $res.LastLine = (Get-Content -Path $HealthLog -Tail 1 -Encoding utf8 -ErrorAction SilentlyContinue) } catch { }
    }
    return $res
}

# ============================== 本体 ==============================
# --- 単一インスタンス保証 ---
# ゲーム内ボタンや .cmd から重ねて起動されても、番犬は常に1匹だけ動くようにする（二重監視=二重再起動を防止）。
try { New-Item -ItemType Directory -Force -Path $HealthDir -ErrorAction SilentlyContinue | Out-Null } catch { }
$script:Mutex = New-Object System.Threading.Mutex($false, 'Local\EndKnotWatchdog')
if (-not $script:Mutex.WaitOne(0)) {
    Write-WatchLog "既に番犬が起動しているため、二重起動を避けて終了します。" 'Yellow'
    return
}
# 起動時に古い停止フラグを掃除（前回セッションの停止指示を引きずらない）。
try { if (Test-Path $StopFlag) { Remove-Item $StopFlag -Force -ErrorAction SilentlyContinue } } catch { }

Write-WatchLog "===== End K not ウォッチドッグ起動 =====" 'Cyan'
Write-WatchLog "監視ログ: $HealthLog" 'DarkGray'
Write-WatchLog "設定: stale=${StaleSeconds}s / 巡回=${CheckIntervalSec}s / 起動猶予=${BootGraceSec}s / 再起動上限=${MaxRelaunchPerHour}/h / ブート死ホールド=${BootDeathHoldThreshold}回→${BootDeathRetrySec}s間隔 / EGL再起動実験=$RestartEglOnBootDeathRetry / ゾンビ検知=${ZombieMenuSeconds}s" 'DarkGray'

# 起動時に AU が動いていれば、その実行ファイルパスをフォールバック用に捕捉。
$startProc = Get-AuProcess
if ($startProc) {
    try { $script:CapturedExe = $startProc.Path } catch { }
    if ($script:CapturedExe) { Write-WatchLog "起動中の AU を捕捉: $($script:CapturedExe)" 'DarkGray' }
    # 既に走っているので、起動直後の心拍未生成を誤検知しないよう軽く猶予。
    $script:GraceUntil = (Get-Date).AddSeconds(30)
} else {
    Write-WatchLog "AU は現在起動していません。" 'DarkGray'
    if ($LaunchIfNotRunning) { Start-Au | Out-Null }
}

while ($true) {
    Start-Sleep -Seconds $CheckIntervalSec

    # 停止指示チェックはループ最上段で（proc 消失→再起動 判定より先に読む。順序を守らないと
    # 「AU が消えた巡回」がフラグより先に発火して蘇生してしまう）。
    if (Test-Path $StopFlag) {
        try { Remove-Item $StopFlag -Force -ErrorAction SilentlyContinue } catch { }
        Write-WatchLog "停止指示を検出しました。番犬を終了します（ゲーム側からの停止 or 正常終了）。" 'Cyan'
        break
    }

    if ($RunSeconds -gt 0 -and ((Get-Date) - $script:StartedAt).TotalSeconds -ge $RunSeconds) {
        Write-WatchLog "テスト時間 ${RunSeconds}s 経過 — ウォッチドッグを終了します（通常運用時は RunSeconds=0 で無限）。" 'Cyan'
        break
    }

    $now      = Get-Date
    $proc     = Get-AuProcess
    $health   = Get-HealthStatus
    $inGrace  = ($now -lt $script:GraceUntil)

    # 動いている AU の exe パスを随時更新（次回フォールバック用）
    if ($proc -and -not $script:CapturedExe) { try { $script:CapturedExe = $proc.Path } catch { } }

    # 生きているうちにハンドルを掴んでおく (消えてからでは ExitCode を読めない = 死因識別の生命線)
    if ($proc) { Register-AuProcessHandle $proc }

    # --- ゲーム側からの意図的な再起動要求 (穴2) ---
    # AutoRestart が認証/回線死からの復帰でプロセスを終了する直前に restart_request.flag を置く。
    # これは番犬自身のブートループ抑止(grace/cooldown)の対象外の「明示要求」なので、それらの窓を無視して
    # 即座に立て直す。さもないと直前に番犬が(再)起動していた場合、grace(150s)/cooldown(120s)の窓に落ちて
    # 意図的終了後に一時的に無人死する空振りが起きる。per-hour 上限だけは最後の暴走ベルトとして残す。
    if (Test-Path $RestartFlag) {
        try { $reqAge = ((Get-Date) - (Get-Item $RestartFlag).LastWriteTime).TotalSeconds } catch { $reqAge = 99999 }
        if ($reqAge -gt 300) {
            # 古すぎる要求 = quit も hard-kill も着地しなかった異常系。誤発火防止に消して通常監視へ戻す。
            try { Remove-Item $RestartFlag -Force -ErrorAction SilentlyContinue } catch { }
            Write-WatchLog ("再起動要求フラグが古い({0:N0}s)ため無視して削除しました。" -f $reqAge) 'DarkGray'
        } elseif ($proc) {
            # プロセスがまだ生存 = 終了(quit)の着地待ち。フラグは残したまま待つ。
            Write-WatchLog "再起動要求を検出。ゲーム終了の着地を待っています... (proc=生存)" 'Yellow'
            continue
        } else {
            # プロセス消失を確認 = 意図的終了が完了 → grace/cooldown を飛ばして即立て直し。
            try { Remove-Item $RestartFlag -Force -ErrorAction SilentlyContinue } catch { }
            if (Test-RelaunchAllowed) {
                Write-WatchLog "ゲームからの再起動要求により即座に立て直します (grace/cooldown をスキップ)。" 'Cyan'
                Start-Au | Out-Null
            } else {
                Write-WatchLog "再起動要求ですが直近1時間の再起動が上限(${MaxRelaunchPerHour})に到達。暴走防止のため保留します。" 'Magenta'
            }
            continue
        }
    }

    # --- 正常表示 (コンソールには毎回、ファイルへは5分毎だけ書いてログを異常中心に保つ) ---
    if ($proc -and $health.Fresh) {
        # 心拍が出ている = ブートは成功している。ブート死ループの疑いを解除する。
        if ($script:BootDeaths -gt 0) { Write-WatchLog "心拍を確認。ブート死ループの疑いを解除します (連続カウントをリセット)。" 'Green' }
        $script:BootDeaths   = 0
        $script:LaunchJudged = $true

        # --- ゾンビ検知: 心拍は新鮮なのに部屋が立たないまま Menu に座り続けている ---
        # 最終行が state= を含む時だけ判定を更新する (ANOM 等 state 無し行でタイマーを壊さない)。
        if ($ZombieMenuSeconds -gt 0 -and $health.LastLine -match 'state=([A-Za-z]+)') {
            if ($Matches[1] -eq 'Menu') {
                if ($script:MenuSince -eq [datetime]::MinValue) { $script:MenuSince = $now }
                $menuFor = ($now - $script:MenuSince).TotalSeconds
                $cool    = ($now - $script:LastRelaunch).TotalSeconds
                if ($menuFor -ge $ZombieMenuSeconds -and -not $inGrace -and $cool -ge $RelaunchCooldownSec) {
                    if (Test-RelaunchAllowed) {
                        Write-WatchLog ("異常 [zombie]: 心拍は新鮮ですが state=Menu が {0:N0}s 継続 — 部屋立てが詰まっています。強制再起動します。" -f $menuFor) 'Red'
                        Stop-Au
                        Start-Au | Out-Null
                        continue
                    } else {
                        Write-WatchLog ("ゾンビ疑い (state=Menu {0:N0}s 継続) ですが、直近1時間の再起動が上限(${MaxRelaunchPerHour})のため保留します。" -f $menuFor) 'Magenta'
                    }
                }
            } else {
                $script:MenuSince = [datetime]::MinValue
            }
        }

        $summary = if ($health.LastLine) { $health.LastLine } else { '(心拍待ち)' }
        $okMsg = "OK  proc=生存 心拍={0:N0}s前  {1}" -f $health.AgeSec, $summary
        if (((Get-Date) - $script:LastOkFileLog).TotalSeconds -ge 300) {
            $script:LastOkFileLog = Get-Date
            Write-WatchLog $okMsg 'Green'
        } else {
            Write-WatchLog $okMsg 'Green' -ConsoleOnly
        }
        continue
    }

    # --- 起動猶予中はどんな状態でも待つ ---
    if ($inGrace) {
        $left = [int]($script:GraceUntil - $now).TotalSeconds
        # 心拍ログ未存在時は AgeSec が ∞ (PositiveInfinity) で [int] キャストが例外になるため文字列で逃がす。
        $ageStr = if ($health.Exists) { "$([int]$health.AgeSec)s" } else { 'なし' }
        Write-WatchLog "起動猶予中... 残り ${left}s (proc=$([bool]$proc) 心拍鮮度=$ageStr)" 'DarkGray'
        continue
    }

    # --- クールダウン中は待つ ---
    $sinceRelaunch = ($now - $script:LastRelaunch).TotalSeconds
    if ($sinceRelaunch -lt $RelaunchCooldownSec) {
        Write-WatchLog ("再起動クールダウン中... 経過 {0:N0}s / {1}s" -f $sinceRelaunch, $RelaunchCooldownSec) 'DarkGray'
        continue
    }

    # --- 異常判定 ---
    if (-not $proc) {
        # ExitCode を読んで crash / clean exit を切り分ける。「プロセスが見つかりません」だけでは
        # AutoRestart の Application.Quit も本物のクラッシュも同じ顔をする (2026-07-15 22:58 の教訓)。
        $exitDiag = Get-AuExitDiagnosis
        Write-WatchLog "異常: AU プロセスが見つかりません（クラッシュ/終了）。$exitDiag" 'Red'

        # 意図的な再起動要求 (restart_request.flag) が既に処理済みで来ている場合を除き、証拠を固める。
        # 再起動は 1 秒後に走り、その次の死が今回の証拠 (.prev / log.html / イベントログ) を上書きするため
        # ここで逃がさないと永久に失われる。
        Save-CrashSnapshot -Reason 'AU プロセス消失 (クラッシュ/終了)' -ExitDiag $exitDiag

        # ハンドルは使い切ったので解放 (次のプロセスを掴み直す)
        try { if ($script:AuProcHandle) { $script:AuProcHandle.Dispose() } } catch { }
        $script:AuProcHandle = $null
        $script:AuProcPid    = 0
    } elseif (-not $health.Fresh) {
        # WerFault がこの AU のクラッシュダンプを書いている間はプロセスがサスペンドされ心拍が止まり
        # 「ハング」に見える。ここで Stop-Au すると書き込み途中のダンプが破棄され、LocalDumps 計器が
        # 永遠に空振りする (2026-07-14 22:41 実証: coreclr AV → フルダンプ書込中に番犬 kill → CrashDumps 空)。
        # WerFault (この AU の PID 宛) が生きている間は kill を保留する。上限 900s で通常フローに復帰。
        $wer = $null
        try { $wer = Get-CimInstance Win32_Process -Filter "Name='WerFault.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -match ('-p\s+{0}(\s|$)' -f $proc.Id) } } catch { }
        if ($wer) {
            if ($script:WerWaitSince -eq [datetime]::MinValue) { $script:WerWaitSince = $now }
            $werWait = ($now - $script:WerWaitSince).TotalSeconds
            if ($werWait -lt 900) {
                Write-WatchLog ("WER がクラッシュダンプを書き込み中のため AU の kill を保留します (経過 {0:N0}s / 900s)。ダンプ完了後に通常の再起動フローへ戻ります。" -f $werWait) 'Yellow'
                continue
            }
            Write-WatchLog "WER ダンプ待機が 900s を超えました。通常のハング処理に進みます。" 'Red'
        } else {
            $script:WerWaitSince = [datetime]::MinValue
        }
        Write-WatchLog ("異常: AU は生存だが心拍が {0:N0}s 途切れています（ハング疑い）。" -f $health.AgeSec) 'Red'
        # ハングは番犬が Stop-Au で殺すため、殺す前の状態を固める (殺した後では心拍途絶の前後関係が読めない)。
        Save-CrashSnapshot -Reason ("ハング疑い (心拍 {0:N0}s 途絶・プロセスは生存)" -f $health.AgeSec) -ExitDiag 'N/A (まだ生存中 — この後 番犬が強制終了する)'
    }

    # --- ブート死判定 (launch 1回につき1回だけ) ---
    # 番犬が起動した AU が、心拍を一度も出さずにプロセス消失した = EOS 認証切れ等で起動中に自己 exit する
    # 「再起動しても直らない」状態のサイン。連射しても無駄打ちなので回数を数えてホールドに繋げる。
    if (-not $script:LaunchJudged -and -not $proc -and $script:LastRelaunch -ne [datetime]::MinValue) {
        $script:LaunchJudged = $true
        $hbSeen = $health.Exists -and ($health.LastWrite -gt $script:LastRelaunch)
        if ($hbSeen) {
            $script:BootDeaths = 0
        } else {
            $script:BootDeaths++
            Write-WatchLog ("ブート死を検出 [bootdeath]: 起動後に心拍が一度も出ないままプロセスが消えました (連続 {0} 回目 / しきい値 {1})。" -f $script:BootDeaths, $BootDeathHoldThreshold) 'Red'
            # ブート死の典型 (FATAL ERROR: Unable to get Epic Account ID / EGL 接続エラー) は
            # EGL の再起動で直る一過性のことが実測で多い。3連ホールドまで待たず、1回目から
            # 次の立て直しの前に EGL をリフレッシュして即復帰を狙う (2026-07-07 実機知見)。
            if ($RestartEglOnBootDeathRetry -and $script:BootDeaths -lt $BootDeathHoldThreshold) {
                Restart-EpicLauncher
            }
        }
    }

    # --- 回線死活ゲート: 回線が死んでいる間は再起動しても必ずブート死するので、予算を燃やさず待つ ---
    # (EGL 自体が「接続エラー: サーバーに接続できません」になる実測 2026-07-07。この状態では
    #  Epic の手動サインインし直しも効かない = 認証切れではなく到達不能。回復待ちが唯一の正解。)
    if (-not (Test-InternetAlive)) {
        $script:NetWasDown = $true
        $netMsg = "回線死を検出 [net-down]: インターネット (TCP 443) へ到達できません。再起動してもブート死するだけなので、回線回復を待ちます (AU/EGL の立て直しは保留)。"
        if (((Get-Date) - $script:LastNetLog).TotalSeconds -ge 300) {
            $script:LastNetLog = Get-Date
            Write-WatchLog $netMsg 'Magenta'
        } else {
            Write-WatchLog $netMsg 'Magenta' -ConsoleOnly
        }
        continue
    }
    if ($script:NetWasDown) {
        $script:NetWasDown = $false
        Write-WatchLog "回線復帰を検出 [net-recovered]: 立て直しを再開します。" 'Cyan'
        if ($script:BootDeaths -gt 0) {
            # 直前までのブート死は回線死で説明がつく (認証切れの証拠ではない) ので、ホールドに
            # 落とさず仕切り直す。EOS セッションを確実にリフレッシュするため EGL も再起動しておく。
            $script:BootDeaths = 0
            if ($RestartEglOnBootDeathRetry) { Restart-EpicLauncher }
        }
    }

    # --- ブート死ホールド: 連射をやめて長周期の再試行だけにする ---
    if ($script:BootDeaths -ge $BootDeathHoldThreshold) {
        $sinceLaunch = ($now - $script:LastRelaunch).TotalSeconds
        if ($sinceLaunch -lt $BootDeathRetrySec) {
            $leftSec = [int]($BootDeathRetrySec - $sinceLaunch)
            $holdMsg = "ブート死ループ検知 [bootdeath-hold]: 回線は生きているのにブート死が続く = EOS/Epic 認証切れの疑い。再起動では直りません。Epic Games Launcher をサインアウト→サインインし、AU を手動起動してメインメニュー到達を確認してください。自動再試行まで残り ${leftSec}s。"
            if (((Get-Date) - $script:LastHoldLog).TotalSeconds -ge 300) {
                $script:LastHoldLog = Get-Date
                Write-WatchLog $holdMsg 'Magenta'
            } else {
                Write-WatchLog $holdMsg 'Magenta' -ConsoleOnly
            }
            continue
        }
        Write-WatchLog "ブート死ホールド中の定期再試行を行います (認証が復旧していれば心拍確認で自動復帰します)。" 'Yellow'
        # 再試行の前に EGL をフル再起動して EOS トークンの自動リフレッシュを試みる (実験枠)。
        if ($RestartEglOnBootDeathRetry) { Restart-EpicLauncher }
    }

    # --- 再起動上限チェック ---
    if (-not (Test-RelaunchAllowed)) {
        Write-WatchLog "直近1時間の再起動が上限(${MaxRelaunchPerHour})に達しました。自動再起動を一時停止します（クラッシュ原因の調査が必要）。" 'Magenta'
        continue
    }

    # --- 復帰処理 ---
    if ($proc) { Stop-Au }   # ハング時は掃除してから
    Start-Au | Out-Null
}

# 正常終了（停止フラグ or テスト時間経過）。単一インスタンスの Mutex を解放する。
try { $script:Mutex.ReleaseMutex() } catch { }
try { $script:Mutex.Dispose() } catch { }
