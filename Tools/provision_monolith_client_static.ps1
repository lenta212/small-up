[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ClientPackagePath,
    [Parameter(Mandatory = $true)]
    [string]$ExpectedSha256,
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [string]$ReleaseReceiptPath,
    [Parameter(Mandatory = $true)]
    [string]$ExpectedReleaseReceiptSha256,
    [string]$SshTarget = "monolith-new",
    [string]$BaseDir = "/opt/monolith-ds",
    [string]$PublicHost = "188.127.225.57",
    [int]$Port = 1213,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "luam_release_contract.ps1")
$releasePolicy = Read-LuaMReleasePolicy -Root $root
$releasePolicySha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot "luam_release_policy.json") -Algorithm SHA256).Hash.ToLowerInvariant()

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

    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    $normalizedScript = $Script -replace "`r`n", "`n" -replace "`r", "`n"
    $encoded = [Convert]::ToBase64String($utf8NoBom.GetBytes($normalizedScript))
    Invoke-CheckedNative ssh $SshTarget "printf %s $encoded | base64 -d | bash"
}

function Normalize-RemoteAbsolutePath {
    param(
        [string]$Path,
        [string]$Name
    )

    $normalized = ($Path.Trim() -replace "\\", "/").TrimEnd("/")
    if (-not $normalized.StartsWith("/") -or $normalized -eq "/") {
        throw "$Name must be a non-root absolute Linux path: $Path"
    }

    if ($normalized -notmatch "^/[A-Za-z0-9._/-]+$") {
        throw "$Name contains unsupported characters. Use a simple absolute Linux path: $Path"
    }

    $parts = $normalized.Substring(1).Split([char]'/', [System.StringSplitOptions]::None)
    if (@($parts | Where-Object { $_ -eq "" -or $_ -eq "." -or $_ -eq ".." }).Count -gt 0) {
        throw "$Name contains an empty, current-directory, or parent-directory segment: $Path"
    }

    return "/" + ($parts -join "/")
}

if (-not (Test-Path -LiteralPath $ClientPackagePath -PathType Leaf)) {
    throw "Client package not found: $ClientPackagePath"
}

if ($ExpectedSha256.Trim() -notmatch "^[0-9A-Fa-f]{64}$") {
    throw "ExpectedSha256 must contain exactly 64 hexadecimal characters."
}

if ($Version.Length -gt 128 -or $Version -notmatch "^[A-Za-z0-9][A-Za-z0-9_.-]*$") {
    throw "Version must be 1-128 characters, start with a letter or digit, and contain only letters, digits, dot, underscore, or hyphen."
}

if ($SshTarget -notmatch "^[A-Za-z0-9][A-Za-z0-9_.@:-]*$") {
    throw "SshTarget contains unsupported characters: '$SshTarget'"
}

$hostKind = [Uri]::CheckHostName($PublicHost)
if ($hostKind -eq [UriHostNameType]::Unknown) {
    throw "PublicHost must be a DNS name or IP address without scheme or path: '$PublicHost'"
}

if ($Port -lt 1024 -or $Port -gt 65535) {
    throw "Port must be between 1024 and 65535. Got $Port."
}

$resolvedPackage = (Resolve-Path -LiteralPath $ClientPackagePath).Path
$actualSha = (Get-FileHash -LiteralPath $resolvedPackage -Algorithm SHA256).Hash.ToLowerInvariant()
$expectedSha = $ExpectedSha256.Trim().ToLowerInvariant()
if ($actualSha -ne $expectedSha) {
    throw "Client SHA256 mismatch. Expected $expectedSha, got $actualSha"
}

$baseDirNormalized = Normalize-RemoteAbsolutePath -Path $BaseDir -Name "BaseDir"

$remoteStageDir = "$baseDirNormalized/deploy-staging"
$remoteStagePackage = "$remoteStageDir/SS14.Client-$Version.zip"
$clientRoot = "/var/www/monolith-client"
$publicHostForUrl = if ($hostKind -eq [UriHostNameType]::IPv6) { "[$PublicHost]" } else { $PublicHost }
$downloadUrl = "http://${publicHostForUrl}:$Port/$Version/SS14.Client.zip"
$packageBytes = (Get-Item -LiteralPath $resolvedPackage).Length
$releaseReceipt = Read-LuaMBinaryReleaseReceipt -Path $ReleaseReceiptPath -ExpectedSha256 $ExpectedReleaseReceiptSha256
if (-not ([string]$releaseReceipt.policySha256).Equals($releasePolicySha256, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Binary release receipt policy SHA256 does not match the active local policy."
}
if (-not ([string]$releaseReceipt.client.fileName).Equals([IO.Path]::GetFileName($resolvedPackage), [StringComparison]::Ordinal) -or
    -not ([string]$releaseReceipt.client.sha256).Equals($actualSha, [StringComparison]::OrdinalIgnoreCase) -or
    [int64]$releaseReceipt.client.bytes -ne [int64]$packageBytes) {
    throw "Client package name/hash/size does not match the binary release receipt."
}
if ($releaseReceipt.delivery.mode -ne 'external-zip' -or
    -not ([string]$releaseReceipt.delivery.clientDownloadUrl).Equals($downloadUrl, [StringComparison]::Ordinal)) {
    throw "Static client URL does not match the receipt-bound external client delivery URL."
}

# A successful child PowerShell script does not necessarily initialize the
# native-process exit code under Windows PowerShell strict mode.
$global:LASTEXITCODE = 0
$auditOutput = @(& (Join-Path $PSScriptRoot "audit_release_surface.ps1") -PackagePath $resolvedPackage -ExpectedPackageKind client -AllowPartial -Json)
if ($global:LASTEXITCODE -ne 0) {
    throw "Client release surface audit failed before static provisioning."
}
$clientSurfaceAudit = ($auditOutput -join "`n") | ConvertFrom-Json
$clientAudit = @($clientSurfaceAudit.packages | Where-Object { $_.kind -eq 'client' }) | Select-Object -First 1
if ($clientSurfaceAudit.ok -ne $true -or [int64]$clientSurfaceAudit.violationCount -ne 0 -or
    $null -eq $clientAudit -or $clientAudit.clientServerOnlyAbsent -ne $true) {
    throw "Client package lacks fresh passing privacy/surface-audit evidence."
}

$plan = [ordered]@{
    client_package = $resolvedPackage
    client_sha256 = $actualSha
    client_bytes = $packageBytes
    version = $Version
    ssh_target = $SshTarget
    remote_stage = $remoteStagePackage
    client_root = $clientRoot
    download_url = $downloadUrl
    nginx_port = $Port
    release_receipt = (Resolve-Path -LiteralPath $ReleaseReceiptPath).Path
    release_receipt_sha256 = $ExpectedReleaseReceiptSha256.Trim().ToLowerInvariant()
    source_payload_digest_sha256 = [string]$releaseReceipt.sourcePackage.payloadDigestSha256
    surface_audit_passed = [bool]$clientSurfaceAudit.ok
    server_only_absent = [bool]$clientAudit.clientServerOnlyAbsent
    freeze_policy = [pscustomobject]@{
        active = [bool]$releasePolicy.remoteDeployFrozen
        detail = [string]$releasePolicy.reason
        authorization = $releasePolicy.deploymentAuthorization
    }
}

if ($DryRun) {
    $plan | ConvertTo-Json -Depth 4
    exit 0
}

$mutationPolicy = Read-LuaMReleasePolicy -Root $root
$mutationPolicySha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot "luam_release_policy.json") -Algorithm SHA256).Hash.ToLowerInvariant()
if (-not $releasePolicySha256.Equals($mutationPolicySha256, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Release policy changed after receipt validation; refusing remote mutation."
}
Assert-LuaMRemoteMutationAllowed -Policy $mutationPolicy -Mutation 'client-static'

Invoke-RemoteBash @"
set -euo pipefail
mkdir -p $(ConvertTo-ShellSingleQuoted $remoteStageDir)
chmod 700 $(ConvertTo-ShellSingleQuoted $remoteStageDir)
"@

Invoke-CheckedNative scp $resolvedPackage "${SshTarget}:$remoteStagePackage"

$nginxConfig = @"
server {
    listen $Port;
    listen [::]:$Port;
    server_name _;

    root $clientRoot;
    sendfile on;
    tcp_nopush on;
    access_log off;

    location / {
        limit_except GET HEAD { deny all; }
        try_files `$uri =404;
        add_header Cache-Control "public, max-age=31536000, immutable" always;
        add_header X-Content-Type-Options "nosniff" always;
    }
}
"@
$nginxConfig = $nginxConfig -replace "`r`n", "`n" -replace "`r", "`n"
$nginxConfigB64 = [Convert]::ToBase64String(([System.Text.UTF8Encoding]::new($false)).GetBytes($nginxConfig))

Invoke-RemoteBash @"
set -euo pipefail
stage_package=$(ConvertTo-ShellSingleQuoted $remoteStagePackage)
expected_sha=$(ConvertTo-ShellSingleQuoted $actualSha)
version=$(ConvertTo-ShellSingleQuoted $Version)
client_root=$(ConvertTo-ShellSingleQuoted $clientRoot)
port=$(ConvertTo-ShellSingleQuoted ([string]$Port))
expected_bytes=$(ConvertTo-ShellSingleQuoted ([string]$packageBytes))
client_path="`$client_root/`$version/SS14.Client.zip"
nginx_available=/etc/nginx/sites-available/monolith-client-files
nginx_enabled=/etc/nginx/sites-enabled/monolith-client-files
nginx_candidate="/tmp/monolith-client-files.nginx.`$`$"
nginx_backup_dir=`$(mktemp -d /tmp/monolith-nginx-backup.XXXXXX)
nginx_was_active=`$(systemctl is-active nginx 2>/dev/null || true)
nginx_was_enabled=`$(systemctl is-enabled nginx 2>/dev/null || true)
had_nginx_available=0
had_nginx_enabled=0
client_created=0
nginx_changed=0
nginx_install_attempted=0
committed=0

cleanup() {
  result=`$?
  trap - EXIT
  rm -f -- "`$stage_package" "`$nginx_candidate"

  if [ "`$committed" != "1" ]; then
    if [ "`$nginx_changed" = "1" ]; then
      sudo rm -f -- "`$nginx_available" "`$nginx_enabled"
      if [ "`$had_nginx_available" = "1" ]; then
        sudo cp -a -- "`$nginx_backup_dir/available" "`$nginx_available"
      fi
      if [ "`$had_nginx_enabled" = "1" ]; then
        sudo cp -a -- "`$nginx_backup_dir/enabled" "`$nginx_enabled"
      fi
      if sudo nginx -t >/dev/null 2>&1; then
        if [ "`$nginx_was_active" = "active" ]; then
          sudo systemctl reload nginx || true
        else
          sudo systemctl stop nginx || true
        fi
      fi
      if [ "`$nginx_was_enabled" != "enabled" ]; then
        sudo systemctl disable nginx >/dev/null 2>&1 || true
      fi
    fi

    if [ "`$nginx_install_attempted" = "1" ] && [ "`$nginx_changed" != "1" ]; then
      if [ "`$nginx_was_active" != "active" ]; then
        sudo systemctl stop nginx >/dev/null 2>&1 || true
      fi
      if [ "`$nginx_was_enabled" != "enabled" ]; then
        sudo systemctl disable nginx >/dev/null 2>&1 || true
      fi
    fi

    if [ "`$client_created" = "1" ]; then
      sudo rm -f -- "`$client_path"
      sudo rmdir -- "`$client_root/`$version" 2>/dev/null || true
    fi
  fi

  sudo rm -rf -- "`$nginx_backup_dir"
  exit "`$result"
}
trap cleanup EXIT

test -f "`$stage_package"
actual_sha=`$(sha256sum "`$stage_package" | awk '{print `$1}')
if [ "`$actual_sha" != "`$expected_sha" ]; then
  echo "Remote client SHA256 mismatch. Expected `$expected_sha, got `$actual_sha" >&2
  exit 31
fi

echo "client-static-step=install-client"
sudo install -d -m 0755 "`$client_root/`$version"
if sudo test -f "`$client_path"; then
  existing_sha=`$(sudo sha256sum "`$client_path" | awk '{print `$1}')
  if [ "`$existing_sha" != "`$expected_sha" ]; then
    echo "Refusing to overwrite immutable client version '`$version'. Existing SHA256 `$existing_sha differs from `$expected_sha." >&2
    exit 34
  fi
else
  sudo install -m 0644 "`$stage_package" "`$client_path"
  client_created=1
fi
installed_sha=`$(sha256sum "`$client_path" | awk '{print `$1}')
if [ "`$installed_sha" != "`$expected_sha" ]; then
  echo "Installed client SHA256 mismatch. Expected `$expected_sha, got `$installed_sha" >&2
  exit 32
fi

if ! command -v nginx >/dev/null 2>&1; then
  echo "client-static-step=install-nginx"
  nginx_install_attempted=1
  sudo env DEBIAN_FRONTEND=noninteractive apt-get update
  sudo env DEBIAN_FRONTEND=noninteractive apt-get install -y nginx
fi

echo "client-static-step=configure-nginx"
if sudo test -e "`$nginx_available" || sudo test -L "`$nginx_available"; then
  sudo cp -a -- "`$nginx_available" "`$nginx_backup_dir/available"
  had_nginx_available=1
fi
if sudo test -e "`$nginx_enabled" || sudo test -L "`$nginx_enabled"; then
  sudo cp -a -- "`$nginx_enabled" "`$nginx_backup_dir/enabled"
  had_nginx_enabled=1
fi
printf %s $(ConvertTo-ShellSingleQuoted $nginxConfigB64) | base64 -d > "`$nginx_candidate"
nginx_changed=1
sudo install -m 0644 "`$nginx_candidate" "`$nginx_available"
sudo ln -sfn "`$nginx_available" "`$nginx_enabled"
sudo nginx -t
sudo systemctl enable --now nginx
sudo systemctl reload nginx

echo "client-static-step=verify"
installed_bytes=`$(stat -c %s "`$client_path")
if [ "`$installed_bytes" != "`$expected_bytes" ]; then
  echo "Installed client size mismatch. Expected `$expected_bytes, got `$installed_bytes" >&2
  exit 33
fi
curl -fsSI "http://127.0.0.1:`$port/`$version/SS14.Client.zip" >/dev/null

if command -v ufw >/dev/null 2>&1; then
  sudo ufw allow "`$port/tcp" comment 'Monolith client files'
fi

committed=1
echo "client_static_sha256=`$installed_sha"
echo "client_static_bytes=`$installed_bytes"
"@

$response = $null
$lastHeadError = $null
for ($attempt = 1; $attempt -le 5; $attempt += 1) {
    try {
        $response = Invoke-WebRequest -Uri $downloadUrl -Method Head -TimeoutSec 20 -UseBasicParsing
        if ([int]$response.StatusCode -eq 200) {
            break
        }

        $lastHeadError = "HTTP $([int]$response.StatusCode)"
    }
    catch {
        $lastHeadError = $_.Exception.Message
    }

    if ($attempt -lt 5) {
        Start-Sleep -Seconds 2
    }
}

if ($null -eq $response -or [int]$response.StatusCode -ne 200) {
    throw "External client URL did not become reachable after 5 attempts: $downloadUrl ($lastHeadError)"
}

[pscustomobject]@{
    ok = $true
    client_sha256 = $actualSha
    client_bytes = $packageBytes
    version = $Version
    download_url = $downloadUrl
    http_status = [int]$response.StatusCode
} | ConvertTo-Json -Depth 4
