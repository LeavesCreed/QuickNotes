#requires -Version 5.1
<#
.SYNOPSIS
Restores any C6 ACL backup and removes only the C6 run root for this RunId.

This is the final cleanup entry point used by the C6 fault driver. It never
deletes %TEMP%\QuickNotes-C6 itself, never follows a reparse point, and
refuses to continue if an ACL cannot be restored or a reparse point is not a
positively verified C6 mount. A hard-killed run that died after creating the
exact '<RunId>.vhdx' but before writing metadata is tolerated; no other volume
is ever touched.
#>
param(
    [string]$RunId
)

. (Join-Path $PSScriptRoot 'C6ScriptCommon.ps1')

function Write-C6ClearResult {
    param([string]$Status, [int]$ExitCode, [string]$Reason, $Detail)
    $payload = [ordered]@{
        status = $Status
        runId = $RunId
        createdByC6 = ($Status -eq 'PASS')
        reason = $Reason
        detail = $Detail
    }
    Write-Output ($payload | ConvertTo-Json -Depth 8 -Compress)
    exit $ExitCode
}

function Get-C6RecordedMountPath {
    param(
        [string]$MetadataPath,
        [string]$RunId,
        [string]$RunRoot,
        [string]$Root
    )

    $metadata = Read-C6JsonFile -Path $MetadataPath
    if ($null -eq $metadata -or $metadata.createdByC6 -ne $true) {
        return $null
    }

    if (-not [string]::Equals([string]$metadata.runId, $RunId, [System.StringComparison]::Ordinal)) {
        return $null
    }

    try {
        $vhdx = [System.IO.Path]::GetFullPath([string]$metadata.vhdxPath)
        $mount = [System.IO.Path]::GetFullPath([string]$metadata.mountPath)
    }
    catch {
        return $null
    }

    if (-not [string]::Equals(
            [System.IO.Path]::GetFileName($vhdx),
            ($RunId + '.vhdx'),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        return $null
    }

    if (-not (Test-C6PathUnder -Path $vhdx -Root $Root) -or
        -not (Test-C6PathUnder -Path $mount -Root $RunRoot)) {
        return $null
    }

    return $mount
}

function Get-C6PartialMountOwnership {
    param([string]$Vhdx, [string]$MountPath)

    try {
        $image = Get-DiskImage -ImagePath $Vhdx -ErrorAction Stop
        if ($null -eq $image) {
            return $null
        }

        $disk = $image | Get-Disk -ErrorAction Stop
        if ($null -eq $disk) {
            return $null
        }

        $partitions = @(Get-Partition -DiskNumber $disk.Number -ErrorAction Stop)
        $mounted = $null
        foreach ($partition in $partitions) {
            $resolved = Get-Volume -FilePath $MountPath -ErrorAction Stop
            if ($null -eq $resolved) {
                continue
            }

            $resolvedPartitions = @(Get-Partition -Volume $resolved -ErrorAction Stop)
            if ($resolvedPartitions.Count -ne 1) {
                continue
            }

            if ([int]$resolvedPartitions[0].DiskNumber -eq [int]$disk.Number -and
                [int]$resolvedPartitions[0].PartitionNumber -eq [int]$partition.PartitionNumber) {
                $mounted = [pscustomobject]@{
                    DiskNumber = $disk.Number
                    PartitionNumber = $partition.PartitionNumber
                    VolumeLabel = [string]$resolved.FileSystemLabel
                    VolumePath = [string]$resolved.Path
                }
                break
            }
        }

        return $mounted
    }
    catch {
        return $null
    }
}

function Remove-C6PartialVolume {
    param(
        [string]$Vhdx,
        [string]$MountPath,
        $Detail
    )

    if (Test-Path -LiteralPath $MountPath -PathType Container) {
        $ownership = Get-C6PartialMountOwnership -Vhdx $Vhdx -MountPath $MountPath
        if ($null -eq $ownership) {
            throw ("A mount path exists without metadata and cannot be positively tied to the exact C6 VHDX '$Vhdx'; refusing to remove it.")
        }

        $Detail.partialMountVerified = $true
        $Detail.partialVolumeLabel = $ownership.VolumeLabel
    }

    if (Test-C6IsReparsePoint -Path $Vhdx) {
        throw 'The partial C6 VHDX path is a reparse point; refusing to delete it.'
    }

    try {
        $image = Get-DiskImage -ImagePath $Vhdx -ErrorAction Stop
    }
    catch {
        $image = $null
    }

    if ($null -ne $image -and $image.Attached) {
        Dismount-DiskImage -ImagePath $Vhdx -ErrorAction Stop | Out-Null
        $Detail.partialDismounted = $true
    }

    if (Test-Path -LiteralPath $MountPath -PathType Container) {
        Remove-C6MountPathSafely -MountPath $MountPath
        $Detail.partialMountRemoved = $true
    }

    try {
        $stream = [System.IO.File]::Open(
            $Vhdx,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None)
        $stream.Dispose()
    }
    catch {
        throw ("The partial C6 VHDX '$Vhdx' is still in use and cannot be proven detached: $($_.Exception.Message)")
    }

    Remove-Item -LiteralPath $Vhdx -Force
    $Detail.partialVhdxRemoved = $true
}

try {
    Assert-C6RunId -RunId $RunId
}
catch {
    Write-C6ClearResult -Status 'REFUSED' -ExitCode 2 -Reason $_.Exception.Message -Detail $null
}

$root = Get-C6Root
$runRoot = Get-C6RunRoot -RunId $RunId
Assert-C6PathUnder -Path $runRoot -Root $root -Description 'C6 run root'

if (-not (Test-Path -LiteralPath $runRoot -PathType Container)) {
    Write-C6ClearResult -Status 'PASS' -ExitCode 0 -Reason 'The C6 run root does not exist; nothing to remove.' -Detail $null
}

try {
    $item = Get-Item -LiteralPath $runRoot -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        Write-C6ClearResult -Status 'REFUSED' -ExitCode 4 -Reason 'The C6 run root is a reparse point; refusing to traverse it.' -Detail $null
    }

    $detail = [ordered]@{
        aclRestored = 0
        aclSddlVerified = 0
        volumeRemoved = $false
        partialVhdxRemoved = $false
        partialMountVerified = $false
        partialMountRemoved = $false
        reparsePointsChecked = 0
        runRootRemoved = $false
    }

    $metadataPath = Join-Path $root ($RunId + '.volume.json')
    $vhdx = Join-Path $root ($RunId + '.vhdx')
    $mountPath = Join-Path $runRoot 'volume'
    $allowedMountPath = $null
    $hasMetadata = Test-Path -LiteralPath $metadataPath -PathType Leaf
    $hasPartialVhdx = (-not $hasMetadata) -and (Test-Path -LiteralPath $vhdx -PathType Leaf)

    if ($hasMetadata) {
        $allowedMountPath = Get-C6RecordedMountPath -MetadataPath $metadataPath -RunId $RunId -RunRoot $runRoot -Root $root
    }
    elseif ($hasPartialVhdx -and (Test-Path -LiteralPath $mountPath -PathType Container)) {
        # Verify the directory mount belongs to the exact C6 VHDX before it is
        # allowed through the reparse-point gate or removed.
        $ownership = Get-C6PartialMountOwnership -Vhdx $vhdx -MountPath $mountPath
        if ($null -eq $ownership) {
            Write-C6ClearResult -Status 'REFUSED' -ExitCode 4 -Reason 'A partial mount exists but cannot be positively tied to the exact C6 VHDX; refusing to remove it.' -Detail $detail
        }

        $allowedMountPath = $mountPath
        $detail.partialMountVerified = $true
        $detail.partialVolumeLabel = $ownership.VolumeLabel
    }

    # ACL backups are written only at the run root; never recurse before the
    # reparse-point audit.
    $aclBackups = @(Get-ChildItem -LiteralPath $runRoot -Filter 'acl-*.json' -File -Force -ErrorAction SilentlyContinue)
    foreach ($backup in $aclBackups) {
        $state = Read-C6JsonFile -Path $backup.FullName
        if ($null -eq $state -or $state.createdByC6 -ne $true) {
            Write-C6ClearResult -Status 'FAIL' -ExitCode 5 -Reason "An ACL backup under the run root is not tagged CreatedByC6: $($backup.Name)." -Detail $detail
        }

        $target = [System.IO.Path]::GetFullPath([string]$state.target)
        Assert-C6PathUnder -Path $target -Root $runRoot -Description 'ACL target from backup'
        if (Test-Path -LiteralPath $target -PathType Container) {
            try {
                $acl = Get-Acl -LiteralPath $target
                $acl.SetSecurityDescriptorSddlForm([string]$state.sddl)
                Set-Acl -LiteralPath $target -AclObject $acl
                $restoredSddl = (Get-Acl -LiteralPath $target).Sddl
            }
            catch {
                Write-C6ClearResult -Status 'FAIL' -ExitCode 5 -Reason "ACL restoration failed for a C6 directory. Repair with icacls or Set-Acl from the recorded SDDL before deleting: $($_.Exception.Message)" -Detail $detail
            }

            if (-not [string]::Equals(
                    [string]$restoredSddl,
                    [string]$state.sddl,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                Write-C6ClearResult -Status 'FAIL' -ExitCode 5 -Reason 'The restored SDDL does not match the recorded original; refusing to delete the run root.' -Detail $detail
            }

            if (-not (Test-C6WriteAccess -Directory $target)) {
                Write-C6ClearResult -Status 'FAIL' -ExitCode 5 -Reason 'ACL restoration was applied but the write probe still fails. Repair with icacls before deleting.' -Detail $detail
            }

            $detail.aclSddlVerified = [int]$detail.aclSddlVerified + 1
        }

        Remove-Item -LiteralPath $backup.FullName -Force
        $detail.aclRestored = [int]$detail.aclRestored + 1
    }

    $reparsePoints = @(Get-C6ReparsePoints -Root $runRoot)
    $detail.reparsePointsChecked = $reparsePoints.Count
    foreach ($point in $reparsePoints) {
        if ([string]::IsNullOrWhiteSpace($allowedMountPath) -or
            -not [string]::Equals($point, $allowedMountPath, [System.StringComparison]::OrdinalIgnoreCase)) {
            Write-C6ClearResult -Status 'REFUSED' -ExitCode 4 -Reason "A reparse point under the run root is not the recorded C6 mount path: $point" -Detail $detail
        }
    }

    if ($hasMetadata) {
        $removeOutput = @(& (Join-Path $PSScriptRoot 'Remove-C6TestVolume.ps1') -RunId $RunId 2>&1 |
            ForEach-Object { $_.ToString() })
        $removeExit = $LASTEXITCODE
        if ($removeExit -ne 0) {
            Write-C6ClearResult -Status 'FAIL' -ExitCode 5 -Reason "Remove-C6TestVolume refused or failed; the run root was kept for diagnosis. $($removeOutput -join ' ')" -Detail $detail
        }

        $detail.volumeRemoved = $true
    }
    elseif ($hasPartialVhdx) {
        Remove-C6PartialVolume -Vhdx $vhdx -MountPath $mountPath -Detail $detail
    }

    $remainingReparsePoints = @(Get-C6ReparsePoints -Root $runRoot)
    if ($remainingReparsePoints.Count -gt 0) {
        Write-C6ClearResult -Status 'FAIL' -ExitCode 5 -Reason ("Reparse points remain under the run root after volume cleanup: " + ($remainingReparsePoints -join ', ')) -Detail $detail
    }

    Remove-Item -LiteralPath $runRoot -Recurse -Force
    $detail.runRootRemoved = $true
    if (Test-Path -LiteralPath $runRoot) {
        Write-C6ClearResult -Status 'FAIL' -ExitCode 5 -Reason 'The C6 run root still exists after cleanup.' -Detail $detail
    }

    Write-C6ClearResult -Status 'PASS' -ExitCode 0 -Reason 'The C6 run root and its tagged resources were removed; retained evidence is outside the run root.' -Detail $detail
}
catch {
    Write-C6ClearResult -Status 'FAIL' -ExitCode 5 -Reason $_.Exception.Message -Detail $null
}
