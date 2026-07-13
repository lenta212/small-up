[CmdletBinding()]
param(
    [string]$SourcePath = "Tools\luam_ai_gateway.py",
    [Parameter(Mandatory = $true)]
    [string]$ExpectedSha256,
    [string]$Tag = "",
    [string]$SshTarget = "monolith-new",
    [string]$BaseDir = "/opt/monolith-ds",
    [string]$ServiceName = "luam-ai-gateway.service",
    [int]$PiperCacheSize = 2,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

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

function ConvertTo-ShellSingleQuoted {
    param([string]$Value)

    return "'" + ($Value -replace "'", "'\''") + "'"
}

function Invoke-RemoteBash {
    param([string]$Script)

    $normalizedScript = $Script -replace "`r`n", "`n" -replace "`r", "`n"
    $encoded = [Convert]::ToBase64String(([System.Text.UTF8Encoding]::new($false)).GetBytes($normalizedScript))
    Invoke-CheckedNative ssh $SshTarget "printf %s $encoded | base64 -d | bash"
}

if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
    throw "Gateway source not found: $SourcePath"
}

if ($SshTarget -notmatch '^[A-Za-z0-9_.@-]+$' -or $SshTarget.StartsWith('-')) {
    throw "SshTarget contains unsupported characters: $SshTarget"
}

if ($ServiceName -notmatch '^[A-Za-z0-9_.@-]+\.service$' -or $ServiceName.StartsWith('-')) {
    throw "ServiceName must be a plain systemd .service unit name: $ServiceName"
}

if ($PiperCacheSize -lt 1 -or $PiperCacheSize -gt 16) {
    throw "PiperCacheSize must be between 1 and 16. Got $PiperCacheSize."
}

$resolvedSource = (Resolve-Path -LiteralPath $SourcePath).Path
$actualSha = (Get-FileHash -LiteralPath $resolvedSource -Algorithm SHA256).Hash.ToLowerInvariant()
$expectedSha = $ExpectedSha256.Trim().ToLowerInvariant()
if ($actualSha -ne $expectedSha) {
    throw "Gateway SHA256 mismatch. Expected $expectedSha, got $actualSha"
}

if ([string]::IsNullOrWhiteSpace($Tag)) {
    $Tag = $actualSha.Substring(0, 12)
}
$Tag = ($Tag.Trim() -replace "[^A-Za-z0-9_.-]", "-").Trim("-")
if ([string]::IsNullOrWhiteSpace($Tag)) {
    throw "Gateway deployment tag is empty after sanitizing."
}
if ($Tag.Length -gt 80) {
    throw "Gateway deployment tag must not exceed 80 characters."
}

$baseDirNormalized = ($BaseDir.Trim() -replace "\\", "/").TrimEnd("/")
if ($baseDirNormalized -notmatch '^/[A-Za-z0-9._/-]+$' -or
    $baseDirNormalized -eq "/" -or
    $baseDirNormalized.Contains("//") -or
    @($baseDirNormalized.Split('/') | Where-Object { $_ -eq '.' -or $_ -eq '..' }).Count -gt 0) {
    throw "BaseDir must be a safe non-root absolute Linux path: $BaseDir"
}

$remoteStageDir = "$baseDirNormalized/deploy-staging"
$remoteStage = "$remoteStageDir/luam_ai_gateway-$Tag.py"
$remoteGateway = "$baseDirNormalized/ai-gateway/luam_ai_gateway.py"
$remoteEnv = "/etc/monolith-ds/ai-gateway.env"

$plan = [ordered]@{
    source = $resolvedSource
    sha256 = $actualSha
    ssh_target = $SshTarget
    remote_gateway = $remoteGateway
    remote_env = $remoteEnv
    service = $ServiceName
    piper_cache_size = $PiperCacheSize
    tag = $Tag
}

if ($DryRun) {
    $plan | ConvertTo-Json -Depth 4
    exit 0
}

Invoke-RemoteBash @"
set -euo pipefail
umask 077
mkdir -p -- $(ConvertTo-ShellSingleQuoted $remoteStageDir)
"@
Invoke-CheckedNative scp $resolvedSource "${SshTarget}:$remoteStage"

Invoke-RemoteBash @"
set -euo pipefail
stage=$(ConvertTo-ShellSingleQuoted $remoteStage)
gateway=$(ConvertTo-ShellSingleQuoted $remoteGateway)
env_file=$(ConvertTo-ShellSingleQuoted $remoteEnv)
service=$(ConvertTo-ShellSingleQuoted $ServiceName)
expected_sha=$(ConvertTo-ShellSingleQuoted $actualSha)
tag=$(ConvertTo-ShellSingleQuoted $Tag)
cache_size=$(ConvertTo-ShellSingleQuoted ([string]$PiperCacheSize))
python_bin=$(ConvertTo-ShellSingleQuoted "$baseDirNormalized/ai-gateway/piper-venv/bin/python")
backup_base=$(ConvertTo-ShellSingleQuoted "$baseDirNormalized/backups/ai-gateway-$Tag")

test -f "`$stage"
test -f "`$gateway"
test -f "`$env_file"
actual_sha=`$(sha256sum "`$stage" | awk '{print `$1}')
if [ "`$actual_sha" != "`$expected_sha" ]; then
  echo "Remote gateway SHA256 mismatch. Expected `$expected_sha, got `$actual_sha" >&2
  exit 41
fi

test -x "`$python_bin"
"`$python_bin" -m py_compile "`$stage"

if ! sudo systemctl is-active --quiet "`$service" ||
   ! curl -fsS http://127.0.0.1:8787/health >/dev/null; then
  echo "Gateway service is not healthy before deployment; refusing to mutate files." >&2
  exit 43
fi

backup_dir="`$backup_base"
backup_suffix=0
while sudo test -e "`$backup_dir"; do
  backup_suffix=`$((backup_suffix + 1))
  backup_dir="`$backup_base-`$backup_suffix"
done

sudo install -d -m 0700 "`$backup_dir"
sudo install -m 0600 "`$gateway" "`$backup_dir/luam_ai_gateway.py"
sudo install -m 0600 "`$env_file" "`$backup_dir/ai-gateway.env"

rollback_needed=0
rollback() {
  rc="`$1"
  trap - ERR

  if [ "`$rollback_needed" -eq 1 ]; then
    echo "Gateway deployment failed; rolling back from `$backup_dir" >&2
    recovery_failed=0
    sudo install -o monolith -g monolith -m 0644 "`$backup_dir/luam_ai_gateway.py" "`$gateway" || recovery_failed=1
    sudo install -m 0600 "`$backup_dir/ai-gateway.env" "`$env_file" || recovery_failed=1
    sudo systemctl restart "`$service" || recovery_failed=1

    healthy=0
    attempt=0
    while [ "`$attempt" -lt 15 ]; do
      if sudo systemctl is-active --quiet "`$service" &&
         curl -fsS http://127.0.0.1:8787/health >/dev/null; then
        healthy=1
        break
      fi
      attempt=`$((attempt + 1))
      sleep 2
    done
    if [ "`$healthy" -ne 1 ]; then
      recovery_failed=1
    fi

    if [ "`$recovery_failed" -ne 0 ]; then
      echo "Gateway rollback did not restore a healthy service." >&2
      exit 70
    fi
  fi

  exit "`$rc"
}
trap 'rollback `$?' ERR

rollback_needed=1
sudo install -o monolith -g monolith -m 0644 "`$stage" "`$gateway"
sudo python3 - "`$env_file" "`$cache_size" <<'PY'
import os
import pathlib
import tempfile
import sys

path = pathlib.Path(sys.argv[1])
value = sys.argv[2]
key = "LUAM_TTS_PIPER_CACHE_SIZE"
lines = path.read_text(encoding="utf-8").splitlines()
updated = []
replaced = False
for line in lines:
    if line.startswith(key + "="):
        if not replaced:
            updated.append(f"{key}={value}")
            replaced = True
        continue
    updated.append(line)
if not replaced:
    updated.append(f"{key}={value}")
fd, tmp_name = tempfile.mkstemp(prefix=path.name + ".", suffix=".tmp", dir=path.parent)
try:
    with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("\n".join(updated) + "\n")
    os.chmod(tmp_name, 0o600)
    os.replace(tmp_name, path)
except BaseException:
    try:
        os.unlink(tmp_name)
    except FileNotFoundError:
        pass
    raise
PY

sudo systemctl restart "`$service"
healthy=0
attempt=0
while [ "`$attempt" -lt 20 ]; do
  if sudo systemctl is-active --quiet "`$service" &&
     curl -fsS http://127.0.0.1:8787/health >/dev/null; then
    healthy=1
    break
  fi
  attempt=`$((attempt + 1))
  sleep 2
done
if [ "`$healthy" -ne 1 ]; then
  rollback 42
fi

rollback_needed=0
trap - ERR
rm -f -- "`$stage" || true
echo "gateway_backup=`$backup_dir"
echo "gateway_sha256=`$actual_sha"
echo "gateway_cache_size=`$cache_size"
"@

[pscustomobject]@{
    ok = $true
    source = $resolvedSource
    sha256 = $actualSha
    service = $ServiceName
    piper_cache_size = $PiperCacheSize
    tag = $Tag
} | ConvertTo-Json -Depth 4
