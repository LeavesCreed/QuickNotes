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
budgetStatus=unconfirmed and never claims a performance pass. A short
-DurationMinutes run is a smoke only; the 2-hour soak remains NOT RUN until
the exact command printed in the result is executed.

Examples:
  $env:C6_BUDGETS_CONFIRMED='1'
  pwsh -File scripts/c6/Invoke-C6Performance.ps1 -RunId c6-task5-smoke -Notes 1000 -Visible 20 -DurationMinutes 3 -EvidenceRoot <dir>
  pwsh -File scripts/c6/Invoke-C6Performance.ps1 -RunId c6-task5-2h -Notes 1000 -Visible 20 -DurationMinutes 120 -EvidenceRoot <dir>

Exit 0 only when the requested evidence is PASS or explicitly UNCONFIRMED.
Exit 1 when any check FAILs or a required item (including the 2-hour soak) is
NOT RUN.
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
    param([string]$Name, [double]$Actual, [double]$Limit, [string]$Unit)

    return [ordered]@{
        name = $Name
        actual = $Actual
        limit = $Limit
        unit = $Unit
        status = 'unconfirmed'
        passed = $false
    }
}

function Set-C6PerfCheckResult {
    param($Check, [bool]$Confirmed)

    if (-not $Confirmed) {
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

$budgetConfirmed = [string]$env:C6_BUDGETS_CONFIRMED -eq '1'
$budgetStatus = if ($budgetConfirmed) { 'confirmed' } else { 'unconfirmed' }

# Build both test-only executables and seed the synthetic 1,000-note history.
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
    $p95 = [double]$metric.p95Ms
    $p99 = [double]$metric.p99Ms
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

# WPF startup x10, 5/10/20-window resources, and repeated show/hide.
$uiStatus = 'NOT RUN'
$uiReason = $null
$uiLaunchResults = New-Object System.Collections.Generic.List[object]
$windowRuns = New-Object System.Collections.Generic.List[object]
$showHide = $null
$uiReadyValues = New-Object System.Collections.Generic.List[double]
$uiHostValues = New-Object System.Collections.Generic.List[double]
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

        $uiReadyValues.Add([double]$launch.Json.readyMs)
        $uiHostValues.Add([double]$launch.Json.hostStartMs)
        $uiLaunchResults.Add([ordered]@{
            launch = $index
            readyMs = [double]$launch.Json.readyMs
            hostStartMs = [double]$launch.Json.hostStartMs
            windowCount = [int]$launch.Json.windowCount
            workingSetBytes = [int64]$launch.Json.workingSetBytes
            handleCount = [int]$launch.Json.handleCount
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
                break
            }

            if ($launch.ExitCode -ne 0 -or [string](Get-C6ObjectProperty $launch.Json 'status' '') -ne 'ok') {
                $uiStatus = 'FAIL'
                $uiReason = "UI window run $count exited $($launch.ExitCode)."
                break
            }

            $windowRuns.Add([ordered]@{
                count = $count
                readyMs = [double]$launch.Json.readyMs
                workingSetBytes = [int64]$launch.Json.workingSetBytes
                privateBytes = [int64]$launch.Json.privateBytes
                handleCount = [int]$launch.Json.handleCount
                threadCount = [int]$launch.Json.threadCount
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
            }
            else {
                $showHide = [ordered]@{
                    cycles = [int]$launch.Json.showHideCycles
                    p50Ms = [double]$launch.Json.showHideP50Ms
                    p95Ms = [double]$launch.Json.showHideP95Ms
                    maxMs = [double]$launch.Json.showHideMaxMs
                }
            }
        }
        catch {
            $uiStatus = 'FAIL'
            $uiReason = $_.Exception.Message
        }
    }
}

# Optional soak. Any duration shorter than 120 minutes is explicitly a smoke.
$soak = [ordered]@{
    status = 'NOT RUN'
    requestedMinutes = $DurationMinutes
    sampleSeconds = $SoakSampleSeconds
    reason = $null
    command = $null
    peakWorkingSetBytes = $null
    peakHandleCount = $null
    firstWorkingSetBytes = $null
    lastWorkingSetBytes = $null
    firstHandleCount = $null
    lastHandleCount = $null
    sampleCount = 0
    completed = $false
}
$twoHourCommand = 'pwsh -File scripts/c6/Invoke-C6Performance.ps1 -RunId ' + $RunId + `
    ' -Notes ' + $Notes + ' -Visible ' + $Visible + ' -UiLaunches ' + $UiLaunches + `
    ' -DurationMinutes 120 -EvidenceRoot "' + $evidenceRoot + '"'
$soak.command = $twoHourCommand
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
                '--visible', [string]([Math]::Min(20, $Notes)),
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
        }
        else {
            $soakEvidence = Get-C6ObjectProperty $launch.Json 'soak' $null
            if ($null -eq $soakEvidence) {
                $soak.status = 'FAIL'
                $soak.reason = 'The soak result contained no sample evidence.'
            }
            else {
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
                elseif ($soak.sampleCount -lt 1) {
                    $soak.status = 'FAIL'
                    $soak.reason = 'The soak produced no process resource samples.'
                }
                else {
                    $soak.status = 'PASS'
                }

                if ($soak.status -eq 'PASS') {
                    $checks.Add((New-C6PerfCheck 'soak_peak_working_set' `
                        ([double]$soak.peakWorkingSetBytes) ([double]$budget.workingSetBytes) 'bytes'))
                }
            }
        }
    }
    catch {
        $soak.status = 'FAIL'
        $soak.reason = $_.Exception.Message
    }
}

$twoHourStatus = if ($DurationMinutes -ge 120 -and $soak.status -eq 'PASS') { 'PASS' } else { 'NOT RUN' }

foreach ($check in $checks) {
    Set-C6PerfCheckResult -Check $check -Confirmed $budgetConfirmed
}

$failedChecks = @($checks | Where-Object { $_.status -eq 'fail' })
$unconfirmedChecks = @($checks | Where-Object { $_.status -eq 'unconfirmed' })
$overallStatus = 'PASS'
if ([string](Get-C6ObjectProperty $service 'status' '') -ne 'ok' -or $uiStatus -eq 'FAIL' -or $soak.status -eq 'FAIL' -or $failedChecks.Count -gt 0) {
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
$uiSummary['launches'] = [object[]]$uiLaunchResults.ToArray()
$uiSummary['readyP95Ms'] = $uiReadyP95
$uiSummary['hostP95Ms'] = $uiHostP95
$uiSummary['windows'] = [object[]]$windowRuns.ToArray()
$uiSummary['showHide'] = $showHide

$actual = [ordered]@{}
$actual['budgetStatus'] = $budgetStatus
$actual['budgetConfirmed'] = $budgetConfirmed
$actual['performanceStatus'] = $overallStatus
$actual['notes'] = $Notes
$actual['visibleWindows'] = $Visible
$actual['startupIterations'] = $StartupIterations
$actual['perf'] = $service
$actual['ui'] = $uiSummary
$actual['soak'] = $soak
$actual['twoHourSoak'] = [ordered]@{
    status = $twoHourStatus
    requiredMinutes = 120
    command = $twoHourCommand
}
$actual['checks'] = [object[]]$checks.ToArray()
$actual['serviceBudgetStatus'] = [string](Get-C6ObjectProperty $service 'budgetStatus' '')
$actual['servicePerformanceStatus'] = [string](Get-C6ObjectProperty $service 'performanceStatus' '')
$actual['unconfirmedChecks'] = $unconfirmedChecks.Count

$reason = $null
if ($overallStatus -eq 'FAIL') {
    $reason = if ($uiReason) { $uiReason } else { 'One or more performance checks failed or an evidence process failed.' }
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

$cleanup = [ordered]@{
    runRootRetained = $true
    cleanupCommand = ('pwsh -File scripts/c6/Clear-C6Artifacts.ps1 -RunId ' + $RunId)
}

$resultPath = Write-C6CaseResult -RunId $RunId -Case 'C6-PERF' -Status $overallStatus `
    -Scenario 'perf' `
    -Expected 'Startup/save/input/window/show-hide/soak metrics are collected against synthetic data; budgets only apply when C6_BUDGETS_CONFIRMED=1; the 2-hour soak is a separate, explicitly reported item.' `
    -Actual $actual -Restart $null -Evidence $evidence -Cleanup $cleanup -Reason $reason `
    -EvidenceRoot $evidenceRoot

if ($overallStatus -eq 'FAIL' -or $overallStatus -eq 'NOT RUN') {
    exit 1
}

exit 0
