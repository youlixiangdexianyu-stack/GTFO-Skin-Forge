<#
Offline load check for a set of Forge assemblies; never loads or runs them.

For every dll listed in -List, each type reference that points at another assembly of the same set must exist there
(namespace, nested name), each member reference on such a type must exist by name (method or field, searched up the
base-type chain), and every reference to a Forge* assembly must be answered by a dll of the set. Exit 1 lists every miss;
each one is what a TypeLoadException or MissingMethodException would be at load time. On success it prints one ASM <name> <version> line per dll.

    pwsh Mods/Build/check-assembly-refs.ps1 -List <text file with one dll path per line>
#>
param([Parameter(Mandatory)][string]$List)
$ErrorActionPreference = 'Stop'
$Paths = Get-Content -LiteralPath $List

$MT = [System.Reflection.Metadata.Ecma335.MetadataTokens]
function Get-Row($h) { [int]$MT::GetRowNumber([System.Reflection.Metadata.EntityHandle]$h) }

function Get-TypeKey($reader, $handle) {
    # Namespace.Name, with nested types joined by '+'.
    $e = [System.Reflection.Metadata.EntityHandle]$handle
    if ($e.Kind -eq 'TypeReference') {
        $t = $reader.GetTypeReference($MT::TypeReferenceHandle((Get-Row $e)))
        $name = $reader.GetString($t.Name); $ns = $reader.GetString($t.Namespace)
        if ($t.ResolutionScope.Kind -eq 'TypeReference') { return (Get-TypeKey $reader $t.ResolutionScope) + '+' + $name }
        return $(if ($ns) { "$ns.$name" } else { $name })
    }
    $d = $reader.GetTypeDefinition($MT::TypeDefinitionHandle((Get-Row $e)))
    $name = $reader.GetString($d.Name); $ns = $reader.GetString($d.Namespace)
    $outer = $d.GetDeclaringType()
    if (-not $outer.IsNil) { return (Get-TypeKey $reader $outer) + '+' + $name }
    return $(if ($ns) { "$ns.$name" } else { $name })
}

function Get-Scope($reader, $refAsm, $handle) {
    $t = $reader.GetTypeReference($MT::TypeReferenceHandle((Get-Row $handle)))
    while ($t.ResolutionScope.Kind -eq 'TypeReference') { $t = $reader.GetTypeReference($MT::TypeReferenceHandle((Get-Row $t.ResolutionScope))) }
    if ($t.ResolutionScope.Kind -eq 'AssemblyReference') { return $refAsm[(Get-Row $t.ResolutionScope)] }
    return $null
}
$set = @{}
foreach ($path in $Paths) {
    $pe = [System.Reflection.PortableExecutable.PEReader]::new([IO.File]::OpenRead($path))
    $r = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
    $refAsm = @{}
    foreach ($h in $r.AssemblyReferences) { $refAsm[(Get-Row $h)] = $r.GetString($r.GetAssemblyReference($h).Name) }
    $a = @{ Name = $r.GetString($r.GetAssemblyDefinition().Name); Pe = $pe; Reader = $r; RefAsm = $refAsm
            Version = ""; Types = [System.Collections.Generic.HashSet[string]]::new(); Members = [System.Collections.Generic.HashSet[string]]::new(); Bases = @{} }
    foreach ($h in $r.TypeDefinitions) {
        $key = Get-TypeKey $r $h
        [void]$a.Types.Add($key)
        $d = $r.GetTypeDefinition($h)
        foreach ($m in $d.GetMethods()) { [void]$a.Members.Add($key + '::' + $r.GetString($r.GetMethodDefinition($m).Name)) }
        foreach ($f in $d.GetFields()) { [void]$a.Members.Add($key + '::' + $r.GetString($r.GetFieldDefinition($f).Name)) }
        $b = $d.BaseType
        if ($b.IsNil) { continue }
        if ($b.Kind -eq 'TypeDefinition') { $a.Bases[$key] = @{ Scope = $a.Name; Key = (Get-TypeKey $r $b) } }
        elseif ($b.Kind -eq 'TypeReference') { $a.Bases[$key] = @{ Scope = (Get-Scope $r $refAsm $b); Key = (Get-TypeKey $r $b) } }
    }
    $a.Version = $r.GetAssemblyDefinition().Version.ToString()
    $set[$a.Name] = $a
}

function Test-Member($scope, $key, $member) {
    for ($i = 0; $i -lt 32 -and $scope -and $set.ContainsKey($scope); $i++) {
        $a = $set[$scope]
        if ($a.Members.Contains($key + '::' + $member)) { return $true }
        $base = $a.Bases[$key]
        if (-not $base) { return $false }
        $scope = $base.Scope; $key = $base.Key
    }
    # The chain leaves the set. A class that only derives from System.Object inherits nothing but Object's own members, so any
    # other name it lacks is missing; a base from Unity or the game (or ValueType, Enum) could supply it: not ours to judge.
    if ($key -eq 'System.Object') { return $member -in @('ToString', 'Equals', 'GetHashCode', 'GetType', 'Finalize', 'MemberwiseClone', 'ReferenceEquals') }
    return $true
}

$problems = [System.Collections.Generic.HashSet[string]]::new()
$checked = 0
foreach ($a in $set.Values) {
    $r = $a.Reader; $refAsm = $a.RefAsm
    foreach ($name in $refAsm.Values) {
        if ($name -like 'Forge*' -and -not $set.ContainsKey($name)) { [void]$problems.Add("$($a.Name): references assembly $name, which is not in the set") }
    }
    foreach ($h in $r.TypeReferences) {
        $scope = Get-Scope $r $refAsm $h
        if (-not $scope -or -not $set.ContainsKey($scope)) { continue }
        $key = Get-TypeKey $r $h; $checked++
        if (-not $set[$scope].Types.Contains($key)) { [void]$problems.Add("$($a.Name): type $key is missing from $scope") }
    }
    foreach ($h in $r.MemberReferences) {
        $m = $r.GetMemberReference($h)
        if ($m.Parent.Kind -ne 'TypeReference') { continue }
        $scope = Get-Scope $r $refAsm $m.Parent
        if (-not $scope -or -not $set.ContainsKey($scope)) { continue }
        $key = Get-TypeKey $r $m.Parent
        if (-not $set[$scope].Types.Contains($key)) { continue }
        $checked++
        $member = $r.GetString($m.Name)
        if (-not (Test-Member $scope $key $member)) { [void]$problems.Add("$($a.Name): member $key::$member is missing from $scope") }
    }
}
foreach ($a in $set.Values) { $a.Pe.Dispose() }
if ($problems.Count) { $problems | Sort-Object | ForEach-Object { [Console]::Error.WriteLine($_) }; exit 1 }
foreach ($a in ($set.Values | Sort-Object { $_.Name })) { "ASM $($a.Name) $($a.Version)" }
"assembly refs ok: $($set.Count) assemblies, $checked cross-assembly references resolved"




