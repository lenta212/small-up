param(
    [switch]$SkipClient
)

$ErrorActionPreference = "Stop"

$startScript = Join-Path $PSScriptRoot "start_local_stack.ps1"
$stopScript = Join-Path $PSScriptRoot "stop_local_stack.ps1"

try {
    if ($SkipClient) {
        & $startScript -Reset -SkipClient
    } else {
        & $startScript -Reset
    }

    Invoke-RestMethod -Uri "http://127.0.0.1:8787/health" -Method Get | Out-Null
}
finally {
    & $stopScript -Force | Out-Null
}

$deadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $deadline) {
    $alive = Get-Process Content.Server,Content.Client,Robust.Server,Robust.Client -ErrorAction SilentlyContinue
    if (-not $alive) {
        break
    }
    Start-Sleep -Milliseconds 250
}

if (Get-Process Content.Server,Content.Client,Robust.Server,Robust.Client -ErrorAction SilentlyContinue) {
    throw "Local stack did not stop cleanly."
}

[pscustomobject]@{
    started = $true
    health = "http://127.0.0.1:8787/health"
    stopped = $true
} | ConvertTo-Json -Depth 4
