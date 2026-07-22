#!/usr/bin/env python3

from __future__ import annotations

import json
import sys
import unittest
from pathlib import Path


TOOLS_DIR = Path(__file__).resolve().parent
if str(TOOLS_DIR) not in sys.path:
    sys.path.insert(0, str(TOOLS_DIR))

import luam_ship_generator as generator


FIXTURE_PATH = TOOLS_DIR / "fixtures" / "luam_ship_generator_contract_v1.json"
FIXTURE_VERSION = 1


class ShipGeneratorCrossLanguageFixtureTest(unittest.TestCase):
    def test_checked_in_blueprints_match_current_python_generator(self) -> None:
        fixture = json.loads(FIXTURE_PATH.read_text(encoding="utf-8"))
        self.assertEqual(
            set(fixture),
            {"fixtureVersion", "seed", "cases"},
        )
        self.assertEqual(fixture["fixtureVersion"], FIXTURE_VERSION)

        expected_matrix = [
            (preset, size)
            for preset in generator.PRESETS
            for size in generator.SIZE_DIMENSIONS
        ]
        actual_matrix = [
            (case["preset"], case["size"])
            for case in fixture["cases"]
        ]
        self.assertEqual(actual_matrix, expected_matrix)

        seed = fixture["seed"]
        for case in fixture["cases"]:
            preset = case["preset"]
            size = case["size"]
            with self.subTest(preset=preset, size=size):
                self.assertEqual(set(case), {"preset", "size", "blueprint"})
                self.assertEqual(
                    case["blueprint"],
                    generator.generate_ship(preset, size, seed),
                    "Refresh the cross-language fixture when the intentional "
                    "Python blueprint contract changes.",
                )


if __name__ == "__main__":
    unittest.main()
