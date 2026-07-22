# Monolith-DS production server journal

This is the persistent operational handoff for AI-assisted production-server work. Read it together with `.agents/ITERATION_LOG.md` before accessing the host. The installed mirror is `/opt/monolith-ds/AI_SERVER_JOURNAL.md`.

Never record secrets, credentials, raw environment files, private player data, or private keys here.

## Current state

- Service: `monolith-ds.service`; the live build observed before cleanup on 2026-07-19 UTC was `3986a48dcf1c3e3b71dcda02ee82003d528f1fd33a688bd878455ab712316e62`.
- Storage before cleanup: root filesystem 40 GiB total, 32 GiB used, 5.3 GiB available (86%).
- Large areas before cleanup: `/opt/monolith-ds/backups` 6.8 GiB, `/var/www/monolith-client` 4.4 GiB, `/opt/monolith-ds/uploads` 1.8 GiB, `/opt/monolith-ds/deploy-staging` 757 MiB, system journal about 799 MiB, APT cache/lists about 429 MiB.
- Cleanup policy selected by the user: free production disk space while preserving the live server, live client build, current data, and recent recovery material.
- Latest forced deploy: `2026-07-21T00:44Z`, service active on client version `a611cba7804203b3c71d1c6d2e6c95280dd2ed6cd124e598a813a6065a350af6`; round 144 started successfully with no error-priority service entries in the deployment window.
- Latest database maintenance: at `2026-07-21T00:04Z`, the ship ownership registry and its presence leases were cleared at the user's request; all unrelated persistent player data was preserved and the service returned healthy in round 143.
- Latest AI gateway change: at `2026-07-21T22:14Z`, the loopback-only gateway switched to the user's local Ollama `huihui_ai/qwen3.5-abliterated:9b` through a restricted reverse SSH tunnel; the end-to-end `/chat` smoke returned `action=none` with `fallback=false` while the game remained active.
- Latest operator-tool read: at `2026-07-21T22:35Z`, the local sector-report generator was reduced to a 13-line, strictly read-only player-safe brief; production remained healthy in round 145 with three players.
- Latest local AI-character batch: at `2026-07-22T00:22Z`, the desktop panel and source tree contain the stranded-human `Неизвестный`, his stripped wreck, personal-AI personas, a radio-driven survival/death sequence, and bounded anonymized dialogue audit. This batch is validated locally but is not deployed to production.
- Latest damaged-AI shuttle audit: at `2026-07-21T23:44Z`, a read-only production check confirmed that the Apocalypse damaged-AI scheduler repeatedly creates persistent unknown vessels at low population because its `StationEvent` entries omit `minimumPlayers`; no live game state was changed.
- Scope correction prepared at `2026-07-22T01:40Z`: the user's current `Безымянный` report concerns docking/restoration of saved ships, not the Apocalypse random-shuttle scheduler. The release candidate fixes legacy runtime-UID identity by assigning a stable saved-ship GUID and rebinding deeds, grids, locks, and consoles after restore. The older Apocalypse audit remains historical evidence only; no Apocalypse scheduler/cleanup change is included in this release.

## 2026-07-22T01:40Z -- saved-ship docking scope correction; no host operation

- The user clarified that the current incident is a docking problem involving restored ships. The earlier bounded production log observation that two `Unidentified Vessel` grids reparented to `«Колосс Централл»` did not contain an explicit docking failure and does not establish the current root cause.
- Local source/test review identified the relevant persistence defect: legacy saved records used a runtime `EntityUid` string as `shuttleId`; entity UIDs change across restore, so deed/grid ownership and console locks could lose their common identity. The release candidate migrates that value to a stable GUID and rebinds every restored participant to it. Exact reciprocal dock-pair and controlled-grid checks remain in place.
- No production host read, service operation, database read, player/ship inspection, configuration mutation, or deployment occurred while recording this correction. Rollback is not applicable.

Commands and outcomes:

```powershell
git log --oneline -18
git status --short --untracked-files=all
rg -n <saved ship GUID, restore rebinding, deed/grid/console lock, reciprocal docking symbols> Content.Server Content.Shared Content.IntegrationTests
```

Result: the release scope is corrected and the unrelated Apocalypse proposal was intentionally excluded.

Next action: after the clean local release gate passes, verify the installed journal mirror and zero-player state, then use the guarded first-rollout save bootstrap before any production stop:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/ship_luam_release.ps1 -Deploy -LegacyShipSaveBootstrap
```

## 2026-07-21T23:44Z -- repeated Apocalypse unknown-shuttle accumulation diagnosed read-only

- Scope was a bounded read-only investigation of the user's report that the damaged/unknown AI had again created multiple ships. No raw player data, chat, ship payload, credentials, or database content was read.
- The repository and installed server journals each contained 599 logical lines with zero text differences. Their byte hashes differed only because of line-ending representation; owner/mode remained `root:root`/`0644`.
- Over the bounded eight-hour service journal, six `неизвестный шаттл ИИ` rules were added, started, and ended. Four belonged to the previous long-running Apocalypse process and occurred at exact 30-minute intervals (`Nebula`, `Zenith`, `ZenithE`, `Zenith`). Two `Unidentified Vessel` grids later changed parent from their private map entities to `«Колосс Централл»`; no shuttle-impact log involving an `Unidentified Vessel` was present.
- Root cause is deterministic in the unchanged deployed-source prototypes. `MonoAISTCShuttleSpawnerSchedulerApocalypse` starts after 1800 seconds and schedules every fixed 1800 seconds. The six Apocalypse shuttle `StationEvent` components omit `minimumPlayers`, even though their separate `GameRule.minPlayers` fields contain 10-32; dynamic station-event eligibility checks `StationEvent.MinimumPlayers`, so the ships remain eligible with one player. `BaseRandomShuttleRule` lasts one minute, but `RuleGridsSystem` only records loaded grids and has no end-of-rule cleanup, so ending the rule does not remove the vessel.
- Current production health remained good: both game and gateway services active, round 149 running with one player, and the root filesystem at 64% used with about 14 GiB free. The current process had not yet emitted a damaged-AI shuttle event at the audit time, but its first fixed 30-minute scheduler deadline was imminent.
- No service, process, configuration, game rule, grid, round, database, player state, or release artifact was changed. Rollback is not applicable.
- After recording the audit, the updated repository journal was installed at `/opt/monolith-ds/AI_SERVER_JOURNAL.md`, verified byte-identical with owner/mode `root:root`/`0644`, and the game service remained active.

Bounded operations and outcomes:

```powershell
(Get-FileHash -Algorithm SHA256 -LiteralPath Tools/AI_SERVER_JOURNAL.md).Hash
ssh monolith-new "sha256sum /opt/monolith-ds/AI_SERVER_JOURNAL.md; stat ...; systemctl is-active ...; curl .../status"
ssh monolith-new "sudo cat /opt/monolith-ds/AI_SERVER_JOURNAL.md" # compared line-by-line in memory; zero text differences
ssh monolith-new "sudo journalctl -u monolith-ds.service --since '8 hours ago' ..." # aggregate counts and bounded unknown-shuttle timeline only
rg -n <Apocalypse scheduler, event, base-rule, eligibility, and RuleGrids symbols>
```

Result: all bounded reads exited 0 and established low-pop filter bypass plus persistent-grid accumulation as the cause; no live mutation was attempted.

Next action: add `StationEvent.minimumPlayers` to every Apocalypse damaged-AI shuttle entry and introduce an explicit bounded lifetime/cleanup policy for undocked rule grids, then prove low-pop suppression and end-of-life cleanup in integration tests before any deployment:

```powershell
rg -n "UnknownShuttle.*Apoc|minimumPlayers|BaseRandomShuttleRule|RuleGrids" Resources/Prototypes/_Mono/GameRules Content.Server/GameTicking/Rules Content.IntegrationTests --glob '*.yml' --glob '*.cs'
```

## 2026-07-21T23:40Z -- Unknown wreck survival scenario validated locally; no game deploy

- Scope was local implementation and verification only. `Неизвестный` now wakes on a sealed but immobile stripped wreck with no PDA, ID, navigation console, coordinates, thrusters, or gyroscope. Available survival equipment is bounded to the ore processor, hand drill, three full-size oxygen canisters, hydroponics/water/food, generator, ordinary radio, and structural life support.
- Player radio advice addressed to `Неизвестный` advances a deterministic six-stage survival sequence. Correct ordered advice stabilizes the wreck and reaches `Survived`; dangerous advice, three accumulated errors, or extended delay reaches `Dead` and changes the physical mob state. Panic replies use original short/disfluent wording and do not quote real victims or reveal numeric mechanics. Each status, misunderstanding, mistake, panic, successful stage, and survived state has multiple variants; the immediately previous exact reply is excluded from the next random selection while facts and consequences remain unchanged.
- The desktop panel persona was updated to the same novice/no-coordinate lore, first-person-only radio speech, gradual panic, no third-person stage directions, and explicit non-repetition with factual continuity. The exact panel process was reloaded after the final prompt and dialogue-journal update as PID 32808. No bridge action or player-visible production message was sent during any smoke test.
- The local release candidate adds anonymized dialogue audit at `data/luam/unknown_dialogue.jsonl`: UTC time, round ID, stage before/after, outcome, advice, and reply, with no separate sender-name or sender-ID field. Known active player names/IDs and sensitive technical strings are redacted from bounded text. At 5 MiB the current file replaces the single prior `.1` segment, keeping retention near 10 MiB.
- The desktop panel stores each local operator/Unknown exchange as one paired JSONL object under `AI-Agent-Workspace/sector-reports/unknown-dialogue.jsonl`, using a random per-launch conversation ID and no Windows account or player identity. `ЖУРНАЛ → ДИАЛОГИ` renders the last 200 local/server records and outcome counts; `ЖУРНАЛ → СИСТЕМА` remains separate. Nearby personal-AI speech is not persisted by this audit.
- Validation passed: server compile; `LuaMAiDirectorParsingTest` 119/119 from a separate temporary output; prototype/YAML checks; panel no-coordinate and panic smokes; panel JSONL write/redaction/viewer runtime self-test; full `Tools/validate_luam_feature_pack.py`; and bounded `git diff --check` with only line-ending notices. A normal local build copy was blocked solely by the already-running isolated local `Content.Server` PID 27316; it was not stopped.
- No production binary, resource, config, database, service, process, round, player state, or AI-provider setting was changed. The production release policy remains frozen, and the mixed accumulated tree still requires explicit guarded-release authorization before deployment. Rollback is not applicable to production; the local panel can be closed through its exact process or relaunched from `Desktop/HuiHui - Панель управления.cmd`.
- At `2026-07-22T00:22Z`, only this required journal mirror was installed on the host as `root:root` mode `0644` and verified byte-identically. Both `monolith-ds.service` and `luam-ai-gateway.service` remained active; the game stayed in round 149 with zero players and was not restarted.

Commands and outcomes:

```powershell
dotnet msbuild Content.Server/Content.Server.csproj /t:Compile /p:RestoreIgnoreFailedSources=true /v:minimal
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMAiDirectorParsingTest" -p:OutputPath=<temporary-output> -- NUnit.NumberOfTestWorkers=1
python Tools/validate_luam_feature_pack.py
powershell -File Desktop/HuiHui-Control-Panel.ps1 # bounded chat self-tests only
```

Result: all source, 119 targeted tests, prototype, panel dialogue-journal, and feature-pack checks passed. Production remained outside the mutation scope.

Next action: after explicit authorization for the accumulated release candidate, run the normal guarded packaging/deployment gate, restart once in an approved zero-player window, then verify the wreck load, monitoring entry, Unknown radio steps, death/survival endpoints, and personal-AI nearby replies.

## 2026-07-21T23:12Z -- production server renamed to Cosmic Cassiopeia

- The user explicitly requested the production identity change. A bounded config-only operation changed `hostname`, `lobby_name`, and the title at the start of `game.desc` from the prior temporary recruitment name to `🌟 Мёртвый космос: Космическая Кассиопея 🚀 🌟`; no binary, client package, database, persistent player data, AI provider, or gameplay setting was changed.
- Preflight verified the required installed journal mirror byte-identical at SHA256 `3061130f972694554bbcecc97cf1c4bd2a354be35d5ac6cfa286d8d8999d2b53`, owner/mode `root:root`/`0644`. Both game and gateway services were active; `/status` showed round 148 in the lobby with zero players, and about 14 GiB was free.
- The operation copied the live config to `/opt/monolith-ds/backups/server_config-before-cassiopeia-20260721T231145Z.toml` (`monolith:monolith`, mode `0600`), required exactly one old hostname/lobby/description title, parsed the updated TOML before atomically replacing it, and installed an automatic error trap that would restore the backup and restart the game on failure.
- The required game restart passed. Final `/status` advertised the exact new Unicode name in round 149 at run level 0 with zero players. `monolith-ds.service` and `luam-ai-gateway.service` were active, the live config contained the expected hostname and lobby name, about 14 GiB remained free, and the warning-or-higher service query since the operation began returned no entries.
- One initial read-only preflight command allowed PowerShell to interpret two remote shell substitutions locally, so its printed host-hash and service fields were empty and local `cut`/`systemctl` errors appeared. Its bounded status/config/storage reads still completed. A separate correctly quoted preflight immediately verified the mirror hash and both service states before mutation; the malformed read changed nothing.
- Rollback: restore the exact backup above over `/opt/monolith-ds/server/server_config.toml`, restart `monolith-ds.service`, and confirm `/status` advertises the prior name. No rollback was needed.
- After recording the operation, the repository journal was installed on the host byte-identically at SHA256 `e1e836a5a8d6871724f2ab23d0948b7a5658dbdd030f36837511073ca1045291`, owner/mode `root:root`/`0644`. A separate public `/status` read also returned the exact new name, round 149, run level 0, and zero players.

Commands and outcomes:

```powershell
(Get-FileHash -Algorithm SHA256 Tools/AI_SERVER_JOURNAL.md).Hash.ToLowerInvariant()
ssh monolith-new '<journal SHA/mode, both service states>'
# Base64-encoded bounded root script: back up and validate the exact live TOML, atomically update the three identity strings, restart only monolith-ds.service, and verify status/services/storage/logs.
```

Result: corrected preflight, guarded config rollout, journal installation, and public-status confirmation exited 0. The earlier malformed read-only command is explicitly not counted as a complete mirror/service verification.

Next action: if the displayed identity is questioned later, confirm it without restarting or mutating the round:

```powershell
Invoke-RestMethod http://188.127.225.57:1212/status | Select-Object name, players, round_id
```

## 2026-07-21T23:11Z -- read-only post-activation TTS verification

- Scope was a read-only answer to whether TTS now fully works. Before host access, the repository and installed copies of this journal matched at SHA256 `3061130f972694554bbcecc97cf1c4bd2a354be35d5ac6cfa286d8d8999d2b53`; the installed copy was `root:root` mode `0644`.
- Both game and gateway services are active. The live game retains `tts_characters_enabled = true` and `tts_enabled = false`; Piper health remains good with all four configured Russian voices. Round 148 is in the lobby at run level 0 with one player, and root storage has about 14 GiB free.
- Aggregate gateway audit metadata since the successful rollout contains zero `/tts` requests. This is consistent with no character IC speech occurring while the current round remains in lobby, but it means player-originated end-to-end playback is not yet directly observed. Warning-or-higher service entries and TTS-related error matches are both zero.
- No message content, player identifier, token, configuration, service, process, round, or player state was read or changed. No synthesis request was added during this verification. Rollback is not applicable.

Commands and outcomes:

```powershell
Get-Content -Raw .agents/ITERATION_LOG.md
Get-Content -Raw Tools/AI_SERVER_JOURNAL.md
ssh monolith-new "sha256sum /opt/monolith-ds/AI_SERVER_JOURNAL.md; stat -c 'journal=%U:%G:%a' /opt/monolith-ds/AI_SERVER_JOURNAL.md"
ssh monolith-new "<bounded service/status, gateway health, live TTS config, aggregate /tts audit counts, warning/error counts, and storage reads>"
scp Tools/AI_SERVER_JOURNAL.md monolith-new:/tmp/AI_SERVER_JOURNAL.20260721T2311Z.md
ssh monolith-new "sudo install -o root -g root -m 0644 /tmp/AI_SERVER_JOURNAL.20260721T2311Z.md /opt/monolith-ds/AI_SERVER_JOURNAL.md; <remove exact temporary file and verify mirror/services>"
```

Result: all bounded reads exited 0. Infrastructure/configuration health is green, but no game-originated TTS request exists yet, so player playback remains awaiting one normal in-round IC line. The required journal mirror was installed byte-identically with owner/mode `root:root`/`0644`.

Next action: once the round is running, send one ordinary IC line in-game and then verify only aggregate request status/latency with:

```powershell
ssh monolith-new "sudo journalctl -u monolith-ds.service -u luam-ai-gateway.service --since '2026-07-21 23:11:00 UTC' --no-pager | grep -Ei 'tts|gateway_request|warning|error|exception' | tail -n 120"
```

## 2026-07-21T23:00Z -- character TTS enabled with secured gateway authentication

- The user explicitly authorized enabling TTS. The bounded rollout enabled only character IC speech with `tts_characters_enabled = true`; AI Director speech remains disabled with `tts_enabled = false`. The other live limits remain 180 characters, 1.25-second cooldown, volume `-5`, 524288-byte response maximum, and queue size 8.
- Preflight found both services active in round 145 with three players and confirmed the required journal mirror matched SHA256 `c1e60712863032fa679f7a756bac65f58b8c0bcf6887ca7075281b476f91b03f`, owner/mode `root:root`/`0644`. Robust loads this CVar only at process startup and the service has `StandardInput=null`, so no supported live console/config reload path was available.
- The first guarded attempt changed only the character flag and restarted the game, but the required Russian Piper smoke returned HTTP 401. Its error trap restored the original config and restarted the service. This exposed a pre-existing authentication mismatch: the loopback gateway required a bearer token while the game process had no corresponding token. A first follow-up audit command failed locally before SSH because PowerShell interpreted a Python regular-expression fragment; a second bounded audit reached the host while the rollback restart was still starting and exited 1 on the temporarily unavailable status endpoint. The subsequent readiness audit passed and confirmed the config was restored, both services were active, and no warning-or-higher entries were present.
- The final secure rollout reused the existing gateway token without printing or copying it into the repository. It installed `/etc/monolith-ds/game-ai-gateway-token.env` as `root:root` mode `0600` and a non-secret systemd drop-in `/etc/systemd/system/monolith-ds.service.d/20-ai-gateway-token.conf` as `root:root` mode `0644`. The game receives the protected value through a `ROBUST_CVAR_luam__ai_director__gateway_token` environment override, so future source-config replacements do not erase it.
- The final game restart passed. A root-only equality check confirmed the game process received the same token without emitting it. An authenticated Piper request synthesized a valid 118828-byte RIFF/WAV using `ru_RU-irina-medium`. Final health at `2026-07-21T23:00:35Z`: game and gateway services active, round 148 in lobby with zero players, gateway/Piper healthy with all four configured Russian voices, about 14 GiB free, and no warning-or-higher service entries since rollout began.
- The initial attempt and automatic rollback restarted the live game while three players were connected, advancing rounds 145 through 147; the final successful restart advanced to round 148. No database, persistent player profile, account, bank, ship registry, AI provider, or client binary was changed.
- Recovery material: `/opt/monolith-ds/backups/server_config-before-tts-characters-20260721T2256Z.toml` and `/opt/monolith-ds/backups/server_config-before-tts-secure-20260721T2302Z.toml`. To disable/roll back, restore the latter config, remove the exact game token environment file and systemd drop-in above, run `systemctl daemon-reload`, restart `monolith-ds.service`, and verify both endpoints. Do not print or journal the protected token.

Commands and outcomes:

```powershell
ssh monolith-new "<journal mirror, service/status, systemd stdin/unit, and current TTS reads>"
rg -n -i "SIGHUP|reload.*config|LoadFromFile|changecvar" RobustToolbox Content.Server --glob '*.cs'
# First base64-encoded guarded remote config/restart/Piper-smoke script.
```

Result: source inspection confirmed restart was required. The first guarded rollout exited 1 on the authenticated gateway's HTTP 401 response and automatically restored the config/restarted the game; it is explicitly not counted green.

```powershell
# Bounded auth-presence/readiness audits; no token value was output.
# Final base64-encoded root script installing the protected environment override, enabling character TTS, restarting, verifying process-token equality, and running the authenticated Piper smoke.
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~LuaMServerNewsChangelogTest -m:1 --verbosity:quiet --logger "console;verbosity=minimal" -- NUnit.NumberOfTestWorkers=1
ssh monolith-new "<final UTC time, service/status, gateway health, TTS config, protected-file metadata, warning, and storage reads>"
scp Tools/AI_SERVER_JOURNAL.md monolith-new:/tmp/AI_SERVER_JOURNAL.20260721T2300Z.md
ssh monolith-new "sudo install -o root -g root -m 0644 /tmp/AI_SERVER_JOURNAL.20260721T2300Z.md /opt/monolith-ds/AI_SERVER_JOURNAL.md; <remove exact temporary file and verify mirror/services>"
```

Result: the final rollout and health checks exited 0; the player-news changelog/localization integration test passed 1/1. The required server-journal mirror was installed byte-identically with owner/mode `root:root`/`0644`.

Next action: after a player sends a normal IC line, inspect only bounded TTS request metadata and service warnings to confirm game-originated delivery without retaining chat text:

```powershell
ssh monolith-new "sudo journalctl -u monolith-ds.service -u luam-ai-gateway.service --since '2026-07-21 23:00:00 UTC' --no-pager | grep -Ei 'tts|gateway_request|warning|error|exception' | tail -n 120"
```

## 2026-07-21T22:50Z -- read-only TTS failure confirmation

- Scope was a repeated read-only diagnosis after the user reported that TTS does not work. Before host access, the repository and installed copies of this journal matched at SHA256 `87f1bb55b6b3d2d5c1547719c485224ec414801988a746eb7ec5e7de8edc7d38`; the installed copy was `root:root` mode `0644`.
- The failure is configuration gating, not a crashed synthesizer. `monolith-ds.service` and `luam-ai-gateway.service` are active, and gateway health reports Piper ready with default `ru_RU-irina-medium` plus the allowlisted Irina, Denis, Dmitri, and Ruslan voices.
- The live game configuration still has `tts_enabled = false` and `tts_characters_enabled = false`. The server code returns before queueing or delivering audio whenever the corresponding flag is false, so AI Director speech and character IC speech are both intentionally silent.
- The remaining live bounds are intact: 180 character maximum, 1.25-second character cooldown, volume `-5`, 524288-byte response limit, and queue size 8. The game is healthy in round 145 at run level 1 with three players; root storage has about 14 GiB free. The warning-or-higher journal query for both services over the preceding two hours returned no entries.
- No synthesis request, file/configuration change, service restart, game mutation, round mutation, or player-state mutation was performed. Rollback is not applicable.

Commands and outcomes:

```powershell
ssh monolith-new "sha256sum /opt/monolith-ds/AI_SERVER_JOURNAL.md; stat -c 'journal=%U:%G:%a' /opt/monolith-ds/AI_SERVER_JOURNAL.md"
ssh monolith-new "<bounded UTC time, service state, gateway health, live TTS keys, game status, storage, and two-hour warning reads>"
rg -n "tts_(enabled|characters_enabled|character_max_chars|character_cooldown|character_voices|volume|max_bytes|max_queue)" server_config.remote.toml
rg -n "LuaMAiDirectorTtsEnabled|LuaMCharacterTtsEnabled" Content.Server Content.Shared Content.Client --glob '*.cs'
scp Tools/AI_SERVER_JOURNAL.md monolith-new:/tmp/AI_SERVER_JOURNAL.20260721T2250Z.md
ssh monolith-new "sudo install -o root -g root -m 0644 /tmp/AI_SERVER_JOURNAL.20260721T2250Z.md /opt/monolith-ds/AI_SERVER_JOURNAL.md; <remove exact temporary file and verify mirror/services>"
```

Result: all commands exited 0 and confirmed healthy TTS infrastructure with both game-side feature gates disabled. The required journal mirror was installed with owner `root:root` and mode `0644`; both services remained active.

Next action: if the user authorizes activation, enable character IC TTS first while keeping AI Director TTS disabled, perform one bounded Russian synthesis smoke, and monitor queue/latency; begin with:

```powershell
rg -n "tts_(enabled|characters_enabled|character_max_chars|character_cooldown|character_voices|volume|max_bytes|max_queue)" server_config.remote.toml
```

## 2026-07-21T22:45Z -- read-only TTS status audit

- Scope was a read-only status check requested by the user. Before host access, the repository and installed copies of this journal matched at SHA256 `66240207bbace963064a15c124ce3362a1dcaf4807f3f710c29a4a8c07e1b429`; the installed copy was `root:root` mode `0644`.
- `monolith-ds.service` and `luam-ai-gateway.service` were active. Gateway health reported Piper ready with default model `ru_RU-irina-medium`, the four-voice allowlist `ru_RU-irina-medium`, `ru_RU-denis-medium`, `ru_RU-dmitri-medium`, and `ru_RU-ruslan-medium`, a 300-character gateway limit, and a 524288-byte WAV limit.
- The live game configuration deliberately disables both TTS paths: `tts_enabled = false` for AI Director speech and `tts_characters_enabled = false` for character IC speech. Its character bounds remain 180 characters, 1.25-second cooldown, volume `-5`, 524288-byte output limit, and queue size 8.
- The live game was healthy in round 145 at run level 1 with three players. Root storage was 40 GiB total, 24 GiB used, and 14 GiB available (64% used).
- No synthesis request was sent, because that would create gateway audit output and was unnecessary to establish readiness. No game file, configuration, service, process, round, player state, or persistent data was changed. Only this required journal mirror was installed after the audit; rollback is not applicable.

Commands and outcomes:

```powershell
(Get-FileHash -Algorithm SHA256 -LiteralPath 'Tools/AI_SERVER_JOURNAL.md').Hash.ToLowerInvariant()
ssh monolith-new "sha256sum /opt/monolith-ds/AI_SERVER_JOURNAL.md; stat -c 'journal=%U:%G:%a' /opt/monolith-ds/AI_SERVER_JOURNAL.md; systemctl is-active monolith-ds.service; systemctl is-active luam-ai-gateway.service; curl -fsS http://127.0.0.1:8787/health; sudo grep -E '<bounded TTS keys>' /opt/monolith-ds/server/server_config.toml; curl -fsS http://127.0.0.1:1212/status"
ssh monolith-new "df -h / /opt/monolith-ds | tail -n +2 | head -n 1"
scp Tools/AI_SERVER_JOURNAL.md monolith-new:/tmp/AI_SERVER_JOURNAL.md
ssh monolith-new "sudo install -o root -g root -m 0644 /tmp/AI_SERVER_JOURNAL.md /opt/monolith-ds/AI_SERVER_JOURNAL.md; rm -f /tmp/AI_SERVER_JOURNAL.md; sha256sum /opt/monolith-ds/AI_SERVER_JOURNAL.md; stat -c '%U:%G:%a' /opt/monolith-ds/AI_SERVER_JOURNAL.md"
```

Result: all bounded reads and the required journal installation exited 0; journal identity, services, gateway TTS readiness, disabled game flags, round health, and storage were confirmed without synthesizing audio. The installed mirror was verified byte-identical, `root:root`, and mode `0644`; both services remained active.

Next action: if the user authorizes activation, enable only character IC TTS first while keeping AI Director TTS disabled, then perform a bounded Russian speech smoke and monitor queue/latency; begin by reviewing the intended config delta with:

```powershell
rg -n "tts_(enabled|characters_enabled|character_max_chars|character_cooldown|character_voices|volume|max_bytes|max_queue)" server_config.remote.toml
```

## 2026-07-21T22:35Z -- concise read-only sector brief validation

- Scope was the user's local desktop-panel report generator only. Before reading production, the repository journal and installed mirror matched. No server file, service, process, round, player, database, AI inbox, or game state was changed.
- `AI-Agent-Workspace/HUIHUI-SECTOR-AUTOSTART.ps1` no longer reads service dumps, process lists, journals, warning logs, raw AI inbox contents, or verbose hazard bodies. It reads only the public game status, loopback gateway/Ollama health, and open sector task records required for five compact ranked entries.
- The previous automatic `status` append to the AI inbox was removed. Both manual execution and the actual `HuiHui Sector Autostart` scheduled task completed successfully; task result was `0`. The generated brief had 13 nonblank lines, five unique priority tasks, and no service dump or inbox content.
- The first local validation generated Cyrillic placeholders and failed to propagate a remote Python error because Windows PowerShell piped the script using an unsuitable encoding and the here-document lacked a final newline. No production mutation occurred. The generator now transfers the read-only script as UTF-8/base64 with a final newline and rejects any nonzero remote exit code; the subsequent manual and scheduled runs passed.
- Final health/storage facts: `monolith-ds.service` and `luam-ai-gateway.service` active; round 145 at run level 1 with three players; gateway healthy on native Ollama with the requested 9B model; root filesystem 40 GiB total, 24 GiB used, 14 GiB available, 64% used.
- Rollback is local-only: restore the previous `HUIHUI-SECTOR-AUTOSTART.ps1` if the infrastructure-heavy report is ever required. No production rollback applies.

Commands and outcomes:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File AI-Agent-Workspace/HUIHUI-SECTOR-AUTOSTART.ps1
Start-ScheduledTask -TaskName 'HuiHui Sector Autostart'
Get-ScheduledTaskInfo -TaskName 'HuiHui Sector Autostart'
ssh monolith-new '<bounded SHA256 mirror comparison, service/storage/status/gateway reads>'
```

Result: the concise report and registered scheduled task passed; all production checks were read-only and healthy.

Next action: keep the brief unchanged through live play unless operators request risk- or recency-based task ranking instead of current reward-first ranking.

## 2026-07-21T22:17Z -- local Ollama production gateway connection

- The user explicitly authorized completing the previously audited local-model connection. Scope was limited to the SSH forwarding policy required for one loopback listener, LuaM AI gateway code/environment, and the user's local tunnel supervisor. The game service, server binaries, client files, persistent game data, round state, and player state were not changed or restarted.
- Preflight verified the repository and installed server journal matched at SHA256 `2ecd6a94909be9d0049b5671dea1b4a70549d8d8eebee339da1dd9c7bb741397`; `monolith-ds.service` and `luam-ai-gateway.service` were active; round 145 had two players; root storage had about 14 GiB available. The existing gateway used an external OpenAI-compatible provider.
- The local Ollama endpoint was healthy and the requested model was installed. A cold local smoke returned `OK` in about 8.5 seconds. The initial reverse-forward attempt failed closed because production SSH hardening had `AllowTcpForwarding no`; no listener or provider change occurred from that attempt.
- SSH hardening was minimally adjusted to `AllowTcpForwarding remote`, `PermitListen 127.0.0.1:11434`, and `GatewayPorts no`. `sshd -t`, reload, effective-policy inspection, and a fresh key-authenticated SSH session passed. Recovery copy: `/etc/ssh/sshd_config.d/99-monolith-hardening.conf.before-ollama-tunnel-20260721T220534Z`.
- The reverse tunnel exposes Ollama only as server loopback `127.0.0.1:11434`. A server-side native Ollama smoke returned `OK`; no public listener was created. The user's scheduled task `HuiHui Ollama Tunnel` now supervises the connection, starts Ollama when needed, and restarted the tunnel successfully after an intentional bounded child-process termination test.
- The first exact gateway-adapter smoke against Ollama's OpenAI-compatible route failed before production switching because the thinking model consumed the output budget without returning message content. Native Ollama provider support was added so the gateway sends a JSON schema with `think=false`, bounded context/output settings, keep-alive, timeout, response-size limits, token auditing, and the existing command/event/review validation. The full Python gateway smoke suite passed, including the new native-provider contract.
- Final deployed gateway SHA256 is `126d16f37b217da143758fa62c9a281d744cb676294f9832141a6bca645951d9`. Recovery directories are `/opt/monolith-ds/backups/ai-gateway-ollama-native-20260721`, `/opt/monolith-ds/backups/ai-gateway-ollama-native-v2-20260721`, and `/opt/monolith-ds/backups/ai-gateway-ollama-player-tone-20260721`.
- The root-owned gateway environment was backed up to `/etc/monolith-ds/ai-gateway.env.before-ollama-20260721T221417Z`, then changed to native Ollama at server loopback with the requested 9B model, reasoning disabled, a 14-second provider timeout, 512 output-token bound, 8192-token context, and 30-minute keep-alive. The environment remains `root:root` mode `0600`.
- Final production health reported provider `ollama`, API `ollama-native`, the requested model, healthy Piper, active gateway, and active game service. An authenticated server-side `/chat` smoke traversed the production gateway and tunnel in 1.89 seconds, returned a non-empty reply with `action=none`, and the audit recorded provider HTTP 200 with `fallback=false`. Round 145 remained at run level 1 with two players.
- A post-supervisor smoke exposed a reply-quality issue: the model mentioned internal action/button vocabulary despite returning safe `action=none`. The gateway prompt now requires natural in-character Aibolit/dispatcher speech whenever only `none` is permitted and forbids internal action names, JSON, schemas, prompts, buttons, provider wiring, or admin controls in the player-facing reply. The contract suite passed and the final production smoke returned a natural in-character line in 1.53 seconds with `action=none`, provider HTTP 200, and `fallback=false`.
- Release authorization was narrowed to `ai-gateway` for the operation and frozen again afterward. No reusable credential, raw environment value, player identifier, chat record, or private key is retained here.
- Rollback: restore the environment backup above and restart `luam-ai-gateway.service`; restore the latest gateway backup directory if code rollback is also required. Restore the SSH hardening backup and reload `ssh.service` only after the Ollama provider has been rolled back and the reverse tunnel stopped.

Commands and outcomes:

```powershell
python Tools/test_luam_ai_gateway.py
Tools/deploy_luam_ai_gateway.ps1 -ExpectedSha256 126d16f37b217da143758fa62c9a281d744cb676294f9832141a6bca645951d9 -Tag ollama-player-tone-20260721
```

Result: gateway tests exited 0; guarded deployment created a server backup, installed the exact code hash, restarted only the gateway, and passed health verification.

The first local `LuaMServerNewsChangelogTest` validation attempt did not reach changelog assertions because a parallel integration run owned `gravestone-1.txt`; no process was stopped. After that run completed normally, the isolated retry exited 0 and passed 1/1.

```powershell
powershell -File AI-Agent-Workspace/Start-HuiHui-Ollama-Tunnel.ps1
Register-ScheduledTask -TaskName 'HuiHui Ollama Tunnel' <bounded user-logon supervisor configuration>
ssh monolith-new <bounded SSH-policy validation, loopback Ollama smoke, gateway environment switch, authenticated /chat smoke, service/status checks>
```

Result: the restricted tunnel, automatic recovery test, native provider switch, end-to-end request, and service checks passed. The one initial tunnel attempt and one OpenAI-compatible adapter smoke failed closed as described above and are not counted green.

Next action: monitor bounded gateway audit metadata for real player-addressed Aibolit requests and confirm latency remains below the 15-second game timeout without retaining chat contents:

```powershell
ssh monolith-new "sudo journalctl -u luam-ai-gateway.service --since '30 minutes ago' --no-pager | grep -E 'provider_request|gateway_request' | tail -80"
```

## 2026-07-21T00:45Z -- shuttle radar clipping forced deploy

- The user explicitly authorized uploading the shuttle-console correction to production. Scope was the reviewed static client and server binaries; configuration, persistent data contents, and AI gateway settings were unchanged except for the required backup and normal new-round startup.
- The final source package was `DeploymentPackages/LuaM/luam-local-release-20260721-002621.zip`, SHA256 `674bd1d6a375899cde9a9238366c14ebee8c093cc973f24ae25ca9f62e89a0b9`, payload digest `2175156d4052430a2007b8e19b6e5892094d6dda31421f9191b3557146f0e121`, production eligible, with all 1094 changed files packaged and no untracked or unexpected out-of-package files. The full source gate and independent package verification passed.
- Binary and repeated surface verification passed with zero violations. Client/version SHA256 is `a611cba7804203b3c71d1c6d2e6c95280dd2ed6cd124e598a813a6065a350af6`, server SHA256 is `4ca82e9d9f7ef7e0f3164550af563b082f6a835435b1d6be754bd2778c4575c6`, and release-receipt SHA256 is `ed3e6145b2c98f5030b5a9703d04883b2d56a5378cc1c9e30a58f4c5613f85c3`.
- Before production mutation, the installed journal matched the repository copy at SHA256 `029871f7b89b65b562ede8b1a0ce436c071e4c9cbc6bb861251a1acb2d32ee93`, owner/mode `root:root`/`0644`. The service was active in round 143 with one player; about 14 GiB was free immediately before the server deployment.
- The immutable 333903604-byte client archive was published and independently returned HTTP 200. Forced deploy tag `luam-20260721-radar-clip-force` completed after a required fresh data backup. The configuration SHA256 remained `9107b9e168794dba70fdb3563a1c0fa704226c3ec6e11b0cee383e44150c12cd`.
- Recovery material: `/opt/monolith-ds/backups/server-luam-20260721-radar-clip-force` (about 264 MiB), `/opt/monolith-ds/backups/server_config-before-luam-20260721-radar-clip-force.toml`, and `/opt/monolith-ds/backups/data-luam-20260721-radar-clip-force.tar.gz` (80330019 bytes, about 77 MiB). Restore the tagged server/config backups for binary rollback; restore the data archive only if persistent-state recovery is required.
- Post-deploy health: `monolith-ds.service` active since `2026-07-21T00:43:45Z`; `/status` and `/info` healthy in round 144; the advertised build and download URL use the new client hash; one player had rejoined and the round reached run level 1 by the final check; root filesystem 64% used with about 14 GiB free; server directory about 264 MiB and staging 4 KiB. The bounded error-priority journal query since `2026-07-21T00:43:30Z` returned no entries.

Commands and outcomes:

```powershell
Tools\audit_release_surface.ps1 -PackagePath release\SS14.Client.zip,release\SS14.Server_linux-x64.zip -Json
Tools\provision_monolith_client_static.ps1 -ClientPackagePath release\SS14.Client.zip -ExpectedSha256 a611cba7804203b3c71d1c6d2e6c95280dd2ed6cd124e598a813a6065a350af6 -Version a611cba7804203b3c71d1c6d2e6c95280dd2ed6cd124e598a813a6065a350af6 -ReleaseReceiptPath release\luam-binary-release-receipt.json -ExpectedReleaseReceiptSha256 ed3e6145b2c98f5030b5a9703d04883b2d56a5378cc1c9e30a58f4c5613f85c3
Tools\deploy_luam_server_release.ps1 -PackagePath release\SS14.Server_linux-x64.zip -ExpectedSha256 4ca82e9d9f7ef7e0f3164550af563b082f6a835435b1d6be754bd2778c4575c6 -ReleaseReceiptPath release\luam-binary-release-receipt.json -ExpectedReleaseReceiptSha256 ed3e6145b2c98f5030b5a9703d04883b2d56a5378cc1c9e30a58f4c5613f85c3 -Tag luam-20260721-radar-clip-force -ConfigSourcePath server_config.remote.toml -RemoteConfigPath /opt/monolith-ds/server/server_config.toml -RemoteDataDir /opt/monolith-ds/data -RequireDataBackup -Force
```

Result: all commands exited 0. The static archive returned HTTP 200, the deploy script warned about the one connected player under the explicitly authorized force path, and backup, swap, restart, endpoint, storage, and bounded log checks passed. No production command remains partially running.

Next action: after players use the corrected console, inspect bounded release diagnostics without retaining private player data:

```powershell
ssh monolith-new "sudo journalctl -u monolith-ds.service --since '2026-07-21 00:43:30 UTC' --no-pager | grep -Ei 'error|exception|shuttle|radar' | tail -n 160"
```

## 2026-07-21T00:25Z -- shuttle radar clipping release preflight

- The user explicitly requested that the shuttle-console UI correction be uploaded to production. Planned scope is the reviewed source release, static client publication, and server binary update; configuration and AI gateway settings are unchanged. Deployment will require a service restart and a fresh data backup.
- Before host access, the repository and installed copies of this journal matched at SHA256 `d7490c9d12634be89664ae869f393f38acf1c2a83dfe5964e9c482e1b92a8d8e`; the installed copy was `root:root` mode `0644`.
- Preflight health: `monolith-ds.service` active; `/status` and `/info` healthy; round 143, run level 0, zero players; client version `9ecabd9e56f14e8c3ade78e98844a782fbbfd22332d327a52a95941dda11a80f`; root filesystem 62% used with about 15 GiB free.
- Local targeted validation before this preflight passed the radar geometry tests 3/3, shuttle layout/window tests 2/2, and server-news load/localization test 1/1. The new behavior clips the flight-tab radar itself and clips sweep, missile, and hitscan segment endpoints to its rectangular bounds.
- No production service, data, configuration, client files, or binaries were changed during this preflight. Rollback is not applicable.
- The updated journal mirror was installed at `/opt/monolith-ds/AI_SERVER_JOURNAL.md` with owner `root:root`, mode `0644`, and matching SHA256; the service remained active.

Commands and outcomes:

```powershell
(Get-FileHash -Algorithm SHA256 -LiteralPath 'Tools/AI_SERVER_JOURNAL.md').Hash.ToLowerInvariant()
ssh monolith-new "sha256sum /opt/monolith-ds/AI_SERVER_JOURNAL.md; stat -c 'journal_owner_mode=%U:%G:%a' /opt/monolith-ds/AI_SERVER_JOURNAL.md; systemctl is-active monolith-ds.service; curl -fsS http://127.0.0.1:1212/status; curl -fsS http://127.0.0.1:1212/info; df -h / /opt/monolith-ds"
```

Result: journal identity, zero-player state, endpoint health, current client version, and storage facts were verified as recorded above.

Next action: complete the full source release gate before any production mutation:

```powershell
Tools\build_luam_release_package.ps1 -RunTests -RunLocalSmoke -Json
```

## 2026-07-21T00:04Z -- ship ownership registry clear

- The user explicitly requested clearing the ship ownership database. Scope was limited to `luam_ship_presence_lease` and `luam_ship_snapshot`; accounts, character profiles, banks, career progression, deep cryo, expeditions, game files, configuration, static client files, and the AI gateway were not changed.
- Before host access, the installed journal matched the repository copy at SHA256 `063ed75c950094a59a225d441d6d3b7a099c7634f9955f917723ad34a4ba2d69`, owner/mode `root:root`/`0644`. `monolith-ds.service` was active, `/status` reported round 142 with two players, and the root filesystem had about 15 GiB free.
- A read-only Python `sqlite3` audit of `/opt/monolith-ds/data/preferences.db` reported `quick_check=ok`, 13 ship snapshots, and one ship presence lease. Both ship tables had zero foreign-key violations. The database already had 311870 unrelated `admin_log_player` foreign-key findings; their count was recorded as a before/after invariant and did not change.
- The service was stopped from `2026-07-21T00:03:57Z` through `2026-07-21T00:04:19Z`. A fresh full data archive was created and completely checked before mutation: `/opt/monolith-ds/backups/data-before-ship-ownership-clear-20260721T000357Z.tar.gz`, 81161624 bytes, SHA256 `1d1cc14fd970af803712d80f695632e4745b923eb42b5debd7cf4e3e6a7ccc42`, owner/mode `root:root`/`0644`.
- With SQLite foreign-key enforcement enabled, one `BEGIN IMMEDIATE` transaction deleted leases first and snapshots second, then committed. It deleted one lease and 13 snapshots. Post-commit and post-restart read-only checks both reported zero rows in both tables, `quick_check=ok`, and zero ship-table foreign-key violations.
- Aggregate control counts for preferences, profiles, bank accounts, career progression, deep-cryo snapshots, and expedition sites were identical before and after the transaction. The pre-existing global foreign-key finding count also remained exactly 311870.
- Post-maintenance health: `monolith-ds.service` active; `/status` and `/info` healthy; round 143, run level 0, zero players at the check; engine `277.2.1`; root filesystem 62% used with about 15 GiB free. The bounded error-priority journal query for the maintenance window returned no entries.
- Rollback: stop the service and restore the checked archive above into `/opt/monolith-ds` only if the cleared ship registry must be recovered, then verify ownership, SQLite integrity, and both health endpoints before reopening access. No rollback was required.
- The updated journal mirror was installed at `/opt/monolith-ds/AI_SERVER_JOURNAL.md` with owner `root:root`, mode `0644`, and a matching SHA256; the service remained active.

Commands and outcomes:

```powershell
(Get-FileHash -Algorithm SHA256 -LiteralPath 'Tools/AI_SERVER_JOURNAL.md').Hash.ToLowerInvariant()
ssh monolith-new "sha256sum /opt/monolith-ds/AI_SERVER_JOURNAL.md; stat -c 'journal_owner_mode=%U:%G:%a' /opt/monolith-ds/AI_SERVER_JOURNAL.md; systemctl is-active monolith-ds.service; curl -fsS http://127.0.0.1:1212/status; df -h / /opt/monolith-ds"
```

Result: the required mirror and pre-maintenance service, player, and storage state were verified. A read-only attempt established that the standalone `sqlite3` CLI is not installed; all subsequent SQLite work used Python's standard library as the `monolith` service account.

```sql
PRAGMA foreign_keys = ON;
BEGIN IMMEDIATE;
DELETE FROM luam_ship_presence_lease;
DELETE FROM luam_ship_snapshot;
COMMIT;
PRAGMA quick_check;
PRAGMA foreign_key_check;
```

Result: the bounded transaction and every postcondition above passed. The wrapper had an exit trap that restarted `monolith-ds.service` on failure; it was not needed because the operation exited 0. One earlier local script-construction attempt failed before SSH execution and made no host change.

```powershell
ssh monolith-new "systemctl is-active monolith-ds.service; curl -fsS http://127.0.0.1:1212/status; curl -fsS http://127.0.0.1:1212/info; df -h / /opt/monolith-ds"
ssh monolith-new "sudo journalctl -u monolith-ds.service --since '2026-07-21 00:03:50 UTC' -p err..alert --no-pager"
```

Result: restart, endpoints, storage, backup metadata, and bounded error-log checks passed. No command remains partially running.

Next action: after the first new persistent ship is purchased and parked, inspect only aggregate registry state and bounded persistence diagnostics:

```powershell
ssh monolith-new "sudo journalctl -u monolith-ds.service --since '2026-07-21 00:04:19 UTC' --no-pager | grep -Ei 'ship-persistence|snapshot|shipyard|exception|error' | tail -n 160"
```

## 2026-07-20T23:58Z -- asteroid-belt, copper, and shuttle-console forced deploy

- The user explicitly renewed production-update authorization. The release remained limited to server and static-client mutation; the AI gateway was not changed.
- Before mutation, the installed journal matched the repository copy at SHA256 `14e908ea429eaedcb7df6451282f71c4edf08a491e9d0ed6eb5306124a9b032a`, owner/mode `root:root`/`0644`. `monolith-ds.service` was active, round 141 had two players, and about 16 GiB was free.
- Final source package: `DeploymentPackages/LuaM/luam-local-release-20260720-234117.zip`, SHA256 `69a8ed2a8522005d95ea876b4e240e8d12c382f29957ec7e3a2920ee73996c77`, payload digest `b5e4b4ddbf281719095f6c95c819c4fda8341a3c26589ccbf34dccd98469446d`, 1584 files, 1094/1094 changed files packaged, zero untracked files, and zero unexpected files outside the package.
- The final production gate passed all policy tests, local server/client smoke, gateway smoke, feature/localization checks, and the dependency audit. `System.Security.Cryptography.Xml` was updated from vulnerable `10.0.9` to fixed `10.0.10`; the final five-project audit reported zero vulnerable entries.
- Binary hashes: server `f63bc7d0841694228417b1bd0fee5d3dbf1ee5ce0a90687c0375e926d363873e`; client/version `9ecabd9e56f14e8c3ade78e98844a782fbbfd22332d327a52a95941dda11a80f`; receipt `5984642299719a35cb9c0c50047d8071de3a76322d64b7d04ffdc54c0b2c128c`. The independent client/server surface audit reported zero violations. Static client publication returned HTTP 200.
- Forced deploy tag `luam-20260721-asteroid-belt-force` stopped and restarted the service after backing up data. Config SHA256 remained `9107b9e168794dba70fdb3563a1c0fa704226c3ec6e11b0cee383e44150c12cd`.
- Recovery material: server backup `/opt/monolith-ds/backups/server-luam-20260721-asteroid-belt-force` (about 263 MiB), config backup `/opt/monolith-ds/backups/server_config-before-luam-20260721-asteroid-belt-force.toml`, and data backup `/opt/monolith-ds/backups/data-luam-20260721-asteroid-belt-force.tar.gz` (about 78 MiB). Restore the server/config backups for a binary rollback; restore the data archive only if data recovery is required.
- Post-deploy health at `2026-07-20T23:58Z`: service active; `/status` reported round 142 and run level 0; `/info` advertised the new client version and archive; one player had rejoined; root filesystem was 62% used with about 15 GiB free; server directory about 264 MiB and deployment staging 4 KiB. A sudo error-level journal query since deployment returned no entries.
- This updated journal is installed at `/opt/monolith-ds/AI_SERVER_JOURNAL.md` with owner `root:root`, mode `0644`, and a matching SHA256; the service remains active after mirror installation.

Commands and outcomes:

```powershell
Tools\provision_monolith_client_static.ps1 -ClientPackagePath release\SS14.Client.zip -ExpectedSha256 9ecabd9e56f14e8c3ade78e98844a782fbbfd22332d327a52a95941dda11a80f -Version 9ecabd9e56f14e8c3ade78e98844a782fbbfd22332d327a52a95941dda11a80f -ReleaseReceiptPath release\luam-binary-release-receipt.json -ExpectedReleaseReceiptSha256 5984642299719a35cb9c0c50047d8071de3a76322d64b7d04ffdc54c0b2c128c
Tools\deploy_luam_server_release.ps1 -PackagePath release\SS14.Server_linux-x64.zip -ExpectedSha256 f63bc7d0841694228417b1bd0fee5d3dbf1ee5ce0a90687c0375e926d363873e -ReleaseReceiptPath release\luam-binary-release-receipt.json -ExpectedReleaseReceiptSha256 5984642299719a35cb9c0c50047d8071de3a76322d64b7d04ffdc54c0b2c128c -Tag luam-20260721-asteroid-belt-force -ConfigSourcePath server_config.remote.toml -RemoteConfigPath /opt/monolith-ds/server/server_config.toml -RemoteDataDir /opt/monolith-ds/data -RequireDataBackup -Force
ssh monolith-new "systemctl is-active monolith-ds.service; curl -fsS http://127.0.0.1:1212/status; curl -fsS http://127.0.0.1:1212/info; df -h / /opt/monolith-ds"
```

Result: publication, required backup, forced swap/restart, endpoint verification, and bounded health/storage checks passed. A non-sudo `ls` of root-owned backups returned permission denied; exact `sudo stat` and `sudo du` checks then confirmed all three recovery paths and their sizes. No command was left partially running.

Next action: after normal player activity resumes, inspect bounded release diagnostics without retaining private player data:

```powershell
ssh monolith-new "sudo journalctl -u monolith-ds.service --since '2026-07-20 23:56:00 UTC' --no-pager | grep -Ei 'error|exception|asteroid|planetoid|bluespace|ship-persistence|TsfStationDefense' | tail -n 160"
```

## 2026-07-20T22:30Z -- asteroid-belt and shuttle-UI release preflight

- The user explicitly authorized a production update. This was a bounded read-only preflight; no service, configuration, database, player state, round state, or game file was changed.
- Before host access, the installed journal SHA256 matched the repository copy at `e02acfaf293c673a2e0c389932a13d9c1cb896fdf19dfcc861d3bc6805842000`; owner/mode were `root:root` and `0644`.
- `monolith-ds.service` was active. `/status` reported round 141, run level 1, and three connected players. The root filesystem was 60% used with about 16 GiB available.
- Because players are online, deployment will require the script's explicit force path. The user's current authorization is being treated as permission for that restart, but no restart will occur until source-package tests, local smoke, binary packaging, package-surface audit, client publication, and a required production data backup pass.
- Rollback is not yet applicable because this operation was read-only.
- The updated preflight journal was installed at `/opt/monolith-ds/AI_SERVER_JOURNAL.md`, verified as owner `root:root` with mode `0644`, and the game service remained active.

Commands and outcomes:

```powershell
(Get-FileHash -Algorithm SHA256 -LiteralPath 'Tools\AI_SERVER_JOURNAL.md').Hash.ToLowerInvariant()
ssh monolith-new "sha256sum /opt/monolith-ds/AI_SERVER_JOURNAL.md; stat -c '%U:%G %a %y' /opt/monolith-ds/AI_SERVER_JOURNAL.md; systemctl is-active monolith-ds.service; curl -fsS http://127.0.0.1:1212/status; df -h / /opt/monolith-ds"
```

Result: journal mirror, service health, round/player state, and storage checks passed with the facts above.

Next action: complete the local production gate before any host mutation:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\build_luam_release_package.ps1 -RunTests -RunLocalSmoke -Json
```

## 2026-07-20T16:15Z -- shared stored-shuttle and restore-error diagnosis

- Scope was a user-requested read-only diagnosis of current shipyard persistence errors and the symptom that multiple users see one stored shuttle. No service, configuration, database row, player entity, ship entity, or round state was changed.
- Before host access, the installed journal SHA256 matched the repository copy at `b214a76b8e91bcf5a692fb41a3478a69a5279bc80f4dbabe686437c0cd5d1178`; its owner/mode were `root:root` and `0644`.
- Health/storage at `2026-07-20T16:09Z`: `monolith-ds.service` active; round 141, run level 1, 6 players; root filesystem 60% used with about 16 GiB available.
- Bounded current-round logs contained 234 failed persistent ship calls across three ship identities and three actor accounts. Of these, 233 were `InvalidRequest: database snapshot envelope identity or revision mismatch`; one was an owner-filtered `NotFound: snapshot not found` when a different actor attempted the same deed identity. No account or ship identifiers are retained here.
- A read-only SQLite `mode=ro` audit found 10 ship rows for 5 owners: 7 stored, 3 retired, and 0 presence leases. Every payload parsed, all 10 embedded ship identities matched their database row, and no embedded identity was duplicated. All 10 failed the code's exact `payload revision == database revision - 1` relation because lifecycle/CAS transitions advance the row revision without replacing the grid payload.
- Local code inspection identified the paired causes. Restore claim/abort/recovery transitions increment the database revision, while `LuaMShipPersistenceOrchestrator.TryDecode` treats it as the serialized grid revision; a failed placement therefore makes future calls reject a valid payload and repeated retries continue increasing the row revision. Separately, shipyard slot refresh loops over all UI actors, lets the first actor attach a recovered deed to the shared inserted ID, accepts that existing deed for later actors without owner revalidation, and broadcasts actor-specific state through globally replicated `SetUiState`.
- A follow-up read-only audit while the round continued observed one newly active ship row: 11 total rows (7 stored, 3 retired, 1 active). Only the new active row satisfied the current payload/database revision relation; all seven callable stored rows remained mismatched. Every one of the 11 payloads contains one `ShipGridLock`, one `ShuttleConsoleLock`, and two serialized `shuttleId` string fields. These runtime entity IDs cannot be remapped when stored as strings, so restored console/deed unlock matching is a confirmed next compatibility defect after the revision gate is corrected.
- A final aggregate found one consistent presence lease for the active row, zero status/lease inconsistencies, and zero payload hashes duplicated across different owners. Two owners had multiple non-retired rows, but the current deed recovery orders by last update and returns only its first match; removing a deed from a card does not retire or select a different registry row, so automatic recovery can attach that same row again.
- The database remains identity-consistent and owner-filtered; this is a state-machine/UI isolation defect, not evidence that one payload was copied to all owners. Current-round logs contained no `LatheRecipeBatch` definition error, but the already-tested local lathe serialization fix is not yet deployed.
- Rollback/recovery is not applicable because the inspection was read-only. The required journal mirror was installed and verified against the repository copy with owner `root:root`, mode `0644`; `monolith-ds.service` remained active and the status endpoint still reported round 141 with 6 players.

Bounded operations and outcomes:

```powershell
(Get-FileHash -Algorithm SHA256 -LiteralPath 'Tools/AI_SERVER_JOURNAL.md').Hash.ToLowerInvariant()
ssh monolith-new "sha256sum /opt/monolith-ds/AI_SERVER_JOURNAL.md; stat -c '%U:%G %a' /opt/monolith-ds/AI_SERVER_JOURNAL.md"
ssh monolith-new "<bounded current service/status/disk, three-hour persistence diagnostics, and 30-minute warning query>"
ssh monolith-new "find /opt/monolith-ds/data -maxdepth 3 -type f <database-name filters>"
$encoded | ssh monolith-new "base64 -d | python3 -"
```

Result: mirror and health checks passed. Two initial Python-wrapper quoting attempts failed before the database audit executed. The successful stdin/base64 scripts opened the SQLite database in read-only mode and emitted only aggregate counts/one-way aliases; they established the valid payload identities, universal revision divergence, and current-round error totals above.

A later invocation with the same read-only command shape counted only saved lock components/fields across the now-11 payloads; it emitted no payload contents or identifiers and established the lock-identity finding above.

The final invocation returned only owner-multiplicity, lease/status consistency, cross-owner payload-hash, and stored revision-relation counts. It confirmed two owners with multiple non-retired rows, one consistent active lease, zero inconsistent lease rows, zero cross-owner duplicate payload hashes, and zero currently callable stored rows that satisfy the erroneous revision relation.

```powershell
scp Tools/AI_SERVER_JOURNAL.md monolith-new:/tmp/AI_SERVER_JOURNAL.20260720T1615Z.md
ssh monolith-new "sudo install -o root -g root -m 0644 /tmp/AI_SERVER_JOURNAL.20260720T1615Z.md /opt/monolith-ds/AI_SERVER_JOURNAL.md && <remove exact temporary file and verify mirror/service/status>"
```

Result: the repository and installed copies matched; owner/mode were `root:root`/`0644`; service health was unchanged.

Next action: implement and test (1) payload revision validation independent of the database lifecycle/CAS revision, including failed-placement/abort/retry and expired-lease recovery, (2) stable or remapped restored ship-lock identity, and (3) owner-bound deed recovery with per-actor shipyard UI state or equivalent single-actor isolation before any production repair or deployment:

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMShipPersistenceOrchestratorRuntimeTest|FullyQualifiedName~LuaMShipPersistenceDatabaseTest|FullyQualifiedName~LuaMShipPersistenceLifecycleContractTest" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
```

## 2026-07-20T14:39Z -- bounded ghost-follow incident diagnosis

- Scope was a read-only diagnosis requested by the affected administrator after ghost following unexpectedly ended. No account identifiers, chat content, target identity, or reusable player data are retained here.
- Before host access, the installed journal SHA256 matched the repository copy at `c4b5de0a94251c090a5ab1ea73ee1270a5683f9ab21ba2fd711fa70755a30389`.
- Bounded service-log inspection showed no disconnect, timeout, or kick for the affected session. The session entered admin ghost mode and shortly afterward generated repeated verb requests against a target entity that no longer existed. This is consistent with the followed entity being deleted or replaced and the follow attachment returning the observer to free ghost movement.
- A separate later error was also observed: persistent serialization of one shuttle repeatedly failed because `Content.Shared.Lathe.LatheRecipeBatch` has no writable data definition, alongside stale deleted-entity references. This occurred after the follow event and is not evidence that the followed player disconnected, but it requires a separate persistence fix.
- Current health/storage facts: `monolith-ds.service` status endpoint healthy; round 140, run level 1, 5 players; root filesystem 58% used with about 16 GiB available. No service, configuration, database, player state, or round state was changed. Rollback is not applicable.
- The updated journal mirror was installed at `/opt/monolith-ds/AI_SERVER_JOURNAL.md`, verified to match the repository copy, and retained owner `root:root` with mode `0644`.

Bounded operations and outcomes:

```powershell
(Get-FileHash -Algorithm SHA256 -LiteralPath 'Tools/AI_SERVER_JOURNAL.md').Hash.ToLowerInvariant()
ssh monolith-new "sha256sum /opt/monolith-ds/AI_SERVER_JOURNAL.md"
ssh monolith-new "sudo journalctl -u monolith-ds.service --since '20 minutes ago' --no-pager | <bounded follow, entity-lifecycle, disconnect, and exception filters>"
ssh monolith-new "curl -fsS http://127.0.0.1:1212/status"
ssh monolith-new "df -h / /opt/monolith-ds"
```

Result: journal mirror pre-check passed; the follow target ceased to exist without a session disconnect; a later independent lathe-batch snapshot failure was identified; service and storage remained healthy.

Next action: add a serialization-safe representation for live lathe batches and a full-grid persistence regression test before another production build:

```powershell
rg -n "LatheRecipeBatch|LatheComponent|RecipeBatch" Content.Server Content.Shared Content.IntegrationTests --glob '*.cs'
```

## 2026-07-20T12:26Z -- exact dock binding and ship-persistence forced deploy

- The user explicitly reauthorized an immediate production update with players online. Authorization remained limited to `server-release` and `client-static`; the AI gateway was only dry-run checked and was not changed.
- Before host mutation, the installed journal SHA256 matched the repository copy at `f47b62a314acd6512812e23a55ac76bd8b4ab258cb08ab23628aa9746b7b60d5`. Preflight showed `monolith-ds.service` active, round 139 running with 3 players, and about 17 GiB free disk.
- Production gates passed: source package `DeploymentPackages/LuaM/luam-local-release-20260720-120853.zip` SHA256 `22ce75c43e2e48211049fef758134fa55146657144a7e0838db0aaa110580022`, payload digest `4fcc2346a4fb18979a59253938f2c5acd610ad593c6570718f98deef61d9b9dc`, 1533 files, 1040 changed files packaged, no untracked release files, and no unexpected changed files outside the package.
- Binary release: server SHA256 `f5a9db27074a1af024d42ea244a12ace39b72717c66847a5e777ea401805bd4b`; client/version SHA256 `9838a0a641945ff0bf26bd571ec433221970f0cd028195328f89cd6b230ecf0e`; receipt SHA256 `e1155355564d689e3cb7e5b13f485b343182e98d46f4ab4045c7ba1326d7c422`. Independent surface audit found zero violations.
- Published the immutable client archive and received HTTP 200. Forced server deployment used tag `luam-20260720-dock-binding-force`, stopped and restarted the service, and required a data backup. Three players were online immediately before the restart.
- Recovery material: server backup `/opt/monolith-ds/backups/server-luam-20260720-dock-binding-force`, config backup `/opt/monolith-ds/backups/server_config-before-luam-20260720-dock-binding-force.toml`, and data backup `/opt/monolith-ds/backups/data-luam-20260720-dock-binding-force.tar.gz`. Config SHA256 remained `9107b9e168794dba70fdb3563a1c0fa704226c3ec6e11b0cee383e44150c12cd`.
- Post-deploy health: service active; `/status` reported round 140, run level 0, 0 players; `/info` advertised the new client version and URL; root filesystem was 58% used with about 16 GiB available. The bounded warning journal query returned no visible entries; the SSH account's usual journal visibility limitation remains.
- The updated journal mirror was installed at `/opt/monolith-ds/AI_SERVER_JOURNAL.md`, verified to match the repository copy, and verified as owner `root:root` with mode `0644`; the game service remained active.
- Rollback: restore the server and config backup directories above; restore the tagged data archive only if data recovery is required.

Commands and outcomes:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\build_luam_release_package.ps1 -RunTests -RunLocalSmoke -Json
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\verify_luam_release_package.ps1 -PackagePath DeploymentPackages\LuaM\luam-local-release-20260720-120853.zip -ExpectedSha256 22ce75c43e2e48211049fef758134fa55146657144a7e0838db0aaa110580022 -Json
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\build_luam_server_release.ps1 -ExternalClientBaseUrl http://188.127.225.57:1213 -SourcePackagePath DeploymentPackages\LuaM\luam-local-release-20260720-120853.zip -ExpectedSourcePackageSha256 22ce75c43e2e48211049fef758134fa55146657144a7e0838db0aaa110580022 -ReleaseReceiptPath release\luam-binary-release-receipt.json -Json
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\audit_release_surface.ps1 -PackagePath release\SS14.Client.zip,release\SS14.Server_linux-x64.zip -Json
```

Result: all commands exited 0; full release tests and smoke checks passed, package verification passed without warnings, binary build completed with existing compiler warnings, and surface audit reported zero violations.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\provision_monolith_client_static.ps1 -ClientPackagePath release\SS14.Client.zip -ExpectedSha256 9838a0a641945ff0bf26bd571ec433221970f0cd028195328f89cd6b230ecf0e -Version 9838a0a641945ff0bf26bd571ec433221970f0cd028195328f89cd6b230ecf0e -ReleaseReceiptPath release\luam-binary-release-receipt.json -ExpectedReleaseReceiptSha256 e1155355564d689e3cb7e5b13f485b343182e98d46f4ab4045c7ba1326d7c422
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\deploy_luam_server_release.ps1 -PackagePath release\SS14.Server_linux-x64.zip -ExpectedSha256 f5a9db27074a1af024d42ea244a12ace39b72717c66847a5e777ea401805bd4b -ReleaseReceiptPath release\luam-binary-release-receipt.json -ExpectedReleaseReceiptSha256 e1155355564d689e3cb7e5b13f485b343182e98d46f4ab4045c7ba1326d7c422 -Tag luam-20260720-dock-binding-force -ConfigSourcePath server_config.remote.toml -RemoteConfigPath /opt/monolith-ds/server/server_config.toml -RemoteDataDir /opt/monolith-ds/data -RequireDataBackup -Force
```

Result: client publication exited 0 with HTTP 200; forced server deployment exited 0 and completed backup, swap, restart, and status/info verification.

Next action: after players rejoin, inspect only bounded docking and persistence diagnostics for the new round:

```powershell
ssh monolith-new "journalctl -u monolith-ds.service --since '2026-07-20 12:25:00 UTC' --no-pager | grep -Ei 'dock|undock|ship-persistence|snapshot|shipyard' | tail -n 160"
```

## 2026-07-20T11:08Z -- bounded connected-player activity check

- Scope was a read-only check requested by the user for one specified connected account. No account identifiers, addresses, character names, messages, or other private player data are retained here.
- Before host access, SHA256 of the installed journal matched the repository copy: `9c23251039697aeee252d6b80bfc5346ee1e534f7df8a6c72d2a88a7a4245857`.
- Bounded service-journal inspection confirmed that the account connected during round 138 and had not disconnected. A read-only exact-account SQLite query found association with the current round and zero linked admin-log actions; bounded recent logs contained no chat from that account. Ordinary movement is not recorded, so exact in-world position or passive activity could not be established.
- Current health/storage facts: `monolith-ds.service` active; `/status` reported round 138, run level 1, and 3 players; root filesystem 56% used with about 17 GiB available.
- Only this required journal mirror is updated on the host; no game file, service, configuration, database row, player state, or round state was mutated. Rollback/recovery is not applicable.

Bounded operations and outcomes:

```powershell
(Get-FileHash -Algorithm SHA256 -LiteralPath 'Tools/AI_SERVER_JOURNAL.md').Hash.ToLowerInvariant()
ssh monolith-new "sha256sum /opt/monolith-ds/AI_SERVER_JOURNAL.md"
ssh monolith-new "<bounded current service/status, exact recent connection and disconnect journal search, read-only exact-account round/admin-log lookup, and filesystem usage check>"
```

Result: mirror pre-check passed; the account was connected with no logged chat or admin-log activity; service and storage remained healthy.

Next action: resume the local Phoenix persistence regression fix and run its targeted integration test before any deployment:

```powershell
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMFullShipPersistenceRuntimeTest.PhoenixWithLiveSpreaderQueuesCapturesAndRestores" -m:1 --verbosity:minimal -- NUnit.NumberOfTestWorkers=1
```

## 2026-07-20 UTC -- local Ollama player-chat feasibility audit

- Scope was read-only production feasibility inspection; no game build, server configuration, AI provider configuration, service, or round state was changed.
- Before host inspection, SHA256 of the installed journal matched the repository copy: `249278b4dadb764c3da6a1c2597f974921654ad93956e7535bd421093ea6415e`.
- `monolith-ds.service` and `luam-ai-gateway.service` were active. The gateway health endpoint reported a configured OpenAI-compatible provider and healthy Piper support; no credentials or raw environment values were read.
- Host capacity observed at about `2026-07-20T10:45Z`: 4 AMD EPYC Rome vCPUs, 17 GiB RAM total with about 12 GiB available, 4 GiB swap, root filesystem 56% used with about 17 GiB available, no visible NVIDIA runtime, and no Ollama binary installed.
- The requested model is the user's local Ollama `huihui_ai/qwen3.5-abliterated:9b`. Same-host CPU inference is not recommended because it would compete with the live game and likely miss the current 15-second gateway timeout. The preferred topology is a private authenticated tunnel to the user's Ollama host or a separate GPU worker, while keeping the existing localhost LuaM gateway as the game-facing boundary.
- Existing code already sends Aibolit-addressed player radio requests through the gateway in strict response-only mode (`action=none`). General player `ИИ` chat remains deterministic and would need a bounded response-only gateway path, per-player cooldown/history limits, one-flight queueing, and deterministic fallback before broad activation.
- Rollback/recovery: none required because no runtime or configuration mutation was performed. The required journal mirror installation completed with owner `root:root`, mode `0644`; both `monolith-ds.service` and `luam-ai-gateway.service` remained active.

Bounded operations and outcomes:

```powershell
(Get-FileHash -Algorithm SHA256 -LiteralPath 'Tools/AI_SERVER_JOURNAL.md').Hash.ToLowerInvariant()
ssh monolith-new "sha256sum /opt/monolith-ds/AI_SERVER_JOURNAL.md"
```

Result: hashes matched before inspection.

```powershell
ssh monolith-new "<bounded systemctl status, gateway /health, lscpu, free, df, NVIDIA-runtime presence, Ollama presence, and process-name probe>"
```

Result: the service, gateway, capacity, and runtime facts above were collected. The wrapper exited 1 only on its final optional process-name lookup; preceding checks completed.

Next action: verify the user's Ollama endpoint locally, then stage a private-tunnel smoke test without switching production provider settings:

```powershell
ollama serve
ollama run huihui_ai/qwen3.5-abliterated:9b
```

## 2026-07-20 UTC -- forced ship deed recovery server deploy

- User explicitly authorized a production update even with players online.
- Verified the host mirror of this journal matched the repository copy before production work; pre-deploy host state was `monolith-ds.service` active, round 137, 3 players during dry-run and 1 player immediately before the forced live deploy, root filesystem 54-55% used with about 17-18 GiB available.
- Built and verified source package `DeploymentPackages/LuaM/luam-local-release-20260720-083002.zip`, SHA256 `a52ae0802bf78053f057747c4dbcdf1de3621cbefa408e2d61c50c3dae38cfc5`, production eligible, 1522 files, 1028 changed files packaged, 0 unexpected changed files outside package.
- Built binary release with external client delivery. Server package SHA256 `f4201b98b1bbc3501cc01af6619736fc81f6b40f426dd124cc9661efb2b67e1b`; client package/version SHA256 `14db964d98f14be4ec5497fc2ece76ad791f83f6f15fee426c1c625c8edfb6d6`; receipt SHA256 `14165d4b6a68e49f2fd40788dce20bbe2ff1a7acbb5c2d2d258d4a24a7f09e63`.
- Published client static archive with `Tools\provision_monolith_client_static.ps1`; HTTP verification returned 200 for `http://188.127.225.57:1213/14db964d98f14be4ec5497fc2ece76ad791f83f6f15fee426c1c625c8edfb6d6/SS14.Client.zip`.
- Deployed server with `Tools\deploy_luam_server_release.ps1 -Force -RequireDataBackup -Tag luam-20260720-ship-deed-force -ConfigSourcePath server_config.remote.toml`. The script warned that players were online, then extracted, verified CDN build metadata, copied config, stopped service, backed up data, swapped server, started service, and passed status/info checks.
- Recovery material: server backup `/opt/monolith-ds/backups/server-luam-20260720-ship-deed-force`, config backup `/opt/monolith-ds/backups/server_config-before-luam-20260720-ship-deed-force.toml`, data backup `/opt/monolith-ds/backups/data-luam-20260720-ship-deed-force.tar.gz`.
- Config source SHA256 was `9107b9e168794dba70fdb3563a1c0fa704226c3ec6e11b0cee383e44150c12cd`; previous remote config SHA256 recorded by deploy script was `f96dc3b53f7274a50750c847850190d5c657fbac37e1c7a1224c79efe6e89e4c`.
- Post-deploy verification at about `2026-07-20T08:43Z`: service active, `/status` healthy with round 138, run level 0, 0 players, Apocalypse 7-day preset; `/info` advertises external client download URL for version `14db964d98f14be4ec5497fc2ece76ad791f83f6f15fee426c1c625c8edfb6d6`; root filesystem 55% used with 17 GiB available; `/opt/monolith-ds/server` about 263 MiB; `/opt/monolith-ds/deploy-staging` about 4 KiB.
- Bounded warning check `journalctl -u monolith-ds.service -p warning..alert -n 40 --no-pager` returned no entries visible to the SSH user. Direct `sha256sum` for `/opt/monolith-ds/server/server_config.toml` was permission denied; deploy-script config hashes are the authoritative record for this operation.
- Rollback: restore `/opt/monolith-ds/backups/server-luam-20260720-ship-deed-force` and `/opt/monolith-ds/backups/server_config-before-luam-20260720-ship-deed-force.toml`, or restore data from `/opt/monolith-ds/backups/data-luam-20260720-ship-deed-force.tar.gz` if needed.

Next action: have a player open a shipyard console with a fresh ID after cryo/new round and verify their stored shuttle appears/calls correctly; if not, inspect bounded server logs for `ship-persistence`, `snapshot`, `deed`, `shipyard`, and the affected ship id without recording private player data.

## 2026-07-19 UTC — pre-cleanup audit

- Read-only inspection covered filesystem usage, top-level `/opt`, `/var`, Monolith directories, backup timestamps, published client versions, package caches, and journal size.
- Confirmed the live immutable client build before cleanup.
- Planned bounded cleanup: remove disposable deployment staging and uploads; remove backup entries older than 2026-07-16 while retaining 2026-07-16 and newer recovery sets; remove published client directories except the active build; clean APT caches; vacuum system journal to 200 MiB.
- No database, current server directory, current data directory, active client build, server config, service unit, or gateway environment is in deletion scope.

Next action: execute the bounded cleanup, verify service health and live client HTTP availability, record reclaimed space here, then install this journal mirror on the host.

## 2026-07-19 UTC — bounded disk cleanup completed

- Removed all contents beneath `/opt/monolith-ds/deploy-staging` and `/opt/monolith-ds/uploads` after resolving and verifying both exact absolute directories.
- Removed top-level backup entries older than 2026-07-16. Recovery sets dated 2026-07-16 and 2026-07-18 remain; backup usage is now about 2.2 GiB.
- Removed obsolete immutable client directories while preserving the active build `3986a48dcf1c3e3b71dcda02ee82003d528f1fd33a688bd878455ab712316e62`; published-client usage is now about 319 MiB.
- Cleaned APT caches/lists and vacuumed the system journal from about 799 MiB to about 150 MiB.
- Reclaimed 13,101,957,120 bytes. Root filesystem changed from 86% used (5.3 GiB available) to 54% used (18 GiB available).
- The combined cleanup script performed the intended mutations but exited 1 during its last `/status` check because a PowerShell here-string introduced BOM/CRLF into the remote Bash stream. This verification failure did not interrupt or roll back cleanup. A separate clean read-only check then passed: service active, `/status` healthy with round 137 and two connected players, active client archive present, and root filesystem at 54%.
- The live server, database/data directory, configuration, gateway environment, active client, and retained recent backups were not removed or restarted.

Next action: configure a scheduled retention job that preserves the active client plus one rollback client and keeps a documented number of recent data/server backups; verify with a dry-run inventory before enabling it.

## 2026-07-19 UTC — security pressure monitor installed

- Installed `/usr/local/sbin/monitor-monolith-security` with a hardened oneshot service and an enabled timer running approximately once per minute.
- Monitor records SSH authentication failures over two minutes, current fail2ban bans, SYN-RECV pressure on SSH/web/game ports, established game TCP connections, root-disk usage, and `monolith-ds.service` state.
- Threshold breaches are written to journald with tag `monolith-security`; the current machine-readable snapshot is `/var/lib/monolith-security-monitor/status.env`.
- First service start failed before executing the monitor because `ReadWritePaths` referenced a state directory that did not yet exist. Added systemd `StateDirectory`; second start failed because the fully dropped capability set correctly prevented an unnecessary `chown` inside the script. Removed that ownership mutation. Both failed starts are retained in the service journal and are not counted green.
- Final start passed. Timer is `active` and `enabled`; snapshot at `2026-07-19T23:19:35Z` reported `status=ok`, 0 SSH failures in two minutes, 14 currently banned addresses, 0 public SYN-RECV sockets, 1 established game TCP connection, disk 54%, and game service active.
- No external notification transport is configured. Alerts are currently available through `journalctl -t monolith-security` and the protected status file. Discord delivery requires a webhook for a private channel stored only in a root-owned environment file.

Next action: obtain the private Discord webhook or another notification destination, add rate-limited delivery with recovery notifications, and test it with a synthetic non-attack warning.
