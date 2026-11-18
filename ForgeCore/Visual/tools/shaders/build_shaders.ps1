#Requires -Version 7
# Builds ForgeVisual's particle, marker, outline, procedural and toon shaders with the game's Unity (2019.4.21f1), writes Assets/Shaders/forgevisual-shaders.shaderbundle and rewrites Native/ShaderBundle.Generated.cs.
# Take artifacts/work/unity-lock.txt in the main checkout first. Run from the repository root:
#   pwsh Mods/ForgeCore/Visual/tools/shaders/build_shaders.ps1 [-Work artifacts/work/forgevisual-shaders/unity]
param(
    [string]$Work = 'artifacts/work/forgevisual-shaders/unity',
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\2019.4.21f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../../tools/Unity/UnityBatch.ps1')
Invoke-ShaderBatch $Unity ([IO.Path]::GetFullPath($Work)) $PSScriptRoot ([IO.Path]::GetFullPath("$PSScriptRoot/../../Native")) "$PSScriptRoot/ForgeVisualShaderBuild.cs" 'ForgeVisualShaderBuild.Build'
