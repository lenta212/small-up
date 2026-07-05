using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server.Chat.Systems;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Shared.Buckle.Components;
using Content.Shared.Chat;
using Content.Shared.CombatMode;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._LuaM.Rescue;

public sealed class LuaMRescueTeamSystem : EntitySystem
{
    private const string EscortPrototype = "LuaMRescueEscort";
    private const float SceneScanRange = 6f;
    private const int CrowdPressureThreshold = 4;
    private const int RouteBlockerThreshold = 2;
    private const int SceneMemoryLimit = 8;
    private const double SceneMemoryLifetimeSeconds = 90;
    private const double SceneMemoryReinforceSeconds = 18;
    private const double SortiePlanHoldSeconds = 4;
    private const double EscortDutyHoldSeconds = 2;
    private const double EscortDutyActionIntervalSeconds = 2;
    private const float EscortDutyActionRange = 1.75f;
    private const float EscortThreatScreenRange = 7f;
    private const float EscortPatientAssistRange = 1.5f;
    private const float EscortCrowdControlRange = 3f;

    private static readonly (LuaMRescueEscortRole Role, Vector2 Offset)[] EscortFormation =
    [
        (LuaMRescueEscortRole.Tourniquet, new Vector2(0f, 1.25f)),
        (LuaMRescueEscortRole.Kostyl, new Vector2(-1.25f, 0f)),
        (LuaMRescueEscortRole.Zaslon, new Vector2(0f, -1.25f)),
    ];

    [Dependency] private readonly NPCSystem _npc = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly MetaDataSystem _metaData = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly NpcFactionSystem _factions = default!;
    [Dependency] private readonly PullingSystem _pulling = default!;
    [Dependency] private readonly SharedCombatModeSystem _combatMode = default!;
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
        team.CrowdTarget = null;
        team.RouteBlockerTarget = null;
        team.LastMemoryDigest = "memory clear";
        team.RecentThreatMemories = 0;
        team.RecentCrowdMemories = 0;
        team.RecentRouteMemories = 0;
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
            lines.Add(
                $"team={team.TeamId}; leader={FormatEntityRef(uid)}; phase={FormatPhase(team.Phase)}; " +
                $"plan={FormatPlan(team.SortiePlan)}; planAge={GetSortiePlanAgeSeconds(team)}s; planTransitions={team.SortiePlanTransitions}; " +
                $"patient={FormatEntityRef(team.Patient)}; shuttle={FormatEntityRef(team.Shuttle)}; " +
                $"escorts={team.Escorts.Count}; scene={team.LastSceneStatus}; " +
                $"threat={FormatEntityRef(team.ThreatTarget)}; crowd={team.NearbyCrowd}; crowdTarget={FormatEntityRef(team.CrowdTarget)}; " +
                $"blockers={team.NearbyBlockers}; blockerTarget={FormatEntityRef(team.RouteBlockerTarget)}; " +
                $"memory={team.LastMemoryDigest}; planStatus={team.LastSortiePlanStatus}; last={team.LastStatus}");
        }

        var escortQuery = EntityQueryEnumerator<LuaMRescueEscortComponent>();
        while (escortQuery.MoveNext(out var uid, out var escort))
        {
            lines.Add(
                $"escort={FormatEntityRef(uid)}; team={escort.TeamId}; role={FormatRole(escort.Role)}; " +
                $"plan={FormatPlan(escort.SortiePlan)}; duty={FormatDuty(escort.CurrentDuty)}; " +
                $"dutyAge={GetEscortDutyAgeSeconds(escort)}s; dutyTransitions={escort.DutyTransitions}; " +
                $"follow={FormatEntityRef(escort.CurrentFollowTarget)}; " +
                $"leader={FormatEntityRef(escort.Leader)}; patient={FormatEntityRef(escort.Patient)}; " +
                $"threat={FormatEntityRef(escort.ThreatTarget)}; crowdTarget={FormatEntityRef(escort.CrowdTarget)}; " +
                $"blockerTarget={FormatEntityRef(escort.RouteBlockerTarget)}; " +
                $"scene={escort.LastSceneStatus}; action={escort.LastDutyActionStatus}; " +
                $"memory={escort.LastMemoryDigest}; last={escort.LastDutyStatus}");
        }

        return lines;
    }

    public List<string> BuildRescueAiMemoryDigestLines(int limit = 4)
    {
        var lines = new List<string>();

        var teamQuery = EntityQueryEnumerator<LuaMRescueTeamComponent>();
        while (teamQuery.MoveNext(out _, out var team))
        {
            var escortCount = team.Escorts.Count(escort => escort.Valid && !Deleted(escort));
            lines.Add(
                $"ADMIN_ONLY: rescue sortie digest: team={team.TeamId}; autonomy=escort-group; " +
                $"phase={FormatPhase(team.Phase)}; plan={FormatPlan(team.SortiePlan)}; planAge={GetSortiePlanAgeSeconds(team)}s; " +
                $"planTransitions={team.SortiePlanTransitions}; escorts={escortCount}; scene={team.LastSceneStatus}; " +
                $"pressure(threat/crowd/route)={team.RecentThreatMemories}/{team.RecentCrowdMemories}/{team.RecentRouteMemories}; " +
                $"memory={team.LastMemoryDigest}; planStatus={team.LastSortiePlanStatus}; identities=withheld; coordinates=withheld.");
        }

        return lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(limit)
            .ToList();
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
        escort.DutyActions = 0;
        escort.NextDutyActionAt = _timing.CurTime;
        escort.NextSpeechTime = _timing.CurTime;

        _metaData.SetEntityName(uid, GetRoleName(role));

        if (TryComp<HTNComponent>(uid, out var htn))
            UpdateEscortDuty(uid, escort, htn, forceSpeech: true);

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
        var phase = GetTeamPhase(uid, rescue, patient);
        var status = BuildTeamStatus(rescue, phase);

        var changed = false;
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

        changed |= PruneTeamEscorts(team);
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

        if (changed)
            Dirty(uid, team);
    }

    private void UpdateEscortDuty(EntityUid uid, LuaMRescueEscortComponent escort, HTNComponent htn, bool forceSpeech = false)
    {
        SyncEscortContextFromLeader(escort);

        var candidateDuty = GetEscortDuty(escort);
        var dutyChanged = UpdateEscortDutySelection(escort, candidateDuty, _timing.CurTime);
        var duty = escort.CurrentDuty;
        var followTarget = GetEscortFollowTarget(escort, duty);
        var followChanged = escort.CurrentFollowTarget != followTarget;

        escort.CurrentFollowTarget = followTarget;

        SetEscortFollowTarget(uid, escort, htn, followTarget, duty);
        TryRunEscortDutyAction(uid, escort, htn, duty, followTarget);
        escort.LastDutyStatus = BuildEscortDutyStatus(escort, candidateDuty, followTarget, _timing.CurTime);

        if ((dutyChanged || followChanged || forceSpeech) &&
            _timing.CurTime >= escort.NextSpeechTime)
        {
            TrySayDutyLine(uid, escort, duty);
            escort.NextSpeechTime = _timing.CurTime + TimeSpan.FromSeconds(18);
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
            $"action={escort.LastDutyActionStatus} actions={escort.DutyActions}";

        if (candidateDuty != escort.CurrentDuty)
        {
            var pendingAge = GetElapsedSeconds(escort.PendingDutySince, now);
            status += $" holding candidate {FormatDuty(candidateDuty)} {pendingAge}s/{EscortDutyHoldSeconds:0}s";
        }

        return $"{status}; {escort.LastSceneStatus}; {escort.LastMemoryDigest}";
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

        if (duty == LuaMRescueEscortDuty.ThreatScreen)
        {
            TryRunThreatScreenAction(uid, escort, htn, followTarget);
            return;
        }

        if (duty == LuaMRescueEscortDuty.ClearRoute)
        {
            TryRunClearRouteAction(uid, escort, followTarget);
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

        escort.LastDutyActionStatus = $"watch {FormatDuty(duty)}";
    }

    private void TryRunThreatScreenAction(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        HTNComponent htn,
        EntityUid? followTarget)
    {
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
            escort.LastDutyActionStatus = $"threat-screen target neutralized {FormatEntityRef(threatUid)}";
            return;
        }

        if (TryComp<CombatModeComponent>(uid, out var combat))
            _combatMode.SetInCombatMode(uid, true, combat);

        if (!IsHostileToObserver(uid, threatUid))
        {
            htn.Blackboard.Remove<EntityUid>(NPCBlackboard.CurrentOrderedTarget);
            escort.LastDutyActionStatus = $"threat-screen screening armed pressure {FormatEntityRef(threatUid)}";
            return;
        }

        _npc.SetBlackboard(uid, NPCBlackboard.CurrentOrderedTarget, threatUid, htn);
        _npc.WakeNPC(uid, htn);
        escort.DutyActions++;

        escort.LastDutyActionStatus = !IsWithinRange(uid, threatUid, EscortThreatScreenRange)
            ? $"threat-screen advancing to hostile {FormatEntityRef(threatUid)}"
            : $"threat-screen engaging hostile {FormatEntityRef(threatUid)}";
    }

    private void TryRunClearRouteAction(
        EntityUid uid,
        LuaMRescueEscortComponent escort,
        EntityUid? followTarget)
    {
        var blocker = ValidOrNull(escort.RouteBlockerTarget) ?? ValidOrNull(followTarget);
        if (blocker is not { Valid: true } blockerUid)
        {
            escort.LastDutyActionStatus = "clear-route no blocker target";
            return;
        }

        if (!TryComp<PullableComponent>(blockerUid, out var pullable))
        {
            escort.LastDutyActionStatus = $"clear-route blocker not pullable {FormatEntityRef(blockerUid)}";
            return;
        }

        if (!TryComp<PullerComponent>(uid, out var puller))
        {
            escort.LastDutyActionStatus = "clear-route escort cannot pull";
            return;
        }

        if (!IsWithinRange(uid, blockerUid, EscortDutyActionRange))
        {
            escort.LastDutyActionStatus = $"clear-route moving to {FormatEntityRef(blockerUid)}";
            return;
        }

        if (_pulling.TryStartPull(uid, blockerUid, puller, pullable))
        {
            escort.DutyActions++;
            escort.LastDutyActionStatus = $"clear-route pulling {FormatEntityRef(blockerUid)}";
            return;
        }

        escort.LastDutyActionStatus = $"clear-route pull blocked {FormatEntityRef(blockerUid)}";
    }

    private static bool ShouldEscortAssistPatientPull(
        LuaMRescueEscortComponent escort,
        LuaMRescueEscortDuty duty)
    {
        return escort.Role == LuaMRescueEscortRole.Kostyl &&
            duty == LuaMRescueEscortDuty.PatientSupport &&
            escort.SortiePlan == LuaMRescueSortiePlan.EvacuatePatient;
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

        if (TryComp<BuckleComponent>(patientUid, out var buckle) &&
            buckle.BuckledTo is { Valid: true })
        {
            escort.LastDutyActionStatus = $"patient-assist already buckled {FormatEntityRef(patientUid)}";
            return;
        }

        if (!TryComp<PullableComponent>(patientUid, out var pullable))
        {
            escort.LastDutyActionStatus = $"patient-assist patient not pullable {FormatEntityRef(patientUid)}";
            return;
        }

        if (pullable.Puller == uid)
        {
            escort.LastDutyActionStatus = $"patient-assist holding {FormatEntityRef(patientUid)}";
            return;
        }

        if (pullable.Puller is { Valid: true } puller && !Deleted(puller))
        {
            escort.LastDutyActionStatus = $"patient-assist already pulled by {FormatEntityRef(puller)}";
            return;
        }

        if (!TryComp<PullerComponent>(uid, out var pullerComp))
        {
            escort.LastDutyActionStatus = "patient-assist escort cannot pull";
            return;
        }

        if (!IsWithinRange(uid, patientUid, EscortPatientAssistRange))
        {
            escort.LastDutyActionStatus = $"patient-assist moving to {FormatEntityRef(patientUid)}";
            return;
        }

        if (_pulling.TryStartPull(uid, patientUid, pullerComp, pullable))
        {
            escort.DutyActions++;
            escort.LastDutyActionStatus = $"patient-assist pulling {FormatEntityRef(patientUid)}";
            return;
        }

        escort.LastDutyActionStatus = $"patient-assist pull blocked {FormatEntityRef(patientUid)}";
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
            escort.LastDutyActionStatus = "crowd-control no crowd target";
            return;
        }

        if (!IsWithinRange(uid, crowdUid, EscortCrowdControlRange))
        {
            escort.LastDutyActionStatus = $"crowd-control moving to {FormatEntityRef(crowdUid)}";
            return;
        }

        var line = GetCrowdControlLine(escort);
        if (!string.IsNullOrWhiteSpace(line))
            _chat.TrySendInGameICMessage(uid, line, InGameICChatType.Speak, hideChat: false, hideLog: true);

        escort.DutyActions++;
        escort.LastDutyActionStatus = $"crowd-control warning {FormatEntityRef(crowdUid)}";
    }

    private static string GetCrowdControlLine(LuaMRescueEscortComponent escort)
    {
        return escort.NearbyCrowd >= CrowdPressureThreshold
            ? "Medical rescue corridor. Step back from the patient."
            : "Keep the rescue corridor clear.";
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
            Deleted(leader))
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
        var combatantCount = 0;
        var crowdCount = 0;
        var blockerCount = 0;
        EntityUid? threatTarget = null;
        EntityUid? crowdTarget = null;
        EntityUid? routeBlockerTarget = null;
        var threatDistance = float.MaxValue;
        var crowdDistance = float.MaxValue;
        var routeBlockerDistance = float.MaxValue;

        _sceneEntities.Clear();
        _lookup.GetEntitiesInRange(anchorUid, SceneScanRange, _sceneEntities, LookupFlags.Dynamic | LookupFlags.Sundries | LookupFlags.Approximate);

        foreach (var candidate in _sceneEntities)
        {
            if (ShouldIgnoreSceneEntity(candidate, teamId, leader, patient, shuttle, shuttleAnchor))
                continue;

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
            var activeCombatant = !hostile && IsActiveCombatant(observer, candidate);

            if (hostile)
                hostileCount++;
            else if (activeCombatant)
                combatantCount++;

            var distance = (candidateXform.MapPosition.Position - origin.Position).LengthSquared();

            if (hostile || activeCombatant)
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

            if (IsRouteBlocker(candidate, hasMobState))
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

        var summary = BuildSceneSummary(hostileCount, combatantCount, crowdCount, blockerCount);
        return new LuaMRescueSceneSnapshot(
            anchorUid,
            ValidOrNull(threatTarget),
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

        return observerFaction.Factions.Any(faction => _factions.IsFactionHostile(faction, (candidate, candidateFaction)));
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

    private static string BuildSceneSummary(int hostiles, int combatants, int crowd, int blockers)
    {
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
            _ => null,
        };
    }

    private LuaMRescueEscortDuty GetEscortDuty(LuaMRescueEscortComponent escort)
    {
        var patient = escort.Patient is { Valid: true } patientUid && !Deleted(patientUid)
            ? patientUid
            : (EntityUid?) null;

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

    private EntityUid? GetEscortFollowTarget(LuaMRescueEscortComponent escort, LuaMRescueEscortDuty duty)
    {
        var patient = escort.Patient is { Valid: true } patientUid && !Deleted(patientUid)
            ? patientUid
            : (EntityUid?) null;
        var leader = escort.Leader is { Valid: true } leaderUid && !Deleted(leaderUid)
            ? leaderUid
            : (EntityUid?) null;
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
        var sceneAnchor = escort.SceneAnchor is { Valid: true } sceneAnchorUid && !Deleted(sceneAnchorUid)
            ? sceneAnchorUid
            : (EntityUid?) null;

        return duty switch
        {
            LuaMRescueEscortDuty.ThreatScreen => threat ?? sceneAnchor ?? patient ?? leader ?? shuttleAnchor ?? shuttle,
            LuaMRescueEscortDuty.CrowdControl => crowd ?? sceneAnchor ?? patient ?? leader ?? shuttleAnchor ?? shuttle,
            LuaMRescueEscortDuty.ClearRoute => routeBlocker ?? sceneAnchor ?? leader ?? shuttleAnchor ?? patient ?? shuttle,
            LuaMRescueEscortDuty.SecureScene => escort.Role == LuaMRescueEscortRole.Zaslon
                ? threat ?? patient ?? leader ?? shuttleAnchor ?? shuttle
                : leader ?? patient ?? shuttleAnchor ?? shuttle,
            LuaMRescueEscortDuty.PatientSupport => patient ?? leader ?? shuttleAnchor ?? shuttle,
            LuaMRescueEscortDuty.EvacuationCorridor => escort.Role == LuaMRescueEscortRole.Zaslon
                ? threat ?? patient ?? shuttleAnchor ?? leader ?? shuttle
                : leader ?? patient ?? shuttleAnchor ?? shuttle,
            LuaMRescueEscortDuty.ReturnToShuttle => shuttleAnchor ?? shuttle ?? leader,
            _ => leader ?? shuttleAnchor ?? shuttle,
        };
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
            LuaMRescueEscortDuty.ClearRoute => 2.5f,
            LuaMRescueEscortDuty.SecureScene => escort.Role == LuaMRescueEscortRole.Zaslon ? 1.0f : 2.0f,
            LuaMRescueEscortDuty.EvacuationCorridor => escort.Role == LuaMRescueEscortRole.Kostyl ? 1.25f : 2.25f,
            LuaMRescueEscortDuty.PatientSupport => 1.25f,
            LuaMRescueEscortDuty.ReturnToShuttle => 2.0f,
            _ => escort.FollowCloseRange,
        };

        var followRange = duty switch
        {
            LuaMRescueEscortDuty.ThreatScreen => 7f,
            LuaMRescueEscortDuty.CrowdControl => 5.5f,
            LuaMRescueEscortDuty.ClearRoute => 6.5f,
            LuaMRescueEscortDuty.SecureScene => 5.5f,
            LuaMRescueEscortDuty.EvacuationCorridor => 6f,
            LuaMRescueEscortDuty.PatientSupport => 4f,
            LuaMRescueEscortDuty.ReturnToShuttle => 5f,
            _ => escort.FollowRange,
        };

        _npc.SetBlackboard(uid, NPCBlackboard.FollowTarget, new EntityCoordinates(target, Vector2.Zero), htn);
        _npc.SetBlackboard(uid, "FollowCloseRange", closeRange, htn);
        _npc.SetBlackboard(uid, "FollowRange", followRange, htn);
        _npc.WakeNPC(uid, htn);
    }

    private LuaMRescueTeamPhase GetTeamPhase(EntityUid uid, LuaMRescueAgentComponent rescue, EntityUid? patient)
    {
        if (patient is not { Valid: true } patientUid ||
            Deleted(patientUid))
            return rescue.AssignedShuttle is { Valid: true }
                ? LuaMRescueTeamPhase.ReturnOrExtract
                : LuaMRescueTeamPhase.Idle;

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

    private static string BuildTeamStatus(LuaMRescueAgentComponent rescue, LuaMRescueTeamPhase phase)
    {
        return phase switch
        {
            LuaMRescueTeamPhase.TreatOnSite => $"Aibolit treating: {rescue.LastAutoTreatmentStatus}",
            LuaMRescueTeamPhase.PrepareEvacuation => $"preparing evacuation: {rescue.LastAutoEvacuationStatus}",
            LuaMRescueTeamPhase.EvacuateToShuttle => $"evacuating patient: {rescue.LastAutoEvacuationStatus}",
            LuaMRescueTeamPhase.Triage => $"triage and supply: {rescue.LastAutoSupplyStatus}",
            LuaMRescueTeamPhase.ReturnOrExtract => "returning or extracting",
            _ => rescue.LastTaskStatus,
        };
    }

    private void TrySayDutyLine(EntityUid uid, LuaMRescueEscortComponent escort, LuaMRescueEscortDuty duty)
    {
        var line = GetDutyLine(escort.Role, duty);
        if (string.IsNullOrWhiteSpace(line))
            return;

        _chat.TrySendInGameICMessage(uid, line, InGameICChatType.Speak, hideChat: false, hideLog: true);
    }

    private static string GetDutyLine(LuaMRescueEscortRole role, LuaMRescueEscortDuty duty)
    {
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

    private bool PruneTeamEscorts(LuaMRescueTeamComponent team)
    {
        return team.Escorts.RemoveAll(escort => !escort.Valid || Deleted(escort)) > 0;
    }

    private EntityUid? ValidOrNull(EntityUid? uid)
    {
        return uid is { Valid: true } entity && !Deleted(entity)
            ? entity
            : null;
    }

    private EntityUid? GetActiveRescuePatient(LuaMRescueAgentComponent rescue)
    {
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
