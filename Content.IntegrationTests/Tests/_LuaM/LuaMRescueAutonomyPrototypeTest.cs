using System.IO;
using System.Linq;
using System.Text;
using YamlDotNet.RepresentationModel;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMRescueAutonomyPrototypeTest
{
    [Test]
    public void RescueAgentsUseArmedProtectedTeamGear()
    {
        var agentGear = FindPrototype(
            LoadSequence("Resources/Prototypes/_LuaM/Loadouts/rescue.yml"),
            "LuaMRescueAgentGear");
        var agentEquipment = Mapping(agentGear, "equipment");

        Assert.That(ScalarValue(agentEquipment, "outerClothing"), Is.EqualTo("ClothingOuterArmorBasicSlim"));
        Assert.That(ScalarValue(agentEquipment, "head"), Is.EqualTo("ClothingHeadHelmetBasic"));
        Assert.That(ScalarValue(agentEquipment, "eyes"), Is.EqualTo("ClothingEyesHudMedical"));
        Assert.That(ScalarValue(agentEquipment, "gloves"), Is.EqualTo("ClothingHandsGlovesCombat"));
        Assert.That(SequenceValues(Sequence(agentGear, "inhand")), Does.Contain("WeaponLaserCarbine"));
        Assert.That(SequenceValues(Sequence(Mapping(agentGear, "storage"), "back")), Does.Contain("MedkitCombatFilled"));
        Assert.That(SequenceValues(Sequence(Mapping(agentGear, "storage"), "back")), Does.Contain("DefibrillatorCompact"));

        var escortGear = FindPrototype(
            LoadSequence("Resources/Prototypes/_LuaM/Loadouts/rescue.yml"),
            "LuaMRescueEscortGear");
        var escortEquipment = Mapping(escortGear, "equipment");

        Assert.That(ScalarValue(escortEquipment, "outerClothing"), Is.EqualTo("ClothingOuterArmorBPVestHeavy"));
        Assert.That(ScalarValue(escortEquipment, "head"), Is.EqualTo("ClothingHeadHelmetSwat"));
        Assert.That(ScalarValue(escortEquipment, "eyes"), Is.EqualTo("ClothingEyesGlassesSecurity"));
        Assert.That(ScalarValue(escortEquipment, "belt"), Is.EqualTo("ClothingBeltSecurityFilled"));
        Assert.That(SequenceValues(Sequence(escortGear, "inhand")), Does.Contain("WeaponLaserCarbine"));
        Assert.That(SequenceValues(Sequence(Mapping(escortGear, "storage"), "back")), Does.Contain("WeaponDisablerSMG"));
        Assert.That(SequenceValues(Sequence(Mapping(escortGear, "storage"), "back")), Does.Contain("CombatMedipen"));

        var entities = LoadSequence("Resources/Prototypes/_LuaM/Entities/Mobs/rescue_agent.yml");
        AssertLoadout(entities, "LuaMRescueAgent", "LuaMRescueAgentGear");
        AssertLoadout(entities, "LuaMRescueEscort", "LuaMRescueEscortGear");
    }

    [Test]
    public void RescueCompoundPrioritizesCombatBeforeTreatmentAndFollow()
    {
        var compound = FindPrototype(
            LoadSequence("Resources/Prototypes/_LuaM/NPCs/rescue.yml"),
            "LuaMRescueCompound");
        var branches = Sequence(compound, "branches");

        Assert.That(branches.Children, Has.Count.EqualTo(5));
        Assert.That(GetCompoundTask(branches, 0), Is.EqualTo("RangedCombatCompound"));
        Assert.That(GetCompoundTask(branches, 1), Is.EqualTo("MeleeCombatCompound"));
        Assert.That(GetCompoundTask(branches, 2), Is.EqualTo("InjectNearbyCompound"));
        Assert.That(GetCompoundTask(branches, 3), Is.EqualTo("FollowCompound"));
        Assert.That(GetCompoundTask(branches, 4), Is.EqualTo("IdleCompound"));
        Assert.That(Sequence((YamlMappingNode) branches.Children[3], "preconditions").Children, Has.Count.EqualTo(1));
    }

    [Test]
    public void RescueShuttleAnnouncesDeathSignalDispatchOverMedicalRadio()
    {
        var source = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueShuttleSystem.cs"), Encoding.UTF8);
        var component = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueAgentComponent.cs"), Encoding.UTF8);
        var agent = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueAgentSystem.cs"), Encoding.UTF8);

        Assert.That(source, Does.Contain("SubscribeLocalEvent<MobStateChangedEvent>(OnMobStateChanged)"));
        Assert.That(source, Does.Contain("TryDispatchAutomaticDeathSignal(ev.Target)"));
        Assert.That(source, Does.Contain("HasComp<ActorComponent>(target)"));
        Assert.That(source, Does.Contain("AutomaticDeathSignalCooldownSeconds"));
        Assert.That(source, Does.Contain("TryResolveAutomaticDeathSignalStation"));
        Assert.That(source, Does.Contain("spawnTeam: true"));
        Assert.That(source, Does.Contain("deathSignal: true"));
        Assert.That(source, Does.Contain("DeathSignalFlag"));
        Assert.That(source, Does.Contain("--death-signal"));
        Assert.That(source, Does.Contain("requires target=<entity|player> so Aibolit can report who it is flying to"));
        Assert.That(source, Does.Contain("bool deathSignal"));
        Assert.That(source, Does.Contain("Death signal dispatch requires a rescue agent so Aibolit can report who it is flying to."));
        Assert.That(source, Does.Contain("Death signal dispatch requires target=<entity|player> so Aibolit can report who it is flying to."));
        Assert.That(source, Does.Contain("rescue.DeathSignalTarget = deathSignalTarget"));
        Assert.That(source, Does.Contain("deathSignal &&"));
        Assert.That(source, Does.Contain("SendDispatchRadio(agent.Value, dispatchTarget);"));
        Assert.That(source, Does.Contain("\u041c\u0435\u0434\u0441\u0438\u0433\u043d\u0430\u043b \u0441\u043c\u0435\u0440\u0442\u0438 \u043f\u0440\u0438\u043d\u044f\u0442. \u0412\u044b\u043b\u0435\u0442\u0430\u044e \u043a \u043f\u0430\u0446\u0438\u0435\u043d\u0442\u0443 {targetName}."));
        Assert.That(source, Does.Contain("_radio.SendRadioMessage("));
        Assert.That(source, Does.Contain("MedicalRadioChannel"));
        Assert.That(source, Does.Contain("MarkDeathSignalDispatchReported"));
        Assert.That(source, Does.Contain("rescue.DeathSignalDispatchReported = true"));
        Assert.That(source, Does.Contain("rescue.LastAutoCommsKey = $\"death-signal-dispatch:{target}\""));
        Assert.That(component, Does.Contain("DeathSignalTarget"));
        Assert.That(component, Does.Contain("DeathSignalDispatchReported"));
        Assert.That(agent, Does.Contain("TryReportDeathSignalDispatch"));
        Assert.That(agent, Does.Contain("death-signal-dispatch:"));
        Assert.That(agent, Does.Contain("deathSignal={FormatEntityRef(rescue.DeathSignalTarget)}"));
        Assert.That(agent, Does.Contain("mobState.CurrentState != MobState.Dead"));
        Assert.That(agent, Does.Contain("ClearDeathSignalTarget"));
        Assert.That(agent, Does.Contain("\\u041c\\u0435\\u0434\\u0441\\u0438\\u0433\\u043d\\u0430\\u043b \\u0441\\u043c\\u0435\\u0440\\u0442\\u0438 \\u043f\\u0440\\u0438\\u043d\\u044f\\u0442"));
        Assert.That(agent, Does.Contain("\\u0412\\u044b\\u043b\\u0435\\u0442\\u0430\\u044e \\u043a \\u043f\\u0430\\u0446\\u0438\\u0435\\u043d\\u0442\\u0443 {Name(target)}"));
    }

    [Test]
    public void RescueAgentRecoversDeadPatientsToShuttleWithoutAutoRelease()
    {
        var component = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueAgentComponent.cs"), Encoding.UTF8);
        var source = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueAgentSystem.cs"), Encoding.UTF8);

        Assert.That(component, Does.Contain("RecoverDeadPatientsToShuttle = true"));
        Assert.That(source, Does.Contain("IsDeadPatientRecoveryTarget"));
        Assert.That(source, Does.Contain("return IsDeadPatientRecoveryTarget(target, rescue, mobState);"));
        Assert.That(source, Does.Contain("rescue.AssignedTarget == target"));
        Assert.That(source, Does.Contain("HasComp<ActorComponent>(target)"));
        Assert.That(source, Does.Contain("score += 1500f"));
        Assert.That(source, Does.Contain("holding dead onboard patient"));
        Assert.That(source, Does.Contain("mobState.CurrentState == MobState.Dead ||"));
    }

    [Test]
    public void RescueAgentEvacuatesThreatenedPatientsBeforeOnSiteTreatment()
    {
        var component = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueAgentComponent.cs"), Encoding.UTF8);
        var source = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueAgentSystem.cs"), Encoding.UTF8);

        Assert.That(component, Does.Contain("EvacuateWhenSceneThreatened = true"));
        Assert.That(component, Does.Contain("ThreatEvacuationMinDamage = 5f"));
        Assert.That(component, Does.Contain("OverwhelmingThreatHostileThreshold = 3"));
        Assert.That(component, Does.Contain("OverwhelmingThreatCombatantThreshold = 4"));
        Assert.That(source, Does.Contain("TryTreatOrEvacuateTarget"));
        Assert.That(source, Does.Contain("ShouldEvacuateBeforeTreatment"));
        Assert.That(source, Does.Contain("IsThreatenedEvacuationTarget"));
        Assert.That(source, Does.Contain("HasRescueTeamThreatPressure"));
        Assert.That(source, Does.Contain("HasRescueTeamOverwhelmingThreatPressure"));
        Assert.That(source, Does.Contain("TryComp<LuaMRescueTeamComponent>(uid, out var team)"));
        Assert.That(source, Does.Contain("team.ThreatTarget is { Valid: true }"));
        Assert.That(source, Does.Contain("team.RecentThreatMemories > 0"));
        Assert.That(source, Does.Contain("team.NearbyHostiles >= rescue.OverwhelmingThreatHostileThreshold"));
        Assert.That(source, Does.Contain("team.NearbyCombatants >= rescue.OverwhelmingThreatCombatantThreshold"));
        Assert.That(source, Does.Contain("rescue.ThreatEvacuationMinDamage"));
        Assert.That(source, Does.Contain("overwhelming-threat evacuation"));
        Assert.That(source, Does.Contain("unsafe-scene evacuation"));
        Assert.That(source, Does.Contain("score += 750f"));
        Assert.That(source, Does.Contain("!unsafeSceneEvacuation"));
    }

    [Test]
    public void RescueAgentExplainsEvacuationWhenTreatmentCanNoLongerHold()
    {
        var source = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueAgentSystem.cs"), Encoding.UTF8);

        Assert.That(source, Does.Contain("BuildEvacuationReason"));
        Assert.That(source, Does.Contain("rescue.LastAutoEvacuationStatus = evacuationReason"));
        Assert.That(source, Does.Contain("evacuating {FormatEntityRef(target)}; {evacuationReason}"));
        Assert.That(source, Does.Contain("condition-worsened evacuation"));
        Assert.That(source, Does.Contain("on-site treatment limited"));
        Assert.That(source, Does.Contain("heavy-damage evacuation"));
        Assert.That(source, Does.Contain("shuttle care required"));
    }

    [Test]
    public void RescueAgentKeepsShuttleForwardForPendingRescueTargets()
    {
        var source = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueAgentSystem.cs"), Encoding.UTF8);

        Assert.That(source, Does.Contain("var hasPendingRescueTarget = HasPendingRescueTarget(uid, rescue, target);"));
        Assert.That(source, Does.Contain("HasPendingRescueTarget"));
        Assert.That(source, Does.Contain("HasPendingEvacuationTarget(uid, rescue, completedTarget)"));
        Assert.That(source, Does.Contain("TryComp<MedibotComponent>(uid, out var medibot)"));
        Assert.That(source, Does.Contain("TryFindRescueTarget(uid, rescue.SearchRange, rescue, medibot, out _, completedTarget)"));
        Assert.That(source, Does.Contain("EntityUid? excludedTarget = null"));
        Assert.That(source, Does.Contain("candidate == excludedTarget"));
        Assert.That(source, Does.Contain("allowAutoReturn: !hasPendingRescueTarget"));
        Assert.That(source, Does.Contain("holding shuttle forward after evacuation"));
        Assert.That(source, Does.Contain("holding shuttle forward after skipping"));
        Assert.That(source, Does.Contain("pending rescue target detected"));
        Assert.That(source, Does.Contain("bool allowAutoReturn = true"));
        Assert.That(source, Does.Contain("if (allowAutoReturn)"));
    }

    [Test]
    public void RescueAgentHoldsAndRequestsHelpWhenEvacuationRouteIsBlocked()
    {
        var component = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueAgentComponent.cs"), Encoding.UTF8);
        var source = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueAgentSystem.cs"), Encoding.UTF8);

        Assert.That(component, Does.Contain("HoldPositionOnBlockedEvacuation = true"));
        Assert.That(component, Does.Contain("RouteBlockHoldSeconds = 4f"));
        Assert.That(component, Does.Contain("RouteBlockHoldTarget"));
        Assert.That(component, Does.Contain("RouteBlockHoldGoal"));
        Assert.That(component, Does.Contain("RouteBlockHelpRequested"));
        Assert.That(component, Does.Contain("LastRouteBlockHoldStatus"));
        Assert.That(source, Does.Contain("TryHoldRouteBlockedEvacuation"));
        Assert.That(source, Does.Contain("HasRescueTeamRoutePressure"));
        Assert.That(source, Does.Contain("team.RouteBlockerTarget is { Valid: true }"));
        Assert.That(source, Does.Contain("team.NearbyBlockers >= 2"));
        Assert.That(source, Does.Contain("TryGetActiveDeliveryGoal"));
        Assert.That(source, Does.Contain("TryRerouteBlockedDeliveryTarget"));
        Assert.That(source, Does.Contain("TryFallbackToShuttleExtraction"));
        Assert.That(source, Does.Contain("TryFallbackToShuttleExtraction(uid, target, rescue, htn, progressGoal)"));
        Assert.That(source, Does.Contain("hold-position route blocked"));
        Assert.That(source, Does.Contain("route-blocked-hold:"));
        Assert.That(source, Does.Contain("route-blocked-reroute:"));
        Assert.That(source, Does.Contain("route-blocked-help:"));
        Assert.That(source, Does.Contain("route-blocked-extract:"));
        Assert.That(source, Does.Contain("rescue.AssignedPatientStrap = null;"));
        Assert.That(source, Does.Contain("fallback extraction after blocked route"));
        Assert.That(source, Does.Contain("fallback-extraction-waiting"));
        Assert.That(source, Does.Contain("requesting route help"));
        Assert.That(source, Does.Contain("routeHold={rescue.LastRouteBlockHoldStatus}"));
        Assert.That(source, Does.Contain("\\u041c\\u0430\\u0440\\u0448\\u0440\\u0443\\u0442 \\u044d\\u0432\\u0430\\u043a\\u0443\\u0430\\u0446\\u0438\\u0438 \\u0437\\u0430\\u0431\\u043b\\u043e\\u043a\\u0438\\u0440\\u043e\\u0432\\u0430\\u043d"));
        Assert.That(source, Does.Contain("\\u041d\\u0443\\u0436\\u043d\\u0430 \\u043f\\u043e\\u043c\\u043e\\u0449\\u044c \\u0441 \\u043a\\u043e\\u0440\\u0438\\u0434\\u043e\\u0440\\u043e\\u043c"));
    }

    [Test]
    public void RescueAgentOnlyReroutesToCloserHigherAcuityPatients()
    {
        var source = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueAgentSystem.cs"), Encoding.UTF8);

        Assert.That(source, Does.Contain("TryFindPriorityRescueOverride"));
        Assert.That(source, Does.Contain("TryGetActivePatientTarget"));
        Assert.That(source, Does.Contain("TryGetRescueTargetPriority"));
        Assert.That(source, Does.Contain("GetRescueTargetAcuity"));
        Assert.That(source, Does.Contain("candidatePriority <= currentPriority"));
        Assert.That(source, Does.Contain("candidateDistance >= currentDistance"));
        Assert.That(source, Does.Contain("rerouting to closer higher-acuity patient"));
        Assert.That(source, Does.Contain("mobState.CurrentState == MobState.Critical"));
        Assert.That(source, Does.Contain("totalDamage >= rescue.EvacuationMinDamage"));
        Assert.That(source, Does.Contain("totalDamage >= rescue.AutoTreatMinDamage"));
    }

    [Test]
    public void RescueAgentAutoAcquireIgnoresMinorScratchTargets()
    {
        var component = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueAgentComponent.cs"), Encoding.UTF8);
        var source = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueAgentSystem.cs"), Encoding.UTF8);

        Assert.That(component, Does.Contain("AutoAcquireMinDamage = 5f"));
        Assert.That(source, Does.Contain("TryFindRescueTarget"));
        Assert.That(source, Does.Contain("if (!HasAutoAcquireAcuity(candidate, rescue))"));
        Assert.That(source, Does.Contain("HasAutoAcquireAcuity"));
        Assert.That(source, Does.Contain("mobState.CurrentState == MobState.Critical"));
        Assert.That(source, Does.Contain("damage.TotalDamage.Float() >= rescue.AutoAcquireMinDamage"));
    }

    [Test]
    public void RescueAgentReportsMovedAndLostPatientTargets()
    {
        var component = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueAgentComponent.cs"), Encoding.UTF8);
        var source = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueAgentSystem.cs"), Encoding.UTF8);

        Assert.That(component, Does.Contain("LastTargetTrackingStatus"));
        Assert.That(source, Does.Contain("UpdateTargetTrackingStatus(uid, rescue, htn);"));
        Assert.That(source, Does.Contain("targetTrack={rescue.LastTargetTrackingStatus}"));
        Assert.That(source, Does.Contain("target_lost:"));
        Assert.That(source, Does.Contain("target_moved:"));
        Assert.That(source, Does.Contain("distance > rescue.SearchRange"));
        Assert.That(source, Does.Contain("ClearFollowTarget(uid, rescue, htn)"));
        Assert.That(source, Does.Contain("ClearArrivalReportTarget(rescue, target)"));
        Assert.That(source, Does.Contain("ClearTriageDecisionTarget(rescue, target)"));
        Assert.That(source, Does.Contain("ClearDeathSignalTarget(rescue, target)"));
    }

    [Test]
    public void RescueAgentReportsClearAndDelayedRoutes()
    {
        var source = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueAgentSystem.cs"), Encoding.UTF8);

        Assert.That(source, Does.Contain("route_clear: target={FormatEntityRef(target)}"));
        Assert.That(source, Does.Contain("route_delayed: target={FormatEntityRef(target)}"));
        Assert.That(source, Does.Contain("rescue.LastTargetTrackingStatus.StartsWith(\"route_\", StringComparison.Ordinal)"));
        Assert.That(source, Does.Contain("stalled={rescue.TargetStallAccumulator:0.0}/{rescue.TargetStallSeconds:0.0}s"));
        Assert.That(source, Does.Contain("goal={FormatEntityRef(progressGoal)}"));
        Assert.That(source, Does.Contain("distance={distance:0.0}"));
    }

    [Test]
    public void RescueAgentAutoDefibsDeadPatientsWithStandardDefibrillatorSystem()
    {
        var component = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueAgentComponent.cs"), Encoding.UTF8);
        var source = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueAgentSystem.cs"), Encoding.UTF8);

        Assert.That(component, Does.Contain("AutoDefibDeadPatients = true"));
        Assert.That(component, Does.Contain("AutoDefibCooldown = 8f"));
        Assert.That(component, Does.Contain("NextAutoDefibAttempt"));
        Assert.That(component, Does.Contain("LastAutoDefibStatus"));
        Assert.That(source, Does.Contain("DefibrillatorSystem"));
        Assert.That(source, Does.Contain("ItemToggleSystem"));
        Assert.That(source, Does.Contain("TryAutoDefibTarget"));
        Assert.That(source, Does.Contain("TryAutoDefibDeadPatientOnShuttle"));
        Assert.That(source, Does.Contain("TryFindDefibrillatorItem"));
        Assert.That(source, Does.Contain("_itemToggle.TryActivate(defib, uid)"));
        Assert.That(source, Does.Contain("_defibrillator.TryStartZap(defib, target, uid)"));
        Assert.That(source, Does.Contain("defibrillation in progress"));
        Assert.That(source, Does.Contain("defibrillating onboard"));
        Assert.That(source, Does.Contain("autoDefib={rescue.LastAutoDefibStatus}"));
    }

    [Test]
    public void RescueAgentReportsDefibAndShuttleCarePhasesOverComms()
    {
        var component = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueAgentComponent.cs"), Encoding.UTF8);
        var source = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueAgentSystem.cs"), Encoding.UTF8);

        Assert.That(component, Does.Contain("AutoCommsCooldown = 10f"));
        Assert.That(component, Does.Contain("NextAutoCommsAt"));
        Assert.That(component, Does.Contain("LastAutoCommsKey"));
        Assert.That(component, Does.Contain("ArrivalReportedTarget"));
        Assert.That(component, Does.Contain("LastArrivalReportStatus"));
        Assert.That(component, Does.Contain("TriageReportedTarget"));
        Assert.That(component, Does.Contain("LastTriageDecisionKey"));
        Assert.That(component, Does.Contain("LastTriageDecisionStatus"));
        Assert.That(component, Does.Contain("OnboardCareTarget"));
        Assert.That(component, Does.Contain("LastOnboardCareStatus"));
        Assert.That(component, Does.Contain("LastOnboardActionStatus"));
        Assert.That(component, Does.Contain("LastRescueActionStatus"));
        Assert.That(component, Does.Contain("OnboardActionCooldown = 10f"));
        Assert.That(component, Does.Contain("RescueActionCooldown = 6f"));
        Assert.That(component, Does.Contain("LastOnboardActionKey"));
        Assert.That(component, Does.Contain("NextOnboardActionAt"));
        Assert.That(component, Does.Contain("LastRescueActionKey"));
        Assert.That(component, Does.Contain("NextRescueActionAt"));
        Assert.That(source, Does.Contain("SubscribeLocalEvent<MobStateComponent, TargetDefibrillatedEvent>(OnTargetDefibrillated)"));
        Assert.That(source, Does.Contain("TrySendRescueStatusComms"));
        Assert.That(source, Does.Contain("TryReportPatientArrival"));
        Assert.That(source, Does.Contain("patient-arrival:"));
        Assert.That(source, Does.Contain("ClearArrivalReportTarget"));
        Assert.That(source, Does.Contain("TryReportTriageDecision"));
        Assert.That(source, Does.Contain("triage-decision:"));
        Assert.That(source, Does.Contain("GetTriageDecisionKey"));
        Assert.That(source, Does.Contain("BuildTriageDecisionMessage"));
        Assert.That(source, Does.Contain("FormatMobStateForTriage"));
        Assert.That(source, Does.Contain("ClearTriageDecisionTarget"));
        Assert.That(source, Does.Contain("_chat.TrySendInGameICMessage"));
        Assert.That(source, Does.Contain("_radio.SendRadioMessage"));
        Assert.That(source, Does.Contain("MedicalRadioChannel"));
        Assert.That(source, Does.Contain("autoComms={rescue.LastAutoCommsKey}"));
        Assert.That(source, Does.Contain("rescueAction={rescue.LastRescueActionStatus}"));
        Assert.That(source, Does.Contain("onboardCare={rescue.LastOnboardCareStatus}"));
        Assert.That(source, Does.Contain("onboardAction={rescue.LastOnboardActionStatus}"));
        Assert.That(source, Does.Contain("arrival={rescue.LastArrivalReportStatus}"));
        Assert.That(source, Does.Contain("triageDecision={rescue.LastTriageDecisionStatus}"));
        Assert.That(source, Does.Contain("defib-start:"));
        Assert.That(source, Does.Contain("defib-success:"));
        Assert.That(source, Does.Contain("defib-failed:"));
        Assert.That(source, Does.Contain("patient-onboard-dead:"));
        Assert.That(source, Does.Contain("ReportLivingPatientOnboardStatus"));
        Assert.That(source, Does.Contain("patient-onboard-critical:"));
        Assert.That(source, Does.Contain("patient-onboard:"));
        Assert.That(source, Does.Contain("patient-hold-dead:"));
        Assert.That(source, Does.Contain("patient-hold-critical:"));
        Assert.That(source, Does.Contain("patient-hold-treatment:"));
        Assert.That(source, Does.Contain("patient-release:"));
        Assert.That(source, Does.Contain("SetOnboardCareStatus"));
        Assert.That(source, Does.Contain("MarkOnboardCareReleased"));
        Assert.That(source, Does.Contain("if (TryTreatOnboardPatient(uid, rescue, htn))"));
        Assert.That(source, Does.Contain("TryFindOnboardTreatmentPatient"));
        Assert.That(source, Does.Contain("TryAutoTreatTarget(uid, rescue, htn, patient)"));
        Assert.That(source, Does.Contain("treating onboard"));
        Assert.That(source, Does.Contain("needs onboard treatment"));
        Assert.That(source, Does.Contain("CompleteReleasedPatientCare"));
        Assert.That(source, Does.Contain("released stabilized {FormatEntityRef(patient)}; ready for next rescue"));
        Assert.That(source, Does.Contain("holding shuttle forward after release of {FormatEntityRef(patient)}; pending rescue target detected"));
        Assert.That(source, Does.Contain("StandbyAtAssignedShuttle(uid, rescue, htn, allowAutoReturn: !hasPendingRescueTarget)"));
        Assert.That(source, Does.Contain("TrySayOnboardAction"));
        Assert.That(source, Does.Contain("TrySayRescueAction"));
        Assert.That(source, Does.Contain("onboard-action:{step}:{patient}"));
        Assert.That(source, Does.Contain("rescue-action:{step}:{patient}"));
        Assert.That(source, Does.Contain("rescue.NextOnboardActionAt > now"));
        Assert.That(source, Does.Contain("rescue.NextRescueActionAt > now"));
        Assert.That(source, Does.Contain("rescue.OnboardActionCooldown"));
        Assert.That(source, Does.Contain("rescue.RescueActionCooldown"));
        Assert.That(source, Does.Contain("\"approach-patient\""));
        Assert.That(source, Does.Contain("\"pull-start\""));
        Assert.That(source, Does.Contain("\"deliver-bed\""));
        Assert.That(source, Does.Contain("\"deliver-shuttle\""));
        Assert.That(source, Does.Contain("\"treat-onsite\""));
        Assert.That(source, Does.Contain("\"defib-approach\""));
        Assert.That(source, Does.Contain("patient pull started"));
        Assert.That(source, Does.Contain("moving patient to shuttle bed"));
        Assert.That(source, Does.Contain("treating patient on scene"));
        Assert.That(source, Does.Contain("!IsOnAssignedShuttle(target, rescue)"));
        Assert.That(source, Does.Contain("\"boarded\""));
        Assert.That(source, Does.Contain("\"treating-critical\""));
        Assert.That(source, Does.Contain("\"treating\""));
        Assert.That(source, Does.Contain("\"reanimation\""));
        Assert.That(source, Does.Contain("\"onboard-defib\""));
        Assert.That(source, Does.Contain("\"onboard-treatment\""));
        Assert.That(source, Does.Contain("\"release-ready\""));
        Assert.That(source, Does.Contain("\"released\""));
        Assert.That(source, Does.Contain("patient secured onboard"));
        Assert.That(source, Does.Contain("critical onboard treatment"));
        Assert.That(source, Does.Contain("onboard treatment and observation"));
        Assert.That(source, Does.Contain("onboard treatment loop"));
        Assert.That(source, Does.Contain("onboard reanimation cycle"));
        Assert.That(source, Does.Contain("dead recovery defib cycle"));
        Assert.That(source, Does.Contain("patient stable, preparing release"));
        Assert.That(source, Does.Contain("patient released from shuttle care"));
        Assert.That(source, Does.Contain("\\u0411\\u0435\\u0433\\u0443 \\u043a \\u043f\\u0430\\u0446\\u0438\\u0435\\u043d\\u0442\\u0443 {Name(target)}"));
        Assert.That(source, Does.Contain("\\u041f\\u0430\\u0446\\u0438\\u0435\\u043d\\u0442 {Name(target)} \\u043d\\u0430 \\u043c\\u043d\\u0435"));
        Assert.That(source, Does.Contain("\\u0422\\u0430\\u0449\\u0443 {Name(target)} \\u043d\\u0430 \\u0431\\u043e\\u0440\\u0442"));
        Assert.That(source, Does.Contain("\\u041b\\u0435\\u0447\\u0443 {Name(target)} \\u043d\\u0430 \\u043c\\u0435\\u0441\\u0442\\u0435"));
        Assert.That(source, Does.Contain("onboard-care: patient={FormatEntityRef(patient)}"));
        Assert.That(source, Does.Contain("critical; treatment continuing"));
        Assert.That(source, Does.Contain("dead recovery; defib cycle pending"));
        Assert.That(source, Does.Contain("release-ready; {status}"));
        Assert.That(source, Does.Contain("onboard-care released: patient={FormatEntityRef(patient)}"));
        Assert.That(source, Does.Contain("dead-recovery"));
        Assert.That(source, Does.Contain("unsafe-evacuation"));
        Assert.That(source, Does.Contain("onsite-treatment"));
        Assert.That(source, Does.Contain("hasPendingRescueTarget"));
        Assert.That(source, Does.Contain("\u0414\u0435\u0444\u0438\u0431\u0440\u0438\u043b\u043b\u044f\u0446\u0438\u044f {Name(target)} \u043d\u0430\u0447\u0430\u0442\u0430. \u041d\u0435 \u0442\u0440\u043e\u0433\u0430\u0439\u0442\u0435 \u043f\u0430\u0446\u0438\u0435\u043d\u0442\u0430."));
        Assert.That(source, Does.Contain("\\u041f\\u0430\\u0446\\u0438\\u0435\\u043d\\u0442 {Name(target)} \\u043d\\u0430\\u0439\\u0434\\u0435\\u043d"));
        Assert.That(source, Does.Contain("\\u0422\\u0440\\u0438\\u0430\\u0436 {name}:"));
        Assert.That(source, Does.Contain("\u041f\u0443\u043b\u044c\u0441 {targetName} \u0432\u043e\u0441\u0441\u0442\u0430\u043d\u043e\u0432\u043b\u0435\u043d. \u041f\u0440\u043e\u0434\u043e\u043b\u0436\u0430\u044e \u0441\u0442\u0430\u0431\u0438\u043b\u0438\u0437\u0430\u0446\u0438\u044e."));
        Assert.That(source, Does.Contain("\u041f\u0430\u0446\u0438\u0435\u043d\u0442 {Name(target)} \u043d\u0430 \u0431\u043e\u0440\u0442\u0443. \u041d\u0430\u0447\u0438\u043d\u0430\u044e \u0440\u0435\u0430\u043d\u0438\u043c\u0430\u0446\u0438\u043e\u043d\u043d\u044b\u0439 \u0446\u0438\u043a\u043b."));
        Assert.That(source, Does.Contain("\u041f\u0430\u0446\u0438\u0435\u043d\u0442 {Name(patient)} \u0441\u0442\u0430\u0431\u0438\u043b\u0435\u043d. \u041e\u0442\u043f\u0443\u0441\u043a\u0430\u044e \u0441 \u0431\u043e\u0440\u0442\u0430."));
    }

    [Test]
    public void RescueTeamConfirmsCoverAfterAibolitTriageDecision()
    {
        var component = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueTeamComponent.cs"), Encoding.UTF8);
        var source = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueTeamSystem.cs"), Encoding.UTF8);

        Assert.That(component, Does.Contain("TriageCoverConfirmedPatient"));
        Assert.That(component, Does.Contain("LastTriageCoverDecisionKey"));
        Assert.That(component, Does.Contain("LastTriageCoverStatus"));
        Assert.That(component, Does.Contain("NextTriageCoverConfirmAt"));
        Assert.That(source, Does.Contain("TriageCoverConfirmCooldownSeconds"));
        Assert.That(source, Does.Contain("TryConfirmTriageCover"));
        Assert.That(source, Does.Contain("rescue.TriageReportedTarget != patientUid"));
        Assert.That(source, Does.Contain("rescue.LastTriageDecisionKey"));
        Assert.That(source, Does.Contain("SelectTriageCoverRole"));
        Assert.That(source, Does.Contain("TryFindEscortByRole"));
        Assert.That(source, Does.Contain("TryFindAnyEscort"));
        Assert.That(source, Does.Contain("BuildTriageCoverLine"));
        Assert.That(source, Does.Contain("triage-cover:"));
        Assert.That(source, Does.Contain("triageCover={team.LastTriageCoverStatus}"));
        Assert.That(source, Does.Contain("escort.LastDutyActionStatus = $\"triage-cover:{decisionKey}"));
        Assert.That(source, Does.Contain("HasThreatPressure(team) || decisionKey == \"unsafe-evacuation\""));
        Assert.That(source, Does.Contain("decisionKey == \"critical-evacuation\""));
        Assert.That(source, Does.Contain("decisionKey == \"heavy-evacuation\""));
        Assert.That(source, Does.Contain("\\u041f\\u0435\\u0440\\u0438\\u043c\\u0435\\u0442\\u0440 \\u0441\\u0442\\u0430\\u0431\\u0438\\u043b\\u0435\\u043d"));
        Assert.That(source, Does.Contain("\\u041c\\u0430\\u0440\\u0448\\u0440\\u0443\\u0442 \\u043a \\u0448\\u0430\\u0442\\u0442\\u043b\\u0443"));
    }

    [Test]
    public void RescueTeamAnnouncesPhaseBarksForReadablePlayerScene()
    {
        var component = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueTeamComponent.cs"), Encoding.UTF8);
        var source = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueTeamSystem.cs"), Encoding.UTF8);

        Assert.That(component, Does.Contain("LastAnnouncedPhase"));
        Assert.That(component, Does.Contain("LastAnnouncedSomberScene"));
        Assert.That(component, Does.Contain("LastPhaseAnnouncementStatus"));
        Assert.That(component, Does.Contain("NextPhaseAnnouncementAt"));
        Assert.That(component, Does.Contain("LastSharedSpeechStatus"));
        Assert.That(component, Does.Contain("NextSharedSpeechAt"));
        Assert.That(source, Does.Contain("TeamPhaseAnnouncementCooldownSeconds"));
        Assert.That(source, Does.Contain("TeamSharedSpeechCooldownSeconds = 6"));
        Assert.That(source, Does.Contain("TrySayTeamPhaseLine"));
        Assert.That(source, Does.Contain("TryReserveTeamSpeech"));
        Assert.That(source, Does.Contain("SetTeamSharedSpeechStatus"));
        Assert.That(source, Does.Contain("shared-speech waiting: {key}"));
        Assert.That(source, Does.Contain("shared-speech:{key}"));
        Assert.That(source, Does.Contain("SetTeamPhaseAnnouncementStatus"));
        Assert.That(source, Does.Contain("TryBuildTeamPhaseLine"));
        Assert.That(source, Does.Contain("TryBuildTeamPhaseLine(phase, somberScene"));
        Assert.That(source, Does.Contain("phase-bark:"));
        Assert.That(source, Does.Contain("phase-bark waiting shared speech"));
        Assert.That(source, Does.Contain("somber={somberScene}"));
        Assert.That(source, Does.Contain("phaseBark={team.LastPhaseAnnouncementStatus}"));
        Assert.That(source, Does.Contain("speech={team.LastSharedSpeechStatus}"));
        Assert.That(source, Does.Contain("LuaMRescueTeamPhase.SecureScene"));
        Assert.That(source, Does.Contain("LuaMRescueTeamPhase.Triage"));
        Assert.That(source, Does.Contain("LuaMRescueTeamPhase.EvacuateToShuttle"));
        Assert.That(source, Does.Contain("LuaMRescueTeamPhase.Handoff"));
        Assert.That(source, Does.Contain("LuaMRescueEscortRole.Kostyl"));
        Assert.That(source, Does.Contain("LuaMRescueEscortRole.Tourniquet"));
        Assert.That(source, Does.Contain("\\u041f\\u0435\\u0440\\u0438\\u043c\\u0435\\u0442\\u0440 \\u0432\\u0437\\u044f\\u0442"));
        Assert.That(source, Does.Contain("\\u041f\\u0430\\u0446\\u0438\\u0435\\u043d\\u0442 \\u043d\\u0430\\u0439\\u0434\\u0435\\u043d"));
        Assert.That(source, Does.Contain("\\u041a\\u043e\\u0441\\u0442\\u044b\\u043b\\u044c \\u0438\\u0434\\u0435\\u0442"));
        Assert.That(source, Does.Contain("\\u0421\\u0435\\u043a\\u0442\\u043e\\u0440 \\u043e\\u0442\\u043f\\u0443\\u0441\\u043a\\u0430\\u0435\\u043c"));
    }

    [Test]
    public void RescueTeamSuppressesHumorForSomberScenes()
    {
        var component = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueTeamComponent.cs"), Encoding.UTF8);
        var source = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueTeamSystem.cs"), Encoding.UTF8);

        Assert.That(component, Does.Contain("LastAnnouncedSomberScene"));
        Assert.That(source, Does.Contain("IsSomberTeamScene"));
        Assert.That(source, Does.Contain("IsSomberEscortScene"));
        Assert.That(source, Does.Contain("IsDeadPatient"));
        Assert.That(source, Does.Contain("HasSomberStatus"));
        Assert.That(source, Does.Contain("MobState.Dead"));
        Assert.That(source, Does.Contain("GetSomberDutyLine"));
        Assert.That(source, Does.Contain("GetDutyLine(escort.Role, duty, IsSomberEscortScene(escort))"));
        Assert.That(source, Does.Contain("status.Contains(\"dead\", StringComparison.OrdinalIgnoreCase)"));
        Assert.That(source, Does.Contain("status.Contains(\"failed\", StringComparison.OrdinalIgnoreCase)"));
        Assert.That(source, Does.Contain("status.Contains(\"aborted\", StringComparison.OrdinalIgnoreCase)"));
        Assert.That(source, Does.Contain("status.Contains(\"blocked\", StringComparison.OrdinalIgnoreCase)"));
        Assert.That(source, Does.Contain("\\u0412\\u044b\\u0435\\u0437\\u0434 \\u043f\\u0440\\u0438\\u043d\\u044f\\u0442. \\u0420\\u0430\\u0431\\u043e\\u0442\\u0430\\u0435\\u043c \\u0431\\u0435\\u0437 \\u043b\\u0438\\u0448\\u043d\\u0438\\u0445 \\u0440\\u0435\\u043f\\u043b\\u0438\\u043a"));
        Assert.That(source, Does.Contain("\\u041f\\u0430\\u0446\\u0438\\u0435\\u043d\\u0442 \\u0434\\u0432\\u0438\\u0436\\u0435\\u0442\\u0441\\u044f \\u043a \\u0448\\u0430\\u0442\\u0442\\u043b\\u0443"));
        Assert.That(source, Does.Contain("\\u0411\\u0435\\u0440\\u0443 \\u043f\\u043e\\u0434\\u0434\\u0435\\u0440\\u0436\\u043a\\u0443 \\u043f\\u0430\\u0446\\u0438\\u0435\\u043d\\u0442\\u0430"));
        Assert.That(source, Does.Contain("\\u0423\\u0433\\u0440\\u043e\\u0437\\u0430 \\u043d\\u0430 \\u043c\\u043d\\u0435"));
        Assert.That(source, Does.Contain("Носилки морально готовы"));
        Assert.That(source, Does.Contain("Без пациента скучно"));
    }

    [Test]
    public void RescueTeamUsesSharedSpeechGateForDutyBarks()
    {
        var source = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueTeamSystem.cs"), Encoding.UTF8);

        Assert.That(source, Does.Contain("TryGetEscortLeaderTeam"));
        Assert.That(source, Does.Contain("var key = $\"duty:{FormatRole(escort.Role)}:{FormatDuty(duty)}\""));
        Assert.That(source, Does.Contain("TryReserveTeamSpeech(uid, team, key, now, out var speechStatusChanged)"));
        Assert.That(source, Does.Contain("Dirty(leader, team)"));
        Assert.That(source, Does.Contain("if (!reserved)"));
        Assert.That(source, Does.Contain("return;"));
        Assert.That(source, Does.Contain("shared-speech:{key}"));
        Assert.That(source, Does.Contain("cooldown={TeamSharedSpeechCooldownSeconds:0}s"));
    }

    [Test]
    public void RescueTeamThreatDetectionUsesObserverFaction()
    {
        var source = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueTeamSystem.cs"), Encoding.UTF8);

        Assert.That(source, Does.Contain("observerFaction.Factions.Any(faction => _factions.IsFactionHostile(faction, (candidate, candidateFaction)))"));
        Assert.That(source, Does.Not.Contain("IsFactionHostile(\"NanoTrasen\""));
    }

    [Test]
    public void RescueTeamPrioritizesSyntheticThreatsNearPatients()
    {
        var source = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueTeamSystem.cs"), Encoding.UTF8);

        Assert.That(source, Does.Contain("IsPrioritySyntheticThreat"));
        Assert.That(source, Does.Contain("IsSyntheticRescueActor"));
        Assert.That(source, Does.Contain("HasImmediateRescueSyntheticControl"));
        Assert.That(source, Does.Contain("LuaMAiDroneTaskComponent"));
        Assert.That(source, Does.Contain("DroneControlComponent"));
        Assert.That(source, Does.Contain("AiRemoteControllerComponent"));
        Assert.That(source, Does.Contain("SiliconComponent"));
        Assert.That(source, Does.Contain("BorgChassisComponent"));
        Assert.That(source, Does.Contain("_tag.HasTag(candidate, BotTag)"));
        Assert.That(source, Does.Contain("HasImmediateRescueSyntheticControl(candidate);"));
        Assert.That(source, Does.Contain("var syntheticThreat = IsPrioritySyntheticThreat(observer, candidate);"));
        Assert.That(source, Does.Contain("if (hostile || syntheticThreat)"));
        Assert.That(source, Does.Contain("if (hostile || syntheticThreat || activeCombatant)"));
        Assert.That(source, Does.Contain("var syntheticThreat = IsPrioritySyntheticThreat(uid, threatUid);"));
        Assert.That(source, Does.Contain("!IsHostileToObserver(uid, threatUid) && !syntheticThreat"));
        Assert.That(source, Does.Contain("threat-screen advancing to synthetic"));
        Assert.That(source, Does.Contain("threat-screen engaging synthetic"));
    }

    [Test]
    public void RescueTeamReportsNeutralizedThreats()
    {
        var component = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueTeamComponent.cs"), Encoding.UTF8);
        var source = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueTeamSystem.cs"), Encoding.UTF8);

        Assert.That(component, Does.Contain("LastThreatNeutralizedTarget"));
        Assert.That(component, Does.Contain("LastThreatNeutralizedBy"));
        Assert.That(component, Does.Contain("LastThreatNeutralizedStatus"));
        Assert.That(component, Does.Contain("NextThreatNeutralizedReportAt"));
        Assert.That(source, Does.Contain("ThreatNeutralizedReportCooldownSeconds = 6"));
        Assert.That(source, Does.Contain("TryReportThreatNeutralized"));
        Assert.That(source, Does.Contain("SetThreatNeutralizedStatus"));
        Assert.That(source, Does.Contain("BuildThreatNeutralizedLine"));
        Assert.That(source, Does.Contain("team.LastThreatNeutralizedTarget == threatUid"));
        Assert.That(source, Does.Contain("threat-neutralized:"));
        Assert.That(source, Does.Contain("threat-screen target neutralized and reported"));
        Assert.That(source, Does.Contain("threatClear={team.LastThreatNeutralizedStatus}"));
        Assert.That(source, Does.Contain("\\u0421\\u0438\\u043d\\u0442\\u0435\\u0442\\u0438\\u0447\\u0435\\u0441\\u043a\\u0430\\u044f \\u0443\\u0433\\u0440\\u043e\\u0437\\u0430"));
        Assert.That(source, Does.Contain("\\u0423\\u0433\\u0440\\u043e\\u0437\\u0430 {targetName} \\u043d\\u0435\\u0439\\u0442\\u0440\\u0430\\u043b\\u0438\\u0437\\u043e\\u0432\\u0430\\u043d\\u0430"));
    }

    [Test]
    public void RescueEscortsAskCrewForOperationalHelp()
    {
        var component = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueTeamComponent.cs"), Encoding.UTF8);
        var source = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueTeamSystem.cs"), Encoding.UTF8);

        Assert.That(component, Does.Contain("LastCrewHelpStatus"));
        Assert.That(component, Does.Contain("LastCrewHelpKey"));
        Assert.That(component, Does.Contain("NextCrewHelpRequestAt"));
        Assert.That(source, Does.Contain("CrewHelpRequestCooldownSeconds = 12"));
        Assert.That(source, Does.Contain("TryRequestCrewHelp"));
        Assert.That(source, Does.Contain("crew-help:{key}; {status}"));
        Assert.That(source, Does.Contain("crewHelp={escort.LastCrewHelpStatus}"));
        Assert.That(source, Does.Contain("escort.NextCrewHelpRequestAt > now"));
        Assert.That(source, Does.Contain("mark-safe-path"));
        Assert.That(source, Does.Contain("open-route:"));
        Assert.That(source, Does.Contain("clear-blocker:"));
        Assert.That(source, Does.Contain("hold-corridor:"));
        Assert.That(source, Does.Contain("remove-blocker:"));
        Assert.That(source, Does.Contain("drop-blocker:"));
        Assert.That(source, Does.Contain("step-away"));
        Assert.That(source, Does.Contain("stretcher-needed:"));
        Assert.That(source, Does.Contain("stretcher-moving:"));
        Assert.That(source, Does.Contain("stretcher-coordinate:"));
        Assert.That(source, Does.Contain("stretcher-ready:"));
        Assert.That(source, Does.Contain("stretcher-escort:"));
        Assert.That(source, Does.Contain("stretcher-blocked:"));
        Assert.That(source, Does.Contain("open route around non-pullable"));
        Assert.That(source, Does.Contain("manual removal needed"));
        Assert.That(source, Does.Contain("crowd control warning"));
        Assert.That(source, Does.Contain("patient assist pull blocked"));
        Assert.That(source, Does.Contain("moving patient {FormatEntityRef(patientUid)} to shuttle"));
        Assert.That(source, Does.Contain("var followTarget = GetEscortFollowTarget(uid, escort, duty);"));
        Assert.That(source, Does.Contain("GetPatientSupportFollowTarget"));
        Assert.That(source, Does.Contain("IsEscortPullingPatient(uid, patient)"));
        Assert.That(source, Does.Contain("return shuttleAnchor ?? shuttle ?? leader ?? patient;"));
        Assert.That(source, Does.Contain("patient-assist escorting"));
    }

    [Test]
    public void RescueTeamClearsCompletedPatientForReturnOrExtract()
    {
        var component = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueTeamComponent.cs"), Encoding.UTF8);
        var source = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueTeamSystem.cs"), Encoding.UTF8);

        Assert.That(component, Does.Contain("LastReturnOrExtractReasonStatus"));
        Assert.That(source, Does.Contain("GetActiveRescuePatient"));
        Assert.That(source, Does.Contain("var patient = GetActiveRescuePatient(rescue);"));
        Assert.That(source, Does.Contain("escort.Patient = GetActiveRescuePatient(rescue);"));
        Assert.That(source, Does.Not.Contain("?? team.Patient"));
        Assert.That(source, Does.Contain("LuaMRescueTeamPhase.ReturnOrExtract"));
        Assert.That(source, Does.Contain("plan return-to-shuttle: extraction phase"));
        Assert.That(source, Does.Contain("returning or extracting"));
        Assert.That(source, Does.Contain("BuildReturnOrExtractReasonStatus"));
        Assert.That(source, Does.Contain("BuildReturnOrExtractLine"));
        Assert.That(source, Does.Contain("returnReason={team.LastReturnOrExtractReasonStatus}"));
        Assert.That(source, Does.Contain("returning or extracting: {returnOrExtractReason}"));
        Assert.That(source, Does.Contain("reason=route-blocked"));
        Assert.That(source, Does.Contain("reason=unsafe-scene"));
        Assert.That(source, Does.Contain("reason=failed-or-aborted"));
        Assert.That(source, Does.Contain("reason=dead-recovery"));
        Assert.That(source, Does.Contain("reason=handoff-complete"));
        Assert.That(source, Does.Contain("HasRouteBlockedStatus"));
        Assert.That(source, Does.Contain("HasUnsafeStatus"));
        Assert.That(source, Does.Contain("HasFailedStatus"));
        Assert.That(source, Does.Contain("HasDeadStatus"));
        Assert.That(source, Does.Contain("\\u041e\\u0442\\u0445\\u043e\\u0434\\u0438\\u043c: \\u043c\\u0430\\u0440\\u0448\\u0440\\u0443\\u0442 \\u0437\\u0430\\u0431\\u043b\\u043e\\u043a\\u0438\\u0440\\u043e\\u0432\\u0430\\u043d"));
        Assert.That(source, Does.Contain("\\u041e\\u0442\\u0445\\u043e\\u0434\\u0438\\u043c: \\u0437\\u043e\\u043d\\u0430 \\u043d\\u0435 \\u0434\\u0435\\u0440\\u0436\\u0438\\u0442\\u0441\\u044f"));
        Assert.That(source, Does.Contain("\\u041e\\u0442\\u0445\\u043e\\u0434\\u0438\\u043c: \\u0437\\u0430\\u0434\\u0430\\u0447\\u0430 \\u0441\\u043e\\u0440\\u0432\\u0430\\u043d\\u0430"));
        Assert.That(source, Does.Contain("TryRunReturnToShuttleAction"));
        Assert.That(source, Does.Contain("TryReleaseReturnPull"));
        Assert.That(source, Does.Contain("puller.Pulling is not { Valid: true } pulled"));
        Assert.That(source, Does.Contain("_pulling.TryStopPull(pulled, pullable, uid)"));
        Assert.That(source, Does.Contain("return-to-shuttle released pull"));
        Assert.That(source, Does.Contain("return-to-shuttle moving to"));
        Assert.That(source, Does.Contain("return-to-shuttle ready at"));
        Assert.That(source, Does.Contain("return-to-shuttle no shuttle target"));
    }

    [Test]
    public void RescueTeamRecordsHandoffAfterActionDigest()
    {
        var component = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueTeamComponent.cs"), Encoding.UTF8);
        var agent = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueAgentSystem.cs"), Encoding.UTF8);
        var team = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueTeamSystem.cs"), Encoding.UTF8);
        var sectorMemory = File.ReadAllText(FullPath("Content.Server/_LuaM/Sector/LuaMSectorMemoryComponent.cs"), Encoding.UTF8);
        var sectorStory = File.ReadAllText(FullPath("Content.Server/_LuaM/Sector/LuaMSectorStorySystem.cs"), Encoding.UTF8);

        Assert.That(component, Does.Contain("LastHandoffPatient"));
        Assert.That(component, Does.Contain("LastHandoffRecord"));
        Assert.That(component, Does.Contain("LastHandoffDigest"));
        Assert.That(component, Does.Contain("HandoffRecords"));
        Assert.That(team, Does.Contain("TryRecordRescueHandoff"));
        Assert.That(team, Does.Contain("handoff record #"));
        Assert.That(team, Does.Contain("after-action record #"));
        Assert.That(team, Does.Contain("HandoffPhaseHoldSeconds"));
        Assert.That(team, Does.Contain("handoff complete:"));
        Assert.That(team, Does.Contain("plan return-to-shuttle: handoff recorded"));
        Assert.That(team, Does.Contain("playerContribution="));
        Assert.That(team, Does.Contain("teamStatus="));
        Assert.That(team, Does.Contain("handoff={team.LastHandoffRecord}"));
        Assert.That(team, Does.Contain("handoff={team.LastHandoffDigest}"));
        Assert.That(team, Does.Contain("TryRecordRescueAfterAction"));
        Assert.That(agent, Does.Contain("RecordRescueHandoff"));
        Assert.That(agent, Does.Contain("stabilized on site; no evacuation required"));
        Assert.That(agent, Does.Contain("secured onboard; return route requested"));
        Assert.That(agent, Does.Contain("released from shuttle care"));
        Assert.That(agent, Does.Contain("aborted after stalled target"));
        Assert.That(agent, Does.Contain("BuildHandoffBlockerSummary"));
        Assert.That(agent, Does.Contain("BuildHandoffBlockerSummary(team, crewHelp)"));
        Assert.That(agent, Does.Contain("BuildHandoffTriageCoverSummary"));
        Assert.That(agent, Does.Contain("triageCover={triageCover}"));
        Assert.That(agent, Does.Contain("LastTriageCoverStatus"));
        Assert.That(agent, Does.Contain("BuildHandoffThreatClearSummary"));
        Assert.That(agent, Does.Contain("threatClear={threatClear}"));
        Assert.That(agent, Does.Contain("LastThreatNeutralizedStatus"));
        Assert.That(agent, Does.Contain("BuildHandoffCrewHelpSummary"));
        Assert.That(agent, Does.Contain("FormatEscortRole"));
        Assert.That(agent, Does.Contain("crew-help requested:"));
        Assert.That(agent, Does.Contain("crewHelp={crewHelp}"));
        Assert.That(agent, Does.Contain("BuildPatientTreatmentResult"));
        Assert.That(sectorMemory, Does.Contain("LuaMSectorRescueAfterActionEntry"));
        Assert.That(sectorMemory, Does.Contain("RescueAfterActions"));
        Assert.That(sectorStory, Does.Contain("TryRecordRescueAfterAction"));
        Assert.That(sectorStory, Does.Contain("RescueAfterActionLimit"));
        Assert.That(sectorStory, Does.Contain("LuaMSectorPersistedRescueAfterAction"));
        Assert.That(sectorStory, Does.Contain("ToPersistedRescueAfterAction"));
        Assert.That(sectorStory, Does.Contain("FromPersistedRescueAfterAction"));
        Assert.That(sectorStory, Does.Contain("LuaMSectorRescueAfterActionRecordedEvent"));
        Assert.That(sectorStory, Does.Contain("\"Rescue\""));
        Assert.That(sectorStory, Does.Contain("RescueAfterActionStoryId"));
    }

    [Test]
    public void RescueEscortsActivelyScreenHostileThreatTargets()
    {
        var source = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueTeamSystem.cs"), Encoding.UTF8);
        var component = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueTeamComponent.cs"), Encoding.UTF8);

        Assert.That(source, Does.Contain("TryRunThreatScreenAction"));
        Assert.That(source, Does.Contain("EscortThreatScreenRange = 7f"));
        Assert.That(source, Does.Contain("EscortThreatLeashRange = 8.5f"));
        Assert.That(source, Does.Contain("duty != LuaMRescueEscortDuty.ThreatScreen"));
        Assert.That(component, Does.Contain("LastWeaponReadinessStatus"));
        Assert.That(source, Does.Contain("EscortCombatStorageSlotPriority"));
        Assert.That(source, Does.Contain("TryEnsureEscortWeaponReady"));
        Assert.That(source, Does.Contain("IsEscortCombatReadinessDuty"));
        Assert.That(source, Does.Contain("TrySelectHeldEscortWeapon"));
        Assert.That(source, Does.Contain("TryTakeStoredEscortWeapon"));
        Assert.That(source, Does.Contain("TryMakeRoomForEscortWeapon"));
        Assert.That(source, Does.Contain("TrySelectStoredEscortWeapon"));
        Assert.That(source, Does.Contain("HasComp<GunComponent>(item)"));
        Assert.That(source, Does.Contain("weapon={escort.LastWeaponReadinessStatus}"));
        Assert.That(source, Does.Contain("weapon-ready held"));
        Assert.That(source, Does.Contain("no empty hand for stored weapon"));
        Assert.That(source, Does.Contain("stowed {FormatEntityRef(held)}"));
        Assert.That(source, Does.Contain("NPCBlackboard.CurrentOrderedTarget"));
        Assert.That(source, Does.Contain("_combatMode.SetInCombatMode(uid, true, combat)"));
        Assert.That(source, Does.Contain("GetThreatScreenFollowTarget"));
        Assert.That(source, Does.Contain("IsThreatWithinRescueLeash"));
        Assert.That(source, Does.Contain("TryGetThreatLeashAnchor"));
        Assert.That(source, Does.Contain("IsWithinRange(anchor, threat, EscortThreatLeashRange)"));
        Assert.That(source, Does.Contain("threat-screen leash holding rescue perimeter"));
        Assert.That(source, Does.Contain("return sceneAnchor ?? patient ?? leader ?? shuttleAnchor ?? shuttle;"));
        Assert.That(source, Does.Contain("IsHostileToObserver(uid, threatUid)"));
        Assert.That(source, Does.Contain("threat-screen advancing to hostile"));
        Assert.That(source, Does.Contain("threat-screen engaging hostile"));
        Assert.That(source, Does.Contain("threat-screen screening armed pressure"));
        Assert.That(source, Does.Contain("threat-screen target neutralized"));
    }

    [Test]
    public void RescueEscortsDragRouteBlockersToDropoff()
    {
        var source = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueTeamSystem.cs"), Encoding.UTF8);

        Assert.That(source, Does.Contain("RouteBlockerDropoffDistance = 3.5f"));
        Assert.That(source, Does.Contain("RouteBlockerReleaseDistance = 3.25f"));
        Assert.That(source, Does.Contain("TryRunClearRouteAction(uid, escort, htn, followTarget)"));
        Assert.That(source, Does.Contain("TryFinishClearRouteBlockerAtDropoff"));
        Assert.That(source, Does.Contain("IsRouteBlockerClearOfRescueCorridor"));
        Assert.That(source, Does.Contain("IsRouteBlockerWithinRescueCorridor"));
        Assert.That(source, Does.Contain("TrySetClearRouteDropoffTarget"));
        Assert.That(source, Does.Contain("GetClearRouteDropoffDirection"));
        Assert.That(source, Does.Contain("puller.Pulling != blockerUid"));
        Assert.That(source, Does.Contain("_pulling.TryStopPull(blockerUid, pullable, uid)"));
        Assert.That(source, Does.Contain("blockerXform.Coordinates.Offset(direction * RouteBlockerDropoffDistance)"));
        Assert.That(source, Does.Contain("_npc.SetBlackboard(uid, NPCBlackboard.FollowTarget, dropoff, htn)"));
        Assert.That(source, Does.Contain("_npc.SetBlackboard(uid, \"FollowCloseRange\", 0.75f, htn)"));
        Assert.That(source, Does.Contain("_npc.SetBlackboard(uid, \"FollowRange\", 1.5f, htn)"));
        Assert.That(source, Does.Contain("clear-route dragging"));
        Assert.That(source, Does.Contain("clear-route dropped"));
        Assert.That(source, Does.Contain("clearance {distance:0.0}/{RouteBlockerReleaseDistance:0.0}m"));
        Assert.That(source, Does.Contain("Vector2.Normalize(away)"));
    }

    private static void AssertLoadout(YamlSequenceNode entities, string prototypeId, string loadoutId)
    {
        var entity = FindPrototype(entities, prototypeId);
        var loadout = FindComponent(entity, "Loadout");

        Assert.That(SequenceValues(Sequence(loadout, "prototypes")), Is.EquivalentTo(new[] { loadoutId }));
        AssertNavInteractBool(entity);
    }

    private static void AssertNavInteractBool(YamlMappingNode entity)
    {
        var htn = FindComponent(entity, "HTN");
        var blackboard = Mapping(htn, "blackboard");
        var navInteract = Scalar(blackboard, "NavInteract");

        Assert.That(navInteract.Tag.Value, Is.EqualTo("!type:Bool"));
        Assert.That(navInteract.Value, Is.EqualTo("true"));
    }

    private static string GetCompoundTask(YamlSequenceNode branches, int branch)
    {
        var tasks = Sequence((YamlMappingNode) branches.Children[branch], "tasks");
        Assert.That(tasks.Children, Has.Count.EqualTo(1));
        return ScalarValue((YamlMappingNode) tasks.Children[0], "task");
    }

    private static YamlMappingNode FindComponent(YamlMappingNode entity, string componentType)
    {
        var matches = Sequence(entity, "components")
            .Children
            .OfType<YamlMappingNode>()
            .Where(component => ScalarValue(component, "type") == componentType)
            .ToList();

        Assert.That(matches, Has.Count.EqualTo(1), $"Expected one {componentType} component.");
        return matches[0];
    }

    private static YamlMappingNode FindPrototype(YamlSequenceNode prototypes, string id)
    {
        var matches = prototypes
            .Children
            .OfType<YamlMappingNode>()
            .Where(prototype => ScalarValue(prototype, "id") == id)
            .ToList();

        Assert.That(matches, Has.Count.EqualTo(1), $"Expected one prototype with id {id}.");
        return matches[0];
    }

    private static string[] SequenceValues(YamlSequenceNode sequence)
    {
        return sequence.Children.Select(ScalarValue).ToArray();
    }

    private static YamlSequenceNode LoadSequence(string relativePath)
    {
        using var reader = new StreamReader(FullPath(relativePath), Encoding.UTF8);
        var stream = new YamlStream();
        stream.Load(reader);

        Assert.That(stream.Documents, Has.Count.EqualTo(1));
        Assert.That(stream.Documents[0].RootNode, Is.TypeOf<YamlSequenceNode>());
        return (YamlSequenceNode) stream.Documents[0].RootNode;
    }

    private static YamlMappingNode Mapping(YamlMappingNode mapping, string key)
    {
        var node = Node(mapping, key);
        Assert.That(node, Is.TypeOf<YamlMappingNode>(), $"Expected YAML key {key} to be a mapping.");
        return (YamlMappingNode) node;
    }

    private static YamlSequenceNode Sequence(YamlMappingNode mapping, string key)
    {
        var node = Node(mapping, key);
        Assert.That(node, Is.TypeOf<YamlSequenceNode>(), $"Expected YAML key {key} to be a sequence.");
        return (YamlSequenceNode) node;
    }

    private static YamlScalarNode Scalar(YamlMappingNode mapping, string key)
    {
        var node = Node(mapping, key);
        Assert.That(node, Is.TypeOf<YamlScalarNode>(), $"Expected YAML key {key} to be a scalar.");
        return (YamlScalarNode) node;
    }

    private static string ScalarValue(YamlMappingNode mapping, string key)
    {
        return Scalar(mapping, key).Value ?? string.Empty;
    }

    private static string ScalarValue(YamlNode node)
    {
        Assert.That(node, Is.TypeOf<YamlScalarNode>(), "Expected YAML sequence entry to be a scalar.");
        return ((YamlScalarNode) node).Value ?? string.Empty;
    }

    private static YamlNode Node(YamlMappingNode mapping, string key)
    {
        foreach (var child in mapping.Children)
        {
            if (child.Key is YamlScalarNode { Value: var childKey } &&
                childKey == key)
            {
                return child.Value;
            }
        }

        Assert.Fail($"Missing YAML key {key}.");
        throw new InvalidDataException($"Missing YAML key {key}.");
    }

    private static string FullPath(string relativePath)
    {
        return Path.Combine(
            Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..")),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
