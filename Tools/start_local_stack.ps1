param(
    [string]$Root = "C:\Users\Orvar Od\Monolith-DS",
    [string]$TempRoot = "C:\MonolithTemp",
    [string]$GatewayPort = "8787",
    [string]$ServerPort = "1213",
    [switch]$OfficialOpenAI,
    [string]$OpenAIModel = "gpt-5.5",
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
$mcpServerSource = Join-Path $Root "Tools\luam_openai_mcp_server.py"
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

function Save-ProcessEnv {
    param([string[]]$Names)

    $snapshot = @{}
    foreach ($name in $Names) {
        $snapshot[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
    }
    return $snapshot
}

function Restore-ProcessEnv {
    param([hashtable]$Snapshot)

    foreach ($name in $Snapshot.Keys) {
        $value = $Snapshot[$name]
        if ([string]::IsNullOrEmpty($value)) {
            Remove-Item -Path ("Env:{0}" -f $name) -ErrorAction SilentlyContinue
        } else {
            Set-Item -Path ("Env:{0}" -f $name) -Value $value
        }
    }
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
    $gatewayEnvNames = @(
        "LUAM_AI_AUDIT_LOG",
        "LUAM_AI_PROVIDER",
        "LUAM_MCP_COMMAND",
        "LUAM_MCP_SERVER",
        "LUAM_MCP_TOOL",
        "LUAM_MCP_SERVER_ID",
        "LUAM_MCP_TIMEOUT",
        "LUAM_OPENAI_MCP_SERVER",
        "LUAM_OPENAI_MCP_COMMAND",
        "LUAM_OPENAI_MCP_TOOL",
        "LUAM_OPENAI_MCP_SERVER_ID",
        "LUAM_OPENAI_MCP_TIMEOUT",
        "OPENAI_OFFICIAL_API_KEY",
        "OPENAI_OFFICIAL_BASE_URL",
        "OPENAI_API_MODE",
        "OPENAI_MODEL",
        "OPENAI_BASE_URL",
        "OPENAI_API_BASE",
        "LUAM_OPENAI_RESPONSES_URL",
        "LUAM_OPENAI_CHAT_COMPLETIONS_URL",
        "LUAM_OPENAI_MCP_RESPONSES_URL",
        "OPENAI_RESPONSES_URL",
        "OPENAI_CHAT_COMPLETIONS_URL",
        "LUAM_COMPAT_API_KEY",
        "LUAM_COMPAT_BASE_URL",
        "LUAM_COMPAT_MODEL",
        "LUAM_COMPAT_API_MODE",
        "LUAM_COMPAT_RESPONSES_URL",
        "LUAM_COMPAT_CHAT_COMPLETIONS_URL"
    )
    $previousGatewayEnv = Save-ProcessEnv -Names $gatewayEnvNames
    $env:LUAM_AI_AUDIT_LOG = $gatewayAudit
    if ($OfficialOpenAI) {
        if ([string]::IsNullOrWhiteSpace($env:OPENAI_OFFICIAL_API_KEY)) {
            throw "OPENAI_OFFICIAL_API_KEY is required when -OfficialOpenAI is used. Do not reuse a custom-provider OPENAI_API_KEY for the official OpenAI MCP server."
        }

        if (-not (Test-Path -LiteralPath $mcpServerSource)) {
            throw "LuaM OpenAI MCP server is missing: $mcpServerSource"
        }

        Remove-Item Env:LUAM_MCP_COMMAND -ErrorAction SilentlyContinue
        Remove-Item Env:LUAM_OPENAI_MCP_COMMAND -ErrorAction SilentlyContinue
        Remove-Item Env:LUAM_OPENAI_MCP_SERVER -ErrorAction SilentlyContinue
        Remove-Item Env:LUAM_OPENAI_MCP_TOOL -ErrorAction SilentlyContinue
        Remove-Item Env:LUAM_OPENAI_MCP_SERVER_ID -ErrorAction SilentlyContinue
        $env:LUAM_AI_PROVIDER = "mcp"
        $env:LUAM_MCP_SERVER = $mcpServerSource
        $env:LUAM_MCP_TOOL = "openai_responses_json"
        $env:LUAM_MCP_SERVER_ID = "luam-openai"
        $env:OPENAI_MODEL = $OpenAIModel
        Remove-Item Env:OPENAI_API_MODE -ErrorAction SilentlyContinue
        Remove-Item Env:OPENAI_BASE_URL -ErrorAction SilentlyContinue
        Remove-Item Env:OPENAI_API_BASE -ErrorAction SilentlyContinue
        Remove-Item Env:LUAM_OPENAI_RESPONSES_URL -ErrorAction SilentlyContinue
        Remove-Item Env:LUAM_OPENAI_CHAT_COMPLETIONS_URL -ErrorAction SilentlyContinue
        Remove-Item Env:OPENAI_RESPONSES_URL -ErrorAction SilentlyContinue
        Remove-Item Env:OPENAI_CHAT_COMPLETIONS_URL -ErrorAction SilentlyContinue
        Remove-Item Env:LUAM_COMPAT_API_KEY -ErrorAction SilentlyContinue
        Remove-Item Env:LUAM_COMPAT_BASE_URL -ErrorAction SilentlyContinue
        Remove-Item Env:LUAM_COMPAT_MODEL -ErrorAction SilentlyContinue
        Remove-Item Env:LUAM_COMPAT_API_MODE -ErrorAction SilentlyContinue
        Remove-Item Env:LUAM_COMPAT_RESPONSES_URL -ErrorAction SilentlyContinue
        Remove-Item Env:LUAM_COMPAT_CHAT_COMPLETIONS_URL -ErrorAction SilentlyContinue
    }
    try {
        $gateway = Start-Process -FilePath $python -ArgumentList @($gatewayCopy) -WindowStyle Hidden -PassThru -RedirectStandardOutput $gatewayOut -RedirectStandardError $gatewayErr
    } finally {
        Restore-ProcessEnv -Snapshot $previousGatewayEnv
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
