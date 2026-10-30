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
    $state = [ordered]@{
        case = $Case
        target = $Target
        sddl = $acl.Sddl
        user = Get-C6CurrentUser
        createdByC6 = $true
        appliedAtUtc = (Get-Date).ToUniversalTime().ToString('O')
    }
    $backupPath = Join-Path $RunRoot ("acl-{0}.json" -f $Case)
    Write-C6JsonFile -Path $backupPath -Object $state
    $stateObject = [pscustomobject]@{
        Target = $Target
        Sddl = $state.sddl
        User = $state.user
        BackupPath = $backupPath
    }

    # Deny file-creation and directory-creation only. Denying all write access
    # on a directory also blocks some directory handle opens on Windows, which
    # would make the failure unobservable instead of testing a write failure.
    & icacls.exe $Target /deny "$($state.user):(OI)(CI)(WD,AD)" | Out-Null
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

    return $stateObject
}

function Restore-C6Acl {
    param($State)

    if ($null -eq $State) {
        return [pscustomobject]@{ Status = 'PASS'; Message = 'No ACL change was applied.' }
    }

    if (-not (Test-Path -LiteralPath $State.Target -PathType Container)) {
        if (Test-Path -LiteralPath $State.BackupPath -PathType Leaf) {
            Remove-Item -LiteralPath $State.BackupPath -Force
        }

        return [pscustomobject]@{ Status = 'PASS'; Message = 'ACL target no longer exists.' }
    }

    try {
        $acl = Get-Acl -LiteralPath $State.Target
        $acl.SetSecurityDescriptorSddlForm($State.Sddl)
        Set-Acl -LiteralPath $State.Target -AclObject $acl
    }
    catch {
        return [pscustomobject]@{
            Status = 'FAIL'
            Message = "ACL restoration failed: $($_.Exception.Message)"
            Repair = "Run: icacls `"$($State.Target)`" /remove:d `"$($State.User)`" ; if that fails, Set-Acl from SDDL $($State.Sddl)"
        }
    }

    if (-not (Test-C6WriteAccess -Directory $State.Target)) {
        return [pscustomobject]@{
            Status = 'FAIL'
            Message = 'The ACL was rewritten but the write probe still fails.'
            Repair = "Run: icacls `"$($State.Target)`" /remove:d `"$($State.User)`" ; if that fails, Set-Acl from SDDL $($State.Sddl)"
        }
    }

    if (Test-Path -LiteralPath $State.BackupPath -PathType Leaf) {
        Remove-Item -LiteralPath $State.BackupPath -Force
    }

    return [pscustomobject]@{ Status = 'PASS'; Message = 'The original SDDL was restored and verified.' }
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
        [string]$Reason
    )

    $runRoot = Get-C6RunRoot -RunId $RunId
    $resultsDirectory = Join-Path $runRoot 'results'
    [System.IO.Directory]::CreateDirectory($resultsDirectory) | Out-Null

    $relativeEvidence = @()
    foreach ($item in @($Evidence)) {
        if ([string]::IsNullOrWhiteSpace($item)) {
            continue
        }

        $full = [System.IO.Path]::GetFullPath((Join-Path $runRoot $item))
        Assert-C6PathUnder -Path $full -Root $runRoot -Description 'Evidence path'
        $relativeEvidence += $item
    }

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
        evidence = @($relativeEvidence)
        cleanup = $Cleanup
    }
    if (-not [string]::IsNullOrWhiteSpace($Reason)) {
        $result.reason = $Reason
    }

    $resultPath = Join-Path $resultsDirectory ("{0}.result.json" -f $Case)
    Write-C6JsonFile -Path $resultPath -Object $result
    $summary = [ordered]@{
        case = $Case
        status = $Status
        resultPath = ("results/{0}.result.json" -f $Case)
    }
    [Console]::Out.WriteLine(($summary | ConvertTo-Json -Compress))
    return $resultPath
}

function Save-C6HarnessEvidence {
    param(
        [string]$RunId,
        [string]$Name,
        $HarnessResult
    )

    $runRoot = Get-C6RunRoot -RunId $RunId
    $path = Join-Path $runRoot ("evidence\{0}.json" -f $Name)
    $evidence = [ordered]@{
        exitCode = $HarnessResult.ExitCode
        result = $HarnessResult.Json
    }
    Write-C6JsonFile -Path $path -Object $evidence
    return ("evidence/{0}.json" -f $Name)
}
