param(
    [string]$StatusUrl = "http://188.127.225.57:1212/status",
    [string]$InfoUrl = "http://188.127.225.57:1212/info",
    [string]$HubUrl = "https://hub.spacestation14.com/api/servers",
    [string]$ServerAddress = "ss14://188.127.225.57:1212/",
    [string]$SshTarget = "monolith-new",
    [string]$ServiceName = "monolith-ds.service",
    [string]$ReportPath = "$env:TEMP\monolith-restart-check-latest.json",
    [string[]]$ExpectedTags = @("lang:ru", "region:eu_e", "rp:low"),
    [int]$ExpectedSoftMaxPlayers = 100,
    [int]$MaxRoundAgeDays = 7,
    [int]$PollSeconds = 60,
    [switch]$Wait,
    [switch]$Force,
    [switch]$VerifyOnly,
    [switch]$SkipSsh,
    [switch]$Strict
)

$ErrorActionPreference = "Stop"

function Get-MonolithStatus {
    Invoke-RestMethod -Uri $StatusUrl -TimeoutSec 10
}

function Get-MonolithInfo {
    Invoke-RestMethod -Uri $InfoUrl -TimeoutSec 15
}

function Get-HubEntry {
    $servers = Invoke-RestMethod -Uri $HubUrl -TimeoutSec 20
    $servers | Where-Object { $_.address -eq $ServerAddress } | Select-Object -First 1
}

function Get-RemoteJournalMetrics {
    $script = @"
svc='$ServiceName'
since=`$(systemctl show -p ActiveEnterTimestamp --value "`$svc")
echo since=`$since
echo handshake_count=`$(sudo journalctl -u "`$svc" --since="`$since" --no-pager | grep -c 'disconnected while handshake was in-progress' || true)
echo keepup_count=`$(sudo journalctl -u "`$svc" --since="`$since" --no-pager | grep -c 'MainLoop: Cannot keep up' || true)
echo error_count=`$(sudo journalctl -u "`$svc" --since="`$since" --no-pager | grep -Ec 'FATL|EROR|Exception|exception:|OOM|killed process|YAML' || true)
echo cleanup_count=`$(sudo journalctl -u "`$svc" --since="`$since" --no-pager | grep -Ec 'system\.space_cleanup|system\.grid_cleanup|system\.map: Removing grid' || true)
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($script))
    $command = "printf %s $encoded | base64 -d | bash"

    $raw = ssh $SshTarget $command
    $metrics = [ordered]@{}
    foreach ($line in $raw) {
        if ($line -match "^([^=]+)=(\d+)$") {
            $metrics[$matches[1]] = [int]$matches[2]
        } elseif ($line -match "^since=(.+)$") {
            $metrics["since"] = $matches[1]
        }
    }
    [pscustomobject]$metrics
}

function Add-PreflightCheck {
    param(
        [System.Collections.Generic.List[object]]$Checks,
        [string]$Name,
        [bool]$Passed,
        [string]$Detail,
        [string]$Severity = "error"
    )

    $Checks.Add([pscustomobject]@{
        name = $Name
        status = if ($Passed) { "passed" } elseif ($Severity -eq "warning") { "warning" } else { "failed" }
        severity = $Severity
        detail = $Detail
    }) | Out-Null
}

function Invoke-MonolithVerification {
    $status = Get-MonolithStatus
    $info = Get-MonolithInfo
    $hubEntry = Get-HubEntry
    $serviceState = "skipped"
    $journalMetrics = [pscustomobject]@{ skipped = $true }

    if (-not $SkipSsh) {
        try {
            $serviceState = (ssh $SshTarget "systemctl is-active $ServiceName" | Select-Object -First 1)
            $journalMetrics = Get-RemoteJournalMetrics
        }
        catch {
            $serviceState = "unavailable"
            $journalMetrics = [pscustomobject]@{
                unavailable = $true
                error = $_.Exception.Message
            }
        }
    }

    $checks = New-Object System.Collections.Generic.List[object]
    $statusTags = @($status.tags)
    $hubStatus = if ($hubEntry) { $hubEntry.statusData } else { $null }
    $hubTags = if ($hubStatus) { @($hubStatus.tags) } else { @() }
    $manifestHash = [string]$info.build.manifest_hash
    $roundStart = $null
    $roundAgeDays = $null

    if (-not [string]::IsNullOrWhiteSpace([string]$status.round_start_time)) {
        try {
            $roundStart = [DateTimeOffset]::Parse([string]$status.round_start_time)
            $roundAgeDays = [Math]::Round(([DateTimeOffset]::UtcNow - $roundStart).TotalDays, 3)
        }
        catch {
            $roundStart = $null
        }
    }

    Add-PreflightCheck $checks "status-http" ($null -ne $status) "Status endpoint returned round $($status.round_id), players $($status.players)."
    Add-PreflightCheck $checks "info-http" ($null -ne $info) "Info endpoint returned engine $($info.build.engine_version), manifest $manifestHash."
    Add-PreflightCheck $checks "hub-entry" ([bool]$hubEntry) "Hub entry for $ServerAddress."
    Add-PreflightCheck $checks "acz" ([bool]$info.build.acz) "ACZ must be enabled for packaged client delivery."
    Add-PreflightCheck $checks "manifest-hash" (-not [string]::IsNullOrWhiteSpace($manifestHash)) "Info manifest_hash should be present."
    Add-PreflightCheck $checks "auth-required" ([string]$info.auth.mode -eq "Required") "Auth mode is $($info.auth.mode)." "warning"
    Add-PreflightCheck $checks "soft-max" ([int]$status.soft_max_players -eq $ExpectedSoftMaxPlayers) "soft_max_players=$($status.soft_max_players), expected $ExpectedSoftMaxPlayers." "warning"
    Add-PreflightCheck $checks "panic-bunker" (-not [bool]$status.panic_bunker) "panic_bunker=$($status.panic_bunker)." "warning"
    Add-PreflightCheck $checks "connect-address" (-not [string]::IsNullOrWhiteSpace([string]$info.connect_address)) "connect_address='$($info.connect_address)'; empty is acceptable only when hub/server_url is correct." "warning"
    Add-PreflightCheck $checks "round-age" ($roundAgeDays -ne $null -and $roundAgeDays -le $MaxRoundAgeDays) "round_age_days=$roundAgeDays, max expected $MaxRoundAgeDays." "warning"
    Add-PreflightCheck $checks "description-round-length" ([string]$info.desc -match "\b7\b") "Server description should mention the 7-day round cadence." "warning"

    foreach ($tag in $ExpectedTags) {
        Add-PreflightCheck $checks "status-tag-$tag" ($statusTags -contains $tag) "Status tags: $($statusTags -join ', ')." "warning"
        if ($hubEntry) {
            Add-PreflightCheck $checks "hub-tag-$tag" ($hubTags -contains $tag) "Hub tags: $($hubTags -join ', ')." "warning"
        }
    }

    if (-not $SkipSsh) {
        Add-PreflightCheck $checks "service-active" ([string]$serviceState -eq "active") "systemctl is-active $ServiceName = $serviceState."
        if ($journalMetrics.PSObject.Properties.Name -contains "error_count") {
            Add-PreflightCheck $checks "journal-errors" ([int]$journalMetrics.error_count -eq 0) "journal error count since service start: $($journalMetrics.error_count)." "warning"
        }
    }

    $failures = @($checks | Where-Object { $_.status -eq "failed" })
    $warnings = @($checks | Where-Object { $_.status -eq "warning" })

    $report = [ordered]@{
        checked_at = (Get-Date).ToString("o")
        ok = $failures.Count -eq 0
        strict_ok = $failures.Count -eq 0 -and $warnings.Count -eq 0
        service = [ordered]@{
            name = $ServiceName
            state = $serviceState
            ssh_skipped = [bool]$SkipSsh
        }
        status = $status
        info = [ordered]@{
            connect_address = $info.connect_address
            engine_version = $info.build.engine_version
            manifest_hash = $info.build.manifest_hash
            acz = $info.build.acz
        }
        hub = [ordered]@{
            found = [bool]$hubEntry
            address = $hubEntry.address
            players = $hubEntry.statusData.players
            preset = $hubEntry.statusData.preset
            map = $hubEntry.statusData.map
        }
        preflight = [ordered]@{
            strict = [bool]$Strict
            checks = @($checks.ToArray())
            failure_count = $failures.Count
            warning_count = $warnings.Count
        }
        journal_since_service_start = $journalMetrics
    }

    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
    Write-Host "Verification report written to $ReportPath"
    Write-Host ($report | ConvertTo-Json -Depth 8)
    return [pscustomobject]$report
}

function Write-StatusLine($status) {
    $players = $status.players
    $round = $status.round_id
    $runLevel = $status.run_level
    $map = $status.map
    $preset = $status.preset
    Write-Host "players=$players round_id=$round run_level=$runLevel map='$map' preset='$preset'"
}

if ($VerifyOnly) {
    $report = Invoke-MonolithVerification
    if ($Strict -and -not $report.strict_ok) {
        exit 1
    }

    if (-not $report.ok) {
        exit 1
    }

    exit 0
}

do {
    $status = Get-MonolithStatus
    Write-StatusLine $status

    if ($Force -or [int]$status.players -eq 0) {
        if ($Force -and [int]$status.players -gt 0) {
            Write-Warning "Force was set; restarting while players are online."
        }

        Write-Host "Restarting $ServiceName on $SshTarget..."
        ssh $SshTarget "sudo systemctl restart $ServiceName"
        if ($LASTEXITCODE -ne 0) {
            exit $LASTEXITCODE
        }

        Start-Sleep -Seconds 12
        [void](Invoke-MonolithVerification)
        exit 0
    }

    if (-not $Wait) {
        Write-Host "Online is not zero; not restarting. Re-run with -Wait to poll, or -Force to override."
        exit 2
    }

    Write-Host "Online is not zero; waiting $PollSeconds seconds..."
    Start-Sleep -Seconds $PollSeconds
} while ($true)
