# Monolith LuaM Release Runbook

This runbook is for local release preparation only. Do not upload, restart, or update the remote server while `Tools\luam_release_policy.json` has `remoteDeployFrozen` set to `true`.

## Current invariants

- LuaM rounds stay 7 days: `shuttle.auto_call_time = 10080`.
- Real deploys must use `Tools\deploy_luam_server_release.ps1`; do not hand-run `scp`, `unzip`, or `systemctl restart` for release rollout.
- Real deploys require `-ExpectedSha256`.
- Use `-RequireDataBackup -RemoteDataDir <live-data-dir>` after the live systemd data directory is confirmed.
- Use `-RemoteConfigPath <live-server_config.toml>` after the live config is moved outside the packaged server directory.
- `-Force`, `-SkipPostVerify`, and `-AllowClientZipRestore` are emergency options only.

## Safe read-only checks

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\monolith-restart-when-empty.ps1 -VerifyOnly -SkipSsh
```

Expected result: `ok=true`. `strict_ok=false` is acceptable when the only warning is an empty `/info.connect_address` and the hub entry is present for `ss14://188.127.225.57:1212/`.

## Local readiness

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\check_luam_release_ready.ps1 -RunTests -RunLocalSmoke -Json
```

This gate covers release scope, strict UTF-8, LuaM feature validation, admin-rank policy, gateway tests, LuaM dotnet filters, local stack smoke, dependency vulnerability audit, and the 7-day round-length invariant.

## Build artifacts

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\build_luam_release_package.ps1 -RunTests -RunLocalSmoke -Json
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\build_luam_server_release.ps1 -Json
```

Record these hashes from command output:

- LuaM source package SHA256.
- `release\SS14.Server_linux-x64.zip` SHA256.
- `release\SS14.Client.zip` SHA256.

## Verify artifacts

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\verify_luam_release_package.ps1 -PackagePath DeploymentPackages\LuaM\<package>.zip -Json
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\audit_release_surface.ps1 -ReleaseDir release -Json
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\build_luam_server_release.ps1 -SkipPackageBuild -Json
```

The server release verification must report matching `clientSha256` and `embeddedClientSha256`, `hybridAcz=true`, and `audited=true`.

## Deploy dry-run

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\deploy_luam_server_release.ps1 -PackagePath release\SS14.Server_linux-x64.zip -ExpectedSha256 <server-sha256> -Tag <tag> -RemoteConfigPath <live-server_config.toml> -RequireDataBackup -RemoteDataDir <live-data-dir> -DryRun
```

Expected while frozen: `freeze_policy.active=true` and `freeze_policy.source=json`.

## Real deploy after freeze is lifted

Only run this after `remoteDeployFrozen=false`, readiness/build/audit are fresh, the live server is empty, and the live config/data paths are confirmed:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\deploy_luam_server_release.ps1 -PackagePath release\SS14.Server_linux-x64.zip -ExpectedSha256 <server-sha256> -Tag <tag> -RemoteConfigPath <live-server_config.toml> -RequireDataBackup -RemoteDataDir <live-data-dir>
```

Post-deploy, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\monolith-restart-when-empty.ps1 -VerifyOnly -SkipSsh
```

Then review `backup_dir`, `config_backup`, `config_sha256`, and `data_backup` from deploy output.
