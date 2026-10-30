#requires -Version 5.1
<#
.SYNOPSIS
Dismounts and deletes only the C6 VHDX recorded for the supplied RunId.

The script refuses to act if the metadata is missing, CreatedByC6 is not
true, the RunId does not match, or any recorded path is outside
%TEMP%\QuickNotes-C6. It tolerates a partially created/dismounted volume.
#>
param(
    [string]$RunId
)

. (Join-Path $PSScriptRoot 'C6ScriptCommon.ps1')

function Write-C6RemoveResult {
    param([string]$Status, [int]$ExitCode, [string]$Reason, $Removed)
    $payload = [ordered]@{
        status = $Status
        runId = $RunId
        createdByC6 = ($Status -eq 'PASS')
        reason = $Reason
        removed = $Removed
    }
    Write-Output ($payload | ConvertTo-Json -Depth 6 -Compress)
    exit $ExitCode
}

try {
    Assert-C6RunId -RunId $RunId
}
catch {
    Write-C6RemoveResult -Status 'REFUSED' -ExitCode 2 -Reason $_.Exception.Message -Removed $null
}

$root = Get-C6Root
$runRoot = Get-C6RunRoot -RunId $RunId
$metadataPath = Join-Path $root ($RunId + '.volume.json')
if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
    Write-C6RemoveResult -Status 'REFUSED' -ExitCode 4 -Reason 'No C6 volume metadata exists for this RunId.' -Removed $null
}

try {
    $metadata = Read-C6JsonFile -Path $metadataPath
    if ($null -eq $metadata) {
        Write-C6RemoveResult -Status 'REFUSED' -ExitCode 4 -Reason 'The C6 volume metadata is empty or invalid.' -Removed $null
    }

    if ($metadata.createdByC6 -ne $true) {
        Write-C6RemoveResult -Status 'REFUSED' -ExitCode 4 -Reason 'Refusing: CreatedByC6 is not true.' -Removed $null
    }

    if (-not [string]::Equals([string]$metadata.runId, $RunId, [System.StringComparison]::Ordinal)) {
        Write-C6RemoveResult -Status 'REFUSED' -ExitCode 4 -Reason 'Refusing: metadata RunId does not match.' -Removed $null
    }

    $vhdx = [System.IO.Path]::GetFullPath([string]$metadata.vhdxPath)
    $mountPath = [System.IO.Path]::GetFullPath([string]$metadata.mountPath)
    Assert-C6PathUnder -Path $vhdx -Root $root -Description 'Recorded VHDX path'
    Assert-C6PathUnder -Path $mountPath -Root $runRoot -Description 'Recorded mount path'
    if (-not [string]::Equals(
            [System.IO.Path]::GetFileName($vhdx),
            ($RunId + '.vhdx'),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        Write-C6RemoveResult -Status 'REFUSED' -ExitCode 4 -Reason 'Refusing: VHDX name does not match the RunId.' -Removed $null
    }

    $removed = [ordered]@{
        dismounted = $false
        mountDirectoryRemoved = $false
        vhdxRemoved = $false
        metadataRemoved = $false
    }

    if (Test-Path -LiteralPath $vhdx -PathType Leaf) {
        try {
            $image = Get-DiskImage -ImagePath $vhdx -ErrorAction Stop
        }
        catch {
            $image = $null
        }

        if ($null -ne $image -and $image.Attached) {
            Dismount-DiskImage -ImagePath $vhdx -ErrorAction Stop | Out-Null
            $removed.dismounted = $true
        }
    }

    if (Test-Path -LiteralPath $mountPath -PathType Container) {
        Remove-Item -LiteralPath $mountPath -Recurse -Force
        $removed.mountDirectoryRemoved = $true
    }

    if (Test-Path -LiteralPath $vhdx -PathType Leaf) {
        try {
            $image = Get-DiskImage -ImagePath $vhdx -ErrorAction Stop
        }
        catch {
            $image = $null
        }

        if ($null -ne $image -and $image.Attached) {
            throw 'The C6 VHDX is still attached; refusing to delete it.'
        }

        Remove-Item -LiteralPath $vhdx -Force
        $removed.vhdxRemoved = $true
    }

    Remove-Item -LiteralPath $metadataPath -Force
    $removed.metadataRemoved = $true

    if ((Test-Path -LiteralPath $vhdx -PathType Leaf) -or (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
        throw 'The C6 volume metadata or VHDX still exists after cleanup.'
    }

    Write-C6RemoveResult -Status 'PASS' -ExitCode 0 -Reason 'The C6 volume resources were removed.' -Removed $removed
}
catch {
    Write-C6RemoveResult -Status 'FAIL' -ExitCode 5 -Reason $_.Exception.Message -Removed $null
}
