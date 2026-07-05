param(
    [string]$StatusUrl = "http://188.127.225.57:1212/status",
    [string]$InfoUrl = "http://188.127.225.57:1212/info",
    [string]$HubUrl = "https://hub.spacestation14.com/api/servers",
    [string]$ServerAddress = "ss14://188.127.225.57:1212/",
    [string]$SshTarget = "monolith-new",
    [string]$ServiceName = "monolith-ds.service",
    [string]$ReportPath = "$env:TEMP\monolith-restart-check-latest.json",
    [int]$PollSeconds = 60,
    [switch]$Wait,
    [switch]$Force,
    [switch]$VerifyOnly
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

function Invoke-MonolithVerification {
    $status = Get-MonolithStatus
    $info = Get-MonolithInfo
    $hubEntry = Get-HubEntry
    $serviceState = ssh $SshTarget "systemctl is-active $ServiceName"
    $journalMetrics = Get-RemoteJournalMetrics

    $report = [ordered]@{
        checked_at = (Get-Date).ToString("o")
        service = [ordered]@{
            name = $ServiceName
            state = ($serviceState | Select-Object -First 1)
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
        journal_since_service_start = $journalMetrics
    }

    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
    Write-Host "Verification report written to $ReportPath"
    $report | ConvertTo-Json -Depth 8
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
    Invoke-MonolithVerification
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
        Invoke-MonolithVerification
        exit 0
    }

    if (-not $Wait) {
        Write-Host "Online is not zero; not restarting. Re-run with -Wait to poll, or -Force to override."
        exit 2
    }

    Write-Host "Online is not zero; waiting $PollSeconds seconds..."
    Start-Sleep -Seconds $PollSeconds
} while ($true)
