# Ship persistence required work

This file records non-optional development requirements discovered from the 2026-07-21 live-server restart incident.

## Implementation status — 2026-07-22

The guarded release path now has the required fail-closed maintenance barrier:

- token-authenticated `POST /admin/actions/maintenance/ship-save` rejects every connected session before changing persistence state;
- the orchestrator freezes new ship lifecycle mutations, drains work already in flight, saves every active ship, and returns a privacy-safe aggregate receipt;
- any failed save leaves that ship active, returns a negative receipt, and leaves the service running;
- `deploy_luam_server_release.ps1` validates a fresh complete receipt before `systemctl stop`; ordinary `-Force` cannot bypass it;
- the first rollout may use the explicitly named legacy bootstrap only when the endpoint is absent, online is zero, and the database proves there are no Active/Restoring rows or presence leases.

Runtime coverage proves success, revision-conflict retention, lifecycle freeze, concurrent-request rejection, active-session rejection, stable restored ship identity, full modified-grid snapshots, and expired-lease recovery. Direct manual `systemctl stop/restart` remains prohibited because it bypasses this guard. Per-ship operator diagnostics and any non-deploy shutdown entry points remain follow-up work.

## Incident driver

A live service restart was performed while player ships could be active in the round. Persistent registry rows still existed afterward, but an actively used/modified ship can lose recent in-world construction if the server restarts before a durable full-ship snapshot is completed. A reported case involved a `Pathfinder` that had been worked on for hours.

## Mandatory requirements before future production restarts

1. **No direct restart path for AI/operator tooling.** Production restart must go through a guarded script that:
   - checks current player count and run level;
   - requires explicit human confirmation when players are online or a round is running;
   - records a fresh data backup location before mutation;
   - writes the exact rollback path before restarting.

2. **Pre-shutdown full-ship save barrier.** Before any controlled stop/restart/update, the server must attempt a synchronous save of every active LuaM player ship and wait for completion. The guarded deploy path now blocks on any failed snapshot; other stop/restart paths must be routed through the same barrier before they are authorized.

3. **Admin/config changes must not imply restart.** Toggling LuaM AI bridge, chat, or director options must be staged as config-only unless an explicit restart approval is given. If a restart is required, the UI/tooling must show the ship-save and player-count risk first.

4. **Snapshot freshness must be visible.** Shipyard/admin diagnostics must show for each non-retired ship:
   - ship name/prototype;
   - owner-safe display identity;
   - status: Stored/Restoring/Active/Quarantined/Retired;
   - payload revision and lifecycle revision;
   - last durable snapshot time;
   - whether the ship is currently active in-world and leased.

5. **Crash/restart recovery must reconcile active leases.** On startup, expired active leases from the previous round must be safely converted to callable stored snapshots when the payload is valid. A ship that was active before an unplanned stop must not remain stuck in an uncallable state.

6. **Player-build protection test coverage.** Add integration/runtime coverage for this exact scenario:
   - buy/call a ship;
   - mutate/build on it for multiple entity changes;
   - trigger controlled restart/round cleanup;
   - verify the latest construction state is saved and callable after restart;
   - include a `Pathfinder`-class or equivalent small purchased ship fixture.

7. **Operational audit command.** Provide a read-only command that summarizes active/stored ship safety before restart without dumping private payloads or player data.

## Acceptance criteria

- A production restart with active ships either saves all active ships first or refuses to proceed.
- A failed save names the affected ship row/prototype/status in operator-safe terms and leaves the server running.
- After restart, every successfully saved active ship is available from the shipyard/deed recovery path.
- The guarded restart path is documented in `Tools/AI_SERVER_JOURNAL.md` before deployment.
