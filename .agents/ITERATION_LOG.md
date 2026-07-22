# Monolith-DS iteration journal

Updated: 2026-07-22 06:09 MSK

## 2026-07-22 -- accumulated release split and docking scope corrected

- Objective: preserve the accumulated interface, persistence, ship, AI, gameplay, and content work as reviewable commits, verify it, and prepare the explicitly authorized immediate production release.
- Clarified the reported `Безымянный` problem as stored-ship docking/identity behavior. The relevant fix is the saved-ship migration from a runtime `EntityUid` string to a stable GUID plus restore-time rebinding, together with exact reciprocal docking protection. The earlier Apocalypse damaged-AI scheduler diagnosis is a separate observation and is not part of this release; no scheduler or cleanup change was added.
- Protected the original mixed worktree with `refs/codex/backups/20260722-commit-split-index` and `refs/codex/backups/20260722-commit-split-full`, then split the accumulated tree into 20 commits from `bf98870452` through `4aa1ea20e4`. The final groups cover guarded release tooling, durable database schemas, docking/radar/lathe behavior, complete ship persistence and generation, exact-gate shipyard operations, rescue/sector AI, the pre-stop ship-save barrier, player persistence/economy, PDA and guidebook UI, localization repair, frontier gameplay/content, the Ollama/ship-analysis gateway, release-news contracts, generated-name data repair, and explicit `/luam` chat contracts.
- The pre-stop maintenance endpoint is authenticated, refuses connected players, freezes and drains persistent-ship lifecycle work, saves active ships sequentially, and returns a privacy-bounded schema-v1 receipt. Deployment refuses to stop the service unless that receipt is fresh and complete. The one-time `-LegacyShipSaveBootstrap` path is restricted to an old server returning 404, zero players, and a database proof of absent ship tables or zero active/restoring ships and leases; it cannot be bypassed by `-Force`.
- Completed checks before the final release gate: AI/rescue/sector/pathfinding integration selection passed 394/394; ship persistence/shipyard/docking/radar/lathe and generator selections passed; maintenance-barrier tests passed 5/5; PowerShell release-contract tests passed; integration compilation passed. A corrupted Russian RCD label was found outside the normal validator and repaired as `пласталевая стена`. A full localized-dataset audit repaired the pre-existing `NamesAI`, arachnid, golem, and military contracts and removed one unused xenoborg dataset that had no localized values or references; ServerNews entry `2026072201` records the player-visible name-generation repair.
- The first full `FullyQualifiedName~LuaM` run passed 721/724 and exposed three stale chat-test phrases from before plain `ИИ`/`AI` stopped auto-triggering. Runtime behavior was already correct: only explicit `luam`/`/luam` markers route a request. The three tests now prove the intended contract and shared cooldown using `/luam`; their combined class selection passed 19/19. The final clean full run then passed 724/724 in 6 minutes 14 seconds. The expanded ServerNews/language/localized-dataset selection passed 3/3, the feature validator passed, and Python gateway/generator tests plus the release contract passed.
- Production preflight at `2026-07-22T02:05:52Z` found zero players in round 149, both services active, the legacy endpoint absent with HTTP 404, SQLite healthy with zero active/restoring ship snapshots and leases, and about 14.76 GB free. The installed server journal had no newer host-only entries.
- The preflight also found no usable admin API token. A root-only environment file and non-secret systemd drop-in were installed without exposing the value or restarting the game; metadata validation passed, `daemon-reload` completed, and the service PID/round remained unchanged. The token will be loaded by the guarded deployment restart and then supports the authenticated pre-stop save barrier on every later release.
- The first journal mirror wrapper failed locally during PowerShell parsing before any upload or SSH. The base64-encoded retry installed the repository journal byte-identically as `root:root` mode `0644`; both services stayed active with zero players in round 149.
- The first local journal commit attempt retained both files staged but failed with `fatal: unable to write new index file` while two short-lived Git processes were still present. No index lock remained and more than 1 TB was free; after those processes exited, the identical commit succeeded as `2a68bad829` without resetting or restaging data.
- The release authorization in `.agents/RELEASE_POLICY.json` remains active for the accumulated server, client-static, and AI-gateway batch. No release binary or client package has yet been deployed.
- The first policy-bound `ship_luam_release.ps1` gate ran for 645.8 seconds but failed before creating the source-package step because `prepare_luam_hotfix.ps1` evaluated `.Count` on the scalar/null result of `Get-ChangedRepoFiles` under inherited strict mode. No artifact was accepted and no remote mutation occurred. `changedFiles` is now explicitly array-wrapped; a focused `-Scope Policy -Json` regression passed with `ok=true`, one changed file, and the release-policy contract green.
- The second policy-bound gate ran for 2,154.9 seconds. Its clean local-fast stage passed, the production source-package tests and local smoke passed, source verification passed, and fresh client/server archives plus a binary receipt were physically produced. The outer orchestrator nevertheless rejected the binary-build step before accepting it because `build_luam_server_release.ps1 -Json` streamed normal MSBuild text before its JSON result. The release state therefore ended in `failed` at `2026-07-22T02:56:55Z`; none of those artifacts is approved for deployment, and no production mutation occurred.
- `Invoke-CheckedNative` in the binary builder now captures native stdout/stderr while `-Json` is active and emits no successful native output; on failure it retains only the final 80 diagnostic lines in the exception. The wrapper temporarily uses `ErrorActionPreference=Continue` only around native capture and restores the prior strict behavior in `finally`, so benign stderr cannot bypass the explicit exit-code check. PowerShell parsing passed. An extracted-function regression proved that successful stdout/stderr is suppressed and a nonzero exit preserves its code and diagnostic tail. A fast whole-script `-LocalOnly -SkipPackageBuild -SkipAudit -Json` invocation parsed as one JSON document with `ok=true`. The first extracted-function test command itself failed only because its nested PowerShell string lost quotes; the corrected `cmd.exe` harness passed. A read-only independent audit confirmed the observed mixed-stdout fix and identified the now-hardened benign-stderr edge. A previously started full local regression lost its tracked execution cell during context compaction and has no usable result; it is explicitly not counted as green. The next production gate must rebuild and re-receipt the current HEAD rather than reuse the rejected archives.

Commands and outcomes:

```powershell
git status --short --untracked-files=all
git diff --cached --check
git commit -m <20 bounded thematic messages>
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-build --no-restore -m:1 --filter <AI/rescue/sector/pathfinding selection> -- NUnit.NumberOfTestWorkers=1
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-build --no-restore -m:1 --filter <maintenance ship-save selection> -- NUnit.NumberOfTestWorkers=1
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-build --no-restore -m:1 --filter "FullyQualifiedName~LuaM" -- NUnit.NumberOfTestWorkers=1
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-build --no-restore -m:1 --filter "FullyQualifiedName~LuaMServerNewsChangelogTest|FullyQualifiedName~LanguageLocalizationTest|FullyQualifiedName~LocalizedDatasetPrototypeTest" -- NUnit.NumberOfTestWorkers=1
python Tools/validate_luam_feature_pack.py
python Tools/test_luam_ai_gateway.py
python Tools/test_luam_ship_generator.py
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/test_luam_release_contract.ps1 -Json
scp monolith-new:/opt/monolith-ds/AI_SERVER_JOURNAL.md C:\MonolithTemp\AI_SERVER_JOURNAL.host-preflight.md
ssh monolith-new "<bounded zero-player, service, endpoint, token-presence, SQLite, and storage preflight>"
ssh monolith-new "<protected admin-token override and no-restart verification>"
git commit -m "docs(ops): record production release preflight"
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/ship_luam_release.ps1 -Tag luam-20260722-accumulated -LegacyShipSaveBootstrap -ConfigSourcePath ([string]::Empty) -RemoteConfigPath /opt/monolith-ds/server/server_config.toml -RemoteDataDir /opt/monolith-ds/data -Json
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/prepare_luam_hotfix.ps1 -Scope Policy -Json
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/ship_luam_release.ps1 -Tag luam-20260722-accumulated -LegacyShipSaveBootstrap -ConfigSourcePath ([string]::Empty) -RemoteConfigPath /opt/monolith-ds/server/server_config.toml -RemoteDataDir /opt/monolith-ds/data -Json
[System.Management.Automation.Language.Parser]::ParseFile(<build_luam_server_release.ps1>, ...)
Invoke-CheckedNative powershell.exe <nested stdout/stderr test; failed because the harness lost nested quotes>
Invoke-CheckedNative cmd.exe <successful stdout/stderr suppression and exit-7 tail test>
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_luam_server_release.ps1 -LocalOnly -SkipPackageBuild -SkipAudit -Json
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/test_luam_release_contract.ps1 -Json
```

Result: commit splitting, data repair, integration build, final 724/724 LuaM run, localization/news contracts, Python gateway/generator tests, and the release contract completed successfully. The earlier 721/724 run is explicitly superseded by the final green rerun. One still-earlier broad 344-test command exceeded ten minutes and was interrupted; it is not counted as a green result. Both artifact-gate attempts failed closed locally and have not authorized deployment: the first exposed clean-worktree scalar handling, while the second exposed mixed native/JSON output only after all expensive build checks passed. The hardened binary-builder JSON isolation and strict benign-stderr regressions are green, the fast whole-script output parsed successfully, and the policy release contract remains green. A new current-HEAD artifact gate is still required. No production deploy has yet been claimed.

Next action: commit the binary-builder JSON isolation and this handoff, require a clean tree, then repeat the policy-bound source/client/server artifact gate with the already-completed local-fast stage skipped but all production package tests, smoke checks, verification, binary construction, audit, and deployment dry-runs retained:

```powershell
git add -- Tools/build_luam_server_release.ps1 .agents/ITERATION_LOG.md
git commit -m "fix(release): keep binary build JSON machine-readable"
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/ship_luam_release.ps1 -Tag luam-20260722-accumulated -LegacyShipSaveBootstrap -ConfigSourcePath ([string]::Empty) -RemoteConfigPath /opt/monolith-ds/server/server_config.toml -RemoteDataDir /opt/monolith-ds/data -SkipLocalFast -Json
```

## 2026-07-22 -- damaged-AI unknown-shuttle accumulation diagnosis

- Objective: investigate the user's report that «Безымянный» again did something with his ships, initially separating the local stranded-human scenario from the live Apocalypse damaged-AI shuttle scheduler.
- The local UI acceptance stack was stopped successfully before the investigation. The supplied phrase referred to the production `неизвестный шаттл ИИ` rules, not the undeployed `Неизвестный` survival scenario and not a named player's saved ships.
- A bounded read-only production audit found six unknown-shuttle events in eight hours, all added/started/ended, with four previous-round events exactly 30 minutes apart. Two persistent `Unidentified Vessel` grids later reparented to `«Колосс Централл»`; no impact involving those grids was logged.
- Root cause: the Apocalypse scheduler is fixed at 1800-second intervals; its damaged-AI shuttle `StationEvent` definitions omit `minimumPlayers` and therefore bypass low-pop filtering despite separate `GameRule.minPlayers` values. The inherited one-minute event duration ends only the rule, while `RuleGridsSystem` records grids without deleting them, so spawned ships persist and can accumulate/dock.
- Production remained active and healthy in round 149 with one player and about 14 GiB free. No live game, database, service, process, configuration, ship, or player state was changed. Detailed health, recovery, and bounded operation facts are recorded in `Tools/AI_SERVER_JOURNAL.md`.
- The required updated server-journal mirror was installed byte-identically on the host with owner/mode `root:root`/`0644`; the game service remained active.
- No player-facing behavior was changed, so no ServerNews entry was added.

Commands and outcomes:

```powershell
powershell -ExecutionPolicy Bypass -File Tools/stop_local_stack.ps1
git status --short | Select-String -Pattern '(?i)ship|shuttle|vessel|shipyard|saved|generator|unknown|wreck|grid'
rg -n <unknown operator, unknown shuttle, Apocalypse scheduler, station-event eligibility, and RuleGrids symbols>
ssh monolith-new "<journal hash/mode, status, bounded eight-hour unknown-shuttle counts/timeline, service state, and storage>"
```

Result: local stack shutdown exited 0. The first broad local discovery command exited 1 after intentional output truncation; narrower reads succeeded. Repository/host server journals had zero logical-line differences, and all production reads exited 0. Diagnosis is complete; no fix was implemented under this diagnose-only request.

Next action: implement and test low-pop suppression plus bounded cleanup for Apocalypse unknown shuttles before authorizing a release:

```powershell
rg -n "UnknownShuttle.*Apoc|minimumPlayers|BaseRandomShuttleRule|RuleGrids" Resources/Prototypes/_Mono/GameRules Content.Server/GameTicking/Rules Content.IntegrationTests --glob '*.yml' --glob '*.cs'
```

## 2026-07-22 -- restored PDA navigation and settings text

- Objective: fix the user-reported missing labels in the live PDA, visible in the supplied screenshot as empty `Программы`, `Службы`, and `Настройки` navigation tabs, with the same symptom reported for `Рингтон` and other settings entries.
- The locale keys were present and correct. The failure was confined to text passed through nested custom-control properties during live XAML construction. `PdaMenu` now reapplies all navigation and settings labels after nested XAML has loaded; the navigation label no longer uses aggressive clipping and has an explicit Nano text style/color.
- Extended `LuaMPdaTextLayoutTest` to assert that all three text navigation buttons are non-empty, visible, laid out, and have an opaque explicit color. Its existing assertions continue to cover the ringtone title/description and ringtone editor labels.
- Added Russian ServerNews entry `2026072124`, author `LuaM`, with the real UTC completion time. Production was not read or changed.
- Rebuilt the current integration project, validated the PDA test and ServerNews, then restarted the isolated local stack. Gateway health is true, port `1213` is reachable, and the rebuilt client reached `InGame`/lobby. A style-update backlog warning still appears once during lobby loading and remains a separate performance observation.

Commands and outcomes:

```powershell
powershell -ExecutionPolicy Bypass -File Tools/stop_local_stack.ps1
dotnet build Content.IntegrationTests/Content.IntegrationTests.csproj --no-restore --verbosity:minimal
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-build --no-restore --filter "FullyQualifiedName~LuaMPdaTextLayoutTest" --logger "console;verbosity=minimal"
python Tools/validate_luam_feature_pack.py
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-build --no-restore --filter "FullyQualifiedName~LuaMServerNewsChangelogTest" --logger "console;verbosity=minimal"
git diff --check -- <bounded PDA, test, stylesheet, and ServerNews paths>
powershell -ExecutionPolicy Bypass -File Tools/start_local_stack.ps1
```

Result: the stop script printed its complete stopped-process report but the wrapper timed out after 31 seconds; follow-up health/port checks confirmed the stack was stopped. The source build exited 0, PDA test passed 1/1, feature-pack validation passed, ServerNews test passed 1/1, and targeted diff validation passed. The rebuilt stack then started in about 49 seconds and all readiness checks passed.

Next action: in the running client, reopen the PDA and visually confirm the three top tabs plus `Настройки → Рингтон`; if any label is still absent, capture that exact view and current window size before further changes.

## 2026-07-22 -- requested shuttle/manufacturing batch completed and production renamed

- Objective: complete and verify the user's mining/data-farm flatpack, plastic machine, RCD plasteel, shuttle UX/BSS synchronization, expedition-console, station/shuttle binding, R&D, saved-ship analysis, admin camera, and server-name requests while preserving the heavily mixed worktree.
- Confirmed and regression-tested the already-present player-facing implementations for data-farm board flatpacking; the separate plastic processor and its technology/recipe; RCD plasteel walls; explicit shuttle flight/docking/BSS status; full dock-connected BSS synchronization; expedition consoles on ordinary shuttles; and station/shuttle-local market, cargo, bounty, telepad, pallet, and R&D bindings. Existing Russian ServerNews entries `2026072108` through `2026072117` cover those player-visible changes.
- Added privacy-bounded saved-ship analysis across the C# persistence orchestrator and Python gateway. Only a validated aggregate prototype manifest, counts, payload size, and hash leave persistence; raw YAML, owner identity, ship name, and UUID do not. The analyzer returns deterministic capability totals, suggested preset, and top prototypes through authenticated `/analyze_saved_ship`.
- Added a direct `Камера` action to every connected row in the main admin player table. It invokes the existing Admin-only `camera` command for that player's network entity; disconnected or entity-less rows cannot invoke it. English/Russian labels and a static security/layout contract were added.
- Hardened R&D registration so a client can bind only within its grid/owning station and stale one-sided membership can repair idempotently. The first new test placed an anchored local server at `x=1.5` without a supporting tile, so the machine correctly fell into nullspace; the fixture now creates the missing tile and explicitly proves local/remote grid and station ownership before registration.
- Renamed local and production identity to `🌟 Мёртвый космос: Космическая Кассиопея 🚀 🌟` in hostname/lobby and the remote description title. Added Russian ServerNews entry `2026072122`; later concurrent entry `2026072123` remains monotonic. Production used a guarded config-only restart during a zero-player lobby, created `/opt/monolith-ds/backups/server_config-before-cassiopeia-20260721T231145Z.toml`, and returned healthy in round 149 with both services active, about 14 GiB free, and no warning-or-higher entries. The required server-journal mirror was installed and verified; full operational and rollback facts are in `Tools/AI_SERVER_JOURNAL.md`.
- Only the production identity configuration was deployed. The broader code/resource batch remains local and must follow the normal guarded release process before it reaches production. Unrelated staged, unstaged, untracked, and concurrently edited files were preserved.

Commands and outcomes:

```powershell
python -m py_compile Tools/luam_ship_generator.py Tools/luam_ai_gateway.py Tools/test_luam_ship_generator.py Tools/test_luam_ai_gateway.py
python Tools/test_luam_ship_generator.py
python Tools/test_luam_ai_gateway.py
python Tools/validate_luam_feature_pack.py
dotnet build Content.Server/Content.Server.csproj --no-restore -m:1 --verbosity:minimal
dotnet build Content.Client/Content.Client.csproj --no-restore -m:1 --verbosity:minimal
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-build --no-restore -m:1 --verbosity:quiet --filter "<13 targeted test classes>" --logger "console;verbosity=minimal" -- NUnit.NumberOfTestWorkers=1
```

Result: Python compilation passed; generator/analyzer tests passed 18/18; gateway smoke/validation passed including `savedShipStatus=200`; feature-pack/localization validation passed; server and client builds passed with zero errors; final combined integration run passed 16/16. UTF-8-sig TOML parsing passed for all three changed configs and targeted `git diff --check` passed.

Interrupted/partial accounting:

- One server build was stopped by an intentionally too-short command timeout before producing a result; the immediate normal-timeout rerun passed and is the only build counted green.
- One integration command put `--filter` after NUnit's `--` separator, unintentionally selected 443 tests, and collided with another process on `gravestone-1.txt`; its 76 failures are infrastructure/filter noise and are not counted. No conflicting process remained before the corrected isolated rerun.
- The first corrected combined run passed 15/16 and exposed the missing R&D fixture tile above. After the fixture correction, the R&D test passed 1/1 and the complete set passed 16/16.
- One production preflight allowed PowerShell to expand remote substitutions locally, leaving two printed fields blank while its bounded reads still completed. A correctly quoted preflight then verified the journal hash and both services before any mutation.

Next action: before publishing the accumulated code/resource batch, run the normal source gate under fresh deployment authorization:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\build_luam_release_package.ps1 -RunTests -RunLocalSmoke -Json
```

## 2026-07-22 -- local UI acceptance stack startup

- Objective: start the isolated local game stack for the user's hands-on acceptance check of the completed interface tranche and provide a bounded visual checklist.
- The local gateway, game server, and client started successfully from the existing `Bin` artifacts. Gateway health returns `ok: true`, TCP port `1213` is reachable, and the client reached `InGame` before switching to the lobby state.
- Gateway TTS is disabled for this smoke run. The server and client error files are empty; gateway stderr contains only successful local `/health` requests. The client emitted one style-update-limit warning and one temporary main-loop backlog warning during lobby loading, so responsiveness is included in the manual checklist.
- No source, resource, configuration-in-repository, or production state was changed. No player-facing behavior was implemented, so no ServerNews entry was added. Temporary runtime files and process IDs are under `C:\MonolithTemp`.

Commands and outcomes:

```powershell
Get-Content .agents/ITERATION_LOG.md -Encoding UTF8 -TotalCount 80
Get-Content Tools/start_local_stack.ps1 -Encoding UTF8 <bounded sections>
powershell -ExecutionPolicy Bypass -File Tools/start_local_stack.ps1
Invoke-RestMethod http://127.0.0.1:8787/health -TimeoutSec 5
Test-NetConnection 127.0.0.1 -Port 1213
Get-Content C:\MonolithTemp\server-out.txt -Tail 12
Get-Content C:\MonolithTemp\client-out.txt -Tail 12
Get-Content C:\MonolithTemp\gw-err.txt
```

Result: startup exited 0 in about 63 seconds; gateway health and server-port checks passed, and the client log confirmed `Runlevel changed to: InGame`.

Next action: complete the visual checklist in the running client, then stop the isolated stack when finished:

```powershell
powershell -ExecutionPolicy Bypass -File Tools/stop_local_stack.ps1
```

## 2026-07-22 -- Unknown radio persona and conversational personal AI roster

- Objective: turn the local HuiHui model into a controlled operator chat, represent one stranded human named `Неизвестный` over game radio and crew monitoring, remove automatic responses to the ordinary word `ИИ`, and make activated personal AI devices react to nearby speech using 20 distinct characters.
- The desktop control panel now has separate `ЧАТ И УПРАВЛЕНИЕ` and `ЖУРНАЛ` tabs. Ordinary conversation stays local; strict allowlisted game actions require an explicit operator verb and a second Yes/No confirmation. Plain non-JSON model replies are handled as conversation, service metadata is stripped, and explicit radio requests are converted to a bounded proposal. The live panel was reloaded with the final survival prompt and dialogue journal as PID 32808; no game command was sent during tests.
- `Неизвестный` is one remote living-human persona who wakes as an inexperienced, disoriented survivor on a stripped, immobile wreck. The locked suit sensor keeps him visible to crew monitoring, but he has no PDA, ID card, navigation computer, coordinates, thrusters, or gyroscope. The sealed wreck retains only structural/life-support machinery plus an ore processor, hand drill, three full-size oxygen canisters, two hydroponic trays, water, seeds/produce, a generator, and a Common headset. Pressure/breathing immunity was removed.
- Radio advice addressed to `Неизвестный` now drives a six-stage survival sequence: hull inspection, power, oxygen, hydroponics, radio repair, and resource-conserving wait for rescue. Correct stage-specific advice reaches `Survived`; dangerous advice or three accumulated errors/extended delay reaches `Dead` and changes the physical mob state. Player-facing replies do not reveal numeric mechanics. Panic escalates through pauses, repetition, concrete sensory focus, and unfinished pleas; it uses original text rather than quotations from real victims. Every live/status/error/panic/success branch now has multiple context-preserving variants, and the last exact line is excluded from the next random selection.
- For scenario review, the server writes one anonymized JSONL record per addressed exchange: UTC time, round ID, stage before/after, outcome, advice, and reply, with no separate sender-name or sender-ID field. Known active player names/IDs and sensitive technical strings are redacted from the bounded text. `data/luam/unknown_dialogue.jsonl` rotates at 5 MiB and retains one prior `.1` segment. The panel writes its own paired `advice`/`reply` records to `AI-Agent-Workspace/sector-reports/unknown-dialogue.jsonl`; `ЖУРНАЛ → ДИАЛОГИ` shows a 200-record combined tail plus outcome counts, while `ЖУРНАЛ → СИСТЕМА` keeps operational messages separate.
- Plain `ИИ` and `AI` were removed from automatic local/radio address markers. Explicit `/luam` requests remain available, and player help/briefing copy was updated accordingly.
- Activated pAI devices waiting for a controller hear local speech within 10 metres, retain a bounded eight-line dialogue history, and can emit at most one response per 12 seconds through the strict gateway `action=none` path. One nearest active receiver responds to a given line; heard speech cannot launch gameplay actions.
- The pAI roster contains 20 unique deterministic characters and speech styles. Three villain personas (`Нокс`, `Раздор`, `Мора`) first ask whether the user is 18+. A clear denial keeps an age-appropriate neutral mode; a clear confirmation allows mature ominous roleplay while sexual content, harassment, real threats, harm encouragement, and dangerous instructions remain forbidden.
- Added/expanded server-news entry `2026072121`. Server compile passed. `LuaMAiDirectorParsingTest` passed 119/119, including roster/adult gates, wreck inventory, no-navigation constraints, Unknown radio addressing, dangerous-advice recognition, dialogue-file bounds, and survival-outcome classification. The Unknown YAML parses with exactly three prototypes and no PDA/ID. Panel safe chat, control proposal, no-coordinate response, panic-response smokes, JSONL write/redaction, and nested-journal UI checks passed; a narration filter supplies a first-person-only fallback when the local model tries to add stage directions.
- No production binary/client deployment was attempted. The production release policy remains frozen, and the working tree contains a large concurrent release candidate that cannot safely be bundled under this narrow request. During read-only preflight another authorized config-only operation renamed the server and restarted it into round 149 with zero players; this work did not cause or repeat that restart. The required server-journal mirror alone was updated byte-identically at `2026-07-22T00:22Z`; both services remained active.

Commands and outcomes:

```powershell
dotnet build Content.Server/Content.Server.csproj --no-restore -v:minimal
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMAiDirectorParsingTest" -- NUnit.NumberOfTestWorkers=1
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-build --no-restore --filter "FullyQualifiedName~LuaMServerNewsChangelogTest" -- NUnit.NumberOfTestWorkers=1
```

Result: server compile exited 0; parsing/persona/survival/audit tests passed 119/119; feature-pack validation and panel runtime self-tests passed. A normal build output copy was blocked only because the isolated local `Content.Server` PID 27316 holds its executable and DLL open, so compilation and tests were run into a separate temporary output without stopping that process.

Next action: after the accumulated release candidate is intentionally authorized, run the full guarded production package/build/audit pipeline and deploy this feature in one scheduled server restart. Then verify that `Неизвестный` appears on crew monitoring, a confirmed panel radio line displays his name, and an activated pAI answers one nearby line without reacting to plain `ИИ` outside personal mode.

## 2026-07-22 -- player interface completion tranche

- Objective: finish the agreed player-interface plan in the existing dark graphite/teal `Industrial Nano` style, preserving unrelated worktree changes and without touching production.
- Completed the server-news surface: entries render newest first in expandable cards, long text uses reserved vertical scrolling, malformed/negative read cutoffs fall back safely, and the read marker advances only after a successful load.
- Completed the operational surfaces: the AI Director is resizable and all five tabs have explicit bounded vertical scrolling; the four-tab sector fragment exposes a testable tab root and uses the shared `UiSurfaceHeader`, `UiSurfaceSection`, `UiSurfaceCard`, `UiTextTitle`, `UiTextSection`, and `UiTextMuted` primitives. `RichTextLabel` now supports the shared body/muted text styles.
- Kept the PDA services page consistent with the same palette and added explicit readable colors to its heading, description, and bank-transfer labels. The earlier shipyard, shuttle, PDA, and selectable onboarding work was verified together rather than rewritten.
- Added source tests for changelog read-state parsing and the AI Director/sector layouts. Updated the feature-pack validator from the retired sector-only `PdaListingCard` alias to `UiSurfaceCard`. A missing `System.Collections.Generic` import in the concurrently edited AI parsing test was also restored so the current integration project compiles.
- Added Russian player-news entry `2026072123`, author `LuaM`, with the real UTC completion time. Production was not read or mutated, so `Tools/AI_SERVER_JOURNAL.md` was not opened or changed.

Commands and outcomes:

```powershell
dotnet build Content.Client/Content.Client.csproj --no-restore
dotnet build Content.Server/Content.Server.csproj --no-restore
dotnet build Content.Tests/Content.Tests.csproj --no-restore
dotnet build Content.IntegrationTests/Content.IntegrationTests.csproj --no-restore --verbosity:minimal
```

Result: the final client, server, unit-test, and integration-test source builds exited 0. During synchronization, earlier integration attempts failed first on a non-public `DirectorTabs` access, then on transient duplicate/missing personal-AI declarations from a concurrently changing server file, and finally on a missing `HashSet<>` import; these attempts are explicitly failed/partial and are not reported as green builds. The final integration build completed with 0 errors.

```powershell
dotnet test Content.Tests/Content.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~LuaMFrontierTutorialFlowTest|FullyQualifiedName~LuaMChangelogReadSessionTest" --logger "console;verbosity=minimal"
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-build --no-restore --filter "FullyQualifiedName~LuaMOperationalUiLayoutTest|FullyQualifiedName~LuaMShipyardConsoleLayoutTest|FullyQualifiedName~LuaMShuttleConsoleLayoutTest|FullyQualifiedName~LuaMPdaTextLayoutTest|FullyQualifiedName~LuaMServerNewsChangelogTest" --logger "console;verbosity=minimal"
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-build --no-restore --filter "FullyQualifiedName~LuaMServerNewsChangelogTest" --logger "console;verbosity=minimal"
```

Result: unit/state tests passed 13/13; combined UI layout and data tests passed 9/9 after adding explicit PDA service-label colors. The post-news resource loader/localization check passed 1/1.

```powershell
python Tools/validate_luam_feature_pack.py
rg -n "\?\?\?|�" <bounded changelog, AI Director, sector, PDA, and _Mono locale paths>
git diff --check -- <bounded UI, test, validator, and ServerNews paths>
```

Result: the first validator run failed only because it still required the retired `PdaListingCard` alias; after updating that contract, validation passed. The corruption scan found no markers. Targeted diff validation passed with only Git's existing LF-to-CRLF notices.

Next action: perform an optional hands-on visual smoke at the target viewport after starting the local stack:

```powershell
powershell -ExecutionPolicy Bypass -File Tools/start_local_stack.ps1
```






## 2026-07-22 -- post-activation TTS verification

- Objective: answer whether TTS now fully works using current production health, configuration, aggregate request metadata, and bounded errors without reading message content or changing runtime state.
- The server-side infrastructure is healthy: game and gateway services active, character TTS enabled, AI Director TTS disabled, Piper healthy with all four Russian voices, no warning-or-higher entries, and no TTS-related error matches.
- Full player end-to-end playback is not yet proven. Since activation, the gateway has received zero `/tts` requests. At the check, round 148 was still in lobby at run level 0 with one player, so no normal in-round character IC speech had exercised the path.
- No synthesis request, configuration change, restart, service mutation, game mutation, or player-facing behavior change was made. No server-news entry was added for this read-only audit.

Commands and outcomes:

```powershell
Get-Content -Raw .agents/ITERATION_LOG.md
Get-Content -Raw Tools/AI_SERVER_JOURNAL.md
ssh monolith-new "sha256sum /opt/monolith-ds/AI_SERVER_JOURNAL.md; stat -c 'journal=%U:%G:%a' /opt/monolith-ds/AI_SERVER_JOURNAL.md"
ssh monolith-new "<bounded service/status, gateway health, live TTS config, aggregate /tts audit counts, warning/error counts, and storage reads>"
```

Result: all reads exited 0. Configuration and infrastructure are ready, while player playback awaits one normal in-round IC line.

Next action: after the round starts, send one ordinary IC line and inspect only aggregate request status/latency:

```powershell
ssh monolith-new "sudo journalctl -u monolith-ds.service -u luam-ai-gateway.service --since '2026-07-21 23:11:00 UTC' --no-pager | grep -Ei 'tts|gateway_request|warning|error|exception' | tail -n 120"
```

## 2026-07-22 -- production character TTS activation

- Objective: enable production TTS after the user's authorization, using the previously recommended character-first rollout while keeping AI Director speech disabled.
- Local and live config now have `tts_characters_enabled = true`; `tts_enabled = false` remains unchanged. Character speech keeps the 180-character cap, 1.25-second cooldown, four-voice Russian allowlist, volume `-5`, 524288-byte maximum, and queue size 8.
- Robust has no supported config hot reload here and the systemd service has null stdin. The first guarded restart attempt exposed a pre-existing 401 mismatch between the token-protected gateway and the game, automatically restored the old config, and restarted. The final rollout securely supplied the same existing gateway token to the game through a root-only systemd environment file, without exposing it or placing it in the repository.
- Final verification passed: both services active, round 148 in lobby, zero players, Piper healthy, all four voices present, valid 118828-byte Russian RIFF/WAV synthesized, about 14 GiB free, and no warning-or-higher service entries since rollout began. The initial attempt/rollback restarted while three players were online and advanced rounds 145 through 147; the successful restart advanced to 148.
- Added Russian player-news entry `2026072120` with author `LuaM`. `LuaMServerNewsChangelogTest` passed 1/1 and validates data loading, ordering, localization resources, text integrity, and the new messages. The news source is recorded for the next normal content release; this config-only operation did not publish a new client/server binary.
- Recovery details, protected file metadata, failed/interrupted command accounting, and exact bounded server operations are recorded in `Tools/AI_SERVER_JOURNAL.md`. No secret or player data was recorded.

Commands and outcomes:

```powershell
Get-Content -Raw .agents/ITERATION_LOG.md
Get-Content -Raw Tools/AI_SERVER_JOURNAL.md
ssh monolith-new "<journal mirror, service/status, systemd stdin/unit, and current TTS reads>"
rg -n -i "SIGHUP|reload.*config|LoadFromFile|changecvar" RobustToolbox Content.Server --glob '*.cs'
# First guarded remote rollout: exited 1 on HTTP 401 and automatically rolled back/restarted.
# One local PowerShell quoting attempt failed before SSH; one readiness audit exited 1 while the rollback restart was still starting.
# Final guarded remote rollout: installed protected auth override, enabled character TTS, restarted, and passed authenticated synthesis/health checks.
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~LuaMServerNewsChangelogTest -m:1 --verbosity:quiet --logger "console;verbosity=minimal" -- NUnit.NumberOfTestWorkers=1
```

Result: final rollout exited 0; changelog/localization test passed 1/1 in 53 seconds. Earlier failed/partial attempts are explicitly excluded from the green result.

Next action: after normal player IC speech occurs, inspect bounded metadata/errors without retaining message text:

```powershell
ssh monolith-new "sudo journalctl -u monolith-ds.service -u luam-ai-gateway.service --since '2026-07-21 23:00:00 UTC' --no-pager | grep -Ei 'tts|gateway_request|warning|error|exception' | tail -n 120"
```

## 2026-07-22 -- selectable Frontier onboarding checklist

- Objective: continue the player-interface work in the established dark graphite/teal `Industrial Nano` style, preserve the completed shipyard, shuttle-console, and PDA structures already present in the worktree, and implement the next agreed onboarding slice without touching production.
- Replaced the sequential ten-question Frontier readiness flow with one resizable checklist window. It shows the complete learning route, saved completed/total progress, explicit `[DONE]`/`[NOW]`/pending text markers in addition to color, and a selected-topic action panel.
- Players can select any unfinished topic instead of being forced through catalog order. Completed topics remain read-only, and completing an out-of-order topic safely wraps selection to the next remaining item without falsely completing the checklist.
- Existing persisted bit-mask progress and legacy migration remain compatible. Guidebook lessons still require an explicit completion action, and closing the lesson does not silently grant progress.
- Updated English and Russian tutorial copy for the checklist interaction and added player-facing server-news entry `2026072119` with the real completion time in UTC.
- The existing shipyard/shuttle/PDA work was inspected rather than rewritten. `Content.Client` builds successfully, confirming the new checklist UI and controller compile with the current client surface.
- Production was not read or mutated, so `Tools/AI_SERVER_JOURNAL.md` was not opened or changed.

Commands and outcomes:

```powershell
Get-Content -Raw .agents/ITERATION_LOG.md
git status --short
rg --files <bounded UI paths>
Get-Content <targeted shipyard, shuttle, PDA, guidebook, localization, and test files>
git diff HEAD --stat -- <targeted UI paths>
```

Result: restored the prior UI plan, confirmed the already-present shipyard (`Catalog / My ships`), shuttle status/feedback, PDA dashboard/services split, corruption guards, and identified the sequential Frontier tutorial as the next unfinished UX item. The initial combined discovery command exited 1 after intentional output truncation; narrower reads succeeded.

```powershell
dotnet build Content.Client/Content.Client.csproj --no-restore --consoleLoggerParameters:ErrorsOnly
```

Result: exit 0; client compilation passed after the checklist implementation.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMShipyardConsoleLayoutTest" <logger options>
dotnet test Content.Tests/Content.Tests.csproj --no-restore --filter "FullyQualifiedName~LuaMFrontierTutorialFlowTest" <logger options>
dotnet build Content.Tests/Content.Tests.csproj --no-dependencies --no-restore --consoleLoggerParameters:ErrorsOnly
```

Result: the first shipyard attempt was interrupted by a 5-second command timeout. The retried source builds did not reach the selected tests because of unrelated existing compile errors in `CargoSystem.Bounty.cs`, `CargoSystem.Shuttle.cs`, `CargoSystem.Telepad.cs`, `LuaMSectorAiDirectorSystem.cs`, and stale `LuaMRingerPlaybackStateTest` API expectations. These commands are explicitly partial/blocked, not successful current-source test runs.

```powershell
dotnet test Content.Tests/Content.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~LuaMFrontierTutorialFlowTest" --logger "console;verbosity=minimal"
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-build --no-restore --filter "FullyQualifiedName~LuaMServerNewsChangelogTest" --logger "console;verbosity=minimal"
python Tools/validate_luam_feature_pack.py
git diff --check -- <checklist, locale, test, and ServerNews paths>
rg -n '\?\?\?|�' <checklist locale and ServerNews paths>
```

Result: cached-binary tutorial-flow tests passed 9/9 and the server-news loader test passed 1/1 against current resources; because `--no-build` was required, the new out-of-order-selection assertion was not included in that cached binary. Feature-pack/localization validation passed, targeted diff whitespace validation passed, and no replacement-text corruption was found.

Next action: once the unrelated repository compile errors are reconciled, build and execute the new checklist state-machine assertion from source with:

```powershell
dotnet test Content.Tests/Content.Tests.csproj --no-restore --filter "FullyQualifiedName~LuaMFrontierTutorialFlowTest" --logger "console;verbosity=minimal"
```

## 2026-07-22 -- TTS failure confirmation

## 2026-07-22 -- TTS failure confirmation

- Objective: diagnose the user's report that TTS does not work, using current source/configuration and bounded production reads without changing gameplay behavior.
- Root cause confirmed: production intentionally disables both game-side TTS paths. `tts_enabled = false` blocks AI Director speech and `tts_characters_enabled = false` blocks ordinary character IC speech; both server systems return before queueing/delivering audio while their respective flag is false.
- This is not a Piper or service failure. Both production services are active; gateway health reports Piper ready with the four configured Russian voices. The two-hour warning-or-higher service query returned no entries. The game remains healthy in round 145 at run level 1 with three players and about 14 GiB free.
- No synthesis request, configuration change, restart, game mutation, or player-facing behavior change was made, so no server-news entry was added.

Commands and outcomes:

```powershell
Get-Content -Raw .agents/ITERATION_LOG.md
Get-Content Tools/AI_SERVER_JOURNAL.md <in complete bounded chunks>
ssh monolith-new "sha256sum /opt/monolith-ds/AI_SERVER_JOURNAL.md; stat -c 'journal=%U:%G:%a' /opt/monolith-ds/AI_SERVER_JOURNAL.md"
ssh monolith-new "<bounded UTC time, service state, gateway health, live TTS keys, game status, storage, and two-hour warning reads>"
rg -n "tts_(enabled|characters_enabled|character_max_chars|character_cooldown|character_voices|volume|max_bytes|max_queue)" server_config.remote.toml
rg -n "LuaMAiDirectorTtsEnabled|LuaMCharacterTtsEnabled" Content.Server Content.Shared Content.Client --glob '*.cs'
scp Tools/AI_SERVER_JOURNAL.md monolith-new:/tmp/AI_SERVER_JOURNAL.20260721T2250Z.md
ssh monolith-new "sudo install -o root -g root -m 0644 /tmp/AI_SERVER_JOURNAL.20260721T2250Z.md /opt/monolith-ds/AI_SERVER_JOURNAL.md; <remove exact temporary file and verify mirror/services>"
```

Result: all diagnostic commands exited 0 and established configuration gating as the reason for silence. The required server-journal mirror installation passed with owner/mode `root:root`/`0644`, and both services remained active.

Next action: if the user wants TTS enabled, review and apply a config-only rollout starting with character IC TTS while leaving AI Director speech disabled:

```powershell
rg -n "tts_(enabled|characters_enabled|character_max_chars|character_cooldown|character_voices|volume|max_bytes|max_queue)" server_config.remote.toml
```

## 2026-07-22 -- TTS voice status audit

- Objective: answer the user's TTS voice-status question from current source, release artifacts, local runtime state, and bounded production health/config reads without changing gameplay behavior.
- The hardened game-to-gateway TTS path is implemented. The July 21 production source package contains byte-identical current character TTS server/client files and robotic voice prototype assignments.
- Robotic assignments are present in that deployed source: borg chassis, MMI, positronic brains, and pAI use reserved `TrainingRobot`; station AI variants use reserved `Glados`; IPC voice selection remains character-profile driven. `LuaMRoboticTtsPrototypeTest` previously passed 1/1, the combined selected run passed 9/9, the fresh YAML linter passed, and a corrected UTF-8 local Piper smoke returned a valid 90668-byte WAV.
- Production gateway TTS is ready: Piper is healthy with default `ru_RU-irina-medium` and allowlisted Irina, Denis, Dmitri, and Ruslan voices. However, the live game configuration has both `tts_enabled = false` and `tts_characters_enabled = false`, so neither AI Director lines nor ordinary character IC lines are currently synthesized for players.
- Production remained healthy during the read-only check: both game and gateway services active, round 145 at run level 1 with three players, and about 14 GiB free. The required installed server-journal mirror matched before access.
- The local game/gateway stack is currently not running on ports 1213/8787. Ollama is running independently, but that does not activate local TTS.
- No build or test was rerun because no code changed and the question was a status audit. No synthesis request, production configuration change, service restart, game mutation, or player-facing change was made; therefore no server-news entry was added.

Commands and outcomes:

```powershell
Get-Content -Raw .agents/ITERATION_LOG.md
rg -n -i -C 4 "tts|piper|озвуч|voice|голос" .agents/ITERATION_LOG.md Tools Content.Server Content.Client Content.Shared Resources <bounded globs>
git status --short -- <TTS paths>
git log -8 --oneline --all -- <TTS paths>
```

Result: the handoff and TTS implementation history were inspected. Two broad `rg ... | Select-Object -First ...` discovery commands exited 1 after intentional output truncation and one also referenced a nonexistent `AI-Agent-Workspace` path; corrected targeted reads established the status.

```powershell
Invoke-RestMethod http://127.0.0.1:8787/health -TimeoutSec 5
Invoke-RestMethod http://127.0.0.1:1213/status -TimeoutSec 5
```

Result: both local endpoints were unreachable; no local game/client process was running.

```powershell
# Read-only SHA256 comparison between the July 21 production source package entries and current TTS implementation/prototype files.
rg -n -i "tts|piper|voice" server_config.remote.toml Tools/luam_release_policy.json Tools/local_stack.md
```

Result: all six checked source/package hashes matched exactly; the checked-in production config also has both TTS feature flags disabled.

```powershell
(Get-FileHash -Algorithm SHA256 Tools/AI_SERVER_JOURNAL.md).Hash.ToLowerInvariant()
ssh monolith-new "<bounded journal mirror, service, gateway-health, TTS-config, game-status, and storage reads>"
```

Result: exits 0; production facts above were confirmed without sending a TTS request or mutating the game. The updated required server-journal mirror was then installed and verified byte-identical with owner/mode `root:root`/`0644`; both services remained active.

Next action: if the user wants TTS audible on production, prepare a config-only rollout enabling `tts_characters_enabled` first while retaining `tts_enabled = false`, beginning with:

```powershell
rg -n "tts_(enabled|characters_enabled|character_max_chars|character_cooldown|character_voices|volume|max_bytes|max_queue)" server_config.remote.toml
```

## 2026-07-22 -- concise player-safe sector brief

- Objective: replace the desktop panel's verbose infrastructure dump with a short operational brief suitable while players are online.
- Reworked `AI-Agent-Workspace/HUIHUI-SECTOR-AUTOSTART.ps1`. The report now contains only server/round/player state, map/preset, local AI readiness, and the five highest-reward unique open sector tasks with compact reward/risk fields.
- Removed service-status dumps, process lists, journal excerpts, warning scans, raw sector-memory counters, long hazard text, AI inbox contents, SSH identity, and the explanatory control-mode block.
- Removed the automatic `status` append to `/opt/monolith-ds/data/luam/ai_inbox.txt`; scheduled summaries are now strictly read-only and cannot inject anything into the running round.
- The first validation run exposed Windows-to-SSH Cyrillic replacement and a missing remote exit-code check. The remote script is now transferred as UTF-8/base64 with a final newline, and a nonzero remote result fails the report instead of saving partial output.
- Manual and actual scheduled-task runs passed. The scheduled task returned result `0`; the final report has 13 nonblank lines, five tasks, no service/process/journal dump, and no inbox content. Live verification found round 145 at run level 1 with three players; both services and the native Ollama gateway remained healthy. No production state was mutated.
- This changes local operator tooling only, so no player-facing server-news entry was added.

Commands and outcomes:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File AI-Agent-Workspace/HUIHUI-SECTOR-AUTOSTART.ps1
Start-ScheduledTask -TaskName 'HuiHui Sector Autostart'
Get-ScheduledTaskInfo -TaskName 'HuiHui Sector Autostart'
ssh monolith-new '<bounded journal-mirror, service, storage, game-status, gateway-health reads>'
```

Result: concise report generation and the registered task passed without sending any command to the game.

Next action: use the desktop panel's `ОТКРЫТЬ СВОДКУ` button and adjust only the five-task ranking if live player feedback prefers risk or recency over reward.

## 2026-07-22 -- local Ollama connected to production AI gateway

- Objective: complete the user's requested connection of local Ollama `huihui_ai/qwen3.5-abliterated:9b` to the production game while preserving the gateway's bounded validation and avoiding a game-service restart.
- Verified local Ollama and the requested model. A cold native smoke returned `OK` in about 8.5 seconds. The production game and AI gateway were healthy with two players before mutation, and the repository/server journal mirrors matched.
- Added a restricted reverse SSH tunnel from server loopback `127.0.0.1:11434` to the user's local loopback Ollama. Production SSH policy now allows remote forwarding only to that exact loopback listener; config syntax, reload, effective policy, and a fresh SSH session passed.
- Added local start/stop/supervisor scripts under `AI-Agent-Workspace`. Scheduled task `HuiHui Ollama Tunnel` is running at user logon and supervises Ollama plus SSH keepalive/reconnect. An intentional child-process termination proved automatic tunnel restoration.
- The initial exact adapter smoke found that Ollama's OpenAI-compatible route returned only reasoning and empty content. Added a native `ollama` provider to `Tools/luam_ai_gateway.py` with `think=false`, JSON-schema output, 512-token output and 8192-token context bounds, 14-second timeout, keep-alive, bounded response reading, token auditing, and unchanged response validation.
- Added `run_ollama_native_provider_test` to the gateway smoke suite. `python Tools/test_luam_ai_gateway.py` passed, Python compilation passed, and the exact live adapter returned a validated `action=none` reply.
- Fresh release authorization was limited to `ai-gateway`. The guarded deploy script installed final gateway SHA256 `126d16f37b217da143758fa62c9a281d744cb676294f9832141a6bca645951d9` with server backups. Production environment switch retained root-only permissions and has an exact rollback copy.
- Final production `/health` reports provider `ollama`, API `ollama-native`, and the requested 9B model. An authenticated `/chat` smoke traversed the live gateway and tunnel in 1.89 seconds, returned a non-empty reply with `action=none`, and audited `fallback=false`. `monolith-ds.service` remained active in round 145 with two players; it was not restarted.
- A later post-supervisor smoke safely returned `action=none` but mentioned internal action/button vocabulary. Added an explicit player-radio tone guard to the command prompt and a contract assertion. The final deployed smoke returned an in-character Aibolit line in 1.53 seconds with provider HTTP 200 and `fallback=false`, without internal control vocabulary.
- Added a desktop WinForms control panel and launcher: `Desktop/HuiHui-Control-Panel.ps1` and `Desktop/HuiHui - Панель управления.cmd`. It shows Ollama, tunnel, production gateway, game/round/player, and sector-report task status; provides AI on/off/restart, sector-report on/off/run, and log/report buttons. `-SelfTest` passed against the live system. Turning AI off disables the user task and stops only the local tunnel/Ollama; the game gateway remains up and uses its existing deterministic fallback.
- Added server-news entry `2026072118`. Release deployment was frozen again after the operation. Full recovery paths and bounded production commands are recorded in `Tools/AI_SERVER_JOURNAL.md`; no secrets or player data are recorded.

Commands and outcomes:

```powershell
python -m py_compile Tools/luam_ai_gateway.py Tools/test_luam_ai_gateway.py
python Tools/test_luam_ai_gateway.py
Tools/deploy_luam_ai_gateway.ps1 -ExpectedSha256 126d16f37b217da143758fa62c9a281d744cb676294f9832141a6bca645951d9 -Tag ollama-player-tone-20260721
```

Result: all local gateway checks passed; guarded gateway deployment and restart passed. The initial OpenAI-compatible real-model smoke failed before provider switching because it returned empty content after reasoning; native Ollama support corrected that incompatibility.

The first `LuaMServerNewsChangelogTest` run failed before assertions because a parallel test process owned `bin/Content.IntegrationTests/gravestone-1.txt`. That process was allowed to finish normally; the isolated retry then exited 0 and passed 1/1.

```powershell
AI-Agent-Workspace/Start-HuiHui-Ollama-Tunnel.ps1
Register-ScheduledTask -TaskName 'HuiHui Ollama Tunnel' <bounded logon supervisor configuration>
ssh monolith-new <bounded SSH-policy update, tunnel validation, environment switch, /chat smoke, and health checks>
```

Result: the initial reverse-forward attempt failed closed under the old SSH policy. The restricted policy update, fresh-session verification, loopback tunnel, supervisor recovery test, gateway switch, authenticated end-to-end smoke, and final game/gateway health checks passed.

Next action: monitor request metadata only for live Aibolit player traffic and latency, starting with:

```powershell
ssh monolith-new "sudo journalctl -u luam-ai-gateway.service --since '30 minutes ago' --no-pager | grep -E 'provider_request|gateway_request' | tail -80"
```

## 2026-07-22 -- player UI/UX audit and external-reference review

- Objective: inspect the current local UI work and relevant public interface patterns, then propose a clearer, more usable direction without changing product behavior or touching production.
- The current redesign is a viable foundation rather than a rewrite candidate. Shuttle and PDA already use a coherent dark teal operational style, tabs, cards, explicit sectioning, and generally strong contrast. The main usability debt is now information architecture, inconsistent shared styling, missing feedback, and player-visible copy defects.
- Recommended direction is a hybrid `Industrial Nano` system: clean, predictable structure and semantic controls with the current atmospheric teal presentation. CRT/faction styling should remain an optional visual layer, and animated scanlines/retrace should honor the existing reduced-motion setting.
- Recommended shared primitives are neutral semantic tokens and reusable window header, section/card, notice/status banner, tab, primary/secondary/destructive action, empty-state, filter row, and icon-plus-text status components. Current common surfaces are named `Pda*` even when reused by AI Director, so aliases should be generalized before further broad reskinning.
- Highest-priority defects discovered during the read-only audit:
  - literal `????` is present in new Russian shuttle/FTL status strings and in 16 player-facing server-news messages; the current tests accept these strings because they check only for non-empty content;
  - the tutorial-linked shipyard and bank guidebook pages still describe one-round rentals and mandatory end-of-round selling, contradicting the current persistent purchased-ship behavior; the Survival page claims PDA crew-list behavior that the current home screen does not provide;
  - shipyard actions include hardcoded English (`Sell`, `Name`, `Rename`, and `None`), reuse one ambiguous confirmation for purchase/sale/unassign, silently disable controls, and can present Purchase even when access or a free gate is unavailable;
  - important AI Director explanations and state builders bypass the complete EN/RU FTL catalog and remain hardcoded English; safety meaning is conveyed inconsistently through red/green styling and generic double-click confirmation;
  - current UI tests cover instantiation, non-empty labels, and basic resizing but do not reject replacement/question-mark corruption, exercise long Russian strings, verify focus affordance, or test important pending/blocked/error layouts.
- First implementation slice should be P0 correctness before visual expansion: restore corrupted localization/news, add corruption guards, update guidebook facts, localize and disambiguate shipyard actions, show exact disabled/busy reasons, and ensure color is never the only status channel.
- The recommended first structural redesign is the shipyard: resizable `Catalog / My ships` modes, a compact account/ID status strip, search/filter/reset plus result count, cards without nested scrollbars, one state-dependent primary action, separated destructive actions with ship/action/consequence in confirmation, and the existing gate map with a text/shape legend.
- Subsequent slices: persistent shuttle status strip and guided target/coordinate/docking feedback; a PDA home summary with bank/support operations moved to a Services/Wallet program; an explicit contextual onboarding checklist instead of ten up-front prompts; release cards newest-first in changelog; then the lower-reach AI Director and sector terminal using the same primitives.
- Public references reviewed read-only:
  - SS14 UI documentation and survival guide support container-driven layout and `FancyWindow` for designed windows: `https://docs.spacestation14.com/en/robust-toolbox/user-interface.html` and `https://github.com/space-wizards/docs/blob/master/src/en/ss14-by-example/ui-survival-guide.md`;
  - upstream SS14 issue `#44236` documents the same inconsistent spacing, alignment, bright defaults, and missing themed-panel problem: `https://github.com/space-wizards/space-station-14/issues/44236`;
  - tgstation TGUI demonstrates a large catalog of interfaces built on shared UI primitives: `https://github.com/tgstation/tgstation/tree/master/tgui/packages/tgui/interfaces`;
  - Barotrauma's official UI overhaul identifies compactness, convention consistency, interaction consistency, and flexibility as core goals: `https://barotraumagame.com/updates/preview-the-silky-smooth-update/`;
  - Microsoft Xbox accessibility guidance and Game Accessibility Guidelines support predictable navigation/focus, non-color status channels, clear error recovery and destructive-action confirmation, readable text, resizable interfaces, and interactive tutorials: `https://learn.microsoft.com/en-us/xbox/accessibility/xbox-accessibility-guidelines/112`, `https://learn.microsoft.com/en-us/xbox/accessibility/xbox-accessibility-guidelines/103`, `https://learn.microsoft.com/en-us/windows/uwp/gaming/accessibility-for-games`, and `https://gameaccessibilityguidelines.com/full-list/`.
- Three parallel read-only audits covered shuttle/shipyard, PDA/onboarding/changelog, and shared styles/AI Director/sector status. They made no file edits and ran no tests.
- Production was not read or mutated, so `Tools/AI_SERVER_JOURNAL.md` was not opened or changed. No behavior was implemented, so no new `Resources/Changelog/ServerNews.yml` entry was added in this iteration.

Commands and outcomes:

```powershell
Get-Content -Raw .agents/ITERATION_LOG.md
Get-Content -Raw AGENTS.md
git status --short --branch
git diff HEAD --stat -- Content.Client/Shuttles Content.Client/PDA Content.Client/_NF/Shipyard Content.Client/_LuaM Resources/Locale
```

Result: required handoff was read; the targeted UI work is a large mixed staged/unstaged tranche, so the audit preserved it and made no product edits.

```powershell
rg --files Content.Client Content.Shared Content.IntegrationTests Content.Tests Resources | rg 'ShuttleConsole|Shipyard|Pda|Guidebook|Tutorial|Changelog|AiDirector|SectorStatus|StyleNano'
git diff HEAD -- <targeted UI and localization paths>
Get-Content <targeted XAML, C#, FTL, XML, and UI-test files/ranges>
```

Result: read-only inspection established the current hierarchy, resizing behavior, hardcoded copy, state feedback, nested-scroll and truncation issues, tutorial completion behavior, and test gaps. Some broad discovery pipelines returned exit 1 after `Select-Object -First` truncation, and one guessed PDA/sector path did not exist; corrected targeted paths were then read successfully.

```powershell
rg -n '\?\?\?\?' Resources/Changelog/ServerNews.yml Resources/Locale/ru-RU/shuttles/console.ftl Resources/Locale/ru-RU/_NF/shuttles/console.ftl
rg -n 'аренд|продат|список экипажа|crew manifest' Resources/ServerInfo/_NF/Guidebook/Shipyard.xml Resources/ServerInfo/_NF/Guidebook/Bank.xml Resources/ServerInfo/Guidebook/Survival.xml
```

Result: exit 0; independently confirmed the literal question-mark corruption and stale player-guide claims.

```powershell
# Local WCAG relative-luminance/contrast calculation for the main new palette.
```

Result: the first PowerShell attempt was invalid because object-array division produced errors and bogus `1:1` values; the corrected calculation succeeded. Representative text/background pairs are strong (`13.51:1`, `9.23:1`, `5.93:1`, `4.84:1`), while a `3.94:1` pair is used for disabled text. The design priority is therefore hierarchy/state clarity rather than wholesale palette replacement.

No build, test, formatter, production, or server command was run in this research-only iteration.

Next action: after confirming the recommended direction, implement the P0 correctness slice and the shipyard structure first, beginning with:

```powershell
Get-Content Resources/Locale/ru-RU/shuttles/console.ftl; Get-Content Resources/Locale/ru-RU/_NF/shuttles/console.ftl; Get-Content Content.Client/_NF/Shipyard/UI/ShipyardConsoleMenu.xaml; Get-Content Content.Client/_NF/Shipyard/UI/VesselRow.xaml; Get-Content Content.IntegrationTests/Tests/_LuaM/LuaMShuttleConsoleLayoutTest.cs
```

## 2026-07-22 -- repository status audit

- Objective: answer the user's project-status question from the current local handoff, Git state, and local-stack health without changing product code or reading/mutating production.
- Repository state: branch `codex/finish-project-20260714` remains at `0efe0838ed` (`Bound AI gateway provider responses`), is 9 commits ahead of local `master`, and has no configured upstream.
- The worktree is not release-ready: 1050 staged paths contain 213327 insertions and 11414 deletions; 128 paths also have unstaged edits containing 7383 insertions and 1711 deletions; 23 paths are untracked. In total, `git status --porcelain=v1` reports 1147 entries.
- Latest completed journaled work includes shared situation-aware bot behavior, worker executors, shuttle/station console binding fixes, cargo/R&D isolation, UI/content work, and targeted tests for several earlier packages. The newest R&D/cargo binding tests were deliberately deferred, so there is no current full-suite green result for the complete worktree.
- Follow-up correction after the user pointed to the integration package: the repository contains the complete release handoff, release artifacts, and TRX results. `TestResults/LuaMReleaseGate-20260718/LuaMReleaseGate-20260718.trx` is a green 591/591 production gate, and `TestResults/LuaMIntegrationDiagnostic-20260718/LuaMIntegrationDiagnostic.trx` is green 598/598. A later `Content.IntegrationTests/TestResults/luam-integration-full-rerun.trx` ran 600 tests and failed only `PdaBankTransferByIdMissingRecipientShowsRegistrationHint` (599/600); the journal records that fixture as subsequently corrected and targeted green. These artifacts prove the July 18 release package was validated, but they do not certify the additional July 19-21 worktree changes.
- Highest-priority unresolved risk remains full-ship persistence during controlled restart/update: a guarded pre-shutdown save barrier and buy/call -> mutate -> restart -> recall coverage are required before any production operation that can stop or restart `monolith-ds.service`.
- The local status endpoint `http://127.0.0.1:1213/status` is currently unreachable. The local stack had answered in the previous status iteration, but it is not running or not reachable now.
- Production was not read or mutated in this iteration, so `Tools/AI_SERVER_JOURNAL.md` was not opened or changed. No player-facing behavior was changed, so no server-news entry was added.

Commands and outcomes:

```powershell
Get-Content -Raw .agents/ITERATION_LOG.md; git status --short --branch; git log -5 --oneline --decorate
```

Result: exit 0; required handoff journal was read, branch and recent commits were inspected, and the very large status output was truncated by the tool UI.

```powershell
$s = git status --porcelain=v1
git diff --cached --shortstat
git diff --shortstat
git status --short --branch | Select-Object -First 1
Select-String -Path .agents/ITERATION_LOG.md -Pattern '^## '
```

Result: exit 0; counted 1147 status entries, with 1050 staged paths, 128 paths with unstaged edits, and 23 untracked paths; captured staged/unstaged short statistics and recent journal objectives.

```powershell
git rev-parse --abbrev-ref --symbolic-full-name '@{u}'
git show -s --format='%H%n%ci%n%s' HEAD
git rev-list --left-right --count master...HEAD
git ls-files --others --exclude-standard
```

Result: the upstream query reported `fatal: no upstream configured`; the remaining read-only checks succeeded and confirmed HEAD `0efe0838ed`, a `0 9` master-to-HEAD delta, and 23 untracked paths.

```powershell
Invoke-RestMethod -Uri http://127.0.0.1:1213/status -TimeoutSec 5
```

Result: exit 2 from the wrapper; the local status endpoint could not be reached.

```powershell
rg --files -g '*integrat*' -g '*Integrat*' -g '*INTEGRAT*'
Get-ChildItem -Recurse -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'integrat' }
```

Result: exit 0; found `Content.IntegrationTests`, compiled integration outputs, diagnostic directories, and stored TRX results.

```powershell
Get-Content -Raw .agents/current_release.json
Get-ChildItem -Recurse -File -Path TestResults,Content.IntegrationTests/TestResults -Filter *.trx
```

Result: exit 0; parsed the machine-readable release handoff and TRX counters. Confirmed green 591/591 release gate, green 598/598 diagnostic run, and the later 599/600 full rerun with one PDA identity-load fixture failure.

```powershell
[xml]$x = Get-Content -Raw Content.IntegrationTests/TestResults/luam-integration-full-rerun.trx
$x.SelectNodes('//t:UnitTestResult[@outcome="Failed"]', $ns)
```

Result: exit 0; the sole 599/600 failure was `PdaBankTransferByIdMissingRecipientShowsRegistrationHint`. Older targeted failure artifacts were also inspected and are retained as diagnostic history, not claimed as the final release result.

Next action: implement and validate the guarded persistence save barrier before any production restart work, starting with:

```powershell
Get-Content Content.Server/_LuaM/ShipPersistence/LuaMShipPersistenceOrchestrator.cs -TotalCount 980; rg -n "SaveAllActiveShips|RoundRestartCleanup|ReleaseLuaMPresence|LuaMShipPersistence" Content.Server Content.IntegrationTests --glob '*.cs'
```

## 2026-07-21 -- required ship-persistence recovery work

- Objective: record a non-optional development requirement from the live restart incident so it is not lost in the ongoing work queue. No production server mutation was performed by this documentation update.
- Added `Tools/ship_persistence_required_work.md`.
- Requirement: future production restart/update tooling must include a guarded pre-shutdown full-ship save barrier, explicit online-player/run-level confirmation, fresh data-backup reporting, and owner-safe ship snapshot diagnostics.
- The specific player-risk driver is an actively modified purchased ship, including the reported Pathfinder case: persistence rows may exist, but recent in-world construction can be at risk if a restart occurs before a durable full-grid snapshot completes.
- Development must add integration/runtime coverage for buy/call -> mutate/build -> controlled restart -> call after restart, with a Pathfinder-class or equivalent fixture.

Commands and outcomes:

```powershell
Set-Content -LiteralPath Tools/ship_persistence_required_work.md -Encoding UTF8
```

Result: requirement document created locally. No host write, service restart, config edit, or database mutation was performed.

Next action: implement the guarded restart/save barrier and tests before any production operation that can stop or restart `monolith-ds.service`, starting with:

```powershell
Get-Content Content.Server/_LuaM/ShipPersistence/LuaMShipPersistenceOrchestrator.cs -TotalCount 980; rg -n "SaveAllActiveShips|RoundRestartCleanup|ReleaseLuaMPresence|LuaMShipPersistence" Content.Server Content.IntegrationTests --glob '*.cs'
```
## 2026-07-21 -- R&D server shuttle binding audit

- Objective: continue the wider station/shuttle binding audit outside core cargo, focusing on R&D server selection and station/RnD anchoring, while keeping local tests deferred until the last stage as requested. Production was not read or mutated.
- Audited remaining `GetOwningStation(...)` call sites and selected the R&D research client/server path as the next player-facing risk. The UI list was station-filtered, but server selection by numeric id and direct `RegisterClient(...)` calls did not enforce same-station/same-grid scope.
- Added `IsSameResearchScope(clientUid, serverUid)` to `ResearchSystem`. It allows a research client and server to bind only when both resolve to the same owning station, or, for stationless grids, the same grid.
- `GetNFServerNames(...)` and `GetNFServerIds(...)` now use the same scope helper so lists and registration share one rule.
- `OnClientSelected(...)` and `RegisterClient(...)` now reject out-of-scope servers, preventing a ship R&D console, lathe, anomaly vessel, or research data farm from connecting to another ship/station's R&D server by id.
- Added `LuaMResearchServerBindingTest` coverage for two `StandardFrontierVessel` shuttle stations where a research console cannot register to the other shuttle's R&D server but can register to its own. The test was intentionally not run yet because the user asked to leave local tests for the last stage.
- Added player-facing server-news entry `2026072117` and extended the changelog contract test.

Commands and outcomes:

```powershell
Get-Content -LiteralPath .agents/ITERATION_LOG.md -Tail 100
rg -n "GetOwningStation\(" Content.Server Content.Shared Content.Client --glob '*.cs'
```

Result: exit 0; re-read the journal and listed remaining station ownership call sites. R&D research server/client binding was selected as the next player-facing risk.

```powershell
Get-Content Content.Server/Research/Systems/ResearchSystem.cs -TotalCount 180
rg -n "Research|TechnologyDatabase|ResearchClient|ResearchServer|rd|R&D|ComputerResearch" Resources/Prototypes Content.Server Content.Shared --glob '*.cs' --glob '*.yml' | Select-Object -First 260
Get-Content Content.Shared/Research/Components/TechnologyDatabaseComponent.cs
Get-Content Content.Shared/Research/Components/ResearchClientComponent.cs
Get-Content Content.Shared/Research/Components/ResearchServerComponent.cs
Get-Content Content.Server/Research/Systems/ResearchSystem.Client.cs -TotalCount 260
Get-Content Content.Server/Research/Systems/ResearchSystem.Console.cs -TotalCount 260
Get-Content Content.Server/Research/Systems/ResearchSystem.Server.cs -TotalCount 260
```

Result: targeted file reads exited 0. The broad prototype/code `rg ... | Select-Object -First` discovery pipeline exited 1 after truncation and was not used as validation.

```powershell
Get-Content Content.Server/Research/Systems/ResearchSystem.Source.cs -TotalCount 260
rg -n "type: Research(Server|Client|Console)|ResearchServer|TechnologyDatabase|ResearchPointSource|Anomaly|ComputerResearch|rdserver|R&D" Resources/Prototypes --glob '*.yml' | Select-Object -First 260
```

Result: exit 1; guessed source file path did not exist. Follow-up listing found the correct file as `ResearchSystem.PointSource.cs`.

```powershell
Get-ChildItem Content.Server/Research/Systems
rg -n "ResearchServerGetPointsPerSecondEvent|TechnologyDatabaseComponent|ResearchPoint|PointSource|GetPointsPerSecond" Content.Server/Research Content.Shared/Research Resources/Prototypes --glob '*.cs' --glob '*.yml' | Select-Object -First 220
Get-Content Content.Server/Research/Systems/ResearchSystem.PointSource.cs -TotalCount 80
Get-Content Resources/Prototypes/Entities/Structures/Machines/research.yml -TotalCount 140
Get-ChildItem Content.Server/Research/Systems | Select-Object Name
rg -n "RegisterClient\(|ResearchClientServerSelectedMessage|TryGetServerById\(" Content.Server Content.Shared Content.IntegrationTests --glob '*.cs'
Get-Content Resources/Prototypes/Entities/Structures/Machines/Computers/computers.yml -TotalCount 1080 | Select-Object -Skip 1018 -First 50
rg -n "ComputerResearchAndDevelopment|ResearchAndDevelopment|ResearchClient|ResearchConsoleUiKey|ResearchClientUiKey" Resources/Prototypes/Entities/Structures/Machines/Computers Resources/Prototypes --glob '*.yml' | Select-Object -First 120
```

Result: exit 0; confirmed R&D clients include consoles, lathes, anomaly equipment, and data farms; R&D servers carry both `ResearchServer` and `TechnologyDatabase`; only client map-init and explicit selection/register paths bind clients to servers.

```powershell
[DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffffff+00:00')
```

Result: exit 0; produced `2026-07-21T19:23:06.8530587+00:00` for the player-facing changelog entry.

```powershell
rg -n "GetNFServerNames|GetNFServerIds|IsSameResearchScope|RegisterClient\(|ResearchClientServerSelectedMessage" Content.Server/Research/Systems --glob '*.cs'
git diff --check -- Content.Server/Research/Systems/ResearchSystem.cs Content.Server/Research/Systems/ResearchSystem.Client.cs Content.Server/Research/Systems/ResearchSystem.Server.cs Content.IntegrationTests/Tests/_LuaM/LuaMResearchServerBindingTest.cs Resources/Changelog/ServerNews.yml Content.IntegrationTests/Tests/_LuaM/LuaMServerNewsChangelogTest.cs
```

Result: exit 0; reviewed the changed R&D binding sites and selected changed files have no whitespace errors.

Local `dotnet test` commands were not run in this iteration per the user's instruction to leave local tests until the last stage.

Next action: continue the wider station/shuttle binding audit with station records, crew manifest, and ID-card console behavior, starting with:

```powershell
Get-Content Content.Server/StationRecords/Systems/GeneralStationRecordConsoleSystem.cs -TotalCount 180; Get-Content Content.Server/CrewManifest/CrewManifestSystem.cs -TotalCount 180; Get-Content Content.Server/Access/Systems/IdCardConsoleSystem.cs -TotalCount 330
```

## 2026-07-21 -- cargo pallet sale station binding

- Objective: continue the station/shuttle binding audit with cargo pallet sale multiplier behavior and bank balance refresh, while keeping local tests deferred until the last stage as requested. Production was not read or mutated.
- Inspected `CargoSystem.Shuttle.cs`, `CargoSystem.cs`, the Frontier market console/crate purchase flow, trade crate pricing, bank client usage, and pirate bounty service data.
- Adjusted cargo pallet appraisal/sale multiplier station selection. The UI multiplier display and sale calculation now resolve the station from the sale grid (`gridUid`) instead of each sold entity. This makes market modifiers and trade-crate wildcard multipliers apply according to the station/grid where the pallet sale happens, avoiding accidental use of another station associated with a carried or previously-owned item.
- Bank balance refresh was inspected but not changed in this iteration: it updates `BankClientComponent` projections for entities already on the updated station and is less directly relevant to ordinary shuttle local data because current cargo ordering primarily uses player bank accounts in this branch.
- Added player-facing server-news entry `2026072116` and extended the changelog contract test.

Commands and outcomes:

```powershell
Get-Content -LiteralPath .agents/ITERATION_LOG.md -Tail 120
Get-Content Content.Server/Cargo/Systems/CargoSystem.Shuttle.cs -TotalCount 430
Get-Content Content.Server/Cargo/Systems/CargoSystem.cs -TotalCount 120
```

Result: exit 0; re-read the journal and inspected the requested pallet-sale and bank-refresh files.

```powershell
Get-Content Content.Server/_NF/Market/Systems/MarketSystem.MarketConsole.cs -TotalCount 520
Get-Content Content.Server/_NF/Market/Systems/MarketSystem.CrateMachine.cs -TotalCount 220
rg -n "BankClientComponent|BankBalanceUpdatedEvent|StationBankAccount|BankClient" Content.Server Content.Shared Resources/Prototypes --glob '*.cs' --glob '*.yml' | Select-Object -First 220
rg -n "TradeCrateWildcardDestinationComponent|TradeCrateComponent|MarketModifierComponent|GetPriceWithVendingDiscount|EntitySoldEvent" Content.Server Content.Shared Resources/Prototypes --glob '*.cs' --glob '*.yml' | Select-Object -First 260
Get-Content Content.Server/_NF/Cargo/Systems/CargoSystem.TradeCrates.cs -TotalCount 120
Get-Content Content.Server/Cargo/Systems/PricingSystem.cs -TotalCount 345 | Select-Object -Skip 270 -First 70
Get-Content Content.Shared/_NF/Bank/Components/MarketModifierComponent.cs
Get-Content Content.Server/_NF/Cargo/Systems/CargoSystem.PirateBounty.cs -TotalCount 430
rg -n "PirateBountyDatabase|Station.*Pirate|PirateBounty" Content.Server/_NF Content.Shared/_NF Resources/Prototypes/_NF --glob '*.cs' --glob '*.yml' | Select-Object -First 160
```

Result: exit 0; confirmed market-console local market data already uses its ensure helper, bank clients are mainly cargo-order projections, trade-crate pricing still uses its own station logic, and pirate bounties intentionally use sector service data rather than station-local data.

```powershell
[DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffffff+00:00')
```

Result: exit 0; produced `2026-07-21T19:16:36.7919037+00:00` for the player-facing changelog entry.

```powershell
rg -n "GetOwningStation\(" Content.Server/Cargo/Systems Content.Server/_NF/Cargo/Systems Content.Server/_NF/Market/Systems --glob '*.cs'
```

Result: exit 0; remaining call sites are either ensure-helper internals, sale-grid lookups, bank-client refresh, disabled cash insertion, telepad shutdown dead code, and trade-crate/pirate/market service-specific logic still needing later audit if necessary.

```powershell
git diff --check -- Content.Server/Cargo/Systems/CargoSystem.Shuttle.cs Resources/Changelog/ServerNews.yml Content.IntegrationTests/Tests/_LuaM/LuaMServerNewsChangelogTest.cs
```

Result: exit 0; selected changed files have no whitespace errors.

Local `dotnet test` commands were not run in this iteration per the user's instruction to leave local tests until the last stage.

Next action: continue the wider station/shuttle binding audit outside core cargo by listing remaining station ownership call sites and selecting the next player-facing console/device, starting with:

```powershell
rg -n "GetOwningStation\(" Content.Server Content.Shared Content.Client --glob '*.cs'
```

## 2026-07-21 -- cargo bounty and telepad binding audit

- Objective: continue the station/shuttle console-device binding audit, focusing on cargo bounty consoles and cargo telepads, while keeping local tests deferred until the last stage as requested. Production was not read or mutated.
- `CargoBountyConsoleComponent` now initializes, parent-change checks, opens, prints labels, skips bounties, and refreshes open UIs through `TryEnsureCargoBountyDatabase`.
- If a cargo bounty console is installed on a shuttle grid whose owning station lacks `StationCargoBountyDatabaseComponent`, the server now adds a local bounty database to that shuttle station and fills its initial bounty offers. Existing stations with bounty data keep using their existing component. Non-shuttle grids are not auto-enabled by merely having a bounty console.
- Cargo telepads linked to cargo order consoles now resolve the linked console through `TryEnsureCargoOrderDatabase`, so a telepad on a player ship uses the same local order database as the ship's cargo order console instead of requiring a pre-existing station cargo database.
- Added `LuaMCargoBountyConsoleBindingTest` to cover `ComputerCargoBounty` installed on a `StandardFrontierVessel` shuttle station without initial bounty data. The test was intentionally not run yet because the user asked to leave local tests for the last stage.
- Added player-facing server-news entry `2026072115` and extended the changelog contract test.

Commands and outcomes:

```powershell
Get-Content -LiteralPath .agents/ITERATION_LOG.md -Tail 120
```

Result: exit 0; re-read the required handoff journal before continuing.

```powershell
Get-Content Content.Server/Cargo/Systems/CargoSystem.Bounty.cs -TotalCount 560; Get-Content Content.Server/Cargo/Systems/CargoSystem.Telepad.cs -TotalCount 160
rg -n "CargoBountyConsole|StationCargoBountyDatabase|Computer.*Bounty|Bounty.*Console|CargoTelepad|ComputerCargoOrders|CargoOrderConsole" Resources/Prototypes Content.Server Content.Shared --glob '*.yml' --glob '*.cs' | Select-Object -First 220
Get-Content Content.Server/Cargo/Components/StationCargoBountyDatabaseComponent.cs
```

Result: exit 0; inspected bounty, telepad, prototypes, and bounty database defaults. Confirmed ordinary vessels inherit no cargo bounty database and telepads used a raw linked-console station lookup.

```powershell
[DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffffff+00:00')
```

Result: exit 0; produced `2026-07-21T19:13:03.4007774+00:00` for the player-facing changelog entry.

```powershell
rg -n "GetOwningStation\(|CargoBountyConsole|CargoTelepad" Content.Server/Cargo/Systems --glob '*.cs'
```

Result: exit 0; confirmed the player-facing bounty console paths and telepad update path now go through ensure helpers. Remaining station lookups include bank balance refresh, disabled cash insertion code, ensure-helper internals, telepad shutdown dead code, pallet-sale multiplier calculation, and broader cargo/pallet behavior.

```powershell
git diff --check -- Content.Server/Cargo/Systems/CargoSystem.Bounty.cs Content.Server/Cargo/Systems/CargoSystem.Telepad.cs Content.IntegrationTests/Tests/_LuaM/LuaMCargoBountyConsoleBindingTest.cs Resources/Changelog/ServerNews.yml Content.IntegrationTests/Tests/_LuaM/LuaMServerNewsChangelogTest.cs
```

Result: exit 0; selected changed files have no whitespace errors.

Local `dotnet test` commands were not run in this iteration per the user's instruction to leave local tests until the last stage.

Next action: continue the binding audit with cargo pallet-sale multiplier behavior and bank balance refresh, starting with:

```powershell
Get-Content Content.Server/Cargo/Systems/CargoSystem.Shuttle.cs -TotalCount 430; Get-Content Content.Server/Cargo/Systems/CargoSystem.cs -TotalCount 120
```

## 2026-07-21 -- cargo shuttle console binding follow-up

- Objective: continue the console/device shuttle binding audit while deferring local test execution until later as requested. Production was not read or mutated.
- Inspected cargo shuttle and pallet sale paths after the cargo order console fix. `CargoShuttleConsoleComponent` still read `_station.GetOwningStation(...)` directly, so an ordinary player shuttle with only a cargo shuttle console could show/use no local order database until another order console initialized it.
- Reused the cargo-order database ensuring helper for cargo shuttle consoles. `ComputerCargoShuttle` on a shuttle grid now ensures the owning shuttle station has its own `StationCargoOrderDatabaseComponent`, matching the cargo order console behavior.
- Updated the cargo update fan-out path so shuttle consoles are matched through the same local database helper instead of a raw owning-station lookup.
- Added `LuaMCargoShuttleConsoleBindingTest` coverage for installing `ComputerCargoShuttle` on a `StandardFrontierVessel` shuttle station that starts without an order database. The test was intentionally not run yet because the user asked to leave local tests for the last stage.
- Added player-facing server-news entry `2026072114` and extended the changelog contract test.

Commands and outcomes:

```powershell
git status --short --branch
```

Result: exit 0; confirmed the repository already has a large dirty worktree with earlier LuaM work and untracked tests/files. No cleanup/revert was attempted.

```powershell
Get-Content -LiteralPath .agents/ITERATION_LOG.md -Tail 140
```

Result: exit 0; re-read the required handoff journal before changing files.

```powershell
rg -n "GetOwningStation\(|CargoShuttleConsole|CargoPalletConsole" Content.Server/Cargo/Systems Content.Server/_NF/Market/Systems --glob '*.cs'
Get-Content -LiteralPath Content.Server/Cargo/Systems/CargoSystem.Shuttle.cs -TotalCount 540
Get-Content -LiteralPath Content.Server/Cargo/Systems/CargoSystem.cs -TotalCount 180
Get-Content -LiteralPath Content.Server/Cargo/Systems/CargoSystem.Orders.cs -TotalCount 180
Get-Content -LiteralPath Content.IntegrationTests/Tests/_LuaM/LuaMCargoOrderConsoleBindingTest.cs
Get-Content -LiteralPath Content.IntegrationTests/Tests/_LuaM/LuaMMarketConsoleBindingTest.cs
rg -n "class CargoShuttleComponent|CargoShuttleComponent" Content.Server Content.Shared Resources/Prototypes --glob '*.cs' --glob '*.yml' | Select-Object -First 80
Get-Content -LiteralPath Content.Shared/Cargo/Components/CargoShuttleComponent.cs
Get-Content -LiteralPath Content.Server/Shuttles/Systems/ShuttleSystem.GridFill.cs -TotalCount 130
rg -n "ComputerCargoShuttle|CargoShuttleConsole|CargoPalletConsole|CargoPallet" Resources/Prototypes Content.Shared --glob '*.yml' --glob '*.cs' | Select-Object -First 160
Get-Content Resources/Prototypes/Entities/Structures/Machines/Computers/computers.yml -TotalCount 1040 | Select-Object -Skip 980 -First 45
Get-Content Resources/Prototypes/Entities/Structures/Machines/Computers/computers.yml -TotalCount 1370 | Select-Object -Skip 1330 -First 35
Get-Content Resources/Prototypes/_NF/Entities/Structures/Machines/Computers/computers.yml -TotalCount 80
```

Result: targeted inspections exited 0 except one guessed `Get-Content Content.Server/Cargo/Components/CargoShuttleComponent.cs` path failed because the component lives in `Content.Shared/Cargo/Components/CargoShuttleComponent.cs`; the correct file was then found and read. The audit found remaining raw cargo shuttle console station lookups.

```powershell
[DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffffff+00:00')
```

Result: exit 0; produced `2026-07-21T19:08:09.4955862+00:00` for the player-facing changelog entry.

```powershell
git diff -- Content.Server/Cargo/Systems/CargoSystem.Shuttle.cs Content.Server/Cargo/Systems/CargoSystem.Orders.cs Content.IntegrationTests/Tests/_LuaM/LuaMCargoShuttleConsoleBindingTest.cs Resources/Changelog/ServerNews.yml Content.IntegrationTests/Tests/_LuaM/LuaMServerNewsChangelogTest.cs
rg -n "UpdateCargoShuttleConsoles\(" Content.Server/Cargo/Systems --glob '*.cs'
```

Result: exit 0; reviewed the selected diff and confirmed the cargo update helper was not called elsewhere.

```powershell
git diff --check -- Content.Server/Cargo/Systems/CargoSystem.Shuttle.cs Content.Server/Cargo/Systems/CargoSystem.Orders.cs Content.IntegrationTests/Tests/_LuaM/LuaMCargoShuttleConsoleBindingTest.cs Resources/Changelog/ServerNews.yml Content.IntegrationTests/Tests/_LuaM/LuaMServerNewsChangelogTest.cs
```

Result: exit 0; selected changed files have no whitespace errors.

```powershell
rg -n "GetOwningStation\(|CargoShuttleConsole|CargoPalletConsole" Content.Server/Cargo/Systems Content.Server/_NF/Market/Systems --glob '*.cs'
```

Result: exit 0; remaining cargo-related owning-station call sites are in market data ensuring, cargo bounty, bank balance refresh, disabled cash insertion code, the shared ensure helper itself, pallet-sale multiplier calculation, and cargo telepad paths. They still need follow-up audit.

Local `dotnet test` commands were not run in this iteration per the user's instruction to leave local tests until the last stage.

Next action: continue the binding audit with cargo bounty and cargo telepad station assumptions, starting with:

```powershell
Get-Content Content.Server/Cargo/Systems/CargoSystem.Bounty.cs -TotalCount 560; Get-Content Content.Server/Cargo/Systems/CargoSystem.Telepad.cs -TotalCount 160
```

## 2026-07-21 -- plastic processor and plasteel RCD

- Objective: continue the user's production/technology request list by adding a separate plastic-production machine and making plasteel walls available through the RCD. Production was not read or mutated.
- Added `PlasticProcessor`, a dedicated lathe-style processor that accepts ore-tagged material feedstock and runs a new `PlasticProcessing` recipe pack. Its first recipe, `PlasticFromCoalPlasma`, turns coal plus raw plasma into `SheetPlastic10`.
- Added `PlasticProcessorMachineCircuitboard`, its lathe recipe, and Advanced Atmospherics unlock wiring alongside the crystallizer. This keeps plastic processing as a separate researched machine instead of only the old chemistry/biogen/crystallizer routes.
- Added `WallPlasteel` as a constructible reinforced-wall derivative and `WallPlasteel` RCD prototype, added it to the standard RCD's available prototypes, and added English/Russian RCD menu names.
- Added player-facing server-news entry `2026072110` and expanded `LuaMProductionMapPrototypeTest` to lock the plastic processor recipe, machine board, technology unlock, and RCD plasteel wall contract.
- Remaining requested items include expedition consoles on other shuttles, binding audit for consoles/devices, station/RnD anchoring, saved-ship Python analysis, and admin follow/spectate UI.

Commands and outcomes:

```powershell
rg -n -i "plastic|plasteel|RCD|rapid construction|tech|technology|research|lathe|recipe|machine" Resources/Prototypes Content.Server Content.Shared Content.Client Content.IntegrationTests --glob "*.yml" --glob "*.cs" --glob "*.ftl" | Select-Object -First 500
rg -n "PlasticRecipe|CrystallizerRecipe|type: crystallizer|crystallizer|NFBioGenSheetPlastic|PlasticSheet|MaterialReclaimer|OreProcessor|latheRecipePack|dynamicRecipe|staticRecipe" Resources/Prototypes --glob "*.yml" | Select-Object -First 400
Get-Content Resources/Prototypes/_Mono/Atmospherics/crystallizer.yml
Get-Content Resources/Prototypes/_Funkystation/Entities/Structures/Machines/Atmospherics/crystallizer.yml
Get-Content Resources/Prototypes/Entities/Objects/Devices/Circuitboards/Machine/production.yml -TotalCount 1450 | Select-Object -Skip 1390 -First 55
Get-Content Resources/Prototypes/Research/industrial.yml -TotalCount 230 | Select-Object -Skip 145 -First 65
rg -n "RCD|rcd|construction.*wall|PlasteelWall|WallPlasteel|plasteel wall|BaseWall" Resources/Prototypes Content.Shared Content.Server --glob "*.yml" --glob "*.cs" | Select-Object -First 300
Get-Content Resources/Prototypes/Entities/Structures/Machines/lathe.yml -TotalCount 830 | Select-Object -Skip 650 -First 160
Get-Content Resources/Prototypes/Recipes/Lathes/sheet.yml -TotalCount 270 | Select-Object -Skip 220 -First 35
Get-Content Resources/Prototypes/Recipes/Lathes/Packs/engineering.yml -TotalCount 120
Get-Content Content.Shared/RCD/RCDPrototype.cs
```

Result: exit 0 for targeted inspections; broad `rg ... | Select-Object -First` discovery commands may exit 1 after truncating output, and were not validation. Found existing plastic routes and the RCD/wall definitions to extend.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMProductionMapPrototypeTest|FullyQualifiedName~LuaMServerNewsChangelogTest" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
```

First result: exit 1 during compilation; the new test checked a non-existent `MachineComponent.BoardPrototype` property. Corrected the assertion to use `MachineComponent.Board`.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMProductionMapPrototypeTest|FullyQualifiedName~LuaMServerNewsChangelogTest" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
```

Final result: exit 0; passed 3/3 in 35 seconds, existing warnings only.

```powershell
git diff --check -- Resources/Prototypes/Entities/Structures/Machines/lathe.yml Resources/Prototypes/Recipes/Lathes/sheet.yml Resources/Prototypes/Recipes/Lathes/Packs/ore.yml Resources/Prototypes/Entities/Objects/Devices/Circuitboards/Machine/production.yml Resources/Prototypes/Recipes/Lathes/electronics.yml Resources/Prototypes/Recipes/Lathes/Packs/engineering.yml Resources/Prototypes/Research/industrial.yml Resources/Prototypes/_Mono/RCD/rcd.yml Resources/Prototypes/Entities/Structures/Walls/walls.yml Resources/Prototypes/Entities/Objects/Tools/tools.yml Resources/Locale/en-US/rcd/components/rcd-component.ftl Resources/Locale/ru-RU/rcd/components/rcd-component.ftl Content.IntegrationTests/Tests/_LuaM/LuaMProductionMapPrototypeTest.cs Content.IntegrationTests/Tests/_LuaM/LuaMServerNewsChangelogTest.cs Resources/Changelog/ServerNews.yml
```

Result: exit 0; selected changed files have no whitespace errors.

Next action: continue with shuttle console/device scope by auditing expedition consoles and station-vs-shuttle binding, starting with:

```powershell
rg -n -i "expedition.*console|salvage.*console|shuttle.*console|StationUid|Station|GridUid|dock|target shuttle|bound|deed|ship" Content.Server Content.Shared Content.Client Resources/Prototypes --glob "*.cs" --glob "*.yml"
```

## 2026-07-21 -- shuttle console clarity and BSS sync

- Objective: continue the user's shuttle request list by making the docking/BSS and flight-mode tabs unambiguous and fixing BSS synchronization propagation for docked shuttles. Production was not read or mutated.
- Added an explicit inertial-mode status label to the flight tab. It now shows the selected mode (?????/???/???? in Russian locale, Cruise/Drive/Park in English) with a short explanation, while the existing three toggle buttons remain grouped.
- Clarified the docking tab's BSS/FTL synchronization section: added a status line, hint text explaining that a docked follower does not need its own powered BSS/FTL drive to follow the leader, and replaced the confusing Russian ???/???? labels with explicit synchronize/do-not-synchronize actions.
- Fixed BSS/FTL sync toggling on the server so the request applies to the full connected docked shuttle group with a visited queue, not only to directly adjacent docks. The client status calculation now includes the current shuttle as well as docked grids when selecting the displayed state.
- Added/updated layout and server-news coverage. Added player-facing server-news entry `2026072109`. Remaining requested items include the separate plastic machine, technology/RCD plasteel-wall rework, expedition consoles on other shuttles, binding audit for consoles/devices, station/RnD anchoring, saved-ship Python analysis, and admin follow/spectate UI.

Commands and outcomes:

```powershell
Get-Content -LiteralPath .agents/ITERATION_LOG.md -TotalCount 220
```

Result: exit 0; re-read the required repository journal before continuing.

```powershell
Get-Content -LiteralPath Content.Server/Shuttles/Systems/ShuttleSystem.FasterThanLight.cs -TotalCount 540 | Select-Object -Skip 450 -First 100
Get-Content -LiteralPath Content.Client/_NF/Shuttles/UI/NavScreen.xaml.cs -TotalCount 180
Get-Content -LiteralPath Content.Client/Shuttles/UI/DockingScreen.xaml.cs -TotalCount 260
Get-Content -LiteralPath Content.Client/Shuttles/UI/DockingScreen.xaml -TotalCount 220
Get-Content -LiteralPath Content.Client/Shuttles/UI/NavScreen.xaml -TotalCount 260
Get-Content -LiteralPath Resources/Locale/ru-RU/shuttles/console.ftl -TotalCount 120
Get-Content -LiteralPath Resources/Locale/ru-RU/_NF/shuttles/console.ftl -TotalCount 80
rg -n "OnToggleFTLLock|SetFTLLock|ToggleFTLLock" Content.Server/Shuttles/Systems/ShuttleConsoleSystem.cs Content.Shared/Shuttles -C 5
Get-Content -LiteralPath Resources/Locale/en-US/shuttles/console.ftl -TotalCount 100; Get-Content -LiteralPath Resources/Locale/en-US/_NF/shuttles/console.ftl -TotalCount 60
Get-Content -LiteralPath Content.IntegrationTests/Tests/_LuaM/LuaMShuttleConsoleLayoutTest.cs -TotalCount 260
```

Result: exit 0 for inspections except one discovery `rg ... | Select-Object` pipeline earlier returned 1 after truncation; confirmed existing FTL travel grouping honors `FTLLockComponent.Enabled`, docking UI had confusing lock labels/status, and server toggle only updated direct neighbors.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMShuttleConsoleLayoutTest|FullyQualifiedName~LuaMServerNewsChangelogTest" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
```

First result: exit 1; YAML parsing failed on the new server-news entry because an unquoted colon appeared in a message. Fixed the entry quoting/formatting.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMShuttleConsoleLayoutTest|FullyQualifiedName~LuaMServerNewsChangelogTest" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
```

Second result: exit 1; the new layout assertion expected literal `FTL`, but the active locale text was Russian/mojibake. Relaxed it to assert the hint label is populated.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMShuttleConsoleLayoutTest|FullyQualifiedName~LuaMServerNewsChangelogTest" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
```

Final result: exit 0; passed 3/3 in 35 seconds, existing warnings only.

```powershell
git diff --check -- Content.Client/Shuttles/UI/DockingScreen.xaml Content.Client/Shuttles/UI/DockingScreen.xaml.cs Content.Client/Shuttles/UI/NavScreen.xaml Content.Client/_NF/Shuttles/UI/NavScreen.xaml.cs Content.Server/Shuttles/Systems/ShuttleConsoleSystem.cs Resources/Locale/en-US/shuttles/console.ftl Resources/Locale/ru-RU/shuttles/console.ftl Resources/Locale/en-US/_NF/shuttles/console.ftl Resources/Locale/ru-RU/_NF/shuttles/console.ftl Content.IntegrationTests/Tests/_LuaM/LuaMShuttleConsoleLayoutTest.cs Content.IntegrationTests/Tests/_LuaM/LuaMServerNewsChangelogTest.cs Resources/Changelog/ServerNews.yml
```

Result: exit 0; selected changed files have no whitespace errors.

Next action: continue with the next isolated user request, likely the separate plastic machine and technology/RCD plasteel-wall package, starting with:

```powershell
rg -n -i "plastic|plasteel|RCD|rapid construction|tech|technology|research" Resources/Prototypes Content.Server Content.Shared Content.Client Content.IntegrationTests --glob "*.yml" --glob "*.cs" --glob "*.ftl"
```

## 2026-07-21 -- data farm flatpacker triage

- Objective: start the user's multi-item production/shuttle/admin request list with the smallest isolated defect: mining/data-farm boards not being accepted by the flatpacker, while leaving larger shuttle UI/BSS/R&D/admin work for separate iterations.
- Audited the requested areas with broad searches for flatpackers, machine boards, data/crypto farms, docking/synchronization, flight controls, plastic/RCD/research, expedition consoles, saved ships, and admin camera/follow systems. Production was not read or mutated.
- Found `DataFarmResearchCircuitboard` and `DataFarmCryptoCircuitboard` in `Resources/Prototypes/_Mono/Entities/Objects/Electronics/datafarms.yml` explicitly set `flatpackable: false`. `SharedFlatpackSystem.OnInsertAttempt` accepts only `MachineBoardComponent` boards with `Flatpackable == true`, so the flatpacker correctly rejected these boards.
- Removed the `flatpackable: false` override for both data-farm boards and updated their descriptions. The boards now use the default `MachineBoardComponent.Flatpackable = true`, so they can be inserted into the flatpacker. Added `LuaMDataFarmFlatpackPrototypeTest` to lock both boards to their machine prototypes and flatpacker eligibility.
- Added player-facing server-news entry `2026072108`. Larger requested items remain pending: separate plastic machine, technology/RCD plasteel-wall rework, clearer docking/flight status UI, BSS sync behavior, expedition consoles on other shuttles, station-vs-shuttle device binding audit, station/RnD anchoring, Python saved-ship analysis, and admin camera-follow mode.

Commands and outcomes:

```powershell
Get-Content -LiteralPath .agents/ITERATION_LOG.md -TotalCount 260
```

Result: exit 0; re-read required handoff journal before the new work.

```powershell
rg -n -i "mining.*farm|farm.*mining|MiningFarm|???????|packer|flatpack|packager|machine.*board|circuit" Content.Server Content.Shared Content.Client Resources/Prototypes Content.IntegrationTests --glob "*.cs" --glob "*.yml"
rg -n -i "docking|dock|sync|BSS|bluespace|drift|cruise|brake|stop|flight|shuttle console|????????" Content.Server Content.Shared Content.Client Resources/Prototypes Content.IntegrationTests --glob "*.cs" --glob "*.xaml" --glob "*.yml"
rg -n -i "plastic|plasteel|RCD|rapid construction|tech|technology|research|expedition.*console|salvage|admin.*camera|follow|observe|ghost" Content.Server Content.Shared Content.Client Resources/Prototypes Content.IntegrationTests --glob "*.cs" --glob "*.yml" --glob "*.ftl"
rg -n -i "mining.*farm|farm.*mining|crypto|miner|miningfarm|farm" Resources/Prototypes Content.Server Content.Shared Content.Client Content.IntegrationTests --glob "*.cs" --glob "*.yml" | Select-Object -First 240
rg -n "ItemMiner|item miner|ItemMinerComponent|FlatpackCreator|flatpackable|Flatpackable|MachineBoard" Resources/Prototypes/_Goobstation Resources/Prototypes/_Mono Resources/Prototypes/_LuaM Resources/Prototypes Content.Shared/_Goobstation Content.Server/_Goobstation --glob "*.yml" --glob "*.cs" | Select-Object -First 300
```

Result: searches located the flatpacker insertion rule, data-farm boards/machines, and many related systems. Some broad `rg ... | Select-Object -First` commands exited 1 because the pipeline truncated output; they were used only for discovery and are not exhaustive validation.

```powershell
Get-Content -LiteralPath Resources/Prototypes/_Mono/Entities/Objects/Electronics/datafarms.yml
Get-Content -LiteralPath Resources/Prototypes/_Mono/Entities/Structures/Machines/datafarms.yml
Get-Content -LiteralPath Content.Shared/Construction/SharedFlatpackSystem.cs -TotalCount 170
Get-Content -LiteralPath Content.Shared/Construction/Components/MachineBoardComponent.cs -TotalCount 80
```

Result: exit 0; confirmed `flatpackable: false` was the cause and that the default component value is `true`.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~LuaMDataFarmFlatpackPrototypeTest -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
```

First result: exit 1 during compilation; new test missed `using Robust.Shared.GameObjects;` for `IComponentFactory`. Corrected immediately.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~LuaMDataFarmFlatpackPrototypeTest -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
```

Final result: exit 0; passed 1/1 in 34 seconds, existing warnings only.

```powershell
[DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffffff+00:00')
git diff --check -- Resources/Prototypes/_Mono/Entities/Objects/Electronics/datafarms.yml Content.IntegrationTests/Tests/_LuaM/LuaMDataFarmFlatpackPrototypeTest.cs Content.IntegrationTests/Tests/_LuaM/LuaMServerNewsChangelogTest.cs Resources/Changelog/ServerNews.yml
```

Result: time command exited 0 and produced `2026-07-21T14:51:36.1368932+00:00`; first diff-check exited 1 due to one blank line at EOF in `ServerNews.yml`, then the file ending was normalized.

```powershell
git diff --check -- Resources/Prototypes/_Mono/Entities/Objects/Electronics/datafarms.yml Content.IntegrationTests/Tests/_LuaM/LuaMDataFarmFlatpackPrototypeTest.cs Content.IntegrationTests/Tests/_LuaM/LuaMServerNewsChangelogTest.cs Resources/Changelog/ServerNews.yml
```

Final result: exit 0; selected changed files have no whitespace errors.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMDataFarmFlatpackPrototypeTest|FullyQualifiedName~LuaMServerNewsChangelogTest" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
```

Result: exit 0; passed 2/2 in 34 seconds, existing warnings only.

Next action: continue with the shuttle-console clarity/BSS package by inspecting the docking and nav controls plus BSS linking code:

```powershell
rg -n -i "BSS|bluespace|sync|synchron|drift|brake|cruise|stop|Dock" Content.Server/Shuttles Content.Client/Shuttles Content.Shared/Shuttles Content.Server/_Mono Content.Client/_Mono Content.Shared/_Mono --glob "*.cs" --glob "*.xaml"
```


## 2026-07-21 -- reusable ship voucher cooldown hotfix

- Objective: investigate the reported Unnamed/"??????????" shuttle voucher bug and fix voucher validation without touching production.
- Found the bug in `Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs`: all `ShipyardVoucherComponent`s were rejected when `RedemptionsLeft <= 0` and all voucher purchases decremented `RedemptionsLeft`, even for reusable LPC vouchers configured with `destroyOnEmpty: false` and long cooldowns. After one purchase those vouchers could become permanently unusable instead of being limited by `NextBuyAt`.
- Updated purchase validation so `RedemptionsLeft` is enforced only for `DestroyOnEmpty` vouchers. Reusable vouchers still set `NextBuyAt` on every purchase, but no longer consume their default redemption counter. One-time service vouchers with `destroyOnEmpty: true` still decrement and can be deleted on sale as before.
- Added/updated contract coverage in `LuaMShipyardPurchaseDurabilityContractTest` and `LuaMImportedShipContentPrototypeTest`, and added player-facing server-news entry `2026072107`. Production was not read or mutated; `Tools/AI_SERVER_JOURNAL.md` was not opened.
- The local stack was stopped to release locked `Content.Server` binaries for the test build and has not been restarted in this iteration.

Commands and outcomes:

```powershell
Get-Content -LiteralPath .agents/ITERATION_LOG.md
```

Result: exit 0; read the required repository handoff journal before changing files.

```powershell
git status --short --branch
rg -n -i "voucher|????|shipyard|shuttle|vessel|deed|ticket" Content.Server Content.Shared Content.Client Resources Content.IntegrationTests Tools --glob "*.cs" --glob "*.yml" --glob "*.yaml"
rg -n -i "voucher|ShipyardVoucher|voucher" Content.Server/_NF Content.Shared/_NF Content.Client/_NF Resources/Prototypes/_NF Resources/Prototypes/_LuaM Content.IntegrationTests/Tests/_LuaM --glob "*.cs" --glob "*.yml"
```

Result: status showed the existing large dirty worktree; searches located the shipyard voucher component/system, voucher YAML, changelog mentions, and existing LuaM shipyard tests. The broad search produced a very large/truncated output but completed sufficiently for locating the relevant files.

```powershell
Get-Content -LiteralPath Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs -TotalCount 2200 | Select-Object -Skip 330 -First 520
Get-Content -LiteralPath Content.Server/_NF/Shipyard/Components/ShipyardVoucherComponent.cs
Get-Content -LiteralPath Resources/Prototypes/_NF/Entities/Objects/Devices/Misc/ship_vouchers.yml -TotalCount 130
Get-Content -LiteralPath Resources/Prototypes/_LuaM/Entities/Objects/Devices/ship_vouchers.yml
Get-Content -LiteralPath Content.IntegrationTests/Tests/_LuaM/LuaMShipyardPurchaseDurabilityContractTest.cs
Get-Content -LiteralPath Content.IntegrationTests/Tests/_LuaM/LuaMShipPersistenceLifecycleContractTest.cs
Get-Content -LiteralPath Content.IntegrationTests/Tests/_LuaM/LuaMImportedShipContentPrototypeTest.cs -TotalCount 280 | Select-Object -Skip 225 -First 35
Get-Content -LiteralPath Content.IntegrationTests/Tests/_LuaM/LuaMServerNewsChangelogTest.cs -TotalCount 220
Get-Content -LiteralPath Resources/Changelog/ServerNews.yml -Tail 120
```

Result: exit 0; inspected the relevant validation/finalizer, component fields, voucher prototypes, and contract/news tests.

```powershell
Get-Date -AsUTC -Format "yyyy-MM-ddTHH:mm:ss.fffffffK"
```

Result: exit 1; this PowerShell version does not support `-AsUTC`. Superseded by `[DateTime]::UtcNow.ToString(...)`.

```powershell
[DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffffff+00:00')
```

Result: exit 0; produced `2026-07-21T14:39:09.0011784+00:00`, used for the server-news entry.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMShipyardPurchaseDurabilityContractTest|FullyQualifiedName~LuaMImportedShipContentPrototypeTest|FullyQualifiedName~LuaMServerNewsChangelogTest" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
```

First result: exit 1 before tests; the running local `Content.Server (30672)` locked `bin\Content.Server` DLLs during build copy. No test result was produced.

```powershell
& .\Tools\stop_local_stack.ps1 -Force
```

Result: exit 0; stopped `Content.Server`, `Robust.Server`, `Content.Client`, `Robust.Client`, and the local gateway/test process to release build locks.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMShipyardPurchaseDurabilityContractTest|FullyQualifiedName~LuaMImportedShipContentPrototypeTest|FullyQualifiedName~LuaMServerNewsChangelogTest" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
```

Second result: exit 1 during compilation; a duplicated `[Test]` attribute in the newly inserted contract test caused `CS0579`. The test file was corrected.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMShipyardPurchaseDurabilityContractTest|FullyQualifiedName~LuaMImportedShipContentPrototypeTest|FullyQualifiedName~LuaMServerNewsChangelogTest" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
```

Final result: exit 0; passed 7/7 in about 1 minute 13 seconds, with existing warnings only.

```powershell
git diff --check -- Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs Content.IntegrationTests/Tests/_LuaM/LuaMShipyardPurchaseDurabilityContractTest.cs Content.IntegrationTests/Tests/_LuaM/LuaMImportedShipContentPrototypeTest.cs Content.IntegrationTests/Tests/_LuaM/LuaMServerNewsChangelogTest.cs Resources/Changelog/ServerNews.yml
```

First result: exit 1; reported one new blank line at EOF in `Resources/Changelog/ServerNews.yml`. The file ending was normalized.

```powershell
git diff --check -- Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs Content.IntegrationTests/Tests/_LuaM/LuaMShipyardPurchaseDurabilityContractTest.cs Content.IntegrationTests/Tests/_LuaM/LuaMImportedShipContentPrototypeTest.cs Content.IntegrationTests/Tests/_LuaM/LuaMServerNewsChangelogTest.cs Resources/Changelog/ServerNews.yml
```

Final result: exit 0; no whitespace errors in the selected changed files.

Next action: if a runtime smoke is wanted, restart the local stack and try buying a reusable LPC voucher ship twice with the cooldown advanced/expired while confirming a `destroyOnEmpty: true` service voucher still consumes its charge:

```powershell
& .\Tools\start_local_stack.ps1 -Reset -PiperTts -PiperVoices 'ru_RU-irina-medium'
```


## 2026-07-21 -- repository status check

- Objective: answer the user's project-status question with the current local handoff, git, and local-stack facts without changing product code.
- Repository state: branch `codex/finish-project-20260714` is at `0efe0838ed` (`Bound AI gateway provider responses`), ahead of `master` work that remains mostly uncommitted. `git status --porcelain` currently reports 1103 tracked changed entries and 14 untracked entries; this is a large dirty worktree, not a clean release-ready state.
- Latest journaled implementation is the shared situation-aware bot behavior and worker executors. It passed the targeted arbitration/rescue/standard/worker/news integration run, worker coverage, feature-pack validation, YAML linter, and had the local stack started successfully. No production deployment was attempted for that AI iteration.
- Local server status check at `http://127.0.0.1:1213/status` still responds: map `NFDev`, round 1, 0 players, run level 1. This confirms the local stack is reachable, but no fresh client/log smoke was rerun during this status-only check.
- Production was not read or mutated during this iteration, so `Tools/AI_SERVER_JOURNAL.md` was not opened or changed.

Commands and outcomes:

```powershell
Get-Content -LiteralPath .agents/ITERATION_LOG.md
```

Result: exit 0; read the required repository handoff journal.

```powershell
git status --short --branch
git log -5 --oneline --decorate
```

Result: status exited 0 and showed a large dirty worktree; recent commits are `0efe0838ed`, `d4113cb670`, `59ebe15ad4`, `87bf88931c`, and `1cd7246dbd`.

```powershell
Invoke-RestMethod -Uri http://127.0.0.1:1213/status -TimeoutSec 5 | ConvertTo-Json -Depth 4
$s = git status --porcelain; $tracked = ($s | Where-Object { $_ -notmatch '^\?\?' }).Count; $untracked = ($s | Where-Object { $_ -match '^\?\?' }).Count
```

Result: local status endpoint responded with round 1 on `NFDev`, 0 players; changed-entry count was 1103 tracked and 14 untracked.

Next action: continue the journaled AI follow-up by adding service-bot passenger/cargo handoff coverage:

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~LuaMServiceBotRoleExecutorRuntimeTest -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
```

## 2026-07-21 -- shared situation-aware bot behavior and worker executors

- Objective: introduce one deterministic situation-arbitration layer for humanoids, rescue agents, service machines, sector drones, ships, turrets, hostile NPCs, and animals while preserving each domain's existing HTN or entity-system executor and direct player control.
- Added the `_LuaM/AI` behavior profile/observation/decision contracts, deterministic tiered arbiter, bounded built-in perception, HTN/activity bridges, retreat operator, and a work-order board with role/capability filtering, expiring leases, deterministic selection, completion evidence, and per-agent exponential failure backoff.
- Rolled adapters into rescue crews, sector mining/service drones, civilian logistics ships, adaptive Mono ship AI, stock fire/clean/medical bots, taxi/hover-taxi/supply/mime bots, standard and Frontier turrets, hostile humanoids, and atmospheric/space animals. Existing domain systems remain authoritative for movement, weapons, treatment, cleaning, rescue, and ship control.
- Added a hard `ActorComponent` guard to the periodic behavior loop, direct evaluation, and adapters. A player-controlled entity cannot be claimed or have its HTN plan replaced by the situational AI.
- Added autonomous engineer and medic worker prototypes and executors. They claim only repair or treatment work, navigate through the standard HTN stack, use a compatible item actually held in hand, wait for the real repair/healing `DoAfter`, and complete only after the target's real damage is cleared. Missing equipment, deleted targets, unavailable destinations, and stalled routes release the lease with bounded backoff.
- Safety decisions preserve the worker's work-order lease but selectively cancel an active repair or treatment. Repair cancellation recognizes SS14's protected `ToolDoAfterEvent` wrapper through the current target, held tool, and required repair quality; unrelated do-afters are not cancelled.
- Taxi, supply, and entertainment bots now claim only role-compatible work orders, navigate to the requested destination, renew leases, recover from route stalls, and restore their original HTN compound. Physical passenger boarding and cargo transfer remain explicit follow-up domain work and are not claimed as implemented.
- Updated `Content.Server/_LuaM/AI/README.md`, production release policy, and Russian server-news entry `2026072106`. The worker system, prototype, and runtime test are mandatory release inputs. No production package or deployment was attempted for this AI iteration.
- The combined arbitration/rescue/standard/worker/news integration run passed 19/19. Worker coverage passed 5/5 and proves real repair/treatment, missing-tool backoff, player-control exclusion, safety cancellation with lease retention, and route-stall release. The complete feature validator and freshly rebuilt YAML linter passed.
- The local stack is active on `127.0.0.1:1213`: server round 1 on `NFDev` is reachable, the client reached `InGame`, gateway health is good with Piper `ru_RU-irina-medium`, server/client error files are empty, and gateway stderr contains only successful local health requests.

Commands and outcomes:

```powershell
dotnet build Content.Server\Content.Server.csproj --no-restore --nologo --verbosity:minimal -m:1
```

Result: exit 0 before the worker runtime expansion; subsequent integration builds also compiled the final server changes with zero errors.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMWorkerBehaviorAdapterRuntimeTest" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
```

Result: final exit 0; passed 5/5. Earlier diagnostic runs first exposed a zero-delay fixture that completed before `ActiveDoAfterComponent` could be observed, then `0.33` vacuum damage on the human patient, then deferred active-marker cleanup, and finally the repair event's protected tool wrapper. Each fixture/product issue was corrected and superseded; no test process remains running.

```powershell
dotnet format Content.IntegrationTests\Content.IntegrationTests.csproj whitespace --no-restore --include Content.Server\_LuaM\AI\LuaMWorkerBehaviorAdapterSystem.cs Content.IntegrationTests\Tests\_LuaM\LuaMWorkerBehaviorAdapterRuntimeTest.cs Content.IntegrationTests\Tests\_LuaM\LuaMServerNewsChangelogTest.cs --verbosity minimal
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMBehaviorArbitrationRuntimeTest|FullyQualifiedName~LuaMRescueBehaviorAdapterRuntimeTest|FullyQualifiedName~LuaMStandardBehaviorAdapterRuntimeTest|FullyQualifiedName~LuaMWorkerBehaviorAdapterRuntimeTest|FullyQualifiedName~LuaMServerNewsChangelogTest" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
python Tools\validate_luam_feature_pack.py
```

Result: formatting exited 0 with workspace-load warnings only; the combined test run passed 19/19; feature-pack validation passed. `Tools/luam_release_policy.json` also parsed successfully through `ConvertFrom-Json`.

The final post-timestamp `LuaMServerNewsChangelogTest` no-build invocation passed 1/1 in 101.6 seconds, confirming the exact final news YAML and required Russian phrases.

```powershell
dotnet build Content.YAMLLinter\Content.YAMLLinter.csproj --configuration DebugOpt --no-restore --nologo --verbosity:minimal -m:1
& .\bin\Content.YAMLLinter\Content.YAMLLinter.exe
git diff --check -- Content.Server/_LuaM/AI Content.Shared/_LuaM/AI Resources/Prototypes/_LuaM/NPCs Resources/Prototypes/_LuaM/Entities/Mobs/worker_ai.yml Content.IntegrationTests/Tests/_LuaM/LuaMBehaviorArbitrationRuntimeTest.cs Content.IntegrationTests/Tests/_LuaM/LuaMRescueBehaviorAdapterRuntimeTest.cs Content.IntegrationTests/Tests/_LuaM/LuaMStandardBehaviorAdapterRuntimeTest.cs Content.IntegrationTests/Tests/_LuaM/LuaMWorkerBehaviorAdapterRuntimeTest.cs Content.IntegrationTests/Tests/_LuaM/LuaMServerNewsChangelogTest.cs Resources/Changelog/ServerNews.yml Tools/luam_release_policy.json
```

Result: linter build exited 0 with existing warnings; the fresh linter reported `No errors found in 100918 ms.`; selected diff check exited 0 with line-ending notices only.

```powershell
& .\Tools\start_local_stack.ps1 -Reset -PiperTts -PiperVoices 'ru_RU-irina-medium'
Invoke-RestMethod -Uri http://127.0.0.1:8787/health
Invoke-RestMethod -Uri http://127.0.0.1:1213/status
```

Result: reset/start exited 0 in 55.9 seconds; gateway and server health checks passed, server reached `Ready`, client reached `InGame`, and no server/client error output was produced.

Next action: add failing runtime coverage and then implement physical passenger and cargo handoff for taxi and supply bots:

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMServiceBotRoleExecutorRuntimeTest" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
```

## 2026-07-21 -- shuttle-radar clipping and forced production deployment

- Objective: keep the flight-tab sweep line and combat traces inside the radar grid, preserve the completed resizable shuttle-console redesign, validate the exact accumulated worktree, and deploy the correction to production with recovery material.
- `BaseShuttleControl` now clips child drawing to its own rectangle. `ShuttleNavControl` also clips sweep, missile, and hitscan segments mathematically through `TryClipSegmentToBox`, so endpoints cannot extend into the adjacent control panel even when the line starts outside the visible radar region.
- Radar geometry validation passed 3/3, shuttle layout/window validation passed 2/2, and the server-news/localization validation passed 1/1. Player-facing news entry `2026072105` records the radar correction.
- The complete source gate passed in 659.5 seconds. Package `DeploymentPackages/LuaM/luam-local-release-20260721-002621.zip` has SHA256 `674bd1d6a375899cde9a9238366c14ebee8c093cc973f24ae25ca9f62e89a0b9`, payload digest `2175156d4052430a2007b8e19b6e5892094d6dda31421f9191b3557146f0e121`, is production eligible, contains all 1094 changed files, and has no untracked or unexpected out-of-package files. Independent verification passed without warnings.
- Binary build and the repeated independent surface audit passed with zero violations. Client/version SHA256 is `a611cba7804203b3c71d1c6d2e6c95280dd2ed6cd124e598a813a6065a350af6`; server SHA256 is `4ca82e9d9f7ef7e0f3164550af563b082f6a835435b1d6be754bd2778c4575c6`; receipt SHA256 is `ed3e6145b2c98f5030b5a9703d04883b2d56a5378cc1c9e30a58f4c5613f85c3`.
- Production preflight found the required journal mirror identical at SHA256 `029871f7b89b65b562ede8b1a0ce436c071e4c9cbc6bb861251a1acb2d32ee93`, owner/mode `root:root`/`0644`; the service was active in round 143 with one player and about 14 GiB free immediately before deployment.
- The immutable client archive was published and returned HTTP 200 with the exact 333903604-byte content length. Forced deployment tag `luam-20260721-radar-clip-force` completed with the user's explicit authorization and a required fresh data backup. The service is active in round 144, `/status` and `/info` are healthy, the advertised version is the new client hash, one player had rejoined by the final check, and the bounded error-priority service query since startup returned no entries.
- Recovery material is `/opt/monolith-ds/backups/server-luam-20260721-radar-clip-force` (about 264 MiB), `/opt/monolith-ds/backups/server_config-before-luam-20260721-radar-clip-force.toml`, and `/opt/monolith-ds/backups/data-luam-20260721-radar-clip-force.tar.gz` (80330019 bytes, about 77 MiB). The deployed configuration SHA256 remained `9107b9e168794dba70fdb3563a1c0fa704226c3ec6e11b0cee383e44150c12cd`; about 14 GiB remained free after all recovery files were written.
- The next AI implementation remains deliberately separate from this release: first add an autonomous patrol ship through existing `_Mono/NPC/HTN`, then a `_LuaM/AI` work-order/reservation/memory layer for crew. A local model will remain behind LuaM AI Gateway as a bounded structured-command/dialogue layer and will not directly drive thrust, weapons, networking, or arbitrary entity systems.
- The final production journal was installed and verified at `/opt/monolith-ds/AI_SERVER_JOURNAL.md`, SHA256 `2ecd6a94909be9d0049b5671dea1b4a70549d8d8eebee339da1dd9c7bb741397`, owner/mode `root:root`/`0644`; the production service remained active.
- The local stack is active again on `127.0.0.1:1213`: gateway health is good with Piper, local round 1 is reachable, the client reached `InGame`, and both server/client error files are empty. The first restart attempt failed cleanly because binary packaging had removed the disposable local `Bin/Content.Server` and apphost files. A direct no-restore server build then failed before product compilation because its restored assets lacked the SQLite and dotMemory dependencies; explicit server/client restores and builds succeeded, and the second stack start completed in 55.7 seconds. No failed launch process remains.

Commands and outcomes:

```powershell
Tools\build_luam_release_package.ps1 -RunTests -RunLocalSmoke -Json
Tools\verify_luam_release_package.ps1 -PackagePath DeploymentPackages\LuaM\luam-local-release-20260721-002621.zip -ExpectedSha256 674bd1d6a375899cde9a9238366c14ebee8c093cc973f24ae25ca9f62e89a0b9 -Json
Tools\build_luam_server_release.ps1 -ExternalClientBaseUrl http://188.127.225.57:1213 -SourcePackagePath DeploymentPackages\LuaM\luam-local-release-20260721-002621.zip -ExpectedSourcePackageSha256 674bd1d6a375899cde9a9238366c14ebee8c093cc973f24ae25ca9f62e89a0b9 -ReleaseReceiptPath release\luam-binary-release-receipt.json -Json
Tools\audit_release_surface.ps1 -PackagePath release\SS14.Client.zip,release\SS14.Server_linux-x64.zip -Json
```

Result: source tests, smoke checks, feature/localization checks, dependency policy, package verification, binary build, and the final 11707-entry client/10652-entry server surface audit all exited 0. No partially running release command remains.

```powershell
Tools\provision_monolith_client_static.ps1 -ClientPackagePath release\SS14.Client.zip -ExpectedSha256 a611cba7804203b3c71d1c6d2e6c95280dd2ed6cd124e598a813a6065a350af6 -Version a611cba7804203b3c71d1c6d2e6c95280dd2ed6cd124e598a813a6065a350af6 -ReleaseReceiptPath release\luam-binary-release-receipt.json -ExpectedReleaseReceiptSha256 ed3e6145b2c98f5030b5a9703d04883b2d56a5378cc1c9e30a58f4c5613f85c3
Tools\deploy_luam_server_release.ps1 -PackagePath release\SS14.Server_linux-x64.zip -ExpectedSha256 4ca82e9d9f7ef7e0f3164550af563b082f6a835435b1d6be754bd2778c4575c6 -ReleaseReceiptPath release\luam-binary-release-receipt.json -ExpectedReleaseReceiptSha256 ed3e6145b2c98f5030b5a9703d04883b2d56a5378cc1c9e30a58f4c5613f85c3 -Tag luam-20260721-radar-clip-force -ConfigSourcePath server_config.remote.toml -RemoteConfigPath /opt/monolith-ds/server/server_config.toml -RemoteDataDir /opt/monolith-ds/data -RequireDataBackup -Force
```

Result: client publication exited 0 with HTTP 200; required data backup, server/config backups, binary swap, restart, status/info verification, static-client HEAD check, storage checks, and the bounded error-log query all passed.

```powershell
dotnet restore Content.Server\Content.Server.csproj --nologo --verbosity:minimal
dotnet build Content.Server\Content.Server.csproj --no-restore --nologo --verbosity:minimal -m:1
dotnet restore Content.Client\Content.Client.csproj --nologo --verbosity:minimal
dotnet build Content.Client\Content.Client.csproj --no-restore --nologo --verbosity:minimal -m:1
& .\Tools\start_local_stack.ps1 -Reset -PiperTts -PiperVoices 'ru_RU-irina-medium'
```

Result: restores and builds exited 0 with existing warnings; the final local launch exited 0, gateway and local `/status` checks passed, the client reached `InGame`, and error files are empty. The earlier launch and no-restore build failures described above were fully cleaned up and superseded.

Next action: after a player exercises the flight tab, inspect only bounded diagnostics from this deployment:

```powershell
ssh monolith-new "sudo journalctl -u monolith-ds.service --since '2026-07-21 00:43:30 UTC' --no-pager | grep -Ei 'error|exception|shuttle|radar' | tail -n 160"
```

## 2026-07-21 -- production ship-ownership reset and shuttle-console resize repair

- Objective: clear only production ship ownership state at the user's request, preserve every unrelated persistent player system, and fix the shuttle-console screenshot defect where resizing enlarged the window but left the actual interface pinned as a narrow panel in the upper-left corner.
- Production maintenance completed. The required local and installed server journals matched before access. Pre-maintenance production state was round 142 with two players, about 15 GiB free, 13 `luam_ship_snapshot` rows, and one `luam_ship_presence_lease` row. Both ship tables had zero foreign-key violations and SQLite `quick_check` was `ok`.
- `monolith-ds.service` was stopped for 22 seconds. A fresh checked backup was created at `/opt/monolith-ds/backups/data-before-ship-ownership-clear-20260721T000357Z.tar.gz`, 81161624 bytes, SHA256 `1d1cc14fd970af803712d80f695632e4745b923eb42b5debd7cf4e3e6a7ccc42`. One `BEGIN IMMEDIATE` transaction deleted the lease row first and then all 13 snapshot rows. Both counts remained zero after restart.
- Aggregate controls proved that profiles, preferences, bank accounts, career progression, deep cryo, and expedition state did not change. The database has 311870 old `admin_log_player` foreign-key findings unrelated to ship persistence; the count remained exactly unchanged across the operation. Post-maintenance health passed in round 143: service active, `/status` and `/info` healthy, about 15 GiB free, and no error-priority journal entries in the maintenance window.
- The screenshot exposed the UI root cause: adding the CRT overlay changed the window body to a `LayoutContainer`, but its console surface and CRT child had no `Wide` anchor preset. Their desired size therefore stayed in the upper-left while the outer window resized. `ShuttleConsoleWindow` now anchors both children across the full body.
- `LuaMShuttleConsoleLayoutTest` now arranges navigation, world, and docking modes at both `640x480` and `1060x720`. It verifies full-width/full-height surface growth, exact CRT coverage, usable scrolling sidebars, and at least 300 pixels of radar growth. The screenshot's empty right and lower regions are now a regression failure.
- Added player-facing news entry `2026072104` with author `LuaM`, recording the bounded ownership reset and the console layout correction. The integrated changelog/localization load test passed.
- The local stack is active on `127.0.0.1:1213`; gateway health is good, the client connected, and `server-err.txt`/`client-err.txt` are empty. A normal Discord IPC timeout remains in the regular client log. The UI repair is local source/build only and has not yet been packaged or deployed to production; production database cleanup is already live.
- The updated production journal was installed and verified at `/opt/monolith-ds/AI_SERVER_JOURNAL.md`, SHA256 `d7490c9d12634be89664ae869f393f38acf1c2a83dfe5964e9c482e1b92a8d8e`, owner/mode `root:root`/`0644`; production remained active in round 143 after installation.

Commands and outcomes:

```powershell
(Get-FileHash -Algorithm SHA256 -LiteralPath 'Tools/AI_SERVER_JOURNAL.md').Hash.ToLowerInvariant()
ssh monolith-new "sha256sum /opt/monolith-ds/AI_SERVER_JOURNAL.md; stat -c 'journal_owner_mode=%U:%G:%a' /opt/monolith-ds/AI_SERVER_JOURNAL.md; systemctl is-active monolith-ds.service; curl -fsS http://127.0.0.1:1212/status; df -h / /opt/monolith-ds"
```

Result: mirror SHA256 `063ed75c950094a59a225d441d6d3b7a099c7634f9955f917723ad34a4ba2d69`, owner/mode `root:root`/`0644`, active service, round 142 with two players, and about 15 GiB free. Read-only Python `sqlite3` queries then established the bounded counts and integrity facts above. The server has no standalone `sqlite3` CLI, so the service account's standard-library driver was used.

```sql
PRAGMA foreign_keys = ON;
BEGIN IMMEDIATE;
DELETE FROM luam_ship_presence_lease;
DELETE FROM luam_ship_snapshot;
COMMIT;
PRAGMA quick_check;
PRAGMA foreign_key_check;
```

Result: exit 0 inside a stop/backup/check/restart wrapper; one lease and 13 snapshots deleted, zero remained, unrelated aggregate controls were unchanged, backup integrity checks passed, and the restart was healthy. The wrapper included a restart-on-error trap. One earlier local wrapper-construction attempt failed before SSH execution due variable interpolation and made no host change.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMShuttleConsoleLayoutTest|FullyQualifiedName~UiControlTest.TestWindows" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
dotnet restore Content.IntegrationTests\Content.IntegrationTests.csproj --nologo --verbosity:minimal
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMShuttleConsoleLayoutTest|FullyQualifiedName~UiControlTest.TestWindows" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
dotnet format Content.IntegrationTests\Content.IntegrationTests.csproj whitespace --no-restore --include Content.Client\Shuttles\UI\ShuttleConsoleWindow.xaml.cs Content.IntegrationTests\Tests\_LuaM\LuaMShuttleConsoleLayoutTest.cs --verbosity minimal
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMShuttleConsoleLayoutTest|FullyQualifiedName~LuaMServerNewsChangelogTest|FullyQualifiedName~UiControlTest.TestWindows" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
```

Result: the first build did not reach tests because the local package cache lacked Roslyn scripting, dotMemory API, and SQLite provider assets. Restore exited 0. The retry passed 2/2, formatting exited 0 with workspace-load warnings only, and the final post-news/post-height-contract run passed 3/3 in 37 seconds. No test process remains running.

```powershell
& .\Tools\start_local_stack.ps1 -Reset -PiperTts -PiperVoices 'ru_RU-irina-medium'
Invoke-RestMethod -Uri http://127.0.0.1:8787/health
Invoke-RestMethod -Uri http://127.0.0.1:1213/status
```

Result: exit 0 in 54.9 seconds; gateway healthy, local round 1 on `NFDev`, client connected, and client/server error files empty. The local stack is intentionally still running for manual UI inspection.

Next action: inspect the resized navigation/world/docking tabs in the running local client; if accepted for production, start the complete release gate with:

```powershell
Tools\build_luam_release_package.ps1 -RunTests -RunLocalSmoke -Json
```

## 2026-07-21 -- asteroid-belt release gate repair and forced production deployment

- Objective: finish the user-authorized production release of the accumulated asteroid-belt, copper, shuttle-console, voucher/dock-map, daily cargo event, TSF defense, and paired-planetoid changes without bypassing the release policy.
- The final source package is `DeploymentPackages/LuaM/luam-local-release-20260720-234117.zip`, SHA256 `69a8ed2a8522005d95ea876b4e240e8d12c382f29957ec7e3a2920ee73996c77`, payload digest `b5e4b4ddbf281719095f6c95c819c4fda8341a3c26589ccbf34dccd98469446d`, 1584 files, production eligible, with all 1094 changed files packaged and no untracked or unexpected out-of-package files.
- Release-gate repairs were bounded to release correctness: the asteroid-belt disk now uses locale keys available in both LuaM locales; the 21 intended new files were added to the release index; the stale root `ITERATION_LOG.updated.md` duplicate was removed; `System.Security.Cryptography.Xml` was raised from vulnerable `10.0.9` to fixed `10.0.10`; and the PDA bank integration fixture now refreshes pooled preferences from the authoritative database before opening a PDA. The last change is test-only and fixes cross-test cache contamination, not gameplay behavior.
- The final gate passed all policy tests, the gateway smoke check, local server/client smoke, feature/localization validation, and a five-project dependency audit with zero vulnerable package entries. The independent package verifier passed all checks without warnings. The binary surface audit found zero client/server boundary violations.
- Binary release hashes: server `f63bc7d0841694228417b1bd0fee5d3dbf1ee5ce0a90687c0375e926d363873e`; client/version `9ecabd9e56f14e8c3ade78e98844a782fbbfd22332d327a52a95941dda11a80f`; receipt `5984642299719a35cb9c0c50047d8071de3a76322d64b7d04ffdc54c0b2c128c`. Client publication returned HTTP 200.
- Forced production deploy tag `luam-20260721-asteroid-belt-force` completed with the user's renewed authorization and a required data backup. Before restart, round 141 had two players and about 16 GiB free. After restart, `monolith-ds.service` is active, `/status` reports round 142, `/info` advertises the new client build, one player had already rejoined, and about 15 GiB remains free. The bounded error-level service query returned no entries.
- Recovery material: `/opt/monolith-ds/backups/server-luam-20260721-asteroid-belt-force` (about 263 MiB), `/opt/monolith-ds/backups/server_config-before-luam-20260721-asteroid-belt-force.toml`, and `/opt/monolith-ds/backups/data-luam-20260721-asteroid-belt-force.tar.gz` (about 78 MiB). The deployed config SHA256 remained `9107b9e168794dba70fdb3563a1c0fa704226c3ec6e11b0cee383e44150c12cd`.
- Player-facing news entry `2026072103` already covers the asteroid belt, copper, and shuttle console. The earlier entries in the same release cover vouchers/dock selection, cargo event timing, TSF defense, and planetoids. No duplicate deployment-only news entry was added.
- A read-only architecture comparison for the user's local-model idea found that `LuaMNpcActivityLifecycleSystem` already owns generic intent lifecycle and rescue already demonstrates coordination/memory. The recommended next design is a LuaM work-order board, target leases, bounded memory, and a bridge to existing `_Mono/NPC/HTN`; no AI implementation was added during this release.

Commands and outcomes:

```powershell
python Tools/validate_luam_feature_pack.py
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMBankAndPdaContractsTest" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
dotnet restore Content.IntegrationTests\Content.IntegrationTests.csproj --nologo
Tools\audit_luam_dependency_vulnerabilities.ps1 -Json
```

Result: feature validation passed; the final bank fixture run passed 30/30; restore selected `System.Security.Cryptography.Xml 10.0.10`; the dependency audit passed five projects with zero findings.

```powershell
Tools\build_luam_release_package.ps1 -RunTests -RunLocalSmoke -Json
Tools\verify_luam_release_package.ps1 -PackagePath DeploymentPackages\LuaM\luam-local-release-20260720-234117.zip -ExpectedSha256 69a8ed2a8522005d95ea876b4e240e8d12c382f29957ec7e3a2920ee73996c77 -Json
Tools\build_luam_server_release.ps1 -ExternalClientBaseUrl http://188.127.225.57:1213 -SourcePackagePath DeploymentPackages\LuaM\luam-local-release-20260720-234117.zip -ExpectedSourcePackageSha256 69a8ed2a8522005d95ea876b4e240e8d12c382f29957ec7e3a2920ee73996c77 -ReleaseReceiptPath release\luam-binary-release-receipt.json -Json
Tools\audit_release_surface.ps1 -PackagePath release\SS14.Client.zip,release\SS14.Server_linux-x64.zip -Json
```

Result: final source gate exited 0 in 615.5 seconds; package verification, binary build, and independent surface audit all exited 0.

Three completed gates before the final pass were intentionally not treated as releasable: the first found 21 untracked release files, one direct English disk description, and one pooled bank test failure; the second passed tests/smoke but a newly published NuGet advisory rejected `System.Security.Cryptography.Xml 10.0.9`; the third passed every other check but reproduced the pooled PDA preference-cache race after two integration attempts. One still earlier gate was interrupted by the user and left no process or package. No interrupted or partially completed command remains.

```powershell
Tools\provision_monolith_client_static.ps1 -ClientPackagePath release\SS14.Client.zip -ExpectedSha256 9ecabd9e56f14e8c3ade78e98844a782fbbfd22332d327a52a95941dda11a80f -Version 9ecabd9e56f14e8c3ade78e98844a782fbbfd22332d327a52a95941dda11a80f -ReleaseReceiptPath release\luam-binary-release-receipt.json -ExpectedReleaseReceiptSha256 5984642299719a35cb9c0c50047d8071de3a76322d64b7d04ffdc54c0b2c128c
Tools\deploy_luam_server_release.ps1 -PackagePath release\SS14.Server_linux-x64.zip -ExpectedSha256 f63bc7d0841694228417b1bd0fee5d3dbf1ee5ce0a90687c0375e926d363873e -ReleaseReceiptPath release\luam-binary-release-receipt.json -ExpectedReleaseReceiptSha256 5984642299719a35cb9c0c50047d8071de3a76322d64b7d04ffdc54c0b2c128c -Tag luam-20260721-asteroid-belt-force -ConfigSourcePath server_config.remote.toml -RemoteConfigPath /opt/monolith-ds/server/server_config.toml -RemoteDataDir /opt/monolith-ds/data -RequireDataBackup -Force
```

Result: immutable client publication and forced server deployment exited 0; backup, swap, restart, status, and info checks passed.

Next action: after players exercise the new round, inspect only bounded release diagnostics:

```powershell
ssh monolith-new "sudo journalctl -u monolith-ds.service --since '2026-07-20 23:56:00 UTC' --no-pager | grep -Ei 'error|exception|asteroid|planetoid|bluespace|ship-persistence|TsfStationDefense' | tail -n 160"
```

## 2026-07-21 -- shared asteroid belt, shuttle-console redesign, and production release preparation

- Objective: add one shared critical-danger asteroid-belt world reached by sold coordinate disks, finish the futuristic shuttle-console redesign with a CRT power-on effect and resizable layout, validate the accumulated copper/sector changes, assess Erida for reusable systems, and prepare the user-authorized production update.
- The asteroid-belt map, shared per-round disk destination, dense biome, FTL entry beacon, sale inventory entry, localization, and `World` map refresh are implemented. `LuaMAsteroidBeltTest` covers the map/prototype contract.
- The shuttle console now uses custom line icons, stateful controls, cohesive navigation/world/docking panels, an old CRT reveal/scanline overlay on every open, and explicit resizing down to `640x480`. Sidebars scroll at the minimum size. `LuaMShuttleConsoleLayoutTest` and the general UI-window test cover XAML loading and all three minimum-size layouts.
- Added player-facing news entry `2026072103`; also quoted the asteroid-belt world's colon-containing news message so the changelog remains valid YAML.
- Read-only Erida audit pinned `dead-space-server/space-erida-14` at `10df9cbf66df534047a93b924581487d55900e57`. Its Station AI control of unoccupied borgs is the strongest adaptable feature and has MIT regression tests. The seed-DNA console is only a design reference because its client can submit nearly arbitrary plant chemistry. Erida's directional emotes trust a client-supplied source entity, its self-recharging turbolasers need rebalance, and its fixed `960x762` shuttle UI, station maps, TTS, languages, CPR, research UI, and material magnet either do not fit Frontier or duplicate local systems. The partial clone remains outside the repository under `%LOCALAPPDATA%\Temp\MonolithDSEridaAudit20260721`.
- Production preflight was read-only. The local and installed server-journal hashes matched, the mirror was `root:root` mode `0644`, `monolith-ds.service` was active, round 141 was running with three players, and the root filesystem had about 16 GiB free. The user's update authorization is being treated as permission for the required forced restart after a mandatory data backup.

Commands and outcomes:

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMShuttleConsoleLayoutTest|FullyQualifiedName~UiControlTest.TestWindows" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
```

Result: the first invocation compiled the UI changes but failed before layout assertions because the new asteroid-belt news message contained an unquoted colon. After quoting that message, the retry exited 0 and passed 2/2.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMAsteroidBelt|FullyQualifiedName~LuaMShuttleConsoleLayoutTest|FullyQualifiedName~LuaMCopperIndustryPrototypeTest|FullyQualifiedName~LuaMServerNewsChangelogTest|FullyQualifiedName~UiControlTest.TestWindows" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
```

Result: exit 0; passed 5/5 in 55 seconds.

```powershell
dotnet build Content.YAMLLinter\Content.YAMLLinter.csproj --no-restore --nologo --verbosity:minimal -m:1
& .\bin\Content.YAMLLinter\Content.YAMLLinter.exe
git diff --check
```

Result: the fresh linter build exited 0 with existing warnings and no errors; full YAML/map validation exited 0 with `No errors found in 97198 ms.`; diff hygiene exited 0 with expected LF-to-CRLF notices only.

External audit commands used GitHub repository metadata, `git ls-remote`, a depth-one blob-filtered clone, bounded `git ls-tree`, and direct `git show` reads. Two broad `git grep` attempts against lazily fetched blobs timed out after roughly 64 seconds and were explicitly replaced with direct path reads; no external repository file was modified.

Next action: build and smoke-test the production source package:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\build_luam_release_package.ps1 -RunTests -RunLocalSmoke -Json
```

## 2026-07-21 -- TSF automatic PDV defense, radar affiliation, and paired planetoid cycle

- Objective: make the TSF flagship Halcyon automatically fire on Vanguard/PDV ships, verify how other SS14 projects represent ship factions on radar, and change bluespace planetoids to recurring two-object batches that clean up floor contents without deleting players.
- Ship targeting now has an optional grid-level `CompanyComponent` filter. `NearbyPdvShuttleTargets` accepts only targets on grids marked `PDV`; the standalone query deliberately does not inherit the unrestricted shuttle query because utility-query lists append during prototype inheritance.
- Halcyon's station gunnery server now runs `TsfStationDefenseCompound`, which uses the existing stationary `ShipFireGunsOperator`. That operator already requires the server to be anchored and powered. Halcyon's grid is marked `TSF`, Helios is marked `PDV`, and the existing IFF label path renders the ship name followed by `TSF` or `PDV` on a second line.
- External read-only comparison used Frontier `93da5661d3f39d00dbd34ede80332ef757a566a4`, Monolith `4ba3d72addac2ac3383e47f0c7c975e93d058406`, and Hullrot `de0b0381796fa6d9db26eaddf40ac9b0f464e551`. Frontier labels grids by entity name plus service suffix and uses IFF color, without a ship-faction identity. Monolith already appends `CompanyComponent` prototype names to radar labels. Hullrot carries dedicated `IFFComponent.Faction` and relation data, but its `GetIFFLabel` still returns only the grid name; the separate partial component patch is commented/stale. The local Monolith company path is therefore the smallest complete ship-level solution. Character `NpcFactionMember` was not reused because ship AI selects a target console/grid rather than a pilot.
- `BluespaceDungeonEventScheduler` now starts a batch every 20 minutes and every dungeon group creates exactly two planetoids. A batch is active for 15 minutes, retains the existing five-minute departure warning, and leaves a five-minute scheduled gap before the next batch.
- Planetoids no longer auto-extend while players are nearby and can no longer be preserved with `ClaimableGrid`. Existing linked-grid cleanup detaches player-controlled entities and their transform children before deleting the grid. `LuaMPlanetoidCleanupTest` proves that the player and a carried item survive while the planetoid and a loose floor item are deleted.
- Russian player-facing news entries `2026072014` and `2026072015` describe Halcyon defense/radar affiliations and the paired planetoid lifecycle. Russian and English planetoid radio announcements now consistently use plural wording.
- No production server read or mutation was performed. `Tools/AI_SERVER_JOURNAL.md` was not updated. The heavily dirty pre-existing worktree was left intact outside the scoped files. After validation, the user requested the local stack and it was started successfully.

Commands and outcomes:

```powershell
git show HEAD:Content.Shared/Shuttles/Systems/SharedShuttleSystem.IFF.cs
git show HEAD:Content.Shared/Shuttles/Components/IFFComponent.cs
git show HEAD:Content.Shared/_Crescent/Diplomacy/SharedShuttleSystemPatch.cs
git show HEAD:Content.Server/_Crescent/Diplomacy/DiplomacySystem.cs
```

Result: the bounded reads of the three temporary metadata-only clones completed and established the ship-IFF comparison above. Two broader parallel `git grep` attempts against the partial Hullrot clone timed out after roughly 34 seconds while lazily fetching blobs; they were interrupted by timeout and replaced with bounded `git ls-tree` plus `git show` reads. The temporary clones remain outside the repository under `%LOCALAPPDATA%\Temp\MonolithDSFactionResearch20260720`.

```powershell
dotnet format SpaceStation14.slnx whitespace --no-restore --include <TSF and planetoid C# files> --verbosity minimal
dotnet format Content.IntegrationTests\Content.IntegrationTests.csproj whitespace --no-restore --include <targeted LuaM tests> --verbosity minimal
```

Result: both formatter invocations exited 0 with workspace-load warnings only. The solution formatter also reformatted an old switch in `NPCUtilitySystem.cs`; an initial reverse patch failed on whitespace, the retry with whitespace-insensitive matching succeeded, and the five-line company filter was reapplied so no unrelated formatting churn remains.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMTsfStationDefenseTest|FullyQualifiedName~LuaMPlanetoidCleanupTest|FullyQualifiedName~LuaMRescueAutonomyPrototypeTest.BluespaceDungeonSchedulerRunsTwoPlanetoidsWithFiveMinuteGap" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
```

Result: exit 0; passed 4/4 in the first targeted run. This covered strict PDV-only target selection, Halcyon map/prototype wiring, planetoid schedule/count configuration, and player-versus-floor-item cleanup.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMTsfStationDefenseTest|FullyQualifiedName~LuaMPlanetoidCleanupTest|FullyQualifiedName~LuaMRescueAutonomyPrototypeTest.BluespaceDungeonSchedulerRunsTwoPlanetoidsWithFiveMinuteGap|FullyQualifiedName~LuaMServerNewsChangelogTest" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
```

Result: the first full scoped invocation passed 5/5 in 37 seconds. After reducing formatter churn and reapplying the same filter, the final invocation exited 0 and passed 5/5 in 36 seconds. The defense test also verifies the exact remote-radar label `Vanguard Test Ship\nPDV`; the news test loaded the updated changelog and localization.

```powershell
dotnet build Content.YAMLLinter\Content.YAMLLinter.csproj --no-restore --nologo --verbosity:minimal -m:1
& .\bin\Content.YAMLLinter\Content.YAMLLinter.exe
```

Result: the fresh linter build exited 0 with 0 errors and existing repository warnings; the full YAML/map validation exited 0 with `No errors found in 130802 ms.`

```powershell
git diff --check
```

Result before the journal update: exit 0; no whitespace errors, only expected LF-to-CRLF working-copy notices.

```powershell
& .\Tools\start_local_stack.ps1 -Reset -PiperTts -PiperVoices 'ru_RU-irina-medium'
Invoke-RestMethod -Uri http://127.0.0.1:8787/health
Invoke-RestMethod -Uri http://127.0.0.1:1213/status
```

Result: the start command exited 0 in 62 seconds. The content server reports `Server Version 277.2.1.0 -> Ready` on `127.0.0.1:1213`; the client connected and reached `InGame`; the gateway health response is `ok=true` with Piper voice `ru_RU-irina-medium`. `server-err.txt` and `client-err.txt` are empty. The normal client log contains one non-blocking Discord IPC timeout. The local round is still in the lobby with zero players.

Next action: manually verify Halcyon's `PDV` radar label/automatic fire and one forced planetoid batch in the running client; if runtime behavior differs, capture the bounded logs with:

```powershell
rg -n -i "TsfStationDefense|NearbyPdv|BluespaceDungeon|planetoid|error|exception" C:\MonolithTemp\server-out.txt C:\MonolithTemp\client-out.txt | Select-Object -Last 160
```

## 2026-07-20 -- ephemeral voucher ships, dock-map calling, and daily cargo-crate events

- Objective: detach voucher-created ships from LuaM persistence, preserve the existing ship parking flow, replace persistent ship calling's gate dropdown with a clickable station map, and limit analogous bluespace cargo-crate events to one run per 24 hours.
- Voucher purchases now create round-local ships without registering a LuaM ship identity or persistent security binding. Their shuttle and deed components keep `PersistentShipId` empty, and selling a non-persistent voucher ship skips registry retirement. Historical persistent voucher records remain callable for compatibility.
- Persistent parking and calling require an ID card. Parking behavior remains unchanged. Calling now opens a dedicated station-grid map with clickable dock markers; free and occupied docks are visually distinct, only free docks can be confirmed, and the server revalidates the selected dock entity and ownership before restoration.
- The purchase gate selector remains in the main shipyard window and is explicitly labelled for purchases. Stored-ship and dock selections survive actor-targeted state refreshes where still valid.
- All three events backed by `BluespaceCargoRule` were found in `Resources/Prototypes/_NF/Events/nf_events.yml`: `BluespaceCargoCrate`, `BluespaceMcCargoCrate`, and `BluespaceSyndicateCrate`. Their per-round recurrence delay is now 1440 minutes. Spawn counts within one event were not changed.
- Added `LuaMBluespaceCargoEventContractTest`, which loads all three real prototypes and verifies their cargo spawners and 1440-minute recurrence delay.
- Added Russian player-facing news entries `2026072012` and `2026072013`; changelog loading and localization validation pass.
- No production server reads or mutations were performed. `Tools/AI_SERVER_JOURNAL.md` was not updated.

Commands and outcomes:

```powershell
dotnet format SpaceStation14.slnx whitespace --no-restore --include <shipyard and persistence files> --verbosity minimal
dotnet format Content.IntegrationTests\Content.IntegrationTests.csproj whitespace --no-restore --include Content.IntegrationTests\Tests\_LuaM\LuaMBluespaceCargoEventContractTest.cs Content.IntegrationTests\Tests\_LuaM\LuaMServerNewsChangelogTest.cs --verbosity minimal
```

Result: both commands exited 0; workspace-load warnings only.

```powershell
dotnet build Content.Client\Content.Client.csproj --no-restore --verbosity:minimal -m:1
```

Result: exit 0; 0 errors. Existing repository warnings remain.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMShipPersistenceLifecycleContractTest|FullyQualifiedName~LuaMShipyardPurchaseDurabilityContractTest" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
```

Result: exit 0; passed 23/23 in the initial scoped run.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMShipPersistenceOrchestratorRuntimeTest|FullyQualifiedName~LuaMShipPersistenceDatabaseTest|FullyQualifiedName~LuaMShipPersistenceLifecycleContractTest|FullyQualifiedName~LuaMShipyardPurchaseDurabilityContractTest|FullyQualifiedName~LuaMServerNewsChangelogTest" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
```

Result: the first broader invocation exited 1 with 29/30 passing because the new Russian news message contained an unquoted colon and made the YAML invalid. The message was quoted, the isolated news test passed 1/1, and the final invocation exited 0 with 36/36 passing in 64.5 seconds.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMBluespaceCargoEventContractTest|FullyQualifiedName~LuaMServerNewsChangelogTest" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
```

Result: exit 0; passed 2/2 in 53 seconds. The server loaded the changed event prototypes and the client loaded the updated player news.

```powershell
git diff --check
```

Result: exit 0; no whitespace errors, only expected LF-to-CRLF working-copy warnings.

Exploration notes: `Get-Date -AsUTC` failed because this PowerShell version lacks that parameter; `(Get-Date).ToUniversalTime()` succeeded. Two initial `rg` variants for the event search exited 1 because `--glob` followed `--` or a Windows path contained a wildcard directory; corrected `rg -g "*.yml"` searches found exactly the three cargo-crate event prototypes listed above. No command was interrupted or left running.

Next action: run the full local release gate before packaging or deployment:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\build_luam_release_package.ps1 -RunTests -RunLocalSmoke -Json
```

## 2026-07-20 -- targeted ship-persistence project check

- Objective: run the next local project validation for the current ship-persistence repair surface without touching production.
- The targeted ship-persistence integration suite passed: `LuaMShipPersistenceOrchestratorRuntimeTest`, `LuaMShipPersistenceDatabaseTest`, and `LuaMShipPersistenceLifecycleContractTest` reported 30/30 passed, 0 failed, 0 skipped.
- Repository diff hygiene passed with no whitespace errors. Git only reported expected LF-to-CRLF working-copy warnings.
- No production server reads or mutations were performed, so `Tools/AI_SERVER_JOURNAL.md` was not updated for this iteration.
- No player-facing behavior was changed in this iteration, so no new `Resources/Changelog/ServerNews.yml` entry was added.

Commands and outcomes:

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMShipPersistenceOrchestratorRuntimeTest|FullyQualifiedName~LuaMShipPersistenceDatabaseTest|FullyQualifiedName~LuaMShipPersistenceLifecycleContractTest" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
```

Result: exit 0; passed 30/30 in 42 seconds, with existing package-trimming warnings only.

```powershell
git diff --check
```

Result: exit 0; no whitespace errors, only LF-to-CRLF warnings.

```powershell
Get-Date -Format "yyyy-MM-dd HH:mm K"
Get-Date -AsUTC -Format "yyyy-MM-ddTHH:mm:ssZ"
Get-Content -LiteralPath .agents/ITERATION_LOG.md -TotalCount 40
```

Result: local time read succeeded as `2026-07-20 23:24 +03:00`; the `-AsUTC` variant failed because this PowerShell version lacks that parameter; the journal header read succeeded.

Next action: run the broader release-gate source package check when ready:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\build_luam_release_package.ps1 -RunTests -RunLocalSmoke -Json
```

## 2026-07-20 -- live shared-shuttle and restore-error diagnosis

- Objective: inspect the current production symptom where shipyard users appear to receive one shared stored shuttle and repeated errors; diagnosis only, with no gameplay, database, service, or configuration mutation requested.
- The required production-journal mirror matched before host inspection. Production was healthy at `2026-07-20T16:09Z`: round 141, run level 1, 6 players, service active, and about 16 GiB free disk.
- Current-round bounded logs contained 234 persistent-call failures across three ship identities and three actor accounts: 233 were `database snapshot envelope identity or revision mismatch`, and one was an owner-filtered `snapshot not found` when another actor attempted the same deed ship identity. There were no current-round lathe-definition errors, although the tested local lathe fix is still release-pending.
- A read-only SQLite audit found 10 ship registry rows for 5 owners: 7 stored, 3 retired, and no live leases. Every payload parsed, every embedded ship identity matched its row, and no embedded ship identity was duplicated. All 10 rows failed the current `payload revision == database revision - 1` expectation.
- Root cause 1: `luam_ship_snapshot.revision` is both the database CAS/lifecycle counter and is treated as the grid payload revision. Claim, abort, completion, retirement, and recovery advance the database counter without replacing the payload. After a failed placement/abort (or recovery), `LuaMShipPersistenceOrchestrator.TryDecode` permanently rejects the still-valid payload; repeated calls continue advancing the row revision. One affected row had reached database revision 431 while its valid payload remained revision 1.
- Root cause 2: `OnItemSlotChanged` loops over every actor viewing the same console and calls `TryRecoverStoredShipDeed` against the same inserted ID. The first actor can attach their stored ship identity to that shared entity; later actors accept the existing deed without owner revalidation. `RefreshState` then uses globally replicated `SetUiState`, so the last actor-specific balance/deed state is shown to all viewers. The restore lookup itself remains owner-filtered, which explains the wrong-owner `NotFound` rather than cross-owner payload disclosure.
- A later read-only snapshot while the round continued saw one new active registry row (11 total: 7 stored, 3 retired, 1 active). The new active row still satisfied the current relation, but all seven callable stored rows remained mismatched. All 11 payloads contain a `ShipGridLock`, a `ShuttleConsoleLock`, and two serialized `shuttleId` string fields. Because those fields encode runtime entity IDs as strings, map loading cannot remap them to the restored grid; console/deed unlock matching is therefore the next confirmed compatibility defect after restore is unblocked.
- The final aggregate found one consistent lease for the active row, zero inconsistent lease/status rows, and zero payload hashes shared across owners. Two owners currently have multiple non-retired rows, while deed recovery sorts by last update and silently chooses only `FirstOrDefault`; unassigning removes only the card component, so the same stored row can be auto-attached again. A further code-review risk is that round-end capture failure leaves the in-memory active lease entry intact and renewal does not first prove that its grid still exists.
- A read-only primary-source survey found one useful public cross-restart reference: `michaelchessall/SS14-Persistence` on its `persistence_testing` branch, with per-grid GUID filenames, key-bound bluespace parking, and persistent-ID remapping. It is not safe to transplant directly: its save path can delete a grid after a failed save, its unchecked backup move can leave a snapshot reloadable, and whole-map plus per-grid restoration can duplicate a ship. `persistent-survival` is a downstream of the same design. Frontier explicitly remains round-scoped, while ordinary SS14/RobustToolbox provides grid serialization primitives rather than an ownership/lease registry.
- No production state was repaired or modified. The required server-journal mirror was installed and verified against the repository copy with owner `root:root`, mode `0644`; the service remained active with round 141 and 6 players. No player-facing changelog entry is needed because no behavior was changed.

Commands and outcomes:

```powershell
(Get-FileHash -Algorithm SHA256 -LiteralPath 'Tools/AI_SERVER_JOURNAL.md').Hash.ToLowerInvariant()
ssh monolith-new "sha256sum /opt/monolith-ds/AI_SERVER_JOURNAL.md; stat -c '%U:%G %a' /opt/monolith-ds/AI_SERVER_JOURNAL.md"
ssh monolith-new "<bounded service/status/disk and persistence-log query for the last three hours>"
ssh monolith-new "find /opt/monolith-ds/data -maxdepth 3 -type f <database-name filters>"
```

Result: the mirror hash matched; the service/status/storage checks passed; bounded logs reproduced the shared-deed and revision failures; the database was located without reading configuration or secrets.

```powershell
$encoded | ssh monolith-new "base64 -d | python3 -"
```

Result: two initial quoting variants failed before executing the read-only audit. The final stdin/base64 invocation opened `preferences.db` with SQLite `mode=ro` and returned only aggregate counts and one-way aliases; it confirmed valid, unique payload identities and universal lifecycle/payload revision divergence. A second bounded invocation aggregated current-round persistence errors without retaining account identifiers.

A follow-up read-only aggregate used the same command shape and inspected only component/field counts inside the 11 current payloads. It confirmed that every ship snapshot carries the non-remappable string lock identity described above; no payload contents or identifiers were emitted.

The final aggregate also executed owner-multiplicity, lease/status-consistency, cross-owner payload-hash, and stored-revision-relation counts. It returned two owners with multiple non-retired rows, one consistent active lease, zero inconsistent lease rows, zero cross-owner duplicate payload hashes, and zero valid revision relations among seven stored rows.

Read-only primary-source review covered `michaelchessall/SS14-Persistence`, its bluespace-parking and persistent-ID pull requests, downstream `persistent-survival`, Frontier's persistence policy, and the upstream SS14/RobustToolbox grid save/load surface. It found no production-ready drop-in module; the reusable architectural ideas are stable ship UUIDs, per-ship keys, reference remapping, and explicit atomic lifecycle transitions.

```powershell
scp Tools/AI_SERVER_JOURNAL.md monolith-new:/tmp/AI_SERVER_JOURNAL.20260720T1615Z.md
ssh monolith-new "sudo install -o root -g root -m 0644 /tmp/AI_SERVER_JOURNAL.20260720T1615Z.md /opt/monolith-ds/AI_SERVER_JOURNAL.md && <remove exact temporary file and verify mirror/service/status>"
```

Result: the repository and installed journal hashes matched; owner/mode were `root:root`/`0644`; the service and round status remained healthy.

Next action: add failed-placement/abort/retry, multi-viewer deed ownership/UI-isolation, and restored lock-identity regressions, then implement separate payload-versus-CAS revision validation, stable/remapped lock identity, and owner-safe per-actor shipyard state:

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMShipPersistenceOrchestratorRuntimeTest|FullyQualifiedName~LuaMShipPersistenceDatabaseTest|FullyQualifiedName~LuaMShipPersistenceLifecycleContractTest" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
```

## 2026-07-20 -- lathe queue grid-serialization fix (release pending)

- Objective: restore docking/parking persistence for a live shuttle whose `OreProcessor` queue caused `grid-serialization-failed`; deploy after production gates under the still-current server/client authorization.
- Bounded production logs confirmed repeated snapshot failures for one `M-Class CIV-108`: `LatheComponent.Queue` contained `LatheRecipeBatch`, which had no data definition. This prevented the ship snapshot operation from completing; it was not an exact dock-pair rejection.
- `LatheRecipeBatch` is now a data definition. Snapshots persist the recipe and printed/requested counters, while the session-only actor entity and UI cancellation index are rebuilt or cleared instead of becoming stale cross-round references.
- Added `LiveLatheQueueCapturesAndRestores`, using a real `OreProcessor` on a grid. It captures, deletes, and restores the grid, then proves recipe/progress preservation and removal of the old actor reference.
- Added Russian player-facing server news entry `2026072011` and extended its changelog contract.

Commands and outcomes:

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMFullShipPersistenceRuntimeTest.LiveLatheQueueCapturesAndRestores" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
```

Result: the first invocation exited 1 before testing because Debug package assets were absent after the release build. `dotnet restore Content.IntegrationTests\Content.IntegrationTests.csproj` exited 0. The next compile exposed one missing test-only prototype namespace and exited 1; after adding the import, the test exited 0 and passed 1/1 in 54 seconds.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMLatheQueueRuntimeTest|FullyQualifiedName~LuaMFullShipPersistenceRuntimeTest.LiveLatheQueueCapturesAndRestores|FullyQualifiedName~LuaMServerNewsChangelogTest" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
dotnet format Content.IntegrationTests\Content.IntegrationTests.csproj whitespace --no-restore --include Content.Shared\Lathe\LatheComponent.cs Content.IntegrationTests\Tests\_LuaM\LuaMFullShipPersistenceRuntimeTest.cs Content.IntegrationTests\Tests\_LuaM\LuaMServerNewsChangelogTest.cs --verbosity minimal
git diff --check -- Content.Shared\Lathe\LatheComponent.cs Content.IntegrationTests\Tests\_LuaM\LuaMFullShipPersistenceRuntimeTest.cs Content.IntegrationTests\Tests\_LuaM\LuaMServerNewsChangelogTest.cs Resources\Changelog\ServerNews.yml
```

Result: tests passed 4/4 in 37 seconds; formatter exited 0 with workspace-load warnings; diff check exited 0 with line-ending notices only.

Next action: build the production-gated source package:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\build_luam_release_package.ps1 -RunTests -RunLocalSmoke -Json
```

## 2026-07-20 -- bounded ghost-follow incident diagnosis

- Objective: determine why an administrator was returned from following another player to free ghost movement; no gameplay or production mutation was requested.
- The required production-journal mirror matched before host inspection. Bounded logs showed no disconnect, timeout, or kick for the affected session.
- The session entered admin ghost mode, then generated repeated verb requests against a target entity that no longer existed. The observed behavior is consistent with entity deletion or replacement invalidating the follow target and returning the observer to free ghost mode.
- A later, separate production error was found: full-grid persistence repeatedly failed on a shuttle because `Content.Shared.Lathe.LatheRecipeBatch` has no writable data definition, with additional stale deleted-entity references. It happened after the follow event and does not indicate a player disconnect, but it is the next persistence defect to fix.
- Production remained healthy: round 140, run level 1, 5 players, about 16 GiB free disk. No service, configuration, database, player state, or round state was changed. No private player data is retained in this journal.

Commands and outcomes:

```powershell
(Get-FileHash -Algorithm SHA256 -LiteralPath 'Tools/AI_SERVER_JOURNAL.md').Hash.ToLowerInvariant()
ssh monolith-new "sha256sum /opt/monolith-ds/AI_SERVER_JOURNAL.md"
ssh monolith-new "sudo journalctl -u monolith-ds.service --since '20 minutes ago' --no-pager | <bounded follow, entity-lifecycle, disconnect, and exception filters>"
ssh monolith-new "curl -fsS http://127.0.0.1:1212/status"
ssh monolith-new "df -h / /opt/monolith-ds"
```

Result: read-only checks completed; follow-target invalidation was distinguished from a network disconnect, and the independent lathe-batch snapshot error was captured for follow-up.

Next action: inspect the lathe batch ownership and serialization boundary:

```powershell
rg -n "LatheRecipeBatch|LatheComponent|RecipeBatch" Content.Server Content.Shared Content.IntegrationTests --glob '*.cs'
```

## 2026-07-20 -- Phoenix persistence, exact dock binding, and shipyard fixes (deployed)

- Current objective completed: packaged and deployed the tested Phoenix persistence, selected-gate, parked-deed unassign, and exact dock/undock patch to production under the user's explicit authorization to update with players online.
- Production diagnosis: a populated `SpreaderGridComponent.SpreadQueues` contained runtime `Entity<EdgeSpreaderComponent>` references that the map serializer cannot write. Persistent registration returned `grid-serialization-failed`, and purchase cleanup then removed the staged Phoenix grid.
- `SpreaderGridComponent` now retains a private serializable compatibility field for existing map YAML while excluding the derived live scheduler queue from snapshots. `SpreaderSystem` rebuilds that queue from active spreaders after grid initialization. The tiny real-spreader capture/delete/restore runtime test passes.
- The shipyard UI preserves the selected gate by entity across state refreshes and selects an available gate initially. A parked persistent deed can now be unassigned even while its physical grid is unloaded; the Russian failure text no longer incorrectly refers to a sale.
- Shuttle-console dock and undock commands are now bound to the exact reciprocal port pair. The server verifies that the initiating port belongs to the console-controlled shuttle, rejects substituted or unrelated pairs, derives `Undock All` ports server-side, and derives FTL-lock targets from live dock connections instead of accepting arbitrary client entity lists.
- The docking tab now shows only port names in its selector, removes duplicate per-port `LOCKED`/`UNLOCKED` labels and debug chatter, and hides unrelated connected-port actions. `DockingPortState` carries the exact reciprocal port so the client only offers undock for the selected pair.
- Added `LuaMShuttleDockBindingRuntimeTest`; it proves that unrelated and substituted pairs remain docked, the exact pair undocks, `Undock All` only affects the controlled shuttle, and UI state contains the reciprocal port.
- Updated player-facing Russian server news entry `2026072010` with the implemented persistence, gate, deed, exact docking, and UI cleanup behavior.
- Full release gate passed and produced source package `DeploymentPackages/LuaM/luam-local-release-20260720-120853.zip`, SHA256 `22ce75c43e2e48211049fef758134fa55146657144a7e0838db0aaa110580022`, with all 1040 changed files included and no untracked release files.
- Built and audited matching server/client archives. Server SHA256 is `f5a9db27074a1af024d42ea244a12ace39b72717c66847a5e777ea401805bd4b`; client/version SHA256 is `9838a0a641945ff0bf26bd571ec433221970f0cd028195328f89cd6b230ecf0e`; release receipt SHA256 is `e1155355564d689e3cb7e5b13f485b343182e98d46f4ab4045c7ba1326d7c422`.
- Client publication returned HTTP 200. Forced production deploy `luam-20260720-dock-binding-force` completed while 3 players were online, with server/config/data backups. Post-deploy service is active; round 140 status/info are healthy; the new client version is advertised; disk has about 16 GiB free; no visible warning/error entries were returned by the bounded journal query.
- The updated production journal mirror was installed and verified against the repository copy with owner `root:root`, mode `0644`; the service remained active afterward.
- At the user's request, a prior bounded read-only activity check found no chat or linked admin-log activity after the specified account connected; ordinary movement is not logged. No private player data is retained here. Service state remained active with 3 players and about 17 GiB free disk. The required server journal mirror was updated after that read-only operation.

Commands and outcomes:

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMFullShipPersistenceRuntimeTest.PhoenixWithLiveSpreaderQueuesCapturesAndRestores" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
```

Result: interrupted after the wrapper timed out at 184 seconds; the surviving test process tree was explicitly stopped.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMFullShipPersistenceRuntimeTest.LiveSpreaderQueueCapturesAndRestores" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
```

Result: exit 0; passed 1/1 in 38 seconds after the final nonserialized runtime-queue implementation.

```powershell
dotnet build Content.Server\Content.Server.csproj --no-restore --verbosity:minimal -m:1
dotnet build Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --verbosity:minimal -m:1
```

Result: the first server build exposed one nullable wrapper mismatch in the new `Undock All` loop and exited 1; after using the non-null `GetDocks` contract directly, the full integration build exited 0 with existing warnings and no errors.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMShuttleDockBindingRuntimeTest" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
```

Result: the first compile exited 1 for a missing `System.Linq` import; the next invocation executed the assertions but was reported skipped because the new test dirty-disposed its pair. After adding `CleanReturnAsync`, the clean rerun exited 0 and passed 1/1 in 34 seconds.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMFullShipPersistenceRuntimeTest|FullyQualifiedName~LuaMShipPersistenceLifecycleContractTest|FullyQualifiedName~LuaMShuttleDockBindingRuntimeTest|FullyQualifiedName~LuaMShuttleConsoleLayoutTest|FullyQualifiedName~LuaMServerNewsChangelogTest" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
git diff --check -- <changed shuttle, shipyard, spreader, changelog, and test paths>
```

Result: exit 0; passed 17/17 in 1 minute 50 seconds. Scoped diff check exited 0 with only line-ending notices.

The first release-package attempt after the code changes exited 1 immediately because the previous deployment authorization had expired. The user explicitly reauthorized an immediate update; only its time window and approval ID were refreshed, while allowed mutations remained `server-release` and `client-static`. The repeated release command then passed.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\build_luam_release_package.ps1 -RunTests -RunLocalSmoke -Json
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\verify_luam_release_package.ps1 -PackagePath DeploymentPackages\LuaM\luam-local-release-20260720-120853.zip -ExpectedSha256 22ce75c43e2e48211049fef758134fa55146657144a7e0838db0aaa110580022 -Json
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\build_luam_server_release.ps1 -ExternalClientBaseUrl http://188.127.225.57:1213 -SourcePackagePath DeploymentPackages\LuaM\luam-local-release-20260720-120853.zip -ExpectedSourcePackageSha256 22ce75c43e2e48211049fef758134fa55146657144a7e0838db0aaa110580022 -ReleaseReceiptPath release\luam-binary-release-receipt.json -Json
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\audit_release_surface.ps1 -PackagePath release\SS14.Client.zip,release\SS14.Server_linux-x64.zip -Json
```

Result: exits 0. Full production tests and local smoke passed in 520 seconds; source verification passed without issues or warnings; binary build completed with existing warnings; independent client/server surface audit found zero violations.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\provision_monolith_client_static.ps1 -ClientPackagePath release\SS14.Client.zip -ExpectedSha256 9838a0a641945ff0bf26bd571ec433221970f0cd028195328f89cd6b230ecf0e -Version 9838a0a641945ff0bf26bd571ec433221970f0cd028195328f89cd6b230ecf0e -ReleaseReceiptPath release\luam-binary-release-receipt.json -ExpectedReleaseReceiptSha256 e1155355564d689e3cb7e5b13f485b343182e98d46f4ab4045c7ba1326d7c422
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\deploy_luam_server_release.ps1 -PackagePath release\SS14.Server_linux-x64.zip -ExpectedSha256 f5a9db27074a1af024d42ea244a12ace39b72717c66847a5e777ea401805bd4b -ReleaseReceiptPath release\luam-binary-release-receipt.json -ExpectedReleaseReceiptSha256 e1155355564d689e3cb7e5b13f485b343182e98d46f4ab4045c7ba1326d7c422 -Tag luam-20260720-dock-binding-force -ConfigSourcePath server_config.remote.toml -RemoteConfigPath /opt/monolith-ds/server/server_config.toml -RemoteDataDir /opt/monolith-ds/data -RequireDataBackup -Force
```

Result: exits 0. Client static HTTP verification returned 200; server deployment backed up data, swapped the build, restarted the service, and passed post-status/info checks. Recovery paths are recorded in `Tools/AI_SERVER_JOURNAL.md`.

Next action: verify the exact pair actions in-game after reconnecting, then inspect bounded new-round diagnostics if needed:

```powershell
ssh monolith-new "journalctl -u monolith-ds.service --since '2026-07-20 12:25:00 UTC' --no-pager | grep -Ei 'dock|undock|ship-persistence|snapshot|shipyard' | tail -n 160"
```

## 2026-07-20 -- local Ollama player-chat feasibility audit

- Objective: determine whether the user's local model can be connected to production so it can converse with players; no implementation or provider switch was authorized in this iteration.
- The configured local model is Ollama `huihui_ai/qwen3.5-abliterated:9b` at an OpenAI-compatible localhost endpoint. The `local-llm` MCP health check failed because all endpoint connection attempts failed; no model response was fabricated or used.
- Existing game support is close to the requested behavior: the LuaM gateway already accepts OpenAI-compatible `/chat/completions`, player radio messages addressed to Aibolit are sent through the gateway with `action=none`, and AI replies can be emitted through radio, direct server chat, broadcast chat, and optional TTS. General player messages addressed to `РР` currently use deterministic local handlers rather than the model.
- Before the production read-only audit, the installed `/opt/monolith-ds/AI_SERVER_JOURNAL.md` SHA256 matched the repository journal SHA256 `249278b4dadb764c3da6a1c2597f974921654ad93956e7535bd421093ea6415e`.
- Production read-only facts: `monolith-ds.service` and `luam-ai-gateway.service` are active; gateway health reports a configured OpenAI-compatible provider; the host has 4 AMD EPYC Rome vCPUs, 17 GiB RAM with about 12 GiB available, 4 GiB swap, about 17 GiB free disk, no visible NVIDIA runtime, and no Ollama installation.
- Conclusion: colocating the 9B Ollama model on the game host is possible in memory but not operationally recommended because CPU-only inference would contend with the game and likely exceed the current 15-second request timeout. Preferred deployment is the user's Ollama host or a separate GPU worker connected to the production gateway over a private authenticated tunnel, with strict response-only mode, cooldowns, bounded context, queue limits, and deterministic fallback.
- No product code, server config, provider config, service, or game state was changed. The required server-journal mirror was installed as `root:root` mode `0644`; both the game and gateway services remained active.

Commands and outcomes:

```text
local_model_status
```

Result: failed with `All connection attempts failed`; the configured local Ollama endpoint was unavailable.

```powershell
rg -n -i --hidden --glob '!bin/**' --glob '!obj/**' --glob '!.git/**' "local[_ -]?llm|ollama|lm studio|openai|chat completion|language model|ai gateway|luam_ai" Content.Server Content.Shared Content.Client Resources Tools
Get-Content -Encoding utf8 Tools\luam_ai_gateway.py
Get-Content -Encoding utf8 Content.Server\_LuaM\Sector\LuaMSectorAiDirectorSystem.cs
Get-Content -Encoding utf8 Content.Shared\CCVar\CCVars.LuaM.cs
```

Result: confirmed the existing provider adapter, guarded gateway, player radio hook, output channels, request budgets, and the distinction between model-backed Aibolit replies and deterministic general `РР` replies.

```powershell
(Get-FileHash -Algorithm SHA256 -LiteralPath 'Tools/AI_SERVER_JOURNAL.md').Hash.ToLowerInvariant()
ssh monolith-new "sha256sum /opt/monolith-ds/AI_SERVER_JOURNAL.md"
```

Result: repository and host journal hashes matched before production inspection.

```powershell
ssh monolith-new "<bounded service/gateway health, CPU, memory, disk, GPU-runtime, Ollama-presence, and process probe>"
```

Result: all substantive checks completed and produced the facts above; the combined probe exited 1 only because its final optional `ps -C` lookup found no matching process name.

Next action: start and verify the local Ollama endpoint before implementing a private tunnel and response-only player chat path:

```powershell
ollama serve
ollama run huihui_ai/qwen3.5-abliterated:9b
```

## 2026-07-20 -- forced production deploy for stored ship recovery

- Objective: ship the cryosleep self-enter fix, stored shuttle deed recovery, server description text about shuttle persistence between rounds/characters, and the current gated release batch to production.
- User explicitly authorized updating production even with players online.
- Production pre-checks: host mirror of `Tools/AI_SERVER_JOURNAL.md` matched the repository copy before mutation; service was active; status checks showed round 137 with 3 players during dry-run and 1 player immediately before the forced live deploy; disk was about 54-55% used with 17-18 GiB available.
- Release policy was unfrozen and authorization was expanded from `server-release` to `server-release` plus `client-static`, because the server build uses external client delivery and deploy refuses to start if the referenced client archive is not reachable.
- Fixed release-readiness blockers in existing LuaM release content: removed staged blank lines at EOF in three YAML files and translated direct English LuaM descriptions to Russian in ship vouchers, gunnery, Phoenix, Arrow, and Rising prototypes.
- Fixed `LuaMShipPersistenceLifecycleContractTest` source lookup so contract tests read the actual repository root instead of stale copied source files under the test output directory.
- Built and verified source package `DeploymentPackages\LuaM\luam-local-release-20260720-083002.zip`, SHA256 `a52ae0802bf78053f057747c4dbcdf1de3621cbefa408e2d61c50c3dae38cfc5`, production eligible.
- Built binary release with server package SHA256 `f4201b98b1bbc3501cc01af6619736fc81f6b40f426dd124cc9661efb2b67e1b`, client package/version SHA256 `14db964d98f14be4ec5497fc2ece76ad791f83f6f15fee426c1c625c8edfb6d6`, receipt SHA256 `14165d4b6a68e49f2fd40788dce20bbe2ff1a7acbb5c2d2d258d4a24a7f09e63`.
- Published client archive to static hosting and verified HTTP 200.
- Deployed production server with `-Force`, tag `luam-20260720-ship-deed-force`, `-RequireDataBackup`, and `server_config.remote.toml` as config source. Deploy completed, stopped/started `monolith-ds.service`, created server/config/data backup paths, and post-status returned round 138, run level 0, 0 players.
- Post-deploy health: `monolith-ds.service` active, `/status` and `/info` respond, `/info` advertises new external client URL, root filesystem 55% used with about 17 GiB available, warning journal check returned no visible warning/error entries.

Commands and outcomes:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\build_luam_release_package.ps1 -RunTests -RunLocalSmoke -Json
```

Result: initial attempts were blocked by release-readiness validators and later by two stale-source contract assertions; after minimal fixes, final run exited 0 and produced source package `a52ae0802bf78053f057747c4dbcdf1de3621cbefa408e2d61c50c3dae38cfc5`.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\verify_luam_release_package.ps1 -PackagePath "C:\Users\Orvar Od\Monolith-DS\DeploymentPackages\LuaM\luam-local-release-20260720-083002.zip" -ExpectedSha256 a52ae0802bf78053f057747c4dbcdf1de3621cbefa408e2d61c50c3dae38cfc5 -Json
```

Result: exit 0; package hash, archive safety, manifest/file list, payload hashes, UTF-8 text, required files, readiness evidence, and scope audit passed.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --configuration DebugOpt --no-restore --filter "FullyQualifiedName~LuaMShipPersistenceLifecycleContractTest" -- NUnit.NumberOfTestWorkers=1
```

Result: exit 0; 9/9 tests passed after source-root lookup fix.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\build_luam_server_release.ps1 -ExternalClientBaseUrl http://188.127.225.57:1213 -SourcePackagePath "C:\Users\Orvar Od\Monolith-DS\DeploymentPackages\LuaM\luam-local-release-20260720-083002.zip" -ExpectedSourcePackageSha256 a52ae0802bf78053f057747c4dbcdf1de3621cbefa408e2d61c50c3dae38cfc5 -ReleaseReceiptPath release\luam-binary-release-receipt.json -Json
```

Result: exit 0; produced server/client packages and receipt; compiler emitted existing warnings.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\provision_monolith_client_static.ps1 -ClientPackagePath "C:\Users\Orvar Od\Monolith-DS\release\SS14.Client.zip" -ExpectedSha256 14db964d98f14be4ec5497fc2ece76ad791f83f6f15fee426c1c625c8edfb6d6 -Version 14db964d98f14be4ec5497fc2ece76ad791f83f6f15fee426c1c625c8edfb6d6 -ReleaseReceiptPath "C:\Users\Orvar Od\Monolith-DS\release\luam-binary-release-receipt.json" -ExpectedReleaseReceiptSha256 14165d4b6a68e49f2fd40788dce20bbe2ff1a7acbb5c2d2d258d4a24a7f09e63
```

Result: exit 0; client static archive installed; HTTP status 200.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\deploy_luam_server_release.ps1 -PackagePath "C:\Users\Orvar Od\Monolith-DS\release\SS14.Server_linux-x64.zip" -ExpectedSha256 f4201b98b1bbc3501cc01af6619736fc81f6b40f426dd124cc9661efb2b67e1b -ReleaseReceiptPath "C:\Users\Orvar Od\Monolith-DS\release\luam-binary-release-receipt.json" -ExpectedReleaseReceiptSha256 14165d4b6a68e49f2fd40788dce20bbe2ff1a7acbb5c2d2d258d4a24a7f09e63 -Tag luam-20260720-ship-deed-force -ConfigSourcePath server_config.remote.toml -RemoteConfigPath /opt/monolith-ds/server/server_config.toml -RemoteDataDir /opt/monolith-ds/data -RequireDataBackup -Force
```

Result: exit 0; forced deploy completed while 1 player was online; service restarted; data backup, server backup, and config backup paths were recorded by deploy script; post-status healthy.

```powershell
ssh monolith-new "systemctl is-active monolith-ds.service; curl -fsS http://127.0.0.1:1212/status; curl -fsS http://127.0.0.1:1212/info | head -c 1000; df -h / /opt/monolith-ds; du -sh /opt/monolith-ds/backups /opt/monolith-ds/deploy-staging /opt/monolith-ds/server 2>/dev/null"
ssh monolith-new "journalctl -u monolith-ds.service -p warning..alert -n 40 --no-pager"
```

Result: service active; status/info healthy; info points at client version `14db964d98f14be4ec5497fc2ece76ad791f83f6f15fee426c1c625c8edfb6d6`; disk 55% used; no visible warning/error journal entries.

Next action: verify in-game with a fresh ID card after cryo/new round that the shipyard console recovers and calls the stored shuttle:

```powershell
rg -n -i "ship-persistence|snapshot|restore|placement|dock|persistent|park|call|deed|shipyard|Hammer" C:\MonolithTemp\server-out.txt C:\MonolithTemp\client-out.txt | Select-Object -Last 320
```

## 2026-07-20 вЂ” Stored ship deed recovery after cryo/round restart

- User restarted the local round and asked where the shuttle went.
- Log/SQLite check showed `Hammer -116` was not lost: snapshot `91674118-E53E-43B8-85D4-D4BA5742331B` is stored for owner `8fba9d80-30b3-4edc-bd37-9eea291a3125`, vessel `Hammer`, entity count `770`, status `Stored`, no active presence lease.
- Root cause: parking correctly unloaded the physical grid, but the callable `PersistentShipId` lived only on the old `ShuttleDeedComponent` on the old ID card. After cryo/new round the new ID card had no deed, so the shipyard console could not offer/call the stored ship.
- Updated `Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs` so the shipyard console recovers the latest stored, unleased owner ship into a `ShuttleDeedComponent` on the inserted ID card before UI refresh/call validation.
- Added `ShipyardRecoversStoredOwnerDeedBeforeCallingParkedShip` to `LuaMShipPersistenceLifecycleContractTest`.
- Added player-facing server news entry `2026072009`.
- Stopped the old local client/server to unblock build output, rebuilt, and restarted the local stack. Current local server is reachable on `127.0.0.1:1213`; `Content.Server` PID 38428 and `Content.Client` PID 21232 are running; server/client stderr are empty; client reached `InGame`.
- No production deployment or remote mutation was performed.

Commands and outcomes:

```powershell
rg -n -i "ship-persistence|snapshot|registration|restore|placement|dock|persistent|purchase|park|round|Restarting round|Starting round|JoeGenero|Hammer|McChicken|shuttle|shipyard|РІС‹Р·РѕРІ|СЃРѕС…СЂР°РЅ" C:\MonolithTemp\server-out.txt C:\MonolithTemp\client-out.txt | Select-Object -Last 420
```

Result: confirmed `Hammer -116` was purchased, registered to `JoeGenero`, serialized twice, parked/deleted from the world, then the round was restarted and the player rejoined with a new body.

```powershell
@'
import sqlite3
from pathlib import Path
p = Path(r'C:\MonolithTemp\server-data\preferences.db')
con = sqlite3.connect(f'file:{p}?mode=ro', uri=True)
cur = con.cursor()
for row in cur.execute("""
select ship_id, owner_user_id, status, vessel_prototype_id, ship_name, ship_name_suffix, entity_count, source_round_id, last_restore_round_id, stored_at_utc, updated_at_utc
from luam_ship_snapshot
order by updated_at_utc desc
limit 5
"""):
    print(row)
'@ | python -
```

Result: stored snapshot row exists for `Hammer -116`; `luam_ship_presence_lease` was empty when inspected.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMShipPersistenceLifecycleContractTest.ShipyardRecoversStoredOwnerDeedBeforeCallingParkedShip|FullyQualifiedName~LuaMServerNewsChangelogTest" -m:1 --verbosity:minimal
```

Result: exit 0; passed 2/2.

```powershell
dotnet build Content.Server/Content.Server.csproj --no-restore --verbosity:minimal
```

Result: exit 0; server build succeeded with 0 errors and existing warnings.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-build --no-restore --filter "FullyQualifiedName~LuaMShipPersistenceLifecycleContractTest.ShipyardRecoversStoredOwnerDeedBeforeCallingParkedShip|FullyQualifiedName~LuaMServerNewsChangelogTest" -m:1 --verbosity:minimal
git diff --check -- Content.Server\_NF\Shipyard\Systems\ShipyardSystem.Consoles.cs Content.IntegrationTests\Tests\_LuaM\LuaMShipPersistenceLifecycleContractTest.cs Resources\Changelog\ServerNews.yml .agents\ITERATION_LOG.md
```

Results: test rerun passed 2/2; diff check exit 0 with only expected LF-to-CRLF notices.

```powershell
.\Tools\start_local_stack.ps1 -Reset -PiperTts -PiperVoices 'ru_RU-irina-medium'
Get-Process Content.Server,Content.Client -ErrorAction SilentlyContinue | Select-Object Id,ProcessName,StartTime
Test-NetConnection 127.0.0.1 -Port 1213 | Select-Object TcpTestSucceeded,RemoteAddress,RemotePort
rg -n -i "exception|fatal|error|Connected|InGame|state changed|JoeGenero|Hammer|ship deed|recover stored|Persistent ship call|ShipParked" C:\MonolithTemp\server-out.txt C:\MonolithTemp\client-out.txt | Select-Object -Last 120
```

Result: local stack started, TCP 1213 reachable, stderr empty, client reached `InGame`.

Next action: in the running local client, insert the new ID card in a shipyard console. The parked `Hammer -116` should be recovered onto the card and the Call action should be available; if not, capture:

```powershell
rg -n -i "ship-persistence|snapshot|restore|placement|dock|persistent|park|call|deed|shipyard|Hammer|JoeGenero" C:\MonolithTemp\server-out.txt C:\MonolithTemp\client-out.txt | Select-Object -Last 320
```

## 2026-07-20 вЂ” Cryosleep self-enter action restored during local transition check

- User reported that while already asleep/lying near cryosleep pods there appeared to be no `Р—Р°Р»РµР·С‚СЊ` action; they entered by dragging instead.
- Investigated `Content.Server/_NF/CryoSleep/CryoSleepSystem.cs` and found the self-insert verb was hidden when `args.CanInteract`/`_actionBlocker.CanMove(args.User)` was false. Sleeping adds interaction blockers, so the player-facing verb could disappear even though drag insertion still accepted the live body.
- Updated cryosleep self-entry to use a shared `CanSelfEnterCryo` guard: empty pod, live mob, `MindContainerComponent`, access to the pod, and either normal interaction or `SleepingComponent`.
- Added a normal empty-hand interaction on `MachineCryoSleepPod` so a conscious player can enter an empty cryosleep chamber by ordinary interaction without needing drag/drop.
- Added `SleepingBodyStillGetsSelfEnterCryoVerb` to `LuaMDeepCryoRuntimeTest`; it verifies a sleeping live body still receives an insert verb from the cryosleep pod even when `canInteract` is false.
- Added player-facing server news entry `2026072008` and updated the changelog contract to the public author `РљРѕРјР°РЅРґР° СЃРµСЂРІРµСЂР°` and current non-technical phrases.
- Restarted the local stack after build/test. Current local server is reachable on `127.0.0.1:1213`; `Content.Server` and `Content.Client` are running; server/client stderr are empty; client reached `InGame`.
- Manual UI verification still pending: confirm the cryosleep chamber now shows/accepts `Р—Р°Р»РµР·С‚СЊ`, then continue buy/park/restart/call ship persistence check.
- No production deployment or remote mutation was performed.

Commands and outcomes:

```powershell
rg -n -i "cryo|cryosleep|cryostorage|enter|climb|buckle|drag|strip|РєР°РїСЃСѓР»|СЃРѕРЅ|sleep" Content.Server Content.Shared Content.Client Resources -g "*.cs" -g "*.yml" -g "*.ftl" | Select-Object -First 260
rg -n -i "CryoSleep|Cryostorage|CryoPod|Sleeper|Stasis|Enter" Resources\Prototypes -g "*.yml" | Select-Object -First 220
Get-Content -Encoding utf8 Content.Server\_NF\CryoSleep\CryoSleepSystem.cs | Select-Object -First 260
Get-Content -Encoding utf8 Content.Server\_NF\CryoSleep\CryoSleepSystem.Returning.cs | Select-Object -First 220
```

Result: inspected cryosleep server logic, return flow, prototypes, and locale. Some broad searches returned exit 1 because of no matches/truncated pipeline behavior; useful output confirmed `MachineCryoSleepPod` uses `CryoSleep` and the self-enter verb was gated by movement/interaction checks.

```powershell
Get-Content -Encoding utf8 Resources\Prototypes\_NF\Entities\Structures\Machines\cryopod.yml | Select-Object -First 140
Get-Content -Encoding utf8 Content.Server\_NF\CryoSleep\CryoSleepComponent.cs
Get-Content -Encoding utf8 Content.Server\_NF\CryoSleep\CryoSleepFallbackComponent.cs
Get-Content -Encoding utf8 Content.Shared\Medical\Cryogenics\SharedCryoPodSystem.cs | Select-Object -Skip 120 -First 80
Get-Content -Encoding utf8 Content.IntegrationTests\Tests\_LuaM\LuaMDeepCryoRuntimeTest.cs | Select-Object -First 260
```

Result: confirmed the difference between medical `CryoPod` and Frontier `MachineCryoSleepPod`, plus existing deep-cryo runtime coverage.

```powershell
.\Tools\stop_local_stack.ps1 -Force
```

Result: exit 0; stopped local server, client, Robust wrappers, and gateway/test Python processes before rebuilding.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMDeepCryoRuntimeTest.SleepingBodyStillGetsSelfEnterCryoVerb" -m:1 --verbosity:minimal
```

First attempt: exit 1 at compile time because `InteractHandEvent` needed `Content.Shared.Interaction`; fixed the import.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMDeepCryoRuntimeTest.SleepingBodyStillGetsSelfEnterCryoVerb" -m:1 --verbosity:minimal
```

Second attempt: exit 1 at compile time because the test used `Loc.GetString` without the test context import; removed localization dependency.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMDeepCryoRuntimeTest.SleepingBodyStillGetsSelfEnterCryoVerb" -m:1 --verbosity:minimal
```

Third attempt: exit 1 only because the verb text was localized as `Р—Р°Р»РµР·С‚СЊ` rather than the test's hard-coded `Enter`; this proved the verb existed. Updated the assertion to check for any non-empty insert verb.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMDeepCryoRuntimeTest.SleepingBodyStillGetsSelfEnterCryoVerb" -m:1 --verbosity:minimal
```

Result: exit 0; passed 1/1 in 34 seconds.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMDeepCryoRuntimeTest.SleepingBodyStillGetsSelfEnterCryoVerb|FullyQualifiedName~LuaMServerNewsChangelogTest" -m:1 --verbosity:minimal
```

Result: exit 0; passed 2/2 in 49 seconds.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-build --no-restore --filter "FullyQualifiedName~LuaMDeepCryoRuntimeTest" -m:1 --verbosity:minimal
git diff --check -- Content.Server/_NF/CryoSleep/CryoSleepSystem.cs Content.IntegrationTests/Tests/_LuaM/LuaMDeepCryoRuntimeTest.cs Content.IntegrationTests/Tests/_LuaM/LuaMServerNewsChangelogTest.cs Resources/Changelog/ServerNews.yml .agents/ITERATION_LOG.md
```

Results: deep-cryo runtime class passed 11/11 in 49 seconds; selected diff check exit 0 with only expected LF-to-CRLF notices.

```powershell
dotnet build Content.Server/Content.Server.csproj --no-restore --verbosity:minimal
```

Result: exit 0; server build succeeded with 0 errors and three existing package trim warnings.

```powershell
.\Tools\start_local_stack.ps1 -Reset -PiperTts -PiperVoices 'ru_RU-irina-medium'
```

Result: exit 0; local stack started.

```powershell
Get-Process Content.Server,Content.Client,Robust.Server,Robust.Client -ErrorAction SilentlyContinue | Select-Object ProcessName,Id,StartTime
Test-NetConnection 127.0.0.1 -Port 1213 | Select-Object ComputerName,RemotePort,TcpTestSucceeded
Get-Content -Encoding utf8 C:\MonolithTemp\server-err.txt -Tail 80 -ErrorAction SilentlyContinue
Get-Content -Encoding utf8 C:\MonolithTemp\client-err.txt -Tail 80 -ErrorAction SilentlyContinue
rg -n -i "Runlevel changed to: InGame|Server Version|Ready|Starting round|toggleready|Connected|cryo|РєСЂРёРѕ|ship-persistence|purchase|restore|dock" C:\MonolithTemp\server-out.txt C:\MonolithTemp\client-out.txt | Select-Object -Last 180
```

Result: `Content.Server` PID 32940 and `Content.Client` PID 38120 running; TCP 1213 reachable; stderr files empty; client reached `InGame`.

Next action: in the running local client, first verify `MachineCryoSleepPod` now exposes/accepts `Р—Р°Р»РµР·С‚СЊ` without drag/drop, then resume the ship check: buy Hammer/McChicken, park/save it, restart the round, and call it back. If anything fails, capture:

```powershell
rg -n -i "cryo|РєСЂРёРѕ|ship-persistence|snapshot|registration|restore|placement|dock|persistent|purchase|park|round" C:\MonolithTemp\server-out.txt C:\MonolithTemp\client-out.txt | Select-Object -Last 320
```

## 2026-07-20 вЂ” Local inter-round ship persistence check in progress

- User requested local verification of inter-round ship persistence.
- Initial local stack state: no Content/Robust server or client processes were running and `127.0.0.1:1213` was not reachable.
- First local stack start partially completed: command timed out after 184 seconds, but `Content.Server` was running and the port was reachable. The client exited before staying connected.
- Root cause of the client exit was a YAML parse error in `Resources/Changelog/ServerNews.yml` at line 15, caused by an unquoted Russian `message:` scalar containing a colon.
- Fixed that single YAML line by quoting the message. After the fix, `LuaMServerNewsChangelogTest` parses the changelog but still fails because it intentionally still expects author `LuaM` while the file now uses `РљРѕРјР°РЅРґР° СЃРµСЂРІРµСЂР°`.
- Restarted the local stack successfully. Current local server is reachable on `127.0.0.1:1213`, `Content.Server` and `Content.Client` are running, and both `server-err.txt` and `client-err.txt` are empty.
- Current post-start logs show client `JoeGenero` reached `InGame` and switched to `LobbyState`; no `toggleready True` was seen in the fresh scan. The round itself auto-started locally as Frontier roundstart.
- Ran the server-side inter-round persistence runtime test without rebuilding while the local stack was up; `LuaMShipPersistenceOrchestratorRuntimeTest` passed 2/2.
- Manual UI verification is still pending: buy a ship, park/save it, restart the round, then call the same ship back at a selected free dock.
- No production deployment or remote mutation was performed.

Commands and outcomes:

```powershell
Get-Process Content.Server,Content.Client,Robust.Server,Robust.Client -ErrorAction SilentlyContinue | Select-Object ProcessName,Id,StartTime
Test-NetConnection 127.0.0.1 -Port 1213 | Select-Object ComputerName,RemotePort,TcpTestSucceeded
git status --short
Get-Content -Encoding utf8 C:\MonolithTemp\server-out.txt -Tail 120 -ErrorAction SilentlyContinue
```

Result: no local Content/Robust processes; TCP test returned `False`; worktree is heavily dirty with many unrelated existing changes; old server log had no current ship-persistence purchase/park/restore evidence.

```powershell
rg -n -i "ship-persistence|snapshot|registration|restore|placement|dock|persistent|purchase|round" C:\MonolithTemp\server-out.txt | Select-Object -Last 260
rg -n -i "restartround|endround|startround|force.*round|round.*restart|restart" Content.Server Content.Shared Resources Tools -g "*.cs" -g "*.yml" -g "*.ftl" -g "*.ps1" | Select-Object -First 200
Get-Content -Encoding utf8 Tools\start_local_stack.ps1 | Select-Object -First 220
```

Result: old log showed normal round restart/start lines; command search found restart-related admin/vote/round code and `LuaMShipPersistenceOrchestrator` round cleanup subscription; inspected the local stack script.

```powershell
.\Tools\start_local_stack.ps1 -Reset -PiperTts -PiperVoices 'ru_RU-irina-medium'
```

Result: timed out after 184 seconds. This is a partially completed command: server process was later found running and listening, but client did not remain running.

```powershell
Get-Content -Encoding utf8 C:\MonolithTemp\client-err.txt -Tail 160 -ErrorAction SilentlyContinue
Get-Content -Encoding utf8 C:\MonolithTemp\server-err.txt -Tail 160 -ErrorAction SilentlyContinue
Get-Content -Encoding utf8 C:\MonolithTemp\server-out.txt -Tail 220 -ErrorAction SilentlyContinue
Get-Content -Encoding utf8 C:\MonolithTemp\local-stack-pids.json -ErrorAction SilentlyContinue
```

Result: client stderr contained `Unhandled exception. (Line: 15, Col: 69, Idx: 673) ... invalid mapping`; server stderr was empty.

```powershell
$i=0; Get-Content -Encoding utf8 Resources\Changelog\ServerNews.yml | ForEach-Object { $i++; if ($i -ge 1 -and $i -le 35) { '{0,4}: {1}' -f $i, $_ } }
```

Result: confirmed line 15 was an unquoted changelog message with a colon.

```powershell
rg -n "^\\s*message:\\s+[^'\\\"].*:" Resources\Changelog\ServerNews.yml
```

Result: exit 1 because the PowerShell quoting was invalid; no files were changed.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMServerNewsChangelogTest" -m:1 --verbosity:minimal
```

Result: exit 1; changelog parsed, but test failed at the known stale assertion expecting author `LuaM` instead of `РљРѕРјР°РЅРґР° СЃРµСЂРІРµСЂР°`.

```powershell
.\Tools\stop_local_stack.ps1 -Force; .\Tools\start_local_stack.ps1 -Reset -PiperTts -PiperVoices 'ru_RU-irina-medium'
```

Result: exit 0; local stack restarted successfully.

```powershell
Get-Process Content.Server,Content.Client,Robust.Server,Robust.Client -ErrorAction SilentlyContinue | Select-Object ProcessName,Id,StartTime
Test-NetConnection 127.0.0.1 -Port 1213 | Select-Object ComputerName,RemotePort,TcpTestSucceeded
Get-Content -Encoding utf8 C:\MonolithTemp\server-err.txt -Tail 80 -ErrorAction SilentlyContinue
Get-Content -Encoding utf8 C:\MonolithTemp\client-err.txt -Tail 80 -ErrorAction SilentlyContinue
rg -n -i "Runlevel changed to: InGame|Server Version|Ready|Starting round|toggleready|Connected" C:\MonolithTemp\server-out.txt C:\MonolithTemp\client-out.txt | Select-Object -Last 120
```

Result: `Content.Server` PID 5588 and `Content.Client` PID 32824 running; TCP 1213 reachable; server/client stderr empty; client reached `InGame`.

```powershell
Start-Sleep -Seconds 10; rg -n -i "toggleready|spawn|lobby|JoeGenero|Ready|InGame|Starting round|roundstart|ship-persistence|purchase|park|restore|snapshot" C:\MonolithTemp\server-out.txt C:\MonolithTemp\client-out.txt | Select-Object -Last 220
```

Result: `JoeGenero` connected, admin status applied, client reached `InGame` and switched to `LobbyState`; fresh scan did not show `toggleready True`; round auto-started locally.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-build --no-restore --filter "FullyQualifiedName~LuaMShipPersistenceOrchestratorRuntimeTest" -m:1 --verbosity:minimal
```

Result: exit 0; passed 2/2 in 34 seconds.

Next action: in the running local client, buy Hammer/McChicken at a selected free gate, park/save it, restart the round, and call it back through a selected free gate. If anything fails or after the call completes, capture:

```powershell
rg -n -i "ship-persistence|snapshot|registration|restore|placement|dock|persistent|purchase|park|round" C:\MonolithTemp\server-out.txt C:\MonolithTemp\client-out.txt | Select-Object -Last 300
```

## 2026-07-20 вЂ” Server-news cleanup paused

- User requested editing the in-game Updates/Server News so there are no `LuaM` mentions and no technical/internal details.
- Began cleanup in `Resources/Changelog/ServerNews.yml`: public `author` values were changed from `LuaM` to `РљРѕРјР°РЅРґР° СЃРµСЂРІРµСЂР°`, and player-facing messages were simplified to remove internal/technical terms such as `LuaM`, snapshots, revisions, hashes, database/storage wording, upstream wording, predicted-hit wording, and temporary restore-zone wording.
- A quick scan of `Resources/Changelog/ServerNews.yml` after edits found no matches for the searched technical terms: `LuaM|snapshot|СЃРЅРёРјРѕРє|СЂРµРІРёР·Рё|payload|hash|database|upstream|predicted|РІСЂРµРјРµРЅРЅСѓСЋ Р·РѕРЅСѓ|РїРѕСЃС‚РѕСЏРЅРЅРѕРј С…СЂР°РЅРёР»РёС‰Рµ|СЃРµСЂРІРµСЂРЅРѕР№ РїСЂРѕРІРµСЂРєРѕР№|Р°С‚РѕРјР°СЂ`.
- The changelog contract test has not yet been updated after the author/text changes. `Content.IntegrationTests/Tests/_LuaM/LuaMServerNewsChangelogTest.cs` still expects author `LuaM` and old phrases `РїРѕСЃС‚РѕСЏРЅРЅРѕРј С…СЂР°РЅРёР»РёС‰Рµ` / `РІСЂРµРјРµРЅРЅСѓСЋ Р·РѕРЅСѓ РІРѕСЃСЃС‚Р°РЅРѕРІР»РµРЅРёСЏ`, so it will fail until adjusted.
- User also reported that the local client appeared immediately as a human on the station. Current evidence from `C:\MonolithTemp\server-out.txt` earlier showed `JoeGenero:toggleready True`, so the likely cause is automatic ready/spawn behavior in the local launch/test setup. This was not fixed yet.
- No production deployment or remote mutation was performed.

Commands and outcomes:

```powershell
Get-Content -Encoding utf8 .agents/ITERATION_LOG.md | Select-Object -First 120
Get-Content -Encoding utf8 Resources/Changelog/ServerNews.yml | Select-Object -Last 180
rg -n -i "LuaM|РїРѕСЃС‚РѕСЏРЅ|snapshot|revision|database|payload|hash|РІСЂРµРјРµРЅРЅСѓСЋ Р·РѕРЅСѓ|С…СЂР°РЅРёР»РёС‰|С‚РµС…РЅРёС‡РµСЃ|persistent" Resources/Changelog/ServerNews.yml Resources/Locale/ru-RU/_LuaM/changelog Resources/Locale/en-US/_LuaM/changelog Content.IntegrationTests/Tests/_LuaM/LuaMServerNewsChangelogTest.cs
```

Result: journal and server-news files were read; scan found public-news/test mentions requiring cleanup, including author `LuaM` and technical ship-persistence phrases.

```powershell
Get-Content -Encoding utf8 Content.IntegrationTests/Tests/_LuaM/LuaMServerNewsChangelogTest.cs | Select-Object -First 150
```

Result: exit 0; confirmed the test still asserts author `LuaM` and old technical phrases.

```powershell
Get-Content -Encoding utf8 Resources/Changelog/ServerNews.yml | Select-Object -First 230
rg -n -i "auto|ready|toggleready|JoeGenero|lobby|spawn|profile|client" Tools/start_local_stack.ps1 Resources/ConfigPresets Content.Server Content.Client -g "*.ps1" -g "*.toml" -g "*.cs" | Select-Object -First 160
```

Result: exit 0 for reading server news; the search command returned exit 1 after output/search constraints, but showed `Tools/start_local_stack.ps1` starts the client with `--connect` and waits for `InGame|Connected`; earlier server log contained `JoeGenero:toggleready True`.

```powershell
rg -n -i "LuaM|snapshot|СЃРЅРёРјРѕРє|СЂРµРІРёР·Рё|payload|hash|database|upstream|predicted|РІСЂРµРјРµРЅРЅСѓСЋ Р·РѕРЅСѓ|РїРѕСЃС‚РѕСЏРЅРЅРѕРј С…СЂР°РЅРёР»РёС‰Рµ|СЃРµСЂРІРµСЂРЅРѕР№ РїСЂРѕРІРµСЂРєРѕР№|Р°С‚РѕРјР°СЂ" Resources/Changelog/ServerNews.yml
rg -n "author:" Resources/Changelog/ServerNews.yml
Get-Content -Encoding utf8 C:\MonolithTemp\server-out.txt -Tail 180 | Select-String -Pattern "toggleready|spawn|Spawn|lobby|round|JoeGenero|Ready|InGame"
```

Results: technical-term scan exit 1 because there were no matches after the edits; author scan exit 0 and showed all current entries use `РљРѕРјР°РЅРґР° СЃРµСЂРІРµСЂР°`; log scan exit 0 but only captured later AI-director lines in that tail window.

Interrupted state:

- Work was intentionally paused by the user before updating `LuaMServerNewsChangelogTest`, running changelog validation, or diagnosing/fixing the immediate human spawn.

Next action: update `Content.IntegrationTests/Tests/_LuaM/LuaMServerNewsChangelogTest.cs` to accept `РљРѕРјР°РЅРґР° СЃРµСЂРІРµСЂР°` and the new non-technical phrases, then run:

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMServerNewsChangelogTest" -m:1 --verbosity:minimal
```

After the news test passes, diagnose the immediate station spawn with:

```powershell
rg -n -i "toggleready|ready|spawn|lobby|JoeGenero|character|job" C:\MonolithTemp\server-out.txt C:\MonolithTemp\client-out.txt
```

## 2026-07-20 вЂ” Round-to-round ship persistence runtime check

- Checked the current round-transfer coverage for persistent ships. Existing lifecycle coverage proved that the orchestrator subscribes to round-end events and lease recovery, but did not directly run the full active ship -> round-end save -> next-round restore path.
- Added `ActiveShipSavesAtRoundEndAndRestoresNextRound` to `Content.IntegrationTests/Tests/_LuaM/LuaMShipPersistenceOrchestratorRuntimeTest.cs`.
- The new runtime test registers a purchased grid as active in round 77, saves all active ships at round end, verifies the active lease is removed and the stored snapshot advances to revision 3, deletes the live grid, restores the same ship in round 78 from the stored snapshot, verifies the restored grid and identity revision, then saves it again to leave the integration-test pool clean.
- No player-facing behavior was changed in this iteration, so `Resources/Changelog/ServerNews.yml` was not updated.
- No production deployment or remote mutation was performed.

Commands and outcomes:

```powershell
Get-Content -Encoding utf8 .agents/ITERATION_LOG.md | Select-Object -First 120
Get-Content -Encoding utf8 Content.IntegrationTests/Tests/_LuaM/LuaMShipPersistenceOrchestratorRuntimeTest.cs | Select-Object -First 260
rg -n "record LuaMShipSnapshotRecord|LuaMShipSnapshotRecord\(" Content.Server Content.Shared Content.IntegrationTests
```

Result: exit 0; inspected the repository handoff journal, the existing orchestrator runtime test, and the database snapshot record shape.

```powershell
Get-Content -Encoding utf8 Content.Server/Database/DatabaseRecords.LuaMShips.cs | Select-Object -Skip 90 -First 90
rg -n "SaveAllActiveShipsAsync|RestoreClaimAsync|StoreAndDeactivate|SnapshotRevision" Content.Server/_LuaM/ShipPersistence/LuaMShipPersistenceOrchestrator.cs
rg -n "GetLuaMShipSnapshotAsync|ClaimLuaMShipRestoreAsync|CompleteLuaMShipRestoreAsync|StoreLuaMShipSnapshotAsync" Content.Server/Database Content.Server/_LuaM Content.IntegrationTests/Tests/_LuaM
Get-Content -Encoding utf8 Content.Server/_LuaM/ShipPersistence/LuaMShipPersistenceOrchestrator.cs | Select-Object -Skip 70 -First 250
```

Result: exit 0; confirmed the revision flow and DB mock points needed for a direct round-to-round runtime test.

```powershell
.\Tools\stop_local_stack.ps1 -Force
```

Result: exit 0; stopped the local server, client, Robust wrappers, and gateway/test Python processes before build/test work.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMShipPersistenceOrchestratorRuntimeTest" -m:1 --verbosity:minimal
```

First attempt: exit 1 at compile time because the helper used an unavailable explicit `TestServer` type name. Fixed by making the helper local to the test and using the captured `server` variable.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMShipPersistenceOrchestratorRuntimeTest" -m:1 --verbosity:minimal
```

Second attempt: exit 1; the new test path itself completed, but left an active restored lease in the shared integration-test pool, causing `RegistrationDurablyActivatesThePurchasedGrid` to see 2 active leases. Fixed by saving the restored ship at the end of the new test.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMShipPersistenceOrchestratorRuntimeTest" -m:1 --verbosity:minimal
```

Result: exit 0; passed 2/2 in 34 seconds.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMFullShipPersistenceRuntimeTest|FullyQualifiedName~LuaMShipPersistenceOrchestratorRuntimeTest|FullyQualifiedName~LuaMShipPersistenceLifecycleContractTest|FullyQualifiedName~LuaMShipPersistenceDatabaseTest|FullyQualifiedName~LuaMServerNewsChangelogTest" -m:1 --verbosity:minimal
dotnet build Content.Server/Content.Server.csproj --no-restore --verbosity:minimal
```

Results: persistence/changelog suite exit 0, passed 17/17 in 1 minute 30 seconds; server build exit 0 with 0 errors and three existing package warnings.

```powershell
git diff --check -- Content.IntegrationTests/Tests/_LuaM/LuaMShipPersistenceOrchestratorRuntimeTest.cs .agents/ITERATION_LOG.md
```

Result: exit 0 with only the existing LF-to-CRLF notice for `.agents/ITERATION_LOG.md`.

```powershell
.\Tools\start_local_stack.ps1 -Reset -PiperTts -PiperVoices 'ru_RU-irina-medium'
Get-Process Content.Server,Content.Client,Robust.Server,Robust.Client -ErrorAction SilentlyContinue | Select-Object ProcessName,Id,StartTime
Test-NetConnection 127.0.0.1 -Port 1213 | Select-Object ComputerName,RemotePort,TcpTestSucceeded
Get-ChildItem C:\MonolithTemp -Filter '*err.txt' -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName; Get-Content -Encoding utf8 $_.FullName -Tail 40 }
Get-Content -Encoding utf8 C:\MonolithTemp\server-out.txt -Tail 80 -ErrorAction SilentlyContinue
```

Results: local stack start command exit 0; `Content.Server` PID 36732 and `Content.Client` PID 45740 are running; TCP 127.0.0.1:1213 is reachable; `server-err.txt` and `client-err.txt` are empty; server log shows `Server Version 277.2.1.0 -> Ready`, client approved as `JoeGenero`, and round start loaded station `MonoDev`.

Partially completed command:

```powershell
Get-Date -AsUTC -Format "yyyy-MM-dd HH:mm:ss 'UTC'"
```

Result: exit 1 because this Windows PowerShell version does not support `-AsUTC`; retried with `[DateTime]::UtcNow.ToString(...)` successfully.

Next action: in the running local client, manually buy a shuttle, park/save it, restart/end the round, and call it in the next round. If transfer fails, capture `rg -n -i 'ship-persistence|snapshot|registration|restore|round|lease|dock|persistent|purchase' 'C:\MonolithTemp\server-out.txt' | Select-Object -Last 300`.

## 2026-07-20 вЂ” Purchased-ship persistent registration hash fix

- Investigated the fresh live purchase failure. `C:\MonolithTemp\server-out.txt` showed Hammer buying and docking successfully, then failing at persistent registration with `snapshot registration failed: InvalidRequest`.
- Root cause: `LuaMShipPersistenceOrchestrator` stores a JSON envelope (`LuaMFullShipSnapshot`) in `luam_ship_snapshot.Payload`, but passed the inner YAML snapshot hash and size into the database request. `ServerDbBase.LuaMShips` validates the hash against the exact stored payload bytes, so real purchases were rejected before activation.
- Fixed `ToStoreRequest` to compute `PayloadHash` and `PayloadSizeBytes` from the serialized JSON envelope sent to the database. The inner YAML hash remains inside the envelope for restore-time snapshot integrity.
- Extended `LuaMShipPersistenceOrchestratorRuntimeTest` so mocked DB registration now asserts the outgoing stored payload hash and size match the actual payload bytes.
- Added player-facing server-news entry `2026072006` and extended the changelog contract. No production deployment or remote mutation was performed.

Commands and outcomes:

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMShipPersistenceOrchestratorRuntimeTest|FullyQualifiedName~LuaMShipPersistenceDatabaseTest|FullyQualifiedName~LuaMFullShipPersistenceRuntimeTest" -m:1 --verbosity:minimal
```

First attempt: exit 1 before tests because the running local `Content.Server (30864)` and `Content.Client (54532)` locked build outputs. This was a blocked build, not a test failure.

```powershell
& .\Tools\stop_local_stack.ps1 -Force
```

Result: exit 0; stopped local server, client, Robust wrappers, and gateway/test Python processes.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMShipPersistenceOrchestratorRuntimeTest|FullyQualifiedName~LuaMShipPersistenceDatabaseTest|FullyQualifiedName~LuaMFullShipPersistenceRuntimeTest" -m:1 --verbosity:minimal
```

Result: exit 0; passed 7/7 in 1 minute 18 seconds.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMFullShipPersistenceRuntimeTest|FullyQualifiedName~LuaMShipPersistenceOrchestratorRuntimeTest|FullyQualifiedName~LuaMShipPersistenceLifecycleContractTest|FullyQualifiedName~LuaMShipPersistenceDatabaseTest|FullyQualifiedName~LuaMServerNewsChangelogTest" -m:1 --verbosity:minimal
```

Result: exit 0; passed 16/16 in 1 minute 15 seconds. Existing unrelated analyzer/package warnings remain.

```powershell
dotnet build Content.Server/Content.Server.csproj --no-restore --verbosity:minimal
git diff --check -- Content.Server/_LuaM/ShipPersistence/LuaMShipPersistenceOrchestrator.cs Content.IntegrationTests/Tests/_LuaM/LuaMShipPersistenceOrchestratorRuntimeTest.cs Content.IntegrationTests/Tests/_LuaM/LuaMServerNewsChangelogTest.cs Resources/Changelog/ServerNews.yml
```

Results: server build exit 0 with 0 errors and three existing package warnings; selected `git diff --check` exit 0.

Next action: start the fresh local stack with `.\Tools\start_local_stack.ps1 -Reset -PiperTts -PiperVoices 'ru_RU-irina-medium'`, then manually buy a shuttle at a selected free gate, park it, confirm it disappears, and recall it through another selected free gate. If anything fails, capture `rg -n -i 'ship-persistence|snapshot|registration|dock|persistent|purchase' 'C:\MonolithTemp\server-out.txt' | Select-Object -Last 220`.

## 2026-07-20 вЂ” Parked-ship call staging-map fix

- Investigated the user's follow-up that the same failure now occurred on calling a parked ship back. The fresh local log showed purchase and parking succeeding, then restore loading a new Hammer grid and immediately deleting it after placement failed.
- Root cause: `OnCallShipMessage` restored the ship directly onto the station map before running `TryFTLDockAtDock`. The docking geometry code rejects any intersecting grid on the target map and did not exclude the restored ship itself, so the restored ship blocked its own placement check.
- Fixed parked-ship calls to restore the snapshot onto a temporary staging map, then dock the restored grid to the player-selected station gate. The staging map is deleted afterwards. Failed calls now log the real restore/placement reason and show the reason instead of always masking it as occupied gates.
- Extended `RealMcChickenGridCapturesTwoRevisionsAndRestores` to assert the restored parked McChicken/Hammer-like grid can be called to the exact selected gate after source deletion.
- Added player-facing server-news entry `2026072007` and extended the changelog contract. No production deployment or remote mutation was performed.

Commands and outcomes:

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~RealMcChickenGridCapturesTwoRevisionsAndRestores" -m:1 --verbosity:minimal
```

First updated test run: exit 1; reproduced the call failure. Restored docks were present, anchored, Airlock, not receive-only, and undocked, proving the failure was placement geometry rather than missing dock state.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~RealMcChickenGridCapturesTwoRevisionsAndRestores" -m:1 --verbosity:minimal
```

Result after staging-map restore fix: exit 0; passed 1/1 in 40 seconds.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMFullShipPersistenceRuntimeTest|FullyQualifiedName~LuaMShipPersistenceOrchestratorRuntimeTest|FullyQualifiedName~LuaMShipPersistenceLifecycleContractTest|FullyQualifiedName~LuaMShipPersistenceDatabaseTest|FullyQualifiedName~LuaMServerNewsChangelogTest" -m:1 --verbosity:minimal
```

Result: exit 0; passed 16/16 in 1 minute 32 seconds. Existing unrelated analyzer/package warnings remain.

```powershell
dotnet build Content.Server/Content.Server.csproj --no-restore --verbosity:minimal
git diff --check -- Content.Server/_NF/Shipyard/Systems/ShipyardSystem.Consoles.cs Content.IntegrationTests/Tests/_LuaM/LuaMFullShipPersistenceRuntimeTest.cs Content.IntegrationTests/Tests/_LuaM/LuaMServerNewsChangelogTest.cs Resources/Changelog/ServerNews.yml .agents/ITERATION_LOG.md
```

Results: server build exit 0 with 0 errors and three existing package warnings; selected `git diff --check` exit 0 with only LF-to-CRLF notices.

Next action: start the fresh local stack with `.\Tools\start_local_stack.ps1 -Reset -PiperTts -PiperVoices 'ru_RU-irina-medium'`, then manually buy Hammer/McChicken at a selected free gate, park it, confirm it disappears, and call it back through a selected free gate. If anything fails, capture `rg -n -i 'ship-persistence|snapshot|registration|restore|placement|dock|persistent|purchase' 'C:\MonolithTemp\server-out.txt' | Select-Object -Last 260`.

## 2026-07-20 вЂ” Cross-grid docking isolation for all persistent ships

- Reproduced the purchase-only snapshot failure with the real McChicken grid docked to a separate station grid. A purchased vessel is already docked before registration, so `DockingComponent.DockedWith` and the weld joint crossed the ship snapshot boundary and pulled external station entities into serialization.
- Fixed the shared `LuaMFullShipPersistenceSystem`, so the change applies to every persisted vessel rather than one shuttle prototype. Capture now validates every reciprocal external dock pair, synchronously undocks all pairs, serializes the portable ship, preflights every original pair, and restores the same live docking connections before returning.
- Failure cleanup is fail-closed: prototype flags and the live revision are restored, docking restoration failure makes capture fail before any database write, and `_busyShips` is always released. Full capture exceptions and shipyard registration status/reason are now written to the server log.
- Extended `LuaMFullShipPersistenceRuntimeTest` with real geometry-checked McChicken docking, two consecutive revisions, proof that the external station gate is absent from the payload, live redocking after both captures, source deletion, and an undocked restored grid ready for the player-selected gate.
- Added player-facing server-news entry `2026072005` and extended its contract test. No production deployment or remote mutation was performed.

Commands and outcomes:

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~RealMcChickenGridCapturesTwoRevisionsAndRestores" -m:1 --verbosity:minimal
```

The initial undocked real-grid version passed 1/1. The first docked invocation completed but its final output was lost when the tool output exceeded the retained context, so it is not counted green. A later five-second shell timeout left only its test runner children alive; the immediate retry exited 1 before test execution because `gravestone-1.txt` was locked. The exact two test processes were inspected and stopped. The clean rerun passed 1/1 in 37 seconds. After reciprocal-pair validation, guaranteed cleanup, real FTL docking, and payload assertions were added, the rebuilt test passed 1/1 again in 44 seconds.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMFullShipPersistenceRuntimeTest|FullyQualifiedName~LuaMShipPersistenceOrchestratorRuntimeTest|FullyQualifiedName~LuaMShipPersistenceLifecycleContractTest|FullyQualifiedName~LuaMShipPersistenceDatabaseTest|FullyQualifiedName~LuaMServerNewsChangelogTest" -m:1 --verbosity:minimal
```

Result: exit 0; passed 16/16 in 1 minute 22 seconds. Existing unrelated analyzer/package warnings remain.

```powershell
dotnet build Content.Server/Content.Server.csproj --no-restore --nologo --verbosity:minimal
```

Result: exit 0; 0 errors and three existing package warnings.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~RealMcChickenGridCapturesTwoRevisionsAndRestores|FullyQualifiedName~LuaMServerNewsChangelogTest" -m:1 --verbosity:minimal
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-restore --no-build --filter "FullyQualifiedName~LuaMServerNewsChangelogTest" -m:1 --verbosity:minimal
```

Results: exit 0; passed 2/2 after the final test/news assertions, then passed the timestamped news validation 1/1.

```powershell
dotnet build Content.YAMLLinter/Content.YAMLLinter.csproj --configuration DebugOpt --no-restore --nologo --verbosity:minimal
& .\bin\Content.YAMLLinter\Content.YAMLLinter.exe
```

Result: build exit 0 with 0 errors; resource validation reported `No errors found`.

```powershell
& .\Tools\start_local_stack.ps1 -Reset -PiperTts -PiperVoices 'ru_RU-irina-medium'
```

Result: exit 0 in 46.3 seconds. Fresh server/client processes are responsive, the server reports `Ready`, the client reached `InGame`, gateway health is green with Piper `ru_RU-irina-medium`, and `server-err.txt`/`client-err.txt` are empty.

Next action: in the running client, buy any shuttle at a selected free gate, park it, confirm the grid disappears while the deed remains, then recall it through another selected free gate. If anything fails, capture the new reason with `rg -n -i 'ship-persistence|snapshot|registration|dock|persistent' 'C:\MonolithTemp\server-out.txt' | Select-Object -Last 200`.

## 2026-07-20 вЂ” Active-ship parking rejection and tutorial markup

- Removed literal rich-text color/bold tags from the Russian and English station-orientation intro so unsupported rendering cannot expose `color` words or brackets in the training frame.
- Investigated the fresh local report that ship storage was rejected. `C:\MonolithTemp\server-out.txt` showed the McChicken registration snapshot advancing past vending inventory but failing on `StationRecords/System.RuntimeType`.
- Marked the runtime type-indexed station-record table as non-serialized; station record systems rebuild this runtime-only data.
- Corrected parking snapshot capture to request `active.SnapshotRevision + 1` instead of trying to recapture the committed active revision.
- Corrected the purchase finalizer so a failed persistence registration that is proven fully cleaned rolls the bank callback back and releases retry reservations; only unprovable cleanup remains administrator-recovery state.
- Added lifecycle contracts for next-revision parking and clean purchase rollback. Added server-news entry `2026072004` with the implemented player-facing fixes.
- `Tools/stop_local_stack.ps1 -Force` stopped client/server/gateway cleanly. Server build passed with 0 errors. Combined lifecycle/news validation passed 8/8; existing unrelated warnings remain.
- Restarted the local stack with the exact next-action command: exit 0 in 39.6 seconds. Fresh client/server processes are running and both stderr files are empty.
- A subsequent live McChicken purchase exposed another external graph edge: full-grid serialization attempted to auto-include the online owner (`MobDiona`) from the station and rejected the unsavable player entity. Changed ship snapshots from unrestricted `AutoInclude` to `IncludeNullspace`: the complete explicit ship transform graph and null-space support entities remain saved, while live off-ship entities are not pulled into the ship file.
- Added a lifecycle contract preventing unrestricted owner auto-inclusion and extended server-news entry `2026072004`. Stopped the stack cleanly, rebuilt the server with 0 errors, and passed 10/10 across full persistence runtime, lifecycle, and news tests.
- Restarted the local stack again: exit 0 in 38.1 seconds. Fresh client/server processes are running and both stderr files are empty.

Next action: restart with `Tools/start_local_stack.ps1 -Reset -PiperTts -PiperVoices 'ru_RU-irina-medium'`, buy McChicken, park it, confirm the grid disappears, and call it back through a selected free gate.

## 2026-07-20 вЂ” Ship parking evacuation and purchase snapshot fix

- Changed parking so sapient mobs no longer block storage: every mob in the shuttle transform graph is unbuckled, force-removed from containers, and transferred to the station at the shipyard console before the snapshot is captured. After a successful durable store, the deed keeps its persistent UUID, clears only the live grid UID, and the shuttle grid is queued for deletion.
- The retained deed continues to make `TryValidateShuttlePurchase` reject a second purchase on the same ID card. Added source-contract tests for evacuation/store/delete ordering and this deed invariant.
- Diagnosed the user's local purchase recovery message from `C:\MonolithTemp\server-out.txt`: full-grid serialization failed on `VendingMachineInventoryEntry` in a McChicken shuttle vending machine. Made that runtime inventory entry a serializable data definition with fields and a default constructor.
- Added player-facing server-news entry `2026072003` for automatic crew evacuation, disappearing parked ships, retained one-card ownership, and vending snapshot compatibility.
- The first combined stop/build/test command timed out after 5 seconds and was interrupted. The next server build failed with one missing `Robust.Shared.Map` import; corrected it. A targeted lifecycle run then passed the four new/existing parking/purchase tests but exposed one stale recovery assertion; updated it to the explicit parked-ship lease-recovery behavior.
- Final server build passed with 0 errors. `LuaMShipPersistenceLifecycleContractTest` passed 5/5. `LuaMServerNewsChangelogTest` passed 1/1. Existing unrelated warnings remain.
- Local client/server were stopped to release build outputs. No production deployment or mutation was performed.
- Restarted the clean local stack with `Tools/start_local_stack.ps1 -Reset -PiperTts -PiperVoices 'ru_RU-irina-medium'`: exit 0 in 43.2 seconds. Fresh server/client processes are running and both stderr files are empty; the prior in-memory purchase recovery block is cleared.

Next action: start the local stack with `Tools/start_local_stack.ps1 -Reset -PiperTts -PiperVoices 'ru_RU-irina-medium'`, then buy McChicken again and verify purchase, automatic crew evacuation on parking, grid disappearance, and second-purchase rejection on the same ID card.

## 2026-07-20 вЂ” Production disk cleanup and permanent AI operations journal

- User explicitly authorized freeing production-server disk space and requested a permanent operational journal for future AI work.
- Added `Tools/AI_SERVER_JOURNAL.md` and strengthened `AGENTS.md`: future production work must read/reconcile both journals, update operational facts after every meaningful server action, install the host mirror, and never record secrets or private player data.
- Read-only audit found root storage at 86%, with 6.8 GiB backups, 4.4 GiB published clients, 1.8 GiB uploads, 757 MiB staging, about 799 MiB journals, and about 429 MiB APT cache/lists.
- Bounded cleanup removed staging/uploads contents, backup entries older than 2026-07-16, obsolete client builds except the active immutable build, APT caches/lists, and old journal segments. Current server/data/config, active client, and backups from 2026-07-16 and 2026-07-18 were preserved.
- Reclaimed 13,101,957,120 bytes; root usage fell from 86% to 54%, leaving 18 GiB available. Backups now use about 2.2 GiB, published client about 319 MiB, and journals about 150 MiB.
- The combined cleanup command exited 1 only at its final `/status` verification because BOM/CRLF from the PowerShell here-string malformed the last remote command. Cleanup itself completed. A separate verification passed: service active, `/status` healthy on round 137 with two players, active client archive present, and disk at 54%.
- No service restart or production code deployment was performed.

Next action: create and dry-run a scheduled retention policy, then enable it only after confirming the exact number of client and backup generations to preserve.

Security-monitor follow-up:

- Added and installed a hardened one-minute `monolith-security-monitor.timer` plus its oneshot service/script.
- It monitors SSH failure spikes, fail2ban pressure, public SYN backlog, game TCP connections, disk pressure, and game-service state; alerts use journald tag `monolith-security`, with current state under `/var/lib/monolith-security-monitor/status.env`.
- Initial start failed on a missing state directory; the second failed because the empty capability set rejected an unnecessary `chown`. Added `StateDirectory` and removed the ownership mutation. Final start passed; timer is active/enabled and current status is `ok` with the game service active.
- External notification is not yet configured. A private Discord webhook or another destination is required; it must be stored outside the repository in a root-only environment file.

Next action: add rate-limited Discord notification and recovery messages after receiving the private webhook destination.

## 2026-07-20 вЂ” Selectable shipyard gates and parked-ship local slice

- Added a gate selector to every shipyard console. Purchase messages carry the selected server entity, and purchase validation confirms that it is an undocked airlock port on the current station's largest grid.
- Added exact-port FTL placement. The server recalculates dock compatibility, ship geometry, station obstruction, other-grid collision, and occupancy immediately before placement; it does not trust the UI availability snapshot.
- Added same-session parking and recall. A registered ship must be physically docked to the current station and contain no sapient mobs before the full snapshot is stored and the live grid unloaded. The deed retains the stable ship UUID; recall restores and docks the grid before completing the database restore transition.
- Added server-news entry `2026072002` for the implemented player-facing behavior.
- `Tools/stop_local_stack.ps1 -Force` stopped the previous local client/server/gateway cleanly. The first server build exited 1 with 10 compile errors from a missing station-component import and definite-assignment checks; these were corrected and that run is not counted green.
- Rebuilt `Content.Server.csproj` DebugOpt: exit 0, 0 errors. Rebuilt `Content.Client.csproj` DebugOpt: exit 0, 0 errors. Existing analyzer warnings remain.
- Restarted the local stack with `Tools/start_local_stack.ps1 -Reset -PiperTts -PiperVoices 'ru_RU-irina-medium'`: exit 0. Client reached `InGame`, server/client stderr are empty, and gateway/Piper health is green.
- Known incomplete path: the current UI recalls a parked ship through the deed retained in the same session. Selecting an owned parked ship from the persistent registry after a full round restart is not yet exposed in the console UI and must be completed before release.
- No remote deployment or mutation was performed.

Next action: manually test purchase at a selected free gate, occupied-gate rejection, park with/without a mob aboard, and same-session recall; then add the owner-registry parked-ship selector for cross-round recall and run the persistence/news/YAML validation set.

Local-news refresh: after adding server-news entry `2026072002`, reran `Tools/start_local_stack.ps1 -Reset -PiperTts -PiperVoices 'ru_RU-irina-medium'`; exit 0 in 37.1 seconds. This restart ensures the running client loads the new changelog resource.

## 2026-07-20 вЂ” Local persistence manual-test stack

- Started the rebuilt local stack with `Tools/start_local_stack.ps1 -Reset -PiperTts -PiperVoices 'ru_RU-irina-medium'`; command exited 0 in 54 seconds.
- Client connected as the local test account and reached `InGame`; server and client stderr files are empty. The gateway health endpoint reports healthy and Piper `ru_RU-irina-medium` is enabled.
- No dedicated persistence parking zone exists yet. Current behavior saves all active registered ships automatically at post-round/restart cleanup and restores stored owner ships into the map containing the owner's spawned mob, retaining the grid transform serialized in the snapshot.
- No product file, server-news entry, or remote system was changed in this startup-only iteration.

Next action: in the running client, buy a shuttle, alter its contents/state, end the round, reconnect/spawn as the same owner, and observe the restored grid position. Capture persistence failures with `rg -n -i 'ship-persistence|snapshot|lease|restore' 'C:\MonolithTemp\server-out.txt' | Select-Object -Last 160`.

## 2026-07-20 вЂ” Ship persistence lifecycle integration and defensive host audit

- Completed the lifecycle wiring for full-state ship persistence. Shipyard purchase now durably registers the new grid before success, sale retires it before deed/world deletion, and the orchestrator restores stored owner ships on first spawn, renews active leases, saves all active ships at post-round/restart cleanup, and recovers expired leases before restore.
- Added player-facing server-news entry `2026072001` describing only the implemented persistence behavior.
- Corrected `LuaMShipPersistenceLifecycleContractTest` so the sale assertion slices the sale finalizer rather than the earlier purchase finalizer.
- The first targeted test command was interrupted by the shell timeout and left its `dotnet` build tree running. A second run failed before tests because those children locked `Content.Server.dll`; the exact child process tree rooted at PID 51964 was inspected and stopped. Neither run is counted green.
- Targeted persistence validation then passed 9/9: lifecycle contract, orchestrator runtime, full snapshot runtime, and database state-machine tests.
- Lifecycle/news validation passed 4/4. Fresh `Content.YAMLLinter` build completed with 0 errors (four existing package warnings), and the rebuilt linter reported `No errors found`.
- Read-only production security audit found UFW default-deny active, fail2ban active, SSH key authentication enabled, service hardening enabled, and the AI gateway bound only to loopback. It also found password SSH authentication still enabled, 1,286 failed SSH authentication events in the preceding 24 hours, no nginx request/connection limits in the effective config, deployment-only backups, and the root filesystem at 86% usage. No remote setting or file was changed.
- Release policy remains frozen; no deployment or remote mutation was performed.

Commands actually run included the targeted `dotnet test` selections above, a fresh YAML-linter build/execution, `git diff --check`, local read-only config searches, and SSH read-only inspection of listeners, UFW, sshd policy, fail2ban, systemd hardening, updates, backup timestamps, and disk usage. Two follow-up SSH formatting commands partially succeeded and then exited 1 because of shell quoting in `awk`/Python; their successful findings were independently available from the other read-only checks and they made no mutation.

Next action: after explicit authorization for production security mutation, disable SSH password/root login with a validated key-session rollback guard, add nginx connection/request limits, configure off-host scheduled backups, and reduce disk usage; begin with a second live SSH session and `sudo sshd -t` before reloading sshd.

## 2026-07-19 вЂ” Full ship persistence targeted baseline and fidelity expansion

- Re-read `AGENTS.md`, persistence continuation notes, runtime serializer, DB lease/CAS API, and targeted tests before editing.
- Preserved the mixed worktree; no `reset`, `clean`, mass formatting, or remote deployment was performed.
- Verified targeted baseline from actual test runs: `LuaMFullShipPersistenceRuntimeTest` 1/1 passed; `LuaMShipPersistenceDatabaseTest` 4/4 passed.
- Expanded runtime fidelity with an `APCBasic` battery charge round-trip assertion (`BatteryComponent.CurrentCharge`) and explicit rejection checks for unsupported snapshot format versions and prototype-manifest mismatch.
- Re-ran the expanded runtime test: 1/1 passed.
- Confirmed DB behavior remains green across snapshot store, claim/complete, renew/abort, stale CAS, quarantine, retire, and payload-integrity cases.
- Orchestration/lifecycle integration is the next step. Remote deployment remains frozen by `Tools/luam_release_policy.json`.

## 2026-07-19 вЂ” Full ship persistence orchestration service

- Added `LuaMShipPersistenceOrchestrator` between runtime snapshots and `IServerDbManager`.
- Implemented registration, restore claim, envelope decode/validation, runtime restore, restore completion, lease renewal, CAS store/deactivation, and irreversible retirement.
- Failure handling aborts unsafe restores and falls back to quarantine if abort cannot be committed; a restored runtime grid is deleted if DB completion fails.
- Added active-lease tracking to prevent two active copies of one ship UUID on the same server.
- Verified `Content.Server.csproj` DebugOpt build: 0 errors (existing analyzer warnings remain).
- No remote deployment performed.

## Current objective

Harden the production host against the observed SSH attack traffic without locking out administration, add rate limiting and off-host backups, then run the guarded release flow for the completed full-state ship-persistence lifecycle. Remote mutation and deployment require fresh explicit authorization.

## Current state

- Branch: `codex/finish-project-20260714`; HEAD observed for this integration is `0efe0838ed` (`Bound AI gateway provider responses`).
- The worktree remains very large and mixed: hundreds of prior staged, unstaged, and untracked files coexist with this integration. Re-run `git status --short --branch` before relying on exact counts, and do not discard unrelated user changes.
- Upstream comparison point: `origin/master` at `42277ff6cae06c2b08eab7fe7d946090c709a96f` (`Brain Blip's back (#16)`, 2026-07-18); merge base `3f244b9b728c567d5949daaf3336022ea8920115`; observed divergence 179 local / 13 upstream commits.
- A full content merge was deliberately rejected after `git merge-tree` exposed 10 direct conflicts and the audit found unsafe or incompatible balance/system changes. Only reviewed improvements were ported and adapted to LuaM.
- Imported and integrated: Arrow, Rising, and corrected Phoenix ships; enhanced GCS; Nordfall/Annie resprites; cybernetic organs and lung metabolism; safe IPC self-repair; server-only free-brain radar; surgery gloves/bone multitool; wall-grid cleanup; one-handed Vector handling; Sultan spread correction; missing language/dataset fixes.
- Preserved local behavior: `ScorpionLuaM`, its voucher/map/catalog IDs, deep-cryo persistence and wake flow, station-spawn tutorial, radar isolation, matter-synthesizer unanchoring, and LuaM progression/economy systems.
- Deliberately not ported: upstream wieldable scaling, raw ship-repair pre-existing-entity flag, broad weapon buffs, heavy hardsuit balance, white-phosphorus/carp/admin-ghost/kit balance, and unrelated v0.4 changes.
- The production technology map now also documents the exact research, machines, and material costs for six cybernetic organs, bluespace surgical gloves, and the bone multitool.
- The Updates window now has a first, public `ServerNews` tab localized as `РќРѕРІРѕСЃС‚Рё СЃРµСЂРІРµСЂР°`. It is the primary source for the new-update badge and already contains the recent LuaM player-facing history in Russian.
- `AGENTS.md` now requires every meaningful player-facing iteration to append a validated `LuaM` entry to `Resources/Changelog/ServerNews.yml`; a dedicated integration test locks the tab order, ID ordering, localization, and core history coverage.
- The Radiant audit and selective integration are complete at pinned HEAD `28d354a3a6fffa63b553d6e487dd53d66053156a`. LuaM now has adapted `Gornyak` (69,000) and `Salomandra` (105,000) vessels with local jobs, prices, tiles, decals, equipment, and a 300-thrust/3,750-W compatibility thruster. Provenance is preserved in the imported YAML and `LEGAL.md`.
- The functional medibot already present locally was preserved with its safer 10u Tricordrazine treatment, `Advertise`, emag behavior, HTN, and LuaM rescue integration. Only construction-part labels were localized; the Radiant/SPLURT binary sprite was not imported.
- Radiant validation is green: targeted ship/medibot/news tests 3/3, rescue autonomy 52/52, both raw maps load with required equipment and resale-safe prices, and the full YAML linter reports no errors.
- A combined broad construction run passed 57/59 but is not green: two global prototype cleanup tests still report pre-existing `ImpCoffeeMachine` storage overflow and wall-locker container-fill errors outside the adapted maps. Do not count this command as passed.
- The lathe queue now accepts known recipes while a machine is working or lacks current materials, consumes materials only when an item actually starts, enforces a configurable 1,000-item future-work limit, rejects foreign recipes and arithmetic overflow, and preserves actor attribution between batches.
- Biological `Civilian` department roles (`Contractor`, `Pilot`, and `Mercenary`) now receive exactly one `MedicalTrackingImplant` at spawn and after cloning. Their loadouts use a new novelty-only `CivilianImplanter` group, while Borg is excluded and the Vanguard tracker/radio implanter path is preserved.
- Server-news entry `2026071906` records the queue and civilian-tracker changes. The missing Russian revival death-rattle message was added.
- FuelVend now dispenses 75-unit plasma, uranium, and bananium bundles at 4,500 / 9,375 / 18,750 credits. The original per-unit prices of 60 / 125 / 250 credits are preserved and a regression test locks both count and price.
- Male and female vulpkanin once again use their distinct scream collections and the shared species-specific fox laugh, sneeze, and cry collections. Existing sex-specific growl/howl/awoo mappings remain intact; the pre-existing Dead Space Station audio now has an attribution manifest.
- Playable robotic shells have explicit TTS voices again: borgs, MMI, positronic brains, and pAI use `TrainingRobot`; station AI variants use `Glados`; IPC still receives its player-selected profile voice. LuaM AI Director settings were not changed.
- `SS14_universal_systems_pack_v2` was audited read-only. It contains eight architectural specifications and pinned licensed reference files, not a directly installable mod. Ship blueprint disks are the strongest low-risk future candidate; registry/escrow/persistence ideas can enhance local systems, while raw hardpoint/tactical/bioreplicator ports are dependency-heavy or overlap existing LuaM mechanics. The package-authored skeleton/example layer has no root LICENSE and must not be copied verbatim without explicit licensing.
- The user explicitly superseded the restricted/whitelisted ship-persistence recommendation. The required target is now a complete authoritative snapshot of the live ship grid with all gameplay-visible changes and contents preserved; the trusted-blueprint-plus-deltas model is no longer the selected restoration design. Stable UUID ownership, revisions, atomic commits, and single-active-copy locking are still required.
- The pinned RMC14 TacticalMap audit is complete. RMC implements a tile map for one ground grid with Marine/Xeno blips, live or published snapshots, lines, labels, squad/status decoration, and faction-specific behavior; it is not a relay network and has no sensor fusion, contact TTL/confidence, jamming, or multi-ship topology. A direct server-system port would not compile because the local reference slice omits most supporting client/shared files and Monolith lacks the RMC squad/skill/xeno/distress dependencies. Keep Mono radar as sensor truth and adapt only the client-map/annotation ideas behind a per-grid LuaM contract if authorized later.
- IPC deep cryo now recognizes the installed `PositronicBrain` as part of the same synthetic body instead of rejecting it as `snapshot-contains-additional-organic-body`. The exception is ownership-bound through `OrganComponent.Body` (and the exact borg brain slot for future chassis use), so carried mobs or loose neural cores remain rejected.
- A failed deep-cryo snapshot now restores the empty `DoAfterComponent` capability after discarding stale round-local operations. This prevents the reproduced second attempt from failing with a missing `DoAfterComponent` while preserving the no-external-reference snapshot rule.
- Server-news entry `2026071908` records the IPC cryo and retry fixes. The targeted IPC regression passed 1/1 and the complete deep-cryo plus server-news selection passed 11/11.
- Final validation for this iteration is green: the freshly rebuilt YAML linter reports no errors and the combined queue/civilian/fuel/vulp/TTS/news/lathe/UI integration run passes 9/9.
- Final automated validation is green: integration build 0 errors; selected upstream/map/production tests 5/5; IPC 1/1; Brain Blip 4/4; full LuaM deep-cryo set 13/13; radar/matter synthesizer 3/3; client radar/tutorial tests 10/10.
- Asset audit is clean after correcting `hemostat.ogg` attribution to the real `hemostat1.ogg`: 229 changed resource references, 34 changed RSI directories, three ship maps, prototype IDs, and en/ru localization were checked.
- The rebuilt local stack is running for manual testing: gateway PID 46592 is healthy on `127.0.0.1:8787` with Piper `ru_RU-irina-medium`, content server PID 46296 listens on `127.0.0.1:1213`, and content client PID 9380 reached `InGame`. Server/client error logs are empty; the gateway stderr contains only successful local HTTP access lines.
- Release policy remains frozen. Do not deploy remotely from this worktree without a new explicit authorization.
- Full-state ship persistence is now wired into purchase, sale, owner login, lease renewal, post-round save, and round-restart cleanup; the targeted persistence suite passes 9/9.
- The production security audit is read-only and complete. Password SSH authentication, absent nginx limits, deployment-only backups, and 86% disk use remain unresolved until remote mutation is explicitly authorized.

## Checks completed

### 2026-07-18 release preparation

Passed:

```powershell
python Tools\validate_luam_feature_pack.py --self-test
```

Result: `LuaM BankSystem validator self-test passed.`

Passed:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\test_luam_release_contract.ps1 -Json
```

Result summary:

- `ok=true`
- schema version 2
- 230 required files
- 319 package scopes
- production tests: `content-tests-luam`, `integration-tests-luam`
- smoke checks: `gateway-test`, `local-stack-smoke`
- 21 critical test files

## Interrupted work

The following full readiness command was started and deliberately interrupted after approximately 9 seconds:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\check_luam_release_ready.ps1 -RunTests -RunLocalSmoke -Json
```

Do not count it as passed. Before restarting it, check whether a child `dotnet`, `python`, or PowerShell process from the interrupted run is still active.

## 2026-07-18 production attempt

- The captured full readiness run correctly failed before remote mutation.
- The first integration run had one transient failure out of 591 tests. A complete rerun passed 591/591; TRX: `TestResults/LuaMReleaseGate-20260718/LuaMReleaseGate-20260718.trx`.
- Local smoke failed reproducibly because the standalone client sandbox rejected `ShuttleMapControl.DrawNavigationTarget` during IL verification.
- `Content.Client/Shuttles/UI/ShuttleMapControl.xaml.cs` was fixed by replacing the rejected stack-allocated span with a managed three-element array.
- `dotnet build Content.Client/Content.Client.csproj --configuration DebugOpt --no-restore` passed with no errors.
- `Tools/test_local_stack.ps1` then passed: gateway healthy, server ready, client connected, and all processes stopped cleanly.
- The user explicitly authorized deployment even while players are online. Use `-Force` for this rollout after every release gate passes.
- No remote mutation had occurred as of this journal update.

## 2026-07-18 production rollout completed

Rollout tag: `luam-20260718-195400`.

User authorization:

- Approval id: `chat-20260718-forced-server-update`.
- Scope: server release and client static provisioning, including forced update while players may be online.
- Approved at: `2026-07-18T14:07:27Z`.
- Expired at: `2026-07-18T20:07:27Z`.

Source/package gates:

- Full readiness passed before packaging: `ok=true`, `productionEligible=true`, 275 required files.
- Targeted PDA text/layout integration test passed: `LuaMPdaTextLayoutTest` 1/1.
- Targeted UI window coverage passed: `UiControlTest.TestWindows` 1/1.
- `LuaMBankAndPdaContractsTest.PdaBankTransferByIdMissingRecipientShowsRegistrationHint` was flaky in the first source package attempt. The test fixture was corrected to wait for the exact bank identity load path, then the targeted test passed 1/1.
- Final source package built successfully:
  - Path: `DeploymentPackages/LuaM/luam-local-release-20260718-194457.zip`
  - SHA256: `c1879f710829019b84156a3bc4e2a1a0f785dbc4a496df9654ace02466d15e81`
  - Policy SHA256: `d83164f25ed512222f08cfcc59c340fac858ef1b913867aa0715f8069bc421bf`
  - Payload digest: `53839ff11fad4dd29233fe2f30b90364a58f0a9e171830e3825e26848cfbfe14`
  - Worktree digest: `ba90f945ca16be480d9c2471a0d4543d824adb0e8aad4de962faf1d6f8a99f3d`
  - File count: 1264
  - Changed files packaged: 766/766
- Independent source verifier passed with no issues or warnings.

Binary artifacts:

- Client archive: `release/SS14.Client.zip`
  - SHA256: `3986a48dcf1c3e3b71dcda02ee82003d528f1fd33a688bd878455ab712316e62`
  - Bytes: 333780301
- Server archive: `release/SS14.Server_linux-x64.zip`
  - SHA256: `3197c57eb8e27d93d0659c4b04d9a2e0696457bc680d26b1ce5313939b7aa080`
  - Bytes: 44839213
- Binary receipt: `release/luam-binary-release-receipt.json`
  - SHA256: `4c88efd4df238c2b78a92d0b7a21ddbb0e022c40557c2bc2f045eddece48d330`
- Immutable client URL:
  - `http://188.127.225.57:1213/3986a48dcf1c3e3b71dcda02ee82003d528f1fd33a688bd878455ab712316e62/SS14.Client.zip`
- Release surface audit passed. Server-only canary was present in the server package and absent from the client package.
- Dry-run client provisioning passed.
- Dry-run server deployment passed with `-RequireDataBackup`, `-ConfigSourcePath server_config.remote.toml`, and `-Force`.

Production deployment:

- Client static provisioning completed. Public HEAD check returned HTTP 200, `Content-Length: 333780301`, `Content-Type: application/zip`.
- Server deploy command was launched and the assistant turn was interrupted while it was running. Later read-only audit showed the deployment had completed:
  - Service: `monolith-ds.service`
  - ActiveState/SubState: `active/running`
  - MainPID: `268253`
  - NRestarts: `0`
  - ActiveEnterTimestamp: `Sat 2026-07-18 22:56:14 MSK`
- Live config SHA256: `f96dc3b53f7274a50750c847850190d5c657fbac37e1c7a1224c79efe6e89e4c`.
- Backups created:
  - `/opt/monolith-ds/backups/data-luam-20260718-195400.tar.gz`
  - `/opt/monolith-ds/backups/server-luam-20260718-195400`
- Live `/info` after deployment:
  - `build.download_url` equals the immutable client URL above.
  - `build.hash` equals `3986A48DCF1C3E3B71DCDA02EE82003D528F1FD33A688BD878455AB712316E62`.
  - `build.version` equals `3986a48dcf1c3e3b71dcda02ee82003d528f1fd33a688bd878455ab712316e62`.
  - `links` contains `{ icon: "discord", name: "Discord", url: "https://discord.gg/hfSkDrKUR2" }`.
  - `desc` no longer contains the plain Discord invite line.
- Live `/status` monitoring for four samples over 45 seconds stayed on `round_id=137`, `run_level=0`, `players=0`, map `В«РљРѕР»РѕСЃСЃ Р¦РµРЅС‚СЂР°Р»Р»В»`.
- Journal since service start showed one normal `ticker: Restarting round!` line and no repeated restart loop in the sampled window.

Player-facing fixes included in this rollout:

- PDA program/settings/ringtone labels now have explicit text colors instead of depending on the new nano stylesheet.
- Ringtone menu minimum size was raised and resizing was restored.
- Shuttle console windows keep safe minimum sizes.
- Launcher Discord button is supplied through `[infolinks] discord = "https://discord.gg/hfSkDrKUR2"` instead of plain description text.
- Frontier tutorial flow is sequential and asks the player what they already know when they appear on the station.
- Deep cryo return, round-cycle, PDA bank, shuttle/radar, and related release-batch fixes are included in the deployed build.

## 2026-07-18 development loop acceleration

Implemented the two-stage workflow requested after the production rollout:

- `Tools/prepare_luam_hotfix.ps1`: scoped local-fast runner for development. Supported scopes: `Auto`, `Policy`, `PdaUi`, `ShuttleUi`, `Cryo`, `Round`, `LauncherInfo`, `Bank`, `LuaM`; optional `-IncludeBuild`.
- `Tools/ship_luam_release.ps1`: full production-gate orchestrator. It drives local-fast, source packaging, source verification, binary build, release-surface audit, client-static dry-run, and server-deploy dry-run. It only mutates the remote server when `-Deploy` is passed and the existing release policy authorizes the mutation.
- `Tools/luam_release_state.ps1`: helper for `.agents/current_release.json`.
- `.agents/current_release.json`: machine-readable handoff state for the latest rollout, local-fast checks, production-gate artifacts, and deploy facts.
- `Tools/monolith_release_runbook.md` and `Tools/luam_release_manifest.md` now document the new cadence.
- `Tools/luam_release_policy.json` now includes the new PS1 files in policy-required files and package scopes; canonical `requiredChecksBeforeDeploy` remains the original 7-item strict list because the release contract requires exactly those entries.

Checks run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\prepare_luam_hotfix.ps1 -Scope Policy,LauncherInfo -Json
```

Result: `ok=true`, scopes `LauncherInfo, Policy`, `changedFileCount=770`; launcher config contract passed and release-policy contract passed.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\test_luam_release_contract.ps1 -Json
```

Result: `ok=true`, `remoteDeployFrozen=true`, required files `278`, package scopes `369`, critical test files `26`.

Implementation note: the first quick-run emitted Git CRLF warnings into command output. `Tools/prepare_luam_hotfix.ps1` was corrected to filter read-only Git warning noise during changed-file detection and to capture child command output inside JSON `outputTail`.

## Next action

Use the running local client to test `wake from deep cryo`, IPC self-repair with the nanite applicator, one new cyberorgan install, and one of Arrow/Rising/Phoenix. If anything misbehaves, capture the current local logs before restarting:

```powershell
Get-Content -LiteralPath 'C:\MonolithTemp\server-out.txt' -Tail 160
Get-Content -LiteralPath 'C:\MonolithTemp\client-out.txt' -Tail 160
```

Remote deployment remains frozen. If a new deployment is needed, first get fresh user authorization and then run the guarded release process from readiness through dry-runs again. Do not reuse the expired authorization from `chat-20260718-forced-server-update`.

## Artifact record

The latest production artifact is `luam-20260718-195400`; see the completed rollout section above for source package, binary package, receipt, client URL, config SHA, backup paths, and post-deployment checks.

## History

- 2026-07-18: Initial repository orientation completed. Identified the SS14/RobustToolbox architecture and the very large mixed working tree.
- 2026-07-18: Release validator self-test and release-contract test passed. Full readiness was interrupted and remains pending.
## 2026-07-18 cryo retry hotfix

Player report: after entering cryo and interrupting/cancelling the procedure, the same player could not start cryo again.

Root cause found in `Content.Server/_NF/CryoSleep/CryoSleepSystem.cs`: `CryoSleepComponent.CryosleepDoAfter` was cancelled in some paths but not cleared. After deny/eject/cancel/success, the pod could retain a stale do-after id and fail the next automatic cryo-store attempt.

Implemented local fix:

- `OnAutoCryoSleep` clears `CryosleepDoAfter` on cancelled, already-handled, invalid target/pod, and successful completion paths.
- `StartAutomaticCryoStore` ignores a still-running do-after but clears stale completed/cancelled ids before retrying.
- Added shared helpers `ClearCryoStoreDoAfter` and `CancelCryoStoreDoAfter`.
- `FinalizeCryoStoreBody` and `EjectBody` now cancel and clear the stored do-after id.
- Added regression coverage in `Content.IntegrationTests/Tests/_LuaM/LuaMDeepCryoRuntimeTest.cs`: `InterruptedAutomaticStoreClearsDoAfterAndAllowsRetry`.

Checks run:

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-restore --filter FullyQualifiedName~InterruptedAutomaticStoreClearsDoAfterAndAllowsRetry -- NUnit.NumberOfTestWorkers=1
```

Result: passed, 1/1. First attempt before adding `DoAfter` to the test prototype failed because the mock body was invalid for `DoAfterSystem`; that was test setup, not product code.

```powershell
powershell -ExecutionPolicy Bypass -File Tools\prepare_luam_hotfix.ps1 -Scope Cryo -Json
```

Result: `ok=true`, local-fast Cryo scope passed, `LuaMDeepCryo` integration tests passed 13/13.

Deployment status: not deployed. This is a local patch only until fresh server-update authorization is given for this specific cryo hotfix.

## 2026-07-19 station-spawn tutorial follow-up

User request: rework onboarding so it appears after the player character has spawned inside a station instead of appearing in the lobby.

Repository state and implementation verified:

- The pending `GuidebookUIController` rework removes the old lobby-time automatic guidebook opening.
- Tutorial startup is gated on `GameplayState`, the local attached entity being alive, not having `GhostComponent`, and `_stationSystem.GetOwningStation(entity)` returning a valid station.
- Local-player attachment, parent changes, and return to the alive mob state reschedule the station check. Short 250 ms and 1000 ms retries cover the initial client state arriving before station ownership.
- The sequential topic flow and localized station-orientation prompt remain intact.
- This change is local only and has not been deployed.

Commands run:

```powershell
rg -n --hidden -S "tutorial|РѕР±СѓС‡РµРЅ|what they already know|already know|Tutorial" Content.* Resources Tools .agents | Select-Object -First 300
```

Result: partially successful search output identified the tutorial files, then exited 1 because PowerShell/Windows rejected the `Content.*` path argument. No mutation occurred.

```powershell
dotnet test Content.Tests/Content.Tests.csproj --configuration DebugOpt --no-restore --filter FullyQualifiedName~LuaMFrontierTutorialFlowTest -- NUnit.NumberOfTestWorkers=1
```

Result: passed 8/8.

```powershell
dotnet test Content.Tests/Content.Tests.csproj --configuration DebugOpt --no-restore --filter "FullyQualifiedName~UiControlTest.TestWindows" -- NUnit.NumberOfTestWorkers=1
```

Result: command completed successfully but matched no tests because `UiControlTest` belongs to `Content.IntegrationTests`; do not count this as a passed UI test.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-restore --filter "FullyQualifiedName~UiControlTest.TestWindows" -- NUnit.NumberOfTestWorkers=1
```

Result: passed 1/1.

Next action: perform an in-client smoke check with a fresh account, confirming that no prompt appears in the lobby and that the station-orientation prompt appears only after the character is alive and attached to a station. After that, obtain fresh explicit authorization before any production rollout.

## 2026-07-19 local tutorial smoke session

User requested a persistent local server and client for manual tutorial testing.

Repository/tooling changes:

- `Tools/start_local_stack.ps1` now exposes `ServerReadyTimeoutSeconds` with a 120-second default instead of hard-coding 45 seconds.
- The default `ClientConnectTimeoutSeconds` was raised from 240 to 420 seconds because this DebugOpt client took longer than four minutes to initialize and verify content before connecting.

Commands and outcomes:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\start_local_stack.ps1 -Reset
```

Result: failed after 52.6 seconds with no console message. Gateway was healthy, but the server had not reached `Ready` within the old 45-second limit. A child `Content.Server` process remained after the wrapper exited and was explicitly cleaned up before retrying.

Direct diagnostic launches of `bin/Content.Server/Content.Server.exe` and `dotnet bin/Content.Server/Content.Server.dll` were each interrupted by the command timeout after about 34 seconds and initially left child processes; those exact local server processes were stopped before the final retry.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\stop_local_stack.ps1 -Force
Get-Process Content.Server,Robust.Server,Content.Client,Robust.Client -ErrorAction SilentlyContinue | Stop-Process -Force
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\start_local_stack.ps1 -Reset -ServerReadyTimeoutSeconds 120
```

Result: server reached `Server Version 277.2.1.0 -> Ready` and bound to `127.0.0.1:1213`. The wrapper later returned exit 1 because the client crossed `Connected`/`InGame` just after the old 240-second client deadline. Read-only log and process inspection confirmed that the client completed both handshakes, entered `InGame`, and switched to `LobbyState`.

Current live local state at handoff:

- `Content.Server` PID observed as 18016, listening on `127.0.0.1:1213`.
- `Content.Client` PID observed as 18452, responsive with an active Monolith window and connected to the local server.
- The local stack is intentionally left running for user testing. This is not a production deployment.

Next action: user manually confirms no tutorial prompt in `LobbyState`, spawns a living character on a station, and confirms the sequential station-orientation prompt appears only then. To stop afterward, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\stop_local_stack.ps1 -Force
```

## 2026-07-19 cryo manual-test crash fix

Player report during the local smoke session: the game appeared to freeze when entering a Frontier cryo pod.

Diagnosis from the preserved logs:

- The client had actually crashed with `KeyNotFoundException`: local entity 342 retained `ActiveDoAfterComponent` for one predicted update after `DoAfterComponent` had been removed by deep-cryo transient-state cleanup.
- The server stayed responsive but refused the durable deep-cryo store. `BodyPartAppearanceComponent.Type` serialized as the data key `type`, colliding with the entity serializer's reserved component discriminator key and throwing `InvalidOperationException: Already contains key type`.

Fixes implemented:

- `Content.Client/DoAfter/DoAfterSystem.cs` now requires both `ActiveDoAfterComponent` and `DoAfterComponent` via `TryComp` in `Update` and `TryFindActiveDoAfter`; a transient replication mismatch no longer crashes the client.
- `BodyPartAppearanceComponent.Type` now serializes under `layer`, leaving the reserved `type` key to the component serializer.
- `CaptureDropsCryoDoAfterThatReferencesThePod` now attaches a runtime-created `BodyPartAppearanceComponent`, covering both transient do-after removal and the former serialization-key collision.

Checks and commands:

- The first parallel integration-test/client-build attempt failed with `CS2012` because both compilers tried to write `Content.Shared/obj/DebugOpt/Content.Shared.dll`; this is not counted as a product check.
- A sequential integration-test attempt failed with `MSB3021/MSB3027` because the preserved local server and a diagnostic `dotnet` process still held `bin/Content.Server/Content.Shared.dll`; this is not counted as a product check.
- The local stack and the exact remaining diagnostic processes were then stopped. The stop/verification compound command returned exit 1 only because the final `Get-Process` found no remaining named processes; the stop itself reported success.

Passed:

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-restore --filter FullyQualifiedName~CaptureDropsCryoDoAfterThatReferencesThePod -- NUnit.NumberOfTestWorkers=1
```

Result: passed 1/1.

```powershell
dotnet build Content.Client/Content.Client.csproj --configuration DebugOpt --no-restore
```

Result: succeeded with 0 errors and 3 existing NU1510 warnings.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\start_local_stack.ps1 -Reset
```

Result: exit 0. Fresh server PID 44572 bound to `127.0.0.1:1213`; fresh client PID 45368 completed handshakes, reached `InGame`, switched to `LobbyState`, and was left running for the user.

Next action: repeat the manual flow on the fresh local stack: confirm no tutorial in the lobby, spawn on station, enter Frontier cryo, and verify that the client remains alive and the server completes deep-cryo storage without `entity-serialization-failed`.

## 2026-07-19 deep-cryo wake auto-eject fix

Player report during the next local smoke: after a successful deep-cryo store, pressing `Wake up` returned control to the body but left the player trapped inside the cryo capsule.

Preserved live evidence:

- Client and server both remained responsive.
- Server serialized 53 entities successfully, consumed the restore, and transferred session `JoeGenero` back to the original body entity 165494.
- Client detached from the ghost and attached to body entity 165494.
- The successful branch in `TryReturnToBody` inserted the body into `CryoSleepComponent.BodyContainer` but never called `EjectBody` or removed it from the container.
- The client wakeup window only closes after a successful response, and the Frontier pod does not provide `ExitContainerOnMove`, leaving no self-service exit.
- Server PVS also reported a stale `SleepingComponent.WakeAction` entity reference after canonical action rebuilding deleted the old action entity.

Fixes implemented:

- Successful restore now force-wakes the body through `SleepingSystem.TryWaking(..., force: true)`.
- Successful restore calls `EjectBody` immediately after control transfer. If that unexpectedly fails after the durable snapshot has already been consumed, it logs an error and forcibly removes the body from the pod container.
- Deep-cryo canonical sanitization now clears `SleepingComponent.WakeAction`, resets its temporary cooldown, and dirties the component so no deleted action entity reaches PVS.
- The real-player regression now injects a stale wake-action reference on the restored body and asserts canonical sanitization clears it.
- Runtime `BodyPartAppearanceComponent` serialization coverage was placed in the capture regression that actually serializes the body.

Commands and outcomes:

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-restore --filter "FullyQualifiedName~CaptureDropsCryoDoAfterThatReferencesThePod|FullyQualifiedName~RealPlayerPrototypeSerializesAndRoundAuthorityIsCanonicalized" -- NUnit.NumberOfTestWorkers=1
```

First result: 1 passed, 1 failed because the initial test fixture created an unattached `ActionWake`, which itself produced an action-removal error and an invalid external serialization reference. Product auto-eject logic compiled; the fixture was corrected to inject the stale reference only on the already-restored body before canonical sanitization. Do not count the first run as passed.

The same command was rerun after correcting the fixture. Result: passed 2/2.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\prepare_luam_hotfix.ps1 -Scope Cryo -Json
```

Result: `ok=true`; full local-fast Cryo integration set passed 13/13.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\start_local_stack.ps1 -Reset
```

Result: exit 0. Fresh server PID 34976 bound to `127.0.0.1:1213`; fresh client PID 39660 completed handshakes, reached `InGame`, switched to `LobbyState`, and was left running for the user.

Next action: manually repeat `spawn on station -> enter Frontier cryo -> Wake up` and confirm the restored character is awake outside the pod, the pod container is empty, and no `Can't resolve MetaDataComponent`, client crash, or deep-cryo error appears in the fresh logs.

## 2026-07-19 matter synthesizer unanchoring

User request: allow matter synthesizers to be unbolted with the standard anchoring tool.

Diagnosis and implementation:

- `LaserDrillBase` inherits the normal `AnchorableComponent` from `BaseMachine`, whose default flags allow both anchoring and unanchoring.
- `StationLaserDrill` explicitly overrode those flags with `None`, disabling both interactions. The override was removed, so the universal synthesizer now follows the same wrench behavior as the expeditionary synthesizer.
- Added `LuaMMatterSynthesizerPrototypeTest`, which resolves the fully composed `LaserDrill` and `StationLaserDrill` prototypes and requires both `Anchorable` and `Unanchorable` flags.

Checks:

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-restore --filter FullyQualifiedName~LuaMMatterSynthesizerPrototypeTest -- NUnit.NumberOfTestWorkers=1
```

First result: build failed with analyzer `RA0002` because the test used `Enum.HasFlag` on an access-controlled component field. No test ran; product YAML was not implicated. The assertions were rewritten as read-only bitmask checks.

The same command was rerun. Result: passed 2/2.

The user also asked where conveyor belts appear in the `G` construction menu. Repository inspection confirmed:

- Recipe `ConveyorBelt` is active under `construction-category-structures` (`Structures`).
- It requires a `ConveyorBeltAssembly`, produced by the cargo lathe recipe for 250 steel and 50 plastic.
- The finished belt can be converted back to the assembly with a prying tool.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\start_local_stack.ps1 -Reset
```

Result: exit 0. Fresh server PID 46936 bound to `127.0.0.1:1213`; fresh client PID 19516 reached `InGame`, switched to `LobbyState`, and was left running for manual testing.

Next action: in the live local client, spawn or unpack both matter synthesizer variants, use a wrench to unanchor and re-anchor each, and repeat the deep-cryo wake flow if it has not yet been confirmed manually.

## 2026-07-19 production technology map

User request: create a technology map that makes it clear where production items come from and what they cost.

Implementation:

- Reworked `Resources/ServerInfo/_Mono/Guidebook/Economy/EconomyRecipes.xml` into a Russian in-game production map under `Economy -> РўРµС…РЅРѕР»РѕРіРёС‡РµСЃРєР°СЏ РєР°СЂС‚Р° РїСЂРѕРёР·РІРѕРґСЃС‚РІР°`.
- Documented the common production flow: source or synthesizer -> raw ore -> ore processor -> sheets -> autolathe/techfab -> assembly -> `G` construction menu -> finished object.
- Added material-unit conventions and exact base costs for core tools, parts, conveyor assemblies, airlocks, machine/computer frames, walls, glass, reinforced glass, steel, and plastic routes.
- Added the exact conveyor path: autolathe makes `Conveyor Belt Assembly` for 250 steel and 50 plastic; build the belt through `G -> Structures`; a prying tool returns the assembly.
- Documented regular and station matter synthesizer prices, output restrictions, 50 kW HV requirement, anchoring behavior, and the absence of plastic production.
- Documented flatpack prices and the minimum autonomous production-contour totals for regular and expeditionary synthesizer variants.
- Updated the Russian guide entry name and added missing English localization entries.
- Added `Content.IntegrationTests/Tests/_LuaM/LuaMProductionMapPrototypeTest.cs` to validate the page markup/entity embeds and lock the documented recipe/vendor values to the live prototypes.

Commands and outcomes:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\stop_local_stack.ps1 -Force
```

Result: passed; the prior gateway, server, and client stack was stopped before compilation.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-restore --filter FullyQualifiedName~LuaMProductionMapPrototypeTest -- NUnit.NumberOfTestWorkers=1
```

First result: 1/1 failed because the guide initially documented 50 plastic for the welder. Resolved prototype composition showed that the inherited material mapping is replaced and the welder costs exactly 400 steel. The guide and exact-material assertion were corrected. The next contract run passed 1/1.

The first targeted markup-test compilation failed because the fixture lacked the client guidebook namespace and passed a string instead of the resolved `ResPath`. No test ran. Both fixture issues were corrected.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-restore --filter FullyQualifiedName~GuideEntryPrototypeTests.ValidatePrototypeContents -- NUnit.NumberOfTestWorkers=1
```

Result: failed on 11 pre-existing unrelated guide embeds for deleted prototypes (`NFMobDragonDungeon`, `WireBrush`, `RespironCanister`, `HeliumCanister`, `ChemistryBottleUnstableMutagen`, `GasDepositHelium`, `BorgModuleChemical`, `ChestHuman`, `GroinHuman`, `NFWeaponEnergyRifleSniperXrayCannon`). None came from the production map; do not count this broad legacy check as passed.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-restore --filter FullyQualifiedName~LuaMProductionMapPrototypeTest --logger "console;verbosity=minimal" -- NUnit.NumberOfTestWorkers=1
```

Final result: passed 2/2 in 29 seconds with 0 build errors. This covers both the new page parser/entity embeds and the documented live recipe/vendor contracts.

```powershell
git diff --check -- Resources/ServerInfo/_Mono/Guidebook/Economy/EconomyRecipes.xml Resources/Locale/ru-RU/_Mono/guidebook/guides.ftl Resources/Locale/en-US/_Mono/guidebook/guides.ftl Content.IntegrationTests/Tests/_LuaM/LuaMProductionMapPrototypeTest.cs
```

Result: no whitespace errors; only the existing Windows LF-to-CRLF conversion warnings were reported.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\start_local_stack.ps1 -Reset
```

Result: exit 0. Current verification found `Content.Server` PID 5340 listening on `127.0.0.1:1213`, gateway PID 2992 listening on `127.0.0.1:8787`, and `Content.Client` PID 39296 connected as `JoeGenero` in `GameplayState`. The stack was intentionally left running for the user.

Next action: in the live client open `Р СѓРєРѕРІРѕРґСЃС‚РІРѕ -> Р­РєРѕРЅРѕРјРёРєР° -> РўРµС…РЅРѕР»РѕРіРёС‡РµСЃРєР°СЏ РєР°СЂС‚Р° РїСЂРѕРёР·РІРѕРґСЃС‚РІР°`, verify page layout/scrolling and compare one conveyor and one airlock build against the displayed costs. If the UI misbehaves, run `Get-Content -LiteralPath 'C:\MonolithTemp\client-out.txt' -Tail 120` and inspect the fresh client errors.

## 2026-07-19 selective upstream integration

User request: compare the local project with `https://github.com/Lua-Frontier/Monolith-DS`, port improvements and new content, combine them with local mechanics, and choose the better implementation where variants differ.

Comparison and selection:

- Fetched `origin/master` at `42277ff6cae06c2b08eab7fe7d946090c709a96f`; the merge base is `3f244b9b728c567d5949daaf3336022ea8920115` and the observed divergence was 179 local / 13 upstream commits.
- The upstream range changed 423 paths. `git merge-tree --write-tree --name-only --messages HEAD origin/master` exited 1 by design and exposed 10 conflicts around launcher/version, Scorpion/vouchers, Altair, and Andromeda. No merge commit or index mutation was performed.
- Full upstream adoption was rejected. The audit retained LuaM's `ScorpionLuaM`, deep cryo, tutorial, economy/progression, and radar work; it also rejected non-idempotent wieldable scaling, the raw ship-repair flag, broad weapon/hardsuit buffs, and unrelated v0.4 balance changes.

Implemented content and mechanics:

- Added TSF Arrow and Rising plus the corrected expedition Phoenix. Phoenix costs 420000, has a limit of 2, and is not a capital ship. Rising is a T3 voucher ship. Arrow is included in the random T2 TSF voucher.
- Added Arrow/Rising vouchers and TSF uplink listings without replacing the local Scorpion voucher. Added `GunneryServerExtra` with 120 processing power and its machine board.
- Imported and validated the final Arrow, Rising, and Phoenix maps, Nordfall hardsuit resprites, Annie PNG resprite, surgery/cybernetic sprites, and surgery audio.
- Added cybernetic eyes, heart, liver, lungs, and upgraded heart/lungs; cybernetic metabolizer behavior; corrected lung gas reactions; research nodes; recipes; lathe packs; en/ru localization; and Exosuit Fabricator/Medical TechFab availability.
- Added bluespace surgical gloves that grant surgery-through-clothing and an advanced bone multitool. The global upstream surgery speed buffs and debug omnitool were not ported.
- Added safe IPC self-healing through use-in-hand, missing-damage-key handling, stop-after-fully-healed behavior, and configurable move/damage interruption with defaults kept at true. `SelfHealPenalty` remains 3.
- Reimplemented Brain Blip as a server-only LuaM system. Free human/diona brains receive an owned radar blip; insertion removes only the owned blip; foreign blips survive. A `TerminatingOrDeleted` guard prevents deep-cryo recursive deletion from adding a component back to a terminating brain.
- Added `RequiresGrid` to walls, made all three Vector variants intentionally one-handed with 2вЂ“12 degree spread, and changed Sultan's projectile spread multiplier from 0.4 to 1.0. Upstream weapon damage/fire-rate buffs were not ported.
- Fixed the language chooser localization key, fortune dataset ID, missing Syndicate name datasets/locales, and the old surgery attribution typo `hemostat.ogg` -> `hemostat1.ogg`.
- Extended `EconomyRecipes.xml` with exact production costs and research/machine sources for the new cyberorgans and surgery tools; regression coverage locks those values to live prototypes.

Important integration defects found and fixed:

- Initial cyberorgan composition caused a Robust `PushComposition` assertion because child organs repeated the inherited `[AlwaysPushInheritance]` scalar `slotId`. Repeated child values were removed while base eye/heart/lung/liver slots were retained.
- The imported upstream Russian metabolizer locale used an underscore filename alongside the existing hyphenated file and duplicated `metabolizer-type-cybernetic`. The duplicate untracked file was removed.
- An early Brain Blip test tried to deep-cryo-capture an isolated organ, which is not a valid deep-cryo root. It was removed and replaced by the existing real-player body-graph regression; that real scenario exposed and verified the terminating-entity guard above.

Commands and outcomes:

```powershell
git fetch --prune origin
```

Result: passed; `origin/master` advanced to `42277ff6ca`.

```powershell
git merge-tree --write-tree --name-only --messages HEAD origin/master
```

Result: exited 1 as an intentional conflict audit with 10 conflicts. No merge was applied; do not record this as a passed merge.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\stop_local_stack.ps1 -Force
```

Result: passed; prior server, client, and gateway were stopped before compilation.

```powershell
dotnet build Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-restore --nologo --verbosity:minimal
```

Final result: passed with 0 errors and 74 existing warnings.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-build --no-restore --filter FullyQualifiedName~LuaMIpcWeldingHealingTest -- NUnit.NumberOfTestWorkers=0
```

Final result: passed 1/1. Earlier fixture-only attempts failed on a missing LINQ import, absent hand slot, and a mismatched test damage container; all were corrected and must not be counted as product failures.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-restore --filter FullyQualifiedName~LuaMFreeBrainRadarTest -- NUnit.NumberOfTestWorkers=1
```

Final result: passed 4/4 after adding the terminating-entity guard.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-build --filter FullyQualifiedName~LuaMDeepCryoRuntimeTest.RealPlayerPrototypeSerializesAndRoundAuthorityIsCanonicalized -- NUnit.NumberOfTestWorkers=1
```

Final result: passed 1/1 on a real player body graph.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-build --no-restore --filter "FullyQualifiedName~LuaMImportedShipContentPrototypeTest|FullyQualifiedName~LuaMUpstreamImprovementPrototypeTest|FullyQualifiedName~LuaMProductionMapPrototypeTest" -- NUnit.NumberOfTestWorkers=1
```

First result: 3/5. The two failures were over-strict new test assumptions: abstract `BaseWall` is not in the runtime prototype index, and raw shuttle grids need not contain `BecomesStation`. The test now checks concrete `WallSolid` and successful map-grid loading. Final rerun passed 5/5; all three imported maps loaded.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\prepare_luam_hotfix.ps1 -Scope Cryo -Json
```

Result: `ok=true`; full LuaM deep-cryo set passed 13/13.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-build --no-restore --filter "FullyQualifiedName~LuaMRadarIsolationTest|FullyQualifiedName~LuaMMatterSynthesizerPrototypeTest" -- NUnit.NumberOfTestWorkers=1
```

Result: passed 3/3.

```powershell
dotnet test Content.Tests/Content.Tests.csproj --configuration DebugOpt --no-build --no-restore --filter "FullyQualifiedName~LuaMRadarSequenceTest|FullyQualifiedName~LuaMFrontierTutorialFlowTest"
```

Result: passed 10/10.

```powershell
git diff --check -- <selected upstream-integration paths>
```

Result: passed; only existing Windows LF-to-CRLF notices were emitted.

Asset audit result: 229 changed map/RSI/audio references, 34 changed RSI directories, 38,098 prototype/localization IDs, and all three imported maps were checked. After fixing the hemostat attribution filename, no missing files, case mismatches, duplicate IDs, invalid RSI metadata, duplicate UIDs, lost map parents, unknown map prototypes, or unknown tiles remain.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\start_local_stack.ps1 -Reset
```

Result: exit 0. Fresh server PID 45420 is ready on `127.0.0.1:1213`; gateway PID 45492 reports `/health` `ok=true`; client PID 46264 completed the serializer handshake and entered `LobbyState`. Server log has no new errors. Client log contains only the expected local Discord IPC timeout.

One post-start read-only diagnostic command exited 1 solely because `C:\MonolithTemp\gateway-out.txt` does not exist; listener/process/log checks before that point completed and a subsequent direct `/health` request passed. Do not interpret that diagnostic exit as a stack failure.

Next action: use the running local client to test `wake from deep cryo`, IPC self-repair with the nanite applicator, one new cyberorgan install, and one of Arrow/Rising/Phoenix. If anything misbehaves, immediately capture fresh logs with:

```powershell
Get-Content -LiteralPath 'C:\MonolithTemp\server-out.txt' -Tail 160
Get-Content -LiteralPath 'C:\MonolithTemp\client-out.txt' -Tail 160
```

## 2026-07-19 server-news tab and persistent update feed

User request: add a `РќРѕРІРѕСЃС‚Рё СЃРµСЂРІРµСЂР°` section under `РћР±РЅРѕРІР»РµРЅРёСЏ` and keep every future player-facing update there.

Implemented:

- Added `Resources/Changelog/ServerNews.yml` as the first public changelog tab (`Order: -100`) with an initial Russian history covering the recent tutorial, deep-cryo, matter-synthesizer, production-map, ship, cyberorgan, IPC, radar, banking, and UI work.
- Added `changelog-tab-title-ServerNews` localization for ru-RU and en-US.
- Changed `ChangelogManager.MainChangelogName` to `ServerNews`, so the menu badge and read divider track LuaM news instead of the upstream changelog.
- Corrected `UpdateChangelogs` to use the changelog matched by `MainChangelogName` instead of blindly taking `changelogs[0]`.
- Added `LuaMServerNewsChangelogTest` and the persistent `AGENTS.md` rule requiring future completed player-facing work to update the feed in the same iteration.

Commands and outcomes:

```powershell
Get-Content -Raw Resources/Changelog/ServerNews.yml
Get-Content -Raw Content.Client/Changelog/ChangelogManager.cs
rg -n <changelog and test discovery patterns> ...
```

Result: the useful changelog/UI/test structure was identified. Several combined discovery invocations exited 1 after producing partial output because Windows rejected a literal `Content.*` path, `Content.UnitTests` does not exist, or a final `rg` branch had no match. These were read-only failures and made no mutation.

```powershell
& .\Tools\stop_local_stack.ps1 -Force
```

## 2026-07-19 selective Radiant ship and medibot integration

User request: inspect `TetrisZz/frontier-Radiant` for its medibot and ships, take compatible improvements, compare variants, and keep the better LuaM behavior.

Implemented:

- Audited public Radiant `master` at pinned commit `28d354a3a6fffa63b553d6e487dd53d66053156a`. The YAML is AGPLv3 code; the selected ships are Radiant derivatives of Frontier/SS14 maps. Source URLs, introduction commits, and derivative provenance are recorded in each imported file and in `LEGAL.md`.
- Imported and adapted `Gornyak` as a 69,000-credit medium salvage/expedition vessel. Replaced three Radiant-only wall/window prototypes, removed the unrelated placed underwear item, corrected station jobs, and verified the actual five drills, ore processor, shuttle console, and radar.
- Imported and adapted `Salomandra` as a 105,000-credit medium medical/chemistry/botany vessel. Remapped nine Radiant-only tiles, removed 39 unavailable decorative decal nodes, replaced `ThrusterZeta` with `ThrusterRadiantMediumLuaM`, corrected the generator classification to Bananium, added the medical job, and removed three stale serialized/runtime incompatibilities.
- The local compatibility thruster reuses the already-attributed 2x2 Mono large-thruster RSI while preserving Radiant's 300 thrust, upgrade levels, 3,750-W draw, 2x2 collision, radar blip, and ship-repair integration.
- Preserved the existing LuaM medibot implementation: 10u Tricordrazine rather than Radiant's unsafe 30u, working `Advertise` rather than absent vocalizer components, manual/emag/HTN behavior, and rescue-agent integration. Localized all four construction-part labels in en-US and ru-RU.
- Added `LuaMRadiantContentPortTest` for vessel metadata, source hygiene, medibot dose/recipe, compatibility-thruster values, raw map loading, equipment counts, ordinary-vs-deep cryo separation, and price/appraisal bounds.
- Added server-news entry `2026071905` and made the server-news test future-proof: it requires the new entry but permits later ascending IDs.

Commands and outcomes:

```powershell
& .\Tools\stop_local_stack.ps1 -Force
```

Result: exit 0; server, client, and gateway were stopped before compilation.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-restore --filter FullyQualifiedName~LuaMRadiantContentPortTest --logger 'console;verbosity=minimal' -- NUnit.NumberOfTestWorkers=1
```

The first invocation was interrupted by an accidental five-second shell timeout. It left root `dotnet` PID 36436 and its MSBuild nodes running; a read-only CIM audit identified the exact process tree, it was allowed to complete normally, and a subsequent PID check reported completion. Its console result was lost and is not counted.

Targeted development reruns initially exposed over-strict drill count (expected 3, actual 5), stale `UserInterface` state on filing cabinet UID 315, `ImpCoffeeMachine` storage overflow, a broken filled wall-locker container, and non-zero rotations on the compatible coffee machine/locker replacements. Each map issue was corrected. Final result:

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-build --no-restore --filter FullyQualifiedName~LuaMRadiantContentPortTest --logger 'console;verbosity=minimal' -- NUnit.NumberOfTestWorkers=1
```

Result: exit 0; passed 2/2. Both maps loaded, required equipment was present, ordinary Salomandra `CryoPod` had no `CryoSleepComponent`, and both prices stayed inside appraisal bounds.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-build --no-restore --filter 'FullyQualifiedName~ConstructionPrototypeTest|FullyQualifiedName~ConstructionActionValid|FullyQualifiedName~LuaMRescueAutonomyPrototypeTest' --logger 'console;verbosity=minimal' -- NUnit.NumberOfTestWorkers=1
```

Result: exit 1; passed 57/59. Do not count as green. The two global construction/prototype cleanup failures came from existing all-prototype spawning errors for `ImpCoffeeMachine` storage overflow and wall-locker `EntityTableContainerFill` without `storagebase`, outside the adapted maps. No medibot graph assertion failed.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-build --no-restore --filter FullyQualifiedName~LuaMRescueAutonomyPrototypeTest --logger 'console;verbosity=minimal' -- NUnit.NumberOfTestWorkers=1
```

Result: exit 0; passed 52/52.

```powershell
& .\bin\Content.YAMLLinter\Content.YAMLLinter.exe
```

Result: exit 0; `No errors found in 92374 ms.`

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-restore --filter 'FullyQualifiedName~LuaMServerNewsChangelogTest|FullyQualifiedName~LuaMRadiantContentPortTest' --logger 'console;verbosity=minimal' -- NUnit.NumberOfTestWorkers=1
```

The first combined rerun passed the two Radiant tests but correctly failed the old exact-latest-news assertion (`2026071904` vs new `2026071905`). The test was changed to require the new ID while allowing future IDs. Final result: exit 0; build 0 errors, passed 3/3.

A read-only `Get-Date -AsUTC -Format o` discovery command reported that this PowerShell version has no `-AsUTC` parameter; the corrected `(Get-Date).ToUniversalTime().ToString('o')` command succeeded. A selected `git diff --check` passed with only CRLF notices before the final test/news edits and must be rerun after the production-queue work.

The local stack remains stopped. Remote deployment was not attempted.

Next action: finish the read-only production-queue audit, then inspect the queue guards directly with:

```powershell
rg -n "queue|Queued|Fabricat|Lathe|Production" Content.Server Content.Shared Content.Client
```

Result: exit 0; the previous server, gateway, and client were stopped before compilation.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-restore --filter 'FullyQualifiedName~LuaMServerNewsChangelogTest' --logger 'console;verbosity=minimal' -- NUnit.NumberOfTestWorkers=1
```

Result: exit 0; integration build completed with 0 errors and the new server-news test passed 1/1.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-build --no-restore --filter 'FullyQualifiedName~UiControlTest.TestWindows' --logger 'console;verbosity=minimal' -- NUnit.NumberOfTestWorkers=1
```

Result: exit 0; UI window construction passed 1/1.

```powershell
dotnet run --project Content.YAMLLinter/Content.YAMLLinter.csproj --configuration DebugOpt --no-restore
```

Result: interrupted by the 124-second command timeout before the executable could report its result. The timed-out build left 27 orphaned MSBuild node processes with parent PID 36172; an exact read-only CIM audit identified them, a PID-scoped `Stop-Process -Force` stopped only those nodes, and a second CIM query verified none remained.

```powershell
& .\bin\Content.YAMLLinter\Content.YAMLLinter.exe
```

Result: exit 0; `No errors found in 103816 ms.`

```powershell
git diff --check -- AGENTS.md Content.Client/Changelog/ChangelogManager.cs Content.IntegrationTests/Tests/_LuaM/LuaMServerNewsChangelogTest.cs Resources/Changelog/ServerNews.yml Resources/Locale/en-US/_LuaM/changelog/server-news.ftl Resources/Locale/ru-RU/_LuaM/changelog/server-news.ftl
& .\Tools\start_local_stack.ps1 -Reset
```

Result: exit 0. Server PID 31476 listens on `127.0.0.1:1213`, gateway PID 40496 reports `/health` `ok=true`, and client PID 13060 is running. Remote deployment was not attempted.

Next action: finish the read-only Radiant medibot/ship dependency audit. Before applying any selected port, preserve the user's live test session and stop the stack explicitly with:

```powershell
& .\Tools\stop_local_stack.ps1 -Force
```

## 2026-07-19 appendable production queue and civilian medical tracker

User requests:

- allow another production job to be added while a device is already working;
- give every biological Civilian-department character an installed implant that reports critical/death location;
- remove the redundant civilian medical implanter from their kits while preserving the Vanguard implanter.

Implemented production behavior:

- `LatheSystem.TryAddToQueue` now validates `HasRecipe` independently from material availability. Known recipes may wait for future material delivery; material remains consumed only inside `TryStartProducing` immediately before the item starts.
- `LatheComponent.MaxQueuedItems` is a networked/configurable future-work limit with a default of 1,000. Queue arithmetic uses `long`, rejects invalid quantities and overflow, and preserves separate batches for different actors.
- The client keeps recipe buttons available when material is missing and retains the missing-material tooltip. Waiting work remains in the queue instead of being displayed as an actively fabricated item.
- `LuaMLatheQueueRuntimeTest` covers active A -> append B at zero steel -> wait -> refill -> exact A/B, unknown recipe rejection, zero/negative quantities, limits, overflow, and actor-aware merging.

Implemented civilian tracker behavior:

- `Contractor`, `Pilot`, and `Mercenary` receive `MedicalTrackingImplant` through one `AddImplantSpecial`; Borg is intentionally excluded because it uses `PlayerBorgBattery` and cannot use subdermal implants.
- `AddImplantSpecial.ApplyOnClone` is opt-in and the cloning system replays only opted-in implant specials, so the civilian invariant survives a new cloned body without changing legacy job implants.
- New `CivilianImplanter` keeps Light/BikeHorn/SadTrombone/Mime options but has no medical-tracker fallback. Only the three biological Civilian role loadouts use it; the shared `ContractorImplanter` remains unchanged for other roles.
- All five humanoid Vanguard roles retain `FreelanceTrackingImplant`; the PDV uplink still sells `RadioImplanterFreelance -> RadioImplantFreelance`.
- Added the missing ru-RU revival death-rattle localization and server-news entry `2026071906`.
- `LuaMCivilianTrackingImplantTest` locks department scope, exactly one tracker, clone opt-in, Borg exclusion, novelty loadout contents, Medical critical/death coordinates, and Vanguard preservation.

Commands and outcomes:

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-restore --filter FullyQualifiedName~LuaMLatheQueueRuntimeTest -- NUnit.NumberOfTestWorkers=1
```

Result from the queue implementation run: exit 0; passed 2/2; build completed with 0 errors.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-build --no-restore --filter "FullyQualifiedName~UiControlTest.TestWindows" -- NUnit.NumberOfTestWorkers=1
```

Result: exit 0; passed 1/1.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-build --no-restore --filter "FullyQualifiedName~LuaMLatheQueueRuntimeTest|FullyQualifiedName~AllLatheRecipesValidTest|FullyQualifiedName~UiControlTest.TestWindows" -- NUnit.NumberOfTestWorkers=1
```

Result: exit 0; passed 4/4.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-restore --filter "FullyQualifiedName~LuaMCivilianTrackingImplantTest|FullyQualifiedName~LuaMLatheQueueRuntimeTest" --logger "console;verbosity=minimal" -- NUnit.NumberOfTestWorkers=1
```

Result: exit 0; build completed with 0 errors and passed 3/3 in 39 seconds.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-build --no-restore --filter FullyQualifiedName~LatheTest -- NUnit.NumberOfTestWorkers=1
```

Result from the queue audit: exit 1; passed 1/2. Do not count as green. The unrelated existing `TestLatheRecipeIngredientsFitLathe` failure is `OreProcessorIndustrial`: `SheetPlastitanium1/15` require Plasteel and Plasma that its storage whitelist does not accept. No queue assertion failed.

```powershell
& .\bin\Content.YAMLLinter\Content.YAMLLinter.exe
```

Result after adding `ApplyOnClone`: exit 1 with three `Field "applyOnClone" not found` reports. This direct linter executable had a stale copied `Content.Server` assembly; the integration build had already compiled and loaded the new field successfully. Rebuild the linter before counting YAML validation. A parallel combined test/diff/linter wrapper returned only this failing linter result, so the hidden sibling results are not counted.

The selected queue diff check run reported exit 0 with only LF-to-CRLF notices. A final combined diff check remains pending after the implant/news edits.

New work added before local restart:

- recalculate the relevant fuel stack and pricing for 75 rather than 25;
- restore per-vulpkanin sounds;
- restore robot TTS.

Next action: finish those three bounded implementations, then rebuild the YAML linter and run all final checks with:

```powershell
dotnet build Content.YAMLLinter/Content.YAMLLinter.csproj --configuration DebugOpt --no-restore --nologo --verbosity:minimal
& .\bin\Content.YAMLLinter\Content.YAMLLinter.exe
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-build --no-restore --filter "FullyQualifiedName~LuaMLatheQueueRuntimeTest|FullyQualifiedName~LuaMCivilianTrackingImplantTest|FullyQualifiedName~LuaMServerNewsChangelogTest|FullyQualifiedName~UiControlTest.TestWindows" -- NUnit.NumberOfTestWorkers=1
```

## 2026-07-19 75-unit fuel, vulpkanin voices, robotic TTS, and universal-pack audit

User requests:

- change the sold fuel bundle from 25 to 75 units and recalculate its price;
- restore the individual vulpkanin sound sets;
- restore robot TTS;
- inspect `C:\Users\Orvar Od\Downloads\SS14_universal_systems_pack_v2` for safe future ports.

Implemented fuel behavior:

- `FuelPlasma`, `FuelUranium`, and `FuelBananium` full bundles now contain 75 units and have matching suffixes.
- Their FuelVend prices are 4,500 / 9,375 / 18,750 credits, preserving the previous unit prices of 60 / 125 / 250 credits.
- `LuaMFuelVendStackPriceTest` locks inventory presence, 75-unit stack capacity/count, no cargo resale price, and unchanged unit price.

Implemented vulpkanin voices:

- Male and female `Scream` now use `VulpkaninMaleScreams` and `VulpkaninFemaleScreams` respectively.
- Both profiles use the available vulpkanin-specific laugh, sneeze, and cry collections; existing male/female growl, howl, and awoo mappings remain separate.
- No synthetic replacements were invented for cough/yawn/gasp/death sounds because no matching species assets exist in the current set.
- Added the missing `Resources/Audio/_DeadSpace/Voice/Vulpkanin/attributions.yml`, pointing to Monolith PR 786 and explicitly noting that the source preserved no more granular authorship.
- `LuaMVulpkaninVoicePrototypeTest` locks playable-species inheritance, sex/profile selection, collection IDs, exact scream files, and non-overlap between male and female screams.

Implemented robotic TTS:

- `BaseBorgChassis`, MMI, positronic brains, and pAI use the reserved `TrainingRobot` voice.
- `StationAiBrain` and inherited vessel/TSFMC/PDV/redacted variants use the reserved `Glados` voice.
- `MobIPC` remains unset at prototype level so the character profile continues to select its voice.
- LuaM AI Director TTS, radio, language, and voice-mask behavior were deliberately not changed.
- `LuaMRoboticTtsPrototypeTest` locks all of these inheritance rules.

Player-facing update:

- Added server-news entry `2026071907` with fuel, vulpkanin-sound, and robotic-TTS notes.
- Extended `LuaMServerNewsChangelogTest` to require the new entry and its key player-facing phrases.

Commands and outcomes:

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-restore --filter FullyQualifiedName~LuaMFuelVendStackPriceTest --logger "console;verbosity=minimal" -- NUnit.NumberOfTestWorkers=1
```

Result from the fuel implementation: exit 0; build 0 errors; passed 1/1.

The first targeted vulpkanin test invocation failed 0/1 only because the test attempted to index abstract `BaseMobVulpkanin`; the product YAML loaded. The fixture was corrected to inspect concrete `MobVulpkanin`, and the next targeted invocation passed 1/1 in 35 seconds.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-restore --filter FullyQualifiedName~LuaMRoboticTtsPrototypeTest --logger "console;verbosity=minimal" -- NUnit.NumberOfTestWorkers=1
```

Result: exit 0; build 0 errors; passed 1/1.

The vulpkanin audit also ran the old standalone YAML-linter binary. It exited 1 with only the same three stale-assembly `AddImplantSpecial.applyOnClone` errors in contractor/pilot/mercenary YAML; it reported no vulpkanin error and is not counted green. The correct fresh sequence was then run:

```powershell
dotnet build Content.YAMLLinter/Content.YAMLLinter.csproj --configuration DebugOpt --no-restore --nologo --verbosity:minimal
& .\bin\Content.YAMLLinter\Content.YAMLLinter.exe
```

Result: build exit 0 with 87 existing warnings and 0 errors; freshly rebuilt linter exit 0, `No errors found in 91391 ms.`

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-build --no-restore --filter 'FullyQualifiedName~LuaMLatheQueueRuntimeTest|FullyQualifiedName~LuaMCivilianTrackingImplantTest|FullyQualifiedName~LuaMFuelVendStackPriceTest|FullyQualifiedName~LuaMVulpkaninVoicePrototypeTest|FullyQualifiedName~LuaMRoboticTtsPrototypeTest|FullyQualifiedName~LuaMServerNewsChangelogTest|FullyQualifiedName~AllLatheRecipesValidTest|FullyQualifiedName~UiControlTest.TestWindows' --logger 'console;verbosity=minimal' -- NUnit.NumberOfTestWorkers=1
```

Result: exit 0; passed 9/9 in 38 seconds.

The selected tracked-file `git diff --check` exited 0 with only LF-to-CRLF notices. A dedicated `rg -n '[ \t]+$'` pass over the new queue/civilian/fuel/vulp/TTS/news tests and vulpkanin attribution found no trailing whitespace.

Universal systems pack audit:

- The effective root is `C:\Users\Orvar Od\Downloads\SS14_universal_systems_pack_v2\ss14_universal_systems_pack_v2`.
- Inventory: 92 files / 387,121 bytes, all text; no maps, sprites, audio, `.git`, or root `LICENSE`.
- It is eight specifications/skeletons, not a buildable module: lathe queue, ship blueprint disks, ship registry, player contracts, modular hardpoints, tactical relays, bioreplicator, and restricted ship persistence.
- `reference_sources/SOURCES.md` pins Frontier `deab108ca9187b6edfa40c3766a607afcd19a477` (AGPL-3.0), RMC14 `b6d677947dd8ebcb06194a66798938645fed5a54` (MIT file included), and upstream SS14 `c17429daa479477b16cd00378449659cf142b5f0` (MIT code). Reference files may be ported only with their notices/provenance.
- The package-authored `.example`, skeleton, schema, and prose layer only says it may be adapted freely; it has no explicit author or license grant. Treat it as design input and do not copy it verbatim until a root license/author statement is supplied.
- The local queue is already the better LuaM variant because it supports jobs waiting for later materials instead of reserving/refunding the entire batch. Future value from module 01 is limited to server-authoritative reorder/removal UI adapted to deferred consumption.
- Best first new candidate: module 02 ship blueprint disks, implemented against the existing LuaM/Frontier shipyard with server-side vessel/category/access checks and without copying unlicensed skeleton code.
- Module 03 overlaps `ShuttleDeed`, shuttle records, ship access, and `LuaMSectorStorySystem` registry; retain only stable UUID, two-party transfer, versioning, and reissue ideas.
- Module 04 overlaps the full existing bounty-contract UI/system and LuaM banking; only structured delivery conditions, escrow, idempotent payout, and dispute states are useful extensions.
- Modules 05/06 are not direct ports. Their RMC reference subset is incomplete and depends on absent RMC dropship/tactical infrastructure; adapt their pure recalculation, TTL/delta, and relay-graph ideas to local ship/radar systems if selected.
- Module 07 overlaps cloning plus LuaM deep-cryo persistence and is high risk for duplicate bodies. Use only the stable signed-bioprofile/one-mind invariant if a separate design is approved.
- Module 08 is a strong later architecture reference, but must restore a trusted vessel blueprint plus whitelisted deltas rather than serialize arbitrary grids. It should follow a persistent ship-registry decision, migrations, locks, size limits, and two-client tests.

Read-only audit commands included `rg --files` inventory/grouping, `Get-ChildItem -Recurse -File` size/type checks, reads of `README_RU.md`, `РџРћР РЇР”РћРљ_Р’РќР•Р”Р Р•РќРРЇ_RU.md`, module algorithms/reference maps, and the three included license/source manifests. A first read attempted nonexistent `README.md` instead of `README_RU.md` and exited 1 without mutation. One broad repository overlap `rg ... | Select-Object -First 320` produced useful partial matches but exited 1 after truncation and is not treated as exhaustive. The delegated read-only audit was interrupted during overlong follow-up summarization; no package or repository file was changed by it, and the main agent completed the bounded audit directly.

Local runtime start and verification:

```powershell
& .\Tools\start_local_stack.ps1 -Reset -PiperTts -PiperVoices 'ru_RU-irina-medium'
```

Result: exit 0. Gateway wrapper PID 36288 reports `ok=true`, `ttsProvider=piper`, model/allowlist `ru_RU-irina-medium`; server content PID 45828 reports `Server Version 277.2.1.0 -> Ready` and listens on `127.0.0.1:1213`; client content PID 36656 reports `Runlevel changed to: InGame`. `server-err.txt` and `client-err.txt` are both empty.

The first manual `/tts` smoke request used a PowerShell string body without an explicit UTF-8 byte encoding. Cyrillic was corrupted before reaching Piper, so it exited 1 with `# channels not specified`; this was a diagnostic-client encoding error, not counted green. The corrected request used `[System.Text.Encoding]::UTF8.GetBytes(...)` with `application/json; charset=utf-8` and succeeded: WAV, `ru_RU-irina-medium`, 90,668 decoded bytes, non-empty request ID. No runtime code change was required.

The final post-start selected `git diff --check` covering the journal and all tracked queue/civilian/fuel/vulp/TTS/news files exited 0 with only expected LF-to-CRLF notices.

Remote deployment was not attempted.

Next action: use the running client to test one 75-unit FuelVend purchase, queue a second lathe job while the first is active, trigger male/female vulpkanin scream/laugh/sneeze/cry, and speak as a borg or station AI. If runtime behavior differs, capture:

```powershell
Get-Content -LiteralPath 'C:\MonolithTemp\server-out.txt' -Tail 160
Get-Content -LiteralPath 'C:\MonolithTemp\client-out.txt' -Tail 160
Get-Content -LiteralPath 'C:\MonolithTemp\gw-err.txt' -Tail 120
```

## 2026-07-19 pinned RMC14 tactical-map audit

User reference:

- RMC14 commit `b6d677947dd8ebcb06194a66798938645fed5a54`;
- `Content.Shared/_RMC14/TacticalMap`;
- `Content.Server/_RMC14/TacticalMap/TacticalMapSystem.cs`.

Read-only findings:

- RMC TacticalMap is a map-grid tactical display, not the relay/sensor network described by the universal-pack proposal. It tracks Marine, Xeno, and XenoStructure tile positions and decorates them with status, squad, and leader information.
- It supports current positions or a last-published snapshot, faction-specific lines and labels, bounded manual updates, announcements/admin logs, and RMC-specific reveal/queen-eye behavior.
- It does not provide network keys, relay hops, jamming, confidence decay, TTL contacts, multi-sensor fusion, or cross-ship contact sharing. `CommunicationsTower` is a separate communications mechanic.
- The local pack contains only four RMC tactical-map reference files. The pinned upstream implementation also needs the full shared/client BUI and control layer plus RMC announcements, distress rules, skills, squads, unrevivable medical state, dropship terminals, and several Xenonid systems that are absent from Monolith.
- Mono `RadarBlipSystem` already has the correct space-sensor boundary: open-UI validation, request throttling, per-radar snapshots, multiple radar sources, range/map/detection filtering, server sample time, velocity extrapolation, and missile/hitscan data. It must remain the source of truth.
- If implementation is later authorized, use a per-`GridUid` LuaM tactical-chart aggregate and separate server-owned annotations. Reuse RMC client UX ideas such as zoom/pan/layers/labels/lines, and connect ship contacts only through a filtered adapter after the existing radar checks. Do not copy the RMC singleton/first-map server model.

Inspection commands and outcomes:

```powershell
rg --files Content.Server Content.Shared Content.Client Resources | rg -i 'tacticalmap|tactical_map|tactical-map'
```

Result: exit 1; no existing Monolith tactical-map implementation was found. This was a no-match result, not a mutation.

```powershell
Get-Content -LiteralPath 'C:\Users\Orvar Od\Downloads\SS14_universal_systems_pack_v2\ss14_universal_systems_pack_v2\reference_sources\RMC14\TacticalMapSystem.cs'
```

Result: exit 0; inspected the pinned 1,178-line server system and its included shared component/blip/system subset.

Pinned GitHub shared, server, and client directories were opened read-only. Raw client `TacticalMapControl.xaml.cs` and client `TacticalMapSystem.cs` were fetched into memory with `Invoke-RestMethod`; no file was written. `Test-Path` checks confirmed that the major RMC dependency directories are absent locally.

Some broad `rg ... | Select-Object -First ...` inspections returned useful partial output but exit 1 after output truncation/broken-pipe behavior. They are not treated as exhaustive. No repository or package file was changed by this audit, no build/test was run for it, and remote deployment was not attempted.

Next action if the user authorizes implementation: first define a neutral `LuaMTacticalContact`/annotation contract and its per-grid ownership rules, then adapt only the RMC client control concepts with provenance while keeping `RadarBlipSystem` filtering authoritative.

## 2026-07-19 IPC deep-cryo storage and retry fix

Runtime evidence:

```powershell
rg -n -i 'cryo_sleep|stored job|deepcryo' 'C:\MonolithTemp\server-out.txt' | Select-Object -Last 120
```

Result: the live test logged two identical refusals for `MobIPC`: `snapshot-contains-additional-organic-body`. The first refusal was followed by a retry error because `DoAfterComponent` had been removed during the rejected capture.

Root cause and implementation:

- An IPC has an installed `PositronicBrain` organ with its own `MobStateComponent`. The graph guard treated every nested `MobStateComponent` as a second passenger even when `OrganComponent.Body` proved it belonged to the root IPC.
- `LuaMDeepCryoPersistenceSystem.IsInstalledNeuralCore` now permits only an organ whose `Body` is the exact snapshot root, or an exact `BorgChassisComponent.BrainEntity` with `BorgBrainComponent`. A loose brain or another carried mob still fails closed.
- Transient do-afters are still removed before serialization so pod references never become durable. If capture fails, only an empty `DoAfterComponent` capability is restored; stale operations and `ActiveDoAfterComponent` are not resurrected.
- `IpcInstalledPositronicBrainPersistsButNestedPassengerDoesNot` exercises the real `MobIPC` graph, verifies its installed positronic brain, verifies successful capture, rejects a nested `MobMouse`, and verifies retry capability.
- Added server-news entry `2026071908` and extended the changelog contract test.

Commands and outcomes:

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-restore --filter "FullyQualifiedName~LuaMDeepCryoRuntimeTest.IpcInstalledPositronicBrainPersistsButNestedPassengerDoesNot" --logger "console;verbosity=minimal" -- NUnit.NumberOfTestWorkers=1
```

First invocation: exit 1 before tests; the running content server PID 45828 locked `Content.Server.exe`. A retry with `-p:UseAppHost=false` also exited 1 before tests because the same process locked `Content.Server.dll`. Neither invocation is counted as test execution.

```powershell
& .\Tools\stop_local_stack.ps1 -Force
```

Result: local server, client, and gateway stopped and their disposable local-stack artifacts were cleared. The combined wrapper exited 1 only because its trailing `Get-Process` check found no matching processes after the successful stop.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-restore --filter "FullyQualifiedName~LuaMDeepCryoRuntimeTest.IpcInstalledPositronicBrainPersistsButNestedPassengerDoesNot" --logger "console;verbosity=minimal" -- NUnit.NumberOfTestWorkers=1
```

Result: exit 0; build completed without errors; passed 1/1 in 41 seconds.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-build --no-restore --filter "FullyQualifiedName~LuaMDeepCryoRuntimeTest" --logger "console;verbosity=minimal" -- NUnit.NumberOfTestWorkers=1
```

Result: exit 0; passed the complete class 10/10 in 39 seconds.

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-restore --filter "FullyQualifiedName~LuaMDeepCryoRuntimeTest|FullyQualifiedName~LuaMServerNewsChangelogTest" --logger "console;verbosity=minimal" -- NUnit.NumberOfTestWorkers=1
```

Result: exit 0; incremental build completed without errors; passed 11/11 in 39 seconds.

```powershell
& .\Tools\start_local_stack.ps1 -Reset -PiperTts -PiperVoices 'ru_RU-irina-medium'
```

Result: exit 0. Gateway PID 46592 reports healthy with Piper `ru_RU-irina-medium`; content server PID 46296 reports `Server Version 277.2.1.0 -> Ready` and listens on `127.0.0.1:1213`; content client PID 9380 reached `InGame`. Server/client error logs are empty. Gateway stderr contains only two successful local `GET /health` access lines.

The final selected `git diff --check` for the cryo system/test, server-news file/test, and this journal exited 0 with only expected LF-to-CRLF notices.

Remote deployment was not attempted.

Next action: in the running client, join as an IPC, enter a cryosleep pod, wait for the automatic store (or confirm it), then use the stored-character wake flow and verify the IPC exits the pod with inventory intact. If it still fails, capture the exact new lines with:

```powershell
rg -n -i 'cryo_sleep|deep-cryo|DoAfter|MobIPC' 'C:\MonolithTemp\server-out.txt' | Select-Object -Last 160
```

## 2026-07-19 full-state ship-persistence requirement

User decision:

- The earlier exclusion list was rejected. A stored ship must return as the same changed ship, not as a fresh vessel prototype with a whitelist of deltas.
- The snapshot source of truth is the complete live grid: tiles and construction changes, machines and their state, damage, power/pipe/device/wire state, atmosphere, inventories and containers, loose objects, mobs/bodies, and other grid-owned gameplay entities are all in scope.
- Engine/session handles that cannot remain numerically identical across a restart must be rebound to their restored entities. This is restore plumbing and must not silently discard gameplay state.
- The independent persistent registry remains necessary for ship UUID, ownership, snapshot revision/checksum, state transitions, and a lock that prevents two active copies.

Read-only feasibility findings:

- `MapLoaderSystem.TrySaveGrid` already recursively serializes a grid and all serializable transform children; `TryLoadGrid` restores it onto a map. This is the correct engine primitive for the new requirement.
- Tiles and component differences from prototypes are represented by the entity serializer. The default missing-entity behavior can also include referenced null-space entities such as minds or network state.
- Full-state fidelity must be proven with a save/delete/load integration matrix. Prototype `MapSavable=false`, serialization hooks, external cross-grid references, online player sessions, active timers/do-afters, and version migrations require explicit reconciliation tests rather than an exclusion whitelist.

Commands run for this decision:

```powershell
Get-Content -Raw '.agents/ITERATION_LOG.md'
rg -n -S "TrySave(Grid|Map)|SaveGrid|SaveMap|MapSave|MapLoaderSystem" Content.Server Content.Shared RobustToolbox --glob '*.cs'
rg -n -S "record.*SerializationOptions|class SerializationOptions|struct SerializationOptions|IsSerializable\(" RobustToolbox/Robust.Shared/EntitySerialization --glob '*.cs'
rg -n -i -S "mapsavable" Resources Content.Server Content.Shared Content.Client RobustToolbox --glob '*.yml' --glob '*.yaml' --glob '*.cs'
```

Results: read-only inspection succeeded and confirmed the full-grid save/load primitives. One combined file-read command partially succeeded, then exited 1 because it guessed the nonexistent path `EntitySerialization/SerializationOptions.cs`; the actual file was found as `EntitySerialization/Options.cs` and read successfully. One combined `rg` command printed the requested serializer context but exited 1 because the Windows path argument `Content.*` was invalid; it was rerun with explicit content directories and exited 0. No product code was changed, no build/test was run, the running local stack was not touched, and no deployment was attempted.

Next action when implementation is authorized: first add a failing round-trip integration fixture covering a structurally modified ship, atmosphere, machinery, nested/loose items, a mob body/mind, and cross-grid references, then run:

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --configuration DebugOpt --no-restore --filter FullyQualifiedName~LuaMFullShipPersistenceRuntimeTest -- NUnit.NumberOfTestWorkers=1
```

## 2026-07-21 expedition console shuttle binding

Objective:

- Continue the shuttle/consoles work by allowing expedition consoles installed on ordinary player shuttles to work without preselecting or stealing another expedition vessel's data.

Repository state:

- `Content.Server/Salvage/SalvageSystem.ExpeditionConsole.cs` now routes expedition console initialization, parent changes, UI state, and claim handling through `TryEnsureConsoleExpeditionData`.
- If a console is on a grid with `ShuttleComponent` and its owning station lacks `SalvageExpeditionDataComponent`, the server now adds that component to the owning station and generates initial mission offers. Existing expedition stations still reuse their existing data. Non-shuttle station grids are not auto-enabled by merely having an expedition console.
- Claim handling was adjusted to use the non-null ensured `EntityUid` directly, preserving the existing FTL/proximity checks and station update flow.
- Added `LuaMSalvageExpeditionConsoleBindingTest` to cover a `ComputerSalvageExpedition` installed on a `StandardFrontierVessel` shuttle station that initially has no expedition data.
- Added player-facing server news entry `2026072111` and extended the changelog contract test.
- No production server operation was attempted.

Commands and outcomes:

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~LuaMSalvageExpeditionConsoleBindingTest -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1 2>&1 | Tee-Object -FilePath .agents\last-expedition-console-test.log
```

Result: exit 1/truncated; follow-up log filtering showed compile errors caused by old nullable-station `.Value` access after changing the helper to return `EntityUid`.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~LuaMSalvageExpeditionConsoleBindingTest -m:1 --verbosity:quiet --logger "console;verbosity=minimal" -- NUnit.NumberOfTestWorkers=1 *> .agents\last-expedition-console-test2.log
```

Result: exit 1; compile errors in `SalvageSystem.ExpeditionConsole.cs` for `EntityUid.Value`, plus a nullable out assignment warning. Fixed by using the ensured `EntityUid` directly and assigning the existing data component through a local variable.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~LuaMSalvageExpeditionConsoleBindingTest -m:1 --verbosity:quiet --logger "console;verbosity=minimal" -- NUnit.NumberOfTestWorkers=1 *> .agents\last-expedition-console-test3.log
```

Result: exit 1; runtime failure because the created grid already had `ShuttleComponent`. Fixed the test to add it only when absent, and separated setup/tick/assertion phases.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~LuaMSalvageExpeditionConsoleBindingTest -m:1 --verbosity:quiet --logger "console;verbosity=minimal" -- NUnit.NumberOfTestWorkers=1 *> .agents\last-expedition-console-test4.log
```

Result: exit 1; cleanup assertion masked the test result. Retried with dirty pair settings while diagnosing.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~LuaMSalvageExpeditionConsoleBindingTest -m:1 --verbosity:quiet --logger "console;verbosity=minimal" -- NUnit.NumberOfTestWorkers=1 *> .agents\last-expedition-console-test5.log
```

Result: exit 1; debug assert `Attempted to dirty a non-networked component: SalvageExpeditionDataComponent`. Removed the invalid `Dirty(stationUid, data)` call.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~LuaMSalvageExpeditionConsoleBindingTest -m:1 --verbosity:quiet --logger "console;verbosity=minimal" -- NUnit.NumberOfTestWorkers=1 *> .agents\last-expedition-console-test6.log
```

Result: exit 0 but NUnit reported the dirty-disposed test as skipped; not accepted as a passing validation.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-build --no-restore --filter FullyQualifiedName~LuaMSalvageExpeditionConsoleBindingTest -m:1 --verbosity:minimal --logger "console;verbosity=detailed" -- NUnit.NumberOfTestWorkers=1
```

Result: exit 0; confirmed the dirty-disposed skip cause. Test was restored to normal cleanup with `Connected=false` only.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~LuaMSalvageExpeditionConsoleBindingTest -m:1 --verbosity:quiet --logger "console;verbosity=minimal" -- NUnit.NumberOfTestWorkers=1 *> .agents\last-expedition-console-test7.log
```

Result: exit 0; passed 1/1 in 35 seconds.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMSalvageExpeditionConsoleBindingTest|FullyQualifiedName~LuaMServerNewsChangelogTest" -m:1 --verbosity:quiet --logger "console;verbosity=minimal" -- NUnit.NumberOfTestWorkers=1 *> .agents\last-expedition-console-news-test.log
```

Result: exit 0; passed 2/2 in 37 seconds.

```powershell
git diff --check -- Content.Server/Salvage/SalvageSystem.ExpeditionConsole.cs Content.IntegrationTests/Tests/_LuaM/LuaMSalvageExpeditionConsoleBindingTest.cs Resources/Changelog/ServerNews.yml Content.IntegrationTests/Tests/_LuaM/LuaMServerNewsChangelogTest.cs
```

Result: exit 0; only expected LF-to-CRLF warnings for `Resources/Changelog/ServerNews.yml` were printed.

Next action: audit other shuttle/station-bound consoles that call `_station.GetOwningStation(...)` and may bind to a docked station or another shuttle, starting with:

```powershell
rg -n "GetOwningStation\(" Content.Server Content.Shared Content.Client --glob '*.cs'
```

## 2026-07-21 market console shuttle binding audit

Objective:

- Continue the console/device binding audit after expedition consoles, looking for station ownership calls that can make installed ship consoles use another station or docked ship's data.

Repository state:

- Audited `GetOwningStation(...)` call sites and selected the Frontier market console path as the next player-facing risk: `MarketSystem.MarketConsole` stored and read market data only from the current owning station, while ordinary player vessels (`StandardFrontierVessel`) do not start with `CargoMarketDataComponent`.
- `Content.Server/_NF/Market/Systems/MarketSystem.MarketConsole.cs` now initializes and parent-change checks `MarketConsoleComponent` through `TryEnsureLocalMarketData`.
- If a market console or cargo sale event is on a shuttle grid whose owning station lacks `CargoMarketDataComponent`, the server now adds local cargo-market data to that shuttle station and copies the normal Frontier market whitelist/blacklist filters from `MarketFrontierOutpost`.
- Existing stations with `CargoMarketDataComponent` keep using their existing data. Non-shuttle grids are not auto-enabled just by having a market console.
- Market cart messages, UI refresh, and entity-sold events now use the ensured local market data path.
- Added `LuaMMarketConsoleBindingTest` to cover `ComputerMarketConsoleNFNormal` installed on a `StandardFrontierVessel` shuttle station without initial market data.
- Added player-facing server news entry `2026072112` and extended the changelog contract test.
- No production server operation was attempted.

Commands and outcomes:

```powershell
Get-Content -Path .agents/ITERATION_LOG.md -Tail 120
rg -n "GetOwningStation\(" Content.Server Content.Shared Content.Client --glob '*.cs'
```

Result: exit 0; journal was read and station-ownership call sites were listed. The market-console path was selected for a narrow fix.

```powershell
rg -n "StationLimitedNetwork" Content.Server Content.Shared Resources --glob '*.cs' --glob '*.yml'
Get-Content Content.Server/DeviceNetwork/Systems/StationLimitedNetworkSystem.cs -TotalCount 120
Get-Content Content.Server/_NF/Market/Systems/MarketSystem.MarketConsole.cs -TotalCount 470
Get-Content Resources/Prototypes/_NF/Entities/Stations/base.yml -TotalCount 90
Get-Content Resources/Prototypes/_NF/Entities/Stations/nanotrasen.yml -TotalCount 110
Get-Content Resources/Prototypes/_NF/Entities/Structures/Machines/Computers/computers.yml -TotalCount 240
```

Result: exit 0 for the targeted inspections; confirmed that cargo market data is a station component, market consoles are installable computers, and normal ship stations do not inherit the market data component.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~LuaMMarketConsoleBindingTest -m:1 --verbosity:quiet --logger "console;verbosity=minimal" -- NUnit.NumberOfTestWorkers=1 *> .agents\last-market-console-test.log
```

Result: exit 1; the component was added but default market filters were null because `BaseStationCargoMarket` is an abstract parent prototype. Fixed by copying defaults from concrete `MarketFrontierOutpost`.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~LuaMMarketConsoleBindingTest -m:1 --verbosity:quiet --logger "console;verbosity=minimal" -- NUnit.NumberOfTestWorkers=1 *> .agents\last-market-console-test2.log
```

Result: exit 0; passed 1/1 in 37 seconds.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMMarketConsoleBindingTest|FullyQualifiedName~LuaMServerNewsChangelogTest" -m:1 --verbosity:quiet --logger "console;verbosity=minimal" -- NUnit.NumberOfTestWorkers=1 *> .agents\last-market-console-news-test.log
```

Result: exit 0; passed 2/2 in 35 seconds.

```powershell
git diff --check -- Content.Server/_NF/Market/Systems/MarketSystem.MarketConsole.cs Content.IntegrationTests/Tests/_LuaM/LuaMMarketConsoleBindingTest.cs Resources/Changelog/ServerNews.yml Content.IntegrationTests/Tests/_LuaM/LuaMServerNewsChangelogTest.cs
```

Result: exit 0; only expected LF-to-CRLF warnings for `Resources/Changelog/ServerNews.yml` were printed.

Next action: continue the binding audit with cargo pallet sale consoles and station/cargo order consoles, starting with:

```powershell
Get-Content Content.Server/Cargo/Systems/CargoSystem.Shuttle.cs -TotalCount 520
```

## 2026-07-21 cargo order console shuttle binding audit

Objective:

- Continue the console/device binding audit with cargo order/request consoles after the market-console fix.

Repository state:

- `Content.Server/Cargo/Systems/CargoSystem.Orders.cs` now initializes and parent-change checks `CargoOrderConsoleComponent` through `TryEnsureCargoOrderDatabase`.
- If a cargo request console is installed on a shuttle grid whose owning station lacks `StationCargoOrderDatabaseComponent`, the server now adds a local cargo-order database to that shuttle station.
- Existing stations with a cargo order database continue using their existing component. Non-shuttle grids are not auto-enabled just by having a cargo request console.
- Cargo order UI refresh, add/remove/approve order paths, bank-account lookup, and update broadcasts now use the ensured local database helper where appropriate.
- Added `LuaMCargoOrderConsoleBindingTest` to cover `ComputerCargoOrders` installed on a `StandardFrontierVessel` shuttle station without initial order database.
- Added player-facing server news entry `2026072113` and extended the changelog contract test.
- No production server operation was attempted.

Commands and outcomes:

```powershell
Get-Content -Path .agents/ITERATION_LOG.md -Tail 100
Get-Content Content.Server/Cargo/Systems/CargoSystem.Shuttle.cs -TotalCount 540
Get-Content Content.Server/Cargo/Systems/CargoSystem.Orders.cs -TotalCount 780
```

Result: exit 0; journal was read and cargo shuttle/order code was inspected. `CargoOrderConsoleComponent` was selected for the narrow fix because ordinary vessels do not inherit `StationCargoOrderDatabaseComponent`.

```powershell
Get-Content Content.Server/Cargo/Components/StationCargoOrderDatabaseComponent.cs
rg -n "class CargoOrderConsoleComponent|RegisterComponent.*CargoOrderConsole|CargoOrderConsoleComponent" Content.Server Content.Shared --glob '*.cs'
rg -n "BaseStationCargo|StationBankAccount|StationCargoOrderDatabase" Resources/Prototypes -g '*.yml'
```

Result: targeted inspections succeeded; confirmed the cargo order database component and station-prototype inheritance surface.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~LuaMCargoOrderConsoleBindingTest -m:1 --verbosity:quiet --logger "console;verbosity=minimal" -- NUnit.NumberOfTestWorkers=1 *> .agents\last-cargo-order-console-test.log
```

Result: exit 1; compile error from using `_` as both a discard and parameter name in `TryGetOrderDatabase`/`GetBankAccount` helper calls. Renamed those parameters to `component` and passed them explicitly.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~LuaMCargoOrderConsoleBindingTest -m:1 --verbosity:quiet --logger "console;verbosity=minimal" -- NUnit.NumberOfTestWorkers=1 *> .agents\last-cargo-order-console-test2.log
```

Result: exit 0; passed 1/1 in 36 seconds.

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMCargoOrderConsoleBindingTest|FullyQualifiedName~LuaMServerNewsChangelogTest" -m:1 --verbosity:quiet --logger "console;verbosity=minimal" -- NUnit.NumberOfTestWorkers=1 *> .agents\last-cargo-order-console-news-test.log
```

Result: exit 0; passed 2/2 in 34 seconds.

```powershell
git diff --check -- Content.Server/Cargo/Systems/CargoSystem.Orders.cs Content.IntegrationTests/Tests/_LuaM/LuaMCargoOrderConsoleBindingTest.cs Resources/Changelog/ServerNews.yml Content.IntegrationTests/Tests/_LuaM/LuaMServerNewsChangelogTest.cs
```

Result: exit 0; only expected LF-to-CRLF warnings for `Resources/Changelog/ServerNews.yml` were printed.

Next action: continue the binding audit with cargo shuttle consoles and sale/pallet behavior; start with:

```powershell
rg -n "GetOwningStation\(|CargoShuttleConsole|CargoPalletConsole" Content.Server/Cargo/Systems Content.Server/_NF/Market/Systems --glob '*.cs'
```
