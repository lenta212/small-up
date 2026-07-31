using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Body.Components;
using Content.Server.Body.Systems;
using Content.Server.NPC.HTN;
using Content.Server.Temperature.Components;
using Content.Server._LuaM.NPC;
using Content.Shared.ActionBlocker;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.Damage;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC.Systems;
using Content.Shared.Power.Components;
using Content.Shared._Shitmed.Body.Components;
using Content.Shared._LuaM.AI;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._LuaM.AI;

/// <summary>
/// Collects common self-preservation facts, runs the behavior arbiter, and
/// publishes its result to role adapters and the standard NPC blackboard.
/// </summary>
public sealed partial class LuaMBehaviorSystem : EntitySystem
{
    public const string IntentKey = "LuaMBehaviorIntent";
    public const string TargetKey = "LuaMBehaviorTarget";
    public const string DestinationKey = "LuaMBehaviorDestination";
    public const string TierKey = "LuaMBehaviorTier";
    public const string ScoreKey = "LuaMBehaviorScore";
    public const string GenerationKey = "LuaMBehaviorGeneration";
    public const string ReasonKey = "LuaMBehaviorReason";

    private const string BuiltInSource = "builtin";
    private static readonly TimeSpan MinimumObservationTtl = TimeSpan.FromMilliseconds(100);

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ActionBlockerSystem _actionBlocker = default!;
    [Dependency] private AtmosphereSystem _atmosphere = default!;
    [Dependency] private HTNSystem _htn = default!;
    [Dependency] private LuaMNpcActivityLifecycleSystem _activity = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private MobThresholdSystem _mobThreshold = default!;
    [Dependency] private NpcFactionSystem _factions = default!;
    [Dependency] private RespiratorSystem _respirator = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<LuaMBehaviorAgentComponent>();
        while (query.MoveNext(out var uid, out var component))
        {
            if (HasComp<ActorComponent>(uid))
                continue;

            if (component.ExternalEvaluationOnly)
                continue;

            if (component.NextEvaluation != TimeSpan.Zero && now < component.NextEvaluation)
                continue;

            Evaluate(uid, component, refreshBuiltInPerception: true);
        }
    }

    public bool ReportObservation(
        EntityUid actor,
        LuaMBehaviorStimulus stimulus,
        float severity,
        float confidence = 1f,
        EntityUid? target = null,
        EntityCoordinates? destination = null,
        TimeSpan? ttl = null,
        string source = "external",
        bool evaluateNow = false)
    {
        if (!TryComp<LuaMBehaviorAgentComponent>(actor, out var component) ||
            stimulus == LuaMBehaviorStimulus.None ||
            !float.IsFinite(severity) ||
            !float.IsFinite(confidence))
        {
            return false;
        }

        UpsertObservation(
            component,
            stimulus,
            severity,
            confidence,
            target,
            destination,
            ttl ?? TimeSpan.FromSeconds(2),
            source);

        if (evaluateNow)
            Evaluate(actor, component, refreshBuiltInPerception: false);
        return true;
    }

    public int ClearObservations(
        EntityUid actor,
        LuaMBehaviorStimulus? stimulus = null,
        EntityUid? target = null,
        string? source = null)
    {
        if (!TryComp<LuaMBehaviorAgentComponent>(actor, out var component))
            return 0;

        return component.Observations.RemoveAll(observation =>
            (stimulus == null || observation.Stimulus == stimulus) &&
            (target == null || observation.Target == target) &&
            (source == null || string.Equals(observation.Source, source, StringComparison.Ordinal)));
    }

    public bool EvaluateNow(EntityUid actor, out LuaMBehaviorDecision decision)
    {
        if (!TryComp<LuaMBehaviorAgentComponent>(actor, out var component))
        {
            decision = new LuaMBehaviorDecision();
            return false;
        }

        return Evaluate(actor, component, refreshBuiltInPerception: true, out decision);
    }

    public bool GetDecision(EntityUid actor, out LuaMBehaviorDecision decision)
    {
        if (!TryComp<LuaMBehaviorAgentComponent>(actor, out var component))
        {
            decision = new LuaMBehaviorDecision();
            return false;
        }

        decision = component.Decision;
        return true;
    }

    private void Evaluate(
        EntityUid actor,
        LuaMBehaviorAgentComponent component,
        bool refreshBuiltInPerception)
    {
        Evaluate(actor, component, refreshBuiltInPerception, out _);
    }

    private bool Evaluate(
        EntityUid actor,
        LuaMBehaviorAgentComponent component,
        bool refreshBuiltInPerception,
        out LuaMBehaviorDecision decision)
    {
        if (HasComp<ActorComponent>(actor))
        {
            component.LastStatus = "player controlled; behavior evaluation suspended";
            decision = component.Decision;
            return false;
        }

        var now = _timing.CurTime;
        if (!_prototypes.TryIndex(component.Profile, out LuaMBehaviorProfilePrototype? profile))
        {
            component.LastStatus = $"missing behavior profile {component.Profile}";
            component.NextEvaluation = now + TimeSpan.FromSeconds(1);
            decision = component.Decision;
            return false;
        }

        if (refreshBuiltInPerception && component.BuiltInPerception)
            RefreshBuiltInPerception(actor, component, profile);

        PruneObservations(component, profile, now);
        var previous = component.Decision;
        decision = LuaMBehaviorArbiter.Decide(profile, component.Observations, previous, now);
        component.Decision = decision;
        component.NextEvaluation = now + TimeSpan.FromSeconds(
            Math.Clamp(profile.DecisionIntervalSeconds, 0.1f, 10f));
        component.LastStatus =
            $"{decision.Intent}/{decision.Tier} g{decision.Generation}: {decision.Reason}";

        var changed = decision.Generation != previous.Generation;
        ApplyDecision(actor, component, decision, changed);
        if (changed)
        {
            var ev = new LuaMBehaviorDecisionChangedEvent(actor, previous, decision);
            RaiseLocalEvent(actor, ref ev);
        }

        return true;
    }

    private void ApplyDecision(
        EntityUid actor,
        LuaMBehaviorAgentComponent component,
        LuaMBehaviorDecision decision,
        bool changed)
    {
        if (TryComp<HTNComponent>(actor, out var htn))
        {
            if (component.WriteHtnBlackboard)
                WriteBlackboard(htn, decision);

            if (TryComp<LuaMBehaviorHtnBindingComponent>(actor, out var binding))
            {
                CaptureOriginalTask(htn, binding);
                if (binding.MirrorDecisionTarget)
                    MirrorDecisionTarget(htn, decision);

                if (changed)
                    ApplyHtnBinding(htn, binding, decision);
            }
            else if (changed && component.ReplanHtnOnDecision)
                _htn.Replan(htn);
        }

        if (!component.DriveActivityLifecycle || string.IsNullOrWhiteSpace(decision.Activity))
            return;

        if (!_activity.BeginOrReplaceIntent(
                actor,
                decision.Activity,
                decision.Target,
                decision.Destination,
                out _,
                out var rejection))
        {
            component.LastStatus += $"; lifecycle rejected={rejection}";
        }
    }

    private void ApplyHtnBinding(
        HTNComponent htn,
        LuaMBehaviorHtnBindingComponent binding,
        LuaMBehaviorDecision decision)
    {
        var task = binding.IntentTasks.GetValueOrDefault(decision.Intent);
        if (string.IsNullOrWhiteSpace(task))
            task = binding.DefaultTask;
        if (string.IsNullOrWhiteSpace(task) && binding.RestoreOriginalTask)
            task = binding.OriginalTask;

        if (string.IsNullOrWhiteSpace(task) || string.Equals(htn.RootTask.Task, task, StringComparison.Ordinal))
            return;

        _htn.ShutdownPlan(htn);
        htn.PlanningToken?.Cancel();
        htn.PlanningJob = null;
        htn.RootTask = new HTNCompoundTask { Task = task };
        _htn.Replan(htn);
    }

    private static void CaptureOriginalTask(
        HTNComponent htn,
        LuaMBehaviorHtnBindingComponent binding)
    {
        if (string.IsNullOrWhiteSpace(binding.OriginalTask))
            binding.OriginalTask = htn.RootTask.Task;
    }

    private void MirrorDecisionTarget(HTNComponent htn, LuaMBehaviorDecision decision)
    {
        var blackboard = htn.Blackboard;
        if (decision.Target is { } target && !TerminatingOrDeleted(target))
            blackboard.SetValue("Target", target);
        else
            blackboard.Remove<EntityUid>("Target");

        var coordinates = decision.Destination;
        if (coordinates == null &&
            decision.Target is { } coordinateTarget &&
            !TerminatingOrDeleted(coordinateTarget))
        {
            coordinates = Transform(coordinateTarget).Coordinates;
        }

        if (coordinates is { } targetCoordinates)
            blackboard.SetValue("TargetCoordinates", targetCoordinates);
        else
            blackboard.Remove<EntityCoordinates>("TargetCoordinates");
    }

    private static void WriteBlackboard(HTNComponent htn, LuaMBehaviorDecision decision)
    {
        var blackboard = htn.Blackboard;
        blackboard.SetValue(IntentKey, decision.Intent.ToString());
        blackboard.SetValue(TierKey, decision.Tier.ToString());
        blackboard.SetValue(ScoreKey, decision.Score);
        blackboard.SetValue(GenerationKey, decision.Generation);
        blackboard.SetValue(ReasonKey, decision.Reason);

        if (decision.Target is { } target)
            blackboard.SetValue(TargetKey, target);
        else
            blackboard.Remove<EntityUid>(TargetKey);

        if (decision.Destination is { } destination)
            blackboard.SetValue(DestinationKey, destination);
        else
            blackboard.Remove<EntityCoordinates>(DestinationKey);
    }

    private void RefreshBuiltInPerception(
        EntityUid actor,
        LuaMBehaviorAgentComponent component,
        LuaMBehaviorProfilePrototype profile)
    {
        var ttl = TimeSpan.FromSeconds(Math.Clamp(profile.DecisionIntervalSeconds * 3f, 1f, 10f));

        if (_mobState.IsIncapacitated(actor))
        {
            UpsertObservation(component, LuaMBehaviorStimulus.SelfIncapacitated, 1f, 1f, null, null, ttl,
                $"{BuiltInSource}:self");
        }
        else if (_mobState.IsCritical(actor))
        {
            UpsertObservation(component, LuaMBehaviorStimulus.SelfCritical, 1f, 1f, null, null, ttl,
                $"{BuiltInSource}:self");
        }

        if (TryComp<DamageableComponent>(actor, out var damage) &&
            _mobThreshold.TryGetIncapPercentage(actor, damage.TotalDamage, out var damageSeverity) &&
            damageSeverity > 0.02)
        {
            UpsertObservation(component, LuaMBehaviorStimulus.SelfDamaged, (float) damageSeverity, 1f, null, null, ttl,
                $"{BuiltInSource}:damage");
        }

        if (TryComp<FlammableComponent>(actor, out var flammable) && flammable.OnFire)
        {
            UpsertObservation(component, LuaMBehaviorStimulus.SelfOnFire, 1f, 1f, actor, null, ttl,
                $"{BuiltInSource}:fire");
        }

        if (profile.HasCapability(LuaMBehaviorCapability.Move) && !_actionBlocker.CanMove(actor))
        {
            UpsertObservation(component, LuaMBehaviorStimulus.Immobilized, 1f, 1f, null, null, ttl,
                $"{BuiltInSource}:movement");
        }

        RefreshAtmospherePerception(actor, component, profile, ttl);
        RefreshThreatPerception(actor, component, profile, ttl);
        RefreshPowerPerception(actor, component, ttl);
    }

    private void RefreshAtmospherePerception(
        EntityUid actor,
        LuaMBehaviorAgentComponent component,
        LuaMBehaviorProfilePrototype profile,
        TimeSpan ttl)
    {
        var mixture = _atmosphere.GetContainingMixture(actor);
        if (mixture == null)
            return;

        if (profile.HasCapability(LuaMBehaviorCapability.Breathe) &&
            !HasComp<BreathingImmunityComponent>(actor) &&
            TryComp<RespiratorComponent>(actor, out var respirator) &&
            !_respirator.CanMetabolizeInhaledAir((actor, respirator)))
        {
            UpsertObservation(component, LuaMBehaviorStimulus.UnsafeAtmosphere, 1f, 1f, null, null, ttl,
                $"{BuiltInSource}:atmosphere");
        }

        if (profile.HasCapability(LuaMBehaviorCapability.PressureVulnerable) &&
            !HasComp<PressureImmunityComponent>(actor))
        {
            if (mixture.Pressure < Atmospherics.WarningLowPressure)
            {
                var severity = 1f - mixture.Pressure / Atmospherics.WarningLowPressure;
                UpsertObservation(component, LuaMBehaviorStimulus.LowPressure, severity, 1f, null, null, ttl,
                    $"{BuiltInSource}:pressure");
            }
            else if (mixture.Pressure > Atmospherics.WarningHighPressure)
            {
                var span = Math.Max(1f, Atmospherics.HazardHighPressure - Atmospherics.WarningHighPressure);
                var severity = (mixture.Pressure - Atmospherics.WarningHighPressure) / span;
                UpsertObservation(component, LuaMBehaviorStimulus.HighPressure, severity, 1f, null, null, ttl,
                    $"{BuiltInSource}:pressure");
            }
        }

        if (!profile.HasCapability(LuaMBehaviorCapability.TemperatureVulnerable) ||
            !TryComp<TemperatureComponent>(actor, out var temperature))
            return;

        var safeMinimum = temperature.ParentColdDamageThreshold ?? temperature.ColdDamageThreshold;
        var safeMaximum = temperature.ParentHeatDamageThreshold ?? temperature.HeatDamageThreshold;
        if (mixture.Temperature < safeMinimum)
        {
            UpsertObservation(component, LuaMBehaviorStimulus.ExtremeTemperature,
                (safeMinimum - mixture.Temperature) / 100f, 1f, null, null, ttl,
                $"{BuiltInSource}:temperature");
        }
        else if (mixture.Temperature > safeMaximum)
        {
            UpsertObservation(component, LuaMBehaviorStimulus.ExtremeTemperature,
                (mixture.Temperature - safeMaximum) / 300f, 1f, null, null, ttl,
                $"{BuiltInSource}:temperature");
        }
    }

    private void RefreshThreatPerception(
        EntityUid actor,
        LuaMBehaviorAgentComponent component,
        LuaMBehaviorProfilePrototype profile,
        TimeSpan ttl)
    {
        if (profile.PerceptionRange <= 0f)
            return;

        var count = 0;
        var nearestDistance = float.MaxValue;
        EntityUid? nearest = null;
        var actorCoordinates = Transform(actor).Coordinates;
        foreach (var hostile in _factions.GetNearbyHostiles(actor, profile.PerceptionRange))
        {
            if (TerminatingOrDeleted(hostile))
                continue;

            count++;
            if (Transform(hostile).Coordinates.TryDistance(
                    EntityManager,
                    _transform,
                    actorCoordinates,
                    out var distance) &&
                distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = hostile;
            }

            if (count >= 32)
                break;
        }

        if (count == 0)
            return;

        var countSeverity = count / Math.Max(1f, profile.ThreatTolerance);
        var proximitySeverity = nearestDistance == float.MaxValue
            ? 0f
            : 1f - Math.Clamp(nearestDistance / Math.Max(0.1f, profile.PerceptionRange), 0f, 1f);
        var severity = Math.Max(countSeverity, proximitySeverity);
        UpsertObservation(component, LuaMBehaviorStimulus.HostileThreat, severity, 1f, nearest, null, ttl,
            $"{BuiltInSource}:threat");

        if (count > Math.Max(1f, profile.ThreatTolerance))
        {
            UpsertObservation(component, LuaMBehaviorStimulus.Overwhelmed,
                count / Math.Max(1f, profile.ThreatTolerance * 2f), 1f, nearest, null, ttl,
                $"{BuiltInSource}:threat");
        }
    }

    private void RefreshPowerPerception(
        EntityUid actor,
        LuaMBehaviorAgentComponent component,
        TimeSpan ttl)
    {
        if (!TryComp<BatteryComponent>(actor, out var battery) || battery.MaxCharge <= 0f)
            return;

        var ratio = Math.Clamp(battery.CurrentCharge / battery.MaxCharge, 0f, 1f);
        if (ratio >= 0.3f)
            return;

        UpsertObservation(component, LuaMBehaviorStimulus.LowPower,
            (0.3f - ratio) / 0.3f, 1f, null, null, ttl, $"{BuiltInSource}:power");
    }

    private void UpsertObservation(
        LuaMBehaviorAgentComponent component,
        LuaMBehaviorStimulus stimulus,
        float severity,
        float confidence,
        EntityUid? target,
        EntityCoordinates? destination,
        TimeSpan ttl,
        string source)
    {
        var now = _timing.CurTime;
        ttl = ttl < MinimumObservationTtl ? MinimumObservationTtl : ttl;
        source = string.IsNullOrWhiteSpace(source) ? "external" : source.Trim();
        var existing = component.Observations.Find(observation =>
            observation.Stimulus == stimulus &&
            observation.Target == target &&
            string.Equals(observation.Source, source, StringComparison.Ordinal));

        if (existing == null)
        {
            existing = new LuaMBehaviorObservation();
            component.Observations.Add(existing);
        }

        existing.Stimulus = stimulus;
        existing.Severity = Math.Clamp(severity, 0f, 1f);
        existing.Confidence = Math.Clamp(confidence, 0f, 1f);
        existing.Target = target;
        existing.Destination = destination;
        existing.ObservedAt = now;
        existing.ExpiresAt = AddDuration(now, ttl);
        existing.Source = source;
    }

    private void PruneObservations(
        LuaMBehaviorAgentComponent component,
        LuaMBehaviorProfilePrototype profile,
        TimeSpan now)
    {
        component.Observations.RemoveAll(observation =>
            observation.Stimulus == LuaMBehaviorStimulus.None ||
            observation.ExpiresAt != TimeSpan.Zero && observation.ExpiresAt <= now ||
            observation.Target is { } target && TerminatingOrDeleted(target));

        var limit = Math.Clamp(profile.MaxObservations, 8, 256);
        if (component.Observations.Count <= limit)
            return;

        component.Observations.Sort((left, right) =>
        {
            var strength = (right.Severity * right.Confidence).CompareTo(left.Severity * left.Confidence);
            return strength != 0 ? strength : right.ObservedAt.CompareTo(left.ObservedAt);
        });
        component.Observations.RemoveRange(limit, component.Observations.Count - limit);
    }

    private static TimeSpan AddDuration(TimeSpan now, TimeSpan duration)
    {
        var ticks = Math.Min(
            TimeSpan.MaxValue.Ticks - Math.Max(0L, now.Ticks),
            Math.Max(0L, duration.Ticks));
        return TimeSpan.FromTicks(Math.Max(0L, now.Ticks) + ticks);
    }
}
