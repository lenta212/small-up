[CmdletBinding()]
param(
    [string[]]$PackagePath = @(),
    [string]$ReleaseDir = "release",
    [ValidateSet('auto', 'client', 'server')]
    [string]$ExpectedPackageKind = 'auto',
    [switch]$AllowPartial,
    [switch]$Json,
    [switch]$WarnOnly
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$maxArchiveEntries = 100000
$maxArchiveUncompressedBytes = [int64]2GB
$maxEntryBytes = [int64]512MB
$maxTextBytes = [int64]16MB
$maxNestedZipBytes = [int64]512MB

$forbiddenExtensions = @(
    ".pdb", ".mdb", ".cs", ".fs", ".vb", ".csproj", ".fsproj", ".vbproj",
    ".sln", ".suo", ".user", ".log", ".tmp", ".cache", ".bak"
)
$forbiddenNames = @(
    ".env", ".env.local", ".env.production", "appsettings.development.json",
    "appsettings.local.json", "secrets.json", "nuget.config"
)
$forbiddenFragments = @("/.git/", "/.github/", "/.vs/", "/obj/", "/testresults/")
$textExtensionsToScan = @(
    ".cfg", ".conf", ".csv", ".ftl", ".html", ".ini", ".js", ".json", ".lua",
    ".md", ".ps1", ".toml", ".txt", ".xml", ".yaml", ".yml"
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

function ConvertTo-SafeArchivePath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$Context = 'ZIP entry path',
        [switch]$Directory
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -ne $Path.Trim() -or $Path -match '[\x00-\x1F]') {
        throw "$Context is empty, padded, or contains control characters: '$Path'"
    }
    $normalized = $Path.Replace('\', '/')
    if ($normalized -cne $normalized.Normalize([Text.NormalizationForm]::FormC)) {
        throw "$Context is not Unicode NFC canonical: '$Path'"
    }
    if ($Directory) {
        if (-not $normalized.EndsWith('/')) {
            throw "$Context was marked as a directory without a trailing slash: '$Path'"
        }
        $normalized = $normalized.Substring(0, $normalized.Length - 1)
    }
    if ([string]::IsNullOrWhiteSpace($normalized) -or $normalized.StartsWith('/') -or $normalized -match '^[A-Za-z]:') {
        throw "$Context is absolute or empty: '$Path'"
    }
    $parts = $normalized.Split([char]'/', [StringSplitOptions]::None)
    foreach ($part in $parts) {
        if ($part -eq '' -or $part -eq '.' -or $part -eq '..' -or $part.Contains(':') -or
            $part -ne $part.Trim() -or $part.EndsWith('.')) {
            throw "$Context contains an unsafe segment: '$Path'"
        }
        $deviceBase = [IO.Path]::GetFileNameWithoutExtension($part)
        if ($deviceBase -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
            throw "$Context contains a reserved Windows segment: '$Path'"
        }
    }
    return $parts -join '/'
}

function Get-ArchiveKind {
    param([string]$Label, [int]$Depth)

    if ($Depth -eq 0 -and $ExpectedPackageKind -ne 'auto') {
        return $ExpectedPackageKind
    }
    $leaf = (($Label -split '!') | Select-Object -Last 1)
    $name = [IO.Path]::GetFileName($leaf)
    if ($name -match '(?i)^(SS14|Content)\.Client(?:_|\.|$)') {
        return 'client'
    }
    if ($name -match '(?i)^SS14\.Server(?:_|\.|$)') {
        return 'server'
    }
    return 'other'
}

function Read-StrictZipText {
    param(
        [IO.Compression.ZipArchiveEntry]$Entry,
        [int64]$MaxBytes = $maxTextBytes
    )

    if ($Entry.Length -gt $MaxBytes) {
        throw "Text entry '$($Entry.FullName)' exceeds the $MaxBytes-byte audit limit."
    }
    $stream = $Entry.Open()
    try {
        $memory = [IO.MemoryStream]::new()
        try {
            $stream.CopyTo($memory)
            $text = [Text.UTF8Encoding]::new($false, $true).GetString($memory.ToArray())
            if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) { return $text.Substring(1) }
            return $text
        }
        finally { $memory.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Test-ForbiddenReleaseEntry {
    param([string]$EntryName)

    $normalized = ConvertTo-SafeArchivePath -Path $EntryName
    $lower = '/' + $normalized.ToLowerInvariant()
    $fileName = [IO.Path]::GetFileName($normalized).ToLowerInvariant()
    $extension = [IO.Path]::GetExtension($fileName).ToLowerInvariant()
    if ($forbiddenExtensions -contains $extension) { return "forbidden extension $extension" }
    if ($forbiddenNames -contains $fileName) { return "forbidden file name $fileName" }
    foreach ($fragment in $forbiddenFragments) {
        if ($lower.Contains($fragment)) { return "forbidden path fragment $fragment" }
    }
    return $null
}

function Test-ForbiddenClientEntry {
    param([string]$EntryName)

    $normalized = ConvertTo-SafeArchivePath -Path $EntryName
    $parts = @($normalized.Split('/') | ForEach-Object { $_.ToLowerInvariant() })
    $fileName = [IO.Path]::GetFileName($normalized).ToLowerInvariant()
    if ('serveronly' -in $parts) { return 'server-only resource in client package' }
    if ($fileName -like 'content.server*.dll') { return 'server assembly in client package' }
    if ('migrations' -in $parts) { return 'database migration in client package' }
    if ($fileName -eq 'client-package-canary.txt') { return 'server-only canary in client package' }
    return $null
}

function Test-ForbiddenTextContent {
    param([IO.Compression.ZipArchiveEntry]$Entry, [string]$Normalized)

    $extension = [IO.Path]::GetExtension($Normalized).ToLowerInvariant()
    if ($textExtensionsToScan -notcontains $extension) { return @() }
    if ($Entry.Length -gt $maxTextBytes) {
        return @("text entry exceeds the $maxTextBytes-byte privacy scan limit")
    }
    try { $text = Read-StrictZipText -Entry $Entry }
    catch { return @("text entry is not bounded strict UTF-8: $($_.Exception.Message)") }
    $matches = [System.Collections.Generic.List[string]]::new()
    foreach ($rule in $forbiddenTextPatterns) {
        if ([regex]::IsMatch($text, $rule.Pattern)) { $matches.Add($rule.Reason) | Out-Null }
    }
    return $matches.ToArray()
}

function Add-Violation {
    param(
        [System.Collections.Generic.List[object]]$List,
        [string]$Package,
        [string]$Entry,
        [string]$Reason,
        [int64]$Bytes = 0
    )
    $List.Add([pscustomobject]@{ package = $Package; entry = $Entry; reason = $Reason; bytes = $Bytes }) | Out-Null
}

function Inspect-ZipArchive {
    param(
        [IO.Compression.ZipArchive]$Archive,
        [string]$Label,
        [int]$Depth = 0
    )

    $entryCount = 0
    $byteCount = [int64]0
    $assemblyCount = 0
    $resourceCount = 0
    $violations = [System.Collections.Generic.List[object]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $filePaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $directoryPaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $kind = Get-ArchiveKind -Label $Label -Depth $Depth
    $serverOnlyCanarySeen = $false
    $clientServerOnlyAbsent = $true

    foreach ($entry in $Archive.Entries) {
        $entryCount += 1
        if ($entryCount -gt $maxArchiveEntries) { throw "Archive '$Label' exceeds $maxArchiveEntries entries." }
        if ($entry.Length -lt 0 -or $entry.Length -gt $maxEntryBytes -or
            $entry.Length -gt ($maxArchiveUncompressedBytes - $byteCount)) {
            throw "Archive '$Label' exceeds entry or total uncompressed-size limits at '$($entry.FullName)'."
        }
        $byteCount += [int64]$entry.Length

        $raw = $entry.FullName.Replace('\', '/')
        $isDirectory = $raw.EndsWith('/')
        $normalized = ConvertTo-SafeArchivePath -Path $raw -Directory:$isDirectory
        if (-not $seen.Add($normalized)) {
            throw "Archive '$Label' contains a duplicate or case-colliding path: '$($entry.FullName)'"
        }
        $pathParts = $normalized.Split('/')
        for ($index = 1; $index -lt $pathParts.Length; $index += 1) {
            $ancestor = $pathParts[0..($index - 1)] -join '/'
            if ($filePaths.Contains($ancestor)) {
                throw "Archive '$Label' treats file '$ancestor' as a directory prefix."
            }
            $directoryPaths.Add($ancestor) | Out-Null
        }
        if ($isDirectory) {
            if ($filePaths.Contains($normalized)) { throw "Archive '$Label' contains a file/directory collision: '$normalized'" }
            $directoryPaths.Add($normalized) | Out-Null
        }
        else {
            if ($directoryPaths.Contains($normalized)) { throw "Archive '$Label' contains a directory/file collision: '$normalized'" }
            $filePaths.Add($normalized) | Out-Null
        }
        if ($isDirectory) { continue }

        $lower = $normalized.ToLowerInvariant()
        if ($kind -eq 'server' -and $lower -in @(
            'serveronly/_luam/client-package-canary.txt',
            'resources/serveronly/_luam/client-package-canary.txt')) {
            try {
                $canaryText = Read-StrictZipText -Entry $entry -MaxBytes 4096
                if ($canaryText -notmatch '(?m)^LUAM_SERVER_ONLY_CANARY_20260714\s*$') {
                    Add-Violation -List $violations -Package $Label -Entry $normalized -Reason 'server-only canary content is invalid' -Bytes $entry.Length
                }
                else { $serverOnlyCanarySeen = $true }
            }
            catch {
                Add-Violation -List $violations -Package $Label -Entry $normalized -Reason "server-only canary is unreadable: $($_.Exception.Message)" -Bytes $entry.Length
            }
        }
        if ($normalized.StartsWith('Assemblies/', [StringComparison]::OrdinalIgnoreCase) -or
            $normalized.StartsWith('Resources/Assemblies/', [StringComparison]::OrdinalIgnoreCase)) { $assemblyCount += 1 }
        if ($normalized.StartsWith('Resources/', [StringComparison]::OrdinalIgnoreCase)) { $resourceCount += 1 }

        $reason = Test-ForbiddenReleaseEntry -EntryName $normalized
        if ($null -ne $reason) { Add-Violation -List $violations -Package $Label -Entry $normalized -Reason $reason -Bytes $entry.Length }
        if ($kind -eq 'client') {
            $clientReason = Test-ForbiddenClientEntry -EntryName $normalized
            if ($null -ne $clientReason) {
                if ($clientReason -like 'server-only*' -or $clientReason -like 'server-only canary*') { $clientServerOnlyAbsent = $false }
                Add-Violation -List $violations -Package $Label -Entry $normalized -Reason $clientReason -Bytes $entry.Length
            }
        }
        foreach ($textReason in @(Test-ForbiddenTextContent -Entry $entry -Normalized $normalized)) {
            Add-Violation -List $violations -Package $Label -Entry $normalized -Reason $textReason -Bytes $entry.Length
        }

        if ($Depth -lt 2 -and $normalized.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase) -and $entry.Length -gt 0) {
            if ($entry.Length -gt $maxNestedZipBytes) {
                Add-Violation -List $violations -Package $Label -Entry $normalized -Reason 'nested ZIP exceeds bounded audit size' -Bytes $entry.Length
                continue
            }
            $temporary = [IO.Path]::GetTempFileName()
            try {
                $source = $entry.Open()
                try {
                    $destination = [IO.File]::Open($temporary, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
                    try { $source.CopyTo($destination, 1MB) }
                    finally { $destination.Dispose() }
                }
                finally { $source.Dispose() }
                try {
                    $nestedArchive = [IO.Compression.ZipFile]::OpenRead($temporary)
                    try {
                        $nested = Inspect-ZipArchive -Archive $nestedArchive -Label "$Label!$normalized" -Depth ($Depth + 1)
                        foreach ($nestedViolation in @($nested.violations)) { $violations.Add($nestedViolation) | Out-Null }
                    }
                    finally { $nestedArchive.Dispose() }
                }
                catch {
                    Add-Violation -List $violations -Package $Label -Entry $normalized -Reason "nested ZIP is invalid or unsafe: $($_.Exception.Message)" -Bytes $entry.Length
                }
            }
            finally { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
        }
    }

    if ($kind -eq 'server' -and -not $serverOnlyCanarySeen) {
        Add-Violation -List $violations -Package $Label -Entry '<missing:ServerOnly/_LuaM/client-package-canary.txt>' -Reason 'server-only canary missing from server package'
    }
    return [pscustomobject]@{
        package = $Label
        kind = $kind
        entries = $entryCount
        bytes = $byteCount
        assemblies = $assemblyCount
        resources = $resourceCount
        serverOnlyCanaryPresent = $serverOnlyCanarySeen
        clientServerOnlyAbsent = $clientServerOnlyAbsent
        violations = $violations.ToArray()
    }
}

$expandedPackagePaths = [System.Collections.Generic.List[string]]::new()
foreach ($candidate in @($PackagePath)) {
    foreach ($piece in @(([string]$candidate).Split([char]',', [StringSplitOptions]::RemoveEmptyEntries))) {
        $trimmed = $piece.Trim()
        if (-not [string]::IsNullOrWhiteSpace($trimmed)) { $expandedPackagePaths.Add($trimmed) | Out-Null }
    }
}
$PackagePath = @($expandedPackagePaths.ToArray())

if ($ExpectedPackageKind -ne 'auto' -and $PackagePath.Count -ne 1) {
    throw '-ExpectedPackageKind requires exactly one -PackagePath.'
}
if ($PackagePath.Count -eq 0) {
    if (-not (Test-Path -LiteralPath $ReleaseDir -PathType Container)) { throw "Release directory not found: $ReleaseDir" }
    $PackagePath = @(Get-ChildItem -LiteralPath $ReleaseDir -Filter '*.zip' -File | ForEach-Object { $_.FullName })
}
if ($PackagePath.Count -eq 0) { throw 'No release zip packages found.' }

$summaries = [System.Collections.Generic.List[object]]::new()
$allViolations = [System.Collections.Generic.List[object]]::new()
foreach ($package in $PackagePath) {
    if (-not (Test-Path -LiteralPath $package -PathType Leaf)) { throw "Package not found: $package" }
    $resolved = (Resolve-Path -LiteralPath $package).Path
    $archive = [IO.Compression.ZipFile]::OpenRead($resolved)
    try { $summary = Inspect-ZipArchive -Archive $archive -Label $resolved }
    finally { $archive.Dispose() }
    $summaries.Add($summary) | Out-Null
    foreach ($violation in @($summary.violations)) { $allViolations.Add($violation) | Out-Null }
}

if (-not $AllowPartial) {
    $clients = @($summaries | Where-Object { $_.kind -eq 'client' })
    $servers = @($summaries | Where-Object { $_.kind -eq 'server' })
    if ($clients.Count -ne 1) {
        Add-Violation -List $allViolations -Package '<release-set>' -Entry '<client-package>' -Reason "release set must contain exactly one client package; found $($clients.Count)"
    }
    if ($servers.Count -lt 1) {
        Add-Violation -List $allViolations -Package '<release-set>' -Entry '<server-package>' -Reason 'release set must contain at least one server package'
    }
}

$result = [pscustomobject]@{
    ok = $allViolations.Count -eq 0
    pairRequired = -not [bool]$AllowPartial
    packageCount = $summaries.Count
    violationCount = $allViolations.Count
    clientPackageCount = @($summaries | Where-Object { $_.kind -eq 'client' }).Count
    serverPackageCount = @($summaries | Where-Object { $_.kind -eq 'server' }).Count
    packages = @($summaries | ForEach-Object {
        [pscustomobject]@{
            package = $_.package
            kind = $_.kind
            entries = $_.entries
            bytes = $_.bytes
            assemblies = $_.assemblies
            resources = $_.resources
            serverOnlyCanaryPresent = $_.serverOnlyCanaryPresent
            clientServerOnlyAbsent = $_.clientServerOnlyAbsent
        }
    })
    violations = $allViolations.ToArray()
}

if ($Json) { $result | ConvertTo-Json -Depth 6 }
else {
    Write-Host "Release surface audit: $(if ($result.ok) { 'OK' } else { 'FAILED' })"
    foreach ($package in $result.packages) {
        Write-Host ("[checked] {0}: kind={1} entries={2} assemblies={3} resources={4} bytes={5}" -f $package.package, $package.kind, $package.entries, $package.assemblies, $package.resources, $package.bytes)
    }
    foreach ($violation in @($result.violations | Select-Object -First 80)) {
        Write-Host ("  {0}: {1} ({2}, {3} bytes)" -f $violation.package, $violation.entry, $violation.reason, $violation.bytes)
    }
}
if (-not $result.ok -and -not $WarnOnly) { exit 1 }
