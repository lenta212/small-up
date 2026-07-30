Set-StrictMode -Version 3.0

function Test-LuaMJsonObject {
    param([object]$Value)

    return $null -ne $Value -and
        $Value -isnot [System.Array] -and
        $Value -isnot [string] -and
        $Value -isnot [ValueType]
}

function Assert-LuaMJsonObject {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Value,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if (-not (Test-LuaMJsonObject -Value $Value)) {
        throw "$Name must be a JSON object."
    }
}

function Assert-LuaMJsonProperties {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Value,
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string[]]$Required,
        [Parameter(Mandatory = $true)]
        [string[]]$Allowed
    )

    Assert-LuaMJsonObject -Value $Value -Name $Name
    $properties = @($Value.PSObject.Properties.Name)
    $missing = @($Required | Where-Object { $_ -notin $properties })
    $unexpected = @($properties | Where-Object { $_ -notin $Allowed })
    if ($missing.Count -gt 0 -or $unexpected.Count -gt 0) {
        throw "$Name has invalid properties. Missing: $($missing -join ', '); unexpected: $($unexpected -join ', ')."
    }
}

function Assert-LuaMNonEmptyString {
    param(
        [object]$Value,
        [string]$Name,
        [int]$MaxLength = 4096,
        [string]$Pattern = ""
    )

    if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace($Value) -or
        $Value -ne $Value.Trim() -or $Value.Length -gt $MaxLength -or
        $Value -match '[\x00-\x1F]') {
        throw "$Name must be a trimmed, non-empty string of at most $MaxLength characters."
    }

    if (-not [string]::IsNullOrWhiteSpace($Pattern) -and $Value -notmatch $Pattern) {
        throw "$Name has an invalid format: $Value"
    }
}

function ConvertTo-LuaMReleaseRepoPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -ne $Path.Trim() -or
        [System.IO.Path]::IsPathRooted($Path) -or $Path -match '[\x00-\x1F]') {
        throw "Release policy contains an invalid repository path: $Path"
    }

    $normalized = $Path.Replace('\', '/')
    if ($normalized -cne $normalized.Normalize([Text.NormalizationForm]::FormC)) {
        throw "Release policy contains a non-canonical Unicode repository path: $Path"
    }
    if ($normalized.StartsWith('/') -or $normalized -match '^[A-Za-z]:' -or
        $normalized.IndexOfAny([char[]]@('*', '?', '[', ']')) -ge 0) {
        throw "Release policy contains an absolute or wildcard repository path: $Path"
    }

    $parts = $normalized.Split([char]'/', [System.StringSplitOptions]::None)
    foreach ($part in $parts) {
        if ($part -eq '' -or $part -eq '.' -or $part -eq '..' -or $part.Contains(':') -or
            $part -ne $part.Trim() -or $part.EndsWith('.')) {
            throw "Release policy contains an unsafe repository path segment: $Path"
        }

        $deviceBase = [System.IO.Path]::GetFileNameWithoutExtension($part)
        if ($deviceBase -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
            throw "Release policy contains a reserved Windows path segment: $Path"
        }
    }

    return $parts -join '/'
}

function Assert-LuaMStringArray {
    param(
        [object]$Value,
        [string]$Name,
        [switch]$AllowEmpty,
        [switch]$RepositoryPaths,
        [int]$MaxItemLength = 4096
    )

    if ($Value -isnot [System.Array]) {
        throw "$Name must be a JSON array."
    }

    $items = @($Value)
    if (-not $AllowEmpty -and $items.Count -eq 0) {
        throw "$Name must not be empty."
    }

    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($candidate in $items) {
        if ($candidate -isnot [string]) {
            throw "$Name must contain only strings."
        }

        if ($RepositoryPaths) {
            $normalized = ConvertTo-LuaMReleaseRepoPath -Path $candidate
            if ($candidate -cne $normalized) {
                throw "$Name repository paths must use canonical forward slashes: $candidate"
            }
        }
        else {
            Assert-LuaMNonEmptyString -Value $candidate -Name "$Name item" -MaxLength $MaxItemLength
            $normalized = $candidate
        }

        if (-not $seen.Add($normalized)) {
            throw "$Name contains a duplicate or case-colliding item: $candidate"
        }
    }
}

function ConvertTo-LuaMUtcTimestamp {
    param(
        [object]$Value,
        [string]$Name
    )

    if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace($Value)) {
        throw "$Name must be an ISO-8601 UTC timestamp."
    }

    $parsed = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
        $Value,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind,
        [ref]$parsed) -or $parsed.Offset -ne [TimeSpan]::Zero -or -not $Value.EndsWith('Z')) {
        throw "$Name must be an ISO-8601 timestamp with an explicit Z UTC suffix: $Value"
    }

    return $parsed
}

function Assert-LuaMReleasePolicy {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Policy
    )

    $rootProperties = @(
        'remoteDeployFrozen',
        'reason',
        'lastReviewed',
        'deploymentAuthorization',
        'releaseGate',
        'approvedReleaseAdditions',
        'pendingLocalIntegrationBatch',
        'approvedExistingWorkOutsideLuaMPackage',
        'requiredChecksBeforeDeploy'
    )
    Assert-LuaMJsonProperties -Value $Policy -Name 'LuaM release policy' -Required $rootProperties -Allowed $rootProperties

    if ($Policy.remoteDeployFrozen -isnot [bool]) {
        throw 'LuaM release policy must declare remoteDeployFrozen as a JSON boolean.'
    }
    Assert-LuaMNonEmptyString -Value $Policy.reason -Name 'reason' -MaxLength 4096
    if ($Policy.lastReviewed -isnot [string] -or $Policy.lastReviewed -notmatch '^\d{4}-\d{2}-\d{2}$') {
        throw 'lastReviewed must use YYYY-MM-DD format.'
    }
    $reviewDate = [DateTime]::MinValue
    if (-not [DateTime]::TryParseExact(
        $Policy.lastReviewed,
        'yyyy-MM-dd',
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::None,
        [ref]$reviewDate)) {
        throw 'lastReviewed is not a valid calendar date.'
    }

    $authorizationProperties = @('state', 'approvalId', 'approvedBy', 'approvedAtUtc', 'expiresAtUtc', 'allowedMutations')
    Assert-LuaMJsonProperties -Value $Policy.deploymentAuthorization -Name 'deploymentAuthorization' -Required $authorizationProperties -Allowed $authorizationProperties
    $authorization = $Policy.deploymentAuthorization
    if ($authorization.state -isnot [string] -or $authorization.state -notin @('frozen', 'authorized')) {
        throw "deploymentAuthorization.state must be frozen or authorized."
    }
    Assert-LuaMStringArray -Value $authorization.allowedMutations -Name 'deploymentAuthorization.allowedMutations' -AllowEmpty
    $knownMutations = @('server-release', 'client-static', 'ai-gateway')
    $unknownMutations = @($authorization.allowedMutations | Where-Object { $_ -notin $knownMutations })
    if ($unknownMutations.Count -gt 0) {
        throw "deploymentAuthorization.allowedMutations contains unsupported values: $($unknownMutations -join ', ')"
    }

    if ($authorization.state -eq 'frozen') {
        if (-not $Policy.remoteDeployFrozen) {
            throw 'A frozen deploymentAuthorization requires remoteDeployFrozen=true.'
        }
        if (@($authorization.allowedMutations).Count -ne 0 -or
            $null -ne $authorization.approvalId -or $null -ne $authorization.approvedBy -or
            $null -ne $authorization.approvedAtUtc -or $null -ne $authorization.expiresAtUtc) {
            throw 'Frozen deploymentAuthorization metadata must be null and allowedMutations must be empty.'
        }
    }
    else {
        if ($Policy.remoteDeployFrozen) {
            throw 'An authorized deploymentAuthorization requires remoteDeployFrozen=false.'
        }
        Assert-LuaMNonEmptyString -Value $authorization.approvalId -Name 'deploymentAuthorization.approvalId' -MaxLength 128 -Pattern '^[A-Za-z0-9][A-Za-z0-9_.:-]*$'
        Assert-LuaMNonEmptyString -Value $authorization.approvedBy -Name 'deploymentAuthorization.approvedBy' -MaxLength 256
        if (@($authorization.allowedMutations).Count -eq 0) {
            throw 'Authorized deploymentAuthorization must name at least one allowed mutation.'
        }
        $approvedAt = ConvertTo-LuaMUtcTimestamp -Value $authorization.approvedAtUtc -Name 'deploymentAuthorization.approvedAtUtc'
        $expiresAt = ConvertTo-LuaMUtcTimestamp -Value $authorization.expiresAtUtc -Name 'deploymentAuthorization.expiresAtUtc'
        if ($expiresAt -le $approvedAt) {
            throw 'deploymentAuthorization.expiresAtUtc must be later than approvedAtUtc.'
        }
        $now = [DateTimeOffset]::UtcNow
        if ($approvedAt -gt $now.AddMinutes(5) -or $approvedAt -lt $now.AddHours(-24) -or
            $expiresAt -le $now -or $expiresAt -gt $approvedAt.AddHours(24)) {
            throw 'Authorized deployment metadata must be current and expire within 24 hours of approval.'
        }
    }

    $gateProperties = @('schemaVersion', 'requireTrackedWorktreeForProduction', 'requiredFiles', 'productionTests', 'smokeChecks')
    Assert-LuaMJsonProperties -Value $Policy.releaseGate -Name 'releaseGate' -Required $gateProperties -Allowed $gateProperties
    if (($Policy.releaseGate.schemaVersion -isnot [int] -and $Policy.releaseGate.schemaVersion -isnot [long]) -or
        [int64]$Policy.releaseGate.schemaVersion -ne 2) {
        throw "Unsupported LuaM releaseGate schemaVersion: $($Policy.releaseGate.schemaVersion)"
    }
    if ($Policy.releaseGate.requireTrackedWorktreeForProduction -isnot [bool] -or
        -not $Policy.releaseGate.requireTrackedWorktreeForProduction) {
        throw 'releaseGate.requireTrackedWorktreeForProduction must be true.'
    }
    Assert-LuaMStringArray -Value $Policy.releaseGate.requiredFiles -Name 'releaseGate.requiredFiles' -RepositoryPaths

    if ($Policy.releaseGate.productionTests -isnot [System.Array] -or @($Policy.releaseGate.productionTests).Count -eq 0) {
        throw 'releaseGate.productionTests must be a non-empty JSON array.'
    }
    if ($Policy.releaseGate.smokeChecks -isnot [System.Array] -or @($Policy.releaseGate.smokeChecks).Count -eq 0) {
        throw 'releaseGate.smokeChecks must be a non-empty JSON array.'
    }

    $stepNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $testProperties = @('name', 'runner', 'project', 'filter', 'arguments', 'requiredForProduction', 'requiredFiles')
    foreach ($test in @($Policy.releaseGate.productionTests)) {
        Assert-LuaMJsonProperties -Value $test -Name 'production test' -Required $testProperties -Allowed $testProperties
        Assert-LuaMNonEmptyString -Value $test.name -Name 'production test name' -MaxLength 128 -Pattern '^[a-z0-9][a-z0-9-]*$'
        if (-not $stepNames.Add($test.name)) {
            throw "Duplicate release gate step name: $($test.name)"
        }
        if ($test.runner -isnot [string] -or $test.runner -cne 'dotnet') {
            throw "Production test '$($test.name)' must use the dotnet runner."
        }
        $projectValue = [string]$test.project
        $project = ConvertTo-LuaMReleaseRepoPath -Path $projectValue
        if ($projectValue -cne $project) {
            throw "Production test '$($test.name)' project must use a canonical forward-slash repository path."
        }
        if (-not $project.EndsWith('.csproj', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Production test '$($test.name)' must reference a .csproj file."
        }
        Assert-LuaMNonEmptyString -Value $test.filter -Name "production test '$($test.name)' filter" -MaxLength 2048
        Assert-LuaMStringArray -Value $test.arguments -Name "production test '$($test.name)' arguments" -AllowEmpty -MaxItemLength 512
        $testArguments = @($test.arguments | ForEach-Object { [string]$_ })
        $configurationIndex = [Array]::IndexOf($testArguments, '--configuration')
        if ($configurationIndex -lt 0 -or $configurationIndex -ge ($testArguments.Count - 1) -or
            $testArguments[$configurationIndex + 1] -cne 'DebugOpt' -or '--no-restore' -notin $testArguments) {
            throw "Production test '$($test.name)' must run DebugOpt with --no-restore."
        }
        if ($test.requiredForProduction -isnot [bool] -or -not $test.requiredForProduction) {
            throw "Production test '$($test.name)' must set requiredForProduction=true."
        }
        Assert-LuaMStringArray -Value $test.requiredFiles -Name "production test '$($test.name)' requiredFiles" -RepositoryPaths
    }

    $smokeProperties = @('name', 'runner', 'script', 'arguments', 'mode', 'requiredForProduction')
    foreach ($smoke in @($Policy.releaseGate.smokeChecks)) {
        Assert-LuaMJsonProperties -Value $smoke -Name 'smoke check' -Required $smokeProperties -Allowed $smokeProperties
        Assert-LuaMNonEmptyString -Value $smoke.name -Name 'smoke check name' -MaxLength 128 -Pattern '^[a-z0-9][a-z0-9-]*$'
        if (-not $stepNames.Add($smoke.name)) {
            throw "Duplicate release gate step name: $($smoke.name)"
        }
        if ($smoke.runner -isnot [string] -or $smoke.runner -notin @('python', 'powershell')) {
            throw "Smoke check '$($smoke.name)' has an unsupported runner."
        }
        $smokeScriptValue = [string]$smoke.script
        $smokeScript = ConvertTo-LuaMReleaseRepoPath -Path $smokeScriptValue
        if ($smokeScriptValue -cne $smokeScript) {
            throw "Smoke check '$($smoke.name)' script must use a canonical forward-slash repository path."
        }
        $expectedSmokeExtension = if ($smoke.runner -eq 'python') { '.py' } else { '.ps1' }
        if (-not $smokeScript.EndsWith($expectedSmokeExtension, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Smoke check '$($smoke.name)' runner requires a $expectedSmokeExtension script."
        }
        Assert-LuaMStringArray -Value $smoke.arguments -Name "smoke check '$($smoke.name)' arguments" -AllowEmpty -MaxItemLength 512
        if ($smoke.mode -isnot [string] -or $smoke.mode -notin @('always', 'local')) {
            throw "Smoke check '$($smoke.name)' has an unsupported mode."
        }
        if ($smoke.requiredForProduction -isnot [bool] -or -not $smoke.requiredForProduction) {
            throw "Smoke check '$($smoke.name)' must set requiredForProduction=true."
        }
    }

    Assert-LuaMStringArray -Value $Policy.approvedReleaseAdditions -Name 'approvedReleaseAdditions' -RepositoryPaths

    $batchProperties = @('name', 'state', 'packageSelection', 'packageFiles', 'packageScopes', 'runtimeDependenciesIncluded', 'excludedLocalArtifacts')
    Assert-LuaMJsonProperties -Value $Policy.pendingLocalIntegrationBatch -Name 'pendingLocalIntegrationBatch' -Required $batchProperties -Allowed $batchProperties
    Assert-LuaMNonEmptyString -Value $Policy.pendingLocalIntegrationBatch.name -Name 'pendingLocalIntegrationBatch.name' -MaxLength 256 -Pattern '^[A-Za-z0-9][A-Za-z0-9_.-]*$'
    if ($Policy.pendingLocalIntegrationBatch.state -isnot [string] -or
        $Policy.pendingLocalIntegrationBatch.state -notin @('local-package-only', 'release-candidate', 'deployment-authorized')) {
        throw 'pendingLocalIntegrationBatch.state is invalid.'
    }
    if ($authorization.state -eq 'authorized' -and $Policy.pendingLocalIntegrationBatch.state -ne 'deployment-authorized') {
        throw 'Authorized remote deployment requires pendingLocalIntegrationBatch.state=deployment-authorized.'
    }
    if ($authorization.state -eq 'frozen' -and $Policy.pendingLocalIntegrationBatch.state -eq 'deployment-authorized') {
        throw 'deployment-authorized batch state requires an authorized deploymentAuthorization.'
    }
    Assert-LuaMNonEmptyString -Value $Policy.pendingLocalIntegrationBatch.packageSelection -Name 'pendingLocalIntegrationBatch.packageSelection' -MaxLength 1024
    Assert-LuaMStringArray -Value $Policy.pendingLocalIntegrationBatch.packageFiles -Name 'pendingLocalIntegrationBatch.packageFiles' -RepositoryPaths
    Assert-LuaMStringArray -Value $Policy.pendingLocalIntegrationBatch.packageScopes -Name 'pendingLocalIntegrationBatch.packageScopes' -RepositoryPaths
    Assert-LuaMStringArray -Value $Policy.pendingLocalIntegrationBatch.runtimeDependenciesIncluded -Name 'pendingLocalIntegrationBatch.runtimeDependenciesIncluded' -AllowEmpty -RepositoryPaths
    Assert-LuaMStringArray -Value $Policy.pendingLocalIntegrationBatch.excludedLocalArtifacts -Name 'pendingLocalIntegrationBatch.excludedLocalArtifacts' -RepositoryPaths

    $pathOwners = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    $ownedPathGroups = [ordered]@{
        'releaseGate.requiredFiles' = @($Policy.releaseGate.requiredFiles)
        'approvedReleaseAdditions' = @($Policy.approvedReleaseAdditions)
        'pendingLocalIntegrationBatch.runtimeDependenciesIncluded' = @($Policy.pendingLocalIntegrationBatch.runtimeDependenciesIncluded)
        'releaseGate.productionTests.requiredFiles' = @($Policy.releaseGate.productionTests | ForEach-Object { @($_.requiredFiles) })
        'releaseGate.productionTests.project' = @($Policy.releaseGate.productionTests | ForEach-Object { $_.project })
        'releaseGate.smokeChecks.script' = @($Policy.releaseGate.smokeChecks | ForEach-Object { $_.script })
    }
    foreach ($groupName in $ownedPathGroups.Keys) {
        foreach ($candidate in @($ownedPathGroups[$groupName])) {
            $ownedPath = ConvertTo-LuaMReleaseRepoPath -Path ([string]$candidate)
            if ($pathOwners.ContainsKey($ownedPath)) {
                throw "Release policy path '$ownedPath' is owned by both $($pathOwners[$ownedPath]) and $groupName."
            }
            $pathOwners.Add($ownedPath, $groupName)
        }
    }

    $outsideProperties = @('reason', 'files')
    Assert-LuaMJsonProperties -Value $Policy.approvedExistingWorkOutsideLuaMPackage -Name 'approvedExistingWorkOutsideLuaMPackage' -Required $outsideProperties -Allowed $outsideProperties
    Assert-LuaMNonEmptyString -Value $Policy.approvedExistingWorkOutsideLuaMPackage.reason -Name 'approvedExistingWorkOutsideLuaMPackage.reason' -MaxLength 4096
    Assert-LuaMStringArray -Value $Policy.approvedExistingWorkOutsideLuaMPackage.files -Name 'approvedExistingWorkOutsideLuaMPackage.files' -AllowEmpty -RepositoryPaths
    Assert-LuaMStringArray -Value $Policy.requiredChecksBeforeDeploy -Name 'requiredChecksBeforeDeploy'
    $requiredCommands = @($Policy.requiredChecksBeforeDeploy | ForEach-Object { [string]$_ })
    $commandSpecs = @(
        [pscustomobject]@{ Tool = 'build_luam_release_package.ps1'; Command = 'powershell -NoProfile -ExecutionPolicy Bypass -File Tools\build_luam_release_package.ps1 -RunTests -RunLocalSmoke -Json' },
        [pscustomobject]@{ Tool = 'verify_luam_release_package.ps1'; Command = 'powershell -NoProfile -ExecutionPolicy Bypass -File Tools\verify_luam_release_package.ps1 -PackagePath <source-package.zip> -ExpectedSha256 <source-sha256> -Json' },
        [pscustomobject]@{ Tool = 'build_luam_server_release.ps1'; Command = 'powershell -NoProfile -ExecutionPolicy Bypass -File Tools\build_luam_server_release.ps1 -ExternalClientBaseUrl http://188.127.225.57:1213 -SourcePackagePath <source-package.zip> -ExpectedSourcePackageSha256 <source-sha256> -ReleaseReceiptPath release\luam-binary-release-receipt.json -Json' },
        [pscustomobject]@{ Tool = 'audit_release_surface.ps1'; Command = 'powershell -NoProfile -ExecutionPolicy Bypass -File Tools\audit_release_surface.ps1 -PackagePath release\SS14.Client.zip,release\SS14.Server_linux-x64.zip -Json' },
        [pscustomobject]@{ Tool = 'provision_monolith_client_static.ps1'; Command = 'powershell -NoProfile -ExecutionPolicy Bypass -File Tools\provision_monolith_client_static.ps1 -ClientPackagePath release\SS14.Client.zip -ExpectedSha256 <client-sha256> -Version <version> -ReleaseReceiptPath release\luam-binary-release-receipt.json -ExpectedReleaseReceiptSha256 <receipt-sha256> -DryRun' },
        [pscustomobject]@{ Tool = 'deploy_luam_server_release.ps1'; Command = 'powershell -NoProfile -ExecutionPolicy Bypass -File Tools\deploy_luam_server_release.ps1 -PackagePath release\SS14.Server_linux-x64.zip -ExpectedSha256 <server-sha256> -ReleaseReceiptPath release\luam-binary-release-receipt.json -ExpectedReleaseReceiptSha256 <receipt-sha256> -DryRun' },
        [pscustomobject]@{ Tool = 'deploy_luam_ai_gateway.ps1'; Command = 'powershell -NoProfile -ExecutionPolicy Bypass -File Tools\deploy_luam_ai_gateway.ps1 -SourcePath Tools\luam_ai_gateway.py -ExpectedSha256 <gateway-sha256> -DryRun' }
    )
    if ($requiredCommands.Count -ne $commandSpecs.Count) {
        throw "requiredChecksBeforeDeploy must contain exactly $($commandSpecs.Count) canonical release checks."
    }
    foreach ($spec in $commandSpecs) {
        $matches = @($requiredCommands | Where-Object { $_.IndexOf($spec.Tool, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
        if ($matches.Count -ne 1) {
            throw "requiredChecksBeforeDeploy must contain exactly one $($spec.Tool) command."
        }
        if ($matches[0] -cne $spec.Command) {
            throw "requiredChecksBeforeDeploy command for $($spec.Tool) is not canonical."
        }
    }
}

function Read-LuaMReleasePolicy {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $policyPath = Join-Path $Root 'Tools/luam_release_policy.json'
    if (-not (Test-Path -LiteralPath $policyPath -PathType Leaf)) {
        throw "LuaM release policy not found: $policyPath"
    }
    $policyItem = Get-Item -LiteralPath $policyPath -Force
    if (($policyItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'LuaM release policy cannot be a reparse point.'
    }
    if ($policyItem.Length -gt 4MB) {
        throw "LuaM release policy exceeds the 4 MiB control-file limit."
    }

    try {
        $policy = Get-Content -LiteralPath $policyPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        throw "LuaM release policy is not valid JSON: $($_.Exception.Message)"
    }

    Assert-LuaMReleasePolicy -Policy $policy
    return $policy
}

function Get-LuaMReleaseGateRequiredFiles {
    param([Parameter(Mandatory = $true)][object]$Policy)

    Assert-LuaMReleasePolicy -Policy $Policy
    $files = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($group in @(
        @($Policy.approvedReleaseAdditions),
        @($Policy.pendingLocalIntegrationBatch.packageFiles),
        @($Policy.pendingLocalIntegrationBatch.runtimeDependenciesIncluded),
        @($Policy.releaseGate.requiredFiles))) {
        foreach ($candidate in @($group)) {
            $files.Add((ConvertTo-LuaMReleaseRepoPath -Path ([string]$candidate))) | Out-Null
        }
    }
    foreach ($test in @($Policy.releaseGate.productionTests)) {
        $files.Add((ConvertTo-LuaMReleaseRepoPath -Path ([string]$test.project))) | Out-Null
        foreach ($candidate in @($test.requiredFiles)) {
            $files.Add((ConvertTo-LuaMReleaseRepoPath -Path ([string]$candidate))) | Out-Null
        }
    }
    foreach ($smoke in @($Policy.releaseGate.smokeChecks)) {
        $files.Add((ConvertTo-LuaMReleaseRepoPath -Path ([string]$smoke.script))) | Out-Null
    }
    return @($files) | Sort-Object
}

function Get-LuaMReleaseGatePackageScopes {
    param([Parameter(Mandatory = $true)][object]$Policy)

    Assert-LuaMReleasePolicy -Policy $Policy
    $scopes = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($candidate in @($Policy.pendingLocalIntegrationBatch.packageScopes)) {
        $scopes.Add((ConvertTo-LuaMReleaseRepoPath -Path ([string]$candidate))) | Out-Null
    }
    foreach ($requiredFile in Get-LuaMReleaseGateRequiredFiles -Policy $Policy) {
        $scopes.Add($requiredFile) | Out-Null
    }
    return @($scopes) | Sort-Object
}

function Get-LuaMReleaseExcludedLocalArtifacts {
    param([Parameter(Mandatory = $true)][object]$Policy)

    Assert-LuaMReleasePolicy -Policy $Policy
    $artifacts = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($candidate in @($Policy.pendingLocalIntegrationBatch.excludedLocalArtifacts)) {
        $normalized = (ConvertTo-LuaMReleaseRepoPath -Path ([string]$candidate)).TrimEnd('/')
        if ($normalized -notmatch '(^|/)(bin|obj|\.vs|\.idea|\.vscode|node_modules|__pycache__|\.pytest_cache|logs?|tmp|temp|test_results)(/|$)') {
            throw "Release policy may exclude only recognized local-artifact paths: $candidate"
        }
        $artifacts.Add($normalized) | Out-Null
    }
    return @($artifacts) | Sort-Object
}

function Test-LuaMReleaseExcludedLocalArtifact {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string[]]$ExcludedArtifacts
    )

    $normalized = (ConvertTo-LuaMReleaseRepoPath -Path $Path).TrimEnd('/')
    foreach ($artifact in $ExcludedArtifacts) {
        if ($normalized.Equals($artifact, [StringComparison]::OrdinalIgnoreCase) -or
            $normalized.StartsWith("$artifact/", [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return $false
}

function Get-LuaMReleaseApprovedOutsidePackageFiles {
    param([Parameter(Mandatory = $true)][object]$Policy)

    Assert-LuaMReleasePolicy -Policy $Policy
    return @($Policy.approvedExistingWorkOutsideLuaMPackage.files | ForEach-Object {
        ConvertTo-LuaMReleaseRepoPath -Path ([string]$_)
    }) | Sort-Object
}

function Get-LuaMSha256ForText {
    param([Parameter(Mandatory = $true)][string]$Text)

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Text)
        return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-LuaMFileRecordDigest {
    param([Parameter(Mandatory = $true)][object[]]$Files)

    $lines = [System.Collections.Generic.List[string]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $ordered = [System.Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($file in @($Files)) {
        $path = ConvertTo-LuaMReleaseRepoPath -Path ([string]$file.path)
        if (-not $seen.Add($path) -or [string]$file.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
            (($file.bytes -isnot [int]) -and ($file.bytes -isnot [long])) -or [int64]$file.bytes -lt 0) {
            throw "Invalid or duplicate payload digest record: $path"
        }
        $ordered.Add($path, $file)
    }
    foreach ($path in $ordered.Keys) {
        $file = $ordered[$path]
        $lines.Add("$path`t$(([string]$file.sha256).ToLowerInvariant())`t$([int64]$file.bytes)") | Out-Null
    }
    return Get-LuaMSha256ForText -Text (($lines -join "`n") + "`n")
}

function Invoke-LuaMGitCapture {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    Push-Location $Root
    $oldPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = @(& git @Arguments)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $oldPreference
        Pop-Location
    }
    if ($exitCode -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $exitCode."
    }
    return @($output | ForEach-Object { [string]$_ })
}

function Get-LuaMWorktreeReceipt {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string[]]$ExcludedArtifacts
    )

    $head = @((Invoke-LuaMGitCapture -Root $Root -Arguments @('rev-parse', 'HEAD')))[0].Trim()
    if ($head -notmatch '^[0-9a-fA-F]{40,64}$') {
        throw "Unable to resolve a valid Git HEAD for release receipt: $head"
    }
    $branch = @((Invoke-LuaMGitCapture -Root $Root -Arguments @('rev-parse', '--abbrev-ref', 'HEAD')))[0].Trim()

    $changed = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $untracked = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($arguments in @(
        @('-c', 'core.quotepath=false', 'diff', '--name-only', '--no-ext-diff'),
        @('-c', 'core.quotepath=false', 'diff', '--cached', '--name-only', '--no-ext-diff'))) {
        foreach ($candidate in Invoke-LuaMGitCapture -Root $Root -Arguments $arguments) {
            if (-not [string]::IsNullOrWhiteSpace($candidate)) {
                $changed.Add((ConvertTo-LuaMReleaseRepoPath -Path $candidate)) | Out-Null
            }
        }
    }
    foreach ($candidate in Invoke-LuaMGitCapture -Root $Root -Arguments @('-c', 'core.quotepath=false', 'ls-files', '--others', '--exclude-standard')) {
        if (-not [string]::IsNullOrWhiteSpace($candidate)) {
            $normalized = ConvertTo-LuaMReleaseRepoPath -Path $candidate
            $changed.Add($normalized) | Out-Null
            $untracked.Add($normalized) | Out-Null
        }
    }

    $records = [System.Collections.Generic.List[object]]::new()
    $orderedChanged = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    foreach ($candidate in $changed) { $orderedChanged.Add($candidate) | Out-Null }
    foreach ($path in $orderedChanged) {
        if (Test-LuaMReleaseExcludedLocalArtifact -Path $path -ExcludedArtifacts $ExcludedArtifacts) {
            continue
        }
        $fullPath = Join-Path $Root ($path.Replace('/', [IO.Path]::DirectorySeparatorChar))
        if (-not (Test-Path -LiteralPath $fullPath)) {
            $records.Add([pscustomobject]@{ path = $path; kind = 'deleted'; sha256 = ('0' * 64); bytes = [int64]0 }) | Out-Null
            continue
        }
        if (Test-Path -LiteralPath $fullPath -PathType Container) {
            $nestedStatus = @(Invoke-LuaMGitCapture -Root $fullPath -Arguments @('status', '--porcelain=v1', '--untracked-files=all'))
            if ($nestedStatus.Count -gt 0) {
                throw "Dirty nested repository cannot be represented by the release receipt: $path"
            }
            $nestedHead = @((Invoke-LuaMGitCapture -Root $fullPath -Arguments @('rev-parse', 'HEAD')))[0].Trim().ToLowerInvariant()
            $records.Add([pscustomobject]@{ path = $path; kind = 'gitlink'; sha256 = (Get-LuaMSha256ForText -Text $nestedHead); bytes = [int64]0 }) | Out-Null
            continue
        }

        $item = Get-Item -LiteralPath $fullPath -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Release receipt refuses a reparse-point file: $path"
        }
        $hash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $records.Add([pscustomobject]@{ path = $path; kind = 'file'; sha256 = $hash; bytes = [int64]$item.Length }) | Out-Null
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("HEAD`t$($head.ToLowerInvariant())") | Out-Null
    foreach ($record in $records) {
        $lines.Add("$($record.path)`t$($record.kind)`t$($record.sha256)`t$($record.bytes)") | Out-Null
    }
    $digest = Get-LuaMSha256ForText -Text (($lines -join "`n") + "`n")
    $includedUntracked = @($untracked | Where-Object {
        -not (Test-LuaMReleaseExcludedLocalArtifact -Path $_ -ExcludedArtifacts $ExcludedArtifacts)
    })
    return [pscustomobject]@{
        schemaVersion = 1
        gitHead = $head.ToLowerInvariant()
        gitBranch = $branch
        digestSha256 = $digest
        changedFileCount = $records.Count
        untrackedFileCount = $includedUntracked.Count
        trackedForProduction = $includedUntracked.Count -eq 0
    }
}

function Assert-LuaMBinaryReleaseReceipt {
    param([Parameter(Mandatory = $true)][object]$Receipt)

    $properties = @('schemaVersion', 'generatedAtUtc', 'policySha256', 'sourcePackage', 'buildWorktree', 'client', 'server', 'delivery', 'surfaceAudit')
    Assert-LuaMJsonProperties -Value $Receipt -Name 'binary release receipt' -Required $properties -Allowed $properties
    if (($Receipt.schemaVersion -isnot [int] -and $Receipt.schemaVersion -isnot [long]) -or [int64]$Receipt.schemaVersion -ne 1) {
        throw 'Unsupported binary release receipt schemaVersion.'
    }
    $receiptGeneratedAt = ConvertTo-LuaMUtcTimestamp -Value $Receipt.generatedAtUtc -Name 'binary receipt generatedAtUtc'
    $receiptNow = [DateTimeOffset]::UtcNow
    if ($receiptGeneratedAt -gt $receiptNow.AddMinutes(5) -or $receiptGeneratedAt -lt $receiptNow.AddHours(-24)) {
        throw 'Binary release receipt must have been generated within the last 24 hours.'
    }
    if ($Receipt.policySha256 -isnot [string] -or $Receipt.policySha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw 'Binary receipt policySha256 is invalid.'
    }

    $sourceProperties = @('fileName', 'sha256', 'payloadDigestSha256', 'worktreeDigestSha256', 'gitHead')
    Assert-LuaMJsonProperties -Value $Receipt.sourcePackage -Name 'binary receipt sourcePackage' -Required $sourceProperties -Allowed $sourceProperties
    $sourceFileName = ConvertTo-LuaMReleaseRepoPath -Path ([string]$Receipt.sourcePackage.fileName)
    if ($sourceFileName.Contains('/') -or -not $sourceFileName.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Binary receipt sourcePackage.fileName must be a ZIP leaf name.'
    }
    $worktreeProperties = @('digestSha256', 'gitHead', 'untrackedFileCount')
    Assert-LuaMJsonProperties -Value $Receipt.buildWorktree -Name 'binary receipt buildWorktree' -Required $worktreeProperties -Allowed $worktreeProperties
    foreach ($value in @($Receipt.sourcePackage.sha256, $Receipt.sourcePackage.payloadDigestSha256, $Receipt.sourcePackage.worktreeDigestSha256, $Receipt.buildWorktree.digestSha256)) {
        if ($value -isnot [string] -or $value -notmatch '^[0-9a-fA-F]{64}$') {
            throw 'Binary receipt contains an invalid SHA256 binding.'
        }
    }
    foreach ($value in @($Receipt.sourcePackage.gitHead, $Receipt.buildWorktree.gitHead)) {
        if ($value -isnot [string] -or $value -notmatch '^[0-9a-fA-F]{40,64}$') {
            throw 'Binary receipt contains an invalid Git HEAD.'
        }
    }
    if (($Receipt.buildWorktree.untrackedFileCount -isnot [int] -and $Receipt.buildWorktree.untrackedFileCount -isnot [long]) -or
        [int64]$Receipt.buildWorktree.untrackedFileCount -ne 0) {
        throw 'Production binary receipt must record zero untracked files.'
    }

    $artifactProperties = @('fileName', 'sha256', 'bytes')
    foreach ($artifactName in @('client', 'server')) {
        $artifact = $Receipt.$artifactName
        Assert-LuaMJsonProperties -Value $artifact -Name "binary receipt $artifactName" -Required $artifactProperties -Allowed $artifactProperties
        $safeName = ConvertTo-LuaMReleaseRepoPath -Path ([string]$artifact.fileName)
        if ($safeName.Contains('/') -or -not $safeName.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Binary receipt $artifactName.fileName must be a ZIP leaf name."
        }
        if ($artifact.sha256 -isnot [string] -or $artifact.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
            (($artifact.bytes -isnot [int]) -and ($artifact.bytes -isnot [long])) -or [int64]$artifact.bytes -le 0) {
            throw "Binary receipt $artifactName artifact metadata is invalid."
        }
    }

    $deliveryProperties = @('mode', 'clientDownloadUrl')
    Assert-LuaMJsonProperties -Value $Receipt.delivery -Name 'binary receipt delivery' -Required $deliveryProperties -Allowed $deliveryProperties
    if ($Receipt.delivery.mode -isnot [string] -or $Receipt.delivery.mode -notin @('hybrid-acz', 'external-zip', 'external-manifest')) {
        throw 'Binary receipt delivery.mode is invalid.'
    }
    if ($Receipt.delivery.mode -eq 'external-zip') {
        Assert-LuaMNonEmptyString -Value $Receipt.delivery.clientDownloadUrl -Name 'binary receipt clientDownloadUrl' -MaxLength 2048
        $clientUri = $null
        if (-not [Uri]::TryCreate([string]$Receipt.delivery.clientDownloadUrl, [UriKind]::Absolute, [ref]$clientUri) -or
            $clientUri.Scheme -notin @('http', 'https') -or [string]::IsNullOrWhiteSpace($clientUri.Host) -or
            -not [string]::IsNullOrWhiteSpace($clientUri.UserInfo) -or
            -not [string]::IsNullOrWhiteSpace($clientUri.Query) -or
            -not [string]::IsNullOrWhiteSpace($clientUri.Fragment)) {
            throw 'Binary receipt clientDownloadUrl must be an absolute HTTP(S) URL without credentials, query, or fragment.'
        }
    }
    elseif ($null -ne $Receipt.delivery.clientDownloadUrl -and $Receipt.delivery.clientDownloadUrl -ne '') {
        throw 'Binary receipt clientDownloadUrl must be null or empty outside external-zip mode.'
    }

    $surfaceProperties = @('passed', 'serverOnlyCanaryPresent', 'clientServerOnlyAbsent', 'violationCount')
    Assert-LuaMJsonProperties -Value $Receipt.surfaceAudit -Name 'binary receipt surfaceAudit' -Required $surfaceProperties -Allowed $surfaceProperties
    if ($Receipt.surfaceAudit.passed -isnot [bool] -or -not $Receipt.surfaceAudit.passed -or
        $Receipt.surfaceAudit.serverOnlyCanaryPresent -isnot [bool] -or -not $Receipt.surfaceAudit.serverOnlyCanaryPresent -or
        $Receipt.surfaceAudit.clientServerOnlyAbsent -isnot [bool] -or -not $Receipt.surfaceAudit.clientServerOnlyAbsent -or
        (($Receipt.surfaceAudit.violationCount -isnot [int]) -and ($Receipt.surfaceAudit.violationCount -isnot [long])) -or
        [int64]$Receipt.surfaceAudit.violationCount -ne 0) {
        throw 'Binary receipt requires a zero-violation surface audit and both canary directions.'
    }

    if (-not ([string]$Receipt.sourcePackage.worktreeDigestSha256).Equals([string]$Receipt.buildWorktree.digestSha256, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([string]$Receipt.sourcePackage.gitHead).Equals([string]$Receipt.buildWorktree.gitHead, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Binary receipt source-package and build-worktree bindings differ.'
    }
}

function Read-LuaMBinaryReleaseReceipt {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Binary release receipt not found: $Path"
    }
    $receiptItem = Get-Item -LiteralPath $Path -Force
    if (($receiptItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Binary release receipt cannot be a reparse point.'
    }
    if ($receiptItem.Length -gt 1MB) {
        throw 'Binary release receipt exceeds the 1 MiB control-file limit.'
    }
    if ($ExpectedSha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw 'Expected receipt SHA256 must contain exactly 64 hexadecimal characters.'
    }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $actual.Equals($ExpectedSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Binary release receipt SHA256 mismatch. Expected $ExpectedSha256, got $actual"
    }
    try {
        $receipt = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        throw "Binary release receipt is not valid JSON: $($_.Exception.Message)"
    }
    Assert-LuaMBinaryReleaseReceipt -Receipt $receipt
    return $receipt
}

function Assert-LuaMRemoteMutationAllowed {
    param(
        [Parameter(Mandatory = $true)][object]$Policy,
        [Parameter(Mandatory = $true)]
        [ValidateSet('server-release', 'client-static', 'ai-gateway')]
        [string]$Mutation
    )

    Assert-LuaMReleasePolicy -Policy $Policy
    if ($Policy.remoteDeployFrozen -or $Policy.deploymentAuthorization.state -ne 'authorized') {
        throw [string]$Policy.reason
    }
    $expiresAt = ConvertTo-LuaMUtcTimestamp -Value $Policy.deploymentAuthorization.expiresAtUtc -Name 'deploymentAuthorization.expiresAtUtc'
    if ($expiresAt -le [DateTimeOffset]::UtcNow) {
        throw "Remote deployment authorization '$($Policy.deploymentAuthorization.approvalId)' expired at $($expiresAt.ToString('o'))."
    }
    if ($Mutation -notin @($Policy.deploymentAuthorization.allowedMutations)) {
        throw "Remote mutation '$Mutation' is not covered by deployment authorization '$($Policy.deploymentAuthorization.approvalId)'."
    }
}
