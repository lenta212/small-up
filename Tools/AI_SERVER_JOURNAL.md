## 2026-07-30T05:36:59Z -- stored-ship compatibility repair authorized and fleet snapshots inventoried

- Authorization/scope: after the read-only Hammer diagnosis, the operator explicitly said to fix it and to verify that every other ship can be called. This authorizes only a receipt-bound `server-release` hotfix and the necessary restart for batch `20260730-stored-ship-call-compat`, expiring at `2026-07-30T09:00:00Z`; client-static and AI-gateway mutations remain out of scope.
- Fleet inventory: query-only SQLite inspection found 14 callable `Stored` snapshots and nine retired rows. The callable set contains 19,699,553 payload bytes across 14 records, with entity counts from 77 to 5,206 and source rounds 158 through 181. None has a presence lease. Owner identifiers and private payload content are not recorded in this journal.
- Test evidence preparation: exported only the 14 stored snapshot payloads and non-owner test metadata through a query-only database connection into a temporary bundle, verified every database payload size/SHA256 during export, downloaded the 1,577,194-byte bundle with SHA256 `9eb475d7db38e72939d6962070ab87b139153944d85bcf037a75b53b530fe579`, and deleted the remote temporary archive. The bundle remains outside the repository and will not be committed or deployed.
- Production scope/health: no database row, snapshot, lease, ship, player, round, service, config, package, firewall, client, or gateway state was changed. The game service remained active. The previously installed diagnostic journal was current before this operation.
- Next action: add synthetic regressions for a reintroduced prototype and an invalid external `StationTracker`, apply the narrow compatibility fixes, then locally restore and roll back all 14 exported snapshots before running the full release gate.

## 2026-07-30T05:27:08Z -- persistent Hammer call failure diagnosed read-only

- Report/result: investigated the operator's inability to call a stored ship in round 185. The request passed ownership/card and free-gate validation, reached the durable restore path, and failed at `2026-07-30T05:22:48Z` with `InvalidRequest: snapshot-restore-exception:KeyNotFoundException`; this is not a balance, deed, occupied-gate, or server-health rejection.
- Restore evidence: deserialization treated `SpaceMedipen` as obsolete/removed, converted multiple external entity references to invalid entity `0`, and then `PdaSystem.UpdateStationName` attempted to name the invalid station referenced by a restored PDA's `StationTrackerComponent`. Other non-fatal invalid references were reported in `ShipRepairData`, `Store`, `ShuttleDestinationCoordinates`, `ShuttleDeed`, and device links. The runtime rolled the partial grid back and removed its temporary map.
- Durable safety: a read-only SQLite query proved the affected `Hammer` record remains `Stored` with registry revision 36, payload revision 8, 1,901 entities, 2,644,286 payload bytes, source round 179, no quarantine reason, and no presence lease. Repeating the call before a compatibility repair will reproduce the failure but the failed attempt did not consume or activate the ship.
- Code-level cause: `Resources/migration.yml` still maps `SpaceMedipen` to `null` even though a current prototype with that ID exists, so a newly captured full-ship snapshot can lose that entity on its next load and fail the exact entity/manifest contract. Independently, `SharedStationSystem.GetOwningStation` returns an invalid deserialized `StationTrackerComponent.Station` without checking entity existence, allowing PDA initialization to call `Name(EntityUid.Invalid)` and throw.
- Production health/scope: game service remained active with `NRestarts=0` and `ExecMainStatus=0`; round 185 was running with one player. Repository and installed journals matched before inspection. Operations were limited to `/status`, bounded service logs, source reads, and a query-only SQLite connection; the unavailable `sqlite3` CLI attempt failed before opening the database, and the Python read-only fallback succeeded. No service, config, package, database, snapshot, ship, round, player, firewall, client, or gateway state was changed.
- Next action: add a regression that restores a snapshot containing a live prototype erroneously listed in migration data plus a PDA with an invalid external station tracker; repair both compatibility paths, prove the existing snapshot loads without weakening entity/manifest integrity, then seek separate deployment authorization. Until then, do not repeatedly call or delete the stored ship.

## 2026-07-30T02:39:14Z -- hardened Unknown conversation update deployed

- Result/scope: deployed the receipt-bound server-only rollout `luam-20260730-unknown-radio-hardening` from clean source commit `c5b3ff8ed8c41744b9eda68e1e806dbb6c02d177`. The update adds bounded per-player/per-channel Unknown conversation memory, natural nameless follow-ups, short ordered gateway history, lifecycle fencing, three transient attempts, silent final provider failure, and subordinate-persona rejection. Neither client-static content nor the AI gateway was changed.
- Release evidence: the final orchestrator completed all seven stages with `ok=true`. Content tests passed 87/87 and LuaM integration tests passed 944/944; the policy contract, both required smoke checks, source verification, binary build, two-package surface audit, client-static dry-run, and server deploy dry-run passed. The source archive SHA256 is `8c90a94613169a8a4d7141f9b61f4863dec77eca09499a7bf908743ec6c12e9a`, payload digest `9e411aeea931e312cb37d9416d7a4394b09a225910d18932132dd41563404729`, worktree digest `836a8dffb180a912795b20055d629bbe69c8f93c4f5ae38d1e926b6215555e19`, server archive SHA256 `2106a57c59ff0f69663efc6a32b04ecdac00104be6efcb6154402125629b8503`, and binary receipt SHA256 `fd057e3f406d88a4ca0bdfc9de603780bd7577757626985ce344856c1dacaa78`. Surface violations were zero.
- Client compatibility: packaging reused the already published client archive and proved SHA256/build version `87e0388787c3894b0fb11ccc2ef9e57de4e5ecf74f2df5fab464094531b1c574`; `cdnPublishRequired=false`, the URL returned HTTP 200 before and after deployment, and `/info` continued advertising that exact hash and URL. No client archive was uploaded. The deploy dry-run and live result both had an empty `config_source_path`, so production configuration was not replaced.
- Production mutation: preflight found round 184 running with zero players, both services active, game `NRestarts=0`/`ExecMainStatus=0`, about 15.2 GB root storage free, and about 12.2 GB memory available. Authenticated ship-save receipt `a93b93ee-a239-4424-9cee-68c7445a218f` was created at `2026-07-30T02:34:59.906115Z`; it froze the barrier and reported attempted/saved/failed/active-remaining `0/0/0/0`. The official deploy script then stopped the game service, backed up data, swapped the server directory, and restarted into round 185 lobby.
- Backups/recovery: previous server directory is `/opt/monolith-ds/backups/server-luam-20260730-unknown-radio-hardening`; its `Content.Server.dll` SHA256 is `ec68f024da00054f27a5d3c0ecee5f532743107d30d36511121d23cda0a2f57d`. Config backup is `/opt/monolith-ds/backups/server_config-before-luam-20260730-unknown-radio-hardening.toml`, root-only mode `0600`; its SHA256 `0806dded983b474b43a4009cdf0699f4beeb22ce203cc5e7c6204be5bc4063ed` exactly matches the unchanged live config. Required data backup is `/opt/monolith-ds/backups/data-luam-20260730-unknown-radio-hardening.tar.gz`, 242,119,706 bytes, `root:root` mode `0600`, and passes `gzip -t`. Roll back by stopping the game service, preserving the failed new directory, restoring the recorded server backup and config backup with their recorded ownership/modes, restoring the data archive only if persistence was damaged, then starting and rechecking `/status` and `/info`.
- Post-deploy health: game and gateway services are active; game main PID 44425 has `NRestarts=0` and `ExecMainStatus=0`; gateway retained its pre-deploy activation timestamp and also reports zero restarts/status. Installed `Resources/Assemblies/Content.Server.dll` SHA256 `39305c6bacd8d5a0e0029432eeef685a281c397c00d15e646f5b96f6f4aeedba` exactly matches the release archive. Round 185 is in lobby with zero players, root has 14,678,355,968 bytes free, and memory has about 16.2 GB available. Four broad error-pattern matches came from the old PID during pre-stop world cleanup (three transient transform/audio errors and one bluespace cleanup warning); the new PID has zero error/exception/fatal/OOM/YAML matches and one expected startup `Cannot keep up` while prototypes loaded.
- Closure: remote deployment policy is frozen again, authorization fields are cleared, and the batch is `local-package-only`. The canonical journal mirror is installed after this entry is finalized.

## 2026-07-30T00:14:26Z -- hardened Unknown server release authorized and preflighted

- Authorization/scope: the operator explicitly authorized preparing the hardened Unknown radio-conversation update and restarting production. The release policy is refreshed for the exact `20260730-unknown-radio-hardening` batch, expires at `2026-07-30T03:00:00Z`, and permits only `server-release`; no client-static or AI-gateway publication is authorized.
- Local evidence: clean branch `codex/unknown-radio-conversation-release` at `864a98d6a67a3cd627bde6c27beb176cd3b48345` passed the complete readiness gate with `ok=true`, `productionEligible=true`, zero issues/warnings, both policy test filters, both smoke checks, and stable worktree receipt `95508bb9ad60851d7806b0d08877e55a0169723923ed463d43106fdf496e6065`. That result used the preceding policy SHA256 `030965e66abd4f31a13148b6d6156e9d52826d269563d1f4896544dc9a5b2eb3`; refreshing authorization intentionally invalidates it as final package evidence, so the production package gate must run again under the new policy SHA.
- Journal reconciliation: repository SHA256 `e54a7b45cfc8749002eb362ac0befd742e97ea198df560720e9079f18ee2c2a5` differed from installed SHA256 `05d48fcaa7bb7f3d5debf0897892c331e999f0ca533175c1f9842fbc24a827ab`. The installed copy contained six newer 2026-07-29 operational entries, including the original Unknown radio-stream deployment; those facts are reconciled below before this repository copy is used as the new canonical journal.
- Production preflight: `monolith-ds.service` and `luam-ai-gateway.service` are active; the game service reports `NRestarts=0` and `ExecMainStatus=0`. Loopback `/status` reports round 184 in progress with one player and 100-player soft capacity. `/info` still advertises build `87e0388787c3894b0fb11ccc2ef9e57de4e5ecf74f2df5fab464094531b1c574`. Root storage has 15,192,936,448 bytes available (63% used), and memory has about 12.1 GB available.
- Commands/outcomes: required local journal/runbook/policy reads; SHA256 comparison; read-only `scp` of the installed journal; heading/content reconciliation; bounded `systemctl`, loopback `/status` and `/info`, `df`, and `free` checks. The first SSH metadata wrapper stopped at a harmless `stat` quoting error after hashing the journal and before health checks; the corrected read-only wrapper completed. No service, package, configuration, database, ship, round, player, firewall, or gateway mutation has occurred in this iteration.
- Recovery: none required because preflight was read-only. The currently installed original Unknown stream remains live.
- Next action: commit the reconciled journals and refreshed authorization, then run `powershell -NoProfile -ExecutionPolicy Bypass -File Tools\ship_luam_release.ps1 -Force -Json` to rebuild all receipt-bound artifacts and dry-run the server deployment before any live swap.

## 2026-07-29T06:10Z -- replacement-address host is not Monolith production

- The operator supplied `85.137.164.42` for diagnosis. Read-only SSH access succeeded, but the host identifies as `s1570741.smartape-vps.com` and does not contain `/opt/monolith-ds`, the installed server journal, a Monolith systemd unit, game files in the bounded application-directory search, or a listener on port 1212.
- `monolith-ds.service` is nonexistent/inactive, not a failed installed service. The machine instead runs nginx on ports 80/443 and contains unrelated `/opt/ai-reseller` and `/opt/tg-scanner` applications.
- Host facts: approximately 90 days uptime, root filesystem 81% used with 3.6 GiB free, about 1.3 GiB memory available, and unrelated failed `certbot.service` and `logrotate.service` units.
- Commands were bounded to identity, journal presence, service/status, storage, memory, units, listeners, directories, containers, boot history, and current-boot errors. No credentials were retained and no host mutation occurred.
- Recovery: none required. The repository journal was deliberately not installed on this unrelated host.
- Next action: identify the current network address or provider console for the real Monolith host containing `/opt/monolith-ds` and `monolith-ds.service`.

## 2026-07-29T17:53Z -- local update preparation only

- Prepared the current source integration batch locally; no production host was accessed and no remote mutation occurred.
- The expired 2026-07-27 authorization was cleared from local release policy. Remote deployment was frozen and the batch marked `local-package-only` pending fresh explicit authorization.
- Server, client, and integration projects built with zero errors. Six directly affected LuaM test classes passed 282/282. The full LuaM integration batch did not finish within a locally imposed 15-minute limit and had no passing result; content tests and local-stack smoke remained outstanding.
- Local evidence archive: `luam-local-release-20260729-175210.zip`, SHA256 `0429b0dbcd0ef5922f1820296770ba642de0004793b5d8034ed84612667cdc4c`, payload digest `123aae10cbd825cdb30b7ec8592b69fe608ca357488a72a9c2eedd0c551fb441`. It was deliberately `productionEligible=false` because two migration files remained untracked and mandatory gate evidence was incomplete.
- Recovery: no remote recovery was needed. The local archive must be discarded if its source worktree changes; a production candidate must be rebuilt from a tracked worktree after the complete gate passes.
- Next action: track the two fleet-index migrations and run the complete source gate with tests and local smoke before seeking fresh deployment authorization.

## 2026-07-29T20:07Z -- local AI bridge enabled and test radio sent

- Authorization/scope: the operator explicitly authorized connecting the local AI bridge and restarting production. Work was limited to LuaM bridge CVars, two service restarts needed to apply and correct configuration access, one inbox command, health checks, and journal synchronization; no code/package, database, snapshot, firewall, or client publication change was made.
- Preflight: `monolith-ds.service` was active; `/status` reported round 179 in progress with three players; TCP 1212 was listening; root storage was 62% used with about 15 GiB available. The installed journal SHA256 was `e54a7b45cfc8749002eb362ac0befd742e97ea198df560720e9079f18ee2c2a5`.
- Configuration: created root-only backup `/opt/monolith-ds/backups/server_config.toml.before-local-bridge-20260729T200248Z`; set `luam.ai_director.local_bridge_enabled=true` and `luam.ai_director.local_bridge_unsafe_actions_enabled=true`; retained `admin_mode=false`. The first edit accidentally left the live config `root:root` mode `0600`, so the first restart could not load it and briefly started with defaults. Corrected the live config to `monolith:monolith` mode `0640` and restarted again; the normal server identity/config returned.
- Test messages: sent three operator-requested Common-channel lines through the inbox. The outbox recorded one successful transmission for each at `2026-07-29T20:07:02.4968446Z`, `2026-07-29T20:08:32.4775331Z`, and `2026-07-29T20:09:36.4774546Z`; the service journal also confirmed each exactly once. The bridge implementation supplied the radio actor name `Неизвестный`.
- Final health: service active; round 181 in progress with two players at verification; production name/tags and 100-player limit restored; root storage 62% used with about 15 GiB available; inbox/outbox remained `monolith:monolith` mode `0644`.
- Recovery: restore the root-only config backup to the live config, reset ownership/mode to `monolith:monolith`/`0640`, and restart `monolith-ds.service`. If only bridge access must be disabled, set both local bridge CVars to `false` and restart.
- Next action: monitor bridge commands and keep the inbox writer restricted; decide whether unsafe bridge actions should remain enabled after the interactive AI controller is connected.

## 2026-07-29T20:47Z -- interactive Unknown radio follow-up and neural reply path prepared

- Authorization/scope: the operator requested that Unknown receive a live stream and answer players online. Production inspection and one bounded radio follow-up were performed; no package, executable, database, snapshot, firewall, client publication, config edit, or service restart was made in this follow-up.
- Live state: `monolith-ds.service` and `luam-ai-gateway.service` were active; gateway listened on `127.0.0.1:8787`; `/status` reported round 181 in progress with two players; root storage was 62% used with about 15 GiB available. The gateway was backed by local Ollama, while production's current Unknown path still used the bounded rule-based survival responder.
- Radio delivery: a PowerShell-to-SSH encoding mistake first sent one garbled duplicate line at `2026-07-29T20:45:17Z`; this was corrected once with an explicit UTF-8/base64 payload. The outbox recorded the readable Common-channel line at `2026-07-29T20:46:03.8319296Z`; the local bridge reported successful delivery.
- Local preparation: extended `LuaMSectorAiDirectorSystem` so radio messages explicitly addressed to `Неизвестный` could use the existing `/chat` neural gateway, preserve the survival-state fallback, accept only action `none`, limit replies to 220 characters, reuse request budgeting/deduplication, and keep the visible actor `Неизвестный`. This was an event-driven addressed-message stream, not indiscriminate capture of all player chat.
- Validation: `dotnet build Content.Server/Content.Server.csproj --no-restore -v:minimal` completed with zero errors and existing warnings. Deployment remained blocked by the repository's fail-closed release policy (`remoteDeployFrozen=true`, authorization state `frozen`), so the prepared code was not active in production yet.
- Recovery: no production rollback was required because no code/config/service mutation occurred.
- Next action: obtain a fresh bounded `server-release` authorization, run the complete release/package gates, deploy through `Tools/deploy_luam_server_release.ps1`, restart under that guarded rollout, and verify one addressed Unknown radio exchange plus fallback behavior.

## 2026-07-29T21:04Z -- Unknown neural radio stream deployed

- Objective/result: activated the event-driven neural reply path for radio messages explicitly addressed to `Неизвестный`. The handler sends the addressed message and bounded sector/survival context to the existing local gateway `/chat`, accepts only `action=none`, caps output at 220 characters, and retains the deterministic survival reply as provider/transport fallback.
- Validation: isolated `Content.Server` DebugOpt build passed with zero errors; focused source checks confirmed the `/chat` URI, in-character Unknown prompt, `AllowedActions=["none"]`, and rejection of any provider action other than `none`. A production gateway smoke returned a natural Russian in-character reply with `action=none` after Ollama warmed the 9B model.
- Deployment: with the operator's explicit connect/restart authorization, backed up the prior server assembly and config under `/opt/monolith-ds/backups/unknown-stream-20260729T210242Z`, replaced only `Resources/Assemblies/Content.Server.dll`, and restarted only `monolith-ds.service`. No client, database, data, snapshot, firewall, gateway code, or configuration change was made.
- Health: `monolith-ds.service` and `luam-ai-gateway.service` were active, the game listened on TCP 1212, `/status` returned HTTP 200, startup reached `Ready`, the gateway `/health` was healthy, and root storage remained 62% used with about 15 GiB free. Installed assembly SHA256 was `ec68f024da00054f27a5d3c0ecee5f532743107d30d36511121d23cda0a2f57d`.
- Recovery: restore `/opt/monolith-ds/backups/unknown-stream-20260729T210242Z/Content.Server.dll` to `/opt/monolith-ds/server/Resources/Assemblies/Content.Server.dll` with `monolith:monolith` ownership and restart `monolith-ds.service`.
- Next action: verify one real player radio message addressed to `Неизвестный`; the server log should record `gateway radio Common` exactly once and not the old `survival radio` path.

## 2026-07-29T21:05:00Z -- Unknown radio AI stream deployed and verified

- Scope: operator-authorized live connection of addressed radio traffic to the existing local AI gateway. Only the AI gateway timeout and the server `Content.Server.dll` were changed; no client, database, migration, ship snapshot, or manual inbound radio message was created.
- Implementation/validation: `LuaMSectorAiDirectorSystem` sends radio requests addressed to `Неизвестный` to gateway `/chat`, permits only action `none`, limits replies to 220 characters, preserves the radio channel/language/source, and falls back to the existing survival reply on provider failure. The isolated release worktree built with zero errors and 1099 existing warnings; installed DLL SHA256 was `ec68f024da00054f27a5d3c0ecee5f532743107d30d36511121d23cda0a2f57d`.
- Gateway: the first provider smoke timed out at the configured 14 seconds. `LUAM_OLLAMA_TIMEOUT` was changed from 14 to 45 and `luam-ai-gateway.service` restarted; an authenticated `/chat` smoke then returned a provider-generated reply with action `none`. Gateway health remained HTTP 200 on loopback port 8787 with the configured Ollama model. Credentials and raw environment values were not recorded.
- Deployment: the previous server DLL was copied to `/opt/monolith-ds/backups/Content.Server.dll.before-unknown-stream-20260729T210158Z`; the verified DLL was installed and `monolith-ds.service` restarted. A first restart command in a CRLF stdin script installed the file and backup but failed before restart because systemd received a carriage return in the unit name; the direct corrected restart succeeded. A later overlapping verification caused a second intentional restart while the first process was still starting; the final process reached `Server Version 277.2.1.0 -> Ready`.
- Health/storage: final `monolith-ds.service` was active with `NRestarts=0` and `ExecMainStatus=0`; TCP 1212 listened, `/status` returned HTTP 200 in lobby round 183 with one connected session, gateway service was active, and root storage was 62% used with about 15 GiB free.
- Recovery/next action: restore the timestamped DLL backup, set `LUAM_OLLAMA_TIMEOUT=14` if the gateway change must also be reverted, then restart the affected services. Functional completion still required one genuine player radio line addressed to `Неизвестный`; verify exactly one gateway radio reply in bounded logs without retaining player-private text.

## 2026-07-25T21:41:19Z -- live ghostrespawn failure confirmed; repair direction paused

- Scope: bounded read-only follow-up after the affected player tested the deployed suspended-body hotfix. No service, package, configuration, database, round, ship, or player state was changed.
- Result: the real sequence still failed. A normal join transferred control through three successive spawn entities; `ghostrespawn` then detached the mind, and the next join was blocked because the exact durable `Playable` authority remained active. The deployed reattachment logic therefore does not cover the actual final controlled body.
- Refined direction: the operator explicitly does not require returning to the same body. The next repair should make `ghostrespawn` durably release the exact current-round `Playable` authority and dispose its exact locally tracked body before returning the player to the lobby, so the following join can reserve a new lifecycle and spawn a new body. The release must remain fail-closed for foreign-server, foreign-round, duplicate, Store, restore, and indeterminate database outcomes.
- Repository state: no implementation was made after this clarification. An uncommitted integration-test addition and journal updates are present and must be reviewed rather than overwritten. Work is paused at the operator's request.
- Commands/outcomes: required repository/server journal reads; Git status/diff inspection; host journal SHA/owner/mode and service-active reconciliation; privileged, bounded service-log reads around the reported smoke. One initial SSH stdin script failed because PowerShell emitted BOM/CRLF; a base64/UTF-8 rerun succeeded. No gameplay mutation occurred.
- Recovery: none required. The installed second hotfix remains live, but the reported spawn issue is not fixed. Do not claim success until a new-body regression and a real player smoke pass.
- Next action after explicit resume: inspect and replace the direct `GameTicker.Respawn` call in `Content.Server/_NF/Commands/GhostRespawnCommand.cs` with an awaited deep-cryo respawn preparation API, beginning with `rg -n "ghostrespawn|BeginPresenceRelease|ReleaseLuaMCharacterPresenceAsync|TryResumeLocalPlayableBody" Content.Server Content.IntegrationTests`.

﻿## 2026-07-25T21:31:24Z -- post-hotfix production health check

- Scope: read-only verification after the suspended-body ghostrespawn repair; no service, package, configuration, database, round, ship, or player state was changed.
- Journal reconciliation: repository and installed `/opt/monolith-ds/AI_SERVER_JOURNAL.md` matched SHA256 `29e8d5e5cc528a76e75b07c69d35d520b16021ca0955bb11cced719dc0971a4f`; installed owner/mode remained `root:root`/`0644`.
- Health: `monolith-ds.service` was active; `/status` reported round 172 running with three players; `/info` advertised the deployed hotfix client/build `70e83304ffe67fed9ef33c68256606e1c1bcbfc54de16e5ec8d0afa7818166eb`.
- Bounded log check: no visible `Blocked fresh spawn`, local-resume, presence-authority, ghostrespawn, cryo, exception, or error entry appeared since the 21:16 UTC deployment window. The SSH account cannot see every privileged journal field, so a player smoke remains the decisive functional check.
- Journal installation: the first local PowerShell wrapper failed at parse time because && is unsupported and made no host change. The corrected sequential scp/SSH command installed the journal byte-identically; SHA256 e37357896c3adc9a3785231d3192b0ddff7281cb751f3646ec75178c0f6f34b9, oot:root/ 644, service active.
- Recovery: none required; the deployed rollback set remains the one recorded in the preceding deployment entry.
- Next action: affected player performs one ghostrespawn -> join attempt; if it fails, immediately capture the bounded current-round lifecycle logs without recording player identifiers.
## 2026-07-25T21:18:17Z -- suspended Playable-body ghostrespawn hotfix deployed and verified

- Result: deployed the second operator-authorized ghostrespawn repair. Exact locally suspended Playable bodies can now be restored only when their component and in-memory suspension record agree; untracked nullspace bodies and all duplicate/foreign/pending authority cases remain fail-closed.
- Release: source ec61b7903720093ec6d7b7e51cf9e5b968c13b9e616ef959eca1fd22f668c96; server 547adf2c6d35fbf18905764e6690af87b6f48b9fce6f5915ee3e07ae80c6ccb2; client/build 70e83304ffe67fed9ef33c68256606e1c1bcbfc54de16e5ec8d0afa7818166eb; receipt 1c68237cd34ae2504e89e4b270ecc05334e66d6a7287c1df5476524bdd8ba2d; installed server DLL 9cfd8a133a7eb90208fce008af623abe4fb984e2009a7b90521835d3db858cd3.
- Safety/deployment: initial forced deploy stopped safely at HTTP 409 with seven sessions. A scoped non-loopback UDP/1212 ingress guard plus the authorized service restart created a zero-session window. Authenticated ship-save barrier cbac022-b9ee-46f4-9ec8-06ad6eebdb0a completed at 2026-07-25T21:16:26.327460Z with attempted/saved/failed/activeRemaining all zero and publication frozen. Guarded swap/restart succeeded as luam-20260725-ghostrespawn-suspended-hotfix; the temporary guard was deleted and proved absent.
- Recovery: /opt/monolith-ds/backups/data-luam-20260725-ghostrespawn-suspended-hotfix.tar.gz passes gzip -t, is oot:root mode  600, size 111,332,917 bytes. Server rollback is /opt/monolith-ds/backups/server-luam-20260725-ghostrespawn-suspended-hotfix; config rollback is /opt/monolith-ds/backups/server_config-before-luam-20260725-ghostrespawn-suspended-hotfix.toml.
- Health: service active with NRestarts=0, ExecMainStatus=0; round 172 lobby; exact new client version/URL advertised; no warning-or-higher service entry in the bounded post-deploy window. Root storage is 87% used with 5,599,088,640 bytes free. One combined postcheck's Python JSON quoting failed after all preceding health/hash/archive checks; a direct PowerShell JSON parse then verified the exact client metadata.
- Next action: affected player joins, runs ghostrespawn, then joins once; inspect bounded logs for successful reattachment and absence of durable presence authority Playable is active.
## 2026-07-25T21:14:55Z -- suspended-body hotfix client published; server barrier refused active sessions

- Scope: second operator-authorized ghostrespawn hotfix. Full source gate passed with productionEligible=true; source package ec61b7903720093ec6d7b7e51cf9e5b968c13b9e616ef959eca1fd22f668c96, server package 547adf2c6d35fbf18905764e6690af87b6f48b9fce6f5915ee3e07ae80c6ccb2, client/build 70e83304ffe67fed9ef33c68256606e1c1bcbfc54de16e5ec8d0afa7818166eb, receipt 1c68237cd34ae2504e89e4b270ecc05334e66d6a7287c1df5476524bdd8ba2d.
- Client publication succeeded and returned HTTP 200. Server preflight found round 170 running with seven players. The forced guarded deploy verified staging/CDN/config inputs, then the authenticated ship-save barrier returned HTTP 409 and stopped before service stop, live swap, data backup, config replacement, database mutation, or ship mutation.
- Recovery: no server rollback is needed; the previous server remains active. The new client will be advertised only after the matching server package is installed.
- Next action: create an operator-authorized zero-session window with the scoped UDP/1212 ingress guard and service restart, rerun the same guarded deploy with required data backup, remove the guard, and verify health/artifact/backup facts.
## 2026-07-25T20:32:00Z -- load-audit journal mirror verified

- Final mirror result: SHA256-gated staging verification returned `8c3c58543510749e925fcea6a494ce4789825d06771a3299bb22447e86ecd676`. The journal was installed at `/opt/monolith-ds/AI_SERVER_JOURNAL.md`, verified with the same SHA256 and `root:root` mode `0644`; `monolith-ds.service` remained active. No game, package, configuration, database, round, ship, or player state changed.
- Next action: continue ordinary load observation; if players report lag, repeat the bounded 25-second Prometheus delta.
## 2026-07-25T20:28:37Z -- production load audit: stable at normal tick cadence

- Scope: read-only 25-second production load audit after the operator asked whether the server is under normal load. No service, package, configuration, database, round, ship, or player state was changed.
- Journal reconciliation: repository and installed journal matched SHA256 `82eabd5dfe50fb18edaea9550457ce0d38ee6a50dcd99b0ff9679e31a3aefb8f`; installed copy is `root:root` mode `0644` before this entry.
- Service/game: `monolith-ds.service` is active with `NRestarts=0`, `ExecMainStatus=0`, and PID 603910; round 170 is running with six players.
- Performance: over 25 seconds, ticks advanced 502 (20.08/s). Mean frame time was 28.38 ms (14.2461684 s / 502 frames), remaining below the 50 ms 20 Hz tick budget. Mean EntitySystems work was 26.57 ms/tick (13.3368153 s / 502); GameState work was 1.57 ms/tick (0.7894127 s / 502). Process CPU was 96.7% of one core (24.186369 CPU seconds / 25 s); host load was 2.16/2.06/2.09 on the known four-vCPU host. No `Cannot keep up`, error, exception, or fatal entry was visible in the permitted service journal query since 20:00 UTC.
- Capacity: process RSS was stable near 4.66 GiB (4,998,991,872 to 5,000,069,120 bytes); managed memory rose from 3.45 to 3.49 GiB during the short sample, not an exhaustion signal. Host has about 11 GiB available RAM, 42 MiB of 4 GiB swap used, and 5.9 GiB free on `/` (85% used).
- Outcome/recovery: the server is healthy and keeping full 20 Hz cadence, with expected CPU-bound simulation work but no current overload or resource-pressure indicator. No recovery action is required. Continue ordinary observation; next bounded check if players report lag: repeat the 25-second Prometheus delta and inspect `Cannot keep up` count.
- Commands: read required local journals; hash/mode reconciliation; bounded `systemctl`, `/status`, `uptime`, `free`, `df`, `ps`, Prometheus, and service-journal reads. An initial combined metrics probe ended early when `head` closed its pipe (`curl` exit 23), and an earlier shell line had PowerShell expansion around a remote command substitution; useful preceding read-only facts were separately rerun by a successful base64/stdin script.
## 2026-07-25T19:50:59Z -- ghostrespawn playable-body recovery deployed and verified

- Scope/result: deployed the operator-authorized emergency fix for ghostrespawn/occupied-capsule loops. The new server reattaches a joining player only to one exact valid local Playable body under the current server/round authority; duplicate, foreign, suspended, pending, deleted, and nullspace cases remain fail-closed.
- Release: source package 2beee41f8af6810aef109362fecc8cec5f319d5ef9693b31bc4df39c9117afb4; server package 716e92f76b4261b3741c9bd035d65923356329ce0a19bb1e8a1d433a2531f069; client/build version dd1c1e1178822374c74bc5e2f0e67ef98c14c43e67090bf05fab89f8e6b7771d; receipt cb56b027b6399a238447639fbb385b6324c2dd9a599582b6c22b4ca1fc22b25; installed server DLL 6d81b672728c95e9baad1ced6a4e2b497b23c5983ec74c62478213521bbb54e9.
- Safety sequence: after the initial HTTP 409 barrier refusal with four players, a temporary inet luam_deploy_guard blocked only non-loopback UDP/1212 and the explicitly authorized service restart disconnected sessions. A CRLF/BOM remote-script attempt partially executed the guard/restart but ended with a malformed service-name check; a clean explicit restart recovered the service, round 169 became healthy with zero players, and the guarded deployment then completed. The temporary nft table was deleted and proved absent.
- Deployment: authenticated ship-save barrier 56e9d70f-3a9d-4c9a-80c6-f21c1e5e0e56 completed at 2026-07-25T19:48:32.138794Z with attempted/saved/failed/activeRemaining all zero and publication frozen. Package swap/restart succeeded as luam-20260725-ghostrespawn-hotfix; round 170 is healthy and advertises the exact new client URL/version.
- Recovery: data archive /opt/monolith-ds/backups/data-luam-20260725-ghostrespawn-hotfix.tar.gz passes gzip -t, is oot:root mode  600, and is 110,340,210 bytes. Server rollback is /opt/monolith-ds/backups/server-luam-20260725-ghostrespawn-hotfix; config rollback is /opt/monolith-ds/backups/server_config-before-luam-20260725-ghostrespawn-hotfix.toml.
- Health/storage: service active with NRestarts=0 and ExecMainStatus=0; /status round 170 lobby with zero players; /info advertises exact version/client; no warning-or-higher service entries were found in the bounded post-deploy window. Root storage is 85% used with 6,344,290,304 bytes free.
- Next action: have the affected player reconnect and join once; verify the session controls its preserved body and no occupied-capsule message recurs. If the old body belonged to the previous round, normal fresh spawning is expected instead of cross-round reattachment.
## 2026-07-25T19:44:30Z -- ghostrespawn hotfix client published; server barrier refused active sessions

- Scope: deploy the operator-authorized emergency fix that reattaches a ghosted player to their one exact local Playable body instead of leaving them blocked by the active presence lease. Source package 2beee41f8af6810aef109362fecc8cec5f319d5ef9693b31bc4df39c9117afb4 is production-eligible and independently verified; server package SHA256 is 716e92f76b4261b3741c9bd035d65923356329ce0a19bb1e8a1d433a2531f069; client/build version is dd1c1e1178822374c74bc5e2f0e67ef98c14c43e67090bf05fab89f8e6b7771d; release receipt SHA256 is cb56b027b6399a238447639fbb385b6324c2dd9a599582b6c22b4ca1fc22b25.
- Client publication: installed the exact receipt-bound client archive and verified HTTP 200. The first command omitted the mandatory Version argument and failed before mutation; the corrected command succeeded.
- Server attempt: preflight found round 167 running with four players and about 7.1 GB free. The guarded forced deploy extracted and verified staging/CDN/config inputs, then the authenticated ship-save barrier returned HTTP 409 because sessions remained connected. It stopped before service stop, live server swap, data backup, config replacement, database mutation, or ship mutation.
- Recovery: no server rollback is needed because the live server was not replaced. The prior comprehensive server remains active; the new matching client is published but will not be advertised until the server hotfix is installed.
- Next action: use the operator-authorized restart to disconnect the remaining sessions, prevent immediate reconnection during the short swap, rerun the same guarded deploy with -RequireDataBackup, then remove the temporary ingress guard and verify service/status/info/hash/archive/storage.
# Monolith-DS production server journal

This is the persistent operational handoff for AI-assisted production-server work. Read it together with `.agents/ITERATION_LOG.md` before accessing the host. The installed mirror is `/opt/monolith-ds/AI_SERVER_JOURNAL.md`.

## 2026-07-25T18:35Z -- comprehensive LuaM release deployed and verified

- Scope: deploy the explicitly authorized comprehensive LuaM release containing deep-cryo/presence authority, persistent-ship, silo, map-load, AI, bank/PDA, reagent-dispenser, and restored-mind sanitization repairs. Source package `4f6ec312d363255e7cf0a2bb769e71c2569a175fceb6594f46771bcfdbff452d` was production-eligible and independently verified; server package SHA256 was `481cfa3b93497a1b66a1f211ba665cce8f2c3c7067e32e3bdc871113f3106a7e`, client/build version is `3bb954e677fe31a7db348364da0e0f8d90d4ec65a63127beab2f4952353298e7`, and release receipt SHA256 was `6197aa59a399bec17108e46231d31f241210b3fd70dede2ff8cb014496aaaa15`.
- Safety sequence: client static publication succeeded first and returned HTTP 200. Two server attempts while sessions were active stopped safely at the authenticated ship-save barrier with HTTP 409 before stopping or replacing the service. A temporary `inet luam_deploy_guard` blocked new non-loopback UDP/1212 sessions; after the existing sessions drained to zero, the guarded deployment completed and the table was deleted and proved absent.
- Deployment: authenticated ship-save barrier `e5f1a3cc-73a4-4e08-8bb1-a7d3a042d19d` completed with attempted/saved/failed/activeRemaining all zero and froze publication. The script stopped the service, created data/config/server rollback material, swapped the verified package, and restarted successfully as tag `luam-20260725-comprehensive`.
- Recovery: data archive `/opt/monolith-ds/backups/data-luam-20260725-comprehensive.tar.gz` passes `gzip -t`, is `root:root` mode `0600`, size 109,942,130 bytes. Server rollback is `/opt/monolith-ds/backups/server-luam-20260725-comprehensive`; config rollback is `/opt/monolith-ds/backups/server_config-before-luam-20260725-comprehensive.toml`.
- Health: `monolith-ds.service` active with `NRestarts=0` and `ExecMainStatus=0`; `/status` reports round 167 and the server is accepting the new build. `/info` advertises exact version `3bb954e677fe31a7db348364da0e0f8d90d4ec65a63127beab2f4952353298e7` and its matching external client URL. Installed `Content.Server.dll` SHA256 is `03c402b58308f632864a198414578022881217aa483dd146783807dfd3afcee1`. Root storage is 83% used with about 6.6 GiB free. Startup logs show no fatal/migration failure; observed warnings were an obsolete `RandomItem`, disconnected-client sends, and one transform attachment warning.
- Commands: complete source gate/tests/smokes; source verification; binary build and surface audit; client/server dry runs; client publication; bounded status polling; temporary nft ingress guard; final deploy with required data backup; service/status/info/hash/archive/log/storage verification. The two barrier-refused attempts and one public-status 503 during an independently starting server were partial/failed pre-deployment checks and made no server-file swap. One local PowerShell HEAD request failed from a cmdlet null-reference after `/info` had already proved the exact URL; server-side publication verification had already returned HTTP 200.
- Policy/next action: deployment is frozen again. Run a real player smoke for login, deep cryo enter/wake/re-enter, restored ship summon, reagent dispenser slots, and absence of duplicated spirits; do not mutate production further without new explicit authorization.

## 2026-07-25T17:39Z -- duplicate-mind restoration hotfix accepted for authorized rollout

- Scope: incorporate the read-only production diagnosis of repeated copied spirits into the verified release after the operator explicitly confirmed the production update. No production package, service, database, round, ship, or player mutation has occurred at the time of this entry.
- Change: restored shuttle graphs now discard serialized `MindComponent` entities and disconnect their restored mind-container links before the grid is activated. Minds are runtime player ownership and must not be portable ship content. The exact full-grid regression passed 1/1 after correcting a fixture assertion that referred to an original mind the fixture had deliberately deleted before restoration.
- Authorization/recovery constraints: the release policy permits `server-release` and `client-static` until `2026-07-26T00:00:00Z`. The deploy scripts must still pass package hashes, release receipt, data/config/server backup, ship-save, player-safety, migration, and post-start health barriers. Root storage was recently 91% used, so backup/package space must be checked immediately before mutation.
- Commands: integration project build and exact one-worker full-grid regression; both final commands passed. No command was interrupted. No rollback is required because this entry precedes deployment.
- Next action: produce a clean committed source package and verified client/server binaries, execute all dry runs, then run the authorized deployment only if the current production preflight and backup gates pass.

## 2026-07-25T16:20Z -- release preparation preflight; deployment remains frozen

- Scope: reconcile the production journal and inspect current health before preparing a local release. The operator requires a separate confirmation immediately before any actual server update, so no package upload, client publication, migration, service restart, database mutation, or gameplay mutation was attempted.
- Journal reconciliation: the repository journal was newer than the installed host copy. The installed copy contained no newer operational entry, so the repository history was retained and this preflight entry was added before reinstalling the required mirror.
- Health/storage: `monolith-ds.service` is active with `NRestarts=0` and `ExecMainStatus=0`; loopback `/status` reports round 164, run level 1, and two players. Root storage is 91% used with 3,737,280,512 bytes available. The low free-space margin must be considered by the package/deployment backup gates.
- Local release facts: the release contract passed with `remoteDeployFrozen=true`. An initial readiness capture wrote its output inside the release-scoped `.agents` directory while the gate was hashing it, so that run failed and is not readiness evidence; the accidental file was moved outside the repository. A subsequent no-test readiness audit passed its static/dependency/feature checks but reported the expected frozen deployment state and a gateway smoke runner failure; full package preparation remains local-only pending clean committed input.
- Commands: repository and installed journal SHA256/owner/mode comparison; bounded installed journal heading read; `systemctl is-active/show`, loopback `/status`, and `df`; local release-contract and readiness scripts. No command was interrupted. Rollback is not applicable because production behavior and data were unchanged.
- Next action: commit the verified source batch, build and verify local release artifacts, then request explicit operator confirmation before running any non-dry-run production update command.

## 2026-07-24T21:27Z -- all-time game-account versus retained SSH brute-force comparison

- Scope: compare all game accounts/source addresses recorded by the current production database with source addresses present in retained SSH authentication-failure logs. This was aggregate/read-only security analysis; no usernames attempted over SSH, game account names, UUIDs, hardware IDs, or raw player records are retained here.
- Pre-access reconciliation: repository and installed journal SHA256 matched at `4bd2670ac178e1f8a32007ab65493d2d7fd4cc7ce7b8eced8b97207eb9d5d9ae`.
- Game population: the current database contains 102 player accounts. `connection_log` covers all 102 accounts with 682 connection rows from 141 distinct game-source addresses between `2026-06-29T08:01:58Z` and `2026-07-24T21:18:24Z`; no connection row is marked denied.
- SSH attack evidence: retained `ssh` journald data covers `2026-07-16T13:57:46+03:00` through `2026-07-25T00:26:12+03:00`. It contains 3,690 `Failed password` events from 489 source addresses and 2,008 `Invalid user` messages from 449 addresses. PAM duplicates many failed-password events, so the broader raw marker count of 9,700 is not an independent attempt count and must not be reported as one. Fail2ban currently has 13 addresses banned.
- Correlation result: zero of the 567 distinct addresses seen across the broad retained SSH failure markers overlap any of the 141 game connection addresses. Zero current fail2ban-banned addresses overlap game addresses. Retained successful SSH logins came from three addresses, and none of those addresses appears among SSH failure sources; one is also a known game-source address, consistent with legitimate administration rather than brute force.
- Highest retained failed-password sources by count: `175.101.46.58` (767), `185.49.240.109` (408), `89.169.61.250` (279), `94.154.43.56` (179), `109.197.49.27` (133), `193.151.145.198` (72), `170.81.145.238` (30), and `130.49.129.81` (28). These are observational source addresses, not attribution to persons.
- Health/storage: `monolith-ds.service` remained active; `/status` reported round 160 with seven players; root storage remained 87% used with about 5.1 GiB free.
- Commands run: required journal reads and SHA256 comparison; Python SQLite URI `mode=ro` aggregates over `player` and `connection_log`; bounded `journalctl -u ssh`; `fail2ban-client status`; privacy-preserving Python parsing/correlation; `systemctl is-active`, `/status`, and `df`. An initial JavaScript wrapper failed at parse time before its nested command ran. A later PowerShell wrapper partially ran but local PowerShell captured Bash command substitution and attempted `C:\dev\null`; the final Python-over-SSH commands completed successfully.
- Mutation/rollback: no service, database, firewall, fail2ban, configuration, player, or round mutation occurred. Rollback is not applicable. Only this updated journal mirror is installed with `root:root` ownership and mode `0644`.
- Next action: if durable historical attribution is required, configure privacy-bounded aggregation before journald retention expires; do not infer identity from source IP alone.

## 2026-07-24T21:20Z -- read-only 24-hour connection-attempt count

- Scope: answer the user's request for the aggregate number of connection attempts during the preceding rolling 24 hours. No player identifiers, addresses, hardware identifiers, or individual connection records were retained or reported.
- Pre-access reconciliation: repository and installed journal SHA256 values matched at `d6c511638e0e98ebfbe23fdbd59f384a0b1e1379f52c389a010d7ec5b7beeb55`.
- Result: a read-only query of `connection_log` from cutoff `2026-07-23T21:20:16Z` through `2026-07-24T21:20:16Z` counted 33 connection attempts from 12 distinct account IDs. All 33 rows were not denied (`denied IS NULL`); zero rows had `denied = 1`. Recorded attempts spanned `2026-07-24 00:30:54Z` through `2026-07-24 21:18:24Z`.
- Health/storage: `monolith-ds.service` remained active; `/status` reported round 160 running with seven players; root storage remained 87% used with about 5.1 GiB free.
- Bounded operations: compared journal hashes over SSH; inspected bounded service journal/file inventory to locate the authoritative log; opened `/opt/monolith-ds/data/preferences.db` through Python SQLite URI `mode=ro`; inspected only table schemas and aggregate counts for the rolling window; checked service, `/status`, and root storage. One journal keyword search returned no matches; one quoted remote `sqlite3` attempt failed before querying because the host has no `sqlite3` CLI; one `/status` parsing wrapper fetched status but its Python projection failed from shell quoting, then a direct bounded `/status` request succeeded.
- Mutation/rollback: no game, database, service, configuration, player, or round mutation occurred. Rollback is not applicable. Only this required journal mirror was updated and installed with `root:root` ownership and mode `0644`. The first PowerShell pipeline installation changed line endings and therefore produced a different host hash; a binary `scp` plus `install` corrected it, after which local and host SHA256 matched at `f8d171b0be09ade0eccbaa8119a0957cc354d766a5088f6224901e95bed152a9`.
- Next action: if trend monitoring is desired, add a privacy-preserving aggregate report over `connection_log` grouped by hour and denied status, without emitting account IDs or addresses.

Never record secrets, credentials, raw environment files, private player data, or private keys here.

## 2026-07-22T11:19Z -- repeated deep-cryo report checked read-only

- Scope: correlate the new report that a character still does not enter deep cryo with the active production process, without touching the character, pod, round, database, binaries, configuration, or services. Both mandatory journals were reread first. The repository and installed journal SHA256 matched at `819981de185173feb323188c5375c7edd4a37f25ef81d7d5c8881f5ed109c76d`; the installed mirror was `root:root`, mode `0644`.
- The most recent 30-minute service window contained no deep-cryo or entity-serialization marker, so no new server-side capture attempt was visible during that exact window. The bounded three-hour window still contains the previously diagnosed 18 `entity-serialization-failed` refusals and 18 matching `KillTrackerComponent` serializer exceptions. No player or entity identifier is retained here.
- This confirms that production still exhibits the already diagnosed old-build failure when an affected damaged character reaches capture. The refusal occurs before the snapshot database write, so it does not delete the body, inventory, or an existing durable save. The local `KillTracker` correction remains undeployed while its accumulated cryo/ship/silo release batch is under final crash-window review.
- Current health/storage: `monolith-ds.service` active, `NRestarts=0`, `ExecMainStatus=0`; loopback `/status` healthy in round 151/run level 1 with two players; root filesystem 42,174,005,248 bytes total with 13,236,277,248 bytes available (67% used). No restart, deployment, player mutation, database mutation, or gameplay mutation was performed. Recovery/rollback is not applicable; the only host mutation after inspection is installation of this required journal mirror.

Commands and outcomes:

```powershell
(Get-FileHash -Algorithm SHA256 -LiteralPath 'Tools/AI_SERVER_JOURNAL.md').Hash.ToLowerInvariant()
ssh monolith-new "<installed-journal SHA256/owner/mode pre-check>"
ssh monolith-new "<bounded service/restart/status check plus 30-minute cryo/serializer tail>"
ssh monolith-new "<identifier-free three-hour refusal and KillTracker exception counts plus storage check>"
```

Result: the server is healthy and unchanged; no fresh capture event appeared in the last 30 minutes, while the existing affected-character attempts remain the same deterministic serializer failure on the old production build.

Interrupted/partial operations: the first identifier-free aggregation wrapper was parsed by local PowerShell because remote command substitutions were insufficiently protected and never reached SSH. A second quoting attempt reached the host but split spaced arguments and produced only parser/grep diagnostics. The final bounded commands used simple fixed filters and succeeded. Neither failed wrapper changed local or host state.

Next action: stop retries on the current binary, finish and rerun the local two-phase cryo publication regressions, then schedule a separately authorized backed-up maintenance deployment. If a player tries the pod again before deployment and no capture marker appears, inspect the pod interaction/do-after path as a distinct pre-capture symptom.

## 2026-07-22T10:30Z -- current ship/cryo/silo release status verified read-only

- Scope: determine whether the locally repaired saved-ship, deep-cryo, and ore-silo behavior is already live, while leaving the active round, players, ships, snapshots, database, configuration, binaries, services, and network policy unchanged. Both mandatory journals were reread before host access.
- The repository and installed server-journal SHA256 matched at `0ca5e1857a0e84455e3a28ea32c50616871184d50999a0c6638629be154f7a40`; the installed mirror was `root:root`, mode `0644`, size 142,554 bytes.
- Both `monolith-ds.service` and `luam-ai-gateway.service` were active. The game service reported `NRestarts=0` and `ExecMainStatus=0`. Loopback `/status` was healthy in round 151/run level 1 with three players. Root storage was 42,174,005,248 bytes total with 13,254,885,376 bytes available (67% used).
- Production still advertises immutable client/build version `9643610e6c726c773be4b31c7420866af55b3f8733250a49dc2a8bd9b209e3e0`. Installed `Resources/Assemblies/Content.Server.dll` SHA256 is `dd85906e581d7f129ed2719af774da79ec8221d65d47cbc9b22b279e4ac12bb2`, with an installed timestamp predating the later local ship rollback, damaged-character cryo, nested capability, and silo-link fixes. Those fixes are therefore not yet deployed.
- Local release policy remains frozen and local-package-only. A read-only local readiness audit passed every executed gate and found no release-scope issue, but correctly reported `productionEligible=false` because remote deployment is frozen and two test files remain untracked. No package, upload, client publication, restart, or deployment was attempted.
- Recovery/rollback: not applicable; all host checks were read-only. The only host mutation after this inspection is installation of this required journal mirror, which does not restart either service or change gameplay.

Commands and outcomes:

```powershell
(Get-FileHash -Algorithm SHA256 -LiteralPath 'Tools/AI_SERVER_JOURNAL.md').Hash.ToLowerInvariant()
ssh monolith-new "<installed-journal hash/owner/mode, service state/restarts, installed server DLL path/hash, loopback status/info, and storage checks>"
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/test_luam_release_contract.ps1 -Json
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/check_luam_release_ready.ps1 -AllowUntracked -Json
```

Result: the active server is healthy but still runs the earlier `964361...` build, so the newly verified local behavior cannot yet be claimed live. The release contract returned `ok=true`; local readiness returned `ok=true`, `productionEligible=false`, with only the intentional freeze and two untracked test files as release-status warnings.

Interrupted/partial operations: the first host wrapper verified the journal and both service states, then stopped at an obsolete `/opt/monolith-ds/server/Content.Server.dll` path. A bounded `find` located the actual immutable assembly at `/opt/monolith-ds/server/Resources/Assemblies/Content.Server.dll`; the remaining hash/status/info/storage checks were rerun successfully. The first journal-install wrapper uploaded the exact temporary file but PowerShell interpreted remote command substitution locally, so the malformed SSH script exited before `install`. A corrected SHA256-gated wrapper uploaded the same exact path, installed the mirror as `root:root` mode `0644`, removed the temporary file, and verified both services active plus loopback status healthy. Neither partial command changed gameplay or the database.

Next action: finish the independent local completion audit and its newly identified docking/restore-publication regressions. Only after all focused and combined checks pass may a fresh release be prepared; production still requires new explicit authorization, policy-bound artifacts, a checked data backup, and a verified zero-player window.

## 2026-07-22T08:56Z -- damaged-character deep-cryo serialization failure diagnosed; fix prepared locally

- Scope: diagnose the live report that a character would not enter cryosleep, without deleting or moving the character, forcing cryo, changing the database, restarting the active round, or deploying code. Both mandatory journals were reread first. Repository and installed journal SHA256 matched at `c624b22155604c2ec4f5c8579ce6086bf4662677e6cf8034710b751c6a8ad90e`; the installed copy was `root:root` mode `0644`.
- Identifier-bounded live logs contained 18 identical refused deep-cryo stores during the reported interval. Each failed at synchronous entity capture with `entity-serialization-failed` before a database write. The underlying exception was `Yaml mapping keys must serialize to a ValueDataNode` while serializing `KillTrackerComponent`; the standard failure path restored the original mind/body attachment and ejected the body. No character, inventory, or durable snapshot was lost or duplicated.
- Confirmed cause: spawned players receive a kill tracker, and positive damage populates its `Dictionary<KillSource, FixedPoint2>`. A polymorphic `KillSource` becomes a YAML mapping and cannot serve as a scalar mapping key. This explains why undamaged test bodies passed while the live damaged body consistently failed.
- Local correction, not deployed: make only `LifetimeDamage` runtime state while retaining serialized `KillState`. The ledger is current-life attribution, can contain live entity or player identifiers, is absent from all prototype YAML, and was already cleared deliberately on deep-cryo restore. No payload/database migration is required because non-empty ledgers could not previously serialize. A real-damage capture/load regression proves the live ledger is not mutated by capture, does not enter the payload, and is empty after restore.
- A second local retry safeguard was completed in the same bounded subsystem: serialized payloads continue to discard active do-after operations, but capture returns an empty do-after capability to a live body for database-failure retries, and canonical wake restoration re-adds that capability when required by the current body prototype. The final deep-cryo runtime/database plus ServerNews suite passed 17/17; server compilation passed. Player news entry `2026072204` records the implemented behavior. No release package or deployment was produced.
- Final health/storage: `monolith-ds.service` and `luam-ai-gateway.service` active; game `NRestarts=0` and `ExecMainStatus=0`; round 151 running with four players; loopback `/status` healthy; root filesystem 42,174,005,248 bytes total with 13,267,181,568 bytes available, 67% used. Recovery/rollback is not applicable because all production inspection was read-only. The finalized journal was installed byte-identically at `/opt/monolith-ds/AI_SERVER_JOURNAL.md`, owner `root:root`, mode `0644`; this journal-only mirror did not restart either service.

Bounded operations and outcomes:

```powershell
Get-Content -LiteralPath .agents/ITERATION_LOG.md -Raw -Encoding utf8
Get-Content -LiteralPath Tools/AI_SERVER_JOURNAL.md -Raw -Encoding utf8
ssh monolith-new "<installed-journal SHA256/owner/mode plus service/restart/status/storage pre-check>"
ssh monolith-new "<bounded recent cryo and serializer stack inspection; player identifiers not retained>"
ssh monolith-new "<identifier-free refusal/error counts plus final service/restart/status/storage check>"
rg -n -i "cryo|KillTracker|TryCapturePayload|TrySaveEntity|DoAfter" Content.Server Content.Shared Content.IntegrationTests Resources
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-restore -m:1 --verbosity:minimal --filter "FullyQualifiedName~LuaMDeepCryoRuntimeTest|FullyQualifiedName~LuaMDeepCryoPersistenceTest|FullyQualifiedName~LuaMServerNewsChangelogTest" -- NUnit.NumberOfTestWorkers=1
dotnet msbuild Content.Server/Content.Server.csproj /t:Compile /m:1 /v:minimal
scp Tools/AI_SERVER_JOURNAL.md monolith-new:/tmp/AI_SERVER_JOURNAL.20260722T0856Z.md
ssh monolith-new "<SHA256-gated journal install, exact temporary-file removal, owner/mode/service/status verification>"
```

Result: the live failure is a deterministic non-empty kill-attribution serialization defect; its local fix and repeat-action safeguard passed the full focused regression set. Production remains on the previous binary, so affected players should stop retrying until a separately authorized maintenance deployment.

Interrupted/partial operations: the first local regression failed compilation because its setup directly mutated an access-restricted component field; the corrected setup used real damage. The corrected pre-fix test then intentionally reproduced the live YAML-key failure. A later three-test run used an overly broad payload assertion that also matched the expected `missingComponents` marker; after narrowing it to actual transient operation fields, the exact run passed. None of these local checks affected production.

Next action: during a separately approved maintenance window, build and deploy the accumulated server batch through the normal data-backup gate, then verify damaged-character enter/wake/re-enter behavior before closing the live incident.

## 2026-07-22T08:22Z -- stored-ship call failure diagnosed read-only

- Scope: diagnose one player's inability to call a previously stored ship without attempting another restore, altering any ship or player record, moving entities, restarting the round or services, or deploying code. Both required journals were reread first. The repository journal was newer than the installed mirror only by previously completed local-test facts; the host had no newer operational fact to reconcile.
- A bounded exact-account read-only database check confirmed that current snapshots remain in `Stored` state with no active lease or quarantine. Failed call claims returned safely to `Stored`; no durable ship save was deleted or stranded. SQLite `PRAGMA quick_check` returned `ok`. Player, ship, snapshot, and entity identifiers were deliberately omitted from this journal.
- The first observed call in the current server process passed deserialization and graph/manifest validation, then failed because the selected free shipyard gate did not provide valid docking geometry for that ship. Later calls for the available stored snapshots repeatedly failed earlier with `restored-entity-graph-or-manifest-mismatch`.
- Source inspection confirms an incomplete post-load rollback boundary. The persistence loader owns and can synchronously delete the complete set of entities created during deserialization, including auto-included nullspace support. After successful loading, however, the orchestrator retains only the restored grid; when placement rejects it, `DeleteGrid` queues deletion of that grid root. Separate support roots in nullspace can therefore survive. This is a confirmed cleanup defect and is consistent with the observed transition from a placement failure to later graph mismatches. The precise path from surviving support state to the later manifest result still needs a focused integration regression and is not claimed as proven here.
- Recovery: stop repeated call attempts in the current process. A controlled restart when the server is empty will clear transient restored entities; the next attempt should use a genuinely suitable free gate. Permanent correction is to retain the full created-entity set beyond deserialization and use bounded synchronous full-set cleanup after placement rejection, with a regression that includes an auto-included nullspace entity and proves a subsequent retry succeeds. No rollback is needed because this operation made no gameplay or database mutation.
- Health/storage at the check: `monolith-ds.service` and `luam-ai-gateway.service` active; game `NRestarts=0` and `ExecMainStatus=0`; round 151 with five connected players; loopback `/status` healthy; root filesystem 42,174,005,248 bytes total with 13,279,170,560 bytes available, 67% used. No restart was authorized or performed. The finalized journal was installed byte-identically at `/opt/monolith-ds/AI_SERVER_JOURNAL.md` with owner `root:root` and mode `0644`; the journal-only mirror did not restart either service.

Bounded operations and outcomes:

```powershell
Get-Content -LiteralPath .agents/ITERATION_LOG.md -Raw -Encoding utf8
Get-Content -LiteralPath Tools/AI_SERVER_JOURNAL.md -Raw -Encoding utf8
ssh monolith-new "<installed-journal SHA256/owner/mode and repository-versus-host heading comparison>"
ssh monolith-new "<bounded service/restart/status/storage checks and database-file discovery>"
ssh monolith-new "<read-only Python SQLite quick_check plus exact-account snapshot/lease/quarantine lifecycle query; identifiers not retained>"
ssh monolith-new "<identifier-filtered restore sequence and failure aggregate; raw identifiers not retained>"
rg -n "TryRestoreSnapshot|DeleteGrid|RestoreCoreAsync|createdEntities|manifest" Content.Server Content.Shared Content.IntegrationTests
scp Tools/AI_SERVER_JOURNAL.md monolith-new:/tmp/AI_SERVER_JOURNAL.20260722T0822Z.md
ssh monolith-new "<SHA256-gated journal install, temporary-file removal, owner/mode/service/status verification>"
```

Result: durable stored-ship records remain intact and reusable after recovery; a first gate-geometry failure was followed by repeated graph mismatches in the same process, and the placement-failure cleanup scope is incomplete. All production inspection was read-only apart from the required journal mirror.

Interrupted/partial operations: the first health wrapper reached its final `sqlite3 -readonly` check and exited 1 because the CLI is not installed. The check was rerun through Python's SQLite module using a read-only URI and returned `ok`; preceding health facts were also reconfirmed. The first journal-install wrapper exited before `install` because nested quoting left GNU `cut` without its delimiter; the operation failed closed after the temporary upload and did not replace the installed journal. A corrected hash-gated wrapper then installed the finalized copy and removed the exact temporary file. No gameplay or database state changed.

Next action: add the focused placement-rejection/full-created-set cleanup retry regression locally; schedule a controlled service restart only when no players are connected, then retry the stored ship at a gate with sufficient docking geometry.

## 2026-07-22T07:18Z -- restored shuttle gravity desynchronization diagnosed read-only

- Scope: diagnose the reported loss of gravity aboard the active shuttle after its expedition visit without moving or mutating any shuttle, player, map, round, service, configuration, or database. Both required journals were reread first. Repository and installed journal SHA256 matched at `eeeae009f0bad2d8812d06ae766064d3faef4b25c91ba5f460fa0e5f9af84e36`; the installed copy was `root:root` mode `0644`.
- At `07:08:48Z`, round 151 was running with two players and both services active. Identifier-free journald aggregation found no gravity/power/EMP marker, damage/explosion marker, expedition-end marker, or FTL fault since the expedition began. A new expedition FTL transition was detected at `07:09:33Z`; through `07:15Z` there was no inability-to-FTL, invalid state, stuck-expedition, anti-collision correction, or general shuttle fault.
- A read-only round-151 admin-log aggregate, processed remotely without emitting messages, JSON, identities, entity IDs, or coordinates, found one existing `GravityGeneratorMini` used between `07:00:50Z` and `07:08:37Z`. It was switched off and immediately back on six times between `07:01:05Z` and `07:05:55Z`, with on as the last state. No damage, destruction, unanchor, cable-cut, APC, SMES, anti-gravity equipment, gravity anomaly, or gravity-smite marker was present. Two additional mini gravity generators were spawned at `07:08:29Z` and `07:08:44Z`, but no log proves either was anchored to powered cabling.
- Source and loader ordering identify the defect. Full-grid persistence restores serialized `PowerChargeComponent.Active`, while `GravityGeneratorComponent.GravityActive` is runtime-only. Deserialization precedes component/map initialization, so a previously active generator can load with `PowerCharge.Active=true` and `GravityActive=false`. Grid gravity initialization consequently sees no gravity-active generator. `PowerChargeSystem.OnMapInit` refreshes visuals/load but does not raise an activation event, and later ticks also emit no activation edge while `Active` remains true.
- `NFVirologyLab` always selects a real biome and enables gravity on its expedition map. Effective gravity is enabled when either the current grid or map has gravity, so the expedition map masked the shuttle-grid defect. The hyperspace map has no gravity, and FTL/docking contains no generator-state repair path. The new map transition exposed the existing restore mismatch; it did not damage the generator.
- Immediate recovery is player-operated and requires a full state edge: leave the existing mini generator off until its normalized charge reaches zero (up to approximately 100 seconds), then provide at least 500 W, switch it on, and wait until full charge (up to approximately 100 seconds). Only the zero/full `Active` transitions raise the deactivation/activation events; quick toggles do not. A newly spawned replacement must be anchored on powered cabling.
- Current health/storage at `07:17:54Z`: `monolith-ds.service` and `luam-ai-gateway.service` active; loopback `/status` HTTP 200; root filesystem 42,174,005,248 bytes total with 13,327,101,952 bytes available, 67% used. Recovery/rollback: none required because all game/server/database checks were read-only. The finalized journal was installed byte-identically at `/opt/monolith-ds/AI_SERVER_JOURNAL.md`, owner `root:root`, mode `0644`, without restarting either service. No ServerNews entry was added because no player-facing behavior changed.

Bounded operations and outcomes:

```powershell
Get-Content -LiteralPath .agents/ITERATION_LOG.md -Raw -Encoding utf8
Get-Content -LiteralPath Tools/AI_SERVER_JOURNAL.md -Raw -Encoding utf8
ssh monolith-new "<installed journal SHA256/owner/mode pre-check>"
ssh monolith-new "<identifier-free service/status/metrics and categorized journald aggregate since 06:20Z>"
ssh monolith-new "<read-only SQLite schema and round-151 gravity/power/admin-event aggregates without message, JSON, identity, entity ID, or coordinate output>"
ssh monolith-new "<identifier-free current FTL/fault aggregate and health/storage check>"
rg -n "GravityGenerator|PowerCharge|GravityComponent|NFVirologyLab|FTL" Content.Server Content.Shared Resources --glob '*.{cs,yml}'
```

Result: the restored shuttle's grid-gravity activation state is desynchronized. Expedition map gravity concealed it until the next FTL/map transition. No production behavior was changed.

Interrupted/partial checks: one broad local gravity search ended exit 1 after `Select-Object -First` closed the pipe, and one local `LogType` read used the wrong dotted project path. Corrected targeted reads succeeded; no host command failed.

Next action: use the complete off-to-zero, powered on-to-full workaround now. Then add a focused full-grid restore regression and reconcile runtime `GravityActive` from the restored power-charge state before any separately authorized deployment.

## 2026-07-22T06:53Z -- player shuttle located at normal virology-lab expedition

- Scope: identify the destination of the reported two-player shuttle jump without moving the ship, reading private player records, or issuing an in-game command. Both required journals were reread; the installed copy initially matched repository SHA256 `7b15572a69b69770c52420885bd41a382b4be6778532ef4fec5407ccf6c5c122`, owner/mode `root:root`/`0644`, and the first bounded journal update installed SHA256 `1a45edaef75c317c5f9af9038be91eb0c67fbc55c3dbcea58d0a583753cd7b34`. The game service remained active.
- The user supplied the visible label `Р СљР С‘Р С”РЎРѓР С‘Р Р…Р В°-84-E`. Identifier-free correlation proved that `NFVirologyLab` generation began there at `06:27:32Z`, expedition FTL began at `06:27:47Z`, and arrival occurred at `06:28:41Z`. No FTL failure, anti-collision correction, invalid state, or stuck-expedition error was logged.
- `Р СљР С‘Р С”РЎРѓР С‘Р Р…Р В°-84-E` is a normal procedural salvage expedition, localized as a virology laboratory, not either later temporary scrap-dungeon event grid. Expedition time modifiers are 2,700-3,600 seconds; the active console timer is authoritative. The prior 900-second event-grid warning for this destination was withdrawn.
- This expedition generation is temporally inside the earlier entity surge from 181,922 at `06:20Z` to 245,468 at `06:30Z`. It replaces the earlier attribution of that whole increase to a low-pop random dungeon event. Concurrent `BluespaceErrorRule` work was measured as well, so the evidence supports expedition-generation plus background-event load rather than assigning all approximately 66k added entities to one source.
- A distinct recurring bluespace event later created two `NFVGRoidScrap` grids at `06:45Z`, publicly named `Р РЋР ВµРЎР‚РЎвЂљРЎС“Р В»РЎРЏРЎР‚Р С‘РЎРЏ-87-K` and `Р СњР ВµРЎР‚Р ВµР С‘РЎРѓ-13-M`; those concrete event prototypes set `extendIfPopulated: false`. They are not the players' expedition.
- At `06:52:34Z`, round 151 still had three players, 232,324 entities, 433 active physics movers, zero active NPCs, and an active service. Recovery: none required. No ship, player, map, event, service, database, configuration, or round state was mutated, and no restart occurred. The finalized journal is mirrored byte-identically with `root:root` ownership and mode `0644` without restarting either service.

Commands and outcomes:

```powershell
ssh monolith-new "<installed journal SHA/mode and service/status check>"
ssh monolith-new "<bounded current entity/physics gauges and identifier-free 15-minute FTL/dungeon/main-loop aggregate>"
ssh monolith-new "<bounded public destination history and sanitized expedition-generation/arrival correlation>"
rg -n "NFVirologyLab|FTL|TryFTLProximity|SpawnSalvageMissionJob|BluespaceErrorRule|extendIfPopulated|minDuration|maxDuration" Content.Server Content.Shared Resources --glob '*.{cs,yml}'
```

Result: the players arrived normally at the `Р СљР С‘Р С”РЎРѓР С‘Р Р…Р В°-84-E` virology-lab salvage expedition. No FTL fault is logged, and the expedition's generation materially contributed to the measured server load.

Interrupted/partial check: the first mirror/status one-liner verified journal hash, mode, and active service, then failed only in its final JSON-formatting fragment due shell quoting. It made no mutation; a corrected base64 wrapper completed the status and event checks.

Next action: use the expedition-console timer as the deadline, complete or abandon the virology-lab mission normally, and return by FTL before it expires; no emergency host action is needed.

## 2026-07-22T06:36Z -- live lag diagnosed read-only

- Scope: investigate the live lag report with privacy-bounded service, host, Prometheus, SQLite, network, and journald aggregates. The game, gateway, configuration, database, ships, maps, events, players, and round were not mutated; no restart, admin command, authenticated maintenance request, profiler, dump, or deployment occurred.
- Mandatory journal pre-check at `2026-07-22T06:15:37Z` passed: repository and installed copies both had SHA256 `a3bb3cec381bfa839802772a68f3c51ea6403367644646b268c5c4a88d60c8c5`; the installed copy was `root:root` mode `0644`. Both services were active; the game had `NRestarts=0`, `ExecMainStatus=0`, and three players in round 151.
- Host health excludes general resource exhaustion: four vCPUs, load `1.62/2.19/2.29`, about 11.79 GB available RAM, negligible swap use, 13,353,578,496 bytes free on `/`, zero current memory/I/O pressure, no cgroup CPU throttling, and no OOM events. The gateway used about 1.3% CPU. SQLite `quick_check` was `ok`, no busy/lock markers were present, `db_executing_ops=0`, and database/WAL sizes were about 783.2 MB/4.3 MB.
- The game update path is CPU-bound. A 15-second Prometheus delta at `06:20Z` showed 89.8% of one core and 27.2 ms average frame time. After the next dungeon generation, the `06:30Z` delta showed 105.1% of one core and 35.2 ms average frame time. Eleven `MainLoop: Cannot keep up!` warnings occurred through `06:30:18Z`; with tick rate 20 and five queued ticks, each proves more than 250 ms backlog and the warning is limited to once per 15 seconds.
- The dominant current load is entity-system work: about 32.8 ms/tick versus 0.8 ms/tick in game-state processing. Representative post-surge costs were `PhysicsSystem` 9.9 ms/tick, `LuaMBehaviorSystem` 5.9, `DungeonSystem` 2.6, `LuaMStationaryTurretBehaviorAdapterSystem` 2.1, and `AtmosphereSystem` 1.8. The latter two LuaM systems are new broad base-prototype behavior in the deployed release and are a proved persistent contributor.
- Large procedural map generation is the dominant current surge. A player-selected `NFVirologyLab` salvage expedition began generating at `06:27:32Z` and entered FTL at `06:27:47Z`; entities rose from 181,922 at `06:20Z` to 245,468 at `06:30Z`, then stabilized near 247,900 in a 35-second trend. Active physics movers rose from 407 to 462-467. Concurrent `BluespaceErrorRule` work was also present, so the approximately 66k rise is attributed to expedition plus background-event generation, not solely to a low-pop random dungeon. A later `06:45Z` event created two temporary scrap grids whose prototypes disable population-based extension.
- Full-ship restore introduced a separate persistent PVS defect. Ten invalid `OreSilo`/`OreSiloClient` entity references were logged at `05:46:39Z`. PVS began at `05:47:36Z` to add one nonexistent target for two sessions and has continued at exactly 2,400 errors/minute (40/s), exceeding 100,000 events during the audit. Aggregate inspection of the relevant current-round restored payload found one `OreSilo`, three `OreSiloClient`, one `Store`, and two `HTN` component tokens; no payload, identity, owner, name, or entity ID was emitted or retained. Local source confirms `OreSiloSystem` adds the referenced silo to a session PVS override without checking that it exists.
- A transient 3,425-event `KeyNotFoundException` burst ran at about 20/s from `06:13Z` through `06:16:38Z`, rooted in `AltInteractOperator.Update` requesting absent NPC blackboard key `Owner`. This aligns with the first 1,800-second Apocalypse damaged-AI shuttle trigger after round start, and prior/source evidence shows those low-pop rules load persistent unknown-vessel grids. Because the selected live rule ID was not logged, treat this as a high-confidence secondary correlation rather than direct proof. The burst had stopped by final sampling.
- Current transport is healthy: a 15-second delta had zero Robust packet drops, zero kernel IP/UDP discard/error increments, zero UDP/1212 queue or socket drops, and 0.67 ms average loopback status latency. The PVS spam is still incorrect and wasteful, but current network loss, the database, docking persistence I/O, gateway, RAM, disk, and swap are not the active bottlenecks.
- Recovery: none required because the audit was read-only. The finalized repository journal, including the pre-existing local-only Unknown radio candidate entry and this production diagnosis, is installed byte-identically at `/opt/monolith-ds/AI_SERVER_JOURNAL.md` with `root:root` ownership and mode `0644`; this journal-only mirror operation does not restart either service.

Commands and outcomes:

```powershell
ssh monolith-new "<journal SHA/mode, service state, and aggregate /status pre-check>"
ssh monolith-new "<bounded systemd, process, cgroup, PSI, storage, network, SQLite, and sanitized journald aggregates>"
ssh monolith-new "<15-second and 35-second loopback Prometheus delta/trend scrapes for entity systems, frames, entities, physics, GC, and network>"
rg -n "AddPvsOverride|PvsOverrideSystem|LuaMBehaviorAgent|LuaMStationaryTurretBehaviorAdapter|BluespaceErrorRule|MonoAISTCShuttleSpawnerSchedulerApocalypse" Content.Server Content.Shared RobustToolbox Resources --glob '*.{cs,yml}'
```

Result: the lag is confirmed server-side. The main current cause is the roughly 66k-entity expedition/background-event surge feeding physics/entity systems, amplified by about 8 ms/tick of new broad LuaM behavior/turret work. A restored-ship OreSilo reference independently causes a permanent 40-error/s PVS loop; a timed damaged-AI/HTN exception burst was transient. No production behavior was changed.

Interrupted/partial checks: the first hash command had an `awk` quoting error after only a timestamp; the first broad script stopped at non-root `/proc/<pid>/io` access after useful partial output; one `curl | head` metrics probe ended with an expected broken pipe; two direct SSH/Python one-liners failed from quoting and made no mutation; an early template view was abandoned because redaction was insufficient. Corrected identifier-free base64/stdin wrappers completed the required checks.

Next action: make no live-round mutation without new authorization. Prepare and test a local three-part hotfix: constrain low-pop dungeon/damaged-AI spawning, budget/narrow LuaM behavior and turret adapters, and validate or clear OreSilo references before adding PVS overrides; deploy only in a separately authorized player-safe window.

## 2026-07-22T06:30Z -- Unknown radio marker and dialogue fix prepared locally; no host operation

- Scope: document a local release candidate for the player-visible Unknown radio defect. The supplied production screenshot showed the raw `__LUAM_AI_RADIO__<token>|` marker, attributed the line to `Р С–Р В°РЎР‚Р Р…Р С‘РЎвЂљРЎС“РЎР‚Р В° Р С—Р В°РЎРѓРЎРѓР В°Р В¶Р С‘РЎР‚Р В°`, and included mechanical `Р РЋР ВµР в„–РЎвЂЎР В°РЎРѓ Р С–Р В»Р В°Р Р†Р Р…Р С•Р Вµ:` coaching.
- Root cause: the game raises `RadioTransformMessageEvent` as a directed, non-broadcast event on the radio source, while the LuaM system subscribed as though it were broadcast/global. Its transform handler therefore did not consume the pending token, replace the speaker with Unknown, or strip the transport prefix before radio delivery.
- Local correction: the handler now subscribes through the source entity's `MetaDataComponent`, consumes valid token/actor metadata, and censors unknown or expired markers. The Unknown path stays silent after death, absence, disablement, or round end. Replies are shorter and stage-aware without quest-style prompts; ordinary conversation does not count as survival advice or a mistake; only concrete action-plus-object guidance advances the scenario. Advice parsing is fail-closed for questions, conditions, warnings, local and shared negation, hard sentence boundaries, and unrelated nearby objects while preserving polite/subordinate commands; recent complete replies are avoided when alternatives exist.
- Local evidence: `Content.Server` compilation passed. Final verification at `2026-07-22T07:47Z` passed 251/251 focused cases: 246 parsing cases and 5 radio cases. The radio test now exercises an actual `Р Р…Р ВµР С‘Р В·Р Р†Р ВµРЎРѓРЎвЂљР Р…РЎвЂ№Р в„–, РЎРѓРЎвЂљР В°РЎвЂљРЎС“РЎРѓ` request before verifying token cleanup, actor replacement, and marker removal. An earlier combined run used an isolated output directory and failed to mount `Resources`; that environment-only attempt is not counted as a product failure.
- Production state: this candidate is not deployed. No host file, service, configuration, database, data, round, or player state was changed, and no restart occurred. The deployment policy is frozen and four players were connected at the final observation. A production update requires new explicit authorization, fresh policy-bound artifacts, and a verified zero-player window.
- Recovery: none required because no host operation occurred.

Commands and outcomes:

```powershell
dotnet msbuild Content.Server\Content.Server.csproj /t:Compile /m:1 /v:minimal
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMAiDirectorParsingTest" -- NUnit.NumberOfTestWorkers=1
dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~LuaMRadioAiReceiveTest" -- NUnit.NumberOfTestWorkers=1
```

Result: the defect is corrected only in the local source candidate; the live server remains on the prior deployed build and behavior.

Next action: run the complete release gate and schedule a separately authorized zero-player deployment without forcing or interrupting the active round.

## Current state

- Latest player location: `Р СљР С‘Р С”РЎРѓР С‘Р Р…Р В°-84-E` is a normal `NFVirologyLab` salvage expedition. Generation began at `06:27:32Z`, expedition FTL at `06:27:47Z`, and arrival at `06:28:41Z`; no FTL fault is logged. Use its 45-60 minute console timer, not the unrelated 15-minute bluespace-event lifetime.
- Latest lag diagnosis: round 151 remained active with three players and no service restarts. The game is CPU-bound by a large procedural expedition plus concurrent background-event generation and broad LuaM AI/turret passes; restored-ship OreSilo references also produce one missing-target PVS error for two sessions at 40 events/s. Host memory/disk, SQLite, gateway, and current packet transport are healthy. No runtime mutation was performed.
- Latest production rollout: `luam-20260722-accumulated` completed at `2026-07-22T05:50Z`. The game advertises external client/version `9643610e6c726c773be4b31c7420866af55b3f8733250a49dc2a8bd9b209e3e0`; the public client is 333,944,765 bytes and returns HTTP 200. The matching server package SHA256 is `9f5c03f2a33ab64af53b3d8ce1e6237246253a681add68eee97982ad7a7eb586`.
- Live health: `monolith-ds.service` and `luam-ai-gateway.service` are active/running with `NRestarts=0`, `ExecMainStatus=0`, and zero warning-or-higher entries since their rollout starts. Round 151 is visible through `/status` and the public hub; two players were online at the final `2026-07-22T05:50Z` verification.
- Game access is open. The temporary `inet luam_deploy_guard` table that blocked non-loopback UDP/1212 only during the replacement window was deleted and proved absent; the game listens on two UDP/1212 sockets.
- Ship recovery proof: before stopping the old server, the player-led store transition was proved as `Stored`, revision 6, payload revision 2, source round 149, with zero Active/Restoring rows and zero leases. The 1,190,592-byte outer envelope and 892,546-byte inner snapshot both passed their recorded SHA256 and metadata checks. No ship/player identifier or payload is retained here.
- Recovery material: `/opt/monolith-ds/backups/data-luam-20260722-predeploy-ship-snapshot-20260722T053046Z.tar.gz` and `/opt/monolith-ds/backups/data-luam-20260722-accumulated.tar.gz` both pass `gzip -t`, are `root:root` mode `0600`, and preserve the proved snapshot. Server/config/gateway rollback material is under `/opt/monolith-ds/backups/server-luam-20260722-accumulated`, `/opt/monolith-ds/backups/server_config-before-luam-20260722-accumulated.toml`, and `/opt/monolith-ds/backups/ai-gateway-luam-20260722-accumulated-gateway`.
- The protected admin token is loaded in the game process without exposing its value. Unauthenticated `POST /admin/actions/maintenance/ship-save` now returns 401 instead of the old 404; later releases must use this authenticated barrier and must not reuse `-LegacyShipSaveBootstrap`.
- AI gateway SHA256 is `1871684c3078a6070049625651fd611d3733d62fba9946852b0c3d53183d3a31`; ship-generator SHA256 is `19db63f0d7a15c7ee43ba17558b013eb3d1215b138f2caa6964e7cae5642b756`. Health reports Ollama native with `huihui_ai/qwen3.5-abliterated:9b`, Piper and four Russian voices. A protected `/chat` smoke returned HTTP 200, a non-empty `action=none` response, provider HTTP 200, and `fallback=false` in 12,986 ms.
- Root storage is 42,174,005,248 bytes total with 13,391,106,048 bytes available (67% used). The release policy is frozen again; a later production mutation requires new explicit authorization and fresh policy-bound artifacts.

The older bullets immediately below are retained as superseded operational context; the rollout facts above are authoritative.

- Service: `monolith-ds.service`; the live build observed before cleanup on 2026-07-19 UTC was `3986a48dcf1c3e3b71dcda02ee82003d528f1fd33a688bd878455ab712316e62`.
- Storage before cleanup: root filesystem 40 GiB total, 32 GiB used, 5.3 GiB available (86%).
- Large areas before cleanup: `/opt/monolith-ds/backups` 6.8 GiB, `/var/www/monolith-client` 4.4 GiB, `/opt/monolith-ds/uploads` 1.8 GiB, `/opt/monolith-ds/deploy-staging` 757 MiB, system journal about 799 MiB, APT cache/lists about 429 MiB.
- Cleanup policy selected by the user: free production disk space while preserving the live server, live client build, current data, and recent recovery material.
- Latest forced deploy: `2026-07-21T00:44Z`, service active on client version `a611cba7804203b3c71d1c6d2e6c95280dd2ed6cd124e598a813a6065a350af6`; round 144 started successfully with no error-priority service entries in the deployment window.
- Latest database maintenance: at `2026-07-21T00:04Z`, the ship ownership registry and its presence leases were cleared at the user's request; all unrelated persistent player data was preserved and the service returned healthy in round 143.
- Latest AI gateway change: at `2026-07-21T22:14Z`, the loopback-only gateway switched to the user's local Ollama `huihui_ai/qwen3.5-abliterated:9b` through a restricted reverse SSH tunnel; the end-to-end `/chat` smoke returned `action=none` with `fallback=false` while the game remained active.
- Latest operator-tool read: at `2026-07-21T22:35Z`, the local sector-report generator was reduced to a 13-line, strictly read-only player-safe brief; production remained healthy in round 145 with three players.
- Latest local AI-character batch: at `2026-07-22T00:22Z`, the desktop panel and source tree contain the stranded-human `Р СњР ВµР С‘Р В·Р Р†Р ВµРЎРѓРЎвЂљР Р…РЎвЂ№Р в„–`, his stripped wreck, personal-AI personas, a radio-driven survival/death sequence, and bounded anonymized dialogue audit. This batch is validated locally but is not deployed to production.
- Latest damaged-AI shuttle audit: at `2026-07-21T23:44Z`, a read-only production check confirmed that the Apocalypse damaged-AI scheduler repeatedly creates persistent unknown vessels at low population because its `StationEvent` entries omit `minimumPlayers`; no live game state was changed.
- Scope correction prepared at `2026-07-22T01:40Z`: the user's current `Р вЂР ВµР В·РЎвЂ№Р СРЎРЏР Р…Р Р…РЎвЂ№Р в„–` report concerns docking/restoration of saved ships, not the Apocalypse random-shuttle scheduler. The release candidate fixes legacy runtime-UID identity by assigning a stable saved-ship GUID and rebinding deeds, grids, locks, and consoles after restore. The older Apocalypse audit remains historical evidence only; no Apocalypse scheduler/cleanup change is included in this release.
- Latest guarded-release preflight: at `2026-07-22T02:06Z`, production had zero players in round 149, both services active, the old ship-save endpoint returned 404, SQLite passed `quick_check` with zero active/restoring ship snapshots and zero leases, and about 14.76 GB was free. Because no admin API token existed, a protected root-only systemd environment override was staged without restarting the game; it will be loaded by the guarded deployment restart.
- Latest accumulated-release state: immutable client version `3087b48b0001df0e17d1f9f373a70739b9f0597ebb6eec31b9c0145a5792e9a1` is published but not advertised. A newer fully verified runtime receipt for Git HEAD `9d114e4bc97d627ac08c34ad27d91076d436bec2` is ready, but production remained on `a611cba7804203b3c71d1c6d2e6c95280dd2ed6cd124e598a813a6065a350af6` because one player and one active ship lease occupied the entire `2026-07-22T03:57Z` through `04:27Z` guarded wait. No server, service, configuration, database, data, or round mutation occurred.

## 2026-07-22T05:50Z -- accumulated client, server, and gateway rollout completed

- Scope: deploy the already verified accumulated interface, saved-ship/docking, persistence, gameplay, content, and AI batch after the user removed/stored the active shuttle. The operation retained only aggregate ship proof; no credential, raw environment, player identity, ship identity, payload, or private row was printed or recorded.
- The user's conditional `-Force` authorization required a complete ship snapshot first. The old-server database proved the player-led transition from Active revision 5/payload revision 1 to Stored revision 6/payload revision 2, source round 149, with a fresh `2026-07-22 05:24:53.5268666` store time. Outer payload size/hash, JSON envelope, inner YAML size/hash/UTF-8, format, revision, and entity count all matched. Active/Restoring and lease counts were both zero. Because a controlled zero-player window was then established, the actual deployment did not use `-Force`.
- Before the window, a local client UI automation attempt opened the debug console but did not execute `endround`; the database did not change. One malformed read-only SSH quoting attempt also exited with shell errors and no mutation. The player subsequently stored the shuttle normally, which produced the proved revision delta above.
- A guarded remote operation revalidated the snapshot, stopped the old game, revalidated it again, and created `/opt/monolith-ds/backups/data-luam-20260722-predeploy-ship-snapshot-20260722T053046Z.tar.gz`: 88,829,059 bytes, SHA256 `dae362018b86a0622b8d420b8bb80843c1c202151882cc2d40708678d32e13dd`, `root:root` mode `0600`. It installed only the temporary `inet luam_deploy_guard` rule dropping non-loopback UDP/1212, restarted the unchanged old server, and proved zero players, endpoint 404, loaded protected token, and unchanged snapshot before publication.
- Client publication succeeded with SHA256/version `9643610e6c726c773be4b31c7420866af55b3f8733250a49dc2a8bd9b209e3e0`, 333,944,765 bytes, nginx syntax success, and public HTTP 200. Receipt SHA256 was `7bea9669ddc27b4b1388ffa093eb6e7bc3e96340331d40c736ef3ffe25beab17`; source package SHA256 was `75f52f6d9fe0e408a60226e591e12a2b4614cffb146210f7a1224cbeff960e66`.
- The first server-wrapper invocation was interrupted by an erroneous one-second local execution limit before useful output; no deployment process remained. The identical command reran with a normal limit and passed. Legacy bootstrap proved old endpoint 404, zero players, zero Active/Restoring rows, and zero leases; the service stopped only after those checks. Server package SHA256 `9f5c03f2a33ab64af53b3d8ce1e6237246253a681add68eee97982ad7a7eb586` was installed, live config SHA256 stayed `3ad2adb6197a12b7154f2116c6fd4995130697ce7ad181ad99d2898054db0a2e`, and round 151 became ready with the exact client metadata.
- The deployment created `/opt/monolith-ds/backups/server-luam-20260722-accumulated` (304,087,359 bytes), `/opt/monolith-ds/backups/server_config-before-luam-20260722-accumulated.toml`, and `/opt/monolith-ds/backups/data-luam-20260722-accumulated.tar.gz` (88,829,455 bytes, SHA256 `6a6a75aa46fa833b3079c1705dcdf5520a16c73d27a855ba6dcb407843ffa7b3`). The latter passed `gzip -t`; its initial root-owned mode `0644` was immediately restricted to `0600`. The local deploy wrapper and release-contract test now require root ownership and mode `0600` for every future required data backup.
- Gateway deployment created `/opt/monolith-ds/backups/ai-gateway-luam-20260722-accumulated-gateway` (238,053 bytes), then installed gateway SHA256 `1871684c3078a6070049625651fd611d3733d62fba9946852b0c3d53183d3a31` and generator SHA256 `19db63f0d7a15c7ee43ba17558b013eb3d1215b138f2caa6964e7cae5642b756`. One early health retry printed a connection refusal while the service was starting; the guarded wrapper subsequently observed health and exited 0.
- Guarded postchecks before reopening proved both services active/running with zero restarts and successful main status; exact deployed server DLL/build hashes; exact gateway/generator hashes; `/status` and `/info` on the new version; public client HTTP 200 and exact length; gateway JSON contract; protected token loaded; unauthenticated maintenance HTTP 401; SQLite `quick_check=ok`; zero active/restoring rows and leases; and the unchanged Stored rev-6/payload-rev-2 snapshot. The outer SHA256 is `3e0529f46f1e7b4160b799b62c30b6fea281775c1b9165e46e9baf745f112786`; the inner SHA256 is `b28ac0cbf379f050cdeb11b48a0e1a2563e29c8f74a7ecfda2779aeca905a566`.
- The first combined postcheck stopped after the valid database proof because it incorrectly expected the deploy script to retain its staging ZIP; normal cleanup had removed it. A corrected audit compared installed files to key ZIP entries. Another corrected retry fixed locally assumed gateway file sizes. These were read-only assertion failures, not runtime failures.
- The exact temporary nft table was inspected as one table, one chain, one UDP/1212 drop rule, and one matching comment, then deleted. Its deletion succeeded, but the first follow-up command exited 1 because it checked peer column `$5` instead of local column `$4` in `ss`; the corrected check proved the table absent and two UDP/1212 listeners. Players connected immediately afterward. Public verification then passed with round 151 visible in the hub, exact external client metadata, and two players online.
- A protected synthetic gateway `/chat` request returned HTTP 200, a non-empty reply, and `action=none`. Its first audit assertion expected a separate file although this host records audit metadata in journald; an initial pipe/heredoc reader also consumed the wrong stdin. The corrected bounded journald parser proved provider HTTP 200, `fallback=false`, Ollama native, and no provider error. No game action or player message was emitted.
- The release policy was returned to `remoteDeployFrozen=true`, authorization metadata was cleared/frozen, and the batch returned to `local-package-only`. The release contract passed with `ok=true`. No additional ServerNews entry was added because this was publication of behavior already documented in the release candidate, not a new player-facing code change.
- The updated repository journal was installed byte-identically at `/opt/monolith-ds/AI_SERVER_JOURNAL.md` with owner/mode `root:root`/`0644`. This journal-only mirror operation did not restart either service; both remained active with two players online at verification.
- Recovery: for a binary rollback, first schedule an authorized player-safe window, stop the game, restore the exact server directory and config backup named above, normalize ownership/executable mode, start the service, and verify the prior advertised client. Restore either data archive only if a proven database/data rollback is required and only while the game is stopped. Gateway recovery uses its exact backup directory and a gateway-only restart. No rollback is currently needed; do not recreate the temporary nft guard during normal operation.

Commands and outcomes:

```powershell
& .\Tools\provision_monolith_client_static.ps1 -ClientPackagePath (Resolve-Path 'release/SS14.Client.zip') -ExpectedSha256 9643610e6c726c773be4b31c7420866af55b3f8733250a49dc2a8bd9b209e3e0 -Version 9643610e6c726c773be4b31c7420866af55b3f8733250a49dc2a8bd9b209e3e0 -ReleaseReceiptPath (Resolve-Path 'release/luam-binary-release-receipt.json') -ExpectedReleaseReceiptSha256 7bea9669ddc27b4b1388ffa093eb6e7bc3e96340331d40c736ef3ffe25beab17
& .\Tools\deploy_luam_server_release.ps1 -PackagePath (Resolve-Path 'release/SS14.Server_linux-x64.zip') -ExpectedSha256 9f5c03f2a33ab64af53b3d8ce1e6237246253a681add68eee97982ad7a7eb586 -ReleaseReceiptPath (Resolve-Path 'release/luam-binary-release-receipt.json') -ExpectedReleaseReceiptSha256 7bea9669ddc27b4b1388ffa093eb6e7bc3e96340331d40c736ef3ffe25beab17 -Tag luam-20260722-accumulated -RemoteConfigPath /opt/monolith-ds/server/server_config.toml -RemoteDataDir /opt/monolith-ds/data -RequireDataBackup -LegacyShipSaveBootstrap
& .\Tools\deploy_luam_ai_gateway.ps1 -ExpectedSha256 1871684c3078a6070049625651fd611d3733d62fba9946852b0c3d53183d3a31 -Tag luam-20260722-accumulated-gateway
ssh monolith-new '<bounded pre/post-stop snapshot proof, root-only recovery archive, temporary nft guard, and old-server restart>'
ssh monolith-new '<bounded service/status/info/gateway/token/endpoint/SQLite/snapshot/hash/backup/log/storage postchecks>'
ssh monolith-new '<inspect exact temporary nft table; delete only table inet luam_deploy_guard; verify table absent and UDP listener>'
ssh monolith-new '<root-only synthetic /chat and sanitized journald audit proof>'
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\monolith-restart-when-empty.ps1 -VerifyOnly -SkipSsh
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\test_luam_release_contract.ps1 -Json
```

Result: the accumulated client, server, and gateway are live; the saved ship has two independent root-only recovery archives; configuration and snapshot integrity are unchanged; the server is open and visible in the hub; both services and the AI provider path are healthy; and the remote deployment policy is frozen again.

Next action: after the first normal restored-ship docking session, run the public/hub verification again and, only if the player reports a persistence issue, perform a privacy-bounded aggregate ship-lifecycle audit before considering any mutation:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\monolith-restart-when-empty.ps1 -VerifyOnly -SkipSsh
```

## 2026-07-22T04:28Z -- current receipt ready; deployment waiting for player and ship lease

- Scope: retry the accumulated release after repairing the SSH transport, while preserving the live config and requiring a safe first-rollout ship state. No player identity, ship payload, raw database row, or credential was read or recorded.
- A new clean-HEAD gate again passed production tests, local smoke, source package construction and verification, binary construction, a zero-violation client/server surface audit, and client-static dry-run. Current evidence: source `luam-local-release-20260722-033810.zip` SHA256 `75f52f6d9fe0e408a60226e591e12a2b4614cffb146210f7a1224cbeff960e66`; payload digest `def41ba83584887b56b6d504927f22071263c1bdbb887544a1f4c77e2a80da1e`; worktree digest `c3e5f0a9f6902e68b01adb7179a615465e449d47503f675b09f6c258ac569018`; Git HEAD `9d114e4bc97d627ac08c34ad27d91076d436bec2`; client/version `9643610e6c726c773be4b31c7420866af55b3f8733250a49dc2a8bd9b209e3e0` with 333,944,765 bytes; server SHA256 `9f5c03f2a33ab64af53b3d8ce1e6237246253a681add68eee97982ad7a7eb586`; receipt SHA256 `7bea9669ddc27b4b1388ffa093eb6e7bc3e96340331d40c736ef3ffe25beab17`.
- The outer gate's final server dry-run failed because Windows PowerShell removed an empty native argument and left `-ConfigSourcePath` without a value. The exact server deploy script was then invoked directly with the same receipt and its dry-run passed: the live config remains untouched, the data backup is mandatory, the surface audit and worktree receipt match, and legacy bootstrap still requires zero players, zero Active/Restoring snapshots, zero leases, and endpoint HTTP 404.
- Final production preflight at `2026-07-22T03:56:33Z` refused deployment: both services were healthy and the endpoint/database/token/journal/storage checks were otherwise valid, but one player was connected and the database contained one Active/Restoring snapshot plus one presence lease. The newer `964361...` client was therefore not published, and neither the server nor gateway was changed.
- A read-only monitor polled only public player count and, if it ever reached zero, aggregate ship/lease counts every 20 seconds for 30 minutes. Every minute from `2026-07-22T03:57:13Z` through `04:26:31Z` still reported one player; the command intentionally exited 2 at its deadline. No kick, forced restart, authenticated maintenance call, ship mutation, or service mutation was attempted.
- The local orchestrator now omits `-ConfigSourcePath` entirely when the requested source is empty, relying on the deploy script's safe empty default; non-empty sources are still appended explicitly. The release contract, parser, and an AST-level regression proved both empty omission and non-empty forwarding. This ops-only follow-up does not change the already verified runtime binaries above.
- The required updated journal mirror was installed byte-identically as `root:root` mode `0644`; both services remained active. This installation changed only the handoff journal.
- Recovery: none required. The old server/client pair remains advertised and healthy. The previously published unadvertised `3087b48...` immutable client remains harmless. Before retry, require a fresh zero-player/status/endpoint/SQLite check; do not kick the player, do not use `-Force`, and do not authenticate the new ship-save endpoint until after the one-time legacy deployment succeeds.

Commands and outcomes:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/ship_luam_release.ps1 -Tag luam-20260722-accumulated -LegacyShipSaveBootstrap -ConfigSourcePath ([string]::Empty) -RemoteConfigPath /opt/monolith-ds/server/server_config.toml -RemoteDataDir /opt/monolith-ds/data -SkipLocalFast -Json
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/deploy_luam_server_release.ps1 <current receipt-bound arguments> -ConfigSourcePath ([string]::Empty) -RequireDataBackup -LegacyShipSaveBootstrap -DryRun
ssh monolith-new "<bounded current journal/service/status/endpoint/SQLite/token-metadata/storage preflight>"
Invoke-RestMethod http://188.127.225.57:1212/status # aggregate 20-second monitor; timed out after 30 minutes with one player
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/test_luam_release_contract.ps1 -Json
scp Tools/AI_SERVER_JOURNAL.md monolith-new:/tmp/AI_SERVER_JOURNAL.20260722T0428Z.md
ssh monolith-new "<install journal root:root 0644 and verify hash/services/status>"
```

Result: the current source/client/server artifacts and direct guarded dry-run are verified. Deployment is safely waiting on external state: one connected player and the associated active ship/lease. Production runtime was not changed.

Next action: once public status reports zero players, re-read both journals, prove zero Active/Restoring snapshots and leases, then publish client `964361...`, deploy server `9f5c03...` with the current receipt and one-time legacy bootstrap, and deploy gateway `187168...` without `-Force`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/provision_monolith_client_static.ps1 -ClientPackagePath release/SS14.Client.zip -ExpectedSha256 9643610e6c726c773be4b31c7420866af55b3f8733250a49dc2a8bd9b209e3e0 -Version 9643610e6c726c773be4b31c7420866af55b3f8733250a49dc2a8bd9b209e3e0 -ReleaseReceiptPath release/luam-binary-release-receipt.json -ExpectedReleaseReceiptSha256 7bea9669ddc27b4b1388ffa093eb6e7bc3e96340331d40c736ef3ffe25beab17
```

## 2026-07-22T03:35Z -- accumulated client published; server deploy failed closed before SSH

- Scope: complete the explicitly authorized accumulated interface, saved-ship/docking, gameplay, persistence, and AI release while preserving the live config and enforcing the one-time ship-save bootstrap. No player identity, private database row, raw environment, or credential was read or recorded.
- The current-HEAD source gate passed production tests and local smoke, produced `luam-local-release-20260722-031025.zip` SHA256 `6b285a6297e61783fb755a1dbc04cf11789e222bc510bf6bacbcaffe6cd3e313`, payload digest `23f3ec3c15a46ec206d31a4febe8072f945806f92af0c1a6442e8b2fb54d2646`, and bound it to Git HEAD `96dd9162d374c26a5afe60726b3fe0c116bd95bd`. Source verification, fresh client/server construction, the binary surface audit with zero violations, and client-static dry-run all passed. The outer orchestration then failed before its server dry-run because a mandatory `[string[]]` wrapper rejected the intentionally empty config-source value. A direct invocation of the exact server dry-run exited 0 and confirmed receipt, audit, live-config preservation, required data backup, and all three legacy bootstrap conditions.
- Binary evidence from that clean HEAD: client/version SHA256 `3087b48b0001df0e17d1f9f373a70739b9f0597ebb6eec31b9c0145a5792e9a1`; server SHA256 `8f4777954feea784b49c3756a06fc4f386e45215ce1c5cc53e15a55e1ff951be`; receipt SHA256 `bf01ccdb49bc7f340319d56367393b1f0c1bb4b52c8d25d96cf3ff7f949614f9`. Those values are historical evidence only after the transport repair changes HEAD; a new receipt is required before the server retry.
- Immediate production preflight at `2026-07-22T03:30:13Z` passed: both services were active with `NRestarts=0` and `ExecMainStatus=0`; round 149 had zero players; the old maintenance endpoint returned 404; `preferences.db` passed `quick_check`; the ship snapshot and lease tables existed with zero Active/Restoring rows and zero leases; the protected token environment/drop-in retained `root:root` modes `0600`/`0644`; the installed journal matched; and 14,807,007,232 bytes were available.
- The immutable client was installed at `/var/www/monolith-client/3087b48b0001df0e17d1f9f373a70739b9f0597ebb6eec31b9c0145a5792e9a1/SS14.Client.zip`. Its SHA256 and 333,944,736-byte size matched locally and remotely, nginx validation passed, and the public URL returned HTTP 200.
- The subsequent guarded server command confirmed zero players, then failed on the local Windows host before starting `ssh.exe`: its base64-encoded remote preflight exceeded the process command-line length. Consequently it did not create server/config/data backups, upload the server archive, invoke the legacy ship-save bootstrap, stop or restart the service, load the staged token, change data, or advertise the new client. Follow-up verification found both services active, round 149 still at zero players, the old build still advertised, the new immutable client reachable, and 14,427,881,472 bytes available.
- Local release-tool repair now streams the encoded Bash program over SSH stdin instead of embedding it in the Windows command line, permits PowerShell's transport CRLF during remote base64 decoding, and lets the orchestrator forward an intentionally empty config-source argument. Parser checks and the policy release contract passed. A harmless 121,200-byte remote stdin probe returned `remote-stdin-ok`. The first probe reached the host but failed decoding because GNU base64 rejected PowerShell's CR; after enabling transport garbage tolerance, the next probe transported successfully but its local assertion expected a newline rather than the intentionally printed literal `\\n`; the corrected `echo` probe passed. None of these probes mutated production state.
- The required updated journal mirror was installed byte-identically as `root:root` mode `0644`. Post-install verification found both services active and round 149 still at zero players.
- Recovery: no server rollback is needed because the server deployment never reached SSH. The unadvertised immutable client can safely remain for the upcoming retry; if the release is abandoned, only its exact version directory above is eligible for deliberate removal after re-verifying that `/info` does not advertise it. The old advertised server/client pair remains intact.

Commands and outcomes:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/ship_luam_release.ps1 -Tag luam-20260722-accumulated -LegacyShipSaveBootstrap -ConfigSourcePath ([string]::Empty) -RemoteConfigPath /opt/monolith-ds/server/server_config.toml -RemoteDataDir /opt/monolith-ds/data -SkipLocalFast -Json
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/deploy_luam_server_release.ps1 <receipt-bound arguments> -ConfigSourcePath ([string]::Empty) -RequireDataBackup -LegacyShipSaveBootstrap -DryRun
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/deploy_luam_ai_gateway.ps1 -ExpectedSha256 1871684c3078a6070049625651fd611d3733d62fba9946852b0c3d53183d3a31 -Tag luam-20260722-accumulated-gateway -DryRun
ssh monolith-new "<bounded journal/service/status/info/endpoint/SQLite/token-metadata/storage preflight>"
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/provision_monolith_client_static.ps1 <receipt-bound arguments>
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/deploy_luam_server_release.ps1 <receipt-bound arguments> -ConfigSourcePath ([string]::Empty) -RequireDataBackup -LegacyShipSaveBootstrap
ssh monolith-new "<bounded post-failure service/status/info/client/storage verification>"
Invoke-RemoteBash <121200-byte harmless stdin transport probe>
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/test_luam_release_contract.ps1 -Json
scp Tools/AI_SERVER_JOURNAL.md monolith-new:/tmp/AI_SERVER_JOURNAL.20260722T0335Z.md
ssh monolith-new "<install journal root:root 0644 and verify hash/services/status>"
```

Result: the source, binary, audit, direct dry-run, gateway dry-run, and immutable-client stages passed. Both orchestration defects failed closed before a server mutation. The release transport repair is locally green, but the server and gateway are not yet updated and no production completion is claimed.

Next action: install this journal mirror, commit the transport/orchestration repair with both journals, produce a new clean-HEAD receipt, then repeat the zero-player guarded server deployment without `-Force`:

```powershell
git add -- Tools/deploy_luam_server_release.ps1 Tools/ship_luam_release.ps1 Tools/test_luam_release_contract.ps1 Tools/AI_SERVER_JOURNAL.md .agents/ITERATION_LOG.md
git commit -m "fix(release): stream guarded deploy scripts over ssh"
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/ship_luam_release.ps1 -Tag luam-20260722-accumulated -LegacyShipSaveBootstrap -ConfigSourcePath ([string]::Empty) -RemoteConfigPath /opt/monolith-ds/server/server_config.toml -RemoteDataDir /opt/monolith-ds/data -SkipLocalFast -Json
```

## 2026-07-22T02:06Z -- release preflight passed and protected admin token staged

- Scope: bounded production readiness checks plus the minimum prerequisite for the authenticated ship-save barrier in all deployments after the one-time legacy bootstrap. No raw configuration, environment, token value, player identity, ship payload, or private database row was read or emitted.
- The installed journal had no host-only headings and was an older subset of the repository journal; the only repository-only heading was the saved-ship docking scope correction. The installed file remained `root:root` mode `0644`.
- Preflight health: `monolith-ds.service` and `luam-ai-gateway.service` were active with `NRestarts=0` and `ExecMainStatus=0`; round 149 was running at run level 1 with zero players. The live build was `a611cba7804203b3c71d1c6d2e6c95280dd2ed6cd124e598a813a6065a350af6`. Root storage was 42,174,005,248 bytes total with 14,762,942,464 bytes available (64% used).
- First-rollout ship proof passed: unauthenticated `POST /admin/actions/maintenance/ship-save` returned the required old-binary 404; `preferences.db` passed read-only `pragma quick_check`; both persistence tables were present and contained zero `Active`/`Restoring` snapshots and zero presence leases.
- The old process and live TOML had no usable `admin.api_token`. A new random token was generated entirely inside a root shell and written to `/etc/monolith-ds/game-admin-api-token.env` as `root:root` mode `0600`; its value was never printed or copied into the repository. The non-secret drop-in `/etc/systemd/system/monolith-ds.service.d/30-admin-api-token.conf` was installed as `root:root` mode `0644` and points the service at that environment file. `systemctl daemon-reload` completed without restarting the game.
- Post-operation verification proved the service PID and round ID were unchanged, the game remained active in round 149 with zero players, and the override file format/ownership/mode were valid. The token is intentionally reported only as `pending_restart=true`; the normal guarded deployment restart will load it.
- The first journal-install wrapper failed during local PowerShell parsing of a nested Python expression and did not reach `scp` or SSH. A base64-encoded retry then installed the journal byte-identically with SHA256 `24ba07147dcfc3265d37949fb7d13b63c4c35ba2f25998f7de2511cbf642e6b3`, owner/mode `root:root`/`0644`; both services remained active with zero players in round 149.
- Recovery before the next restart: remove exactly the environment file and `30-admin-api-token.conf`, then run `systemctl daemon-reload`. Recovery after a restart additionally requires restarting `monolith-ds.service`. No rollback was needed.

Commands and outcomes:

```powershell
scp monolith-new:/opt/monolith-ds/AI_SERVER_JOURNAL.md C:\MonolithTemp\AI_SERVER_JOURNAL.host-preflight.md
ssh monolith-new "<base64-encoded bounded service/status/info, endpoint-status, token-presence, read-only SQLite quick_check/counts, and storage script>"
ssh monolith-new "<base64-encoded root-only token environment/drop-in installation, daemon-reload, and PID/round invariance verification>"
scp Tools/AI_SERVER_JOURNAL.md monolith-new:/tmp/AI_SERVER_JOURNAL.20260722T0206Z.md
ssh monolith-new "<base64-encoded journal install and hash/service/status verification>"
```

Result: the preflight, protected-token prerequisite, and corrected journal installation completed successfully without a service restart or player/ship mutation. The malformed local-only journal wrapper is not counted as a host operation.

Next action: install this updated journal mirror, commit it with the iteration journal, require a clean worktree, then build the policy-bound release artifacts before the zero-player state changes:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/ship_luam_release.ps1 -Tag luam-20260722-accumulated -LegacyShipSaveBootstrap -ConfigSourcePath ([string]::Empty) -RemoteConfigPath /opt/monolith-ds/server/server_config.toml -RemoteDataDir /opt/monolith-ds/data -Json
```

## 2026-07-22T01:40Z -- saved-ship docking scope correction; no host operation

- The user clarified that the current incident is a docking problem involving restored ships. The earlier bounded production log observation that two `Unidentified Vessel` grids reparented to `Р’В«Р С™Р С•Р В»Р С•РЎРѓРЎРѓ Р В¦Р ВµР Р…РЎвЂљРЎР‚Р В°Р В»Р В»Р’В»` did not contain an explicit docking failure and does not establish the current root cause.
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
- Over the bounded eight-hour service journal, six `Р Р…Р ВµР С‘Р В·Р Р†Р ВµРЎРѓРЎвЂљР Р…РЎвЂ№Р в„– РЎв‚¬Р В°РЎвЂљРЎвЂљР В» Р ВР В` rules were added, started, and ended. Four belonged to the previous long-running Apocalypse process and occurred at exact 30-minute intervals (`Nebula`, `Zenith`, `ZenithE`, `Zenith`). Two `Unidentified Vessel` grids later changed parent from their private map entities to `Р’В«Р С™Р С•Р В»Р С•РЎРѓРЎРѓ Р В¦Р ВµР Р…РЎвЂљРЎР‚Р В°Р В»Р В»Р’В»`; no shuttle-impact log involving an `Unidentified Vessel` was present.
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

- Scope was local implementation and verification only. `Р СњР ВµР С‘Р В·Р Р†Р ВµРЎРѓРЎвЂљР Р…РЎвЂ№Р в„–` now wakes on a sealed but immobile stripped wreck with no PDA, ID, navigation console, coordinates, thrusters, or gyroscope. Available survival equipment is bounded to the ore processor, hand drill, three full-size oxygen canisters, hydroponics/water/food, generator, ordinary radio, and structural life support.
- Player radio advice addressed to `Р СњР ВµР С‘Р В·Р Р†Р ВµРЎРѓРЎвЂљР Р…РЎвЂ№Р в„–` advances a deterministic six-stage survival sequence. Correct ordered advice stabilizes the wreck and reaches `Survived`; dangerous advice, three accumulated errors, or extended delay reaches `Dead` and changes the physical mob state. Panic replies use original short/disfluent wording and do not quote real victims or reveal numeric mechanics. Each status, misunderstanding, mistake, panic, successful stage, and survived state has multiple variants; the immediately previous exact reply is excluded from the next random selection while facts and consequences remain unchanged.
- The desktop panel persona was updated to the same novice/no-coordinate lore, first-person-only radio speech, gradual panic, no third-person stage directions, and explicit non-repetition with factual continuity. The exact panel process was reloaded after the final prompt and dialogue-journal update as PID 32808. No bridge action or player-visible production message was sent during any smoke test.
- The local release candidate adds anonymized dialogue audit at `data/luam/unknown_dialogue.jsonl`: UTC time, round ID, stage before/after, outcome, advice, and reply, with no separate sender-name or sender-ID field. Known active player names/IDs and sensitive technical strings are redacted from bounded text. At 5 MiB the current file replaces the single prior `.1` segment, keeping retention near 10 MiB.
- The desktop panel stores each local operator/Unknown exchange as one paired JSONL object under `AI-Agent-Workspace/sector-reports/unknown-dialogue.jsonl`, using a random per-launch conversation ID and no Windows account or player identity. `Р вЂ“Р Р€Р В Р СњР С’Р вЂє РІвЂ вЂ™ Р вЂќР ВР С’Р вЂєР С›Р вЂњР В` renders the last 200 local/server records and outcome counts; `Р вЂ“Р Р€Р В Р СњР С’Р вЂє РІвЂ вЂ™ Р РЋР ВР РЋР СћР вЂўР СљР С’` remains separate. Nearby personal-AI speech is not persisted by this audit.
- Validation passed: server compile; `LuaMAiDirectorParsingTest` 119/119 from a separate temporary output; prototype/YAML checks; panel no-coordinate and panic smokes; panel JSONL write/redaction/viewer runtime self-test; full `Tools/validate_luam_feature_pack.py`; and bounded `git diff --check` with only line-ending notices. A normal local build copy was blocked solely by the already-running isolated local `Content.Server` PID 27316; it was not stopped.
- No production binary, resource, config, database, service, process, round, player state, or AI-provider setting was changed. The production release policy remains frozen, and the mixed accumulated tree still requires explicit guarded-release authorization before deployment. Rollback is not applicable to production; the local panel can be closed through its exact process or relaunched from `Desktop/HuiHui - Р СџР В°Р Р…Р ВµР В»РЎРЉ РЎС“Р С—РЎР‚Р В°Р Р†Р В»Р ВµР Р…Р С‘РЎРЏ.cmd`.
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

- The user explicitly requested the production identity change. A bounded config-only operation changed `hostname`, `lobby_name`, and the title at the start of `game.desc` from the prior temporary recruitment name to `СЂСџРЉСџ Р СљРЎвЂРЎР‚РЎвЂљР Р†РЎвЂ№Р в„– Р С”Р С•РЎРѓР СР С•РЎРѓ: Р С™Р С•РЎРѓР СР С‘РЎвЂЎР ВµРЎРѓР С”Р В°РЎРЏ Р С™Р В°РЎРѓРЎРѓР С‘Р С•Р С—Р ВµРЎРЏ СЂСџС™Р‚ СЂСџРЉСџ`; no binary, client package, database, persistent player data, AI provider, or gameplay setting was changed.
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
- Existing code already sends Aibolit-addressed player radio requests through the gateway in strict response-only mode (`action=none`). General player `Р ВР В` chat remains deterministic and would need a bounded response-only gateway path, per-player cooldown/history limits, one-flight queueing, and deterministic fallback before broad activation.
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

## 2026-07-19 UTC РІР‚вЂќ pre-cleanup audit

- Read-only inspection covered filesystem usage, top-level `/opt`, `/var`, Monolith directories, backup timestamps, published client versions, package caches, and journal size.
- Confirmed the live immutable client build before cleanup.
- Planned bounded cleanup: remove disposable deployment staging and uploads; remove backup entries older than 2026-07-16 while retaining 2026-07-16 and newer recovery sets; remove published client directories except the active build; clean APT caches; vacuum system journal to 200 MiB.
- No database, current server directory, current data directory, active client build, server config, service unit, or gateway environment is in deletion scope.

Next action: execute the bounded cleanup, verify service health and live client HTTP availability, record reclaimed space here, then install this journal mirror on the host.

## 2026-07-19 UTC РІР‚вЂќ bounded disk cleanup completed

- Removed all contents beneath `/opt/monolith-ds/deploy-staging` and `/opt/monolith-ds/uploads` after resolving and verifying both exact absolute directories.
- Removed top-level backup entries older than 2026-07-16. Recovery sets dated 2026-07-16 and 2026-07-18 remain; backup usage is now about 2.2 GiB.
- Removed obsolete immutable client directories while preserving the active build `3986a48dcf1c3e3b71dcda02ee82003d528f1fd33a688bd878455ab712316e62`; published-client usage is now about 319 MiB.
- Cleaned APT caches/lists and vacuumed the system journal from about 799 MiB to about 150 MiB.
- Reclaimed 13,101,957,120 bytes. Root filesystem changed from 86% used (5.3 GiB available) to 54% used (18 GiB available).
- The combined cleanup script performed the intended mutations but exited 1 during its last `/status` check because a PowerShell here-string introduced BOM/CRLF into the remote Bash stream. This verification failure did not interrupt or roll back cleanup. A separate clean read-only check then passed: service active, `/status` healthy with round 137 and two connected players, active client archive present, and root filesystem at 54%.
- The live server, database/data directory, configuration, gateway environment, active client, and retained recent backups were not removed or restarted.

Next action: configure a scheduled retention job that preserves the active client plus one rollback client and keeps a documented number of recent data/server backups; verify with a dry-run inventory before enabling it.

## 2026-07-19 UTC РІР‚вЂќ security pressure monitor installed

- Installed `/usr/local/sbin/monitor-monolith-security` with a hardened oneshot service and an enabled timer running approximately once per minute.
- Monitor records SSH authentication failures over two minutes, current fail2ban bans, SYN-RECV pressure on SSH/web/game ports, established game TCP connections, root-disk usage, and `monolith-ds.service` state.
- Threshold breaches are written to journald with tag `monolith-security`; the current machine-readable snapshot is `/var/lib/monolith-security-monitor/status.env`.
- First service start failed before executing the monitor because `ReadWritePaths` referenced a state directory that did not yet exist. Added systemd `StateDirectory`; second start failed because the fully dropped capability set correctly prevented an unnecessary `chown` inside the script. Removed that ownership mutation. Both failed starts are retained in the service journal and are not counted green.
- Final start passed. Timer is `active` and `enabled`; snapshot at `2026-07-19T23:19:35Z` reported `status=ok`, 0 SSH failures in two minutes, 14 currently banned addresses, 0 public SYN-RECV sockets, 1 established game TCP connection, disk 54%, and game service active.
- No external notification transport is configured. Alerts are currently available through `journalctl -t monolith-security` and the protected status file. Discord delivery requires a webhook for a private channel stored only in a root-owned environment file.

Next action: obtain the private Discord webhook or another notification destination, add rate-limited delivery with recovery notifications, and test it with a synthetic non-attack warning.

## 2026-07-24T15:30Z -- repository reconciliation before requested player playtime grant

- The installed server journal was newer than the repository copy during the pre-mutation check. Its recorded 2026-07-23 facts were reconciled here: forced persistent-ship hotfix deployment completed, global persistent-ship cleanup removed only ship snapshots/leases, and a later normal deployment preflight/attempt made no additional server mutation.
- Current read-only preflight: `monolith-ds.service` active, round 158 in progress with four players, root filesystem 82% used with about 6.8 GiB free.
- Pending user-authorized scope: one-time playtime grant for accounts already existing at the time of the operation only; new accounts are not changed. No role bans or permissions will be altered. Exact database backup, transaction, aggregate validation, and service health checks are required before completion.
- Next action: inspect the production play_time schema and existing-account count read-only, then stop the service, back up preferences.db, and atomically set all current accounts to at least 24 hours in every defined playtime tracker.

## 2026-07-24T17:17Z -- one-time existing-account playtime grant

- Read-only audit found 101 existing accounts, a healthy `play_time` schema, no duplicate account/tracker rows, and `PRAGMA quick_check` = `ok`. The defined tracker set contains 52 trackers, including `Overall`.
- The service was stopped briefly to prevent database writes, then `preferences.db` was copied before mutation to `/opt/monolith-ds/backups/preferences-before-playtime-grant-20260724T171745Z.db` (907,182,080 bytes, SHA256 `25d89f2e71c139273a9ce289271e5f8acd93ef77ffba39f871259eba78fa4110`, `root:root`, mode `0600`).
- In one SQLite transaction, only accounts present in `player` at execution time received missing play-time rows for every defined tracker, at `1.00:00:00` (24 hours). Result: 5,252 rows inserted, 0 lower existing rows raised; validation confirmed 101 x 52 = 5,252 target rows, none below target, no duplicates, and `quick_check` = `ok`. No bans, permissions, profiles, currency, or new accounts were changed.
- The service restart ended the active round: preflight was round 158 with four players; post-operation `/status` is healthy on round 160, lobby state, zero players. Root filesystem is 87% used with about 5.1 GiB free.
- No persistent-ship snapshot, lease, cleanup, or ship database operation was performed in this playtime task. Rollback for the playtime change: stop the service, restore the backup database above, then start the service.

Next action: before any unrelated production change, notify players if a service restart is required and verify the installed journal matches this repository copy.

## 2026-07-24T18:10Z -- read-only holodilnik66 persistence-log audit

- User explicitly requested inspection of logs for `holodilnik66` concerning shuttle and item persistence. Read-only `journalctl` queries were performed; no service, database, snapshot, cleanup, or configuration mutation occurred.
- At 2026-07-24 20:20-20:35 MSK, the player registered `HT Sagittarius EXP-405` and reported that a filled locker persisted. The same player reported not seeing another player's shuttle; this is ownership/visibility context, not proof of a failed save.
- Confirmed persistence-risk errors around saves: serializer references to missing/deleted entities, invalid `EntityUid` deserialization (including `ShipRepairData`), and container operations against terminating entities. A repeated `KillTracker` YAML serialization exception also affected save attempts.
- `luam.shipgen` HTTP 400 warnings explicitly state that saved-ship analysis failed while persistence remained unaffected; they are not evidence of a failed shuttle save.
- Next action: reproduce the serializer/container cases locally and harden snapshot capture to reject or sanitize invalid references before a ship snapshot is committed. Any production repair, reload, snapshot mutation, or restart requires explicit user approval.

## 2026-07-24T18:28Z -- forced production rollout authorized; preflight

- User explicitly authorized a forced production update despite players online and the outstanding deep-cryo release blocker. Intended scope is the current repository release, including persistent-shuttle restoration fixes and the configured lobby music; this will restart `monolith-ds.service`.
- Read-only preflight: service is active; `/status` reported round 160 running with 6 players; root filesystem is 87% used with approximately 5.0 GiB available.
- Risk: this repository has known unfinished deep-cryo callback/replay coverage and a full ship-persistence test class that exceeded the local harness timeout. The user accepted the forced rollout risk.
- Planned safeguards: create and verify a source package and binary receipt, publish the matching client, take the deployment script's required data backup, perform the atomic server swap/restart, then verify service status, API health, and available storage.
- Recovery: the deployment script will record the exact server/config/data backup paths after it completes; rollback will use that recorded set.

Next action: mirror this journal to the host, run the release package/build gates, then force deploy only the verified generated artifacts.

## 2026-07-24T19:25Z -- forced rollout blocked before mutation by release gate

- The user reconfirmed authorization to restart with players online. No deployment mutation occurred.
- Completed checks: current service was active with 6 players; forced authorization policy contract passed; source-package readiness/smoke checks passed after release-scoped untracked files were staged and local diagnostic files were moved outside the repository.
- Blocking result: the required full production test batch was started but was interrupted by the user after approximately 11 minutes. The generated source package is therefore explicitly `productionEligible=false`, and the receipt-bound server deploy tool correctly refuses a package without completed production verification.
- The server has not been stopped, restarted, swapped, or changed; no database, snapshots, configuration, or game data were mutated. Current preflight health remains the prior active service with 6 players and about 5.0 GiB free.
- Recovery/rollback: not applicable; no deployment began.

Next action: rerun the required production test batch to completion, then create the production-eligible source/binary receipts and execute the already authorized forced deploy.

## 2026-07-25T00:05Z -- forced deployment reconfirmed; read-only preflight

- User explicitly reconfirmed authorization for the deployment and restart while players are online.
- Before host access, the installed `/opt/monolith-ds/AI_SERVER_JOURNAL.md` SHA256 exactly matched the repository copy: `ade582a846c129e9d1e66e177346afec9ca5413ba09ffd6a819a1c492a9a588e`.
- Read-only preflight: `monolith-ds.service` active; `/status` reported round 160 in progress with 7 players; root filesystem 87% used with about 5.0 GiB free.
- Candidate source package is locally verified and production-eligible, but its temporary authorization metadata expired before this explicitly reconfirmed deployment. A new policy-bound source receipt and matching binary receipt are required before any server mutation.
- No server data, service, deployment, configuration, snapshots, or database state was changed in this preflight.

Next action: renew the local authorization metadata, rerun the formal package gate for a fresh receipt, then build and verify the receipt-bound client/server artifacts before the user-authorized forced deployment.

## 2026-07-25T00:19Z -- forced deploy attempt stopped safely at active-ship save barrier

- User explicitly authorized deployment/restart with players online. Verified receipt-bound artifacts and passed client/server release-surface audit before mutation.
- Client publication: static client archive `24ec9a764241f4357c24f448186bd7b831220214fdc41b6e37685b1509576ed3` was published successfully; HTTP verification returned 200.
- Deployment attempt: guarded deploy reached extract verification, CDN metadata verification, config staging, and the required authenticated ship-save barrier. With 5 players in round 160, the barrier endpoint returned HTTP 409. The tool refuses to bypass this barrier even with `-Force`, so no service stop/restart, server swap, database mutation, snapshot mutation, or config replacement occurred.
- Pre-existing temporary staging data may remain under `/opt/monolith-ds/deploy-staging`; the live server directory and live config remain unchanged.
- Recovery: not required because the live service was not stopped or replaced.

Next action: wait briefly and retry the guarded deployment so the active-ship save barrier can produce a fresh receipt. Do not bypass the barrier; if it continues returning 409, inspect its bounded reason and resolve only the saving condition.

## 2026-07-25T00:28Z -- round restarted but deployment barrier still protects connected sessions

- User reported restarting the round and again requested deployment. Read required journals and verified the installed journal hash before the retry.
- Read-only preflight found `monolith-ds.service` active, new round 161 in lobby (`run_level=0`), 6 connected sessions, and root filesystem 89% used with about 4.4 GiB free.
- Retried the exact receipt-bound forced deploy. It again passed staging/extract/config/CDN checks, then the authenticated ship-save barrier returned HTTP 409. Source inspection confirms the current live endpoint deliberately returns this status whenever any session is connected, regardless of round state; it will not freeze/save active persistent ships while sessions exist.
- No service stop/restart, server swap, live config replacement, database mutation, or ship snapshot mutation occurred. The externally published matching client archive remains available.
- Recovery: not applicable; live server remains unchanged.

Next action: require all six sessions to disconnect, then immediately rerun the guarded deploy; the barrier will save/freeze ships, back up data, swap the server, restart it, and run health checks.

## 2026-07-25T00:11Z -- user-authorized direct service restart; deployment remains unapplied

- User explicitly instructed a server restart despite connected players after the guarded deployment barrier repeatedly refused active sessions.
- Performed bounded operation: `sudo systemctl restart monolith-ds.service`. This restarted the prior live server directory; no server package swap, config replacement, database backup/mutation, snapshot mutation, or release artifact installation was performed.
- Result: service is active under a new process, `/status` is healthy with round 162 in lobby and 0 players. Root filesystem is 89% used with about 4.4 GiB free. Startup logs show server Ready and hub advertisement; only existing duplicate-emote warnings and one initial `Cannot keep up` startup warning were observed.
- Important release state: the verified client archive remains published, but the new persistent-shuttle server binary has NOT been deployed because the mandatory ship-save barrier requires zero sessions before a guarded swap.
- Recovery: service restart itself needs no file rollback. If required, restart the service again with `sudo systemctl restart monolith-ds.service`.

Next action: while player count remains zero, rerun the receipt-bound guarded deploy so it can obtain a valid ship-save receipt, back up `/opt/monolith-ds/data`, swap the prepared server, and perform post-deploy verification.

## 2026-07-24T23:20:18Z -- direct authorized release swap rolled back after migration startup failure

- User explicitly authorized a direct production update and restart even with connected players after the guarded ship-save barrier repeatedly returned HTTP 409. The direct operation retained artifact checksum verification, a full pre-stop data backup, a live-config backup, directory-level atomic swap, and automatic rollback.
- Verified staged server ZIP SHA256 56933fe22aa529b582b9a39d4efda82a4959f19a1701f36151a75293ce56364 and staged config SHA256 2378ff06a422ac7a6d60a3dc6f007218ef98350ea0ad61494aba9ea70321074; release client was already published as 24ec9a764241f4357c24f448186bd7b831220214fdc41b6e37685b1509576ed3.
- Created and verified recovery material before stop: /opt/monolith-ds/backups/data-luam-20260725-shuttle-persistence-direct-authorized.tar.gz (gzip test passed, root-only) and /opt/monolith-ds/backups/server_config-before-luam-20260725-shuttle-persistence-direct-authorized.toml (root-only).
- The new server began startup but database migration failed with SQLite Error 19: UNIQUE constraint failed: luam_ship_snapshot.owner_user_id. The health wait timed out and the script automatically restored the prior server directory and config, then restarted the prior service. Do not treat the new server binary as deployed.
- Post-rollback health: monolith-ds.service active; /status healthy in round 163 lobby with zero players; /info advertises previous client hash 2962b8600c4b05eb5082ceae2d488fe7616b19c0b4223b32c709c0dffb814766; root storage 89% used with about 4.3 GiB free.
- Database caution: the failed migration was attempted against live data before the process aborted. The deployment data archive is the recovery point; inspect migration SQL and database migration history against a copy before any further deployment.
- Rollback/recovery: the prior server directory and config are active. If database recovery is necessary, stop the service and restore /opt/monolith-ds/data from the verified archive above, then start the service.

Next action: reproduce the duplicate-owner snapshot data and migration failure against a copy of the backed-up database; add a deterministic deduplication or migration repair and test it before building a new release artifact.

## 2026-07-24T23:23:03Z -- read-only analysis of blocked one-ship migration

- Read-only production database inspection used SQLite URI mode=ro and aggregate-only results; no player identifiers, raw snapshots, or row payloads were read or retained.
- Database integrity: PRAGMA quick_check returned ok. The production migration history ends at 20260720164259_LuaMShipPayloadRevision; the failed 20260723210901_LuaMOnePersistentShipPerOwner migration is not recorded as applied.
- Ship registry aggregate: 14 total snapshot rows; 7 non-retired and 7 retired. Exactly one owner has two non-retired rows (two conflicting rows). The release migration creates unique partial index UX_luam_ship_snapshot_active_owner on owner_user_id WHERE status <> 4, so SQLite correctly rejects that existing conflict.
- Recovery state remains intact: old server is active, /status is healthy in lobby (one connected player at read time), and the verified pre-swap data archive remains the recovery point.
- No repair was applied. Safe next action: copy the verified data backup to an isolated workspace, inspect the two conflicting rows only within that copy, choose deterministic retention/quarantine policy that preserves recoverability, then add a migration plus regression test.

## 2026-07-25T17:24Z -- read-only duplicate-spirit investigation

- User reported numerous spirits named `Р РЋР ВµР Т‘Р В¶Р В°Р в„–Р Т‘Р В¶Р С‘Р В»Р В°Р С”РЎРѓ-Р вЂР В°РЎвЂљР В°РЎР‚` on Georgiy's shuttle. Read-only journal inspection found repeated invalid `Mind` entity-reference deserialization during map/entity loads, followed by duplicate assignment attempts for the same account to distinct `MindBase` entities with that character name.
- Observed occurrences were at 19:17, 19:20, 19:52, 20:05, and 20:12 MSK. The server correctly ignored each duplicate user field, so this is not evidence that multiple players created those spirits or that Georgiy spawned them manually. The records point to a malformed or stale serialized shuttle/entity state being loaded repeatedly.
- No entity, shuttle, database, service, configuration, player, or round mutation was performed. Host health at inspection: `monolith-ds.service` active; round 164 running with 2 players; root storage 91% used with about 3.5 GiB free. The installed server journal matched the pre-update repository copy and was `root:root` mode `0644`.
- Recovery: no action was taken. Do not delete entities or restart the service while players are active without a separately authorized, bounded remediation plan.

Next action: reproduce the invalid `Mind` reference path locally from a copy of the affected shuttle snapshot, then add snapshot sanitization and a regression before any production cleanup.

## 2026-07-25T18:15Z -- read-only intrusion indicators audit

- Performed a bounded, read-only 24-hour audit of SSH authentication, fail2ban, privilege/service-change records, enabled units, listening sockets, game health, storage, and journal mirror metadata. No host mutation was made.
- No unrecognized successful SSH authentication was found: all retained successful SSH entries used the expected `monolithadmin` public-key identity. The privilege/service records were consistent with the documented deployment/restart activity; no unexpected account, SSH-key, sudoers, service-enable, or cron-install entry was found in the bounded query.
- SSH is under continued password-brute-force pressure: 1,287 failed/invalid authentication events in the preceding 24 hours. Fail2ban had 16 currently banned addresses and 497 total bans; this is attack traffic, not proof of a successful intrusion.
- Listener review showed only expected public SSH, HTTP, game ports, and documented loopback services. `monolith-ds.service` was active with zero restarts since its documented startup; round 164 had four players. Root storage remained 91% used with about 3.5 GiB free.
- Assessment: the inspected evidence shows no sign of successful external penetration, but retained logs cannot prove absence of compromise. Continue monitoring and investigate immediately if an unfamiliar successful SSH key/user, new listener, unapproved privileged action, or unexpected unit appears.

Next action: retain the current fail2ban/SSH monitoring and perform a separate integrity-baseline audit of authorized keys, sudo policy, systemd units, and deployed binaries before the next production release.

## 2026-07-25T18:23Z -- bounded disk-space recovery

- User requested disk-space recovery. Read-only preflight found the root filesystem at 93% used with about 2.9 GiB free while round 164 was running with five players; `monolith-ds.service` was active.
- Removed only verified obsolete deployment material: five `server.prev-*` rollback directories from 2026-07-02/03 and stale failed/obsolete deployment-stage extracts, ZIPs, and Python cache. The current `server-luam-20260725-comprehensive` staging directory and ZIP were deliberately retained.
- No live server directory, `/opt/monolith-ds/data`, database, configuration, active client publication, current staging input, backup set, service, player, or round state was changed.
- Result: root free space increased to 7,133,229,056 bytes (about 6.6 GiB), 83% used. The remaining deployment staging is about 307 MiB.
- Recovery: deleted files were obsolete rollback/deployment copies only; current production remains unchanged. If an old July 2/3 rollback artifact is exceptionally needed, recover it from an external/off-host source rather than the live server.

Next action: retain the current staging release input until its deployment outcome is resolved; separately define backup/client retention before deleting any retained recovery or public-client artifacts.

## 2026-07-25T18:28Z -- user-authorized service restart

- User explicitly requested a server restart while round 165 had six connected players. Preflight confirmed the active service, journal mirror metadata, and about 7.13 GB free on root.
- Restarted only `monolith-ds.service` with `sudo systemctl restart monolith-ds.service`; no package swap, configuration change, database mutation, snapshot mutation, or cleanup was performed.
- Initial status probes during startup failed because the local status listener was not ready yet; this was transient. The service log then reported `Ready`, hub advertisement succeeded, and the final `/status` check reported round 166 in lobby with zero players.
- Post-restart health: service active, game listener restored on TCP/UDP 1212, root free space 7,140,171,776 bytes (83% used). Startup emitted existing duplicate-emote warnings and one initial main-loop catch-up warning only.
- Recovery: restarting the same service is the rollback action if needed: `sudo systemctl restart monolith-ds.service`.

Next action: monitor player reconnection and do not assume the un-deployed release changes are present; this restart used the existing live server directory.




## 2026-07-26T10:30Z -- comprehensive update authorized; read-only production preflight

- Authorization: operator explicitly requested building and deploying the current comprehensive update. Local release policy now permits only receipt-bound `client-static` and guarded `server-release` mutations until 2026-07-26T16:00:00Z; required backups, ship-save barrier, artifact verification, and health checks remain mandatory.
- Journal reconciliation: before host access, repository and installed `/opt/monolith-ds/AI_SERVER_JOURNAL.md` matched SHA256 `496afb4f715a5d355b7405371e4efaddf7ce9ccefb814931be2b29a6333c6ee1`; installed ownership/mode was `root:root`/`0644`.
- Read-only preflight: `monolith-ds.service` was active; `/status` reported round 172 running with one player; root storage was 89% used with about 4.3 GiB available. No package, service, database, snapshot, player, round, or configuration mutation occurred in this preflight.
- Local preparation: release contract passed after recording fresh authorization. The comprehensive source batch is being committed and must pass the full production tests and smokes before any publication or server mutation.
- Next action: mirror this journal, run `Tools/build_luam_release_package.ps1 -RunTests -RunLocalSmoke -Json`, then continue only if it reports `productionEligible=true`.
## 2026-07-26T10:48Z -- release gate stopped before production mutation

- The authorized comprehensive release gate did not produce a package: content tests, gateway smoke, and local-stack smoke passed, while the integration batch completed 350 tests but aborted during pooled teardown on an expected duplicate-body safety log from a faulty regression fixture.
- Local diagnosis/fix: the three new ghostrespawn regressions now use the fixture's existing player body instead of creating a second matching body. Integration build passed and the focused batch passed 3/3. A new full gate is required; the failed attempt is not production evidence.
- Production impact: none. No client publication, service restart, server swap, database, ship snapshot, configuration, round, or player mutation occurred.
- Next action: commit the fixture correction, rerun the full clean release gate, and continue only if `productionEligible=true`.
## 2026-07-26T11:05Z -- second release gate stopped locally; cryo fixture family corrected

- The second authorized gate produced no package. It passed content tests and both smokes, then stopped with 387 integration tests passing and one deep-cryo round-cleanup fixture failure.
- Cause/fix: two older tests created synthetic bodies in addition to the real body now assigned to connected sessions. They now use/finalize the existing playable body consistently. The two affected tests passed 2/2 and the full deep-cryo runtime class passed 52/52.
- Production impact: none. No client publication, service restart, server swap, database, ship snapshot, configuration, round, or player mutation occurred.
- Next action: commit and rerun the full clean production gate; do not deploy without `productionEligible=true`.
## 2026-07-26T11:36Z -- comprehensive release deployed and verified

- Result: deployed the operator-authorized `luam-20260726-comprehensive` release containing the new-body ghostrespawn repair, restored-mind cleanup, Vanguard radio/uplink content, tile-cutter production chain, admin command restrictions/audit, and asteroid-belt POI relocation.
- Release evidence: full source gate passed with `productionEligible=true`; source SHA256 `665ccc64ca199259b9b0d8f3e9dd87bc5f09c7a721fc578a9b4a5710ec681f7e`, payload digest `6540b541e4bad7593d94009da803205a945ee36df93cf0d3f0789cf9b07f1549`, server SHA256 `c5e9538b7c11f02bec5ae848f01c72ad646b5b90d6b9502c369b9b705b8508e2`, client/build `752ce027d6d3d99935b1e0e997b091031fd2b64dd51f3b1f49468462a51c3521`, receipt SHA256 `4492d0f6dcd3792ea3bf6009e3b9cef7fa7639c833da6aa0fa3a01face3b8ebe`. Surface audit found zero violations.
- Deployment: client publication returned HTTP 200. The first guarded server attempt stopped safely at HTTP 409 with one active session before service stop or swap. A temporary `inet luam_deploy_guard` blocked new non-loopback UDP/1212 sessions; the explicitly authorized restart created a zero-session lobby. Authenticated ship-save barrier `bc41a7aa-6e06-4f1c-b91a-cea0643c5ea9` completed with attempted/saved/failed/activeRemaining all zero and publication frozen, then the verified server swap/restart succeeded. The temporary nft table was deleted and proved absent.
- Recovery: data backup `/opt/monolith-ds/backups/data-luam-20260726-comprehensive.tar.gz` passes `gzip -t`, is `root:root` mode `0600`, size 111,729,867 bytes. Server rollback is `/opt/monolith-ds/backups/server-luam-20260726-comprehensive`; config rollback is `/opt/monolith-ds/backups/server_config-before-luam-20260726-comprehensive.toml`.
- Health: service active with `NRestarts=0`, `ExecMainStatus=0`; round 174 lobby with zero players; `/info` advertises the exact new client version and URL; client URL returns HTTP 200. Installed `/opt/monolith-ds/server/Resources/Assemblies/Content.Server.dll` SHA256 is `889e9cfed78a30d54fcf235924bbdf4c4c3b2bb15c19621861c168047ec830a2`. No fatal, migration, exception, failed-map-load, POI, or asteroid error appeared in the bounded post-start log. Root is 91% used with about 3.7 GiB free.
- Policy/next action: deployment is frozen again. Perform player smokes for join/ghostrespawn, Vanguard channel/key, tile cutter production, and asteroid-belt station placement.
