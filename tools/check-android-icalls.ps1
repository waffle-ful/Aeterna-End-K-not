<#
.SYNOPSIS
  Checks that the Android build of the mod only reaches Unity engine calls the game's own
  libunity.so provides.

.DESCRIPTION
  The game ships a libunity built with engine code stripping. A Unity API whose internal call was
  stripped still compiles and loads, but throws NotSupportedException the moment it is called on
  Android. This script builds the mod in the Android configuration and lists every such internal
  call the mod can reach, compared against tools/android-icall-baseline.txt (calls reached only
  from code that does not run on Android). Exit code 1 means a new unserved call appeared.

  -Probe <regex> instead lists which matching internal calls the game's libunity has, e.g. to see
  whether an engine module survives stripping. Pass -FullLibUnity (an unstripped libunity.so of the
  same Unity version) to also see the calls a complete engine build would have.

.PARAMETER AndroidBepInExPath
  Directory holding the on-device loader's core/ and interop/ assemblies (same as the
  AndroidBepInExPath MSBuild property). Defaults to local.props, then the csproj default.

.PARAMETER LibUnity
  The game's libunity.so (lib/arm64-v8a of the game's split_config.arm64_v8a.apk).
  Defaults to <parent of AndroidBepInExPath>\apk-extract\lib\arm64-v8a\libunity.so.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-android-icalls.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-android-icalls.ps1 -Probe 'UnityEngine\.Video\.' -FullLibUnity <unstripped libunity.so>
#>
param(
    [string]$AndroidBepInExPath,
    [string]$LibUnity,
    [string]$FullLibUnity,
    [string]$Probe,
    [string]$Report,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$tool = Join-Path $PSScriptRoot 'AndroidIcallAudit'
$baseline = Join-Path $PSScriptRoot 'android-icall-baseline.txt'

if (-not $AndroidBepInExPath) {
    $props = Join-Path $repo 'local.props'
    if (Test-Path $props) {
        $m = Select-String -Path $props -Pattern '<AndroidBepInExPath>([^<]+)</AndroidBepInExPath>' | Select-Object -First 1
        if ($m) { $AndroidBepInExPath = $m.Matches[0].Groups[1].Value }
    }
    if (-not $AndroidBepInExPath) { $AndroidBepInExPath = 'D:\Amongus\EndKnot-android\android-bepinex' }
}
$interop = Join-Path $AndroidBepInExPath 'interop'
if (-not $LibUnity) { $LibUnity = Join-Path (Split-Path -Parent $AndroidBepInExPath) 'apk-extract\lib\arm64-v8a\libunity.so' }
if (-not (Test-Path $interop)) { throw "interop directory not found: $interop" }
if (-not (Test-Path $LibUnity)) { throw "game libunity.so not found: $LibUnity (pass -LibUnity)" }

$work = Join-Path $env:TEMP 'endknot-android-icalls'
New-Item -ItemType Directory -Force $work | Out-Null

if ($Probe) {
    $probeArgs = @('probe', '--interop', $interop, '--libunity', $LibUnity, '--pattern', $Probe)
    if ($FullLibUnity) { $probeArgs += @('--full', $FullLibUnity) }
    dotnet run --project $tool -c Release -v:q -- @probeArgs
    exit $LASTEXITCODE
}

$out = Join-Path $work 'android-out'
if (-not $SkipBuild) {
    # Build into a scratch directory so the deployed PC plugin is left untouched.
    dotnet build (Join-Path $repo 'EndKnot.csproj') -c Android -o $out -v:q -nologo "-p:AmongUsPath=$(Join-Path $work 'deploy-hold')" | Select-String -Pattern ' error |エラー' | ForEach-Object { $_.Line }
    if ($LASTEXITCODE -ne 0) { throw "Android build failed" }
}
$mod = Join-Path $out 'EndKnot.dll'
if (-not (Test-Path $mod)) { throw "Android build output not found: $mod (run without -SkipBuild)" }
if (-not $Report) { $Report = Join-Path $work 'icall-report.txt' }

dotnet run --project $tool -c Release -v:q -- audit --mod $mod --interop $interop --libunity $LibUnity --baseline $baseline --report $Report
$code = $LASTEXITCODE
Write-Host "report: $Report"
exit $code
