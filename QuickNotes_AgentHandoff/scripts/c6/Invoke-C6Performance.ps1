#requires -Version 5.1
<#
.SYNOPSIS
Collects the C6 Task 5 performance, window, show/hide, and soak evidence.

Service metrics come from the real C6 harness `perf` body (production host,
save coordinator, editor session, backup, and restore against synthetic
%TEMP%\QuickNotes-C6\<RunId> data). WPF startup/window/show-hide metrics come
from QuickNotes.C6.UiProbe, which hosts the real note windows without the
production App: no tray icon, global hotkey, HKCU Run value, workspace window,
or user data root is touched.

The C0 candidate budgets are only acceptance gates when the caller sets
$env:C6_BUDGETS_CONFIRMED='1'. Otherwise the script reports
budgetStatus=unconfirmed and never claims a performance pass.

Confirmed acceptance shape (fail closed):
- at least 1,000 seeded notes and 20 visible windows;
- at least 10 fresh host startup iterations and 10 WPF launches;
- the 5-, 10-, and 20-window resource checks must all run;
- -SkipUi is rejected because it removes a required acceptance item.

The 2-hour soak gate requires 121 samples for 7,200 s at a 60 s cadence
(minimum 120, one boundary sample tolerance), a maximum sample gap of 90 s,
no unhandled exception/crash/dialog, stable 20-window counts, peak working set
within budget, working-set growth at most 15%, and handle growth at most 50.
A shorter -DurationMinutes run is a structural smoke only; it is never reported
as 2-hour proof.

The result prints a machine-readable 2-hour command that always uses a fresh
RunId and a sibling EvidenceRoot, so rerunning it cannot overwrite the current
smoke evidence. The command also sets the exact C6_BUDGETS_CONFIRMED=1 gate.

Exit 0 only when the requested evidence is PASS or explicitly UNCONFIRMED.
Exit 1 when any check FAILs, the acceptance shape is incomplete, or a required
item (including the 2-hour soak) is NOT RUN.

Examples:
  $env:C6_BUDGETS_CONFIRMED='1'
  pwsh -File scripts/c6/Invoke-C6Performance.ps1 -RunId c6-task5-smoke -Notes 1000 -Visible 20 -DurationMinutes 3 -EvidenceRoot <dir>
  pwsh -File scripts/c6/Invoke-C6Performance.ps1 -RunId c6-task5-2h -Notes 1000 -Visible 20 -DurationMinutes 120 -EvidenceRoot <dir>
#>
param(
    [string]$RunId,
    [int]$Notes = 1000,
    [int]$Visible = 20,
    [int]$StartupIterations = 10,
    [int]$UiLaunches = 10,
    [int]$ShowHideCycles = 30,
    [int]$DurationMinutes = 0,
    [int]$SoakSampleSeconds = 60,
    [string]$EvidenceRoot,
    [switch]$SkipUi,
    [switch]$SkipSoak
)

. (Join-Path $PSScriptRoot 'C6ScriptCommon.ps1')

if ($Notes -lt 1) {
    throw 'Notes must be at least 1.'
}

if ($Visible -lt 1 -or $Visible -gt $Notes) {
    throw 'Visible must be between 1 and Notes.'
}

if ($StartupIterations -lt 1 -or $UiLaunches -lt 1) {
    throw 'StartupIterations and UiLaunches must be at least 1.'
}

if ($ShowHideCycles -lt 0) {
    throw 'ShowHideCycles must not be negative.'
}

if ($DurationMinutes -lt 0) {
    throw 'DurationMinutes must not be negative.'
}

if ($SoakSampleSeconds -lt 10) {
    throw 'SoakSampleSeconds must be at least 10.'
}

Assert-C6RunId -RunId $RunId
$handoffRoot = Get-C6HandoffRoot -ScriptRoot $PSScriptRoot
$runRoot = Get-C6RunRoot -RunId $RunId
$evidenceRoot = Get-C6EvidenceRoot -HandoffRoot $handoffRoot -RunId $RunId `
    -EvidenceRoot $EvidenceRoot -EvidenceTaskName 'task-5-evidence'
$rawDirectory = Join-Path $evidenceRoot 'raw'
[System.IO.Directory]::CreateDirectory($runRoot) | Out-Null
[System.IO.Directory]::CreateDirectory($rawDirectory) | Out-Null
$dataRoot = Join-Path $runRoot 'data'
$perfOutput = Join-Path $rawDirectory 'perf.json'

$budgetConfirmed = [string]$env:C6_BUDGETS_CONFIRMED -eq '1'
$budgetStatus = if ($budgetConfirmed) { 'confirmed' } else { 'unconfirmed' }

$twoHour = New-C6TwoHourSoakCommand -ScriptRoot $PSScriptRoot -RunId $RunId `
    -Notes 1000 -Visible 20 -StartupIterations 10 -UiLaunches 10 `
    -ShowHideCycles 30 -SoakSampleSeconds 60 -EvidenceRoot $evidenceRoot
$cleanup = [ordered]@{
    runRootRetained = $true
    cleanupCommand = ('pwsh -File scripts/c6/Clear-C6Artifacts.ps1 -RunId ' + $RunId)
}

# The confirmed acceptance shape is validated before any build or seeding so a
# partial run cannot quietly become "PASS". The 5/10/20 window presence is
# checked again after the UI runs.
$preflightFailures = New-Object System.Collections.Generic.List[string]
if ($budgetConfirmed) {
    if ($Notes -lt 1000) {
        $preflightFailures.Add("Notes must be at least 1000 for the confirmed acceptance run (received $Notes).")
    }

    if ($Visible -lt 20) {
        $preflightFailures.Add("Visible must be at least 20 for the confirmed acceptance run (received $Visible).")
    }

    if ($StartupIterations -lt 10) {
        $preflightFailures.Add("StartupIterations must be at least 10 for the confirmed acceptance run (received $StartupIterations).")
    }

    if ($UiLaunches -lt 10) {
        $preflightFailures.Add("UiLaunches must be at least 10 for the confirmed acceptance run (received $UiLaunches).")
    }

    if ($SkipUi) {
        $preflightFailures.Add('SkipUi removes the required 10-launch and 5/10/20-window acceptance evidence.')
    }
}

if ($preflightFailures.Count -gt 0) {
    $preflightAcceptance = [ordered]@{
        enforced = $true
        notes = [ordered]@{ required = 1000; actual = $Notes; passed = ($Notes -ge 1000) }
        visibleWindows = [ordered]@{ required = 20; actual = $Visible; passed = ($Visible -ge 20) }
        startupIterations = [ordered]@{ required = 10; actual = $StartupIterations; passed = ($StartupIterations -ge 10) }
        uiLaunches = [ordered]@{ required = 10; actual = $UiLaunches; passed = ($UiLaunches -ge 10) }
        requiredWindowCounts = @(5, 10, 20)
        observedWindowCounts = @()
        missingWindowCounts = @(5, 10, 20)
        windowChecksPassed = $false
        skipUi = [bool]$SkipUi
        failures = @($preflightFailures)
        passed = $false
    }
    $preflightActual = [ordered]@{
        budgetStatus = $budgetStatus
        budgetConfirmed = $budgetConfirmed
        performanceStatus = 'FAIL'
        notes = $Notes
        visibleWindows = $Visible
        startupIterations = $StartupIterations
        uiLaunches = $UiLaunches
        acceptance = $preflightAcceptance
        twoHourSoak = [ordered]@{
            status = 'NOT RUN'
            requiredMinutes = 120
            command = $twoHour.command
            commandShell = $twoHour.shell
            runId = $twoHour.runId
            evidenceRoot = $twoHour.evidenceRoot
        }
    }
    $preflightReason = 'The confirmed acceptance shape is incomplete: ' + ($preflightFailures -join ' ')
    $null = Write-C6CaseResult -RunId $RunId -Case 'C6-PERF' -Status 'FAIL' `
        -Scenario 'perf' `
        -Expected 'The confirmed acceptance run must use at least 1,000 notes, 20 visible windows, 10 startup iterations, 10 UI launches, and the 5/10/20 window checks.' `
        -Actual $preflightActual -Restart $null -Evidence @() -Cleanup $cleanup `
        -Reason $preflightReason -EvidenceRoot $evidenceRoot
    exit 1
}

function Get-C6UiProbeDll {
    param([string]$HandoffRoot)

    $project = Join-Path $HandoffRoot 'tests\QuickNotes.C6.UiProbe\QuickNotes.C6.UiProbe.csproj'
    $dll = Join-Path $HandoffRoot 'tests\QuickNotes.C6.UiProbe\bin\Debug\net10.0-windows\QuickNotes.C6.UiProbe.dll'
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
        & dotnet build $project --nologo -v:q | Out-Null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $dll -PathType Leaf)) {
            throw 'QuickNotes.C6.UiProbe could not be built.'
        }
    }

    return [System.IO.Path]::GetFullPath($dll)
}

function Invoke-C6UiProbeLaunch {
    param(
        [string]$ProbeDll,
        [string[]]$Arguments,
        [int]$TimeoutSeconds,
        [string]$Name
    )

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = 'dotnet'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.WorkingDirectory = [System.IO.Path]::GetDirectoryName($ProbeDll)
    $startInfo.ArgumentList.Add($ProbeDll)
    foreach ($argument in @($Arguments)) {
        $startInfo.ArgumentList.Add([string]$argument)
    }

    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "The C6 UI probe '$Name' could not be started."
    }

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $exited = $process.WaitForExit($TimeoutSeconds * 1000)
    if (-not $exited) {
        Stop-C6ProcessTree -ProcessId $process.Id
        [void]$process.WaitForExit(10000)
        throw "The C6 UI probe '$Name' exceeded $TimeoutSeconds seconds and was stopped."
    }

    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText(
        (Join-Path $rawDirectory ("ui-{0}.log" -f $Name)),
        ($stdout + [Environment]::NewLine + $stderr),
        $encoding)
    $jsonLine = Get-C6LastJsonLine -Lines ($stdout -split "`n")
    if ($null -eq $jsonLine) {
        throw "The C6 UI probe '$Name' produced no JSON (exit $($process.ExitCode)). $stderr"
    }

    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        Json = ($jsonLine | ConvertFrom-Json)
        RawOutput = ($stdout + [Environment]::NewLine + $stderr)
    }
}

function Get-C6Percentile {
    param([double[]]$Values, [int]$Percentile)

    if ($null -eq $Values -or $Values.Count -eq 0) {
        return $null
    }

    $ordered = @($Values | Sort-Object)
    $rank = [int][Math]::Ceiling(($Percentile / 100.0) * $ordered.Count)
    $rank = [Math]::Min([Math]::Max($rank, 1), $ordered.Count)
    return [double]$ordered[$rank - 1]
}

function New-C6PerfCheck {
    param(
        [string]$Name,
        [double]$Actual,
        [double]$Limit,
        [string]$Unit,
        [bool]$BudgetGated = $true
    )

    return [ordered]@{
        name = $Name
        actual = $Actual
        limit = $Limit
        unit = $Unit
        budgetGated = $BudgetGated
        status = 'unconfirmed'
        passed = $false
    }
}

function Set-C6PerfCheckResult {
    param($Check, [bool]$Confirmed)

    if (-not $Confirmed -and [bool]$Check.budgetGated) {
        $Check.status = 'unconfirmed'
        $Check.passed = $false
    }
    elseif ($Check.actual -le $Check.limit) {
        $Check.status = 'pass'
        $Check.passed = $true
    }
    else {
        $Check.status = 'fail'
        $Check.passed = $false
    }
}

function Test-C6MetricPresent {
    param($Metrics, [string]$Name)

    foreach ($metric in @($Metrics)) {
        if ([string](Get-C6ObjectProperty $metric 'name' '') -ne $Name) {
            continue
        }

        if ($null -eq (Get-C6ObjectProperty $metric 'error' $null) -and
            [int](Get-C6ObjectProperty $metric 'sampleCount' 0) -gt 0) {
            return $true
        }
    }

    return $false
}

# Validates that UI evidence describes the same seeded history, the requested
# window count, a responsive process, and no unexpected dialog/modal window.
function Test-C6UiPayload {
    param(
        $Payload,
        [string]$Name,
        [int]$ExpectedWindows,
        [int]$ExpectedNotes
    )

    $failures = New-Object System.Collections.Generic.List[string]
    $noteCount = Get-C6ObjectProperty $Payload 'noteCount' $null
    $noteCountMismatch = ($null -eq $noteCount -or [int]$noteCount -ne $ExpectedNotes)
    if ($noteCountMismatch) {
        $failures.Add("$Name noteCount was '$noteCount', expected $ExpectedNotes.")
    }

    $windowCount = Get-C6ObjectProperty $Payload 'windowCount' $null
    if ($null -eq $windowCount -or [int]$windowCount -ne $ExpectedWindows) {
        $failures.Add("$Name windowCount was '$windowCount', expected $ExpectedWindows.")
    }

    $responding = Get-C6ObjectProperty $Payload 'processResponding' $null
    if ($null -eq $responding -or -not [bool]$responding) {
        $failures.Add("$Name did not report a responsive process.")
    }

    $unexpected = Get-C6ObjectProperty $Payload 'unexpectedWindowCount' $null
    if ($null -eq $unexpected -or [int]$unexpected -ne 0) {
        $failures.Add("$Name reported '$unexpected' unexpected dialog/window(s), expected 0.")
    }

    return [pscustomobject]@{
        Failures = @($failures)
        NoteCountMismatch = $noteCountMismatch
    }
}

# Build harness and UI probe, seed synthetic 1,000-note history.
& dotnet build (Join-Path $handoffRoot 'tests\QuickNotes.C6.Harness\QuickNotes.C6.Harness.csproj') `
    --nologo -v:q | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'QuickNotes.C6.Harness could not be built for the performance run.'
}

& dotnet build (Join-Path $handoffRoot 'tests\QuickNotes.C6.UiProbe\QuickNotes.C6.UiProbe.csproj') `
    --nologo -v:q | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'QuickNotes.C6.UiProbe could not be built for the performance run.'
}

Get-C6HarnessDll -HandoffRoot $handoffRoot | Out-Null
$probeDll = Get-C6UiProbeDll -HandoffRoot $handoffRoot
$seed = Invoke-C6Harness -HandoffRoot $handoffRoot -Arguments @(
    'seed',
    '--data-root', $dataRoot,
    '--notes', [string]$Notes,
    '--visible', [string]$Visible,
    '--run-id', $RunId
)
if ($seed.ExitCode -ne 0 -or [string](Get-C6ObjectProperty $seed.Json 'status' '') -ne 'ok') {
    throw "The C6 seed failed with exit $($seed.ExitCode)."
}

# Archived/trash notes in the deterministic seed are not active, so window
# counts above the active-note count are skipped instead of failing a launch.
$activeNotes = [int](Get-C6ObjectProperty $seed.Json 'noteCount' $Notes)
$totalSeededNotes = [int](Get-C6ObjectProperty $seed.Json 'totalNoteCount' $Notes)

if ($budgetConfirmed -and ($totalSeededNotes -lt 1000 -or $activeNotes -lt 20)) {
    $shortShape = [ordered]@{
        enforced = $true
        seedTotalNoteCount = $totalSeededNotes
        seedActiveNoteCount = $activeNotes
        notes = [ordered]@{ required = 1000; actual = $totalSeededNotes; passed = ($totalSeededNotes -ge 1000) }
        activeNotes = [ordered]@{ required = 20; actual = $activeNotes; passed = ($activeNotes -ge 20) }
        passed = $false
    }
    $shortActual = [ordered]@{
        budgetStatus = $budgetStatus
        budgetConfirmed = $budgetConfirmed
        performanceStatus = 'FAIL'
        seed = [ordered]@{ requestedNotes = $Notes; totalNoteCount = $totalSeededNotes; noteCount = $activeNotes }
        acceptance = $shortShape
    }
    $shortReason = "The confirmed acceptance seed has $totalSeededNotes total notes and $activeNotes active notes; at least 1000 total and 20 active are required."
    $null = Write-C6CaseResult -RunId $RunId -Case 'C6-PERF' -Status 'FAIL' `
        -Scenario 'perf' -Expected 'The confirmed acceptance seed must contain at least 1,000 notes with at least 20 active windows.' `
        -Actual $shortActual -Restart $null -Evidence @() -Cleanup $cleanup `
        -Reason $shortReason -EvidenceRoot $evidenceRoot
    exit 1
}

# Service-level timing, save/input, backup/restore, and scaling probes.
$perf = Invoke-C6Harness -HandoffRoot $handoffRoot -Arguments @(
    'perf',
    '--data-root', $dataRoot,
    '--notes', [string]$Notes,
    '--visible', [string]$Visible,
    '--iterations', [string]$StartupIterations,
    '--output', $perfOutput,
    '--run-id', $RunId
)
$service = $perf.Json
if ($perf.ExitCode -ne 0 -or [string](Get-C6ObjectProperty $service 'status' '') -ne 'ok') {
    throw "The C6 perf scenario failed with exit $($perf.ExitCode)."
}

$budget = Get-C6ObjectProperty $service 'budget' $null
if ($null -eq $budget) {
    throw 'The C6 perf result did not echo the budget constants; refusing to guess thresholds.'
}

$checks = New-Object System.Collections.Generic.List[object]
$serviceMetrics = @(Get-C6ObjectProperty $service 'metrics' @())
foreach ($metric in $serviceMetrics) {
    $name = [string]$metric.name
    $metricError = Get-C6ObjectProperty $metric 'error' $null
    if ($null -ne $metricError) {
        $checks.Add((New-C6PerfCheck ("{0}_samples" -f $name) 1 0 'count' $false))
        continue
    }

    if ([int](Get-C6ObjectProperty $metric 'sampleCount' 0) -le 0) {
        $checks.Add((New-C6PerfCheck ("{0}_missing" -f $name) 1 0 'count' $false))
        continue
    }

    $p95 = [double](Get-C6ObjectProperty $metric 'p95Ms' 0)
    $p99 = [double](Get-C6ObjectProperty $metric 'p99Ms' 0)
    switch ($name) {
        'startup_host' {
            $checks.Add((New-C6PerfCheck 'startup_host_p95' $p95 ([double]$budget.startupReadyP95Ms) 'ms'))
        }
        'save' {
            $checks.Add((New-C6PerfCheck 'save_p95' $p95 ([double]$budget.saveP95Ms) 'ms'))
            $checks.Add((New-C6PerfCheck 'save_p99' $p99 ([double]$budget.saveP99Ms) 'ms'))
        }
        'input' {
            $checks.Add((New-C6PerfCheck 'input_p95' $p95 ([double]$budget.inputP95Ms) 'ms'))
        }
    }
}

# Couple the PowerShell verdict to the harness verdict instead of trusting only
# the checks the script happened to rebuild from metrics.
$requiredMetricNames = @('startup_host', 'save', 'input')
$missingMetricNames = @($requiredMetricNames | Where-Object {
    -not (Test-C6MetricPresent -Metrics $serviceMetrics -Name $_)
})
$errorMetricNames = @($serviceMetrics | Where-Object {
    $null -ne (Get-C6ObjectProperty $_ 'error' $null)
} | ForEach-Object { [string]$_.name })

$requiredServiceChecks = @('startup_host_p95', 'save_p95', 'save_p99', 'input_p95')
$serviceBudgetChecks = @(Get-C6ObjectProperty $service 'budgetChecks' @())
$observedServiceCheckNames = @($serviceBudgetChecks | ForEach-Object { [string]$_.name })
$missingServiceChecks = @($requiredServiceChecks | Where-Object {
    $observedServiceCheckNames -notcontains $_
})
$mismatchedServiceChecks = New-Object System.Collections.Generic.List[string]
foreach ($requiredCheckName in $requiredServiceChecks) {
    $serviceCheck = @($serviceBudgetChecks | Where-Object {
        [string]$_.name -eq $requiredCheckName
    }) | Select-Object -First 1
    $scriptCheck = @($checks | Where-Object {
        [string]$_.name -eq $requiredCheckName
    }) | Select-Object -First 1
    if ($null -ne $serviceCheck -and $null -ne $scriptCheck) {
        $serviceActual = [double](Get-C6ObjectProperty $serviceCheck 'actual' 0)
        $scriptActual = [double](Get-C6ObjectProperty $scriptCheck 'actual' 0)
        $serviceLimit = [double](Get-C6ObjectProperty $serviceCheck 'limit' 0)
        $scriptLimit = [double](Get-C6ObjectProperty $scriptCheck 'limit' 0)
        if ([Math]::Abs($serviceActual - $scriptActual) -gt 0.0001 -or
            [Math]::Abs($serviceLimit - $scriptLimit) -gt 0.0001) {
            $mismatchedServiceChecks.Add(
                "$requiredCheckName service=$serviceActual/$serviceLimit script=$scriptActual/$scriptLimit")
        }
    }
}

$expectedServicePerformanceStatus = if ($budgetConfirmed) { 'pass' } else { 'unconfirmed' }
$actualServicePerformanceStatus = [string](Get-C6ObjectProperty $service 'performanceStatus' '')
$actualServiceBudgetStatus = [string](Get-C6ObjectProperty $service 'budgetStatus' '')
$servicePassed = [bool](Get-C6ObjectProperty $service 'passed' $false)
$serviceStatusCouplingPassed = (
    [string](Get-C6ObjectProperty $service 'status' '') -eq 'ok' -and
    $servicePassed -and
    $actualServicePerformanceStatus -eq $expectedServicePerformanceStatus -and
    $actualServiceBudgetStatus -eq $budgetStatus -and
    $missingMetricNames.Count -eq 0 -and
    $errorMetricNames.Count -eq 0 -and
    $missingServiceChecks.Count -eq 0 -and
    $mismatchedServiceChecks.Count -eq 0)

$checks.Add((New-C6PerfCheck 'service_status_coupling' $(if ($serviceStatusCouplingPassed) { 0 } else { 1 }) 0 'count' $false))
$checks.Add((New-C6PerfCheck 'required_service_metrics' ($missingMetricNames.Count + $errorMetricNames.Count) 0 'count' $false))
$checks.Add((New-C6PerfCheck 'service_budget_checks' ($missingServiceChecks.Count + $mismatchedServiceChecks.Count) 0 'count' $false))

$startupMetric = @($serviceMetrics | Where-Object { [string]$_.name -eq 'startup_host' }) | Select-Object -First 1
$saveMetric = @($serviceMetrics | Where-Object { [string]$_.name -eq 'save' }) | Select-Object -First 1
$inputMetric = @($serviceMetrics | Where-Object { [string]$_.name -eq 'input' }) | Select-Object -First 1
if ($budgetConfirmed) {
    $startupCount = if ($null -eq $startupMetric) { 0 } else { [int](Get-C6ObjectProperty $startupMetric 'sampleCount' 0) }
    $checks.Add((New-C6PerfCheck 'startup_host_sample_count_shortfall' `
        ([Math]::Max(0, 10 - $startupCount)) 0 'count' $false))
}

$saveCount = if ($null -eq $saveMetric) { 0 } else { [int](Get-C6ObjectProperty $saveMetric 'sampleCount' 0) }
$inputCount = if ($null -eq $inputMetric) { 0 } else { [int](Get-C6ObjectProperty $inputMetric 'sampleCount' 0) }
$checks.Add((New-C6PerfCheck 'save_sample_count_shortfall' ([Math]::Max(0, 50 - $saveCount)) 0 'count' $false))
$checks.Add((New-C6PerfCheck 'input_sample_count_shortfall' ([Math]::Max(0, 100 - $inputCount)) 0 'count' $false))

$checks.Add((New-C6PerfCheck 'service_notes_mismatch' `
    ([Math]::Abs([int](Get-C6ObjectProperty $service 'noteCount' 0) - $Notes)) 0 'count' $false))
$checks.Add((New-C6PerfCheck 'service_visible_mismatch' `
    ([Math]::Abs([int](Get-C6ObjectProperty $service 'visibleCount' 0) - $Visible)) 0 'count' $false))
$checks.Add((New-C6PerfCheck 'service_iterations_mismatch' `
    ([Math]::Abs([int](Get-C6ObjectProperty $service 'iterations' 0) - $StartupIterations)) 0 'count' $false))

$restoreChecks = @(Get-C6ObjectProperty $service 'restoreChecks' @())
$restoreFailures = @($restoreChecks | Where-Object {
    -not [bool](Get-C6ObjectProperty $_ 'passed' $false) -or
    [int](Get-C6ObjectProperty $_ 'restoredNoteCount' -1) -ne [int](Get-C6ObjectProperty $_ 'noteCount' -1)
})
$requiredRestoreSizes = if ($budgetConfirmed) { @(100, 1000) } else { @() }
$observedRestoreSizes = @($restoreChecks | ForEach-Object { [int](Get-C6ObjectProperty $_ 'noteCount' 0) })
$missingRestoreSizes = @($requiredRestoreSizes | Where-Object { $observedRestoreSizes -notcontains $_ })
$checks.Add((New-C6PerfCheck 'restore_checks' `
    ($restoreFailures.Count + $missingRestoreSizes.Count) 0 'count' $false))

# WPF startup x10, 5/10/20-window resources, and repeated show/hide.
$uiStatus = 'NOT RUN'
$uiReason = $null
$uiLaunchResults = New-Object System.Collections.Generic.List[object]
$windowRuns = New-Object System.Collections.Generic.List[object]
$showHide = $null
$uiReadyValues = New-Object System.Collections.Generic.List[double]
$uiHostValues = New-Object System.Collections.Generic.List[double]
$uiEvidenceFailureDetails = New-Object System.Collections.Generic.List[string]
$uiNoteCountMismatches = 0
if (-not $SkipUi) {
    $uiStatus = 'PASS'
    for ($index = 1; $index -le $UiLaunches; $index++) {
        $output = Join-Path $rawDirectory ("ui-startup-{0:D2}.json" -f $index)
        try {
            $launch = Invoke-C6UiProbeLaunch -ProbeDll $probeDll -Name ("startup-{0:D2}" -f $index) `
                -TimeoutSeconds 120 -Arguments @(
                    '--data-root', $dataRoot,
                    '--visible', [string]$Visible,
                    '--show-hide-cycles', '0',
                    '--run-id', $RunId,
                    '--output', $output
                )
        }
        catch {
            $uiStatus = 'FAIL'
            $uiReason = $_.Exception.Message
            break
        }

        if ($launch.ExitCode -ne 0 -or [string](Get-C6ObjectProperty $launch.Json 'status' '') -ne 'ok') {
            $uiStatus = 'FAIL'
            $uiReason = "UI startup launch $index exited $($launch.ExitCode)."
            break
        }

        $payload = Test-C6UiPayload -Payload $launch.Json -Name "startup-$index" `
            -ExpectedWindows $Visible -ExpectedNotes $activeNotes
        if ($payload.NoteCountMismatch) {
            $uiNoteCountMismatches++
        }

        $payloadFailures = @($payload.Failures)
        if ($payloadFailures.Count -gt 0) {
            $uiStatus = 'FAIL'
            $uiReason = ($payloadFailures -join ' ')
            $uiEvidenceFailureDetails.Add($uiReason)
            break
        }

        $launchNoteCount = [int]$launch.Json.noteCount
        $uiReadyValues.Add([double]$launch.Json.readyMs)
        $uiHostValues.Add([double]$launch.Json.hostStartMs)
        $uiLaunchResults.Add([ordered]@{
            launch = $index
            noteCount = $launchNoteCount
            readyMs = [double]$launch.Json.readyMs
            hostStartMs = [double]$launch.Json.hostStartMs
            windowCount = [int]$launch.Json.windowCount
            workingSetBytes = [int64]$launch.Json.workingSetBytes
            handleCount = [int]$launch.Json.handleCount
            processResponding = [bool]$launch.Json.processResponding
            unexpectedWindowCount = [int]$launch.Json.unexpectedWindowCount
            startupMode = [string]$launch.Json.startupMode
        })
    }

    if ($uiStatus -eq 'PASS') {
        $readyP95 = Get-C6Percentile -Values $uiReadyValues.ToArray() -Percentile 95
        $hostP95 = Get-C6Percentile -Values $uiHostValues.ToArray() -Percentile 95
        $checks.Add((New-C6PerfCheck 'startup_ui_p95' ([double]$readyP95) ([double]$budget.startupWithUiP95Ms) 'ms'))
        $checks.Add((New-C6PerfCheck 'startup_ui_host_p95' ([double]$hostP95) ([double]$budget.startupReadyP95Ms) 'ms'))

        $windowCounts = @(5, 10, 20 | Where-Object { $_ -le $activeNotes } | Select-Object -Unique)
        foreach ($count in $windowCounts) {
            $output = Join-Path $rawDirectory ("ui-windows-{0:D2}.json" -f $count)
            try {
                $launch = Invoke-C6UiProbeLaunch -ProbeDll $probeDll -Name ("windows-{0:D2}" -f $count) `
                    -TimeoutSeconds 120 -Arguments @(
                        '--data-root', $dataRoot,
                        '--visible', [string]$count,
                        '--show-hide-cycles', '0',
                        '--run-id', $RunId,
                        '--output', $output
                    )
            }
            catch {
                $uiStatus = 'FAIL'
                $uiReason = $_.Exception.Message
                $uiEvidenceFailureDetails.Add($uiReason)
                break
            }

            if ($launch.ExitCode -ne 0 -or [string](Get-C6ObjectProperty $launch.Json 'status' '') -ne 'ok') {
                $uiStatus = 'FAIL'
                $uiReason = "UI window run $count exited $($launch.ExitCode)."
                $uiEvidenceFailureDetails.Add($uiReason)
                break
            }

            $payload = Test-C6UiPayload -Payload $launch.Json -Name "windows-$count" `
                -ExpectedWindows $count -ExpectedNotes $activeNotes
            if ($payload.NoteCountMismatch) {
                $uiNoteCountMismatches++
            }

            $payloadFailures = @($payload.Failures)
            if ($payloadFailures.Count -gt 0) {
                $uiStatus = 'FAIL'
                $uiReason = ($payloadFailures -join ' ')
                $uiEvidenceFailureDetails.Add($uiReason)
                break
            }

            $windowRuns.Add([ordered]@{
                count = $count
                noteCount = [int]$launch.Json.noteCount
                windowCount = [int]$launch.Json.windowCount
                readyMs = [double]$launch.Json.readyMs
                workingSetBytes = [int64]$launch.Json.workingSetBytes
                privateBytes = [int64]$launch.Json.privateBytes
                handleCount = [int]$launch.Json.handleCount
                threadCount = [int]$launch.Json.threadCount
                processResponding = [bool]$launch.Json.processResponding
                unexpectedWindowCount = [int]$launch.Json.unexpectedWindowCount
            })
            $checks.Add((New-C6PerfCheck ("windows_{0}_working_set" -f $count) `
                ([double]$launch.Json.workingSetBytes) ([double]$budget.workingSetBytes) 'bytes'))
        }
    }

    if ($uiStatus -eq 'PASS' -and $ShowHideCycles -gt 0) {
        $output = Join-Path $rawDirectory 'ui-show-hide.json'
        try {
            $launch = Invoke-C6UiProbeLaunch -ProbeDll $probeDll -Name 'show-hide' `
                -TimeoutSeconds 180 -Arguments @(
                    '--data-root', $dataRoot,
                    '--visible', [string]$Visible,
                    '--show-hide-cycles', [string]$ShowHideCycles,
                    '--run-id', $RunId,
                    '--output', $output
                )
            if ($launch.ExitCode -ne 0 -or [string](Get-C6ObjectProperty $launch.Json 'status' '') -ne 'ok') {
                $uiStatus = 'FAIL'
                $uiReason = "UI show/hide run exited $($launch.ExitCode)."
                $uiEvidenceFailureDetails.Add($uiReason)
            }
            else {
                $payload = Test-C6UiPayload -Payload $launch.Json -Name 'show-hide' `
                    -ExpectedWindows $Visible -ExpectedNotes $activeNotes
                if ($payload.NoteCountMismatch) {
                    $uiNoteCountMismatches++
                }

                $payloadFailures = @($payload.Failures)
                if ($payloadFailures.Count -gt 0) {
                    $uiStatus = 'FAIL'
                    $uiReason = ($payloadFailures -join ' ')
                    $uiEvidenceFailureDetails.Add($uiReason)
                }
                elseif ([int]$launch.Json.showHideCycles -ne $ShowHideCycles) {
                    $uiStatus = 'FAIL'
                    $uiReason = "UI show/hide recorded $($launch.Json.showHideCycles) cycles, expected $ShowHideCycles."
                    $uiEvidenceFailureDetails.Add($uiReason)
                }
                else {
                    $showHide = [ordered]@{
                        cycles = [int]$launch.Json.showHideCycles
                        noteCount = [int]$launch.Json.noteCount
                        p50Ms = [double]$launch.Json.showHideP50Ms
                        p95Ms = [double]$launch.Json.showHideP95Ms
                        maxMs = [double]$launch.Json.showHideMaxMs
                    }
                }
            }
        }
        catch {
            $uiStatus = 'FAIL'
            $uiReason = $_.Exception.Message
            $uiEvidenceFailureDetails.Add($uiReason)
        }
    }
}

# Optional soak. Any duration shorter than 120 minutes is explicitly a smoke.
$soakVisible = [Math]::Min(20, $Notes)
$twoHourRequiredSampleCount = [int][Math]::Floor(7200 / $SoakSampleSeconds) + 1
$twoHourMinimumSampleCount = [Math]::Max(2, $twoHourRequiredSampleCount - 1)
$twoHourCadenceLimitSeconds = [double]$SoakSampleSeconds * 1.5
$soak = [ordered]@{
    status = 'NOT RUN'
    requestedMinutes = $DurationMinutes
    requestedSeconds = ($DurationMinutes * 60)
    sampleSeconds = $SoakSampleSeconds
    requiredMinutes = 120
    requiredSampleCount = $twoHourRequiredSampleCount
    minimumSampleCount = $twoHourMinimumSampleCount
    cadenceLimitSeconds = $twoHourCadenceLimitSeconds
    maximumWorkingSetGrowthPercent = 15
    maximumHandleGrowth = 50
    reason = $null
    command = $twoHour.command
    expectedSampleCount = $null
    minimumAcceptedSampleCount = $null
    maxGapSeconds = $null
    finalGapSeconds = $null
    workingSetGrowthPercent = $null
    handleGrowth = $null
    growthEvaluated = $false
    growthMethod = $null
    sampleCount = 0
    completed = $false
    checks = @()
    peakWorkingSetBytes = $null
    peakHandleCount = $null
    firstWorkingSetBytes = $null
    lastWorkingSetBytes = $null
    firstHandleCount = $null
    lastHandleCount = $null
}
$soakChecks = New-Object System.Collections.Generic.List[object]
if ($SkipSoak) {
    $soak.reason = 'Skipped by -SkipSoak; the 2-hour soak is NOT RUN.'
}
elseif ($DurationMinutes -eq 0) {
    $soak.reason = 'No duration requested; the 2-hour soak is NOT RUN.'
}
else {
    $soakSeconds = $DurationMinutes * 60
    $samplesPath = Join-Path $rawDirectory 'soak-samples.jsonl'
    $soakOutput = Join-Path $rawDirectory 'soak.json'
    if (Test-Path -LiteralPath $samplesPath -PathType Leaf) {
        Remove-Item -LiteralPath $samplesPath -Force
    }

    try {
        $launch = Invoke-C6UiProbeLaunch -ProbeDll $probeDll -Name 'soak' `
            -TimeoutSeconds ($soakSeconds + 300) -Arguments @(
                '--data-root', $dataRoot,
                '--visible', [string]$soakVisible,
                '--show-hide-cycles', '0',
                '--soak-seconds', [string]$soakSeconds,
                '--sample-seconds', [string]$SoakSampleSeconds,
                '--run-id', $RunId,
                '--samples', $samplesPath,
                '--output', $soakOutput
            )
        if ($launch.ExitCode -ne 0 -or [string](Get-C6ObjectProperty $launch.Json 'status' '') -ne 'ok') {
            $soak.status = 'FAIL'
            $soak.reason = "The soak process exited $($launch.ExitCode)."
            $soakChecks.Add((New-C6PerfCheck 'soak_probe_failure' 1 0 'count' $false))
        }
        else {
            $payload = Test-C6UiPayload -Payload $launch.Json -Name 'soak' `
                -ExpectedWindows $soakVisible -ExpectedNotes $activeNotes
            if ($payload.NoteCountMismatch) {
                $uiNoteCountMismatches++
            }

            $payloadFailures = @($payload.Failures)
            if ($payloadFailures.Count -gt 0) {
                $soak.status = 'FAIL'
                $soak.reason = ($payloadFailures -join ' ')
                $soakChecks.Add((New-C6PerfCheck 'soak_probe_failure' 1 0 'count' $false))
            }

            $soakEvidence = Get-C6ObjectProperty $launch.Json 'soak' $null
            if ($soak.status -ne 'FAIL' -and $null -eq $soakEvidence) {
                $soak.status = 'FAIL'
                $soak.reason = 'The soak result contained no sample evidence.'
                $soakChecks.Add((New-C6PerfCheck 'soak_probe_failure' 1 0 'count' $false))
            }

            if ($soak.status -ne 'FAIL') {
                $soak.peakWorkingSetBytes = [int64]$soakEvidence.peakWorkingSetBytes
                $soak.peakHandleCount = [int]$soakEvidence.peakHandleCount
                $soak.firstWorkingSetBytes = $soakEvidence.firstWorkingSetBytes
                $soak.lastWorkingSetBytes = $soakEvidence.lastWorkingSetBytes
                $soak.firstHandleCount = $soakEvidence.firstHandleCount
                $soak.lastHandleCount = $soakEvidence.lastHandleCount
                $soak.sampleCount = [int]$soakEvidence.sampleCount
                $soak.completed = [bool]$soakEvidence.completed
                if (-not $soak.completed) {
                    $soak.status = 'FAIL'
                    $soak.reason = 'The soak process exited before its requested duration.'
                }

                try {
                    $soakSamples = @(Read-C6SoakSamples -Path $samplesPath)
                    $measure = Measure-C6SoakEvidence -Samples $soakSamples `
                        -DurationSeconds $soakSeconds -SampleSeconds $SoakSampleSeconds `
                        -ExpectedWindowCount $soakVisible `
                        -EvaluateGrowth ($DurationMinutes -ge 120)
                }
                catch {
                    $measure = $null
                    $soak.status = 'FAIL'
                    $soak.reason = $_.Exception.Message
                }

                if ($null -ne $measure) {
                    $soak.expectedSampleCount = $measure.ExpectedSampleCount
                    $soak.minimumAcceptedSampleCount = $measure.MinimumSampleCount
                    $soak.maxGapSeconds = $measure.MaxGapSeconds
                    $soak.finalGapSeconds = $measure.FinalGapSeconds
                    $soak.workingSetGrowthPercent = $measure.WorkingSetGrowthPercent
                    $soak.handleGrowth = $measure.HandleGrowth
                    $soak.growthEvaluated = $measure.GrowthEvaluated
                    $soak.growthMethod = $measure.GrowthMethod
                    $soak.sampleCount = $measure.SampleCount
                    if ($soak.status -ne 'FAIL' -and -not $measure.StructuralPassed) {
                        $soak.status = 'FAIL'
                        $soak.reason = ($measure.StructuralFailures -join ' ')
                    }

                    $soakChecks.Add((New-C6PerfCheck 'soak_completed' $(if ($soak.completed) { 0 } else { 1 }) 0 'count' $false))
                    $soakChecks.Add((New-C6PerfCheck 'soak_sample_count_shortfall' `
                        ([double]$measure.SampleCountShortfall) 0 'count' $false))
                    $soakChecks.Add((New-C6PerfCheck 'soak_sample_summary_mismatch' `
                        ([Math]::Abs($soak.sampleCount - [int]$soakEvidence.sampleCount)) 0 'count' $false))
                    $soakChecks.Add((New-C6PerfCheck 'soak_cadence_max_gap_seconds' `
                        ([double]$measure.MaxGapSeconds) ([double]$measure.CadenceLimitSeconds) 'seconds' $false))
                    $soakChecks.Add((New-C6PerfCheck 'soak_final_sample_gap_seconds' `
                        ([double]$measure.FinalGapSeconds) ([double]$measure.CadenceLimitSeconds) 'seconds' $false))
                    $soakChecks.Add((New-C6PerfCheck 'soak_window_instability' `
                        $measure.WindowInstabilityCount 0 'count' $false))
                    $soakChecks.Add((New-C6PerfCheck 'soak_process_unresponsive' `
                        $measure.ProcessUnresponsiveCount 0 'count' $false))
                    $soakChecks.Add((New-C6PerfCheck 'soak_unexpected_windows' `
                        $measure.UnexpectedWindowCount 0 'count' $false))

                    if ($soak.status -ne 'FAIL') {
                        $soakChecks.Add((New-C6PerfCheck 'soak_peak_working_set' `
                            ([double]$soak.peakWorkingSetBytes) ([double]$budget.workingSetBytes) 'bytes'))
                        if ($measure.GrowthEvaluated) {
                            $soakChecks.Add((New-C6PerfCheck 'soak_working_set_growth_percent' `
                                ([double]$measure.WorkingSetGrowthPercent) 15 'percent' $false))
                            $soakChecks.Add((New-C6PerfCheck 'soak_handle_growth' `
                                ([double]$measure.HandleGrowth) 50 'count' $false))
                        }

                        $soak.status = 'PASS'
                    }
                }
            }
        }
    }
    catch {
        $soak.status = 'FAIL'
        $soak.reason = $_.Exception.Message
        $soakChecks.Add((New-C6PerfCheck 'soak_probe_failure' 1 0 'count' $false))
    }
}

# Structural soak checks are not budget-gated: a single sample, a stalled
# cadence, a dialog, or an unresponsive process fails even without confirmed
# budgets. Growth/peak checks remain budget-gated.
foreach ($soakCheck in $soakChecks) {
    $checks.Add($soakCheck)
}

$soak.checks = [object[]]$soakChecks.ToArray()

# Acceptance shape is evaluated after the UI runs. Missing 5/10/20 checks are
# a hard failure in confirmed mode, not a silent skip.
$requiredWindowCounts = @(5, 10, 20)
$observedWindowCounts = @($windowRuns | ForEach-Object { [int]$_.count } | Sort-Object -Unique)
$missingWindowCounts = @($requiredWindowCounts | Where-Object { $observedWindowCounts -notcontains $_ })
$acceptanceFailures = New-Object System.Collections.Generic.List[string]
if ($Notes -lt 1000) {
    $acceptanceFailures.Add("Notes must be at least 1000 (received $Notes).")
}

if ($Visible -lt 20) {
    $acceptanceFailures.Add("Visible must be at least 20 (received $Visible).")
}

if ($StartupIterations -lt 10) {
    $acceptanceFailures.Add("StartupIterations must be at least 10 (received $StartupIterations).")
}

if ($UiLaunches -lt 10) {
    $acceptanceFailures.Add("UiLaunches must be at least 10 (received $UiLaunches).")
}

if ($SkipUi) {
    $acceptanceFailures.Add('SkipUi removed the required UI evidence.')
}

if ($totalSeededNotes -lt 1000) {
    $acceptanceFailures.Add("The seed contained $totalSeededNotes total notes, expected at least 1000.")
}

if ($activeNotes -lt 20) {
    $acceptanceFailures.Add("The seed contained $activeNotes active notes, expected at least 20.")
}

if ($missingWindowCounts.Count -gt 0) {
    $acceptanceFailures.Add(
        'The 5/10/20 window checks are incomplete; missing: ' + ($missingWindowCounts -join ', ') + '.')
}

$acceptance = [ordered]@{
    enforced = $budgetConfirmed
    notes = [ordered]@{ required = 1000; actual = $Notes; passed = ($Notes -ge 1000) }
    visibleWindows = [ordered]@{ required = 20; actual = $Visible; passed = ($Visible -ge 20) }
    startupIterations = [ordered]@{ required = 10; actual = $StartupIterations; passed = ($StartupIterations -ge 10) }
    uiLaunches = [ordered]@{ required = 10; actual = $UiLaunches; passed = ($UiLaunches -ge 10) }
    requiredWindowCounts = $requiredWindowCounts
    observedWindowCounts = $observedWindowCounts
    missingWindowCounts = $missingWindowCounts
    windowChecksPassed = ($missingWindowCounts.Count -eq 0)
    skipUi = [bool]$SkipUi
    seedTotalNoteCount = $totalSeededNotes
    seedActiveNoteCount = $activeNotes
    failures = @($acceptanceFailures)
    passed = ($acceptanceFailures.Count -eq 0)
}
if ($budgetConfirmed) {
    $checks.Add((New-C6PerfCheck 'acceptance_shape' $(if ($acceptance.passed) { 0 } else { 1 }) 0 'count' $false))
}

$checks.Add((New-C6PerfCheck 'ui_evidence_failures' $uiEvidenceFailureDetails.Count 0 'count' $false))
$checks.Add((New-C6PerfCheck 'ui_note_count_mismatch' $uiNoteCountMismatches 0 'count' $false))

foreach ($check in $checks) {
    Set-C6PerfCheckResult -Check $check -Confirmed $budgetConfirmed
}

$failedChecks = @($checks | Where-Object { $_.status -eq 'fail' })
$unconfirmedChecks = @($checks | Where-Object { $_.status -eq 'unconfirmed' })
$failedSoakChecks = @($soakChecks | Where-Object { $_.status -eq 'fail' })
if ($soak.status -eq 'PASS' -and $failedSoakChecks.Count -gt 0) {
    $soak.status = 'FAIL'
    $soak.reason = 'One or more soak checks failed: ' + (($failedSoakChecks | ForEach-Object { $_.name }) -join ', ')
}

$twoHourStatus = 'NOT RUN'
if ($DurationMinutes -ge 120) {
    if ($soak.status -eq 'FAIL' -or $failedSoakChecks.Count -gt 0) {
        $twoHourStatus = 'FAIL'
    }
    elseif ($soak.status -eq 'PASS') {
        if (-not $budgetConfirmed) {
            $twoHourStatus = 'UNCONFIRMED'
        }
        elseif (@($soakChecks | Where-Object { $_.status -eq 'unconfirmed' }).Count -gt 0) {
            $twoHourStatus = 'NOT RUN'
        }
        else {
            $twoHourStatus = 'PASS'
        }
    }
}

$hardFailureReasons = New-Object System.Collections.Generic.List[string]
if ([string](Get-C6ObjectProperty $service 'status' '') -ne 'ok') {
    $hardFailureReasons.Add('The harness perf scenario did not report status ok.')
}

if (-not $serviceStatusCouplingPassed) {
    $hardFailureReasons.Add(
        "The harness verdict did not match the script verdict (service performanceStatus='$actualServicePerformanceStatus', budgetStatus='$actualServiceBudgetStatus', passed=$servicePassed).")
}

if ($missingMetricNames.Count -gt 0 -or $errorMetricNames.Count -gt 0) {
    $hardFailureReasons.Add(
        'Required service metrics were missing or invalid: ' +
        (@($missingMetricNames + $errorMetricNames) -join ', ') + '.')
}

if ($restoreFailures.Count -gt 0 -or $missingRestoreSizes.Count -gt 0) {
    $hardFailureReasons.Add(
        'Backup/restore checks failed or were missing for: ' +
        (@(@($restoreFailures | ForEach-Object { [string]$_.noteCount }) + $missingRestoreSizes) -join ', ') + '.')
}

if ($uiStatus -eq 'FAIL') {
    $hardFailureReasons.Add('UI evidence failed: ' + $uiReason)
}

if ($budgetConfirmed -and -not $acceptance.passed) {
    $hardFailureReasons.Add('Acceptance shape failed: ' + ($acceptanceFailures -join ' '))
}

if ($soak.status -eq 'FAIL') {
    $hardFailureReasons.Add('Soak failed: ' + $soak.reason)
}

if ($failedChecks.Count -gt 0) {
    $hardFailureReasons.Add(
        'Failed checks: ' + (($failedChecks | ForEach-Object { $_.name }) -join ', ') + '.')
}

$overallStatus = 'PASS'
if ($hardFailureReasons.Count -gt 0) {
    $overallStatus = 'FAIL'
}
elseif (-not $budgetConfirmed) {
    $overallStatus = 'UNCONFIRMED'
}
elseif ($uiStatus -eq 'NOT RUN' -or $twoHourStatus -ne 'PASS') {
    $overallStatus = 'NOT RUN'
}

$uiReadyP95 = $null
$uiHostP95 = $null
if ($uiReadyValues.Count -gt 0) {
    $uiReadyP95 = Get-C6Percentile -Values $uiReadyValues.ToArray() -Percentile 95
}

if ($uiHostValues.Count -gt 0) {
    $uiHostP95 = Get-C6Percentile -Values $uiHostValues.ToArray() -Percentile 95
}

$uiSummary = [ordered]@{}
$uiSummary['status'] = $uiStatus
$uiSummary['reason'] = $uiReason
$uiSummary['noteCount'] = $activeNotes
$uiSummary['expectedNoteCount'] = $activeNotes
$uiSummary['launches'] = [object[]]$uiLaunchResults.ToArray()
$uiSummary['readyP95Ms'] = $uiReadyP95
$uiSummary['hostP95Ms'] = $uiHostP95
$uiSummary['windows'] = [object[]]$windowRuns.ToArray()
$uiSummary['showHide'] = $showHide
$uiSummary['evidenceFailures'] = [object[]]$uiEvidenceFailureDetails.ToArray()

$actual = [ordered]@{}
$actual['budgetStatus'] = $budgetStatus
$actual['budgetConfirmed'] = $budgetConfirmed
$actual['performanceStatus'] = $overallStatus
$actual['notes'] = $Notes
$actual['visibleWindows'] = $Visible
$actual['startupIterations'] = $StartupIterations
$actual['uiLaunches'] = $UiLaunches
$actual['seed'] = [ordered]@{
    requestedNotes = $Notes
    noteCount = $activeNotes
    totalNoteCount = $totalSeededNotes
}
$actual['acceptance'] = $acceptance
$actual['perf'] = $service
$actual['ui'] = $uiSummary
$actual['soak'] = $soak
$actual['twoHourSoak'] = [ordered]@{
    status = $twoHourStatus
    requiredMinutes = 120
    requiredSampleCount = $twoHourRequiredSampleCount
    minimumSampleCount = $twoHourMinimumSampleCount
    cadenceLimitSeconds = $twoHourCadenceLimitSeconds
    maximumWorkingSetGrowthPercent = 15
    maximumHandleGrowth = 50
    command = $twoHour.command
    commandShell = $twoHour.shell
    runId = $twoHour.runId
    evidenceRoot = $twoHour.evidenceRoot
    safety = 'The command uses a fresh RunId and sibling EvidenceRoot derived from this run, so it cannot overwrite the current evidence.'
}
$actual['serviceStatusCoupling'] = [ordered]@{
    passed = $serviceStatusCouplingPassed
    expectedPerformanceStatus = $expectedServicePerformanceStatus
    actualPerformanceStatus = $actualServicePerformanceStatus
    expectedBudgetStatus = $budgetStatus
    actualBudgetStatus = $actualServiceBudgetStatus
    servicePassed = $servicePassed
    missingMetrics = [object[]]$missingMetricNames
    errorMetrics = [object[]]$errorMetricNames
    missingBudgetChecks = [object[]]$missingServiceChecks
    mismatchedBudgetChecks = [object[]]$mismatchedServiceChecks.ToArray()
    restoreFailures = [object[]]@($restoreFailures | ForEach-Object { [int]$_.noteCount })
    missingRestoreSizes = [object[]]$missingRestoreSizes
}
$actual['checks'] = [object[]]$checks.ToArray()
$actual['serviceBudgetStatus'] = $actualServiceBudgetStatus
$actual['servicePerformanceStatus'] = $actualServicePerformanceStatus
$actual['unconfirmedChecks'] = $unconfirmedChecks.Count

$reason = $null
if ($overallStatus -eq 'FAIL') {
    $reason = ($hardFailureReasons | Select-Object -Unique) -join ' '
}
elseif ($overallStatus -eq 'UNCONFIRMED') {
    $reason = "C6_BUDGETS_CONFIRMED is not '1'; raw metrics are reported with budgetStatus=unconfirmed and no performance pass is claimed."
}
elseif ($overallStatus -eq 'NOT RUN') {
    $reason = 'The 2-hour soak (or a required UI metric) is NOT RUN; rerun with -DurationMinutes 120.'
}

$evidence = @(
    $perfOutput,
    (Join-Path $rawDirectory 'ui-startup-01.json')
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }
$evidence += @(Get-ChildItem -LiteralPath $rawDirectory -Filter 'ui-*.json' -File |
    ForEach-Object { $_.FullName })
$evidence = @($evidence | Select-Object -Unique)
foreach ($path in @(
    (Join-Path $rawDirectory 'soak.json'),
    (Join-Path $rawDirectory 'soak-samples.jsonl'))) {
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $evidence += $path
    }
}

$resultPath = Write-C6CaseResult -RunId $RunId -Case 'C6-PERF' -Status $overallStatus `
    -Scenario 'perf' `
    -Expected 'Startup/save/input/window/show-hide/soak metrics are collected against synthetic data; confirmed runs require 1,000 notes, 20 windows, 10 startup iterations, 10 UI launches, 5/10/20 window checks, and a hardened 2-hour soak; budgets only apply when C6_BUDGETS_CONFIRMED=1.' `
    -Actual $actual -Restart $null -Evidence $evidence -Cleanup $cleanup -Reason $reason `
    -EvidenceRoot $evidenceRoot

if ($overallStatus -eq 'FAIL' -or $overallStatus -eq 'NOT RUN') {
    exit 1
}

exit 0
