<#
.SYNOPSIS
  Copy the reference DLLs named in a probe manifest into one flat staging directory.

.DESCRIPTION
  Used by bootstrap.py. The staging directory lives under %TEMP% and is removed again after
  the archive has been copied back to Linux. Only files listed in the manifest are read and
  nothing inside the Unity project is written.
#>
param(
    [Parameter(Mandatory = $true)][string]$ManifestPath,
    [Parameter(Mandatory = $true)][string]$StageDir
)

$ErrorActionPreference = 'Stop'
$m = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Force -Path $StageDir | Out-Null

$done = @{}
$copied = 0
foreach ($r in $m.references) {
    $dest = Join-Path $StageDir ($r.assembly + '.dll')
    if ($done.ContainsKey($dest)) { continue }
    $done[$dest] = 1
    Copy-Item -LiteralPath $r.path -Destination $dest -Force
    $copied++
}
foreach ($a in $m.assemblies) {
    foreach ($pr in @($a.project_references)) {
        $dest = Join-Path $StageDir ($pr.assembly + '.dll')
        if ($done.ContainsKey($dest)) { continue }
        $done[$dest] = 1
        if (Test-Path -LiteralPath $pr.path) {
            Copy-Item -LiteralPath $pr.path -Destination $dest -Force
            $copied++
        }
    }
}
Write-Output ("STAGED " + $copied)
