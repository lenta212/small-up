# Ship persistence development requirements

These requirements are mandatory before any further production work that can
restart, stop, redeploy, or otherwise interrupt the live game service.

## Current implementation — 2026-07-22

The production deploy workflow now calls a narrow authenticated maintenance API
before stopping the service. It rejects active sessions, freezes new persistent
ship lifecycle work, waits for already-started work, saves all active ships, and
returns a bounded aggregate receipt. The deploy validates a fresh success
receipt with `attempted == saved`, `failed == 0`, `activeRemaining == 0`, and
`frozen == true`; `-Force` does not bypass this check. Save conflicts keep the
affected ship active and leave the service running.

For the one-time rollout of the endpoint itself, the legacy bootstrap fails
closed unless the endpoint is absent, online is zero, and read-only database
inspection proves both Active/Restoring rows and presence leases are zero (or
the complete ship-persistence schema is not installed yet). Direct manual
service stops remain unauthorized because they bypass these checks.

## Incident driver

During a live round, a player-built `Pathfinder` was active for several hours
before an unapproved service restart. The persistent registry still contained a
snapshot row for the ship, but this incident exposed a critical product gap: a
ship that players spend hours building or modifying must not depend on an
operator remembering to take a manual snapshot before a restart.

## Required behavior

1. **No live restart without a ship-safe path.** Any production script or manual
   runbook that stops/restarts `monolith-ds.service` must first either:
   - prove there are zero active player ships, or
   - trigger and verify a full active-ship save, or
   - create a checked full data backup and explicitly warn that live grid state
     may be lost.

2. **Automatic active-ship snapshot on shutdown.** The server must save every
   active/restoring persistent ship during graceful shutdown, round restart, and
   deploy stop paths. Failure must be logged with ship id, owner-safe alias, ship
   name/prototype, and reason, without writing private player data to public
   logs.

3. **Crash/restart recovery.** On startup, expired active leases from a previous
   server instance/round must become callable stored ships when the durable
   payload is valid. They must not remain stuck as active/restoring with an
   expired lease.

4. **Player modifications are authoritative.** The stored payload must include
   the current modified grid state, not just the purchased prototype. This must
   cover loose/nested items, machines, atmos/power-relevant state, and ship lock
   identity/remapping.

5. **Admin-visible safety check.** Add a read-only admin command/tool that lists
   aggregate active ship persistence state before maintenance: counts by status,
   active leases with expiry, and whether each active ship has a recent verified
   payload. It must not dump raw payloads or private user identifiers.

6. **Manual emergency save command.** Add an explicit admin command/tool to force
   save all active player ships and report per-ship success/failure before a
   planned maintenance action.

7. **Production operation guardrails.** Deployment/restart scripts must require
   a successful ship-safety preflight. The guarded deploy now refuses active
   sessions and requires a successful active-ship save even when `-Force` is
   supplied; it also requires the configured checked data backup before the
   server swap. Other restart scripts must adopt the same contract before use.

## Required tests

Add targeted integration/regression coverage for:

- active ship with structural modifications is saved on graceful shutdown and
  callable after restart;
- active ship lease expires across restart and recovers to a callable stored
  state;
- failed placement/abort/retry does not corrupt the payload revision or CAS
  lifecycle revision;
- restored ship lock identity is stable or remapped so consoles/deeds keep
  working;
- owner-bound shipyard UI/deed recovery does not expose another player's stored
  ship;
- production deploy/restart preflight refuses to stop the service when active
  ships exist unless the backup/save/force requirements are satisfied.

## Remaining priority

Route every remaining manual/config/restart entry point through the same
maintenance API, and add the bounded per-ship operator diagnostic described
above. Until then, use only the guarded release script for production stops.
