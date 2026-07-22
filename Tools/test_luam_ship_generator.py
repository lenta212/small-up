#!/usr/bin/env python3

from __future__ import annotations

import copy
import json
import os
import sys
import threading
import unittest
import urllib.error
import urllib.request
from collections import Counter
from http.server import ThreadingHTTPServer
from pathlib import Path


TOOLS_DIR = Path(__file__).resolve().parent
if str(TOOLS_DIR) not in sys.path:
    sys.path.insert(0, str(TOOLS_DIR))

import luam_ship_generator as generator


class ShipGeneratorTest(unittest.TestCase):
    def test_saved_ship_manifest_analysis_is_bounded_and_deterministic(self) -> None:
        counts = {
            "ComputerShuttle": 1,
            "ThrusterNfsd": 4,
            "ResearchAndDevelopmentServer": 1,
            "WallReinforced": 20,
        }
        request = {
            "schemaVersion": generator.SAVED_SHIP_ANALYSIS_SCHEMA_VERSION,
            "snapshotFormatVersion": 1,
            "entityCount": sum(counts.values()),
            "payloadSizeBytes": 4096,
            "prototypeManifestHash": generator._saved_ship_manifest_hash(counts),
            "prototypes": [
                {"id": prototype, "count": count}
                for prototype, count in reversed(list(counts.items()))
            ],
        }

        analysis = generator.analyze_saved_ship_request(request)

        self.assertEqual(analysis["prototypeCount"], 4)
        self.assertEqual(analysis["capabilities"]["navigation"], 1)
        self.assertEqual(analysis["capabilities"]["propulsion"], 4)
        self.assertEqual(analysis["capabilities"]["research"], 1)
        self.assertEqual(analysis["suggestedPreset"], "expedition")
        self.assertEqual(analysis["topPrototypes"][0], {"id": "WallReinforced", "count": 20})
        self.assertNotIn("shipId", analysis)
        self.assertNotIn("owner", analysis)

    def test_saved_ship_manifest_analysis_rejects_tampering(self) -> None:
        request = {
            "schemaVersion": generator.SAVED_SHIP_ANALYSIS_SCHEMA_VERSION,
            "snapshotFormatVersion": 1,
            "entityCount": 2,
            "payloadSizeBytes": 128,
            "prototypeManifestHash": "0" * 64,
            "prototypes": [{"id": "Thruster", "count": 2}],
        }

        with self.assertRaisesRegex(generator.ShipGenerationError, "manifest hash mismatch"):
            generator.analyze_saved_ship_request(request)

    def test_every_preset_and_size_obeys_contract(self) -> None:
        for preset in generator.PRESETS:
            for size, dimensions in generator.SIZE_DIMENSIONS.items():
                with self.subTest(preset=preset, size=size):
                    blueprint = generator.generate_ship(preset, size, "contract-seed")
                    self.assertEqual(generator.validate_blueprint(blueprint), [])
                    self.assertEqual(
                        (blueprint["width"], blueprint["height"]),
                        dimensions,
                    )
                    self.assertLessEqual(blueprint["summary"]["tileCount"], 400)
                    self.assertLessEqual(blueprint["summary"]["entityCount"], 512)

                    for tile in blueprint["tiles"]:
                        self.assertGreaterEqual(tile["x"], 0)
                        self.assertGreaterEqual(tile["y"], 0)
                        self.assertLess(tile["x"], blueprint["width"])
                        self.assertLess(tile["y"], blueprint["height"])

    def test_same_request_is_byte_deterministic(self) -> None:
        first = generator.generate_ship("fighter", "large", "424242", "Test Ship")
        second = generator.generate_ship("fighter", "large", "424242", "Test Ship")
        self.assertEqual(first, second)
        self.assertEqual(
            json.dumps(first, ensure_ascii=False, separators=(",", ":")),
            json.dumps(second, ensure_ascii=False, separators=(",", ":")),
        )

    def test_different_seed_changes_replayable_blueprint(self) -> None:
        first = generator.generate_ship("expedition", "medium", "seed-a")
        second = generator.generate_ship("expedition", "medium", "seed-b")
        self.assertNotEqual(first["seed"], second["seed"])
        self.assertNotEqual(first["tiles"], second["tiles"])

    def test_auto_seed_can_be_replayed(self) -> None:
        generated = generator.generate_ship("salvage", "small", "auto")
        self.assertNotEqual(generated["seed"], "auto")
        replayed = generator.generate_ship(
            "salvage",
            "small",
            generated["seed"],
        )
        self.assertEqual(generated, replayed)

    def test_boundary_is_covered_exactly_once_and_systems_are_interior(self) -> None:
        blueprint = generator.generate_ship("fighter", "large", "airtight")
        tiles = {(tile["x"], tile["y"]) for tile in blueprint["tiles"]}
        boundary = generator._boundary_tiles(tiles)
        cover: Counter[tuple[int, int]] = Counter()

        for entity in blueprint["entities"]:
            position = (entity["x"], entity["y"])
            if entity["kind"] in generator.BOUNDARY_KINDS:
                self.assertIn(position, boundary)
                cover[position] += 1
            else:
                self.assertIn(position, tiles - boundary)

        self.assertEqual(set(cover), boundary)
        self.assertTrue(all(count == 1 for count in cover.values()))

    def test_power_and_mission_kind_counts(self) -> None:
        for preset in generator.PRESETS:
            blueprint = generator.generate_ship(preset, "medium", "systems")
            counts = Counter(entity["kind"] for entity in blueprint["entities"])
            with self.subTest(preset=preset):
                self.assertEqual(counts["console"], 1)
                self.assertEqual(counts["substation"], 1)
                self.assertEqual(counts["apc"], 1)
                self.assertGreaterEqual(counts["apu"], 1)
                self.assertEqual(
                    counts["thruster"],
                    generator._thruster_count(preset, "medium"),
                )
                self.assertEqual(counts["gyro"], generator._gyro_count("medium"))
                self.assertLessEqual(counts["weapon"], 2)
                self.assertEqual(
                    counts["bulkhead"],
                    counts["thruster"] + counts["weapon"],
                )
                required_power = (
                    counts["thruster"] * 1_500
                    + counts["gyro"] * 1_500
                    + counts["gunnery_server"] * 250
                    + counts["gunnery_console"] * 200
                    + counts["research_server"] * 200
                    + counts["medical"] * 1_000
                    + 4_000
                )
                self.assertEqual(counts["apu"], (required_power + 5_999) // 6_000)
                self.assertLessEqual(counts["apu"], 4)

                if preset == "fighter":
                    self.assertEqual(counts["weapon"], 2)
                    self.assertEqual(counts["gunnery_server"], 1)
                    self.assertEqual(counts["gunnery_console"], 1)
                    self.assertEqual(counts["research_server"], 0)
                    self.assertFalse(any(counts[kind] for kind in generator.MISSION_KINDS))
                elif preset == "expedition":
                    self.assertEqual(counts["weapon"], 0)
                    self.assertEqual(counts["gunnery_server"], 0)
                    self.assertEqual(counts["gunnery_console"], 0)
                    self.assertEqual(counts["research_server"], 1)
                    for kind in generator.MISSION_KINDS:
                        self.assertEqual(counts[kind], 1)
                else:
                    self.assertEqual(counts["weapon"], 0)
                    self.assertEqual(counts["gunnery_server"], 0)
                    self.assertEqual(counts["gunnery_console"], 0)
                    self.assertEqual(counts["research_server"], 0)
                    self.assertEqual(counts["salvage"], 1)
                    self.assertEqual(counts["science"], 0)
                    self.assertEqual(counts["medical"], 0)

    def test_atmos_device_and_bounded_storage_counts(self) -> None:
        expected_devices = {"small": 1, "medium": 2, "large": 2}

        for preset in generator.PRESETS:
            for size in generator.SIZE_DIMENSIONS:
                with self.subTest(preset=preset, size=size):
                    blueprint = generator.generate_ship(preset, size, "atmos-counts")
                    counts = Counter(
                        entity["kind"] for entity in blueprint["entities"]
                    )
                    self.assertEqual(counts["air_storage"], 1)
                    self.assertEqual(counts["waste_storage"], 1)
                    self.assertEqual(counts["vent"], expected_devices[size])
                    self.assertEqual(counts["scrubber"], expected_devices[size])

    def test_atmos_network_routes_are_safe_connected_and_deterministic(self) -> None:
        for preset in generator.PRESETS:
            for size in generator.SIZE_DIMENSIONS:
                blueprint = generator.generate_ship(preset, size, "atmos-routes")
                tiles = {(tile["x"], tile["y"]) for tile in blueprint["tiles"]}
                interior = tiles - generator._boundary_tiles(tiles)

                for network_name, network_kinds in generator.ATMOS_NETWORK_KINDS.items():
                    with self.subTest(
                        preset=preset,
                        size=size,
                        network=network_name,
                    ):
                        endpoints = [
                            (
                                entity["kind"],
                                (entity["x"], entity["y"]),
                                entity["rotation"],
                            )
                            for entity in blueprint["entities"]
                            if entity["kind"] in network_kinds
                        ]
                        blocked_endpoints = {
                            position for _, position, _ in endpoints
                        }
                        first_route, first_error = generator._try_build_atmos_network(
                            tiles,
                            endpoints,
                            blocked_endpoints,
                        )
                        replayed_route, replayed_error = (
                            generator._try_build_atmos_network(
                                tiles,
                                reversed(endpoints),
                                blocked_endpoints,
                            )
                        )

                        self.assertIsNone(first_error)
                        self.assertIsNone(replayed_error)
                        self.assertIsNotNone(first_route)
                        self.assertEqual(first_route, replayed_route)
                        assert first_route is not None
                        self.assertTrue(generator._connected_tiles(first_route))
                        self.assertLessEqual(
                            first_route,
                            interior - blocked_endpoints,
                        )
                        self.assertLessEqual(
                            {
                                generator._atmos_connection(position, rotation)
                                for _, position, rotation in endpoints
                            },
                            first_route,
                        )

    def test_validator_rejects_atmos_endpoint_facing_unsafe_tile(self) -> None:
        blueprint = generator.generate_ship(
            "expedition",
            "small",
            "atmos-rotation",
        )
        tiles = {(tile["x"], tile["y"]) for tile in blueprint["tiles"]}
        interior = tiles - generator._boundary_tiles(tiles)
        tampered = copy.deepcopy(blueprint)
        expected_error = None

        for network_name, network_kinds in generator.ATMOS_NETWORK_KINDS.items():
            endpoints = [
                entity
                for entity in tampered["entities"]
                if entity["kind"] in network_kinds
            ]
            blocked_endpoints = {
                (entity["x"], entity["y"]) for entity in endpoints
            }
            safe_route_tiles = interior - blocked_endpoints
            for entity in endpoints:
                position = (entity["x"], entity["y"])
                unsafe_rotation = next(
                    (
                        rotation
                        for rotation in sorted(generator.ROTATIONS)
                        if rotation != entity["rotation"]
                        and generator._atmos_connection(position, rotation)
                        not in safe_route_tiles
                    ),
                    None,
                )
                if unsafe_rotation is None:
                    continue
                entity["rotation"] = unsafe_rotation
                expected_error = (
                    f"{network_name} atmosphere network: {entity['kind']} at "
                    f"{position[0]},{position[1]} does not face a safe interior pipe tile"
                )
                break
            if expected_error is not None:
                break

        self.assertIsNotNone(expected_error)
        self.assertIn(expected_error, generator.validate_blueprint(tampered))

    def test_validator_rejects_missing_or_duplicate_atmos_storage(self) -> None:
        blueprint = generator.generate_ship("fighter", "small", "atmos-storage")

        missing = copy.deepcopy(blueprint)
        missing["entities"] = [
            entity
            for entity in missing["entities"]
            if entity["kind"] != "air_storage"
        ]
        missing["summary"]["entityCount"] = len(missing["entities"])
        missing["preview"] = generator.render_preview(missing)
        self.assertIn(
            "air_storage count must be exactly 1",
            generator.validate_blueprint(missing),
        )

        duplicate = copy.deepcopy(blueprint)
        duplicate["entities"].append(
            copy.deepcopy(
                next(
                    entity
                    for entity in duplicate["entities"]
                    if entity["kind"] == "waste_storage"
                )
            )
        )
        duplicate["entities"] = generator._sort_entities(duplicate["entities"])
        duplicate["summary"]["entityCount"] = len(duplicate["entities"])
        duplicate["preview"] = generator.render_preview(duplicate)
        self.assertIn(
            "waste_storage count must be exactly 1",
            generator.validate_blueprint(duplicate),
        )

    def test_validator_rejects_untrusted_gas_contract_extensions(self) -> None:
        blueprint = generator.generate_ship("salvage", "small", "untrusted-gas")

        unknown_kind = copy.deepcopy(blueprint)
        light = next(
            entity for entity in unknown_kind["entities"] if entity["kind"] == "light"
        )
        light["kind"] = "gas_miner"
        self.assertTrue(
            any(
                "unknown logical kind 'gas_miner'" in error
                for error in generator.validate_blueprint(unknown_kind)
            )
        )

        root_extension = copy.deepcopy(blueprint)
        root_extension["gasPlan"] = {"source": "unbounded"}
        self.assertIn(
            "unknown blueprint fields: gasPlan",
            generator.validate_blueprint(root_extension),
        )

    def test_exposed_pods_have_unique_inward_bulkheads(self) -> None:
        blueprint = generator.generate_ship("fighter", "large", "pod-geometry")
        tiles = {(tile["x"], tile["y"]) for tile in blueprint["tiles"]}
        interior = tiles - generator._boundary_tiles(tiles)
        bulkheads = {
            (entity["x"], entity["y"])
            for entity in blueprint["entities"]
            if entity["kind"] == "bulkhead"
        }
        required: set[tuple[int, int]] = set()
        thruster_rotations: Counter[int] = Counter()

        for entity in blueprint["entities"]:
            kind = entity["kind"]
            position = (entity["x"], entity["y"])
            if kind == "thruster":
                rotation = entity["rotation"]
                thruster_rotations[rotation] += 1
                outward_delta = generator.THRUSTER_OUTWARD[rotation]
                outside = (
                    position[0] + outward_delta[0],
                    position[1] + outward_delta[1],
                )
                inward = (
                    position[0] - outward_delta[0],
                    position[1] - outward_delta[1],
                )
                self.assertNotIn(outside, tiles)
            elif kind == "weapon" and entity["rotation"] == 270:
                self.assertNotIn((position[0] - 1, position[1]), tiles)
                inward = (position[0] + 1, position[1])
            elif kind == "weapon" and entity["rotation"] == 90:
                self.assertNotIn((position[0] + 1, position[1]), tiles)
                inward = (position[0] - 1, position[1])
            else:
                continue
            self.assertIn(inward, interior)
            self.assertIn(inward, bulkheads)
            self.assertNotIn(inward, required)
            required.add(inward)

        self.assertEqual(required, bulkheads)
        self.assertGreaterEqual(thruster_rotations[0], 1)
        self.assertEqual(thruster_rotations[180], 1)
        self.assertEqual(thruster_rotations[90], 1)
        self.assertEqual(thruster_rotations[270], 1)

    def test_request_schema_rejects_unknown_or_missing_fields(self) -> None:
        request = {
            "schemaVersion": 1,
            "preset": "fighter",
            "size": "small",
            "seed": "one",
            "name": "",
        }
        self.assertEqual(generator.generate_ship_request(request)["seed"], "one")

        extra = {**request, "prototype": "WeaponDebug"}
        with self.assertRaisesRegex(generator.ShipGenerationError, "unknown request fields"):
            generator.generate_ship_request(extra)

        missing = dict(request)
        del missing["size"]
        with self.assertRaisesRegex(generator.ShipGenerationError, "missing request fields"):
            generator.generate_ship_request(missing)

        invalid_seed = {**request, "seed": "Uppercase Seed"}
        with self.assertRaisesRegex(generator.ShipGenerationError, "seed must match"):
            generator.generate_ship_request(invalid_seed)

        long_name = {**request, "name": "x" * 33}
        with self.assertRaisesRegex(generator.ShipGenerationError, "at most 32"):
            generator.generate_ship_request(long_name)

    def test_validator_detects_boundary_hole_and_illegal_weapon(self) -> None:
        blueprint = generator.generate_ship("expedition", "small", "tamper")
        damaged = copy.deepcopy(blueprint)
        damaged["entities"] = [
            entity
            for entity in damaged["entities"]
            if entity["kind"] != "dock"
        ]
        damaged["summary"]["entityCount"] = len(damaged["entities"])
        damaged["preview"] = generator.render_preview(damaged)
        errors = generator.validate_blueprint(damaged)
        self.assertTrue(any("boundary tile" in error for error in errors))
        self.assertIn("dock count must be exactly 1", errors)

        armed = copy.deepcopy(blueprint)
        interior = {
            (tile["x"], tile["y"])
            for tile in armed["tiles"]
        } - generator._boundary_tiles(
            {(tile["x"], tile["y"]) for tile in armed["tiles"]}
        )
        occupied = {
            (entity["x"], entity["y"])
            for entity in armed["entities"]
            if entity["kind"] not in generator.BOUNDARY_KINDS
        }
        position = sorted(interior - occupied)[0]
        armed["entities"].append(generator._entity("weapon", position))
        armed["entities"] = generator._sort_entities(armed["entities"])
        armed["summary"]["entityCount"] = len(armed["entities"])
        armed["preview"] = generator.render_preview(armed)
        self.assertIn(
            "weapon is only allowed for fighter preset",
            generator.validate_blueprint(armed),
        )


class ShipGeneratorGatewayTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        # Import after generator setup so both script and unittest discovery modes work.
        import luam_ai_gateway

        cls._old_token = os.environ.pop("LUAM_AI_GATEWAY_TOKEN", None)
        cls._server = ThreadingHTTPServer(("127.0.0.1", 0), luam_ai_gateway.Handler)
        cls._thread = threading.Thread(target=cls._server.serve_forever, daemon=True)
        cls._thread.start()
        cls._url = f"http://127.0.0.1:{cls._server.server_port}/generate_ship"

    @classmethod
    def tearDownClass(cls) -> None:
        cls._server.shutdown()
        cls._server.server_close()
        cls._thread.join(timeout=2)
        if cls._old_token is not None:
            os.environ["LUAM_AI_GATEWAY_TOKEN"] = cls._old_token

    def _post(self, body: dict[str, object]) -> tuple[int, dict[str, object]]:
        request = urllib.request.Request(
            self._url,
            data=json.dumps(body).encode("utf-8"),
            method="POST",
            headers={"Content-Type": "application/json"},
        )
        try:
            with urllib.request.urlopen(request, timeout=5) as response:
                return response.status, json.loads(response.read().decode("utf-8"))
        except urllib.error.HTTPError as exc:
            return exc.code, json.loads(exc.read().decode("utf-8"))

    def test_generate_ship_endpoint_returns_valid_blueprint(self) -> None:
        status, response = self._post(
            {
                "schemaVersion": 1,
                "preset": "fighter",
                "size": "small",
                "seed": "gateway-seed",
                "name": "Gateway Test",
            }
        )
        self.assertEqual(status, 200)
        self.assertEqual(generator.validate_blueprint(response), [])
        self.assertEqual(response["name"], "Gateway Test")

    def test_generate_ship_endpoint_rejects_untrusted_fields(self) -> None:
        status, response = self._post(
            {
                "schemaVersion": 1,
                "preset": "fighter",
                "size": "small",
                "seed": "gateway-seed",
                "name": "Gateway Test",
                "prototype": "WeaponDebug",
            }
        )
        self.assertEqual(status, 400)
        self.assertIn("error", response)


if __name__ == "__main__":
    unittest.main()
