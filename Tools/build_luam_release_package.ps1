param(
    [string]$OutputDir = "DeploymentPackages\LuaM",
    [switch]$RunTests,
    [switch]$RunLocalSmoke,
    [switch]$AllowUntracked,
    [switch]$SkipReadiness,
    [switch]$Json
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "luam_release_contract.ps1")
$releasePolicy = Read-LuaMReleasePolicy -Root $root
$releasePolicyPath = Join-Path $root "Tools\luam_release_policy.json"
$releasePolicySha256 = (Get-FileHash -LiteralPath $releasePolicyPath -Algorithm SHA256).Hash.ToLowerInvariant()
$policyRequiredFiles = @(Get-LuaMReleaseGateRequiredFiles -Policy $releasePolicy)
$policyPackageScopes = @(Get-LuaMReleaseGatePackageScopes -Policy $releasePolicy)
$policyExcludedLocalArtifacts = @(Get-LuaMReleaseExcludedLocalArtifacts -Policy $releasePolicy)
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
    "Resources/Maps/_LuaM",
    "Resources/Prototypes/_LuaM",
    "Resources/ServerInfo/_LuaM",
    "Resources/Textures/_LuaM"
)

$releaseFiles = @(
    "Content.IntegrationTests/Pair/TestPair.Recycle.cs",
    "Content.Server/NPC/HTN/HTNSystem.cs",
    "Content.Shared/Mind/SharedMindSystem.cs",
    "Content.Client/PDA/PdaBoundUserInterface.cs",
    "Content.Client/PDA/PdaMenu.xaml",
    "Content.Client/PDA/PdaMenu.xaml.cs",
    "Content.Client/_NF/Shipyard/BUI/ShipyardConsoleBoundUserInterface.cs",
    "Content.Client/_NF/Shipyard/UI/ShipyardConsoleMenu.xaml",
    "Content.Client/_NF/Shipyard/UI/ShipyardConsoleMenu.xaml.cs",
    "Content.Client/Administration/UI/Tabs/AdminTab/AdminTab.xaml",
    "Content.Client/Administration/UI/Tabs/AdminTab/AdminTab.xaml.cs",
    "Content.Client/Clothing/ClientClothingSystem.cs",
    "Content.Client/Lobby/LobbyState.cs",
    "Content.Client/Players/PlayTimeTracking/JobRequirementsManager.cs",
    "Content.Client/RoundEnd/RoundEndSummaryWindow.cs",
    "Content.Client/_LuaM/Sector/LuaMAiTtsAudioSystem.cs",
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
    "Content.Server/Access/Systems/IdCardConsoleSystem.cs",
    "Content.Server/Cargo/Systems/CargoSystem.Bounty.cs",
    "Content.Server/Cargo/Systems/CargoSystem.Orders.cs",
    "Content.Server/CartridgeLoader/CartridgeLoaderSystem.cs",
    "Content.Server/Chat/Systems/ChatSystem.cs",
    "Content.Server/Gateway/Components/GatewayGeneratorDestinationComponent.cs",
    "Content.Server/Gateway/Systems/GatewayGeneratorSystem.cs",
    "Content.Server/Nutrition/EntitySystems/AnimalHusbandrySystem.cs",
    "Content.Server/Spawners/Components/TimedSpawnerComponent.cs",
    "Content.Server/Spawners/EntitySystems/SpawnerSystem.cs",
    "Content.Server/StationEvents/Events/VentCrittersRule.cs",
    "Content.Server/PDA/PdaSystem.cs",
    "Content.Server/Pinpointer/NavMapSystem.cs",
    "Content.Server/Players/PlayTimeTracking/PlayTimeTrackingSystem.cs",
    "Content.Server/Radio/EntitySystems/HeadsetSystem.cs",
    "Content.Server/Shuttles/Systems/ShuttleSystem.FasterThanLight.cs",
    "Content.Server/Station/Systems/StationJobsSystem.cs",
    "Content.Server/Database/ServerDbBase.cs",
    "Content.Server/Database/ServerDbManager.cs",
    "Content.Server/Database/DatabaseRecords.LuaMShips.cs",
    "Content.Server/Preferences/Managers/IServerPreferencesManager.cs",
    "Content.Server/Preferences/Managers/ServerPreferencesManager.cs",
    "Content.Server.Database/Model.cs",
    "Content.Server.Database/Migrations/Postgres/20260713070624_PreserveCharacterProfiles.cs",
    "Content.Server.Database/Migrations/Postgres/20260713070624_PreserveCharacterProfiles.Designer.cs",
    "Content.Server.Database/Migrations/Postgres/20260713162540_DurablePdaBankTransfers.cs",
    "Content.Server.Database/Migrations/Postgres/20260713162540_DurablePdaBankTransfers.Designer.cs",
    "Content.Server.Database/Migrations/Postgres/20260720164309_LuaMShipPayloadRevision.cs",
    "Content.Server.Database/Migrations/Postgres/20260720164309_LuaMShipPayloadRevision.Designer.cs",
    "Content.Server.Database/Migrations/Postgres/20260727090000_LuaMShipFleetOwnerIndex.cs",
    "Content.Server.Database/Migrations/Postgres/PostgresServerDbContextModelSnapshot.cs",
    "Content.Server.Database/Migrations/Sqlite/20260713070603_PreserveCharacterProfiles.cs",
    "Content.Server.Database/Migrations/Sqlite/20260713070603_PreserveCharacterProfiles.Designer.cs",
    "Content.Server.Database/Migrations/Sqlite/20260713162532_DurablePdaBankTransfers.cs",
    "Content.Server.Database/Migrations/Sqlite/20260713162532_DurablePdaBankTransfers.Designer.cs",
    "Content.Server.Database/Migrations/Sqlite/20260720164259_LuaMShipPayloadRevision.cs",
    "Content.Server.Database/Migrations/Sqlite/20260720164259_LuaMShipPayloadRevision.Designer.cs",
    "Content.Server.Database/Migrations/Sqlite/20260727090000_LuaMShipFleetOwnerIndex.cs",
    "Content.Server.Database/Migrations/Sqlite/SqliteServerDbContextModelSnapshot.cs",
    "Content.Server/_LuaM/Expeditions/LuaMExpeditionPlan.cs",
    "Content.Server/_LuaM/Expeditions/LuaMExpeditionPlanCommand.cs",
    "Content.Server/_LuaM/Expeditions/LuaMExpeditionPlanValidator.cs",
    "Content.Server/_LuaM/Expeditions/LuaMExpeditionPlanner.cs",
    "Content.Server/_LuaM/Progression/LuaMCampaignShiftClock.cs",
    "Content.Server/_LuaM/Progression/LuaMCareerProgressionRules.cs",
    "Content.Server/_LuaM/Administration/LuaMShipPersistenceCommands.cs",
    "Content.Server/_LuaM/Sector/LuaMCharacterTtsSystem.cs",
    "Content.Server/_LuaM/Sector/LuaMSectorTrafficContactComponent.cs",
    "Content.Server/_LuaM/Sector/LuaMSectorTrafficSystem.cs",
    "Content.Server/_CorvaxNext/Silicons/Borgs/AiRemoteControlSystem.cs",
    "Content.Server/_NF/Bank/ATMSystem.cs",
    "Content.Server/_NF/Bank/BankSystem.cs",
    "Content.Server/_NF/ShuttleRecords/ShuttleRecordsSystem.Console.cs",
    "Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs",
    "Content.Server/_NF/Shipyard/Systems/ShipyardSystem.cs",
    "Content.Server/_NF/BountyContracts/BountyContractSystem.Ui.cs",
    "Content.Server/_NF/BountyContracts/BountyContractSystem.cs",
    "Content.IntegrationTests/Pair/TestPair.cs",
    "Content.IntegrationTests/PoolManager.Cvars.cs",
    "Content.IntegrationTests/Tests/Gateway/GatewayGeneratorGrowthLimitTest.cs",
    "Content.IntegrationTests/Tests/_LuaM/LuaMAnimalHusbandryIntervalTest.cs",
    "Content.IntegrationTests/Tests/_LuaM/LuaMAnimalPopulationControlTest.cs",
    "Content.IntegrationTests/Tests/_LuaM/LuaMSectorTrafficTest.cs",
    "Content.IntegrationTests/Tests/_LuaM/LuaMSectorTrafficInterceptTest.cs",
    "Content.IntegrationTests/Tests/_LuaM/LuaMTimedSpawnerLimitTest.cs",
    "Content.IntegrationTests/Tests/_LuaM/LuaMCharacterPersistenceTest.cs",
    "Content.IntegrationTests/Tests/_LuaM/LuaMCharacterTtsValidationTest.cs",
    "Content.IntegrationTests/Tests/_LuaM/LuaMDynamicEventGrowthLimitTest.cs",
    "Content.IntegrationTests/Tests/_LuaM/LuaMExpeditionPlannerTest.cs",
    "Content.IntegrationTests/Tests/_LuaM/LuaMProgressionRulesTest.cs",
    "Content.IntegrationTests/Tests/Lobby/CharacterCreationTest.cs",
    "Content.IntegrationTests/Utility/GameDataScrounger.Files.cs",
    "Content.Tests/Server/_LuaM/LuaMSectorPlayerBriefingTest.cs",
    "Content.Shared/CCVar/CCVars.LuaM.cs",
    "Content.Shared/CCVar/CCVars.Misc.cs",
    "Content.Shared/Inventory/SlotFlags.cs",
    "Content.Shared/Nutrition/AnimalHusbandry/ReproductiveComponent.cs",
    "Content.Shared/PDA/PdaComponent.cs",
    "Content.Shared/PDA/PdaMessagesUi.cs",
    "Content.Shared/PDA/PdaUpdateState.cs",
    "Content.Shared/Preferences/HumanoidCharacterProfile.cs",
    "Content.Shared/Roles/JobRequirements.cs",
    "Content.Shared/Roles/SharedRoleSystem.cs",
    "Content.Shared/_LuaM/Sector/LuaMAiTtsAudioEvent.cs",
    "Content.Shared/_CorvaxNext/Silicons/Borgs/Components/SharedAiRemoteControllerComponent.cs",
    "Content.Shared/_NF/BountyContracts/SharedBountyContractSystem.cs",
    "Content.Shared/_NF/Shipyard/BUI/ShipyardConsoleInterfaceState.cs",
    "Content.Shared/_NF/Shipyard/Components/ShuttleDeedComponent.cs",
    "Content.Shared/_NF/Shipyard/Events/ShipyardConsoleParkMessage.cs",
    "Content.Shared/_NF/Shipyard/Events/ShipyardConsolePurchaseMessage.cs",
    "Content.Shared/_NF/ShuttleRecords/ShuttleRecord.cs",
    "Resources/Changelog/Parts/luam-animal-population-cap.yml",
    "Resources/Changelog/Parts/luam-sector-traffic.yml",
    "Resources/Changelog/Parts/luam-expedition-persistence-foundations.yml",
    "Resources/Changelog/Parts/luam-pda-bank-transfer-fix.yml",
    "Resources/Changelog/Parts/luam-ship-generator.yml",
    "Resources/migration.yml",
    "Resources/ConfigPresets/_Mono/monolithCore.toml",
    "Resources/Locale/en-US/_Goobstation/research/ui.ftl",
    "Resources/Locale/en-US/_Mono/gamerules/gamemodes.ftl",
    "Resources/Locale/en-US/_NF/bank/bank-ATM-component.ftl",
    "Resources/Locale/en-US/_NF/bounty-contracts/bounty-contracts.ftl",
    "Resources/Locale/en-US/_NF/cartridge-loader/cartridges.ftl",
    "Resources/Locale/en-US/_NF/pda/pda-component.ftl",
    "Resources/Locale/en-US/_NF/shipyard/shipyard-console-component.ftl",
    "Resources/Locale/en-US/administration/ui/tabs/admin-tab/player-actions-window.ftl",
    "Resources/Locale/en-US/cargo/cargo-bounty-console.ftl",
    "Resources/Locale/en-US/holiday/greet/holiday-greet.ftl",
    "Resources/Locale/ru-RU/_Goobstation/research/ui.ftl",
    "Resources/Locale/ru-RU/_Mono/gamerules/gamemodes.ftl",
    "Resources/Locale/ru-RU/_NF/bank/bank-ATM-component.ftl",
    "Resources/Locale/ru-RU/_NF/bounty-contracts/bounty-contracts.ftl",
    "Resources/Locale/ru-RU/_NF/cartridge-loader/cartridges.ftl",
    "Resources/Locale/ru-RU/_NF/pda/pda-component.ftl",
    "Resources/Locale/ru-RU/_NF/shipyard/shipyard-console-component.ftl",
    "Resources/Locale/ru-RU/administration/ui/tabs/admin-tab/player-actions-window.ftl",
    "Resources/Locale/ru-RU/cargo/cargo-bounty-console.ftl",
    "Resources/Locale/ru-RU/holiday/greet/holiday-greet.ftl",
    "Resources/Prototypes/Entities/Mobs/Species/arachnid.yml",
    "Resources/Prototypes/Entities/Mobs/Species/base.yml",
    "Resources/Prototypes/Entities/Markers/Spawners/Conditional/timed.yml",
    "Resources/Prototypes/GameRules/pests.yml",
    "Resources/Prototypes/InventoryTemplates/arachnid_inventory_template.yml",
    "Resources/Prototypes/InventoryTemplates/corpse_inventory_template.yml",
    "Resources/Prototypes/InventoryTemplates/human_inventory_template.yml",
    "Resources/Prototypes/_LuaM/Sector/rescue_after_action.yml",
    "Resources/Prototypes/_LuaM/Entities/World/sector_traffic.yml",
    "Resources/Prototypes/_NF/Entities/Mobs/NPCs/mob_hostile_rogue_ai.yml",
    "Resources/Prototypes/_NF/Loadouts/Jobs/Contractor/cartridge.yml",
    "Resources/Prototypes/_NF/Loadouts/contractor_loadout_groups.yml",
    "Resources/Prototypes/_NF/bounty_contract_collections.yml",
    "Resources/Prototypes/_Mono/Entities/Markers/Spawners/shuttles.yml",
    "Resources/Prototypes/_Mono/GameRules/timings.yml",
    "Resources/Prototypes/_Mono/Shipyard/triage.yml",
    "Resources/Prototypes/_Mono/game_presets.yml",
    "Resources/Prototypes/_Mono/lobbyscreens.yml",
    "Resources/Maps/_Mono/Shuttles/triage.yml",
    "Resources/Maps/_NF/Shuttles/Scrap/bison.yml",
    "Resources/Maps/_NF/Shuttles/barge.yml",
    "Resources/Maps/_NF/Shuttles/caladrius.yml",
    "Resources/Maps/_NF/Shuttles/Expedition/pathfinder.yml",
    "Resources/Maps/_NF/Shuttles/hammer.yml",
    "Resources/Maps/_NF/Shuttles/Nfsd/hospitaller.yml",
    "Resources/Maps/_NF/Shuttles/stasis.yml",
    "Resources/Maps/_NF/Shuttles/spirit.yml",
    "Resources/Maps/_NF/Shuttles/tyne.yml",
    "Resources/Prototypes/_NF/Shipyard/barge.yml",
    "Resources/Prototypes/_NF/Shipyard/caladrius.yml",
    "Resources/Prototypes/_NF/Shipyard/Expedition/pathfinder.yml",
    "Resources/Prototypes/_NF/Shipyard/hammer.yml",
    "Resources/Prototypes/_NF/Shipyard/Nfsd/hospitaller.yml",
    "Resources/Prototypes/_NF/Shipyard/stasis.yml",
    "Resources/Prototypes/_NF/Shipyard/spirit.yml",
    "Resources/Prototypes/_NF/Shipyard/tyne.yml",
    "Resources/ServerInfo/_NF/Guidebook/PreflightChecklist.xml",
    "Resources/ServerInfo/_NF/Guidebook/Shipyard/Spirit.xml",
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
    "Tools/archive_monolith_admin_logs.ps1",
    "Tools/deploy_luam_ai_gateway.ps1",
    "Tools/deploy_luam_server_release.ps1",
    "Tools/monolith-restart-when-empty.ps1",
    "Tools/monolith_release_runbook.md",
    "Tools/provision_monolith_client_static.ps1",
    "Tools/local_stack.md",
    "Tools/monolith_improvement_audit.md",
    "Tools/luam_admin_ranks.yml",
    "Tools/generate_luam_admin_rank_sql.py",
    "Tools/audit_luam_dependency_vulnerabilities.ps1",
    "Tools/luam_ai_gateway.py",
    "Tools/luam_ship_generator.py",
    "Tools/luam_openai_mcp_server.py",
    "Tools/summarize_luam_ai_audit.py",
    "Tools/luam_release_manifest.md",
    "Tools/luam_release_policy.json",
    "Tools/luam_character_progression_design.md",
    "Tools/luam_expedition_implementation_proposal.md",
    "Tools/luam_expedition_worldgen_design.md",
    "Tools/start_local_stack.ps1",
    "Tools/stop_local_stack.ps1",
    "Tools/setup_luam_piper_tts.ps1",
    "Tools/test_local_frontier.ps1",
    "Tools/test_local_stack.ps1",
    "Tools/test_luam_ai_gateway.py",
    "Tools/test_luam_ship_generator.py",
    "Tools/validate_luam_feature_pack.py",
    "DeploymentPackages/LuaM/luam-admin-ranks.sqlite.sql",
    "DeploymentPackages/LuaM/luam-admin-ranks.postgres.sql",
    "DeploymentPackages/LuaM/luam-admin-ranks.md"
)

$releaseFiles = @($releaseFiles + $policyRequiredFiles) | Sort-Object -Unique

$releaseScopes = @(
    "Content.Client/_LuaM",
    "Content.Client/_Mono/FireControl",
    "Content.Client/_Mono/Radar",
    "Content.Client/Shuttles/UI/ShuttleNavControl.xaml.cs",
    "Content.Client/_NF/Storage/Visualizers/ContainerCountVisualizerSystem.cs",
    "Content.Server/_LuaM",
    "Content.Server/_Mono/Projectiles/TargetSeeking",
    "Content.Server/_Mono/Radar",
    "Content.Server/Mech/Systems/MechSystem.cs",
    "Content.Server/PowerCell/PowerCellSystem.cs",
    "Content.Server/_NF/Fluids",
    "Content.Server/_NF/Mech",
    "Content.Server/_NF/Power",
    "Content.Server/_NF/Storage/EntitySystems/ContainerCountVisualizerSystem.cs",
    "Content.Shared/_LuaM",
    "Content.Shared/Fluids/SharedDrainSystem.cs",
    "Content.Shared/Mech/Components/MechComponent.cs",
    "Content.Shared/Mech/EntitySystems/SharedMechSystem.cs",
    "Content.Shared/_Mono/Radar",
    "Content.Shared/_NF/Cargo",
    "Content.Shared/_NF/Fluids",
    "Content.Shared/_NF/Mech",
    "Content.Shared/_NF/Species",
    "Content.Shared/_NF/Storage/Components/ContainerCountVisualizerComponent.cs",
    "Content.Shared/_NF/Whitelist",
    "Content.IntegrationTests/Tests/_LuaM",
    "Content.IntegrationTests/Tests/_NF/BountyContracts",
    "Content.IntegrationTests/Pair/TestPair.cs",
    "Content.IntegrationTests/PoolManager.Cvars.cs",
    "Content.IntegrationTests/Tests/Gateway/GatewayGeneratorGrowthLimitTest.cs",
    "Content.IntegrationTests/Tests/_LuaM/LuaMTimedSpawnerLimitTest.cs",
    "Content.IntegrationTests/Tests/Lobby/CharacterCreationTest.cs",
    "Content.IntegrationTests/Utility/GameDataScrounger.Files.cs",
    "Content.Tests/Client/_LuaM",
    "Content.Tests/Server/_LuaM",
    "Content.Tests/Shared/_NF/BountyContracts",
    "Content.Client/Administration/UI/Tabs/AdminTab/AdminTab.xaml",
    "Content.Client/Administration/UI/Tabs/AdminTab/AdminTab.xaml.cs",
    "Content.Client/Clothing/ClientClothingSystem.cs",
    "Content.Client/Lobby/LobbyState.cs",
    "Content.Client/Players/PlayTimeTracking/JobRequirementsManager.cs",
    "Content.Client/RoundEnd/RoundEndSummaryWindow.cs",
    "Content.Client/PDA",
    "Content.Client/Research/UI/ResearchConsoleMenu.xaml",
    "Content.Client/_Goobstation/Research/UI/FancyResearchConsoleItem.xaml",
    "Content.Client/_Goobstation/Research/UI/FancyResearchConsoleItem.xaml.cs",
    "Content.Client/_Goobstation/Research/UI/FancyResearchConsoleMenu.xaml",
    "Content.Client/_Goobstation/Research/UI/FancyResearchConsoleMenu.xaml.cs",
    "Content.Client/_NF/BountyContracts",
    "Content.Client/_NF/LateJoin",
    "Content.Client/_NF/Shipyard/BUI/ShipyardConsoleBoundUserInterface.cs",
    "Content.Client/_NF/Shipyard/UI/ShipyardConsoleMenu.xaml",
    "Content.Client/_NF/Shipyard/UI/ShipyardConsoleMenu.xaml.cs",
    "Content.Server/Access/Systems/IdCardConsoleSystem.cs",
    "Content.Server/Cargo/Systems/CargoSystem.Bounty.cs",
    "Content.Server/Cargo/Systems/CargoSystem.Orders.cs",
    "Content.Server/CartridgeLoader/CartridgeLoaderSystem.cs",
    "Content.Server/Chat/Systems/ChatSystem.cs",
    "Content.Server/Gateway/Components/GatewayGeneratorDestinationComponent.cs",
    "Content.Server/Gateway/Systems/GatewayGeneratorSystem.cs",
    "Content.Server/Nutrition/EntitySystems/AnimalHusbandrySystem.cs",
    "Content.Server/Spawners/Components/TimedSpawnerComponent.cs",
    "Content.Server/Spawners/EntitySystems/SpawnerSystem.cs",
    "Content.Server/StationEvents/Events/VentCrittersRule.cs",
    "Content.Server/PDA",
    "Content.Server/Pinpointer/NavMapSystem.cs",
    "Content.Server/Players/PlayTimeTracking/PlayTimeTrackingSystem.cs",
    "Content.Server/Radio/EntitySystems/HeadsetSystem.cs",
    "Content.Server/Shuttles/Systems/ShuttleSystem.FasterThanLight.cs",
    "Content.Server/Station/Systems/StationJobsSystem.cs",
    "Content.Server/Database/ServerDbBase.cs",
    "Content.Server/Database/ServerDbManager.cs",
    "Content.Server/Database/DatabaseRecords.LuaMShips.cs",
    "Content.Server/Preferences/Managers/IServerPreferencesManager.cs",
    "Content.Server/Preferences/Managers/ServerPreferencesManager.cs",
    "Content.Server.Database/Model.cs",
    "Content.Server.Database/Migrations/Postgres/20260713070624_PreserveCharacterProfiles.cs",
    "Content.Server.Database/Migrations/Postgres/20260713070624_PreserveCharacterProfiles.Designer.cs",
    "Content.Server.Database/Migrations/Postgres/20260713162540_DurablePdaBankTransfers.cs",
    "Content.Server.Database/Migrations/Postgres/20260713162540_DurablePdaBankTransfers.Designer.cs",
    "Content.Server.Database/Migrations/Postgres/20260720164309_LuaMShipPayloadRevision.cs",
    "Content.Server.Database/Migrations/Postgres/20260720164309_LuaMShipPayloadRevision.Designer.cs",
    "Content.Server.Database/Migrations/Postgres/20260727090000_LuaMShipFleetOwnerIndex.cs",
    "Content.Server.Database/Migrations/Postgres/PostgresServerDbContextModelSnapshot.cs",
    "Content.Server.Database/Migrations/Sqlite/20260713070603_PreserveCharacterProfiles.cs",
    "Content.Server.Database/Migrations/Sqlite/20260713070603_PreserveCharacterProfiles.Designer.cs",
    "Content.Server.Database/Migrations/Sqlite/20260713162532_DurablePdaBankTransfers.cs",
    "Content.Server.Database/Migrations/Sqlite/20260713162532_DurablePdaBankTransfers.Designer.cs",
    "Content.Server.Database/Migrations/Sqlite/20260720164259_LuaMShipPayloadRevision.cs",
    "Content.Server.Database/Migrations/Sqlite/20260720164259_LuaMShipPayloadRevision.Designer.cs",
    "Content.Server.Database/Migrations/Sqlite/20260727090000_LuaMShipFleetOwnerIndex.cs",
    "Content.Server.Database/Migrations/Sqlite/SqliteServerDbContextModelSnapshot.cs",
    "Content.Server/_CorvaxNext/Silicons/Borgs/AiRemoteControlSystem.cs",
    "Content.Shared/PDA",
    "Content.Shared/CCVar/CCVars.LuaM.cs",
    "Content.Shared/CCVar/CCVars.Misc.cs",
    "Content.Shared/Inventory/SlotFlags.cs",
    "Content.Shared/Nutrition/AnimalHusbandry/ReproductiveComponent.cs",
    "Content.Shared/Preferences/HumanoidCharacterProfile.cs",
    "Content.Shared/Roles/JobRequirements.cs",
    "Content.Shared/Roles/SharedRoleSystem.cs",
    "Content.Shared/_CorvaxNext/Silicons/Borgs/Components/SharedAiRemoteControllerComponent.cs",
    "Content.Shared/_NF/ShuttleRecords/ShuttleRecord.cs",
    "Content.Server/_NF/Bank",
    "Content.Server/_NF/Commands/BankCommand.cs",
    "Content.Server/_NF/ShuttleRecords/ShuttleRecordsSystem.Console.cs",
    "Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs",
    "Content.Server/_NF/Shipyard/Systems/ShipyardSystem.cs",
    "Content.Server/_NF/BountyContracts",
    "Content.Shared/_NF/Bank",
    "Content.Shared/_NF/BountyContracts",
    "Content.Shared/_NF/Shipyard/BUI/ShipyardConsoleInterfaceState.cs",
    "Content.Shared/_NF/Shipyard/Components/ShuttleDeedComponent.cs",
    "Content.Shared/_NF/Shipyard/Events/ShipyardConsoleParkMessage.cs",
    "Content.Shared/_NF/Shipyard/Events/ShipyardConsolePurchaseMessage.cs",
    "Resources/ConfigPresets/_Mono/monolithCore.toml",
    "Resources/ConfigPresets/_LuaM",
    "Resources/Audio/_NF/Mecha",
    "Resources/Locale/en-US/_Goobstation/research/ui.ftl",
    "Resources/Locale/en-US/_LuaM",
    "Resources/Locale/en-US/_Mono/gamerules/gamemodes.ftl",
    "Resources/Locale/en-US/_NF",
    "Resources/Locale/en-US/_NF/bank/bank-ATM-component.ftl",
    "Resources/Locale/en-US/_NF/bounty-contracts/bounty-contracts.ftl",
    "Resources/Locale/en-US/_NF/cartridge-loader/cartridges.ftl",
    "Resources/Locale/en-US/_NF/pda/pda-component.ftl",
    "Resources/Locale/en-US/_NF/shipyard/shipyard-console-component.ftl",
    "Resources/Locale/en-US/administration/ui/tabs/admin-tab/player-actions-window.ftl",
    "Resources/Locale/en-US/cargo/cargo-bounty-console.ftl",
    "Resources/Locale/en-US/holiday/greet/holiday-greet.ftl",
    "Resources/Locale/ru-RU/_Goobstation/research/ui.ftl",
    "Resources/Locale/ru-RU/_LuaM",
    "Resources/Locale/ru-RU/_Mono/gamerules/gamemodes.ftl",
    "Resources/Locale/ru-RU/_NF",
    "Resources/Locale/ru-RU/_NF/bank/bank-ATM-component.ftl",
    "Resources/Locale/ru-RU/_NF/bounty-contracts/bounty-contracts.ftl",
    "Resources/Locale/ru-RU/_NF/cartridge-loader/cartridges.ftl",
    "Resources/Locale/ru-RU/_NF/pda/pda-component.ftl",
    "Resources/Locale/ru-RU/_NF/shipyard/shipyard-console-component.ftl",
    "Resources/Locale/ru-RU/administration/ui/tabs/admin-tab/player-actions-window.ftl",
    "Resources/Locale/ru-RU/cargo/cargo-bounty-console.ftl",
    "Resources/Locale/ru-RU/holiday/greet/holiday-greet.ftl",
    "Resources/Locale/ru-RU/launcher/launcher-connecting.ftl",
    "Resources/Locale/ru-RU/ss14-ru/prototypes/_LuaM",
    "Resources/Locale/ru-RU/ss14-ru/prototypes/_nf/entities/objects/misc/paper.ftl",
    "Resources/Locale/ru-RU/ss14-ru/prototypes/catalog/fills/crates/engineering.ftl",
    "Resources/Prototypes/Entities/Mobs/Species/arachnid.yml",
    "Resources/Prototypes/Entities/Mobs/Species/base.yml",
    "Resources/Prototypes/Entities/Markers/Spawners/Conditional/timed.yml",
    "Resources/Prototypes/GameRules/pests.yml",
    "Resources/Prototypes/InventoryTemplates/arachnid_inventory_template.yml",
    "Resources/Prototypes/InventoryTemplates/corpse_inventory_template.yml",
    "Resources/Prototypes/InventoryTemplates/human_inventory_template.yml",
    "Resources/Prototypes/_LuaM",
    "Resources/Prototypes/_Mono",
    "Resources/Prototypes/_NF",
    "Resources/Prototypes/Catalog/Fills",
    "Resources/Prototypes/Entities/Objects/Specific/chemistry-bottles.yml",
    "Resources/Prototypes/Entities/Objects/Weapons/Guns/Shotguns/shotguns.yml",
    "Resources/Prototypes/Entities/Structures/Doors/Airlocks",
    "Resources/Prototypes/Entities/Structures/Storage/Closets/base_structureclosets.yml",
    "Resources/Prototypes/tags.yml",
    "Resources/Prototypes/_NF/Entities/Mobs/NPCs/mob_hostile_rogue_ai.yml",
    "Resources/Prototypes/_NF/Loadouts",
    "Resources/Prototypes/_NF/PointsOfInterest",
    "Resources/Prototypes/_NF/Roles/Jobs",
    "Resources/Prototypes/_NF/bounty_contract_collections.yml",
    "Resources/Prototypes/_Mono/Entities/Markers/Spawners/shuttles.yml",
    "Resources/Prototypes/_Mono/GameRules/timings.yml",
    "Resources/Prototypes/_Mono/Shipyard/triage.yml",
    "Resources/Prototypes/_Mono/Roles/Jobs",
    "Resources/Prototypes/_Mono/game_presets.yml",
    "Resources/Prototypes/_Mono/lobbyscreens.yml",
    "Resources/Maps/_Mono/Shuttles/triage.yml",
    "Resources/Maps/_LuaM",
    "Resources/Maps/_NF/Shuttles",
    "Resources/Maps/_NF/Shuttles/Scrap/bison.yml",
    "Resources/Changelog/Parts/luam-animal-population-cap.yml",
    "Resources/Changelog/Parts/luam-sector-traffic.yml",
    "Resources/Changelog/Parts",
    "Resources/Prototypes/holidays.yml",
    "Resources/ServerInfo/Intro.txt",
    "Resources/ServerInfo/_LuaM",
    "Resources/ServerInfo/_NF/Guidebook",
    "Resources/Textures/_LuaM",
    "Resources/Textures/_NF",
    "Resources/Textures/Structures/Doors/Airlocks/Glass/salvage.rsi",
    "Resources/Textures/Structures/Machines/holopad.rsi",
    "Resources/manifest.yml",
    "Directory.Packages.props",
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
    "Tools/archive_monolith_admin_logs.ps1",
    "Tools/deploy_luam_ai_gateway.ps1",
    "Tools/deploy_luam_server_release.ps1",
    "Tools/monolith-restart-when-empty.ps1",
    "Tools/monolith_release_runbook.md",
    "Tools/provision_monolith_client_static.ps1",
    "Tools/local_stack.md",
    "Tools/monolith_improvement_audit.md",
    "Tools/luam_admin_ranks.yml",
    "Tools/generate_luam_admin_rank_sql.py",
    "Tools/audit_luam_dependency_vulnerabilities.ps1",
    "Tools/luam_ai_gateway.py",
    "Tools/luam_ship_generator.py",
    "Tools/luam_openai_mcp_server.py",
    "Tools/summarize_luam_ai_audit.py",
    "Tools/luam_release_manifest.md",
    "Tools/luam_release_policy.json",
    "Tools/luam_character_progression_design.md",
    "Tools/luam_expedition_implementation_proposal.md",
    "Tools/luam_expedition_worldgen_design.md",
    "Tools/start_local_stack.ps1",
    "Tools/stop_local_stack.ps1",
    "Tools/setup_luam_piper_tts.ps1",
    "Tools/test_local_frontier.ps1",
    "Tools/test_local_stack.ps1",
    "Tools/test_luam_ai_gateway.py",
    "Tools/test_luam_ship_generator.py",
    "Tools/validate_luam_feature_pack.py"
)

$releaseScopes = @($releaseScopes + $policyPackageScopes) | Sort-Object -Unique

$allowedOutOfScopeChangedFiles = @(Get-LuaMReleaseApprovedOutsidePackageFiles -Policy $releasePolicy)

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
    if ((Test-LuaMReleaseExcludedLocalArtifact -Path $repoPath -ExcludedArtifacts $policyExcludedLocalArtifacts) -or
        $repoPath -match "(^|/)(bin|obj|\.vs|\.idea|\.vscode|node_modules|__pycache__|\.pytest_cache|logs?|tmp|temp)(/|$)|\.(log|tmp|bak|cache|db|sqlite|sqlite3)$") {
        return
    }

    $fullPath = Join-Path $root ($repoPath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
    if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
        $Set.Add($repoPath) | Out-Null
    }
}

function Invoke-GitCaptureForPathspecs {
    param(
        [Parameter(Mandatory = $true)][string[]]$BaseArguments,
        [Parameter(Mandatory = $true)][string[]]$Pathspecs
    )

    # Keep each CreateProcess command line comfortably below the Windows
    # limit. The production scope currently contains hundreds of pathspecs.
    $results = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $batch = [System.Collections.Generic.List[string]]::new()
    $batchCharacters = 0

    foreach ($pathspec in $Pathspecs) {
        $estimatedCharacters = $pathspec.Length + 3
        if ($batch.Count -gt 0 -and $batchCharacters + $estimatedCharacters -gt 12000) {
            foreach ($line in Invoke-LuaMGitCapture -Root $root -Arguments (@($BaseArguments) + '--' + @($batch))) {
                if (-not [string]::IsNullOrWhiteSpace($line)) {
                    $results.Add($line) | Out-Null
                }
            }
            $batch.Clear()
            $batchCharacters = 0
        }

        $batch.Add($pathspec)
        $batchCharacters += $estimatedCharacters
    }

    if ($batch.Count -gt 0) {
        foreach ($line in Invoke-LuaMGitCapture -Root $root -Arguments (@($BaseArguments) + '--' + @($batch))) {
            if (-not [string]::IsNullOrWhiteSpace($line)) {
                $results.Add($line) | Out-Null
            }
        }
    }

    return @($results) | Sort-Object
}

function Invoke-Readiness {
    $args = @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        "Tools\check_luam_release_ready.ps1",
        "-Json"
    )

    if ($RunTests) {
        $args += "-RunTests"
    }

    if ($RunLocalSmoke) {
        $args += "-RunLocalSmoke"
    }

    if ($AllowUntracked) {
        $args += "-AllowUntracked"
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
    $gitOutputs = @(
        @(Invoke-LuaMGitCapture -Root $root -Arguments @('-c', 'core.quotepath=false', 'diff', '--name-only', '--no-ext-diff'))
        @(Invoke-LuaMGitCapture -Root $root -Arguments @('-c', 'core.quotepath=false', 'diff', '--cached', '--name-only', '--no-ext-diff'))
        @(Invoke-LuaMGitCapture -Root $root -Arguments @('-c', 'core.quotepath=false', 'ls-files', '--others', '--exclude-standard'))
    )
    foreach ($file in $gitOutputs) {
        if ([string]::IsNullOrWhiteSpace($file)) {
            continue
        }

        $repoPath = Convert-ToRepoPath $file
        if (Test-LuaMReleaseExcludedLocalArtifact -Path $repoPath -ExcludedArtifacts $policyExcludedLocalArtifacts) {
            continue
        }

        $changed.Add($repoPath) | Out-Null
    }

    return @($changed) | Sort-Object
}

$staging = $null
Push-Location $root
try {
    # Generate policy-owned deterministic artifacts before taking the readiness/worktree receipt.
    Invoke-AdminRankSqlGeneration | Out-Null

    $readiness = $null
    if (-not $SkipReadiness) {
        $readiness = Invoke-Readiness
    }
    $sourceReceipt = if ($null -ne $readiness) {
        if ($null -eq $readiness.worktree) {
            throw "Readiness output is missing its worktree receipt."
        }
        $readiness.worktree
    } else {
        Get-LuaMWorktreeReceipt -Root $root -ExcludedArtifacts $policyExcludedLocalArtifacts
    }

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

    $changedInScope = @(
        @(Invoke-GitCaptureForPathspecs -BaseArguments @('-c', 'core.quotepath=false', 'diff', '--name-only', '--no-ext-diff') -Pathspecs $releaseScopes)
        @(Invoke-GitCaptureForPathspecs -BaseArguments @('-c', 'core.quotepath=false', 'diff', '--cached', '--name-only', '--no-ext-diff') -Pathspecs $releaseScopes)
        @(Invoke-GitCaptureForPathspecs -BaseArguments @('-c', 'core.quotepath=false', 'ls-files', '--others', '--exclude-standard') -Pathspecs $releaseScopes)
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique
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
        $sourceItem = Get-Item -LiteralPath $source -Force
        if (($sourceItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Release package refuses a reparse-point source file: $relative"
        }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Force

        $destinationItem = Get-Item -LiteralPath $destination -Force
        if (($destinationItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Release package staging unexpectedly contains a reparse point: $relative"
        }
        $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $destination
        $fileHashes.Add([pscustomobject]@{
            path = $relative
            sha256 = $hash.Hash.ToLowerInvariant()
            bytes = [int64]$destinationItem.Length
        }) | Out-Null
    }

    $finalWorktreeReceipt = Get-LuaMWorktreeReceipt -Root $root -ExcludedArtifacts $policyExcludedLocalArtifacts
    if (-not ([string]$sourceReceipt.digestSha256).Equals([string]$finalWorktreeReceipt.digestSha256, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not ([string]$sourceReceipt.gitHead).Equals([string]$finalWorktreeReceipt.gitHead, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Release worktree changed after readiness and before package manifest creation."
    }
    $finalPolicySha256 = (Get-FileHash -LiteralPath $releasePolicyPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $releasePolicySha256.Equals($finalPolicySha256, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Release policy changed during source package creation."
    }

    $gitHead = [string]$finalWorktreeReceipt.gitHead
    $gitBranch = [string]$finalWorktreeReceipt.gitBranch
    $untracked = @(Invoke-GitCaptureForPathspecs -BaseArguments @('-c', 'core.quotepath=false', 'ls-files', '--others', '--exclude-standard') -Pathspecs $releaseScopes)
    $fileHashArray = @($fileHashes | ForEach-Object { $_ })
    $payloadDigestSha256 = Get-LuaMFileRecordDigest -Files $fileHashArray
    $changedFiles = @(Get-ChangedFiles)
    $packagedChangedFiles = @($changedFiles | Where-Object { $files.Contains($_) })
    $changedFilesOutsidePackage = @($changedFiles | Where-Object { -not $files.Contains($_) })
    $allowedOutOfScope = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $allowedOutOfScopeChangedFiles) {
        $allowedOutOfScope.Add($file) | Out-Null
    }
    $unexpectedChangedFilesOutsidePackage = @(
        $changedFilesOutsidePackage |
            Where-Object { -not $allowedOutOfScope.Contains($_) }
    )
    if ($unexpectedChangedFilesOutsidePackage.Count -gt 0) {
        throw "Changed files outside package are not allowlisted: $($unexpectedChangedFilesOutsidePackage -join ', ')"
    }

    $allowedChangedFilesOutsidePackage = @(
        $changedFilesOutsidePackage |
            Where-Object { $allowedOutOfScope.Contains($_) }
    )

    $packageManifest = [pscustomobject]@{
        schemaVersion = 2
        name = $releaseName
        generatedAtUtc = [DateTime]::UtcNow.ToString("o")
        policySha256 = $releasePolicySha256
        payloadDigestSha256 = $payloadDigestSha256
        sourceReceipt = $finalWorktreeReceipt
        productionEligible = ($null -ne $readiness -and [bool]$readiness.productionEligible -and -not [bool]$AllowUntracked)
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
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    # ZIP entry names are always slash-delimited, regardless of the host OS.
    # Compress-Archive preserves Windows path separators, which makes an archive
    # fail the same verifier on Linux and even our own exact-entry checks.
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::Open(
        $zipPath,
        [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $stagingPrefix = $staging.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        $stagedFiles = Get-ChildItem -LiteralPath $staging -Recurse -File |
            Sort-Object @{ Expression = {
                $_.FullName.Substring($stagingPrefix.Length).Replace('\', '/')
            } }
        foreach ($stagedFile in $stagedFiles) {
            $entryName = $stagedFile.FullName.Substring($stagingPrefix.Length).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive,
                $stagedFile.FullName,
                $entryName,
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $archive.Dispose()
    }
    $zipHash = Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath

    Remove-Item -LiteralPath $staging -Recurse -Force

    $result = [pscustomobject]@{
        ok = $true
        package = $zipPath
        sha256 = $zipHash.Hash.ToLowerInvariant()
        policySha256 = $releasePolicySha256
        payloadDigestSha256 = $payloadDigestSha256
        worktreeDigestSha256 = [string]$finalWorktreeReceipt.digestSha256
        gitHead = [string]$finalWorktreeReceipt.gitHead
        productionEligible = [bool]$packageManifest.productionEligible
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
    if ($null -ne $staging -and (Test-Path -LiteralPath $staging)) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
    Pop-Location
}
