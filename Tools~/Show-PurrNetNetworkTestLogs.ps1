[CmdletBinding(DefaultParameterSetName = 'Artifacts')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Run')]
    [string]$RunPath,

    [Parameter(Mandatory = $true, ParameterSetName = 'Artifacts')]
    [string]$ArtifactsPath,

    [ValidateRange(10, 100000)]
    [int]$TailLines = 500,

    [ValidateRange(250, 60000)]
    [int]$RefreshMilliseconds = 1000,

    [switch]$FollowNewestRun,

    [string]$IgnoreRunPath,

    [ValidateSet('Evidence', 'RawLog')]
    [string]$DefaultTab = 'Evidence'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not [System.OperatingSystem]::IsWindows()) {
    throw 'The live log viewer uses WPF and is only available on Windows.'
}

# WPF requires an STA thread. pwsh can be configured to start in MTA, so relaunch this
# script in a child STA pwsh process while preserving every user-supplied argument.
if ([System.Threading.Thread]::CurrentThread.ApartmentState -ne [System.Threading.ApartmentState]::STA) {
    $pwshPath = Join-Path $PSHOME 'pwsh.exe'
    if (-not (Test-Path -LiteralPath $pwshPath -PathType Leaf)) {
        throw "PowerShell 7 could not be found at '$pwshPath'. Run this script with 'pwsh -STA'."
    }

    $forwardedArguments = @('-NoProfile', '-STA', '-File', $PSCommandPath)
    if ($PSCmdlet.ParameterSetName -eq 'Run') {
        $forwardedArguments += @('-RunPath', $RunPath)
    }
    else {
        $forwardedArguments += @('-ArtifactsPath', $ArtifactsPath)
    }

    $forwardedArguments += @(
        '-TailLines', $TailLines.ToString([System.Globalization.CultureInfo]::InvariantCulture),
        '-RefreshMilliseconds', $RefreshMilliseconds.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    )

    if ($FollowNewestRun) {
        $forwardedArguments += '-FollowNewestRun'
    }

    if (-not [string]::IsNullOrWhiteSpace($IgnoreRunPath)) {
        $forwardedArguments += @('-IgnoreRunPath', $IgnoreRunPath)
    }

    $forwardedArguments += @('-DefaultTab', $DefaultTab)

    & $pwshPath @forwardedArguments
    exit $LASTEXITCODE
}

if ($FollowNewestRun -and $PSCmdlet.ParameterSetName -eq 'Run') {
    throw '-FollowNewestRun requires -ArtifactsPath because a fixed -RunPath cannot follow later suite runs.'
}

if (-not [string]::IsNullOrWhiteSpace($IgnoreRunPath) -and -not $FollowNewestRun) {
    throw '-IgnoreRunPath requires -FollowNewestRun.'
}

function Find-NewestRunDirectory {
    param([Parameter(Mandatory = $true)][string]$ResolvedArtifactsPath)

    $runsPath = Join-Path $ResolvedArtifactsPath 'Runs'
    if (-not (Test-Path -LiteralPath $runsPath -PathType Container)) {
        return $null
    }

    $newestRun = Get-ChildItem -LiteralPath $runsPath -Directory |
        Sort-Object -Property LastWriteTimeUtc, Name -Descending |
        Select-Object -First 1

    if ($null -eq $newestRun) {
        return $null
    }

    return $newestRun.FullName
}

function Resolve-RunDirectory {
    param(
        [string]$RequestedRunPath,
        [string]$RequestedArtifactsPath,
        [Parameter(Mandatory = $true)][string]$ParameterSetName,
        [Parameter(Mandatory = $true)][bool]$AllowMissingRun
    )

    if ($ParameterSetName -eq 'Run') {
        $fullRunPath = [System.IO.Path]::GetFullPath($RequestedRunPath)
        if (-not (Test-Path -LiteralPath $fullRunPath -PathType Container)) {
            throw "Run directory '$fullRunPath' does not exist. Pass a directory created under Artifacts/NetworkTests/Runs."
        }

        return $fullRunPath
    }

    $fullArtifactsPath = [System.IO.Path]::GetFullPath($RequestedArtifactsPath)
    if (-not (Test-Path -LiteralPath $fullArtifactsPath -PathType Container)) {
        throw "Artifacts directory '$fullArtifactsPath' does not exist. Run the network test coordinator first."
    }

    $newestRunPath = Find-NewestRunDirectory -ResolvedArtifactsPath $fullArtifactsPath
    if ($null -eq $newestRunPath -and -not $AllowMissingRun) {
        throw "No run directories were found under '$(Join-Path $fullArtifactsPath 'Runs')'. Start a network test before opening the viewer."
    }

    return $newestRunPath
}

function Read-SharedText {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = $null
    $reader = $null
    try {
        $stream = [System.IO.FileStream]::new(
            $Path,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
        $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8, $true)
        return $reader.ReadToEnd()
    }
    finally {
        if ($null -ne $reader) {
            $reader.Dispose()
        }
        elseif ($null -ne $stream) {
            $stream.Dispose()
        }
    }
}

function Read-JsonArtifact {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [pscustomobject]@{
            Exists = $false
            Value = $null
            Error = $null
        }
    }

    try {
        $content = Read-SharedText -Path $Path
        return [pscustomobject]@{
            Exists = $true
            Value = $content | ConvertFrom-Json
            Error = $null
        }
    }
    catch {
        return [pscustomobject]@{
            Exists = $true
            Value = $null
            Error = $_.Exception.Message
        }
    }
}

function Read-TailText {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][int]$LineCount
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return "Waiting for log file:`r`n$Path"
    }

    try {
        # Keep memory bounded by the requested line count even when a Player log is long.
        $lines = [System.Collections.Generic.Queue[string]]::new($LineCount)
        $stream = $null
        $reader = $null
        try {
            $stream = [System.IO.FileStream]::new(
                $Path,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
            $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8, $true)

            while (-not $reader.EndOfStream) {
                if ($lines.Count -eq $LineCount) {
                    $lines.Dequeue() | Out-Null
                }

                $lines.Enqueue($reader.ReadLine())
            }
        }
        finally {
            if ($null -ne $reader) {
                $reader.Dispose()
            }
            elseif ($null -ne $stream) {
                $stream.Dispose()
            }
        }

        return [string]::Join([Environment]::NewLine, $lines)
    }
    catch {
        return "Log is temporarily unavailable: $($_.Exception.Message)`r`n$Path"
    }
}

function Format-ArtifactEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$Role,
        [Parameter(Mandatory = $true)]$ReadyArtifact,
        [Parameter(Mandatory = $true)]$ResultArtifact
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("ROLE: $Role")

    $artifact = if ($ResultArtifact.Exists -and $null -eq $ResultArtifact.Error) {
        $ResultArtifact.Value
    }
    elseif ($ReadyArtifact.Exists -and $null -eq $ReadyArtifact.Error) {
        $ReadyArtifact.Value
    }
    else {
        $null
    }

    if ($null -ne $artifact) {
        foreach ($propertyName in @('runId', 'scenarioId')) {
            $property = $artifact.PSObject.Properties[$propertyName]
            if ($null -ne $property -and -not [string]::IsNullOrWhiteSpace([string]$property.Value)) {
                $lines.Add("$($propertyName.ToUpperInvariant()): $($property.Value)")
            }
        }
    }

    $lines.Add('')
    $lines.Add('READINESS')
    if (-not $ReadyArtifact.Exists) {
        $lines.Add('  Waiting for ready artifact...')
    }
    elseif ($null -ne $ReadyArtifact.Error) {
        $lines.Add("  Unreadable: $($ReadyArtifact.Error)")
    }
    else {
        $lines.Add('  Ready artifact received')
        $readyMilestones = $ReadyArtifact.Value.PSObject.Properties['milestones']
        if ($null -ne $readyMilestones) {
            $index = 1
            foreach ($milestone in @($readyMilestones.Value)) {
                $lines.Add(('  {0:D2}. {1}' -f $index, $milestone))
                $index++
            }
        }
    }

    $lines.Add('')
    $lines.Add('RESULT')
    if (-not $ResultArtifact.Exists) {
        $lines.Add('  Waiting for result artifact...')
    }
    elseif ($null -ne $ResultArtifact.Error) {
        $lines.Add("  Unreadable: $($ResultArtifact.Error)")
    }
    else {
        $result = $ResultArtifact.Value
        $status = $result.PSObject.Properties['status']
        $revision = $result.PSObject.Properties['stateRevision']
        $failure = $result.PSObject.Properties['failure']
        $lines.Add("  Status: $(if ($null -eq $status) { 'missing' } else { $status.Value })")
        $lines.Add("  State revision: $(if ($null -eq $revision) { 'missing' } else { $revision.Value })")

        if ($null -ne $failure -and -not [string]::IsNullOrWhiteSpace([string]$failure.Value)) {
            $lines.Add("  Failure: $($failure.Value)")
        }

        $resultMilestones = $result.PSObject.Properties['milestones']
        if ($null -ne $resultMilestones) {
            $lines.Add('')
            $lines.Add('ROLE-SPECIFIC MILESTONES')
            $index = 1
            foreach ($milestone in @($resultMilestones.Value)) {
                $lines.Add(('  {0:D2}. {1}' -f $index, $milestone))
                $index++
            }
        }

        $assertions = $result.PSObject.Properties['assertions']
        if ($null -ne $assertions -and $null -ne $assertions.Value) {
            $lines.Add('')
            $lines.Add('ROLE-OWNED ASSERTIONS')
            foreach ($assertion in @($assertions.Value)) {
                $lines.Add("  PASS: $assertion")
            }
        }

        $evidence = $result.PSObject.Properties['roleEvidence']
        if ($null -ne $evidence -and $null -ne $evidence.Value) {
            $lines.Add('')
            $lines.Add('ROLE-LOCAL EVIDENCE')
            foreach ($item in $evidence.Value.PSObject.Properties) {
                $value = if ($null -eq $item.Value) { 'null' } elseif ($item.Value -is [bool]) { $item.Value.ToString().ToLowerInvariant() } elseif ([string]::IsNullOrEmpty([string]$item.Value)) { '<empty>' } else { [string]$item.Value }
                $lines.Add("  $($item.Name) = $value")
            }
        }

        $facts = $result.PSObject.Properties['sharedFacts']
        if ($null -ne $facts -and $null -ne $facts.Value) {
            $lines.Add('')
            $lines.Add('SHARED FACTS (EXPECTED TO AGREE)')
            foreach ($fact in $facts.Value.PSObject.Properties) {
                $value = if ($null -eq $fact.Value) { 'null' } elseif ($fact.Value -is [bool]) { $fact.Value.ToString().ToLowerInvariant() } elseif ([string]::IsNullOrEmpty([string]$fact.Value)) { '<empty>' } else { [string]$fact.Value }
                $lines.Add("  $($fact.Name) = $value")
            }
        }
    }

    return [string]::Join([Environment]::NewLine, $lines)
}

function New-RoleView {
    param(
        [Parameter(Mandatory = $true)][string]$Role,
        [Parameter(Mandatory = $true)][int]$Column,
        [Parameter(Mandatory = $true)][string]$SelectedTab
    )

    $headerPanel = [System.Windows.Controls.StackPanel]::new()
    $headerPanel.Orientation = [System.Windows.Controls.Orientation]::Vertical

    $roleText = [System.Windows.Controls.TextBlock]::new()
    $roleText.Text = $Role
    $roleText.FontWeight = [System.Windows.FontWeights]::SemiBold
    $roleText.FontSize = 15
    $headerPanel.Children.Add($roleText) | Out-Null

    $statusText = [System.Windows.Controls.TextBlock]::new()
    $statusText.Text = 'Waiting for artifacts'
    $statusText.Margin = [System.Windows.Thickness]::new(0, 3, 0, 0)
    $statusText.FontSize = 12
    $headerPanel.Children.Add($statusText) | Out-Null

    $artifactText = [System.Windows.Controls.TextBlock]::new()
    $artifactText.Text = 'Ready: no | Result: no'
    $artifactText.Foreground = [System.Windows.Media.Brushes]::Gray
    $artifactText.FontSize = 11
    $headerPanel.Children.Add($artifactText) | Out-Null

    $tabControl = [System.Windows.Controls.TabControl]::new()

    $evidenceText = [System.Windows.Controls.TextBox]::new()
    $evidenceText.IsReadOnly = $true
    $evidenceText.AcceptsReturn = $true
    $evidenceText.TextWrapping = [System.Windows.TextWrapping]::NoWrap
    $evidenceText.VerticalScrollBarVisibility = [System.Windows.Controls.ScrollBarVisibility]::Auto
    $evidenceText.HorizontalScrollBarVisibility = [System.Windows.Controls.ScrollBarVisibility]::Auto
    $evidenceText.FontFamily = [System.Windows.Media.FontFamily]::new('Consolas')
    $evidenceText.FontSize = 12
    $evidenceText.Background = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#111827')
    $evidenceText.Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#E5E7EB')
    $evidenceText.BorderThickness = [System.Windows.Thickness]::new(0)
    $evidenceText.Padding = [System.Windows.Thickness]::new(8)

    $evidenceTab = [System.Windows.Controls.TabItem]::new()
    $evidenceTab.Header = 'Harness evidence'
    $evidenceTab.Content = $evidenceText
    $tabControl.Items.Add($evidenceTab) | Out-Null

    $logText = [System.Windows.Controls.TextBox]::new()
    $logText.IsReadOnly = $true
    $logText.AcceptsReturn = $true
    $logText.AcceptsTab = $true
    $logText.TextWrapping = [System.Windows.TextWrapping]::NoWrap
    $logText.VerticalScrollBarVisibility = [System.Windows.Controls.ScrollBarVisibility]::Auto
    $logText.HorizontalScrollBarVisibility = [System.Windows.Controls.ScrollBarVisibility]::Auto
    $logText.FontFamily = [System.Windows.Media.FontFamily]::new('Consolas')
    $logText.FontSize = 12
    $logText.Background = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#111827')
    $logText.Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#E5E7EB')
    $logText.BorderThickness = [System.Windows.Thickness]::new(0)
    $logText.Padding = [System.Windows.Thickness]::new(8)

    $logTab = [System.Windows.Controls.TabItem]::new()
    $logTab.Header = 'Raw Unity log'
    $logTab.Content = $logText
    $tabControl.Items.Add($logTab) | Out-Null
    $tabControl.SelectedIndex = if ($SelectedTab -eq 'RawLog') { 1 } else { 0 }

    $group = [System.Windows.Controls.GroupBox]::new()
    $group.Header = $headerPanel
    $group.Content = $tabControl
    $group.Margin = [System.Windows.Thickness]::new(5)
    [System.Windows.Controls.Grid]::SetColumn($group, $Column)

    return [pscustomobject]@{
        Group = $group
        StatusText = $statusText
        ArtifactText = $artifactText
        EvidenceText = $evidenceText
        LogText = $logText
    }
}

function Get-RoleStatus {
    param(
        [Parameter(Mandatory = $true)]$ReadyArtifact,
        [Parameter(Mandatory = $true)]$ResultArtifact,
        [Parameter(Mandatory = $true)][bool]$LogExists
    )

    if ($ResultArtifact.Exists) {
        if ($null -ne $ResultArtifact.Error) {
            return [pscustomobject]@{
                Label = 'RESULT UNREADABLE'
                Detail = $ResultArtifact.Error
                Category = 'Error'
            }
        }

        $statusProperty = $ResultArtifact.Value.PSObject.Properties['status']
        if ($null -eq $statusProperty -or [string]::IsNullOrWhiteSpace([string]$statusProperty.Value)) {
            return [pscustomobject]@{
                Label = 'RESULT INVALID'
                Detail = 'The result JSON has no status value.'
                Category = 'Error'
            }
        }

        $status = [string]$statusProperty.Value
        if ($status -ieq 'passed') {
            return [pscustomobject]@{ Label = 'PASSED'; Detail = ''; Category = 'Passed' }
        }

        if ($status -ieq 'failed') {
            $failureProperty = $ResultArtifact.Value.PSObject.Properties['failure']
            $failure = if ($null -eq $failureProperty) { '' } else { [string]$failureProperty.Value }
            if ([string]::IsNullOrWhiteSpace($failure)) {
                $failure = 'No failure message was provided.'
            }

            return [pscustomobject]@{ Label = 'FAILED'; Detail = $failure; Category = 'Failed' }
        }

        return [pscustomobject]@{
            Label = "RESULT: $status"
            Detail = 'The result contains an unrecognized status.'
            Category = 'Error'
        }
    }

    if ($ReadyArtifact.Exists) {
        if ($null -ne $ReadyArtifact.Error) {
            return [pscustomobject]@{
                Label = 'READY FILE UNREADABLE'
                Detail = $ReadyArtifact.Error
                Category = 'Error'
            }
        }

        return [pscustomobject]@{ Label = 'RUNNING (READY)'; Detail = ''; Category = 'Running' }
    }

    if ($LogExists) {
        return [pscustomobject]@{ Label = 'STARTING'; Detail = ''; Category = 'Running' }
    }

    return [pscustomobject]@{ Label = 'WAITING FOR PROCESS'; Detail = ''; Category = 'Waiting' }
}

$resolvedArtifactsPath = if ($PSCmdlet.ParameterSetName -eq 'Artifacts') {
    [System.IO.Path]::GetFullPath($ArtifactsPath)
}
else {
    $null
}

$resolvedNewestRunPath = Resolve-RunDirectory `
    -RequestedRunPath $RunPath `
    -RequestedArtifactsPath $ArtifactsPath `
    -ParameterSetName $PSCmdlet.ParameterSetName `
    -AllowMissingRun $FollowNewestRun.IsPresent

$resolvedIgnoredRunPath = if ([string]::IsNullOrWhiteSpace($IgnoreRunPath)) {
    $null
}
else {
    [System.IO.Path]::GetFullPath($IgnoreRunPath)
}

$initialRunPath = if ($resolvedNewestRunPath -eq $resolvedIgnoredRunPath) {
    $null
}
else {
    $resolvedNewestRunPath
}
$viewerState = [pscustomobject]@{
    RunPath = $initialRunPath
    BaselineRunPath = $resolvedIgnoredRunPath
}

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$roles = [ordered]@{
    Server = 'server'
    OwnerClient = 'owner'
    ObserverClient = 'observer'
}

$window = [System.Windows.Window]::new()
$initialRunLabel = if ($null -eq $viewerState.RunPath) {
    'waiting for first run'
}
else {
    [System.IO.Path]::GetFileName($viewerState.RunPath)
}
$window.Title = "Caffeinated Network Testing Viewer - $initialRunLabel"
$window.Width = 1500
$window.Height = 850
$window.MinWidth = 900
$window.MinHeight = 500
$window.WindowStartupLocation = [System.Windows.WindowStartupLocation]::CenterScreen

$rootPanel = [System.Windows.Controls.DockPanel]::new()
$rootPanel.Margin = [System.Windows.Thickness]::new(10)
$window.Content = $rootPanel

$headerPanel = [System.Windows.Controls.StackPanel]::new()
$headerPanel.Margin = [System.Windows.Thickness]::new(5, 0, 5, 8)
[System.Windows.Controls.DockPanel]::SetDock($headerPanel, [System.Windows.Controls.Dock]::Top)
$rootPanel.Children.Add($headerPanel) | Out-Null

$titleText = [System.Windows.Controls.TextBlock]::new()
$titleText.Text = 'PurrNet multi-process network test'
$titleText.FontSize = 19
$titleText.FontWeight = [System.Windows.FontWeights]::SemiBold
$headerPanel.Children.Add($titleText) | Out-Null

$runText = [System.Windows.Controls.TextBlock]::new()
$runText.Text = if ($null -eq $viewerState.RunPath) {
    "Waiting for the first suite run under $resolvedArtifactsPath"
}
elseif ($FollowNewestRun) {
    "$($viewerState.RunPath) (following newest suite run)"
}
else {
    $viewerState.RunPath
}
$runText.Foreground = [System.Windows.Media.Brushes]::DimGray
$runText.TextWrapping = [System.Windows.TextWrapping]::Wrap
$headerPanel.Children.Add($runText) | Out-Null

$overallText = [System.Windows.Controls.TextBlock]::new()
$overallText.Text = 'Overall: IN PROGRESS'
$overallText.FontSize = 14
$overallText.FontWeight = [System.Windows.FontWeights]::Bold
$overallText.Margin = [System.Windows.Thickness]::new(0, 5, 0, 0)
$headerPanel.Children.Add($overallText) | Out-Null

$footerPanel = [System.Windows.Controls.DockPanel]::new()
$footerPanel.Margin = [System.Windows.Thickness]::new(5, 8, 5, 0)
[System.Windows.Controls.DockPanel]::SetDock($footerPanel, [System.Windows.Controls.Dock]::Bottom)
$rootPanel.Children.Add($footerPanel) | Out-Null

$refreshInfo = [System.Windows.Controls.TextBlock]::new()
$refreshInfo.Text = "Auto-refresh: ${RefreshMilliseconds} ms | Showing last ${TailLines} lines per role"
$refreshInfo.VerticalAlignment = [System.Windows.VerticalAlignment]::Center
$refreshInfo.Foreground = [System.Windows.Media.Brushes]::DimGray
[System.Windows.Controls.DockPanel]::SetDock($refreshInfo, [System.Windows.Controls.Dock]::Left)
$footerPanel.Children.Add($refreshInfo) | Out-Null

$buttonPanel = [System.Windows.Controls.StackPanel]::new()
$buttonPanel.Orientation = [System.Windows.Controls.Orientation]::Horizontal
$buttonPanel.HorizontalAlignment = [System.Windows.HorizontalAlignment]::Right
[System.Windows.Controls.DockPanel]::SetDock($buttonPanel, [System.Windows.Controls.Dock]::Right)
$footerPanel.Children.Add($buttonPanel) | Out-Null

$refreshButton = [System.Windows.Controls.Button]::new()
$refreshButton.Content = 'Refresh now'
$refreshButton.MinWidth = 100
$refreshButton.Margin = [System.Windows.Thickness]::new(0, 0, 8, 0)
$refreshButton.Padding = [System.Windows.Thickness]::new(12, 6, 12, 6)
$buttonPanel.Children.Add($refreshButton) | Out-Null

$closeButton = [System.Windows.Controls.Button]::new()
$closeButton.Content = 'Close'
$closeButton.MinWidth = 80
$closeButton.Padding = [System.Windows.Thickness]::new(12, 6, 12, 6)
$buttonPanel.Children.Add($closeButton) | Out-Null

$logGrid = [System.Windows.Controls.Grid]::new()
foreach ($nullValue in 0..2) {
    $columnDefinition = [System.Windows.Controls.ColumnDefinition]::new()
    $columnDefinition.Width = [System.Windows.GridLength]::new(1, [System.Windows.GridUnitType]::Star)
    $logGrid.ColumnDefinitions.Add($columnDefinition)
}
$rootPanel.Children.Add($logGrid) | Out-Null

$roleViews = [ordered]@{}
$columnIndex = 0
foreach ($role in $roles.Keys) {
    $roleView = New-RoleView -Role $role -Column $columnIndex -SelectedTab $DefaultTab
    $roleViews[$role] = $roleView
    $logGrid.Children.Add($roleView.Group) | Out-Null
    $columnIndex++
}

$refreshAction = {
    if ($FollowNewestRun) {
        $newestRunPath = Find-NewestRunDirectory -ResolvedArtifactsPath $resolvedArtifactsPath
        $isBaselineRun = $null -eq $viewerState.RunPath -and
            $null -ne $viewerState.BaselineRunPath -and
            $newestRunPath -eq $viewerState.BaselineRunPath
        if ($null -ne $newestRunPath -and -not $isBaselineRun -and $newestRunPath -ne $viewerState.RunPath) {
            $viewerState.RunPath = $newestRunPath
            $runText.Text = "$newestRunPath (following newest suite run)"
        }
    }

    if ($null -eq $viewerState.RunPath) {
        $overallText.Text = 'Overall: WAITING FOR FIRST RUN'
        $overallText.Foreground = [System.Windows.Media.Brushes]::DimGray
        $window.Title = 'Caffeinated Network Testing Viewer - waiting for first run'
        return
    }

    $categories = [System.Collections.Generic.List[string]]::new()

    foreach ($role in $roles.Keys) {
        $prefix = $roles[$role]
        $readyPath = Join-Path $viewerState.RunPath "$prefix.ready.json"
        $resultPath = Join-Path $viewerState.RunPath "$prefix.result.json"
        $logPath = Join-Path $viewerState.RunPath "$prefix.log"

        $readyArtifact = Read-JsonArtifact -Path $readyPath
        $resultArtifact = Read-JsonArtifact -Path $resultPath
        $logExists = Test-Path -LiteralPath $logPath -PathType Leaf
        $roleStatus = Get-RoleStatus `
            -ReadyArtifact $readyArtifact `
            -ResultArtifact $resultArtifact `
            -LogExists $logExists
        $categories.Add($roleStatus.Category)

        $roleViews[$role].StatusText.Text = if ([string]::IsNullOrWhiteSpace($roleStatus.Detail)) {
            $roleStatus.Label
        }
        else {
            "$($roleStatus.Label): $($roleStatus.Detail)"
        }

        $roleViews[$role].StatusText.Foreground = switch ($roleStatus.Category) {
            'Passed' { [System.Windows.Media.BrushConverter]::new().ConvertFromString('#059669') }
            'Failed' { [System.Windows.Media.BrushConverter]::new().ConvertFromString('#DC2626') }
            'Error' { [System.Windows.Media.BrushConverter]::new().ConvertFromString('#D97706') }
            'Running' { [System.Windows.Media.BrushConverter]::new().ConvertFromString('#2563EB') }
            default { [System.Windows.Media.Brushes]::DimGray }
        }

        $readyLabel = if ($readyArtifact.Exists) { 'yes' } else { 'no' }
        $resultLabel = if ($resultArtifact.Exists) { 'yes' } else { 'no' }
        $roleViews[$role].ArtifactText.Text = "Ready: $readyLabel | Result: $resultLabel"

        $evidence = Format-ArtifactEvidence `
            -Role $role `
            -ReadyArtifact $readyArtifact `
            -ResultArtifact $resultArtifact
        if ($roleViews[$role].EvidenceText.Text -ne $evidence) {
            $roleViews[$role].EvidenceText.Text = $evidence
            $roleViews[$role].EvidenceText.ScrollToHome()
        }

        $wasAtEnd = $roleViews[$role].LogText.VerticalOffset -ge
            ($roleViews[$role].LogText.ExtentHeight - $roleViews[$role].LogText.ViewportHeight - 1)
        $logContent = Read-TailText -Path $logPath -LineCount $TailLines
        if ($roleViews[$role].LogText.Text -ne $logContent) {
            $roleViews[$role].LogText.Text = $logContent
            if ($wasAtEnd -or $roleViews[$role].LogText.VerticalOffset -eq 0) {
                $roleViews[$role].LogText.ScrollToEnd()
            }
        }
    }

    if ($categories.Contains('Failed')) {
        $overallText.Text = 'Overall: FAILED'
        $overallText.Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#DC2626')
    }
    elseif ($categories.Contains('Error')) {
        $overallText.Text = 'Overall: ARTIFACT ERROR'
        $overallText.Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#D97706')
    }
    elseif (@($categories | Where-Object { $_ -eq 'Passed' }).Count -eq $roles.Count) {
        $overallText.Text = 'Overall: PASSED'
        $overallText.Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#059669')
    }
    else {
        $overallText.Text = 'Overall: IN PROGRESS'
        $overallText.Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#2563EB')
    }

    $window.Title = "Caffeinated Network Testing Viewer - $([System.IO.Path]::GetFileName($viewerState.RunPath)) - $($overallText.Text.Replace('Overall: ', ''))"
}

$timer = [System.Windows.Threading.DispatcherTimer]::new()
$timer.Interval = [TimeSpan]::FromMilliseconds($RefreshMilliseconds)
$timer.Add_Tick($refreshAction)
$refreshButton.Add_Click($refreshAction)
$closeButton.Add_Click({ $window.Close() })
$window.Add_Closed({ $timer.Stop() })

& $refreshAction
$timer.Start()
$window.ShowDialog() | Out-Null
