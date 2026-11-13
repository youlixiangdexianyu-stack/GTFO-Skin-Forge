#Requires -Version 7.0
# Builds the plugin and stages what the editor installs into a game profile, where the backend finds it
# (artifacts/forge-skin-release/publish/plugin/): ForgeSkin/ (the DLL and lang/) and ForgeCore/ (copied from the profile).
#   pwsh tools/stage-plugin.ps1 -BepInEx <profile>/BepInEx
[CmdletBinding()]
param([Parameter(Mandatory)][string]$BepInEx)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$core = Join-Path $BepInEx 'plugins/NAinfini-ForgeCore'
if (-not (Test-Path (Join-Path $core 'ForgeCore.dll'))) { throw "ForgeCore is not installed in this profile ($core); install NAinfini-ForgeCore with the mod manager first" }
dotnet build (Join-Path $root 'src/ForgeSkin.Plugin') -c Release "-p:GTFOBepInExPath=$BepInEx" --nologo -v q
if ($LASTEXITCODE) { throw "plugin build failed (exit $LASTEXITCODE)" }
$stage = Join-Path $root 'artifacts/forge-skin-release/publish/plugin'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
$built = Join-Path $root 'src/ForgeSkin.Plugin/bin/Release/net6.0'
New-Item -ItemType Directory -Force (Join-Path $stage 'ForgeSkin') | Out-Null
Copy-Item (Join-Path $built 'ForgeSkin.Plugin.dll') (Join-Path $stage 'ForgeSkin')
Copy-Item -Recurse (Join-Path $built 'lang') (Join-Path $stage 'ForgeSkin')
Copy-Item -Recurse $core (Join-Path $stage 'ForgeCore')
Write-Host "staged $stage"
