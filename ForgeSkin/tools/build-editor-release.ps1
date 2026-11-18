#Requires -Version 7.0
# Builds one ForgeSkin release from the current source and lists what to upload; never uploads:
#   pwsh Mods/ForgeSkin/tools/build-editor-release.ps1 -ForgeCore <NAinfini-ForgeCore zip> [-BepInEx <profile>/BepInEx] [-Output <dir>] [-Character <model.vrm>] [-SkipMake | -PluginOnly]
# -ForgeCore is the ForgeCore zip of the release set (Mods/Release/build-all.ps1 or build-release-set.ps1, <release>/packages/):
# the editor carries that very package, never a second build of it.
# 1. the plugin (Release; builds ForgeCore with it), 2. the ForgeSkin Thunderstore zip, checked together with the given ForgeCore
# zip by Mods/Build/verify_release.py against this build, 3. Backend/vanilla-textures.json from the installed game (the game's own textures of its
# revision, which tell the editor when another mod rewrote the game files: run on a Steam-verified install; commit the file
# when it changed) and the self-contained single-file backend, 4. the plugin the installer carries:
# publish/plugin/ForgeSkin/ (ForgeSkin.Plugin.dll and its lang/) and publish/plugin/ForgeCore/ (the zip's plugins/ForgeCore/, the folder a mod manager installs),
# 5. the character preview's motion clips (Tools/Models/stage-skin-motions.ts), the tutorial's pictures when any is missing
# (Editor/scripts/docs-shots.mjs; the character pictures are taken on the humanoid model -Character names, the release's
# AvatarSample_D_Darkness, and a release with any picture still missing stops), the weapon gallery's images of every item
# from the installed game (Editor/scripts/gallery-thumbs.mjs) and `pnpm make` (Squirrel: ForgeSkin-<version> Setup.exe, ForgeSkin-<version>-full.nupkg, RELEASES; and the portable
# ForgeSkin-<version>-portable.zip, whose contents are checked), 6. <Output>/upload with every file to upload and
# upload.sha256. -SkipMake stops before step 5. -PluginOnly runs steps 1, 2 and 4 only (no backend, no installer): the
# development editor's plugin. The default Output is the folder the backend of a checkout
# reads its plugin from (Install.PluginSource), so a run also refreshes the development plugin. -BepInEx defaults to
# $env:GTFO_BEPINEX_PATH, else the Forge-Multiplayer r2modman profile.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ForgeCore,
    [string]$BepInEx,
    [string]$Output,
    [string]$Character,
    [switch]$SkipMake,
    [switch]$PluginOnly
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
Set-Location $root
if (-not $BepInEx) { $BepInEx = if ($env:GTFO_BEPINEX_PATH) { $env:GTFO_BEPINEX_PATH } else { Join-Path $env:APPDATA 'r2modmanPlus-local/GTFO/profiles/Forge-Multiplayer/BepInEx' } }
$ForgeCore = (Resolve-Path $ForgeCore).Path
$out = [System.IO.Path]::GetFullPath($(if ($Output) { $Output } else { Join-Path $root 'artifacts/forge-skin-release' }))
$skin = Join-Path $root 'Mods/ForgeSkin'
$editor = Join-Path $skin 'Editor'
$dotnet = Join-Path $out 'dotnet'
$zips = Join-Path $out 'thunderstore'
$publish = Join-Path $out 'publish'
$upload = Join-Path $out 'upload'
function Step([string]$name) { Write-Host "== $name" }
function Check([string]$what) { if ($LASTEXITCODE) { throw "$what failed (exit $LASTEXITCODE)" } }

# The editor's version is Editor/package.json; the plugin's is the ForgeSkin row of release.json, which set-package-version.ts
# writes into manifest.json and the code. Each is checked against its own source.
$version = (Get-Content (Join-Path $editor 'package.json') -Raw | ConvertFrom-Json).version
$plugin = (Get-Content (Join-Path $skin 'manifest.json') -Raw | ConvertFrom-Json).version_number
pnpm ts Tools/Forge/set-package-version.ts --check
Check 'plugin version check'
Write-Host "ForgeSkin editor $version, plugin $plugin -> $out"

Step 'plugin (Release)'
dotnet build Mods/ForgeSkin/ForgeSkin.csproj -c Release --artifacts-path $dotnet "-p:GTFOBepInExPath=$BepInEx" -v q --nologo --disable-build-servers
Check 'plugin build'

Step 'Thunderstore zips'
foreach ($dir in $zips, $upload) { if (Test-Path $dir) { Remove-Item -Recurse -Force $dir } }
New-Item -ItemType Directory -Force $zips | Out-Null
Copy-Item $ForgeCore $zips
python -E -X utf8 -B Mods/ForgeSkin/tools/package_release.py --artifacts-path $dotnet --output $zips
Check 'ForgeSkin package'
python -E -X utf8 -B Mods/Build/verify_release.py --packages $zips --artifacts-path $dotnet
Check 'verify_release'

if (-not $PluginOnly) {
    Step 'backend (self-contained single file)'
    if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
    $vanilla = Join-Path $skin 'Backend/vanilla-textures.json'
    dotnet run --project Mods/ForgeSkin/Backend/ForgeSkin.Backend.csproj -c Release --artifacts-path (Join-Path $out 'dotnet-backend') -v q -- --write-vanilla-textures $vanilla
    Check 'vanilla texture list'
    git diff --quiet -- $vanilla
    if ($LASTEXITCODE) { Write-Warning "$vanilla changed (new game revision): commit it with this release" }
    dotnet publish Mods/ForgeSkin/Backend/ForgeSkin.Backend.csproj -c Release --artifacts-path (Join-Path $out 'dotnet-backend') --self-contained -p:PublishSingleFile=true -o $publish -v q --nologo
    Check 'backend publish'
}

Step 'plugin the installer carries'
# Staged beside publish/ and swapped in whole, so a failed or interrupted run never leaves a half publish/plugin.
$staging = Join-Path $out 'plugin.staging'
if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
$pluginDir = New-Item -ItemType Directory -Force (Join-Path $staging 'ForgeSkin')
Copy-Item (Join-Path $dotnet 'bin/ForgeSkin/release/ForgeSkin.Plugin.dll') $pluginDir
Copy-Item -Recurse (Join-Path $dotnet 'bin/ForgeSkin/release/lang') $pluginDir
# The zip's plugins/ForgeCore/ (ForgeCore.dll with its lang/ and fonts/ beside it): the folder a mod manager installs as
# BepInEx/plugins/NAinfini-ForgeCore/ForgeCore/, which the editor compares file by file. The root files are the mod manager's own.
$unpacked = Join-Path $out 'forgecore.unpacked'
if (Test-Path $unpacked) { Remove-Item -Recurse -Force $unpacked }
Expand-Archive $ForgeCore $unpacked
Move-Item (Join-Path $unpacked 'plugins/ForgeCore') (Join-Path $staging 'ForgeCore')
Remove-Item -Recurse -Force $unpacked
foreach ($need in 'ForgeSkin/ForgeSkin.Plugin.dll', 'ForgeCore/ForgeCore.dll') {
    if (-not (Test-Path (Join-Path $staging $need))) { throw "staged plugin lacks $need" }
}
New-Item -ItemType Directory -Force $publish | Out-Null
$final = Join-Path $publish 'plugin'
if (Test-Path $final) { Remove-Item -Recurse -Force $final }
Move-Item $staging $final
Write-Host "  publish/plugin: ForgeSkin.Plugin.dll and lang/ + plugins/ForgeCore/ of $(Split-Path $ForgeCore -Leaf)"
if ($PluginOnly) { return }

$files = @(Get-ChildItem $zips -Filter 'NAinfini-ForgeSkin-*.zip')
if (-not $SkipMake) {
    Step 'pnpm make'
    $env:FORGESKIN_PUBLISH_DIR = $publish
    $env:FORGESKIN_BACKEND_EXE = Join-Path $publish 'ForgeSkin.Backend.exe'
    # The character preview's motion clips are not in Git: copied in from the local resources (the site's player actions and
    # ForgeEmote's clips) and checked against them.
    pnpm ts Tools/Models/stage-skin-motions.ts
    Check 'motion clips'
    Push-Location $editor
    try {
        pnpm install --frozen-lockfile
        Check 'pnpm install'
        # The tutorial's pictures are not in Git: make them (from the dev editor and the game's models) when any is missing.
        node scripts/docs-shots.mjs --check
        if ($LASTEXITCODE) {
            node scripts/docs-shots.mjs @(if ($Character) { '--character', (Resolve-Path $Character).Path })
            Check 'tutorial pictures'
            node scripts/docs-shots.mjs --check
            Check 'tutorial pictures (the character ones need -Character)'
        }
        # The gallery's images are not in Git either: made from the installed game every release, so they match its items.
        node scripts/gallery-thumbs.mjs
        Check 'gallery images'
        pnpm make
        Check 'pnpm make'
    } finally { Pop-Location }
    $made = Join-Path $editor 'out/make/squirrel.windows/x64'
    $files += @("ForgeSkin-$version Setup.exe", "ForgeSkin-$version-full.nupkg", 'RELEASES' | ForEach-Object { Get-Item (Join-Path $made $_) })

    Step 'portable zip'
    # The same app unzipped anywhere: no installer, no updates, data in GTFO_UserData beside ForgeSkin.exe. It ships no
    # data and no data-folder.json (the portable copy's choice of folder, written beside the exe when the user picks one).
    $portable = Join-Path $out "ForgeSkin-$version-portable.zip"
    if (Test-Path $portable) { Remove-Item $portable }
    [System.IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $editor 'out/ForgeSkin-win32-x64'), $portable, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    $zip = [System.IO.Compression.ZipFile]::OpenRead($portable)
    try { $entries = @($zip.Entries | ForEach-Object { $_.FullName -replace '\\', '/' }) } finally { $zip.Dispose() }
    $top = 'ForgeSkin-win32-x64/'
    foreach ($need in 'ForgeSkin.exe', 'resources/app.asar', 'resources/publish/ForgeSkin.Backend.exe', 'resources/publish/plugin/ForgeSkin/ForgeSkin.Plugin.dll', 'resources/publish/plugin/ForgeCore/ForgeCore.dll') {
        if ($entries -notcontains "$top$need") { throw "portable zip lacks $top$need" }
    }
    $stray = @($entries | Where-Object { $_ -notlike "$top*" -or $_ -match '/GTFO_UserData/|/Update\.exe$|/data-folder\.json$' })
    if ($stray) { throw "portable zip holds what it must not: $($stray -join ', ')" }
    Write-Host "  $($entries.Count) entries, $([math]::Round((Get-Item $portable).Length / 1MB, 1)) MB"
    $files += Get-Item $portable
}

Step 'upload'
New-Item -ItemType Directory -Force $upload | Out-Null
$lines = foreach ($file in $files) {
    Copy-Item $file.FullName $upload
    '{0}  {1}  {2} bytes' -f (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $file.Name, $file.Length
}
$lines | Set-Content (Join-Path $upload 'upload.sha256') -Encoding utf8NoBOM
$lines | ForEach-Object { Write-Host "  $_" }
if ($SkipMake) { Write-Host '  (-SkipMake: no installer, nupkg, RELEASES or portable zip)' }
Write-Host "Not uploaded. Upload steps: Mods/Release/README.md, ForgeSkin editor."
