using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._LuaM.Sector;
using Content.Shared._CorvaxNext.Silicons.Borgs.Components;
using Content.Shared._Crescent.DroneControl;
using Content.Shared._EinsteinEngines.Silicon.Components;
using Content.Server.Chat.Systems;
using Content.Server.Gateway.Components;
using Content.Server.Hands.Systems;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Server.Teleportation;
using Content.Shared.Buckle.Components;
using Content.Shared.Chat;
using Content.Shared.CombatMode;
using Content.Shared.Examine;
using Content.Shared.Hands.Components;
using Content.Shared.Inventory;
using Content.Shared.Interaction;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Content.Shared.Silicons.Borgs.Components;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Tag;
using Content.Shared.Teleportation.Components;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._LuaM.Rescue;

public sealed class LuaMRescueTeamSystem : EntitySystem
{
    // Team coordination/telemetry components are server-authoritative.
    private static void Dirty(EntityUid _, LuaMRescueTeamComponent __) { }
    private static void Dirty(EntityUid _, LuaMRescueEscortComponent __) { }
    private static void Dirty(EntityUid _, LuaMRescueActivityCarrierComponent __) { }

    private const string EscortPrototype = "LuaMRescueEscort";
    private const float SceneScanRange = 6f;
    private const int CrowdPressureThreshold = 4;
    private const int RouteBlockerThreshold = 2;
    private const int SceneMemoryLimit = 8;
    private const double SceneMemoryLifetimeSeconds = 90;
    private const double SceneMemoryReinforceSeconds = 18;
    private const double HandoffPhaseHoldSeconds = 6;
    private const double SortiePlanHoldSeconds = 4;
    private const double TeamPhaseAnnouncementCooldownSeconds = 20;
    private const double TeamSharedSpeechCooldownSeconds = 6;
    private const int TeamRecentLineMemoryLimit = 8;
    private const double TeamRecentLineMemorySeconds = 90;
    private const double ThreatNeutralizedReportCooldownSeconds = 6;
    private const double TriageCoverConfirmCooldownSeconds = 20;
    private const double CrewHelpRequestCooldownSeconds = 12;
    private const double EscortSpeechCooldownSeconds = 30;
    private const double EscortDutyHoldSeconds = 2;
    private const double EscortDutyActionIntervalSeconds = 2;
    private const float EscortDutyActionRange = 1.75f;
    private const float EscortThreatScreenRange = 7f;
    private const float EscortThreatLeashRange = 8.5f;
    private const float EscortActivityArrivalRange = 2.5f;
    private const float EscortPatientAssistRange = 1.5f;
    private const float EscortCrowdControlRange = 3f;
    private const double EscortTerminalRecoveryPollSeconds = 0.25;
    private const double EscortTerminalRecoveryProbeTimeoutSeconds = 5;
    private const double EscortTerminalDormantObservationSeconds = 15;
    private const float RouteBlockerDropoffDistance = 3.5f;
    private const float RouteBlockerReleaseDistance = 3.25f;
    private const float RouteBlockerReleaseDistanceSquared = RouteBlockerReleaseDistance * RouteBlockerReleaseDistance;
    private const string BotTag = "Bot";

    private static readonly (LuaMRescueEscortRole Role, Vector2 Offset)[] EscortFormation =
    [
        (LuaMRescueEscortRole.Tourniquet, new Vector2(0f, 1.5f)),
        (LuaMRescueEscortRole.Kostyl, new Vector2(-1f, 0f)),
        (LuaMRescueEscortRole.Zaslon, new Vector2(0f, -1.5f)),
    ];

    private static readonly string[] EscortCombatStorageSlotPriority =
    [
        "back",
        "belt",
        "suitstorage",
        "outerClothing",
    ];

    [Dependency] private readonly NPCSystem _npc = default!;
    [Dependency] private readonly HTNSystem _htnSystem = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly MetaDataSystem _metaData = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly NpcFactionSystem _factions = default!;
    [Dependency] private readonly HandsSystem _hands = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly PullingSystem _pulling = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly ExamineSystemShared _examine = default!;
    [Dependency] private readonly LuaMRescueActivityCoordinatorSystem _activity = default!;
    [Dependency] private readonly LuaMRescueNavigationSystem _rescueNavigation = default!;
    [Dependency] private readonly PortalSystem _portal = default!;
    [Dependency] private readonly SharedCombatModeSystem _combatMode = default!;
    [Dependency] private readonly SharedStorageSystem _storage = default!;
    [Dependency] private readonly LuaMSectorStorySystem _sectorStory = default!;
    [Dependency] private readonly TagSystem _tag = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private readonly HashSet<EntityUid> _sceneEntities = new();
    private int _nextTeamId = 1;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var teamQuery = EntityQueryEnumerator<LuaMRescueTeamComponent, LuaMRescueAgentComponent>();
        while (teamQuery.MoveNext(out var uid, out var team, out var rescue))
        {
            UpdateLeaderTeamState(uid, team, rescue, frameTime);
        }

        var escortQuery = EntityQueryEnumerator<LuaMRescueEscortComponent, HTNComponent>();
        while (escortQuery.MoveNext(out var uid, out var escort, out var htn))
        {
            if (HasComp<ActorComponent>(uid))
                continue;

            escort.DutyRefreshAccumulator += frameTime;
            if (escort.DutyRefreshAccumulator < escort.DutyRefreshInterval)
                continue;

            escort.DutyRefreshAccumulator = 0f;
            UpdateEscortDuty(uid, escort, htn);
        }
    }

    public bool RefreshEscortBehaviorExecution(EntityUid uid)
    {
        if (HasComp<ActorComponent>(uid) ||
            !TryComp<LuaMRescueEscortComponent>(uid, out var escort) ||
            !TryComp<HTNComponent>(uid, out var htn))
        {
            return false;
        }

        escort.DutyRefreshAccumulator = 0f;
        UpdateEscortDuty(uid, escort, htn);
        return true;
    }

    public void PurgeMedicalTargetState(EntityUid target, string reason)
    {
        var teamQuery = EntityQueryEnumerator<LuaMRescueTeamComponent>();
        while (teamQuery.MoveNext(out var teamUid, out var team))
        {
            var changed = false;
            if (team.Leader == target)
            {
                team.Leader = null;
                changed = true;
            }

            if (team.Patient == target)
            {
                team.Patient = TryComp<LuaMRescueAgentComponent>(teamUid, out var leaderRescue)
                    ? GetActiveRescuePatient(leaderRescue)
                    : null;
                if (team.Patient == target)
                    team.Patient = null;
                changed = true;
            }

            if (team.Shuttle == target)
            {
                team.Shuttle = null;
                changed = true;
            }

            if (team.ShuttleAnchor == target)
            {
                team.ShuttleAnchor = null;
                changed = true;
            }

            if (team.TriageCoverConfirmedPatient == target)
            {
                team.TriageCoverConfirmedPatient = null;
                changed = true;
            }

            if (team.LastHandoffPatient == target)
            {
                team.LastHandoffPatient = null;
                changed = true;
            }

            if (team.LastThreatNeutralizedTarget == target)
            {
                team.LastThreatNeutralizedTarget = null;
                changed = true;
            }

            if (team.LastThreatNeutralizedBy == target)
            {
                team.LastThreatNeutralizedBy = null;
                changed = true;
            }

            if (team.SceneAnchor == target)
            {
                team.SceneAnchor = null;
                changed = true;
            }

            if (team.ThreatTarget == target)
            {
                team.ThreatTarget = null;
                changed = true;
            }

            if (team.CrowdTarget == target)
            {
                team.CrowdTarget = null;
                changed = true;
            }

            if (team.RouteBlockerTarget == target)
            {
                team.RouteBlockerTarget = null;
                changed = true;
            }

            if (team.SceneMemory.RemoveAll(memory =>
                    memory.Anchor == target || memory.ThreatTarget == target) > 0)
            {
                RefreshSceneMemoryDigest(team);
                changed = true;
            }

            if (team.Escorts.Remove(target))
                changed = true;

            if (!changed)
                continue;

            team.LastStatus =
                $"patient identity purged: target={FormatEntityRef(target)}; reason={reason}";
            Dirty(teamUid, team);
        }

        var escortQuery = EntityQueryEnumerator<LuaMRescueEscortComponent>();
        while (escortQuery.MoveNext(out var escortUid, out var escort))
            PurgeEscortMedicalTargetState(escortUid, escort, target, reason);
    }

    private void PurgeEscortMedicalTargetState(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        EntityUid target,
        string reason)
    {
        TryComp<LuaMRescueActivityCarrierComponent>(uid, out var carrier);
        TryComp<HTNComponent>(uid, out var htn);

        var carrierReferencesTarget = carrier != null &&
                                      (carrier.ActivityContext.Target == target ||
                                       carrier.ActivityContext.Destination is { } destination &&
                                       destination.EntityId == target);
        var htnReferencesTarget = false;
        if (htn != null)
        {
            htnReferencesTarget =
                (htn.Blackboard.TryGetValue<EntityCoordinates>(
                     NPCBlackboard.FollowTarget,
                     out var followTarget,
                     EntityManager) &&
                 followTarget.EntityId == target) ||
                (htn.Blackboard.TryGetValue<EntityUid>(
                     NPCBlackboard.CurrentOrderedTarget,
                     out var orderedTarget,
                     EntityManager) &&
                 orderedTarget == target);
        }

        EntityUid? pulledTarget = null;
        if (TryComp<PullerComponent>(uid, out var puller) &&
            puller.Pulling is { Valid: true } pulled)
        {
            pulledTarget = pulled;
        }

        var activeTarget = escort.CurrentFollowTarget == target ||
                           carrierReferencesTarget ||
                           htnReferencesTarget ||
                           pulledTarget == target;
        var changed = false;
        if (escort.Leader == target)
        {
            escort.Leader = null;
            changed = true;
        }

        if (escort.Patient == target)
        {
            escort.Patient = null;
            changed = true;
        }

        if (escort.Shuttle == target)
        {
            escort.Shuttle = null;
            changed = true;
        }

        if (escort.ShuttleAnchor == target)
        {
            escort.ShuttleAnchor = null;
            changed = true;
        }

        if (escort.SceneAnchor == target)
        {
            escort.SceneAnchor = null;
            changed = true;
        }

        if (escort.ThreatTarget == target)
        {
            escort.ThreatTarget = null;
            changed = true;
        }

        if (escort.CrowdTarget == target)
        {
            escort.CrowdTarget = null;
            changed = true;
        }

        if (escort.RouteBlockerTarget == target)
        {
            escort.RouteBlockerTarget = null;
            changed = true;
        }

        if (escort.PatientAssistAttemptTarget == target)
        {
            ResetPatientAssistAttempts(escort);
            changed = true;
        }

        if (escort.PatientHandoffAttemptTarget == target)
        {
            ResetPatientHandoffAttempts(escort);
            changed = true;
        }

        if (escort.ClearRoutePullAttemptTarget == target)
        {
            ResetClearRoutePullAttempts(escort);
            changed = true;
        }

        if (escort.ClearRouteReleaseAttemptTarget == target)
        {
            ResetClearRouteReleaseAttempts(escort);
            changed = true;
        }

        if (activeTarget)
        {
            if (pulledTarget == target &&
                TryComp<PullableComponent>(target, out var pullable))
            {
                _pulling.TryStopPull(target, pullable, uid);
            }

            escort.CurrentFollowTarget = null;
            escort.CurrentDuty = LuaMRescueEscortDuty.Standby;
            escort.PendingDuty = LuaMRescueEscortDuty.Standby;
            escort.DutyUpdatedAt = _timing.CurTime;
            escort.PendingDutySince = _timing.CurTime;
            escort.SortiePlan = LuaMRescueSortiePlan.Standby;

            // Force the carrier through its own role-aware Standby mapping so
            // the old target/destination and terminal recovery generation are
            // replaced atomically before HTN replans.
            if (carrier != null)
            {
                var previousGeneration = carrier.ActivityContext.Generation;
                carrier.ActivityContext = new LuaMRescueActivityContext
                {
                    Activity = LuaMRescueActivity.None,
                    TerminalStatus = LuaMRescueTerminalStatus.Cancelled,
                    Generation = previousGeneration,
                };
            }

            UpdateEscortActivityCarrier(
                uid,
                escort,
                LuaMRescueEscortDuty.Standby,
                followTarget: null);

            if (htn != null)
            {
                EntityUid? preservedPulledTarget =
                    pulledTarget is { Valid: true } other && other != target
                        ? other
                        : null;
                CancelEscortIntent(
                    uid,
                    htn,
                    preservedPulledTarget,
                    cancelRoute: true,
                    replan: false);
                _htnSystem.Replan(htn);
            }
            else
            {
                _rescueNavigation.CancelRoute(uid, target);
            }

            changed = true;
        }

        if (!changed)
            return;

        escort.LastDutyStatus =
            $"patient identity purged: target={FormatEntityRef(target)}; reason={reason}";
        Dirty(uid, escort);
    }

    public List<EntityUid> SpawnEscortTeam(
        EntityUid leader,
        EntityUid anchor,
        EntityUid? patient,
        EntityUid? shuttle,
        EntityUid? shuttleAnchor)
    {
        var team = EnsureComp<LuaMRescueTeamComponent>(leader);
        if (team.TeamId <= 0)
            team.TeamId = _nextTeamId++;

        team.Leader = leader;
        team.Patient = ValidOrNull(patient);
        team.Shuttle = ValidOrNull(shuttle);
        team.ShuttleAnchor = ValidOrNull(shuttleAnchor);
        team.Phase = team.Patient is { Valid: true }
            ? LuaMRescueTeamPhase.Dispatch
            : LuaMRescueTeamPhase.Idle;
        team.SortiePlan = team.Patient is { Valid: true }
            ? LuaMRescueSortiePlan.ApproachPatient
            : LuaMRescueSortiePlan.Standby;
        team.SortiePlanUpdatedAt = _timing.CurTime;
        team.PendingSortiePlan = team.SortiePlan;
        team.PendingSortiePlanSince = _timing.CurTime;
        team.SortiePlanTransitions = 0;
        team.LastSortiePlanStatus = $"plan {FormatPlan(team.SortiePlan)} after dispatch";
        team.LastStatus = "autonomous rescue team deployed";
        team.LastAnnouncedPhase = LuaMRescueTeamPhase.Idle;
        team.LastAnnouncedSomberScene = false;
        team.LastPhaseAnnouncementStatus = "phase-bark pending";
        team.NextPhaseAnnouncementAt = _timing.CurTime;
        team.LastSharedSpeechStatus = "shared-speech ready";
        team.NextSharedSpeechAt = _timing.CurTime;
        team.LastReturnOrExtractReasonStatus = "none";
        team.LastEvacuationFormationStatus = "none";
        team.LastEscortActivityDigest = "none";
        team.LastCrewHelpAcknowledgementStatus = "none";
        team.LastThreatNeutralizedTarget = null;
        team.LastThreatNeutralizedBy = null;
        team.LastThreatNeutralizedStatus = "none";
        team.NextThreatNeutralizedReportAt = _timing.CurTime;
        team.ThreatTarget = null;
        team.CrowdTarget = null;
        team.RouteBlockerTarget = null;
        team.NearbyHostiles = 0;
        team.NearbyCombatants = 0;
        team.NearbyCrowd = 0;
        team.NearbyBlockers = 0;
        team.LastMemoryDigest = "memory clear";
        team.RecentThreatMemories = 0;
        team.RecentCrowdMemories = 0;
        team.RecentRouteMemories = 0;
        team.LastHandoffPatient = null;
        team.LastHandoffUpdatedAt = _timing.CurTime;
        team.HandoffRecords = 0;
        team.LastHandoffRecord = "handoff pending";
        team.LastHandoffDigest = "after-action pending";
        team.TriageCoverConfirmedPatient = null;
        team.LastTriageCoverDecisionKey = "none";
        team.LastTriageCoverStatus = "none";
        team.NextTriageCoverConfirmAt = _timing.CurTime;
        team.SceneScanAccumulator = team.SceneScanInterval;
        team.Escorts.Clear();
        team.SceneMemory.Clear();

        var anchorCoordinates = Transform(anchor).Coordinates;
        foreach (var (role, offset) in EscortFormation)
        {
            var escortUid = Spawn(EscortPrototype, anchorCoordinates.Offset(offset));
            ConfigureEscort(escortUid, team.TeamId, role, leader, team.Patient, team.Shuttle, team.ShuttleAnchor);
            team.Escorts.Add(escortUid);
        }

        Dirty(leader, team);
        return new List<EntityUid>(team.Escorts);
    }

    public List<string> BuildRescueTeamStatusLines()
    {
        var lines = new List<string>();

        var teamQuery = EntityQueryEnumerator<LuaMRescueTeamComponent>();
        while (teamQuery.MoveNext(out var uid, out var team))
        {
            PruneTeamEscorts(team);
            PruneTeamSpeechMemory(team, _timing.CurTime);
            lines.Add(
                $"team={team.TeamId}; leader={FormatEntityRef(uid)}; phase={FormatPhase(team.Phase)}; " +
                $"plan={FormatPlan(team.SortiePlan)}; planAge={GetSortiePlanAgeSeconds(team)}s; planTransitions={team.SortiePlanTransitions}; " +
                $"patient={FormatEntityRef(team.Patient)}; shuttle={FormatEntityRef(team.Shuttle)}; " +
                $"escorts={team.Escorts.Count}; scene={team.LastSceneStatus}; " +
                $"threat={FormatEntityRef(team.ThreatTarget)}; crowd={team.NearbyCrowd}; crowdTarget={FormatEntityRef(team.CrowdTarget)}; " +
                $"blockers={team.NearbyBlockers}; blockerTarget={FormatEntityRef(team.RouteBlockerTarget)}; " +
                $"memory={team.LastMemoryDigest}; handoff={team.LastHandoffRecord}; returnReason={team.LastReturnOrExtractReasonStatus}; " +
                $"evacFormation={team.LastEvacuationFormationStatus}; " +
                $"escortActivities={team.LastEscortActivityDigest}; " +
                $"crewHelpAck={team.LastCrewHelpAcknowledgementStatus}; " +
                $"planStatus={team.LastSortiePlanStatus}; triageCover={team.LastTriageCoverStatus}; " +
                $"threatClear={team.LastThreatNeutralizedStatus}; " +
                $"phaseBark={team.LastPhaseAnnouncementStatus}; speech={team.LastSharedSpeechStatus}; " +
                $"lastLine={team.LastTeamLineKey}; recentLines={team.RecentTeamLines.Count}; last={team.LastStatus}");
        }

        var escortQuery = EntityQueryEnumerator<LuaMRescueEscortComponent>();
        while (escortQuery.MoveNext(out var uid, out var escort))
        {
            var activityTelemetry = TryComp<LuaMRescueActivityCarrierComponent>(uid, out var activityCarrier)
                ? BuildEscortActivityTelemetry(activityCarrier)
                : "activityRole=none; activity=none; activityTerminal=none; activityGeneration=0; activityTarget=none";
            lines.Add(
                $"escort={FormatEntityRef(uid)}; team={escort.TeamId}; role={FormatRole(escort.Role)}; " +
                $"plan={FormatPlan(escort.SortiePlan)}; duty={FormatDuty(escort.CurrentDuty)}; " +
                $"dutyAge={GetEscortDutyAgeSeconds(escort)}s; dutyTransitions={escort.DutyTransitions}; " +
                $"follow={FormatEntityRef(escort.CurrentFollowTarget)}; " +
                $"leader={FormatEntityRef(escort.Leader)}; patient={FormatEntityRef(escort.Patient)}; " +
                $"threat={FormatEntityRef(escort.ThreatTarget)}; crowdTarget={FormatEntityRef(escort.CrowdTarget)}; " +
                $"blockerTarget={FormatEntityRef(escort.RouteBlockerTarget)}; " +
                $"{activityTelemetry}; " +
                $"lifeSupport={NormalizeHandoffValue(escort.LastLifeSupportStatus, "not checked")}; " +
                $"lifeSupportSwaps={escort.LifeSupportSwapCount}; " +
                $"scene={escort.LastSceneStatus}; weapon={escort.LastWeaponReadinessStatus}; action={escort.LastDutyActionStatus}; " +
                $"crewHelp={escort.LastCrewHelpStatus}; memory={escort.LastMemoryDigest}; last={escort.LastDutyStatus}");
        }

        return lines;
    }

    public List<string> BuildRescueAiMemoryDigestLines(int limit = 4)
    {
        var lines = new List<string>();

        var teamQuery = EntityQueryEnumerator<LuaMRescueTeamComponent>();
        while (teamQuery.MoveNext(out _, out var team))
        {
            PruneTeamSpeechMemory(team, _timing.CurTime);
            var escortCount = team.Escorts.Count(escort => escort.Valid && !Deleted(escort));
            lines.Add(
                $"ADMIN_ONLY: rescue sortie digest: team={team.TeamId}; autonomy=escort-group; " +
                $"phase={FormatPhase(team.Phase)}; plan={FormatPlan(team.SortiePlan)}; planAge={GetSortiePlanAgeSeconds(team)}s; " +
                $"planTransitions={team.SortiePlanTransitions}; escorts={escortCount}; scene={team.LastSceneStatus}; " +
                $"pressure(threat/crowd/route)={team.RecentThreatMemories}/{team.RecentCrowdMemories}/{team.RecentRouteMemories}; " +
                $"memory={team.LastMemoryDigest}; handoff={team.LastHandoffDigest}; returnReason={team.LastReturnOrExtractReasonStatus}; " +
                $"evacFormation={team.LastEvacuationFormationStatus}; " +
                $"escortActivities={team.LastEscortActivityDigest}; " +
                $"crewHelpAck={team.LastCrewHelpAcknowledgementStatus}; " +
                $"planStatus={team.LastSortiePlanStatus}; triageCover={team.LastTriageCoverStatus}; " +
                $"threatClear={team.LastThreatNeutralizedStatus}; " +
                $"phaseBark={team.LastPhaseAnnouncementStatus}; speech={team.LastSharedSpeechStatus}; " +
                $"lastLine={team.LastTeamLineKey}; recentLines={team.RecentTeamLines.Count}; " +
                "identities=withheld; coordinates=withheld.");
        }

        return lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(limit)
            .ToList();
    }

    public bool TryRecordRescueHandoff(
        EntityUid leader,
        EntityUid patient,
        string location,
        string treatmentResult,
        string evacuationResult,
        string blockers,
        string playerContribution,
        string teamStatus)
    {
        if (!TryComp<LuaMRescueTeamComponent>(leader, out var team))
            return false;

        location = NormalizeHandoffValue(location, "unknown");
        treatmentResult = NormalizeHandoffValue(treatmentResult, "unknown");
        evacuationResult = NormalizeHandoffValue(evacuationResult, "unknown");
        blockers = NormalizeHandoffValue(blockers, "none");
        playerContribution = NormalizeHandoffValue(playerContribution, "unverified");
        teamStatus = NormalizeHandoffValue(teamStatus, "available");
        var crewHelpAcknowledgement = BuildCrewHelpAcknowledgementStatus(blockers, playerContribution);
        if (!string.Equals(crewHelpAcknowledgement, "none", StringComparison.OrdinalIgnoreCase))
            playerContribution = $"{playerContribution}, {crewHelpAcknowledgement}";

        team.LastHandoffPatient = ValidOrNull(patient);
        team.LastHandoffUpdatedAt = _timing.CurTime;
        team.HandoffRecords++;
        team.LastCrewHelpAcknowledgementStatus = crewHelpAcknowledgement;
        team.LastHandoffRecord =
            $"handoff record #{team.HandoffRecords}: patient={FormatEntityRef(patient)}; location={location}; " +
            $"treatment={treatmentResult}; evacuation={evacuationResult}; blockers={blockers}; " +
            $"playerContribution={playerContribution}; crewHelpAck={team.LastCrewHelpAcknowledgementStatus}; teamStatus={teamStatus}";
        team.LastHandoffDigest =
            $"after-action record #{team.HandoffRecords}: patient=withheld; location={location}; " +
            $"treatment={treatmentResult}; evacuation={evacuationResult}; blockers={blockers}; " +
            $"playerContribution={playerContribution}; crewHelpAck={team.LastCrewHelpAcknowledgementStatus}; " +
            $"teamStatus={teamStatus}; identities=withheld; coordinates=withheld";
        TrySayCrewHelpAcknowledgement(leader, team, crewHelpAcknowledgement);

        _sectorStory.TryRecordRescueAfterAction(
            "LuaM Rescue",
            "withheld",
            location,
            treatmentResult,
            evacuationResult,
            blockers,
            playerContribution,
            teamStatus,
            out _);

        Dirty(leader, team);
        return true;
    }

    private bool TrySayCrewHelpAcknowledgement(
        EntityUid leader,
        LuaMRescueTeamComponent team,
        string acknowledgement)
    {
        if (Deleted(leader) ||
            !acknowledgement.StartsWith("crew-help-ack:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var now = _timing.CurTime;
        var line = "\u042d\u043a\u0438\u043f\u0430\u0436, \u043f\u043e\u043c\u043e\u0449\u044c \u0437\u0430\u0441\u0447\u0438\u0442\u0430\u043d\u0430. \u041a\u043e\u0440\u0438\u0434\u043e\u0440 \u0441\u0440\u0430\u0431\u043e\u0442\u0430\u043b.";
        if (!TryReserveTeamSpeech(
                leader,
                team,
                $"crew-help-ack:{team.HandoffRecords}",
                now,
                out var speechStatusChanged,
                line))
        {
            return speechStatusChanged;
        }

        _chat.TrySendInGameICMessage(
            leader,
            line,
            InGameICChatType.Speak,
            hideChat: false,
            hideLog: true);
        return true;
    }

    private static string BuildCrewHelpAcknowledgementStatus(string blockers, string playerContribution)
    {
        if (!ContainsCrewHelpRequest(playerContribution) ||
            !HasClearedCrewHelpRoute(blockers))
        {
            return "none";
        }

        return "crew-help-ack: route clear; contribution recorded";
    }

    private static bool ContainsCrewHelpRequest(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Contains("crew-help requested:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasClearedCrewHelpRoute(string blockers)
    {
        if (string.IsNullOrWhiteSpace(blockers))
            return false;

        var normalized = blockers.Replace(" ", string.Empty);
        if (normalized.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("none,", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalized.StartsWith("threat/crowd/route=0/0/0", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHandoffValue(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        return value.Trim().Replace(';', ',');
    }

    private void ConfigureEscort(
        EntityUid uid,
        int teamId,
        LuaMRescueEscortRole role,
        EntityUid leader,
        EntityUid? patient,
        EntityUid? shuttle,
        EntityUid? shuttleAnchor)
    {
        var escort = EnsureComp<LuaMRescueEscortComponent>(uid);
        escort.TeamId = teamId;
        escort.Role = role;
        escort.Leader = leader;
        escort.Patient = ValidOrNull(patient);
        escort.Shuttle = ValidOrNull(shuttle);
        escort.ShuttleAnchor = ValidOrNull(shuttleAnchor);
        escort.CurrentDuty = LuaMRescueEscortDuty.Standby;
        escort.DutyUpdatedAt = _timing.CurTime;
        escort.PendingDuty = escort.CurrentDuty;
        escort.PendingDutySince = _timing.CurTime;
        escort.DutyTransitions = 0;
        escort.SortiePlan = LuaMRescueSortiePlan.Standby;
        escort.LastDutyStatus = "deployed";
        escort.LastDutyActionStatus = "deployed";
        escort.LastWeaponReadinessStatus = "deployed";
        escort.LastCrewHelpStatus = "none";
        escort.LastCrewHelpKey = "none";
        escort.DutyActions = 0;
        escort.NextDutyActionAt = _timing.CurTime;
        escort.NextSpeechTime = _timing.CurTime;
        escort.NextCrewHelpRequestAt = _timing.CurTime;
        escort.PatientAssistAttemptTarget = null;
        escort.PatientAssistAttempts = 0;
        escort.NextPatientAssistAttemptAt = TimeSpan.Zero;
        escort.PatientHandoffAttemptTarget = null;
        escort.PatientHandoffAttempts = 0;
        escort.NextPatientHandoffAttemptAt = TimeSpan.Zero;
        escort.ClearRoutePullAttemptTarget = null;
        escort.ClearRoutePullAttempts = 0;
        escort.NextClearRoutePullAttemptAt = TimeSpan.Zero;
        escort.ClearRouteReleaseAttemptTarget = null;
        escort.ClearRouteReleaseAttempts = 0;
        escort.NextClearRouteReleaseAttemptAt = TimeSpan.Zero;

        var activityCarrier = EnsureComp<LuaMRescueActivityCarrierComponent>(uid);
        activityCarrier.ActivityRole = LuaMRescueRole.None;
        activityCarrier.ActivityRoleProfile = new LuaMRescueRoleProfile();
        activityCarrier.ActivityContext = new LuaMRescueActivityContext();
        activityCarrier.SourceDuty = LuaMRescueEscortDuty.Standby;
        activityCarrier.IntentTransitions = 0;
        ResetEscortTerminalRecovery(activityCarrier, "not-evaluated");
        activityCarrier.LastStatus = "role=None; activity=None; terminal=None; generation=0; target=none";

        _metaData.SetEntityName(uid, GetRoleName(role));

        if (TryComp<HTNComponent>(uid, out var htn))
            UpdateEscortDuty(uid, escort, htn, forceSpeech: true);
        else
            UpdateEscortActivityCarrier(uid, escort, LuaMRescueEscortDuty.Standby, followTarget: null);

        Dirty(uid, escort);
    }

    private void UpdateLeaderTeamState(
        EntityUid uid,
        LuaMRescueTeamComponent team,
        LuaMRescueAgentComponent rescue,
        float frameTime)
    {
        var patient = GetActiveRescuePatient(rescue);
        var shuttle = ValidOrNull(rescue.AssignedShuttle);
        var shuttleAnchor = ValidOrNull(rescue.AssignedShuttleAnchor);
        var phase = GetTeamPhase(team, rescue, patient);
        var returnOrExtractReason = BuildReturnOrExtractReasonStatus(team, rescue, phase);

        var changed = false;
        var status = BuildTeamStatus(team, rescue, phase, returnOrExtractReason);

        if (team.Leader != uid)
        {
            team.Leader = uid;
            changed = true;
        }

        if (team.Patient != patient)
        {
            team.Patient = patient;
            changed = true;
        }

        if (team.Shuttle != shuttle)
        {
            team.Shuttle = shuttle;
            changed = true;
        }

        if (team.ShuttleAnchor != shuttleAnchor)
        {
            team.ShuttleAnchor = shuttleAnchor;
            changed = true;
        }

        if (team.Phase != phase)
        {
            team.Phase = phase;
            changed = true;
        }

        if (!string.Equals(team.LastStatus, status, StringComparison.Ordinal))
        {
            team.LastStatus = status;
            changed = true;
        }

        if (!string.Equals(team.LastReturnOrExtractReasonStatus, returnOrExtractReason, StringComparison.Ordinal))
        {
            team.LastReturnOrExtractReasonStatus = returnOrExtractReason;
            changed = true;
        }

        changed |= UpdateEvacuationFormationStatus(team, phase, patient);
        changed |= PruneTeamEscorts(team);
        changed |= UpdateEscortActivityDigest(team);
        var somberScene = IsSomberTeamScene(team, rescue, patient);
        changed |= TrySayTeamPhaseLine(uid, team, phase, somberScene, returnOrExtractReason);
        team.SceneScanAccumulator += frameTime;
        if (team.SceneScanAccumulator >= team.SceneScanInterval)
        {
            team.SceneScanAccumulator = 0f;
            var scene = ScanRescueScene(uid, team.TeamId, uid, patient, shuttle, shuttleAnchor);
            changed |= UpdateTeamScene(team, scene);
            changed |= RememberScenePressure(team, scene);
        }
        else
        {
            changed |= PruneSceneMemory(team);
        }

        changed |= UpdateSortiePlan(team, phase, patient);
        changed |= TryConfirmTriageCover(team, rescue, patient);

        if (changed)
            Dirty(uid, team);
    }

    private bool UpdateEvacuationFormationStatus(
        LuaMRescueTeamComponent team,
        LuaMRescueTeamPhase phase,
        EntityUid? patient)
    {
        var status = phase is LuaMRescueTeamPhase.PrepareEvacuation or LuaMRescueTeamPhase.EvacuateToShuttle &&
            patient is { Valid: true } patientUid &&
            !Deleted(patientUid)
                ? BuildEvacuationFormationStatus(team, patientUid)
                : "none";

        if (string.Equals(team.LastEvacuationFormationStatus, status, StringComparison.Ordinal))
            return false;

        team.LastEvacuationFormationStatus = status;
        return true;
    }

    private string BuildEvacuationFormationStatus(LuaMRescueTeamComponent team, EntityUid patient)
    {
        var shuttleTarget = ValidOrNull(team.ShuttleAnchor) ?? ValidOrNull(team.Shuttle);
        var route = shuttleTarget is { Valid: true } target
            ? $"route={FormatEntityRef(target)}"
            : "route=shuttle-unassigned";
        var pressure = HasThreatPressure(team)
            ? "threat-side active"
            : HasRoutePressure(team)
                ? "route pressure active"
                : HasCrowdPressure(team)
                    ? "crowd pressure active"
                    : "corridor nominal";

        return $"evac-formation: patient={FormatEntityRef(patient)}; kostyl=patient-lead; " +
            $"tourniquet=corridor-control; zaslon=threat-side; {route}; pressure={pressure}";
    }

    private void UpdateEscortDuty(EntityUid uid, LuaMRescueEscortComponent escort, HTNComponent htn, bool forceSpeech = false)
    {
        SyncEscortContextFromLeader(escort);

        var candidateDuty = GetEscortDuty(uid, escort);
        var dutyChanged = UpdateEscortDutySelection(escort, candidateDuty, _timing.CurTime);
        var duty = escort.CurrentDuty;
        var followTarget = GetEscortFollowTarget(uid, escort, duty);
        var intentReplacement = IsEscortActivityIntentReplacement(
            uid,
            escort,
            duty,
            followTarget,
            out var primaryTarget);
        if (intentReplacement)
        {
            // GetEscortFollowTarget may have just completed an authoritative
            // route probe for the replacement. Shut down the old HTN executor,
            // but retain that new probe and cancel only the displaced target.
            CancelEscortIntent(uid, htn, primaryTarget, cancelRoute: false);
            if (escort.CurrentFollowTarget is { Valid: true } previousTarget &&
                previousTarget != followTarget)
            {
                _rescueNavigation.CancelRoute(uid, previousTarget);
            }
        }

        UpdateEscortActivityCarrier(uid, escort, duty, followTarget);
        ResolveEscortActivityExecution(
            uid,
            escort,
            duty,
            followTarget,
            out var executionDuty,
            out var executionTarget);
        var followChanged = escort.CurrentFollowTarget != executionTarget;

        if (!intentReplacement && followChanged)
        {
            var preservedPrimaryTarget = TryComp<LuaMRescueActivityCarrierComponent>(uid, out var carrier)
                ? IsEscortActivityTerminal(carrier.ActivityContext.TerminalStatus)
                    ? null
                    : carrier.ActivityContext.Target
                : primaryTarget;
            CancelEscortIntent(uid, htn, preservedPrimaryTarget, cancelRoute: false);
            if (escort.CurrentFollowTarget is { Valid: true } previousTarget &&
                previousTarget != executionTarget)
            {
                _rescueNavigation.CancelRoute(uid, previousTarget);
            }
        }

        escort.CurrentFollowTarget = executionTarget;

        var preserveClearRouteDropoff = executionDuty == LuaMRescueEscortDuty.ClearRoute &&
                                        IsEscortPullingRouteBlocker(uid, escort);
        if (!preserveClearRouteDropoff)
            SetEscortFollowTarget(uid, escort, htn, executionTarget, executionDuty);
        TryRunEscortDutyAction(uid, escort, htn, executionDuty, executionTarget);
        escort.LastDutyStatus = BuildEscortDutyStatus(escort, candidateDuty, executionTarget, _timing.CurTime);

        if ((dutyChanged || followChanged || forceSpeech) &&
            _timing.CurTime >= escort.NextSpeechTime)
        {
            TrySayDutyLine(uid, escort, executionDuty);
            escort.NextSpeechTime = _timing.CurTime + TimeSpan.FromSeconds(EscortSpeechCooldownSeconds);
        }

        Dirty(uid, escort);
    }

    private static bool UpdateEscortDutySelection(
        LuaMRescueEscortComponent escort,
        LuaMRescueEscortDuty candidateDuty,
        TimeSpan now)
    {
        if (candidateDuty == escort.CurrentDuty)
        {
            if (escort.PendingDuty != candidateDuty)
            {
                escort.PendingDuty = candidateDuty;
                escort.PendingDutySince = escort.DutyUpdatedAt;
            }

            return false;
        }

        if (escort.CurrentDuty == LuaMRescueEscortDuty.Standby ||
            IsUrgentEscortDuty(candidateDuty))
        {
            return CommitEscortDuty(escort, candidateDuty, now);
        }

        if (escort.PendingDuty != candidateDuty)
        {
            escort.PendingDuty = candidateDuty;
            escort.PendingDutySince = now;
            return false;
        }

        if ((now - escort.PendingDutySince).TotalSeconds < EscortDutyHoldSeconds)
            return false;

        return CommitEscortDuty(escort, candidateDuty, now);
    }

    private static bool CommitEscortDuty(
        LuaMRescueEscortComponent escort,
        LuaMRescueEscortDuty duty,
        TimeSpan now)
    {
        var changed = false;

        if (escort.CurrentDuty != duty)
        {
            escort.CurrentDuty = duty;
            escort.DutyUpdatedAt = now;
            escort.DutyTransitions++;
            changed = true;
        }

        if (escort.PendingDuty != duty)
        {
            escort.PendingDuty = duty;
            changed = true;
        }

        if (escort.PendingDutySince != now)
            escort.PendingDutySince = now;

        return changed;
    }

    private static bool IsUrgentEscortDuty(LuaMRescueEscortDuty duty)
    {
        return duty is LuaMRescueEscortDuty.Standby
            or LuaMRescueEscortDuty.ReturnToShuttle
            or LuaMRescueEscortDuty.ThreatScreen
            or LuaMRescueEscortDuty.ClearRoute;
    }

    private string BuildEscortDutyStatus(
        LuaMRescueEscortComponent escort,
        LuaMRescueEscortDuty candidateDuty,
        EntityUid? followTarget,
        TimeSpan now)
    {
        var dutyAge = GetEscortDutyAgeSeconds(escort);
        var status = $"{FormatRole(escort.Role)} plan={FormatPlan(escort.SortiePlan)} duty={FormatDuty(escort.CurrentDuty)} " +
            $"age={dutyAge}s transitions={escort.DutyTransitions} focus={FormatEntityRef(followTarget)} " +
            $"weapon={escort.LastWeaponReadinessStatus} action={escort.LastDutyActionStatus} " +
            $"crewHelp={escort.LastCrewHelpStatus} actions={escort.DutyActions}";

        if (candidateDuty != escort.CurrentDuty)
        {
            var pendingAge = GetElapsedSeconds(escort.PendingDutySince, now);
            status += $" holding candidate {FormatDuty(candidateDuty)} {pendingAge}s/{EscortDutyHoldSeconds:0}s";
        }

        return $"{status}; {escort.LastSceneStatus}; {escort.LastMemoryDigest}";
    }

    private bool IsEscortActivityIntentReplacement(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        LuaMRescueEscortDuty duty,
        EntityUid? followTarget,
        out EntityUid? primaryTarget)
    {
        if (!TryComp<LuaMRescueActivityCarrierComponent>(uid, out var carrier))
        {
            primaryTarget = ValidOrNull(followTarget);
            return true;
        }

        var role = GetActivityRole(escort.Role);
        var activity = GetEscortActivity(role, duty);
        if (!carrier.ActivityRoleProfile.Allows(activity))
            activity = LuaMRescueActivity.Observing;

        var routeTarget = ValidOrNull(followTarget);
        primaryTarget = GetEscortActivityTarget(uid, escort, activity, routeTarget);
        var current = carrier.ActivityContext;
        return carrier.ActivityRole != role ||
               carrier.SourceDuty != duty ||
               current.Activity != activity ||
               current.Target != primaryTarget;
    }

    private void CancelEscortIntent(
        EntityUid uid,
        HTNComponent htn,
        EntityUid? preservedPrimaryTarget,
        bool cancelRoute = true,
        bool replan = true)
    {
        _npc.SleepNPC(uid, htn);
        htn.PlanningToken?.Cancel();
        htn.PlanningToken = null;
        htn.PlanningJob = null;

        if (htn.Plan != null)
        {
            _htnSystem.ShutdownTask(htn.Plan.CurrentOperator, htn.Blackboard, HTNOperatorStatus.Failed);
            _htnSystem.ShutdownPlan(htn);
        }

        if (cancelRoute)
            _rescueNavigation.CancelRoute(uid);
        htn.Blackboard.Remove<EntityCoordinates>(NPCBlackboard.FollowTarget);
        htn.Blackboard.Remove<EntityUid>(NPCBlackboard.CurrentOrderedTarget);

        if (TryComp<PullerComponent>(uid, out var puller) &&
            puller.Pulling is { Valid: true } pulled &&
            pulled != preservedPrimaryTarget &&
            TryComp<PullableComponent>(pulled, out var pullable))
        {
            _pulling.TryStopPull(pulled, pullable, uid);
        }

        if (replan)
            _htnSystem.Replan(htn);
    }

    private void UpdateEscortActivityCarrier(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        LuaMRescueEscortDuty duty,
        EntityUid? followTarget)
    {
        var carrier = EnsureComp<LuaMRescueActivityCarrierComponent>(uid);
        var role = GetActivityRole(escort.Role);
        if (carrier.ActivityRole != role || carrier.ActivityRoleProfile.Role != role)
            carrier.ActivityRoleProfile = LuaMRescueRoleProfile.CreateDefault(role);

        var activity = GetEscortActivity(role, duty);
        if (!carrier.ActivityRoleProfile.Allows(activity))
            activity = LuaMRescueActivity.Observing;

        if (!carrier.ActivityRoleProfile.TryGetPolicy(activity, out var policy) ||
            policy.Timeout <= TimeSpan.Zero)
        {
            activity = LuaMRescueActivity.Observing;
            policy = carrier.ActivityRoleProfile.TryGetPolicy(activity, out var observingPolicy) &&
                     observingPolicy.Timeout > TimeSpan.Zero
                ? observingPolicy
                : LuaMRescueActivityPolicy.CreateDefault(activity);
        }

        var routeTarget = ValidOrNull(followTarget);
        var target = GetEscortActivityTarget(uid, escort, activity, routeTarget);
        var destination = routeTarget is { Valid: true } routeTargetUid
            ? new EntityCoordinates(routeTargetUid, GetEscortFormationOffset(uid, escort, duty, routeTargetUid))
            : (EntityCoordinates?) null;
        var current = carrier.ActivityContext;
        var fallback = ResolveEscortActivityFallback(escort, carrier.ActivityRoleProfile, policy.TimeoutFallback);
        var intentChanged = carrier.ActivityRole != role ||
                            carrier.SourceDuty != duty ||
                            current.Activity != activity ||
                            current.Target != target;
        var now = _timing.CurTime;

        if (intentChanged)
        {
            carrier.ActivityRole = role;
            carrier.SourceDuty = duty;
            carrier.IntentTransitions++;
            ResetEscortTerminalRecovery(carrier, "intent-changed");
            carrier.ActivityContext = new LuaMRescueActivityContext
            {
                Activity = activity,
                TerminalStatus = LuaMRescueTerminalStatus.Active,
                Target = target,
                Destination = destination,
                StartedAt = now,
                LastProgressAt = now,
                Generation = NextEscortActivityGeneration(current.Generation),
                Fallback = fallback,
                RouteStatus = routeTarget == null
                    ? LuaMRescueRouteStatus.Arrived
                    : LuaMRescueRouteStatus.Pending,
                LastTransitionAt = now,
                Deadline = AddEscortActivityTimeout(now, policy.Timeout),
            };
            current = carrier.ActivityContext;
        }
        else if (IsEscortActivityTerminal(current.TerminalStatus))
        {
            // A terminal activity stays immutable during fallback updates. Route failures get a
            // separate bounded reprobe budget; only a confirmed reachable route creates one fresh
            // generation and explicitly cancels the fallback HTN execution.
            if (TryRecoverTerminalEscortActivity(
                    uid,
                    escort,
                    carrier,
                    duty,
                    routeTarget,
                    destination,
                    policy,
                    fallback,
                    now))
            {
                UpdateEscortActivityTelemetry(uid, carrier, escort, routeTarget);
                return;
            }

            UpdateEscortActivityTelemetry(uid, carrier, escort, routeTarget);
            return;
        }
        else
        {
            current.Destination = destination;
            current.Fallback = fallback;
        }

        var previousRoute = current.RouteStatus;
        var wasTerminal = IsEscortActivityTerminal(current.TerminalStatus);
        if (routeTarget is { Valid: true } movementTarget)
        {
            var actionRange = GetEscortRouteProbeRange(uid, escort, duty, movementTarget);
            var route = _rescueNavigation.ProbeRoute(uid, movementTarget, actionRange);
            var distance = Math.Max(0f, route.Distance);
            current.RouteStatus = route.State switch
            {
                LuaMRescuePathProbeState.Pending => LuaMRescueRouteStatus.Planning,
                LuaMRescuePathProbeState.Reachable when distance <= actionRange => LuaMRescueRouteStatus.Arrived,
                LuaMRescuePathProbeState.Reachable => LuaMRescueRouteStatus.Moving,
                LuaMRescuePathProbeState.AccessDenied => LuaMRescueRouteStatus.Blocked,
                LuaMRescuePathProbeState.NoLineOfSight => LuaMRescueRouteStatus.Blocked,
                LuaMRescuePathProbeState.NoPath or
                    LuaMRescuePathProbeState.DifferentGrid or
                    LuaMRescuePathProbeState.Invalid => LuaMRescueRouteStatus.NoPath,
                _ => LuaMRescueRouteStatus.InvalidDestination,
            };

            var madeProgress = current.LastProgressDistance == null ||
                distance + Math.Max(0f, carrier.ActivityRoleProfile.ProgressTolerance) < current.LastProgressDistance.Value;
            var arrivedNow = current.RouteStatus == LuaMRescueRouteStatus.Arrived &&
                             previousRoute != LuaMRescueRouteStatus.Arrived;
            if (route.State == LuaMRescuePathProbeState.Reachable && (madeProgress || arrivedNow))
            {
                current.LastProgressAt = now;
                current.Attempts = 0;
                current.Blocked = false;
                current.FailureReason = LuaMRescueFailureReason.None;
                current.RetryNotBefore = TimeSpan.Zero;
                current.Deadline = AddEscortActivityTimeout(now, policy.Timeout);
            }
            else if (route.State == LuaMRescuePathProbeState.Reachable && current.Blocked)
            {
                // The route is usable again. Preserve the attempt history until actual progress,
                // but stop executing the fallback while the escort gets another chance to move.
                current.Blocked = false;
                current.FailureReason = LuaMRescueFailureReason.None;
                current.RetryNotBefore = TimeSpan.Zero;
            }

            current.LastProgressDistance = float.IsFinite(distance) ? distance : null;
            var routeFailure = route.State switch
            {
                LuaMRescuePathProbeState.AccessDenied => LuaMRescueFailureReason.AccessDenied,
                LuaMRescuePathProbeState.NoLineOfSight => LuaMRescueFailureReason.NoLineOfSight,
                LuaMRescuePathProbeState.NoPath or
                    LuaMRescuePathProbeState.DifferentGrid or
                    LuaMRescuePathProbeState.Invalid => LuaMRescueFailureReason.NoPath,
                _ => LuaMRescueFailureReason.None,
            };

            if (routeFailure != LuaMRescueFailureReason.None && now < current.Deadline)
            {
                RecordEscortRouteFailure(
                    carrier.ActivityRoleProfile,
                    current,
                    routeFailure,
                    fallback,
                    now);
            }
            else if (current.RouteStatus != LuaMRescueRouteStatus.Arrived && now >= current.Deadline)
            {
                SetEscortActivityTerminal(
                    current,
                    LuaMRescueTerminalStatus.Failed,
                    LuaMRescueFailureReason.DeadlineExceeded,
                    fallback,
                    now);
            }
        }
        else
        {
            current.RouteStatus = routeTarget == null
                ? LuaMRescueRouteStatus.Arrived
                : LuaMRescueRouteStatus.InvalidDestination;

            if (routeTarget == null)
            {
                current.LastProgressDistance = null;
                current.Blocked = false;
                current.FailureReason = LuaMRescueFailureReason.None;
                current.RetryNotBefore = TimeSpan.Zero;
            }
            else if (now >= current.Deadline)
            {
                SetEscortActivityTerminal(
                    current,
                    LuaMRescueTerminalStatus.Failed,
                    LuaMRescueFailureReason.DeadlineExceeded,
                    fallback,
                    now);
            }
            else
            {
                RecordEscortRouteFailure(
                    carrier.ActivityRoleProfile,
                    current,
                    LuaMRescueFailureReason.NoPath,
                    fallback,
                    now);
            }
        }

        if (!intentChanged && previousRoute != current.RouteStatus)
            current.LastTransitionAt = now;

        if (current.TerminalStatus == LuaMRescueTerminalStatus.Active &&
            now >= current.Deadline &&
            (current.RouteStatus != LuaMRescueRouteStatus.Arrived || EscortDutyRequiresActionCompletion(duty)))
        {
            SetEscortActivityTerminal(
                current,
                LuaMRescueTerminalStatus.Failed,
                LuaMRescueFailureReason.DeadlineExceeded,
                fallback,
                now);
        }

        if (!wasTerminal && IsEscortActivityTerminal(current.TerminalStatus))
        {
            ArmEscortTerminalRecovery(uid, carrier, current, routeTarget, now);
            ReportEscortActivityTerminal(uid, escort, current);
        }

        UpdateEscortActivityTelemetry(uid, carrier, escort, routeTarget);
    }

    private void ResolveEscortActivityExecution(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        LuaMRescueEscortDuty requestedDuty,
        EntityUid? requestedTarget,
        out LuaMRescueEscortDuty executionDuty,
        out EntityUid? executionTarget)
    {
        executionDuty = requestedDuty;
        executionTarget = requestedTarget;
        if (!TryComp<LuaMRescueActivityCarrierComponent>(uid, out var carrier))
            return;

        var current = carrier.ActivityContext;
        if (!current.Blocked && !IsEscortActivityTerminal(current.TerminalStatus))
            return;

        if (current.Fallback == LuaMRescueActivity.Returning)
        {
            var returnTarget = GetEscortFollowTarget(uid, escort, LuaMRescueEscortDuty.ReturnToShuttle);
            if (returnTarget is { Valid: true })
            {
                executionDuty = LuaMRescueEscortDuty.ReturnToShuttle;
                executionTarget = returnTarget;
                return;
            }
        }

        executionDuty = LuaMRescueEscortDuty.Standby;
        executionTarget = GetEscortFollowTarget(uid, escort, LuaMRescueEscortDuty.Standby);
    }

    private LuaMRescueActivity ResolveEscortActivityFallback(
        LuaMRescueEscortComponent escort,
        LuaMRescueRoleProfile profile,
        LuaMRescueActivity requestedFallback)
    {
        var hasShuttle = ValidOrNull(escort.ShuttleAnchor) != null || ValidOrNull(escort.Shuttle) != null;
        if (hasShuttle &&
            profile.Allows(LuaMRescueActivity.Returning) &&
            requestedFallback is not LuaMRescueActivity.Observing)
        {
            return LuaMRescueActivity.Returning;
        }

        if (profile.Allows(LuaMRescueActivity.Observing))
            return LuaMRescueActivity.Observing;

        return hasShuttle && profile.Allows(LuaMRescueActivity.Returning)
            ? LuaMRescueActivity.Returning
            : LuaMRescueActivity.None;
    }

    private void ArmEscortTerminalRecovery(
        EntityUid uid,
        LuaMRescueActivityCarrierComponent carrier,
        LuaMRescueActivityContext current,
        EntityUid? routeTarget,
        TimeSpan now)
    {
        carrier.TerminalRecoveryEvaluated = true;
        carrier.TerminalRecoveryArmed = IsRecoverableEscortRouteTerminal(current, routeTarget);
        carrier.TerminalRecoveryDormant = false;
        carrier.TerminalRecoveryAttempts = 0;
        carrier.TerminalRecoveryProbeInFlight = false;
        carrier.TerminalRecoveryProbeStartedAt = TimeSpan.Zero;

        if (!carrier.TerminalRecoveryArmed)
        {
            carrier.NextTerminalRecoveryAt = TimeSpan.Zero;
            carrier.LastTerminalRecoveryStatus = "not-recoverable";
            return;
        }

        carrier.NextTerminalRecoveryAt = now + CalculateEscortTerminalRecoveryBackoff(
            carrier.ActivityRoleProfile,
            nextAttempt: 1);
        carrier.LastTerminalRecoveryStatus =
            $"scheduled attempt=1/{Math.Max(1, carrier.ActivityRoleProfile.MaxAttempts)} at " +
            $"{carrier.NextTerminalRecoveryAt.TotalSeconds:0.0}s";
        _rescueNavigation.CancelRoute(uid, routeTarget);
    }

    private bool TryRecoverTerminalEscortActivity(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        LuaMRescueActivityCarrierComponent carrier,
        LuaMRescueEscortDuty duty,
        EntityUid? routeTarget,
        EntityCoordinates? destination,
        LuaMRescueActivityPolicy policy,
        LuaMRescueActivity fallback,
        TimeSpan now)
    {
        var current = carrier.ActivityContext;
        if (!carrier.TerminalRecoveryEvaluated)
            ArmEscortTerminalRecovery(uid, carrier, current, routeTarget, now);

        var maxAttempts = Math.Max(1, carrier.ActivityRoleProfile.MaxAttempts);
        if (!carrier.TerminalRecoveryArmed ||
            now < carrier.NextTerminalRecoveryAt)
        {
            return false;
        }

        if (routeTarget is not { Valid: true } target || Deleted(target))
        {
            carrier.TerminalRecoveryArmed = false;
            carrier.TerminalRecoveryDormant = false;
            carrier.TerminalRecoveryProbeInFlight = false;
            carrier.NextTerminalRecoveryAt = TimeSpan.Zero;
            carrier.LastTerminalRecoveryStatus = "cancelled: target-lost";
            return false;
        }

        if (!carrier.TerminalRecoveryProbeInFlight)
        {
            if (!carrier.TerminalRecoveryDormant)
            {
                if (carrier.TerminalRecoveryAttempts >= maxAttempts)
                    return false;

                carrier.TerminalRecoveryAttempts++;
            }

            carrier.TerminalRecoveryProbeInFlight = true;
            carrier.TerminalRecoveryProbeStartedAt = now;
        }

        var actionRange = GetEscortRouteProbeRange(uid, escort, duty, target);
        var route = _rescueNavigation.ProbeRoute(uid, target, actionRange);
        if (route.State == LuaMRescuePathProbeState.Pending)
        {
            if (now - carrier.TerminalRecoveryProbeStartedAt >=
                TimeSpan.FromSeconds(EscortTerminalRecoveryProbeTimeoutSeconds))
            {
                FinishEscortTerminalRecoveryFailure(
                    uid,
                    escort,
                    carrier,
                    current,
                    target,
                    LuaMRescueRouteStatus.NoPath,
                    LuaMRescueFailureReason.NoPath,
                    now);
            }
            else
            {
                carrier.NextTerminalRecoveryAt = now +
                    TimeSpan.FromSeconds(EscortTerminalRecoveryPollSeconds);
                carrier.LastTerminalRecoveryStatus =
                    $"probing attempt={carrier.TerminalRecoveryAttempts}/{maxAttempts}";
            }

            return false;
        }

        var distance = Math.Max(0f, route.Distance);
        if (route.State == LuaMRescuePathProbeState.Reachable)
        {
            var recoveryAttempt = carrier.TerminalRecoveryAttempts;
            if (TryComp<HTNComponent>(uid, out var htn))
                CancelEscortIntent(uid, htn, current.Target, cancelRoute: false);

            carrier.IntentTransitions++;
            current.TerminalStatus = LuaMRescueTerminalStatus.Active;
            current.Blocked = false;
            current.FailureReason = LuaMRescueFailureReason.None;
            current.RetryNotBefore = TimeSpan.Zero;
            current.Attempts = 0;
            current.Generation = NextEscortActivityGeneration(current.Generation);
            current.Destination = destination;
            current.RouteStatus = distance <= actionRange
                ? LuaMRescueRouteStatus.Arrived
                : LuaMRescueRouteStatus.Moving;
            current.StartedAt = now;
            current.LastProgressAt = now;
            current.LastProgressDistance = float.IsFinite(distance) ? distance : null;
            current.LastTransitionAt = now;
            current.Deadline = AddEscortActivityTimeout(now, policy.Timeout);
            current.Fallback = fallback;
            current.DoAfterStatus = LuaMRescueDoAfterStatus.None;

            ResetEscortTerminalRecovery(
                carrier,
                $"recovered attempt={recoveryAttempt}/{maxAttempts}; generation={current.Generation}");
            return true;
        }

        var routeStatus = route.State is LuaMRescuePathProbeState.AccessDenied or
            LuaMRescuePathProbeState.NoLineOfSight
            ? LuaMRescueRouteStatus.Blocked
            : LuaMRescueRouteStatus.NoPath;
        var failure = route.State switch
        {
            LuaMRescuePathProbeState.AccessDenied => LuaMRescueFailureReason.AccessDenied,
            LuaMRescuePathProbeState.NoLineOfSight => LuaMRescueFailureReason.NoLineOfSight,
            _ => LuaMRescueFailureReason.NoPath,
        };
        FinishEscortTerminalRecoveryFailure(
            uid,
            escort,
            carrier,
            current,
            target,
            routeStatus,
            failure,
            now);
        return false;
    }

    private void FinishEscortTerminalRecoveryFailure(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        LuaMRescueActivityCarrierComponent carrier,
        LuaMRescueActivityContext current,
        EntityUid routeTarget,
        LuaMRescueRouteStatus routeStatus,
        LuaMRescueFailureReason failure,
        TimeSpan now)
    {
        carrier.TerminalRecoveryProbeInFlight = false;
        carrier.TerminalRecoveryProbeStartedAt = TimeSpan.Zero;
        current.RouteStatus = routeStatus;
        current.FailureReason = failure;
        current.Blocked = true;
        current.LastTransitionAt = now;

        var maxAttempts = Math.Max(1, carrier.ActivityRoleProfile.MaxAttempts);
        if (carrier.TerminalRecoveryDormant || carrier.TerminalRecoveryAttempts >= maxAttempts)
        {
            var enteringDormant = !carrier.TerminalRecoveryDormant;
            carrier.TerminalRecoveryDormant = true;
            carrier.NextTerminalRecoveryAt = now +
                TimeSpan.FromSeconds(EscortTerminalDormantObservationSeconds);
            carrier.LastTerminalRecoveryStatus =
                $"dormant attempts={carrier.TerminalRecoveryAttempts}/{maxAttempts}; reason={failure}; " +
                $"observeAt={carrier.NextTerminalRecoveryAt.TotalSeconds:0.0}s";
            _rescueNavigation.CancelRoute(uid, routeTarget);
            if (enteringDormant)
            {
                TryRequestCrewHelp(
                    uid,
                    escort,
                    $"activity-recovery-exhausted:{current.Generation}:{failure}",
                    $"escort route recovery exhausted: {failure}; fallback={current.Fallback}",
                    "Маршрут всё ещё заблокирован после повторной проверки. Нужна помощь экипажа.");
            }

            return;
        }

        carrier.NextTerminalRecoveryAt = now + CalculateEscortTerminalRecoveryBackoff(
            carrier.ActivityRoleProfile,
            carrier.TerminalRecoveryAttempts + 1);
        carrier.LastTerminalRecoveryStatus =
            $"failed attempt={carrier.TerminalRecoveryAttempts}/{maxAttempts}; reason={failure}; " +
            $"retryAt={carrier.NextTerminalRecoveryAt.TotalSeconds:0.0}s";
        _rescueNavigation.CancelRoute(uid, routeTarget);
    }

    private static bool IsRecoverableEscortRouteTerminal(
        LuaMRescueActivityContext current,
        EntityUid? routeTarget)
    {
        if (routeTarget is not { Valid: true } ||
            current.TerminalStatus is not (LuaMRescueTerminalStatus.Blocked or LuaMRescueTerminalStatus.Failed))
        {
            return false;
        }

        if (current.FailureReason is LuaMRescueFailureReason.NoPath or
            LuaMRescueFailureReason.AccessDenied or
            LuaMRescueFailureReason.NoLineOfSight or
            LuaMRescueFailureReason.RouteBlocked)
        {
            return true;
        }

        return current.FailureReason == LuaMRescueFailureReason.DeadlineExceeded &&
               current.RouteStatus != LuaMRescueRouteStatus.Arrived;
    }

    private static TimeSpan CalculateEscortTerminalRecoveryBackoff(
        LuaMRescueRoleProfile profile,
        int nextAttempt)
    {
        var configured = CalculateEscortActivityBackoff(profile, Math.Max(1, nextAttempt));
        var minimum = TimeSpan.FromSeconds(EscortTerminalRecoveryPollSeconds);
        return configured < minimum ? minimum : configured;
    }

    private static void ResetEscortTerminalRecovery(
        LuaMRescueActivityCarrierComponent carrier,
        string status)
    {
        carrier.TerminalRecoveryEvaluated = false;
        carrier.TerminalRecoveryArmed = false;
        carrier.TerminalRecoveryDormant = false;
        carrier.TerminalRecoveryAttempts = 0;
        carrier.NextTerminalRecoveryAt = TimeSpan.Zero;
        carrier.TerminalRecoveryProbeInFlight = false;
        carrier.TerminalRecoveryProbeStartedAt = TimeSpan.Zero;
        carrier.LastTerminalRecoveryStatus = status;
    }

    private static void RecordEscortRouteFailure(
        LuaMRescueRoleProfile profile,
        LuaMRescueActivityContext current,
        LuaMRescueFailureReason reason,
        LuaMRescueActivity fallback,
        TimeSpan now)
    {
        if (current.RetryNotBefore > now)
            return;

        current.Attempts++;
        current.Blocked = true;
        current.FailureReason = reason;
        current.LastTransitionAt = now;

        if (current.Attempts >= Math.Max(1, profile.MaxAttempts))
        {
            SetEscortActivityTerminal(
                current,
                LuaMRescueTerminalStatus.Blocked,
                reason,
                fallback,
                now);
            return;
        }

        current.RetryNotBefore = now + CalculateEscortActivityBackoff(profile, current.Attempts);
    }

    private void ReportEscortActivityTerminal(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        LuaMRescueActivityContext current)
    {
        if (TryComp<PullerComponent>(uid, out var puller) &&
            puller.Pulling is { Valid: true } pulled &&
            TryComp<PullableComponent>(pulled, out var pullable) &&
            !_pulling.TryStopPull(pulled, pullable, uid))
        {
            escort.LastDutyActionStatus =
                $"terminal handoff could not release {FormatEntityRef(pulled)}; reason={current.FailureReason}";
        }

        var reason = current.FailureReason;
        TryRequestCrewHelp(
            uid,
            escort,
            $"activity-terminal:{current.Generation}:{reason}",
            $"escort activity {current.Activity} blocked: {reason}; fallback={current.Fallback}",
            reason == LuaMRescueFailureReason.AccessDenied
                ? "Нужен сотрудник с доступом. Маршрут спасательной группы закрыт."
                : "Маршрут спасательной группы заблокирован. Нужна помощь с безопасным обходом.");
    }

    private float GetEscortActivityActionRange(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        LuaMRescueEscortDuty duty)
    {
        var fallback = duty switch
        {
            LuaMRescueEscortDuty.ClearRoute => EscortDutyActionRange,
            LuaMRescueEscortDuty.PatientSupport => EscortPatientAssistRange,
            LuaMRescueEscortDuty.CrowdControl => EscortCrowdControlRange,
            LuaMRescueEscortDuty.ThreatScreen => Math.Min(EscortThreatScreenRange, EscortThreatLeashRange),
            LuaMRescueEscortDuty.SecureScene => 5.5f,
            LuaMRescueEscortDuty.EvacuationCorridor => 6f,
            LuaMRescueEscortDuty.ReturnToShuttle => 5f,
            LuaMRescueEscortDuty.Standby => Math.Max(0.05f, escort.FollowRange),
            _ => EscortActivityArrivalRange,
        };

        // Carrier formation arrival ranges are duty-specific (secure scene and
        // evacuation corridor intentionally differ although both map to
        // Protecting). Direct actions are profile-driven and therefore reusable
        // by future escort professions without another monolithic AI branch.
        if (duty is LuaMRescueEscortDuty.SecureScene or
            LuaMRescueEscortDuty.EvacuationCorridor or
            LuaMRescueEscortDuty.Standby)
        {
            return fallback;
        }

        var profile = GetEscortRoleProfile(uid, escort);
        var activity = GetEscortActivity(profile.Role, duty);
        return profile.GetActionRange(activity, fallback);
    }

    private float GetEscortRouteProbeRange(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        LuaMRescueEscortDuty duty,
        EntityUid target)
    {
        return duty == LuaMRescueEscortDuty.ReturnToShuttle &&
               HasComp<GatewayComponent>(target)
            ? SharedInteractionSystem.InteractionRange
            : GetEscortActivityActionRange(uid, escort, duty);
    }

    private static bool EscortDutyRequiresActionCompletion(LuaMRescueEscortDuty duty)
    {
        return duty is LuaMRescueEscortDuty.PatientSupport
            or LuaMRescueEscortDuty.ClearRoute
            or LuaMRescueEscortDuty.CrowdControl
            or LuaMRescueEscortDuty.ThreatScreen;
    }

    private static void SetEscortActivityTerminal(
        LuaMRescueActivityContext current,
        LuaMRescueTerminalStatus terminalStatus,
        LuaMRescueFailureReason failureReason,
        LuaMRescueActivity fallback,
        TimeSpan now)
    {
        current.TerminalStatus = terminalStatus;
        current.Blocked = terminalStatus == LuaMRescueTerminalStatus.Blocked;
        current.FailureReason = failureReason;
        current.Fallback = fallback;
        current.RetryNotBefore = TimeSpan.Zero;
        current.LastTransitionAt = now;
    }

    private static TimeSpan CalculateEscortActivityBackoff(LuaMRescueRoleProfile profile, int attempts)
    {
        var exponent = Math.Clamp(attempts - 1, 0, 20);
        var multiplier = 1L << exponent;
        var baseTicks = Math.Max(0L, profile.BaseRetryBackoff.Ticks);
        var maxTicks = Math.Max(baseTicks, profile.MaxRetryBackoff.Ticks);
        var scaledTicks = baseTicks > long.MaxValue / multiplier
            ? long.MaxValue
            : baseTicks * multiplier;
        return TimeSpan.FromTicks(Math.Min(maxTicks, scaledTicks));
    }

    private static TimeSpan AddEscortActivityTimeout(TimeSpan now, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            return now;

        return now.Ticks > TimeSpan.MaxValue.Ticks - timeout.Ticks
            ? TimeSpan.MaxValue
            : now + timeout;
    }

    private static bool IsEscortActivityTerminal(LuaMRescueTerminalStatus status)
    {
        return status is LuaMRescueTerminalStatus.Blocked
            or LuaMRescueTerminalStatus.Succeeded
            or LuaMRescueTerminalStatus.Failed
            or LuaMRescueTerminalStatus.Cancelled;
    }

    private void UpdateEscortActivityTelemetry(
        EntityUid uid,
        LuaMRescueActivityCarrierComponent carrier,
        LuaMRescueEscortComponent escort,
        EntityUid? routeTarget)
    {
        var current = carrier.ActivityContext;
        var status =
            $"role={FormatActivityRole(carrier.ActivityRole)}; sourceDuty={FormatDuty(carrier.SourceDuty)}; " +
            $"activity={FormatEscortActivity(current.Activity)}; terminal={current.TerminalStatus}; " +
            $"generation={current.Generation}; target={FormatEntityRef(current.Target)}; " +
            $"destination={FormatEntityRef(routeTarget)}; route={current.RouteStatus}; " +
            $"distance={(current.LastProgressDistance is { } remaining ? $"{remaining:0.0}" : "none")}; " +
            $"attempts={current.Attempts}/{Math.Max(1, carrier.ActivityRoleProfile.MaxAttempts)}; " +
            $"blocked={current.Blocked}; failure={current.FailureReason}; fallback={FormatEscortActivity(current.Fallback)}; " +
            $"retryAt={current.RetryNotBefore.TotalSeconds:0.0}s; deadline={current.Deadline.TotalSeconds:0.0}s; " +
            $"recovery={carrier.TerminalRecoveryAttempts}/{Math.Max(1, carrier.ActivityRoleProfile.MaxAttempts)}; " +
            $"recoveryDormant={carrier.TerminalRecoveryDormant}; " +
            $"recoveryAt={carrier.NextTerminalRecoveryAt.TotalSeconds:0.0}s; " +
            $"recoveryStatus={carrier.LastTerminalRecoveryStatus}; " +
            $"lastProgress={current.LastProgressAt.TotalSeconds:0.0}s; " +
            $"startedAt={current.StartedAt.TotalSeconds:0.0}s; transitions={carrier.IntentTransitions}";
        if (string.Equals(carrier.LastStatus, status, StringComparison.Ordinal))
            return;

        carrier.LastStatus = status;
        Dirty(uid, carrier);
    }

    private bool UpdateEscortActivityDigest(LuaMRescueTeamComponent team)
    {
        var entries = new List<string>();
        foreach (var escortUid in team.Escorts)
        {
            if (!escortUid.Valid || Deleted(escortUid) ||
                !TryComp<LuaMRescueActivityCarrierComponent>(escortUid, out var carrier))
            {
                continue;
            }

            entries.Add(
                $"{FormatActivityRole(carrier.ActivityRole)}:{FormatDuty(carrier.SourceDuty)}:" +
                $"{FormatEscortActivity(carrier.ActivityContext.Activity)}" +
                $"@g{carrier.ActivityContext.Generation}:{carrier.ActivityContext.RouteStatus}:" +
                $"{carrier.ActivityContext.TerminalStatus}:{carrier.ActivityContext.FailureReason}:" +
                $"fallback={FormatEscortActivity(carrier.ActivityContext.Fallback)}:" +
                $"recovery={carrier.TerminalRecoveryAttempts}/{Math.Max(1, carrier.ActivityRoleProfile.MaxAttempts)}:" +
                $"dormant={carrier.TerminalRecoveryDormant}");
        }

        var digest = entries.Count == 0
            ? "none"
            : $"[{string.Join('|', entries)}]";
        if (string.Equals(team.LastEscortActivityDigest, digest, StringComparison.Ordinal))
            return false;

        team.LastEscortActivityDigest = digest;
        return true;
    }

    private string BuildEscortActivityTelemetry(LuaMRescueActivityCarrierComponent carrier)
    {
        var context = carrier.ActivityContext;
        return $"activityRole={FormatActivityRole(carrier.ActivityRole)}; " +
               $"activity={FormatEscortActivity(context.Activity)}; activityDuty={FormatDuty(carrier.SourceDuty)}; " +
               $"activityTerminal={context.TerminalStatus}; activityGeneration={context.Generation}; " +
               $"activityTarget={FormatEntityRef(context.Target)}; activityRoute={context.RouteStatus}; " +
               $"activityAttempts={context.Attempts}/{Math.Max(1, carrier.ActivityRoleProfile.MaxAttempts)}; " +
               $"activityFailure={context.FailureReason}; activityFallback={FormatEscortActivity(context.Fallback)}; " +
               $"activityRetryAt={context.RetryNotBefore.TotalSeconds:0.0}s; " +
               $"activityRecovery={carrier.TerminalRecoveryAttempts}/{Math.Max(1, carrier.ActivityRoleProfile.MaxAttempts)}; " +
               $"activityRecoveryDormant={carrier.TerminalRecoveryDormant}; " +
               $"activityRecoveryAt={carrier.NextTerminalRecoveryAt.TotalSeconds:0.0}s; " +
               $"activityDeadline={context.Deadline.TotalSeconds:0.0}s; " +
               $"activityLastProgress={context.LastProgressAt.TotalSeconds:0.0}s; " +
               $"activityStartedAt={context.StartedAt.TotalSeconds:0.0}s; " +
               $"activityTransitions={carrier.IntentTransitions}; activityStatus={carrier.LastStatus}";
    }

    private static LuaMRescueRole GetActivityRole(LuaMRescueEscortRole role)
    {
        return role switch
        {
            LuaMRescueEscortRole.Tourniquet => LuaMRescueRole.Tourniquet,
            LuaMRescueEscortRole.Kostyl => LuaMRescueRole.Kostyl,
            LuaMRescueEscortRole.Zaslon => LuaMRescueRole.Zaslon,
            _ => LuaMRescueRole.None,
        };
    }

    private LuaMRescueRoleProfile GetEscortRoleProfile(
        EntityUid uid,
        LuaMRescueEscortComponent escort)
    {
        var carrier = EnsureComp<LuaMRescueActivityCarrierComponent>(uid);
        var role = GetActivityRole(escort.Role);
        if (carrier.ActivityRoleProfile.Role != role)
            carrier.ActivityRoleProfile = LuaMRescueRoleProfile.CreateDefault(role);
        return carrier.ActivityRoleProfile;
    }

    private float GetObservationRange(EntityUid observer)
    {
        if (TryComp<LuaMRescueAgentComponent>(observer, out var rescue))
        {
            var role = rescue.ActivityRole == LuaMRescueRole.None
                ? LuaMRescueRole.Aibolit
                : rescue.ActivityRole;
            if (rescue.ActivityRoleProfile.Role != role)
                rescue.ActivityRoleProfile = LuaMRescueRoleProfile.CreateDefault(role);
            return Math.Max(0.05f, rescue.ActivityRoleProfile.ThreatPolicy.ObservationRange);
        }

        if (TryComp<LuaMRescueEscortComponent>(observer, out var escort))
            return Math.Max(0.05f, GetEscortRoleProfile(observer, escort).ThreatPolicy.ObservationRange);

        return SceneScanRange;
    }

    private static LuaMRescueActivity GetEscortActivity(LuaMRescueRole role, LuaMRescueEscortDuty duty)
    {
        return (role, duty) switch
        {
            (_, LuaMRescueEscortDuty.Standby) => LuaMRescueActivity.Observing,
            (_, LuaMRescueEscortDuty.ReturnToShuttle) => LuaMRescueActivity.Returning,
            (LuaMRescueRole.Zaslon, LuaMRescueEscortDuty.ThreatScreen) => LuaMRescueActivity.ThreatScreen,
            (LuaMRescueRole.Tourniquet, LuaMRescueEscortDuty.CrowdControl) => LuaMRescueActivity.CrowdControl,
            (LuaMRescueRole.Tourniquet or LuaMRescueRole.Zaslon, LuaMRescueEscortDuty.ClearRoute) =>
                LuaMRescueActivity.ClearRoute,
            (LuaMRescueRole.Kostyl, LuaMRescueEscortDuty.PatientSupport or LuaMRescueEscortDuty.EvacuationCorridor) =>
                LuaMRescueActivity.PreparingEvacuation,
            (LuaMRescueRole.Tourniquet, LuaMRescueEscortDuty.EvacuationCorridor) => LuaMRescueActivity.ClearRoute,
            (LuaMRescueRole.Zaslon, LuaMRescueEscortDuty.EvacuationCorridor) => LuaMRescueActivity.Protecting,
            (LuaMRescueRole.Tourniquet or LuaMRescueRole.Zaslon, LuaMRescueEscortDuty.SecureScene) =>
                LuaMRescueActivity.Protecting,
            (LuaMRescueRole.Tourniquet or LuaMRescueRole.Zaslon, LuaMRescueEscortDuty.PatientSupport) =>
                LuaMRescueActivity.Protecting,
            _ => LuaMRescueActivity.Observing,
        };
    }

    private EntityUid? GetEscortActivityTarget(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        LuaMRescueActivity activity,
        EntityUid? routeTarget)
    {
        switch (activity)
        {
            case LuaMRescueActivity.ThreatScreen:
                if (ValidOrNull(escort.ThreatTarget) is { Valid: true } threat &&
                    IsThreatWithinRescueLeash(uid, escort, threat))
                {
                    return threat;
                }
                break;
            case LuaMRescueActivity.CrowdControl:
                return ValidOrNull(escort.CrowdTarget) ?? routeTarget;
            case LuaMRescueActivity.ClearRoute:
                return ValidOrNull(escort.RouteBlockerTarget) ?? routeTarget;
            case LuaMRescueActivity.PreparingEvacuation:
                return GetEligibleEscortPatient(uid, escort.Patient) ?? routeTarget;
            case LuaMRescueActivity.Returning:
                return ValidOrNull(escort.ShuttleAnchor) ?? ValidOrNull(escort.Shuttle) ?? routeTarget;
        }

        return TryGetEscortFormationAnchor(escort, out var formationAnchor)
            ? formationAnchor
            : routeTarget;
    }

    private static uint NextEscortActivityGeneration(uint generation)
    {
        var next = unchecked(generation + 1);
        return next == 0 ? 1u : next;
    }

    private static string FormatActivityRole(LuaMRescueRole role)
    {
        return role switch
        {
            LuaMRescueRole.Tourniquet => "tourniquet",
            LuaMRescueRole.Kostyl => "kostyl",
            LuaMRescueRole.Zaslon => "zaslon",
            LuaMRescueRole.Aibolit => "aibolit",
            _ => "none",
        };
    }

    private static string FormatEscortActivity(LuaMRescueActivity activity)
    {
        return activity switch
        {
            LuaMRescueActivity.Observing => "formation",
            LuaMRescueActivity.Protecting => "protection",
            LuaMRescueActivity.ThreatScreen => "threat-screen",
            LuaMRescueActivity.CrowdControl => "crowd-control",
            LuaMRescueActivity.ClearRoute => "clear-route",
            LuaMRescueActivity.PreparingEvacuation => "evacuation-assist",
            LuaMRescueActivity.Returning => "return",
            LuaMRescueActivity.Approaching => "approach",
            _ => activity.ToString().ToLowerInvariant(),
        };
    }

    private bool TryConfirmTriageCover(
        LuaMRescueTeamComponent team,
        LuaMRescueAgentComponent rescue,
        EntityUid? patient)
    {
        if (patient is not { Valid: true } patientUid ||
            Deleted(patientUid) ||
            rescue.TriageReportedTarget != patientUid ||
            string.IsNullOrWhiteSpace(rescue.LastTriageDecisionKey) ||
            rescue.LastTriageDecisionKey == "none")
        {
            return ClearTriageCoverConfirmation(team);
        }

        var decisionKey = rescue.LastTriageDecisionKey;
        if (team.TriageCoverConfirmedPatient == patientUid &&
            string.Equals(team.LastTriageCoverDecisionKey, decisionKey, StringComparison.Ordinal))
        {
            return false;
        }

        if (_timing.CurTime < team.NextTriageCoverConfirmAt &&
            team.LastTriageCoverStatus.StartsWith("triage-cover: waiting", StringComparison.Ordinal))
        {
            return false;
        }

        var role = SelectTriageCoverRole(team, decisionKey);
        if (!TryFindEscortByRole(team, role, out var speaker, out var escort) &&
            !TryFindAnyEscort(team, out speaker, out escort))
        {
            team.LastTriageCoverDecisionKey = decisionKey;
            team.NextTriageCoverConfirmAt = _timing.CurTime + TimeSpan.FromSeconds(TriageCoverConfirmCooldownSeconds);
            return SetTriageCoverStatus(team, $"triage-cover: waiting for escort; decision={decisionKey}");
        }

        var line = BuildTriageCoverLine(escort.Role, decisionKey);
        if (!string.IsNullOrWhiteSpace(line))
        {
            var now = _timing.CurTime;
            var reserved = TryReserveTeamSpeech(
                speaker,
                team,
                $"triage-cover:{decisionKey}",
                now,
                out var speechStatusChanged,
                line);
            if (!reserved)
            {
                return SetTriageCoverStatus(team, $"triage-cover: waiting shared speech; decision={decisionKey}") ||
                       speechStatusChanged;
            }

            _chat.TrySendInGameICMessage(speaker, line, InGameICChatType.Speak, hideChat: false, hideLog: true);
        }

        escort.LastDutyActionStatus = $"triage-cover:{decisionKey} confirming {FormatRole(escort.Role)}";
        escort.DutyActions++;
        escort.NextSpeechTime = _timing.CurTime + TimeSpan.FromSeconds(EscortSpeechCooldownSeconds);
        Dirty(speaker, escort);

        team.TriageCoverConfirmedPatient = patientUid;
        team.LastTriageCoverDecisionKey = decisionKey;
        team.NextTriageCoverConfirmAt = _timing.CurTime + TimeSpan.FromSeconds(TriageCoverConfirmCooldownSeconds);

        return SetTriageCoverStatus(
            team,
            $"triage-cover: role={FormatRole(escort.Role)}; decision={decisionKey}; scene={team.LastSceneStatus}");
    }

    private static bool ClearTriageCoverConfirmation(LuaMRescueTeamComponent team)
    {
        if (team.TriageCoverConfirmedPatient == null &&
            team.LastTriageCoverDecisionKey == "none" &&
            team.LastTriageCoverStatus == "none")
        {
            return false;
        }

        team.TriageCoverConfirmedPatient = null;
        team.LastTriageCoverDecisionKey = "none";
        team.LastTriageCoverStatus = "none";
        return true;
    }

    private static bool SetTriageCoverStatus(LuaMRescueTeamComponent team, string status)
    {
        if (string.Equals(team.LastTriageCoverStatus, status, StringComparison.Ordinal))
            return false;

        team.LastTriageCoverStatus = status;
        return true;
    }

    private bool TrySayTeamPhaseLine(
        EntityUid leader,
        LuaMRescueTeamComponent team,
        LuaMRescueTeamPhase phase,
        bool somberScene,
        string returnOrExtractReason)
    {
        if (team.LastAnnouncedPhase == phase &&
            team.LastAnnouncedSomberScene == somberScene)
        {
            return false;
        }

        var now = _timing.CurTime;
        if (now < team.NextPhaseAnnouncementAt)
        {
            var wait = Math.Max(0, (int) Math.Ceiling((team.NextPhaseAnnouncementAt - now).TotalSeconds));
            return SetTeamPhaseAnnouncementStatus(team, $"phase-bark waiting: {FormatPhase(phase)} in {wait}s; somber={somberScene}");
        }

        if (!TryBuildTeamPhaseLine(phase, somberScene, returnOrExtractReason, out var preferredRole, out var line))
        {
            team.LastAnnouncedPhase = phase;
            team.LastAnnouncedSomberScene = somberScene;
            team.NextPhaseAnnouncementAt = now;
            return SetTeamPhaseAnnouncementStatus(team, $"phase-bark skipped: {FormatPhase(phase)}; somber={somberScene}");
        }

        var speaker = leader;
        var speakerLabel = "aibolit";
        if (preferredRole is { } role &&
            TryFindEscortByRole(team, role, out var escortUid, out var escort))
        {
            speaker = escortUid;
            speakerLabel = FormatRole(escort.Role);
        }

        if (!TryReserveTeamSpeech(
                speaker,
                team,
                $"phase:{FormatPhase(phase)}",
                now,
                out var speechStatusChanged,
                line))
        {
            return SetTeamPhaseAnnouncementStatus(
                       team,
                       $"phase-bark waiting shared speech: {FormatPhase(phase)}; somber={somberScene}") ||
                   speechStatusChanged;
        }

        if (!Deleted(speaker))
            _chat.TrySendInGameICMessage(speaker, line, InGameICChatType.Speak, hideChat: false, hideLog: true);

        team.LastAnnouncedPhase = phase;
        team.LastAnnouncedSomberScene = somberScene;
        team.NextPhaseAnnouncementAt = now + TimeSpan.FromSeconds(TeamPhaseAnnouncementCooldownSeconds);
        return SetTeamPhaseAnnouncementStatus(team, $"phase-bark:{FormatPhase(phase)} speaker={speakerLabel}; somber={somberScene}") ||
               speechStatusChanged;
    }

    private bool TryReserveTeamSpeech(
        EntityUid speaker,
        LuaMRescueTeamComponent team,
        string key,
        TimeSpan now,
        out bool statusChanged,
        string? line = null)
    {
        PruneTeamSpeechMemory(team, now);
        var normalizedLine = NormalizeTeamLine(line);
        if (!string.IsNullOrWhiteSpace(normalizedLine) &&
            IsRecentTeamLine(team, normalizedLine))
        {
            statusChanged = SetTeamSharedSpeechStatus(
                team,
                $"shared-speech repeated line suppressed: {key}; speaker={FormatEntityRef(speaker)}; recent={team.RecentTeamLines.Count}");
            return false;
        }

        if (now < team.NextSharedSpeechAt)
        {
            var wait = Math.Max(0, (int) Math.Ceiling((team.NextSharedSpeechAt - now).TotalSeconds));
            statusChanged = SetTeamSharedSpeechStatus(
                team,
                $"shared-speech waiting: {key} in {wait}s; speaker={FormatEntityRef(speaker)}");
            return false;
        }

        team.NextSharedSpeechAt = now + TimeSpan.FromSeconds(TeamSharedSpeechCooldownSeconds);
        if (!string.IsNullOrWhiteSpace(normalizedLine))
            RecordTeamSpeechLine(team, key, normalizedLine, now);
        statusChanged = SetTeamSharedSpeechStatus(
            team,
            $"shared-speech:{key}; speaker={FormatEntityRef(speaker)}; cooldown={TeamSharedSpeechCooldownSeconds:0}s; recentLines={team.RecentTeamLines.Count}");
        return true;
    }

    private static void PruneTeamSpeechMemory(LuaMRescueTeamComponent team, TimeSpan now)
    {
        if (team.RecentTeamLines.Count == 0)
            return;

        team.RecentTeamLines.RemoveAll(entry =>
            string.IsNullOrWhiteSpace(entry.Line) ||
            entry.ExpiresAt <= now);
    }

    private static bool IsRecentTeamLine(LuaMRescueTeamComponent team, string normalizedLine)
    {
        return team.RecentTeamLines.Any(entry =>
            string.Equals(entry.Line, normalizedLine, StringComparison.Ordinal));
    }

    private static void RecordTeamSpeechLine(
        LuaMRescueTeamComponent team,
        string key,
        string normalizedLine,
        TimeSpan now)
    {
        team.LastTeamLine = normalizedLine;
        team.LastTeamLineKey = key;
        team.LastTeamLineAt = now;

        team.RecentTeamLines.RemoveAll(entry =>
            string.Equals(entry.Line, normalizedLine, StringComparison.Ordinal));
        team.RecentTeamLines.Add(new LuaMRescueTeamSpeechMemoryEntry
        {
            Key = key,
            Line = normalizedLine,
            SpokenAt = now,
            ExpiresAt = now + TimeSpan.FromSeconds(TeamRecentLineMemorySeconds),
        });

        if (team.RecentTeamLines.Count > TeamRecentLineMemoryLimit)
        {
            team.RecentTeamLines.RemoveRange(
                0,
                team.RecentTeamLines.Count - TeamRecentLineMemoryLimit);
        }
    }

    private static string NormalizeTeamLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return string.Empty;

        return line.ReplaceLineEndings(" ").Trim();
    }

    private static bool SetTeamSharedSpeechStatus(LuaMRescueTeamComponent team, string status)
    {
        if (string.Equals(team.LastSharedSpeechStatus, status, StringComparison.Ordinal))
            return false;

        team.LastSharedSpeechStatus = status;
        return true;
    }

    private static bool SetTeamPhaseAnnouncementStatus(LuaMRescueTeamComponent team, string status)
    {
        if (string.Equals(team.LastPhaseAnnouncementStatus, status, StringComparison.Ordinal))
            return false;

        team.LastPhaseAnnouncementStatus = status;
        return true;
    }

    private static bool TryBuildTeamPhaseLine(
        LuaMRescueTeamPhase phase,
        bool somberScene,
        string returnOrExtractReason,
        out LuaMRescueEscortRole? preferredRole,
        out string line)
    {
        preferredRole = null;
        line = somberScene
            ? phase switch
            {
                LuaMRescueTeamPhase.Dispatch => "\u0412\u044b\u0435\u0437\u0434 \u043f\u0440\u0438\u043d\u044f\u0442. \u0420\u0430\u0431\u043e\u0442\u0430\u0435\u043c \u0431\u0435\u0437 \u043b\u0438\u0448\u043d\u0438\u0445 \u0440\u0435\u043f\u043b\u0438\u043a.",
                LuaMRescueTeamPhase.EnRoute => "\u041c\u0430\u0440\u0448\u0440\u0443\u0442 \u043a \u043f\u0430\u0446\u0438\u0435\u043d\u0442\u0443 \u043f\u0440\u0438\u043d\u044f\u0442. \u0418\u0434\u0435\u043c \u0431\u0435\u0437 \u0437\u0430\u0434\u0435\u0440\u0436\u0435\u043a.",
                LuaMRescueTeamPhase.SecureScene => "\u041c\u0435\u0434\u0438\u0446\u0438\u043d\u0441\u043a\u0430\u044f \u0437\u043e\u043d\u0430 \u0437\u0430\u043a\u0440\u044b\u0442\u0430. \u0410\u0439\u0431\u043e\u043b\u0438\u0442 \u0440\u0430\u0431\u043e\u0442\u0430\u0435\u0442.",
                LuaMRescueTeamPhase.Triage => "\u041f\u0430\u0446\u0438\u0435\u043d\u0442 \u043d\u0430\u0439\u0434\u0435\u043d. \u041e\u0446\u0435\u043d\u0438\u0432\u0430\u044e \u0441\u043e\u0441\u0442\u043e\u044f\u043d\u0438\u0435.",
                LuaMRescueTeamPhase.TreatOnSite => "\u041b\u0435\u0447\u0435\u043d\u0438\u0435 \u0438\u0434\u0435\u0442. \u0414\u0435\u0440\u0436\u0438\u043c \u0442\u0438\u0448\u0438\u043d\u0443 \u0438 \u043f\u0440\u043e\u0441\u0442\u0440\u0430\u043d\u0441\u0442\u0432\u043e.",
                LuaMRescueTeamPhase.PrepareEvacuation => "\u041b\u0435\u0447\u0435\u043d\u0438\u0435 \u043d\u0430 \u043c\u0435\u0441\u0442\u0435 \u043e\u0433\u0440\u0430\u043d\u0438\u0447\u0435\u043d\u043e. \u0413\u043e\u0442\u043e\u0432\u0438\u043c \u0432\u044b\u043d\u043e\u0441.",
                LuaMRescueTeamPhase.EvacuateToShuttle => "\u041f\u0430\u0446\u0438\u0435\u043d\u0442 \u0434\u0432\u0438\u0436\u0435\u0442\u0441\u044f \u043a \u0448\u0430\u0442\u0442\u043b\u0443. \u041a\u043e\u0440\u0438\u0434\u043e\u0440 \u0434\u0435\u0440\u0436\u0430\u0442\u044c \u0441\u0432\u043e\u0431\u043e\u0434\u043d\u044b\u043c.",
                LuaMRescueTeamPhase.Handoff => "\u041f\u0435\u0440\u0435\u0434\u0430\u0447\u0430 \u0437\u0430\u0432\u0435\u0440\u0448\u0435\u043d\u0430. \u0421\u0442\u0430\u0442\u0443\u0441 \u0437\u0430\u043f\u0438\u0441\u0430\u043d.",
                LuaMRescueTeamPhase.ReturnOrExtract => BuildReturnOrExtractLine(returnOrExtractReason, somberScene),
                _ => string.Empty,
            }
            : phase switch
        {
            LuaMRescueTeamPhase.Dispatch => "\u0412\u044b\u0435\u0437\u0434 \u043f\u043e\u0434\u0442\u0432\u0435\u0440\u0436\u0434\u0435\u043d. \u0413\u0440\u0443\u043f\u043f\u0430 \u0410\u0439\u0431\u043e\u043b\u0438\u0442\u0430 \u0432 \u0440\u0430\u0431\u043e\u0442\u0435.",
            LuaMRescueTeamPhase.EnRoute => "\u041c\u0430\u0440\u0448\u0440\u0443\u0442 \u0434\u043e \u043f\u0430\u0446\u0438\u0435\u043d\u0442\u0430 \u043f\u043e\u0447\u0442\u0438 \u043f\u0440\u044f\u043c\u043e\u0439. \u041f\u043e\u0447\u0442\u0438 - \u044d\u0442\u043e \u043c\u0435\u0434\u0438\u0446\u0438\u043d\u0441\u043a\u0438\u0439 \u0442\u0435\u0440\u043c\u0438\u043d.",
            LuaMRescueTeamPhase.SecureScene => "\u041f\u0435\u0440\u0438\u043c\u0435\u0442\u0440 \u0432\u0437\u044f\u0442. \u0410\u0439\u0431\u043e\u043b\u0438\u0442 \u0440\u0430\u0431\u043e\u0442\u0430\u0435\u0442.",
            LuaMRescueTeamPhase.Triage => "\u041f\u0430\u0446\u0438\u0435\u043d\u0442 \u043d\u0430\u0439\u0434\u0435\u043d. \u041d\u0430\u0447\u0438\u043d\u0430\u044e \u0441\u0442\u0430\u0431\u0438\u043b\u0438\u0437\u0430\u0446\u0438\u044e.",
            LuaMRescueTeamPhase.TreatOnSite => "\u041b\u0435\u0447\u0435\u043d\u0438\u0435 \u0438\u0434\u0435\u0442. \u041f\u0430\u043d\u0438\u043a\u0430 \u043d\u0435 \u0432\u0445\u043e\u0434\u0438\u0442 \u0432 \u043d\u0430\u0437\u043d\u0430\u0447\u0435\u043d\u0438\u0435.",
            LuaMRescueTeamPhase.PrepareEvacuation => "\u041b\u0435\u0447\u0435\u043d\u0438\u0435 \u043d\u0430 \u043c\u0435\u0441\u0442\u0435 \u043e\u0433\u0440\u0430\u043d\u0438\u0447\u0435\u043d\u043e. \u0412\u0435\u0437\u0435\u043c \u043a \u043e\u0431\u043e\u0440\u0443\u0434\u043e\u0432\u0430\u043d\u0438\u044e.",
            LuaMRescueTeamPhase.EvacuateToShuttle => "\u041a\u043e\u0441\u0442\u044b\u043b\u044c \u0438\u0434\u0435\u0442. \u0423\u0441\u0442\u0443\u043f\u0438\u0442\u0435 \u043c\u0435\u0434\u0438\u0446\u0438\u043d\u0435 \u043d\u0430 \u043d\u043e\u0433\u0430\u0445.",
            LuaMRescueTeamPhase.Handoff => "\u041f\u0430\u0446\u0438\u0435\u043d\u0442 \u043f\u043e\u0434 \u043d\u0430\u0431\u043b\u044e\u0434\u0435\u043d\u0438\u0435\u043c. \u0412\u043e\u0437\u0432\u0440\u0430\u0449\u0430\u0435\u043c\u0441\u044f.",
            LuaMRescueTeamPhase.ReturnOrExtract => BuildReturnOrExtractLine(returnOrExtractReason, somberScene),
            _ => string.Empty,
        };

        preferredRole = phase switch
        {
            LuaMRescueTeamPhase.EnRoute or LuaMRescueTeamPhase.EvacuateToShuttle => LuaMRescueEscortRole.Kostyl,
            LuaMRescueTeamPhase.SecureScene or LuaMRescueTeamPhase.ReturnOrExtract => LuaMRescueEscortRole.Tourniquet,
            _ => null,
        };

        return !string.IsNullOrWhiteSpace(line);
    }

    private string BuildReturnOrExtractReasonStatus(
        LuaMRescueTeamComponent team,
        LuaMRescueAgentComponent rescue,
        LuaMRescueTeamPhase phase)
    {
        if (phase != LuaMRescueTeamPhase.ReturnOrExtract)
            return "none";

        if (rescue.LifeSupportEmergencyActive)
        {
            return $"reason=life-support; detail=" +
                   NormalizeHandoffValue(rescue.LastLifeSupportStatus, "life-support emergency");
        }

        if (HasRouteBlockedStatus(rescue.LastRouteBlockHoldStatus))
            return $"reason=route-blocked; detail={NormalizeHandoffValue(rescue.LastRouteBlockHoldStatus, "route blocked")}";

        if (HasRouteBlockedStatus(rescue.LastAutoEvacuationStatus))
            return $"reason=route-blocked; detail={NormalizeHandoffValue(rescue.LastAutoEvacuationStatus, "route blocked")}";

        if (HasUnsafeStatus(rescue.LastAutoEvacuationStatus) ||
            HasUnsafeStatus(team.LastSceneStatus) ||
            HasUnsafeStatus(team.LastSortiePlanStatus))
        {
            return $"reason=unsafe-scene; detail={NormalizeHandoffValue(rescue.LastAutoEvacuationStatus, team.LastSceneStatus)}";
        }

        if (HasFailedStatus(rescue.LastTaskStatus) ||
            HasFailedStatus(rescue.LastAutoEvacuationStatus) ||
            HasFailedStatus(team.LastHandoffRecord))
        {
            return $"reason=failed-or-aborted; detail={NormalizeHandoffValue(rescue.LastTaskStatus, team.LastHandoffRecord)}";
        }

        if (HasDeadStatus(rescue.LastOnboardCareStatus) ||
            HasDeadStatus(rescue.LastAutoDefibStatus) ||
            HasDeadStatus(team.LastHandoffRecord))
        {
            return $"reason=dead-recovery; detail={NormalizeHandoffValue(rescue.LastOnboardCareStatus, "dead recovery")}";
        }

        if (team.HandoffRecords > 0)
            return $"reason=handoff-complete; detail={NormalizeHandoffValue(team.LastHandoffDigest, "handoff complete")}";

        return "reason=no-active-patient; detail=returning to shuttle standby";
    }

    private static string BuildReturnOrExtractLine(string returnOrExtractReason, bool somberScene)
    {
        if (returnOrExtractReason.Contains("reason=life-support", StringComparison.OrdinalIgnoreCase))
        {
            return "\u041e\u0442\u0445\u043e\u0434\u0438\u043c: \u0430\u0432\u0430\u0440\u0438\u044f \u0436\u0438\u0437\u043d\u0435\u043e\u0431\u0435\u0441\u043f\u0435\u0447\u0435\u043d\u0438\u044f \u0410\u0439\u0431\u043e\u043b\u0438\u0442\u0430. " +
                   "\u0413\u0440\u0443\u043f\u043f\u0430 \u0432\u043e\u0437\u0432\u0440\u0430\u0449\u0430\u0435\u0442\u0441\u044f \u043a \u0448\u0430\u0442\u0442\u043b\u0443.";
        }

        if (returnOrExtractReason.Contains("reason=route-blocked", StringComparison.OrdinalIgnoreCase))
            return "\u041e\u0442\u0445\u043e\u0434\u0438\u043c: \u043c\u0430\u0440\u0448\u0440\u0443\u0442 \u0437\u0430\u0431\u043b\u043e\u043a\u0438\u0440\u043e\u0432\u0430\u043d. \u041d\u0443\u0436\u0435\u043d \u043a\u043e\u0440\u0438\u0434\u043e\u0440 \u043a \u0448\u0430\u0442\u0442\u043b\u0443.";

        if (returnOrExtractReason.Contains("reason=unsafe-scene", StringComparison.OrdinalIgnoreCase))
            return "\u041e\u0442\u0445\u043e\u0434\u0438\u043c: \u0437\u043e\u043d\u0430 \u043d\u0435 \u0434\u0435\u0440\u0436\u0438\u0442\u0441\u044f. \u0421\u043e\u0445\u0440\u0430\u043d\u044f\u0435\u043c \u0448\u0430\u043d\u0441 \u043f\u0430\u0446\u0438\u0435\u043d\u0442\u0430.";

        if (returnOrExtractReason.Contains("reason=failed-or-aborted", StringComparison.OrdinalIgnoreCase))
            return "\u041e\u0442\u0445\u043e\u0434\u0438\u043c: \u0437\u0430\u0434\u0430\u0447\u0430 \u0441\u043e\u0440\u0432\u0430\u043d\u0430 \u0438\u043b\u0438 \u0446\u0435\u043b\u044c \u043d\u0435\u0434\u043e\u0441\u0442\u0443\u043f\u043d\u0430. \u041f\u0440\u0438\u0447\u0438\u043d\u0430 \u0437\u0430\u043f\u0438\u0441\u0430\u043d\u0430.";

        if (returnOrExtractReason.Contains("reason=dead-recovery", StringComparison.OrdinalIgnoreCase))
            return "\u041e\u0442\u0445\u043e\u0434\u0438\u043c: \u043f\u0430\u0446\u0438\u0435\u043d\u0442 \u0431\u0435\u0437 \u043f\u0443\u043b\u044c\u0441\u0430. \u0420\u0435\u0430\u043d\u0438\u043c\u0430\u0446\u0438\u044f \u0438 \u0441\u0442\u0430\u0442\u0443\u0441 \u043d\u0430 \u0431\u043e\u0440\u0442\u0443.";

        if (returnOrExtractReason.Contains("reason=handoff-complete", StringComparison.OrdinalIgnoreCase))
            return somberScene
                ? "\u041f\u0435\u0440\u0435\u0434\u0430\u0447\u0430 \u0437\u0430\u0432\u0435\u0440\u0448\u0435\u043d\u0430. \u0421\u0442\u0430\u0442\u0443\u0441 \u0437\u0430\u043f\u0438\u0441\u0430\u043d."
                : "\u0421\u0435\u043a\u0442\u043e\u0440 \u043e\u0442\u043f\u0443\u0441\u043a\u0430\u0435\u043c. \u041f\u0430\u0446\u0438\u0435\u043d\u0442 \u043f\u0435\u0440\u0435\u0434\u0430\u043d.";

        return somberScene
            ? "\u041e\u0442\u0445\u043e\u0434\u0438\u043c \u043e\u0442 \u0437\u043e\u043d\u044b. \u041f\u0440\u0438\u0447\u0438\u043d\u0430 \u0437\u0430\u043f\u0438\u0441\u0430\u043d\u0430."
            : "\u0421\u0435\u043a\u0442\u043e\u0440 \u043e\u0442\u043f\u0443\u0441\u043a\u0430\u0435\u043c. \u0412\u043e\u0437\u0432\u0440\u0430\u0449\u0430\u0435\u043c\u0441\u044f \u043a \u0448\u0430\u0442\u0442\u043b\u0443.";
    }

    private static bool HasRouteBlockedStatus(string? status)
    {
        return ContainsStatus(status, "route blocked") ||
               ContainsStatus(status, "route-blocked") ||
               ContainsStatus(status, "fallback-extraction") ||
               ContainsStatus(status, "blocked route");
    }

    private static bool HasUnsafeStatus(string? status)
    {
        return ContainsStatus(status, "unsafe") ||
               ContainsStatus(status, "overwhelming") ||
               ContainsStatus(status, "threat");
    }

    private static bool HasFailedStatus(string? status)
    {
        return ContainsStatus(status, "failed") ||
               ContainsStatus(status, "failure") ||
               ContainsStatus(status, "aborted") ||
               ContainsStatus(status, "incomplete") ||
               ContainsStatus(status, "stalled") ||
               ContainsStatus(status, "unavailable");
    }

    private static bool HasDeadStatus(string? status)
    {
        return ContainsStatus(status, "dead") ||
               ContainsStatus(status, "no pulse");
    }

    private static bool ContainsStatus(string? status, string value)
    {
        return !string.IsNullOrWhiteSpace(status) &&
               status.Contains(value, StringComparison.OrdinalIgnoreCase);
    }

    private static LuaMRescueEscortRole SelectTriageCoverRole(LuaMRescueTeamComponent team, string decisionKey)
    {
        if (HasThreatPressure(team) || decisionKey == "unsafe-evacuation")
            return LuaMRescueEscortRole.Zaslon;

        if (HasCrowdPressure(team))
            return LuaMRescueEscortRole.Tourniquet;

        if (HasRoutePressure(team) ||
            decisionKey == "dead-recovery" ||
            decisionKey == "critical-evacuation" ||
            decisionKey == "heavy-evacuation")
        {
            return LuaMRescueEscortRole.Kostyl;
        }

        return LuaMRescueEscortRole.Tourniquet;
    }

    private bool TryFindEscortByRole(
        LuaMRescueTeamComponent team,
        LuaMRescueEscortRole role,
        out EntityUid uid,
        out LuaMRescueEscortComponent escort)
    {
        foreach (var candidate in team.Escorts)
        {
            if (candidate.Valid &&
                !Deleted(candidate) &&
                TryComp<LuaMRescueEscortComponent>(candidate, out var candidateEscort) &&
                candidateEscort.Role == role)
            {
                uid = candidate;
                escort = candidateEscort;
                return true;
            }
        }

        uid = default;
        escort = default!;
        return false;
    }

    private bool TryFindAnyEscort(
        LuaMRescueTeamComponent team,
        out EntityUid uid,
        out LuaMRescueEscortComponent escort)
    {
        foreach (var candidate in team.Escorts)
        {
            if (candidate.Valid &&
                !Deleted(candidate) &&
                TryComp<LuaMRescueEscortComponent>(candidate, out var candidateEscort))
            {
                uid = candidate;
                escort = candidateEscort;
                return true;
            }
        }

        uid = default;
        escort = default!;
        return false;
    }

    private static string BuildTriageCoverLine(LuaMRescueEscortRole role, string decisionKey)
    {
        return role switch
        {
            LuaMRescueEscortRole.Zaslon when decisionKey == "unsafe-evacuation" =>
                "\u041e\u043f\u0430\u0441\u043d\u0443\u044e \u0441\u0442\u043e\u0440\u043e\u043d\u0443 \u0434\u0435\u0440\u0436\u0443. \u0410\u0439\u0431\u043e\u043b\u0438\u0442, \u0440\u0430\u0431\u043e\u0442\u0430\u0439.",
            LuaMRescueEscortRole.Zaslon =>
                "\u0423\u0433\u0440\u043e\u0437\u0430 \u043d\u0430 \u043c\u043d\u0435. \u041c\u0435\u0434\u0438\u043a \u0437\u0430 \u0441\u043f\u0438\u043d\u043e\u0439.",
            LuaMRescueEscortRole.Kostyl when decisionKey == "dead-recovery" =>
                "\u041f\u0435\u0440\u0435\u043d\u043e\u0441 \u0431\u0435\u0440\u0443. \u041f\u0430\u0446\u0438\u0435\u043d\u0442 \u043d\u0435 \u043e\u0441\u0442\u0430\u043d\u0435\u0442\u0441\u044f \u0437\u0434\u0435\u0441\u044c.",
            LuaMRescueEscortRole.Kostyl when decisionKey.EndsWith("evacuation", StringComparison.Ordinal) =>
                "\u041c\u0430\u0440\u0448\u0440\u0443\u0442 \u043a \u0448\u0430\u0442\u0442\u043b\u0443 \u0434\u0435\u0440\u0436\u0443. \u041f\u0430\u0446\u0438\u0435\u043d\u0442\u0430 \u043d\u0435 \u0431\u0440\u043e\u0441\u0430\u0435\u043c.",
            LuaMRescueEscortRole.Tourniquet when decisionKey == "onsite-treatment" =>
                "\u041f\u0435\u0440\u0438\u043c\u0435\u0442\u0440 \u0441\u0442\u0430\u0431\u0438\u043b\u0435\u043d. \u0410\u0439\u0431\u043e\u043b\u0438\u0442 \u0440\u0430\u0431\u043e\u0442\u0430\u0435\u0442.",
            LuaMRescueEscortRole.Tourniquet =>
                "\u041c\u0435\u0434\u0438\u0446\u0438\u043d\u0441\u043a\u0430\u044f \u0437\u043e\u043d\u0430 \u0434\u0435\u0440\u0436\u0438\u0442\u0441\u044f. \u041d\u0435 \u043c\u0435\u0448\u0430\u0435\u043c \u0432\u0440\u0430\u0447\u0443.",
            _ =>
                "\u041f\u0440\u0438\u043a\u0440\u044b\u0442\u0438\u0435 \u043d\u0430 \u043c\u0435\u0441\u0442\u0435. \u0410\u0439\u0431\u043e\u043b\u0438\u0442 \u0440\u0430\u0431\u043e\u0442\u0430\u0435\u0442.",
        };
    }

    private void TryRunEscortDutyAction(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        HTNComponent htn,
        LuaMRescueEscortDuty duty,
        EntityUid? followTarget)
    {
        if (_timing.CurTime < escort.NextDutyActionAt)
            return;

        escort.NextDutyActionAt = _timing.CurTime + TimeSpan.FromSeconds(EscortDutyActionIntervalSeconds);

        if (duty != LuaMRescueEscortDuty.ThreatScreen)
            htn.Blackboard.Remove<EntityUid>(NPCBlackboard.CurrentOrderedTarget);

        if (IsEscortCombatReadinessDuty(duty))
            TryEnsureEscortWeaponReady(uid, escort, duty);
        else
            escort.LastWeaponReadinessStatus = $"weapon-ready not required for {FormatDuty(duty)}";

        if (duty == LuaMRescueEscortDuty.ThreatScreen)
        {
            TryRunThreatScreenAction(uid, escort, htn, followTarget);
            return;
        }

        if (duty == LuaMRescueEscortDuty.ClearRoute)
        {
            TryRunClearRouteAction(uid, escort, htn, followTarget);
            return;
        }

        if (ShouldEscortAssistPatientPull(escort, duty))
        {
            TryRunPatientAssistAction(uid, escort, followTarget);
            return;
        }

        if (ShouldEscortCrowdControl(escort, duty))
        {
            TryRunCrowdControlAction(uid, escort, followTarget);
            return;
        }

        if (duty == LuaMRescueEscortDuty.ReturnToShuttle)
        {
            TryRunReturnToShuttleAction(uid, escort, followTarget);
            return;
        }

        escort.LastDutyActionStatus = $"watch {FormatDuty(duty)}";
    }

    private bool TryEnsureEscortWeaponReady(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        LuaMRescueEscortDuty duty)
    {
        var equipmentPolicy = GetEscortRoleProfile(uid, escort).EquipmentPolicy;
        if (!equipmentPolicy.Allows(LuaMRescueEquipmentKind.Weapon))
        {
            escort.LastWeaponReadinessStatus =
                $"weapon-ready denied by {GetActivityRole(escort.Role)} equipment policy";
            return false;
        }

        if (!TryComp<HandsComponent>(uid, out var hands))
        {
            escort.LastWeaponReadinessStatus = $"weapon-ready failed for {FormatDuty(duty)}: no hands";
            return false;
        }

        if (TrySelectHeldEscortWeapon(uid, hands, out var heldWeapon))
        {
            escort.LastWeaponReadinessStatus = $"weapon-ready held {FormatEntityRef(heldWeapon)} for {FormatDuty(duty)}";
            return true;
        }

        if (TryTakeStoredEscortWeapon(uid, hands, out var storedWeapon, out var takeStatus))
        {
            escort.LastWeaponReadinessStatus = $"weapon-ready stored {FormatEntityRef(storedWeapon)} for {FormatDuty(duty)}; {takeStatus}";
            return true;
        }

        if (!takeStatus.StartsWith("no empty hand for stored weapon", StringComparison.Ordinal))
        {
            escort.LastWeaponReadinessStatus = $"weapon-ready failed for {FormatDuty(duty)}: {takeStatus}";
            return false;
        }

        if (!TryMakeRoomForEscortWeapon(uid, hands, out var stowStatus))
        {
            escort.LastWeaponReadinessStatus = $"weapon-ready failed for {FormatDuty(duty)}: {takeStatus}; {stowStatus}";
            return false;
        }

        if (TryTakeStoredEscortWeapon(uid, hands, out storedWeapon, out takeStatus))
        {
            escort.LastWeaponReadinessStatus = $"weapon-ready stored {FormatEntityRef(storedWeapon)} for {FormatDuty(duty)}; {stowStatus}; {takeStatus}";
            return true;
        }

        escort.LastWeaponReadinessStatus = $"weapon-ready failed for {FormatDuty(duty)}: {stowStatus}; {takeStatus}";
        return false;
    }

    private bool TrySelectHeldEscortWeapon(EntityUid uid, HandsComponent hands, out EntityUid weapon)
    {
        weapon = default;

        if (hands.ActiveHandEntity is { Valid: true } activeHeld &&
            IsEscortCombatWeapon(activeHeld))
        {
            weapon = activeHeld;
            return true;
        }

        foreach (var hand in hands.Hands.Values)
        {
            if (hand.HeldEntity is not { Valid: true } held ||
                !IsEscortCombatWeapon(held))
            {
                continue;
            }

            if (!_hands.TrySelect(uid, held, hands))
                continue;

            weapon = held;
            return true;
        }

        return false;
    }

    private bool TryTakeStoredEscortWeapon(
        EntityUid uid,
        HandsComponent hands,
        out EntityUid weapon,
        out string status)
    {
        weapon = default;
        status = "no combat storage slot found";
        EntityUid weaponStorageUid = default;
        StorageComponent weaponStorage = default!;

        foreach (var slot in GetEscortCombatStorageSlots(uid))
        {
            if (!TryResolveEscortStorageSlot(uid, slot, out var storageUid, out var storage, out status))
                continue;

            if (!TrySelectStoredEscortWeapon(storage, out weapon))
            {
                status = $"{FormatEntityRef(storageUid)} in slot {slot} has no combat weapon";
                continue;
            }

            weaponStorageUid = storageUid;
            weaponStorage = storage;
            break;
        }

        if (weapon is not { Valid: true })
            return false;

        if (!_hands.TryGetEmptyHand(uid, out var emptyHand, hands))
        {
            status = "no empty hand for stored weapon";
            return false;
        }

        if (!weaponStorage.Container.Contains(weapon))
        {
            status = $"{FormatEntityRef(weapon)} is no longer in {FormatEntityRef(weaponStorageUid)}";
            return false;
        }

        if (!_hands.TryPickup(uid, weapon, emptyHand, handsComp: hands))
        {
            status = $"could not take weapon {FormatEntityRef(weapon)} from {FormatEntityRef(weaponStorageUid)}";
            return false;
        }

        _hands.TrySelect(uid, weapon, hands);
        status = $"took weapon {FormatEntityRef(weapon)} from {FormatEntityRef(weaponStorageUid)}";
        return true;
    }

    private bool TryMakeRoomForEscortWeapon(EntityUid uid, HandsComponent hands, out string status)
    {
        if (_hands.TryGetEmptyHand(uid, out _, hands))
        {
            status = "empty hand already available";
            return true;
        }

        status = "no stowable hand item for stored weapon";
        foreach (var hand in EnumerateEscortHandsForStow(hands))
        {
            if (hand.HeldEntity is not { Valid: true } held ||
                IsEscortCombatWeapon(held))
            {
                continue;
            }

            foreach (var slot in GetEscortCombatStorageSlots(uid))
            {
                if (!TryResolveEscortStorageSlot(uid, slot, out var storageUid, out var storage, out _))
                    continue;

                if (!_storage.CanInsert(storageUid, held, out var reason, storage))
                {
                    status = reason == null
                        ? $"could not stow {FormatEntityRef(held)} in {FormatEntityRef(storageUid)}"
                        : $"could not stow {FormatEntityRef(held)} in {FormatEntityRef(storageUid)}: {reason}";
                    continue;
                }

                if (!_hands.TryDrop(uid, hand, handsComp: hands))
                {
                    status = $"could not free hand by dropping {FormatEntityRef(held)}";
                    continue;
                }

                if (_storage.Insert(storageUid, held, out _, out _, user: uid, storageComp: storage))
                {
                    status = $"stowed {FormatEntityRef(held)} in {FormatEntityRef(storageUid)} from hand";
                    return true;
                }

                _hands.TryPickup(uid, held, hand, handsComp: hands);
                status = $"could not stow {FormatEntityRef(held)} in {FormatEntityRef(storageUid)}";
            }
        }

        return false;
    }

    private bool TryResolveEscortStorageSlot(
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
            status = "escort has no inventory";
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

        storageUid = slotItem;
        storage = storageComp;
        return true;
    }

    private bool TrySelectStoredEscortWeapon(StorageComponent storage, out EntityUid weapon)
    {
        weapon = default;

        var contained = storage.Container.ContainedEntities;
        for (var i = contained.Count - 1; i >= 0; i--)
        {
            var candidate = contained[i];
            if (!IsEscortCombatWeapon(candidate))
                continue;

            weapon = candidate;
            return true;
        }

        return false;
    }

    private bool IsEscortCombatWeapon(EntityUid item)
    {
        return !Deleted(item) && HasComp<GunComponent>(item);
    }

    private static bool IsEscortCombatReadinessDuty(LuaMRescueEscortDuty duty)
    {
        return duty is LuaMRescueEscortDuty.ThreatScreen
            or LuaMRescueEscortDuty.SecureScene
            or LuaMRescueEscortDuty.EvacuationCorridor
            or LuaMRescueEscortDuty.CrowdControl;
    }

    private static IEnumerable<Hand> EnumerateEscortHandsForStow(HandsComponent hands)
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

    private void TryRunReturnToShuttleAction(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        EntityUid? followTarget)
    {
        var shuttleTarget = ValidOrNull(escort.ShuttleAnchor) ??
                            ValidOrNull(escort.Shuttle) ??
                            ValidOrNull(followTarget);
        var releasedPull = TryReleaseReturnPull(uid, shuttleTarget, out var released);
        if (releasedPull)
            escort.DutyActions++;

        if (shuttleTarget is not { Valid: true } target)
        {
            escort.LastDutyActionStatus = releasedPull
                ? $"return-to-shuttle released pull {FormatEntityRef(released)}; no shuttle target"
                : "return-to-shuttle no shuttle target";
            return;
        }

        if (IsWithinRange(uid, target, 2.5f))
        {
            var status = releasedPull
                ? $"return-to-shuttle released pull {FormatEntityRef(released)}; ready at {FormatEntityRef(target)}"
                : $"return-to-shuttle ready at {FormatEntityRef(target)}";
            if (!releasedPull &&
                !string.Equals(escort.LastDutyActionStatus, status, StringComparison.Ordinal))
            {
                escort.DutyActions++;
            }

            escort.LastDutyActionStatus = status;
            return;
        }

        escort.LastDutyActionStatus = releasedPull
            ? $"return-to-shuttle released pull {FormatEntityRef(released)}; moving to {FormatEntityRef(target)}"
            : $"return-to-shuttle moving to {FormatEntityRef(target)}";
    }

    private bool TryReleaseReturnPull(EntityUid uid, EntityUid? shuttleTarget, out EntityUid released)
    {
        released = default;

        if (!TryComp<PullerComponent>(uid, out var puller) ||
            puller.Pulling is not { Valid: true } pulled ||
            Deleted(pulled) ||
            shuttleTarget is { Valid: true } target && pulled == target ||
            !TryComp<PullableComponent>(pulled, out var pullable))
        {
            return false;
        }

        if (!_pulling.TryStopPull(pulled, pullable, uid))
            return false;

        released = pulled;
        return true;
    }

    private void TryRunThreatScreenAction(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        HTNComponent htn,
        EntityUid? followTarget)
    {
        var threatPolicy = GetEscortRoleProfile(uid, escort).ThreatPolicy;
        if (!threatPolicy.EngageHostiles)
        {
            htn.Blackboard.Remove<EntityUid>(NPCBlackboard.CurrentOrderedTarget);
            escort.LastDutyActionStatus = "threat-screen role policy forbids engagement";
            return;
        }

        var target = ValidOrNull(escort.ThreatTarget) ?? ValidOrNull(followTarget);
        if (target is not { Valid: true } threatUid ||
            Deleted(threatUid))
        {
            htn.Blackboard.Remove<EntityUid>(NPCBlackboard.CurrentOrderedTarget);
            escort.LastDutyActionStatus = "threat-screen no threat target";
            return;
        }

        if (TryComp<MobStateComponent>(threatUid, out var mobState) &&
            mobState.CurrentState == MobState.Dead)
        {
            htn.Blackboard.Remove<EntityUid>(NPCBlackboard.CurrentOrderedTarget);
            var reported = TryReportThreatNeutralized(uid, escort, threatUid);
            escort.LastDutyActionStatus = reported
                ? $"threat-screen target neutralized and reported {FormatEntityRef(threatUid)}"
                : $"threat-screen target neutralized {FormatEntityRef(threatUid)}";
            return;
        }

        if (!IsThreatWithinRescueLeash(uid, escort, threatUid))
        {
            htn.Blackboard.Remove<EntityUid>(NPCBlackboard.CurrentOrderedTarget);
            escort.LastDutyActionStatus = $"threat-screen leash holding rescue perimeter; threat={FormatEntityRef(threatUid)}";
            return;
        }

        if (escort.NearbyHostiles > Math.Max(0, threatPolicy.SelfPreservationHostileLimit))
        {
            htn.Blackboard.Remove<EntityUid>(NPCBlackboard.CurrentOrderedTarget);
            if (threatPolicy.RequestHelpWhenOverwhelmed)
            {
                TryRequestCrewHelp(
                    uid,
                    escort,
                    $"threat-overwhelmed:{escort.TeamId}",
                    $"threat screen holding: {escort.NearbyHostiles} hostiles exceed profile limit {threatPolicy.SelfPreservationHostileLimit}",
                    "Спасательная группа под сильным огнём. Требуется поддержка охраны.");
            }

            escort.LastDutyActionStatus =
                $"threat-screen self-preservation hold {escort.NearbyHostiles}/{threatPolicy.SelfPreservationHostileLimit}";
            return;
        }

        if (TryComp<CombatModeComponent>(uid, out var combat))
            _combatMode.SetInCombatMode(uid, true, combat);

        var syntheticThreat = IsPrioritySyntheticThreat(uid, threatUid);
        if (!IsHostileToObserver(uid, threatUid) && !syntheticThreat)
        {
            htn.Blackboard.Remove<EntityUid>(NPCBlackboard.CurrentOrderedTarget);
            escort.LastDutyActionStatus = $"threat-screen screening armed pressure {FormatEntityRef(threatUid)}";
            return;
        }

        _npc.SetBlackboard(uid, NPCBlackboard.CurrentOrderedTarget, threatUid, htn);
        _npc.WakeNPC(uid, htn);
        escort.DutyActions++;

        escort.LastDutyActionStatus = syntheticThreat
            ? !IsWithinRange(uid, threatUid, EscortThreatScreenRange)
                ? $"threat-screen advancing to synthetic {FormatEntityRef(threatUid)}; immediate synthetic cleanup"
                : $"threat-screen engaging synthetic {FormatEntityRef(threatUid)}; immediate synthetic cleanup"
            : !IsWithinRange(uid, threatUid, EscortThreatScreenRange)
                ? $"threat-screen advancing to hostile {FormatEntityRef(threatUid)}"
                : $"threat-screen engaging hostile {FormatEntityRef(threatUid)}";
    }

    private bool TryReportThreatNeutralized(
        EntityUid escortUid,
        LuaMRescueEscortComponent escort,
        EntityUid threatUid)
    {
        if (escort.Leader is not { Valid: true } leader ||
            Deleted(leader) ||
            !TryComp<LuaMRescueTeamComponent>(leader, out var team))
        {
            return false;
        }

        var threatKind = IsSyntheticRescueActor(threatUid) ? "synthetic" : "hostile";
        var targetRef = FormatEntityRef(threatUid);
        var role = FormatRole(escort.Role);

        if (team.LastThreatNeutralizedTarget == threatUid)
            return false;

        var now = _timing.CurTime;
        if (now < team.NextThreatNeutralizedReportAt)
        {
            var wait = Math.Max(0, (int) Math.Ceiling((team.NextThreatNeutralizedReportAt - now).TotalSeconds));
            if (SetThreatNeutralizedStatus(
                    team,
                    $"threat-neutralized waiting {wait}s; kind={threatKind}; target={targetRef}; by={role}"))
            {
                Dirty(leader, team);
            }

            return false;
        }

        var line = BuildThreatNeutralizedLine(threatKind, Name(threatUid));
        if (!TryReserveTeamSpeech(
                escortUid,
                team,
                $"threat-neutralized:{threatKind}:{threatUid}",
                now,
                out var speechStatusChanged,
                line))
        {
            if (SetThreatNeutralizedStatus(
                    team,
                    $"threat-neutralized waiting shared speech; kind={threatKind}; target={targetRef}; by={role}") ||
                speechStatusChanged)
            {
                Dirty(leader, team);
            }

            return false;
        }

        if (!Deleted(escortUid) &&
            !string.IsNullOrWhiteSpace(line))
        {
            _chat.TrySendInGameICMessage(escortUid, line, InGameICChatType.Speak, hideChat: false, hideLog: true);
        }

        team.LastThreatNeutralizedTarget = threatUid;
        team.LastThreatNeutralizedBy = escortUid;
        team.NextThreatNeutralizedReportAt = now + TimeSpan.FromSeconds(ThreatNeutralizedReportCooldownSeconds);
        SetThreatNeutralizedStatus(
            team,
            $"threat-neutralized:{threatKind}; target={targetRef}; by={role}");
        Dirty(leader, team);
        return true;
    }

    private static bool SetThreatNeutralizedStatus(LuaMRescueTeamComponent team, string status)
    {
        if (string.Equals(team.LastThreatNeutralizedStatus, status, StringComparison.Ordinal))
            return false;

        team.LastThreatNeutralizedStatus = status;
        return true;
    }

    private static string BuildThreatNeutralizedLine(string threatKind, string targetName)
    {
        return threatKind == "synthetic"
            ? $"\u0421\u0438\u043d\u0442\u0435\u0442\u0438\u0447\u0435\u0441\u043a\u0430\u044f \u0443\u0433\u0440\u043e\u0437\u0430 {targetName} \u043d\u0435\u0439\u0442\u0440\u0430\u043b\u0438\u0437\u043e\u0432\u0430\u043d\u0430. \u041f\u0430\u0446\u0438\u0435\u043d\u0442 \u043f\u043e\u0434 \u0437\u0430\u0449\u0438\u0442\u043e\u0439."
            : $"\u0423\u0433\u0440\u043e\u0437\u0430 {targetName} \u043d\u0435\u0439\u0442\u0440\u0430\u043b\u0438\u0437\u043e\u0432\u0430\u043d\u0430. \u041f\u0435\u0440\u0438\u043c\u0435\u0442\u0440 \u0434\u0435\u0440\u0436\u0438\u043c.";
    }

    private bool TryRequestCrewHelp(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        string key,
        string status,
        string line)
    {
        escort.LastCrewHelpStatus = $"crew-help:{key}; {status}";

        if (Deleted(uid) ||
            string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var now = _timing.CurTime;
        if (string.Equals(escort.LastCrewHelpKey, key, StringComparison.Ordinal) &&
            escort.NextCrewHelpRequestAt > now)
        {
            return false;
        }

        if (TryGetEscortLeaderTeam(escort, out var leader, out var team))
        {
            var reserved = TryReserveTeamSpeech(uid, team, $"crew-help:{key}", now, out var speechStatusChanged, line);
            if (speechStatusChanged)
                Dirty(leader, team);

            if (!reserved)
                return false;
        }

        escort.LastCrewHelpKey = key;
        escort.NextCrewHelpRequestAt = now + TimeSpan.FromSeconds(CrewHelpRequestCooldownSeconds);
        escort.NextSpeechTime = now + TimeSpan.FromSeconds(EscortSpeechCooldownSeconds);
        _chat.TrySendInGameICMessage(uid, line, InGameICChatType.Speak, hideChat: false, hideLog: true);
        return true;
    }

    private void TryRunClearRouteAction(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        HTNComponent htn,
        EntityUid? followTarget)
    {
        var blocker = ValidOrNull(escort.RouteBlockerTarget);
        if (blocker is not { Valid: true } blockerUid)
        {
            if (HasRoutePressure(escort))
            {
                TryRequestCrewHelp(
                    uid,
                    escort,
                    "mark-safe-path",
                    $"route pressure without target; scene={escort.LastSceneStatus}",
                    "\u041e\u0442\u043c\u0435\u0442\u044c\u0442\u0435 \u0441\u0432\u043e\u0431\u043e\u0434\u043d\u044b\u0439 \u043f\u0443\u0442\u044c \u043a \u0448\u0430\u0442\u0442\u043b\u0443. \u0410\u0439\u0431\u043e\u043b\u0438\u0442 \u0432\u0435\u0434\u0435\u0442 \u043f\u0430\u0446\u0438\u0435\u043d\u0442\u0430.");
            }

            escort.LastDutyActionStatus = "clear-route no blocker target";
            return;
        }

        if (!TryComp<PullableComponent>(blockerUid, out var pullable))
        {
            TerminalizeEscortAction(uid, escort, LuaMRescueFailureReason.TargetNotPullable);
            TryRequestCrewHelp(
                uid,
                escort,
                $"open-route:{blockerUid}",
                $"open route around non-pullable {FormatEntityRef(blockerUid)}",
                "\u041e\u0442\u043a\u0440\u043e\u0439\u0442\u0435 \u0434\u0432\u0435\u0440\u044c \u0438\u043b\u0438 \u043e\u0442\u043c\u0435\u0442\u044c\u0442\u0435 \u043e\u0431\u0445\u043e\u0434. \u041a\u043e\u0440\u0438\u0434\u043e\u0440 \u043d\u0443\u0436\u0435\u043d \u0434\u043b\u044f \u044d\u0432\u0430\u043a\u0443\u0430\u0446\u0438\u0438.");
            escort.LastDutyActionStatus = $"clear-route blocker not pullable {FormatEntityRef(blockerUid)}";
            return;
        }

        if (!TryComp<PullerComponent>(uid, out var puller))
        {
            TerminalizeEscortAction(uid, escort, LuaMRescueFailureReason.NoFreeHand);
            escort.LastDutyActionStatus = "clear-route escort cannot pull";
            return;
        }

        if (TryFinishClearRouteBlockerAtDropoff(uid, escort, htn, blockerUid, puller, pullable))
            return;

        if (puller.Pulling == blockerUid)
        {
            escort.LastDutyActionStatus = TrySetClearRouteDropoffTarget(uid, escort, htn, blockerUid)
                ? $"clear-route dragging {FormatEntityRef(blockerUid)} to dropoff"
                : $"clear-route pulling {FormatEntityRef(blockerUid)}";
            return;
        }

        if (!IsWithinRange(uid, blockerUid, EscortDutyActionRange))
        {
            TryRequestCrewHelp(
                uid,
                escort,
                $"clear-blocker:{blockerUid}",
                $"clear path to route blocker {FormatEntityRef(blockerUid)}",
                "\u041e\u0441\u0432\u043e\u0431\u043e\u0434\u0438\u0442\u0435 \u043a\u043e\u0440\u0438\u0434\u043e\u0440: \u0443\u0431\u0435\u0440\u0438\u0442\u0435 \u044f\u0449\u0438\u043a \u0438\u043b\u0438 \u043e\u0442\u043a\u0440\u043e\u0439\u0442\u0435 \u0434\u0432\u0435\u0440\u044c.");
            escort.LastDutyActionStatus = $"clear-route moving to {FormatEntityRef(blockerUid)}";
            return;
        }

        if (escort.ClearRoutePullAttemptTarget != blockerUid)
            ResetClearRoutePullAttempts(escort);
        if (escort.ClearRoutePullAttemptTarget == blockerUid &&
            escort.NextClearRoutePullAttemptAt > _timing.CurTime)
        {
            escort.LastDutyActionStatus =
                $"clear-route pull backoff {FormatEntityRef(blockerUid)} until " +
                $"{escort.NextClearRoutePullAttemptAt.TotalSeconds:0.0}s";
            return;
        }

        if (!TryPrepareEscortPull(uid, escort, out var pullFailure, out var pullPreparation))
        {
            TerminalizeEscortAction(uid, escort, pullFailure);
            escort.LastDutyActionStatus = $"clear-route pull blocked: {pullPreparation}";
            return;
        }

        if (_pulling.TryStartPull(uid, blockerUid, puller, pullable))
        {
            ResetClearRoutePullAttempts(escort);
            escort.DutyActions++;
            escort.LastDutyActionStatus = TrySetClearRouteDropoffTarget(uid, escort, htn, blockerUid)
                ? $"clear-route dragging {FormatEntityRef(blockerUid)} to dropoff"
                : $"clear-route pulling {FormatEntityRef(blockerUid)}";
            TryRequestCrewHelp(
                uid,
                escort,
                $"hold-corridor:{blockerUid}",
                $"keep corridor open while dragging {FormatEntityRef(blockerUid)}",
                "\u0414\u0435\u0440\u0436\u0438\u0442\u0435 \u043f\u0440\u043e\u0445\u043e\u0434 \u0441\u0432\u043e\u0431\u043e\u0434\u043d\u044b\u043c. \u042f \u0443\u0432\u043e\u0436\u0443 \u043f\u043e\u043c\u0435\u0445\u0443.");
            return;
        }

        var terminalPullFailure = RecordClearRoutePullFailure(uid, escort, blockerUid);

        TryRequestCrewHelp(
            uid,
            escort,
            $"remove-blocker:{blockerUid}",
            $"manual removal needed for {FormatEntityRef(blockerUid)}",
            "\u041d\u0443\u0436\u043d\u0430 \u043f\u043e\u043c\u043e\u0449\u044c: \u0443\u0431\u0435\u0440\u0438\u0442\u0435 \u044d\u0442\u0443 \u043f\u043e\u043c\u0435\u0445\u0443 \u0438 \u0434\u0435\u0440\u0436\u0438\u0442\u0435 \u0434\u0432\u0435\u0440\u0438 \u043e\u0442\u043a\u0440\u044b\u0442\u044b\u043c\u0438.");
        escort.LastDutyActionStatus = terminalPullFailure
            ? $"clear-route terminal pull failure {FormatEntityRef(blockerUid)}"
            : $"clear-route pull blocked {FormatEntityRef(blockerUid)}; " +
              $"attempt={escort.ClearRoutePullAttempts}; retryAt={escort.NextClearRoutePullAttemptAt.TotalSeconds:0.0}s";
    }

    private bool RecordClearRoutePullFailure(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        EntityUid blocker)
    {
        if (escort.ClearRoutePullAttemptTarget != blocker)
        {
            escort.ClearRoutePullAttemptTarget = blocker;
            escort.ClearRoutePullAttempts = 0;
            escort.NextClearRoutePullAttemptAt = TimeSpan.Zero;
        }

        escort.ClearRoutePullAttempts++;
        var profile = TryComp<LuaMRescueActivityCarrierComponent>(uid, out var carrier)
            ? carrier.ActivityRoleProfile
            : LuaMRescueRoleProfile.CreateDefault(GetActivityRole(escort.Role));
        if (escort.ClearRoutePullAttempts >= Math.Max(1, profile.MaxAttempts))
        {
            escort.NextClearRoutePullAttemptAt = TimeSpan.Zero;
            TerminalizeEscortAction(uid, escort, LuaMRescueFailureReason.ActionCancelled);
            return true;
        }

        escort.NextClearRoutePullAttemptAt = _timing.CurTime +
            CalculateEscortActivityBackoff(profile, escort.ClearRoutePullAttempts);
        return false;
    }

    private IReadOnlyList<string> GetEscortCombatStorageSlots(EntityUid uid)
    {
        if (TryComp<LuaMRescueEscortComponent>(uid, out var escort))
        {
            var slots = GetEscortRoleProfile(uid, escort).EquipmentPolicy.InventorySearchSlots;
            if (slots.Count > 0)
                return slots;
        }

        return EscortCombatStorageSlotPriority;
    }

    private bool TryPrepareEscortPull(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        out LuaMRescueFailureReason failure,
        out string status)
    {
        var policy = GetEscortRoleProfile(uid, escort).EquipmentPolicy;
        if (!policy.Allows(LuaMRescueEquipmentKind.Pulling))
        {
            failure = LuaMRescueFailureReason.RoleDisallowed;
            status = $"{GetActivityRole(escort.Role)} equipment policy forbids pulling";
            return false;
        }

        failure = LuaMRescueFailureReason.None;
        if (!policy.RequireFreeHandForPull)
        {
            status = "profile does not require a free pull hand";
            return true;
        }

        if (!TryComp<HandsComponent>(uid, out var hands))
        {
            failure = LuaMRescueFailureReason.NoFreeHand;
            status = "escort has no hands";
            return false;
        }

        if (_hands.TryGetEmptyHand(uid, out _, hands))
        {
            status = "free pull hand ready";
            return true;
        }

        foreach (var hand in EnumerateEscortHandsForStow(hands))
        {
            if (hand.HeldEntity is not { Valid: true } held)
                continue;

            foreach (var slot in GetEscortCombatStorageSlots(uid))
            {
                if (!TryResolveEscortStorageSlot(uid, slot, out var storageUid, out var storage, out _) ||
                    !_storage.CanInsert(storageUid, held, out _, storage) ||
                    !_hands.TryDrop(uid, hand, handsComp: hands))
                {
                    continue;
                }

                if (_storage.Insert(storageUid, held, out _, out _, user: uid, storageComp: storage))
                {
                    status = $"stowed {FormatEntityRef(held)} in {FormatEntityRef(storageUid)} for pulling";
                    return true;
                }

                _hands.TryPickup(uid, held, hand, handsComp: hands);
            }
        }

        failure = LuaMRescueFailureReason.NoFreeHand;
        status = "all hands occupied and no carried storage accepted an item";
        return false;
    }

    private static void ResetClearRoutePullAttempts(LuaMRescueEscortComponent escort)
    {
        escort.ClearRoutePullAttemptTarget = null;
        escort.ClearRoutePullAttempts = 0;
        escort.NextClearRoutePullAttemptAt = TimeSpan.Zero;
    }

    private bool TryFinishClearRouteBlockerAtDropoff(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        HTNComponent htn,
        EntityUid blockerUid,
        PullerComponent puller,
        PullableComponent pullable)
    {
        if (!IsRouteBlockerClearOfRescueCorridor(escort, blockerUid, out var clearanceStatus))
            return false;

        if (puller.Pulling != blockerUid)
        {
            escort.RouteBlockerTarget = null;
            ResetClearRouteReleaseAttempts(escort);
            CompleteEscortActivity(uid, escort);
            escort.LastDutyActionStatus = $"clear-route blocker already clear {FormatEntityRef(blockerUid)}; {clearanceStatus}";
            return true;
        }

        if (escort.ClearRouteReleaseAttemptTarget == blockerUid &&
            escort.NextClearRouteReleaseAttemptAt > _timing.CurTime)
        {
            escort.LastDutyActionStatus =
                $"clear-route release backoff {FormatEntityRef(blockerUid)} until " +
                $"{escort.NextClearRouteReleaseAttemptAt.TotalSeconds:0.0}s; {clearanceStatus}";
            return true;
        }

        if (_pulling.TryStopPull(blockerUid, pullable, uid))
        {
            escort.RouteBlockerTarget = null;
            ResetClearRouteReleaseAttempts(escort);
            htn.Blackboard.Remove<EntityCoordinates>(NPCBlackboard.FollowTarget);
            CompleteEscortActivity(uid, escort);
            escort.DutyActions++;
            escort.LastDutyActionStatus = $"clear-route dropped {FormatEntityRef(blockerUid)} at dropoff; {clearanceStatus}";
            return true;
        }

        var terminal = RecordClearRouteReleaseFailure(uid, escort, blockerUid);

        TryRequestCrewHelp(
            uid,
            escort,
            $"drop-blocker:{blockerUid}",
            $"dropoff reached but release failed for {FormatEntityRef(blockerUid)}; {clearanceStatus}",
            "\u041f\u043e\u043c\u0435\u0445\u0430 \u043e\u0442\u0442\u0430\u0449\u0435\u043d\u0430. \u041e\u0441\u0432\u043e\u0431\u043e\u0434\u0438\u0442\u0435 \u043c\u0435\u0441\u0442\u043e \u0441\u0431\u0440\u043e\u0441\u0430 \u0438 \u0434\u0435\u0440\u0436\u0438\u0442\u0435 \u043f\u0440\u043e\u0445\u043e\u0434.");
        escort.LastDutyActionStatus = terminal
            ? $"clear-route terminal release failure {FormatEntityRef(blockerUid)}; {clearanceStatus}"
            : $"clear-route drop blocked {FormatEntityRef(blockerUid)}; " +
              $"attempt={escort.ClearRouteReleaseAttempts}; {clearanceStatus}";
        return true;
    }

    private bool RecordClearRouteReleaseFailure(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        EntityUid blocker)
    {
        if (escort.ClearRouteReleaseAttemptTarget != blocker)
        {
            escort.ClearRouteReleaseAttemptTarget = blocker;
            escort.ClearRouteReleaseAttempts = 0;
            escort.NextClearRouteReleaseAttemptAt = TimeSpan.Zero;
        }

        escort.ClearRouteReleaseAttempts++;
        var profile = TryComp<LuaMRescueActivityCarrierComponent>(uid, out var carrier)
            ? carrier.ActivityRoleProfile
            : LuaMRescueRoleProfile.CreateDefault(GetActivityRole(escort.Role));
        if (escort.ClearRouteReleaseAttempts >= Math.Max(1, profile.MaxAttempts))
        {
            escort.NextClearRouteReleaseAttemptAt = TimeSpan.Zero;
            TerminalizeEscortAction(uid, escort, LuaMRescueFailureReason.ActionCancelled);
            return true;
        }

        escort.NextClearRouteReleaseAttemptAt = _timing.CurTime +
            CalculateEscortActivityBackoff(profile, escort.ClearRouteReleaseAttempts);
        return false;
    }

    private static void ResetClearRouteReleaseAttempts(LuaMRescueEscortComponent escort)
    {
        escort.ClearRouteReleaseAttemptTarget = null;
        escort.ClearRouteReleaseAttempts = 0;
        escort.NextClearRouteReleaseAttemptAt = TimeSpan.Zero;
    }

    private bool IsRouteBlockerClearOfRescueCorridor(
        LuaMRescueEscortComponent escort,
        EntityUid blockerUid,
        out string status)
    {
        status = "clearance unknown";

        if (!TryGetRouteClearanceAnchor(escort, out var anchor, out var anchorLabel) ||
            !TryComp<TransformComponent>(blockerUid, out var blockerXform) ||
            !TryComp<TransformComponent>(anchor, out var anchorXform) ||
            blockerXform.MapID != anchorXform.MapID)
        {
            return false;
        }

        var distance = (blockerXform.MapPosition.Position - anchorXform.MapPosition.Position).Length();
        status = $"clearance {distance:0.0}/{RouteBlockerReleaseDistance:0.0}m from {anchorLabel}";
        return distance >= RouteBlockerReleaseDistance;
    }

    private bool TryGetRouteClearanceAnchor(
        LuaMRescueEscortComponent escort,
        out EntityUid anchor,
        out string label)
    {
        if (ValidOrNull(escort.SceneAnchor) is { Valid: true } sceneAnchor)
        {
            anchor = sceneAnchor;
            label = "scene-anchor";
            return true;
        }

        if (ValidOrNull(escort.Patient) is { Valid: true } patient)
        {
            anchor = patient;
            label = "patient";
            return true;
        }

        if (ValidOrNull(escort.Leader) is { Valid: true } leader)
        {
            anchor = leader;
            label = "leader";
            return true;
        }

        if (ValidOrNull(escort.ShuttleAnchor) is { Valid: true } shuttleAnchor)
        {
            anchor = shuttleAnchor;
            label = "shuttle-anchor";
            return true;
        }

        if (ValidOrNull(escort.Shuttle) is { Valid: true } shuttle)
        {
            anchor = shuttle;
            label = "shuttle";
            return true;
        }

        anchor = default;
        label = "none";
        return false;
    }

    private bool TrySetClearRouteDropoffTarget(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        HTNComponent htn,
        EntityUid blockerUid)
    {
        if (Deleted(blockerUid) ||
            !TryComp<TransformComponent>(blockerUid, out var blockerXform))
        {
            return false;
        }

        var direction = GetClearRouteDropoffDirection(escort, blockerXform);
        if (direction.LengthSquared() <= 0.001f)
            return false;

        var dropoff = blockerXform.Coordinates.Offset(direction * RouteBlockerDropoffDistance);
        _npc.SetBlackboard(uid, NPCBlackboard.FollowTarget, dropoff, htn);
        _npc.SetBlackboard(uid, "FollowCloseRange", 0.75f, htn);
        _npc.SetBlackboard(uid, "FollowRange", 1.5f, htn);
        _npc.WakeNPC(uid, htn);
        return true;
    }

    private Vector2 GetClearRouteDropoffDirection(
        LuaMRescueEscortComponent escort,
        TransformComponent blockerXform)
    {
        var anchor = ValidOrNull(escort.SceneAnchor) ??
                     ValidOrNull(escort.Patient) ??
                     ValidOrNull(escort.Leader) ??
                     ValidOrNull(escort.ShuttleAnchor) ??
                     ValidOrNull(escort.Shuttle);

        if (anchor is { Valid: true } anchorUid &&
            TryComp<TransformComponent>(anchorUid, out var anchorXform) &&
            anchorXform.MapID == blockerXform.MapID)
        {
            var away = blockerXform.MapPosition.Position - anchorXform.MapPosition.Position;
            if (away.LengthSquared() > 0.001f)
                return Vector2.Normalize(away);
        }

        return escort.Role switch
        {
            LuaMRescueEscortRole.Tourniquet => Vector2.UnitY,
            LuaMRescueEscortRole.Zaslon => -Vector2.UnitY,
            LuaMRescueEscortRole.Kostyl => -Vector2.UnitX,
            _ => Vector2.UnitX,
        };
    }

    private bool ShouldEscortAssistPatientPull(
        LuaMRescueEscortComponent escort,
        LuaMRescueEscortDuty duty)
    {
        return escort.Role == LuaMRescueEscortRole.Kostyl &&
            duty == LuaMRescueEscortDuty.PatientSupport &&
            IsKostylEvacuationSupport(escort);
    }

    private bool IsKostylEvacuationSupport(LuaMRescueEscortComponent escort)
    {
        if (escort.SortiePlan == LuaMRescueSortiePlan.EvacuatePatient)
            return true;

        return escort.Leader is { Valid: true } leader &&
            !Deleted(leader) &&
            TryComp<LuaMRescueAgentComponent>(leader, out var rescue) &&
            (rescue.TaskStage is LuaMRescueTaskStage.EvacuatingPatient or LuaMRescueTaskStage.DeliveringPatient ||
             rescue.EvacuatingTarget is { Valid: true });
    }

    private void TryRunPatientAssistAction(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        EntityUid? followTarget)
    {
        var patient = ValidOrNull(escort.Patient) ?? ValidOrNull(followTarget);
        if (patient is not { Valid: true } patientUid)
        {
            escort.LastDutyActionStatus = "patient-assist no patient target";
            return;
        }

        if (!_activity.IsEligibleRescuePatient(
                uid,
                patientUid,
                LuaMRescuePatientRequestKind.AutomaticEvacuation,
                manualOverride: false,
                out var eligibilityFailure))
        {
            TerminalizeEscortAction(uid, escort, eligibilityFailure);
            escort.LastDutyActionStatus =
                $"patient-assist invalid patient {FormatEntityRef(patientUid)}: {eligibilityFailure}";
            return;
        }

        if (_container.IsEntityOrParentInContainer(patientUid) ||
            !_container.IsInSameOrNoContainer((uid, null, null), (patientUid, null, null)))
        {
            TerminalizeEscortAction(uid, escort, LuaMRescueFailureReason.ContainedTarget);
            escort.LastDutyActionStatus =
                $"patient-assist contained patient {FormatEntityRef(patientUid)}";
            return;
        }

        if (escort.PatientAssistAttemptTarget != patientUid)
            ResetPatientAssistAttempts(escort);
        if (escort.PatientAssistAttemptTarget == patientUid &&
            escort.NextPatientAssistAttemptAt > _timing.CurTime)
        {
            escort.LastDutyActionStatus =
                $"patient-assist backoff {FormatEntityRef(patientUid)} until " +
                $"{escort.NextPatientAssistAttemptAt.TotalSeconds:0.0}s";
            return;
        }

        if (TryComp<BuckleComponent>(patientUid, out var buckle) &&
            buckle.BuckledTo is { Valid: true })
        {
            escort.LastDutyActionStatus = $"patient-assist already buckled {FormatEntityRef(patientUid)}";
            return;
        }

        if (!TryComp<PullableComponent>(patientUid, out var pullable))
        {
            TerminalizeEscortAction(uid, escort, LuaMRescueFailureReason.TargetNotPullable);
            TryRequestCrewHelp(
                uid,
                escort,
                $"stretcher-needed:{patientUid}",
                $"patient not pullable; bring stretcher for {FormatEntityRef(patientUid)}",
                "\u041d\u0443\u0436\u043d\u044b \u043d\u043e\u0441\u0438\u043b\u043a\u0438 \u0438\u043b\u0438 \u043a\u0430\u0442\u0430\u043b\u043a\u0430 \u043a \u043f\u0430\u0446\u0438\u0435\u043d\u0442\u0443. \u0410\u0439\u0431\u043e\u043b\u0438\u0442 \u0433\u043e\u0442\u043e\u0432\u0438\u0442 \u044d\u0432\u0430\u043a\u0443\u0430\u0446\u0438\u044e.");
            escort.LastDutyActionStatus = $"patient-assist patient not pullable {FormatEntityRef(patientUid)}";
            return;
        }

        if (pullable.Puller == uid)
        {
            var evacuationTarget = ValidOrNull(escort.ShuttleAnchor) ??
                                   ValidOrNull(escort.Shuttle) ??
                                   ValidOrNull(escort.Leader);
            if (evacuationTarget is { Valid: true } handoffTarget &&
                IsWithinRange(patientUid, handoffTarget, EscortPatientAssistRange))
            {
                if (escort.PatientHandoffAttemptTarget != patientUid)
                    ResetPatientHandoffAttempts(escort);
                if (escort.PatientHandoffAttemptTarget == patientUid &&
                    escort.NextPatientHandoffAttemptAt > _timing.CurTime)
                {
                    escort.LastDutyActionStatus =
                        $"patient-assist handoff backoff {FormatEntityRef(patientUid)} until " +
                        $"{escort.NextPatientHandoffAttemptAt.TotalSeconds:0.0}s";
                    return;
                }

                if (_pulling.TryStopPull(patientUid, pullable, uid))
                {
                    ResetPatientHandoffAttempts(escort);
                    ResetPatientAssistAttempts(escort);
                    CompleteEscortActivity(uid, escort);
                    escort.DutyActions++;
                    escort.LastDutyActionStatus =
                        $"patient-assist handoff complete {FormatEntityRef(patientUid)} at {FormatEntityRef(handoffTarget)}";
                    return;
                }

                var terminalHandoff = RecordPatientHandoffFailure(uid, escort, patientUid);
                TryRequestCrewHelp(
                    uid,
                    escort,
                    $"handoff-blocked:{patientUid}",
                    $"patient handoff release blocked for {FormatEntityRef(patientUid)}",
                    "Пациент у шаттла, но передача заблокирована. Нужна помощь с безопасным освобождением.");
                escort.LastDutyActionStatus = terminalHandoff
                    ? $"patient-assist terminal handoff failure {FormatEntityRef(patientUid)}"
                    : $"patient-assist handoff release blocked {FormatEntityRef(patientUid)}; " +
                      $"attempt={escort.PatientHandoffAttempts}; retryAt={escort.NextPatientHandoffAttemptAt.TotalSeconds:0.0}s";
                return;
            }

            TryRequestCrewHelp(
                uid,
                escort,
                $"stretcher-moving:{patientUid}",
                $"escort pulling patient {FormatEntityRef(patientUid)}",
                "\u041f\u043e\u043c\u043e\u0433\u0438\u0442\u0435 \u0441 \u043d\u043e\u0441\u0438\u043b\u043a\u0430\u043c\u0438: \u0434\u0435\u0440\u0436\u0438\u0442\u0435 \u0434\u0432\u0435\u0440\u0438 \u043e\u0442\u043a\u0440\u044b\u0442\u044b\u043c\u0438 \u0438 \u043c\u0430\u0440\u0448\u0440\u0443\u0442 \u0447\u0438\u0441\u0442\u044b\u043c.");
            escort.LastDutyActionStatus = evacuationTarget is { Valid: true } target
                ? $"patient-assist escorting {FormatEntityRef(patientUid)} to {FormatEntityRef(target)}"
                : $"patient-assist holding {FormatEntityRef(patientUid)}";
            return;
        }

        if (pullable.Puller is { Valid: true } puller && !Deleted(puller))
        {
            TryRequestCrewHelp(
                uid,
                escort,
                $"stretcher-coordinate:{patientUid}",
                $"patient already pulled by {FormatEntityRef(puller)}",
                "\u041d\u0435 \u0434\u0435\u0440\u0433\u0430\u0439\u0442\u0435 \u043f\u0430\u0446\u0438\u0435\u043d\u0442\u0430. \u041f\u043e\u043c\u043e\u0433\u0438\u0442\u0435 \u0441 \u043d\u043e\u0441\u0438\u043b\u043a\u0430\u043c\u0438 \u0438 \u043e\u0441\u0432\u043e\u0431\u043e\u0434\u0438\u0442\u0435 \u043f\u0443\u0442\u044c \u043a \u0448\u0430\u0442\u0442\u043b\u0443.");
            escort.LastDutyActionStatus = $"patient-assist already pulled by {FormatEntityRef(puller)}";
            return;
        }

        if (!TryComp<PullerComponent>(uid, out var pullerComp))
        {
            TerminalizeEscortAction(uid, escort, LuaMRescueFailureReason.NoFreeHand);
            escort.LastDutyActionStatus = "patient-assist escort cannot pull";
            return;
        }

        if (!IsWithinRange(uid, patientUid, EscortPatientAssistRange))
        {
            TryRequestCrewHelp(
                uid,
                escort,
                $"stretcher-ready:{patientUid}",
                $"moving to patient assist {FormatEntityRef(patientUid)}",
                "\u0414\u0430\u0439\u0442\u0435 \u043f\u0440\u043e\u0445\u043e\u0434 \u043a \u043f\u0430\u0446\u0438\u0435\u043d\u0442\u0443 \u0438 \u0433\u043e\u0442\u043e\u0432\u044c\u0442\u0435 \u043d\u043e\u0441\u0438\u043b\u043a\u0438.");
            escort.LastDutyActionStatus = $"patient-assist moving to {FormatEntityRef(patientUid)}";
            return;
        }

        if (!TryPrepareEscortPull(uid, escort, out var pullFailure, out var pullPreparation))
        {
            TerminalizeEscortAction(uid, escort, pullFailure);
            escort.LastDutyActionStatus = $"patient-assist pull blocked: {pullPreparation}";
            return;
        }

        if (_pulling.TryStartPull(uid, patientUid, pullerComp, pullable))
        {
            ResetPatientAssistAttempts(escort);
            escort.DutyActions++;
            TryRequestCrewHelp(
                uid,
                escort,
                $"stretcher-escort:{patientUid}",
                $"moving patient {FormatEntityRef(patientUid)} to shuttle",
                "\u041f\u0430\u0446\u0438\u0435\u043d\u0442 \u0432 \u044d\u0432\u0430\u043a\u0443\u0430\u0446\u0438\u0438. \u0414\u0435\u0440\u0436\u0438\u0442\u0435 \u0434\u0432\u0435\u0440\u0438 \u0438 \u043f\u0440\u043e\u0445\u043e\u0434 \u043a \u0448\u0430\u0442\u0442\u043b\u0443 \u043e\u0442\u043a\u0440\u044b\u0442\u044b\u043c\u0438.");
            escort.LastDutyActionStatus = $"patient-assist pulling {FormatEntityRef(patientUid)}";
            return;
        }

        var terminal = RecordPatientAssistFailure(
            uid,
            escort,
            patientUid,
            LuaMRescueFailureReason.ActionCancelled);

        TryRequestCrewHelp(
            uid,
            escort,
            $"stretcher-blocked:{patientUid}",
            $"patient assist pull blocked for {FormatEntityRef(patientUid)}",
            "\u041d\u0443\u0436\u043d\u0430 \u0440\u0443\u0447\u043d\u0430\u044f \u043f\u043e\u043c\u043e\u0449\u044c \u0441 \u043d\u043e\u0441\u0438\u043b\u043a\u0430\u043c\u0438. \u041f\u043e\u0434\u043e\u0439\u0434\u0438\u0442\u0435 \u043a \u043f\u0430\u0446\u0438\u0435\u043d\u0442\u0443.");
        escort.LastDutyActionStatus = terminal
            ? $"patient-assist terminal pull failure {FormatEntityRef(patientUid)}"
            : $"patient-assist pull blocked {FormatEntityRef(patientUid)}; " +
              $"attempt={escort.PatientAssistAttempts}; retryAt={escort.NextPatientAssistAttemptAt.TotalSeconds:0.0}s";
    }

    private bool RecordPatientHandoffFailure(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        EntityUid patient)
    {
        if (escort.PatientHandoffAttemptTarget != patient)
        {
            escort.PatientHandoffAttemptTarget = patient;
            escort.PatientHandoffAttempts = 0;
            escort.NextPatientHandoffAttemptAt = TimeSpan.Zero;
        }

        escort.PatientHandoffAttempts++;
        var profile = TryComp<LuaMRescueActivityCarrierComponent>(uid, out var carrier)
            ? carrier.ActivityRoleProfile
            : LuaMRescueRoleProfile.CreateDefault(LuaMRescueRole.Kostyl);
        if (escort.PatientHandoffAttempts >= Math.Max(1, profile.MaxAttempts))
        {
            escort.NextPatientHandoffAttemptAt = TimeSpan.Zero;
            TerminalizeEscortAction(uid, escort, LuaMRescueFailureReason.ActionCancelled);
            return true;
        }

        escort.NextPatientHandoffAttemptAt = _timing.CurTime +
            CalculateEscortActivityBackoff(profile, escort.PatientHandoffAttempts);
        return false;
    }

    private static void ResetPatientHandoffAttempts(LuaMRescueEscortComponent escort)
    {
        escort.PatientHandoffAttemptTarget = null;
        escort.PatientHandoffAttempts = 0;
        escort.NextPatientHandoffAttemptAt = TimeSpan.Zero;
    }

    private void CompleteEscortActivity(EntityUid uid, LuaMRescueEscortComponent escort)
    {
        if (!TryComp<LuaMRescueActivityCarrierComponent>(uid, out var carrier) ||
            carrier.ActivityContext.TerminalStatus != LuaMRescueTerminalStatus.Active)
        {
            return;
        }

        var current = carrier.ActivityContext;
        current.TerminalStatus = LuaMRescueTerminalStatus.Succeeded;
        current.Blocked = false;
        current.FailureReason = LuaMRescueFailureReason.None;
        current.RetryNotBefore = TimeSpan.Zero;
        current.LastProgressAt = _timing.CurTime;
        current.LastTransitionAt = _timing.CurTime;
        ResetEscortTerminalRecovery(carrier, "activity-completed");
        UpdateEscortActivityTelemetry(uid, carrier, escort, ValidOrNull(escort.CurrentFollowTarget));
    }

    private bool RecordPatientAssistFailure(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        EntityUid patient,
        LuaMRescueFailureReason reason)
    {
        if (escort.PatientAssistAttemptTarget != patient)
        {
            escort.PatientAssistAttemptTarget = patient;
            escort.PatientAssistAttempts = 0;
            escort.NextPatientAssistAttemptAt = TimeSpan.Zero;
        }

        var now = _timing.CurTime;
        if (escort.NextPatientAssistAttemptAt > now)
            return false;

        escort.PatientAssistAttempts++;
        var profile = TryComp<LuaMRescueActivityCarrierComponent>(uid, out var carrier)
            ? carrier.ActivityRoleProfile
            : LuaMRescueRoleProfile.CreateDefault(LuaMRescueRole.Kostyl);
        if (escort.PatientAssistAttempts >= Math.Max(1, profile.MaxAttempts))
        {
            escort.NextPatientAssistAttemptAt = TimeSpan.Zero;
            TerminalizeEscortAction(uid, escort, reason);
            return true;
        }

        escort.NextPatientAssistAttemptAt = now +
            CalculateEscortActivityBackoff(profile, escort.PatientAssistAttempts);
        return false;
    }

    private static void ResetPatientAssistAttempts(LuaMRescueEscortComponent escort)
    {
        escort.PatientAssistAttemptTarget = null;
        escort.PatientAssistAttempts = 0;
        escort.NextPatientAssistAttemptAt = TimeSpan.Zero;
    }

    private void TerminalizeEscortAction(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        LuaMRescueFailureReason reason)
    {
        if (!TryComp<LuaMRescueActivityCarrierComponent>(uid, out var carrier) ||
            IsEscortActivityTerminal(carrier.ActivityContext.TerminalStatus))
        {
            return;
        }

        var current = carrier.ActivityContext;
        current.Attempts = Math.Max(current.Attempts, Math.Max(1, carrier.ActivityRoleProfile.MaxAttempts));
        SetEscortActivityTerminal(
            current,
            LuaMRescueTerminalStatus.Blocked,
            reason,
            current.Fallback,
            _timing.CurTime);
        ArmEscortTerminalRecovery(
            uid,
            carrier,
            current,
            current.Destination is { } destination
                ? ValidOrNull(destination.EntityId)
                : ValidOrNull(escort.CurrentFollowTarget),
            _timing.CurTime);
        ReportEscortActivityTerminal(uid, escort, current);
        UpdateEscortActivityTelemetry(uid, carrier, escort, ValidOrNull(escort.CurrentFollowTarget));
    }

    private static bool ShouldEscortCrowdControl(
        LuaMRescueEscortComponent escort,
        LuaMRescueEscortDuty duty)
    {
        return escort.Role == LuaMRescueEscortRole.Tourniquet &&
            duty == LuaMRescueEscortDuty.CrowdControl;
    }

    private void TryRunCrowdControlAction(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        EntityUid? followTarget)
    {
        var target = ValidOrNull(escort.CrowdTarget) ?? ValidOrNull(followTarget);
        if (target is not { Valid: true } crowdUid)
        {
            if (HasCrowdPressure(escort))
            {
                TryRequestCrewHelp(
                    uid,
                    escort,
                    "step-away",
                    $"crowd pressure without target; crowd={escort.NearbyCrowd}",
                    "\u041e\u0442\u043e\u0439\u0434\u0438\u0442\u0435 \u043e\u0442 \u043f\u0430\u0446\u0438\u0435\u043d\u0442\u0430. \u041c\u0435\u0434\u0438\u0446\u0438\u043d\u0441\u043a\u043e\u0439 \u0433\u0440\u0443\u043f\u043f\u0435 \u043d\u0443\u0436\u043d\u043e \u043c\u0435\u0441\u0442\u043e.");
            }

            escort.LastDutyActionStatus = "crowd-control no crowd target";
            return;
        }

        if (!IsWithinRange(uid, crowdUid, EscortCrowdControlRange))
        {
            TryRequestCrewHelp(
                uid,
                escort,
                $"step-away:{crowdUid}",
                $"crowd target near corridor {FormatEntityRef(crowdUid)}",
                "\u041e\u0442\u043e\u0439\u0434\u0438\u0442\u0435 \u043e\u0442 \u043a\u043e\u0440\u0438\u0434\u043e\u0440\u0430. \u041f\u0443\u0442\u044c \u043a \u0448\u0430\u0442\u0442\u043b\u0443 \u0434\u043e\u043b\u0436\u0435\u043d \u0431\u044b\u0442\u044c \u0447\u0438\u0441\u0442\u044b\u043c.");
            escort.LastDutyActionStatus = $"crowd-control moving to {FormatEntityRef(crowdUid)}";
            return;
        }

        var line = GetCrowdControlLine(escort);
        TryRequestCrewHelp(
            uid,
            escort,
            $"step-away:{crowdUid}",
            $"crowd control warning {FormatEntityRef(crowdUid)}; crowd={escort.NearbyCrowd}",
            line);

        escort.DutyActions++;
        escort.LastDutyActionStatus = $"crowd-control warning {FormatEntityRef(crowdUid)}";
    }

    private static string GetCrowdControlLine(LuaMRescueEscortComponent escort)
    {
        return escort.NearbyCrowd >= CrowdPressureThreshold
            ? "\u041c\u0435\u0434\u0438\u0446\u0438\u043d\u0441\u043a\u0438\u0439 \u043a\u043e\u0440\u0438\u0434\u043e\u0440. \u041e\u0442\u043e\u0439\u0434\u0438\u0442\u0435 \u043e\u0442 \u043f\u0430\u0446\u0438\u0435\u043d\u0442\u0430."
            : "\u0414\u0435\u0440\u0436\u0438\u0442\u0435 \u043f\u0443\u0442\u044c \u043a \u0448\u0430\u0442\u0442\u043b\u0443 \u0447\u0438\u0441\u0442\u044b\u043c.";
    }

    private bool UpdateSortiePlan(
        LuaMRescueTeamComponent team,
        LuaMRescueTeamPhase phase,
        EntityUid? patient)
    {
        var (candidatePlan, candidateStatus) = SelectSortiePlan(team, phase, patient);
        var now = _timing.CurTime;
        var changed = false;

        if (candidatePlan == team.SortiePlan)
        {
            if (team.PendingSortiePlan != candidatePlan)
            {
                team.PendingSortiePlan = candidatePlan;
                team.PendingSortiePlanSince = team.SortiePlanUpdatedAt;
                changed = true;
            }

            changed |= SetSortiePlanStatus(team, BuildActiveSortiePlanStatus(team, candidateStatus));
            return changed;
        }

        if (IsUrgentSortiePlan(candidatePlan, phase, patient))
        {
            changed |= CommitSortiePlan(team, candidatePlan, now);
            changed |= SetSortiePlanStatus(team, BuildActiveSortiePlanStatus(team, $"immediate transition; {candidateStatus}"));
            return changed;
        }

        if (team.PendingSortiePlan != candidatePlan)
        {
            team.PendingSortiePlan = candidatePlan;
            team.PendingSortiePlanSince = now;
            changed = true;
        }

        if ((now - team.PendingSortiePlanSince).TotalSeconds >= SortiePlanHoldSeconds)
        {
            changed |= CommitSortiePlan(team, candidatePlan, now);
            changed |= SetSortiePlanStatus(team, BuildActiveSortiePlanStatus(team, $"confirmed transition; {candidateStatus}"));
            return changed;
        }

        changed |= SetSortiePlanStatus(team, BuildHoldingSortiePlanStatus(team, candidatePlan, candidateStatus, now));
        return changed;
    }

    private static bool CommitSortiePlan(
        LuaMRescueTeamComponent team,
        LuaMRescueSortiePlan plan,
        TimeSpan now)
    {
        var changed = false;

        if (team.SortiePlan != plan)
        {
            team.SortiePlan = plan;
            team.SortiePlanUpdatedAt = now;
            team.SortiePlanTransitions++;
            changed = true;
        }

        if (team.PendingSortiePlan != plan)
        {
            team.PendingSortiePlan = plan;
            changed = true;
        }

        if (team.PendingSortiePlanSince != now)
        {
            team.PendingSortiePlanSince = now;
            changed = true;
        }

        return changed;
    }

    private static bool SetSortiePlanStatus(LuaMRescueTeamComponent team, string status)
    {
        if (string.Equals(team.LastSortiePlanStatus, status, StringComparison.Ordinal))
            return false;

        team.LastSortiePlanStatus = status;
        return true;
    }

    private static string BuildActiveSortiePlanStatus(
        LuaMRescueTeamComponent team,
        string candidateStatus)
    {
        return $"plan {FormatPlan(team.SortiePlan)} active; transitions={team.SortiePlanTransitions}; {candidateStatus}";
    }

    private static string BuildHoldingSortiePlanStatus(
        LuaMRescueTeamComponent team,
        LuaMRescueSortiePlan candidatePlan,
        string candidateStatus,
        TimeSpan now)
    {
        var pendingSeconds = GetElapsedSeconds(team.PendingSortiePlanSince, now);
        return $"plan {FormatPlan(team.SortiePlan)} holding candidate {FormatPlan(candidatePlan)} " +
            $"{pendingSeconds}s/{SortiePlanHoldSeconds:0}s; transitions={team.SortiePlanTransitions}; {candidateStatus}";
    }

    private static bool IsUrgentSortiePlan(
        LuaMRescueSortiePlan plan,
        LuaMRescueTeamPhase phase,
        EntityUid? patient)
    {
        if (phase == LuaMRescueTeamPhase.ReturnOrExtract)
            return true;

        if (patient is not { Valid: true })
            return true;

        if (plan == LuaMRescueSortiePlan.ThreatScreen)
            return true;

        return plan == LuaMRescueSortiePlan.ClearRoute &&
            phase is LuaMRescueTeamPhase.PrepareEvacuation or LuaMRescueTeamPhase.EvacuateToShuttle;
    }

    private int GetSortiePlanAgeSeconds(LuaMRescueTeamComponent team)
    {
        return GetElapsedSeconds(team.SortiePlanUpdatedAt, _timing.CurTime);
    }

    private int GetEscortDutyAgeSeconds(LuaMRescueEscortComponent escort)
    {
        return GetElapsedSeconds(escort.DutyUpdatedAt, _timing.CurTime);
    }

    private static int GetElapsedSeconds(TimeSpan startedAt, TimeSpan now)
    {
        return Math.Max(0, (int) (now - startedAt).TotalSeconds);
    }

    private void SyncEscortContextFromLeader(LuaMRescueEscortComponent escort)
    {
        if (escort.Leader is not { Valid: true } leader ||
            Deleted(leader) ||
            !IsLivingFormationEntity(leader))
        {
            escort.Leader = null;
            escort.Patient = null;
            escort.SceneAnchor = null;
            escort.ThreatTarget = null;
            escort.CrowdTarget = null;
            escort.RouteBlockerTarget = null;
            escort.NearbyHostiles = 0;
            escort.NearbyCombatants = 0;
            escort.NearbyCrowd = 0;
            escort.NearbyBlockers = 0;
            escort.LastSceneStatus = "scene clear";
            escort.LastMemoryDigest = "memory clear";
            escort.SortiePlan = LuaMRescueSortiePlan.Standby;
            escort.RecentThreatMemories = 0;
            escort.RecentCrowdMemories = 0;
            escort.RecentRouteMemories = 0;
            return;
        }

        if (TryComp<LuaMRescueTeamComponent>(leader, out var team))
        {
            escort.TeamId = team.TeamId;
            escort.Patient = ValidOrNull(team.Patient);
            escort.Shuttle = ValidOrNull(team.Shuttle);
            escort.ShuttleAnchor = ValidOrNull(team.ShuttleAnchor);
            escort.SceneAnchor = ValidOrNull(team.SceneAnchor);
            escort.ThreatTarget = ValidOrNull(team.ThreatTarget);
            escort.CrowdTarget = ValidOrNull(team.CrowdTarget);
            escort.RouteBlockerTarget = ValidOrNull(team.RouteBlockerTarget);
            escort.NearbyHostiles = team.NearbyHostiles;
            escort.NearbyCombatants = team.NearbyCombatants;
            escort.NearbyCrowd = team.NearbyCrowd;
            escort.NearbyBlockers = team.NearbyBlockers;
            escort.LastSceneStatus = team.LastSceneStatus;
            escort.LastMemoryDigest = team.LastMemoryDigest;
            escort.SortiePlan = team.SortiePlan;
            escort.RecentThreatMemories = team.RecentThreatMemories;
            escort.RecentCrowdMemories = team.RecentCrowdMemories;
            escort.RecentRouteMemories = team.RecentRouteMemories;
            return;
        }

        if (TryComp<LuaMRescueAgentComponent>(leader, out var rescue))
        {
            escort.Patient = GetActiveRescuePatient(rescue);
            escort.Shuttle = ValidOrNull(rescue.AssignedShuttle);
            escort.ShuttleAnchor = ValidOrNull(rescue.AssignedShuttleAnchor);
            var scene = ScanRescueScene(leader, escort.TeamId, leader, escort.Patient, escort.Shuttle, escort.ShuttleAnchor);
            ApplySceneToEscort(escort, scene);
            escort.SortiePlan = escort.Patient is { Valid: true }
                ? LuaMRescueSortiePlan.ApproachPatient
                : LuaMRescueSortiePlan.Standby;
        }
    }

    private LuaMRescueSceneSnapshot ScanRescueScene(
        EntityUid observer,
        int teamId,
        EntityUid? leader,
        EntityUid? patient,
        EntityUid? shuttle,
        EntityUid? shuttleAnchor)
    {
        var anchor = ValidOrNull(patient) ??
            ValidOrNull(leader) ??
            ValidOrNull(shuttleAnchor) ??
            ValidOrNull(shuttle) ??
            ValidOrNull(observer);

        if (anchor is not { Valid: true } anchorUid)
            return LuaMRescueSceneSnapshot.Clear;

        var origin = Transform(anchorUid).MapPosition;
        var hostileCount = 0;
        var syntheticThreatCount = 0;
        var combatantCount = 0;
        var crowdCount = 0;
        var blockerCount = 0;
        EntityUid? threatTarget = null;
        EntityUid? syntheticThreatTarget = null;
        EntityUid? crowdTarget = null;
        EntityUid? routeBlockerTarget = null;
        var threatDistance = float.MaxValue;
        var syntheticThreatDistance = float.MaxValue;
        var crowdDistance = float.MaxValue;
        var routeBlockerDistance = float.MaxValue;

        _sceneEntities.Clear();
        // Threat knowledge belongs to the observing actor. The patient remains
        // the formation/route anchor, but it must not act as a remote omniscient
        // sensor for an escort on the other side of a wall.
        var observationRange = GetObservationRange(observer);
        _lookup.GetEntitiesInRange(observer, observationRange, _sceneEntities, LookupFlags.Dynamic | LookupFlags.Sundries | LookupFlags.Approximate);

        foreach (var candidate in _sceneEntities)
        {
            if (ShouldIgnoreSceneEntity(candidate, teamId, leader, patient, shuttle, shuttleAnchor))
                continue;

            if (_container.IsEntityOrParentInContainer(candidate) ||
                !_examine.CanExamine(observer, candidate))
            {
                continue;
            }

            if (!TryComp(candidate, out TransformComponent? candidateXform) ||
                candidateXform.MapID != origin.MapId)
            {
                continue;
            }

            var hasMobState = TryComp<MobStateComponent>(candidate, out var mobState);
            if (mobState?.CurrentState == MobState.Dead)
                continue;

            if (hasMobState)
                crowdCount++;

            var hostile = IsHostileToObserver(observer, candidate);
            var syntheticThreat = IsPrioritySyntheticThreat(observer, candidate);
            var activeCombatant = !hostile && !syntheticThreat && IsActiveCombatant(observer, candidate);

            if (hostile || syntheticThreat)
            {
                hostileCount++;
                if (syntheticThreat)
                    syntheticThreatCount++;
            }
            else if (activeCombatant)
                combatantCount++;

            var distance = (candidateXform.MapPosition.Position - origin.Position).LengthSquared();

            if (syntheticThreat &&
                distance < syntheticThreatDistance)
            {
                syntheticThreatDistance = distance;
                syntheticThreatTarget = candidate;
            }

            if (hostile || syntheticThreat || activeCombatant)
            {
                if (distance < threatDistance)
                {
                    threatDistance = distance;
                    threatTarget = candidate;
                }
            }
            else if (hasMobState && distance < crowdDistance)
            {
                crowdDistance = distance;
                crowdTarget = candidate;
            }

            if (IsRouteBlocker(candidate, hasMobState) &&
                IsRouteBlockerWithinRescueCorridor(candidateXform, origin))
            {
                blockerCount++;
                if (distance < routeBlockerDistance)
                {
                    routeBlockerDistance = distance;
                    routeBlockerTarget = candidate;
                }
            }
        }

        _sceneEntities.Clear();

        var summary = BuildSceneSummary(hostileCount, syntheticThreatCount, combatantCount, crowdCount, blockerCount);
        return new LuaMRescueSceneSnapshot(
            anchorUid,
            ValidOrNull(syntheticThreatTarget) ?? ValidOrNull(threatTarget),
            ValidOrNull(crowdTarget),
            ValidOrNull(routeBlockerTarget),
            hostileCount,
            combatantCount,
            crowdCount,
            blockerCount,
            summary);
    }

    private bool UpdateTeamScene(LuaMRescueTeamComponent team, LuaMRescueSceneSnapshot scene)
    {
        var changed = false;

        if (team.SceneAnchor != scene.Anchor)
        {
            team.SceneAnchor = scene.Anchor;
            changed = true;
        }

        if (team.ThreatTarget != scene.ThreatTarget)
        {
            team.ThreatTarget = scene.ThreatTarget;
            changed = true;
        }

        if (team.CrowdTarget != scene.CrowdTarget)
        {
            team.CrowdTarget = scene.CrowdTarget;
            changed = true;
        }

        if (team.RouteBlockerTarget != scene.RouteBlockerTarget)
        {
            team.RouteBlockerTarget = scene.RouteBlockerTarget;
            changed = true;
        }

        if (team.NearbyHostiles != scene.HostileCount)
        {
            team.NearbyHostiles = scene.HostileCount;
            changed = true;
        }

        if (team.NearbyCombatants != scene.CombatantCount)
        {
            team.NearbyCombatants = scene.CombatantCount;
            changed = true;
        }

        if (team.NearbyCrowd != scene.CrowdCount)
        {
            team.NearbyCrowd = scene.CrowdCount;
            changed = true;
        }

        if (team.NearbyBlockers != scene.BlockerCount)
        {
            team.NearbyBlockers = scene.BlockerCount;
            changed = true;
        }

        if (!string.Equals(team.LastSceneStatus, scene.Status, StringComparison.Ordinal))
        {
            team.LastSceneStatus = scene.Status;
            changed = true;
        }

        return changed;
    }

    private void ApplySceneToEscort(LuaMRescueEscortComponent escort, LuaMRescueSceneSnapshot scene)
    {
        escort.SceneAnchor = scene.Anchor;
        escort.ThreatTarget = scene.ThreatTarget;
        escort.CrowdTarget = scene.CrowdTarget;
        escort.RouteBlockerTarget = scene.RouteBlockerTarget;
        escort.NearbyHostiles = scene.HostileCount;
        escort.NearbyCombatants = scene.CombatantCount;
        escort.NearbyCrowd = scene.CrowdCount;
        escort.NearbyBlockers = scene.BlockerCount;
        escort.LastSceneStatus = scene.Status;
        escort.LastMemoryDigest = BuildSceneSnapshotMemoryDigest(scene);
        escort.RecentThreatMemories = scene.HostileCount > 0 || scene.CombatantCount > 0 ? 1 : 0;
        escort.RecentCrowdMemories = scene.CrowdCount >= CrowdPressureThreshold ? 1 : 0;
        escort.RecentRouteMemories = scene.BlockerCount >= RouteBlockerThreshold ? 1 : 0;
    }

    private bool RememberScenePressure(LuaMRescueTeamComponent team, LuaMRescueSceneSnapshot scene)
    {
        var changed = PruneSceneMemory(team);
        var pressure = GetScenePressure(scene);
        if (pressure == LuaMRescueScenePressure.None)
            return RefreshSceneMemoryDigest(team) || changed;

        var now = _timing.CurTime;
        var memory = FindSceneMemoryToReinforce(team, scene, pressure, now);
        if (memory == null)
        {
            if (team.SceneMemory.Count >= SceneMemoryLimit)
                team.SceneMemory.RemoveAt(0);

            memory = new LuaMRescueSceneMemoryEntry
            {
                Pressure = pressure,
                Anchor = scene.Anchor,
                ThreatTarget = scene.ThreatTarget,
                FirstSeen = now,
            };
            team.SceneMemory.Add(memory);
        }
        else
        {
            memory.Observations++;
        }

        memory.ThreatTarget = scene.ThreatTarget ?? memory.ThreatTarget;
        memory.HostileCount = Math.Max(memory.HostileCount, scene.HostileCount);
        memory.CombatantCount = Math.Max(memory.CombatantCount, scene.CombatantCount);
        memory.CrowdCount = Math.Max(memory.CrowdCount, scene.CrowdCount);
        memory.BlockerCount = Math.Max(memory.BlockerCount, scene.BlockerCount);
        memory.LastSeen = now;
        memory.ExpiresAt = now + TimeSpan.FromSeconds(SceneMemoryLifetimeSeconds);
        memory.Status = scene.Status;

        changed = true;
        changed |= RefreshSceneMemoryDigest(team);
        return changed;
    }

    private bool PruneSceneMemory(LuaMRescueTeamComponent team)
    {
        var now = _timing.CurTime;
        var changed = team.SceneMemory.RemoveAll(memory => memory.ExpiresAt <= now) > 0;
        return RefreshSceneMemoryDigest(team) || changed;
    }

    private bool RefreshSceneMemoryDigest(LuaMRescueTeamComponent team)
    {
        var threat = 0;
        var crowd = 0;
        var route = 0;
        LuaMRescueSceneMemoryEntry? last = null;

        foreach (var memory in team.SceneMemory)
        {
            switch (memory.Pressure)
            {
                case LuaMRescueScenePressure.Threat:
                case LuaMRescueScenePressure.Armed:
                    threat++;
                    break;
                case LuaMRescueScenePressure.Crowd:
                    crowd++;
                    break;
                case LuaMRescueScenePressure.Route:
                    route++;
                    break;
            }

            if (last == null || memory.LastSeen > last.LastSeen)
                last = memory;
        }

        var digest = BuildSceneMemoryDigest(threat, crowd, route, last);
        var changed = false;

        if (team.RecentThreatMemories != threat)
        {
            team.RecentThreatMemories = threat;
            changed = true;
        }

        if (team.RecentCrowdMemories != crowd)
        {
            team.RecentCrowdMemories = crowd;
            changed = true;
        }

        if (team.RecentRouteMemories != route)
        {
            team.RecentRouteMemories = route;
            changed = true;
        }

        if (!string.Equals(team.LastMemoryDigest, digest, StringComparison.Ordinal))
        {
            team.LastMemoryDigest = digest;
            changed = true;
        }

        return changed;
    }

    private LuaMRescueSceneMemoryEntry? FindSceneMemoryToReinforce(
        LuaMRescueTeamComponent team,
        LuaMRescueSceneSnapshot scene,
        LuaMRescueScenePressure pressure,
        TimeSpan now)
    {
        LuaMRescueSceneMemoryEntry? best = null;
        foreach (var memory in team.SceneMemory)
        {
            if (memory.Pressure != pressure ||
                now - memory.LastSeen > TimeSpan.FromSeconds(SceneMemoryReinforceSeconds))
            {
                continue;
            }

            if (scene.ThreatTarget is { Valid: true } threat &&
                memory.ThreatTarget == threat)
            {
                return memory;
            }

            if (scene.Anchor is { Valid: true } anchor &&
                memory.Anchor == anchor)
            {
                best = memory;
            }
        }

        return best;
    }

    private static LuaMRescueScenePressure GetScenePressure(LuaMRescueSceneSnapshot scene)
    {
        if (scene.HostileCount > 0)
            return LuaMRescueScenePressure.Threat;

        if (scene.CombatantCount > 0)
            return LuaMRescueScenePressure.Armed;

        if (scene.BlockerCount >= RouteBlockerThreshold)
            return LuaMRescueScenePressure.Route;

        if (scene.CrowdCount >= CrowdPressureThreshold)
            return LuaMRescueScenePressure.Crowd;

        return LuaMRescueScenePressure.None;
    }

    private string BuildSceneMemoryDigest(
        int threat,
        int crowd,
        int route,
        LuaMRescueSceneMemoryEntry? last)
    {
        if (threat == 0 && crowd == 0 && route == 0)
            return "memory clear";

        var lastPressure = last != null
            ? $"{FormatPressure(last.Pressure)} {GetMemoryAgeSeconds(last.LastSeen)}s ago x{last.Observations}"
            : "none";

        return $"recent threat={threat} crowd={crowd} route={route}; last={lastPressure}";
    }

    private static string BuildSceneSnapshotMemoryDigest(LuaMRescueSceneSnapshot scene)
    {
        return GetScenePressure(scene) == LuaMRescueScenePressure.None
            ? "memory clear"
            : $"snapshot {scene.Status}";
    }

    private int GetMemoryAgeSeconds(TimeSpan lastSeen)
    {
        return Math.Max(0, (int) (_timing.CurTime - lastSeen).TotalSeconds);
    }

    private static string FormatPressure(LuaMRescueScenePressure pressure)
    {
        return pressure switch
        {
            LuaMRescueScenePressure.Threat => "threat",
            LuaMRescueScenePressure.Armed => "armed",
            LuaMRescueScenePressure.Route => "route",
            LuaMRescueScenePressure.Crowd => "crowd",
            _ => "none",
        };
    }

    private bool ShouldIgnoreSceneEntity(
        EntityUid candidate,
        int teamId,
        EntityUid? leader,
        EntityUid? patient,
        EntityUid? shuttle,
        EntityUid? shuttleAnchor)
    {
        if (Deleted(candidate) ||
            candidate == leader ||
            candidate == patient ||
            candidate == shuttle ||
            candidate == shuttleAnchor)
        {
            return true;
        }

        if (TryComp<LuaMRescueTeamComponent>(candidate, out var team) &&
            (teamId <= 0 || team.TeamId == teamId))
        {
            return true;
        }

        if (TryComp<LuaMRescueEscortComponent>(candidate, out var escort) &&
            (teamId <= 0 || escort.TeamId == teamId))
        {
            return true;
        }

        return false;
    }

    private bool IsHostileToObserver(EntityUid observer, EntityUid candidate)
    {
        if (!TryComp<NpcFactionMemberComponent>(observer, out var observerFaction) ||
            !TryComp<NpcFactionMemberComponent>(candidate, out var candidateFaction))
        {
            return false;
        }

        if (_factions.IsEntityFriendly((observer, observerFaction), (candidate, candidateFaction)))
            return false;

        return _factions.IsEntityHostile((observer, observerFaction), (candidate, candidateFaction));
    }

    private bool IsActiveCombatant(EntityUid observer, EntityUid candidate)
    {
        if (!TryComp<CombatModeComponent>(candidate, out var combat) ||
            !combat.IsInCombatMode)
        {
            return false;
        }

        return !IsFriendlyToObserver(observer, candidate);
    }

    private bool IsPrioritySyntheticThreat(EntityUid observer, EntityUid candidate)
    {
        if (!IsSyntheticRescueActor(candidate) ||
            IsFriendlyToObserver(observer, candidate))
        {
            return false;
        }

        return IsHostileToObserver(observer, candidate) ||
            IsActiveCombatant(observer, candidate) ||
            HasImmediateRescueSyntheticControl(candidate);
    }

    private bool IsSyntheticRescueActor(EntityUid candidate)
    {
        return HasImmediateRescueSyntheticControl(candidate) ||
            HasComp<SiliconComponent>(candidate) ||
            HasComp<BorgChassisComponent>(candidate) ||
            _tag.HasTag(candidate, BotTag);
    }

    private bool HasImmediateRescueSyntheticControl(EntityUid candidate)
    {
        return HasComp<LuaMAiDroneTaskComponent>(candidate) ||
            HasComp<DroneControlComponent>(candidate) ||
            HasComp<AiRemoteControllerComponent>(candidate);
    }

    private bool IsFriendlyToObserver(EntityUid observer, EntityUid candidate)
    {
        return TryComp<NpcFactionMemberComponent>(observer, out var observerFaction) &&
            TryComp<NpcFactionMemberComponent>(candidate, out var candidateFaction) &&
            _factions.IsEntityFriendly((observer, observerFaction), (candidate, candidateFaction));
    }

    private bool IsRouteBlocker(EntityUid candidate, bool hasMobState)
    {
        if (hasMobState ||
            !TryComp<PhysicsComponent>(candidate, out var physics))
        {
            return false;
        }

        return physics.Hard &&
            physics.CanCollide &&
            physics.BodyType is not BodyType.Static and not BodyType.KinematicController;
    }

    private static bool IsRouteBlockerWithinRescueCorridor(TransformComponent blockerXform, MapCoordinates origin)
    {
        if (blockerXform.MapID != origin.MapId)
            return false;

        return (blockerXform.MapPosition.Position - origin.Position).LengthSquared() < RouteBlockerReleaseDistanceSquared;
    }

    private static bool HasSceneThreat(LuaMRescueEscortComponent escort)
    {
        return escort.ThreatTarget is { Valid: true } ||
            escort.NearbyHostiles > 0 ||
            escort.NearbyCombatants > 0;
    }

    private static bool HasThreatPressure(LuaMRescueEscortComponent escort)
    {
        return HasSceneThreat(escort) || escort.RecentThreatMemories > 0;
    }

    private static bool HasCrowdPressure(LuaMRescueEscortComponent escort)
    {
        return escort.NearbyCrowd >= CrowdPressureThreshold || escort.RecentCrowdMemories > 0;
    }

    private static bool HasRoutePressure(LuaMRescueEscortComponent escort)
    {
        return escort.NearbyBlockers >= RouteBlockerThreshold || escort.RecentRouteMemories > 0;
    }

    private static bool HasSceneThreat(LuaMRescueTeamComponent team)
    {
        return team.ThreatTarget is { Valid: true } ||
            team.NearbyHostiles > 0 ||
            team.NearbyCombatants > 0;
    }

    private static bool HasThreatPressure(LuaMRescueTeamComponent team)
    {
        return HasSceneThreat(team) || team.RecentThreatMemories > 0;
    }

    private static bool HasCrowdPressure(LuaMRescueTeamComponent team)
    {
        return team.NearbyCrowd >= CrowdPressureThreshold || team.RecentCrowdMemories > 0;
    }

    private static bool HasRoutePressure(LuaMRescueTeamComponent team)
    {
        return team.NearbyBlockers >= RouteBlockerThreshold || team.RecentRouteMemories > 0;
    }

    private static string BuildSceneSummary(int hostiles, int syntheticThreats, int combatants, int crowd, int blockers)
    {
        if (syntheticThreats > 0)
            return $"threat synthetic={syntheticThreats} hostiles={hostiles} combatants={combatants} crowd={crowd} blockers={blockers}";

        if (hostiles > 0)
            return $"threat hostiles={hostiles} combatants={combatants} crowd={crowd} blockers={blockers}";

        if (combatants > 0)
            return $"armed pressure combatants={combatants} crowd={crowd} blockers={blockers}";

        if (blockers >= RouteBlockerThreshold)
            return $"route pressure crowd={crowd} blockers={blockers}";

        if (crowd >= CrowdPressureThreshold)
            return $"crowd pressure crowd={crowd} blockers={blockers}";

        return $"scene clear crowd={crowd} blockers={blockers}";
    }

    private static (LuaMRescueSortiePlan Plan, string Status) SelectSortiePlan(
        LuaMRescueTeamComponent team,
        LuaMRescueTeamPhase phase,
        EntityUid? patient)
    {
        if (phase == LuaMRescueTeamPhase.ReturnOrExtract)
        {
            return (LuaMRescueSortiePlan.ReturnToShuttle,
                $"plan return-to-shuttle: authoritative extraction phase; {team.LastSceneStatus}");
        }

        if (patient is not { Valid: true })
        {
            var returnPlan = team.ShuttleAnchor is { Valid: true } || team.Shuttle is { Valid: true }
                ? LuaMRescueSortiePlan.ReturnToShuttle
                : LuaMRescueSortiePlan.Standby;
            return (returnPlan, $"plan {FormatPlan(returnPlan)}: no active patient");
        }

        if (HasThreatPressure(team))
            return (LuaMRescueSortiePlan.ThreatScreen, $"plan threat-screen: hostile or combat pressure; {team.LastSceneStatus}; {team.LastMemoryDigest}");

        if (phase is LuaMRescueTeamPhase.PrepareEvacuation or LuaMRescueTeamPhase.EvacuateToShuttle &&
            HasRoutePressure(team))
        {
            return (LuaMRescueSortiePlan.ClearRoute, $"plan clear-route: evacuation route blocked; {team.LastSceneStatus}; {team.LastMemoryDigest}");
        }

        if (phase == LuaMRescueTeamPhase.Triage)
            return (LuaMRescueSortiePlan.Resupply, $"plan resupply: Aibolit is collecting medical supplies; {team.LastMemoryDigest}");

        if (HasCrowdPressure(team))
            return (LuaMRescueSortiePlan.CrowdControl, $"plan crowd-control: medical scene crowd pressure; {team.LastSceneStatus}; {team.LastMemoryDigest}");

        if (HasRoutePressure(team))
            return (LuaMRescueSortiePlan.ClearRoute, $"plan clear-route: route pressure remembered; {team.LastSceneStatus}; {team.LastMemoryDigest}");

        return phase switch
        {
            LuaMRescueTeamPhase.TreatOnSite => (LuaMRescueSortiePlan.TreatPatient, $"plan treat-patient: on-site treatment; {team.LastSceneStatus}"),
            LuaMRescueTeamPhase.PrepareEvacuation or LuaMRescueTeamPhase.EvacuateToShuttle => (LuaMRescueSortiePlan.EvacuatePatient, $"plan evacuate-patient: move patient to shuttle; {team.LastSceneStatus}"),
            LuaMRescueTeamPhase.SecureScene => (LuaMRescueSortiePlan.SecureScene, $"plan secure-scene: stabilize rescue zone; {team.LastSceneStatus}"),
            LuaMRescueTeamPhase.Handoff => (LuaMRescueSortiePlan.ReturnToShuttle, $"plan return-to-shuttle: handoff recorded; {team.LastHandoffDigest}"),
            LuaMRescueTeamPhase.ReturnOrExtract => (LuaMRescueSortiePlan.ReturnToShuttle, $"plan return-to-shuttle: extraction phase; {team.LastSceneStatus}"),
            _ => (LuaMRescueSortiePlan.ApproachPatient, $"plan approach-patient: reach and assess patient; {team.LastSceneStatus}"),
        };
    }

    private static LuaMRescueEscortDuty? GetPlannedEscortDuty(LuaMRescueEscortComponent escort)
    {
        return escort.SortiePlan switch
        {
            LuaMRescueSortiePlan.ApproachPatient => escort.Role switch
            {
                LuaMRescueEscortRole.Kostyl => LuaMRescueEscortDuty.PatientSupport,
                LuaMRescueEscortRole.Zaslon when HasThreatPressure(escort) => LuaMRescueEscortDuty.ThreatScreen,
                LuaMRescueEscortRole.Tourniquet when HasCrowdPressure(escort) => LuaMRescueEscortDuty.CrowdControl,
                _ => LuaMRescueEscortDuty.SecureScene,
            },
            LuaMRescueSortiePlan.SecureScene => escort.Role switch
            {
                LuaMRescueEscortRole.Kostyl => LuaMRescueEscortDuty.PatientSupport,
                LuaMRescueEscortRole.Zaslon when HasThreatPressure(escort) => LuaMRescueEscortDuty.ThreatScreen,
                LuaMRescueEscortRole.Tourniquet when HasCrowdPressure(escort) => LuaMRescueEscortDuty.CrowdControl,
                _ => LuaMRescueEscortDuty.SecureScene,
            },
            LuaMRescueSortiePlan.ThreatScreen => escort.Role switch
            {
                LuaMRescueEscortRole.Zaslon => LuaMRescueEscortDuty.ThreatScreen,
                LuaMRescueEscortRole.Tourniquet when HasCrowdPressure(escort) => LuaMRescueEscortDuty.CrowdControl,
                LuaMRescueEscortRole.Kostyl => LuaMRescueEscortDuty.PatientSupport,
                _ => LuaMRescueEscortDuty.SecureScene,
            },
            LuaMRescueSortiePlan.CrowdControl => escort.Role switch
            {
                LuaMRescueEscortRole.Tourniquet => LuaMRescueEscortDuty.CrowdControl,
                LuaMRescueEscortRole.Kostyl => LuaMRescueEscortDuty.PatientSupport,
                _ => LuaMRescueEscortDuty.SecureScene,
            },
            LuaMRescueSortiePlan.ClearRoute => escort.Role == LuaMRescueEscortRole.Kostyl
                ? LuaMRescueEscortDuty.PatientSupport
                : LuaMRescueEscortDuty.ClearRoute,
            LuaMRescueSortiePlan.Resupply => escort.Role switch
            {
                LuaMRescueEscortRole.Kostyl => LuaMRescueEscortDuty.PatientSupport,
                LuaMRescueEscortRole.Zaslon when HasThreatPressure(escort) => LuaMRescueEscortDuty.ThreatScreen,
                LuaMRescueEscortRole.Tourniquet when HasCrowdPressure(escort) => LuaMRescueEscortDuty.CrowdControl,
                _ => LuaMRescueEscortDuty.SecureScene,
            },
            LuaMRescueSortiePlan.TreatPatient => escort.Role switch
            {
                LuaMRescueEscortRole.Kostyl => LuaMRescueEscortDuty.PatientSupport,
                LuaMRescueEscortRole.Zaslon when HasThreatPressure(escort) => LuaMRescueEscortDuty.ThreatScreen,
                LuaMRescueEscortRole.Tourniquet when HasCrowdPressure(escort) => LuaMRescueEscortDuty.CrowdControl,
                _ => LuaMRescueEscortDuty.SecureScene,
            },
            LuaMRescueSortiePlan.EvacuatePatient => escort.Role == LuaMRescueEscortRole.Kostyl
                ? LuaMRescueEscortDuty.PatientSupport
                : LuaMRescueEscortDuty.EvacuationCorridor,
            LuaMRescueSortiePlan.ReturnToShuttle => LuaMRescueEscortDuty.ReturnToShuttle,
            _ => null,
        };
    }

    private LuaMRescueEscortDuty GetEscortDuty(EntityUid uid, LuaMRescueEscortComponent escort)
    {
        if (escort.Leader is { Valid: true } emergencyLeader &&
            TryComp<LuaMRescueAgentComponent>(emergencyLeader, out var emergencyRescue) &&
            emergencyRescue.LifeSupportEmergencyActive)
        {
            return LuaMRescueEscortDuty.ReturnToShuttle;
        }

        if (TryComp<LuaMRescueBehaviorAdapterComponent>(uid, out var behavior) &&
            behavior.EscortDutyOverride is { } behaviorDuty)
        {
            return behaviorDuty;
        }

        if (!TryGetEscortFormationAnchor(escort, out _))
            return LuaMRescueEscortDuty.Standby;

        var patient = GetEligibleEscortPatient(uid, escort.Patient);

        if (patient == null)
            return escort.ShuttleAnchor is { Valid: true } || escort.Shuttle is { Valid: true }
                ? LuaMRescueEscortDuty.ReturnToShuttle
                : LuaMRescueEscortDuty.Standby;

        if (GetPlannedEscortDuty(escort) is { } plannedDuty)
            return plannedDuty;

        if (escort.Leader is { Valid: true } leader &&
            TryComp<LuaMRescueAgentComponent>(leader, out var rescue))
        {
            var evacuating = rescue.TaskStage is LuaMRescueTaskStage.EvacuatingPatient or LuaMRescueTaskStage.DeliveringPatient ||
                rescue.EvacuatingTarget is { Valid: true };

            if (HasThreatPressure(escort) &&
                escort.Role == LuaMRescueEscortRole.Zaslon)
            {
                return LuaMRescueEscortDuty.ThreatScreen;
            }

            if (evacuating &&
                HasRoutePressure(escort) &&
                escort.Role != LuaMRescueEscortRole.Kostyl)
            {
                return LuaMRescueEscortDuty.ClearRoute;
            }

            if (HasCrowdPressure(escort) &&
                escort.Role == LuaMRescueEscortRole.Tourniquet)
            {
                return LuaMRescueEscortDuty.CrowdControl;
            }

            if (evacuating)
            {
                return escort.Role switch
                {
                    LuaMRescueEscortRole.Tourniquet => LuaMRescueEscortDuty.EvacuationCorridor,
                    LuaMRescueEscortRole.Kostyl => LuaMRescueEscortDuty.PatientSupport,
                    LuaMRescueEscortRole.Zaslon => LuaMRescueEscortDuty.EvacuationCorridor,
                    _ => LuaMRescueEscortDuty.PatientSupport,
                };
            }

            if (rescue.TaskStage == LuaMRescueTaskStage.TreatingPatient)
            {
                if (HasCrowdPressure(escort) &&
                    escort.Role == LuaMRescueEscortRole.Tourniquet)
                {
                    return LuaMRescueEscortDuty.CrowdControl;
                }

                return escort.Role == LuaMRescueEscortRole.Kostyl
                    ? LuaMRescueEscortDuty.PatientSupport
                    : LuaMRescueEscortDuty.SecureScene;
            }
        }

        if (HasThreatPressure(escort) &&
            escort.Role == LuaMRescueEscortRole.Zaslon)
        {
            return LuaMRescueEscortDuty.ThreatScreen;
        }

        if (HasCrowdPressure(escort) &&
            escort.Role == LuaMRescueEscortRole.Tourniquet)
        {
            return LuaMRescueEscortDuty.CrowdControl;
        }

        return escort.Role == LuaMRescueEscortRole.Kostyl
            ? LuaMRescueEscortDuty.PatientSupport
            : LuaMRescueEscortDuty.SecureScene;
    }

    private EntityUid? GetEscortFollowTarget(EntityUid uid, LuaMRescueEscortComponent escort, LuaMRescueEscortDuty duty)
    {
        var patient = GetEligibleEscortPatient(uid, escort.Patient);
        var leader = GetLivingFormationEntity(escort.Leader);
        var shuttleAnchor = escort.ShuttleAnchor is { Valid: true } anchorUid && !Deleted(anchorUid)
            ? anchorUid
            : (EntityUid?) null;
        var shuttle = escort.Shuttle is { Valid: true } shuttleUid && !Deleted(shuttleUid)
            ? shuttleUid
            : (EntityUid?) null;
        var threat = escort.ThreatTarget is { Valid: true } threatUid && !Deleted(threatUid)
            ? threatUid
            : (EntityUid?) null;
        var crowd = escort.CrowdTarget is { Valid: true } crowdUid && !Deleted(crowdUid)
            ? crowdUid
            : (EntityUid?) null;
        var routeBlocker = escort.RouteBlockerTarget is { Valid: true } blockerUid && !Deleted(blockerUid)
            ? blockerUid
            : (EntityUid?) null;
        var sceneAnchor = GetLivingFormationEntity(escort.SceneAnchor);

        return duty switch
        {
            LuaMRescueEscortDuty.ThreatScreen => GetThreatScreenFollowTarget(uid, escort, threat),
            LuaMRescueEscortDuty.CrowdControl => crowd ?? sceneAnchor ?? patient ?? leader ?? shuttleAnchor ?? shuttle,
            LuaMRescueEscortDuty.ClearRoute => routeBlocker ?? sceneAnchor ?? leader ?? shuttleAnchor ?? patient ?? shuttle,
            LuaMRescueEscortDuty.SecureScene => escort.Role == LuaMRescueEscortRole.Zaslon
                ? threat ?? patient ?? leader ?? shuttleAnchor ?? shuttle
                : leader ?? patient ?? shuttleAnchor ?? shuttle,
            LuaMRescueEscortDuty.PatientSupport => GetPatientSupportFollowTarget(uid, escort, patient, leader, shuttleAnchor, shuttle),
            LuaMRescueEscortDuty.EvacuationCorridor => escort.Role == LuaMRescueEscortRole.Zaslon
                ? threat ?? patient ?? shuttleAnchor ?? leader ?? shuttle
                : leader ?? patient ?? shuttleAnchor ?? shuttle,
            LuaMRescueEscortDuty.ReturnToShuttle => GetEscortReturnFollowTarget(
                uid,
                leader,
                shuttleAnchor,
                shuttle),
            _ => leader ?? shuttleAnchor ?? shuttle,
        };
    }

    private EntityUid? GetEscortReturnFollowTarget(
        EntityUid escort,
        EntityUid? leader,
        EntityUid? shuttleAnchor,
        EntityUid? shuttle)
    {
        // ReturnToShuttle must keep the configured medical destination authoritative.
        // The leader is only a fallback when the team has no usable shuttle anchor.
        var destination = shuttleAnchor ?? shuttle ?? leader;
        if (destination is not { Valid: true } target || Deleted(target))
            return null;

        var sourceMap = Transform(escort).MapID;
        var destinationMap = Transform(target).MapID;
        if (sourceMap == destinationMap)
            return target;

        if (sourceMap == MapId.Nullspace || destinationMap == MapId.Nullspace)
            return null;

        EntityUid? nearest = null;
        var nearestDistance = float.PositiveInfinity;
        var query = AllEntityQuery<GatewayComponent, PortalComponent, TransformComponent>();
        while (query.MoveNext(out var gateway, out var gatewayComp, out var portalComp, out var gatewayXform))
        {
            if (!gatewayComp.Enabled ||
                !portalComp.CanTeleportToOtherMaps ||
                gatewayXform.MapID != sourceMap ||
                !TryComp<LinkedEntityComponent>(gateway, out var sourceLinks) ||
                sourceLinks.LinkedEntities.Count != 1)
            {
                continue;
            }

            var endpoint = sourceLinks.LinkedEntities.First();
            if (!endpoint.Valid ||
                Deleted(endpoint) ||
                !TryComp<GatewayComponent>(endpoint, out var endpointGateway) ||
                !endpointGateway.Enabled ||
                !TryComp<PortalComponent>(endpoint, out var endpointPortal) ||
                !endpointPortal.CanTeleportToOtherMaps ||
                !TryComp<LinkedEntityComponent>(endpoint, out var endpointLinks) ||
                endpointLinks.LinkedEntities.Count != 1 ||
                endpointLinks.LinkedEntities.First() != gateway ||
                Transform(endpoint).MapID != destinationMap)
            {
                continue;
            }

            // At the gateway's non-free navigation polygon stock steering can
            // stop producing a 0.1 m route even though the escort is already
            // physically close enough to enter the portal. Preserve that
            // validated reciprocal endpoint when it is in unobstructed,
            // fixture-aware interaction range so SetEscortFollowTarget can
            // complete the controlled transfer instead of dropping the goal.
            if (_interaction.InRangeUnobstructed(
                    escort,
                    gateway,
                    SharedInteractionSystem.InteractionRange))
            {
                return gateway;
            }

            // Euclidean proximity is not enough: a closer reciprocal gateway
            // behind a sealed wall must not permanently starve a farther,
            // reachable return route.
            // A gateway centre is not a free navigation point. Reachability only
            // needs to prove that the escort can reach the controlled-transfer
            // boundary; SetEscortFollowTarget still publishes a 0.1 m HTN goal.
            var route = _rescueNavigation.ProbeRoute(
                escort,
                gateway,
                SharedInteractionSystem.InteractionRange);
            if (route.State != LuaMRescuePathProbeState.Reachable)
                continue;

            var distance = route.Distance;
            if (distance >= nearestDistance)
                continue;

            nearest = gateway;
            nearestDistance = distance;
        }

        return nearest;
    }

    private EntityUid? GetThreatScreenFollowTarget(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        EntityUid? threat)
    {
        if (threat is { Valid: true } threatUid &&
            !Deleted(threatUid) &&
            IsThreatWithinRescueLeash(uid, escort, threatUid))
        {
            return threatUid;
        }

        return TryGetEscortFormationAnchor(escort, out var formationAnchor)
            ? formationAnchor
            : null;
    }

    private bool IsThreatWithinRescueLeash(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        EntityUid threat)
    {
        var policy = GetEscortRoleProfile(uid, escort).ThreatPolicy;
        if (!policy.EngageHostiles || policy.PursuitLeashRange <= 0f)
            return false;

        return TryGetThreatLeashAnchor(escort, out var anchor) &&
               IsWithinRange(anchor, threat, policy.PursuitLeashRange);
    }

    private bool TryGetThreatLeashAnchor(LuaMRescueEscortComponent escort, out EntityUid anchor)
    {
        return TryGetEscortFormationAnchor(escort, out anchor);
    }

    private bool TryGetEscortFormationAnchor(LuaMRescueEscortComponent escort, out EntityUid anchor)
    {
        if (GetLivingFormationEntity(escort.SceneAnchor) is { Valid: true } sceneAnchor)
        {
            anchor = sceneAnchor;
            return true;
        }

        if (GetLivingFormationEntity(escort.Leader) is { Valid: true } leader)
        {
            anchor = leader;
            return true;
        }

        if (GetLivingFormationEntity(escort.Patient) is { Valid: true } patient)
        {
            anchor = patient;
            return true;
        }

        if (ValidOrNull(escort.ShuttleAnchor) is { Valid: true } shuttleAnchor)
        {
            anchor = shuttleAnchor;
            return true;
        }

        if (ValidOrNull(escort.Shuttle) is { Valid: true } shuttle)
        {
            anchor = shuttle;
            return true;
        }

        anchor = default;
        return false;
    }

    private EntityUid? GetLivingFormationEntity(EntityUid? entity)
    {
        return entity is { Valid: true } uid && IsLivingFormationEntity(uid)
            ? uid
            : null;
    }

    private EntityUid? GetEligibleEscortPatient(EntityUid escortUid, EntityUid? entity)
    {
        if (entity is not { Valid: true } patient || Deleted(patient))
            return null;

        return _activity.IsEligibleRescuePatient(
            escortUid,
            patient,
            LuaMRescuePatientRequestKind.AutomaticEvacuation,
            manualOverride: false,
            out _)
                ? patient
                : null;
    }

    private bool IsLivingFormationEntity(EntityUid uid)
    {
        return !Deleted(uid) &&
               (!TryComp<MobStateComponent>(uid, out var mobState) || mobState.CurrentState != MobState.Dead);
    }

    private EntityUid? GetPatientSupportFollowTarget(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        EntityUid? patient,
        EntityUid? leader,
        EntityUid? shuttleAnchor,
        EntityUid? shuttle)
    {
        if (escort.Role == LuaMRescueEscortRole.Kostyl &&
            IsKostylEvacuationSupport(escort) &&
            IsEscortPullingPatient(uid, patient))
        {
            return shuttleAnchor ?? shuttle ?? leader ?? patient;
        }

        return patient ?? leader ?? shuttleAnchor ?? shuttle;
    }

    private bool IsEscortPullingPatient(EntityUid uid, EntityUid? patient)
    {
        return patient is { Valid: true } patientUid &&
            !Deleted(patientUid) &&
            TryComp<PullerComponent>(uid, out var puller) &&
            puller.Pulling == patientUid;
    }

    private bool IsEscortPullingRouteBlocker(EntityUid uid, LuaMRescueEscortComponent escort)
    {
        return escort.RouteBlockerTarget is { Valid: true } blocker &&
               !Deleted(blocker) &&
               TryComp<PullerComponent>(uid, out var puller) &&
               puller.Pulling == blocker;
    }

    private void SetEscortFollowTarget(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        HTNComponent htn,
        EntityUid? followTarget,
        LuaMRescueEscortDuty duty)
    {
        if (followTarget is not { Valid: true } target ||
            Deleted(target))
        {
            htn.Blackboard.Remove<EntityCoordinates>(NPCBlackboard.FollowTarget);
            return;
        }

        var closeRange = duty switch
        {
            LuaMRescueEscortDuty.ThreatScreen => 2.75f,
            LuaMRescueEscortDuty.CrowdControl => 2.25f,
            LuaMRescueEscortDuty.ClearRoute => 1.5f,
            LuaMRescueEscortDuty.SecureScene => escort.Role == LuaMRescueEscortRole.Zaslon ? 1.0f : 2.0f,
            LuaMRescueEscortDuty.EvacuationCorridor => escort.Role == LuaMRescueEscortRole.Kostyl ? 1.25f : 2.25f,
            LuaMRescueEscortDuty.PatientSupport => 1.25f,
            LuaMRescueEscortDuty.ReturnToShuttle => 2.0f,
            _ => escort.FollowCloseRange,
        };

        var followRange = GetEscortActivityActionRange(uid, escort, duty);
        var returningThroughGateway = duty == LuaMRescueEscortDuty.ReturnToShuttle &&
                                      HasComp<GatewayComponent>(target);
        if (returningThroughGateway)
        {
            closeRange = 0.1f;
            followRange = 0.1f;

            // Use fixture-aware interaction range rather than center distance:
            // the gateway's non-free navigation polygon stops stock steering
            // before a 0.1 m center goal is possible. The unobstructed check
            // also prevents this controlled hand-off through a wall.
            if (_interaction.InRangeUnobstructed(
                    uid,
                    target,
                    SharedInteractionSystem.InteractionRange) &&
                _portal.TryTeleportThroughLinkedPortal(target, uid, ignoreTimeout: true))
            {
                // Shut down the old-map movement operator immediately. The
                // next urgent ReturnToShuttle refresh will bind to the leader
                // or shuttle anchor on the destination map.
                CancelEscortIntent(uid, htn, target);
                escort.LastDutyActionStatus =
                    $"crossed reciprocal return gateway {FormatEntityRef(target)}";
                return;
            }
        }

        if (IsDirectEscortActionDuty(duty) &&
            Transform(uid).Coordinates.TryDistance(EntityManager, Transform(target).Coordinates, out var directDistance) &&
            directDistance <= followRange &&
            !_interaction.InRangeUnobstructed(uid, target, followRange))
        {
            followRange = Math.Min(followRange, 0.25f);
            closeRange = Math.Min(closeRange, followRange);
        }

        _npc.SetBlackboard(
            uid,
            NPCBlackboard.FollowTarget,
            new EntityCoordinates(
                target,
                returningThroughGateway
                    ? Vector2.Zero
                    : GetEscortFormationOffset(uid, escort, duty, target)),
            htn);
        _npc.SetBlackboard(uid, "FollowCloseRange", closeRange, htn);
        _npc.SetBlackboard(uid, "FollowRange", followRange, htn);
        _npc.WakeNPC(uid, htn);
    }

    private static bool IsDirectEscortActionDuty(LuaMRescueEscortDuty duty)
    {
        return duty is LuaMRescueEscortDuty.PatientSupport
            or LuaMRescueEscortDuty.ClearRoute
            or LuaMRescueEscortDuty.CrowdControl
            or LuaMRescueEscortDuty.ThreatScreen;
    }

    private Vector2 GetEscortFormationOffset(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        LuaMRescueEscortDuty duty,
        EntityUid followTarget)
    {
        var directActionTarget = duty switch
        {
            LuaMRescueEscortDuty.ThreatScreen => escort.ThreatTarget == followTarget,
            LuaMRescueEscortDuty.CrowdControl => escort.CrowdTarget == followTarget,
            LuaMRescueEscortDuty.ClearRoute => escort.RouteBlockerTarget == followTarget,
            LuaMRescueEscortDuty.PatientSupport => true,
            _ => false,
        };
        if (directActionTarget)
        {
            return Vector2.Zero;
        }

        var offset = escort.Role switch
        {
            LuaMRescueEscortRole.Tourniquet => new Vector2(0f, 1.5f),
            LuaMRescueEscortRole.Kostyl => new Vector2(-1f, 0f),
            LuaMRescueEscortRole.Zaslon => new Vector2(0f, -1.5f),
            _ => Vector2.Zero,
        };

        var formationRange = GetEscortRoleProfile(uid, escort).ThreatPolicy.FormationRange;
        return offset * (Math.Max(0.05f, formationRange) / 1.75f);
    }

    private LuaMRescueTeamPhase GetTeamPhase(
        LuaMRescueTeamComponent team,
        LuaMRescueAgentComponent rescue,
        EntityUid? patient)
    {
        if (rescue.LifeSupportEmergencyActive)
            return LuaMRescueTeamPhase.ReturnOrExtract;

        if (patient is not { Valid: true } patientUid ||
            Deleted(patientUid))
        {
            if (team.HandoffRecords > 0 &&
                (_timing.CurTime - team.LastHandoffUpdatedAt).TotalSeconds <= HandoffPhaseHoldSeconds)
            {
                return LuaMRescueTeamPhase.Handoff;
            }

            return rescue.AssignedShuttle is { Valid: true }
                ? LuaMRescueTeamPhase.ReturnOrExtract
                : LuaMRescueTeamPhase.Idle;
        }

        return rescue.TaskStage switch
        {
            LuaMRescueTaskStage.TreatingPatient => LuaMRescueTeamPhase.TreatOnSite,
            LuaMRescueTaskStage.EvacuatingPatient => LuaMRescueTeamPhase.PrepareEvacuation,
            LuaMRescueTaskStage.DeliveringPatient => LuaMRescueTeamPhase.EvacuateToShuttle,
            LuaMRescueTaskStage.PickingUpSupply or LuaMRescueTaskStage.VendingSupply => LuaMRescueTeamPhase.Triage,
            LuaMRescueTaskStage.FollowingPatient => LuaMRescueTeamPhase.EnRoute,
            LuaMRescueTaskStage.Standby => LuaMRescueTeamPhase.Dispatch,
            _ => HasComp<MobStateComponent>(patientUid)
                ? LuaMRescueTeamPhase.SecureScene
                : LuaMRescueTeamPhase.EnRoute,
        };
    }

    private static string BuildTeamStatus(
        LuaMRescueTeamComponent team,
        LuaMRescueAgentComponent rescue,
        LuaMRescueTeamPhase phase,
        string returnOrExtractReason)
    {
        return phase switch
        {
            LuaMRescueTeamPhase.TreatOnSite => $"Aibolit treating: {rescue.LastAutoTreatmentStatus}",
            LuaMRescueTeamPhase.PrepareEvacuation => $"preparing evacuation: {rescue.LastAutoEvacuationStatus}",
            LuaMRescueTeamPhase.EvacuateToShuttle => $"evacuating patient: {rescue.LastAutoEvacuationStatus}",
            LuaMRescueTeamPhase.Handoff => $"handoff complete: {team.LastHandoffRecord}",
            LuaMRescueTeamPhase.Triage => $"triage and supply: {rescue.LastAutoSupplyStatus}",
            LuaMRescueTeamPhase.ReturnOrExtract => $"returning or extracting: {returnOrExtractReason}",
            _ => rescue.LastTaskStatus,
        };
    }

    private void TrySayDutyLine(EntityUid uid, LuaMRescueEscortComponent escort, LuaMRescueEscortDuty duty)
    {
        var line = GetDutyLine(escort.Role, duty, IsSomberEscortScene(escort));
        if (string.IsNullOrWhiteSpace(line))
            return;

        if (TryGetEscortLeaderTeam(escort, out var leader, out var team))
        {
            var now = _timing.CurTime;
            var key = $"duty:{FormatRole(escort.Role)}:{FormatDuty(duty)}";
            var reserved = TryReserveTeamSpeech(uid, team, key, now, out var speechStatusChanged, line);
            if (speechStatusChanged)
                Dirty(leader, team);

            if (!reserved)
                return;
        }

        _chat.TrySendInGameICMessage(uid, line, InGameICChatType.Speak, hideChat: false, hideLog: true);
    }

    private static string GetDutyLine(LuaMRescueEscortRole role, LuaMRescueEscortDuty duty, bool somberScene)
    {
        if (somberScene)
            return GetSomberDutyLine(role, duty);

        return (role, duty) switch
        {
            (LuaMRescueEscortRole.Tourniquet, LuaMRescueEscortDuty.SecureScene) => "Медицинская зона. Дайте Айболиту пространство.",
            (LuaMRescueEscortRole.Tourniquet, LuaMRescueEscortDuty.EvacuationCorridor) => "Медицинский коридор, освободить.",
            (LuaMRescueEscortRole.Tourniquet, LuaMRescueEscortDuty.CrowdControl) => "Держим медицинскую зону свободной.",
            (LuaMRescueEscortRole.Tourniquet, LuaMRescueEscortDuty.ClearRoute) => "Маршрут эвакуации проверяю.",
            (LuaMRescueEscortRole.Kostyl, LuaMRescueEscortDuty.PatientSupport) => "Пациент в работе. Носилки морально готовы.",
            (LuaMRescueEscortRole.Kostyl, LuaMRescueEscortDuty.ReturnToShuttle) => "Возвращаюсь к шаттлу. Без пациента скучно, но легче.",
            (LuaMRescueEscortRole.Zaslon, LuaMRescueEscortDuty.SecureScene) => "Я встал. Сектор понял намек.",
            (LuaMRescueEscortRole.Zaslon, LuaMRescueEscortDuty.EvacuationCorridor) => "Пациент внутри периметра.",
            (LuaMRescueEscortRole.Zaslon, LuaMRescueEscortDuty.ThreatScreen) => "Вижу угрозу. Держу сторону.",
            (LuaMRescueEscortRole.Zaslon, LuaMRescueEscortDuty.ClearRoute) => "Коридор держу, лишнее обхожу.",
            _ => string.Empty,
        };
    }

    private static string GetSomberDutyLine(LuaMRescueEscortRole role, LuaMRescueEscortDuty duty)
    {
        return (role, duty) switch
        {
            (LuaMRescueEscortRole.Tourniquet, LuaMRescueEscortDuty.SecureScene) => "\u041c\u0435\u0434\u0438\u0446\u0438\u043d\u0441\u043a\u0430\u044f \u0437\u043e\u043d\u0430 \u0437\u0430\u043a\u0440\u044b\u0442\u0430. \u0420\u0430\u0431\u043e\u0442\u0430\u0435\u043c \u0442\u0438\u0445\u043e.",
            (LuaMRescueEscortRole.Tourniquet, LuaMRescueEscortDuty.EvacuationCorridor) => "\u041a\u043e\u0440\u0438\u0434\u043e\u0440 \u044d\u0432\u0430\u043a\u0443\u0430\u0446\u0438\u0438 \u0434\u0435\u0440\u0436\u0430\u0442\u044c \u0441\u0432\u043e\u0431\u043e\u0434\u043d\u044b\u043c.",
            (LuaMRescueEscortRole.Tourniquet, LuaMRescueEscortDuty.CrowdControl) => "\u0428\u0430\u0433 \u043d\u0430\u0437\u0430\u0434. \u041f\u0430\u0446\u0438\u0435\u043d\u0442\u0443 \u043d\u0443\u0436\u0435\u043d \u0432\u043e\u0437\u0434\u0443\u0445.",
            (LuaMRescueEscortRole.Tourniquet, LuaMRescueEscortDuty.ClearRoute) => "\u041c\u0430\u0440\u0448\u0440\u0443\u0442 \u043a \u0448\u0430\u0442\u0442\u043b\u0443 \u0434\u0435\u0440\u0436\u0438\u043c \u0447\u0438\u0441\u0442\u044b\u043c.",
            (LuaMRescueEscortRole.Kostyl, LuaMRescueEscortDuty.PatientSupport) => "\u0411\u0435\u0440\u0443 \u043f\u043e\u0434\u0434\u0435\u0440\u0436\u043a\u0443 \u043f\u0430\u0446\u0438\u0435\u043d\u0442\u0430. \u0411\u0435\u0437 \u043b\u0438\u0448\u043d\u0438\u0445 \u0441\u043b\u043e\u0432.",
            (LuaMRescueEscortRole.Kostyl, LuaMRescueEscortDuty.ReturnToShuttle) => "\u0412\u043e\u0437\u0432\u0440\u0430\u0449\u0430\u044e\u0441\u044c \u043a \u0448\u0430\u0442\u0442\u043b\u0443. \u0421\u0435\u043a\u0442\u043e\u0440 \u043f\u0440\u043e\u0432\u0435\u0440\u0435\u043d.",
            (LuaMRescueEscortRole.Zaslon, LuaMRescueEscortDuty.SecureScene) => "\u0421\u0442\u043e\u044e \u043c\u0435\u0436\u0434\u0443 \u0443\u0433\u0440\u043e\u0437\u043e\u0439 \u0438 \u043c\u0435\u0434\u0438\u043a\u043e\u043c.",
            (LuaMRescueEscortRole.Zaslon, LuaMRescueEscortDuty.EvacuationCorridor) => "\u041f\u0430\u0446\u0438\u0435\u043d\u0442 \u0432\u043d\u0443\u0442\u0440\u0438 \u043f\u0435\u0440\u0438\u043c\u0435\u0442\u0440\u0430. \u041e\u0442\u0445\u043e\u0434 \u043f\u0440\u0438\u043a\u0440\u044b\u0442.",
            (LuaMRescueEscortRole.Zaslon, LuaMRescueEscortDuty.ThreatScreen) => "\u0423\u0433\u0440\u043e\u0437\u0430 \u043d\u0430 \u043c\u043d\u0435. \u041a \u043f\u0430\u0446\u0438\u0435\u043d\u0442\u0443 \u043d\u0435 \u043f\u0440\u043e\u0439\u0434\u0435\u0442.",
            (LuaMRescueEscortRole.Zaslon, LuaMRescueEscortDuty.ClearRoute) => "\u041a\u043e\u0440\u0438\u0434\u043e\u0440 \u0443\u0434\u0435\u0440\u0436\u0430\u043d. \u041b\u0438\u0448\u043d\u0435\u0435 \u0443\u0431\u0438\u0440\u0430\u044e.",
            _ => string.Empty,
        };
    }

    private bool PruneTeamEscorts(LuaMRescueTeamComponent team)
    {
        return team.Escorts.RemoveAll(escort => !escort.Valid || Deleted(escort)) > 0;
    }

    private bool TryGetEscortLeaderTeam(
        LuaMRescueEscortComponent escort,
        out EntityUid leader,
        out LuaMRescueTeamComponent team)
    {
        leader = default;
        team = default!;

        if (ValidOrNull(escort.Leader) is not { Valid: true } leaderUid ||
            !TryComp<LuaMRescueTeamComponent>(leaderUid, out var leaderTeam))
        {
            return false;
        }

        leader = leaderUid;
        team = leaderTeam;
        return true;
    }

    private bool IsSomberTeamScene(
        LuaMRescueTeamComponent team,
        LuaMRescueAgentComponent rescue,
        EntityUid? patient)
    {
        return IsDeadPatient(patient) ||
               HasSomberStatus(rescue.LastAutoEvacuationStatus) ||
               HasSomberStatus(rescue.LastAutoDefibStatus) ||
               HasSomberStatus(rescue.LastOnboardCareStatus) ||
               HasSomberStatus(rescue.LastOnboardActionStatus) ||
               HasSomberStatus(rescue.LastTaskStatus) ||
               HasSomberStatus(team.LastHandoffRecord) ||
               HasSomberStatus(team.LastSortiePlanStatus) ||
               HasSomberStatus(team.LastSceneStatus);
    }

    private bool IsSomberEscortScene(LuaMRescueEscortComponent escort)
    {
        return IsDeadPatient(escort.Patient) ||
               HasSomberStatus(escort.LastDutyActionStatus) ||
               HasSomberStatus(escort.LastCrewHelpStatus) ||
               HasSomberStatus(escort.LastSceneStatus) ||
               HasSomberStatus(escort.LastMemoryDigest) ||
               HasSomberStatus(escort.LastWeaponReadinessStatus);
    }

    private bool IsDeadPatient(EntityUid? patient)
    {
        return ValidOrNull(patient) is { Valid: true } patientUid &&
               TryComp<MobStateComponent>(patientUid, out var mobState) &&
               mobState.CurrentState == MobState.Dead;
    }

    private static bool HasSomberStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        return status.Contains("dead", StringComparison.OrdinalIgnoreCase) ||
               status.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
               status.Contains("failure", StringComparison.OrdinalIgnoreCase) ||
               status.Contains("aborted", StringComparison.OrdinalIgnoreCase) ||
               status.Contains("incomplete", StringComparison.OrdinalIgnoreCase) ||
               status.Contains("route blocked", StringComparison.OrdinalIgnoreCase) ||
               status.Contains("route-blocked", StringComparison.OrdinalIgnoreCase) ||
               status.Contains("fallback-extraction", StringComparison.OrdinalIgnoreCase) ||
               status.Contains("blocked", StringComparison.OrdinalIgnoreCase) ||
               status.Contains("unsafe", StringComparison.OrdinalIgnoreCase) ||
               status.Contains("overwhelming", StringComparison.OrdinalIgnoreCase);
    }

    private EntityUid? ValidOrNull(EntityUid? uid)
    {
        return uid is { Valid: true } entity && !Deleted(entity)
            ? entity
            : null;
    }

    private EntityUid? GetActiveRescuePatient(LuaMRescueAgentComponent rescue)
    {
        if (rescue.LifeSupportEmergencyActive)
            return ValidOrNull(rescue.LifeSupportEmergencyPatient);

        return ValidOrNull(rescue.EvacuatingTarget ?? rescue.AssignedTarget ?? rescue.TaskPatientTarget);
    }

    private bool IsWithinRange(EntityUid first, EntityUid second, float range)
    {
        if (Deleted(first) || Deleted(second) ||
            !TryComp(first, out TransformComponent? firstXform) ||
            !TryComp(second, out TransformComponent? secondXform) ||
            firstXform.MapID != secondXform.MapID)
        {
            return false;
        }

        var distance = (firstXform.MapPosition.Position - secondXform.MapPosition.Position).LengthSquared();
        return distance <= range * range;
    }

    private string FormatEntityRef(EntityUid? uid)
    {
        if (uid is not { Valid: true } entity)
            return "none";

        if (Deleted(entity))
            return $"{entity}:deleted";

        return $"{GetNetEntity(entity)}:{Name(entity)}";
    }

    private static string GetRoleName(LuaMRescueEscortRole role)
    {
        return role switch
        {
            LuaMRescueEscortRole.Tourniquet => "Турникет",
            LuaMRescueEscortRole.Kostyl => "Костыль",
            LuaMRescueEscortRole.Zaslon => "Заслон",
            _ => "LuaM escort",
        };
    }

    private static string FormatRole(LuaMRescueEscortRole role)
    {
        return role switch
        {
            LuaMRescueEscortRole.Tourniquet => "tourniquet",
            LuaMRescueEscortRole.Kostyl => "kostyl",
            LuaMRescueEscortRole.Zaslon => "zaslon",
            _ => "unknown",
        };
    }

    private static string FormatDuty(LuaMRescueEscortDuty duty)
    {
        return duty switch
        {
            LuaMRescueEscortDuty.Standby => "standby",
            LuaMRescueEscortDuty.SecureScene => "secure-scene",
            LuaMRescueEscortDuty.PatientSupport => "patient-support",
            LuaMRescueEscortDuty.EvacuationCorridor => "evacuation-corridor",
            LuaMRescueEscortDuty.ReturnToShuttle => "return-to-shuttle",
            LuaMRescueEscortDuty.ThreatScreen => "threat-screen",
            LuaMRescueEscortDuty.CrowdControl => "crowd-control",
            LuaMRescueEscortDuty.ClearRoute => "clear-route",
            _ => "unknown",
        };
    }

    private static string FormatPlan(LuaMRescueSortiePlan plan)
    {
        return plan switch
        {
            LuaMRescueSortiePlan.Standby => "standby",
            LuaMRescueSortiePlan.ApproachPatient => "approach-patient",
            LuaMRescueSortiePlan.SecureScene => "secure-scene",
            LuaMRescueSortiePlan.ThreatScreen => "threat-screen",
            LuaMRescueSortiePlan.CrowdControl => "crowd-control",
            LuaMRescueSortiePlan.ClearRoute => "clear-route",
            LuaMRescueSortiePlan.Resupply => "resupply",
            LuaMRescueSortiePlan.TreatPatient => "treat-patient",
            LuaMRescueSortiePlan.EvacuatePatient => "evacuate-patient",
            LuaMRescueSortiePlan.ReturnToShuttle => "return-to-shuttle",
            _ => "unknown",
        };
    }

    private static string FormatPhase(LuaMRescueTeamPhase phase)
    {
        return phase switch
        {
            LuaMRescueTeamPhase.Idle => "idle",
            LuaMRescueTeamPhase.Dispatch => "dispatch",
            LuaMRescueTeamPhase.EnRoute => "en-route",
            LuaMRescueTeamPhase.SecureScene => "secure-scene",
            LuaMRescueTeamPhase.Triage => "triage",
            LuaMRescueTeamPhase.TreatOnSite => "treat-on-site",
            LuaMRescueTeamPhase.PrepareEvacuation => "prepare-evacuation",
            LuaMRescueTeamPhase.EvacuateToShuttle => "evacuate-to-shuttle",
            LuaMRescueTeamPhase.Handoff => "handoff",
            LuaMRescueTeamPhase.ReturnOrExtract => "return-or-extract",
            _ => "unknown",
        };
    }

    private readonly record struct LuaMRescueSceneSnapshot(
        EntityUid? Anchor,
        EntityUid? ThreatTarget,
        EntityUid? CrowdTarget,
        EntityUid? RouteBlockerTarget,
        int HostileCount,
        int CombatantCount,
        int CrowdCount,
        int BlockerCount,
        string Status)
    {
        public static LuaMRescueSceneSnapshot Clear => new(
            null,
            null,
            null,
            null,
            0,
            0,
            0,
            0,
            "scene clear");
    }
}
