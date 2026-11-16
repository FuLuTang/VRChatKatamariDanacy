<#
.SYNOPSIS
  Read-only probe of the Windows Unity project; prints a JSON manifest to stdout.

.DESCRIPTION
  Reads the Unity-generated *.csproj files for the requested assemblies and emits the
  compiler settings Unity uses for them (defines, language version), every reference DLL
  with its absolute path, and the ScriptAssemblies DLL substituted for each csproj
  ProjectReference. Nothing is written and no Unity process is touched.
#>
param(
    [string]$ProjectRoot = 'D:\VRChatKatamariDanacy\VRChatKatamariDanacyUnity',
    [string]$Assemblies = 'VRCKatamari.Runtime,VRCKatamari.Editor,VRCKatamari.Tests.EditMode'
)

$ErrorActionPreference = 'Stop'

function Resolve-Path2([string]$Base, [string]$Path) {
    if (-not $Path) { return $null }
    if ($Path.StartsWith('$(')) {
        $Path = $Path -replace '\$\(MSBuildThisFileDirectory\)\\?', ($Base + '\')
    }
    if (-not [System.IO.Path]::IsPathRooted($Path)) { $Path = Join-Path $Base $Path }
    return [System.IO.Path]::GetFullPath($Path)
}

$scriptAssemblies = Join-Path $ProjectRoot 'Library\ScriptAssemblies'
$refs = [ordered]@{}          # absolute path -> reference record
$assembliesOut = @()
$unityEditor = $null
$langVersion = '9.0'
$unityVersion = $null

foreach ($name in $Assemblies.Split(',')) {
    $name = $name.Trim()
    if (-not $name) { continue }
    $csproj = Join-Path $ProjectRoot ($name + '.csproj')
    if (-not (Test-Path -LiteralPath $csproj)) {
        $assembliesOut += [ordered]@{ name = $name; csproj = $csproj; missing_csproj = $true }
        continue
    }
    $x = [xml](Get-Content -LiteralPath $csproj)
    $defines = $null
    foreach ($g in $x.Project.PropertyGroup) {
        if ($g.DefineConstants) { $defines = $g.DefineConstants.InnerText; break }
    }
    if (-not $defines) {
        foreach ($g in $x.Project.PropertyGroup) {
            if ($g.DefineConstants) { $defines = [string]$g.DefineConstants; break }
        }
    }
    if ($x.Project.PropertyGroup.LangVersion) { $langVersion = [string]$x.Project.PropertyGroup.LangVersion }
    if ($x.Project.PropertyGroup.UnityVersion) { $unityVersion = [string]$x.Project.PropertyGroup.UnityVersion }

    $asmRefs = @()
    $asmProjectRefs = @()
    foreach ($g in $x.Project.ItemGroup) {
        foreach ($r in @($g.Reference)) {
            if (-not $r.HintPath) { continue }
            $full = Resolve-Path2 $ProjectRoot ([string]$r.HintPath)
            $exists = Test-Path -LiteralPath $full
            if (-not $refs.Contains($full)) {
                $refs[$full] = [ordered]@{ assembly = [string]$r.Include; path = $full; exists = $exists }
            }
            $asmRefs += [string]$r.Include
            if ($exists -and $full -match '^(.*\\Editor)\\Data\\Managed\\UnityEngine\\') {
                $unityEditor = $Matches[1]
            }
        }
        foreach ($pr in @($g.ProjectReference)) {
            if (-not $pr.Include) { continue }
            $projName = [System.IO.Path]::GetFileNameWithoutExtension([string]$pr.Include)
            $dll = Join-Path $scriptAssemblies ($projName + '.dll')
            $asmProjectRefs += [ordered]@{
                assembly = $projName
                path     = $dll
                exists   = (Test-Path -LiteralPath $dll)
            }
        }
    }
    $assembliesOut += [ordered]@{
        name               = $name
        csproj             = $csproj
        defines            = $defines
        references         = $asmRefs
        project_references = $asmProjectRefs
    }
}

# Unity's own compiler, used only for the optional Windows backend.
$unityCsc = $null
$unityRuntime = $null
if ($unityEditor) {
    $candidateCsc = Join-Path $unityEditor 'Data\DotNetSdkRoslyn\csc.dll'
    if (Test-Path -LiteralPath $candidateCsc) { $unityCsc = $candidateCsc }
    $candidateRuntime = Join-Path $unityEditor 'Data\NetCoreRuntime\dotnet.exe'
    if (Test-Path -LiteralPath $candidateRuntime) { $unityRuntime = $candidateRuntime }
}

$unityCscVersion = $null
if ($unityCsc -and $unityRuntime) {
    $unityCscVersion = (& $unityRuntime $unityCsc -version 2>&1 | Select-Object -First 1)
}

$dotnet = $null
$cmd = Get-Command dotnet -ErrorAction SilentlyContinue
if ($cmd) { $dotnet = $cmd.Source }

$manifest = [ordered]@{
    schema        = 1
    project_root  = $ProjectRoot
    unity_version = if ($unityVersion) { $unityVersion.Trim() } else { $null }
    unity_editor  = $unityEditor
    unity_csc     = $unityCsc
    unity_runtime = $unityRuntime
    unity_csc_version = $unityCscVersion
    dotnet        = $dotnet
    langversion   = $langVersion
    assemblies    = $assembliesOut
    references    = @($refs.Values)
}

$manifest | ConvertTo-Json -Depth 6 -Compress
