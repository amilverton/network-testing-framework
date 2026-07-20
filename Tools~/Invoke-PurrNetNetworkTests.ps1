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

    [ValidateRange(5, 1800)]
    [int]$BuildTimeoutSeconds = 600,

    [switch]$ReusePlayer,

    [switch]$BuildInPlace,

    [switch]$KeepStaging,

    [switch]$OpenViewer
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$localPackageStagingPath = Join-Path $PSScriptRoot 'Stage-UnityLocalPackages.ps1'
if (-not (Test-Path -LiteralPath $localPackageStagingPath -PathType Leaf)) {
    throw "Local package staging helper '$localPackageStagingPath' does not exist."
}

. $localPackageStagingPath

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

function Start-HiddenProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        $process.Dispose()
        throw "Could not start process '$FilePath'."
    }

    return $process
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
        [Parameter(Mandatory = $true)][string]$BuildLogPath,
        [Parameter(Mandatory = $true)][int]$BuildTimeoutSeconds
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
            if ($existingReceipt.SchemaVersion -notin @(1, 2)) {
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
        '-projectPath', $BuildProjectPath,
        '-buildTarget', 'StandaloneWindows64',
        '-executeMethod', 'Amilverton.PurrNetTesting.Editor.NetworkTestPlayerBuilder.BuildFromCommandLine',
        '-networkTestBuildPath', $ResolvedPlayerPath,
        '-logFile', $BuildLogPath
    )

    $unityProcess = Start-HiddenProcess -FilePath $ResolvedUnityPath -Arguments $unityArgumentParts
    try {
        if (-not $unityProcess.WaitForExit($BuildTimeoutSeconds * 1000)) {
            throw "Unity Player build exceeded its $BuildTimeoutSeconds second deadline. See '$BuildLogPath'."
        }

        $unityExitCode = $unityProcess.ExitCode
    }
    finally {
        if (-not $unityProcess.HasExited) {
            $unityProcess.Kill($true)
            if (-not $unityProcess.WaitForExit(10000)) {
                Write-Warning "Unity build process tree $($unityProcess.Id) did not exit within 10 seconds after termination."
            }
        }

        $unityProcess.Dispose()
    }

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
    $temporaryPath = $Path + '.tmp-' + [System.Guid]::NewGuid().ToString('N')
    try {
        [System.IO.File]::WriteAllText(
            $temporaryPath,
            $json,
            [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::Move($temporaryPath, $Path)
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Get-LabeledInputFingerprint {
    param([Parameter(Mandatory = $true)][object[]]$Inputs)

    $inputsByLabel = [System.Collections.Generic.Dictionary[string, object]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($input in $Inputs) {
        $label = [string]$input.Label
        if (-not $inputsByLabel.TryAdd($label, $input)) {
            throw "Fingerprint input label '$label' is duplicated."
        }
    }

    $labels = [string[]]@($inputsByLabel.Keys)
    [System.Array]::Sort($labels, [System.StringComparer]::Ordinal)
    $incrementalHash = [System.Security.Cryptography.IncrementalHash]::CreateHash(
        [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        foreach ($label in $labels) {
            Add-LabeledFileToIncrementalHash `
                -Hash $incrementalHash `
                -Label $label `
                -Path ([string]$inputsByLabel[$label].Path)
        }

        return [System.Convert]::ToHexString($incrementalHash.GetHashAndReset()).ToLowerInvariant()
    }
    finally {
        $incrementalHash.Dispose()
    }
}

function Get-NetworkTestProjectSnapshotFingerprint {
    param([Parameter(Mandatory = $true)][string]$ProjectPath)

    $inputs = [System.Collections.Generic.List[object]]::new()
    $assetsPath = Join-Path $ProjectPath 'Assets'
    if (Test-Path -LiteralPath $assetsPath -PathType Container) {
        Get-ChildItem -LiteralPath $assetsPath -Recurse -File |
            Where-Object {
                $relativeAssetPath = [System.IO.Path]::GetRelativePath($assetsPath, $_.FullName).Replace('\', '/')
                -not $relativeAssetPath.StartsWith(
                    'PurrNetNetworkTestGenerated/',
                    [System.StringComparison]::OrdinalIgnoreCase)
            } |
            ForEach-Object {
                $relative = [System.IO.Path]::GetRelativePath($ProjectPath, $_.FullName).Replace('\', '/')
                $inputs.Add([pscustomobject]@{ Label = $relative; Path = $_.FullName })
            }
    }

    foreach ($relativePath in @('Packages\manifest.json', 'Packages\packages-lock.json')) {
        $absolutePath = Join-Path $ProjectPath $relativePath
        if (Test-Path -LiteralPath $absolutePath -PathType Leaf) {
            $inputs.Add([pscustomobject]@{
                Label = $relativePath.Replace('\', '/')
                Path = $absolutePath
            })
        }
    }

    $projectSettingsPath = Join-Path $ProjectPath 'ProjectSettings'
    if (Test-Path -LiteralPath $projectSettingsPath -PathType Container) {
        Get-ChildItem -LiteralPath $projectSettingsPath -Recurse -File | ForEach-Object {
            $relative = [System.IO.Path]::GetRelativePath($ProjectPath, $_.FullName).Replace('\', '/')
            $inputs.Add([pscustomobject]@{ Label = $relative; Path = $_.FullName })
        }
    }

    return Get-LabeledInputFingerprint -Inputs $inputs.ToArray()
}

function Assert-LocalPackageSpecsMatch {
    param(
        [Parameter(Mandatory = $true)][object[]]$StagedSpecs,
        [Parameter(Mandatory = $true)][object[]]$CurrentSourceSpecs
    )

    $currentByName = [System.Collections.Generic.Dictionary[string, object]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($spec in $CurrentSourceSpecs) {
        if (-not $currentByName.TryAdd([string]$spec.DependencyName, $spec)) {
            throw "Current local-package spec '$($spec.DependencyName)' is duplicated."
        }
    }

    if ($StagedSpecs.Count -ne $currentByName.Count) {
        throw "Local-package dependency membership changed while staging; staged $($StagedSpecs.Count) package(s) but source now declares $($currentByName.Count)."
    }

    foreach ($stagedSpec in $StagedSpecs) {
        $dependencyName = [string]$stagedSpec.DependencyName
        if (-not $currentByName.ContainsKey($dependencyName)) {
            throw "Local-package dependency '$dependencyName' was removed or renamed while staging."
        }

        $currentSpec = $currentByName[$dependencyName]
        if ([string]$stagedSpec.SourceVersion -cne [string]$currentSpec.SourceVersion -or
            [string]$stagedSpec.Digest -cne [string]$currentSpec.Digest) {
            throw "Local-package dependency '$dependencyName' changed while it was being staged; refusing to build an unbound snapshot."
        }
    }
}

function Get-NetworkTestInputFingerprint {
    param(
        [Parameter(Mandatory = $true)][string]$ResolvedProjectPath,
        [Parameter(Mandatory = $true)][string]$HarnessRootPath
    )

    $inputs = [System.Collections.Generic.List[object]]::new()
    $harnessPatterns = @('Runtime', 'Editor')
    foreach ($relativeRoot in $harnessPatterns) {
        $absoluteRoot = Join-Path $HarnessRootPath $relativeRoot
        if (-not (Test-Path -LiteralPath $absoluteRoot -PathType Container)) {
            continue
        }

        Get-ChildItem -LiteralPath $absoluteRoot -Recurse -File |
            Where-Object { $_.Extension -in @('.cs', '.asmdef', '.meta', '.json', '.xml') } |
            ForEach-Object {
                $relative = [System.IO.Path]::GetRelativePath($HarnessRootPath, $_.FullName).Replace('\', '/')
                $inputs.Add([pscustomobject]@{ Label = "harness/$relative"; Path = $_.FullName })
            }
    }

    $packageJsonPath = Join-Path $HarnessRootPath 'package.json'
    if (Test-Path -LiteralPath $packageJsonPath -PathType Leaf) {
        $inputs.Add([pscustomobject]@{ Label = 'harness/package.json'; Path = $packageJsonPath })
    }

    $assetsPath = Join-Path $ResolvedProjectPath 'Assets'
    if (Test-Path -LiteralPath $assetsPath -PathType Container) {
        Get-ChildItem -LiteralPath $assetsPath -Recurse -File |
            Where-Object {
                $relativeAssetPath = [System.IO.Path]::GetRelativePath($assetsPath, $_.FullName).Replace('\', '/')
                -not $relativeAssetPath.StartsWith(
                    'PurrNetNetworkTestGenerated/',
                    [System.StringComparison]::OrdinalIgnoreCase)
            } |
            ForEach-Object {
                $relative = [System.IO.Path]::GetRelativePath($ResolvedProjectPath, $_.FullName).Replace('\', '/')
                $inputs.Add([pscustomobject]@{ Label = "project/$relative"; Path = $_.FullName })
            }
    }

    foreach ($relativePath in @('Packages\manifest.json', 'Packages\packages-lock.json')) {
        $absolutePath = Join-Path $ResolvedProjectPath $relativePath
        if (Test-Path -LiteralPath $absolutePath -PathType Leaf) {
            $inputs.Add([pscustomobject]@{
                Label = 'project/' + $relativePath.Replace('\', '/')
                Path = $absolutePath
            })
        }
    }


    $projectSettingsPath = Join-Path $ResolvedProjectPath 'ProjectSettings'
    if (Test-Path -LiteralPath $projectSettingsPath -PathType Container) {
        Get-ChildItem -LiteralPath $projectSettingsPath -Recurse -File | ForEach-Object {
            $relative = [System.IO.Path]::GetRelativePath($ResolvedProjectPath, $_.FullName).Replace('\', '/')
            $inputs.Add([pscustomobject]@{ Label = "project/$relative"; Path = $_.FullName })
        }
    }

    $manifestPath = Join-Path $ResolvedProjectPath 'Packages\manifest.json'
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $manifestDirectory = Split-Path -Parent $manifestPath
        foreach ($dependency in $manifest.dependencies.PSObject.Properties) {
            $dependencyValue = [string]$dependency.Value
            if (-not $dependencyValue.StartsWith('file:', [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $localValue = [System.Uri]::UnescapeDataString($dependencyValue.Substring(5))
            $localPath = if ([System.IO.Path]::IsPathRooted($localValue)) {
                [System.IO.Path]::GetFullPath($localValue)
            }
            else {
                [System.IO.Path]::GetFullPath((Join-Path $manifestDirectory $localValue))
            }

            if (-not (Test-Path -LiteralPath $localPath)) {
                throw "Local package dependency '$($dependency.Name)' resolves to missing path '$localPath'."
            }

            if ((Test-Path -LiteralPath $localPath -PathType Container) -and
                -not $localPath.TrimEnd('\').Equals($HarnessRootPath.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)) {
                $localPackageManifest = Join-Path $localPath 'package.json'
                if (-not (Test-Path -LiteralPath $localPackageManifest -PathType Leaf)) {
                    throw "Local package dependency '$($dependency.Name)' resolves to '$localPath', which has no package.json."
                }

                $excludedPackageDirectories = @(
                    '.git', '.svn', '.hg', '.vs', '.idea',
                    'Library', 'Temp', 'tmp', 'Logs', 'obj', 'bin', 'Artifacts'
                )
                $packageDirectories = [System.Collections.Generic.Stack[System.IO.DirectoryInfo]]::new()
                $packageDirectories.Push([System.IO.DirectoryInfo]::new($localPath))
                while ($packageDirectories.Count -gt 0) {
                    $packageDirectory = $packageDirectories.Pop()
                    foreach ($file in $packageDirectory.EnumerateFiles()) {
                        $relative = [System.IO.Path]::GetRelativePath($localPath, $file.FullName).Replace('\', '/')
                        $inputs.Add([pscustomobject]@{
                            Label = "local-package/$($dependency.Name)/$relative"
                            Path = $file.FullName
                        })
                    }

                    foreach ($directory in $packageDirectory.EnumerateDirectories()) {
                        $isReparsePoint = ($directory.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
                        $isExcluded = $directory.Name -in $excludedPackageDirectories -or
                            $directory.Name.StartsWith('.') -or
                            $directory.Name.EndsWith('~')
                        if (-not $isReparsePoint -and -not $isExcluded) {
                            $packageDirectories.Push($directory)
                        }
                    }
                }
            }
            elseif (Test-Path -LiteralPath $localPath -PathType Leaf) {
                $inputs.Add([pscustomobject]@{
                    Label = "local-package/$($dependency.Name)/$([System.IO.Path]::GetFileName($localPath))"
                    Path = $localPath
                })
            }
        }
    }

    return Get-LabeledInputFingerprint -Inputs $inputs.ToArray()
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
        '-File', $viewerPath,
        '-RunPath', $ResolvedRunPath
    )

    $viewerProcess = Start-HiddenProcess -FilePath $pwshPath -Arguments $viewerArgumentParts
    $viewerProcess.Dispose()
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
        '-logFile', $LogPath,
        '-networkTestRunId', $RunId,
        '-networkTestScenario', $ScenarioId,
        '-networkTestRole', $Role,
        '-networkTestConfig', $ConfigurationPath,
        '-networkTestReady', $ReadyPath,
        '-networkTestResult', $ResultPath,
        '-networkTestLog', $LogPath
    )

    return Start-HiddenProcess -FilePath $ResolvedPlayerPath -Arguments $argumentParts
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

function Wait-ForRoleExit {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)][string]$Role,
        [Parameter(Mandatory = $true)][datetime]$DeadlineUtc
    )

    while ([DateTime]::UtcNow -lt $DeadlineUtc) {
        if ($Process.HasExited) {
            if ($Process.ExitCode -ne 0) {
                throw "$Role process $($Process.Id) exited with code $($Process.ExitCode) after publishing its result."
            }

            return
        }

        Start-Sleep -Milliseconds 100
    }

    throw "Reached the global run deadline while waiting for $Role process $($Process.Id) to exit naturally."
}

function Read-ValidatedReadyReport {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedRunId,
        [Parameter(Mandatory = $true)][string]$ExpectedScenario,
        [Parameter(Mandatory = $true)][string]$ExpectedRole,
        [Parameter(Mandatory = $true)]$ScenarioContracts
    )

    $report = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($report.schemaVersion -ne 2) {
        throw "Ready report '$Path' has unsupported schema version '$($report.schemaVersion)'."
    }

    if ($report.runId -ne $ExpectedRunId -or $report.scenarioId -ne $ExpectedScenario -or $report.role -ne $ExpectedRole) {
        throw "Ready report '$Path' does not match run '$ExpectedRunId', scenario '$ExpectedScenario', role '$ExpectedRole'."
    }

    $scenarioContractProperty = $ScenarioContracts.PSObject.Properties[$ExpectedScenario]
    if ($null -eq $scenarioContractProperty) {
        throw "Scenario '$ExpectedScenario' has no exact contract in the Player execution manifest."
    }

    $roleContractProperty = $scenarioContractProperty.Value.roles.PSObject.Properties[$ExpectedRole]
    if ($null -eq $roleContractProperty) {
        throw "Scenario contract '$ExpectedScenario' has no ready contract for role '$ExpectedRole'."
    }

    Assert-ExactSequence `
        -Actual $report.milestones `
        -Expected $roleContractProperty.Value.readyMilestones `
        -Description "Role '$ExpectedRole' ready milestones"

    return $report
}

function Read-ValidatedCompletionReport {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedRunId,
        [Parameter(Mandatory = $true)][string]$ExpectedScenario,
        [Parameter(Mandatory = $true)][string]$ExpectedRole,
        [Parameter(Mandatory = $true)]$ScenarioContracts
    )

    $report = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($report.schemaVersion -ne 2 -or
        $report.runId -cne $ExpectedRunId -or
        $report.scenarioId -cne $ExpectedScenario -or
        $report.role -cne $ExpectedRole) {
        throw "Completion report '$Path' does not match run '$ExpectedRunId', scenario '$ExpectedScenario', role '$ExpectedRole'."
    }

    $scenarioContractProperty = $ScenarioContracts.PSObject.Properties[$ExpectedScenario]
    if ($null -eq $scenarioContractProperty) {
        throw "Scenario '$ExpectedScenario' has no exact contract in the Player execution manifest."
    }

    if ([int]$report.stateRevision -ne [int]$scenarioContractProperty.Value.stateRevision) {
        throw "Role '$ExpectedRole' reached the shutdown barrier at revision '$($report.stateRevision)'; exact contract expects '$($scenarioContractProperty.Value.stateRevision)'."
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

function Assert-ExactPrimitiveObject {
    param(
        [Parameter(Mandatory = $true)]$Actual,
        [Parameter(Mandatory = $true)]$Expected,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $actualNames = @($Actual.PSObject.Properties.Name | Sort-Object)
    $expectedNames = if ($Expected -is [System.Collections.IDictionary]) {
        @($Expected.Keys | ForEach-Object { [string]$_ } | Sort-Object)
    }
    else {
        @($Expected.PSObject.Properties.Name | Sort-Object)
    }

    $actualNameJson = ConvertTo-Json -InputObject $actualNames -Compress
    $expectedNameJson = ConvertTo-Json -InputObject $expectedNames -Compress
    if ($actualNameJson -ne $expectedNameJson) {
        throw "$Description keys were '$([string]::Join(',', $actualNames))'; expected '$([string]::Join(',', $expectedNames))'."
    }

    foreach ($name in $expectedNames) {
        $actualValue = $Actual.PSObject.Properties[$name].Value
        $expectedValue = if ($Expected -is [System.Collections.IDictionary]) {
            $Expected[$name]
        }
        else {
            $Expected.PSObject.Properties[$name].Value
        }

        $actualJson = ConvertTo-Json -InputObject $actualValue -Depth 12 -Compress
        $expectedJson = ConvertTo-Json -InputObject $expectedValue -Depth 12 -Compress
        if ($actualJson -ne $expectedJson) {
            throw "$Description value '$name' was '$actualJson'; expected '$expectedJson'."
        }
    }
}

function Assert-ExactSequence {
    param(
        [Parameter(Mandatory = $true)]$Actual,
        [Parameter(Mandatory = $true)]$Expected,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $actualJson = ConvertTo-Json -InputObject @($Actual) -Depth 12 -Compress
    $expectedJson = ConvertTo-Json -InputObject @($Expected) -Depth 12 -Compress
    if ($actualJson -ne $expectedJson) {
        throw "$Description was '$actualJson'; expected '$expectedJson'."
    }
}

function Read-NetworkTestExecutionManifest {
    param([Parameter(Mandatory = $true)][string]$ResolvedPlayerPath)

    $receiptPath = $ResolvedPlayerPath + '.build.json'
    if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
        throw "Network test Player build receipt '$receiptPath' does not exist. Rebuild the Player."
    }

    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    if ($receipt.SchemaVersion -ne 2) {
        throw "Network test Player build receipt '$receiptPath' has schema '$($receipt.SchemaVersion)'; schema 2 is required. Rebuild the Player."
    }

    $manifestPath = $ResolvedPlayerPath + '.manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Network test execution manifest '$manifestPath' does not exist. Rebuild the Player."
    }

    $expectedManifestName = [System.IO.Path]::GetFileName($manifestPath)
    if ($receipt.ExecutionManifestFileName -cne $expectedManifestName) {
        throw "Build receipt names execution manifest '$($receipt.ExecutionManifestFileName)'; expected '$expectedManifestName'."
    }

    $actualManifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ([string]$receipt.ExecutionManifestSha256 -cne $actualManifestHash) {
        throw "Execution manifest '$manifestPath' does not match the SHA-256 recorded by the Player build. Rebuild the Player."
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1) {
        throw "Execution manifest '$manifestPath' has unsupported schema version '$($manifest.schemaVersion)'."
    }

    if ($null -eq $manifest.scenarioContracts -or
        @($manifest.scenarioContracts.PSObject.Properties).Count -eq 0) {
        throw "Execution manifest '$manifestPath' contains no scenario contracts."
    }

    if ($null -eq $manifest.supportedEnvelope -or
        $manifest.supportedEnvelope.transport -cne 'UDP' -or
        $manifest.supportedEnvelope.buildTarget -cne 'StandaloneWindows64') {
        throw "Execution manifest '$manifestPath' is outside the coordinator's UDP Windows v1 support envelope."
    }

    return $manifest
}

function Assert-FinalReports {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary]$ReportPaths,
        [Parameter(Mandatory = $true)][string]$ExpectedRunId,
        [Parameter(Mandatory = $true)][string]$ExpectedScenario,
        [Parameter(Mandatory = $true)]$ScenarioContracts
    )

    $scenarioContractProperty = $ScenarioContracts.PSObject.Properties[$ExpectedScenario]
    if ($null -eq $scenarioContractProperty) {
        throw "Scenario '$ExpectedScenario' has no exact contract in the Player execution manifest."
    }

    $scenarioContract = $scenarioContractProperty.Value
    $reports = [ordered]@{}
    foreach ($role in @('Server', 'OwnerClient', 'ObserverClient')) {
        $path = [string]$ReportPaths[$role]
        $report = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json

    if ($report.schemaVersion -ne 2) {
            throw "Result '$path' has unsupported schema version '$($report.schemaVersion)'."
        }

        if ($report.runId -ne $ExpectedRunId -or $report.scenarioId -ne $ExpectedScenario -or $report.role -ne $role) {
            throw "Result '$path' does not match run '$ExpectedRunId', scenario '$ExpectedScenario', role '$role'."
        }

        if ($report.status -ne 'passed') {
            throw "Role '$role' failed: $($report.failure). See '$($report.logPath)'."
        }

        if ($null -eq $report.sharedFacts -or @($report.sharedFacts.PSObject.Properties).Count -eq 0) {
            throw "Role '$role' published no shared facts."
        }

        if ($null -eq $report.roleEvidence) {
            throw "Role '$role' published no role evidence."
        }

        $expectedProvenance = if ($role -eq 'Server') { 'dedicated-server-authority' } else { 'client-replicated-read' }
        if ($report.roleEvidence.role -ne $role -or
            $report.roleEvidence.provenance -ne $expectedProvenance -or
            [int]$report.roleEvidence.processId -le 0 -or
            [string]::IsNullOrWhiteSpace([string]$report.roleEvidence.transitionTrace)) {
            throw "Role '$role' published invalid process-derived role evidence."
        }

        $expectedTrace = [string]::Join('>', [string[]]$report.milestones)
        if ($report.roleEvidence.transitionTrace -ne $expectedTrace) {
            throw "Role '$role' transition trace does not match its milestone sequence."
        }

        $assertions = @($report.assertions)
        if ($assertions.Count -eq 0 -or @($assertions | Sort-Object -Unique).Count -ne $assertions.Count) {
            throw "Role '$role' must publish one or more unique assertions."
        }

        if ([int]$report.stateRevision -ne [int]$scenarioContract.stateRevision) {
            throw "Role '$role' reported revision '$($report.stateRevision)'; exact contract expects '$($scenarioContract.stateRevision)'."
        }

        Assert-ExactPrimitiveObject `
            -Actual $report.sharedFacts `
            -Expected $scenarioContract.sharedFacts `
            -Description "Role '$role' shared facts"

        $roleContractProperty = $scenarioContract.roles.PSObject.Properties[$role]
        if ($null -eq $roleContractProperty) {
            throw "Scenario contract '$ExpectedScenario' has no role contract for '$role'."
        }

        $roleContract = $roleContractProperty.Value
        $expectedEvidence = [ordered]@{
            role = $role
            processId = [int]$report.roleEvidence.processId
            provenance = $expectedProvenance
            transitionTrace = $expectedTrace
        }
        foreach ($property in $roleContract.evidence.PSObject.Properties) {
            $expectedEvidence[$property.Name] = $property.Value
        }

        Assert-ExactPrimitiveObject `
            -Actual $report.roleEvidence `
            -Expected $expectedEvidence `
            -Description "Role '$role' evidence"
        Assert-ExactSequence `
            -Actual $report.assertions `
            -Expected $roleContract.assertions `
            -Description "Role '$role' assertions"
        Assert-ExactSequence `
            -Actual $report.milestones `
            -Expected $roleContract.milestones `
            -Description "Role '$role' milestones"

        $reports[$role] = $report
    }

    $serverRevision = [int]$reports.Server.stateRevision
    $serverFacts = ConvertTo-StableFactsJson -Facts $reports.Server.sharedFacts

    foreach ($role in @('OwnerClient', 'ObserverClient')) {
        if ([int]$reports[$role].stateRevision -ne $serverRevision) {
            throw "Role '$role' reported revision '$($reports[$role].stateRevision)' but Server reported '$serverRevision'."
        }

        $roleFacts = ConvertTo-StableFactsJson -Facts $reports[$role].sharedFacts
        if ($roleFacts -ne $serverFacts) {
            throw "Role '$role' facts do not match authoritative Server facts."
        }
    }

    $processIds = @($reports.Values | ForEach-Object { [int]$_.roleEvidence.processId })
    if (@($processIds | Sort-Object -Unique).Count -ne 3) {
        throw 'Server, OwnerClient, and ObserverClient did not report distinct operating-system process IDs.'
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
                $process.Kill($true)
                if (-not $process.WaitForExit(10000)) {
                    Write-Warning "Network test process tree $($process.Id) did not exit within 10 seconds after termination."
                }
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
$harnessRootPath = Split-Path -Parent $PSScriptRoot
$inputFingerprint = Get-NetworkTestInputFingerprint `
    -ResolvedProjectPath $resolvedProjectPath `
    -HarnessRootPath $harnessRootPath
$fingerprintPath = $resolvedPlayerPath + '.inputs.sha256'
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

        if (Test-Path -LiteralPath $fingerprintPath -PathType Leaf) {
            Remove-Item -LiteralPath $fingerprintPath -Force
        }

        if ($BuildInPlace) {
            Assert-ProjectIsNotOpen -ResolvedProjectPath $resolvedProjectPath
        }
        else {
            Copy-StagingProject -SourcePath $resolvedProjectPath -DestinationPath $stagingProjectPath -ResolvedArtifactsPath $resolvedArtifactsPath
            $stagedProjectSnapshotFingerprint = Get-NetworkTestProjectSnapshotFingerprint `
                -ProjectPath $stagingProjectPath
            $stagedLocalPackageSpecs = @(Stage-UnityLocalPackages `
                -SourceProjectPath $resolvedProjectPath `
                -StagedProjectPath $stagingProjectPath)

            $currentSourceSnapshotFingerprint = Get-NetworkTestProjectSnapshotFingerprint `
                -ProjectPath $resolvedProjectPath
            if ($stagedProjectSnapshotFingerprint -ne $currentSourceSnapshotFingerprint) {
                throw "The staged Unity project snapshot does not match the current source project. Source files changed while they were copied; refusing to build an unbound Player."
            }

            $currentLocalPackageSpecs = @(Get-LocalPackageStagingSpecs `
                -SourceProjectPath $resolvedProjectPath)
            Assert-LocalPackageSpecsMatch `
                -StagedSpecs $stagedLocalPackageSpecs `
                -CurrentSourceSpecs $currentLocalPackageSpecs
            $buildProjectPath = $stagingProjectPath
        }

        $buildLogPath = Join-Path $runPath 'build.log'
        Invoke-NetworkTestPlayerBuild `
            -ResolvedUnityPath $resolvedUnityPath `
            -BuildProjectPath $buildProjectPath `
            -ResolvedPlayerPath $resolvedPlayerPath `
            -BuildLogPath $buildLogPath `
            -BuildTimeoutSeconds $BuildTimeoutSeconds

        $postBuildInputFingerprint = Get-NetworkTestInputFingerprint `
            -ResolvedProjectPath $resolvedProjectPath `
            -HarnessRootPath $harnessRootPath
        if ($postBuildInputFingerprint -ne $inputFingerprint) {
            throw "Project network test inputs changed while the Player was being staged or built. The unbound Player was retained for diagnosis but cannot be reused; rebuild from stable inputs."
        }

        [System.IO.File]::WriteAllText(
            $fingerprintPath,
            $postBuildInputFingerprint,
            [System.Text.UTF8Encoding]::new($false))
    }

    if (-not (Test-Path -LiteralPath $resolvedPlayerPath -PathType Leaf)) {
        throw "Network test Player '$resolvedPlayerPath' does not exist. Build it or omit -ReusePlayer."
    }

    if (-not (Test-Path -LiteralPath $fingerprintPath -PathType Leaf)) {
        throw "Network test Player fingerprint '$fingerprintPath' does not exist. Rebuild without -ReusePlayer."
    }

    $currentInputFingerprint = Get-NetworkTestInputFingerprint `
        -ResolvedProjectPath $resolvedProjectPath `
        -HarnessRootPath $harnessRootPath
    $builtFingerprint = (Get-Content -LiteralPath $fingerprintPath -Raw).Trim()
    if ($builtFingerprint -ne $currentInputFingerprint) {
        throw "Refusing to reuse stale network test Player. Source, dependencies, Unity version, or harness inputs changed; rebuild without -ReusePlayer."
    }

    $executionManifest = Read-NetworkTestExecutionManifest -ResolvedPlayerPath $resolvedPlayerPath
    $scenarioContracts = $executionManifest.scenarioContracts
    if ($null -eq $scenarioContracts.PSObject.Properties[$Scenario]) {
        $availableScenarios = @($scenarioContracts.PSObject.Properties.Name | Sort-Object)
        throw "Scenario '$Scenario' is not present in this Player build. Available scenarios: $([string]::Join(', ', $availableScenarios))."
    }

    $configurationPath = Join-Path $runPath 'config.json'
    $port = Get-FreeUdpPort
    $configuration = [ordered]@{
        schemaVersion = 2
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
            Completion = Join-Path $runPath "$filePrefix.result.json.complete.json"
            Stop = Join-Path $runPath "$filePrefix.result.json.stop.json"
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
    Read-ValidatedReadyReport `
        -Path $paths.Server.Ready `
        -ExpectedRunId $runId `
        -ExpectedScenario $Scenario `
        -ExpectedRole 'Server' `
        -ScenarioContracts $scenarioContracts | Out-Null

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
        Wait-ForRoleArtifact `
            -Process $clientProcess `
            -ArtifactPath $paths[$clientRole].Ready `
            -Description "$clientRole readiness" `
            -DeadlineUtc $runDeadlineUtc
        Read-ValidatedReadyReport `
            -Path $paths[$clientRole].Ready `
            -ExpectedRunId $runId `
            -ExpectedScenario $Scenario `
            -ExpectedRole $clientRole `
            -ScenarioContracts $scenarioContracts | Out-Null
    }

    $roles = @('Server', 'OwnerClient', 'ObserverClient')
    for ($index = 0; $index -lt $childProcesses.Count; $index++) {
        $role = $roles[$index]
        Wait-ForRoleArtifact `
            -Process $childProcesses[$index] `
            -ArtifactPath $paths[$role].Completion `
            -Description "$role completion barrier" `
            -DeadlineUtc $runDeadlineUtc
        Read-ValidatedCompletionReport `
            -Path $paths[$role].Completion `
            -ExpectedRunId $runId `
            -ExpectedScenario $Scenario `
            -ExpectedRole $role `
            -ScenarioContracts $scenarioContracts | Out-Null
    }

    foreach ($role in $roles) {
        Write-JsonFile -Path $paths[$role].Stop -Value ([ordered]@{
            schemaVersion = 2
            runId = $runId
            scenarioId = $Scenario
            role = $role
        })
    }

    for ($index = 0; $index -lt $childProcesses.Count; $index++) {
        $role = $roles[$index]
        Wait-ForRoleArtifact `
            -Process $childProcesses[$index] `
            -ArtifactPath $paths[$role].Result `
            -Description "$role final result" `
            -DeadlineUtc $runDeadlineUtc
    }


    for ($index = 0; $index -lt $childProcesses.Count; $index++) {
        Wait-ForRoleExit `
            -Process $childProcesses[$index] `
            -Role $roles[$index] `
            -DeadlineUtc $runDeadlineUtc
    }

    $reportPaths = [ordered]@{
        Server = $paths.Server.Result
        OwnerClient = $paths.OwnerClient.Result
        ObserverClient = $paths.ObserverClient.Result
    }
    $reports = Assert-FinalReports `
        -ReportPaths $reportPaths `
        -ExpectedRunId $runId `
        -ExpectedScenario $Scenario `
        -ScenarioContracts $scenarioContracts

    $finalOutput = [ordered]@{
        status = 'passed'
        runId = $runId
        scenarioId = $Scenario
        stateRevision = $reports.Server.stateRevision
        artifactsPath = $runPath
        playerPath = $resolvedPlayerPath
        sharedFacts = $reports.Server.sharedFacts
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
