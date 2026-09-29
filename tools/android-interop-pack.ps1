<#
.SYNOPSIS
  Packs the Il2CppInterop assemblies generated on a phone into the launcher APK asset
  (build-android/apk-assets/interop-arm64.zip + .properties) so a fresh install never runs
  Cpp2IL / the interop generator on the device.

.DESCRIPTION
  BepInEx decides whether to regenerate by comparing interop/assembly-hash.txt with
  MD5( padded libil2cpp.so | unity-libs/*.dll (name+bytes, readdir order) | generator version |
  Cpp2IL version ). The bundle ships no unity-libs (their readdir order differs per filesystem),
  so the hash here is computed over the padded library plus the two version strings only, with
  the padding reproduced exactly as fusion's padded_dlopen writes it (last PT_LOAD p_memsz
  rounded up to 16 KiB after adding the 1 MiB trampoline pool).

  Inputs come from the connected device by default: the game's original libil2cpp.so and the
  interop set BepInEx generated under files/<game>/BepInEx/interop of the launcher package.

.PARAMETER InteropDir
  Use this local directory instead of pulling BepInEx/interop from the device.
.PARAMETER Il2CppSo
  Use this local original (unpadded) libil2cpp.so instead of pulling it from the device.
#>
[CmdletBinding()]
param(
    [string]$Adb = 'D:\Android\Sdk\platform-tools\adb.exe',
    [string]$Serial,
    [string]$LauncherPackage = 'dev.waffleful.endknot',
    [string]$GamePackage = 'com.innersloth.spacemafia',
    [string]$InteropDir,
    [string]$Il2CppSo,
    [string]$BepInExZip,
    [string]$OutDir,
    [long]$PoolSize = 1MB,
    [long]$Align = 16KB
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $BepInExZip) { $BepInExZip = Join-Path $repo 'Android\Launcher\fusionApp\src\main\assets\BepInEx-arm64.zip' }
if (-not $OutDir) { $OutDir = Join-Path $repo 'build-android\apk-assets' }
$work = Join-Path ([IO.Path]::GetTempPath()) ("endknot-interop-pack-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
trap { if (Test-Path $work) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }; break }

function Invoke-Adb([string[]]$adbArgs) {
    $all = @(); if ($Serial) { $all += @('-s', $Serial) }; $all += $adbArgs
    # adb writes notices to stderr; under 'Stop' those lines would become terminating errors.
    $ErrorActionPreference = 'Continue'
    try { $out = & $Adb @all 2>&1 } finally { $ErrorActionPreference = 'Stop' }
    if ($LASTEXITCODE -ne 0) { throw "adb $($adbArgs -join ' ') failed: $out" }
    return ($out | ForEach-Object { "$_".TrimEnd("`r") })
}
# cmd's redirection keeps exec-out binary-safe; PowerShell's pipeline would re-encode it.
function Invoke-AdbExecOutToFile([string]$shellCommand, [string]$file) {
    $s = if ($Serial) { "-s $Serial " } else { '' }
    cmd /c "`"$Adb`" $s exec-out `"$shellCommand`" > `"$file`""
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $file) -or (Get-Item $file).Length -eq 0) { throw "adb exec-out '$shellCommand' produced nothing" }
}

# --- game identity -----------------------------------------------------------------------
$dump = Invoke-Adb @('shell', "dumpsys package $GamePackage")
$gameVersionCode = ($dump | Where-Object { $_ -match '^\s*versionCode=(\d+)' } | Select-Object -First 1) -replace '^\s*versionCode=(\d+).*', '$1'
$gameVersionName = ($dump | Where-Object { $_ -match '^\s*versionName=' } | Select-Object -First 1) -replace '^\s*versionName=', ''
$libDir = ($dump | Where-Object { $_ -match '^\s*legacyNativeLibraryDir=' } | Select-Object -First 1) -replace '^\s*legacyNativeLibraryDir=', ''
if (-not $gameVersionCode -or -not $libDir) { throw "Could not read $GamePackage from dumpsys (installed?)" }
Write-Host "game: $GamePackage versionCode=$gameVersionCode versionName=$gameVersionName"

# --- original libil2cpp.so ---------------------------------------------------------------
if (-not $Il2CppSo) {
    $Il2CppSo = Join-Path $work 'libil2cpp.so'
    Invoke-AdbExecOutToFile "cat $libDir/arm64/libil2cpp.so" $Il2CppSo
}
$lib = [IO.File]::ReadAllBytes($Il2CppSo)
if ($lib.Length -lt 64 -or $lib[0] -ne 0x7F -or $lib[1] -ne 0x45 -or $lib[4] -ne 2 -or [BitConverter]::ToUInt16($lib, 0x10) -ne 3) { throw "$Il2CppSo is not an ELF64 shared object" }
$phoff = [BitConverter]::ToUInt64($lib, 0x20)
$phentsize = [BitConverter]::ToUInt16($lib, 0x36)
$phnum = [BitConverter]::ToUInt16($lib, 0x38)
$lastOff = -1; $lastEnd = [uint64]0
for ($i = 0; $i -lt $phnum; $i++) {
    $o = [int]($phoff + $i * $phentsize)
    if ([BitConverter]::ToUInt32($lib, $o) -ne 1) { continue }   # PT_LOAD
    $vaddr = [BitConverter]::ToUInt64($lib, $o + 0x10)
    $memsz = [BitConverter]::ToUInt64($lib, $o + 0x28)
    if ($lastOff -lt 0 -or ($vaddr + $memsz) -gt $lastEnd) { $lastOff = $o; $lastEnd = $vaddr + $memsz }
}
if ($lastOff -lt 0) { throw 'no PT_LOAD segment found' }
$memsz = [BitConverter]::ToUInt64($lib, $lastOff + 0x28)
$padded = [uint64](([uint64]$memsz + [uint64]$PoolSize + [uint64]$Align - 1) -band (-bnot ([uint64]$Align - 1)))
[Array]::Copy([BitConverter]::GetBytes($padded), 0, $lib, $lastOff + 0x28, 8)
Write-Host ("padded last PT_LOAD p_memsz 0x{0:x} -> 0x{1:x} (pool {2}, align {3})" -f $memsz, $padded, $PoolSize, $Align)

# --- generator versions from the bundled BepInEx core ------------------------------------
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($BepInExZip)
try {
    $versions = @{}
    foreach ($name in 'Il2CppInterop.Generator.dll', 'Cpp2IL.Core.dll') {
        $entry = $zip.GetEntry("core/$name"); if (-not $entry) { throw "core/$name missing in $BepInExZip" }
        $tmp = Join-Path $work $name
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $tmp, $true)
        $versions[$name] = [Reflection.AssemblyName]::GetAssemblyName($tmp).Version.ToString()
    }
} finally { $zip.Dispose() }
Write-Host "generator: Il2CppInterop.Generator $($versions['Il2CppInterop.Generator.dll']) / Cpp2IL.Core $($versions['Cpp2IL.Core.dll'])"

# --- assembly-hash.txt (BepInEx's Il2CppInteropManager.ComputeHash without unity-libs) ----
$md5 = [Security.Cryptography.MD5]::Create()
[void]$md5.TransformBlock($lib, 0, $lib.Length, $lib, 0)
foreach ($v in $versions['Il2CppInterop.Generator.dll'], $versions['Cpp2IL.Core.dll']) {
    $b = [Text.Encoding]::UTF8.GetBytes($v); [void]$md5.TransformBlock($b, 0, $b.Length, $b, 0)
}
[void]$md5.TransformFinalBlock([byte[]]::new(0), 0, 0)
$interopHash = ($md5.Hash | ForEach-Object { $_.ToString('x2') }) -join ''
Write-Host "interop hash: $interopHash"

# --- interop set -------------------------------------------------------------------------
if (-not $InteropDir) {
    $tar = Join-Path $work 'interop.tar'
    Invoke-AdbExecOutToFile "run-as $LauncherPackage tar -cf - -C files/$GamePackage/BepInEx interop" $tar
    # Windows' own bsdtar: a Git-Bash tar on PATH would misread the drive letter as a host name.
    & "$env:SystemRoot\System32\tar.exe" -xf $tar -C $work
    if ($LASTEXITCODE -ne 0) { throw 'tar -xf failed' }
    $InteropDir = Join-Path $work 'interop'
}
$files = Get-ChildItem -File $InteropDir | Where-Object { $_.Name -ne 'assembly-hash.txt' }
$dlls = @($files | Where-Object { $_.Extension -eq '.dll' })
if ($dlls.Count -lt 50) { throw "only $($dlls.Count) interop DLLs in $InteropDir - not a complete set" }
foreach ($must in 'Assembly-CSharp.dll', 'Il2Cppmscorlib.dll', 'UnityEngine.CoreModule.dll') {
    if (-not ($dlls | Where-Object Name -eq $must)) { throw "$must missing in $InteropDir" }
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
$outZip = Join-Path $OutDir 'interop-arm64.zip'
$outProps = Join-Path $OutDir 'interop-arm64.properties'
# Both files are staged in the work directory and moved together at the end, so a failure in
# between never leaves a new zip next to stale properties (the launcher keys on their sha256).
$stageZip = Join-Path $work 'interop-arm64.zip'
$stageProps = Join-Path $work 'interop-arm64.properties'
$archive = [IO.Compression.ZipFile]::Open($stageZip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($f in ($files | Sort-Object Name)) {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, "interop/$($f.Name)", [IO.Compression.CompressionLevel]::Optimal)
    }
    $e = $archive.CreateEntry('interop/assembly-hash.txt', [IO.Compression.CompressionLevel]::Optimal)
    $s = $e.Open(); $b = [Text.Encoding]::ASCII.GetBytes($interopHash); $s.Write($b, 0, $b.Length); $s.Dispose()
} finally { $archive.Dispose() }

$sha = (Get-FileHash -Algorithm SHA256 $stageZip).Hash.ToLowerInvariant()
$raw = ($files | Measure-Object Length -Sum).Sum
@(
    "sha256=$sha",
    "size=$((Get-Item $stageZip).Length)",
    "rawSize=$raw",
    "files=$($files.Count)",
    "interopHash=$interopHash",
    "gameVersionCode=$gameVersionCode",
    "gameVersionName=$gameVersionName",
    "generatorVersion=$($versions['Il2CppInterop.Generator.dll'])",
    "cpp2ilVersion=$($versions['Cpp2IL.Core.dll'])",
    "poolSize=$PoolSize",
    "align=$Align",
    "packedAt=$(Get-Date -Format 'yyyy-MM-ddTHH:mm:ssK')"
) | Set-Content -Encoding ASCII $stageProps

Move-Item -Force $stageZip $outZip
Move-Item -Force $stageProps $outProps
Remove-Item -Recurse -Force $work
Write-Host ("packed {0} files ({1:n1} MB raw) -> {2} ({3:n1} MB) sha256={4}" -f $files.Count, ($raw / 1MB), $outZip, ((Get-Item $outZip).Length / 1MB), $sha.Substring(0, 12))
