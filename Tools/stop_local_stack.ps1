param(
    [string]$GatewayPath = "C:\MonolithTemp\luam_ai_gateway.py",
    [string]$PidFile = "C:\MonolithTemp\local-stack-pids.json",
    [string]$ServerPort = "1213",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

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

function Remove-LocalStackArtifacts {
    param(
        [string]$Root = "C:\MonolithTemp"
    )

    $explicitFiles = @(
        "gw-out.txt",
        "gw-err.txt",
        "server-out.txt",
        "server-err.txt",
        "client-out.txt",
        "client-err.txt",
        "local-stack-pids.json",
        "luam_ai_gateway.py",
        "server_config_local.toml"
    )

    foreach ($file in $explicitFiles) {
        Remove-Item -LiteralPath (Join-Path $Root $file) -Force -ErrorAction SilentlyContinue
    }

    Get-ChildItem -Path $Root -Recurse -File -Force -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -like "luam_ai_gateway-*.py" -or $_.Name -like "server_config_local-*.toml"
    } | ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
    }

    Get-ChildItem -Path $Root -Recurse -Directory -Force -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -like "server-logs-*"
    } | ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Force -Recurse -ErrorAction SilentlyContinue
    }

    Remove-Item -LiteralPath (Join-Path $Root "server-data") -Force -Recurse -ErrorAction SilentlyContinue
}

Stop-Process -Name "Content.Server" -Force:$Force -ErrorAction SilentlyContinue
Stop-Process -Name "Robust.Server" -Force:$Force -ErrorAction SilentlyContinue
Stop-Process -Name "Content.Client" -Force:$Force -ErrorAction SilentlyContinue
Stop-Process -Name "Robust.Client" -Force:$Force -ErrorAction SilentlyContinue
if (Test-Path $PidFile) {
    try {
        $pids = Get-Content -LiteralPath $PidFile -Raw | ConvertFrom-Json
        foreach ($pid in @($pids.gateway, $pids.server, $pids.client)) {
            if ($pid) {
                Stop-Process -Id $pid -Force -ErrorAction SilentlyContinue
            }
        }
    } catch {
        Write-Verbose ("Failed to parse pid file {0}: {1}" -f $PidFile, $_)
    }
}
Stop-LocalStackProcess -Name "python.exe" -Needles @($GatewayPath, "luam_ai_gateway", "test_luam_ai_gateway.py")
Stop-LocalStackProcess -Name "cmd.exe" -Needles @($GatewayPath, "luam_ai_gateway", "test_luam_ai_gateway.py")

$deadline = (Get-Date).AddSeconds(10)
while ((Get-Date) -lt $deadline) {
    $alive = Get-CimInstance Win32_Process -Filter "Name='python.exe' or Name='cmd.exe'" | Where-Object {
        $cmd = $_.CommandLine
        $cmd -and ($cmd -like "*luam_ai_gateway*" -or $cmd -like "*test_luam_ai_gateway.py*")
    }
    if (-not $alive) {
        break
    }
    Start-Sleep -Milliseconds 200
}

Remove-Item -LiteralPath $PidFile -Force -ErrorAction SilentlyContinue
Remove-LocalStackArtifacts

[pscustomobject]@{
    stopped = @(
        "Content.Server",
        "Robust.Server",
        "Content.Client",
        "Robust.Client",
        "python gateway/test"
    )
    gateway_path = $GatewayPath
    server_port = $ServerPort
} | ConvertTo-Json -Depth 4
