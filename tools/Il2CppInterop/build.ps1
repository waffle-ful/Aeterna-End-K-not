# Builds packaging/il2cppinterop/ from public source:
#   1. Pietrodjaowjao/Il2CppInterop@f1627d60 (LGPL-3.0) with tools/Il2CppInterop/il2cppinterop.patch applied, built with the local SDK
#   2. Il2CppInterop.Runtime.dll -> tools/InteropWbPatch -> tools/InteropPtrCachePatch
# Usage: pwsh -NoProfile -File tools/Il2CppInterop/build.ps1 [-Out packaging/il2cppinterop]
param([string] $Out)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $Out) { $Out = Join-Path $repo 'packaging/il2cppinterop' }
$interopRepo = 'https://github.com/Pietrodjaowjao/Il2CppInterop.git'
$interopCommit = 'f1627d60efc218e4861bacc1239267f96f32ee2c'
$files = @('Il2CppInterop.Common.dll', 'Il2CppInterop.Common.xml', 'Il2CppInterop.Runtime.dll', 'Il2CppInterop.Runtime.xml', 'Il2CppInterop.Runtime.deps.json')

$work = Join-Path ([IO.Path]::GetTempPath()) "ek-il2cppinterop-$([Guid]::NewGuid().ToString('N'))"
try {
    # The patch has LF line endings, so check out the source as it is in the repository.
    $src = Join-Path $work 'src'
    git init -q $src
    git -C $src config core.autocrlf false
    git -C $src fetch -q --depth 1 $interopRepo $interopCommit
    if ($LASTEXITCODE -ne 0) { throw "fetching Il2CppInterop failed ($LASTEXITCODE)" }
    git -C $src checkout -q FETCH_HEAD
    if ($LASTEXITCODE -ne 0) { throw "checking out Il2CppInterop failed ($LASTEXITCODE)" }
    git -C $src apply (Join-Path $PSScriptRoot 'il2cppinterop.patch')
    if ($LASTEXITCODE -ne 0) { throw "applying il2cppinterop.patch failed ($LASTEXITCODE)" }
    # Run from this repository so its global.json picks the SDK (Il2CppInterop's own global.json asks for SDK 8).
    Push-Location $repo
    try {
        dotnet build (Join-Path $src 'Il2CppInterop.Runtime/Il2CppInterop.Runtime.csproj') -c Release -v:q -nologo -p:GeneratePackageOnBuild=false -p:ContinuousIntegrationBuild=true -p:SourceRevisionId=f1627d60.endknot
        if ($LASTEXITCODE -ne 0) { throw "Il2CppInterop build failed ($LASTEXITCODE)" }
        $built = Join-Path $src 'bin/Il2CppInterop.Runtime/net6.0'
        $wb = Join-Path $work 'Il2CppInterop.Runtime.wb.dll'
        $cached = Join-Path $work 'Il2CppInterop.Runtime.dll'
        dotnet run --project (Join-Path $repo 'tools/InteropWbPatch') -c Release -v:q -- (Join-Path $built 'Il2CppInterop.Runtime.dll') $wb
        if ($LASTEXITCODE -ne 0) { throw "InteropWbPatch failed ($LASTEXITCODE)" }
        dotnet run --project (Join-Path $repo 'tools/InteropPtrCachePatch') -c Release -v:q -- $wb $cached
        if ($LASTEXITCODE -ne 0) { throw "InteropPtrCachePatch failed ($LASTEXITCODE)" }
    }
    finally { Pop-Location }

    New-Item -ItemType Directory -Force $Out | Out-Null
    foreach ($f in $files) { Copy-Item (Join-Path $built $f) (Join-Path $Out $f) -Force }
    Copy-Item $cached (Join-Path $Out 'Il2CppInterop.Runtime.dll') -Force
    Write-Host "built: $Out"
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
