param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,
    [Parameter(Mandatory = $true)]
    [string]$ExpectedSha256,
    [switch]$Json
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "luam_release_contract.ps1")
$issues = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]
$steps = New-Object System.Collections.Generic.List[object]

function Add-Step {
    param(
        [string]$Name,
        [string]$Status,
        [string]$Detail = ""
    )

    $steps.Add([pscustomobject]@{
        name = $Name
        status = $Status
        detail = $Detail
    }) | Out-Null
}

function ConvertTo-SafeZipPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [string]$Context = "package path"
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -ne $Path.Trim()) {
        throw "$Context is empty or has leading/trailing whitespace: '$Path'"
    }

    $normalized = $Path.Replace('\', '/')
    if ($normalized -cne $normalized.Normalize([Text.NormalizationForm]::FormC)) {
        throw "$Context is not Unicode NFC canonical: '$Path'"
    }
    if ($normalized.StartsWith('/') -or $normalized -match '^[A-Za-z]:' -or $normalized -match '[\x00-\x1F]') {
        throw "$Context is absolute or contains control characters: '$Path'"
    }

    $parts = $normalized.Split([char]'/', [System.StringSplitOptions]::None)
    foreach ($part in $parts) {
        if ($part -eq '' -or $part -eq '.' -or $part -eq '..' -or $part.Contains(':') -or
            $part -ne $part.Trim() -or $part.EndsWith('.')) {
            throw "$Context contains an unsafe path segment: '$Path'"
        }
    }

    return $parts -join '/'
}

function Normalize-ZipPath {
    param([string]$Path)

    return ConvertTo-SafeZipPath -Path $Path
}

function Get-SafeZipEntryIndex {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.Compression.ZipArchive]$Archive,
        [int]$MaxEntries = 100000,
        [int64]$MaxUncompressedBytes = 2GB,
        [int64]$MaxEntryBytes = 512MB
    )

    $seenPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $files = [System.Collections.Generic.Dictionary[string, System.IO.Compression.ZipArchiveEntry]]::new(
        [System.StringComparer]::OrdinalIgnoreCase
    )
    $filePaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $directoryPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $entryCount = 0
    $totalUncompressedBytes = [int64] 0

    foreach ($entry in $Archive.Entries) {
        $entryCount += 1
        if ($entryCount -gt $MaxEntries) {
            throw "Package has too many ZIP entries: $entryCount > $MaxEntries"
        }

        if ($entry.Length -lt 0 -or $entry.Length -gt $MaxEntryBytes) {
            throw "Package ZIP entry exceeds the per-entry limit of $MaxEntryBytes bytes: $($entry.FullName)"
        }
        if ($entry.Length -gt ($MaxUncompressedBytes - $totalUncompressedBytes)) {
            throw "Package uncompressed size exceeds limit: $MaxUncompressedBytes bytes"
        }
        $totalUncompressedBytes += [int64] $entry.Length

        $rawPath = $entry.FullName.Replace('\', '/')
        $isDirectory = $rawPath.EndsWith('/')
        $pathToValidate = if ($isDirectory) { $rawPath.Substring(0, $rawPath.Length - 1) } else { $rawPath }
        $canonicalPath = ConvertTo-SafeZipPath -Path $pathToValidate -Context "ZIP entry path"
        if (-not $seenPaths.Add($canonicalPath)) {
            throw "Package contains a duplicate or case-colliding ZIP path: $($entry.FullName)"
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

        if (-not $isDirectory) {
            $files.Add($canonicalPath, $entry)
        }
    }

    return [pscustomobject]@{
        Files = $files
        EntryCount = $entryCount
        TotalUncompressedBytes = $totalUncompressedBytes
    }
}

function Test-NonNegativeJsonInteger {
    param([object]$Value)

    return ((($Value -is [int]) -or ($Value -is [long])) -and ([int64] $Value -ge 0))
}

function Read-ZipText {
    param(
        [System.IO.Compression.ZipArchive]$Archive,
        [string]$EntryName,
        [int64]$MaxBytes = 16MB
    )

    $entry = $Archive.GetEntry($EntryName)
    if ($null -eq $entry) {
        throw "Missing package entry: $EntryName"
    }
    if ($entry.Length -gt $MaxBytes) {
        throw "Package control entry exceeds the $MaxBytes byte text limit: $EntryName"
    }

    $text = [System.Text.UTF8Encoding]::new($false, $true).GetString((Get-ZipEntryBytes -Entry $entry -MaxBytes $MaxBytes))
    if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) { return $text.Substring(1) }
    return $text
}

function Get-ZipEntrySha256 {
    param([System.IO.Compression.ZipArchiveEntry]$Entry)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = $Entry.Open()
    try {
        $hash = $sha.ComputeHash($stream)
        return ([BitConverter]::ToString($hash)).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $stream.Dispose()
        $sha.Dispose()
    }
}

function Get-ZipEntryBytes {
    param(
        [System.IO.Compression.ZipArchiveEntry]$Entry,
        [int64]$MaxBytes = 16MB
    )

    if ($Entry.Length -gt $MaxBytes) {
        throw "Text payload exceeds the $MaxBytes byte in-memory validation limit: $($Entry.FullName)"
    }

    $stream = $Entry.Open()
    try {
        $memory = [System.IO.MemoryStream]::new()
        try {
            $stream.CopyTo($memory)
            return $memory.ToArray()
        }
        finally {
            $memory.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
    throw "Package not found: $PackagePath"
}

if ([string]::IsNullOrWhiteSpace($ExpectedSha256) -or $ExpectedSha256.Trim() -notmatch '^[0-9A-Fa-f]{64}$') {
    throw "ExpectedSha256 must contain exactly 64 hexadecimal characters."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$expectedPackageSha256 = $ExpectedSha256.Trim().ToLowerInvariant()
$packageHash = Get-FileHash -Algorithm SHA256 -LiteralPath $resolvedPackage
$actualPackageSha256 = $packageHash.Hash.ToLowerInvariant()
if ($actualPackageSha256 -ne $expectedPackageSha256) {
    throw "Package SHA256 mismatch. Expected $expectedPackageSha256, got $actualPackageSha256"
}
Add-Step "package-hash" "passed" "Package SHA256 matches the independently supplied expected hash $actualPackageSha256."

$archive = [System.IO.Compression.ZipFile]::OpenRead($resolvedPackage)
try {
    $archiveIndex = Get-SafeZipEntryIndex -Archive $archive
    $entryByPath = $archiveIndex.Files
    Add-Step "archive-safety" "passed" "Validated $($archiveIndex.EntryCount) canonical ZIP entries and $($archiveIndex.TotalUncompressedBytes) uncompressed bytes."

    $manifestText = Read-ZipText -Archive $archive -EntryName "PACKAGE_MANIFEST.json"
    $fileListText = Read-ZipText -Archive $archive -EntryName "PACKAGE_FILES.txt"
    $releasePolicyText = Read-ZipText -Archive $archive -EntryName "Tools/luam_release_policy.json" -MaxBytes 4MB
    $manifest = $manifestText | ConvertFrom-Json
    $releasePolicy = $releasePolicyText | ConvertFrom-Json
    Assert-LuaMReleasePolicy -Policy $releasePolicy
    $policyRequiredFiles = @(Get-LuaMReleaseGateRequiredFiles -Policy $releasePolicy)
    $policyExcludedArtifacts = @(Get-LuaMReleaseExcludedLocalArtifacts -Policy $releasePolicy)
    $policyApprovedOutsidePackageFiles = @(Get-LuaMReleaseApprovedOutsidePackageFiles -Policy $releasePolicy)
    Add-Step "manifest-readable" "passed" "PACKAGE_MANIFEST.json, PACKAGE_FILES.txt, and the packaged release policy are readable."

    if ($null -eq $manifest -or $manifest -is [System.Array]) {
        throw "PACKAGE_MANIFEST.json must contain one JSON object."
    }
    $manifestProperties = @('schemaVersion', 'name', 'generatedAtUtc', 'policySha256', 'payloadDigestSha256', 'sourceReceipt', 'productionEligible', 'git', 'readiness', 'scopeAudit', 'fileCount', 'files')
    Assert-LuaMJsonProperties -Value $manifest -Name 'source package manifest' -Required $manifestProperties -Allowed $manifestProperties
    if (
        $manifest.PSObject.Properties.Name -notcontains "schemaVersion" -or
        (($manifest.schemaVersion -isnot [int]) -and ($manifest.schemaVersion -isnot [long])) -or
        [int64]$manifest.schemaVersion -ne 2 -or
        $manifest.PSObject.Properties.Name -contains "sourceRoot" -or
        $manifest.PSObject.Properties.Name -notcontains "policySha256" -or
        $manifest.PSObject.Properties.Name -notcontains "payloadDigestSha256" -or
        $manifest.PSObject.Properties.Name -notcontains "sourceReceipt" -or
        $manifest.PSObject.Properties.Name -notcontains "productionEligible" -or
        $manifest.PSObject.Properties.Name -notcontains "git" -or
        $manifest.PSObject.Properties.Name -notcontains "readiness" -or
        $manifest.PSObject.Properties.Name -notcontains "scopeAudit" -or
        $manifest.PSObject.Properties.Name -notcontains "fileCount" -or
        $manifest.PSObject.Properties.Name -notcontains "files" -or
        -not (Test-NonNegativeJsonInteger -Value $manifest.fileCount) -or
        $manifest.fileCount -gt 100000 -or $manifest.files -isnot [System.Array]) {
        throw "PACKAGE_MANIFEST.json has an invalid versioned release schema."
    }
    if ($manifest.policySha256 -isnot [string] -or $manifest.policySha256 -notmatch '^[0-9A-Fa-f]{64}$' -or
        $manifest.payloadDigestSha256 -isnot [string] -or $manifest.payloadDigestSha256 -notmatch '^[0-9A-Fa-f]{64}$' -or
        $manifest.productionEligible -isnot [bool]) {
        throw "PACKAGE_MANIFEST.json has invalid policy, payload, or production bindings."
    }
    Assert-LuaMNonEmptyString -Value $manifest.name -Name 'source package manifest name' -MaxLength 256 -Pattern '^[A-Za-z0-9][A-Za-z0-9_.-]*$'
    $sourceGeneratedAt = ConvertTo-LuaMUtcTimestamp -Value $manifest.generatedAtUtc -Name 'source package generatedAtUtc'
    $sourceNow = [DateTimeOffset]::UtcNow
    if ($sourceGeneratedAt -gt $sourceNow.AddMinutes(5) -or $sourceGeneratedAt -lt $sourceNow.AddHours(-24)) {
        throw "Production source-package evidence must have been generated within the last 24 hours."
    }

    $sourceReceiptProperties = @('schemaVersion', 'gitHead', 'gitBranch', 'digestSha256', 'changedFileCount', 'untrackedFileCount', 'trackedForProduction')
    Assert-LuaMJsonProperties -Value $manifest.sourceReceipt -Name 'source package worktree receipt' -Required $sourceReceiptProperties -Allowed $sourceReceiptProperties
    if (($manifest.sourceReceipt.schemaVersion -isnot [int] -and $manifest.sourceReceipt.schemaVersion -isnot [long]) -or
        [int64]$manifest.sourceReceipt.schemaVersion -ne 1 -or
        $manifest.sourceReceipt.gitHead -isnot [string] -or $manifest.sourceReceipt.gitHead -notmatch '^[0-9a-fA-F]{40,64}$' -or
        $manifest.sourceReceipt.gitBranch -isnot [string] -or [string]::IsNullOrWhiteSpace($manifest.sourceReceipt.gitBranch) -or
        $manifest.sourceReceipt.digestSha256 -isnot [string] -or $manifest.sourceReceipt.digestSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        -not (Test-NonNegativeJsonInteger -Value $manifest.sourceReceipt.changedFileCount) -or
        -not (Test-NonNegativeJsonInteger -Value $manifest.sourceReceipt.untrackedFileCount) -or
        [int64]$manifest.sourceReceipt.untrackedFileCount -lt 0 -or
        $manifest.sourceReceipt.trackedForProduction -isnot [bool]) {
        throw "PACKAGE_MANIFEST.json has an invalid source worktree receipt."
    }
    Assert-LuaMNonEmptyString -Value $manifest.sourceReceipt.gitBranch -Name 'source worktree Git branch' -MaxLength 256

    $gitProperties = @('branch', 'head', 'untrackedReleaseFiles')
    Assert-LuaMJsonProperties -Value $manifest.git -Name 'source package Git metadata' -Required $gitProperties -Allowed $gitProperties
    if ($manifest.git.branch -isnot [string] -or [string]::IsNullOrWhiteSpace($manifest.git.branch) -or
        $manifest.git.head -isnot [string] -or $manifest.git.head -notmatch '^[0-9a-fA-F]{40,64}$' -or
        -not (Test-NonNegativeJsonInteger -Value $manifest.git.untrackedReleaseFiles)) {
        throw "PACKAGE_MANIFEST.json has invalid Git metadata."
    }
    Assert-LuaMNonEmptyString -Value $manifest.git.branch -Name 'source package Git branch' -MaxLength 256

    $readinessProperties = @('ok', 'productionEligible', 'allowUntracked', 'runTests', 'runLocalSmoke', 'releaseGate', 'worktree', 'issues', 'warnings', 'steps')
    Assert-LuaMJsonProperties -Value $manifest.readiness -Name 'source package readiness evidence' -Required $readinessProperties -Allowed $readinessProperties
    foreach ($booleanProperty in @('ok', 'productionEligible', 'allowUntracked', 'runTests', 'runLocalSmoke')) {
        if ($manifest.readiness.$booleanProperty -isnot [bool]) {
            throw "Readiness evidence property '$booleanProperty' must be a JSON boolean."
        }
    }
    foreach ($arrayProperty in @('issues', 'warnings', 'steps')) {
        if ($manifest.readiness.$arrayProperty -isnot [System.Array]) {
            throw "Readiness evidence property '$arrayProperty' must be a JSON array."
        }
    }
    $readinessWorktreeProperties = @('schemaVersion', 'gitHead', 'gitBranch', 'digestSha256', 'changedFileCount', 'untrackedFileCount', 'trackedForProduction')
    Assert-LuaMJsonProperties -Value $manifest.readiness.worktree -Name 'readiness worktree receipt' -Required $readinessWorktreeProperties -Allowed $readinessWorktreeProperties
    if (($manifest.readiness.worktree.schemaVersion -isnot [int] -and $manifest.readiness.worktree.schemaVersion -isnot [long]) -or
        [int64]$manifest.readiness.worktree.schemaVersion -ne 1 -or
        $manifest.readiness.worktree.gitHead -isnot [string] -or $manifest.readiness.worktree.gitHead -notmatch '^[0-9a-fA-F]{40,64}$' -or
        $manifest.readiness.worktree.digestSha256 -isnot [string] -or $manifest.readiness.worktree.digestSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        -not (Test-NonNegativeJsonInteger -Value $manifest.readiness.worktree.changedFileCount) -or
        -not (Test-NonNegativeJsonInteger -Value $manifest.readiness.worktree.untrackedFileCount) -or
        $manifest.readiness.worktree.trackedForProduction -isnot [bool]) {
        throw "Readiness worktree receipt has invalid typed bindings."
    }
    Assert-LuaMNonEmptyString -Value $manifest.readiness.worktree.gitBranch -Name 'readiness worktree Git branch' -MaxLength 256
    $readinessGateProperties = @('schemaVersion', 'policySha256', 'requiredFileCount', 'productionTests', 'smokeChecks')
    Assert-LuaMJsonProperties -Value $manifest.readiness.releaseGate -Name 'readiness release gate evidence' -Required $readinessGateProperties -Allowed $readinessGateProperties
    if (($manifest.readiness.releaseGate.schemaVersion -isnot [int] -and $manifest.readiness.releaseGate.schemaVersion -isnot [long]) -or
        $manifest.readiness.releaseGate.policySha256 -isnot [string] -or $manifest.readiness.releaseGate.policySha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        -not (Test-NonNegativeJsonInteger -Value $manifest.readiness.releaseGate.requiredFileCount) -or
        $manifest.readiness.releaseGate.productionTests -isnot [System.Array] -or
        $manifest.readiness.releaseGate.smokeChecks -isnot [System.Array]) {
        throw "Readiness release gate evidence has invalid typed bindings."
    }

    $scopeAuditProperties = @('changedFileCount', 'packagedChangedFileCount', 'changedFilesOutsidePackageCount', 'allowedChangedFilesOutsidePackage', 'unexpectedChangedFilesOutsidePackage')
    Assert-LuaMJsonProperties -Value $manifest.scopeAudit -Name 'source package scope audit' -Required $scopeAuditProperties -Allowed $scopeAuditProperties
    foreach ($countProperty in @('changedFileCount', 'packagedChangedFileCount', 'changedFilesOutsidePackageCount')) {
        if (-not (Test-NonNegativeJsonInteger -Value $manifest.scopeAudit.$countProperty)) {
            throw "Scope audit property '$countProperty' must be a non-negative JSON integer."
        }
    }
    foreach ($arrayProperty in @('allowedChangedFilesOutsidePackage', 'unexpectedChangedFilesOutsidePackage')) {
        if ($manifest.scopeAudit.$arrayProperty -isnot [System.Array]) {
            throw "Scope audit property '$arrayProperty' must be a JSON array."
        }
    }

    $listedFileList = [System.Collections.Generic.List[string]]::new()
    $listedFileSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($line in @($fileListText -split "`r?`n")) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $listedPath = ConvertTo-SafeZipPath -Path $line -Context "PACKAGE_FILES path"
        if (-not $listedFileSet.Add($listedPath)) {
            throw "PACKAGE_FILES.txt contains a duplicate or case-colliding path: $listedPath"
        }
        $listedFileList.Add($listedPath) | Out-Null
    }
    $listedFiles = @($listedFileList.ToArray())

    $manifestFileList = [System.Collections.Generic.List[string]]::new()
    $manifestFileSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($file in @($manifest.files)) {
        if ($null -eq $file -or
            $file.PSObject.Properties.Name -notcontains "path" -or
            $file.PSObject.Properties.Name -notcontains "sha256" -or
            $file.PSObject.Properties.Name -notcontains "bytes" -or
            $file.path -isnot [string] -or $file.sha256 -isnot [string] -or
            $file.sha256 -notmatch '^[0-9A-Fa-f]{64}$' -or
            -not (Test-NonNegativeJsonInteger -Value $file.bytes) -or
            [int64] $file.bytes -gt 512MB) {
            throw "PACKAGE_MANIFEST.json contains an invalid file path/hash/bytes record."
        }
        Assert-LuaMJsonProperties -Value $file -Name 'source package file record' -Required @('path', 'sha256', 'bytes') -Allowed @('path', 'sha256', 'bytes')

        $manifestPath = ConvertTo-SafeZipPath -Path $file.path -Context "manifest file path"
        if (-not $manifestFileSet.Add($manifestPath)) {
            throw "PACKAGE_MANIFEST.json contains a duplicate or case-colliding path: $manifestPath"
        }
        $manifestFileList.Add($manifestPath) | Out-Null
    }
    $manifestFiles = @($manifestFileList.ToArray())

    $zipFiles = @($entryByPath.Keys)
    $payloadZipFiles = @($zipFiles | Where-Object { $_ -notin @("PACKAGE_MANIFEST.json", "PACKAGE_FILES.txt") })
    $payloadZipFileSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($zipFile in $payloadZipFiles) {
        $payloadZipFileSet.Add($zipFile) | Out-Null
    }

    if ([int64] $manifest.fileCount -ne $listedFiles.Count -or
        $manifestFiles.Count -ne $listedFiles.Count -or
        $payloadZipFiles.Count -ne $listedFiles.Count) {
        $issues.Add("Manifest fileCount $($manifest.fileCount) does not match PACKAGE_FILES count $($listedFiles.Count).") | Out-Null
    }

    $manifestMissingFromList = @($manifestFiles | Where-Object { -not $listedFileSet.Contains($_) })
    $listMissingFromManifest = @($listedFiles | Where-Object { -not $manifestFileSet.Contains($_) })
    if ($manifestMissingFromList.Count -gt 0 -or $listMissingFromManifest.Count -gt 0) {
        $issues.Add("Manifest/PACKAGE_FILES path sets differ. Manifest-only: $($manifestMissingFromList -join ', '); list-only: $($listMissingFromManifest -join ', ')") | Out-Null
    }

    $missingFromZip = @($listedFiles | Where-Object { -not $payloadZipFileSet.Contains($_) })
    if ($missingFromZip.Count -gt 0) {
        $issues.Add("PACKAGE_FILES lists missing zip entries: $($missingFromZip -join ', ')") | Out-Null
    }

    $unexpectedPayload = @(
        $payloadZipFiles |
            Where-Object { -not $listedFileSet.Contains($_) }
    )
    if ($unexpectedPayload.Count -gt 0) {
        $issues.Add("Zip contains payload entries not listed in PACKAGE_FILES: $($unexpectedPayload -join ', ')") | Out-Null
    }

    $junk = @(
        $zipFiles |
            Where-Object {
                $_ -match "(^|/)(bin|obj|\.vs|\.idea|\.vscode|node_modules|__pycache__|\.pytest_cache|logs?|tmp|temp)(/|$)|\.(log|tmp|bak|cache|db|sqlite|sqlite3)$" -or
                (Test-LuaMReleaseExcludedLocalArtifact -Path $_ -ExcludedArtifacts $policyExcludedArtifacts)
            }
    )
    if ($junk.Count -gt 0) {
        $issues.Add("Package contains local junk paths: $($junk -join ', ')") | Out-Null
    }

    if ($issues.Count -eq 0) {
        Add-Step "file-list" "passed" "Manifest, file list, and zip payload entries match."
    } else {
        Add-Step "file-list" "failed" "Manifest/file-list mismatch detected."
    }

    $hashMismatches = New-Object System.Collections.Generic.List[string]
    $actualPayloadRecords = New-Object System.Collections.Generic.List[object]
    foreach ($file in @($manifest.files)) {
        $path = Normalize-ZipPath $file.path
        $entry = $entryByPath[$path]
        if ($null -eq $entry) {
            $hashMismatches.Add("$path missing from archive") | Out-Null
            continue
        }

        $actual = Get-ZipEntrySha256 -Entry $entry
        $actualPayloadRecords.Add([pscustomobject]@{ path = $path; sha256 = $actual; bytes = [int64]$entry.Length }) | Out-Null
        if (-not $actual.Equals([string] $file.sha256, [System.StringComparison]::OrdinalIgnoreCase)) {
            $hashMismatches.Add("$path expected $($file.sha256) actual $actual") | Out-Null
        }

        if ($entry.Length -ne [int64] $file.bytes) {
            $hashMismatches.Add("$path expected $($file.bytes) bytes actual $($entry.Length)") | Out-Null
        }
    }

    if ($hashMismatches.Count -gt 0) {
        $issues.Add("Payload hash/size mismatch: $($hashMismatches -join '; ')") | Out-Null
        Add-Step "payload-hashes" "failed" "$($hashMismatches.Count) mismatch(es)."
    } else {
        Add-Step "payload-hashes" "passed" "All payload SHA256 hashes and sizes match PACKAGE_MANIFEST.json."
    }

    $actualPayloadDigestSha256 = Get-LuaMFileRecordDigest -Files @($actualPayloadRecords.ToArray())
    if (-not $actualPayloadDigestSha256.Equals([string]$manifest.payloadDigestSha256, [System.StringComparison]::OrdinalIgnoreCase)) {
        $issues.Add("Payload digest does not match PACKAGE_MANIFEST.json. Expected $($manifest.payloadDigestSha256), got $actualPayloadDigestSha256.") | Out-Null
        Add-Step "payload-digest" "failed" "Payload record digest mismatch."
    } else {
        Add-Step "payload-digest" "passed" "Payload digest $actualPayloadDigestSha256 binds the complete file record set."
    }

    $textExtensions = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($extension in @(".cs", ".xaml", ".yml", ".yaml", ".ftl", ".toml", ".md", ".py", ".ps1", ".txt", ".xml", ".json", ".jsonc", ".cfg", ".config")) {
        $textExtensions.Add($extension) | Out-Null
    }

    $strictUtf8 = [System.Text.UTF8Encoding]::new($false, $true)
    $invalidUtf8 = New-Object System.Collections.Generic.List[string]
    foreach ($file in @($manifest.files)) {
        $path = Normalize-ZipPath $file.path
        if (-not $textExtensions.Contains([System.IO.Path]::GetExtension($path))) {
            continue
        }

        $entry = $entryByPath[$path]
        if ($null -eq $entry) {
            continue
        }

        try {
            [void] $strictUtf8.GetString((Get-ZipEntryBytes -Entry $entry))
        }
        catch {
            $invalidUtf8.Add($path) | Out-Null
        }
    }

    if ($invalidUtf8.Count -gt 0) {
        $issues.Add("Package text files are not strict UTF-8: $($invalidUtf8 -join ', ')") | Out-Null
        Add-Step "utf8-package-text" "failed" "$($invalidUtf8.Count) invalid UTF-8 file(s)."
    } else {
        Add-Step "utf8-package-text" "passed" "Package text files are strict UTF-8."
    }

    $adminRankArtifacts = @(
        "DeploymentPackages/LuaM/luam-admin-ranks.sqlite.sql",
        "DeploymentPackages/LuaM/luam-admin-ranks.postgres.sql",
        "DeploymentPackages/LuaM/luam-admin-ranks.md"
    )
    $missingAdminRankArtifacts = @($adminRankArtifacts | Where-Object { $_ -notin $listedFiles })
    if ($missingAdminRankArtifacts.Count -gt 0) {
        $issues.Add("Package is missing admin rank artifact(s): $($missingAdminRankArtifacts -join ', ')") | Out-Null
        Add-Step "admin-rank-artifacts" "failed" "$($missingAdminRankArtifacts.Count) missing artifact(s)."
    } else {
        Add-Step "admin-rank-artifacts" "passed" "Generated admin rank SQL/markdown artifacts are packaged."
    }

    $requiredLuaMArtifacts = @(
        "Content.Shared/CCVar/CCVars.LuaM.cs",
        "Content.Shared/CCVar/CCVars.Misc.cs",
        "Content.Shared/Inventory/SlotFlags.cs",
        "Content.Shared/Nutrition/AnimalHusbandry/ReproductiveComponent.cs",
        "Content.Shared/Preferences/HumanoidCharacterProfile.cs",
        "Content.Shared/_LuaM/Sector/LuaMAiTtsAudioEvent.cs",
        "Content.Client/Administration/UI/Tabs/AdminTab/AdminTab.xaml",
        "Content.Client/Administration/UI/Tabs/AdminTab/AdminTab.xaml.cs",
        "Content.Client/Clothing/ClientClothingSystem.cs",
        "Content.Client/Lobby/LobbyState.cs",
        "Content.Client/Players/PlayTimeTracking/JobRequirementsManager.cs",
        "Content.Client/RoundEnd/RoundEndSummaryWindow.cs",
        "Content.Client/_LuaM/Sector/LuaMAiTtsAudioSystem.cs",
        "Content.IntegrationTests/Pair/TestPair.cs",
        "Content.IntegrationTests/PoolManager.Cvars.cs",
        "Content.IntegrationTests/Tests/Gateway/GatewayGeneratorGrowthLimitTest.cs",
        "Content.IntegrationTests/Tests/_LuaM/LuaMAnimalHusbandryIntervalTest.cs",
        "Content.IntegrationTests/Tests/_LuaM/LuaMAnimalPopulationControlTest.cs",
        "Content.IntegrationTests/Tests/_LuaM/LuaMSectorTrafficTest.cs",
        "Content.IntegrationTests/Tests/_LuaM/LuaMSectorTrafficInterceptTest.cs",
        "Content.IntegrationTests/Tests/_LuaM/LuaMTimedSpawnerLimitTest.cs",
        "Content.IntegrationTests/Tests/_LuaM/LuaMShipyardPurchaseDurabilityContractTest.cs",
        "Content.IntegrationTests/Tests/_LuaM/LuaMCharacterPersistenceTest.cs",
        "Content.IntegrationTests/Tests/_LuaM/LuaMCharacterTtsValidationTest.cs",
        "Content.IntegrationTests/Tests/_LuaM/LuaMDynamicEventGrowthLimitTest.cs",
        "Content.IntegrationTests/Tests/_LuaM/LuaMExpeditionPlannerTest.cs",
        "Content.IntegrationTests/Tests/_LuaM/LuaMProgressionRulesTest.cs",
        "Content.IntegrationTests/Tests/_LuaM/LuaMDonationShopTest.cs",
        "Content.IntegrationTests/Tests/_LuaM/LuaMDynamicEventDebrisTest.cs",
        "Content.Tests/Server/_LuaM/LuaMSectorPlayerBriefingTest.cs",
        "Content.Server/_LuaM/Donation/LuaMDonationShopCommand.cs",
        "Content.Server/_LuaM/Donation/LuaMDonationShopSystem.cs",
        "Content.Server/_LuaM/Expeditions/LuaMExpeditionPlan.cs",
        "Content.Server/_LuaM/Expeditions/LuaMExpeditionPlanCommand.cs",
        "Content.Server/_LuaM/Expeditions/LuaMExpeditionPlanValidator.cs",
        "Content.Server/_LuaM/Expeditions/LuaMExpeditionPlanner.cs",
        "Content.Server/_LuaM/Progression/LuaMCampaignShiftClock.cs",
        "Content.Server/_LuaM/Progression/LuaMCareerProgressionRules.cs",
        "Content.Server/_LuaM/Administration/LuaMAnimalPopulationCommands.cs",
        "Content.Server/_LuaM/Animals/LuaMAnimalPopulationSystem.cs",
        "Content.Server/_LuaM/Sector/LuaMCharacterTtsSystem.cs",
        "Content.Server/_LuaM/Sector/LuaMSectorTrafficContactComponent.cs",
        "Content.Server/_LuaM/Sector/LuaMSectorTrafficSystem.cs",
        "Content.Server/Database/ServerDbBase.cs",
        "Content.Server/Database/ServerDbManager.cs",
        "Content.Server/Preferences/Managers/IServerPreferencesManager.cs",
        "Content.Server/Preferences/Managers/ServerPreferencesManager.cs",
        "Content.Server/Access/Systems/IdCardConsoleSystem.cs",
        "Content.Server/Cargo/Systems/CargoSystem.Orders.cs",
        "Content.Server/PDA/PdaSystem.cs",
        "Content.Server/_NF/Bank/ATMSystem.cs",
        "Content.Server/_NF/Bank/BankSystem.cs",
        "Content.Server/_NF/ShuttleRecords/ShuttleRecordsSystem.Console.cs",
        "Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs",
        "Content.Server/_NF/Shipyard/Systems/ShipyardSystem.cs",
        "Content.Shared/_NF/ShuttleRecords/ShuttleRecord.cs",
        "Content.Server.Database/Model.cs",
        "Content.Server.Database/Migrations/Postgres/20260713070624_PreserveCharacterProfiles.cs",
        "Content.Server.Database/Migrations/Postgres/20260713070624_PreserveCharacterProfiles.Designer.cs",
        "Content.Server.Database/Migrations/Postgres/20260713162540_DurablePdaBankTransfers.cs",
        "Content.Server.Database/Migrations/Postgres/20260713162540_DurablePdaBankTransfers.Designer.cs",
        "Content.Server.Database/Migrations/Postgres/20260720164309_LuaMShipPayloadRevision.cs",
        "Content.Server.Database/Migrations/Postgres/20260720164309_LuaMShipPayloadRevision.Designer.cs",
        "Content.Server.Database/Migrations/Postgres/PostgresServerDbContextModelSnapshot.cs",
        "Content.Server.Database/Migrations/Sqlite/20260713070603_PreserveCharacterProfiles.cs",
        "Content.Server.Database/Migrations/Sqlite/20260713070603_PreserveCharacterProfiles.Designer.cs",
        "Content.Server.Database/Migrations/Sqlite/20260713162532_DurablePdaBankTransfers.cs",
        "Content.Server.Database/Migrations/Sqlite/20260713162532_DurablePdaBankTransfers.Designer.cs",
        "Content.Server.Database/Migrations/Sqlite/20260720164259_LuaMShipPayloadRevision.cs",
        "Content.Server.Database/Migrations/Sqlite/20260720164259_LuaMShipPayloadRevision.Designer.cs",
        "Content.Server.Database/Migrations/Sqlite/SqliteServerDbContextModelSnapshot.cs",
        "Content.Server/Chat/Systems/ChatSystem.cs",
        "Content.Server/Gateway/Components/GatewayGeneratorDestinationComponent.cs",
        "Content.Server/Gateway/Systems/GatewayGeneratorSystem.cs",
        "Content.Server/Nutrition/EntitySystems/AnimalHusbandrySystem.cs",
        "Content.Server/Spawners/Components/TimedSpawnerComponent.cs",
        "Content.Server/Spawners/EntitySystems/SpawnerSystem.cs",
        "Content.Server/StationEvents/Events/VentCrittersRule.cs",
        "Content.Server/Players/PlayTimeTracking/PlayTimeTrackingSystem.cs",
        "Content.Server/Radio/EntitySystems/HeadsetSystem.cs",
        "Content.Shared/Roles/JobRequirements.cs",
        "Content.Shared/Roles/SharedRoleSystem.cs",
        "Content.Shared/PDA/PdaUpdateState.cs",
        "Content.Client/_NF/Shipyard/BUI/ShipyardConsoleBoundUserInterface.cs",
        "Content.Client/_NF/Shipyard/UI/ShipyardConsoleMenu.xaml",
        "Content.Client/_NF/Shipyard/UI/ShipyardConsoleMenu.xaml.cs",
        "Content.Shared/_NF/Shipyard/BUI/ShipyardConsoleInterfaceState.cs",
        "Content.Shared/_NF/Shipyard/Components/ShuttleDeedComponent.cs",
        "Content.Shared/_NF/Shipyard/Events/ShipyardConsoleParkMessage.cs",
        "Content.Shared/_NF/Shipyard/Events/ShipyardConsolePurchaseMessage.cs",
        "Resources/ConfigPresets/_Mono/monolithCore.toml",
        "Resources/Changelog/Parts/luam-animal-population-cap.yml",
        "Resources/Changelog/Parts/luam-sector-traffic.yml",
        "Resources/Changelog/Parts/luam-expedition-persistence-foundations.yml",
        "Resources/Changelog/Parts/luam-pda-bank-transfer-fix.yml",
        "Resources/Changelog/Parts/luam-ship-generator.yml",
        "Resources/ConfigPresets/_LuaM/deadSpaceLowPop.toml",
        "Resources/Locale/en-US/_LuaM/administration/luam-ai-director.ftl",
        "Resources/Locale/ru-RU/_LuaM/administration/luam-ai-director.ftl",
        "Resources/Locale/en-US/_NF/bank/bank-ATM-component.ftl",
        "Resources/Locale/ru-RU/_NF/bank/bank-ATM-component.ftl",
        "Resources/Locale/en-US/_NF/pda/pda-component.ftl",
        "Resources/Locale/ru-RU/_NF/pda/pda-component.ftl",
        "Resources/Locale/en-US/_NF/shipyard/shipyard-console-component.ftl",
        "Resources/Locale/ru-RU/_NF/shipyard/shipyard-console-component.ftl",
        "Resources/Locale/en-US/administration/ui/tabs/admin-tab/player-actions-window.ftl",
        "Resources/Locale/ru-RU/administration/ui/tabs/admin-tab/player-actions-window.ftl",
        "Resources/Prototypes/_LuaM/Sector/sector_stories.yml",
        "Resources/Prototypes/_LuaM/Sector/rescue_after_action.yml",
        "Resources/Prototypes/_LuaM/Entities/World/sector_traffic.yml",
        "Resources/Prototypes/_LuaM/SectorServices/services.yml",
        "Resources/Prototypes/_LuaM/Entities/Objects/Monolith/artifacts.yml",
        "Resources/Prototypes/_LuaM/Entities/Objects/Devices/cartridges.yml",
        "Resources/Prototypes/Entities/Markers/Spawners/Conditional/timed.yml",
        "Resources/Prototypes/GameRules/pests.yml",
        "Resources/Prototypes/_Mono/Entities/Markers/Spawners/shuttles.yml",
        "Resources/Prototypes/_Mono/GameRules/timings.yml",
        "Resources/Prototypes/_Mono/Shipyard/triage.yml",
        "Resources/Prototypes/_Mono/game_presets.yml",
        "Resources/Locale/en-US/_Mono/gamerules/gamemodes.ftl",
        "Resources/Locale/ru-RU/_Mono/gamerules/gamemodes.ftl",
        "Resources/Maps/_Mono/Shuttles/triage.yml",
        "Resources/Maps/_NF/Shuttles/Scrap/bison.yml",
        "Resources/Prototypes/_NF/Entities/Mobs/NPCs/mob_hostile_rogue_ai.yml",
        "server_config.remote.toml",
        "Resources/Prototypes/InventoryTemplates/human_inventory_template.yml",
        "Resources/ServerInfo/_LuaM/Guidebook/DeadSpace/DeadSpace.xml",
        "Resources/ServerInfo/Intro.txt",
        "Resources/Textures/_LuaM/Objects/Monolith/sector_route_beacon.rsi/icon.png",
        "Content.Client/_NF/BountyContracts/UI/BountyContractUiFragmentListEntry.xaml.cs",
        "Content.Server/_NF/BountyContracts/BountyContractSystem.cs",
        "Content.Shared/_NF/BountyContracts/SharedBountyContractSystem.cs",
        "Content.Packaging/ClientPackaging.cs",
        "Content.Packaging/ServerPackaging.cs",
        "Content.Packaging/ReleaseSurfacePolicy.cs",
        ".github/workflows/publish.yml",
        ".github/workflows/publish-testing.yml",
        ".github/workflows/test-packaging.yml",
        "Tools/audit_release_surface.ps1",
        "Tools/audit_luam_dependency_vulnerabilities.ps1",
        "Tools/archive_monolith_admin_logs.ps1",
        "Tools/deploy_luam_ai_gateway.ps1",
        "Tools/deploy_luam_server_release.ps1",
        "Tools/monolith-restart-when-empty.ps1",
        "Tools/monolith_release_runbook.md",
        "Tools/monolith_improvement_audit.md",
        "Tools/provision_monolith_client_static.ps1",
        "Tools/luam_ai_gateway.py",
        "Tools/luam_ship_generator.py",
        "Tools/summarize_luam_ai_audit.py",
        "Tools/luam_release_policy.json",
        "Tools/luam_character_progression_design.md",
        "Tools/luam_expedition_implementation_proposal.md",
        "Tools/luam_expedition_worldgen_design.md",
        "Tools/setup_luam_piper_tts.ps1",
        "Tools/test_luam_ai_gateway.py",
        "Tools/test_luam_ship_generator.py",
        "Tools/validate_luam_feature_pack.py",
        "Tools/local_stack.md"
    )
    $missingLuaMArtifacts = @($requiredLuaMArtifacts | Where-Object { $_ -notin $listedFiles })
    if ($missingLuaMArtifacts.Count -gt 0) {
        $issues.Add("Package is missing LuaM resource/code artifact(s): $($missingLuaMArtifacts -join ', ')") | Out-Null
        Add-Step "luam-resource-artifacts" "failed" "$($missingLuaMArtifacts.Count) missing artifact(s)."
    } else {
        Add-Step "luam-resource-artifacts" "passed" "Key LuaM resources and linked code artifacts are packaged."
    }

    $missingPolicyRequiredFiles = @($policyRequiredFiles | Where-Object { $_ -notin $listedFiles })
    $packagedPolicySha256 = Get-ZipEntrySha256 -Entry $entryByPath["Tools/luam_release_policy.json"]
    if (-not ([string]$manifest.policySha256).Equals($packagedPolicySha256, [System.StringComparison]::OrdinalIgnoreCase)) {
        $issues.Add("Manifest policy SHA256 does not match the packaged release policy.") | Out-Null
    }
    if ($missingPolicyRequiredFiles.Count -gt 0) {
        $issues.Add("Package is missing policy-required release file(s): $($missingPolicyRequiredFiles -join ', ')") | Out-Null
        Add-Step "policy-required-files" "failed" "$($missingPolicyRequiredFiles.Count) policy-required file(s) are missing."
    } else {
        Add-Step "policy-required-files" "passed" "$($policyRequiredFiles.Count) policy-required file(s) are packaged; policy SHA256 $packagedPolicySha256."
    }

    if ($manifest.readiness -eq $null) {
        $issues.Add("Manifest does not contain readiness evidence.") | Out-Null
        Add-Step "readiness-evidence" "failed" "Missing readiness object."
    } elseif ($manifest.readiness.ok -ne $true) {
        $issues.Add("Manifest readiness evidence is not OK.") | Out-Null
        Add-Step "readiness-evidence" "failed" "readiness.ok is not true."
    } else {
        $detail = "readiness ok"
        $readinessEvidenceOk = $true
        $readinessSteps = @($manifest.readiness.steps)
        if ($manifest.productionEligible -ne $true -or $manifest.readiness.productionEligible -ne $true -or
            $manifest.readiness.allowUntracked -ne $false -or $manifest.readiness.worktree.trackedForProduction -ne $true -or
            [int64]$manifest.readiness.worktree.untrackedFileCount -ne 0 -or
            $manifest.sourceReceipt.trackedForProduction -ne $true -or [int64]$manifest.sourceReceipt.untrackedFileCount -ne 0 -or
            [int64]$manifest.git.untrackedReleaseFiles -ne 0) {
            $issues.Add("Production verification rejects AllowUntracked and every receipt containing untracked release files.") | Out-Null
            $readinessEvidenceOk = $false
        }
        if (-not ([string]$manifest.sourceReceipt.digestSha256).Equals([string]$manifest.readiness.worktree.digestSha256, [System.StringComparison]::OrdinalIgnoreCase) -or
            -not ([string]$manifest.sourceReceipt.gitHead).Equals([string]$manifest.readiness.worktree.gitHead, [System.StringComparison]::OrdinalIgnoreCase) -or
            -not ([string]$manifest.sourceReceipt.gitHead).Equals([string]$manifest.git.head, [System.StringComparison]::OrdinalIgnoreCase) -or
            -not ([string]$manifest.sourceReceipt.gitBranch).Equals([string]$manifest.readiness.worktree.gitBranch, [System.StringComparison]::Ordinal) -or
            -not ([string]$manifest.sourceReceipt.gitBranch).Equals([string]$manifest.git.branch, [System.StringComparison]::Ordinal) -or
            [int64]$manifest.sourceReceipt.changedFileCount -ne [int64]$manifest.readiness.worktree.changedFileCount -or
            [int64]$manifest.sourceReceipt.untrackedFileCount -ne [int64]$manifest.readiness.worktree.untrackedFileCount) {
            $issues.Add("Readiness, source worktree receipt, and Git metadata are not cryptographically bound to the same source state.") | Out-Null
            $readinessEvidenceOk = $false
        }
        $adminRankStep = @($manifest.readiness.steps) | Where-Object { $_.name -eq "admin-rank-ladder" } | Select-Object -First 1
        if ($null -eq $adminRankStep -or $adminRankStep.status -ne "passed") {
            $issues.Add("Manifest readiness evidence does not include passed admin-rank-ladder step.") | Out-Null
            $readinessEvidenceOk = $false
        } else {
            $detail += ", admin rank ladder recorded"
        }

        $releaseContractStep = $readinessSteps | Where-Object { $_.name -eq "release-contract" } | Select-Object -First 1
        if ($null -eq $releaseContractStep -or $releaseContractStep.status -ne "passed") {
            $issues.Add("Manifest readiness evidence does not include a passed release-contract step.") | Out-Null
            $readinessEvidenceOk = $false
        } else {
            $detail += ", static release contract recorded"
        }

        if ($null -eq $manifest.readiness.releaseGate) {
            $issues.Add("Manifest readiness evidence does not include releaseGate metadata.") | Out-Null
            $readinessEvidenceOk = $false
        } else {
            if ([int] $manifest.readiness.releaseGate.schemaVersion -ne [int] $releasePolicy.releaseGate.schemaVersion) {
                $issues.Add("Manifest readiness releaseGate schemaVersion does not match the packaged policy.") | Out-Null
                $readinessEvidenceOk = $false
            }
            if (-not ([string] $manifest.readiness.releaseGate.policySha256).Equals($packagedPolicySha256, [System.StringComparison]::OrdinalIgnoreCase)) {
                $issues.Add("Manifest readiness policy SHA256 does not match the packaged release policy.") | Out-Null
                $readinessEvidenceOk = $false
            }
            if ([int] $manifest.readiness.releaseGate.requiredFileCount -ne $policyRequiredFiles.Count) {
                $issues.Add("Manifest readiness policy-required file count does not match the packaged release policy.") | Out-Null
                $readinessEvidenceOk = $false
            }
            $expectedTestNames = @($releasePolicy.releaseGate.productionTests | ForEach-Object { [string]$_.name })
            $expectedSmokeNames = @($releasePolicy.releaseGate.smokeChecks | ForEach-Object { [string]$_.name })
            $recordedTestNames = @($manifest.readiness.releaseGate.productionTests | ForEach-Object { [string]$_ })
            $recordedSmokeNames = @($manifest.readiness.releaseGate.smokeChecks | ForEach-Object { [string]$_ })
            if (($expectedTestNames -join "`n") -cne ($recordedTestNames -join "`n") -or
                ($expectedSmokeNames -join "`n") -cne ($recordedSmokeNames -join "`n")) {
                $issues.Add("Manifest readiness release-gate step names do not exactly match the packaged policy.") | Out-Null
                $readinessEvidenceOk = $false
            }
        }

        $requiredProductionTests = @($releasePolicy.releaseGate.productionTests | Where-Object { $_.requiredForProduction -eq $true })
        if ($requiredProductionTests.Count -gt 0 -and $manifest.readiness.runTests -ne $true) {
            $issues.Add("Package readiness did not record RunTests=true for required production tests.") | Out-Null
            $readinessEvidenceOk = $false
        }
        foreach ($test in $requiredProductionTests) {
            $testName = [string] $test.name
            $testStep = $readinessSteps | Where-Object { $_.name -eq $testName } | Select-Object -First 1
            if ($null -eq $testStep -or $testStep.status -ne "passed") {
                $issues.Add("Manifest readiness evidence does not include passed production test '$testName'.") | Out-Null
                $readinessEvidenceOk = $false
            }
        }

        $requiredSmokeChecks = @($releasePolicy.releaseGate.smokeChecks | Where-Object { $_.requiredForProduction -eq $true })
        $requiresLocalSmoke = @($requiredSmokeChecks | Where-Object { $_.mode -eq "local" }).Count -gt 0
        if ($requiresLocalSmoke -and $manifest.readiness.runLocalSmoke -ne $true) {
            $issues.Add("Package readiness did not record RunLocalSmoke=true for required production smoke checks.") | Out-Null
            $readinessEvidenceOk = $false
        }
        foreach ($smoke in $requiredSmokeChecks) {
            $smokeName = [string] $smoke.name
            $smokeStep = $readinessSteps | Where-Object { $_.name -eq $smokeName } | Select-Object -First 1
            if ($null -eq $smokeStep -or $smokeStep.status -ne "passed") {
                $issues.Add("Manifest readiness evidence does not include passed smoke check '$smokeName'.") | Out-Null
                $readinessEvidenceOk = $false
            }
        }

        if ($manifest.readiness.runTests -eq $true) {
            $detail += ", policy production tests recorded"
        }
        if ($manifest.readiness.runLocalSmoke -eq $true) {
            $detail += ", policy local smoke recorded"
        }

        if ($readinessEvidenceOk) {
            Add-Step "readiness-evidence" "passed" $detail
        } else {
            Add-Step "readiness-evidence" "failed" "Missing required readiness step."
        }
    }

    if ($manifest.scopeAudit -eq $null) {
        $issues.Add("Manifest does not contain scopeAudit evidence.") | Out-Null
        Add-Step "scope-audit" "failed" "Missing scopeAudit object."
    } else {
        $unexpectedOutsidePackage = @(
            $manifest.scopeAudit.unexpectedChangedFilesOutsidePackage |
                Where-Object { -not [string]::IsNullOrWhiteSpace([string] $_) } |
                ForEach-Object { Normalize-ZipPath ([string] $_) }
        )
        $allowedOutsidePackage = @(
            $manifest.scopeAudit.allowedChangedFilesOutsidePackage |
                Where-Object { -not [string]::IsNullOrWhiteSpace([string] $_) } |
                ForEach-Object { Normalize-ZipPath ([string] $_) }
        )
        $unapprovedAllowedOutsidePackage = @(
            $allowedOutsidePackage |
                Where-Object { $_ -notin $policyApprovedOutsidePackageFiles }
        )

        $scopeCountsConsistent =
            [int64]$manifest.scopeAudit.changedFileCount -eq
                ([int64]$manifest.scopeAudit.packagedChangedFileCount + [int64]$manifest.scopeAudit.changedFilesOutsidePackageCount) -and
            [int64]$manifest.scopeAudit.changedFilesOutsidePackageCount -eq
                ($allowedOutsidePackage.Count + $unexpectedOutsidePackage.Count)

        if (-not $scopeCountsConsistent) {
            $issues.Add("Scope audit count relationships are internally inconsistent.") | Out-Null
        }

        if (-not $scopeCountsConsistent -or $unexpectedOutsidePackage.Count -gt 0 -or $unapprovedAllowedOutsidePackage.Count -gt 0) {
            if ($unexpectedOutsidePackage.Count -gt 0) {
                $issues.Add("Changed files outside package are not allowlisted: $($unexpectedOutsidePackage -join ', ')") | Out-Null
            }
            if ($unapprovedAllowedOutsidePackage.Count -gt 0) {
                $issues.Add("Scope audit allowlist contains paths not approved by the packaged policy: $($unapprovedAllowedOutsidePackage -join ', ')") | Out-Null
            }

            Add-Step "scope-audit" "failed" "countsConsistent=$scopeCountsConsistent; $($unexpectedOutsidePackage.Count) unexpected and $($unapprovedAllowedOutsidePackage.Count) policy-unapproved outside-package path(s)."
        } else {
            $detail = "No unexpected changed files outside package."
            if ($allowedOutsidePackage.Count -gt 0) {
                $warnings.Add("Package scope audit has allowlisted changed files outside package: $($allowedOutsidePackage -join ', ')") | Out-Null
                $detail += " $($allowedOutsidePackage.Count) allowlisted outside-package file(s) recorded."
            }

            Add-Step "scope-audit" "passed" $detail
        }
    }

    $result = [pscustomobject]@{
        ok = $issues.Count -eq 0
        package = $resolvedPackage
        sha256 = $actualPackageSha256
        policySha256 = $packagedPolicySha256
        payloadDigestSha256 = $actualPayloadDigestSha256
        worktreeDigestSha256 = [string]$manifest.sourceReceipt.digestSha256
        gitHead = [string]$manifest.sourceReceipt.gitHead
        productionEligible = $issues.Count -eq 0
        fileCount = $listedFiles.Count
        issues = @($issues.ToArray())
        warnings = @($warnings.ToArray())
        steps = @($steps.ToArray())
    }
}
finally {
    $archive.Dispose()
}

if ($Json) {
    $result | ConvertTo-Json -Depth 6
} else {
    $status = if ($result.ok) { "OK" } else { "FAILED" }
    Write-Host "LuaM package verification: $status"
    Write-Host "Package: $($result.package)"
    Write-Host "SHA256:  $($result.sha256)"
    foreach ($step in $steps) {
        Write-Host ("[{0}] {1}: {2}" -f $step.status, $step.name, $step.detail)
    }

    foreach ($warning in $warnings) {
        Write-Warning $warning
    }

    foreach ($issue in $issues) {
        Write-Error $issue -ErrorAction Continue
    }
}

if (-not $result.ok) {
    exit 1
}
