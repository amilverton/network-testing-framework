[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectPath,

    [string]$UnityPath,

    [string]$PlayerPath,

    [string]$ArtifactsPath,

    [string]$Scenario = 'Harness.InventoryTransfer',

    [ValidateRange(5, 600)]
    [int]$TimeoutSeconds = 45,

    [switch]$ReusePlayer,

    [switch]$BuildInPlace,

    [switch]$KeepStaging,

    [switch]$OpenViewer
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-AbsolutePath {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [System.IO.Path]::GetFullPath($Path)
}

function Resolve-UnityEditorPath {
    param(
        [Parameter(Mandatory = $true)][string]$ResolvedProjectPath,
        [string]$RequestedUnityPath
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedUnityPath)) {
        $resolvedRequestedPath = Get-AbsolutePath -Path $RequestedUnityPath
        if (-not (Test-Path -LiteralPath $resolvedRequestedPath -PathType Leaf)) {
            throw "Unity executable '$resolvedRequestedPath' does not exist."
        }

        return $resolvedRequestedPath
    }

    $projectVersionPath = Join-Path $ResolvedProjectPath 'ProjectSettings\ProjectVersion.txt'
    if (-not (Test-Path -LiteralPath $projectVersionPath -PathType Leaf)) {
        throw "Unity version file '$projectVersionPath' does not exist."
    }

    $versionLine = Get-Content -LiteralPath $projectVersionPath | Where-Object { $_ -like 'm_EditorVersion:*' } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($versionLine)) {
        throw "Unity version file '$projectVersionPath' does not contain m_EditorVersion."
    }

    $version = ($versionLine -split ':', 2)[1].Trim()
    $candidate = Join-Path $env:ProgramFiles "Unity\Hub\Editor\$version\Editor\Unity.exe"
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Unity $version is not installed at '$candidate'. Supply -UnityPath explicitly."
    }

    return $candidate
}

function Assert-ProjectIsNotOpen {
    param([Parameter(Mandatory = $true)][string]$ResolvedProjectPath)

    $unityProcesses = Get-CimInstance Win32_Process -Filter "Name = 'Unity.exe'" -ErrorAction SilentlyContinue
    foreach ($unityProcess in $unityProcesses) {
        if ([string]::IsNullOrWhiteSpace($unityProcess.CommandLine)) {
            continue
        }

        if ($unityProcess.CommandLine.IndexOf($ResolvedProjectPath, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Unity already has project '$ResolvedProjectPath' open. Use staged build mode or close that Editor."
        }
    }
}

function Copy-StagingProject {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)][string]$ResolvedArtifactsPath
    )

    [System.IO.Directory]::CreateDirectory($DestinationPath) | Out-Null

    $excludedDirectories = @(
        (Join-Path $SourcePath 'Library'),
        (Join-Path $SourcePath 'Temp'),
        (Join-Path $SourcePath 'Logs'),
        (Join-Path $SourcePath 'obj'),
        (Join-Path $SourcePath 'Build'),
        (Join-Path $SourcePath 'Builds'),
        (Join-Path $SourcePath '.git')
    )

    if ($ResolvedArtifactsPath.StartsWith($SourcePath + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        $excludedDirectories += $ResolvedArtifactsPath
    }

    $robocopyArguments = @(
        $SourcePath,
        $DestinationPath,
        '/E',
        '/NFL',
        '/NDL',
        '/NJH',
        '/NJS',
        '/NP',
        '/R:2',
        '/W:1',
        '/XD'
    ) + $excludedDirectories + @('/XF', '*.csproj', '*.sln', '*.suo', '*.user')

    & robocopy @robocopyArguments
    $robocopyExitCode = $LASTEXITCODE
    if ($robocopyExitCode -ge 8) {
        throw "Staging project copy failed with robocopy exit code $robocopyExitCode."
    }
}

function Invoke-NetworkTestPlayerBuild {
    param(
        [Parameter(Mandatory = $true)][string]$ResolvedUnityPath,
        [Parameter(Mandatory = $true)][string]$BuildProjectPath,
        [Parameter(Mandatory = $true)][string]$ResolvedPlayerPath,
        [Parameter(Mandatory = $true)][string]$BuildLogPath
    )

    $playerDirectory = Split-Path -Parent $ResolvedPlayerPath
    $buildReceiptPath = $ResolvedPlayerPath + '.build.json'

    if (Test-Path -LiteralPath $playerDirectory -PathType Container) {
        $existingEntries = @(Get-ChildItem -LiteralPath $playerDirectory -Force)
        if ($existingEntries.Count -gt 0) {
            if (-not (Test-Path -LiteralPath $buildReceiptPath -PathType Leaf)) {
                throw "Refusing to clean non-empty Player directory '$playerDirectory' because it has no harness build receipt. Supply a dedicated -PlayerPath directory."
            }

            $existingReceipt = Get-Content -LiteralPath $buildReceiptPath -Raw | ConvertFrom-Json
            if ($existingReceipt.SchemaVersion -ne 1) {
                throw "Refusing to clean Player directory '$playerDirectory' because its harness build receipt has unsupported schema version '$($existingReceipt.SchemaVersion)'."
            }

            $resolvedPlayerDirectory = (Resolve-Path -LiteralPath $playerDirectory).Path
            $playerDirectoryRoot = [System.IO.Path]::GetPathRoot($resolvedPlayerDirectory)
            if ($resolvedPlayerDirectory.TrimEnd('\') -eq $playerDirectoryRoot.TrimEnd('\')) {
                throw "Refusing to clean filesystem root '$resolvedPlayerDirectory'."
            }

            Remove-Item -LiteralPath $resolvedPlayerDirectory -Recurse -Force
        }
    }

    [System.IO.Directory]::CreateDirectory($playerDirectory) | Out-Null

    $unityArgumentParts = @(
        '-batchmode',
        '-quit',
        '-projectPath', (ConvertTo-QuotedProcessArgument -Value $BuildProjectPath),
        '-buildTarget', 'StandaloneWindows64',
        '-executeMethod', 'Amilverton.PurrNetTesting.Editor.NetworkTestPlayerBuilder.BuildFromCommandLine',
        '-networkTestBuildPath', (ConvertTo-QuotedProcessArgument -Value $ResolvedPlayerPath),
        '-logFile', (ConvertTo-QuotedProcessArgument -Value $BuildLogPath)
    )

    $unityProcess = Start-Process `
        -FilePath $ResolvedUnityPath `
        -ArgumentList ($unityArgumentParts -join ' ') `
        -PassThru `
        -WindowStyle Hidden
    $unityProcess.WaitForExit()
    $unityExitCode = $unityProcess.ExitCode
    if ($unityExitCode -ne 0) {
        throw "Unity Player build failed with exit code $unityExitCode. See '$BuildLogPath'."
    }

    if (-not (Test-Path -LiteralPath $ResolvedPlayerPath -PathType Leaf)) {
        throw "Unity returned success but did not create Player '$ResolvedPlayerPath'. See '$BuildLogPath'."
    }

    if (-not (Test-Path -LiteralPath $buildReceiptPath -PathType Leaf)) {
        throw "Unity returned success but did not create build receipt '$buildReceiptPath'."
    }
}

function Get-FreeUdpPort {
    $udpClient = [System.Net.Sockets.UdpClient]::new(0)
    try {
        $endpoint = [System.Net.IPEndPoint]$udpClient.Client.LocalEndPoint
        return $endpoint.Port
    }
    finally {
        $udpClient.Dispose()
    }
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object]$Value
    )

    $json = $Value | ConvertTo-Json -Depth 12
    [System.IO.File]::WriteAllText($Path, $json, [System.Text.UTF8Encoding]::new($false))
}

function ConvertTo-QuotedProcessArgument {
    param([Parameter(Mandatory = $true)][string]$Value)

    return '"' + $Value.Replace('"', '\"') + '"'
}

function Start-NetworkTestViewer {
    param([Parameter(Mandatory = $true)][string]$ResolvedRunPath)

    $viewerPath = Join-Path $PSScriptRoot 'Show-PurrNetNetworkTestLogs.ps1'
    if (-not (Test-Path -LiteralPath $viewerPath -PathType Leaf)) {
        throw "Network test viewer '$viewerPath' does not exist."
    }

    $pwshPath = Join-Path $PSHOME 'pwsh.exe'
    if (-not (Test-Path -LiteralPath $pwshPath -PathType Leaf)) {
        throw "PowerShell 7 executable '$pwshPath' does not exist."
    }

    $viewerArgumentParts = @(
        '-NoProfile',
        '-STA',
        '-File', (ConvertTo-QuotedProcessArgument -Value $viewerPath),
        '-RunPath', (ConvertTo-QuotedProcessArgument -Value $ResolvedRunPath)
    )

    Start-Process `
        -FilePath $pwshPath `
        -ArgumentList ($viewerArgumentParts -join ' ') `
        -WindowStyle Hidden | Out-Null
}

function Start-NetworkTestRole {
    param(
        [Parameter(Mandatory = $true)][string]$ResolvedPlayerPath,
        [Parameter(Mandatory = $true)][string]$RunId,
        [Parameter(Mandatory = $true)][string]$ScenarioId,
        [Parameter(Mandatory = $true)][ValidateSet('Server', 'OwnerClient', 'ObserverClient')][string]$Role,
        [Parameter(Mandatory = $true)][string]$ConfigurationPath,
        [Parameter(Mandatory = $true)][string]$ReadyPath,
        [Parameter(Mandatory = $true)][string]$ResultPath,
        [Parameter(Mandatory = $true)][string]$LogPath
    )

    $argumentParts = @(
        '-batchmode',
        '-nographics',
        '-logFile', (ConvertTo-QuotedProcessArgument -Value $LogPath),
        '-networkTestRunId', (ConvertTo-QuotedProcessArgument -Value $RunId),
        '-networkTestScenario', (ConvertTo-QuotedProcessArgument -Value $ScenarioId),
        '-networkTestRole', $Role,
        '-networkTestConfig', (ConvertTo-QuotedProcessArgument -Value $ConfigurationPath),
        '-networkTestReady', (ConvertTo-QuotedProcessArgument -Value $ReadyPath),
        '-networkTestResult', (ConvertTo-QuotedProcessArgument -Value $ResultPath),
        '-networkTestLog', (ConvertTo-QuotedProcessArgument -Value $LogPath)
    )

    $argumentString = $argumentParts -join ' '
    return Start-Process -FilePath $ResolvedPlayerPath -ArgumentList $argumentString -PassThru -WindowStyle Hidden
}

function Wait-ForRoleArtifact {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)][string]$ArtifactPath,
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][datetime]$DeadlineUtc
    )

    while ([DateTime]::UtcNow -lt $DeadlineUtc) {
        if (Test-Path -LiteralPath $ArtifactPath -PathType Leaf) {
            return
        }

        if ($Process.HasExited) {
            throw "$Description was not published because process $($Process.Id) exited with code $($Process.ExitCode)."
        }

        Start-Sleep -Milliseconds 100
    }

    throw "Reached the global run deadline while waiting for $Description at '$ArtifactPath'."
}

function Read-ValidatedReadyReport {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedRunId,
        [Parameter(Mandatory = $true)][string]$ExpectedScenario,
        [Parameter(Mandatory = $true)][string]$ExpectedRole
    )

    $report = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($report.schemaVersion -ne 1) {
        throw "Ready report '$Path' has unsupported schema version '$($report.schemaVersion)'."
    }

    if ($report.runId -ne $ExpectedRunId -or $report.scenarioId -ne $ExpectedScenario -or $report.role -ne $ExpectedRole) {
        throw "Ready report '$Path' does not match run '$ExpectedRunId', scenario '$ExpectedScenario', role '$ExpectedRole'."
    }

    return $report
}

function ConvertTo-StableFactsJson {
    param([Parameter(Mandatory = $true)][object]$Facts)

    $orderedFacts = [ordered]@{}
    foreach ($property in ($Facts.PSObject.Properties | Sort-Object -Property Name)) {
        $orderedFacts[$property.Name] = $property.Value
    }

    return $orderedFacts | ConvertTo-Json -Depth 12 -Compress
}

function Assert-FinalReports {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary]$ReportPaths,
        [Parameter(Mandatory = $true)][string]$ExpectedRunId,
        [Parameter(Mandatory = $true)][string]$ExpectedScenario
    )

    $reports = [ordered]@{}
    foreach ($role in @('Server', 'OwnerClient', 'ObserverClient')) {
        $path = [string]$ReportPaths[$role]
        $report = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json

        if ($report.schemaVersion -ne 1) {
            throw "Result '$path' has unsupported schema version '$($report.schemaVersion)'."
        }

        if ($report.runId -ne $ExpectedRunId -or $report.scenarioId -ne $ExpectedScenario -or $report.role -ne $role) {
            throw "Result '$path' does not match run '$ExpectedRunId', scenario '$ExpectedScenario', role '$role'."
        }

        if ($report.status -ne 'passed') {
            throw "Role '$role' failed: $($report.failure). See '$($report.logPath)'."
        }

        $reports[$role] = $report
    }

    $serverRevision = [int]$reports.Server.stateRevision
    $serverFacts = ConvertTo-StableFactsJson -Facts $reports.Server.facts

    foreach ($role in @('OwnerClient', 'ObserverClient')) {
        if ([int]$reports[$role].stateRevision -ne $serverRevision) {
            throw "Role '$role' reported revision '$($reports[$role].stateRevision)' but Server reported '$serverRevision'."
        }

        $roleFacts = ConvertTo-StableFactsJson -Facts $reports[$role].facts
        if ($roleFacts -ne $serverFacts) {
            throw "Role '$role' facts do not match authoritative Server facts."
        }
    }

    return $reports
}

function Stop-NetworkTestProcesses {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[System.Diagnostics.Process]]$Processes
    )

    foreach ($process in $Processes) {
        if ($null -eq $process) {
            continue
        }

        try {
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force
            }
        }
        catch {
            Write-Warning "Failed to stop network test process $($process.Id): $($_.Exception.Message)"
        }
        finally {
            $process.Dispose()
        }
    }
}

function Remove-ValidatedStagingDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$StagingRunPath,
        [Parameter(Mandatory = $true)][string]$StagingRootPath
    )

    if (-not (Test-Path -LiteralPath $StagingRunPath -PathType Container)) {
        return
    }

    $resolvedRunPath = (Resolve-Path -LiteralPath $StagingRunPath).Path
    $resolvedRootPath = (Resolve-Path -LiteralPath $StagingRootPath).Path
    $requiredPrefix = $resolvedRootPath.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

    if (-not $resolvedRunPath.StartsWith($requiredPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove staging path '$resolvedRunPath' outside '$resolvedRootPath'."
    }

    Remove-Item -LiteralPath $resolvedRunPath -Recurse -Force
}

$resolvedProjectPath = Get-AbsolutePath -Path $ProjectPath
if (-not (Test-Path -LiteralPath $resolvedProjectPath -PathType Container)) {
    throw "Unity project '$resolvedProjectPath' does not exist."
}

if ([string]::IsNullOrWhiteSpace($ArtifactsPath)) {
    $ArtifactsPath = Join-Path $resolvedProjectPath 'Artifacts\NetworkTests'
}

$resolvedArtifactsPath = Get-AbsolutePath -Path $ArtifactsPath
[System.IO.Directory]::CreateDirectory($resolvedArtifactsPath) | Out-Null

if ([string]::IsNullOrWhiteSpace($PlayerPath)) {
    $PlayerPath = Join-Path $resolvedArtifactsPath 'Player\PurrNetNetworkTestPlayer.exe'
}

$resolvedPlayerPath = Get-AbsolutePath -Path $PlayerPath
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [System.Guid]::NewGuid().ToString('N').Substring(0, 8)
$runPath = Join-Path $resolvedArtifactsPath "Runs\$runId"
[System.IO.Directory]::CreateDirectory($runPath) | Out-Null

if ($OpenViewer) {
    Start-NetworkTestViewer -ResolvedRunPath $runPath
}

$stagingRootPath = Join-Path $resolvedArtifactsPath 'Staging'
$stagingRunPath = Join-Path $stagingRootPath $runId
$stagingProjectPath = Join-Path $stagingRunPath 'Project'
$childProcesses = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$exitCode = 0
$finalOutput = $null

try {
    if (-not $ReusePlayer) {
        $resolvedUnityPath = Resolve-UnityEditorPath -ResolvedProjectPath $resolvedProjectPath -RequestedUnityPath $UnityPath
        $buildProjectPath = $resolvedProjectPath

        if ($BuildInPlace) {
            Assert-ProjectIsNotOpen -ResolvedProjectPath $resolvedProjectPath
        }
        else {
            Copy-StagingProject -SourcePath $resolvedProjectPath -DestinationPath $stagingProjectPath -ResolvedArtifactsPath $resolvedArtifactsPath
            $buildProjectPath = $stagingProjectPath
        }

        $buildLogPath = Join-Path $runPath 'build.log'
        Invoke-NetworkTestPlayerBuild `
            -ResolvedUnityPath $resolvedUnityPath `
            -BuildProjectPath $buildProjectPath `
            -ResolvedPlayerPath $resolvedPlayerPath `
            -BuildLogPath $buildLogPath
    }

    if (-not (Test-Path -LiteralPath $resolvedPlayerPath -PathType Leaf)) {
        throw "Network test Player '$resolvedPlayerPath' does not exist. Build it or omit -ReusePlayer."
    }

    $configurationPath = Join-Path $runPath 'config.json'
    $port = Get-FreeUdpPort
    $configuration = [ordered]@{
        schemaVersion = 1
        runId = $runId
        scenarioId = $Scenario
        address = '127.0.0.1'
        port = $port
        timeoutSeconds = $TimeoutSeconds
    }
    Write-JsonFile -Path $configurationPath -Value $configuration

    $paths = [ordered]@{}
    foreach ($role in @('Server', 'OwnerClient', 'ObserverClient')) {
        $filePrefix = switch ($role) {
            'Server' { 'server' }
            'OwnerClient' { 'owner' }
            'ObserverClient' { 'observer' }
        }

        $paths[$role] = [ordered]@{
            Ready = Join-Path $runPath "$filePrefix.ready.json"
            Result = Join-Path $runPath "$filePrefix.result.json"
            Log = Join-Path $runPath "$filePrefix.log"
        }
    }

    $runDeadlineUtc = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)

    $serverProcess = Start-NetworkTestRole `
        -ResolvedPlayerPath $resolvedPlayerPath `
        -RunId $runId `
        -ScenarioId $Scenario `
        -Role 'Server' `
        -ConfigurationPath $configurationPath `
        -ReadyPath $paths.Server.Ready `
        -ResultPath $paths.Server.Result `
        -LogPath $paths.Server.Log
    $childProcesses.Add($serverProcess)

    Wait-ForRoleArtifact -Process $serverProcess -ArtifactPath $paths.Server.Ready -Description 'Server readiness' -DeadlineUtc $runDeadlineUtc
    Read-ValidatedReadyReport -Path $paths.Server.Ready -ExpectedRunId $runId -ExpectedScenario $Scenario -ExpectedRole 'Server' | Out-Null

    foreach ($clientRole in @('OwnerClient', 'ObserverClient')) {
        $clientProcess = Start-NetworkTestRole `
            -ResolvedPlayerPath $resolvedPlayerPath `
            -RunId $runId `
            -ScenarioId $Scenario `
            -Role $clientRole `
            -ConfigurationPath $configurationPath `
            -ReadyPath $paths[$clientRole].Ready `
            -ResultPath $paths[$clientRole].Result `
            -LogPath $paths[$clientRole].Log
        $childProcesses.Add($clientProcess)
    }

    for ($index = 1; $index -lt $childProcesses.Count; $index++) {
        $role = if ($index -eq 1) { 'OwnerClient' } else { 'ObserverClient' }
        Wait-ForRoleArtifact `
            -Process $childProcesses[$index] `
            -ArtifactPath $paths[$role].Ready `
            -Description "$role readiness" `
            -DeadlineUtc $runDeadlineUtc
        Read-ValidatedReadyReport `
            -Path $paths[$role].Ready `
            -ExpectedRunId $runId `
            -ExpectedScenario $Scenario `
            -ExpectedRole $role | Out-Null
    }

    $roles = @('Server', 'OwnerClient', 'ObserverClient')
    for ($index = 0; $index -lt $childProcesses.Count; $index++) {
        $role = $roles[$index]
        Wait-ForRoleArtifact `
            -Process $childProcesses[$index] `
            -ArtifactPath $paths[$role].Result `
            -Description "$role final result" `
            -DeadlineUtc $runDeadlineUtc
    }

    $reportPaths = [ordered]@{
        Server = $paths.Server.Result
        OwnerClient = $paths.OwnerClient.Result
        ObserverClient = $paths.ObserverClient.Result
    }
    $reports = Assert-FinalReports -ReportPaths $reportPaths -ExpectedRunId $runId -ExpectedScenario $Scenario

    $finalOutput = [ordered]@{
        status = 'passed'
        runId = $runId
        scenarioId = $Scenario
        stateRevision = $reports.Server.stateRevision
        artifactsPath = $runPath
        playerPath = $resolvedPlayerPath
        facts = $reports.Server.facts
    }
}
catch {
    $exitCode = 1
    Write-Error $_
}
finally {
    Stop-NetworkTestProcesses -Processes $childProcesses

    if (-not $BuildInPlace -and -not $KeepStaging) {
        Remove-ValidatedStagingDirectory -StagingRunPath $stagingRunPath -StagingRootPath $stagingRootPath
    }
}

if ($exitCode -ne 0) {
    exit $exitCode
}

$finalOutput | ConvertTo-Json -Depth 12
