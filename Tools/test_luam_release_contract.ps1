param(
    [switch]$Json
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "luam_release_contract.ps1")

function Assert-Contract {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

$policy = Read-LuaMReleasePolicy -Root $root
Assert-LuaMReleasePolicy -Policy $policy
$gate = $policy.releaseGate

Assert-Contract ($policy.remoteDeployFrozen -is [bool]) "remoteDeployFrozen must be a JSON boolean."
if ($policy.remoteDeployFrozen) {
    Assert-Contract ($policy.deploymentAuthorization.state -eq 'frozen') "Frozen policy must use a frozen deployment authorization."
    Assert-Contract ($policy.pendingLocalIntegrationBatch.state -ne 'deployment-authorized') "Frozen policy cannot label its batch deployment-authorized."
} else {
    Assert-Contract ($policy.deploymentAuthorization.state -eq 'authorized') "Unfrozen policy must carry an explicit deployment authorization."
    Assert-Contract ($policy.pendingLocalIntegrationBatch.state -eq 'deployment-authorized') "Unfrozen policy must label its batch deployment-authorized."
}
Assert-Contract ([int]$gate.schemaVersion -eq 2) "The release gate must use receipt-aware schemaVersion 2."
Assert-Contract ($gate.requireTrackedWorktreeForProduction -eq $true) "Production release policy must require a tracked worktree."

# Prove the same strict schema can be intentionally authorized without editing this test.
$authorizedPolicy = ($policy | ConvertTo-Json -Depth 20) | ConvertFrom-Json
$authorizedPolicy.remoteDeployFrozen = $false
$authorizedPolicy.reason = 'Contract-test authorization only.'
$authorizedPolicy.pendingLocalIntegrationBatch.state = 'deployment-authorized'
$authorizedPolicy.deploymentAuthorization.state = 'authorized'
$authorizedPolicy.deploymentAuthorization.approvalId = 'contract-test-approval'
$authorizedPolicy.deploymentAuthorization.approvedBy = 'release-contract-test'
$authorizedPolicy.deploymentAuthorization.approvedAtUtc = [DateTime]::UtcNow.AddMinutes(-1).ToString('o')
$authorizedPolicy.deploymentAuthorization.expiresAtUtc = [DateTime]::UtcNow.AddHours(1).ToString('o')
$authorizedPolicy.deploymentAuthorization.allowedMutations = @('server-release', 'client-static', 'ai-gateway')
Assert-LuaMReleasePolicy -Policy $authorizedPolicy
foreach ($mutation in @('server-release', 'client-static', 'ai-gateway')) {
    Assert-LuaMRemoteMutationAllowed -Policy $authorizedPolicy -Mutation $mutation
}
if ($policy.remoteDeployFrozen) {
    $frozenGuardRejected = $false
    try {
        Assert-LuaMRemoteMutationAllowed -Policy $policy -Mutation 'server-release'
    }
    catch {
        $frozenGuardRejected = $true
    }
    Assert-Contract $frozenGuardRejected "The shared mutation guard accepted the frozen policy."
}

$dummyBinaryReceipt = [pscustomobject]@{
    schemaVersion = 1
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    policySha256 = ('a' * 64)
    sourcePackage = [pscustomobject]@{
        fileName = 'source.zip'; sha256 = ('b' * 64); payloadDigestSha256 = ('c' * 64)
        worktreeDigestSha256 = ('d' * 64); gitHead = ('e' * 40)
    }
    buildWorktree = [pscustomobject]@{ digestSha256 = ('d' * 64); gitHead = ('e' * 40); untrackedFileCount = 0 }
    client = [pscustomobject]@{ fileName = 'SS14.Client.zip'; sha256 = ('f' * 64); bytes = 1 }
    server = [pscustomobject]@{ fileName = 'SS14.Server_linux-x64.zip'; sha256 = ('1' * 64); bytes = 1 }
    delivery = [pscustomobject]@{ mode = 'external-zip'; clientDownloadUrl = 'https://release.invalid/v/SS14.Client.zip' }
    surfaceAudit = [pscustomobject]@{ passed = $true; serverOnlyCanaryPresent = $true; clientServerOnlyAbsent = $true; violationCount = 0 }
}
Assert-LuaMBinaryReleaseReceipt -Receipt $dummyBinaryReceipt

$minimalPolicyRejected = $false
try {
    Assert-LuaMReleasePolicy -Policy ([pscustomobject]@{ remoteDeployFrozen = $true })
}
catch {
    $minimalPolicyRejected = $true
}
Assert-Contract $minimalPolicyRejected "Strict policy validation accepted an incomplete policy object."

$tamperedCommandPolicy = ($policy | ConvertTo-Json -Depth 20) | ConvertFrom-Json
$tamperedCommandPolicy.requiredChecksBeforeDeploy[0] += ' -InjectedArgument'
$tamperedCommandRejected = $false
try {
    Assert-LuaMReleasePolicy -Policy $tamperedCommandPolicy
}
catch {
    $tamperedCommandRejected = $true
}
Assert-Contract $tamperedCommandRejected "Strict policy validation accepted a modified operator command."

$featureValidatorPath = Join-Path $root "Tools/validate_luam_feature_pack.py"
$featureValidatorText = Get-Content -LiteralPath $featureValidatorPath -Raw -Encoding UTF8
foreach ($validatorMarker in @(
    'validate_bank_system_contract',
    'run_bank_system_contract_self_test',
    'UpdateCharacterBankBalanceAsync',
    'identity.ProfileId',
    'BANK_SYSTEM_FORBIDDEN_MARKERS')) {
    Assert-Contract ($featureValidatorText.Contains($validatorMarker)) "Feature validator is missing BankSystem contract marker: $validatorMarker"
}
$bankSystemText = Get-Content -LiteralPath (Join-Path $root "Content.Server/_NF/Bank/BankSystem.cs") -Raw -Encoding UTF8
Assert-Contract (-not $bankSystemText.Contains('SaveCharacterSlotAsync')) "BankSystem regressed to the stale whole-profile save API."
$validatorSelfTestOutput = @(& python $featureValidatorPath '--self-test' 2>&1)
Assert-Contract ($LASTEXITCODE -eq 0) "BankSystem validator self-test failed: $($validatorSelfTestOutput -join '; ')"

$productionTests = @($gate.productionTests)
$smokeChecks = @($gate.smokeChecks)
Assert-Contract ($productionTests.Count -gt 0) "releaseGate.productionTests must not be empty."
Assert-Contract ($smokeChecks.Count -gt 0) "releaseGate.smokeChecks must not be empty."

$testNames = @($productionTests | ForEach-Object { [string] $_.name })
$smokeNames = @($smokeChecks | ForEach-Object { [string] $_.name })
Assert-Contract (@($testNames | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -eq 0) "Every production test needs a name."
Assert-Contract (@($smokeNames | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -eq 0) "Every smoke check needs a name."
Assert-Contract (@($testNames | Sort-Object -Unique).Count -eq $testNames.Count) "Production test names must be unique."
Assert-Contract (@($smokeNames | Sort-Object -Unique).Count -eq $smokeNames.Count) "Smoke check names must be unique."
Assert-Contract (@(@($testNames + $smokeNames) | Sort-Object -Unique).Count -eq ($testNames.Count + $smokeNames.Count)) "Release gate step names must be unique across tests and smoke checks."
foreach ($expectedTestName in @("content-tests-luam", "integration-tests-luam")) {
    Assert-Contract ($expectedTestName -in $testNames) "Required production test suite is missing: $expectedTestName"
}
foreach ($expectedSmokeName in @("gateway-test", "local-stack-smoke")) {
    Assert-Contract ($expectedSmokeName -in $smokeNames) "Required smoke check is missing: $expectedSmokeName"
}

$declaredTestFiles = @(
    $productionTests |
        ForEach-Object { @($_.requiredFiles) } |
        ForEach-Object { ConvertTo-LuaMReleaseRepoPath -Path ([string] $_) }
)
Assert-Contract (@($declaredTestFiles | Sort-Object -Unique).Count -eq $declaredTestFiles.Count) "Critical production test files must be declared exactly once."
$criticalTestFiles = @($declaredTestFiles)
foreach ($criticalTestFile in $criticalTestFiles) {
    Assert-Contract ($criticalTestFile -notin @($policy.approvedReleaseAdditions)) "Critical production tests belong in releaseGate.productionTests, not approvedReleaseAdditions: $criticalTestFile"
}

foreach ($test in $productionTests) {
    Assert-Contract ([string] $test.runner -eq "dotnet") "Production test '$($test.name)' must use the dotnet runner."
    Assert-Contract ($test.requiredForProduction -eq $true) "Production test '$($test.name)' must require production evidence."
    Assert-Contract (-not [string]::IsNullOrWhiteSpace([string] $test.project)) "Production test '$($test.name)' is missing its project."
    Assert-Contract (-not [string]::IsNullOrWhiteSpace([string] $test.filter)) "Production test '$($test.name)' is missing its filter."
    Assert-Contract (@($test.requiredFiles).Count -gt 0) "Production test '$($test.name)' must name the critical test files it covers."

    $filterTerms = @(
        [regex]::Matches([string] $test.filter, 'FullyQualifiedName~([^|&()]+)') |
            ForEach-Object { $_.Groups[1].Value.Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    Assert-Contract ($filterTerms.Count -gt 0) "Production test '$($test.name)' filter must contain a FullyQualifiedName term."

    foreach ($testFile in @($test.requiredFiles)) {
        $className = [System.IO.Path]::GetFileNameWithoutExtension([string] $testFile)
        $covered = @($filterTerms | Where-Object { $className.IndexOf($_, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 }).Count -gt 0
        Assert-Contract $covered "Production filter '$($test.filter)' does not cover $className."
    }
}

foreach ($smoke in $smokeChecks) {
    Assert-Contract ($smoke.requiredForProduction -eq $true) "Smoke check '$($smoke.name)' must require production evidence."
    Assert-Contract ([string] $smoke.runner -in @("python", "powershell")) "Smoke check '$($smoke.name)' has an unsupported runner."
    Assert-Contract ([string] $smoke.mode -in @("always", "local")) "Smoke check '$($smoke.name)' has an unsupported mode."
    Assert-Contract (-not [string]::IsNullOrWhiteSpace([string] $smoke.script)) "Smoke check '$($smoke.name)' is missing its script."
}

$requiredFiles = @(Get-LuaMReleaseGateRequiredFiles -Policy $policy)
foreach ($requiredFile in $requiredFiles) {
    $fullPath = Join-Path $root ($requiredFile.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
    Assert-Contract (Test-Path -LiteralPath $fullPath -PathType Leaf) "Policy-required release file does not exist: $requiredFile"
}

$declaredPackageScopes = @($policy.pendingLocalIntegrationBatch.packageScopes)
Assert-Contract ($declaredPackageScopes.Count -gt 0) "pendingLocalIntegrationBatch.packageScopes must not be empty."
$packageScopes = @(Get-LuaMReleaseGatePackageScopes -Policy $policy)
foreach ($packageScope in $packageScopes) {
    $fullPath = Join-Path $root ($packageScope.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
    Assert-Contract (Test-Path -LiteralPath $fullPath) "Policy package scope does not exist: $packageScope"
}

$excludedLocalArtifacts = @(Get-LuaMReleaseExcludedLocalArtifacts -Policy $policy)
Assert-Contract (@($excludedLocalArtifacts | Sort-Object -Unique).Count -eq $excludedLocalArtifacts.Count) "Excluded local artifacts must be unique."
Assert-Contract (Test-LuaMReleaseExcludedLocalArtifact -Path "test_results/example.trx" -ExcludedArtifacts $excludedLocalArtifacts) "test_results must remain an explicit policy-owned local exclusion."

$approvedOutsidePackage = @(Get-LuaMReleaseApprovedOutsidePackageFiles -Policy $policy)
Assert-Contract ($approvedOutsidePackage.Count -eq 0) "The current integration batch must not allow changed files outside the source package."

$gateScripts = @(
    "Tools/build_luam_release_package.ps1",
    "Tools/check_luam_release_ready.ps1",
    "Tools/verify_luam_release_package.ps1"
)
foreach ($gateScript in $gateScripts) {
    $scriptText = Get-Content -LiteralPath (Join-Path $root $gateScript) -Raw -Encoding UTF8
    Assert-Contract ($scriptText.Contains("Get-LuaMReleaseGateRequiredFiles")) "$gateScript does not consume policy-required files."
    if ($gateScript -ne "Tools/verify_luam_release_package.ps1") {
        Assert-Contract ($scriptText.Contains("Get-LuaMReleaseGatePackageScopes")) "$gateScript does not consume policy package scopes."
    }
    foreach ($criticalTestFile in $criticalTestFiles) {
        $className = [System.IO.Path]::GetFileNameWithoutExtension($criticalTestFile)
        Assert-Contract (-not $scriptText.Contains($className)) "$gateScript hardcodes critical test '$className'; keep it only in luam_release_policy.json."
    }
}

$readinessText = Get-Content -LiteralPath (Join-Path $root "Tools/check_luam_release_ready.ps1") -Raw -Encoding UTF8
Assert-Contract ($readinessText.Contains('releaseGate.productionTests')) "Readiness does not execute policy production tests."
Assert-Contract ($readinessText.Contains('releaseGate.smokeChecks')) "Readiness does not execute policy smoke checks."
Assert-Contract ($readinessText.Contains('$requiredEvidenceRequested')) "Readiness productionEligible does not require requested production tests/local smoke evidence."

$builderText = Get-Content -LiteralPath (Join-Path $root "Tools/build_luam_release_package.ps1") -Raw -Encoding UTF8
Assert-Contract ($builderText.Contains('Get-LuaMReleaseApprovedOutsidePackageFiles')) "Package builder does not consume the policy-owned outside-package allowlist."
Assert-Contract ($builderText.Contains('Get-LuaMReleaseExcludedLocalArtifacts')) "Package builder does not consume policy-owned local-artifact exclusions."
Assert-Contract ($builderText.Contains('Get-LuaMWorktreeReceipt')) "Package builder does not bind readiness to a stable worktree receipt."
Assert-Contract ($builderText.Contains('payloadDigestSha256')) "Package builder does not bind its complete payload record set."
Assert-Contract (-not $builderText.Contains('sourceRoot = $root')) "Package manifest leaks an absolute sourceRoot."

$clientPackagingText = Get-Content -LiteralPath (Join-Path $root "Content.Packaging/ClientPackaging.cs") -Raw -Encoding UTF8
Assert-Contract ($clientPackagingText.Contains('"ServerOnly"')) "Client packaging does not explicitly exclude Resources/ServerOnly."
Assert-Contract ($clientPackagingText.Contains('ClientOnlyIgnoredResources,')) "Client packaging does not pass its server-only exclusion to RobustClientPackaging."
Assert-Contract ($clientPackagingText.Contains('ValidateServerOnlyDirectoryCasing')) "Client packaging does not fail closed on ServerOnly directory casing collisions."

$surfaceAuditText = Get-Content -LiteralPath (Join-Path $root "Tools/audit_release_surface.ps1") -Raw -Encoding UTF8
Assert-Contract ($surfaceAuditText.Contains('Test-ForbiddenClientEntry')) "Release surface audit has no client-specific privacy gate."
Assert-Contract ($surfaceAuditText.Contains('server-only canary in client package')) "Release surface audit does not reject the server-only canary from client packages."
Assert-Contract ($surfaceAuditText.Contains('OrdinalIgnoreCase')) "Release surface audit does not reject case-colliding archive paths."
Assert-Contract ($surfaceAuditText.Contains('release set must contain exactly one client package')) "Release surface audit does not enforce client/server pair presence."
foreach ($limitToken in @('100000', '2GB', '512MB', '16MB')) {
    Assert-Contract ($surfaceAuditText.Contains($limitToken)) "Release surface audit is missing conservative archive limit $limitToken."
}
Assert-Contract ($surfaceAuditText.Contains('Unicode NFC canonical')) "Release surface audit does not enforce normalized canonical ZIP paths."
Assert-Contract ($surfaceAuditText.Contains('directory/file collision')) "Release surface audit does not reject file/directory prefix collisions."

$verifierText = Get-Content -LiteralPath (Join-Path $root "Tools/verify_luam_release_package.ps1") -Raw -Encoding UTF8
Assert-Contract ($verifierText.Contains('[string]$ExpectedSha256')) "Verifier does not require an externally supplied package SHA256."
Assert-Contract ($verifierText.Contains('Get-SafeZipEntryIndex')) "Verifier does not validate canonical ZIP paths and archive limits."
Assert-Contract ($verifierText.Contains('manifestMissingFromList')) "Verifier does not compare manifest and PACKAGE_FILES path sets."
Assert-Contract ($verifierText.Contains('Assert-LuaMReleasePolicy')) "Verifier does not validate the complete packaged policy contract."
Assert-Contract ($verifierText.Contains('Get-LuaMReleaseExcludedLocalArtifacts')) "Verifier does not reject policy-owned local artifacts."
Assert-Contract ($verifierText.Contains('Get-LuaMReleaseApprovedOutsidePackageFiles')) "Verifier does not enforce the packaged policy outside-package allowlist."
Assert-Contract ($verifierText.Contains('Read-ZipText -Archive $archive -EntryName "Tools/luam_release_policy.json"')) "Verifier does not read the packaged release policy."
Assert-Contract ($verifierText.Contains("requiredForProduction")) "Verifier does not enforce policy-required production evidence."
Assert-Contract ($verifierText.Contains("policySha256")) "Verifier does not bind readiness evidence to the packaged policy hash."
Assert-Contract ($verifierText.Contains('Add-Step "policy-required-files"')) "Verifier does not report the policy-required file gate."
Assert-Contract ($verifierText.Contains('payload-digest')) "Verifier does not recompute the complete payload digest."
Assert-Contract ($verifierText.Contains('Production verification rejects AllowUntracked')) "Verifier does not reject production AllowUntracked evidence."
Assert-Contract ($verifierText.Contains('worktreeDigestSha256')) "Verifier does not expose the source worktree binding for binary builds."
foreach ($limitToken in @('100000', '2GB', '512MB', '16MB')) {
    Assert-Contract ($verifierText.Contains($limitToken)) "Source package verifier is missing conservative archive limit $limitToken."
}

$binaryBuildText = Get-Content -LiteralPath (Join-Path $root "Tools/build_luam_server_release.ps1") -Raw -Encoding UTF8
Assert-Contract ($binaryBuildText.Contains('Invoke-SourcePackageVerification')) "Binary builder does not verify its source package."
Assert-Contract ($binaryBuildText.Contains('Assert-LuaMBinaryReleaseReceipt')) "Binary builder does not emit a validated release receipt."
Assert-Contract ($binaryBuildText.Contains('-SkipAudit is local-only')) "Binary builder permits a production audit bypass."

$deployText = Get-Content -LiteralPath (Join-Path $root "Tools/deploy_luam_server_release.ps1") -Raw -Encoding UTF8
Assert-Contract ($deployText.Contains("LuaM release policy is missing; refusing remote deploy.")) "Deploy guard must fail closed when the JSON policy is missing."
Assert-Contract ($deployText.Contains("Assert-LuaMReleasePolicy")) "Deploy guard does not validate the complete JSON policy contract."
Assert-Contract (-not $deployText.Contains('source = "manifest"')) "Deploy guard must not derive freeze state from the markdown manifest."
Assert-Contract ($deployText.Contains('Read-LuaMBinaryReleaseReceipt')) "Server deploy does not require a hash-pinned binary release receipt."
Assert-Contract ($deployText.Contains("Assert-LuaMRemoteMutationAllowed -Policy `$mutationPolicy -Mutation 'server-release'")) "Server deploy does not use the shared remote mutation guard."
Assert-Contract ($deployText.Contains('serverOnlyCanaryPresent')) "Server deploy does not require fresh canary audit evidence."
Assert-Contract ($deployText.Contains('/admin/actions/maintenance/ship-save')) "Server deploy does not use the fixed ship-save maintenance endpoint."
Assert-Contract ($deployText.Contains('Authorization') -and $deployText.Contains('SS14Token')) "Ship-save barrier does not authenticate with the existing admin API scheme."
Assert-Contract ($deployText.Contains('ROBUST_CVAR_admin__api_token') -and $deployText.Contains('/proc/{pid}/environ')) "Ship-save barrier does not read the running service token only on the host."
Assert-Contract (-not $deployText.Contains('[string]$AdminApiToken')) "Server deploy must not accept the admin API token as a local command-line parameter."
Assert-Contract ($deployText.Contains('ship_save_barrier = [ordered]@{')) "Server dry-run does not record the ship-save barrier plan."
Assert-Contract ($deployText.Contains('force_cannot_bypass = $true') -and $deployText.Contains('ship_save_force_bypass = $false')) "Normal -Force MUST NOT bypass the ship-save barrier."
Assert-Contract ($deployText.Contains('[switch]$LegacyShipSaveBootstrap')) "Server deploy lacks an explicitly named first-rollout legacy bootstrap switch."
Assert-Contract ($deployText.Contains('maintenance endpoint already exists') -and $deployText.Contains('exc.code != 404')) "Legacy bootstrap does not prove that the maintenance endpoint is absent."
Assert-Contract ($deployText.Contains('Legacy ship-save bootstrap refused: players=') -and $deployText.Contains('players=`$legacy_players (zero required)')) "Legacy bootstrap does not require a fresh zero-player proof."
Assert-Contract ($deployText.Contains('select count(*) from luam_ship_snapshot where status in (1, 2)')) "Legacy bootstrap does not reject Active or Restoring ship rows."
Assert-Contract ($deployText.Contains('select count(*) from luam_ship_presence_lease')) "Legacy bootstrap does not reject ship presence leases."
Assert-Contract ($deployText.Contains('tables != expected_tables')) "Legacy bootstrap does not fail closed on a partial persistence schema."

$shipSaveValidatorStart = $deployText.IndexOf('# LUAM_SHIP_SAVE_RECEIPT_VALIDATOR_BEGIN', [StringComparison]::Ordinal)
$shipSaveValidatorEnd = $deployText.IndexOf('# LUAM_SHIP_SAVE_RECEIPT_VALIDATOR_END', [StringComparison]::Ordinal)
$serviceStopPosition = $deployText.IndexOf('echo "deploy-step=stop-service"', [StringComparison]::Ordinal)
Assert-Contract ($shipSaveValidatorStart -ge 0 -and $shipSaveValidatorEnd -gt $shipSaveValidatorStart) "Ship-save receipt validator markers are missing or out of order."
Assert-Contract ($serviceStopPosition -gt $shipSaveValidatorEnd) "Service stop occurs before the ship-save receipt is validated."

$shipPipelineText = Get-Content -LiteralPath (Join-Path $root "Tools/ship_luam_release.ps1") -Raw -Encoding UTF8
Assert-Contract ($shipPipelineText.Contains('[switch]$LegacyShipSaveBootstrap')) "Ship release orchestrator does not expose the legacy ship-save bootstrap switch."
Assert-Contract ($shipPipelineText.Contains('$serverDeployArgs += "-LegacyShipSaveBootstrap"')) "Ship release orchestrator does not pass the legacy bootstrap guard to server deploy/dry-run arguments."
Assert-Contract ($shipPipelineText.Contains('legacy_ship_save_bootstrap = [bool]$LegacyShipSaveBootstrap')) "Ship release summary does not record the legacy bootstrap mode."

# Execute the exact Python validator embedded into the remote deploy script.
$validatorMatch = [regex]::Match(
    $deployText,
    '(?ms)^# LUAM_SHIP_SAVE_RECEIPT_VALIDATOR_BEGIN\r?\n(?<code>.*?)^# LUAM_SHIP_SAVE_RECEIPT_VALIDATOR_END\s*$')
Assert-Contract $validatorMatch.Success "Unable to extract the embedded ship-save receipt validator."

$shipSaveTestRoot = Join-Path ([IO.Path]::GetTempPath()) ("luam-ship-save-contract-" + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($shipSaveTestRoot) | Out-Null
try {
    function Invoke-ExpectedShipSaveValidatorFailure {
        param([string]$ValidatorPath, [string]$ReceiptPath)

        $previousErrorAction = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            & python $ValidatorPath $ReceiptPath '300' 2>&1 | Out-Null
            return [int]$LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorAction
        }
    }

    $validatorPath = Join-Path $shipSaveTestRoot 'validate_ship_save_receipt.py'
    $utf8NoBom = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText($validatorPath, $validatorMatch.Groups['code'].Value, $utf8NoBom)

    $validReceipt = [ordered]@{
        schemaVersion = 1
        ok = $true
        barrierId = [Guid]::NewGuid().ToString()
        createdAtUtc = [DateTime]::UtcNow.ToString('o')
        attempted = 2
        saved = 2
        failed = 0
        activeRemaining = 0
        frozen = $true
        busy = $false
    }
    $validReceiptPath = Join-Path $shipSaveTestRoot 'valid.json'
    [IO.File]::WriteAllText($validReceiptPath, ($validReceipt | ConvertTo-Json -Compress), $utf8NoBom)
    $validOutput = @(& python $validatorPath $validReceiptPath '300' 2>&1)
    Assert-Contract ($LASTEXITCODE -eq 0) "Embedded ship-save validator rejected a fresh complete success receipt: $($validOutput -join '; ')"
    Assert-Contract (($validOutput -join "`n").Contains('ship_save_frozen=true')) "Embedded ship-save validator did not emit sanitized success evidence."

    $mismatchReceipt = (($validReceipt | ConvertTo-Json -Depth 4) | ConvertFrom-Json)
    $mismatchReceipt.saved = 1
    $mismatchPath = Join-Path $shipSaveTestRoot 'mismatch.json'
    [IO.File]::WriteAllText($mismatchPath, ($mismatchReceipt | ConvertTo-Json -Compress), $utf8NoBom)
    $mismatchExitCode = Invoke-ExpectedShipSaveValidatorFailure -ValidatorPath $validatorPath -ReceiptPath $mismatchPath
    Assert-Contract ($mismatchExitCode -ne 0) "Embedded ship-save validator accepted attempted != saved."

    $unsafeReceipt = (($validReceipt | ConvertTo-Json -Depth 4) | ConvertFrom-Json)
    $unsafeReceipt.activeRemaining = 1
    $unsafePath = Join-Path $shipSaveTestRoot 'active-remaining.json'
    [IO.File]::WriteAllText($unsafePath, ($unsafeReceipt | ConvertTo-Json -Compress), $utf8NoBom)
    $unsafeExitCode = Invoke-ExpectedShipSaveValidatorFailure -ValidatorPath $validatorPath -ReceiptPath $unsafePath
    Assert-Contract ($unsafeExitCode -ne 0) "Embedded ship-save validator accepted activeRemaining != 0."

    $failedReceipt = (($validReceipt | ConvertTo-Json -Depth 4) | ConvertFrom-Json)
    $failedReceipt.failed = 1
    $failedPath = Join-Path $shipSaveTestRoot 'failed.json'
    [IO.File]::WriteAllText($failedPath, ($failedReceipt | ConvertTo-Json -Compress), $utf8NoBom)
    $failedExitCode = Invoke-ExpectedShipSaveValidatorFailure -ValidatorPath $validatorPath -ReceiptPath $failedPath
    Assert-Contract ($failedExitCode -ne 0) "Embedded ship-save validator accepted failed != 0."

    $unfrozenReceipt = (($validReceipt | ConvertTo-Json -Depth 4) | ConvertFrom-Json)
    $unfrozenReceipt.frozen = $false
    $unfrozenPath = Join-Path $shipSaveTestRoot 'unfrozen.json'
    [IO.File]::WriteAllText($unfrozenPath, ($unfrozenReceipt | ConvertTo-Json -Compress), $utf8NoBom)
    $unfrozenExitCode = Invoke-ExpectedShipSaveValidatorFailure -ValidatorPath $validatorPath -ReceiptPath $unfrozenPath
    Assert-Contract ($unfrozenExitCode -ne 0) "Embedded ship-save validator accepted frozen=false."

    $staleReceipt = (($validReceipt | ConvertTo-Json -Depth 4) | ConvertFrom-Json)
    $staleReceipt.createdAtUtc = [DateTime]::UtcNow.AddHours(-1).ToString('o')
    $stalePath = Join-Path $shipSaveTestRoot 'stale.json'
    [IO.File]::WriteAllText($stalePath, ($staleReceipt | ConvertTo-Json -Compress), $utf8NoBom)
    $staleExitCode = Invoke-ExpectedShipSaveValidatorFailure -ValidatorPath $validatorPath -ReceiptPath $stalePath
    Assert-Contract ($staleExitCode -ne 0) "Embedded ship-save validator accepted a stale receipt."
}
finally {
    if (Test-Path -LiteralPath $shipSaveTestRoot) {
        Remove-Item -LiteralPath $shipSaveTestRoot -Recurse -Force
    }
}

$provisionText = Get-Content -LiteralPath (Join-Path $root "Tools/provision_monolith_client_static.ps1") -Raw -Encoding UTF8
Assert-Contract ($provisionText.Contains('Read-LuaMBinaryReleaseReceipt')) "Client provisioning does not require the binary release receipt."
Assert-Contract ($provisionText.Contains("Assert-LuaMRemoteMutationAllowed -Policy `$mutationPolicy -Mutation 'client-static'")) "Client provisioning does not use the shared remote mutation guard."

$gatewayDeployText = Get-Content -LiteralPath (Join-Path $root "Tools/deploy_luam_ai_gateway.ps1") -Raw -Encoding UTF8
Assert-Contract ($gatewayDeployText.Contains("Assert-LuaMRemoteMutationAllowed -Policy `$mutationPolicy -Mutation 'ai-gateway'")) "Gateway deployment does not use the shared remote mutation guard."

$result = [pscustomobject]@{
    ok = $true
    schemaVersion = [int] $gate.schemaVersion
    remoteDeployFrozen = [bool] $policy.remoteDeployFrozen
    requiredFileCount = $requiredFiles.Count
    packageScopeCount = $packageScopes.Count
    productionTests = $testNames
    smokeChecks = $smokeNames
    criticalTestFileCount = $criticalTestFiles.Count
}

if ($Json) {
    $result | ConvertTo-Json -Depth 4
} else {
    Write-Host "LuaM release contract: OK"
    Write-Host "Policy-required files: $($result.requiredFileCount)"
    Write-Host "Production tests: $($result.productionTests -join ', ')"
    Write-Host "Smoke checks: $($result.smokeChecks -join ', ')"
    Write-Host "remoteDeployFrozen: $($result.remoteDeployFrozen)"
}
