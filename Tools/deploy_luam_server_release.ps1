param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,
    [string]$StatusUrl = "http://188.127.225.57:1212/status",
    [string]$SshTarget = "monolith-new",
    [string]$BaseDir = "/opt/monolith-ds",
    [string]$ServiceName = "monolith-ds.service",
    [string]$ExpectedSha256 = "",
    [string]$ReleaseReceiptPath = "",
    [string]$ExpectedReleaseReceiptSha256 = "",
    [string]$Tag = "",
    [int]$PollSeconds = 60,
    [switch]$Wait,
    [switch]$Force,
    [switch]$DryRun,
    [switch]$SkipPostVerify,
    [switch]$AllowClientZipRestore,
    [string]$ConfigSourcePath = "",
    [string]$RemoteConfigPath = "",
    [string]$RemoteStatusUrl = "http://127.0.0.1:1212/status",
    [string]$RemoteInfoUrl = "http://127.0.0.1:1212/info",
    [string]$RemoteShipSaveUrl = "http://127.0.0.1:1212/admin/actions/maintenance/ship-save",
    [int]$ShipSaveReceiptMaxAgeSeconds = 300,
    [switch]$LegacyShipSaveBootstrap,
    [string]$RemoteDataDir = "",
    [switch]$RequireDataBackup
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "luam_release_contract.ps1")
$root = Split-Path -Parent $PSScriptRoot
$releasePolicy = Read-LuaMReleasePolicy -Root $root
$releasePolicySha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot "luam_release_policy.json") -Algorithm SHA256).Hash.ToLowerInvariant()

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
    $normalizedScript = $Script -replace "`r`n", "`n" -replace "`r", "`n"
    $encoded = [Convert]::ToBase64String($utf8NoBom.GetBytes($normalizedScript))
    $sshCommand = (Get-Command ssh -CommandType Application -ErrorAction Stop).Source
    $previousErrorActionPreference = $ErrorActionPreference
    $previousOutputEncoding = $OutputEncoding
    try {
        $ErrorActionPreference = "Continue"
        $OutputEncoding = $utf8NoBom
        $encoded | & $sshCommand $SshTarget "base64 --decode --ignore-garbage | bash"
        $exit = if ($null -eq $LASTEXITCODE) { 0 } else { $LASTEXITCODE }
    }
    finally {
        $OutputEncoding = $previousOutputEncoding
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($exit -ne 0) {
        throw "ssh failed with exit code $exit"
    }
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

    if ($normalized -notmatch "^/[A-Za-z0-9._/-]+$") {
        throw "$Name contains unsupported characters. Use a simple absolute Linux path: $Path"
    }

    $parts = $normalized.Substring(1).Split([char]'/', [System.StringSplitOptions]::None)
    if (@($parts | Where-Object { $_ -eq "" -or $_ -eq "." -or $_ -eq ".." }).Count -gt 0) {
        throw "$Name contains an empty, current-directory, or parent-directory segment: $Path"
    }

    return "/" + ($parts -join "/")
}

function Test-AbsoluteHttpUrl {
    param([string]$Value)

    $uri = $null
    return [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -and
        $uri.Scheme -in @("http", "https") -and
        -not [string]::IsNullOrWhiteSpace($uri.Host) -and
        [string]::IsNullOrWhiteSpace($uri.UserInfo)
}

function Test-LoopbackHttpUrl {
    param([string]$Value)

    $uri = $null
    return (Test-AbsoluteHttpUrl -Value $Value) -and
        [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -and
        $uri.IsLoopback
}

function Test-RemotePathWithin {
    param(
        [string]$Path,
        [string]$Directory
    )

    return $Path -eq $Directory -or $Path.StartsWith("$Directory/", [StringComparison]::Ordinal)
}

function Get-RemoteFreezePolicy {
    $policyPath = Join-Path $PSScriptRoot "luam_release_policy.json"
    $manifestPath = Join-Path $PSScriptRoot "luam_release_manifest.md"

    if (Test-Path -LiteralPath $policyPath -PathType Leaf) {
        try {
            $policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json -ErrorAction Stop
            Assert-LuaMReleasePolicy -Policy $policy
            if ($policy.PSObject.Properties.Name -notcontains "remoteDeployFrozen" -or
                $policy.remoteDeployFrozen -isnot [bool]) {
                throw "remoteDeployFrozen must be present as a JSON boolean."
            }

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
                authorization = $policy.deploymentAuthorization
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

    return [pscustomobject]@{
        active = $true
        source = "json"
        policy = $policyPath
        manifest = $manifestPath
        detail = "LuaM release policy is missing; refusing remote deploy."
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

function Get-ZipEntryText {
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
        $entry = $archive.Entries | Where-Object { $_.FullName -eq $EntryName } | Select-Object -First 1
        if ($null -eq $entry) {
            return $null
        }

        if ($entry.Length -gt 1MB) {
            throw "ZIP entry '$EntryName' is unexpectedly large: $($entry.Length) bytes"
        }

        $stream = $entry.Open()
        try {
            $reader = [System.IO.StreamReader]::new($stream, [System.Text.UTF8Encoding]::new($false, $true), $true)
            try {
                return $reader.ReadToEnd()
            }
            finally {
                $reader.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-ZipEntrySha256 {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$EntryName
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName -ceq $EntryName })
        if ($entries.Count -ne 1) {
            throw "Required unique ZIP entry '$EntryName' was not found in $ArchivePath"
        }
        $stream = $entries[0].Open()
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
        }
        finally {
            $sha.Dispose()
            $stream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-SafeZipEntries {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ArchivePath,
        [int]$MaxEntries = 100000,
        [int64]$MaxUncompressedBytes = 2GB,
        [int64]$MaxEntryBytes = 512MB
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entryCount = 0
        $totalBytes = [int64]0
        $seenPaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $filePaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $directoryPaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            $entryCount += 1
            if ($entryCount -gt $MaxEntries) {
                throw "Package has too many entries: $entryCount > $MaxEntries"
            }

            if ($entry.Length -lt 0 -or $entry.Length -gt $MaxEntryBytes) {
                throw "Package entry exceeds the per-entry limit of $MaxEntryBytes bytes: $($entry.FullName)"
            }
            $totalBytes += [int64]$entry.Length
            if ($totalBytes -gt $MaxUncompressedBytes) {
                throw "Package uncompressed size exceeds limit: $totalBytes > $MaxUncompressedBytes"
            }

            $normalized = ($entry.FullName -replace "\\", "/")
            $isDirectory = $normalized.EndsWith('/')
            if ($isDirectory) { $normalized = $normalized.Substring(0, $normalized.Length - 1) }
            if ($normalized -cne $normalized.Normalize([Text.NormalizationForm]::FormC)) {
                throw "Package entry is not Unicode NFC canonical: $($entry.FullName)"
            }
            if ($normalized.StartsWith("/") -or $normalized -match "^[A-Za-z]:") {
                throw "Package contains an absolute path entry: $($entry.FullName)"
            }

            $parts = $normalized.Split([char]'/', [System.StringSplitOptions]::None)
            foreach ($part in $parts) {
                $deviceBase = [IO.Path]::GetFileNameWithoutExtension($part)
                if ($part -eq "" -or $part -eq "." -or $part -eq ".." -or $part.Contains(":") -or
                    $part -match '[\x00-\x1F]' -or $part -ne $part.Trim() -or $part.EndsWith('.') -or
                    $deviceBase -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
                    throw "Package contains an unsafe path entry: $($entry.FullName)"
                }
            }

            $canonicalPath = $parts -join "/"
            if (-not $seenPaths.Add($canonicalPath)) {
                throw "Package contains a duplicate path entry: $($entry.FullName)"
            }
            $pathParts = $canonicalPath.Split('/')
            for ($index = 1; $index -lt $pathParts.Length; $index += 1) {
                $ancestor = $pathParts[0..($index - 1)] -join '/'
                if ($filePaths.Contains($ancestor)) { throw "Package treats file '$ancestor' as a directory prefix." }
                $directoryPaths.Add($ancestor) | Out-Null
            }
            if ($isDirectory) {
                if ($filePaths.Contains($canonicalPath)) { throw "Package contains a file/directory collision: $canonicalPath" }
                $directoryPaths.Add($canonicalPath) | Out-Null
            }
            else {
                if ($directoryPaths.Contains($canonicalPath)) { throw "Package contains a directory/file collision: $canonicalPath" }
                $filePaths.Add($canonicalPath) | Out-Null
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

if ([string]::IsNullOrWhiteSpace($ExpectedSha256) -or $ExpectedSha256.Trim() -notmatch "^[0-9A-Fa-f]{64}$") {
    throw "ExpectedSha256 is required and must contain exactly 64 hexadecimal characters."
}
if ([string]::IsNullOrWhiteSpace($ReleaseReceiptPath) -or [string]::IsNullOrWhiteSpace($ExpectedReleaseReceiptSha256)) {
    throw "ReleaseReceiptPath and ExpectedReleaseReceiptSha256 are required for server deployment."
}
if ($AllowClientZipRestore) {
    throw "AllowClientZipRestore cannot be used for a receipt-bound production deployment."
}
if ($SkipPostVerify) {
    throw "SkipPostVerify cannot be used for a receipt-bound production deployment."
}

if ($SshTarget -notmatch "^[A-Za-z0-9][A-Za-z0-9_.@:-]*$") {
    throw "SshTarget contains unsupported characters: '$SshTarget'"
}

if ($ServiceName -notmatch "^[A-Za-z0-9][A-Za-z0-9_.@:-]*\.service$") {
    throw "ServiceName must be a simple systemd .service unit name: '$ServiceName'"
}

if ($PollSeconds -lt 1) {
    throw "PollSeconds must be at least 1."
}

if ($ShipSaveReceiptMaxAgeSeconds -lt 30 -or $ShipSaveReceiptMaxAgeSeconds -gt 600) {
    throw "ShipSaveReceiptMaxAgeSeconds must be between 30 and 600 seconds."
}

if (-not (Test-AbsoluteHttpUrl -Value $StatusUrl) -or
    -not (Test-LoopbackHttpUrl -Value $RemoteStatusUrl) -or
    -not (Test-LoopbackHttpUrl -Value $RemoteInfoUrl) -or
    -not (Test-LoopbackHttpUrl -Value $RemoteShipSaveUrl)) {
    throw "StatusUrl must be HTTP(S); RemoteStatusUrl, RemoteInfoUrl, and RemoteShipSaveUrl must be loopback HTTP(S) URLs without credentials."
}

$shipSaveUri = [Uri]$RemoteShipSaveUrl
if ($shipSaveUri.AbsolutePath -cne "/admin/actions/maintenance/ship-save" -or
    -not [string]::IsNullOrEmpty($shipSaveUri.Query) -or
    -not [string]::IsNullOrEmpty($shipSaveUri.Fragment)) {
    throw "RemoteShipSaveUrl must use the exact maintenance path /admin/actions/maintenance/ship-save without a query or fragment."
}

$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$packageName = [System.IO.Path]::GetFileName($resolvedPackage)
if (-not $packageName.EndsWith(".zip", [StringComparison]::OrdinalIgnoreCase)) {
    throw "Package must be a .zip file: $resolvedPackage"
}

Assert-SafeZipEntries -ArchivePath $resolvedPackage

$hasRobustServer = Test-ZipEntry -ArchivePath $resolvedPackage -EntryName "Robust.Server"
$hasClientZip = Test-ZipEntry -ArchivePath $resolvedPackage -EntryName "Content.Client.zip"
$hasBuildJson = Test-ZipEntry -ArchivePath $resolvedPackage -EntryName "build.json"
$hasCdnBuildMetadata = $false
$hasZipDelivery = $false
$hasManifestDelivery = $false
$buildMetadata = $null
if (-not $hasRobustServer) {
    throw "Package is missing required Robust.Server entry: $resolvedPackage"
}

if ($hasClientZip -and $hasBuildJson) {
    throw "Package contains both Content.Client.zip and build.json. Refusing ambiguous client delivery metadata."
}

if (-not $hasClientZip -and $hasBuildJson) {
    try {
        $buildMetadata = (Get-ZipEntryText -ArchivePath $resolvedPackage -EntryName "build.json") | ConvertFrom-Json -ErrorAction Stop
        $hasIdentity = [string]$buildMetadata.engine_version -match "^[A-Za-z0-9][A-Za-z0-9_.+-]{0,127}$" -and
            [string]$buildMetadata.fork_id -match "^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$" -and
            [string]$buildMetadata.version -match "^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$"
        $hasManifestDelivery = [string]$buildMetadata.manifest_hash -match "^[0-9A-Fa-f]{64}$" -and
            (Test-AbsoluteHttpUrl -Value ([string]$buildMetadata.manifest_url)) -and
            (Test-AbsoluteHttpUrl -Value ([string]$buildMetadata.manifest_download_url))
        $hasZipDelivery = [string]$buildMetadata.hash -match "^[0-9A-Fa-f]{64}$" -and
            (Test-AbsoluteHttpUrl -Value ([string]$buildMetadata.download))
        $hasCdnBuildMetadata = $hasIdentity -and ($hasManifestDelivery -or $hasZipDelivery)
    }
    catch {
        throw "Package build.json is invalid: $($_.Exception.Message)"
    }
}

if (-not $hasClientZip -and -not $hasCdnBuildMetadata -and -not $AllowClientZipRestore) {
    throw "Package has neither Content.Client.zip nor complete CDN build.json metadata. Deploy the server artifact returned by Robust.Cdn, or use -AllowClientZipRestore only for an emergency rollback."
}

if (-not $hasClientZip -and $hasBuildJson -and -not $hasCdnBuildMetadata) {
    throw "Package contains incomplete build.json metadata. Remove the stale entry before using -AllowClientZipRestore."
}

$deliveryMode = if ($hasClientZip) { "hybrid-acz" } elseif ($hasCdnBuildMetadata -and $hasZipDelivery) { "external-zip" } elseif ($hasCdnBuildMetadata) { "external-manifest" } else { "restore-existing-client" }

$localHash = (Get-FileHash -LiteralPath $resolvedPackage -Algorithm SHA256).Hash.ToLowerInvariant()
if ($localHash -ne $ExpectedSha256.Trim().ToLowerInvariant()) {
    throw "Local package SHA256 mismatch. Expected $ExpectedSha256, got $localHash"
}

$releaseReceipt = Read-LuaMBinaryReleaseReceipt -Path $ReleaseReceiptPath -ExpectedSha256 $ExpectedReleaseReceiptSha256
if (-not ([string]$releaseReceipt.policySha256).Equals($releasePolicySha256, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Binary release receipt policy SHA256 does not match the active local policy."
}
if (-not ([string]$releaseReceipt.server.fileName).Equals($packageName, [StringComparison]::Ordinal) -or
    -not ([string]$releaseReceipt.server.sha256).Equals($localHash, [StringComparison]::OrdinalIgnoreCase) -or
    [int64]$releaseReceipt.server.bytes -ne [int64](Get-Item -LiteralPath $resolvedPackage).Length) {
    throw "Server package name/hash/size does not match the binary release receipt."
}
if (-not ([string]$releaseReceipt.delivery.mode).Equals($deliveryMode, [StringComparison]::Ordinal)) {
    throw "Server package delivery mode '$deliveryMode' does not match receipt mode '$($releaseReceipt.delivery.mode)'."
}
if ($deliveryMode -eq 'hybrid-acz') {
    $embeddedClientHash = Get-ZipEntrySha256 -ArchivePath $resolvedPackage -EntryName 'Content.Client.zip'
    if (-not $embeddedClientHash.Equals([string]$releaseReceipt.client.sha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Embedded client hash does not match the receipt-bound client artifact."
    }
}
elseif ($deliveryMode -eq 'external-zip') {
    if (-not ([string]$buildMetadata.hash).Equals([string]$releaseReceipt.client.sha256, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([string]$buildMetadata.download).Equals([string]$releaseReceipt.delivery.clientDownloadUrl, [StringComparison]::Ordinal)) {
        throw "External client hash/URL metadata does not match the binary release receipt."
    }
}
else {
    throw "Receipt-bound deployment currently supports only hybrid-acz or external-zip delivery."
}

# A successful child PowerShell script does not necessarily initialize the
# native-process exit code under Windows PowerShell strict mode.
$global:LASTEXITCODE = 0
$auditOutput = @(& (Join-Path $PSScriptRoot "audit_release_surface.ps1") -PackagePath $resolvedPackage -ExpectedPackageKind server -AllowPartial -Json)
if ($global:LASTEXITCODE -ne 0) {
    throw "Server release surface audit failed before deployment."
}
$deploySurfaceAudit = ($auditOutput -join "`n") | ConvertFrom-Json
$serverAudit = @($deploySurfaceAudit.packages | Where-Object { $_.kind -eq 'server' }) | Select-Object -First 1
if ($deploySurfaceAudit.ok -ne $true -or [int64]$deploySurfaceAudit.violationCount -ne 0 -or
    $null -eq $serverAudit -or $serverAudit.serverOnlyCanaryPresent -ne $true) {
    throw "Server package lacks fresh passing surface-audit/canary evidence."
}

if ([string]::IsNullOrWhiteSpace($Tag)) {
    $Tag = [System.IO.Path]::GetFileNameWithoutExtension($packageName)
}

$Tag = ($Tag.Trim() -replace "[^A-Za-z0-9_.-]", "-").Trim("-")
if ([string]::IsNullOrWhiteSpace($Tag)) {
    throw "Deployment tag is empty after sanitizing."
}

$resolvedConfigSource = ""
$configSourceSha256 = ""
if (-not [string]::IsNullOrWhiteSpace($ConfigSourcePath)) {
    if (-not (Test-Path -LiteralPath $ConfigSourcePath -PathType Leaf)) {
        throw "Config source not found: $ConfigSourcePath"
    }

    $resolvedConfigSource = (Resolve-Path -LiteralPath $ConfigSourcePath).Path
    $configSourceSha256 = (Get-FileHash -LiteralPath $resolvedConfigSource -Algorithm SHA256).Hash.ToLowerInvariant()
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

$configInControlDir = @("$normalizedBaseDir/server", "$normalizedBaseDir/deploy-staging", "$normalizedBaseDir/backups") |
    Where-Object { Test-RemotePathWithin -Path $normalizedRemoteConfigPath -Directory $_ }
if ($normalizedRemoteConfigPath -eq $normalizedBaseDir -or
    ($normalizedRemoteConfigPath -ne $serverConfigDefaultPath -and @($configInControlDir).Count -gt 0)) {
    throw "RemoteConfigPath points at a deploy/control directory instead of server_config.toml: $normalizedRemoteConfigPath"
}

if (-not [string]::IsNullOrWhiteSpace($resolvedConfigSource) -and $normalizedRemoteConfigPath -ne $serverConfigDefaultPath) {
    throw "ConfigSourcePath currently requires the live config at $serverConfigDefaultPath. External config replacement needs an explicit service migration first."
}

$normalizedRemoteDataDir = Normalize-RemoteAbsolutePath -Path $RemoteDataDir -Name "RemoteDataDir"
if ($RequireDataBackup -and [string]::IsNullOrWhiteSpace($normalizedRemoteDataDir)) {
    throw "RequireDataBackup was set, but RemoteDataDir is empty. Pass the live server data directory explicitly."
}
if ($LegacyShipSaveBootstrap -and [string]::IsNullOrWhiteSpace($normalizedRemoteDataDir)) {
    throw "LegacyShipSaveBootstrap requires RemoteDataDir so the first-rollout SQLite state can be proven empty."
}

if (-not [string]::IsNullOrWhiteSpace($normalizedRemoteDataDir)) {
    $forbiddenDataDirs = @(
        "$normalizedBaseDir/server",
        "$normalizedBaseDir/deploy-staging",
        "$normalizedBaseDir/backups"
    )
    if ($normalizedRemoteDataDir -eq $normalizedBaseDir -or
        @($forbiddenDataDirs | Where-Object { Test-RemotePathWithin -Path $normalizedRemoteDataDir -Directory $_ }).Count -gt 0) {
        throw "RemoteDataDir points at a deploy/control directory instead of a server data directory: $normalizedRemoteDataDir"
    }
}

$remoteZip = "$BaseDir/deploy-staging/server-$Tag.zip"
$remoteConfigUpload = if ([string]::IsNullOrWhiteSpace($resolvedConfigSource)) { "" } else { "$BaseDir/deploy-staging/server_config-$Tag.toml" }
$freezePolicy = Get-RemoteFreezePolicy
$plan = [ordered]@{
    package = $resolvedPackage
    package_sha256 = $localHash
    expected_sha256 = $ExpectedSha256
    package_has_robust_server = $hasRobustServer
    package_has_client_zip = $hasClientZip
    package_has_build_json = $hasBuildJson
    package_has_cdn_metadata = $hasCdnBuildMetadata
    delivery_mode = $deliveryMode
    release_receipt = (Resolve-Path -LiteralPath $ReleaseReceiptPath).Path
    release_receipt_sha256 = $ExpectedReleaseReceiptSha256.Trim().ToLowerInvariant()
    source_payload_digest_sha256 = [string]$releaseReceipt.sourcePackage.payloadDigestSha256
    source_worktree_digest_sha256 = [string]$releaseReceipt.sourcePackage.worktreeDigestSha256
    surface_audit_passed = [bool]$deploySurfaceAudit.ok
    server_only_canary_present = [bool]$serverAudit.serverOnlyCanaryPresent
    freeze_policy = $freezePolicy
    ssh_target = $SshTarget
    remote_zip = $remoteZip
    tag = $Tag
    service = $ServiceName
    base_dir = $BaseDir
    remote_config_path = $normalizedRemoteConfigPath
    config_source_path = $resolvedConfigSource
    config_source_sha256 = $configSourceSha256
    remote_config_upload = $remoteConfigUpload
    remote_status_url = $RemoteStatusUrl
    remote_info_url = $RemoteInfoUrl
    ship_save_barrier = [ordered]@{
        mode = if ($LegacyShipSaveBootstrap) { "legacy-bootstrap" } else { "authenticated-endpoint" }
        endpoint = $RemoteShipSaveUrl
        receipt_max_age_seconds = $ShipSaveReceiptMaxAgeSeconds
        required_before_stop = $true
        force_cannot_bypass = $true
        legacy_requires_zero_players = [bool]$LegacyShipSaveBootstrap
        legacy_requires_zero_database_state = [bool]$LegacyShipSaveBootstrap
        legacy_requires_endpoint_absent = [bool]$LegacyShipSaveBootstrap
    }
    remote_data_dir = $normalizedRemoteDataDir
    require_data_backup = [bool]$RequireDataBackup
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
Assert-LuaMRemoteMutationAllowed -Policy $mutationPolicy -Mutation 'server-release'

if ($LegacyShipSaveBootstrap) {
    Write-Warning "LegacyShipSaveBootstrap is a first-rollout-only escape hatch. It will refuse deployment unless the endpoint is absent, players are zero, and the SQLite persistence state is provably empty."
}

if ($hasZipDelivery) {
    $externalDownloadUrl = [string]$buildMetadata.download
    try {
        $downloadHead = Invoke-WebRequest -Uri $externalDownloadUrl -Method Head -TimeoutSec 20 -UseBasicParsing
        if ([int]$downloadHead.StatusCode -lt 200 -or [int]$downloadHead.StatusCode -ge 400) {
            throw "HTTP $([int]$downloadHead.StatusCode)"
        }
    }
    catch {
        throw "External client package is not reachable at '$externalDownloadUrl': $($_.Exception.Message)"
    }
}

$preStatus = Wait-ForEmptyServer

Invoke-RemoteBash @"
set -euo pipefail
mkdir -p $(ConvertTo-ShellSingleQuoted "$BaseDir/deploy-staging")
chmod 700 $(ConvertTo-ShellSingleQuoted "$BaseDir/deploy-staging")
"@

Invoke-CheckedNative scp $resolvedPackage "${SshTarget}:$remoteZip"
if (-not [string]::IsNullOrWhiteSpace($resolvedConfigSource)) {
    Invoke-CheckedNative scp $resolvedConfigSource "${SshTarget}:$remoteConfigUpload"
}

$forceValue = if ($Force) { "1" } else { "0" }
$skipPostVerifyValue = if ($SkipPostVerify) { "1" } else { "0" }
$allowClientZipRestoreValue = if ($AllowClientZipRestore) { "1" } else { "0" }
$requireDataBackupValue = if ($RequireDataBackup) { "1" } else { "0" }
$legacyShipSaveBootstrapValue = if ($LegacyShipSaveBootstrap) { "1" } else { "0" }
$hasCdnBuildMetadataValue = if ($hasCdnBuildMetadata) { "1" } else { "0" }
$expectedClientHash = if ($hasZipDelivery) { [string]$buildMetadata.hash } else { "" }
$expectedClientDownload = if ($hasZipDelivery) { [string]$buildMetadata.download } else { "" }
$remoteScript = @"
set -euo pipefail

base_dir=$(ConvertTo-ShellSingleQuoted $BaseDir)
service_name=$(ConvertTo-ShellSingleQuoted $ServiceName)
zip_path=$(ConvertTo-ShellSingleQuoted $remoteZip)
expected_sha=$(ConvertTo-ShellSingleQuoted $localHash)
tag=$(ConvertTo-ShellSingleQuoted $Tag)
remote_config_path=$(ConvertTo-ShellSingleQuoted $normalizedRemoteConfigPath)
uploaded_config_path=$(ConvertTo-ShellSingleQuoted $remoteConfigUpload)
uploaded_config_sha=$(ConvertTo-ShellSingleQuoted $configSourceSha256)
remote_status_url=$(ConvertTo-ShellSingleQuoted $RemoteStatusUrl)
remote_info_url=$(ConvertTo-ShellSingleQuoted $RemoteInfoUrl)
remote_ship_save_url=$(ConvertTo-ShellSingleQuoted $RemoteShipSaveUrl)
ship_save_receipt_max_age=$ShipSaveReceiptMaxAgeSeconds
remote_data_dir=$(ConvertTo-ShellSingleQuoted $normalizedRemoteDataDir)
force_deploy=$forceValue
skip_post_verify=$skipPostVerifyValue
allow_client_zip_restore=$allowClientZipRestoreValue
require_data_backup=$requireDataBackupValue
legacy_ship_save_bootstrap=$legacyShipSaveBootstrapValue
package_has_cdn_metadata=$hasCdnBuildMetadataValue
delivery_mode=$(ConvertTo-ShellSingleQuoted $deliveryMode)
expected_client_hash=$(ConvertTo-ShellSingleQuoted $expectedClientHash)
expected_client_download=$(ConvertTo-ShellSingleQuoted $expectedClientDownload)

server_dir="`$base_dir/server"
stage_dir="`$base_dir/deploy-staging/server-`$tag"
backup_dir="`$base_dir/backups/server-`$tag"
failed_dir="`$base_dir/backups/server-`$tag-failed"
config_backup="`$base_dir/backups/server_config-before-`$tag.toml"
config_sha256=""
new_config_sha256=""
data_backup=""
ship_save_receipt_path=""

cleanup_upload() {
  if [ -n "`$ship_save_receipt_path" ]; then
    rm -f -- "`$ship_save_receipt_path"
  fi
  if [ -n "`$uploaded_config_path" ]; then
    rm -f -- "`$uploaded_config_path"
  fi
}
trap cleanup_upload EXIT

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

if [ -n "`$uploaded_config_path" ]; then
  if [ ! -f "`$uploaded_config_path" ]; then
    echo "Uploaded server config not found: `$uploaded_config_path" >&2
    exit 24
  fi

  actual_uploaded_config_sha=`$(sha256sum "`$uploaded_config_path" | awk '{print `$1}')
  if [ "`$actual_uploaded_config_sha" != "`$uploaded_config_sha" ]; then
    echo "Uploaded config SHA256 mismatch. Expected `$uploaded_config_sha, got `$actual_uploaded_config_sha" >&2
    exit 25
  fi
  chmod 600 "`$uploaded_config_path"
fi

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
  players=`$(python3 - "`$remote_status_url" <<'PY'
import json
import sys
import urllib.request

with urllib.request.urlopen(sys.argv[1], timeout=10) as response:
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
import shutil
import sys
import zipfile

zip_path = sys.argv[1]
stage_dir = pathlib.Path(sys.argv[2])
stage_root = stage_dir.resolve()
max_entries = 100000
max_uncompressed_bytes = 2 * 1024 * 1024 * 1024
max_entry_bytes = 512 * 1024 * 1024

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

        if info.file_size < 0 or info.file_size > max_entry_bytes:
            raise SystemExit(f"Package entry exceeds per-entry limit: {info.filename!r}")
        total_bytes += info.file_size
        if total_bytes > max_uncompressed_bytes:
            raise SystemExit(f"Package uncompressed size exceeds limit: {total_bytes} > {max_uncompressed_bytes}")

        target.parent.mkdir(parents=True, exist_ok=True)
        with archive.open(info) as source, target.open('wb') as dest:
            shutil.copyfileobj(source, dest, length=1024 * 1024)
PY

echo "deploy-step=verify-extract"
test -f "`$stage_dir/Robust.Server"
if [ ! -f "`$stage_dir/Content.Client.zip" ]; then
  if [ "`$package_has_cdn_metadata" = "1" ]; then
    echo "deploy-step=verify-cdn-build-metadata"
    test -f "`$stage_dir/build.json"
  elif [ "`$allow_client_zip_restore" = "1" ]; then
    echo "deploy-step=restore-client-zip"
    if [ ! -f "`$server_dir/Content.Client.zip" ]; then
      echo "Emergency client restore requested, but the live server has no Content.Client.zip." >&2
      exit 30
    fi
    cp "`$server_dir/Content.Client.zip" "`$stage_dir/Content.Client.zip"
  else
    echo "Package has neither Content.Client.zip nor verified CDN build metadata." >&2
    exit 14
  fi
fi

echo "deploy-step=chmod-stage"
chmod 755 "`$stage_dir/Robust.Server"
if [ -f "`$stage_dir/Robust.Packaging" ]; then
  chmod 755 "`$stage_dir/Robust.Packaging"
fi

echo "deploy-step=copy-config"
if [ -e "`$config_backup" ]; then
  config_backup="`$base_dir/backups/server_config-before-`$tag-`$(date +%H%M%S).toml"
fi
sudo install -m 0600 -o root -g root "`$remote_config_path" "`$config_backup"
if [ -n "`$uploaded_config_path" ]; then
  sudo install -m 0600 -o monolith -g monolith "`$uploaded_config_path" "`$stage_dir/server_config.toml"
else
  sudo install -m 0600 -o monolith -g monolith "`$remote_config_path" "`$stage_dir/server_config.toml"
fi
new_config_sha256=`$(sudo sha256sum "`$stage_dir/server_config.toml" | awk '{print `$1}')
expected_new_config_sha="`$config_sha256"
if [ -n "`$uploaded_config_path" ]; then
  expected_new_config_sha="`$uploaded_config_sha"
fi
if [ "`$new_config_sha256" != "`$expected_new_config_sha" ]; then
  echo "Staged config SHA256 mismatch. Expected `$expected_new_config_sha, got `$new_config_sha256" >&2
  exit 26
fi
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
  set +e
  sudo systemctl stop "`$service_name"
  if [ -d "`$server_dir" ]; then
    sudo mv "`$server_dir" "`$failed_dir"
  fi
  if [ -d "`$backup_dir" ]; then
    sudo mv "`$backup_dir" "`$server_dir"
    sudo chmod 755 "`$server_dir/Robust.Server"
    if [ -f "`$server_dir/Robust.Packaging" ]; then
      sudo chmod 755 "`$server_dir/Robust.Packaging"
    fi
    sudo chown -R monolith:monolith "`$server_dir"
    sudo systemctl start "`$service_name"
    rollback_state=`$(systemctl is-active "`$service_name" 2>/dev/null || true)
    if [ "`$rollback_state" != "active" ]; then
      echo "Rollback restored files, but service state is '`$rollback_state'." >&2
    fi
  else
    echo "Rollback backup is missing: `$backup_dir" >&2
  fi
  set -e
}

if [ "`$legacy_ship_save_bootstrap" = "1" ]; then
  echo "deploy-step=ship-save-legacy-bootstrap"
  echo "WARNING: using the first-rollout-only legacy ship-save bootstrap guard." >&2

  # The legacy path is valid only while the old binary demonstrably lacks the
  # authenticated endpoint. Once the endpoint exists, this bypass cannot be reused.
  sudo python3 - "`$remote_ship_save_url" <<'PY'
import sys
import urllib.error
import urllib.request

request = urllib.request.Request(
    sys.argv[1],
    data=b"{}",
    headers={"Content-Type": "application/json"},
    method="POST",
)
try:
    with urllib.request.urlopen(request, timeout=10):
        raise SystemExit("Legacy ship-save bootstrap refused: maintenance endpoint already exists.")
except urllib.error.HTTPError as exc:
    if exc.code != 404:
        raise SystemExit(
            "Legacy ship-save bootstrap refused: maintenance endpoint presence is not demonstrably absent."
        )
except urllib.error.URLError:
    raise SystemExit("Legacy ship-save bootstrap refused: maintenance endpoint probe failed.")
PY

  legacy_players=`$(python3 - "`$remote_status_url" <<'PY'
import json
import sys
import urllib.request

try:
    with urllib.request.urlopen(sys.argv[1], timeout=10) as response:
        payload = json.load(response)
    players = payload.get("players") if isinstance(payload, dict) else None
    if isinstance(players, bool) or not isinstance(players, int) or players < 0:
        raise ValueError
    print(players)
except Exception:
    raise SystemExit("Legacy ship-save bootstrap refused: player count is unavailable or invalid.")
PY
)
  if [ "`$legacy_players" != "0" ]; then
    echo "Legacy ship-save bootstrap refused: players=`$legacy_players (zero required)." >&2
    exit 31
  fi

  legacy_database="`$remote_data_dir/preferences.db"
  sudo -u monolith python3 - "`$legacy_database" <<'PY'
# LUAM_LEGACY_SHIP_SAVE_DATABASE_GUARD_BEGIN
import os
import sqlite3
import sys

database_path = sys.argv[1]
if not os.path.isfile(database_path):
    raise SystemExit("Legacy ship-save bootstrap refused: SQLite database is unavailable.")

try:
    connection = sqlite3.connect(f"file:{database_path}?mode=ro", uri=True, timeout=10)
    connection.execute("pragma query_only = on")
    quick_check = connection.execute("pragma quick_check").fetchone()
    if quick_check is None or quick_check[0] != "ok":
        raise RuntimeError("quick_check")

    expected_tables = {"luam_ship_snapshot", "luam_ship_presence_lease"}
    tables = {
        row[0]
        for row in connection.execute(
            "select name from sqlite_master where type = 'table' and name in (?, ?)",
            tuple(sorted(expected_tables)),
        )
    }

    if not tables:
        schema_state = "absent"
        active_or_restoring = 0
        leases = 0
    elif tables != expected_tables:
        raise RuntimeError("partial_schema")
    else:
        schema_state = "present-empty"
        active_or_restoring = connection.execute(
            "select count(*) from luam_ship_snapshot where status in (1, 2)"
        ).fetchone()[0]
        leases = connection.execute(
            "select count(*) from luam_ship_presence_lease"
        ).fetchone()[0]

    if active_or_restoring != 0 or leases != 0:
        raise RuntimeError(f"nonzero:{active_or_restoring}:{leases}")
except sqlite3.Error:
    raise SystemExit("Legacy ship-save bootstrap refused: SQLite state is unavailable or invalid.")
except RuntimeError as exc:
    if str(exc).startswith("nonzero:"):
        _, active_or_restoring, leases = str(exc).split(":")
        raise SystemExit(
            "Legacy ship-save bootstrap refused: "
            f"active_or_restoring={active_or_restoring}, leases={leases}."
        )
    raise SystemExit("Legacy ship-save bootstrap refused: persistence schema proof failed.")
finally:
    if "connection" in locals():
        connection.close()

print("ship_save_barrier_mode=legacy-bootstrap")
print(f"ship_save_legacy_schema={schema_state}")
print("ship_save_active_or_restoring=0")
print("ship_save_leases=0")
# LUAM_LEGACY_SHIP_SAVE_DATABASE_GUARD_END
PY
else
  echo "deploy-step=ship-save-barrier"
  ship_save_receipt_path=`$(mktemp "`$base_dir/deploy-staging/ship-save-receipt.XXXXXX")
  chmod 600 "`$ship_save_receipt_path"

  # Read the existing server's admin API token only inside a root helper on the
  # host. The token is never placed in argv, stdout, the receipt, or deploy logs.
  sudo python3 - "`$service_name" "`$remote_config_path" "`$remote_ship_save_url" > "`$ship_save_receipt_path" <<'PY'
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.request

service_name, config_path, endpoint = sys.argv[1:4]


def process_environment_token():
    try:
        result = subprocess.run(
            ["systemctl", "show", "--property", "MainPID", "--value", service_name],
            check=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
        )
        pid = int(result.stdout.strip())
        if pid <= 0:
            return None
        with open(f"/proc/{pid}/environ", "rb") as stream:
            entries = stream.read(1024 * 1024).split(b"\0")
        prefix = b"ROBUST_CVAR_admin__api_token="
        for entry in entries:
            if entry.startswith(prefix):
                return entry[len(prefix):].decode("utf-8", "strict")
    except Exception:
        return None
    return None


def fallback_config_token():
    try:
        section = None
        with open(config_path, "r", encoding="utf-8") as stream:
            for line in stream:
                stripped = line.strip()
                section_match = re.match(r"^\[([^]]+)](?:\s*#.*)?$", stripped)
                if section_match:
                    section = section_match.group(1).strip()
                    continue
                if section != "admin" or not re.match(r"^api_token\s*=", stripped):
                    continue
                raw = stripped.split("=", 1)[1].lstrip()
                if raw.startswith('"'):
                    return json.JSONDecoder().raw_decode(raw)[0]
                if raw.startswith("'"):
                    end = raw.find("'", 1)
                    return raw[1:end] if end > 0 else None
    except Exception:
        return None
    return None


def config_token():
    try:
        import tomllib
        with open(config_path, "rb") as stream:
            document = tomllib.load(stream)
        admin = document.get("admin")
        if isinstance(admin, dict):
            value = admin.get("api_token")
            return value if isinstance(value, str) else None
    except ImportError:
        return fallback_config_token()
    except Exception:
        return None
    return None


token = process_environment_token() or config_token()
if not token or any(character in token for character in "\r\n\0"):
    raise SystemExit("Ship-save barrier refused: admin API token is unavailable on the host.")

request = urllib.request.Request(
    endpoint,
    data=b"{}",
    headers={
        "Authorization": f"SS14Token {token}",
        "Content-Type": "application/json",
    },
    method="POST",
)
try:
    with urllib.request.urlopen(request, timeout=300) as response:
        if response.status != 200:
            raise SystemExit("Ship-save barrier refused: endpoint did not return HTTP 200.")
        body = response.read(65537)
        if len(body) > 65536:
            raise SystemExit("Ship-save barrier refused: receipt exceeds 65536 bytes.")
except urllib.error.HTTPError as exc:
    raise SystemExit(f"Ship-save barrier refused: endpoint returned HTTP {exc.code}.")
except urllib.error.URLError:
    raise SystemExit("Ship-save barrier refused: endpoint is unavailable.")

sys.stdout.buffer.write(body)
PY

  python3 - "`$ship_save_receipt_path" "`$ship_save_receipt_max_age" <<'PY'
# LUAM_SHIP_SAVE_RECEIPT_VALIDATOR_BEGIN
import datetime
import json
import os
import sys
import uuid


def fail(reason):
    raise SystemExit(f"Ship-save receipt validation failed: {reason}.")


receipt_path = sys.argv[1]
try:
    max_age_seconds = int(sys.argv[2])
except (ValueError, IndexError):
    fail("invalid maximum age")

if max_age_seconds < 30 or max_age_seconds > 600:
    fail("maximum age outside the allowed range")
if not os.path.isfile(receipt_path) or os.path.getsize(receipt_path) > 65536:
    fail("receipt file is missing or too large")

try:
    with open(receipt_path, "r", encoding="utf-8") as stream:
        receipt = json.load(stream)
except Exception:
    fail("receipt is not valid UTF-8 JSON")

required = {
    "schemaVersion",
    "ok",
    "barrierId",
    "createdAtUtc",
    "attempted",
    "saved",
    "failed",
    "activeRemaining",
    "frozen",
}
allowed = required | {"busy"}
if not isinstance(receipt, dict) or set(receipt) - allowed or required - set(receipt):
    fail("receipt schema is incomplete or contains unknown fields")
if type(receipt["schemaVersion"]) is not int or receipt["schemaVersion"] != 1:
    fail("schemaVersion is not 1")
if type(receipt["ok"]) is not bool or receipt["ok"] is not True:
    fail("ok is not true")
if type(receipt["frozen"]) is not bool or receipt["frozen"] is not True:
    fail("frozen is not true")
if "busy" in receipt and (type(receipt["busy"]) is not bool or receipt["busy"] is not False):
    fail("busy is not false")

for field in ("attempted", "saved", "failed", "activeRemaining"):
    if type(receipt[field]) is not int or receipt[field] < 0:
        fail(f"{field} is not a non-negative integer")
if receipt["attempted"] != receipt["saved"]:
    fail("attempted does not equal saved")
if receipt["failed"] != 0:
    fail("failed is not zero")
if receipt["activeRemaining"] != 0:
    fail("activeRemaining is not zero")

try:
    barrier_id = uuid.UUID(receipt["barrierId"])
except (ValueError, TypeError, AttributeError):
    fail("barrierId is not a UUID")
if barrier_id.int == 0:
    fail("barrierId is empty")

created_text = receipt["createdAtUtc"]
if not isinstance(created_text, str) or not created_text:
    fail("createdAtUtc is not a timestamp")
try:
    created = datetime.datetime.fromisoformat(
        created_text[:-1] + "+00:00" if created_text.endswith("Z") else created_text
    )
except ValueError:
    fail("createdAtUtc is not ISO-8601")
if created.tzinfo is None or created.utcoffset() != datetime.timedelta(0):
    fail("createdAtUtc is not UTC")

now = datetime.datetime.now(datetime.timezone.utc)
age_seconds = (now - created).total_seconds()
if age_seconds < -30 or age_seconds > max_age_seconds:
    fail("receipt is stale or too far in the future")

print("ship_save_barrier_mode=endpoint")
print(f"ship_save_barrier_id={barrier_id}")
print(f"ship_save_created_at_utc={created.astimezone(datetime.timezone.utc).isoformat().replace('+00:00', 'Z')}")
print(f"ship_save_attempted={receipt['attempted']}")
print(f"ship_save_saved={receipt['saved']}")
print("ship_save_failed=0")
print("ship_save_active_remaining=0")
print("ship_save_frozen=true")
# LUAM_SHIP_SAVE_RECEIPT_VALIDATOR_END
PY
fi

echo "deploy-step=stop-service"
if ! sudo systemctl stop "`$service_name"; then
  echo "Failed to stop `$service_name; server files were not changed." >&2
  exit 27
fi
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
if ! sudo mv "`$server_dir" "`$backup_dir"; then
  echo "Failed to create server backup; restarting unchanged server." >&2
  sudo systemctl start "`$service_name" || true
  exit 28
fi
if ! sudo mv "`$stage_dir" "`$server_dir"; then
  echo "Failed to install staged server; restoring backup." >&2
  sudo mv "`$backup_dir" "`$server_dir" || true
  sudo systemctl start "`$service_name" || true
  exit 29
fi
echo "deploy-step=start-service"
if ! sudo systemctl start "`$service_name"; then
  rollback
  exit 13
fi

if [ "`$skip_post_verify" != "1" ]; then
  state=`$(systemctl is-active "`$service_name" 2>/dev/null || true)
  if [ "`$state" != "active" ]; then
    sudo systemctl status "`$service_name" --no-pager -l || true
    rollback
    exit 13
  fi

  echo "deploy-step=wait-status-and-info"
  status_ready=0
  for attempt in `$(seq 1 45); do
    if python3 - "`$remote_status_url" >/dev/null 2>&1 <<'PY'
import json
import sys
import urllib.request

with urllib.request.urlopen(sys.argv[1], timeout=3) as response:
    payload = json.load(response)
    if not isinstance(payload, dict) or "players" not in payload:
        raise SystemExit(1)
PY
    then
      status_ready=1
      break
    fi

    state=`$(systemctl is-active "`$service_name" 2>/dev/null || true)
    if [ "`$state" != "active" ]; then
      break
    fi
    sleep 2
  done

  if [ "`$status_ready" != "1" ]; then
    sudo systemctl status "`$service_name" --no-pager -l || true
    rollback
    exit 13
  fi

  info_ready=0
  for attempt in `$(seq 1 5); do
    if python3 - "`$remote_info_url" "`$delivery_mode" "`$expected_client_hash" "`$expected_client_download" >/dev/null 2>&1 <<'PY'
import json
import re
import sys
import urllib.parse
import urllib.request

info_url, delivery_mode, expected_hash, expected_download = sys.argv[1:]
sha256_pattern = re.compile(r"^[0-9A-Fa-f]{64}$")

def is_http_url(value):
    parsed = urllib.parse.urlparse(value or "")
    return parsed.scheme in ("http", "https") and bool(parsed.hostname) and not parsed.username and not parsed.password

with urllib.request.urlopen(info_url, timeout=20) as response:
    payload = json.load(response)

build = payload.get("build") if isinstance(payload, dict) else None
if not isinstance(build, dict):
    raise SystemExit(1)

if delivery_mode == "external-zip":
    if build.get("acz") is not False:
        raise SystemExit(1)
    if str(build.get("hash", "")).lower() != expected_hash.lower():
        raise SystemExit(1)
    if str(build.get("download_url", "")) != expected_download or not is_http_url(build.get("download_url")):
        raise SystemExit(1)
elif delivery_mode == "external-manifest":
    if build.get("acz") is not False:
        raise SystemExit(1)
    if not sha256_pattern.fullmatch(str(build.get("manifest_hash", ""))):
        raise SystemExit(1)
    if not is_http_url(build.get("manifest_url")) or not is_http_url(build.get("manifest_download_url")):
        raise SystemExit(1)
elif delivery_mode in ("hybrid-acz", "restore-existing-client"):
    if build.get("acz") is not True or not sha256_pattern.fullmatch(str(build.get("manifest_hash", ""))):
        raise SystemExit(1)
else:
    raise SystemExit(1)
PY
    then
      info_ready=1
      break
    fi

    state=`$(systemctl is-active "`$service_name" 2>/dev/null || true)
    if [ "`$state" != "active" ]; then
      break
    fi
    sleep 2
  done

  if [ "`$info_ready" != "1" ]; then
    echo "Status became ready, but /info client-delivery metadata did not match the deployed artifact." >&2
    sudo systemctl status "`$service_name" --no-pager -l || true
    rollback
    exit 13
  fi
fi

echo "deployed_tag=`$tag"
echo "backup_dir=`$backup_dir"
echo "config_backup=`$config_backup"
echo "config_sha256=`$config_sha256"
echo "new_config_sha256=`$new_config_sha256"
if [ -n "`$data_backup" ]; then
  echo "data_backup=`$data_backup"
fi
if [ -f "`$server_dir/Content.Client.zip" ]; then
  sha256sum "`$server_dir/Content.Client.zip"
else
  sha256sum "`$server_dir/build.json"
fi
rm -f -- "`$zip_path"
"@

try {
    Invoke-RemoteBash $remoteScript
}
finally {
    # The remote EXIT trap normally removes the uploaded TOML. This second,
    # best-effort cleanup also covers a dropped SSH session before bash starts.
    if (-not [string]::IsNullOrWhiteSpace($remoteConfigUpload)) {
        try {
            Invoke-RemoteBash "rm -f -- $(ConvertTo-ShellSingleQuoted $remoteConfigUpload)"
        }
        catch {
            Write-Warning "Unable to confirm cleanup of uploaded config '$remoteConfigUpload': $($_.Exception.Message)"
        }
    }
}

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
    delivery_mode = $deliveryMode
    remote_config_path = $normalizedRemoteConfigPath
    remote_status_url = $RemoteStatusUrl
    remote_info_url = $RemoteInfoUrl
    ship_save_barrier_mode = if ($LegacyShipSaveBootstrap) { "legacy-bootstrap" } else { "authenticated-endpoint" }
    remote_ship_save_url = $RemoteShipSaveUrl
    ship_save_receipt_max_age_seconds = $ShipSaveReceiptMaxAgeSeconds
    ship_save_force_bypass = $false
    config_source_path = $resolvedConfigSource
    config_source_sha256 = $configSourceSha256
    remote_data_dir = $normalizedRemoteDataDir
    require_data_backup = [bool]$RequireDataBackup
} | ConvertTo-Json -Depth 6
