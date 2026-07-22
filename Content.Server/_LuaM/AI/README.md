# LuaM common bot behavior model

This directory owns high-level decision arbitration shared by humanoids,
service machines, drones, ships, turrets, and animals. It does not replace the
standard SS14 HTN executor or the Mono ship steering and targeting systems.

## Decision pipeline

1. A perception adapter reports short-lived observations. Observations describe
   facts, never commands.
2. `LuaMBehaviorArbiter` filters rules by profile capabilities and current
   observations.
3. The highest priority candidate becomes the active intent.
4. A domain adapter executes that intent through HTN, the generic activity
   lifecycle, or a bounded entity system.
5. Executors report completion, blockage, target loss, or resource changes as
   new observations. They do not silently choose a different mission.

The score inside one tier is:

```text
tier * 100000 + base score + sum(clamp(severity) * clamp(confidence) * weight)
```

A higher tier always wins. Equal scores are ordered by rule ID and then entity
ID, so the same state produces the same decision. The previous target receives
stickiness, and same-tier challengers must cross the profile preemption margin.
An absent or invalid previous target is never kept merely for stickiness.

## Priority contract

| Tier | Meaning | Typical decisions |
| --- | --- | --- |
| HardStop | The actor cannot execute safely | await rescue |
| Critical | Immediate loss of actor or area | extinguish self, evacuate, seek air |
| Safety | Survival maneuver is required | retreat, take cover, disengage ship |
| Combat | An actionable hostile remains | defend, guard, engage ship |
| Support | A person or shared system needs urgent help | revive, treat, seal breach |
| Mission | Assigned work | follow order, mine, deliver, escort |
| Routine | Maintenance and needs | recharge, reload, patrol |
| Idle | No actionable observation | standby |

An order never overrides fire, unsafe atmosphere, incapacitation, or a required
retreat. Support does not override an active combat threat unless a role profile
adds an explicit higher-tier rule.

## Situation matrix

| Actor category | Situation | Required high-level response |
| --- | --- | --- |
| Any actor | Incapacitated | stop all work and await rescue |
| Mobile organic | On fire | self-extinguish if capable, otherwise flee |
| Mobile organic | Unsafe pressure or breathable gas | seek safe atmosphere |
| Mobile actor | Explosion, electrical, contamination, or local breach risk | evacuate hazard |
| Unarmed worker | Hostile present | flee; if immobilized, request assistance |
| Armed humanoid | Hostile present | defend self; retreat when overwhelmed |
| Ranged-only combatant | Hostile and ammunition critically low | take cover |
| Engineer | Fire, breach, power loss, or structural damage | fire, breach, power, then repair priority |
| Medic | Dead, critical, and injured allies | revive, critical treatment, then routine treatment |
| Rescue crew | Threat around patient | survive combat, secure scene, then resume patient work |
| Service robot | Low charge | recharge; critical charge preempts support work |
| Mining drone | Cargo full | return cargo before selecting another deposit |
| Mining drone | Route lost | replan; after bounded failures release the work lease |
| Combat drone | Hostile present | defend; take cover when ranged-only and out of ammunition |
| Civilian ship | Hostile contact | disengage and preserve distance |
| Combat ship | Viable hostile contact | engage and orbit |
| Combat ship | Low power, fuel, ammunition, or overwhelming force | disengage |
| Stationary turret | Hostile contact | defend assigned area; never select movement intents |
| Animal | Threat present | defend when viable, retreat when overwhelmed |
| Any worker | Direct order plus safety hazard | resolve the safety hazard first, then resume order |

## Coordination contract

`LuaMWorkOrderSystem` owns shared jobs. A job declares its required
capabilities, urgency, target, lifetime, and assignee limit. Claim leases expire
when an agent disappears or stalls. A failed agent receives exponential bounded
backoff for that exact order, allowing another capable agent to take it. Deleted
targets cancel their open work. Agents may not invent capabilities to claim a
job.

The behavior decision is only the current intent. Durable mission state,
attempt counts, inventory reservations, and completion evidence stay in the
work-order or domain executor. This prevents a perception refresh from resetting
failure budgets.

## Executor ownership

| Domain | Executor | Integration state |
| --- | --- | --- |
| Generic humanoid HTN | `Content.Server/NPC/HTN` | blackboard/root-task bridge, bounded retreat, original-task restore, and hostile humanoid rollout implemented |
| Mono ship movement and guns | `_Mono/NPC/HTN` | adaptive combat, navigation, orbit/fire, and disengage plans implemented |
| Generic activity lifecycle | `_LuaM/NPC` | optional intent bridge implemented |
| Rescue agents and escorts | `_LuaM/Rescue` | patient, route, threat, activity, and escort-duty adapter implemented |
| Sector mining and service drones | `_LuaM/Sector` | task perception, role profiles, route detour, and return/evasion bridge implemented |
| Sector logistics ships | `_LuaM/Sector` | civilian behavior core, delivery navigation, route recovery, and threat disengage implemented |
| Existing service robots | standard bot HTN | firebot, cleanbot, medibot, and generic service-bot adapters implemented |
| Existing turrets | gun/turret systems | standard and Frontier target/ammunition adapter rollout implemented |
| General animals | standard NPC HTN | atmospheric and space-animal defend/retreat rollout implemented |
| General engineer and medic workers | standard humanoid HTN plus held-item interaction | repair/treatment claims, navigation, real tool and medicine `DoAfter`, safety cancellation, completion evidence, and bounded failure release implemented |
| Taxi, supply, and mime bots | role-specific systems | role-filtered work-order navigation, arrival completion, lease recovery, and original HTN restoration implemented; physical passenger and cargo transfer remain domain-owned follow-up work |

Every broad prototype rollout requires a runtime test proving both intent
selection and an executor state change. Entities with `ActorComponent` are
excluded in the periodic loop, adapters, and the common direct-evaluation path;
manual player control always retains executor authority.

## Local model boundary

A local language model may translate dialogue or an operator request into a
validated structured work order. It may not apply thrust, fire weapons, invoke
arbitrary entity systems, create network events, or bypass capability, faction,
lease, and safety checks. Deterministic server systems remain authoritative.

## Adapter acceptance checks

Each category is complete only when tests prove:

1. Critical and safety observations preempt routine work.
2. The role cannot select an intent requiring a missing capability.
3. Multiple targets are scored deterministically without rapid oscillation.
4. A decision changes a real executor plan or state.
5. Completion and failure return to the work board with bounded retries.
6. Deleted targets, lost paths, low resources, and manual player control recover
   without duplicate ownership or an infinite retry loop.
