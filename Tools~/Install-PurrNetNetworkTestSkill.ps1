[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ProjectPath,

    [switch]$StageIncoming
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
}

function Get-RelativeFileEntries {
    param([Parameter(Mandatory = $true)][string]$RootPath)

    $files = @(Get-ChildItem -LiteralPath $RootPath -Recurse -File -Force |
        Where-Object { $_.Name -ne '.caffeinated-network-testing-skill.json' } |
        Sort-Object -Property FullName)

    $entries = [System.Collections.Generic.List[string]]::new()
    foreach ($file in $files) {
        $relativePath = [System.IO.Path]::GetRelativePath($RootPath, $file.FullName).Replace('\', '/')
        $contentHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $entries.Add("$relativePath`n$contentHash")
    }

    return $entries
}

function Get-DirectoryContentHash {
    param([Parameter(Mandatory = $true)][string]$RootPath)

    if (-not (Test-Path -LiteralPath $RootPath -PathType Container)) {
        throw "Cannot hash missing directory '$RootPath'."
    }

    $entries = Get-RelativeFileEntries -RootPath $RootPath
    $payload = [string]::Join("`n", $entries)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
    $digest = [System.Security.Cryptography.SHA256]::HashData($bytes)
    return [System.Convert]::ToHexString($digest).ToLowerInvariant()
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object]$Value
    )

    $json = $Value | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText($Path, $json + "`n", [System.Text.UTF8Encoding]::new($false))
}

function Enter-SkillInstallerLock {
    param(
        [Parameter(Mandatory = $true)][string]$SkillsRoot,
        [ValidateRange(1, 300000)][int]$TimeoutMilliseconds = 30000
    )

    $lockKey = [System.IO.Path]::GetFullPath($SkillsRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    if ([System.OperatingSystem]::IsWindows()) {
        $lockKey = $lockKey.ToUpperInvariant()
    }

    $lockKeyBytes = [System.Text.Encoding]::UTF8.GetBytes($lockKey)
    $lockKeyDigest = [System.Security.Cryptography.SHA256]::HashData($lockKeyBytes)
    $mutexName = 'caffeinated-network-testing-skill-installer-' +
        [System.Convert]::ToHexString($lockKeyDigest).ToLowerInvariant()
    $mutex = [System.Threading.Mutex]::new($false, $mutexName)
    $acquired = $false
    try {
        try {
            $acquired = $mutex.WaitOne($TimeoutMilliseconds)
        }
        catch [System.Threading.AbandonedMutexException] {
            $acquired = $true
        }

        if (-not $acquired) {
            throw "Timed out after $TimeoutMilliseconds ms waiting for another skill installation at '$SkillsRoot' to finish."
        }

        return $mutex
    }
    catch {
        if (-not $acquired) {
            $mutex.Dispose()
        }

        throw
    }
}

function Exit-SkillInstallerLock {
    param([Parameter(Mandatory = $true)][System.Threading.Mutex]$Mutex)

    try {
        $Mutex.ReleaseMutex()
    }
    finally {
        $Mutex.Dispose()
    }
}

function Copy-SkillSource {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    [System.IO.Directory]::CreateDirectory($DestinationPath) | Out-Null
    $sourceFiles = @(Get-ChildItem -LiteralPath $SourcePath -Recurse -File -Force | Sort-Object -Property FullName)

    foreach ($sourceFile in $sourceFiles) {
        $relativePath = [System.IO.Path]::GetRelativePath($SourcePath, $sourceFile.FullName)
        $destinationFile = Join-Path $DestinationPath $relativePath
        $destinationDirectory = Split-Path -Parent $destinationFile
        [System.IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null
        Copy-Item -LiteralPath $sourceFile.FullName -Destination $destinationFile
    }
}

function Read-OwnershipRecord {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Installed skill has no ownership record '$Path'; refusing to overwrite user-owned files."
    }

    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "Installed skill ownership record '$Path' is malformed: $($_.Exception.Message)"
    }
}

$resolvedProjectPath = Get-FullPath -Path $ProjectPath
$manifestPath = Join-Path $resolvedProjectPath 'Packages\manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Project '$resolvedProjectPath' has no Packages/manifest.json. Pass the Unity project and repository root."
}

$packageRoot = Get-FullPath -Path (Split-Path -Parent $PSScriptRoot)
$sourcePath = Join-Path $packageRoot 'Skills~\run-caffeinated-network-tests'
$packageJsonPath = Join-Path $packageRoot 'package.json'
if (-not (Test-Path -LiteralPath $sourcePath -PathType Container)) {
    throw "Package-owned skill source '$sourcePath' does not exist."
}

if (-not (Test-Path -LiteralPath (Join-Path $sourcePath 'SKILL.md') -PathType Leaf)) {
    throw "Package-owned skill source '$sourcePath' has no SKILL.md."
}

if (-not (Test-Path -LiteralPath $packageJsonPath -PathType Leaf)) {
    throw "Package manifest '$packageJsonPath' does not exist."
}

$packageJson = Get-Content -LiteralPath $packageJsonPath -Raw | ConvertFrom-Json
$packageVersion = [string]$packageJson.version
if ([string]::IsNullOrWhiteSpace($packageVersion)) {
    throw "Package manifest '$packageJsonPath' has no version."
}

$sourceHash = Get-DirectoryContentHash -RootPath $sourcePath
$skillsRoot = Join-Path $resolvedProjectPath '.agents\skills'
$destinationPath = Join-Path $skillsRoot 'run-caffeinated-network-tests'
$ownershipPath = Join-Path $destinationPath '.caffeinated-network-testing-skill.json'
[System.IO.Directory]::CreateDirectory($skillsRoot) | Out-Null
$installerMutex = Enter-SkillInstallerLock -SkillsRoot $skillsRoot

try {
    if (Test-Path -LiteralPath $destinationPath -PathType Container) {
        $ownership = Read-OwnershipRecord -Path $ownershipPath
        $installedHash = Get-DirectoryContentHash -RootPath $destinationPath
        if ([string]$ownership.installedHash -ne $installedHash) {
            if (-not $StageIncoming) {
                throw "Installed skill '$destinationPath' was modified after installation. No files were changed. Re-run with -StageIncoming to create a reviewable sibling copy."
            }

            $incomingName = "run-caffeinated-network-tests.incoming-$packageVersion"
            $incomingPath = Join-Path $skillsRoot $incomingName
            if (Test-Path -LiteralPath $incomingPath) {
                throw "Incoming skill destination '$incomingPath' already exists; review or remove that exact path first."
            }

            Copy-SkillSource -SourcePath $sourcePath -DestinationPath $incomingPath
            $incomingHash = Get-DirectoryContentHash -RootPath $incomingPath
            Write-JsonFile -Path (Join-Path $incomingPath '.caffeinated-network-testing-skill.json') -Value ([ordered]@{
                schemaVersion = 1
                packageName = [string]$packageJson.name
                packageVersion = $packageVersion
                canonicalSourceHash = $sourceHash
                installedHash = $incomingHash
                installedAtUtc = [DateTime]::UtcNow.ToString('O')
                status = 'incoming-review-required'
            })

            [pscustomobject]@{
                status = 'incoming-staged'
                packageVersion = $packageVersion
                destination = $incomingPath
                sourceHash = $sourceHash
            } | ConvertTo-Json -Depth 6
            exit 0
        }
    }

    $operationId = [Guid]::NewGuid().ToString('N')
    $temporaryPath = Join-Path $skillsRoot "run-caffeinated-network-tests.installing-$operationId"
    $backupPath = Join-Path $skillsRoot "run-caffeinated-network-tests.backup-$operationId"

    try {
        Copy-SkillSource -SourcePath $sourcePath -DestinationPath $temporaryPath
        $temporaryHash = Get-DirectoryContentHash -RootPath $temporaryPath
        if ($temporaryHash -ne $sourceHash) {
            throw "Copied skill hash '$temporaryHash' does not match canonical source hash '$sourceHash'."
        }

        Write-JsonFile -Path (Join-Path $temporaryPath '.caffeinated-network-testing-skill.json') -Value ([ordered]@{
            schemaVersion = 1
            packageName = [string]$packageJson.name
            packageVersion = $packageVersion
            canonicalSourceHash = $sourceHash
            installedHash = $temporaryHash
            installedAtUtc = [DateTime]::UtcNow.ToString('O')
            status = 'installed'
        })

        if (Test-Path -LiteralPath $destinationPath -PathType Container) {
            [System.IO.Directory]::Move($destinationPath, $backupPath)
        }

        [System.IO.Directory]::Move($temporaryPath, $destinationPath)
        if (Test-Path -LiteralPath $backupPath -PathType Container) {
            Remove-Item -LiteralPath $backupPath -Recurse -Force
        }
    }
    catch {
        if ((Test-Path -LiteralPath $backupPath -PathType Container) -and
            -not (Test-Path -LiteralPath $destinationPath)) {
            [System.IO.Directory]::Move($backupPath, $destinationPath)
        }

        if (Test-Path -LiteralPath $temporaryPath -PathType Container) {
            Remove-Item -LiteralPath $temporaryPath -Recurse -Force
        }

        throw
    }

    [pscustomobject]@{
        status = 'installed'
        packageVersion = $packageVersion
        destination = $destinationPath
        sourceHash = $sourceHash
    } | ConvertTo-Json -Depth 6
}
finally {
    Exit-SkillInstallerLock -Mutex $installerMutex
}
