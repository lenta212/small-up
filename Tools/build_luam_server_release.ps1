param(
    [string]$Platform = "linux-x64",
    [string]$Configuration = "Release",
    [switch]$HybridAcz,
    [string]$ExternalClientBaseUrl = "",
    [string]$ForkId = "dsmonolith",
    [string]$BuildVersion = "",
    [string]$SourcePackagePath = "",
    [string]$ExpectedSourcePackageSha256 = "",
    [string]$ReleaseReceiptPath = "release\luam-binary-release-receipt.json",
    [switch]$SkipPackageBuild,
    [switch]$SkipAudit,
    [switch]$LocalOnly,
    [switch]$Json
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "luam_release_contract.ps1")
$releasePolicy = Read-LuaMReleasePolicy -Root $root
$releasePolicyPath = Join-Path $root "Tools\luam_release_policy.json"
$releasePolicySha256 = (Get-FileHash -LiteralPath $releasePolicyPath -Algorithm SHA256).Hash.ToLowerInvariant()
$policyExcludedLocalArtifacts = @(Get-LuaMReleaseExcludedLocalArtifacts -Policy $releasePolicy)
$packagingOut = Join-Path $root ".packaging-run"
$releaseDir = Join-Path $root "release"
$serverPackage = Join-Path $releaseDir "SS14.Server_$Platform.zip"
$clientPackage = Join-Path $releaseDir "SS14.Client.zip"
$resolvedReceiptOutputPath = [IO.Path]::GetFullPath(
    $(if ([IO.Path]::IsPathRooted($ReleaseReceiptPath)) { $ReleaseReceiptPath } else { Join-Path $root $ReleaseReceiptPath }))
$releaseDirFull = [IO.Path]::GetFullPath($releaseDir).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
if (-not $resolvedReceiptOutputPath.StartsWith($releaseDirFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    -not $resolvedReceiptOutputPath.EndsWith('.json', [StringComparison]::OrdinalIgnoreCase)) {
    throw "ReleaseReceiptPath must be a JSON file beneath the local release directory."
}
if (Test-Path -LiteralPath $resolvedReceiptOutputPath) {
    $receiptOutputItem = Get-Item -LiteralPath $resolvedReceiptOutputPath -Force
    if (($receiptOutputItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "ReleaseReceiptPath cannot target a reparse point."
    }
}

if ($Configuration -cne "Release") {
    throw "Production server packages must use Configuration=Release. Got '$Configuration'."
}

if (-not $LocalOnly) {
    if ($SkipPackageBuild) {
        throw "-SkipPackageBuild is local-only and cannot produce a production binary receipt."
    }
    if ($SkipAudit) {
        throw "-SkipAudit is local-only and cannot produce a production binary receipt."
    }
    if ([string]::IsNullOrWhiteSpace($SourcePackagePath) -or [string]::IsNullOrWhiteSpace($ExpectedSourcePackageSha256)) {
        throw "Production binary builds require -SourcePackagePath and -ExpectedSourcePackageSha256."
    }
    if (-not $HybridAcz -and [string]::IsNullOrWhiteSpace($ExternalClientBaseUrl)) {
        throw "Production binary builds require a complete client delivery mode: -HybridAcz or -ExternalClientBaseUrl."
    }
}

if ($HybridAcz -and -not [string]::IsNullOrWhiteSpace($ExternalClientBaseUrl)) {
    throw "-HybridAcz and -ExternalClientBaseUrl are mutually exclusive."
}

if ([string]::IsNullOrWhiteSpace($ForkId) -or $ForkId.Length -gt 128 -or $ForkId -notmatch "^[A-Za-z0-9][A-Za-z0-9_.-]*$") {
    throw "ForkId must be 1-128 characters and contain only letters, digits, dot, underscore, or hyphen: '$ForkId'"
}

if (-not [string]::IsNullOrWhiteSpace($BuildVersion) -and
    ($BuildVersion.Trim().Length -gt 128 -or $BuildVersion.Trim() -notmatch "^[A-Za-z0-9][A-Za-z0-9_.-]*$")) {
    throw "BuildVersion must be 1-128 characters, start with a letter or digit, and contain only letters, digits, dot, underscore, or hyphen."
}

if (($HybridAcz -or [string]::IsNullOrWhiteSpace($ExternalClientBaseUrl)) -and
    -not [string]::IsNullOrWhiteSpace($BuildVersion)) {
    throw "-BuildVersion is only valid together with -ExternalClientBaseUrl."
}

$externalClientBaseUri = $null
if (-not [string]::IsNullOrWhiteSpace($ExternalClientBaseUrl)) {
    $candidateUri = $null
    if (-not [Uri]::TryCreate($ExternalClientBaseUrl.Trim(), [UriKind]::Absolute, [ref]$candidateUri) -or
        $candidateUri.Scheme -notin @("http", "https") -or
        [string]::IsNullOrWhiteSpace($candidateUri.Host) -or
        -not [string]::IsNullOrWhiteSpace($candidateUri.UserInfo) -or
        -not [string]::IsNullOrWhiteSpace($candidateUri.Query) -or
        -not [string]::IsNullOrWhiteSpace($candidateUri.Fragment)) {
        throw "ExternalClientBaseUrl must be an absolute public HTTP(S) URL without credentials, query, or fragment: '$ExternalClientBaseUrl'"
    }

    $externalClientBaseUri = $candidateUri
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

function Get-ZipEntrySha256 {
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
            throw "Required entry '$EntryName' not found in $ArchivePath"
        }

        $stream = $entry.Open()
        try {
            $sha = [System.Security.Cryptography.SHA256]::Create()
            try {
                return ([System.BitConverter]::ToString($sha.ComputeHash($stream)) -replace "-", "").ToLowerInvariant()
            }
            finally {
                $sha.Dispose()
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

function Set-ZipBuildMetadata {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ArchivePath,
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$Metadata
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $json = $Metadata | ConvertTo-Json -Depth 4 -Compress
    $jsonBytes = [System.Text.UTF8Encoding]::new($false).GetBytes($json)

    # Preserve the complete server archive byte-for-byte when an identical
    # metadata entry is already present. This keeps -SkipPackageBuild idempotent.
    $readArchive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $existingEntries = @($readArchive.Entries | Where-Object { $_.FullName -eq "build.json" })
        if ($existingEntries.Count -eq 1 -and $existingEntries[0].Length -eq $jsonBytes.Length) {
            $stream = $existingEntries[0].Open()
            try {
                $memory = [System.IO.MemoryStream]::new()
                try {
                    $stream.CopyTo($memory)
                    $existingBytes = $memory.ToArray()
                }
                finally {
                    $memory.Dispose()
                }
            }
            finally {
                $stream.Dispose()
            }

            if ([Convert]::ToBase64String($existingBytes) -ceq [Convert]::ToBase64String($jsonBytes)) {
                return
            }
        }
    }
    finally {
        $readArchive.Dispose()
    }

    $archive = [System.IO.Compression.ZipFile]::Open($ArchivePath, [System.IO.Compression.ZipArchiveMode]::Update)
    try {
        foreach ($existing in @($archive.Entries | Where-Object { $_.FullName -eq "build.json" })) {
            $existing.Delete()
        }

        $entry = $archive.CreateEntry("build.json", [System.IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        $stream = $entry.Open()
        try {
            $stream.Write($jsonBytes, 0, $jsonBytes.Length)
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Remove-ZipEntry {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ArchivePath,
        [Parameter(Mandatory = $true)]
        [string]$EntryName
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    if (-not (Test-ZipEntry -ArchivePath $ArchivePath -EntryName $EntryName)) {
        return
    }

    $archive = [System.IO.Compression.ZipFile]::Open($ArchivePath, [System.IO.Compression.ZipArchiveMode]::Update)
    try {
        foreach ($entry in @($archive.Entries | Where-Object { $_.FullName -eq $EntryName })) {
            $entry.Delete()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Invoke-SourcePackageVerification {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256
    )

    $output = @(& powershell "-NoProfile" "-ExecutionPolicy" "Bypass" "-File" "Tools\verify_luam_release_package.ps1" "-PackagePath" $Path "-ExpectedSha256" $ExpectedSha256 "-Json")
    if ($LASTEXITCODE -ne 0) {
        throw "Source package verification failed with exit code $LASTEXITCODE."
    }
    try {
        return ($output -join "`n") | ConvertFrom-Json
    }
    catch {
        throw "Source package verifier returned invalid JSON: $($_.Exception.Message)"
    }
}

Push-Location $root
try {
    $sourceVerification = $null
    $initialWorktreeReceipt = Get-LuaMWorktreeReceipt -Root $root -ExcludedArtifacts $policyExcludedLocalArtifacts
    if (-not $LocalOnly) {
        $sourceVerification = Invoke-SourcePackageVerification -Path $SourcePackagePath -ExpectedSha256 $ExpectedSourcePackageSha256
        if ($sourceVerification.ok -ne $true -or $sourceVerification.productionEligible -ne $true) {
            throw "Source package is not production-eligible."
        }
        if (-not ([string]$sourceVerification.policySha256).Equals($releasePolicySha256, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Source package policy SHA256 does not match the policy used for the binary build."
        }
        if (-not ([string]$sourceVerification.worktreeDigestSha256).Equals([string]$initialWorktreeReceipt.digestSha256, [System.StringComparison]::OrdinalIgnoreCase) -or
            -not ([string]$sourceVerification.gitHead).Equals([string]$initialWorktreeReceipt.gitHead, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Source package receipt does not match the current binary-build worktree."
        }
        if (-not $initialWorktreeReceipt.trackedForProduction -or [int64]$initialWorktreeReceipt.untrackedFileCount -ne 0) {
            throw "Production binary builds require a fully tracked worktree."
        }
    }

    if (-not $SkipPackageBuild) {
        Invoke-CheckedNative dotnet "publish" "Content.Packaging" "-c" $Configuration "-o" $packagingOut
        if ($HybridAcz) {
            Invoke-CheckedNative dotnet (Join-Path $packagingOut "Content.Packaging.dll") "server" "--platform" $Platform "--configuration" $Configuration "--hybrid-acz"
        }
        else {
            Invoke-CheckedNative dotnet (Join-Path $packagingOut "Content.Packaging.dll") "server" "--platform" $Platform "--configuration" $Configuration
            Invoke-CheckedNative dotnet (Join-Path $packagingOut "Content.Packaging.dll") "client" "--configuration" $Configuration "--no-wipe-release"
        }
    }

    if (-not (Test-Path -LiteralPath $serverPackage -PathType Leaf)) {
        throw "Server package was not created: $serverPackage"
    }

    if (-not (Test-Path -LiteralPath $clientPackage -PathType Leaf)) {
        throw "Client package was not created: $clientPackage"
    }

    if (-not (Test-ZipEntry -ArchivePath $serverPackage -EntryName "Robust.Server")) {
        throw "Required entry 'Robust.Server' not found in $serverPackage"
    }

    $clientSha = (Get-FileHash -LiteralPath $clientPackage -Algorithm SHA256).Hash.ToLowerInvariant()
    $hasEmbeddedClient = Test-ZipEntry -ArchivePath $serverPackage -EntryName "Content.Client.zip"
    $embeddedClientSha = $null
    $effectiveBuildVersion = ""
    $clientDownloadUrl = ""

    if ($HybridAcz) {
        if (-not $hasEmbeddedClient) {
            throw "Hybrid ACZ package is missing Content.Client.zip: $serverPackage"
        }

        $embeddedClientSha = Get-ZipEntrySha256 -ArchivePath $serverPackage -EntryName "Content.Client.zip"
        if ($embeddedClientSha -ne $clientSha) {
            throw "Embedded Content.Client.zip SHA256 mismatch. Embedded $embeddedClientSha, release $clientSha"
        }
    }
    elseif ($hasEmbeddedClient) {
        throw "Non-hybrid package unexpectedly contains Content.Client.zip. Rebuild without -HybridAcz."
    }

    if ($null -ne $externalClientBaseUri) {
        $effectiveBuildVersion = if ([string]::IsNullOrWhiteSpace($BuildVersion)) { $clientSha } else { $BuildVersion.Trim() }

        $enginePropsPath = Join-Path $root "RobustToolbox\MSBuild\Robust.Engine.Version.props"
        [xml]$engineProps = Get-Content -LiteralPath $enginePropsPath -Raw
        $engineVersion = [string]@($engineProps.Project.PropertyGroup.Version | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -First 1)[0]
        if ([string]::IsNullOrWhiteSpace($engineVersion)) {
            throw "Unable to read Robust engine version from $enginePropsPath"
        }

        $clientDownloadUrl = $externalClientBaseUri.AbsoluteUri.TrimEnd("/") + "/$effectiveBuildVersion/SS14.Client.zip"
        Set-ZipBuildMetadata -ArchivePath $serverPackage -Metadata ([ordered]@{
            engine_version = $engineVersion
            hash = $clientSha.ToUpperInvariant()
            download = $clientDownloadUrl
            fork_id = $ForkId
            version = $effectiveBuildVersion
            manifest_hash = $null
            manifest_url = $null
            manifest_download_url = $null
        })
    }
    else {
        # A stale external build.json takes precedence over ACZ at runtime. Strip it
        # from Hybrid packages and clean non-hybrid inputs intended for Robust.Cdn.
        Remove-ZipEntry -ArchivePath $serverPackage -EntryName "build.json"
    }

    $serverSha = (Get-FileHash -LiteralPath $serverPackage -Algorithm SHA256).Hash.ToLowerInvariant()
    $hasBuildJson = Test-ZipEntry -ArchivePath $serverPackage -EntryName "build.json"

    $surfaceAudit = $null
    if (-not $SkipAudit) {
        $auditOutput = @(& (Join-Path $PSScriptRoot "audit_release_surface.ps1") -PackagePath @($clientPackage, $serverPackage) -Json)
        if ($LASTEXITCODE -ne 0) {
            throw "Tools\audit_release_surface.ps1 failed with exit code $LASTEXITCODE"
        }
        try {
            $surfaceAudit = ($auditOutput -join "`n") | ConvertFrom-Json
        }
        catch {
            throw "Release surface audit returned invalid JSON: $($_.Exception.Message)"
        }
        if ($surfaceAudit.ok -ne $true -or [int64]$surfaceAudit.violationCount -ne 0 -or
            [int64]$surfaceAudit.clientPackageCount -ne 1 -or [int64]$surfaceAudit.serverPackageCount -ne 1) {
            throw "Release surface audit did not prove an exact clean client/server pair."
        }
        $postAuditClientSha = (Get-FileHash -LiteralPath $clientPackage -Algorithm SHA256).Hash.ToLowerInvariant()
        $postAuditServerSha = (Get-FileHash -LiteralPath $serverPackage -Algorithm SHA256).Hash.ToLowerInvariant()
        if (-not $clientSha.Equals($postAuditClientSha, [StringComparison]::OrdinalIgnoreCase) -or
            -not $serverSha.Equals($postAuditServerSha, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Client or server artifact changed while the release surface audit was running."
        }
    }

    $finalWorktreeReceipt = Get-LuaMWorktreeReceipt -Root $root -ExcludedArtifacts $policyExcludedLocalArtifacts
    $finalPolicySha256 = (Get-FileHash -LiteralPath $releasePolicyPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not ([string]$initialWorktreeReceipt.digestSha256).Equals([string]$finalWorktreeReceipt.digestSha256, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not ([string]$initialWorktreeReceipt.gitHead).Equals([string]$finalWorktreeReceipt.gitHead, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Release worktree changed during binary package construction."
    }
    if (-not $releasePolicySha256.Equals($finalPolicySha256, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Release policy changed during binary package construction."
    }

    $receiptPath = $null
    $receiptSha256 = $null
    if (-not $LocalOnly) {
        $serverAudit = @($surfaceAudit.packages | Where-Object { $_.kind -eq 'server' }) | Select-Object -First 1
        $clientAudit = @($surfaceAudit.packages | Where-Object { $_.kind -eq 'client' }) | Select-Object -First 1
        if ($null -eq $serverAudit -or $serverAudit.serverOnlyCanaryPresent -ne $true -or
            $null -eq $clientAudit -or $clientAudit.clientServerOnlyAbsent -ne $true) {
            throw "Release surface audit did not prove both directions of the server-only canary contract."
        }

        $deliveryMode = if ($HybridAcz) { 'hybrid-acz' } else { 'external-zip' }
        $receipt = [pscustomobject]@{
            schemaVersion = 1
            generatedAtUtc = [DateTime]::UtcNow.ToString('o')
            policySha256 = $releasePolicySha256
            sourcePackage = [pscustomobject]@{
                fileName = [IO.Path]::GetFileName([string]$sourceVerification.package)
                sha256 = ([string]$sourceVerification.sha256).ToLowerInvariant()
                payloadDigestSha256 = ([string]$sourceVerification.payloadDigestSha256).ToLowerInvariant()
                worktreeDigestSha256 = ([string]$sourceVerification.worktreeDigestSha256).ToLowerInvariant()
                gitHead = ([string]$sourceVerification.gitHead).ToLowerInvariant()
            }
            buildWorktree = [pscustomobject]@{
                digestSha256 = ([string]$finalWorktreeReceipt.digestSha256).ToLowerInvariant()
                gitHead = ([string]$finalWorktreeReceipt.gitHead).ToLowerInvariant()
                untrackedFileCount = [int64]$finalWorktreeReceipt.untrackedFileCount
            }
            client = [pscustomobject]@{
                fileName = [IO.Path]::GetFileName($clientPackage)
                sha256 = $clientSha
                bytes = [int64](Get-Item -LiteralPath $clientPackage).Length
            }
            server = [pscustomobject]@{
                fileName = [IO.Path]::GetFileName($serverPackage)
                sha256 = $serverSha
                bytes = [int64](Get-Item -LiteralPath $serverPackage).Length
            }
            delivery = [pscustomobject]@{
                mode = $deliveryMode
                clientDownloadUrl = if ($deliveryMode -eq 'external-zip') { $clientDownloadUrl } else { $null }
            }
            surfaceAudit = [pscustomobject]@{
                passed = $true
                serverOnlyCanaryPresent = $true
                clientServerOnlyAbsent = $true
                violationCount = 0
            }
        }
        Assert-LuaMBinaryReleaseReceipt -Receipt $receipt
        $receiptPath = $resolvedReceiptOutputPath
        $receiptDirectory = Split-Path -Parent $receiptPath
        if (-not [string]::IsNullOrWhiteSpace($receiptDirectory)) {
            New-Item -ItemType Directory -Force -Path $receiptDirectory | Out-Null
        }
        $receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $receiptPath -Encoding UTF8
        $receiptSha256 = (Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }

    $result = [pscustomobject]@{
        ok = $true
        platform = $Platform
        configuration = $Configuration
        serverPackage = (Resolve-Path -LiteralPath $serverPackage).Path
        serverSha256 = $serverSha
        clientPackage = (Resolve-Path -LiteralPath $clientPackage).Path
        clientSha256 = $clientSha
        embeddedClientSha256 = $embeddedClientSha
        hybridAcz = [bool]$HybridAcz
        externalClientDelivery = -not [string]::IsNullOrWhiteSpace($clientDownloadUrl)
        clientDownloadUrl = $clientDownloadUrl
        buildVersion = $effectiveBuildVersion
        buildJson = $hasBuildJson
        cdnPublishRequired = -not [bool]$HybridAcz -and [string]::IsNullOrWhiteSpace($clientDownloadUrl)
        packageBuildSkipped = [bool]$SkipPackageBuild
        audited = -not $SkipAudit
        localOnly = [bool]$LocalOnly
        sourcePackageVerified = $null -ne $sourceVerification
        sourcePayloadDigestSha256 = if ($null -ne $sourceVerification) { [string]$sourceVerification.payloadDigestSha256 } else { $null }
        worktreeDigestSha256 = [string]$finalWorktreeReceipt.digestSha256
        releaseReceipt = $receiptPath
        releaseReceiptSha256 = $receiptSha256
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 4
    }
    else {
        $result
    }
}
finally {
    Pop-Location
}
