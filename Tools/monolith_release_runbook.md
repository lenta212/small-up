# Monolith LuaM Release Runbook

This runbook is for local release preparation by default. Do not upload, restart, or update the remote server while `Tools\luam_release_policy.json` has `remoteDeployFrozen` set to `true`.

Current rollout state (2026-07-18): production rollout `luam-20260718-195400` is complete and remote deployment is frozen again. Any new server or client-static mutation requires a fresh explicit authorization and newly rebuilt policy-bound artifacts.

## Development cadence

Use two separate loops:

1. During implementation, run scoped local checks only:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\prepare_luam_hotfix.ps1 -Scope Auto -Json
```

Use an explicit scope when the changed area is known:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\prepare_luam_hotfix.ps1 -Scope PdaUi -Json
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\prepare_luam_hotfix.ps1 -Scope ShuttleUi -Json
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\prepare_luam_hotfix.ps1 -Scope Cryo -Json
```

Add `-IncludeBuild` when the touched code affects compiled client/server behavior and the scoped tests alone are not enough evidence.

2. Immediately before a deploy, run the full production gate through the orchestrator:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\ship_luam_release.ps1 -Force -Json
```

This performs local-fast, source package build with full production tests and local smoke, source verification, binary build, release-surface audit, client-static dry-run, and server-deploy dry-run. It does not mutate the remote server unless `-Deploy` is also passed and the release policy contains a current authorization for the requested mutations.

`Tools\luam_release_state.ps1` maintains `.agents\current_release.json` so the next run can see the latest local-fast, production-gate, or deployment result without redoing context discovery.

## Current invariants

- The primary `MonoAllAtOnce` Apocalypse preset has no `RoundEndTimeRule` override, and its only fallback is `MonoMixed`; both use the 7-day automatic round ceiling from `shuttle.auto_call_time = 10080`. If an automatic shuttle is recalled, the normal extension interval applies instead of restarting a 7-day override.
- Live capacity stays at `game.soft_max_players = 100` with `net.max_connections = 128` for administrator and handshake headroom.
- The public `/info` response must remain below the hub 10 KiB limit; the confirmed 2026-07-11 rollout reports 3404 bytes.
- Real deploys must use `Tools\deploy_luam_server_release.ps1`; do not hand-run `scp`, `unzip`, or `systemctl restart` for release rollout.
- Real deploys require both the independently recorded server SHA256 and the independently recorded SHA256 of `luam-binary-release-receipt.json`.
- `Tools\luam_release_policy.json` is the single machine-readable authority for required payload files, production test suites, smoke checks, and remote freeze. Missing or invalid policy data fails closed.
- The production rollout completed on 2026-07-16 with DB-first preference/bank/commerce finalizers, provider-matched expedition/progression shadow persistence, and physical AI that is disabled by default and remains bounded by hard per-map/live/spawn caps when explicitly enabled. Any post-rollout follow-up remains local-only while the policy is frozen.
- Persistent-to-persistent MonoCoins transfer is an atomic journaled database transaction. The generic Bank finalize callbacks used by the six ATM/cargo/shipyard/commerce callers are only exception-atomic inside the running process; they are not payment-plus-world-mutation crash-atomic. A universal crash-safe saga still requires all callers to use stable operation IDs and durable fulfillment/recovery state.
- Deep cryo is DB-first: both cryo paths must commit the profile-bound body/inventory snapshot before removing the live body. Restore must claim and then consume the snapshot before `ControlMob` or exposing inventory; quarantine and profile lifecycle transitions fail closed.
- `Resources\ServerOnly\_LuaM\client-package-canary.txt` must be absent from the client archive and present in the server archive produced by the same fresh build.
- Use `-RequireDataBackup -RemoteDataDir <live-data-dir>` after the live systemd data directory is confirmed.
- The confirmed live paths are `-RemoteConfigPath /opt/monolith-ds/server/server_config.toml` and `-RemoteDataDir /opt/monolith-ds/data`; pass the reviewed `server_config.remote.toml` through `-ConfigSourcePath`.
- `-Force` is an emergency operational override, not an authorization mechanism. The historical 2026-07-16 approval is expired; any future use requires a fresh explicit authorization. Receipt-bound production deploys still reject `-SkipPostVerify` and `-AllowClientZipRestore`.

## Safe read-only checks

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\monolith-restart-when-empty.ps1 -VerifyOnly -SkipSsh
```

Expected result: `ok=true`. `strict_ok=false` is acceptable while the server is in the lobby and `/status.round_start_time` is not available, provided the only warning is `round-age` and the hub entry is present for `ss14://188.127.225.57:1212/`.

If `/info.connect_address` is empty, confirm the live config has `connectaddress = "udp://188.127.225.57:1212"` under `[status]`. Apply that config only during an approved maintenance window; it requires the server process to reload/restart before `/info` changes.

## Local readiness

```powershell
python Tools\validate_luam_feature_pack.py --self-test
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\test_luam_release_contract.ps1 -Json
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\check_luam_release_ready.ps1 -RunTests -RunLocalSmoke -Json
```

The feature-validator self-test deliberately tampers with the DB-first/exact-profile BankSystem contract and must fail closed for legacy whole-profile saves, slot-only writes, and world finalizers reordered before the durable debit. The release contract runs this self-test again so validator drift cannot silently pass readiness.

The lightweight contract command validates policy structure, every policy-required file path, the policy-bound bank, physical-AI, durable-persistence, radar/map, progression, commerce, and geometry tests, smoke declarations, script wiring, and the current freeze without building or launching the stack. Strict readiness then covers release scope, strict UTF-8, LuaM feature validation, admin-rank policy, policy-owned LuaM dotnet filters in CI-aligned `DebugOpt --no-restore`, gateway and local-stack smoke, dependency vulnerability audit, and the 7-day round-length invariant.

Before accepting persistence evidence, confirm that both SQLite and PostgreSQL migrations/snapshots have no pending model changes and that `LuaMDurablePersistenceTest`, `LuaMDeepCryoPersistenceTest`, and `LuaMDeepCryoRuntimeTest` pass. Deep-cryo evidence must cover nested inventory, authority sanitization, incompatible-prototype quarantine, idempotent replay, concurrent claim fencing, expired lease recovery, and immutable DB triggers. Before enabling physical AI anywhere, confirm the default-off test, growth-limit test, former story scenarios, disable cleanup, and round cleanup under the exact package revision.

## Build artifacts

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\build_luam_release_package.ps1 -RunTests -RunLocalSmoke -Json
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\verify_luam_release_package.ps1 -PackagePath DeploymentPackages\LuaM\<package>.zip -ExpectedSha256 <source-sha256> -Json
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\build_luam_server_release.ps1 -ExternalClientBaseUrl http://188.127.225.57:1213 -SourcePackagePath DeploymentPackages\LuaM\<package>.zip -ExpectedSourcePackageSha256 <source-sha256> -ReleaseReceiptPath release\luam-binary-release-receipt.json -Json
```

Record these hashes from command output:

- LuaM source package SHA256.
- `release\SS14.Server_linux-x64.zip` SHA256.
- `release\SS14.Client.zip` SHA256.
- `release\luam-binary-release-receipt.json` SHA256.
- Source payload digest, worktree digest, and Git HEAD reported by both source verification and the binary receipt.

## Verify artifacts

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\verify_luam_release_package.ps1 -PackagePath DeploymentPackages\LuaM\<package>.zip -ExpectedSha256 <sha256> -Json
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\audit_release_surface.ps1 -PackagePath release\SS14.Client.zip,release\SS14.Server_linux-x64.zip -Json
```

The production server release must report `hybridAcz=false`, `externalClientDelivery=true`, `buildJson=true`, `audited=true`, and a receipt path/hash. `Content.Client.zip` must stay outside the server archive. Production refuses `-SkipPackageBuild` and `-SkipAudit`; those switches require `-LocalOnly` and produce no deployable receipt.
The source-package verifier must report `productionEligible=true` plus passed `package-hash`, `payload-digest`, `policy-required-files`, and `readiness-evidence`. It rejects a whole-package SHA mismatch, non-canonical/oversized/case-colliding paths, manifest/list drift, policy/worktree/payload binding mismatch, any `AllowUntracked` evidence, or missing `requiredForProduction` test/smoke evidence.
The release-surface audit must additionally prove that the server-only canary is absent from the client archive and present in the server archive. Reusing an older archive that predates the canary is not valid evidence; rebuild both artifacts fresh from the reviewed package revision.

Provision the immutable client file before deploying the matching server:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\provision_monolith_client_static.ps1 -ClientPackagePath release\SS14.Client.zip -ExpectedSha256 <client-sha256> -Version <build-version> -ReleaseReceiptPath release\luam-binary-release-receipt.json -ExpectedReleaseReceiptSha256 <receipt-sha256> -DryRun
```

While frozen, this command validates the complete plan only when the receipt was freshly built under the exact active policy. The URL in the plan must exactly match `clientDownloadUrl` from that receipt. A historical receipt from a completed rollout remains bound to its authorized policy revision and must fail active-policy binding after the policy is frozen again; do not edit or relabel it. Remove `-DryRun` only after the final authorized policy is in place and all receipt evidence has been rebuilt under its exact policy hash.

## Deploy dry-run

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\deploy_luam_server_release.ps1 -PackagePath release\SS14.Server_linux-x64.zip -ExpectedSha256 <server-sha256> -ReleaseReceiptPath release\luam-binary-release-receipt.json -ExpectedReleaseReceiptSha256 <receipt-sha256> -Tag <tag> -ConfigSourcePath server_config.remote.toml -RemoteConfigPath <live-server_config.toml> -RequireDataBackup -RemoteDataDir <live-data-dir> -DryRun
```

Expected for a current-policy dry-run while frozen: `freeze_policy.active=true` and `freeze_policy.source=json`. A retained receipt from an earlier authorized rollout should fail policy binding before a plan is produced.

## Real deploy after freeze is lifted

Do not flip only `remoteDeployFrozen`. The same policy revision must set `deploymentAuthorization.state=authorized`, record approval identity plus UTC approval/expiry timestamps, explicitly list each allowed mutation, and set the batch state to `deployment-authorized`. Then rerun readiness and rebuild/reverify source, binary, receipt, and audit under that exact policy hash. The contract test is state-agnostic and validates both consistent frozen and authorized policies, so the gate can pass after authorization without a logical deadlock.

Only run this after that non-expired authorization exists, all receipt evidence is fresh, and the live config/data paths are confirmed. This rollout is explicitly authorized to proceed without waiting for an empty server; keep `-Force` in the reviewed command:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\deploy_luam_server_release.ps1 -PackagePath release\SS14.Server_linux-x64.zip -ExpectedSha256 <server-sha256> -ReleaseReceiptPath release\luam-binary-release-receipt.json -ExpectedReleaseReceiptSha256 <receipt-sha256> -Tag <tag> -ConfigSourcePath server_config.remote.toml -RemoteConfigPath <live-server_config.toml> -RequireDataBackup -RemoteDataDir <live-data-dir> -Force
```

Post-deploy, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\monolith-restart-when-empty.ps1 -VerifyOnly -SkipSsh
```

Then review `backup_dir`, `config_backup`, `config_sha256`, and `data_backup` from deploy output.
