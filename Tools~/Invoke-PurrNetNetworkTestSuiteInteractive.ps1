[CmdletBinding()]
param(
    [string]$ProjectPath,

    [string]$UnityPath,

    [string]$PlayerPath,

    [string]$ArtifactsPath,

    [string[]]$Scenarios,

    [ValidateRange(5, 600)]
    [int]$TimeoutSeconds = 60,

    [ValidateRange(1, 10)]
    [int]$Repeat = 1,

    [switch]$ReusePlayer,

    [switch]$BuildInPlace,

    [switch]$KeepStaging,

    [ValidateRange(10, 100000)]
    [int]$ViewerTailLines = 500,

    [ValidateRange(250, 60000)]
    [int]$ViewerRefreshMilliseconds = 1000,

    [ValidateSet('Evidence', 'RawLog')]
    [string]$ViewerDefaultTab = 'RawLog'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-QuotedProcessArgument {
    param([Parameter(Mandatory = $true)][string]$Value)

    return '"' + $Value.Replace('"', '\"') + '"'
}

$suitePath = Join-Path $PSScriptRoot 'Invoke-PurrNetNetworkTestSuite.ps1'
$viewerPath = Join-Path $PSScriptRoot 'Show-PurrNetNetworkTestLogs.ps1'
foreach ($requiredPath in @($suitePath, $viewerPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required network-test script '$requiredPath' does not exist."
    }
}

$includedProjectPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\TestProject~'))
$selectedIncludedProject = $false
if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $currentPath = [System.IO.Path]::GetFullPath((Get-Location).Path)
    if (Test-Path -LiteralPath (Join-Path $currentPath 'Packages\manifest.json') -PathType Leaf) {
        $ProjectPath = $currentPath
    }
    elseif (Test-Path -LiteralPath (Join-Path $includedProjectPath 'Packages\manifest.json') -PathType Leaf) {
        $ProjectPath = $includedProjectPath
        $selectedIncludedProject = $true
    }
    else {
        throw 'No Unity project was found in the current directory or the repository TestProject~. Supply -ProjectPath explicitly.'
    }
}

$resolvedProjectPath = [System.IO.Path]::GetFullPath($ProjectPath)
if (-not (Test-Path -LiteralPath (Join-Path $resolvedProjectPath 'Packages\manifest.json') -PathType Leaf)) {
    throw "Unity project '$resolvedProjectPath' does not contain Packages\manifest.json."
}

$selectedIncludedProject = $resolvedProjectPath.Equals(
    $includedProjectPath,
    [System.StringComparison]::OrdinalIgnoreCase)

if ([string]::IsNullOrWhiteSpace($ArtifactsPath)) {
    $ArtifactsPath = Join-Path $resolvedProjectPath 'Artifacts\NetworkTests'
}

$resolvedArtifactsPath = [System.IO.Path]::GetFullPath($ArtifactsPath)
$runsPath = Join-Path $resolvedArtifactsPath 'Runs'
[System.IO.Directory]::CreateDirectory($runsPath) | Out-Null
$baselineRun = Get-ChildItem -LiteralPath $runsPath -Directory |
    Sort-Object -Property LastWriteTimeUtc, Name -Descending |
    Select-Object -First 1

$pwshPath = Join-Path $PSHOME 'pwsh.exe'
if (-not (Test-Path -LiteralPath $pwshPath -PathType Leaf)) {
    throw "PowerShell 7 executable '$pwshPath' does not exist."
}

$viewerArguments = @(
    '-NoProfile',
    '-STA',
    '-File', (ConvertTo-QuotedProcessArgument -Value $viewerPath),
    '-ArtifactsPath', (ConvertTo-QuotedProcessArgument -Value $resolvedArtifactsPath),
    '-FollowNewestRun',
    '-TailLines', $ViewerTailLines.ToString([System.Globalization.CultureInfo]::InvariantCulture),
    '-RefreshMilliseconds', $ViewerRefreshMilliseconds.ToString([System.Globalization.CultureInfo]::InvariantCulture),
    '-DefaultTab', $ViewerDefaultTab
)

if ($null -ne $baselineRun) {
    $viewerArguments += @(
        '-IgnoreRunPath',
        (ConvertTo-QuotedProcessArgument -Value $baselineRun.FullName)
    )
}

Start-Process `
    -FilePath $pwshPath `
    -ArgumentList ($viewerArguments -join ' ') `
    -WindowStyle Hidden | Out-Null

$suiteParameters = @{
    ProjectPath = $resolvedProjectPath
    ArtifactsPath = $resolvedArtifactsPath
    TimeoutSeconds = $TimeoutSeconds
    Repeat = $Repeat
}

if (-not [string]::IsNullOrWhiteSpace($UnityPath)) {
    $suiteParameters['UnityPath'] = $UnityPath
}

if (-not [string]::IsNullOrWhiteSpace($PlayerPath)) {
    $suiteParameters['PlayerPath'] = $PlayerPath
}

if ($PSBoundParameters.ContainsKey('Scenarios')) {
    $suiteParameters['Scenarios'] = $Scenarios
}

if ($ReusePlayer) {
    $suiteParameters['ReusePlayer'] = $true
}

# The repository's included project references this package through file:../.., which cannot
# survive a staging copy. Selecting it automatically therefore uses the coordinator's guarded
# in-place mode; explicitly selected projects retain the normal staging default.
if ($BuildInPlace -or $selectedIncludedProject) {
    $suiteParameters['BuildInPlace'] = $true
}

if ($KeepStaging) {
    $suiteParameters['KeepStaging'] = $true
}

Write-Host "Live viewer opened. Running the complete PurrNet matrix against '$resolvedProjectPath'."
& $suitePath @suiteParameters
