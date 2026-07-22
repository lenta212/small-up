#!/usr/bin/env python3
"""Deterministic, dependency-free runtime ship blueprint generator for LuaM.

The generator deliberately emits logical entity kinds instead of SS14 prototype
IDs.  A trusted server-side adapter owns the mapping from those logical kinds to
game prototypes and performs the final engine validation.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import secrets
import sys
from collections import Counter, deque
from pathlib import Path
from typing import Any, Iterable, Sequence


SCHEMA_VERSION = 1
GENERATOR_VERSION = "1.1.0"

PRESETS = ("expedition", "fighter", "salvage")
SIZE_DIMENSIONS: dict[str, tuple[int, int]] = {
    "small": (11, 13),
    "medium": (15, 19),
    "large": (19, 23),
}

MAX_DIMENSION = 25
MAX_TILES = 400
MAX_ENTITIES = 512
MAX_NAME_LENGTH = 32
MAX_SAVED_SHIP_ENTITIES = 131_072
MAX_SAVED_SHIP_PAYLOAD_BYTES = 128 * 1024 * 1024
MAX_SAVED_SHIP_PROTOTYPES = 512
MAX_PROTOTYPE_ID_LENGTH = 128
SAVED_SHIP_ANALYSIS_SCHEMA_VERSION = 1

SEED_PATTERN = re.compile(r"^[a-z0-9][a-z0-9_-]{0,63}$")

HULL_KINDS = frozenset({"wall", "window", "airlock", "dock"})
BOUNDARY_KINDS = HULL_KINDS | frozenset({"thruster", "weapon"})
MISSION_KINDS = frozenset({"medical", "salvage", "science"})
ATMOS_NETWORK_KINDS = {
    "supply": frozenset({"vent", "air_storage"}),
    "waste": frozenset({"scrubber", "waste_storage"}),
}
ATMOS_ENDPOINT_KINDS = frozenset().union(*ATMOS_NETWORK_KINDS.values())
PATH_BLOCKING_KINDS = BOUNDARY_KINDS | frozenset(
    {
        "bulkhead",
        "chair",
        "console",
        "gunnery_console",
        "gunnery_server",
        "gyro",
        "medical",
        "research_server",
        "salvage",
        "science",
        "substation",
        "air_storage",
        "waste_storage",
    }
)
ENTITY_KINDS = frozenset(
    {
        "wall",
        "window",
        "airlock",
        "chair",
        "console",
        "gunnery_console",
        "gunnery_server",
        "light",
        "vent",
        "scrubber",
        "apu",
        "substation",
        "apc",
        "bulkhead",
        "thruster",
        "gyro",
        "dock",
        "weapon",
        "medical",
        "research_server",
        "salvage",
        "science",
        "air_storage",
        "waste_storage",
    }
)

ROTATIONS = frozenset({0, 90, 180, 270})
CARDINALS = ((1, 0), (-1, 0), (0, 1), (0, -1))
THRUSTER_OUTWARD = {
    0: (0, 1),
    180: (0, -1),
    90: (-1, 0),
    270: (1, 0),
}
PIPE_CONNECTION_OFFSETS = {
    0: (0, -1),
    90: (1, 0),
    180: (0, 1),
    270: (-1, 0),
}

_PRESET_PREFIXES = {
    "expedition": "EXP-SV",
    "fighter": "TSF-SKR",
    "salvage": "SALV-SV",
}

_PRESET_NAMES = {
    "expedition": (
        "Aster",
        "Curie",
        "Faraday",
        "Horizon",
        "Kepler",
        "Quercus",
    ),
    "fighter": (
        "Aquila",
        "Comet",
        "Harrier",
        "Kite",
        "Lancer",
        "Vigil",
    ),
    "salvage": (
        "Anvil",
        "Badger",
        "Mole",
        "Prospector",
        "Rivet",
        "Tug",
    ),
}

_PREVIEW_GLYPHS = {
    "wall": "#",
    "window": "o",
    "airlock": "A",
    "dock": "D",
    "chair": "c",
    "console": "C",
    "gunnery_console": "Q",
    "gunnery_server": "R",
    "light": "*",
    "vent": "v",
    "scrubber": "s",
    "apu": "P",
    "substation": "U",
    "apc": "B",
    "bulkhead": "H",
    "thruster": "T",
    "gyro": "G",
    "weapon": "W",
    "medical": "M",
    "research_server": "E",
    "salvage": "$",
    "science": "N",
    "air_storage": "a",
    "waste_storage": "w",
}

_PREVIEW_PRIORITY = {
    kind: priority
    for priority, kind in enumerate(
        (
            "light",
            "vent",
            "scrubber",
            "chair",
            "gyro",
            "apu",
            "substation",
            "apc",
            "bulkhead",
            "medical",
            "research_server",
            "salvage",
            "science",
            "air_storage",
            "waste_storage",
            "console",
            "gunnery_console",
            "gunnery_server",
            "thruster",
            "weapon",
            "wall",
            "window",
            "airlock",
            "dock",
        )
    )
}

_SAVED_SHIP_CAPABILITY_TOKENS: dict[str, tuple[str, ...]] = {
    "atmosphere": ("gaspipe", "vent", "scrubber", "canister", "airalarm", "portablepump"),
    "cargo": ("cargo", "market", "pallet", "telepad", "mail", "crate"),
    "combat": ("weapon", "turret", "gunnery", "ammo", "missile", "cannon"),
    "docking": ("airlockshuttle", "docking", "dock"),
    "medical": ("medical", "stasis", "defibrillator", "cryo", "surgery"),
    "navigation": ("computershuttle", "shuttleconsole", "radar", "navconsole"),
    "power": ("generator", "battery", "substation", "apc", "cable", "solar"),
    "propulsion": ("thruster", "gyroscope", "gyro"),
    "research": ("research", "anomaly", "datafarm", "science"),
    "salvage": ("oreprocessor", "salvage", "mining", "materialreclaimer"),
}


def _saved_ship_manifest_hash(prototype_counts: dict[str, int]) -> str:
    manifest = "".join(
        f"{prototype}\t{prototype_counts[prototype]}\n"
        for prototype in sorted(prototype_counts)
    )
    return hashlib.sha256(manifest.encode("utf-8")).hexdigest()


def analyze_saved_ship_request(request: Any) -> dict[str, Any]:
    """Analyze a privacy-bounded manifest from a durable player-ship snapshot.

    The game server extracts and verifies the prototype manifest from the saved
    YAML.  Only prototype identifiers and aggregate counts cross the gateway;
    names, owners, entity state, paper text, coordinates, and the raw snapshot
    are deliberately excluded.
    """

    if not isinstance(request, dict):
        raise ShipGenerationError("saved ship analysis request must be an object")

    expected = {
        "schemaVersion",
        "snapshotFormatVersion",
        "entityCount",
        "payloadSizeBytes",
        "prototypeManifestHash",
        "prototypes",
    }
    actual = set(request)
    missing = sorted(expected - actual)
    extra = sorted(actual - expected)
    if missing:
        raise ShipGenerationError(
            f"missing saved ship analysis fields: {', '.join(missing)}"
        )
    if extra:
        raise ShipGenerationError(
            f"unknown saved ship analysis fields: {', '.join(extra)}"
        )

    if (
        not _is_int(request["schemaVersion"])
        or request["schemaVersion"] != SAVED_SHIP_ANALYSIS_SCHEMA_VERSION
    ):
        raise ShipGenerationError(
            f"schemaVersion must be {SAVED_SHIP_ANALYSIS_SCHEMA_VERSION}"
        )

    snapshot_format = request["snapshotFormatVersion"]
    entity_count = request["entityCount"]
    payload_size = request["payloadSizeBytes"]
    manifest_hash = request["prototypeManifestHash"]
    prototypes = request["prototypes"]
    if not _is_int(snapshot_format) or snapshot_format <= 0:
        raise ShipGenerationError("snapshotFormatVersion must be a positive integer")
    if not _is_int(entity_count) or not 0 < entity_count <= MAX_SAVED_SHIP_ENTITIES:
        raise ShipGenerationError("entityCount is outside the saved ship limit")
    if not _is_int(payload_size) or not 0 < payload_size <= MAX_SAVED_SHIP_PAYLOAD_BYTES:
        raise ShipGenerationError("payloadSizeBytes is outside the saved ship limit")
    if (
        not isinstance(manifest_hash, str)
        or re.fullmatch(r"[0-9a-fA-F]{64}", manifest_hash) is None
    ):
        raise ShipGenerationError("prototypeManifestHash must be a SHA-256 hex digest")
    if not isinstance(prototypes, list) or not 0 < len(prototypes) <= MAX_SAVED_SHIP_PROTOTYPES:
        raise ShipGenerationError("prototypes is outside the saved ship limit")

    prototype_counts: dict[str, int] = {}
    for entry in prototypes:
        if not isinstance(entry, dict) or set(entry) != {"id", "count"}:
            raise ShipGenerationError("each prototype must contain only id and count")
        prototype = entry["id"]
        count = entry["count"]
        if (
            not isinstance(prototype, str)
            or not prototype
            or len(prototype) > MAX_PROTOTYPE_ID_LENGTH
            or any(ord(char) < 33 or char.isspace() for char in prototype)
        ):
            raise ShipGenerationError("prototype id is invalid")
        if prototype in prototype_counts:
            raise ShipGenerationError(f"duplicate prototype id: {prototype}")
        if not _is_int(count) or count <= 0:
            raise ShipGenerationError(f"prototype count must be positive: {prototype}")
        prototype_counts[prototype] = count

    if sum(prototype_counts.values()) != entity_count:
        raise ShipGenerationError("prototype counts do not match entityCount")
    computed_hash = _saved_ship_manifest_hash(prototype_counts)
    if not secrets.compare_digest(computed_hash, manifest_hash.lower()):
        raise ShipGenerationError("prototype manifest hash mismatch")

    capability_counts: dict[str, int] = {}
    classified_entities: set[str] = set()
    for capability, tokens in _SAVED_SHIP_CAPABILITY_TOKENS.items():
        matched = {
            prototype
            for prototype in prototype_counts
            if any(token in prototype.lower() for token in tokens)
        }
        capability_counts[capability] = sum(prototype_counts[item] for item in matched)
        classified_entities.update(matched)

    if capability_counts["combat"] > 0:
        suggested_preset = "fighter"
    elif capability_counts["research"] > 0 or capability_counts["medical"] > 0:
        suggested_preset = "expedition"
    elif capability_counts["salvage"] > 0 or capability_counts["cargo"] > 0:
        suggested_preset = "salvage"
    else:
        suggested_preset = "expedition"

    top_prototypes = [
        {"id": prototype, "count": count}
        for prototype, count in sorted(
            prototype_counts.items(), key=lambda item: (-item[1], item[0])
        )[:20]
    ]
    classified_count = sum(prototype_counts[item] for item in classified_entities)
    return {
        "schemaVersion": SAVED_SHIP_ANALYSIS_SCHEMA_VERSION,
        "generatorVersion": GENERATOR_VERSION,
        "snapshotFormatVersion": snapshot_format,
        "entityCount": entity_count,
        "payloadSizeBytes": payload_size,
        "prototypeCount": len(prototype_counts),
        "prototypeManifestHash": computed_hash,
        "capabilities": capability_counts,
        "classifiedEntityCount": classified_count,
        "unclassifiedEntityCount": entity_count - classified_count,
        "suggestedPreset": suggested_preset,
        "topPrototypes": top_prototypes,
    }


class ShipGenerationError(ValueError):
    """A request or generated blueprint violated the public contract."""


class StableRandom:
    """Tiny BLAKE2-backed RNG whose output is stable across Python versions."""

    def __init__(self, seed: str, namespace: str) -> None:
        self._key = f"luam-shipgen:{GENERATOR_VERSION}:{seed}:{namespace}".encode("utf-8")
        self._counter = 0

    def _next_u64(self) -> int:
        counter = self._counter.to_bytes(8, "little", signed=False)
        self._counter += 1
        digest = hashlib.blake2b(self._key + counter, digest_size=8).digest()
        return int.from_bytes(digest, "little", signed=False)

    def randbelow(self, upper: int) -> int:
        if upper <= 0:
            raise ValueError("upper must be positive")

        # Rejection sampling avoids modulo bias and preserves deterministic output.
        limit = (1 << 64) - ((1 << 64) % upper)
        while True:
            value = self._next_u64()
            if value < limit:
                return value % upper

    def choice(self, values: Sequence[Any]) -> Any:
        if not values:
            raise ValueError("cannot choose from an empty sequence")
        return values[self.randbelow(len(values))]

    def shuffle(self, values: list[Any]) -> None:
        for index in range(len(values) - 1, 0, -1):
            other = self.randbelow(index + 1)
            values[index], values[other] = values[other], values[index]


def _is_int(value: Any) -> bool:
    return type(value) is int


def _normalize_seed(seed: Any) -> str:
    if not isinstance(seed, str):
        raise ShipGenerationError("seed must be a string")

    normalized = seed.strip()
    if normalized.lower() == "auto":
        return secrets.token_hex(16)
    if not normalized:
        raise ShipGenerationError("seed must not be empty")
    if SEED_PATTERN.fullmatch(normalized) is None:
        raise ShipGenerationError(
            "seed must match ^[a-z0-9][a-z0-9_-]{0,63}$ or be auto"
        )
    return normalized


def _normalize_name(name: Any) -> str:
    if not isinstance(name, str):
        raise ShipGenerationError("name must be a string")

    normalized = " ".join(name.strip().split())
    if len(normalized) > MAX_NAME_LENGTH:
        raise ShipGenerationError(f"name must be at most {MAX_NAME_LENGTH} characters")
    if any(ord(char) < 32 for char in normalized):
        raise ShipGenerationError("name must not contain control characters")
    return normalized


def validate_request(request: Any) -> dict[str, Any]:
    """Validate and normalize the strict POST /generate_ship request."""

    if not isinstance(request, dict):
        raise ShipGenerationError("request body must be a JSON object")

    expected = {"schemaVersion", "preset", "size", "seed", "name"}
    actual = set(request)
    missing = sorted(expected - actual)
    extra = sorted(actual - expected)
    if missing:
        raise ShipGenerationError(f"missing request fields: {', '.join(missing)}")
    if extra:
        raise ShipGenerationError(f"unknown request fields: {', '.join(extra)}")

    if request["schemaVersion"] != SCHEMA_VERSION or not _is_int(request["schemaVersion"]):
        raise ShipGenerationError(f"schemaVersion must be {SCHEMA_VERSION}")

    preset = request["preset"]
    if not isinstance(preset, str) or preset not in PRESETS:
        raise ShipGenerationError(f"preset must be one of: {', '.join(PRESETS)}")

    size = request["size"]
    if not isinstance(size, str) or size not in SIZE_DIMENSIONS:
        raise ShipGenerationError(f"size must be one of: {', '.join(SIZE_DIMENSIONS)}")

    return {
        "schemaVersion": SCHEMA_VERSION,
        "preset": preset,
        "size": size,
        "seed": _normalize_seed(request["seed"]),
        "name": _normalize_name(request["name"]),
    }


def _generate_name(preset: str, seed: str) -> str:
    rng = StableRandom(seed, "name")
    word = rng.choice(_PRESET_NAMES[preset])
    serial = 100 + rng.randbelow(900)
    return f"{_PRESET_PREFIXES[preset]} {word}-{serial}"


def _thruster_count(preset: str, size: str) -> int:
    size_index = tuple(SIZE_DIMENSIONS).index(size)
    return (4, 5, 6)[size_index] + (2 if preset == "fighter" else 0)


def _gyro_count(size: str) -> int:
    return 1 + (1 if size != "small" else 0)


def _profile_half_widths(
    preset: str,
    size: str,
    width: int,
    height: int,
    seed: str,
) -> list[int]:
    max_half = (width - 1) // 2
    peak = max(2, (height * (62 if preset == "expedition" else 55)) // 100)
    rng = StableRandom(seed, "hull-profile")
    profile: list[int] = []

    for y in range(height):
        if preset == "fighter":
            widen_until = max(2, (height * 2) // 3)
            if y <= widen_until:
                half = 1 + ((max_half - 1) * y) // widen_until
            else:
                # Broad engine deck with a slight split-tail taper.
                taper = ((y - widen_until) * 2) // max(1, height - 1 - widen_until)
                half = max(2, max_half - taper)
        elif preset == "expedition":
            if y <= peak:
                half = 1 + ((max_half - 1) * y) // peak
            else:
                half = max(
                    2,
                    max_half
                    - ((max_half - 2) * (y - peak)) // max(1, height - 1 - peak),
                )
        else:  # salvage: blunt, roomy, inexpensive industrial hull.
            widen_until = max(2, height // 3)
            if y <= widen_until:
                half = 2 + ((max_half - 2) * y) // widen_until
            else:
                half = max(3, max_half - (1 if y >= height - 2 else 0))

        if 1 < y < height - 2:
            half += rng.choice((-1, 0, 0, 0, 1))
        profile.append(max(1, min(max_half, half)))

    # Keep a connected center corridor and prevent abrupt one-row spikes.
    for y in range(1, height):
        profile[y] = min(profile[y], profile[y - 1] + 1)
    for y in range(height - 2, -1, -1):
        profile[y] = min(profile[y], profile[y + 1] + 1)

    profile[0] = 1 if preset != "salvage" else 2

    # Three thrusters provide braking and lateral translation.  The remaining
    # thrusters point aft for forward acceleration and share that row with the
    # dock and airlock.
    aft_thruster_count = _thruster_count(preset, size) - 3
    required_aft_half = (aft_thruster_count + 2) // 2
    profile[-1] = max(required_aft_half, profile[-1])
    profile[-2] = max(min(max_half, required_aft_half + 1), profile[-2])
    return profile


def _generate_tiles(
    preset: str,
    size: str,
    width: int,
    height: int,
    seed: str,
) -> set[tuple[int, int]]:
    center_x = width // 2
    profile = _profile_half_widths(preset, size, width, height, seed)
    tiles: set[tuple[int, int]] = set()
    for y, half_width in enumerate(profile):
        for x in range(center_x - half_width, center_x + half_width + 1):
            tiles.add((x, y))
    return tiles


def _boundary_tiles(tiles: set[tuple[int, int]]) -> set[tuple[int, int]]:
    return {
        (x, y)
        for x, y in tiles
        if any((x + dx, y + dy) not in tiles for dx, dy in CARDINALS)
    }


def _boundary_rotation(position: tuple[int, int], width: int, height: int) -> int:
    x, y = position
    center_x = (width - 1) / 2
    center_y = (height - 1) / 2
    horizontal = abs(x - center_x) / max(1.0, width / 2)
    vertical = abs(y - center_y) / max(1.0, height / 2)
    if vertical >= horizontal:
        return 0 if y < center_y else 180
    return 270 if x < center_x else 90


def _entity(kind: str, position: tuple[int, int], rotation: int = 0) -> dict[str, Any]:
    return {
        "kind": kind,
        "x": position[0],
        "y": position[1],
        "rotation": rotation,
    }


def _place_hull_entities(
    preset: str,
    size: str,
    width: int,
    height: int,
    seed: str,
    tiles: set[tuple[int, int]],
) -> list[dict[str, Any]]:
    boundary = _boundary_tiles(tiles)
    interior = tiles - boundary
    center_x = width // 2
    aft = max(y for _, y in tiles)
    dock = (center_x, aft)

    if dock not in boundary:
        raise ShipGenerationError("generated docking tile is not on the hull boundary")

    airlock_candidates = sorted(
        (position for position in boundary if position[1] == aft and position != dock),
        key=lambda position: (abs(position[0] - center_x), position[0]),
    )
    if not airlock_candidates:
        raise ShipGenerationError("generated hull has no boundary tile for an airlock")
    airlock = airlock_candidates[0]

    aft_thruster_candidates = sorted(
        (
            position
            for position in boundary
            if position[1] == aft
            and position not in {dock, airlock}
            and (position[0], position[1] - 1) in interior
            and (position[0], position[1] + 1) not in tiles
        ),
        key=lambda position: (-abs(position[0] - center_x), position[0]),
    )
    aft_thruster_count = _thruster_count(preset, size) - 3
    if len(aft_thruster_candidates) < aft_thruster_count:
        raise ShipGenerationError(
            "generated hull has only "
            f"{len(aft_thruster_candidates)} valid aft pods for "
            f"{aft_thruster_count} aft thrusters"
        )
    thrusters: dict[tuple[int, int], int] = {
        position: 0 for position in aft_thruster_candidates[:aft_thruster_count]
    }

    weapons: dict[tuple[int, int], int] = {}
    if preset == "fighter":
        target_y = max(2, height // 3)
        for side, rotation in (("left", 270), ("right", 90)):
            candidates: list[tuple[int, int]] = []
            for y in range(1, aft):
                row = sorted(x for x, tile_y in tiles if tile_y == y)
                if not row:
                    continue
                x = row[0] if side == "left" else row[-1]
                position = (x, y)
                inward = (x + 1, y) if side == "left" else (x - 1, y)
                outward = (x - 1, y) if side == "left" else (x + 1, y)
                if position in boundary and inward in interior and outward not in tiles:
                    candidates.append(position)
            if not candidates:
                raise ShipGenerationError(f"generated fighter has no valid {side} weapon pod")
            candidates.sort(key=lambda position: (abs(position[1] - target_y), position[1], position[0]))
            weapons[candidates[0]] = rotation

    forward = min(y for _, y in tiles)
    forward_candidates = sorted(
        (
            position
            for position in boundary
            if position[1] == forward
            and position not in weapons
            and (position[0], position[1] + 1) in interior
            and (position[0], position[1] - 1) not in tiles
        ),
        key=lambda position: (abs(position[0] - center_x), position[0]),
    )
    if not forward_candidates:
        raise ShipGenerationError("generated hull has no valid forward braking pod")
    thrusters[forward_candidates[0]] = 180

    side_target_y = max(2, (height * 2) // 3)
    for side, rotation in (("left", 90), ("right", 270)):
        candidates = []
        for y in range(forward + 1, aft):
            row = sorted(x for x, tile_y in tiles if tile_y == y)
            if not row:
                continue
            x = row[0] if side == "left" else row[-1]
            position = (x, y)
            inward = (x + 1, y) if side == "left" else (x - 1, y)
            outward = (x - 1, y) if side == "left" else (x + 1, y)
            if (
                position in boundary
                and position not in weapons
                and position not in thrusters
                and inward in interior
                and outward not in tiles
            ):
                candidates.append(position)
        if not candidates:
            raise ShipGenerationError(f"generated hull has no valid {side} translation pod")
        candidates.sort(
            key=lambda position: (
                abs(position[1] - side_target_y),
                position[1],
                position[0],
            )
        )
        thrusters[candidates[0]] = rotation

    if len(thrusters) != _thruster_count(preset, size):
        raise ShipGenerationError("generated hull has an incorrect thruster pod count")

    pod_bulkheads: dict[tuple[int, int], tuple[int, int]] = {}
    for position, rotation in thrusters.items():
        outward_x, outward_y = THRUSTER_OUTWARD[rotation]
        pod_bulkheads[position] = (
            position[0] - outward_x,
            position[1] - outward_y,
        )
    for position, rotation in weapons.items():
        inward = (position[0] + 1, position[1]) if rotation == 270 else (position[0] - 1, position[1])
        pod_bulkheads[position] = inward

    inward_positions = list(pod_bulkheads.values())
    if len(set(inward_positions)) != len(inward_positions):
        raise ShipGenerationError("generated pods attempted to reuse one inward bulkhead")
    if any(position not in interior for position in inward_positions):
        raise ShipGenerationError("generated pod bulkhead is not on an interior tile")

    window_candidates = sorted(
        position
        for position in boundary
        if position not in {dock, airlock}
        and position not in thrusters
        and position not in weapons
        and 1 < position[1] < height - 2
    )
    window_rng = StableRandom(seed, "windows")
    window_rng.shuffle(window_candidates)
    window_divisor = {"fighter": 9, "expedition": 5, "salvage": 12}[preset]
    window_count = min(len(window_candidates), max(2, len(boundary) // window_divisor))
    windows = set(window_candidates[:window_count])

    result: list[dict[str, Any]] = []
    for position in sorted(boundary, key=lambda item: (item[1], item[0])):
        rotation = _boundary_rotation(position, width, height)
        if position == dock:
            kind = "dock"
        elif position == airlock:
            kind = "airlock"
        elif position in thrusters:
            result.append(_entity("thruster", position, thrusters[position]))
            continue
        elif position in weapons:
            result.append(_entity("weapon", position, weapons[position]))
            continue
        elif position in windows:
            kind = "window"
        else:
            kind = "wall"
        result.append(_entity(kind, position, rotation))
    for position in sorted(set(inward_positions), key=lambda item: (item[1], item[0])):
        result.append(_entity("bulkhead", position, 0))
    return result


def _place_system_entities(
    preset: str,
    size: str,
    width: int,
    height: int,
    seed: str,
    tiles: set[tuple[int, int]],
    hull_entities: Iterable[dict[str, Any]],
) -> list[dict[str, Any]]:
    boundary = _boundary_tiles(tiles)
    interior = tiles - boundary
    if not interior:
        raise ShipGenerationError("generated hull has no interior tiles")

    center_x = width // 2
    rng = StableRandom(seed, "systems")
    reserved: set[tuple[int, int]] = {
        (entity["x"], entity["y"])
        for entity in hull_entities
        if entity["kind"] == "bulkhead"
    }
    protected_path: set[tuple[int, int]] = set()
    result: list[dict[str, Any]] = []

    def pick_position(target_x: int, target_y: int, label: str) -> tuple[int, int]:
        candidates = [
            position
            for position in interior
            if position not in reserved and position not in protected_path
        ]
        if not candidates:
            raise ShipGenerationError(f"not enough interior tiles while placing {label}")
        candidates.sort(key=lambda position: (position[1], position[0]))
        rng.shuffle(candidates)
        position = min(
            candidates,
            key=lambda item: abs(item[0] - target_x) * 2 + abs(item[1] - target_y) * 3,
        )
        reserved.add(position)
        return position

    def place(
        kind: str,
        count: int,
        target_y: int,
        x_offsets: Iterable[int] = (0,),
        rotation: int = 0,
    ) -> None:
        offsets = tuple(x_offsets) or (0,)
        for index in range(count):
            target_x = center_x + offsets[index % len(offsets)]
            result.append(
                _entity(
                    kind,
                    pick_position(target_x, target_y, f"{kind}[{index}]"),
                    rotation,
                )
            )

    def place_atmos_storage(
        kind: str,
        peer_kind: str,
        target_x: int,
        target_y: int,
    ) -> None:
        candidates = sorted(
            (
                position
                for position in interior
                if position not in reserved and position not in protected_path
            ),
            key=lambda position: (
                abs(position[0] - target_x) * 2 + abs(position[1] - target_y) * 3,
                position[1],
                position[0],
            ),
        )
        peer_positions = {
            (entity["x"], entity["y"])
            for entity in result
            if entity["kind"] == peer_kind
        }
        for position in candidates:
            endpoint_positions = peer_positions | {position}
            route_tiles = interior - endpoint_positions
            if not any(
                all(
                    any(
                        (endpoint[0] + dx, endpoint[1] + dy) in component
                        for dx, dy in PIPE_CONNECTION_OFFSETS.values()
                    )
                    for endpoint in endpoint_positions
                )
                for component in _tile_components(route_tiles)
            ):
                continue

            reserved.add(position)
            result.append(_entity(kind, position))
            return

        raise ShipGenerationError(f"not enough connected interior space for {kind}")

    # Command and pilot position.  Once the console is fixed, keep a simple
    # centerline route clear from the dock's inward tile to the console.
    console = pick_position(center_x, max(1, height // 6), "console[0]")
    result.append(_entity("console", console))
    aft = max(y for _, y in tiles)
    for y in range(console[1] + 1, aft):
        protected_path.add((center_x, y))
    for x in range(min(center_x, console[0]), max(center_x, console[0]) + 1):
        if (x, console[1]) != console:
            protected_path.add((x, console[1]))
    place("chair", 1, max(2, height // 6 + 1), rotation=180)

    # Trusted server-side code maps and wires these generic power nodes.
    place("substation", 1, (height * 2) // 3, x_offsets=(-2, 2))
    place("apc", 1, height // 2, x_offsets=(-2, 2))

    size_index = tuple(SIZE_DIMENSIONS).index(size)
    gyro_count = _gyro_count(size)
    thruster_count = _thruster_count(preset, size)
    required_power = (
        thruster_count * 1_500
        + gyro_count * 1_500
        + (450 if preset == "fighter" else 0)
        + (1_200 if preset == "expedition" else 0)
        + 4_000
    )
    apu_count = min(4, (required_power + 5_999) // 6_000)
    atmosphere_count = (1, 2, 2)[size_index]

    place("apu", apu_count, (height * 3) // 5, x_offsets=(-3, 3, -1, 1))
    place("gyro", gyro_count, height // 2, x_offsets=(-1, 1))
    place("vent", atmosphere_count, height // 2, x_offsets=(-3, 3, 0))
    place("scrubber", atmosphere_count, (height * 3) // 5, x_offsets=(3, -3, 0))
    place_atmos_storage("air_storage", "vent", center_x - 2, (height * 2) // 5)
    place_atmos_storage("waste_storage", "scrubber", center_x + 2, (height * 2) // 5)

    if preset == "fighter":
        place("gunnery_console", 1, height // 3, x_offsets=(-2,))
        place("gunnery_server", 1, height // 2, x_offsets=(2,))
    elif preset == "expedition":
        place("medical", 1, height // 3, x_offsets=(-3,))
        place("science", 1, height // 3, x_offsets=(3,))
        place("research_server", 1, height // 2, x_offsets=(-2,))
        place("salvage", 1, (height * 2) // 3, x_offsets=(3,))
    elif preset == "salvage":
        place("salvage", 1, height // 2, x_offsets=(3,))

    light_count = min(8, max(2, len(interior) // 45))
    for index in range(light_count):
        target_y = 2 + ((height - 5) * index) // max(1, light_count - 1)
        place("light", 1, target_y, x_offsets=(0, -2, 2))

    return result


def _sort_tiles(tiles: Iterable[tuple[int, int]]) -> list[dict[str, int]]:
    return [
        {"x": x, "y": y}
        for x, y in sorted(tiles, key=lambda position: (position[1], position[0]))
    ]


def _sort_entities(entities: Iterable[dict[str, Any]]) -> list[dict[str, Any]]:
    return sorted(
        entities,
        key=lambda entity: (
            entity["kind"],
            entity["y"],
            entity["x"],
            entity["rotation"],
        ),
    )


def render_preview(blueprint: dict[str, Any]) -> list[str]:
    """Render a fixed-size ASCII preview from a blueprint."""

    width = blueprint["width"]
    height = blueprint["height"]
    canvas = [[" " for _ in range(width)] for _ in range(height)]

    for tile in blueprint["tiles"]:
        x, y = tile["x"], tile["y"]
        if 0 <= x < width and 0 <= y < height:
            canvas[y][x] = "."

    chosen: dict[tuple[int, int], tuple[int, str]] = {}
    for entity in blueprint["entities"]:
        position = (entity["x"], entity["y"])
        priority = _PREVIEW_PRIORITY[entity["kind"]]
        current = chosen.get(position)
        if current is None or priority >= current[0]:
            chosen[position] = (priority, _PREVIEW_GLYPHS[entity["kind"]])

    for (x, y), (_, glyph) in chosen.items():
        if 0 <= x < width and 0 <= y < height:
            canvas[y][x] = glyph

    return ["".join(row) for row in canvas]


def _connected_tiles(tiles: set[tuple[int, int]]) -> bool:
    if not tiles:
        return False
    visited = {next(iter(tiles))}
    pending = deque(visited)
    while pending:
        x, y = pending.popleft()
        for dx, dy in CARDINALS:
            neighbor = (x + dx, y + dy)
            if neighbor in tiles and neighbor not in visited:
                visited.add(neighbor)
                pending.append(neighbor)
    return visited == tiles


def _tile_components(tiles: set[tuple[int, int]]) -> list[set[tuple[int, int]]]:
    remaining = set(tiles)
    components: list[set[tuple[int, int]]] = []
    while remaining:
        first = min(remaining, key=lambda position: (position[1], position[0]))
        component = {first}
        pending = deque([first])
        remaining.remove(first)
        while pending:
            x, y = pending.popleft()
            for dx, dy in CARDINALS:
                neighbor = (x + dx, y + dy)
                if neighbor not in remaining:
                    continue
                remaining.remove(neighbor)
                component.add(neighbor)
                pending.append(neighbor)
        components.append(component)
    return components


def _atmos_connection(
    position: tuple[int, int],
    rotation: int,
) -> tuple[int, int]:
    dx, dy = PIPE_CONNECTION_OFFSETS[rotation]
    return position[0] + dx, position[1] + dy


def _orient_atmos_entities(
    tiles: set[tuple[int, int]],
    entities: list[dict[str, Any]],
) -> None:
    center_x = sum(x for x, _ in tiles) / len(tiles)
    center_y = sum(y for _, y in tiles) / len(tiles)
    rotation_order = (0, 90, 180, 270)

    for network_name, network_kinds in ATMOS_NETWORK_KINDS.items():
        endpoints = [entity for entity in entities if entity["kind"] in network_kinds]
        if not endpoints:
            raise ShipGenerationError(f"generated ship has no {network_name} atmosphere endpoints")

        endpoint_positions = {(entity["x"], entity["y"]) for entity in endpoints}
        route_tiles = tiles - _boundary_tiles(tiles) - endpoint_positions
        candidates = []
        for component in _tile_components(route_tiles):
            if all(
                any(
                    _atmos_connection((entity["x"], entity["y"]), rotation) in component
                    for rotation in rotation_order
                )
                for entity in endpoints
            ):
                candidates.append(component)

        if not candidates:
            raise ShipGenerationError(
                f"generated systems leave no connected interior {network_name} atmosphere route"
            )

        route_component = min(
            candidates,
            key=lambda component: (
                -len(component),
                min((y, x) for x, y in component),
            ),
        )
        for entity in endpoints:
            position = (entity["x"], entity["y"])
            rotations = [
                rotation
                for rotation in rotation_order
                if _atmos_connection(position, rotation) in route_component
            ]
            entity["rotation"] = min(
                rotations,
                key=lambda rotation: (
                    abs(_atmos_connection(position, rotation)[0] - center_x)
                    + abs(_atmos_connection(position, rotation)[1] - center_y),
                    rotation_order.index(rotation),
                ),
            )


def _try_build_atmos_network(
    tiles: set[tuple[int, int]],
    endpoints: Iterable[tuple[str, tuple[int, int], int]],
    blocked_endpoints: set[tuple[int, int]],
) -> tuple[set[tuple[int, int]] | None, str | None]:
    endpoint_list = list(endpoints)
    if not endpoint_list:
        return None, "network has no endpoints"

    route_tiles = tiles - _boundary_tiles(tiles) - blocked_endpoints
    connections: list[tuple[int, int]] = []
    for kind, position, rotation in endpoint_list:
        connection = _atmos_connection(position, rotation)
        if connection not in route_tiles:
            return (
                None,
                f"{kind} at {position[0]},{position[1]} does not face a safe interior pipe tile",
            )
        connections.append(connection)

    ordered_connections = sorted(set(connections), key=lambda position: (position[1], position[0]))
    network = {ordered_connections[0]}
    for endpoint in ordered_connections[1:]:
        if endpoint in network:
            continue

        pending = deque([endpoint])
        visited = {endpoint}
        parents: dict[tuple[int, int], tuple[int, int]] = {}
        connection: tuple[int, int] | None = None
        while pending and connection is None:
            current = pending.popleft()
            for dx, dy in CARDINALS:
                neighbor = (current[0] + dx, current[1] + dy)
                if neighbor not in route_tiles or neighbor in visited:
                    continue
                visited.add(neighbor)
                parents[neighbor] = current
                if neighbor in network:
                    connection = neighbor
                    break
                pending.append(neighbor)

        if connection is None:
            return None, "network endpoints are disconnected"

        cursor = connection
        network.add(cursor)
        while cursor != endpoint:
            cursor = parents[cursor]
            network.add(cursor)

    return network, None


def _has_walkable_path(
    tiles: set[tuple[int, int]],
    start: tuple[int, int],
    goal: tuple[int, int],
    blocked: set[tuple[int, int]],
) -> bool:
    if start not in tiles or goal not in tiles or start in blocked:
        return False
    visited = {start}
    pending = deque(visited)
    while pending:
        position = pending.popleft()
        if position == goal:
            return True
        x, y = position
        for dx, dy in CARDINALS:
            neighbor = (x + dx, y + dy)
            if (
                neighbor in tiles
                and neighbor not in blocked
                and neighbor not in visited
            ):
                visited.add(neighbor)
                pending.append(neighbor)
    return False


def validate_blueprint(blueprint: Any) -> list[str]:
    """Return all contract errors found in a generated or external blueprint."""

    errors: list[str] = []
    if not isinstance(blueprint, dict):
        return ["blueprint must be a JSON object"]

    expected_keys = {
        "schemaVersion",
        "generatorVersion",
        "seed",
        "name",
        "preset",
        "width",
        "height",
        "tiles",
        "entities",
        "summary",
        "preview",
    }
    missing = sorted(expected_keys - set(blueprint))
    extra = sorted(set(blueprint) - expected_keys)
    if missing:
        errors.append(f"missing blueprint fields: {', '.join(missing)}")
    if extra:
        errors.append(f"unknown blueprint fields: {', '.join(extra)}")
    if missing:
        return errors

    if blueprint["schemaVersion"] != SCHEMA_VERSION or not _is_int(blueprint["schemaVersion"]):
        errors.append(f"schemaVersion must be {SCHEMA_VERSION}")
    if blueprint["generatorVersion"] != GENERATOR_VERSION:
        errors.append(f"generatorVersion must be {GENERATOR_VERSION}")
    if (
        not isinstance(blueprint["seed"], str)
        or SEED_PATTERN.fullmatch(blueprint["seed"]) is None
    ):
        errors.append("seed must match ^[a-z0-9][a-z0-9_-]{0,63}$")
    if (
        not isinstance(blueprint["name"], str)
        or not blueprint["name"]
        or len(blueprint["name"]) > MAX_NAME_LENGTH
        or any(ord(char) < 32 for char in blueprint["name"])
    ):
        errors.append(f"name must contain 1 to {MAX_NAME_LENGTH} printable characters")

    preset = blueprint["preset"]
    if preset not in PRESETS:
        errors.append(f"preset must be one of: {', '.join(PRESETS)}")

    width = blueprint["width"]
    height = blueprint["height"]
    if not _is_int(width) or not 1 <= width <= MAX_DIMENSION:
        errors.append(f"width must be an integer from 1 to {MAX_DIMENSION}")
    if not _is_int(height) or not 1 <= height <= MAX_DIMENSION:
        errors.append(f"height must be an integer from 1 to {MAX_DIMENSION}")
    if errors and (not _is_int(width) or not _is_int(height)):
        return errors

    raw_tiles = blueprint["tiles"]
    if not isinstance(raw_tiles, list):
        return errors + ["tiles must be an array"]
    if len(raw_tiles) > MAX_TILES:
        errors.append(f"tiles must contain at most {MAX_TILES} entries")

    tiles: set[tuple[int, int]] = set()
    for index, tile in enumerate(raw_tiles):
        if not isinstance(tile, dict) or set(tile) != {"x", "y"}:
            errors.append(f"tiles[{index}] must contain only integer x and y")
            continue
        x, y = tile["x"], tile["y"]
        if not _is_int(x) or not _is_int(y):
            errors.append(f"tiles[{index}] coordinates must be integers")
            continue
        if not 0 <= x < width or not 0 <= y < height:
            errors.append(f"tiles[{index}] is outside blueprint bounds")
            continue
        if (x, y) in tiles:
            errors.append(f"duplicate tile at {x},{y}")
        tiles.add((x, y))

    if not tiles:
        errors.append("tiles must not be empty")
    elif not _connected_tiles(tiles):
        errors.append("all floor tiles must be 4-connected")

    raw_entities = blueprint["entities"]
    if not isinstance(raw_entities, list):
        return errors + ["entities must be an array"]
    if len(raw_entities) > MAX_ENTITIES:
        errors.append(f"entities must contain at most {MAX_ENTITIES} entries")

    parsed_entities: list[tuple[str, tuple[int, int], int]] = []
    entity_keys: set[tuple[str, int, int, int]] = set()
    for index, entity in enumerate(raw_entities):
        if not isinstance(entity, dict) or set(entity) != {"kind", "x", "y", "rotation"}:
            errors.append(
                f"entities[{index}] must contain only kind, x, y, and rotation"
            )
            continue
        kind = entity["kind"]
        x, y, rotation = entity["x"], entity["y"], entity["rotation"]
        if not isinstance(kind, str) or kind not in ENTITY_KINDS:
            errors.append(f"entities[{index}] has unknown logical kind {kind!r}")
            continue
        if not _is_int(x) or not _is_int(y) or not _is_int(rotation):
            errors.append(f"entities[{index}] coordinates and rotation must be integers")
            continue
        if not 0 <= x < width or not 0 <= y < height:
            errors.append(f"entities[{index}] is outside blueprint bounds")
            continue
        if rotation not in ROTATIONS:
            errors.append(f"entities[{index}] rotation must be 0, 90, 180, or 270")
            continue
        key = (kind, x, y, rotation)
        if key in entity_keys:
            errors.append(f"duplicate {kind} entity at {x},{y}")
        entity_keys.add(key)
        parsed_entities.append((kind, (x, y), rotation))

    boundary = _boundary_tiles(tiles) if tiles else set()
    interior = tiles - boundary
    boundary_cover: dict[tuple[int, int], list[str]] = {}
    occupied_systems: dict[tuple[int, int], list[str]] = {}
    counts = Counter(kind for kind, _, _ in parsed_entities)

    for kind, position, _ in parsed_entities:
        if kind in BOUNDARY_KINDS:
            if position not in boundary:
                errors.append(f"{kind} at {position[0]},{position[1]} is not on boundary")
            boundary_cover.setdefault(position, []).append(kind)
        else:
            if position not in interior:
                errors.append(f"{kind} at {position[0]},{position[1]} is not on an interior floor tile")
            occupied_systems.setdefault(position, []).append(kind)

    for position in sorted(boundary, key=lambda item: (item[1], item[0])):
        cover = boundary_cover.get(position, [])
        if len(cover) != 1:
            errors.append(
                f"boundary tile {position[0]},{position[1]} must have exactly one boundary entity"
            )
    for position, kinds in occupied_systems.items():
        if len(kinds) > 1:
            errors.append(
                f"interior tile {position[0]},{position[1]} has overlapping systems: "
                + ", ".join(sorted(kinds))
            )

    exact_counts = {
        "console": 1,
        "substation": 1,
        "apc": 1,
        "dock": 1,
        "airlock": 1,
        "air_storage": 1,
        "waste_storage": 1,
    }
    for kind, expected in exact_counts.items():
        if counts[kind] != expected:
            errors.append(f"{kind} count must be exactly {expected}")
    for kind in ("apu", "thruster", "gyro"):
        if counts[kind] < 1:
            errors.append(f"{kind} count must be at least 1")
    if counts["apu"] > 4:
        errors.append("apu count must not exceed 4")

    size_for_dimensions = next(
        (
            size_name
            for size_name, dimensions in SIZE_DIMENSIONS.items()
            if dimensions == (width, height)
        ),
        None,
    )
    if size_for_dimensions is not None:
        expected_atmos_devices = {"small": 1, "medium": 2, "large": 2}[size_for_dimensions]
        for kind in ("vent", "scrubber"):
            if counts[kind] != expected_atmos_devices:
                errors.append(f"{kind} count must be exactly {expected_atmos_devices}")

    required_power = (
        counts["thruster"] * 1_500
        + counts["gyro"] * 1_500
        + counts["gunnery_server"] * 250
        + counts["gunnery_console"] * 200
        + counts["research_server"] * 200
        + counts["medical"] * 1_000
        + 4_000
    )
    available_power = counts["apu"] * 6_000
    if available_power < required_power:
        errors.append(
            f"apu power {available_power} must cover required load {required_power}"
        )

    if counts["weapon"] > 2:
        errors.append("weapon count must not exceed 2")
    if preset != "fighter" and counts["weapon"] != 0:
        errors.append("weapon is only allowed for fighter preset")
    if preset == "fighter" and counts["weapon"] != 2:
        errors.append("fighter preset must contain exactly 2 weapons")

    combat_system_counts = {
        "gunnery_server": 1 if preset == "fighter" else 0,
        "gunnery_console": 1 if preset == "fighter" else 0,
        "research_server": 1 if preset == "expedition" else 0,
    }
    for kind, expected in combat_system_counts.items():
        if counts[kind] != expected:
            errors.append(f"{kind} count must be exactly {expected}")

    for kind in MISSION_KINDS:
        if counts[kind] > 1:
            errors.append(f"{kind} count must not exceed 1")
    if preset == "expedition":
        for kind in ("medical", "science", "salvage"):
            if counts[kind] != 1:
                errors.append(f"expedition preset must contain exactly 1 {kind}")
    elif preset == "salvage":
        if counts["salvage"] != 1:
            errors.append("salvage preset must contain exactly 1 salvage entity")
        for kind in ("medical", "science"):
            if counts[kind] != 0:
                errors.append(f"salvage preset must not contain {kind}")
    elif preset == "fighter" and any(counts[kind] for kind in MISSION_KINDS):
        errors.append("fighter preset must not contain mission entities")

    # Exposed pods replace hull entities and each has a dedicated reinforced
    # bulkhead one cell inward.  No two pods may share that bulkhead.
    bulkhead_counts = Counter(
        position for kind, position, _ in parsed_entities if kind == "bulkhead"
    )
    required_bulkheads: list[tuple[int, int]] = []
    aft_y = max((y for _, y in tiles), default=-1)
    forward_y = min((y for _, y in tiles), default=-1)

    thrusters = [
        (position, rotation)
        for kind, position, rotation in parsed_entities
        if kind == "thruster"
    ]
    if preset in PRESETS and size_for_dimensions is not None:
        expected_thrusters = _thruster_count(preset, size_for_dimensions)
        if len(thrusters) != expected_thrusters:
            errors.append(f"thruster count must be exactly {expected_thrusters}")

    thruster_rotations = Counter(rotation for _, rotation in thrusters)
    if thruster_rotations[0] < 1:
        errors.append("ship must have at least one aft-facing thruster pod")
    for rotation in (180, 90, 270):
        if thruster_rotations[rotation] != 1:
            errors.append(
                f"ship must have exactly one translation thruster at rotation {rotation}"
            )
    for position, rotation in thrusters:
        x, y = position
        outward_delta = THRUSTER_OUTWARD[rotation]
        outside = (x + outward_delta[0], y + outward_delta[1])
        inward = (x - outward_delta[0], y - outward_delta[1])
        if rotation == 0 and y != aft_y:
            errors.append(f"thruster at {x},{y} with rotation 0 must be aft")
        elif rotation == 180 and y != forward_y:
            errors.append(f"thruster at {x},{y} with rotation 180 must be forward")
        elif rotation == 90:
            row_edge = min(
                (tile_x for tile_x, tile_y in tiles if tile_y == y),
                default=x,
            )
            if x != row_edge:
                errors.append(f"thruster at {x},{y} with rotation 90 must be left")
        elif rotation == 270:
            row_edge = max(
                (tile_x for tile_x, tile_y in tiles if tile_y == y),
                default=x,
            )
            if x != row_edge:
                errors.append(f"thruster at {x},{y} with rotation 270 must be right")
        if outside in tiles:
            errors.append(f"thruster at {x},{y} must face empty space outward")
        if inward not in interior:
            errors.append(f"thruster at {x},{y} must have an interior inward tile")
        if bulkhead_counts[inward] != 1:
            errors.append(
                f"thruster at {x},{y} must have exactly one inward bulkhead"
            )
        required_bulkheads.append(inward)

    weapons = [
        (position, rotation)
        for kind, position, rotation in parsed_entities
        if kind == "weapon"
    ]
    weapon_rotations = Counter(rotation for _, rotation in weapons)
    if preset == "fighter" and (
        weapon_rotations[270] != 1 or weapon_rotations[90] != 1
    ):
        errors.append("fighter must have one left and one right weapon pod")
    for position, rotation in weapons:
        x, y = position
        if rotation == 270:
            inward = (x + 1, y)
            outside = (x - 1, y)
            row_edge = min((tile_x for tile_x, tile_y in tiles if tile_y == y), default=x)
        elif rotation == 90:
            inward = (x - 1, y)
            outside = (x + 1, y)
            row_edge = max((tile_x for tile_x, tile_y in tiles if tile_y == y), default=x)
        else:
            errors.append(f"weapon at {x},{y} must have rotation 270 or 90")
            continue
        if y in {forward_y, aft_y} or x != row_edge:
            errors.append(f"weapon at {x},{y} must be on a side boundary")
        if outside in tiles:
            errors.append(f"weapon at {x},{y} must face empty space outward")
        if inward not in interior:
            errors.append(f"weapon at {x},{y} must have an interior inward tile")
        if bulkhead_counts[inward] != 1:
            errors.append(f"weapon at {x},{y} must have exactly one inward bulkhead")
        required_bulkheads.append(inward)

    if len(set(required_bulkheads)) != len(required_bulkheads):
        errors.append("each exposed pod must have a unique inward bulkhead")
    if bulkhead_counts != Counter(required_bulkheads):
        errors.append("bulkheads must correspond one-to-one with exposed pods")

    for network_name, network_kinds in ATMOS_NETWORK_KINDS.items():
        blocked_atmos_endpoints = {
            position
            for kind, position, _ in parsed_entities
            if kind in network_kinds
        }
        _, network_error = _try_build_atmos_network(
            tiles,
            (
                (kind, position, rotation)
                for kind, position, rotation in parsed_entities
                if kind in network_kinds
            ),
            blocked_atmos_endpoints,
        )
        if network_error is not None:
            errors.append(f"{network_name} atmosphere network: {network_error}")

    docks = [
        (position, rotation)
        for kind, position, rotation in parsed_entities
        if kind == "dock"
    ]
    consoles = [
        position for kind, position, _ in parsed_entities if kind == "console"
    ]
    if len(docks) == 1:
        dock_position, dock_rotation = docks[0]
        dock_x, dock_y = dock_position
        dock_inward = (dock_x, dock_y - 1)
        if dock_y != aft_y:
            errors.append("dock must be on the aft boundary")
        if dock_rotation != 180:
            errors.append("dock must have rotation 180")
        if (dock_x, dock_y + 1) in tiles:
            errors.append("dock must face empty space aft")
        if dock_inward not in interior:
            errors.append("dock must have an interior inward tile")
        if len(consoles) == 1:
            blocked = {
                position
                for kind, position, _ in parsed_entities
                if kind in PATH_BLOCKING_KINDS and kind != "console"
            }
            if not _has_walkable_path(
                tiles,
                dock_inward,
                consoles[0],
                blocked,
            ):
                errors.append("dock inward tile must have a walkable path to console")

    summary = blueprint["summary"]
    if not isinstance(summary, dict) or set(summary) != {"tileCount", "entityCount"}:
        errors.append("summary must contain only tileCount and entityCount")
    else:
        if summary["tileCount"] != len(raw_tiles):
            errors.append("summary.tileCount does not match tiles")
        if summary["entityCount"] != len(raw_entities):
            errors.append("summary.entityCount does not match entities")

    preview = blueprint["preview"]
    if not isinstance(preview, list) or not all(isinstance(line, str) for line in preview):
        errors.append("preview must be an array of strings")
    elif len(preview) != height or any(len(line) != width for line in preview):
        errors.append("preview dimensions must match width and height")
    else:
        try:
            expected_preview = render_preview(blueprint)
            if preview != expected_preview:
                errors.append("preview does not match tiles and entities")
        except (KeyError, TypeError, IndexError):
            errors.append("preview could not be reconstructed")

    return errors


def generate_ship(
    preset: str = "fighter",
    size: str = "small",
    seed: str = "auto",
    name: str = "",
) -> dict[str, Any]:
    """Generate one validated, deterministic logical ship blueprint."""

    request = validate_request(
        {
            "schemaVersion": SCHEMA_VERSION,
            "preset": preset,
            "size": size,
            "seed": seed,
            "name": name,
        }
    )
    actual_seed = request["seed"]
    width, height = SIZE_DIMENSIONS[size]
    tiles = _generate_tiles(preset, size, width, height, actual_seed)
    entities = _place_hull_entities(
        preset,
        size,
        width,
        height,
        actual_seed,
        tiles,
    )
    entities.extend(
        _place_system_entities(
            preset,
            size,
            width,
            height,
            actual_seed,
            tiles,
            entities,
        )
    )
    _orient_atmos_entities(tiles, entities)

    sorted_tiles = _sort_tiles(tiles)
    sorted_entities = _sort_entities(entities)
    blueprint: dict[str, Any] = {
        "schemaVersion": SCHEMA_VERSION,
        "generatorVersion": GENERATOR_VERSION,
        "seed": actual_seed,
        "name": request["name"] or _generate_name(preset, actual_seed),
        "preset": preset,
        "width": width,
        "height": height,
        "tiles": sorted_tiles,
        "entities": sorted_entities,
        "summary": {
            "tileCount": len(sorted_tiles),
            "entityCount": len(sorted_entities),
        },
        "preview": [],
    }
    blueprint["preview"] = render_preview(blueprint)

    errors = validate_blueprint(blueprint)
    if errors:
        raise ShipGenerationError("generated invalid blueprint: " + "; ".join(errors))
    return blueprint


def generate_ship_request(request: Any) -> dict[str, Any]:
    normalized = validate_request(request)
    return generate_ship(
        preset=normalized["preset"],
        size=normalized["size"],
        seed=normalized["seed"],
        name=normalized["name"],
    )


def _interactive_values(args: argparse.Namespace) -> None:
    print("LuaM ship generator — interactive mode")
    preset = input(f"Preset {PRESETS} [{args.preset}]: ").strip()
    size = input(f"Size {tuple(SIZE_DIMENSIONS)} [{args.size}]: ").strip()
    seed = input(f"Seed or auto [{args.seed}]: ").strip()
    name = input(f"Name (empty = generated) [{args.name}]: ").strip()
    args.preset = preset or args.preset
    args.size = size or args.size
    args.seed = seed or args.seed
    args.name = name or args.name


def _print_human(blueprint: dict[str, Any]) -> None:
    summary = blueprint["summary"]
    print(
        f"{blueprint['name']} | preset={blueprint['preset']} | "
        f"seed={blueprint['seed']} | {blueprint['width']}x{blueprint['height']} | "
        f"tiles={summary['tileCount']} entities={summary['entityCount']}"
    )
    print()
    for line in blueprint["preview"]:
        print(line)
    print()
    print(
        "Legend: # wall, o window, A airlock, D dock, C console, "
        "T thruster, W weapon, v vent, s scrubber, a air reserve, w waste reserve"
    )


def _load_blueprint(path: Path) -> dict[str, Any]:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise ShipGenerationError(f"failed to read blueprint: {exc}") from exc
    if not isinstance(data, dict):
        raise ShipGenerationError("blueprint JSON root must be an object")
    return data


def build_argument_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--preset", choices=PRESETS, default="fighter")
    parser.add_argument("--size", choices=tuple(SIZE_DIMENSIONS), default="small")
    parser.add_argument("--seed", default="auto")
    parser.add_argument("--name", default="")
    parser.add_argument("--json", action="store_true", help="print the full JSON blueprint")
    parser.add_argument("--interactive", action="store_true", help="prompt for request values")
    parser.add_argument("--output", type=Path, help="also write the JSON blueprint to this path")
    parser.add_argument("--validate", type=Path, metavar="FILE", help="validate an existing JSON blueprint")
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    parser = build_argument_parser()
    args = parser.parse_args(argv)
    try:
        if args.validate is not None:
            blueprint = _load_blueprint(args.validate)
            errors = validate_blueprint(blueprint)
            if errors:
                for error in errors:
                    print(f"ERROR: {error}", file=sys.stderr)
                return 1
            print(f"Valid LuaM ship blueprint: {args.validate}")
            return 0

        if args.interactive:
            _interactive_values(args)

        blueprint = generate_ship(
            preset=args.preset,
            size=args.size,
            seed=args.seed,
            name=args.name,
        )
        json_text = json.dumps(
            blueprint,
            ensure_ascii=False,
            indent=2,
            separators=(",", ": "),
        )
        if args.output is not None:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(json_text + "\n", encoding="utf-8")

        if args.json:
            print(json_text)
        else:
            _print_human(blueprint)
        return 0
    except ShipGenerationError as exc:
        print(f"ship generation failed: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
