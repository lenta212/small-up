# Ship generator: air-system checkpoint

Date: 2026-07-15

## Current state

The local procedural ship generator now builds bounded, automatically connected life support:

- `Tools/luam_ship_generator.py` emits deterministic version `1.1.0` blueprints while keeping `schemaVersion: 1`.
- The public blueprint contract contains one logical `air_storage` and one `waste_storage`; it never accepts prototype IDs, pipe layers, gas mixtures, capacities, or an external gas plan.
- `Content.Server/_LuaM/ShipGen/LuaMShipGeneratorSystem.cs` derives and validates all hidden atmosphere infrastructure server-side.
- `Content.IntegrationTests/Tests/_LuaM/LuaMShipGeneratorValidationTest.cs` covers the sealed contract and derived plan.
- The checked-in cross-language fixture contains exact Python output for every preset/size pair, and C# deserializes, validates, and builds all nine cases.
- Runtime tests cover atmosphere initialization, persistent pod isolation, live node connectivity, powered gas transfer, and transactional rollback.

## Implemented air system

- One supply network connects the finite `AirCanister` reserve to every `GasVentPump` through a same-tile `GasPort`.
- One independent waste network connects every `GasVentScrubber` to a finite empty `StorageCanister` through its own `GasPort`.
- Supply uses trusted `GasPipeFourway` entities on the primary layer; waste uses trusted `GasPipeFourwayAlt1` entities on the secondary layer.
- Endpoint rotations must face a safe interior route tile. The server recomputes both connected routes and rejects unsafe, disconnected, boundary, or oversized plans.
- Vents, scrubbers, ports, and pipes are assigned and checked against their trusted layers before the crossing networks can connect.
- No gas miners or other unbounded gas sources are accepted.
- Thruster and weapon pod tiles are derived from the already validated entity geometry and receive persistent anchored `AtmosFixBlockerMarker` entities with server-added `AirtightComponent` isolation.
- `fixgridatmos` therefore initializes the sealed cabin with air while leaving exposed pods in vacuum. The markers remain idempotent, and their airtight component prevents later atmosphere processing from refilling a pod through an open tangent or adjacent dock.
- Construction still rolls back and deletes the entire generated grid if any trusted prototype cannot spawn, anchor, or take its required layer.

## Verification

Passed locally on 2026-07-15:

- `python -m unittest Tools.test_luam_ship_generator Tools.test_luam_ship_generator_contract_fixture -v`: 17/17.
- Deterministic stress matrix: 4500/4500 blueprints across every preset and size.
- `python Tools/test_luam_ai_gateway.py`: passed; gateway reports `shipGeneratorVersion=1.1.0`.
- Cross-language and engine matrix: all nine exact Python blueprints pass the production C# JSON contract, validator, and real grid construction.
- Combined ship-generator integration run: 18/18, including validation, seed-map loading, the 3x3 runtime matrix, operational atmosphere, and rollback.
- The 3x3 runtime matrix checks every thruster/weapon pod after 30 ticks; every pod remains vacuum and every cabin remains pressurized.
- The operational runtime test verifies all endpoint rotations, settled APC power, enabled/default vent and scrubber modes, distinct primary/secondary `NodeGroup` networks, bidirectional port-to-canister reachability, an idempotent second `fixgridatmos`, measurable finite-air delivery, and measurable CO2 transfer into finite waste storage.
- The rollback test injects a post-load atmosphere-layer failure, then proves that every created entity is deleted, the map entity set returns exactly to baseline, and the rollback path logs once.
- `dotnet build Content.Server/Content.Server.csproj --no-restore --nologo /clp:ErrorsOnly`: zero errors (pre-existing repository warnings remain).

## Deployment state

Remote deployment remains intentionally frozen. All changes and verification are local only unless deployment is explicitly requested.
