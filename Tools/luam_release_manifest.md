# LuaM Local Release Manifest

Current policy: the 2026-07-12 seven-day round release remains live. The operator explicitly requested the 2026-07-13 durable-transfer, animal-control, and sector-interception update; all local release gates have passed, and remote deployment remains frozen only until the guarded zero-player rollout begins.

The machine-readable policy lives in `Tools\luam_release_policy.json`. The deploy helper reads that JSON file first and falls back to this manifest only if the JSON file is missing.

This manifest exists to prevent the local LuaM feature pack from being partially released. The LuaM package scope should be present in the git snapshot before deployment; use `Tools\check_luam_release_ready.ps1` as the automated gate for this check.

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
- An atomic transfer journal keyed by durable operation ID, restart reconciliation, explicit acknowledgement, and idempotent replay protection against duplicate debits.
- Durable, fail-closed settlement for PDA transfers, ATM cash exchange, cargo order payments, and shipyard purchases and sales before irreversible world changes.
- Donation shop state, PDA listings, and manual account access.
- Sector story memory, dynamic tasks, route text, site notes, evidence, terminal/report flows.
- Dynamic quest debris and periodic hostile contacts on every fifth debris site.
- Four staggered radar-only contact profiles (civilian, cargo, distress, unknown) on the active primary sector map, with a global hard cap of four, player-presence gating, collision-free routes, expiry, and round cleanup.
- Physical sector-terminal interception that converts contacts into bounded route, distress, or cargo tasks without NPC ships or debris grids; recoveries must be delivered back to a terminal and are removed on completion, expiry, reset, or round cleanup.
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
- Server-only deterministic expedition planning with versioned seeds, a stable plan hash, bounded macro-regions, six POIs, connectivity/uniqueness validation, and an admin diagnostic command. This is a planning foundation only; it is not yet connected to ECS world materialization.
- Character persistence groundwork that archives profile rows instead of physically deleting them, exposes the active server-side `Profile.Id`, and carries matching SQLite/PostgreSQL migrations and integration coverage. This is not yet the complete eternal-character or career system described by the design document.
- Server-only progression rules for exact seven-day `CampaignShiftId` periods, the 100-XP weekly cap, the joint 700-XP/seven-shift level gate, level 10 cap, and stable award idempotency keys. Database ledgers and gameplay award sources are not connected yet.

## Files That Must Be Included

Tracked modified files:

- `Content.Client/PDA/PdaBoundUserInterface.cs`
- `Content.Client/PDA/PdaMenu.xaml`
- `Content.Client/PDA/PdaMenu.xaml.cs`
- `Content.Client/Lobby/LobbyState.cs`
- `Content.Client/RoundEnd/RoundEndSummaryWindow.cs`
- `Content.Server/PDA/PdaSystem.cs`
- `Content.Server/_NF/Bank/BankSystem.cs`
- `Content.Server/_NF/Bank/ATMSystem.cs`
- `Content.Server/Cargo/Systems/CargoSystem.Orders.cs`
- `Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs`
- `Content.Server/_NF/Shipyard/Systems/ShipyardSystem.cs`
- `Content.Shared/PDA/PdaComponent.cs`
- `Content.Shared/PDA/PdaMessagesUi.cs`
- `Content.Shared/PDA/PdaUpdateState.cs`
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
- `Content.Server.Database/Migrations/Postgres/PostgresServerDbContextModelSnapshot.cs`
- `Content.Server.Database/Migrations/Sqlite/20260713070603_PreserveCharacterProfiles.cs`
- `Content.Server.Database/Migrations/Sqlite/20260713070603_PreserveCharacterProfiles.Designer.cs`
- `Content.Server.Database/Migrations/Sqlite/20260713162532_DurablePdaBankTransfers.cs`
- `Content.Server.Database/Migrations/Sqlite/20260713162532_DurablePdaBankTransfers.Designer.cs`
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
- LuaM-linked admin tab, Bounty Contracts, Bank/Payroll locale, LateJoin, cartridge, research, role-time, pinpointer, synthetic-control, clothing/underwear slot, rogue AI, character profile, and test-harness files listed in `Tools\check_luam_release_ready.ps1`.
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
- `Tools/start_local_stack.ps1`
- `Tools/stop_local_stack.ps1`
- `Tools/test_local_stack.ps1`
- `Tools/test_local_frontier.ps1`
- `Tools/luam_release_policy.json`
- `Tools/luam_character_progression_design.md`
- `Tools/luam_expedition_implementation_proposal.md`
- `Tools/luam_expedition_worldgen_design.md`
- Current LuaM changelog fragments under `Resources/Changelog/Parts/**` when present.

Approved pre-existing work outside the LuaM source package:

- `Resources/Maps/_NF/Shuttles/barge.yml`
- `Resources/Maps/_NF/Shuttles/caladrius.yml`
- `Resources/Maps/_NF/Shuttles/Expedition/pathfinder.yml`
- `Resources/Maps/_NF/Shuttles/hammer.yml`
- `Resources/Maps/_NF/Shuttles/Nfsd/hospitaller.yml`
- `Resources/Maps/_NF/Shuttles/stasis.yml`
- `Resources/Prototypes/_NF/Shipyard/barge.yml`
- `Resources/Prototypes/_NF/Shipyard/caladrius.yml`
- `Resources/Prototypes/_NF/Shipyard/Expedition/pathfinder.yml`
- `Resources/Prototypes/_NF/Shipyard/hammer.yml`
- `Resources/Prototypes/_NF/Shipyard/Nfsd/hospitaller.yml`
- `Resources/Prototypes/_NF/Shipyard/stasis.yml`

These six prototype/map pairs are deliberately excluded from the LuaM source package, not ignored: they are preserved as existing NF vessel work, recorded by the package scope audit as allowed outside-package changes, included by the full server `Resources` build, and still covered by the final release-surface audit.

Before any release, confirm the exact untracked list with:

```powershell
git ls-files --others --exclude-standard Content.Client/_LuaM Content.Server/_LuaM Content.Shared/_LuaM Content.Server/Database/ServerDbBase.cs Content.Server/Database/ServerDbManager.cs Content.Server.Database/Model.cs Content.Server.Database/Migrations/Postgres/20260713070624_PreserveCharacterProfiles.cs Content.Server.Database/Migrations/Postgres/20260713070624_PreserveCharacterProfiles.Designer.cs Content.Server.Database/Migrations/Postgres/20260713162540_DurablePdaBankTransfers.cs Content.Server.Database/Migrations/Postgres/20260713162540_DurablePdaBankTransfers.Designer.cs Content.Server.Database/Migrations/Postgres/PostgresServerDbContextModelSnapshot.cs Content.Server.Database/Migrations/Sqlite/20260713070603_PreserveCharacterProfiles.cs Content.Server.Database/Migrations/Sqlite/20260713070603_PreserveCharacterProfiles.Designer.cs Content.Server.Database/Migrations/Sqlite/20260713162532_DurablePdaBankTransfers.cs Content.Server.Database/Migrations/Sqlite/20260713162532_DurablePdaBankTransfers.Designer.cs Content.Server.Database/Migrations/Sqlite/SqliteServerDbContextModelSnapshot.cs Content.Server/StationEvents/Events/VentCrittersRule.cs Content.IntegrationTests/Tests/_LuaM Content.IntegrationTests/Tests/_NF/BountyContracts Content.Tests/Client/_LuaM Content.Tests/Server/_LuaM Content.Tests/Shared/_NF/BountyContracts Resources/Changelog/Parts Resources/ConfigPresets/_LuaM Resources/Locale/en-US/_LuaM Resources/Locale/ru-RU/_LuaM Resources/Prototypes/_LuaM Resources/Prototypes/_Mono/Entities/Markers/Spawners/shuttles.yml Resources/ServerInfo/_LuaM Resources/Textures/_LuaM Tools
```

For a real git-based release gate, run it without `-AllowUntracked`. It must pass before deployment:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\check_luam_release_ready.ps1
```

To build a local zip package without touching the remote server:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\build_luam_release_package.ps1
```

The package is written under `DeploymentPackages\LuaM` and includes a `PACKAGE_MANIFEST.json` with SHA256 hashes.
It also includes the generated admin-rank SQL/markdown artifacts under `DeploymentPackages\LuaM`.
The package also includes LuaM-owned resources: config presets, locales, prototypes, guidebook XML, textures, server intro text, and key linked code/resources outside `_LuaM` that are required by the validator.

## Build live server release

For live deployment, build a Release server without Hybrid ACZ and inject immutable external client metadata:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\build_luam_server_release.ps1 -ExternalClientBaseUrl http://188.127.225.57:1213 -Json
```

This writes `release\SS14.Client.zip` and a small `release\SS14.Server_linux-x64.zip`, verifies that the server package does not contain `Content.Client.zip`, injects `build.json` with the client SHA256 and immutable URL, and runs `Tools\audit_release_surface.ps1 -ReleaseDir release`. Publish the client first with `Tools\provision_monolith_client_static.ps1`; `-HybridAcz` is emergency fallback only.
`PACKAGE_MANIFEST.json` also records a `scopeAudit` block. The verifier fails if changed files outside the package are not explicitly allowlisted.

To verify a built package:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\verify_luam_release_package.ps1 -PackagePath DeploymentPackages\LuaM\<package>.zip
```

The verifier checks manifest/file-list parity, payload hashes, local junk paths, generated admin-rank artifacts, recorded readiness evidence, and the package `scopeAudit`.
It also checks strict UTF-8 for packaged text files and requires key LuaM resource/code artifacts to be present.

To audit the actual public client/server release zips before upload:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\audit_release_surface.ps1 -ReleaseDir release
```

This fails if `release\*.zip` contains `.pdb`, source files, project/solution files, local secret/config names, logs, caches, or common local build directories.

To deploy a verified server release zip to the VPS, use the guarded deploy helper instead of hand-running `scp`/`unzip`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\deploy_luam_server_release.ps1 -PackagePath release\SS14.Server_linux-x64.zip -ExpectedSha256 <sha256> -Tag <tag> -ConfigSourcePath server_config.remote.toml -RemoteConfigPath <live-server_config.toml> -RequireDataBackup -RemoteDataDir <live-data-dir>
```

The helper checks `Tools\luam_release_policy.json` first and refuses a real upload while the current freeze policy is active. `-DryRun` remains available for inspecting the deploy plan without contacting the remote server.
For real deployment, the helper requires `-ExpectedSha256`, verifies `Robust.Server` plus either external `build.json` metadata or emergency Hybrid ACZ, checks the live player count, uploads the package and optional approved config, verifies both SHA256 values, stages into `/opt/monolith-ds/deploy-staging`, creates server/config/data backups, starts `monolith-ds.service`, and rolls back if start verification fails. `-ConfigSourcePath` removes silent config drift by atomically deploying the reviewed TOML. Use `-AllowClientZipRestore` only for an intentional emergency rollback.

## Current Development Slice (Not Deployed)

The 2026-07-13 expedition/persistence work is deliberately covered by the source-package, readiness, verification, and policy lists, but it has not been approved for a remote rollout.

- The expedition planner is server-only and testable without creating ECS entities. Streaming terrain, POI materialization, persistence deltas/snapshots, and `ExpeditionDirector` remain later stages.
- Profile deletion now preserves the database row through `is_archived`/`archived_at` and releases the active slot through `NULL`. A normal replacement receives a new `Profile.Id`; an archived identity returns only through the explicit, ownership-checked, idempotent `RestoreArchivedCharacterAsync` path.
- The pure progression foundation resolves exact seven-day campaign shifts and enforces the documented XP/shift level gates and award identity shape; persistence tables, ledger transactions, participation tracking, and gameplay integration remain future work.
- A strict git-based readiness run still requires every release-scope file to be tracked. `-AllowUntracked` is only for local validation while the development slice is being assembled.

## Verification Records

### 2026-07-13 — current development slice (not deployed)

- The selected update is committed as three reviewed gameplay slices plus their release-gate coverage: durable PDA operation journaling, bounded animal-population administration, and playable sector contact interception. Remote deployment remains frozen pending the zero-player rollout.
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

- Confirm server freeze is lifted.
- Confirm every file group above is tracked or explicitly packaged.
- Re-run the LuaM validator and both LuaM test filters.
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
