#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Content.Server.Database;
using Content.Server.Mind;
using Content.Server.PDA.Ringer;
using Content.Server.RandomMetadata;
using Content.Server.Revolutionary.Components;
using Content.Server.Traitor.Components;
using Content.Server.Traitor.Uplink;
using Content.Server.Zombies;
using Content.Server._LuaM.Cryo;
using Content.Server._NF.CryoSleep;
using Content.Shared.Access.Components;
using Content.Shared.Actions;
using Content.Shared.Antag;
using Content.Shared.Bed.Sleep;
using Content.Shared.Body.Organ;
using Content.Shared.CombatMode.Pacification;
using Content.Shared.Damage;
using Content.Shared.DoAfter;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared._Shitmed.Body.Part;
using Content.Shared.Mind;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Content.Shared.NukeOps;
using Content.Shared.Preferences;
using Content.Shared.Revolutionary.Components;
using Content.Shared.Storage;
using Content.Shared.Store.Components;
using Content.Shared.Zombies;
using Content.Shared._NF.CryoSleep;
using Content.Shared.Verbs;
using NUnit.Framework;
using Robust.Server.Containers;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Serilog.Events;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMDeepCryoRuntimeTest
{
    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  id: LuaMDeepCryoTestPod
  name: deep cryo test pod
  components:
  - type: ContainerContainer
    containers:
      body: !type:ContainerSlot

- type: entity
  id: LuaMDeepCryoTestBody
  name: deep cryo test body
  components:
  - type: Damageable
    damageContainer: Biological
  - type: MobState
  - type: MobThresholds
    thresholds:
      0: Alive
      100: Critical
      200: Dead
  - type: MindContainer
  - type: DoAfter
  - type: ContainerContainer
    containers:
      inventory: !type:Container
        ents: []

- type: entity
  id: LuaMDeepCryoTestSleepPod
  name: deep cryo pending test pod
  components:
  - type: CryoSleep
  - type: ContainerContainer
    containers:
      body_container: !type:ContainerSlot
      other: !type:ContainerSlot
";

    [Test]
    public async Task RestartLikeReloadPreservesNestedInventoryAndSanitizesAuthority()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var containers = entities.System<ContainerSystem>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();

        LuaMDeepCryoPayload payload = default;
        EntityUid restored = default;
        await server.WaitAssertion(() =>
        {
            var pod = entities.SpawnEntity("LuaMDeepCryoTestPod", MapCoordinates.Nullspace);
            var body = entities.SpawnEntity("LuaMDeepCryoTestBody", MapCoordinates.Nullspace);
            var backpack = entities.SpawnEntity("ClothingBackpack", MapCoordinates.Nullspace);
            var card = entities.SpawnEntity("CaptainIDCard", MapCoordinates.Nullspace);

            Assert.That(containers.Insert(body, containers.GetContainer(pod, "body")), Is.True);
            Assert.That(containers.Insert(backpack, containers.GetContainer(body, "inventory")), Is.True);
            var storage = entities.GetComponent<StorageComponent>(backpack);
            Assert.That(containers.Insert(card, storage.Container), Is.True);
            Assert.That(entities.GetComponent<TransformComponent>(backpack).ParentUid, Is.EqualTo(body));
            Assert.That(entities.GetComponent<TransformComponent>(card).ParentUid, Is.EqualTo(backpack));

            var job = entities.EnsureComponent<PlayerJobComponent>(body);
            job.JobPrototype = "Captain";
            Assert.That(entities.GetComponent<AccessComponent>(card).Tags, Is.Not.Empty);

            Assert.That(cryo.TryCapturePayload(body, out payload, out var captureReason),
                Is.True, captureReason);
            Assert.That(payload.EntityCount, Is.GreaterThanOrEqualTo(3));

            var snapshot = Snapshot(payload);
            Assert.That(cryo.TryLoadPayload(snapshot, out restored, out var loadReason),
                Is.True, loadReason);

            var graph = CollectGraph(entities, restored);
            var restoredBackpack = graph.Single(uid => Prototype(entities, uid) == "ClothingBackpack");
            var restoredCard = graph.Single(uid => Prototype(entities, uid) == "CaptainIDCard");
            Assert.Multiple(() =>
            {
                Assert.That(entities.GetComponent<TransformComponent>(restoredBackpack).ParentUid,
                    Is.EqualTo(restored));
                Assert.That(entities.GetComponent<TransformComponent>(restoredCard).ParentUid,
                    Is.EqualTo(restoredBackpack));
                Assert.That(entities.HasComponent<PlayerJobComponent>(restored), Is.False);
                Assert.That(entities.HasComponent<SleepingComponent>(restored), Is.True);
                Assert.That(entities.GetComponent<MobStateComponent>(restored).CurrentState,
                    Is.EqualTo(MobState.Alive));
                Assert.That(entities.GetComponent<DamageableComponent>(restored).TotalDamage.Int(), Is.Zero);
            });

            var restoredAccess = entities.GetComponent<AccessComponent>(restoredCard);
            var restoredId = entities.GetComponent<IdCardComponent>(restoredCard);
            var restoredCompany = restoredId.CompanyName;
            var restoredJobIcon = restoredId.JobIcon;
            Assert.Multiple(() =>
            {
                Assert.That(restoredAccess.Enabled, Is.False);
                Assert.That(restoredAccess.Tags, Is.Empty);
                Assert.That(restoredId.JobTitle, Is.Null);
                Assert.That(restoredId.JobDepartments, Is.Empty);
                Assert.That(restoredCompany.ToString(), Is.EqualTo("None"));
                Assert.That(restoredJobIcon.ToString(), Is.EqualTo("JobIconUnknown"));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PendingStoreCannotBeEjected()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var containers = entities.System<ContainerSystem>();
        var sleep = entities.System<CryoSleepSystem>();

        await server.WaitAssertion(() =>
        {
            var pod = entities.SpawnEntity("LuaMDeepCryoTestSleepPod", MapCoordinates.Nullspace);
            var body = entities.SpawnEntity("LuaMDeepCryoTestBody", MapCoordinates.Nullspace);
            var podComp = entities.GetComponent<CryoSleepComponent>(pod);
            Assert.That(containers.Insert(body, podComp.BodyContainer), Is.True);

            var pending = entities.EnsureComponent<LuaMDeepCryoPendingComponent>(body);
            pending.OperationId = Guid.NewGuid();
            pending.Pod = pod;
            pending.Source = LuaMDeepCryoSource.FrontierCryoSleep;

            Assert.Multiple(() =>
            {
                Assert.That(sleep.EjectBody(pod, podComp), Is.False);
                Assert.That(podComp.BodyContainer.ContainedEntity, Is.EqualTo(body));
                Assert.That(entities.GetComponent<TransformComponent>(body).ParentUid, Is.EqualTo(pod));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task InterruptedAutomaticStoreClearsDoAfterAndAllowsRetry()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var containers = entities.System<ContainerSystem>();
        var sleep = entities.System<CryoSleepSystem>();

        await server.WaitAssertion(() =>
        {
            var pod = entities.SpawnEntity("LuaMDeepCryoTestSleepPod", MapCoordinates.Nullspace);
            var body = entities.SpawnEntity("LuaMDeepCryoTestBody", MapCoordinates.Nullspace);
            var podComp = entities.GetComponent<CryoSleepComponent>(pod);
            Assert.That(containers.Insert(body, podComp.BodyContainer), Is.True);

            StartAutomaticCryoStore(sleep, body, pod, podComp);
            Assert.That(podComp.CryosleepDoAfter, Is.Not.Null);

            Assert.That(sleep.EjectBody(pod, podComp, body), Is.True);
            Assert.That(podComp.CryosleepDoAfter, Is.Null);
            Assert.That(podComp.BodyContainer.ContainedEntity, Is.Null);

            Assert.That(containers.Insert(body, podComp.BodyContainer), Is.True);
            StartAutomaticCryoStore(sleep, body, pod, podComp);
            Assert.That(podComp.CryosleepDoAfter, Is.Not.Null,
                "Cancelling or denying cryo must leave the pod able to start the next 30-second store.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SleepingBodyStillGetsSelfEnterCryoVerb()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var pod = entities.SpawnEntity("LuaMDeepCryoTestSleepPod", MapCoordinates.Nullspace);
            var body = entities.SpawnEntity("LuaMDeepCryoTestBody", MapCoordinates.Nullspace);
            entities.EnsureComponent<SleepingComponent>(body);

            var ev = new GetVerbsEvent<AlternativeVerb>(
                body,
                pod,
                null,
                null,
                canInteract: false,
                canComplexInteract: false,
                canAccess: true,
                new List<VerbCategory>());

            entities.EventBus.RaiseLocalEvent(pod, ev);

            Assert.That(ev.Verbs.Any(verb =>
                    verb.Category == VerbCategory.Insert &&
                    !string.IsNullOrWhiteSpace(verb.Text)),
                Is.True,
                "A player who already used the sleep action must still see the cryosleep self-enter verb.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FailedImmediateGhostRestoresMindAndEjectsBody()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var containers = entities.System<ContainerSystem>();
        var sleep = entities.System<CryoSleepSystem>();
        var minds = entities.System<MindSystem>();
        var sawExpectedFailure = false;

        bool JudgeExpectedGhostFailure(string sawmillName, LogEvent message)
        {
            if (sawmillName == "system.cryo_sleep" &&
                message.RenderMessage().Contains("ghost-spawn-failed", StringComparison.Ordinal))
            {
                sawExpectedFailure = true;
                return true;
            }

            return false;
        }

        pair.ServerLogHandler.JudgeLog += JudgeExpectedGhostFailure;
        try
        {
            await server.WaitAssertion(() =>
            {
                // A disconnected test server has no map, grid or observer point.
                // This exercises GhostSystem's detach-and-null failure path
                // without mutating the maps of a connected test session.
                var pod = entities.SpawnEntity("LuaMDeepCryoTestSleepPod", MapCoordinates.Nullspace);
                var body = entities.SpawnEntity("LuaMDeepCryoTestBody", MapCoordinates.Nullspace);
                var podComp = entities.GetComponent<CryoSleepComponent>(pod);
                Assert.That(containers.Insert(body, podComp.BodyContainer), Is.True);

                var mindId = minds.CreateMind(null, nameof(FailedImmediateGhostRestoresMindAndEjectsBody));
                minds.TransferTo(mindId, body);

                sleep.CryoStoreBody(body, pod);

                var mind = entities.GetComponent<MindComponent>(mindId);
                Assert.Multiple(() =>
                {
                    Assert.That(mind.OwnedEntity, Is.EqualTo(body));
                    Assert.That(mind.VisitingEntity, Is.Null);
                    Assert.That(podComp.BodyContainer.ContainedEntity, Is.Null);
                    Assert.That(entities.HasComponent<LuaMDeepCryoPendingComponent>(body), Is.False);
                });
            });

            Assert.That(sawExpectedFailure, Is.True);
        }
        finally
        {
            pair.ServerLogHandler.JudgeLog -= JudgeExpectedGhostFailure;
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task CaptureDropsCryoDoAfterThatReferencesThePod()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var containers = entities.System<ContainerSystem>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();

        await server.WaitAssertion(() =>
        {
            var pod = entities.SpawnEntity("LuaMDeepCryoTestSleepPod", MapCoordinates.Nullspace);
            var body = entities.SpawnEntity("LuaMDeepCryoTestBody", MapCoordinates.Nullspace);
            var podComp = entities.GetComponent<CryoSleepComponent>(pod);
            Assert.That(containers.Insert(body, podComp.BodyContainer), Is.True);

            // Runtime-created body-part appearance used to collide with the
            // serializer's reserved component discriminator key "type".
            entities.EnsureComponent<BodyPartAppearanceComponent>(body);

            var transient = entities.EnsureComponent<DoAfterComponent>(body);
            var args = new DoAfterArgs(
                entities,
                body,
                TimeSpan.FromSeconds(30),
                new SharedCryoSleepSystem.CryoStoreDoAfterEvent(),
                pod,
                body,
                pod);
            transient.DoAfters.Add(0, new Content.Shared.DoAfter.DoAfter(0, args, TimeSpan.Zero));
            entities.EnsureComponent<ActiveDoAfterComponent>(body);

            Assert.That(cryo.TryCapturePayload(body, out _, out var reason), Is.True, reason);
            Assert.That(entities.HasComponent<DoAfterComponent>(body), Is.False,
                "Durable cryo must discard round-local do-after state before serialization.");
            Assert.That(entities.HasComponent<ActiveDoAfterComponent>(body), Is.False,
                "Durable cryo must discard the round-local active do-after marker too.");
            Assert.That(entities.GetComponent<TransformComponent>(body).ParentUid, Is.EqualTo(pod));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task IpcInstalledPositronicBrainPersistsButNestedPassengerDoesNot()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var containers = entities.System<ContainerSystem>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();

        await server.WaitAssertion(() =>
        {
            var pod = entities.SpawnEntity("LuaMDeepCryoTestPod", MapCoordinates.Nullspace);
            var ipc = entities.SpawnEntity("MobIPC", MapCoordinates.Nullspace);
            Assert.That(containers.Insert(ipc, containers.GetContainer(pod, "body")), Is.True);

            var positronicBrain = CollectGraph(entities, ipc)
                .Single(uid => Prototype(entities, uid) == "PositronicBrain");
            Assert.Multiple(() =>
            {
                Assert.That(entities.HasComponent<MobStateComponent>(positronicBrain), Is.True);
                Assert.That(entities.GetComponent<OrganComponent>(positronicBrain).Body, Is.EqualTo(ipc));
                Assert.That(cryo.TryCapturePayload(ipc, out _, out var ipcReason), Is.True, ipcReason);
            });

            var rejectedIpc = entities.SpawnEntity("MobIPC", MapCoordinates.Nullspace);
            var passenger = entities.SpawnEntity("MobMouse", MapCoordinates.Nullspace);
            entities.System<SharedTransformSystem>().SetParent(passenger, rejectedIpc);

            Assert.Multiple(() =>
            {
                Assert.That(cryo.TryCapturePayload(rejectedIpc, out _, out var passengerReason), Is.False);
                Assert.That(passengerReason, Is.EqualTo("snapshot-contains-additional-organic-body"));
                Assert.That(entities.HasComponent<DoAfterComponent>(rejectedIpc), Is.True,
                    "A rejected store must leave the IPC able to start the next cryo do-after.");
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task StoreRequiresBodyInsideClaimedPod()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();
        var containers = entities.System<ContainerSystem>();

        await server.WaitAssertion(() =>
        {
            var pod = entities.SpawnEntity("LuaMDeepCryoTestSleepPod", MapCoordinates.Nullspace);
            var body = entities.SpawnEntity("LuaMDeepCryoTestBody", MapCoordinates.Nullspace);
            Assert.That(containers.Insert(body, containers.GetContainer(pod, "other")), Is.True);

            Assert.That(cryo.TryBeginStore(
                body,
                pod,
                null,
                LuaMDeepCryoSource.FrontierCryoSleep,
                _ => { },
                out var reason), Is.False);
            Assert.That(reason, Is.EqualTo("body-not-contained-by-pod"));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RealPlayerPrototypeSerializesAndRoundAuthorityIsCanonicalized()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();
        var containers = entities.System<ContainerSystem>();
        var factions = entities.System<NpcFactionSystem>();
        var actions = entities.System<ActionContainerSystem>();

        EntityUid restored = default;
        await server.WaitAssertion(() =>
        {
            var bodyPrototype = prototypes.Index<EntityPrototype>("MobHuman");
            Assert.That(bodyPrototype.MapSavable, Is.False,
                "The regression requires the real player prototype's save:false policy.");

            var pod = entities.SpawnEntity("LuaMDeepCryoTestPod", MapCoordinates.Nullspace);
            var body = entities.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
            Assert.That(containers.Insert(body, containers.GetContainer(pod, "body")), Is.True);
            entities.EnsureComponent<NukeOperativeComponent>(body);
            entities.EnsureComponent<RevolutionaryComponent>(body);
            entities.EnsureComponent<HeadRevolutionaryComponent>(body);
            entities.EnsureComponent<PendingZombieComponent>(body);
            entities.EnsureComponent<ZombifyOnDeathComponent>(body);
            entities.EnsureComponent<IncurableZombieComponent>(body);
            entities.EnsureComponent<AutoTraitorComponent>(body);
            entities.EnsureComponent<PacifiedComponent>(body);
            entities.EnsureComponent<CommandStaffComponent>(body);
            entities.EnsureComponent<RandomMetadataComponent>(body);
            entities.EnsureComponent<ShowAntagIconsComponent>(body);
            factions.AddFaction(body, "Syndicate");
            actions.AddAction(body, "ActionTurnUndead");

            var nested = entities.SpawnEntity("CaptainIDCard", MapCoordinates.Nullspace);
            entities.EnsureComponent<UplinkComponent>(nested);
            entities.EnsureComponent<RingerUplinkComponent>(nested);
            entities.EnsureComponent<StoreComponent>(nested);
            entities.System<SharedTransformSystem>().SetParent(nested, body);

            var runtimeBindingsBefore = CollectRuntimeBindings(entities, containers, body);
            var runtimeChildrenBefore = runtimeBindingsBefore.Keys.ToHashSet();
            Assert.That(runtimeChildrenBefore, Is.Not.Empty,
                "MobHuman must exercise regenerated runtime children during this regression.");

            Assert.That(cryo.TryCapturePayload(body, out var payload, out var captureReason),
                Is.True, captureReason);
            Assert.That(bodyPrototype.MapSavable, Is.False,
                "Deep cryo must restore the global map-save policy after capture.");
            var graphAfterCapture = CollectGraph(entities, body).ToHashSet();
            Assert.That(runtimeChildrenBefore.All(graphAfterCapture.Contains), Is.True,
                "Capture must return every temporarily detached runtime child to the live body.");
            AssertRuntimeBindingsUnchanged(entities, containers, runtimeBindingsBefore);
            Assert.That(entities.GetComponent<TransformComponent>(body).ParentUid, Is.EqualTo(pod));

            Assert.That(cryo.TryLoadPayload(Snapshot(payload), out restored, out var loadReason),
                Is.True, loadReason);
            var restoredSleep = entities.GetComponent<SleepingComponent>(restored);
            restoredSleep.WakeAction = entities.SpawnEntity("ActionWake", MapCoordinates.Nullspace);
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human");
            Assert.That(cryo.TryApplyCanonicalAuthority(
                    restored,
                    profile,
                    bodyPrototype,
                    preserveLiveAttachment: false,
                    out var authorityReason),
                Is.True, authorityReason);

            var graph = CollectGraph(entities, restored);
            var restoredNested = graph.Single(uid => Prototype(entities, uid) == "CaptainIDCard");
            var graphPrototypes = graph.Select(uid => Prototype(entities, uid)).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(entities.HasComponent<NukeOperativeComponent>(restored), Is.False);
                Assert.That(entities.HasComponent<RevolutionaryComponent>(restored), Is.False);
                Assert.That(entities.HasComponent<HeadRevolutionaryComponent>(restored), Is.False);
                Assert.That(entities.HasComponent<PendingZombieComponent>(restored), Is.False);
                Assert.That(entities.HasComponent<ZombifyOnDeathComponent>(restored), Is.False);
                Assert.That(entities.HasComponent<IncurableZombieComponent>(restored), Is.False);
                Assert.That(entities.HasComponent<AutoTraitorComponent>(restored), Is.False);
                Assert.That(entities.HasComponent<PacifiedComponent>(restored), Is.False);
                Assert.That(entities.HasComponent<CommandStaffComponent>(restored), Is.False);
                Assert.That(entities.HasComponent<ShowAntagIconsComponent>(restored), Is.False);
                Assert.That(entities.GetComponent<SleepingComponent>(restored).WakeAction, Is.Null);
                Assert.That(entities.HasComponent<UplinkComponent>(restoredNested), Is.False);
                Assert.That(entities.HasComponent<RingerUplinkComponent>(restoredNested), Is.False);
                Assert.That(entities.HasComponent<StoreComponent>(restoredNested), Is.False);
                Assert.That(graph.Any(uid => Prototype(entities, uid) == "UplinkImplant"), Is.False);
                Assert.That(graphPrototypes, Does.Not.Contain("ActionTurnUndead"));
                Assert.That(entities.GetComponent<NpcFactionMemberComponent>(restored).Factions,
                    Does.Not.Contain((ProtoId<Content.Shared.NPC.Prototypes.NpcFactionPrototype>) "Syndicate"));
                Assert.That(entities.GetComponent<MetaDataComponent>(restored).EntityName,
                    Is.EqualTo(profile.Name));
            });

            var zombiePod = entities.SpawnEntity("LuaMDeepCryoTestPod", MapCoordinates.Nullspace);
            var zombie = entities.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
            Assert.That(containers.Insert(zombie, containers.GetContainer(zombiePod, "body")), Is.True);
            var zombieRuntimeBindings = CollectRuntimeBindings(entities, containers, zombie);
            var zombieRuntimeChildren = zombieRuntimeBindings.Keys.ToHashSet();
            entities.EnsureComponent<ZombieComponent>(zombie);
            Assert.That(cryo.TryCapturePayload(zombie, out _, out var zombieReason), Is.False);
            Assert.That(zombieReason, Is.EqualTo("irreversible-zombie-body"));
            var zombieGraphAfterRejection = CollectGraph(entities, zombie).ToHashSet();
            Assert.That(zombieRuntimeChildren.All(zombieGraphAfterRejection.Contains), Is.True,
                "Rejected capture must not disturb generated runtime children.");
            AssertRuntimeBindingsUnchanged(entities, containers, zombieRuntimeBindings);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RestorePublicationFenceRequiresTheExactLeaseToFinish()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();

        await server.WaitAssertion(() =>
        {
            var key = new LuaMDeepCryoPersistenceSystem.CharacterKey(
                new NetUserId(Guid.NewGuid()),
                42,
                3);
            var lease = Guid.NewGuid();
            Assert.That(cryo.TryStartRestorePublication(key, lease), Is.True);
            Assert.That(cryo.IsRestorePublicationActive(key), Is.True);

            cryo.FinishRestorePublication(new LuaMDeepCryoRestoreHandle(
                key,
                1,
                1,
                Guid.NewGuid(),
                EntityUid.Invalid,
                false));
            Assert.That(cryo.IsRestorePublicationActive(key), Is.True,
                "A stale handle must not release another restore's fresh-spawn fence.");

            cryo.FinishRestorePublication(new LuaMDeepCryoRestoreHandle(
                key,
                1,
                1,
                lease,
                EntityUid.Invalid,
                false));
            Assert.That(cryo.IsRestorePublicationActive(key), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MissingPrototypePayloadIsRejectedBeforeWorldExposure()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();
        var sawExpectedPrototypeError = false;

        bool JudgeExpectedPrototypeError(string sawmillName, LogEvent message)
        {
            if (sawmillName != "entity_deserializer")
                return false;

            var rendered = message.RenderMessage();
            if (rendered.Contains("LuaMDeepCryoDefinitelyMissingPrototype", StringComparison.Ordinal))
            {
                sawExpectedPrototypeError = true;
                return true;
            }

            return rendered.StartsWith("Found missing prototypes in map file.", StringComparison.Ordinal);
        }

        pair.ServerLogHandler.JudgeLog += JudgeExpectedPrototypeError;
        try
        {
            await server.WaitAssertion(() =>
            {
                var pod = entities.SpawnEntity("LuaMDeepCryoTestPod", MapCoordinates.Nullspace);
                var body = entities.SpawnEntity("LuaMDeepCryoTestBody", MapCoordinates.Nullspace);
                var child = entities.SpawnEntity("CaptainIDCard", MapCoordinates.Nullspace);
                Assert.That(entities.System<ContainerSystem>().Insert(
                    body,
                    entities.System<ContainerSystem>().GetContainer(pod, "body")), Is.True);
                entities.System<SharedTransformSystem>().SetParent(child, body);

                Assert.That(cryo.TryCapturePayload(body, out var payload, out var captureReason),
                    Is.True, captureReason);
                var yaml = Encoding.UTF8.GetString(payload.Bytes);
                Assert.That(yaml, Does.Contain("CaptainIDCard"));
                var incompatible = Encoding.UTF8.GetBytes(yaml.Replace(
                    "CaptainIDCard",
                    "LuaMDeepCryoDefinitelyMissingPrototype",
                    StringComparison.Ordinal));
                var corrupt = payload with
                {
                    Bytes = incompatible,
                    Hash = Convert.ToHexString(SHA256.HashData(incompatible)).ToLowerInvariant(),
                };

                Assert.That(cryo.TryLoadPayload(Snapshot(corrupt), out var loaded, out var reason), Is.False);
                Assert.That(loaded, Is.EqualTo(EntityUid.Invalid));
                Assert.That(reason, Does.Contain("prototype-validation"));
            });

            Assert.That(sawExpectedPrototypeError, Is.True,
                "The map loader should identify the missing prototype that will trigger DB quarantine.");
        }
        finally
        {
            pair.ServerLogHandler.JudgeLog -= JudgeExpectedPrototypeError;
            await pair.CleanReturnAsync();
        }
    }

    private static LuaMDeepCryoSnapshotRecord Snapshot(LuaMDeepCryoPayload payload)
    {
        var now = DateTime.UtcNow;
        return new LuaMDeepCryoSnapshotRecord(
            1,
            new NetUserId(Guid.NewGuid()),
            1,
            1,
            0,
            1,
            null,
            DbLuaMDeepCryoSnapshotStatus.Stored,
            0,
            LuaMDeepCryoPersistenceSystem.PayloadFormatVersion,
            payload.Bytes,
            payload.Hash,
            payload.Bytes.Length,
            payload.EntityCount,
            "integration-test",
            payload.PrototypeManifestHash,
            now,
            now,
            null,
            null,
            null,
            null,
            null);
    }

    private static List<EntityUid> CollectGraph(IEntityManager entities, EntityUid root)
    {
        var result = new List<EntityUid>();
        var stack = new Stack<EntityUid>();
        stack.Push(root);
        while (stack.TryPop(out var uid))
        {
            result.Add(uid);
            var children = entities.GetComponent<TransformComponent>(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                stack.Push(child);
        }

        return result;
    }

    private static Dictionary<EntityUid, (EntityUid Owner, string Id)> CollectRuntimeBindings(
        IEntityManager entities,
        ContainerSystem containers,
        EntityUid root)
    {
        var result = new Dictionary<EntityUid, (EntityUid, string)>();
        foreach (var uid in CollectGraph(entities, root))
        {
            if (entities.GetComponent<MetaDataComponent>(uid).EntityPrototype != null)
                continue;

            Assert.That(containers.TryGetContainingContainer((uid, null, null), out var container),
                Is.True, $"Generated runtime child {uid} must be container-bound.");
            result.Add(uid, (container.Owner, container.ID));
        }

        return result;
    }

    private static void AssertRuntimeBindingsUnchanged(
        IEntityManager entities,
        ContainerSystem containers,
        Dictionary<EntityUid, (EntityUid Owner, string Id)> expected)
    {
        foreach (var (uid, binding) in expected)
        {
            Assert.That(entities.EntityExists(uid), Is.True, $"Generated runtime child {uid} was deleted.");
            Assert.That(containers.TryGetContainingContainer((uid, null, null), out var container),
                Is.True, $"Generated runtime child {uid} was left outside its container.");
            Assert.That((container.Owner, container.ID), Is.EqualTo(binding),
                $"Generated runtime child {uid} changed its container binding.");
        }
    }

    private static string? Prototype(IEntityManager entities, EntityUid uid)
    {
        return entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID;
    }

    private static void StartAutomaticCryoStore(
        CryoSleepSystem sleep,
        EntityUid body,
        EntityUid pod,
        CryoSleepComponent component)
    {
        var method = typeof(CryoSleepSystem).GetMethod(
            "StartAutomaticCryoStore",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);
        method!.Invoke(sleep, new object[] { body, pod, component });
    }
}
