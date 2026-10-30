#requires -Version 5.1
<#
.SYNOPSIS
Runs the C6 checkpoint process-kill matrix (C6-K01..C6-K09).

Each case starts a real QuickNotes.C6.Harness process with a unique C6 RunId,
waits for one checkpoint marker, verifies the process command line is that
harness with that exact RunId, kills only its process tree, then starts
`QuickNotes.C6.Harness restart` and evaluates the startup/count/artifact
evidence. No user process is ever enumerated for termination, no production
data root, HKCU value, tray, or hotkey is touched, and all synthetic data lives
under %TEMP%\QuickNotes-C6\<RunId>.

Exit 0 when every requested case is PASS, 1 when any case is FAIL or NOT RUN.
Results and raw evidence are retained outside the disposable run root. After
the matrix, run Clear-C6Artifacts.ps1 -RunId <id> to remove the run root; the
retained evidence under task-3-evidence is never deleted.
#>
param(
    [string]$RunId,
    [string]$Case = 'All',
    [int]$Notes = 120,
    [int]$MarkerTimeoutSeconds = 90,
    [string]$EvidenceRoot
)

. (Join-Path $PSScriptRoot 'C6ScriptCommon.ps1')

if ($Notes -lt 1) {
    throw 'Notes must be at least 1.'
}

if ($MarkerTimeoutSeconds -lt 5) {
    throw 'MarkerTimeoutSeconds must be at least 5.'
}

Assert-C6RunId -RunId $RunId
$handoffRoot = Get-C6HandoffRoot -ScriptRoot $PSScriptRoot
$runRoot = Get-C6RunRoot -RunId $RunId
$evidenceRoot = Get-C6EvidenceRoot -HandoffRoot $handoffRoot -RunId $RunId `
    -EvidenceRoot $EvidenceRoot -EvidenceTaskName 'task-3-evidence'
[System.IO.Directory]::CreateDirectory($runRoot) | Out-Null
& dotnet build (Join-Path $handoffRoot 'tests\QuickNotes.C6.Harness\QuickNotes.C6.Harness.csproj') `
    --nologo -v:q | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'QuickNotes.C6.Harness could not be built for the process-kill matrix.'
}

Get-C6HarnessDll -HandoffRoot $handoffRoot | Out-Null

$caseSpecs = @(
    [pscustomobject]@{ Case = 'C6-K01'; Scenario = 'backup'; Marker = 'backup.before_publish'; Setup = 'backup-baseline'; FaultPostSwitch = $false },
    [pscustomobject]@{ Case = 'C6-K02'; Scenario = 'backup'; Marker = 'backup.after_publish'; Setup = 'backup-baseline'; FaultPostSwitch = $false },
    [pscustomobject]@{ Case = 'C6-K03'; Scenario = 'save'; Marker = 'save.after_draft'; Setup = 'save'; FaultPostSwitch = $false },
    [pscustomobject]@{ Case = 'C6-K04'; Scenario = 'save'; Marker = 'save.before_commit'; Setup = 'save'; FaultPostSwitch = $false },
    [pscustomobject]@{ Case = 'C6-K05'; Scenario = 'restore'; Marker = 'restore.before_switch'; Setup = 'restore'; FaultPostSwitch = $false },
    [pscustomobject]@{ Case = 'C6-K06'; Scenario = 'restore'; Marker = 'restore.after_switch'; Setup = 'restore'; FaultPostSwitch = $false },
    [pscustomobject]@{ Case = 'C6-K07'; Scenario = 'restore'; Marker = 'restore.rollback'; Setup = 'restore'; FaultPostSwitch = $true },
    [pscustomobject]@{ Case = 'C6-K08'; Scenario = 'migrate'; Marker = 'migration.before'; Setup = 'migrate'; FaultPostSwitch = $false },
    [pscustomobject]@{ Case = 'C6-K09'; Scenario = 'migrate'; Marker = 'migration.after'; Setup = 'migrate'; FaultPostSwitch = $false }
)

function Get-C6KillHarnessField {
    param($Object, [string]$Name, $Default = $null)
    return Get-C6ObjectProperty -Object $Object -Name $Name -Default $Default
}

function New-C6KillCaseRoots {
    param([string]$CaseName)

    $caseRoot = Join-Path $runRoot ($CaseName.ToLowerInvariant().Replace('-', ''))
    $roots = [ordered]@{
        caseRoot = $caseRoot
        dataRoot = Join-Path $caseRoot 'data'
        sourceRoot = Join-Path $caseRoot 'source'
        backupDirectory = Join-Path $caseRoot 'backups'
        checkpointDirectory = Join-Path $caseRoot 'checkpoints'
        setupCheckpointDirectory = Join-Path $caseRoot 'setup-checkpoints'
        rawDirectory = Join-Path $evidenceRoot ('raw\' + $CaseName)
    }
    foreach ($value in $roots.Values) {
        if ($value -notlike '*.json' -and $value -notlike '*\raw\*') {
            [System.IO.Directory]::CreateDirectory($value) | Out-Null
        }
    }

    [System.IO.Directory]::CreateDirectory($roots.rawDirectory) | Out-Null
    return [pscustomobject]$roots
}

function Invoke-C6Seed {
    param($Roots, [int]$SeedNotes, [string]$DataRoot)

    $seed = Invoke-C6Harness -HandoffRoot $handoffRoot -Arguments @(
        'seed',
        '--data-root', $DataRoot,
        '--notes', [string]$SeedNotes,
        '--visible', '1',
        '--run-id', $RunId
    )
    $json = $seed.Json
    if ($seed.ExitCode -ne 0 -or [string](Get-C6KillHarnessField $json 'status' '') -ne 'ok') {
        throw "The C6 seed for '$DataRoot' failed with exit $($seed.ExitCode)."
    }

    return $json
}

function Initialize-C6KillCase {
    param($Spec, $Roots)

    $setupEvidence = @()
    $context = [ordered]@{
        packagePath = $null
        packageBefore = $null
        sourceTotal = $null
        currentTotal = $null
        targetNoteId = $null
        baseVersion = $null
    }

    switch ($Spec.Setup) {
        'save' {
            $seed = Invoke-C6Seed -Roots $Roots -SeedNotes $Notes -DataRoot $Roots.dataRoot
            $context.currentTotal = Get-C6KillHarnessField $seed 'totalNoteCount' 0
        }
        'backup-baseline' {
            $seed = Invoke-C6Seed -Roots $Roots -SeedNotes $Notes -DataRoot $Roots.dataRoot
            $context.currentTotal = Get-C6KillHarnessField $seed 'totalNoteCount' 0
            $baseline = Invoke-C6Harness -HandoffRoot $handoffRoot -Arguments @(
                'backup',
                '--data-root', $Roots.dataRoot,
                '--backup-dir', $Roots.backupDirectory,
                '--checkpoint-dir', $Roots.setupCheckpointDirectory,
                '--run-id', $RunId,
                '--notes', [string]$Notes,
                '--visible', '1'
            )
            $setupEvidence += Save-C6HarnessEvidence -RunId $RunId -Name ($Spec.Case + '-baseline') `
                -HarnessResult $baseline -EvidenceRoot $evidenceRoot
            if ($baseline.ExitCode -ne 0 -or [string](Get-C6KillHarnessField $baseline.Json 'status' '') -ne 'ok') {
                throw "The baseline backup failed with exit $($baseline.ExitCode)."
            }

            $context.packageBefore = Get-C6FileHashMap -Directory $Roots.backupDirectory
        }
        'restore' {
            $sourceSeed = Invoke-C6Seed -Roots $Roots -SeedNotes 5 -DataRoot $Roots.sourceRoot
            $context.sourceTotal = Get-C6KillHarnessField $sourceSeed 'totalNoteCount' 0
            $currentSeed = Invoke-C6Seed -Roots $Roots -SeedNotes $Notes -DataRoot $Roots.dataRoot
            $context.currentTotal = Get-C6KillHarnessField $currentSeed 'totalNoteCount' 0
            $sourceBackup = Invoke-C6Harness -HandoffRoot $handoffRoot -Arguments @(
                'backup',
                '--data-root', $Roots.sourceRoot,
                '--backup-dir', $Roots.backupDirectory,
                '--checkpoint-dir', $Roots.setupCheckpointDirectory,
                '--run-id', $RunId,
                '--notes', '5',
                '--visible', '1'
            )
            $setupEvidence += Save-C6HarnessEvidence -RunId $RunId -Name ($Spec.Case + '-source-backup') `
                -HarnessResult $sourceBackup -EvidenceRoot $evidenceRoot
            if ($sourceBackup.ExitCode -ne 0 -or [string](Get-C6KillHarnessField $sourceBackup.Json 'status' '') -ne 'ok') {
                throw "The restore source package could not be created (exit $($sourceBackup.ExitCode))."
            }

            $packageName = [string](Get-C6KillHarnessField (Get-C6KillHarnessField $sourceBackup.Json 'backup' $null) 'newPackageFileName' '')
            if ([string]::IsNullOrWhiteSpace($packageName)) {
                throw 'The restore source backup did not report a new package file name.'
            }

            $context.packagePath = Join-Path $Roots.backupDirectory $packageName
        }
        'migrate' {
            # MigrationScenario creates the real v1 source database itself.
        }
    }

    return [pscustomobject]@{
        Context = [pscustomobject]$context
        SetupEvidence = @($setupEvidence)
    }
}

function Get-C6KillRestartEvidence {
    param($Spec, $Roots)

    $arguments = @(
        'restart',
        '--data-root', $Roots.dataRoot,
        '--run-id', $RunId
    )
    if ($Spec.Scenario -in @('backup', 'restore')) {
        $arguments += @('--backup-dir', $Roots.backupDirectory)
    }

    $targetPath = Join-Path $Roots.checkpointDirectory 'save.target.json'
    $target = Read-C6JsonFile -Path $targetPath
    if ($null -ne $target) {
        $noteId = [string](Get-C6KillHarnessField $target 'noteId' '')
        if (-not [string]::IsNullOrWhiteSpace($noteId)) {
            $arguments += @('--probe-note-id', $noteId)
        }
    }

    $restart = Invoke-C6Harness -HandoffRoot $handoffRoot -Arguments $arguments
    $evidence = Save-C6HarnessEvidence -RunId $RunId -Name ($Spec.Case + '-restart') `
        -HarnessResult $restart -EvidenceRoot $evidenceRoot
    return [pscustomobject]@{
        ExitCode = $restart.ExitCode
        Json = $restart.Json
        EvidencePath = $evidence
    }
}

function Test-C6KillRestartCommon {
    param($Spec, $Restart)

    $probe = Get-C6KillHarnessField $Restart 'restartProbe' $Restart
    $failures = New-Object System.Collections.Generic.List[string]
    $evidenceCheck = Test-C6RestartEvidence -Restart $probe
    foreach ($failure in @($evidenceCheck.Failures)) {
        $failures.Add([string]$failure)
    }

    $expectedMode = 'Ready'
    if ($Spec.Scenario -eq 'restore') {
        $expectedMode = 'NeedsRecovery'
    }

    if ([string](Get-C6KillHarnessField $probe 'startupMode' '') -ne $expectedMode) {
        $failures.Add("Expected startupMode '$expectedMode' after the kill.")
    }

    if ($Spec.Case -ne 'C6-K07' -and (Get-C6KillHarnessField $probe 'writesBlocked' $false) -eq $true) {
        $failures.Add('Database writes were unexpectedly blocked after this kill.')
    }

    return $failures
}

function Test-C6KillCaseOutcome {
    param($Spec, $Roots, $Context, $Restart, $PackageBefore, $BasePackagePath)

    $probe = Get-C6KillHarnessField $Restart 'restartProbe' $Restart
    $failures = Test-C6KillRestartCommon -Spec $Spec -Restart $probe
    $total = [int64](Get-C6KillHarnessField $probe 'totalNoteCount' 0)
    $backupPackages = @(Get-C6KillHarnessField $probe 'backupPackages' @())
    $artifacts = @(Get-C6KillHarnessField $probe 'recoveryArtifacts' @())
    $artifactKinds = @($artifacts | ForEach-Object { [string]$_.kind })

    switch ($Spec.Case) {
        'C6-K01' {
            $after = Get-C6FileHashMap -Directory $Roots.backupDirectory
            if ($after.Count -ne $PackageBefore.Count) {
                $failures.Add("A final backup package appeared before publish: before=$($PackageBefore.Count), after=$($after.Count).")
            }

            foreach ($name in $PackageBefore.Keys) {
                if (-not $after.Contains($name) -or $after[$name] -ne $PackageBefore[$name]) {
                    $failures.Add("Existing backup package '$name' changed or disappeared.")
                }
            }

            if (@($backupPackages | Where-Object { $_.valid -ne $true }).Count -gt 0) {
                $failures.Add('A backup package reported by the restart probe is not valid.')
            }
        }
        'C6-K02' {
            $after = Get-C6FileHashMap -Directory $Roots.backupDirectory
            if ($after.Count -ne ($PackageBefore.Count + 1)) {
                $failures.Add("Expected exactly one new final package after publish: before=$($PackageBefore.Count), after=$($after.Count).")
            }

            foreach ($name in $PackageBefore.Keys) {
                if (-not $after.Contains($name) -or $after[$name] -ne $PackageBefore[$name]) {
                    $failures.Add("Existing backup package '$name' changed or disappeared.")
                }
            }

            if (@($backupPackages | Where-Object { $_.valid -ne $true }).Count -gt 0) {
                $failures.Add('A backup package reported by the restart probe is not valid.')
            }
        }
        'C6-K03' {
            if ($null -eq $Context.baseVersion) {
                $failures.Add('The save target metadata was not written before the kill.')
            }

            if ((Get-C6KillHarnessField $probe 'draftCount' 0) -lt 1) {
                $failures.Add('The save draft did not survive the kill.')
            }

            if ((Get-C6KillHarnessField $probe 'targetNoteExists' $false) -ne $true) {
                $failures.Add('The target note disappeared after the save kill.')
            }

            if ([int64](Get-C6KillHarnessField $probe 'targetNoteVersion' -1) -ne [int64]$Context.baseVersion) {
                $failures.Add("The target note was committed despite the draft-only kill: base=$($Context.baseVersion), observed=$(Get-C6KillHarnessField $probe 'targetNoteVersion' $null).")
            }
        }
        'C6-K04' {
            if ($null -eq $Context.baseVersion) {
                $failures.Add('The save target metadata was not written before the kill.')
            }

            if ((Get-C6KillHarnessField $probe 'draftCount' 0) -lt 1) {
                $failures.Add('The save draft did not survive the before-commit kill.')
            }

            if ((Get-C6KillHarnessField $probe 'targetNoteExists' $false) -ne $true) {
                $failures.Add('The target note disappeared after the before-commit kill.')
            }

            if ([int64](Get-C6KillHarnessField $probe 'targetNoteVersion' -1) -ne [int64]$Context.baseVersion) {
                $failures.Add("The target note reports a commit after the before-commit kill: base=$($Context.baseVersion), observed=$(Get-C6KillHarnessField $probe 'targetNoteVersion' $null).")
            }
        }
        'C6-K05' {
            if ($total -ne [int64]$Context.currentTotal) {
                $failures.Add("The current database was not preserved before the switch: expected=$($Context.currentTotal), observed=$total.")
            }

            $journalStage = [string](Get-C6KillHarnessField $probe 'restoreJournalStage' '')
            if ($journalStage -in @('Completed', 'Failed', '')) {
                $failures.Add("The restore was not marked interrupted: journalStage='$journalStage'.")
            }

            if ($backupPackages.Count -lt 2) {
                $failures.Add("Expected the source package plus a protection package: observed=$($backupPackages.Count).")
            }

            if (@($backupPackages | Where-Object { $_.valid -ne $true }).Count -gt 0) {
                $failures.Add('The restore protection package is not valid.')
            }
        }
        'C6-K06' {
            if ($total -ne [int64]$Context.sourceTotal) {
                $failures.Add("The switched database is not the package database: expected=$($Context.sourceTotal), observed=$total.")
            }

            if ([string](Get-C6KillHarnessField $probe 'restoreJournalStage' '') -ne 'Verifying') {
                $failures.Add("Expected the interrupted restore journal stage 'Verifying'; observed '$(Get-C6KillHarnessField $probe 'restoreJournalStage' '')'.")
            }

            if ($artifactKinds -notcontains 'RestoreDirectory') {
                $failures.Add('No restore staging directory was reported as a recovery artifact.')
            }

            if ($backupPackages.Count -lt 2) {
                $failures.Add("Expected the source package plus a protection package: observed=$($backupPackages.Count).")
            }

            if (@($backupPackages | Where-Object { $_.valid -ne $true }).Count -gt 0) {
                $failures.Add('A restore backup package is not valid.')
            }
        }
        'C6-K07' {
            $journalStage = [string](Get-C6KillHarnessField $probe 'restoreJournalStage' '')
            if ($journalStage -ne 'Rollback') {
                $failures.Add("Expected the interrupted restore journal stage 'Rollback'; observed '$journalStage'.")
            }

            if ($total -ne [int64]$Context.sourceTotal -and $total -ne [int64]$Context.currentTotal) {
                $failures.Add("The database is neither the pre-restore nor the package database: expected $($Context.currentTotal) or $($Context.sourceTotal), observed $total.")
            }

            if ($artifactKinds -notcontains 'RestoreDirectory') {
                $failures.Add('The restore staging directory (rollback database) was not preserved as a recovery artifact.')
            }

            if ($backupPackages.Count -lt 2) {
                $failures.Add("Expected the source package plus a protection package: observed=$($backupPackages.Count).")
            }

            if (@($backupPackages | Where-Object { $_.valid -ne $true }).Count -gt 0) {
                $failures.Add('A restore backup package is not valid.')
            }
        }
        'C6-K08' {
            if ([int64](Get-C6KillHarnessField $probe 'schemaVersionBeforeStartup' -1) -ne 1) {
                $failures.Add("Expected the killed source to remain v1; observed schema $(Get-C6KillHarnessField $probe 'schemaVersionBeforeStartup' $null).")
            }

            if ([int64](Get-C6KillHarnessField $probe 'schemaVersionAfterStartup' -1) -ne 2) {
                $failures.Add('Startup did not migrate the v1 source to the current schema.')
            }

            if ($total -ne [int64]$Notes) {
                $failures.Add("The migration lost notes: expected=$Notes, observed=$total.")
            }

            if ($artifactKinds -notcontains 'MigrationProtectionSnapshot') {
                $failures.Add('The migration protection snapshot was not preserved.')
            }
        }
        'C6-K09' {
            if ([int64](Get-C6KillHarnessField $probe 'schemaVersionBeforeStartup' -1) -ne 2) {
                $failures.Add("Expected the killed migration to have reached schema 2; observed $(Get-C6KillHarnessField $probe 'schemaVersionBeforeStartup' $null).")
            }

            if ([int64](Get-C6KillHarnessField $probe 'schemaVersionAfterStartup' -1) -ne 2) {
                $failures.Add('The restarted database is not on the current schema.')
            }

            if ($total -ne [int64]$Notes) {
                $failures.Add("The migration lost notes: expected=$Notes, observed=$total.")
            }

            if ($artifactKinds -notcontains 'MigrationProtectionSnapshot') {
                $failures.Add('The migration protection snapshot was not preserved.')
            }
        }
    }

    return [pscustomobject]@{
        Passed = ($failures.Count -eq 0)
        Failures = @($failures)
    }
}

function Invoke-C6KillCase {
    param($Spec)

    $roots = New-C6KillCaseRoots -CaseName $Spec.Case
    $expected = "Kill the harness at '$($Spec.Marker)' with RunId '$RunId', then require a readable non-empty restart with the case-specific artifacts."
    $setup = $null
    $started = $null
    $completed = $null
    $restart = $null
    $markerSeen = $false
    $identityVerified = $false
    $wasRunningAtKill = $false
    $killedTreeCount = 0
    $errorMessage = $null
    $evidence = New-Object System.Collections.Generic.List[string]
    $context = [pscustomobject]@{}

    try {
        $setup = Initialize-C6KillCase -Spec $Spec -Roots $roots
        $context = $setup.Context
        foreach ($path in @($setup.SetupEvidence)) {
            if (-not [string]::IsNullOrWhiteSpace($path)) {
                $evidence.Add([string]$path)
            }
        }

        $targetPath = Join-Path $roots.checkpointDirectory 'save.target.json'
        # The save scenario writes the target metadata itself; only after the
        # process starts. The value is read again by the restart probe.
        $arguments = @(
            $Spec.Scenario,
            '--data-root', $roots.dataRoot,
            '--checkpoint-dir', $roots.checkpointDirectory,
            '--run-id', $RunId,
            '--notes', [string]$Notes,
            '--visible', '1',
            '--kill-marker', $Spec.Marker
        )
        if ($Spec.Scenario -in @('backup', 'restore')) {
            $arguments += @('--backup-dir', $roots.backupDirectory)
        }

        if ($Spec.Scenario -eq 'restore') {
            $arguments += @('--package', [string]$context.packagePath)
            if ($Spec.FaultPostSwitch) {
                $arguments += '--fault-post-switch'
            }
        }

        $stdoutPath = Join-Path $roots.rawDirectory ($Spec.Case + '-kill.stdout.log')
        $stderrPath = Join-Path $roots.rawDirectory ($Spec.Case + '-kill.stderr.log')
        $started = Start-C6HarnessProcess -HandoffRoot $handoffRoot -Arguments $arguments `
            -StdoutPath $stdoutPath -StderrPath $stderrPath
        $markerPath = Join-Path $roots.checkpointDirectory ($Spec.Marker + '.reached')
        $markerSeen = Wait-C6Marker -Path $markerPath -TimeoutSeconds $MarkerTimeoutSeconds
        if (-not $markerSeen) {
            if (-not $started.Process.HasExited) {
                # The process was started by this script; stop it so a missed
                # marker cannot leave an orphan. It is never chosen by name.
                & taskkill.exe /PID $started.ProcessId /T /F | Out-Null
            }

            $completed = Complete-C6HarnessProcess -Started $started
            throw "The checkpoint marker '$($Spec.Marker)' was not observed within $MarkerTimeoutSeconds seconds (exit $($completed.ExitCode))."
        }

        $wasRunningAtKill = -not $started.Process.HasExited
        $commandLine = Get-C6ProcessCommandLine -ProcessId $started.ProcessId
        $identityVerified = Test-C6HarnessProcessIdentity -CommandLine $commandLine -RunId $RunId
        if (-not $identityVerified) {
            if (-not $started.Process.HasExited) {
                & taskkill.exe /PID $started.ProcessId /T /F | Out-Null
            }

            $completed = Complete-C6HarnessProcess -Started $started
            throw 'The checkpoint process command line could not be verified as the C6 harness with the exact RunId; no process-tree kill was performed.'
        }

        $killedTreeCount = @(Get-C6ProcessTreeIds -ProcessId $started.ProcessId).Count
        Stop-C6ProcessTree -ProcessId $started.ProcessId
        $completed = Complete-C6HarnessProcess -Started $started
        if (-not $wasRunningAtKill) {
            throw 'The harness exited before the kill; this checkpoint did not exercise a process kill.'
        }

        if ($completed.ExitCode -eq 0) {
            throw 'The killed harness exited successfully; a process kill was not observed.'
        }

        if ([string]$completed.Stdout -match '"status"\s*:\s*"ok"' -or
            [string]$completed.Stdout -match '"passed"\s*:\s*true') {
            throw 'The killed harness emitted a success result; a kill must never report success.'
        }

        $restart = Get-C6KillRestartEvidence -Spec $Spec -Roots $roots
        if (-not [string]::IsNullOrWhiteSpace($restart.EvidencePath)) {
            $evidence.Add($restart.EvidencePath)
        }

        if ($restart.ExitCode -ne 0 -or $null -eq $restart.Json) {
            throw "The restart probe failed with exit $($restart.ExitCode)."
        }

        $target = Read-C6JsonFile -Path $targetPath
        if ($null -ne $target) {
            $context | Add-Member -NotePropertyName baseVersion -NotePropertyValue (Get-C6KillHarnessField $target 'baseVersion' -1) -Force
            $context | Add-Member -NotePropertyName targetNoteId -NotePropertyValue (Get-C6KillHarnessField $target 'noteId' $null) -Force
        }

        $outcome = Test-C6KillCaseOutcome -Spec $Spec -Roots $roots -Context $context `
            -Restart $restart.Json -PackageBefore $context.packageBefore -BasePackagePath $context.packagePath
        if (-not $outcome.Passed) {
            $errorMessage = (@($outcome.Failures) -join ' ')
        }
    }
    catch {
        $errorMessage = $_.Exception.Message
        if ($null -ne $started -and -not $started.Process.HasExited) {
            & taskkill.exe /PID $started.ProcessId /T /F | Out-Null
        }

        if ($null -eq $completed -and $null -ne $started) {
            try {
                $completed = Complete-C6HarnessProcess -Started $started
            }
            catch {
            }
        }
    }

    foreach ($path in @(
        (Join-Path $roots.rawDirectory ($Spec.Case + '-kill.stdout.log')),
        (Join-Path $roots.rawDirectory ($Spec.Case + '-kill.stderr.log')))) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $evidence.Add($path)
        }
    }

    $restartJson = $null
    if ($null -ne $restart) {
        $restartJson = Get-C6KillHarnessField $restart.Json 'restartProbe' $restart.Json
    }

    $status = 'FAIL'
    if ([string]::IsNullOrWhiteSpace($errorMessage)) {
        $status = 'PASS'
    }

    $actual = [ordered]@{
        injection = 'process-tree-kill'
        scenario = $Spec.Scenario
        marker = $Spec.Marker
        markerObserved = $markerSeen
        runId = $RunId
        processId = if ($null -ne $started) { $started.ProcessId } else { $null }
        commandLineVerified = $identityVerified
        runningAtKill = $wasRunningAtKill
        killedTreeProcessCount = $killedTreeCount
        killedExitCode = if ($null -ne $completed) { $completed.ExitCode } else { $null }
        packageBefore = @($context.packageBefore)
        packageAfter = @(Get-C6PackageSummary -Directory $roots.backupDirectory)
        restart = $restartJson
        error = $errorMessage
    }
    $restartForResult = if ($null -ne $restartJson) { $restartJson } else {
        [ordered]@{
            method = 'restart-probe'
            passed = $false
            databaseReadable = $false
            notePresent = $false
            failure = $errorMessage
        }
    }
    $cleanup = [ordered]@{
        status = 'PENDING'
        message = 'The case run root is retained until the explicit Clear-C6Artifacts.ps1 step; evidence is outside the run root.'
    }
    Write-C6CaseResult -RunId $RunId -Case $Spec.Case -Status $status -Scenario $Spec.Scenario `
        -Expected $expected -Actual $actual -Restart $restartForResult `
        -Evidence @($evidence) -Cleanup $cleanup -Reason $errorMessage `
        -EvidenceRoot $evidenceRoot | Out-Null
    return $status
}function Resolve-C6KillCases {
    param([string]$CaseName)

    if ([string]::IsNullOrWhiteSpace($CaseName) -or $CaseName -ieq 'All') {
        return $caseSpecs
    }

    $normalized = $CaseName.Trim().ToUpperInvariant()
    if ($normalized.StartsWith('C6-')) {
        $normalized = $normalized.Substring(3)
    }

    $matched = @($caseSpecs | Where-Object { $_.Case -ieq ('C6-' + $normalized) })
    if ($matched.Count -ne 1) {
        throw "Unknown case '$CaseName'. Supported cases: K01..K09 or All."
    }

    return $matched
}

$selected = @(Resolve-C6KillCases -CaseName $Case)
$statuses = @()
foreach ($spec in $selected) {
    $statuses += Invoke-C6KillCase -Spec $spec
}

if ($statuses -contains 'FAIL' -or $statuses -contains 'NOT RUN') {
    exit 1
}

exit 0
