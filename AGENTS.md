# Repository agent handoff

Before starting work, read `.agents/ITERATION_LOG.md`. It is the persistent handoff journal for this repository.

After every meaningful iteration:

1. Update the current objective and repository state.
2. Record commands that were actually run and their outcome.
3. Mark interrupted or partially completed commands explicitly.
4. Leave one concrete next action with an exact command when possible.
5. Preserve older entries under the history section; do not rewrite successful checks as if they ran in the current iteration.

Never record secrets, access tokens, private keys, passwords, or connection credentials in the journal.

## Production server operations

Before any read or mutation of the production server, read both `.agents/ITERATION_LOG.md` and `Tools/AI_SERVER_JOURNAL.md`. If the host is reachable, also verify that the installed copy at `/opt/monolith-ds/AI_SERVER_JOURNAL.md` matches the repository copy or reconcile newer host facts back into the repository.

After every meaningful server operation, update `Tools/AI_SERVER_JOURNAL.md` with the UTC time, exact commands or bounded operation performed, outcome, current health/storage facts, rollback or recovery notes, and one concrete next action. Install the updated copy on the host at `/opt/monolith-ds/AI_SERVER_JOURNAL.md` with owner `root:root` and mode `0644`.

Never put secrets, tokens, private keys, passwords, raw environment files, private player data, or reusable credentials in either server journal.

## Player-facing server news

After every meaningful player-facing iteration, add a concise Russian entry to `Resources/Changelog/ServerNews.yml` in the same iteration.

1. Use author `LuaM` unless the user specifies another public attribution.
2. Keep IDs unique and monotonically increasing. Use `YYYYMMDDNN` while the value fits a signed 32-bit integer.
3. Use the real completion time in UTC and only describe behavior that was actually implemented.
4. Group related changes into readable `Add`, `Remove`, `Fix`, or `Tweak` lines.
5. Do not put internal credentials, infrastructure addresses, exploit details, unfinished work, or test-only changes in player-facing news.
6. Validate the changelog data and localization before handing off the iteration.
