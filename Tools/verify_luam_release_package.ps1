param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,
    [switch]$Json
)

$ErrorActionPreference = "Stop"

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

function Normalize-ZipPath {
    param([string]$Path)

    return $Path.Replace('\', '/').TrimStart('/')
}

function Read-ZipText {
    param(
        [System.IO.Compression.ZipArchive]$Archive,
        [string]$EntryName
    )

    $entry = $Archive.GetEntry($EntryName)
    if ($null -eq $entry) {
        throw "Missing package entry: $EntryName"
    }

    $stream = $entry.Open()
    try {
        $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8)
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
    param([System.IO.Compression.ZipArchiveEntry]$Entry)

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

Add-Type -AssemblyName System.IO.Compression.FileSystem

$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$archive = [System.IO.Compression.ZipFile]::OpenRead($resolvedPackage)
try {
    $manifestText = Read-ZipText -Archive $archive -EntryName "PACKAGE_MANIFEST.json"
    $fileListText = Read-ZipText -Archive $archive -EntryName "PACKAGE_FILES.txt"
    $manifest = $manifestText | ConvertFrom-Json
    Add-Step "manifest-readable" "passed" "PACKAGE_MANIFEST.json and PACKAGE_FILES.txt are readable."

    $listedFiles = @(
        $fileListText -split "`r?`n" |
            ForEach-Object { $_.Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            ForEach-Object { Normalize-ZipPath $_ }
    )

    $manifestFiles = @($manifest.files | ForEach-Object { Normalize-ZipPath $_.path })
    $entryByPath = @{}
    foreach ($entry in $archive.Entries) {
        $normalized = Normalize-ZipPath $entry.FullName
        if ($normalized.EndsWith("/") -or [string]::IsNullOrWhiteSpace($entry.Name)) {
            continue
        }

        $entryByPath[$normalized] = $entry
    }

    $zipFiles = @($entryByPath.Keys)

    if ($manifest.fileCount -ne $listedFiles.Count) {
        $issues.Add("Manifest fileCount $($manifest.fileCount) does not match PACKAGE_FILES count $($listedFiles.Count).") | Out-Null
    }

    if ($manifestFiles.Count -ne $listedFiles.Count) {
        $issues.Add("Manifest files count $($manifestFiles.Count) does not match PACKAGE_FILES count $($listedFiles.Count).") | Out-Null
    }

    $missingFromZip = @($listedFiles | Where-Object { $zipFiles -notcontains $_ })
    if ($missingFromZip.Count -gt 0) {
        $issues.Add("PACKAGE_FILES lists missing zip entries: $($missingFromZip -join ', ')") | Out-Null
    }

    $unexpectedPayload = @(
        $zipFiles |
            Where-Object { $_ -notin @("PACKAGE_MANIFEST.json", "PACKAGE_FILES.txt") } |
            Where-Object { $listedFiles -notcontains $_ }
    )
    if ($unexpectedPayload.Count -gt 0) {
        $issues.Add("Zip contains payload entries not listed in PACKAGE_FILES: $($unexpectedPayload -join ', ')") | Out-Null
    }

    $junk = @(
        $zipFiles |
            Where-Object { $_ -match "(^|/)(bin|obj|\.vs|\.idea|\.vscode|node_modules|__pycache__|\.pytest_cache|logs?|tmp|temp)(/|$)|\.(log|tmp|bak|cache|db|sqlite|sqlite3)$" }
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
    foreach ($file in @($manifest.files)) {
        $path = Normalize-ZipPath $file.path
        $entry = $entryByPath[$path]
        if ($null -eq $entry) {
            $hashMismatches.Add("$path missing from archive") | Out-Null
            continue
        }

        $actual = Get-ZipEntrySha256 -Entry $entry
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
        "Content.IntegrationTests/Tests/_LuaM/LuaMBankAndPdaContractsTest.cs",
        "Content.IntegrationTests/Tests/_LuaM/LuaMBankDurableMutationContractTest.cs",
        "Content.IntegrationTests/Tests/_LuaM/LuaMBankPersistenceTest.cs",
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
        "Content.Server/Cargo/Systems/CargoSystem.Orders.cs",
        "Content.Server/PDA/PdaSystem.cs",
        "Content.Server/_NF/Bank/ATMSystem.cs",
        "Content.Server/_NF/Bank/BankSystem.cs",
        "Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs",
        "Content.Server/_NF/Shipyard/Systems/ShipyardSystem.cs",
        "Content.Server.Database/Model.cs",
        "Content.Server.Database/Migrations/Postgres/20260713070624_PreserveCharacterProfiles.cs",
        "Content.Server.Database/Migrations/Postgres/20260713070624_PreserveCharacterProfiles.Designer.cs",
        "Content.Server.Database/Migrations/Postgres/20260713162540_DurablePdaBankTransfers.cs",
        "Content.Server.Database/Migrations/Postgres/20260713162540_DurablePdaBankTransfers.Designer.cs",
        "Content.Server.Database/Migrations/Postgres/PostgresServerDbContextModelSnapshot.cs",
        "Content.Server.Database/Migrations/Sqlite/20260713070603_PreserveCharacterProfiles.cs",
        "Content.Server.Database/Migrations/Sqlite/20260713070603_PreserveCharacterProfiles.Designer.cs",
        "Content.Server.Database/Migrations/Sqlite/20260713162532_DurablePdaBankTransfers.cs",
        "Content.Server.Database/Migrations/Sqlite/20260713162532_DurablePdaBankTransfers.Designer.cs",
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
        "Resources/ConfigPresets/_Mono/monolithCore.toml",
        "Resources/Changelog/Parts/luam-animal-population-cap.yml",
        "Resources/Changelog/Parts/luam-sector-traffic.yml",
        "Resources/Changelog/Parts/luam-expedition-persistence-foundations.yml",
        "Resources/Changelog/Parts/luam-pda-bank-transfer-fix.yml",
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
        "Tools/summarize_luam_ai_audit.py",
        "Tools/luam_release_policy.json",
        "Tools/luam_character_progression_design.md",
        "Tools/luam_expedition_implementation_proposal.md",
        "Tools/luam_expedition_worldgen_design.md",
        "Tools/setup_luam_piper_tts.ps1",
        "Tools/test_luam_ai_gateway.py",
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

    if ($manifest.readiness -eq $null) {
        $issues.Add("Manifest does not contain readiness evidence.") | Out-Null
        Add-Step "readiness-evidence" "failed" "Missing readiness object."
    } elseif ($manifest.readiness.ok -ne $true) {
        $issues.Add("Manifest readiness evidence is not OK.") | Out-Null
        Add-Step "readiness-evidence" "failed" "readiness.ok is not true."
    } else {
        $detail = "readiness ok"
        $readinessEvidenceOk = $true
        $adminRankStep = @($manifest.readiness.steps) | Where-Object { $_.name -eq "admin-rank-ladder" } | Select-Object -First 1
        if ($null -eq $adminRankStep -or $adminRankStep.status -ne "passed") {
            $issues.Add("Manifest readiness evidence does not include passed admin-rank-ladder step.") | Out-Null
            $readinessEvidenceOk = $false
        } else {
            $detail += ", admin rank ladder recorded"
        }

        if ($manifest.readiness.runTests -eq $true) {
            $detail += ", dotnet tests recorded"
        } else {
            $warnings.Add("Package readiness did not record RunTests=true.") | Out-Null
        }

        if ($manifest.readiness.runLocalSmoke -eq $true) {
            $detail += ", local smoke recorded"
        } else {
            $warnings.Add("Package readiness did not record RunLocalSmoke=true.") | Out-Null
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
                ForEach-Object { Normalize-ZipPath ([string] $_) } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        )
        $allowedOutsidePackage = @(
            $manifest.scopeAudit.allowedChangedFilesOutsidePackage |
                ForEach-Object { Normalize-ZipPath ([string] $_) } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        )

        if ($unexpectedOutsidePackage.Count -gt 0) {
            $issues.Add("Changed files outside package are not allowlisted: $($unexpectedOutsidePackage -join ', ')") | Out-Null
            Add-Step "scope-audit" "failed" "$($unexpectedOutsidePackage.Count) unexpected changed file(s) outside package."
        } else {
            $detail = "No unexpected changed files outside package."
            if ($allowedOutsidePackage.Count -gt 0) {
                $warnings.Add("Package scope audit has allowlisted changed files outside package: $($allowedOutsidePackage -join ', ')") | Out-Null
                $detail += " $($allowedOutsidePackage.Count) allowlisted outside-package file(s) recorded."
            }

            Add-Step "scope-audit" "passed" $detail
        }
    }

    $packageHash = Get-FileHash -Algorithm SHA256 -LiteralPath $resolvedPackage
    Add-Step "package-hash" "passed" "Package SHA256 $($packageHash.Hash.ToLowerInvariant())."

    $result = [pscustomobject]@{
        ok = $issues.Count -eq 0
        package = $resolvedPackage
        sha256 = $packageHash.Hash.ToLowerInvariant()
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
