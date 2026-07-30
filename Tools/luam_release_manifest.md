# LuaM Local Release Manifest

Current policy (2026-07-30): batch `20260730-comprehensive-connected-update` completed in production as rollout `luam-20260730-081938`. The matching client, procedural gateway settings, stored-ship compatibility, viewport/HUD stability, Unknown/Aibolit behavior, radar/targeting, late-join, and selected safety updates are live. Remote deployment is frozen again and the batch has returned to `local-package-only`; every later production mutation requires fresh explicit authorization, policy-bound artifacts, all release gates, a checked data backup, and the active-ship save barrier.

The machine-readable policy lives in `Tools\luam_release_policy.json` and is the only authority for the release gate and remote freeze. A missing or invalid JSON policy blocks remote deployment; this markdown manifest is documentation, not a fallback policy.

This manifest exists to prevent the local LuaM feature pack from being partially released. During implementation use `Tools\prepare_luam_hotfix.ps1` for scoped local checks. Before deployment use `Tools\ship_luam_release.ps1` to drive the complete production gate and dry-runs through the existing guarded scripts.

## Observe-First Anti-Cheat Follow-Up — 2026-07-16

The authorized follow-up adds server-side validation and bounded telemetry for remote bound-UI requests, implausible shooting coordinates or targets, and predicted-hit floods. Suspicion scores decay over time and alert administrators with player attribution; automatic kicks and bans remain disabled until production telemetry establishes safe thresholds. The matching client archive is required because shared shooting and UI request paths changed.

## Authorized Post-Rollout Follow-Up — 2026-07-16

The reviewed follow-up is now deployed in production. Post-deployment checks passed, `remoteDeployFrozen=true`, `deploymentAuthorization.state=frozen`, and the pending batch has returned to `local-package-only`.

The production package deployed on 2026-07-16 included the following reviewed slice. Any later follow-up in these scopes requires fresh strict readiness, source verification, binary receipts, dry-runs, data backup, explicit authorization, and post-deployment verification:

- Radar/fire-control missile-vector networking and map/grid-correct rendering, plus IFF and dock-visibility controls.
- The LuaM PDV Scorpion voucher, shipyard listing, canonical Tier 1 map, and its Helios reference without retaining the conflicting Mono listing.
- PDA/bank generation and profile guards that prevent stale asynchronous transfer results from applying to a replacement character, plus exact MonoCoins compare-and-swap accounting for long-term credits, debits, and compensation.
- Exact-quantity vending purchases from 1 to 30 items: the client shows unit price, selected quantity, and total price; the server reserves exact stock and charges the batch in one transaction before issuing every item.
- A per-user FIFO profile-mutation gate shared by preference lifecycle and bank operations. Durable writes are DB-first, and post-commit callbacks revalidate the exact session, entity, slot, and `Profile.Id` before applying world or cache state.
- SQLite/PostgreSQL expedition and progression shadow persistence: provider-matched models, migrations and snapshots; revision-checked manifests/checkpoints and shift sealing; idempotent expedition mutation checkpoints; an append-only XP ledger; and integration coverage for replay, conflicts, rollback, and provider parity. Gameplay award producers and ECS expedition materialization are still outside this slice.
- Profile-bound deep cryo persistence for both cryo implementations: recursive body/inventory snapshots, atomic store/claim/consume transitions, a single restore lease, append-only operation proofs, quarantine on incompatible payloads, and fail-closed removal of old round job/access authority before wake. A fresh ordinary spawn explicitly consumes the stored snapshot; joining the lobby does not.
- Physical AI-base entities remain disabled by default. If explicitly enabled, startup and producer admission enforce per-map/live-per-round/spawn-per-round caps of `1/2/2` anchors, `5/10/20` zones, `1/2/2` ships, `6/12/12` drones, `32/64/128` traces, and `4/8/16` drops; bounded update slices, disable/round cleanup, and rejection metrics remain active.
- Pow3r-linked power-cell, fluid-drain, mech, and mixed-power receiver changes outside automatic `_LuaM` directories, including the non-removable integral EMU equipment contract, plus the rendering package update in `Directory.Packages.props`.
- The restored NF shuttle/content slice, including Caladrius, Spirit, Tyne, their shipyard prototypes and maps, the Spirit guide page, and the Preflight Checklist guide dependency.
- Required C# systems, locales, audio, prototypes, RSI metadata, and PNG assets outside `_LuaM` ownership paths, including container-count storage visuals and their wall-locker fill states.
- A server-only packaging canary that must be absent from the client archive and present in the freshly built server archive; the surface audit fails either direction closed.

Runtime map dependencies are packaged, not allowlisted. In particular, the package carries the Scorpion voucher/listing/map chain and the Caladrius/Spirit/Tyne shipyard/listing/map chain together with their guidebook pages. `Resources/Maps/_LuaM/**` is a full release directory; the former six NF vessel map/prototype pairs plus Spirit and Tyne are explicit payload files, while other changed NF shuttle dependencies are selected fail-closed through the release scopes.

`test_results/` remains local verification evidence and is intentionally excluded from both payload selection and changed-file scope accounting.

## Policy-Owned Release Gate

`releaseGate` in `Tools\luam_release_policy.json` is the single source of truth for production test suites, critical test files, smoke checks, and gate tooling. The policy also owns integration package scopes, safe local-artifact exclusions, the outside-package allowlist, approved release additions, and runtime dependencies. The package builder, readiness checker, and package verifier consume these contracts through `Tools\luam_release_contract.ps1`; critical tests are not duplicated in their hardcoded historical payload lists.

The current production test contract runs the full `FullyQualifiedName~LuaM` filters for both test projects and explicitly binds these critical files to those suites:

- `Content.IntegrationTests/Tests/_LuaM/LuaMBankAndPdaContractsTest.cs`
- `Content.IntegrationTests/Tests/_LuaM/LuaMBankDurableMutationContractTest.cs`
- `Content.IntegrationTests/Tests/_LuaM/LuaMBankPersistenceTest.cs`
- `Content.IntegrationTests/Tests/_LuaM/LuaMAiPhysicalBaseDisabledTest.cs`
- `Content.IntegrationTests/Tests/_LuaM/LuaMAiPhysicalBaseGrowthLimitTest.cs`
- `Content.IntegrationTests/Tests/_LuaM/LuaMSectorStoryTest.cs`
- `Content.IntegrationTests/Tests/_LuaM/LuaMDurablePersistenceTest.cs`
- `Content.IntegrationTests/Tests/_LuaM/LuaMDeepCryoPersistenceTest.cs`
- `Content.IntegrationTests/Tests/_LuaM/LuaMDeepCryoRuntimeTest.cs`
- `Content.IntegrationTests/Tests/_LuaM/LuaMRadarIsolationTest.cs`
- `Content.IntegrationTests/Tests/_LuaM/LuaMTargetSeekingMapIsolationTest.cs`
- `Content.IntegrationTests/Tests/_LuaM/LuaMProgressionBuildRulesTest.cs`
- `Content.IntegrationTests/Tests/_LuaM/LuaMTransactionalCommerceContractTest.cs`
- `Content.IntegrationTests/Tests/_LuaM/LuaMVendingBatchPurchaseTest.cs`
- `Content.IntegrationTests/Tests/_LuaM/LuaMPaidLoadoutLifecycleTest.cs`
- `Content.IntegrationTests/Tests/_LuaM/LuaMMonoCoinsTransferPersistenceTest.cs`
- `Content.Tests/Client/_LuaM/LuaMRadarGeometryTest.cs`
- `Content.Tests/Server/_LuaM/LuaMRadarRangeGeometryTest.cs`

The policy also owns the always-on AI gateway smoke and the `-RunLocalSmoke` local server/client stack smoke. A production package verifies fail-closed unless its embedded readiness evidence has the same policy SHA256 and records every `requiredForProduction` test and smoke step as passed. Run the lightweight contract check without building or launching the stack with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\test_luam_release_contract.ps1 -Json
```

The contract test also runs `python Tools\validate_luam_feature_pack.py --self-test`. Its tamper cases bind the validator to the current DB-first/exact-profile BankSystem API and reject the removed `SaveCharacterSlotAsync` path, slot-only balance writes, or a world finalizer moved ahead of the durable debit.

## Release Scope

The local pack currently covers:

- End-to-end LuaM and character TTS delivery through the local Piper gateway, with bounded voice caching and explicit client audio events.
- Animal breeding attempts scheduled once per hour without catch-up bursts after long pauses.
- Bounded gateway destination generation with TTL cleanup and a hard retained-map cap.
- Bison shuttle pest cleanup with its timed mouse spawner removed.
- LuaM dynamic-event active-site growth limits and rescue after-action records.
- Guarded static-client, AI-gateway, and admin-log archive operations tools.
- LuaM AI director administration UI and commands.
- AI chat/radio interaction through explicit markers such as `ИИ`, `иишка`, `luam`, and `/luam`.
- AI Director review API: `POST /review` returns structured influence/process, risk, tempo, economy, crew-load, safety, and recommended-action remarks for admins.
- AI Director admin UI keeps the latest AI review separately, can copy it to clipboard, can save it to the server log, and shows a short review history.
- PDA sector status display without a free-form AI message input.
- PDA bank transfers by short copy-friendly database-backed bank ID, with a confirmation preview showing recipient, ID, amount, and operation ID.
- An atomic transfer journal keyed by durable operation ID, restart reconciliation, explicit acknowledgement, and idempotent replay protection against duplicate debits. Persistent-to-persistent MonoCoins transfers use their own journaled, atomic database transaction.
- Generic Bank finalize callbacks used by the six ATM/cargo/shipyard/commerce callers are exception-atomic only while the current server process remains alive. They do not make payment plus world fulfillment crash-atomic: a process/host crash can still occur after durable settlement and before the caller finishes its world mutation.
- FIFO-exclusive profile lifecycle mutations and non-blocking bank admission, with exact durable `Profile.Id`/slot/session finalizers. A universal crash-safe commerce saga still requires migrating all six callers to stable operation IDs plus durable fulfillment/recovery state; this pending slice must not be described as providing that guarantee.
- Donation shop state, PDA listings, and manual account access.
- Sector story memory, dynamic tasks, route text, site notes, evidence, terminal/report flows.
- Dynamic quest debris and periodic hostile contacts on every fifth debris site.
- Four staggered radar-only contact profiles (civilian, cargo, distress, unknown) on the active primary sector map, with a global hard cap of four, player-presence gating, collision-free routes, expiry, and round cleanup.
- Physical sector-terminal interception that converts contacts into bounded route, distress, or cargo tasks without NPC ships or debris grids; recoveries must be delivered back to a terminal and are removed on completion, expiry, reset, or round cleanup.
- Default-off physical AI-base machinery with fail-closed admission, fixed per-map/live/spawn caps, bounded work slices, disable cleanup, and round cleanup for physical anchors, work zones, logistics ships, drones, traces, and supply drops.
- Per-map animal population reports and throttled overflow alerts, plus a fingerprint-confirmed cleanup command restricted to safe excess pests while preserving pets, livestock, named/player-controlled creatures, and species minimums.
- Subspace/stargate-style temporary portal actions.
- Synthetic/robot control hooks and tests.
- Payroll mapping, pioneer starting grant, payroll status, and real hourly payout.
- AI Director admin-tab entry point, payroll feedback text, rogue AI controller hook, and LuaM underwear slot support.
- Local AI gateway fallback behavior when no external API key is configured.
- Local AI gateway Anthropic Claude Haiku 4.5 mode through `LUAM_AI_PROVIDER=anthropic`.
- Local AI gateway structured `/review` fallback and Anthropic/OpenAI provider paths for `summary`, `influenceRemarks`, `processRemarks`, `riskRemarks`, `tempoRemarks`, `economyRemarks`, `crewRemarks`, `safetyNotes`, and `recommendedActions`.
- Local AI gateway audit reader for model, request path, status, time window, token, cache-token, fallback, estimated cost, and observed provider-cost checks.
- Hub-safe compact `/info` description, a 100-player admission cap, and 128 network connections so administrator access and handshakes retain headroom.
- Release-surface protection: client/server zip packaging drops debug symbols, source files, project files, local secrets, logs, and cache artifacts; CI runs `Tools/audit_release_surface.ps1` before publish.
- Server-only deterministic expedition planning with versioned seeds, a stable plan hash, bounded macro-regions, six POIs, connectivity/uniqueness validation, and an admin diagnostic command. Durable shadow storage now covers manifests, regions, sites, mutation deltas, entity snapshots, tombstones, and checkpoints with revision/idempotency guards; ECS world materialization is still not connected.
- Character persistence groundwork that archives profile rows instead of physically deleting them, exposes the active server-side `Profile.Id`, and carries matching SQLite/PostgreSQL migrations and integration coverage. This is not yet the complete eternal-character or career system described by the design document.
- Deep-cryo characters and nested inventory are stored against that durable `Profile.Id` before the world body is removed. Cross-round wake claims the snapshot with a time-bounded lease, materializes it in nullspace, consumes the DB state before exposing the body, and restores the character alive and sleeping without old mind, objectives, job, access, or coordinates.
- Server-only progression rules for exact seven-day `CampaignShiftId` periods, the 100-XP weekly cap, the joint 700-XP/seven-shift level gate, level 10 cap, and stable award identities. Durable shadow tables and APIs now cover campaign shifts/runs, career state, participation, idempotent append-only XP awards/reversals, and revision-checked shift sealing; gameplay award sources are not connected yet.
- Fresh client/server release packaging is guarded by `Resources/ServerOnly/_LuaM/client-package-canary.txt`: client exclusion and server inclusion are both mandatory surface-audit conditions.

## Files That Must Be Included

Current integration scopes outside the established LuaM baseline:

- `Content.Client/_Mono/FireControl/**`, `Content.Client/_Mono/Radar/**`, and `Content.Client/Shuttles/UI/ShuttleNavControl.xaml.cs`.
- Mech client controls; client/shared vending UI and messages; `Content.Server/_Mono/MonoCoins/**`, target-seeking and radar; the exact PDA, station-spawning, vending, and NF market callers; `Content.Server/PowerCell/PowerCellSystem.cs`; and the new `_NF` fluid, mech, and power systems.
- Exact DB-first lifecycle files: `Content.Server/Database/UserDbDataManager.cs`, both preference-manager files, `Content.Server/_NF/Bank/BankSystem.cs`, `Content.Server/_Mono/MonoCoins/MonoCoinsManager.cs`, and their PDA/station/vending/market finalizer callers.
- Exact durable persistence files: `Content.Server.Database/LuaMExpeditionModel.cs`, `Content.Server.Database/LuaMProgressionModel.cs`, `Content.Server.Database/LuaMDeepCryoModel.cs`, all provider migration pairs and both snapshots, plus the `DatabaseRecords` and `ServerDb*` LuaM/deep-cryo API partials.
- Exact deep-cryo runtime files: `Content.Server/_LuaM/Cryo/**`, `Content.Server/_NF/CryoSleep/CryoSleepSystem*.cs`, `Content.Server/Bed/Cryostorage/CryostorageSystem.cs`, plus the entity-save guards in `Content.Shared/Follower/FollowerSystem.cs` and `Content.Server/DeviceNetwork/Systems/{DeviceListSystem,NetworkConfiguratorSystem}.cs`.
- Exact physical AI runtime files: `LuaMAiPhysicalBaseBudgetSystem.cs`, `LuaMAiPhysicalBaseFeature.cs`, the ecology/logistics/mining/supply producers, `LuaMSectorAiDirectorSystem.cs`, the server-only CVar/default-off preset, and the bound physical-AI integration tests.
- `Content.Shared/_Mono/Radar/**`, the linked shared fluid/mech systems, and the new `_NF` cargo, fluid, mech, species, and whitelist components.
- `Directory.Packages.props`, NF mecha audio, NF locale changes, vending locales for English and Russian, and the affected Russian launcher/prototype locale paths.
- `Resources/Maps/_LuaM/**`, changed `Resources/Maps/_NF/Shuttles/**`, and changed LuaM/Mono/NF/catalog/entity prototypes declared by the packaging policy.
- `Resources/ServerInfo/_NF/Guidebook/**`, `Resources/Textures/_NF/**`, the salvage airlock RSI, and the shared holopad RSI.

Tracked modified files:

- `Content.Client/PDA/PdaBoundUserInterface.cs`
- `Content.Client/PDA/PdaMenu.xaml`
- `Content.Client/PDA/PdaMenu.xaml.cs`
- `Content.Client/_NF/Shipyard/BUI/ShipyardConsoleBoundUserInterface.cs`
- `Content.Client/_NF/Shipyard/UI/ShipyardConsoleMenu.xaml`
- `Content.Client/_NF/Shipyard/UI/ShipyardConsoleMenu.xaml.cs`
- `Content.Client/Lobby/LobbyState.cs`
- `Content.Client/RoundEnd/RoundEndSummaryWindow.cs`
- `Content.Server/Access/Systems/IdCardConsoleSystem.cs`
- `Content.Server/PDA/PdaSystem.cs`
- `Content.Server/_NF/Bank/BankSystem.cs`
- `Content.Server/_NF/Bank/ATMSystem.cs`
- `Content.Server/Cargo/Systems/CargoSystem.Orders.cs`
- `Content.Server/_NF/ShuttleRecords/ShuttleRecordsSystem.Console.cs`
- `Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs`
- `Content.Server/_NF/Shipyard/Systems/ShipyardSystem.cs`
- `Content.Shared/_NF/ShuttleRecords/ShuttleRecord.cs`
- `Content.Shared/PDA/PdaComponent.cs`
- `Content.Shared/PDA/PdaMessagesUi.cs`
- `Content.Shared/PDA/PdaUpdateState.cs`
- `Content.Shared/_NF/Shipyard/BUI/ShipyardConsoleInterfaceState.cs`
- `Content.Shared/_NF/Shipyard/Components/ShuttleDeedComponent.cs`
- `Content.Shared/_NF/Shipyard/Events/ShipyardConsoleParkMessage.cs`
- `Content.Shared/_NF/Shipyard/Events/ShipyardConsolePurchaseMessage.cs`
- `Resources/Locale/en-US/_NF/shipyard/shipyard-console-component.ftl`
- `Resources/Locale/ru-RU/_NF/shipyard/shipyard-console-component.ftl`

LuaM file groups that must be in the release/package snapshot:

- `Content.Client/_LuaM/Sector/LuaMAiTtsAudioSystem.cs`
- `Content.Server/Chat/Systems/ChatSystem.cs`
- `Content.Server/Radio/EntitySystems/HeadsetSystem.cs`
- `Content.Server/_LuaM/Sector/LuaMCharacterTtsSystem.cs`
- `Content.Server/_LuaM/Sector/LuaMSectorTrafficContactComponent.cs`
- `Content.Server/_LuaM/Sector/LuaMSectorTrafficSystem.cs`
- `Content.Server/_LuaM/Expeditions/LuaMExpeditionPlan.cs`
- `Content.Server/_LuaM/Expeditions/LuaMExpeditionPlanCommand.cs`
- `Content.Server/_LuaM/Expeditions/LuaMExpeditionPlanValidator.cs`
- `Content.Server/_LuaM/Expeditions/LuaMExpeditionPlanner.cs`
- `Content.Server/_LuaM/Progression/LuaMCampaignShiftClock.cs`
- `Content.Server/_LuaM/Progression/LuaMCareerProgressionRules.cs`
- `Content.Server/Database/ServerDbBase.cs`
- `Content.Server/Database/ServerDbManager.cs`
- `Content.Server/Preferences/Managers/IServerPreferencesManager.cs`
- `Content.Server/Preferences/Managers/ServerPreferencesManager.cs`
- `Content.Server.Database/Model.cs`
- `Content.Server.Database/Migrations/Postgres/20260713070624_PreserveCharacterProfiles.cs`
- `Content.Server.Database/Migrations/Postgres/20260713070624_PreserveCharacterProfiles.Designer.cs`
- `Content.Server.Database/Migrations/Postgres/20260713162540_DurablePdaBankTransfers.cs`
- `Content.Server.Database/Migrations/Postgres/20260713162540_DurablePdaBankTransfers.Designer.cs`
- `Content.Server.Database/Migrations/Postgres/20260720164309_LuaMShipPayloadRevision.cs`
- `Content.Server.Database/Migrations/Postgres/20260720164309_LuaMShipPayloadRevision.Designer.cs`
- `Content.Server.Database/Migrations/Postgres/PostgresServerDbContextModelSnapshot.cs`
- `Content.Server.Database/Migrations/Sqlite/20260713070603_PreserveCharacterProfiles.cs`
- `Content.Server.Database/Migrations/Sqlite/20260713070603_PreserveCharacterProfiles.Designer.cs`
- `Content.Server.Database/Migrations/Sqlite/20260713162532_DurablePdaBankTransfers.cs`
- `Content.Server.Database/Migrations/Sqlite/20260713162532_DurablePdaBankTransfers.Designer.cs`
- `Content.Server.Database/Migrations/Sqlite/20260720164259_LuaMShipPayloadRevision.cs`
- `Content.Server.Database/Migrations/Sqlite/20260720164259_LuaMShipPayloadRevision.Designer.cs`
- `Content.Server.Database/Migrations/Sqlite/SqliteServerDbContextModelSnapshot.cs`
- `Content.Shared/_LuaM/Sector/LuaMAiTtsAudioEvent.cs`
- `Content.Server/Nutrition/EntitySystems/AnimalHusbandrySystem.cs`
- `Content.Server/Spawners/Components/TimedSpawnerComponent.cs`
- `Content.Server/Spawners/EntitySystems/SpawnerSystem.cs`
- `Content.Server/StationEvents/Events/VentCrittersRule.cs`
- `Content.Server/_LuaM/Administration/LuaMAnimalPopulationCommands.cs`
- `Content.Server/_LuaM/Animals/LuaMAnimalPopulationSystem.cs`
- `Content.Shared/Nutrition/AnimalHusbandry/ReproductiveComponent.cs`
- `Content.Server/Gateway/Components/GatewayGeneratorDestinationComponent.cs`
- `Content.Server/Gateway/Systems/GatewayGeneratorSystem.cs`
- `Content.Shared/CCVar/CCVars.LuaM.cs`
- `Content.Shared/CCVar/CCVars.Misc.cs`
- `Content.IntegrationTests/PoolManager.Cvars.cs`
- `Content.IntegrationTests/Tests/Gateway/GatewayGeneratorGrowthLimitTest.cs`
- `Content.IntegrationTests/Tests/_LuaM/LuaMAnimalHusbandryIntervalTest.cs`
- `Content.IntegrationTests/Tests/_LuaM/LuaMAnimalPopulationControlTest.cs`
- `Content.IntegrationTests/Tests/_LuaM/LuaMSectorTrafficTest.cs`
- `Content.IntegrationTests/Tests/_LuaM/LuaMSectorTrafficInterceptTest.cs`
- `Content.IntegrationTests/Tests/_LuaM/LuaMTimedSpawnerLimitTest.cs`
- `Content.IntegrationTests/Tests/_LuaM/LuaMBankAndPdaContractsTest.cs`
- `Content.IntegrationTests/Tests/_LuaM/LuaMBankDurableMutationContractTest.cs`
- `Content.IntegrationTests/Tests/_LuaM/LuaMBankPersistenceTest.cs`
- `Content.IntegrationTests/Tests/_LuaM/LuaMShipyardPurchaseDurabilityContractTest.cs`
- `Content.IntegrationTests/Tests/_LuaM/LuaMCharacterPersistenceTest.cs`
- `Content.IntegrationTests/Tests/_LuaM/LuaMCharacterTtsValidationTest.cs`
- `Content.IntegrationTests/Tests/_LuaM/LuaMDynamicEventGrowthLimitTest.cs`
- `Content.IntegrationTests/Tests/_LuaM/LuaMExpeditionPlannerTest.cs`
- `Content.IntegrationTests/Tests/_LuaM/LuaMProgressionRulesTest.cs`
- `Resources/Prototypes/_LuaM/Sector/rescue_after_action.yml`
- `Resources/Prototypes/_LuaM/Entities/World/sector_traffic.yml`
- `Resources/ConfigPresets/_LuaM/deadSpaceLowPop.toml`
- `Resources/Prototypes/Entities/Markers/Spawners/Conditional/timed.yml`
- `Resources/Prototypes/GameRules/pests.yml`
- `Resources/Maps/_NF/Shuttles/Scrap/bison.yml`
- `Resources/Changelog/Parts/luam-animal-population-cap.yml`
- `Resources/Changelog/Parts/luam-sector-traffic.yml`
- `Resources/Changelog/Parts/luam-pda-bank-transfer-fix.yml`
- `Content.Client/_LuaM/**`
- `Content.Server/_LuaM/**`
- `Content.Shared/_LuaM/**`
- `Content.IntegrationTests/Tests/_LuaM/**`
- `Content.IntegrationTests/Tests/_NF/BountyContracts/**`
- `Content.Tests/Client/_LuaM/**`
- `Content.Tests/Server/_LuaM/**`
- `Content.Tests/Shared/_NF/BountyContracts/**`
- `Resources/ConfigPresets/_LuaM/**`
- `Resources/Locale/en-US/_LuaM/**`
- `Resources/Locale/ru-RU/_LuaM/**`
- `Resources/Prototypes/_LuaM/**`
- `Resources/ServerInfo/_LuaM/**`
- `Resources/Textures/_LuaM/**`
- `Resources/ServerInfo/Intro.txt`
- `server_config.remote.toml`
- `Resources/Prototypes/_Mono/Entities/Markers/Spawners/shuttles.yml`
- `Resources/ConfigPresets/_Mono/monolithCore.toml`
- `Resources/Prototypes/_Mono/GameRules/timings.yml`
- `Resources/Prototypes/_Mono/Shipyard/triage.yml`
- `Resources/Prototypes/_Mono/game_presets.yml`
- `Resources/Locale/en-US/_Mono/gamerules/gamemodes.ftl`
- `Resources/Locale/ru-RU/_Mono/gamerules/gamemodes.ftl`
- `Resources/Maps/_Mono/Shuttles/triage.yml`
- LuaM-linked admin tab, Bounty Contracts, Bank/Payroll locale, LateJoin, cartridge, research, role-time, pinpointer, synthetic-control, clothing/underwear slot, rogue AI, character profile, and test-harness files declared by `Tools\luam_release_policy.json` or retained in the historical baseline payload.
- `Tools/luam_ai_gateway.py`
- `Tools/luam_openai_mcp_server.py`
- `Tools/summarize_luam_ai_audit.py`
- `Tools/test_luam_ai_gateway.py`
- `Tools/validate_luam_feature_pack.py`
- `Tools/local_stack.md`
- `Tools/audit_release_surface.ps1`
- `Tools/archive_monolith_admin_logs.ps1`
- `Tools/deploy_luam_ai_gateway.ps1`
- `Tools/deploy_luam_server_release.ps1`
- `Tools/monolith-restart-when-empty.ps1`
- `Tools/monolith_release_runbook.md`
- `Tools/provision_monolith_client_static.ps1`
- `Tools/setup_luam_piper_tts.ps1`
- `Tools/luam_admin_ranks.yml`
- `Tools/generate_luam_admin_rank_sql.py`
- `Tools/check_luam_release_ready.ps1`
- `Tools/build_luam_release_package.ps1`
- `Tools/build_luam_server_release.ps1`
- `Tools/verify_luam_release_package.ps1`
- `Tools/luam_release_contract.ps1`
- `Tools/test_luam_release_contract.ps1`
- `Tools/start_local_stack.ps1`
- `Tools/stop_local_stack.ps1`
- `Tools/test_local_stack.ps1`
- `Tools/test_local_frontier.ps1`
- `Tools/luam_release_policy.json`
- `Tools/luam_character_progression_design.md`
- `Tools/luam_expedition_implementation_proposal.md`
- `Tools/luam_expedition_worldgen_design.md`
- Current LuaM changelog fragments under `Resources/Changelog/Parts/**` when present.

No current runtime map dependency is approved outside the LuaM source package. The former NF vessel allowlist has been removed; changed shuttle maps and shipyard prototypes must be in the payload or the scope audit fails.

Before any release, inspect all three working-tree surfaces. The package builder uses the same union and excludes only `test_results/` as local evidence:

```powershell
git diff --name-only
git diff --cached --name-only
git ls-files --others --exclude-standard
```

For a real git-based release gate, run it without `-AllowUntracked`. It must pass before deployment:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\check_luam_release_ready.ps1 -RunTests -RunLocalSmoke
```

To build a local zip package without touching the remote server:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\build_luam_release_package.ps1 -RunTests -RunLocalSmoke
```

The package is written under `DeploymentPackages\LuaM` and includes a schema-v2 `PACKAGE_MANIFEST.json`. It binds the complete payload record digest to the readiness worktree digest, Git HEAD, and exact policy SHA256; it contains no absolute source-root path.
It also includes the generated admin-rank SQL/markdown artifacts under `DeploymentPackages\LuaM`.
The package also includes LuaM-owned resources: config presets, locales, prototypes, guidebook XML, textures, server intro text, and key linked code/resources outside `_LuaM` that are required by the validator.
`pendingLocalIntegrationBatch.packageFiles` is the durable, clean-tree source selection for the current batch. Every declared path is policy-required and packaged even after the implementing commit removes it from `git diff`; `packageScopes` remains the audit boundary for additional dirty-tree changes.

## Build live server release

First verify the source package with an independently recorded whole-archive hash. Then build a Release server from that exact verified source receipt and inject immutable external client metadata:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\verify_luam_release_package.ps1 -PackagePath DeploymentPackages\LuaM\<package>.zip -ExpectedSha256 <source-sha256> -Json
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\build_luam_server_release.ps1 -ExternalClientBaseUrl http://188.127.225.57:1213 -SourcePackagePath DeploymentPackages\LuaM\<package>.zip -ExpectedSourcePackageSha256 <source-sha256> -ReleaseReceiptPath release\luam-binary-release-receipt.json -Json
```

This writes `release\SS14.Client.zip`, `release\SS14.Server_linux-x64.zip`, and a hash-pinned binary release receipt. The receipt binds both artifact hashes and sizes, delivery metadata, the passing two-sided canary audit, the verified source payload digest, worktree digest, Git HEAD, and policy hash. `-SkipPackageBuild` and `-SkipAudit` are accepted only with `-LocalOnly` and cannot produce a production receipt.
`PACKAGE_MANIFEST.json` also records a `scopeAudit` block. The verifier fails if changed files outside the package are not explicitly allowlisted.

To verify a built package:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\verify_luam_release_package.ps1 -PackagePath DeploymentPackages\LuaM\<package>.zip -ExpectedSha256 <sha256>
```

The verifier requires the independently recorded whole-package SHA256, rejects unsafe, non-canonical, oversized, duplicate, or case-colliding ZIP paths, checks exact manifest/file-list/payload set parity, recomputes the complete payload digest, and verifies recorded readiness/worktree evidence and `scopeAudit`.
It also checks strict UTF-8, every policy-required payload file, the embedded policy SHA256, and passed evidence for every production-required test and smoke check. `-AllowUntracked`, any nonzero untracked receipt, or a package built without `-RunTests -RunLocalSmoke` is local evidence only and always fails production verification.

To audit the actual public client/server release zips before upload:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\audit_release_surface.ps1 -PackagePath release\SS14.Client.zip,release\SS14.Server_linux-x64.zip
```

This fails if `release\*.zip` contains `.pdb`, source files, project/solution files, local secret/config names, logs, caches, or common local build directories.

To deploy a verified server release zip to the VPS, use the guarded deploy helper instead of hand-running `scp`/`unzip`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\deploy_luam_server_release.ps1 -PackagePath release\SS14.Server_linux-x64.zip -ExpectedSha256 <server-sha256> -ReleaseReceiptPath release\luam-binary-release-receipt.json -ExpectedReleaseReceiptSha256 <receipt-sha256> -Tag <tag> -ConfigSourcePath server_config.remote.toml -RemoteConfigPath <live-server_config.toml> -RequireDataBackup -RemoteDataDir <live-data-dir>
```

The helper validates the strict JSON policy, receipt hash, policy/source/artifact bindings, delivery metadata, canonical ZIP surface, and server-only canary before even producing a dry-run plan. A real upload additionally requires a non-expired `deploymentAuthorization` that explicitly allows `server-release`; `remoteDeployFrozen=true` always blocks it.
Do not merely flip the freeze boolean. An authorized policy needs `deploymentAuthorization.state=authorized`, approval identity/timestamps/expiry, explicit allowed mutations, and `pendingLocalIntegrationBatch.state=deployment-authorized`. Because the policy hash is part of every source and binary receipt, rerun strict readiness and rebuild/reverify all receipts under that exact authorized policy before mutation. The contract test accepts both internally consistent frozen and authorized states, so this sequence has no gate deadlock.

## Current Release Slice (Deployed)

The verified 2026-07-13 expedition/persistence foundations, durable-transfer, animal-control, and sector-interception update, and the reviewed 2026-07-14 radar/commerce/persistence/physical-AI integration batch were included in production rollout `20260716-aibolit-217e3ad5ff3c`.

- The deployed expedition planner remains server-only and does not create ECS entities. The deployed batch adds durable shadow deltas/snapshots/checkpoints, but streaming terrain, POI materialization, and `ExpeditionDirector` remain later stages.
- Profile deletion now preserves the database row through `is_archived`/`archived_at` and releases the active slot through `NULL`. A normal replacement receives a new `Profile.Id`; an archived identity returns only through the explicit, ownership-checked, idempotent `RestoreArchivedCharacterAsync` path.
- The deployed batch adds provider-matched progression shadow tables and transactional APIs for shifts/runs, participation, careers, awards/reversals, and sealing. Gameplay award integration remains future work, so this does not make the shadow ledger authoritative for live progression by itself.
- The deployed release passed the strict tracked-scope gate. `-AllowUntracked` remains available only for local development validation and is not a production-release bypass; the post-rollout follow-up must establish fresh evidence.

## Verification Records

### 2026-07-14 — local integration package preparation

- Package modeling is provisional while the parallel bank-durability stream is still changing the working tree. `Content.Server/_NF/Commands/BankCommand.cs` is selected through an exact fail-closed scope; final dirty/selected/payload counts will be recorded only after that stream is ready.
- All runtime dependencies declared by the pending policy were present in the modeled package, staged-only selection remained covered, and the runtime outside-package allowlist was empty.
- The package script parsed with zero PowerShell errors. Every changed/new JSON file parsed successfully, and every state declared by changed/new RSI metadata had a matching PNG.
- Both staged and unstaged `git diff --check` passed. The changelog YAML parsed successfully, and `Directory.Packages.props` parsed as XML with one Veldrid entry at version 4.9.0.
- `Content.YAMLLinter` built with zero errors, and the complete YAML/prototype pass reported `No errors found`. No package build, staging operation, deployment, upload, or restart was performed; `remoteDeployFrozen` remained `true`.

### 2026-07-13 — deployed release slice

- The selected update is committed as three reviewed gameplay slices plus their release-gate coverage: durable PDA operation journaling, bounded animal-population administration, and playable sector contact interception. It was deployed by the guarded rollout recorded below, and remote deployment is frozen again.
- A combined final-state integration filter passed 39/39 across bank/PDA, animal husbandry/population/timed spawners, and sector traffic/interception. The focused workstreams additionally exercised all 31 bank scenarios, six animal scenarios, and two sector scenarios after their last fixes.
- SQLite and PostgreSQL both report no pending EF model changes after the new durable-transfer migrations. The database, server, client, and integration-test projects build with zero errors.
- The strict readiness gate with tests and local smoke passed with no issues or warnings. It covered the LuaM Content/Integration filters, dependency audit, feature validator, AI gateway, admin ranks, and local server/client smoke.
- Source package `luam-local-release-20260713-170614.zip` independently verified 528 files with no untracked or out-of-scope entries; SHA256 is `78a7f90bba84d532ce0f4d08ca05c2710b0b28e9ae39038f36af0c1ac3ee6ca9`.
- Release artifacts passed the zero-violation surface audit: server SHA256 `1dac909c6a76113012e6b49838146b3c22755e4fe3b3d33eb2d39a528f9daf1b`; client/build-version SHA256 `dfb52888fc31af2e075bc36b6ef5f03c895cf76bd14891e1a316dc4cd782b147`; intended immutable client URL `http://188.127.225.57:1213/dfb52888fc31af2e075bc36b6ef5f03c895cf76bd14891e1a316dc4cd782b147/SS14.Client.zip`.

- `Tools\check_luam_release_ready.ps1 -AllowUntracked -Json` passed with no issues. Its sole warning is the expected set of 18 untracked source/test/migration/design files; a strict git-based release remains blocked until those files are intentionally reviewed and added.
- The focused persistence/expedition/progression/TTS regression filter passed 53/53, including a non-empty SQLite upgrade, fail-closed downgrade, archive CHECK constraint, 2048 expedition seeds, the v2 golden hash, culture invariance, bounded gateway JSON, and structured WAV/Ogg validation.
- The full `Content.IntegrationTests` LuaM filter passed 321 tests and skipped the 13 intentionally disabled physical AI-base tests; `Content.Tests` LuaM passed 32/32.
- `python Tools\validate_luam_feature_pack.py` and `python Tools\test_luam_ai_gateway.py` passed.
- A temporary source package built with explicit `-AllowUntracked` and verified successfully: 496 files, payload hashes/UTF-8/required artifacts/readiness evidence all valid, and zero unexpected changed files outside the package. The verifier only warned that `RunTests` and `RunLocalSmoke` were not embedded in that package run; tests were run separately as recorded above.
- Both unstaged and staged release-scope whitespace checks passed. No package was deployed and `remoteDeployFrozen` remains unchanged.
- The PDA/bank/shipyard durability filter passed 36/36 after a fresh integration-test build. Coverage includes collision-safe PDA IDs, offline routing, duplicate submission guards, both database row-order branches, conservation/overflow/insufficient-funds behavior, stale profile-save protection, unknown-COMMIT fail-closed mapping, fresh-balance classification after initial save exceptions, durable callback rollback before world side effects, and shipyard purchase reservation/revalidation/cleanup contracts.
- A clean strict readiness run passed with no issues or warnings after the first-run PDA registry loader was hardened to create its parent directory. The two affected clean-server scenarios passed 3/3 each, and the dynamic sensor-drift contract passed 3/3 after removing unstable live-position assertions for its intentionally unanchored physics entity.
- Read-only round-123 evidence identified 13 mouse, 13 cockroach, and 12 low-pop snail vent migrations in roughly 14 hours 40 minutes. The release now limits each migration to one occurrence per round, removes unintended guaranteed bonus pests, caps map husbandry at 32 population units including fertilized eggs, and gives mouse/cockroach timed markers a persisted five-spawn lifetime budget. The combined animal-cap integration filter passed 4/4.
- The existing `CargoTest` fixture passed 4 tests and kept its 1 intentional skip. Server, client, and integration-test builds completed with zero errors; only the documented baseline warnings remain.
- Read-only production preflight at `2026-07-13 20:14 MSK` found `monolith-ds.service` active in round 123 with two players online. The system journal had no priority-error entries; the broader text metric contained only known unrelated one-off transform, hub-reset, dungeon-key, and component-validation messages. Semantic config comparison confirmed that the reviewed local config preserves every live value and adds only `luam.animal_husbandry.max_population_per_map = 32`, `luam.sector_traffic.enabled = true`, and `luam.sector_traffic.contacts = 2`. The rollout must wait for zero players and must not use `-Force`.

### 2026-07-12 — deployed release baseline

- The current local package SHA256 is reported by `Tools\build_luam_release_package.ps1` and `Tools\verify_luam_release_package.ps1`; it is not pinned here because this file is included in the package.
- Latest package `scopeAudit` must have 0 unexpected changed files outside package.
- `python Tools\validate_luam_feature_pack.py` passed.
- `python Tools\generate_luam_admin_rank_sql.py --check-only --json` passed, 6 ranks / 90 flags.
- `python Tools\test_luam_ai_gateway.py` passed with local Anthropic Haiku 4.5 no-key fallback, mock OpenAI MCP `/responses`, mock Anthropic `/messages`, `/review` structured remarks, audit token/cache/cost fields, and filtered audit-summary reader coverage.
- `dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --filter "FullyQualifiedName~LuaMDonationShopTest" --no-restore` passed; it covers locked balance without access, one-month manual access, duplicate permanent reward blocking, balance spend, ledger actions, and certificate printing.
- `dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --filter "FullyQualifiedName~LuaMDynamicEventDebrisTest" --no-restore` passed; it covers five generated debris sites, GPS marker/site-note clarity, cleanup, and hostile contact only on the fifth site.
- `Tools\check_luam_release_ready.ps1 -RunTests -RunLocalSmoke` passed with `utf8-release-text`, zero untracked release-scope files, LuaM dotnet test filters, and full local gateway/server/client smoke.
- The post-rebase capacity/hub gate passed with `game.soft_max_players = 100`, `net.max_connections = 128`, and a predicted compact `/info` payload of about 3.4 KiB.
- `Tools\verify_luam_release_package.ps1` passed with `utf8-package-text`, `admin-rank-artifacts`, `luam-resource-artifacts`, recorded local smoke evidence, and `scope-audit`.
- `Tools\audit_release_surface.ps1 -ReleaseDir release` passed after rebuilding `SS14.Client.zip` and `SS14.Server_linux-x64.zip`; the previous 15 `.pdb` entries were removed.
- `dotnet list Content.Server.Database\Content.Server.Database.csproj package --vulnerable --include-transitive` passed with no vulnerable packages after pinning `System.Security.Cryptography.Xml` to a patched 10.0.x package.
- `Tools\check_luam_release_ready.ps1` preserves the 7-day automatic round ceiling by requiring `shuttle.auto_call_time = 10080` in the Monolith core, LuaM preset, and remote/local configs, and by forbidding `MonoAllAtOnce` from overriding that value with a `RoundEndRule`.
- `dotnet test Content.Tests\Content.Tests.csproj --filter "FullyQualifiedName~LuaM" --no-restore` passed, 1/1.
- `dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --filter "FullyQualifiedName~LuaM" --no-restore` passed, 80/80.
- `dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --filter "FullyQualifiedName~LuaMBankAndPdaContractsTest" --no-restore` passed, 14/14.
- `git diff --check` over LuaM/PDA/Bank/Tools scope passed. Only LF/CRLF warnings were reported.

Expected existing warnings:

- NU1510 trim-analysis warnings for existing RobustToolbox package references.
- Existing analyzer/obsolete warnings in unrelated test and engine paths.

### 2026-07-12 — seven-day round update

- Removed the `RoundEndRuleHour3` override from `MonoAllAtOnce`; Apocalypse, Mixed fallback, Monolith core, LuaM preset, and local/remote configs now use `shuttle.auto_call_time = 10080` (7 active simulation days). A recalled automatic shuttle keeps the normal 45-minute extension behavior.
- Updated Apocalypse EN/RU titles and the public server description from 3 hours to 7 days.
- Long-round lobby and round-summary displays now use total elapsed hours instead of wrapping to `0..23` after each day.
- `python Tools\validate_luam_feature_pack.py` passed.
- `dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --filter "FullyQualifiedName~LuaMGamePresetRulesTest" --no-restore --nologo` passed, 2/2.
- `dotnet build Content.Client\Content.Client.csproj --no-restore --nologo` passed with existing warnings and 0 errors.
- `Tools\check_luam_release_ready.ps1 -RunTests -RunLocalSmoke -Json` passed with no issues or warnings, including full LuaM dotnet filters and local server/client smoke.
- The independently verified source package and production rollout are recorded below; `remoteDeployFrozen` is `true` again after successful post-deploy checks.

## Production Rollout History

### 2026-07-16 — deep-cryo persistence rollout

- Checks and artifacts: the strict production gate passed all 588 integration tests and all remaining release checks. The independently verified 1154-file source package `luam-local-release-20260716-115932.zip` has SHA256 `bf6469322ba5830cf9456f5b5655281912b2388b8d2fbb49e1fb187568e621cd`; client SHA256/build version is `338ab621a94499d4185b8dfead3aadc50b5dd5b02e7ce833759520b85bf94790`; server SHA256 is `1b50b699c41159c1ec7a10852a48e5e2cb1299b631938954f9b9dfef5b6574de`.
- Live result: the authorized forced rollout completed while two players were online. The service is `active/running`; round 126 is running with two players, `run_level=1`, and the exact title `Мёртвый космос НУЖНЫ ТСФ СРОЧНО! 🙏`. The immutable client URL returned HTTP 200 with the expected 333752213-byte archive.
- Deep cryo and database: live SQLite contains migration `20260716101408_LuaMDeepCryoPersistence`, all three deep-cryo persistence tables, and the lifecycle revision column; `PRAGMA integrity_check` returned `ok`. Characters entering either supported cryo implementation after this deployment are stored for guarded restoration in the next round. Pre-deployment cryo deletions cannot be reconstructed retroactively.
- Backups and freeze: data backup `/opt/monolith-ds/backups/data-SS14.Server_linux-x64.tar.gz` has SHA256 `dc3d8ec65dbfdc295619ee886017bb44013cd51464c73b77379aa31e4d10cd35` and passed `gzip -t`; server and prior-config backups are retained beside it. Post-deployment verification passed and remote deployment was frozen again.

### 2026-07-16 — `20260716-aibolit-217e3ad5ff3c`

- Identity and authorization: the production-eligible source package was built from Git HEAD `0efe0838ed808b7cc24c3931290ca9f90c282638` with worktree digest `217e3ad5ff3c9a1aa84c565dede0de2a10ac680089d49f21f97c04d909675e2a` under authorized policy SHA256 `093ef7f422b612f4de2bab5fb3d9b9fc560acdb32c6e6ce2f660d7ac365deee6`. Approval `codex-20260715-remote-update` covered only `server-release` and `client-static` from `2026-07-15T20:03:18Z` through `2026-07-16T00:03:18Z`.
- Checks and artifacts: `PACKAGE_MANIFEST.json` recorded `productionEligible=true`, zero readiness issues or warnings, and zero untracked files. Source package `luam-local-release-20260715-213854.zip` has SHA256 `d3f4dd2a106b23f6929623b742b0f3a446f6ca3a473e9d9e0093a2f86bf93879`. Binary receipt SHA256 is `a3dbfdf6f1df6140b89d87d2ef3145c83c5471eb53acd0614acfced7bbeda358`; server SHA256 is `dadff07a1ddf8461aa16ddfb077881745a7dce7b24265c420c491275d031a0ec` (44738011 bytes), and client SHA256 is `c9f8ef3b896752f74e8d84c5c743949c364ddbfd394508ff525bb50fe1db4793` (333751769 bytes).
- Delivery: immutable client URL `http://188.127.225.57:1213/20260716-aibolit-217e3ad5ff3c/SS14.Client.zip` is bound into the receipt generated at `2026-07-15T21:49:41.6326254Z`.
- Live result and freeze: the rollout completed and passed post-deployment verification. Remote deployment was frozen again and the active batch returned to local-only follow-up work. The retained binary receipt remains historical evidence bound to the authorized policy revision; it is intentionally not reusable under a later frozen policy hash.

### 2026-07-13 — `20260713-durable-bank-animal-sector-277.2.1`

- Identity: the verified gameplay build through Git commit `3f5c61c49b` entered `active/running` at `2026-07-13 22:58:24 MSK` (`19:58:24 UTC`). The deploy helper confirmed zero players immediately before stopping the service, did not use `-Force`, and moved active round 123 to lobby round 124. Post-deploy `NRestarts=0` and `ExecMainStatus=0`.
- Gameplay: PDA transfers now use database-backed bank IDs and a durable operation journal with idempotent replay/restart reconciliation; animal population is capped and observable per map with fingerprint-confirmed safe pest cleanup; four radar-only sector signatures can be intercepted into report, cargo, distress, and unknown-contact tasks without NPC ships.
- Checks and artifacts: strict readiness with tests and local smoke passed with no issues or warnings; the independently verified 528-file source package has SHA256 `78a7f90bba84d532ce0f4d08ca05c2710b0b28e9ae39038f36af0c1ac3ee6ca9`; the release-surface audit found zero violations. Server SHA256 is `1dac909c6a76113012e6b49838146b3c22755e4fe3b3d33eb2d39a528f9daf1b`; client/build-version SHA256 is `dfb52888fc31af2e075bc36b6ef5f03c895cf76bd14891e1a316dc4cd782b147`; immutable client URL `http://188.127.225.57:1213/dfb52888fc31af2e075bc36b6ef5f03c895cf76bd14891e1a316dc4cd782b147/SS14.Client.zip` returned HTTP 200 with the expected 333579842-byte payload.
- Database and config: live SQLite contains migration `20260713162532_DurablePdaBankTransfers`, both durable-bank tables, and their expected indexes. The reviewed config changed from SHA256 `0f17c2794b5e6aca9e61ac31ce5e7fdb07a87916d0ec4788bce8a4c3bf62ff77` to `cf6657fec006ee626dc25cd126556401bc3b6dfb7cced0f7209f284b0a7eb6b5`, adding only animal map limit `32`, enabled sector traffic, and `2` simultaneous sector contacts.
- Live result: the server reached `Ready` at `22:59:03 MSK`; the full SSH-backed post-verify at `23:07 MSK` returned `strict_ok=true` with zero failures and zero warnings after round 124 entered `run_level=1`. Public status/info, hub presence, auth, capacity 100, panic-bunker, external client delivery, build integrity, service state, and journal checks all pass. There are no startup `ERR`/`FATAL`, database failures, exceptions, or target bank/animal/sector errors; non-blocking startup messages are limited to the known old `PreserveCharacterProfiles` PRAGMA warning, duplicate emote words, map-load catch-up, and one obsolete `RandomItem` prototype.
- Backups: server `/opt/monolith-ds/backups/server-20260713-durable-bank-animal-sector-277.2.1` was fully readable (10573 files, 224854622 bytes); prior config `/opt/monolith-ds/backups/server_config-before-20260713-durable-bank-animal-sector-277.2.1.toml` has SHA256 `0f17c2794b5e6aca9e61ac31ce5e7fdb07a87916d0ec4788bce8a4c3bf62ff77`; data `/opt/monolith-ds/backups/data-20260713-durable-bank-animal-sector-277.2.1.tar.gz` has SHA256 `0a7135e265312f6f8496f7070a44df93ccae4c4060af16aaf6bf8d40ee56330d` and passed both `gzip -t` and a complete `tar -tzf` listing.

### 2026-07-12 — `20260712-seven-day-rounds-277.2.1`

- Identity: service entered active state at `2026-07-12 23:51:47 MSK` (`20:51:47 UTC`) from `master` at Git HEAD `a54586c10137a765814a95edeedec4a675a561f8`, plus the verified release working tree. Deployment ran at zero online players without `-Force` and moved round 122 to lobby round 123.
- Round duration: `MonoAllAtOnce` no longer contains a `RoundEndTimeRule`; Monolith core, LuaM preset, and deployed config all use `shuttle.auto_call_time = 10080`. Live `/status` and the public hub report `Апокалипсис (7 дней)`, and `/info.desc` states that Apocalypse and Mixed rounds run up to 7 days.
- Checks: full readiness passed with the five-project dependency audit, LuaM Content/Integration filters, gateway checks, admin-rank validation, and local stack smoke. Source package `luam-local-release-20260712-204344.zip` passed independent verification (473 files, SHA256 `7f37e4a5426416c6d7ec18b0e767b0ea3bc4101ce3d25e3c780ea21d77e0ca16`); client/server surface audit reported zero violations. One earlier repeat encountered a seed-dependent RobustToolbox broadphase test flake; the affected test then passed 3/3 in isolation and the recorded package gate passed in full.
- Artifacts: server SHA256 `c170f930bc9688388bd14303d8f3b347f5ad6632b612cfe6c57fbef0061aefa6`; client SHA256/build version `84e15c903ed600558de2f850c9d3b04ce7fdb2ae183b1b034a196b66f4201487`; immutable client URL `http://188.127.225.57:1213/84e15c903ed600558de2f850c9d3b04ce7fdb2ae183b1b034a196b66f4201487/SS14.Client.zip` returned HTTP 200.
- Live result: service is `active/running` with `NRestarts=0`; the journal has zero errors since startup; external client delivery, public hub presence, auth, capacity, panic-bunker, connect-address, and build-integrity checks pass. Post-verify `strict_ok=false` only because lobby round 123 with zero players has no `round_start_time` yet.
- Backups: server `/opt/monolith-ds/backups/server-20260712-seven-day-rounds-277.2.1`; prior config `/opt/monolith-ds/backups/server_config-before-20260712-seven-day-rounds-277.2.1.toml`, SHA256 `1bcb45e8f62102a5ba75a3349da46136395b73b7e7780050fa5b6da0e3a7a665`; deployed config SHA256 `0f17c2794b5e6aca9e61ac31ce5e7fdb07a87916d0ec4788bce8a4c3bf62ff77`; data `/opt/monolith-ds/backups/data-20260712-seven-day-rounds-277.2.1.tar.gz`, SHA256 `fe485b3d0e88f7983236875475dbf7a00986ba7ac2d7a501e778e32461107452`, verified by both `gzip -t` and a complete `tar -tzf` listing.

### 2026-07-11 — `20260711-hub-cap100-277.2.1`

- Identity: deployed at `2026-07-11 16:10:41 MSK` (`13:10:41 UTC`) from `master` at Git HEAD `a54586c10137a765814a95edeedec4a675a561f8`, plus the verified release working-tree payload recorded by `PACKAGE_MANIFEST.json`.
- Artifacts: server SHA256 `fadbea5127ac32a2b0b553e87c854b7dd91b6caf5f5eb5a2163a675ffeeb7a73`; client SHA256 `0aa9930ae18a93ea0ecfb7fb38ec7b5b1369358405fc6c6b66579c9fad405bad`; immutable client URL `http://188.127.225.57:1213/0aa9930ae18a93ea0ecfb7fb38ec7b5b1369358405fc6c6b66579c9fad405bad/SS14.Client.zip`.
- Capacity and hub: live `game.soft_max_players = 100`, `net.max_connections = 128`, `/info` is 3404 bytes, `connect_address` is populated, and the public hub entry for `ss14://188.127.225.57:1212/` is present.
- Checks: full readiness passed without issues or warnings, including the five-project dependency audit, LuaM Content.Tests and IntegrationTests filters, AI gateway checks, admin-rank validation, and local stack smoke. The source-package verifier passed with zero unexpected out-of-scope files; the client/server surface audit reported zero violations. Post-deploy preflight returned `ok=true`, service active, external client HTTP 200, and zero journal errors; `strict_ok=false` only because round 109 was still in the lobby and had no round-start timestamp.
- Backups: server `/opt/monolith-ds/backups/server-20260711-hub-cap100-277.2.1`; prior config `/opt/monolith-ds/backups/server_config-before-20260711-hub-cap100-277.2.1.toml` with SHA256 `4e56fd9a045ad7921e35483cac4fc86f73c2ece3e358d95ec4d9f3625f1a6d56`; data `/opt/monolith-ds/backups/data-20260711-hub-cap100-277.2.1.tar.gz`, verified by both `gzip -t` and a complete `tar -tzf` listing. The deployed config SHA256 is `2f87b8a9cd153482c22848e820ae46891980144bfa5c744fc6c20b6925d90c64`.
- Deployment notes: the operator explicitly requested immediate deployment while one player remained online; guarded deploy used `-Force`, moved round 108 to lobby round 109, completed on the first start, and did not need rollback. The rollout also fixed CRLF normalization for generated remote bash, made source-package scope failures fail closed, updated the upstream radio-event test API, and validated localization keys against both language trees.
- Follow-up: the first attempted round start exposed an invalid `LuaMDeadSpaceLowPop` rule reference (`PortstrikeAnnounceRuleHour4`) and repeatedly returned players to the lobby. Production was corrected by the `20260711-roundstart-hotfix-277.2.1` rollout below.

### 2026-07-11 — `20260711-roundstart-hotfix-277.2.1`

- Identity: service started the hotfix at `2026-07-11 16:24:12 MSK` (`13:24:12 UTC`) from `master` at Git HEAD `a54586c10137a765814a95edeedec4a675a561f8`, plus the reviewed working-tree hotfix.
- Artifacts: server SHA256 `b64280b454d934f0c1cd81850fd4aacea189d9f2dc7c03b59f1b241ded4e8194`; client SHA256 `2a4842905e0ae49da1ebe9c0085968fff4c1eff978068ba9a9794e90c738f15e`; immutable client URL `http://188.127.225.57:1213/2a4842905e0ae49da1ebe9c0085968fff4c1eff978068ba9a9794e90c738f15e/SS14.Client.zip` returned HTTP 200.
- Root cause and fix: upstream renamed the port-strike announcement prototype from `PortstrikeAnnounceRuleHour4` to `PortstrikeAnnounceRuleHour3`, while `Resources/Prototypes/_LuaM/game_presets.yml` still referenced the removed ID. The preset now uses `PortstrikeAnnounceRuleHour3`; the LuaM validator scans all entity prototype IDs and rejects unresolved preset rules, and `LuaMGamePresetRulesTest` resolves every actual `LuaMDeadSpaceLowPop` rule through `IPrototypeManager`.
- Checks: `python Tools/validate_luam_feature_pack.py` passed, targeted `LuaMGamePresetRulesTest` passed 1/1, the complete client/server Release build passed, and the public release-surface audit reported zero violations. After deployment the service remained active with `NRestarts=0`; when the first player connected, round 114 successfully entered `run_level=1` at `2026-07-11 16:28:30 MSK` (`13:28:30 UTC`) and no longer returned to the lobby.
- Capacity and delivery: live `/status` reports `soft_max_players = 100`; deployed config keeps `net.max_connections = 128`; `/info` advertises the new external client hash and immutable URL.
- Backups: server `/opt/monolith-ds/backups/server-20260711-roundstart-hotfix-277.2.1`; prior config `/opt/monolith-ds/backups/server_config-before-20260711-roundstart-hotfix-277.2.1.toml`; data `/opt/monolith-ds/backups/data-20260711-roundstart-hotfix-277.2.1.tar.gz`, SHA256 `1f779a30c9f3ec0b6356715974f03151fcac962dbb7179969f7e69bde43ff031`, verified by `gzip -t` and a complete `tar -tzf` listing. Both prior and deployed config SHA256 values are `2f87b8a9cd153482c22848e820ae46891980144bfa5c744fc6c20b6925d90c64`.

### 2026-07-11 — `20260711-apocalypse-role-time-277.2.1`

- Identity: service entered active state at `2026-07-11 21:11:33 MSK` (`18:11:33 UTC`) from `master` at Git HEAD `a54586c10137a765814a95edeedec4a675a561f8`, plus the verified release working tree. Deployment ran at zero online players and did not force-disconnect anyone.
- Modes and sector density: production now starts `MonoAllAtOnce` (`Апокалипсис`) and uses `MonoMixed` as its only failed-start fallback; preset autovoting remains disabled. Worldgen was restored to 4 cargo depots and 6 optional stations, while cleanup distances returned to the upstream 1280/628/628 values so distant sector content is not removed by the former 256-unit austerity settings. The primary Apocalypse round ends after 3 hours; the Mixed fallback retains the 7-day shuttle auto-call ceiling.
- Role-time enforcement: active administrators who play a living job now accumulate that job's tracker in addition to `Admin` and `Overall`. A `JobRequirementOverridePrototype` is authoritative on both server and client, so original alternate requirement sets can no longer bypass `LuaMRoleLadder`; this closes the Brigmedic 20-hour and DirectorOfCare 50-hour overall-only bypasses while preserving their required Security Officer/Medic role time.
- Verification: `LuaMGamePresetRulesTest` passed 2/2 and now resolves Apocalypse, Mixed, and low-pop rule prototypes plus runtime role-gate behavior. The full readiness gate passed with no issues or warnings, including dependency audit, feature validator, gateway test, LuaM Content/Integration filters, and local stack smoke. Source package `luam-local-release-20260711-180338.zip` passed independent verification (468 files, SHA256 `c94bafdd216c31f3b47c8b808fbfb9dae33c9a5ca8b03d88a183b818772a563e`); the client/server surface audit reported zero violations.
- Artifacts: server SHA256 `a74c4756d102d6603406a753f00efc8ab49448b430b808d0b10aec0b1cb97802`; client SHA256 `758920469509ea81726fe118b51d01aa21665a7885c5a63733f55f2b2e401990`; immutable client URL `http://188.127.225.57:1213/758920469509ea81726fe118b51d01aa21665a7885c5a63733f55f2b2e401990/SS14.Client.zip` returned HTTP 200. The deployed config SHA256 is `1bcb45e8f62102a5ba75a3349da46136395b73b7e7780050fa5b6da0e3a7a665`.
- Live result: `/status` and the public hub both report round 115 in the lobby with preset `Апокалипсис`; the service is active with `NRestarts=0`, external client delivery passes, and the journal has zero errors since startup. Post-verify `strict_ok=false` only because a lobby with zero players has no `round_start_time` yet.
- Backups: server `/opt/monolith-ds/backups/server-20260711-apocalypse-role-time-277.2.1`; prior config `/opt/monolith-ds/backups/server_config-before-20260711-apocalypse-role-time-277.2.1.toml`, SHA256 `2f87b8a9cd153482c22848e820ae46891980144bfa5c744fc6c20b6925d90c64`; data `/opt/monolith-ds/backups/data-20260711-apocalypse-role-time-277.2.1.tar.gz`, SHA256 `10df6f711c596dafbc1a55d22cf8e74ecab51564573f3ea38af0aa584ccd1c4e`, verified by both `gzip -t` and a complete `tar -tzf` listing.

## Local Smoke Test

When manual local runtime verification is needed:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\start_local_stack.ps1 -Reset
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\test_local_stack.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\stop_local_stack.ps1 -Force
```

Use `-SkipClient` with `test_local_stack.ps1` if only the gateway and content server need to be checked.

## Live Read-Only Preflight

For live status checks that must not restart or update the remote server:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\monolith-restart-when-empty.ps1 -VerifyOnly -SkipSsh
```

This checks `/status`, `/info`, the public hub entry, either ACZ or complete external client delivery, manifest/build identity, expected tags, soft max players, panic bunker state, round age, and the 7-day round description. Use `-Strict` only when warnings should fail the check. Omit `-SkipSsh` only when read-only SSH journal/service metrics are intentionally needed.

## Admin Rank Ladder

The local admin rank policy is stored in:

- `Tools\luam_admin_ranks.yml`
- `Tools\generate_luam_admin_rank_sql.py`

Generate reviewed SQL import files with:

```powershell
python Tools\generate_luam_admin_rank_sql.py
```

Validate the policy and rendered SQLite import without writing files:

```powershell
python Tools\generate_luam_admin_rank_sql.py --check-only --json
```

This writes:

- `DeploymentPackages\LuaM\luam-admin-ranks.sqlite.sql`
- `DeploymentPackages\LuaM\luam-admin-ranks.postgres.sql`
- `DeploymentPackages\LuaM\luam-admin-ranks.md`

`Tools\build_luam_release_package.ps1` regenerates these files before packaging, so the release zip carries both the source policy and the reviewed import artifacts.

The ladder is:

- `LuaM Стажер`
- `LuaM Модератор`
- `LuaM Игровой админ`
- `LuaM Старший админ`
- `LuaM Главный админ`
- `LuaM Владелец`

Policy guardrails:

- `HOST` is only present on `LuaM Владелец`.
- `PERMISSIONS` is only present on `LuaM Главный админ` and `LuaM Владелец`.
- Prefer creating/editing ranks through the in-game Permissions Panel when possible.
- SQL files are local import artifacts only; review database backups before applying them.

## Pre-Release Checklist

- Confirm a non-expired explicit deployment authorization exists for each intended mutation; never lift only the boolean freeze.
- Confirm strict readiness, source verification, binary receipt, and canary audit were regenerated after the final authorization/policy change.
- Confirm every policy-required file is tracked and packaged.
- Run `Tools\test_luam_release_contract.ps1`, then re-run the policy-owned production tests and smoke checks through strict readiness.
- Confirm `admin-rank-ladder` passes in `Tools\check_luam_release_ready.ps1`.
- Run local stack smoke test.
- Check that PDA no longer exposes a free-form AI message input.
- Check that AI still answers through chat/radio markers and `/luam`.
- Check that the admin AI Director review button calls `/review`, returns structured influence/process/tempo/economy/crew/safety remarks, and that copy/save/history controls work.
- Check that PDA transfer accepts copied/lowercase/formatted bank IDs.
- Check that hourly payroll reaches balance after timer elapses.
- Check that payroll feedback uses `bank-payroll-received` and the starting grant remains 75000.
- Check that the admin tab opens `luamai`.
- Check that LuaM underwear loadouts have matching inventory slots and visuals.
- Check that rogue AI can be selected by synthetic-control logic through `AiRemoteController`.
- Check that accepted tasks explain coordinates and completion by LuaM marker/site note.
- Check that generated debris sites include hostile contacts every fifth site.
- Check that donation shop access and spending are still manual-admin controlled.
- Check that no `bin`, `obj`, logs, local DBs, or cache directories are included in the release package.
