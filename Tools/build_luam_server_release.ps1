param(
    [string]$Platform = "linux-x64",
    [string]$Configuration = "Release",
    [switch]$HybridAcz,
    [string]$ExternalClientBaseUrl = "",
    [string]$ForkId = "dsmonolith",
    [string]$BuildVersion = "",
    [switch]$SkipPackageBuild,
    [switch]$SkipAudit,
    [switch]$Json
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$packagingOut = Join-Path $root ".packaging-run"
$releaseDir = Join-Path $root "release"
$serverPackage = Join-Path $releaseDir "SS14.Server_$Platform.zip"
$clientPackage = Join-Path $releaseDir "SS14.Client.zip"

if ($Configuration -cne "Release") {
    throw "Production server packages must use Configuration=Release. Got '$Configuration'."
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

Push-Location $root
try {
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

    if (-not $SkipAudit) {
        if ($Json) {
            & powershell "-NoProfile" "-ExecutionPolicy" "Bypass" "-File" "Tools\audit_release_surface.ps1" "-ReleaseDir" "release" "-Json" | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "Tools\audit_release_surface.ps1 failed with exit code $LASTEXITCODE"
            }
        }
        else {
            Invoke-CheckedNative powershell "-NoProfile" "-ExecutionPolicy" "Bypass" "-File" "Tools\audit_release_surface.ps1" "-ReleaseDir" "release"
        }
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
