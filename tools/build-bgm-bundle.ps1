# BGM AssetBundle ビルド: Resources/Sounds/BGM/*.ogg を Unity プロジェクト unity/BgmBundle に写し、
# Unity 2022.3.44f1 (Among Us 実機と同じ版) をバッチモードで走らせて endknot_bgm を焼き、
# Resources/Sounds/BGM/endknot_bgm.bundle に置く (csproj の Resources/** 埋込で DLL に入る)。
# 素材を差し替えた時だけ手で実行する (dotnet build には組み込まない)。
# メインメニュー背景の PNG (Resources/Images/MainMenu/Dusk/<theme>/dusk_*.png) は別バンドル endknot_dusk に
#   非圧縮 RGBA32 で焼き、Resources/Images/MainMenu/Dusk/endknot_dusk.bundle に置く (Windows 版のみ)。
# Backrooms ロビーの床・壁 PNG (Resources/Images/Backrooms/*.png) も同じ設定で別バンドル endknot_backrooms に
#   焼き、Resources/Images/Backrooms/endknot_backrooms.bundle に置く (-Android では endknot_backrooms.android.bundle)。
# -Android: BuildTarget=Android (arm64) で焼き、*.android.bundle として置く (csproj の Android 構成が
#   Windows 版の代わりに埋め込む)。Editor に Android Build Support モジュールが要る。
param(
    [string]$UnityExe = 'D:\Unity\Editor\2022.3.44f1\Editor\Unity.exe',
    [switch]$Android,
    [switch]$KeepLog
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo 'unity\BgmBundle'
$src  = Join-Path $repo 'Resources\Sounds\BGM'
$dstAssets = Join-Path $proj 'Assets\BGM'
$buildDir = if ($Android) { 'Build\android' } else { 'Build' }
$bundleSuffix = if ($Android) { '.android.bundle' } else { '.bundle' }
$method = if ($Android) { 'BundleBuilder.BuildAndroid' } else { 'BundleBuilder.Build' }
$out  = Join-Path $proj "$buildDir\endknot_bgm"
# 長尺効果音 (別バンドル endknot_sfx): 発射/チャージ音と Backrooms ロビー環境音
$sfxSources = @(
    (Join-Path $repo 'Resources\Sounds\Backrooms\lobby-ambient.wav'),
    (Join-Path $repo 'Resources\Sounds\MapAtmosphere\MiraRainLoop.ogg'),
    (Join-Path $repo 'Resources\Sounds\MapAtmosphere\MiraThunderNear.ogg'),
    (Join-Path $repo 'Resources\Sounds\MapAtmosphere\MiraThunderFar.ogg'),
    (Join-Path $repo 'Resources\Sounds\MapAtmosphere\MiraWindGust.ogg'),
    (Join-Path $repo 'Resources\Sounds\MapAtmosphere\AirshipHum.ogg'),
    (Join-Path $repo 'Resources\Sounds\MapAtmosphere\AirshipFarDoor.ogg'),
    (Join-Path $repo 'Resources\Sounds\MapAtmosphere\AirshipGlitch.ogg')
)
$dstSfx = Join-Path $proj 'Assets\SFX'
$outSfx = Join-Path $proj "$buildDir\endknot_sfx"
$duskSrc = Join-Path $repo 'Resources\Images\MainMenu\Dusk'
$dstDusk = Join-Path $proj 'Assets\Dusk'
$outDusk = Join-Path $proj "$buildDir\endknot_dusk"
$backroomsSrc = Join-Path $repo 'Resources\Images\Backrooms'
$dstBackrooms = Join-Path $proj 'Assets\Backrooms'
$outBackrooms = Join-Path $proj "$buildDir\endknot_backrooms"
$log  = Join-Path $proj 'Logs\build-bgm-bundle.log'

if (-not (Test-Path $UnityExe)) { throw "Unity editor not found: $UnityExe" }
$oggs = Get-ChildItem $src -Filter '*.ogg'
if ($oggs.Count -eq 0) { throw "no .ogg under $src" }

New-Item -ItemType Directory -Force $dstAssets | Out-Null
New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
Get-ChildItem $dstAssets -Filter '*.ogg' | Remove-Item -Force
foreach ($f in $oggs) { Copy-Item $f.FullName (Join-Path $dstAssets $f.Name) -Force }
Write-Host ("copied {0} tracks -> {1}" -f $oggs.Count, $dstAssets)
New-Item -ItemType Directory -Force $dstSfx | Out-Null
Get-ChildItem $dstSfx -Include '*.ogg', '*.wav' -File | Remove-Item -Force
foreach ($f in $sfxSources) { if (-not (Test-Path $f)) { throw "sfx source missing: $f" }; Copy-Item $f (Join-Path $dstSfx (Split-Path -Leaf $f)) -Force }
Write-Host ("copied {0} sfx -> {1}" -f $sfxSources.Count, $dstSfx)
if (Test-Path $dstDusk) { Remove-Item $dstDusk -Recurse -Force }
if (-not $Android) {
    $duskPngs = Get-ChildItem $duskSrc -Recurse -Filter 'dusk_*.png' | Where-Object { $_.Directory.FullName -ne $duskSrc }
    if ($duskPngs.Count -eq 0) { throw "no dusk png under $duskSrc" }
    foreach ($f in $duskPngs) {
        $dir = Join-Path $dstDusk $f.Directory.Name
        New-Item -ItemType Directory -Force $dir | Out-Null
        Copy-Item $f.FullName (Join-Path $dir $f.Name) -Force
    }
    Write-Host ("copied {0} dusk textures -> {1}" -f $duskPngs.Count, $dstDusk)
}
if (Test-Path $dstBackrooms) { Remove-Item $dstBackrooms -Recurse -Force }
$backroomsPngs = Get-ChildItem $backroomsSrc -Filter '*.png'
if ($backroomsPngs.Count -eq 0) { throw "no backrooms png under $backroomsSrc" }
New-Item -ItemType Directory -Force $dstBackrooms | Out-Null
foreach ($f in $backroomsPngs) { Copy-Item $f.FullName (Join-Path $dstBackrooms $f.Name) -Force }
Write-Host ("copied {0} backrooms textures -> {1}" -f $backroomsPngs.Count, $dstBackrooms)

$args = @('-batchmode', '-nographics', '-quit', '-projectPath', ('"' + $proj + '"'), '-executeMethod', $method, '-logFile', ('"' + $log + '"'))
$p = Start-Process -FilePath $UnityExe -ArgumentList $args -PassThru -Wait
if ($p.ExitCode -ne 0) {
    Select-String -Path $log -Pattern 'error CS|BundleBuilder|Exception' | Select-Object -First 10 | ForEach-Object { Write-Host $_.Line }
    throw "Unity exited with $($p.ExitCode) (log: $log)"
}
if (-not (Test-Path $out)) { throw "bundle not produced: $out" }

$dst = Join-Path $src "endknot_bgm$bundleSuffix"
Copy-Item $out $dst -Force
$size = (Get-Item $dst).Length
Write-Host ("bundle: {0} ({1:N0} bytes)" -f $dst, $size)
if (-not (Test-Path $outSfx)) { throw "sfx bundle not produced: $outSfx" }
$dstSfxBundle = Join-Path $repo "Resources\Sounds\endknot_sfx$bundleSuffix"
Copy-Item $outSfx $dstSfxBundle -Force
Write-Host ("sfx bundle: {0} ({1:N0} bytes)" -f $dstSfxBundle, (Get-Item $dstSfxBundle).Length)
if (-not $Android) {
    if (-not (Test-Path $outDusk)) { throw "dusk bundle not produced: $outDusk" }
    $dstDuskBundle = Join-Path $duskSrc 'endknot_dusk.bundle'
    Copy-Item $outDusk $dstDuskBundle -Force
    Write-Host ("dusk bundle: {0} ({1:N0} bytes)" -f $dstDuskBundle, (Get-Item $dstDuskBundle).Length)
}
if (-not (Test-Path $outBackrooms)) { throw "backrooms bundle not produced: $outBackrooms" }
$dstBackroomsBundle = Join-Path $backroomsSrc "endknot_backrooms$bundleSuffix"
Copy-Item $outBackrooms $dstBackroomsBundle -Force
Write-Host ("backrooms bundle: {0} ({1:N0} bytes)" -f $dstBackroomsBundle, (Get-Item $dstBackroomsBundle).Length)
Select-String -Path $log -Pattern 'BundleBuilder:' | Select-Object -First 1 | ForEach-Object { Write-Host $_.Line }
if (-not $KeepLog) { Remove-Item $log -Force -ErrorAction SilentlyContinue }
