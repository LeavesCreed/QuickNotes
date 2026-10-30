#requires -Version 5.1
<#
.SYNOPSIS
Runs the C6 ACL fault cases against disposable directories under
%TEMP%\QuickNotes-C6\<RunId>.

Every deny is recorded as SDDL before it is applied and restored in a finally
block. When restoration fails the script keeps the run root, prints repair
instructions, and returns FAIL instead of deleting evidence.

Cases:
  DraftDeny   C6-F02  draft directory denies writes; retry after restore.
  BackupDeny  C6-F03  backup destination denies writes; existing backup stays.
  RestorePreflightDeny  C6-F06  reserved for Task 3's restore preflight body.
#>
param(
    [string]$RunId,
    [ValidateSet('DraftDeny', 'BackupDeny', 'RestorePreflightDeny', 'All')]
    [string]$Case = 'All',
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

function Invoke-C6SaveHarness {
    param([string]$DraftDirectory)
    return Invoke-C6Harness -HandoffRoot $handoffRoot -Arguments @(
        'save',
        '--data-root', $dataRoot,
        '--draft-dir', $DraftDirectory,
        '--checkpoint-dir', $checkpointDirectory,
        '--run-id', $RunId,
        '--notes', [string]$Notes,
        '--visible', '1'
    )
}

function Invoke-C6BackupHarness {
    param([string]$BackupDirectory)
    return Invoke-C6Harness -HandoffRoot $handoffRoot -Arguments @(
        'backup',
        '--data-root', $dataRoot,
        '--backup-dir', $BackupDirectory,
        '--checkpoint-dir', $checkpointDirectory,
        '--run-id', $RunId,
        '--notes', [string]$Notes,
        '--visible', '1'
    )
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

function Invoke-C6DraftDenyCase {
    $target = Join-Path $runRoot 'acl-root\drafts'
    [System.IO.Directory]::CreateDirectory($target) | Out-Null
    $state = $null
    $restore = $null
    $first = $null
    $second = $null
    $firstEvidence = $null
    $secondEvidence = $null
    $errorMessage = $null

    try {
        $state = Set-C6AclDenyWrite -Target $target -RunRoot $runRoot -Case 'draft'
        $first = Invoke-C6SaveHarness -DraftDirectory $target
        $firstEvidence = Save-C6HarnessEvidence -RunId $RunId -Name 'C6-F02-first' -HarnessResult $first
    }
    catch {
        $errorMessage = $_.Exception.Message
    }
    finally {
        if ($null -ne $state) {
            $restore = Restore-C6Acl -State $state
        }
    }

    $aclRestored = ($null -ne $restore -and $restore.Status -eq 'PASS')
    if ($null -eq $errorMessage -and $aclRestored) {
        try {
            $second = Invoke-C6SaveHarness -DraftDirectory $target
            $secondEvidence = Save-C6HarnessEvidence -RunId $RunId -Name 'C6-F02-retry' -HarnessResult $second
        }
        catch {
            $errorMessage = $_.Exception.Message
        }
    }

    $firstJson = if ($null -ne $first) { $first.Json } else { $null }
    $secondJson = if ($null -ne $second) { $second.Json } else { $null }
    $draftFailureVisible = (
        (Get-C6HarnessField $firstJson 'status' '') -eq 'draft_failure' -and
        (Get-C6HarnessField $firstJson 'failureStage' '') -eq 'draft' -and
        (Get-C6HarnessField $firstJson 'draftSaved' $true) -eq $false -and
        (Get-C6HarnessField $firstJson 'noteSaved' $true) -eq $false -and
        (Get-C6HarnessField $firstJson 'databaseReadable' $false) -eq $true
    )
    $noFalseSuccess = (
        (Get-C6HarnessField $firstJson 'savedReported' $false) -ne $true -and
        (Get-C6HarnessField $firstJson 'saveStatus' '') -ne 'Saved'
    )
    $retrySucceeded = (
        $null -ne $second -and $second.ExitCode -eq 0 -and
        (Get-C6HarnessField $secondJson 'status' '') -eq 'ok' -and
        (Get-C6HarnessField $secondJson 'saveStatus' '') -eq 'Saved' -and
        (Get-C6HarnessField $secondJson 'noteSaved' $false) -eq $true -and
        (Get-C6HarnessField $secondJson 'draftRetained' $true) -eq $false
    )
    $restart = Get-C6HarnessField $secondJson 'restart' $null
    $restartPassed = ($null -ne $restart -and (Get-C6HarnessField $restart 'passed' $false) -eq $true)
    $targetWritable = Test-C6WriteAccess -Directory $target
    $passed = (
        $draftFailureVisible -and $noFalseSuccess -and $retrySucceeded -and
        $aclRestored -and $targetWritable -and $restartPassed
    )

    $actual = [ordered]@{
        injection = 'acl-deny-write'
        target = 'acl-root/drafts'
        firstRun = [ordered]@{
            exitCode = if ($null -ne $first) { $first.ExitCode } else { $null }
            status = Get-C6HarnessField $firstJson 'status' $null
            failureStage = Get-C6HarnessField $firstJson 'failureStage' $null
            draftSaved = Get-C6HarnessField $firstJson 'draftSaved' $null
            noteSaved = Get-C6HarnessField $firstJson 'noteSaved' $null
        }
        secondRun = [ordered]@{
            exitCode = if ($null -ne $second) { $second.ExitCode } else { $null }
            status = Get-C6HarnessField $secondJson 'status' $null
            saveStatus = Get-C6HarnessField $secondJson 'saveStatus' $null
            noteSaved = Get-C6HarnessField $secondJson 'noteSaved' $null
            draftRetained = Get-C6HarnessField $secondJson 'draftRetained' $null
        }
        draftFailureVisible = $draftFailureVisible
        noFalseSuccess = $noFalseSuccess
        retrySucceeded = $retrySucceeded
        aclWritableAfterRestore = $targetWritable
        error = $errorMessage
    }
    $cleanup = [ordered]@{
        aclRestored = $aclRestored
        message = if ($null -ne $restore) { $restore.Message } else { 'No ACL restoration was recorded.' }
        repair = if ($null -ne $restore) { $restore.Repair } else { $null }
    }
    $restartEvidence = if ($null -ne $restart) {
        $restart
    }
    else {
        [ordered]@{ method = 'reopen-services'; passed = $false; databaseReadable = $false; notePresent = $false; failure = 'The retry did not run.' }
    }
    $evidence = @($firstEvidence, $secondEvidence) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    $status = if ($passed) { 'PASS' } else { 'FAIL' }
    $reason = if ($passed) { $null } elseif (-not $aclRestored) { 'The ACL was not restored; repair it before cleanup.' } else { $errorMessage }
    Write-C6CaseResult -RunId $RunId -Case 'C6-F02' -Status $status -Scenario 'save' `
        -Expected 'A denied draft directory produces a visible draft failure and a retry succeeds after the ACL is restored; the database remains readable.' `
        -Actual $actual -Restart $restartEvidence -Evidence $evidence -Cleanup $cleanup -Reason $reason | Out-Null
    return $status
}

function Invoke-C6BackupDenyCase {
    $target = Join-Path $runRoot 'acl-root\backups'
    [System.IO.Directory]::CreateDirectory($target) | Out-Null
    $state = $null
    $restore = $null
    $baseline = $null
    $faulted = $null
    $retry = $null
    $baselineEvidence = $null
    $faultEvidence = $null
    $retryEvidence = $null
    $errorMessage = $null
    $baselineHashes = @{}

    try {
        $baseline = Invoke-C6BackupHarness -BackupDirectory $target
        $baselineEvidence = Save-C6HarnessEvidence -RunId $RunId -Name 'C6-F03-baseline' -HarnessResult $baseline
        foreach ($file in Get-ChildItem -LiteralPath $target -Filter 'quicknotes-*.zip' -File) {
            $baselineHashes[$file.Name] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }

        $state = Set-C6AclDenyWrite -Target $target -RunRoot $runRoot -Case 'backup'
        $faulted = Invoke-C6BackupHarness -BackupDirectory $target
        $faultEvidence = Save-C6HarnessEvidence -RunId $RunId -Name 'C6-F03-fault' -HarnessResult $faulted
    }
    catch {
        $errorMessage = $_.Exception.Message
    }
    finally {
        if ($null -ne $state) {
            $restore = Restore-C6Acl -State $state
        }
    }

    $aclRestored = ($null -ne $restore -and $restore.Status -eq 'PASS')
    if ($null -eq $errorMessage -and $aclRestored) {
        try {
            $retry = Invoke-C6BackupHarness -BackupDirectory $target
            $retryEvidence = Save-C6HarnessEvidence -RunId $RunId -Name 'C6-F03-retry' -HarnessResult $retry
        }
        catch {
            $errorMessage = $_.Exception.Message
        }
    }

    $baselineJson = if ($null -ne $baseline) { $baseline.Json } else { $null }
    $faultJson = if ($null -ne $faulted) { $faulted.Json } else { $null }
    $retryJson = if ($null -ne $retry) { $retry.Json } else { $null }
    $baselineBackup = Get-C6HarnessField $baselineJson 'backup' $null
    $faultBackup = Get-C6HarnessField $faultJson 'backup' $null
    $retryBackup = Get-C6HarnessField $retryJson 'backup' $null

    $baselineSucceeded = (
        $null -ne $baseline -and $baseline.ExitCode -eq 0 -and
        (Get-C6HarnessField $baselineJson 'status' '') -eq 'ok' -and
        (Get-C6HarnessField $baselineBackup 'newPackagePublished' $false) -eq $true
    )
    $noFalseSuccess = (
        (Get-C6HarnessField $faultJson 'status' '') -ne 'ok' -and
        (Get-C6HarnessField $faultBackup 'status' '') -ne 'Succeeded' -and
        (Get-C6HarnessField $faulted 'ExitCode' 0) -ne 0
    )
    $noNewPackage = (
        (Get-C6HarnessField $faultBackup 'newPackagePublished' $true) -eq $false -and
        (Get-C6HarnessField $faultBackup 'temporaryResidueCount' 1) -eq 0
    )
    $currentHashes = @{}
    foreach ($file in Get-ChildItem -LiteralPath $target -Filter 'quicknotes-*.zip' -File) {
        $currentHashes[$file.Name] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }

    $existingBackupIntact = $true
    foreach ($name in $baselineHashes.Keys) {
        if (-not $currentHashes.ContainsKey($name) -or $currentHashes[$name] -ne $baselineHashes[$name]) {
            $existingBackupIntact = $false
        }
    }

    $retrySucceeded = (
        $null -ne $retry -and $retry.ExitCode -eq 0 -and
        (Get-C6HarnessField $retryJson 'status' '') -eq 'ok' -and
        (Get-C6HarnessField $retryBackup 'newPackagePublished' $false) -eq $true
    )
    $restart = Get-C6HarnessField $faultJson 'restart' $null
    $restartPassed = ($null -ne $restart -and (Get-C6HarnessField $restart 'passed' $false) -eq $true)
    $targetWritable = Test-C6WriteAccess -Directory $target
    $passed = (
        $baselineSucceeded -and $noFalseSuccess -and $noNewPackage -and
        $existingBackupIntact -and $retrySucceeded -and $aclRestored -and
        $targetWritable -and $restartPassed
    )

    $actual = [ordered]@{
        injection = 'acl-deny-write'
        target = 'acl-root/backups'
        baseline = [ordered]@{
            exitCode = if ($null -ne $baseline) { $baseline.ExitCode } else { $null }
            packageCount = if ($null -ne $baselineBackup) { Get-C6HarnessField $baselineBackup 'packageCountAfter' $null } else { $null }
        }
        faulted = [ordered]@{
            exitCode = if ($null -ne $faulted) { $faulted.ExitCode } else { $null }
            status = Get-C6HarnessField $faultJson 'status' $null
            backupStatus = Get-C6HarnessField $faultBackup 'status' $null
            errorCode = Get-C6HarnessField $faultBackup 'errorCode' $null
            packageCountAfter = Get-C6HarnessField $faultBackup 'packageCountAfter' $null
            temporaryResidueCount = Get-C6HarnessField $faultBackup 'temporaryResidueCount' $null
        }
        retry = [ordered]@{
            exitCode = if ($null -ne $retry) { $retry.ExitCode } else { $null }
            status = Get-C6HarnessField $retryJson 'status' $null
            newPackagePublished = Get-C6HarnessField $retryBackup 'newPackagePublished' $null
        }
        noFalseSuccess = $noFalseSuccess
        noNewPackage = $noNewPackage
        existingBackupIntact = $existingBackupIntact
        retrySucceeded = $retrySucceeded
        aclWritableAfterRestore = $targetWritable
        error = $errorMessage
    }
    $cleanup = [ordered]@{
        aclRestored = $aclRestored
        message = if ($null -ne $restore) { $restore.Message } else { 'No ACL restoration was recorded.' }
        repair = if ($null -ne $restore) { $restore.Repair } else { $null }
    }
    $restartEvidence = if ($null -ne $restart) {
        $restart
    }
    else {
        [ordered]@{ method = 'reopen-services'; passed = $false; databaseReadable = $false; notePresent = $false; failure = 'The fault run did not finish.' }
    }
    $evidence = @($baselineEvidence, $faultEvidence, $retryEvidence) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    $status = if ($passed) { 'PASS' } else { 'FAIL' }
    $reason = if ($passed) { $null } elseif (-not $aclRestored) { 'The ACL was not restored; repair it before cleanup.' } else { $errorMessage }
    Write-C6CaseResult -RunId $RunId -Case 'C6-F03' -Status $status -Scenario 'backup' `
        -Expected 'An unwritable backup destination reports no success, publishes no final package, and leaves the existing backup byte-identical.' `
        -Actual $actual -Restart $restartEvidence -Evidence $evidence -Cleanup $cleanup -Reason $reason | Out-Null
    return $status
}

function Invoke-C6RestorePreflightCase {
    $actual = [ordered]@{
        injection = 'none'
        note = 'The restore preflight body is implemented by Task 3; Task 2 does not create a restore scenario.'
    }
    $cleanup = [ordered]@{
        aclRestored = $true
        message = 'No ACL change was applied for this reserved case.'
    }
    $restart = [ordered]@{
        method = 'not-run'
        passed = $false
        databaseReadable = $false
        notePresent = $false
        failure = 'Restore scenario body is Task 3.'
    }
    Write-C6CaseResult -RunId $RunId -Case 'C6-F06' -Status 'NOT RUN' -Scenario 'restore' `
        -Expected 'An offline backup directory is rejected by restore preflight without switching or bypassing writes.' `
        -Actual $actual -Restart $restart -Evidence @() -Cleanup $cleanup `
        -Reason 'NOT RUN: the restore preflight scenario body is Task 3; Task 2 does not implement a restore harness.' | Out-Null
    return 'NOT RUN'
}

$cases = if ($Case -eq 'All') { @('DraftDeny', 'BackupDeny', 'RestorePreflightDeny') } else { @($Case) }
$statuses = @()
foreach ($selected in $cases) {
    try {
        switch ($selected) {
            'DraftDeny' { $statuses += Invoke-C6DraftDenyCase }
            'BackupDeny' { $statuses += Invoke-C6BackupDenyCase }
            'RestorePreflightDeny' { $statuses += Invoke-C6RestorePreflightCase }
        }
    }
    catch {
        $statuses += 'FAIL'
        Write-C6CaseResult -RunId $RunId -Case ('C6-' + $selected) -Status 'FAIL' -Scenario $selected `
            -Expected 'The case must complete without an unhandled script error.' `
            -Actual ([ordered]@{ error = $_.Exception.Message }) `
            -Restart ([ordered]@{ method = 'not-run'; passed = $false; databaseReadable = $false; notePresent = $false }) `
            -Evidence @() -Cleanup ([ordered]@{ aclRestored = $false; message = 'Unhandled script error.' }) `
            -Reason $_.Exception.Message | Out-Null
    }
}

if ($statuses -contains 'FAIL') {
    exit 1
}

exit 0
