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

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $Default
    }

    return $property.Value
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
        [string]$EvidenceRoot
    )

    $runRoot = Get-C6RunRoot -RunId $RunId
    if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
        $repoRoot = [System.IO.Path]::GetFullPath((Join-Path $HandoffRoot '..'))
        $EvidenceRoot = Join-Path $repoRoot (
            '.superpowers\sdd\2026-10-30-c6-fault-performance-stability\task-2-evidence\' + $RunId)
    }

    $full = [System.IO.Path]::GetFullPath($EvidenceRoot)
    if (Test-C6PathUnder -Path $full -Root $runRoot) {
        throw "EvidenceRoot must be outside the disposable C6 run root. Received '$full'."
    }

    [System.IO.Directory]::CreateDirectory($full) | Out-Null
    return $full
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
