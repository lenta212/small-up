using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server.Administration;
using Content.Server.Bed.Components;
using Content.Server.Buckle.Systems;
using Content.Server.Chat.Systems;
using Content.Server.Hands.Systems;
using Content.Server.Interaction;
using Content.Server.Medical;
using Content.Server.Medical.Components;
using Content.Server.Mind;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Server.Radio.EntitySystems;
using Content.Server.Shuttles.Components;
using Content.Server.VendingMachines;
using Content.Shared.ActionBlocker;
using Content.Shared.Buckle.Components;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.Chat;
using Content.Shared.Damage;
using Content.Shared.Hands.Components;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Item.ItemToggle;
using Content.Shared.Medical;
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
using Content.Shared.VendingMachines;
using Robust.Server.Player;
using Robust.Shared.Containers;
using Robust.Shared.Console;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._LuaM.Rescue;

public sealed class LuaMRescueAgentSystem : EntitySystem
{
    private const string RescueAgentPrototype = "LuaMRescueAgent";
    private static readonly Vector2 SpawnOffset = new(1.25f, 0f);
    private static readonly ProtoId<RadioChannelPrototype> MedicalRadioChannel = "Medical";
    private static readonly string[] TreatmentStorageSlotPriority =
    [
        "belt",
        "pocket1",
        "pocket2",
        "back",
        "suitstorage",
        "outerClothing",
        "jumpsuit",
    ];
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
    [Dependency] private readonly MindSystem _mind = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly MedibotSystem _medibot = default!;
    [Dependency] private readonly PullingSystem _pulling = default!;
    [Dependency] private readonly BuckleSystem _buckle = default!;
    [Dependency] private readonly HandsSystem _hands = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly InteractionSystem _interaction = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly RadioSystem _radio = default!;
    [Dependency] private readonly DefibrillatorSystem _defibrillator = default!;
    [Dependency] private readonly SharedStorageSystem _storage = default!;
    [Dependency] private readonly ItemToggleSystem _itemToggle = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly ActionBlockerSystem _actionBlocker = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly VendingMachineSystem _vending = default!;
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly PathfindingSystem _pathfinding = default!;
    [Dependency] private readonly LuaMRescueTeamSystem _rescueTeam = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<MobStateComponent, TargetDefibrillatedEvent>(OnTargetDefibrillated);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<LuaMRescueAgentComponent, HTNComponent>();
        while (query.MoveNext(out var uid, out var rescue, out var htn))
        {
            if (HasComp<ActorComponent>(uid))
                continue;

            if (rescue.PendingPlayerAction != LuaMRescuePlayerActionKind.None)
            {
                UpdatePendingPlayerAction(uid, rescue, htn, frameTime);
                continue;
            }

            if (!rescue.AutoAcquireTargets)
                continue;

            rescue.TargetRefreshAccumulator += frameTime;
            if (rescue.TargetRefreshAccumulator < rescue.TargetRefreshInterval)
                continue;

            rescue.TargetRefreshAccumulator = 0f;
            UpdateAssignedTarget(uid, rescue, htn);
        }
    }

    public EntityUid SpawnAgent(EntityUid anchor, EntityUid? followTarget, ICommonSession? controller, bool control)
    {
        var spawnCoordinates = Transform(anchor).Coordinates.Offset(SpawnOffset);
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

        if (target is not { Valid: true } targetUid)
        {
            if (rescue.EvacuatingTarget is { Valid: true } previousTarget &&
                !Deleted(previousTarget))
            {
                StopPullingTarget(agent, previousTarget);
            }

            ClearPendingPlayerAction(rescue);
            ClearRescueTask(agent, rescue, "order cleared");
            ResetTargetProgress(rescue);
            StandbyAtAssignedShuttle(agent, rescue, htn);
            status = $"{FormatEntityRef(agent)} cleared current rescue order and is returning to standby.";
            return true;
        }

        if (Deleted(targetUid))
        {
            status = $"Target {targetUid} is deleted.";
            return false;
        }

        if (rescue.EvacuatingTarget is { Valid: true } previous &&
            previous != targetUid &&
            !Deleted(previous))
        {
            StopPullingTarget(agent, previous);
        }

        rescue.SkippedTargets.Remove(targetUid);
        ClearPendingPlayerAction(rescue);
        SetRescueTask(
            agent,
            rescue,
            LuaMRescueTaskStage.FollowingPatient,
            targetUid,
            null,
            $"ordered to rescue {FormatEntityRef(targetUid)}");
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

        if (rescue.EvacuatingTarget is { Valid: true } previous &&
            !Deleted(previous))
        {
            StopPullingTarget(agent, previous);
        }

        rescue.EvacuatingTarget = null;
        rescue.AssignedPatientStrap = null;
        ResetTargetProgress(rescue);

        if (target is not { Valid: true } actionTarget)
        {
            if (TryExecutePlayerAction(agent, rescue, action, null, normalizedSlot, normalizedItemSelector, out var actionStatus))
            {
                ClearPendingPlayerAction(rescue, actionStatus);
                StandbyAtAssignedShuttle(agent, rescue, htn);
                status = $"{FormatEntityRef(agent)} {actionStatus}.";
                return true;
            }

            rescue.LastPlayerActionStatus = actionStatus;
            StandbyAtAssignedShuttle(agent, rescue, htn);
            status = $"{FormatEntityRef(agent)} failed to {FormatPlayerAction(action)}: {actionStatus}.";
            return false;
        }

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
        var route = rescue.ShuttleReturnRouted
            ? "home"
            : rescue.ShuttleRoutedTarget is { Valid: true } routedTarget && !Deleted(routedTarget)
                ? $"target:{FormatEntityRef(routedTarget)}"
                : "none";

        return $"{FormatEntityRef(uid)} phase={GetRescuePhase(uid, rescue)}; " +
               $"target={FormatEntityRef(target)}; shuttle={FormatEntityRef(rescue.AssignedShuttle)}; " +
               $"bed={FormatEntityRef(rescue.AssignedPatientStrap)}; route={route}; " +
               $"taskStage={FormatRescueTaskStage(rescue.TaskStage)}; " +
               $"taskPatient={FormatEntityRef(rescue.TaskPatientTarget)}; taskSupply={FormatEntityRef(rescue.TaskSupplyTarget)}; " +
               $"taskLast={rescue.LastTaskStatus}; " +
               $"skipped={rescue.SkippedTargets.Count}; skippedSupply={rescue.SkippedSupplyTargets.Count}; " +
               $"skippedDelivery={rescue.SkippedDeliveryTargets.Count}; " +
               $"autoAnalyze={rescue.LastAutoAnalyzeStatus}; " +
               $"autoTreat={rescue.LastAutoTreatmentStatus}; autoDefib={rescue.LastAutoDefibStatus}; " +
               $"autoEvac={rescue.LastAutoEvacuationStatus}; " +
               $"autoComms={rescue.LastAutoCommsKey}; " +
               $"autoSupply={rescue.LastAutoSupplyStatus}; " +
               $"{FormatPlayerActionStatus(rescue)}; {FormatProgress(rescue)}";
    }

    private void OnTargetDefibrillated(EntityUid target, MobStateComponent mobState, ref TargetDefibrillatedEvent args)
    {
        if (!TryComp<LuaMRescueAgentComponent>(args.User, out var rescue))
            return;

        var targetName = Name(target);
        if (mobState.CurrentState != MobState.Dead)
        {
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

        _chat.TrySendInGameICMessage(uid, message, InGameICChatType.Speak, hideChat: false, hideLog: true);

        if (radio)
            _radio.SendRadioMessage(uid, message, MedicalRadioChannel, uid);

        Dirty(uid, rescue);
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
            (Deleted(patient) || IsTargetTemporarilySkipped(patient, rescue)))
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
            IsTargetTemporarilySkipped(patient, rescue))
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

                if (rescue.AssignedTarget != targetUid)
                    SetFollowTarget(uid, rescue, htn, targetUid);

                return;
            }
        }

        if (action == LuaMRescuePlayerActionKind.Vend)
        {
            UpdatePendingVendingAction(uid, rescue, htn, target!.Value);
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

                var handled = _interaction.InteractUsing(uid, item, targetUid, Transform(targetUid).Coordinates);
                status = handled
                    ? $"treated {FormatEntityRef(targetUid)} using {FormatEntityRef(item)}"
                    : $"treatment of {FormatEntityRef(targetUid)} using {FormatEntityRef(item)} was not handled";
                return handled;
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

            foreach (var slot in TreatmentStorageSlotPriority)
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

        foreach (var slot in TreatmentStorageSlotPriority)
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

        if (!TrySelectVendingProduct(vendingUid, vending, itemSelector, includeDiagnosticItems, out var product, out status))
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
            var score = GetMedicalVendingProductScore(entry.ID, includeDiagnosticItems);
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
            SetFollowTarget(uid, rescue, htn, candidateUid);
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

    private bool TryFindHealthAnalyzerItem(
        EntityUid uid,
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

        foreach (var candidateSlot in TreatmentStorageSlotPriority)
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

        foreach (var hand in hands.Hands.Values)
        {
            if (hand.HeldEntity is not { Valid: true } held ||
                !HasComp<DefibrillatorComponent>(held))
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

        foreach (var candidateSlot in TreatmentStorageSlotPriority)
        {
            if (TryTakeDefibrillatorFromSlot(uid, candidateSlot, emptyHand, hands, out item, out _))
                return true;
        }

        status = "no defibrillator found in hands or storage";
        return false;
    }

    private bool TryTakeDefibrillatorFromSlot(
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

        if (!TrySelectDefibrillatorItem(storageUid, storage, out var storedItem, out status))
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
        EntityUid storageUid,
        StorageComponent storage,
        out EntityUid item,
        out string status)
    {
        item = default;
        status = string.Empty;

        foreach (var contained in storage.Container.ContainedEntities)
        {
            if (!HasComp<DefibrillatorComponent>(contained))
                continue;

            item = contained;
            return true;
        }

        status = $"no defibrillator in {FormatEntityRef(storageUid)}";
        return false;
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

        foreach (var hand in hands.Hands.Values)
        {
            if (hand.HeldEntity is not { Valid: true } held)
                continue;

            if (GetTreatmentItemScore(held, target, itemSelector, includeDiagnosticItems) <= 0f)
                continue;

            item = held;
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

        foreach (var candidateSlot in TreatmentStorageSlotPriority)
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

        if (!TrySelectTreatmentItem(storageUid, storage, target, itemSelector, includeDiagnosticItems, out var storedItem, out status))
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
        status = $"took {FormatEntityRef(storedItem)} from {FormatEntityRef(storageUid)}";
        return true;
    }

    private bool TrySelectTreatmentItem(
        EntityUid storageUid,
        StorageComponent storage,
        EntityUid target,
        string? itemSelector,
        bool includeDiagnosticItems,
        out EntityUid item,
        out string status)
    {
        item = default;
        status = string.Empty;

        var bestScore = 0f;
        foreach (var contained in storage.Container.ContainedEntities)
        {
            var score = GetTreatmentItemScore(contained, target, itemSelector, includeDiagnosticItems);
            if (score <= bestScore)
                continue;

            item = contained;
            bestScore = score;
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

        if (HasComp<HyposprayComponent>(item) || HasComp<InjectorComponent>(item))
            score = Math.Max(score, 110f);

        if (includeDiagnosticItems && HasComp<HealthAnalyzerComponent>(item))
            score = Math.Max(score, 90f);

        if (score > 0f)
            return score;

        if (string.IsNullOrWhiteSpace(itemSelector))
            return 0f;

        var lowered = itemSelector.Trim().ToLowerInvariant();
        var prototype = MetaData(item).EntityPrototype?.ID?.ToLowerInvariant() ?? string.Empty;
        var name = Name(item).ToLowerInvariant();

        if (prototype.Contains(lowered) || name.Contains(lowered))
            return 50f;

        return 0f;
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

        if (healing.BloodlossModifier != 0 ||
            healing.ModifyBloodLevel > 0)
        {
            score = 105f;
            return true;
        }

        return false;
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
            !IsTargetTemporarilySkipped(patient, rescue))
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
        PruneSkippedTargets(rescue);
        PruneSkippedSupplyTargets(rescue);
        PruneSkippedDeliveryTargets(rescue);
        PruneAnalyzedTargets(rescue);
        PruneRescueTaskMemory(uid, rescue);

        if (UpdateEvacuation(uid, rescue, htn))
            return;

        if (rescue.AssignedTarget is { Valid: true } assigned &&
            !IsTargetTemporarilySkipped(assigned, rescue) &&
            TryTreatOrEvacuateTarget(uid, rescue, htn, assigned))
        {
            return;
        }

        if (!TryComp<MedibotComponent>(uid, out var medibot))
        {
            ClearFollowTarget(uid, rescue, htn);
            return;
        }

        if (TryFindPriorityRescueOverride(uid, rescue, medibot, out var priorityTarget))
        {
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

        if (TryResumeRememberedPatientTask(uid, rescue, htn, medibot))
            return;

        if (rescue.AssignedTarget is { Valid: true } current &&
            !IsTargetTemporarilySkipped(current, rescue) &&
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

        if (TryFindRescueTarget(uid, rescue.SearchRange, rescue, medibot, out var target))
        {
            if (TryTreatOrEvacuateTarget(uid, rescue, htn, target))
                return;

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

        if (TryFindEvacuationTarget(uid, rescue.SearchRange, rescue, out var evacuationTarget) &&
            (TryAutoDefibTarget(uid, rescue, htn, evacuationTarget) ||
             TryTreatOrEvacuateTarget(uid, rescue, htn, evacuationTarget)))
        {
            return;
        }

        if (TryAutoDefibDeadPatientOnShuttle(uid, rescue, htn))
            return;

        if (TryReleaseStabilizedPatientOnShuttle(uid, rescue, htn))
            return;

        StandbyAtAssignedShuttle(uid, rescue, htn);
    }

    private bool TryTreatOrEvacuateTarget(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        EntityUid target)
    {
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
        out EntityUid target)
    {
        target = default;
        var bestScore = float.MinValue;

        foreach (var candidate in _lookup.GetEntitiesInRange(uid, searchRange))
        {
            if (IsTargetTemporarilySkipped(candidate, rescue))
                continue;

            if (!IsRescueCandidate(uid, candidate, medibot, requireRange: true, searchRange, out var score))
                continue;

            if (score <= bestScore)
                continue;

            target = candidate;
            bestScore = score;
        }

        return target != default;
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
            IsTargetTemporarilySkipped(current, rescue) ||
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

        var bestScore = float.MinValue;
        foreach (var candidate in _lookup.GetEntitiesInRange(uid, rescue.SearchRange))
        {
            if (candidate == current ||
                IsTargetTemporarilySkipped(candidate, rescue))
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

            if (candidatePriority <= currentPriority ||
                candidateDistance >= currentDistance)
            {
                continue;
            }

            var score = candidatePriority * 1000f - candidateDistance;
            if (score <= bestScore)
                continue;

            target = candidate;
            bestScore = score;
        }

        return target != default;
    }

    private bool TryGetActivePatientTarget(LuaMRescueAgentComponent rescue, out EntityUid target)
    {
        if (rescue.EvacuatingTarget is { Valid: true } evacuating)
        {
            target = evacuating;
            return true;
        }

        if (rescue.AssignedTarget is { Valid: true } assigned)
        {
            target = assigned;
            return true;
        }

        if (rescue.TaskPatientTarget is { Valid: true } remembered)
        {
            target = remembered;
            return true;
        }

        target = default;
        return false;
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

        priority = GetRescueTargetAcuity(mobState, damage.TotalDamage.Float(), rescue);
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

            if (IsTargetTemporarilySkipped(candidate, rescue))
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

    private bool UpdateEvacuation(EntityUid uid, LuaMRescueAgentComponent rescue, HTNComponent htn)
    {
        if (!CanUseAssignedShuttle(rescue))
        {
            rescue.EvacuatingTarget = null;
            return false;
        }

        if (rescue.EvacuatingTarget is not { Valid: true } target ||
            Deleted(target))
        {
            ResetTargetProgress(rescue);
            rescue.EvacuatingTarget = null;
            return false;
        }

        if (IsTargetTemporarilySkipped(target, rescue))
        {
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
            !IsDeadPatientRecoveryTarget(target, rescue, mobState))
        {
            StopPullingTarget(uid, target);
            rescue.EvacuatingTarget = null;
            ResetTargetProgress(rescue);
            StandbyAtAssignedShuttle(uid, rescue, htn);
            Dirty(uid, rescue);
            return false;
        }

        if (IsEvacuationComplete(target, rescue))
        {
            CompleteEvacuation(uid, rescue, htn, target);
            return false;
        }

        if (UpdateTargetProgress(uid, target, rescue))
        {
            if (TryHandleStalledDeliveryTarget(uid, target, rescue, htn))
                return true;

            TemporarilySkipTarget(uid, rescue, htn, target);
            return false;
        }

        var unsafeSceneEvacuation = IsThreatenedEvacuationTarget(uid, target, rescue);

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

        if (TryFindPatientDeliveryStrap(rescue, out var patientStrap, out _))
        {
            rescue.AssignedPatientStrap = patientStrap;
            if (TryBucklePatientToStrap(uid, target, patientStrap, rescue))
            {
                CompleteEvacuation(uid, rescue, htn, target);
                return false;
            }

            if (ShouldSkipFailedPatientDeliveryTarget(target, patientStrap, rescue))
            {
                TemporarilySkipDeliveryTarget(rescue, patientStrap, "buckle failed");
                ResetTargetProgress(rescue);
                if (TryFindPatientDeliveryStrap(rescue, out var replacementStrap, out _))
                    rescue.AssignedPatientStrap = replacementStrap;
            }
        }
        else
        {
            rescue.AssignedPatientStrap = null;
        }

        if (!IsPullingTarget(uid, target))
        {
            if (IsWithinRange(uid, target, rescue.EvacuationStartRange))
                _pulling.TryStartPull(uid, target);

            if (!IsPullingTarget(uid, target))
            {
                SetRescueTask(
                    uid,
                    rescue,
                    LuaMRescueTaskStage.EvacuatingPatient,
                    target,
                    null,
                    $"approaching evacuation patient {FormatEntityRef(target)}");
                SetFollowTarget(uid, rescue, htn, target);
                return true;
            }
        }

        if (rescue.AssignedPatientStrap is { Valid: true } deliveryStrap &&
            !Deleted(deliveryStrap))
        {
            if (TryBucklePatientToStrap(uid, target, deliveryStrap, rescue))
            {
                CompleteEvacuation(uid, rescue, htn, target);
                return false;
            }

            if (ShouldSkipFailedPatientDeliveryTarget(target, deliveryStrap, rescue))
            {
                TemporarilySkipDeliveryTarget(rescue, deliveryStrap, "buckle failed");
                ResetTargetProgress(rescue);
                if (TryFindPatientDeliveryStrap(rescue, out var replacementStrap, out _))
                    rescue.AssignedPatientStrap = replacementStrap;
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
            return SetFollowDeliveryStrap(uid, rescue, htn, assignedStrap);
        }

        SetRescueTask(
            uid,
            rescue,
            LuaMRescueTaskStage.DeliveringPatient,
            target,
            rescue.AssignedShuttleAnchor ?? rescue.AssignedShuttle,
            $"returning {FormatEntityRef(target)} to shuttle");
        return SetFollowShuttle(uid, rescue, htn);
    }

    private bool TryStartOrContinueEvacuation(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        EntityUid target)
    {
        var unsafeSceneEvacuation = IsThreatenedEvacuationTarget(uid, target, rescue);
        if (!NeedsEvacuation(uid, target, rescue))
            return false;

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
            $"evacuating {FormatEntityRef(target)}");
        if (unsafeSceneEvacuation &&
            TryComp<LuaMRescueTeamComponent>(uid, out var team))
        {
            rescue.LastAutoEvacuationStatus = $"unsafe-scene evacuation of {FormatEntityRef(target)}; {team.LastSceneStatus}; {team.LastMemoryDigest}";
        }

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
            SetFollowTarget(uid, rescue, htn, target);
            return true;
        }

        _pulling.TryStartPull(uid, target);

        if (!IsPullingTarget(uid, target))
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

        SetRescueTask(
            uid,
            rescue,
            LuaMRescueTaskStage.DeliveringPatient,
            target,
            rescue.AssignedShuttleAnchor ?? rescue.AssignedShuttle,
            $"returning {FormatEntityRef(target)} to shuttle");
        return SetFollowShuttle(uid, rescue, htn);
    }

    private bool TryAutoUnbucklePatientForEvacuation(
        EntityUid uid,
        EntityUid target,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn)
    {
        if (!rescue.AutoUnbucklePatientsForEvacuation ||
            !TryComp<BuckleComponent>(target, out var buckle) ||
            buckle.BuckledTo is not { Valid: true } strap ||
            Deleted(strap) ||
            IsAssignedShuttlePatientStrap(strap, rescue))
        {
            return false;
        }

        if (!IsWithinRange(uid, strap, buckle.Range))
        {
            rescue.LastAutoEvacuationStatus = $"moving to unbuckle {FormatEntityRef(target)} from {FormatEntityRef(strap)}";
            SetFollowTarget(uid, rescue, htn, target);
            return true;
        }

        var unbuckled = _buckle.TryUnbuckle(target, uid, buckle, popup: false);
        rescue.LastAutoEvacuationStatus = unbuckled
            ? $"unbuckled {FormatEntityRef(target)} from {FormatEntityRef(strap)} for evacuation"
            : $"could not unbuckle {FormatEntityRef(target)} from {FormatEntityRef(strap)} for evacuation";

        if (!unbuckled)
            SetFollowTarget(uid, rescue, htn, target);
        else
            Dirty(uid, rescue);

        return true;
    }

    private bool TryAutoAnalyzeTarget(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        EntityUid target)
    {
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

        var handled = _interaction.InteractUsing(uid, analyzer, target, Transform(target).Coordinates);
        rescue.LastAutoAnalyzeStatus = handled
            ? $"analyzed {FormatEntityRef(target)} using {FormatEntityRef(analyzer)}"
            : $"analysis of {FormatEntityRef(target)} using {FormatEntityRef(analyzer)} was not handled";

        if (handled)
        {
            rescue.AnalyzedTargets[target] = _timing.CurTime + TimeSpan.FromSeconds(rescue.AutoAnalyzeCooldown);
            rescue.NextAutoAnalyzeAttempt = _timing.CurTime + TimeSpan.FromSeconds(0.1f);
            SetFollowTarget(uid, rescue, htn, target);
        }

        Dirty(uid, rescue);
        return handled;
    }

    private bool TryAutoTreatTarget(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn,
        EntityUid target)
    {
        if (!rescue.AutoTreatWithCarriedItems ||
            Deleted(target) ||
            !TryComp<MobStateComponent>(target, out var mobState) ||
            mobState.CurrentState == MobState.Dead ||
            !TryComp<DamageableComponent>(target, out var damageable) ||
            damageable.TotalDamage.Float() < rescue.AutoTreatMinDamage ||
            !IsWithinRange(uid, target, rescue.PlayerActionRange))
        {
            return false;
        }

        SetRescueTask(
            uid,
            rescue,
            LuaMRescueTaskStage.TreatingPatient,
            target,
            null,
            $"treating {FormatEntityRef(target)}");

        if (TryAutoAnalyzeTarget(uid, rescue, htn, target))
            return true;

        if (_timing.CurTime < rescue.NextAutoTreatmentAttempt)
            return false;

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

            Dirty(uid, rescue);
            return false;
        }

        var handled = _interaction.InteractUsing(uid, item, target, Transform(target).Coordinates);
        rescue.LastAutoTreatmentStatus = handled
            ? $"treated {FormatEntityRef(target)} using {FormatEntityRef(item)}"
            : $"treatment of {FormatEntityRef(target)} using {FormatEntityRef(item)} was not handled";

        Dirty(uid, rescue);

        if (!handled)
            return false;

        SetFollowTarget(uid, rescue, htn, target);
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
            mobState.CurrentState != MobState.Dead)
        {
            return false;
        }

        SetRescueTask(
            uid,
            rescue,
            LuaMRescueTaskStage.TreatingPatient,
            target,
            null,
            $"defibrillating {FormatEntityRef(target)}");

        if (!IsWithinRange(uid, target, rescue.PlayerActionRange))
        {
            rescue.LastAutoDefibStatus = $"moving to defibrillate {FormatEntityRef(target)}";
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

        if (!TryFindDefibrillatorItem(uid, out var defib, out var status))
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

            Dirty(uid, rescue);
            return false;
        }

        if (!_itemToggle.IsActivated(defib) &&
            !_itemToggle.TryActivate(defib, uid))
        {
            rescue.LastAutoDefibStatus = $"could not activate defibrillator {FormatEntityRef(defib)}";
            Dirty(uid, rescue);
            return false;
        }

        if (!_defibrillator.TryStartZap(defib, target, uid))
        {
            rescue.LastAutoDefibStatus = $"could not start defibrillation of {FormatEntityRef(target)} with {FormatEntityRef(defib)}";
            Dirty(uid, rescue);
            return false;
        }

        rescue.LastAutoDefibStatus = $"started defibrillation of {FormatEntityRef(target)} with {FormatEntityRef(defib)}";
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
        SetFollowTarget(uid, rescue, htn, supplyUid);

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
        SetFollowTarget(uid, rescue, htn, storageUid);

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
                _container.IsEntityOrParentInContainer(candidate) ||
                !TryComp<StorageComponent>(candidate, out var storage) ||
                !TryCanUseTargetStorage(uid, candidate, storage, out _) ||
                !TrySelectTreatmentItem(candidate, storage, target, itemSelector: null, includeDiagnosticItems: false, out var storedItem, out _) ||
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

            storageUid = candidate;
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

        if (!TryFindMedicalVendingSupply(uid, rescue, rescue.AutoResupplyRange, out var vendingUid, out var productId, out status))
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
        SetFollowTarget(uid, rescue, htn, vendingUid);

        status = $"resupplying {productId} from {FormatEntityRef(vendingUid)}";
        return true;
    }

    private bool TryFindMedicalVendingSupply(
        EntityUid uid,
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
                !TryComp<VendingMachineComponent>(candidate, out var vending) ||
                vending.Broken ||
                vending.Ejecting ||
                !TrySelectVendingProduct(candidate, vending, null, includeDiagnosticItems: false, out var product, out _))
            {
                continue;
            }

            if (!TryGetDistance(uid, candidate, out var distance))
                continue;

            if (!TryGetNavigationSelectionPenalty(uid, candidate, rescue.PlayerActionRange, out var navPenalty))
                continue;

            var score = GetMedicalVendingProductScore(product.ID, includeDiagnosticItems: false) * 100f - distance - navPenalty;
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

        if (candidate == rescuer ||
            Deleted(candidate) ||
            !TryComp<MobStateComponent>(candidate, out var mobState) ||
            !TryComp<DamageableComponent>(candidate, out var damage) ||
            !HasComp<InjectableSolutionComponent>(candidate))
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

        score = damage.TotalDamage.Float() - distance - navPenalty;
        if (mobState.CurrentState == MobState.Critical)
            score += 1000f;

        return true;
    }

    private bool NeedsEvacuation(EntityUid uid, EntityUid target, LuaMRescueAgentComponent rescue)
    {
        if (!rescue.EvacuateTargetsToShuttle ||
            !CanUseAssignedShuttle(rescue) ||
            IsTargetTemporarilySkipped(target, rescue) ||
            IsEvacuationComplete(target, rescue) ||
            !TryComp<MobStateComponent>(target, out var mobState) ||
            !HasComp<PullableComponent>(target))
        {
            return false;
        }

        if (mobState.CurrentState == MobState.Dead)
            return IsDeadPatientRecoveryTarget(target, rescue, mobState);

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

    private bool IsDeadPatientRecoveryTarget(EntityUid target, LuaMRescueAgentComponent rescue, MobStateComponent mobState)
    {
        return rescue.RecoverDeadPatientsToShuttle &&
               mobState.CurrentState == MobState.Dead &&
               (rescue.EvacuatingTarget == target ||
                rescue.AssignedTarget == target ||
                rescue.TaskPatientTarget == target ||
                HasComp<ActorComponent>(target));
    }

    private bool IsEvacuationCandidate(
        EntityUid rescuer,
        EntityUid candidate,
        LuaMRescueAgentComponent rescue,
        float searchRange,
        out float score)
    {
        score = 0f;

        if (candidate == rescuer ||
            Deleted(candidate) ||
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
        if (!rescue.AutoReturnShuttle ||
            rescue.ShuttleReturnRouted ||
            rescue.AssignedShuttleConsole is not { Valid: true } console ||
            Deleted(console) ||
            rescue.AssignedReturnTarget is not { Valid: true } returnTarget ||
            Deleted(returnTarget) ||
            !TryComp<ShuttleConsoleComponent>(console, out var shuttleConsole) ||
            !TryComp<HTNComponent>(console, out var htn))
        {
            return false;
        }

        _npc.SetBlackboard(console, shuttleConsole.AutopilotTargetKey, new EntityCoordinates(returnTarget, Vector2.Zero), htn);
        htn.Blackboard.Remove<Angle>(shuttleConsole.AutopilotRotationKey);
        _npc.WakeNPC(console, htn);
        rescue.ShuttleReturnRouted = true;
        rescue.ShuttleRoutedTarget = null;
        Dirty(uid, rescue);
        return true;
    }

    private bool TryRouteShuttleToTarget(EntityUid uid, LuaMRescueAgentComponent rescue, EntityUid target)
    {
        if (!rescue.AutoRouteShuttleToTargets ||
            IsOnAssignedShuttle(target, rescue) ||
            IsEvacuationComplete(target, rescue) ||
            rescue.ShuttleRoutedTarget == target ||
            rescue.AssignedShuttleConsole is not { Valid: true } console ||
            Deleted(console) ||
            Deleted(target) ||
            !TryComp<ShuttleConsoleComponent>(console, out var shuttleConsole) ||
            !TryComp<HTNComponent>(console, out var htn))
        {
            return false;
        }

        _npc.SetBlackboard(console, shuttleConsole.AutopilotTargetKey, new EntityCoordinates(target, Vector2.Zero), htn);
        htn.Blackboard.Remove<Angle>(shuttleConsole.AutopilotRotationKey);
        _npc.WakeNPC(console, htn);
        rescue.ShuttleRoutedTarget = target;
        rescue.ShuttleReturnRouted = false;
        Dirty(uid, rescue);
        return true;
    }

    private bool IsWithinRange(EntityUid uid, EntityUid target, float range)
    {
        var uidCoordinates = Transform(uid).Coordinates;
        var targetCoordinates = Transform(target).Coordinates;
        return uidCoordinates.TryDistance(EntityManager, targetCoordinates, out var distance) &&
               distance <= range;
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

    private bool IsTargetTemporarilySkipped(EntityUid target, LuaMRescueAgentComponent rescue)
    {
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

    private bool ShouldSkipFailedPatientDeliveryTarget(
        EntityUid target,
        EntityUid patientStrap,
        LuaMRescueAgentComponent rescue)
    {
        if (Deleted(target) ||
            Deleted(patientStrap) ||
            !TryComp<BuckleComponent>(target, out var buckle) ||
            !TryComp<StrapComponent>(patientStrap, out var strap) ||
            !IsAssignedShuttlePatientStrap(patientStrap, rescue))
        {
            return true;
        }

        if (buckle.BuckledTo == patientStrap)
            return false;

        if (!strap.Enabled ||
            IsDeliveryTargetTemporarilySkipped(patientStrap, rescue) ||
            strap.BuckledEntities.Count != 0)
        {
            return true;
        }

        return IsWithinRange(target, patientStrap, buckle.Range);
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

        if (TryFindPatientDeliveryStrap(rescue, out var replacementStrap, out _))
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

        SetRescueTask(
            uid,
            rescue,
            LuaMRescueTaskStage.DeliveringPatient,
            target,
            rescue.AssignedShuttleAnchor ?? rescue.AssignedShuttle,
            $"falling back to shuttle delivery for {FormatEntityRef(target)}");
        return SetFollowShuttle(uid, rescue, htn);
    }

    private bool UpdateTargetProgress(EntityUid uid, EntityUid target, LuaMRescueAgentComponent rescue)
    {
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
            return false;
        }

        if (distance + rescue.TargetProgressTolerance < rescue.LastProgressDistance)
        {
            rescue.LastProgressDistance = distance;
            rescue.TargetStallAccumulator = 0f;
            return false;
        }

        if (distance > rescue.LastProgressDistance + rescue.TargetProgressTolerance)
            rescue.LastProgressDistance = distance;

        rescue.TargetStallAccumulator += rescue.TargetRefreshInterval;
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

        if (directDistance <= directAllowRange)
            return true;

        var sourcePoly = _pathfinding.GetPoly(Transform(uid).Coordinates);
        var targetPoly = _pathfinding.GetPoly(Transform(target).Coordinates);

        if (sourcePoly == null || targetPoly == null)
        {
            penalty = 500f;
            return true;
        }

        if (sourcePoly.GraphUid != targetPoly.GraphUid)
            penalty = 250f;

        return true;
    }

    private void TemporarilySkipTarget(EntityUid uid, LuaMRescueAgentComponent rescue, HTNComponent htn, EntityUid target)
    {
        rescue.SkippedTargets[target] = _timing.CurTime + TimeSpan.FromSeconds(rescue.TargetSkipSeconds);

        RecordRescueHandoff(
            uid,
            rescue,
            target,
            "incomplete",
            $"aborted after stalled target for {rescue.TargetSkipSeconds:0.0}s",
            "returning or redeploying");

        StopPullingTarget(uid, target);
        ClearRescueTask(uid, rescue, $"skipped stalled target {FormatEntityRef(target)}");
        rescue.EvacuatingTarget = null;
        rescue.AssignedTarget = null;
        rescue.AssignedPatientStrap = null;
        rescue.ShuttleRoutedTarget = null;
        ResetTargetProgress(rescue);

        var hasPendingEvacuationTarget = HasPendingEvacuationTarget(uid, rescue, target);
        if (!hasPendingEvacuationTarget)
            TryRouteShuttleHome(uid, rescue);
        else
        {
            rescue.ShuttleReturnRouted = false;
            rescue.LastAutoEvacuationStatus = $"holding shuttle forward after skipping {FormatEntityRef(target)}; pending evacuation target detected";
        }

        StandbyAtAssignedShuttle(uid, rescue, htn, allowAutoReturn: !hasPendingEvacuationTarget);
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
    }

    private void CompleteEvacuation(EntityUid uid, LuaMRescueAgentComponent rescue, HTNComponent htn, EntityUid target)
    {
        StopPullingTarget(uid, target);
        ClearRescueTask(uid, rescue, $"completed evacuation of {FormatEntityRef(target)}");
        if (TryComp<MobStateComponent>(target, out var mobState) &&
            mobState.CurrentState == MobState.Dead)
        {
            TrySendRescueStatusComms(
                uid,
                rescue,
                $"patient-onboard-dead:{target}",
                $"Пациент {Name(target)} на борту. Начинаю реанимационный цикл.");
        }

        rescue.ShuttleRoutedTarget = null;
        ResetTargetProgress(rescue);
        var hasPendingEvacuationTarget = HasPendingEvacuationTarget(uid, rescue, target);
        if (!hasPendingEvacuationTarget)
            rescue.LastAutoEvacuationStatus = $"completed evacuation of {FormatEntityRef(target)}; return route requested";

        if (!TryComp<MobStateComponent>(target, out var onboardMobState) ||
            onboardMobState.CurrentState != MobState.Dead)
        {
            ReportLivingPatientOnboardStatus(uid, rescue, target, onboardMobState, hasPendingEvacuationTarget);
        }

        RecordRescueHandoff(
            uid,
            rescue,
            target,
            BuildPatientTreatmentResult(target, rescue),
            hasPendingEvacuationTarget
                ? "secured onboard; pending evacuation target detected"
                : "secured onboard; return route requested",
            hasPendingEvacuationTarget ? "redeploying to next patient" : "returning to shuttle base");

        if (!hasPendingEvacuationTarget)
            TryRouteShuttleHome(uid, rescue);
        else
        {
            rescue.ShuttleReturnRouted = false;
            rescue.LastAutoEvacuationStatus = $"holding shuttle forward after evacuation of {FormatEntityRef(target)}; pending evacuation target detected";
        }

        rescue.EvacuatingTarget = null;
        rescue.AssignedTarget = null;
        rescue.AssignedPatientStrap = null;
        StandbyAtAssignedShuttle(uid, rescue, htn, allowAutoReturn: !hasPendingEvacuationTarget);
        Dirty(uid, rescue);
    }

    private void ReportLivingPatientOnboardStatus(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        EntityUid target,
        MobStateComponent? mobState,
        bool hasPendingEvacuationTarget)
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

        var followUp = hasPendingEvacuationTarget
            ? "\u041f\u0440\u043e\u0434\u043e\u043b\u0436\u0430\u044e \u0441\u043b\u0435\u0434\u0443\u044e\u0449\u0438\u0439 \u0432\u044b\u0437\u043e\u0432."
            : "\u0412\u043e\u0437\u0432\u0440\u0430\u0449\u0430\u0435\u043c\u0441\u044f.";
        TrySendRescueStatusComms(
            uid,
            rescue,
            $"patient-onboard:{target}",
            $"\u041f\u0430\u0446\u0438\u0435\u043d\u0442 {Name(target)} \u043d\u0430 \u0431\u043e\u0440\u0442\u0443. \u041f\u0430\u0446\u0438\u0435\u043d\u0442 \u043f\u043e\u0434 \u043d\u0430\u0431\u043b\u044e\u0434\u0435\u043d\u0438\u0435\u043c. {followUp}");
    }

    private bool IsEvacuationComplete(EntityUid target, LuaMRescueAgentComponent rescue)
    {
        if (IsBuckledToAssignedShuttlePatientStrap(target, rescue))
            return true;

        if (TryFindPatientDeliveryStrap(rescue, out _, out _))
            return false;

        return IsOnAssignedShuttle(target, rescue) ||
               IsAtAssignedShuttleAnchor(target, rescue);
    }

    private bool TryReleaseStabilizedPatientOnShuttle(
        EntityUid uid,
        LuaMRescueAgentComponent rescue,
        HTNComponent htn)
    {
        if (!rescue.AutoReleaseStabilizedPatients)
            return false;

        if (!TryFindStabilizedShuttlePatient(rescue, out var patient, out var patientStrap, out var buckle, out var status))
        {
            if (!string.IsNullOrWhiteSpace(status))
            {
                rescue.LastAutoEvacuationStatus = status;

                if (status.StartsWith("holding critical onboard patient", StringComparison.OrdinalIgnoreCase))
                {
                    TrySendRescueStatusComms(
                        uid,
                        rescue,
                        $"patient-hold-critical:{rescue.AssignedShuttle}",
                        "\u041f\u0430\u0446\u0438\u0435\u043d\u0442 \u043d\u0430 \u0431\u043e\u0440\u0442\u0443, \u0441\u043e\u0441\u0442\u043e\u044f\u043d\u0438\u0435 \u043a\u0440\u0438\u0442\u0438\u0447\u0435\u0441\u043a\u043e\u0435. \u0414\u0435\u0440\u0436\u0443 \u043b\u0435\u0447\u0435\u043d\u0438\u0435 \u0434\u043e \u0441\u0442\u0430\u0431\u0438\u043b\u0438\u0437\u0430\u0446\u0438\u0438.");
                }
                else if (status.StartsWith("holding onboard patient", StringComparison.OrdinalIgnoreCase))
                {
                    TrySendRescueStatusComms(
                        uid,
                        rescue,
                        $"patient-hold-treatment:{rescue.AssignedShuttle}",
                        "\u041f\u0430\u0446\u0438\u0435\u043d\u0442 \u043d\u0430 \u0431\u043e\u0440\u0442\u0443, \u043f\u043e\u043a\u0430 \u043d\u0435 \u0441\u0442\u0430\u0431\u0438\u043b\u0435\u043d. \u041f\u0440\u043e\u0434\u043e\u043b\u0436\u0430\u044e \u043b\u0435\u0447\u0435\u043d\u0438\u0435 \u0438 \u043d\u0430\u0431\u043b\u044e\u0434\u0435\u043d\u0438\u0435.");
                }
                else if (status.StartsWith("holding dead onboard patient", StringComparison.OrdinalIgnoreCase))
                {
                    TrySendRescueStatusComms(
                        uid,
                        rescue,
                        $"patient-hold-dead:{rescue.AssignedShuttle}",
                        "Пациент без пульса удерживается на борту. Продолжаю реанимационный цикл.");
                }
            }

            return false;
        }

        SetRescueTask(
            uid,
            rescue,
            LuaMRescueTaskStage.DeliveringPatient,
            patient,
            patientStrap,
            $"releasing stabilized {FormatEntityRef(patient)} from {FormatEntityRef(patientStrap)}");

        if (!IsWithinRange(uid, patientStrap, rescue.AutoReleaseRange))
        {
            rescue.LastAutoEvacuationStatus = $"moving to release stabilized {FormatEntityRef(patient)} from {FormatEntityRef(patientStrap)}";
            SetFollowDeliveryStrap(uid, rescue, htn, patientStrap);
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

            RecordRescueHandoff(
                uid,
                rescue,
                patient,
                BuildPatientTreatmentResult(patient, rescue),
                "released from shuttle care",
                "available for next rescue");
            ClearFollowTarget(uid, rescue, htn);
            TrySendRescueStatusComms(
                uid,
                rescue,
                $"patient-release:{patient}",
                $"Пациент {Name(patient)} стабилен. Отпускаю с борта.");
        }
        else
        {
            SetFollowDeliveryStrap(uid, rescue, htn, patientStrap);
        }

        Dirty(uid, rescue);
        return true;
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
        var blockers = TryComp<LuaMRescueTeamComponent>(uid, out var team)
            ? BuildHandoffBlockerSummary(team)
            : "team scene memory unavailable";

        _rescueTeam.TryRecordRescueHandoff(
            uid,
            patient,
            location,
            treatmentResult,
            evacuationResult,
            blockers,
            "unverified",
            teamStatus);
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

    private static string BuildHandoffBlockerSummary(LuaMRescueTeamComponent team)
    {
        if (team.RecentThreatMemories <= 0 &&
            team.RecentCrowdMemories <= 0 &&
            team.RecentRouteMemories <= 0 &&
            team.NearbyBlockers <= 0)
        {
            return "none";
        }

        return $"threat/crowd/route={team.RecentThreatMemories}/{team.RecentCrowdMemories}/{team.RecentRouteMemories}; " +
               $"blockers={team.NearbyBlockers}; scene={team.LastSceneStatus}; memory={team.LastMemoryDigest}";
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
        LuaMRescueAgentComponent rescue,
        out EntityUid patient,
        out EntityUid patientStrap,
        out BuckleComponent buckle,
        out string status)
    {
        patient = default;
        patientStrap = default;
        buckle = default!;
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
                    !TryComp<BuckleComponent>(buckled, out var buckledComp) ||
                    buckledComp.BuckledTo != strapUid)
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
                    holdingStatus = patientStatus;
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

        if (!TryFindDeadShuttlePatient(rescue, out var patient, out var patientStrap, out var status))
        {
            if (!string.IsNullOrWhiteSpace(status))
                rescue.LastAutoDefibStatus = status;

            return false;
        }

        SetRescueTask(
            uid,
            rescue,
            LuaMRescueTaskStage.TreatingPatient,
            patient,
            patientStrap,
            $"defibrillating onboard {FormatEntityRef(patient)} at {FormatEntityRef(patientStrap)}");

        if (!IsWithinRange(uid, patientStrap, rescue.PlayerActionRange))
        {
            rescue.LastAutoDefibStatus = $"moving to onboard defibrillation {FormatEntityRef(patient)} at {FormatEntityRef(patientStrap)}";
            SetFollowDeliveryStrap(uid, rescue, htn, patientStrap);
            Dirty(uid, rescue);
            return true;
        }

        return TryAutoDefibTarget(uid, rescue, htn, patient);
    }

    private bool TryFindDeadShuttlePatient(
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
                    !TryComp<BuckleComponent>(buckled, out var buckledComp) ||
                    buckledComp.BuckledTo != strapUid ||
                    !TryComp<MobStateComponent>(buckled, out var mobState) ||
                    mobState.CurrentState != MobState.Dead)
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

    private bool HasPendingEvacuationTarget(EntityUid uid, LuaMRescueAgentComponent rescue, EntityUid completedTarget)
    {
        return TryFindEvacuationTarget(uid, rescue.SearchRange, rescue, out _, completedTarget);
    }

    private bool TryFindPatientDeliveryStrap(
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

        if (rescue.AssignedPatientStrap is { Valid: true } assigned &&
            !Deleted(assigned) &&
            TryComp<StrapComponent>(assigned, out var assignedStrap) &&
            IsAvailablePatientDeliveryStrap(assigned, assignedStrap, rescue))
        {
            patientStrap = assigned;
            strap = assignedStrap;
            return true;
        }

        var bestScore = float.MinValue;
        var query = EntityQueryEnumerator<StrapComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var strapComp, out var xform))
        {
            if (xform.GridUid != shuttle ||
                !IsAvailablePatientDeliveryStrap(uid, strapComp, rescue))
            {
                continue;
            }

            var score = GetPatientDeliveryStrapScore(uid, rescue);
            if (score <= bestScore)
                continue;

            patientStrap = uid;
            strap = strapComp;
            bestScore = score;
        }

        return patientStrap != default;
    }

    private bool IsAvailablePatientDeliveryStrap(
        EntityUid uid,
        StrapComponent strap,
        LuaMRescueAgentComponent rescue)
    {
        return strap.Enabled &&
               !IsDeliveryTargetTemporarilySkipped(uid, rescue) &&
               strap.BuckledEntities.Count == 0 &&
               IsAssignedShuttlePatientStrap(uid, rescue);
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

    private bool TryBucklePatientToStrap(
        EntityUid uid,
        EntityUid target,
        EntityUid patientStrap,
        LuaMRescueAgentComponent rescue)
    {
        if (!rescue.BucklePatientsOnShuttle ||
            Deleted(target) ||
            Deleted(patientStrap) ||
            !IsAssignedShuttlePatientStrap(patientStrap, rescue) ||
            !TryComp<BuckleComponent>(target, out var buckle) ||
            !TryComp<StrapComponent>(patientStrap, out var strap))
        {
            return false;
        }

        if (buckle.BuckledTo == patientStrap)
            return true;

        if (strap.BuckledEntities.Count != 0)
            return false;

        if (!IsWithinRange(target, patientStrap, buckle.Range))
            return false;

        return _buckle.TryBuckle(target, uid, patientStrap, buckle, popup: false);
    }

    private bool IsBuckledToAssignedShuttlePatientStrap(EntityUid target, LuaMRescueAgentComponent rescue)
    {
        return TryComp<BuckleComponent>(target, out var buckle) &&
               buckle.BuckledTo is { Valid: true } strap &&
               !Deleted(strap) &&
               IsAssignedShuttlePatientStrap(strap, rescue);
    }

    private void SetFollowTarget(EntityUid uid, LuaMRescueAgentComponent rescue, HTNComponent htn, EntityUid target)
    {
        rescue.AssignedTarget = target;
        _npc.SetBlackboard(uid, NPCBlackboard.FollowTarget, new EntityCoordinates(target, Vector2.Zero), htn);
        _npc.SetBlackboard(uid, "FollowCloseRange", rescue.FollowCloseRange, htn);
        _npc.SetBlackboard(uid, "FollowRange", rescue.FollowRange, htn);
        _npc.WakeNPC(uid, htn);
        Dirty(uid, rescue);
    }

    private bool SetFollowShuttle(EntityUid uid, LuaMRescueAgentComponent rescue, HTNComponent htn)
    {
        if (!TryGetShuttleAnchorCoordinates(rescue, out var coordinates))
            return false;

        _npc.SetBlackboard(uid, NPCBlackboard.FollowTarget, coordinates, htn);
        _npc.SetBlackboard(uid, "FollowCloseRange", rescue.FollowCloseRange, htn);
        _npc.SetBlackboard(uid, "FollowRange", rescue.FollowRange, htn);
        _npc.WakeNPC(uid, htn);
        Dirty(uid, rescue);
        return true;
    }

    private bool SetFollowDeliveryStrap(EntityUid uid, LuaMRescueAgentComponent rescue, HTNComponent htn, EntityUid patientStrap)
    {
        if (Deleted(patientStrap))
            return SetFollowShuttle(uid, rescue, htn);

        _npc.SetBlackboard(uid, NPCBlackboard.FollowTarget, new EntityCoordinates(patientStrap, Vector2.Zero), htn);
        _npc.SetBlackboard(uid, "FollowCloseRange", rescue.FollowCloseRange, htn);
        _npc.SetBlackboard(uid, "FollowRange", rescue.FollowRange, htn);
        _npc.WakeNPC(uid, htn);
        Dirty(uid, rescue);
        return true;
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

        if (CanUseAssignedShuttle(rescue))
        {
            if (allowAutoReturn)
                TryRouteShuttleHome(uid, rescue);

            if (!IsOnAssignedShuttle(uid, rescue) &&
                SetFollowShuttle(uid, rescue, htn))
            {
                return;
            }
        }

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
        var agent = system.SpawnAgent(anchorUid, target, shell.Player, control);
        var netAgent = _entities.GetNetEntity(agent);
        var targetText = target is { Valid: true } targetUid
            ? _entities.GetNetEntity(targetUid).ToString()
            : "none";

        shell.WriteLine($"Spawned LuaM rescue agent {netAgent}; followTarget={targetText}; controlled={control}.");
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
    public string Description => "Prints LuaM rescue agent mission telemetry.";
    public string Help => $"Usage: {Command}";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var system = _entities.System<LuaMRescueAgentSystem>();
        var lines = system.BuildRescueStatusLines();
        if (lines.Count == 0)
        {
            shell.WriteLine("No LuaM rescue agents are active.");
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
