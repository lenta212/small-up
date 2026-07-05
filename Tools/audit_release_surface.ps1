[CmdletBinding()]
param(
    [string[]]$PackagePath = @(),
    [string]$ReleaseDir = "release",
    [switch]$Json,
    [switch]$WarnOnly
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$forbiddenExtensions = @(
    ".pdb",
    ".mdb",
    ".cs",
    ".fs",
    ".vb",
    ".csproj",
    ".fsproj",
    ".vbproj",
    ".sln",
    ".suo",
    ".user",
    ".log",
    ".tmp",
    ".cache",
    ".bak"
)

$forbiddenNames = @(
    ".env",
    ".env.local",
    ".env.production",
    "appsettings.development.json",
    "appsettings.local.json",
    "secrets.json",
    "nuget.config"
)

$forbiddenFragments = @(
    "/.git/",
    "/.github/",
    "/.vs/",
    "/obj/",
    "/testresults/"
)

$textExtensionsToScan = @(
    ".cfg",
    ".csv",
    ".ftl",
    ".json",
    ".md",
    ".toml",
    ".txt",
    ".xml",
    ".yaml",
    ".yml"
)

$forbiddenTextPatterns = @(
    [pscustomobject]@{ Pattern = "(?i)\bANTHROPIC_API_KEY\b"; Reason = "forbidden API key variable name" },
    [pscustomobject]@{ Pattern = "(?i)\bOPENAI_API_KEY\b"; Reason = "forbidden API key variable name" },
    [pscustomobject]@{ Pattern = "(?i)\bLUAM_AI_GATEWAY_TOKEN\b"; Reason = "forbidden gateway token variable name" },
    [pscustomobject]@{ Pattern = "(?i)\bgateway_token\b\s*=\s*[""'][^""']+[""']"; Reason = "forbidden non-empty gateway token config" },
    [pscustomobject]@{ Pattern = "(?i)\bapi[_-]?key\b\s*[:=]\s*[""'][^""']+[""']"; Reason = "forbidden non-empty API key config" },
    [pscustomobject]@{ Pattern = "(?i)\bserver_config\.toml\b"; Reason = "forbidden server config reference" },
    [pscustomobject]@{ Pattern = "-----BEGIN [A-Z ]*PRIVATE KEY-----"; Reason = "forbidden private key block" },
    [pscustomobject]@{ Pattern = "\bsk-[A-Za-z0-9_-]{20,}\b"; Reason = "forbidden OpenAI-style secret token" }
)

function Normalize-ZipPath {
    param([string]$Path)

    return ($Path -replace "\\", "/").TrimStart("/")
}

function Test-ForbiddenReleaseEntry {
    param([string]$EntryName)

    $normalized = Normalize-ZipPath $EntryName
    if ([string]::IsNullOrWhiteSpace($normalized) -or $normalized.EndsWith("/")) {
        return $null
    }

    $lower = "/" + $normalized.ToLowerInvariant()
    $fileName = [System.IO.Path]::GetFileName($normalized).ToLowerInvariant()
    $extension = [System.IO.Path]::GetExtension($fileName).ToLowerInvariant()

    if ($forbiddenExtensions -contains $extension) {
        return "forbidden extension $extension"
    }

    if ($forbiddenNames -contains $fileName) {
        return "forbidden file name $fileName"
    }

    foreach ($fragment in $forbiddenFragments) {
        if ($lower.Contains($fragment)) {
            return "forbidden path fragment $fragment"
        }
    }

    return $null
}

function Test-ForbiddenTextContent {
    param(
        [System.IO.Compression.ZipArchiveEntry]$Entry,
        [string]$Normalized
    )

    $extension = [System.IO.Path]::GetExtension($Normalized).ToLowerInvariant()
    if ($textExtensionsToScan -notcontains $extension -or $Entry.Length -gt 1MB) {
        return @()
    }

    $stream = $Entry.Open()
    try {
        $reader = [System.IO.StreamReader]::new($stream, [System.Text.UTF8Encoding]::new($false, $false), $true)
        try {
            $text = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    $matches = New-Object System.Collections.Generic.List[string]
    foreach ($rule in $forbiddenTextPatterns) {
        if ([regex]::IsMatch($text, $rule.Pattern)) {
            $matches.Add($rule.Reason) | Out-Null
        }
    }

    return $matches.ToArray()
}

function Inspect-ZipArchive {
    param(
        [System.IO.Compression.ZipArchive]$Archive,
        [string]$Label,
        [int]$Depth = 0
    )

    $entryCount = 0
    $byteCount = [int64]0
    $assemblyCount = 0
    $resourceCount = 0
    $violations = New-Object System.Collections.Generic.List[object]

    foreach ($entry in $Archive.Entries) {
        if ([string]::IsNullOrWhiteSpace($entry.FullName) -or $entry.FullName.EndsWith("/")) {
            continue
        }

        $entryCount += 1
        $byteCount += [int64]$entry.Length

        $normalized = Normalize-ZipPath $entry.FullName
        if ($normalized.StartsWith("Assemblies/") -or $normalized.StartsWith("Resources/Assemblies/")) {
            $assemblyCount += 1
        }

        if ($normalized.StartsWith("Resources/")) {
            $resourceCount += 1
        }

        $reason = Test-ForbiddenReleaseEntry $normalized
        if ($reason -ne $null) {
            $violations.Add([pscustomobject]@{
                package = $Label
                entry = $normalized
                reason = $reason
                bytes = [int64]$entry.Length
            }) | Out-Null
        }

        foreach ($textReason in @(Test-ForbiddenTextContent -Entry $entry -Normalized $normalized)) {
            $violations.Add([pscustomobject]@{
                package = $Label
                entry = $normalized
                reason = $textReason
                bytes = [int64]$entry.Length
            }) | Out-Null
        }

        if ($Depth -lt 2 -and $normalized.EndsWith(".zip") -and $entry.Length -gt 0 -and $entry.Length -le 600MB) {
            $stream = $entry.Open()
            try {
                $memory = New-Object System.IO.MemoryStream
                try {
                    $stream.CopyTo($memory)
                    $memory.Position = 0
                    $nestedArchive = New-Object System.IO.Compression.ZipArchive($memory, [System.IO.Compression.ZipArchiveMode]::Read, $false)
                    try {
                        $nested = Inspect-ZipArchive -Archive $nestedArchive -Label "$Label!$normalized" -Depth ($Depth + 1)
                        foreach ($nestedViolation in @($nested.violations)) {
                            $violations.Add($nestedViolation) | Out-Null
                        }
                    } finally {
                        $nestedArchive.Dispose()
                    }
                } finally {
                    $memory.Dispose()
                }
            } finally {
                $stream.Dispose()
            }
        }
    }

    return [pscustomobject]@{
        package = $Label
        entries = $entryCount
        bytes = $byteCount
        assemblies = $assemblyCount
        resources = $resourceCount
        violations = $violations.ToArray()
    }
}

if ($PackagePath.Count -eq 0) {
    if (-not (Test-Path -LiteralPath $ReleaseDir)) {
        throw "Release directory not found: $ReleaseDir"
    }

    $PackagePath = @(
        Get-ChildItem -LiteralPath $ReleaseDir -Filter "*.zip" -File |
            ForEach-Object { $_.FullName }
    )
}

if ($PackagePath.Count -eq 0) {
    throw "No release zip packages found."
}

$summaries = New-Object System.Collections.Generic.List[object]
$allViolations = New-Object System.Collections.Generic.List[object]

foreach ($package in $PackagePath) {
    if (-not (Test-Path -LiteralPath $package)) {
        throw "Package not found: $package"
    }

    $resolved = (Resolve-Path -LiteralPath $package).Path
    $archive = [System.IO.Compression.ZipFile]::OpenRead($resolved)
    try {
        $summary = Inspect-ZipArchive -Archive $archive -Label $resolved
        $summaries.Add($summary) | Out-Null
        foreach ($violation in $summary.violations) {
            $allViolations.Add($violation) | Out-Null
        }
    } finally {
        $archive.Dispose()
    }
}

$result = [pscustomobject]@{
    ok = $allViolations.Count -eq 0
    packageCount = $summaries.Count
    violationCount = $allViolations.Count
    packages = @($summaries | ForEach-Object {
        [pscustomobject]@{
            package = $_.package
            entries = $_.entries
            bytes = $_.bytes
            assemblies = $_.assemblies
            resources = $_.resources
        }
    })
    violations = $allViolations.ToArray()
}

if ($Json) {
    $result | ConvertTo-Json -Depth 6
} else {
    if ($result.ok) {
        Write-Host "Release surface audit: OK"
    } else {
        Write-Host "Release surface audit: FAILED"
    }

    foreach ($package in @($result.packages)) {
        Write-Host ("[checked] {0}: entries={1} assemblies={2} resources={3} bytes={4}" -f $package.package, $package.entries, $package.assemblies, $package.resources, $package.bytes)
    }

    if (-not $result.ok) {
        Write-Host ("[failed] forbidden release entries: {0}" -f $result.violationCount)
        foreach ($violation in @($result.violations | Select-Object -First 80)) {
            Write-Host ("  {0}: {1} ({2}, {3} bytes)" -f $violation.package, $violation.entry, $violation.reason, $violation.bytes)
        }
    }
}

if (-not $result.ok -and -not $WarnOnly) {
    exit 1
}
