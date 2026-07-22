using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Content.Server._LuaM.Sector;
using Content.Server._NF.Shipyard.Systems;
using Content.Server.Administration;
using Content.Server.Bed.Components;
using Content.Server.Buckle.Systems;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Server.Radio.EntitySystems;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Station.Systems;
using Content.Shared._Mono.CorticalBorer;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared.Administration;
using Content.Shared.Buckle.Components;
using Content.Shared.Damage;
using Content.Shared.Humanoid;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Radio;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Content.Shared.Station.Components;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Containers;
using Robust.Shared.Console;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Physics;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._LuaM.Rescue;

public sealed class LuaMRescueShuttleSystem : EntitySystem
{
    // Dispatch/lifecycle state is server-only and exposed through snapshots/events.
    private static void Dirty(EntityUid _, LuaMRescueAgentComponent __) { }
    private static void Dirty(EntityUid _, LuaMRescueShuttleLifecycleComponent __) { }

    public const string DefaultVessel = "Triage";
    private const double AutomaticDeathSignalCooldownSeconds = 180;
    private const double AutomaticCriticalSignalCooldownSeconds = 300;
    private const int DispatchQueueMaxAttempts = 3;
    private static readonly TimeSpan DispatchQueueRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DispatchQueueDeadline = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LifecycleUpdateInterval = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan DispatchQueueUpdateInterval = TimeSpan.FromSeconds(1);
    private static readonly ProtoId<RadioChannelPrototype> MedicalRadioChannel = "Medical";

    [Dependency] private readonly ShipyardSystem _shipyard = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly MetaDataSystem _metaData = default!;
    [Dependency] private readonly BuckleSystem _buckle = default!;
    [Dependency] private readonly LuaMRescueAgentSystem _rescueAgent = default!;
    [Dependency] private readonly LuaMRescueNavigationSystem _rescueNavigation = default!;
    [Dependency] private readonly LuaMRescueActivityCoordinatorSystem _activity = default!;
    [Dependency] private readonly LuaMRescueTeamSystem _rescueTeam = default!;
    [Dependency] private readonly LuaMSectorStorySystem _sectorStory = default!;
    [Dependency] private readonly NPCSystem _npc = default!;
    [Dependency] private readonly RadioSystem _radio = default!;
    [Dependency] private readonly DockingSystem _docking = default!;
    [Dependency] private readonly ShuttleSystem _shuttle = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;

    private readonly Dictionary<EntityUid, TimeSpan> _automaticDeathSignalCooldowns = new();
    private readonly Dictionary<EntityUid, TimeSpan> _automaticCriticalSignalCooldowns = new();
    private readonly Queue<(EntityUid Target, ulong Generation)> _pendingDispatchOrder = new();
    private readonly Dictionary<EntityUid, PendingMedicalDispatch> _pendingDispatches = new();
    private readonly Dictionary<EntityUid, PendingMedicalDispatch> _terminalDispatches = new();
    private readonly Dictionary<EntityUid, LuaMRescueDormantRouteTransfer> _dormantRouteTransfers = new();
    private readonly Dictionary<EntityUid, OnboardOwnershipTransfer> _onboardOwnershipTransfers = new();
    private readonly Dictionary<EntityUid, PatientRecoveryTransfer> _patientRecoveryTransfers = new();
    private ulong _nextPendingDispatchGeneration;
    private TimeSpan _nextLifecycleUpdate;
    private TimeSpan _nextDispatchQueueUpdate;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<MobStateChangedEvent>(OnMobStateChanged);
        SubscribeLocalEvent<MobStateComponent, ComponentStartup>(OnMedicalTargetStarted);
        SubscribeLocalEvent<MobStateComponent, ComponentShutdown>(OnMedicalTargetShutdown);
        SubscribeLocalEvent<MobStateComponent, EntityTerminatingEvent>(OnMedicalTargetTerminating);
        SubscribeLocalEvent<LuaMRescueShuttleLifecycleComponent, LuaMRescueShuttleRouteRequestEvent>(OnRouteRequest);
        SubscribeLocalEvent<LuaMRescueAgentComponent, LuaMRescueManualOverrideRevokedEvent>(OnManualOverrideRevoked);
        SubscribeLocalEvent<LuaMRescueAgentComponent, LuaMRescueExplicitPatientOrderAcceptedEvent>(OnExplicitPatientOrderAccepted);
        SubscribeLocalEvent<LuaMRescueAgentComponent, LuaMRescueAgentRetiringEvent>(OnRescueAgentRetiring);
        SubscribeLocalEvent<LuaMRescueAgentComponent, EntityTerminatingEvent>(OnRescueAgentTerminating);
        SubscribeLocalEvent<LuaMRescueAgentComponent, ComponentShutdown>(OnRescueAgentShutdown);
    }

    private void OnRescueAgentRetiring(
        Entity<LuaMRescueAgentComponent> agent,
        ref LuaMRescueAgentRetiringEvent args)
    {
        HandoffRescueAgentOwnership(agent.Owner, agent.Comp, args.Reason);
    }

    private void OnMedicalTargetStarted(
        EntityUid target,
        MobStateComponent component,
        ComponentStartup args)
    {
        // EntityUid values are reusable. A new mob identity must never inherit
        // transfer/queue state left by an entity removed during pooled cleanup.
        PurgeMedicalTargetLifecycle(target, "patient mob-state identity started");
    }

    private void OnMedicalTargetShutdown(
        EntityUid target,
        MobStateComponent component,
        ComponentShutdown args)
    {
        PurgeMedicalTargetLifecycle(target, "patient mob-state identity removed");
    }

    private void OnMedicalTargetTerminating(
        EntityUid target,
        MobStateComponent component,
        ref EntityTerminatingEvent args)
    {
        PurgeMedicalTargetLifecycle(target, "patient entity terminating");
    }

    private void PurgeMedicalTargetLifecycle(EntityUid target, string reason)
    {
        // Queue/transfer ownership is retired first; agent-side manual lineage
        // is then scrubbed directly without re-entering shuttle event handlers.
        // Team/escort mirrors run last so they observe the authoritative leader
        // assignment that remains after an unrelated passive patient is removed.
        PurgeMedicalTargetState(target);
        _rescueAgent.PurgeMedicalTargetStateFromAllAgents(target, reason);
        _rescueTeam.PurgeMedicalTargetState(target, reason);
    }

    private void PurgeMedicalTargetState(EntityUid target)
    {
        _pendingDispatches.Remove(target);
        _terminalDispatches.Remove(target);
        _dormantRouteTransfers.Remove(target);
        _onboardOwnershipTransfers.Remove(target);
        _patientRecoveryTransfers.Remove(target);
        ReleaseAutomaticSignalCooldowns(target);
        PurgePendingDispatchOrder(target);
    }

    private void OnRescueAgentTerminating(
        EntityUid agent,
        LuaMRescueAgentComponent rescue,
        ref EntityTerminatingEvent args)
    {
        HandoffRescueAgentOwnership(agent, rescue, "rescue agent entity terminating");
    }

    private void OnRescueAgentShutdown(
        EntityUid agent,
        LuaMRescueAgentComponent rescue,
        ComponentShutdown args)
    {
        HandoffRescueAgentOwnership(agent, rescue, "rescue agent role component removed");
    }

    private void HandoffRescueAgentOwnership(
        EntityUid agent,
        LuaMRescueAgentComponent rescue,
        string reason)
    {
        if (rescue.RetirementHandoffCompleted)
            return;

        // Set the guard before raising any downstream work. Explicit retirement
        // removes the component synchronously, which immediately raises shutdown.
        rescue.RetirementHandoffCompleted = true;
        CaptureOnboardOwnershipTransfer(agent, rescue, reason);
        CapturePatientRecoveryTransfers(agent, rescue, reason);
        CaptureDormantRouteTransfer(agent, rescue, reason);
        TransferAutomaticPatientsFromRetiringAgent(agent, rescue, reason);
        DowngradeManualDispatchesOwnedBy(agent, reason);
        // Route cancellation may synchronously complete a probe and mutate its
        // owner, so it must happen only after every handoff snapshot is captured.
        _rescueNavigation.CancelRoute(agent);
    }

    private void CaptureOnboardOwnershipTransfer(
        EntityUid agent,
        LuaMRescueAgentComponent rescue,
        string reason)
    {
        if (rescue.OnboardCareTarget is not { Valid: true } patient ||
            Deleted(patient) ||
            !TryComp<BuckleComponent>(patient, out var buckle) ||
            buckle.BuckledTo is not { Valid: true } strap ||
            Deleted(strap) ||
            rescue.AssignedShuttle is not { Valid: true } shuttle ||
            Deleted(shuttle) ||
            !IsEntityOnGrid(strap, shuttle))
        {
            return;
        }

        // A stable patient may be released synchronously only when there is no
        // configured home-docking contract left to satisfy. TryUnbuckle keeps the
        // engine's safety delay authoritative; a failed attempt becomes an exact
        // transfer instead of being counted as a new bounded failure.
        var stable = TryComp<MobStateComponent>(patient, out var mobState) &&
                     mobState.CurrentState == MobState.Alive &&
                     TryComp<DamageableComponent>(patient, out var damageable) &&
                     damageable.TotalDamage.Float() <= Math.Max(0f, rescue.AutoReleaseMaxDamage);
        if (stable &&
            rescue.AutoReleaseStabilizedPatients &&
            !rescue.IgnoredOnboardPatients.Contains(patient) &&
            !rescue.TerminalOnboardCareFailures.ContainsKey(patient) &&
            rescue.AssignedReturnTarget == null &&
            Transform(agent).Coordinates.TryDistance(
                EntityManager,
                Transform(strap).Coordinates,
                out var releaseDistance) &&
            releaseDistance <= Math.Max(0.1f, rescue.AutoReleaseRange) &&
            _buckle.TryUnbuckle(patient, user: null, buckleComp: buckle, popup: false))
        {
            _rescueAgent.CloseRecoveredPatientEpisode(agent, rescue, patient);
            rescue.AssignedPatientStrap = null;
            rescue.OnboardCareTarget = null;
            rescue.AssignedTarget = rescue.AssignedTarget == patient ? null : rescue.AssignedTarget;
            rescue.TaskPatientTarget = rescue.TaskPatientTarget == patient ? null : rescue.TaskPatientTarget;
            rescue.OnboardCareAttempts.Remove(patient);
            rescue.TerminalOnboardCareFailures.Remove(patient);
            rescue.OnboardHandoffAttempts.Remove(patient);
            rescue.IgnoredOnboardPatients.Remove(patient);
            rescue.RequiredOnboardHandoffPatients.Remove(patient);
            return;
        }

        var replacementAnchor = ResolveReplacementAnchor(agent, rescue, patient);
        _onboardOwnershipTransfers[patient] = new OnboardOwnershipTransfer
        {
            Patient = patient,
            ReplacementAnchor = replacementAnchor,
            Shuttle = shuttle,
            ShuttleAnchor = rescue.AssignedShuttleAnchor,
            Strap = strap,
            ShuttleConsole = rescue.AssignedShuttleConsole,
            ReturnTarget = rescue.AssignedReturnTarget,
            OnboardCareAttempts = rescue.OnboardCareAttempts.GetValueOrDefault(patient),
            TerminalOnboardCareFailure = rescue.TerminalOnboardCareFailures.GetValueOrDefault(patient),
            OnboardHandoffAttempts = rescue.OnboardHandoffAttempts.GetValueOrDefault(patient),
            Ignored = rescue.IgnoredOnboardPatients.Contains(patient),
            AutoReleaseStabilizedPatients = rescue.AutoReleaseStabilizedPatients,
            AutoReleaseMaxDamage = rescue.AutoReleaseMaxDamage,
            AutoReleaseRange = rescue.AutoReleaseRange,
            RequiredOnboardHandoff = rescue.RequiredOnboardHandoffPatients.Contains(patient) ||
                                       rescue.AssignedReturnTarget is { Valid: true },
            Status = $"owner={GetNetEntity(agent)}; reason={reason}",
        };

        _pendingDispatches.Remove(patient);
        _terminalDispatches.Remove(patient);
        ReleaseAutomaticSignalCooldowns(patient);
    }

    private void CapturePatientRecoveryTransfers(
        EntityUid agent,
        LuaMRescueAgentComponent rescue,
        string reason)
    {
        var targets = new HashSet<EntityUid>(rescue.SkippedTargets.Keys);
        targets.UnionWith(rescue.DeferredPatientTargets.Keys);
        targets.UnionWith(rescue.RouteFailureAttempts.Keys);
        targets.UnionWith(rescue.AnalysisAttempts.Keys);
        targets.UnionWith(rescue.TerminalAnalysisFailures.Keys);
        targets.UnionWith(rescue.TreatmentAttempts.Keys);
        targets.UnionWith(rescue.TerminalTreatmentFailures.Keys);
        targets.UnionWith(rescue.TerminalTreatmentFailureDamage.Keys);
        targets.UnionWith(rescue.DefibrillationAttempts.Keys);
        targets.UnionWith(rescue.CompletedDefibrillationFailures.Keys);
        targets.UnionWith(rescue.DefibrillationStartedAt.Keys);
        targets.UnionWith(rescue.TerminalDefibrillationFailures.Keys);
        targets.UnionWith(rescue.PullAttempts.Keys);
        targets.UnionWith(rescue.NextPullAttemptAt.Keys);
        targets.UnionWith(rescue.TerminalPullFailures.Keys);
        targets.UnionWith(rescue.EvacuationUnbuckleAttempts.Keys);
        targets.UnionWith(rescue.NextEvacuationUnbuckleAttemptAt.Keys);
        targets.UnionWith(rescue.TerminalEvacuationUnbuckleFailures.Keys);
        targets.UnionWith(rescue.PatientBuckleAttempts.Keys.Select(pair => pair.Patient));
        targets.UnionWith(rescue.NextPatientBuckleAttemptAt.Keys.Select(pair => pair.Patient));
        targets.UnionWith(rescue.TerminalPatientBuckleFailures.Keys.Select(pair => pair.Patient));
        targets.UnionWith(rescue.OnboardCareAttempts.Keys);
        targets.UnionWith(rescue.TerminalOnboardCareFailures.Keys);
        targets.UnionWith(rescue.OnboardHandoffAttempts.Keys);
        targets.UnionWith(rescue.IgnoredOnboardPatients);
        targets.UnionWith(rescue.RequiredOnboardHandoffPatients);

        foreach (var target in targets)
        {
            if (!target.Valid || Deleted(target))
                continue;

            // Elevated manual selection belongs to the exact retiring agent.
            // Neither deferred selection nor its bounded histories may silently
            // turn into automatic provenance on a replacement generation.
            if (rescue.ManualOverrideTarget == target &&
                !rescue.RequiredOnboardHandoffPatients.Contains(target) &&
                !_onboardOwnershipTransfers.ContainsKey(target))
                continue;

            var transfer = new PatientRecoveryTransfer
            {
                Target = target,
                ReplacementAnchor = ResolveReplacementAnchor(agent, rescue, target),
                AssignedShuttle = rescue.AssignedShuttle,
                AssignedShuttleAnchor = rescue.AssignedShuttleAnchor,
                AssignedShuttleConsole = rescue.AssignedShuttleConsole,
                AssignedReturnTarget = rescue.AssignedReturnTarget,
                Status = $"owner={GetNetEntity(agent)}; reason={reason}",
            };

            if (rescue.SkippedTargets.TryGetValue(target, out var skippedUntil))
                transfer.SkippedUntil = skippedUntil;
            if (rescue.DeferredPatientTargets.TryGetValue(target, out var deferredUntil))
                transfer.DeferredUntil = deferredUntil;
            if (rescue.RouteFailureAttempts.TryGetValue(target, out var routeAttempts))
                transfer.RouteFailureAttempts = routeAttempts;
            if (rescue.AnalysisAttempts.TryGetValue(target, out var analysisAttempts))
                transfer.AnalysisAttempts = analysisAttempts;
            if (rescue.TerminalAnalysisFailures.TryGetValue(target, out var terminalAnalysis))
                transfer.TerminalAnalysisFailure = terminalAnalysis;
            if (rescue.TreatmentAttempts.TryGetValue(target, out var treatmentAttempts))
                transfer.TreatmentAttempts = treatmentAttempts;
            if (rescue.TerminalTreatmentFailures.TryGetValue(target, out var terminalTreatment))
                transfer.TerminalTreatmentFailure = terminalTreatment;
            if (rescue.TerminalTreatmentFailureDamage.TryGetValue(target, out var terminalTreatmentDamage))
                transfer.TerminalTreatmentFailureDamage = terminalTreatmentDamage;
            if (rescue.DefibrillationAttempts.TryGetValue(target, out var defibAttempts))
                transfer.DefibrillationAttempts = defibAttempts;
            if (rescue.CompletedDefibrillationFailures.TryGetValue(target, out var completedDefibFailures))
                transfer.CompletedDefibrillationFailures = completedDefibFailures;
            if (rescue.DefibrillationStartedAt.TryGetValue(target, out var defibStartedAt))
                transfer.DefibrillationStartedAt = defibStartedAt;
            if (rescue.TerminalDefibrillationFailures.TryGetValue(target, out var terminalDefib))
                transfer.TerminalDefibrillationFailure = terminalDefib;
            if (rescue.PullAttempts.TryGetValue(target, out var pullAttempts))
                transfer.PullAttempts = pullAttempts;
            if (rescue.NextPullAttemptAt.TryGetValue(target, out var nextPullAt))
                transfer.NextPullAttemptAt = nextPullAt;
            if (rescue.TerminalPullFailures.TryGetValue(target, out var terminalPull))
                transfer.TerminalPullFailure = terminalPull;
            if (rescue.EvacuationUnbuckleAttempts.TryGetValue(target, out var unbuckleAttempts))
                transfer.EvacuationUnbuckleAttempts = unbuckleAttempts;
            if (rescue.NextEvacuationUnbuckleAttemptAt.TryGetValue(target, out var nextUnbuckleAt))
                transfer.NextEvacuationUnbuckleAttemptAt = nextUnbuckleAt;
            if (rescue.TerminalEvacuationUnbuckleFailures.TryGetValue(target, out var terminalUnbuckle))
                transfer.TerminalEvacuationUnbuckleFailure = terminalUnbuckle;
            if (rescue.OnboardCareAttempts.TryGetValue(target, out var onboardCareAttempts))
                transfer.OnboardCareAttempts = onboardCareAttempts;
            if (rescue.TerminalOnboardCareFailures.TryGetValue(target, out var terminalOnboardCare))
                transfer.TerminalOnboardCareFailure = terminalOnboardCare;
            if (rescue.OnboardHandoffAttempts.TryGetValue(target, out var onboardHandoffAttempts))
                transfer.OnboardHandoffAttempts = onboardHandoffAttempts;
            transfer.IgnoredOnboard = rescue.IgnoredOnboardPatients.Contains(target);
            transfer.RequiredOnboardHandoff = rescue.RequiredOnboardHandoffPatients.Contains(target);

            foreach (var (pair, attempts) in rescue.PatientBuckleAttempts)
            {
                if (pair.Patient == target)
                    transfer.PatientBuckleAttempts[pair.Strap] = attempts;
            }
            foreach (var (pair, retryAt) in rescue.NextPatientBuckleAttemptAt)
            {
                if (pair.Patient == target)
                    transfer.NextPatientBuckleAttemptAt[pair.Strap] = retryAt;
            }
            foreach (var (pair, failure) in rescue.TerminalPatientBuckleFailures)
            {
                if (pair.Patient == target)
                    transfer.TerminalPatientBuckleFailures[pair.Strap] = failure;
            }

            _patientRecoveryTransfers[target] = transfer;
        }
    }

    private void CaptureDormantRouteTransfer(
        EntityUid agent,
        LuaMRescueAgentComponent rescue,
        string reason)
    {
        if (rescue.DormantRouteTarget is not { Valid: true } target ||
            Deleted(target) ||
            rescue.ManualOverrideTarget == target)
        {
            return;
        }

        var kind = TryComp<MobStateComponent>(target, out var mobState)
            ? mobState.CurrentState switch
            {
                MobState.Dead => LuaMRescueMedicalSignalKind.Death,
                MobState.Critical => LuaMRescueMedicalSignalKind.Critical,
                _ => LuaMRescueMedicalSignalKind.FollowUp,
            }
            : LuaMRescueMedicalSignalKind.FollowUp;
        if (!TryGetAutomaticMedicalSignalEligibility(
                agent,
                target,
                kind,
                requireAttachedPlayer: false,
                manualOverride: false,
                out _))
        {
            return;
        }

        _dormantRouteTransfers[target] = new LuaMRescueDormantRouteTransfer(
            target,
            ResolveReplacementAnchor(agent, rescue, target),
            rescue.AssignedShuttle,
            rescue.AssignedShuttleAnchor,
            rescue.AssignedShuttleConsole,
            rescue.AssignedReturnTarget,
            rescue.NextDormantRouteProbeAt,
            rescue.DormantRouteActionRange,
            rescue.DormantRouteProbeCount,
            rescue.DormantRouteResumeCount,
            rescue.RouteFailureAttempts.GetValueOrDefault(target),
            rescue.DormantRouteObservationSeconds,
            rescue.DormantRouteProbeTimeoutSeconds,
            $"owner={GetNetEntity(agent)}; reason={reason}; previous={rescue.LastDormantRouteStatus}");

        // Dormant recovery is the queue owner. Any duplicate automatic record
        // must not race the replacement into a fresh zero-budget assignment.
        _pendingDispatches.Remove(target);
        _terminalDispatches.Remove(target);
        ReleaseAutomaticSignalCooldowns(target);
    }

    private void TransferAutomaticPatientsFromRetiringAgent(
        EntityUid agent,
        LuaMRescueAgentComponent rescue,
        string reason)
    {
        var candidates = new HashSet<EntityUid>();
        AddCandidate(rescue.AssignedTarget);
        AddCandidate(rescue.DeathSignalTarget);
        AddCandidate(rescue.EvacuatingTarget);
        AddCandidate(rescue.OnboardCareTarget);
        AddCandidate(rescue.TaskPatientTarget);
        if (rescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active)
            AddCandidate(rescue.ActivityContext.Target);
        foreach (var deferred in rescue.DeferredPatientTargets.Keys)
            AddCandidate(deferred);
        foreach (var requiredHandoff in rescue.RequiredOnboardHandoffPatients)
            AddCandidate(requiredHandoff);

        foreach (var target in candidates)
        {
            var requiredHandoff = rescue.RequiredOnboardHandoffPatients.Contains(target);
            // Manual authority belongs to the exact retired lineage and cannot
            // silently migrate to a replacement NPC. A normal automatic signal
            // may still rediscover the patient through its own eligibility gate.
            // Required physical custody instead becomes an automatic handoff
            // mission without carrying the retired manual authority forward.
            if ((rescue.ManualOverrideTarget == target && !requiredHandoff) ||
                _onboardOwnershipTransfers.ContainsKey(target) ||
                Deleted(target))
                continue;

            var kind = TryComp<MobStateComponent>(target, out var mobState)
                ? mobState.CurrentState switch
                {
                    MobState.Dead => LuaMRescueMedicalSignalKind.Death,
                    MobState.Critical => LuaMRescueMedicalSignalKind.Critical,
                    _ => LuaMRescueMedicalSignalKind.FollowUp,
                }
                : LuaMRescueMedicalSignalKind.FollowUp;
            var eligible = TryGetAutomaticMedicalSignalEligibility(
                    agent,
                    target,
                    kind,
                    requireAttachedPlayer: false,
                    manualOverride: false,
                    out var eligibilityStatus,
                    requiredOnboardHandoff: requiredHandoff);
            if (!eligible && !requiredHandoff)
            {
                continue;
            }

            QueueAutomaticSignalUnchecked(
                target,
                kind,
                eligible
                    ? $"automatic patient transferred from retiring rescue agent {GetNetEntity(agent)}: {reason}"
                    : $"required handoff transferred for structural reconciliation: {eligibilityStatus}; {reason}",
                manualOverride: false,
                manualOverrideOwner: null,
                manualOverrideGeneration: 0,
                preservedOwnerLineage: true,
                replacementAnchor: ResolveReplacementAnchor(agent, rescue, target),
                assignedShuttle: rescue.AssignedShuttle,
                assignedShuttleAnchor: rescue.AssignedShuttleAnchor,
                assignedShuttleConsole: rescue.AssignedShuttleConsole,
                assignedReturnTarget: rescue.AssignedReturnTarget,
                requiredOnboardHandoff: requiredHandoff);
        }

        void AddCandidate(EntityUid? candidate)
        {
            if (candidate is { Valid: true } candidateUid)
                candidates.Add(candidateUid);
        }
    }

    private void OnManualOverrideRevoked(
        Entity<LuaMRescueAgentComponent> agent,
        ref LuaMRescueManualOverrideRevokedEvent args)
    {
        if (_pendingDispatches.TryGetValue(args.Target, out var pending) &&
            pending.ManualOverride &&
            pending.ManualOverrideOwner == agent.Owner &&
            pending.ManualOverrideGeneration == args.Generation)
        {
            DowngradeManualDispatch(
                pending,
                $"manual override revoked by {GetNetEntity(agent.Owner)}: {args.Reason}; awaiting automatic eligibility");
        }

        if (_terminalDispatches.TryGetValue(args.Target, out var terminal) &&
            terminal.ManualOverride &&
            terminal.ManualOverrideOwner == agent.Owner &&
            terminal.ManualOverrideGeneration == args.Generation)
        {
            DowngradeManualDispatch(
                terminal,
                $"manual override revoked by {GetNetEntity(agent.Owner)}: {args.Reason}; explicit automatic retry required");
        }
    }

    private void OnExplicitPatientOrderAccepted(
        Entity<LuaMRescueAgentComponent> agent,
        ref LuaMRescueExplicitPatientOrderAcceptedEvent args)
    {
        // Manual provenance supersedes the old automatic owner, but physical
        // custody and its shuttle/home infrastructure do not. Adopt those
        // fields before deleting either durable dispatch record.
        if (_pendingDispatches.TryGetValue(args.Target, out var pending))
            ApplyDispatchProvenance(agent.Owner, agent.Comp, pending, args.Target);
        if (_terminalDispatches.TryGetValue(args.Target, out var terminal))
            ApplyDispatchProvenance(agent.Owner, agent.Comp, terminal, args.Target);

        // The accepted manual lineage is now the sole non-physical owner. An
        // older automatic queue/terminal record must not outlive this agent and
        // silently migrate the manual patient to a later replacement.
        _pendingDispatches.Remove(args.Target);
        _terminalDispatches.Remove(args.Target);
        ReleaseAutomaticSignalCooldowns(args.Target);

        // A successful explicit order supersedes non-physical recovery lineage.
        // Apply its infrastructure first: a manually spawned replacement may
        // otherwise lose the only surviving reference to the rescue shuttle.
        if (_patientRecoveryTransfers.Remove(args.Target, out var recovery))
        {
            if (recovery.RequiredOnboardHandoff)
            {
                ApplyRequiredHandoffInfrastructure(
                    agent.Comp,
                    recovery.AssignedShuttle,
                    recovery.AssignedShuttleAnchor,
                    recovery.AssignedShuttleConsole,
                    recovery.AssignedReturnTarget);
                agent.Comp.RequiredOnboardHandoffPatients.Add(args.Target);
            }
            else
            {
                ApplyReplacementInfrastructure(
                    agent.Comp,
                    recovery.AssignedShuttle,
                    recovery.AssignedShuttleAnchor,
                    recovery.AssignedShuttleConsole,
                    recovery.AssignedReturnTarget);
            }
        }

        if (_dormantRouteTransfers.Remove(args.Target, out var dormant))
        {
            ApplyReplacementInfrastructure(
                agent.Comp,
                dormant.AssignedShuttle,
                dormant.AssignedShuttleAnchor,
                dormant.AssignedShuttleConsole,
                dormant.AssignedReturnTarget);
        }

        // The patient may still be physically strapped to the inherited
        // shuttle. Preserve that exact transfer, but make its infrastructure
        // immediately visible to the explicit owner.
        if (_onboardOwnershipTransfers.TryGetValue(args.Target, out var onboard))
        {
            if (onboard.RequiredOnboardHandoff)
            {
                ApplyRequiredHandoffInfrastructure(
                    agent.Comp,
                    onboard.Shuttle,
                    onboard.ShuttleAnchor,
                    onboard.ShuttleConsole,
                    onboard.ReturnTarget);
                agent.Comp.RequiredOnboardHandoffPatients.Add(args.Target);
            }
            else
            {
                ApplyReplacementInfrastructure(
                    agent.Comp,
                    onboard.Shuttle,
                    onboard.ShuttleAnchor,
                    onboard.ShuttleConsole,
                    onboard.ReturnTarget);
            }
        }
    }

    private void OnRouteRequest(
        Entity<LuaMRescueShuttleLifecycleComponent> shuttle,
        ref LuaMRescueShuttleRouteRequestEvent args)
    {
        args.Handled = TrySetAutopilotTarget(
            shuttle.Owner,
            args.Target,
            args.RouteActivity,
            out var console);
        args.AutopilotConsole = console;
        args.Status = shuttle.Comp.LastStatus;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        if (now >= _nextLifecycleUpdate)
        {
            _nextLifecycleUpdate = now + LifecycleUpdateInterval;
            UpdateRouteLifecycles();
        }

        if (now >= _nextDispatchQueueUpdate)
        {
            _nextDispatchQueueUpdate = now + DispatchQueueUpdateInterval;
            ProcessPendingAutomaticDispatchesNow();
        }
    }

    private void OnMobStateChanged(MobStateChangedEvent ev)
    {
        if (ev.OldMobState is MobState.Critical or MobState.Dead &&
            ev.NewMobState == MobState.Alive)
        {
            CloseRecoveredMedicalEpisode(ev.Target);
            return;
        }

        if (ev.OldMobState != MobState.Dead &&
            ev.NewMobState == MobState.Dead)
        {
            TryDispatchAutomaticDeathSignal(ev.Target);
            return;
        }

        if (ev.OldMobState is MobState.Critical or MobState.Dead ||
            ev.NewMobState != MobState.Critical)
        {
            return;
        }

        TryDispatchAutomaticCriticalSignal(ev.Target);
    }

    private void CloseRecoveredMedicalEpisode(EntityUid target)
    {
        // Queue attempts, terminal diagnostics and global replacement snapshots
        // all belong to the just-completed critical/death episode. Leaving even
        // one of them alive can suppress or terminalize a later deterioration of
        // the same entity before a new owner is established.
        if (_pendingDispatches.TryGetValue(target, out var pending) && pending.RequiredOnboardHandoff)
        {
            pending.Kind = LuaMRescueMedicalSignalKind.FollowUp;
            pending.LastStatus = $"medical episode recovered; required physical handoff remains; {pending.LastStatus}";
        }
        else
        {
            _pendingDispatches.Remove(target);
        }

        if (_terminalDispatches.TryGetValue(target, out var terminal) && terminal.RequiredOnboardHandoff)
        {
            terminal.Kind = LuaMRescueMedicalSignalKind.FollowUp;
            terminal.LastStatus = $"medical episode recovered; terminal physical handoff remains; {terminal.LastStatus}";
        }
        else
        {
            _terminalDispatches.Remove(target);
        }

        if (_patientRecoveryTransfers.Remove(target, out var recovery) && recovery.RequiredOnboardHandoff)
        {
            if (_pendingDispatches.TryGetValue(target, out pending))
            {
                MergeRecoveryProvenance(pending, recovery);
            }
            else if (_terminalDispatches.TryGetValue(target, out terminal))
            {
                MergeRecoveryProvenance(terminal, recovery);
            }
            else if (_onboardOwnershipTransfers.TryGetValue(target, out var onboardTransfer))
            {
                onboardTransfer.RequiredOnboardHandoff = true;
            }
            else
            {
                QueueAutomaticSignalUnchecked(
                    target,
                    LuaMRescueMedicalSignalKind.FollowUp,
                    $"medical episode recovered while required handoff awaited adoption: {recovery.Status}",
                    manualOverride: false,
                    manualOverrideOwner: null,
                    manualOverrideGeneration: 0,
                    preservedOwnerLineage: true,
                    replacementAnchor: recovery.ReplacementAnchor,
                    assignedShuttle: recovery.AssignedShuttle,
                    assignedShuttleAnchor: recovery.AssignedShuttleAnchor,
                    assignedShuttleConsole: recovery.AssignedShuttleConsole,
                    assignedReturnTarget: recovery.AssignedReturnTarget,
                    requiredOnboardHandoff: true);
            }
        }

        _dormantRouteTransfers.Remove(target);
        ReleaseAutomaticSignalCooldowns(target);

        // Keep the physical strap/shuttle contract, but reset its episode-scoped
        // bounded history so an adopted stable patient can be released normally.
        if (_onboardOwnershipTransfers.TryGetValue(target, out var onboard))
        {
            onboard.OnboardCareAttempts = 0;
            onboard.TerminalOnboardCareFailure = null;
            onboard.OnboardHandoffAttempts = 0;
            onboard.Ignored = false;
        }

        var query = EntityQueryEnumerator<LuaMRescueAgentComponent>();
        while (query.MoveNext(out var agent, out var rescue))
        {
            if (Deleted(agent))
                continue;
            _rescueAgent.CloseRecoveredPatientEpisode(agent, rescue, target);
        }
    }

    private bool TryDispatchAutomaticDeathSignal(EntityUid target)
    {
        if (!TryGetAutomaticMedicalSignalEligibility(
                target,
                LuaMRescueMedicalSignalKind.Death,
                requireAttachedPlayer: true,
                out _))
            return false;

        PruneAutomaticDeathSignalCooldowns();

        var now = _timing.CurTime;
        if (_automaticDeathSignalCooldowns.TryGetValue(target, out var until) &&
            until > now)
        {
            return false;
        }

        if (!TryDispatchOrQueueAutomaticSignal(target, LuaMRescueMedicalSignalKind.Death, out _))
            return false;

        _automaticDeathSignalCooldowns[target] = now + TimeSpan.FromSeconds(AutomaticDeathSignalCooldownSeconds);
        return true;
    }

    private bool TryDispatchAutomaticCriticalSignal(EntityUid target)
    {
        if (!TryGetAutomaticMedicalSignalEligibility(
                target,
                LuaMRescueMedicalSignalKind.Critical,
                requireAttachedPlayer: true,
                out _))
            return false;

        PruneAutomaticCriticalSignalCooldowns();

        var now = _timing.CurTime;
        if (_automaticCriticalSignalCooldowns.TryGetValue(target, out var until) &&
            until > now)
        {
            return false;
        }

        if (!TryDispatchOrQueueAutomaticSignal(target, LuaMRescueMedicalSignalKind.Critical, out _))
            return false;

        _automaticCriticalSignalCooldowns[target] = now + TimeSpan.FromSeconds(AutomaticCriticalSignalCooldownSeconds);
        return true;
    }

    /// <summary>
    /// Strict local eligibility gate for automatic medical signals. This deliberately excludes non-humanoid
    /// actors and contained entities, because the rescue agent cannot safely evacuate either category.
    /// </summary>
    public bool TryGetAutomaticMedicalSignalEligibility(
        EntityUid target,
        LuaMRescueMedicalSignalKind kind,
        bool requireAttachedPlayer,
        out string reason)
    {
        return TryGetAutomaticMedicalSignalEligibility(
            target,
            kind,
            requireAttachedPlayer,
            manualOverride: false,
            out reason);
    }

    private bool TryGetAutomaticMedicalSignalEligibility(
        EntityUid target,
        LuaMRescueMedicalSignalKind kind,
        bool requireAttachedPlayer,
        bool manualOverride,
        out string reason)
    {
        return TryGetAutomaticMedicalSignalEligibility(
            EntityUid.Invalid,
            target,
            kind,
            requireAttachedPlayer,
            manualOverride,
            out reason,
            requiredOnboardHandoff: HasRequiredHandoffLineage(target));
    }

    private bool HasRequiredHandoffLineage(EntityUid target)
    {
        if ((_pendingDispatches.TryGetValue(target, out var pending) && pending.RequiredOnboardHandoff) ||
            (_terminalDispatches.TryGetValue(target, out var terminal) && terminal.RequiredOnboardHandoff) ||
            (_patientRecoveryTransfers.TryGetValue(target, out var recovery) && recovery.RequiredOnboardHandoff) ||
            (_onboardOwnershipTransfers.TryGetValue(target, out var onboard) && onboard.RequiredOnboardHandoff))
        {
            return true;
        }

        var query = EntityQueryEnumerator<LuaMRescueAgentComponent>();
        while (query.MoveNext(out _, out var rescue))
        {
            if (rescue.RequiredOnboardHandoffPatients.Contains(target))
                return true;
        }

        return false;
    }

    private bool TryGetAutomaticMedicalSignalEligibility(
        EntityUid eligibilityRescuer,
        EntityUid target,
        LuaMRescueMedicalSignalKind kind,
        bool requireAttachedPlayer,
        bool manualOverride,
        out string reason,
        bool requiredOnboardHandoff = false)
    {
        if (!target.Valid || Deleted(target))
        {
            reason = "target is missing or deleted";
            return false;
        }

        if (requireAttachedPlayer && !HasComp<ActorComponent>(target))
        {
            reason = "target is not an attached player actor";
            return false;
        }

        var requestKind = manualOverride
            ? LuaMRescuePatientRequestKind.Manual
            : kind switch
            {
                LuaMRescueMedicalSignalKind.Death => LuaMRescuePatientRequestKind.AutomaticDeathSignal,
                LuaMRescueMedicalSignalKind.FollowUp => LuaMRescuePatientRequestKind.AutomaticTreatment,
                _ => LuaMRescuePatientRequestKind.AutomaticEvacuation,
            };
        var medicallyEligible = _activity.IsEligibleRescuePatient(
                eligibilityRescuer,
                target,
                requestKind,
                manualOverride,
                out var failureReason);
        if (!medicallyEligible &&
            (!requiredOnboardHandoff || !IsAcceptedHandoffMedicalFailure(failureReason)))
        {
            reason = failureReason.ToString();
            return false;
        }

        if (!TryComp<MobStateComponent>(target, out var mobState))
        {
            reason = "target has no mob state";
            return false;
        }

        var expectedState = kind switch
        {
            LuaMRescueMedicalSignalKind.Death => MobState.Dead,
            LuaMRescueMedicalSignalKind.Critical => MobState.Critical,
            _ => (MobState?) null,
        };
        if (expectedState is { } requiredState && mobState.CurrentState != requiredState)
        {
            reason = $"target state is {mobState.CurrentState}, expected {requiredState}";
            return false;
        }

        if (!manualOverride && !HasComp<BuckleComponent>(target))
        {
            reason = "target does not expose the buckle capability required for evacuation";
            return false;
        }

        if (requiredOnboardHandoff && !HasComp<PullableComponent>(target))
        {
            reason = "required handoff target does not expose the pullable capability required for evacuation";
            return false;
        }

        reason = medicallyEligible
            ? "eligible"
            : $"eligible required handoff despite medical status {failureReason}";
        return true;
    }

    public int PendingAutomaticDispatchCount => _pendingDispatches.Count;

    public IReadOnlyList<LuaMRescuePendingDispatchSnapshot> GetPendingAutomaticDispatches()
    {
        var snapshots = new List<LuaMRescuePendingDispatchSnapshot>(_pendingDispatches.Count);
        var seen = new HashSet<EntityUid>();
        foreach (var queued in _pendingDispatchOrder)
        {
            var target = queued.Target;
            if (!_pendingDispatches.TryGetValue(target, out var pending) ||
                pending.QueueGeneration != queued.Generation ||
                !seen.Add(target))
            {
                continue;
            }

            snapshots.Add(ToPendingDispatchSnapshot(pending));
        }

        return snapshots;
    }

    public IReadOnlyList<LuaMRescuePendingDispatchSnapshot> GetTerminalAutomaticDispatches()
    {
        return _terminalDispatches.Values
            .OrderBy(entry => entry.EnqueuedAt)
            .ThenBy(entry => entry.Target.Id)
            .Select(ToPendingDispatchSnapshot)
            .ToArray();
    }

    public bool TryRetryTerminalAutomaticDispatch(EntityUid target, out string status)
    {
        if (_pendingDispatches.ContainsKey(target))
        {
            _terminalDispatches.Remove(target);
            status = "dispatch is already pending; obsolete terminal record removed";
            return true;
        }

        if (!_terminalDispatches.Remove(target, out var pending) || Deleted(target))
        {
            status = "no retryable terminal medical dispatch exists for the target";
            return false;
        }

        var now = _timing.CurTime;
        pending.Attempts = 0;
        pending.EnqueuedAt = now;
        pending.NextAttemptAt = now;
        pending.Deadline = now + DispatchQueueDeadline;
        pending.Terminal = false;
        pending.LastStatus = "manual dispatch retry requested";
        pending.QueueGeneration = NextPendingDispatchGeneration();
        _pendingDispatches[target] = pending;
        _pendingDispatchOrder.Enqueue((target, pending.QueueGeneration));
        status = pending.LastStatus;
        return true;
    }

    /// <summary>
    /// Runtime/test surface for feeding an automatic signal through the same strict gate as MobState events.
    /// A successful result means the dispatch was either launched or durably queued.
    /// </summary>
    public bool TryQueueAutomaticMedicalSignal(
        EntityUid target,
        LuaMRescueMedicalSignalKind kind,
        out string status)
    {
        if (!TryGetAutomaticMedicalSignalEligibility(target, kind, requireAttachedPlayer: true, out status))
            return false;

        return TryDispatchOrQueueAutomaticSignal(target, kind, out status);
    }

    /// <summary>
    /// Attempts one due pending signal. Exposed so integration tests and admin diagnostics can advance the
    /// queue without waiting for the periodic update.
    /// </summary>
    public bool ProcessPendingAutomaticDispatchesNow()
    {
        var adoptedOnboard = ReconcileOnboardOwnershipTransfers();
        var adoptedDormant = ReconcileDormantRouteTransfers();
        // Onboard and dormant ownership are accepted active missions and may
        // create the singleton replacement. Recovery history is applied only to
        // an agent created by one of those mission owners (or by a pending
        // dispatch); historical failure memory alone must never spawn an NPC.
        var adoptedRecovery = ReconcilePatientRecoveryTransfers();
        var reconciledTerminal = ReconcileResolvedTerminalDispatches();
        if (_pendingDispatchOrder.Count == 0)
            return adoptedRecovery > 0 || adoptedOnboard > 0 || adoptedDormant > 0 || reconciledTerminal > 0;

        var now = _timing.CurTime;
        var candidates = _pendingDispatchOrder.Count;
        for (var i = 0; i < candidates && _pendingDispatchOrder.Count > 0; i++)
        {
            var queued = _pendingDispatchOrder.Dequeue();
            var target = queued.Target;
            if (!_pendingDispatches.TryGetValue(target, out var pending) ||
                pending.QueueGeneration != queued.Generation)
            {
                continue;
            }

            if (pending.ManualOverride &&
                !IsManualDispatchLineageCurrent(pending, out var lineageStatus))
            {
                DowngradeManualDispatch(
                    pending,
                    $"manual override lineage expired: {lineageStatus}; awaiting automatic eligibility");
            }

            // Provenance must be merged before every early exit, including
            // deadline terminalization and active-owner reconciliation.
            if (_patientRecoveryTransfers.TryGetValue(target, out var earlyRecovery))
                MergeRecoveryProvenance(pending, earlyRecovery);

            if (TryFindLivingActiveRescueAgent(out var activeAgent, out var activeRescue) &&
                IsRescueAgentOperational(activeAgent) &&
                activeRescue.ActivityContext.Target == target &&
                activeRescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active)
            {
                ApplyDispatchProvenance(activeAgent, activeRescue, pending, target);
                _pendingDispatches.Remove(target);
                pending.LastStatus =
                    $"reconciled with active rescue owner {GetNetEntity(activeAgent)} before queue deadline";
                return true;
            }

            if (pending.Attempts >= DispatchQueueMaxAttempts || now >= pending.Deadline)
            {
                TerminalizePendingDispatch(
                    pending,
                    now >= pending.Deadline
                        ? "dispatch deadline exceeded"
                        : $"dispatch failed after {pending.Attempts} attempts");
                return true;
            }

            if (pending.NextAttemptAt > now)
            {
                _pendingDispatchOrder.Enqueue((target, pending.QueueGeneration));
                continue;
            }

            if (TryComp<MobStateComponent>(target, out var currentMobState))
            {
                pending.Kind = currentMobState.CurrentState switch
                {
                    MobState.Dead => LuaMRescueMedicalSignalKind.Death,
                    MobState.Critical => LuaMRescueMedicalSignalKind.Critical,
                    _ when pending.Kind is LuaMRescueMedicalSignalKind.Critical
                        or LuaMRescueMedicalSignalKind.Death => LuaMRescueMedicalSignalKind.FollowUp,
                    _ => pending.Kind,
                };
            }

            var eligibilityRescuer = EntityUid.Invalid;
            if (pending.PreservedOwnerLineage &&
                !TryFindLivingActiveRescueAgent(out eligibilityRescuer, out _))
            {
                if (!TrySpawnReplacementAgent(
                        pending.ReplacementAnchor,
                        target,
                        pending.AssignedShuttle,
                        pending.AssignedShuttleAnchor,
                        pending.AssignedShuttleConsole,
                        pending.AssignedReturnTarget,
                        out eligibilityRescuer,
                        out _,
                        out var replacementStatus))
                {
                    pending.LastStatus = replacementStatus;
                    pending.NextAttemptAt = now + DispatchQueueRetryDelay;
                    _pendingDispatchOrder.Enqueue((target, pending.QueueGeneration));
                    return true;
                }
            }
            if (pending.PreservedOwnerLineage)
            {
                // The accepted pending mission has now supplied a concrete
                // replacement owner. Restore all bounded histories before
                // eligibility or dispatch can observe a fresh zero budget.
                ReconcilePatientRecoveryTransfers();
            }

            var automaticallyEligible = TryGetAutomaticMedicalSignalEligibility(
                eligibilityRescuer,
                target,
                pending.Kind,
                requireAttachedPlayer: false,
                manualOverride: pending.ManualOverride,
                out var eligibilityStatus,
                requiredOnboardHandoff: pending.RequiredOnboardHandoff);
            if (!automaticallyEligible)
            {
                if (pending.RequiredOnboardHandoff)
                {
                    TerminalizePendingDispatch(
                        pending,
                        $"accepted onboard handoff became structurally ineligible: {eligibilityStatus}");
                    ReleaseAutomaticSignalCooldowns(target);
                    return true;
                }

                _pendingDispatches.Remove(target);
                ReleaseAutomaticSignalCooldowns(target);
                pending.LastStatus = $"discarded: {eligibilityStatus}";
                return true;
            }

            var inheritedRequiredHandoff = false;
            LuaMRescueAgentComponent? handoffRescue = null;
            if (pending.RequiredOnboardHandoff &&
                eligibilityRescuer.Valid &&
                TryComp<LuaMRescueAgentComponent>(eligibilityRescuer, out handoffRescue))
            {
                inheritedRequiredHandoff = handoffRescue.RequiredOnboardHandoffPatients.Contains(target);
                handoffRescue.RequiredOnboardHandoffPatients.Add(target);
            }

            var dispatchResult = TryDispatchAutomaticSignalNow(
                target,
                pending.Kind,
                pending.ManualOverride,
                pending.ManualOverrideOwner,
                pending.ManualOverrideGeneration,
                out var dispatchStatus);
            if (dispatchResult == AutomaticDispatchAttemptResult.Dispatched)
            {
                if (pending.PreservedOwnerLineage &&
                    TryFindLivingActiveRescueAgent(out var dispatchedAgent, out var dispatchedRescue) &&
                    IsRescueAssignedToTarget(dispatchedRescue, target))
                {
                    ApplyDispatchProvenance(dispatchedAgent, dispatchedRescue, pending, target);
                }
                _pendingDispatches.Remove(target);
                pending.LastStatus = dispatchStatus;
                return true;
            }

            if (!inheritedRequiredHandoff)
                handoffRescue?.RequiredOnboardHandoffPatients.Remove(target);

            pending.LastStatus = dispatchStatus;
            if (dispatchResult == AutomaticDispatchAttemptResult.Waiting)
            {
                // A busy/incapacitated singleton or global cooldown is queue
                // pressure, not a failed launch. Preserve the patient without
                // consuming the three-attempt infrastructure failure budget.
                pending.NextAttemptAt = now + DispatchQueueRetryDelay;
                _pendingDispatchOrder.Enqueue((target, pending.QueueGeneration));
                return true;
            }

            pending.Attempts++;
            if (pending.Attempts >= DispatchQueueMaxAttempts)
            {
                TerminalizePendingDispatch(
                    pending,
                    $"dispatch failed after {pending.Attempts} attempts: {dispatchStatus}");
                return true;
            }

            pending.NextAttemptAt = now + DispatchQueueRetryDelay;
            _pendingDispatchOrder.Enqueue((target, pending.QueueGeneration));
            return true;
        }

        return adoptedRecovery > 0 || adoptedOnboard > 0 || adoptedDormant > 0 || reconciledTerminal > 0;
    }

    private static bool IsAcceptedHandoffMedicalFailure(LuaMRescueFailureReason failure)
    {
        return failure is LuaMRescueFailureReason.TargetHealthy or
               LuaMRescueFailureReason.Unrevivable or
               LuaMRescueFailureReason.TargetHasNoMind or
               LuaMRescueFailureReason.TargetNotInjectable;
    }

    private bool TryDispatchOrQueueAutomaticSignal(
        EntityUid target,
        LuaMRescueMedicalSignalKind kind,
        out string status)
    {
        var requiredHandoffLineage = HasRequiredHandoffLineage(target);
        PendingMedicalDispatch? supersededTerminal = null;
        if (_terminalDispatches.TryGetValue(target, out var terminal))
        {
            var strictEscalation = IsStrictSignalEscalation(terminal.Kind, kind);
            if (!strictEscalation && !IsTerminalDispatchResolved(target, out _))
            {
                _pendingDispatches.Remove(target);
                status = $"terminal dispatch requires explicit retry: {terminal.LastStatus}";
                return false;
            }

            if (strictEscalation)
                supersededTerminal = terminal;
            _terminalDispatches.Remove(target);
        }

        _patientRecoveryTransfers.TryGetValue(target, out var recoveryProvenance);
        if (_pendingDispatches.TryGetValue(target, out var existingPending))
        {
            if (recoveryProvenance != null)
                MergeRecoveryProvenance(existingPending, recoveryProvenance);
            QueueAutomaticSignalUnchecked(
                target,
                kind,
                $"new {kind} signal refreshed an existing durable dispatch",
                existingPending.ManualOverride,
                existingPending.ManualOverrideOwner,
                existingPending.ManualOverrideGeneration,
                existingPending.PreservedOwnerLineage,
                existingPending.ReplacementAnchor,
                existingPending.AssignedShuttle,
                existingPending.AssignedShuttleAnchor,
                existingPending.AssignedShuttleConsole,
                existingPending.AssignedReturnTarget,
                existingPending.RequiredOnboardHandoff || requiredHandoffLineage);

            // Refreshing a durable record must still reconcile an owner that
            // became authoritative since the record was first queued. Keep the
            // queue on Waiting/Failed, and consume it only after the full
            // recovery/required-handoff provenance has been applied.
            var dispatchResult = TryDispatchAutomaticSignalNow(
                target,
                existingPending.Kind,
                existingPending.ManualOverride,
                existingPending.ManualOverrideOwner,
                existingPending.ManualOverrideGeneration,
                out var dispatchStatus);
            if (dispatchResult == AutomaticDispatchAttemptResult.Dispatched)
            {
                if (TryFindLivingActiveRescueAgent(out var dispatchedAgent, out var dispatchedRescue) &&
                    IsRescueAssignedToTarget(dispatchedRescue, target))
                {
                    ApplyDispatchProvenance(dispatchedAgent, dispatchedRescue, existingPending, target);
                }

                _pendingDispatches.Remove(target);
                status = $"refreshed existing durable dispatch: {dispatchStatus}";
                return true;
            }

            existingPending.LastStatus = dispatchStatus;
            status = $"queued by refreshing the existing durable dispatch: {dispatchStatus}";
            return true;
        }

        // Exact dormant/onboard custody outranks generic recovery history.
        // Retirement captures both, but a fresh signal in the sub-tick handoff
        // window must wait behind the exact transfer instead of presenting it
        // as history-only replacement provenance.
        if (_dormantRouteTransfers.ContainsKey(target) ||
            _onboardOwnershipTransfers.ContainsKey(target))
        {
            _ = TryDispatchAutomaticSignalNow(
                target,
                kind,
                manualOverride: false,
                manualOverrideOwner: null,
                manualOverrideGeneration: 0,
                out var exactTransferStatus);

            var recoveryOwnsRequiredInfrastructure = recoveryProvenance?.RequiredOnboardHandoff == true;
            var replacementAnchor = recoveryOwnsRequiredInfrastructure
                ? recoveryProvenance!.ReplacementAnchor
                : supersededTerminal?.ReplacementAnchor ?? recoveryProvenance?.ReplacementAnchor;
            var assignedShuttle = recoveryOwnsRequiredInfrastructure
                ? recoveryProvenance!.AssignedShuttle
                : supersededTerminal?.AssignedShuttle ?? recoveryProvenance?.AssignedShuttle;
            var assignedShuttleAnchor = recoveryOwnsRequiredInfrastructure
                ? recoveryProvenance!.AssignedShuttleAnchor
                : supersededTerminal?.AssignedShuttleAnchor ?? recoveryProvenance?.AssignedShuttleAnchor;
            var assignedShuttleConsole = recoveryOwnsRequiredInfrastructure
                ? recoveryProvenance!.AssignedShuttleConsole
                : supersededTerminal?.AssignedShuttleConsole ?? recoveryProvenance?.AssignedShuttleConsole;
            var assignedReturnTarget = recoveryOwnsRequiredInfrastructure
                ? recoveryProvenance!.AssignedReturnTarget
                : supersededTerminal?.AssignedReturnTarget ?? recoveryProvenance?.AssignedReturnTarget;
            var requiredHandoff = supersededTerminal?.RequiredOnboardHandoff == true ||
                                  recoveryOwnsRequiredInfrastructure ||
                                  requiredHandoffLineage;
            QueueAutomaticSignalUnchecked(
                target,
                kind,
                exactTransferStatus,
                manualOverride: false,
                manualOverrideOwner: null,
                manualOverrideGeneration: 0,
                preservedOwnerLineage: true,
                replacementAnchor: replacementAnchor,
                assignedShuttle: assignedShuttle,
                assignedShuttleAnchor: assignedShuttleAnchor,
                assignedShuttleConsole: assignedShuttleConsole,
                assignedReturnTarget: assignedReturnTarget,
                requiredOnboardHandoff: requiredHandoff);
            status = $"queued: {exactTransferStatus}";
            return true;
        }

        if (supersededTerminal != null || recoveryProvenance != null)
        {
            var recoveryOwnsRequiredInfrastructure = recoveryProvenance?.RequiredOnboardHandoff == true;
            var replacementAnchor = recoveryOwnsRequiredInfrastructure
                ? recoveryProvenance!.ReplacementAnchor
                : supersededTerminal?.ReplacementAnchor ?? recoveryProvenance?.ReplacementAnchor;
            var assignedShuttle = recoveryOwnsRequiredInfrastructure
                ? recoveryProvenance!.AssignedShuttle
                : supersededTerminal?.AssignedShuttle ?? recoveryProvenance?.AssignedShuttle;
            var assignedShuttleAnchor = recoveryOwnsRequiredInfrastructure
                ? recoveryProvenance!.AssignedShuttleAnchor
                : supersededTerminal?.AssignedShuttleAnchor ?? recoveryProvenance?.AssignedShuttleAnchor;
            var assignedShuttleConsole = recoveryOwnsRequiredInfrastructure
                ? recoveryProvenance!.AssignedShuttleConsole
                : supersededTerminal?.AssignedShuttleConsole ?? recoveryProvenance?.AssignedShuttleConsole;
            var assignedReturnTarget = recoveryOwnsRequiredInfrastructure
                ? recoveryProvenance!.AssignedReturnTarget
                : supersededTerminal?.AssignedReturnTarget ?? recoveryProvenance?.AssignedReturnTarget;
            var requiredHandoff = supersededTerminal?.RequiredOnboardHandoff == true ||
                                  recoveryOwnsRequiredInfrastructure ||
                                  requiredHandoffLineage;
            QueueAutomaticSignalUnchecked(
                target,
                kind,
                supersededTerminal != null
                    ? $"strict signal escalation superseded terminal {supersededTerminal.Kind} dispatch"
                    : "fresh signal adopted preserved patient recovery lineage",
                manualOverride: false,
                manualOverrideOwner: null,
                manualOverrideGeneration: 0,
                preservedOwnerLineage: true,
                replacementAnchor: replacementAnchor,
                assignedShuttle: assignedShuttle,
                assignedShuttleAnchor: assignedShuttleAnchor,
                assignedShuttleConsole: assignedShuttleConsole,
                assignedReturnTarget: assignedReturnTarget,
                requiredOnboardHandoff: requiredHandoff);
            status = "queued with preserved rescue ownership lineage";
            return true;
        }

        if (TryDispatchAutomaticSignalNow(
                target,
                kind,
                manualOverride: false,
                manualOverrideOwner: null,
                manualOverrideGeneration: 0,
                out status) ==
            AutomaticDispatchAttemptResult.Dispatched)
        {
            _pendingDispatches.Remove(target);
            return true;
        }

        QueueAutomaticSignalUnchecked(
            target,
            kind,
            status,
            manualOverride: false,
            manualOverrideOwner: null,
            manualOverrideGeneration: 0,
            preservedOwnerLineage: requiredHandoffLineage,
            requiredOnboardHandoff: requiredHandoffLineage);
        status = $"queued: {status}";
        return true;
    }

    private void QueueAutomaticSignalUnchecked(
        EntityUid target,
        LuaMRescueMedicalSignalKind kind,
        string status)
        => QueueAutomaticSignalUnchecked(
            target,
            kind,
            status,
            manualOverride: false,
            manualOverrideOwner: null,
            manualOverrideGeneration: 0);

    private void QueueAutomaticSignalUnchecked(
        EntityUid target,
        LuaMRescueMedicalSignalKind kind,
        string status,
        bool manualOverride,
        EntityUid? manualOverrideOwner,
        uint manualOverrideGeneration,
        bool preservedOwnerLineage = false,
        EntityUid? replacementAnchor = null,
        EntityUid? assignedShuttle = null,
        EntityUid? assignedShuttleAnchor = null,
        EntityUid? assignedShuttleConsole = null,
        EntityUid? assignedReturnTarget = null,
        bool requiredOnboardHandoff = false)
    {
        var now = _timing.CurTime;
        // A target has exactly one queue owner. Any fresh queue insertion or
        // displacement supersedes an older terminal diagnostic for that target.
        _terminalDispatches.Remove(target);

        if (_pendingDispatches.TryGetValue(target, out var existing))
        {
            var strictEscalation = IsStrictSignalEscalation(existing.Kind, kind);
            if (kind == LuaMRescueMedicalSignalKind.Death)
                existing.Kind = LuaMRescueMedicalSignalKind.Death;
            else if (kind == LuaMRescueMedicalSignalKind.Critical &&
                     existing.Kind == LuaMRescueMedicalSignalKind.FollowUp)
                existing.Kind = LuaMRescueMedicalSignalKind.Critical;

            existing.NextAttemptAt = now;
            existing.LastStatus = status;
            if (strictEscalation)
            {
                existing.Attempts = 0;
                existing.EnqueuedAt = now;
                existing.Deadline = now + DispatchQueueDeadline;
                existing.Terminal = false;
            }
            existing.PreservedOwnerLineage |= preservedOwnerLineage;
            if (requiredOnboardHandoff)
            {
                existing.ReplacementAnchor = replacementAnchor;
                existing.AssignedShuttle = assignedShuttle;
                existing.AssignedShuttleAnchor = assignedShuttleAnchor;
                existing.AssignedShuttleConsole = assignedShuttleConsole;
                existing.AssignedReturnTarget = assignedReturnTarget;
            }
            else
            {
                if (replacementAnchor is { Valid: true })
                    existing.ReplacementAnchor ??= replacementAnchor;
                existing.AssignedShuttle ??= assignedShuttle;
                existing.AssignedShuttleAnchor ??= assignedShuttleAnchor;
                existing.AssignedShuttleConsole ??= assignedShuttleConsole;
                existing.AssignedReturnTarget ??= assignedReturnTarget;
            }
            existing.RequiredOnboardHandoff |= requiredOnboardHandoff;
            if (manualOverride)
            {
                var newLineage = !existing.ManualOverride ||
                                 existing.ManualOverrideOwner != manualOverrideOwner ||
                                 existing.ManualOverrideGeneration != manualOverrideGeneration;
                existing.ManualOverride = true;
                existing.ManualOverrideOwner = manualOverrideOwner;
                existing.ManualOverrideGeneration = manualOverrideGeneration;
                if (newLineage)
                {
                    existing.Attempts = 0;
                    existing.EnqueuedAt = now;
                    existing.Deadline = now + DispatchQueueDeadline;
                    existing.Terminal = false;
                }
            }
            return;
        }

        var pending = new PendingMedicalDispatch
        {
            Target = target,
            Kind = kind,
            ManualOverride = manualOverride,
            ManualOverrideOwner = manualOverrideOwner,
            ManualOverrideGeneration = manualOverrideGeneration,
            PreservedOwnerLineage = preservedOwnerLineage,
            ReplacementAnchor = replacementAnchor,
            AssignedShuttle = assignedShuttle,
            AssignedShuttleAnchor = assignedShuttleAnchor,
            AssignedShuttleConsole = assignedShuttleConsole,
            AssignedReturnTarget = assignedReturnTarget,
            RequiredOnboardHandoff = requiredOnboardHandoff,
            QueueGeneration = NextPendingDispatchGeneration(),
            EnqueuedAt = now,
            NextAttemptAt = now,
            Deadline = now + DispatchQueueDeadline,
            LastStatus = status,
        };
        _pendingDispatches.Add(target, pending);
        _pendingDispatchOrder.Enqueue((target, pending.QueueGeneration));
        RaiseLocalEvent(
            target,
            new LuaMRescueDispatchQueuedEvent(target, kind, manualOverride, _pendingDispatches.Count),
            true);
    }

    private ulong NextPendingDispatchGeneration()
    {
        _nextPendingDispatchGeneration++;
        if (_nextPendingDispatchGeneration == 0)
            _nextPendingDispatchGeneration++;
        return _nextPendingDispatchGeneration;
    }

    private void PurgePendingDispatchOrder(EntityUid target)
    {
        var count = _pendingDispatchOrder.Count;
        for (var i = 0; i < count; i++)
        {
            var queued = _pendingDispatchOrder.Dequeue();
            if (queued.Target != target)
                _pendingDispatchOrder.Enqueue(queued);
        }
    }

    private static bool IsStrictSignalEscalation(
        LuaMRescueMedicalSignalKind current,
        LuaMRescueMedicalSignalKind incoming)
    {
        return SignalUrgency(incoming) > SignalUrgency(current);
    }

    private static int SignalUrgency(LuaMRescueMedicalSignalKind kind)
    {
        return kind switch
        {
            LuaMRescueMedicalSignalKind.FollowUp => 0,
            LuaMRescueMedicalSignalKind.Critical => 1,
            LuaMRescueMedicalSignalKind.Death => 2,
            _ => -1,
        };
    }

    private AutomaticDispatchAttemptResult TryDispatchAutomaticSignalNow(
        EntityUid target,
        LuaMRescueMedicalSignalKind kind,
        bool manualOverride,
        EntityUid? manualOverrideOwner,
        uint manualOverrideGeneration,
        out string status)
    {
        if (_dormantRouteTransfers.ContainsKey(target))
        {
            status = "target is awaiting dormant route lineage adoption by the replacement rescue agent";
            return AutomaticDispatchAttemptResult.Waiting;
        }

        if (_onboardOwnershipTransfers.ContainsKey(target))
        {
            status = "target is awaiting exact onboard ownership adoption by the replacement rescue agent";
            return AutomaticDispatchAttemptResult.Waiting;
        }

        if (_patientRecoveryTransfers.ContainsKey(target))
        {
            status = "target is awaiting bounded recovery-state adoption by the replacement rescue agent";
            return AutomaticDispatchAttemptResult.Waiting;
        }

        if (TryResolveDispatchAgent(
                manualOverride,
                manualOverrideOwner,
                out var activeAgent,
                out var activeRescue))
        {
            if (manualOverride &&
                (manualOverrideOwner != activeAgent ||
                 activeRescue.ManualOverrideTarget != target ||
                 activeRescue.ManualOverrideGeneration != manualOverrideGeneration))
            {
                status = "manual dispatch lineage is no longer owned by the active rescue agent";
                return AutomaticDispatchAttemptResult.Failed;
            }

            if (!IsRescueAgentOperational(activeAgent))
            {
                status = $"active rescue agent {GetNetEntity(activeAgent)} is medically incapacitated";
                return AutomaticDispatchAttemptResult.Waiting;
            }

            if (IsPatientAwaitingLocalRecovery(activeAgent, activeRescue, target))
            {
                status =
                    $"target {GetNetEntity(target)} remains owned by rescue agent {GetNetEntity(activeAgent)}; " +
                    "awaiting local route recovery";
                return AutomaticDispatchAttemptResult.Waiting;
            }

            if (IsRescueAssignedToTarget(activeRescue, target))
            {
                if (manualOverride)
                    _rescueAgent.EstablishManualOverrideTarget(activeAgent, activeRescue, target);
                if (activeRescue.ActivityContext.Target != target ||
                    activeRescue.ActivityContext.TerminalStatus != LuaMRescueTerminalStatus.Active)
                {
                    BeginFreshSignalEpisodeIntent(activeAgent, activeRescue, target);
                }
                ApplySignalToAgent(activeAgent, activeRescue, target, kind);
                status = $"existing rescue agent {GetNetEntity(activeAgent)} refreshed for target";
                return AutomaticDispatchAttemptResult.Dispatched;
            }

            if (!IsRescueAvailableForDispatch(activeRescue))
            {
                if (kind == LuaMRescueMedicalSignalKind.Critical &&
                    TryGetAssignedPatient(activeRescue, out var currentTarget) &&
                    ShouldPreemptForCriticalSignal(activeAgent, currentTarget, target, manualOverride))
                {
                    // A strapped onboard patient must first pass the bounded
                    // release/handoff policy. Dropping these fields here left
                    // an occupied bed with no activity owner.
                    if (activeRescue.OnboardCareTarget == currentTarget ||
                        activeRescue.AssignedPatientStrap is { Valid: true } ||
                        activeRescue.RequiredOnboardHandoffPatients.Contains(currentTarget))
                    {
                        status = $"active rescue agent {GetNetEntity(activeAgent)} is handing off onboard patient {GetNetEntity(currentTarget)}";
                        return AutomaticDispatchAttemptResult.Waiting;
                    }

                    var displacedKind = TryComp<MobStateComponent>(currentTarget, out var currentState) &&
                                        currentState.CurrentState == MobState.Dead
                        ? LuaMRescueMedicalSignalKind.Death
                        : LuaMRescueMedicalSignalKind.FollowUp;
                    var displacedManualOverride = activeRescue.ManualOverrideTarget == currentTarget;
                    QueueAutomaticSignalUnchecked(
                        currentTarget,
                        displacedKind,
                        $"preempted by higher-priority critical signal {GetNetEntity(target)}",
                        displacedManualOverride,
                        displacedManualOverride ? activeAgent : null,
                        displacedManualOverride ? activeRescue.ManualOverrideGeneration : 0,
                        preservedOwnerLineage: !displacedManualOverride,
                        replacementAnchor: ResolveReplacementAnchor(activeAgent, activeRescue, currentTarget),
                        assignedShuttle: activeRescue.AssignedShuttle,
                        assignedShuttleAnchor: activeRescue.AssignedShuttleAnchor,
                        assignedShuttleConsole: activeRescue.AssignedShuttleConsole,
                        assignedReturnTarget: activeRescue.AssignedReturnTarget,
                        requiredOnboardHandoff: activeRescue.RequiredOnboardHandoffPatients.Contains(currentTarget));
                    AssignSignalToExistingAgent(activeAgent, activeRescue, target, kind, manualOverride);
                    status = $"critical target {GetNetEntity(target)} preempted lower-priority patient {GetNetEntity(currentTarget)}";
                    return AutomaticDispatchAttemptResult.Dispatched;
                }

                status = $"active rescue agent {GetNetEntity(activeAgent)} is busy";
                return AutomaticDispatchAttemptResult.Waiting;
            }

            AssignSignalToExistingAgent(activeAgent, activeRescue, target, kind, manualOverride);
            status = $"idle rescue agent {GetNetEntity(activeAgent)} assigned to target";
            return AutomaticDispatchAttemptResult.Dispatched;
        }

        if (manualOverride)
        {
            status = "manual dispatch owner is no longer an active rescue agent";
            return AutomaticDispatchAttemptResult.Failed;
        }

        if (!_prototypes.TryIndex<VesselPrototype>(DefaultVessel, out var vessel))
        {
            status = $"rescue vessel prototype {DefaultVessel} is unavailable";
            return AutomaticDispatchAttemptResult.Failed;
        }

        if (!TryResolveAutomaticMedicalSignalStation(target, out var station))
        {
            status = "could not resolve a station for the medical signal";
            return AutomaticDispatchAttemptResult.Failed;
        }

        if (kind != LuaMRescueMedicalSignalKind.Death &&
            _sectorStory.TryGetActiveRescueCooldown(out var remainingSeconds, out _))
        {
            status = $"rescue dispatch cooldown active for {remainingSeconds}s";
            return AutomaticDispatchAttemptResult.Waiting;
        }

        var dispatched = TryDispatchRescueShuttle(
            station,
            vessel,
            target,
            controller: null,
            spawnAgent: true,
            spawnTeam: true,
            control: false,
            routeToTarget: true,
            deathSignal: kind == LuaMRescueMedicalSignalKind.Death,
            manualOverride: manualOverride,
            out _,
            out var agent,
            out _,
            out _,
            out status);

        if (!dispatched)
            return AutomaticDispatchAttemptResult.Failed;

        if (kind != LuaMRescueMedicalSignalKind.Death &&
            agent is { Valid: true } agentUid &&
            !Deleted(agentUid))
        {
            if (kind == LuaMRescueMedicalSignalKind.FollowUp)
            {
                SendFollowUpDispatchRadio(agentUid, target);
                MarkFollowUpDispatchReported(agentUid, target);
            }
            else
            {
                SendCriticalDispatchRadio(agentUid, target);
                MarkCriticalSignalDispatchReported(agentUid, target);
            }
        }

        return AutomaticDispatchAttemptResult.Dispatched;
    }

    private enum AutomaticDispatchAttemptResult : byte
    {
        Dispatched,
        Waiting,
        Failed,
    }

    private static LuaMRescuePendingDispatchSnapshot ToPendingDispatchSnapshot(PendingMedicalDispatch pending)
    {
        return new LuaMRescuePendingDispatchSnapshot(
            pending.Target,
            pending.Kind,
            pending.ManualOverride,
            pending.ManualOverrideOwner,
            pending.ManualOverrideGeneration,
            pending.EnqueuedAt,
            pending.NextAttemptAt,
            pending.Attempts,
            pending.Deadline,
            pending.Terminal,
            pending.LastStatus,
            pending.RequiredOnboardHandoff);
    }

    private void TerminalizePendingDispatch(PendingMedicalDispatch pending, string status)
    {
        _pendingDispatches.Remove(pending.Target);
        pending.NextAttemptAt = TimeSpan.Zero;
        pending.Terminal = true;
        pending.LastStatus = status;
        _terminalDispatches[pending.Target] = pending;
    }

    private int ReconcileResolvedTerminalDispatches()
    {
        var removed = 0;
        foreach (var target in _terminalDispatches.Keys.ToArray())
        {
            if (!IsTerminalDispatchResolved(target, out _))
                continue;

            _terminalDispatches.Remove(target);
            removed++;
        }

        return removed;
    }

    private int ReconcileDormantRouteTransfers()
    {
        if (_dormantRouteTransfers.Count == 0)
            return 0;

        var changed = 0;
        if (!TryFindLivingActiveRescueAgent(out var activeAgent, out var activeRescue))
        {
            var first = _dormantRouteTransfers.Values
                .OrderBy(entry => entry.NextProbeAt)
                .ThenBy(entry => entry.Target.Id)
                .First();
            if (!TrySpawnReplacementAgent(
                    first.ReplacementAnchor,
                    first.Target,
                    first.AssignedShuttle,
                    first.AssignedShuttleAnchor,
                    first.AssignedShuttleConsole,
                    first.AssignedReturnTarget,
                    out activeAgent,
                    out activeRescue,
                    out _))
            {
                return 0;
            }
            changed++;
        }
        foreach (var transfer in _dormantRouteTransfers.Values
                     .OrderBy(entry => entry.NextProbeAt)
                     .ThenBy(entry => entry.Target.Id)
                     .ToArray())
        {
            var target = transfer.Target;
            ApplyReplacementInfrastructure(
                activeRescue,
                transfer.AssignedShuttle,
                transfer.AssignedShuttleAnchor,
                transfer.AssignedShuttleConsole,
                transfer.AssignedReturnTarget);
            var kind = TryComp<MobStateComponent>(target, out var mobState)
                ? mobState.CurrentState switch
                {
                    MobState.Dead => LuaMRescueMedicalSignalKind.Death,
                    MobState.Critical => LuaMRescueMedicalSignalKind.Critical,
                    _ => LuaMRescueMedicalSignalKind.FollowUp,
                }
                : LuaMRescueMedicalSignalKind.FollowUp;
            if (Deleted(target) ||
                !TryGetAutomaticMedicalSignalEligibility(
                    activeAgent,
                    target,
                    kind,
                    requireAttachedPlayer: false,
                    manualOverride: false,
                    out _))
            {
                _dormantRouteTransfers.Remove(target);
                ReleaseAutomaticSignalCooldowns(target);
                changed++;
                continue;
            }

            if (activeRescue.ManualOverrideTarget == target)
            {
                _dormantRouteTransfers.Remove(target);
                changed++;
                continue;
            }

            if (!_rescueAgent.TryAdoptDormantRouteRecovery(activeAgent, activeRescue, transfer))
                continue;

            _dormantRouteTransfers.Remove(target);
            changed++;
        }

        return changed;
    }

    private int ReconcileOnboardOwnershipTransfers()
    {
        var changed = 0;
        foreach (var transfer in _onboardOwnershipTransfers.Values.ToArray())
        {
            var patient = transfer.Patient;
            if (Deleted(patient))
            {
                _onboardOwnershipTransfers.Remove(patient);
                ReleaseAutomaticSignalCooldowns(patient);
                changed++;
                continue;
            }

            if (Deleted(transfer.Strap) ||
                Deleted(transfer.Shuttle) ||
                !TryComp<BuckleComponent>(patient, out var buckle) ||
                buckle.BuckledTo != transfer.Strap ||
                !IsEntityOnGrid(transfer.Strap, transfer.Shuttle))
            {
                // The exact physical contract disappeared before adoption. A
                // still-unresolved accepted patient remains a mission: downgrade
                // it to the preserved automatic queue instead of waiting for a
                // MobState edge that may never happen again.
                _onboardOwnershipTransfers.Remove(patient);
                if (_patientRecoveryTransfers.TryGetValue(patient, out var recovery))
                {
                    // These budgets describe the vanished strap contract, not
                    // the preserved medical mission. Reapplying them would make
                    // the replacement wait forever on an object that no longer
                    // owns the patient.
                    recovery.EvacuationUnbuckleAttempts = null;
                    recovery.NextEvacuationUnbuckleAttemptAt = null;
                    recovery.TerminalEvacuationUnbuckleFailure = null;
                    recovery.PatientBuckleAttempts.Clear();
                    recovery.NextPatientBuckleAttemptAt.Clear();
                    recovery.TerminalPatientBuckleFailures.Clear();
                    recovery.OnboardCareAttempts = null;
                    recovery.TerminalOnboardCareFailure = null;
                    recovery.OnboardHandoffAttempts = null;
                    recovery.IgnoredOnboard = false;
                }
                var state = TryComp<MobStateComponent>(patient, out var mobState)
                    ? mobState.CurrentState
                    : MobState.Invalid;
                var recovered = state == MobState.Alive &&
                                !transfer.RequiredOnboardHandoff &&
                                TryComp<DamageableComponent>(patient, out var damageable) &&
                                damageable.TotalDamage.Float() <= Math.Max(0f, transfer.AutoReleaseMaxDamage) &&
                                transfer.AutoReleaseStabilizedPatients &&
                                transfer.ReturnTarget == null &&
                                !transfer.Ignored &&
                                string.IsNullOrWhiteSpace(transfer.TerminalOnboardCareFailure);
                if (recovered)
                {
                    _patientRecoveryTransfers.Remove(patient);
                }
                else
                {
                    if (_patientRecoveryTransfers.TryGetValue(patient, out var requiredRecovery))
                        requiredRecovery.RequiredOnboardHandoff = true;
                    var kind = state switch
                    {
                        MobState.Dead => LuaMRescueMedicalSignalKind.Death,
                        MobState.Critical => LuaMRescueMedicalSignalKind.Critical,
                        _ => LuaMRescueMedicalSignalKind.FollowUp,
                    };
                    QueueAutomaticSignalUnchecked(
                        patient,
                        kind,
                        $"onboard physical ownership invalidated before adoption: {transfer.Status}",
                        manualOverride: false,
                        manualOverrideOwner: null,
                        manualOverrideGeneration: 0,
                        preservedOwnerLineage: true,
                        replacementAnchor: transfer.ReplacementAnchor,
                        assignedShuttle: transfer.Shuttle,
                        assignedShuttleAnchor: transfer.ShuttleAnchor,
                        assignedShuttleConsole: transfer.ShuttleConsole,
                        assignedReturnTarget: transfer.ReturnTarget,
                        requiredOnboardHandoff: true);
                }
                ReleaseAutomaticSignalCooldowns(patient);
                changed++;
                continue;
            }

            if (!TryFindLivingActiveRescueAgent(out var activeAgent, out var activeRescue) &&
                !TrySpawnReplacementAgent(
                    transfer.ReplacementAnchor,
                    patient,
                    transfer.Shuttle,
                    transfer.ShuttleAnchor,
                    transfer.ShuttleConsole,
                    transfer.ReturnTarget,
                    out activeAgent,
                    out activeRescue,
                    out _))
            {
                continue;
            }

            if (!IsRescueAvailableForDispatch(activeRescue) &&
                !IsRescueAssignedToTarget(activeRescue, patient))
            {
                continue;
            }

            _rescueAgent.PrepareForExternalIntentReplacement(activeAgent, patient);
            if (!_activity.BeginOrReplaceIntent(
                    activeAgent,
                    activeRescue.ActivityRole,
                    LuaMRescueActivity.OnboardCare,
                    patient,
                    new EntityCoordinates(transfer.Strap, Vector2.Zero),
                    out _))
            {
                continue;
            }

            activeRescue.AssignedShuttle = transfer.Shuttle;
            activeRescue.AssignedShuttleAnchor = transfer.ShuttleAnchor ?? transfer.ReplacementAnchor;
            activeRescue.AssignedPatientStrap = transfer.Strap;
            activeRescue.AssignedShuttleConsole = transfer.ShuttleConsole;
            activeRescue.AssignedReturnTarget = transfer.ReturnTarget;
            activeRescue.AssignedTarget = patient;
            activeRescue.OnboardCareTarget = patient;
            activeRescue.TaskPatientTarget = patient;
            activeRescue.TaskStage = LuaMRescueTaskStage.DeliveringPatient;
            activeRescue.AutoReleaseStabilizedPatients = transfer.AutoReleaseStabilizedPatients;
            activeRescue.AutoReleaseMaxDamage = transfer.AutoReleaseMaxDamage;
            activeRescue.AutoReleaseRange = transfer.AutoReleaseRange;
            if (transfer.OnboardCareAttempts > 0)
                activeRescue.OnboardCareAttempts[patient] = transfer.OnboardCareAttempts;
            if (!string.IsNullOrWhiteSpace(transfer.TerminalOnboardCareFailure))
                activeRescue.TerminalOnboardCareFailures[patient] = transfer.TerminalOnboardCareFailure;
            if (transfer.OnboardHandoffAttempts > 0)
                activeRescue.OnboardHandoffAttempts[patient] = transfer.OnboardHandoffAttempts;
            if (transfer.Ignored)
                activeRescue.IgnoredOnboardPatients.Add(patient);
            if (transfer.RequiredOnboardHandoff)
                activeRescue.RequiredOnboardHandoffPatients.Add(patient);
            Dirty(activeAgent, activeRescue);

            _pendingDispatches.Remove(patient);
            _terminalDispatches.Remove(patient);
            _onboardOwnershipTransfers.Remove(patient);
            ReleaseAutomaticSignalCooldowns(patient);
            changed++;
        }

        return changed;
    }

    private int ReconcilePatientRecoveryTransfers()
    {
        var changed = 0;
        foreach (var target in _patientRecoveryTransfers.Keys.ToArray())
        {
            if (!Deleted(target))
                continue;
            _patientRecoveryTransfers.Remove(target);
            changed++;
        }

        if (_patientRecoveryTransfers.Count == 0)
            return changed;

        if (!TryFindLivingActiveRescueAgent(out var activeAgent, out var activeRescue))
            return changed;

        foreach (var transfer in _patientRecoveryTransfers.Values.ToArray())
        {
            var target = transfer.Target;
            var authoritativeOwner = activeRescue.ActivityContext.Target == target &&
                                     activeRescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active;
            if (transfer.RequiredOnboardHandoff)
            {
                if (_pendingDispatches.TryGetValue(target, out var pending))
                {
                    MergeRecoveryProvenance(pending, transfer);
                }
                else if (_terminalDispatches.TryGetValue(target, out var terminal))
                {
                    MergeRecoveryProvenance(terminal, transfer);
                }
                else if (authoritativeOwner)
                {
                    activeRescue.RequiredOnboardHandoffPatients.Add(target);
                }
                else
                {
                    var kind = TryComp<MobStateComponent>(target, out var mobState)
                        ? mobState.CurrentState switch
                        {
                            MobState.Dead => LuaMRescueMedicalSignalKind.Death,
                            MobState.Critical => LuaMRescueMedicalSignalKind.Critical,
                            _ => LuaMRescueMedicalSignalKind.FollowUp,
                        }
                        : LuaMRescueMedicalSignalKind.FollowUp;
                    QueueAutomaticSignalUnchecked(
                        target,
                        kind,
                        $"required handoff restored from recovery lineage: {transfer.Status}",
                        manualOverride: false,
                        manualOverrideOwner: null,
                        manualOverrideGeneration: 0,
                        preservedOwnerLineage: true,
                        replacementAnchor: transfer.ReplacementAnchor,
                        assignedShuttle: transfer.AssignedShuttle,
                        assignedShuttleAnchor: transfer.AssignedShuttleAnchor,
                        assignedShuttleConsole: transfer.AssignedShuttleConsole,
                        assignedReturnTarget: transfer.AssignedReturnTarget,
                        requiredOnboardHandoff: true);
                }
            }

            if (transfer.RequiredOnboardHandoff && authoritativeOwner)
            {
                ApplyRequiredHandoffInfrastructure(
                    activeRescue,
                    transfer.AssignedShuttle,
                    transfer.AssignedShuttleAnchor,
                    transfer.AssignedShuttleConsole,
                    transfer.AssignedReturnTarget);
            }
            else if (!transfer.RequiredOnboardHandoff)
            {
                ApplyReplacementInfrastructure(
                    activeRescue,
                    transfer.AssignedShuttle,
                    transfer.AssignedShuttleAnchor,
                    transfer.AssignedShuttleConsole,
                    transfer.AssignedReturnTarget);
            }

            if (transfer.SkippedUntil is { } skippedUntil &&
                (!activeRescue.SkippedTargets.TryGetValue(target, out var currentSkip) || currentSkip < skippedUntil))
            {
                activeRescue.SkippedTargets[target] = skippedUntil;
            }
            if (transfer.DeferredUntil is { } deferredUntil &&
                (!activeRescue.DeferredPatientTargets.TryGetValue(target, out var currentDeferred) || currentDeferred < deferredUntil))
            {
                activeRescue.DeferredPatientTargets[target] = deferredUntil;
            }
            CopyMax(transfer.RouteFailureAttempts, activeRescue.RouteFailureAttempts);
            CopyMax(transfer.AnalysisAttempts, activeRescue.AnalysisAttempts);
            CopyMax(transfer.TreatmentAttempts, activeRescue.TreatmentAttempts);
            CopyMax(transfer.DefibrillationAttempts, activeRescue.DefibrillationAttempts);
            CopyMax(transfer.CompletedDefibrillationFailures, activeRescue.CompletedDefibrillationFailures);
            CopyMax(transfer.PullAttempts, activeRescue.PullAttempts);
            CopyMax(transfer.EvacuationUnbuckleAttempts, activeRescue.EvacuationUnbuckleAttempts);
            CopyMax(transfer.OnboardCareAttempts, activeRescue.OnboardCareAttempts);
            CopyMax(transfer.OnboardHandoffAttempts, activeRescue.OnboardHandoffAttempts);

            CopyLater(transfer.NextPullAttemptAt, activeRescue.NextPullAttemptAt);
            CopyLater(transfer.NextEvacuationUnbuckleAttemptAt, activeRescue.NextEvacuationUnbuckleAttemptAt);
            if (transfer.DefibrillationStartedAt is { } defibStarted &&
                !activeRescue.DefibrillationStartedAt.ContainsKey(target))
            {
                activeRescue.DefibrillationStartedAt[target] = defibStarted;
            }

            CopyTerminal(transfer.TerminalTreatmentFailure, activeRescue.TerminalTreatmentFailures);
            CopyTerminal(transfer.TerminalAnalysisFailure, activeRescue.TerminalAnalysisFailures);
            if (transfer.TerminalTreatmentFailureDamage is { } terminalDamage &&
                (!activeRescue.TerminalTreatmentFailureDamage.TryGetValue(target, out var currentDamage) ||
                 currentDamage < terminalDamage))
            {
                activeRescue.TerminalTreatmentFailureDamage[target] = terminalDamage;
            }
            CopyTerminal(transfer.TerminalDefibrillationFailure, activeRescue.TerminalDefibrillationFailures);
            CopyTerminal(transfer.TerminalPullFailure, activeRescue.TerminalPullFailures);
            CopyTerminal(transfer.TerminalEvacuationUnbuckleFailure, activeRescue.TerminalEvacuationUnbuckleFailures);
            CopyTerminal(transfer.TerminalOnboardCareFailure, activeRescue.TerminalOnboardCareFailures);
            if (transfer.IgnoredOnboard)
                activeRescue.IgnoredOnboardPatients.Add(target);
            if (transfer.RequiredOnboardHandoff && authoritativeOwner)
                activeRescue.RequiredOnboardHandoffPatients.Add(target);

            foreach (var (strap, attempts) in transfer.PatientBuckleAttempts)
            {
                var pair = (Patient: target, Strap: strap);
                activeRescue.PatientBuckleAttempts[pair] = Math.Max(
                    activeRescue.PatientBuckleAttempts.GetValueOrDefault(pair),
                    attempts);
            }
            foreach (var (strap, retryAt) in transfer.NextPatientBuckleAttemptAt)
            {
                var pair = (Patient: target, Strap: strap);
                if (!activeRescue.NextPatientBuckleAttemptAt.TryGetValue(pair, out var currentRetry) ||
                    currentRetry < retryAt)
                {
                    activeRescue.NextPatientBuckleAttemptAt[pair] = retryAt;
                }
            }
            foreach (var (strap, failure) in transfer.TerminalPatientBuckleFailures)
            {
                activeRescue.TerminalPatientBuckleFailures.TryAdd((target, strap), failure);
            }

            _patientRecoveryTransfers.Remove(target);
            changed++;

            void CopyMax(int? value, Dictionary<EntityUid, int> destination)
            {
                if (value is { } count)
                    destination[target] = Math.Max(destination.GetValueOrDefault(target), count);
            }

            void CopyLater(TimeSpan? value, Dictionary<EntityUid, TimeSpan> destination)
            {
                if (value is not { } retryAt)
                    return;
                if (!destination.TryGetValue(target, out var current) || current < retryAt)
                    destination[target] = retryAt;
            }

            void CopyTerminal(string? value, Dictionary<EntityUid, string> destination)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    destination.TryAdd(target, value);
            }
        }

        Dirty(activeAgent, activeRescue);
        return changed;
    }

    private bool TrySpawnReplacementAgent(
        EntityUid? preferredAnchor,
        EntityUid fallbackTarget,
        EntityUid? assignedShuttle,
        EntityUid? assignedShuttleAnchor,
        EntityUid? assignedShuttleConsole,
        EntityUid? assignedReturnTarget,
        out EntityUid agent,
        out LuaMRescueAgentComponent rescue,
        out string status)
    {
        var anchor = preferredAnchor is { Valid: true } preferred && !Deleted(preferred)
            ? preferred
            : fallbackTarget;
        if (!anchor.Valid || Deleted(anchor))
        {
            agent = default;
            rescue = default!;
            status = "accepted patient has no valid replacement spawn anchor";
            return false;
        }

        if (!_rescueAgent.TrySpawnAgent(
                anchor,
                followTarget: null,
                controller: null,
                control: false,
                out agent,
                out status))
        {
            if (TryFindLivingActiveRescueAgent(out agent, out rescue))
            {
                ApplyReplacementInfrastructure(
                    rescue,
                    assignedShuttle,
                    assignedShuttleAnchor,
                    assignedShuttleConsole,
                    assignedReturnTarget);
                return true;
            }

            rescue = default!;
            return false;
        }

        rescue = Comp<LuaMRescueAgentComponent>(agent);
        ApplyReplacementInfrastructure(
            rescue,
            assignedShuttle,
            assignedShuttleAnchor,
            assignedShuttleConsole,
            assignedReturnTarget);
        status = $"replacement rescue agent {GetNetEntity(agent)} spawned for preserved ownership lineage";
        return true;
    }

    private void ApplyDispatchProvenance(
        EntityUid owner,
        LuaMRescueAgentComponent rescue,
        PendingMedicalDispatch dispatch,
        EntityUid target)
    {
        ApplyDispatchInfrastructure(rescue, dispatch);
        if (dispatch.RequiredOnboardHandoff)
            rescue.RequiredOnboardHandoffPatients.Add(target);
        Dirty(owner, rescue);
    }

    private void ApplyDispatchInfrastructure(
        LuaMRescueAgentComponent rescue,
        PendingMedicalDispatch dispatch)
    {
        if (dispatch.RequiredOnboardHandoff)
        {
            ApplyRequiredHandoffInfrastructure(
                rescue,
                dispatch.AssignedShuttle,
                dispatch.AssignedShuttleAnchor,
                dispatch.AssignedShuttleConsole,
                dispatch.AssignedReturnTarget);
            return;
        }

        ApplyReplacementInfrastructure(
            rescue,
            dispatch.AssignedShuttle,
            dispatch.AssignedShuttleAnchor,
            dispatch.AssignedShuttleConsole,
            dispatch.AssignedReturnTarget);
    }

    private static void MergeRecoveryProvenance(
        PendingMedicalDispatch dispatch,
        PatientRecoveryTransfer recovery)
    {
        dispatch.PreservedOwnerLineage = true;
        if (recovery.RequiredOnboardHandoff)
        {
            // Required custody owns one coherent shuttle/home tuple. Mixing a
            // recovery field with an unrelated active/pending tuple can route
            // the patient to the wrong vessel or release point.
            dispatch.ReplacementAnchor = recovery.ReplacementAnchor;
            dispatch.AssignedShuttle = recovery.AssignedShuttle;
            dispatch.AssignedShuttleAnchor = recovery.AssignedShuttleAnchor;
            dispatch.AssignedShuttleConsole = recovery.AssignedShuttleConsole;
            dispatch.AssignedReturnTarget = recovery.AssignedReturnTarget;
            dispatch.RequiredOnboardHandoff = true;
            return;
        }

        dispatch.ReplacementAnchor ??= recovery.ReplacementAnchor;
        dispatch.AssignedShuttle ??= recovery.AssignedShuttle;
        dispatch.AssignedShuttleAnchor ??= recovery.AssignedShuttleAnchor;
        dispatch.AssignedShuttleConsole ??= recovery.AssignedShuttleConsole;
        dispatch.AssignedReturnTarget ??= recovery.AssignedReturnTarget;
        dispatch.RequiredOnboardHandoff |= recovery.RequiredOnboardHandoff;
    }

    private void ApplyRequiredHandoffInfrastructure(
        LuaMRescueAgentComponent rescue,
        EntityUid? assignedShuttle,
        EntityUid? assignedShuttleAnchor,
        EntityUid? assignedShuttleConsole,
        EntityUid? assignedReturnTarget)
    {
        rescue.AssignedShuttle = assignedShuttle is { Valid: true } shuttle && !Deleted(shuttle)
            ? shuttle
            : null;
        rescue.AssignedShuttleAnchor = assignedShuttleAnchor is { Valid: true } anchor && !Deleted(anchor)
            ? anchor
            : null;
        rescue.AssignedShuttleConsole = assignedShuttleConsole is { Valid: true } console && !Deleted(console)
            ? console
            : null;
        rescue.AssignedReturnTarget = assignedReturnTarget is { Valid: true } returnTarget && !Deleted(returnTarget)
            ? returnTarget
            : null;
    }

    private void ApplyReplacementInfrastructure(
        LuaMRescueAgentComponent rescue,
        EntityUid? assignedShuttle,
        EntityUid? assignedShuttleAnchor,
        EntityUid? assignedShuttleConsole,
        EntityUid? assignedReturnTarget)
    {
        if (assignedShuttle is { Valid: true } shuttle && !Deleted(shuttle))
            rescue.AssignedShuttle ??= shuttle;
        if (assignedShuttleAnchor is { Valid: true } anchor && !Deleted(anchor))
            rescue.AssignedShuttleAnchor ??= anchor;
        if (assignedShuttleConsole is { Valid: true } console && !Deleted(console))
            rescue.AssignedShuttleConsole ??= console;
        if (assignedReturnTarget is { Valid: true } returnTarget && !Deleted(returnTarget))
            rescue.AssignedReturnTarget ??= returnTarget;
    }

    private EntityUid ResolveReplacementAnchor(
        EntityUid agent,
        LuaMRescueAgentComponent rescue,
        EntityUid fallbackTarget)
    {
        if (rescue.AssignedShuttleAnchor is { Valid: true } shuttleAnchor && !Deleted(shuttleAnchor))
            return shuttleAnchor;
        if (rescue.AssignedShuttle is { Valid: true } shuttle && !Deleted(shuttle))
            return shuttle;

        var xform = Transform(agent);
        if (xform.GridUid is { Valid: true } grid && !Deleted(grid))
            return grid;
        if (xform.ParentUid is { Valid: true } parent && !Deleted(parent))
            return parent;
        return fallbackTarget;
    }

    private bool IsEntityOnGrid(EntityUid entity, EntityUid grid)
    {
        if (entity == grid || Deleted(entity) || Deleted(grid))
            return entity == grid;

        var xform = Transform(entity);
        return xform.GridUid == grid || xform.ParentUid == grid;
    }

    private bool IsTerminalDispatchResolved(EntityUid target, out string status)
    {
        if (Deleted(target))
        {
            status = "target was deleted";
            return true;
        }

        _terminalDispatches.TryGetValue(target, out var terminal);
        if (terminal != null &&
            TryFindLivingActiveRescueAgent(out var activeAgent, out var activeRescue) &&
            IsRescueAgentOperational(activeAgent) &&
            activeRescue.ActivityContext.Target == target &&
            activeRescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active)
        {
            ApplyDispatchProvenance(activeAgent, activeRescue, terminal, target);
            status = $"target now has authoritative activity ownership by rescue agent {GetNetEntity(activeAgent)}";
            return true;
        }

        // Recovery closes a medical episode, not a physical/home custody
        // obligation. A required terminal remains retryable until deletion,
        // explicit adoption, or authoritative automatic ownership.
        if (terminal?.RequiredOnboardHandoff == true)
        {
            status = "required physical handoff remains unresolved";
            return false;
        }

        if (terminal != null &&
            TryComp<MobStateComponent>(target, out var mobState) &&
            mobState.CurrentState == MobState.Alive)
        {
            var recoveredEpisode = terminal.Kind is LuaMRescueMedicalSignalKind.Critical
                or LuaMRescueMedicalSignalKind.Death;
            var recoveredFollowUp = terminal.Kind == LuaMRescueMedicalSignalKind.FollowUp &&
                                    TryComp<DamageableComponent>(target, out var damageable) &&
                                    damageable.TotalDamage.Float() <= 0f;
            if (recoveredEpisode || recoveredFollowUp)
            {
                status = "the medical episode represented by the terminal dispatch has recovered";
                ReleaseAutomaticSignalCooldowns(target);
                return true;
            }
        }

        status = "dispatch remains unresolved";
        return false;
    }

    private void PruneAutomaticDeathSignalCooldowns()
    {
        PruneAutomaticSignalCooldowns(_automaticDeathSignalCooldowns);
    }

    private void PruneAutomaticCriticalSignalCooldowns()
    {
        PruneAutomaticSignalCooldowns(_automaticCriticalSignalCooldowns);
    }

    private void ReleaseAutomaticSignalCooldowns(EntityUid target)
    {
        _automaticCriticalSignalCooldowns.Remove(target);
        _automaticDeathSignalCooldowns.Remove(target);
    }

    private void PruneAutomaticSignalCooldowns(Dictionary<EntityUid, TimeSpan> cooldowns)
    {
        if (cooldowns.Count == 0)
            return;

        var now = _timing.CurTime;
        foreach (var target in cooldowns
                     .Where(entry => Deleted(entry.Key) || entry.Value <= now)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            cooldowns.Remove(target);
        }
    }

    private bool TryResolveAutomaticMedicalSignalStation(EntityUid target, out EntityUid station)
    {
        if (_station.GetOwningStation(target) is { Valid: true } owningStation)
        {
            station = owningStation;
            return true;
        }

        var stations = _station.GetStationsSet();
        if (stations.Count == 1)
        {
            station = stations.First();
            return true;
        }

        station = default;
        return false;
    }

    private bool TryFindLivingActiveRescueAgent(
        out EntityUid agent,
        out LuaMRescueAgentComponent rescue)
    {
        var query = EntityQueryEnumerator<LuaMRescueAgentComponent>();
        while (query.MoveNext(out var uid, out var rescueComp))
        {
            if (Deleted(uid))
                continue;

            if (TryComp<MobStateComponent>(uid, out var mobState) &&
                mobState.CurrentState == MobState.Dead)
                continue;

            agent = uid;
            rescue = rescueComp;
            return true;
        }

        agent = default;
        rescue = default!;
        return false;
    }

    private bool TryResolveDispatchAgent(
        bool manualOverride,
        EntityUid? manualOverrideOwner,
        out EntityUid agent,
        out LuaMRescueAgentComponent rescue)
    {
        if (manualOverride &&
            manualOverrideOwner is { Valid: true } owner &&
            !Deleted(owner) &&
            TryComp<LuaMRescueAgentComponent>(owner, out var ownerRescue) &&
            (!TryComp<MobStateComponent>(owner, out var mobState) ||
             mobState.CurrentState != MobState.Dead))
        {
            agent = owner;
            rescue = ownerRescue;
            return true;
        }

        return TryFindLivingActiveRescueAgent(out agent, out rescue);
    }

    public void RetireDeadRescueAgents()
    {
        _rescueAgent.RetireDeadAgentsForReplacement("manual override owner retired");
    }

    private bool IsManualDispatchLineageCurrent(PendingMedicalDispatch pending, out string status)
    {
        if (!pending.ManualOverride ||
            pending.ManualOverrideOwner is not { Valid: true } owner ||
            pending.ManualOverrideGeneration == 0)
        {
            status = "missing owner or generation";
            return false;
        }

        if (Deleted(owner) ||
            !TryComp<LuaMRescueAgentComponent>(owner, out var rescue))
        {
            status = "owner is deleted or retired";
            return false;
        }

        if (TryComp<MobStateComponent>(owner, out var mobState) &&
            mobState.CurrentState == MobState.Dead)
        {
            status = "owner is dead";
            return false;
        }

        if (rescue.ManualOverrideTarget != pending.Target ||
            rescue.ManualOverrideGeneration != pending.ManualOverrideGeneration)
        {
            status = "owner has a newer explicit patient order";
            return false;
        }

        status = "current";
        return true;
    }

    private static void DowngradeManualDispatch(PendingMedicalDispatch pending, string status)
    {
        pending.ManualOverride = false;
        pending.ManualOverrideOwner = null;
        pending.ManualOverrideGeneration = 0;
        pending.LastStatus = status;
    }

    private void DowngradeManualDispatchesOwnedBy(EntityUid owner, string reason)
    {
        foreach (var pending in _pendingDispatches.Values)
        {
            if (pending.ManualOverride && pending.ManualOverrideOwner == owner)
            {
                DowngradeManualDispatch(
                    pending,
                    $"{reason}: {GetNetEntity(owner)}; awaiting automatic eligibility");
            }
        }

        foreach (var terminal in _terminalDispatches.Values)
        {
            if (terminal.ManualOverride && terminal.ManualOverrideOwner == owner)
            {
                DowngradeManualDispatch(
                    terminal,
                    $"{reason}: {GetNetEntity(owner)}; explicit automatic retry required");
            }
        }
    }

    private static bool HasActivePatientOwnershipForTarget(
        LuaMRescueAgentComponent rescue,
        EntityUid target)
    {
        return rescue.AssignedTarget == target ||
               rescue.DeathSignalTarget == target ||
               rescue.EvacuatingTarget == target ||
               rescue.OnboardCareTarget == target ||
               rescue.TaskPatientTarget == target ||
               rescue.ActivityContext.Target == target &&
               rescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active;
    }

    private bool IsPatientAwaitingLocalRecovery(
        EntityUid agent,
        LuaMRescueAgentComponent rescue,
        EntityUid target)
    {
        // Durable local-recovery state outranks legacy ownership mirrors. A
        // canonical Required handoff deliberately retains Assigned/Evacuating/
        // Task mirrors while its authoritative activity is Blocked; treating
        // those mirrors as active would let a repeated signal reopen it.
        if ((rescue.RequiredOnboardHandoffPatients.Contains(target) &&
             rescue.ActivityContext.Activity == LuaMRescueActivity.Handoff &&
             rescue.ActivityContext.Target == target &&
             rescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Blocked) ||
            rescue.DormantRouteTarget == target ||
            rescue.IgnoredOnboardPatients.Contains(target) ||
            rescue.TerminalPullFailures.ContainsKey(target) ||
            rescue.TerminalEvacuationUnbuckleFailures.ContainsKey(target) ||
            (rescue.TerminalPatientBuckleFailures.Keys.Any(pair => pair.Patient == target) &&
             !_rescueAgent.HasViablePatientDeliveryStrap(agent, rescue, target)))
        {
            return true;
        }

        if (!rescue.SkippedTargets.TryGetValue(target, out var skipUntil))
            return false;

        if (skipUntil > _timing.CurTime)
            return true;

        rescue.SkippedTargets.Remove(target);
        return false;
    }

    private static bool IsRescueAssignedToTarget(LuaMRescueAgentComponent rescue, EntityUid target)
    {
        return HasActivePatientOwnershipForTarget(rescue, target);
    }

    private static bool IsRescueAvailableForDispatch(LuaMRescueAgentComponent rescue)
    {
        return rescue.PendingPlayerAction == LuaMRescuePlayerActionKind.None &&
               rescue.AssignedTarget == null &&
               rescue.DeathSignalTarget == null &&
               rescue.EvacuatingTarget == null &&
               rescue.OnboardCareTarget == null &&
               rescue.TaskPatientTarget == null &&
               rescue.ManualOverrideTarget == null &&
               !(rescue.ActivityContext.Target is { Valid: true } &&
                 rescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active);
    }

    private static bool TryGetAssignedPatient(LuaMRescueAgentComponent rescue, out EntityUid target)
    {
        if (rescue.ActivityContext.Target is { Valid: true } activityTarget &&
            rescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active)
        {
            target = activityTarget;
            return true;
        }

        target = rescue.EvacuatingTarget ??
                 rescue.OnboardCareTarget ??
                 rescue.AssignedTarget ??
                 rescue.DeathSignalTarget ??
                 rescue.TaskPatientTarget ??
                 rescue.ManualOverrideTarget ??
                 default;
        return target.Valid;
    }

    private bool ShouldPreemptForCriticalSignal(
        EntityUid agent,
        EntityUid current,
        EntityUid candidate,
        bool manualOverride)
    {
        var requestKind = manualOverride
            ? LuaMRescuePatientRequestKind.Manual
            : LuaMRescuePatientRequestKind.AutomaticEvacuation;
        if (!_activity.IsEligibleRescuePatient(
                agent,
                candidate,
                requestKind,
                manualOverride,
                out _))
        {
            return false;
        }

        // Hysteresis is categorical: a few damage points or a slightly shorter
        // distance must never make two Critical patients steal the intent from
        // one another. Only a strictly higher urgency category may preempt.
        return _activity.GetPatientUrgency(candidate) > _activity.GetPatientUrgency(current);
    }

    private bool IsRescueAgentOperational(EntityUid agent)
    {
        return !TryComp<MobStateComponent>(agent, out var mobState) ||
               mobState.CurrentState == MobState.Alive;
    }

    private void AssignSignalToExistingAgent(
        EntityUid agent,
        LuaMRescueAgentComponent rescue,
        EntityUid target,
        LuaMRescueMedicalSignalKind kind,
        bool manualOverride)
    {
        if (manualOverride)
            _rescueAgent.EstablishManualOverrideTarget(agent, rescue, target);
        InitializeSignalAssignment(agent, rescue, target, routeToTarget: true);
        ApplySignalToAgent(agent, rescue, target, kind);
    }

    private bool InitializeSignalAssignment(
        EntityUid agent,
        LuaMRescueAgentComponent rescue,
        EntityUid target,
        bool routeToTarget)
    {
        _rescueAgent.PrepareForExternalIntentReplacement(agent, target);
        rescue.EvacuatingTarget = null;
        rescue.OnboardCareTarget = null;
        rescue.DeathSignalTarget = null;
        rescue.AssignedPatientStrap = null;
        rescue.AssignedTarget = target;
        rescue.TaskPatientTarget = target;
        rescue.TaskSupplyTarget = null;
        rescue.TaskStage = LuaMRescueTaskStage.FollowingPatient;
        rescue.ShuttleReturnRouted = false;
        rescue.ShuttleRoutedTarget = null;
        rescue.SkippedTargets.Remove(target);
        rescue.DeferredPatientTargets.Remove(target);

        _activity.BeginOrReplaceIntent(
            agent,
            LuaMRescueRole.Aibolit,
            LuaMRescueActivity.Dispatching,
            target,
            new EntityCoordinates(target, Vector2.Zero),
            out _);

        EntityUid? autopilotConsole = null;
        var routed = routeToTarget &&
                     rescue.AssignedShuttle is { Valid: true } shuttle &&
                     !Deleted(shuttle) &&
                     TrySetAutopilotTarget(shuttle, target, out autopilotConsole);
        if (routed)
        {
            rescue.AssignedShuttleConsole = autopilotConsole;
            rescue.ShuttleRoutedTarget = target;
        }

        Dirty(agent, rescue);
        return routed;
    }

    private void ApplySignalToAgent(
        EntityUid agent,
        LuaMRescueAgentComponent rescue,
        EntityUid target,
        LuaMRescueMedicalSignalKind kind)
    {
        if (kind == LuaMRescueMedicalSignalKind.Death)
        {
            rescue.DeathSignalTarget = target;
            rescue.DeathSignalDispatchReported = false;
            SendDispatchRadio(agent, target);
            MarkDeathSignalDispatchReported(agent, rescue, target);
            return;
        }

        if (kind == LuaMRescueMedicalSignalKind.FollowUp)
        {
            SendFollowUpDispatchRadio(agent, target);
            MarkFollowUpDispatchReported(agent, target);
            return;
        }

        SendCriticalDispatchRadio(agent, target);
        MarkCriticalSignalDispatchReported(agent, target);
        Dirty(agent, rescue);
    }

    private void BeginFreshSignalEpisodeIntent(
        EntityUid agent,
        LuaMRescueAgentComponent rescue,
        EntityUid target)
    {
        _rescueAgent.PrepareForExternalIntentReplacement(agent, target);
        var activity = rescue.OnboardCareTarget == target
            ? LuaMRescueActivity.OnboardCare
            : rescue.EvacuatingTarget == target
                ? LuaMRescueActivity.PreparingEvacuation
                : LuaMRescueActivity.Dispatching;
        var destination = rescue.OnboardCareTarget == target &&
                          rescue.AssignedPatientStrap is { Valid: true } strap &&
                          !Deleted(strap)
            ? new EntityCoordinates(strap, Vector2.Zero)
            : new EntityCoordinates(target, Vector2.Zero);
        _activity.BeginOrReplaceIntent(
            agent,
            rescue.ActivityRole == LuaMRescueRole.None ? LuaMRescueRole.Aibolit : rescue.ActivityRole,
            activity,
            target,
            destination,
            out _);
        rescue.AssignedTarget ??= target;
        rescue.TaskPatientTarget ??= target;
        if (rescue.TaskStage == LuaMRescueTaskStage.None)
            rescue.TaskStage = LuaMRescueTaskStage.FollowingPatient;
        Dirty(agent, rescue);
    }

    public bool TryDispatchRescueShuttle(
        EntityUid station,
        VesselPrototype vessel,
        EntityUid? followTarget,
        ICommonSession? controller,
        bool spawnAgent,
        bool spawnTeam,
        bool control,
        bool routeToTarget,
        bool deathSignal,
        bool manualOverride,
        out EntityUid? shuttle,
        out EntityUid? agent,
        out EntityUid? autopilotConsole,
        out int escortCount,
        out string status)
    {
        shuttle = null;
        agent = null;
        autopilotConsole = null;
        escortCount = 0;
        status = string.Empty;

        if (deathSignal)
        {
            if (!spawnAgent)
            {
                status = "Death signal dispatch requires a rescue agent so Aibolit can report who it is flying to.";
                return false;
            }

            if (followTarget is not { Valid: true } reportTarget ||
                Deleted(reportTarget))
            {
                status = "Death signal dispatch requires target=<entity|player> so Aibolit can report who it is flying to.";
                return false;
            }
        }

        if (!HasComp<StationDataComponent>(station))
        {
            status = "Target is not a station.";
            return false;
        }

        if (spawnAgent)
            RetireDeadRescueAgents();

        if (spawnAgent &&
            TryFindLivingActiveRescueAgent(out var activeAgent, out var activeRescue))
        {
            shuttle = activeRescue.AssignedShuttle;
            agent = activeAgent;
            autopilotConsole = activeRescue.AssignedShuttleConsole;
            status =
                $"Only one Aibolit rescue agent may be active. Existing agent={GetNetEntity(activeAgent)}; " +
                "new rescue shuttle purchase and agent spawn blocked.";
            return false;
        }

        if (!deathSignal &&
            spawnAgent &&
            spawnTeam &&
            _sectorStory.TryGetActiveRescueCooldown(out var remainingSeconds, out var cooldownStatus))
        {
            status =
                $"Rescue dispatch cooldown active for {remainingSeconds}s after the last after-action: {cooldownStatus}. " +
                "Use --death-signal for a confirmed death signal or wait before launching another full rescue team.";
            return false;
        }

        if (!_shipyard.TryPurchaseShuttle(station, vessel.ShuttlePath, out shuttle))
        {
            status = $"Failed to purchase rescue vessel {vessel.ID} from {vessel.ShuttlePath}.";
            return false;
        }

        var shuttleName = $"LuaM Rescue {vessel.Name}";
        _metaData.SetEntityName(shuttle.Value, shuttleName);
        if (_station.GetOwningStation(shuttle.Value) is { Valid: true } shuttleStation)
            _station.RenameStation(shuttleStation, shuttleName, loud: false);

        if (TryFindAutopilotConsole(shuttle.Value, out var shuttleConsoleEntity, out _, out _))
            autopilotConsole = shuttleConsoleEntity;

        var returnTarget = TryFindStationReturnTarget(station, out var stationReturnTarget)
            ? stationReturnTarget
            : (EntityUid?) null;

        var routed = false;
        if (!spawnAgent &&
            routeToTarget &&
            followTarget is { Valid: true } routeTarget)
        {
            routed = TrySetAutopilotTarget(shuttle.Value, routeTarget, out autopilotConsole);
        }

        if (!spawnAgent)
        {
            status = BuildStatus(shuttleName, deployedAgent: false, deployedEscorts: 0, routeRequested: routeToTarget && followTarget != null, routed);
            return true;
        }

        var anchor = TryFindShuttleAnchor(shuttle.Value, out var anchorEntity)
            ? anchorEntity
            : shuttle.Value;

        // A fresh Aibolit must receive its shuttle/return ownership before any
        // cross-grid patient intent can start. Otherwise SpawnAgent's immediate
        // follow can terminalize against a shuttle that is not assigned yet.
        if (!_rescueAgent.TrySpawnAgent(anchor, null, controller, control, out var spawnedAgent, out var spawnStatus))
        {
            status = spawnStatus;
            agent = spawnedAgent;
            return false;
        }

        agent = spawnedAgent;
        var rescue = EnsureComp<LuaMRescueAgentComponent>(agent.Value);
        rescue.AssignedShuttle = shuttle;
        rescue.AssignedShuttleAnchor = anchor;
        rescue.AssignedShuttleConsole = autopilotConsole;
        rescue.AssignedReturnTarget = returnTarget;
        if (followTarget is { Valid: true } assignedTarget)
        {
            if (manualOverride)
                _rescueAgent.EstablishManualOverrideTarget(agent.Value, rescue, assignedTarget);
            routed = InitializeSignalAssignment(
                agent.Value,
                rescue,
                assignedTarget,
                routeToTarget);

            if (deathSignal)
            {
                ApplySignalToAgent(
                    agent.Value,
                    rescue,
                    assignedTarget,
                    LuaMRescueMedicalSignalKind.Death);
            }
        }
        Dirty(agent.Value, rescue);

        if (spawnTeam)
        {
            var escorts = _rescueTeam.SpawnEscortTeam(agent.Value, anchor, followTarget, shuttle, anchor);
            escortCount = escorts.Count;
        }

        status = BuildStatus(shuttleName, deployedAgent: true, deployedEscorts: escortCount, routeRequested: routeToTarget && followTarget != null, routed);
        return true;
    }

    private void SendDispatchRadio(EntityUid agent, EntityUid target)
    {
        var targetName = Name(target);
        _radio.SendRadioMessage(
            agent,
            $"Медсигнал смерти принят. Вылетаю к пациенту {targetName}.",
            MedicalRadioChannel,
            agent);
    }

    private void SendCriticalDispatchRadio(EntityUid agent, EntityUid target)
    {
        var targetName = Name(target);
        _radio.SendRadioMessage(
            agent,
            $"\u041a\u0440\u0438\u0442\u0438\u0447\u0435\u0441\u043a\u0438\u0439 \u043c\u0435\u0434\u0441\u0438\u0433\u043d\u0430\u043b \u043f\u0440\u0438\u043d\u044f\u0442. \u0412\u044b\u043b\u0435\u0442\u0430\u044e \u043a \u043f\u0430\u0446\u0438\u0435\u043d\u0442\u0443 {targetName}.",
            MedicalRadioChannel,
            agent);
    }

    private void SendFollowUpDispatchRadio(EntityUid agent, EntityUid target)
    {
        _radio.SendRadioMessage(
            agent,
            $"Возвращаюсь к отложенному пациенту {Name(target)}. Предыдущий результат учтён.",
            MedicalRadioChannel,
            agent);
    }

    private void MarkDeathSignalDispatchReported(EntityUid agent, LuaMRescueAgentComponent rescue, EntityUid target)
    {
        rescue.DeathSignalDispatchReported = true;
        rescue.LastAutoCommsKey = $"death-signal-dispatch:{target}";
        rescue.NextAutoCommsAt = _timing.CurTime + TimeSpan.FromSeconds(Math.Max(0.1f, rescue.AutoCommsCooldown));
        rescue.LastRescueSpeechKey = rescue.LastAutoCommsKey;
        rescue.NextRescueSpeechAt = _timing.CurTime + TimeSpan.FromSeconds(Math.Max(0.1f, rescue.RescueSpeechCooldown));
        rescue.LastRescueSpeechStatus = $"speech:auto-comms:{rescue.LastAutoCommsKey}; cooldown={rescue.RescueSpeechCooldown:0}s";
        Dirty(agent, rescue);
    }

    private void MarkCriticalSignalDispatchReported(EntityUid agent, EntityUid target)
    {
        if (!TryComp<LuaMRescueAgentComponent>(agent, out var rescue))
            return;

        rescue.LastAutoCommsKey = $"critical-signal-dispatch:{target}";
        rescue.NextAutoCommsAt = _timing.CurTime + TimeSpan.FromSeconds(Math.Max(0.1f, rescue.AutoCommsCooldown));
        rescue.LastRescueSpeechKey = rescue.LastAutoCommsKey;
        rescue.NextRescueSpeechAt = _timing.CurTime + TimeSpan.FromSeconds(Math.Max(0.1f, rescue.RescueSpeechCooldown));
        rescue.LastRescueSpeechStatus = $"speech:auto-comms:{rescue.LastAutoCommsKey}; cooldown={rescue.RescueSpeechCooldown:0}s";
        Dirty(agent, rescue);
    }

    private void MarkFollowUpDispatchReported(EntityUid agent, EntityUid target)
    {
        if (!TryComp<LuaMRescueAgentComponent>(agent, out var rescue))
            return;

        rescue.LastAutoCommsKey = $"follow-up-dispatch:{target}";
        rescue.NextAutoCommsAt = _timing.CurTime + TimeSpan.FromSeconds(Math.Max(0.1f, rescue.AutoCommsCooldown));
        rescue.LastRescueSpeechKey = rescue.LastAutoCommsKey;
        rescue.NextRescueSpeechAt = _timing.CurTime + TimeSpan.FromSeconds(Math.Max(0.1f, rescue.RescueSpeechCooldown));
        rescue.LastRescueSpeechStatus = $"speech:auto-comms:{rescue.LastAutoCommsKey}; cooldown={rescue.RescueSpeechCooldown:0}s";
        Dirty(agent, rescue);
    }

    public bool TrySetAutopilotTarget(EntityUid shuttle, EntityUid target, out EntityUid? autopilotConsole)
    {
        return TrySetAutopilotTarget(
            shuttle,
            target,
            LuaMRescueActivity.Delivering,
            out autopilotConsole);
    }

    public bool TrySetAutopilotTarget(
        EntityUid shuttle,
        EntityUid target,
        LuaMRescueActivity routeActivity,
        out EntityUid? autopilotConsole)
    {
        autopilotConsole = null;
        if (!shuttle.Valid || Deleted(shuttle))
            return false;

        var lifecycle = EnsureComp<LuaMRescueShuttleLifecycleComponent>(shuttle);
        EnsureAutopilotRoleProfile(lifecycle);
        var newIntent = lifecycle.Target != target || lifecycle.RouteActivity != routeActivity;
        if (newIntent)
            lifecycle.RetryCount = 0;

        if (!newIntent && lifecycle.State != LuaMRescueShuttleRouteState.None)
        {
            // Route creation is idempotent. Automatic and explicit retries have
            // dedicated entry points that increment RetryCount; issuing attempt
            // zero again here would reset deadlines and defeat the terminal cap.
            autopilotConsole = lifecycle.AutopilotConsole;
            var active = lifecycle.State is LuaMRescueShuttleRouteState.Routing
                or LuaMRescueShuttleRouteState.Arrived
                or LuaMRescueShuttleRouteState.Docked;
            var retryable = (lifecycle.State is LuaMRescueShuttleRouteState.Failed
                    or LuaMRescueShuttleRouteState.TimedOut) &&
                lifecycle.RetryCount < lifecycle.EffectiveMaxRetries;
            return active || retryable;
        }

        return TryIssueAutopilotRoute(
            shuttle,
            target,
            routeActivity,
            lifecycle,
            countAsRetry: false,
            out autopilotConsole,
            out _);
    }

    public bool TryGetRouteSnapshot(EntityUid shuttle, out LuaMRescueShuttleRouteSnapshot snapshot)
    {
        if (!TryComp<LuaMRescueShuttleLifecycleComponent>(shuttle, out var lifecycle))
        {
            snapshot = default;
            return false;
        }

        EnsureAutopilotRoleProfile(lifecycle);
        SyncAutopilotActivityContext(shuttle, lifecycle, remainingDistance: null);
        snapshot = ToRouteSnapshot(shuttle, lifecycle);
        return true;
    }

    /// <summary>
    /// Manually retries a failed/timed-out route. If automatic retries were exhausted, this starts a fresh
    /// retry budget while preserving the monotonically increasing route generation.
    /// </summary>
    public bool TryRetryAutopilotRoute(EntityUid shuttle, out string status)
    {
        if (!TryComp<LuaMRescueShuttleLifecycleComponent>(shuttle, out var lifecycle))
        {
            status = "shuttle has no rescue route lifecycle";
            return false;
        }

        if (lifecycle.State is not (LuaMRescueShuttleRouteState.Failed or LuaMRescueShuttleRouteState.TimedOut))
        {
            status = $"route state {lifecycle.State} is not retryable";
            return false;
        }

        if (lifecycle.Target is not { Valid: true } target || Deleted(target))
        {
            status = "route target is missing or deleted";
            return false;
        }

        EnsureAutopilotRoleProfile(lifecycle);
        if (lifecycle.RetryCount >= lifecycle.EffectiveMaxRetries)
            lifecycle.RetryCount = -1;

        return TryIssueAutopilotRoute(
            shuttle,
            target,
            lifecycle.RouteActivity,
            lifecycle,
            countAsRetry: true,
            out _,
            out status);
    }

    private bool TryIssueAutopilotRoute(
        EntityUid shuttle,
        EntityUid target,
        LuaMRescueActivity routeActivity,
        LuaMRescueShuttleLifecycleComponent lifecycle,
        bool countAsRetry,
        out EntityUid? autopilotConsole,
        out string status)
    {
        autopilotConsole = null;
        var now = _timing.CurTime;

        if (!shuttle.Valid || Deleted(shuttle))
        {
            status = "shuttle is missing or deleted";
            return false;
        }

        var profile = EnsureAutopilotRoleProfile(lifecycle);
        lifecycle.Target = target.Valid ? target : null;
        lifecycle.RouteActivity = routeActivity;
        lifecycle.RouteGeneration++;
        if (countAsRetry)
            lifecycle.RetryCount++;

        // A route generation owns exactly one HTN execution. Shut down the
        // previous operator before validation so even a rejected replacement
        // cannot leave the shuttle steering toward the stale target.
        StopAutopilotForDocking(lifecycle);
        lifecycle.RouteStartedAt = now;
        lifecycle.AutopilotConsole = null;
        lifecycle.DockingAttemptCount = 0;
        lifecycle.NextDockingAttemptAt = TimeSpan.Zero;
        lifecycle.SafeExitConfirmed = false;
        lifecycle.RouteFailureReason = LuaMRescueFailureReason.None;

        if (!IsAutopilotRouteActivity(routeActivity) ||
            !profile.Allows(routeActivity) ||
            !profile.TryGetPolicy(routeActivity, out var routePolicy) ||
            routePolicy.Timeout <= TimeSpan.Zero)
        {
            lifecycle.RetryCount = lifecycle.EffectiveMaxRetries;
            lifecycle.RouteDeadline = now;
            status = $"autopilot profile does not allow route activity {routeActivity}";
            SetRouteState(
                shuttle,
                lifecycle,
                LuaMRescueShuttleRouteState.Failed,
                status,
                LuaMRescueFailureReason.RoleDisallowed);
            return false;
        }

        var configuredTimeout = TimeSpan.FromSeconds(Math.Max(1f, lifecycle.RouteTimeoutSeconds));
        var effectiveTimeout = configuredTimeout <= routePolicy.Timeout
            ? configuredTimeout
            : routePolicy.Timeout;
        lifecycle.RouteDeadline = now + effectiveTimeout;
        SetAutopilotPlanningContext(shuttle, lifecycle);

        if (!target.Valid || Deleted(target))
        {
            lifecycle.RetryCount = lifecycle.EffectiveMaxRetries;
            lifecycle.NextRetryAt = TimeSpan.Zero;
            status = "route target is missing or deleted";
            SetRouteState(
                shuttle,
                lifecycle,
                LuaMRescueShuttleRouteState.Failed,
                status,
                LuaMRescueFailureReason.TargetLost);
            return false;
        }

        // A freshly purchased rescue shuttle is normally docked to its owning
        // station. If the routed patient is on that grid, departure would only
        // remove an already-confirmed safe exit and force a pointless round trip.
        if (IsShuttleDockedToTargetGrid(shuttle, target))
        {
            lifecycle.SafeExitConfirmed = true;
            lifecycle.NextRetryAt = TimeSpan.Zero;
            status = "shuttle is already docked to the target grid; safe exit confirmed";
            SetRouteState(shuttle, lifecycle, LuaMRescueShuttleRouteState.Docked, status);
            return true;
        }

        if (!TryFindAutopilotConsole(shuttle, out var console, out var shuttleConsole, out var htn))
        {
            lifecycle.NextRetryAt = lifecycle.RetryCount < lifecycle.EffectiveMaxRetries
                ? now + GetAutopilotRetryDelay(lifecycle)
                : TimeSpan.Zero;
            status = "autopilot console with HTN controller was not found on the shuttle";
            SetRouteState(shuttle, lifecycle, LuaMRescueShuttleRouteState.Failed, status);
            return false;
        }

        // Rescue routes are world-level assignments and must keep flying even
        // when the patient and shuttle are outside every player's proximity
        // bubble. The route lifecycle itself owns the bounded timeout/retry
        // policy, so the generic NPC sleep sweep must not suspend this HTN.
        htn.PauseWhenNoPlayersInRange = false;
        _npc.SleepNPC(console, htn);
        htn.Blackboard.Remove<EntityCoordinates>(shuttleConsole.AutopilotTargetKey);
        htn.Blackboard.Remove<Angle>(shuttleConsole.AutopilotRotationKey);

        if (!TryPrepareSafeDeparture(shuttle, out var departureStatus, out var departed))
        {
            lifecycle.NextRetryAt = lifecycle.RetryCount < lifecycle.EffectiveMaxRetries
                ? now + GetAutopilotRetryDelay(lifecycle)
                : TimeSpan.Zero;
            status = $"safe departure failed: {departureStatus}";
            SetRouteState(shuttle, lifecycle, LuaMRescueShuttleRouteState.Failed, status);
            return false;
        }

        _npc.SetBlackboard(console, shuttleConsole.AutopilotTargetKey, new EntityCoordinates(target, Vector2.Zero), htn);
        _npc.WakeNPC(console, htn);

        autopilotConsole = console;
        lifecycle.AutopilotConsole = console;
        lifecycle.NextRetryAt = TimeSpan.Zero;
        status = countAsRetry
            ? $"{routeActivity} attempt {lifecycle.RetryCount + 1}/{lifecycle.EffectiveMaxRetries + 1} issued"
            : $"autopilot {routeActivity} route issued";
        if (departed)
            status += $"; {departureStatus}";
        SetRouteState(shuttle, lifecycle, LuaMRescueShuttleRouteState.Routing, status);
        return true;
    }

    private bool TryPrepareSafeDeparture(EntityUid shuttle, out string status, out bool departed)
    {
        departed = false;
        var dockedPorts = _docking.GetDocks(shuttle)
            .Where(port => port.Comp.DockedWith is { Valid: true })
            .ToArray();
        if (dockedPorts.Length == 0)
        {
            status = "shuttle was already clear of all docks";
            return true;
        }

        if (!_docking.CanShuttleUndock(shuttle))
        {
            status = "shuttle departure is prohibited by PreventPilot policy";
            return false;
        }

        if (TryComp<FTLComponent>(shuttle, out var ftl) &&
            (ftl.State & (FTLState.Starting | FTLState.Travelling | FTLState.Arriving)) != 0)
        {
            status = $"shuttle cannot safely undock while FTL state is {ftl.State}";
            return false;
        }

        foreach (var port in dockedPorts)
        {
            if (!_docking.CanUndock((port.Owner, (DockingComponent?) port.Comp)) ||
                port.Comp.DockedWith is not { Valid: true } otherPort ||
                Deleted(otherPort) ||
                !TryComp<DockingComponent>(otherPort, out var reciprocal) ||
                reciprocal.DockedWith != port.Owner)
            {
                status = $"docking port {GetNetEntity(port.Owner)} has no valid reciprocal undock state";
                return false;
            }
        }

        foreach (var port in dockedPorts)
            _docking.Undock(port);

        foreach (var port in dockedPorts)
        {
            if (port.Comp.DockedWith != null)
            {
                status = $"docking port {GetNetEntity(port.Owner)} remained connected after undock";
                return false;
            }
        }

        departed = true;
        status = $"safely undocked {dockedPorts.Length} port pair(s) with docking airlock closure policy";
        return true;
    }

    private void UpdateRouteLifecycles()
    {
        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<LuaMRescueShuttleLifecycleComponent>();
        while (query.MoveNext(out var shuttle, out var lifecycle))
        {
            EnsureAutopilotRoleProfile(lifecycle);
            if (lifecycle.State == LuaMRescueShuttleRouteState.None)
                continue;

            if (lifecycle.Target is not { Valid: true } target || Deleted(target))
            {
                lifecycle.RetryCount = lifecycle.EffectiveMaxRetries;
                lifecycle.NextRetryAt = TimeSpan.Zero;
                SetRouteState(
                    shuttle,
                    lifecycle,
                    LuaMRescueShuttleRouteState.Failed,
                    "route target disappeared",
                    LuaMRescueFailureReason.TargetLost);
                continue;
            }

            var docked = IsShuttleDockedToTargetGrid(shuttle, target);
            if (lifecycle.SafeExitConfirmed != docked)
            {
                lifecycle.SafeExitConfirmed = docked;
                Dirty(shuttle, lifecycle);
            }

            var hasDistance = TryGetRouteDistance(shuttle, target, out var distance);
            SyncAutopilotActivityContext(
                shuttle,
                lifecycle,
                hasDistance ? distance : null);
            switch (lifecycle.State)
            {
                case LuaMRescueShuttleRouteState.Routing:
                    if (docked)
                    {
                        SetRouteState(shuttle, lifecycle, LuaMRescueShuttleRouteState.Docked, "shuttle docked to target grid");
                        break;
                    }

                    if (hasDistance && distance <= lifecycle.ArrivalRange)
                    {
                        StopAutopilotForDocking(lifecycle);
                        lifecycle.NextDockingAttemptAt = now;
                        SetRouteState(
                            shuttle,
                            lifecycle,
                            LuaMRescueShuttleRouteState.Arrived,
                            $"shuttle arrived within {distance:0.0}m; awaiting confirmed safe docking");
                        break;
                    }

                    if (lifecycle.AutopilotConsole is not { Valid: true } console || Deleted(console))
                    {
                        ScheduleRouteRetry(shuttle, lifecycle, LuaMRescueShuttleRouteState.Failed, "autopilot console disappeared");
                        break;
                    }

                    if (now >= lifecycle.RouteDeadline)
                    {
                        ScheduleRouteRetry(shuttle, lifecycle, LuaMRescueShuttleRouteState.TimedOut, "autopilot route timed out");
                    }
                    break;

                case LuaMRescueShuttleRouteState.Arrived:
                    if (docked)
                    {
                        SetRouteState(shuttle, lifecycle, LuaMRescueShuttleRouteState.Docked, "shuttle docked to target grid");
                        break;
                    }

                    if (!hasDistance || distance > lifecycle.ArrivalRange + 10f)
                    {
                        ScheduleRouteRetry(shuttle, lifecycle, LuaMRescueShuttleRouteState.Failed, "target moved outside arrival range");
                        break;
                    }

                    if (now >= lifecycle.RouteDeadline)
                    {
                        ScheduleRouteRetry(
                            shuttle,
                            lifecycle,
                            LuaMRescueShuttleRouteState.TimedOut,
                            "shuttle arrived but docking was not confirmed before the route deadline");
                        break;
                    }

                    if (now >= lifecycle.NextDockingAttemptAt)
                    {
                        if (!TryCompleteConfirmedDocking(shuttle, target, lifecycle, out var dockingStatus))
                        {
                            lifecycle.DockingAttemptCount++;
                            var maxDockingAttempts = Math.Max(1, lifecycle.MaxDockingAttempts);
                            if (lifecycle.DockingAttemptCount >= maxDockingAttempts)
                            {
                                ScheduleRouteRetry(
                                    shuttle,
                                    lifecycle,
                                    LuaMRescueShuttleRouteState.Failed,
                                    $"safe docking failed after {lifecycle.DockingAttemptCount}/{maxDockingAttempts} attempts: {dockingStatus}");
                                break;
                            }

                            lifecycle.NextDockingAttemptAt = now + TimeSpan.FromSeconds(
                                Math.Max(0.1f, lifecycle.DockingRetryDelaySeconds));
                            SetRouteState(
                                shuttle,
                                lifecycle,
                                LuaMRescueShuttleRouteState.Arrived,
                                $"safe docking attempt {lifecycle.DockingAttemptCount}/{maxDockingAttempts} failed: {dockingStatus}");
                            break;
                        }

                        SetRouteState(
                            shuttle,
                            lifecycle,
                            LuaMRescueShuttleRouteState.Docked,
                            dockingStatus);
                        break;
                    }
                    break;

                case LuaMRescueShuttleRouteState.Docked:
                    if (!docked)
                    {
                        if (hasDistance && distance <= lifecycle.ArrivalRange)
                        {
                            SetRouteState(shuttle, lifecycle, LuaMRescueShuttleRouteState.Arrived, "shuttle undocked but remains at target");
                        }
                        else
                        {
                            ScheduleRouteRetry(shuttle, lifecycle, LuaMRescueShuttleRouteState.Failed, "shuttle undocked away from target");
                        }
                    }
                    break;

                case LuaMRescueShuttleRouteState.Failed:
                case LuaMRescueShuttleRouteState.TimedOut:
                    if (lifecycle.RetryCount >= lifecycle.EffectiveMaxRetries || now < lifecycle.NextRetryAt)
                        break;

                    TryIssueAutopilotRoute(
                        shuttle,
                        target,
                        lifecycle.RouteActivity,
                        lifecycle,
                        countAsRetry: true,
                        out _,
                        out _);
                    break;
            }
        }
    }

    private void ScheduleRouteRetry(
        EntityUid shuttle,
        LuaMRescueShuttleLifecycleComponent lifecycle,
        LuaMRescueShuttleRouteState state,
        string status)
    {
        lifecycle.NextDockingAttemptAt = TimeSpan.Zero;
        lifecycle.NextRetryAt = lifecycle.RetryCount < lifecycle.EffectiveMaxRetries
            ? _timing.CurTime + GetAutopilotRetryDelay(lifecycle)
            : TimeSpan.Zero;
        SetRouteState(shuttle, lifecycle, state, status);
    }

    private bool TryCompleteConfirmedDocking(
        EntityUid shuttle,
        EntityUid target,
        LuaMRescueShuttleLifecycleComponent lifecycle,
        out string status)
    {
        lifecycle.SafeExitConfirmed = false;
        StopAutopilotForDocking(lifecycle);

        if (!TryResolveTargetGrid(target, out var targetGrid))
        {
            status = "target is not attached to a valid docking grid";
            return false;
        }

        if (targetGrid == shuttle)
        {
            lifecycle.SafeExitConfirmed = true;
            status = "route target is already safely aboard the shuttle";
            return true;
        }

        if (!HasComp<MapGridComponent>(shuttle) ||
            !HasComp<FixturesComponent>(shuttle))
        {
            status = "rescue shuttle has no dockable map-grid geometry";
            return false;
        }

        if (!HasComp<MapGridComponent>(targetGrid))
        {
            status = "target docking grid is unavailable";
            return false;
        }

        var config = _docking.GetDockingConfig(
            shuttle,
            targetGrid,
            dockType: DockType.Airlock);
        if (config == null)
        {
            status = "no compatible free airlock or collision-free docking configuration";
            return false;
        }

        _shuttle.FTLDock((shuttle, Transform(shuttle)), config);
        lifecycle.SafeExitConfirmed = IsShuttleDockedToTargetGrid(shuttle, target);
        if (!lifecycle.SafeExitConfirmed)
        {
            status = "docking command completed without reciprocal dock confirmation";
            return false;
        }

        status = $"shuttle docked to target grid via {config.Docks.Count} confirmed airlock pair(s); safe exit confirmed";
        return true;
    }

    private void StopAutopilotForDocking(LuaMRescueShuttleLifecycleComponent lifecycle)
    {
        if (lifecycle.AutopilotConsole is not { Valid: true } console ||
            Deleted(console) ||
            !TryComp<HTNComponent>(console, out var htn) ||
            !TryComp<ShuttleConsoleComponent>(console, out var shuttleConsole))
        {
            return;
        }

        _npc.SleepNPC(console, htn);
        htn.Blackboard.Remove<EntityCoordinates>(shuttleConsole.AutopilotTargetKey);
        htn.Blackboard.Remove<Angle>(shuttleConsole.AutopilotRotationKey);
    }

    private bool TryResolveTargetGrid(EntityUid target, out EntityUid targetGrid)
    {
        if (HasComp<MapGridComponent>(target))
        {
            targetGrid = target;
            return true;
        }

        if (Transform(target).GridUid is { Valid: true } grid && !Deleted(grid))
        {
            targetGrid = grid;
            return true;
        }

        targetGrid = default;
        return false;
    }

    private bool IsShuttleDockedToTargetGrid(EntityUid shuttle, EntityUid target)
    {
        if (!TryResolveTargetGrid(target, out var targetGrid))
            return false;

        // Once the routed patient has been brought onto the shuttle, the forward
        // delivery is complete. Treating this as an undock would otherwise turn a
        // successful rescue into endless route-to-self retries during onboard care.
        if (targetGrid == shuttle)
            return true;

        var query = EntityQueryEnumerator<DockingComponent, TransformComponent>();
        while (query.MoveNext(out var dockUid, out var docking, out var xform))
        {
            if (xform.GridUid != shuttle ||
                docking.DockedWith is not { Valid: true } otherDock ||
                Deleted(otherDock) ||
                !TryComp<DockingComponent>(otherDock, out var reciprocal) ||
                reciprocal.DockedWith != dockUid ||
                docking.PathfindHandle < 0 ||
                reciprocal.PathfindHandle != docking.PathfindHandle)
            {
                continue;
            }

            if (Transform(otherDock).GridUid == targetGrid)
                return true;
        }

        return false;
    }

    private bool TryGetRouteDistance(EntityUid shuttle, EntityUid target, out float distance)
    {
        distance = float.PositiveInfinity;
        if (Deleted(shuttle) || Deleted(target))
            return false;

        return Transform(shuttle).Coordinates.TryDistance(EntityManager, Transform(target).Coordinates, out distance);
    }

    private static LuaMRescueRoleProfile EnsureAutopilotRoleProfile(
        LuaMRescueShuttleLifecycleComponent lifecycle)
    {
        if (lifecycle.ActivityRoleProfile.Role != LuaMRescueRole.Autopilot)
        {
            lifecycle.ActivityRoleProfile =
                LuaMRescueRoleProfile.CreateDefault(LuaMRescueRole.Autopilot);
        }

        return lifecycle.ActivityRoleProfile;
    }

    private static bool IsAutopilotRouteActivity(LuaMRescueActivity activity)
    {
        return activity is LuaMRescueActivity.Delivering or LuaMRescueActivity.Returning;
    }

    private static TimeSpan GetAutopilotRetryDelay(LuaMRescueShuttleLifecycleComponent lifecycle)
    {
        var profile = EnsureAutopilotRoleProfile(lifecycle);
        var configuredSeconds = Math.Max(0.1, lifecycle.RetryDelaySeconds);
        var profileCapSeconds = Math.Max(0.1, profile.MaxRetryBackoff.TotalSeconds);
        return TimeSpan.FromSeconds(Math.Min(configuredSeconds, profileCapSeconds));
    }

    private void SetAutopilotPlanningContext(
        EntityUid shuttle,
        LuaMRescueShuttleLifecycleComponent lifecycle)
    {
        var now = _timing.CurTime;
        var context = lifecycle.ActivityContext;
        context.Activity = LuaMRescueActivity.PlanningRoute;
        context.TerminalStatus = LuaMRescueTerminalStatus.Active;
        context.Target = lifecycle.Target;
        context.Destination = lifecycle.Target is { Valid: true } target && !Deleted(target)
            ? new EntityCoordinates(target, Vector2.Zero)
            : null;
        context.StartedAt = lifecycle.RouteStartedAt;
        context.LastProgressAt = now;
        context.LastProgressDistance = null;
        context.Attempts = Math.Max(1, lifecycle.RetryCount + 1);
        context.Generation = (uint) Math.Max(0, lifecycle.RouteGeneration);
        context.Blocked = false;
        context.Fallback = LuaMRescueActivity.Standby;
        context.FailureReason = LuaMRescueFailureReason.None;
        context.RetryNotBefore = TimeSpan.Zero;
        context.RouteStatus = LuaMRescueRouteStatus.Planning;
        context.DoAfterStatus = LuaMRescueDoAfterStatus.None;
        context.LastTransitionAt = now;
        context.Deadline = lifecycle.RouteDeadline;
        Dirty(shuttle, lifecycle);
    }

    private void SyncAutopilotActivityContext(
        EntityUid shuttle,
        LuaMRescueShuttleLifecycleComponent lifecycle,
        float? remainingDistance)
    {
        var profile = EnsureAutopilotRoleProfile(lifecycle);
        var context = lifecycle.ActivityContext;
        var previousActivity = context.Activity;
        var previousTerminal = context.TerminalStatus;
        var previousTarget = context.Target;
        var previousDestination = context.Destination;
        var previousStartedAt = context.StartedAt;
        var previousProgressAt = context.LastProgressAt;
        var previousDistance = context.LastProgressDistance;
        var previousAttempts = context.Attempts;
        var previousGeneration = context.Generation;
        var previousBlocked = context.Blocked;
        var previousFallback = context.Fallback;
        var previousFailure = context.FailureReason;
        var previousRetryAt = context.RetryNotBefore;
        var previousRoute = context.RouteStatus;
        var previousDoAfter = context.DoAfterStatus;
        var previousTransition = context.LastTransitionAt;
        var previousDeadline = context.Deadline;
        var now = _timing.CurTime;
        var terminalFailure = IsRouteTerminal(lifecycle);
        var routeFailure = lifecycle.State is LuaMRescueShuttleRouteState.Failed
            or LuaMRescueShuttleRouteState.TimedOut;

        context.Activity = lifecycle.State switch
        {
            LuaMRescueShuttleRouteState.None => LuaMRescueActivity.Standby,
            LuaMRescueShuttleRouteState.Failed or LuaMRescueShuttleRouteState.TimedOut =>
                LuaMRescueActivity.Recovering,
            _ => lifecycle.RouteActivity,
        };
        context.TerminalStatus = lifecycle.State switch
        {
            LuaMRescueShuttleRouteState.None => LuaMRescueTerminalStatus.None,
            LuaMRescueShuttleRouteState.Routing or LuaMRescueShuttleRouteState.Arrived =>
                LuaMRescueTerminalStatus.Active,
            LuaMRescueShuttleRouteState.Docked => LuaMRescueTerminalStatus.Succeeded,
            LuaMRescueShuttleRouteState.Failed or LuaMRescueShuttleRouteState.TimedOut when terminalFailure =>
                LuaMRescueTerminalStatus.Failed,
            _ => LuaMRescueTerminalStatus.Blocked,
        };
        context.Target = lifecycle.Target;
        context.Destination = lifecycle.Target is { Valid: true } target && !Deleted(target)
            ? new EntityCoordinates(target, Vector2.Zero)
            : null;
        context.StartedAt = lifecycle.RouteStartedAt;
        if (context.LastProgressAt == TimeSpan.Zero)
            context.LastProgressAt = lifecycle.RouteStartedAt;
        if (remainingDistance is { } distance &&
            (context.LastProgressDistance == null ||
             distance + Math.Max(0f, profile.ProgressTolerance) < context.LastProgressDistance.Value))
        {
            context.LastProgressAt = now;
            context.LastProgressDistance = distance;
        }
        context.Attempts = Math.Max(1, lifecycle.RetryCount + 1);
        context.Generation = (uint) Math.Max(0, lifecycle.RouteGeneration);
        context.Blocked = routeFailure;
        context.Fallback = routeFailure && !terminalFailure
            ? lifecycle.RouteActivity
            : LuaMRescueActivity.Standby;
        context.FailureReason = routeFailure
            ? lifecycle.RouteFailureReason == LuaMRescueFailureReason.None
                ? LuaMRescueFailureReason.ShuttleRouteFailed
                : lifecycle.RouteFailureReason
            : LuaMRescueFailureReason.None;
        context.RetryNotBefore = lifecycle.NextRetryAt;
        context.RouteStatus = lifecycle.State switch
        {
            LuaMRescueShuttleRouteState.Routing => LuaMRescueRouteStatus.Moving,
            LuaMRescueShuttleRouteState.Arrived or LuaMRescueShuttleRouteState.Docked =>
                LuaMRescueRouteStatus.Arrived,
            LuaMRescueShuttleRouteState.Failed or LuaMRescueShuttleRouteState.TimedOut =>
                LuaMRescueRouteStatus.Blocked,
            _ => LuaMRescueRouteStatus.None,
        };
        context.DoAfterStatus = LuaMRescueDoAfterStatus.None;
        context.LastTransitionAt = lifecycle.StateChangedAt;
        context.Deadline = lifecycle.RouteDeadline;

        var changed = previousActivity != context.Activity ||
                      previousTerminal != context.TerminalStatus ||
                      previousTarget != context.Target ||
                      !Nullable.Equals(previousDestination, context.Destination) ||
                      previousStartedAt != context.StartedAt ||
                      previousProgressAt != context.LastProgressAt ||
                      previousDistance != context.LastProgressDistance ||
                      previousAttempts != context.Attempts ||
                      previousGeneration != context.Generation ||
                      previousBlocked != context.Blocked ||
                      previousFallback != context.Fallback ||
                      previousFailure != context.FailureReason ||
                      previousRetryAt != context.RetryNotBefore ||
                      previousRoute != context.RouteStatus ||
                      previousDoAfter != context.DoAfterStatus ||
                      previousTransition != context.LastTransitionAt ||
                      previousDeadline != context.Deadline;
        if (changed)
            Dirty(shuttle, lifecycle);
    }

    private void SetRouteState(
        EntityUid shuttle,
        LuaMRescueShuttleLifecycleComponent lifecycle,
        LuaMRescueShuttleRouteState state,
        string status,
        LuaMRescueFailureReason failureReason = LuaMRescueFailureReason.None)
    {
        var oldState = lifecycle.State;
        if (state is LuaMRescueShuttleRouteState.Failed or LuaMRescueShuttleRouteState.TimedOut)
            StopAutopilotForDocking(lifecycle);
        lifecycle.State = state;
        lifecycle.StateChangedAt = _timing.CurTime;
        lifecycle.LastStatus = status;
        lifecycle.RouteFailureReason = state is LuaMRescueShuttleRouteState.Failed
            or LuaMRescueShuttleRouteState.TimedOut
            ? failureReason == LuaMRescueFailureReason.None
                ? LuaMRescueFailureReason.ShuttleRouteFailed
                : failureReason
            : LuaMRescueFailureReason.None;
        SyncAutopilotActivityContext(shuttle, lifecycle, remainingDistance: null);
        Dirty(shuttle, lifecycle);

        if (oldState != state)
        {
            var terminal = IsRouteTerminal(lifecycle);
            RaiseLocalEvent(
                shuttle,
                new LuaMRescueShuttleRouteStateChangedEvent(
                    shuttle,
                    lifecycle.Target,
                    oldState,
                    state,
                    lifecycle.RouteActivity,
                    lifecycle.RouteGeneration,
                    lifecycle.RetryCount + 1,
                    terminal,
                    status),
                true);
        }
    }

    private static LuaMRescueShuttleRouteSnapshot ToRouteSnapshot(
        EntityUid shuttle,
        LuaMRescueShuttleLifecycleComponent lifecycle)
    {
        return new LuaMRescueShuttleRouteSnapshot(
            shuttle,
            lifecycle.Target,
            lifecycle.AutopilotConsole,
            lifecycle.State,
            lifecycle.RouteActivity,
            lifecycle.ActivityRoleProfile.Role,
            lifecycle.ActivityContext.Activity,
            lifecycle.ActivityContext.TerminalStatus,
            lifecycle.ActivityContext.Generation,
            lifecycle.ActivityContext.FailureReason,
            lifecycle.ActivityContext.Fallback,
            lifecycle.ActivityContext.LastProgressDistance,
            lifecycle.RouteStartedAt,
            lifecycle.RouteDeadline,
            lifecycle.StateChangedAt,
            lifecycle.RetryCount,
            lifecycle.EffectiveMaxRetries,
            lifecycle.RouteGeneration,
            lifecycle.NextRetryAt,
            lifecycle.DockingAttemptCount,
            lifecycle.MaxDockingAttempts,
            lifecycle.NextDockingAttemptAt,
            lifecycle.SafeExitConfirmed,
            IsRouteTerminal(lifecycle),
            lifecycle.LastStatus);
    }

    private static bool IsRouteTerminal(LuaMRescueShuttleLifecycleComponent lifecycle)
    {
        return lifecycle.State is LuaMRescueShuttleRouteState.Failed or LuaMRescueShuttleRouteState.TimedOut &&
               lifecycle.RetryCount >= lifecycle.EffectiveMaxRetries;
    }

    public List<string> BuildAutopilotStatusLines()
    {
        var lines = new List<string>();
        var query = EntityQueryEnumerator<LuaMRescueShuttleLifecycleComponent>();
        while (query.MoveNext(out var shuttle, out var lifecycle))
        {
            var snapshot = ToRouteSnapshot(shuttle, lifecycle);
            var distance = snapshot.LastProgressDistance?.ToString("0.00", CultureInfo.InvariantCulture) ?? "none";
            lines.Add(
                $"autopilot={FormatAutopilotEntity(snapshot.AutopilotConsole)}; shuttle={FormatAutopilotEntity(shuttle)}; " +
                $"role={snapshot.Role}; activity={snapshot.Activity}; terminal={snapshot.ActivityTerminal}; " +
                $"intent={snapshot.RouteActivity}; target={FormatAutopilotEntity(snapshot.Target)}; " +
                $"route={snapshot.State}; generation={snapshot.RouteGeneration}; activityGeneration={snapshot.ActivityGeneration}; " +
                $"attempts={snapshot.Attempt}/{snapshot.MaxRetries + 1}; distance={distance}; actionRange=docking-confirmation; " +
                $"deadline={snapshot.RouteDeadline.TotalSeconds:0.00}s; retryAt={snapshot.NextRetryAt.TotalSeconds:0.00}s; " +
                $"safeExit={snapshot.SafeExitConfirmed}; blockedReason={snapshot.ActivityFailureReason}; " +
                $"fallback={snapshot.ActivityFallback}; status={snapshot.LastStatus}");
        }

        return lines;
    }

    private string FormatAutopilotEntity(EntityUid? entity)
    {
        if (entity is not { Valid: true } uid)
            return "none";
        if (Deleted(uid))
            return $"{uid}:deleted";
        return $"{GetNetEntity(uid)}:{Name(uid)}";
    }

    private static string BuildStatus(string shuttleName, bool deployedAgent, int deployedEscorts, bool routeRequested, bool routed)
    {
        var status = $"Purchased rescue shuttle {shuttleName}";

        if (deployedAgent)
        {
            status += deployedEscorts > 0
                ? $" and deployed a LuaM rescue agent with {deployedEscorts} autonomous escorts"
                : " and deployed a LuaM rescue agent";
        }

        if (routeRequested)
            status += routed
                ? " with autopilot routed to the rescue target"
                : " but autopilot routing was unavailable";

        return status + ".";
    }

    private bool TryFindAutopilotConsole(
        EntityUid shuttle,
        out EntityUid console,
        out ShuttleConsoleComponent shuttleConsole,
        out HTNComponent htn)
    {
        var consoleQuery = EntityQueryEnumerator<ShuttleConsoleComponent, HTNComponent, TransformComponent>();
        while (consoleQuery.MoveNext(out var uid, out var consoleComp, out var htnComp, out var xform))
        {
            if (xform.GridUid != shuttle)
                continue;

            console = uid;
            shuttleConsole = consoleComp;
            htn = htnComp;
            return true;
        }

        console = default;
        shuttleConsole = default!;
        htn = default!;
        return false;
    }

    private bool TryFindStationReturnTarget(EntityUid station, out EntityUid returnTarget)
    {
        if (TryComp<StationDataComponent>(station, out var stationData) &&
            _station.GetLargestGrid((station, stationData)) is { Valid: true } grid)
        {
            returnTarget = grid;
            return true;
        }

        returnTarget = default;
        return false;
    }

    private bool TryFindShuttleAnchor(EntityUid shuttle, out EntityUid anchor)
    {
        if (TryFindPatientCareAnchor(shuttle, out anchor))
            return true;

        var consoleQuery = EntityQueryEnumerator<ShuttleConsoleComponent, TransformComponent>();
        while (consoleQuery.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.GridUid == shuttle)
            {
                anchor = uid;
                return true;
            }
        }

        var dockingQuery = EntityQueryEnumerator<DockingComponent, TransformComponent>();
        while (dockingQuery.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.GridUid == shuttle)
            {
                anchor = uid;
                return true;
            }
        }

        anchor = default;
        return false;
    }

    private bool TryFindPatientCareAnchor(EntityUid shuttle, out EntityUid anchor)
    {
        EntityUid? careAnchor = null;
        EntityUid? fallbackStrap = null;
        var strapQuery = EntityQueryEnumerator<StrapComponent, TransformComponent>();
        while (strapQuery.MoveNext(out var uid, out var strap, out var xform))
        {
            if (xform.GridUid != shuttle ||
                !strap.Enabled)
            {
                continue;
            }

            if (HasComp<StasisBedComponent>(uid))
            {
                anchor = uid;
                return true;
            }

            if (HasComp<HealOnBuckleComponent>(uid))
                careAnchor ??= uid;

            fallbackStrap ??= uid;
        }

        if (careAnchor is { Valid: true } care)
        {
            anchor = care;
            return true;
        }

        if (fallbackStrap is { Valid: true } fallback)
        {
            anchor = fallback;
            return true;
        }

        anchor = default;
        return false;
    }

    private sealed class PendingMedicalDispatch
    {
        public EntityUid Target;
        public LuaMRescueMedicalSignalKind Kind;
        public bool ManualOverride;
        public EntityUid? ManualOverrideOwner;
        public uint ManualOverrideGeneration;
        public bool PreservedOwnerLineage;
        public EntityUid? ReplacementAnchor;
        public EntityUid? AssignedShuttle;
        public EntityUid? AssignedShuttleAnchor;
        public EntityUid? AssignedShuttleConsole;
        public EntityUid? AssignedReturnTarget;
        public bool RequiredOnboardHandoff;
        public ulong QueueGeneration;
        public TimeSpan EnqueuedAt;
        public TimeSpan NextAttemptAt;
        public int Attempts;
        public TimeSpan Deadline;
        public bool Terminal;
        public string LastStatus = "none";
    }

    private sealed class OnboardOwnershipTransfer
    {
        public EntityUid Patient;
        public EntityUid ReplacementAnchor;
        public EntityUid Shuttle;
        public EntityUid? ShuttleAnchor;
        public EntityUid Strap;
        public EntityUid? ShuttleConsole;
        public EntityUid? ReturnTarget;
        public int OnboardCareAttempts;
        public string? TerminalOnboardCareFailure;
        public int OnboardHandoffAttempts;
        public bool Ignored;
        public bool AutoReleaseStabilizedPatients;
        public float AutoReleaseMaxDamage;
        public float AutoReleaseRange;
        public bool RequiredOnboardHandoff;
        public string Status = "none";
    }

    private sealed class PatientRecoveryTransfer
    {
        public EntityUid Target;
        public EntityUid ReplacementAnchor;
        public EntityUid? AssignedShuttle;
        public EntityUid? AssignedShuttleAnchor;
        public EntityUid? AssignedShuttleConsole;
        public EntityUid? AssignedReturnTarget;
        public TimeSpan? SkippedUntil;
        public TimeSpan? DeferredUntil;
        public int? RouteFailureAttempts;
        public int? AnalysisAttempts;
        public string? TerminalAnalysisFailure;
        public int? TreatmentAttempts;
        public string? TerminalTreatmentFailure;
        public float? TerminalTreatmentFailureDamage;
        public int? DefibrillationAttempts;
        public int? CompletedDefibrillationFailures;
        public TimeSpan? DefibrillationStartedAt;
        public string? TerminalDefibrillationFailure;
        public int? PullAttempts;
        public TimeSpan? NextPullAttemptAt;
        public string? TerminalPullFailure;
        public int? EvacuationUnbuckleAttempts;
        public TimeSpan? NextEvacuationUnbuckleAttemptAt;
        public string? TerminalEvacuationUnbuckleFailure;
        public readonly Dictionary<EntityUid, int> PatientBuckleAttempts = new();
        public readonly Dictionary<EntityUid, TimeSpan> NextPatientBuckleAttemptAt = new();
        public readonly Dictionary<EntityUid, string> TerminalPatientBuckleFailures = new();
        public int? OnboardCareAttempts;
        public string? TerminalOnboardCareFailure;
        public int? OnboardHandoffAttempts;
        public bool IgnoredOnboard;
        public bool RequiredOnboardHandoff;
        public string Status = "none";
    }
}

[AdminCommand(AdminFlags.Server)]
public sealed class LuaMRescueShuttleCommand : IConsoleCommand
{
    private const string ControlFlag = "--control";
    private const string NoAgentFlag = "--no-agent";
    private const string NoTeamFlag = "--no-team";
    private const string NoAutopilotFlag = "--no-autopilot";
    private const string DeathSignalFlag = "--death-signal";

    [Dependency] private readonly IEntityManager _entities = default!;
    [Dependency] private readonly IPlayerManager _players = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;

    public string Command => "luam_rescue_shuttle";
    public string Description => "Purchases a LuaM rescue shuttle and optionally deploys a rescue agent aboard it.";
    public string Help =>
        $"Usage: {Command} [station=<stationEntity>] [vessel={LuaMRescueShuttleSystem.DefaultVessel}] [target=<entity|player>] [{DeathSignalFlag}] [{ControlFlag}] [{NoAgentFlag}] [{NoTeamFlag}] [{NoAutopilotFlag}]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var deathSignal = args.Any(arg => arg.Equals(DeathSignalFlag, StringComparison.OrdinalIgnoreCase));
        var control = args.Any(arg => arg.Equals(ControlFlag, StringComparison.OrdinalIgnoreCase));
        var spawnAgent = !args.Any(arg => arg.Equals(NoAgentFlag, StringComparison.OrdinalIgnoreCase));
        var spawnTeam = spawnAgent && !args.Any(arg => arg.Equals(NoTeamFlag, StringComparison.OrdinalIgnoreCase));
        var routeToTarget = !args.Any(arg => arg.Equals(NoAutopilotFlag, StringComparison.OrdinalIgnoreCase));

        if (control && shell.Player == null)
        {
            shell.WriteError($"{ControlFlag} requires a player shell.");
            return;
        }

        var stationArg = GetValue(args, "station");
        var vesselId = GetValue(args, "vessel") ?? LuaMRescueShuttleSystem.DefaultVessel;
        var targetArg = GetValue(args, "target");

        if (deathSignal && string.IsNullOrWhiteSpace(targetArg))
        {
            shell.WriteError($"{DeathSignalFlag} requires target=<entity|player> so Aibolit can report who it is flying to.");
            return;
        }

        if (!TryResolveStation(shell, stationArg, out var station, out var error))
        {
            shell.WriteError(error);
            return;
        }

        if (!_prototypes.TryIndex<VesselPrototype>(vesselId, out var vessel))
        {
            shell.WriteError($"Unknown vessel prototype: {vesselId}.");
            return;
        }

        EntityUid? target = null;
        if (!string.IsNullOrWhiteSpace(targetArg) &&
            !TryResolveTarget(targetArg, out target, out error))
        {
            shell.WriteError(error);
            return;
        }

        var system = _entities.System<LuaMRescueShuttleSystem>();
        if (!system.TryDispatchRescueShuttle(
                station,
                vessel,
                target,
                shell.Player,
                spawnAgent,
                spawnTeam,
                control,
                routeToTarget,
                deathSignal,
                manualOverride: target is { Valid: true },
                out var shuttle,
                out var agent,
                out var autopilotConsole,
                out var escortCount,
                out var status))
        {
            shell.WriteError(status);
            return;
        }

        var shuttleNet = shuttle is { Valid: true } shuttleUid
            ? _entities.GetNetEntity(shuttleUid).ToString()
            : "none";
        var agentNet = agent is { Valid: true } agentUid
            ? _entities.GetNetEntity(agentUid).ToString()
            : "none";
        var autopilotNet = autopilotConsole is { Valid: true } autopilotUid
            ? _entities.GetNetEntity(autopilotUid).ToString()
            : "none";

        shell.WriteLine($"{status} shuttle={shuttleNet}; agent={agentNet}; escorts={escortCount}; autopilotConsole={autopilotNet}; vessel={vessel.ID}; deathSignal={deathSignal}.");
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return CompletionResult.FromHintOptions(
            [
                $"vessel={LuaMRescueShuttleSystem.DefaultVessel}",
                "station=",
                "target=",
                DeathSignalFlag,
                ControlFlag,
                NoAgentFlag,
                NoTeamFlag,
                NoAutopilotFlag,
            ],
            "rescue shuttle option");
    }

    private bool TryResolveStation(IConsoleShell shell, string? raw, out EntityUid station, out string error)
    {
        station = default;
        error = string.Empty;
        var stationSystem = _entities.System<StationSystem>();

        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (TryResolveEntity(raw, out var parsed) &&
                _entities.HasComponent<StationDataComponent>(parsed))
            {
                station = parsed;
                return true;
            }

            error = $"Station not found or target is not a station: {raw}.";
            return false;
        }

        if (shell.Player?.AttachedEntity is { Valid: true } attached &&
            stationSystem.GetOwningStation(attached) is { Valid: true } owningStation)
        {
            station = owningStation;
            return true;
        }

        var stations = stationSystem.GetStationsSet();
        if (stations.Count == 1)
        {
            station = stations.First();
            return true;
        }

        error = $"Could not infer station. Pass station=<entity>; current station count is {stations.Count}.";
        return false;
    }

    private bool TryResolveTarget(string raw, out EntityUid? target, out string error)
    {
        target = null;
        error = string.Empty;

        if (TryResolveEntity(raw, out var parsedTarget))
        {
            target = parsedTarget;
            return true;
        }

        var matches = _players.Sessions
            .Where(session => session.Name.Contains(raw, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matches.Length == 1 && matches[0].AttachedEntity is { Valid: true } attached)
        {
            target = attached;
            return true;
        }

        error = matches.Length > 1
            ? $"Target player name is ambiguous: {raw}."
            : $"Target not found or has no attached entity: {raw}.";
        return false;
    }

    private bool TryResolveEntity(string raw, out EntityUid entity)
    {
        entity = default;

        if (NetEntity.TryParse(raw, out var netEntity) &&
            _entities.TryGetEntity(netEntity, out var parsed) &&
            parsed is { Valid: true })
        {
            entity = parsed.Value;
            return true;
        }

        if (!int.TryParse(raw, out var integerId))
            return false;

        if (_entities.TryGetEntity(new NetEntity(integerId), out parsed) &&
            parsed is { Valid: true })
        {
            entity = parsed.Value;
            return true;
        }

        var uid = new EntityUid(integerId);
        if (!_entities.EntityExists(uid))
            return false;

        entity = uid;
        return true;
    }

    private static string? GetValue(string[] args, string key)
    {
        var prefix = $"{key}=";
        var match = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return match?.Substring(prefix.Length);
    }
}
