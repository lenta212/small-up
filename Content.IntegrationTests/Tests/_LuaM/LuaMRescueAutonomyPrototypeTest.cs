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
        var gear = FindPrototype(
            LoadSequence("Resources/Prototypes/_LuaM/Loadouts/rescue.yml"),
            "LuaMRescueTeamGear");
        var equipment = Mapping(gear, "equipment");

        Assert.That(ScalarValue(equipment, "outerClothing"), Is.EqualTo("ClothingOuterArmorBasicSlim"));
        Assert.That(ScalarValue(equipment, "head"), Is.EqualTo("ClothingHeadHelmetBasic"));
        Assert.That(ScalarValue(equipment, "eyes"), Is.EqualTo("ClothingEyesHudMedical"));
        Assert.That(ScalarValue(equipment, "gloves"), Is.EqualTo("ClothingHandsGlovesCombat"));
        Assert.That(SequenceValues(Sequence(gear, "inhand")), Does.Contain("WeaponLaserCarbine"));
        Assert.That(SequenceValues(Sequence(Mapping(gear, "storage"), "back")), Does.Contain("MedkitCombatFilled"));
        Assert.That(SequenceValues(Sequence(Mapping(gear, "storage"), "back")), Does.Contain("DefibrillatorCompact"));

        var entities = LoadSequence("Resources/Prototypes/_LuaM/Entities/Mobs/rescue_agent.yml");
        AssertLoadout(entities, "LuaMRescueAgent");
        AssertLoadout(entities, "LuaMRescueEscort");
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
        Assert.That(source, Does.Contain("rescue.DeathSignalTarget = deathSignalTarget"));
        Assert.That(source, Does.Contain("deathSignal &&"));
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
        Assert.That(source, Does.Contain("TryTreatOrEvacuateTarget"));
        Assert.That(source, Does.Contain("ShouldEvacuateBeforeTreatment"));
        Assert.That(source, Does.Contain("IsThreatenedEvacuationTarget"));
        Assert.That(source, Does.Contain("HasRescueTeamThreatPressure"));
        Assert.That(source, Does.Contain("TryComp<LuaMRescueTeamComponent>(uid, out var team)"));
        Assert.That(source, Does.Contain("team.ThreatTarget is { Valid: true }"));
        Assert.That(source, Does.Contain("team.RecentThreatMemories > 0"));
        Assert.That(source, Does.Contain("rescue.ThreatEvacuationMinDamage"));
        Assert.That(source, Does.Contain("unsafe-scene evacuation"));
        Assert.That(source, Does.Contain("score += 750f"));
        Assert.That(source, Does.Contain("!unsafeSceneEvacuation"));
    }

    [Test]
    public void RescueAgentKeepsShuttleForwardForPendingEvacuationTargets()
    {
        var source = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueAgentSystem.cs"), Encoding.UTF8);

        Assert.That(source, Does.Contain("var hasPendingEvacuationTarget = HasPendingEvacuationTarget(uid, rescue, target);"));
        Assert.That(source, Does.Contain("allowAutoReturn: !hasPendingEvacuationTarget"));
        Assert.That(source, Does.Contain("holding shuttle forward after evacuation"));
        Assert.That(source, Does.Contain("holding shuttle forward after skipping"));
        Assert.That(source, Does.Contain("pending evacuation target detected"));
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
        Assert.That(source, Does.Contain("hold-position route blocked"));
        Assert.That(source, Does.Contain("route-blocked-hold:"));
        Assert.That(source, Does.Contain("route-blocked-reroute:"));
        Assert.That(source, Does.Contain("route-blocked-help:"));
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
        Assert.That(source, Does.Contain("onboardCare={rescue.LastOnboardCareStatus}"));
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
        Assert.That(source, Does.Contain("onboard-care: patient={FormatEntityRef(patient)}"));
        Assert.That(source, Does.Contain("critical; treatment continuing"));
        Assert.That(source, Does.Contain("dead recovery; defib cycle pending"));
        Assert.That(source, Does.Contain("release-ready; {status}"));
        Assert.That(source, Does.Contain("onboard-care released: patient={FormatEntityRef(patient)}"));
        Assert.That(source, Does.Contain("dead-recovery"));
        Assert.That(source, Does.Contain("unsafe-evacuation"));
        Assert.That(source, Does.Contain("onsite-treatment"));
        Assert.That(source, Does.Contain("hasPendingEvacuationTarget"));
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
        Assert.That(component, Does.Contain("LastPhaseAnnouncementStatus"));
        Assert.That(component, Does.Contain("NextPhaseAnnouncementAt"));
        Assert.That(source, Does.Contain("TeamPhaseAnnouncementCooldownSeconds"));
        Assert.That(source, Does.Contain("TrySayTeamPhaseLine"));
        Assert.That(source, Does.Contain("SetTeamPhaseAnnouncementStatus"));
        Assert.That(source, Does.Contain("TryBuildTeamPhaseLine"));
        Assert.That(source, Does.Contain("phase-bark:"));
        Assert.That(source, Does.Contain("phaseBark={team.LastPhaseAnnouncementStatus}"));
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
        Assert.That(source, Does.Contain("LuaMAiDroneTaskComponent"));
        Assert.That(source, Does.Contain("DroneControlComponent"));
        Assert.That(source, Does.Contain("SiliconComponent"));
        Assert.That(source, Does.Contain("BorgChassisComponent"));
        Assert.That(source, Does.Contain("_tag.HasTag(candidate, BotTag)"));
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
    public void RescueTeamClearsCompletedPatientForReturnOrExtract()
    {
        var source = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueTeamSystem.cs"), Encoding.UTF8);

        Assert.That(source, Does.Contain("GetActiveRescuePatient"));
        Assert.That(source, Does.Contain("var patient = GetActiveRescuePatient(rescue);"));
        Assert.That(source, Does.Contain("escort.Patient = GetActiveRescuePatient(rescue);"));
        Assert.That(source, Does.Not.Contain("?? team.Patient"));
        Assert.That(source, Does.Contain("LuaMRescueTeamPhase.ReturnOrExtract"));
        Assert.That(source, Does.Contain("plan return-to-shuttle: extraction phase"));
        Assert.That(source, Does.Contain("returning or extracting"));
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

        Assert.That(source, Does.Contain("TryRunThreatScreenAction"));
        Assert.That(source, Does.Contain("EscortThreatScreenRange = 7f"));
        Assert.That(source, Does.Contain("duty != LuaMRescueEscortDuty.ThreatScreen"));
        Assert.That(source, Does.Contain("NPCBlackboard.CurrentOrderedTarget"));
        Assert.That(source, Does.Contain("_combatMode.SetInCombatMode(uid, true, combat)"));
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
        Assert.That(source, Does.Contain("TryRunClearRouteAction(uid, escort, htn, followTarget)"));
        Assert.That(source, Does.Contain("TrySetClearRouteDropoffTarget"));
        Assert.That(source, Does.Contain("GetClearRouteDropoffDirection"));
        Assert.That(source, Does.Contain("blockerXform.Coordinates.Offset(direction * RouteBlockerDropoffDistance)"));
        Assert.That(source, Does.Contain("_npc.SetBlackboard(uid, NPCBlackboard.FollowTarget, dropoff, htn)"));
        Assert.That(source, Does.Contain("_npc.SetBlackboard(uid, \"FollowCloseRange\", 0.75f, htn)"));
        Assert.That(source, Does.Contain("_npc.SetBlackboard(uid, \"FollowRange\", 1.5f, htn)"));
        Assert.That(source, Does.Contain("clear-route dragging"));
        Assert.That(source, Does.Contain("Vector2.Normalize(away)"));
    }

    private static void AssertLoadout(YamlSequenceNode entities, string prototypeId)
    {
        var entity = FindPrototype(entities, prototypeId);
        var loadout = FindComponent(entity, "Loadout");

        Assert.That(SequenceValues(Sequence(loadout, "prototypes")), Is.EquivalentTo(new[] { "LuaMRescueTeamGear" }));
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
