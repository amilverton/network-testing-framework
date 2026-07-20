[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectPath,

    [string]$UnityPath,

    [string]$PlayerPath,

    [string]$ArtifactsPath,

    [string[]]$Scenarios = @(
        'Harness.SustainedPacketStream',
        'Harness.RpcRouting',
        'Harness.CrossPlayerDamage',
        'Harness.LateJoinState',
        'Harness.OwnerNetworkTransform',
        'Harness.OwnershipTransfer',
        'Harness.SyncListOrder',
        'Harness.InventoryTransfer'
    ),

    [ValidateRange(5, 600)]
    [int]$TimeoutSeconds = 60,

    [ValidateRange(1, 10)]
    [int]$Repeat = 1,

    [switch]$ReusePlayer,

    [switch]$BuildInPlace,

    [switch]$KeepStaging,

    [switch]$OpenViewer
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$coordinatorPath = Join-Path $PSScriptRoot 'Invoke-PurrNetNetworkTests.ps1'
if (-not (Test-Path -LiteralPath $coordinatorPath -PathType Leaf)) {
    throw "Network test coordinator '$coordinatorPath' does not exist."
}

$pwshPath = Join-Path $PSHOME 'pwsh.exe'
if (-not (Test-Path -LiteralPath $pwshPath -PathType Leaf)) {
    throw "PowerShell 7 executable '$pwshPath' does not exist."
}

$results = [System.Collections.Generic.List[object]]::new()
$reusePlayer = $ReusePlayer.IsPresent

for ($iteration = 1; $iteration -le $Repeat; $iteration++) {
    foreach ($scenario in $Scenarios) {
        $arguments = @(
            '-NoProfile',
            '-File', $coordinatorPath,
            '-ProjectPath', $ProjectPath,
            '-Scenario', $scenario,
            '-TimeoutSeconds', [string]$TimeoutSeconds
        )

        if (-not [string]::IsNullOrWhiteSpace($UnityPath)) {
            $arguments += @('-UnityPath', $UnityPath)
        }

        if (-not [string]::IsNullOrWhiteSpace($PlayerPath)) {
            $arguments += @('-PlayerPath', $PlayerPath)
        }

        if (-not [string]::IsNullOrWhiteSpace($ArtifactsPath)) {
            $arguments += @('-ArtifactsPath', $ArtifactsPath)
        }

        if ($reusePlayer) {
            $arguments += '-ReusePlayer'
        }

        if ($BuildInPlace) {
            $arguments += '-BuildInPlace'
        }

        if ($KeepStaging) {
            $arguments += '-KeepStaging'
        }

        if ($OpenViewer) {
            $arguments += '-OpenViewer'
        }

        $output = & $pwshPath @arguments 2>&1
        if ($LASTEXITCODE -ne 0) {
            $failureText = [string]::Join([Environment]::NewLine, [string[]]$output)
            throw "Scenario '$scenario' failed on suite iteration $iteration.`n$failureText"
        }

        $parsed = ([string]::Join([Environment]::NewLine, [string[]]$output)) | ConvertFrom-Json
        $results.Add([pscustomobject]@{
            iteration = $iteration
            scenarioId = $parsed.scenarioId
            runId = $parsed.runId
            stateRevision = $parsed.stateRevision
            artifactsPath = $parsed.artifactsPath
        })
        $reusePlayer = $true
    }
}

[ordered]@{
    status = 'passed'
    scenarioCount = $Scenarios.Count
    repeatCount = $Repeat
    runCount = $results.Count
    results = $results
} | ConvertTo-Json -Depth 8
