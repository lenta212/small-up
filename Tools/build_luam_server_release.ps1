param(
    [string]$Platform = "linux-x64",
    [string]$Configuration = "Release",
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

Push-Location $root
try {
    if (-not $SkipPackageBuild) {
        Invoke-CheckedNative dotnet "publish" "Content.Packaging" "-c" $Configuration "-o" $packagingOut
        Invoke-CheckedNative dotnet (Join-Path $packagingOut "Content.Packaging.dll") "server" "--platform" $Platform "--configuration" $Configuration "--hybrid-acz"
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

    $serverSha = (Get-FileHash -LiteralPath $serverPackage -Algorithm SHA256).Hash.ToLowerInvariant()
    $clientSha = (Get-FileHash -LiteralPath $clientPackage -Algorithm SHA256).Hash.ToLowerInvariant()
    $embeddedClientSha = Get-ZipEntrySha256 -ArchivePath $serverPackage -EntryName "Content.Client.zip"

    if ($embeddedClientSha -ne $clientSha) {
        throw "Embedded Content.Client.zip SHA256 mismatch. Embedded $embeddedClientSha, release $clientSha"
    }

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
        hybridAcz = $true
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
