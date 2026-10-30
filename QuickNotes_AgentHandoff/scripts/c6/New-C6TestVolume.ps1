#requires -Version 5.1
<#
.SYNOPSIS
Creates a disposable, dynamically expanding VHDX under %TEMP%\QuickNotes-C6,
formats it, and mounts it on an empty directory inside the same run root.

The script refuses to reuse an existing VHDX, refuses unsafe sizes, refuses
drive letters, and never mounts or formats an existing volume. Creation
requires an elevated PowerShell session with the Hyper-V New-VHD cmdlet.
#>
param(
    [int]$SizeMB = 1024,
    [string]$RunId,
    [switch]$ValidateOnly
)

. (Join-Path $PSScriptRoot 'C6ScriptCommon.ps1')

$root = Get-C6Root
$vhdx = $null
$mountPath = $null
$metadataPath = $null
$createdByThisInvocation = $false
$mounted = $false

function Write-C6VolumeError {
    param([string]$Reason, [int]$ExitCode)
    $payload = [ordered]@{
        status = 'NOT RUN'
        runId = $RunId
        createdByC6 = $false
        reason = $Reason
    }
    Write-Output ($payload | ConvertTo-Json -Compress)
    exit $ExitCode
}

try {
    Assert-C6RunId -RunId $RunId
    if ($SizeMB -lt 256 -or $SizeMB -gt 4096) {
        Write-C6VolumeError -Reason 'SizeMB must be between 256 and 4096.' -ExitCode 2
    }

    $runRoot = Get-C6RunRoot -RunId $RunId
    $vhdx = Join-Path $root ($RunId + '.vhdx')
    $mountPath = Join-Path $runRoot 'volume'
    $metadataPath = Join-Path $root ($RunId + '.volume.json')

    Assert-C6PathUnder -Path $vhdx -Root $root -Description 'VHDX path'
    Assert-C6PathUnder -Path $mountPath -Root $runRoot -Description 'Mount path'

    $systemDrive = [System.IO.Path]::GetPathRoot(
        [System.IO.Path]::GetFullPath($env:SystemRoot)).TrimEnd('\')

    if (Test-Path -LiteralPath $vhdx -PathType Leaf) {
        Write-C6VolumeError -Reason 'A C6 VHDX already exists for this RunId; refusing to reuse it.' -ExitCode 3
    }

    if (Test-Path -LiteralPath $metadataPath -PathType Leaf) {
        Write-C6VolumeError -Reason 'C6 volume metadata already exists for this RunId; run Remove-C6TestVolume first.' -ExitCode 3
    }

    $newVhd = Get-Command New-VHD -ErrorAction SilentlyContinue
    $readyReasons = @()
    if (-not (Test-C6Elevated)) {
        $readyReasons += 'the current PowerShell session is not elevated'
    }

    if ($null -eq $newVhd) {
        $readyReasons += 'New-VHD is unavailable (Hyper-V PowerShell module is not installed)'
    }

    $readyReason = $null
    if ($readyReasons.Count -gt 0) {
        $readyReason = 'Creating and mounting a VHDX requires elevation and the Hyper-V New-VHD cmdlet: ' + ($readyReasons -join '; ') + '.'
    }

    if ($ValidateOnly) {
        if ($null -ne $readyReason) {
            Write-C6VolumeError -Reason $readyReason -ExitCode 3
        }

        $payload = [ordered]@{
            status = 'READY'
            runId = $RunId
            createdByC6 = $false
            reason = 'VHDX creation prerequisites are available; ValidateOnly did not create anything.'
        }
        Write-Output ($payload | ConvertTo-Json -Compress)
        exit 0
    }

    if ($null -ne $readyReason) {
        Write-C6VolumeError -Reason $readyReason -ExitCode 3
    }

    [System.IO.Directory]::CreateDirectory($runRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory($mountPath) | Out-Null

    New-VHD -Path $vhdx -SizeBytes ([int64]$SizeMB * 1MB) -Dynamic | Out-Null
    if (-not (Test-Path -LiteralPath $vhdx -PathType Leaf)) {
        throw 'New-VHD did not create the C6 VHDX.'
    }

    $createdByThisInvocation = $true
    $image = Mount-DiskImage -ImagePath $vhdx -PassThru
    if ($null -eq $image) {
        throw 'Mount-DiskImage did not return an image.'
    }

    $mounted = $true
    $disk = Get-DiskImage -ImagePath $vhdx | Get-Disk
    if ($null -eq $disk) {
        throw 'The mounted C6 image did not expose a disk.'
    }

    Initialize-Disk -Number $disk.Number -PartitionStyle GPT -Confirm:$false | Out-Null
    $partition = New-Partition -DiskNumber $disk.Number -UseMaximumSize -AssignDriveLetter:$false
    $volume = Format-Volume -Partition $partition -FileSystem NTFS -NewFileSystemLabel ('C6-' + $RunId) -Confirm:$false
    if ($null -eq $volume) {
        throw 'Format-Volume did not return a volume.'
    }

    Add-PartitionAccessPath -DiskNumber $disk.Number -PartitionNumber $partition.PartitionNumber -AccessPath $mountPath
    Start-Sleep -Milliseconds 300

    $partition = Get-Partition -DiskNumber $disk.Number -PartitionNumber $partition.PartitionNumber
    $systemDriveLetter = $systemDrive.Substring(0, 1)
    if ($partition.DriveLetter -and
        ([string]$partition.DriveLetter).TrimEnd(':') -ieq $systemDriveLetter) {
        throw 'The C6 volume unexpectedly received the system drive letter.'
    }

    $volumeId = [string]$partition.Guid
    if ([string]::IsNullOrWhiteSpace($volumeId)) {
        $volumeId = [string]$volume.UniqueId
    }

    $volumeLabel = 'C6-' + $RunId

    # Positive mount guard: resolve the directory mount back to a volume and
    # partition, and require it to be the just-created C6 disk. A path that
    # merely exists (for example on C:) never satisfies this check.
    $mountVerification = Get-C6MountVerification `
        -MountPath $mountPath `
        -RunRoot $runRoot `
        -SystemRoot $env:SystemRoot `
        -ExpectedDiskNumber $disk.Number `
        -ExpectedPartitionNumber $partition.PartitionNumber `
        -ExpectedVolumeId $volumeId `
        -ExpectedLabel $volumeLabel
    if (-not $mountVerification.Passed) {
        throw ('The C6 mount could not be positively verified as the just-created volume: ' +
            (@($mountVerification.Failures) -join '; '))
    }

    $metadata = [ordered]@{
        runId = $RunId
        createdByC6 = $true
        vhdxPath = $vhdx
        mountPath = $mountPath
        volumeLabel = $volumeLabel
        volumeId = $volumeId
        diskNumber = $disk.Number
        partitionNumber = $partition.PartitionNumber
        mountVerified = $true
        mountVerification = $mountVerification.Observed
        sizeMB = $SizeMB
        createdAtUtc = (Get-Date).ToUniversalTime().ToString('O')
    }
    Write-C6JsonFile -Path $metadataPath -Object $metadata

    $payload = [ordered]@{
        status = 'PASS'
        runId = $RunId
        mountPath = $mountPath
        volumeId = $volumeId
        volumeLabel = $volumeLabel
        diskNumber = $disk.Number
        partitionNumber = $partition.PartitionNumber
        mountVerified = $true
        mountVerification = $mountVerification.Observed
        createdByC6 = $true
    }
    Write-Output ($payload | ConvertTo-Json -Compress)
    exit 0
}
catch {
    $reason = $_.Exception.Message
    try {
        if ($mounted -and $null -ne $vhdx -and (Test-Path -LiteralPath $vhdx -PathType Leaf)) {
            Dismount-DiskImage -ImagePath $vhdx -ErrorAction SilentlyContinue | Out-Null
        }
    }
    catch {
    }

    try {
        if ($createdByThisInvocation -and $null -ne $mountPath -and (Test-Path -LiteralPath $mountPath -PathType Container)) {
            Assert-C6PathUnder -Path $mountPath -Root (Get-C6RunRoot -RunId $RunId) -Description 'Partial mount path'
            Remove-C6MountPathSafely -MountPath $mountPath
        }
    }
    catch {
    }

    try {
        if ($createdByThisInvocation -and $null -ne $vhdx -and (Test-Path -LiteralPath $vhdx -PathType Leaf)) {
            Assert-C6PathUnder -Path $vhdx -Root $root -Description 'Partial VHDX path'
            if (-not (Test-C6IsReparsePoint -Path $vhdx)) {
                Remove-Item -LiteralPath $vhdx -Force -ErrorAction SilentlyContinue
            }
        }
    }
    catch {
    }

    try {
        if ($null -ne $metadataPath -and (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
            Remove-Item -LiteralPath $metadataPath -Force -ErrorAction SilentlyContinue
        }
    }
    catch {
    }

    Write-C6VolumeError -Reason $reason -ExitCode 3
}
