using System;
using System.Collections.Generic;
using System.Numerics;
using Content.Server.Chat.Systems;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Shared.Chat;
using Content.Shared.CombatMode;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
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
        team.LastStatus = "autonomous rescue team deployed";
        team.SceneScanAccumulator = team.SceneScanInterval;
        team.Escorts.Clear();

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
                $"patient={FormatEntityRef(team.Patient)}; shuttle={FormatEntityRef(team.Shuttle)}; " +
                $"escorts={team.Escorts.Count}; scene={team.LastSceneStatus}; " +
                $"threat={FormatEntityRef(team.ThreatTarget)}; crowd={team.NearbyCrowd}; " +
                $"blockers={team.NearbyBlockers}; last={team.LastStatus}");
        }

        var escortQuery = EntityQueryEnumerator<LuaMRescueEscortComponent>();
        while (escortQuery.MoveNext(out var uid, out var escort))
        {
            lines.Add(
                $"escort={FormatEntityRef(uid)}; team={escort.TeamId}; role={FormatRole(escort.Role)}; " +
                $"duty={FormatDuty(escort.CurrentDuty)}; follow={FormatEntityRef(escort.CurrentFollowTarget)}; " +
                $"leader={FormatEntityRef(escort.Leader)}; patient={FormatEntityRef(escort.Patient)}; " +
                $"threat={FormatEntityRef(escort.ThreatTarget)}; scene={escort.LastSceneStatus}; " +
                $"last={escort.LastDutyStatus}");
        }

        return lines;
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
        escort.LastDutyStatus = "deployed";
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
        var patient = ValidOrNull(rescue.EvacuatingTarget ?? rescue.AssignedTarget ?? rescue.TaskPatientTarget ?? team.Patient);
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
        }

        if (changed)
            Dirty(uid, team);
    }

    private void UpdateEscortDuty(EntityUid uid, LuaMRescueEscortComponent escort, HTNComponent htn, bool forceSpeech = false)
    {
        SyncEscortContextFromLeader(escort);

        var duty = GetEscortDuty(escort);
        var followTarget = GetEscortFollowTarget(escort, duty);
        var changed = escort.CurrentDuty != duty || escort.CurrentFollowTarget != followTarget;

        escort.CurrentDuty = duty;
        escort.CurrentFollowTarget = followTarget;
        escort.LastDutyStatus = $"{FormatRole(escort.Role)} {FormatDuty(duty)}; {escort.LastSceneStatus}";

        SetEscortFollowTarget(uid, escort, htn, followTarget, duty);
        if ((changed || forceSpeech) &&
            _timing.CurTime >= escort.NextSpeechTime)
        {
            TrySayDutyLine(uid, escort, duty);
            escort.NextSpeechTime = _timing.CurTime + TimeSpan.FromSeconds(18);
        }

        Dirty(uid, escort);
    }

    private void SyncEscortContextFromLeader(LuaMRescueEscortComponent escort)
    {
        if (escort.Leader is not { Valid: true } leader ||
            Deleted(leader))
        {
            escort.Leader = null;
            escort.Patient = null;
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
            escort.NearbyHostiles = team.NearbyHostiles;
            escort.NearbyCombatants = team.NearbyCombatants;
            escort.NearbyCrowd = team.NearbyCrowd;
            escort.NearbyBlockers = team.NearbyBlockers;
            escort.LastSceneStatus = team.LastSceneStatus;
            return;
        }

        if (TryComp<LuaMRescueAgentComponent>(leader, out var rescue))
        {
            escort.Patient = ValidOrNull(rescue.EvacuatingTarget ?? rescue.AssignedTarget ?? rescue.TaskPatientTarget);
            escort.Shuttle = ValidOrNull(rescue.AssignedShuttle);
            escort.ShuttleAnchor = ValidOrNull(rescue.AssignedShuttleAnchor);
            var scene = ScanRescueScene(leader, escort.TeamId, leader, escort.Patient, escort.Shuttle, escort.ShuttleAnchor);
            ApplySceneToEscort(escort, scene);
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
        var threatDistance = float.MaxValue;

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

            if (hostile || activeCombatant)
            {
                var distance = (candidateXform.MapPosition.Position - origin.Position).LengthSquared();
                if (distance < threatDistance)
                {
                    threatDistance = distance;
                    threatTarget = candidate;
                }
            }

            if (IsRouteBlocker(candidate, hasMobState))
                blockerCount++;
        }

        _sceneEntities.Clear();

        var summary = BuildSceneSummary(hostileCount, combatantCount, crowdCount, blockerCount);
        return new LuaMRescueSceneSnapshot(
            anchorUid,
            ValidOrNull(threatTarget),
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
        escort.NearbyHostiles = scene.HostileCount;
        escort.NearbyCombatants = scene.CombatantCount;
        escort.NearbyCrowd = scene.CrowdCount;
        escort.NearbyBlockers = scene.BlockerCount;
        escort.LastSceneStatus = scene.Status;
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

        return _factions.IsFactionHostile("NanoTrasen", (candidate, candidateFaction));
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

    private LuaMRescueEscortDuty GetEscortDuty(LuaMRescueEscortComponent escort)
    {
        var patient = escort.Patient is { Valid: true } patientUid && !Deleted(patientUid)
            ? patientUid
            : (EntityUid?) null;

        if (patient == null)
            return escort.ShuttleAnchor is { Valid: true } || escort.Shuttle is { Valid: true }
                ? LuaMRescueEscortDuty.ReturnToShuttle
                : LuaMRescueEscortDuty.Standby;

        if (escort.Leader is { Valid: true } leader &&
            TryComp<LuaMRescueAgentComponent>(leader, out var rescue))
        {
            var evacuating = rescue.TaskStage is LuaMRescueTaskStage.EvacuatingPatient or LuaMRescueTaskStage.DeliveringPatient ||
                rescue.EvacuatingTarget is { Valid: true };

            if (HasSceneThreat(escort) &&
                escort.Role == LuaMRescueEscortRole.Zaslon)
            {
                return LuaMRescueEscortDuty.ThreatScreen;
            }

            if (evacuating &&
                escort.NearbyBlockers >= RouteBlockerThreshold &&
                escort.Role != LuaMRescueEscortRole.Kostyl)
            {
                return LuaMRescueEscortDuty.ClearRoute;
            }

            if (escort.NearbyCrowd >= CrowdPressureThreshold &&
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
                if (escort.NearbyCrowd >= CrowdPressureThreshold &&
                    escort.Role == LuaMRescueEscortRole.Tourniquet)
                {
                    return LuaMRescueEscortDuty.CrowdControl;
                }

                return escort.Role == LuaMRescueEscortRole.Kostyl
                    ? LuaMRescueEscortDuty.PatientSupport
                    : LuaMRescueEscortDuty.SecureScene;
            }
        }

        if (HasSceneThreat(escort) &&
            escort.Role == LuaMRescueEscortRole.Zaslon)
        {
            return LuaMRescueEscortDuty.ThreatScreen;
        }

        if (escort.NearbyCrowd >= CrowdPressureThreshold &&
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

        return duty switch
        {
            LuaMRescueEscortDuty.ThreatScreen => threat ?? patient ?? leader ?? shuttleAnchor ?? shuttle,
            LuaMRescueEscortDuty.CrowdControl => patient ?? leader ?? shuttleAnchor ?? shuttle,
            LuaMRescueEscortDuty.ClearRoute => leader ?? shuttleAnchor ?? patient ?? shuttle,
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
        int HostileCount,
        int CombatantCount,
        int CrowdCount,
        int BlockerCount,
        string Status)
    {
        public static LuaMRescueSceneSnapshot Clear => new(
            null,
            null,
            0,
            0,
            0,
            0,
            "scene clear");
    }
}
