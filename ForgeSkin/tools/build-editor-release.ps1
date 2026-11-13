#Requires -Version 7.0
# Builds one ForgeSkin release from the current source and lists what to upload; never uploads:
#   pwsh Mods/ForgeSkin/tools/build-editor-release.ps1 -BepInEx <profile>/BepInEx [-Output <dir>] [-SkipMake]
# 1. the plugin (Release; builds ForgeCore with it), 2. the ForgeCore and ForgeSkin Thunderstore zips, checked by
# Mods/Build/verify_release.py, 3. the self-contained single-file backend, 4. the plugin the installer carries:
# publish/plugin/ForgeSkin/ (ForgeSkin.Plugin.dll and its lang/) and publish/plugin/ForgeCore/ (the unpacked ForgeCore zip),
# 5. `pnpm make` (Squirrel: ForgeSkin-<version> Setup.exe, ForgeSkin-<version>-full.nupkg, RELEASES), 6. <Output>/upload
# with every file to upload and upload.sha256. -SkipMake stops before step 5. The default Output is the folder the
# backend of a checkout reads its plugin from (Install.PluginSource), so a run also refreshes the development plugin.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BepInEx,
    [string]$Output,
    [switch]$SkipMake
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
Set-Location $root
$out = [System.IO.Path]::GetFullPath($(if ($Output) { $Output } else { Join-Path $root 'artifacts/forge-skin-release' }))
$skin = Join-Path $root 'Mods/ForgeSkin'
$editor = Join-Path $skin 'Editor'
$dotnet = Join-Path $out 'dotnet'
$zips = Join-Path $out 'thunderstore'
$publish = Join-Path $out 'publish'
$upload = Join-Path $out 'upload'
function Step([string]$name) { Write-Host "== $name" }
function Check([string]$what) { if ($LASTEXITCODE) { throw "$what failed (exit $LASTEXITCODE)" } }

# Editor/package.json and manifest.json carry the same version (set-package-version.ts writes both from release.json).
$version = (Get-Content (Join-Path $editor 'package.json') -Raw | ConvertFrom-Json).version
$manifest = (Get-Content (Join-Path $skin 'manifest.json') -Raw | ConvertFrom-Json).version_number
if ($version -ne $manifest) { throw "Editor/package.json is $version but manifest.json is ${manifest}: run pnpm ts Tools/Forge/set-package-version.ts --write" }
Write-Host "ForgeSkin $version -> $out"

Step 'plugin (Release)'
dotnet build Mods/ForgeSkin/ForgeSkin.csproj -c Release --artifacts-path $dotnet "-p:GTFOBepInExPath=$BepInEx" -v q --nologo --disable-build-servers
Check 'plugin build'

Step 'Thunderstore zips'
foreach ($dir in $zips, $upload) { if (Test-Path $dir) { Remove-Item -Recurse -Force $dir } }
foreach ($package in 'ForgeCore', 'ForgeSkin') {
    $packager = "Mods/$package/tools/package_release.py"
    if (-not (Test-Path $packager)) { throw "$packager is missing" }
    python -E -X utf8 -B $packager --artifacts-path $dotnet --output $zips
    Check "$package package"
}
python -E -X utf8 -B Mods/Build/verify_release.py --packages $zips --artifacts-path $dotnet
Check 'verify_release'

Step 'backend (self-contained single file)'
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish Mods/ForgeSkin/Backend/ForgeSkin.Backend.csproj -c Release --artifacts-path (Join-Path $out 'dotnet-backend') --self-contained -p:PublishSingleFile=true -o $publish -v q --nologo
Check 'backend publish'

Step 'plugin the installer carries'
$plugin = New-Item -ItemType Directory -Force (Join-Path $publish 'plugin/ForgeSkin')
Copy-Item (Join-Path $dotnet 'bin/ForgeSkin/release/ForgeSkin.Plugin.dll') $plugin
Copy-Item -Recurse (Join-Path $dotnet 'bin/ForgeSkin/release/lang') $plugin
$core = @(Get-ChildItem $zips -Filter 'NAinfini-ForgeCore-*.zip')
if ($core.Count -ne 1) { throw "expected one ForgeCore zip in $zips" }
Expand-Archive $core[0].FullName (Join-Path $publish 'plugin/ForgeCore')
Write-Host "  publish/plugin: ForgeSkin.Plugin.dll and lang/ + $($core[0].Name) unpacked"

$files = @(Get-ChildItem $zips -Filter 'NAinfini-ForgeSkin-*.zip')
if (-not $SkipMake) {
    Step 'pnpm make'
    $env:FORGESKIN_PUBLISH_DIR = $publish
    $env:FORGESKIN_BACKEND_EXE = Join-Path $publish 'ForgeSkin.Backend.exe'
    Push-Location $editor
    try {
        pnpm install --frozen-lockfile
        Check 'pnpm install'
        pnpm make
        Check 'pnpm make'
    } finally { Pop-Location }
    $made = Join-Path $editor 'out/make/squirrel.windows/x64'
    $files += @("ForgeSkin-$version Setup.exe", "ForgeSkin-$version-full.nupkg", 'RELEASES' | ForEach-Object { Get-Item (Join-Path $made $_) })
}

Step 'upload'
New-Item -ItemType Directory -Force $upload | Out-Null
$lines = foreach ($file in $files) {
    Copy-Item $file.FullName $upload
    '{0}  {1}  {2} bytes' -f (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $file.Name, $file.Length
}
$lines | Set-Content (Join-Path $upload 'upload.sha256') -Encoding utf8NoBOM
$lines | ForEach-Object { Write-Host "  $_" }
if ($SkipMake) { Write-Host '  (-SkipMake: no installer, nupkg or RELEASES)' }
Write-Host "Not uploaded. Upload steps: Mods/Release/README.md, ForgeSkin editor."
