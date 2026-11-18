# Loads InteropSignature.cs for a PowerShell tool: `. <this file> -BepInEx <profile>/BepInEx`, then [ForgeTestInputs.InteropSignature]::Sha256(<dll>).
param([Parameter(Mandatory)][string]$BepInEx)
$cecil = Join-Path $BepInEx 'core/Mono.Cecil.dll'
Add-Type -LiteralPath $cecil
Add-Type -LiteralPath (Join-Path $PSScriptRoot 'InteropSignature.cs') -ReferencedAssemblies $cecil, netstandard, System.Runtime, System.Collections, System.Linq, System.Security.Cryptography
