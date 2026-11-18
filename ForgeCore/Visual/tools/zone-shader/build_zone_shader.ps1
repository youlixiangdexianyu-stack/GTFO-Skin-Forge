#Requires -Version 7
# Builds the trigger-zone shader with the game's Unity (2019.4.21f1): writes Assets/Shaders/forgevisual-zone-shader.shaderbundle and rewrites Native/ZoneShader.Generated.cs.
# Take artifacts/work/unity-lock.txt in the main checkout first. Run from the repository root:
#   pwsh Mods/ForgeCore/Visual/tools/zone-shader/build_zone_shader.ps1 [-Work artifacts/work/forgevisual-zone-shader/unity]
param(
    [string]$Work = 'artifacts/work/forgevisual-zone-shader/unity',
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\2019.4.21f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../../tools/Unity/UnityBatch.ps1')
Invoke-ShaderBatch $Unity ([IO.Path]::GetFullPath($Work)) $PSScriptRoot ([IO.Path]::GetFullPath("$PSScriptRoot/../../Native")) "$PSScriptRoot/TriggerZoneShaderBuild.cs" 'TriggerZoneShaderBuild.Build'
