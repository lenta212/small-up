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

        Assert.That(source, Does.Contain("SubscribeLocalEvent<MobStateChangedEvent>(OnMobStateChanged)"));
        Assert.That(source, Does.Contain("TryDispatchAutomaticDeathSignal(ev.Target)"));
        Assert.That(source, Does.Contain("HasComp<ActorComponent>(target)"));
        Assert.That(source, Does.Contain("AutomaticDeathSignalCooldownSeconds"));
        Assert.That(source, Does.Contain("TryResolveAutomaticDeathSignalStation"));
        Assert.That(source, Does.Contain("spawnTeam: true"));
        Assert.That(source, Does.Contain("DeathSignalFlag"));
        Assert.That(source, Does.Contain("--death-signal"));
        Assert.That(source, Does.Contain("requires target=<entity|player> so Aibolit can report who it is flying to"));
        Assert.That(source, Does.Contain("\u041c\u0435\u0434\u0441\u0438\u0433\u043d\u0430\u043b \u0441\u043c\u0435\u0440\u0442\u0438 \u043f\u0440\u0438\u043d\u044f\u0442. \u0412\u044b\u043b\u0435\u0442\u0430\u044e \u043a {targetName}."));
        Assert.That(source, Does.Contain("_radio.SendRadioMessage("));
        Assert.That(source, Does.Contain("MedicalRadioChannel"));
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
        Assert.That(source, Does.Contain("SubscribeLocalEvent<MobStateComponent, TargetDefibrillatedEvent>(OnTargetDefibrillated)"));
        Assert.That(source, Does.Contain("TrySendRescueStatusComms"));
        Assert.That(source, Does.Contain("_chat.TrySendInGameICMessage"));
        Assert.That(source, Does.Contain("_radio.SendRadioMessage"));
        Assert.That(source, Does.Contain("MedicalRadioChannel"));
        Assert.That(source, Does.Contain("autoComms={rescue.LastAutoCommsKey}"));
        Assert.That(source, Does.Contain("defib-start:"));
        Assert.That(source, Does.Contain("defib-success:"));
        Assert.That(source, Does.Contain("defib-failed:"));
        Assert.That(source, Does.Contain("patient-onboard-dead:"));
        Assert.That(source, Does.Contain("patient-hold-dead:"));
        Assert.That(source, Does.Contain("patient-release:"));
        Assert.That(source, Does.Contain("\u0414\u0435\u0444\u0438\u0431\u0440\u0438\u043b\u043b\u044f\u0446\u0438\u044f {Name(target)} \u043d\u0430\u0447\u0430\u0442\u0430. \u041d\u0435 \u0442\u0440\u043e\u0433\u0430\u0439\u0442\u0435 \u043f\u0430\u0446\u0438\u0435\u043d\u0442\u0430."));
        Assert.That(source, Does.Contain("\u041f\u0443\u043b\u044c\u0441 {targetName} \u0432\u043e\u0441\u0441\u0442\u0430\u043d\u043e\u0432\u043b\u0435\u043d. \u041f\u0440\u043e\u0434\u043e\u043b\u0436\u0430\u044e \u0441\u0442\u0430\u0431\u0438\u043b\u0438\u0437\u0430\u0446\u0438\u044e."));
        Assert.That(source, Does.Contain("\u041f\u0430\u0446\u0438\u0435\u043d\u0442 {Name(target)} \u043d\u0430 \u0431\u043e\u0440\u0442\u0443. \u041d\u0430\u0447\u0438\u043d\u0430\u044e \u0440\u0435\u0430\u043d\u0438\u043c\u0430\u0446\u0438\u043e\u043d\u043d\u044b\u0439 \u0446\u0438\u043a\u043b."));
        Assert.That(source, Does.Contain("\u041f\u0430\u0446\u0438\u0435\u043d\u0442 {Name(patient)} \u0441\u0442\u0430\u0431\u0438\u043b\u0435\u043d. \u041e\u0442\u043f\u0443\u0441\u043a\u0430\u044e \u0441 \u0431\u043e\u0440\u0442\u0430."));
    }

    [Test]
    public void RescueTeamThreatDetectionUsesObserverFaction()
    {
        var source = File.ReadAllText(FullPath("Content.Server/_LuaM/Rescue/LuaMRescueTeamSystem.cs"), Encoding.UTF8);

        Assert.That(source, Does.Contain("observerFaction.Factions.Any(faction => _factions.IsFactionHostile(faction, (candidate, candidateFaction)))"));
        Assert.That(source, Does.Not.Contain("IsFactionHostile(\"NanoTrasen\""));
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
