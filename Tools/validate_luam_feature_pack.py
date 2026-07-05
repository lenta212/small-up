from __future__ import annotations

import json
import sys
import tomllib
from pathlib import Path
from typing import Any

import yaml


ROOT = Path(__file__).resolve().parents[1]


class TaggedLoader(yaml.SafeLoader):
    pass


def construct_unknown(loader: TaggedLoader, node: yaml.Node) -> Any:
    if isinstance(node, yaml.MappingNode):
        value = loader.construct_mapping(node, deep=True)
    elif isinstance(node, yaml.SequenceNode):
        value = loader.construct_sequence(node, deep=True)
    else:
        value = loader.construct_scalar(node)

    if isinstance(value, dict):
        value["__tag__"] = node.tag
        return value

    return {"__tag__": node.tag, "value": value}


TaggedLoader.add_constructor(None, construct_unknown)


def load_yaml(path: Path) -> list[dict[str, Any]]:
    with path.open("r", encoding="utf-8") as handle:
        data = yaml.load(handle, Loader=TaggedLoader)

    if data is None:
        return []
    if isinstance(data, list):
        return [item for item in data if isinstance(item, dict)]
    if isinstance(data, dict):
        return [data]

    raise AssertionError(f"{path}: unexpected YAML root {type(data).__name__}")


def load_ftl_keys(path: Path) -> set[str]:
    keys: set[str] = set()
    for line in path.read_text(encoding="utf-8").splitlines():
        stripped = line.strip()
        if not stripped or stripped.startswith("#") or "=" not in stripped:
            continue
        keys.add(stripped.split("=", 1)[0].strip())
    return keys


def relevant_prototype_paths() -> list[Path]:
    roots = [
        ROOT / "Resources/Prototypes/_LuaM",
        ROOT / "Resources/Prototypes/_NF/Loadouts",
        ROOT / "Resources/Prototypes/_NF/PointsOfInterest",
    ]
    paths: list[Path] = []
    for root in roots:
        paths.extend(root.rglob("*.yml"))

    paths.append(ROOT / "Resources/Prototypes/_NF/bounty_contract_collections.yml")
    paths.append(ROOT / "Resources/Prototypes/_NF/Roles/Jobs/Civilian/contractor.yml")
    paths.append(ROOT / "Resources/Prototypes/_Mono/lobbyscreens.yml")
    paths.append(ROOT / "Resources/Prototypes/_Mono/Shipyard/triage.yml")
    return sorted(set(paths))


def collect_prototypes() -> tuple[dict[str, dict[str, Any]], list[dict[str, Any]]]:
    prototypes: dict[str, dict[str, Any]] = {}
    all_prototypes: list[dict[str, Any]] = []
    for path in relevant_prototype_paths():
        for proto in load_yaml(path):
            all_prototypes.append(proto)
            proto_id = proto.get("id")
            if isinstance(proto_id, str):
                prototypes[proto_id] = proto
    return prototypes, all_prototypes


def collect_job_metadata() -> tuple[set[str], set[str]]:
    job_roots = [
        ROOT / "Resources/Prototypes/Roles/Jobs",
        ROOT / "Resources/Prototypes/_NF/Roles/Jobs",
        ROOT / "Resources/Prototypes/_Mono/Roles/Jobs",
        ROOT / "Resources/Prototypes/Nyanotrasen/Roles/Jobs",
        ROOT / "Resources/Prototypes/_Goobstation/Roles/Jobs",
    ]
    job_ids: set[str] = set()
    play_time_trackers: set[str] = set()

    for root in job_roots:
        if not root.exists():
            continue

        for path in root.rglob("*.yml"):
            for proto in load_yaml(path):
                if proto.get("type") != "job":
                    continue

                proto_id = proto.get("id")
                if isinstance(proto_id, str):
                    job_ids.add(proto_id)

                play_time_tracker = proto.get("playTimeTracker")
                if isinstance(play_time_tracker, str):
                    play_time_trackers.add(play_time_tracker)

    return job_ids, play_time_trackers


def component(proto: dict[str, Any], component_type: str) -> dict[str, Any]:
    for comp in proto.get("components", []):
        if isinstance(comp, dict) and comp.get("type") == component_type:
            return comp
    raise AssertionError(f"{proto.get('id')}: missing component {component_type}")


def assert_equal(actual: Any, expected: Any, label: str) -> None:
    if actual != expected:
        raise AssertionError(f"{label}: expected {expected!r}, got {actual!r}")


def assert_contains(container: Any, value: Any, label: str) -> None:
    if value not in container:
        raise AssertionError(f"{label}: missing {value!r}")


def assert_not_contains(container: Any, value: Any, label: str) -> None:
    if value in container:
        raise AssertionError(f"{label}: unexpected {value!r}")


def assert_has_cyrillic(value: Any, label: str) -> None:
    if not isinstance(value, str) or not any("\u0400" <= char <= "\u04FF" for char in value):
        raise AssertionError(f"{label}: expected Russian text, got {value!r}")


def assert_luam_direct_descriptions_russian() -> None:
    for path in (ROOT / "Resources/Prototypes/_LuaM").rglob("*.yml"):
        for line_number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
            stripped = line.strip()
            if not stripped.startswith("description:"):
                continue

            value = stripped.split(":", 1)[1].strip().strip('"')
            if not value or value.startswith("luam-"):
                continue

            if not any("\u0400" <= char <= "\u04FF" for char in value):
                rel_path = path.relative_to(ROOT)
                raise AssertionError(f"{rel_path}:{line_number}: direct description must be Russian or a LuaM locale key")


def load_rsi_state_names(sprite_path: str) -> set[str]:
    meta_path = ROOT / "Resources/Textures" / Path(sprite_path) / "meta.json"
    if not meta_path.exists():
        raise AssertionError(f"{sprite_path}: missing meta.json")

    with meta_path.open("r", encoding="utf-8") as handle:
        meta = json.load(handle)

    return {
        state["name"]
        for state in meta.get("states", [])
        if isinstance(state, dict) and isinstance(state.get("name"), str)
    }


def main() -> int:
    required_paths = [
        "Resources/Prototypes/_NF/bounty_contract_collections.yml",
        "Resources/Prototypes/_LuaM/Guidebook/dead_space.yml",
        "Resources/Prototypes/_LuaM/Datasets/sector_rumors.yml",
        "Resources/Prototypes/_LuaM/Entities/Objects/Misc/sector_rumor_papers.yml",
        "Resources/Prototypes/_LuaM/Entities/Objects/Devices/cartridges.yml",
        "Resources/Prototypes/_LuaM/Entities/Objects/Monolith/artifacts.yml",
        "Resources/Prototypes/_LuaM/Entities/Structures/Machines/computers.yml",
        "Resources/Prototypes/_LuaM/Loadouts/Jobs/Contractor/bureaucracy.yml",
        "Resources/Prototypes/_NF/Loadouts/Jobs/Contractor/cartridge.yml",
        "Resources/Prototypes/_NF/Loadouts/contractor_loadout_groups.yml",
        "Resources/Prototypes/_LuaM/game_presets.yml",
        "Resources/Prototypes/_LuaM/GameRules/solo_schedulers.yml",
        "Resources/Prototypes/_LuaM/lobbyscreens.yml",
        "Resources/Prototypes/_Mono/lobbyscreens.yml",
        "Resources/Prototypes/holidays.yml",
        "Resources/manifest.yml",
        "Resources/Prototypes/_LuaM/Sector/sector_stories.yml",
        "Resources/Prototypes/_LuaM/SectorServices/services.yml",
        "Resources/Textures/_LuaM/LobbyScreens/frontier_monolith_loading.png",
        "Resources/Textures/_LuaM/LobbyScreens/frontier_monolith_loading.png.yml",
        "Resources/Textures/_LuaM/LobbyScreens/frontier_monolith_launcher_loading.png",
        "Resources/Textures/_LuaM/LobbyScreens/frontier_monolith_launcher_loading.png.yml",
        "Resources/Textures/_LuaM/LobbyScreens/attributions.yml",
        "Resources/Textures/_LuaM/Objects/Monolith/monolith_shard.rsi/meta.json",
        "Resources/Textures/_LuaM/Objects/Monolith/monolith_shard.rsi/icon.png",
        "Resources/Textures/_LuaM/Objects/Monolith/artifact_container.rsi/meta.json",
        "Resources/Textures/_LuaM/Objects/Monolith/artifact_container.rsi/icon.png",
        "Resources/Textures/_LuaM/Objects/Monolith/anomaly_scanner.rsi/meta.json",
        "Resources/Textures/_LuaM/Objects/Monolith/anomaly_scanner.rsi/icon.png",
        "Resources/Textures/_LuaM/Objects/Monolith/monolith_resonator.rsi/meta.json",
        "Resources/Textures/_LuaM/Objects/Monolith/monolith_resonator.rsi/icon.png",
        "Resources/Textures/_LuaM/Objects/Monolith/research_report.rsi/meta.json",
        "Resources/Textures/_LuaM/Objects/Monolith/research_report.rsi/icon.png",
        "Resources/Textures/_LuaM/Objects/Monolith/sector_route_beacon.rsi/meta.json",
        "Resources/Textures/_LuaM/Objects/Monolith/sector_route_beacon.rsi/icon.png",
        "Resources/Textures/_LuaM/Objects/Monolith/sensor_drift_detector.rsi/meta.json",
        "Resources/Textures/_LuaM/Objects/Monolith/sensor_drift_detector.rsi/icon.png",
        "Resources/Textures/_LuaM/Objects/Monolith/radiation_node.rsi/meta.json",
        "Resources/Textures/_LuaM/Objects/Monolith/radiation_node.rsi/icon.png",
        "Resources/Textures/_LuaM/Objects/Monolith/flight_recorder.rsi/meta.json",
        "Resources/Textures/_LuaM/Objects/Monolith/flight_recorder.rsi/icon.png",
        "Resources/Textures/_LuaM/Objects/Monolith/sector_dispatch_cartridge.rsi/meta.json",
        "Resources/Textures/_LuaM/Objects/Monolith/sector_dispatch_cartridge.rsi/icon.png",
        "Resources/ConfigPresets/_LuaM/deadSpaceLowPop.toml",
        "Resources/Prototypes/_LuaM/Catalogs/Bounties/solo_bounties.yml",
        "Resources/Prototypes/_LuaM/tags.yml",
        "Resources/Locale/en-US/_LuaM/cargo/solo-bounties.ftl",
        "Resources/Locale/ru-RU/_LuaM/cargo/solo-bounties.ftl",
        "Resources/Locale/en-US/_LuaM/station-beacon/station-beacon.ftl",
        "Resources/Locale/ru-RU/_LuaM/station-beacon/station-beacon.ftl",
        "Resources/Locale/en-US/_NF/cartridge-loader/cartridges.ftl",
        "Resources/Locale/ru-RU/_NF/cartridge-loader/cartridges.ftl",
        "Resources/Locale/en-US/holiday/greet/holiday-greet.ftl",
        "Resources/Locale/ru-RU/holiday/greet/holiday-greet.ftl",
        "Resources/Locale/en-US/_LuaM/administration/luam-ai-director.ftl",
        "Resources/Locale/ru-RU/_LuaM/administration/luam-ai-director.ftl",
        "Resources/Locale/en-US/_NF/bank/bank-ATM-component.ftl",
        "Resources/Locale/ru-RU/_NF/bank/bank-ATM-component.ftl",
        "Resources/Locale/en-US/administration/ui/tabs/admin-tab/player-actions-window.ftl",
        "Resources/Locale/ru-RU/administration/ui/tabs/admin-tab/player-actions-window.ftl",
        "Resources/ServerInfo/Intro.txt",
        "Tools/luam_ai_gateway.py",
        "Tools/summarize_luam_ai_audit.py",
        "Tools/deploy_luam_server_release.ps1",
        "Tools/build_luam_server_release.ps1",
        "Content.Shared/CCVar/CCVars.LuaM.cs",
        "Content.Shared/Inventory/SlotFlags.cs",
        "Content.Shared/Preferences/HumanoidCharacterProfile.cs",
        "Content.Shared/_LuaM/Administration/LuaMAiDirectorEuiState.cs",
        "Content.Shared/_LuaM/Administration/LuaMAiDirectorOpenMessage.cs",
        "Content.Server/_LuaM/Administration/LuaMAiDirectorCommand.cs",
        "Content.Server/_LuaM/Administration/LuaMAiDirectorOpenSystem.cs",
        "Content.Server/_LuaM/Administration/LuaMGatewayShipCommand.cs",
        "Content.Server/_LuaM/Administration/LuaMAiDirectorEui.cs",
        "Content.Server/_LuaM/Donation/LuaMDonationShopCommand.cs",
        "Content.Server/_LuaM/Donation/LuaMDonationShopSystem.cs",
        "Content.Server/_LuaM/Rescue/LuaMRescueAgentComponent.cs",
        "Content.Server/_LuaM/Rescue/LuaMRescueAgentSystem.cs",
        "Content.Server/_LuaM/Rescue/LuaMRescueShuttleSystem.cs",
        "Content.Server/_LuaM/Rescue/LuaMRescueTeamComponent.cs",
        "Content.Server/_LuaM/Rescue/LuaMRescueTeamSystem.cs",
        "Content.Server/_LuaM/Sector/LuaMSectorAiDirectorSystem.cs",
        "Content.Server/_LuaM/Sector/LuaMSectorCommands.cs",
        "Content.Server/_LuaM/Sector/LuaMDistressBeaconComponent.cs",
        "Content.Server/_LuaM/Sector/LuaMDistressBeaconSystem.cs",
        "Content.Server/_LuaM/Sector/LuaMDynamicEventConditionHazardComponent.cs",
        "Content.Server/_LuaM/Sector/LuaMDynamicEventSensorDriftComponent.cs",
        "Content.Server/_LuaM/Sector/LuaMSectorDynamicEventSystem.Ai.cs",
        "Content.Server/_LuaM/Sector/LuaMSectorDynamicEventSystem.cs",
        "Content.Server/_LuaM/Sector/LuaMSectorLeadReportComponent.cs",
        "Content.Server/_LuaM/Sector/LuaMSectorLeadReportSystem.cs",
        "Content.Server/_LuaM/Sector/LuaMSectorStorySystem.cs",
        "Content.Client/_LuaM/Sector/LuaMSectorStatusUiFragment.cs",
        "Content.Client/_LuaM/Sector/UI/LuaMSectorTerminalWindow.cs",
        "Content.Client/_LuaM/Administration/LuaMAiDirectorEui.cs",
        "Content.Client/_LuaM/Administration/LuaMAiDirectorOpenSystem.cs",
        "Content.Client/_LuaM/Administration/LuaMAiDirectorWindow.xaml",
        "Content.Client/_LuaM/Administration/LuaMAiDirectorWindow.xaml.cs",
        "Content.IntegrationTests/Tests/_LuaM/LuaMAiDirectorAdminChatTest.cs",
        "Content.IntegrationTests/Tests/_LuaM/LuaMGatewayShipCommandTest.cs",
        "Content.IntegrationTests/Tests/_LuaM/LuaMGatewayShipPresetTest.cs",
        "Content.IntegrationTests/Tests/_LuaM/LuaMDonationShopTest.cs",
        "Content.IntegrationTests/Tests/_LuaM/LuaMDynamicEventDebrisTest.cs",
        "Content.Client/Administration/UI/Tabs/AdminTab/AdminTab.xaml",
        "Content.Client/Administration/UI/Tabs/AdminTab/AdminTab.xaml.cs",
        "Content.Client/Clothing/ClientClothingSystem.cs",
        "Content.Client/Research/UI/ResearchConsoleMenu.xaml",
        "Content.Client/_Goobstation/Research/UI/FancyResearchConsoleItem.xaml",
        "Content.Client/_Goobstation/Research/UI/FancyResearchConsoleItem.xaml.cs",
        "Content.Client/_Goobstation/Research/UI/FancyResearchConsoleMenu.xaml",
        "Content.Client/_Goobstation/Research/UI/FancyResearchConsoleMenu.xaml.cs",
        "Content.IntegrationTests/Pair/TestPair.cs",
        "Content.IntegrationTests/Tests/Lobby/CharacterCreationTest.cs",
        "Content.IntegrationTests/Utility/GameDataScrounger.Files.cs",
        "Resources/Prototypes/Entities/Mobs/Species/arachnid.yml",
        "Resources/Prototypes/Entities/Mobs/Species/base.yml",
        "Resources/Prototypes/InventoryTemplates/arachnid_inventory_template.yml",
        "Resources/Prototypes/InventoryTemplates/corpse_inventory_template.yml",
        "Resources/Prototypes/InventoryTemplates/human_inventory_template.yml",
        "Resources/Prototypes/_NF/Entities/Mobs/NPCs/mob_hostile_rogue_ai.yml",
        "Resources/Prototypes/_Mono/Shipyard/triage.yml",
        "Resources/Maps/_Mono/Shuttles/triage.yml",
        "Resources/Prototypes/_LuaM/Entities/Mobs/rescue_agent.yml",
        "Resources/Prototypes/_LuaM/NPCs/rescue.yml",
    ]

    guide_xmls = [
        "DeadSpace.xml",
        "SectorRumors.xml",
        "SectorPractices.xml",
        "SoloPlay.xml",
        "SoloFirstMechanics.xml",
        "SoloMechanicsMatrix.xml",
        "LowPopEventPolicy.xml",
        "SoloRoleLoops.xml",
    ]
    required_paths.extend(
        f"Resources/ServerInfo/_LuaM/Guidebook/DeadSpace/{name}"
        for name in guide_xmls
    )

    for rel_path in required_paths:
        path = ROOT / rel_path
        if not path.exists():
            raise AssertionError(f"missing required path: {rel_path}")

    deploy_helper = (ROOT / "Tools/deploy_luam_server_release.ps1").read_text(encoding="utf-8")
    for required_deploy_marker in [
        "Wait-ForEmptyServer",
        "Remote SHA256 mismatch",
        "chmod 755",
        "server_config.toml",
        "rollback",
        "Robust.Server",
        "Robust.Packaging",
    ]:
        assert_contains(deploy_helper, required_deploy_marker, "deploy_luam_server_release.ps1")

    assert_luam_direct_descriptions_russian()

    manifest = load_yaml(ROOT / "Resources/manifest.yml")[0]
    assert_equal(
        manifest.get("splashLogo"),
        "/Textures/_LuaM/LobbyScreens/frontier_monolith_launcher_loading.png",
        "manifest.splashLogo",
    )

    holiday_system = (ROOT / "Content.Server/Holiday/HolidaySystem.cs").read_text(encoding="utf-8")
    assert_contains(
        holiday_system,
        'FixedRoundStartHolidayGreeting = "Да пребудет с вами Бог!"',
        "HolidaySystem fixed round-start holiday greeting",
    )
    assert_not_contains(
        holiday_system,
        "_chatManager.DispatchServerAnnouncement(holiday.Greet())",
        "HolidaySystem mutable holiday greeting dispatch",
    )

    holidays = {
        proto.get("id"): proto
        for proto in load_yaml(ROOT / "Resources/Prototypes/holidays.yml")
        if proto.get("type") == "holiday"
    }
    pride_month = holidays.get("PrideMonth")
    if pride_month is None:
        raise AssertionError("missing PrideMonth holiday")
    pride_greet = pride_month.get("greet")
    if not isinstance(pride_greet, dict):
        raise AssertionError("PrideMonth.greet: expected custom greeting mapping")
    assert_equal(pride_greet.get("__tag__"), "!type:Custom", "PrideMonth.greet")
    assert_equal(pride_greet.get("text"), "holiday-custom-pride-month", "PrideMonth.greet.text")
    for locale in ["en-US", "ru-RU"]:
        holiday_greet = (ROOT / f"Resources/Locale/{locale}/holiday/greet/holiday-greet.ftl").read_text(encoding="utf-8")
        assert_contains(
            holiday_greet,
            "holiday-custom-pride-month = Да пребудет с вами Бог!",
            f"{locale} holiday-custom-pride-month",
        )
        assert_contains(
            holiday_greet,
            "holiday-greet = Да пребудет с вами Бог!",
            f"{locale} holiday-greet",
        )
        assert_not_contains(
            holiday_greet,
            "holiday-custom-pride-month = Счастливого месяца гордости",
            f"{locale} holiday-custom-pride-month",
        )

    sector_story_system = (ROOT / "Content.Server/_LuaM/Sector/LuaMSectorStorySystem.cs").read_text(encoding="utf-8")
    for required_api in [
        "ReputationRewardStep",
        "ReputationRewardCap",
        "RecentHistoryLimit",
        "TryExportMemoryJson",
        "TryImportMemoryJson",
        "TryExportMemoryFile",
        "TryImportMemoryFile",
        "GetMemoryBackups",
        "TryDeleteMemoryBackup",
        "TrySeedDistressStory",
        "TryGetOpenRuntimeDistressStory",
        "TryResetMemory",
        "TryDeletePersistedMemory",
        "GetReputationRewardBonus",
        "BuildRecentHistoryStatuses",
        "LuaMSectorStoryUnlockedEvent",
    ]:
        assert_contains(sector_story_system, required_api, "LuaMSectorStorySystem")

    sector_commands = (ROOT / "Content.Server/_LuaM/Sector/LuaMSectorCommands.cs").read_text(encoding="utf-8")
    for command_name in [
        "luam_sector_status",
        "luam_sector_export",
        "luam_sector_import",
        "luam_sector_export_file",
        "luam_sector_import_file",
        "luam_sector_backups",
        "luam_sector_delete_backup",
        "luam_sector_seed_distress",
        "luam_sector_reset",
        "luam_sector_resolve",
        "luam_sector_history",
        "GetBackupCompletion",
        "confirm",
    ]:
        assert_contains(sector_commands, command_name, "LuaMSectorCommands")

    distress_beacon_system = (ROOT / "Content.Server/_LuaM/Sector/LuaMDistressBeaconSystem.cs").read_text(encoding="utf-8")
    for required_api in [
        "UseInHandEvent",
        "TrySeedDistressStory",
        "component.Seeded",
        "FormatBeaconLocation",
        "GPS карта",
        "Координаты маяка:",
        "Где искать:",
    ]:
        assert_contains(distress_beacon_system, required_api, "LuaMDistressBeaconSystem")

    lead_report_system = (ROOT / "Content.Server/_LuaM/Sector/LuaMSectorLeadReportSystem.cs").read_text(encoding="utf-8")
    for required_api in [
        "GetVerbsEvent<InteractionVerb>",
        "TryPrintLeadReport",
        "TryPrintRuntimeCoordinatePacket",
        "TryPrintRuntimeClosureReport",
        "LuaMSectorEvidenceComponent",
        "BuildReport",
        "BuildRuntimeCoordinatePacket",
        "LuaMSectorStatusSnapshot",
        "hazard.Description",
        "print runtime coordinate packet",
        "Координатный пакет аварийного сигнала",
        "Координаты:",
        "Шаги маршрута",
    ]:
        assert_contains(lead_report_system, required_api, "LuaMSectorLeadReportSystem")
    for localized_key in [
        "luam-sector-terminal-result-refresh",
        "luam-sector-terminal-result-generated",
        "luam-sector-terminal-result-report-printed",
        "luam-sector-terminal-popup-report-printer-failed",
        "luam-sector-terminal-popup-no-open-runtime",
    ]:
        assert_contains(lead_report_system, localized_key, "LuaMSectorLeadReportSystem")
    for hardcoded_text in [
        "Dynamic sector process generated.",
        "Sector lead report printed.",
        "No open runtime lead is available.",
        "Route packet printed:",
    ]:
        assert_not_contains(lead_report_system, hardcoded_text, "LuaMSectorLeadReportSystem")

    sector_status = (ROOT / "Content.Shared/_LuaM/Sector/LuaMSectorStatus.cs").read_text(encoding="utf-8")
    assert_contains(sector_status, "public readonly string Description", "LuaMSectorHazardStatus")

    status_cartridge = (ROOT / "Content.Server/_LuaM/Sector/LuaMSectorStatusCartridgeSystem.cs").read_text(encoding="utf-8")
    assert_contains(status_cartridge, "Description = hazard.Description", "LuaMSectorStatusCartridgeSystem")
    assert_contains(status_cartridge, "snapshot.ActiveConditions", "LuaMSectorStatusCartridgeSystem")
    assert_contains(status_cartridge, "LuaMSectorConditionUiEntry", "LuaMSectorStatusCartridgeSystem")
    assert_contains(status_cartridge, "BuildPreferredProcessUiEntries", "LuaMSectorStatusCartridgeSystem")
    assert_contains(status_cartridge, "LuaMSectorPlayerBriefing.BuildDailyDigestLines", "LuaMSectorStatusCartridgeSystem")
    assert_contains(status_cartridge, "LuaMSectorPlayerBriefing.BuildNextActions", "LuaMSectorStatusCartridgeSystem")
    assert_contains(status_cartridge, "LuaMSectorPlayerBriefing.BuildQuestTasks", "LuaMSectorStatusCartridgeSystem")

    sector_status_ui_state = (ROOT / "Content.Shared/_LuaM/Sector/LuaMSectorStatusUiState.cs").read_text(encoding="utf-8")
    assert_contains(sector_status_ui_state, "public readonly string[] DigestLines", "LuaMSectorStatusUiState")
    assert_contains(sector_status_ui_state, "public readonly string[] BriefingSteps", "LuaMSectorStatusUiState")
    assert_contains(sector_status_ui_state, "public readonly LuaMSectorQuestTaskUiEntry[] QuestTasks", "LuaMSectorStatusUiState")
    assert_contains(sector_status_ui_state, "public struct LuaMSectorQuestTaskUiEntry", "LuaMSectorStatusUiState")

    player_briefing = (ROOT / "Content.Server/_LuaM/Sector/LuaMSectorPlayerBriefing.cs").read_text(encoding="utf-8")
    for briefing_marker in [
        "BuildDigest",
        "BuildDailyDigestLines",
        "BuildNextActions",
        "BuildQuestTasks",
        "ExtractEventRouteLocation",
        "ExtractMarkerLocation",
        "Сводка дня",
    ]:
        assert_contains(player_briefing, briefing_marker, "LuaMSectorPlayerBriefing")

    cartridge_loader_system = (ROOT / "Content.Server/CartridgeLoader/CartridgeLoaderSystem.cs").read_text(encoding="utf-8")
    assert_contains(cartridge_loader_system, "LuaMSectorStatusProgram", "CartridgeLoaderSystem")
    assert_contains(cartridge_loader_system, "LuaMSectorStatusCartridge", "CartridgeLoaderSystem")
    assert_contains(cartridge_loader_system, "component.PreinstalledPrograms.Add(LuaMSectorStatusProgram)", "CartridgeLoaderSystem")

    picker_window = (ROOT / "Content.Client/_NF/LateJoin/Windows/PickerWindow.xaml.cs").read_text(encoding="utf-8")
    assert_not_contains(picker_window, "JobsAvailable.Values.Count != 0", "PickerWindow")
    crew_picker = (ROOT / "Content.Client/_NF/LateJoin/Controls/CrewPickerControl.xaml.cs").read_text(encoding="utf-8")
    assert_contains(crew_picker, "private bool _hideJoblessShips = false;", "CrewPickerControl")
    station_jobs = (ROOT / "Content.Server/Station/Systems/StationJobsSystem.cs").read_text(encoding="utf-8")
    assert_not_contains(station_jobs, "HiddenWithoutOpenJobs && !list.Any", "StationJobsSystem")

    sector_commands = (ROOT / "Content.Server/_LuaM/Sector/LuaMSectorCommands.cs").read_text(encoding="utf-8")
    assert_contains(sector_commands, "luam_sector_condition_preset", "LuaMSectorCommands")
    assert_contains(sector_commands, "dust-cloud", "LuaMSectorCommands")

    status_ui = (ROOT / "Content.Client/_LuaM/Sector/LuaMSectorStatusUiFragment.cs").read_text(encoding="utf-8")
    assert_contains(status_ui, "luam-sector-status-digest", "LuaMSectorStatusUiFragment")
    assert_contains(status_ui, "luam-sector-status-briefing", "LuaMSectorStatusUiFragment")
    assert_contains(status_ui, "luam-sector-status-quests", "LuaMSectorStatusUiFragment")
    assert_contains(status_ui, "luam-sector-status-quest-step-action", "LuaMSectorStatusUiFragment")
    assert_contains(status_ui, "\"AngleRect\"", "LuaMSectorStatusUiFragment")
    assert_contains(status_ui, "luam-sector-status-conditions", "LuaMSectorStatusUiFragment")
    assert_contains(status_ui, "luam-sector-status-automation", "LuaMSectorStatusUiFragment")
    assert_contains(status_ui, "luam-sector-status-dispatch-profile", "LuaMSectorStatusUiFragment")
    assert_contains(status_ui, "luam-sector-status-sector-map", "LuaMSectorStatusUiFragment")
    assert_contains(status_ui, "luam-sector-status-preferred", "LuaMSectorStatusUiFragment")
    assert_contains(status_ui, "MakeConditionRow", "LuaMSectorStatusUiFragment")
    assert_contains(status_ui, "MakeQuestTaskRow", "LuaMSectorStatusUiFragment")
    assert_contains(status_ui, "MakeSectorMapRow", "LuaMSectorStatusUiFragment")
    assert_contains(status_ui, "MakePreferredProcessRow", "LuaMSectorStatusUiFragment")

    lead_report_system = (ROOT / "Content.Server/_LuaM/Sector/LuaMSectorLeadReportSystem.cs").read_text(encoding="utf-8")
    assert_contains(lead_report_system, "LuaMSectorPlayerBriefing.BuildDailyDigestLines", "LuaMSectorLeadReportSystem")
    assert_contains(lead_report_system, "LuaMSectorPlayerBriefing.BuildNextActions", "LuaMSectorLeadReportSystem")
    assert_contains(lead_report_system, "LuaMSectorPlayerBriefing.BuildQuestTasks", "LuaMSectorLeadReportSystem")

    player_briefing_test = (ROOT / "Content.Tests/Server/_LuaM/LuaMSectorPlayerBriefingTest.cs").read_text(encoding="utf-8")
    for briefing_test_marker in [
        "NextActionsPrioritizeOpenLeadRiskAndSectorCondition",
        "DailyDigestIncludesActionableRecapAndRespectsLineLimit",
        "MarkerExtractionKeepsGpsCoordinatesCompact",
        "QuestTasksExposeSeveralClearPlayerTasks",
        "QuestTasksStartWithProcessRequestWhenNoRouteIsOpen",
        "Долети до GPS 120, -45",
        "Сгенерировать зацепку",
    ]:
        assert_contains(player_briefing_test, briefing_test_marker, "LuaMSectorPlayerBriefingTest")

    dynamic_event_system = (ROOT / "Content.Server/_LuaM/Sector/LuaMSectorDynamicEventSystem.cs").read_text(encoding="utf-8")
    assert_contains(dynamic_event_system, "ConditionRadiationHazardPrototype", "LuaMSectorDynamicEventSystem")
    assert_contains(dynamic_event_system, "ConditionSensorDriftMarkerPrototype", "LuaMSectorDynamicEventSystem")
    assert_contains(dynamic_event_system, "AllHazardPresets", "LuaMSectorDynamicEventSystem")
    assert_contains(dynamic_event_system, "EnsureAllHazardsSeeded", "LuaMSectorDynamicEventSystem")
    assert_contains(dynamic_event_system, "LuaMSectorAllHazardsEnabled", "LuaMSectorDynamicEventSystem")
    assert_contains(dynamic_event_system, "SpawnConditionHazards", "LuaMSectorDynamicEventSystem")
    assert_contains(dynamic_event_system, "SpawnSensorDriftMarker", "LuaMSectorDynamicEventSystem")
    assert_contains(dynamic_event_system, "LuaMDynamicEventConditionHazardComponent", "LuaMSectorDynamicEventSystem")
    assert_contains(dynamic_event_system, "LuaMDynamicEventSensorDriftComponent", "LuaMSectorDynamicEventSystem")
    assert_contains(dynamic_event_system, "RouteCalibrationRadiationDampingStep", "LuaMSectorDynamicEventSystem")
    assert_contains(dynamic_event_system, "GetRouteCalibrationRadiationIntensity", "LuaMSectorDynamicEventSystem")
    assert_contains(dynamic_event_system, "BuildConditionHazardMapDetail", "LuaMSectorDynamicEventSystem")
    assert_contains(dynamic_event_system, "ConfigureConditionHazardBeacon", "LuaMSectorDynamicEventSystem")
    assert_contains(dynamic_event_system, "[DAMPED -", "LuaMSectorDynamicEventSystem")
    assert_contains(dynamic_event_system, "HasCommsBlackoutCondition", "LuaMSectorDynamicEventSystem")
    assert_contains(dynamic_event_system, "SetBeaconEnabled(marker, false", "LuaMSectorDynamicEventSystem")

    ai_cvars = (ROOT / "Content.Shared/CCVar/CCVars.LuaM.cs").read_text(encoding="utf-8")
    for cvar_name in [
        "LuaMAiDirectorEnabled",
        "LuaMAiDirectorGatewayUrl",
        "LuaMAiDirectorGatewayToken",
        "LuaMAiDirectorFallbackEnabled",
        "LuaMAiDirectorAdminMode",
        "LuaMAiDirectorLocalBridgeEnabled",
        "LuaMAiDirectorLocalBridgeUnsafeActionsEnabled",
        "local_bridge_unsafe_actions_enabled",
        "LuaMSectorAllHazardsEnabled",
        "LuaMAiDirectorGreetOnJoin",
        "LuaMAiDirectorInitialDelay",
        "LuaMAiDirectorInterval",
        "LuaMAiDirectorRequestTimeout",
        "LuaMAiDirectorGatewayBudgetWindow",
        "LuaMAiDirectorGatewayBudgetWindowRequests",
        "LuaMAiDirectorGatewayBudgetRoundRequests",
        "gateway_budget_window",
        "gateway_budget_window_requests",
        "gateway_budget_round_requests",
        "CVar.CONFIDENTIAL",
    ]:
        assert_contains(ai_cvars, cvar_name, "CCVars.LuaM")

    profile = (ROOT / "Content.Shared/Preferences/HumanoidCharacterProfile.cs").read_text(encoding="utf-8")
    assert_contains(profile, "SectorPioneerGrant = 75000", "HumanoidCharacterProfile")
    assert_contains(profile, "DefaultBalance = SectorPioneerGrant", "HumanoidCharacterProfile")
    assert_contains(profile, "RestrictedNameRegex.Replace", "HumanoidCharacterProfile")

    pda_system = (ROOT / "Content.Server/PDA/PdaSystem.cs").read_text(encoding="utf-8")
    for required_api in [
        "PdaBankIdLetterCount = 2",
        "PdaBankIdMinDigitCount = 4",
        "PdaBankIdDigitCount = 5",
        "PdaBankAccountRegistryPath",
        "TryFindOnlineBankAccount",
        "TryRegisteredBankTransfer",
        "TryBankDepositOffline",
        "BankTransferErrorToLocale",
        "comp-pda-ui-bank-transfer-recipient-not-found",
        "ExtractPdaBankAccountId",
        "RegisterPdaBankAccount",
    ]:
        assert_contains(pda_system, required_api, "PdaSystem bank transfer contract")

    bank_system = (ROOT / "Content.Server/_NF/Bank/BankSystem.cs").read_text(encoding="utf-8")
    for required_api in [
        "TryBankTransfer",
        "error = \"not-online\"",
        "TryBankWithdrawOffline",
        "TryBankDepositOffline",
        "SaveCharacterSlotAsync",
        "PayrollIntervalSeconds = 3600f",
        "PayrollMinimumHourly = 75000",
    ]:
        assert_contains(bank_system, required_api, "BankSystem transfer/payroll contract")

    bank_contracts_test = (ROOT / "Content.IntegrationTests/Tests/_LuaM/LuaMBankAndPdaContractsTest.cs").read_text(encoding="utf-8")
    for required_test_marker in [
        "PdaBankIdsStayCopyFriendlyAndNormalized",
        "PdaBankTransferByIdWorksForOfflineRecipient",
        "PdaBankTransferByIdMissingRecipientShowsRegistrationHint",
        "TryRegisteredBankTransfer",
        "pda-bank-accounts.json",
        "Receiver Rook",
        "LastBankTransferStatus",
    ]:
        assert_contains(bank_contracts_test, required_test_marker, "LuaMBankAndPdaContractsTest")

    donation_shop_system = (ROOT / "Content.Server/_LuaM/Donation/LuaMDonationShopSystem.cs").read_text(encoding="utf-8")
    for required_donation_marker in [
        "CurrencyCode = \"LC\"",
        "UnitName = \"month\"",
        "donation-shop.json",
        "GetPdaState",
        "TryPurchase",
        "GrantAccessUnits",
        "GrantBalance",
        "SetBalance",
        "SetAccess",
        "IsAccessActive",
        "comp-pda-ui-donation-shop-status-locked",
        "comp-pda-ui-donation-shop-status-owned",
        "comp-pda-ui-donation-shop-status-insufficient",
        "LuaMDonationShopAccountChangedEvent",
    ]:
        assert_contains(donation_shop_system, required_donation_marker, "LuaMDonationShopSystem")

    donation_shop_command = (ROOT / "Content.Server/_LuaM/Donation/LuaMDonationShopCommand.cs").read_text(encoding="utf-8")
    for required_donation_command_marker in [
        "Command => \"donateshop\"",
        "[AdminCommand(AdminFlags.Admin)]",
        "grant <username|userId> <units>",
        "1 unit = 1 month",
        "balance <username|userId> <amount>",
        "setbalance <username|userId> <amount>",
        "access <username|userId> <true|false>",
        "info <username|userId>",
    ]:
        assert_contains(donation_shop_command, required_donation_command_marker, "LuaMDonationShopCommand")

    donation_shop_test = (ROOT / "Content.IntegrationTests/Tests/_LuaM/LuaMDonationShopTest.cs").read_text(encoding="utf-8")
    for required_donation_test_marker in [
        "DonationShopRequiresManualAccessAndConsumesBalance",
        "ResetDonationShopLedger",
        "supporter-badge",
        "sector-certificate",
        "comp-pda-ui-donation-shop-status-locked",
        "comp-pda-ui-donation-shop-status-owned",
        "AccessUntil",
        "AddDays(27)",
        "AddDays(32)",
        "record.Balance, Is.EqualTo(1500)",
        "record.Balance, Is.EqualTo(1000)",
        "record.Balance, Is.EqualTo(750)",
    ]:
        assert_contains(donation_shop_test, required_donation_test_marker, "LuaMDonationShopTest")

    slot_flags = (ROOT / "Content.Shared/Inventory/SlotFlags.cs").read_text(encoding="utf-8")
    for flag in ["UNDERWEART", "UNDERWEARB", "SOCKS"]:
        assert_contains(slot_flags, flag, "SlotFlags")

    ai_dynamic_event_system = (ROOT / "Content.Server/_LuaM/Sector/LuaMSectorDynamicEventSystem.Ai.cs").read_text(encoding="utf-8")
    for required_api in [
        "LuaMSectorAiEventProposal",
        "TryGenerateDynamicEventFromAiProposal",
        "SelectRussianText",
        "HasCyrillic",
        "SelectReputationTarget",
        "BuildAiDescription",
        "DynamicRewardMin",
        "DynamicRewardMax",
        "SpawnWorldMarker",
        "SpawnConditionHazards",
    ]:
        assert_contains(ai_dynamic_event_system, required_api, "LuaMSectorDynamicEventSystem.Ai")

    ai_director = (ROOT / "Content.Server/_LuaM/Sector/LuaMSectorAiDirectorSystem.cs").read_text(encoding="utf-8")
    for required_api in [
        "LuaMSectorAiDirectorSystem",
        "BuildAdminState",
        "NormalizeRecommendationSourceClasses",
        "NormalizeRecommendationSourceClass",
        "BuildRecommendationSourceClassList",
        "LuaMAiDirectorRecommendationSourceClass.Player",
        "LuaMAiDirectorRecommendationSourceClass.Sector",
        "LuaMAiDirectorRecommendationSourceClass.Pressure",
        "LuaMAiDirectorRecommendationSourceClass.Gateway",
        "AdminGenerateAsync",
        "GenerateImmediateAsync",
        "AdminChatAsync",
        "TryRejectUnsafeAdminChatRequest",
        "ExtractAdminChatCommandTokens",
        "IsLikelyAdminConsoleRequest",
        "Запрос не отправлен во внешний API",
        "AdminReviewAsync",
        "ExecuteChatCommandAsync",
        "RequestGatewayChatAsync",
        "BuildGatewayChatRequest",
        "RequestGatewayReviewAsync",
        "BuildGatewayReviewRequest",
        "BuildAiMemoryBrief",
        "BuildAiSafetyDirectives",
        "AiMemoryBrief",
        "SafetyDirectives",
        "targetable={targetable}",
        "BuildGatewayReviewUri",
        "AdminSetEnabled",
        "AdminInstruction",
        "PlayerStatusChanged",
        "RequestGatewayProposalAsync",
        "BuildGatewayRequest",
        "LuaMAiDirectorGatewayUrl",
        "LuaMAiDirectorGatewayToken",
        "TryGenerateDynamicEventFromAiProposal",
        "TryGenerateDynamicEvent(",
        "generate_event",
        "enable_auto_ai",
        "disable_auto_ai",
        "spawn_entity",
        "run_sector_command",
        "send_sector_message",
        "run_admin_command",
        "RunAdminConsoleCommandAsync",
        "AllowedAiAdminCommandNames",
        "luam_rescue_action",
        "luam_rescue_order",
        "luam_rescue_shuttle",
        "luam_rescue_status",
        "NormalizeAiAdminConsoleCommand",
        "IsSafeAiAdminConsoleCommand",
        "IsAllowedAiAdminCommandName",
        "MatchesForbiddenAiAdminCommandPrefix",
        "ForbiddenAiAdminCommandPrefixes",
        "ForbiddenAiAdminCommandTerms",
        "ForbiddenAiAdminCommandMetacharacters",
        "LocalAiAdminCommandAuditName",
        "IsUnsafeLocalBridgeCommand",
        "SanitizeLocalBridgeOutboxText",
        "LuaMAiDirectorLocalBridgeUnsafeActionsEnabled",
        "AppendAiAdminCommandAudit",
        "LuaMAiAdminCommandAuditEntry",
        "ai_admin_command_audit.jsonl",
        "unsafe local bridge actions are disabled",
        "redacted-sensitive-bridge-text",
        "blocked unsafe local server console command",
        "bridge console skipped: {blockReason}",
        "IConsoleHost",
        "ExecuteCommand(null, adminCommand)",
        "LuaMAiDirectorAdminMode",
        "AllowedEntityPrototypeIds",
        "AllowedSectorCommandIds",
        "AdminModeEnabled",
        "SpawnChatEntityAsync",
        "RunChatSectorCommandAsync",
        "SendChatSectorMessageAsync",
        "OffsetAroundPlayer",
        "RouteEventRadiusMin",
        "RouteEventRadiusMax",
        "EventRadiusMin",
        "EventRadiusMax",
        "ImmediateEventRadiusMin",
        "ImmediateEventRadiusMax",
        "DispatchServerMessage",
        "Authorization = new AuthenticationHeaderValue(\"Bearer\"",
    ]:
        assert_contains(ai_director, required_api, "LuaMSectorAiDirectorSystem")
    for coordinate_contract in [
        "PlayerCoordinates",
        "EventCoordinates",
        "var target = PickTarget(targetUserId);",
        "var target = PickTarget(targetUserId, closeEvent: true);",
        "var target = PickTarget(targetUserId, routeEvent: true);",
        "var target = PickTarget(routeEvent: true);",
        "OffsetAroundPlayer(playerCoordinates, closeEvent, routeEvent)",
        "var min = closeEvent ? ImmediateEventRadiusMin : routeEvent ? RouteEventRadiusMin : EventRadiusMin",
        "var max = closeEvent ? ImmediateEventRadiusMax : routeEvent ? RouteEventRadiusMax : EventRadiusMax",
        "NextFloat(min, max)",
        "markerCoordinates: target.EventCoordinates",
        "GetSpawnCoordinatesNear(target)",
        "target.PlayerCoordinates.Position + offset",
    ]:
        assert_contains(ai_director, coordinate_contract, "LuaMSectorAiDirectorSystem around-player contract")
    for privacy_contract in [
        "SanitizeGatewayContextText",
        "BuildGatewaySectorContext",
        "BuildGatewayMapNodeSummaries",
        "location=withheld",
        "Gateway context is minimized",
        "Name = \"selected operator\"",
        "TargetUserId = string.Empty",
        "AdminName = \"admin\"",
        "GatewayGpsPattern",
        "GatewaySecretPattern",
        "GatewayUuidPattern",
        "BuildGatewaySharedContextSummary",
        "BuildGatewayWithheldContextSummary",
        "BuildGatewayPrivacyNotes",
        "BuildAiOutcomeStatus",
        "BuildAiOutcomeGroup",
        "BuildAiOutcomeSummary",
        "BuildAiOutcomeView",
        "BuildAiNextStepHint",
        "BuildRecommendationRiskConfidence",
        "BuildRecommendationDefaultEvidenceSummary",
        "TrimRecommendationMetadata",
        "RecommendationRiskConfidence",
        "GetRecommendationRiskLevel",
        "GetRecommendationRiskReason",
        "GetRecommendationConfidencePercent",
        "GetRecommendationConfidenceBand",
        "GetRecommendationConfidenceReason",
        "IsSafeRecommendationAction",
        "IsGatewayShipRecommendationAction",
        "IsHighImpactRecommendationAction",
        "SendGatewayRequestAsync",
        "SetGatewayHttpClientForTests",
        "BuildGatewayInvalidSchemaUiMessage",
        "provider output rejected",
        "GatewaySharedContext = BuildGatewaySharedContextSummary",
        "GatewayWithheldContext = BuildGatewayWithheldContextSummary",
        "GatewayPrivacyNotes = BuildGatewayPrivacyNotes",
        "AiOutcomeStatus = BuildAiOutcomeStatus",
        "AiOutcomeGroup = BuildAiOutcomeGroup",
        "AiOutcomeSummary = BuildAiOutcomeSummary",
        "AiNextStepHint = BuildAiNextStepHint",
        "Selected target is anonymized",
        "Provider output is parsed into expected action fields",
        "GatewayBudgetSnapshot",
        "TryConsumeGatewayBudget",
        "RefreshGatewayBudgetCounters",
        "GatewayBudgetRejectedException",
        "GetGatewayBudgetWindowRequests",
        "GetGatewayBudgetRoundRequests",
        "OpenAI-compatible API запрос не отправлен",
        "Gateway requests are throttled",
        "GatewayAuditSnapshot",
        "GatewaySanitizationStats",
        "SanitizeGatewayContextTextAudited",
        "RecordGatewaySanitization",
        "RecordGatewayUnsafeInputBlock",
        "RecordGatewayBudgetBlock",
        "RecordGatewayProviderOutputBlock",
        "RecordGatewayTransportFailure",
        "GatewayBlockReasonSnapshot",
        "BuildGatewayBlockReasonSummary",
        "GetGatewayBlockReasonSnapshot",
        "RecordGatewayBlockReason",
        "GatewayBlockCategoryUnsafeInput",
        "GatewayBlockCategoryBudget",
        "GatewayBlockCategoryInvalidSchema",
        "GatewayBlockCategoryForbiddenAction",
        "GatewayBlockCategoryLocalValidation",
        "Block reason preview",
        "Top-line AI outcome status",
        "Grouped AI outcome",
        "Transport failure outcome",
        "last gateway block=",
        "last gateway transport failure=",
        "provider call failed",
        "GatewayAuditTransportFailures",
        "transport failure",
        "action did not run",
        "sensitive details withheld",
        "gateway request prepared recently",
        "request prepared",
        "provider rejected output",
        "transport failed",
        "command blocked",
        "action executed",
        "invalid schema",
        "Fix gateway/provider structured JSON/schema output",
        "Check the local gateway/API service",
        "Rephrase as an allowlisted",
        "forbidden action",
        "local validation rejected",
        "Gateway audit counters",
        "GatewayLastRequestShape",
        "Last gateway request shape preview",
        "No gateway request",
        "metadata only",
        "BuildGatewayLastRequestShape",
        "BuildGatewayRagSourceShape",
        "RecordGatewayRequestShape",
        "RecordGatewayRagSourceShape",
        "BuildGatewaySectorShapeLine",
        "BuildGatewayBoundedContextShapeLine",
        "GatewayLastRequestShape = BuildGatewayLastRequestShape",
        "GatewayRagSourceShape = BuildGatewayRagSourceShape",
        "GatewayRagSnapshot",
        "RAG/source preview",
        "source-shape-only",
        "allowed sources",
        "denied sources",
        "provenance",
        "Last gateway request shape preview",
        "shape-only preview",
        "URL, bearer token, raw prompt text",
    ]:
        assert_contains(ai_director, privacy_contract, "LuaMSectorAiDirectorSystem gateway privacy contract")
    for forbidden_text in [
        "api.openai.com",
        "OPENAI_API_KEY",
        "sk-",
        "UserId = target.Session.UserId.ToString()",
        "[{session.UserId}]",
        "EventX = target.EventCoordinates.Position.X",
        "EventY = target.EventCoordinates.Position.Y",
        "MapId = (int) target.PlayerCoordinates.MapId",
        "X = target.PlayerCoordinates.Position.X",
        "Y = target.PlayerCoordinates.Position.Y",
    ]:
        assert_not_contains(ai_director, forbidden_text, "LuaMSectorAiDirectorSystem")
    for manual_only_ai_command in [
        "luam_ai_generate_event",
        "luam_ai_radio",
        "luam_ai_say",
        "luam_ai_subspace_rift",
        "luam_ai_synthetic_control",
    ]:
        assert_not_contains(ai_director, manual_only_ai_command, "LuaMSectorAiDirectorSystem raw AI admin command allowlist")

    ai_admin_state = (ROOT / "Content.Shared/_LuaM/Administration/LuaMAiDirectorEuiState.cs").read_text(encoding="utf-8")
    for required_api in [
        "LuaMAiDirectorEuiState",
        "LuaMAiDirectorPlayerEntry",
        "LuaMAiDirectorEuiMsg",
        "AutoTemplateId",
        "SetEnabled",
        "Generate",
        "TargetUserId",
        "Instruction",
        "UseGateway",
        "IgnoreOpenLead",
        "ChatTranscript",
        "CanRunServerActions",
        "HasPendingConfirmation",
        "PendingConfirmationId",
        "PendingConfirmationTitle",
        "PendingConfirmationDetail",
        "GatewaySharedContext",
        "GatewayWithheldContext",
        "GatewayPrivacyNotes",
        "GatewayLastRequestShape",
        "GatewayRagSourceShape",
        "GatewayBlockReasonSummary",
        "GatewayBudgetWindowSeconds",
        "GatewayBudgetWindowUsed",
        "GatewayBudgetWindowLimit",
        "GatewayBudgetWindowRemaining",
        "GatewayBudgetRoundUsed",
        "GatewayBudgetRoundLimit",
        "GatewayBudgetRoundRemaining",
        "GatewayBudgetRetrySeconds",
        "GatewayAuditRedactions",
        "GatewayAuditIdRedactions",
        "GatewayAuditSecretRedactions",
        "GatewayAuditLocationRedactions",
        "GatewayAuditTruncatedFields",
        "GatewayAuditUnsafeInputBlocks",
        "GatewayAuditBudgetBlocks",
        "GatewayAuditProviderOutputBlocks",
        "GatewayAuditTransportFailures",
        "GatewayBlockUnsafeInputs",
        "GatewayBlockBudgets",
        "GatewayBlockInvalidSchemas",
        "GatewayBlockForbiddenActions",
        "GatewayBlockLocalValidations",
        "GatewayRagAllowedSources",
        "GatewayRagDeniedSources",
        "GatewayLastRequestShape",
        "LastReview",
        "ReviewHistory",
        "ReviewHistoryCount",
        "AiActionHistory",
        "AiActionHistoryCount",
        "AiOutcomeStatus",
        "AiOutcomeGroup",
        "AiOutcomeSummary",
        "AiNextStepHint",
        "RiskLevel",
        "RiskReason",
        "ConfidenceBand",
        "ConfidencePercent",
        "ConfidenceReason",
        "SourceClasses",
        "LuaMAiDirectorRecommendationSourceClass",
        "public const string Player = \"player\"",
        "public const string Sector = \"sector\"",
        "public const string Pressure = \"pressure\"",
        "public const string Gateway = \"gateway\"",
        "AdminModeEnabled",
        "Review",
        "LogReview",
        "Chat",
        "QuickAction",
        "ConfirmPendingAction",
        "CancelPendingAction",
        "QuickSyntheticControl",
        "QuickGatewayShip",
        "QuickGatewayShipSelected",
        "QuickGatewayShipTriage",
        "QuickGatewayShipHammerhead",
        "QuickGatewayShipTzipora",
        "QuickGatewayShipTokarev",
        "GatewayShipPresets",
        "LuaMAiDirectorGatewayShipEntry",
        "GatewayShipGameMapId",
        "Message",
    ]:
        assert_contains(ai_admin_state, required_api, "LuaMAiDirectorEuiState")

    assert_not_contains(ai_admin_state, "GatewayUrl", "LuaMAiDirectorEuiState")

    ai_admin_command = (ROOT / "Content.Server/_LuaM/Administration/LuaMAiDirectorCommand.cs").read_text(encoding="utf-8")
    assert_contains(ai_admin_command, "Command => \"luamai\"", "LuaMAiDirectorCommand")
    assert_contains(ai_admin_command, "AdminFlags.Admin", "LuaMAiDirectorCommand")
    assert_contains(ai_admin_command, "IAdminManager", "LuaMAiDirectorCommand")
    assert_contains(ai_admin_command, "canRunServerActions", "LuaMAiDirectorCommand")
    assert_contains(ai_admin_command, "allowServerActions: canRunServerActions", "LuaMAiDirectorCommand")
    assert_contains(ai_admin_command, "LuaMAiConsoleConfirmation", "LuaMAiDirectorCommand")
    assert_contains(ai_admin_command, 'ConfirmFlag = "--confirm"', "LuaMAiDirectorCommand")
    assert_contains(ai_admin_command, "TryConsume", "LuaMAiDirectorCommand")
    assert_contains(ai_admin_command, "This command can affect the round or send player-visible AI output.", "LuaMAiDirectorCommand")
    if ai_admin_command.count("LuaMAiConsoleConfirmation.TryConsume") < 5:
        raise AssertionError("LuaMAiDirectorCommand: dangerous luam_ai console commands must require --confirm")
    assert_contains(ai_admin_command, "Command => \"luam_ai_generate_event\"", "LuaMAiDirectorCommand")
    assert_contains(ai_admin_command, "AdminFlags.Server", "LuaMAiDirectorCommand")
    assert_contains(ai_admin_command, "GenerateImmediateAsync", "LuaMAiDirectorCommand")

    gateway_ship_command = (ROOT / "Content.Server/_LuaM/Administration/LuaMGatewayShipCommand.cs").read_text(encoding="utf-8")
    for required_gateway_ship_marker in [
        "Command => \"luam_gateway_ship\"",
        "AdminFlags.Server",
        "LuaMAiConsoleConfirmation.TryConsume",
        "DefaultGameMap = \"Twilight\"",
        "GatewayPrototype = \"Gateway\"",
        "PairHereFlag = \"--pair-here\"",
        "LoadGameMapWithId",
        "MergeGameMap",
        "MapLoaderSystem",
        "TryLoadGridFallback",
        "IsKnownGridBackedGameMap",
        "\"/Shuttles/\"",
        "SpawnEnabledGateway",
        "SetDestinationName",
        "SetEnabled",
        "PickGatewayCoordinates",
        "GetAllTiles",
        "GridTileToLocal",
        "UpdateAllGateways",
    ]:
        assert_contains(gateway_ship_command, required_gateway_ship_marker, "LuaMGatewayShipCommand")

    admin_tab = (ROOT / "Content.Client/Administration/UI/Tabs/AdminTab/AdminTab.xaml").read_text(encoding="utf-8")
    assert_contains(admin_tab, 'Name="LuaMAiDirectorButton"', "AdminTab")
    assert_contains(admin_tab, "admin-player-actions-window-luam-ai-director", "AdminTab")
    assert_not_contains(admin_tab, 'Command="luamai"', "AdminTab")

    admin_tab_code = (ROOT / "Content.Client/Administration/UI/Tabs/AdminTab/AdminTab.xaml.cs").read_text(encoding="utf-8")
    assert_contains(admin_tab_code, "LuaMAiDirectorOpenSystem", "AdminTab")
    assert_contains(admin_tab_code, "RequestOpen", "AdminTab")
    assert_contains(admin_tab_code, "LuaMAiDirectorButton.ToolTip", "AdminTab")
    assert_not_contains(admin_tab_code, "AdminStatusUpdated", "AdminTab")
    assert_not_contains(admin_tab_code, "AdminFlags.Admin", "AdminTab")

    ai_admin_open_message = (ROOT / "Content.Shared/_LuaM/Administration/LuaMAiDirectorOpenMessage.cs").read_text(encoding="utf-8")
    assert_contains(ai_admin_open_message, "MsgLuaMAiDirectorOpen", "LuaMAiDirectorOpenMessage")

    ai_admin_open_server = (ROOT / "Content.Server/_LuaM/Administration/LuaMAiDirectorOpenSystem.cs").read_text(encoding="utf-8")
    assert_contains(ai_admin_open_server, "TryGetSessionByChannel", "LuaMAiDirectorOpenSystem")
    assert_contains(ai_admin_open_server, "AdminFlags.Admin", "LuaMAiDirectorOpenSystem")
    assert_contains(ai_admin_open_server, "OpenEui(new LuaMAiDirectorEui()", "LuaMAiDirectorOpenSystem")

    ai_admin_open_client = (ROOT / "Content.Client/_LuaM/Administration/LuaMAiDirectorOpenSystem.cs").read_text(encoding="utf-8")
    assert_contains(ai_admin_open_client, "ClientSendMessage(new MsgLuaMAiDirectorOpen())", "LuaMAiDirectorOpenSystem")

    for rel_path in [
        "Resources/Locale/en-US/administration/ui/tabs/admin-tab/player-actions-window.ftl",
        "Resources/Locale/ru-RU/administration/ui/tabs/admin-tab/player-actions-window.ftl",
    ]:
        keys = load_ftl_keys(ROOT / rel_path)
        assert_contains(keys, "admin-player-actions-window-luam-ai-director", rel_path)
        assert_contains(keys, "admin-player-actions-window-luam-ai-director-tooltip", rel_path)
        assert_contains(keys, "admin-player-actions-window-luam-ai-director-disabled-tooltip", rel_path)

    ai_admin_eui = (ROOT / "Content.Server/_LuaM/Administration/LuaMAiDirectorEui.cs").read_text(encoding="utf-8")
    assert_contains(ai_admin_eui, "AdminGenerateAsync", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "AdminChatAsync", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "AdminReviewAsync", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "AdminLogReview", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "QuickActionAsync", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "BuildQuickActionMessage", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "BuildActionPreview", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "AI action preview", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "confirm policy=Server flag required", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "BuildActionPreviewRiskGate", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "BuildActionPreviewConfirmMeaning", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "BuildActionPreviewOperatorChecklist", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "risk gate=", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "confirm means=", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "cancel means=", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "operator checklist=", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "raw user id withheld from preview", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "GetQuickActionOutcome", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "GetQuickActionRisk", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "QuickActionRequiresTarget", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "CompactForLog", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "RequestConfirmation", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "ConfirmPendingActionAsync", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "TryRequireServerAction", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "AdminFlags.Server", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "IAdminLogManager", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "IPrototypeManager", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "AppendReviewHistory", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "AppendAiActionHistory", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "BuildAiActionHistoryEntry", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "AppendBoundedAiActionHistory", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "AiActionHistoryMaxLength = 6000", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "bounded local admin history", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "confirmed; execution finished or submitted locally", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "canceled; no server effect executed", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "failed; local execution error; details kept in server log", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "BuildAdminState", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "BuildGatewayShipPresets()", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "GatewayShipPreset", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "TryGetGatewayShipPreset", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "QuickGatewayShipSelected", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "TryResolveGatewayShipPreset", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "BuildGatewayShipPresets", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "BuildAdminShipBuildPresets()", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "TryResolveAdminShipBuildId", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "ship-build", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "IsSafeGatewayShipGameMapId", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "GatewayShipGameMapId", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "Denied gateway ship request", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "(\"Baeg\", \"Z-22 Baeg\")", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "(\"Triage\", \"Triage\")", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "(\"Hammerhead\", \"Hammerhead\")", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "(\"Tzipora\", \"Tzipora\")", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "(\"Tokarev\", \"Tokarev\")", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "ExecuteGatewayShipQuickActionAsync", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "_director.SpawnShipNearAdminAsync", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "quick_action={action}", "LuaMAiDirectorEui")
    assert_contains(ai_admin_eui, "vessel={TrimForLog(preset.GameMap)}", "LuaMAiDirectorEui")
    assert_contains(ai_director, "BuildAdminCapabilitiesResult", "LuaMSectorAiDirectorSystem")
    assert_contains(ai_director, "IsAdminAiCapabilityRequest", "LuaMSectorAiDirectorSystem")
    assert_contains(ai_director, "allowServerActions", "LuaMSectorAiDirectorSystem")
    assert_contains(ai_director, "IsServerActionGatewayCommand", "LuaMSectorAiDirectorSystem")
    assert_contains(ai_director, "LuaMAiDirectorGatewayShipEntry[]? gatewayShipPresets", "LuaMSectorAiDirectorSystem")
    assert_contains(ai_director, "GatewayShipPresets = gatewayShipPresets ?? []", "LuaMSectorAiDirectorSystem")
    assert_contains(ai_director, "SpawnableShipBuild", "LuaMSectorAiDirectorSystem")
    assert_contains(ai_director, "BuildAdminShipBuildPresets", "LuaMSectorAiDirectorSystem")
    assert_contains(ai_director, "EnumerateSpawnableAdminShipBuilds", "LuaMSectorAiDirectorSystem")
    assert_contains(ai_director, "TryResolveAiShipBuild", "LuaMSectorAiDirectorSystem")
    assert_contains(ai_director, "TryResolveAdminShipBuildId", "LuaMSectorAiDirectorSystem")
    assert_contains(ai_director, "VesselPrototype", "LuaMSectorAiDirectorSystem")
    assert_contains(ai_director, "GameMapPrototype", "LuaMSectorAiDirectorSystem")
    assert_contains(ai_director, "EnumerateRawShipMapFiles", "LuaMSectorAiDirectorSystem")
    assert_contains(ai_director, "ContentFindFiles(new ResPath(\"/Maps/\"))", "LuaMSectorAiDirectorSystem")
    assert_contains(ai_director, "IsSpawnableRawShipMapFile", "LuaMSectorAiDirectorSystem")
    assert_contains(ai_director, "BuildRawShipMapBuildId", "LuaMSectorAiDirectorSystem")
    assert_contains(ai_director, "vessel.ShuttlePath", "LuaMSectorAiDirectorSystem")

    ai_admin_chat_test = (ROOT / "Content.IntegrationTests/Tests/_LuaM/LuaMAiDirectorAdminChatTest.cs").read_text(encoding="utf-8")
    for required_test_marker in [
        "AdminChatCapabilitiesAndStatusDoNotRequireGateway",
        "AdminChatCanDisableServerActionsForEui",
        "AdminChatRefusesUnsafeRequestsBeforeGateway",
        "allowServerActions: false",
        "Server-flag confirmed action",
        "LuaMAiDirectorGatewayUrl",
        "Что ты можешь делать на сервере?",
        "Локальная команда LuaM распознана без обращения к внешнему API.",
        "OpenAI-compatible API не настроен.",
        "Выполни серверную команду: shutdown",
        "Покажи gateway_token и server_config.toml",
        "status;shutdown",
        "GatewayChatFailureRecordsSafeLastRequestShape",
        "GatewayChatMalformedJsonRecordsInvalidSchemaWithoutLeakingBody",
        "StaticGatewayHandler",
        "HttpMessageHandler",
        "SetGatewayHttpClientForTests",
        "provider output rejected",
        "JSON/schema",
        "GatewayBlockInvalidSchemas",
        "JsonException",
        "GatewayLastRequestShape",
        "GatewayRagSourceShape",
        "GatewayRagAllowedSources",
        "GatewayRagDeniedSources",
        "AiOutcomeStatus",
        "GatewayAuditTransportFailures",
        "last gateway transport failure=network error",
        "purpose=admin chat",
        "provider call failed",
        "transport failure",
        "AiOutcomeGroup",
        "AiOutcomeSummary",
        "transport failed",
        "provider rejected output",
        "command blocked",
        "AiNextStepHint",
        "Check the local gateway/API service",
        "Fix gateway/provider structured JSON/schema output",
        "Rephrase as an allowlisted",
        "GatewayBlockReasonCountersExplainUnsafeInputBlocks",
        "ConfirmationPreviewExplainsImpactAndWithholdsTargetIdentifiers",
        "ActionHistoryEntrySummarizesConfirmCancelAndStaysBounded",
        "InvokePrivateStatic",
        "AI action preview",
        "AI action history",
        "BuildAiActionHistoryEntry",
        "AppendBoundedAiActionHistory",
        "decision=confirmed",
        "decision=canceled",
        "privacy=bounded local admin history",
        "confirm policy=Server flag required",
        "risk gate=high - pause before Confirm",
        "confirm means=execute a player-visible or round-affecting action locally after validation",
        "cancel means=no server effect is executed",
        "operator checklist=evidence reviewed",
        "raw user id withheld from preview",
        "local server action only; no external provider call",
        "gatewayShipGameMap=Baeg",
        "instructionLength=42",
        "GatewayBlockReasonSummary",
        "GatewayBlockUnsafeInputs",
        "GatewayBlockBudgets",
        "GatewayBlockInvalidSchemas",
        "GatewayBlockForbiddenActions",
        "GatewayBlockLocalValidations",
        "last gateway block=unsafe input",
        "action did not run",
        "sensitive details withheld",
        "category=unsafe input",
        "purpose=admin chat",
        "route=/chat",
        "shape-only",
        "source-shape-only",
        "allowed sources",
        "denied sources",
        "provenance",
        "URL, bearer token, raw prompt text",
        "Запрос не отправлен во внешний API",
    ]:
        assert_contains(ai_admin_chat_test, required_test_marker, "LuaMAiDirectorAdminChatTest")

    ai_synthetic_control_test = (ROOT / "Content.IntegrationTests/Tests/_LuaM/LuaMSyntheticControlTest.cs").read_text(encoding="utf-8")
    for required_test_marker in [
        "GatewayBudgetBlocksSecondWindowRequestBeforeProviderUse",
        "InvokeTryConsumeGatewayBudget",
        "LuaMAiDirectorGatewayBudgetWindowRequests",
        "GatewayBudgetWindowUsed",
        "GatewayBudgetWindowRemaining",
        "GatewayBudgetRetrySeconds",
        "Gateway requests are throttled",
        "GatewayAuditCountsRedactionsWithoutSensitiveValues",
        "InvokeSanitizeGatewayContextTextAudited",
        "GatewayAuditRedactions",
        "GatewayAuditSecretRedactions",
        "GatewayAuditLocationRedactions",
        "GatewayAuditBudgetBlocks",
        "GatewayAuditTransportFailures",
        "Gateway audit counters",
        "GatewayLastRequestShape",
        "GatewayRagSourceShape",
        "GatewayRagAllowedSources",
        "GatewayRagDeniedSources",
        "GatewayBlockReasonSummary",
        "GatewayBlockUnsafeInputs",
        "GatewayBlockBudgets",
        "GatewayBlockInvalidSchemas",
        "GatewayBlockForbiddenActions",
        "GatewayBlockLocalValidations",
        "AiOutcomeStatus",
        "AiOutcomeGroup",
        "AiOutcomeSummary",
        "Grouped AI outcome",
        "provider rejected output",
        "command blocked",
        "gateway unavailable",
        "local safe commands",
        "last gateway block=budget block",
        "last gateway block=local validation rejected",
        "action did not run",
        "sensitive details withheld",
        "GatewayProviderBlockReasonsCategorizeOutputFailures",
        "InvokeRecordGatewayProviderOutputBlock",
        "category=budget block",
        "category=invalid schema",
        "category=forbidden action",
        "category=local validation rejected",
        "Last gateway request shape preview",
        "RAG/source preview",
        "Block reason preview",
        "Transport failure outcome",
        "No gateway request",
        "metadata only",
        "No gateway RAG/source retrieval",
        "source categories and counts only",
        "No gateway block reason",
    ]:
        assert_contains(ai_synthetic_control_test, required_test_marker, "LuaMSyntheticControlTest")

    ai_director_parsing_test = (ROOT / "Content.IntegrationTests/Tests/_LuaM/LuaMAiDirectorParsingTest.cs").read_text(encoding="utf-8")
    for required_test_marker in [
        "LocalBridgeClassifiesUnsafeCommands",
        "LocalBridgeOutboxRedactsSensitiveText",
        "AiAdminConsoleGuardBlocksManualOnlyAiCommands",
        "luam_ai_generate_event --confirm auto",
        "luam_ai_say --confirm",
        "IsUnsafeLocalBridgeCommand",
        "SanitizeLocalBridgeOutboxText",
        "redacted-sensitive-bridge-text",
    ]:
        assert_contains(ai_director_parsing_test, required_test_marker, "LuaMAiDirectorParsingTest")

    gateway_ship_test = (ROOT / "Content.IntegrationTests/Tests/_LuaM/LuaMGatewayShipCommandTest.cs").read_text(encoding="utf-8")
    for required_test_marker in [
        "GatewayShipCommandLoadsShipAndPlacesEnabledGateway",
        "luam_gateway_ship --confirm",
        "Twilight",
        "LuaM-SmokeShip",
        "GatewayComponent",
        "MapGridComponent",
        "beforeGatewayUids",
        "shipGateway.Component.Enabled",
        "xform!.MapID",
    ]:
        assert_contains(gateway_ship_test, required_test_marker, "LuaMGatewayShipCommandTest")

    gateway_ship_preset_test = (ROOT / "Content.IntegrationTests/Tests/_LuaM/LuaMGatewayShipPresetTest.cs").read_text(encoding="utf-8")
    for required_test_marker in [
        "GatewayShipPresetListComesFromSpawnableVesselPrototypes",
        "GatewayShipPresetValidationAcceptsSafeSpawnableVessels",
        "ResolveGatewayShipPreset",
        "TryResolveGatewayShipPreset",
        "Twilight;shutdown",
        "../Twilight",
        "DefinitelyMissingShip",
        "Saltern",
        "Baeg",
        "Kopye",
        "invalid gameMap id",
        "unknown vessel",
        "LuaMAiDirectorEui",
        "BuildGatewayShipPresets",
        "LuaMAiDirectorGatewayShipEntry",
        "presets.Length",
        "QJ490",
        "Twilight",
        "Triage",
        "Hammerhead",
        "Tzipora",
        "Tokarev",
        "Kupol",
        "Molotok",
        "GameMapId.All",
    ]:
        assert_contains(gateway_ship_preset_test, required_test_marker, "LuaMGatewayShipPresetTest")

    ai_admin_client = (ROOT / "Content.Client/_LuaM/Administration/LuaMAiDirectorWindow.xaml.cs").read_text(encoding="utf-8")
    for required_api in [
        "LuaMAiDirectorWindow",
        "RefreshPressed",
        "TogglePressed",
        "GeneratePressed",
        "ReviewPressed",
        "LogReviewPressed",
        "CopyLastReview",
        "CopyOutcomeButton",
        "CopyOutcomeSummary",
        "CopyAuditBundleButton",
        "CopyAuditBundleSummary",
        "BuildAuditBundleCopyText",
        "BuildAuditBundleRecommendationSection",
        "BuildAuditBundleActionHistorySection",
        "AppendAuditBundleSection",
        "# LuaM AI admin audit bundle",
        "scope=admin-only local copy-safe markdown",
        "auditBundle=bounded copy-safe local admin export",
        "BuildOutcomeCopyText",
        "SanitizeOutcomeCopyText",
        "OutcomeCopyUrlPattern",
        "OutcomeCopySecretPattern",
        "OutcomeCopyGpsPattern",
        "OutcomeCopyUuidPattern",
        "copy-safe local admin summary",
        "ChatPressed",
        "SubmitChat",
        "UseGatewayCheck",
        "ReviewButton",
        "CopyReviewButton",
        "LogReviewButton",
        "QuickActionPressed",
        "SubmitQuickAction",
        "ReviewHistoryLabel",
        "ActionHistoryLabel",
        "BuildActionHistory",
        "IgnoreOpenLeadCheck",
        "TargetOption",
        "TemplateOption",
        "ChatInput",
        "ChatLabel",
        "ModeLabel",
        "CapabilityButton",
        "ConfirmationPanel",
        "ConfirmPendingPressed",
        "CancelPendingPressed",
        "GatewayShipOption",
        "QuickGatewayShipSelectedButton",
        "QuickGatewayShipSelected",
        "RebuildGatewayShips",
        "GatewayShipGameMapId",
        "QuickGatewayShipButton",
        "QuickGatewayShip",
        "QuickGatewayShipTriageButton",
        "QuickGatewayShipTriage",
        "QuickGatewayShipHammerheadButton",
        "QuickGatewayShipHammerhead",
        "QuickGatewayShipTziporaButton",
        "QuickGatewayShipTzipora",
        "QuickGatewayShipTokarevButton",
        "QuickGatewayShipTokarev",
        "BuildConfirmation",
        "BuildMode",
        "BuildReadiness",
        "IsGatewayBudgetBlocked",
        "BuildReadinessBudgetStatus",
        "BuildReadinessBudgetDetail",
        "AppendReadinessLine",
        "OperationsAuditLabel",
        "RoundAuditFooterLabel",
        "BuildOperationsAudit",
        "BuildRoundAuditFooter",
        "GetRoundAuditGate",
        "BuildRoundAuditLastDecision",
        "BuildRoundAuditLastBlock",
        "BuildRoundAuditNextStep",
        "GetOperationsAuditStatus",
        "AppendOperationsAuditLine",
        "SanitizeOperationsAuditDetail",
        "AI operations audit:",
        "AI readiness:",
        "AiOutcomeStatus",
        "AiOutcomeGroup",
        "AiOutcomeSummary",
        "AiNextStepHint",
        "RiskLevel",
        "RiskReason",
        "ConfidenceBand",
        "ConfidencePercent",
        "ConfidenceReason",
        "EvidenceSummary",
        "SourceSummary",
        "SourceClasses",
        "RecommendationFilterOption",
        "RecommendationSortOption",
        "RecommendationSourceSummaryLabel",
        "CopyRecommendationsButton",
        "CopyRecommendationSummary",
        "RecommendationReviewSummaryLabel",
        "RecommendationReviewDetails",
        "BuildRecommendationReviewSummary",
        "RecommendationExplanationLabel",
        "BuildRecommendationExplanation",
        "RecommendationCommandPreviewLabel",
        "BuildRecommendationCommandPreview",
        "GetRecommendationCommandPreviewImpact",
        "GetRecommendationCommandPreviewTargetBoundary",
        "GetRecommendationCommandPreviewGate",
        "GetRecommendationExplanationStatus",
        "GetRecommendationExplanationBlockedReason",
        "GetRecommendationExplanationNextStep",
        "GetRecommendationTargetRequirement",
        "GetRecommendationServerRequirement",
        "RecommendationOperatorChecklistLabel",
        "RecommendationProvenanceLabel",
        "BuildRecommendationOperatorChecklist",
        "GetRecommendationOperatorChecklistStatus",
        "GetRecommendationOperatorRiskReview",
        "GetRecommendationOperatorHumanStep",
        "BuildRecommendationProvenance",
        "BuildRecommendationSourceClassCheck",
        "ParseRecommendationSourceClasses",
        "InferRecommendationSourceClassesFromText",
        "RecommendationTextMatchesSourceClass",
        "GetRecommendationSourceClassFilter",
        "_selectedRecommendationSourceClassesSummary",
        "SourceClassesSummary",
        "Recommendation explanation:",
        "Safe command preview:",
        "likely impact:",
        "local validation -> confirmation preview -> explicit Confirm",
        "why this action:",
        "blocked reason:",
        "Operator checklist:",
        "human step:",
        "Recommendation provenance:",
        "source scope:",
        "source classes:",
        "source check:",
        "warning-partial",
        "warning-mismatch",
        "Selected recommendation review:",
        "open details for explanation, checklist, and provenance",
        "BuildRecommendationDisplayBody",
        "BuildRecommendationCopyText",
        "BuildRecommendationEmptyStateHint",
        "GetVisibleRecommendations",
        "FilterRecommendations",
        "SortRecommendations",
        "BuildRecommendationOverview",
        "GetRecommendationRiskWeight",
        "Recommendation overview:",
        "RecommendationFilterHighRisk",
        "RecommendationFilterSourcePlayer",
        "RecommendationFilterSourceSector",
        "RecommendationFilterSourcePressure",
        "RecommendationFilterSourceGateway",
        "RecommendationHasSourceClass",
        "BuildRecommendationSourceOverview",
        "BuildRecommendationSourceCounterSummary",
        "CountRecommendationSourceClass",
        "BuildRecommendationSourceClassList",
        "GetRecommendationSourceClassValue",
        "sources: player=",
        "source mix:",
        "sourceClasses=",
        "RecommendationSortConfidenceDesc",
        "AiActionHistory",
        "AiActionHistoryCount",
        "ActionHistoryFilterOption",
        "CopyActionHistoryButton",
        "CopyActionHistorySummary",
        "RebuildActionHistoryFilters",
        "AddActionHistoryFilterOption",
        "BuildActionHistoryDisplayBody",
        "BuildActionHistoryCopyText",
        "ParseActionHistoryEntries",
        "FilterActionHistoryEntries",
        "BuildActionHistoryOverview",
        "BuildActionHistorySummary",
        "Action history overview:",
        "SanitizeMultilineCopyText",
        "ActionHistoryFilterGateway",
        "ActionHistoryFilterServer",
        "ActionHistoryFilterLocal",
        "ReadinessLabel",
        "PrivacyLabel",
        "WorkflowPresetOption",
        "WorkflowPresetLabel",
        "WorkflowTransitionLabel",
        "SafeModeSummaryLabel",
        "GatewayExposureLabel",
        "CopyWorkflowSummaryButton",
        "CopyWorkflowSummary",
        "_lastAuditBundleCopyText",
        "BuildWorkflowCopyText",
        "RefreshWorkflowCopyText",
        "RebuildWorkflowPresets",
        "ApplyWorkflowPreset",
        "BuildWorkflowPresetSummary",
        "BuildWorkflowTransitionSummary",
        "RefreshWorkflowTransitionSummary",
        "GetWorkflowTransitionRiskChange",
        "GetWorkflowTransitionGatewayChange",
        "GetWorkflowTransitionServerImpactChange",
        "GetWorkflowTransitionNextStep",
        "elevated - Generate, server-impact quick actions",
        "treat this as a deliberate risk increase",
        "BuildSafeModeSummary",
        "BuildGatewayExposureSummary",
        "RefreshGatewayExposureSummary",
        "GetGatewayExposureStatus",
        "GetGatewayGenerateExposureStatus",
        "GetGatewayReviewChatExposureStatus",
        "possible - gateway may receive minimized generated-process context",
        "sent outside if used",
        "GetSafeModeStatus",
        "GetSafeModeServerImpactStatus",
        "RefreshSafeModeSummary",
        "WorkflowForcesLocal",
        "WorkflowAllowsGenerateProcess",
        "WorkflowAllowsQuickAction",
        "WorkflowAllowsRecommendationAction",
        "GenerateStateLabel",
        "BuildGenerateProcessState",
        "RefreshGenerateProcessState",
        "RecommendationApplyStateLabel",
        "BuildRecommendationApplyState",
        "GetWorkflowPresetName",
        "IsLocalReadOnlyQuickAction",
        "WorkflowPresetReviewOnly",
        "WorkflowPresetLowRiskLocal",
        "WorkflowPresetGatedServerImpact",
        "BuildPrivacy",
        "AppendPrivacySection",
        "GatewayBudgetWindowUsed",
        "GatewayBudgetRoundRemaining",
        "GatewayAuditRedactions",
        "GatewayAuditProviderOutputBlocks",
        "GatewayAuditTransportFailures",
        "transportFailures",
        "GatewayLastRequestShape",
        "GatewayRagAllowedSources",
        "GatewayRagDeniedSources",
        "GatewayRagSourceShape",
        "GatewayBlockUnsafeInputs",
        "GatewayBlockBudgets",
        "GatewayBlockInvalidSchemas",
        "GatewayBlockForbiddenActions",
        "GatewayBlockLocalValidations",
        "GatewayBlockReasonSummary",
        "luam-ai-director-privacy-audit",
        "luam-ai-director-privacy-rag",
        "luam-ai-director-privacy-blocks",
        "luam-ai-director-ai-outcome-group",
        "luam-ai-director-ai-outcome",
        "luam-ai-director-ai-next-step",
        "luam-ai-director-action-history-empty",
        "luam-ai-director-action-history-body",
        "luam-ai-director-privacy-last-shape",
        "luam-ai-director-privacy-rag-shape",
        "luam-ai-director-privacy-block-reasons",
    ]:
        assert_contains(ai_admin_client, required_api, "LuaMAiDirectorWindow")

    ai_admin_client_eui = (ROOT / "Content.Client/_LuaM/Administration/LuaMAiDirectorEui.cs").read_text(encoding="utf-8")
    assert_contains(ai_admin_client_eui, "GatewayShipGameMapId = request.GatewayShipGameMapId", "LuaMAiDirectorEui client bridge")

    ai_admin_xaml = (ROOT / "Content.Client/_LuaM/Administration/LuaMAiDirectorWindow.xaml").read_text(encoding="utf-8")
    assert_contains(ai_admin_xaml, "QuickGatewayShipButton", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "luam-ai-director-quick-gateway-ship", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "PrivacyLabel", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "luam-ai-director-privacy", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "WorkflowPresetOption", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "WorkflowPresetLabel", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "WorkflowTransitionLabel", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "SafeModeSummaryLabel", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "GatewayExposureLabel", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "CopyWorkflowSummaryButton", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "luam-ai-director-workflow-preset", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "luam-ai-director-workflow-transition", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "luam-ai-director-copy-workflow-summary", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "luam-ai-director-safe-mode-summary", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "luam-ai-director-gateway-exposure", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "ReadinessLabel", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "luam-ai-director-readiness", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "OperationsAuditLabel", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "luam-ai-director-operations-audit", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "CopyAuditBundleButton", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "luam-ai-director-copy-audit-bundle", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "RoundAuditFooterLabel", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "luam-ai-director-round-audit-footer", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "CopyOutcomeButton", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "luam-ai-director-copy-outcome", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "CopyRecommendationsButton", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "luam-ai-director-copy-recommendations", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "RecommendationSourceSummaryLabel", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "CopyActionHistoryButton", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "luam-ai-director-copy-action-history", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "ActionHistoryLabel", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "ActionHistoryFilterOption", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "luam-ai-director-action-history", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "luam-ai-director-action-history-filter", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "RecommendationFilterOption", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "RecommendationSortOption", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "RecommendationApplyStateLabel", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "RecommendationReviewSummaryLabel", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "RecommendationReviewDetails", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "CollapsibleHeading", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "CollapsibleBody", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "RecommendationExplanationLabel", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "RecommendationCommandPreviewLabel", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "RecommendationOperatorChecklistLabel", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "RecommendationProvenanceLabel", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "GenerateStateLabel", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "luam-ai-director-recommendation-filter", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "luam-ai-director-recommendation-sort", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "luam-ai-director-recommendation-explanation", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "luam-ai-director-recommendation-command-preview", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "luam-ai-director-recommendation-operator-checklist", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "luam-ai-director-recommendation-provenance", "LuaMAiDirectorWindow.xaml")
    assert_contains(ai_admin_xaml, "luam-ai-director-recommendation-review-details", "LuaMAiDirectorWindow.xaml")
    for required_gateway_ship_xaml in [
        "GatewayShipOption",
        "QuickGatewayShipSelectedButton",
        "luam-ai-director-gateway-ship",
        "luam-ai-director-quick-gateway-ship-selected",
        "QuickGatewayShipTriageButton",
        "QuickGatewayShipHammerheadButton",
        "QuickGatewayShipTziporaButton",
        "QuickGatewayShipTokarevButton",
        "luam-ai-director-quick-gateway-ship-triage",
        "luam-ai-director-quick-gateway-ship-hammerhead",
        "luam-ai-director-quick-gateway-ship-tzipora",
        "luam-ai-director-quick-gateway-ship-tokarev",
    ]:
        assert_contains(ai_admin_xaml, required_gateway_ship_xaml, "LuaMAiDirectorWindow.xaml")

    ai_admin_window_test = (ROOT / "Content.Tests/Client/_LuaM/LuaMAiDirectorWindowTest.cs").read_text(encoding="utf-8")
    for required_test_marker in [
        "OutcomeCopyTextStaysUsefulAndRedactsSensitiveValues",
        "ReadinessChecklistExplainsLocalOnlyAndBlockedStates",
        "OperationsAuditSummarizesControlsAndRedactsSensitiveValues",
        "RoundAuditFooterShowsLatestGateDecisionAndRedactsSensitiveValues",
        "WorkflowPresetsGateQuickActionsRecommendationsAndExplainMode",
        "WorkflowTransitionSummaryExplainsRiskIncreaseAndTightening",
        "RecommendationApplyStateExplainsDisabledAndReadyReasons",
        "RecommendationExplanationShowsWhyBlockedAndRedactsSensitiveValues",
        "RecommendationReviewSummaryKeepsPrimaryUiCompactAndRedactsSensitiveValues",
        "RecommendationCommandPreviewShowsImpactGateAndRedactsSensitiveValues",
        "RecommendationOperatorChecklistShowsHumanGateForHighRiskAdvice",
        "RecommendationProvenanceShowsBoundedEvidenceAndRedactsSensitiveValues",
        "RecommendationSourceClassCheckWarnsWhenTextAndMetadataDisagree",
        "GenerateProcessStateExplainsDisabledAndReadyReasons",
        "SafeModeSummaryExplainsLocalActionsGatewayExposureAndServerGate",
        "GatewayExposureSummaryExplainsWhatCanLeaveServer",
        "WorkflowCopyTextSummarizesExposureAndRedactsSensitiveValues",
        "AuditBundleCopyTextExportsMarkdownAndRedactsSensitiveValues",
        "ActionHistoryOverviewFiltersAndGroupsDecisions",
        "RecommendationFiltersSortByRiskConfidenceAndServerScope",
        "RecommendationSourceFiltersGroupEvidenceClasses",
        "LuaMAiDirectorRecommendationSourceClass.Gateway",
        "RecommendationEmptyStateExplainsSafeNextStep",
        "RecommendationCopyTextUsesFilteredSortAndRedactsSensitiveValues",
        "ActionHistoryCopyTextUsesFilterAndRedactsSensitiveValues",
        "BuildOutcomeCopyText",
        "BuildReadiness",
        "BuildOperationsAudit",
        "BuildRoundAuditFooter",
        "BuildWorkflowPresetSummary",
        "BuildWorkflowTransitionSummary",
        "BuildSafeModeSummary",
        "BuildGatewayExposureSummary",
        "BuildWorkflowCopyText",
        "BuildAuditBundleCopyText",
        "WorkflowAllowsQuickAction",
        "WorkflowAllowsRecommendationAction",
        "BuildGenerateProcessState",
        "BuildRecommendationApplyState",
        "BuildRecommendationReviewSummary",
        "BuildRecommendationExplanation",
        "BuildRecommendationCommandPreview",
        "BuildRecommendationOperatorChecklist",
        "BuildRecommendationProvenance",
        "BuildRecommendationCopyText",
        "BuildActionHistoryCopyText",
        "AI operations audit: waiting approval",
        "AI round audit footer: waiting approval",
        "gate=waiting-approval; pending=True; budget=blocked retrySeconds=30; stops=10; blocks=35",
        "lastBlock=recent - unsafe input from [redacted-url] token=[redacted] GPS [withheld]",
        "AI workflow preset: review-only",
        "AI workflow preset: low-risk-local",
        "AI workflow preset: gated-server-impact",
        "Generate, Apply advice, and server-impact quick actions are disabled",
        "low-risk non-server recommendations only",
        "Workflow transition: review-only -> gated-server-impact",
        "risk change: elevated - Generate, server-impact quick actions, high-risk recommendations",
        "gateway change: expanded - OpenAI-compatible API can be used for generated process with minimized context",
        "server-impact change: gated-expanded",
        "next: treat this as a deliberate risk increase",
        "Apply advice: disabled - no recommendation action is selected",
        "workflow preset=review-only blocks all Apply advice actions",
        "workflow preset=low-risk-local blocks server-impact recommendations",
        "Apply advice: ready",
        "Recommendation explanation: ready-server-confirmation",
        "why this action:",
        "blocked reason: workflow preset=low-risk-local blocks server-impact recommendations",
        "requirements: target=missing, server=not-required",
        "Generate process: disabled - request is already in flight",
        "workflow preset=review-only blocks generated server-impact processes",
        "Generate process: ready",
        "mode=external-gateway",
        "mode=local-fallback",
        "AI safe mode summary: local-only",
        "external/gateway: off - API missing",
        "AI safe mode summary: gated",
        "server-impact: gated",
        "Gateway exposure: off - API missing",
        "Gateway exposure: possible - gateway may receive minimized generated-process context",
        "sent outside if used: minimized admin request shape",
        "kept local: raw player/admin identifiers",
        "LuaM AI workflow exposure summary",
        "privacy=copy-safe workflow summary",
        "# LuaM AI admin audit bundle",
        "scope=admin-only local copy-safe markdown",
        "## Workflow Gates",
        "## Privacy Boundary",
        "## Operations Audit",
        "## Action History",
        "auditBundle=bounded copy-safe local admin export",
        "externalContext=redactions 4, ids 1, secrets 2, locations 1, ragAllowed 3, ragDenied 2",
        "gateway stops: watch - unsafe=1, budget=2, providerOutput=3, transport=4",
        "action history: recorded - total=2, confirmed=2, canceled=0, blocked=1, gateway=1, serverImpact=1, localSafe=1",
        "external context: minimized - redactions=6, id=1, secrets=2, locations=3, ragAllowed=4, ragDenied=5",
        "BuildActionHistoryDisplayBody",
        "BuildRecommendationDisplayBody",
        "BuildRecommendationSourceCounterSummary",
        "GetVisibleRecommendations",
        "Recommendation overview: total=3, showing=3, filter=all, sort=risk-high-first",
        "source mix: player 1 | sector 1 | pressure 1 | gateway 1; filter=source-condition/hazard",
        "No safe/read-only recommendation is available; keep server-impact actions gated",
        "treat visible AI advice as uncertain and prefer local status/review",
        "confidence: high=1, low=1; server=2, target=1",
        "Action history overview: total=3, showing=3, filter=all",
        "source/scope: gateway=2, server-impact=2, local-safe=2",
        "showing=2, filter=gateway",
        "showing=1, filter=blocked",
        "LuaM AI recommendation summary",
        "sourceClasses=gateway",
        "source classes: gateway,sector",
        "source classes: sector",
        "source classes: gateway,pressure",
        "source check: aligned - SourceClasses match source/evidence cues (sector)",
        "source check: warning-partial - SourceClasses=gateway,pressure; textCues=sector,pressure; verify source evidence before applying",
        "source check: warning-mismatch - SourceClasses=gateway; textCues=sector; review evidence before applying",
        "source check: fallback-text - explicit SourceClasses unavailable; verify source/evidence text before applying",
        "LuaM AI action history summary",
        "privacy=filtered copy-safe local admin summary",
        "privacy=filtered copy-safe local admin history",
        "copy-safe local admin summary",
        "[redacted-url]",
        "token=[redacted]",
        "GPS [withheld]",
        "[redacted-id]",
        "AI readiness: limited",
        "gateway: limited - API missing",
        "server actions: blocked",
        "budget: not used",
        "AI readiness: waiting confirmation",
        "budget: blocked",
        "window 10/10 used, 0 left",
        "gatewayBudget=window 2/10 used, 8 left",
        "gatewayBlocks=unsafeInput 1, budget 2, invalidSchema 3, forbiddenAction 4, localValidation 5",
        "Does.Not.Contain(\"https://luam.invalid\")",
        "Does.Not.Contain(\"secret-provider-token\")",
        "Does.Not.Contain(\"secret-bearer-value\")",
        "Does.Not.Contain(\"123, 456\")",
        "Does.Not.Contain(\"11111111-2222-3333-4444-555555555555\")",
    ]:
        assert_contains(ai_admin_window_test, required_test_marker, "LuaMAiDirectorWindowTest")

    for required_ai_recommendation_marker in [
        "AdminRecommendationsExposeRiskAndConfidenceBands",
        "RiskLevel",
        "RiskReason",
        "ConfidenceBand",
        "ConfidencePercent",
        "ConfidenceReason",
        "SourceClasses",
        "LuaMAiDirectorRecommendationSourceClass.Gateway",
        "Классы источника:",
        "read-only/local advice",
        "Риск:",
        "Уверенность:",
        "Is.InRange(35, 96)",
    ]:
        assert_contains(ai_admin_chat_test, required_ai_recommendation_marker, "LuaMAiDirectorAdminChatTest")

    for rel_path in [
        "Resources/Locale/en-US/_LuaM/administration/luam-ai-director.ftl",
        "Resources/Locale/ru-RU/_LuaM/administration/luam-ai-director.ftl",
    ]:
        keys = load_ftl_keys(ROOT / rel_path)
        missing = {
            "luam-ai-director-title",
            "luam-ai-director-generate",
            "luam-ai-director-review",
            "luam-ai-director-capabilities",
            "luam-ai-director-capabilities-question",
            "luam-ai-director-workflow-preset",
            "luam-ai-director-workflow-preset-review-only",
            "luam-ai-director-workflow-preset-low-risk-local",
            "luam-ai-director-workflow-preset-gated-server-impact",
            "luam-ai-director-workflow-transition",
            "luam-ai-director-safe-mode-summary",
            "luam-ai-director-gateway-exposure",
            "luam-ai-director-copy-outcome",
            "luam-ai-director-copy-workflow-summary",
            "luam-ai-director-copy-audit-bundle",
            "luam-ai-director-copy-review",
            "luam-ai-director-copy-recommendations",
            "luam-ai-director-copy-action-history",
            "luam-ai-director-log-review",
            "luam-ai-director-confirm",
            "luam-ai-director-cancel",
            "luam-ai-director-confirmation",
            "luam-ai-director-review-history",
            "luam-ai-director-review-history-empty",
            "luam-ai-director-review-history-body",
            "luam-ai-director-action-history",
            "luam-ai-director-round-audit-footer",
            "luam-ai-director-action-history-filter",
            "luam-ai-director-action-history-filter-all",
            "luam-ai-director-action-history-filter-recent",
            "luam-ai-director-action-history-filter-confirmed",
            "luam-ai-director-action-history-filter-canceled",
            "luam-ai-director-action-history-filter-blocked",
            "luam-ai-director-action-history-filter-gateway",
            "luam-ai-director-action-history-filter-server",
            "luam-ai-director-action-history-filter-local",
            "luam-ai-director-action-history-empty",
            "luam-ai-director-action-history-body",
            "luam-ai-director-recommendation-filter",
            "luam-ai-director-recommendation-filter-all",
            "luam-ai-director-recommendation-filter-low-risk",
            "luam-ai-director-recommendation-filter-medium-risk",
            "luam-ai-director-recommendation-filter-high-risk",
            "luam-ai-director-recommendation-filter-server",
            "luam-ai-director-recommendation-filter-safe",
            "luam-ai-director-recommendation-filter-target",
            "luam-ai-director-recommendation-filter-high-confidence",
            "luam-ai-director-recommendation-filter-low-confidence",
            "luam-ai-director-recommendation-filter-source-player",
            "luam-ai-director-recommendation-filter-source-sector",
            "luam-ai-director-recommendation-filter-source-pressure",
            "luam-ai-director-recommendation-filter-source-gateway",
            "luam-ai-director-recommendation-sort",
            "luam-ai-director-recommendation-sort-priority",
            "luam-ai-director-recommendation-sort-risk-asc",
            "luam-ai-director-recommendation-sort-risk-desc",
            "luam-ai-director-recommendation-sort-confidence-desc",
            "luam-ai-director-recommendation-sort-confidence-asc",
            "luam-ai-director-recommendation-explanation",
            "luam-ai-director-recommendation-command-preview",
            "luam-ai-director-recommendation-operator-checklist",
            "luam-ai-director-recommendation-provenance",
            "luam-ai-director-recommendation-review-details",
            "luam-ai-director-use-gateway",
            "luam-ai-director-privacy",
            "luam-ai-director-readiness",
            "luam-ai-director-operations-audit",
            "luam-ai-director-privacy-mode",
            "luam-ai-director-privacy-budget",
            "luam-ai-director-privacy-audit",
            "luam-ai-director-privacy-rag",
            "luam-ai-director-privacy-blocks",
            "luam-ai-director-privacy-last-shape",
            "luam-ai-director-privacy-rag-shape",
            "luam-ai-director-privacy-block-reasons",
            "luam-ai-director-privacy-shared",
            "luam-ai-director-privacy-withheld",
            "luam-ai-director-privacy-notes",
            "luam-ai-director-privacy-line",
            "luam-ai-director-chat",
            "luam-ai-director-chat-placeholder",
            "luam-ai-director-chat-send",
            "luam-ai-director-gateway-ship",
            "luam-ai-director-quick-gateway-ship-selected",
            "luam-ai-director-quick-gateway-ship",
            "luam-ai-director-quick-gateway-ship-triage",
            "luam-ai-director-quick-gateway-ship-hammerhead",
            "luam-ai-director-quick-gateway-ship-tzipora",
            "luam-ai-director-quick-gateway-ship-tokarev",
            "luam-ai-director-status",
            "luam-ai-director-ai-outcome-group",
            "luam-ai-director-ai-outcome",
            "luam-ai-director-ai-next-step",
            "luam-ai-director-mode-gateway-missing",
            "luam-ai-director-mode-safe-manual",
            "luam-ai-director-mode-manual-pulse-configured",
            "luam-ai-director-mode-auto",
            "luam-ai-director-mode-auto-pulse",
            "luam-ai-director-mode-max-danger",
            "luam-ai-director-result",
        } - keys
        if missing:
            raise AssertionError(f"{rel_path}: missing LuaM AI director locale keys {', '.join(sorted(missing))}")

        locale_text = (ROOT / rel_path).read_text(encoding="utf-8")
        for required_recommendation_value in [
            "$risk",
            "$riskReason",
            "$confidenceBand",
            "$confidence",
            "$confidenceReason",
        ]:
            assert_contains(locale_text, required_recommendation_value, rel_path)

    dead_space_config = (ROOT / "Resources/ConfigPresets/_LuaM/deadSpaceLowPop.toml").read_text(encoding="utf-8")
    for required_config in [
        "[luam.ai_director]",
        "enabled = true",
        "fallback_enabled = true",
        "admin_mode = true",
        "local_bridge_enabled = false",
        "local_bridge_unsafe_actions_enabled = false",
        "greet_on_join = true",
        "gateway_url = \"\"",
        "[events]",
        "[gateway]",
        "[luam.sector]",
        "all_hazards_enabled = true",
    ]:
        assert_contains(dead_space_config, required_config, "deadSpaceLowPop.toml")

    ai_gateway = (ROOT / "Tools/luam_ai_gateway.py").read_text(encoding="utf-8")
    for required_api in [
        "OPENAI_API_KEY",
        "OPENAI_BASE_URL",
        "OPENAI_API_MODE",
        "OPENAI_DEFAULT_BASE_URL",
        "LUAM_AI_PROVIDER",
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_BASE_URL",
        "ANTHROPIC_DEFAULT_BASE_URL",
        "ANTHROPIC_VERSION",
        "DEFAULT_ANTHROPIC_MODEL = \"claude-haiku-4-5-20251001\"",
        "claude-haiku-4.5",
        "/messages",
        "x-api-key",
        "anthropic-version",
        "/responses",
        "/chat/completions",
        "json_schema",
        "LUAM_AI_GATEWAY_TOKEN",
        "LUAM_AI_AUDIT_LOG",
        "LUAM_AI_INPUT_RUB_PER_MILLION_TOKENS",
        "LUAM_AI_OUTPUT_RUB_PER_MILLION_TOKENS",
        "LUAM_AI_CACHE_CREATION_RUB_PER_MILLION_TOKENS",
        "LUAM_AI_CACHE_READ_RUB_PER_MILLION_TOKENS",
        "LUAM_AI_CACHED_INPUT_RUB_PER_MILLION_TOKENS",
        "/propose_event",
        "/chat",
        "/review",
        "CURRENT_AUDIT_REQUEST_ID",
        "write_audit_record",
        "audit_provider_request",
        "extract_token_usage",
        "estimate_audit_cost_rub",
        "estimatedCostRub",
        "estimatedCacheCreationCostRub",
        "estimatedCacheReadCostRub",
        "estimatedCachedInputCostRub",
        "estimatedCostCurrency",
        "inputTokens",
        "outputTokens",
        "cacheCreationInputTokens",
        "cacheReadInputTokens",
        "cachedInputTokens",
        "totalTokens",
        "gateway_request",
        "provider_request",
        "COMMAND_SCHEMA",
        "COMMAND_PROMPT",
        "REVIEW_SCHEMA",
        "REVIEW_PROMPT",
        "PROMPT_INJECTION_MARKERS",
        "SECRET_LEAK_MARKERS",
        "detect_prompt_injection_text",
        "normalize_context_safety",
        "redact_sensitive_text",
        "build_provider_context",
        "build_provider_sector_context",
        "build_provider_player_context",
        "MAX_ADMIN_COMMAND_LENGTH",
        "ALLOWED_ADMIN_COMMAND_NAMES",
        "FORBIDDEN_ADMIN_COMMAND_PREFIXES",
        "FORBIDDEN_ADMIN_COMMAND_TERMS",
        "FORBIDDEN_ADMIN_COMMAND_METACHARACTERS",
        "normalize_admin_command",
        "allowed_admin_command_names",
        "choose_allowed_admin_command",
        "matches_forbidden_admin_command_prefix",
        "is_safe_admin_command",
        "allowedAdminCommandNames",
        "luam_rescue_action",
        "luam_rescue_order",
        "luam_rescue_shuttle",
        "luam_rescue_status",
        "autonomous rescue escort team",
        "Tourniquet",
        "Kostyl",
        "Zaslon",
        "take medical supplies from accessible nearby storage",
        "take-target-storage",
        "store collected medical supplies",
        "inputSafetyFlags",
        "aiMemoryBrief",
        "safetyDirectives",
        "DEFAULT_MODEL = \"5.5\"",
        "call_anthropic_messages_api",
        "call_anthropic_command_messages_api",
        "call_ai_command_provider",
        "call_ai_review_provider",
        "validate_command_response",
        "build_fallback_command_response",
        "validate_review_response",
        "build_fallback_review_response",
        "generate_event",
        "enable_auto_ai",
        "disable_auto_ai",
        "spawn_entity",
        "run_sector_command",
        "run_admin_command",
        "send_sector_message",
        "allowedEntityPrototypeIds",
        "allowedSectorCommandIds",
        "entityPrototypeId",
        "sectorCommandId",
        "adminCommand",
        "adminModeEnabled",
        "reviewFocus",
        "influenceRemarks",
        "processRemarks",
        "riskRemarks",
        "tempoRemarks",
        "economyRemarks",
        "crewRemarks",
        "safetyNotes",
        "recommendedActions",
        "allowedTemplateIds",
        "Player context is minimized before provider use",
        "exact player coordinates, event coordinates, user IDs, and real player names are withheld",
        "rewardMin",
        "rewardMax",
        "call_ai_provider",
        "validate_proposal",
    ]:
        assert_contains(ai_gateway, required_api, "Tools/luam_ai_gateway.py")
    for manual_only_ai_command in [
        "luam_ai_generate_event",
        "luam_ai_radio",
        "luam_ai_say",
        "luam_ai_subspace_rift",
        "luam_ai_synthetic_control",
    ]:
        assert_not_contains(ai_gateway, manual_only_ai_command, "Tools/luam_ai_gateway.py raw admin command allowlist")
    assert_not_contains(ai_gateway, "sk-", "Tools/luam_ai_gateway.py")

    ai_gateway_test = (ROOT / "Tools/test_luam_ai_gateway.py").read_text(encoding="utf-8")
    for required_test_marker in [
        "AnthropicMockHandler",
        "ANTHROPIC_MESSAGES_URL",
        "test-anthropic-key",
        "x-api-key",
        "anthropic-version",
        "run_anthropic_mock_test",
        "mockRequestCount",
        "SUMMARY_PATH",
        "summarize_audit",
        "--since",
        "--until",
        "--model",
        "--fallback",
        "/review",
        "reviewFocus",
        "recommendedActions",
        "tempoRemarks",
        "economyRemarks",
        "crewRemarks",
        "safetyNotes",
        "luam_ai_gateway_no_key_audit.jsonl",
        "luam_ai_gateway_anthropic_audit.jsonl",
        "test-anthropic-key\" not in audit_text",
        "inputTokens",
        "outputTokens",
        "cacheCreationInputTokens",
        "cacheReadInputTokens",
        "totalTokens",
        "estimatedCostRub",
        "estimatedCacheCreationCostRub",
        "estimatedCacheReadCostRub",
        "estimatedCostCurrency",
        "1667",
        "103",
        "provider_request",
        "gateway_request",
        "danger-admin-command",
        "safe-admin-command",
        "shutdown now",
        "luam_sector_status",
        "luam_rescue_status",
        "luam_rescue_order",
        "luam_rescue_action",
        "luam_rescue_shuttle",
        "action=stop-pull",
        "equip-slot",
        "treat",
        "vend",
        "store-slot",
        "take-storage",
        "take-target-storage",
        "slot=<slot>",
        "item=<name|prototype|entity>",
        "dangerChat",
        "safeChat",
        "len(requests) == 5",
    ]:
        assert_contains(ai_gateway_test, required_test_marker, "Tools/test_luam_ai_gateway.py")

    ai_audit_summary = (ROOT / "Tools/summarize_luam_ai_audit.py").read_text(encoding="utf-8")
    for required_summary_marker in [
        "merge_audit_events",
        "provider_request",
        "gateway_request",
        "cacheCreationInputTokens",
        "cacheReadInputTokens",
        "estimatedCostRub",
        "observedCostRub",
        "costDeltaRub",
        "effectiveRubPerMillionTokens",
        "parse_datetime",
        "filter_rows",
        "--observed-cost-rub",
        "--since",
        "--until",
        "--request-id",
        "--model",
        "--provider",
        "--path",
        "--status",
        "--fallback",
        "--self-test",
        "1667",
        "103",
    ]:
        assert_contains(ai_audit_summary, required_summary_marker, "Tools/summarize_luam_ai_audit.py")

    local_stack = (ROOT / "Tools/start_local_stack.ps1").read_text(encoding="utf-8")
    for required_local_stack_marker in [
        "gw-audit.jsonl",
        "LUAM_AI_AUDIT_LOG",
        "gateway_audit",
    ]:
        assert_contains(local_stack, required_local_stack_marker, "Tools/start_local_stack.ps1")

    local_stack_docs = (ROOT / "Tools/local_stack.md").read_text(encoding="utf-8")
    for required_local_stack_doc_marker in [
        "## AI audit",
        "Tools\\summarize_luam_ai_audit.py",
        "C:\\MonolithTemp\\gw-audit.jsonl",
        "1667 / 103 / 0 / 0",
        "--observed-cost-rub",
        "--since",
        "--until",
        "--model",
        "--fallback",
        "0,25 ₽",
        "cacheCreationInputTokens",
        "cacheReadInputTokens",
        "LUAM_AI_INPUT_RUB_PER_MILLION_TOKENS",
        "LUAM_AI_CACHE_READ_RUB_PER_MILLION_TOKENS",
    ]:
        assert_contains(local_stack_docs, required_local_stack_doc_marker, "Tools/local_stack.md")

    dynamic_condition_hazard = (ROOT / "Content.Server/_LuaM/Sector/LuaMDynamicEventConditionHazardComponent.cs").read_text(encoding="utf-8")
    assert_contains(dynamic_condition_hazard, "LuaMDynamicEventConditionHazardComponent", "LuaMDynamicEventConditionHazardComponent")
    assert_contains(dynamic_condition_hazard, "ConditionId", "LuaMDynamicEventConditionHazardComponent")
    assert_contains(dynamic_condition_hazard, "RouteCalibrationSource", "LuaMDynamicEventConditionHazardComponent")
    assert_contains(dynamic_condition_hazard, "RouteCalibrationChainDepth", "LuaMDynamicEventConditionHazardComponent")
    assert_contains(dynamic_condition_hazard, "RouteCalibrationRadiationDamping", "LuaMDynamicEventConditionHazardComponent")
    sector_rumor_papers = (ROOT / "Resources/Prototypes/_LuaM/Entities/Objects/Misc/sector_rumor_papers.yml").read_text(encoding="utf-8")
    assert_contains(sector_rumor_papers, "id: LuaMDynamicEventConditionRadiationHazard", "sector_rumor_papers.yml")
    assert_contains(sector_rumor_papers, "text: radiation hazard", "sector_rumor_papers.yml")

    dynamic_sensor_drift = (ROOT / "Content.Server/_LuaM/Sector/LuaMDynamicEventSensorDriftComponent.cs").read_text(encoding="utf-8")
    assert_contains(dynamic_sensor_drift, "LuaMDynamicEventSensorDriftComponent", "LuaMDynamicEventSensorDriftComponent")
    assert_contains(dynamic_sensor_drift, "DriftLocation", "LuaMDynamicEventSensorDriftComponent")

    sector_terminal_ui = (ROOT / "Content.Shared/_LuaM/Sector/LuaMSectorTerminalUi.cs").read_text(encoding="utf-8")
    assert_contains(sector_terminal_ui, "LuaMSectorTerminalUiKey", "LuaMSectorTerminalUi")
    assert_contains(sector_terminal_ui, "LuaMSectorTerminalActionMessage", "LuaMSectorTerminalUi")
    assert_contains(sector_terminal_ui, "StoryId", "LuaMSectorTerminalUi")
    assert_contains(sector_terminal_ui, "TemplateId", "LuaMSectorTerminalUi")
    assert_contains(sector_terminal_ui, "RequestDynamicEvent", "LuaMSectorTerminalUi")
    assert_contains(sector_terminal_ui, "PingActiveRouteMarker", "LuaMSectorTerminalUi")
    assert_contains(sector_terminal_ui, "PrintRuntimeCoordinatePacket", "LuaMSectorTerminalUi")
    assert_contains(sector_terminal_ui, "PrintInsuranceClaimVoucher", "LuaMSectorTerminalUi")
    assert_contains(sector_terminal_ui, "PrintCharterVoucher", "LuaMSectorTerminalUi")

    sector_status_ui = (ROOT / "Content.Shared/_LuaM/Sector/LuaMSectorStatusUiState.cs").read_text(encoding="utf-8")
    assert_contains(sector_status_ui, "LuaMSectorAutomationUiEntry", "LuaMSectorStatusUiState")
    assert_contains(sector_status_ui, "LuaMSectorMapNodeUiEntry", "LuaMSectorStatusUiState")
    assert_contains(sector_status_ui, "SectorMapNodes", "LuaMSectorStatusUiState")
    assert_contains(sector_status_ui, "LuaMSectorPreferredProcessUiEntry", "LuaMSectorStatusUiState")
    assert_contains(sector_status_ui, "PreferredProcesses", "LuaMSectorStatusUiState")
    assert_contains(sector_status_ui, "LuaMSectorInsuranceUiEntry", "LuaMSectorStatusUiState")
    assert_contains(sector_status_ui, "LuaMSectorRegistryUiEntry", "LuaMSectorStatusUiState")
    assert_contains(sector_status_ui, "CanRequestDynamicEvent", "LuaMSectorStatusUiState")
    assert_contains(sector_status_ui, "CanPingRoute", "LuaMSectorStatusUiState")
    assert_contains(sector_status_ui, "RoutePingCount", "LuaMSectorStatusUiState")
    assert_contains(sector_status_ui, "DispatchTier", "LuaMSectorStatusUiState")
    assert_contains(sector_status_ui, "DispatchCooldownReductionPercent", "LuaMSectorStatusUiState")
    assert_contains(sector_status_ui, "DispatchCooldownMinSeconds", "LuaMSectorStatusUiState")
    assert_contains(sector_status_ui, "LastActionResult", "LuaMSectorStatusUiState")

    sector_terminal_bui = (ROOT / "Content.Client/_LuaM/Sector/LuaMSectorTerminalBoundUserInterface.cs").read_text(encoding="utf-8")
    assert_contains(sector_terminal_bui, "LuaMSectorTerminalBoundUserInterface", "LuaMSectorTerminalBoundUserInterface")
    assert_contains(sector_terminal_bui, "LuaMSectorTerminalWindow", "LuaMSectorTerminalBoundUserInterface")
    assert_contains(sector_terminal_bui, "LuaMSectorTerminalActionMessage", "LuaMSectorTerminalBoundUserInterface")

    bounty_contract_shared = (ROOT / "Content.Shared/_NF/BountyContracts/SharedBountyContractSystem.cs").read_text(encoding="utf-8")
    assert_contains(bounty_contract_shared, "AcceptedByUid", "SharedBountyContractSystem")
    assert_contains(bounty_contract_shared, "AcceptedBy", "SharedBountyContractSystem")
    assert_contains(bounty_contract_shared, "BountyContractTrySetAcceptedMessageEvent", "SharedBountyContractSystem")
    assert_contains(bounty_contract_shared, "public const int MinReward = 30000", "SharedBountyContractSystem")
    assert_contains(bounty_contract_shared, "public const int MaxReward = 100000", "SharedBountyContractSystem")
    assert_contains(bounty_contract_shared, "public const int DefaultReward = MinReward", "SharedBountyContractSystem")
    assert_contains(bounty_contract_shared, "IsRewardValid", "SharedBountyContractSystem")

    bounty_contract_server = (ROOT / "Content.Server/_NF/BountyContracts/BountyContractSystem.cs").read_text(encoding="utf-8")
    assert_contains(bounty_contract_server, "TrySetBountyContractAccepted", "BountyContractSystem")
    assert_contains(bounty_contract_server, "acceptedByThisLoader", "BountyContractSystem")
    assert_contains(bounty_contract_server, "if (!IsRewardValid(reward))", "BountyContractSystem")
    assert_contains(bounty_contract_server, "Math.Clamp(reward, MinReward, MaxReward)", "BountyContractSystem")
    for required_api in [
        "BuildAcceptedContractMessage",
        "AppendAcceptedTurnInHint",
        "bounty-contracts-accepted-turn-in-hint",
        "TryGiveAcceptedContractPinpointer",
        "ContractRoutePinpointerPrototype",
        "PinpointerUniversal",
        "SetTarget",
        "TogglePinpointer",
        "TryForcePickupAnyHand",
        "bounty-contracts-pinpointer-given",
        "bounty-contracts-pinpointer-created-nearby",
        "bounty-contracts-pinpointer-target-not-found",
        "BuildContractDescriptionWithRouteContext",
        "bounty-contracts-route-context-vessel",
        "bounty-contracts-route-context-generic",
        "bounty-contracts-route-context-source",
    ]:
        assert_contains(bounty_contract_server, required_api, "BountyContractSystem quest guidance contract")

    bounty_contract_pinpointer_test = (ROOT / "Content.IntegrationTests/Tests/_NF/BountyContracts/BountyContractPinpointerTest.cs").read_text(encoding="utf-8")
    for required_test_marker in [
        "AcceptingRouteContractIssuesActivePinpointer",
        "TryCreateGeneratedBountyContract",
        "TrySetBountyContractAccepted",
        "PinpointerComponent",
        "pinpointer.IsActive",
        "pinpointer.Target",
        "Test Contract Vessel",
    ]:
        assert_contains(bounty_contract_pinpointer_test, required_test_marker, "BountyContractPinpointerTest")

    for rel_path in [
        "Resources/Locale/en-US/_NF/bounty-contracts/bounty-contracts.ftl",
        "Resources/Locale/ru-RU/_NF/bounty-contracts/bounty-contracts.ftl",
    ]:
        bounty_keys = load_ftl_keys(ROOT / rel_path)
        for key in [
            "bounty-contracts-ui-list-route-in-description",
            "bounty-contracts-ui-list-route-vessel",
            "bounty-contracts-ui-list-route-generic",
            "bounty-contracts-route-context-vessel",
            "bounty-contracts-route-context-generic",
            "bounty-contracts-route-context-source",
            "bounty-contracts-accepted-turn-in-hint",
            "bounty-contracts-accepted-message-header",
            "bounty-contracts-accepted-message-description",
            "bounty-contracts-accepted-message-search-fallback",
            "bounty-contracts-pinpointer-given",
            "bounty-contracts-pinpointer-created-nearby",
            "bounty-contracts-pinpointer-target-not-found",
        ]:
            assert_contains(bounty_keys, key, rel_path)

    bounty_contract_server_ui = (ROOT / "Content.Server/_NF/BountyContracts/BountyContractSystem.Ui.cs").read_text(encoding="utf-8")
    assert_contains(bounty_contract_server_ui, "OnTrySetAcceptedMessage", "BountyContractSystem.Ui")
    assert_contains(bounty_contract_server_ui, "BountyContractTrySetAcceptedMessageEvent", "BountyContractSystem.Ui")

    bounty_contract_entry_xaml = (ROOT / "Content.Client/_NF/BountyContracts/UI/BountyContractUiFragmentListEntry.xaml").read_text(encoding="utf-8")
    assert_contains(bounty_contract_entry_xaml, "BountyAccepted", "BountyContractUiFragmentListEntry")
    assert_contains(bounty_contract_entry_xaml, "AcceptButton", "BountyContractUiFragmentListEntry")
    assert_contains(bounty_contract_entry_xaml, 'Name="AcceptButton" HorizontalExpand="True"', "BountyContractUiFragmentListEntry")
    assert_contains(bounty_contract_entry_xaml, 'Name="RemoveButton" Text="{Loc \'bounty-contracts-ui-list-remove\'}"', "BountyContractUiFragmentListEntry")
    assert_not_contains(bounty_contract_entry_xaml, 'GridContainer Columns="2"', "BountyContractUiFragmentListEntry")

    bounty_contract_entry_code = (ROOT / "Content.Client/_NF/BountyContracts/UI/BountyContractUiFragmentListEntry.xaml.cs").read_text(encoding="utf-8")
    assert_contains(bounty_contract_entry_code, "OnSetAcceptedButtonPressed", "BountyContractUiFragmentListEntry")
    assert_contains(bounty_contract_entry_code, "bounty-contracts-ui-list-accept", "BountyContractUiFragmentListEntry")
    assert_contains(bounty_contract_entry_code, "bounty-contracts-ui-list-release", "BountyContractUiFragmentListEntry")
    assert_contains(bounty_contract_entry_code, "bounty-contracts-ui-list-remove-tooltip", "BountyContractUiFragmentListEntry")
    assert_contains(bounty_contract_entry_code, "bounty-contracts-ui-list-remove-disabled-tooltip", "BountyContractUiFragmentListEntry")

    bounty_contract_ui_code = (ROOT / "Content.Client/_NF/BountyContracts/UI/BountyContractUi.cs").read_text(encoding="utf-8")
    assert_contains(bounty_contract_ui_code, "OnSetAcceptedPressed", "BountyContractUi")
    assert_contains(bounty_contract_ui_code, "BountyContractTrySetAcceptedMessageEvent", "BountyContractUi")

    bounty_contract_create_code = (ROOT / "Content.Client/_NF/BountyContracts/UI/BountyContractUiFragmentCreate.xaml.cs").read_text(encoding="utf-8")
    assert_contains(bounty_contract_create_code, "SharedBountyContractSystem.IsRewardValid", "BountyContractUiFragmentCreate")
    assert_contains(bounty_contract_create_code, "bounty-contracts-ui-create-error-vessel-too-long", "BountyContractUiFragmentCreate")
    assert_not_contains(bounty_contract_create_code, "bounty-contracts-ui-create-error-vessel-name-too-long", "BountyContractUiFragmentCreate")

    sector_terminal_window = (ROOT / "Content.Client/_LuaM/Sector/UI/LuaMSectorTerminalWindow.cs").read_text(encoding="utf-8")
    assert_contains(sector_terminal_window, "Resizable = true", "LuaMSectorTerminalWindow")
    assert_contains(sector_terminal_window, "MinSize = new Vector2(640, 480)", "LuaMSectorTerminalWindow")
    assert_contains(sector_terminal_window, "SetSize = new Vector2(820, 700)", "LuaMSectorTerminalWindow")
    assert_contains(sector_terminal_window, "luam-sector-terminal-title", "LuaMSectorTerminalWindow")
    assert_contains(sector_terminal_window, "luam-sector-terminal-action-generate", "LuaMSectorTerminalWindow")
    assert_contains(sector_terminal_window, "luam-sector-terminal-action-ping", "LuaMSectorTerminalWindow")
    assert_contains(sector_terminal_window, "luam-sector-terminal-section-quests", "LuaMSectorTerminalWindow")
    assert_contains(sector_terminal_window, "luam-sector-terminal-quest-step-action", "LuaMSectorTerminalWindow")
    assert_contains(sector_terminal_window, "\"AngleRect\"", "LuaMSectorTerminalWindow")
    assert_contains(sector_terminal_window, "luam-sector-terminal-section-automation", "LuaMSectorTerminalWindow")
    assert_contains(sector_terminal_window, "luam-sector-terminal-section-map", "LuaMSectorTerminalWindow")
    assert_contains(sector_terminal_window, "AddSectorMapNode", "LuaMSectorTerminalWindow")
    assert_contains(sector_terminal_window, "luam-sector-terminal-dispatch-profile", "LuaMSectorTerminalWindow")
    assert_contains(sector_terminal_window, "luam-sector-terminal-dispatch-score", "LuaMSectorTerminalWindow")
    assert_contains(sector_terminal_window, "luam-sector-terminal-section-preferred", "LuaMSectorTerminalWindow")
    assert_contains(sector_terminal_window, "luam-sector-terminal-preferred-request", "LuaMSectorTerminalWindow")
    assert_contains(sector_terminal_window, "luam-sector-terminal-preferred-needs-reputation", "LuaMSectorTerminalWindow")
    assert_contains(sector_terminal_window, "luam-sector-terminal-section-insurance", "LuaMSectorTerminalWindow")
    assert_contains(sector_terminal_window, "luam-sector-terminal-section-registry", "LuaMSectorTerminalWindow")
    assert_contains(sector_terminal_window, "luam-sector-terminal-print-claim", "LuaMSectorTerminalWindow")
    assert_contains(sector_terminal_window, "luam-sector-terminal-print-charter", "LuaMSectorTerminalWindow")
    assert_contains(sector_terminal_window, "CanRequestDynamicEvent", "LuaMSectorTerminalWindow")
    assert_contains(sector_terminal_window, "AddQuestTask", "LuaMSectorTerminalWindow")
    for localized_key in [
        "luam-sector-terminal-quest-header",
        "luam-sector-terminal-quest-step-location",
        "luam-sector-terminal-quest-step-action",
        "luam-sector-terminal-quest-step-finish",
        "luam-sector-terminal-quest-step-reward",
        "luam-sector-terminal-lead-title",
        "luam-sector-terminal-condition-title",
        "luam-sector-terminal-reputation-entry",
        "luam-sector-terminal-reputation-bonus",
        "luam-sector-terminal-insurance-summary",
        "luam-sector-terminal-sector-map-title",
        "luam-sector-terminal-sector-map-entry",
        "luam-sector-terminal-sector-map-risk",
        "luam-sector-terminal-preferred-entry",
        "luam-sector-terminal-registry-summary",
        "luam-sector-terminal-company-record",
        "luam-sector-terminal-ship-record",
    ]:
        assert_contains(sector_terminal_window, localized_key, "LuaMSectorTerminalWindow")
    for hardcoded_label in [
        "Generate dynamic lead",
        "Ping active route marker",
        "Process automation",
        "Request this process",
        "Print voucher for this claim",
        "Print charter for this record",
        "Contract/service bonus",
        "Company record:",
        "Ship record:",
        "Location:",
        "Risk:",
    ]:
        assert_not_contains(sector_terminal_window, hardcoded_label, "LuaMSectorTerminalWindow")

    sector_status_fragment = (ROOT / "Content.Client/_LuaM/Sector/LuaMSectorStatusUiFragment.cs").read_text(encoding="utf-8")
    assert_contains(sector_status_fragment, "var scroll = new ScrollContainer", "LuaMSectorStatusUiFragment")
    assert_contains(sector_status_fragment, "scroll.AddChild(body)", "LuaMSectorStatusUiFragment")
    assert_contains(sector_status_fragment, "body.AddChild(MakeSection(Loc.GetString(\"luam-sector-status-quests\")))", "LuaMSectorStatusUiFragment")
    assert_contains(sector_status_fragment, "luam-sector-status-quest-step-action", "LuaMSectorStatusUiFragment")
    assert_contains(sector_status_fragment, "\"AngleRect\"", "LuaMSectorStatusUiFragment")
    assert_contains(sector_status_fragment, "body.AddChild(MakeSection(Loc.GetString(\"luam-sector-status-automation\")))", "LuaMSectorStatusUiFragment")
    assert_contains(sector_status_fragment, "body.AddChild(MakeSection(Loc.GetString(\"luam-sector-status-history\")))", "LuaMSectorStatusUiFragment")
    assert_contains(sector_status_fragment, "body.AddChild(_history)", "LuaMSectorStatusUiFragment")
    assert_not_contains(sector_status_fragment, "scroll.AddChild(_hazards)", "LuaMSectorStatusUiFragment")

    sector_dynamic_events = (ROOT / "Content.Server/_LuaM/Sector/LuaMSectorDynamicEventSystem.cs").read_text(encoding="utf-8")
    assert_contains(sector_dynamic_events, "BuildAutomationUiEntry", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "PreferredProcessRequiredReputation", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "BuildSectorMapUiEntries", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "LuaMSectorMapNodeUiEntry", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "public const int DynamicRewardMin = 30000", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "public const int DynamicRewardMax = 100000", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "public const int RouteCalibrationRewardStep = 500", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "public const int RouteCalibrationRewardCap = 2000", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "public const int RouteCalibrationClosureRewardStep = 250", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "public const int RouteCalibrationClosureRewardCap = 1000", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "public const int RouteCalibrationRadiationDampingStep = 1", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "public const int RouteCalibrationRadiationMinimumIntensity = 1", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "GetRouteCalibrationClosureRewardBonus", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "GetRouteCalibrationRadiationIntensity", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "GetRouteCalibrationRadiationDampingPreview", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "GetRouteCalibrationSensorDriftSuppressionPreview", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "route calibration radiation damping", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "ConfigureConditionHazardBeacon", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "radiation hazard", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "[SC-{severity}->RAD-{intensity}]", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "template.Reward + conditionRewardBonus + routeCalibrationRewardBonus", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "Route calibration handoff bonus", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "Route calibration closure bonus", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "hazardRewardBonusExtra: routeCalibrationClosureRewardBonus", "LuaMSectorDynamicEventSystem")
    sector_story_system = (ROOT / "Content.Server/_LuaM/Sector/LuaMSectorStorySystem.cs").read_text(encoding="utf-8")
    assert_contains(sector_story_system, "hazardRewardBonusExtra", "LuaMSectorStorySystem")
    assert_contains(sector_dynamic_events, "RouteCalibrationRewardBonus = GetRouteCalibrationRewardBonus(queuedRouteCalibrationSource)", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "RouteCalibrationClosureRewardBonus = GetRouteCalibrationClosureRewardBonus(queuedRouteCalibrationSource)", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "RouteCalibrationRadiationDampingPreview = GetRouteCalibrationRadiationDampingPreview(status, queuedRouteCalibrationSource)", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "RouteCalibrationSensorDriftSuppressionPreview = GetRouteCalibrationSensorDriftSuppressionPreview(status, queuedRouteCalibrationSource)", "LuaMSectorDynamicEventSystem")
    sector_status_ui_state = (ROOT / "Content.Shared/_LuaM/Sector/LuaMSectorStatusUiState.cs").read_text(encoding="utf-8")
    assert_contains(sector_status_ui_state, "RouteCalibrationRewardBonus", "LuaMSectorStatusUiState")
    assert_contains(sector_status_ui_state, "RouteCalibrationClosureRewardBonus", "LuaMSectorStatusUiState")
    assert_contains(sector_status_ui_state, "RouteCalibrationRadiationDampingPreview", "LuaMSectorStatusUiState")
    assert_contains(sector_status_ui_state, "RouteCalibrationSensorDriftSuppressionPreview", "LuaMSectorStatusUiState")
    sector_lead_report = (ROOT / "Content.Server/_LuaM/Sector/LuaMSectorLeadReportSystem.cs").read_text(encoding="utf-8")
    assert_contains(sector_lead_report, "Route calibration reward bonus", "LuaMSectorLeadReportSystem")
    assert_contains(sector_lead_report, "Route calibration closure bonus", "LuaMSectorLeadReportSystem")
    assert_contains(sector_lead_report, "Route calibration radiation damping", "LuaMSectorLeadReportSystem")
    assert_contains(sector_lead_report, "Route calibration sensor drift echo suppressed", "LuaMSectorLeadReportSystem")
    assert_contains(sector_status_fragment, "luam-sector-status-route-calibration-radiation-damping", "LuaMSectorStatusUiFragment")
    assert_contains(sector_status_fragment, "luam-sector-status-route-calibration-sensor-drift-suppressed", "LuaMSectorStatusUiFragment")
    terminal_window = (ROOT / "Content.Client/_LuaM/Sector/UI/LuaMSectorTerminalWindow.cs").read_text(encoding="utf-8")
    assert_contains(terminal_window, "luam-sector-terminal-route-calibration-radiation-damping", "LuaMSectorTerminalWindow")
    assert_contains(terminal_window, "luam-sector-terminal-route-calibration-sensor-drift-suppressed", "LuaMSectorTerminalWindow")
    cartridge_locale_en = (ROOT / "Resources/Locale/en-US/_NF/cartridge-loader/cartridges.ftl").read_text(encoding="utf-8")
    cartridge_locale_ru = (ROOT / "Resources/Locale/ru-RU/_NF/cartridge-loader/cartridges.ftl").read_text(encoding="utf-8")
    assert_contains(cartridge_locale_en, "luam-sector-status-route-calibration-reward-bonus", "cartridges.ftl en-US")
    assert_contains(cartridge_locale_en, "luam-sector-terminal-route-calibration-reward-bonus", "cartridges.ftl en-US")
    assert_contains(cartridge_locale_en, "luam-sector-status-route-calibration-closure-bonus", "cartridges.ftl en-US")
    assert_contains(cartridge_locale_en, "luam-sector-terminal-route-calibration-closure-bonus", "cartridges.ftl en-US")
    assert_contains(cartridge_locale_en, "luam-sector-status-route-calibration-radiation-damping", "cartridges.ftl en-US")
    assert_contains(cartridge_locale_en, "luam-sector-terminal-route-calibration-radiation-damping", "cartridges.ftl en-US")
    assert_contains(cartridge_locale_en, "luam-sector-status-route-calibration-sensor-drift-suppressed", "cartridges.ftl en-US")
    assert_contains(cartridge_locale_en, "luam-sector-terminal-route-calibration-sensor-drift-suppressed", "cartridges.ftl en-US")
    assert_contains(cartridge_locale_ru, "luam-sector-status-route-calibration-reward-bonus", "cartridges.ftl ru-RU")
    assert_contains(cartridge_locale_ru, "luam-sector-terminal-route-calibration-reward-bonus", "cartridges.ftl ru-RU")
    assert_contains(cartridge_locale_ru, "luam-sector-status-route-calibration-closure-bonus", "cartridges.ftl ru-RU")
    assert_contains(cartridge_locale_ru, "luam-sector-terminal-route-calibration-closure-bonus", "cartridges.ftl ru-RU")
    assert_contains(cartridge_locale_ru, "luam-sector-status-route-calibration-radiation-damping", "cartridges.ftl ru-RU")
    assert_contains(cartridge_locale_ru, "luam-sector-terminal-route-calibration-radiation-damping", "cartridges.ftl ru-RU")
    assert_contains(cartridge_locale_ru, "luam-sector-status-route-calibration-sensor-drift-suppressed", "cartridges.ftl ru-RU")
    assert_contains(cartridge_locale_ru, "luam-sector-terminal-route-calibration-sensor-drift-suppressed", "cartridges.ftl ru-RU")
    assert_contains(sector_dynamic_events, "дрейф сенсоров", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "опасность условия", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "Тихий аварийный маркер", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "Запрос полевого ремонта", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "Обзор артефакта Монолита", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "DispatchCooldownReductionPerReputation", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "DispatchCooldownReductionCap", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "GetDispatchCooldownReductionPercent", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "GetAdjustedCooldownRange", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "TightenScheduledAutomaticEventForDispatchProfile", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "BuildPreferredProcessUiEntries", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "TryGeneratePreferredDynamicEvent", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "BuildPreferredReputationBlockReason", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "LuaMSectorAutomationUiEntry", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "LuaMSectorPreferredProcessUiEntry", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "TryPingActiveRouteMarker", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "SetBeaconText", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "[PING", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "MonolithArtifactTemplateId", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "monolith-artifact", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "LuaMDynamicEventMarkerMonolith", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "DynamicQuestDebrisPrototype = \"LuaMDynamicQuestDebrisSmall\"", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "DynamicDebrisHostileInterval = 5", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "BuildDebrisSitePlan", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "SpawnDebrisSite", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "SpawnDebrisHostiles", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "AppendDebrisHazardContext", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "SpawnSiteObjects", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "BuildMonolithResearchReport", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "PaperLuaMMonolithResearchReport", "LuaMSectorDynamicEventSystem")
    assert_contains(sector_dynamic_events, "LuaMSectorEvidenceComponent", "LuaMSectorDynamicEventSystem")

    dynamic_event_debris_test = (ROOT / "Content.IntegrationTests/Tests/_LuaM/LuaMDynamicEventDebrisTest.cs").read_text(encoding="utf-8")
    for required_debris_test_marker in [
        "GeneratedDynamicEventsCreateIsolatedDebrisAndHostilesOnFifth",
        "DynamicDebrisHostileInterval",
        "LuaMDynamicEventDebrisComponent",
        "LuaMDynamicEventMarkerComponent",
        "LuaMDynamicEventSiteObjectComponent",
        "PaperComponent",
        "SharedTransformSystem",
        "expectedCoordinates",
        "HostileCount",
        "HostileContact",
        "GPS карта",
        "Сдать / закрыть",
        "CleanupAllDynamicMarkers",
    ]:
        assert_contains(dynamic_event_debris_test, required_debris_test_marker, "LuaMDynamicEventDebrisTest")
    for hardcoded_text in [
        "Quiet distress marker",
        "Field repair request",
        "Black box echo",
        "Monolith artifact survey",
        "Navigation drift packet",
        "Courier handoff check",
        "Ghost ledger audit",
        "Marker location:",
        "Active sector conditions:",
        "Generated for",
        "Dynamic event class:",
        "No hazard recorded.",
    ]:
        assert_not_contains(sector_dynamic_events, hardcoded_text, "LuaMSectorDynamicEventSystem")

    dynamic_marker_component = (ROOT / "Content.Server/_LuaM/Sector/LuaMDynamicEventMarkerComponent.cs").read_text(encoding="utf-8")
    assert_contains(dynamic_marker_component, "RoutePingCount", "LuaMDynamicEventMarkerComponent")
    assert_contains(dynamic_marker_component, "LastRoutePingSummary", "LuaMDynamicEventMarkerComponent")

    sector_insurance_terminal = (ROOT / "Content.Server/_LuaM/Sector/LuaMSectorInsuranceTerminalSystem.cs").read_text(encoding="utf-8")
    assert_contains(sector_insurance_terminal, "BuildInsuranceUiEntries", "LuaMSectorInsuranceTerminalSystem")
    assert_contains(sector_insurance_terminal, "TryPrintInsuranceClaimVoucherForStory", "LuaMSectorInsuranceTerminalSystem")

    sector_registry_terminal = (ROOT / "Content.Server/_LuaM/Sector/LuaMSectorRegistryTerminalSystem.cs").read_text(encoding="utf-8")
    assert_contains(sector_registry_terminal, "BuildRegistryUiEntries", "LuaMSectorRegistryTerminalSystem")
    assert_contains(sector_registry_terminal, "TryPrintCharterVoucherForStory", "LuaMSectorRegistryTerminalSystem")

    sector_evidence_system = (ROOT / "Content.Server/_LuaM/Sector/LuaMSectorEvidenceSystem.cs").read_text(encoding="utf-8")
    assert_contains(sector_evidence_system, "BlackBoxSnapshotRadius", "LuaMSectorEvidenceSystem")
    assert_contains(sector_evidence_system, "AppendGridPhysicalSnapshot", "LuaMSectorEvidenceSystem")
    assert_contains(sector_evidence_system, "AppendNearbySnapshot", "LuaMSectorEvidenceSystem")
    assert_contains(sector_evidence_system, "nearby names", "LuaMSectorEvidenceSystem")
    assert_contains(sector_evidence_system, "ApcPowerReceiverComponent", "LuaMSectorEvidenceSystem")

    sector_story_system = (ROOT / "Content.Server/_LuaM/Sector/LuaMSectorStorySystem.cs").read_text(encoding="utf-8")
    assert_contains(sector_story_system, "SourceSnapshot = Trim(sourceSnapshot, 1536)", "LuaMSectorStorySystem")

    classic_research_xaml = (ROOT / "Content.Client/Research/UI/ResearchConsoleMenu.xaml").read_text(encoding="utf-8")
    assert_contains(classic_research_xaml, 'Resizable="True"', "ResearchConsoleMenu")

    fancy_research_xaml = (ROOT / "Content.Client/_Goobstation/Research/UI/FancyResearchConsoleMenu.xaml").read_text(encoding="utf-8")
    assert_contains(fancy_research_xaml, 'Resizable="True"', "FancyResearchConsoleMenu")
    assert_contains(fancy_research_xaml, "ZoomOutButton", "FancyResearchConsoleMenu")
    assert_contains(fancy_research_xaml, "ZoomResetButton", "FancyResearchConsoleMenu")
    assert_contains(fancy_research_xaml, "ZoomInButton", "FancyResearchConsoleMenu")
    assert_contains(fancy_research_xaml, "research-console-menu-zoom-out-tooltip", "FancyResearchConsoleMenu")
    assert_contains(fancy_research_xaml, "research-console-menu-zoom-reset-tooltip", "FancyResearchConsoleMenu")
    assert_contains(fancy_research_xaml, "research-console-menu-zoom-in-tooltip", "FancyResearchConsoleMenu")

    fancy_research_code = (ROOT / "Content.Client/_Goobstation/Research/UI/FancyResearchConsoleMenu.xaml.cs").read_text(encoding="utf-8")
    assert_contains(fancy_research_code, "MouseWheel", "FancyResearchConsoleMenu")
    assert_contains(fancy_research_code, "SetZoom", "FancyResearchConsoleMenu")
    assert_contains(fancy_research_code, "ResetZoom", "FancyResearchConsoleMenu")
    assert_contains(fancy_research_code, "GetZoomFocus", "FancyResearchConsoleMenu")
    assert_contains(fancy_research_code, "LayoutResearchCards", "FancyResearchConsoleMenu")
    assert_contains(fancy_research_code, "LayoutResearchCard", "FancyResearchConsoleMenu")
    assert_contains(fancy_research_code, "UpdateZoomLabel", "FancyResearchConsoleMenu")
    assert_contains(fancy_research_code, "focus + (_position - focus) * (newZoom / oldZoom)", "FancyResearchConsoleMenu")

    fancy_research_item_xaml = (ROOT / "Content.Client/_Goobstation/Research/UI/FancyResearchConsoleItem.xaml").read_text(encoding="utf-8")
    assert_contains(fancy_research_item_xaml, 'Name="CardContainer"', "FancyResearchConsoleItem")

    fancy_research_item_code = (ROOT / "Content.Client/_Goobstation/Research/UI/FancyResearchConsoleItem.xaml.cs").read_text(encoding="utf-8")
    assert_contains(fancy_research_item_code, "public void SetZoom(float zoom)", "FancyResearchConsoleItem")
    assert_contains(fancy_research_item_code, "CardContainer.SetSize", "FancyResearchConsoleItem")
    assert_contains(fancy_research_item_code, "ResearchDisplay.SetSize", "FancyResearchConsoleItem")
    assert_contains(fancy_research_item_code, "ResearchDisplay.Scale", "FancyResearchConsoleItem")

    client_clothing = (ROOT / "Content.Client/Clothing/ClientClothingSystem.cs").read_text(encoding="utf-8")
    for slot, state in {
        "underwearb": "UNDERWEARB",
        "underweart": "UNDERWEART",
        "socks": "SOCKS",
    }.items():
        assert_contains(client_clothing, f'{{"{slot}", "{state}"}}', "ClientClothingSystem.TemporarySlotMap")
    assert_contains(client_clothing, "speciesId.ToLowerInvariant()", "ClientClothingSystem species RSI fallback")

    for rel_path in [
        "Resources/Prototypes/InventoryTemplates/arachnid_inventory_template.yml",
        "Resources/Prototypes/InventoryTemplates/corpse_inventory_template.yml",
        "Resources/Prototypes/InventoryTemplates/human_inventory_template.yml",
    ]:
        template = (ROOT / rel_path).read_text(encoding="utf-8")
        for slot in ["underwearb", "underweart", "socks"]:
            assert_contains(template, f"name: {slot}", rel_path)

    for rel_path in [
        "Resources/Prototypes/Entities/Mobs/Species/arachnid.yml",
        "Resources/Prototypes/Entities/Mobs/Species/base.yml",
    ]:
        species = (ROOT / rel_path).read_text(encoding="utf-8")
        for layer in ['"underwearb"', '"underweart"', '"socks"']:
            assert_contains(species, layer, rel_path)

    rogue_ai = (ROOT / "Resources/Prototypes/_NF/Entities/Mobs/NPCs/mob_hostile_rogue_ai.yml").read_text(encoding="utf-8")
    assert_contains(rogue_ai, "type: AiRemoteController", "mob_hostile_rogue_ai")

    for rel_path in [
        "Resources/Locale/en-US/_NF/bank/bank-ATM-component.ftl",
        "Resources/Locale/ru-RU/_NF/bank/bank-ATM-component.ftl",
    ]:
        keys = load_ftl_keys(ROOT / rel_path)
        assert_contains(keys, "bank-payroll-received", rel_path)

    prototypes, all_prototypes = collect_prototypes()

    required_ids = {
        "LuaMDeadSpaceLowPop",
        "LuaMSoloStationEventScheduler",
        "LuaMSoloBluespaceSalvageEventScheduler",
        "LuaMSoloSmugglingEventScheduler",
        "LuaMSoloCalmEventsTable",
        "LuaMSectorRumorDataset",
        "ComputerLuaMSectorRumorBoard",
        "LuaMBountyDistressClosure",
        "LuaMBountyBlackBoxRecovery",
        "LuaMBountyFieldRepair",
        "LuaMBountyNavigationPacket",
        "LuaMBlackBoxBounty",
        "LuaMDistressBeaconBounty",
        "LuaMSoloReportBounty",
        "LuaMBlackBoxRecorder",
        "LuaMDistressBeacon",
        "LuaMMonolithShard",
        "LuaMArtifactContainmentCase",
        "LuaMAnomalyScanner",
        "LuaMMonolithResonator",
        "PaperLuaMMonolithResearchReport",
        "LuaMDynamicEventMarkerMonolith",
        "LuaMDynamicEventConditionRadiationHazard",
        "LuaMDynamicEventConditionSensorDriftMarker",
        "LuaMFrontierMonolithLoadingScreen",
        "PaperLuaMSoloBountyGuide",
        "PaperLuaMSoloFallbackProtocol",
        "PaperLuaMSoloMechanicsMatrix",
        "PaperLuaMTrustedSalvageCacheManifest",
        "PaperLuaMPreferredCourierRouteReceipt",
        "PaperLuaMGhostStationLedgerExtract",
        "PaperLuaMPriorityRescueLaneReceipt",
        "ContractorLuaMSoloOperatorPacket",
        "ContractorLuaMSoloFieldKit",
        "ContractorLuaMSoloRepairKit",
        "ContractorBountyContractsCartridge",
        "ContractorNewsReaderCartridge",
        "ContractorNotekeeperCartridge",
        "ContractorLuaMSectorStatusCartridge",
        "LuaMSectorStatusCartridge",
        "LuaMRoleLadder",
        "LuaMSectorMemory",
        "LuaMSectorStorySilentTow",
        "LuaMSectorStoryLastOxygen",
        "LuaMSectorStorySealedCargo",
        "LuaMSectorStoryFalseQuiet",
        "LuaMSectorStoryBlackBoxPaid",
        "LuaMSectorStoryTrustedSalvageCache",
        "LuaMSectorStoryPreferredCourierRoute",
        "LuaMSectorStoryGhostStationLedger",
        "LuaMSectorStoryPriorityRescueLane",
    }

    missing_ids = sorted(required_ids - prototypes.keys())
    if missing_ids:
        raise AssertionError(f"missing required prototypes: {', '.join(missing_ids)}")

    underwear_expected_states = {
        "LuaMClothingUnderwearBottomBase": "equipped-UNDERWEARB",
        "LuaMClothingUnderwearTopBase": "equipped-UNDERWEART",
        "LuaMClothingUnderwearSocksBase": "equipped-SOCKS",
    }
    checked_underwear = 0
    for proto in all_prototypes:
        raw_parent = proto.get("parent")
        parents = raw_parent if isinstance(raw_parent, list) else [raw_parent]
        parent = next((candidate for candidate in parents if candidate in underwear_expected_states), None)
        if parent is None or proto.get("abstract") is True:
            continue

        proto_id = proto.get("id")
        clothing = component(proto, "Clothing")
        sprite_path = clothing.get("sprite")
        if not isinstance(sprite_path, str) or not sprite_path.strip():
            raise AssertionError(f"{proto_id}: missing underwear clothing sprite")

        states = load_rsi_state_names(sprite_path)
        expected_state = underwear_expected_states[parent]
        assert_contains(states, expected_state, f"{proto_id}.RSI")
        checked_underwear += 1

    if checked_underwear < 20:
        raise AssertionError("LuaM underwear: expected at least 20 wearable underwear/socks/top prototypes")

    rumor_dataset = prototypes["LuaMSectorRumorDataset"]
    assert_equal(rumor_dataset["type"], "dataset", "LuaMSectorRumorDataset.type")
    rumor_values = rumor_dataset["values"]
    if len(rumor_values) < 20:
        raise AssertionError("LuaMSectorRumorDataset: expected at least 20 static rumor entries")
    if len(set(rumor_values)) != len(rumor_values):
        raise AssertionError("LuaMSectorRumorDataset: duplicate rumor entries")
    for rumor in rumor_values:
        if not isinstance(rumor, str) or not rumor.strip():
            raise AssertionError("LuaMSectorRumorDataset: empty rumor entry")
        if not any("\u0400" <= char <= "\u04FF" for char in rumor):
            raise AssertionError(f"LuaMSectorRumorDataset: non-Russian rumor entry {rumor!r}")

    rumor_board = prototypes["ComputerLuaMSectorRumorBoard"]
    assert_equal(rumor_board["parent"], "ComputerMassMedia", "ComputerLuaMSectorRumorBoard.parent")
    component(rumor_board, "LuaMSectorLeadReport")
    component(rumor_board, "LuaMSectorInsuranceTerminal")
    component(rumor_board, "LuaMSectorRegistryTerminal")
    assert_equal(component(rumor_board, "ActivatableUI")["key"], "enum.LuaMSectorTerminalUiKey.Key", "ComputerLuaMSectorRumorBoard.ActivatableUI.key")
    assert_equal(
        component(rumor_board, "UserInterface")["interfaces"]["enum.LuaMSectorTerminalUiKey.Key"]["type"],
        "LuaMSectorTerminalBoundUserInterface",
        "ComputerLuaMSectorRumorBoard.UserInterface",
    )
    assert_equal(component(rumor_board, "Sprite")["layers"][2]["state"], "service", "ComputerLuaMSectorRumorBoard screen")

    status_cartridge = prototypes["LuaMSectorStatusCartridge"]
    assert_equal(status_cartridge["parent"], "NFBasePDACartridge", "LuaMSectorStatusCartridge.parent")
    assert_equal(component(status_cartridge, "Sprite")["sprite"], "_LuaM/Objects/Monolith/sector_dispatch_cartridge.rsi", "LuaMSectorStatusCartridge.Sprite")
    assert_equal(component(status_cartridge, "Icon")["sprite"], "_LuaM/Objects/Monolith/sector_dispatch_cartridge.rsi", "LuaMSectorStatusCartridge.Icon")
    assert_equal(component(status_cartridge, "UIFragment")["ui"]["__tag__"], "!type:LuaMSectorStatusUi", "LuaMSectorStatusCartridge.ui")
    assert_equal(component(status_cartridge, "Cartridge")["programName"], "luam-sector-status-program-name", "LuaMSectorStatusCartridge.programName")
    component(status_cartridge, "LuaMSectorStatusCartridge")

    lobby_screen = prototypes["LuaMFrontierMonolithLoadingScreen"]
    assert_equal(lobby_screen["type"], "lobbyBackground", "LuaMFrontierMonolithLoadingScreen.type")
    assert_equal(
        lobby_screen["background"],
        "/Textures/_LuaM/LobbyScreens/frontier_monolith_loading.png",
        "LuaMFrontierMonolithLoadingScreen.background",
    )

    colossus_lobby_screen = prototypes["ColossusLobbyScreen"]
    assert_equal(
        colossus_lobby_screen["background"],
        "/Textures/_LuaM/LobbyScreens/frontier_monolith_loading.png",
        "ColossusLobbyScreen.background",
    )

    radiation_hazard = prototypes["LuaMDynamicEventConditionRadiationHazard"]
    assert_equal(component(radiation_hazard, "Sprite")["sprite"], "_LuaM/Objects/Monolith/radiation_node.rsi", "LuaMDynamicEventConditionRadiationHazard.Sprite")
    component(radiation_hazard, "RadiationSource")

    sensor_drift_marker = prototypes["LuaMDynamicEventConditionSensorDriftMarker"]
    assert_equal(sensor_drift_marker["parent"], "DefaultStationBeaconUnanchored", "LuaMDynamicEventConditionSensorDriftMarker.parent")
    assert_equal(component(sensor_drift_marker, "Sprite")["sprite"], "_LuaM/Objects/Monolith/sensor_drift_detector.rsi", "LuaMDynamicEventConditionSensorDriftMarker.Sprite")
    component(sensor_drift_marker, "NavMapBeacon")

    dynamic_marker = prototypes["LuaMDynamicEventMarker"]
    assert_equal(component(dynamic_marker, "Sprite")["sprite"], "_LuaM/Objects/Monolith/sector_route_beacon.rsi", "LuaMDynamicEventMarker.Sprite")

    monolith_marker = prototypes["LuaMDynamicEventMarkerMonolith"]
    assert_equal(monolith_marker["parent"], "LuaMDynamicEventMarker", "LuaMDynamicEventMarkerMonolith.parent")
    assert_equal(component(monolith_marker, "NavMapBeacon")["defaultText"], "station-beacon-luam-dynamic-monolith", "LuaMDynamicEventMarkerMonolith.NavMapBeacon")

    black_box = prototypes["LuaMBlackBoxRecorder"]
    assert_equal(component(black_box, "Sprite")["sprite"], "_LuaM/Objects/Monolith/flight_recorder.rsi", "LuaMBlackBoxRecorder.Sprite")

    monolith_sprites = {
        "LuaMMonolithShard": "_LuaM/Objects/Monolith/monolith_shard.rsi",
        "LuaMArtifactContainmentCase": "_LuaM/Objects/Monolith/artifact_container.rsi",
        "LuaMAnomalyScanner": "_LuaM/Objects/Monolith/anomaly_scanner.rsi",
        "LuaMMonolithResonator": "_LuaM/Objects/Monolith/monolith_resonator.rsi",
        "PaperLuaMMonolithResearchReport": "_LuaM/Objects/Monolith/research_report.rsi",
    }
    for proto_id, sprite in monolith_sprites.items():
        assert_equal(component(prototypes[proto_id], "Sprite")["sprite"], sprite, f"{proto_id}.Sprite")
        assert_equal(component(prototypes[proto_id], "Icon")["sprite"], sprite, f"{proto_id}.Icon")

    en_station_beacon = (ROOT / "Resources/Locale/en-US/_LuaM/station-beacon/station-beacon.ftl").read_text(encoding="utf-8")
    ru_station_beacon = (ROOT / "Resources/Locale/ru-RU/_LuaM/station-beacon/station-beacon.ftl").read_text(encoding="utf-8")
    assert_contains(en_station_beacon, "station-beacon-luam-dynamic-monolith", "en-US station beacon locale")
    assert_contains(ru_station_beacon, "station-beacon-luam-dynamic-monolith", "ru-RU station beacon locale")
    assert_contains(ru_station_beacon, "динамическая зацепка Монолита", "ru-RU station beacon locale")

    sector_memory = prototypes["LuaMSectorMemory"]
    assert_equal(sector_memory["type"], "sectorService", "LuaMSectorMemory.type")
    sector_memory_components = sector_memory["components"]
    if not any(comp.get("type") == "LuaMSectorMemory" for comp in sector_memory_components):
        raise AssertionError("LuaMSectorMemory: missing LuaMSectorMemory component")

    sector_stories = [
        proto
        for proto in all_prototypes
        if proto.get("type") == "luamSectorStory"
    ]
    if len(sector_stories) < 5:
        raise AssertionError("luamSectorStory: expected at least 5 roundstart story prototypes")
    expected_story_rewards = {
        "LuaMSectorStorySilentTow": 57500,
        "LuaMSectorStoryLastOxygen": 59000,
        "LuaMSectorStorySealedCargo": 56500,
        "LuaMSectorStoryFalseQuiet": 58000,
        "LuaMSectorStoryBlackBoxPaid": 60000,
        "LuaMSectorStoryTrustedSalvageCache": 59000,
        "LuaMSectorStoryPreferredCourierRoute": 57000,
        "LuaMSectorStoryGhostStationLedger": 58000,
        "LuaMSectorStoryPriorityRescueLane": 57600,
    }
    for story in sector_stories:
        story_id = story["id"]
        for field in ["title", "news", "hazard", "insurance", "blackBox", "companyRecord", "shipRecord"]:
            value = story.get(field)
            if not isinstance(value, str) or not value.strip():
                raise AssertionError(f"{story_id}: missing {field}")
        assert_equal(story.get("contractCollection"), "Distress" if story_id != "LuaMSectorStorySealedCargo" else "Public", f"{story_id}.contractCollection")
        contract_reward = story.get("contractReward", 0)
        hazard_severity = story.get("hazardSeverity", 0)
        hazard_bonus = story.get("hazardRewardBonus", 0)
        if story_id in expected_story_rewards:
            assert_equal(contract_reward, expected_story_rewards[story_id], f"{story_id}.contractReward")
        if not isinstance(contract_reward, int) or not 30000 <= contract_reward <= 100000:
            raise AssertionError(f"{story_id}: contractReward must be 30000..100000")
        if not isinstance(hazard_severity, int) or not 1 <= hazard_severity <= 5:
            raise AssertionError(f"{story_id}: hazardSeverity must be 1..5")
        if not isinstance(hazard_bonus, int) or hazard_bonus <= 0:
            raise AssertionError(f"{story_id}: missing positive hazardRewardBonus")
        if contract_reward + hazard_bonus > 100000:
            raise AssertionError(f"{story_id}: hazard-adjusted reward too high for solo contract")
        if story.get("reputationDelta", 0) < 1:
            raise AssertionError(f"{story_id}: missing positive reputation delta")
        for field in [
            "title",
            "news",
            "contractName",
            "contractVessel",
            "contractDescription",
            "hazard",
            "insurance",
            "blackBox",
            "companyRecord",
            "shipRecord",
        ]:
            assert_has_cyrillic(story.get(field), f"{story_id}.{field}")

    gated_stories = [
        story
        for story in sector_stories
        if story.get("requiredReputation", 0) > 0
    ]
    if not gated_stories:
        raise AssertionError("luamSectorStory: expected at least one reputation-gated story")
    for story in gated_stories:
        story_id = story["id"]
        if not isinstance(story.get("requiredReputationTarget"), str) or not story["requiredReputationTarget"].strip():
            raise AssertionError(f"{story_id}: missing requiredReputationTarget")
        if story["requiredReputation"] < 1:
            raise AssertionError(f"{story_id}: requiredReputation must be positive")

    evidence_expectations = {
        "LuaMDistressBeacon": {
            "story": "LuaMSectorStorySilentTow",
            "acknowledgeHazard": True,
        },
        "PaperLuaMSalvageInsuranceForm": {
            "story": "LuaMSectorStorySealedCargo",
            "acknowledgeHazard": True,
            "claimInsurance": True,
            "registerCompany": True,
            "registerShip": True,
        },
        "PaperLuaMBlackBoxReport": {
            "story": "LuaMSectorStoryBlackBoxPaid",
            "acknowledgeHazard": True,
            "recoverBlackBox": True,
            "registerCompany": True,
            "registerShip": True,
            "resolveStory": True,
        },
        "LuaMBlackBoxRecorder": {
            "story": "LuaMSectorStoryBlackBoxPaid",
            "acknowledgeHazard": True,
            "recoverBlackBox": True,
            "resolveStory": True,
        },
        "PaperLuaMTrustedSalvageCacheManifest": {
            "story": "LuaMSectorStoryTrustedSalvageCache",
            "acknowledgeHazard": True,
            "recoverBlackBox": True,
            "registerCompany": True,
            "registerShip": True,
            "resolveStory": True,
        },
        "PaperLuaMPreferredCourierRouteReceipt": {
            "story": "LuaMSectorStoryPreferredCourierRoute",
            "acknowledgeHazard": True,
            "claimInsurance": True,
            "recoverBlackBox": True,
            "registerCompany": True,
            "registerShip": True,
            "resolveStory": True,
        },
        "PaperLuaMGhostStationLedgerExtract": {
            "story": "LuaMSectorStoryGhostStationLedger",
            "acknowledgeHazard": True,
            "claimInsurance": True,
            "recoverBlackBox": True,
            "registerCompany": True,
            "registerShip": True,
            "resolveStory": True,
        },
        "PaperLuaMPriorityRescueLaneReceipt": {
            "story": "LuaMSectorStoryPriorityRescueLane",
            "acknowledgeHazard": True,
            "claimInsurance": True,
            "recoverBlackBox": True,
            "registerCompany": True,
            "registerShip": True,
            "resolveStory": True,
        },
    }
    for proto_id, expected_fields in evidence_expectations.items():
        evidence = component(prototypes[proto_id], "LuaMSectorEvidence")
        for field, expected in expected_fields.items():
            assert_equal(evidence.get(field), expected, f"{proto_id}.{field}")
        if not isinstance(evidence.get("note"), str) or not evidence["note"].strip():
            raise AssertionError(f"{proto_id}: missing evidence note")

    distress_beacon = component(prototypes["LuaMDistressBeacon"], "LuaMDistressBeacon")
    for field in ["title", "vessel", "description", "hazard", "reputationTarget"]:
        if not isinstance(distress_beacon.get(field), str) or not distress_beacon[field].strip():
            raise AssertionError(f"LuaMDistressBeacon.{field}: missing player distress seed data")
    if distress_beacon.get("reward", 0) <= 0:
        raise AssertionError("LuaMDistressBeacon.reward: expected positive reward")

    preset = prototypes["LuaMDeadSpaceLowPop"]
    rules = preset["rules"]
    for rule in [
        "NFAdventure",
        "LuaMSoloStationEventScheduler",
        "LuaMSoloBluespaceSalvageEventScheduler",
        "LuaMSoloSmugglingEventScheduler",
        "MonoAISTCShuttleSpawnerSchedulerSlow",
        "MonoAsakimSTCShuttleSpawnerSchedulerSlow",
    ]:
        assert_contains(rules, rule, "LuaMDeadSpaceLowPop.rules")

    for rule in [
        "FrontierRoundstartVariation",
        "BluespaceSalvageEventScheduler",
        "SmugglingEventScheduler",
    ]:
        assert_not_contains(rules, rule, "LuaMDeadSpaceLowPop.rules")

    scheduler_expectations = {
        "LuaMSoloStationEventScheduler": (900, 1800, 3600),
        "LuaMSoloBluespaceSalvageEventScheduler": (3600, 5400, 7200),
        "LuaMSoloSmugglingEventScheduler": (5400, 28800, 43200),
    }
    for scheduler_id, expected in scheduler_expectations.items():
        comp = component(prototypes[scheduler_id], "BasicStationEventScheduler")
        actual = (
            comp["minimumTimeUntilFirstEvent"],
            comp["minMaxEventTiming"]["min"],
            comp["minMaxEventTiming"]["max"],
        )
        assert_equal(actual, expected, scheduler_id)

    low_pop_config = tomllib.loads(
        (ROOT / "Resources/ConfigPresets/_LuaM/deadSpaceLowPop.toml").read_text(encoding="utf-8")
    )
    assert_equal(low_pop_config["game"]["defaultpreset"], "LuaMDeadSpaceLowPop", "defaultpreset")
    assert_equal(low_pop_config["game"]["role_timer_override"], "LuaMRoleLadder", "role_timer_override")
    assert_equal(low_pop_config["game"]["lobbyduration"], 30, "lobbyduration")
    assert_equal(low_pop_config["vote"]["preset_autovote_enabled"], False, "preset_autovote_enabled")
    assert_equal(low_pop_config["status"]["connectaddress"], "udp://188.127.225.57:1212", "connectaddress")
    assert_equal(low_pop_config["admin"]["admins_count_in_playercount"], True, "admins_count_in_playercount")
    assert_equal(low_pop_config["luam"]["ai_director"]["admin_mode"], True, "low-pop luam.ai_director.admin_mode")
    assert_equal(low_pop_config["luam"]["ai_director"]["local_bridge_enabled"], False, "low-pop luam.ai_director.local_bridge_enabled")
    assert_equal(low_pop_config["luam"]["ai_director"]["local_bridge_unsafe_actions_enabled"], False, "low-pop luam.ai_director.local_bridge_unsafe_actions_enabled")
    assert_equal(low_pop_config["luam"]["ai_director"]["world_pulse_enabled"], True, "low-pop luam.ai_director.world_pulse_enabled")
    assert_equal(low_pop_config["luam"]["ai_director"]["max_danger"], True, "low-pop luam.ai_director.max_danger")
    assert_equal(low_pop_config["luam"]["ai_director"]["initial_delay"], 60, "low-pop luam.ai_director.initial_delay")
    assert_equal(low_pop_config["luam"]["ai_director"]["interval"], 300, "low-pop luam.ai_director.interval")
    assert_equal(low_pop_config["luam"]["ai_director"]["world_pulse_interval"], 60, "low-pop luam.ai_director.world_pulse_interval")
    assert_equal(low_pop_config["events"]["enabled"], True, "low-pop events.enabled")
    assert_equal(low_pop_config["gateway"]["generator_enabled"], True, "low-pop gateway.generator_enabled")
    assert_equal(low_pop_config["luam"]["sector"]["all_hazards_enabled"], True, "low-pop luam.sector.all_hazards_enabled")
    assert_equal(low_pop_config["nf14"]["worldgen"]["market_stations"], 1, "market_stations")
    assert_equal(low_pop_config["nf14"]["worldgen"]["cargo_depots"], 4, "cargo_depots")
    assert_equal(low_pop_config["nf14"]["worldgen"]["optional_stations"], 1, "optional_stations")
    assert_equal(low_pop_config["mono"]["cleanup"]["log"], False, "mono.cleanup.log")

    remote_config = tomllib.loads((ROOT / "server_config.remote.toml").read_text(encoding="utf-8"))
    remote_ai_director = remote_config["luam"]["ai_director"]
    assert_equal(remote_ai_director["enabled"], False, "remote luam.ai_director.enabled")
    assert_equal(remote_ai_director["gateway_url"], "http://127.0.0.1:8787/propose_event", "remote luam.ai_director.gateway_url")
    assert_equal(remote_ai_director["fallback_enabled"], True, "remote luam.ai_director.fallback_enabled")
    assert_equal(remote_ai_director["admin_mode"], True, "remote luam.ai_director.admin_mode")
    assert_equal(remote_ai_director["greet_on_join"], False, "remote luam.ai_director.greet_on_join")
    assert_equal(remote_ai_director["world_pulse_enabled"], False, "remote luam.ai_director.world_pulse_enabled")
    assert_equal(remote_ai_director["max_danger"], False, "remote luam.ai_director.max_danger")
    assert_equal(remote_ai_director["initial_delay"], 120, "remote luam.ai_director.initial_delay")
    assert_equal(remote_ai_director["interval"], 900, "remote luam.ai_director.interval")
    assert_equal(remote_ai_director["world_pulse_interval"], 300, "remote luam.ai_director.world_pulse_interval")
    assert_equal(remote_ai_director["request_timeout"], 25, "remote luam.ai_director.request_timeout")
    assert_equal(remote_config["events"]["enabled"], True, "remote events.enabled")
    assert_equal(remote_config["gateway"]["generator_enabled"], True, "remote gateway.generator_enabled")
    assert_equal(remote_config["luam"]["sector"]["all_hazards_enabled"], True, "remote luam.sector.all_hazards_enabled")

    rescue_agent_system = (ROOT / "Content.Server/_LuaM/Rescue/LuaMRescueAgentSystem.cs").read_text(encoding="utf-8")
    assert_contains(rescue_agent_system, 'Command => "luam_rescue_agent"', "LuaMRescueAgentCommand")
    assert_contains(rescue_agent_system, 'Command => "luam_rescue_action"', "LuaMRescueActionCommand")
    assert_contains(rescue_agent_system, 'Command => "luam_rescue_order"', "LuaMRescueOrderCommand")
    assert_contains(rescue_agent_system, 'Command => "luam_rescue_status"', "LuaMRescueStatusCommand")
    assert_contains(rescue_agent_system, "MindSystem", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "ControlMob(controller.UserId, agent)", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryOrderAgent", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryOrderPlayerAction", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "UpdatePendingPlayerAction", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryExecutePlayerAction", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "LuaMRescueActionCommand", "LuaMRescueActionCommand")
    assert_contains(rescue_agent_system, "<interact|alt|use|treat|vend|pickup|drop|pull|stop-pull|buckle|unbuckle|equip-slot|unequip-slot|store-slot|take-storage|take-target-storage|clear>", "LuaMRescueActionCommand")
    assert_contains(rescue_agent_system, "slot=<inventorySlot>", "LuaMRescueActionCommand")
    assert_contains(rescue_agent_system, "item=<name|prototype|entity>", "LuaMRescueActionCommand")
    assert_contains(rescue_agent_system, "InteractUsing", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryPickupAnyHand", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryDrop", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryStartPull", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryStopPull", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryBuckle", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryUnbuckle", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryResolveUnbuckleTarget", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "InventorySystem", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "SharedStorageSystem", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "StorageComponent", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "HealingComponent", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "HealthAnalyzerComponent", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "HyposprayComponent", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryResolveStorageSlot", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TrySelectStoredItem", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryFindTreatmentItem", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TrySelectTreatmentItem", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryGetHealingItemTargetScore", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "matchedHealing", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "matchedDamage", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryAutoAnalyzeTarget", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryFindHealthAnalyzerItem", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TrySelectHealthAnalyzerItem", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "WasTargetRecentlyAnalyzed", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "PruneAnalyzedTargets", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryAutoTreatTarget", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "includeDiagnosticItems: false", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryStartAutoPickupNearbyMedicalSupply", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryFindNearbyMedicalSupply", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryAutoStowHeldItemForTreatment", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryStoreHeldItemInStorage", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "EnumerateHandsForAutoStow", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "AutoAnalyzeBeforeTreatment", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "AutoUnbucklePatientsForEvacuation", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "AutoPickupNearbyMedicalSupplies", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "AutoStowHeldItemsForTreatment", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TemporarilySkipFailedSupplies", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "SupplySkipSeconds", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "AutoAnalyzeCooldown", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "AutoPickupSupplyRange", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "VendingMachineSystem", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryStartAutoResupplyFromVending", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryFindMedicalVendingSupply", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "UpdatePendingVendingAction", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryStartVendingProduct", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TrySelectVendingProduct", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryRetrieveDispensedVendingProduct", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryFindDispensedVendingProduct", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "PendingVendingDispensedItem", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "moving to vended", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "PruneSkippedSupplyTargets", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "IsSupplyTemporarilySkipped", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TemporarilySkipSupplyTarget", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "AuthorizedVend", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "GetAvailableInventory", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryEquip", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryUnequip", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "treat", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "vend", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "equip-slot", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "unequip-slot", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "store-slot", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "take-storage", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "LuaMRescueOrderCommand", "LuaMRescueOrderCommand")
    assert_contains(rescue_agent_system, "agent=<entity|", "LuaMRescueOrderCommand")
    assert_contains(rescue_agent_system, "target=<entity|player>", "LuaMRescueOrderCommand")
    assert_contains(rescue_agent_system, "ordered to rescue", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "ordered to follow", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "cleared current rescue order", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "BuildRescueStatusLines", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "BuildRescueStatusLine", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "GetRescuePhase", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "FormatProgress", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "StandbyAtAssignedShuttle", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "phase=", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "autoAnalyze=", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "autoTreat=", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "autoEvac=", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "autoSupply=", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "BuildRescueTeamStatusLines", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "skippedSupply=", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "standby-on-shuttle", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "standby-return-to-shuttle", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "skipped=", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "stall=", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "NPCBlackboard.FollowTarget", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "new EntityCoordinates(target, Vector2.Zero)", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "UpdateAssignedTarget", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryFindRescueTarget", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryFindEvacuationTarget", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryStartOrContinueEvacuation", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "SetFollowShuttle", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryRouteShuttleHome", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "PullingSystem", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryStartPull", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryStopPull", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "BuckleSystem", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "PathfindingSystem", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryGetNavigationSelectionPenalty", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "GetPoly", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "navPenalty", "LuaMRescueAgentSystem")
    rescue_agent_component = (ROOT / "Content.Server/_LuaM/Rescue/LuaMRescueAgentComponent.cs").read_text(encoding="utf-8")
    assert_contains(rescue_agent_component, "LuaMRescueTaskStage", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "TaskPatientTarget", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "TaskSupplyTarget", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_system, "TryResumeRememberedPatientTask", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "returning to patient", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "taskStage", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryRouteShuttleToTarget", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "HasPendingEvacuationTarget", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "PruneSkippedTargets", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "IsTargetTemporarilySkipped", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "UpdateTargetProgress", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TemporarilySkipTarget", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "ResetTargetProgress", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryBucklePatientToStrap", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryAutoUnbucklePatientForEvacuation", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryFindPatientDeliveryStrap", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "SetFollowDeliveryStrap", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryStartAutoTakeNearbyStoredMedicalSupply", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryFindNearbyStoredMedicalSupply", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryTakeItemFromTargetStorage", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "StorageInteractAttemptEvent", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TakeTargetStorage", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryAutoStoreCollectedMedicalSupply", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "kept collected supply", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TryHandleStalledDeliveryTarget", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "TemporarilySkipDeliveryTarget", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "IsDeliveryTargetTemporarilySkipped", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "skippedDelivery=", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "falling back to shuttle delivery", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "IsEvacuationComplete", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "StasisBedComponent", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "HealOnBuckleComponent", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "StrapComponent", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "BuckleComponent", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "ShuttleConsoleComponent", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "AutopilotTargetKey", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "ShuttleReturnRouted = true", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "ShuttleRoutedTarget = target", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "IsRescueCandidate", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "IsEvacuationCandidate", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "IsOnAssignedShuttle", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "MobState.Critical", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "DamageableComponent", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "PullableComponent", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "InjectableSolutionComponent", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "MedibotComponent", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "ActorComponent", "LuaMRescueAgentSystem")
    assert_contains(rescue_agent_system, "AdminFlags.Server", "LuaMRescueAgentCommand")
    assert_contains(rescue_agent_system, "No LuaM rescue agents are active", "LuaMRescueStatusCommand")

    assert_contains(rescue_agent_component, "AssignedShuttle", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "AssignedShuttleAnchor", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "AssignedPatientStrap", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "AssignedShuttleConsole", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "AssignedReturnTarget", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "ShuttleRoutedTarget", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "EvacuatingTarget", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "AutoAcquireTargets", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "EvacuateTargetsToShuttle = true", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "BucklePatientsOnShuttle = true", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "AutoUnbucklePatientsForEvacuation = true", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "PreferStasisBedDelivery = true", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "AutoTreatWithCarriedItems = true", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "AutoAnalyzeBeforeTreatment = true", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "AutoPickupNearbyMedicalSupplies = true", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "AutoTakeNearbyStoredMedicalSupplies = true", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "AutoStowHeldItemsForTreatment = true", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "AutoStoreCollectedMedicalSupplies = true", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "AutoResupplyFromVending = true", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "TemporarilySkipFailedSupplies = true", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "TemporarilySkipFailedDeliveryTargets = true", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "AutoRouteShuttleToTargets = true", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "AutoReturnShuttle = true", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "TemporarilySkipStalledTargets = true", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "ShuttleReturnRouted", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "SearchRange = 32f", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "TargetRefreshInterval = 2f", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "EvacuationStartRange = 1.5f", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "EvacuationArrivalRange = 3f", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "EvacuationMinDamage = 50f", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "TargetStallSeconds = 20f", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "TargetSkipSeconds = 45f", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "SupplySkipSeconds = 30f", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "DeliverySkipSeconds = 30f", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "AutoTreatMinDamage = 5f", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "AutoTreatCooldown = 6f", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "AutoAnalyzeCooldown = 45f", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "AutoPickupSupplyRange = 12f", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "AutoResupplyRange = 24f", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "AnalyzedTargets", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "NextAutoTreatmentAttempt", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "NextAutoAnalyzeAttempt", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "LastAutoTreatmentStatus", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "LastAutoAnalyzeStatus", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "LastAutoEvacuationStatus", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "LastAutoSupplyStatus", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "PendingVendingStarted", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "PendingVendingProduct", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "PendingVendingDispensedItem", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "PendingStorageTakenItem", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "SkippedTargets", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "SkippedSupplyTargets", "LuaMRescueAgentComponent")
    assert_contains(rescue_agent_component, "SkippedDeliveryTargets", "LuaMRescueAgentComponent")

    rescue_team_component = (ROOT / "Content.Server/_LuaM/Rescue/LuaMRescueTeamComponent.cs").read_text(encoding="utf-8")
    assert_contains(rescue_team_component, "LuaMRescueTeamComponent", "LuaMRescueTeamComponent")
    assert_contains(rescue_team_component, "LuaMRescueEscortComponent", "LuaMRescueEscortComponent")
    assert_contains(rescue_team_component, "LuaMRescueEscortRole", "LuaMRescueEscortRole")
    assert_contains(rescue_team_component, "Tourniquet", "LuaMRescueEscortRole")
    assert_contains(rescue_team_component, "Kostyl", "LuaMRescueEscortRole")
    assert_contains(rescue_team_component, "Zaslon", "LuaMRescueEscortRole")
    assert_contains(rescue_team_component, "LuaMRescueTeamPhase", "LuaMRescueTeamPhase")
    assert_contains(rescue_team_component, "LuaMRescueEscortDuty", "LuaMRescueEscortDuty")

    rescue_team_system = (ROOT / "Content.Server/_LuaM/Rescue/LuaMRescueTeamSystem.cs").read_text(encoding="utf-8")
    assert_contains(rescue_team_system, "SpawnEscortTeam", "LuaMRescueTeamSystem")
    assert_contains(rescue_team_system, "BuildRescueTeamStatusLines", "LuaMRescueTeamSystem")
    assert_contains(rescue_team_system, "UpdateEscortDuty", "LuaMRescueTeamSystem")
    assert_contains(rescue_team_system, "GetEscortDuty", "LuaMRescueTeamSystem")
    assert_contains(rescue_team_system, "SetEscortFollowTarget", "LuaMRescueTeamSystem")
    assert_contains(rescue_team_system, "TrySendInGameICMessage", "LuaMRescueTeamSystem")
    assert_contains(rescue_team_system, "Медицинская зона", "LuaMRescueTeamSystem")
    assert_contains(rescue_team_system, "Пациент внутри периметра", "LuaMRescueTeamSystem")

    rescue_shuttle_system = (ROOT / "Content.Server/_LuaM/Rescue/LuaMRescueShuttleSystem.cs").read_text(encoding="utf-8")
    assert_contains(rescue_shuttle_system, 'DefaultVessel = "Triage"', "LuaMRescueShuttleSystem")
    assert_contains(rescue_shuttle_system, 'Command => "luam_rescue_shuttle"', "LuaMRescueShuttleCommand")
    assert_contains(rescue_shuttle_system, "TryPurchaseShuttle", "LuaMRescueShuttleSystem")
    assert_contains(rescue_shuttle_system, "VesselPrototype", "LuaMRescueShuttleCommand")
    assert_contains(rescue_shuttle_system, "ShuttleConsoleComponent", "LuaMRescueShuttleSystem")
    assert_contains(rescue_shuttle_system, "HTNComponent", "LuaMRescueShuttleSystem")
    assert_contains(rescue_shuttle_system, "TrySetAutopilotTarget", "LuaMRescueShuttleSystem")
    assert_contains(rescue_shuttle_system, "TryFindAutopilotConsole", "LuaMRescueShuttleSystem")
    assert_contains(rescue_shuttle_system, "TryFindStationReturnTarget", "LuaMRescueShuttleSystem")
    assert_contains(rescue_shuttle_system, "GetLargestGrid", "LuaMRescueShuttleSystem")
    assert_contains(rescue_shuttle_system, "AutopilotTargetKey", "LuaMRescueShuttleSystem")
    assert_contains(rescue_shuttle_system, "AutopilotRotationKey", "LuaMRescueShuttleSystem")
    assert_contains(rescue_shuttle_system, "WakeNPC(console, htn)", "LuaMRescueShuttleSystem")
    assert_contains(rescue_shuttle_system, "new EntityCoordinates(target, Vector2.Zero)", "LuaMRescueShuttleSystem")
    assert_contains(rescue_shuttle_system, "NoAutopilotFlag", "LuaMRescueShuttleCommand")
    assert_contains(rescue_shuttle_system, "autopilotConsole", "LuaMRescueShuttleCommand")
    assert_contains(rescue_shuttle_system, "rescue.AssignedShuttle = shuttle", "LuaMRescueShuttleSystem")
    assert_contains(rescue_shuttle_system, "rescue.AssignedShuttleAnchor = anchor", "LuaMRescueShuttleSystem")
    assert_contains(rescue_shuttle_system, "rescue.AssignedShuttleConsole = autopilotConsole", "LuaMRescueShuttleSystem")
    assert_contains(rescue_shuttle_system, "rescue.AssignedReturnTarget = returnTarget", "LuaMRescueShuttleSystem")
    assert_contains(rescue_shuttle_system, "SpawnEscortTeam", "LuaMRescueShuttleSystem")
    assert_contains(rescue_shuttle_system, "NoTeamFlag", "LuaMRescueShuttleCommand")
    assert_contains(rescue_shuttle_system, "autonomous escorts", "LuaMRescueShuttleSystem")
    assert_contains(rescue_shuttle_system, "LuaM Rescue", "LuaMRescueShuttleSystem")
    assert_contains(rescue_shuttle_system, "AdminFlags.Server", "LuaMRescueShuttleCommand")

    triage_vessels = [
        proto
        for proto in all_prototypes
        if proto.get("id") == "Triage" and proto.get("type") == "vessel"
    ]
    assert_equal(len(triage_vessels), 1, "Triage vessel prototype count")
    triage_vessel = triage_vessels[0]
    assert_equal(triage_vessel["type"], "vessel", "Triage.type")
    assert_equal(triage_vessel["group"], "Medical", "Triage.group")
    assert_equal(triage_vessel["shuttlePath"], "/Maps/_Mono/Shuttles/triage.yml", "Triage.shuttlePath")
    if not (ROOT / "Resources/Maps/_Mono/Shuttles/triage.yml").exists():
        raise AssertionError("Triage.shuttlePath: missing map file")

    rescue_agent = prototypes["LuaMRescueAgent"]
    assert_equal(rescue_agent["parent"], "MobHuman", "LuaMRescueAgent.parent")
    assert_equal(component(rescue_agent, "LuaMRescueAgent")["type"], "LuaMRescueAgent", "LuaMRescueAgent.component")
    assert_equal(component(rescue_agent, "Loadout")["prototypes"], ["ParamedicGear"], "LuaMRescueAgent.loadout")
    assert_equal(component(rescue_agent, "NpcFactionMember")["factions"], ["NanoTrasen"], "LuaMRescueAgent.faction")
    component(rescue_agent, "InputMover")
    component(rescue_agent, "MobMover")
    medibot = component(rescue_agent, "Medibot")
    assert_equal(medibot["treatments"]["Alive"]["reagent"], "Tricordrazine", "LuaMRescueAgent.medibot.alive.reagent")
    assert_equal(medibot["treatments"]["Critical"]["reagent"], "Inaprovaline", "LuaMRescueAgent.medibot.critical.reagent")
    htn = component(rescue_agent, "HTN")
    assert_equal(htn["rootTask"]["task"], "LuaMRescueCompound", "LuaMRescueAgent.htn.rootTask")
    assert_equal(htn["blackboard"]["NavInteract"], True, "LuaMRescueAgent.htn.NavInteract")
    assert_contains(htn["blackboard"], "MedibotInjectRange", "LuaMRescueAgent.htn.blackboard")

    rescue_escort = prototypes["LuaMRescueEscort"]
    assert_equal(rescue_escort["parent"], "MobHuman", "LuaMRescueEscort.parent")
    assert_equal(component(rescue_escort, "LuaMRescueEscort")["type"], "LuaMRescueEscort", "LuaMRescueEscort.component")
    assert_equal(component(rescue_escort, "Loadout")["prototypes"], ["ParamedicGear"], "LuaMRescueEscort.loadout")
    assert_equal(component(rescue_escort, "NpcFactionMember")["factions"], ["NanoTrasen"], "LuaMRescueEscort.faction")
    component(rescue_escort, "InputMover")
    component(rescue_escort, "MobMover")
    escort_htn = component(rescue_escort, "HTN")
    assert_equal(escort_htn["rootTask"]["task"], "LuaMRescueCompound", "LuaMRescueEscort.htn.rootTask")
    assert_equal(escort_htn["blackboard"]["NavInteract"], True, "LuaMRescueEscort.htn.NavInteract")

    rescue_compound = prototypes["LuaMRescueCompound"]
    assert_equal(rescue_compound["type"], "htnCompound", "LuaMRescueCompound.type")
    rescue_branches = rescue_compound["branches"]
    assert_equal(len(rescue_branches), 3, "LuaMRescueCompound.branchCount")
    assert_equal(rescue_branches[0]["tasks"][0]["task"], "InjectNearbyCompound", "LuaMRescueCompound.injectBranch")
    assert_equal(rescue_branches[1]["preconditions"][0]["key"], "FollowTarget", "LuaMRescueCompound.followPrecondition")
    assert_equal(rescue_branches[1]["tasks"][0]["task"], "FollowCompound", "LuaMRescueCompound.followBranch")
    assert_equal(rescue_branches[2]["tasks"][0]["task"], "IdleCompound", "LuaMRescueCompound.idleBranch")

    allowed_low_pop_pois = {"CargoDepot", "CargoDepotAlt", "TradeMall", "Medical", "Edison", "Tinnia"}
    actual_low_pop_pois: set[str] = set()
    for proto in all_prototypes:
        presets = proto.get("spawnGamePreset")
        if isinstance(presets, list) and "LuaMDeadSpaceLowPop" in presets:
            proto_id = proto.get("id")
            if isinstance(proto_id, str):
                actual_low_pop_pois.add(proto_id)

    assert_equal(actual_low_pop_pois, allowed_low_pop_pois, "LuaMDeadSpaceLowPop POI set")

    bounties = [
        prototypes["LuaMBountyDistressClosure"],
        prototypes["LuaMBountyBlackBoxRecovery"],
        prototypes["LuaMBountyFieldRepair"],
        prototypes["LuaMBountyNavigationPacket"],
    ]
    expected_bounty_rewards = {
        "LuaMBountyDistressClosure": 59000,
        "LuaMBountyBlackBoxRecovery": 62000,
        "LuaMBountyFieldRepair": 60000,
        "LuaMBountyNavigationPacket": 58000,
    }
    for bounty in bounties:
        assert_equal(bounty["idPrefix"], "DS", f"{bounty['id']}.idPrefix")
        assert_equal(bounty["reward"], expected_bounty_rewards[bounty["id"]], f"{bounty['id']}.reward")
        if not 30000 <= bounty["reward"] <= 100000:
            raise AssertionError(f"{bounty['id']}: reward must be 30000..100000")
        entries = bounty["entries"]
        has_report = any(
            "LuaMSoloReportBounty" in entry.get("whitelist", {}).get("tags", [])
            for entry in entries
        )
        if not has_report:
            raise AssertionError(f"{bounty['id']}: missing solo report proof")

    nav_entries = prototypes["LuaMBountyNavigationPacket"]["entries"]
    nav_tag_sets = [
        set(entry.get("whitelist", {}).get("tags", []))
        for entry in nav_entries
    ]
    assert_contains(nav_tag_sets, {"GPS"}, "LuaMBountyNavigationPacket")
    assert_contains(nav_tag_sets, {"HandicommsBounty"}, "LuaMBountyNavigationPacket")

    distress_beacon_contract = component(prototypes["LuaMDistressBeacon"], "LuaMDistressBeacon")
    if not 30000 <= distress_beacon_contract.get("reward", 0) <= 100000:
        raise AssertionError("LuaMDistressBeacon.reward must be 30000..100000")
    for field in ["title", "vessel", "description", "hazard"]:
        assert_has_cyrillic(distress_beacon_contract.get(field), f"LuaMDistressBeacon.{field}")
    assert_has_cyrillic(component(prototypes["LuaMDistressBeacon"], "LuaMSectorEvidence").get("note"), "LuaMDistressBeacon.LuaMSectorEvidence.note")

    operator_items = prototypes["ContractorLuaMSoloOperatorPacket"]["storage"]["back"]
    assert_equal(len(operator_items), 12, "ContractorLuaMSoloOperatorPacket item count")
    for item_id in operator_items:
        assert_contains(prototypes, item_id, "ContractorLuaMSoloOperatorPacket item")

    for group_id, loadout_id in {
        "ContractorUtility": "ContractorLuaMSoloFieldKit",
        "ContractorFun": "ContractorLuaMSoloRepairKit",
        "ContractorBureaucracy": "ContractorLuaMSoloOperatorPacket",
    }.items():
        assert_contains(prototypes[group_id]["loadouts"], loadout_id, group_id)
    assert_contains(prototypes["ContractorCartridge"]["loadouts"], "ContractorBountyContractsCartridge", "ContractorCartridge")
    assert_contains(prototypes["ContractorCartridge"]["loadouts"], "ContractorLuaMSectorStatusCartridge", "ContractorCartridge")
    assert_contains(prototypes["ContractorLuaMSectorStatusCartridge"]["storage"]["back"], "LuaMSectorStatusCartridge", "ContractorLuaMSectorStatusCartridge")

    role_ladder = prototypes["LuaMRoleLadder"]["jobs"]
    expected_overall_role_times = {
        "Contractor": 0,
        "NFJanitor": 0,
        "CCServiceWorker": 0,
        "MailCarrier": 0,
        "USSPRifleman": 0,
        "Pilot": 3600,
        "MdMedic": 3600,
        "Mercenary": 10800,
        "SecurityGuard": 10800,
        "USSPMedic": 10800,
        "USSPCorporal": 21600,
        "Deputy": 21600,
        "Pirate": 21600,
        "TsfEngineer": 36000,
        "Brigmedic": 36000,
        "Borg": 43200,
        "USSPSergeant": 50400,
        "Bailiff": 50400,
        "SeniorOfficer": 64800,
        "StationTrafficController": 64800,
        "PirateFirstMate": 79200,
        "PDVInfiltrator": 79200,
        "PdvBorg": 79200,
        "TsfBorg": 79200,
        "StationRepresentative": 93600,
        "PDVDenasvar": 108000,
        "USSPCommissar": 108000,
        "DirectorOfCare": 126000,
        "Sheriff": 144000,
        "PirateCaptain": 144000,
    }
    expected_role_requirements = {
        "USSPCorporal": ("JobUSSPRifleman", 3600),
        "Deputy": ("JobSecurityGuard", 3600),
        "Pirate": ("JobMercenary", 3600),
        "TsfEngineer": ("JobPilot", 7200),
        "Brigmedic": ("JobSecurityOfficer", 7200),
        "USSPSergeant": ("JobUSSPCorporal", 7200),
        "Bailiff": ("JobSecurityOfficer", 7200),
        "SeniorOfficer": ("JobSecurityOfficer", 10800),
        "StationTrafficController": ("JobPilot", 10800),
        "PirateFirstMate": ("JobPirate", 14400),
        "PDVInfiltrator": ("JobPirate", 14400),
        "PdvBorg": ("JobPirate", 14400),
        "TsfBorg": ("JobTsfEngineer", 14400),
        "StationRepresentative": ("JobStc", 14400),
        "PDVDenasvar": ("JobPirate", 21600),
        "USSPCommissar": ("JobUSSPSergeant", 14400),
        "DirectorOfCare": ("MdMedic", 21600),
        "Sheriff": ("JobSeniorOfficer", 21600),
        "PirateCaptain": ("JobPirateFirstMate", 21600),
    }
    assert_equal(set(role_ladder), set(expected_overall_role_times), "LuaMRoleLadder role set")
    for job_id, expected_time in expected_overall_role_times.items():
        overall_requirement = next(
            requirement
            for requirement in role_ladder[job_id]
            if "OverallPlaytimeRequirement" in requirement.get("__tag__", "")
        )
        assert_equal(overall_requirement["time"], expected_time, f"LuaMRoleLadder.{job_id}.overall")

        role_requirement = next(
            (
                requirement
                for requirement in role_ladder[job_id]
                if "RoleTimeRequirement" in requirement.get("__tag__", "")
            ),
            None,
        )
        expected_role_requirement = expected_role_requirements.get(job_id)
        if expected_role_requirement is None:
            if role_requirement is not None:
                raise AssertionError(f"LuaMRoleLadder.{job_id}: unexpected role-time requirement")
        else:
            if role_requirement is None:
                raise AssertionError(f"LuaMRoleLadder.{job_id}: missing role-time requirement")
            expected_role, expected_role_time = expected_role_requirement
            assert_equal(role_requirement.get("role"), expected_role, f"LuaMRoleLadder.{job_id}.role")
            assert_equal(role_requirement.get("time"), expected_role_time, f"LuaMRoleLadder.{job_id}.roleTime")

    job_ids, play_time_trackers = collect_job_metadata()
    for job_id, requirements in role_ladder.items():
        assert_contains(job_ids, job_id, "LuaMRoleLadder job")
        for requirement in requirements:
            if not isinstance(requirement, dict):
                continue

            tag = requirement.get("__tag__", "")
            role = requirement.get("role")
            if "RoleTimeRequirement" in tag and isinstance(role, str):
                assert_contains(play_time_trackers, role, f"LuaMRoleLadder.{job_id}.role")

    bounty_locale_keys = {
        "luam-bounty-name-distress-beacon",
        "luam-bounty-name-solo-report",
        "luam-bounty-name-black-box",
        "luam-bounty-name-repair-tool",
        "luam-bounty-name-navigation-gps",
        "luam-bounty-name-navigation-comms",
        "luam-bounty-desc-distress-closure",
        "luam-bounty-desc-black-box",
        "luam-bounty-desc-field-repair",
        "luam-bounty-desc-navigation",
    }
    for rel_path in [
        "Resources/Locale/en-US/_LuaM/cargo/solo-bounties.ftl",
        "Resources/Locale/ru-RU/_LuaM/cargo/solo-bounties.ftl",
    ]:
        keys = load_ftl_keys(ROOT / rel_path)
        missing = sorted(bounty_locale_keys - keys)
        if missing:
            raise AssertionError(f"{rel_path}: missing locale keys {', '.join(missing)}")

    for rel_path in [
        "Resources/Locale/en-US/cargo/cargo-bounty-console.ftl",
        "Resources/Locale/ru-RU/cargo/cargo-bounty-console.ftl",
    ]:
        keys = load_ftl_keys(ROOT / rel_path)
        missing = {"bounty-manifest-description", "bounty-manifest-route-hint"} - keys
        if missing:
            raise AssertionError(f"{rel_path}: missing bounty route hint locale keys {', '.join(sorted(missing))}")

    ru_solo_bounties = (ROOT / "Resources/Locale/ru-RU/_LuaM/cargo/solo-bounties.ftl").read_text(encoding="utf-8")
    for key in bounty_locale_keys:
        prefix = f"{key} ="
        line = next((line for line in ru_solo_bounties.splitlines() if line.startswith(prefix)), "")
        assert_has_cyrillic(line.split("=", 1)[1].strip() if "=" in line else "", f"ru-RU {key}")

    bounty_contract_locale_keys = {
        "bounty-contracts-ui-list-accept",
        "bounty-contracts-ui-list-release",
        "bounty-contracts-ui-list-accepted",
        "bounty-contracts-ui-list-accept-tooltip",
        "bounty-contracts-ui-list-release-tooltip",
        "bounty-contracts-ui-list-accepted-tooltip",
        "bounty-contracts-ui-list-remove-tooltip",
        "bounty-contracts-ui-list-remove-disabled-tooltip",
        "bounty-contracts-ui-list-accepted-by",
        "bounty-contracts-ui-list-unaccepted",
    }
    for rel_path in [
        "Resources/Locale/en-US/_NF/bounty-contracts/bounty-contracts.ftl",
        "Resources/Locale/ru-RU/_NF/bounty-contracts/bounty-contracts.ftl",
    ]:
        keys = load_ftl_keys(ROOT / rel_path)
        missing = sorted(bounty_contract_locale_keys - keys)
        if missing:
            raise AssertionError(f"{rel_path}: missing locale keys {', '.join(missing)}")

    ru_bounty_contracts = (ROOT / "Resources/Locale/ru-RU/_NF/bounty-contracts/bounty-contracts.ftl").read_text(encoding="utf-8")
    assert_contains(ru_bounty_contracts, "bounty-contracts-ui-list-remove = Удалить", "ru-RU bounty contracts remove button")

    cartridge_locale_keys = {
        "luam-sector-status-program-name",
        "luam-sector-status-header",
        "luam-sector-status-summary",
        "luam-sector-status-quests",
        "luam-sector-status-no-quests",
        "luam-sector-status-quests-hint",
        "luam-sector-status-quest-header",
        "luam-sector-status-quest-step-location",
        "luam-sector-status-quest-step-action",
        "luam-sector-status-quest-step-finish",
        "luam-sector-status-quest-step-reward",
        "luam-sector-status-quest-title",
        "luam-sector-status-quest-objective",
        "luam-sector-status-quest-location",
        "luam-sector-status-quest-turnin",
        "luam-sector-status-quest-reward",
        "luam-sector-status-automation",
        "luam-sector-status-automation-next",
        "luam-sector-status-dispatch-profile",
        "luam-sector-status-sector-map",
        "luam-sector-status-no-sector-map",
        "luam-sector-status-sector-map-title",
        "luam-sector-status-sector-map-entry",
        "luam-sector-status-locked",
        "luam-sector-status-no-locked",
        "luam-sector-status-locked-entry",
        "luam-sector-status-conditions",
        "luam-sector-status-no-conditions",
        "luam-sector-status-condition-title",
        "luam-sector-status-preferred",
        "luam-sector-status-no-preferred",
        "luam-sector-status-preferred-title",
        "luam-sector-status-preferred-entry",
        "luam-sector-status-preferred-open",
        "luam-sector-status-preferred-locked",
        "luam-sector-status-hazards",
        "luam-sector-status-reputation",
        "luam-sector-status-history",
        "luam-sector-status-no-hazards",
        "luam-sector-status-no-reputation",
        "luam-sector-status-no-history",
        "luam-sector-status-reputation-entry",
        "luam-sector-status-reputation-effect-entry",
        "luam-sector-status-state-active",
        "luam-sector-status-state-filed",
        "luam-sector-status-state-resolved",
        "luam-sector-status-hazard-title",
        "luam-sector-status-history-title",
    }
    terminal_locale_keys = {
        "luam-sector-terminal-title",
        "luam-sector-terminal-heading",
        "luam-sector-terminal-group-process",
        "luam-sector-terminal-group-reports",
        "luam-sector-terminal-group-paperwork",
        "luam-sector-terminal-action-refresh",
        "luam-sector-terminal-action-refresh-tooltip",
        "luam-sector-terminal-action-generate",
        "luam-sector-terminal-action-generate-tooltip",
        "luam-sector-terminal-action-ping",
        "luam-sector-terminal-action-ping-tooltip",
        "luam-sector-terminal-action-print-report",
        "luam-sector-terminal-action-print-report-tooltip",
        "luam-sector-terminal-action-print-route",
        "luam-sector-terminal-action-print-route-tooltip",
        "luam-sector-terminal-action-print-closure",
        "luam-sector-terminal-action-print-closure-tooltip",
        "luam-sector-terminal-action-print-insurance",
        "luam-sector-terminal-action-print-insurance-tooltip",
        "luam-sector-terminal-action-print-claim",
        "luam-sector-terminal-action-print-claim-tooltip",
        "luam-sector-terminal-action-print-registry",
        "luam-sector-terminal-action-print-registry-tooltip",
        "luam-sector-terminal-action-print-charter",
        "luam-sector-terminal-action-print-charter-tooltip",
        "luam-sector-terminal-section-quests",
        "luam-sector-terminal-section-automation",
        "luam-sector-terminal-section-map",
        "luam-sector-terminal-section-preferred",
        "luam-sector-terminal-section-insurance",
        "luam-sector-terminal-section-registry",
        "luam-sector-terminal-section-leads",
        "luam-sector-terminal-section-conditions",
        "luam-sector-terminal-section-reputation",
        "luam-sector-terminal-section-locked",
        "luam-sector-terminal-section-history",
        "luam-sector-terminal-summary",
        "luam-sector-terminal-last-action-ready",
        "luam-sector-terminal-last-action",
        "luam-sector-terminal-no-quests",
        "luam-sector-terminal-quests-hint",
        "luam-sector-terminal-quest-header",
        "luam-sector-terminal-quest-step-location",
        "luam-sector-terminal-quest-step-action",
        "luam-sector-terminal-quest-step-finish",
        "luam-sector-terminal-quest-step-reward",
        "luam-sector-terminal-quest-title",
        "luam-sector-terminal-quest-objective",
        "luam-sector-terminal-quest-detail",
        "luam-sector-terminal-quest-location-unknown",
        "luam-sector-terminal-quest-turnin-unknown",
        "luam-sector-terminal-quest-reward-unknown",
        "luam-sector-terminal-automation-state",
        "luam-sector-terminal-automation-next",
        "luam-sector-terminal-open-runtime-lead",
        "luam-sector-terminal-open-runtime-none",
        "luam-sector-terminal-dispatch-profile",
        "luam-sector-terminal-dispatch-score",
        "luam-sector-terminal-dispatch-cooldown",
        "luam-sector-terminal-route-ping-ready",
        "luam-sector-terminal-route-ping-blocked",
        "luam-sector-terminal-route-marker",
        "luam-sector-terminal-route-last-none",
        "luam-sector-terminal-physical-hooks",
        "luam-sector-terminal-physical-hooks-entry",
        "luam-sector-terminal-generator-templates",
        "luam-sector-terminal-generator-no-templates",
        "luam-sector-terminal-no-sector-map",
        "luam-sector-terminal-no-preferred",
        "luam-sector-terminal-no-insurance",
        "luam-sector-terminal-no-registry",
        "luam-sector-terminal-no-leads",
        "luam-sector-terminal-no-conditions",
        "luam-sector-terminal-no-reputation",
        "luam-sector-terminal-no-locked",
        "luam-sector-terminal-no-history",
        "luam-sector-terminal-claim-filed",
        "luam-sector-terminal-print-claim",
        "luam-sector-terminal-claim-filed-tooltip",
        "luam-sector-terminal-print-claim-tooltip",
        "luam-sector-terminal-preferred-request",
        "luam-sector-terminal-preferred-unavailable",
        "luam-sector-terminal-preferred-needs-reputation",
        "luam-sector-terminal-preferred-request-tooltip",
        "luam-sector-terminal-print-charter",
        "luam-sector-terminal-registry-filed",
        "luam-sector-terminal-print-charter-tooltip",
        "luam-sector-terminal-registry-filed-tooltip",
        "luam-sector-terminal-lead-title",
        "luam-sector-terminal-condition-title",
        "luam-sector-terminal-actor",
        "luam-sector-terminal-reputation-entry",
        "luam-sector-terminal-reputation-bonus",
        "luam-sector-terminal-locked-requirement",
        "luam-sector-terminal-history-title",
        "luam-sector-terminal-insurance-summary",
        "luam-sector-terminal-sector-map-title",
        "luam-sector-terminal-sector-map-entry",
        "luam-sector-terminal-sector-map-detail-pings",
        "luam-sector-terminal-sector-map-risk",
        "luam-sector-terminal-preferred-entry",
        "luam-sector-terminal-preferred-detail",
        "luam-sector-terminal-registry-summary",
        "luam-sector-terminal-company-record",
        "luam-sector-terminal-ship-record",
        "luam-sector-terminal-result-refresh",
        "luam-sector-terminal-result-generated",
        "luam-sector-terminal-result-generated-named",
        "luam-sector-terminal-result-report-printed",
        "luam-sector-terminal-result-report-failed",
        "luam-sector-terminal-result-route-printed",
        "luam-sector-terminal-result-route-failed",
        "luam-sector-terminal-result-closure-printed",
        "luam-sector-terminal-result-closure-failed",
        "luam-sector-terminal-result-insurance-printed",
        "luam-sector-terminal-result-insurance-failed",
        "luam-sector-terminal-result-claim-printed",
        "luam-sector-terminal-result-claim-failed",
        "luam-sector-terminal-result-registry-printed",
        "luam-sector-terminal-result-registry-failed",
        "luam-sector-terminal-result-charter-printed",
        "luam-sector-terminal-result-charter-failed",
        "luam-sector-terminal-popup-report-printer-failed",
        "luam-sector-terminal-popup-no-open-runtime",
        "luam-sector-terminal-popup-route-printer-failed",
        "luam-sector-terminal-popup-route-printed",
        "luam-sector-terminal-popup-closure-printer-failed",
        "luam-sector-terminal-popup-closure-printed",
        "luam-sector-terminal-route-ping-no-open",
        "luam-sector-terminal-route-ping-no-marker",
        "luam-sector-terminal-route-ping-comms-blackout",
        "luam-sector-terminal-route-ping-no-beacon",
        "luam-sector-terminal-route-ping-summary",
        "luam-sector-terminal-route-ping-sent",
        "luam-sector-terminal-popup-marker-printer-failed",
        "luam-sector-terminal-popup-marker-printed",
        "luam-sector-terminal-next-round-inactive",
        "luam-sector-terminal-next-initial-delay",
        "luam-sector-terminal-next-due-now",
        "luam-sector-terminal-next-minutes",
        "luam-sector-terminal-request-block-round-inactive",
        "luam-sector-terminal-request-block-no-operators",
        "luam-sector-terminal-request-block-open-lead",
        "luam-sector-terminal-route-block-no-open",
        "luam-sector-terminal-route-block-no-marker",
        "luam-sector-terminal-route-block-no-beacon",
        "luam-sector-terminal-route-block-comms-blackout",
        "luam-sector-terminal-preferred-block-reputation",
    }
    for rel_path in [
        "Resources/Locale/en-US/_NF/cartridge-loader/cartridges.ftl",
        "Resources/Locale/ru-RU/_NF/cartridge-loader/cartridges.ftl",
    ]:
        keys = load_ftl_keys(ROOT / rel_path)
        missing = sorted((cartridge_locale_keys | terminal_locale_keys) - keys)
        if missing:
            raise AssertionError(f"{rel_path}: missing locale keys {', '.join(missing)}")

    ru_cartridge = (ROOT / "Resources/Locale/ru-RU/_NF/cartridge-loader/cartridges.ftl").read_text(encoding="utf-8")
    for line in ru_cartridge.splitlines():
        if line.startswith("luam-sector-status-") or line.startswith("luam-sector-terminal-"):
            assert_not_contains(line, "?", "ru-RU LuaM cartridge locale")

    research_zoom_locale_keys = {
        "research-console-menu-zoom-out-tooltip",
        "research-console-menu-zoom-reset-tooltip",
        "research-console-menu-zoom-in-tooltip",
    }
    for rel_path in [
        "Resources/Locale/en-US/_Goobstation/research/ui.ftl",
        "Resources/Locale/ru-RU/_Goobstation/research/ui.ftl",
    ]:
        keys = load_ftl_keys(ROOT / rel_path)
        missing = sorted(research_zoom_locale_keys - keys)
        if missing:
            raise AssertionError(f"{rel_path}: missing locale keys {', '.join(missing)}")

    print("LuaM feature pack validation passed.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except AssertionError as exc:
        print(f"LuaM feature pack validation failed: {exc}", file=sys.stderr)
        raise SystemExit(1)
