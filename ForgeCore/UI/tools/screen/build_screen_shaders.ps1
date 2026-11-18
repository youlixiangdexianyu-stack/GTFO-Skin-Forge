#Requires -Version 7
# Builds ForgeUI's screen shaders (the power-on panel and the screen effect stack) with the game's Unity (2019.4.21f1) writes Assets/Shaders/*.shaderbundle and rewrites Native/PowerOnShader.Generated.cs and Native/ScreenEffectsShader.Generated.cs.
# Take artifacts/work/unity-lock.txt first. Run from the repository root:
#   pwsh Mods/ForgeCore/UI/tools/screen/build_screen_shaders.ps1 [-Work artifacts/work/uikit-screen/unity]
param(
    [string]$Work = 'artifacts/work/uikit-screen/unity',
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\2019.4.21f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../../tools/Unity/UnityBatch.ps1')
Invoke-ShaderBatch $Unity ([IO.Path]::GetFullPath($Work)) $PSScriptRoot ([IO.Path]::GetFullPath("$PSScriptRoot/../../Native")) "$PSScriptRoot/ScreenShaderBuild.cs" 'ScreenShaderBuild.Build'

