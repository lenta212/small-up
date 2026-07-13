# Monolith Server Improvement Audit

Date: 2026-07-13

Scope: LuaM/Monolith server release preparation, live preflight, package safety, and dependency/security gates. Remote deploys, remote restarts, and remote config writes remain frozen while `Tools\luam_release_policy.json` has `remoteDeployFrozen=true`.

## External references checked

- SS14 server hosting tutorial: https://docs.spacestation14.com/en/general-development/setup/server-hosting-tutorial.html
- SS14 hub rules reference from server config: https://docs.spacestation14.io/hosts/hub-rules
- Robust status/info HTTP API: https://docs.spacestation14.com/en/robust-toolbox/server-http-api.html
- NuGet package auditing: https://learn.microsoft.com/en-us/nuget/concepts/auditing-packages
- .NET package listing CLI: https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-package-list

## Implemented in this pass

- Added `Tools\audit_luam_dependency_vulnerabilities.ps1`.
- Wired that audit into LuaM readiness and the publish/test-packaging workflows.
- The gate now checks 5 release-critical projects instead of only `Content.Server.Database`:
  - `Content.Server.Database`
  - `Content.Server`
  - `Content.Client`
  - `Content.IntegrationTests`
  - `Content.Packaging`

## Current evidence

- `Tools\check_luam_release_ready.ps1 -Json` reports `ok=true`.
- `round-length-seven-days` reports `shuttle.auto_call_time = 10080` in the LuaM preset and remote config.
- `dependency-vulnerability-audit` reports no vulnerable packages across 5 release-critical projects.
- Full solution audit still reports unrelated high-severity findings in `Pow3r` through old transitive `Microsoft.NETCore.App` and `Microsoft.NETCore.Jit` packages. `Pow3r` is not part of the server release path, but it should be cleaned up separately.

## Recommended next improvements

Completed since the original audit:

- The live connect address was applied and post-deploy verification now checks it.
- The confirmed production paths are `/opt/monolith-ds/server/server_config.toml` and `/opt/monolith-ds/data`.
- The 2026-07-12 deployment used config and data backups and passed post-deploy verification; remote deployment is frozen again.
- The local character-persistence slice now frees archived slots, assigns a new `Profile.Id` to normal replacements, and restores old identities only through an explicit server procedure.

Priority 0, before packaging the current development slice:

- Keep expedition sources/tests, profile database code and migrations, design documents, and changelog fragments inside the release/readiness/package verification scope.
- Run the character persistence, progression rules, expedition planner, and character TTS validation tests together before accepting the slice.
- Track every release-scope file, then run full release readiness with `-RunTests -RunLocalSmoke` immediately before packaging final deploy artifacts.

Priority 1, after the frozen release is safe:

- Resolve the `Pow3r` vulnerable transitive packages or remove `Pow3r` from the main solution if it is dead tooling.
- Add a restore drill for deploy backups: verify that the generated data tarball can be listed and extracted into a temporary directory.
- Add a scheduled read-only preflight job outside deploy flow to watch `/status`, `/info`, hub presence, tags, ACZ, manifest hash, and round age.

Priority 2, operational quality:

- Review ServerInfo/rules/contact text against hub expectations so players and hub maintainers have a clear support/contact path.
- Add an explicit post-release checklist for checking the launcher listing, hub tags, player cap, panic bunker state, and current round age.
- Keep package verification warnings actionable: final source packages should be produced with `-RunTests -RunLocalSmoke`, not quick readiness only.
