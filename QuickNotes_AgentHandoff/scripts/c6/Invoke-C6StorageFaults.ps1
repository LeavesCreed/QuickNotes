#requires -Version 5.1
<#
.SYNOPSIS
Executes the C6 isolated storage fault matrix.

save       C6-F01 (VHD free space exhausted) + C6-F02 (ACL denied drafts)
backup     C6-F03 (ACL denied backup destination)
restore    C6-F05 (VHD staging full) + C6-F06 (offline backup preflight)
migration  C6-F04 (VHD staging full)

C6-F01/F04/F05 need an elevated session with Hyper-V New-VHD; when those
prerequisites are unavailable the case is written as NOT RUN with the exact
reason. Nothing is reported as PASS on a simulated fault alone.
#>
param(
    [string]$RunId,
    [ValidateSet('save', 'backup', 'restore', 'migration', 'all')]
    [string]$Scenario = 'all',
    [int]$SizeMB = 512,
    [int]$Notes = 1000
)

. (Join-Path $PSScriptRoot 'C6ScriptCommon.ps1')

if ($Notes -lt 1) {
    throw 'Notes must be at least 1.'
}

$handoffRoot = Get-C6HandoffRoot -ScriptRoot $PSScriptRoot
$runRoot = Get-C6RunRoot -RunId $RunId
[System.IO.Directory]::CreateDirectory($runRoot) | Out-Null
$dataRoot = Join-Path $runRoot 'data'
$checkpointDirectory = Join-Path $runRoot 'checkpoints'

if (-not ('C6DiskSpace.C6FreeSpace' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace C6DiskSpace
{
    public static class C6FreeSpace
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetDiskFreeSpaceExW(
            string directoryName,
            out ulong freeBytesAvailable,
            out ulong totalNumberOfBytes,
            out ulong totalNumberOfFreeBytes);

        public static long GetFreeBytes(string directoryName)
        {
            ulong freeBytes;
            ulong totalBytes;
            ulong totalFreeBytes;
            if (!GetDiskFreeSpaceExW(directoryName, out freeBytes, out totalBytes, out totalFreeBytes))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            return freeBytes > long.MaxValue ? long.MaxValue : (long)freeBytes;
        }
    }
}
'@
}

function Get-C6HarnessField {
    param($Object, [string]$Name, $Default = $null)
    if ($null -eq $Object) {
        return $Default
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $Default
    }

    return $property.Value
}

function Invoke-C6ChildJsonScript {
    param([string]$ScriptPath, [hashtable]$Parameters)
    $raw = @(& $ScriptPath @Parameters 2>&1 | ForEach-Object { $_.ToString() })
    $exitCode = $LASTEXITCODE
    $jsonLine = Get-C6LastJsonLine -Lines $raw
    $json = if ($null -ne $jsonLine) { $jsonLine | ConvertFrom-Json } else { $null }
    return [pscustomobject]@{
        ExitCode = $exitCode
        Json = $json
        RawOutput = ($raw -join [Environment]::NewLine)
    }
}

function New-C6StorageVolumeIfPossible {
    $script = Join-Path $PSScriptRoot 'New-C6TestVolume.ps1'
    $validation = Invoke-C6ChildJsonScript -ScriptPath $script -Parameters @{
        RunId = $RunId
        SizeMB = $SizeMB
        ValidateOnly = $true
    }
    $validationStatus = Get-C6HarnessField $validation.Json 'status' 'NOT RUN'
    if ($validationStatus -ne 'READY') {
        return [pscustomobject]@{
            Created = $false
            Reason = [string](Get-C6HarnessField $validation.Json 'reason' 'VHD prerequisites are unavailable.')
            Validation = $validation
            Creation = $null
        }
    }

    $creation = Invoke-C6ChildJsonScript -ScriptPath $script -Parameters @{
        RunId = $RunId
        SizeMB = $SizeMB
    }
    if ($creation.ExitCode -ne 0 -or (Get-C6HarnessField $creation.Json 'status' '') -ne 'PASS') {
        return [pscustomobject]@{
            Created = $false
            Reason = [string](Get-C6HarnessField $creation.Json 'reason' 'The C6 VHD could not be created.')
            Validation = $validation
            Creation = $creation
        }
    }

    return [pscustomobject]@{
        Created = $true
        Reason = $null
        Validation = $validation
        Creation = $creation
    }
}

function Get-C6VolumePrerequisiteReason {
    $script = Join-Path $PSScriptRoot 'New-C6TestVolume.ps1'
    $validation = Invoke-C6ChildJsonScript -ScriptPath $script -Parameters @{
        RunId = $RunId
        SizeMB = $SizeMB
        ValidateOnly = $true
    }
    $status = Get-C6HarnessField $validation.Json 'status' 'NOT RUN'
    if ($status -eq 'READY') {
        return 'VHD creation prerequisites are available.'
    }

    return [string](Get-C6HarnessField $validation.Json 'reason' 'VHD prerequisites are unavailable.')
}

function Remove-C6StorageVolume {
    $script = Join-Path $PSScriptRoot 'Remove-C6TestVolume.ps1'
    $removal = Invoke-C6ChildJsonScript -ScriptPath $script -Parameters @{ RunId = $RunId }
    $passed = ($removal.ExitCode -eq 0 -and (Get-C6HarnessField $removal.Json 'status' '') -eq 'PASS')
    return [pscustomobject]@{
        Passed = $passed
        Json = $removal.Json
        Reason = [string](Get-C6HarnessField $removal.Json 'reason' 'Volume removal failed.')
    }
}

function Write-C6StorageNotRun {
    param(
        [string]$Case,
        [string]$Scenario,
        [string]$Expected,
        [string]$Reason,
        $ActualExtra
    )
    $actual = [ordered]@{
        injection = 'not-run'
        note = 'No destructive action was taken for this case.'
    }
    if ($null -ne $ActualExtra) {
        foreach ($key in $ActualExtra.Keys) {
            $actual[$key] = $ActualExtra[$key]
        }
    }

    Write-C6CaseResult -RunId $RunId -Case $Case -Status 'NOT RUN' -Scenario $Scenario `
        -Expected $Expected -Actual $actual `
        -Restart ([ordered]@{ method = 'not-run'; passed = $false; databaseReadable = $false; notePresent = $false; failure = $Reason }) `
        -Evidence @() `
        -Cleanup ([ordered]@{ status = 'PASS'; message = 'No C6 resources were created for this case.' }) `
        -Reason $Reason | Out-Null
}

function Invoke-C6F01 {
    $volume = New-C6StorageVolumeIfPossible
    if (-not $volume.Created) {
        Write-C6StorageNotRun -Case 'C6-F01' -Scenario 'save' `
            -Expected 'When the isolated volume is full during autosave, the save reports no success, the draft remains, and a reopen finds a readable database.' `
            -Reason ("NOT RUN: $($volume.Reason) The same save contract is covered without a VHD by FaultDecoratorTests, but that is not a substitute for this real-volume case.") `
            -ActualExtra ([ordered]@{ simulatedCoverage = 'FaultDecoratorTests.SaveScenario_DiskFull_ReportsFaultAndReopensWithDraft'; vhdPrerequisite = $volume.Reason })
        return 'NOT RUN'
    }

    $creation = $volume.Creation.Json
    $mountPath = [string](Get-C6HarnessField $creation 'mountPath' '')
    $volumeId = [string](Get-C6HarnessField $creation 'volumeId' '')
    $volumeDataRoot = Join-Path $mountPath 'data'
    $draftDirectory = Join-Path $runRoot 'drafts'
    [System.IO.Directory]::CreateDirectory($draftDirectory) | Out-Null
    $setup = $null
    $faulted = $null
    $setupEvidence = $null
    $faultEvidence = $null
    $fillDetail = $null
    $errorMessage = $null
    $removal = $null

    try {
        $setup = Invoke-C6Harness -HandoffRoot $handoffRoot -Arguments @(
            'save',
            '--data-root', $volumeDataRoot,
            '--draft-dir', $draftDirectory,
            '--checkpoint-dir', $checkpointDirectory,
            '--run-id', $RunId,
            '--notes', [string]$Notes,
            '--visible', '1'
        )
        $setupEvidence = Save-C6HarnessEvidence -RunId $RunId -Name 'C6-F01-setup' -HarnessResult $setup

        $filler = Join-Path $mountPath 'c6-filler.bin'
        $margin = 8192
        $freeBefore = [C6DiskSpace.C6FreeSpace]::GetFreeBytes($mountPath)
        $fill = [int64]$freeBefore - $margin
        if ($fill -gt 0) {
            & fsutil.exe file createnew $filler $fill | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw 'fsutil could not create the C6 filler file.'
            }
        }

        $freeAfter = [C6DiskSpace.C6FreeSpace]::GetFreeBytes($mountPath)
        $attempt = 0
        while ($freeAfter -gt 65536 -and $attempt -lt 6) {
            $chunkPath = Join-Path $mountPath ("c6-filler-{0}.bin" -f $attempt)
            $chunk = [int64]$freeAfter - $margin
            if ($chunk -le 0) {
                break
            }

            & fsutil.exe file createnew $chunkPath $chunk | Out-Null
            $freeAfter = [C6DiskSpace.C6FreeSpace]::GetFreeBytes($mountPath)
            $attempt++
        }

        $fillDetail = [ordered]@{
            freeBytesBefore = $freeBefore
            freeBytesAfter = $freeAfter
            marginBytes = $margin
        }
        if ($freeAfter -gt 65536) {
            throw 'The C6 VHD could not be filled below 64 KiB; the disk-full injection is not meaningful.'
        }

        $faulted = Invoke-C6Harness -HandoffRoot $handoffRoot -Arguments @(
            'save',
            '--data-root', $volumeDataRoot,
            '--draft-dir', $draftDirectory,
            '--checkpoint-dir', $checkpointDirectory,
            '--run-id', $RunId,
            '--notes', [string]$Notes,
            '--visible', '1'
        )
        $faultEvidence = Save-C6HarnessEvidence -RunId $RunId -Name 'C6-F01-fault' -HarnessResult $faulted
    }
    catch {
        $errorMessage = $_.Exception.Message
    }
    finally {
        $removal = Remove-C6StorageVolume
    }

    $setupJson = if ($null -ne $setup) { $setup.Json } else { $null }
    $faultJson = if ($null -ne $faulted) { $faulted.Json } else { $null }
    $setupOk = ($null -ne $setup -and $setup.ExitCode -eq 0 -and (Get-C6HarnessField $setupJson 'status' '') -eq 'ok')
    $savedReported = (Get-C6HarnessField $faultJson 'savedReported' $false) -eq $true
    $saveStatus = [string](Get-C6HarnessField $faultJson 'saveStatus' '')
    $draftRetained = (Get-C6HarnessField $faultJson 'draftRetained' $false) -eq $true
    $noteSaved = (Get-C6HarnessField $faultJson 'noteSaved' $true) -eq $true
    $restart = Get-C6HarnessField $faultJson 'restart' $null
    $restartPassed = ($null -ne $restart -and (Get-C6HarnessField $restart 'passed' $false) -eq $true)
    $cleanupPassed = $null -ne $removal -and $removal.Passed
    $faultSafe = (
        $null -ne $faulted -and -not $savedReported -and
        $saveStatus -eq 'StorageUnavailable' -and $draftRetained -and -not $noteSaved
    )
    $passed = ($setupOk -and $faultSafe -and $restartPassed -and $cleanupPassed -and [string]::IsNullOrWhiteSpace($errorMessage))

    $actual = [ordered]@{
        injection = 'vhd-free-space-exhausted'
        sizeMB = $SizeMB
        volumeCreated = $true
        fill = $fillDetail
        setupStatus = Get-C6HarnessField $setupJson 'status' $null
        faultStatus = Get-C6HarnessField $faultJson 'status' $null
        saveStatus = if ([string]::IsNullOrWhiteSpace($saveStatus)) { $null } else { $saveStatus }
        savedReported = $savedReported
        draftRetained = $draftRetained
        noteSaved = $noteSaved
        error = $errorMessage
    }
    $restartEvidence = if ($null -ne $restart) {
        $restart
    }
    else {
        [ordered]@{ method = 'reopen-services'; passed = $false; databaseReadable = $false; notePresent = $false; failure = 'The fault run did not finish.' }
    }
    $evidence = @($setupEvidence, $faultEvidence) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $cleanup = [ordered]@{
        status = if ($cleanupPassed) { 'PASS' } else { 'FAIL' }
        message = if ($null -ne $removal) { $removal.Reason } else { 'No removal result.' }
        volumeId = $volumeId
    }
    $status = if ($passed) { 'PASS' } else { 'FAIL' }
    $reason = if ($passed) { $null } elseif (-not $cleanupPassed) { 'The C6 volume cleanup failed.' } else { $errorMessage }
    Write-C6CaseResult -RunId $RunId -Case 'C6-F01' -Status $status -Scenario 'save' `
        -Expected 'When the isolated volume is full during autosave, the save reports no success, the draft remains, and a reopen finds a readable database.' `
        -Actual $actual -Restart $restartEvidence -Evidence $evidence -Cleanup $cleanup -Reason $reason | Out-Null
    return $status
}

function Read-C6CaseStatus {
    param([string]$Case)
    $path = Join-Path $runRoot ("results\{0}.result.json" -f $Case)
    $json = Read-C6JsonFile -Path $path
    if ($null -eq $json) {
        return $null
    }

    return [string]$json.status
}

function Invoke-C6AclChildCase {
    param([ValidateSet('DraftDeny', 'BackupDeny', 'RestorePreflightDeny')][string]$AclCase, [string]$ResultCase)
    $script = Join-Path $PSScriptRoot 'Invoke-C6AclFaults.ps1'
    $child = Invoke-C6ChildJsonScript -ScriptPath $script -Parameters @{
        RunId = $RunId
        Case = $AclCase
        Notes = $Notes
    }
    $status = Read-C6CaseStatus -Case $ResultCase
    if ($null -eq $status) {
        $scenarioName = if ($AclCase -eq 'BackupDeny') { 'backup' } else { 'save' }
        Write-C6StorageNotRun -Case $ResultCase -Scenario $scenarioName `
            -Expected 'The ACL case must write its result.' `
            -Reason ("NOT RUN: the ACL driver did not write a result (exit $($child.ExitCode)). " + $child.RawOutput) `
            -ActualExtra $null
        return 'NOT RUN'
    }

    return $status
}

function Invoke-C6F04 {
    $vhdReason = Get-C6VolumePrerequisiteReason

    Write-C6StorageNotRun -Case 'C6-F04' -Scenario 'migration' `
        -Expected 'A full migration staging volume leaves the original v1/current database readable and does not mark migration successful.' `
        -Reason ("NOT RUN: the migration staging scenario body is implemented by Task 3; C6-F04 cannot be executed by Task 2. VHD prerequisite: $vhdReason") `
        -ActualExtra ([ordered]@{ migrationBody = 'Task 3'; vhdPrerequisite = $vhdReason })
    return 'NOT RUN'
}

function Invoke-C6F05 {
    $vhdReason = Get-C6VolumePrerequisiteReason

    Write-C6StorageNotRun -Case 'C6-F05' -Scenario 'restore' `
        -Expected 'A full restore staging volume keeps the current/protected database and performs no switch.' `
        -Reason ("NOT RUN: the restore scenario body is implemented by Task 3; C6-F05 cannot be executed by Task 2. VHD prerequisite: $vhdReason") `
        -ActualExtra ([ordered]@{ restoreBody = 'Task 3'; vhdPrerequisite = $vhdReason })
    return 'NOT RUN'
}

function Invoke-C6F06 {
    $aclScript = Join-Path $PSScriptRoot 'Invoke-C6AclFaults.ps1'
    $child = Invoke-C6ChildJsonScript -ScriptPath $aclScript -Parameters @{
        RunId = $RunId
        Case = 'RestorePreflightDeny'
        Notes = $Notes
    }
    $status = Read-C6CaseStatus -Case 'C6-F06'
    if ($null -eq $status) {
        Write-C6StorageNotRun -Case 'C6-F06' -Scenario 'restore' `
            -Expected 'An offline backup directory is rejected by restore preflight without switching or bypassing writes.' `
            -Reason ("NOT RUN: the restore preflight body is Task 3 and the ACL driver wrote no result (exit $($child.ExitCode)).") `
            -ActualExtra $null
        return 'NOT RUN'
    }

    return $status
}

$statuses = @()
switch ($Scenario) {
    'save' {
        $statuses += Invoke-C6F01
        $statuses += Invoke-C6AclChildCase -AclCase 'DraftDeny' -ResultCase 'C6-F02'
    }
    'backup' {
        $statuses += Invoke-C6AclChildCase -AclCase 'BackupDeny' -ResultCase 'C6-F03'
    }
    'restore' {
        $statuses += Invoke-C6F05
        $statuses += Invoke-C6F06
    }
    'migration' {
        $statuses += Invoke-C6F04
    }
    'all' {
        $statuses += Invoke-C6F01
        $statuses += Invoke-C6AclChildCase -AclCase 'DraftDeny' -ResultCase 'C6-F02'
        $statuses += Invoke-C6AclChildCase -AclCase 'BackupDeny' -ResultCase 'C6-F03'
        $statuses += Invoke-C6F04
        $statuses += Invoke-C6F05
        $statuses += Invoke-C6F06
    }
}

if ($statuses -contains 'FAIL') {
    exit 1
}

exit 0
