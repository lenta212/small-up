Set-StrictMode -Version 3.0

function Get-LuaMReleaseStatePath {
    param([string]$Root)

    if ([string]::IsNullOrWhiteSpace($Root)) {
        $Root = Split-Path -Parent $PSScriptRoot
    }

    return Join-Path $Root ".agents/current_release.json"
}

function Read-LuaMReleaseState {
    param([string]$Root)

    $path = Get-LuaMReleaseStatePath -Root $Root
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return [pscustomobject]@{
            schemaVersion = 1
            updatedAtUtc = $null
            phase = "not-started"
            objective = ""
            latestRolloutTag = $null
            lastLocalFast = $null
            lastProductionGate = $null
            lastDeploy = $null
            notes = @()
        }
    }

    $state = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -ErrorAction Stop
    if ($state.schemaVersion -ne 1) {
        throw "Unsupported LuaM release state schemaVersion: $($state.schemaVersion)"
    }

    return $state
}

function Write-LuaMReleaseState {
    param(
        [Parameter(Mandatory = $true)]
        [object]$State,
        [string]$Root
    )

    $path = Get-LuaMReleaseStatePath -Root $Root
    $directory = Split-Path -Parent $path
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $State.updatedAtUtc = [DateTime]::UtcNow.ToString("o")
    $State | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path -Encoding UTF8
    return $path
}

function Set-LuaMReleaseStage {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet("local-fast", "production-gate", "deployed", "failed")]
        [string]$Phase,
        [Parameter(Mandatory = $true)]
        [object]$Result,
        [string]$Root
    )

    $state = Read-LuaMReleaseState -Root $Root
    $state.phase = $Phase
    switch ($Phase) {
        "local-fast" { $state.lastLocalFast = $Result }
        "production-gate" { $state.lastProductionGate = $Result }
        "deployed" {
            $state.lastDeploy = $Result
            if ($Result.PSObject.Properties.Name -contains "tag") {
                $state.latestRolloutTag = $Result.tag
            }
        }
        "failed" {
            $state.notes = @(@($state.notes) + [pscustomobject]@{
                atUtc = [DateTime]::UtcNow.ToString("o")
                level = "error"
                detail = [string]$Result
            })
        }
    }

    Write-LuaMReleaseState -State $state -Root $Root | Out-Null
    return $state
}
