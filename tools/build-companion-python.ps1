# 埋め込み Python (companion.py 用) を packaging/companion-python/EndKnot_DATA/companion/python/ に用意する。
# release.ps1 はこのフォルダをそのまま zip の EndKnot_DATA/ 配下 (BepInEx と並ぶトップレベル) へコピーするだけで、
# ユーザー側の Python インストールが不要になる (companion-run.cmd は同梱 python\python.exe を優先して探す)。
# 素材/依存 (tools/companion/requirements.txt) を変更した時だけ手で実行する (dotnet build には組み込まない)。
param(
    [string]$Out = (Join-Path (Split-Path -Parent $PSScriptRoot) 'packaging\companion-python'),
    [string]$PythonVersion = '3.12.9',
    [string]$CacheDir = (Join-Path $env:TEMP 'endknot-companion-python-cache'),
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest の進捗バー描画は大きいファイルで著しく遅い

$repo = Split-Path -Parent $PSScriptRoot
$requirements = Join-Path $repo 'tools\companion\requirements.txt'
if (-not (Test-Path $requirements)) { throw "requirements.txt not found: $requirements" }

# python.org は release ページから SHA-256 の掲載をやめている (MD5 チェックサム + GPG 署名 + sigstore のみ)。
# ここでは python.org の FTP から直接 TLS 取得した実体の SHA-256 を一度計算して固定し、以後のビルドは
# 毎回この値と突き合わせる (取得元の差し替え/破損/キャッシュ汚染を検知する pin)。バージョンを上げる時は
# 手で1回ダウンロードして sha256 を計算し、下の表に追記すること。
$KnownHashes = @{
    '3.12.9' = '615861fb801e8b04c847598db4e1e46e4b046295017caa37cb5486dde72b5865'
}
if (-not $KnownHashes.ContainsKey($PythonVersion)) {
    throw "PythonVersion $PythonVersion のハッシュが未登録です。python.org からダウンロードし sha256 を計算のうえ `$KnownHashes に追記してください。"
}
$expectedSha256 = $KnownHashes[$PythonVersion]

$zipName = "python-$PythonVersion-embed-amd64.zip"
$zipUrl  = "https://www.python.org/ftp/python/$PythonVersion/$zipName"
New-Item -ItemType Directory -Force $CacheDir | Out-Null
$cacheZip = Join-Path $CacheDir $zipName

if ($Force -or -not (Test-Path $cacheZip)) {
    Write-Host "downloading $zipUrl"
    Invoke-WebRequest -Uri $zipUrl -OutFile $cacheZip -UseBasicParsing
} else {
    Write-Host "using cached zip: $cacheZip"
}

$actualSha256 = (Get-FileHash -Path $cacheZip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualSha256 -ne $expectedSha256) {
    Remove-Item $cacheZip -Force -ErrorAction SilentlyContinue
    throw "SHA-256 mismatch for ${zipName}: expected $expectedSha256, got $actualSha256 (cached file removed)"
}
Write-Host "sha256 verified: $actualSha256"

$pythonDir = Join-Path $Out 'EndKnot_DATA\companion\python'
if ($Force -and (Test-Path $pythonDir)) { Remove-Item $pythonDir -Recurse -Force }
New-Item -ItemType Directory -Force $pythonDir | Out-Null

$pythonExe = Join-Path $pythonDir 'python.exe'
if (-not (Test-Path $pythonExe)) {
    Write-Host "extracting $zipName -> $pythonDir"
    Expand-Archive -Path $cacheZip -DestinationPath $pythonDir -Force
} else {
    Write-Host 'python.exe already present, skipping extraction (use -Force to redo)'
}
if (-not (Test-Path $pythonExe)) { throw "python.exe not found after extraction: $pythonExe" }

# 埋め込み Python の既定 ._pth は `import site` がコメントアウトされており、有効化しないと
# site-packages が sys.path に載らず pip 自体を import できない。
$shortVer = ($PythonVersion -split '\.')[0..1] -join ''
$pthPath = Join-Path $pythonDir "python$shortVer._pth"
if (-not (Test-Path $pthPath)) { throw "_pth file not found: $pthPath" }
$pthText = Get-Content -Path $pthPath -Raw -Encoding UTF8
if ($pthText -match '(?m)^\s*#\s*import site\s*$') {
    $pthText = $pthText -replace '(?m)^\s*#\s*import site\s*$', 'import site'
    [System.IO.File]::WriteAllText($pthPath, $pthText, [System.Text.UTF8Encoding]::new($false))
    Write-Host "enabled 'import site' in $pthPath"
} elseif ($pthText -match '(?m)^\s*import site\s*$') {
    Write-Host "'import site' already enabled in $pthPath"
} else {
    throw "unexpected _pth contents (no import site line found): $pthPath"
}

$env:PYTHONDONTWRITEBYTECODE = '1'   # __pycache__ を最初から生やさない (掃除の手間を減らす)

$getPipPath = Join-Path $CacheDir 'get-pip.py'
Write-Host 'downloading get-pip.py (pypa bootstrap script; intentionally not version-pinned)'
Invoke-WebRequest -Uri 'https://bootstrap.pypa.io/get-pip.py' -OutFile $getPipPath -UseBasicParsing

& $pythonExe $getPipPath --no-warn-script-location
if ($LASTEXITCODE -ne 0) { throw "get-pip.py failed with exit code $LASTEXITCODE" }
Remove-Item $getPipPath -Force -ErrorAction SilentlyContinue

& $pythonExe -m pip install --no-warn-script-location --no-cache-dir -r $requirements
if ($LASTEXITCODE -ne 0) { throw "pip install -r requirements.txt failed with exit code $LASTEXITCODE" }

Write-Host 'stripping pip cache / __pycache__ / bundled test directories'
& $pythonExe -m pip cache purge 2>$null | Out-Null
Get-ChildItem -Path $pythonDir -Recurse -Directory -Filter '__pycache__' -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
$sitePackages = Join-Path $pythonDir 'Lib\site-packages'
if (Test-Path $sitePackages) {
    Get-ChildItem -Path $sitePackages -Recurse -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq 'tests' -or $_.Name -eq 'test' } |
        ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
}

$sizeBytes = (Get-ChildItem -Path $pythonDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
$sizeMB = [math]::Round($sizeBytes / 1MB, 1)
Write-Host "companion python folder: $pythonDir ($sizeMB MB)"
