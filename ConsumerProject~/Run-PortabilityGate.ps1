[CmdletBinding()]
param(
    [string]$UnityPath,

    [ValidateRange(1, 1800)]
    [int]$AuthoringTimeoutSeconds = 300,

    [ValidateRange(5, 1800)]
    [int]$BuildTimeoutSeconds = 600,

    [switch]$ProjectOnly,

    [switch]$KeepStaging
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectPath = [System.IO.Path]::GetFullPath($PSScriptRoot)
$repositoryPath = [System.IO.Path]::GetFullPath((Join-Path $projectPath '..'))
$suitePath = Join-Path $repositoryPath 'Tools~\Invoke-PurrNetNetworkTestSuite.ps1'
if (-not (Test-Path -LiteralPath $suitePath -PathType Leaf)) {
    throw "Suite runner '$suitePath' does not exist."
}

if ([string]::IsNullOrWhiteSpace($UnityPath)) {
    $versionLine = Get-Content -LiteralPath (Join-Path $projectPath 'ProjectSettings\ProjectVersion.txt') |
        Where-Object { $_ -like 'm_EditorVersion:*' } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($versionLine)) {
        throw 'Consumer ProjectVersion.txt does not declare m_EditorVersion.'
    }

    $unityVersion = ($versionLine -split ':', 2)[1].Trim()
    $UnityPath = Join-Path $env:ProgramFiles "Unity\Hub\Editor\$unityVersion\Editor\Unity.exe"
}

$UnityPath = [System.IO.Path]::GetFullPath($UnityPath)
if (-not (Test-Path -LiteralPath $UnityPath -PathType Leaf)) {
    throw "Unity executable '$UnityPath' does not exist."
}

$logsPath = Join-Path $projectPath 'Logs'
[System.IO.Directory]::CreateDirectory($logsPath) | Out-Null
$authoringLogPath = Join-Path $logsPath 'consumer-portability-authoring.log'
$authoringArguments = @(
    '-batchmode',
    '-nographics',
    '-quit',
    '-projectPath', $projectPath,
    '-executeMethod',
    'ConsumerProject.FixtureAuthoring.Editor.ConsumerFixtureAuthoring.CreateFixtureAssets',
    '-logFile', $authoringLogPath
)
$authoringStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
$authoringStartInfo.FileName = $UnityPath
$authoringStartInfo.UseShellExecute = $false
$authoringStartInfo.CreateNoWindow = $true
foreach ($argument in $authoringArguments) {
    $authoringStartInfo.ArgumentList.Add($argument)
}

$authoringProcess = [System.Diagnostics.Process]::new()
$authoringProcess.StartInfo = $authoringStartInfo
if (-not $authoringProcess.Start()) {
    $authoringProcess.Dispose()
    throw "Could not start Unity fixture authoring process '$UnityPath'."
}

try {
    if (-not $authoringProcess.WaitForExit($AuthoringTimeoutSeconds * 1000)) {
        throw "Consumer fixture authoring exceeded its $AuthoringTimeoutSeconds second deadline. See '$authoringLogPath'."
    }

    $authoringExitCode = $authoringProcess.ExitCode
}
finally {
    if (-not $authoringProcess.HasExited) {
        $authoringProcess.Kill($true)
        if (-not $authoringProcess.WaitForExit(10000)) {
            Write-Warning "Unity fixture authoring process tree $($authoringProcess.Id) did not exit within 10 seconds after termination."
        }
    }

    $authoringProcess.Dispose()
}

if ($authoringExitCode -ne 0) {
    throw "Consumer fixture authoring failed with exit code $authoringExitCode. See '$authoringLogPath'."
}

$suiteParameters = @{
    ProjectPath = $projectPath
    UnityPath = $UnityPath
    TimeoutSeconds = 90
    BuildTimeoutSeconds = $BuildTimeoutSeconds
}
if ($ProjectOnly) {
    $suiteParameters['Scenarios'] = @(
        'Consumer.PortabilityCounter',
        'Consumer.ObserverMutationRejected'
    )
}

if ($KeepStaging) {
    $suiteParameters['KeepStaging'] = $true
}

& $suitePath @suiteParameters
