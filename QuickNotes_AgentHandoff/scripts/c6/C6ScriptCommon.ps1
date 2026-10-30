# Shared guard and result helpers for the C6 fault drivers.
# Every resource must live under %TEMP%\QuickNotes-C6\<runId>; this file never
# touches the product data directory, HKCU, the tray, or global hotkeys.

$ErrorActionPreference = 'Stop'

function Get-C6Root {
    return [System.IO.Path]::GetFullPath((Join-Path $env:TEMP 'QuickNotes-C6'))
}

function Assert-C6RunId {
    param([string]$RunId)
    if ([string]::IsNullOrWhiteSpace($RunId)) {
        throw 'RunId is required.'
    }

    if ($RunId -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$') {
        throw "RunId must match ^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$ (no dots or path separators)."
    }
}

function Get-C6RunRoot {
    param([string]$RunId)
    Assert-C6RunId -RunId $RunId
    return [System.IO.Path]::GetFullPath((Join-Path (Get-C6Root) $RunId))
}

function Test-C6PathUnder {
    param([string]$Path, [string]$Root)
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $prefix = $rootFull + [System.IO.Path]::DirectorySeparatorChar
    return $fullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-C6PathUnder {
    param([string]$Path, [string]$Root, [string]$Description)
    if (-not (Test-C6PathUnder -Path $Path -Root $Root)) {
        throw "$Description must live under '$Root'. Received '$Path'."
    }
}

function Get-C6ObjectProperty {
    param($Object, [string]$Name, $Default = $null)
    if ($null -eq $Object) {
        return $Default
    }

    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.Contains($Name)) {
            return $Object[$Name]
        }

        return $Default
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $Default
    }

    return $property.Value
}

function Get-C6Median {
    param([double[]]$Values)

    if ($null -eq $Values -or $Values.Count -eq 0) {
        return $null
    }

    $ordered = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($ordered.Count / 2)
    if (($ordered.Count % 2) -eq 1) {
        return [double]$ordered[$middle]
    }

    return (([double]$ordered[$middle - 1] + [double]$ordered[$middle]) / 2.0)
}

# Reads the JSONL samples written by QuickNotes.C6.UiProbe. A missing or
# malformed file is a hard failure, never an empty pass.
function Read-C6SoakSamples {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'The soak sample path is required.'
    }

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "The soak sample file '$Path' is missing."
    }

    $samples = New-Object System.Collections.Generic.List[object]
    foreach ($line in @(Get-Content -LiteralPath $Path)) {
        $trimmed = ([string]$line).Trim()
        if ($trimmed.Length -eq 0) {
            continue
        }

        try {
            $samples.Add(($trimmed | ConvertFrom-Json))
        }
        catch {
            throw "The soak sample file '$Path' contains an invalid JSON line."
        }
    }

    return $samples.ToArray()
}

<#
.SYNOPSIS
Evaluates soak sample cadence, window stability, responsiveness, and growth.

.DESCRIPTION
Structural rules apply to every soak and fail closed:
- expected samples = floor(duration / interval) + 1 (121 for 7,200 s at 60 s);
- a 2-hour soak (duration >= 7,200 s) must use a 60 s cadence and at least
  120 samples; a coarser cadence is a structural failure, not a shorter pass;
- minimum accepted samples = max(2, expected - 1) for smoke runs and at least
  120 for a 2-hour run, so a single sample can never pass;
- maximum gap between samples and before the first sample is 150% of the
  interval (90 s for a 60 s cadence);
- the final sample must be no more than one cadence tolerance before the
  requested duration;
- every sample must report the expected window count, a responding process,
  no unexpected (dialog/modal) windows, and a completed edit/save cycle.

Growth is evaluated only when -EvaluateGrowth is set (the 2-hour gate):
- runs with at least 10 samples compare the median of the first 5 samples with
  the median of the last 5 samples;
- shorter runs use the first vs last sample for diagnostics only;
- the 15% working-set and 50-handle thresholds are NOT confirmed gates. Values
  are returned for diagnostics; the caller must mark them diagnostic.
#>
function Measure-C6SoakEvidence {
    param(
        [object[]]$Samples,
        [int]$DurationSeconds,
        [int]$SampleSeconds,
        [int]$ExpectedWindowCount,
        [bool]$EvaluateGrowth = $false
    )

    if ($DurationSeconds -lt 1) {
        throw 'DurationSeconds must be at least 1.'
    }

    if ($SampleSeconds -lt 1) {
        throw 'SampleSeconds must be at least 1.'
    }

    $isTwoHourAcceptance = $DurationSeconds -ge 7200
    $items = @($Samples)
    $elapsedValues = New-Object System.Collections.Generic.List[double]
    $workingSetValues = New-Object System.Collections.Generic.List[double]
    $handleValues = New-Object System.Collections.Generic.List[double]
    $editSaveFailureCount = 0
    foreach ($item in $items) {
        $elapsed = Get-C6ObjectProperty $item 'elapsedSeconds' $null
        $workingSet = Get-C6ObjectProperty $item 'workingSetBytes' $null
        $handles = Get-C6ObjectProperty $item 'handleCount' $null
        if ($null -eq $elapsed -or $null -eq $workingSet -or $null -eq $handles) {
            throw 'A soak sample is missing elapsedSeconds, workingSetBytes, or handleCount.'
        }

        $elapsedValues.Add([double]$elapsed)
        $workingSetValues.Add([double]$workingSet)
        $handleValues.Add([double]$handles)

        $editSaveSucceeded = Get-C6ObjectProperty $item 'editSaveSucceeded' $null
        if ($null -eq $editSaveSucceeded -or -not [bool]$editSaveSucceeded) {
            $editSaveFailureCount++
        }
    }

    $expectedSampleCount = [int][Math]::Floor($DurationSeconds / $SampleSeconds) + 1
    $minimumSampleCount = if ($isTwoHourAcceptance) {
        [Math]::Max(120, $expectedSampleCount - 1)
    }
    else {
        [Math]::Max(2, $expectedSampleCount - 1)
    }
    $cadenceLimitSeconds = if ($isTwoHourAcceptance) { 90.0 } else { [double]$SampleSeconds * 1.5 }
    $firstElapsedSeconds = if ($elapsedValues.Count -eq 0) { $null } else { $elapsedValues[0] }
    $lastElapsedSeconds = if ($elapsedValues.Count -eq 0) { $null } else { $elapsedValues[$elapsedValues.Count - 1] }

    $maxGapSeconds = 0.0
    if ($elapsedValues.Count -gt 0) {
        $maxGapSeconds = $elapsedValues[0]
        for ($index = 1; $index -lt $elapsedValues.Count; $index++) {
            $gap = $elapsedValues[$index] - $elapsedValues[$index - 1]
            if ($gap -gt $maxGapSeconds) {
                $maxGapSeconds = $gap
            }
        }
    }

    $windowInstabilityCount = 0
    $processUnresponsiveCount = 0
    $unexpectedWindowCount = 0
    foreach ($item in $items) {
        $windowCount = Get-C6ObjectProperty $item 'windowCount' $null
        if ($null -eq $windowCount -or [int]$windowCount -ne $ExpectedWindowCount) {
            $windowInstabilityCount++
        }

        $responding = Get-C6ObjectProperty $item 'processResponding' $null
        if ($null -eq $responding -or -not [bool]$responding) {
            $processUnresponsiveCount++
        }

        $unexpected = Get-C6ObjectProperty $item 'unexpectedWindowCount' $null
        if ($null -eq $unexpected -or [int]$unexpected -ne 0) {
            $unexpectedWindowCount++
        }
    }

    $structuralFailures = New-Object System.Collections.Generic.List[string]
    if ($isTwoHourAcceptance -and $SampleSeconds -ne 60) {
        $structuralFailures.Add(
            "The 2-hour soak requires a 60 s sample cadence; received $SampleSeconds s.")
    }

    if ($items.Count -lt $minimumSampleCount) {
        $structuralFailures.Add(
            "Only $($items.Count) soak samples were recorded; at least $minimumSampleCount are required.")
    }

    if ($elapsedValues.Count -gt 1) {
        for ($index = 1; $index -lt $elapsedValues.Count; $index++) {
            if ($elapsedValues[$index] -lt $elapsedValues[$index - 1]) {
                $structuralFailures.Add('Soak sample timestamps are not monotonic.')
                break
            }
        }
    }

    if ($maxGapSeconds -gt $cadenceLimitSeconds) {
        $structuralFailures.Add(
            "The largest soak sample gap was $([Math]::Round($maxGapSeconds, 3)) s; the limit is $cadenceLimitSeconds s.")
    }

    if ($items.Count -gt 0 -and $firstElapsedSeconds -gt $cadenceLimitSeconds) {
        $structuralFailures.Add(
            "The first soak sample arrived at $([Math]::Round([double]$firstElapsedSeconds, 3)) s; the limit is $cadenceLimitSeconds s.")
    }

    if ($items.Count -gt 0) {
        $finalGapSeconds = [double]$DurationSeconds - [double]$lastElapsedSeconds
        if ($finalGapSeconds -gt $cadenceLimitSeconds) {
            $structuralFailures.Add(
                "The last soak sample was $([Math]::Round($finalGapSeconds, 3)) s before the requested end; the limit is $cadenceLimitSeconds s.")
        }
    }

    if ($windowInstabilityCount -gt 0) {
        $structuralFailures.Add(
            "$windowInstabilityCount soak sample(s) did not have exactly $ExpectedWindowCount windows.")
    }

    if ($processUnresponsiveCount -gt 0) {
        $structuralFailures.Add(
            "$processUnresponsiveCount soak sample(s) reported an unresponsive process.")
    }

    if ($unexpectedWindowCount -gt 0) {
        $structuralFailures.Add(
            "$unexpectedWindowCount soak sample(s) reported an unexpected dialog or window.")
    }

    if ($editSaveFailureCount -gt 0) {
        $structuralFailures.Add(
            "$editSaveFailureCount soak sample(s) did not complete an edit/save cycle.")
    }

    $growthEvaluated = $false
    $growthMethod = 'not-evaluated'
    $baselineWorkingSetBytes = $null
    $finalWorkingSetBytes = $null
    $baselineHandleCount = $null
    $finalHandleCount = $null
    $workingSetGrowthPercent = $null
    $handleGrowth = $null
    if ($EvaluateGrowth -and $items.Count -ge 2) {
        $growthEvaluated = $true
        if ($items.Count -ge 10) {
            $growthMethod = 'median-first-5-vs-median-last-5'
            $firstWorkingSetWindow = @(for ($index = 0; $index -lt 5; $index++) { $workingSetValues[$index] })
            $lastWorkingSetWindow = @(for ($index = $items.Count - 5; $index -lt $items.Count; $index++) { $workingSetValues[$index] })
            $firstHandleWindow = @(for ($index = 0; $index -lt 5; $index++) { $handleValues[$index] })
            $lastHandleWindow = @(for ($index = $items.Count - 5; $index -lt $items.Count; $index++) { $handleValues[$index] })
            $baselineWorkingSetBytes = Get-C6Median -Values $firstWorkingSetWindow
            $finalWorkingSetBytes = Get-C6Median -Values $lastWorkingSetWindow
            $baselineHandleCount = Get-C6Median -Values $firstHandleWindow
            $finalHandleCount = Get-C6Median -Values $lastHandleWindow
        }
        else {
            $growthMethod = 'first-vs-last-short-run'
            $baselineWorkingSetBytes = $workingSetValues[0]
            $finalWorkingSetBytes = $workingSetValues[$items.Count - 1]
            $baselineHandleCount = $handleValues[0]
            $finalHandleCount = $handleValues[$items.Count - 1]
        }

        if ($baselineWorkingSetBytes -le 0) {
            $structuralFailures.Add('The soak working-set baseline was not positive.')
        }
        else {
            $workingSetGrowthPercent = (
                (([double]$finalWorkingSetBytes - [double]$baselineWorkingSetBytes) /
                    [double]$baselineWorkingSetBytes) * 100.0)
        }

        $handleGrowth = [double]$finalHandleCount - [double]$baselineHandleCount
    }

    return [pscustomobject]@{
        SampleCount = $items.Count
        ExpectedSampleCount = $expectedSampleCount
        MinimumSampleCount = $minimumSampleCount
        SampleCountShortfall = [Math]::Max(0, $minimumSampleCount - $items.Count)
        CadenceLimitSeconds = $cadenceLimitSeconds
        MaxGapSeconds = $maxGapSeconds
        FirstElapsedSeconds = $firstElapsedSeconds
        LastElapsedSeconds = $lastElapsedSeconds
        FinalGapSeconds = if ($items.Count -eq 0) { $null } else { [double]$DurationSeconds - [double]$lastElapsedSeconds }
        WindowInstabilityCount = $windowInstabilityCount
        ProcessUnresponsiveCount = $processUnresponsiveCount
        UnexpectedWindowCount = $unexpectedWindowCount
        EditSaveFailureCount = $editSaveFailureCount
        GrowthEvaluated = $growthEvaluated
        GrowthMethod = $growthMethod
        BaselineWorkingSetBytes = $baselineWorkingSetBytes
        FinalWorkingSetBytes = $finalWorkingSetBytes
        WorkingSetGrowthPercent = $workingSetGrowthPercent
        BaselineHandleCount = $baselineHandleCount
        FinalHandleCount = $finalHandleCount
        HandleGrowth = $handleGrowth
        StructuralPassed = ($structuralFailures.Count -eq 0)
        StructuralFailures = @($structuralFailures)
    }
}

# Builds the machine-readable 2-hour acceptance command. The command always
# uses a fresh RunId and a sibling EvidenceRoot derived from the current
# evidence root, so executing it can never overwrite the 3-minute smoke run.
# It also sets the exact C6_BUDGETS_CONFIRMED=1 gate before invoking pwsh.
function New-C6TwoHourSoakCommand {
    param(
        [string]$ScriptRoot,
        [string]$RunId,
        [int]$Notes = 1000,
        [int]$Visible = 20,
        [int]$StartupIterations = 10,
        [int]$UiLaunches = 10,
        [int]$ShowHideCycles = 30,
        [int]$SoakSampleSeconds = 60,
        [string]$EvidenceRoot,
        [string]$Timestamp
    )

    Assert-C6RunId -RunId $RunId
    if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
        throw 'EvidenceRoot is required to build the 2-hour soak command.'
    }

    if ($SoakSampleSeconds -ne 60) {
        throw 'The 2-hour soak command requires a 60 s sample cadence.'
    }

    $stamp = if ([string]::IsNullOrWhiteSpace($Timestamp)) {
        (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmssfff')
    }
    else {
        $Timestamp
    }

    $suffix = '-2h-' + $stamp
    $maximumBaseLength = 64 - $suffix.Length
    if ($maximumBaseLength -lt 1) {
        throw 'The 2-hour RunId suffix is too long.'
    }

    $baseRunId = if ($RunId.Length -gt $maximumBaseLength) {
        $RunId.Substring(0, $maximumBaseLength)
    }
    else {
        $RunId
    }

    $freshRunId = $baseRunId + $suffix
    Assert-C6RunId -RunId $freshRunId
    $fullEvidenceRoot = [System.IO.Path]::GetFullPath($EvidenceRoot)
    $freshEvidenceRoot = Join-Path ([System.IO.Path]::GetDirectoryName($fullEvidenceRoot)) $freshRunId
    $scriptPath = Join-Path ([System.IO.Path]::GetFullPath($ScriptRoot)) 'Invoke-C6Performance.ps1'
    if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
        throw "The performance script was not found at '$scriptPath'."
    }

    $parts = @(
        ('pwsh -NoProfile -File ' + (ConvertTo-C6QuotedArgument -Value $scriptPath)),
        ('-RunId ' + $freshRunId),
        ('-Notes ' + $Notes),
        ('-Visible ' + $Visible),
        ('-StartupIterations ' + $StartupIterations),
        ('-UiLaunches ' + $UiLaunches),
        ('-ShowHideCycles ' + $ShowHideCycles),
        ('-DurationMinutes 120'),
        ('-SoakSampleSeconds ' + $SoakSampleSeconds),
        ('-EvidenceRoot ' + (ConvertTo-C6QuotedArgument -Value $freshEvidenceRoot))
    )
    $envPrefix = '$env:C6_BUDGETS_CONFIRMED=''1''; '
    $requiredSampleCount = 121
    $minimumSampleCount = 120
    $cadenceLimitSeconds = 90.0

    return [pscustomobject]@{
        runId = $freshRunId
        evidenceRoot = $freshEvidenceRoot
        command = ($envPrefix + ($parts -join ' '))
        shell = 'pwsh'
        requiredMinutes = 120
        sampleSeconds = 60
        requiredSampleCount = $requiredSampleCount
        minimumSampleCount = $minimumSampleCount
        cadenceLimitSeconds = $cadenceLimitSeconds
        growthThresholdsConfirmed = $false
        growthThresholdRole = 'diagnostic'
        maximumWorkingSetGrowthPercent = 15
        maximumHandleGrowth = 50
    }
}

function Test-C6IsReparsePoint {
    param([string]$Path)
    $attributes = [System.IO.File]::GetAttributes($Path)
    return (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)
}

function Get-C6ReparsePoints {
    param([string]$Root)
    $fullRoot = [System.IO.Path]::GetFullPath($Root)
    if (Test-C6IsReparsePoint -Path $fullRoot) {
        return @($fullRoot)
    }

    $found = New-Object System.Collections.Generic.List[string]
    $stack = New-Object System.Collections.Stack
    $stack.Push($fullRoot)
    while ($stack.Count -gt 0) {
        $directory = [string]$stack.Pop()
        foreach ($entry in [System.IO.Directory]::EnumerateFileSystemEntries($directory)) {
            $attributes = [System.IO.File]::GetAttributes($entry)
            if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                $found.Add($entry)
                continue
            }

            if (($attributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
                $stack.Push($entry)
            }
        }
    }

    return $found.ToArray()
}

function Remove-C6MountPathSafely {
    param([string]$MountPath)

    if (-not (Test-Path -LiteralPath $MountPath)) {
        return
    }

    if (Test-C6IsReparsePoint -Path $MountPath) {
        # A volume mount point / junction is removed as a link only; the
        # target volume is never traversed by a recursive delete.
        [System.IO.Directory]::Delete($MountPath, $false)
        return
    }

    $reparsePoints = @(Get-C6ReparsePoints -Root $MountPath)
    if ($reparsePoints.Count -gt 0) {
        throw ("Refusing to recursively delete '$MountPath' because it contains reparse points: " +
            ($reparsePoints -join ', '))
    }

    Remove-Item -LiteralPath $MountPath -Recurse -Force
}

function Test-C6MountIdentity {
    param(
        $ResolvedVolume,
        $ResolvedPartition,
        [int]$ExpectedDiskNumber,
        [int]$ExpectedPartitionNumber,
        [string]$ExpectedVolumeId,
        [string]$ExpectedLabel,
        [string]$MountPath,
        [string]$RunRoot,
        [string]$SystemRoot
    )

    $failures = New-Object System.Collections.Generic.List[string]
    $observed = [ordered]@{
        mountPath = $MountPath
        volumeLabel = $null
        volumePath = $null
        driveLetter = $null
        volumeUniqueId = $null
        diskNumber = $null
        partitionNumber = $null
        partitionGuid = $null
    }

    $mountFull = [System.IO.Path]::GetFullPath($MountPath)
    $runRootFull = [System.IO.Path]::GetFullPath($RunRoot)
    if (-not (Test-C6PathUnder -Path $mountFull -Root $runRootFull)) {
        $failures.Add("The mount path '$mountFull' is not under the C6 run root '$runRootFull'.")
    }

    $systemDriveLetter = $null
    if ([string]::IsNullOrWhiteSpace($SystemRoot)) {
        $failures.Add('The system root could not be determined; the mount cannot be proven non-system.')
    }
    else {
        $systemRootFull = [System.IO.Path]::GetFullPath($SystemRoot).TrimEnd('\')
        $systemDriveLetter = $systemRootFull.Substring(0, 1)
        if ($mountFull -ieq $systemRootFull -or
            $mountFull.StartsWith($systemRootFull + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
            $failures.Add("The mount path '$mountFull' is on the system volume ($systemRootFull).")
        }
    }

    if ($null -eq $ResolvedVolume) {
        $failures.Add('Get-Volume did not resolve the mount path to a volume.')
    }
    else {
        $observed.volumeLabel = [string]$ResolvedVolume.FileSystemLabel
        $observed.volumePath = [string]$ResolvedVolume.Path
        $observed.volumeUniqueId = [string]$ResolvedVolume.UniqueId
        if ($null -ne $ResolvedVolume.DriveLetter) {
            $observed.driveLetter = ([string]$ResolvedVolume.DriveLetter).TrimEnd(':')
        }

        if ([string]::IsNullOrWhiteSpace($ExpectedLabel) -or
            $observed.volumeLabel -ine $ExpectedLabel) {
            $failures.Add("The resolved volume label '$($observed.volumeLabel)' does not match the expected C6 label '$ExpectedLabel'.")
        }

        if (-not [string]::IsNullOrWhiteSpace($observed.driveLetter) -and
            $observed.driveLetter -ieq $systemDriveLetter) {
            $failures.Add('The resolved volume is mounted on the system drive letter.')
        }
    }

    if ($null -eq $ResolvedPartition) {
        $failures.Add('Get-Partition did not resolve the mount path to a partition.')
    }
    else {
        $observed.diskNumber = [int]$ResolvedPartition.DiskNumber
        $observed.partitionNumber = [int]$ResolvedPartition.PartitionNumber
        $observed.partitionGuid = [string]$ResolvedPartition.Guid
        if ($observed.diskNumber -ne $ExpectedDiskNumber) {
            $failures.Add("The resolved disk number $($observed.diskNumber) does not match the created disk $ExpectedDiskNumber.")
        }

        if ($observed.partitionNumber -ne $ExpectedPartitionNumber) {
            $failures.Add("The resolved partition number $($observed.partitionNumber) does not match the created partition $ExpectedPartitionNumber.")
        }

        if ([string]::IsNullOrWhiteSpace($ExpectedVolumeId)) {
            $failures.Add('The created volume has no recorded identity to compare against.')
        }
        elseif ($observed.partitionGuid -ine $ExpectedVolumeId -and
            $observed.volumeUniqueId -ine $ExpectedVolumeId) {
            $failures.Add('The resolved partition/volume identity does not match the just-created C6 volume.')
        }
    }

    return [pscustomobject]@{
        Passed = ($failures.Count -eq 0)
        Failures = @($failures)
        Observed = [pscustomobject]$observed
    }
}

function Get-C6MountVerification {
    param(
        [string]$MountPath,
        [string]$RunRoot,
        [string]$SystemRoot,
        [int]$ExpectedDiskNumber,
        [int]$ExpectedPartitionNumber,
        [string]$ExpectedVolumeId,
        [string]$ExpectedLabel
    )

    $resolvedVolume = $null
    $resolvedPartition = $null
    try {
        $volumeCommand = Get-Command Get-Volume -ErrorAction SilentlyContinue
        if ($null -eq $volumeCommand) {
            throw 'Get-Volume is unavailable; the C6 mount cannot be positively verified.'
        }

        $resolvedVolume = Get-Volume -FilePath $MountPath -ErrorAction Stop
        if ($null -eq $resolvedVolume) {
            throw 'Get-Volume -FilePath returned no volume for the C6 mount path.'
        }

        $partitions = @(Get-Partition -Volume $resolvedVolume -ErrorAction Stop)
        if ($partitions.Count -ne 1) {
            throw "Get-Partition returned $($partitions.Count) partitions for the C6 mount path; exactly one is required."
        }

        $resolvedPartition = $partitions[0]
    }
    catch {
        return [pscustomobject]@{
            Passed = $false
            Failures = @($_.Exception.Message)
            Observed = $null
        }
    }

    return Test-C6MountIdentity -ResolvedVolume $resolvedVolume -ResolvedPartition $resolvedPartition `
        -ExpectedDiskNumber $ExpectedDiskNumber -ExpectedPartitionNumber $ExpectedPartitionNumber `
        -ExpectedVolumeId $ExpectedVolumeId -ExpectedLabel $ExpectedLabel `
        -MountPath $MountPath -RunRoot $RunRoot -SystemRoot $SystemRoot
}

function Test-C6BackupFaultSignature {
    param($FaultedJson)

    $backup = Get-C6ObjectProperty -Object $FaultedJson -Name 'backup' -Default $null
    $observedStatus = [string](Get-C6ObjectProperty -Object $backup -Name 'status' -Default '')
    $observedErrorCode = [string](Get-C6ObjectProperty -Object $backup -Name 'errorCode' -Default '')
    return [pscustomobject]@{
        Passed = ($observedStatus -eq 'StorageUnavailable' -and $observedErrorCode -eq 'DestinationUnavailable')
        ExpectedStatus = 'StorageUnavailable'
        ExpectedErrorCode = 'DestinationUnavailable'
        ObservedStatus = $observedStatus
        ObservedErrorCode = $observedErrorCode
    }
}

function Get-C6EvidenceRoot {
    param(
        [string]$HandoffRoot,
        [string]$RunId,
        [string]$EvidenceRoot,
        [string]$EvidenceTaskName = 'task-2-evidence'
    )

    $runRoot = Get-C6RunRoot -RunId $RunId
    if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
        $repoRoot = [System.IO.Path]::GetFullPath((Join-Path $HandoffRoot '..'))
        $EvidenceRoot = Join-Path $repoRoot (
            '.superpowers\sdd\2026-10-30-c6-fault-performance-stability\' +
            $EvidenceTaskName + '\' + $RunId)
    }

    $full = [System.IO.Path]::GetFullPath($EvidenceRoot)
    if (Test-C6PathUnder -Path $full -Root $runRoot) {
        throw "EvidenceRoot must be outside the disposable C6 run root. Received '$full'."
    }

    [System.IO.Directory]::CreateDirectory($full) | Out-Null
    return $full
}

function Get-C6HarnessDll {
    param([string]$HandoffRoot)

    $project = Join-Path $HandoffRoot 'tests\QuickNotes.C6.Harness\QuickNotes.C6.Harness.csproj'
    $dll = Join-Path $HandoffRoot 'tests\QuickNotes.C6.Harness\bin\Debug\net10.0\QuickNotes.C6.Harness.dll'
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
        & dotnet build $project --nologo -v:q | Out-Null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $dll -PathType Leaf)) {
            throw 'QuickNotes.C6.Harness could not be built.'
        }
    }

    return [System.IO.Path]::GetFullPath($dll)
}

function ConvertTo-C6QuotedArgument {
    param([string]$Value)

    if ($null -eq $Value) {
        return '""'
    }

    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    return '"' + ($Value -replace '"', '\"') + '"'
}

function Start-C6HarnessProcess {
    param(
        [string]$HandoffRoot,
        [string[]]$Arguments,
        [string]$StdoutPath,
        [string]$StderrPath
    )

    $dll = Get-C6HarnessDll -HandoffRoot $HandoffRoot
    $parts = New-Object System.Collections.Generic.List[string]
    $parts.Add((ConvertTo-C6QuotedArgument -Value $dll))
    foreach ($argument in @($Arguments)) {
        $parts.Add((ConvertTo-C6QuotedArgument -Value ([string]$argument)))
    }

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = 'dotnet'
    $startInfo.Arguments = ($parts -join ' ')
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.WorkingDirectory = [System.IO.Path]::GetDirectoryName($dll)

    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw 'The C6 harness process could not be started.'
    }

    return [pscustomobject]@{
        Process = $process
        ProcessId = $process.Id
        CommandLine = ($startInfo.FileName + ' ' + $startInfo.Arguments)
        StdoutPath = [System.IO.Path]::GetFullPath($StdoutPath)
        StderrPath = [System.IO.Path]::GetFullPath($StderrPath)
    }
}

function Complete-C6HarnessProcess {
    param(
        $Started,
        [int]$ExitTimeoutMilliseconds = 10000
    )

    if ($null -eq $Started -or $null -eq $Started.Process) {
        return [pscustomobject]@{ ExitCode = $null; Stdout = ''; Stderr = '' }
    }

    $process = $Started.Process
    if (-not $process.WaitForExit($ExitTimeoutMilliseconds)) {
        throw 'The C6 harness process did not exit after the process tree was stopped.'
    }

    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    foreach ($pair in @(
        @{ Path = $Started.StdoutPath; Text = $stdout },
        @{ Path = $Started.StderrPath; Text = $stderr })) {
        $directory = [System.IO.Path]::GetDirectoryName($pair.Path)
        if (-not [string]::IsNullOrEmpty($directory)) {
            [System.IO.Directory]::CreateDirectory($directory) | Out-Null
        }

        $encoding = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText($pair.Path, [string]$pair.Text, $encoding)
    }

    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        Stdout = $stdout
        Stderr = $stderr
    }
}

function Wait-C6Marker {
    param(
        [string]$Path,
        [int]$TimeoutSeconds = 60
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            return $true
        }

        Start-Sleep -Milliseconds 50
    }

    return $false
}

function Get-C6ProcessCommandLine {
    param([int]$ProcessId)

    if ($ProcessId -le 4) {
        return $null
    }

    try {
        $process = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction Stop
        if ($null -ne $process) {
            return [string]$process.CommandLine
        }
    }
    catch {
    }

    return $null
}

function Test-C6HarnessProcessIdentity {
    param(
        [string]$CommandLine,
        [string]$RunId
    )

    if ([string]::IsNullOrWhiteSpace($CommandLine) -or [string]::IsNullOrWhiteSpace($RunId)) {
        return $false
    }

    $trimmed = $CommandLine.Trim()
    $directExe = [regex]::IsMatch(
        $trimmed,
        '^(?:"[^"]*QuickNotes\.C6\.Harness\.exe"|\S*QuickNotes\.C6\.Harness\.exe)(?:\s|$)',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $dotnetDll = [regex]::IsMatch(
        $trimmed,
        '^(?:"[^"]*dotnet(?:\.exe)?"|\S*dotnet(?:\.exe)?)\s+(?:"[^"]*QuickNotes\.C6\.Harness\.dll"|\S*QuickNotes\.C6\.Harness\.dll)(?:\s|$)',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $directExe -and -not $dotnetDll) {
        return $false
    }

    $runIdPattern = '--run-id\s+"?' + [regex]::Escape($RunId) + '"?(\s|$)'
    return [regex]::IsMatch($trimmed, $runIdPattern)
}

function Get-C6ProcessTreeIds {
    param([int]$ProcessId)

    if ($ProcessId -le 4) {
        throw "Refusing to build a process tree for PID $ProcessId."
    }

    $all = @(Get-CimInstance -ClassName Win32_Process -ErrorAction Stop |
        Select-Object ProcessId, ParentProcessId)
    $result = New-Object System.Collections.Generic.List[int]
    $queue = New-Object System.Collections.Generic.Queue[int]
    $queue.Enqueue($ProcessId)
    while ($queue.Count -gt 0) {
        $current = $queue.Dequeue()
        if ($result -contains $current) {
            continue
        }

        $result.Add($current)
        foreach ($child in @($all | Where-Object { [int]$_.ParentProcessId -eq $current })) {
            $queue.Enqueue([int]$child.ProcessId)
        }
    }

    return $result.ToArray()
}

function Stop-C6ProcessTree {
    param([int]$ProcessId)

    if ($ProcessId -le 4) {
        throw "Refusing to stop PID $ProcessId."
    }

    $ids = @(Get-C6ProcessTreeIds -ProcessId $ProcessId)
    for ($index = $ids.Count - 1; $index -ge 0; $index--) {
        Stop-Process -Id $ids[$index] -Force -ErrorAction SilentlyContinue
    }
}

function Test-C6RestartEvidence {
    param($Restart)

    $failures = New-Object System.Collections.Generic.List[string]
    if ($null -eq $Restart) {
        return [pscustomobject]@{
            Passed = $false
            Failures = @('The restart probe produced no JSON result.')
        }
    }

    $required = @(
        'startupMode', 'writesBlocked', 'databaseReadable', 'databaseExists',
        'noteCount', 'totalNoteCount', 'draftCountBeforeStartup',
        'draftCountAfterStartup', 'draftCount', 'recoveryArtifacts', 'backupPackages'
    )
    foreach ($name in $required) {
        if ($null -eq $Restart.PSObject.Properties[$name]) {
            $failures.Add("The restart evidence is missing '$name'.")
        }
    }

    if ($failures.Count -eq 0) {
        $startupMode = [string]$Restart.startupMode
        $allowedModes = @('Ready', 'NeedsRecovery', 'WriteBlocked', 'FirstRunInitialized', 'NoData', 'ReadOnlyDiagnostic')
        if ($allowedModes -notcontains $startupMode) {
            $failures.Add("Unexpected startupMode '$startupMode'.")
        }

        if ($Restart.writesBlocked -isnot [bool]) {
            $failures.Add('writesBlocked must be a boolean.')
        }

        if ($Restart.databaseReadable -ne $true) {
            $failures.Add('The restart probe could not read the database.')
        }

        if ($Restart.databaseExists -ne $true) {
            $failures.Add('The database file is missing after the kill.')
        }

        if ([int64]$Restart.noteCount -lt 1) {
            $failures.Add('The database reported no active notes after the kill.')
        }

        if ([int64]$Restart.totalNoteCount -lt 1) {
            $failures.Add('The database reported no notes at all after the kill.')
        }

        if ([int64]$Restart.draftCount -lt 0) {
            $failures.Add('The draft count is negative.')
        }

        if ([int64]$Restart.draftCountAfterStartup -lt 0) {
            $failures.Add('The post-startup draft count is negative.')
        }

        if ([int64]$Restart.draftCountAfterStartup -lt [int64]$Restart.draftCountBeforeStartup) {
            $failures.Add('Drafts disappeared during the restart probe.')
        }

        if ($Restart.recoveryArtifacts -isnot [System.Array]) {
            $failures.Add('recoveryArtifacts must be an array.')
        }

        if ($Restart.backupPackages -isnot [System.Array]) {
            $failures.Add('backupPackages must be an array.')
        }

        $json = $Restart | ConvertTo-Json -Depth 12 -Compress
        if ($json -match '(?i)"(body|content|title)"\s*:') {
            $failures.Add('The restart evidence contains note body/content/title fields.')
        }
    }

    return [pscustomobject]@{
        Passed = ($failures.Count -eq 0)
        Failures = @($failures)
    }
}

function Get-C6FileHashMap {
    param([string]$Directory)

    $map = [ordered]@{}
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        return $map
    }

    foreach ($file in @(Get-ChildItem -LiteralPath $Directory -Filter 'quicknotes-*.zip' -File)) {
        $map[$file.Name] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }

    return $map
}

function Get-C6PackageSummary {
    param([string]$Directory)

    $packages = @()
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        return $packages
    }

    foreach ($file in @(Get-ChildItem -LiteralPath $Directory -Filter 'quicknotes-*.zip' -File)) {
        $packages += [ordered]@{
            fileName = $file.Name
            sizeBytes = [int64]$file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }

    return $packages
}

function Write-C6JsonFile {
    param([string]$Path, $Object)
    $directory = [System.IO.Path]::GetDirectoryName($Path)
    if (-not [string]::IsNullOrEmpty($directory)) {
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    }

    $json = $Object | ConvertTo-Json -Depth 12
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $json, $encoding)
}

function Read-C6JsonFile {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }

    return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json)
}

function Test-C6Elevated {
    try {
        $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
        return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    catch {
        return $false
    }
}

function Get-C6Environment {
    $caption = [System.Environment]::OSVersion.VersionString
    $version = [System.Environment]::OSVersion.Version.ToString()
    try {
        $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
        if ($null -ne $os) {
            $caption = $os.Caption
            $version = $os.Version
        }
    }
    catch {
    }

    $newVhd = Get-Command New-VHD -ErrorAction SilentlyContinue
    return [ordered]@{
        os = $caption
        osVersion = $version
        architecture = $env:PROCESSOR_ARCHITECTURE
        powershell = $PSVersionTable.PSVersion.ToString()
        elevated = Test-C6Elevated
        newVhdAvailable = ($null -ne $newVhd)
        timestampUtc = (Get-Date).ToUniversalTime().ToString('O')
    }
}

function Get-C6HandoffRoot {
    param([string]$ScriptRoot)
    $candidate = [System.IO.Path]::GetFullPath((Join-Path $ScriptRoot '..\..'))
    if (-not (Test-Path -LiteralPath (Join-Path $candidate 'QuickNotes.DataPrototype.sln') -PathType Leaf)) {
        throw "Could not locate QuickNotes_AgentHandoff from '$ScriptRoot'."
    }

    return $candidate
}

function Get-C6LastJsonLine {
    param($Lines)
    $items = @($Lines)
    for ($index = $items.Count - 1; $index -ge 0; $index--) {
        $line = ([string]$items[$index]).Trim()
        if ($line.StartsWith('{')) {
            return $line
        }
    }

    return $null
}

function Invoke-C6Harness {
    param(
        [string]$HandoffRoot,
        [string[]]$Arguments
    )

    $project = Join-Path $HandoffRoot 'tests\QuickNotes.C6.Harness\QuickNotes.C6.Harness.csproj'
    $dll = Join-Path $HandoffRoot 'tests\QuickNotes.C6.Harness\bin\Debug\net10.0\QuickNotes.C6.Harness.dll'
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
        & dotnet build $project --nologo -v:q | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw 'QuickNotes.C6.Harness could not be built.'
        }
    }

    $raw = @(& dotnet $dll @Arguments 2>&1 | ForEach-Object { $_.ToString() })
    $exitCode = $LASTEXITCODE
    $jsonLine = $null
    for ($index = $raw.Count - 1; $index -ge 0; $index--) {
        $line = $raw[$index].Trim()
        if ($line.StartsWith('{')) {
            $jsonLine = $line
            break
        }
    }

    if ($null -eq $jsonLine) {
        throw "The C6 harness did not emit a JSON result (exit $exitCode). Output: $($raw -join ' | ')"
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Json = ($jsonLine | ConvertFrom-Json)
        RawOutput = ($raw -join [Environment]::NewLine)
    }
}

function Test-C6WriteAccess {
    param([string]$Directory)
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        return $false
    }

    $probe = Join-Path $Directory ('.c6-write-probe-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [System.IO.File]::WriteAllText($probe, '')
        [System.IO.File]::Delete($probe)
        return $true
    }
    catch {
        try {
            if (Test-Path -LiteralPath $probe -PathType Leaf) {
                [System.IO.File]::Delete($probe)
            }
        }
        catch {
        }

        return $false
    }
}

function Get-C6CurrentUser {
    return [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
}

function Get-C6DenyAceAudit {
    param($Acl, [string]$User)

    $currentSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $denyAce = $null
    foreach ($ace in @($Acl.Access)) {
        if ($ace.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Deny) {
            continue
        }

        try {
            $aceSid = $ace.IdentityReference.Translate(
                [System.Security.Principal.SecurityIdentifier]).Value
        }
        catch {
            $aceSid = $null
        }

        if ($aceSid -eq $currentSid) {
            $denyAce = $ace
            break
        }
    }

    $mask = $null
    $maskHex = $null
    $rights = $null
    if ($null -ne $denyAce) {
        $mask = [int]$denyAce.FileSystemRights
        $maskHex = ('0x{0:X}' -f $mask)
        $rights = [string]$denyAce.FileSystemRights
    }

    return [pscustomobject]@{
        User = $User
        DenyAcePresent = ($null -ne $denyAce)
        Mask = $mask
        MaskHex = $maskHex
        Rights = $rights
        HasWriteData = ($null -ne $mask -and (($mask -band 0x2) -ne 0))
        HasAppendData = ($null -ne $mask -and (($mask -band 0x4) -ne 0))
    }
}

function Set-C6AclDenyWrite {
    param(
        [string]$Target,
        [string]$RunRoot,
        [string]$Case
    )

    Assert-C6PathUnder -Path $Target -Root $RunRoot -Description 'ACL target'
    if (-not (Test-Path -LiteralPath $Target -PathType Container)) {
        [System.IO.Directory]::CreateDirectory($Target) | Out-Null
    }

    $acl = Get-Acl -LiteralPath $Target
    $user = Get-C6CurrentUser
    $state = [ordered]@{
        case = $Case
        target = $Target
        sddl = $acl.Sddl
        user = $user
        createdByC6 = $true
        appliedAtUtc = (Get-Date).ToUniversalTime().ToString('O')
        appliedSddl = $null
        denyAcePresent = $false
        denyMaskHex = $null
        denyRights = $null
    }
    $backupPath = Join-Path $RunRoot ("acl-{0}.json" -f $Case)

    # The original SDDL is persisted before any ACL change so a crash can
    # always restore it from disk.
    Write-C6JsonFile -Path $backupPath -Object $state
    $stateObject = [pscustomobject]@{
        Target = $Target
        Sddl = $state.sddl
        User = $user
        BackupPath = $backupPath
        AppliedSddl = $null
        DenyAcePresent = $false
        DenyMaskHex = $null
        DenyRights = $null
    }

    # Deny file-creation and directory-creation only. Denying all write access
    # on a directory also blocks some directory handle opens on Windows, which
    # would make the failure unobservable instead of testing a write failure.
    & icacls.exe $Target /deny "$($user):(OI)(CI)(WD,AD)" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        $restore = Restore-C6Acl -State $stateObject
        if ($restore.Status -ne 'PASS') {
            throw "icacls failed and the ACL could not be restored: $($restore.Message) $($restore.Repair)"
        }

        throw 'icacls could not deny writes on the C6 ACL target.'
    }

    if (Test-C6WriteAccess -Directory $Target) {
        $restore = Restore-C6Acl -State $stateObject
        if ($restore.Status -ne 'PASS') {
            throw "The ACL deny did not take effect and the original ACL could not be restored: $($restore.Message) $($restore.Repair)"
        }

        throw 'The ACL deny did not take effect (an elevated process can bypass it); no fault was injected.'
    }

    $appliedAcl = Get-Acl -LiteralPath $Target
    $audit = Get-C6DenyAceAudit -Acl $appliedAcl -User $user
    if (-not $audit.DenyAcePresent -or -not $audit.HasWriteData -or -not $audit.HasAppendData) {
        $restore = Restore-C6Acl -State $stateObject
        if ($restore.Status -ne 'PASS') {
            throw "The deny ACE audit failed and the original ACL could not be restored: $($restore.Message) $($restore.Repair)"
        }

        throw ("The deny ACE audit failed: present=$($audit.DenyAcePresent), mask=$($audit.MaskHex), rights=$($audit.Rights).")
    }

    $state.appliedSddl = $appliedAcl.Sddl
    $state.denyAcePresent = $audit.DenyAcePresent
    $state.denyMaskHex = $audit.MaskHex
    $state.denyRights = $audit.Rights
    Write-C6JsonFile -Path $backupPath -Object $state

    $stateObject.AppliedSddl = $appliedAcl.Sddl
    $stateObject.DenyAcePresent = $audit.DenyAcePresent
    $stateObject.DenyMaskHex = $audit.MaskHex
    $stateObject.DenyRights = $audit.Rights
    return $stateObject
}

function Restore-C6Acl {
    param($State)

    if ($null -eq $State) {
        return [pscustomobject]@{
            Status = 'PASS'
            Message = 'No ACL change was applied.'
            OriginalSddl = $null
            RestoredSddl = $null
            SddlMatchesOriginal = $true
            WriteProbePassed = $true
            Repair = $null
        }
    }

    if (-not (Test-Path -LiteralPath $State.Target -PathType Container)) {
        if (Test-Path -LiteralPath $State.BackupPath -PathType Leaf) {
            Remove-Item -LiteralPath $State.BackupPath -Force
        }

        return [pscustomobject]@{
            Status = 'PASS'
            Message = 'ACL target no longer exists.'
            OriginalSddl = $State.Sddl
            RestoredSddl = $null
            SddlMatchesOriginal = $true
            WriteProbePassed = $true
            Repair = $null
        }
    }

    $restoredSddl = $null
    try {
        $acl = Get-Acl -LiteralPath $State.Target
        $acl.SetSecurityDescriptorSddlForm($State.Sddl)
        Set-Acl -LiteralPath $State.Target -AclObject $acl
        $restoredSddl = (Get-Acl -LiteralPath $State.Target).Sddl
    }
    catch {
        return [pscustomobject]@{
            Status = 'FAIL'
            Message = "ACL restoration failed: $($_.Exception.Message)"
            OriginalSddl = $State.Sddl
            RestoredSddl = $restoredSddl
            SddlMatchesOriginal = $false
            WriteProbePassed = $false
            Repair = "Run: icacls `"$($State.Target)`" /remove:d `"$($State.User)`" ; if that fails, Set-Acl from SDDL $($State.Sddl)"
        }
    }

    $sddlMatches = [string]::Equals(
        [string]$restoredSddl,
        [string]$State.Sddl,
        [System.StringComparison]::OrdinalIgnoreCase)
    $writeProbePassed = Test-C6WriteAccess -Directory $State.Target
    if (-not $sddlMatches) {
        return [pscustomobject]@{
            Status = 'FAIL'
            Message = 'The ACL was rewritten but the restored SDDL does not match the original.'
            OriginalSddl = $State.Sddl
            RestoredSddl = $restoredSddl
            SddlMatchesOriginal = $false
            WriteProbePassed = $writeProbePassed
            Repair = "Run: icacls `"$($State.Target)`" /remove:d `"$($State.User)`" ; if that fails, Set-Acl from SDDL $($State.Sddl)"
        }
    }

    if (-not $writeProbePassed) {
        return [pscustomobject]@{
            Status = 'FAIL'
            Message = 'The ACL was rewritten but the write probe still fails.'
            OriginalSddl = $State.Sddl
            RestoredSddl = $restoredSddl
            SddlMatchesOriginal = $true
            WriteProbePassed = $false
            Repair = "Run: icacls `"$($State.Target)`" /remove:d `"$($State.User)`" ; if that fails, Set-Acl from SDDL $($State.Sddl)"
        }
    }

    if (Test-Path -LiteralPath $State.BackupPath -PathType Leaf) {
        Remove-Item -LiteralPath $State.BackupPath -Force
    }

    return [pscustomobject]@{
        Status = 'PASS'
        Message = 'The original SDDL was restored and verified.'
        OriginalSddl = $State.Sddl
        RestoredSddl = $restoredSddl
        SddlMatchesOriginal = $true
        WriteProbePassed = $true
        Repair = $null
    }
}

function Write-C6CaseResult {
    param(
        [string]$RunId,
        [string]$Case,
        [string]$Status,
        [string]$Scenario,
        [string]$Expected,
        $Actual,
        $Restart,
        [string[]]$Evidence,
        $Cleanup,
        [string]$Reason,
        [string]$EvidenceRoot
    )

    $runRoot = Get-C6RunRoot -RunId $RunId
    $resultsDirectory = Join-Path $runRoot 'results'
    [System.IO.Directory]::CreateDirectory($resultsDirectory) | Out-Null
    $retainedEvidenceRoot = $null
    if (-not [string]::IsNullOrWhiteSpace($EvidenceRoot)) {
        $retainedEvidenceRoot = [System.IO.Path]::GetFullPath($EvidenceRoot)
        if (Test-C6PathUnder -Path $retainedEvidenceRoot -Root $runRoot) {
            throw "EvidenceRoot must be outside the disposable C6 run root. Received '$retainedEvidenceRoot'."
        }

        [System.IO.Directory]::CreateDirectory($retainedEvidenceRoot) | Out-Null
    }

    $retainedEvidence = @()
    foreach ($item in @($Evidence)) {
        if ([string]::IsNullOrWhiteSpace($item)) {
            continue
        }

        if ([System.IO.Path]::IsPathRooted($item)) {
            $full = [System.IO.Path]::GetFullPath($item)
        }
        else {
            $full = [System.IO.Path]::GetFullPath((Join-Path $runRoot $item))
        }

        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            throw "Evidence file does not exist: '$full'."
        }

        if ($null -ne $retainedEvidenceRoot) {
            Assert-C6PathUnder -Path $full -Root $retainedEvidenceRoot -Description 'Retained evidence path'
        }
        else {
            Assert-C6PathUnder -Path $full -Root $runRoot -Description 'Evidence path'
        }

        $retainedEvidence += $full
    }

    $archivePath = $null
    $result = [ordered]@{
        case = $Case
        status = $Status
        scenario = $Scenario
        runId = $RunId
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString('O')
        environment = Get-C6Environment
        expected = $Expected
        actual = $Actual
        restart = $Restart
        evidence = @($retainedEvidence)
        cleanup = $Cleanup
    }
    if ($null -ne $retainedEvidenceRoot) {
        $archivePath = Join-Path $retainedEvidenceRoot ("results\{0}.result.json" -f $Case)
        $result.evidenceRoot = $retainedEvidenceRoot
        $result.resultArchivePath = $archivePath
    }

    if (-not [string]::IsNullOrWhiteSpace($Reason)) {
        $result.reason = $Reason
    }

    $resultPath = Join-Path $resultsDirectory ("{0}.result.json" -f $Case)
    Write-C6JsonFile -Path $resultPath -Object $result
    if ($null -ne $archivePath) {
        [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($archivePath)) | Out-Null
        Copy-Item -LiteralPath $resultPath -Destination $archivePath -Force
        if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
            throw "The retained result archive was not written: '$archivePath'."
        }
    }

    $summary = [ordered]@{
        case = $Case
        status = $Status
        resultPath = if ($null -ne $archivePath) { $archivePath } else { ("results/{0}.result.json" -f $Case) }
    }
    [Console]::Out.WriteLine(($summary | ConvertTo-Json -Compress))
    return $resultPath
}

function Save-C6HarnessEvidence {
    param(
        [string]$RunId,
        [string]$Name,
        $HarnessResult,
        [string]$EvidenceRoot
    )

    $runRoot = Get-C6RunRoot -RunId $RunId
    if (-not [string]::IsNullOrWhiteSpace($EvidenceRoot)) {
        $fullRoot = [System.IO.Path]::GetFullPath($EvidenceRoot)
        if (Test-C6PathUnder -Path $fullRoot -Root $runRoot) {
            throw "EvidenceRoot must be outside the disposable C6 run root. Received '$fullRoot'."
        }

        $path = Join-Path $fullRoot ("raw\{0}.json" -f $Name)
    }
    else {
        $path = Join-Path $runRoot ("evidence\{0}.json" -f $Name)
    }

    $evidence = [ordered]@{
        exitCode = $HarnessResult.ExitCode
        result = $HarnessResult.Json
    }
    Write-C6JsonFile -Path $path -Object $evidence
    return [System.IO.Path]::GetFullPath($path)
}
