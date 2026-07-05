param(
    [string]$Root = "C:\Users\Orvar Od\Monolith-DS",
    [string]$TempRoot = "C:\MonolithTemp",
    [string]$GatewayPort = "8787",
    [string]$ServerPort = "1213",
    [switch]$SkipClient,
    [switch]$Reset
)

$ErrorActionPreference = "Stop"

$serverDir = Join-Path $Root "Bin\Content.Server"
$clientDir = Join-Path $Root "Bin\Content.Client"
$serverConfig = Join-Path $TempRoot ("server_config_local-" + [DateTime]::UtcNow.ToString("yyyyMMddHHmmssfff") + ".toml")
$serverData = Join-Path $TempRoot "server-data"
$serverLogDir = Join-Path $TempRoot ("server-logs-" + [DateTime]::UtcNow.ToString("yyyyMMddHHmmssfff"))
$gatewaySource = Join-Path $Root "Tools\luam_ai_gateway.py"
$gatewayCopy = Join-Path $TempRoot ("luam_ai_gateway-" + [DateTime]::UtcNow.ToString("yyyyMMddHHmmssfff") + ".py")
$gatewayOut = Join-Path $TempRoot "gw-out.txt"
$gatewayErr = Join-Path $TempRoot "gw-err.txt"
$gatewayAudit = Join-Path $TempRoot "gw-audit.jsonl"
$serverOut = Join-Path $TempRoot "server-out.txt"
$serverErr = Join-Path $TempRoot "server-err.txt"
$clientOut = Join-Path $TempRoot "client-out.txt"
$clientErr = Join-Path $TempRoot "client-err.txt"
$pidFile = Join-Path $TempRoot "local-stack-pids.json"

New-Item -ItemType Directory -Force -Path $TempRoot | Out-Null
New-Item -ItemType Directory -Force -Path $serverData | Out-Null
New-Item -ItemType Directory -Force -Path $serverLogDir | Out-Null
Copy-Item $gatewaySource $gatewayCopy -Force
Copy-Item (Join-Path $Root "server_config_local.toml") $serverConfig -Force

$serverConfigText = Get-Content $serverConfig -Raw
$serverConfigText = $serverConfigText -replace 'path = "logs"', ('path = "' + ($serverLogDir -replace '\\','/') + '"')
Set-Content -LiteralPath $serverConfig -Value $serverConfigText -Encoding UTF8

$python = (Get-Command python).Source

function Stop-LocalStackProcess {
    param(
        [string]$Name,
        [string[]]$Needles
    )

    Get-CimInstance Win32_Process -Filter "Name='$Name'" | Where-Object {
        $cmd = $_.CommandLine
        $cmd -and ($Needles | Where-Object { $cmd -like "*$_*" })
    } | ForEach-Object {
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

function Wait-HttpHealth {
    param(
        [string]$Url,
        [int]$TimeoutSeconds = 30
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            Invoke-RestMethod -Uri $Url -Method Get -TimeoutSec 3 | Out-Null
            return $true
        } catch {
            Start-Sleep -Milliseconds 250
        }
    }

    return $false
}

function Wait-FilePattern {
    param(
        [string]$Path,
        [string]$Pattern,
        [int]$TimeoutSeconds = 30
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $Path) {
            $content = Get-Content $Path -ErrorAction SilentlyContinue
            if ($content -match $Pattern) {
                return $true
            }
        }
        Start-Sleep -Milliseconds 250
    }

    return $false
}

if ($Reset) {
    Stop-Process -Name "Content.Server" -Force -ErrorAction SilentlyContinue
    Stop-Process -Name "Robust.Server" -Force -ErrorAction SilentlyContinue
    Stop-Process -Name "Content.Client" -Force -ErrorAction SilentlyContinue
    Stop-Process -Name "Robust.Client" -Force -ErrorAction SilentlyContinue
    Stop-LocalStackProcess -Name "python.exe" -Needles @($gatewayCopy, "luam_ai_gateway.py")
    Start-Sleep -Seconds 2
}

$gateway = $null
$server = $null
$client = $null

try {
    $previousGatewayAudit = $env:LUAM_AI_AUDIT_LOG
    $env:LUAM_AI_AUDIT_LOG = $gatewayAudit
    try {
        $gateway = Start-Process -FilePath $python -ArgumentList @($gatewayCopy) -WindowStyle Hidden -PassThru -RedirectStandardOutput $gatewayOut -RedirectStandardError $gatewayErr
    } finally {
        if ([string]::IsNullOrEmpty($previousGatewayAudit)) {
            Remove-Item Env:LUAM_AI_AUDIT_LOG -ErrorAction SilentlyContinue
        } else {
            $env:LUAM_AI_AUDIT_LOG = $previousGatewayAudit
        }
    }

    if (-not (Wait-HttpHealth -Url "http://127.0.0.1:$GatewayPort/health" -TimeoutSeconds 20)) {
        throw "Gateway did not become healthy on port $GatewayPort. See $gatewayOut / $gatewayErr"
    }

    $serverCmd = "cd /d `"$serverDir`" && `"`"Content.Server.exe`"`" --config-file `"$serverConfig`" --data-dir `"$serverData`""
    $server = Start-Process -FilePath "cmd.exe" -ArgumentList "/c", $serverCmd -WindowStyle Hidden -PassThru -RedirectStandardOutput $serverOut -RedirectStandardError $serverErr
    if (-not (Wait-FilePattern -Path $serverOut -Pattern 'Server Version .* -> Ready' -TimeoutSeconds 45)) {
        throw "Server did not report Ready. See $serverOut / $serverErr"
    }

    if (-not $SkipClient) {
        $clientCmd = "cd /d `"$clientDir`" && `"`"Content.Client.exe`"`" --connect --connect-address 127.0.0.1:$ServerPort --self-contained"
        $client = Start-Process -FilePath "cmd.exe" -ArgumentList "/c", $clientCmd -WindowStyle Hidden -PassThru -RedirectStandardOutput $clientOut -RedirectStandardError $clientErr
        if (-not (Wait-FilePattern -Path $clientOut -Pattern 'Runlevel changed to: InGame|Runlevel changed to: Connected' -TimeoutSeconds 120)) {
            throw "Client did not reach Connected/InGame. See $clientOut / $clientErr"
        }
    }

    [pscustomobject]@{
        gateway = [pscustomobject]@{
            pid = $gateway.Id
            health = "http://127.0.0.1:$GatewayPort/health"
            stdout = $gatewayOut
            stderr = $gatewayErr
            audit = $gatewayAudit
        }
        server = [pscustomobject]@{
            pid = $server.Id
            stdout = $serverOut
            stderr = $serverErr
        }
        client = if ($client) {
            [pscustomobject]@{
                pid = $client.Id
                stdout = $clientOut
                stderr = $clientErr
            }
        } else {
            $null
        }
    } | Tee-Object -Variable result | ConvertTo-Json -Depth 4

    if ($result.client) {
        $clientPid = $result.client.pid
    } else {
        $clientPid = ""
    }

    Write-Host "gateway=$($result.gateway.pid) server=$($result.server.pid) client=$clientPid"
    Write-Host "gateway_health=$($result.gateway.health)"
    Write-Host "gateway_audit=$($result.gateway.audit)"
    Write-Host "server_stdout=$($result.server.stdout)"
    if ($result.client) {
        Write-Host "client_stdout=$($result.client.stdout)"
    }

    [pscustomobject]@{
        gateway = $gateway.Id
        server = $server.Id
        client = if ($client) { $client.Id } else { $null }
        gateway_copy = $gatewayCopy
        gateway_audit = $gatewayAudit
        server_port = $ServerPort
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $pidFile -Encoding UTF8
}
catch {
    if ($client) { Stop-Process -Id $client.Id -Force -ErrorAction SilentlyContinue }
    if ($server) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
    if ($gateway) { Stop-Process -Id $gateway.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
    throw
}
