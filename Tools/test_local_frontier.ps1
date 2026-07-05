param(
    [switch]$SkipClient
)

$ErrorActionPreference = "Stop"

$stackScript = Join-Path $PSScriptRoot "test_local_stack.ps1"
$python = (Get-Command python).Source
if ($SkipClient) {
    & $stackScript -SkipClient | Out-Null
} else {
    & $stackScript | Out-Null
}

& $python (Join-Path $PSScriptRoot "test_luam_ai_gateway.py") | Out-Null

[pscustomobject]@{
    local_stack = $true
    ai_gateway = $true
} | ConvertTo-Json -Depth 4
