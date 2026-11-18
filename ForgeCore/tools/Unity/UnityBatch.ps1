# The Unity batch step every Forge bundle build shares (ForgeCore's fonts and shaders, ForgeOrdnance's Carrier, Arsenal and Frames
# bundles, EnergyLab's model bundles). Dot-source it, then call Invoke-UnityBatch: it creates the project once, copies UnityBatch.cs and
# the build's scripts into its Assets/Editor, runs the one static method and returns Unity's exit code, which the caller checks with its
# own log handling. ForgeCore's shader builds call Invoke-ShaderBatch, which also checks the log.
function Invoke-UnityBatch([string]$Unity, [string]$Project, [string[]]$Scripts, [string]$Method, [string]$Log, [string]$CreateLog = "$Project.create.log") {
    if (!(Test-Path -LiteralPath (Join-Path $Project 'ProjectSettings'))) {
        New-Item -ItemType Directory -Force (Split-Path $Project -Parent) | Out-Null
        $proc = Start-Process -FilePath $Unity -WindowStyle Hidden -PassThru -ArgumentList @('-batchmode', '-nographics', '-quit', '-createProject', "`"$Project`"", '-logFile', "`"$CreateLog`"")
        $proc.WaitForExit()
        if ($proc.ExitCode -ne 0) { throw "Unity could not create the project; see $CreateLog" }
    }
    $editor = Join-Path $Project 'Assets/Editor'
    New-Item -ItemType Directory -Force $editor | Out-Null
    foreach ($script in @("$PSScriptRoot/UnityBatch.cs") + $Scripts) { Copy-Item -LiteralPath $script -Destination (Join-Path $editor (Split-Path $script -Leaf)) -Force }
    $proc = Start-Process -FilePath $Unity -WindowStyle Hidden -PassThru -ArgumentList @('-batchmode', '-nographics', '-quit', '-projectPath', "`"$Project`"", '-executeMethod', $Method, '-logFile', "`"$Log`"")
    $proc.WaitForExit()
    return $proc.ExitCode
}

# A ForgeCore shader build (screen, visual and trigger-zone shaders): the script reads FORGE_TOOLS (its folder) and FORGE_NATIVE (the Native
# folder it writes the reader into) and logs SHADER_BUILT per bundle. Fails on a Unity failure, a shader compile error or a log without
# this run's SHADER_BUILT lines, which it prints.
function Invoke-ShaderBatch([string]$Unity, [string]$Project, [string]$Tools, [string]$Native, [string]$Script, [string]$Method) {
    if (!(Test-Path -LiteralPath $Unity)) { throw "Missing Unity: $Unity" }
    $env:FORGE_TOOLS = $Tools
    $env:FORGE_NATIVE = $Native
    $log = "$Project.build.log"
    if ((Invoke-UnityBatch $Unity $Project @($Script) $Method $log) -ne 0) {
        Select-String -LiteralPath $log -Pattern 'error' | Select-Object -First 15 | ForEach-Object { Write-Host $_.Line }
        throw "Unity build failed; see $log"
    }
    $errors = @(Select-String -LiteralPath $log -Pattern 'Shader error in' -SimpleMatch)
    if ($errors.Count) { $errors | Select-Object -First 10 | ForEach-Object { Write-Host $_.Line }; throw "The shaders have compile errors; see $log" }
    $built = @(Select-String -LiteralPath $log -Pattern 'SHADER_BUILT' -SimpleMatch)
    if (!$built.Count) { throw "Unity did not run the build; see $log" }
    $built | ForEach-Object { Write-Host $_.Line }
}
