using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server.Administration;
using Content.Server.Atmos.EntitySystems;
using Content.Server.PowerCell;
using Content.Server.Bed.Components;
using Content.Server.Body.Components;
using Content.Server.Body.Systems;
using Content.Server.Buckle.Systems;
using Content.Server.Chemistry.EntitySystems;
using Content.Server.Chat.Systems;
using Content.Server.Gateway.Components;
using Content.Server.Hands.Systems;
using Content.Server.Interaction;
using Content.Server.Medical;
using Content.Server.Medical.Components;
using Content.Server.Mind;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Server.Radio.EntitySystems;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Teleportation;
using Content.Server.VendingMachines;
using Content.Shared.ActionBlocker;
using Content.Shared._Goobstation.DoAfter;
using Content.Shared.Atmos.Components;
using Content.Shared.Atmos.EntitySystems;
using Content.Shared.Atmos.Rotting;
using Content.Shared.Body.Components;
using Content.Shared.Buckle.Components;
using Content.Shared.Body.Systems;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Chat;
using Content.Shared.Damage;
using Content.Shared.DoAfter;
using Content.Shared.Examine;
using Content.Shared.Hands.Components;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Item.ItemToggle;
using Content.Shared.Medical;
using Content.Shared.MedicalScanner;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.Radio;
using Content.Shared.Silicons.Bots;
using Content.Shared.Administration;
using Content.Shared.Stacks;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Teleportation.Components;
using Content.Shared.Teleportation.Systems;
using Content.Shared.Traits.Assorted;
using Content.Shared.VendingMachines;
using Robust.Server.Player;
using Robust.Shared.Containers;
using Robust.Shared.Console;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._LuaM.Rescue;

public sealed class LuaMRescueAgentSystem : EntitySystem
{
    public readonly record struct MedicalSupplySnapshot(
        int MedicalUnits,
        int EffectiveMedicalUnits,
        int TopicalUnits,
        int InjectorUnits,
        int BloodUnits,
        int DiagnosticUnits,
        string Status);

    // LuaM rescue state is server-only; status commands query it directly.
    private static void Dirty(EntityUid _, LuaMRescueAgentComponent __) { }
    private static void Dirty(EntityUid _, LuaMRescueEscortComponent __) { }

    private enum PullStartResult : byte
    {
        Started,
        Waiting,
        TerminalFailure,
    }

    private enum PatientBuckleResult : byte
    {
        NotReady,
        Waiting,
        Succeeded,
        TerminalFailure,
    }

    private enum PatientRouteFailureDisposition : byte
    {
        TemporarySkip,
        DormantRecovery,
        DurableSkip,
    }

    private enum PatientDeliveryStrapState : byte
    {
        Viable,
        TemporarilyUnavailable,
        Exhausted,
    }

    private sealed class MedicalEffectExpectation
    {
        public readonly HashSet<string> DamageTypes = new(StringComparer.Ordinal);

        public bool ReducesBleeding;

        public bool IncreasesBloodVolume;

        public bool ImprovesMobState;

        public bool HasObservableEffect =>
            DamageTypes.Count > 0 || ReducesBleeding || IncreasesBloodVolume || ImprovesMobState;
    }

    private const string RescueAgentPrototype = "LuaMRescueAgent";
    private const string RescueAgentDisabledStatusPrefix = "rescue-agent-disabled:";
    private const string RescueAgentRecoveredStatusPrefix = "rescue-agent-recovered:";
    private const int SameUrgencyPreemptionPriorityMargin = 10;
    private static readonly TimeSpan MedicalDoAfterHardTimeout = TimeSpan.FromSeconds(30);
    private static readonly ProtoId<RadioChannelPrototype> MedicalRadioChannel = "Medical";
    private static readonly string[] TreatmentStorageSlotPriority =
    [
        "belt",
        "back",
        "pocket1",
        "pocket2",
        "suitstorage",
        "outerClothing",
        "jumpsuit",
    ];
    private static readonly Dictionary<string, float> ConservativeMedicineLimits = new(StringComparer.Ordinal)
    {
        ["Bicaridine"] = 16f,
        // Dermaline starts producing harmful effects at 10u. Keep the
        // post-transfer bloodstream strictly below that prototype threshold.
        ["Dermaline"] = 10f,
        ["Kelotane"] = 25f,
        ["Dexalin"] = 20f,
        ["DexalinPlus"] = 25f,
        ["Dylovene"] = 15f,
        ["Inaprovaline"] = 15f,
        ["Epinephrine"] = 20f,
        ["Tricordrazine"] = 15f,
        ["Omnizine"] = 60f,
        ["TranexamicAcid"] = 15f,
        ["Leporazine"] = 25f,
    };
    private static readonly string[] MedicalVendingProductPriority =
    [
        "Brutepack",
        "Ointment",
        "Gauze",
        "EmergencyMedipen",
        "PillCanisterTricordrazine",
        "EpinephrineChemistryBottle",
        "Bloodpack",
        "HandheldHealthAnalyzer",
    ];

    [Dependency] private readonly NPCSystem _npc = default!;
    [Dependency] private readonly HTNSystem _htnSystem = default!;
    [Dependency] private readonly MindSystem _mind = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly MedibotSystem _medibot = default!;
    [Dependency] private readonly PullingSystem _pulling = default!;
    [Dependency] private readonly BuckleSystem _buckle = default!;
    [Dependency] private readonly HandsSystem _hands = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly InternalsSystem _internals = default!;
    [Dependency] private readonly SharedGasTankSystem _gasTank = default!;
    [Dependency] private readonly RespiratorSystem _respirator = default!;
    [Dependency] private readonly AtmosphereSystem _atmosphere = default!;
    [Dependency] private readonly InteractionSystem _interaction = default!;
    [Dependency] private readonly ExamineSystemShared _examine = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly RadioSystem _radio = default!;
    [Dependency] private readonly DefibrillatorSystem _defibrillator = default!;
    [Dependency] private readonly DockingSystem _docking = default!;
    [Dependency] private readonly PowerCellSystem _powerCell = default!;
    [Dependency] private readonly SharedStorageSystem _storage = default!;
    [Dependency] private readonly SharedBodySystem _body = default!;
    [Dependency] private readonly SharedSolutionContainerSystem _solutionContainers = default!;
    [Dependency] private readonly ItemToggleSystem _itemToggle = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly ActionBlockerSystem _actionBlocker = default!;
    [Dependency] private readonly LinkedEntitySystem _linkedEntity = default!;
    [Dependency] private readonly PortalSystem _portal = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly VendingMachineSystem _vending = default!;
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly LuaMRescueNavigationSystem _rescueNavigation = default!;
    [Dependency] private readonly LuaMRescueActivityCoordinatorSystem _activity = default!;
    [Dependency] private readonly LuaMRescueTeamSystem _rescueTeam = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<MobStateComponent, TargetDefibrillatedEvent>(OnTargetDefibrillated);
        // Medical DoAfter events are component-exclusive in Robust. Subscribe to the
        // user-side completion event instead of competing with the medical systems
        // that own the actual action result.
        SubscribeLocalEvent<LuaMRescueAgentComponent, DoAfterEndedEvent>(OnRescueMedicalDoAfterEnded);
    }

    public void PurgeMedicalTargetStateFromAllAgents(EntityUid target, string reason)
    {
        var agents = new List<EntityUid>();
        var query = EntityQueryEnumerator<LuaMRescueAgentComponent>();
        while (query.MoveNext(out var agent, out _))
            agents.Add(agent);

        foreach (var agent in agents)
        {
            if (!TryComp<LuaMRescueAgentComponent>(agent, out var rescue))
                continue;

            TryComp<HTNComponent>(agent, out var htn);
            PurgePatientState(agent, rescue, htn, target, reason);
        }
    }

    private void PurgePatientState(
        EntityUid agent,
        LuaMRescueAgentComponent rescue,
        HTNComponent? htn,
        EntityUid target,
        string reason)
    {
        // Manual, Required and dormant lineage can coexist with an active
        // mission for another patient. Only exact active-mission references may
        // tear down shared HTN movement, delivery-strap or progress state.
        EntityUid? followedEntity = null;
        if (htn != null &&
            htn.Blackboard.TryGetValue<EntityCoordinates>(
                NPCBlackboard.FollowTarget,
                out var followTarget,
                EntityManager) &&
            followTarget.EntityId is { Valid: true } followUid)
        {
            followedEntity = followUid;
        }

        var followsTarget = followedEntity == target;
        var activityReferencesTarget = rescue.ActivityContext.Target == target ||
                                       rescue.ActivityContext.Destination is { } destination &&
                                       destination.EntityId == target;
        var pendingMedical = rescue.PendingMedicalDoAfterTarget == target;
        var pendingPlayerAction = rescue.PendingPlayerActionTarget == target;
        var taskReferencesTarget = rescue.TaskPatientTarget == target || rescue.TaskSupplyTarget == target;
        var progressReferencesTarget = rescue.ProgressTarget == target ||
                                       rescue.ProgressGoal == target ||
                                       rescue.RouteBlockHoldTarget == target ||
                                       rescue.RouteBlockHoldGoal == target;
        var hasAuthoritativeActivityReference =
            rescue.ActivityContext.Target is { Valid: true } ||
            rescue.ActivityContext.Destination is { EntityId: { Valid: true } };
        var orphanAssignedTarget = rescue.AssignedTarget == target &&
                                   !hasAuthoritativeActivityReference &&
                                   followedEntity == null;
        var evacuationMissionReferencesTarget = rescue.EvacuatingTarget == target ||
                                                 rescue.OnboardCareTarget == target ||
                                                 (rescue.TaskPatientTarget == target &&
                                                  rescue.TaskStage is LuaMRescueTaskStage.EvacuatingPatient or
                                                      LuaMRescueTaskStage.DeliveringPatient) ||
                                                 (rescue.ActivityContext.Target == target &&
                                                  rescue.ActivityContext.Activity is
                                                      LuaMRescueActivity.PreparingEvacuation or
                                                      LuaMRescueActivity.Pulling or
                                                      LuaMRescueActivity.Delivering or
                                                      LuaMRescueActivity.BucklePatient or
                                                      LuaMRescueActivity.OnboardCare or
                                                      LuaMRescueActivity.Handoff);
        var movementReferencesTarget = followsTarget ||
                                       activityReferencesTarget ||
                                       orphanAssignedTarget;

        var changed = false;
        // The tracking mirror may already be stale or point at a newer patient.
        // Always inspect the authoritative DoAfter records for this exact lifecycle
        // identity, while only clearing mirror fields when they reference it too.
        CancelMedicalDoAftersForIntentReplacement(
            agent,
            rescue,
            $"{reason}; target={FormatEntityRef(target)}",
            exactTarget: target,
            recordActivityProgress: false);
        if (pendingMedical)
        {
            changed = true;
        }

        if (pendingPlayerAction)
        {
            ClearPendingPlayerAction(
                rescue,
                $"failed player action: {LuaMRescueFailureReason.TargetLost}; {reason}");
            changed = true;
        }

        StopPullingTarget(agent, target);
        _rescueNavigation.CancelRoute(agent, target);

        if (htn != null && movementReferencesTarget)
        {
            ClearFollowTarget(agent, rescue, htn);
            changed = true;
        }

        if (rescue.ManualOverrideTarget == target)
        {
            rescue.ManualOverrideTarget = null;
            rescue.ManualOverrideGeneration = NextManualOverrideGeneration(rescue.ManualOverrideGeneration);
            changed = true;
        }

        if (rescue.AssignedTarget == target)
        {
            rescue.AssignedTarget = null;
            changed = true;
        }

        if (rescue.DeathSignalTarget == target)
        {
            ClearDeathSignalTarget(rescue, target);
            changed = true;
        }

        if (rescue.ArrivalReportedTarget == target)
        {
            ClearArrivalReportTarget(rescue, target);
            changed = true;
        }

        if (rescue.TriageReportedTarget == target)
        {
            ClearTriageDecisionTarget(rescue, target);
            changed = true;
        }

        if (rescue.ShuttleRoutedTarget == target)
        {
            rescue.ShuttleRoutedTarget = null;
            changed = true;
        }

        if (rescue.EvacuatingTarget == target)
        {
            rescue.EvacuatingTarget = null;
            changed = true;
        }

        if (rescue.OnboardCareTarget == target)
        {
            rescue.OnboardCareTarget = null;
            changed = true;
        }

        if (rescue.LifeSupportEmergencyPatient == target)
        {
            rescue.LifeSupportEmergencyPatient = null;
            changed = true;
        }

        if (evacuationMissionReferencesTarget ||
            orphanAssignedTarget ||
            rescue.AssignedPatientStrap == target)
        {
            rescue.AssignedPatientStrap = null;
            changed = true;
        }

        if (taskReferencesTarget)
        {
            ClearRescueTask(agent, rescue, $"forgot patient identity {FormatEntityRef(target)}: {reason}");
            changed = true;
        }

        if (evacuationMissionReferencesTarget ||
            activityReferencesTarget ||
            taskReferencesTarget ||
            progressReferencesTarget)
        {
            ResetTargetProgress(rescue);
            changed = true;
        }

        if (rescue.DormantRouteTarget == target)
        {
            ClearDormantRouteRecovery(
                rescue,
                $"cancelled target={FormatEntityRef(target)}; reason={LuaMRescueFailureReason.TargetLost}",
                clearFailureBudget: true);
            rescue.DormantRouteProbeCount = 0;
            rescue.DormantRouteResumeCount = 0;
            changed = true;
        }

        changed |= rescue.SkippedTargets.Remove(target);
        changed |= rescue.DeferredPatientTargets.Remove(target);
        changed |= rescue.RouteFailureAttempts.Remove(target);
        changed |= rescue.AnalyzedTargets.Remove(target);
        changed |= rescue.AnalysisAttempts.Remove(target);
        changed |= rescue.TerminalAnalysisFailures.Remove(target);
        changed |= rescue.TreatmentAttempts.Remove(target);
        changed |= rescue.TerminalTreatmentFailures.Remove(target);
        changed |= rescue.TerminalTreatmentFailureDamage.Remove(target);
        changed |= rescue.DefibrillationAttempts.Remove(target);
        changed |= rescue.CompletedDefibrillationFailures.Remove(target);
        changed |= rescue.DefibrillationStartedAt.Remove(target);
        changed |= rescue.TerminalDefibrillationFailures.Remove(target);
        changed |= rescue.PullAttempts.Remove(target);
        changed |= rescue.NextPullAttemptAt.Remove(target);
        changed |= rescue.TerminalPullFailures.Remove(target);
        changed |= rescue.EvacuationUnbuckleAttempts.Remove(target);
        changed |= rescue.NextEvacuationUnbuckleAttemptAt.Remove(target);
        changed |= rescue.TerminalEvacuationUnbuckleFailures.Remove(target);
        changed |= rescue.OnboardCareAttempts.Remove(target);
        changed |= rescue.TerminalOnboardCareFailures.Remove(target);
        changed |= rescue.OnboardHandoffAttempts.Remove(target);
        changed |= rescue.IgnoredOnboardPatients.Remove(target);
        changed |= rescue.RequiredOnboardHandoffPatients.Remove(target);

        var bucklePairs = rescue.PatientBuckleAttempts.Keys
            .Concat(rescue.NextPatientBuckleAttemptAt.Keys)
            .Concat(rescue.TerminalPatientBuckleFailures.Keys)
            .Where(pair => pair.Patient == target)
            .Distinct()
            .ToArray();
        if (bucklePairs.Length > 0)
        {
            ClearPatientBuckleFailures(rescue, target);
            changed = true;
        }

        if (activityReferencesTarget)
        {
            _activity.BeginOrReplaceIntent(
                agent,
                rescue.ActivityRole == LuaMRescueRole.None ? LuaMRescueRole.Aibolit : rescue.ActivityRole,
                LuaMRescueActivity.Standby,
                target: null,
                destination: null,
                out _);
            rescue.LastIntentCancellationStatus =
                $"patient identity lost: target={target}; reason={reason}";

            changed = true;
        }

        if (!changed)
            return;

        rescue.LastTargetTrackingStatus =
            $"patient_identity_purged: target={target}; reason={reason}";
        Dirty(agent, rescue);
    }

    private void OnRescueMedicalDoAfterEnded(
        Entity<LuaMRescueAgentComponent> agent,
        ref DoAfterEndedEvent args)
    {
        var rescue = agent.Comp;
        if (args.Target is not { Valid: true } patient ||
            rescue.PendingMedicalDoAfterTarget != patient)
        {
            return;
        }

        // DoAfterEndedEvent carries no event type or id. Ignore an unrelated
        // action ending on the same target while the tracked medical DoAfter is
        // still authoritative, and never re-enter a completed transfer that is
        // already waiting for its metabolic effect.
        if (rescue.PendingMedicalEffectVerification ||
            rescue.PendingMedicalDoAfterId is { } trackedId &&
            _doAfter.GetStatus(trackedId) == DoAfterStatus.Running)
        {
            return;
        }

        var completesManualTreatment =
            rescue.PendingPlayerAction == LuaMRescuePlayerActionKind.Treat &&
            rescue.PendingPlayerActionTarget == patient;

        if (rescue.PendingMedicalIntentGeneration != rescue.ActivityContext.Generation ||
            rescue.ActivityContext.Target != patient ||
            rescue.ActivityContext.TerminalStatus != LuaMRescueTerminalStatus.Active)
        {
            var staleStatus =
                $"failed treat: ActionCancelled; stale medical intent for {FormatEntityRef(patient)} was replaced";
            ClearTrackedMedicalDoAfter(rescue);
            if (completesManualTreatment)
                ClearPendingPlayerAction(rescue, staleStatus);
            rescue.LastAutoTreatmentStatus = staleStatus;
            Dirty(agent.Owner, rescue);
            return;
        }

        var used = rescue.PendingMedicalDoAfterItem;
        var kind = rescue.PendingMedicalDoAfterKind;
        var item = used is { Valid: true } usedItem && !Deleted(usedItem)
            ? FormatEntityRef(usedItem)
            : "none";
        var succeeded = !args.Cancelled && DidTrackedMedicalActionSucceed(rescue, patient, used);
        var treatmentAction = kind is "healing" or "hypospray" or "injector";
        var injectionAction = IsInjectionMedicalAction(kind, used);
        if (string.Equals(kind, "defibrillator", StringComparison.Ordinal))
        {
            if (succeeded)
                rescue.CompletedDefibrillationFailures.Remove(patient);
            else if (!args.Cancelled)
            {
                rescue.CompletedDefibrillationFailures[patient] =
                    rescue.CompletedDefibrillationFailures.GetValueOrDefault(patient) + 1;
            }
        }

        if (!args.Cancelled &&
            !succeeded &&
            injectionAction &&
            rescue.PendingMedicalExpectedPositiveEffect &&
            DidTrackedMedicalSolutionTransfer(rescue, used))
        {
            // The item system has authoritatively completed the transfer, but
            // reagent metabolism normally applies on a later server update.
            // Keep ownership of the action and wait for an observed benefit.
            BeginMedicalEffectVerification(agent.Owner, rescue, patient, kind, item);
            return;
        }

        var result = args.Cancelled ? "ActionCancelled" : succeeded ? "completed" : "Failed";
        var status = $"{kind} DoAfter {result}: patient={FormatEntityRef(patient)}; item={item}";

        switch (kind)
        {
            case "analyzer":
                rescue.LastAutoAnalyzeStatus = status;
                if (succeeded)
                {
                    rescue.AnalysisAttempts.Remove(patient);
                    rescue.TerminalAnalysisFailures.Remove(patient);
                    rescue.AnalyzedTargets[patient] =
                        _timing.CurTime + TimeSpan.FromSeconds(rescue.AutoAnalyzeCooldown);
                }
                break;
            case "defibrillator":
                rescue.LastAutoDefibStatus = status;
                break;
            default:
                rescue.LastAutoTreatmentStatus = status;
                break;
        }

        var doAfterStatus = args.Cancelled
            ? LuaMRescueDoAfterStatus.Cancelled
            : succeeded
                ? LuaMRescueDoAfterStatus.Succeeded
                : LuaMRescueDoAfterStatus.Failed;

        if (succeeded)
        {
            rescue.TargetStallAccumulator = 0f;
            if (treatmentAction)
            {
                rescue.OnboardCareAttempts.Remove(patient);
                ClearTreatmentFailure(rescue, patient);
            }
            rescue.NextAutoTreatmentAttempt = _timing.CurTime +
                TimeSpan.FromSeconds(Math.Max(0.1f, rescue.AutoTreatCooldown));
            _activity.RecordProgress(
                agent.Owner,
                Transform(patient).Coordinates,
                TryGetDistance(agent.Owner, patient, out var distance) ? distance : null,
                LuaMRescueRouteStatus.Arrived,
                doAfterStatus,
                out _);
        }
        else
        {
            _activity.RecordProgress(
                agent.Owner,
                Transform(patient).Coordinates,
                TryGetDistance(agent.Owner, patient, out var distance) ? distance : null,
                LuaMRescueRouteStatus.Arrived,
                doAfterStatus,
                out _);
            if (treatmentAction)
            {
                RecordTreatmentFailure(
                    agent.Owner,
                    rescue,
                    patient,
                    args.Cancelled
                        ? LuaMRescueFailureReason.ActionCancelled
                        : LuaMRescueFailureReason.NoEffectiveMedicine,
                    $"{kind} DoAfter did not produce a confirmed medical effect");
            }
            else if (string.Equals(kind, "analyzer", StringComparison.Ordinal))
            {
                RecordAnalysisFailure(
                    agent.Owner,
                    rescue,
                    patient,
                    args.Cancelled
                        ? LuaMRescueFailureReason.ActionCancelled
                        : LuaMRescueFailureReason.NoEffectiveMedicine,
                    $"analyzer DoAfter did not produce a confirmed scan for {FormatEntityRef(patient)}");
            }
            else
            {
                _activity.RecordAttempt(agent.Owner, out _);
            }
            if (IsOnAssignedShuttle(patient, rescue))
            {
                var attempts = rescue.OnboardCareAttempts.GetValueOrDefault(patient) + 1;
                rescue.OnboardCareAttempts[patient] = attempts;
                if (attempts >= Math.Max(1, rescue.ActivityRoleProfile.MaxAttempts))
                {
                    MarkTerminalOnboardCareFailure(
                        agent.Owner,
                        rescue,
                        patient,
                        args.Cancelled
                            ? LuaMRescueFailureReason.ActionCancelled
                            : LuaMRescueFailureReason.NoEffectiveMedicine,
                        $"medical DoAfter failed after {attempts} bounded onboard attempts");
                }
            }
        }

        var completedDefibrillationPolicy =
            string.Equals(kind, "defibrillator", StringComparison.Ordinal) &&
            !succeeded &&
            !args.Cancelled &&
            (rescue.CompletedDefibrillationFailures.GetValueOrDefault(patient) >= 3 ||
             rescue.DefibrillationStartedAt.TryGetValue(patient, out var defibrillationStartedAt) &&
             _timing.CurTime - defibrillationStartedAt >= TimeSpan.FromSeconds(30));

        ClearTrackedMedicalDoAfter(rescue);
        if (completedDefibrillationPolicy)
        {
            MarkTerminalDefibrillationFailure(
                agent.Owner,
                rescue,
                patient,
                LuaMRescueFailureReason.Unrevivable,
                "defibrillation failed after the bounded three-attempt/30-second recovery policy; transport/handoff fallback");
        }
        if (completesManualTreatment && TryComp<HTNComponent>(agent.Owner, out var htn))
        {
            FinishPendingPlayerAction(
                agent.Owner,
                rescue,
                htn,
                succeeded
                    ? status
                    : $"failed treat: {status}");
        }
        Dirty(agent.Owner, rescue);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<LuaMRescueAgentComponent, HTNComponent>();
        while (query.MoveNext(out var uid, out var rescue, out var htn))
        {
            // Identity cleanup is lifecycle safety and must precede manual
            // control or self-incapacitation gates.
            PruneDeletedRequiredHandoffPatients(uid, rescue, htn);

            // Life-support preemption owns the still-current patient before any
            // terminal/compatibility reconciliation is allowed to retire its
            // mirrors. Otherwise an arrival or transition on the same tick as
            // oxygen exhaustion can erase the exact mission we must resume.
            if (!HasComp<ActorComponent>(uid) &&
                (!TryComp<MobStateComponent>(uid, out var lifeSupportMob) ||
                 lifeSupportMob.CurrentState is not (MobState.Dead or MobState.Critical)) &&
                UpdateLifeSupport(uid, rescue, htn, frameTime))
            {
                continue;
            }

            ReconcileTerminalPatientIntentOwnership(uid, rescue, htn);
            ReconcileActivePatientIntentOwnership(uid, rescue, htn);

            var holdingHomeHandoff = false;
            if (rescue.AssignedReturnTarget is { Valid: true } configuredHome && !Deleted(configuredHome))
            {
                foreach (var patient in rescue.RequiredOnboardHandoffPatients.ToArray())
                {
                    if (!IsBuckledToAssignedShuttlePatientStrap(patient, rescue))
                        continue;

                    if (TryHoldOnboardPatientForConfirmedHomeHandoff(uid, rescue, htn, patient) ||
                        TryContinueOnboardCare(uid, rescue, htn))
                    {
                        holdingHomeHandoff = true;
                        break;
                    }
                }
            }

            if (holdingHomeHandoff)
                continue;

            if (HasComp<ActorComponent>(uid))
                continue;

            if (TryHandleRescueAgentMobState(uid, rescue, htn))
                continue;

            if (TryDiscardStaleTrackedMedicalAction(uid, rescue))
                continue;

            if (TryUpdatePendingMedicalEffectVerification(uid, rescue))
                continue;

            if (TryExpireMedicalDoAfter(uid, rescue))
                continue;

            if (rescue.PendingPlayerAction != LuaMRescuePlayerActionKind.None)
            {
                UpdatePendingPlayerAction(uid, rescue, htn, frameTime);
                continue;
            }

            if (TryRefreshPatientGoalAfterGatewayTraversal(uid, rescue, htn))
                continue;

            if (TryContinueOwnedPatientRoute(uid, rescue, htn))
                continue;

            if (!rescue.AutoAcquireTargets)
                continue;

            rescue.TargetRefreshAccumulator += frameTime;
            if (rescue.TargetRefreshAccumulator < rescue.TargetRefreshInterval)
                continue;

            rescue.TargetRefreshAccumulator = 0f;
            UpdateAssignedTarget(uid, rescue, htn);
        }

        var escortQuery = EntityQueryEnumerator<LuaMRescueEscortComponent>();
        while (escortQuery.MoveNext(out var escortUid, out var escort))
        {
            if (HasComp<ActorComponent>(escortUid) ||
                TryComp<MobStateComponent>(escortUid, out var mobState) &&
                mobState.CurrentState is MobState.Dead or MobState.Critical)
            {
                continue;
            }

            UpdateEscortLifeSupport(escortUid, escort, frameTime);
        }
    }

    private bool TryRefreshPatientGoalAfterGatewayTraversal(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn)
    {
        if (rescue.LifeSupportEmergencyActive ||
            rescue.TaskStage != LuaMRescueTaskStage.FollowingPatient ||
            rescue.AssignedTarget is not { Valid: true } patient ||
            rescue.TaskPatientTarget != patient ||
            rescue.ActivityContext.TerminalStatus != LuaMRescueTerminalStatus.Active ||
            rescue.ActivityContext.Target != patient ||
            Deleted(patient) ||
            Transform(uid).MapID != Transform(patient).MapID ||
            rescue.PendingMedicalDoAfterTarget is { Valid: true })
        {
            return false;
        }

        var agentMap = Transform(uid).MapID;
        var staleGatewayGoal = false;
        if (htn.Blackboard.TryGetValue<EntityCoordinates>(
                NPCBlackboard.FollowTarget,
                out var follow,
                EntityManager))
        {
            if (follow.EntityId == patient)
                return false;

            if (follow.EntityId is { Valid: true } previous &&
                !Deleted(previous) &&
                HasComp<GatewayComponent>(previous) &&
                Transform(previous).MapID != agentMap)
            {
                staleGatewayGoal = true;
            }
        }
        else if (rescue.ActivityContext.Destination is { EntityId: { Valid: true } previous } &&
                 !Deleted(previous) &&
                 HasComp<GatewayComponent>(previous) &&
                 Transform(previous).MapID != agentMap)
        {
            staleGatewayGoal = true;
        }

        if (!staleGatewayGoal)
            return false;

        SetFollowTarget(uid, rescue, htn, patient);
        rescue.LastTargetTrackingStatus =
            $"gateway traversal complete; restored patient goal {FormatEntityRef(patient)}";
        Dirty(uid, rescue);
        return true;
    }

    private bool TryContinueOwnedPatientRoute(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn)
    {
        if (rescue.LifeSupportEmergencyActive ||
            rescue.PendingPlayerAction != LuaMRescuePlayerActionKind.None ||
            rescue.PendingMedicalDoAfterTarget is { Valid: true } ||
            rescue.AssignedTarget is not { Valid: true } patient ||
            Deleted(patient) ||
            rescue.TaskPatientTarget != patient ||
            rescue.TaskStage is not (LuaMRescueTaskStage.FollowingPatient or LuaMRescueTaskStage.ManualAction) ||
            rescue.ActivityContext.TerminalStatus != LuaMRescueTerminalStatus.Active ||
            rescue.ActivityContext.Target != patient ||
            rescue.ActivityContext.Activity is not (LuaMRescueActivity.PlanningRoute or
                LuaMRescueActivity.ApproachPatient or LuaMRescueActivity.ManualAction) ||
            HasUsablePatientMovementGoal(uid, htn, patient))
        {
            return false;
        }

        // Route probes are asynchronous. AutoAcquireTargets may deliberately be disabled for
        // an explicitly owned mission, so the ownership loop must still poll a pending route
        // after HoldMovementForRoute removed the old HTN executor.
        SetFollowTarget(uid, rescue, htn, patient);
        return true;
    }

    private bool HasUsablePatientMovementGoal(EntityUid uid, HTNComponent htn, EntityUid patient)
    {
        if (!htn.Blackboard.TryGetValue<EntityCoordinates>(
                NPCBlackboard.FollowTarget,
                out var follow,
                EntityManager))
        {
            return false;
        }

        var sourceMap = Transform(uid).MapID;
        var targetMap = Transform(patient).MapID;
        if (follow.EntityId == patient)
            return sourceMap == targetMap;

        if (sourceMap == targetMap ||
            sourceMap == MapId.Nullspace ||
            targetMap == MapId.Nullspace ||
            follow.EntityId is not { Valid: true } gateway ||
            Deleted(gateway) ||
            !TryComp<GatewayComponent>(gateway, out var gatewayComp) ||
            !TryComp<PortalComponent>(gateway, out var portalComp) ||
            !TryComp<TransformComponent>(gateway, out var gatewayXform) ||
            _interaction.InRangeUnobstructed(
                uid,
                gateway,
                SharedInteractionSystem.InteractionRange))
        {
            return false;
        }

        return TryResolveSingleReciprocalGatewayEndpoint(
            gateway,
            gatewayComp,
            portalComp,
            gatewayXform,
            sourceMap,
            targetMap,
            out _);
    }

    private void UpdateEscortLifeSupport(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        float frameTime)
    {
        if (!escort.AutoManageLifeSupport)
            return;

        escort.LifeSupportCheckAccumulator += frameTime;
        if (escort.LifeSupportCheckAccumulator < escort.LifeSupportCheckInterval)
            return;

        escort.LifeSupportCheckAccumulator = 0f;
        if (!TryComp<InternalsComponent>(uid, out var internals) ||
            internals.GasTankEntity is not { Valid: true } activeTank ||
            Deleted(activeTank) ||
            !TryComp<GasTankComponent>(activeTank, out var activeGasTank) ||
            !_internals.AreInternalsWorking(uid, internals))
        {
            if (CanBreatheAmbientAir(uid))
            {
                escort.LastLifeSupportStatus =
                    "standby; internals are not active; ambient atmosphere breathable";
                Dirty(uid, escort);
                return;
            }

            var reserve = FindAccessibleLifeSupportReserve(uid, EntityUid.Invalid);
            if (reserve is { } available && _gasTank.ConnectToInternals(available, uid))
            {
                var reservePressure = Math.Max(0f, available.Comp.Air.Pressure);
                escort.LifeSupportSwapCount++;
                escort.LastLifeSupportStatus = reservePressure > escort.LifeSupportSwapPressure
                    ? $"automatic compatible oxygen reconnection succeeded; pressure={reservePressure:0} kPa; swaps={escort.LifeSupportSwapCount}"
                    : $"last compatible oxygen supply connected; pressure={reservePressure:0} kPa; swaps={escort.LifeSupportSwapCount}";
                Dirty(uid, escort);
                return;
            }

            escort.LastLifeSupportStatus =
                "life-support unavailable; no accessible compatible gas reserve in unbreathable atmosphere";
            Dirty(uid, escort);
            return;
        }

        var activePressure = Math.Max(0f, activeGasTank.Air.Pressure);
        if (activePressure > escort.LifeSupportSwapPressure)
        {
            escort.LastLifeSupportStatus = $"primary supply nominal; pressure={activePressure:0} kPa";
            Dirty(uid, escort);
            return;
        }

        if (CanBreatheAmbientAir(uid))
        {
            var disconnected = _gasTank.DisconnectFromInternals((activeTank, activeGasTank), uid, forced: true);
            if (disconnected &&
                internals.GasTankEntity == null &&
                _respirator.CanMetabolizeInhaledAir(uid))
            {
                escort.LastLifeSupportStatus =
                    $"low oxygen tank disconnected; breathing verified from ambient atmosphere; pressure={activePressure:0} kPa";
                Dirty(uid, escort);
                return;
            }
        }

        var replacement = FindAccessibleLifeSupportReserve(uid, activeTank);
        var replacementPressure = replacement is { } candidate
            ? Math.Max(0f, candidate.Comp.Air.Pressure)
            : 0f;
        if (replacement is not { } compatible || replacementPressure <= activePressure)
        {
            escort.LastLifeSupportStatus =
                $"critical oxygen reserve; no better accessible compatible replacement; pressure={activePressure:0} kPa";
            Dirty(uid, escort);
            return;
        }

        if (!_gasTank.ConnectToInternals(compatible, uid))
        {
            escort.LastLifeSupportStatus =
                $"critical oxygen reserve; automatic compatible replacement failed; pressure={activePressure:0} kPa";
            Dirty(uid, escort);
            return;
        }

        escort.LifeSupportSwapCount++;
        escort.LastLifeSupportStatus = replacementPressure > escort.LifeSupportSwapPressure
            ? $"automatic compatible oxygen replacement connected; pressure={replacementPressure:0} kPa; swaps={escort.LifeSupportSwapCount}"
            : $"last compatible oxygen replacement connected; pressure={replacementPressure:0} kPa; swaps={escort.LifeSupportSwapCount}";
        Dirty(uid, escort);
    }

    private bool UpdateLifeSupport(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        float frameTime)
    {
        if (!rescue.AutoManageLifeSupport)
            return false;

        rescue.LifeSupportCheckAccumulator += frameTime;
        if (rescue.LifeSupportCheckAccumulator < rescue.LifeSupportCheckInterval)
            return rescue.LifeSupportEmergencyActive;

        rescue.LifeSupportCheckAccumulator = 0f;
        if (!TryComp<InternalsComponent>(uid, out var internals) ||
            internals.GasTankEntity is not { Valid: true } activeTank ||
            Deleted(activeTank) ||
            !TryComp<GasTankComponent>(activeTank, out var activeGasTank) ||
            !_internals.AreInternalsWorking(uid, internals))
        {
            if (CanBreatheAmbientAir(uid))
            {
                ResolveLifeSupportEmergency(uid, rescue, htn);
                rescue.LastLifeSupportStatus = "standby; internals are not active; ambient atmosphere breathable";
                Dirty(uid, rescue);
                return false;
            }

            var lastChance = FindAccessibleLifeSupportReserve(uid, EntityUid.Invalid);
            if (lastChance is { } available &&
                _respirator.CanMetabolizeGas(uid, available.Comp.Air) &&
                _gasTank.ConnectToInternals(available, uid))
            {
                var lastChancePressure = Math.Max(0f, available.Comp.Air.Pressure);
                rescue.LifeSupportSwapCount++;
                if (lastChancePressure > rescue.LifeSupportSwapPressure)
                {
                    ResolveLifeSupportEmergency(uid, rescue, htn);
                    rescue.LastLifeSupportStatus =
                        $"automatic oxygen reconnection succeeded; pressure={lastChancePressure:0} kPa; swaps={rescue.LifeSupportSwapCount}";
                    Dirty(uid, rescue);
                    return false;
                }

                rescue.LastLifeSupportStatus =
                    $"last oxygen supply connected; pressure={lastChancePressure:0} kPa; emergency return active";
                ActivateOrMaintainLifeSupportEmergency(
                    uid,
                    rescue,
                    htn,
                    $"only low-pressure oxygen remains; pressure={lastChancePressure:0} kPa");
                Dirty(uid, rescue);
                return true;
            }

            rescue.LastLifeSupportStatus = "life-support emergency; internals unavailable in unbreathable atmosphere";
            ActivateOrMaintainLifeSupportEmergency(uid, rescue, htn, "internals unavailable");
            Dirty(uid, rescue);
            return true;
        }

        var activePressure = Math.Max(0f, activeGasTank.Air.Pressure);
        if (activePressure > rescue.LifeSupportSwapPressure)
        {
            ResolveLifeSupportEmergency(uid, rescue, htn);
            rescue.LastLifeSupportStatus = $"primary supply nominal; pressure={activePressure:0} kPa";
            Dirty(uid, rescue);
            return false;
        }

        if (CanBreatheAmbientAir(uid))
        {
            var disconnected = _gasTank.DisconnectFromInternals((activeTank, activeGasTank), uid, forced: true);
            var internalsReleased = internals.GasTankEntity == null;
            if (disconnected && internalsReleased && _respirator.CanMetabolizeInhaledAir(uid))
            {
                ResolveLifeSupportEmergency(uid, rescue, htn);
                rescue.LastLifeSupportStatus =
                    $"low oxygen tank disconnected; breathing verified from ambient atmosphere; pressure={activePressure:0} kPa";
                Dirty(uid, rescue);
                return false;
            }
        }

        var reserve = FindAccessibleLifeSupportReserve(uid, activeTank);
        if (reserve == null || reserve.Value.Owner == activeTank)
        {
            rescue.LastLifeSupportStatus = $"critical oxygen reserve; no accessible replacement; pressure={activePressure:0} kPa";
            return HandleCriticalLifeSupportWithoutReplacement(
                uid,
                rescue,
                htn,
                (activeTank, activeGasTank),
                $"no accessible replacement; pressure={activePressure:0} kPa");
        }

        var reservePressure = Math.Max(0f, reserve.Value.Comp.Air.Pressure);
        if (reservePressure <= 0f ||
            !_respirator.CanMetabolizeGas(uid, reserve.Value.Comp.Air))
        {
            rescue.LastLifeSupportStatus = $"critical oxygen reserve; replacement unusable; pressure={activePressure:0} kPa";
            return HandleCriticalLifeSupportWithoutReplacement(
                uid,
                rescue,
                htn,
                (activeTank, activeGasTank),
                $"replacement unusable; pressure={activePressure:0} kPa");
        }

        if (!_gasTank.ConnectToInternals(reserve.Value, uid))
        {
            rescue.LastLifeSupportStatus = $"critical oxygen reserve; automatic replacement failed; pressure={activePressure:0} kPa";
            return HandleCriticalLifeSupportWithoutReplacement(
                uid,
                rescue,
                htn,
                (activeTank, activeGasTank),
                $"automatic replacement failed; pressure={activePressure:0} kPa");
        }

        rescue.LifeSupportSwapCount++;
        if (reservePressure <= rescue.LifeSupportSwapPressure)
        {
            rescue.LastLifeSupportStatus =
                $"last oxygen replacement connected; pressure={reservePressure:0} kPa; emergency return active";
            ActivateOrMaintainLifeSupportEmergency(
                uid,
                rescue,
                htn,
                $"only low-pressure oxygen remains; pressure={reservePressure:0} kPa");
            Dirty(uid, rescue);
            return true;
        }

        ResolveLifeSupportEmergency(uid, rescue, htn);
        rescue.LastLifeSupportStatus =
            $"automatic oxygen replacement connected; pressure={reservePressure:0} kPa; swaps={rescue.LifeSupportSwapCount}";
        Dirty(uid, rescue);
        return false;
    }

    private bool HandleCriticalLifeSupportWithoutReplacement(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        Entity<GasTankComponent> reserveActiveTank,
        string reason)
    {
        if (CanBreatheAmbientAir(uid))
        {
            var disconnected = _gasTank.DisconnectFromInternals(reserveActiveTank, uid, forced: true);
            var internalsReleased = TryComp<InternalsComponent>(uid, out var releasedInternals) &&
                                    releasedInternals.GasTankEntity == null;
            if (disconnected && internalsReleased && _respirator.CanMetabolizeInhaledAir(uid))
            {
                ResolveLifeSupportEmergency(uid, rescue, htn);
                rescue.LastLifeSupportStatus +=
                    "; depleted tank disconnected; breathing verified from ambient atmosphere";
                Dirty(uid, rescue);
                return false;
            }

            reason = $"{reason}; failed to release depleted tank for ambient breathing";
        }

        rescue.LastLifeSupportStatus += "; unbreathable atmosphere; emergency return active";
        ActivateOrMaintainLifeSupportEmergency(uid, rescue, htn, reason);
        Dirty(uid, rescue);
        return true;
    }

    private bool CanBreatheAmbientAir(EntityUid uid)
    {
        var mixture = _atmosphere.GetContainingMixture(uid);
        return mixture != null && _respirator.CanMetabolizeGas(uid, mixture);
    }

    private void ActivateOrMaintainLifeSupportEmergency(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        string reason)
    {
        var carryingEmergencyPatient = false;
        if (!rescue.LifeSupportEmergencyActive)
        {
            rescue.LifeSupportEmergencyActive = true;
            rescue.LifeSupportEmergencyStartedAt = _timing.CurTime;
            rescue.LifeSupportEmergencyCount++;

            if (TryGetActivePatientTarget(rescue, out var patient) && !Deleted(patient))
            {
                rescue.LifeSupportEmergencyPatient = patient;
                carryingEmergencyPatient = IsPullingTarget(uid, patient) ||
                                           IsBuckledToAssignedShuttlePatientStrap(patient, rescue);

                // Automatic ownership must remain durable even when the patient
                // is already being carried or is buckled onboard. Manual lineage
                // has its own stronger owner and is resumed before this queue.
                if (!IsManualPatientIntent(rescue, patient))
                    rescue.DeferredPatientTargets.TryAdd(patient, _timing.CurTime);

                if (!carryingEmergencyPatient)
                {
                    PrepareForExternalIntentReplacement(uid, EntityUid.Invalid);
                    rescue.AssignedTarget = null;
                    rescue.EvacuatingTarget = null;
                    rescue.DeathSignalTarget = null;
                    ClearRescueTask(uid, rescue, $"life-support emergency deferred {FormatEntityRef(patient)}");
                    ClearPendingPlayerAction(rescue, "cancelled by life-support emergency");
                }
            }

            if (!carryingEmergencyPatient)
                BeginLifeSupportReturnActivity(uid, rescue);
        }

        var patientStatus = rescue.LifeSupportEmergencyPatient is { Valid: true } patientUid && !Deleted(patientUid)
            ? $"; patient={FormatEntityRef(patientUid)}"
            : string.Empty;
        TrySendRescueStatusComms(
            uid,
            rescue,
            $"life-support-emergency:{rescue.LifeSupportEmergencyCount}",
            $"Авария жизнеобеспечения: {reason}. Отхожу к спасательному шаттлу{patientStatus}.");

        if (!CanUseAssignedShuttleForLifeSupport(rescue))
        {
            BeginLifeSupportReturnActivity(uid, rescue);
            HoldMovementForRoute(uid, htn);
            _activity.Block(
                uid,
                LuaMRescueFailureReason.LifeSupportUnavailable,
                LuaMRescueActivity.ReturnToShuttle,
                out _);
            rescue.LastShuttleReturnStatus =
                $"LifeSupportUnavailable: no assigned rescue shuttle; {reason}";
            rescue.LastLifeSupportStatus += "; no assigned shuttle; holding position and requesting assistance";
            return;
        }

        if (IsOnAssignedShuttle(uid, rescue))
        {
            BeginLifeSupportReturnActivity(uid, rescue);
            HoldMovementForRoute(uid, htn);
            if (TryRouteShuttleHome(uid, rescue))
            {
                rescue.LastShuttleReturnStatus =
                    $"life-support emergency onboard; {rescue.LastShuttleReturnStatus}; {reason}";
            }
            return;
        }

        SetFollowShuttle(uid, rescue, htn);
    }

    private void BeginLifeSupportReturnActivity(EntityUid uid, LuaMRescueAgentComponent rescue)
    {
        EntityUid? returnTarget = rescue.AssignedShuttle is { Valid: true } shuttle && !Deleted(shuttle)
            ? shuttle
            : rescue.AssignedShuttleAnchor is { Valid: true } anchor && !Deleted(anchor)
                ? anchor
                : null;
        EntityCoordinates? destination = TryGetShuttleAnchorCoordinates(rescue, out var coordinates)
            ? coordinates
            : null;
        BeginPatientActivity(
            uid,
            rescue,
            LuaMRescueActivity.ReturnToShuttle,
            returnTarget,
            destination);
    }

    private void ResolveLifeSupportEmergency(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn)
    {
        if (!rescue.LifeSupportEmergencyActive)
            return;

        var duration = Math.Max(0d, (_timing.CurTime - rescue.LifeSupportEmergencyStartedAt).TotalSeconds);
        var interruptedPatient = rescue.LifeSupportEmergencyPatient;
        rescue.LifeSupportEmergencyActive = false;
        rescue.LastLifeSupportStatus =
            $"life-support emergency cleared after {duration:0.0}s; patient mission queued for recovery";
        rescue.LastTargetTrackingStatus =
            $"life-support recovered; deferred={FormatEntityRef(interruptedPatient)}";
        rescue.LifeSupportEmergencyPatient = null;
        rescue.TargetRefreshAccumulator = rescue.TargetRefreshInterval;
        TryResumeManualOverrideTarget(uid, rescue, htn);
        if (rescue.AssignedTarget == null)
            TryResumeDeferredPatientTask(uid, rescue, htn, interruptedPatient);
        Dirty(uid, rescue);
    }

    private Entity<GasTankComponent>? FindAccessibleLifeSupportReserve(EntityUid uid, EntityUid activeTank)
    {
        Entity<GasTankComponent>? best = null;
        var bestPressure = 0f;

        void Consider(EntityUid candidate)
        {
            if (candidate == activeTank ||
                Deleted(candidate) ||
                !TryComp<GasTankComponent>(candidate, out var tank) ||
                !_gasTank.CanConnectToInternals((candidate, tank)) ||
                !_respirator.CanMetabolizeGas(uid, tank.Air))
                return;

            var pressure = Math.Max(0f, tank.Air.Pressure);
            if (pressure <= bestPressure)
                return;

            best = (candidate, tank);
            bestPressure = pressure;
        }

        if (TryComp<HandsComponent>(uid, out var hands))
        {
            foreach (var hand in hands.Hands.Values)
            {
                if (hand.HeldEntity is { Valid: true } held)
                    Consider(held);
            }
        }

        if (TryComp<InventoryComponent>(uid, out var inventory))
        {
            foreach (var slot in inventory.Slots)
            {
                if (_inventory.TryGetSlotEntity(uid, slot.Name, out var item, inventory) &&
                    item is { Valid: true } equipped)
                    Consider(equipped);
            }
        }

        return best;
    }

    public LuaMRescueLifeSupportSnapshot GetLifeSupportSnapshot(EntityUid uid)
    {
        TryComp<LuaMRescueAgentComponent>(uid, out var rescue);
        if (!TryComp<InternalsComponent>(uid, out var internals) ||
            internals.GasTankEntity is not { Valid: true } tank ||
            Deleted(tank) ||
            !TryComp<GasTankComponent>(tank, out var gasTank))
        {
            return new LuaMRescueLifeSupportSnapshot(
                false,
                rescue?.LifeSupportEmergencyActive ?? false,
                0f,
                0,
                0f,
                rescue?.LifeSupportSwapCount ?? 0,
                rescue?.LifeSupportEmergencyCount ?? 0,
                rescue?.LastLifeSupportStatus ?? "internals disconnected; no active gas tank");
        }

        var reserveCount = 0;
        var bestReservePressure = 0f;
        var seen = new HashSet<EntityUid> { tank };
        if (TryComp<InventoryComponent>(uid, out var inventory))
        {
            foreach (var slot in inventory.Slots)
            {
                if (!_inventory.TryGetSlotEntity(uid, slot.Name, out var item, inventory) ||
                    item is not { Valid: true } candidate ||
                    !seen.Add(candidate) ||
                    !TryComp<GasTankComponent>(candidate, out var candidateTank))
                    continue;

                reserveCount++;
                bestReservePressure = Math.Max(bestReservePressure, Math.Max(0f, candidateTank.Air.Pressure));
            }
        }

        var active = _internals.AreInternalsWorking(uid, internals);
        var pressure = Math.Max(0f, gasTank.Air.Pressure);
        var status = rescue?.LastLifeSupportStatus;
        if (string.IsNullOrWhiteSpace(status) || status == "not checked")
        {
            status = !active
                ? $"internals unavailable; tank pressure={pressure:0} kPa"
                : gasTank.IsLowPressure
                    ? $"oxygen reserve low; pressure={pressure:0} kPa"
                    : $"internals active; pressure={pressure:0} kPa";
        }

        return new LuaMRescueLifeSupportSnapshot(
            active,
            rescue?.LifeSupportEmergencyActive ?? false,
            pressure,
            reserveCount,
            bestReservePressure,
            rescue?.LifeSupportSwapCount ?? 0,
            rescue?.LifeSupportEmergencyCount ?? 0,
            status);
    }

    public EntityUid SpawnAgent(EntityUid anchor, EntityUid? followTarget, ICommonSession? controller, bool control)
    {
        var spawnCoordinates = Transform(anchor).Coordinates;
        var agent = Spawn(RescueAgentPrototype, spawnCoordinates);
        var rescue = EnsureComp<LuaMRescueAgentComponent>(agent);

        if (followTarget is { Valid: true } target &&
            TryComp<HTNComponent>(agent, out var htn))
        {
            SetFollowTarget(agent, rescue, htn, target);
        }

        if (control && controller != null)
            _mind.ControlMob(controller.UserId, agent);

        return agent;
    }

    public bool TrySpawnAgent(
        EntityUid anchor,
        EntityUid? followTarget,
        ICommonSession? controller,
        bool control,
        out EntityUid agent,
        out string status)
    {
        RetireDeadAgentsForReplacement("direct rescue agent replacement");

        if (TryFindActiveAgent(out var activeAgent, out _))
        {
            agent = activeAgent;
            status = $"Only one Aibolit rescue agent may be active. Existing agent={GetNetEntity(activeAgent)}; dispatch/spawn blocked.";
            return false;
        }

        agent = SpawnAgent(anchor, followTarget, controller, control);
        status = $"Aibolit rescue agent spawned: {GetNetEntity(agent)}.";
        return true;
    }

    public bool TryFindActiveAgent(out EntityUid agent, out LuaMRescueAgentComponent rescue)
    {
        agent = default;
        rescue = default!;
        var bestScore = int.MaxValue;
        var query = EntityQueryEnumerator<LuaMRescueAgentComponent>();
        while (query.MoveNext(out var uid, out var rescueComp))
        {
            if (Deleted(uid) ||
                TryComp<MobStateComponent>(uid, out var mobState) &&
                mobState.CurrentState == MobState.Dead)
                continue;

            var ownsMission = rescueComp.LifeSupportEmergencyActive ||
                              rescueComp.AssignedTarget is { Valid: true } ||
                              rescueComp.DeathSignalTarget is { Valid: true } ||
                              rescueComp.EvacuatingTarget is { Valid: true } ||
                              rescueComp.OnboardCareTarget is { Valid: true } ||
                              rescueComp.TaskPatientTarget is { Valid: true } ||
                              rescueComp.ActivityContext.Target is { Valid: true } &&
                              rescueComp.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active;
            var score = ownsMission ? 0 : 1;
            if (score > bestScore ||
                score == bestScore && agent.Valid && uid.Id >= agent.Id)
            {
                continue;
            }

            bestScore = score;
            agent = uid;
            rescue = rescueComp;
        }

        return agent.Valid;
    }

    private bool TryHandleRescueAgentMobState(EntityUid uid, LuaMRescueAgentComponent rescue, HTNComponent htn)
    {
        if (!TryComp<MobStateComponent>(uid, out var mobState))
            return false;

        if (mobState.CurrentState is MobState.Dead or MobState.Critical)
        {
            var status = mobState.CurrentState == MobState.Dead
                ? $"{RescueAgentDisabledStatusPrefix} dead; waiting for recovery"
                : $"{RescueAgentDisabledStatusPrefix} critical; waiting for stabilization";

            if (!rescue.LastAutoEvacuationStatus.Equals(status, StringComparison.Ordinal))
            {
                DeferPatientInterruptedByAgentState(uid, rescue, mobState.CurrentState);
                if (rescue.EvacuatingTarget is { Valid: true } evacuatingTarget &&
                    !Deleted(evacuatingTarget))
                {
                    StopPullingTarget(uid, evacuatingTarget);
                }

                ClearRescueTask(uid, rescue, "rescue agent disabled");
                rescue.EvacuatingTarget = null;
                rescue.AssignedTarget = null;
                rescue.AssignedPatientStrap = null;
                rescue.ArrivalReportedTarget = null;
                rescue.TriageReportedTarget = null;
                rescue.DeathSignalTarget = null;
                rescue.DeathSignalDispatchReported = false;
                ResetTargetProgress(rescue);
                rescue.LastAutoEvacuationStatus = status;
                rescue.LastTargetTrackingStatus = $"self-state={mobState.CurrentState}; auto rescue paused";
                ClearFollowTarget(uid, rescue, htn);
                Dirty(uid, rescue);
            }

            return true;
        }

        if (rescue.LastAutoEvacuationStatus.StartsWith(RescueAgentDisabledStatusPrefix, StringComparison.Ordinal))
        {
            ResetTargetProgress(rescue);
            rescue.LastAutoEvacuationStatus = $"{RescueAgentRecoveredStatusPrefix} returning to shuttle anchor before reacquire";
            rescue.LastTargetTrackingStatus = "self-state recovered; returning to shuttle anchor";

            if (CanUseAssignedShuttle(rescue))
                SetFollowShuttle(uid, rescue, htn);
            else
                ClearFollowTarget(uid, rescue, htn);

            Dirty(uid, rescue);
            return true;
        }

        if (!rescue.LastAutoEvacuationStatus.StartsWith(RescueAgentRecoveredStatusPrefix, StringComparison.Ordinal))
            return false;

        if (CanUseAssignedShuttle(rescue) &&
            !IsAtAssignedShuttleAnchor(uid, rescue))
        {
            SetFollowShuttle(uid, rescue, htn);
            return true;
        }

        rescue.LastAutoEvacuationStatus = "rescue-agent-recovered: ready";
        rescue.LastTargetTrackingStatus = "self-state recovered; ready to reacquire";
        Dirty(uid, rescue);
        if (TryResumeManualOverrideTarget(uid, rescue, htn) ||
            TryResumeDeferredPatientTask(uid, rescue, htn))
        {
            return true;
        }
        return false;
    }

    public List<string> BuildRescueStatusLines()
    {
        var lines = new List<string>();
        var query = EntityQueryEnumerator<LuaMRescueAgentComponent>();
        while (query.MoveNext(out var uid, out var rescue))
        {
            PruneSkippedTargets(rescue);
            PruneSkippedSupplyTargets(rescue);
            PruneSkippedDeliveryTargets(rescue);
            lines.Add(BuildRescueStatusLine(uid, rescue));
        }

        lines.AddRange(_rescueTeam.BuildRescueTeamStatusLines());
        return lines;
    }

    public bool TryGetReliabilitySnapshot(EntityUid agent, out LuaMRescueReliabilitySnapshot snapshot)
    {
        snapshot = default;
        if (!TryComp<LuaMRescueAgentComponent>(agent, out var rescue))
            return false;

        var durableRouteCircuits = rescue.RouteFailureAttempts.Keys.Count(target =>
            rescue.SkippedTargets.TryGetValue(target, out var until) && until == TimeSpan.MaxValue);
        var activeRetryBudgets = rescue.RouteFailureAttempts.Count +
                                 rescue.AnalysisAttempts.Count +
                                 rescue.TreatmentAttempts.Count +
                                 rescue.DefibrillationAttempts.Count +
                                 rescue.PullAttempts.Count +
                                 rescue.EvacuationUnbuckleAttempts.Count +
                                 rescue.PatientBuckleAttempts.Count +
                                 rescue.OnboardCareAttempts.Count +
                                 rescue.OnboardHandoffAttempts.Count;
        var openCircuits = durableRouteCircuits +
                           rescue.TerminalAnalysisFailures.Count +
                           rescue.TerminalTreatmentFailures.Count +
                           rescue.TerminalDefibrillationFailures.Count +
                           rescue.TerminalPullFailures.Count +
                           rescue.TerminalEvacuationUnbuckleFailures.Count +
                           rescue.TerminalPatientBuckleFailures.Count +
                           rescue.TerminalOnboardCareFailures.Count;
        var pendingOperations = (rescue.PendingPlayerAction != LuaMRescuePlayerActionKind.None ? 1 : 0) +
                                (rescue.PendingMedicalDoAfterTarget is { Valid: true } ? 1 : 0) +
                                (rescue.PendingMedicalEffectVerification ? 1 : 0) +
                                (rescue.DormantRouteProbeInFlight ? 1 : 0);

        snapshot = new LuaMRescueReliabilitySnapshot(
            activeRetryBudgets,
            openCircuits,
            pendingOperations,
            rescue.RequiredOnboardHandoffPatients.Count,
            rescue.ActivityContext.Generation,
            rescue.ActivityContext.TerminalStatus);
        return true;
    }

    public string BuildRescueRadioStatus()
    {
        var summaries = new List<string>();
        var agentCount = 0;
        var query = EntityQueryEnumerator<LuaMRescueAgentComponent>();
        while (query.MoveNext(out var uid, out var rescue))
        {
            agentCount++;
            if (summaries.Count >= 2)
                continue;

            var patient = rescue.LifeSupportEmergencyActive
                ? rescue.LifeSupportEmergencyPatient
                : null;
            patient ??= rescue.EvacuatingTarget ??
                          rescue.AssignedTarget ??
                          rescue.DeathSignalTarget ??
                          rescue.TaskPatientTarget;
            var patientName = FormatRadioEntityName(patient, "цель не назначена");
            var shuttle = rescue.AssignedShuttle is { Valid: true } assignedShuttle && !Deleted(assignedShuttle)
                ? "медборт назначен"
                : "медборт не назначен";
            var status = SelectRadioStatus(
                rescue.LifeSupportEmergencyActive ? rescue.LastLifeSupportStatus : string.Empty,
                rescue.LastOnboardCareStatus,
                rescue.LastAutoTreatmentStatus,
                rescue.LastAutoDefibStatus,
                rescue.LastAutoEvacuationStatus,
                rescue.LastShuttleReturnStatus,
                rescue.LastRouteBlockHoldStatus);

            summaries.Add($"{Name(uid)}: фаза {GetRescuePhase(uid, rescue)}; пациент: {patientName}; {shuttle}; {status}.");
        }

        if (agentCount == 0)
            return "медгруппа не развернута; активного Айболита в секторе нет.";

        var extra = agentCount > summaries.Count
            ? $" Еще активных единиц: {agentCount - summaries.Count}."
            : string.Empty;

        return $"медгруппа активна: {agentCount}. {string.Join(" ", summaries)}{extra}";
    }

    /// <summary>
    /// Accepts a patient self-dispatch without granting radio users the
    /// replacement semantics of the administrative order API.
    /// </summary>
    public bool TryOrderAgentFromRadio(EntityUid agent, EntityUid target, out string status)
    {
        status = string.Empty;

        if (!TryComp<LuaMRescueAgentComponent>(agent, out var rescue) ||
            !TryComp<HTNComponent>(agent, out _))
        {
            status = $"{FormatEntityRef(agent)} is not a LuaM rescue agent.";
            return false;
        }

        if (rescue.LifeSupportEmergencyActive)
        {
            status =
                $"{FormatEntityRef(agent)} is returning during a life-support emergency and cannot accept a radio dispatch.";
            return false;
        }

        if (HasComp<ActorComponent>(agent))
        {
            status = $"{FormatEntityRef(agent)} is under manual player control.";
            return false;
        }

        if (!_activity.IsEligibleRescuePatient(
                agent,
                target,
                LuaMRescuePatientRequestKind.AutomaticTreatment,
                manualOverride: false,
                out var eligibilityFailure))
        {
            status =
                $"Radio caller {FormatEntityRef(target)} is not an eligible medical patient: {eligibilityFailure}.";
            return false;
        }

        if (TryGetConflictingRadioPatientOwner(rescue, target, out var currentPatient, out var alreadyAssigned))
        {
            status =
                $"{FormatEntityRef(agent)} is already committed to {FormatEntityRef(currentPatient)}; " +
                $"radio caller {FormatEntityRef(target)} cannot replace the active patient.";
            return false;
        }

        if (alreadyAssigned)
        {
            status =
                $"{FormatEntityRef(agent)} already owns the rescue task for radio caller {FormatEntityRef(target)}.";
            return true;
        }

        return TryOrderAgent(agent, target, out status);
    }

    private bool TryGetConflictingRadioPatientOwner(
        LuaMRescueAgentComponent rescue,
        EntityUid requestedTarget,
        out EntityUid currentPatient,
        out bool alreadyAssigned)
    {
        alreadyAssigned = false;
        currentPatient = default;
        EntityUid?[] ownedPatients =
        [
            rescue.LifeSupportEmergencyActive ? rescue.LifeSupportEmergencyPatient : null,
            rescue.AssignedTarget,
            rescue.EvacuatingTarget,
            rescue.OnboardCareTarget,
            rescue.DeathSignalTarget,
            rescue.TaskPatientTarget,
            rescue.ManualOverrideTarget,
            rescue.DormantRouteTarget,
            rescue.RouteBlockHoldTarget,
            rescue.PendingMedicalDoAfterTarget,
            rescue.PendingPlayerActionTarget,
            rescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active
                ? rescue.ActivityContext.Target
                : null,
        ];

        foreach (var ownedPatient in ownedPatients)
        {
            if (ownedPatient is not { Valid: true } patient || Deleted(patient))
                continue;

            if (patient == requestedTarget)
            {
                alreadyAssigned = true;
                continue;
            }

            currentPatient = patient;
            return true;
        }

        foreach (var patient in rescue.RequiredOnboardHandoffPatients)
        {
            if (!patient.Valid || Deleted(patient))
                continue;

            if (patient == requestedTarget)
            {
                alreadyAssigned = true;
                continue;
            }

            currentPatient = patient;
            return true;
        }

        return false;
    }

    public bool TryOrderAgent(EntityUid agent, EntityUid? target, out string status)
    {
        status = string.Empty;

        if (!TryComp<LuaMRescueAgentComponent>(agent, out var rescue) ||
            !TryComp<HTNComponent>(agent, out var htn))
        {
            status = $"{FormatEntityRef(agent)} is not a LuaM rescue agent.";
            return false;
        }

        if (HasComp<ActorComponent>(agent))
        {
            status = $"{FormatEntityRef(agent)} is under manual player control.";
            return false;
        }

        if (rescue.LifeSupportEmergencyActive)
        {
            status = rescue.LifeSupportEmergencyPatient is { Valid: true } emergencyPatient
                ? $"Aibolit is returning for life support; patient {FormatEntityRef(emergencyPatient)} remains reserved."
                : "Aibolit is returning for life support and cannot accept a new patient order.";
            return false;
        }

        if (TryGetBlockingOnboardPatientForExplicitOrder(rescue, target, out var onboardPatient))
        {
            status =
                $"Cannot replace the rescue order while {FormatEntityRef(onboardPatient)} still owns " +
                "an onboard handoff; release, re-board, or complete the confirmed home handoff first.";
            return false;
        }

        if (target is not { Valid: true } targetUid)
        {
            PrepareForExternalIntentReplacement(agent, EntityUid.Invalid);
            ClearPatientIntentOwnershipForExplicitOrder(agent, rescue);
            rescue.DeferredPatientTargets.Clear();
            rescue.LastDeferredPatientStatus = "explicit order cleared deferred patients";
            ClearDormantRouteRecovery(rescue, "explicit order cleared", clearFailureBudget: true);
            ClearPendingPlayerAction(rescue);
            ClearRescueTask(agent, rescue, "order cleared");
            ResetTargetProgress(rescue);
            StandbyAtAssignedShuttle(agent, rescue, htn);
            status = $"{FormatEntityRef(agent)} cleared current rescue order and is returning to standby.";
            return true;
        }

        var restartTerminalIntent = rescue.ActivityContext.Target == targetUid &&
                                    rescue.ActivityContext.TerminalStatus is LuaMRescueTerminalStatus.Blocked
                                        or LuaMRescueTerminalStatus.Succeeded
                                        or LuaMRescueTerminalStatus.Failed
                                        or LuaMRescueTerminalStatus.Cancelled;
        var terminalGeneration = rescue.ActivityContext.Generation;
        var clearsDurableRouteFailure =
            rescue.RouteFailureAttempts.ContainsKey(targetUid) &&
            rescue.SkippedTargets.TryGetValue(targetUid, out var skipUntil) &&
            skipUntil == TimeSpan.MaxValue;
        var preserveRequiredOwnershipOnRejection =
            rescue.RequiredOnboardHandoffPatients.Contains(targetUid) &&
            (rescue.AssignedTarget == targetUid ||
             rescue.EvacuatingTarget == targetUid ||
             rescue.TaskPatientTarget == targetUid ||
             rescue.OnboardCareTarget == targetUid ||
             rescue.ActivityContext.Target == targetUid);

        // Ordinary administrative replacement keeps the historical reject-and-
        // clear behavior. The exact Required target is transactional: a failed
        // same-patient retry must not erase the only physical custody owner.
        if (!preserveRequiredOwnershipOnRejection)
        {
            PrepareForExternalIntentReplacement(agent, targetUid);
            ClearPatientIntentOwnershipForExplicitOrder(agent, rescue);
            ClearDormantRouteRecovery(rescue, "explicit order replaced dormant route", clearFailureBudget: true);
            if (clearsDurableRouteFailure)
                rescue.RouteFailureAttempts.Remove(targetUid);
        }

        if (Deleted(targetUid))
        {
            rescue.RequiredOnboardHandoffPatients.Remove(targetUid);
            status = $"Target {targetUid} is deleted.";
            return false;
        }

        if (!_activity.IsEligibleRescuePatient(
                agent,
                targetUid,
                LuaMRescuePatientRequestKind.Manual,
                manualOverride: true,
                out var eligibilityFailure))
        {
            if (preserveRequiredOwnershipOnRejection)
            {
                rescue.LastTargetTrackingStatus =
                    $"required_handoff_order_rejected: target={FormatEntityRef(targetUid)}; " +
                    $"ownership=retained; reason={eligibilityFailure}";
                Dirty(agent, rescue);
                status =
                    $"Target {FormatEntityRef(targetUid)} remains the required handoff owner but the order " +
                    $"is currently blocked: {eligibilityFailure}.";
                return false;
            }

            rescue.RequiredOnboardHandoffPatients.Remove(targetUid);
            StopPullingTarget(agent, targetUid);
            ClearRescueTask(agent, rescue, $"manual order rejected: {eligibilityFailure}");
            ClearFollowTarget(agent, rescue, htn);
            _activity.BeginOrReplaceIntent(
                agent,
                LuaMRescueRole.Aibolit,
                LuaMRescueActivity.Interacting,
                targetUid,
                new EntityCoordinates(targetUid, Vector2.Zero),
                out _);
            _activity.Block(
                agent,
                eligibilityFailure,
                LuaMRescueActivity.Handoff,
                out _);
            rescue.LastTargetTrackingStatus =
                $"target_rejected: {FormatEntityRef(targetUid)}; reason={eligibilityFailure}";
            Dirty(agent, rescue);
            status = $"Target {FormatEntityRef(targetUid)} is not a supported rescue patient: {eligibilityFailure}.";
            return false;
        }

        if (preserveRequiredOwnershipOnRejection)
        {
            PrepareForExternalIntentReplacement(agent, targetUid);
            ClearPatientIntentOwnershipForExplicitOrder(agent, rescue);
            ClearDormantRouteRecovery(rescue, "explicit order replaced dormant route", clearFailureBudget: true);
            if (clearsDurableRouteFailure)
                rescue.RouteFailureAttempts.Remove(targetUid);
        }

        // Eligibility is the commit boundary for an authoritative explicit
        // redispatch. Let external queue owners synchronously retire recovery
        // lineage from the previous Aibolit before any local bounded state is
        // cleared or a replacement intent can start.
        var acceptedOrder = new LuaMRescueExplicitPatientOrderAcceptedEvent(targetUid);
        RaiseLocalEvent(agent, ref acceptedOrder);

        rescue.SkippedTargets.Remove(targetUid);
        rescue.DeferredPatientTargets.Remove(targetUid);
        rescue.AnalysisAttempts.Remove(targetUid);
        rescue.TerminalAnalysisFailures.Remove(targetUid);
        rescue.PullAttempts.Remove(targetUid);
        rescue.NextPullAttemptAt.Remove(targetUid);
        rescue.TerminalPullFailures.Remove(targetUid);
        ClearPatientBuckleFailures(rescue, targetUid);
        rescue.EvacuationUnbuckleAttempts.Remove(targetUid);
        rescue.NextEvacuationUnbuckleAttemptAt.Remove(targetUid);
        rescue.TerminalEvacuationUnbuckleFailures.Remove(targetUid);
        rescue.TreatmentAttempts.Remove(targetUid);
        rescue.TerminalTreatmentFailures.Remove(targetUid);
        rescue.TerminalTreatmentFailureDamage.Remove(targetUid);
        ClearPendingPlayerAction(rescue);

        if (restartTerminalIntent)
        {
            var role = rescue.ActivityRole == LuaMRescueRole.None
                ? LuaMRescueRole.Aibolit
                : rescue.ActivityRole;
            if (!_activity.BeginOrReplaceIntent(
                    agent,
                    role,
                    LuaMRescueActivity.ApproachPatient,
                    targetUid,
                    new EntityCoordinates(targetUid, Vector2.Zero),
                    out var restarted))
            {
                ClearRescueTask(agent, rescue, "explicit terminal intent restart rejected");
                ClearFollowTarget(agent, rescue, htn);
                rescue.LastTargetTrackingStatus =
                    $"explicit_order_intent_failed: target={FormatEntityRef(targetUid)}; " +
                    $"generation={terminalGeneration}; reason={restarted.FailureReason}";
                Dirty(agent, rescue);
                status =
                    $"Could not start a fresh rescue intent for {FormatEntityRef(targetUid)}: " +
                    $"{restarted.FailureReason}.";
                return false;
            }

            rescue.LastIntentCancellationStatus =
                $"explicit terminal redispatch: target={FormatEntityRef(targetUid)}; " +
                $"oldGeneration={terminalGeneration}; newGeneration={restarted.Generation}";
        }

        SetRescueTask(
            agent,
            rescue,
            LuaMRescueTaskStage.FollowingPatient,
            targetUid,
            null,
            $"ordered to rescue {FormatEntityRef(targetUid)}");
        EstablishManualOverrideTarget(agent, rescue, targetUid);
        ResetTargetProgress(rescue);

        if (TryStartOrContinueEvacuation(agent, rescue, htn, targetUid))
        {
            status = $"{FormatEntityRef(agent)} ordered to rescue {FormatEntityRef(targetUid)}.";
            return true;
        }

        SetFollowTarget(agent, rescue, htn, targetUid);
        status = $"{FormatEntityRef(agent)} ordered to follow {FormatEntityRef(targetUid)}.";
        return true;
    }

    public bool TryOrderPlayerAction(
        EntityUid agent,
        LuaMRescuePlayerActionKind action,
        EntityUid? target,
        out string status)
    {
        return TryOrderPlayerAction(agent, action, target, null, null, out status);
    }

    public bool TryOrderPlayerAction(
        EntityUid agent,
        LuaMRescuePlayerActionKind action,
        EntityUid? target,
        string? slot,
        out string status)
    {
        return TryOrderPlayerAction(agent, action, target, slot, null, out status);
    }

    public bool TryOrderPlayerAction(
        EntityUid agent,
        LuaMRescuePlayerActionKind action,
        EntityUid? target,
        string? slot,
        string? itemSelector,
        out string status)
    {
        status = string.Empty;
        var normalizedSlot = NormalizeInventorySlot(slot);
        var normalizedItemSelector = NormalizeItemSelector(itemSelector);

        if (!TryComp<LuaMRescueAgentComponent>(agent, out var rescue) ||
            !TryComp<HTNComponent>(agent, out var htn))
        {
            status = $"{FormatEntityRef(agent)} is not a LuaM rescue agent.";
            return false;
        }

        if (HasComp<ActorComponent>(agent))
        {
            status = $"{FormatEntityRef(agent)} is under manual player control.";
            return false;
        }

        if (action == LuaMRescuePlayerActionKind.None)
        {
            // Clearing the action is itself a valid explicit replacement.
            RevokeManualOverrideTarget(agent, rescue, null, "explicit manual action clear");
            PrepareForExternalIntentReplacement(agent, EntityUid.Invalid);
            ClearPendingPlayerAction(rescue, "cleared");
            ClearRescueTask(agent, rescue, "manual action cleared");
            ResetTargetProgress(rescue);
            StandbyAtAssignedShuttle(agent, rescue, htn);
            status = $"{FormatEntityRef(agent)} cleared pending player action and is returning to standby.";
            return true;
        }

        if (RequiresPlayerActionSlot(action) &&
            string.IsNullOrWhiteSpace(normalizedSlot))
        {
            status = $"{FormatPlayerAction(action)} requires slot=<inventorySlot>.";
            return false;
        }

        if (RequiresPlayerActionTarget(action) &&
            target is not { Valid: true })
        {
            status = $"{FormatPlayerAction(action)} requires target=<entity|player>.";
            return false;
        }

        if (target is { Valid: true } targetUid &&
            Deleted(targetUid))
        {
            status = $"Target {targetUid} is deleted.";
            return false;
        }

        if (target is not { Valid: true } actionTarget)
        {
            // Execute targetless actions before committing an intent
            // replacement. If the action is rejected (for example an empty
            // inventory slot), the previous patient owner, generation and HTN
            // steering must remain completely authoritative.
            if (!TryExecutePlayerAction(
                    agent,
                    rescue,
                    action,
                    null,
                    normalizedSlot,
                    normalizedItemSelector,
                    out var actionStatus))
            {
                rescue.LastPlayerActionStatus = actionStatus;
                Dirty(agent, rescue);
                status = $"{FormatEntityRef(agent)} failed to {FormatPlayerAction(action)}: {actionStatus}.";
                return false;
            }

            RevokeManualOverrideTarget(
                agent,
                rescue,
                null,
                $"accepted targetless action {FormatPlayerAction(action)}");
            if (rescue.EvacuatingTarget is { Valid: true } previousTargetless &&
                !Deleted(previousTargetless))
            {
                StopPullingTarget(agent, previousTargetless);
            }

            rescue.EvacuatingTarget = null;
            rescue.AssignedPatientStrap = null;
            ResetTargetProgress(rescue);
            PrepareForExternalIntentReplacement(agent, EntityUid.Invalid);
            ClearPendingPlayerAction(rescue, actionStatus);
            StandbyAtAssignedShuttle(agent, rescue, htn);
            status = $"{FormatEntityRef(agent)} {actionStatus}.";
            return true;
        }

        // Scheduling a validated target action is the commit point that
        // explicitly supersedes the older patient override.
        RevokeManualOverrideTarget(
            agent,
            rescue,
            null,
            $"accepted target action {FormatPlayerAction(action)}");
        if (rescue.EvacuatingTarget is { Valid: true } previous &&
            !Deleted(previous))
        {
            StopPullingTarget(agent, previous);
        }

        rescue.EvacuatingTarget = null;
        rescue.AssignedPatientStrap = null;
        ResetTargetProgress(rescue);

        rescue.PendingPlayerAction = action;
        rescue.PendingPlayerActionTarget = actionTarget;
        rescue.PendingPlayerActionSlot = normalizedSlot;
        rescue.PendingPlayerActionItem = normalizedItemSelector;
        rescue.PendingVendingStarted = false;
        rescue.PendingVendingProduct = null;
        rescue.PendingVendingDispensedItem = null;
        rescue.PendingStorageTakenItem = null;
        rescue.PlayerActionAccumulator = 0f;
        rescue.LastPlayerActionStatus = $"pending {FormatPlayerAction(action)} {FormatEntityRef(actionTarget)}";
        SetRescueTask(
            agent,
            rescue,
            LuaMRescueTaskStage.ManualAction,
            null,
            actionTarget,
            $"manual {FormatPlayerAction(action)} {FormatEntityRef(actionTarget)}");
        SetFollowTarget(agent, rescue, htn, actionTarget);

        status = $"{FormatEntityRef(agent)} ordered to {FormatPlayerAction(action)} {FormatEntityRef(actionTarget)}.";
        return true;
    }

    private string BuildRescueStatusLine(EntityUid uid, LuaMRescueAgentComponent rescue)
    {
        var target = rescue.EvacuatingTarget ?? rescue.AssignedTarget;
        var activityStatus = _activity.GetSnapshot(uid, out var activitySnapshot)
            ? activitySnapshot.ToDebugString()
            : "activity=unavailable";
        var eligibility = "none";
        var priority = int.MinValue;
        var distance = "none";
        if (target is { Valid: true } patient && !Deleted(patient))
        {
            var kind = TryComp<MobStateComponent>(patient, out var mobState) && mobState.CurrentState == MobState.Dead
                ? LuaMRescuePatientRequestKind.AutomaticEvacuation
                : LuaMRescuePatientRequestKind.AutomaticTreatment;
            priority = _activity.GetPatientPriority(uid, patient, kind, manualOverride: false, out var eligibilityReason);
            eligibility = priority == int.MinValue ? $"false:{eligibilityReason}" : "true";
            if (TryGetDistance(uid, patient, out var patientDistance))
                distance = patientDistance.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }

        var route = rescue.ShuttleReturnRouted
            ? "home"
            : rescue.ShuttleRoutedTarget is { Valid: true } routedTarget && !Deleted(routedTarget)
                ? $"target:{FormatEntityRef(routedTarget)}"
                : "none";

        TryGetReliabilitySnapshot(uid, out var reliability);
        return $"{FormatEntityRef(uid)} phase={GetRescuePhase(uid, rescue)}; {activityStatus}; " +
               $"reliability={reliability.ToDebugString()}; " +
               $"target={FormatEntityRef(target)}; shuttle={FormatEntityRef(rescue.AssignedShuttle)}; " +
               $"eligibility={eligibility}; priority={priority}; distance={distance}; " +
               $"actionRange={rescue.PlayerActionRange:0.00}; held={FormatHeldItems(uid)}; " +
               $"selectedTreatment={FormatEntityRef(rescue.PendingMedicalDoAfterItem)}; " +
               $"bed={FormatEntityRef(rescue.AssignedPatientStrap)}; route={route}; " +
               $"taskStage={FormatRescueTaskStage(rescue.TaskStage)}; " +
               $"taskPatient={FormatEntityRef(rescue.TaskPatientTarget)}; taskSupply={FormatEntityRef(rescue.TaskSupplyTarget)}; " +
               $"targetTrack={rescue.LastTargetTrackingStatus}; " +
               $"deathSignal={FormatEntityRef(rescue.DeathSignalTarget)}; " +
               $"taskLast={rescue.LastTaskStatus}; " +
               $"deferredPatients={rescue.DeferredPatientTargets.Count}; deferredLast={rescue.LastDeferredPatientStatus}; " +
               $"skipped={rescue.SkippedTargets.Count}; skippedSupply={rescue.SkippedSupplyTargets.Count}; " +
               $"skippedDelivery={rescue.SkippedDeliveryTargets.Count}; " +
               $"dormantRoute={FormatEntityRef(rescue.DormantRouteTarget)}; " +
               $"dormantProbe={rescue.DormantRouteProbeCount}; dormantResume={rescue.DormantRouteResumeCount}; " +
               $"dormantNext={rescue.NextDormantRouteProbeAt.TotalSeconds:0.0}s; dormantStatus={rescue.LastDormantRouteStatus}; " +
               $"autoAnalyze={rescue.LastAutoAnalyzeStatus}; " +
               $"autoTreat={rescue.LastAutoTreatmentStatus}; autoDefib={rescue.LastAutoDefibStatus}; " +
               $"autoEvac={rescue.LastAutoEvacuationStatus}; " +
               $"redispatch={rescue.LastRedispatchStatus}; " +
               $"shuttleReturn={rescue.LastShuttleReturnStatus}; " +
               $"rescueAction={rescue.LastRescueActionStatus}; " +
               $"onboardCare={rescue.LastOnboardCareStatus}; " +
               $"onboardAction={rescue.LastOnboardActionStatus}; " +
               $"routeHold={rescue.LastRouteBlockHoldStatus}; " +
               $"arrival={rescue.LastArrivalReportStatus}; " +
               $"triageDecision={rescue.LastTriageDecisionStatus}; " +
               $"autoComms={rescue.LastAutoCommsKey}; " +
               $"speech={rescue.LastRescueSpeechStatus}; " +
               $"autoSupply={rescue.LastAutoSupplyStatus}; " +
               $"{FormatPlayerActionStatus(rescue)}; {FormatProgress(rescue)}";
    }

    private string FormatHeldItems(EntityUid uid)
    {
        if (!TryComp<HandsComponent>(uid, out var hands))
            return "none";

        var held = hands.Hands.Values
            .Where(hand => hand.HeldEntity is { Valid: true })
            .Select(hand => FormatEntityRef(hand.HeldEntity))
            .ToArray();
        return held.Length == 0 ? "none" : string.Join(',', held);
    }

    private string FormatRadioEntityName(EntityUid? entity, string fallback)
    {
        if (entity is not { Valid: true } uid || Deleted(uid))
            return fallback;

        var name = Name(uid).ReplaceLineEndings(" ").Trim();
        return string.IsNullOrWhiteSpace(name)
            ? fallback
            : TrimRadioStatus(name, 48);
    }

    private static string SelectRadioStatus(params string[] statuses)
    {
        foreach (var status in statuses)
        {
            if (string.IsNullOrWhiteSpace(status) ||
                string.Equals(status, "none", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return $"последний статус: {TrimRadioStatus(status, 96)}";
        }

        return "ожидаю медсигнал или приказ";
    }

    private static string TrimRadioStatus(string value, int limit)
    {
        var status = value.ReplaceLineEndings(" ").Trim();
        if (status.Length <= limit)
            return status;

        return $"{status[..limit].TrimEnd()}...";
    }

    private void OnTargetDefibrillated(EntityUid target, MobStateComponent mobState, ref TargetDefibrillatedEvent args)
    {
        if (!TryComp<LuaMRescueAgentComponent>(args.User, out var rescue) ||
            rescue.PendingMedicalDoAfterTarget != target ||
            !string.Equals(rescue.PendingMedicalDoAfterKind, "defibrillator", StringComparison.Ordinal) ||
            rescue.PendingMedicalIntentGeneration != rescue.ActivityContext.Generation ||
            rescue.ActivityContext.Target != target ||
            rescue.ActivityContext.TerminalStatus != LuaMRescueTerminalStatus.Active)
        {
            return;
        }

        rescue.PendingMedicalOutcomeRecorded = true;
        rescue.PendingMedicalOutcomeSucceeded = mobState.CurrentState != MobState.Dead;

        var targetName = Name(target);
        if (mobState.CurrentState != MobState.Dead)
        {
            rescue.DefibrillationAttempts.Remove(target);
            rescue.CompletedDefibrillationFailures.Remove(target);
            rescue.DefibrillationStartedAt.Remove(target);
            rescue.TerminalDefibrillationFailures.Remove(target);
            TrySendRescueStatusComms(
                args.User,
                rescue,
                $"defib-success:{target}",
                $"Пульс {targetName} восстановлен. Продолжаю стабилизацию.");
            return;
        }

        TrySendRescueStatusComms(
            args.User,
            rescue,
            $"defib-failed:{target}",
            $"Дефибрилляция {targetName} не дала возврата. Держу пациента на борту.");
    }

    private void TrySendRescueStatusComms(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        string key,
        string message,
        bool radio = true)
    {
        if (Deleted(uid) ||
            string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var now = _timing.CurTime;
        if (string.Equals(rescue.LastAutoCommsKey, key, StringComparison.Ordinal) &&
            rescue.NextAutoCommsAt > now)
        {
            return;
        }

        rescue.LastAutoCommsKey = key;
        rescue.NextAutoCommsAt = now + TimeSpan.FromSeconds(Math.Max(0.1f, rescue.AutoCommsCooldown));

        if (!TryReserveRescueSpeech(uid, rescue, key, "auto-comms", now))
        {
            Dirty(uid, rescue);
            return;
        }

        _chat.TrySendInGameICMessage(uid, message, InGameICChatType.Speak, hideChat: false, hideLog: true);

        if (radio)
            _radio.SendRadioMessage(uid, message, MedicalRadioChannel, uid);

        Dirty(uid, rescue);
    }

    private bool TryReserveRescueSpeech(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        string key,
        string channel,
        TimeSpan now)
    {
        if (now < rescue.NextRescueSpeechAt)
        {
            var wait = Math.Max(0, (int) Math.Ceiling((rescue.NextRescueSpeechAt - now).TotalSeconds));
            rescue.LastRescueSpeechStatus = $"speech-throttle waiting {wait}s; channel={channel}; key={key}";
            return false;
        }

        rescue.LastRescueSpeechKey = key;
        rescue.NextRescueSpeechAt = now + TimeSpan.FromSeconds(Math.Max(0.1f, rescue.RescueSpeechCooldown));
        rescue.LastRescueSpeechStatus = $"speech:{channel}:{key}; cooldown={rescue.RescueSpeechCooldown:0}s";
        Dirty(uid, rescue);
        return true;
    }

    private void TryReportDeathSignalDispatch(EntityUid uid, LuaMRescueAgentComponent rescue)
    {
        if (rescue.DeathSignalDispatchReported ||
            rescue.DeathSignalTarget is not { Valid: true } target ||
            Deleted(target) ||
            !TryComp<MobStateComponent>(target, out var mobState) ||
            mobState.CurrentState != MobState.Dead)
        {
            return;
        }

        TrySendRescueStatusComms(
            uid,
            rescue,
            $"death-signal-dispatch:{target}",
            $"\u041c\u0435\u0434\u0441\u0438\u0433\u043d\u0430\u043b \u0441\u043c\u0435\u0440\u0442\u0438 \u043f\u0440\u0438\u043d\u044f\u0442. \u0412\u044b\u043b\u0435\u0442\u0430\u044e \u043a \u043f\u0430\u0446\u0438\u0435\u043d\u0442\u0443 {Name(target)}.");
        rescue.DeathSignalDispatchReported = true;
        Dirty(uid, rescue);
    }

    private void ClearDeathSignalTarget(LuaMRescueAgentComponent rescue, EntityUid target)
    {
        if (rescue.DeathSignalTarget != target)
            return;

        rescue.DeathSignalTarget = null;
        rescue.DeathSignalDispatchReported = false;
    }

    private void TryReportPatientArrival(EntityUid uid, LuaMRescueAgentComponent rescue, EntityUid target)
    {
        if (rescue.ArrivalReportedTarget == target ||
            Deleted(target) ||
            IsOnAssignedShuttle(target, rescue) ||
            !TryComp<MobStateComponent>(target, out var mobState) ||
            !IsWithinRange(uid, target, Math.Max(rescue.PlayerActionRange, rescue.EvacuationStartRange)))
        {
            return;
        }

        var state = mobState.CurrentState switch
        {
            MobState.Dead => "\u0431\u0435\u0437 \u043f\u0443\u043b\u044c\u0441\u0430",
            MobState.Critical => "\u043a\u0440\u0438\u0442\u0438\u0447\u0435\u0441\u043a\u043e\u0435",
            _ => "\u0436\u0438\u0432",
        };

        TrySendRescueStatusComms(
            uid,
            rescue,
            $"patient-arrival:{target}",
            $"\u041f\u0430\u0446\u0438\u0435\u043d\u0442 {Name(target)} \u043d\u0430\u0439\u0434\u0435\u043d. \u0421\u043e\u0441\u0442\u043e\u044f\u043d\u0438\u0435: {state}. \u041d\u0430\u0447\u0438\u043d\u0430\u044e \u0441\u0442\u0430\u0431\u0438\u043b\u0438\u0437\u0430\u0446\u0438\u044e.");
        rescue.ArrivalReportedTarget = target;
        rescue.LastArrivalReportStatus = $"reported arrival at {FormatEntityRef(target)} state={mobState.CurrentState}";
        Dirty(uid, rescue);
    }

    private void ClearArrivalReportTarget(LuaMRescueAgentComponent rescue, EntityUid target)
    {
        if (rescue.ArrivalReportedTarget == target)
            rescue.ArrivalReportedTarget = null;
    }

    private void TryReportTriageDecision(EntityUid uid, LuaMRescueAgentComponent rescue, EntityUid target)
    {
        if (Deleted(target) ||
            IsOnAssignedShuttle(target, rescue) ||
            !TryComp<MobStateComponent>(target, out var mobState) ||
            !IsWithinRange(uid, target, Math.Max(rescue.PlayerActionRange, rescue.EvacuationStartRange)))
        {
            return;
        }

        var damage = TryComp<DamageableComponent>(target, out var damageable)
            ? damageable.TotalDamage.Float()
            : 0f;
        var unsafeSceneEvacuation = ShouldEvacuateBeforeTreatment(uid, target, rescue);
        var needsEvacuation = NeedsEvacuation(uid, target, rescue);
        var decisionKey = GetTriageDecisionKey(mobState, damage, unsafeSceneEvacuation, needsEvacuation, rescue);

        if (rescue.TriageReportedTarget == target &&
            string.Equals(rescue.LastTriageDecisionKey, decisionKey, StringComparison.Ordinal))
        {
            return;
        }

        var message = BuildTriageDecisionMessage(target, mobState, damage, decisionKey);
        TrySendRescueStatusComms(
            uid,
            rescue,
            $"triage-decision:{target}:{decisionKey}",
            message);

        rescue.TriageReportedTarget = target;
        rescue.LastTriageDecisionKey = decisionKey;
        rescue.LastTriageDecisionStatus = $"decision={decisionKey}; target={FormatEntityRef(target)}; damage={damage:0.0}";
        Dirty(uid, rescue);
    }

    private static string GetTriageDecisionKey(
        MobStateComponent mobState,
        float damage,
        bool unsafeSceneEvacuation,
        bool needsEvacuation,
        LuaMRescueAgentComponent rescue)
    {
        if (mobState.CurrentState == MobState.Dead)
            return "dead-recovery";

        if (unsafeSceneEvacuation)
            return "unsafe-evacuation";

        if (needsEvacuation && mobState.CurrentState == MobState.Critical)
            return "critical-evacuation";

        if (needsEvacuation)
            return "heavy-evacuation";

        if (damage >= rescue.AutoTreatMinDamage)
            return "onsite-treatment";

        return "monitoring";
    }

    private string BuildTriageDecisionMessage(
        EntityUid target,
        MobStateComponent mobState,
        float damage,
        string decisionKey)
    {
        var name = Name(target);
        return decisionKey switch
        {
            "dead-recovery" => $"\u0422\u0440\u0438\u0430\u0436 {name}: \u043f\u0443\u043b\u044c\u0441\u0430 \u043d\u0435\u0442. \u0417\u0430\u0431\u0438\u0440\u0430\u044e \u043d\u0430 \u0431\u043e\u0440\u0442 \u0434\u043b\u044f \u0440\u0435\u0430\u043d\u0438\u043c\u0430\u0446\u0438\u043e\u043d\u043d\u043e\u0433\u043e \u0446\u0438\u043a\u043b\u0430.",
            "unsafe-evacuation" => $"\u0422\u0440\u0438\u0430\u0436 {name}: \u0437\u043e\u043d\u0430 \u043d\u0435\u0431\u0435\u0437\u043e\u043f\u0430\u0441\u043d\u0430. \u0421\u0442\u0430\u0431\u0438\u043b\u0438\u0437\u0430\u0446\u0438\u044f \u043f\u043e \u043f\u0443\u0442\u0438, \u044d\u0432\u0430\u043a\u0443\u0430\u0446\u0438\u044f \u043d\u0430 \u0448\u0430\u0442\u0442\u043b.",
            "critical-evacuation" => $"\u0422\u0440\u0438\u0430\u0436 {name}: \u0441\u043e\u0441\u0442\u043e\u044f\u043d\u0438\u0435 \u043a\u0440\u0438\u0442\u0438\u0447\u0435\u0441\u043a\u043e\u0435. \u0413\u043e\u0442\u043e\u0432\u043b\u044e \u044d\u0432\u0430\u043a\u0443\u0430\u0446\u0438\u044e \u043d\u0430 \u0448\u0430\u0442\u0442\u043b.",
            "heavy-evacuation" => $"\u0422\u0440\u0438\u0430\u0436 {name}: \u0442\u044f\u0436\u0435\u043b\u044b\u0435 \u043f\u043e\u0432\u0440\u0435\u0436\u0434\u0435\u043d\u0438\u044f {damage:0.0}. \u0412\u0435\u0437\u0443 \u043d\u0430 \u0448\u0430\u0442\u0442\u043b.",
            "onsite-treatment" => $"\u0422\u0440\u0438\u0430\u0436 {name}: \u0441\u043e\u0441\u0442\u043e\u044f\u043d\u0438\u0435 {FormatMobStateForTriage(mobState)}, \u0443\u0440\u043e\u043d {damage:0.0}. \u041b\u0435\u0447\u0443 \u043d\u0430 \u043c\u0435\u0441\u0442\u0435.",
            _ => $"\u0422\u0440\u0438\u0430\u0436 {name}: \u0441\u0440\u043e\u0447\u043d\u043e\u0433\u043e \u0432\u044b\u0432\u043e\u0437\u0430 \u043d\u0435 \u0442\u0440\u0435\u0431\u0443\u0435\u0442\u0441\u044f. \u041d\u0430\u0431\u043b\u044e\u0434\u0430\u044e.",
        };
    }

    private static string FormatMobStateForTriage(MobStateComponent mobState)
    {
        return mobState.CurrentState switch
        {
            MobState.Critical => "\u043a\u0440\u0438\u0442\u0438\u0447\u0435\u0441\u043a\u043e\u0435",
            MobState.Dead => "\u0431\u0435\u0437 \u043f\u0443\u043b\u044c\u0441\u0430",
            _ => "\u0436\u0438\u0432",
        };
    }

    private void ClearTriageDecisionTarget(LuaMRescueAgentComponent rescue, EntityUid target)
    {
        if (rescue.TriageReportedTarget != target)
            return;

        rescue.TriageReportedTarget = null;
        rescue.LastTriageDecisionKey = "none";
    }

    private string GetRescuePhase(EntityUid uid, LuaMRescueAgentComponent rescue)
    {
        if (HasComp<ActorComponent>(uid))
            return "manual-control";

        if (rescue.PendingPlayerAction != LuaMRescuePlayerActionKind.None)
            return $"player-action-{FormatPlayerAction(rescue.PendingPlayerAction)}";

        if (!rescue.AutoAcquireTargets)
            return "manual";

        if (rescue.EvacuatingTarget is { Valid: true } target &&
            !Deleted(target))
        {
            if (IsBuckledToAssignedShuttlePatientStrap(target, rescue) ||
                IsOnAssignedShuttle(target, rescue) &&
                rescue.AssignedPatientStrap is not { Valid: true })
            {
                return "delivered";
            }

            if (IsPullingTarget(uid, target))
            {
                return rescue.AssignedPatientStrap is { Valid: true } patientStrap && !Deleted(patientStrap)
                    ? "deliver-to-bed"
                    : "return-to-shuttle";
            }

            return "approach-patient";
        }

        if (rescue.AssignedTarget is { Valid: true } assigned &&
            !Deleted(assigned))
        {
            return "follow-target";
        }

        if (rescue.AssignedShuttle is { Valid: true } shuttle &&
            !Deleted(shuttle))
        {
            return IsOnAssignedShuttle(uid, rescue)
                ? "standby-on-shuttle"
                : "standby-return-to-shuttle";
        }

        if (rescue.ShuttleReturnRouted)
            return "shuttle-return-home";

        if (rescue.ShuttleRoutedTarget is { Valid: true } routedTarget &&
            !Deleted(routedTarget))
        {
            return "shuttle-routing-to-target";
        }

        return "idle";
    }

    private string FormatProgress(LuaMRescueAgentComponent rescue)
    {
        var distance = float.IsPositiveInfinity(rescue.LastProgressDistance)
            ? "n/a"
            : $"{rescue.LastProgressDistance:0.0}";

        return $"progressGoal={FormatEntityRef(rescue.ProgressGoal)}; " +
               $"distance={distance}; stall={rescue.TargetStallAccumulator:0.0}/{rescue.TargetStallSeconds:0.0}s";
    }

    private string FormatEntityRef(EntityUid? uid)
    {
        if (uid is not { Valid: true } entity)
            return "none";

        if (Deleted(entity))
            return $"{entity}:deleted";

        return $"{GetNetEntity(entity)}:{Name(entity)}";
    }

    private string FormatPlayerActionStatus(LuaMRescueAgentComponent rescue)
    {
        if (rescue.PendingPlayerAction == LuaMRescuePlayerActionKind.None)
            return $"action=none; lastAction={rescue.LastPlayerActionStatus}";

        return $"action={FormatPlayerAction(rescue.PendingPlayerAction)}; " +
               $"actionTarget={FormatEntityRef(rescue.PendingPlayerActionTarget)}; " +
               $"actionSlot={rescue.PendingPlayerActionSlot ?? "none"}; " +
               $"actionItem={rescue.PendingPlayerActionItem ?? "none"}; " +
               $"actionTime={rescue.PlayerActionAccumulator:0.0}/{rescue.PlayerActionTimeout:0.0}s; " +
               $"lastAction={rescue.LastPlayerActionStatus}";
    }

    private static string FormatRescueTaskStage(LuaMRescueTaskStage stage)
    {
        return stage switch
        {
            LuaMRescueTaskStage.Standby => "standby",
            LuaMRescueTaskStage.FollowingPatient => "following-patient",
            LuaMRescueTaskStage.TreatingPatient => "treating-patient",
            LuaMRescueTaskStage.PickingUpSupply => "picking-up-supply",
            LuaMRescueTaskStage.VendingSupply => "vending-supply",
            LuaMRescueTaskStage.EvacuatingPatient => "evacuating-patient",
            LuaMRescueTaskStage.DeliveringPatient => "delivering-patient",
            LuaMRescueTaskStage.ManualAction => "manual-action",
            _ => "none",
        };
    }

    private void SetRescueTask(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        LuaMRescueTaskStage stage,
        EntityUid? patientTarget,
        EntityUid? supplyTarget,
        string status)
    {
        patientTarget = patientTarget is { Valid: true } patient ? patient : null;
        supplyTarget = supplyTarget is { Valid: true } supply ? supply : null;
        status = string.IsNullOrWhiteSpace(status) ? FormatRescueTaskStage(stage) : status;

        if (rescue.TaskStage == stage &&
            rescue.TaskPatientTarget == patientTarget &&
            rescue.TaskSupplyTarget == supplyTarget &&
            string.Equals(rescue.LastTaskStatus, status, StringComparison.Ordinal))
        {
            return;
        }

        rescue.TaskStage = stage;
        rescue.TaskPatientTarget = patientTarget;
        rescue.TaskSupplyTarget = supplyTarget;
        rescue.LastTaskStatus = status;
        Dirty(uid, rescue);
    }

    private void ClearRescueTask(EntityUid uid, LuaMRescueAgentComponent rescue, string status)
    {
        SetRescueTask(uid, rescue, LuaMRescueTaskStage.None, null, null, status);
    }

    private void PruneRescueTaskMemory(EntityUid uid, LuaMRescueAgentComponent rescue)
    {
        if (rescue.TaskPatientTarget is { Valid: true } patient &&
            (Deleted(patient) ||
             IsTargetTemporarilySkipped(uid, patient, rescue) &&
             !rescue.RequiredOnboardHandoffPatients.Contains(patient)))
        {
            ClearRescueTask(uid, rescue, $"forgot unavailable patient {FormatEntityRef(patient)}");
            return;
        }

        if (rescue.TaskSupplyTarget is { Valid: true } supply &&
            (Deleted(supply) || IsSupplyTemporarilySkipped(supply, rescue)))
        {
            SetRescueTask(
                uid,
                rescue,
                rescue.TaskPatientTarget is { Valid: true }
                    ? LuaMRescueTaskStage.FollowingPatient
                    : LuaMRescueTaskStage.None,
                rescue.TaskPatientTarget,
                null,
                $"forgot unavailable supply {FormatEntityRef(supply)}");
        }

        if (rescue.TaskSupplyTarget is { Valid: true } delivery &&
            IsDeliveryTargetTemporarilySkipped(delivery, rescue))
        {
            SetRescueTask(
                uid,
                rescue,
                rescue.TaskPatientTarget is { Valid: true }
                    ? LuaMRescueTaskStage.DeliveringPatient
                    : LuaMRescueTaskStage.None,
                rescue.TaskPatientTarget,
                null,
                $"forgot unavailable delivery target {FormatEntityRef(delivery)}");
        }
    }

    private bool TryGetRememberedPatientTarget(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        MedibotComponent medibot,
        out EntityUid target)
    {
        target = default;

        if (rescue.TaskPatientTarget is not { Valid: true } patient ||
            Deleted(patient) ||
            IsTargetTemporarilySkipped(uid, patient, rescue))
        {
            return false;
        }

        if (NeedsEvacuation(uid, patient, rescue) ||
            IsRescueCandidate(uid, patient, medibot, requireRange: false, rescue.SearchRange, out _))
        {
            target = patient;
            return true;
        }

        RecordRescueHandoff(
            uid,
            rescue,
            patient,
            BuildPatientTreatmentResult(patient, rescue),
            "stabilized on site; no evacuation required",
            "returning to standby");
        ClearArrivalReportTarget(rescue, patient);
        ClearTriageDecisionTarget(rescue, patient);
        ClearDeathSignalTarget(rescue, patient);
        ClearRescueTask(uid, rescue, $"patient {FormatEntityRef(patient)} no longer needs rescue");
        return false;
    }

    private bool TryResumeRememberedPatientTask(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        MedibotComponent medibot)
    {
        if (!TryGetRememberedPatientTarget(uid, rescue, medibot, out var patient))
            return false;

        if (TryTreatOrEvacuateTarget(uid, rescue, htn, patient))
            return true;

        SetRescueTask(
            uid,
            rescue,
            LuaMRescueTaskStage.FollowingPatient,
            patient,
            null,
            $"returning to remembered patient {FormatEntityRef(patient)}");
        SetFollowTarget(uid, rescue, htn, patient);
        return true;
    }

    private void UpdatePendingPlayerAction(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        float frameTime)
    {
        var action = rescue.PendingPlayerAction;
        if (action == LuaMRescuePlayerActionKind.None)
            return;

        rescue.PlayerActionAccumulator += frameTime;

        var target = rescue.PendingPlayerActionTarget;
        if (RequiresPlayerActionTarget(action) &&
            target is not { Valid: true })
        {
            FinishPendingPlayerAction(uid, rescue, htn, $"failed {FormatPlayerAction(action)}: target missing");
            return;
        }

        var canLeaveActionTargetForVendedItem =
            action == LuaMRescuePlayerActionKind.Vend &&
            rescue.PendingVendingStarted &&
            rescue.PendingVendingDispensedItem is { Valid: true } dispensedItem &&
            !Deleted(dispensedItem);

        if (target is { Valid: true } targetUid && !canLeaveActionTargetForVendedItem)
        {
            if (Deleted(targetUid))
            {
                FinishPendingPlayerAction(uid, rescue, htn, $"failed {FormatPlayerAction(action)}: target deleted");
                return;
            }

            if (!IsWithinRange(uid, targetUid, rescue.PlayerActionRange))
            {
                if (rescue.PlayerActionAccumulator >= rescue.PlayerActionTimeout)
                {
                    FinishPendingPlayerAction(uid, rescue, htn, $"failed {FormatPlayerAction(action)}: timed out reaching {FormatEntityRef(targetUid)}");
                    return;
                }

                var automaticResupplyMovement =
                    rescue.TaskPatientTarget is { Valid: true } &&
                    rescue.TaskSupplyTarget == targetUid &&
                    rescue.TaskStage is LuaMRescueTaskStage.PickingUpSupply or LuaMRescueTaskStage.VendingSupply &&
                    action is LuaMRescuePlayerActionKind.Pickup
                        or LuaMRescuePlayerActionKind.TakeTargetStorage
                        or LuaMRescuePlayerActionKind.Vend;
                if (automaticResupplyMovement)
                {
                    // The supply is only the movement destination. The remembered
                    // patient and Resupply activity remain the authoritative intent.
                    SetTaskMovementTarget(uid, rescue, htn, targetUid);
                }
                else if (rescue.AssignedTarget != targetUid ||
                         !HasUsablePatientMovementGoal(uid, htn, targetUid))
                {
                    SetFollowTarget(uid, rescue, htn, targetUid);
                }

                return;
            }
        }

        if (action == LuaMRescuePlayerActionKind.Vend)
        {
            UpdatePendingVendingAction(uid, rescue, htn, target!.Value);
            return;
        }

        if (action == LuaMRescuePlayerActionKind.Treat &&
            target is { Valid: true } treatmentTarget &&
            rescue.PendingMedicalDoAfterTarget == treatmentTarget &&
            rescue.PendingMedicalEffectVerification)
        {
            rescue.LastPlayerActionStatus =
                $"waiting for observed medical effect on {FormatEntityRef(treatmentTarget)}";
            rescue.TargetStallAccumulator = 0f;
            Dirty(uid, rescue);
            return;
        }

        if (action == LuaMRescuePlayerActionKind.Treat &&
            target is { Valid: true } runningTreatmentTarget &&
            rescue.PendingMedicalDoAfterTarget == runningTreatmentTarget &&
            HasActiveMedicalDoAfter(uid, runningTreatmentTarget, out var runningKind))
        {
            rescue.LastPlayerActionStatus =
                $"waiting for {runningKind} DoAfter on {FormatEntityRef(runningTreatmentTarget)}";
            rescue.TargetStallAccumulator = 0f;
            Dirty(uid, rescue);
            return;
        }

        var succeeded = TryExecutePlayerAction(
            uid,
            rescue,
            action,
            target,
            rescue.PendingPlayerActionSlot,
            rescue.PendingPlayerActionItem,
            out var status);
        if (succeeded &&
            action == LuaMRescuePlayerActionKind.Treat &&
            target is { Valid: true } startedTreatmentTarget &&
            rescue.PendingMedicalDoAfterTarget == startedTreatmentTarget)
        {
            // InteractUsing only started the authoritative medical action. Keep the
            // manual order pending until DoAfterEndedEvent confirms its outcome.
            rescue.LastPlayerActionStatus = status;
            rescue.TargetStallAccumulator = 0f;
            Dirty(uid, rescue);
            return;
        }

        FinishPendingPlayerAction(
            uid,
            rescue,
            htn,
            succeeded
                ? status
                : $"failed {FormatPlayerAction(action)}: {status}");
    }

    private bool TryExecutePlayerAction(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        LuaMRescuePlayerActionKind action,
        EntityUid? target,
        string? slot,
        string? itemSelector,
        out string status)
    {
        switch (action)
        {
            case LuaMRescuePlayerActionKind.Interact:
            {
                if (target is not { Valid: true } targetUid)
                {
                    status = "target missing";
                    return false;
                }

                var handled = _interaction.InteractHand(uid, targetUid);
                status = handled
                    ? $"interacted with {FormatEntityRef(targetUid)}"
                    : $"interact with {FormatEntityRef(targetUid)} was not handled";
                return handled;
            }
            case LuaMRescuePlayerActionKind.AltInteract:
            {
                if (target is not { Valid: true } targetUid)
                {
                    status = "target missing";
                    return false;
                }

                var handled = _interaction.AltInteract(uid, targetUid);
                status = handled
                    ? $"alt-interacted with {FormatEntityRef(targetUid)}"
                    : $"alt-interact with {FormatEntityRef(targetUid)} was not handled";
                return handled;
            }
            case LuaMRescuePlayerActionKind.Use:
            {
                if (!TryComp<HandsComponent>(uid, out var hands) ||
                    hands.ActiveHandEntity is not { Valid: true } held)
                {
                    status = "active hand is empty";
                    return false;
                }

                if (target is { Valid: true } targetUid)
                {
                    var handled = held == targetUid
                        ? _interaction.UseInHandInteraction(uid, held)
                        : _interaction.InteractUsing(uid, held, targetUid, Transform(targetUid).Coordinates);
                    status = handled
                        ? $"used {FormatEntityRef(held)} on {FormatEntityRef(targetUid)}"
                        : $"use of {FormatEntityRef(held)} on {FormatEntityRef(targetUid)} was not handled";
                    return handled;
                }

                var usedInHand = _interaction.UseInHandInteraction(uid, held);
                status = usedInHand
                    ? $"used {FormatEntityRef(held)} in hand"
                    : $"use of {FormatEntityRef(held)} in hand was not handled";
                return usedInHand;
            }
            case LuaMRescuePlayerActionKind.Treat:
            {
                if (target is not { Valid: true } targetUid)
                {
                    status = "target missing";
                    return false;
                }

                if (!TryFindTreatmentItem(uid, targetUid, slot, itemSelector, includeDiagnosticItems: true, out var item, out status))
                    return false;

                var startingDamage = TryComp<DamageableComponent>(targetUid, out var damageable)
                    ? damageable.TotalDamage.Float()
                    : 0f;
                var startingDamageByType = GetDamageTypeSnapshot(targetUid);
                var startingVolume = GetMedicalItemSolutionVolume(item);
                var startingMobState = TryComp<MobStateComponent>(targetUid, out var mobState)
                    ? mobState.CurrentState
                    : MobState.Invalid;
                var startingBleedAmount = TryComp<BloodstreamComponent>(targetUid, out var bloodstream)
                    ? bloodstream.BleedAmount
                    : 0f;
                var startingBloodVolume = GetPatientBloodVolume(targetUid);
                var expectedEffect = BuildMedicalEffectExpectation(item, targetUid);
                var expectedPositiveEffect = expectedEffect.HasObservableEffect &&
                                             IsEffectiveTreatmentItem(item, targetUid, out _);
                var handled = _interaction.InteractUsing(uid, item, targetUid, Transform(targetUid).Coordinates);
                if (HasActiveMedicalDoAfter(uid, targetUid, out var kind))
                {
                    TrackMedicalDoAfter(uid, rescue, targetUid, item, kind);
                    status =
                        $"{kind} DoAfter started for {FormatEntityRef(targetUid)} using {FormatEntityRef(item)}";
                    return true;
                }

                var analyzed = TryComp<HealthAnalyzerComponent>(item, out var analyzer) &&
                               analyzer.ScannedEntity == targetUid;
                var solutionConsumed = !Deleted(item) &&
                                       startingVolume is { } before &&
                                       GetMedicalItemSolutionVolume(item) is { } after &&
                                       after + 0.001f < before;
                var observedImprovement = HasObservedMedicalImprovement(
                    targetUid,
                    startingDamageByType,
                    startingBleedAmount,
                    startingBloodVolume,
                    expectedEffect.DamageTypes,
                    expectedEffect.ReducesBleeding,
                    expectedEffect.IncreasesBloodVolume,
                    startingMobState,
                    expectedEffect.ImprovesMobState);
                var completed = handled && (analyzed || observedImprovement);
                if (!completed &&
                    handled &&
                    solutionConsumed &&
                    expectedPositiveEffect &&
                    IsInjectionMedicalAction("injection", item))
                {
                    TrackInstantMedicalEffectVerification(
                        uid,
                        rescue,
                        targetUid,
                        item,
                        TryComp<InjectorComponent>(item, out _) ? "injector" : "hypospray",
                        startingDamage,
                        startingDamageByType,
                        startingVolume,
                        startingBleedAmount,
                        startingMobState,
                        startingBloodVolume,
                        expectedEffect);
                    status =
                        $"injection transferred for {FormatEntityRef(targetUid)}; awaiting observed medical effect";
                    return true;
                }

                status = completed
                    ? $"treatment completed for {FormatEntityRef(targetUid)} using {FormatEntityRef(item)}"
                    : handled
                        ? $"treatment of {FormatEntityRef(targetUid)} produced no confirmed medical effect"
                        : $"treatment of {FormatEntityRef(targetUid)} using {FormatEntityRef(item)} was not handled";
                return completed;
            }
            case LuaMRescuePlayerActionKind.Pickup:
            {
                if (target is not { Valid: true } targetUid)
                {
                    status = "target missing";
                    return false;
                }

                var pickedUp = _hands.TryPickupAnyHand(uid, targetUid);
                status = pickedUp
                    ? $"picked up {FormatEntityRef(targetUid)}"
                    : $"could not pick up {FormatEntityRef(targetUid)}";
                return pickedUp;
            }
            case LuaMRescuePlayerActionKind.Drop:
            {
                EntityCoordinates? dropLocation = null;
                if (target is { Valid: true } targetUid)
                    dropLocation = Transform(targetUid).Coordinates;

                var dropped = _hands.TryDrop(uid, dropLocation);
                status = dropped
                    ? target is { Valid: true }
                        ? $"dropped active hand near {FormatEntityRef(target)}"
                        : "dropped active hand"
                    : "could not drop active hand";
                return dropped;
            }
            case LuaMRescuePlayerActionKind.Pull:
            {
                if (target is not { Valid: true } targetUid)
                {
                    status = "target missing";
                    return false;
                }

                if (!TryComp<PullableComponent>(targetUid, out _))
                {
                    status = $"{FormatEntityRef(targetUid)} is not pullable";
                    return false;
                }

                if (_container.IsEntityOrParentInContainer(targetUid) ||
                    !_container.IsInSameOrNoContainer((uid, null, null), (targetUid, null, null)))
                {
                    status = $"ContainedTarget: {FormatEntityRef(targetUid)} cannot be passed to PullingSystem";
                    return false;
                }

                if (!IsPullingTarget(uid, targetUid))
                    _pulling.TryStartPull(uid, targetUid);

                var pulling = IsPullingTarget(uid, targetUid);
                status = pulling
                    ? $"pulling {FormatEntityRef(targetUid)}"
                    : $"could not start pulling {FormatEntityRef(targetUid)}";
                return pulling;
            }
            case LuaMRescuePlayerActionKind.StopPull:
            {
                var targetWasExplicit = target is { Valid: true };
                EntityUid? pulled = targetWasExplicit
                    ? target
                    : TryComp<PullerComponent>(uid, out var puller)
                        ? puller.Pulling
                        : null;

                if (pulled is not { Valid: true } pulledUid)
                {
                    status = "not pulling anything";
                    return true;
                }

                if (targetWasExplicit && !IsPullingTarget(uid, pulledUid))
                {
                    status = $"not pulling {FormatEntityRef(pulledUid)}";
                    return false;
                }

                if (!TryComp<PullableComponent>(pulledUid, out var pullable))
                {
                    status = $"{FormatEntityRef(pulledUid)} is not pullable";
                    return false;
                }

                var stopped = _pulling.TryStopPull(pulledUid, pullable, uid);
                status = stopped
                    ? $"stopped pulling {FormatEntityRef(pulledUid)}"
                    : $"could not stop pulling {FormatEntityRef(pulledUid)}";
                return stopped;
            }
            case LuaMRescuePlayerActionKind.Buckle:
            {
                if (target is not { Valid: true } strapUid)
                {
                    status = "target missing";
                    return false;
                }

                if (!TryComp<StrapComponent>(strapUid, out var strap))
                {
                    status = $"{FormatEntityRef(strapUid)} is not a strap";
                    return false;
                }

                if (!strap.Enabled)
                {
                    status = $"{FormatEntityRef(strapUid)} strap is disabled";
                    return false;
                }

                var buckledEntity = TryComp<PullerComponent>(uid, out var puller) &&
                                    puller.Pulling is { Valid: true } pulled &&
                                    !Deleted(pulled)
                    ? pulled
                    : uid;

                if (!TryComp<BuckleComponent>(buckledEntity, out var buckle))
                {
                    status = $"{FormatEntityRef(buckledEntity)} cannot be buckled";
                    return false;
                }

                var buckled = _buckle.TryBuckle(buckledEntity, uid, strapUid, buckle, popup: false);
                status = buckled
                    ? $"buckled {FormatEntityRef(buckledEntity)} to {FormatEntityRef(strapUid)}"
                    : $"could not buckle {FormatEntityRef(buckledEntity)} to {FormatEntityRef(strapUid)}";
                return buckled;
            }
            case LuaMRescuePlayerActionKind.Unbuckle:
            {
                if (target is not { Valid: true } targetUid)
                {
                    status = "target missing";
                    return false;
                }

                if (!TryResolveUnbuckleTarget(targetUid, out var buckledEntity, out var buckle, out status))
                    return false;

                var oldStrap = buckle.BuckledTo;
                var unbuckled = _buckle.TryUnbuckle(buckledEntity, uid, buckle, popup: false);
                status = unbuckled
                    ? $"unbuckled {FormatEntityRef(buckledEntity)} from {FormatEntityRef(oldStrap)}"
                    : $"could not unbuckle {FormatEntityRef(buckledEntity)} from {FormatEntityRef(oldStrap)}";
                return unbuckled;
            }
            case LuaMRescuePlayerActionKind.EquipSlot:
            {
                if (string.IsNullOrWhiteSpace(slot))
                {
                    status = "slot missing";
                    return false;
                }

                if (!TryComp<InventoryComponent>(uid, out var inventory))
                {
                    status = "agent has no inventory";
                    return false;
                }

                if (!TryComp<HandsComponent>(uid, out var hands) ||
                    hands.ActiveHandEntity is not { Valid: true } held)
                {
                    status = "active hand is empty";
                    return false;
                }

                if (_inventory.TryGetSlotEntity(uid, slot, out var existing, inventory))
                {
                    status = $"slot {slot} already holds {FormatEntityRef(existing)}";
                    return false;
                }

                var equipped = _inventory.TryEquip(uid, held, slot, silent: true, inventory: inventory);
                status = equipped
                    ? $"equipped {FormatEntityRef(held)} to slot {slot}"
                    : $"could not equip {FormatEntityRef(held)} to slot {slot}";
                return equipped;
            }
            case LuaMRescuePlayerActionKind.UnequipSlot:
            {
                if (string.IsNullOrWhiteSpace(slot))
                {
                    status = "slot missing";
                    return false;
                }

                if (!TryComp<InventoryComponent>(uid, out var inventory))
                {
                    status = "agent has no inventory";
                    return false;
                }

                if (!_inventory.TryGetSlotEntity(uid, slot, out var slotEntity, inventory))
                {
                    status = $"slot {slot} is empty or unavailable";
                    return false;
                }

                if (!TryComp<HandsComponent>(uid, out var hands) ||
                    !_hands.TryGetEmptyHand(uid, out _, hands))
                {
                    status = "no empty hand for unequipped item";
                    return false;
                }

                if (!_inventory.TryUnequip(uid, slot, out var removed, silent: true, inventory: inventory) ||
                    removed is not { Valid: true } removedUid)
                {
                    status = $"could not unequip {FormatEntityRef(slotEntity)} from slot {slot}";
                    return false;
                }

                var pickedUp = _hands.TryPickupAnyHand(uid, removedUid, handsComp: hands);
                status = pickedUp
                    ? $"unequipped {FormatEntityRef(removedUid)} from slot {slot}"
                    : $"unequipped {FormatEntityRef(removedUid)} from slot {slot} but could not take it into hand";
                return pickedUp;
            }
            case LuaMRescuePlayerActionKind.StoreSlot:
            {
                if (string.IsNullOrWhiteSpace(slot))
                {
                    status = "slot missing";
                    return false;
                }

                if (!TryResolveStorageSlot(uid, slot, out var storageUid, out var storage, out status))
                    return false;

                if (!TryComp<HandsComponent>(uid, out var hands) ||
                    hands.ActiveHand is not { } activeHand ||
                    activeHand.HeldEntity is not { Valid: true })
                {
                    status = "active hand is empty";
                    return false;
                }

                return TryStoreHeldItemInStorage(uid, hands, activeHand, storageUid, storage, slot, out status);
            }
            case LuaMRescuePlayerActionKind.TakeStorage:
            {
                if (string.IsNullOrWhiteSpace(slot))
                {
                    status = "slot missing";
                    return false;
                }

                if (!TryResolveStorageSlot(uid, slot, out var storageUid, out var storage, out status))
                    return false;

                if (!TryComp<HandsComponent>(uid, out var hands) ||
                    !_hands.TryGetEmptyHand(uid, out _, hands))
                {
                    status = "no empty hand for stored item";
                    return false;
                }

                if (!TrySelectStoredItem(storageUid, storage, itemSelector, out var storedItem, out status))
                    return false;

                if (!_container.RemoveEntity(storageUid, storedItem))
                {
                    status = $"could not remove {FormatEntityRef(storedItem)} from {FormatEntityRef(storageUid)}";
                    return false;
                }

                var pickedUp = _hands.TryPickupAnyHand(uid, storedItem, handsComp: hands);
                status = pickedUp
                    ? $"took {FormatEntityRef(storedItem)} from {FormatEntityRef(storageUid)}"
                    : $"removed {FormatEntityRef(storedItem)} from {FormatEntityRef(storageUid)} but could not take it into hand";
                return pickedUp;
            }
            case LuaMRescuePlayerActionKind.TakeTargetStorage:
            {
                if (target is not { Valid: true } storageUid)
                {
                    status = "target missing";
                    return false;
                }

                if (!TryComp<StorageComponent>(storageUid, out var storage))
                {
                    status = $"{FormatEntityRef(storageUid)} is not storage";
                    return false;
                }

                if (!TryTakeItemFromTargetStorage(uid, storageUid, storage, itemSelector, out var storedItem, out status))
                    return false;

                rescue.PendingStorageTakenItem = storedItem;
                status = $"took {FormatEntityRef(storedItem)} from {FormatEntityRef(storageUid)}";
                return true;
            }
            default:
                status = "unsupported player action";
                return false;
        }
    }

    private bool TryResolveStorageSlot(
        EntityUid uid,
        string slot,
        out EntityUid storageUid,
        out StorageComponent storage,
        out string status)
    {
        storageUid = default;
        storage = default!;
        status = string.Empty;

        if (!TryComp<InventoryComponent>(uid, out var inventory))
        {
            status = "agent has no inventory";
            return false;
        }

        if (!_inventory.TryGetSlotEntity(uid, slot, out var slotEntity, inventory) ||
            slotEntity is not { Valid: true } slotItem)
        {
            status = $"slot {slot} is empty or unavailable";
            return false;
        }

        if (!TryComp<StorageComponent>(slotItem, out var storageComp))
        {
            status = $"{FormatEntityRef(slotItem)} in slot {slot} is not storage";
            return false;
        }

        storage = storageComp;
        storageUid = slotItem;
        return true;
    }

    private bool TryCanUseTargetStorage(
        EntityUid uid,
        EntityUid storageUid,
        StorageComponent storage,
        out string status)
    {
        status = string.Empty;

        if (Deleted(storageUid))
        {
            status = "storage target is deleted";
            return false;
        }

        if (!_actionBlocker.CanInteract(uid, storageUid))
        {
            status = $"cannot interact with {FormatEntityRef(storageUid)}";
            return false;
        }

        var attempt = new StorageInteractAttemptEvent(true);
        RaiseLocalEvent(storageUid, ref attempt);
        if (attempt.Cancelled)
        {
            status = $"{FormatEntityRef(storageUid)} cannot be opened";
            return false;
        }

        if (storage.Container.ContainedEntities.Count == 0)
        {
            status = $"{FormatEntityRef(storageUid)} is empty";
            return false;
        }

        return true;
    }

    private bool TryTakeItemFromTargetStorage(
        EntityUid uid,
        EntityUid storageUid,
        StorageComponent storage,
        string? itemSelector,
        out EntityUid storedItem,
        out string status)
    {
        storedItem = default;

        if (!TryCanUseTargetStorage(uid, storageUid, storage, out status))
            return false;

        if (!TryComp<HandsComponent>(uid, out var hands) ||
            !_hands.TryGetEmptyHand(uid, out _, hands))
        {
            status = "no empty hand for targeted storage item";
            return false;
        }

        if (!TrySelectStoredItem(storageUid, storage, itemSelector, out storedItem, out status))
            return false;

        if (!storage.Container.Contains(storedItem))
        {
            status = $"{FormatEntityRef(storedItem)} is no longer in {FormatEntityRef(storageUid)}";
            return false;
        }

        if (!_actionBlocker.CanInteract(uid, storedItem))
        {
            status = $"cannot interact with stored item {FormatEntityRef(storedItem)}";
            return false;
        }

        if (!_hands.TryPickupAnyHand(uid, storedItem, handsComp: hands))
        {
            status = $"could not take {FormatEntityRef(storedItem)} from {FormatEntityRef(storageUid)}";
            return false;
        }

        status = $"took {FormatEntityRef(storedItem)} from {FormatEntityRef(storageUid)}";
        return true;
    }

    private bool TryResolveUnbuckleTarget(
        EntityUid target,
        out EntityUid buckledEntity,
        out BuckleComponent buckle,
        out string status)
    {
        buckledEntity = default;
        buckle = default!;
        status = string.Empty;

        if (TryComp<BuckleComponent>(target, out var targetBuckle) &&
            targetBuckle.BuckledTo is { Valid: true })
        {
            buckledEntity = target;
            buckle = targetBuckle;
            return true;
        }

        if (TryComp<StrapComponent>(target, out var strap))
        {
            foreach (var strapped in strap.BuckledEntities)
            {
                if (!Deleted(strapped) &&
                    TryComp<BuckleComponent>(strapped, out var strappedBuckle) &&
                    strappedBuckle.BuckledTo == target)
                {
                    buckledEntity = strapped;
                    buckle = strappedBuckle;
                    return true;
                }
            }

            status = $"{FormatEntityRef(target)} has no buckled patient";
            return false;
        }

        status = $"{FormatEntityRef(target)} is not buckled and is not a strap";
        return false;
    }

    private bool TryStoreHeldItemInStorage(
        EntityUid uid,
        HandsComponent hands,
        Hand hand,
        EntityUid storageUid,
        StorageComponent storage,
        string slot,
        out string status)
    {
        if (hand.HeldEntity is not { Valid: true } held)
        {
            status = "hand is empty";
            return false;
        }

        if (!_storage.CanInsert(storageUid, held, out var reason, storage))
        {
            status = reason == null
                ? $"could not store {FormatEntityRef(held)} in {FormatEntityRef(storageUid)}"
                : $"could not store {FormatEntityRef(held)} in {FormatEntityRef(storageUid)}: {reason}";
            return false;
        }

        if (!_hands.TryDrop(uid, hand, handsComp: hands))
        {
            status = $"could not drop {FormatEntityRef(held)} for storage insert";
            return false;
        }

        var inserted = _storage.Insert(storageUid, held, out var stacked, out _, user: uid, storageComp: storage);
        if (stacked != null &&
            !_storage.CanInsert(storageUid, held, out _, storage) &&
            TryComp<StackComponent>(held, out var handStack) &&
            handStack.Count > 0)
        {
            _hands.TryPickup(uid, held, handsComp: hands);
        }

        if (inserted)
        {
            status = $"stored {FormatEntityRef(held)} in {FormatEntityRef(storageUid)} from slot {slot}";
            return true;
        }

        _hands.TryPickup(uid, held, hand, handsComp: hands);
        status = $"could not store {FormatEntityRef(held)} in {FormatEntityRef(storageUid)}";
        return false;
    }

    private bool TryAutoStowHeldItemForTreatment(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        out string status)
    {
        status = string.Empty;

        if (!rescue.AutoStowHeldItemsForTreatment)
            return false;

        if (!TryComp<HandsComponent>(uid, out var hands))
        {
            status = "agent has no hands";
            return false;
        }

        if (_hands.TryGetEmptyHand(uid, out _, hands))
            return false;

        foreach (var hand in EnumerateHandsForAutoStow(hands))
        {
            if (hand.HeldEntity is not { Valid: true })
                continue;

            foreach (var slot in GetTreatmentStorageSlots(uid))
            {
                if (!TryResolveStorageSlot(uid, slot, out var storageUid, out var storage, out _))
                    continue;

                if (!TryStoreHeldItemInStorage(uid, hands, hand, storageUid, storage, slot, out status))
                    continue;

                rescue.NextAutoTreatmentAttempt = _timing.CurTime;
                rescue.LastPlayerActionStatus = status;
                return true;
            }
        }

        status = "no storage slot can hold a hand item";
        return false;
    }

    private bool TryAutoStoreCollectedMedicalSupply(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        EntityUid patient,
        EntityUid supply,
        out string status)
    {
        status = string.Empty;

        if (!rescue.AutoStoreCollectedMedicalSupplies ||
            Deleted(patient) ||
            Deleted(supply) ||
            GetTreatmentItemScore(supply, patient, itemSelector: null, includeDiagnosticItems: true) <= 0f)
        {
            return false;
        }

        if (!TryComp<HandsComponent>(uid, out var hands))
        {
            status = "agent has no hands for collected supply storage";
            return false;
        }

        Hand? supplyHand = null;
        foreach (var hand in hands.Hands.Values)
        {
            if (hand.HeldEntity != supply)
                continue;

            supplyHand = hand;
            break;
        }

        if (supplyHand == null)
        {
            status = $"collected supply {FormatEntityRef(supply)} is not held";
            return false;
        }

        foreach (var slot in GetTreatmentStorageSlots(uid))
        {
            if (!TryResolveStorageSlot(uid, slot, out var storageUid, out var storage, out _))
                continue;

            if (!TryStoreHeldItemInStorage(uid, hands, supplyHand, storageUid, storage, slot, out status))
                continue;

            rescue.LastPlayerActionStatus = status;
            rescue.NextAutoTreatmentAttempt = _timing.CurTime;
            return true;
        }

        status = $"kept collected supply {FormatEntityRef(supply)} in hand; no storage slot can hold it";
        return false;
    }

    private static IEnumerable<Hand> EnumerateHandsForAutoStow(HandsComponent hands)
    {
        if (hands.ActiveHand != null)
            yield return hands.ActiveHand;

        foreach (var hand in hands.Hands.Values)
        {
            if (hand == hands.ActiveHand)
                continue;

            yield return hand;
        }
    }

    private void UpdatePendingVendingAction(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        EntityUid vendingUid)
    {
        if (!TryComp<VendingMachineComponent>(vendingUid, out var vending))
        {
            FinishPendingPlayerAction(uid, rescue, htn, $"failed vend: {FormatEntityRef(vendingUid)} is not a vending machine");
            return;
        }

        if (!rescue.PendingVendingStarted)
        {
            if (!TryStartVendingProduct(
                    uid,
                    vendingUid,
                    vending,
                    rescue,
                    rescue.PendingPlayerActionItem,
                    includeDiagnosticItems: true,
                    out var vendStatus))
            {
                FinishPendingPlayerAction(uid, rescue, htn, $"failed vend: {vendStatus}");
                return;
            }

            rescue.LastPlayerActionStatus = vendStatus;
            Dirty(uid, rescue);
            return;
        }

        var product = rescue.PendingVendingProduct;
        if (rescue.PlayerActionAccumulator >= rescue.PlayerActionTimeout)
        {
            var timeoutStatus = product == null
                ? $"timed out waiting for {FormatEntityRef(vendingUid)} vend"
                : $"timed out finding dispensed {product} near {FormatEntityRef(vendingUid)}";
            FinishPendingPlayerAction(uid, rescue, htn, $"failed vend: {timeoutStatus}");
            return;
        }

        if (vending.Ejecting)
        {
            rescue.LastPlayerActionStatus = product == null
                ? $"waiting for {FormatEntityRef(vendingUid)} to vend"
                : $"waiting for {FormatEntityRef(vendingUid)} to vend {product}";
            Dirty(uid, rescue);
            return;
        }

        var pickupStatus = product == null
            ? $"waiting for dispensed item near {FormatEntityRef(vendingUid)}"
            : $"waiting for dispensed {product} near {FormatEntityRef(vendingUid)}";
        if (product != null && TryRetrieveDispensedVendingProduct(uid, vendingUid, product, rescue, htn, out pickupStatus))
        {
            FinishPendingPlayerAction(uid, rescue, htn, pickupStatus);
            return;
        }

        rescue.LastPlayerActionStatus = pickupStatus;
        Dirty(uid, rescue);
    }

    private bool TryStartVendingProduct(
        EntityUid uid,
        EntityUid vendingUid,
        VendingMachineComponent vending,
        LuaMRescueAgentComponent rescue,
        string? itemSelector,
        bool includeDiagnosticItems,
        out string status)
    {
        if (vending.Broken)
        {
            status = $"{FormatEntityRef(vendingUid)} is broken";
            return false;
        }

        if (vending.Ejecting)
        {
            status = $"{FormatEntityRef(vendingUid)} is already vending";
            return false;
        }

        if (!TrySelectVendingProduct(
                vendingUid,
                vending,
                itemSelector,
                includeDiagnosticItems,
                treatmentTarget: null,
                out var product,
                out status))
            return false;

        _vending.AuthorizedVend(vendingUid, uid, product.Type, product.ID, vending);
        if (!vending.Ejecting || !string.Equals(vending.NextItemToEject, product.ID, StringComparison.Ordinal))
        {
            status = $"could not buy {product.ID} from {FormatEntityRef(vendingUid)}";
            return false;
        }

        rescue.PendingVendingStarted = true;
        rescue.PendingVendingProduct = product.ID;
        rescue.PendingVendingDispensedItem = null;
        rescue.PendingStorageTakenItem = null;
        status = $"buying {product.ID} from {FormatEntityRef(vendingUid)}";
        return true;
    }

    private bool TrySelectVendingProduct(
        EntityUid vendingUid,
        VendingMachineComponent vending,
        string? itemSelector,
        bool includeDiagnosticItems,
        EntityUid? treatmentTarget,
        out VendingMachineInventoryEntry product,
        out string status)
    {
        product = default!;
        status = string.Empty;

        var available = _vending.GetAvailableInventory(vendingUid, vending);
        if (available.Count == 0)
        {
            status = $"{FormatEntityRef(vendingUid)} has no available inventory";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(itemSelector))
        {
            foreach (var entry in available)
            {
                if (!VendingProductMatchesSelector(entry, itemSelector))
                    continue;

                product = entry;
                return true;
            }

            status = $"no vending product matching '{itemSelector}' in {FormatEntityRef(vendingUid)}";
            return false;
        }

        var bestScore = 0;
        foreach (var entry in available)
        {
            var score = treatmentTarget is { Valid: true } patient && !Deleted(patient)
                ? (int) MathF.Ceiling(GetMedicalVendingProductPatientScore(entry.ID, patient, includeDiagnosticItems))
                : GetMedicalVendingProductScore(entry.ID, includeDiagnosticItems);
            if (score <= bestScore)
                continue;

            product = entry;
            bestScore = score;
        }

        if (bestScore > 0)
            return true;

        status = $"no useful medical product in {FormatEntityRef(vendingUid)}";
        return false;
    }

    private bool VendingProductMatchesSelector(VendingMachineInventoryEntry product, string itemSelector)
    {
        var selector = itemSelector.Trim();
        if (selector.Length == 0)
            return false;

        if (product.ID.Equals(selector, StringComparison.OrdinalIgnoreCase))
            return true;

        var lowered = selector.ToLowerInvariant();
        if (product.ID.ToLowerInvariant().Contains(lowered))
            return true;

        return _prototype.TryIndex<EntityPrototype>(product.ID, out var prototype) &&
               prototype.Name.ToLowerInvariant().Contains(lowered);
    }

    private int GetMedicalVendingProductScore(string productId, bool includeDiagnosticItems)
    {
        for (var i = 0; i < MedicalVendingProductPriority.Length; i++)
        {
            if (!productId.Equals(MedicalVendingProductPriority[i], StringComparison.OrdinalIgnoreCase))
                continue;

            if (!includeDiagnosticItems && productId.Equals("HandheldHealthAnalyzer", StringComparison.OrdinalIgnoreCase))
                return 0;

            return MedicalVendingProductPriority.Length - i;
        }

        return 0;
    }

    private float GetMedicalVendingProductPatientScore(
        string productId,
        EntityUid patient,
        bool includeDiagnosticItems)
    {
        var priority = GetMedicalVendingProductScore(productId, includeDiagnosticItems);
        if (priority <= 0 ||
            !TryComp<DamageableComponent>(patient, out var damageable) ||
            !TryComp<MobStateComponent>(patient, out var mobState))
        {
            return 0f;
        }

        var brute = GetDamageAmount(damageable, "Blunt", "Slash", "Piercing");
        var burn = GetDamageAmount(damageable, "Heat", "Cold", "Shock", "Caustic");
        var general = brute + burn + GetDamageAmount(
            damageable,
            "Asphyxiation",
            "Bloodloss",
            "Poison",
            "Radiation",
            "Cellular");
        var bleeding = TryComp<BloodstreamComponent>(patient, out var bloodstream)
            ? Math.Max(0f, bloodstream.BleedAmount)
            : 0f;

        var expectedBenefit = productId switch
        {
            "Brutepack" => brute,
            "Ointment" => burn,
            "Gauze" => bleeding * 100f,
            "Bloodpack" => bleeding * 50f + GetDamageAmount(damageable, "Bloodloss"),
            "EmergencyMedipen" or "EpinephrineChemistryBottle" when mobState.CurrentState == MobState.Critical =>
                1000f + general,
            "PillCanisterTricordrazine" => general,
            "HandheldHealthAnalyzer" when includeDiagnosticItems => 1f,
            _ => 0f,
        };
        return expectedBenefit > 0f
            ? expectedBenefit * 100f + priority
            : 0f;
    }

    private bool TryRetrieveDispensedVendingProduct(
        EntityUid uid,
        EntityUid vendingUid,
        string productId,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        out string status)
    {
        var candidate = rescue.PendingVendingDispensedItem;
        if (candidate is not { Valid: true } candidateUid ||
            Deleted(candidateUid) ||
            !EntityPrototypeMatches(candidateUid, productId))
        {
            rescue.PendingVendingDispensedItem = null;
            if (!TryFindDispensedVendingProduct(uid, vendingUid, productId, rescue, out candidateUid))
            {
                status = $"no dispensed {productId} found near {FormatEntityRef(vendingUid)}";
                return false;
            }

            rescue.PendingVendingDispensedItem = candidateUid;
        }

        if (!IsWithinRange(uid, candidateUid, rescue.PlayerActionRange))
        {
            status = $"moving to vended {FormatEntityRef(candidateUid)} from {FormatEntityRef(vendingUid)}";
            SetTaskMovementTarget(uid, rescue, htn, candidateUid);
            return false;
        }

        if (_hands.TryPickupAnyHand(uid, candidateUid))
        {
            status = $"picked up vended {FormatEntityRef(candidateUid)} from {FormatEntityRef(vendingUid)}";
            return true;
        }

        if (!TryComp<HandsComponent>(uid, out var hands) ||
            !_hands.TryGetEmptyHand(uid, out _, hands))
        {
            if (TryAutoStowHeldItemForTreatment(uid, rescue, out var stowStatus))
            {
                status = stowStatus;
                return false;
            }

            status = string.IsNullOrWhiteSpace(stowStatus)
                ? $"dispensed {FormatEntityRef(candidateUid)} from {FormatEntityRef(vendingUid)} but no hand is free"
                : stowStatus;
            return false;
        }

        status = $"dispensed {FormatEntityRef(candidateUid)} from {FormatEntityRef(vendingUid)} but could not pick it up";
        return false;
    }

    private bool TryFindDispensedVendingProduct(
        EntityUid uid,
        EntityUid vendingUid,
        string productId,
        LuaMRescueAgentComponent rescue,
        out EntityUid productUid)
    {
        productUid = default;
        var bestDistance = float.PositiveInfinity;
        var searchRange = Math.Max(2.5f, rescue.AutoPickupSupplyRange);

        foreach (var candidate in _lookup.GetEntitiesInRange(vendingUid, searchRange))
        {
            if (candidate == uid ||
                candidate == vendingUid ||
                Deleted(candidate) ||
                _container.IsEntityOrParentInContainer(candidate) ||
                !EntityPrototypeMatches(candidate, productId) ||
                !TryGetNavigationSelectionPenalty(uid, candidate, rescue.PlayerActionRange, out _) ||
                !TryGetDistance(vendingUid, candidate, out var distance) ||
                distance >= bestDistance)
            {
                continue;
            }

            productUid = candidate;
            bestDistance = distance;
        }

        return productUid is { Valid: true };
    }

    private bool EntityPrototypeMatches(EntityUid entity, string prototypeId)
    {
        return MetaData(entity).EntityPrototype?.ID?.Equals(prototypeId, StringComparison.OrdinalIgnoreCase) == true;
    }

    private bool TrySelectStoredItem(
        EntityUid storageUid,
        StorageComponent storage,
        string? itemSelector,
        out EntityUid storedItem,
        out string status)
    {
        storedItem = default;
        status = string.Empty;

        var contained = storage.Container.ContainedEntities;
        if (contained.Count == 0)
        {
            status = $"{FormatEntityRef(storageUid)} is empty";
            return false;
        }

        if (string.IsNullOrWhiteSpace(itemSelector))
        {
            storedItem = contained[^1];
            return true;
        }

        for (var i = contained.Count - 1; i >= 0; i--)
        {
            var candidate = contained[i];
            if (!StorageItemMatchesSelector(candidate, itemSelector))
                continue;

            storedItem = candidate;
            return true;
        }

        status = $"no stored item matching '{itemSelector}' in {FormatEntityRef(storageUid)}";
        return false;
    }

    private bool StorageItemMatchesSelector(EntityUid item, string itemSelector)
    {
        var selector = itemSelector.Trim();
        if (selector.Length == 0)
            return false;

        if (GetNetEntity(item).ToString().Equals(selector, StringComparison.OrdinalIgnoreCase) ||
            item.ToString().Equals(selector, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var lowered = selector.ToLowerInvariant();
        if (Name(item).ToLowerInvariant().Contains(lowered))
            return true;

        var prototype = MetaData(item).EntityPrototype?.ID;
        return prototype != null &&
               prototype.ToLowerInvariant().Contains(lowered);
    }

    private IEnumerable<string> GetTreatmentStorageSlots(EntityUid uid)
    {
        if (TryComp<LuaMRescueAgentComponent>(uid, out var rescue) &&
            rescue.ActivityRoleProfile.EquipmentPolicy.InventorySearchSlots.Count > 0)
        {
            return rescue.ActivityRoleProfile.EquipmentPolicy.InventorySearchSlots;
        }

        return TreatmentStorageSlotPriority;
    }

    private bool AllowsEquipment(EntityUid uid, LuaMRescueEquipmentKind equipment)
    {
        return !TryComp<LuaMRescueAgentComponent>(uid, out var rescue) ||
               rescue.ActivityRoleProfile.EquipmentPolicy.Allows(equipment);
    }

    private bool AllowsTreatmentEquipment(EntityUid uid, EntityUid item)
    {
        if (HasComp<HealthAnalyzerComponent>(item))
            return AllowsEquipment(uid, LuaMRescueEquipmentKind.Analyzer);
        if (HasComp<DefibrillatorComponent>(item))
            return AllowsEquipment(uid, LuaMRescueEquipmentKind.Defibrillator);
        if (HasComp<HealingComponent>(item))
            return AllowsEquipment(uid, LuaMRescueEquipmentKind.TopicalMedicine);
        if (HasComp<HyposprayComponent>(item) || HasComp<InjectorComponent>(item))
            return AllowsEquipment(uid, LuaMRescueEquipmentKind.Injector);

        return false;
    }

    private bool TryFindHealthAnalyzerItem(
        EntityUid uid,
        out EntityUid item,
        out string status)
    {
        item = default;
        status = string.Empty;

        if (!AllowsEquipment(uid, LuaMRescueEquipmentKind.Analyzer))
        {
            status = "role profile disallows health analyzers";
            return false;
        }

        if (!TryComp<HandsComponent>(uid, out var hands))
        {
            status = "agent has no hands";
            return false;
        }

        foreach (var hand in hands.Hands.Values)
        {
            if (hand.HeldEntity is not { Valid: true } held ||
                !HasComp<HealthAnalyzerComponent>(held))
            {
                continue;
            }

            item = held;
            return true;
        }

        if (!_hands.TryGetEmptyHand(uid, out var emptyHand, hands))
        {
            status = "no empty hand for health analyzer";
            return false;
        }

        foreach (var candidateSlot in GetTreatmentStorageSlots(uid))
        {
            if (TryTakeHealthAnalyzerFromSlot(uid, candidateSlot, emptyHand, hands, out item, out _))
                return true;
        }

        status = "no health analyzer found in hands or storage";
        return false;
    }

    private bool TryTakeHealthAnalyzerFromSlot(
        EntityUid uid,
        string slot,
        Hand emptyHand,
        HandsComponent hands,
        out EntityUid item,
        out string status)
    {
        item = default;

        if (!TryResolveStorageSlot(uid, slot, out var storageUid, out var storage, out status))
            return false;

        if (!TrySelectHealthAnalyzerItem(storageUid, storage, out var storedItem, out status))
            return false;

        if (!_container.RemoveEntity(storageUid, storedItem))
        {
            status = $"could not remove {FormatEntityRef(storedItem)} from {FormatEntityRef(storageUid)}";
            return false;
        }

        if (!_hands.TryPickup(uid, storedItem, emptyHand, handsComp: hands))
        {
            _storage.Insert(storageUid, storedItem, out _, user: uid, storageComp: storage);
            status = $"could not take {FormatEntityRef(storedItem)} from {FormatEntityRef(storageUid)} into hand";
            return false;
        }

        item = storedItem;
        status = $"took analyzer {FormatEntityRef(storedItem)} from {FormatEntityRef(storageUid)}";
        return true;
    }

    private bool TrySelectHealthAnalyzerItem(
        EntityUid storageUid,
        StorageComponent storage,
        out EntityUid item,
        out string status)
    {
        item = default;
        status = string.Empty;

        foreach (var contained in storage.Container.ContainedEntities)
        {
            if (!HasComp<HealthAnalyzerComponent>(contained))
                continue;

            item = contained;
            return true;
        }

        status = $"no health analyzer in {FormatEntityRef(storageUid)}";
        return false;
    }

    private bool TryFindDefibrillatorItem(
        EntityUid uid,
        EntityUid target,
        out EntityUid item,
        out string status)
    {
        item = default;
        status = string.Empty;

        if (!AllowsEquipment(uid, LuaMRescueEquipmentKind.Defibrillator))
        {
            status = "role profile disallows defibrillators";
            return false;
        }

        if (!TryComp<HandsComponent>(uid, out var hands))
        {
            status = "agent has no hands";
            return false;
        }

        foreach (var hand in hands.Hands.Values)
        {
            if (hand.HeldEntity is not { Valid: true } held ||
                !HasComp<DefibrillatorComponent>(held) ||
                !TryPrepareDefibrillatorCandidate(uid, target, held, out _))
            {
                continue;
            }

            item = held;
            return true;
        }

        if (!_hands.TryGetEmptyHand(uid, out var emptyHand, hands))
        {
            status = "no empty hand for defibrillator";
            return false;
        }

        foreach (var candidateSlot in GetTreatmentStorageSlots(uid))
        {
            if (TryTakeDefibrillatorFromSlot(
                    uid,
                    target,
                    candidateSlot,
                    emptyHand,
                    hands,
                    out item,
                    out _))
                return true;
        }

        status = "no charged, activatable defibrillator with an expected positive effect was found in hands or storage";
        return false;
    }

    private bool TryTakeDefibrillatorFromSlot(
        EntityUid uid,
        EntityUid target,
        string slot,
        Hand emptyHand,
        HandsComponent hands,
        out EntityUid item,
        out string status)
    {
        item = default;

        if (!TryResolveStorageSlot(uid, slot, out var storageUid, out var storage, out status))
            return false;

        if (!TrySelectDefibrillatorItem(uid, target, storageUid, storage, out var storedItem, out status))
            return false;

        if (!_container.RemoveEntity(storageUid, storedItem))
        {
            status = $"could not remove {FormatEntityRef(storedItem)} from {FormatEntityRef(storageUid)}";
            return false;
        }

        if (!_hands.TryPickup(uid, storedItem, emptyHand, handsComp: hands))
        {
            _storage.Insert(storageUid, storedItem, out _, user: uid, storageComp: storage);
            status = $"could not take {FormatEntityRef(storedItem)} from {FormatEntityRef(storageUid)} into hand";
            return false;
        }

        item = storedItem;
        status = $"took defibrillator {FormatEntityRef(storedItem)} from {FormatEntityRef(storageUid)}";
        return true;
    }

    private bool TrySelectDefibrillatorItem(
        EntityUid uid,
        EntityUid target,
        EntityUid storageUid,
        StorageComponent storage,
        out EntityUid item,
        out string status)
    {
        item = default;
        status = string.Empty;

        foreach (var contained in storage.Container.ContainedEntities)
        {
            if (!TryPrepareDefibrillatorCandidate(uid, target, contained, out _))
                continue;

            item = contained;
            return true;
        }

        status = $"no usable defibrillator in {FormatEntityRef(storageUid)}";
        return false;
    }

    private bool TryPrepareDefibrillatorCandidate(
        EntityUid uid,
        EntityUid target,
        EntityUid candidate,
        out string reason)
    {
        if (!candidate.Valid || Deleted(candidate) ||
            !TryComp<DefibrillatorComponent>(candidate, out var defibrillator))
        {
            reason = "not a defibrillator";
            return false;
        }

        if (!HasExpectedDefibrillatorBenefit(defibrillator, target))
        {
            reason = "no expected positive effect for the patient's current damage";
            return false;
        }

        if (!_powerCell.HasActivatableCharge(candidate, user: uid))
        {
            reason = LuaMRescueFailureReason.NoDefibrillatorCharge.ToString();
            return false;
        }

        if (!_itemToggle.IsActivated(candidate) &&
            !_itemToggle.TryActivate(candidate, uid))
        {
            reason = "defibrillator activation was rejected";
            return false;
        }

        reason = "usable";
        return true;
    }

    /// <summary>
    /// Adopts an exhausted low-frequency route observer without creating a new
    /// activity generation or resetting its failure budget.
    /// </summary>
    public bool TryAdoptDormantRouteRecovery(
        EntityUid agent,
        LuaMRescueAgentComponent rescue,
        LuaMRescueDormantRouteTransfer transfer)
    {
        if (Deleted(transfer.Target) ||
            rescue.ManualOverrideTarget == transfer.Target ||
            rescue.DormantRouteTarget is { Valid: true } current && current != transfer.Target)
        {
            return false;
        }

        _rescueNavigation.CancelRoute(agent, transfer.Target);
        rescue.DormantRouteTarget = transfer.Target;
        rescue.NextDormantRouteProbeAt = transfer.NextProbeAt > _timing.CurTime
            ? transfer.NextProbeAt
            : _timing.CurTime;
        rescue.DormantRouteProbeInFlight = false;
        rescue.DormantRouteProbeStartedAt = TimeSpan.Zero;
        rescue.DormantRouteActionRange = transfer.ActionRange > 0f
            ? transfer.ActionRange
            : GetPatientApproachActionRange(rescue);
        rescue.DormantRouteProbeCount = Math.Max(rescue.DormantRouteProbeCount, transfer.ProbeCount);
        rescue.DormantRouteResumeCount = Math.Max(rescue.DormantRouteResumeCount, transfer.ResumeCount);
        rescue.DormantRouteObservationSeconds = Math.Max(0.1f, transfer.ObservationSeconds);
        rescue.DormantRouteProbeTimeoutSeconds = Math.Max(0.1f, transfer.ProbeTimeoutSeconds);
        rescue.SkippedTargets[transfer.Target] = TimeSpan.MaxValue;
        if (transfer.RouteFailureAttempts > 0)
        {
            rescue.RouteFailureAttempts[transfer.Target] = Math.Max(
                rescue.RouteFailureAttempts.GetValueOrDefault(transfer.Target),
                transfer.RouteFailureAttempts);
        }
        rescue.LastDormantRouteStatus =
            $"transferred target={FormatEntityRef(transfer.Target)} from retired owner; {transfer.Status}";
        Dirty(agent, rescue);
        return true;
    }

    /// <summary>
    /// Atomically retires every dead active-role component before a replacement
    /// is selected. The permanent personnel marker survives revival, while the
    /// event lets the shuttle queue revoke work owned by the retired lineage.
    /// </summary>
    public int RetireDeadAgentsForReplacement(string reason)
    {
        var retired = new List<EntityUid>();
        var query = EntityQueryEnumerator<LuaMRescueAgentComponent, MobStateComponent>();
        while (query.MoveNext(out var uid, out var rescue, out var mobState))
        {
            if (Deleted(uid) || mobState.CurrentState != MobState.Dead)
                continue;

            EnsureComp<LuaMRescuePersonnelComponent>(uid);
            retired.Add(uid);
        }

        foreach (var uid in retired)
        {
            var retiring = new LuaMRescueAgentRetiringEvent(reason);
            RaiseLocalEvent(uid, ref retiring);
            RemComp<LuaMRescueAgentComponent>(uid);
        }

        return retired.Count;
    }

    private bool TryFindTreatmentItem(
        EntityUid uid,
        EntityUid target,
        string? slot,
        string? itemSelector,
        bool includeDiagnosticItems,
        out EntityUid item,
        out string status)
    {
        item = default;
        status = string.Empty;

        if (!TryComp<HandsComponent>(uid, out var hands))
        {
            status = "agent has no hands";
            return false;
        }

        var heldItem = EntityUid.Invalid;
        var heldScore = 0f;
        foreach (var hand in hands.Hands.Values)
        {
            if (hand.HeldEntity is not { Valid: true } held)
                continue;

            if (!AllowsTreatmentEquipment(uid, held))
                continue;

            var score = GetTreatmentItemScore(held, target, itemSelector, includeDiagnosticItems);
            if (score <= 0f ||
                score < heldScore ||
                Math.Abs(score - heldScore) < 0.001f && heldItem.Valid && held.Id >= heldItem.Id)
                continue;

            heldItem = held;
            heldScore = score;
        }

        if (heldItem.Valid && heldScore > 0f)
        {
            item = heldItem;
            return true;
        }

        if (!_hands.TryGetEmptyHand(uid, out var emptyHand, hands))
        {
            status = "no empty hand for treatment item";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(slot))
        {
            return TryTakeTreatmentItemFromSlot(uid, target, slot, itemSelector, includeDiagnosticItems, emptyHand, hands, out item, out status);
        }

        foreach (var candidateSlot in GetTreatmentStorageSlots(uid))
        {
            if (TryTakeTreatmentItemFromSlot(uid, target, candidateSlot, itemSelector, includeDiagnosticItems, emptyHand, hands, out item, out _))
                return true;
        }

        status = itemSelector == null
            ? "no usable medical item found in hands or storage"
            : $"no usable medical item matching '{itemSelector}' found in hands or storage";
        return false;
    }

    private bool TryTakeTreatmentItemFromSlot(
        EntityUid uid,
        EntityUid target,
        string slot,
        string? itemSelector,
        bool includeDiagnosticItems,
        Hand emptyHand,
        HandsComponent hands,
        out EntityUid item,
        out string status)
    {
        item = default;

        if (!TryResolveStorageSlot(uid, slot, out var storageUid, out var storage, out status))
            return false;

        if (!TrySelectTreatmentItem(
                uid,
                storageUid,
                storage,
                target,
                itemSelector,
                includeDiagnosticItems,
                out var storedItem,
                out var sourceStorageUid,
                out var sourceStorage,
                out status))
            return false;

        if (!_container.RemoveEntity(sourceStorageUid, storedItem))
        {
            status = $"could not remove {FormatEntityRef(storedItem)} from {FormatEntityRef(sourceStorageUid)}";
            return false;
        }

        if (!_hands.TryPickup(uid, storedItem, emptyHand, handsComp: hands))
        {
            _storage.Insert(sourceStorageUid, storedItem, out _, user: uid, storageComp: sourceStorage);
            status = $"could not take {FormatEntityRef(storedItem)} from {FormatEntityRef(sourceStorageUid)} into hand";
            return false;
        }

        item = storedItem;
        status = $"took {FormatEntityRef(storedItem)} from {FormatEntityRef(sourceStorageUid)}";
        return true;
    }

    public MedicalSupplySnapshot GetMedicalSupplySnapshot(EntityUid uid, EntityUid? patient)
    {
        var seen = new HashSet<EntityUid>();
        var medicalUnits = 0;
        var effectiveUnits = 0;
        var topicalUnits = 0;
        var injectorUnits = 0;
        var bloodUnits = 0;
        var diagnosticUnits = 0;

        void CountItem(EntityUid item, int depth = 0)
        {
            if (!item.Valid || Deleted(item) || !seen.Add(item))
                return;

            var units = TryComp<StackComponent>(item, out var stack) ? Math.Max(0, stack.Count) : 1;
            var medical = false;
            if (HasComp<HealingComponent>(item))
            {
                topicalUnits += units;
                medical = true;
            }
            if (HasComp<HyposprayComponent>(item) || HasComp<InjectorComponent>(item))
            {
                injectorUnits += units;
                medical = true;
            }
            if (MetaData(item).EntityPrototype?.ID?.Equals("Bloodpack", StringComparison.OrdinalIgnoreCase) == true)
            {
                bloodUnits += units;
                medical = true;
            }
            if (HasComp<HealthAnalyzerComponent>(item) || HasComp<DefibrillatorComponent>(item))
            {
                diagnosticUnits += units;
                medical = true;
            }

            if (medical && AllowsTreatmentEquipment(uid, item))
            {
                medicalUnits += units;
                if (patient is { Valid: true } target && !Deleted(target) &&
                    GetTreatmentItemScore(item, target, null, includeDiagnosticItems: false) > 0f)
                    effectiveUnits += units;
            }

            if (depth >= 2 || !TryComp<StorageComponent>(item, out var nested))
                return;
            foreach (var contained in nested.Container.ContainedEntities)
                CountItem(contained, depth + 1);
        }

        if (TryComp<HandsComponent>(uid, out var hands))
        {
            foreach (var hand in hands.Hands.Values)
            {
                if (hand.HeldEntity is { Valid: true } held)
                    CountItem(held);
            }
        }

        foreach (var slot in GetTreatmentStorageSlots(uid))
        {
            if (TryResolveStorageSlot(uid, slot, out var storageUid, out _, out _))
                CountItem(storageUid);
        }

        var status = patient is not { Valid: true }
            ? $"medical={medicalUnits}; no active patient"
            : effectiveUnits > 0
                ? $"ready; effective={effectiveUnits}; total={medicalUnits}"
                : $"depleted for active patient; total medical={medicalUnits}";
        return new MedicalSupplySnapshot(
            medicalUnits,
            effectiveUnits,
            topicalUnits,
            injectorUnits,
            bloodUnits,
            diagnosticUnits,
            status);
    }

    private bool TrySelectTreatmentItem(
        EntityUid user,
        EntityUid storageUid,
        StorageComponent storage,
        EntityUid target,
        string? itemSelector,
        bool includeDiagnosticItems,
        out EntityUid item,
        out string status)
    {
        return TrySelectTreatmentItem(
            user,
            storageUid,
            storage,
            target,
            itemSelector,
            includeDiagnosticItems,
            out item,
            out _,
            out _,
            out status);
    }

    private bool TrySelectTreatmentItem(
        EntityUid user,
        EntityUid storageUid,
        StorageComponent storage,
        EntityUid target,
        string? itemSelector,
        bool includeDiagnosticItems,
        out EntityUid item,
        out EntityUid sourceStorageUid,
        out StorageComponent sourceStorage,
        out string status,
        int depth = 0)
    {
        item = default;
        sourceStorageUid = default;
        sourceStorage = default!;
        status = string.Empty;

        var bestScore = 0f;
        foreach (var contained in storage.Container.ContainedEntities)
        {
            var score = AllowsTreatmentEquipment(user, contained)
                ? GetTreatmentItemScore(contained, target, itemSelector, includeDiagnosticItems)
                : 0f;
            if (score > bestScore)
            {
                item = contained;
                sourceStorageUid = storageUid;
                sourceStorage = storage;
                bestScore = score;
            }

            if (depth >= 2 ||
                !TryComp<StorageComponent>(contained, out var nestedStorage) ||
                !TrySelectTreatmentItem(
                    user,
                    contained,
                    nestedStorage,
                    target,
                    itemSelector,
                    includeDiagnosticItems,
                    out var nestedItem,
                    out var nestedSourceUid,
                    out var nestedSource,
                    out _,
                    depth + 1))
            {
                continue;
            }

            var nestedScore = AllowsTreatmentEquipment(user, nestedItem)
                ? GetTreatmentItemScore(nestedItem, target, itemSelector, includeDiagnosticItems)
                : 0f;
            if (nestedScore <= bestScore)
                continue;

            item = nestedItem;
            sourceStorageUid = nestedSourceUid;
            sourceStorage = nestedSource;
            bestScore = nestedScore;
        }

        if (item is { Valid: true })
            return true;

        status = itemSelector == null
            ? $"no usable medical item in {FormatEntityRef(storageUid)}"
            : $"no usable medical item matching '{itemSelector}' in {FormatEntityRef(storageUid)}";
        return false;
    }

    private float GetTreatmentItemScore(EntityUid item, EntityUid target, string? itemSelector, bool includeDiagnosticItems)
    {
        if (!string.IsNullOrWhiteSpace(itemSelector) &&
            !StorageItemMatchesSelector(item, itemSelector))
        {
            return 0f;
        }

        var score = 0f;

        if (TryComp<HealingComponent>(item, out var healing) &&
            TryGetHealingItemTargetScore(healing, target, out var healingScore))
        {
            score = Math.Max(score, healingScore);
        }

        if (TryGetInjectionItemTargetScore(item, target, out var injectionScore))
            score = Math.Max(score, injectionScore);

        if (includeDiagnosticItems && HasComp<HealthAnalyzerComponent>(item))
            score = Math.Max(score, 90f);

        return score;
    }

    /// <summary>
    /// Runtime-testable rescue treatment contract. This deliberately does not expose
    /// HypospraySystem's historical "handled but empty" behavior as a medical success.
    /// </summary>
    public bool IsEffectiveTreatmentItem(EntityUid item, EntityUid target, out string reason)
    {
        reason = "NoEffectiveMedicine";
        if (TerminatingOrDeleted(item) || TerminatingOrDeleted(target))
            return false;

        string? solutionName = null;
        var transfer = 0f;
        if (TryComp<HyposprayComponent>(item, out var hypospray))
        {
            solutionName = hypospray.SolutionName;
            transfer = hypospray.TransferAmount.Float();
        }
        else if (TryComp<InjectorComponent>(item, out var injector))
        {
            solutionName = injector.SolutionName;
            transfer = injector.TransferAmount.Float();
        }

        if (solutionName != null)
        {
            if (!_solutionContainers.TryGetSolution(item, solutionName, out _, out var solution) ||
                solution.Volume <= 0)
            {
                reason = "ItemEmpty";
                return false;
            }

            if (!TryGetInjectionItemTargetScore(item, target, out _))
            {
                Solution? bloodstream = null;
                if (TryComp<BloodstreamComponent>(target, out var bloodstreamComponent))
                {
                    _solutionContainers.TryGetSolution(
                        target,
                        bloodstreamComponent.ChemicalSolutionName,
                        out _,
                        out bloodstream);
                }

                if (solution.Contents.Any(quantity =>
                        quantity.Quantity > 0 &&
                        !CanPatientMetabolizeMedicine(target, quantity.Reagent.Prototype)))
                {
                    reason = "UnsupportedSpecies";
                }
                else if (solution.Contents.Any(quantity =>
                             HasMedicineOverdoseRisk(
                                 quantity.Reagent.Prototype,
                                 Math.Min(quantity.Quantity.Float(), transfer),
                                 bloodstream)))
                {
                    reason = "OverdoseRisk";
                }

                return false;
            }
        }

        if (GetTreatmentItemScore(item, target, null, includeDiagnosticItems: false) <= 0f)
            return false;

        reason = "None";
        return true;
    }

    private bool HasActiveMedicalDoAfter(EntityUid user, EntityUid target, out string kind)
    {
        kind = string.Empty;
        if (!TryComp<DoAfterComponent>(user, out var doAfters))
            return false;

        foreach (var doAfter in doAfters.DoAfters.Values)
        {
            if (doAfter.Cancelled ||
                doAfter.Completed ||
                doAfter.Args.Target != target)
            {
                continue;
            }

            kind = doAfter.Args.Event switch
            {
                HealingDoAfterEvent => "healing",
                HyposprayDoAfterEvent => "hypospray",
                InjectorDoAfterEvent => "injector",
                HealthAnalyzerDoAfterEvent => "analyzer",
                DefibrillatorZapDoAfterEvent => "defibrillator",
                _ => string.Empty,
            };

            if (kind.Length != 0)
                return true;
        }

        return false;
    }

    private void TrackMedicalDoAfter(
        EntityUid user,
        LuaMRescueAgentComponent rescue,
        EntityUid target,
        EntityUid? item,
        string kind)
    {
        DoAfterId? activeId = null;
        if (TryComp<DoAfterComponent>(user, out var doAfters))
        {
            foreach (var doAfter in doAfters.DoAfters.Values)
            {
                if (!doAfter.Cancelled &&
                    !doAfter.Completed &&
                    doAfter.Args.Target == target &&
                    IsMedicalDoAfterEvent(doAfter.Args.Event))
                {
                    activeId = doAfter.Id;
                    break;
                }
            }
        }

        if (rescue.PendingMedicalDoAfterTarget == target &&
            string.Equals(rescue.PendingMedicalDoAfterKind, kind, StringComparison.Ordinal))
        {
            rescue.PendingMedicalDoAfterId = activeId ?? rescue.PendingMedicalDoAfterId;
            rescue.PendingMedicalIntentGeneration = rescue.ActivityContext.Generation;
            HoldMovementForMedicalDoAfter(user, rescue, target);
            return;
        }

        rescue.PendingMedicalDoAfterTarget = target;
        rescue.PendingMedicalDoAfterItem = item;
        rescue.PendingMedicalDoAfterId = activeId;
        rescue.PendingMedicalDoAfterKind = kind;
        rescue.PendingMedicalDoAfterStartedAt = _timing.CurTime;
        rescue.PendingMedicalIntentGeneration = rescue.ActivityContext.Generation;
        rescue.PendingMedicalStartingDamage = TryComp<DamageableComponent>(target, out var damageable)
            ? damageable.TotalDamage.Float()
            : 0f;
        CopyDamageTypeSnapshot(
            GetDamageTypeSnapshot(target),
            rescue.PendingMedicalStartingDamageByType);
        rescue.PendingMedicalStartingVolume = item is { Valid: true } used && !Deleted(used)
            ? GetMedicalItemSolutionVolume(used)
            : null;
        rescue.PendingMedicalStartingMobState = TryComp<MobStateComponent>(target, out var mobState)
            ? mobState.CurrentState
            : MobState.Invalid;
        rescue.PendingMedicalStartingBleedAmount = TryComp<BloodstreamComponent>(target, out var bloodstream)
            ? bloodstream.BleedAmount
            : 0f;
        rescue.PendingMedicalStartingBloodVolume = GetPatientBloodVolume(target);
        var expectedEffect = item is { Valid: true } expectedItem && !Deleted(expectedItem)
            ? BuildMedicalEffectExpectation(expectedItem, target)
            : new MedicalEffectExpectation();
        CopyExpectedMedicalEffect(expectedEffect, rescue);
        rescue.PendingMedicalEffectVerification = false;
        rescue.PendingMedicalEffectVerificationStartedAt = TimeSpan.Zero;
        rescue.PendingMedicalExpectedPositiveEffect =
            kind is "healing" or "hypospray" or "injector" &&
            item is { Valid: true } treatmentItem &&
            !Deleted(treatmentItem) &&
            expectedEffect.HasObservableEffect &&
            IsEffectiveTreatmentItem(treatmentItem, target, out _);
        rescue.PendingMedicalOutcomeRecorded = false;
        rescue.PendingMedicalOutcomeSucceeded = false;

        // Healing, analysis and defibrillation DoAfters break on movement. The
        // activity coordinator owns this intent, so the generic FollowCompound
        // must be made inert until the authoritative result event arrives.
        HoldMovementForMedicalDoAfter(user, rescue, target);

        _activity.RecordProgress(
            user,
            Transform(target).Coordinates,
            TryGetDistance(user, target, out var distance) ? distance : null,
            LuaMRescueRouteStatus.Arrived,
            LuaMRescueDoAfterStatus.Running,
            out _);
        Dirty(user, rescue);
    }

    private void HoldMovementForMedicalDoAfter(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        EntityUid target)
    {
        if (!TryComp<HTNComponent>(uid, out var htn))
            return;

        if (htn.Blackboard.ContainsKey(NPCBlackboard.FollowTarget) ||
            htn.Plan != null ||
            htn.PlanningToken != null ||
            htn.PlanningJob != null)
        {
            CancelCurrentHtnMovement(uid, htn, cancelRouteProbe: false);
        }

        htn.Blackboard.Remove<EntityCoordinates>(NPCBlackboard.FollowTarget);
        _npc.SleepNPC(uid, htn);
        rescue.LastTargetTrackingStatus =
            $"medical movement hold: {rescue.PendingMedicalDoAfterKind} on {FormatEntityRef(target)}; " +
            $"generation={rescue.PendingMedicalIntentGeneration}";
    }

    private bool DidTrackedMedicalActionSucceed(
        LuaMRescueAgentComponent rescue,
        EntityUid target,
        EntityUid? item)
    {
        if (rescue.PendingMedicalOutcomeRecorded)
            return rescue.PendingMedicalOutcomeSucceeded;

        if (string.Equals(rescue.PendingMedicalDoAfterKind, "analyzer", StringComparison.Ordinal) &&
            item is { Valid: true } analyzer &&
            !Deleted(analyzer) &&
            TryComp<HealthAnalyzerComponent>(analyzer, out var analyzerComponent))
        {
            return analyzerComponent.ScannedEntity == target;
        }

        return HasObservedMedicalImprovement(
            target,
            rescue.PendingMedicalStartingDamageByType,
            rescue.PendingMedicalStartingBleedAmount,
            rescue.PendingMedicalStartingBloodVolume,
            rescue.PendingMedicalExpectedDamageTypes,
            rescue.PendingMedicalExpectedBleedReduction,
            rescue.PendingMedicalExpectedBloodIncrease,
            rescue.PendingMedicalStartingMobState,
            rescue.PendingMedicalExpectedMobStateImprovement);
    }

    private bool HasObservedMedicalImprovement(
        EntityUid target,
        IReadOnlyDictionary<string, float> startingDamageByType,
        float startingBleedAmount,
        float? startingBloodVolume,
        IReadOnlyCollection<string> expectedDamageTypes,
        bool expectedBleedReduction,
        bool expectedBloodIncrease,
        MobState startingMobState,
        bool expectedMobStateImprovement)
    {
        if (Deleted(target))
            return false;

        if (expectedDamageTypes.Count > 0 &&
            TryComp<DamageableComponent>(target, out var damageable))
        {
            foreach (var damageType in expectedDamageTypes)
            {
                if (!startingDamageByType.TryGetValue(damageType, out var startingAmount) ||
                    startingAmount <= 0f)
                {
                    continue;
                }

                var currentAmount = damageable.Damage.DamageDict.TryGetValue(damageType, out var current)
                    ? current.Float()
                    : 0f;
                if (currentAmount + 0.001f < startingAmount)
                    return true;
            }
        }

        if (TryComp<BloodstreamComponent>(target, out var bloodstream))
        {
            if (expectedBleedReduction &&
                bloodstream.BleedAmount + 0.001f < startingBleedAmount)
            {
                return true;
            }

            if (expectedBloodIncrease &&
                startingBloodVolume is { } initialBlood &&
                bloodstream.BloodSolution is { } bloodSolution &&
                bloodSolution.Comp.Solution.Volume.Float() > initialBlood + 0.001f)
            {
                return true;
            }
        }

        return expectedMobStateImprovement &&
               TryComp<MobStateComponent>(target, out var mobState) &&
               GetMedicalMobStateRank(mobState.CurrentState) > GetMedicalMobStateRank(startingMobState);
    }

    private static int GetMedicalMobStateRank(MobState state)
    {
        return state switch
        {
            MobState.Dead => 0,
            MobState.Critical => 1,
            MobState.Alive => 2,
            _ => -1,
        };
    }

    private Dictionary<string, float> GetDamageTypeSnapshot(EntityUid target)
    {
        var snapshot = new Dictionary<string, float>(StringComparer.Ordinal);
        if (!TryComp<DamageableComponent>(target, out var damageable))
            return snapshot;

        foreach (var (damageType, amount) in damageable.Damage.DamageDict)
            snapshot[damageType] = amount.Float();

        return snapshot;
    }

    private static void CopyDamageTypeSnapshot(
        IReadOnlyDictionary<string, float> source,
        Dictionary<string, float> destination)
    {
        destination.Clear();
        foreach (var (damageType, amount) in source)
            destination[damageType] = amount;
    }

    private bool DidTrackedMedicalSolutionTransfer(
        LuaMRescueAgentComponent rescue,
        EntityUid? item)
    {
        return item is { Valid: true } used &&
               !Deleted(used) &&
               rescue.PendingMedicalStartingVolume is { } startingVolume &&
               GetMedicalItemSolutionVolume(used) is { } remainingVolume &&
               remainingVolume + 0.001f < startingVolume;
    }

    private bool IsInjectionMedicalAction(string kind, EntityUid? item)
    {
        return kind is "hypospray" or "injector" ||
               item is { Valid: true } used &&
               !Deleted(used) &&
               (HasComp<HyposprayComponent>(used) || HasComp<InjectorComponent>(used));
    }

    private void BeginMedicalEffectVerification(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        EntityUid target,
        string kind,
        string item)
    {
        rescue.PendingMedicalDoAfterId = null;
        rescue.PendingMedicalDoAfterKind = kind;
        rescue.PendingMedicalDoAfterStartedAt = _timing.CurTime;
        rescue.PendingMedicalEffectVerification = true;
        rescue.PendingMedicalEffectVerificationStartedAt = _timing.CurTime;
        rescue.PendingMedicalOutcomeRecorded = false;
        rescue.PendingMedicalOutcomeSucceeded = false;
        rescue.TargetStallAccumulator = 0f;
        rescue.LastAutoTreatmentStatus =
            $"awaiting observed medical effect: patient={FormatEntityRef(target)}; item={item}";

        HoldMovementForMedicalDoAfter(uid, rescue, target);
        _activity.RecordProgress(
            uid,
            Transform(target).Coordinates,
            TryGetDistance(uid, target, out var distance) ? distance : null,
            LuaMRescueRouteStatus.Arrived,
            LuaMRescueDoAfterStatus.Running,
            out _);
        Dirty(uid, rescue);
    }

    private void TrackInstantMedicalEffectVerification(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        EntityUid target,
        EntityUid item,
        string kind,
        float startingDamage,
        IReadOnlyDictionary<string, float> startingDamageByType,
        float? startingVolume,
        float startingBleedAmount,
        MobState startingMobState,
        float? startingBloodVolume,
        MedicalEffectExpectation expectedEffect)
    {
        rescue.PendingMedicalDoAfterTarget = target;
        rescue.PendingMedicalDoAfterItem = item;
        rescue.PendingMedicalIntentGeneration = rescue.ActivityContext.Generation;
        rescue.PendingMedicalStartingDamage = startingDamage;
        CopyDamageTypeSnapshot(
            startingDamageByType,
            rescue.PendingMedicalStartingDamageByType);
        rescue.PendingMedicalStartingVolume = startingVolume;
        rescue.PendingMedicalStartingBleedAmount = startingBleedAmount;
        rescue.PendingMedicalStartingMobState = startingMobState;
        rescue.PendingMedicalStartingBloodVolume = startingBloodVolume;
        CopyExpectedMedicalEffect(expectedEffect, rescue);
        rescue.PendingMedicalExpectedPositiveEffect = expectedEffect.HasObservableEffect;
        BeginMedicalEffectVerification(uid, rescue, target, kind, FormatEntityRef(item));
    }

    private static void ClearTrackedMedicalDoAfter(LuaMRescueAgentComponent rescue)
    {
        rescue.PendingMedicalDoAfterTarget = null;
        rescue.PendingMedicalDoAfterItem = null;
        rescue.PendingMedicalDoAfterId = null;
        rescue.PendingMedicalDoAfterKind = "none";
        rescue.PendingMedicalDoAfterStartedAt = TimeSpan.Zero;
        rescue.PendingMedicalIntentGeneration = 0;
        rescue.PendingMedicalStartingDamage = 0f;
        rescue.PendingMedicalStartingDamageByType.Clear();
        rescue.PendingMedicalExpectedDamageTypes.Clear();
        rescue.PendingMedicalStartingVolume = null;
        rescue.PendingMedicalStartingMobState = MobState.Invalid;
        rescue.PendingMedicalStartingBleedAmount = 0f;
        rescue.PendingMedicalStartingBloodVolume = null;
        rescue.PendingMedicalExpectedBleedReduction = false;
        rescue.PendingMedicalExpectedBloodIncrease = false;
        rescue.PendingMedicalExpectedMobStateImprovement = false;
        rescue.PendingMedicalEffectVerification = false;
        rescue.PendingMedicalEffectVerificationStartedAt = TimeSpan.Zero;
        rescue.PendingMedicalExpectedPositiveEffect = false;
        rescue.PendingMedicalOutcomeRecorded = false;
        rescue.PendingMedicalOutcomeSucceeded = false;
    }

    private bool TryDiscardStaleTrackedMedicalAction(
        EntityUid uid,
        LuaMRescueAgentComponent rescue)
    {
        if (rescue.PendingMedicalDoAfterTarget is not { Valid: true } target ||
            rescue.PendingMedicalIntentGeneration == rescue.ActivityContext.Generation &&
            rescue.ActivityContext.Target == target &&
            rescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active)
        {
            return false;
        }

        var clearsManualTreatment =
            rescue.PendingPlayerAction == LuaMRescuePlayerActionKind.Treat &&
            rescue.PendingPlayerActionTarget == target;
        var status =
            $"failed treat: ActionCancelled; stale medical intent for {FormatEntityRef(target)} was replaced";
        var staleDoAfters = new HashSet<DoAfterId>();
        if (rescue.PendingMedicalDoAfterId is { } trackedId)
            staleDoAfters.Add(trackedId);
        if (TryComp<DoAfterComponent>(uid, out var doAfters))
        {
            foreach (var doAfter in doAfters.DoAfters.Values)
            {
                if (!doAfter.Cancelled &&
                    !doAfter.Completed &&
                    doAfter.Args.Target == target &&
                    IsMedicalDoAfterEvent(doAfter.Args.Event))
                {
                    staleDoAfters.Add(doAfter.Id);
                }
            }
        }

        // Cancellation raises DoAfterEndedEvent synchronously. Clear the stale
        // tracker first so that callback cannot observe or mutate the new owner.
        ClearTrackedMedicalDoAfter(rescue);
        foreach (var id in staleDoAfters)
            _doAfter.Cancel(id);
        if (clearsManualTreatment)
            ClearPendingPlayerAction(rescue, status);
        rescue.LastAutoTreatmentStatus = status;
        Dirty(uid, rescue);
        return true;
    }

    private bool TryUpdatePendingMedicalEffectVerification(
        EntityUid uid,
        LuaMRescueAgentComponent rescue)
    {
        if (!rescue.PendingMedicalEffectVerification ||
            rescue.PendingMedicalDoAfterTarget is not { Valid: true } target)
        {
            return false;
        }

        if (Deleted(target) ||
            rescue.PendingMedicalIntentGeneration != rescue.ActivityContext.Generation ||
            rescue.ActivityContext.Target != target ||
            rescue.ActivityContext.TerminalStatus != LuaMRescueTerminalStatus.Active)
        {
            var pendingGeneration = rescue.PendingMedicalIntentGeneration;
            var ownsCurrentIntent = pendingGeneration == rescue.ActivityContext.Generation &&
                                    rescue.ActivityContext.Target == target &&
                                    rescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active;
            var manual = rescue.PendingPlayerAction == LuaMRescuePlayerActionKind.Treat &&
                         rescue.PendingPlayerActionTarget == target;
            var failure = Deleted(target)
                ? LuaMRescueFailureReason.TargetLost
                : LuaMRescueFailureReason.ActionCancelled;
            ClearTrackedMedicalDoAfter(rescue);
            if (ownsCurrentIntent)
                _activity.Fail(uid, pendingGeneration, failure, out _);
            if (manual && ownsCurrentIntent && TryComp<HTNComponent>(uid, out var staleHtn))
            {
                FinishPendingPlayerAction(
                    uid,
                    rescue,
                    staleHtn,
                    $"failed treat: {failure}; pending medical effect lost its intent");
            }
            else if (manual)
            {
                ClearPendingPlayerAction(
                    rescue,
                    $"failed treat: {failure}; stale pending medical effect discarded");
            }
            Dirty(uid, rescue);
            return true;
        }

        if (DidTrackedMedicalActionSucceed(rescue, target, rescue.PendingMedicalDoAfterItem))
        {
            CompletePendingMedicalEffectVerification(uid, rescue, target, succeeded: true);
            return true;
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(0.05f, rescue.MedicalEffectVerificationTimeout));
        if (_timing.CurTime - rescue.PendingMedicalEffectVerificationStartedAt < timeout)
        {
            rescue.TargetStallAccumulator = 0f;
            HoldMovementForMedicalDoAfter(uid, rescue, target);
            return true;
        }

        CompletePendingMedicalEffectVerification(uid, rescue, target, succeeded: false);
        return true;
    }

    private void CompletePendingMedicalEffectVerification(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        EntityUid target,
        bool succeeded)
    {
        var kind = rescue.PendingMedicalDoAfterKind;
        var used = rescue.PendingMedicalDoAfterItem;
        var item = used is { Valid: true } usedItem && !Deleted(usedItem)
            ? FormatEntityRef(usedItem)
            : "none";
        var completesManualTreatment =
            rescue.PendingPlayerAction == LuaMRescuePlayerActionKind.Treat &&
            rescue.PendingPlayerActionTarget == target;
        var onboard = !Deleted(target) && IsOnAssignedShuttle(target, rescue);
        var coordinates = Deleted(target) ? (EntityCoordinates?) null : Transform(target).Coordinates;
        var remainingDistance = !Deleted(target) && TryGetDistance(uid, target, out var distance)
            ? distance
            : (float?) null;

        ClearTrackedMedicalDoAfter(rescue);

        if (succeeded)
        {
            var status =
                $"{kind} effect confirmed: patient={FormatEntityRef(target)}; item={item}";
            rescue.LastAutoTreatmentStatus = status;
            rescue.TargetStallAccumulator = 0f;
            rescue.OnboardCareAttempts.Remove(target);
            ClearTreatmentFailure(rescue, target);
            rescue.NextAutoTreatmentAttempt = _timing.CurTime +
                TimeSpan.FromSeconds(Math.Max(0.1f, rescue.AutoTreatCooldown));
            _activity.RecordProgress(
                uid,
                coordinates,
                remainingDistance,
                LuaMRescueRouteStatus.Arrived,
                LuaMRescueDoAfterStatus.Succeeded,
                out _);

            if (completesManualTreatment && TryComp<HTNComponent>(uid, out var successHtn))
                FinishPendingPlayerAction(uid, rescue, successHtn, status);
        }
        else
        {
            var detail =
                $"{kind} transfer produced no observed damage, bleed, or MobState improvement within " +
                $"{Math.Max(0.05f, rescue.MedicalEffectVerificationTimeout):0.00}s";
            _activity.RecordProgress(
                uid,
                coordinates,
                remainingDistance,
                LuaMRescueRouteStatus.Arrived,
                LuaMRescueDoAfterStatus.Failed,
                out _);
            RecordTreatmentFailure(
                uid,
                rescue,
                target,
                LuaMRescueFailureReason.NoEffectiveMedicine,
                detail);

            if (onboard)
            {
                var attempts = rescue.OnboardCareAttempts.GetValueOrDefault(target) + 1;
                rescue.OnboardCareAttempts[target] = attempts;
                if (attempts >= Math.Max(1, rescue.ActivityRoleProfile.MaxAttempts))
                {
                    MarkTerminalOnboardCareFailure(
                        uid,
                        rescue,
                        target,
                        LuaMRescueFailureReason.NoEffectiveMedicine,
                        detail);
                }
            }

            if (completesManualTreatment && TryComp<HTNComponent>(uid, out var failureHtn))
            {
                FinishPendingPlayerAction(
                    uid,
                    rescue,
                    failureHtn,
                    $"failed treat: {LuaMRescueFailureReason.NoEffectiveMedicine}; {detail}");
            }
        }

        Dirty(uid, rescue);
    }

    private bool TryExpireMedicalDoAfter(EntityUid uid, LuaMRescueAgentComponent rescue)
    {
        if (rescue.PendingMedicalDoAfterTarget is not { Valid: true } target ||
            _timing.CurTime - rescue.PendingMedicalDoAfterStartedAt < MedicalDoAfterHardTimeout)
        {
            return false;
        }

        var kind = rescue.PendingMedicalDoAfterKind;
        var trackedId = rescue.PendingMedicalDoAfterId;
        var elapsed = _timing.CurTime - rescue.PendingMedicalDoAfterStartedAt;
        var expiresManualTreatment =
            rescue.PendingPlayerAction == LuaMRescuePlayerActionKind.Treat &&
            rescue.PendingPlayerActionTarget == target;
        var ids = new List<DoAfterId>();
        if (trackedId is { } trackedDoAfterId)
        {
            ids.Add(trackedDoAfterId);
        }
        else if (TryComp<DoAfterComponent>(uid, out var doAfters))
        {
            foreach (var doAfter in doAfters.DoAfters.Values)
            {
                if (!doAfter.Cancelled &&
                    !doAfter.Completed &&
                    doAfter.Args.Target == target &&
                    IsMedicalDoAfterEvent(doAfter.Args.Event))
                {
                    ids.Add(doAfter.Id);
                }
            }
        }

        // Clear first: cancellation raises DoAfterEndedEvent synchronously and a
        // timed-out operation must not be accepted as the current generation.
        ClearTrackedMedicalDoAfter(rescue);
        foreach (var id in ids)
            _doAfter.Cancel(id);

        EntityCoordinates? targetCoordinates = null;
        float? remainingDistance = null;
        if (!Deleted(target))
        {
            targetCoordinates = Transform(target).Coordinates;
            if (TryGetDistance(uid, target, out var distance))
                remainingDistance = distance;
        }

        _activity.RecordProgress(
            uid,
            targetCoordinates,
            remainingDistance,
            LuaMRescueRouteStatus.Arrived,
            LuaMRescueDoAfterStatus.TimedOut,
            out _);

        var detail = $"{kind} DoAfter exceeded {elapsed.TotalSeconds:0.0}s hard timeout";
        if (string.Equals(kind, "defibrillator", StringComparison.Ordinal))
        {
            MarkTerminalDefibrillationFailure(
                uid,
                rescue,
                target,
                LuaMRescueFailureReason.DoAfterTimedOut,
                $"{detail}; transport/handoff fallback");
        }
        else if (string.Equals(kind, "analyzer", StringComparison.Ordinal))
        {
            RecordAnalysisFailure(
                uid,
                rescue,
                target,
                LuaMRescueFailureReason.DoAfterTimedOut,
                detail,
                forceTerminal: true);
        }
        else
        {
            RecordTreatmentFailure(
                uid,
                rescue,
                target,
                LuaMRescueFailureReason.DoAfterTimedOut,
                detail,
                forceTerminal: true);
            if (!Deleted(target) && IsOnAssignedShuttle(target, rescue))
            {
                MarkTerminalOnboardCareFailure(
                    uid,
                    rescue,
                    target,
                    LuaMRescueFailureReason.DoAfterTimedOut,
                    detail);
            }
        }

        if (expiresManualTreatment && TryComp<HTNComponent>(uid, out var htn))
        {
            FinishPendingPlayerAction(
                uid,
                rescue,
                htn,
                $"failed treat: DoAfterTimedOut after {elapsed.TotalSeconds:0.0}s");
        }

        Dirty(uid, rescue);
        return true;
    }

    private bool HasTerminalTreatmentFailure(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        EntityUid target)
    {
        if (!rescue.TerminalTreatmentFailures.TryGetValue(target, out var failure))
            return false;

        // Keep the current bounded attempt terminal until its recovery delay
        // expires. ApplyTerminalTreatmentFallback parks an unsupported patient
        // long enough to avoid a hot retry loop, while PruneSkippedTargets later
        // re-arms treatment so newly supplied medicine can be discovered.
        rescue.LastAutoTreatmentStatus = failure;
        _activity.Fail(uid, LuaMRescueFailureReason.NoEffectiveMedicine, out _);
        ApplyTerminalTreatmentFallback(uid, rescue, target);
        return true;
    }

    private bool RecordTreatmentFailure(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        EntityUid target,
        LuaMRescueFailureReason reason,
        string detail,
        bool forceTerminal = false)
    {
        var attempts = rescue.TreatmentAttempts.GetValueOrDefault(target) + 1;
        rescue.TreatmentAttempts[target] = attempts;
        _activity.RecordAttempt(uid, out _);

        if (!forceTerminal && attempts < Math.Max(1, rescue.ActivityRoleProfile.MaxAttempts))
        {
            rescue.LastAutoTreatmentStatus =
                $"{reason}: treatment attempt {attempts}/{rescue.ActivityRoleProfile.MaxAttempts} failed; {detail}";
            Dirty(uid, rescue);
            return false;
        }

        var terminal =
            $"{reason}: treatment terminal after {attempts} bounded attempts; {detail}; patient={FormatEntityRef(target)}";
        rescue.TerminalTreatmentFailures[target] = terminal;
        rescue.TerminalTreatmentFailureDamage[target] =
            TryComp<DamageableComponent>(target, out var damageable)
                ? damageable.TotalDamage.Float()
                : 0f;
        rescue.LastAutoTreatmentStatus = terminal;
        _activity.Fail(uid, reason, out _);
        ApplyTerminalTreatmentFallback(uid, rescue, target);
        Dirty(uid, rescue);
        return true;
    }

    private void ApplyTerminalTreatmentFallback(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        EntityUid target)
    {
        if (Deleted(target))
            return;

        if (NeedsEvacuation(uid, target, rescue))
        {
            rescue.LastAutoTreatmentStatus += "; fallback=transport/handoff";
            return;
        }

        // A non-evacuation patient with no effective bounded treatment must not
        // remain the coordinator owner forever, but an equipment failure must
        // not blacklist the patient forever either. Park the episode with a
        // bounded delay; expiry clears the failed budget and permits a fresh
        // scan after players resupply the responder or the patient changes.
        var retrySeconds = Math.Max(
            5f,
            Math.Max(rescue.TargetSkipSeconds, rescue.AutoTreatCooldown));
        rescue.SkippedTargets[target] =
            _timing.CurTime + TimeSpan.FromSeconds(retrySeconds);
        rescue.LastAutoTreatmentStatus += $"; retry-after={retrySeconds:0.0}s";
        StopPullingTarget(uid, target);
        ClearRescueTask(uid, rescue, $"NeedsSpecialist: terminal treatment for {FormatEntityRef(target)}");
        ClearArrivalReportTarget(rescue, target);
        ClearTriageDecisionTarget(rescue, target);
        ClearDeathSignalTarget(rescue, target);
        rescue.EvacuatingTarget = null;
        rescue.AssignedTarget = null;
        rescue.AssignedPatientStrap = null;
        ResetTargetProgress(rescue);

        if (TryComp<HTNComponent>(uid, out var htn))
            StandbyAtAssignedShuttle(uid, rescue, htn);
    }

    private static void ClearTreatmentFailure(LuaMRescueAgentComponent rescue, EntityUid target)
    {
        rescue.TreatmentAttempts.Remove(target);
        rescue.TerminalTreatmentFailures.Remove(target);
        rescue.TerminalTreatmentFailureDamage.Remove(target);
    }

    private void RecordAnalysisFailure(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        EntityUid target,
        LuaMRescueFailureReason reason,
        string detail,
        bool forceTerminal = false)
    {
        var attempts = rescue.AnalysisAttempts.GetValueOrDefault(target) + 1;
        rescue.AnalysisAttempts[target] = attempts;
        _activity.RecordAttempt(uid, out _);

        var terminal = forceTerminal || attempts >= Math.Max(1, rescue.ActivityRoleProfile.MaxAttempts);
        if (!terminal)
        {
            rescue.LastAutoAnalyzeStatus =
                $"{reason}: analyzer attempt {attempts}/{rescue.ActivityRoleProfile.MaxAttempts}; {detail}";
            Dirty(uid, rescue);
            return;
        }

        var status =
            $"{reason}: analyzer failed after {attempts} bounded attempts; patient={FormatEntityRef(target)}; {detail}";
        rescue.TerminalAnalysisFailures[target] = status;
        rescue.LastAutoAnalyzeStatus = status;
        _activity.Fail(uid, reason, out _);
        Dirty(uid, rescue);
    }

    private static bool IsMedicalDoAfterEvent(DoAfterEvent doAfterEvent)
    {
        return doAfterEvent is HealingDoAfterEvent
            or HyposprayDoAfterEvent
            or InjectorDoAfterEvent
            or HealthAnalyzerDoAfterEvent
            or DefibrillatorZapDoAfterEvent;
    }

    private void CancelMedicalDoAftersForIntentReplacement(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        string reason,
        EntityUid? exactTarget = null,
        bool recordActivityProgress = true)
    {
        var ids = new List<DoAfterId>();
        if (TryComp<DoAfterComponent>(uid, out var doAfters))
        {
            foreach (var doAfter in doAfters.DoAfters.Values)
            {
                if (!doAfter.Cancelled &&
                    !doAfter.Completed &&
                    IsMedicalDoAfterEvent(doAfter.Args.Event) &&
                    (exactTarget == null || doAfter.Args.Target == exactTarget))
                {
                    ids.Add(doAfter.Id);
                }
            }
        }

        var clearTrackedMedical = exactTarget == null
            ? rescue.PendingMedicalDoAfterTarget != null
            : rescue.PendingMedicalDoAfterTarget == exactTarget;
        if (ids.Count == 0 && !clearTrackedMedical)
            return;

        if (clearTrackedMedical)
            ClearTrackedMedicalDoAfter(rescue);
        foreach (var id in ids)
            _doAfter.Cancel(id);

        rescue.LastAutoTreatmentStatus = $"ActionCancelled: stale medical action cancelled; {reason}";
        if (recordActivityProgress)
        {
            _activity.RecordProgress(
                uid,
                destination: null,
                remainingDistance: null,
                LuaMRescueRouteStatus.None,
                LuaMRescueDoAfterStatus.Cancelled,
                out _);
        }
        Dirty(uid, rescue);
    }

    public void PrepareForExternalIntentReplacement(
        EntityUid uid,
        EntityUid newTarget)
    {
        if (!TryComp<LuaMRescueAgentComponent>(uid, out var rescue) ||
            !TryComp<HTNComponent>(uid, out var htn))
        {
            return;
        }

        if (newTarget.Valid)
            rescue.DeferredPatientTargets.Remove(newTarget);

        var sameTrackedTarget = rescue.PendingMedicalDoAfterTarget == null ||
                                rescue.PendingMedicalDoAfterTarget == newTarget;

        // ActivityContext is the authoritative owner of the current generation.
        // Legacy mirror fields can be transiently stale while an atomic priority
        // transfer is being reconciled; they must never cancel an already-active
        // intent for the requested target.
        if (newTarget.Valid &&
            rescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active &&
            rescue.ActivityContext.Target == newTarget &&
            sameTrackedTarget)
        {
            return;
        }

        if (rescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active &&
            TryGetActivePatientTarget(rescue, out var current, includeManualOverride: false) &&
            current == newTarget &&
            sameTrackedTarget)
        {
            return;
        }

        CancelMedicalDoAftersForIntentReplacement(uid, rescue, $"newTarget={FormatEntityRef(newTarget)}");
        var replacedRouteTargets = new HashSet<EntityUid>();
        if (TryGetActivePatientTarget(rescue, out current, includeManualOverride: false) && current != newTarget)
        {
            StopPullingTarget(uid, current);
            replacedRouteTargets.Add(current);
        }

        if (htn.Blackboard.TryGetValue<EntityCoordinates>(
                NPCBlackboard.FollowTarget,
                out var previousFollow,
                EntityManager) &&
            previousFollow.EntityId is { Valid: true } previousTarget &&
            previousTarget != newTarget)
        {
            replacedRouteTargets.Add(previousTarget);
        }

        // HTN steering belongs to the old generation, but a completed route
        // probe for the newly requested target is authoritative selection data
        // and may already have been computed by dispatch. Cancel only the route
        // owned by the displaced target instead of wiping every probe for the
        // agent, including the replacement's cache.
        CancelCurrentHtnMovement(uid, htn, cancelRouteProbe: false);
        if (!newTarget.Valid)
        {
            _rescueNavigation.CancelRoute(uid);
        }
        else
        {
            foreach (var oldRouteTarget in replacedRouteTargets)
                _rescueNavigation.CancelRoute(uid, oldRouteTarget);
        }

        var cancelledGeneration = rescue.ActivityContext.Generation;
        var cancellationStatus =
            $"external replacement: current={FormatEntityRef(current)}; new={FormatEntityRef(newTarget)}; " +
            $"assigned={FormatEntityRef(rescue.AssignedTarget)}; task={FormatEntityRef(rescue.TaskPatientTarget)}; " +
            $"evacuating={FormatEntityRef(rescue.EvacuatingTarget)}; onboard={FormatEntityRef(rescue.OnboardCareTarget)}; " +
            $"deathSignal={FormatEntityRef(rescue.DeathSignalTarget)}; " +
            $"activity={rescue.ActivityContext.Activity}; generation={cancelledGeneration}";
        if (_activity.Cancel(uid, cancelledGeneration, LuaMRescueFailureReason.Cancelled, out _))
            rescue.LastIntentCancellationStatus = cancellationStatus;
    }

    /// <summary>
    /// Clears bounded failure and retry memory that belongs to a completed
    /// critical/death episode. Physical ownership (for example a patient still
    /// strapped onboard) and an explicit manual order are deliberately retained.
    /// </summary>
    public void CloseRecoveredPatientEpisode(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        EntityUid target)
    {
        if (rescue.ActivityContext.Target == target &&
            rescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active)
        {
            _activity.Complete(uid, rescue.ActivityContext.Generation, out _);
        }

        rescue.SkippedTargets.Remove(target);
        rescue.DeferredPatientTargets.Remove(target);
        rescue.RouteFailureAttempts.Remove(target);
        rescue.AnalyzedTargets.Remove(target);
        rescue.AnalysisAttempts.Remove(target);
        rescue.TerminalAnalysisFailures.Remove(target);

        ClearTreatmentFailure(rescue, target);
        rescue.DefibrillationAttempts.Remove(target);
        rescue.CompletedDefibrillationFailures.Remove(target);
        rescue.DefibrillationStartedAt.Remove(target);
        rescue.TerminalDefibrillationFailures.Remove(target);
        rescue.PullAttempts.Remove(target);
        rescue.NextPullAttemptAt.Remove(target);
        rescue.TerminalPullFailures.Remove(target);
        rescue.EvacuationUnbuckleAttempts.Remove(target);
        rescue.NextEvacuationUnbuckleAttemptAt.Remove(target);
        rescue.TerminalEvacuationUnbuckleFailures.Remove(target);
        ClearPatientBuckleFailures(rescue, target);
        rescue.OnboardCareAttempts.Remove(target);
        rescue.TerminalOnboardCareFailures.Remove(target);
        rescue.OnboardHandoffAttempts.Remove(target);
        rescue.IgnoredOnboardPatients.Remove(target);

        if (rescue.DormantRouteTarget == target)
        {
            if (rescue.DormantRouteProbeInFlight)
                _rescueNavigation.CancelRoute(uid, target);
            ClearDormantRouteRecovery(rescue, "medical episode recovered", clearFailureBudget: true);
        }

        if (rescue.ArrivalReportedTarget == target)
            rescue.ArrivalReportedTarget = null;
        if (rescue.TriageReportedTarget == target)
            rescue.TriageReportedTarget = null;
        if (rescue.ShuttleRoutedTarget == target)
            rescue.ShuttleRoutedTarget = null;
        if (rescue.DeathSignalTarget == target)
        {
            rescue.DeathSignalTarget = null;
            rescue.DeathSignalDispatchReported = false;
        }

        rescue.LastDeferredPatientStatus = $"medical episode recovered for {FormatEntityRef(target)}";
        Dirty(uid, rescue);
    }

    /// <summary>
    /// An explicit order is an atomic ownership transfer. Merely cancelling HTN
    /// is insufficient because evacuation/onboard/death-signal fields are also
    /// consulted as authoritative patient owners by later updates and dispatch.
    /// </summary>
    private void ClearPatientIntentOwnershipForExplicitOrder(
        EntityUid uid,
        LuaMRescueAgentComponent rescue)
    {
        var previousTargets = new HashSet<EntityUid>();
        if (rescue.EvacuatingTarget is { Valid: true } evacuating)
            previousTargets.Add(evacuating);
        if (rescue.OnboardCareTarget is { Valid: true } onboard)
            previousTargets.Add(onboard);
        if (rescue.AssignedTarget is { Valid: true } assigned)
            previousTargets.Add(assigned);
        if (rescue.DeathSignalTarget is { Valid: true } deathSignal)
            previousTargets.Add(deathSignal);
        if (rescue.TaskPatientTarget is { Valid: true } taskPatient)
            previousTargets.Add(taskPatient);

        foreach (var previous in previousTargets)
        {
            if (Deleted(previous))
                continue;

            StopPullingTarget(uid, previous);
            ClearArrivalReportTarget(rescue, previous);
            ClearTriageDecisionTarget(rescue, previous);
        }

        rescue.EvacuatingTarget = null;
        rescue.OnboardCareTarget = null;
        rescue.AssignedTarget = null;
        RevokeManualOverrideTarget(uid, rescue, null, "explicit patient order replacement");
        rescue.AssignedPatientStrap = null;
        rescue.DeathSignalTarget = null;
        rescue.DeathSignalDispatchReported = false;
        ClearRescueTask(uid, rescue, "explicit order released previous patient ownership");
    }

    private bool TryGetBlockingOnboardPatientForExplicitOrder(
        LuaMRescueAgentComponent rescue,
        EntityUid? requestedTarget,
        out EntityUid patient)
    {
        foreach (var required in rescue.RequiredOnboardHandoffPatients)
        {
            if (!Deleted(required) && required != requestedTarget)
            {
                patient = required;
                return true;
            }
        }

        if (rescue.OnboardCareTarget is { Valid: true } onboard &&
            onboard != requestedTarget &&
            !Deleted(onboard) &&
            TryComp<BuckleComponent>(onboard, out var buckle) &&
            buckle.BuckledTo is { Valid: true } actualStrap &&
            IsAssignedShuttlePatientStrap(actualStrap, rescue))
        {
            patient = onboard;
            return true;
        }

        if (rescue.AssignedPatientStrap is { Valid: true } assignedStrap &&
            !Deleted(assignedStrap) &&
            IsAssignedShuttlePatientStrap(assignedStrap, rescue) &&
            TryComp<StrapComponent>(assignedStrap, out var strap))
        {
            foreach (var buckled in strap.BuckledEntities)
            {
                if (!Deleted(buckled) && buckled != requestedTarget)
                {
                    patient = buckled;
                    return true;
                }
            }
        }

        patient = default;
        return false;
    }

    private float? GetMedicalItemSolutionVolume(EntityUid item)
    {
        var solutionName = TryComp<HyposprayComponent>(item, out var hypospray)
            ? hypospray.SolutionName
            : TryComp<InjectorComponent>(item, out var injector)
                ? injector.SolutionName
                : null;

        if (solutionName == null ||
            !_solutionContainers.TryGetSolution(item, solutionName, out _, out var solution))
        {
            return null;
        }

        return solution.Volume.Float();
    }

    private float? GetPatientBloodVolume(EntityUid target)
    {
        return TryComp<BloodstreamComponent>(target, out var bloodstream) &&
               bloodstream.BloodSolution is { } bloodSolution
            ? bloodSolution.Comp.Solution.Volume.Float()
            : null;
    }

    private MedicalEffectExpectation BuildMedicalEffectExpectation(EntityUid item, EntityUid target)
    {
        var expectation = new MedicalEffectExpectation();
        if (Deleted(item) || Deleted(target))
            return expectation;

        if (TryComp<HealingComponent>(item, out var healing))
        {
            foreach (var (damageType, amount) in healing.Damage.DamageDict)
            {
                if (amount < 0)
                    expectation.DamageTypes.Add(damageType);
            }

            expectation.ReducesBleeding |= healing.BloodlossModifier < 0f;
            expectation.IncreasesBloodVolume |= healing.ModifyBloodLevel > 0f;
        }

        var solutionName = TryComp<HyposprayComponent>(item, out var hypospray)
            ? hypospray.SolutionName
            : TryComp<InjectorComponent>(item, out var injector)
                ? injector.SolutionName
                : null;
        if (solutionName == null ||
            !_solutionContainers.TryGetSolution(item, solutionName, out _, out var solution))
        {
            return expectation;
        }

        var critical = TryComp<MobStateComponent>(target, out var mobState) &&
                       mobState.CurrentState == MobState.Critical;
        foreach (var quantity in solution.Contents)
        {
            if (quantity.Quantity <= 0)
                continue;

            AddExpectedReagentEffects(expectation, quantity.Reagent.Prototype, critical);
        }

        return expectation;
    }

    private static void AddExpectedReagentEffects(
        MedicalEffectExpectation expectation,
        string reagent,
        bool critical)
    {
        switch (reagent)
        {
            case "Bicaridine":
                AddExpectedDamageTypes(expectation, "Blunt", "Slash", "Piercing");
                expectation.ReducesBleeding = true;
                break;
            case "Dermaline":
            case "Kelotane":
                AddExpectedDamageTypes(expectation, "Heat", "Shock", "Cold");
                break;
            case "Dexalin":
            case "DexalinPlus":
                AddExpectedDamageTypes(expectation, "Asphyxiation", "Bloodloss");
                break;
            case "Dylovene":
                AddExpectedDamageTypes(expectation, "Poison");
                break;
            case "Inaprovaline":
                if (critical)
                {
                    AddExpectedDamageTypes(expectation, "Asphyxiation");
                    expectation.ImprovesMobState = true;
                }
                expectation.ReducesBleeding = true;
                break;
            case "Epinephrine" when critical:
                AddExpectedDamageTypes(
                    expectation,
                    "Blunt", "Slash", "Piercing",
                    "Heat", "Shock", "Cold", "Caustic",
                    "Asphyxiation", "Poison");
                expectation.ImprovesMobState = true;
                break;
            case "Tricordrazine":
                AddExpectedDamageTypes(
                    expectation,
                    "Blunt", "Slash", "Piercing",
                    "Heat", "Shock", "Cold", "Caustic",
                    "Poison", "Radiation", "Cellular");
                break;
            case "Omnizine":
                AddExpectedDamageTypes(
                    expectation,
                    "Blunt", "Slash", "Piercing",
                    "Heat", "Shock", "Cold", "Caustic",
                    "Poison", "Radiation",
                    "Asphyxiation", "Bloodloss");
                break;
            case "TranexamicAcid":
                expectation.ReducesBleeding = true;
                break;
            case "Leporazine":
                AddExpectedDamageTypes(expectation, "Cold");
                break;
        }
    }

    private static void AddExpectedDamageTypes(
        MedicalEffectExpectation expectation,
        params string[] damageTypes)
    {
        foreach (var damageType in damageTypes)
            expectation.DamageTypes.Add(damageType);
    }

    private static void CopyExpectedMedicalEffect(
        MedicalEffectExpectation source,
        LuaMRescueAgentComponent rescue)
    {
        rescue.PendingMedicalExpectedDamageTypes.Clear();
        rescue.PendingMedicalExpectedDamageTypes.UnionWith(source.DamageTypes);
        rescue.PendingMedicalExpectedBleedReduction = source.ReducesBleeding;
        rescue.PendingMedicalExpectedBloodIncrease = source.IncreasesBloodVolume;
        rescue.PendingMedicalExpectedMobStateImprovement = source.ImprovesMobState;
    }

    private bool TryGetHealingItemTargetScore(HealingComponent healing, EntityUid target, out float score)
    {
        score = 0f;

        if (!TryComp<DamageableComponent>(target, out var damageable))
            return false;

        if (healing.DamageContainers is not null &&
            damageable.DamageContainerID is not null &&
            !healing.DamageContainers.Contains(damageable.DamageContainerID))
        {
            return false;
        }

        var matchedDamage = 0f;
        var matchedHealing = 0f;
        var matchedTypes = 0;

        foreach (var (type, healingValue) in healing.Damage.DamageDict)
        {
            if (healingValue >= 0 ||
                !damageable.Damage.DamageDict.TryGetValue(type, out var damageValue) ||
                damageValue <= 0)
                continue;

            matchedDamage += damageValue.Float();
            matchedHealing += Math.Min(damageValue.Float(), Math.Abs(healingValue.Float()));
            matchedTypes++;
        }

        if (matchedTypes > 0)
        {
            score = 120f + matchedHealing * 2f + matchedDamage + matchedTypes * 5f;
            return true;
        }

        if (TryComp<BloodstreamComponent>(target, out var bloodstream) &&
            (healing.BloodlossModifier < 0f && bloodstream.BleedAmount > 0f ||
             healing.ModifyBloodLevel > 0f &&
             bloodstream.BloodSolution is { } bloodSolution &&
             bloodSolution.Comp.Solution.Volume < bloodstream.BloodMaxVolume))
        {
            score = 105f + bloodstream.BleedAmount * 10f;
            return true;
        }

        return false;
    }

    private bool TryGetInjectionItemTargetScore(EntityUid item, EntityUid target, out float score)
    {
        score = 0f;

        string solutionName;
        float transferAmount;
        if (TryComp<HyposprayComponent>(item, out var hypospray))
        {
            solutionName = hypospray.SolutionName;
            transferAmount = hypospray.TransferAmount.Float();
        }
        else if (TryComp<InjectorComponent>(item, out var injector))
        {
            if (injector.ToggleState != InjectorToggleMode.Inject || injector.IgnoreMobs)
                return false;

            solutionName = injector.SolutionName;
            transferAmount = injector.TransferAmount.Float();
        }
        else
        {
            return false;
        }

        if (!_solutionContainers.TryGetSolution(item, solutionName, out _, out var solution) ||
            solution.Volume <= 0 ||
            !HasComp<InjectableSolutionComponent>(target) ||
            !TryComp<MobStateComponent>(target, out var mobState) ||
            !TryComp<DamageableComponent>(target, out var damageable))
        {
            return false;
        }

        Solution? bloodstream = null;
        if (TryComp<BloodstreamComponent>(target, out var bloodstreamComponent))
        {
            _solutionContainers.TryGetSolution(
                target,
                bloodstreamComponent.ChemicalSolutionName,
                out _,
                out bloodstream);
        }

        var solutionVolume = solution.Volume.Float();
        var actualTransfer = Math.Min(transferAmount, solutionVolume);
        if (actualTransfer <= 0f)
            return false;

        var weightedBenefit = 0f;
        foreach (var quantity in solution.Contents)
        {
            var reagent = quantity.Reagent.Prototype;
            // Solution transfer preserves mixture proportions. Treating every
            // reagent as if it independently transferred the full injector
            // amount both exaggerated overdose risk and let trace quantities
            // dominate capability scoring.
            var incoming = actualTransfer * quantity.Quantity.Float() / solutionVolume;
            if (incoming <= 0f)
                continue;

            // The rescue planner is intentionally allow-list based. A mixed
            // injector containing an unknown compound cannot be proven safe just
            // because another reagent in it would be beneficial.
            if (!ConservativeMedicineLimits.ContainsKey(reagent) ||
                !CanPatientMetabolizeMedicine(target, reagent) ||
                HasMedicineOverdoseRisk(reagent, incoming, bloodstream))
            {
                return false;
            }

            var reagentScore = GetExpectedMedicineBenefit(reagent, target, mobState, damageable);
            weightedBenefit += reagentScore * (incoming / actualTransfer);
        }

        if (weightedBenefit <= 0f)
            return false;

        score = 110f + weightedBenefit;
        return true;
    }

    private static bool HasMedicineOverdoseRisk(string reagent, float incoming, Solution? bloodstream)
    {
        if (!ConservativeMedicineLimits.TryGetValue(reagent, out var limit))
            return false;

        var current = bloodstream?.Contents
            .Where(quantity => quantity.Reagent.Prototype.Equals(reagent, StringComparison.Ordinal))
            .Sum(quantity => quantity.Quantity.Float()) ?? 0f;
        return current + incoming >= limit;
    }

    private bool CanPatientMetabolizeMedicine(EntityUid target, string reagent)
    {
        if (!_prototype.TryIndex<ReagentPrototype>(reagent, out var prototype) ||
            prototype.Metabolisms is null)
        {
            return false;
        }

        foreach (var metabolizer in _body.GetBodyOrganEntityComps<MetabolizerComponent>((target, null)))
        {
            if (metabolizer.Comp1.MetabolismGroups is null)
                continue;

            foreach (var group in metabolizer.Comp1.MetabolismGroups)
            {
                if (prototype.Metabolisms.ContainsKey(group.Id))
                    return true;
            }
        }

        return false;
    }

    private float GetExpectedMedicineBenefit(
        string reagent,
        EntityUid target,
        MobStateComponent mobState,
        DamageableComponent damageable)
    {
        var brute = GetDamageAmount(damageable, "Blunt", "Slash", "Piercing");
        var burn = GetDamageAmount(damageable, "Heat", "Cold", "Shock", "Caustic");
        var dermalBurn = GetDamageAmount(damageable, "Heat", "Cold", "Shock");
        var asphyxiation = GetDamageAmount(damageable, "Asphyxiation", "Bloodloss");
        var toxin = GetDamageAmount(damageable, "Poison", "Radiation", "Cellular");
        var toxinWithoutCellular = GetDamageAmount(damageable, "Poison", "Radiation");
        var poison = GetDamageAmount(damageable, "Poison");
        var bleeding = TryComp<BloodstreamComponent>(target, out var bloodstream)
            ? Math.Max(0f, bloodstream.BleedAmount)
            : 0f;

        return reagent switch
        {
            "Bicaridine" => brute + bleeding * 10f,
            "Dermaline" or "Kelotane" => dermalBurn,
            "Dexalin" or "DexalinPlus" => asphyxiation,
            "Dylovene" => poison,
            "Inaprovaline" when mobState.CurrentState == MobState.Critical =>
                GetDamageAmount(damageable, "Asphyxiation") + bleeding * 10f,
            "Epinephrine" when mobState.CurrentState == MobState.Critical =>
                brute + burn + GetDamageAmount(damageable, "Asphyxiation", "Poison"),
            "Tricordrazine" => brute + burn + toxin,
            "Omnizine" => brute + burn + toxinWithoutCellular + asphyxiation,
            "TranexamicAcid" when bleeding > 0f => 75f + bleeding * 10f,
            "Leporazine" => GetDamageAmount(damageable, "Cold"),
            _ => 0f,
        };
    }

    private static float GetDamageAmount(DamageableComponent damageable, params string[] damageTypes)
    {
        var total = 0f;
        foreach (var type in damageTypes)
        {
            if (damageable.Damage.DamageDict.TryGetValue(type, out var amount) && amount > 0)
                total += amount.Float();
        }

        return total;
    }

    private void FinishPendingPlayerAction(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        string status)
    {
        var completedAction = rescue.PendingPlayerAction;
        var completedTarget = rescue.PendingPlayerActionTarget;
        var completedDispensedItem = rescue.PendingVendingDispensedItem;
        var completedStoredItem = rescue.PendingStorageTakenItem;
        var failed = status.StartsWith("failed", StringComparison.OrdinalIgnoreCase);
        var completedSupplyAction = completedAction is LuaMRescuePlayerActionKind.Pickup
            or LuaMRescuePlayerActionKind.Vend
            or LuaMRescuePlayerActionKind.TakeTargetStorage;
        ClearPendingPlayerAction(rescue, status);

        if (!failed &&
            completedAction is LuaMRescuePlayerActionKind.Pickup
                or LuaMRescuePlayerActionKind.StoreSlot
                or LuaMRescuePlayerActionKind.TakeStorage
                or LuaMRescuePlayerActionKind.Vend
                or LuaMRescuePlayerActionKind.TakeTargetStorage)
        {
            rescue.NextAutoTreatmentAttempt = _timing.CurTime;
        }

        if (failed &&
            completedTarget is { Valid: true } failedSupply &&
            completedAction is LuaMRescuePlayerActionKind.Pickup
                or LuaMRescuePlayerActionKind.Vend
                or LuaMRescuePlayerActionKind.TakeTargetStorage)
        {
            TemporarilySkipSupplyTarget(rescue, failedSupply, status);
        }

        if (completedSupplyAction &&
            rescue.TaskPatientTarget is { Valid: true } patient &&
            !Deleted(patient) &&
            !IsTargetTemporarilySkipped(uid, patient, rescue))
        {
            var collectedSupply = completedAction switch
            {
                LuaMRescuePlayerActionKind.Pickup => completedTarget,
                LuaMRescuePlayerActionKind.Vend => completedDispensedItem,
                LuaMRescuePlayerActionKind.TakeTargetStorage => completedStoredItem,
                _ => null,
            };

            if (!failed &&
                collectedSupply is { Valid: true } collectedSupplyUid &&
                TryAutoStoreCollectedMedicalSupply(uid, rescue, patient, collectedSupplyUid, out var storeStatus))
            {
                rescue.LastAutoSupplyStatus = storeStatus;
            }

            rescue.TargetRefreshAccumulator = rescue.TargetRefreshInterval;
            SetRescueTask(
                uid,
                rescue,
                LuaMRescueTaskStage.FollowingPatient,
                patient,
                null,
                failed
                    ? $"returning to patient {FormatEntityRef(patient)} after supply failure"
                    : $"returning to patient {FormatEntityRef(patient)} with supply");
            SetFollowTarget(uid, rescue, htn, patient);
            Dirty(uid, rescue);
            return;
        }

        StandbyAtAssignedShuttle(uid, rescue, htn);
        Dirty(uid, rescue);
    }

    private static bool RequiresPlayerActionTarget(LuaMRescuePlayerActionKind action)
    {
        return action is LuaMRescuePlayerActionKind.Interact
            or LuaMRescuePlayerActionKind.AltInteract
            or LuaMRescuePlayerActionKind.Treat
            or LuaMRescuePlayerActionKind.Vend
            or LuaMRescuePlayerActionKind.Pickup
            or LuaMRescuePlayerActionKind.Pull
            or LuaMRescuePlayerActionKind.Buckle
            or LuaMRescuePlayerActionKind.Unbuckle
            or LuaMRescuePlayerActionKind.TakeTargetStorage;
    }

    private static bool RequiresPlayerActionSlot(LuaMRescuePlayerActionKind action)
    {
        return action is LuaMRescuePlayerActionKind.EquipSlot
            or LuaMRescuePlayerActionKind.UnequipSlot
            or LuaMRescuePlayerActionKind.StoreSlot
            or LuaMRescuePlayerActionKind.TakeStorage;
    }

    private static string? NormalizeInventorySlot(string? slot)
    {
        if (string.IsNullOrWhiteSpace(slot))
            return null;

        var trimmed = slot.Trim();
        return trimmed.ToLowerInvariant() switch
        {
            "outer" or "outer-clothing" or "outer_clothing" or "outerclothing" or "suit" => "outerClothing",
            "suit-storage" or "suit_storage" or "suitstorage" => "suitstorage",
            "pocket-1" or "pocket_1" => "pocket1",
            "pocket-2" or "pocket_2" => "pocket2",
            "uniform" => "jumpsuit",
            "backpack" => "back",
            "idcard" or "id-card" or "id_card" => "id",
            "left-armband" or "left_armband" => "leftarmband",
            "right-armband" or "right_armband" => "rightarmband",
            "helmet-cover" or "helmet_cover" => "helmetcover",
            "helmet-attachment" or "helmet_attachment" => "helmetattachment",
            _ => trimmed,
        };
    }

    private static string? NormalizeItemSelector(string? itemSelector)
    {
        return string.IsNullOrWhiteSpace(itemSelector)
            ? null
            : itemSelector.Trim();
    }

    private static string FormatPlayerAction(LuaMRescuePlayerActionKind action)
    {
        return action switch
        {
            LuaMRescuePlayerActionKind.Interact => "interact",
            LuaMRescuePlayerActionKind.AltInteract => "alt-interact",
            LuaMRescuePlayerActionKind.Use => "use",
            LuaMRescuePlayerActionKind.Treat => "treat",
            LuaMRescuePlayerActionKind.Vend => "vend",
            LuaMRescuePlayerActionKind.Pickup => "pickup",
            LuaMRescuePlayerActionKind.Drop => "drop",
            LuaMRescuePlayerActionKind.Pull => "pull",
            LuaMRescuePlayerActionKind.StopPull => "stop-pull",
            LuaMRescuePlayerActionKind.Buckle => "buckle",
            LuaMRescuePlayerActionKind.Unbuckle => "unbuckle",
            LuaMRescuePlayerActionKind.EquipSlot => "equip-slot",
            LuaMRescuePlayerActionKind.UnequipSlot => "unequip-slot",
            LuaMRescuePlayerActionKind.StoreSlot => "store-slot",
            LuaMRescuePlayerActionKind.TakeStorage => "take-storage",
            LuaMRescuePlayerActionKind.TakeTargetStorage => "take-target-storage",
            _ => "none",
        };
    }

    private static void ClearPendingPlayerAction(LuaMRescueAgentComponent rescue, string? lastStatus = null)
    {
        rescue.PendingPlayerAction = LuaMRescuePlayerActionKind.None;
        rescue.PendingPlayerActionTarget = null;
        rescue.PendingPlayerActionSlot = null;
        rescue.PendingPlayerActionItem = null;
        rescue.PendingVendingStarted = false;
        rescue.PendingVendingProduct = null;
        rescue.PendingVendingDispensedItem = null;
        rescue.PendingStorageTakenItem = null;
        rescue.PlayerActionAccumulator = 0f;

        if (lastStatus != null)
            rescue.LastPlayerActionStatus = lastStatus;
    }

    private void UpdateAssignedTarget(EntityUid uid, LuaMRescueAgentComponent rescue, HTNComponent htn)
    {
        PruneManualOverrideTarget(uid, rescue);
        PruneSkippedTargets(rescue);
        PruneSkippedSupplyTargets(rescue);
        PruneSkippedDeliveryTargets(rescue);
        PrunePatientBuckleFailures(rescue);
        PruneAnalyzedTargets(rescue);
        PruneRescueTaskMemory(uid, rescue);
        ReconcileActivePatientIntentOwnership(uid, rescue, htn);
        TryReportDeathSignalDispatch(uid, rescue);
        UpdateTargetTrackingStatus(uid, rescue, htn);

        if (TryGetActivePatientTarget(rescue, out var activeTarget) &&
            !IsEligibleAssignedPatient(uid, activeTarget, rescue, out var ineligibleReason))
        {
            var hadRequiredHandoff = rescue.RequiredOnboardHandoffPatients.Contains(activeTarget);
            if (rescue.AssignedReturnTarget is { Valid: true } configuredHome &&
                !Deleted(configuredHome) &&
                hadRequiredHandoff &&
                IsBuckledToAssignedShuttlePatientStrap(activeTarget, rescue) &&
                TryContinueOnboardCare(uid, rescue, htn))
            {
                // A stable patient can be ineligible for ordinary acquisition
                // while still being physically owned by the home-handoff
                // contract. Let the onboard state machine retain or release
                // that custody; never downgrade it to failed evacuation.
                return;
            }

            var rejectedRequiredHandoff = hadRequiredHandoff && !Deleted(activeTarget);
            if (rejectedRequiredHandoff)
            {
                TryHoldRequiredHandoffCustody(
                    uid,
                    activeTarget,
                    rescue,
                    htn,
                    $"structural eligibility blocked: {ineligibleReason}",
                    ineligibleReason);
                if (!rescue.TerminalOnboardCareFailures.ContainsKey(activeTarget))
                {
                    rescue.TerminalOnboardCareFailures[activeTarget] =
                        $"required handoff structurally blocked: {ineligibleReason}";
                    RecordRescueHandoff(
                        uid,
                        rescue,
                        activeTarget,
                        "blocked-custody",
                        ineligibleReason.ToString(),
                        "required physical handoff retained until the structural blocker is repaired");
                }

                rescue.LastOnboardCareStatus =
                    $"onboard-care blocked handoff: patient={FormatEntityRef(activeTarget)}; " +
                    $"ownership=retained; reason={ineligibleReason}";
                SetTargetTrackingStatus(
                    uid,
                    rescue,
                    $"required_handoff_blocked: {FormatEntityRef(activeTarget)}; reason={ineligibleReason}");
                Dirty(uid, rescue);
                return;
            }

            if (hadRequiredHandoff)
                rescue.RequiredOnboardHandoffPatients.Remove(activeTarget);

            PrepareForExternalIntentReplacement(uid, EntityUid.Invalid);
            StopPullingTarget(uid, activeTarget);
            rescue.EvacuatingTarget = null;
            rescue.AssignedTarget = null;
            if (rescue.ManualOverrideTarget == activeTarget)
            {
                RevokeManualOverrideTarget(
                    uid,
                    rescue,
                    activeTarget,
                    $"patient became ineligible: {ineligibleReason}");
            }
            rescue.AssignedPatientStrap = null;
            ClearArrivalReportTarget(rescue, activeTarget);
            ClearTriageDecisionTarget(rescue, activeTarget);
            ClearDeathSignalTarget(rescue, activeTarget);
            ClearRescueTask(uid, rescue, $"ineligible patient {FormatEntityRef(activeTarget)}: {ineligibleReason}");
            ResetTargetProgress(rescue);
            _activity.Fail(uid, ineligibleReason, out _);
            ClearFollowTarget(uid, rescue, htn);
            SetTargetTrackingStatus(
                uid,
                rescue,
                $"target_rejected: {FormatEntityRef(activeTarget)}; reason={ineligibleReason}");
        }

        if (TryUpdateDormantRouteRecovery(uid, rescue, htn))
            return;

        if (rescue.OnboardCareTarget is { Valid: true } blockedOnboard &&
            rescue.IgnoredOnboardPatients.Contains(blockedOnboard) &&
            IsBuckledToAssignedShuttlePatientStrap(blockedOnboard, rescue))
        {
            // Terminal/manual-release custody still owns a physical bed. It is
            // intentionally ignored by treatment scans, but must be held before
            // priority preemption or automatic reacquisition can choose a second
            // patient and strand the occupied strap.
            CancelCurrentHtnMovement(uid, htn);
            ClearFollowTarget(uid, rescue, htn);
            SetRescueTask(
                uid,
                rescue,
                LuaMRescueTaskStage.DeliveringPatient,
                blockedOnboard,
                rescue.AssignedPatientStrap,
                $"holding terminal onboard custody for {FormatEntityRef(blockedOnboard)} until physical release");
            Dirty(uid, rescue);
            return;
        }

        if (rescue.AssignedTarget is { Valid: true } boardedTarget &&
            IsBuckledToAssignedShuttlePatientStrap(boardedTarget, rescue))
        {
            // Physical boarding is authoritative. Normalize even externally
            // buckled/manual assignments through the same handoff used by the
            // evacuation executor so an onboard patient can never retain a
            // FollowTarget owner or be preempted before care takes ownership.
            CompleteEvacuation(uid, rescue, htn, boardedTarget);
            return;
        }

        if (TryPreemptForHigherUrgencyPatient(uid, rescue, htn))
            return;

        if (UpdateEvacuation(uid, rescue, htn))
            return;

        if (!TryComp<MedibotComponent>(uid, out var medibot))
        {
            ClearFollowTarget(uid, rescue, htn);
            return;
        }

        if (TryResumeDeferredPatientTask(uid, rescue, htn))
            return;

        if (TryResumeManualOverrideTarget(uid, rescue, htn))
            return;

        if (TryFindPriorityRescueOverride(uid, rescue, medibot, out var priorityTarget))
        {
            if (!TryReplacePriorityPatientIntent(uid, rescue, htn, priorityTarget))
                return;

            if (TryTreatOrEvacuateTarget(uid, rescue, htn, priorityTarget))
                return;

            SetRescueTask(
                uid,
                rescue,
                LuaMRescueTaskStage.FollowingPatient,
                priorityTarget,
                null,
                $"rerouting to closer higher-acuity patient {FormatEntityRef(priorityTarget)}");
            SetFollowTarget(uid, rescue, htn, priorityTarget);
            return;
        }

        if (rescue.AssignedTarget is { Valid: true } assigned &&
            !IsTargetTemporarilySkipped(uid, assigned, rescue) &&
            !IsBuckledToAssignedShuttlePatientStrap(assigned, rescue))
        {
            if (TryTreatOrEvacuateTarget(uid, rescue, htn, assigned))
                return;

            // Coordinator/manual ownership is stronger than automatic
            // perception. A patient behind a wall may be temporarily unseen,
            // but the authoritative route probe must be allowed to terminalize
            // that assignment instead of silently dropping it into Standby.
            // Keep the deterministic same-urgency acuity/distance override
            // above this block so a better patient can still take ownership.
            SetRescueTask(
                uid,
                rescue,
                LuaMRescueTaskStage.FollowingPatient,
                assigned,
                null,
                $"following coordinator-assigned patient {FormatEntityRef(assigned)}");
            SetFollowTarget(uid, rescue, htn, assigned);
            return;
        }

        if (TryResumeRememberedPatientTask(uid, rescue, htn, medibot))
            return;

        if (rescue.AssignedTarget is { Valid: true } current &&
            !IsTargetTemporarilySkipped(uid, current, rescue) &&
            IsRescueCandidate(uid, current, medibot, requireRange: false, rescue.SearchRange, out _))
        {
            if (TryTreatOrEvacuateTarget(uid, rescue, htn, current))
                return;

            SetRescueTask(
                uid,
                rescue,
                LuaMRescueTaskStage.FollowingPatient,
                current,
                null,
                $"following current patient {FormatEntityRef(current)}");
            SetFollowTarget(uid, rescue, htn, current);
            return;
        }

        // Onboard scanners are subordinate to the coordinator's explicit
        // assigned/remembered intent. In particular, a low-priority patient on a
        // bed must not steal ownership back from a newly dispatched Critical.
        if (TryContinueOnboardCare(uid, rescue, htn))
            return;

        if (TryFindBestPatientTarget(uid, rescue.SearchRange, rescue, medibot, out var target))
        {
            var dead = TryComp<MobStateComponent>(target, out var selectedState) &&
                       selectedState.CurrentState == MobState.Dead;
            if (dead && TryAutoDefibTarget(uid, rescue, htn, target) ||
                TryTreatOrEvacuateTarget(uid, rescue, htn, target))
            {
                return;
            }

            SetRescueTask(
                uid,
                rescue,
                LuaMRescueTaskStage.FollowingPatient,
                target,
                null,
                $"following selected patient {FormatEntityRef(target)}");
            SetFollowTarget(uid, rescue, htn, target);
            return;
        }

        StandbyAtAssignedShuttle(uid, rescue, htn);
    }

    private bool TryContinueOnboardCare(EntityUid uid, LuaMRescueAgentComponent rescue, HTNComponent htn)
    {
        PruneIgnoredOnboardPatients(rescue);
        PruneStaleOnboardCareOwnership(uid, rescue, htn);
        return TryHandoffTerminalOnboardPatient(uid, rescue, htn) ||
               TryAutoDefibDeadPatientOnShuttle(uid, rescue, htn) ||
               TryTreatOnboardPatient(uid, rescue, htn) ||
               TryReleaseStabilizedPatientOnShuttle(uid, rescue, htn);
    }

    private bool TryTreatOrEvacuateTarget(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        EntityUid target)
    {
        TryReportPatientArrival(uid, rescue, target);
        TryReportTriageDecision(uid, rescue, target);

        // Defibrillation is a patient action, not a shuttle-only action. An
        // explicitly assigned or deferred recoverable patient must remain
        // executable even when no evacuation shuttle is available.
        if (TryHandleTerminalDefibrillationPatient(uid, rescue, htn, target) ||
            TryAutoDefibTarget(uid, rescue, htn, target))
        {
            return true;
        }

        if (ShouldEvacuateBeforeTreatment(uid, target, rescue))
        {
            return TryStartOrContinueEvacuation(uid, rescue, htn, target) ||
                   TryAutoTreatTarget(uid, rescue, htn, target);
        }

        return TryAutoTreatTarget(uid, rescue, htn, target) ||
               TryStartOrContinueEvacuation(uid, rescue, htn, target);
    }

    private bool ShouldEvacuateBeforeTreatment(
        EntityUid uid,
        EntityUid target,
        LuaMRescueAgentComponent rescue)
    {
        return IsThreatenedEvacuationTarget(uid, target, rescue);
    }

    private bool TryFindRescueTarget(
        EntityUid uid,
        float searchRange,
        LuaMRescueAgentComponent rescue,
        MedibotComponent medibot,
        out EntityUid target,
        EntityUid? excludedTarget = null)
    {
        target = default;
        var bestScore = float.MinValue;

        foreach (var candidate in _lookup.GetEntitiesInRange(uid, searchRange))
        {
            if (candidate == excludedTarget)
                continue;

            if (IsTargetTemporarilySkipped(uid, candidate, rescue) ||
                !CanPerceiveAutomaticTarget(uid, candidate, searchRange))
                continue;

            if (!IsRescueCandidate(uid, candidate, medibot, requireRange: true, searchRange, out var score))
                continue;

            if (!HasAutoAcquireAcuity(candidate, rescue))
                continue;

            if (score <= bestScore)
                continue;

            target = candidate;
            bestScore = score;
        }

        return target != default;
    }

    private bool TryFindBestPatientTarget(
        EntityUid uid,
        float searchRange,
        LuaMRescueAgentComponent rescue,
        MedibotComponent medibot,
        out EntityUid target)
    {
        target = default;
        var bestScore = float.MinValue;

        foreach (var candidate in _lookup.GetEntitiesInRange(uid, searchRange))
        {
            if (IsTargetTemporarilySkipped(uid, candidate, rescue) ||
                !CanPerceiveAutomaticTarget(uid, candidate, searchRange) ||
                !TryComp<MobStateComponent>(candidate, out var state) ||
                !TryGetDistance(uid, candidate, out var distance))
            {
                continue;
            }

            LuaMRescuePatientRequestKind kind;
            if (state.CurrentState == MobState.Dead)
            {
                kind = LuaMRescuePatientRequestKind.AutomaticEvacuation;
                var canDefibrillateLocally =
                    rescue.AutoDefibDeadPatients &&
                    !rescue.TerminalDefibrillationFailures.ContainsKey(candidate);
                if (canDefibrillateLocally)
                {
                    // Defibrillation is a patient action and does not require a
                    // shuttle. Keep evacuation eligibility (recoverable body,
                    // pullable target, faction/species policy), but do not gate
                    // this executable local recovery path on an assigned shuttle.
                    if (!_activity.IsEligibleRescuePatient(
                            uid,
                            candidate,
                            kind,
                            manualOverride: false,
                            out _) ||
                        !TryGetNavigationSelectionPenalty(
                            uid,
                            candidate,
                            rescue.PlayerActionRange,
                            out _))
                    {
                        continue;
                    }
                }
                else if (!IsEvacuationCandidate(uid, candidate, rescue, searchRange, out _))
                {
                    continue;
                }
            }
            else
            {
                kind = LuaMRescuePatientRequestKind.AutomaticTreatment;
                if (!IsRescueCandidate(uid, candidate, medibot, requireRange: true, searchRange, out _) ||
                    !HasAutoAcquireAcuity(candidate, rescue))
                {
                    continue;
                }
            }

            var priority = _activity.GetPatientPriority(
                uid,
                candidate,
                kind,
                manualOverride: false,
                out _);
            if (priority == int.MinValue)
                continue;

            var score = priority - distance;
            if (score < bestScore ||
                Math.Abs(score - bestScore) < 0.001f && target.Valid && candidate.Id >= target.Id)
            {
                continue;
            }

            target = candidate;
            bestScore = score;
        }

        return target.Valid;
    }

    private bool TryFindPriorityRescueOverride(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        MedibotComponent medibot,
        out EntityUid target)
    {
        target = default;

        if (!TryGetActivePatientTarget(rescue, out var current) ||
            Deleted(current) ||
            rescue.RequiredOnboardHandoffPatients.Contains(current) ||
            IsTargetTemporarilySkipped(uid, current, rescue) ||
            !TryGetRescueTargetPriority(
                uid,
                current,
                rescue,
                medibot,
                requireRange: false,
                rescue.SearchRange,
                out var currentPriority,
                out var currentDistance))
        {
            return false;
        }

        var currentUrgency = _activity.GetPatientUrgency(current);
        var bestScore = double.MinValue;
        foreach (var candidate in _lookup.GetEntitiesInRange(uid, rescue.SearchRange))
        {
            if (candidate == current ||
                IsTargetTemporarilySkipped(uid, candidate, rescue) ||
                !CanPerceiveAutomaticTarget(uid, candidate, rescue.SearchRange))
            {
                continue;
            }

            if (!TryGetRescueTargetPriority(
                    uid,
                    candidate,
                    rescue,
                    medibot,
                    requireRange: true,
                    rescue.SearchRange,
                    out var candidatePriority,
                    out var candidateDistance))
            {
                continue;
            }

            var candidateUrgency = _activity.GetPatientUrgency(candidate);
            var requiredPriorityAdvantage = candidateUrgency == currentUrgency
                ? SameUrgencyPreemptionPriorityMargin
                : 1;
            if (candidatePriority - currentPriority < requiredPriorityAdvantage ||
                candidateDistance >= currentDistance)
            {
                continue;
            }

            // Use double precision: priorities are around 100k-600k, where a
            // float score multiplied by 1000 loses sub-tile distance ordering.
            var score = candidatePriority * 1000d - candidateDistance;
            if (score < bestScore ||
                Math.Abs(score - bestScore) < 0.001d && target.Valid && candidate.Id >= target.Id)
                continue;

            target = candidate;
            bestScore = score;
        }

        return target != default;
    }

    /// <summary>
    /// Atomically transfers the sole patient intent before any treatment or
    /// evacuation code can run. In particular, an in-range replacement may
    /// start a medical DoAfter without ever calling SetFollowTarget, so leaving
    /// the old FollowTarget/AssignedTarget in place would keep the stale MoveTo
    /// alive beside the new coordinator activity.
    /// </summary>
    private bool TryReplacePriorityPatientIntent(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        EntityUid replacement)
    {
        if (!TryGetActivePatientTarget(rescue, out var displaced) ||
            displaced == replacement ||
            Deleted(displaced) ||
            Deleted(replacement))
        {
            return false;
        }

        DeferPatientDisplacedByUrgency(uid, rescue, displaced, replacement);
        PrepareForExternalIntentReplacement(uid, replacement);
        StopPullingTarget(uid, displaced);

        // PrepareForExternalIntentReplacement shuts the plan and steering down,
        // but FollowTarget is persistent blackboard state. Remove it before the
        // new action starts so an immediate treatment cannot inherit the old
        // patient's movement owner.
        htn.Blackboard.Remove<EntityCoordinates>(NPCBlackboard.FollowTarget);

        rescue.EvacuatingTarget = null;
        rescue.OnboardCareTarget = null;
        rescue.AssignedPatientStrap = null;
        ClearDeathSignalTarget(rescue, displaced);
        if (rescue.ShuttleRoutedTarget == displaced)
            rescue.ShuttleRoutedTarget = null;
        rescue.AssignedTarget = replacement;
        rescue.SkippedTargets.Remove(replacement);
        rescue.DeferredPatientTargets.Remove(replacement);
        ResetTargetProgress(rescue);
        SetRescueTask(
            uid,
            rescue,
            LuaMRescueTaskStage.FollowingPatient,
            replacement,
            null,
            $"preempting {FormatEntityRef(displaced)} for closer higher-acuity {FormatEntityRef(replacement)}");

        var entryActivity = GetPriorityPatientEntryActivity(uid, rescue, replacement);
        if (BeginPatientActivity(
                uid,
                rescue,
                entryActivity,
                replacement,
                new EntityCoordinates(replacement, Vector2.Zero)))
        {
            return true;
        }

        rescue.LastTargetTrackingStatus =
            $"priority replacement failed: target={FormatEntityRef(replacement)}; " +
            $"reason={rescue.ActivityContext.FailureReason}";
        Dirty(uid, rescue);
        return false;
    }

    private LuaMRescueActivity GetPriorityPatientEntryActivity(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        EntityUid target)
    {
        if (ShouldEvacuateBeforeTreatment(uid, target, rescue))
            return LuaMRescueActivity.PrepareEvacuation;

        if (rescue.AutoTreatWithCarriedItems &&
            IsWithinRange(uid, target, rescue.PlayerActionRange) &&
            TryComp<MobStateComponent>(target, out var mobState) &&
            mobState.CurrentState != MobState.Dead &&
            TryComp<DamageableComponent>(target, out var damageable) &&
            damageable.TotalDamage.Float() >= rescue.AutoTreatMinDamage)
        {
            return LuaMRescueActivity.TreatPatient;
        }

        return LuaMRescueActivity.ApproachPatient;
    }

    private bool TryGetActivePatientTarget(
        LuaMRescueAgentComponent rescue,
        out EntityUid target,
        bool includeManualOverride = true)
    {
        if (rescue.LifeSupportEmergencyActive &&
            rescue.LifeSupportEmergencyPatient is { Valid: true } emergencyPatient)
        {
            target = emergencyPatient;
            return true;
        }

        // ActivityContext is the authoritative owner; the fields below are
        // compatibility mirrors and can already have been cleared by another
        // system on this tick. Restrict the fallback to actual medical bodies so
        // PlanningRoute/Returning targets such as a shuttle grid never become a
        // synthetic patient.
        if (rescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active &&
            rescue.ActivityContext.Target is { Valid: true } activityTarget &&
            !Deleted(activityTarget) &&
            HasComp<MobStateComponent>(activityTarget) &&
            HasComp<DamageableComponent>(activityTarget))
        {
            target = activityTarget;
            return true;
        }

        if (rescue.EvacuatingTarget is { Valid: true } evacuating)
        {
            target = evacuating;
            return true;
        }

        if (rescue.OnboardCareTarget is { Valid: true } onboard)
        {
            target = onboard;
            return true;
        }

        if (rescue.AssignedTarget is { Valid: true } assigned)
        {
            target = assigned;
            return true;
        }

        if (rescue.DeathSignalTarget is { Valid: true } deathSignal)
        {
            target = deathSignal;
            return true;
        }

        if (rescue.TaskPatientTarget is { Valid: true } remembered)
        {
            target = remembered;
            return true;
        }

        if (includeManualOverride &&
            rescue.ManualOverrideTarget is { Valid: true } manual)
        {
            target = manual;
            return true;
        }

        target = default;
        return false;
    }

    private bool IsEligibleAssignedPatient(
        EntityUid rescuer,
        EntityUid target,
        LuaMRescueAgentComponent rescue,
        out LuaMRescueFailureReason failureReason)
    {
        // Once a patient physically occupies a bed on the assigned shuttle, the
        // onboard-care lifecycle is the sole owner of their eligibility and
        // release. The generic target loop must not clear that ownership merely
        // because the patient became healthy (or otherwise terminal) before the
        // bounded handoff path has actually unbuckled them.
        if (IsBuckledToAssignedShuttlePatientStrap(target, rescue))
        {
            failureReason = LuaMRescueFailureReason.None;
            return true;
        }

        var requiredHandoff = rescue.RequiredOnboardHandoffPatients.Contains(target);
        if (requiredHandoff && !HasComp<PullableComponent>(target))
        {
            failureReason = LuaMRescueFailureReason.TargetNotPullable;
            return false;
        }

        if (requiredHandoff && !HasComp<BuckleComponent>(target))
        {
            failureReason = LuaMRescueFailureReason.InvalidPatient;
            return false;
        }

        var manualOverride = IsManualPatientIntent(rescue, target);
        var kind = manualOverride
            ? LuaMRescuePatientRequestKind.Manual
            : TryComp<MobStateComponent>(target, out var mobState) &&
              mobState.CurrentState == MobState.Dead
                ? LuaMRescuePatientRequestKind.AutomaticEvacuation
                : LuaMRescuePatientRequestKind.AutomaticTreatment;
        var eligible = _activity.IsEligibleRescuePatient(
            rescuer,
            target,
            kind,
            manualOverride,
            out failureReason);
        return eligible ||
               requiredHandoff &&
               IsAcceptedRequiredHandoffMedicalFailure(failureReason);
    }

    private void PruneDeletedRequiredHandoffPatients(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn)
    {
        foreach (var target in rescue.RequiredOnboardHandoffPatients.ToArray())
        {
            if (!Deleted(target))
                continue;

            PurgePatientState(
                uid,
                rescue,
                htn,
                target,
                "deleted required handoff fallback prune");
        }
    }

    private static bool IsAcceptedRequiredHandoffMedicalFailure(LuaMRescueFailureReason failure)
    {
        // Required handoff is physical custody, not a new treatment request.
        // Medical impossibility must therefore fall through to bounded
        // evacuation/transport, while structural failures remain rejected.
        return failure is LuaMRescueFailureReason.TargetHealthy or
               LuaMRescueFailureReason.Unrevivable or
               LuaMRescueFailureReason.TargetHasNoMind or
               LuaMRescueFailureReason.TargetNotInjectable;
    }

    private static bool IsManualPatientIntent(LuaMRescueAgentComponent rescue, EntityUid target)
    {
        return rescue.ManualOverrideTarget == target ||
               rescue.PendingPlayerAction != LuaMRescuePlayerActionKind.None &&
               rescue.PendingPlayerActionTarget == target;
    }

    private void PruneManualOverrideTarget(EntityUid uid, LuaMRescueAgentComponent rescue)
    {
        if (rescue.ManualOverrideTarget is not { Valid: true } manualTarget)
        {
            rescue.ManualOverrideTarget = null;
            return;
        }

        if (Deleted(manualTarget))
            RevokeManualOverrideTarget(uid, rescue, manualTarget, "manual patient deleted");
    }

    private void RevokeManualOverrideTarget(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        EntityUid? expectedTarget,
        string reason)
    {
        if (rescue.ManualOverrideTarget is not { Valid: true } manualTarget)
        {
            rescue.ManualOverrideTarget = null;
            return;
        }

        if (expectedTarget is { Valid: true } expected && manualTarget != expected)
            return;

        var revokedGeneration = rescue.ManualOverrideGeneration;
        rescue.ManualOverrideTarget = null;
        rescue.ManualOverrideGeneration = NextManualOverrideGeneration(rescue.ManualOverrideGeneration);
        var revoked = new LuaMRescueManualOverrideRevokedEvent(manualTarget, revokedGeneration, reason);
        RaiseLocalEvent(uid, ref revoked);
        Dirty(uid, rescue);
    }

    /// <summary>
    /// Establishes a fresh explicit-order lineage. Returning the existing
    /// generation for the same target lets a displaced manual order be
    /// reconciled without manufacturing a new owner or resetting recovery.
    /// </summary>
    public uint EstablishManualOverrideTarget(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        EntityUid target)
    {
        if (!target.Valid)
            return rescue.ManualOverrideGeneration;

        if (rescue.ManualOverrideTarget == target && rescue.ManualOverrideGeneration != 0)
            return rescue.ManualOverrideGeneration;

        if (rescue.ManualOverrideTarget is { Valid: true })
            RevokeManualOverrideTarget(uid, rescue, null, "manual patient ownership replaced");

        rescue.ManualOverrideGeneration = NextManualOverrideGeneration(rescue.ManualOverrideGeneration);
        rescue.ManualOverrideTarget = target;
        Dirty(uid, rescue);
        return rescue.ManualOverrideGeneration;
    }

    private static uint NextManualOverrideGeneration(uint generation)
    {
        return generation == uint.MaxValue ? 1u : generation + 1u;
    }

    private bool TryPreemptForHigherUrgencyPatient(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn)
    {
        if (!TryGetActivePatientTarget(rescue, out var current) || Deleted(current))
            return false;

        if (rescue.RequiredOnboardHandoffPatients.Contains(current))
            return false;

        var currentUrgency = _activity.GetPatientUrgency(current);
        if (currentUrgency == LuaMRescuePatientUrgency.Critical)
            return false;

        EntityUid best = default;
        var bestUrgency = currentUrgency;
        foreach (var candidate in _lookup.GetEntitiesInRange(uid, rescue.SearchRange))
        {
            if (candidate == current ||
                IsTargetTemporarilySkipped(uid, candidate, rescue) ||
                !CanPerceiveAutomaticTarget(uid, candidate, rescue.SearchRange) ||
                !_activity.IsEligibleRescuePatient(
                    uid,
                    candidate,
                    LuaMRescuePatientRequestKind.AutomaticTreatment,
                    manualOverride: false,
                    out _) ||
                !TryGetNavigationSelectionPenalty(uid, candidate, rescue.PlayerActionRange, out _))
            {
                continue;
            }

            var urgency = _activity.GetPatientUrgency(candidate);
            if (urgency <= bestUrgency || urgency < LuaMRescuePatientUrgency.Severe)
                continue;

            best = candidate;
            bestUrgency = urgency;
        }

        if (!best.Valid)
            return false;

        DeferPatientDisplacedByUrgency(uid, rescue, current, best);
        PrepareForExternalIntentReplacement(uid, best);
        StopPullingTarget(uid, current);
        rescue.EvacuatingTarget = null;
        rescue.OnboardCareTarget = null;
        rescue.AssignedPatientStrap = null;
        rescue.AssignedTarget = best;
        if (rescue.DeathSignalTarget == current)
            rescue.DeathSignalTarget = null;
        rescue.SkippedTargets.Remove(best);
        rescue.DeferredPatientTargets.Remove(best);
        ResetTargetProgress(rescue);
        SetRescueTask(
            uid,
            rescue,
            LuaMRescueTaskStage.FollowingPatient,
            best,
            null,
            $"preempting {FormatEntityRef(current)} for higher urgency {FormatEntityRef(best)}");
        SetFollowTarget(uid, rescue, htn, best);
        return true;
    }

    private void DeferPatientDisplacedByUrgency(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        EntityUid displaced,
        EntityUid replacement)
    {
        var manualOverride = rescue.ManualOverrideTarget == displaced;
        var kind = manualOverride
            ? LuaMRescuePatientRequestKind.Manual
            : TryComp<MobStateComponent>(displaced, out var state) &&
              state.CurrentState == MobState.Dead
                ? LuaMRescuePatientRequestKind.AutomaticEvacuation
                : LuaMRescuePatientRequestKind.AutomaticTreatment;
        if (!_activity.IsEligibleRescuePatient(
                uid,
                displaced,
                kind,
                manualOverride,
                out var failureReason))
        {
            rescue.LastDeferredPatientStatus =
                $"not deferred {FormatEntityRef(displaced)}: {failureReason}";
            return;
        }

        rescue.DeferredPatientTargets.TryAdd(displaced, _timing.CurTime);
        rescue.LastDeferredPatientStatus =
            $"deferred {FormatEntityRef(displaced)} for higher urgency {FormatEntityRef(replacement)}";
        Dirty(uid, rescue);
    }

    private void DeferPatientInterruptedByAgentState(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        MobState agentState)
    {
        var interrupted = rescue.EvacuatingTarget ??
                          rescue.AssignedTarget ??
                          rescue.DeathSignalTarget ??
                          rescue.TaskPatientTarget ??
                          (rescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active
                              ? rescue.ActivityContext.Target
                              : null);
        if (interrupted is not { Valid: true } target ||
            Deleted(target) ||
            rescue.OnboardCareTarget == target ||
            rescue.ManualOverrideTarget == target)
        {
            return;
        }

        var requestKind = TryComp<MobStateComponent>(target, out var targetState) &&
                          targetState.CurrentState == MobState.Dead
            ? LuaMRescuePatientRequestKind.AutomaticEvacuation
            : LuaMRescuePatientRequestKind.AutomaticTreatment;
        if (!_activity.IsEligibleRescuePatient(
                uid,
                target,
                requestKind,
                manualOverride: false,
                out var failureReason))
        {
            rescue.LastDeferredPatientStatus =
                $"not deferred after self-{agentState}: {FormatEntityRef(target)}; {failureReason}";
            return;
        }

        rescue.DeferredPatientTargets.TryAdd(target, _timing.CurTime);
        rescue.LastDeferredPatientStatus =
            $"deferred {FormatEntityRef(target)} while rescue agent was {agentState}";
        if (rescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active &&
            rescue.ActivityContext.Target == target)
        {
            _activity.Cancel(
                uid,
                rescue.ActivityContext.Generation,
                LuaMRescueFailureReason.Cancelled,
                out _);
        }
    }

    private bool TryResumeDeferredPatientTask(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        EntityUid? preferredTarget = null)
    {
        if (rescue.DeferredPatientTargets.Count == 0 ||
            TryGetActivePatientTarget(rescue, out _) ||
            rescue.OnboardCareTarget is { Valid: true } ||
            rescue.DeathSignalTarget is { Valid: true })
        {
            return false;
        }

        EntityUid selected = default;
        var selectedPriority = int.MinValue;
        var selectedEnqueuedAt = TimeSpan.MaxValue;
        foreach (var (candidate, enqueuedAt) in rescue.DeferredPatientTargets.ToArray())
        {
            if (!candidate.Valid || Deleted(candidate))
            {
                rescue.DeferredPatientTargets.Remove(candidate);
                continue;
            }

            if (IsTargetTemporarilySkipped(uid, candidate, rescue))
                continue;

            var manualOverride = rescue.ManualOverrideTarget == candidate;
            var kind = manualOverride
                ? LuaMRescuePatientRequestKind.Manual
                : TryComp<MobStateComponent>(candidate, out var state) &&
                  state.CurrentState == MobState.Dead
                    ? LuaMRescuePatientRequestKind.AutomaticEvacuation
                    : LuaMRescuePatientRequestKind.AutomaticTreatment;
            var priority = _activity.GetPatientPriority(
                uid,
                candidate,
                kind,
                manualOverride,
                out var failureReason);
            if (priority == int.MinValue)
            {
                rescue.DeferredPatientTargets.Remove(candidate);
                rescue.LastDeferredPatientStatus =
                    $"discarded deferred {FormatEntityRef(candidate)}: {failureReason}";
                continue;
            }

            if (candidate == preferredTarget)
            {
                selected = candidate;
                selectedPriority = priority;
                selectedEnqueuedAt = enqueuedAt;
                break;
            }

            if (priority < selectedPriority ||
                priority == selectedPriority &&
                (enqueuedAt > selectedEnqueuedAt ||
                 enqueuedAt == selectedEnqueuedAt && selected.Valid && candidate.Id >= selected.Id))
            {
                continue;
            }

            selected = candidate;
            selectedPriority = priority;
            selectedEnqueuedAt = enqueuedAt;
        }

        if (!selected.Valid)
            return false;

        if (!BeginPatientActivity(
                uid,
                rescue,
                LuaMRescueActivity.ApproachPatient,
                selected,
                new EntityCoordinates(selected, Vector2.Zero)))
        {
            return false;
        }

        rescue.DeferredPatientTargets.Remove(selected);
        rescue.LastDeferredPatientStatus = selected == preferredTarget
            ? $"resuming deferred exact interrupted patient {FormatEntityRef(selected)}"
            : $"resuming deferred {FormatEntityRef(selected)}";
        rescue.SkippedTargets.Remove(selected);
        SetRescueTask(
            uid,
            rescue,
            LuaMRescueTaskStage.FollowingPatient,
            selected,
            null,
            rescue.LastDeferredPatientStatus);
        SetFollowTarget(uid, rescue, htn, selected);
        Dirty(uid, rescue);
        return true;
    }

    /// <summary>
    /// A temporary route skip clears the active compatibility mirrors, but it
    /// must not erase an explicit manual assignment. Once the skip expires,
    /// restore that target through the normal route executor so its bounded
    /// failure budget can advance to a durable or dormant disposition.
    /// </summary>
    private bool TryResumeManualOverrideTarget(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn)
    {
        if (rescue.ManualOverrideTarget is not { Valid: true } target ||
            Deleted(target) ||
            rescue.PendingPlayerAction != LuaMRescuePlayerActionKind.None ||
            TryGetActivePatientTarget(rescue, out _, includeManualOverride: false) ||
            rescue.OnboardCareTarget is { Valid: true } ||
            rescue.DeathSignalTarget is { Valid: true } ||
            rescue.DormantRouteTarget == target ||
            IsTargetTemporarilySkipped(uid, target, rescue))
        {
            return false;
        }

        if (!_activity.IsEligibleRescuePatient(
                uid,
                target,
                LuaMRescuePatientRequestKind.Manual,
                manualOverride: true,
                out var failureReason))
        {
            RevokeManualOverrideTarget(
                uid,
                rescue,
                target,
                $"manual patient became ineligible: {failureReason}");
            rescue.LastDeferredPatientStatus =
                $"discarded manual override {FormatEntityRef(target)}: {failureReason}";
            Dirty(uid, rescue);
            return false;
        }

        rescue.LastDeferredPatientStatus = $"resuming manual override {FormatEntityRef(target)}";
        SetRescueTask(
            uid,
            rescue,
            LuaMRescueTaskStage.FollowingPatient,
            target,
            null,
            rescue.LastDeferredPatientStatus);
        SetFollowTarget(uid, rescue, htn, target);
        Dirty(uid, rescue);
        return true;
    }

    /// <summary>
    /// ActivityContext owns the active generation. The older assignment/task
    /// fields are compatibility mirrors used by selection and status code; a
    /// stale mirror must be repaired before it can trigger a second replacement
    /// of the intent the coordinator already owns.
    /// </summary>
    private void ReconcileActivePatientIntentOwnership(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn)
    {
        if (rescue.ActivityContext.TerminalStatus != LuaMRescueTerminalStatus.Active ||
            rescue.ActivityContext.Target is not { Valid: true } authoritativeTarget ||
            Deleted(authoritativeTarget))
        {
            return;
        }

        // Returning owns a navigation destination, not a patient. During a
        // life-support retreat the interrupted patient remains in the dedicated
        // emergency owner while FollowTarget must stay on the route destination.
        if (rescue.LifeSupportEmergencyActive &&
            (rescue.ActivityContext.Activity == LuaMRescueActivity.Returning ||
             authoritativeTarget == rescue.AssignedShuttle ||
             authoritativeTarget == rescue.AssignedShuttleAnchor))
        {
            if (rescue.AssignedTarget == authoritativeTarget)
            {
                rescue.AssignedTarget = null;
                Dirty(uid, rescue);
            }

            if (rescue.TaskPatientTarget == authoritativeTarget)
            {
                rescue.TaskPatientTarget = null;
                Dirty(uid, rescue);
            }

            return;
        }

        var activityOwnsEvacuation = rescue.ActivityContext.Activity is
            LuaMRescueActivity.PreparingEvacuation or
            LuaMRescueActivity.Pulling or
            LuaMRescueActivity.Delivering or
            LuaMRescueActivity.BucklePatient;
        var activityOwnsOnboardCustody = rescue.ActivityContext.Activity is
            LuaMRescueActivity.OnboardCare or
            LuaMRescueActivity.Handoff;

        if (rescue.EvacuatingTarget is { Valid: true } evacuatingMirror &&
            (!activityOwnsEvacuation || evacuatingMirror != authoritativeTarget))
        {
            if (!rescue.RequiredOnboardHandoffPatients.Contains(evacuatingMirror))
                StopPullingTarget(uid, evacuatingMirror);
            _rescueNavigation.CancelRoute(uid, evacuatingMirror);
            rescue.EvacuatingTarget = null;
            Dirty(uid, rescue);
        }

        if (rescue.OnboardCareTarget is { Valid: true } onboardMirror &&
            (!activityOwnsOnboardCustody || onboardMirror != authoritativeTarget))
        {
            // RequiredOnboardHandoffPatients remains the physical custody
            // authority. Remove only this stale selection mirror so it cannot
            // outrank the active generation during TryGetActivePatientTarget.
            rescue.OnboardCareTarget = null;
            Dirty(uid, rescue);
        }

        if (rescue.DeathSignalTarget is { Valid: true } deathSignalMirror &&
            deathSignalMirror != authoritativeTarget)
        {
            ClearDeathSignalTarget(rescue, deathSignalMirror);
            Dirty(uid, rescue);
        }

        if (rescue.ShuttleRoutedTarget is { Valid: true } shuttleRoutedMirror &&
            shuttleRoutedMirror != authoritativeTarget)
        {
            rescue.ShuttleRoutedTarget = null;
            Dirty(uid, rescue);
        }

        if (!activityOwnsEvacuation &&
            !activityOwnsOnboardCustody &&
            rescue.AssignedPatientStrap != null &&
            !IsBuckledToAssignedShuttlePatientStrap(authoritativeTarget, rescue))
        {
            rescue.AssignedPatientStrap = null;
            Dirty(uid, rescue);
        }

        if (rescue.TaskPatientTarget != authoritativeTarget)
        {
            if (rescue.TaskPatientTarget is { Valid: true } staleTaskTarget &&
                staleTaskTarget != authoritativeTarget &&
                !rescue.RequiredOnboardHandoffPatients.Contains(staleTaskTarget))
            {
                StopPullingTarget(uid, staleTaskTarget);
                _rescueNavigation.CancelRoute(uid, staleTaskTarget);
            }

            var repairedStage = rescue.TaskStage is LuaMRescueTaskStage.None or LuaMRescueTaskStage.Standby
                ? LuaMRescueTaskStage.FollowingPatient
                : rescue.TaskStage;
            SetRescueTask(
                uid,
                rescue,
                repairedStage,
                authoritativeTarget,
                rescue.TaskSupplyTarget,
                $"reconciled task owner to active generation target {FormatEntityRef(authoritativeTarget)}");
        }

        if (IsBuckledToAssignedShuttlePatientStrap(authoritativeTarget, rescue) &&
            rescue.AssignedTarget != authoritativeTarget)
        {
            // OnboardCare/Handoff owns a physically boarded patient without a
            // follow mirror. Preserve an existing matching assignment just long
            // enough for UpdateAssignedTarget to normalize an externally
            // buckled patient through CompleteEvacuation, but never synthesize
            // that mirror again on subsequent onboard refreshes.
            if (rescue.AssignedTarget is { Valid: true } staleOnboardMirror)
            {
                StopPullingTarget(uid, staleOnboardMirror);
                _rescueNavigation.CancelRoute(uid, staleOnboardMirror);
                if (htn.Blackboard.TryGetValue<EntityCoordinates>(
                        NPCBlackboard.FollowTarget,
                        out var onboardFollow,
                        EntityManager) &&
                    onboardFollow.EntityId == staleOnboardMirror)
                {
                    CancelCurrentHtnMovement(uid, htn, cancelRouteProbe: false);
                    htn.Blackboard.Remove<EntityCoordinates>(NPCBlackboard.FollowTarget);
                }

                rescue.AssignedTarget = null;
                Dirty(uid, rescue);
            }
            else if (rescue.AssignedTarget != null)
            {
                rescue.AssignedTarget = null;
                Dirty(uid, rescue);
            }

            return;
        }

        var staleTarget = rescue.AssignedTarget;
        if (staleTarget is { Valid: true } stale && stale != authoritativeTarget)
        {
            if (!rescue.RequiredOnboardHandoffPatients.Contains(stale))
                StopPullingTarget(uid, stale);
            _rescueNavigation.CancelRoute(uid, stale);
        }

        if (htn.Blackboard.TryGetValue<EntityCoordinates>(
                NPCBlackboard.FollowTarget,
                out var follow,
                EntityManager) &&
            follow.EntityId != authoritativeTarget)
        {
            CancelCurrentHtnMovement(uid, htn, cancelRouteProbe: false);
            htn.Blackboard.Remove<EntityCoordinates>(NPCBlackboard.FollowTarget);
        }

        if (rescue.AssignedTarget != authoritativeTarget)
        {
            rescue.AssignedTarget = authoritativeTarget;
            Dirty(uid, rescue);
        }
    }

    /// <summary>
    /// A terminal generation is an immutable diagnostic record, not permission
    /// to keep steering, routing or pulling its patient. Physical onboard
    /// custody is the sole exception: a patient already buckled to the assigned
    /// shuttle, or explicitly awaiting handoff, remains owned by the durable
    /// handoff set until that path performs the real release.
    /// </summary>
    private void ReconcileTerminalPatientIntentOwnership(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn)
    {
        if (rescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active ||
            rescue.ActivityContext.Target is not { Valid: true } terminalTarget ||
            Deleted(terminalTarget))
        {
            return;
        }

        // Cancelling the patient generation is how a life-support retreat
        // releases its old steering. The dedicated emergency owner keeps that
        // exact mission alive until breathing is restored, so terminal cleanup
        // must not revoke a manual lineage (or otherwise reinterpret it as a
        // completed patient episode) while the retreat is authoritative.
        if (rescue.LifeSupportEmergencyActive &&
            rescue.LifeSupportEmergencyPatient == terminalTarget)
        {
            return;
        }

        var buckledOnboard = IsBuckledToAssignedShuttlePatientStrap(terminalTarget, rescue);
        var promotedOnboardCustody = buckledOnboard &&
                                     rescue.RequiredOnboardHandoffPatients.Add(terminalTarget);

        var retainsPhysicalCustody = buckledOnboard ||
                                     rescue.RequiredOnboardHandoffPatients.Contains(terminalTarget);
        var followsTerminalTarget = htn.Blackboard.TryGetValue<EntityCoordinates>(
                                        NPCBlackboard.FollowTarget,
                                        out var follow,
                                        EntityManager) &&
                                    follow.EntityId == terminalTarget;
        var physicallyPullsTerminalTarget = TryComp<PullerComponent>(uid, out var puller) &&
                                            puller.Pulling == terminalTarget;
        var hasStaleOwnership = rescue.AssignedTarget == terminalTarget ||
                                rescue.ManualOverrideTarget == terminalTarget ||
                                rescue.EvacuatingTarget == terminalTarget ||
                                rescue.OnboardCareTarget == terminalTarget ||
                                rescue.DeathSignalTarget == terminalTarget ||
                                rescue.ShuttleRoutedTarget == terminalTarget ||
                                rescue.TaskPatientTarget == terminalTarget ||
                                rescue.ProgressTarget == terminalTarget ||
                                rescue.RouteBlockHoldTarget == terminalTarget ||
                                (rescue.AssignedPatientStrap != null && !buckledOnboard) ||
                                followsTerminalTarget ||
                                physicallyPullsTerminalTarget && !retainsPhysicalCustody;
        if (!promotedOnboardCustody && !hasStaleOwnership)
            return;

        _rescueNavigation.CancelRoute(uid, terminalTarget);
        if (!retainsPhysicalCustody)
            StopPullingTarget(uid, terminalTarget);

        if (followsTerminalTarget)
            ClearFollowTarget(uid, rescue, htn);

        if (rescue.AssignedTarget == terminalTarget)
            rescue.AssignedTarget = null;
        if (rescue.ManualOverrideTarget == terminalTarget)
            RevokeManualOverrideTarget(uid, rescue, terminalTarget, "terminal generation released explicit override");
        if (rescue.EvacuatingTarget == terminalTarget)
            rescue.EvacuatingTarget = null;
        if (rescue.OnboardCareTarget == terminalTarget)
            rescue.OnboardCareTarget = null;
        if (rescue.DeathSignalTarget == terminalTarget)
            ClearDeathSignalTarget(rescue, terminalTarget);
        if (rescue.ShuttleRoutedTarget == terminalTarget)
            rescue.ShuttleRoutedTarget = null;
        if (!buckledOnboard && rescue.AssignedPatientStrap != null)
            rescue.AssignedPatientStrap = null;

        if (rescue.TaskPatientTarget == terminalTarget)
            ClearRescueTask(uid, rescue, $"terminal generation released {FormatEntityRef(terminalTarget)} movement ownership");

        if (rescue.ProgressTarget == terminalTarget ||
            rescue.RouteBlockHoldTarget == terminalTarget)
        {
            ResetTargetProgress(rescue);
        }

        if (rescue.TerminalDefibrillationFailures.TryGetValue(terminalTarget, out var terminalDefib))
            rescue.LastAutoDefibStatus = terminalDefib;

        Dirty(uid, rescue);
    }

    private void UpdateTargetTrackingStatus(EntityUid uid, LuaMRescueAgentComponent rescue, HTNComponent htn)
    {
        if (!TryGetActivePatientTarget(rescue, out var target))
        {
            SetTargetTrackingStatus(uid, rescue, "target_tracking: none");
            return;
        }

        if (Deleted(target))
        {
            if (rescue.EvacuatingTarget == target)
                rescue.EvacuatingTarget = null;

            if (rescue.AssignedTarget == target)
                ClearFollowTarget(uid, rescue, htn);

            ClearArrivalReportTarget(rescue, target);
            ClearTriageDecisionTarget(rescue, target);
            ClearDeathSignalTarget(rescue, target);
            ResetTargetProgress(rescue);
            SetTargetTrackingStatus(uid, rescue, $"target_lost: {FormatEntityRef(target)}");
            return;
        }

        if (!TryGetDistance(uid, target, out var distance))
        {
            SetTargetTrackingStatus(uid, rescue, $"target_lost: {FormatEntityRef(target)}; no shared route distance");
            return;
        }

        if (distance > rescue.SearchRange)
        {
            SetTargetTrackingStatus(uid, rescue, $"target_moved: {FormatEntityRef(target)} outside search range");
            return;
        }

        if (rescue.ProgressTarget == target &&
            rescue.LastTargetTrackingStatus.StartsWith("route_", StringComparison.Ordinal))
        {
            return;
        }

        SetTargetTrackingStatus(uid, rescue, $"target_tracking: {FormatEntityRef(target)} in range");
    }

    private void SetTargetTrackingStatus(EntityUid uid, LuaMRescueAgentComponent rescue, string status)
    {
        if (string.Equals(rescue.LastTargetTrackingStatus, status, StringComparison.Ordinal))
            return;

        rescue.LastTargetTrackingStatus = status;
        Dirty(uid, rescue);
    }

    private bool TryGetRescueTargetPriority(
        EntityUid uid,
        EntityUid target,
        LuaMRescueAgentComponent rescue,
        MedibotComponent medibot,
        bool requireRange,
        float searchRange,
        out int priority,
        out float distance)
    {
        priority = 0;
        distance = float.PositiveInfinity;

        if (!IsRescueCandidate(uid, target, medibot, requireRange, searchRange, out _) ||
            !TryGetDistance(uid, target, out distance) ||
            !TryComp<MobStateComponent>(target, out var mobState) ||
            !TryComp<DamageableComponent>(target, out var damage))
        {
            return false;
        }

        var totalDamage = damage.TotalDamage.Float();
        // Preserve the existing broad acuity bands while carrying enough
        // within-band information for deterministic hysteresis. Previously
        // this returned only 1..4, so a one-point threshold crossing could
        // preempt while meaningful deterioration inside a band was invisible.
        priority = GetRescueTargetAcuity(mobState, totalDamage, rescue) * 10_000 +
                   Math.Clamp((int) totalDamage, 0, 9_999);
        return priority > 0;
    }

    private static int GetRescueTargetAcuity(
        MobStateComponent mobState,
        float totalDamage,
        LuaMRescueAgentComponent rescue)
    {
        if (mobState.CurrentState == MobState.Critical)
            return 4;

        if (totalDamage >= rescue.EvacuationMinDamage)
            return 3;

        if (totalDamage >= rescue.AutoTreatMinDamage)
            return 2;

        return 1;
    }

    private bool TryFindEvacuationTarget(
        EntityUid uid,
        float searchRange,
        LuaMRescueAgentComponent rescue,
        out EntityUid target,
        EntityUid? excludedTarget = null)
    {
        target = default;
        var bestScore = float.MinValue;

        foreach (var candidate in _lookup.GetEntitiesInRange(uid, searchRange))
        {
            if (candidate == excludedTarget)
                continue;

            if (IsTargetTemporarilySkipped(uid, candidate, rescue) ||
                !CanPerceiveAutomaticTarget(uid, candidate, searchRange))
                continue;

            if (!IsEvacuationCandidate(uid, candidate, rescue, searchRange, out var score))
                continue;

            if (score <= bestScore)
                continue;

            target = candidate;
            bestScore = score;
        }

        return target != default;
    }

    private PullStartResult TryPrepareAndStartPatientPull(
        EntityUid uid,
        EntityUid target,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn)
    {
        if (!rescue.ActivityRoleProfile.EquipmentPolicy.Allows(LuaMRescueEquipmentKind.Pulling))
        {
            rescue.LastAutoEvacuationStatus = "RoleDisallowed: role profile disallows patient pulling";
            _activity.Block(uid, LuaMRescueFailureReason.RoleDisallowed, LuaMRescueActivity.Handoff, out _);
            return PullStartResult.TerminalFailure;
        }

        if (rescue.TerminalPullFailures.TryGetValue(target, out var terminalPullFailure))
        {
            rescue.LastAutoEvacuationStatus = terminalPullFailure;
            return PullStartResult.TerminalFailure;
        }

        if (rescue.NextPullAttemptAt.TryGetValue(target, out var retryAt) &&
            _timing.CurTime < retryAt)
        {
            rescue.LastAutoEvacuationStatus =
                $"pull retry backoff until {retryAt.TotalSeconds:0.00}s; patient={FormatEntityRef(target)}";
            return PullStartResult.Waiting;
        }

        var continuingPullIntent = rescue.ActivityContext.Activity == LuaMRescueActivity.PullPatient &&
                                   rescue.ActivityContext.Target == target &&
                                   rescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active;
        if (!continuingPullIntent &&
            !BeginPatientActivity(
                uid,
                rescue,
                LuaMRescueActivity.PrepareEvacuation,
                target,
                new EntityCoordinates(target, Vector2.Zero)))
        {
            return PullStartResult.TerminalFailure;
        }

        if (HasActiveMedicalDoAfter(uid, target, out var medicalAction))
        {
            rescue.LastAutoEvacuationStatus =
                $"waiting for {medicalAction} DoAfter before pull; patient={FormatEntityRef(target)}";
            rescue.TargetStallAccumulator = 0f;
            return PullStartResult.Waiting;
        }

        if (_container.IsEntityOrParentInContainer(target) ||
            !_container.IsInSameOrNoContainer((uid, null, null), (target, null, null)))
        {
            _activity.Block(uid, LuaMRescueFailureReason.ContainedTarget, LuaMRescueActivity.Handoff, out _);
            TemporarilySkipTarget(uid, rescue, htn, target);
            rescue.LastAutoEvacuationStatus =
                $"ContainedTarget: refusing pull for {FormatEntityRef(target)}";
            Dirty(uid, rescue);
            return PullStartResult.TerminalFailure;
        }

        if (!HasComp<PullableComponent>(target))
        {
            _activity.Block(uid, LuaMRescueFailureReason.InvalidPatient, LuaMRescueActivity.Handoff, out _);
            TemporarilySkipTarget(uid, rescue, htn, target);
            rescue.LastAutoEvacuationStatus =
                $"InvalidPatient: {FormatEntityRef(target)} has no PullableComponent";
            Dirty(uid, rescue);
            return PullStartResult.TerminalFailure;
        }

        if (!TryComp<HandsComponent>(uid, out var hands))
        {
            _activity.Block(uid, LuaMRescueFailureReason.NoFreeHand, LuaMRescueActivity.Handoff, out _);
            TemporarilySkipTarget(uid, rescue, htn, target);
            rescue.LastAutoEvacuationStatus = "NoFreeHand: rescue agent has no hands component";
            Dirty(uid, rescue);
            return PullStartResult.TerminalFailure;
        }

        if (!_hands.TryGetEmptyHand(uid, out _, hands))
        {
            if (TryAutoStowHeldItemForTreatment(uid, rescue, out var stowStatus))
            {
                rescue.LastAutoEvacuationStatus = $"preparing pull: {stowStatus}";
                Dirty(uid, rescue);
                return PullStartResult.Waiting;
            }

            TrySendRescueStatusComms(
                uid,
                rescue,
                $"pull-blocked-no-hand:{target}",
                $"Не могу начать эвакуацию {Name(target)}: не удаётся освободить руку. Нужна помощь с переноской.");
            _activity.Block(uid, LuaMRescueFailureReason.NoFreeHand, LuaMRescueActivity.Handoff, out _);
            TemporarilySkipTarget(uid, rescue, htn, target);
            rescue.LastAutoEvacuationStatus = $"NoFreeHand: {stowStatus}";
            Dirty(uid, rescue);
            return PullStartResult.TerminalFailure;
        }

        if (!continuingPullIntent &&
            !BeginPatientActivity(
                uid,
                rescue,
                LuaMRescueActivity.PullPatient,
                target,
                new EntityCoordinates(target, Vector2.Zero)))
        {
            return PullStartResult.TerminalFailure;
        }

        if (_pulling.TryStartPull(uid, target) && IsPullingTarget(uid, target))
        {
            rescue.PullAttempts.Remove(target);
            rescue.NextPullAttemptAt.Remove(target);
            rescue.TerminalPullFailures.Remove(target);
            _activity.RecordProgress(
                uid,
                Transform(target).Coordinates,
                0f,
                LuaMRescueRouteStatus.Arrived,
                LuaMRescueDoAfterStatus.None,
                out _);
            return PullStartResult.Started;
        }

        var attempts = rescue.PullAttempts.GetValueOrDefault(target) + 1;
        rescue.PullAttempts[target] = attempts;
        var backoffSeconds = Math.Min(
            Math.Max(1d, rescue.ActivityRoleProfile.MaxRetryBackoff.TotalSeconds),
            Math.Max(0.1d, rescue.ActivityRoleProfile.BaseRetryBackoff.TotalSeconds) * Math.Pow(2d, attempts - 1));
        rescue.NextPullAttemptAt[target] = _timing.CurTime + TimeSpan.FromSeconds(backoffSeconds);
        _activity.RecordAttempt(uid, out var snapshot);
        if (attempts >= Math.Max(1, rescue.ActivityRoleProfile.MaxAttempts) || snapshot.IsTerminal)
        {
            var terminalStatus =
                $"ActionCancelled: pull failed after {attempts} attempts; requesting evacuation help";
            rescue.TerminalPullFailures[target] = terminalStatus;
            rescue.LastAutoEvacuationStatus = terminalStatus;
            TrySendRescueStatusComms(
                uid,
                rescue,
                $"pull-terminal:{target}",
                $"Не могу зафиксировать переноску {Name(target)} после {attempts} попыток. Нужен помощник эвакуации.");
            _activity.Fail(uid, LuaMRescueFailureReason.ActionCancelled, out _);
            TemporarilySkipTarget(uid, rescue, htn, target);
            return PullStartResult.TerminalFailure;
        }

        rescue.LastAutoEvacuationStatus =
            $"ActionCancelled: pull attempt {attempts}/{rescue.ActivityRoleProfile.MaxAttempts} failed; retry in {backoffSeconds:0.0}s";
        Dirty(uid, rescue);
        return PullStartResult.Waiting;
    }

    private bool UpdateEvacuation(EntityUid uid, LuaMRescueAgentComponent rescue, HTNComponent htn)
    {
        if (rescue.EvacuatingTarget is not { Valid: true } target ||
            Deleted(target))
        {
            ResetTargetProgress(rescue);
            rescue.EvacuatingTarget = null;
            return false;
        }

        if (!CanUseAssignedShuttle(rescue))
        {
            if (TryHoldRequiredHandoffCustody(
                    uid,
                    target,
                    rescue,
                    htn,
                    "assigned shuttle is unavailable",
                    LuaMRescueFailureReason.ShuttleUnavailable))
            {
                return true;
            }

            rescue.EvacuatingTarget = null;
            return false;
        }

        if (TryHandleTerminalDefibrillationPatient(uid, rescue, htn, target))
            return true;

        if (IsTargetTemporarilySkipped(uid, target, rescue))
        {
            if (TryHoldRequiredHandoffCustody(
                    uid,
                    target,
                    rescue,
                    htn,
                    "bounded evacuation failure remains unresolved"))
            {
                return true;
            }

            StopPullingTarget(uid, target);
            rescue.EvacuatingTarget = null;
            rescue.AssignedTarget = null;
            rescue.AssignedPatientStrap = null;
            ResetTargetProgress(rescue);
            StandbyAtAssignedShuttle(uid, rescue, htn);
            Dirty(uid, rescue);
            return false;
        }

        if (!TryComp<MobStateComponent>(target, out var mobState) ||
            mobState.CurrentState == MobState.Dead &&
            !IsDeadPatientRecoveryTarget(uid, target, rescue, mobState))
        {
            if (TryHoldRequiredHandoffCustody(
                    uid,
                    target,
                    rescue,
                    htn,
                    "dead patient cannot enter another recovery cycle",
                    LuaMRescueFailureReason.Unrevivable))
            {
                return true;
            }

            StopPullingTarget(uid, target);
            rescue.EvacuatingTarget = null;
            ResetTargetProgress(rescue);
            StandbyAtAssignedShuttle(uid, rescue, htn);
            Dirty(uid, rescue);
            return false;
        }

        if (IsEvacuationComplete(uid, target, rescue))
        {
            CompleteEvacuation(uid, rescue, htn, target);
            return false;
        }

        if (TryHoldRouteBlockedEvacuation(uid, target, rescue, htn))
            return true;

        if (UpdateTargetProgress(uid, target, rescue))
        {
            if (TryHandleStalledDeliveryTarget(uid, target, rescue, htn))
                return true;

            TemporarilySkipTarget(uid, rescue, htn, target);
            return false;
        }

        var unsafeSceneEvacuation = IsThreatenedEvacuationTarget(uid, target, rescue);
        var startedPatientPull = false;

        if (!IsPullingTarget(uid, target) &&
            TryAutoDefibTarget(uid, rescue, htn, target))
        {
            return true;
        }

        if (!unsafeSceneEvacuation &&
            !IsPullingTarget(uid, target) &&
            TryAutoTreatTarget(uid, rescue, htn, target))
        {
            return true;
        }

        if (!IsPullingTarget(uid, target) &&
            TryAutoUnbucklePatientForEvacuation(uid, target, rescue, htn))
        {
            return true;
        }

        if (TryFindPatientDeliveryStrap(uid, rescue, out var patientStrap, out _))
        {
            rescue.AssignedPatientStrap = patientStrap;
        }
        else
        {
            rescue.AssignedPatientStrap = null;
            if (rescue.RequiredOnboardHandoffPatients.Contains(target) &&
                !HasAvailablePatientDeliveryStrap(rescue, target))
            {
                TryHoldRequiredHandoffCustody(
                    uid,
                    target,
                    rescue,
                    htn,
                    "no viable patient delivery strap is available",
                    LuaMRescueFailureReason.ShuttleUnavailable);
                return true;
            }
        }

        if (!IsPullingTarget(uid, target))
        {
            if (IsWithinRange(uid, target, rescue.EvacuationStartRange))
            {
                var pullResult = TryPrepareAndStartPatientPull(uid, target, rescue, htn);
                if (pullResult == PullStartResult.TerminalFailure)
                    return false;

                if (pullResult == PullStartResult.Waiting)
                    return true;

                if (pullResult == PullStartResult.Started)
                {
                    startedPatientPull = true;
                    TrySayRescueAction(
                        uid,
                        rescue,
                        target,
                        "pull-start",
                        "patient pull started",
                        $"\u041f\u0430\u0446\u0438\u0435\u043d\u0442 {Name(target)} \u043d\u0430 \u043c\u043d\u0435. \u0422\u0430\u0449\u0443 \u043d\u0430 \u0431\u043e\u0440\u0442.");
                }
            }

            if (!IsPullingTarget(uid, target))
            {
                SetRescueTask(
                    uid,
                    rescue,
                    LuaMRescueTaskStage.EvacuatingPatient,
                    target,
                    null,
                    $"approaching evacuation patient {FormatEntityRef(target)}");
                TrySayRescueAction(
                    uid,
                    rescue,
                    target,
                    "approach-patient",
                    "closing on evacuation patient",
                    $"\u0411\u0435\u0433\u0443 \u043a \u043f\u0430\u0446\u0438\u0435\u043d\u0442\u0443 {Name(target)}. \u0414\u0435\u0440\u0436\u0438\u0442\u0435 \u043a\u043e\u0440\u0438\u0434\u043e\u0440.");
                SetFollowTarget(uid, rescue, htn, target);
                return true;
            }
        }

        if (rescue.AssignedPatientStrap is { Valid: true } deliveryStrap &&
            !Deleted(deliveryStrap))
        {
            var buckleResult = TryBucklePatientToStrap(uid, target, deliveryStrap, rescue);
            if (buckleResult == PatientBuckleResult.Succeeded)
            {
                CompleteEvacuation(uid, rescue, htn, target);
                return false;
            }

            if (buckleResult == PatientBuckleResult.Waiting)
            {
                // The patient already satisfies the physical buckle contract.
                // Keep BucklePatient as the authoritative activity while its
                // pair-scoped backoff runs instead of replacing it with a fresh
                // DeliverPatient generation on every refresh.
                HoldMovementForRoute(uid, htn);
                SetRescueTask(
                    uid,
                    rescue,
                    LuaMRescueTaskStage.DeliveringPatient,
                    target,
                    deliveryStrap,
                    $"waiting for bounded buckle retry on {FormatEntityRef(deliveryStrap)}");
                Dirty(uid, rescue);
                return true;
            }

            if (buckleResult == PatientBuckleResult.TerminalFailure)
            {
                ResetTargetProgress(rescue);
                if (TryFindPatientDeliveryStrap(uid, rescue, out var replacementStrap, out _))
                    rescue.AssignedPatientStrap = replacementStrap;
                else
                {
                    rescue.AssignedPatientStrap = null;
                    if (rescue.RequiredOnboardHandoffPatients.Contains(target))
                    {
                        TryHoldRequiredHandoffCustody(
                            uid,
                            target,
                            rescue,
                            htn,
                            "all viable patient/strap buckle pairs exhausted",
                            LuaMRescueFailureReason.ActionCancelled);
                        return true;
                    }
                }
            }
        }

        if (rescue.AssignedPatientStrap is { Valid: true } assignedStrap &&
            !Deleted(assignedStrap))
        {
            SetRescueTask(
                uid,
                rescue,
                LuaMRescueTaskStage.DeliveringPatient,
                target,
                assignedStrap,
                $"delivering {FormatEntityRef(target)} to {FormatEntityRef(assignedStrap)}");
            if (!startedPatientPull)
            {
                TrySayRescueAction(
                    uid,
                    rescue,
                    target,
                    "deliver-bed",
                    "moving patient to shuttle bed",
                    $"\u0422\u0430\u0449\u0443 {Name(target)} \u043d\u0430 \u0431\u043e\u0440\u0442 \u043a \u043a\u043e\u0439\u043a\u0435.");
            }

            return SetFollowDeliveryStrap(uid, rescue, htn, assignedStrap);
        }

        SetRescueTask(
            uid,
            rescue,
            LuaMRescueTaskStage.DeliveringPatient,
            target,
            rescue.AssignedShuttleAnchor ?? rescue.AssignedShuttle,
            $"returning {FormatEntityRef(target)} to shuttle");
        if (!startedPatientPull)
        {
            TrySayRescueAction(
                uid,
                rescue,
                target,
                "deliver-shuttle",
                "moving patient to shuttle",
                $"\u0422\u0430\u0449\u0443 {Name(target)} \u043d\u0430 \u0448\u0430\u0442\u0442\u043b. \u041d\u0430 \u0431\u043e\u0440\u0442\u0443 \u0440\u0430\u0437\u0431\u0435\u0440\u0443.");
        }

        return SetFollowShuttle(uid, rescue, htn);
    }

    private bool TryStartOrContinueEvacuation(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        EntityUid target)
    {
        if (TryHandleTerminalDefibrillationPatient(uid, rescue, htn, target))
            return true;

        var unsafeSceneEvacuation = IsThreatenedEvacuationTarget(uid, target, rescue);
        if (!NeedsEvacuation(uid, target, rescue))
            return false;

        var evacuationReason = BuildEvacuationReason(uid, target, rescue, unsafeSceneEvacuation);

        if (rescue.EvacuatingTarget != target)
        {
            rescue.ShuttleReturnRouted = false;
            rescue.ShuttleRoutedTarget = null;
            rescue.AssignedPatientStrap = null;
            ResetTargetProgress(rescue);
        }

        rescue.EvacuatingTarget = target;
        SetRescueTask(
            uid,
            rescue,
            LuaMRescueTaskStage.EvacuatingPatient,
            target,
            null,
            $"evacuating {FormatEntityRef(target)}; {evacuationReason}");
        rescue.LastAutoEvacuationStatus = evacuationReason;

        TryRouteShuttleToTarget(uid, rescue, target);

        if (TryAutoDefibTarget(uid, rescue, htn, target))
            return true;

        if (!unsafeSceneEvacuation &&
            TryAutoTreatTarget(uid, rescue, htn, target))
        {
            return true;
        }

        if (!IsPullingTarget(uid, target) &&
            TryAutoUnbucklePatientForEvacuation(uid, target, rescue, htn))
        {
            return true;
        }

        if (!IsWithinRange(uid, target, rescue.EvacuationStartRange))
        {
            SetRescueTask(
                uid,
                rescue,
                LuaMRescueTaskStage.EvacuatingPatient,
                target,
                null,
                $"approaching evacuation patient {FormatEntityRef(target)}");
            TrySayRescueAction(
                uid,
                rescue,
                target,
                "approach-patient",
                "closing on evacuation patient",
                $"\u0411\u0435\u0433\u0443 \u043a \u043f\u0430\u0446\u0438\u0435\u043d\u0442\u0443 {Name(target)}. \u0414\u0435\u0440\u0436\u0438\u0442\u0435 \u043a\u043e\u0440\u0438\u0434\u043e\u0440.");
            SetFollowTarget(uid, rescue, htn, target);
            return true;
        }

        var pullResult = TryPrepareAndStartPatientPull(uid, target, rescue, htn);
        if (pullResult == PullStartResult.TerminalFailure)
            return false;

        if (pullResult == PullStartResult.Waiting)
        {
            SetRescueTask(
                uid,
                rescue,
                LuaMRescueTaskStage.EvacuatingPatient,
                target,
                null,
                $"retrying pull on {FormatEntityRef(target)}");
            SetFollowTarget(uid, rescue, htn, target);
            return true;
        }

        TrySayRescueAction(
            uid,
            rescue,
            target,
            "pull-start",
            "patient pull started",
            $"\u041f\u0430\u0446\u0438\u0435\u043d\u0442 {Name(target)} \u043d\u0430 \u043c\u043d\u0435. \u0422\u0430\u0449\u0443 \u043d\u0430 \u0431\u043e\u0440\u0442.");
        SetRescueTask(
            uid,
            rescue,
            LuaMRescueTaskStage.DeliveringPatient,
            target,
            rescue.AssignedShuttleAnchor ?? rescue.AssignedShuttle,
            $"returning {FormatEntityRef(target)} to shuttle");
        return SetFollowShuttle(uid, rescue, htn);
    }

    private string BuildEvacuationReason(
        EntityUid uid,
        EntityUid target,
        LuaMRescueAgentComponent rescue,
        bool unsafeSceneEvacuation)
    {
        if (unsafeSceneEvacuation &&
            TryComp<LuaMRescueTeamComponent>(uid, out var team))
        {
            var evacuationReason = HasRescueTeamOverwhelmingThreatPressure(team, rescue)
                ? "overwhelming-threat evacuation"
                : "unsafe-scene evacuation";
            return $"{evacuationReason} of {FormatEntityRef(target)}; {team.LastSceneStatus}; {team.LastMemoryDigest}";
        }

        if (TryComp<MobStateComponent>(target, out var mobState) &&
            mobState.CurrentState == MobState.Critical)
        {
            var damage = TryComp<DamageableComponent>(target, out var criticalDamage)
                ? criticalDamage.TotalDamage.Float()
                : 0f;
            return $"condition-worsened evacuation of {FormatEntityRef(target)}; state=critical; damage={damage:0.0}; on-site treatment limited";
        }

        if (TryComp<DamageableComponent>(target, out var damageable) &&
            damageable.TotalDamage.Float() >= rescue.EvacuationMinDamage)
        {
            return $"heavy-damage evacuation of {FormatEntityRef(target)}; damage={damageable.TotalDamage.Float():0.0}; shuttle care required";
        }

        return $"evacuation of {FormatEntityRef(target)}";
    }

    private bool TryAutoUnbucklePatientForEvacuation(
        EntityUid uid,
        EntityUid target,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn)
    {
        if (!rescue.AutoUnbucklePatientsForEvacuation ||
            !TryComp<BuckleComponent>(target, out var buckle))
        {
            return false;
        }

        if (buckle.BuckledTo is not { Valid: true } strap)
        {
            rescue.EvacuationUnbuckleAttempts.Remove(target);
            rescue.NextEvacuationUnbuckleAttemptAt.Remove(target);
            rescue.TerminalEvacuationUnbuckleFailures.Remove(target);
            return false;
        }

        if (Deleted(strap) ||
            IsAssignedShuttlePatientStrap(strap, rescue))
        {
            return false;
        }

        if (rescue.TerminalEvacuationUnbuckleFailures.TryGetValue(target, out var terminalFailure))
        {
            rescue.LastAutoEvacuationStatus = terminalFailure;
            TemporarilySkipTarget(uid, rescue, htn, target);
            return true;
        }

        if (!IsWithinRange(uid, strap, buckle.Range))
        {
            rescue.LastAutoEvacuationStatus = $"moving to unbuckle {FormatEntityRef(target)} from {FormatEntityRef(strap)}";
            SetFollowTarget(uid, rescue, htn, target);
            return true;
        }

        if (rescue.NextEvacuationUnbuckleAttemptAt.TryGetValue(target, out var retryAt) &&
            _timing.CurTime < retryAt)
        {
            rescue.LastAutoEvacuationStatus =
                $"ActionCancelled: waiting for bounded unbuckle retry of {FormatEntityRef(target)}";
            return true;
        }

        if (TryGetUnbuckleDelayRemaining(buckle, out var unbuckleDelay))
        {
            rescue.TargetStallAccumulator = 0f;
            rescue.LastAutoEvacuationStatus =
                $"waiting {unbuckleDelay.TotalSeconds:0.00}s for buckle safety delay before evacuating " +
                $"{FormatEntityRef(target)} from {FormatEntityRef(strap)}";
            Dirty(uid, rescue);
            return true;
        }

        var unbuckled = _buckle.TryUnbuckle(target, uid, buckle, popup: false);
        rescue.LastAutoEvacuationStatus = unbuckled
            ? $"unbuckled {FormatEntityRef(target)} from {FormatEntityRef(strap)} for evacuation"
            : $"could not unbuckle {FormatEntityRef(target)} from {FormatEntityRef(strap)} for evacuation";

        if (unbuckled)
        {
            rescue.EvacuationUnbuckleAttempts.Remove(target);
            rescue.NextEvacuationUnbuckleAttemptAt.Remove(target);
            rescue.TerminalEvacuationUnbuckleFailures.Remove(target);
            Dirty(uid, rescue);
            return true;
        }

        var attempts = rescue.EvacuationUnbuckleAttempts.GetValueOrDefault(target) + 1;
        rescue.EvacuationUnbuckleAttempts[target] = attempts;
        var backoffSeconds = Math.Min(
            Math.Max(1d, rescue.ActivityRoleProfile.MaxRetryBackoff.TotalSeconds),
            Math.Max(0.1d, rescue.ActivityRoleProfile.BaseRetryBackoff.TotalSeconds) * Math.Pow(2d, attempts - 1));
        rescue.NextEvacuationUnbuckleAttemptAt[target] =
            _timing.CurTime + TimeSpan.FromSeconds(backoffSeconds);
        _activity.RecordAttempt(uid, out _);
        if (attempts >= Math.Max(1, rescue.ActivityRoleProfile.MaxAttempts))
        {
            var status =
                $"ActionCancelled: unbuckle failed after {attempts} attempts; patient={FormatEntityRef(target)}; assistance required";
            rescue.TerminalEvacuationUnbuckleFailures[target] = status;
            rescue.LastAutoEvacuationStatus = status;
            _activity.Block(uid, LuaMRescueFailureReason.ActionCancelled, LuaMRescueActivity.Handoff, out _);
            TrySendRescueStatusComms(
                uid,
                rescue,
                $"unbuckle-terminal:{target}",
                $"Не могу освободить {Name(target)} для эвакуации после {attempts} попыток. Нужна ручная помощь.");
            TemporarilySkipTarget(uid, rescue, htn, target);
            return true;
        }

        rescue.LastAutoEvacuationStatus =
            $"ActionCancelled: unbuckle attempt {attempts}/{rescue.ActivityRoleProfile.MaxAttempts}; retry in {backoffSeconds:0.0}s";
        SetFollowTarget(uid, rescue, htn, target);
        Dirty(uid, rescue);

        return true;
    }

    private bool TryAutoAnalyzeTarget(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        EntityUid target)
    {
        if (rescue.TerminalAnalysisFailures.TryGetValue(target, out var terminalAnalysis))
        {
            rescue.LastAutoAnalyzeStatus = terminalAnalysis;
            return false;
        }

        if (!rescue.AutoAnalyzeBeforeTreatment ||
            rescue.AutoAnalyzeCooldown <= 0f ||
            _timing.CurTime < rescue.NextAutoAnalyzeAttempt ||
            WasTargetRecentlyAnalyzed(target, rescue))
        {
            return false;
        }

        rescue.NextAutoAnalyzeAttempt = _timing.CurTime + TimeSpan.FromSeconds(Math.Min(5f, rescue.AutoAnalyzeCooldown));

        if (!TryFindHealthAnalyzerItem(uid, out var analyzer, out var status))
        {
            rescue.LastAutoAnalyzeStatus = status;
            if (status.Equals("no empty hand for health analyzer", StringComparison.OrdinalIgnoreCase))
            {
                if (TryAutoStowHeldItemForTreatment(uid, rescue, out var stowStatus))
                {
                    rescue.LastAutoAnalyzeStatus = stowStatus;
                    Dirty(uid, rescue);
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(stowStatus))
                    rescue.LastAutoAnalyzeStatus = stowStatus;
            }

            Dirty(uid, rescue);
            return false;
        }

        if (!BeginPatientActivity(
            uid,
            rescue,
            LuaMRescueActivity.Triage,
            target,
            new EntityCoordinates(target, Vector2.Zero)))
        {
            return false;
        }

        _interaction.InteractUsing(uid, analyzer, target, Transform(target).Coordinates);
        if (HasActiveMedicalDoAfter(uid, target, out var kind))
        {
            TrackMedicalDoAfter(uid, rescue, target, analyzer, kind);
            rescue.LastAutoAnalyzeStatus =
                $"{kind} DoAfter running: patient={FormatEntityRef(target)}; item={FormatEntityRef(analyzer)}";
            Dirty(uid, rescue);
            return true;
        }

        rescue.LastAutoAnalyzeStatus =
            $"analysis of {FormatEntityRef(target)} using {FormatEntityRef(analyzer)} did not start";
        Dirty(uid, rescue);
        return false;
    }

    private bool TryAutoTreatTarget(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        EntityUid target)
    {
        if (!rescue.AutoTreatWithCarriedItems)
        {
            rescue.LastAutoTreatmentDecisionStatus = "automatic carried-item treatment is disabled";
            return false;
        }

        if (Deleted(target))
        {
            rescue.LastAutoTreatmentDecisionStatus = $"treatment target {FormatEntityRef(target)} is deleted";
            return false;
        }

        if (!TryComp<MobStateComponent>(target, out var mobState))
        {
            rescue.LastAutoTreatmentDecisionStatus = $"treatment target {FormatEntityRef(target)} has no mob state";
            return false;
        }

        if (mobState.CurrentState == MobState.Dead)
        {
            rescue.LastAutoTreatmentDecisionStatus = $"treatment target {FormatEntityRef(target)} is dead";
            return false;
        }

        if (!TryComp<DamageableComponent>(target, out var damageable))
        {
            rescue.LastAutoTreatmentDecisionStatus = $"treatment target {FormatEntityRef(target)} has no damage state";
            return false;
        }

        var totalDamage = damageable.TotalDamage.Float();
        if (totalDamage < rescue.AutoTreatMinDamage)
        {
            rescue.LastAutoTreatmentDecisionStatus =
                $"treatment target {FormatEntityRef(target)} is below threshold: " +
                $"damage={totalDamage:0.###}; minimum={rescue.AutoTreatMinDamage:0.###}";
            return false;
        }

        var withinTreatmentRange = IsWithinRange(uid, target, rescue.PlayerActionRange);
        var maintainingTreatmentCooldown =
            _timing.CurTime < rescue.NextAutoTreatmentAttempt &&
            rescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active &&
            rescue.ActivityContext.Activity == LuaMRescueActivity.TreatPatient &&
            rescue.ActivityContext.Target == target;
        if (!withinTreatmentRange && !maintainingTreatmentCooldown)
        {
            rescue.LastAutoTreatmentDecisionStatus =
                $"treatment target {FormatEntityRef(target)} is outside unobstructed action range; " +
                $"range={rescue.PlayerActionRange:0.###}; activity={rescue.ActivityContext.Activity}; " +
                $"terminal={rescue.ActivityContext.TerminalStatus}; generation={rescue.ActivityContext.Generation}";
            return false;
        }

        if (rescue.PendingMedicalEffectVerification &&
            rescue.PendingMedicalDoAfterTarget == target)
        {
            rescue.LastAutoTreatmentStatus =
                $"awaiting observed medical effect on {FormatEntityRef(target)}";
            rescue.TargetStallAccumulator = 0f;
            HoldMovementForMedicalDoAfter(uid, rescue, target);
            Dirty(uid, rescue);
            return true;
        }

        if (HasActiveMedicalDoAfter(uid, target, out var activeKind))
        {
            TrackMedicalDoAfter(uid, rescue, target, rescue.PendingMedicalDoAfterItem, activeKind);
            rescue.LastAutoTreatmentStatus =
                $"{activeKind} DoAfter running: patient={FormatEntityRef(target)}";
            rescue.TargetStallAccumulator = 0f;
            Dirty(uid, rescue);
            return true;
        }

        SetRescueTask(
            uid,
            rescue,
            LuaMRescueTaskStage.TreatingPatient,
            target,
            null,
            $"treating {FormatEntityRef(target)}");
        if (!BeginPatientActivity(
            uid,
            rescue,
            LuaMRescueActivity.TreatPatient,
            target,
            new EntityCoordinates(target, Vector2.Zero)))
        {
            rescue.LastAutoTreatmentDecisionStatus =
                $"cannot own treatment intent for {FormatEntityRef(target)}: " +
                $"activity={rescue.ActivityContext.Activity}; terminal={rescue.ActivityContext.TerminalStatus}; " +
                $"failure={rescue.ActivityContext.FailureReason}; generation={rescue.ActivityContext.Generation}";
            return false;
        }

        if (HasTerminalTreatmentFailure(uid, rescue, target))
        {
            rescue.LastAutoTreatmentDecisionStatus =
                rescue.TerminalTreatmentFailures.GetValueOrDefault(
                    target,
                    $"terminal treatment failure for {FormatEntityRef(target)}");
            return false;
        }
        if (!IsOnAssignedShuttle(target, rescue))
        {
            TrySayRescueAction(
                uid,
                rescue,
                target,
                "treat-onsite",
                "treating patient on scene",
                $"\u041b\u0435\u0447\u0443 {Name(target)} \u043d\u0430 \u043c\u0435\u0441\u0442\u0435. \u041d\u0435 \u043c\u0435\u0448\u0430\u0439\u0442\u0435 \u0434\u043e\u0441\u0442\u0443\u043f\u0443.");
        }

        if (TryAutoAnalyzeTarget(uid, rescue, htn, target))
            return true;

        if (_timing.CurTime < rescue.NextAutoTreatmentAttempt)
        {
            // The coordinator already owns an in-range TreatPatient intent. A
            // finite item cooldown is active waiting, not a navigation failure:
            // returning false would let the caller replace it with ApproachPatient
            // every refresh and churn the intent generation until the deadline.
            var cooldownStatus =
                $"treatment cooldown for {FormatEntityRef(target)} until " +
                $"{rescue.NextAutoTreatmentAttempt.TotalSeconds:0.00}s";
            var changed = rescue.TargetStallAccumulator != 0f ||
                          !string.Equals(
                              rescue.LastAutoTreatmentStatus,
                              cooldownStatus,
                              StringComparison.Ordinal);
            rescue.TargetStallAccumulator = 0f;
            rescue.LastAutoTreatmentStatus = cooldownStatus;

            if (withinTreatmentRange)
            {
                // A stale FollowTarget can still carry velocity from the patient
                // that was just displaced. Stop it while the authoritative
                // treatment intent waits for its bounded item cooldown.
                HoldMovementForTreatmentCooldown(uid, htn);
            }
            else
            {
                // Range loss during a cooldown is execution progress inside the
                // same treatment intent, not a reason to replace that intent with
                // ApproachPatient and then immediately replace it back again.
                SetMovementGoal(
                    uid,
                    rescue,
                    htn,
                    new EntityCoordinates(target, Vector2.Zero),
                    rescue.PlayerActionRange);
            }

            if (changed)
                Dirty(uid, rescue);
            return true;
        }

        rescue.NextAutoTreatmentAttempt = _timing.CurTime + TimeSpan.FromSeconds(Math.Max(0.1f, rescue.AutoTreatCooldown));

        if (!TryFindTreatmentItem(uid, target, null, null, includeDiagnosticItems: false, out var item, out var status))
        {
            rescue.LastAutoTreatmentStatus = status;
            if (status.Equals("no empty hand for treatment item", StringComparison.OrdinalIgnoreCase))
            {
                if (TryAutoStowHeldItemForTreatment(uid, rescue, out var stowStatus))
                {
                    rescue.LastAutoSupplyStatus = stowStatus;
                    Dirty(uid, rescue);
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(stowStatus))
                    rescue.LastAutoSupplyStatus = stowStatus;
            }
            else if (status.StartsWith("no usable medical item", StringComparison.OrdinalIgnoreCase))
            {
                if (TryStartAutoPickupNearbyMedicalSupply(uid, rescue, htn, target, out var supplyStatus))
                {
                    rescue.LastAutoSupplyStatus = supplyStatus;
                    Dirty(uid, rescue);
                    return true;
                }

                if (TryStartAutoTakeNearbyStoredMedicalSupply(uid, rescue, htn, target, out supplyStatus))
                {
                    rescue.LastAutoSupplyStatus = supplyStatus;
                    Dirty(uid, rescue);
                    return true;
                }

                if (TryStartAutoResupplyFromVending(uid, rescue, htn, target, out supplyStatus))
                {
                    rescue.LastAutoSupplyStatus = supplyStatus;
                    Dirty(uid, rescue);
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(supplyStatus))
                    rescue.LastAutoSupplyStatus = supplyStatus;
            }

            var failureReason = status.Equals("no empty hand for treatment item", StringComparison.OrdinalIgnoreCase)
                ? LuaMRescueFailureReason.NoFreeHand
                : LuaMRescueFailureReason.NoEffectiveMedicine;
            RecordTreatmentFailure(uid, rescue, target, failureReason, status);
            Dirty(uid, rescue);
            return false;
        }

        if (!IsEffectiveTreatmentItem(item, target, out var rejectionReason))
        {
            rescue.LastAutoTreatmentStatus =
                $"{rejectionReason}: {FormatEntityRef(item)} cannot treat {FormatEntityRef(target)}";
            var failureReason = Enum.TryParse<LuaMRescueFailureReason>(rejectionReason, out var parsedReason)
                ? parsedReason
                : LuaMRescueFailureReason.NoEffectiveMedicine;
            RecordTreatmentFailure(uid, rescue, target, failureReason, rescue.LastAutoTreatmentStatus);
            Dirty(uid, rescue);
            return false;
        }

        var startingDamage = damageable.TotalDamage.Float();
        var startingDamageByType = GetDamageTypeSnapshot(target);
        var startingMobState = mobState.CurrentState;
        var startingBleedAmount = TryComp<BloodstreamComponent>(target, out var bloodstream)
            ? bloodstream.BleedAmount
            : 0f;
        var startingBloodVolume = GetPatientBloodVolume(target);
        var expectedEffect = BuildMedicalEffectExpectation(item, target);
        var volumeBefore = GetMedicalItemSolutionVolume(item);
        var handled = _interaction.InteractUsing(uid, item, target, Transform(target).Coordinates);
        if (HasActiveMedicalDoAfter(uid, target, out activeKind))
        {
            TrackMedicalDoAfter(uid, rescue, target, item, activeKind);
            rescue.LastAutoTreatmentStatus =
                $"{activeKind} DoAfter started: patient={FormatEntityRef(target)}; item={FormatEntityRef(item)}";
            rescue.TargetStallAccumulator = 0f;
            Dirty(uid, rescue);
            return true;
        }

        var volumeAfter = GetMedicalItemSolutionVolume(item);
        var solutionTransferred = handled &&
                                  volumeBefore is { } before &&
                                  volumeAfter is { } after &&
                                  after + 0.001f < before;
        var immediateEffect = handled && HasObservedMedicalImprovement(
            target,
            startingDamageByType,
            startingBleedAmount,
            startingBloodVolume,
            expectedEffect.DamageTypes,
            expectedEffect.ReducesBleeding,
            expectedEffect.IncreasesBloodVolume,
            startingMobState,
            expectedEffect.ImprovesMobState);
        rescue.LastAutoTreatmentStatus = immediateEffect
            ? $"instant treatment completed: patient={FormatEntityRef(target)}; item={FormatEntityRef(item)}"
            : solutionTransferred && IsInjectionMedicalAction("injection", item)
                ? $"injection transferred: patient={FormatEntityRef(target)}; awaiting observed medical effect"
                : $"treatment failed to produce an effect: patient={FormatEntityRef(target)}; item={FormatEntityRef(item)}";

        Dirty(uid, rescue);

        if (!immediateEffect &&
            solutionTransferred &&
            IsInjectionMedicalAction("injection", item))
        {
            TrackInstantMedicalEffectVerification(
                uid,
                rescue,
                target,
                item,
                TryComp<InjectorComponent>(item, out _) ? "injector" : "hypospray",
                startingDamage,
                startingDamageByType,
                volumeBefore,
                startingBleedAmount,
                startingMobState,
                startingBloodVolume,
                expectedEffect);
            return true;
        }

        if (!immediateEffect)
        {
            RecordTreatmentFailure(
                uid,
                rescue,
                target,
                LuaMRescueFailureReason.ActionCancelled,
                $"interaction with {FormatEntityRef(item)} produced no verified effect");
            return false;
        }

        rescue.TargetStallAccumulator = 0f;
        rescue.OnboardCareAttempts.Remove(target);
        ClearTreatmentFailure(rescue, target);
        _activity.RecordProgress(
            uid,
            Transform(target).Coordinates,
            TryGetDistance(uid, target, out var immediateDistance) ? immediateDistance : null,
            LuaMRescueRouteStatus.Arrived,
            LuaMRescueDoAfterStatus.Succeeded,
            out _);
        return true;
    }

    private bool TryValidateDefibrillationPatient(
        EntityUid target,
        out LuaMRescueFailureReason failureReason,
        out string status)
    {
        if (!TryComp<MobStateComponent>(target, out var mobState) ||
            mobState.CurrentState is not (MobState.Dead or MobState.Critical))
        {
            failureReason = LuaMRescueFailureReason.InvalidPatient;
            status = "defibrillation requires a dead or critical supported patient";
            return false;
        }

        if (HasComp<UnrevivableComponent>(target) || HasComp<RottingComponent>(target))
        {
            failureReason = LuaMRescueFailureReason.Unrevivable;
            status = HasComp<RottingComponent>(target)
                ? "defibrillation blocked: patient is rotten"
                : "defibrillation blocked: patient is marked unrevivable";
            return false;
        }

        if (!HasComp<MobThresholdsComponent>(target))
        {
            failureReason = LuaMRescueFailureReason.UnsupportedSpecies;
            status = "defibrillation blocked: patient has no supported mob thresholds";
            return false;
        }

        if (!TryComp<MindContainerComponent>(target, out var mind) || !mind.HasMind)
        {
            failureReason = LuaMRescueFailureReason.Unrevivable;
            status = "defibrillation blocked: no recoverable mind is attached to the body";
            return false;
        }

        failureReason = LuaMRescueFailureReason.None;
        status = "defibrillation patient validated";
        return true;
    }

    private bool HasExpectedDefibrillatorBenefit(DefibrillatorComponent defibrillator, EntityUid target)
    {
        return TryComp<DamageableComponent>(target, out var damageable) &&
               defibrillator.ZapHeal.DamageDict.Any(entry =>
                   entry.Value < 0 &&
                   damageable.Damage.DamageDict.TryGetValue(entry.Key, out var current) &&
                   current > 0);
    }

    private void MarkTerminalDefibrillationFailure(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        EntityUid target,
        LuaMRescueFailureReason reason,
        string status)
    {
        var debugStatus = $"{reason}: {status}; patient={FormatEntityRef(target)}";
        rescue.TerminalDefibrillationFailures[target] = debugStatus;
        rescue.LastAutoDefibStatus = debugStatus;
        _activity.Fail(uid, reason, out _);
        TrySendRescueStatusComms(
            uid,
            rescue,
            $"defib-terminal:{target}:{reason}",
            $"Восстановление {Name(target)} прекращено: {status}. Перехожу к транспортировке и передаче.");
        Dirty(uid, rescue);
    }

    /// <summary>
    /// Defibrillation eligibility is stricter than manual patient eligibility.
    /// Resolve unrecoverable dead/critical targets before the evacuation gate so
    /// an explicit order cannot fall through into an endless follow intent.
    /// </summary>
    private bool TryHandleTerminalDefibrillationPatient(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        EntityUid target)
    {
        if (!rescue.AutoDefibDeadPatients ||
            Deleted(target) ||
            !TryComp<MobStateComponent>(target, out var mobState) ||
            mobState.CurrentState is not (MobState.Dead or MobState.Critical))
        {
            return false;
        }

        var validationSucceeded =
            TryValidateDefibrillationPatient(target, out var failureReason, out var status);
        var hasRecordedTerminal =
            rescue.TerminalDefibrillationFailures.TryGetValue(target, out var recordedTerminal);

        if (validationSucceeded && !hasRecordedTerminal)
            return false;

        if (!validationSucceeded && !hasRecordedTerminal)
        {
            MarkTerminalDefibrillationFailure(uid, rescue, target, failureReason, status);
        }
        else if (validationSucceeded)
        {
            // Equipment exhaustion and the bounded attempt cap are recorded
            // after structural validation succeeds. Reuse the terminal record
            // here so the executor can release or transport the patient instead
            // of restoring the same follow intent forever.
            failureReason = rescue.ActivityContext.FailureReason == LuaMRescueFailureReason.None
                ? LuaMRescueFailureReason.NoEffectiveMedicine
                : rescue.ActivityContext.FailureReason;
            status = recordedTerminal ?? "recorded terminal defibrillation failure";
            rescue.LastAutoDefibStatus = status;
        }

        // An inherited physical/home-handoff contract transports even an
        // unrevivable body. Defibrillation is terminal, but custody is not: let
        // the forced evacuation path continue instead of clearing its owner.
        if (rescue.RequiredOnboardHandoffPatients.Contains(target))
            return false;

        // A usable shuttle is the recovery path for a structurally valid
        // patient whose local defibrillation budget or equipment is exhausted.
        if (validationSucceeded && NeedsEvacuation(uid, target, rescue))
            return false;

        StopPullingTarget(uid, target);
        if (rescue.ManualOverrideTarget == target)
        {
            RevokeManualOverrideTarget(
                uid,
                rescue,
                target,
                $"terminal defibrillation for {FormatEntityRef(target)}");
        }
        rescue.EvacuatingTarget = null;
        rescue.AssignedTarget = null;
        rescue.AssignedPatientStrap = null;
        ClearArrivalReportTarget(rescue, target);
        ClearTriageDecisionTarget(rescue, target);
        ClearDeathSignalTarget(rescue, target);
        ResetTargetProgress(rescue);

        if (IsBuckledToAssignedShuttlePatientStrap(target, rescue))
        {
            // A physically occupied shuttle bed remains owned by the onboard
            // handoff path until it is safely and actually unbuckled.
            rescue.OnboardCareTarget = target;
            SetRescueTask(
                uid,
                rescue,
                LuaMRescueTaskStage.DeliveringPatient,
                target,
                null,
                $"terminal onboard handoff pending for {FormatEntityRef(target)}: {failureReason}");
            SetOnboardCareStatus(rescue, target, $"terminal defibrillation outcome; {status}");
        }
        else
        {
            if (rescue.TaskPatientTarget == target)
                ClearRescueTask(uid, rescue, $"terminal defibrillation handoff for {FormatEntityRef(target)}: {failureReason}");

            rescue.OnboardCareTarget = null;
        }

        ClearFollowTarget(uid, rescue, htn);
        rescue.LastTargetTrackingStatus =
            $"target_terminal: {FormatEntityRef(target)}; reason={failureReason}; follow=cleared";
        Dirty(uid, rescue);
        return true;
    }

    private bool TryAutoDefibTarget(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        EntityUid target)
    {
        if (!rescue.AutoDefibDeadPatients ||
            Deleted(target) ||
            !TryComp<MobStateComponent>(target, out var mobState) ||
            mobState.CurrentState is not (MobState.Dead or MobState.Critical))
        {
            return false;
        }

        if (rescue.TerminalDefibrillationFailures.TryGetValue(target, out var terminalFailure))
        {
            rescue.LastAutoDefibStatus = terminalFailure;
            return false;
        }

        // A started third attempt still owns its DoAfter. Let its authoritative
        // completion event record the outcome before enforcing the attempt cap;
        // otherwise the intent becomes terminal one tick too early and the
        // completed failure is discarded as stale.
        if (HasActiveMedicalDoAfter(uid, target, out var activeKind))
        {
            TrackMedicalDoAfter(uid, rescue, target, rescue.PendingMedicalDoAfterItem, activeKind);
            rescue.LastAutoDefibStatus =
                $"{activeKind} DoAfter running: patient={FormatEntityRef(target)}";
            rescue.TargetStallAccumulator = 0f;
            Dirty(uid, rescue);
            return true;
        }

        if (rescue.DefibrillationAttempts.GetValueOrDefault(target) >= 3)
        {
            MarkTerminalDefibrillationFailure(
                uid,
                rescue,
                target,
                LuaMRescueFailureReason.Unrevivable,
                "defibrillation failed after the bounded three-attempt policy; transport/handoff fallback");
            return false;
        }

        if (!TryValidateDefibrillationPatient(target, out var validationFailure, out var validationStatus))
        {
            MarkTerminalDefibrillationFailure(uid, rescue, target, validationFailure, validationStatus);
            return false;
        }

        if (rescue.DefibrillationStartedAt.TryGetValue(target, out var defibStarted) &&
            _timing.CurTime - defibStarted >= TimeSpan.FromSeconds(30))
        {
            MarkTerminalDefibrillationFailure(
                uid,
                rescue,
                target,
                LuaMRescueFailureReason.DeadlineExceeded,
                "defibrillation failed: 30 second patient deadline reached; transport/handoff fallback");
            return false;
        }

        SetRescueTask(
            uid,
            rescue,
            LuaMRescueTaskStage.TreatingPatient,
            target,
            null,
            $"defibrillating {FormatEntityRef(target)}");
        if (!BeginPatientActivity(
            uid,
            rescue,
            LuaMRescueActivity.DefibrillatePatient,
            target,
            new EntityCoordinates(target, Vector2.Zero)))
        {
            return false;
        }

        if (!IsWithinRange(uid, target, rescue.PlayerActionRange))
        {
            rescue.LastAutoDefibStatus = $"moving to defibrillate {FormatEntityRef(target)}";
            TrySayRescueAction(
                uid,
                rescue,
                target,
                "defib-approach",
                "moving to defibrillate patient",
                $"\u0418\u0434\u0443 \u043a {Name(target)} \u0441 \u0434\u0435\u0444\u0438\u0431\u0440\u0438\u043b\u043b\u044f\u0442\u043e\u0440\u043e\u043c. \u041e\u0441\u0432\u043e\u0431\u043e\u0434\u0438\u0442\u0435 \u043c\u0435\u0441\u0442\u043e.");
            SetFollowTarget(uid, rescue, htn, target);
            Dirty(uid, rescue);
            return true;
        }

        if (_timing.CurTime < rescue.NextAutoDefibAttempt)
        {
            if (rescue.LastAutoDefibStatus.StartsWith("started defibrillation", StringComparison.OrdinalIgnoreCase))
            {
                rescue.LastAutoDefibStatus = $"defibrillation in progress for {FormatEntityRef(target)}";
                Dirty(uid, rescue);
                return true;
            }

            return false;
        }

        rescue.NextAutoDefibAttempt = _timing.CurTime + TimeSpan.FromSeconds(Math.Max(0.1f, rescue.AutoDefibCooldown));

        if (!TryFindDefibrillatorItem(uid, target, out var defib, out var status))
        {
            rescue.LastAutoDefibStatus = status;
            if (status.Equals("no empty hand for defibrillator", StringComparison.OrdinalIgnoreCase) &&
                TryAutoStowHeldItemForTreatment(uid, rescue, out var stowStatus))
            {
                rescue.LastAutoSupplyStatus = stowStatus;
                rescue.NextAutoDefibAttempt = _timing.CurTime;
                Dirty(uid, rescue);
                return true;
            }

            MarkTerminalDefibrillationFailure(
                uid,
                rescue,
                target,
                LuaMRescueFailureReason.NoEffectiveMedicine,
                $"defibrillation equipment unavailable: {status}; transport/handoff fallback");
            return false;
        }

        if (!TryComp<DefibrillatorComponent>(defib, out var defibrillator) ||
            !HasExpectedDefibrillatorBenefit(defibrillator, target))
        {
            MarkTerminalDefibrillationFailure(
                uid,
                rescue,
                target,
                LuaMRescueFailureReason.NoEffectiveMedicine,
                $"defibrillator {FormatEntityRef(defib)} cannot reduce the patient's current damage");
            return false;
        }

        if (!_itemToggle.IsActivated(defib) &&
            !_itemToggle.TryActivate(defib, uid))
        {
            MarkTerminalDefibrillationFailure(
                uid,
                rescue,
                target,
                LuaMRescueFailureReason.NoEffectiveMedicine,
                $"could not activate defibrillator {FormatEntityRef(defib)}; transport/handoff fallback");
            return false;
        }

        if (!_powerCell.HasActivatableCharge(defib, user: uid))
        {
            MarkTerminalDefibrillationFailure(
                uid,
                rescue,
                target,
                LuaMRescueFailureReason.NoDefibrillatorCharge,
                $"defibrillator {FormatEntityRef(defib)} has no usable charge");
            return false;
        }

        if (!_defibrillator.TryStartZap(defib, target, uid))
        {
            var attempts = rescue.DefibrillationAttempts.GetValueOrDefault(target) + 1;
            rescue.DefibrillationAttempts[target] = attempts;
            rescue.DefibrillationStartedAt.TryAdd(target, _timing.CurTime);
            if (attempts >= 3 ||
                _timing.CurTime - rescue.DefibrillationStartedAt[target] >= TimeSpan.FromSeconds(30))
            {
                MarkTerminalDefibrillationFailure(
                    uid,
                    rescue,
                    target,
                    LuaMRescueFailureReason.ActionCancelled,
                    $"defibrillation could not start after {attempts} bounded attempts; transport/handoff fallback");
                return false;
            }

            rescue.LastAutoDefibStatus =
                $"could not start defibrillation attempt {attempts}/3 of {FormatEntityRef(target)} with {FormatEntityRef(defib)}";
            _activity.RecordAttempt(uid, out _);
            Dirty(uid, rescue);
            return false;
        }

        rescue.LastAutoDefibStatus = $"started defibrillation of {FormatEntityRef(target)} with {FormatEntityRef(defib)}";
        rescue.DefibrillationStartedAt.TryAdd(target, _timing.CurTime);
        rescue.DefibrillationAttempts[target] = rescue.DefibrillationAttempts.GetValueOrDefault(target) + 1;
        TrackMedicalDoAfter(uid, rescue, target, defib, "defibrillator");
        TrySendRescueStatusComms(
            uid,
            rescue,
            $"defib-start:{target}",
            $"Дефибрилляция {Name(target)} начата. Не трогайте пациента.");
        Dirty(uid, rescue);
        return true;
    }

    private bool TryStartAutoPickupNearbyMedicalSupply(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        EntityUid target,
        out string status)
    {
        status = string.Empty;

        if (!rescue.AutoPickupNearbyMedicalSupplies ||
            rescue.AutoPickupSupplyRange <= 0f)
        {
            return false;
        }

        if (!TryComp<HandsComponent>(uid, out var hands) ||
            !_hands.TryGetEmptyHand(uid, out _, hands))
        {
            status = "no empty hand for nearby medical supply";
            return false;
        }

        if (!TryFindNearbyMedicalSupply(uid, target, rescue, rescue.AutoPickupSupplyRange, out var supplyUid, out status))
            return false;

        rescue.PendingPlayerAction = LuaMRescuePlayerActionKind.Pickup;
        rescue.PendingPlayerActionTarget = supplyUid;
        rescue.PendingPlayerActionSlot = null;
        rescue.PendingPlayerActionItem = null;
        rescue.PendingVendingStarted = false;
        rescue.PendingVendingProduct = null;
        rescue.PendingVendingDispensedItem = null;
        rescue.PendingStorageTakenItem = null;
        rescue.PlayerActionAccumulator = 0f;
        rescue.LastPlayerActionStatus = $"pending pickup {FormatEntityRef(supplyUid)}";
        SetRescueTask(
            uid,
            rescue,
            LuaMRescueTaskStage.PickingUpSupply,
            target,
            supplyUid,
            $"collecting supply {FormatEntityRef(supplyUid)} for {FormatEntityRef(target)}");
        SetTaskMovementTarget(uid, rescue, htn, supplyUid);

        status = $"collecting nearby {FormatEntityRef(supplyUid)}";
        return true;
    }

    private bool TryFindNearbyMedicalSupply(
        EntityUid uid,
        EntityUid target,
        LuaMRescueAgentComponent rescue,
        float searchRange,
        out EntityUid supplyUid,
        out string status)
    {
        supplyUid = default;
        status = string.Empty;

        var bestScore = float.MinValue;
        foreach (var candidate in _lookup.GetEntitiesInRange(uid, searchRange))
        {
            if (candidate == uid ||
                candidate == target ||
                Deleted(candidate) ||
                IsSupplyTemporarilySkipped(candidate, rescue) ||
                !CanPerceiveAutomaticTarget(uid, candidate, searchRange) ||
                _container.IsEntityOrParentInContainer(candidate))
            {
                continue;
            }

            var itemScore = GetTreatmentItemScore(candidate, target, itemSelector: null, includeDiagnosticItems: false);
            if (itemScore <= 0f ||
                !TryGetNavigationSelectionPenalty(uid, candidate, rescue.PlayerActionRange, out var navPenalty) ||
                !TryGetDistance(uid, candidate, out var distance))
            {
                continue;
            }

            var score = itemScore * 100f - distance - navPenalty;
            if (score <= bestScore)
                continue;

            supplyUid = candidate;
            bestScore = score;
        }

        if (supplyUid is { Valid: true })
            return true;

        status = $"no nearby medical supply found within {searchRange:0.0}m";
        return false;
    }

    private bool TryStartAutoTakeNearbyStoredMedicalSupply(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        EntityUid target,
        out string status)
    {
        status = string.Empty;

        if (!rescue.AutoTakeNearbyStoredMedicalSupplies ||
            rescue.AutoPickupSupplyRange <= 0f)
        {
            return false;
        }

        if (!TryComp<HandsComponent>(uid, out var hands) ||
            !_hands.TryGetEmptyHand(uid, out _, hands))
        {
            status = "no empty hand for nearby stored medical supply";
            return false;
        }

        if (!TryFindNearbyStoredMedicalSupply(
                uid,
                target,
                rescue,
                rescue.AutoPickupSupplyRange,
                out var storageUid,
                out var supplyUid,
                out status))
        {
            return false;
        }

        rescue.PendingPlayerAction = LuaMRescuePlayerActionKind.TakeTargetStorage;
        rescue.PendingPlayerActionTarget = storageUid;
        rescue.PendingPlayerActionSlot = null;
        rescue.PendingPlayerActionItem = GetNetEntity(supplyUid).ToString();
        rescue.PendingVendingStarted = false;
        rescue.PendingVendingProduct = null;
        rescue.PendingVendingDispensedItem = null;
        rescue.PendingStorageTakenItem = null;
        rescue.PlayerActionAccumulator = 0f;
        rescue.LastPlayerActionStatus = $"pending take {FormatEntityRef(supplyUid)} from {FormatEntityRef(storageUid)}";
        SetRescueTask(
            uid,
            rescue,
            LuaMRescueTaskStage.PickingUpSupply,
            target,
            storageUid,
            $"collecting stored supply {FormatEntityRef(supplyUid)} from {FormatEntityRef(storageUid)} for {FormatEntityRef(target)}");
        SetTaskMovementTarget(uid, rescue, htn, storageUid);

        status = $"collecting stored {FormatEntityRef(supplyUid)} from {FormatEntityRef(storageUid)}";
        return true;
    }

    private bool TryFindNearbyStoredMedicalSupply(
        EntityUid uid,
        EntityUid target,
        LuaMRescueAgentComponent rescue,
        float searchRange,
        out EntityUid storageUid,
        out EntityUid supplyUid,
        out string status)
    {
        storageUid = default;
        supplyUid = default;
        status = string.Empty;

        var bestScore = float.MinValue;
        foreach (var candidate in _lookup.GetEntitiesInRange(uid, searchRange))
        {
            if (candidate == uid ||
                candidate == target ||
                Deleted(candidate) ||
                IsSupplyTemporarilySkipped(candidate, rescue) ||
                !CanPerceiveAutomaticTarget(uid, candidate, searchRange) ||
                _container.IsEntityOrParentInContainer(candidate) ||
                !TryComp<StorageComponent>(candidate, out var storage) ||
                !TryCanUseTargetStorage(uid, candidate, storage, out _) ||
                !TrySelectTreatmentItem(
                    uid,
                    candidate,
                    storage,
                    target,
                    itemSelector: null,
                    includeDiagnosticItems: false,
                    out var storedItem,
                    out var actualStorageUid,
                    out _,
                    out _) ||
                Deleted(storedItem) ||
                IsSupplyTemporarilySkipped(storedItem, rescue))
            {
                continue;
            }

            var itemScore = GetTreatmentItemScore(storedItem, target, itemSelector: null, includeDiagnosticItems: false);
            if (itemScore <= 0f ||
                !TryGetNavigationSelectionPenalty(uid, candidate, rescue.PlayerActionRange, out var navPenalty) ||
                !TryGetDistance(uid, candidate, out var distance))
            {
                continue;
            }

            var score = itemScore * 100f - distance - navPenalty;
            if (score <= bestScore)
                continue;

            storageUid = actualStorageUid;
            supplyUid = storedItem;
            bestScore = score;
        }

        if (storageUid is { Valid: true } &&
            supplyUid is { Valid: true })
        {
            return true;
        }

        status = $"no nearby stored medical supply found within {searchRange:0.0}m";
        return false;
    }

    private bool TryStartAutoResupplyFromVending(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        EntityUid target,
        out string status)
    {
        status = string.Empty;

        if (!rescue.AutoResupplyFromVending ||
            rescue.AutoResupplyRange <= 0f)
        {
            return false;
        }

        if (!TryFindMedicalVendingSupply(
                uid,
                target,
                rescue,
                rescue.AutoResupplyRange,
                out var vendingUid,
                out var productId,
                out status))
            return false;

        rescue.PendingPlayerAction = LuaMRescuePlayerActionKind.Vend;
        rescue.PendingPlayerActionTarget = vendingUid;
        rescue.PendingPlayerActionSlot = null;
        rescue.PendingPlayerActionItem = productId;
        rescue.PendingVendingStarted = false;
        rescue.PendingVendingProduct = null;
        rescue.PendingVendingDispensedItem = null;
        rescue.PendingStorageTakenItem = null;
        rescue.PlayerActionAccumulator = 0f;
        rescue.LastPlayerActionStatus = $"pending vend {productId} from {FormatEntityRef(vendingUid)}";
        SetRescueTask(
            uid,
            rescue,
            LuaMRescueTaskStage.VendingSupply,
            target,
            vendingUid,
            $"vending {productId} for {FormatEntityRef(target)}");
        SetTaskMovementTarget(uid, rescue, htn, vendingUid);

        status = $"resupplying {productId} from {FormatEntityRef(vendingUid)}";
        return true;
    }

    private bool TryFindMedicalVendingSupply(
        EntityUid uid,
        EntityUid patient,
        LuaMRescueAgentComponent rescue,
        float searchRange,
        out EntityUid vendingUid,
        out string productId,
        out string status)
    {
        vendingUid = default;
        productId = string.Empty;
        status = string.Empty;

        var bestScore = float.MinValue;
        foreach (var candidate in _lookup.GetEntitiesInRange(uid, searchRange))
        {
            if (IsSupplyTemporarilySkipped(candidate, rescue) ||
                !CanPerceiveAutomaticTarget(uid, candidate, searchRange) ||
                !TryComp<VendingMachineComponent>(candidate, out var vending) ||
                vending.Broken ||
                vending.Ejecting ||
                !TrySelectVendingProduct(
                    candidate,
                    vending,
                    itemSelector: null,
                    includeDiagnosticItems: false,
                    treatmentTarget: patient,
                    out var product,
                    out _))
            {
                continue;
            }

            if (!TryGetDistance(uid, candidate, out var distance))
                continue;

            if (!TryGetNavigationSelectionPenalty(uid, candidate, rescue.PlayerActionRange, out var navPenalty))
                continue;

            var score = GetMedicalVendingProductPatientScore(product.ID, patient, includeDiagnosticItems: false) -
                        distance - navPenalty;
            if (score <= bestScore)
                continue;

            vendingUid = candidate;
            productId = product.ID;
            bestScore = score;
        }

        if (vendingUid is { Valid: true })
            return true;

        status = $"no medical vending supply found within {searchRange:0.0}m";
        return false;
    }

    private bool IsRescueCandidate(
        EntityUid rescuer,
        EntityUid candidate,
        MedibotComponent medibot,
        bool requireRange,
        float searchRange,
        out float score)
    {
        score = 0f;

        if (!_activity.IsEligibleRescuePatient(
                rescuer,
                candidate,
                LuaMRescuePatientRequestKind.AutomaticTreatment,
                manualOverride: false,
                out _) ||
            !TryComp<MobStateComponent>(candidate, out var mobState) ||
            !TryComp<DamageableComponent>(candidate, out var damage))
        {
            return false;
        }

        if (mobState.CurrentState == MobState.Dead ||
            !_medibot.TryGetTreatment(medibot, mobState.CurrentState, out var treatment) ||
            !treatment.IsValid(damage.TotalDamage))
        {
            return false;
        }

        var rescuerCoordinates = Transform(rescuer).Coordinates;
        var candidateCoordinates = Transform(candidate).Coordinates;
        if (!rescuerCoordinates.TryDistance(EntityManager, candidateCoordinates, out var distance))
            return false;

        if (requireRange && distance > searchRange)
            return false;

        if (!TryGetNavigationSelectionPenalty(rescuer, candidate, SharedInteractionSystem.InteractionRange, out var navPenalty))
            return false;

        var priority = _activity.GetPatientPriority(
            rescuer,
            candidate,
            LuaMRescuePatientRequestKind.AutomaticTreatment,
            manualOverride: false,
            out _);
        score = priority - distance - navPenalty;

        return true;
    }

    private bool HasAutoAcquireAcuity(EntityUid target, LuaMRescueAgentComponent rescue)
    {
        if (!TryComp<MobStateComponent>(target, out var mobState))
            return false;

        if (mobState.CurrentState == MobState.Critical)
            return true;

        return TryComp<DamageableComponent>(target, out var damage) &&
               damage.TotalDamage.Float() >= rescue.AutoAcquireMinDamage;
    }

    private bool NeedsEvacuation(EntityUid uid, EntityUid target, LuaMRescueAgentComponent rescue)
    {
        var requiredHandoff = rescue.RequiredOnboardHandoffPatients.Contains(target);
        if (!rescue.EvacuateTargetsToShuttle ||
            !CanUseAssignedShuttle(rescue) ||
            !TryComp<MobStateComponent>(target, out var mobState) ||
            !HasComp<PullableComponent>(target))
        {
            return false;
        }

        if (requiredHandoff)
            return true;

        if (IsTargetTemporarilySkipped(uid, target, rescue) ||
            IsEvacuationComplete(uid, target, rescue))
        {
            return false;
        }

        if (mobState.CurrentState == MobState.Dead)
            return IsDeadPatientRecoveryTarget(uid, target, rescue, mobState);

        if (IsThreatenedEvacuationTarget(uid, target, rescue))
            return true;

        if (!TryComp<DamageableComponent>(target, out var damage))
            return false;

        return mobState.CurrentState == MobState.Critical ||
               damage.TotalDamage.Float() >= rescue.EvacuationMinDamage;
    }

    private bool IsThreatenedEvacuationTarget(EntityUid uid, EntityUid target, LuaMRescueAgentComponent rescue)
    {
        if (!rescue.EvacuateWhenSceneThreatened ||
            !CanUseAssignedShuttle(rescue) ||
            !TryComp<LuaMRescueTeamComponent>(uid, out var team) ||
            !HasRescueTeamThreatPressure(team) ||
            !HasComp<PullableComponent>(target) ||
            !TryComp<MobStateComponent>(target, out var mobState) ||
            mobState.CurrentState == MobState.Dead)
        {
            return false;
        }

        if (team.Patient is { Valid: true } patient &&
            patient != target &&
            rescue.AssignedTarget != target &&
            rescue.EvacuatingTarget != target &&
            rescue.TaskPatientTarget != target)
        {
            return false;
        }

        if (HasRescueTeamOverwhelmingThreatPressure(team, rescue))
            return true;

        if (mobState.CurrentState == MobState.Critical)
            return true;

        return TryComp<DamageableComponent>(target, out var damage) &&
               damage.TotalDamage.Float() >= rescue.ThreatEvacuationMinDamage;
    }

    private static bool HasRescueTeamThreatPressure(LuaMRescueTeamComponent team)
    {
        return team.ThreatTarget is { Valid: true } ||
               team.NearbyHostiles > 0 ||
               team.NearbyCombatants > 0 ||
               team.RecentThreatMemories > 0;
    }

    private static bool HasRescueTeamOverwhelmingThreatPressure(
        LuaMRescueTeamComponent team,
        LuaMRescueAgentComponent rescue)
    {
        return (rescue.OverwhelmingThreatHostileThreshold > 0 &&
                team.NearbyHostiles >= rescue.OverwhelmingThreatHostileThreshold) ||
               (rescue.OverwhelmingThreatCombatantThreshold > 0 &&
                team.NearbyCombatants >= rescue.OverwhelmingThreatCombatantThreshold);
    }

    private static bool HasRescueTeamRoutePressure(LuaMRescueTeamComponent team)
    {
        return team.RouteBlockerTarget is { Valid: true } ||
               team.NearbyBlockers >= 2;
    }

    private bool IsDeadPatientRecoveryTarget(
        EntityUid rescuer,
        EntityUid target,
        LuaMRescueAgentComponent rescue,
        MobStateComponent mobState)
    {
        return rescue.RecoverDeadPatientsToShuttle &&
               mobState.CurrentState == MobState.Dead &&
               _activity.IsEligibleRescuePatient(
                   rescuer,
                   target,
                   IsManualPatientIntent(rescue, target)
                       ? LuaMRescuePatientRequestKind.Manual
                       : LuaMRescuePatientRequestKind.AutomaticEvacuation,
                   IsManualPatientIntent(rescue, target),
                   out _);
    }

    private bool IsEvacuationCandidate(
        EntityUid rescuer,
        EntityUid candidate,
        LuaMRescueAgentComponent rescue,
        float searchRange,
        out float score)
    {
        score = 0f;

        if (!_activity.IsEligibleRescuePatient(
                rescuer,
                candidate,
                LuaMRescuePatientRequestKind.AutomaticEvacuation,
                manualOverride: false,
                out _) ||
            !NeedsEvacuation(rescuer, candidate, rescue))
        {
            return false;
        }

        var rescuerCoordinates = Transform(rescuer).Coordinates;
        var candidateCoordinates = Transform(candidate).Coordinates;
        if (!rescuerCoordinates.TryDistance(EntityManager, candidateCoordinates, out var distance) ||
            distance > searchRange)
        {
            return false;
        }

        if (!TryComp<MobStateComponent>(candidate, out var mobState))
        {
            return false;
        }

        if (!TryGetNavigationSelectionPenalty(rescuer, candidate, SharedInteractionSystem.InteractionRange, out var navPenalty))
            return false;

        score = 0f - distance - navPenalty;
        if (TryComp<DamageableComponent>(candidate, out var damage))
            score += damage.TotalDamage.Float();

        if (IsThreatenedEvacuationTarget(rescuer, candidate, rescue))
            score += 750f;

        if (mobState.CurrentState == MobState.Dead)
            score += 1500f;

        if (mobState.CurrentState == MobState.Critical)
            score += 1000f;

        return true;
    }

    private bool CanUseAssignedShuttle(LuaMRescueAgentComponent rescue)
    {
        return rescue.EvacuateTargetsToShuttle &&
               rescue.AssignedShuttle is { Valid: true } shuttle &&
               !Deleted(shuttle);
    }

    private bool CanUseAssignedShuttleForLifeSupport(LuaMRescueAgentComponent rescue)
    {
        return rescue.AssignedShuttle is { Valid: true } shuttle &&
               !Deleted(shuttle) &&
               TryGetShuttleAnchorCoordinates(rescue, out _);
    }

    private bool IsOnAssignedShuttle(EntityUid target, LuaMRescueAgentComponent rescue)
    {
        return rescue.AssignedShuttle is { Valid: true } shuttle &&
               !Deleted(shuttle) &&
               Transform(target).GridUid == shuttle;
    }

    private bool IsAtAssignedShuttleAnchor(EntityUid target, LuaMRescueAgentComponent rescue)
    {
        if (!TryGetShuttleAnchorCoordinates(rescue, out var coordinates))
            return false;

        var targetCoordinates = Transform(target).Coordinates;
        return targetCoordinates.TryDistance(EntityManager, coordinates, out var distance) &&
               distance <= rescue.EvacuationArrivalRange;
    }

    private bool IsPullingTarget(EntityUid uid, EntityUid target)
    {
        return TryComp<PullerComponent>(uid, out var puller) &&
               puller.Pulling == target;
    }

    private void StopPullingTarget(EntityUid uid, EntityUid target)
    {
        if (!IsPullingTarget(uid, target) ||
            !TryComp<PullableComponent>(target, out var pullable))
        {
            return;
        }

        _pulling.TryStopPull(target, pullable);
    }

    private bool TryRouteShuttleHome(EntityUid uid, LuaMRescueAgentComponent rescue)
    {
        if (!rescue.AutoReturnShuttle)
            return SetShuttleReturnStatus(uid, rescue, "return-route disabled");

        if (rescue.AssignedShuttle is not { Valid: true } shuttle || Deleted(shuttle))
        {
            return ReportShuttleReturnHold(uid, rescue, "shuttle unavailable");
        }

        if (rescue.AssignedReturnTarget is not { Valid: true } returnTarget ||
            Deleted(returnTarget))
        {
            return ReportShuttleReturnHold(uid, rescue, "return target unavailable");
        }

        var lifecycle = EnsureComp<LuaMRescueShuttleLifecycleComponent>(shuttle);
        if (lifecycle.Target == returnTarget &&
            lifecycle.RouteActivity == LuaMRescueActivity.Returning &&
            lifecycle.State != LuaMRescueShuttleRouteState.None)
        {
            // An existing return route, including its failed/timed-out recovery
            // window, belongs to the lifecycle. Do not reissue it from Standby
            // and thereby bypass the bounded retry policy.
            rescue.ShuttleReturnRouted = true;
            rescue.ShuttleRoutedTarget = null;
            if (lifecycle.State is LuaMRescueShuttleRouteState.Failed or LuaMRescueShuttleRouteState.TimedOut &&
                lifecycle.RetryCount >= lifecycle.EffectiveMaxRetries)
            {
                return ReportShuttleReturnHold(uid, rescue, $"{lifecycle.State}: {lifecycle.LastStatus}");
            }

            rescue.LastShuttleReturnStatus =
                $"return-route {lifecycle.State}: target={FormatEntityRef(returnTarget)}; {lifecycle.LastStatus}";
            Dirty(uid, rescue);
            return true;
        }

        // The flag is only a cache. A changed/missing lifecycle must be allowed
        // to create one new authoritative route.
        rescue.ShuttleReturnRouted = false;
        var routeRequest = new LuaMRescueShuttleRouteRequestEvent(
            returnTarget,
            LuaMRescueActivity.Returning);
        RaiseLocalEvent(shuttle, ref routeRequest);
        if (!routeRequest.Handled)
        {
            // TryIssueAutopilotRoute still creates a retryable lifecycle when its
            // first low-level issue fails (for example, a console is temporarily
            // unavailable). Remember that ownership instead of issuing every tick.
            if (lifecycle.Target == returnTarget &&
                lifecycle.RouteActivity == LuaMRescueActivity.Returning &&
                lifecycle.State is LuaMRescueShuttleRouteState.Failed or LuaMRescueShuttleRouteState.TimedOut &&
                lifecycle.RetryCount < lifecycle.EffectiveMaxRetries)
            {
                rescue.ShuttleReturnRouted = true;
                rescue.ShuttleRoutedTarget = null;
                rescue.LastShuttleReturnStatus =
                    $"return-route recovery pending: target={FormatEntityRef(returnTarget)}; {routeRequest.Status}";
                Dirty(uid, rescue);
                return true;
            }

            return ReportShuttleReturnHold(uid, rescue, routeRequest.Status);
        }

        rescue.AssignedShuttleConsole = routeRequest.AutopilotConsole;
        rescue.LastShuttleReturnStatus =
            $"return-route home: target={FormatEntityRef(returnTarget)}; {routeRequest.Status}";
        rescue.ShuttleReturnRouted = true;
        rescue.ShuttleRoutedTarget = null;
        Dirty(uid, rescue);
        return true;
    }

    private bool SetShuttleReturnStatus(EntityUid uid, LuaMRescueAgentComponent rescue, string status)
    {
        if (string.Equals(rescue.LastShuttleReturnStatus, status, StringComparison.Ordinal))
            return false;

        rescue.LastShuttleReturnStatus = status;
        Dirty(uid, rescue);
        return false;
    }

    private bool ReportShuttleReturnHold(EntityUid uid, LuaMRescueAgentComponent rescue, string reason)
    {
        var status = $"return-route hold: {reason}; repair/manual shuttle help needed";
        rescue.LastShuttleReturnStatus = status;
        rescue.LastAutoEvacuationStatus = status;
        TrySendRescueStatusComms(
            uid,
            rescue,
            $"shuttle-return-hold:{reason}:{rescue.AssignedShuttle}",
            BuildShuttleReturnHoldMessage(reason));
        return false;
    }

    private static string BuildShuttleReturnHoldMessage(string reason)
    {
        return $"\u0428\u0430\u0442\u0442\u043b \u043d\u0435 \u0433\u043e\u0442\u043e\u0432 \u043a \u0432\u043e\u0437\u0432\u0440\u0430\u0442\u0443: {reason}. \u0423\u0434\u0435\u0440\u0436\u0438\u0432\u0430\u044e \u043c\u0435\u0434\u0438\u0446\u0438\u043d\u0441\u043a\u0438\u0439 \u0431\u043e\u0440\u0442; \u043d\u0443\u0436\u043d\u044b \u0440\u0435\u043c\u043e\u043d\u0442 \u0438\u043b\u0438 \u0440\u0443\u0447\u043d\u043e\u0439 \u043c\u0430\u0440\u0448\u0440\u0442.";
    }

    private bool TryRouteShuttleToTarget(EntityUid uid, LuaMRescueAgentComponent rescue, EntityUid target)
    {
        if (!rescue.AutoRouteShuttleToTargets ||
            IsOnAssignedShuttle(target, rescue) ||
            IsEvacuationComplete(uid, target, rescue) ||
            rescue.AssignedShuttle is not { Valid: true } shuttle ||
            Deleted(shuttle) ||
            Deleted(target))
        {
            return false;
        }

        var currentLifecycle = EnsureComp<LuaMRescueShuttleLifecycleComponent>(shuttle);
        if (currentLifecycle.Target == target &&
            currentLifecycle.RouteActivity == LuaMRescueActivity.Delivering &&
            currentLifecycle.State != LuaMRescueShuttleRouteState.None)
        {
            // The shuttle lifecycle is the sole owner of retries for an existing
            // route. Reissuing here on every target refresh would bypass its
            // backoff and retry counter, especially while Failed or TimedOut.
            rescue.ShuttleRoutedTarget = target;
            rescue.ShuttleReturnRouted = false;
            return true;
        }

        var routeRequest = new LuaMRescueShuttleRouteRequestEvent(
            target,
            LuaMRescueActivity.Delivering);
        RaiseLocalEvent(shuttle, ref routeRequest);
        if (!routeRequest.Handled)
        {
            if (currentLifecycle.Target == target &&
                currentLifecycle.RouteActivity == LuaMRescueActivity.Delivering &&
                currentLifecycle.State is LuaMRescueShuttleRouteState.Failed or LuaMRescueShuttleRouteState.TimedOut &&
                currentLifecycle.RetryCount < currentLifecycle.EffectiveMaxRetries)
            {
                // A failed low-level issue still created a lifecycle with a due
                // retry. Adopt it now so the next target refresh cannot reissue
                // attempt zero forever.
                rescue.ShuttleRoutedTarget = target;
                rescue.ShuttleReturnRouted = false;
                rescue.LastAutoEvacuationStatus =
                    $"shuttle route recovery pending for {FormatEntityRef(target)}; {routeRequest.Status}";
                Dirty(uid, rescue);
                return true;
            }

            rescue.LastAutoEvacuationStatus =
                $"ShuttleRouteFailed: target={FormatEntityRef(target)}; {routeRequest.Status}";
            _activity.Block(
                uid,
                LuaMRescueFailureReason.ShuttleRouteFailed,
                LuaMRescueActivity.Standby,
                out _);
            Dirty(uid, rescue);
            return false;
        }

        rescue.AssignedShuttleConsole = routeRequest.AutopilotConsole;
        rescue.ShuttleRoutedTarget = target;
        rescue.ShuttleReturnRouted = false;
        rescue.LastAutoEvacuationStatus =
            $"shuttle routing to {FormatEntityRef(target)}; {routeRequest.Status}";
        Dirty(uid, rescue);
        return true;
    }

    private bool CanPerceiveAutomaticTarget(EntityUid observer, EntityUid candidate, float range)
    {
        return observer.Valid &&
               candidate.Valid &&
               !Deleted(observer) &&
               !Deleted(candidate) &&
               _examine.CanExamine(observer, candidate) &&
               TryGetDistance(observer, candidate, out var distance) &&
               distance <= Math.Max(0.05f, range);
    }

    private bool IsWithinRange(EntityUid uid, EntityUid target, float range)
    {
        return !Deleted(uid) &&
               !Deleted(target) &&
               _interaction.InRangeUnobstructed(uid, target, Math.Max(0.05f, range));
    }

    private void PruneSkippedTargets(LuaMRescueAgentComponent rescue)
    {
        if (rescue.SkippedTargets.Count == 0)
            return;

        var now = _timing.CurTime;
        foreach (var target in rescue.SkippedTargets
                     .Where(entry => Deleted(entry.Key) || entry.Value <= now)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            rescue.SkippedTargets.Remove(target);
            if (Deleted(target) ||
                !rescue.TerminalTreatmentFailures.ContainsKey(target))
            {
                continue;
            }

            ClearTreatmentFailure(rescue, target);
            rescue.LastAutoTreatmentStatus =
                $"treatment retry re-armed after bounded specialist handoff delay; patient={FormatEntityRef(target)}";
        }
    }

    private void PruneSkippedSupplyTargets(LuaMRescueAgentComponent rescue)
    {
        if (rescue.SkippedSupplyTargets.Count == 0)
            return;

        var now = _timing.CurTime;
        foreach (var target in rescue.SkippedSupplyTargets
                     .Where(entry => Deleted(entry.Key) || entry.Value <= now)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            rescue.SkippedSupplyTargets.Remove(target);
        }
    }

    private void PruneSkippedDeliveryTargets(LuaMRescueAgentComponent rescue)
    {
        if (rescue.SkippedDeliveryTargets.Count == 0)
            return;

        var now = _timing.CurTime;
        foreach (var target in rescue.SkippedDeliveryTargets
                     .Where(entry => Deleted(entry.Key) || entry.Value <= now)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            rescue.SkippedDeliveryTargets.Remove(target);
        }
    }

    private void PruneAnalyzedTargets(LuaMRescueAgentComponent rescue)
    {
        if (rescue.AnalyzedTargets.Count == 0)
            return;

        var now = _timing.CurTime;
        foreach (var target in rescue.AnalyzedTargets
                     .Where(entry => Deleted(entry.Key) || entry.Value <= now)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            rescue.AnalyzedTargets.Remove(target);
        }
    }

    private bool WasTargetRecentlyAnalyzed(EntityUid target, LuaMRescueAgentComponent rescue)
    {
        if (!rescue.AnalyzedTargets.TryGetValue(target, out var analyzeUntil))
            return false;

        if (_timing.CurTime < analyzeUntil)
            return true;

        rescue.AnalyzedTargets.Remove(target);
        return false;
    }

    private bool IsTargetTemporarilySkipped(
        EntityUid rescuer,
        EntityUid target,
        LuaMRescueAgentComponent rescue)
    {
        if (rescue.DormantRouteTarget == target)
            return true;

        if (rescue.SkippedTargets.TryGetValue(target, out var durableSkipUntil) &&
            durableSkipUntil == TimeSpan.MaxValue)
        {
            return true;
        }

        // Terminal interaction failures are durable until an explicit
        // administrative order clears them. A short skip TTL alone would let the
        // same impossible pull/unbuckle mission reacquire forever.
        if (rescue.TerminalPullFailures.ContainsKey(target) ||
            rescue.TerminalEvacuationUnbuckleFailures.ContainsKey(target))
        {
            return true;
        }

        if (rescue.TerminalPatientBuckleFailures.Keys.Any(pair => pair.Patient == target) &&
            ArePatientDeliveryStrapsAuthoritativelyExhausted(rescuer, rescue, target))
        {
            // Once every alternate strap has an authoritative terminal route
            // result, latch the patient as durable. Holding custody tears down
            // the old movement probes; without this latch their recreated
            // Pending state would otherwise wake the same failed delivery on
            // every refresh.
            if (!rescue.SkippedTargets.TryGetValue(target, out var durableSkip) ||
                durableSkip != TimeSpan.MaxValue)
            {
                rescue.SkippedTargets[target] = TimeSpan.MaxValue;
                Dirty(rescuer, rescue);
            }

            return true;
        }

        if (!rescue.SkippedTargets.TryGetValue(target, out var skipUntil))
            return false;

        if (_timing.CurTime < skipUntil)
            return true;

        rescue.SkippedTargets.Remove(target);
        return false;
    }

    private bool IsSupplyTemporarilySkipped(EntityUid target, LuaMRescueAgentComponent rescue)
    {
        if (!rescue.SkippedSupplyTargets.TryGetValue(target, out var skipUntil))
            return false;

        if (_timing.CurTime < skipUntil)
            return true;

        rescue.SkippedSupplyTargets.Remove(target);
        return false;
    }

    private bool IsDeliveryTargetTemporarilySkipped(EntityUid target, LuaMRescueAgentComponent rescue)
    {
        if (!rescue.SkippedDeliveryTargets.TryGetValue(target, out var skipUntil))
            return false;

        if (_timing.CurTime < skipUntil)
            return true;

        rescue.SkippedDeliveryTargets.Remove(target);
        return false;
    }

    private bool TryHoldRouteBlockedEvacuation(
        EntityUid uid,
        EntityUid target,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn)
    {
        if (!rescue.HoldPositionOnBlockedEvacuation ||
            rescue.RouteBlockHoldSeconds <= 0f ||
            !IsPullingTarget(uid, target) ||
            !TryComp<LuaMRescueTeamComponent>(uid, out var team) ||
            !HasRescueTeamRoutePressure(team) ||
            !TryGetActiveDeliveryGoal(rescue, out var progressGoal))
        {
            ClearRouteBlockHold(rescue);
            return false;
        }

        if (rescue.RouteBlockHelpRequested &&
            rescue.RouteBlockHoldTarget == target &&
            rescue.RouteBlockHoldGoal == progressGoal)
        {
            return false;
        }

        var now = _timing.CurTime;
        if (rescue.RouteBlockHoldTarget != target ||
            rescue.RouteBlockHoldGoal != progressGoal)
        {
            rescue.RouteBlockHoldTarget = target;
            rescue.RouteBlockHoldGoal = progressGoal;
            rescue.RouteBlockHoldStartedAt = now;
            rescue.RouteBlockHelpRequested = false;
            rescue.LastRouteBlockHoldStatus = $"hold-position route blocked; target={FormatEntityRef(target)}; goal={FormatEntityRef(progressGoal)}";

            TrySendRescueStatusComms(
                uid,
                rescue,
                $"route-blocked-hold:{target}:{progressGoal}",
                $"\u041c\u0430\u0440\u0448\u0440\u0443\u0442 \u044d\u0432\u0430\u043a\u0443\u0430\u0446\u0438\u0438 \u0437\u0430\u0431\u043b\u043e\u043a\u0438\u0440\u043e\u0432\u0430\u043d. \u0414\u0435\u0440\u0436\u0443 \u043f\u043e\u0437\u0438\u0446\u0438\u044e \u0441 {Name(target)}; \u043d\u0443\u0436\u0435\u043d \u043a\u043e\u0440\u0438\u0434\u043e\u0440 \u043a \u0448\u0430\u0442\u0442\u043b\u0443.");
        }

        var elapsed = Math.Max(0f, (float) (now - rescue.RouteBlockHoldStartedAt).TotalSeconds);
        if (elapsed < rescue.RouteBlockHoldSeconds)
        {
            rescue.LastAutoEvacuationStatus =
                $"hold-position route blocked for {elapsed:0.0}/{rescue.RouteBlockHoldSeconds:0.0}s; {team.LastSceneStatus}; {team.LastMemoryDigest}";
            rescue.LastRouteBlockHoldStatus =
                $"holding; target={FormatEntityRef(target)}; goal={FormatEntityRef(progressGoal)}; elapsed={elapsed:0.0}/{rescue.RouteBlockHoldSeconds:0.0}s";
            SetRescueTask(
                uid,
                rescue,
                LuaMRescueTaskStage.DeliveringPatient,
                target,
                progressGoal,
                $"hold-position route blocked for {FormatEntityRef(target)}");
            SetFollowTarget(uid, rescue, htn, target);
            return true;
        }

        rescue.RouteBlockHelpRequested = true;
        return TryRerouteBlockedDeliveryTarget(uid, target, rescue, htn, progressGoal);
    }

    private bool TryGetActiveDeliveryGoal(LuaMRescueAgentComponent rescue, out EntityUid goal)
    {
        if (rescue.AssignedPatientStrap is { Valid: true } patientStrap &&
            !Deleted(patientStrap))
        {
            goal = patientStrap;
            return true;
        }

        if (rescue.AssignedShuttleAnchor is { Valid: true } anchor &&
            !Deleted(anchor))
        {
            goal = anchor;
            return true;
        }

        if (rescue.AssignedShuttle is { Valid: true } shuttle &&
            !Deleted(shuttle))
        {
            goal = shuttle;
            return true;
        }

        goal = default;
        return false;
    }

    private bool TryRerouteBlockedDeliveryTarget(
        EntityUid uid,
        EntityUid target,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        EntityUid blockedGoal)
    {
        if (rescue.AssignedPatientStrap == blockedGoal)
        {
            TemporarilySkipDeliveryTarget(rescue, blockedGoal, "route blocked hold-position");
            ResetTargetProgress(rescue);

            if (TryFindPatientDeliveryStrap(uid, rescue, out var replacementStrap, out _))
            {
                rescue.AssignedPatientStrap = replacementStrap;
                rescue.LastAutoEvacuationStatus =
                    $"rerouting blocked evacuation of {FormatEntityRef(target)} to alternate delivery target {FormatEntityRef(replacementStrap)}";
                rescue.LastRouteBlockHoldStatus =
                    $"reroute alternate; target={FormatEntityRef(target)}; blockedGoal={FormatEntityRef(blockedGoal)}; replacement={FormatEntityRef(replacementStrap)}";

                TrySendRescueStatusComms(
                    uid,
                    rescue,
                    $"route-blocked-reroute:{target}:{blockedGoal}:{replacementStrap}",
                    $"\u041a\u043e\u0440\u0438\u0434\u043e\u0440 \u043d\u0435 \u043e\u0442\u043a\u0440\u044b\u0442. \u041c\u0435\u043d\u044f\u044e \u0442\u043e\u0447\u043a\u0443 \u0434\u043e\u0441\u0442\u0430\u0432\u043a\u0438 {Name(target)}.");

                SetRescueTask(
                    uid,
                    rescue,
                    LuaMRescueTaskStage.DeliveringPatient,
                    target,
                    replacementStrap,
                    $"rerouting blocked evacuation of {FormatEntityRef(target)} to {FormatEntityRef(replacementStrap)}");
                return SetFollowDeliveryStrap(uid, rescue, htn, replacementStrap);
            }
        }

        return TryFallbackToShuttleExtraction(uid, target, rescue, htn, blockedGoal);
    }

    private bool TryFallbackToShuttleExtraction(
        EntityUid uid,
        EntityUid target,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        EntityUid blockedGoal)
    {
        rescue.AssignedPatientStrap = null;

        var fallbackTarget = rescue.AssignedShuttleAnchor ?? rescue.AssignedShuttle;
        rescue.LastAutoEvacuationStatus =
            $"route blocked for {FormatEntityRef(target)}; requesting route help and falling back to shuttle extraction";
        rescue.LastRouteBlockHoldStatus =
            $"fallback-extraction; target={FormatEntityRef(target)}; blockedGoal={FormatEntityRef(blockedGoal)}; fallback={FormatEntityRef(fallbackTarget)}";

        TrySendRescueStatusComms(
            uid,
            rescue,
            $"route-blocked-help:{target}:{blockedGoal}",
            $"\u0411\u043b\u043e\u043a \u043c\u0430\u0440\u0448\u0440\u0443\u0442\u0430 \u043d\u0435 \u0441\u043d\u044f\u0442. \u041d\u0443\u0436\u043d\u0430 \u043f\u043e\u043c\u043e\u0449\u044c \u0441 \u043a\u043e\u0440\u0438\u0434\u043e\u0440\u043e\u043c \u0438\u043b\u0438 \u0434\u043e\u0441\u0442\u0443\u043f\u043e\u043c \u043a \u0448\u0430\u0442\u0442\u043b\u0443; \u043f\u0440\u043e\u0431\u0443\u044e \u0440\u0435\u0437\u0435\u0440\u0432\u043d\u044b\u0439 \u0432\u044b\u0432\u043e\u0437.");

        SetRescueTask(
            uid,
            rescue,
            LuaMRescueTaskStage.DeliveringPatient,
            target,
            fallbackTarget,
            $"fallback extraction after blocked route for {FormatEntityRef(target)}");

        if (SetFollowShuttle(uid, rescue, htn))
            return true;

        rescue.LastAutoEvacuationStatus =
            $"route blocked for {FormatEntityRef(target)}; fallback extraction waiting for shuttle access";
        rescue.LastRouteBlockHoldStatus =
            $"fallback-extraction-waiting; target={FormatEntityRef(target)}; blockedGoal={FormatEntityRef(blockedGoal)}; noShuttleFollow";

        TrySendRescueStatusComms(
            uid,
            rescue,
            $"route-blocked-extract:{target}:{blockedGoal}",
            $"\u0420\u0435\u0437\u0435\u0440\u0432\u043d\u044b\u0439 \u0432\u044b\u0432\u043e\u0437 \u043d\u0435 \u043f\u043e\u0441\u0442\u0440\u043e\u0435\u043d. \u0423\u0434\u0435\u0440\u0436\u0438\u0432\u0430\u044e {Name(target)}; \u043d\u0443\u0436\u0435\u043d \u0440\u0443\u0447\u043d\u043e\u0439 \u043a\u043e\u0440\u0438\u0434\u043e\u0440 \u0438\u043b\u0438 \u0434\u043e\u0441\u0442\u0443\u043f \u043a \u0448\u0430\u0442\u0442\u043b\u0443.");

        SetFollowTarget(uid, rescue, htn, target);
        return true;
    }

    private bool TryHandleStalledDeliveryTarget(
        EntityUid uid,
        EntityUid target,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn)
    {
        if (!IsPullingTarget(uid, target) ||
            rescue.ProgressGoal is not { Valid: true } progressGoal ||
            rescue.AssignedPatientStrap != progressGoal)
        {
            return false;
        }

        TemporarilySkipDeliveryTarget(rescue, progressGoal, "stalled delivery route");
        ResetTargetProgress(rescue);

        if (TryFindPatientDeliveryStrap(uid, rescue, out var replacementStrap, out _))
        {
            rescue.AssignedPatientStrap = replacementStrap;
            SetRescueTask(
                uid,
                rescue,
                LuaMRescueTaskStage.DeliveringPatient,
                target,
                replacementStrap,
                $"rerouting {FormatEntityRef(target)} to alternate delivery target {FormatEntityRef(replacementStrap)}");
            return SetFollowDeliveryStrap(uid, rescue, htn, replacementStrap);
        }

        return TryFallbackToShuttleExtraction(uid, target, rescue, htn, progressGoal);
    }

    private bool UpdateTargetProgress(EntityUid uid, EntityUid target, LuaMRescueAgentComponent rescue)
    {
        // A stationary medical DoAfter is real mission progress, not a failed route.
        if (HasActiveMedicalDoAfter(uid, target, out var medicalAction))
        {
            rescue.TargetStallAccumulator = 0f;
            SetTargetTrackingStatus(
                uid,
                rescue,
                $"medical_progress: target={FormatEntityRef(target)}; action={medicalAction}");
            return false;
        }

        if (!rescue.TemporarilySkipStalledTargets ||
            rescue.TargetStallSeconds <= 0f ||
            rescue.TargetSkipSeconds <= 0f ||
            !TryGetEvacuationProgressDistance(uid, target, rescue, out var progressGoal, out var distance))
        {
            return false;
        }

        if (rescue.ProgressTarget != target ||
            rescue.ProgressGoal != progressGoal)
        {
            rescue.ProgressTarget = target;
            rescue.ProgressGoal = progressGoal;
            rescue.LastProgressDistance = distance;
            rescue.TargetStallAccumulator = 0f;
            SetTargetTrackingStatus(
                uid,
                rescue,
                $"route_clear: target={FormatEntityRef(target)}; goal={FormatEntityRef(progressGoal)}; distance={distance:0.0}");
            return false;
        }

        if (distance + rescue.TargetProgressTolerance < rescue.LastProgressDistance)
        {
            rescue.LastProgressDistance = distance;
            rescue.TargetStallAccumulator = 0f;
            if (!HasDurableRouteFailure(rescue, target))
                rescue.RouteFailureAttempts.Remove(target);
            SetTargetTrackingStatus(
                uid,
                rescue,
                $"route_clear: target={FormatEntityRef(target)}; goal={FormatEntityRef(progressGoal)}; distance={distance:0.0}");
            return false;
        }

        if (distance > rescue.LastProgressDistance + rescue.TargetProgressTolerance)
            rescue.LastProgressDistance = distance;

        rescue.TargetStallAccumulator += rescue.TargetRefreshInterval;
        SetTargetTrackingStatus(
            uid,
            rescue,
            $"route_delayed: target={FormatEntityRef(target)}; goal={FormatEntityRef(progressGoal)}; stalled={rescue.TargetStallAccumulator:0.0}/{rescue.TargetStallSeconds:0.0}s; distance={distance:0.0}");
        return rescue.TargetStallAccumulator >= rescue.TargetStallSeconds;
    }

    private bool TryGetEvacuationProgressDistance(
        EntityUid uid,
        EntityUid target,
        LuaMRescueAgentComponent rescue,
        out EntityUid? progressGoal,
        out float distance)
    {
        if (IsPullingTarget(uid, target))
        {
            if (rescue.AssignedPatientStrap is { Valid: true } patientStrap &&
                !Deleted(patientStrap))
            {
                progressGoal = patientStrap;
                return TryGetDistance(target, patientStrap, out distance);
            }

            if (rescue.AssignedShuttleAnchor is { Valid: true } anchor &&
                !Deleted(anchor))
            {
                progressGoal = anchor;
                return TryGetDistance(target, anchor, out distance);
            }

            if (rescue.AssignedShuttle is { Valid: true } shuttle &&
                !Deleted(shuttle))
            {
                progressGoal = shuttle;
                return TryGetDistance(target, shuttle, out distance);
            }
        }

        progressGoal = target;
        return TryGetDistance(uid, target, out distance);
    }

    private bool TryGetDistance(EntityUid first, EntityUid second, out float distance)
    {
        var firstCoordinates = Transform(first).Coordinates;
        var secondCoordinates = Transform(second).Coordinates;
        return firstCoordinates.TryDistance(EntityManager, secondCoordinates, out distance);
    }

    private bool TryGetNavigationSelectionPenalty(
        EntityUid uid,
        EntityUid target,
        float directAllowRange,
        out float penalty)
    {
        penalty = 0f;

        if (!TryGetDistance(uid, target, out var directDistance))
            return false;

        var route = _rescueNavigation.ProbeRoute(uid, target, directAllowRange);
        switch (route.State)
        {
            case LuaMRescuePathProbeState.Reachable:
                // Prefer an equally urgent route without an access interaction, while
                // keeping access-authorized doors fully traversable.
                penalty = route.RequiresAccess ? 5f : 0f;
                return true;
            case LuaMRescuePathProbeState.Pending:
                if (TryComp<LuaMRescueAgentComponent>(uid, out var pendingRescue) &&
                    TryGetActivePatientTarget(pendingRescue, out var pendingTarget) &&
                    pendingTarget == target)
                {
                    SetTargetTrackingStatus(
                        uid,
                        pendingRescue,
                        $"route_planning: target={FormatEntityRef(target)}; distance={directDistance:0.0}");
                }

                return false;
            case LuaMRescuePathProbeState.DifferentGrid:
            case LuaMRescuePathProbeState.NoPath:
            case LuaMRescuePathProbeState.AccessDenied:
            case LuaMRescuePathProbeState.NoLineOfSight:
            case LuaMRescuePathProbeState.Invalid:
                if (TryComp<LuaMRescueAgentComponent>(uid, out var blockedRescue) &&
                    TryGetActivePatientTarget(blockedRescue, out var blockedTarget) &&
                    blockedTarget == target)
                {
                    var reason = route.State switch
                    {
                        LuaMRescuePathProbeState.DifferentGrid => "different-grid; waiting for shuttle docking",
                        LuaMRescuePathProbeState.AccessDenied => "AccessDenied",
                        LuaMRescuePathProbeState.NoLineOfSight => "NoLineOfSight",
                        _ => "NoPath",
                    };
                    SetTargetTrackingStatus(
                        uid,
                        blockedRescue,
                        $"route_blocked: target={FormatEntityRef(target)}; reason={reason}; distance={directDistance:0.0}");
                }

                return false;
            default:
                return false;
        }
    }

    private void PrunePatientBuckleFailures(LuaMRescueAgentComponent rescue)
    {
        if (rescue.PatientBuckleAttempts.Count == 0 &&
            rescue.NextPatientBuckleAttemptAt.Count == 0 &&
            rescue.TerminalPatientBuckleFailures.Count == 0)
        {
            return;
        }

        var stalePairs = rescue.PatientBuckleAttempts.Keys
            .Concat(rescue.NextPatientBuckleAttemptAt.Keys)
            .Concat(rescue.TerminalPatientBuckleFailures.Keys)
            .Where(pair => Deleted(pair.Patient) || Deleted(pair.Strap))
            .Distinct()
            .ToArray();
        foreach (var pair in stalePairs)
            ClearPatientBuckleFailure(rescue, pair.Patient, pair.Strap);
    }

    private static void ClearPatientBuckleFailures(
        LuaMRescueAgentComponent rescue,
        EntityUid patient)
    {
        var pairs = rescue.PatientBuckleAttempts.Keys
            .Concat(rescue.NextPatientBuckleAttemptAt.Keys)
            .Concat(rescue.TerminalPatientBuckleFailures.Keys)
            .Where(pair => pair.Patient == patient)
            .Distinct()
            .ToArray();
        foreach (var pair in pairs)
            ClearPatientBuckleFailure(rescue, pair.Patient, pair.Strap);
    }

    private static void ClearPatientBuckleFailure(
        LuaMRescueAgentComponent rescue,
        EntityUid patient,
        EntityUid strap)
    {
        var pair = (Patient: patient, Strap: strap);
        rescue.PatientBuckleAttempts.Remove(pair);
        rescue.NextPatientBuckleAttemptAt.Remove(pair);
        rescue.TerminalPatientBuckleFailures.Remove(pair);
    }

    private void TemporarilySkipTarget(EntityUid uid, LuaMRescueAgentComponent rescue, HTNComponent htn, EntityUid target)
    {
        rescue.SkippedTargets[target] = _timing.CurTime + TimeSpan.FromSeconds(rescue.TargetSkipSeconds);

        if (TryHoldRequiredHandoffCustody(
                uid,
                target,
                rescue,
                htn,
                $"target stalled for {rescue.TargetSkipSeconds:0.0}s"))
        {
            RecordRescueHandoff(
                uid,
                rescue,
                target,
                "blocked-custody",
                $"required handoff stalled for {rescue.TargetSkipSeconds:0.0}s",
                "retaining patient ownership until retry or explicit handoff");
            return;
        }

        RecordRescueHandoff(
            uid,
            rescue,
            target,
            "incomplete",
            $"aborted after stalled target for {rescue.TargetSkipSeconds:0.0}s",
            "returning or redeploying");

        StopPullingTarget(uid, target);
        ClearRescueTask(uid, rescue, $"skipped stalled target {FormatEntityRef(target)}");
        ClearArrivalReportTarget(rescue, target);
        ClearTriageDecisionTarget(rescue, target);
        ClearDeathSignalTarget(rescue, target);
        rescue.EvacuatingTarget = null;
        rescue.AssignedTarget = null;
        rescue.AssignedPatientStrap = null;
        rescue.ShuttleRoutedTarget = null;
        ResetTargetProgress(rescue);

        var hasPendingRescueTarget = TryFindPendingRescueTarget(
            uid,
            rescue,
            target,
            out var pendingTarget,
            out var pendingReason);
        if (!hasPendingRescueTarget)
        {
            rescue.LastRedispatchStatus = "none";
            TryRouteShuttleHome(uid, rescue);
        }
        else
        {
            rescue.ShuttleReturnRouted = false;
            rescue.LastAutoEvacuationStatus = $"holding shuttle forward after skipping {FormatEntityRef(target)}; pending rescue target detected";
            TryReportRedispatchTarget(uid, rescue, target, pendingTarget, pendingReason, "skip");
        }

        StandbyAtAssignedShuttle(uid, rescue, htn, allowAutoReturn: !hasPendingRescueTarget);
        Dirty(uid, rescue);
    }

    private void TemporarilySkipSupplyTarget(LuaMRescueAgentComponent rescue, EntityUid target, string reason)
    {
        if (!rescue.TemporarilySkipFailedSupplies ||
            rescue.SupplySkipSeconds <= 0f ||
            Deleted(target))
        {
            return;
        }

        rescue.SkippedSupplyTargets[target] = _timing.CurTime + TimeSpan.FromSeconds(rescue.SupplySkipSeconds);
        rescue.LastAutoSupplyStatus = $"skipping {FormatEntityRef(target)} for {rescue.SupplySkipSeconds:0.0}s after {reason}";
        rescue.NextAutoTreatmentAttempt = _timing.CurTime;
    }

    private void TemporarilySkipDeliveryTarget(LuaMRescueAgentComponent rescue, EntityUid target, string reason)
    {
        if (!rescue.TemporarilySkipFailedDeliveryTargets ||
            rescue.DeliverySkipSeconds <= 0f ||
            Deleted(target))
        {
            return;
        }

        rescue.SkippedDeliveryTargets[target] = _timing.CurTime + TimeSpan.FromSeconds(rescue.DeliverySkipSeconds);
        if (rescue.AssignedPatientStrap == target)
            rescue.AssignedPatientStrap = null;

        rescue.LastAutoEvacuationStatus = $"skipping delivery target {FormatEntityRef(target)} for {rescue.DeliverySkipSeconds:0.0}s after {reason}";
    }

    private void ResetTargetProgress(LuaMRescueAgentComponent rescue)
    {
        rescue.ProgressTarget = null;
        rescue.ProgressGoal = null;
        rescue.LastProgressDistance = float.PositiveInfinity;
        rescue.TargetStallAccumulator = 0f;
        ClearRouteBlockHold(rescue);
    }

    private static void ClearRouteBlockHold(LuaMRescueAgentComponent rescue)
    {
        rescue.RouteBlockHoldTarget = null;
        rescue.RouteBlockHoldGoal = null;
        rescue.RouteBlockHoldStartedAt = TimeSpan.Zero;
        rescue.RouteBlockHelpRequested = false;
        rescue.LastRouteBlockHoldStatus = "none";
    }

    private void CompleteEvacuation(EntityUid uid, LuaMRescueAgentComponent rescue, HTNComponent htn, EntityUid target)
    {
        StopPullingTarget(uid, target);
        BeginPatientActivity(
            uid,
            rescue,
            LuaMRescueActivity.OnboardCare,
            target,
            rescue.AssignedPatientStrap is { Valid: true } strap
                ? new EntityCoordinates(strap, Vector2.Zero)
                : null);
        ClearRescueTask(uid, rescue, $"completed evacuation of {FormatEntityRef(target)}");
        TrySayOnboardAction(
            uid,
            rescue,
            target,
            "boarded",
            "patient secured onboard",
            $"\u0411\u0435\u0440\u0443 {Name(target)} \u043d\u0430 \u0431\u043e\u0440\u0442. \u0424\u0438\u043a\u0441\u0438\u0440\u0443\u044e \u043d\u0430 \u043a\u043e\u0439\u043a\u0435, \u043f\u0440\u043e\u0432\u0435\u0440\u044f\u044e \u0441\u043e\u0441\u0442\u043e\u044f\u043d\u0438\u0435.");
        if (TryComp<MobStateComponent>(target, out var mobState) &&
            mobState.CurrentState == MobState.Dead)
        {
            SetOnboardCareStatus(rescue, target, "dead recovery; defib cycle pending");
            TrySendRescueStatusComms(
                uid,
                rescue,
                $"patient-onboard-dead:{target}",
                $"Пациент {Name(target)} на борту. Начинаю реанимационный цикл.");
        }

        rescue.ShuttleRoutedTarget = null;
        ResetTargetProgress(rescue);
        var hasPendingRescueTarget = TryFindPendingRescueTarget(
            uid,
            rescue,
            target,
            out var pendingTarget,
            out var pendingReason);
        var onboardCarePending = HasPendingOnboardCareOrRelease(target, rescue);
        var homeHandoffConfigured = rescue.AssignedReturnTarget is { Valid: true } returnTarget &&
                                    !Deleted(returnTarget);
        if (homeHandoffConfigured)
            rescue.RequiredOnboardHandoffPatients.Add(target);
        var returnRouteRequested = homeHandoffConfigured && TryRouteShuttleHome(uid, rescue);
        if (!hasPendingRescueTarget)
        {
            rescue.LastRedispatchStatus = "none";
            rescue.LastAutoEvacuationStatus = homeHandoffConfigured
                ? $"completed evacuation of {FormatEntityRef(target)}; onboard care continues during return; " +
                  $"home handoff route requested={returnRouteRequested}"
                : onboardCarePending
                    ? $"completed evacuation of {FormatEntityRef(target)}; onboard care pending"
                    : $"completed evacuation of {FormatEntityRef(target)}; return route requested";
        }
        else if (homeHandoffConfigured || onboardCarePending)
        {
            rescue.LastAutoEvacuationStatus =
                homeHandoffConfigured
                    ? $"returning {FormatEntityRef(target)} home for confirmed handoff; pending rescue target queued"
                    : $"holding shuttle for onboard care of {FormatEntityRef(target)}; pending rescue target detected";
            rescue.LastRedispatchStatus =
                $"redispatch deferred: home-handoff first; completed={FormatEntityRef(target)}; " +
                $"target={FormatEntityRef(pendingTarget)}; reason={pendingReason}; status=queued";
            TrySendRescueStatusComms(
                uid,
                rescue,
                $"redispatch-deferred-onboard:{target}:{pendingTarget}",
                $"\u041f\u0430\u0446\u0438\u0435\u043d\u0442 {Name(target)} \u043d\u0430 \u0431\u043e\u0440\u0442\u0443. \u0412\u043e\u0437\u0432\u0440\u0430\u0449\u0430\u044e \u043d\u0430 \u0431\u0430\u0437\u0443 \u0434\u043b\u044f \u0431\u0435\u0437\u043e\u043f\u0430\u0441\u043d\u043e\u0439 \u043f\u0435\u0440\u0435\u0434\u0430\u0447\u0438; \u0432\u044b\u0437\u043e\u0432 {Name(pendingTarget)} \u043e\u0441\u0442\u0430\u0451\u0442\u0441\u044f \u0432 \u043e\u0447\u0435\u0440\u0435\u0434\u0438.");
        }

        if (!TryComp<MobStateComponent>(target, out var onboardMobState) ||
            onboardMobState.CurrentState != MobState.Dead)
        {
            SetOnboardCareStatus(
                rescue,
                target,
                onboardMobState?.CurrentState == MobState.Critical
                    ? "critical; treatment continuing"
                    : "observation; awaiting stable release");
            ReportLivingPatientOnboardStatus(uid, rescue, target, onboardMobState, hasPendingRescueTarget && !onboardCarePending);
        }

        RecordRescueHandoff(
            uid,
            rescue,
            target,
            BuildPatientTreatmentResult(target, rescue),
            homeHandoffConfigured
                ? "secured onboard; returning for confirmed home handoff"
                : onboardCarePending
                ? "secured onboard; onboard care pending"
                : hasPendingRescueTarget
                ? "secured onboard; pending rescue target detected"
                : "secured onboard; return route requested",
            homeHandoffConfigured
                ? "onboard care during return; release only after confirmed home dock"
                : onboardCarePending
                ? "onboard care before redispatch or return"
                : hasPendingRescueTarget
                    ? "redeploying to next patient"
                    : "returning to shuttle base");

        if (!homeHandoffConfigured && !onboardCarePending && !hasPendingRescueTarget)
        {
            TryRouteShuttleHome(uid, rescue);
        }
        else if (!homeHandoffConfigured && !onboardCarePending)
        {
            rescue.ShuttleReturnRouted = false;
            rescue.LastAutoEvacuationStatus = $"holding shuttle forward after evacuation of {FormatEntityRef(target)}; pending rescue target detected";
            TryReportRedispatchTarget(uid, rescue, target, pendingTarget, pendingReason, "evacuation");
        }

        rescue.EvacuatingTarget = null;
        rescue.AssignedTarget = null;
        rescue.AssignedPatientStrap = null;
        ClearArrivalReportTarget(rescue, target);
        ClearTriageDecisionTarget(rescue, target);
        ClearDeathSignalTarget(rescue, target);
        StandbyAtAssignedShuttle(
            uid,
            rescue,
            htn,
            allowAutoReturn: homeHandoffConfigured || !hasPendingRescueTarget && !onboardCarePending);
        Dirty(uid, rescue);
    }

    private void ReportLivingPatientOnboardStatus(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        EntityUid target,
        MobStateComponent? mobState,
        bool hasPendingRescueTarget)
    {
        if (mobState?.CurrentState == MobState.Critical)
        {
            TrySendRescueStatusComms(
                uid,
                rescue,
                $"patient-onboard-critical:{target}",
                $"\u041f\u0430\u0446\u0438\u0435\u043d\u0442 {Name(target)} \u043d\u0430 \u0431\u043e\u0440\u0442\u0443. \u0421\u043e\u0441\u0442\u043e\u044f\u043d\u0438\u0435 \u043a\u0440\u0438\u0442\u0438\u0447\u0435\u0441\u043a\u043e\u0435. \u041f\u0440\u043e\u0434\u043e\u043b\u0436\u0430\u044e \u043b\u0435\u0447\u0435\u043d\u0438\u0435 \u043d\u0430 \u0431\u043e\u0440\u0442\u0443.");
            return;
        }

        var followUp = hasPendingRescueTarget
            ? "\u041f\u0440\u043e\u0434\u043e\u043b\u0436\u0430\u044e \u0441\u043b\u0435\u0434\u0443\u044e\u0449\u0438\u0439 \u0432\u044b\u0437\u043e\u0432."
            : "\u0412\u043e\u0437\u0432\u0440\u0430\u0449\u0430\u0435\u043c\u0441\u044f.";
        TrySendRescueStatusComms(
            uid,
            rescue,
            $"patient-onboard:{target}",
            $"\u041f\u0430\u0446\u0438\u0435\u043d\u0442 {Name(target)} \u043d\u0430 \u0431\u043e\u0440\u0442\u0443. \u041f\u0430\u0446\u0438\u0435\u043d\u0442 \u043f\u043e\u0434 \u043d\u0430\u0431\u043b\u044e\u0434\u0435\u043d\u0438\u0435\u043c. {followUp}");
    }

    private bool IsEvacuationComplete(EntityUid uid, EntityUid target, LuaMRescueAgentComponent rescue)
    {
        if (IsBuckledToAssignedShuttlePatientStrap(target, rescue))
            return true;

        if (rescue.RequiredOnboardHandoffPatients.Contains(target))
            return false;

        if (TryFindPatientDeliveryStrap(uid, rescue, out _, out _))
            return false;

        // A pending/unreachable bed is not the same as no bed. Keep delivery
        // active so the bounded route/fallback policy can choose an alternate or
        // request help instead of silently declaring an unbuckled patient done.
        if (HasAvailablePatientDeliveryStrap(rescue, target))
            return false;

        return IsOnAssignedShuttle(target, rescue) ||
               IsAtAssignedShuttleAnchor(target, rescue);
    }

    private void MarkTerminalOnboardCareFailure(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        EntityUid patient,
        LuaMRescueFailureReason reason,
        string status)
    {
        var debugStatus = $"{reason}: {status}; patient={FormatEntityRef(patient)}";
        rescue.TerminalOnboardCareFailures[patient] = debugStatus;
        rescue.LastOnboardCareStatus = debugStatus;
        _activity.Block(uid, reason, LuaMRescueActivity.Handoff, out _);
        TrySendRescueStatusComms(
            uid,
            rescue,
            $"onboard-terminal:{patient}:{reason}",
            $"Бортовое лечение {Name(patient)} заблокировано: {status}. Передаю пациента и освобождаю койку.");
        Dirty(uid, rescue);
    }

    private bool TryHoldRequiredHandoffCustody(
        EntityUid uid,
        EntityUid target,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        string reason,
        LuaMRescueFailureReason failureReason = LuaMRescueFailureReason.ActionCancelled)
    {
        if (Deleted(target) || !rescue.RequiredOnboardHandoffPatients.Contains(target))
            return false;

        EntityUid? deliveryTarget = null;
        if (rescue.AssignedShuttleAnchor is { Valid: true } anchor &&
            !Deleted(anchor) &&
            !rescue.SkippedSupplyTargets.ContainsKey(anchor) &&
            !rescue.SkippedDeliveryTargets.ContainsKey(anchor))
        {
            deliveryTarget = anchor;
        }
        else if (rescue.AssignedShuttle is { Valid: true } shuttle &&
                 !Deleted(shuttle) &&
                 !rescue.SkippedSupplyTargets.ContainsKey(shuttle) &&
                 !rescue.SkippedDeliveryTargets.ContainsKey(shuttle))
        {
            deliveryTarget = shuttle;
        }

        EntityCoordinates? holdDestination = deliveryTarget is { Valid: true } destination && !Deleted(destination)
            ? new EntityCoordinates(destination, Vector2.Zero)
            : null;
        var alreadyHeld =
            rescue.AssignedTarget == target &&
            rescue.EvacuatingTarget == target &&
            rescue.AssignedPatientStrap == null &&
            rescue.TaskStage == LuaMRescueTaskStage.DeliveringPatient &&
            rescue.TaskPatientTarget == target &&
            rescue.TaskSupplyTarget == deliveryTarget &&
            rescue.ActivityContext.Activity == LuaMRescueActivity.Handoff &&
            rescue.ActivityContext.Target == target &&
            Nullable.Equals(rescue.ActivityContext.Destination, holdDestination) &&
            rescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Blocked &&
            rescue.ActivityContext.Blocked &&
            rescue.ActivityContext.Fallback == LuaMRescueActivity.Handoff &&
            rescue.ActivityContext.FailureReason == failureReason &&
            !IsPullingTarget(uid, target) &&
            !htn.Blackboard.ContainsKey(NPCBlackboard.FollowTarget);
        if (alreadyHeld)
            return true;

        StopPullingTarget(uid, target);
        if (rescue.AssignedTarget != null || htn.Blackboard.ContainsKey(NPCBlackboard.FollowTarget))
            ClearFollowTarget(uid, rescue, htn);
        else
            CancelCurrentHtnMovement(uid, htn);

        rescue.AssignedTarget = target;
        rescue.EvacuatingTarget = target;
        rescue.AssignedPatientStrap = null;
        ResetTargetProgress(rescue);
        SetRescueTask(
            uid,
            rescue,
            LuaMRescueTaskStage.DeliveringPatient,
            target,
            deliveryTarget,
            $"required handoff held after evacuation failure: {reason}");

        if (!_activity.BeginOrReplaceIntent(
                uid,
                rescue.ActivityRole,
                LuaMRescueActivity.Handoff,
                target,
                holdDestination,
                out _) &&
            rescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active)
        {
            _activity.Cancel(
                uid,
                rescue.ActivityContext.Generation,
                LuaMRescueFailureReason.Cancelled,
                out _);
            _activity.BeginOrReplaceIntent(
                uid,
                rescue.ActivityRole,
                LuaMRescueActivity.Handoff,
                target,
                holdDestination,
                out _);
        }

        _activity.Block(uid, failureReason, LuaMRescueActivity.Handoff, out _);
        rescue.LastAutoEvacuationStatus =
            $"required handoff custody retained: patient={FormatEntityRef(target)}; reason={reason}";
        SetTargetTrackingStatus(
            uid,
            rescue,
            $"required_handoff_blocked: {FormatEntityRef(target)}; reason={reason}");
        Dirty(uid, rescue);
        return true;
    }

    private bool TryHandoffTerminalOnboardPatient(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn)
    {
        if (!TryFindTerminalOnboardPatient(
                uid,
                rescue,
                out var patient,
                out var patientStrap,
                out var buckle,
                out var reason))
        {
            return false;
        }

        if (TryHoldOnboardPatientForConfirmedHomeHandoff(uid, rescue, htn, patient))
            return true;

        var previousAttempts = rescue.OnboardHandoffAttempts.GetValueOrDefault(patient);
        if (previousAttempts >= Math.Max(1, rescue.ActivityRoleProfile.MaxAttempts))
        {
            ReleaseBlockedOnboardOwnership(uid, rescue, htn, patient, reason);
            return false;
        }

        if (!BeginPatientActivity(
                uid,
                rescue,
                LuaMRescueActivity.Handoff,
                patient,
                new EntityCoordinates(patientStrap, Vector2.Zero)))
        {
            ReleaseBlockedOnboardOwnership(
                uid,
                rescue,
                htn,
                patient,
                $"handoff activity terminal: {rescue.ActivityContext.FailureReason}");
            return false;
        }
        SetOnboardCareStatus(rescue, patient, $"handoff terminal care: {reason}");

        if (!IsWithinRange(uid, patientStrap, rescue.AutoReleaseRange))
        {
            SetMovementGoal(
                uid,
                rescue,
                htn,
                new EntityCoordinates(patientStrap, Vector2.Zero),
                rescue.AutoReleaseRange);
            Dirty(uid, rescue);
            return true;
        }

        if (TryGetUnbuckleDelayRemaining(buckle, out var unbuckleDelay))
        {
            rescue.TargetStallAccumulator = 0f;
            rescue.LastOnboardCareStatus =
                $"waiting {unbuckleDelay.TotalSeconds:0.00}s for buckle safety delay before terminal handoff; {reason}";
            Dirty(uid, rescue);
            return true;
        }

        var unbuckled = _buckle.TryUnbuckle(patient, uid, buckle, popup: false);
        if (!unbuckled)
        {
            var attempts = previousAttempts + 1;
            rescue.OnboardHandoffAttempts[patient] = attempts;
            _activity.RecordAttempt(uid, out _);
            rescue.LastOnboardCareStatus =
                $"BedUnavailable: handoff unbuckle attempt {attempts}/{rescue.ActivityRoleProfile.MaxAttempts}; {reason}";
            if (attempts >= Math.Max(1, rescue.ActivityRoleProfile.MaxAttempts))
            {
                _activity.Block(uid, LuaMRescueFailureReason.BedUnavailable, LuaMRescueActivity.Standby, out _);
                TrySendRescueStatusComms(
                    uid,
                    rescue,
                    $"handoff-bed-blocked:{patient}",
                    $"Не могу освободить койку пациента {Name(patient)} после {attempts} попыток. Нужна ручная помощь.");
                ReleaseBlockedOnboardOwnership(
                    uid,
                    rescue,
                    htn,
                    patient,
                    $"BedUnavailable: {reason}; manual bed release required");
            }

            Dirty(uid, rescue);
            return attempts < Math.Max(1, rescue.ActivityRoleProfile.MaxAttempts);
        }

        if (rescue.AssignedPatientStrap == patientStrap)
            rescue.AssignedPatientStrap = null;

        rescue.OnboardHandoffAttempts.Remove(patient);
        rescue.OnboardCareAttempts.Remove(patient);
        rescue.TerminalOnboardCareFailures.Remove(patient);
        rescue.TerminalDefibrillationFailures.Remove(patient);
        MarkOnboardCareReleased(uid, rescue, patient);
        RecordRescueHandoff(
            uid,
            rescue,
            patient,
            "terminal-care",
            reason,
            "unbuckled for specialist/manual handoff");
        _activity.Complete(uid, out _);
        CompleteReleasedPatientCare(uid, rescue, htn, patient);
        Dirty(uid, rescue);
        return true;
    }

    private bool TryFindTerminalOnboardPatient(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        out EntityUid patient,
        out EntityUid patientStrap,
        out BuckleComponent buckle,
        out string reason)
    {
        patient = default;
        patientStrap = default;
        buckle = default!;
        reason = string.Empty;
        if (rescue.AssignedShuttle is not { Valid: true } shuttle || Deleted(shuttle))
            return false;

        var query = EntityQueryEnumerator<StrapComponent, TransformComponent>();
        while (query.MoveNext(out var strapUid, out var strap, out var xform))
        {
            if (xform.GridUid != shuttle || !IsAssignedShuttlePatientStrap(strapUid, rescue))
                continue;

            foreach (var buckled in strap.BuckledEntities)
            {
                if (Deleted(buckled) ||
                    rescue.IgnoredOnboardPatients.Contains(buckled) ||
                    !TryComp<BuckleComponent>(buckled, out var patientBuckle) ||
                    patientBuckle.BuckledTo != strapUid)
                {
                    continue;
                }

                if (rescue.TerminalOnboardCareFailures.TryGetValue(buckled, out var onboardFailure))
                {
                    patient = buckled;
                    patientStrap = strapUid;
                    buckle = patientBuckle;
                    reason = onboardFailure;
                    return true;
                }

                if (rescue.TerminalDefibrillationFailures.TryGetValue(buckled, out var defibFailure))
                {
                    patient = buckled;
                    patientStrap = strapUid;
                    buckle = patientBuckle;
                    reason = defibFailure;
                    return true;
                }

                if (!_activity.IsEligibleRescuePatient(
                        uid,
                        buckled,
                        LuaMRescuePatientRequestKind.OnboardCare,
                        manualOverride: IsManualPatientIntent(rescue, buckled),
                        out var failureReason) &&
                    failureReason != LuaMRescueFailureReason.TargetHealthy)
                {
                    patient = buckled;
                    patientStrap = strapUid;
                    buckle = patientBuckle;
                    reason = failureReason.ToString();
                    return true;
                }
            }
        }

        return false;
    }

    private void HoldMovementForTreatmentCooldown(EntityUid uid, HTNComponent htn)
    {
        if (htn.Blackboard.ContainsKey(NPCBlackboard.FollowTarget) ||
            htn.Plan != null ||
            htn.PlanningToken != null ||
            htn.PlanningJob != null)
        {
            CancelCurrentHtnMovement(uid, htn, cancelRouteProbe: false);
        }

        htn.Blackboard.Remove<EntityCoordinates>(NPCBlackboard.FollowTarget);
        _npc.SleepNPC(uid, htn);
    }

    private bool TryTreatOnboardPatient(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn)
    {
        if (!rescue.AutoTreatWithCarriedItems ||
            !TryFindOnboardTreatmentPatient(uid, rescue, out var patient, out var patientStrap, out var status))
        {
            return false;
        }

        if (rescue.TerminalOnboardCareFailures.ContainsKey(patient))
            return false;

        if (!BeginPatientActivity(
            uid,
            rescue,
            LuaMRescueActivity.OnboardCare,
            patient,
            new EntityCoordinates(patientStrap, Vector2.Zero)))
        {
            if (!rescue.TerminalOnboardCareFailures.ContainsKey(patient))
            {
                MarkTerminalOnboardCareFailure(
                    uid,
                    rescue,
                    patient,
                    rescue.ActivityContext.FailureReason == LuaMRescueFailureReason.None
                        ? LuaMRescueFailureReason.DeadlineExceeded
                        : rescue.ActivityContext.FailureReason,
                    "onboard-care activity reached a terminal state");
            }
            return false;
        }

        SetOnboardCareStatus(rescue, patient, $"treatment; {status}");
        SetRescueTask(
            uid,
            rescue,
            LuaMRescueTaskStage.TreatingPatient,
            patient,
            patientStrap,
            $"treating onboard {FormatEntityRef(patient)} at {FormatEntityRef(patientStrap)}");

        if (!IsWithinRange(uid, patient, rescue.PlayerActionRange))
        {
            rescue.LastAutoTreatmentStatus = $"moving to onboard treatment {FormatEntityRef(patient)} at {FormatEntityRef(patientStrap)}";
            SetOnboardCareStatus(rescue, patient, $"treatment; {rescue.LastAutoTreatmentStatus}");
            TrySayOnboardAction(
                uid,
                rescue,
                patient,
                "onboard-treatment",
                "onboard treatment loop",
                "\u041f\u0430\u0446\u0438\u0435\u043d\u0442 \u043d\u0430 \u0431\u043e\u0440\u0442\u0443, \u0434\u043e\u043b\u0435\u0447\u0438\u0432\u0430\u044e \u0434\u043e \u0432\u044b\u043f\u0443\u0441\u043a\u0430.");
            SetFollowDeliveryStrap(uid, rescue, htn, patientStrap);
            Dirty(uid, rescue);
            return true;
        }

        TrySayOnboardAction(
            uid,
            rescue,
            patient,
            "onboard-treatment",
            "onboard treatment loop",
            "\u041f\u0430\u0446\u0438\u0435\u043d\u0442 \u043d\u0430 \u0431\u043e\u0440\u0442\u0443, \u0434\u043e\u043b\u0435\u0447\u0438\u0432\u0430\u044e \u0434\u043e \u0432\u044b\u043f\u0443\u0441\u043a\u0430.");

        // A cooldown is an in-progress wait, not a failed planner attempt. With
        // the normal 2 s refresh and 6 s treatment cooldown, counting this as a
        // failure could exhaust the whole onboard budget between two real uses.
        if (!HasActiveMedicalDoAfter(uid, patient, out _) &&
            _timing.CurTime < rescue.NextAutoTreatmentAttempt)
        {
            var remaining = rescue.NextAutoTreatmentAttempt - _timing.CurTime;
            rescue.LastOnboardCareStatus =
                $"onboard-care waiting for treatment cooldown: {remaining.TotalSeconds:0.0}s; " +
                $"patient={FormatEntityRef(patient)}";
            _activity.RecordProgress(
                uid,
                Transform(patient).Coordinates,
                TryGetDistance(uid, patient, out var cooldownDistance) ? cooldownDistance : null,
                LuaMRescueRouteStatus.Arrived,
                LuaMRescueDoAfterStatus.None,
                out _);
            Dirty(uid, rescue);
            return true;
        }

        if (TryAutoTreatTarget(uid, rescue, htn, patient))
            return true;

        BeginPatientActivity(
            uid,
            rescue,
            LuaMRescueActivity.OnboardCare,
            patient,
            new EntityCoordinates(patientStrap, Vector2.Zero));
        var attempts = rescue.OnboardCareAttempts.GetValueOrDefault(patient) + 1;
        rescue.OnboardCareAttempts[patient] = attempts;
        if (attempts >= Math.Max(1, rescue.ActivityRoleProfile.MaxAttempts))
        {
            MarkTerminalOnboardCareFailure(
                uid,
                rescue,
                patient,
                LuaMRescueFailureReason.NoEffectiveMedicine,
                $"no effective onboard treatment after {attempts} planner attempts; last={rescue.LastAutoTreatmentStatus}");
            return false;
        }

        rescue.LastOnboardCareStatus =
            $"onboard-care retry {attempts}/{rescue.ActivityRoleProfile.MaxAttempts}: {rescue.LastAutoTreatmentStatus}";
        _activity.RecordAttempt(uid, out _);
        Dirty(uid, rescue);
        return true;
    }

    private bool TryFindOnboardTreatmentPatient(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        out EntityUid patient,
        out EntityUid patientStrap,
        out string status)
    {
        patient = default;
        patientStrap = default;
        status = string.Empty;

        if (rescue.AssignedShuttle is not { Valid: true } shuttle ||
            Deleted(shuttle))
        {
            return false;
        }

        var bestScore = float.MinValue;
        var query = EntityQueryEnumerator<StrapComponent, TransformComponent>();
        while (query.MoveNext(out var strapUid, out var strap, out var xform))
        {
            if (xform.GridUid != shuttle ||
                !IsAssignedShuttlePatientStrap(strapUid, rescue))
            {
                continue;
            }

            foreach (var buckled in strap.BuckledEntities)
            {
                if (Deleted(buckled) ||
                    rescue.IgnoredOnboardPatients.Contains(buckled) ||
                    !TryComp<BuckleComponent>(buckled, out var buckle) ||
                    buckle.BuckledTo != strapUid ||
                    !TryComp<MobStateComponent>(buckled, out var mobState) ||
                    mobState.CurrentState == MobState.Dead ||
                    !TryComp<DamageableComponent>(buckled, out var damageable))
                {
                    continue;
                }

                if (!_activity.IsEligibleRescuePatient(
                        uid,
                        buckled,
                        LuaMRescuePatientRequestKind.OnboardCare,
                        manualOverride: IsManualPatientIntent(rescue, buckled),
                        out _))
                {
                    continue;
                }

                var totalDamage = damageable.TotalDamage.Float();
                if (mobState.CurrentState != MobState.Critical &&
                    totalDamage <= rescue.AutoReleaseMaxDamage)
                {
                    continue;
                }

                if (mobState.CurrentState != MobState.Critical &&
                    totalDamage < rescue.AutoTreatMinDamage)
                {
                    continue;
                }

                var score = totalDamage;
                if (mobState.CurrentState == MobState.Critical)
                    score += 1000f;

                if (score <= bestScore)
                    continue;

                patient = buckled;
                patientStrap = strapUid;
                status = mobState.CurrentState == MobState.Critical
                    ? $"critical onboard patient {FormatEntityRef(buckled)} needs onboard treatment"
                    : $"onboard patient {FormatEntityRef(buckled)} damage {totalDamage:0.0} needs onboard treatment";
                bestScore = score;
            }
        }

        return patient != default;
    }

    private bool TryReleaseStabilizedPatientOnShuttle(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn)
    {
        if (!rescue.AutoReleaseStabilizedPatients)
            return false;

        if (!TryFindStabilizedShuttlePatient(
                uid,
                rescue,
                out var patient,
                out var patientStrap,
                out var buckle,
                out var holdingPatient,
                out var status))
        {
            if (!string.IsNullOrWhiteSpace(status))
            {
                rescue.LastAutoEvacuationStatus = status;
                if (holdingPatient.Valid && !Deleted(holdingPatient))
                    SetOnboardCareStatus(rescue, holdingPatient, $"holding; {status}");

                if (status.StartsWith("holding critical onboard patient", StringComparison.OrdinalIgnoreCase))
                {
                    TrySendRescueStatusComms(
                        uid,
                        rescue,
                        $"patient-hold-critical:{rescue.AssignedShuttle}",
                        "\u041f\u0430\u0446\u0438\u0435\u043d\u0442 \u043d\u0430 \u0431\u043e\u0440\u0442\u0443, \u0441\u043e\u0441\u0442\u043e\u044f\u043d\u0438\u0435 \u043a\u0440\u0438\u0442\u0438\u0447\u0435\u0441\u043a\u043e\u0435. \u0414\u0435\u0440\u0436\u0443 \u043b\u0435\u0447\u0435\u043d\u0438\u0435 \u0434\u043e \u0441\u0442\u0430\u0431\u0438\u043b\u0438\u0437\u0430\u0446\u0438\u0438.");
                    TrySayOnboardAction(
                        uid,
                        rescue,
                        holdingPatient,
                        "treating-critical",
                        "critical onboard treatment",
                        "\u0414\u0435\u0440\u0436\u0443 \u043f\u0430\u0446\u0438\u0435\u043d\u0442\u0430 \u043d\u0430 \u0431\u043e\u0440\u0442\u0443. \u0421\u043e\u0441\u0442\u043e\u044f\u043d\u0438\u0435 \u043a\u0440\u0438\u0442\u0438\u0447\u0435\u0441\u043a\u043e\u0435, \u043b\u0435\u0447\u0435\u043d\u0438\u0435 \u0438\u0434\u0435\u0442.");
                }
                else if (status.StartsWith("holding onboard patient", StringComparison.OrdinalIgnoreCase))
                {
                    TrySendRescueStatusComms(
                        uid,
                        rescue,
                        $"patient-hold-treatment:{rescue.AssignedShuttle}",
                        "\u041f\u0430\u0446\u0438\u0435\u043d\u0442 \u043d\u0430 \u0431\u043e\u0440\u0442\u0443, \u043f\u043e\u043a\u0430 \u043d\u0435 \u0441\u0442\u0430\u0431\u0438\u043b\u0435\u043d. \u041f\u0440\u043e\u0434\u043e\u043b\u0436\u0430\u044e \u043b\u0435\u0447\u0435\u043d\u0438\u0435 \u0438 \u043d\u0430\u0431\u043b\u044e\u0434\u0435\u043d\u0438\u0435.");
                    TrySayOnboardAction(
                        uid,
                        rescue,
                        holdingPatient,
                        "treating",
                        "onboard treatment and observation",
                        "\u041f\u0430\u0446\u0438\u0435\u043d\u0442 \u0435\u0449\u0435 \u043d\u0435 \u0433\u043e\u0442\u043e\u0432 \u043a \u0432\u044b\u043f\u0443\u0441\u043a\u0443. \u041b\u0435\u0447\u0443 \u0438 \u043d\u0430\u0431\u043b\u044e\u0434\u0430\u044e \u043d\u0430 \u0431\u043e\u0440\u0442\u0443.");
                }
                else if (status.StartsWith("holding dead onboard patient", StringComparison.OrdinalIgnoreCase))
                {
                    TrySayOnboardAction(
                        uid,
                        rescue,
                        holdingPatient,
                        "reanimation",
                        "onboard reanimation cycle",
                        "\u041f\u0430\u0446\u0438\u0435\u043d\u0442 \u0431\u0435\u0437 \u043f\u0443\u043b\u044c\u0441\u0430 \u043d\u0430 \u0431\u043e\u0440\u0442\u0443. \u0413\u043e\u0442\u043e\u0432\u043b\u044e \u0440\u0435\u0430\u043d\u0438\u043c\u0430\u0446\u0438\u044e.");
                    TrySendRescueStatusComms(
                        uid,
                        rescue,
                        $"patient-hold-dead:{rescue.AssignedShuttle}",
                        "Пациент без пульса удерживается на борту. Продолжаю реанимационный цикл.");
                }
            }

            return false;
        }

        if (TryHoldOnboardPatientForConfirmedHomeHandoff(uid, rescue, htn, patient))
            return true;

        SetOnboardCareStatus(rescue, patient, $"release-ready; {status}");
        SetRescueTask(
            uid,
            rescue,
            LuaMRescueTaskStage.DeliveringPatient,
            patient,
            patientStrap,
            $"releasing stabilized {FormatEntityRef(patient)} from {FormatEntityRef(patientStrap)}");
        if (!BeginPatientActivity(
                uid,
                rescue,
                LuaMRescueActivity.Handoff,
                patient,
                new EntityCoordinates(patientStrap, Vector2.Zero)))
        {
            ReleaseBlockedOnboardOwnership(
                uid,
                rescue,
                htn,
                patient,
                $"stable-release activity terminal: {rescue.ActivityContext.FailureReason}");
            return false;
        }

        if (!IsWithinRange(uid, patientStrap, rescue.AutoReleaseRange))
        {
            rescue.LastAutoEvacuationStatus = $"moving to release stabilized {FormatEntityRef(patient)} from {FormatEntityRef(patientStrap)}";
            TrySayOnboardAction(
                uid,
                rescue,
                patient,
                "release-ready",
                "patient stable, preparing release",
                "\u041f\u0430\u0446\u0438\u0435\u043d\u0442 \u0441\u0442\u0430\u0431\u0438\u043b\u0435\u043d. \u0418\u0434\u0443 \u0441\u043d\u044f\u0442\u044c \u0441 \u043a\u043e\u0439\u043a\u0438 \u0438 \u0432\u044b\u043f\u0443\u0441\u0442\u0438\u0442\u044c \u0441 \u0431\u043e\u0440\u0442\u0430.");
            SetMovementGoal(
                uid,
                rescue,
                htn,
                new EntityCoordinates(patientStrap, Vector2.Zero),
                rescue.AutoReleaseRange);
            Dirty(uid, rescue);
            return true;
        }

        if (TryGetUnbuckleDelayRemaining(buckle, out var unbuckleDelay))
        {
            rescue.TargetStallAccumulator = 0f;
            rescue.LastAutoEvacuationStatus =
                $"waiting {unbuckleDelay.TotalSeconds:0.00}s for buckle safety delay before releasing " +
                $"stabilized {FormatEntityRef(patient)}";
            Dirty(uid, rescue);
            return true;
        }

        var oldStrap = buckle.BuckledTo;
        var unbuckled = _buckle.TryUnbuckle(patient, uid, buckle, popup: false);
        rescue.LastAutoEvacuationStatus = unbuckled
            ? $"released stabilized {FormatEntityRef(patient)} from {FormatEntityRef(oldStrap)}"
            : $"could not release stabilized {FormatEntityRef(patient)} from {FormatEntityRef(oldStrap)}";

        if (unbuckled)
        {
            if (rescue.AssignedPatientStrap == patientStrap)
                rescue.AssignedPatientStrap = null;

            MarkOnboardCareReleased(uid, rescue, patient);
            RecordRescueHandoff(
                uid,
                rescue,
                patient,
                BuildPatientTreatmentResult(patient, rescue),
                "released from shuttle care",
                "available for next rescue");
            TrySayOnboardAction(
                uid,
                rescue,
                patient,
                "released",
                "patient released from shuttle care",
                "\u041f\u0430\u0446\u0438\u0435\u043d\u0442 \u0432\u044b\u043f\u0443\u0449\u0435\u043d \u0441 \u0431\u043e\u0440\u0442\u0430. \u041c\u0435\u0441\u0442\u043e \u0433\u043e\u0442\u043e\u0432\u043e \u0434\u043b\u044f \u0441\u043b\u0435\u0434\u0443\u044e\u0449\u0435\u0433\u043e.");
            TrySendRescueStatusComms(
                uid,
                rescue,
                $"patient-release:{patient}",
                $"Пациент {Name(patient)} стабилен. Отпускаю с борта.");
            _activity.Complete(uid, out _);
            CompleteReleasedPatientCare(uid, rescue, htn, patient);
        }
        else
        {
            var attempts = rescue.OnboardHandoffAttempts.GetValueOrDefault(patient) + 1;
            rescue.OnboardHandoffAttempts[patient] = attempts;
            _activity.RecordAttempt(uid, out _);
            if (attempts >= Math.Max(1, rescue.ActivityRoleProfile.MaxAttempts))
            {
                rescue.LastOnboardCareStatus =
                    $"BedUnavailable: stable patient release failed after {attempts} attempts";
                _activity.Block(uid, LuaMRescueFailureReason.BedUnavailable, LuaMRescueActivity.Standby, out _);
                TrySendRescueStatusComms(
                    uid,
                    rescue,
                    $"stable-release-failed:{patient}",
                    $"Не могу освободить койку {Name(patient)} после {attempts} попыток. Нужна ручная помощь.");
                ReleaseBlockedOnboardOwnership(
                    uid,
                    rescue,
                    htn,
                    patient,
                    "BedUnavailable: stable patient remained buckled; manual release required");
                Dirty(uid, rescue);
                return false;
            }
        }

        Dirty(uid, rescue);
        return true;
    }

    /// <summary>
    /// A configured return target turns onboard release into a real handoff:
    /// the patient stays strapped through flight and through mere arrival. Only
    /// reciprocal docking at the assigned home grid is a safe release point.
    /// Isolated/local fixtures without a return target retain local-bed behavior.
    /// </summary>
    private bool TryHoldOnboardPatientForConfirmedHomeHandoff(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        EntityUid patient)
    {
        if (rescue.AssignedReturnTarget is not { Valid: true } returnTarget ||
            Deleted(returnTarget))
        {
            return false;
        }

        rescue.RequiredOnboardHandoffPatients.Add(patient);

        if (IsConfirmedHomeHandoffDock(rescue, returnTarget, out var dockStatus))
            return false;

        var routed = TryRouteShuttleHome(uid, rescue);
        // The physical buckle and durable handoff set remain authoritative
        // while the shuttle is in flight. Route/standby transitions may clear
        // compatibility mirrors, so repair them here before yielding the tick.
        if (TryComp<BuckleComponent>(patient, out var buckle) &&
            buckle.BuckledTo is { Valid: true } occupiedStrap &&
            !Deleted(occupiedStrap))
        {
            rescue.OnboardCareTarget = patient;
            rescue.AssignedPatientStrap = occupiedStrap;
            SetRescueTask(
                uid,
                rescue,
                LuaMRescueTaskStage.DeliveringPatient,
                patient,
                occupiedStrap,
                $"holding {FormatEntityRef(patient)} for confirmed home handoff");
            BeginPatientActivity(
                uid,
                rescue,
                LuaMRescueActivity.OnboardCare,
                patient,
                new EntityCoordinates(occupiedStrap, Vector2.Zero));
        }

        rescue.TargetStallAccumulator = 0f;
        rescue.LastAutoEvacuationStatus =
            $"holding {FormatEntityRef(patient)} buckled until confirmed home handoff; " +
            $"return={FormatEntityRef(returnTarget)}; routed={routed}; {dockStatus}";
        SetOnboardCareStatus(rescue, patient, $"returning home for handoff; {dockStatus}");
        Dirty(uid, rescue);
        return true;
    }

    private bool IsConfirmedHomeHandoffDock(
        LuaMRescueAgentComponent rescue,
        EntityUid returnTarget,
        out string status)
    {
        if (rescue.AssignedShuttle is not { Valid: true } shuttle || Deleted(shuttle))
        {
            status = "assigned shuttle unavailable";
            return false;
        }

        var returnGrid = HasComp<MapGridComponent>(returnTarget)
            ? returnTarget
            : Transform(returnTarget).GridUid;
        if (returnGrid is not { Valid: true } grid || Deleted(grid))
        {
            status = "assigned return target has no valid grid";
            return false;
        }

        if (!TryComp<LuaMRescueShuttleLifecycleComponent>(shuttle, out var lifecycle))
        {
            status = "return lifecycle unavailable";
            return false;
        }

        var lifecycleConfirmed = lifecycle.Target == returnTarget &&
                                 lifecycle.RouteActivity == LuaMRescueActivity.Returning &&
                                 lifecycle.State == LuaMRescueShuttleRouteState.Docked &&
                                 lifecycle.SafeExitConfirmed;
        var physicallyDocked = IsShuttleDockedToGrid(shuttle, grid);
        status =
            $"home lifecycle={lifecycle.State}/{lifecycle.RouteActivity}; " +
            $"safeExit={lifecycle.SafeExitConfirmed}; physicalDock={physicallyDocked}";
        return lifecycleConfirmed && physicallyDocked;
    }

    private bool TryGetUnbuckleDelayRemaining(BuckleComponent buckle, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (buckle.BuckleTime is not { } buckleTime)
            return false;

        var readyAt = buckleTime + buckle.Delay;
        if (_timing.CurTime >= readyAt)
            return false;

        remaining = readyAt - _timing.CurTime;
        return true;
    }

    private void CompleteReleasedPatientCare(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        EntityUid patient)
    {
        rescue.EvacuatingTarget = null;
        rescue.AssignedTarget = null;
        rescue.AssignedPatientStrap = null;
        rescue.ShuttleRoutedTarget = null;
        ClearArrivalReportTarget(rescue, patient);
        ClearTriageDecisionTarget(rescue, patient);
        ClearDeathSignalTarget(rescue, patient);
        ResetTargetProgress(rescue);

        var hasPendingRescueTarget = TryFindPendingRescueTarget(
            uid,
            rescue,
            patient,
            out var pendingTarget,
            out var pendingReason);
        if (!hasPendingRescueTarget)
        {
            rescue.LastRedispatchStatus = "none";
            rescue.LastAutoEvacuationStatus = $"released stabilized {FormatEntityRef(patient)}; ready for next rescue";
        }
        else
        {
            rescue.ShuttleReturnRouted = false;
            rescue.LastAutoEvacuationStatus = $"holding shuttle forward after release of {FormatEntityRef(patient)}; pending rescue target detected";
            TryReportRedispatchTarget(uid, rescue, patient, pendingTarget, pendingReason, "release");
        }

        StandbyAtAssignedShuttle(uid, rescue, htn, allowAutoReturn: !hasPendingRescueTarget);
    }

    private void TryReportRedispatchTarget(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        EntityUid completedTarget,
        EntityUid pendingTarget,
        string pendingReason,
        string source)
    {
        rescue.LastRedispatchStatus =
            $"redispatch: source={source}; completed={FormatEntityRef(completedTarget)}; " +
            $"target={FormatEntityRef(pendingTarget)}; reason={pendingReason}; status=forward";

        if (Deleted(uid))
            return;

        if (Deleted(completedTarget) ||
            Deleted(pendingTarget))
        {
            Dirty(uid, rescue);
            return;
        }

        TrySendRescueStatusComms(
            uid,
            rescue,
            $"redispatch:{source}:{completedTarget}:{pendingTarget}",
            $"\u0422\u0435\u043a\u0443\u0449\u0438\u0439 \u044d\u0442\u0430\u043f \u043f\u043e {Name(completedTarget)} \u0437\u0430\u043a\u0440\u044b\u0442. \u041f\u0435\u0440\u0435\u043d\u0430\u0437\u043d\u0430\u0447\u0430\u044e\u0441\u044c \u043a {Name(pendingTarget)}.");
    }

    private void RecordRescueHandoff(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        EntityUid patient,
        string treatmentResult,
        string evacuationResult,
        string teamStatus)
    {
        var location = FormatHandoffLocation(rescue, patient);
        var crewHelp = "unverified";
        var blockers = "team scene memory unavailable";
        if (TryComp<LuaMRescueTeamComponent>(uid, out var team))
        {
            crewHelp = BuildHandoffCrewHelpSummary(team);
            blockers = BuildHandoffBlockerSummary(team, crewHelp);
        }

        _rescueTeam.TryRecordRescueHandoff(
            uid,
            patient,
            location,
            treatmentResult,
            evacuationResult,
            blockers,
            crewHelp,
            teamStatus);
    }

    private void SetOnboardCareStatus(LuaMRescueAgentComponent rescue, EntityUid patient, string status)
    {
        rescue.OnboardCareTarget = patient;
        rescue.LastOnboardCareStatus = $"onboard-care: patient={FormatEntityRef(patient)}; {status}";
    }

    private void TrySayOnboardAction(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        EntityUid patient,
        string step,
        string status,
        string message)
    {
        rescue.LastOnboardActionStatus = $"onboard-action:{step}; patient={FormatEntityRef(patient)}; {status}";

        if (!patient.Valid ||
            Deleted(uid) ||
            Deleted(patient) ||
            string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var key = $"onboard-action:{step}:{patient}";
        var now = _timing.CurTime;
        if (string.Equals(rescue.LastOnboardActionKey, key, StringComparison.Ordinal) &&
            rescue.NextOnboardActionAt > now)
        {
            Dirty(uid, rescue);
            return;
        }

        rescue.LastOnboardActionKey = key;
        rescue.NextOnboardActionAt = now + TimeSpan.FromSeconds(Math.Max(0.1f, rescue.OnboardActionCooldown));
        if (!TryReserveRescueSpeech(uid, rescue, key, "onboard-action", now))
        {
            Dirty(uid, rescue);
            return;
        }

        _chat.TrySendInGameICMessage(uid, message, InGameICChatType.Speak, hideChat: false, hideLog: true);
        Dirty(uid, rescue);
    }

    private void TrySayRescueAction(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        EntityUid patient,
        string step,
        string status,
        string message)
    {
        rescue.LastRescueActionStatus = $"rescue-action:{step}; patient={FormatEntityRef(patient)}; {status}";

        if (!patient.Valid ||
            Deleted(uid) ||
            Deleted(patient) ||
            string.IsNullOrWhiteSpace(message))
        {
            Dirty(uid, rescue);
            return;
        }

        var key = $"rescue-action:{step}:{patient}";
        var now = _timing.CurTime;
        if (string.Equals(rescue.LastRescueActionKey, key, StringComparison.Ordinal) &&
            rescue.NextRescueActionAt > now)
        {
            Dirty(uid, rescue);
            return;
        }

        rescue.LastRescueActionKey = key;
        rescue.NextRescueActionAt = now + TimeSpan.FromSeconds(Math.Max(0.1f, rescue.RescueActionCooldown));
        if (!TryReserveRescueSpeech(uid, rescue, key, "rescue-action", now))
        {
            Dirty(uid, rescue);
            return;
        }

        _chat.TrySendInGameICMessage(uid, message, InGameICChatType.Speak, hideChat: false, hideLog: true);
        Dirty(uid, rescue);
    }

    private void MarkOnboardCareReleased(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        EntityUid patient)
    {
        CloseRecoveredPatientEpisode(uid, rescue, patient);
        rescue.OnboardCareTarget = null;
        rescue.RequiredOnboardHandoffPatients.Remove(patient);
        if (rescue.ManualOverrideTarget == patient)
            RevokeManualOverrideTarget(uid, rescue, patient, "onboard care released patient");
        rescue.IgnoredOnboardPatients.Remove(patient);
        rescue.OnboardCareAttempts.Remove(patient);
        rescue.OnboardHandoffAttempts.Remove(patient);
        rescue.TerminalOnboardCareFailures.Remove(patient);
        rescue.TerminalDefibrillationFailures.Remove(patient);
        rescue.LastOnboardCareStatus = $"onboard-care released: patient={FormatEntityRef(patient)}";
    }

    private void PruneIgnoredOnboardPatients(LuaMRescueAgentComponent rescue)
    {
        foreach (var patient in rescue.IgnoredOnboardPatients.ToArray())
        {
            if (!Deleted(patient) && IsBuckledToAssignedShuttlePatientStrap(patient, rescue))
                continue;

            rescue.IgnoredOnboardPatients.Remove(patient);
            rescue.OnboardCareAttempts.Remove(patient);
            rescue.OnboardHandoffAttempts.Remove(patient);
            rescue.RequiredOnboardHandoffPatients.Remove(patient);
        }
    }

    private void PruneStaleOnboardCareOwnership(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn)
    {
        if (rescue.OnboardCareTarget is not { Valid: true } patient)
            return;

        var stillOnAssignedBed = !Deleted(patient) &&
                                 IsBuckledToAssignedShuttlePatientStrap(patient, rescue);
        if (stillOnAssignedBed)
        {
            var stillEligible = _activity.IsEligibleRescuePatient(
                                    uid,
                                    patient,
                                    LuaMRescuePatientRequestKind.OnboardCare,
                                    manualOverride: IsManualPatientIntent(rescue, patient),
                                    out var eligibilityFailure) ||
                                eligibilityFailure == LuaMRescueFailureReason.TargetHealthy;
            if (stillEligible)
                return;

            // The patient still physically owns a rescue bed. This is a
            // terminal-care handoff, not TargetLost: preserve ownership and the
            // exact reason until the bounded unbuckle path below frees the bed.
            if (!rescue.TerminalOnboardCareFailures.ContainsKey(patient))
            {
                CancelMedicalDoAftersForIntentReplacement(
                    uid,
                    rescue,
                    $"onboard patient {FormatEntityRef(patient)} became ineligible: {eligibilityFailure}");
                CancelCurrentHtnMovement(uid, htn);
                MarkTerminalOnboardCareFailure(
                    uid,
                    rescue,
                    patient,
                    eligibilityFailure,
                    $"still buckled but no longer eligible; fallback=bounded handoff/unbuckle");
            }

            return;
        }

        if (!Deleted(patient) && rescue.RequiredOnboardHandoffPatients.Contains(patient))
        {
            CancelMedicalDoAftersForIntentReplacement(
                uid,
                rescue,
                $"required onboard handoff for {FormatEntityRef(patient)} lost its strap");
            CancelCurrentHtnMovement(uid, htn);
            rescue.OnboardCareTarget = null;
            rescue.AssignedPatientStrap = null;
            rescue.AssignedTarget = patient;
            rescue.EvacuatingTarget = patient;
            SetRescueTask(
                uid,
                rescue,
                LuaMRescueTaskStage.EvacuatingPatient,
                patient,
                null,
                $"recovering externally unbuckled handoff patient {FormatEntityRef(patient)}");
            _activity.BeginOrReplaceIntent(
                uid,
                rescue.ActivityRole == LuaMRescueRole.None ? LuaMRescueRole.Aibolit : rescue.ActivityRole,
                LuaMRescueActivity.PreparingEvacuation,
                patient,
                new EntityCoordinates(patient, Vector2.Zero),
                out _);
            rescue.LastOnboardCareStatus =
                $"onboard-care recovery: patient={FormatEntityRef(patient)}; reason=external unbuckle; re-boarding required";
            Dirty(uid, rescue);
            return;
        }

        CancelMedicalDoAftersForIntentReplacement(
            uid,
            rescue,
            $"stale onboard target {FormatEntityRef(patient)} was removed or unbuckled");
        CancelCurrentHtnMovement(uid, htn);
        if (rescue.AssignedTarget == patient)
            rescue.AssignedTarget = null;
        if (rescue.EvacuatingTarget == patient)
            rescue.EvacuatingTarget = null;
        if (rescue.DeathSignalTarget == patient)
            rescue.DeathSignalTarget = null;
        if (rescue.TaskPatientTarget == patient)
            ClearRescueTask(uid, rescue, "stale onboard patient ownership pruned");

        rescue.OnboardCareTarget = null;
        if (rescue.ManualOverrideTarget == patient)
            RevokeManualOverrideTarget(uid, rescue, patient, "stale onboard ownership pruned");
        rescue.AssignedPatientStrap = null;
        rescue.OnboardCareAttempts.Remove(patient);
        rescue.OnboardHandoffAttempts.Remove(patient);
        rescue.TerminalOnboardCareFailures.Remove(patient);
        rescue.TerminalDefibrillationFailures.Remove(patient);
        rescue.IgnoredOnboardPatients.Remove(patient);
        rescue.RequiredOnboardHandoffPatients.Remove(patient);
        rescue.LastOnboardCareStatus =
            $"onboard-care target pruned: patient={FormatEntityRef(patient)}; reason=TargetLost";
        _activity.Fail(uid, LuaMRescueFailureReason.TargetLost, out _);
        Dirty(uid, rescue);
    }

    private void ReleaseBlockedOnboardOwnership(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        EntityUid patient,
        string reason)
    {
        rescue.IgnoredOnboardPatients.Add(patient);
        CancelMedicalDoAftersForIntentReplacement(uid, rescue, $"terminal onboard handoff for {FormatEntityRef(patient)}");
        CancelCurrentHtnMovement(uid, htn);

        if (TryComp<BuckleComponent>(patient, out var buckle) &&
            buckle.BuckledTo is { Valid: true } occupiedStrap &&
            !Deleted(occupiedStrap))
        {
            // A failed unbuckle is not a physical handoff. Keep one terminal
            // owner for the occupied bed so retirement, external release and
            // explicit-order guards can still recover the exact patient/strap.
            rescue.AssignedTarget = null;
            rescue.EvacuatingTarget = null;
            rescue.OnboardCareTarget = patient;
            rescue.AssignedPatientStrap = occupiedStrap;
            rescue.TerminalOnboardCareFailures[patient] = reason;
            rescue.LastOnboardCareStatus =
                $"onboard-care terminal handoff: patient={FormatEntityRef(patient)}; " +
                $"ownership=retained; physical release required; {reason}";
            SetRescueTask(
                uid,
                rescue,
                LuaMRescueTaskStage.DeliveringPatient,
                patient,
                occupiedStrap,
                rescue.LastOnboardCareStatus);
            ResetTargetProgress(rescue);
            RecordRescueHandoff(
                uid,
                rescue,
                patient,
                "terminal-care",
                reason,
                "patient remains physically buckled; manual release is required before ownership can clear");
            Dirty(uid, rescue);
            return;
        }

        if (rescue.AssignedTarget == patient)
            rescue.AssignedTarget = null;
        if (rescue.EvacuatingTarget == patient)
            rescue.EvacuatingTarget = null;
        if (rescue.OnboardCareTarget == patient)
            rescue.OnboardCareTarget = null;
        if (rescue.ManualOverrideTarget == patient)
            RevokeManualOverrideTarget(uid, rescue, patient, "terminal onboard ownership released");
        if (rescue.DeathSignalTarget == patient)
            rescue.DeathSignalTarget = null;
        if (rescue.TaskPatientTarget == patient)
            rescue.TaskPatientTarget = null;

        rescue.AssignedPatientStrap = null;
        rescue.ShuttleRoutedTarget = null;
        rescue.RequiredOnboardHandoffPatients.Remove(patient);
        rescue.LastOnboardCareStatus =
            $"onboard-care terminal handoff: patient={FormatEntityRef(patient)}; ownership=released; {reason}";
        ResetTargetProgress(rescue);
        RecordRescueHandoff(
            uid,
            rescue,
            patient,
            "terminal-care",
            reason,
            "manual specialist/bed intervention requested; coordinator ownership released");
        Dirty(uid, rescue);
    }

    private string FormatHandoffLocation(LuaMRescueAgentComponent rescue, EntityUid patient)
    {
        var location = rescue.AssignedPatientStrap ??
                       rescue.AssignedShuttleAnchor ??
                       rescue.AssignedShuttle;

        return location is { Valid: true } locationUid && !Deleted(locationUid)
            ? FormatEntityRef(locationUid)
            : FormatEntityRef(patient);
    }

    private static string BuildHandoffBlockerSummary(LuaMRescueTeamComponent team, string crewHelp = "unverified")
    {
        var summary = team.RecentThreatMemories <= 0 &&
            team.RecentCrowdMemories <= 0 &&
            team.RecentRouteMemories <= 0 &&
            team.NearbyBlockers <= 0
                ? "none"
                : $"threat/crowd/route={team.RecentThreatMemories}/{team.RecentCrowdMemories}/{team.RecentRouteMemories}; " +
                  $"blockers={team.NearbyBlockers}; scene={team.LastSceneStatus}; memory={team.LastMemoryDigest}";

        var threatClear = BuildHandoffThreatClearSummary(team);
        if (!string.IsNullOrWhiteSpace(threatClear))
            summary = $"{summary}; threatClear={threatClear}";

        var triageCover = BuildHandoffTriageCoverSummary(team);
        if (!string.IsNullOrWhiteSpace(triageCover))
            summary = $"{summary}; triageCover={triageCover}";

        return string.Equals(crewHelp, "unverified", StringComparison.OrdinalIgnoreCase)
            ? summary
            : $"{summary}; crewHelp={crewHelp}";
    }

    private static string BuildHandoffTriageCoverSummary(LuaMRescueTeamComponent team)
    {
        if (string.IsNullOrWhiteSpace(team.LastTriageCoverStatus) ||
            string.Equals(team.LastTriageCoverStatus, "none", StringComparison.OrdinalIgnoreCase) ||
            team.LastTriageCoverStatus.StartsWith("triage-cover: waiting", StringComparison.Ordinal) ||
            !team.LastTriageCoverStatus.StartsWith("triage-cover:", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return team.LastTriageCoverStatus.Trim();
    }

    private static string BuildHandoffThreatClearSummary(LuaMRescueTeamComponent team)
    {
        if (string.IsNullOrWhiteSpace(team.LastThreatNeutralizedStatus) ||
            string.Equals(team.LastThreatNeutralizedStatus, "none", StringComparison.OrdinalIgnoreCase) ||
            !team.LastThreatNeutralizedStatus.StartsWith("threat-neutralized:", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return team.LastThreatNeutralizedStatus.Trim();
    }

    private string BuildHandoffCrewHelpSummary(LuaMRescueTeamComponent team)
    {
        var requests = new List<string>();
        foreach (var escortUid in team.Escorts)
        {
            if (!escortUid.Valid ||
                Deleted(escortUid) ||
                !TryComp<LuaMRescueEscortComponent>(escortUid, out var escort) ||
                string.IsNullOrWhiteSpace(escort.LastCrewHelpStatus) ||
                string.Equals(escort.LastCrewHelpStatus, "none", StringComparison.OrdinalIgnoreCase) ||
                !escort.LastCrewHelpStatus.StartsWith("crew-help:", StringComparison.Ordinal))
            {
                continue;
            }

            requests.Add($"{FormatEscortRole(escort.Role)}={escort.LastCrewHelpStatus.Trim()}");
            if (requests.Count >= 4)
                break;
        }

        return requests.Count == 0
            ? "unverified"
            : $"crew-help requested: {string.Join(" | ", requests)}";
    }

    private static string FormatEscortRole(LuaMRescueEscortRole role)
    {
        return role switch
        {
            LuaMRescueEscortRole.Tourniquet => "tourniquet",
            LuaMRescueEscortRole.Kostyl => "kostyl",
            LuaMRescueEscortRole.Zaslon => "zaslon",
            _ => "escort",
        };
    }

    private string BuildPatientTreatmentResult(EntityUid patient, LuaMRescueAgentComponent rescue)
    {
        if (!TryComp<MobStateComponent>(patient, out var mobState))
            return "patient status unknown";

        if (mobState.CurrentState == MobState.Dead)
            return rescue.AutoDefibDeadPatients ? "dead recovery; onboard defib cycle pending" : "dead recovery";

        if (mobState.CurrentState == MobState.Critical)
            return "critical; treatment continues";

        if (TryComp<DamageableComponent>(patient, out var damageable))
        {
            var damage = damageable.TotalDamage.Float();
            return damage <= rescue.AutoReleaseMaxDamage
                ? $"stable; damage={damage:0.0}"
                : $"alive under observation; damage={damage:0.0}";
        }

        return "alive under observation";
    }

    private bool TryFindStabilizedShuttlePatient(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        out EntityUid patient,
        out EntityUid patientStrap,
        out BuckleComponent buckle,
        out EntityUid holdingPatient,
        out string status)
    {
        patient = default;
        patientStrap = default;
        buckle = default!;
        holdingPatient = default;
        status = string.Empty;

        if (rescue.AssignedShuttle is not { Valid: true } shuttle ||
            Deleted(shuttle))
        {
            return false;
        }

        var holdingStatus = string.Empty;
        var query = EntityQueryEnumerator<StrapComponent, TransformComponent>();
        while (query.MoveNext(out var strapUid, out var strap, out var xform))
        {
            if (xform.GridUid != shuttle ||
                !IsAssignedShuttlePatientStrap(strapUid, rescue))
            {
                continue;
            }

            foreach (var buckled in strap.BuckledEntities)
            {
                if (Deleted(buckled) ||
                    rescue.IgnoredOnboardPatients.Contains(buckled) ||
                    !TryComp<BuckleComponent>(buckled, out var buckledComp) ||
                    buckledComp.BuckledTo != strapUid)
                {
                    continue;
                }

                if (!_activity.IsEligibleRescuePatient(
                        uid,
                        buckled,
                        LuaMRescuePatientRequestKind.OnboardCare,
                        manualOverride: IsManualPatientIntent(rescue, buckled),
                        out var eligibilityFailure) &&
                    eligibilityFailure != LuaMRescueFailureReason.TargetHealthy)
                {
                    continue;
                }

                if (IsPatientStableForRelease(buckled, rescue, out var patientStatus))
                {
                    patient = buckled;
                    patientStrap = strapUid;
                    buckle = buckledComp;
                    status = patientStatus;
                    return true;
                }

                if (string.IsNullOrWhiteSpace(holdingStatus))
                {
                    holdingStatus = patientStatus;
                    holdingPatient = buckled;
                }
            }
        }

        status = holdingStatus;
        return false;
    }

    private bool TryAutoDefibDeadPatientOnShuttle(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn)
    {
        if (!rescue.AutoDefibDeadPatients)
            return false;

        if (!TryFindDeadShuttlePatient(uid, rescue, out var patient, out var patientStrap, out var status))
        {
            if (!string.IsNullOrWhiteSpace(status))
                rescue.LastAutoDefibStatus = status;

            return false;
        }

        SetOnboardCareStatus(rescue, patient, $"dead recovery; {status}");
        SetRescueTask(
            uid,
            rescue,
            LuaMRescueTaskStage.TreatingPatient,
            patient,
            patientStrap,
            $"defibrillating onboard {FormatEntityRef(patient)} at {FormatEntityRef(patientStrap)}");
        TrySayOnboardAction(
            uid,
            rescue,
            patient,
            "onboard-defib",
            "dead recovery defib cycle",
            $"\u0420\u0435\u0430\u043d\u0438\u043c\u0430\u0446\u0438\u044f \u043d\u0430 \u0431\u043e\u0440\u0442\u0443. \u0413\u043e\u0442\u043e\u0432\u043b\u044e \u0434\u0435\u0444\u0438\u0431\u0440\u0438\u043b\u043b\u044f\u0442\u043e\u0440 \u0434\u043b\u044f {Name(patient)}.");

        if (!IsWithinRange(uid, patientStrap, rescue.PlayerActionRange))
        {
            rescue.LastAutoDefibStatus = $"moving to onboard defibrillation {FormatEntityRef(patient)} at {FormatEntityRef(patientStrap)}";
            SetOnboardCareStatus(rescue, patient, $"dead recovery; {rescue.LastAutoDefibStatus}");
            SetFollowDeliveryStrap(uid, rescue, htn, patientStrap);
            Dirty(uid, rescue);
            return true;
        }

        return TryAutoDefibTarget(uid, rescue, htn, patient);
    }

    private bool TryFindDeadShuttlePatient(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        out EntityUid patient,
        out EntityUid patientStrap,
        out string status)
    {
        patient = default;
        patientStrap = default;
        status = string.Empty;

        if (rescue.AssignedShuttle is not { Valid: true } shuttle ||
            Deleted(shuttle))
        {
            return false;
        }

        var query = EntityQueryEnumerator<StrapComponent, TransformComponent>();
        while (query.MoveNext(out var strapUid, out var strap, out var xform))
        {
            if (xform.GridUid != shuttle ||
                !IsAssignedShuttlePatientStrap(strapUid, rescue))
            {
                continue;
            }

            foreach (var buckled in strap.BuckledEntities)
            {
                if (Deleted(buckled) ||
                    rescue.IgnoredOnboardPatients.Contains(buckled) ||
                    !TryComp<BuckleComponent>(buckled, out var buckledComp) ||
                    buckledComp.BuckledTo != strapUid ||
                    !TryComp<MobStateComponent>(buckled, out var mobState) ||
                    mobState.CurrentState != MobState.Dead)
                {
                    continue;
                }

                if (!_activity.IsEligibleRescuePatient(
                        uid,
                        buckled,
                        LuaMRescuePatientRequestKind.OnboardCare,
                        manualOverride: IsManualPatientIntent(rescue, buckled),
                        out _))
                {
                    continue;
                }

                patient = buckled;
                patientStrap = strapUid;
                status = $"dead onboard patient {FormatEntityRef(buckled)} awaits defibrillation";
                return true;
            }
        }

        return false;
    }

    private bool IsPatientStableForRelease(
        EntityUid patient,
        LuaMRescueAgentComponent rescue,
        out string status)
    {
        status = string.Empty;

        if (!TryComp<MobStateComponent>(patient, out var mobState))
        {
            status = $"holding onboard patient {FormatEntityRef(patient)} without mob state";
            return false;
        }

        if (mobState.CurrentState == MobState.Dead)
        {
            status = $"holding dead onboard patient {FormatEntityRef(patient)}";
            return false;
        }

        if (mobState.CurrentState == MobState.Critical)
        {
            status = $"holding critical onboard patient {FormatEntityRef(patient)}";
            return false;
        }

        if (TryComp<DamageableComponent>(patient, out var damageable) &&
            damageable.TotalDamage.Float() > rescue.AutoReleaseMaxDamage)
        {
            status = $"holding onboard patient {FormatEntityRef(patient)} with damage {damageable.TotalDamage.Float():0.0}";
            return false;
        }

        status = $"stabilized onboard patient {FormatEntityRef(patient)}";
        return true;
    }

    private bool HasPendingOnboardCareOrRelease(EntityUid patient, LuaMRescueAgentComponent rescue)
    {
        if (Deleted(patient) ||
            rescue.IgnoredOnboardPatients.Contains(patient) ||
            !IsBuckledToAssignedShuttlePatientStrap(patient, rescue))
        {
            return false;
        }

        if (rescue.OnboardHandoffAttempts.GetValueOrDefault(patient) >=
            Math.Max(1, rescue.ActivityRoleProfile.MaxAttempts))
        {
            return false;
        }

        if (rescue.TerminalOnboardCareFailures.ContainsKey(patient) ||
            rescue.TerminalDefibrillationFailures.ContainsKey(patient))
        {
            return true;
        }

        if (!TryComp<MobStateComponent>(patient, out var mobState))
            return false;

        if (mobState.CurrentState is MobState.Dead or MobState.Critical)
            return true;

        if (TryComp<DamageableComponent>(patient, out var damageable) &&
            damageable.TotalDamage.Float() > rescue.AutoReleaseMaxDamage)
        {
            return true;
        }

        return rescue.AutoReleaseStabilizedPatients;
    }

    private bool HasPendingEvacuationTarget(EntityUid uid, LuaMRescueAgentComponent rescue, EntityUid completedTarget)
    {
        return TryFindEvacuationTarget(uid, rescue.SearchRange, rescue, out _, completedTarget);
    }

    private bool HasPendingRescueTarget(EntityUid uid, LuaMRescueAgentComponent rescue, EntityUid completedTarget)
    {
        return TryFindPendingRescueTarget(uid, rescue, completedTarget, out _, out _);
    }

    private bool TryFindPendingRescueTarget(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        EntityUid completedTarget,
        out EntityUid pendingTarget,
        out string pendingReason)
    {
        if (TryFindEvacuationTarget(uid, rescue.SearchRange, rescue, out pendingTarget, completedTarget))
        {
            pendingReason = "pending-evacuation";
            return true;
        }

        if (TryComp<MedibotComponent>(uid, out var medibot) &&
            TryFindRescueTarget(uid, rescue.SearchRange, rescue, medibot, out pendingTarget, completedTarget))
        {
            pendingReason = "pending-rescue";
            return true;
        }

        pendingTarget = default;
        pendingReason = "none";
        return false;
    }

    private bool TryFindPatientDeliveryStrap(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        out EntityUid patientStrap,
        out StrapComponent strap)
    {
        patientStrap = default;
        strap = default!;

        if (!rescue.BucklePatientsOnShuttle ||
            rescue.AssignedShuttle is not { Valid: true } shuttle ||
            Deleted(shuttle))
        {
            return false;
        }

        var patient = rescue.EvacuatingTarget ?? rescue.TaskPatientTarget;

        if (rescue.AssignedPatientStrap is { Valid: true } assigned &&
            !Deleted(assigned) &&
            TryComp<StrapComponent>(assigned, out var assignedStrap) &&
            IsAvailablePatientDeliveryStrap(assigned, assignedStrap, rescue, patient) &&
            IsReachablePatientDeliveryStrap(uid, assigned, rescue, out _))
        {
            patientStrap = assigned;
            strap = assignedStrap;
            return true;
        }

        var bestScore = float.MinValue;
        var query = EntityQueryEnumerator<StrapComponent, TransformComponent>();
        while (query.MoveNext(out var strapUid, out var strapComp, out var xform))
        {
            if (xform.GridUid != shuttle ||
                !IsAvailablePatientDeliveryStrap(strapUid, strapComp, rescue, patient) ||
                !IsReachablePatientDeliveryStrap(uid, strapUid, rescue, out var routeDistance))
            {
                continue;
            }

            var score = GetPatientDeliveryStrapScore(strapUid, rescue) - routeDistance;
            if (score <= bestScore)
                continue;

            patientStrap = strapUid;
            strap = strapComp;
            bestScore = score;
        }

        return patientStrap != default;
    }

    private bool IsReachablePatientDeliveryStrap(
        EntityUid rescuer,
        EntityUid patientStrap,
        LuaMRescueAgentComponent rescue,
        out float routeDistance)
    {
        var route = ProbePatientDeliveryStrapRoute(rescuer, patientStrap, rescue);
        routeDistance = route.Distance;
        return route.State == LuaMRescuePathProbeState.Reachable;
    }

    private LuaMRescuePathProbeSnapshot ProbePatientDeliveryStrapRoute(
        EntityUid rescuer,
        EntityUid patientStrap,
        LuaMRescueAgentComponent rescue)
    {
        var rescuerGrid = Transform(rescuer).GridUid;
        var strapGrid = Transform(patientStrap).GridUid;
        var confirmedDockedCrossGrid = false;
        if (rescuerGrid != strapGrid &&
            rescue.AssignedShuttle is { Valid: true } shuttle &&
            strapGrid == shuttle &&
            rescuerGrid is { Valid: true } outsideGrid &&
            TryComp<LuaMRescueShuttleLifecycleComponent>(shuttle, out var lifecycle))
        {
            confirmedDockedCrossGrid = lifecycle.State == LuaMRescueShuttleRouteState.Docked &&
                                       IsShuttleDockedToGrid(shuttle, outsideGrid);
        }

        return _rescueNavigation.ProbeRoute(
            rescuer,
            patientStrap,
            Math.Min(rescue.PlayerActionRange, rescue.AutoReleaseRange),
            confirmedDockedCrossGrid);
    }

    /// <summary>
    /// Once a patient/bed pair is terminal, only a reachable or still-pending
    /// alternate bed keeps the mission active. A merely empty but confirmed
    /// NoPath bed must not cause periodic auto-reacquire forever.
    /// </summary>
    public bool HasViablePatientDeliveryStrap(
        EntityUid rescuer,
        LuaMRescueAgentComponent rescue,
        EntityUid patient)
    {
        return GetPatientDeliveryStrapState(rescuer, rescue, patient) == PatientDeliveryStrapState.Viable;
    }

    private bool ArePatientDeliveryStrapsAuthoritativelyExhausted(
        EntityUid rescuer,
        LuaMRescueAgentComponent rescue,
        EntityUid patient)
    {
        return GetPatientDeliveryStrapState(rescuer, rescue, patient) == PatientDeliveryStrapState.Exhausted;
    }

    private PatientDeliveryStrapState GetPatientDeliveryStrapState(
        EntityUid rescuer,
        LuaMRescueAgentComponent rescue,
        EntityUid patient)
    {
        if (!rescue.BucklePatientsOnShuttle ||
            rescue.AssignedShuttle is not { Valid: true } shuttle ||
            Deleted(shuttle))
        {
            return PatientDeliveryStrapState.TemporarilyUnavailable;
        }

        var foundCandidate = false;
        var foundRecoverableBlocker = false;
        var query = EntityQueryEnumerator<StrapComponent, TransformComponent>();
        while (query.MoveNext(out var strapUid, out var strap, out var xform))
        {
            if (xform.GridUid != shuttle ||
                !IsAssignedShuttlePatientStrap(strapUid, rescue))
            {
                continue;
            }

            foundCandidate = true;
            if (rescue.TerminalPatientBuckleFailures.ContainsKey((patient, strapUid)))
                continue;

            if (!strap.Enabled ||
                strap.BuckledEntities.Count != 0 ||
                IsDeliveryTargetTemporarilySkipped(strapUid, rescue))
            {
                foundRecoverableBlocker = true;
                continue;
            }

            var route = ProbePatientDeliveryStrapRoute(rescuer, strapUid, rescue);
            if (route.State is LuaMRescuePathProbeState.Pending or LuaMRescuePathProbeState.Reachable)
                return PatientDeliveryStrapState.Viable;

            if (route.State is LuaMRescuePathProbeState.DifferentGrid or LuaMRescuePathProbeState.Invalid)
                foundRecoverableBlocker = true;
        }

        if (!foundCandidate || foundRecoverableBlocker)
            return PatientDeliveryStrapState.TemporarilyUnavailable;

        // Every physical candidate is either an explicitly terminal pair or
        // has an authoritative NoPath/AccessDenied/NoLineOfSight result.
        return PatientDeliveryStrapState.Exhausted;
    }

    private bool IsAvailablePatientDeliveryStrap(
        EntityUid uid,
        StrapComponent strap,
        LuaMRescueAgentComponent rescue,
        EntityUid? patient = null)
    {
        return strap.Enabled &&
               !IsDeliveryTargetTemporarilySkipped(uid, rescue) &&
               !(patient is { Valid: true } patientUid &&
                 rescue.TerminalPatientBuckleFailures.ContainsKey((patientUid, uid))) &&
               strap.BuckledEntities.Count == 0 &&
               IsAssignedShuttlePatientStrap(uid, rescue);
    }

    private bool HasAvailablePatientDeliveryStrap(
        LuaMRescueAgentComponent rescue,
        EntityUid? patient = null)
    {
        if (!rescue.BucklePatientsOnShuttle ||
            rescue.AssignedShuttle is not { Valid: true } shuttle ||
            Deleted(shuttle))
        {
            return false;
        }

        var query = EntityQueryEnumerator<StrapComponent, TransformComponent>();
        while (query.MoveNext(out var strapUid, out var strap, out var xform))
        {
            if (xform.GridUid == shuttle && IsAvailablePatientDeliveryStrap(strapUid, strap, rescue, patient))
                return true;
        }

        return false;
    }

    private bool IsAssignedShuttlePatientStrap(EntityUid uid, LuaMRescueAgentComponent rescue)
    {
        if (rescue.AssignedShuttle is not { Valid: true } shuttle ||
            Deleted(shuttle) ||
            !HasComp<StrapComponent>(uid) ||
            Transform(uid).GridUid != shuttle)
        {
            return false;
        }

        if (rescue.PreferStasisBedDelivery && HasComp<StasisBedComponent>(uid))
            return true;

        return HasComp<HealOnBuckleComponent>(uid);
    }

    private float GetPatientDeliveryStrapScore(EntityUid uid, LuaMRescueAgentComponent rescue)
    {
        var score = 0f;
        if (HasComp<StasisBedComponent>(uid))
            score += 1000f;
        else if (HasComp<HealOnBuckleComponent>(uid))
            score += 100f;

        if (TryGetShuttleAnchorCoordinates(rescue, out var anchorCoordinates) &&
            Transform(uid).Coordinates.TryDistance(EntityManager, anchorCoordinates, out var distance))
        {
            score -= distance;
        }

        return score;
    }

    private PatientBuckleResult TryBucklePatientToStrap(
        EntityUid uid,
        EntityUid target,
        EntityUid patientStrap,
        LuaMRescueAgentComponent rescue)
    {
        if (!rescue.BucklePatientsOnShuttle || Deleted(target) || Deleted(patientStrap))
        {
            return PatientBuckleResult.TerminalFailure;
        }

        var pair = (Patient: target, Strap: patientStrap);
        if (!IsAssignedShuttlePatientStrap(patientStrap, rescue) ||
            !TryComp<BuckleComponent>(target, out var buckle) ||
            !TryComp<StrapComponent>(patientStrap, out var strap))
        {
            var structuralAttempts = Math.Max(1, rescue.ActivityRoleProfile.MaxAttempts);
            rescue.PatientBuckleAttempts[pair] = structuralAttempts;
            rescue.NextPatientBuckleAttemptAt.Remove(pair);
            var status =
                $"InvalidPatient: buckle contract unavailable; patient={FormatEntityRef(target)}; " +
                $"strap={FormatEntityRef(patientStrap)}";
            rescue.TerminalPatientBuckleFailures[pair] = status;
            rescue.LastAutoEvacuationStatus = status;
            Dirty(uid, rescue);
            return PatientBuckleResult.TerminalFailure;
        }

        if (rescue.TerminalPatientBuckleFailures.TryGetValue(pair, out var terminalFailure))
        {
            rescue.LastAutoEvacuationStatus = terminalFailure;
            return PatientBuckleResult.TerminalFailure;
        }

        if (buckle.BuckledTo == patientStrap)
        {
            ClearPatientBuckleFailures(rescue, target);
            _activity.Complete(uid, out _);
            return PatientBuckleResult.Succeeded;
        }

        if (!strap.Enabled ||
            strap.BuckledEntities.Count != 0 ||
            !IsWithinRange(target, patientStrap, buckle.Range))
        {
            return PatientBuckleResult.NotReady;
        }

        if (rescue.NextPatientBuckleAttemptAt.TryGetValue(pair, out var retryAt) &&
            _timing.CurTime < retryAt)
        {
            rescue.LastAutoEvacuationStatus =
                $"buckle retry backoff until {retryAt.TotalSeconds:0.00}s; " +
                $"patient={FormatEntityRef(target)}; strap={FormatEntityRef(patientStrap)}";
            return PatientBuckleResult.Waiting;
        }

        BeginPatientActivity(
            uid,
            rescue,
            LuaMRescueActivity.BucklePatient,
            target,
            new EntityCoordinates(patientStrap, Vector2.Zero));
        if (rescue.ActivityContext.Activity == LuaMRescueActivity.BucklePatient &&
            rescue.ActivityContext.Target == target &&
            rescue.ActivityContext.TerminalStatus is LuaMRescueTerminalStatus.Blocked or LuaMRescueTerminalStatus.Failed)
        {
            return PatientBuckleResult.TerminalFailure;
        }

        var buckled = _buckle.TryBuckle(target, uid, patientStrap, buckle, popup: false);
        if (buckled)
        {
            ClearPatientBuckleFailures(rescue, target);
            _activity.Complete(uid, out _);
            return PatientBuckleResult.Succeeded;
        }

        var attempts = rescue.PatientBuckleAttempts.GetValueOrDefault(pair) + 1;
        rescue.PatientBuckleAttempts[pair] = attempts;
        var backoffSeconds = Math.Min(
            Math.Max(1d, rescue.ActivityRoleProfile.MaxRetryBackoff.TotalSeconds),
            Math.Max(0.1d, rescue.ActivityRoleProfile.BaseRetryBackoff.TotalSeconds) * Math.Pow(2d, attempts - 1));
        rescue.NextPatientBuckleAttemptAt[pair] =
            _timing.CurTime + TimeSpan.FromSeconds(backoffSeconds);
        _activity.RecordAttempt(uid, out var snapshot);
        if (attempts >= Math.Max(1, rescue.ActivityRoleProfile.MaxAttempts) || snapshot.IsTerminal)
        {
            var status =
                $"ActionCancelled: buckle failed after {attempts} bounded attempts; " +
                $"patient={FormatEntityRef(target)}; strap={FormatEntityRef(patientStrap)}";
            rescue.TerminalPatientBuckleFailures[pair] = status;
            rescue.LastAutoEvacuationStatus = status;
            _activity.Fail(uid, LuaMRescueFailureReason.ActionCancelled, out _);
            TrySendRescueStatusComms(
                uid,
                rescue,
                $"buckle-terminal:{target}:{patientStrap}",
                $"Не могу закрепить {Name(target)} на койке после {attempts} попыток. Ищу другую койку или передаю пациента экипажу.");
            Dirty(uid, rescue);
            return PatientBuckleResult.TerminalFailure;
        }

        rescue.LastAutoEvacuationStatus =
            $"ActionCancelled: buckle attempt {attempts}/{rescue.ActivityRoleProfile.MaxAttempts} failed; " +
            $"retry in {backoffSeconds:0.0}s";
        Dirty(uid, rescue);
        return PatientBuckleResult.Waiting;
    }

    private bool IsBuckledToAssignedShuttlePatientStrap(EntityUid target, LuaMRescueAgentComponent rescue)
    {
        return TryComp<BuckleComponent>(target, out var buckle) &&
               buckle.BuckledTo is { Valid: true } strap &&
               !Deleted(strap) &&
               IsAssignedShuttlePatientStrap(strap, rescue);
    }

    private void SetFollowTarget(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        EntityUid target)
    {
        if (rescue.PendingMedicalDoAfterTarget == target &&
            rescue.PendingMedicalIntentGeneration == rescue.ActivityContext.Generation)
        {
            // Repeated coordinator/manual updates for the same patient must not
            // wake FollowCompound while a BreakOnMove medical action is running.
            rescue.AssignedTarget = target;
            HoldMovementForMedicalDoAfter(uid, rescue, target);
            Dirty(uid, rescue);
            return;
        }

        if (rescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active &&
            rescue.ActivityContext.Activity == LuaMRescueActivity.TreatPatient &&
            rescue.ActivityContext.Target == target &&
            _timing.CurTime < rescue.NextAutoTreatmentAttempt)
        {
            // Movement is an executor of the current medical intent, not another
            // owner. A follow request for the same patient during a bounded item
            // cooldown must therefore preserve TreatPatient's generation.
            rescue.AssignedTarget = target;
            SetRescueTask(
                uid,
                rescue,
                LuaMRescueTaskStage.TreatingPatient,
                target,
                null,
                $"waiting for treatment cooldown on {FormatEntityRef(target)}");

            if (IsWithinRange(uid, target, rescue.PlayerActionRange))
            {
                HoldMovementForTreatmentCooldown(uid, htn);
            }
            else
            {
                SetMovementGoal(
                    uid,
                    rescue,
                    htn,
                    new EntityCoordinates(target, Vector2.Zero),
                    rescue.PlayerActionRange);
            }

            Dirty(uid, rescue);
            return;
        }

        if (rescue.AssignedTarget != target ||
            rescue.PendingMedicalDoAfterTarget is { } pendingMedicalTarget && pendingMedicalTarget != target)
        {
            PrepareForExternalIntentReplacement(uid, target);
        }

        var playerAction = rescue.PendingPlayerAction != LuaMRescuePlayerActionKind.None &&
                           rescue.PendingPlayerActionTarget == target;
        var manual = playerAction;
        var desiredActivity = playerAction
            ? LuaMRescueActivity.ManualAction
            : LuaMRescueActivity.ApproachPatient;

        rescue.AssignedTarget = target;
        var actionRange = GetPatientApproachActionRange(rescue, desiredActivity);
        var agentGrid = Transform(uid).GridUid;
        var targetGrid = Transform(target).GridUid;
        var crossGrid = agentGrid != targetGrid;
        var dockedForTarget = false;
        LuaMRescueShuttleLifecycleComponent? lifecycle = null;
        if (TryRouteThroughGateway(uid, rescue, htn, target, manual))
        {
            Dirty(uid, rescue);
            return;
        }

        if (crossGrid &&
            rescue.AssignedShuttle is { Valid: true } shuttle &&
            !Deleted(shuttle))
        {
            TryRouteShuttleToTarget(uid, rescue, target);
            TryComp(shuttle, out lifecycle);
            dockedForTarget = lifecycle?.Target == target &&
                              lifecycle.State == LuaMRescueShuttleRouteState.Docked &&
                              lifecycle.SafeExitConfirmed &&
                              targetGrid is { Valid: true } destinationGrid &&
                              IsShuttleDockedToGrid(shuttle, destinationGrid);
        }

        if (crossGrid && !dockedForTarget)
        {
            if (!BeginPatientActivity(
                    uid,
                    rescue,
                    LuaMRescueActivity.PlanningRoute,
                    target,
                    new EntityCoordinates(target, Vector2.Zero)))
            {
                HandlePatientRouteFailure(
                    uid,
                    rescue,
                    htn,
                    target,
                    manual,
                    rescue.ActivityContext.FailureReason == LuaMRescueFailureReason.None
                        ? LuaMRescueFailureReason.DeadlineExceeded
                        : rescue.ActivityContext.FailureReason,
                    "planning activity reached a terminal state");
                return;
            }
            HoldMovementForRoute(uid, htn);

            if (lifecycle != null &&
                (lifecycle.State is LuaMRescueShuttleRouteState.Failed or LuaMRescueShuttleRouteState.TimedOut) &&
                lifecycle.RetryCount >= lifecycle.EffectiveMaxRetries)
            {
                HandlePatientRouteFailure(
                    uid,
                    rescue,
                    htn,
                    target,
                    manual,
                    LuaMRescueFailureReason.ShuttleRouteFailed,
                    $"{lifecycle.State}: {lifecycle.LastStatus}");
                return;
            }

            rescue.LastTargetTrackingStatus = lifecycle == null
                ? "shuttle route lifecycle unavailable; holding onboard"
                : $"shuttle={lifecycle.State}; holding onboard until Docked; {lifecycle.LastStatus}";
            _activity.RecordProgress(
                uid,
                new EntityCoordinates(target, Vector2.Zero),
                null,
                LuaMRescueRouteStatus.Planning,
                LuaMRescueDoAfterStatus.None,
                out _);
            Dirty(uid, rescue);
            return;
        }

        var route = _rescueNavigation.ProbeRoute(uid, target, actionRange, dockedForTarget);
        if (route.State == LuaMRescuePathProbeState.Pending)
        {
            if (!BeginPatientActivity(
                    uid,
                    rescue,
                    LuaMRescueActivity.PlanningRoute,
                    target,
                    new EntityCoordinates(target, Vector2.Zero)))
            {
                HandlePatientRouteFailure(
                    uid,
                    rescue,
                    htn,
                    target,
                    manual,
                    rescue.ActivityContext.FailureReason == LuaMRescueFailureReason.None
                        ? LuaMRescueFailureReason.DeadlineExceeded
                        : rescue.ActivityContext.FailureReason,
                    "path planning activity reached a terminal state");
                return;
            }
            HoldMovementForRoute(uid, htn);
            rescue.LastTargetTrackingStatus = $"route planning to {FormatEntityRef(target)}";
            _activity.RecordProgress(
                uid,
                new EntityCoordinates(target, Vector2.Zero),
                route.Distance,
                LuaMRescueRouteStatus.Planning,
                LuaMRescueDoAfterStatus.None,
                out _);
            Dirty(uid, rescue);
            return;
        }

        if (route.State != LuaMRescuePathProbeState.Reachable)
        {
            var reason = route.State switch
            {
                LuaMRescuePathProbeState.DifferentGrid => LuaMRescueFailureReason.ShuttleUnavailable,
                LuaMRescuePathProbeState.AccessDenied => LuaMRescueFailureReason.AccessDenied,
                LuaMRescuePathProbeState.NoLineOfSight => LuaMRescueFailureReason.NoLineOfSight,
                _ => LuaMRescueFailureReason.NoPath,
            };
            HandlePatientRouteFailure(
                uid,
                rescue,
                htn,
                target,
                manual,
                reason,
                route.State.ToString(),
                authoritativeRouteFailure: true);
            return;
        }

        if (rescue.ActivityContext.Activity != desiredActivity ||
            rescue.ActivityContext.Target != target ||
            rescue.ActivityContext.TerminalStatus != LuaMRescueTerminalStatus.Active)
        {
            if (!BeginPatientActivity(uid, rescue, desiredActivity, target, new EntityCoordinates(target, Vector2.Zero)))
            {
                HandlePatientRouteFailure(
                    uid,
                    rescue,
                    htn,
                    target,
                    manual,
                    rescue.ActivityContext.FailureReason == LuaMRescueFailureReason.None
                        ? LuaMRescueFailureReason.DeadlineExceeded
                        : rescue.ActivityContext.FailureReason,
                    "approach activity reached a terminal state");
                return;
            }
        }

        var madePhysicalRouteProgress = route.Distance <= actionRange ||
                                        rescue.ActivityContext.LastProgressDistance is { } previousDistance &&
                                        route.Distance + Math.Max(
                                            0f,
                                            rescue.ActivityRoleProfile.ProgressTolerance) < previousDistance;
        _activity.RecordProgress(
            uid,
            new EntityCoordinates(target, Vector2.Zero),
            route.Distance,
            route.Distance <= actionRange ? LuaMRescueRouteStatus.Arrived : LuaMRescueRouteStatus.Moving,
            LuaMRescueDoAfterStatus.None,
            out _);
        if (madePhysicalRouteProgress && !HasDurableRouteFailure(rescue, target))
            rescue.RouteFailureAttempts.Remove(target);
        SetMovementGoal(
            uid,
            rescue,
            htn,
            new EntityCoordinates(target, Vector2.Zero),
            actionRange);
        Dirty(uid, rescue);
    }

    /// <summary>
    /// Routes a rescuer through a currently open, reciprocal expedition gateway when its linked
    /// endpoint is on the patient's map. NPC steering stops at the gateway's non-free navigation
    /// polygon, so a rescuer already within unobstructed interaction range uses the linked-portal
    /// transfer contract explicitly and immediately receives the patient goal on the far side.
    /// </summary>
    private bool TryRouteThroughGateway(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        EntityUid target,
        bool manual)
    {
        var agentMap = Transform(uid).MapID;
        var targetMap = Transform(target).MapID;
        if (agentMap == targetMap || agentMap == MapId.Nullspace || targetMap == MapId.Nullspace)
            return false;

        EntityUid? bestGateway = null;
        LuaMRescuePathProbeSnapshot bestRoute = default;
        EntityUid? pendingGateway = null;
        LuaMRescuePathProbeSnapshot pendingRoute = default;
        var query = AllEntityQuery<GatewayComponent, PortalComponent, TransformComponent>();
        while (query.MoveNext(out var gateway, out var gatewayComp, out var portalComp, out var gatewayXform))
        {
            if (!TryResolveSingleReciprocalGatewayEndpoint(
                    gateway,
                    gatewayComp,
                    portalComp,
                    gatewayXform,
                    agentMap,
                    targetMap,
                    out _))
            {
                continue;
            }

            // Gateway fixtures are intentionally non-free navigation polygons. Stock NPC
            // steering considers that boundary arrived at interaction range, while the strict
            // 0.1 route probe may already report NoPath. Retain the fully validated endpoint so
            // the controlled transfer below can finish the crossing.
            if (_interaction.InRangeUnobstructed(
                    uid,
                    gateway,
                    SharedInteractionSystem.InteractionRange))
            {
                bestGateway = gateway;
                bestRoute = default;
                break;
            }

            // The controlled transfer below fires from fixture-aware interaction
            // range. Probe for that reachable boundary instead of the blocked
            // gateway centre; the published HTN goal remains 0.1 m so ordinary
            // steering still enters the fixture when it can.
            var route = _rescueNavigation.ProbeRoute(
                uid,
                gateway,
                SharedInteractionSystem.InteractionRange);
            if (route.State == LuaMRescuePathProbeState.Pending)
            {
                if (pendingGateway == null || route.Distance < pendingRoute.Distance)
                {
                    pendingGateway = gateway;
                    pendingRoute = route;
                }
                continue;
            }

            if (route.State != LuaMRescuePathProbeState.Reachable ||
                bestGateway != null && route.Distance >= bestRoute.Distance)
            {
                continue;
            }

            bestGateway = gateway;
            bestRoute = route;
        }

        if (bestGateway == null && pendingGateway is { } planningGateway)
        {
            if (!BeginPatientActivity(
                    uid,
                    rescue,
                    LuaMRescueActivity.PlanningRoute,
                    target,
                    new EntityCoordinates(planningGateway, Vector2.Zero)))
            {
                HandlePatientRouteFailure(
                    uid,
                    rescue,
                    htn,
                    target,
                    manual,
                    rescue.ActivityContext.FailureReason == LuaMRescueFailureReason.None
                        ? LuaMRescueFailureReason.DeadlineExceeded
                        : rescue.ActivityContext.FailureReason,
                    "gateway planning activity reached a terminal state");
                return true;
            }

            HoldMovementForRoute(uid, htn);
            rescue.LastTargetTrackingStatus =
                $"planning gateway route via {FormatEntityRef(planningGateway)} to map {targetMap}";
            _activity.RecordProgress(
                uid,
                new EntityCoordinates(planningGateway, Vector2.Zero),
                pendingRoute.Distance,
                LuaMRescueRouteStatus.Planning,
                LuaMRescueDoAfterStatus.None,
                out _);
            return true;
        }

        if (bestGateway is not { } portal)
            return false;

        if (!BeginPatientActivity(
                uid,
                rescue,
                manual ? LuaMRescueActivity.ManualAction : LuaMRescueActivity.ApproachPatient,
                target,
                new EntityCoordinates(portal, Vector2.Zero)))
        {
            HandlePatientRouteFailure(
                uid,
                rescue,
                htn,
                target,
                manual,
                rescue.ActivityContext.FailureReason == LuaMRescueFailureReason.None
                    ? LuaMRescueFailureReason.DeadlineExceeded
                    : rescue.ActivityContext.FailureReason,
                "gateway approach activity reached a terminal state");
            return true;
        }

        if (_interaction.InRangeUnobstructed(
                uid,
                portal,
                SharedInteractionSystem.InteractionRange))
        {
            // Cancel the old near-zero gateway executor before teleporting. Otherwise it can
            // survive on the far side and steer the rescuer back into the arrival gateway.
            HoldMovementForRoute(uid, htn);
            if (_portal.TryTeleportThroughLinkedPortal(portal, uid, ignoreTimeout: true))
            {
                rescue.LastTargetTrackingStatus =
                    $"crossed open gateway {FormatEntityRef(portal)} to patient {FormatEntityRef(target)}";
                _activity.RecordProgress(
                    uid,
                    new EntityCoordinates(target, Vector2.Zero),
                    null,
                    LuaMRescueRouteStatus.Moving,
                    LuaMRescueDoAfterStatus.None,
                    out _);
                SetFollowTarget(uid, rescue, htn, target);
                return true;
            }

            rescue.LastTargetTrackingStatus =
                $"gateway transfer failed at {FormatEntityRef(portal)}; reassessing patient route";
            return true;
        }

        rescue.LastTargetTrackingStatus =
            $"approaching open gateway {FormatEntityRef(portal)} to patient map {targetMap}";
        _activity.RecordProgress(
            uid,
            new EntityCoordinates(portal, Vector2.Zero),
            bestRoute.Distance,
            LuaMRescueRouteStatus.Moving,
            LuaMRescueDoAfterStatus.None,
            out _);
        SetMovementGoal(uid, rescue, htn, new EntityCoordinates(portal, Vector2.Zero), 0.1f);
        return true;
    }

    private void HoldMovementForRoute(EntityUid uid, HTNComponent htn)
    {
        if (htn.Blackboard.ContainsKey(NPCBlackboard.FollowTarget) || htn.Plan != null)
            CancelCurrentHtnMovement(uid, htn, cancelRouteProbe: false);

        htn.Blackboard.Remove<EntityCoordinates>(NPCBlackboard.FollowTarget);
    }

    private void HandlePatientRouteFailure(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        EntityUid target,
        bool manual,
        LuaMRescueFailureReason reason,
        string detail,
        bool authoritativeRouteFailure = false)
    {
        var failureDisposition = manual
            ? PatientRouteFailureDisposition.TemporarySkip
            : GetPatientRouteFailureDisposition(
                rescue,
                target,
                reason,
                authoritativeRouteFailure);

        HoldMovementForRoute(uid, htn);
        _rescueNavigation.CancelRoute(uid, target);
        // Close the failed patient generation before TemporarilySkipTarget can
        // create Returning/Standby as the fallback generation.
        _activity.Block(uid, reason, LuaMRescueActivity.Standby, out _);
        if (manual)
        {
            ClearPendingPlayerAction(rescue, $"{reason}: {detail}");
            ClearRescueTask(uid, rescue, $"manual route failed: {reason}; {detail}");
            rescue.AssignedTarget = null;
        }
        else
        {
            TemporarilySkipTarget(uid, rescue, htn, target);
            var requiredHandoff = rescue.RequiredOnboardHandoffPatients.Contains(target);
            if (requiredHandoff &&
                failureDisposition is PatientRouteFailureDisposition.DormantRecovery or
                    PatientRouteFailureDisposition.DurableSkip)
            {
                // Dormant recovery deliberately has no active owner. Required
                // custody must keep one, so an exhausted route becomes a
                // durable blocked handoff until an explicit redispatch repairs
                // the route budget.
                failureDisposition = PatientRouteFailureDisposition.DurableSkip;
                ArmDurableRouteFailure(uid, rescue, target, reason, detail);
            }
            else if (failureDisposition == PatientRouteFailureDisposition.DormantRecovery)
            {
                ArmDormantRouteRecovery(uid, rescue, target, reason, detail);
            }
            else if (failureDisposition == PatientRouteFailureDisposition.DurableSkip)
            {
                ArmDurableRouteFailure(uid, rescue, target, reason, detail);
            }
        }

        rescue.LastTargetTrackingStatus =
            $"route terminal: target={FormatEntityRef(target)}; reason={reason}; " +
            $"disposition={failureDisposition}; detail={detail}";
        Dirty(uid, rescue);
    }

    private static float GetPatientApproachActionRange(
        LuaMRescueAgentComponent rescue,
        LuaMRescueActivity activity = LuaMRescueActivity.ApproachPatient)
    {
        var profileRange = rescue.ActivityRoleProfile.GetActionRange(
            activity,
            rescue.PlayerActionRange);
        return Math.Min(profileRange, rescue.EvacuationStartRange);
    }

    private PatientRouteFailureDisposition GetPatientRouteFailureDisposition(
        LuaMRescueAgentComponent rescue,
        EntityUid target,
        LuaMRescueFailureReason reason,
        bool authoritativeRouteFailure)
    {
        if (!IsBoundedRouteFailure(reason))
            return PatientRouteFailureDisposition.TemporarySkip;

        var attempts = rescue.RouteFailureAttempts.GetValueOrDefault(target);
        if (authoritativeRouteFailure || IsTerminalRouteBudgetFailure(reason))
        {
            attempts++;
            rescue.RouteFailureAttempts[target] = attempts;
        }

        var maxAttempts = Math.Max(1, rescue.ActivityRoleProfile.MaxAttempts);
        var activityBudgetExhausted = rescue.ActivityContext.Target == target &&
                                      (rescue.ActivityContext.TerminalStatus is LuaMRescueTerminalStatus.Blocked
                                          or LuaMRescueTerminalStatus.Failed) &&
                                      rescue.ActivityContext.Attempts >= maxAttempts;
        if (attempts < maxAttempts && !activityBudgetExhausted)
            return PatientRouteFailureDisposition.TemporarySkip;

        // A route becoming reachable is sufficient evidence to recover NoPath
        // and shuttle infrastructure failures. It is not sufficient evidence to
        // recover an activity that already timed out on a reachable route; that
        // requires an explicit administrative redispatch.
        return reason == LuaMRescueFailureReason.DeadlineExceeded
            ? PatientRouteFailureDisposition.DurableSkip
            : PatientRouteFailureDisposition.DormantRecovery;
    }

    private static bool HasDurableRouteFailure(
        LuaMRescueAgentComponent rescue,
        EntityUid target)
    {
        return rescue.RouteFailureAttempts.ContainsKey(target) &&
               rescue.SkippedTargets.TryGetValue(target, out var skipUntil) &&
               skipUntil == TimeSpan.MaxValue;
    }

    private static bool IsBoundedRouteFailure(LuaMRescueFailureReason reason)
    {
        return reason is LuaMRescueFailureReason.NoPath
            or LuaMRescueFailureReason.AccessDenied
            or LuaMRescueFailureReason.NoLineOfSight
            or LuaMRescueFailureReason.RouteBlocked
            or LuaMRescueFailureReason.DeadlineExceeded
            or LuaMRescueFailureReason.ShuttleRouteFailed
            or LuaMRescueFailureReason.ShuttleUnavailable;
    }

    private static bool IsTerminalRouteBudgetFailure(LuaMRescueFailureReason reason)
    {
        return reason is LuaMRescueFailureReason.DeadlineExceeded
            or LuaMRescueFailureReason.ShuttleRouteFailed
            or LuaMRescueFailureReason.ShuttleUnavailable;
    }

    private void ArmDurableRouteFailure(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        EntityUid target,
        LuaMRescueFailureReason reason,
        string detail)
    {
        // The order that created this generation is no longer authoritative.
        // Keeping its manual-override mirror would let the periodic target
        // refresh silently resume the same patient even though a durable route
        // failure explicitly requires a new administrative redispatch.
        if (rescue.ManualOverrideTarget == target)
        {
            RevokeManualOverrideTarget(
                uid,
                rescue,
                target,
                $"durable route failure: {reason}; explicit redispatch required");
        }

        rescue.SkippedTargets[target] = TimeSpan.MaxValue;
        rescue.LastDormantRouteStatus =
            $"durable target={FormatEntityRef(target)}; reason={reason}; " +
            $"attempts={rescue.RouteFailureAttempts.GetValueOrDefault(target)}/" +
            $"{Math.Max(1, rescue.ActivityRoleProfile.MaxAttempts)}; " +
            $"automatic recovery disabled; explicit redispatch required; detail={detail}";
        Dirty(uid, rescue);
    }

    private void ArmDormantRouteRecovery(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        EntityUid target,
        LuaMRescueFailureReason reason,
        string detail)
    {
        if (rescue.DormantRouteTarget is { Valid: true } previous && previous != target)
            ClearDormantRouteRecovery(rescue, "replaced by another terminal route", clearFailureBudget: true);

        var newlyArmed = rescue.DormantRouteTarget != target;
        rescue.DormantRouteTarget = target;
        rescue.DormantRouteActionRange = GetPatientApproachActionRange(rescue);
        rescue.DormantRouteProbeInFlight = false;
        rescue.DormantRouteProbeStartedAt = TimeSpan.Zero;
        rescue.SkippedTargets[target] = TimeSpan.MaxValue;
        if (newlyArmed)
        {
            rescue.NextDormantRouteProbeAt = _timing.CurTime +
                TimeSpan.FromSeconds(Math.Max(0.1f, rescue.DormantRouteObservationSeconds));
        }

        rescue.LastDormantRouteStatus =
            $"armed target={FormatEntityRef(target)}; reason={reason}; " +
            $"attempts={rescue.RouteFailureAttempts.GetValueOrDefault(target)}/" +
            $"{Math.Max(1, rescue.ActivityRoleProfile.MaxAttempts)}; detail={detail}";
        Dirty(uid, rescue);
    }

    /// <summary>
    /// Observes a terminal route outside the active activity/HTN owner. Failed
    /// observations are rate-limited and never consume or reset the action retry
    /// budget. A confirmed reachable route creates exactly one fresh intent.
    /// </summary>
    private bool TryUpdateDormantRouteRecovery(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn)
    {
        if (rescue.DormantRouteTarget is not { Valid: true } target)
            return false;

        if (Deleted(target))
        {
            _rescueNavigation.CancelRoute(uid, target);
            ClearDormantRouteRecovery(
                rescue,
                $"cancelled target={FormatEntityRef(target)}; reason={LuaMRescueFailureReason.TargetLost}",
                clearFailureBudget: true);
            Dirty(uid, rescue);
            return false;
        }

        if (!IsEligibleAssignedPatient(uid, target, rescue, out var eligibilityFailure))
        {
            _rescueNavigation.CancelRoute(uid, target);
            ClearDormantRouteRecovery(
                rescue,
                $"cancelled target={FormatEntityRef(target)}; reason={eligibilityFailure}",
                clearFailureBudget: true);
            Dirty(uid, rescue);
            return false;
        }

        // ManualOverrideTarget is durable provenance, not an executing owner.
        // It must survive while this dormant observer owns the failed route,
        // but must not cancel that observer and immediately recreate the same
        // exhausted intent every update.
        if (TryGetActivePatientTarget(rescue, out var activeTarget, includeManualOverride: false))
        {
            if (activeTarget == target)
            {
                ClearDormantRouteRecovery(
                    rescue,
                    $"superseded by authoritative assignment target={FormatEntityRef(target)}",
                    clearFailureBudget: true);
                Dirty(uid, rescue);
                return false;
            }

            DeferDormantRouteRecovery(uid, rescue, target, $"busy with {FormatEntityRef(activeTarget)}");
            return false;
        }

        if (rescue.PendingPlayerAction != LuaMRescuePlayerActionKind.None ||
            rescue.OnboardCareTarget is { Valid: true })
        {
            DeferDormantRouteRecovery(uid, rescue, target, "coordinator is busy");
            return false;
        }

        var now = _timing.CurTime;
        if (!rescue.DormantRouteProbeInFlight && now < rescue.NextDormantRouteProbeAt)
            return false;

        var actionRange = Math.Max(0.05f, rescue.DormantRouteActionRange);
        var route = _rescueNavigation.ProbeRoute(uid, target, actionRange);
        if (route.State == LuaMRescuePathProbeState.Pending)
        {
            if (!rescue.DormantRouteProbeInFlight)
            {
                rescue.DormantRouteProbeInFlight = true;
                rescue.DormantRouteProbeStartedAt = now;
                rescue.DormantRouteProbeCount++;
            }

            var probeTimeout = TimeSpan.FromSeconds(Math.Max(0.1f, rescue.DormantRouteProbeTimeoutSeconds));
            if (now - rescue.DormantRouteProbeStartedAt >= probeTimeout)
            {
                _rescueNavigation.CancelRoute(uid, target);
                rescue.DormantRouteProbeInFlight = false;
                rescue.DormantRouteProbeStartedAt = TimeSpan.Zero;
                rescue.NextDormantRouteProbeAt = now +
                    TimeSpan.FromSeconds(Math.Max(0.1f, rescue.DormantRouteObservationSeconds));
                rescue.LastDormantRouteStatus =
                    $"probe timed out target={FormatEntityRef(target)}; next={rescue.NextDormantRouteProbeAt.TotalSeconds:0.0}s";
            }
            else
            {
                rescue.NextDormantRouteProbeAt = now + TimeSpan.FromSeconds(0.25);
                rescue.LastDormantRouteStatus =
                    $"probing target={FormatEntityRef(target)}; probe={rescue.DormantRouteProbeCount}";
            }

            Dirty(uid, rescue);
            return false;
        }

        rescue.DormantRouteProbeInFlight = false;
        rescue.DormantRouteProbeStartedAt = TimeSpan.Zero;
        if (route.State != LuaMRescuePathProbeState.Reachable)
        {
            _rescueNavigation.CancelRoute(uid, target);
            rescue.NextDormantRouteProbeAt = now +
                TimeSpan.FromSeconds(Math.Max(0.1f, rescue.DormantRouteObservationSeconds));
            rescue.LastDormantRouteStatus =
                $"dormant target={FormatEntityRef(target)} remains {route.State}; " +
                $"probe={rescue.DormantRouteProbeCount}; next={rescue.NextDormantRouteProbeAt.TotalSeconds:0.0}s";
            Dirty(uid, rescue);
            return false;
        }

        // The route result is authoritative. Cancel fallback movement before the
        // normal patient-activity transition so no old HTN operator survives the
        // generation change. Dormant recovery can begin from Standby, therefore it
        // must use the bounded transition helper instead of attempting the invalid
        // Standby -> ApproachPatient edge directly.
        PrepareForExternalIntentReplacement(uid, target);
        var destination = new EntityCoordinates(target, Vector2.Zero);
        if (!BeginPatientActivity(
                uid,
                rescue,
                LuaMRescueActivity.ApproachPatient,
                target,
                destination))
        {
            _rescueNavigation.CancelRoute(uid, target);
            rescue.NextDormantRouteProbeAt = now +
                TimeSpan.FromSeconds(Math.Max(0.1f, rescue.DormantRouteObservationSeconds));
            rescue.LastDormantRouteStatus =
                $"reachable target={FormatEntityRef(target)} but intent replacement failed: " +
                $"{rescue.ActivityContext.FailureReason}";
            Dirty(uid, rescue);
            return false;
        }

        var resumedGeneration = rescue.ActivityContext.Generation;

        rescue.AssignedTarget = target;
        SetRescueTask(
            uid,
            rescue,
            LuaMRescueTaskStage.FollowingPatient,
            target,
            null,
            $"resumed after dormant route recovery {FormatEntityRef(target)}");
        _activity.RecordProgress(
            uid,
            destination,
            route.Distance,
            route.Distance <= actionRange ? LuaMRescueRouteStatus.Arrived : LuaMRescueRouteStatus.Moving,
            LuaMRescueDoAfterStatus.None,
            out _);
        SetMovementGoal(uid, rescue, htn, destination, actionRange);

        rescue.DormantRouteResumeCount++;
        var resumeCount = rescue.DormantRouteResumeCount;
        var probeCount = rescue.DormantRouteProbeCount;
        ClearDormantRouteRecovery(rescue, "recovered", clearFailureBudget: true);
        rescue.LastDormantRouteStatus =
            $"recovered target={FormatEntityRef(target)}; generation={resumedGeneration}; " +
            $"resume={resumeCount}; probes={probeCount}";
        Dirty(uid, rescue);
        return true;
    }

    private void DeferDormantRouteRecovery(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        EntityUid target,
        string reason)
    {
        if (rescue.DormantRouteProbeInFlight)
            _rescueNavigation.CancelRoute(uid, target);

        rescue.DormantRouteProbeInFlight = false;
        rescue.DormantRouteProbeStartedAt = TimeSpan.Zero;
        rescue.NextDormantRouteProbeAt = _timing.CurTime +
            TimeSpan.FromSeconds(Math.Max(0.1f, rescue.DormantRouteObservationSeconds));
        rescue.LastDormantRouteStatus =
            $"deferred target={FormatEntityRef(target)}; {reason}; next={rescue.NextDormantRouteProbeAt.TotalSeconds:0.0}s";
        Dirty(uid, rescue);
    }

    private static void ClearDormantRouteRecovery(
        LuaMRescueAgentComponent rescue,
        string status,
        bool clearFailureBudget)
    {
        if (rescue.DormantRouteTarget is { Valid: true } target)
        {
            rescue.SkippedTargets.Remove(target);
            if (clearFailureBudget)
                rescue.RouteFailureAttempts.Remove(target);
        }

        rescue.DormantRouteTarget = null;
        rescue.NextDormantRouteProbeAt = TimeSpan.Zero;
        rescue.DormantRouteProbeInFlight = false;
        rescue.DormantRouteProbeStartedAt = TimeSpan.Zero;
        rescue.DormantRouteActionRange = 0f;
        rescue.LastDormantRouteStatus = status;
    }

    private bool BeginPatientActivity(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        LuaMRescueActivity activity,
        EntityUid? target,
        EntityCoordinates? destination = null)
    {
        if (rescue.ActivityContext.Activity == activity &&
            rescue.ActivityContext.Target == target &&
            Nullable.Equals(rescue.ActivityContext.Destination, destination) &&
            rescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Blocked)
        {
            var blockedGeneration = rescue.ActivityContext.Generation;
            if (_activity.TryRecoverBlockedIntent(
                    uid,
                    blockedGeneration,
                    activity,
                    target,
                    destination,
                    out var retryAfter,
                    out _))
            {
                rescue.LastTargetTrackingStatus =
                    $"recovering {activity} after bounded route/action backoff; generation={rescue.ActivityContext.Generation}";
                return true;
            }

            if (retryAfter > TimeSpan.Zero)
            {
                rescue.LastTargetTrackingStatus =
                    $"blocked {activity}; retry in {retryAfter.TotalSeconds:0.0}s; reason={rescue.ActivityContext.FailureReason}";
            }
            return false;
        }

        if (rescue.ActivityContext.Activity == activity &&
            rescue.ActivityContext.Target == target &&
            rescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Failed)
        {
            return false;
        }

        if (rescue.ActivityContext.TerminalStatus == LuaMRescueTerminalStatus.Active &&
            rescue.ActivityContext.Target != target)
        {
            var cancelledGeneration = rescue.ActivityContext.Generation;
            var cancellationStatus =
                $"patient activity target replacement: old={FormatEntityRef(rescue.ActivityContext.Target)}; " +
                $"new={FormatEntityRef(target)}; requested={activity}; " +
                $"generation={cancelledGeneration}";
            if (_activity.Cancel(uid, cancelledGeneration, LuaMRescueFailureReason.Cancelled, out _))
                rescue.LastIntentCancellationStatus = cancellationStatus;
        }

        if (_activity.BeginOrReplaceIntent(
                uid,
                rescue.ActivityRole,
                activity,
                target,
                destination,
                out _))
        {
            return true;
        }

        // A concrete server-side outcome may legitimately skip a cosmetic intermediate
        // state (for example an already-close patient goes straight to treatment).
        // Close the obsolete state before starting the authoritative replacement.
        var obsoleteGeneration = rescue.ActivityContext.Generation;
        var retryCancellationStatus =
            $"patient activity transition retry: from={rescue.ActivityContext.Activity}; requested={activity}; " +
            $"target={FormatEntityRef(target)}; generation={obsoleteGeneration}";
        if (_activity.Cancel(uid, obsoleteGeneration, LuaMRescueFailureReason.Cancelled, out _))
            rescue.LastIntentCancellationStatus = retryCancellationStatus;
        return _activity.BeginOrReplaceIntent(
            uid,
            rescue.ActivityRole,
            activity,
            target,
            destination,
            out _);
    }

    private void SetTaskMovementTarget(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        EntityUid target)
    {
        if (!BeginPatientActivity(
            uid,
            rescue,
            LuaMRescueActivity.Resupply,
            rescue.TaskPatientTarget,
            new EntityCoordinates(target, Vector2.Zero)))
        {
            return;
        }
        SetMovementGoal(
            uid,
            rescue,
            htn,
            new EntityCoordinates(target, Vector2.Zero),
            rescue.PlayerActionRange);
        Dirty(uid, rescue);
    }

    private bool SetFollowShuttle(EntityUid uid, LuaMRescueAgentComponent rescue, HTNComponent htn)
    {
        if (!TryGetShuttleAnchorCoordinates(rescue, out var coordinates))
        {
            HoldMovementForRoute(uid, htn);
            _activity.Block(
                uid,
                LuaMRescueFailureReason.ShuttleUnavailable,
                LuaMRescueActivity.Standby,
                out _);
            rescue.LastShuttleReturnStatus = "ShuttleUnavailable: no shuttle anchor is assigned";
            Dirty(uid, rescue);
            return false;
        }

        var patient = rescue.EvacuatingTarget ?? rescue.TaskPatientTarget;
        var delivering = patient is { Valid: true } patientUid && IsPullingTarget(uid, patientUid);
        if (delivering &&
            patient is { Valid: true } portalPatient &&
            TryReturnPatientThroughGateway(uid, portalPatient, rescue, htn, coordinates.EntityId))
        {
            Dirty(uid, rescue);
            return true;
        }

        if (!delivering &&
            rescue.LifeSupportEmergencyActive &&
            TryReturnAgentThroughGateway(uid, rescue, htn, coordinates.EntityId))
        {
            Dirty(uid, rescue);
            return true;
        }

        if (!EnsureConfirmedShuttleTraversal(
                uid,
                rescue,
                htn,
                coordinates.EntityId,
                delivering ? patient : null))
        {
            return false;
        }

        var activity = delivering ? LuaMRescueActivity.DeliverPatient : LuaMRescueActivity.ReturnToShuttle;
        var activityTarget = delivering ? patient : rescue.AssignedShuttle;
        if (!BeginPatientActivity(
            uid,
            rescue,
            activity,
            activityTarget,
            coordinates))
        {
            return false;
        }

        var destination = coordinates.EntityId;
        var confirmedDockedCrossGrid = Transform(uid).GridUid != Transform(destination).GridUid;
        var route = _rescueNavigation.ProbeRoute(
            uid,
            destination,
            rescue.EvacuationArrivalRange,
            confirmedDockedCrossGrid);
        if (route.State == LuaMRescuePathProbeState.Pending)
        {
            HoldMovementForRoute(uid, htn);
            rescue.LastShuttleReturnStatus =
                $"planning route to shuttle anchor {FormatEntityRef(destination)}";
            _activity.RecordProgress(
                uid,
                coordinates,
                route.Distance,
                LuaMRescueRouteStatus.Planning,
                LuaMRescueDoAfterStatus.None,
                out _);
            Dirty(uid, rescue);
            return true;
        }

        if (route.State != LuaMRescuePathProbeState.Reachable)
        {
            var failure = route.State switch
            {
                LuaMRescuePathProbeState.AccessDenied => LuaMRescueFailureReason.AccessDenied,
                LuaMRescuePathProbeState.NoLineOfSight => LuaMRescueFailureReason.NoLineOfSight,
                LuaMRescuePathProbeState.DifferentGrid => LuaMRescueFailureReason.ShuttleUnavailable,
                _ => LuaMRescueFailureReason.NoPath,
            };
            HoldMovementForRoute(uid, htn);
            rescue.LastShuttleReturnStatus =
                $"{failure}: route to shuttle anchor {FormatEntityRef(destination)} is {route.State}";
            _activity.Block(uid, failure, LuaMRescueActivity.Standby, out _);
            Dirty(uid, rescue);
            return false;
        }

        _activity.RecordProgress(
            uid,
            coordinates,
            route.Distance,
            route.Distance <= rescue.EvacuationArrivalRange
                ? LuaMRescueRouteStatus.Arrived
                : LuaMRescueRouteStatus.Moving,
            LuaMRescueDoAfterStatus.None,
            out _);
        SetMovementGoal(uid, rescue, htn, coordinates, rescue.EvacuationArrivalRange);
        Dirty(uid, rescue);
        return true;
    }

    private bool TryReturnPatientThroughGateway(
        EntityUid uid,
        EntityUid patient,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        EntityUid destination)
    {
        if (Deleted(patient) || Deleted(destination) || !IsPullingTarget(uid, patient))
            return false;

        var agentMap = Transform(uid).MapID;
        var patientMap = Transform(patient).MapID;
        var destinationMap = Transform(destination).MapID;
        if (agentMap != patientMap || agentMap == destinationMap ||
            agentMap == MapId.Nullspace || destinationMap == MapId.Nullspace)
        {
            return false;
        }

        EntityUid? bestGateway = null;
        LuaMRescuePathProbeSnapshot bestRoute = default;
        var query = AllEntityQuery<GatewayComponent, PortalComponent, TransformComponent>();
        while (query.MoveNext(out var gateway, out var gatewayComp, out var portalComp, out var gatewayXform))
        {
            if (!TryResolveSingleReciprocalGatewayEndpoint(
                    gateway,
                    gatewayComp,
                    portalComp,
                    gatewayXform,
                    agentMap,
                    destinationMap,
                    out _))
            {
                continue;
            }

            var route = _rescueNavigation.ProbeRoute(uid, gateway, 1f);
            if (route.State == LuaMRescuePathProbeState.Pending)
            {
                HoldMovementForRoute(uid, htn);
                rescue.LastShuttleReturnStatus =
                    $"planning patient return through gateway {FormatEntityRef(gateway)}";
                _activity.RecordProgress(
                    uid,
                    new EntityCoordinates(gateway, Vector2.Zero),
                    route.Distance,
                    LuaMRescueRouteStatus.Planning,
                    LuaMRescueDoAfterStatus.None,
                    out _);
                return true;
            }

            if (route.State != LuaMRescuePathProbeState.Reachable ||
                bestGateway != null && route.Distance >= bestRoute.Distance)
            {
                continue;
            }

            bestGateway = gateway;
            bestRoute = route;
        }

        if (bestGateway is not { } portal)
            return false;

        if (!BeginPatientActivity(
                uid,
                rescue,
                LuaMRescueActivity.DeliverPatient,
                patient,
                new EntityCoordinates(portal, Vector2.Zero)))
        {
            return true;
        }

        var agentAtPortal = IsWithinRange(uid, portal, 1.05f);
        var patientAtPortal = IsWithinRange(patient, portal, 2.25f);
        if (agentAtPortal && patientAtPortal)
        {
            // The generic collision path deliberately breaks pulling. Move the patient first and
            // the rescuer second through the same validated link, then reacquire custody on the
            // destination side during the next rescue update.
            StopPullingTarget(uid, patient);
            if (_portal.TryTeleportThroughLinkedPortal(portal, patient, ignoreTimeout: true) &&
                _portal.TryTeleportThroughLinkedPortal(portal, uid, ignoreTimeout: true))
            {
                rescue.LastShuttleReturnStatus =
                    $"patient and rescuer crossed gateway {FormatEntityRef(portal)}; reacquiring custody";
                _activity.RecordProgress(
                    uid,
                    new EntityCoordinates(destination, Vector2.Zero),
                    null,
                    LuaMRescueRouteStatus.Moving,
                    LuaMRescueDoAfterStatus.None,
                    out _);
                return true;
            }

            rescue.LastShuttleReturnStatus =
                $"gateway transfer failed at {FormatEntityRef(portal)}; reassessing route";
            return true;
        }

        rescue.LastShuttleReturnStatus =
            $"bringing {FormatEntityRef(patient)} to return gateway {FormatEntityRef(portal)}";
        _activity.RecordProgress(
            uid,
            new EntityCoordinates(portal, Vector2.Zero),
            bestRoute.Distance,
            LuaMRescueRouteStatus.Moving,
            LuaMRescueDoAfterStatus.None,
            out _);
        SetMovementGoal(uid, rescue, htn, new EntityCoordinates(portal, Vector2.Zero), 1f);
        return true;
    }

    /// <summary>
    /// A lone rescuer has no pulled patient to drive the expedition return path.
    /// Move it to the nearest safe reciprocal gateway and explicitly cross once
    /// the fixture is within unobstructed interaction range.
    /// </summary>
    private bool TryReturnAgentThroughGateway(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        EntityUid destination)
    {
        if (Deleted(destination) ||
            rescue.AssignedShuttle is not { Valid: true } shuttle ||
            Deleted(shuttle))
        {
            return false;
        }

        var agentMap = Transform(uid).MapID;
        var destinationMap = Transform(destination).MapID;
        if (agentMap == destinationMap ||
            agentMap == MapId.Nullspace ||
            destinationMap == MapId.Nullspace)
        {
            return false;
        }

        EntityUid? bestGateway = null;
        LuaMRescuePathProbeSnapshot bestRoute = default;
        EntityUid? pendingGateway = null;
        LuaMRescuePathProbeSnapshot pendingRoute = default;
        var query = AllEntityQuery<GatewayComponent, PortalComponent, TransformComponent>();
        while (query.MoveNext(out var gateway, out var gatewayComp, out var portalComp, out var gatewayXform))
        {
            if (!TryResolveSingleReciprocalGatewayEndpoint(
                    gateway,
                    gatewayComp,
                    portalComp,
                    gatewayXform,
                    agentMap,
                    destinationMap,
                    out _))
            {
                continue;
            }

            if (_interaction.InRangeUnobstructed(
                    uid,
                    gateway,
                    SharedInteractionSystem.InteractionRange))
            {
                bestGateway = gateway;
                bestRoute = default;
                break;
            }

            // Match the controlled-transfer boundary used above. A 0.1 m
            // pathfinding probe targets the gateway's blocked centre and can
            // oscillate between Pending and NoPath even though the entrance is
            // safely reachable.
            var route = _rescueNavigation.ProbeRoute(
                uid,
                gateway,
                SharedInteractionSystem.InteractionRange);
            if (route.State == LuaMRescuePathProbeState.Pending)
            {
                if (pendingGateway == null || route.Distance < pendingRoute.Distance)
                {
                    pendingGateway = gateway;
                    pendingRoute = route;
                }
                continue;
            }

            if (route.State != LuaMRescuePathProbeState.Reachable ||
                bestGateway != null && route.Distance >= bestRoute.Distance)
            {
                continue;
            }

            bestGateway = gateway;
            bestRoute = route;
        }

        if (bestGateway == null && pendingGateway is { } planningGateway)
        {
            BeginPatientActivity(
                uid,
                rescue,
                LuaMRescueActivity.ReturnToShuttle,
                shuttle,
                new EntityCoordinates(planningGateway, Vector2.Zero));
            HoldMovementForRoute(uid, htn);
            rescue.LastShuttleReturnStatus =
                $"planning life-support return through gateway {FormatEntityRef(planningGateway)}";
            _activity.RecordProgress(
                uid,
                new EntityCoordinates(planningGateway, Vector2.Zero),
                pendingRoute.Distance,
                LuaMRescueRouteStatus.Planning,
                LuaMRescueDoAfterStatus.None,
                out _);
            return true;
        }

        if (bestGateway is not { } portal)
            return false;

        if (!BeginPatientActivity(
                uid,
                rescue,
                LuaMRescueActivity.ReturnToShuttle,
                shuttle,
                new EntityCoordinates(portal, Vector2.Zero)))
        {
            return true;
        }

        if (_interaction.InRangeUnobstructed(
                uid,
                portal,
                SharedInteractionSystem.InteractionRange))
        {
            // Crossing by collision alone can leave the old HTN operator
            // steering into the arrival portal until its timeout expires,
            // causing a delayed bounce back to the expedition. Stop that
            // executor, use the stock linked-portal transfer contract, then
            // immediately publish the real shuttle-anchor goal on the far side.
            HoldMovementForRoute(uid, htn);
            if (_portal.TryTeleportThroughLinkedPortal(portal, uid, ignoreTimeout: true))
            {
                rescue.LastShuttleReturnStatus =
                    $"rescuer crossed life-support return gateway {FormatEntityRef(portal)}";
                _activity.RecordProgress(
                    uid,
                    new EntityCoordinates(destination, Vector2.Zero),
                    null,
                    LuaMRescueRouteStatus.Moving,
                    LuaMRescueDoAfterStatus.None,
                    out _);
                SetFollowShuttle(uid, rescue, htn);
                return true;
            }

            rescue.LastShuttleReturnStatus =
                $"life-support gateway transfer failed at {FormatEntityRef(portal)}; reassessing route";
            return true;
        }

        rescue.LastShuttleReturnStatus =
            $"returning through gateway {FormatEntityRef(portal)} during life-support emergency";
        _activity.RecordProgress(
            uid,
            new EntityCoordinates(portal, Vector2.Zero),
            bestRoute.Distance,
            LuaMRescueRouteStatus.Moving,
            LuaMRescueDoAfterStatus.None,
            out _);
        SetMovementGoal(uid, rescue, htn, new EntityCoordinates(portal, Vector2.Zero), 0.1f);
        return true;
    }

    private bool TryResolveSingleReciprocalGatewayEndpoint(
        EntityUid source,
        GatewayComponent sourceGateway,
        PortalComponent sourcePortal,
        TransformComponent sourceTransform,
        MapId sourceMap,
        MapId destinationMap,
        out EntityUid destination)
    {
        destination = default;
        if (!sourceGateway.Enabled ||
            !sourcePortal.CanTeleportToOtherMaps ||
            sourceTransform.MapID != sourceMap ||
            !TryComp<LinkedEntityComponent>(source, out var sourceLinks) ||
            sourceLinks.LinkedEntities.Count != 1)
        {
            return false;
        }

        var candidate = sourceLinks.LinkedEntities.First();
        if (!candidate.Valid ||
            Deleted(candidate) ||
            !TryComp<GatewayComponent>(candidate, out var destinationGateway) ||
            !destinationGateway.Enabled ||
            !TryComp<PortalComponent>(candidate, out var destinationPortal) ||
            !destinationPortal.CanTeleportToOtherMaps ||
            !TryComp<LinkedEntityComponent>(candidate, out var destinationLinks) ||
            destinationLinks.LinkedEntities.Count != 1 ||
            destinationLinks.LinkedEntities.First() != source ||
            Transform(candidate).MapID != destinationMap)
        {
            return false;
        }

        destination = candidate;
        return true;
    }

    private bool SetFollowDeliveryStrap(EntityUid uid, LuaMRescueAgentComponent rescue, HTNComponent htn, EntityUid patientStrap)
    {
        if (Deleted(patientStrap))
            return SetFollowShuttle(uid, rescue, htn);

        var patient = rescue.EvacuatingTarget ?? rescue.TaskPatientTarget;
        if (!EnsureConfirmedShuttleTraversal(uid, rescue, htn, patientStrap, patient))
            return false;

        if (!BeginPatientActivity(
            uid,
            rescue,
            LuaMRescueActivity.DeliverPatient,
            rescue.EvacuatingTarget ?? rescue.TaskPatientTarget,
            new EntityCoordinates(patientStrap, Vector2.Zero)))
        {
            return false;
        }

        var movementRange = Math.Min(rescue.PlayerActionRange, rescue.AutoReleaseRange);
        float? remainingDistance = null;
        var patientReadyForBuckle = false;
        if (patient is { Valid: true } patientUid &&
            !Deleted(patientUid) &&
            TryComp<BuckleComponent>(patientUid, out var buckle) &&
            Transform(patientUid).Coordinates.TryDistance(
                EntityManager,
                Transform(patientStrap).Coordinates,
                out var patientDistance))
        {
            remainingDistance = patientDistance;
            patientReadyForBuckle = patientDistance <= buckle.Range &&
                                    IsWithinRange(patientUid, patientStrap, buckle.Range);
            if (!patientReadyForBuckle)
            {
                // The pulled patient trails behind the medic. Keep the medic
                // advancing past the normal interaction stop until the actual
                // patient-to-bed distance satisfies the buckle contract.
                movementRange = Math.Min(
                    movementRange,
                    Math.Max(0.1f, buckle.Range * 0.25f));
            }
        }

        _activity.RecordProgress(
            uid,
            new EntityCoordinates(patientStrap, Vector2.Zero),
            remainingDistance,
            patientReadyForBuckle
                ? LuaMRescueRouteStatus.Arrived
                : LuaMRescueRouteStatus.Moving,
            LuaMRescueDoAfterStatus.None,
            out _);
        SetMovementGoal(
            uid,
            rescue,
            htn,
            new EntityCoordinates(patientStrap, Vector2.Zero),
            movementRange);
        Dirty(uid, rescue);
        return true;
    }

    private bool EnsureConfirmedShuttleTraversal(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        EntityUid destination,
        EntityUid? patient)
    {
        if (Deleted(destination) ||
            rescue.AssignedShuttle is not { Valid: true } shuttle ||
            Deleted(shuttle))
        {
            HoldMovementForRoute(uid, htn);
            _activity.Block(
                uid,
                LuaMRescueFailureReason.ShuttleUnavailable,
                LuaMRescueActivity.Standby,
                out _);
            rescue.LastAutoEvacuationStatus = "ShuttleUnavailable: destination or shuttle is missing";
            return false;
        }

        var agentGrid = Transform(uid).GridUid;
        var destinationGrid = destination == shuttle
            ? shuttle
            : Transform(destination).GridUid;
        if (agentGrid == destinationGrid)
            return true;

        // A destination on an unrelated graph is never a valid shuttle crossing.
        if (destinationGrid != shuttle || agentGrid is not { Valid: true } outsideGrid)
        {
            HoldMovementForRoute(uid, htn);
            _activity.Block(uid, LuaMRescueFailureReason.NoPath, LuaMRescueActivity.Standby, out _);
            rescue.LastAutoEvacuationStatus =
                $"NoPath: shuttle destination {FormatEntityRef(destination)} is on an unrelated grid";
            return false;
        }

        var routeTarget = patient is { Valid: true } patientUid &&
                          !Deleted(patientUid) &&
                          !IsOnAssignedShuttle(patientUid, rescue)
            ? patientUid
            : uid;
        TryRouteShuttleToTarget(uid, rescue, routeTarget);

        var lifecycle = EnsureComp<LuaMRescueShuttleLifecycleComponent>(shuttle);
        var physicallyDocked = lifecycle.State == LuaMRescueShuttleRouteState.Docked &&
                               lifecycle.SafeExitConfirmed &&
                               IsShuttleDockedToGrid(shuttle, outsideGrid);
        if (physicallyDocked)
            return true;

        HoldMovementForRoute(uid, htn);
        var traversalActivity = rescue.LifeSupportEmergencyActive && patient == null
            ? LuaMRescueActivity.ReturnToShuttle
            : LuaMRescueActivity.PlanningRoute;
        BeginPatientActivity(
            uid,
            rescue,
            traversalActivity,
            patient ?? shuttle,
            new EntityCoordinates(destination, Vector2.Zero));

        var terminal = (lifecycle.State is LuaMRescueShuttleRouteState.Failed
                or LuaMRescueShuttleRouteState.TimedOut) &&
            lifecycle.RetryCount >= lifecycle.EffectiveMaxRetries;
        if (terminal)
        {
            rescue.LastAutoEvacuationStatus =
                $"ShuttleRouteFailed: cannot traverse to {FormatEntityRef(destination)}; {lifecycle.LastStatus}";
            _activity.Block(
                uid,
                LuaMRescueFailureReason.ShuttleRouteFailed,
                LuaMRescueActivity.Standby,
                out _);
        }
        else
        {
            rescue.LastAutoEvacuationStatus =
                $"waiting for confirmed Docked before cross-grid traversal; shuttle={lifecycle.State}; target={FormatEntityRef(routeTarget)}";
            _activity.RecordProgress(
                uid,
                new EntityCoordinates(destination, Vector2.Zero),
                remainingDistance: null,
                LuaMRescueRouteStatus.Planning,
                LuaMRescueDoAfterStatus.None,
                out _);
        }

        Dirty(uid, rescue);
        return false;
    }

    private bool IsShuttleDockedToGrid(EntityUid shuttle, EntityUid grid)
    {
        foreach (var dock in _docking.GetDocks(shuttle))
        {
            var dockUid = dock.Owner;
            var docking = dock.Comp;
            if (docking.DockedWith is not { Valid: true } otherDock ||
                Deleted(otherDock) ||
                !TryComp<DockingComponent>(otherDock, out var reciprocal) ||
                reciprocal.DockedWith != dockUid)
            {
                continue;
            }

            var otherTransform = Transform(otherDock);
            if (otherTransform.GridUid == grid || otherTransform.ParentUid == grid)
                return true;
        }

        return false;
    }

    private void SetMovementGoal(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        EntityCoordinates coordinates,
        float actionRange)
    {
        actionRange = Math.Max(0.1f, actionRange);
        if (coordinates.EntityId is { Valid: true } actionTarget &&
            !Deleted(actionTarget) &&
            Transform(uid).Coordinates.TryDistance(EntityManager, coordinates, out var directDistance) &&
            directDistance <= actionRange &&
            !_interaction.InRangeUnobstructed(uid, actionTarget, actionRange))
        {
            // Close behind a door/wall is not arrived. Keep FollowCompound moving
            // toward the approach path returned by the authoritative route query.
            actionRange = Math.Min(actionRange, 0.25f);
        }
        var closeRange = Math.Min(rescue.FollowCloseRange, actionRange);
        var followRange = Math.Min(rescue.FollowRange, actionRange);

        if (htn.Blackboard.TryGetValue<EntityCoordinates>(
                NPCBlackboard.FollowTarget,
                out var previous,
                EntityManager) &&
            !previous.Equals(coordinates))
        {
            CancelCurrentHtnMovement(uid, htn, cancelRouteProbe: false);
            if (previous.EntityId.Valid && previous.EntityId != coordinates.EntityId)
                _rescueNavigation.CancelRoute(uid, previous.EntityId);
        }

        _npc.SetBlackboard(uid, NPCBlackboard.FollowTarget, coordinates, htn);
        _npc.SetBlackboard(uid, "FollowCloseRange", closeRange, htn);
        _npc.SetBlackboard(uid, "FollowRange", followRange, htn);
        _npc.WakeNPC(uid, htn);
    }

    private void CancelCurrentHtnMovement(EntityUid uid, HTNComponent htn, bool cancelRouteProbe = true)
    {
        htn.PlanningToken?.Cancel();
        htn.PlanningToken = null;
        htn.PlanningJob = null;

        if (htn.Plan != null)
        {
            _htnSystem.ShutdownTask(htn.Plan.CurrentOperator, htn.Blackboard, HTNOperatorStatus.Failed);
            _htnSystem.ShutdownPlan(htn);
        }

        if (cancelRouteProbe)
            _rescueNavigation.CancelRoute(uid);
        _htnSystem.Replan(htn);
    }

    private void StandbyAtAssignedShuttle(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        bool allowAutoReturn = true)
    {
        ClearRescueTask(uid, rescue, "standby");
        rescue.EvacuatingTarget = null;
        rescue.AssignedTarget = null;
        rescue.AssignedPatientStrap = null;
        rescue.ArrivalReportedTarget = null;
        rescue.TriageReportedTarget = null;
        rescue.LastTriageDecisionKey = "none";
        rescue.DeathSignalTarget = null;
        rescue.DeathSignalDispatchReported = false;
        ClearRouteBlockHold(rescue);

        if (CanUseAssignedShuttle(rescue))
        {
            var onboard = IsOnAssignedShuttle(uid, rescue);
            // Never command the shuttle home while its medic is still outside.
            // Boarding through a confirmed dock is part of the current intent.
            if (allowAutoReturn && onboard)
                TryRouteShuttleHome(uid, rescue);

            if (!IsAtAssignedShuttleAnchor(uid, rescue))
            {
                // Returning is one durable intent. Do not insert a transient
                // Standby every refresh: that would reset generation/deadline
                // and make an unreachable anchor retry forever.
                SetFollowShuttle(uid, rescue, htn);
                Dirty(uid, rescue);
                return;
            }
        }

        BeginPatientActivity(uid, rescue, LuaMRescueActivity.Standby, null);
        ClearFollowTarget(uid, rescue, htn);
        Dirty(uid, rescue);
    }

    private bool TryGetShuttleAnchorCoordinates(LuaMRescueAgentComponent rescue, out EntityCoordinates coordinates)
    {
        if (rescue.AssignedShuttleAnchor is { Valid: true } anchor &&
            !Deleted(anchor))
        {
            coordinates = Transform(anchor).Coordinates;
            return true;
        }

        if (rescue.AssignedShuttle is { Valid: true } shuttle &&
            !Deleted(shuttle))
        {
            coordinates = Transform(shuttle).Coordinates;
            return true;
        }

        coordinates = default;
        return false;
    }

    private void ClearFollowTarget(EntityUid uid, LuaMRescueAgentComponent rescue, HTNComponent htn)
    {
        if (rescue.AssignedTarget == null &&
            !htn.Blackboard.ContainsKey(NPCBlackboard.FollowTarget))
        {
            return;
        }

        // Finishing a standby/return movement must still tear down its HTN
        // operator, but it must not cancel an independent low-frequency route
        // observation for a dormant patient. Cancel only the route that belonged
        // to the cleared FollowTarget and leave the dormant generation's query
        // alive.
        var dormantTarget = rescue.DormantRouteTarget;
        var preserveDormantProbe = dormantTarget is { Valid: true };
        EntityUid? clearedRouteTarget = null;
        if (preserveDormantProbe &&
            htn.Blackboard.TryGetValue<EntityCoordinates>(
                NPCBlackboard.FollowTarget,
                out var followTarget,
                EntityManager) &&
            followTarget.EntityId is { Valid: true } routeTarget &&
            routeTarget != dormantTarget)
        {
            clearedRouteTarget = routeTarget;
        }

        CancelCurrentHtnMovement(uid, htn, cancelRouteProbe: !preserveDormantProbe);
        if (clearedRouteTarget is { Valid: true } previousTarget)
            _rescueNavigation.CancelRoute(uid, previousTarget);

        rescue.AssignedTarget = null;
        htn.Blackboard.Remove<EntityCoordinates>(NPCBlackboard.FollowTarget);
        Dirty(uid, rescue);
    }
}

[AdminCommand(AdminFlags.Server)]
public sealed class LuaMRescueAgentCommand : IConsoleCommand
{
    private const string ControlFlag = "--control";
    private const string NearTargetFlag = "--near-target";

    [Dependency] private readonly IEntityManager _entities = default!;
    [Dependency] private readonly IPlayerManager _players = default!;

    public string Command => "luam_rescue_agent";
    public string Description => "Spawns a player-like LuaM rescue agent humanoid.";
    public string Help => $"Usage: {Command} [targetEntityId|playerName] [{ControlFlag}] [{NearTargetFlag}]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var control = args.Any(arg => arg.Equals(ControlFlag, StringComparison.OrdinalIgnoreCase));
        var nearTarget = args.Any(arg => arg.Equals(NearTargetFlag, StringComparison.OrdinalIgnoreCase));
        var targetArg = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal));

        if (control && shell.Player == null)
        {
            shell.WriteError($"{ControlFlag} requires a player shell.");
            return;
        }

        EntityUid? target = null;
        if (!string.IsNullOrWhiteSpace(targetArg) &&
            !TryResolveTarget(targetArg, out target, out var error))
        {
            shell.WriteError(error);
            return;
        }

        target ??= shell.Player?.AttachedEntity;
        var anchor = nearTarget ? target : shell.Player?.AttachedEntity ?? target;
        if (anchor is not { Valid: true } anchorUid)
        {
            shell.WriteError("No spawn anchor. Attach to a mob or pass a valid target with --near-target.");
            return;
        }

        var system = _entities.System<LuaMRescueAgentSystem>();
        if (!system.TrySpawnAgent(anchorUid, target, shell.Player, control, out var agent, out var status))
        {
            shell.WriteError(status);
            return;
        }

        var netAgent = _entities.GetNetEntity(agent);
        var targetText = target is { Valid: true } targetUid
            ? _entities.GetNetEntity(targetUid).ToString()
            : "none";

        shell.WriteLine($"{status} followTarget={targetText}; controlled={control}.");
        if (!control)
            shell.WriteLine($"Use `controlmob {netAgent.Id}` if you want to take direct control like a player.");
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
        {
            var names = _players.Sessions.Select(session => session.Name);
            return CompletionResult.FromHintOptions(names.Concat([ControlFlag, NearTargetFlag]), "target player/entity or flag");
        }

        return CompletionResult.FromHintOptions([ControlFlag, NearTargetFlag], "optional flag");
    }

    private bool TryResolveTarget(string raw, out EntityUid? target, out string error)
    {
        target = null;
        error = string.Empty;

        if (NetEntity.TryParse(raw, out var netEntity) &&
            _entities.TryGetEntity(netEntity, out var parsedTarget) &&
            parsedTarget is { Valid: true })
        {
            target = parsedTarget.Value;
            return true;
        }

        if (int.TryParse(raw, out var integerId) &&
            _entities.TryGetEntity(new NetEntity(integerId), out parsedTarget) &&
            parsedTarget is { Valid: true })
        {
            target = parsedTarget.Value;
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
}

[AdminCommand(AdminFlags.Server)]
public sealed class LuaMRescueOrderCommand : IConsoleCommand
{
    private const string AgentKey = "agent";
    private const string TargetKey = "target";
    private const string AllAgentsValue = "all";
    private const string NearestAgentValue = "nearest";
    private const string ClearValue = "clear";

    [Dependency] private readonly IEntityManager _entities = default!;
    [Dependency] private readonly IPlayerManager _players = default!;

    public string Command => "luam_rescue_order";
    public string Description => "Orders active LuaM rescue agents to follow or rescue a target.";
    public string Help =>
        $"Usage: {Command} [agent=<entity|{NearestAgentValue}|{AllAgentsValue}>] [target=<entity|player>|{ClearValue}]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var clear = args.Any(arg => arg.Equals(ClearValue, StringComparison.OrdinalIgnoreCase) ||
                                    arg.Equals($"{TargetKey}={ClearValue}", StringComparison.OrdinalIgnoreCase));
        var agentArg = GetValue(args, AgentKey);
        var targetArg = GetValue(args, TargetKey) ??
                        args.FirstOrDefault(arg => !arg.Contains('=') &&
                                                   !arg.StartsWith("--", StringComparison.Ordinal) &&
                                                   !arg.Equals(ClearValue, StringComparison.OrdinalIgnoreCase));

        if (!TryResolveAgents(shell, agentArg, out var agents, out var error))
        {
            shell.WriteError(error);
            return;
        }

        EntityUid? target = null;
        if (!clear)
        {
            if (string.IsNullOrWhiteSpace(targetArg))
            {
                shell.WriteError($"Pass {TargetKey}=<entity|player> or {ClearValue}.");
                return;
            }

            if (!TryResolveTarget(targetArg, out target, out error))
            {
                shell.WriteError(error);
                return;
            }
        }

        var system = _entities.System<LuaMRescueAgentSystem>();
        foreach (var agent in agents)
        {
            if (system.TryOrderAgent(agent, target, out var status))
                shell.WriteLine(status);
            else
                shell.WriteError(status);
        }
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length <= 1)
        {
            var names = _players.Sessions.Select(session => $"{TargetKey}={session.Name}");
            return CompletionResult.FromHintOptions(
                names.Concat([
                    $"{AgentKey}={NearestAgentValue}",
                    $"{AgentKey}={AllAgentsValue}",
                    $"{TargetKey}=",
                    ClearValue,
                ]),
                "rescue order option");
        }

        return CompletionResult.FromHintOptions([
            $"{AgentKey}={NearestAgentValue}",
            $"{AgentKey}={AllAgentsValue}",
            $"{TargetKey}=",
            ClearValue,
        ], "rescue order option");
    }

    private bool TryResolveAgents(IConsoleShell shell, string? raw, out List<EntityUid> agents, out string error)
    {
        agents = [];
        error = string.Empty;

        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (raw.Equals(AllAgentsValue, StringComparison.OrdinalIgnoreCase))
            {
                agents.AddRange(GetActiveAgents());
                if (agents.Count > 0)
                    return true;

                error = "No LuaM rescue agents are active.";
                return false;
            }

            if (raw.Equals(NearestAgentValue, StringComparison.OrdinalIgnoreCase))
                return TryResolveNearestAgent(shell, out agents, out error);

            if (TryResolveEntity(raw, out var agent) &&
                _entities.HasComponent<LuaMRescueAgentComponent>(agent))
            {
                agents.Add(agent);
                return true;
            }

            error = $"LuaM rescue agent not found: {raw}.";
            return false;
        }

        agents.AddRange(GetActiveAgents());
        if (agents.Count == 1)
            return true;

        if (agents.Count > 1)
            return TryResolveNearestAgent(shell, out agents, out error);

        error = "No LuaM rescue agents are active.";
        return false;
    }

    private bool TryResolveNearestAgent(IConsoleShell shell, out List<EntityUid> agents, out string error)
    {
        agents = [];
        error = string.Empty;

        if (shell.Player?.AttachedEntity is not { Valid: true } attached)
        {
            error = $"Could not infer nearest agent. Pass {AgentKey}=<entity|{AllAgentsValue}>.";
            return false;
        }

        var attachedCoordinates = _entities.GetComponent<TransformComponent>(attached).Coordinates;
        var bestDistance = float.PositiveInfinity;
        EntityUid? bestAgent = null;

        foreach (var agent in GetActiveAgents())
        {
            var coordinates = _entities.GetComponent<TransformComponent>(agent).Coordinates;
            if (!attachedCoordinates.TryDistance(_entities, coordinates, out var distance) ||
                distance >= bestDistance)
            {
                continue;
            }

            bestAgent = agent;
            bestDistance = distance;
        }

        if (bestAgent is { Valid: true } nearest)
        {
            agents.Add(nearest);
            return true;
        }

        error = "No reachable LuaM rescue agent found.";
        return false;
    }

    private List<EntityUid> GetActiveAgents()
    {
        var agents = new List<EntityUid>();
        var query = _entities.EntityQueryEnumerator<LuaMRescueAgentComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            agents.Add(uid);
        }

        return agents;
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

[AdminCommand(AdminFlags.Server)]
public sealed class LuaMRescueActionCommand : IConsoleCommand
{
    private const string ActionKey = "action";
    private const string AgentKey = "agent";
    private const string TargetKey = "target";
    private const string SlotKey = "slot";
    private const string ItemKey = "item";
    private const string AllAgentsValue = "all";
    private const string NearestAgentValue = "nearest";

    private static readonly string[] ActionNames =
    [
        "interact",
        "alt",
        "use",
        "treat",
        "vend",
        "pickup",
        "drop",
        "pull",
        "stop-pull",
        "buckle",
        "unbuckle",
        "equip-slot",
        "unequip-slot",
        "store-slot",
        "take-storage",
        "take-target-storage",
        "clear",
    ];

    private static readonly string[] SlotNames =
    [
        "belt",
        "back",
        "suitstorage",
        "outerClothing",
        "pocket1",
        "pocket2",
        "jumpsuit",
        "id",
        "mask",
        "gloves",
        "head",
        "eyes",
        "ears",
        "neck",
    ];

    private static readonly string[] ItemHints =
    [
        "medkit",
        "medipen",
        "hypospray",
        "ointment",
        "gauze",
        "brutepack",
        "Brutepack",
        "bruise",
        "burn",
        "EmergencyMedipen",
        "PillCanisterTricordrazine",
        "EpinephrineChemistryBottle",
        "analyzer",
        "health-analyzer",
        "tool",
    ];

    [Dependency] private readonly IEntityManager _entities = default!;
    [Dependency] private readonly IPlayerManager _players = default!;

    public string Command => "luam_rescue_action";
    public string Description => "Orders active LuaM rescue agents to perform player-like interactions.";
    public string Help =>
        $"Usage: {Command} {ActionKey}=<interact|alt|use|treat|vend|pickup|drop|pull|stop-pull|buckle|unbuckle|equip-slot|unequip-slot|store-slot|take-storage|take-target-storage|clear> " +
        $"[agent=<entity|{NearestAgentValue}|{AllAgentsValue}>] [target=<entity|player>] [slot=<inventorySlot>] [item=<name|prototype|entity>]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var positional = args
            .Where(arg => !arg.Contains('=') &&
                          !arg.StartsWith("--", StringComparison.Ordinal))
            .ToArray();
        var actionArg = GetValue(args, ActionKey) ?? positional.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(actionArg) ||
            !TryParseAction(actionArg, out var action))
        {
            shell.WriteError($"Pass {ActionKey}=<interact|alt|use|treat|vend|pickup|drop|pull|stop-pull|buckle|unbuckle|equip-slot|unequip-slot|store-slot|take-storage|take-target-storage|clear>.");
            return;
        }

        var actionWasFirstPositional = positional.Length > 0 &&
                                       positional[0].Equals(actionArg, StringComparison.OrdinalIgnoreCase);
        var remainingPositionals = positional
            .Skip(actionWasFirstPositional ? 1 : 0)
            .ToArray();
        var explicitTargetArg = GetValue(args, TargetKey);
        var targetArg = explicitTargetArg ?? remainingPositionals.FirstOrDefault();
        var slotArg = GetValue(args, SlotKey);
        var itemArg = GetValue(args, ItemKey);
        if (ActionRequiresSlot(action) &&
            explicitTargetArg == null)
        {
            if (string.IsNullOrWhiteSpace(slotArg))
            {
                slotArg = remainingPositionals.FirstOrDefault();
                itemArg ??= remainingPositionals.Skip(1).FirstOrDefault();
            }
            else
            {
                itemArg ??= remainingPositionals.FirstOrDefault();
            }

            targetArg = null;
        }
        else if (action == LuaMRescuePlayerActionKind.Treat)
        {
            itemArg ??= remainingPositionals
                .Skip(string.IsNullOrWhiteSpace(explicitTargetArg) ? 1 : 0)
                .FirstOrDefault();
        }
        else if (action == LuaMRescuePlayerActionKind.Vend)
        {
            itemArg ??= remainingPositionals
                .Skip(string.IsNullOrWhiteSpace(explicitTargetArg) ? 1 : 0)
                .FirstOrDefault();
        }
        else if (action == LuaMRescuePlayerActionKind.TakeTargetStorage)
        {
            itemArg ??= remainingPositionals
                .Skip(string.IsNullOrWhiteSpace(explicitTargetArg) ? 1 : 0)
                .FirstOrDefault();
        }

        var agentArg = GetValue(args, AgentKey);

        if (!TryResolveAgents(shell, agentArg, out var agents, out var error))
        {
            shell.WriteError(error);
            return;
        }

        EntityUid? target = null;
        if (!string.IsNullOrWhiteSpace(targetArg))
        {
            if (!TryResolveTarget(targetArg, out target, out error))
            {
                shell.WriteError(error);
                return;
            }
        }
        else if (ActionRequiresTarget(action))
        {
            shell.WriteError($"{FormatAction(action)} requires {TargetKey}=<entity|player>.");
            return;
        }

        if (ActionRequiresSlot(action) &&
            string.IsNullOrWhiteSpace(slotArg))
        {
            shell.WriteError($"{FormatAction(action)} requires {SlotKey}=<inventorySlot>.");
            return;
        }

        var system = _entities.System<LuaMRescueAgentSystem>();
        foreach (var agent in agents)
        {
            if (system.TryOrderPlayerAction(agent, action, target, slotArg, itemArg, out var status))
                shell.WriteLine(status);
            else
                shell.WriteError(status);
        }
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length <= 1)
        {
            var names = _players.Sessions.Select(session => $"{TargetKey}={session.Name}");
            return CompletionResult.FromHintOptions(
                ActionNames.Select(action => $"{ActionKey}={action}")
                    .Concat(names)
                    .Concat(SlotNames.Select(slot => $"{SlotKey}={slot}"))
                    .Concat(ItemHints.Select(item => $"{ItemKey}={item}"))
                    .Concat([
                        $"{AgentKey}={NearestAgentValue}",
                        $"{AgentKey}={AllAgentsValue}",
                        $"{TargetKey}=",
                        $"{SlotKey}=",
                        $"{ItemKey}=",
                    ]),
                "rescue player action option");
        }

        return CompletionResult.FromHintOptions(
            ActionNames.Select(action => $"{ActionKey}={action}")
                .Concat(SlotNames.Select(slot => $"{SlotKey}={slot}"))
                .Concat(ItemHints.Select(item => $"{ItemKey}={item}"))
                .Concat([
                    $"{AgentKey}={NearestAgentValue}",
                    $"{AgentKey}={AllAgentsValue}",
                    $"{TargetKey}=",
                    $"{SlotKey}=",
                    $"{ItemKey}=",
                ]),
            "rescue player action option");
    }

    private bool TryResolveAgents(IConsoleShell shell, string? raw, out List<EntityUid> agents, out string error)
    {
        agents = [];
        error = string.Empty;

        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (raw.Equals(AllAgentsValue, StringComparison.OrdinalIgnoreCase))
            {
                agents.AddRange(GetActiveAgents());
                if (agents.Count > 0)
                    return true;

                error = "No LuaM rescue agents are active.";
                return false;
            }

            if (raw.Equals(NearestAgentValue, StringComparison.OrdinalIgnoreCase))
                return TryResolveNearestAgent(shell, out agents, out error);

            if (TryResolveEntity(raw, out var agent) &&
                _entities.HasComponent<LuaMRescueAgentComponent>(agent))
            {
                agents.Add(agent);
                return true;
            }

            error = $"LuaM rescue agent not found: {raw}.";
            return false;
        }

        agents.AddRange(GetActiveAgents());
        if (agents.Count == 1)
            return true;

        if (agents.Count > 1)
            return TryResolveNearestAgent(shell, out agents, out error);

        error = "No LuaM rescue agents are active.";
        return false;
    }

    private bool TryResolveNearestAgent(IConsoleShell shell, out List<EntityUid> agents, out string error)
    {
        agents = [];
        error = string.Empty;

        if (shell.Player?.AttachedEntity is not { Valid: true } attached)
        {
            error = $"Could not infer nearest agent. Pass {AgentKey}=<entity|{AllAgentsValue}>.";
            return false;
        }

        var attachedCoordinates = _entities.GetComponent<TransformComponent>(attached).Coordinates;
        var bestDistance = float.PositiveInfinity;
        EntityUid? bestAgent = null;

        foreach (var agent in GetActiveAgents())
        {
            var coordinates = _entities.GetComponent<TransformComponent>(agent).Coordinates;
            if (!attachedCoordinates.TryDistance(_entities, coordinates, out var distance) ||
                distance >= bestDistance)
            {
                continue;
            }

            bestAgent = agent;
            bestDistance = distance;
        }

        if (bestAgent is { Valid: true } nearest)
        {
            agents.Add(nearest);
            return true;
        }

        error = "No reachable LuaM rescue agent found.";
        return false;
    }

    private List<EntityUid> GetActiveAgents()
    {
        var agents = new List<EntityUid>();
        var query = _entities.EntityQueryEnumerator<LuaMRescueAgentComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            agents.Add(uid);
        }

        return agents;
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

    private static bool TryParseAction(string raw, out LuaMRescuePlayerActionKind action)
    {
        action = raw.ToLowerInvariant() switch
        {
            "interact" or "click" or "hand" => LuaMRescuePlayerActionKind.Interact,
            "alt" or "alt-interact" or "altinteract" => LuaMRescuePlayerActionKind.AltInteract,
            "use" or "use-held" or "usehand" => LuaMRescuePlayerActionKind.Use,
            "treat" or "heal" or "medical" or "medicate" or "analyze" or "scan-health" => LuaMRescuePlayerActionKind.Treat,
            "vend" or "buy" or "purchase" or "dispense" or "vending" => LuaMRescuePlayerActionKind.Vend,
            "pickup" or "pick-up" or "take" or "grab" => LuaMRescuePlayerActionKind.Pickup,
            "drop" => LuaMRescuePlayerActionKind.Drop,
            "pull" or "drag" => LuaMRescuePlayerActionKind.Pull,
            "stop-pull" or "stoppull" or "unpull" or "release" => LuaMRescuePlayerActionKind.StopPull,
            "buckle" or "strap" or "seat" => LuaMRescuePlayerActionKind.Buckle,
            "unbuckle" or "unstrap" or "unseat" => LuaMRescuePlayerActionKind.Unbuckle,
            "equip-slot" or "equipslot" or "equip" or "wear" => LuaMRescuePlayerActionKind.EquipSlot,
            "unequip-slot" or "unequipslot" or "unequip" or "take-slot" or "draw-slot" => LuaMRescuePlayerActionKind.UnequipSlot,
            "store-slot" or "storeslot" or "store" or "stow" => LuaMRescuePlayerActionKind.StoreSlot,
            "take-storage" or "takestorage" or "take-from-storage" or "draw-storage" or "take-stored" => LuaMRescuePlayerActionKind.TakeStorage,
            "take-target-storage" or "taketargetstorage" or "take-from-target-storage" or "take-container" or "loot-storage" or "loot-container" => LuaMRescuePlayerActionKind.TakeTargetStorage,
            "clear" or "cancel" or "standby" => LuaMRescuePlayerActionKind.None,
            _ => LuaMRescuePlayerActionKind.None,
        };

        return raw.Equals("clear", StringComparison.OrdinalIgnoreCase) ||
               raw.Equals("cancel", StringComparison.OrdinalIgnoreCase) ||
               raw.Equals("standby", StringComparison.OrdinalIgnoreCase) ||
               action != LuaMRescuePlayerActionKind.None;
    }

    private static bool ActionRequiresTarget(LuaMRescuePlayerActionKind action)
    {
        return action is LuaMRescuePlayerActionKind.Interact
            or LuaMRescuePlayerActionKind.AltInteract
            or LuaMRescuePlayerActionKind.Treat
            or LuaMRescuePlayerActionKind.Vend
            or LuaMRescuePlayerActionKind.Pickup
            or LuaMRescuePlayerActionKind.Pull
            or LuaMRescuePlayerActionKind.Buckle
            or LuaMRescuePlayerActionKind.Unbuckle
            or LuaMRescuePlayerActionKind.TakeTargetStorage;
    }

    private static bool ActionRequiresSlot(LuaMRescuePlayerActionKind action)
    {
        return action is LuaMRescuePlayerActionKind.EquipSlot
            or LuaMRescuePlayerActionKind.UnequipSlot
            or LuaMRescuePlayerActionKind.StoreSlot
            or LuaMRescuePlayerActionKind.TakeStorage;
    }

    private static string FormatAction(LuaMRescuePlayerActionKind action)
    {
        return action switch
        {
            LuaMRescuePlayerActionKind.Interact => "interact",
            LuaMRescuePlayerActionKind.AltInteract => "alt-interact",
            LuaMRescuePlayerActionKind.Use => "use",
            LuaMRescuePlayerActionKind.Treat => "treat",
            LuaMRescuePlayerActionKind.Vend => "vend",
            LuaMRescuePlayerActionKind.Pickup => "pickup",
            LuaMRescuePlayerActionKind.Drop => "drop",
            LuaMRescuePlayerActionKind.Pull => "pull",
            LuaMRescuePlayerActionKind.StopPull => "stop-pull",
            LuaMRescuePlayerActionKind.Buckle => "buckle",
            LuaMRescuePlayerActionKind.Unbuckle => "unbuckle",
            LuaMRescuePlayerActionKind.EquipSlot => "equip-slot",
            LuaMRescuePlayerActionKind.UnequipSlot => "unequip-slot",
            LuaMRescuePlayerActionKind.StoreSlot => "store-slot",
            LuaMRescuePlayerActionKind.TakeStorage => "take-storage",
            LuaMRescuePlayerActionKind.TakeTargetStorage => "take-target-storage",
            _ => "clear",
        };
    }

    private static string? GetValue(string[] args, string key)
    {
        var prefix = $"{key}=";
        var match = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return match?.Substring(prefix.Length);
    }
}

[AdminCommand(AdminFlags.Server)]
public sealed class LuaMRescueStatusCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;

    public string Command => "luam_rescue_status";
    public string Description => "Prints LuaM rescue role and autopilot mission telemetry.";
    public string Help => $"Usage: {Command}";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var system = _entities.System<LuaMRescueAgentSystem>();
        var lines = system.BuildRescueStatusLines();
        lines.AddRange(_entities.System<LuaMRescueShuttleSystem>().BuildAutopilotStatusLines());
        if (lines.Count == 0)
        {
            shell.WriteLine("No LuaM rescue roles are active.");
            return;
        }

        shell.WriteLine($"LuaM rescue telemetry: {lines.Count}");
        foreach (var line in lines)
        {
            shell.WriteLine(line);
        }
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return CompletionResult.Empty;
    }
}
