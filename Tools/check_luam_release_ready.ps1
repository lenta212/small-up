param(
    [switch]$AllowUntracked,
    [switch]$RunTests,
    [switch]$RunLocalSmoke,
    [switch]$Json
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "luam_release_contract.ps1")
$releasePolicyPath = Join-Path $root "Tools/luam_release_policy.json"
$releasePolicy = Read-LuaMReleasePolicy -Root $root
$releasePolicySha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $releasePolicyPath).Hash.ToLowerInvariant()
$policyRequiredFiles = @(Get-LuaMReleaseGateRequiredFiles -Policy $releasePolicy)
$policyPackageScopes = @(Get-LuaMReleaseGatePackageScopes -Policy $releasePolicy)
$policyExcludedLocalArtifacts = @(Get-LuaMReleaseExcludedLocalArtifacts -Policy $releasePolicy)
$initialWorktreeReceipt = Get-LuaMWorktreeReceipt -Root $root -ExcludedArtifacts $policyExcludedLocalArtifacts
$issues = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]
$steps = New-Object System.Collections.Generic.List[object]

$requiredFiles = @(
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
    "Content.Server/Preferences/Managers/IServerPreferencesManager.cs",
    "Content.Server/Preferences/Managers/ServerPreferencesManager.cs",
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
    "Content.Server/_LuaM/Administration/LuaMAnimalPopulationCommands.cs",
    "Content.Server/_LuaM/Animals/LuaMAnimalPopulationSystem.cs",
    "Content.Server/_LuaM/Expeditions/LuaMExpeditionPlan.cs",
    "Content.Server/_LuaM/Expeditions/LuaMExpeditionPlanCommand.cs",
    "Content.Server/_LuaM/Expeditions/LuaMExpeditionPlanValidator.cs",
    "Content.Server/_LuaM/Expeditions/LuaMExpeditionPlanner.cs",
    "Content.Server/_LuaM/Progression/LuaMCampaignShiftClock.cs",
    "Content.Server/_LuaM/Progression/LuaMCareerProgressionRules.cs",
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
    "Content.IntegrationTests/Tests/_LuaM/LuaMShipyardPurchaseDurabilityContractTest.cs",
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
    "Resources/Changelog/Parts/luam-animal-population-cap.yml",
    "Resources/Changelog/Parts/luam-sector-traffic.yml",
    "Resources/Changelog/Parts/luam-expedition-persistence-foundations.yml",
    "Resources/Changelog/Parts/luam-pda-bank-transfer-fix.yml",
    "Resources/Changelog/Parts/luam-ship-generator.yml",
    "Content.Shared/_CorvaxNext/Silicons/Borgs/Components/SharedAiRemoteControllerComponent.cs",
    "Content.Shared/_NF/BountyContracts/SharedBountyContractSystem.cs",
    "Content.Shared/_NF/Shipyard/BUI/ShipyardConsoleInterfaceState.cs",
    "Content.Shared/_NF/Shipyard/Components/ShuttleDeedComponent.cs",
    "Content.Shared/_NF/Shipyard/Events/ShipyardConsoleParkMessage.cs",
    "Content.Shared/_NF/Shipyard/Events/ShipyardConsolePurchaseMessage.cs",
    "Content.Shared/_NF/ShuttleRecords/ShuttleRecord.cs",
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
    "Tools/local_stack.md",
    "Tools/monolith_improvement_audit.md",
    "Tools/luam_admin_ranks.yml",
    "Tools/generate_luam_admin_rank_sql.py",
    "Tools/audit_release_surface.ps1",
    "Tools/audit_luam_dependency_vulnerabilities.ps1",
    "Tools/archive_monolith_admin_logs.ps1",
    "Tools/deploy_luam_ai_gateway.ps1",
    "Tools/deploy_luam_server_release.ps1",
    "Tools/monolith-restart-when-empty.ps1",
    "Tools/monolith_release_runbook.md",
    "Tools/provision_monolith_client_static.ps1",
    "Tools/build_luam_release_package.ps1",
    "Tools/build_luam_server_release.ps1",
    "Tools/verify_luam_release_package.ps1",
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

$requiredFiles = @($requiredFiles + $policyRequiredFiles) | Sort-Object -Unique

$requiredDirectories = @(
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

$releaseScopes = @(
    "Content.Client/_LuaM",
    "Content.Server/_LuaM",
    "Content.Shared/_LuaM",
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
    "Content.Server/Preferences/Managers/IServerPreferencesManager.cs",
    "Content.Server/Preferences/Managers/ServerPreferencesManager.cs",
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
    "Resources/Locale/en-US/_Goobstation/research/ui.ftl",
    "Resources/Locale/en-US/_LuaM",
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
    "Resources/Locale/ru-RU/_LuaM",
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
    "Resources/Prototypes/_LuaM",
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
    "Resources/Maps/_NF/Shuttles/Scrap/bison.yml",
    "Resources/Changelog/Parts/luam-animal-population-cap.yml",
    "Resources/Changelog/Parts/luam-sector-traffic.yml",
    "Resources/Changelog/Parts",
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
    "Tools/local_stack.md",
    "Tools/monolith_improvement_audit.md",
    "Tools/luam_admin_ranks.yml",
    "Tools/generate_luam_admin_rank_sql.py",
    "Tools/audit_release_surface.ps1",
    "Tools/audit_luam_dependency_vulnerabilities.ps1",
    "Tools/archive_monolith_admin_logs.ps1",
    "Tools/deploy_luam_ai_gateway.ps1",
    "Tools/deploy_luam_server_release.ps1",
    "Tools/monolith-restart-when-empty.ps1",
    "Tools/monolith_release_runbook.md",
    "Tools/provision_monolith_client_static.ps1",
    "Tools/build_luam_release_package.ps1",
    "Tools/build_luam_server_release.ps1",
    "Tools/verify_luam_release_package.ps1",
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

function Invoke-Captured {
    param(
        [string]$FilePath,
        [string[]]$Arguments
    )

    $oldErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & $FilePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
        return [pscustomobject]@{
            ExitCode = $exitCode
            Output = @($output)
        }
    }
    finally {
        $ErrorActionPreference = $oldErrorActionPreference
    }
}

function Format-CapturedTail {
    param(
        [pscustomobject]$Result,
        [int]$MaxLines = 80
    )

    $lines = @(
        $Result.Output |
            ForEach-Object { [string] $_ } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )

    if ($lines.Count -eq 0) {
        return "no output captured"
    }

    if ($lines.Count -gt $MaxLines) {
        $lines = @($lines | Select-Object -Last $MaxLines)
    }

    return $lines -join "; "
}

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

Push-Location $root
try {
    foreach ($file in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $root $file))) {
            $issues.Add("Missing required release file: $file") | Out-Null
        }
    }

    foreach ($directory in $requiredDirectories) {
        $path = Join-Path $root $directory
        if (-not (Test-Path -LiteralPath $path)) {
            $issues.Add("Missing required release directory: $directory") | Out-Null
            continue
        }

        $fileCount = @(Get-ChildItem -LiteralPath $path -Recurse -File -ErrorAction Stop).Count
        if ($fileCount -le 0) {
            $issues.Add("Required release directory is empty: $directory") | Out-Null
        }
    }
    Add-Step "required-files" "checked" "Required files and directories exist."

    $untracked = @(& git ls-files --others --exclude-standard -- @releaseScopes)
    if ($untracked.Count -gt 0) {
        $message = "Release scope contains $($untracked.Count) untracked file(s). Add/package them before a git-based release. Files: $($untracked -join ', ')"
        if ($AllowUntracked) {
            $warnings.Add($message) | Out-Null
            Add-Step "untracked-release-files" "warning" $message
        } else {
            $issues.Add($message) | Out-Null
            Add-Step "untracked-release-files" "failed" $message
        }
    } else {
        Add-Step "untracked-release-files" "passed" "No untracked files in release scope."
    }

    $junk = @(
        Invoke-LuaMGitCapture -Root $root -Arguments @('-c', 'core.quotepath=false', 'ls-files', '--others', '--exclude-standard') |
            Where-Object {
                -not (Test-LuaMReleaseExcludedLocalArtifact -Path ([string]$_) -ExcludedArtifacts $policyExcludedLocalArtifacts) -and
                ([string]$_ -match "(^|/)(bin|obj|\.vs|\.idea|\.vscode|node_modules|__pycache__|\.pytest_cache|logs?|tmp|temp)(/|$)|\.(log|tmp|bak|cache|db|sqlite|sqlite3)$")
            }
    )

    if ($junk.Count -gt 0) {
        $issues.Add("Untracked local junk detected: $($junk -join ', ')") | Out-Null
        Add-Step "local-junk" "failed" "$($junk.Count) junk path(s) detected."
    } else {
        Add-Step "local-junk" "passed" "No untracked bin/obj/log/tmp/cache/db paths detected."
    }

    $textExtensions = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($extension in @(".cs", ".xaml", ".yml", ".yaml", ".ftl", ".toml", ".md", ".py", ".ps1", ".txt", ".xml", ".json", ".jsonc", ".cfg", ".config")) {
        $textExtensions.Add($extension) | Out-Null
    }

    $utf8Candidates = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $requiredFiles) {
        $utf8Candidates.Add((Convert-ToRepoPath $file)) | Out-Null
    }

    foreach ($directory in $requiredDirectories) {
        $path = Join-Path $root $directory
        if (-not (Test-Path -LiteralPath $path -PathType Container)) {
            continue
        }

        foreach ($file in Get-ChildItem -LiteralPath $path -Recurse -File) {
            $utf8Candidates.Add((Get-RelativePath -BasePath $root -FullPath $file.FullName)) | Out-Null
        }
    }

    foreach ($file in @(& git ls-files --modified --others --exclude-standard -- @releaseScopes)) {
        $utf8Candidates.Add((Convert-ToRepoPath $file)) | Out-Null
    }

    $strictUtf8 = [System.Text.UTF8Encoding]::new($false, $true)
    $invalidUtf8 = New-Object System.Collections.Generic.List[string]
    foreach ($relative in @($utf8Candidates) | Sort-Object) {
        $fullPath = Join-Path $root ($relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            continue
        }

        if (-not $textExtensions.Contains([System.IO.Path]::GetExtension($fullPath))) {
            continue
        }

        try {
            [void] $strictUtf8.GetString([System.IO.File]::ReadAllBytes($fullPath))
        }
        catch {
            $invalidUtf8.Add($relative) | Out-Null
        }
    }

    if ($invalidUtf8.Count -gt 0) {
        $issues.Add("Release text files are not strict UTF-8: $($invalidUtf8 -join ', ')") | Out-Null
        Add-Step "utf8-release-text" "failed" "$($invalidUtf8.Count) invalid UTF-8 file(s)."
    } else {
        Add-Step "utf8-release-text" "passed" "Release text files are strict UTF-8."
    }

    $staleInputSearch = Invoke-Captured -FilePath "rg" -Arguments @(
        "-n",
        "LuaMSectorStatusCartridgeMessages|luam-sector-status-ai",
        "Content.Server/_LuaM",
        "Content.Client/_LuaM",
        "Content.Shared/_LuaM",
        "Resources/Locale"
    )
    if ($staleInputSearch.ExitCode -eq 0) {
        $issues.Add("Stale PDA AI message input symbols remain: $($staleInputSearch.Output -join '; ')") | Out-Null
        Add-Step "pda-ai-input-removed" "failed" "Stale symbols found."
    } elseif ($staleInputSearch.ExitCode -eq 1) {
        Add-Step "pda-ai-input-removed" "passed" "No stale PDA AI input symbols found."
    } else {
        $issues.Add("Failed to search for stale PDA AI input symbols.") | Out-Null
        Add-Step "pda-ai-input-removed" "failed" "rg returned exit code $($staleInputSearch.ExitCode)."
    }

    $diffCheck = Invoke-Captured -FilePath "git" -Arguments @("diff", "--check", "--") + $releaseScopes
    if ($diffCheck.ExitCode -ne 0) {
        $issues.Add("git diff --check failed: $($diffCheck.Output -join '; ')") | Out-Null
        Add-Step "diff-check" "failed" "git diff --check returned $($diffCheck.ExitCode)."
    } else {
        Add-Step "diff-check" "passed" "No whitespace errors in tracked release diff."
    }

    $cachedDiffCheck = Invoke-Captured -FilePath "git" -Arguments @("diff", "--cached", "--check", "--") + $releaseScopes
    if ($cachedDiffCheck.ExitCode -ne 0) {
        $issues.Add("git diff --cached --check failed: $($cachedDiffCheck.Output -join '; ')") | Out-Null
        Add-Step "cached-diff-check" "failed" "git diff --cached --check returned $($cachedDiffCheck.ExitCode)."
    } else {
        Add-Step "cached-diff-check" "passed" "No whitespace errors in staged release diff."
    }

    $roundLengthFiles = @(
        "Resources/ConfigPresets/_Mono/monolithCore.toml",
        "Resources/ConfigPresets/_LuaM/deadSpaceLowPop.toml",
        "server_config.remote.toml"
    )
    if (Test-Path -LiteralPath (Join-Path $root "server_config_local.toml") -PathType Leaf) {
        $roundLengthFiles += "server_config_local.toml"
    }
    $roundLengthFailures = New-Object System.Collections.Generic.List[string]
    foreach ($roundLengthFile in $roundLengthFiles) {
        $roundLengthPath = Join-Path $root ($roundLengthFile.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        if (-not (Test-Path -LiteralPath $roundLengthPath -PathType Leaf)) {
            $roundLengthFailures.Add("$roundLengthFile missing") | Out-Null
            continue
        }

        $roundLengthText = Get-Content -LiteralPath $roundLengthPath -Raw
        if ($roundLengthText -notmatch "(?m)^\s*auto_call_time\s*=\s*10080\s*(#.*)?$") {
            $roundLengthFailures.Add("$roundLengthFile must keep shuttle.auto_call_time = 10080 for 7-day rounds") | Out-Null
        }
    }

    $gamePresetFile = "Resources/Prototypes/_Mono/game_presets.yml"
    $gamePresetPath = Join-Path $root ($gamePresetFile.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
    if (-not (Test-Path -LiteralPath $gamePresetPath -PathType Leaf)) {
        $roundLengthFailures.Add("$gamePresetFile missing") | Out-Null
    } else {
        $gamePresetText = Get-Content -LiteralPath $gamePresetPath -Raw
        $apocalypsePreset = [regex]::Match(
            $gamePresetText,
            "(?ms)^\s*id:\s*MonoAllAtOnce\s*(#.*)?$.*?(?=^\s*-\s*type:\s*gamePreset\s*(#.*)?$|\z)"
        )
        if (-not $apocalypsePreset.Success -or
            $apocalypsePreset.Value -match "(?m)^\s*-\s*RoundEndRule\S*\s*(#.*)?$") {
            $roundLengthFailures.Add("$gamePresetFile MonoAllAtOnce must rely on shuttle.auto_call_time and must not override it with a RoundEndRule") | Out-Null
        }
    }

    if ($roundLengthFailures.Count -gt 0) {
        $issues.Add("LuaM 7-day round length check failed: $($roundLengthFailures -join '; ')") | Out-Null
        Add-Step "round-length-seven-days" "failed" "$($roundLengthFailures.Count) config issue(s)."
    } else {
        Add-Step "round-length-seven-days" "passed" "Apocalypse and fallback rely on the 10080-minute automatic round limit without a preset override."
    }

    $powerShellExe = (Get-Process -Id $PID).Path
    if ([string]::IsNullOrWhiteSpace($powerShellExe)) {
        $powerShellExe = "pwsh"
    }

    $releaseContractTest = Invoke-Captured -FilePath $powerShellExe -Arguments @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        (Join-Path $PSScriptRoot "test_luam_release_contract.ps1"),
        "-Json"
    )
    if ($releaseContractTest.ExitCode -ne 0) {
        $issues.Add("LuaM release contract test failed: $(Format-CapturedTail $releaseContractTest)") | Out-Null
        Add-Step "release-contract" "failed" "Static release contract test returned $($releaseContractTest.ExitCode)."
    } else {
        Add-Step "release-contract" "passed" "Policy schema, required files, production tests, smoke checks, and freeze state are consistent."
    }

    $dependencyAuditScript = Join-Path $PSScriptRoot "audit_luam_dependency_vulnerabilities.ps1"
    $dependencyAudit = Invoke-Captured -FilePath $powerShellExe -Arguments @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        $dependencyAuditScript,
        "-Json"
    )
    $dependencyAuditJson = $dependencyAudit.Output -join [Environment]::NewLine
    if ($dependencyAudit.ExitCode -ne 0) {
        $issues.Add("Dependency vulnerability audit failed: $(Format-CapturedTail $dependencyAudit)") | Out-Null
        Add-Step "dependency-vulnerability-audit" "failed" "Dependency audit script returned $($dependencyAudit.ExitCode)."
    } else {
        $dependencyAuditResult = $dependencyAuditJson | ConvertFrom-Json
        Add-Step "dependency-vulnerability-audit" "passed" "No vulnerable packages found across $($dependencyAuditResult.projectCount) release-critical project(s)."
    }

    $validator = Invoke-Captured -FilePath "python" -Arguments @("Tools\validate_luam_feature_pack.py")
    if ($validator.ExitCode -ne 0) {
        $issues.Add("LuaM feature validator failed: $($validator.Output -join '; ')") | Out-Null
        Add-Step "feature-validator" "failed" "Validator returned $($validator.ExitCode)."
    } else {
        Add-Step "feature-validator" "passed" "LuaM feature validator passed."
    }

    $adminRankCheck = Invoke-Captured -FilePath "python" -Arguments @("Tools\generate_luam_admin_rank_sql.py", "--check-only", "--json")
    if ($adminRankCheck.ExitCode -ne 0) {
        $issues.Add("LuaM admin rank ladder check failed: $($adminRankCheck.Output -join '; ')") | Out-Null
        Add-Step "admin-rank-ladder" "failed" "Rank generator returned $($adminRankCheck.ExitCode)."
    } else {
        Add-Step "admin-rank-ladder" "passed" "LuaM admin rank ladder validated."
    }

    if ($RunTests) {
        foreach ($test in @($releasePolicy.releaseGate.productionTests)) {
            $testName = [string] $test.name
            if ([string] $test.runner -ne "dotnet") {
                $issues.Add("Production test '$testName' has unsupported runner '$($test.runner)'.") | Out-Null
                Add-Step $testName "failed" "Unsupported production test runner."
                continue
            }

            $testArguments = @(
                "test",
                [string] $test.project,
                "--filter",
                [string] $test.filter
            ) + @($test.arguments | ForEach-Object { [string] $_ })
            $testResult = Invoke-Captured -FilePath "dotnet" -Arguments $testArguments
            $attempts = 1
            if ($testResult.ExitCode -ne 0) {
                $attempts++
                $retryResult = Invoke-Captured -FilePath "dotnet" -Arguments $testArguments
                if ($retryResult.ExitCode -eq 0) {
                    $testResult = $retryResult
                }
            }

            if ($testResult.ExitCode -ne 0) {
                $issues.Add("Production test '$testName' failed: $(Format-CapturedTail $testResult)") | Out-Null
                Add-Step $testName "failed" "dotnet test returned $($testResult.ExitCode) after $attempts attempt(s)."
            } else {
                $detail = if ($attempts -gt 1) {
                    "Policy production test filter passed on retry $attempts."
                } else {
                    "Policy production test filter passed."
                }
                Add-Step $testName "passed" $detail
            }
        }
    } else {
        foreach ($test in @($releasePolicy.releaseGate.productionTests)) {
            Add-Step ([string] $test.name) "skipped" "Use -RunTests to run this policy production test."
        }
    }

    foreach ($smoke in @($releasePolicy.releaseGate.smokeChecks)) {
        $smokeName = [string] $smoke.name
        $smokeMode = [string] $smoke.mode
        if ($smokeMode -eq "local" -and -not $RunLocalSmoke) {
            Add-Step $smokeName "skipped" "Use -RunLocalSmoke to run this policy smoke check."
            continue
        }

        if ($smokeMode -notin @("always", "local")) {
            $issues.Add("Smoke check '$smokeName' has unsupported mode '$smokeMode'.") | Out-Null
            Add-Step $smokeName "failed" "Unsupported smoke mode."
            continue
        }

        $smokeArguments = @($smoke.arguments | ForEach-Object { [string] $_ })
        $smokeSupported = $true
        switch ([string] $smoke.runner) {
            "python" {
                $smokeFilePath = "python"
                $smokeArguments = @([string] $smoke.script) + $smokeArguments
            }
            "powershell" {
                $smokeFilePath = $powerShellExe
                $smokeArguments = @(
                    "-NoProfile",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-File",
                    [string] $smoke.script
                ) + $smokeArguments
            }
            default {
                $issues.Add("Smoke check '$smokeName' has unsupported runner '$($smoke.runner)'.") | Out-Null
                Add-Step $smokeName "failed" "Unsupported smoke runner."
                $smokeSupported = $false
            }
        }

        if (-not $smokeSupported) {
            continue
        }

        $smokeResult = Invoke-Captured -FilePath $smokeFilePath -Arguments $smokeArguments
        if ($smokeResult.ExitCode -ne 0) {
            $issues.Add("Smoke check '$smokeName' failed: $(Format-CapturedTail $smokeResult)") | Out-Null
            Add-Step $smokeName "failed" "Smoke check returned $($smokeResult.ExitCode)."
        } else {
            Add-Step $smokeName "passed" "Policy smoke check passed."
        }
    }
}
finally {
    Pop-Location
}

$finalWorktreeReceipt = Get-LuaMWorktreeReceipt -Root $root -ExcludedArtifacts $policyExcludedLocalArtifacts
$finalPolicySha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $releasePolicyPath).Hash.ToLowerInvariant()
if (-not $initialWorktreeReceipt.digestSha256.Equals($finalWorktreeReceipt.digestSha256, [StringComparison]::OrdinalIgnoreCase) -or
    -not $initialWorktreeReceipt.gitHead.Equals($finalWorktreeReceipt.gitHead, [StringComparison]::OrdinalIgnoreCase)) {
    $issues.Add("Release worktree changed while readiness was running. Initial $($initialWorktreeReceipt.digestSha256), final $($finalWorktreeReceipt.digestSha256).") | Out-Null
    Add-Step "worktree-stability" "failed" "Release-relevant Git state changed during readiness."
} else {
    Add-Step "worktree-stability" "passed" "Worktree receipt $($finalWorktreeReceipt.digestSha256) remained stable."
}
if (-not $releasePolicySha256.Equals($finalPolicySha256, [StringComparison]::OrdinalIgnoreCase)) {
    $issues.Add("Release policy changed while readiness was running.") | Out-Null
    Add-Step "policy-stability" "failed" "Policy SHA256 changed during readiness."
} else {
    Add-Step "policy-stability" "passed" "Policy SHA256 $finalPolicySha256 remained stable."
}

if (-not $finalWorktreeReceipt.trackedForProduction) {
    $trackedMessage = "Production worktree receipt contains $($finalWorktreeReceipt.untrackedFileCount) untracked file(s)."
    if ($AllowUntracked) {
        $warnings.Add("$trackedMessage This run is local evidence only.") | Out-Null
        Add-Step "tracked-worktree" "warning" "$trackedMessage Local evidence only."
    }
    else {
        $issues.Add($trackedMessage) | Out-Null
        Add-Step "tracked-worktree" "failed" $trackedMessage
    }
}
else {
    Add-Step "tracked-worktree" "passed" "Worktree receipt contains no untracked files."
}

$requiredProductionTests = @($releasePolicy.releaseGate.productionTests | Where-Object { $_.requiredForProduction -eq $true })
$requiredLocalSmokeChecks = @($releasePolicy.releaseGate.smokeChecks | Where-Object { $_.requiredForProduction -eq $true -and $_.mode -eq 'local' })
$requiredEvidenceRequested = ($requiredProductionTests.Count -eq 0 -or [bool]$RunTests) -and
    ($requiredLocalSmokeChecks.Count -eq 0 -or [bool]$RunLocalSmoke)

$productionEligible = $issues.Count -eq 0 -and
    -not [bool]$AllowUntracked -and
    [bool]$finalWorktreeReceipt.trackedForProduction -and
    $requiredEvidenceRequested

$result = [pscustomobject]@{
    ok = $issues.Count -eq 0
    productionEligible = $productionEligible
    allowUntracked = [bool] $AllowUntracked
    runTests = [bool] $RunTests
    runLocalSmoke = [bool] $RunLocalSmoke
    releaseGate = [pscustomobject]@{
        schemaVersion = [int] $releasePolicy.releaseGate.schemaVersion
        policySha256 = $releasePolicySha256
        requiredFileCount = $policyRequiredFiles.Count
        productionTests = @($releasePolicy.releaseGate.productionTests | ForEach-Object { [string] $_.name })
        smokeChecks = @($releasePolicy.releaseGate.smokeChecks | ForEach-Object { [string] $_.name })
    }
    worktree = $finalWorktreeReceipt
    issues = @($issues.ToArray())
    warnings = @($warnings.ToArray())
    steps = @($steps.ToArray())
}

if ($Json) {
    $result | ConvertTo-Json -Depth 8
} else {
    $status = if ($result.ok) { "OK" } else { "FAILED" }
    Write-Host "LuaM release readiness: $status"
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
