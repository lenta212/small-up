using System;
using Content.Server.Weapons.Ranged.Systems;
using Content.Shared.Ghost;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared._LuaM.AI;
using Robust.Server.Player;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._LuaM.AI;

[RegisterComponent]
public sealed partial class LuaMStationaryTurretBehaviorAdapterComponent : Component
{
    [ViewVariables]
    public TimeSpan NextEvaluation;
}

/// <summary>
/// Adds ammunition awareness and decision-bound targeting to stock stationary
/// turrets while preserving their faction system as the authority on hostility.
/// </summary>
public sealed partial class LuaMStationaryTurretBehaviorAdapterSystem : EntitySystem
{
    private const string ObservationSource = "stationary-turret:domain";
    private static readonly TimeSpan EvaluationInterval = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan IdleEvaluationInterval = TimeSpan.FromSeconds(2);
    private const float IdlePlayerCheckRange = 40f;
    private static readonly TimeSpan ObservationTtl = TimeSpan.FromSeconds(2);

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private GunSystem _guns = default!;
    [Dependency] private LuaMBehaviorSystem _behavior = default!;
    [Dependency] private IPlayerManager _playerManager = default!;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<LuaMStationaryTurretBehaviorAdapterComponent>();
        while (query.MoveNext(out var uid, out var adapter))
        {
            if (HasComp<ActorComponent>(uid) ||
                adapter.NextEvaluation != TimeSpan.Zero && now < adapter.NextEvaluation)
            {
                continue;
            }

            // Monolith performance: a turret that is not currently engaged and
            // has no player nearby only needs a slow re-check. Full behavior
            // evaluation every 0.5s on every idle turret dominated empty-station
            // sim cost, so idle turrets now re-check at a 2s cadence instead.
            if (!IsEngaged(uid) && !HasNearbyPlayer(uid))
            {
                adapter.NextEvaluation = now + IdleEvaluationInterval;
                continue;
            }

            RefreshNow(uid, adapter);
        }
    }

    private bool IsEngaged(EntityUid uid)
    {
        return TryComp<LuaMBehaviorAgentComponent>(uid, out var agent)
            && agent.Decision.Intent is LuaMBehaviorIntent.DefendSelf
                or LuaMBehaviorIntent.DefendArea
                or LuaMBehaviorIntent.ProtectTarget;
    }

    private bool HasNearbyPlayer(EntityUid uid)
    {
        var coords = Transform(uid).Coordinates;
        foreach (var playerData in _playerManager.GetAllPlayerData())
        {
            if (!_playerManager.TryGetSessionById(playerData.UserId, out var session)
                || session.AttachedEntity is not { Valid: true } playerEnt
                || HasComp<GhostComponent>(playerEnt))
            {
                continue;
            }

            var playerCoords = Transform(playerEnt).Coordinates;
            if (coords.TryDistance(EntityManager, playerCoords, out var distance)
                && distance <= IdlePlayerCheckRange)
            {
                return true;
            }
        }

        return false;
    }

    public bool RefreshNow(
        EntityUid uid,
        LuaMStationaryTurretBehaviorAdapterComponent adapter,
        bool force = false)
    {
        if (HasComp<ActorComponent>(uid))
            return false;

        var now = _timing.CurTime;
        if (!force && adapter.NextEvaluation != TimeSpan.Zero && now < adapter.NextEvaluation)
            return true;

        adapter.NextEvaluation = now + EvaluationInterval;
        ConfigureBehavior(uid);
        _behavior.ClearObservations(uid, source: ObservationSource);
        ObserveAmmunition(uid);
        return _behavior.EvaluateNow(uid, out _);
    }

    private void ConfigureBehavior(EntityUid uid)
    {
        var agent = EnsureComp<LuaMBehaviorAgentComponent>(uid);
        if (agent.Profile != "LuaMStationaryTurretBehavior")
        {
            agent.Profile = "LuaMStationaryTurretBehavior";
            agent.NextEvaluation = TimeSpan.Zero;
        }

        agent.BuiltInPerception = true;
        agent.WriteHtnBlackboard = true;

        var binding = EnsureComp<LuaMBehaviorHtnBindingComponent>(uid);
        binding.DefaultTask = "IdleSpinCompound";
        binding.RestoreOriginalTask = false;
        binding.MirrorDecisionTarget = true;
        binding.IntentTasks.Clear();
        binding.IntentTasks[LuaMBehaviorIntent.DefendSelf] = "LuaMBehaviorTurretDecisionCompound";
        binding.IntentTasks[LuaMBehaviorIntent.DefendArea] = "LuaMBehaviorTurretDecisionCompound";
        binding.IntentTasks[LuaMBehaviorIntent.ProtectTarget] = "LuaMBehaviorTurretDecisionCompound";
        binding.IntentTasks[LuaMBehaviorIntent.AwaitRescue] = "IdleSpinCompound";
        binding.IntentTasks[LuaMBehaviorIntent.HoldPosition] = "IdleSpinCompound";
        binding.IntentTasks[LuaMBehaviorIntent.RequestAssistance] = "IdleSpinCompound";
        binding.IntentTasks[LuaMBehaviorIntent.Reload] = "IdleSpinCompound";
    }

    private void ObserveAmmunition(EntityUid uid)
    {
        if (!_guns.TryGetGun(uid, out var gunUid, out _))
        {
            ReportLowAmmunition(uid, 1f);
            return;
        }

        var ammo = new GetAmmoCountEvent();
        RaiseLocalEvent(gunUid, ref ammo);
        var ratio = ammo.Capacity <= 0
            ? 0f
            : Math.Clamp(ammo.Count / (float) ammo.Capacity, 0f, 1f);
        if (ratio >= 0.2f)
            return;

        ReportLowAmmunition(uid, (0.2f - ratio) / 0.2f);
    }

    private void ReportLowAmmunition(EntityUid uid, float severity)
    {
        _behavior.ReportObservation(
            uid,
            LuaMBehaviorStimulus.LowAmmunition,
            severity,
            ttl: ObservationTtl,
            source: ObservationSource);
    }
}
