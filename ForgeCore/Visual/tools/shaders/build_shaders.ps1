#Requires -Version 7
# Builds ForgeVisual's particle, marker and outline shaders with the game's Unity (2019.4.21f1) writes Assets/Shaders/*.shaderbundle and rewrites Native/ShaderBundle.Generated.cs.
# Take artifacts/work/unity-lock.txt in the main checkout first. Run from the repository root:
#   pwsh Mods/ForgeCore/Visual/tools/shaders/build_shaders.ps1 [-Work artifacts/work/forgevisual-shaders/unity]
param(
    [string]$Work = 'artifacts/work/forgevisual-shaders/unity',
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\2019.4.21f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
$tools = $PSScriptRoot
$native = Join-Path (Split-Path (Split-Path $tools -Parent) -Parent) 'Native'
$Work = [IO.Path]::GetFullPath($Work)
if (!(Test-Path -LiteralPath $Unity)) { throw "Missing Unity: $Unity" }
if (!(Test-Path -LiteralPath (Join-Path $Work 'ProjectSettings'))) {
    New-Item -ItemType Directory -Force (Split-Path $Work -Parent) | Out-Null
    $proc = Start-Process -FilePath $Unity -WindowStyle Hidden -PassThru -ArgumentList @('-batchmode', '-nographics', '-quit', '-createProject', "`"$Work`"", '-logFile', "`"$Work.create.log`"")
    $proc.WaitForExit()
    if ($proc.ExitCode -ne 0) { throw "Unity could not create the project; see $Work.create.log" }
}
New-Item -ItemType Directory -Force (Join-Path $Work 'Assets/Editor') | Out-Null
Copy-Item -LiteralPath "$tools/ShaderBuild.cs" -Destination (Join-Path $Work 'Assets/Editor/ForgeVisualShaderBuild.cs') -Force
$env:FORGE_TOOLS = $tools
$env:FORGE_NATIVE = $native
$log = "$Work.build.log"
$proc = Start-Process -FilePath $Unity -WindowStyle Hidden -PassThru -ArgumentList @('-batchmode', '-nographics', '-quit', '-projectPath', "`"$Work`"", '-executeMethod', 'ForgeVisualShaderBuild.Build', '-logFile', "`"$log`"")
$proc.WaitForExit()
if ($proc.ExitCode -ne 0) {
    Select-String -LiteralPath $log -Pattern 'error' | Select-Object -First 15 | ForEach-Object { Write-Host $_.Line }
    throw "Unity build failed; see $log"
}
if (Select-String -LiteralPath $log -Pattern 'Shader error in' -SimpleMatch -Quiet) { Select-String -LiteralPath $log -Pattern 'Shader error in' -SimpleMatch | Select-Object -First 10 | ForEach-Object { Write-Host $_.Line }; throw "The shaders have compile errors; see $log" }
if (!(Select-String -LiteralPath $log -Pattern 'SHADER_BUILT' -SimpleMatch -Quiet)) { throw "Unity did not run the build; see $log" }
Select-String -LiteralPath $log -Pattern 'SHADER_BUILT' -SimpleMatch | ForEach-Object { Write-Host $_.Line }
