param(
    [string]$OutputDir = "DeploymentPackages\LuaM",
    [switch]$RunTests,
    [switch]$RunLocalSmoke,
    [switch]$SkipReadiness,
    [switch]$Json
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$releaseName = "luam-local-release-" + [DateTime]::UtcNow.ToString("yyyyMMdd-HHmmss")
$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDir)) {
    $OutputDir
} else {
    Join-Path $root $OutputDir
}

$releaseDirectories = @(
    "Content.Client/_LuaM",
    "Content.Server/_LuaM",
    "Content.Shared/_LuaM",
    "Content.IntegrationTests/Tests/_LuaM",
    "Content.IntegrationTests/Tests/_NF/BountyContracts",
    "Content.Tests/Client/_LuaM",
    "Content.Tests/Server/_LuaM",
    "Content.Tests/Shared/_NF/BountyContracts",
    "Resources/ConfigPresets/_LuaM",
    "Resources/Locale/en-US/_LuaM",
    "Resources/Locale/ru-RU/_LuaM",
    "Resources/Prototypes/_LuaM",
    "Resources/ServerInfo/_LuaM",
    "Resources/Textures/_LuaM"
)

$releaseFiles = @(
    "Content.Client/PDA/PdaBoundUserInterface.cs",
    "Content.Client/PDA/PdaMenu.xaml",
    "Content.Client/PDA/PdaMenu.xaml.cs",
    "Content.Client/Administration/UI/Tabs/AdminTab/AdminTab.xaml",
    "Content.Client/Administration/UI/Tabs/AdminTab/AdminTab.xaml.cs",
    "Content.Client/Clothing/ClientClothingSystem.cs",
    "Content.Client/Research/UI/ResearchConsoleMenu.xaml",
    "Content.Client/_Goobstation/Research/UI/FancyResearchConsoleItem.xaml",
    "Content.Client/_Goobstation/Research/UI/FancyResearchConsoleItem.xaml.cs",
    "Content.Client/_Goobstation/Research/UI/FancyResearchConsoleMenu.xaml",
    "Content.Client/_Goobstation/Research/UI/FancyResearchConsoleMenu.xaml.cs",
    "Content.Client/_NF/BountyContracts/UI/BountyContractUi.cs",
    "Content.Client/_NF/BountyContracts/UI/BountyContractUiFragmentCreate.xaml.cs",
    "Content.Client/_NF/BountyContracts/UI/BountyContractUiFragmentList.xaml.cs",
    "Content.Client/_NF/BountyContracts/UI/BountyContractUiFragmentListEntry.xaml",
    "Content.Client/_NF/BountyContracts/UI/BountyContractUiFragmentListEntry.xaml.cs",
    "Content.Client/_NF/LateJoin/Controls/CrewPickerControl.xaml.cs",
    "Content.Client/_NF/LateJoin/Extensions/StationJobInformationExtensions.cs",
    "Content.Client/_NF/LateJoin/Windows/PickerWindow.xaml.cs",
    "Content.Server/Cargo/Systems/CargoSystem.Bounty.cs",
    "Content.Server/CartridgeLoader/CartridgeLoaderSystem.cs",
    "Content.Server/PDA/PdaSystem.cs",
    "Content.Server/Pinpointer/NavMapSystem.cs",
    "Content.Server/Players/PlayTimeTracking/PlayTimeTrackingSystem.cs",
    "Content.Server/Shuttles/Systems/ShuttleSystem.FasterThanLight.cs",
    "Content.Server/Station/Systems/StationJobsSystem.cs",
    "Content.Server/_CorvaxNext/Silicons/Borgs/AiRemoteControlSystem.cs",
    "Content.Server/_NF/Bank/BankSystem.cs",
    "Content.Server/_NF/BountyContracts/BountyContractSystem.Ui.cs",
    "Content.Server/_NF/BountyContracts/BountyContractSystem.cs",
    "Content.IntegrationTests/Pair/TestPair.cs",
    "Content.IntegrationTests/Tests/Lobby/CharacterCreationTest.cs",
    "Content.IntegrationTests/Utility/GameDataScrounger.Files.cs",
    "Content.Tests/Server/_LuaM/LuaMSectorPlayerBriefingTest.cs",
    "Content.Shared/CCVar/CCVars.LuaM.cs",
    "Content.Shared/Inventory/SlotFlags.cs",
    "Content.Shared/PDA/PdaComponent.cs",
    "Content.Shared/PDA/PdaMessagesUi.cs",
    "Content.Shared/PDA/PdaUpdateState.cs",
    "Content.Shared/Preferences/HumanoidCharacterProfile.cs",
    "Content.Shared/_CorvaxNext/Silicons/Borgs/Components/SharedAiRemoteControllerComponent.cs",
    "Content.Shared/_NF/BountyContracts/SharedBountyContractSystem.cs",
    "Resources/Locale/en-US/_Goobstation/research/ui.ftl",
    "Resources/Locale/en-US/_Mono/gamerules/gamemodes.ftl",
    "Resources/Locale/en-US/_NF/bank/bank-ATM-component.ftl",
    "Resources/Locale/en-US/_NF/bounty-contracts/bounty-contracts.ftl",
    "Resources/Locale/en-US/_NF/cartridge-loader/cartridges.ftl",
    "Resources/Locale/en-US/_NF/pda/pda-component.ftl",
    "Resources/Locale/en-US/administration/ui/tabs/admin-tab/player-actions-window.ftl",
    "Resources/Locale/en-US/cargo/cargo-bounty-console.ftl",
    "Resources/Locale/en-US/holiday/greet/holiday-greet.ftl",
    "Resources/Locale/ru-RU/_Goobstation/research/ui.ftl",
    "Resources/Locale/ru-RU/_Mono/gamerules/gamemodes.ftl",
    "Resources/Locale/ru-RU/_NF/bank/bank-ATM-component.ftl",
    "Resources/Locale/ru-RU/_NF/bounty-contracts/bounty-contracts.ftl",
    "Resources/Locale/ru-RU/_NF/cartridge-loader/cartridges.ftl",
    "Resources/Locale/ru-RU/_NF/pda/pda-component.ftl",
    "Resources/Locale/ru-RU/administration/ui/tabs/admin-tab/player-actions-window.ftl",
    "Resources/Locale/ru-RU/cargo/cargo-bounty-console.ftl",
    "Resources/Locale/ru-RU/holiday/greet/holiday-greet.ftl",
    "Resources/Prototypes/Entities/Mobs/Species/arachnid.yml",
    "Resources/Prototypes/Entities/Mobs/Species/base.yml",
    "Resources/Prototypes/InventoryTemplates/arachnid_inventory_template.yml",
    "Resources/Prototypes/InventoryTemplates/corpse_inventory_template.yml",
    "Resources/Prototypes/InventoryTemplates/human_inventory_template.yml",
    "Resources/Prototypes/_NF/Entities/Mobs/NPCs/mob_hostile_rogue_ai.yml",
    "Resources/Prototypes/_NF/Loadouts/Jobs/Contractor/cartridge.yml",
    "Resources/Prototypes/_NF/Loadouts/contractor_loadout_groups.yml",
    "Resources/Prototypes/_NF/bounty_contract_collections.yml",
    "Resources/Prototypes/_Mono/Entities/Markers/Spawners/shuttles.yml",
    "Resources/Prototypes/_Mono/lobbyscreens.yml",
    "Resources/Prototypes/holidays.yml",
    "Resources/manifest.yml",
    "Resources/ServerInfo/Intro.txt",
    "server_config.remote.toml",
    "Content.Packaging/ClientPackaging.cs",
    "Content.Packaging/ServerPackaging.cs",
    "Content.Packaging/ReleaseSurfacePolicy.cs",
    ".github/workflows/publish.yml",
    ".github/workflows/publish-testing.yml",
    ".github/workflows/test-packaging.yml",
    "Tools/check_luam_release_ready.ps1",
    "Tools/build_luam_release_package.ps1",
    "Tools/build_luam_server_release.ps1",
    "Tools/verify_luam_release_package.ps1",
    "Tools/audit_release_surface.ps1",
    "Tools/deploy_luam_server_release.ps1",
    "Tools/local_stack.md",
    "Tools/luam_admin_ranks.yml",
    "Tools/generate_luam_admin_rank_sql.py",
    "Tools/luam_ai_gateway.py",
    "Tools/summarize_luam_ai_audit.py",
    "Tools/luam_release_manifest.md",
    "Tools/luam_release_policy.json",
    "Tools/start_local_stack.ps1",
    "Tools/stop_local_stack.ps1",
    "Tools/test_local_frontier.ps1",
    "Tools/test_local_stack.ps1",
    "Tools/test_luam_ai_gateway.py",
    "Tools/validate_luam_feature_pack.py",
    "DeploymentPackages/LuaM/luam-admin-ranks.sqlite.sql",
    "DeploymentPackages/LuaM/luam-admin-ranks.postgres.sql",
    "DeploymentPackages/LuaM/luam-admin-ranks.md"
)

$releaseScopes = @(
    "Content.Client/_LuaM",
    "Content.Server/_LuaM",
    "Content.Shared/_LuaM",
    "Content.IntegrationTests/Tests/_LuaM",
    "Content.IntegrationTests/Tests/_NF/BountyContracts",
    "Content.IntegrationTests/Pair/TestPair.cs",
    "Content.IntegrationTests/Tests/Lobby/CharacterCreationTest.cs",
    "Content.IntegrationTests/Utility/GameDataScrounger.Files.cs",
    "Content.Tests/Client/_LuaM",
    "Content.Tests/Server/_LuaM",
    "Content.Tests/Shared/_NF/BountyContracts",
    "Content.Client/Administration/UI/Tabs/AdminTab/AdminTab.xaml",
    "Content.Client/Administration/UI/Tabs/AdminTab/AdminTab.xaml.cs",
    "Content.Client/Clothing/ClientClothingSystem.cs",
    "Content.Client/PDA",
    "Content.Client/Research/UI/ResearchConsoleMenu.xaml",
    "Content.Client/_Goobstation/Research/UI/FancyResearchConsoleItem.xaml",
    "Content.Client/_Goobstation/Research/UI/FancyResearchConsoleItem.xaml.cs",
    "Content.Client/_Goobstation/Research/UI/FancyResearchConsoleMenu.xaml",
    "Content.Client/_Goobstation/Research/UI/FancyResearchConsoleMenu.xaml.cs",
    "Content.Client/_NF/BountyContracts",
    "Content.Client/_NF/LateJoin",
    "Content.Server/Cargo/Systems/CargoSystem.Bounty.cs",
    "Content.Server/CartridgeLoader/CartridgeLoaderSystem.cs",
    "Content.Server/PDA",
    "Content.Server/Pinpointer/NavMapSystem.cs",
    "Content.Server/Players/PlayTimeTracking/PlayTimeTrackingSystem.cs",
    "Content.Server/Shuttles/Systems/ShuttleSystem.FasterThanLight.cs",
    "Content.Server/Station/Systems/StationJobsSystem.cs",
    "Content.Server/_CorvaxNext/Silicons/Borgs/AiRemoteControlSystem.cs",
    "Content.Shared/PDA",
    "Content.Shared/CCVar/CCVars.LuaM.cs",
    "Content.Shared/Inventory/SlotFlags.cs",
    "Content.Shared/Preferences/HumanoidCharacterProfile.cs",
    "Content.Shared/_CorvaxNext/Silicons/Borgs/Components/SharedAiRemoteControllerComponent.cs",
    "Content.Server/_NF/Bank",
    "Content.Server/_NF/BountyContracts",
    "Content.Shared/_NF/Bank",
    "Content.Shared/_NF/BountyContracts",
    "Resources/ConfigPresets/_LuaM",
    "Resources/Locale/en-US/_Goobstation/research/ui.ftl",
    "Resources/Locale/en-US/_LuaM",
    "Resources/Locale/en-US/_Mono/gamerules/gamemodes.ftl",
    "Resources/Locale/en-US/_NF/bank/bank-ATM-component.ftl",
    "Resources/Locale/en-US/_NF/bounty-contracts/bounty-contracts.ftl",
    "Resources/Locale/en-US/_NF/cartridge-loader/cartridges.ftl",
    "Resources/Locale/en-US/_NF/pda/pda-component.ftl",
    "Resources/Locale/en-US/administration/ui/tabs/admin-tab/player-actions-window.ftl",
    "Resources/Locale/en-US/cargo/cargo-bounty-console.ftl",
    "Resources/Locale/en-US/holiday/greet/holiday-greet.ftl",
    "Resources/Locale/ru-RU/_Goobstation/research/ui.ftl",
    "Resources/Locale/ru-RU/_LuaM",
    "Resources/Locale/ru-RU/_Mono/gamerules/gamemodes.ftl",
    "Resources/Locale/ru-RU/_NF/bank/bank-ATM-component.ftl",
    "Resources/Locale/ru-RU/_NF/bounty-contracts/bounty-contracts.ftl",
    "Resources/Locale/ru-RU/_NF/cartridge-loader/cartridges.ftl",
    "Resources/Locale/ru-RU/_NF/pda/pda-component.ftl",
    "Resources/Locale/ru-RU/administration/ui/tabs/admin-tab/player-actions-window.ftl",
    "Resources/Locale/ru-RU/cargo/cargo-bounty-console.ftl",
    "Resources/Locale/ru-RU/holiday/greet/holiday-greet.ftl",
    "Resources/Prototypes/Entities/Mobs/Species/arachnid.yml",
    "Resources/Prototypes/Entities/Mobs/Species/base.yml",
    "Resources/Prototypes/InventoryTemplates/arachnid_inventory_template.yml",
    "Resources/Prototypes/InventoryTemplates/corpse_inventory_template.yml",
    "Resources/Prototypes/InventoryTemplates/human_inventory_template.yml",
    "Resources/Prototypes/_LuaM",
    "Resources/Prototypes/_NF/Entities/Mobs/NPCs/mob_hostile_rogue_ai.yml",
    "Resources/Prototypes/_NF/Loadouts",
    "Resources/Prototypes/_NF/PointsOfInterest",
    "Resources/Prototypes/_NF/Roles/Jobs",
    "Resources/Prototypes/_NF/bounty_contract_collections.yml",
    "Resources/Prototypes/_Mono/Entities/Markers/Spawners/shuttles.yml",
    "Resources/Prototypes/_Mono/Roles/Jobs",
    "Resources/Prototypes/_Mono/lobbyscreens.yml",
    "Resources/Prototypes/holidays.yml",
    "Resources/ServerInfo/Intro.txt",
    "Resources/ServerInfo/_LuaM",
    "Resources/Textures/_LuaM",
    "Resources/manifest.yml",
    "server_config.remote.toml",
    "Content.Packaging/ClientPackaging.cs",
    "Content.Packaging/ServerPackaging.cs",
    "Content.Packaging/ReleaseSurfacePolicy.cs",
    ".github/workflows/publish.yml",
    ".github/workflows/publish-testing.yml",
    ".github/workflows/test-packaging.yml",
    "Tools/check_luam_release_ready.ps1",
    "Tools/build_luam_release_package.ps1",
    "Tools/build_luam_server_release.ps1",
    "Tools/verify_luam_release_package.ps1",
    "Tools/audit_release_surface.ps1",
    "Tools/deploy_luam_server_release.ps1",
    "Tools/local_stack.md",
    "Tools/luam_admin_ranks.yml",
    "Tools/generate_luam_admin_rank_sql.py",
    "Tools/luam_ai_gateway.py",
    "Tools/summarize_luam_ai_audit.py",
    "Tools/luam_release_manifest.md",
    "Tools/luam_release_policy.json",
    "Tools/start_local_stack.ps1",
    "Tools/stop_local_stack.ps1",
    "Tools/test_local_frontier.ps1",
    "Tools/test_local_stack.ps1",
    "Tools/test_luam_ai_gateway.py",
    "Tools/validate_luam_feature_pack.py"
)

$allowedOutOfScopeChangedFiles = @(
    ".gitignore",
    "Content.Server/Movement/Systems/PullController.cs",
    "Content.Server/Nyanotrasen/Kitchen/EntitySystems/DeepFryerSystem.cs",
    "Content.Server/_Mono/Cleanup/CleanupHelperSystem.cs",
    "Resources/Maps/_NF/POI/bahama.yml",
    "Resources/Maps/_NF/POI/courthouse.yml",
    "Resources/Maps/_NF/POI/tinnia.yml",
    "Resources/Prototypes/Entities/Structures/Piping/Disposal/units.yml",
    "Resources/Prototypes/_Mono/Outpost/colossus.yml",
    "Resources/migration.yml",
    "RobustToolbox",
    "Tools/monolith-restart-when-empty.ps1"
)

function Convert-ToRepoPath {
    param([string]$Path)

    return $Path.Replace('\', '/').TrimStart('/')
}

function Get-RelativePath {
    param(
        [string]$BasePath,
        [string]$FullPath
    )

    $baseUri = [Uri] (($BasePath.TrimEnd('\') + '\').Replace('\', '/'))
    $fileUri = [Uri] ($FullPath.Replace('\', '/'))
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($fileUri).ToString())
}

function Add-File {
    param(
        [System.Collections.Generic.HashSet[string]]$Set,
        [string]$Path
    )

    $repoPath = Convert-ToRepoPath $Path
    if ($repoPath -match "(^|/)(bin|obj|\.vs|\.idea|\.vscode|node_modules|__pycache__|\.pytest_cache|logs?|tmp|temp)(/|$)|\.(log|tmp|bak|cache|db|sqlite|sqlite3)$") {
        return
    }

    $fullPath = Join-Path $root ($repoPath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
    if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
        $Set.Add($repoPath) | Out-Null
    }
}

function Invoke-Readiness {
    $args = @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        "Tools\check_luam_release_ready.ps1",
        "-AllowUntracked",
        "-Json"
    )

    if ($RunTests) {
        $args += "-RunTests"
    }

    if ($RunLocalSmoke) {
        $args += "-RunLocalSmoke"
    }

    $output = & powershell @args
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "LuaM release readiness failed before package build: $output"
    }

    return $output | ConvertFrom-Json
}

function Invoke-AdminRankSqlGeneration {
    $output = & python "Tools\generate_luam_admin_rank_sql.py" "--output-dir" "DeploymentPackages\LuaM" "--json"
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "LuaM admin rank SQL generation failed before package build: $output"
    }

    return $output | ConvertFrom-Json
}

function Get-ChangedFiles {
    $changed = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($file in @(& git diff --name-only) + @(& git ls-files --others --exclude-standard)) {
        if ([string]::IsNullOrWhiteSpace($file)) {
            continue
        }

        $changed.Add((Convert-ToRepoPath $file)) | Out-Null
    }

    return @($changed) | Sort-Object
}

Push-Location $root
try {
    $readiness = $null
    if (-not $SkipReadiness) {
        $readiness = Invoke-Readiness
    }

    Invoke-AdminRankSqlGeneration | Out-Null

    $files = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($directory in $releaseDirectories) {
        $fullDirectory = Join-Path $root $directory
        if (-not (Test-Path -LiteralPath $fullDirectory -PathType Container)) {
            throw "Missing release directory: $directory"
        }

        foreach ($file in Get-ChildItem -LiteralPath $fullDirectory -Recurse -File) {
            Add-File -Set $files -Path (Get-RelativePath -BasePath $root -FullPath $file.FullName)
        }
    }

    foreach ($file in $releaseFiles) {
        Add-File -Set $files -Path $file
    }

    $changedInScope = @(& git ls-files --modified --others --exclude-standard -- @releaseScopes)
    foreach ($file in $changedInScope) {
        Add-File -Set $files -Path $file
    }

    if ($files.Count -eq 0) {
        throw "No files selected for LuaM release package."
    }

    New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
    $staging = Join-Path ([System.IO.Path]::GetTempPath()) ($releaseName + "-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $staging | Out-Null

    $orderedFiles = @($files) | Sort-Object
    $fileHashes = New-Object System.Collections.Generic.List[object]

    foreach ($relative in $orderedFiles) {
        $source = Join-Path $root ($relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        $destination = Join-Path $staging ($relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Force

        $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $source
        $fileHashes.Add([pscustomobject]@{
            path = $relative
            sha256 = $hash.Hash.ToLowerInvariant()
            bytes = (Get-Item -LiteralPath $source).Length
        }) | Out-Null
    }

    $gitHead = (& git rev-parse HEAD).Trim()
    $gitBranch = (& git rev-parse --abbrev-ref HEAD).Trim()
    $untracked = @(& git ls-files --others --exclude-standard -- @releaseScopes)
    $fileHashArray = @($fileHashes | ForEach-Object { $_ })
    $changedFiles = @(Get-ChangedFiles)
    $packagedChangedFiles = @($changedFiles | Where-Object { $files.Contains($_) })
    $changedFilesOutsidePackage = @($changedFiles | Where-Object { -not $files.Contains($_) })
    $allowedOutOfScope = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $allowedOutOfScopeChangedFiles) {
        $allowedOutOfScope.Add((Convert-ToRepoPath $file)) | Out-Null
    }
    $unexpectedChangedFilesOutsidePackage = @(
        $changedFilesOutsidePackage |
            Where-Object { -not $allowedOutOfScope.Contains($_) }
    )
    $allowedChangedFilesOutsidePackage = @(
        $changedFilesOutsidePackage |
            Where-Object { $allowedOutOfScope.Contains($_) }
    )

    $packageManifest = [pscustomobject]@{
        name = $releaseName
        generatedAtUtc = [DateTime]::UtcNow.ToString("o")
        sourceRoot = $root
        git = [pscustomobject]@{
            branch = $gitBranch
            head = $gitHead
            untrackedReleaseFiles = $untracked.Count
        }
        readiness = $readiness
        scopeAudit = [pscustomobject]@{
            changedFileCount = $changedFiles.Count
            packagedChangedFileCount = $packagedChangedFiles.Count
            changedFilesOutsidePackageCount = $changedFilesOutsidePackage.Count
            allowedChangedFilesOutsidePackage = $allowedChangedFilesOutsidePackage
            unexpectedChangedFilesOutsidePackage = $unexpectedChangedFilesOutsidePackage
        }
        fileCount = $orderedFiles.Count
        files = $fileHashArray
    }

    $manifestPath = Join-Path $staging "PACKAGE_MANIFEST.json"
    $fileListPath = Join-Path $staging "PACKAGE_FILES.txt"
    $packageManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    $orderedFiles | Set-Content -LiteralPath $fileListPath -Encoding UTF8

    $zipPath = Join-Path $outputRoot ($releaseName + ".zip")
    Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $zipPath -Force
    $zipHash = Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath

    Remove-Item -LiteralPath $staging -Recurse -Force

    $result = [pscustomobject]@{
        ok = $true
        package = $zipPath
        sha256 = $zipHash.Hash.ToLowerInvariant()
        fileCount = $orderedFiles.Count
        untrackedReleaseFiles = $untracked.Count
        scopeAudit = [pscustomobject]@{
            changedFileCount = $changedFiles.Count
            packagedChangedFileCount = $packagedChangedFiles.Count
            changedFilesOutsidePackageCount = $changedFilesOutsidePackage.Count
            unexpectedChangedFilesOutsidePackageCount = $unexpectedChangedFilesOutsidePackage.Count
        }
        readinessRan = -not $SkipReadiness
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 4
    } else {
        Write-Host "LuaM release package built:"
        Write-Host "  package: $($result.package)"
        Write-Host "  sha256:  $($result.sha256)"
        Write-Host "  files:   $($result.fileCount)"
        Write-Host "  untracked release files included: $($result.untrackedReleaseFiles)"
        Write-Host "  changed files packaged: $($result.scopeAudit.packagedChangedFileCount)"
        Write-Host "  changed files outside package: $($result.scopeAudit.changedFilesOutsidePackageCount)"
        Write-Host "  unexpected outside package: $($result.scopeAudit.unexpectedChangedFilesOutsidePackageCount)"
    }
}
finally {
    Pop-Location
}
