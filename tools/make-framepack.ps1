<#
.SYNOPSIS
  動画 (mp4 / webm) を連番 JPG のフレームパック (.ekfp) に変換する。
  VideoPlayer が使えない環境 (Android のゲーム純正 libunity) で FrameSequencePlayer が再生する形式。

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/make-framepack.ps1 -Source Resources/Media/menu_fire.webm -Output Resources/Media/menu_fire.ekfp -Alpha
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/make-framepack.ps1 -Source Resources/Media/loading_default.mp4 -Output Resources/Media/loading_default.ekfp -Width 640

.NOTES
  形式 (little endian):
    0  "EKFP"
    4  u16 version (=1)
    6  u16 flags   (bit0 = 透明度あり: 各 JPG は上半分が色・下半分が透明度の縦 2 段)
    8  u16 width   (1 コマの表示幅)
    10 u16 height  (1 コマの表示高さ。透明度ありの JPG 自体はこの 2 倍)
    12 u16 fps x100
    14 u16 reserved
    16 u32 frameCount
    20 u32 length[frameCount]
    ... JPG 本体を順に連結
  -Alpha は入力側のデコーダを libvpx に固定する (既定の vp8 デコーダはアルファ面を捨てる)。
#>
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Output,
    [double]$Fps = 15,
    [int]$Width = 512,
    [int]$Quality = 4,
    [switch]$Alpha
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) { throw 'ffmpeg が PATH にありません' }
if (-not (Get-Command ffprobe -ErrorAction SilentlyContinue)) { throw 'ffprobe が PATH にありません' }
if (-not (Test-Path $Source)) { throw "入力が見つかりません: $Source" }

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("ekfp-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

try {
    $fpsText = $Fps.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    $pattern = Join-Path $work 'f%05d.jpg'

    if ($Alpha) {
        $filter = "fps=$fpsText,scale=${Width}:-2,format=rgba,split[a][b];[a]format=rgb24[c];[b]alphaextract[al];[c][al]vstack"
        & ffmpeg -v error -y -c:v libvpx -i $Source -filter_complex $filter -q:v $Quality $pattern
    }
    else {
        & ffmpeg -v error -y -i $Source -vf "fps=$fpsText,scale=${Width}:-2" -q:v $Quality $pattern
    }
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg が失敗しました (exit $LASTEXITCODE)" }

    $frames = @(Get-ChildItem -Path $work -Filter 'f*.jpg' | Sort-Object Name)
    if ($frames.Count -eq 0) { throw 'フレームが 1 枚も出力されませんでした' }

    # 1 枚目の寸法を取る
    $dims = (& ffprobe -v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 $frames[0].FullName).Trim().Split(',')
    $imgW = [int]$dims[0]; $imgH = [int]$dims[1]
    if ($imgW -le 0 -or $imgH -le 0) { throw 'JPG の寸法を読めませんでした' }

    $frameH = if ($Alpha) { [int]($imgH / 2) } else { $imgH }
    $flags = if ($Alpha) { 1 } else { 0 }

    $outDir = Split-Path -Parent ([System.IO.Path]::GetFullPath($Output))
    if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

    $stream = [System.IO.File]::Create([System.IO.Path]::GetFullPath($Output))
    $writer = New-Object System.IO.BinaryWriter($stream)
    try {
        $writer.Write([byte[]][char[]]'EKFP')
        $writer.Write([uint16]1)
        $writer.Write([uint16]$flags)
        $writer.Write([uint16]$imgW)
        $writer.Write([uint16]$frameH)
        $writer.Write([uint16][math]::Round($Fps * 100))
        $writer.Write([uint16]0)
        $writer.Write([uint32]$frames.Count)
        foreach ($f in $frames) { $writer.Write([uint32]$f.Length) }
        foreach ($f in $frames) { $writer.Write([System.IO.File]::ReadAllBytes($f.FullName)) }
    }
    finally {
        $writer.Dispose()
    }

    $size = (Get-Item $Output).Length
    Write-Output ("{0}: {1} frames {2}x{3} @ {4}fps alpha={5} -> {6:N0} KB" -f $Output, $frames.Count, $imgW, $frameH, $fpsText, [bool]$Alpha, ($size / 1KB))
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
