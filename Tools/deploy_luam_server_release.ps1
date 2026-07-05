param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,
    [string]$StatusUrl = "http://188.127.225.57:1212/status",
    [string]$SshTarget = "monolith-new",
    [string]$BaseDir = "/opt/monolith-ds",
    [string]$ServiceName = "monolith-ds.service",
    [string]$ExpectedSha256 = "",
    [string]$Tag = "",
    [int]$PollSeconds = 60,
    [switch]$Wait,
    [switch]$Force,
    [switch]$DryRun,
    [switch]$SkipPostVerify,
    [switch]$AllowClientZipRestore
)

$ErrorActionPreference = "Stop"

function ConvertTo-ShellSingleQuoted {
    param([string]$Value)

    return "'" + ($Value -replace "'", "'\''") + "'"
}

function Invoke-CheckedNative {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]$NativeArgs
    )

    & $FilePath @NativeArgs
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE"
    }
}

function Get-MonolithStatus {
    Invoke-RestMethod -Uri $StatusUrl -TimeoutSec 10
}

function Wait-ForEmptyServer {
    do {
        try {
            $status = Get-MonolithStatus
        }
        catch {
            if ($Force) {
                Write-Warning "Status endpoint unavailable; deploying with -Force anyway."
                return [pscustomobject]@{
                    players = 0
                    round_id = "unavailable"
                    run_level = "unavailable"
                    map = "unavailable"
                    preset = "unavailable"
                }
            }

            throw
        }

        $players = [int]$status.players
        Write-Host ("players={0} round_id={1} run_level={2} map='{3}' preset='{4}'" -f $players, $status.round_id, $status.run_level, $status.map, $status.preset)

        if ($Force -or $players -eq 0) {
            if ($Force -and $players -gt 0) {
                Write-Warning "Force was set; deploying while players are online."
            }

            return $status
        }

        if (-not $Wait) {
            throw "Online is not zero; not deploying. Re-run with -Wait to poll, or -Force to override."
        }

        Write-Host "Online is not zero; waiting $PollSeconds seconds..."
        Start-Sleep -Seconds $PollSeconds
    } while ($true)
}

function Invoke-RemoteBash {
    param([string]$Script)

    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    $encoded = [Convert]::ToBase64String($utf8NoBom.GetBytes($Script))
    Invoke-CheckedNative ssh $SshTarget "printf %s $encoded | base64 -d | bash"
}

function Get-RemoteFreezePolicy {
    $manifestPath = Join-Path $PSScriptRoot "luam_release_manifest.md"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        return [pscustomobject]@{
            active = $true
            manifest = $manifestPath
            detail = "LuaM release manifest is missing; refusing remote deploy."
        }
    }

    $manifestText = Get-Content -LiteralPath $manifestPath -Raw
    $active = $manifestText -match "(?i)Current policy:\s*do not upload, restart, or update the remote server until the server freeze is lifted"
    $detail = if ($active) {
        "Current manifest policy forbids upload, restart, or remote update until the server freeze is lifted."
    } else {
        "No active remote freeze policy found in the manifest."
    }

    return [pscustomobject]@{
        active = $active
        manifest = $manifestPath
        detail = $detail
    }
}

function Test-ZipEntry {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ArchivePath,
        [Parameter(Mandatory = $true)]
        [string]$EntryName
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        return $null -ne ($archive.Entries | Where-Object { $_.FullName -eq $EntryName } | Select-Object -First 1)
    }
    finally {
        $archive.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
    throw "Package not found: $PackagePath"
}

$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$packageName = [System.IO.Path]::GetFileName($resolvedPackage)
if (-not $packageName.EndsWith(".zip", [StringComparison]::OrdinalIgnoreCase)) {
    throw "Package must be a .zip file: $resolvedPackage"
}

$hasRobustServer = Test-ZipEntry -ArchivePath $resolvedPackage -EntryName "Robust.Server"
$hasClientZip = Test-ZipEntry -ArchivePath $resolvedPackage -EntryName "Content.Client.zip"
if (-not $hasRobustServer) {
    throw "Package is missing required Robust.Server entry: $resolvedPackage"
}

if (-not $hasClientZip -and -not $AllowClientZipRestore) {
    throw "Package is missing Content.Client.zip. Rebuild with Tools\build_luam_server_release.ps1 or pass -AllowClientZipRestore for emergency rollback-style deploys."
}

$localHash = (Get-FileHash -LiteralPath $resolvedPackage -Algorithm SHA256).Hash.ToLowerInvariant()
if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256) -and
    $localHash -ne $ExpectedSha256.Trim().ToLowerInvariant()) {
    throw "Local package SHA256 mismatch. Expected $ExpectedSha256, got $localHash"
}

if ([string]::IsNullOrWhiteSpace($Tag)) {
    $Tag = [System.IO.Path]::GetFileNameWithoutExtension($packageName)
}

$Tag = ($Tag.Trim() -replace "[^A-Za-z0-9_.-]", "-").Trim("-")
if ([string]::IsNullOrWhiteSpace($Tag)) {
    throw "Deployment tag is empty after sanitizing."
}

$remoteZip = "$BaseDir/deploy-staging/$packageName"
$freezePolicy = Get-RemoteFreezePolicy
$plan = [ordered]@{
    package = $resolvedPackage
    package_sha256 = $localHash
    expected_sha256 = $ExpectedSha256
    package_has_robust_server = $hasRobustServer
    package_has_client_zip = $hasClientZip
    freeze_policy = $freezePolicy
    ssh_target = $SshTarget
    remote_zip = $remoteZip
    tag = $Tag
    service = $ServiceName
    base_dir = $BaseDir
}

if ($DryRun) {
    $plan | ConvertTo-Json -Depth 4
    exit 0
}

if ($freezePolicy.active) {
    throw $freezePolicy.detail
}

if ([string]::IsNullOrWhiteSpace($ExpectedSha256)) {
    throw "ExpectedSha256 is required for remote deployment. Re-run with the verified server package SHA256."
}

$preStatus = Wait-ForEmptyServer

Invoke-RemoteBash @"
set -euo pipefail
mkdir -p $(ConvertTo-ShellSingleQuoted "$BaseDir/deploy-staging")
"@

Invoke-CheckedNative scp $resolvedPackage "${SshTarget}:$remoteZip"

$forceValue = if ($Force) { "1" } else { "0" }
$skipPostVerifyValue = if ($SkipPostVerify) { "1" } else { "0" }
$allowClientZipRestoreValue = if ($AllowClientZipRestore) { "1" } else { "0" }
$remoteScript = @"
set -euo pipefail

base_dir=$(ConvertTo-ShellSingleQuoted $BaseDir)
service_name=$(ConvertTo-ShellSingleQuoted $ServiceName)
zip_path=$(ConvertTo-ShellSingleQuoted $remoteZip)
expected_sha=$(ConvertTo-ShellSingleQuoted $localHash)
tag=$(ConvertTo-ShellSingleQuoted $Tag)
force_deploy=$forceValue
skip_post_verify=$skipPostVerifyValue
allow_client_zip_restore=$allowClientZipRestoreValue

server_dir="`$base_dir/server"
stage_dir="`$base_dir/deploy-staging/server-`$tag"
backup_dir="`$base_dir/backups/server-`$tag"
failed_dir="`$base_dir/backups/server-`$tag-failed"
config_backup="`$base_dir/backups/server_config-before-`$tag.toml"

if [ ! -f "`$zip_path" ]; then
  echo "Package not found on remote: `$zip_path" >&2
  exit 10
fi

actual_sha=`$(sha256sum "`$zip_path" | awk '{print `$1}')
if [ "`$actual_sha" != "`$expected_sha" ]; then
  echo "Remote SHA256 mismatch. Expected `$expected_sha, got `$actual_sha" >&2
  exit 11
fi

if [ "`$force_deploy" != "1" ]; then
  players=`$(python3 - <<'PY'
import json
import urllib.request

with urllib.request.urlopen("http://127.0.0.1:1212/status", timeout=10) as response:
    print(json.load(response).get("players", -1))
PY
)
  if [ "`$players" != "0" ]; then
    echo "ABORT players=`$players" >&2
    exit 12
  fi
fi

sudo rm -rf "`$stage_dir"
mkdir -p "`$stage_dir"
echo "deploy-step=extract"
python3 - "`$zip_path" "`$stage_dir" <<'PY'
import os
import pathlib
import sys
import zipfile

zip_path = sys.argv[1]
stage_dir = pathlib.Path(sys.argv[2])

with zipfile.ZipFile(zip_path) as archive:
    for info in archive.infolist():
        name = info.filename.replace('\\', '/')
        if not name or name.endswith('/'):
            continue
        target = stage_dir / name
        target.parent.mkdir(parents=True, exist_ok=True)
        with archive.open(info) as source, open(target, 'wb') as dest:
            dest.write(source.read())
PY

echo "deploy-step=verify-extract"
test -f "`$stage_dir/Robust.Server"
if [ ! -f "`$stage_dir/Content.Client.zip" ]; then
  echo "deploy-step=restore-client-zip"
  if [ "`$allow_client_zip_restore" = "1" ]; then
    cp "`$server_dir/Content.Client.zip" "`$stage_dir/Content.Client.zip"
  else
    echo "Package is missing Content.Client.zip. Rebuild with Tools\\build_luam_server_release.ps1 or pass -AllowClientZipRestore for emergency rollback-style deploys." >&2
    exit 14
  fi
fi
test -f "`$stage_dir/Content.Client.zip"

echo "deploy-step=chmod-stage"
chmod 755 "`$stage_dir/Robust.Server"
if [ -f "`$stage_dir/Robust.Packaging" ]; then
  chmod 755 "`$stage_dir/Robust.Packaging"
fi

echo "deploy-step=copy-config"
sudo cp "`$server_dir/server_config.toml" "`$config_backup"
sudo cp "`$server_dir/server_config.toml" "`$stage_dir/server_config.toml"
sudo chown -R monolith:monolith "`$stage_dir"

if [ -e "`$backup_dir" ]; then
  backup_dir="`$backup_dir-`$(date +%H%M%S)"
fi

if [ -e "`$failed_dir" ]; then
  failed_dir="`$failed_dir-`$(date +%H%M%S)"
fi

rollback() {
  echo "Start verification failed; rolling back to `$backup_dir" >&2
  sudo systemctl stop "`$service_name" || true
  if [ -d "`$server_dir" ]; then
    sudo mv "`$server_dir" "`$failed_dir"
  fi
  sudo mv "`$backup_dir" "`$server_dir"
  sudo chmod 755 "`$server_dir/Robust.Server" || true
  if [ -f "`$server_dir/Robust.Packaging" ]; then
    sudo chmod 755 "`$server_dir/Robust.Packaging" || true
  fi
  sudo chown -R monolith:monolith "`$server_dir"
  sudo systemctl start "`$service_name" || true
}

echo "deploy-step=stop-service"
sudo systemctl stop "`$service_name"
echo "deploy-step=swap-server"
sudo mv "`$server_dir" "`$backup_dir"
sudo mv "`$stage_dir" "`$server_dir"
echo "deploy-step=chmod-server"
sudo chmod 755 "`$server_dir/Robust.Server"
if [ -f "`$server_dir/Robust.Packaging" ]; then
  sudo chmod 755 "`$server_dir/Robust.Packaging"
fi
sudo chown -R monolith:monolith "`$server_dir"
echo "deploy-step=start-service"
sudo systemctl start "`$service_name"

if [ "`$skip_post_verify" != "1" ]; then
  sleep 20
  state=`$(systemctl is-active "`$service_name")
  if [ "`$state" != "active" ]; then
    sudo systemctl status "`$service_name" --no-pager -l || true
    rollback
    exit 13
  fi
fi

echo "deployed_tag=`$tag"
echo "backup_dir=`$backup_dir"
echo "config_backup=`$config_backup"
sha256sum "`$server_dir/Content.Client.zip"
"@

Invoke-RemoteBash $remoteScript

if (-not $SkipPostVerify) {
    Start-Sleep -Seconds 10
    try {
        $postStatus = Get-MonolithStatus
        Write-Host ("post_status players={0} round_id={1} run_level={2} map='{3}' preset='{4}'" -f $postStatus.players, $postStatus.round_id, $postStatus.run_level, $postStatus.map, $postStatus.preset)
    }
    catch {
        Write-Warning "Post-verify status endpoint unavailable after restart; service start was already confirmed via systemd."
    }
}

[pscustomobject]@{
    ok = $true
    pre_status = $preStatus
    package = $resolvedPackage
    package_sha256 = $localHash
    remote_zip = $remoteZip
    tag = $Tag
} | ConvertTo-Json -Depth 6
