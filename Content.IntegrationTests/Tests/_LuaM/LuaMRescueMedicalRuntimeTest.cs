using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Pair;
using Content.Server.Atmos.Components;
using Content.Server.Body.Components;
using Content.Server.Medical.Components;
using Content.Server.Mind;
using Content.Server.Power.Components;
using Content.Server._LuaM.Rescue;
using Content.Shared.Atmos.Rotting;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.DoAfter;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Medical;
using Content.Shared.MedicalScanner;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Traits.Assorted;
using Content.Shared._Goobstation.DoAfter;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(LuaMRescueAgentSystem))]
public sealed class LuaMRescueMedicalRuntimeTest
{
    private const string TestAgent = "LuaMRescueMedicalRuntimeAgent";

    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  parent: MobHuman
  id: LuaMRescueMedicalRuntimeAgent
  components:
  - type: LuaMRescueAgent
  - type: Medibot
    treatments:
      Alive:
        reagent: Tricordrazine
        quantity: 15
        minDamage: 0
      Critical:
        reagent: Inaprovaline
        quantity: 20
  - type: DoAfter
    raiseEndedEvent: true
  - type: HTN
    rootTask:
      task: IdleCompound

- type: entity
  parent: BruteAutoInjector
  id: LuaMRescueDelayedBruteInjector
  components:
  - type: Hypospray
    solutionName: pen
    transferAmount: 20
    onlyAffectsMobs: false
    injectOnly: true
    doAfterTime: 0.05

- type: entity
  parent: BruteAutoInjector
  id: LuaMRescueLongDoAfterBruteInjector
  components:
  - type: Hypospray
    solutionName: pen
    transferAmount: 20
    onlyAffectsMobs: false
    injectOnly: true
    doAfterTime: 5

- type: entity
  parent: Defibrillator
  id: LuaMRescueWeakFastDefibrillator
  components:
  - type: Defibrillator
    zapHeal:
      types:
        Asphyxiation: -1
    doAfterDuration: 0.05
    zapDelay: 0.01

- type: vendingMachineInventory
  id: LuaMRescueMedicalRuntimeInventory
  startingInventory:
    Ointment: 2
    Brutepack: 2

- type: entity
  parent: VendingMachine
  id: LuaMRescueMedicalRuntimeVending
  components:
  - type: VendingMachine
    pack: LuaMRescueMedicalRuntimeInventory
    ejectDelay: 0.05
    requiresCash: false
  - type: ApcPowerReceiver
    needsPower: false
  - type: Sprite
    sprite: error.rsi
";

    [Test]
    public async Task NestedMedkitPlannerChoosesDamageSpecificItemAndDoesNotDuplicateDoAfter()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            AssertMatchingNestedTreatment(
                entities,
                map.MapId,
                new Vector2(0f, 0f),
                "Blunt",
                "Brutepack",
                "Ointment");
            AssertMatchingNestedTreatment(
                entities,
                map.MapId,
                new Vector2(5f, 0f),
                "Heat",
                "Ointment",
                "Brutepack");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task HeldMedicinePlannerChoosesHighestPatientBenefitInsteadOfFirstHand()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var rescueSystem = entities.System<LuaMRescueAgentSystem>();
        var handsSystem = entities.System<SharedHandsSystem>();
        var damageable = entities.System<DamageableSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid patient = default;
        EntityUid weakMatch = default;
        EntityUid strongMatch = default;
        await server.WaitAssertion(() =>
        {
            agent = SpawnAgent(entities, map.MapId, Vector2.Zero);
            patient = SpawnDamagedPatient(
                entities,
                map.MapId,
                new Vector2(0.5f, 0f),
                "Blunt",
                40);
            var secondaryDamage = new DamageSpecifier();
            secondaryDamage.DamageDict.Add("Heat", 5);
            Assert.That(
                damageable.TryChangeDamage(patient, secondaryDamage, ignoreResistances: true),
                Is.Not.Null);

            weakMatch = entities.SpawnEntity("Ointment", Coordinates(map.MapId, Vector2.Zero));
            strongMatch = entities.SpawnEntity("Brutepack", Coordinates(map.MapId, Vector2.Zero));
            var hands = entities.GetComponent<HandsComponent>(agent);
            var orderedHands = hands.Hands.Values.ToArray();
            Assert.That(orderedHands, Has.Length.GreaterThanOrEqualTo(2));
            Assert.That(handsSystem.TryPickup(agent, weakMatch, orderedHands[0], handsComp: hands), Is.True);
            Assert.That(handsSystem.TryPickup(agent, strongMatch, orderedHands[1], handsComp: hands), Is.True);

            Assert.That(
                rescueSystem.TryOrderPlayerAction(
                    agent,
                    LuaMRescuePlayerActionKind.Treat,
                    patient,
                    out var orderStatus),
                Is.True,
                orderStatus);
        });

        await pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.PendingMedicalDoAfterTarget, Is.EqualTo(patient));
                Assert.That(rescue.PendingMedicalDoAfterItem, Is.EqualTo(strongMatch),
                    "Planner must rank every held capability instead of accepting the first merely useful hand.");
                Assert.That(rescue.PendingMedicalDoAfterItem, Is.Not.EqualTo(weakMatch));
                Assert.That(CountActiveMedicalDoAfters(entities, agent, patient), Is.EqualTo(1));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AutomaticResupplyVendsAndRetrievesPatientSpecificMedicine()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var rescueSystem = entities.System<LuaMRescueAgentSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var mapSystem = server.System<SharedMapSystem>();
        var transform = entities.System<SharedTransformSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid patient = default;
        EntityUid vending = default;
        await server.WaitAssertion(() =>
        {
            for (var x = 0; x <= 2; x++)
                mapSystem.SetTile(map.Grid, new Vector2i(x, 0), map.Tile.Tile);

            agent = SpawnAgent(entities, map.MapId, Vector2.Zero);
            transform.SetCoordinates(agent, new EntityCoordinates(map.Grid.Owner, new Vector2(0.5f, 0.5f)));
            patient = SpawnDamagedPatient(
                entities,
                map.MapId,
                new Vector2(0.5f, 0f),
                "Blunt",
                30);
            transform.SetCoordinates(patient, new EntityCoordinates(map.Grid.Owner, new Vector2(1f, 0.5f)));
            vending = entities.SpawnEntity(
                "LuaMRescueMedicalRuntimeVending",
                new EntityCoordinates(map.Grid.Owner, new Vector2(1.5f, 0.5f)));
            entities.GetComponent<ApcPowerReceiverComponent>(vending).Powered = true;

            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoAcquireTargets = true;
            rescue.TargetRefreshInterval = 0.001f;
            rescue.AutoTreatWithCarriedItems = true;
            rescue.AutoResupplyFromVending = true;
            rescue.AutoResupplyRange = 4f;
            rescue.AutoPickupSupplyRange = 4f;
            rescue.PlayerActionTimeout = 5f;
            rescue.AssignedTarget = patient;
            rescue.TaskPatientTarget = patient;
            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    agent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.TreatPatient,
                    patient,
                    new EntityCoordinates(patient, Vector2.Zero),
                    out _),
                Is.True,
                "The autonomous resupply path requires an active treatment intent.");
            var depleted = rescueSystem.GetMedicalSupplySnapshot(agent, patient);
            Assert.Multiple(() =>
            {
                Assert.That(depleted.EffectiveMedicalUnits, Is.EqualTo(0));
                Assert.That(depleted.Status, Does.Contain("depleted"));
            });
        });

        EntityUid selectedMedicine = default;
        var verifiedTreatmentStarted = false;
        for (var tick = 0; tick < 120; tick++)
        {
            await pair.RunTicksSync(1);
            await server.WaitAssertion(() =>
            {
                var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
                if (rescue.PendingMedicalDoAfterTarget != patient ||
                    rescue.PendingMedicalDoAfterItem is not { Valid: true } item)
                {
                    return;
                }

                selectedMedicine = item;
                verifiedTreatmentStarted = true;
            });
            if (verifiedTreatmentStarted)
                break;
        }

        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            var supplies = rescueSystem.GetMedicalSupplySnapshot(agent, patient);
            Assert.Multiple(() =>
            {
                Assert.That(verifiedTreatmentStarted, Is.True,
                    $"Resupply never produced executable treatment; supply={rescue.LastAutoSupplyStatus}; " +
                    $"action={rescue.LastPlayerActionStatus}; treatment={rescue.LastAutoTreatmentStatus}");
                Assert.That(entities.GetComponent<MetaDataComponent>(selectedMedicine).EntityPrototype?.ID,
                    Is.EqualTo("Brutepack"));
                Assert.That(IsHeld(entities, agent, selectedMedicine), Is.True,
                    "Resupply is complete only after the selected product is physically retrieved.");
                Assert.That(rescueSystem.IsEffectiveTreatmentItem(selectedMedicine, patient, out var reason), Is.True,
                    reason);
                Assert.That(rescue.PendingVendingProduct, Is.Null);
                Assert.That(rescue.PendingVendingDispensedItem, Is.Null);
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(patient));
                Assert.That(supplies.MedicalUnits, Is.GreaterThan(0));
                Assert.That(supplies.EffectiveMedicalUnits, Is.GreaterThan(0));
                Assert.That(supplies.Status, Does.StartWith("ready"));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task EquippedMedicalBeltPlannerTreatsOnlyAssignedTarget()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var rescueSystem = entities.System<LuaMRescueAgentSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid assignedPatient = default;
        EntityUid unrelatedPatient = default;
        EntityUid belt = default;
        EntityUid medicine = default;
        await server.WaitAssertion(() =>
        {
            agent = SpawnAgent(entities, map.MapId, Vector2.Zero);
            assignedPatient = SpawnDamagedPatient(
                entities,
                map.MapId,
                new Vector2(0.5f, 0f),
                "Blunt",
                20);
            unrelatedPatient = SpawnDamagedPatient(
                entities,
                map.MapId,
                new Vector2(0.6f, 0.2f),
                "Blunt",
                40);
            belt = entities.SpawnEntity(
                "ClothingBeltMedical",
                Coordinates(map.MapId, Vector2.Zero));
            medicine = entities.SpawnEntity(
                "Brutepack",
                Coordinates(map.MapId, Vector2.Zero));

            Assert.That(
                entities.System<InventorySystem>().TryEquip(agent, belt, "belt", silent: true),
                Is.True);
            InsertIntoStorage(entities, belt, medicine, agent);
            Assert.That(entities.GetComponent<StorageComponent>(belt).Container.Contains(medicine), Is.True);

            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            rescue.AutoTreatWithCarriedItems = true;
            rescue.EvacuateTargetsToShuttle = false;

            Assert.That(
                rescueSystem.TryOrderPlayerAction(
                    agent,
                    LuaMRescuePlayerActionKind.Treat,
                    assignedPatient,
                    out var status),
                Is.True,
                status);
        });

        await pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.That(coordinator.GetSnapshot(agent, out var activity), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.AssignedTarget, Is.EqualTo(assignedPatient));
                Assert.That(rescue.PendingMedicalDoAfterTarget, Is.EqualTo(assignedPatient));
                Assert.That(rescue.PendingMedicalDoAfterItem, Is.EqualTo(medicine));
                Assert.That(rescue.PendingMedicalDoAfterKind, Is.EqualTo("healing"));
                Assert.That(activity.Target, Is.EqualTo(assignedPatient));
                Assert.That(activity.DoAfterStatus, Is.EqualTo(LuaMRescueDoAfterStatus.Running));
                Assert.That(CountActiveMedicalDoAfters(entities, agent), Is.EqualTo(1));
                Assert.That(CountActiveMedicalDoAfters(entities, agent, assignedPatient), Is.EqualTo(1));
                Assert.That(CountActiveMedicalDoAfters(entities, agent, unrelatedPatient), Is.Zero);
                Assert.That(IsHeld(entities, agent, medicine), Is.True);
                Assert.That(entities.GetComponent<StorageComponent>(belt).Container.Contains(medicine), Is.False);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task StockRescueAgentCarriesAnalyzerAndMissingAnalyzerNeverClaimsTriage()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var rescueSystem = entities.System<LuaMRescueAgentSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var inventory = entities.System<InventorySystem>();
        var mapSystem = server.System<SharedMapSystem>();
        var map = await pair.CreateTestMap();

        EntityUid stockedAgent = default;
        EntityUid stockedPatient = default;
        EntityUid stockedAnalyzer = default;
        EntityUid missingAgent = default;
        EntityUid missingPatient = default;
        await server.WaitAssertion(() =>
        {
            for (var x = 0; x <= 8; x++)
                mapSystem.SetTile(map.Grid, new Vector2i(x, 0), map.Tile.Tile);

            stockedAgent = entities.SpawnEntity(
                "LuaMRescueAgent",
                new EntityCoordinates(map.Grid.Owner, new Vector2(0.5f, 0.5f)));
            stockedPatient = entities.SpawnEntity(
                "MobHuman",
                new EntityCoordinates(map.Grid.Owner, new Vector2(1.5f, 0.5f)));
            entities.RemoveComponent<BarotraumaComponent>(stockedPatient);
            var stockedDamage = new DamageSpecifier();
            stockedDamage.DamageDict.Add("Blunt", 20);
            Assert.That(
                entities.System<DamageableSystem>()
                    .TryChangeDamage(stockedPatient, stockedDamage, ignoreResistances: true),
                Is.Not.Null);
            var stockedRescue = entities.GetComponent<LuaMRescueAgentComponent>(stockedAgent);
            stockedRescue.AutoAcquireTargets = false;
            stockedRescue.AssignedShuttle = null;
            stockedRescue.EvacuateTargetsToShuttle = false;

            Assert.That(inventory.TryGetSlotEntity(stockedAgent, "back", out var stockedBackpack), Is.True);
            stockedAnalyzer = entities.GetComponent<StorageComponent>(stockedBackpack!.Value)
                .Container.ContainedEntities
                .Single(item => entities.HasComponent<HealthAnalyzerComponent>(item));

            missingAgent = entities.SpawnEntity(
                "LuaMRescueAgent",
                new EntityCoordinates(map.Grid.Owner, new Vector2(5.5f, 0.5f)));
            missingPatient = entities.SpawnEntity(
                "MobHuman",
                new EntityCoordinates(map.Grid.Owner, new Vector2(6.5f, 0.5f)));
            entities.RemoveComponent<BarotraumaComponent>(missingPatient);
            var missingDamage = new DamageSpecifier();
            missingDamage.DamageDict.Add("Blunt", 20);
            Assert.That(
                entities.System<DamageableSystem>()
                    .TryChangeDamage(missingPatient, missingDamage, ignoreResistances: true),
                Is.Not.Null);
            var missingRescue = entities.GetComponent<LuaMRescueAgentComponent>(missingAgent);
            missingRescue.AutoAcquireTargets = false;
            missingRescue.AssignedShuttle = null;
            missingRescue.EvacuateTargetsToShuttle = false;

            Assert.That(inventory.TryGetSlotEntity(missingAgent, "back", out var missingBackpack), Is.True);
            var missingAnalyzer = entities.GetComponent<StorageComponent>(missingBackpack!.Value)
                .Container.ContainedEntities
                .Single(item => entities.HasComponent<HealthAnalyzerComponent>(item));
            entities.DeleteEntity(missingAnalyzer);

            Assert.Multiple(() =>
            {
                Assert.That(stockedRescue.AutoAnalyzeBeforeTreatment, Is.True);
                Assert.That(missingRescue.AutoAnalyzeBeforeTreatment, Is.True);
            });

            Assert.That(
                rescueSystem.TryOrderAgent(stockedAgent, stockedPatient, out var stockedStatus),
                Is.True,
                stockedStatus);
            Assert.That(
                rescueSystem.TryOrderAgent(missingAgent, missingPatient, out var missingStatus),
                Is.True,
                missingStatus);
            stockedRescue.AutoAcquireTargets = true;
            missingRescue.AutoAcquireTargets = true;
            stockedRescue.TargetRefreshAccumulator = stockedRescue.TargetRefreshInterval;
            missingRescue.TargetRefreshAccumulator = missingRescue.TargetRefreshInterval;
        });

        var observedExpectedExecutors = false;
        for (var i = 0; i < 120 && !observedExpectedExecutors; i++)
        {
            await pair.RunTicksSync(1);
            await server.WaitAssertion(() =>
            {
                var stockedRescue = entities.GetComponent<LuaMRescueAgentComponent>(stockedAgent);
                var missingRescue = entities.GetComponent<LuaMRescueAgentComponent>(missingAgent);
                Assert.That(coordinator.GetSnapshot(stockedAgent, out var stockedActivity), Is.True);
                Assert.That(coordinator.GetSnapshot(missingAgent, out var missingActivity), Is.True);
                observedExpectedExecutors =
                    stockedRescue.PendingMedicalDoAfterTarget == stockedPatient &&
                    stockedRescue.PendingMedicalDoAfterItem == stockedAnalyzer &&
                    stockedRescue.PendingMedicalDoAfterKind == "analyzer" &&
                    stockedActivity.Activity == LuaMRescueActivity.Triage &&
                    stockedActivity.DoAfterStatus == LuaMRescueDoAfterStatus.Running &&
                    missingRescue.LastAutoAnalyzeStatus == "no health analyzer found in hands or storage" &&
                    missingRescue.PendingMedicalDoAfterTarget == missingPatient &&
                    missingRescue.PendingMedicalDoAfterKind != "analyzer" &&
                    missingActivity.Activity == LuaMRescueActivity.TreatPatient &&
                    missingActivity.DoAfterStatus == LuaMRescueDoAfterStatus.Running;
            });

            if (!observedExpectedExecutors)
                await Task.Delay(5);
        }

        await server.WaitAssertion(() =>
        {
            var stockedRescue = entities.GetComponent<LuaMRescueAgentComponent>(stockedAgent);
            var missingRescue = entities.GetComponent<LuaMRescueAgentComponent>(missingAgent);
            Assert.That(coordinator.GetSnapshot(stockedAgent, out var stockedActivity), Is.True);
            Assert.That(coordinator.GetSnapshot(missingAgent, out var missingActivity), Is.True);
            var diagnostics =
                $"stocked={stockedActivity.Activity}/{stockedActivity.DoAfterStatus}/" +
                $"{stockedRescue.PendingMedicalDoAfterKind}/{stockedRescue.LastAutoAnalyzeStatus}; " +
                $"missing={missingActivity.Activity}/{missingActivity.DoAfterStatus}/" +
                $"{missingRescue.PendingMedicalDoAfterKind}/{missingRescue.LastAutoAnalyzeStatus}";
            Assert.That(observedExpectedExecutors, Is.True, diagnostics);
            Assert.Multiple(() =>
            {
                Assert.That(IsHeld(entities, stockedAgent, stockedAnalyzer), Is.True, diagnostics);
                Assert.That(stockedRescue.PendingMedicalDoAfterTarget, Is.EqualTo(stockedPatient), diagnostics);
                Assert.That(missingRescue.PendingMedicalDoAfterTarget, Is.EqualTo(missingPatient), diagnostics);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RunningMedicalDoAfterNeverConsumesRouteStallBudget()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var rescueSystem = entities.System<LuaMRescueAgentSystem>();
        var hands = entities.System<SharedHandsSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid patient = default;
        await server.WaitAssertion(() =>
        {
            agent = SpawnAgent(entities, map.MapId, Vector2.Zero);
            patient = SpawnDamagedPatient(
                entities,
                map.MapId,
                new Vector2(0.5f, 0f),
                "Blunt",
                20);
            var injector = entities.SpawnEntity(
                "LuaMRescueLongDoAfterBruteInjector",
                Coordinates(map.MapId, Vector2.Zero));
            Assert.That(hands.TryPickupAnyHand(agent, injector), Is.True);
            var shuttle = entities.SpawnEntity(
                null,
                Coordinates(map.MapId, new Vector2(20f, 0f)));

            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            ConfigureForEvacuation(rescue, shuttle);
            rescue.TargetRefreshInterval = 0.01f;
            rescue.TargetStallSeconds = 0.05f;
            rescue.AutoAcquireTargets = true;

            Assert.That(rescueSystem.TryOrderAgent(agent, patient, out var status), Is.True, status);
        });

        await pair.RunTicksSync(12);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.That(coordinator.GetSnapshot(agent, out var activity), Is.True);
            var diagnostics =
                $"activity={activity.Activity}/{activity.TerminalStatus}/gen={activity.Generation}; " +
                $"assigned={rescue.AssignedTarget}; task={rescue.TaskPatientTarget}/{rescue.TaskStage}; " +
                $"pending={rescue.PendingMedicalDoAfterTarget}/{rescue.PendingMedicalDoAfterKind}/gen={rescue.PendingMedicalIntentGeneration}; " +
                $"tracking={rescue.LastTargetTrackingStatus}; treatment={rescue.LastAutoTreatmentStatus}";
            Assert.Multiple(() =>
            {
                Assert.That(CountActiveMedicalDoAfters(entities, agent, patient), Is.EqualTo(1));
                Assert.That(rescue.PendingMedicalDoAfterTarget, Is.EqualTo(patient));
                Assert.That(rescue.TargetStallAccumulator, Is.Zero,
                    "Every Update during an authoritative medical DoAfter must reset route stall.");
                Assert.That(rescue.SkippedTargets, Does.Not.ContainKey(patient));
                Assert.That(rescue.TerminalTreatmentFailures, Does.Not.ContainKey(patient));
                Assert.That(rescue.AssignedTarget, Is.EqualTo(patient), diagnostics);
                Assert.That(activity.Target, Is.EqualTo(patient));
                Assert.That(activity.DoAfterStatus, Is.EqualTo(LuaMRescueDoAfterStatus.Running));
                Assert.That(activity.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task EmptyMedipenRemainsUnselectedAcrossRepeatedTreatmentOrders()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var rescueSystem = entities.System<LuaMRescueAgentSystem>();
        var solutions = entities.System<SharedSolutionContainerSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid patient = default;
        EntityUid medkit = default;
        EntityUid emptyMedipen = default;
        await server.WaitAssertion(() =>
        {
            agent = SpawnAgent(entities, map.MapId, Vector2.Zero);
            patient = SpawnDamagedPatient(entities, map.MapId, new Vector2(0.5f, 0f), "Blunt", 20);
            medkit = EquipNestedMedkit(entities, agent, map.MapId, Vector2.Zero);
            emptyMedipen = entities.SpawnEntity("EmergencyMedipen", Coordinates(map.MapId, Vector2.Zero));
            EmptyMedipen(entities, solutions, emptyMedipen);
            InsertIntoStorage(entities, medkit, emptyMedipen, agent);

            Assert.That(
                rescueSystem.IsEffectiveTreatmentItem(emptyMedipen, patient, out var rejection),
                Is.False);
            Assert.That(rejection, Is.EqualTo("ItemEmpty"));
            Assert.That(
                rescueSystem.TryOrderPlayerAction(
                    agent,
                    LuaMRescuePlayerActionKind.Treat,
                    patient,
                    out var status),
                Is.True,
                status);
        });

        await pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
            AssertEmptyMedipenWasNotSelected(entities, agent, patient, medkit, emptyMedipen));

        await server.WaitAssertion(() =>
        {
            Assert.That(
                rescueSystem.TryOrderPlayerAction(
                    agent,
                    LuaMRescuePlayerActionKind.Treat,
                    patient,
                    out var status),
                Is.True,
                status);
        });

        await pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
            AssertEmptyMedipenWasNotSelected(entities, agent, patient, medkit, emptyMedipen));

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DermalineAtAdverseThresholdIsRejectedAsOverdoseRisk()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var rescueSystem = entities.System<LuaMRescueAgentSystem>();
        var solutions = entities.System<SharedSolutionContainerSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var patient = SpawnDamagedPatient(
                entities,
                map.MapId,
                Vector2.Zero,
                "Heat",
                20);
            var syringe = entities.SpawnEntity(
                "SyringeDermaline",
                Coordinates(map.MapId, new Vector2(0.5f, 0f)));

            Assert.That(
                rescueSystem.IsEffectiveTreatmentItem(syringe, patient, out var safeReason),
                Is.True,
                safeReason);

            var bloodstream = entities.GetComponent<BloodstreamComponent>(patient);
            Assert.That(
                solutions.TryGetSolution(
                    patient,
                    bloodstream.ChemicalSolutionName,
                    out var bloodstreamSolution,
                    out _),
                Is.True);
            Assert.That(
                solutions.TryAddReagent(
                    bloodstreamSolution!.Value,
                    "Dermaline",
                    FixedPoint2.New(5),
                    out _),
                Is.True);

            Assert.That(
                rescueSystem.IsEffectiveTreatmentItem(syringe, patient, out var rejection),
                Is.False);
            Assert.That(rejection, Is.EqualTo("OverdoseRisk"));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task StockedSafeMixedMedipensRemainPlannerCompatible()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var rescueSystem = entities.System<LuaMRescueAgentSystem>();
        var mobState = entities.System<MobStateSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var brutePatient = SpawnDamagedPatient(
                entities,
                map.MapId,
                Vector2.Zero,
                "Blunt",
                30);
            var generalPatient = SpawnDamagedPatient(
                entities,
                map.MapId,
                new Vector2(2f, 0f),
                "Poison",
                30);
            var criticalPatient = SpawnDamagedPatient(
                entities,
                map.MapId,
                new Vector2(4f, 0f),
                "Asphyxiation",
                80);
            mobState.ChangeMobState(criticalPatient, MobState.Critical);

            var brutePen = entities.SpawnEntity("BruteAutoInjector", Coordinates(map.MapId, Vector2.Zero));
            var combatPen = entities.SpawnEntity("CombatMedipen", Coordinates(map.MapId, new Vector2(2f, 0f)));
            var emergencyPen = entities.SpawnEntity("EmergencyMedipen", Coordinates(map.MapId, new Vector2(4f, 0f)));

            Assert.Multiple(() =>
            {
                Assert.That(
                    rescueSystem.IsEffectiveTreatmentItem(brutePen, brutePatient, out var bruteReason),
                    Is.True,
                    bruteReason);
                Assert.That(
                    rescueSystem.IsEffectiveTreatmentItem(combatPen, generalPatient, out var combatReason),
                    Is.True,
                    combatReason);
                Assert.That(
                    rescueSystem.IsEffectiveTreatmentItem(emergencyPen, criticalPatient, out var emergencyReason),
                    Is.True,
                    emergencyReason);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task InjectionTransferWaitsForObservedEffectAndTimesOutBoundedly()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var rescueSystem = entities.System<LuaMRescueAgentSystem>();
        var hands = entities.System<SharedHandsSystem>();
        var solutions = entities.System<SharedSolutionContainerSystem>();
        var damageableSystem = entities.System<DamageableSystem>();
        var map = await pair.CreateTestMap();

        EntityUid instantAgent = default;
        EntityUid instantPatient = default;
        EntityUid instantPen = default;
        float instantStartingDamage = 0f;
        float instantStartingBlunt = 0f;
        float instantStartingHeat = 0f;
        await server.WaitAssertion(() =>
        {
            instantAgent = SpawnAgent(entities, map.MapId, Vector2.Zero);
            instantPatient = SpawnDamagedPatient(
                entities,
                map.MapId,
                new Vector2(0.5f, 0f),
                "Blunt",
                20);
            var unrelatedDamage = new DamageSpecifier();
            unrelatedDamage.DamageDict.Add("Heat", 10);
            Assert.That(
                damageableSystem.TryChangeDamage(instantPatient, unrelatedDamage, ignoreResistances: true),
                Is.Not.Null);
            instantPen = entities.SpawnEntity(
                "BruteAutoInjector",
                Coordinates(map.MapId, Vector2.Zero));
            Assert.That(hands.TryPickupAnyHand(instantAgent, instantPen), Is.True);
            instantStartingDamage = entities.GetComponent<DamageableComponent>(instantPatient)
                .TotalDamage.Float();
            instantStartingBlunt = entities.GetComponent<DamageableComponent>(instantPatient)
                .Damage.DamageDict["Blunt"].Float();
            instantStartingHeat = entities.GetComponent<DamageableComponent>(instantPatient)
                .Damage.DamageDict["Heat"].Float();

            Assert.That(
                rescueSystem.TryOrderPlayerAction(
                    instantAgent,
                    LuaMRescuePlayerActionKind.Treat,
                    instantPatient,
                    out var orderStatus),
                Is.True,
                orderStatus);
        });

        await pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(instantAgent);
            Assert.That(solutions.TryGetSolution(instantPen, "pen", out _, out var solution), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(
                    solution.Volume,
                    Is.EqualTo(FixedPoint2.Zero),
                    "The authoritative medipen transfer must have happened.");
                Assert.That(
                    entities.GetComponent<DamageableComponent>(instantPatient).TotalDamage.Float(),
                    Is.EqualTo(instantStartingDamage),
                    "Consumption alone must not be reported as healing before metabolism runs.");
                Assert.That(rescue.PendingMedicalEffectVerification, Is.True);
                Assert.That(rescue.PendingMedicalDoAfterTarget, Is.EqualTo(instantPatient));
                Assert.That(rescue.PendingPlayerAction, Is.EqualTo(LuaMRescuePlayerActionKind.Treat));
                Assert.That(rescue.TreatmentAttempts.GetValueOrDefault(instantPatient), Is.Zero);
                Assert.That(rescue.LastAutoTreatmentStatus, Does.Contain("awaiting observed medical effect"));
            });

            var unrelatedHealing = new DamageSpecifier();
            unrelatedHealing.DamageDict.Add("Heat", -5);
            Assert.That(
                damageableSystem.TryChangeDamage(instantPatient, unrelatedHealing, ignoreResistances: true),
                Is.Not.Null);
        });

        await pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(instantAgent);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.PendingMedicalEffectVerification, Is.True,
                    "Healing a damage type Bicaridine cannot affect must not complete its verification.");
                Assert.That(rescue.TreatmentAttempts.GetValueOrDefault(instantPatient), Is.Zero);
                Assert.That(
                    entities.GetComponent<DamageableComponent>(instantPatient).Damage.DamageDict["Blunt"].Float(),
                    Is.EqualTo(instantStartingBlunt));
                Assert.That(
                    entities.GetComponent<DamageableComponent>(instantPatient).Damage.DamageDict["Heat"].Float(),
                    Is.LessThan(instantStartingHeat));
            });
        });

        await pair.RunTicksSync(90);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(instantAgent);
            Assert.Multiple(() =>
            {
                Assert.That(
                    entities.GetComponent<DamageableComponent>(instantPatient)
                        .Damage.DamageDict["Blunt"].Float(),
                    Is.LessThan(instantStartingBlunt),
                    "The injected Bicaridine must reduce its concrete treatable damage type even if unrelated environmental damage rises.");
                Assert.That(rescue.PendingMedicalEffectVerification, Is.False);
                Assert.That(rescue.PendingMedicalDoAfterTarget, Is.Null);
                Assert.That(rescue.PendingPlayerAction, Is.EqualTo(LuaMRescuePlayerActionKind.None));
                Assert.That(rescue.LastPlayerActionStatus, Does.Contain("effect confirmed"));
                Assert.That(rescue.TreatmentAttempts, Does.Not.ContainKey(instantPatient));
            });
        });

        EntityUid delayedAgent = default;
        EntityUid delayedPatient = default;
        await server.WaitAssertion(() =>
        {
            delayedAgent = SpawnAgent(entities, map.MapId, new Vector2(5f, 0f));
            delayedPatient = SpawnDamagedPatient(
                entities,
                map.MapId,
                new Vector2(5.5f, 0f),
                "Blunt",
                20);
            var delayedPen = entities.SpawnEntity(
                "LuaMRescueDelayedBruteInjector",
                Coordinates(map.MapId, new Vector2(5f, 0f)));
            Assert.That(hands.TryPickupAnyHand(delayedAgent, delayedPen), Is.True);

            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(delayedAgent);
            rescue.ActivityRoleProfile.MaxAttempts = 1;
            Assert.That(
                rescueSystem.TryOrderPlayerAction(
                    delayedAgent,
                    LuaMRescuePlayerActionKind.Treat,
                    delayedPatient,
                    out var orderStatus),
                Is.True,
                orderStatus);
        });

        await pair.RunTicksSync(10);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(delayedAgent);
            Assert.Multiple(() =>
            {
                Assert.That(CountActiveMedicalDoAfters(entities, delayedAgent, delayedPatient), Is.Zero);
                Assert.That(rescue.PendingMedicalEffectVerification, Is.True,
                    "Completing injection DoAfter must enter effect verification, not report success.");
                Assert.That(rescue.PendingPlayerAction, Is.EqualTo(LuaMRescuePlayerActionKind.Treat));
            });
            rescue.MedicalEffectVerificationTimeout = 0.05f;
        });

        await pair.RunTicksSync(4);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(delayedAgent);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.PendingMedicalEffectVerification, Is.False);
                Assert.That(rescue.PendingMedicalDoAfterTarget, Is.Null);
                Assert.That(rescue.PendingPlayerAction, Is.EqualTo(LuaMRescuePlayerActionKind.None));
                Assert.That(rescue.TreatmentAttempts.GetValueOrDefault(delayedPatient), Is.EqualTo(1));
                Assert.That(rescue.TerminalTreatmentFailures, Does.ContainKey(delayedPatient));
                Assert.That(
                    rescue.TerminalTreatmentFailures[delayedPatient],
                    Does.Contain(nameof(LuaMRescueFailureReason.NoEffectiveMedicine)));
                Assert.That(rescue.SkippedTargets, Does.ContainKey(delayedPatient));
                Assert.That(
                    rescue.SkippedTargets[delayedPatient],
                    Is.GreaterThan(server.Timing.CurTime),
                    "A real terminal treatment failure must schedule a future retry.");
                Assert.That(
                    rescue.SkippedTargets[delayedPatient],
                    Is.LessThan(TimeSpan.MaxValue),
                    "Equipment exhaustion must not blacklist the patient forever.");
                Assert.That(rescue.LastPlayerActionStatus, Does.Contain("NoEffectiveMedicine"));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task StaleMedicalCompletionCannotFailOrParkReplacementIntent()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var rescueSystem = entities.System<LuaMRescueAgentSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var hands = entities.System<SharedHandsSystem>();
        var solutions = entities.System<SharedSolutionContainerSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid stalePatient = default;
        EntityUid replacementPatient = default;
        EntityUid staleInjector = default;
        uint staleGeneration = 0;
        uint replacementGeneration = 0;
        await server.WaitAssertion(() =>
        {
            agent = SpawnAgent(entities, map.MapId, Vector2.Zero);
            stalePatient = SpawnDamagedPatient(
                entities,
                map.MapId,
                new Vector2(0.5f, 0f),
                "Blunt",
                20);
            replacementPatient = SpawnDamagedPatient(
                entities,
                map.MapId,
                new Vector2(0.7f, 0f),
                "Heat",
                20);
            staleInjector = entities.SpawnEntity(
                "LuaMRescueLongDoAfterBruteInjector",
                Coordinates(map.MapId, Vector2.Zero));
            Assert.That(hands.TryPickupAnyHand(agent, staleInjector), Is.True);
            Assert.That(
                rescueSystem.TryOrderPlayerAction(
                    agent,
                    LuaMRescuePlayerActionKind.Treat,
                    stalePatient,
                    out var orderStatus),
                Is.True,
                orderStatus);
        });

        await pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.That(coordinator.GetSnapshot(agent, out var staleIntent), Is.True);
            staleGeneration = staleIntent.Generation;
            Assert.Multiple(() =>
            {
                Assert.That(staleIntent.Target, Is.EqualTo(stalePatient));
                Assert.That(staleIntent.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                Assert.That(rescue.PendingMedicalDoAfterTarget, Is.EqualTo(stalePatient));
                Assert.That(rescue.PendingMedicalIntentGeneration, Is.EqualTo(staleGeneration));
                Assert.That(CountActiveMedicalDoAfters(entities, agent, stalePatient), Is.EqualTo(1));
            });
            Assert.That(
                coordinator.Cancel(
                    agent,
                    staleGeneration,
                    LuaMRescueFailureReason.Cancelled,
                    out var cancelled),
                Is.True);
            Assert.That(cancelled.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Cancelled));
            Assert.That(
                coordinator.BeginOrReplaceIntent(
                    agent,
                    LuaMRescueRole.Aibolit,
                    LuaMRescueActivity.TreatPatient,
                    replacementPatient,
                    new EntityCoordinates(replacementPatient, Vector2.Zero),
                    out var replacementIntent),
                Is.True);
            replacementGeneration = replacementIntent.Generation;
            rescue.AssignedTarget = replacementPatient;
            rescue.TaskPatientTarget = replacementPatient;
            Assert.That(replacementGeneration, Is.Not.EqualTo(staleGeneration));
        });

        await pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.That(coordinator.GetSnapshot(agent, out var current), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(current.Generation, Is.EqualTo(replacementGeneration));
                Assert.That(current.Target, Is.EqualTo(replacementPatient));
                Assert.That(current.Activity, Is.EqualTo(LuaMRescueActivity.TreatPatient));
                Assert.That(current.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                Assert.That(rescue.AssignedTarget, Is.EqualTo(replacementPatient));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(replacementPatient));
                Assert.That(rescue.PendingMedicalDoAfterTarget, Is.Null);
                Assert.That(rescue.PendingMedicalEffectVerification, Is.False);
                Assert.That(rescue.PendingPlayerAction, Is.EqualTo(LuaMRescuePlayerActionKind.None));
                Assert.That(rescue.LastPlayerActionStatus, Does.Contain("stale medical intent"));
                Assert.That(CountActiveMedicalDoAfters(entities, agent, stalePatient), Is.Zero,
                    "Replacing the intent must cancel the stale physical treatment, not merely forget its tracker.");
            });
            Assert.That(solutions.TryGetSolution(staleInjector, "pen", out _, out var staleSolution), Is.True);
            Assert.That(staleSolution.Volume, Is.GreaterThan(FixedPoint2.Zero),
                "The cancelled stale injector must not apply to its former patient.");

            // Exercise the event callback independently: a delayed DoAfterEnded
            // from the stale generation must likewise be cleanup-only.
            rescue.PendingMedicalDoAfterTarget = stalePatient;
            rescue.PendingMedicalDoAfterKind = "injector";
            rescue.PendingMedicalIntentGeneration = staleGeneration;
            rescue.PendingPlayerAction = LuaMRescuePlayerActionKind.Treat;
            rescue.PendingPlayerActionTarget = stalePatient;
            var ended = new DoAfterEndedEvent(stalePatient, Cancelled: true);
            entities.EventBus.RaiseLocalEvent(agent, ref ended);

            Assert.That(coordinator.GetSnapshot(agent, out current), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(current.Generation, Is.EqualTo(replacementGeneration));
                Assert.That(current.Target, Is.EqualTo(replacementPatient));
                Assert.That(current.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                Assert.That(rescue.AssignedTarget, Is.EqualTo(replacementPatient));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(replacementPatient));
                Assert.That(rescue.PendingMedicalDoAfterTarget, Is.Null);
                Assert.That(rescue.PendingPlayerAction, Is.EqualTo(LuaMRescuePlayerActionKind.None));
            });

            // Exercise delayed effect verification independently from the
            // physical DoAfter callback.
            rescue.PendingMedicalDoAfterTarget = stalePatient;
            rescue.PendingMedicalDoAfterKind = "injector";
            rescue.PendingMedicalIntentGeneration = staleGeneration;
            rescue.PendingMedicalEffectVerification = true;
            rescue.PendingPlayerAction = LuaMRescuePlayerActionKind.Treat;
            rescue.PendingPlayerActionTarget = stalePatient;
        });

        await pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.That(coordinator.GetSnapshot(agent, out var current), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(current.Generation, Is.EqualTo(replacementGeneration));
                Assert.That(current.Target, Is.EqualTo(replacementPatient));
                Assert.That(current.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                Assert.That(rescue.AssignedTarget, Is.EqualTo(replacementPatient));
                Assert.That(rescue.TaskPatientTarget, Is.EqualTo(replacementPatient));
                Assert.That(rescue.PendingMedicalDoAfterTarget, Is.Null);
                Assert.That(rescue.PendingMedicalEffectVerification, Is.False);
                Assert.That(rescue.PendingPlayerAction, Is.EqualTo(LuaMRescuePlayerActionKind.None));
            });

        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task TerminalMissionCancelsPhysicalTreatmentFromSameGeneration()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var rescueSystem = entities.System<LuaMRescueAgentSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var hands = entities.System<SharedHandsSystem>();
        var solutions = entities.System<SharedSolutionContainerSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid patient = default;
        EntityUid injector = default;
        uint generation = 0;
        await server.WaitAssertion(() =>
        {
            agent = SpawnAgent(entities, map.MapId, Vector2.Zero);
            patient = SpawnDamagedPatient(
                entities,
                map.MapId,
                new Vector2(0.5f, 0f),
                "Blunt",
                20);
            injector = entities.SpawnEntity(
                "LuaMRescueLongDoAfterBruteInjector",
                Coordinates(map.MapId, Vector2.Zero));
            Assert.That(hands.TryPickupAnyHand(agent, injector), Is.True);
            Assert.That(
                rescueSystem.TryOrderPlayerAction(
                    agent,
                    LuaMRescuePlayerActionKind.Treat,
                    patient,
                    out var orderStatus),
                Is.True,
                orderStatus);
        });

        await pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.That(coordinator.GetSnapshot(agent, out var active), Is.True);
            generation = active.Generation;
            Assert.Multiple(() =>
            {
                Assert.That(active.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
                Assert.That(rescue.PendingMedicalIntentGeneration, Is.EqualTo(generation));
                Assert.That(rescue.PendingMedicalDoAfterTarget, Is.EqualTo(patient));
                Assert.That(CountActiveMedicalDoAfters(entities, agent, patient), Is.EqualTo(1));
            });

            Assert.That(
                coordinator.Fail(
                    agent,
                    generation,
                    LuaMRescueFailureReason.DeadlineExceeded,
                    out var terminal),
                Is.True);
            Assert.That(terminal.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Failed));
        });

        await pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.That(coordinator.GetSnapshot(agent, out var terminal), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(terminal.Generation, Is.EqualTo(generation));
                Assert.That(terminal.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Failed));
                Assert.That(terminal.FailureReason, Is.EqualTo(LuaMRescueFailureReason.DeadlineExceeded));
                Assert.That(rescue.PendingMedicalDoAfterTarget, Is.Null);
                Assert.That(rescue.PendingMedicalEffectVerification, Is.False);
                Assert.That(rescue.PendingPlayerAction, Is.EqualTo(LuaMRescuePlayerActionKind.None));
                Assert.That(rescue.LastPlayerActionStatus, Does.Contain("stale medical intent"));
                Assert.That(CountActiveMedicalDoAfters(entities, agent, patient), Is.Zero,
                    "A terminal mission must cancel its physical treatment before medicine can be applied.");
            });
            Assert.That(solutions.TryGetSolution(injector, "pen", out _, out var solution), Is.True);
            Assert.That(solution.Volume, Is.GreaterThan(FixedPoint2.Zero));

            // A completed transfer may be waiting for a delayed metabolic
            // effect when the mission terminalizes. The same generation and
            // target must not make that observation phase authoritative again.
            rescue.PendingMedicalDoAfterTarget = patient;
            rescue.PendingMedicalDoAfterItem = injector;
            rescue.PendingMedicalDoAfterKind = "injector";
            rescue.PendingMedicalIntentGeneration = generation;
            rescue.PendingMedicalEffectVerification = true;
            rescue.PendingMedicalEffectVerificationStartedAt = server.Timing.CurTime;
            rescue.PendingMedicalExpectedPositiveEffect = true;
            rescue.PendingPlayerAction = LuaMRescuePlayerActionKind.Treat;
            rescue.PendingPlayerActionTarget = patient;
        });

        await pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.That(coordinator.GetSnapshot(agent, out var terminal), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(terminal.Generation, Is.EqualTo(generation));
                Assert.That(terminal.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Failed));
                Assert.That(rescue.PendingMedicalDoAfterTarget, Is.Null);
                Assert.That(rescue.PendingMedicalEffectVerification, Is.False);
                Assert.That(rescue.PendingPlayerAction, Is.EqualTo(LuaMRescuePlayerActionKind.None));
            });

            var defibrillator = entities.SpawnEntity(
                "Defibrillator",
                Coordinates(map.MapId, Vector2.Zero));
            var defibrillatorComponent = entities.GetComponent<DefibrillatorComponent>(defibrillator);
            rescue.PendingMedicalDoAfterTarget = patient;
            rescue.PendingMedicalDoAfterItem = defibrillator;
            rescue.PendingMedicalDoAfterKind = "defibrillator";
            rescue.PendingMedicalIntentGeneration = generation;
            rescue.PendingMedicalOutcomeRecorded = false;
            rescue.PendingMedicalOutcomeSucceeded = false;
            rescue.DefibrillationAttempts[patient] = 2;
            var lateDefibrillation = new TargetDefibrillatedEvent(
                agent,
                (defibrillator, defibrillatorComponent));
            entities.EventBus.RaiseLocalEvent(patient, ref lateDefibrillation);

            Assert.Multiple(() =>
            {
                Assert.That(rescue.PendingMedicalOutcomeRecorded, Is.False,
                    "A terminal generation must ignore its late defibrillation result.");
                Assert.That(rescue.PendingMedicalOutcomeSucceeded, Is.False);
                Assert.That(rescue.DefibrillationAttempts.GetValueOrDefault(patient), Is.EqualTo(2),
                    "A late success must not clear the terminal generation's retry accounting.");
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FullHandsAreStowedBeforeAutomaticPullAndContainedTargetNeverReachesPullingSystem()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var handsSystem = entities.System<SharedHandsSystem>();
        var rescueSystem = entities.System<LuaMRescueAgentSystem>();
        var mobState = entities.System<MobStateSystem>();
        var containers = entities.System<SharedContainerSystem>();
        var map = await pair.CreateTestMap();

        EntityUid containedAgent = default;
        EntityUid containedPatient = default;
        await server.WaitAssertion(() =>
        {
            var agent = SpawnAgent(entities, map.MapId, Vector2.Zero);
            var patient = entities.SpawnEntity("MobHuman", Coordinates(map.MapId, new Vector2(0.5f, 0f)));
            mobState.ChangeMobState(patient, MobState.Critical);
            var shuttle = entities.SpawnEntity(null, Coordinates(map.MapId, new Vector2(20f, 0f)));
            var backpack = EquipBackpack(entities, agent, map.MapId, Vector2.Zero);
            var firstItem = entities.SpawnEntity("Brutepack", Coordinates(map.MapId, Vector2.Zero));
            var secondItem = entities.SpawnEntity("Ointment", Coordinates(map.MapId, Vector2.Zero));
            Assert.That(handsSystem.TryPickupAnyHand(agent, firstItem), Is.True);
            Assert.That(handsSystem.TryPickupAnyHand(agent, secondItem), Is.True);

            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            ConfigureForEvacuation(rescue, shuttle);
            rescue.AutoTreatWithCarriedItems = false;
            Assert.That(HasEmptyHand(entities, agent), Is.False, "The pull precondition must begin with both hands full.");

            Assert.That(rescueSystem.TryOrderAgent(agent, patient, out var firstStatus), Is.True, firstStatus);
            Assert.Multiple(() =>
            {
                Assert.That(HasEmptyHand(entities, agent), Is.True,
                    "The coordinator must stow a held item before calling PullingSystem.");
                Assert.That(
                    entities.GetComponent<StorageComponent>(backpack).Container.ContainedEntities
                        .Any(item => item == firstItem || item == secondItem),
                    Is.True);
                Assert.That(entities.GetComponent<PullerComponent>(agent).Pulling, Is.Null);
            });

            Assert.That(rescueSystem.TryOrderAgent(agent, patient, out var secondStatus), Is.True, secondStatus);
            Assert.That(entities.GetComponent<PullerComponent>(agent).Pulling, Is.EqualTo(patient));

            containedAgent = SpawnAgent(entities, map.MapId, new Vector2(4f, 0f));
            containedPatient = entities.SpawnEntity("MobHuman", Coordinates(map.MapId, new Vector2(4.5f, 0f)));
            mobState.ChangeMobState(containedPatient, MobState.Critical);
            var owner = entities.SpawnEntity(null, Coordinates(map.MapId, new Vector2(4.5f, 0f)));
            var container = containers.EnsureContainer<Container>(owner, "LuaMRescueMedicalRuntimeContainer");
            Assert.That(containers.Insert(containedPatient, container), Is.True);

            Assert.That(
                rescueSystem.TryOrderPlayerAction(
                    containedAgent,
                    LuaMRescuePlayerActionKind.Pull,
                    containedPatient,
                    out var status),
                Is.True,
                status);
        });

        await pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(containedAgent);
            Assert.Multiple(() =>
            {
                Assert.That(entities.GetComponent<PullerComponent>(containedAgent).Pulling, Is.Null);
                Assert.That(rescue.PendingPlayerAction, Is.EqualTo(LuaMRescuePlayerActionKind.None));
                Assert.That(rescue.LastPlayerActionStatus, Does.Contain("ContainedTarget"));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task EmptyDefibrillatorDoesNotHideChargedCandidateInInventory()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var rescueSystem = entities.System<LuaMRescueAgentSystem>();
        var mobState = entities.System<MobStateSystem>();
        var minds = entities.System<MindSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid patient = default;
        EntityUid backpack = default;
        EntityUid empty = default;
        EntityUid charged = default;
        await server.WaitAssertion(() =>
        {
            agent = SpawnAgent(entities, map.MapId, Vector2.Zero);
            patient = SpawnDamagedPatient(
                entities,
                map.MapId,
                new Vector2(0.5f, 0f),
                "Asphyxiation",
                50);
            var patientMind = minds.CreateMind(null, "LuaM rescue usable defib patient");
            minds.TransferTo(patientMind, patient, createGhost: false, mind: patientMind.Comp);
            mobState.ChangeMobState(patient, MobState.Dead);

            backpack = EquipBackpack(entities, agent, map.MapId, Vector2.Zero);
            empty = entities.SpawnEntity("DefibrillatorEmpty", Coordinates(map.MapId, Vector2.Zero));
            charged = entities.SpawnEntity("Defibrillator", Coordinates(map.MapId, Vector2.Zero));
            InsertIntoStorage(entities, backpack, empty, agent);
            InsertIntoStorage(entities, backpack, charged, agent);
            var shuttle = entities.SpawnEntity(
                null,
                Coordinates(map.MapId, new Vector2(20f, 0f)));

            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            ConfigureForEvacuation(rescue, shuttle);
            rescue.AutoDefibDeadPatients = true;
            rescue.AutoTreatWithCarriedItems = false;
            rescue.AutoAcquireTargets = true;

            Assert.That(rescueSystem.TryOrderAgent(agent, patient, out var status), Is.True, status);
        });

        await pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.PendingMedicalDoAfterTarget, Is.EqualTo(patient));
                Assert.That(rescue.PendingMedicalDoAfterItem, Is.EqualTo(charged));
                Assert.That(rescue.PendingMedicalDoAfterKind, Is.EqualTo("defibrillator"));
                Assert.That(rescue.TerminalDefibrillationFailures, Does.Not.ContainKey(patient));
                Assert.That(CountActiveMedicalDoAfters(entities, agent, patient), Is.EqualTo(1));
                Assert.That(entities.GetComponent<StorageComponent>(backpack).Container.Contains(empty), Is.True);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DefibrillationAttemptCapIsTerminalAndDoesNotStartAnotherDoAfter()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var rescueSystem = entities.System<LuaMRescueAgentSystem>();
        var mobState = entities.System<MobStateSystem>();
        var minds = entities.System<MindSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var agent = SpawnAgent(entities, map.MapId, Vector2.Zero);
            var patient = entities.SpawnEntity("MobHuman", Coordinates(map.MapId, new Vector2(0.5f, 0f)));
            var patientMind = minds.CreateMind(null, "LuaM rescue defib cap patient");
            minds.TransferTo(patientMind, patient, createGhost: false, mind: patientMind.Comp);
            mobState.ChangeMobState(patient, MobState.Dead);

            var shuttle = entities.SpawnEntity(null, Coordinates(map.MapId, new Vector2(20f, 0f)));
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            ConfigureForEvacuation(rescue, shuttle);
            rescue.AutoDefibDeadPatients = true;
            rescue.DefibrillationAttempts[patient] = 3;

            Assert.That(rescueSystem.TryOrderAgent(agent, patient, out var firstStatus), Is.True, firstStatus);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.DefibrillationAttempts[patient], Is.EqualTo(3));
                Assert.That(rescue.TerminalDefibrillationFailures, Does.ContainKey(patient));
                Assert.That(
                    rescue.TerminalDefibrillationFailures[patient],
                    Does.Contain(nameof(LuaMRescueFailureReason.Unrevivable)));
                Assert.That(CountActiveMedicalDoAfters(entities, agent), Is.Zero);
            });

            Assert.That(rescueSystem.TryOrderAgent(agent, patient, out var secondStatus), Is.True, secondStatus);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.DefibrillationAttempts[patient], Is.EqualTo(3));
                Assert.That(rescue.TerminalDefibrillationFailures, Does.ContainKey(patient));
                Assert.That(CountActiveMedicalDoAfters(entities, agent), Is.Zero);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ThreeCompletedFailedDefibrillationsReachTerminalWithoutFourthDoAfter()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var rescueSystem = entities.System<LuaMRescueAgentSystem>();
        var mobState = entities.System<MobStateSystem>();
        var minds = entities.System<MindSystem>();
        var map = await pair.CreateTestMap();

        EntityUid agent = default;
        EntityUid patient = default;
        await server.WaitAssertion(() =>
        {
            agent = SpawnAgent(entities, map.MapId, Vector2.Zero);
            patient = SpawnDamagedPatient(
                entities,
                map.MapId,
                new Vector2(0.5f, 0f),
                "Asphyxiation",
                500);
            var patientMind = minds.CreateMind(null, "LuaM real bounded defib failure patient");
            minds.TransferTo(patientMind, patient, createGhost: false, mind: patientMind.Comp);
            mobState.ChangeMobState(patient, MobState.Dead);

            var backpack = EquipBackpack(entities, agent, map.MapId, Vector2.Zero);
            var defibrillator = entities.SpawnEntity(
                "LuaMRescueWeakFastDefibrillator",
                Coordinates(map.MapId, Vector2.Zero));
            InsertIntoStorage(entities, backpack, defibrillator, agent);
            var shuttle = entities.SpawnEntity(
                null,
                Coordinates(map.MapId, new Vector2(20f, 0f)));

            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            ConfigureForEvacuation(rescue, shuttle);
            rescue.AutoDefibDeadPatients = true;
            rescue.AutoDefibCooldown = 0.01f;
            rescue.AutoTreatWithCarriedItems = false;
            rescue.AutoAcquireTargets = true;
        });

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var expectedAttempt = attempt;
            await server.WaitAssertion(() =>
            {
                Assert.That(
                    rescueSystem.TryOrderAgent(agent, patient, out var status),
                    Is.True,
                    status);
            });

            await pair.RunTicksSync(1);
            await server.WaitAssertion(() =>
            {
                var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
                Assert.Multiple(() =>
                {
                    Assert.That(rescue.DefibrillationAttempts.GetValueOrDefault(patient), Is.EqualTo(expectedAttempt));
                    Assert.That(rescue.CompletedDefibrillationFailures.GetValueOrDefault(patient), Is.EqualTo(expectedAttempt - 1));
                    Assert.That(CountActiveMedicalDoAfters(entities, agent, patient), Is.EqualTo(1));
                    Assert.That(rescue.PendingMedicalDoAfterKind, Is.EqualTo("defibrillator"));
                });
            });

            await pair.RunTicksSync(4);
            await server.WaitAssertion(() =>
            {
                var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
                var diagnostics =
                    $"attempt={expectedAttempt}; started={rescue.DefibrillationAttempts.GetValueOrDefault(patient)}; " +
                    $"completed={rescue.CompletedDefibrillationFailures.GetValueOrDefault(patient)}; " +
                    $"terminal={rescue.TerminalDefibrillationFailures.GetValueOrDefault(patient)}; " +
                    $"activity={rescue.ActivityContext.Activity}/{rescue.ActivityContext.TerminalStatus}/gen={rescue.ActivityContext.Generation}; " +
                    $"pending={rescue.PendingMedicalDoAfterTarget}/{rescue.PendingMedicalDoAfterKind}/gen={rescue.PendingMedicalIntentGeneration}; " +
                    $"status={rescue.LastAutoDefibStatus}";
                Assert.Multiple(() =>
                {
                    Assert.That(mobState.IsDead(patient), Is.True,
                        "A weak completed zap must not revive the high-damage patient.");
                    Assert.That(CountActiveMedicalDoAfters(entities, agent, patient), Is.Zero);
                    Assert.That(rescue.PendingMedicalDoAfterTarget, Is.Null);
                    Assert.That(rescue.DefibrillationAttempts.GetValueOrDefault(patient), Is.EqualTo(expectedAttempt));
                    Assert.That(rescue.CompletedDefibrillationFailures.GetValueOrDefault(patient), Is.EqualTo(expectedAttempt), diagnostics);
                });
            });
        }

        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.That(rescue.TerminalDefibrillationFailures, Does.ContainKey(patient));
            Assert.That(
                rescue.TerminalDefibrillationFailures[patient],
                Does.Contain("three-attempt").Or.Contain("3 bounded"));

            Assert.That(
                rescueSystem.TryOrderAgent(agent, patient, out var fourthStatus),
                Is.True,
                fourthStatus);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.DefibrillationAttempts.GetValueOrDefault(patient), Is.EqualTo(3));
                Assert.That(rescue.CompletedDefibrillationFailures.GetValueOrDefault(patient), Is.EqualTo(3));
                Assert.That(CountActiveMedicalDoAfters(entities, agent, patient), Is.Zero,
                    "The terminal policy must reject a fourth DoAfter.");
            });
        });

        await pair.RunTicksSync(6);
        await server.WaitAssertion(() =>
        {
            var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
            Assert.Multiple(() =>
            {
                Assert.That(rescue.DefibrillationAttempts.GetValueOrDefault(patient), Is.EqualTo(3));
                Assert.That(rescue.CompletedDefibrillationFailures.GetValueOrDefault(patient), Is.EqualTo(3));
                Assert.That(CountActiveMedicalDoAfters(entities, agent, patient), Is.Zero);
                Assert.That(rescue.TerminalDefibrillationFailures, Does.ContainKey(patient));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task UnrevivableAndRottenBodiesTerminalizeWithoutDefibRetries()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var rescueSystem = entities.System<LuaMRescueAgentSystem>();
        var mobState = entities.System<MobStateSystem>();
        var minds = entities.System<MindSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            for (var index = 0; index < 2; index++)
            {
                var origin = new Vector2(index * 4f, 0f);
                var agent = SpawnAgent(entities, map.MapId, origin);
                var patient = entities.SpawnEntity(
                    "MobHuman",
                    Coordinates(map.MapId, origin + new Vector2(0.5f, 0f)));
                var mind = minds.CreateMind(null, $"LuaM terminal defib patient {index}");
                minds.TransferTo(mind, patient, createGhost: false, mind: mind.Comp);
                mobState.ChangeMobState(patient, MobState.Dead);
                if (index == 0)
                    entities.EnsureComponent<UnrevivableComponent>(patient);
                else
                    entities.EnsureComponent<RottingComponent>(patient);

                var shuttle = entities.SpawnEntity(
                    null,
                    Coordinates(map.MapId, origin + new Vector2(2f, 0f)));
                var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
                ConfigureForEvacuation(rescue, shuttle);
                rescue.AutoDefibDeadPatients = true;

                Assert.That(rescueSystem.TryOrderAgent(agent, patient, out var firstStatus), Is.True, firstStatus);
                Assert.That(rescue.TerminalDefibrillationFailures, Does.ContainKey(patient));
                Assert.That(
                    rescue.TerminalDefibrillationFailures[patient],
                    Does.Contain(nameof(LuaMRescueFailureReason.Unrevivable)));
                Assert.That(rescue.DefibrillationAttempts.GetValueOrDefault(patient), Is.Zero);

                Assert.That(rescueSystem.TryOrderAgent(agent, patient, out var secondStatus), Is.True, secondStatus);
                Assert.Multiple(() =>
                {
                    Assert.That(rescue.DefibrillationAttempts.GetValueOrDefault(patient), Is.Zero);
                    Assert.That(CountActiveMedicalDoAfters(entities, agent, patient), Is.Zero);
                });
            }
        });

        await pair.CleanReturnAsync();
    }

    private static void AssertMatchingNestedTreatment(
        IEntityManager entities,
        MapId mapId,
        Vector2 origin,
        string damageType,
        string expectedPrototype,
        string wrongPrototype)
    {
        var rescueSystem = entities.System<LuaMRescueAgentSystem>();
        var coordinator = entities.System<LuaMRescueActivityCoordinatorSystem>();
        var solutions = entities.System<SharedSolutionContainerSystem>();
        var agent = SpawnAgent(entities, mapId, origin);
        var patient = SpawnDamagedPatient(entities, mapId, origin + new Vector2(0.5f, 0f), damageType, 20);
        var shuttle = entities.SpawnEntity(null, Coordinates(mapId, origin + new Vector2(20f, 0f)));
        var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
        ConfigureForEvacuation(rescue, shuttle);

        var medkit = EquipNestedMedkit(entities, agent, mapId, origin);
        var expected = entities.SpawnEntity(expectedPrototype, Coordinates(mapId, origin));
        var wrong = entities.SpawnEntity(wrongPrototype, Coordinates(mapId, origin));
        var emptyMedipen = entities.SpawnEntity("EmergencyMedipen", Coordinates(mapId, origin));
        EmptyMedipen(entities, solutions, emptyMedipen);
        InsertIntoStorage(entities, medkit, wrong, agent);
        InsertIntoStorage(entities, medkit, emptyMedipen, agent);
        InsertIntoStorage(entities, medkit, expected, agent);

        Assert.Multiple(() =>
        {
            Assert.That(rescueSystem.IsEffectiveTreatmentItem(expected, patient, out var expectedReason), Is.True,
                expectedReason);
            Assert.That(rescueSystem.IsEffectiveTreatmentItem(wrong, patient, out _), Is.False);
            Assert.That(rescueSystem.IsEffectiveTreatmentItem(emptyMedipen, patient, out var emptyReason), Is.False);
            Assert.That(emptyReason, Is.EqualTo("ItemEmpty"));
        });

        var damageBefore = entities.GetComponent<DamageableComponent>(patient).TotalDamage.Float();
        Assert.That(rescueSystem.TryOrderAgent(agent, patient, out var status), Is.True, status);

        Assert.That(coordinator.GetSnapshot(agent, out var running), Is.True);
        var activeDoAfterCount = CountActiveMedicalDoAfters(entities, agent, patient);
        var firstDoAfterId = rescue.PendingMedicalDoAfterId;
        Assert.Multiple(() =>
        {
            Assert.That(activeDoAfterCount, Is.EqualTo(1));
            Assert.That(rescue.PendingMedicalDoAfterTarget, Is.EqualTo(patient));
            Assert.That(rescue.PendingMedicalDoAfterItem, Is.EqualTo(expected));
            Assert.That(rescue.PendingMedicalOutcomeRecorded, Is.False);
            Assert.That(rescue.PendingMedicalOutcomeSucceeded, Is.False);
            Assert.That(rescue.TargetStallAccumulator, Is.Zero,
                "A running treatment is real progress and must not consume the route-stall budget.");
            Assert.That(running.DoAfterStatus, Is.EqualTo(LuaMRescueDoAfterStatus.Running));
            Assert.That(running.TerminalStatus, Is.EqualTo(LuaMRescueTerminalStatus.Active));
            Assert.That(entities.GetComponent<DamageableComponent>(patient).TotalDamage.Float(), Is.EqualTo(damageBefore));
            Assert.That(IsHeld(entities, agent, expected), Is.True);
            Assert.That(
                entities.HasComponent<Content.Shared.NPC.ActiveNPCComponent>(agent),
                Is.False,
                "FollowCompound must be asleep while a BreakOnMove medical DoAfter is active.");
            Assert.That(
                entities.GetComponent<Content.Server.NPC.HTN.HTNComponent>(agent)
                    .Blackboard.ContainsKey(Content.Server.NPC.NPCBlackboard.FollowTarget),
                Is.False,
                "The stale movement target must be removed for the duration of treatment.");
            Assert.That(entities.GetComponent<StorageComponent>(medkit).Container.Contains(wrong), Is.True);
            Assert.That(entities.GetComponent<StorageComponent>(medkit).Container.Contains(emptyMedipen), Is.True);
        });

        Assert.That(rescueSystem.TryOrderAgent(agent, patient, out var repeatStatus), Is.True, repeatStatus);
        Assert.Multiple(() =>
        {
            Assert.That(CountActiveMedicalDoAfters(entities, agent, patient), Is.EqualTo(1));
            Assert.That(rescue.PendingMedicalDoAfterId, Is.EqualTo(firstDoAfterId));
            Assert.That(rescue.PendingMedicalOutcomeRecorded, Is.False);
            Assert.That(entities.GetComponent<DamageableComponent>(patient).TotalDamage.Float(), Is.EqualTo(damageBefore));
        });
    }

    private static void AssertEmptyMedipenWasNotSelected(
        IEntityManager entities,
        EntityUid agent,
        EntityUid patient,
        EntityUid medkit,
        EntityUid emptyMedipen)
    {
        var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
        Assert.Multiple(() =>
        {
            Assert.That(rescue.PendingPlayerAction, Is.EqualTo(LuaMRescuePlayerActionKind.None));
            Assert.That(rescue.LastPlayerActionStatus, Does.Contain("no usable medical item"));
            Assert.That(rescue.PendingMedicalDoAfterTarget, Is.Null);
            Assert.That(CountActiveMedicalDoAfters(entities, agent, patient), Is.Zero);
            Assert.That(IsHeld(entities, agent, emptyMedipen), Is.False);
            Assert.That(entities.GetComponent<StorageComponent>(medkit).Container.Contains(emptyMedipen), Is.True);
        });
    }

    private static EntityUid SpawnAgent(IEntityManager entities, MapId mapId, Vector2 position)
    {
        var agent = entities.SpawnEntity(TestAgent, Coordinates(mapId, position));
        var rescue = entities.GetComponent<LuaMRescueAgentComponent>(agent);
        // This deliberately minimal medical fixture has no rescue loadout or
        // internals and runs on a map-root coordinate. Its tests exercise
        // treatment state, not the production agent's vacuum survival policy.
        rescue.AutoManageLifeSupport = false;
        rescue.AutoAcquireTargets = false;
        rescue.AutoAnalyzeBeforeTreatment = false;
        rescue.AutoPickupNearbyMedicalSupplies = false;
        rescue.AutoTakeNearbyStoredMedicalSupplies = false;
        rescue.AutoResupplyFromVending = false;
        rescue.AutoRouteShuttleToTargets = false;
        rescue.AutoReturnShuttle = false;
        rescue.AutoUnbucklePatientsForEvacuation = false;
        rescue.BucklePatientsOnShuttle = false;
        return agent;
    }

    private static EntityUid SpawnDamagedPatient(
        IEntityManager entities,
        MapId mapId,
        Vector2 position,
        string damageType,
        int amount)
    {
        var patient = entities.SpawnEntity("MobHuman", Coordinates(mapId, position));
        // These tests exercise medical decisions, not vacuum exposure. Map-root
        // coordinates otherwise let Barotrauma add unrelated low-pressure damage
        // while a real treatment DoAfter is running.
        entities.RemoveComponent<BarotraumaComponent>(patient);
        var damage = new DamageSpecifier();
        damage.DamageDict.Add(damageType, amount);
        Assert.That(
            entities.System<DamageableSystem>().TryChangeDamage(patient, damage, ignoreResistances: true),
            Is.Not.Null);
        return patient;
    }

    private static void ConfigureForEvacuation(LuaMRescueAgentComponent rescue, EntityUid shuttle)
    {
        rescue.AssignedShuttle = shuttle;
        rescue.EvacuateTargetsToShuttle = true;
        rescue.EvacuationMinDamage = 1f;
        rescue.AutoDefibDeadPatients = false;
        rescue.AutoTreatWithCarriedItems = true;
    }

    private static EntityUid EquipNestedMedkit(
        IEntityManager entities,
        EntityUid agent,
        MapId mapId,
        Vector2 position)
    {
        var backpack = EquipBackpack(entities, agent, mapId, position);
        var medkit = entities.SpawnEntity("Medkit", Coordinates(mapId, position));
        InsertIntoStorage(entities, backpack, medkit, agent);
        return medkit;
    }

    private static EntityUid EquipBackpack(
        IEntityManager entities,
        EntityUid agent,
        MapId mapId,
        Vector2 position)
    {
        var backpack = entities.SpawnEntity("ClothingBackpack", Coordinates(mapId, position));
        Assert.That(entities.System<InventorySystem>().TryEquip(agent, backpack, "back", silent: true), Is.True);
        return backpack;
    }

    private static void InsertIntoStorage(
        IEntityManager entities,
        EntityUid storage,
        EntityUid item,
        EntityUid user)
    {
        Assert.That(
            entities.System<SharedStorageSystem>().Insert(storage, item, out _, user: user),
            Is.True);
    }

    private static void EmptyMedipen(
        IEntityManager entities,
        SharedSolutionContainerSystem solutions,
        EntityUid medipen)
    {
        Assert.That(solutions.TryGetSolution(medipen, "pen", out var solutionEntity, out var solution), Is.True);
        solutions.SplitSolution(solutionEntity.Value, solution.Volume);
        Assert.That(solution.Volume, Is.EqualTo(FixedPoint2.Zero));
    }

    private static bool HasEmptyHand(IEntityManager entities, EntityUid agent)
    {
        var hands = entities.GetComponent<HandsComponent>(agent);
        return entities.System<SharedHandsSystem>().TryGetEmptyHand(agent, out _, hands);
    }

    private static bool IsHeld(IEntityManager entities, EntityUid agent, EntityUid item)
    {
        return entities.GetComponent<HandsComponent>(agent).Hands.Values.Any(hand => hand.HeldEntity == item);
    }

    private static int CountActiveMedicalDoAfters(
        IEntityManager entities,
        EntityUid agent,
        EntityUid? target = null)
    {
        if (!entities.TryGetComponent<DoAfterComponent>(agent, out var doAfters))
            return 0;

        return doAfters.DoAfters.Values.Count(doAfter =>
            !doAfter.Cancelled &&
            !doAfter.Completed &&
            (target == null || doAfter.Args.Target == target) &&
            doAfter.Args.Event is HealingDoAfterEvent
                or HyposprayDoAfterEvent
                or InjectorDoAfterEvent
                or HealthAnalyzerDoAfterEvent
                or DefibrillatorZapDoAfterEvent);
    }

    private static MapCoordinates Coordinates(MapId mapId, Vector2 position)
    {
        return new MapCoordinates(position, mapId);
    }
}
