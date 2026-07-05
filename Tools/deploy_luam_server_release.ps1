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
    [switch]$AllowClientZipRestore,
    [string]$RemoteConfigPath = "",
    [string]$RemoteDataDir = "",
    [switch]$RequireDataBackup
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

function Normalize-RemoteAbsolutePath {
    param(
        [string]$Path,
        [string]$Name = "Remote path"
    )

    $normalized = ($Path.Trim() -replace "\\", "/").TrimEnd("/")
    if ($normalized -eq "") {
        return ""
    }

    if (-not $normalized.StartsWith("/")) {
        throw "$Name must be an absolute Linux path: $Path"
    }

    if ($normalized -eq "/") {
        throw "$Name cannot be the filesystem root."
    }

    return $normalized
}

function Get-RemoteFreezePolicy {
    $policyPath = Join-Path $PSScriptRoot "luam_release_policy.json"
    $manifestPath = Join-Path $PSScriptRoot "luam_release_manifest.md"

    if (Test-Path -LiteralPath $policyPath -PathType Leaf) {
        try {
            $policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json -ErrorAction Stop
            $active = [bool]$policy.remoteDeployFrozen
            $reason = if ([string]::IsNullOrWhiteSpace($policy.reason)) {
                if ($active) {
                    "LuaM release policy freezes remote deployment."
                } else {
                    "LuaM release policy allows remote deployment."
                }
            } else {
                [string]$policy.reason
            }

            return [pscustomobject]@{
                active = $active
                source = "json"
                policy = $policyPath
                manifest = $manifestPath
                detail = $reason
                last_reviewed = $policy.lastReviewed
                required_checks = @($policy.requiredChecksBeforeDeploy)
            }
        }
        catch {
            return [pscustomobject]@{
                active = $true
                source = "json"
                policy = $policyPath
                manifest = $manifestPath
                detail = "LuaM release policy is invalid; refusing remote deploy. $($_.Exception.Message)"
            }
        }
    }

    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        return [pscustomobject]@{
            active = $true
            source = "manifest"
            policy = $policyPath
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
        source = "manifest"
        policy = $policyPath
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

function Assert-SafeZipEntries {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ArchivePath,
        [int]$MaxEntries = 250000,
        [int64]$MaxUncompressedBytes = 5GB
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entryCount = 0
        $totalBytes = [int64]0
        foreach ($entry in $archive.Entries) {
            if ([string]::IsNullOrWhiteSpace($entry.FullName) -or $entry.FullName.EndsWith("/")) {
                continue
            }

            $entryCount += 1
            if ($entryCount -gt $MaxEntries) {
                throw "Package has too many entries: $entryCount > $MaxEntries"
            }

            $totalBytes += [int64]$entry.Length
            if ($totalBytes -gt $MaxUncompressedBytes) {
                throw "Package uncompressed size exceeds limit: $totalBytes > $MaxUncompressedBytes"
            }

            $normalized = ($entry.FullName -replace "\\", "/")
            if ($normalized.StartsWith("/") -or $normalized -match "^[A-Za-z]:") {
                throw "Package contains an absolute path entry: $($entry.FullName)"
            }

            foreach ($part in $normalized.Split([char[]]@('/'), [System.StringSplitOptions]::RemoveEmptyEntries)) {
                if ($part -eq "." -or $part -eq ".." -or $part.Contains(":")) {
                    throw "Package contains an unsafe path entry: $($entry.FullName)"
                }
            }
        }
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

Assert-SafeZipEntries -ArchivePath $resolvedPackage

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

$normalizedBaseDir = Normalize-RemoteAbsolutePath -Path $BaseDir -Name "BaseDir"
if ([string]::IsNullOrWhiteSpace($normalizedBaseDir)) {
    throw "BaseDir cannot be empty."
}

$BaseDir = $normalizedBaseDir
$serverConfigDefaultPath = "$BaseDir/server/server_config.toml"
$normalizedRemoteConfigPath = if ([string]::IsNullOrWhiteSpace($RemoteConfigPath)) {
    $serverConfigDefaultPath
} else {
    Normalize-RemoteAbsolutePath -Path $RemoteConfigPath -Name "RemoteConfigPath"
}

if ($normalizedRemoteConfigPath -in @($normalizedBaseDir, "$normalizedBaseDir/server", "$normalizedBaseDir/deploy-staging", "$normalizedBaseDir/backups")) {
    throw "RemoteConfigPath points at a deploy/control directory instead of server_config.toml: $normalizedRemoteConfigPath"
}

$normalizedRemoteDataDir = Normalize-RemoteAbsolutePath -Path $RemoteDataDir -Name "RemoteDataDir"
if ($RequireDataBackup -and [string]::IsNullOrWhiteSpace($normalizedRemoteDataDir)) {
    throw "RequireDataBackup was set, but RemoteDataDir is empty. Pass the live server data directory explicitly."
}

if (-not [string]::IsNullOrWhiteSpace($normalizedRemoteDataDir)) {
    $forbiddenDataDirs = @(
        $normalizedBaseDir,
        "$normalizedBaseDir/server",
        "$normalizedBaseDir/deploy-staging",
        "$normalizedBaseDir/backups"
    )
    if ($forbiddenDataDirs -contains $normalizedRemoteDataDir) {
        throw "RemoteDataDir points at a deploy/control directory instead of a server data directory: $normalizedRemoteDataDir"
    }
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
    remote_config_path = $normalizedRemoteConfigPath
    remote_data_dir = $normalizedRemoteDataDir
    require_data_backup = [bool]$RequireDataBackup
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
$requireDataBackupValue = if ($RequireDataBackup) { "1" } else { "0" }
$remoteScript = @"
set -euo pipefail

base_dir=$(ConvertTo-ShellSingleQuoted $BaseDir)
service_name=$(ConvertTo-ShellSingleQuoted $ServiceName)
zip_path=$(ConvertTo-ShellSingleQuoted $remoteZip)
expected_sha=$(ConvertTo-ShellSingleQuoted $localHash)
tag=$(ConvertTo-ShellSingleQuoted $Tag)
remote_config_path=$(ConvertTo-ShellSingleQuoted $normalizedRemoteConfigPath)
remote_data_dir=$(ConvertTo-ShellSingleQuoted $normalizedRemoteDataDir)
force_deploy=$forceValue
skip_post_verify=$skipPostVerifyValue
allow_client_zip_restore=$allowClientZipRestoreValue
require_data_backup=$requireDataBackupValue

server_dir="`$base_dir/server"
stage_dir="`$base_dir/deploy-staging/server-`$tag"
backup_dir="`$base_dir/backups/server-`$tag"
failed_dir="`$base_dir/backups/server-`$tag-failed"
config_backup="`$base_dir/backups/server_config-before-`$tag.toml"
config_sha256=""
data_backup=""

if [ -z "`$remote_config_path" ]; then
  echo "Remote config path is empty." >&2
  exit 20
fi

case "`$remote_config_path" in
  /*) ;;
  *)
    echo "Remote config path must be absolute: `$remote_config_path" >&2
    exit 21
    ;;
esac

if [ "`$remote_config_path" = "/" ] || [ "`$remote_config_path" = "`$base_dir" ] || [ "`$remote_config_path" = "`$server_dir" ] || [ "`$remote_config_path" = "`$base_dir/deploy-staging" ] || [ "`$remote_config_path" = "`$base_dir/backups" ]; then
  echo "Remote config path points at a deploy/control directory: `$remote_config_path" >&2
  exit 22
fi

if [ ! -f "`$remote_config_path" ]; then
  echo "Remote server config not found: `$remote_config_path" >&2
  exit 23
fi

config_sha256=`$(sudo sha256sum "`$remote_config_path" | awk '{print `$1}')

if [ "`$require_data_backup" = "1" ] && [ -z "`$remote_data_dir" ]; then
  echo "Data backup is required, but remote_data_dir is empty." >&2
  exit 15
fi

if [ -n "`$remote_data_dir" ]; then
  case "`$remote_data_dir" in
    /*) ;;
    *)
      echo "Remote data dir must be absolute: `$remote_data_dir" >&2
      exit 16
      ;;
  esac

  if [ "`$remote_data_dir" = "/" ] || [ "`$remote_data_dir" = "`$base_dir" ] || [ "`$remote_data_dir" = "`$server_dir" ] || [ "`$remote_data_dir" = "`$base_dir/deploy-staging" ] || [ "`$remote_data_dir" = "`$base_dir/backups" ]; then
    echo "Remote data dir points at a deploy/control directory: `$remote_data_dir" >&2
    exit 17
  fi
fi

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
stage_root = stage_dir.resolve()
max_entries = 250000
max_uncompressed_bytes = 5 * 1024 * 1024 * 1024

def safe_target_path(raw_name):
    name = raw_name.replace('\\', '/')
    if not name or name.endswith('/'):
        return None

    path = pathlib.PurePosixPath(name)
    if path.is_absolute():
        raise ValueError(f"absolute zip entry path: {raw_name!r}")

    if any(part in ('', '.', '..') or ':' in part for part in path.parts):
        raise ValueError(f"unsafe zip entry path: {raw_name!r}")

    target = (stage_root / pathlib.Path(*path.parts)).resolve()
    try:
        target.relative_to(stage_root)
    except ValueError as exc:
        raise ValueError(f"zip entry escapes staging directory: {raw_name!r}") from exc

    return target

with zipfile.ZipFile(zip_path) as archive:
    total_bytes = 0
    entry_count = 0
    for info in archive.infolist():
        target = safe_target_path(info.filename)
        if target is None:
            continue

        entry_count += 1
        if entry_count > max_entries:
            raise SystemExit(f"Package has too many entries: {entry_count} > {max_entries}")

        total_bytes += info.file_size
        if total_bytes > max_uncompressed_bytes:
            raise SystemExit(f"Package uncompressed size exceeds limit: {total_bytes} > {max_uncompressed_bytes}")

        target.parent.mkdir(parents=True, exist_ok=True)
        with archive.open(info) as source, target.open('wb') as dest:
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
sudo cp "`$remote_config_path" "`$config_backup"
sudo cp "`$remote_config_path" "`$stage_dir/server_config.toml"
sudo chown -R monolith:monolith "`$stage_dir"

if [ -e "`$backup_dir" ]; then
  backup_dir="`$backup_dir-`$(date +%H%M%S)"
fi

if [ -e "`$failed_dir" ]; then
  failed_dir="`$failed_dir-`$(date +%H%M%S)"
fi

if [ -n "`$remote_data_dir" ]; then
  data_backup="`$base_dir/backups/data-`$tag.tar.gz"
  if [ -e "`$data_backup" ]; then
    data_backup="`$base_dir/backups/data-`$tag-`$(date +%H%M%S).tar.gz"
  fi
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
if [ -n "`$remote_data_dir" ]; then
  if [ ! -d "`$remote_data_dir" ]; then
    if [ "`$require_data_backup" = "1" ]; then
      echo "Required remote data dir does not exist: `$remote_data_dir" >&2
      sudo systemctl start "`$service_name" || true
      exit 18
    fi
    echo "deploy-step=backup-data-skipped-missing"
  else
    echo "deploy-step=backup-data"
    sudo tar -C "`$(dirname -- "`$remote_data_dir")" -czf "`$data_backup" "`$(basename -- "`$remote_data_dir")" || {
      sudo systemctl start "`$service_name" || true
      exit 19
    }
  fi
fi
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
echo "config_sha256=`$config_sha256"
if [ -n "`$data_backup" ]; then
  echo "data_backup=`$data_backup"
fi
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
    remote_config_path = $normalizedRemoteConfigPath
    remote_data_dir = $normalizedRemoteDataDir
    require_data_backup = [bool]$RequireDataBackup
} | ConvertTo-Json -Depth 6
