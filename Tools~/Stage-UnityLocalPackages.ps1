Set-StrictMode -Version Latest

function Read-Utf8TextFileState {
    param([Parameter(Mandatory = $true)][string]$Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $hasPreamble = $bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and
        $bytes[1] -eq 0xBB -and
        $bytes[2] -eq 0xBF
    if ($bytes.Length -ge 2 -and
        (($bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) -or
         ($bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF))) {
        throw "JSON file '$Path' uses UTF-16; Unity package staging requires UTF-8 so dependency-only rewrites can preserve every other byte."
    }

    $offset = if ($hasPreamble) { 3 } else { 0 }
    $encoding = [System.Text.UTF8Encoding]::new($false, $true)
    try {
        $text = $encoding.GetString($bytes, $offset, $bytes.Length - $offset)
    }
    catch [System.Text.DecoderFallbackException] {
        throw "JSON file '$Path' is not valid UTF-8: $($_.Exception.Message)"
    }

    return [pscustomobject]@{
        Text = $text
        HasPreamble = $hasPreamble
    }
}

function Write-Utf8TextFileState {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][bool]$HasPreamble
    )

    [System.IO.File]::WriteAllText($Path, $Text, [System.Text.UTF8Encoding]::new($HasPreamble))
}

function ConvertTo-JsonStringLiteral {
    param([Parameter(Mandatory = $true)][string]$Value)

    return ConvertTo-Json -InputObject $Value -Compress
}

function Get-ExactJsonProperty {
    param(
        [Parameter(Mandatory = $true)][System.Text.Json.JsonElement]$Object,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Description,
        [switch]$Optional
    )

    if ($Object.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
        throw "$Description is not a JSON object."
    }

    $matches = [System.Collections.Generic.List[System.Text.Json.JsonProperty]]::new()
    foreach ($property in $Object.EnumerateObject()) {
        if ($property.Name.Equals($Name, [System.StringComparison]::Ordinal)) {
            $matches.Add($property)
        }
    }

    if ($matches.Count -gt 1) {
        throw "$Description contains duplicate '$Name' properties."
    }

    if ($matches.Count -eq 0) {
        if ($Optional) {
            return $null
        }

        throw "$Description has no '$Name' property."
    }

    return $matches[0]
}

function Set-ExactJsonStringValue {
    param(
        [Parameter(Mandatory = $true)][string]$RawJson,
        [Parameter(Mandatory = $true)][string[]]$PropertyPath,
        [Parameter(Mandatory = $true)][string]$ExpectedValue,
        [Parameter(Mandatory = $true)][string]$NewValue,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $document = $null
    try {
        $document = [System.Text.Json.JsonDocument]::Parse($RawJson)
        $currentElement = $document.RootElement
        $currentRaw = $currentElement.GetRawText()
        $currentOffset = $RawJson.IndexOf($currentRaw, [System.StringComparison]::Ordinal)
        if ($currentOffset -lt 0) {
            throw "Could not locate the root JSON value for $Description."
        }

        foreach ($segment in $PropertyPath) {
            $property = Get-ExactJsonProperty `
                -Object $currentElement `
                -Name $segment `
                -Description $Description
            $childElement = $property.Value
            $childRaw = $childElement.GetRawText()
            $propertyLiteral = ConvertTo-JsonStringLiteral -Value $segment
            $pattern = [System.Text.RegularExpressions.Regex]::Escape($propertyLiteral) +
                '\s*:\s*' +
                [System.Text.RegularExpressions.Regex]::Escape($childRaw)
            $matches = [System.Text.RegularExpressions.Regex]::Matches($currentRaw, $pattern)
            if ($matches.Count -ne 1) {
                throw "Could not uniquely locate '$segment' while rewriting $Description."
            }

            $relativeChildOffset = $matches[0].Index + $matches[0].Length - $childRaw.Length
            $currentOffset += $relativeChildOffset
            $currentElement = $childElement
            $currentRaw = $childRaw
        }

        if ($currentElement.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
            throw "$Description is not a JSON string."
        }

        $actualValue = $currentElement.GetString()
        if (-not $actualValue.Equals($ExpectedValue, [System.StringComparison]::Ordinal)) {
            throw "$Description was '$actualValue'; expected '$ExpectedValue'."
        }

        $replacement = ConvertTo-JsonStringLiteral -Value $NewValue
        return $RawJson.Substring(0, $currentOffset) +
            $replacement +
            $RawJson.Substring($currentOffset + $currentRaw.Length)
    }
    catch [System.Text.Json.JsonException] {
        throw "$Description belongs to malformed JSON: $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $document) {
            $document.Dispose()
        }
    }
}

function Test-IsReparsePoint {
    param([Parameter(Mandatory = $true)][System.IO.FileSystemInfo]$Item)

    return ($Item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
}

function Assert-NoPackageReparsePoint {
    param(
        [Parameter(Mandatory = $true)][string]$PackageName,
        [Parameter(Mandatory = $true)][string]$PackagePath
    )

    $root = [System.IO.DirectoryInfo]::new($PackagePath)
    if (Test-IsReparsePoint -Item $root) {
        throw "Local package '$PackageName' root '$PackagePath' is a reparse point, junction, or symbolic link; staging refuses to traverse it."
    }

    $directories = [System.Collections.Generic.Stack[System.IO.DirectoryInfo]]::new()
    $directories.Push($root)
    try {
        while ($directories.Count -gt 0) {
            $directory = $directories.Pop()
            foreach ($entry in $directory.EnumerateFileSystemInfos()) {
                if ($entry -is [System.IO.DirectoryInfo] -and
                    -not (Test-IsDistributablePackageDirectory -Name $entry.Name)) {
                    continue
                }

                if ($entry -is [System.IO.FileInfo] -and
                    -not (Test-IsDistributablePackageFile -File $entry)) {
                    continue
                }

                if (Test-IsReparsePoint -Item $entry) {
                    throw "Local package '$PackageName' contains reparse point, junction, or symbolic link '$($entry.FullName)'; staging refuses to traverse it."
                }

                if ($entry -is [System.IO.DirectoryInfo]) {
                    $directories.Push($entry)
                }
            }
        }
    }
    catch {
        if ($_.Exception.Message -like "Local package '$PackageName'*") {
            throw
        }

        throw "Could not inspect local package '$PackageName' at '$PackagePath' for reparse points: $($_.Exception.Message)"
    }
}

function Test-IsDistributablePackageDirectory {
    param([Parameter(Mandatory = $true)][string]$Name)

    if ($Name.StartsWith('.', [System.StringComparison]::Ordinal)) {
        return $false
    }

    $excludedNames = @(
        '.git', '.svn', '.hg', '.plastic', '.vs', '.idea', '.vscode',
        'Library', 'Temp', 'tmp', 'Logs', 'obj', 'bin', 'Build', 'Builds', 'Artifacts',
        'UserSettings', 'MemoryCaptures', 'Recordings',
        'TestProject~', 'ConsumerProject~'
    )
    foreach ($excludedName in $excludedNames) {
        if ($Name.Equals($excludedName, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }

    return $true
}

function Test-IsDistributablePackageFile {
    param([Parameter(Mandatory = $true)][System.IO.FileInfo]$File)

    if ($File.Name.StartsWith('.', [System.StringComparison]::Ordinal) -or
        $File.Name.EndsWith('~', [System.StringComparison]::Ordinal)) {
        return $false
    }

    $excludedExtensions = @('.csproj', '.sln', '.suo', '.user')
    if ($File.Extension -in $excludedExtensions) {
        return $false
    }

    if ($File.Extension.Equals('.meta', [System.StringComparison]::OrdinalIgnoreCase)) {
        $assetName = [System.IO.Path]::GetFileNameWithoutExtension($File.Name)
        if (-not (Test-IsDistributablePackageDirectory -Name $assetName) -or
            [System.IO.Path]::GetExtension($assetName) -in $excludedExtensions) {
            return $false
        }
    }

    return $true
}

function Get-DistributablePackageFiles {
    param(
        [Parameter(Mandatory = $true)][string]$PackageName,
        [Parameter(Mandatory = $true)][string]$PackagePath
    )

    $filesByRelativePath = [System.Collections.Generic.Dictionary[string, System.IO.FileInfo]]::new(
        [System.StringComparer]::Ordinal)
    $directories = [System.Collections.Generic.Stack[System.IO.DirectoryInfo]]::new()
    $directories.Push([System.IO.DirectoryInfo]::new($PackagePath))

    try {
        while ($directories.Count -gt 0) {
            $directory = $directories.Pop()
            foreach ($file in $directory.EnumerateFiles()) {
                if (Test-IsReparsePoint -Item $file) {
                    throw "Local package '$PackageName' contains reparse point '$($file.FullName)'."
                }

                if (Test-IsDistributablePackageFile -File $file) {
                    $relativePath = [System.IO.Path]::GetRelativePath($PackagePath, $file.FullName).Replace('\', '/')
                    if (-not $filesByRelativePath.TryAdd($relativePath, $file)) {
                        throw "Local package '$PackageName' contains a duplicate Unity-visible path '$relativePath'."
                    }
                }
            }

            foreach ($childDirectory in $directory.EnumerateDirectories()) {
                if (Test-IsReparsePoint -Item $childDirectory) {
                    throw "Local package '$PackageName' contains reparse point '$($childDirectory.FullName)'."
                }

                if (Test-IsDistributablePackageDirectory -Name $childDirectory.Name) {
                    $directories.Push($childDirectory)
                }
            }
        }
    }
    catch {
        if ($_.Exception.Message -like "Local package '$PackageName'*") {
            throw
        }

        throw "Could not enumerate Unity-visible files for local package '$PackageName' at '$PackagePath': $($_.Exception.Message)"
    }

    $relativePaths = [string[]]@($filesByRelativePath.Keys)
    [System.Array]::Sort($relativePaths, [System.StringComparer]::Ordinal)
    $result = [System.Collections.Generic.List[object]]::new()
    foreach ($relativePath in $relativePaths) {
        $result.Add([pscustomobject]@{
            RelativePath = $relativePath
            SourcePath = $filesByRelativePath[$relativePath].FullName
        })
    }

    return $result.ToArray()
}

function Add-FileContentToIncrementalHash {
    param(
        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.IncrementalHash]$Hash,

        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][long]$ExpectedLength
    )

    [byte[]]$buffer = [byte[]]::new(1024 * 1024)
    $stream = [System.IO.FileStream]::new(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read,
        $buffer.Length,
        [System.IO.FileOptions]::SequentialScan)
    try {
        [long]$totalBytesRead = 0
        while (($bytesRead = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $Hash.AppendData($buffer, 0, $bytesRead)
            $totalBytesRead += $bytesRead
        }

        if ($totalBytesRead -ne $ExpectedLength) {
            throw "File '$Path' changed length while it was being hashed; expected $ExpectedLength bytes but read $totalBytesRead."
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Add-LabeledFileToIncrementalHash {
    param(
        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.IncrementalHash]$Hash,

        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $labelBytes = [System.Text.Encoding]::UTF8.GetBytes($Label)
    $fileLength = [System.IO.FileInfo]::new($Path).Length
    $Hash.AppendData([System.BitConverter]::GetBytes([long]$labelBytes.Length))
    $Hash.AppendData($labelBytes)
    $Hash.AppendData([System.BitConverter]::GetBytes([long]$fileLength))
    Add-FileContentToIncrementalHash `
        -Hash $Hash `
        -Path $Path `
        -ExpectedLength $fileLength
}

function Get-PackageContentDigest {
    param([Parameter(Mandatory = $true)][object[]]$Files)

    $hash = [System.Security.Cryptography.IncrementalHash]::CreateHash(
        [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        foreach ($file in $Files) {
            Add-LabeledFileToIncrementalHash `
                -Hash $hash `
                -Label ([string]$file.RelativePath) `
                -Path ([string]$file.SourcePath)
        }

        return [System.Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
    }
    finally {
        $hash.Dispose()
    }
}

function ConvertTo-SafePackageDirectoryName {
    param([Parameter(Mandatory = $true)][string]$PackageName)

    $safeName = [System.Text.RegularExpressions.Regex]::Replace($PackageName, '[^A-Za-z0-9._-]', '_').Trim('.')
    if ([string]::IsNullOrWhiteSpace($safeName)) {
        throw "Local package name '$PackageName' cannot be converted to a safe staging directory name."
    }

    return $safeName
}

function Read-LocalPackageMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$ManifestDependencyName,
        [Parameter(Mandatory = $true)][string]$PackagePath
    )

    $packageJsonPath = Join-Path $PackagePath 'package.json'
    if (-not (Test-Path -LiteralPath $packageJsonPath -PathType Leaf)) {
        throw "Local package dependency '$ManifestDependencyName' resolves to '$PackagePath', which has no package.json."
    }

    $document = $null
    try {
        $rawJson = [System.IO.File]::ReadAllText($packageJsonPath)
        $document = [System.Text.Json.JsonDocument]::Parse($rawJson)
        $root = $document.RootElement
        $nameProperty = Get-ExactJsonProperty `
            -Object $root `
            -Name 'name' `
            -Description "Local package manifest '$packageJsonPath'"
        if ($nameProperty.Value.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
            throw "Local package manifest '$packageJsonPath' has a non-string name."
        }

        $packageName = $nameProperty.Value.GetString()
        if (-not $packageName.Equals($ManifestDependencyName, [System.StringComparison]::Ordinal)) {
            throw "Local package dependency '$ManifestDependencyName' resolves to package '$packageName' at '$PackagePath'; package.json name must exactly match the manifest key."
        }

        $dependenciesProperty = Get-ExactJsonProperty `
            -Object $root `
            -Name 'dependencies' `
            -Description "Local package manifest '$packageJsonPath'" `
            -Optional
        if ($null -ne $dependenciesProperty) {
            if ($dependenciesProperty.Value.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
                throw "Local package manifest '$packageJsonPath' has a dependencies value that is not an object."
            }

            $seenDependencies = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
            foreach ($dependency in $dependenciesProperty.Value.EnumerateObject()) {
                if (-not $seenDependencies.Add($dependency.Name)) {
                    throw "Local package manifest '$packageJsonPath' contains duplicate dependency '$($dependency.Name)'."
                }

                if ($dependency.Value.ValueKind -eq [System.Text.Json.JsonValueKind]::String) {
                    $dependencyValue = $dependency.Value.GetString()
                    if ($dependencyValue.StartsWith('file:', [System.StringComparison]::OrdinalIgnoreCase)) {
                        $chain = "$ManifestDependencyName -> $($dependency.Name) ($dependencyValue)"
                        throw "Nested local package dependency chain '$chain' is unsupported. Add '$($dependency.Name)' as a top-level project dependency or publish/embed it before staging."
                    }
                }
            }
        }

        return [pscustomobject]@{
            Name = $packageName
            PackageJsonPath = $packageJsonPath
        }
    }
    catch [System.Text.Json.JsonException] {
        throw "Local package manifest '$packageJsonPath' contains malformed JSON: $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $document) {
            $document.Dispose()
        }
    }
}

function Get-LocalPackageStagingSpecs {
    param([Parameter(Mandatory = $true)][string]$SourceProjectPath)

    $sourceManifestPath = Join-Path $SourceProjectPath 'Packages\manifest.json'
    if (-not (Test-Path -LiteralPath $sourceManifestPath -PathType Leaf)) {
        throw "Source Unity manifest '$sourceManifestPath' does not exist."
    }

    $sourcePackagesPath = Split-Path -Parent $sourceManifestPath
    $document = $null
    try {
        $rawManifest = [System.IO.File]::ReadAllText($sourceManifestPath)
        $document = [System.Text.Json.JsonDocument]::Parse($rawManifest)
        $dependenciesProperty = Get-ExactJsonProperty `
            -Object $document.RootElement `
            -Name 'dependencies' `
            -Description "Source Unity manifest '$sourceManifestPath'"
        if ($dependenciesProperty.Value.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
            throw "Source Unity manifest '$sourceManifestPath' has a dependencies value that is not an object."
        }

        $localReferences = [System.Collections.Generic.Dictionary[string, string]]::new(
            [System.StringComparer]::Ordinal)
        $seenDependencies = [System.Collections.Generic.HashSet[string]]::new(
            [System.StringComparer]::Ordinal)
        foreach ($dependency in $dependenciesProperty.Value.EnumerateObject()) {
            if (-not $seenDependencies.Add($dependency.Name)) {
                throw "Source Unity manifest '$sourceManifestPath' contains duplicate dependency '$($dependency.Name)'."
            }

            if ($dependency.Value.ValueKind -eq [System.Text.Json.JsonValueKind]::String -and
                $dependency.Value.GetString().StartsWith('file:', [System.StringComparison]::OrdinalIgnoreCase)) {
                $localReferences.Add($dependency.Name, $dependency.Value.GetString())
            }
        }

        $dependencyNames = [string[]]@($localReferences.Keys)
        [System.Array]::Sort($dependencyNames, [System.StringComparer]::Ordinal)
        $specs = [System.Collections.Generic.List[object]]::new()
        foreach ($dependencyName in $dependencyNames) {
            $sourceVersion = $localReferences[$dependencyName]
            try {
                $localValue = [System.Uri]::UnescapeDataString($sourceVersion.Substring(5))
            }
            catch {
                throw "Local package dependency '$dependencyName' has invalid escaped file path '$sourceVersion': $($_.Exception.Message)"
            }

            if ([string]::IsNullOrWhiteSpace($localValue)) {
                throw "Local package dependency '$dependencyName' has an empty file path."
            }

            $packagePath = if ([System.IO.Path]::IsPathRooted($localValue)) {
                [System.IO.Path]::GetFullPath($localValue)
            }
            else {
                [System.IO.Path]::GetFullPath((Join-Path $sourcePackagesPath $localValue))
            }

            if (-not (Test-Path -LiteralPath $packagePath -PathType Container)) {
                throw "Local package dependency '$dependencyName' resolves to missing directory '$packagePath'."
            }

            $filesystemRoot = [System.IO.Path]::GetPathRoot($packagePath)
            if ($packagePath.TrimEnd('\', '/').Equals($filesystemRoot.TrimEnd('\', '/'), [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Local package dependency '$dependencyName' resolves to filesystem root '$packagePath', which cannot be staged safely."
            }

            Assert-NoPackageReparsePoint -PackageName $dependencyName -PackagePath $packagePath
            Read-LocalPackageMetadata `
                -ManifestDependencyName $dependencyName `
                -PackagePath $packagePath | Out-Null
            $files = @(Get-DistributablePackageFiles -PackageName $dependencyName -PackagePath $packagePath)
            $digest = Get-PackageContentDigest -Files $files
            $safeName = ConvertTo-SafePackageDirectoryName -PackageName $dependencyName
            $directoryName = "$safeName-$($digest.Substring(0, 12))"
            $stagedVersion = "file:LocalPackages/$directoryName"
            $specs.Add([pscustomobject]@{
                DependencyName = $dependencyName
                SourceVersion = $sourceVersion
                SourcePath = $packagePath
                Digest = $digest
                DirectoryName = $directoryName
                StagedVersion = $stagedVersion
                Files = $files
            })
        }

        return $specs.ToArray()
    }
    catch [System.Text.Json.JsonException] {
        throw "Source Unity manifest '$sourceManifestPath' contains malformed JSON: $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $document) {
            $document.Dispose()
        }
    }
}

function Update-StagedLockLocalVersion {
    param(
        [Parameter(Mandatory = $true)][string]$RawLock,
        [Parameter(Mandatory = $true)]$Spec,
        [Parameter(Mandatory = $true)][string]$LockPath
    )

    $document = $null
    try {
        $document = [System.Text.Json.JsonDocument]::Parse($RawLock)
        $dependenciesProperty = Get-ExactJsonProperty `
            -Object $document.RootElement `
            -Name 'dependencies' `
            -Description "Staged package lock '$LockPath'"
        if ($dependenciesProperty.Value.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
            throw "Staged package lock '$LockPath' has a dependencies value that is not an object."
        }

        $entryProperty = Get-ExactJsonProperty `
            -Object $dependenciesProperty.Value `
            -Name $Spec.DependencyName `
            -Description "Staged package lock '$LockPath' dependencies" `
            -Optional
        if ($null -eq $entryProperty) {
            return $RawLock
        }

        if ($entryProperty.Value.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
            throw "Staged package lock entry '$($Spec.DependencyName)' is not an object."
        }

        $depthProperty = Get-ExactJsonProperty `
            -Object $entryProperty.Value `
            -Name 'depth' `
            -Description "Staged package lock entry '$($Spec.DependencyName)'"
        $versionProperty = Get-ExactJsonProperty `
            -Object $entryProperty.Value `
            -Name 'version' `
            -Description "Staged package lock entry '$($Spec.DependencyName)'"
        if ($depthProperty.Value.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
            $depthProperty.Value.GetInt32() -ne 0 -or
            $versionProperty.Value.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or
            -not $versionProperty.Value.GetString().StartsWith('file:', [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Staged package lock entry '$($Spec.DependencyName)' exists but is not a depth-0 local file dependency. Delete/regenerate the lock or correct the manifest before staging."
        }

        $oldVersion = $versionProperty.Value.GetString()
    }
    catch [System.Text.Json.JsonException] {
        throw "Staged package lock '$LockPath' contains malformed JSON: $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $document) {
            $document.Dispose()
        }
    }

    return Set-ExactJsonStringValue `
        -RawJson $RawLock `
        -PropertyPath @('dependencies', $Spec.DependencyName, 'version') `
        -ExpectedValue $oldVersion `
        -NewValue $Spec.StagedVersion `
        -Description "Staged package lock version for '$($Spec.DependencyName)'"
}

function Copy-StagedLocalPackageFiles {
    param(
        [Parameter(Mandatory = $true)]$Spec,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    [System.IO.Directory]::CreateDirectory($DestinationPath) | Out-Null
    foreach ($file in $Spec.Files) {
        $sourceFile = Get-Item -LiteralPath ([string]$file.SourcePath) -Force
        if ($sourceFile -isnot [System.IO.FileInfo] -or (Test-IsReparsePoint -Item $sourceFile)) {
            throw "Local package '$($Spec.DependencyName)' source file '$($file.SourcePath)' became unsafe before copy."
        }

        $destinationFilePath = Join-Path $DestinationPath ([string]$file.RelativePath).Replace('/', '\')
        $resolvedDestinationFilePath = [System.IO.Path]::GetFullPath($destinationFilePath)
        $destinationPrefix = $DestinationPath.TrimEnd('\') + '\'
        if (-not $resolvedDestinationFilePath.StartsWith($destinationPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to copy local package '$($Spec.DependencyName)' file '$($file.RelativePath)' outside '$DestinationPath'."
        }

        $destinationDirectory = Split-Path -Parent $resolvedDestinationFilePath
        [System.IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null
        [System.IO.File]::Copy([string]$file.SourcePath, $resolvedDestinationFilePath, $false)
    }

    $copiedFiles = @(Get-DistributablePackageFiles `
        -PackageName $Spec.DependencyName `
        -PackagePath $DestinationPath)
    $copiedDigest = Get-PackageContentDigest -Files $copiedFiles
    if (-not $copiedDigest.Equals([string]$Spec.Digest, [System.StringComparison]::Ordinal)) {
        throw "Staged local package '$($Spec.DependencyName)' content changed during copy; expected digest '$($Spec.Digest)' but copied '$copiedDigest'. Retry after source writes finish."
    }
}

function Stage-UnityLocalPackages {
    param(
        [Parameter(Mandatory = $true)][string]$SourceProjectPath,
        [Parameter(Mandatory = $true)][string]$StagedProjectPath
    )

    $resolvedSourceProjectPath = (Resolve-Path -LiteralPath $SourceProjectPath).Path.TrimEnd('\', '/')
    $resolvedStagedProjectPath = (Resolve-Path -LiteralPath $StagedProjectPath).Path.TrimEnd('\', '/')
    if ($resolvedSourceProjectPath.Equals(
        $resolvedStagedProjectPath,
        [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Source and staged Unity project paths resolve to the same directory '$resolvedSourceProjectPath'; refusing to mutate the source project."
    }

    $SourceProjectPath = $resolvedSourceProjectPath
    $StagedProjectPath = $resolvedStagedProjectPath
    $specs = @(Get-LocalPackageStagingSpecs -SourceProjectPath $SourceProjectPath)
    if ($specs.Count -eq 0) {
        return @()
    }

    $stagedManifestPath = Join-Path $StagedProjectPath 'Packages\manifest.json'
    if (-not (Test-Path -LiteralPath $stagedManifestPath -PathType Leaf)) {
        throw "Staged Unity manifest '$stagedManifestPath' does not exist after project copy."
    }

    $localPackagesPath = Join-Path $StagedProjectPath 'Packages\LocalPackages'
    if (Test-Path -LiteralPath $localPackagesPath) {
        $localPackagesInfo = Get-Item -LiteralPath $localPackagesPath -Force
        if ($localPackagesInfo -isnot [System.IO.DirectoryInfo] -or
            (Test-IsReparsePoint -Item $localPackagesInfo)) {
            throw "Staged local package root '$localPackagesPath' is not a safe directory."
        }
    }

    $destinations = [System.Collections.Generic.Dictionary[string, string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($spec in $specs) {
        $destinationPath = Join-Path $localPackagesPath $spec.DirectoryName
        if ($destinations.ContainsKey($destinationPath)) {
            throw "Local packages '$($destinations[$destinationPath])' and '$($spec.DependencyName)' collide at staged destination '$destinationPath'."
        }

        $destinations.Add($destinationPath, $spec.DependencyName)
        if (Test-Path -LiteralPath $destinationPath) {
            throw "Staged local package destination '$destinationPath' already exists; refusing to merge or overwrite it."
        }
    }

    $stagedManifestState = Read-Utf8TextFileState -Path $stagedManifestPath
    $stagedManifestRaw = $stagedManifestState.Text
    foreach ($spec in $specs) {
        $stagedManifestRaw = Set-ExactJsonStringValue `
            -RawJson $stagedManifestRaw `
            -PropertyPath @('dependencies', $spec.DependencyName) `
            -ExpectedValue $spec.SourceVersion `
            -NewValue $spec.StagedVersion `
            -Description "Staged manifest dependency '$($spec.DependencyName)'"
    }

    $stagedLockPath = Join-Path $StagedProjectPath 'Packages\packages-lock.json'
    $hasStagedLock = Test-Path -LiteralPath $stagedLockPath -PathType Leaf
    $stagedLockState = if ($hasStagedLock) { Read-Utf8TextFileState -Path $stagedLockPath } else { $null }
    $stagedLockRaw = if ($hasStagedLock) { $stagedLockState.Text } else { $null }
    if ($hasStagedLock) {
        foreach ($spec in $specs) {
            $stagedLockRaw = Update-StagedLockLocalVersion `
                -RawLock $stagedLockRaw `
                -Spec $spec `
                -LockPath $stagedLockPath
        }
    }

    [System.IO.Directory]::CreateDirectory($localPackagesPath) | Out-Null
    foreach ($spec in $specs) {
        Copy-StagedLocalPackageFiles `
            -Spec $spec `
            -DestinationPath (Join-Path $localPackagesPath $spec.DirectoryName)
    }

    Write-Utf8TextFileState `
        -Path $stagedManifestPath `
        -Text $stagedManifestRaw `
        -HasPreamble $stagedManifestState.HasPreamble
    if ($hasStagedLock) {
        Write-Utf8TextFileState `
            -Path $stagedLockPath `
            -Text $stagedLockRaw `
            -HasPreamble $stagedLockState.HasPreamble
    }

    return $specs
}
