[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$helperPath = Join-Path $PSScriptRoot 'Stage-UnityLocalPackages.ps1'
if (-not (Test-Path -LiteralPath $helperPath -PathType Leaf)) {
    throw "Staging helper '$helperPath' does not exist."
}

. $helperPath

function Write-TestUtf8File {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [AllowNull()][string]$Content
    )

    $parentPath = Split-Path -Parent $Path
    [System.IO.Directory]::CreateDirectory($parentPath) | Out-Null
    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

function Assert-TestCondition {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-TestThrows {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$Description
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notmatch $Pattern) {
            throw "$Description threw '$($_.Exception.Message)', which did not match '$Pattern'."
        }

        return
    }

    throw "$Description did not throw."
}

function New-LocalPackageFixture {
    param(
        [Parameter(Mandatory = $true)][string]$RootPath,
        [Parameter(Mandatory = $true)][string]$Name,
        [AllowNull()][string]$PackageJson
    )

    $fixturePath = Join-Path $RootPath $Name
    $sourceProjectPath = Join-Path $fixturePath 'SourceProject'
    $stagedProjectPath = Join-Path $fixturePath 'StagedProject'
    $packagePath = Join-Path $fixturePath 'LocalPackage'
    foreach ($path in @(
        (Join-Path $sourceProjectPath 'Packages'),
        (Join-Path $stagedProjectPath 'Packages'),
        (Join-Path $packagePath 'Runtime')
    )) {
        [System.IO.Directory]::CreateDirectory($path) | Out-Null
    }

    $manifest = @'
{
  "dependencies": {
    "com.example.local": "file:../../LocalPackage",
    "com.example.remote": "https://example.invalid/remote.git#v1"
  },
  "testables": ["com.example.remote"]
}
'@
    $lock = @'
{
  "dependencies": {
    "com.example.local": {
      "version": "file:../../LocalPackage",
      "depth": 0,
      "source": "local",
      "dependencies": {}
    },
    "com.example.remote": {
      "version": "https://example.invalid/remote.git#v1",
      "depth": 0,
      "source": "git",
      "dependencies": {
        "com.example.transitive": "1.0.0"
      }
    }
  }
}
'@

    foreach ($projectPath in @($sourceProjectPath, $stagedProjectPath)) {
        Write-TestUtf8File -Path (Join-Path $projectPath 'Packages\manifest.json') -Content $manifest
        Write-TestUtf8File -Path (Join-Path $projectPath 'Packages\packages-lock.json') -Content $lock
    }

    if (-not [string]::IsNullOrEmpty($PackageJson)) {
        Write-TestUtf8File -Path (Join-Path $packagePath 'package.json') -Content $PackageJson
    }

    Write-TestUtf8File -Path (Join-Path $packagePath 'Runtime\Visible.cs') -Content 'internal sealed class Visible {}'
    Write-TestUtf8File -Path (Join-Path $packagePath '.git\ignored.txt') -Content 'vcs noise'
    Write-TestUtf8File -Path (Join-Path $packagePath '.idea\ignored.xml') -Content 'ide noise'
    Write-TestUtf8File -Path (Join-Path $packagePath 'Library\ignored.bin') -Content 'unity cache'
    Write-TestUtf8File -Path (Join-Path $packagePath 'Tools~\package-tool.ps1') -Content 'packaged tool'
    Write-TestUtf8File -Path (Join-Path $packagePath 'Generated.csproj') -Content 'ide project'
    Write-TestUtf8File -Path (Join-Path $packagePath 'Library.meta') -Content 'orphan cache metadata'
    Write-TestUtf8File -Path (Join-Path $packagePath 'tmp.meta') -Content 'orphan scratch metadata'
    Write-TestUtf8File -Path (Join-Path $packagePath 'Tools~.meta') -Content 'packaged tool metadata'
    Write-TestUtf8File -Path (Join-Path $packagePath 'Generated.csproj.meta') -Content 'orphan IDE metadata'

    return [pscustomobject]@{
        FixturePath = $fixturePath
        SourceProjectPath = $sourceProjectPath
        StagedProjectPath = $stagedProjectPath
        PackagePath = $packagePath
        Manifest = $manifest
        Lock = $lock
    }
}

$temporaryParent = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$testRoot = Join-Path $temporaryParent ('purrnet-local-package-stage-' + [System.Guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($testRoot) | Out-Null
$createdReparsePoints = [System.Collections.Generic.List[string]]::new()
$passed = [System.Collections.Generic.List[string]]::new()

try {
    $validPackageJson = @'
{
  "name": "com.example.local",
  "version": "1.0.0",
  "dependencies": {
    "com.example.remote": "1.0.0"
  }
}
'@
    $samePath = New-LocalPackageFixture -RootPath $testRoot -Name 'same-project-path' -PackageJson $validPackageJson
    $samePathManifestPath = Join-Path $samePath.SourceProjectPath 'Packages\manifest.json'
    $samePathManifestBefore = [System.IO.File]::ReadAllText($samePathManifestPath)
    Assert-TestThrows `
        -Action { Stage-UnityLocalPackages -SourceProjectPath $samePath.SourceProjectPath -StagedProjectPath $samePath.SourceProjectPath } `
        -Pattern 'same directory.*refusing to mutate the source project' `
        -Description 'Identical source and staged project fixture'
    Assert-TestCondition `
        -Condition ([System.IO.File]::ReadAllText($samePathManifestPath) -ceq $samePathManifestBefore) `
        -Message 'Identical-path rejection mutated the source manifest.'
    Assert-TestCondition `
        -Condition (-not (Test-Path -LiteralPath (Join-Path $samePath.SourceProjectPath 'Packages\LocalPackages'))) `
        -Message 'Identical-path rejection created a local-package staging directory in the source.'
    $passed.Add('identical-project-path-source-integrity')

    $success = New-LocalPackageFixture -RootPath $testRoot -Name 'success' -PackageJson $validPackageJson
    $sourceManifestBefore = [System.IO.File]::ReadAllText((Join-Path $success.SourceProjectPath 'Packages\manifest.json'))
    $sourceLockBefore = [System.IO.File]::ReadAllText((Join-Path $success.SourceProjectPath 'Packages\packages-lock.json'))
    $firstSpecs = @(Get-LocalPackageStagingSpecs -SourceProjectPath $success.SourceProjectPath)
    $secondSpecs = @(Get-LocalPackageStagingSpecs -SourceProjectPath $success.SourceProjectPath)
    Assert-TestCondition -Condition ($firstSpecs.Count -eq 1) -Message 'Success fixture did not discover exactly one local package.'
    Assert-TestCondition `
        -Condition ($firstSpecs[0].Digest -ceq $secondSpecs[0].Digest) `
        -Message 'Identical package contents did not produce a deterministic digest.'

    $stagedSpecs = @(Stage-UnityLocalPackages `
        -SourceProjectPath $success.SourceProjectPath `
        -StagedProjectPath $success.StagedProjectPath)
    $spec = $stagedSpecs[0]
    $destinationPath = Join-Path $success.StagedProjectPath "Packages\LocalPackages\$($spec.DirectoryName)"
    Assert-TestCondition -Condition (Test-Path -LiteralPath (Join-Path $destinationPath 'Runtime\Visible.cs') -PathType Leaf) -Message 'Unity-visible source file was not copied.'
    Assert-TestCondition -Condition (Test-Path -LiteralPath (Join-Path $destinationPath 'package.json') -PathType Leaf) -Message 'package.json was not copied.'
    Assert-TestCondition -Condition (-not (Test-Path -LiteralPath (Join-Path $destinationPath '.git'))) -Message 'VCS directory was copied.'
    Assert-TestCondition -Condition (-not (Test-Path -LiteralPath (Join-Path $destinationPath '.idea'))) -Message 'IDE directory was copied.'
    Assert-TestCondition -Condition (-not (Test-Path -LiteralPath (Join-Path $destinationPath 'Library'))) -Message 'Unity cache directory was copied.'
    Assert-TestCondition -Condition (Test-Path -LiteralPath (Join-Path $destinationPath 'Tools~\package-tool.ps1') -PathType Leaf) -Message 'Packaged Tools~ content was not copied.'
    Assert-TestCondition -Condition (-not (Test-Path -LiteralPath (Join-Path $destinationPath 'Generated.csproj'))) -Message 'IDE project file was copied.'
    Assert-TestCondition -Condition (-not (Test-Path -LiteralPath (Join-Path $destinationPath 'Library.meta'))) -Message 'Excluded cache-directory metadata was copied.'
    Assert-TestCondition -Condition (-not (Test-Path -LiteralPath (Join-Path $destinationPath 'tmp.meta'))) -Message 'Excluded scratch-directory metadata was copied.'
    Assert-TestCondition -Condition (Test-Path -LiteralPath (Join-Path $destinationPath 'Tools~.meta') -PathType Leaf) -Message 'Packaged Tools~ metadata was not copied.'
    Assert-TestCondition -Condition (-not (Test-Path -LiteralPath (Join-Path $destinationPath 'Generated.csproj.meta'))) -Message 'Excluded IDE-file metadata was copied.'

    $stagedManifest = [System.IO.File]::ReadAllText((Join-Path $success.StagedProjectPath 'Packages\manifest.json'))
    $stagedLock = [System.IO.File]::ReadAllText((Join-Path $success.StagedProjectPath 'Packages\packages-lock.json'))
    $expectedManifest = $success.Manifest.Replace('"file:../../LocalPackage"', ('"' + $spec.StagedVersion + '"'))
    $expectedLock = $success.Lock.Replace('"version": "file:../../LocalPackage"', ('"version": "' + $spec.StagedVersion + '"'))
    Assert-TestCondition -Condition ($stagedManifest -ceq $expectedManifest) -Message 'Staged manifest changed bytes outside the local dependency value.'
    Assert-TestCondition -Condition ($stagedLock -ceq $expectedLock) -Message 'Staged lock changed bytes outside the matching local version value.'
    Assert-TestCondition `
        -Condition ([System.IO.File]::ReadAllText((Join-Path $success.SourceProjectPath 'Packages\manifest.json')) -ceq $sourceManifestBefore) `
        -Message 'Source manifest was mutated.'
    Assert-TestCondition `
        -Condition ([System.IO.File]::ReadAllText((Join-Path $success.SourceProjectPath 'Packages\packages-lock.json')) -ceq $sourceLockBefore) `
        -Message 'Source package lock was mutated.'
    $passed.Add('success-copy-rewrite-digest-source-integrity')

    $missing = New-LocalPackageFixture -RootPath $testRoot -Name 'missing-package-json' -PackageJson $null
    Assert-TestThrows `
        -Action { Stage-UnityLocalPackages -SourceProjectPath $missing.SourceProjectPath -StagedProjectPath $missing.StagedProjectPath } `
        -Pattern 'no package\.json' `
        -Description 'Missing package.json fixture'
    $passed.Add('missing-package-json')

    $mismatchPackageJson = $validPackageJson.Replace('com.example.local', 'com.example.other')
    $mismatch = New-LocalPackageFixture -RootPath $testRoot -Name 'name-mismatch' -PackageJson $mismatchPackageJson
    Assert-TestThrows `
        -Action { Stage-UnityLocalPackages -SourceProjectPath $mismatch.SourceProjectPath -StagedProjectPath $mismatch.StagedProjectPath } `
        -Pattern 'must exactly match' `
        -Description 'Package name mismatch fixture'
    $passed.Add('package-name-mismatch')

    $nestedPackageJson = @'
{
  "name": "com.example.local",
  "version": "1.0.0",
  "dependencies": {
    "com.example.nested": "file:../NestedPackage"
  }
}
'@
    $nested = New-LocalPackageFixture -RootPath $testRoot -Name 'nested-file-dependency' -PackageJson $nestedPackageJson
    Assert-TestThrows `
        -Action { Stage-UnityLocalPackages -SourceProjectPath $nested.SourceProjectPath -StagedProjectPath $nested.StagedProjectPath } `
        -Pattern 'com\.example\.local -> com\.example\.nested.*unsupported' `
        -Description 'Nested file dependency fixture'
    $passed.Add('nested-file-dependency')

    $reparse = New-LocalPackageFixture -RootPath $testRoot -Name 'reparse-point' -PackageJson $validPackageJson
    $externalTargetPath = Join-Path $reparse.FixturePath 'ExternalTarget'
    [System.IO.Directory]::CreateDirectory($externalTargetPath) | Out-Null
    Write-TestUtf8File -Path (Join-Path $externalTargetPath 'Outside.cs') -Content 'internal sealed class Outside {}'
    $junctionPath = Join-Path $reparse.PackagePath 'Runtime\Linked'
    try {
        New-Item -ItemType Junction -Path $junctionPath -Target $externalTargetPath -ErrorAction Stop | Out-Null
        $createdReparsePoints.Add($junctionPath)
        Assert-TestThrows `
            -Action { Stage-UnityLocalPackages -SourceProjectPath $reparse.SourceProjectPath -StagedProjectPath $reparse.StagedProjectPath } `
            -Pattern 'reparse point|junction|symbolic link' `
            -Description 'Reparse point fixture'
        $passed.Add('reparse-point')
    }
    catch {
        if (Test-Path -LiteralPath $junctionPath) {
            throw
        }

        Write-Warning "SKIP reparse-point: Windows did not permit creating a junction: $($_.Exception.Message)"
    }

    [ordered]@{
        status = 'passed'
        testCount = $passed.Count
        tests = $passed
    } | ConvertTo-Json -Depth 4
}
finally {
    foreach ($reparsePoint in $createdReparsePoints) {
        if (Test-Path -LiteralPath $reparsePoint) {
            [System.IO.Directory]::Delete($reparsePoint)
        }
    }

    $resolvedTestRoot = [System.IO.Path]::GetFullPath($testRoot)
    $requiredPrefix = $temporaryParent.TrimEnd('\') + '\purrnet-local-package-stage-'
    if (-not $resolvedTestRoot.StartsWith($requiredPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean unexpected fixture path '$resolvedTestRoot'."
    }

    if (Test-Path -LiteralPath $resolvedTestRoot -PathType Container) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
