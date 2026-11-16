<#
.SYNOPSIS
  Run a list of csc invocations with Unity's own Roslyn on Windows.

.DESCRIPTION
  Used by windows_backend.py. Each job names a response file already written under %TEMP%;
  the response file is built by the Linux side with Windows paths, so this script only runs
  the compiler and reports raw output. Results are written as JSON.

  Nothing is installed and no Unity Editor process is touched.
#>
param(
    [Parameter(Mandatory = $true)][string]$JobPath,
    [Parameter(Mandatory = $true)][string]$ResultPath
)

$ErrorActionPreference = 'Stop'
$job = Get-Content -LiteralPath $JobPath -Raw | ConvertFrom-Json

$failed = @{}
$results = @()
foreach ($j in @($job.jobs)) {
    $skip = $null
    foreach ($d in @($j.deps)) {
        if ($failed.ContainsKey([string]$d)) { $skip = [string]$d; break }
    }
    if ($skip) {
        $results += [ordered]@{
            name    = [string]$j.name
            skipped = ('dependency failed: ' + $skip)
            exit    = 0
            output  = ''
            seconds = 0
        }
        $failed[[string]$j.name] = 1
        continue
    }

    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $text = (& $job.dotnet $job.csc -noconfig ('@' + $j.rsp) 2>&1 | Out-String)
    $code = $LASTEXITCODE
    $watch.Stop()
    $results += [ordered]@{
        name    = [string]$j.name
        skipped = $null
        exit    = $code
        output  = $text
        seconds = [math]::Round($watch.Elapsed.TotalSeconds, 2)
    }
    if ($code -ne 0) { $failed[[string]$j.name] = 1 }
}

$results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ResultPath -Encoding UTF8
