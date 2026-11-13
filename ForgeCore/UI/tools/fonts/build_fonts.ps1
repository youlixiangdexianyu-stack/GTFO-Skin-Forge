#Requires -Version 7
# Builds the kit's fonts (Rajdhani for Latin letters and digits, ForgeUI Symbols for the infinity sign, written by make_symbols.py
# with fontTools, Glow Sans SC Condensed for Chinese) into
# Assets/fonts/ForgeUI-fonts.bundle with the game's Unity (2019.4.21f1); the package script ships it in fonts/. The bundle and the
# font files stay out of Git; the licences beside it are tracked. Take artifacts/work/unity-lock.txt first. Run from the repository root:
#   pwsh Mods/ForgeCore/UI/tools/fonts/build_fonts.ps1 [-Latin <folder>] [-Chinese <folder>]
param(
    [string]$Latin = (Join-Path (git rev-parse --path-format=absolute --git-common-dir | Split-Path) 'Source/mods/fonts-rajdhani'),
    [string]$Chinese = (Join-Path (git rev-parse --path-format=absolute --git-common-dir | Split-Path) 'Source/mods/fonts-zh'),
    [string]$Work = 'artifacts/work/uikit-fonts/unity',
    [string]$Python = 'python',
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\2019.4.21f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
$tools = $PSScriptRoot
$out = [IO.Path]::GetFullPath((Join-Path $tools '../../../Assets/fonts'))
$symbols = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetFullPath($Work)) '../symbols'))
& $Python (Join-Path $tools 'make_symbols.py') $symbols
if ($LASTEXITCODE -ne 0) { throw 'make_symbols.py failed (it needs fontTools).' }
$fonts = @(
    (Join-Path $Latin 'Rajdhani-Medium.ttf'), (Join-Path $Latin 'Rajdhani-SemiBold.ttf'),
    (Join-Path $symbols 'ForgeUISymbols-Medium.ttf'), (Join-Path $symbols 'ForgeUISymbols-SemiBold.ttf'),
    (Join-Path $Chinese 'GlowSansSC-Condensed-Medium-common.ttf'), (Join-Path $Chinese 'GlowSansSC-Condensed-Bold-common.ttf')
)
foreach ($font in $fonts) {
    if (!(Test-Path -LiteralPath $font)) { throw "Missing font: $font" }
    $head = [IO.File]::ReadAllBytes($font)[0..3] -join ','
    if ($head -ne '0,1,0,0') { throw "$font is not a TrueType font." }
}
$Work = [IO.Path]::GetFullPath($Work)
if (!(Test-Path -LiteralPath $Unity)) { throw "Missing Unity: $Unity" }
if (!(Test-Path -LiteralPath (Join-Path $Work 'ProjectSettings'))) {
    New-Item -ItemType Directory -Force (Split-Path $Work -Parent) | Out-Null
    $proc = Start-Process -FilePath $Unity -WindowStyle Hidden -PassThru -ArgumentList @('-batchmode', '-nographics', '-quit', '-createProject', "`"$Work`"", '-logFile', "`"$Work.create.log`"")
    $proc.WaitForExit()
    if ($proc.ExitCode -ne 0) { throw "Unity could not create the project; see $Work.create.log" }
}
New-Item -ItemType Directory -Force (Join-Path $Work 'Assets/Editor') | Out-Null
Copy-Item -LiteralPath "$tools/FontBundleBuild.cs" -Destination (Join-Path $Work 'Assets/Editor/FontBundleBuild.cs') -Force
$env:FORGE_FONTS = $fonts -join ';'
$env:FORGE_OUT = $out
$log = "$Work.build.log"
$proc = Start-Process -FilePath $Unity -WindowStyle Hidden -PassThru -ArgumentList @('-batchmode', '-nographics', '-quit', '-projectPath', "`"$Work`"", '-executeMethod', 'FontBundleBuild.Build', '-logFile', "`"$log`"")
$proc.WaitForExit()
if ($proc.ExitCode -ne 0) {
    Select-String -LiteralPath $log -Pattern 'error|exception' | Select-Object -First 15 | ForEach-Object { Write-Host $_.Line }
    throw "Unity build failed; see $log"
}
if (!(Select-String -LiteralPath $log -Pattern 'FONTS_BUILT' -SimpleMatch -Quiet)) { throw "Unity did not run the build; see $log" }
Select-String -LiteralPath $log -Pattern 'FONTS_BUILT' -SimpleMatch | ForEach-Object { Write-Host $_.Line }
