# LuaM Local Release Manifest

Current policy: do not upload, restart, or update the remote server until the server freeze is lifted.

The machine-readable policy lives in `Tools\luam_release_policy.json`. The deploy helper reads that JSON file first and falls back to this manifest only if the JSON file is missing.

This manifest exists to prevent the local LuaM feature pack from being partially released. The LuaM package scope should be present in the git snapshot before deployment; use `Tools\check_luam_release_ready.ps1` as the automated gate for this check.

## Release Scope

The local pack currently covers:

- LuaM AI director administration UI and commands.
- AI chat/radio interaction through explicit markers such as `ИИ`, `иишка`, `luam`, and `/luam`.
- AI Director review API: `POST /review` returns structured influence/process, risk, tempo, economy, crew-load, safety, and recommended-action remarks for admins.
- AI Director admin UI keeps the latest AI review separately, can copy it to clipboard, can save it to the server log, and shows a short review history.
- PDA sector status display without a free-form AI message input.
- PDA bank transfers by short copy-friendly bank ID.
- Donation shop state, PDA listings, and manual account access.
- Sector story memory, dynamic tasks, route text, site notes, evidence, terminal/report flows.
- Dynamic quest debris and periodic hostile contacts on every fifth debris site.
- Subspace/stargate-style temporary portal actions.
- Synthetic/robot control hooks and tests.
- Payroll mapping, pioneer starting grant, payroll status, and real hourly payout.
- AI Director admin-tab entry point, payroll feedback text, rogue AI controller hook, and LuaM underwear slot support.
- Local AI gateway fallback behavior when no external API key is configured.
- Local AI gateway Anthropic Claude Haiku 4.5 mode through `LUAM_AI_PROVIDER=anthropic`.
- Local AI gateway structured `/review` fallback and Anthropic/OpenAI provider paths for `summary`, `influenceRemarks`, `processRemarks`, `riskRemarks`, `tempoRemarks`, `economyRemarks`, `crewRemarks`, `safetyNotes`, and `recommendedActions`.
- Local AI gateway audit reader for model, request path, status, time window, token, cache-token, fallback, estimated cost, and observed provider-cost checks.
- Release-surface protection: client/server zip packaging drops debug symbols, source files, project files, local secrets, logs, and cache artifacts; CI runs `Tools/audit_release_surface.ps1` before publish.

## Files That Must Be Included

Tracked modified files:

- `Content.Client/PDA/PdaBoundUserInterface.cs`
- `Content.Client/PDA/PdaMenu.xaml`
- `Content.Client/PDA/PdaMenu.xaml.cs`
- `Content.Server/PDA/PdaSystem.cs`
- `Content.Server/_NF/Bank/BankSystem.cs`
- `Content.Shared/PDA/PdaComponent.cs`
- `Content.Shared/PDA/PdaMessagesUi.cs`
- `Content.Shared/PDA/PdaUpdateState.cs`

LuaM file groups that must be in the release/package snapshot:

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
- LuaM-linked admin tab, Bounty Contracts, Bank/Payroll locale, LateJoin, cartridge, research, role-time, pinpointer, synthetic-control, clothing/underwear slot, rogue AI, character profile, and test-harness files listed in `Tools\check_luam_release_ready.ps1`.
- `Tools/luam_ai_gateway.py`
- `Tools/summarize_luam_ai_audit.py`
- `Tools/test_luam_ai_gateway.py`
- `Tools/validate_luam_feature_pack.py`
- `Tools/local_stack.md`
- `Tools/audit_release_surface.ps1`
- `Tools/deploy_luam_server_release.ps1`
- `Tools/monolith-restart-when-empty.ps1`
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

Before any release, confirm the exact untracked list with:

```powershell
git ls-files --others --exclude-standard Content.Client/_LuaM Content.Server/_LuaM Content.Shared/_LuaM Content.IntegrationTests/Tests/_LuaM Content.IntegrationTests/Tests/_NF/BountyContracts Content.Tests/Client/_LuaM Content.Tests/Server/_LuaM Content.Tests/Shared/_NF/BountyContracts Resources/ConfigPresets/_LuaM Resources/Locale/en-US/_LuaM Resources/Locale/ru-RU/_LuaM Resources/Prototypes/_LuaM Resources/Prototypes/_Mono/Entities/Markers/Spawners/shuttles.yml Resources/ServerInfo/_LuaM Resources/Textures/_LuaM Tools
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

For live deployment, build a self-contained Hybrid ACZ server package so the server zip carries the matching `Content.Client.zip` and produces a fresh client manifest hash:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\build_luam_server_release.ps1 -Json
```

This writes `release\SS14.Client.zip` and `release\SS14.Server_linux-x64.zip`, verifies that `Content.Client.zip` is embedded in the server package, checks the embedded client SHA256 against `release\SS14.Client.zip`, and runs `Tools\audit_release_surface.ps1 -ReleaseDir release`.
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
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\deploy_luam_server_release.ps1 -PackagePath release\SS14.Server_linux-x64.zip -ExpectedSha256 <sha256> -Tag <tag> -RemoteConfigPath <live-server_config.toml> -RequireDataBackup -RemoteDataDir <live-data-dir>
```

The helper checks `Tools\luam_release_policy.json` first and refuses a real upload while the current freeze policy is active. `-DryRun` remains available for inspecting the deploy plan without contacting the remote server.
For real deployment, the helper requires `-ExpectedSha256`, verifies the local zip contains `Robust.Server` and `Content.Client.zip`, checks the live player count, uploads the package, verifies the remote SHA256, stages into `/opt/monolith-ds/deploy-staging`, preserves the live `server_config.toml`, creates `/opt/monolith-ds/backups/server-<tag>` and `server_config-before-<tag>.toml`, records the live config SHA256, optionally creates `/opt/monolith-ds/backups/data-<tag>.tar.gz` when `-RemoteDataDir` is provided, restores executable bits on `Robust.Server`/`Robust.Packaging`, starts `monolith-ds.service`, and rolls back if start verification fails. `-RemoteConfigPath` defaults to `/opt/monolith-ds/server/server_config.toml`; pass an explicit external path after moving live config outside the server package directory. Use `-RequireDataBackup -RemoteDataDir <live-data-dir>` after confirming the live systemd data directory; with `-RequireDataBackup`, the helper refuses deployment if the data directory is missing or not explicitly provided. Use `-AllowClientZipRestore` only for emergency rollback-style deploys where reusing the previous client zip is intentional.

## Verified Locally

Last local verification:

- The current local package SHA256 is reported by `Tools\build_luam_release_package.ps1` and `Tools\verify_luam_release_package.ps1`; it is not pinned here because this file is included in the package.
- Latest package `scopeAudit` must have 0 unexpected changed files outside package.
- `python Tools\validate_luam_feature_pack.py` passed.
- `python Tools\generate_luam_admin_rank_sql.py --check-only --json` passed, 6 ranks / 90 flags.
- `python Tools\test_luam_ai_gateway.py` passed with local Anthropic Haiku 4.5 no-key fallback, mock Anthropic `/messages`, `/review` structured remarks, audit token/cache/cost fields, and filtered audit-summary reader coverage.
- `dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --filter "FullyQualifiedName~LuaMDonationShopTest" --no-restore` passed; it covers locked balance without access, one-month manual access, duplicate permanent reward blocking, balance spend, ledger actions, and certificate printing.
- `dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --filter "FullyQualifiedName~LuaMDynamicEventDebrisTest" --no-restore` passed; it covers five generated debris sites, GPS marker/site-note clarity, cleanup, and hostile contact only on the fifth site.
- `Tools\check_luam_release_ready.ps1 -RunTests -RunLocalSmoke` passed with `utf8-release-text`, zero untracked release-scope files, LuaM dotnet test filters, and full local gateway/server/client smoke.
- `Tools\verify_luam_release_package.ps1` passed with `utf8-package-text`, `admin-rank-artifacts`, `luam-resource-artifacts`, recorded local smoke evidence, and `scope-audit`.
- `Tools\audit_release_surface.ps1 -ReleaseDir release` passed after rebuilding `SS14.Client.zip` and `SS14.Server_linux-x64.zip`; the previous 15 `.pdb` entries were removed.
- `dotnet list Content.Server.Database\Content.Server.Database.csproj package --vulnerable --include-transitive` passed with no vulnerable packages after pinning `System.Security.Cryptography.Xml` to a patched 10.0.x package.
- `Tools\check_luam_release_ready.ps1` enforces 7-day rounds by requiring `shuttle.auto_call_time = 10080` in both `Resources\ConfigPresets\_LuaM\deadSpaceLowPop.toml` and `server_config.remote.toml`.
- `dotnet test Content.Tests\Content.Tests.csproj --filter "FullyQualifiedName~LuaM" --no-restore` passed, 1/1.
- `dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --filter "FullyQualifiedName~LuaM" --no-restore` passed, 80/80.
- `dotnet test Content.IntegrationTests\Content.IntegrationTests.csproj --filter "FullyQualifiedName~LuaMBankAndPdaContractsTest" --no-restore` passed, 14/14.
- `git diff --check` over LuaM/PDA/Bank/Tools scope passed. Only LF/CRLF warnings were reported.

Expected existing warnings:

- NU1510 trim-analysis warnings for existing RobustToolbox package references.
- Existing analyzer/obsolete warnings in unrelated test and engine paths.

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

This checks `/status`, `/info`, the public hub entry, ACZ, manifest hash, expected tags, soft max players, panic bunker state, round age, and the 7-day round description. Use `-Strict` only when warnings such as an empty `/info.connect_address` should fail the check. Omit `-SkipSsh` only when read-only SSH journal/service metrics are intentionally needed.

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
