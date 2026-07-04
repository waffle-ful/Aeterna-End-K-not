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
$script:RelaunchTimes = New-Object System.Collections.Generic.List[datetime]
$script:LastRelaunch  = [datetime]::MinValue
$script:GraceUntil    = [datetime]::MinValue
$script:CapturedExe   = $null
$script:LastOkFileLog = [datetime]::MinValue
$script:StartedAt     = Get-Date

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
        return $true
    } catch {
        Write-WatchLog "起動に失敗: $($_.Exception.Message)" 'Red'
        return $false
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

# 心拍ログの鮮度。@{ Exists; AgeSec; Fresh; LastLine }
function Get-HealthStatus {
    $res = @{ Exists = $false; AgeSec = [double]::PositiveInfinity; Fresh = $false; LastLine = '' }
    if (Test-Path $HealthLog) {
        $res.Exists = $true
        try {
            $lw = (Get-Item $HealthLog).LastWriteTime
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
Write-WatchLog "設定: stale=${StaleSeconds}s / 巡回=${CheckIntervalSec}s / 起動猶予=${BootGraceSec}s / 再起動上限=${MaxRelaunchPerHour}/h" 'DarkGray'

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

    # --- 正常表示 (コンソールには毎回、ファイルへは5分毎だけ書いてログを異常中心に保つ) ---
    if ($proc -and $health.Fresh) {
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
        Write-WatchLog "起動猶予中... 残り ${left}s (proc=$([bool]$proc) 心拍鮮度=$([int]$health.AgeSec)s)" 'DarkGray'
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
        Write-WatchLog "異常: AU プロセスが見つかりません（クラッシュ/終了）。" 'Red'
    } elseif (-not $health.Fresh) {
        Write-WatchLog ("異常: AU は生存だが心拍が {0:N0}s 途切れています（ハング疑い）。" -f $health.AgeSec) 'Red'
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
