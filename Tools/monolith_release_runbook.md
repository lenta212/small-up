# Monolith LuaM Release Runbook

This runbook is for local release preparation only. Do not upload, restart, or update the remote server while `Tools\luam_release_policy.json` has `remoteDeployFrozen` set to `true`.

## Current invariants

- The primary `MonoAllAtOnce` Apocalypse preset has no `RoundEndTimeRule` override, and its only fallback is `MonoMixed`; both use the 7-day automatic round ceiling from `shuttle.auto_call_time = 10080`. If an automatic shuttle is recalled, the normal extension interval applies instead of restarting a 7-day override.
- Live capacity stays at `game.soft_max_players = 100` with `net.max_connections = 128` for administrator and handshake headroom.
- The public `/info` response must remain below the hub 10 KiB limit; the confirmed 2026-07-11 rollout reports 3404 bytes.
- Real deploys must use `Tools\deploy_luam_server_release.ps1`; do not hand-run `scp`, `unzip`, or `systemctl restart` for release rollout.
- Real deploys require `-ExpectedSha256`.
- Use `-RequireDataBackup -RemoteDataDir <live-data-dir>` after the live systemd data directory is confirmed.
- The confirmed live paths are `-RemoteConfigPath /opt/monolith-ds/server/server_config.toml` and `-RemoteDataDir /opt/monolith-ds/data`; pass the reviewed `server_config.remote.toml` through `-ConfigSourcePath`.
- `-Force`, `-SkipPostVerify`, and `-AllowClientZipRestore` are emergency options only.

## Safe read-only checks

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\monolith-restart-when-empty.ps1 -VerifyOnly -SkipSsh
```

Expected result: `ok=true`. `strict_ok=false` is acceptable while the server is in the lobby and `/status.round_start_time` is not available, provided the only warning is `round-age` and the hub entry is present for `ss14://188.127.225.57:1212/`.

If `/info.connect_address` is empty, confirm the live config has `connectaddress = "udp://188.127.225.57:1212"` under `[status]`. Apply that config only during an approved maintenance window; it requires the server process to reload/restart before `/info` changes.

## Local readiness

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\check_luam_release_ready.ps1 -RunTests -RunLocalSmoke -Json
```

This gate covers release scope, strict UTF-8, LuaM feature validation, admin-rank policy, gateway tests, LuaM dotnet filters, local stack smoke, dependency vulnerability audit, and the 7-day round-length invariant.

## Build artifacts

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\build_luam_release_package.ps1 -RunTests -RunLocalSmoke -Json
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\build_luam_server_release.ps1 -ExternalClientBaseUrl http://188.127.225.57:1213 -Json
```

Record these hashes from command output:

- LuaM source package SHA256.
- `release\SS14.Server_linux-x64.zip` SHA256.
- `release\SS14.Client.zip` SHA256.

## Verify artifacts

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\verify_luam_release_package.ps1 -PackagePath DeploymentPackages\LuaM\<package>.zip -Json
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\audit_release_surface.ps1 -ReleaseDir release -Json
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\build_luam_server_release.ps1 -SkipPackageBuild -ExternalClientBaseUrl http://188.127.225.57:1213 -Json
```

The production server release must report `hybridAcz=false`, `externalClientDelivery=true`, `buildJson=true`, and `audited=true`. `Content.Client.zip` must stay outside the server archive. `-HybridAcz` is retained only for an emergency fallback build.

Provision the immutable client file before deploying the matching server:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\provision_monolith_client_static.ps1 -ClientPackagePath release\SS14.Client.zip -ExpectedSha256 <client-sha256> -Version <build-version>
```

The URL returned by this command must exactly match `clientDownloadUrl` from the build result.

## Deploy dry-run

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\deploy_luam_server_release.ps1 -PackagePath release\SS14.Server_linux-x64.zip -ExpectedSha256 <server-sha256> -Tag <tag> -ConfigSourcePath server_config.remote.toml -RemoteConfigPath <live-server_config.toml> -RequireDataBackup -RemoteDataDir <live-data-dir> -DryRun
```

Expected while frozen: `freeze_policy.active=true` and `freeze_policy.source=json`.

## Real deploy after freeze is lifted

Only run this after `remoteDeployFrozen=false`, readiness/build/audit are fresh, the live server is empty, and the live config/data paths are confirmed:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\deploy_luam_server_release.ps1 -PackagePath release\SS14.Server_linux-x64.zip -ExpectedSha256 <server-sha256> -Tag <tag> -ConfigSourcePath server_config.remote.toml -RemoteConfigPath <live-server_config.toml> -RequireDataBackup -RemoteDataDir <live-data-dir>
```

Post-deploy, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\monolith-restart-when-empty.ps1 -VerifyOnly -SkipSsh
```

Then review `backup_dir`, `config_backup`, `config_sha256`, and `data_backup` from deploy output.
