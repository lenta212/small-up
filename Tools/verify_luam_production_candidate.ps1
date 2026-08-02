param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$package = (Resolve-Path -LiteralPath $PackagePath -ErrorAction Stop).Path
$hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
& (Join-Path $PSScriptRoot "verify_luam_release_package.ps1") -PackagePath $package -ExpectedSha256 $hash

Write-Output "production-candidate=$package"
Write-Output "sha256=$hash"
Write-Output "deploy-policy=RequireDataBackup must be enabled; deploy script creates server/config backup and automatically rolls back failed health checks."
