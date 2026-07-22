[CmdletBinding()]
param(
    [string]$ExternalClientBaseUrl = "http://188.127.225.57:1213",
    [string]$ConfigSourcePath = "server_config.remote.toml",
    [string]$RemoteConfigPath = "/opt/monolith-ds/server/server_config.toml",
    [string]$RemoteDataDir = "/opt/monolith-ds/data",
    [string]$Tag = "",
    [switch]$Deploy,
    [switch]$Force,
    [switch]$LegacyShipSaveBootstrap,
    [switch]$SkipLocalFast,
    [switch]$FreezePolicyAfterDeploy = $true,
    [switch]$Json
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "luam_release_state.ps1")

function Invoke-JsonScript {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $started = [DateTime]::UtcNow
    $output = @(& powershell -NoProfile -ExecutionPolicy Bypass @Arguments)
    $exit = if ($null -eq $global:LASTEXITCODE) { 0 } else { $global:LASTEXITCODE }
    if ($exit -ne 0) {
        throw "$Name failed with exit code $exit.`n$($output -join "`n")"
    }

    try {
        $data = ($output -join "`n") | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "$Name returned invalid JSON: $($_.Exception.Message)`n$($output -join "`n")"
    }

    return [pscustomobject]@{
        name = $Name
        startedAtUtc = $started.ToString("o")
        finishedAtUtc = [DateTime]::UtcNow.ToString("o")
        data = $data
    }
}

function Invoke-PlainScript {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $started = [DateTime]::UtcNow
    & powershell -NoProfile -ExecutionPolicy Bypass @Arguments
    $exit = if ($null -eq $global:LASTEXITCODE) { 0 } else { $global:LASTEXITCODE }
    if ($exit -ne 0) {
        throw "$Name failed with exit code $exit"
    }

    return [pscustomobject]@{
        name = $Name
        startedAtUtc = $started.ToString("o")
        finishedAtUtc = [DateTime]::UtcNow.ToString("o")
        status = "passed"
    }
}

function Freeze-LuaMDeploymentPolicy {
    $policyPath = Join-Path $root "Tools/luam_release_policy.json"
    $policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json -ErrorAction Stop
    $policy.remoteDeployFrozen = $true
    $policy.reason = "Production rollout $Tag completed. Remote deployment is frozen again; future server or client-static mutation requires a fresh explicit authorization."
    $policy.deploymentAuthorization.state = "frozen"
    $policy.deploymentAuthorization.approvalId = $null
    $policy.deploymentAuthorization.approvedBy = $null
    $policy.deploymentAuthorization.approvedAtUtc = $null
    $policy.deploymentAuthorization.expiresAtUtc = $null
    $policy.deploymentAuthorization.allowedMutations = @()
    $policy.pendingLocalIntegrationBatch.state = "local-package-only"
    $policy | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $policyPath -Encoding UTF8
}

if ([string]::IsNullOrWhiteSpace($Tag)) {
    $Tag = "luam-" + [DateTime]::UtcNow.ToString("yyyyMMdd-HHmmss")
}

$steps = [System.Collections.Generic.List[object]]::new()
$ok = $true
$failure = $null

Push-Location $root
try {
    if (-not $SkipLocalFast) {
        $steps.Add((Invoke-JsonScript -Name "local-fast" -Arguments @("-File", "Tools/prepare_luam_hotfix.ps1", "-Scope", "Auto", "-Json"))) | Out-Null
    }

    $package = Invoke-JsonScript -Name "source-package" -Arguments @("-File", "Tools/build_luam_release_package.ps1", "-RunTests", "-RunLocalSmoke", "-Json")
    $steps.Add($package) | Out-Null
    if ($package.data.ok -ne $true -or $package.data.productionEligible -ne $true) {
        throw "Source package is not production-eligible."
    }

    $verification = Invoke-JsonScript -Name "source-verify" -Arguments @(
        "-File", "Tools/verify_luam_release_package.ps1",
        "-PackagePath", [string]$package.data.package,
        "-ExpectedSha256", [string]$package.data.sha256,
        "-Json")
    $steps.Add($verification) | Out-Null
    if ($verification.data.ok -ne $true) {
        throw "Source package verifier did not pass."
    }

    $binary = Invoke-JsonScript -Name "binary-build" -Arguments @(
        "-File", "Tools/build_luam_server_release.ps1",
        "-ExternalClientBaseUrl", $ExternalClientBaseUrl,
        "-SourcePackagePath", [string]$package.data.package,
        "-ExpectedSourcePackageSha256", [string]$package.data.sha256,
        "-ReleaseReceiptPath", "release/luam-binary-release-receipt.json",
        "-Json")
    $steps.Add($binary) | Out-Null

    $audit = Invoke-JsonScript -Name "release-surface-audit" -Arguments @(
        "-File", "Tools/audit_release_surface.ps1",
        "-PackagePath", "$($binary.data.clientPackage),$($binary.data.serverPackage)",
        "-Json")
    $steps.Add($audit) | Out-Null
    if ($audit.data.ok -ne $true -or [int64]$audit.data.violationCount -ne 0) {
        throw "Release surface audit did not pass."
    }

    $clientDryRun = Invoke-JsonScript -Name "client-static-dry-run" -Arguments @(
        "-File", "Tools/provision_monolith_client_static.ps1",
        "-ClientPackagePath", [string]$binary.data.clientPackage,
        "-ExpectedSha256", [string]$binary.data.clientSha256,
        "-Version", [string]$binary.data.buildVersion,
        "-ReleaseReceiptPath", [string]$binary.data.releaseReceipt,
        "-ExpectedReleaseReceiptSha256", [string]$binary.data.releaseReceiptSha256,
        "-DryRun")
    $steps.Add($clientDryRun) | Out-Null

    $serverDeployArgs = @(
        "-File", "Tools/deploy_luam_server_release.ps1",
        "-PackagePath", [string]$binary.data.serverPackage,
        "-ExpectedSha256", [string]$binary.data.serverSha256,
        "-ReleaseReceiptPath", [string]$binary.data.releaseReceipt,
        "-ExpectedReleaseReceiptSha256", [string]$binary.data.releaseReceiptSha256,
        "-Tag", $Tag,
        "-ConfigSourcePath", $ConfigSourcePath,
        "-RemoteConfigPath", $RemoteConfigPath,
        "-RemoteDataDir", $RemoteDataDir,
        "-RequireDataBackup")
    if ($Force) { $serverDeployArgs += "-Force" }
    if ($LegacyShipSaveBootstrap) { $serverDeployArgs += "-LegacyShipSaveBootstrap" }

    $serverDryRun = Invoke-JsonScript -Name "server-deploy-dry-run" -Arguments @($serverDeployArgs + "-DryRun")
    $steps.Add($serverDryRun) | Out-Null

    if ($Deploy) {
        $steps.Add((Invoke-PlainScript -Name "client-static-provision" -Arguments @(
            "-File", "Tools/provision_monolith_client_static.ps1",
            "-ClientPackagePath", [string]$binary.data.clientPackage,
            "-ExpectedSha256", [string]$binary.data.clientSha256,
            "-Version", [string]$binary.data.buildVersion,
            "-ReleaseReceiptPath", [string]$binary.data.releaseReceipt,
            "-ExpectedReleaseReceiptSha256", [string]$binary.data.releaseReceiptSha256))) | Out-Null

        $steps.Add((Invoke-PlainScript -Name "server-deploy" -Arguments $serverDeployArgs)) | Out-Null

        if ($FreezePolicyAfterDeploy) {
            Freeze-LuaMDeploymentPolicy
            $steps.Add([pscustomobject]@{
                name = "freeze-policy-after-deploy"
                status = "passed"
                finishedAtUtc = [DateTime]::UtcNow.ToString("o")
            }) | Out-Null
        }
    }
}
catch {
    $ok = $false
    $failure = $_.Exception.Message
}
finally {
    Pop-Location
}

$summary = [pscustomobject]@{
    ok = $ok
    phase = if ($Deploy -and $ok) { "deployed" } else { "production-gate" }
    tag = $Tag
    deploy = [bool]$Deploy
    force = [bool]$Force
    legacy_ship_save_bootstrap = [bool]$LegacyShipSaveBootstrap
    steps = @($steps.ToArray())
    failure = $failure
}

if ($ok) {
    Set-LuaMReleaseStage -Phase $summary.phase -Result $summary -Root $root | Out-Null
} else {
    Set-LuaMReleaseStage -Phase "failed" -Result $failure -Root $root | Out-Null
}

if ($Json) {
    $summary | ConvertTo-Json -Depth 12
} else {
    Write-Host "LuaM ship pipeline: $(if ($ok) { 'OK' } else { 'FAILED' })"
    Write-Host "Tag: $Tag"
    Write-Host "Deploy: $([bool]$Deploy)"
    Write-Host "Legacy ship-save bootstrap: $([bool]$LegacyShipSaveBootstrap)"
    foreach ($step in $steps) {
        Write-Host "[checked] $($step.name)"
    }
    if (-not $ok) {
        Write-Error $failure -ErrorAction Continue
    }
}

if (-not $ok) {
    exit 1
}
