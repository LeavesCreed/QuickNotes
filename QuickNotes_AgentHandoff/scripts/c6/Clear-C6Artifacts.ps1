#requires -Version 5.1
<#
.SYNOPSIS
Restores any C6 ACL backup and removes only the C6 run root for this RunId.

This is the final cleanup entry point used by the C6 fault driver. It never
deletes %TEMP%\QuickNotes-C6 itself, never follows a reparse point, and
refuses to continue if an ACL cannot be restored or an untagged VHDX remains.
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
        volumeRemoved = $false
        runRootRemoved = $false
    }

    $aclBackups = @(Get-ChildItem -LiteralPath $runRoot -Recurse -Filter 'acl-*.json' -File -ErrorAction SilentlyContinue)
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
            }
            catch {
                Write-C6ClearResult -Status 'FAIL' -ExitCode 5 -Reason "ACL restoration failed for a C6 directory. Repair with icacls or Set-Acl from the recorded SDDL before deleting: $($_.Exception.Message)" -Detail $detail
            }

            if (-not (Test-C6WriteAccess -Directory $target)) {
                Write-C6ClearResult -Status 'FAIL' -ExitCode 5 -Reason 'ACL restoration was applied but the write probe still fails. Repair with icacls before deleting.' -Detail $detail
            }
        }

        Remove-Item -LiteralPath $backup.FullName -Force
        $detail.aclRestored = [int]$detail.aclRestored + 1
    }

    $metadataPath = Join-Path $root ($RunId + '.volume.json')
    $untaggedVhdx = Join-Path $root ($RunId + '.vhdx')
    if (Test-Path -LiteralPath $metadataPath -PathType Leaf) {
        $removeOutput = @(& (Join-Path $PSScriptRoot 'Remove-C6TestVolume.ps1') -RunId $RunId 2>&1 |
            ForEach-Object { $_.ToString() })
        $removeExit = $LASTEXITCODE
        if ($removeExit -ne 0) {
            Write-C6ClearResult -Status 'FAIL' -ExitCode 5 -Reason "Remove-C6TestVolume refused or failed; the run root was kept for diagnosis. $($removeOutput -join ' ')" -Detail $detail
        }

        $detail.volumeRemoved = $true
    }
    elseif (Test-Path -LiteralPath $untaggedVhdx -PathType Leaf) {
        Write-C6ClearResult -Status 'FAIL' -ExitCode 5 -Reason 'An untagged C6 VHDX exists without CreatedByC6 metadata; refusing to delete the run root.' -Detail $detail
    }

    Remove-Item -LiteralPath $runRoot -Recurse -Force
    $detail.runRootRemoved = $true
    if (Test-Path -LiteralPath $runRoot) {
        Write-C6ClearResult -Status 'FAIL' -ExitCode 5 -Reason 'The C6 run root still exists after cleanup.' -Detail $detail
    }

    Write-C6ClearResult -Status 'PASS' -ExitCode 0 -Reason 'The C6 run root and its tagged resources were removed.' -Detail $detail
}
catch {
    Write-C6ClearResult -Status 'FAIL' -ExitCode 5 -Reason $_.Exception.Message -Detail $null
}
