#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Content.Server.Bed.Cryostorage;
using Content.Server.Database;
using Content.Server.Ghost;
using Content.Server.KillTracking;
using Content.Server.Mind;
using Content.Server.PDA.Ringer;
using Content.Server.Preferences.Managers;
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
using Content.Shared.Bed.Cryostorage;
using Content.Shared.Bed.Sleep;
using Content.Shared.Body.Organ;
using Content.Shared.CCVar;
using Content.Shared.CombatMode.Pacification;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.DoAfter;
using Content.Shared.FixedPoint;
using Content.Shared.GameTicking;
using Content.Shared.Ghost;
using Content.Shared.Humanoid.Prototypes;
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
using Robust.Server.Player;
using Robust.Shared.Analyzers;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Player;
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
  - type: CryoSleepFallback
  - type: ContainerContainer
    containers:
      body_container: !type:ContainerSlot
      other: !type:ContainerSlot

- type: entity
  id: LuaMDeepCryoTestDoAfterItem
  name: deep cryo do-after item
  components:
  - type: DoAfter
";

    [Test]
    public void PresenceSafetyWindowsUseStrictDeterministicBoundaries()
    {
        var nowUtc = new DateTime(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc);
        var authority = new LuaMCharacterPresenceAuthorityRecord(
            1,
            null,
            Guid.NewGuid(),
            DbLuaMCharacterPresencePhase.Playable,
            "boundary-test",
            1,
            nowUtc,
            nowUtc,
            nowUtc.AddMinutes(2),
            0,
            0);

        Assert.Multiple(() =>
        {
            Assert.That(
                LuaMDeepCryoPersistenceSystem.HasSafePlayablePresenceWindow(authority, nowUtc),
                Is.False,
                "A playable lease exactly two minutes from expiry must remain suspended.");
            Assert.That(
                LuaMDeepCryoPersistenceSystem.HasSafePlayablePresenceWindow(
                    authority with { ExpiresAtUtc = nowUtc.AddMinutes(2).AddTicks(1) },
                    nowUtc),
                Is.True,
                "Only a playable lease strictly beyond the two-minute boundary may be exposed.");
            Assert.That(
                LuaMDeepCryoPersistenceSystem.HasSafeRestorePublicationWindow(
                    authority with { Phase = DbLuaMCharacterPresencePhase.RestoreClaim, ExpiresAtUtc = nowUtc.AddMinutes(1) },
                    nowUtc),
                Is.False,
                "A restore claim exactly one minute from expiry must not be published.");
            Assert.That(
                LuaMDeepCryoPersistenceSystem.HasSafeRestorePublicationWindow(
                    authority with
                    {
                        Phase = DbLuaMCharacterPresencePhase.RestoreClaim,
                        ExpiresAtUtc = nowUtc.AddMinutes(1).AddTicks(1),
                    },
                    nowUtc),
                Is.True,
                "Only a restore claim strictly beyond the one-minute boundary may be published.");
        });
    }

    [Test]
    public void ForeignRestoreAuthorityRequiresDifferentTokenAndStrictlyNewerEpoch()
    {
        var key = new LuaMDeepCryoPersistenceSystem.CharacterKey(
            new NetUserId(Guid.NewGuid()),
            17,
            2);
        var leaseId = Guid.NewGuid();
        var handle = new LuaMDeepCryoRestoreHandle(
            key,
            31,
            8,
            leaseId,
            EntityUid.Invalid,
            true,
            100);
        var foreign = new LuaMCharacterPresenceAuthorityRecord(
            key.ProfileId,
            handle.SnapshotId,
            Guid.NewGuid(),
            DbLuaMCharacterPresencePhase.Playable,
            "foreign-proof-test",
            1,
            DateTime.UtcNow,
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(15),
            1,
            104);

        Assert.Multiple(() =>
        {
            Assert.That(
                LuaMDeepCryoPersistenceSystem.IsDefinitiveForeignAcknowledgedPresence(foreign, handle),
                Is.False,
                "An equal authority epoch must not settle an acknowledged publication.");
            Assert.That(
                LuaMDeepCryoPersistenceSystem.IsDefinitiveForeignRestoreAuthority(foreign, handle),
                Is.False,
                "An equal authority epoch must not settle an ambiguous restore abort.");
            Assert.That(
                LuaMDeepCryoPersistenceSystem.IsDefinitiveForeignAcknowledgedPresence(
                    foreign with { AuthorityLifecycleRevision = 103 },
                    handle),
                Is.False,
                "A lower authority epoch must remain ambiguous.");
            Assert.That(
                LuaMDeepCryoPersistenceSystem.IsDefinitiveForeignRestoreAuthority(
                    foreign with { AuthorityLifecycleRevision = 103 },
                    handle),
                Is.False,
                "A lower abort authority epoch must remain ambiguous.");
            Assert.That(
                LuaMDeepCryoPersistenceSystem.IsDefinitiveForeignAcknowledgedPresence(
                    foreign with { AuthorityLifecycleRevision = 105 },
                    handle),
                Is.True,
                "Only a different token at a strictly newer authority epoch may settle ACK compensation.");
            Assert.That(
                LuaMDeepCryoPersistenceSystem.IsDefinitiveForeignRestoreAuthority(
                    foreign with { AuthorityLifecycleRevision = 105 },
                    handle),
                Is.True,
                "Only a different token at a strictly newer authority epoch may settle an abort.");
            Assert.That(
                LuaMDeepCryoPersistenceSystem.IsDefinitiveForeignRestoreAuthority(
                    foreign with { LeaseId = leaseId, AuthorityLifecycleRevision = 105 },
                    handle),
                Is.False,
                "A newer observation of the same token is not foreign supersession.");
        });
    }

    [Test]
    public async Task PlayableRenewalFencesBeforeAwaitRebindsStaleSlotGenerationAndOnlyExactSafeReplayRestores()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);
        var players = server.ResolveDependency<IPlayerManager>();
        var session = players.GetSessionById(clientSession!.UserId);
        var entities = server.ResolveDependency<IEntityManager>();
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var realDb = server.ResolveDependency<IServerDbManager>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();
        var minds = entities.System<MindSystem>();
        var transforms = entities.System<SharedTransformSystem>();
        var testMap = await pair.CreateTestMap();
        var slot = preferences.GetPreferences(session.UserId).SelectedCharacterIndex;
        var profileId = await realDb.GetCharacterIdAsync(session.UserId, slot);
        Assert.That(profileId, Is.Not.Null);
        var profile = (HumanoidCharacterProfile) preferences.GetPreferences(session.UserId).Characters[slot];
        var speciesPrototypeId = prototypes.Index<SpeciesPrototype>(profile.Species).Prototype.Id;
        var authority = await EnsurePlayableAuthorityAsync(
            realDb,
            session.UserId,
            profileId!.Value,
            slot,
            TimeSpan.FromMinutes(1));

        var proxy = DispatchProxy.Create<IServerDbManager, PausedPresenceRenewProxy>();
        var proxyState = (PausedPresenceRenewProxy) (object) proxy;
        proxyState.Inner = realDb;
        SetPrivateField(cryo, "_db", proxy);
        var preferencesProxy = DispatchProxy.Create<IServerPreferencesManager, UnresolvedIdentityPreferencesProxy>();
        var preferencesProxyState = (UnresolvedIdentityPreferencesProxy) (object) preferencesProxy;
        preferencesProxyState.Inner = preferences;

        EntityUid body = default;
        EntityUid mindId = default;
        try
        {
            await server.WaitAssertion(() =>
            {
                body = entities.SpawnEntity(speciesPrototypeId, testMap.GridCoords);
                var identity = entities.EnsureComponent<LuaMDeepCryoIdentityComponent>(body);
                BindPlayableTestIdentity(
                    identity,
                    session.UserId,
                    profileId!.Value,
                    slot,
                    authority);
                identity.PresenceLeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(1);
                var currentGeneration = preferences.GetCharacterSlotGeneration(session.UserId, slot);
                identity.SlotGeneration = currentGeneration == long.MaxValue
                    ? currentGeneration - 1
                    : currentGeneration + 1;
                (mindId, _) = AttachSessionToBody(
                    entities,
                    players,
                    minds,
                    session,
                    body,
                    nameof(PlayableRenewalFencesBeforeAwaitRebindsStaleSlotGenerationAndOnlyExactSafeReplayRestores));
            });

            await DrainServerTaskAsync(pair, proxyState.RenewStarted.Task);
            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(entities.EntityExists(body), Is.True);
                    Assert.That(cryo.IsPresenceSuspended(body), Is.True);
                    Assert.That(entities.GetComponent<TransformComponent>(body).MapID, Is.EqualTo(MapId.Nullspace));
                    Assert.That(entities.GetComponent<MindComponent>(mindId).CurrentEntity, Is.Not.EqualTo(body));
                    Assert.That(session.AttachedEntity, Is.Not.EqualTo(body),
                        "The body must be fenced before the renewal DB await can complete.");
                });

                // Simulate an unrelated local system trying to expose the body
                // while the DB call is still hung. The next update must re-fence
                // all three location, mind and session surfaces.
                transforms.SetCoordinates(body, testMap.GridCoords);
                minds.ControlMob(session.UserId, body);
                players.SetAttachedEntity(session, body, true);
            });
            await pair.RunTicksSync(1);
            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(entities.GetComponent<TransformComponent>(body).MapID, Is.EqualTo(MapId.Nullspace));
                    Assert.That(entities.GetComponent<MindComponent>(mindId).CurrentEntity, Is.Not.EqualTo(body));
                    Assert.That(session.AttachedEntity, Is.Not.EqualTo(body));
                });
            });

            proxyState.ReleaseFirstRenew();
            await WaitForServerConditionAsync(
                pair,
                () => proxyState.RenewCalls >= 2 && proxyState.AuthorityReads >= 1,
                "The unknown renewal cycle did not complete its exact retries and null authority re-read.");
            await server.WaitAssertion(() =>
            {
                Assert.That(entities.EntityExists(body), Is.True,
                    "A null authority read alone must not delete the sole suspended body.");
                Assert.That(cryo.IsPresenceSuspended(body), Is.True);
            });

            SetPrivateField(cryo, "_preferences", preferencesProxy);
            proxyState.AllowCommit = true;
            await pair.RunTicksSync(180);
            await WaitForServerConditionAsync(
                pair,
                () => entities.EntityExists(body) &&
                      cryo.IsPresenceSuspended(body) &&
                      entities.GetComponent<LuaMDeepCryoIdentityComponent>(body).PresenceLeaseRevision > authority.Revision,
                "The exact safe renewal did not finish while preferences were unavailable.");

            for (var cycle = 0; cycle < 2; cycle++)
            {
                var previousCalls = proxyState.RenewCalls;
                var previousRevision = entities.GetComponent<LuaMDeepCryoIdentityComponent>(body)
                    .PresenceLeaseRevision;
                await server.WaitAssertion(() =>
                    entities.GetComponent<LuaMDeepCryoIdentityComponent>(body).PresenceLeaseExpiresAtUtc =
                        DateTime.UtcNow.AddMinutes(1));
                await pair.RunTicksSync(2);
                await WaitForServerConditionAsync(
                    pair,
                    () => proxyState.RenewCalls > previousCalls &&
                          entities.GetComponent<LuaMDeepCryoIdentityComponent>(body).PresenceLeaseRevision >
                          previousRevision,
                    "A suspended body with unavailable preferences did not start a new immutable renewal cycle.");
                await server.WaitAssertion(() =>
                {
                    Assert.That(entities.EntityExists(body), Is.True);
                    Assert.That(cryo.IsPresenceSuspended(body), Is.True);
                });
            }

            preferencesProxyState.FailIdentityLookup = false;
            SetPrivateField(cryo, "_preferences", preferences);
            await server.WaitAssertion(() =>
                entities.GetComponent<LuaMDeepCryoIdentityComponent>(body).PresenceLeaseExpiresAtUtc =
                    DateTime.UtcNow.AddMinutes(1));
            await pair.RunTicksSync(2);
            await WaitForServerConditionAsync(
                pair,
                () => entities.EntityExists(body) &&
                      !cryo.IsPresenceSuspended(body) &&
                      session.AttachedEntity == body &&
                      entities.GetComponent<MindComponent>(mindId).CurrentEntity == body,
                "The exact safe renewal did not restore the same body after preferences recovered.");

            var renewalRequests = proxyState.Requests;
            Assert.Multiple(() =>
            {
                Assert.That(proxyState.FirstRequest, Is.Not.Null);
                Assert.That(renewalRequests.Count(request => ReferenceEquals(request, proxyState.FirstRequest)),
                    Is.GreaterThanOrEqualTo(3),
                    "The unknown first renewal must replay the same immutable request object.");
                Assert.That(renewalRequests.Select(request => request.OperationId).Distinct().Count(),
                    Is.GreaterThanOrEqualTo(4),
                    "Each later lead window must create a new immutable renewal operation.");
                Assert.That(entities.GetComponent<LuaMDeepCryoIdentityComponent>(body).PresenceLeaseId,
                    Is.EqualTo(authority.LeaseId));
                Assert.That(entities.GetComponent<LuaMDeepCryoIdentityComponent>(body).SlotGeneration,
                    Is.EqualTo(preferences.GetCharacterSlotGeneration(session.UserId, slot)),
                    "A disconnect-like local generation bump must rebind after exact durable renewal, not delete the sole body.");
            });
        }
        finally
        {
            proxyState.AllowCommit = true;
            proxyState.ReleaseFirstRenew();
            preferencesProxyState.FailIdentityLookup = false;
            SetPrivateField(cryo, "_preferences", preferences);
            SetPrivateField(cryo, "_db", realDb);
        }

        await pair.CleanReturnAsync();
    }

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
    public async Task RuntimeKillTrackingDoesNotBlockCaptureOrSurviveRestore()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var containers = entities.System<ContainerSystem>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();
        var damage = entities.System<DamageableSystem>();

        EntityUid restored = default;
        await server.WaitAssertion(() =>
        {
            var pod = entities.SpawnEntity("LuaMDeepCryoTestPod", MapCoordinates.Nullspace);
            var body = entities.SpawnEntity("LuaMDeepCryoTestBody", MapCoordinates.Nullspace);
            Assert.That(containers.Insert(body, containers.GetContainer(pod, "body")), Is.True);

            var tracker = entities.EnsureComponent<KillTrackerComponent>(body);
            var source = new KillEnvironmentSource();
            var recordedDamage = FixedPoint2.New(42);
            var blunt = prototypes.Index<DamageTypePrototype>("Blunt");
            Assert.That(damage.TryChangeDamage(
                body,
                new DamageSpecifier(blunt, recordedDamage),
                ignoreResistances: true), Is.Not.Null);
            Assert.That(tracker.LifetimeDamage[source], Is.EqualTo(recordedDamage),
                "The regression requires a real non-empty runtime attribution map.");

            Assert.That(cryo.TryCapturePayload(body, out var payload, out var captureReason),
                Is.True, captureReason);
            Assert.Multiple(() =>
            {
                Assert.That(tracker.LifetimeDamage, Has.Count.EqualTo(1),
                    "Synchronous capture must not erase live round-local kill tracking.");
                Assert.That(tracker.LifetimeDamage[source], Is.EqualTo(recordedDamage));
                Assert.That(Encoding.UTF8.GetString(payload.Bytes), Does.Not.Contain("lifetimeDamage"),
                    "Round-local damage attribution must not enter a durable character snapshot.");
            });

            Assert.That(cryo.TryLoadPayload(Snapshot(payload), out restored, out var loadReason),
                Is.True, loadReason);
            var restoredTracker = entities.GetComponent<KillTrackerComponent>(restored);
            Assert.Multiple(() =>
            {
                Assert.That(restoredTracker.KillState, Is.EqualTo(tracker.KillState),
                    "The configured kill threshold remains ordinary component data.");
                Assert.That(restoredTracker.LifetimeDamage, Is.Empty,
                    "A restored character must begin with no prior-round damage attribution.");
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
                Assert.That(containers.Remove(body, podComp.BodyContainer), Is.False,
                    "A direct container removal must not break an in-flight durable store fence.");
                Assert.That(containers.TryRemoveFromContainer(body), Is.False,
                    "Generic container removal must not break an in-flight durable store fence.");
                Assert.That(sleep.EjectBody(pod, podComp), Is.False);
                Assert.That(podComp.BodyContainer.ContainedEntity, Is.EqualTo(body));
                Assert.That(entities.GetComponent<TransformComponent>(body).ParentUid, Is.EqualTo(pod));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RestoreReservationBlocksExternalRemovalAndPodShutdownDeletesBody()
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
            entities.EnsureComponent<LuaMDeepCryoRestoreReservationComponent>(pod).LeaseId = Guid.NewGuid();

            Assert.Multiple(() =>
            {
                Assert.That(containers.Remove(body, podComp.BodyContainer), Is.False,
                    "A direct ContainerSystem.Remove must not bypass the publication reservation.");
                Assert.That(containers.TryRemoveFromContainer(body), Is.False,
                    "A generic TryRemoveFromContainer must not expose the body before durable ACK.");
                Assert.That(sleep.EjectBody(pod, podComp, body), Is.False);
                Assert.That(podComp.BodyContainer.ContainedEntity, Is.EqualTo(body));
                Assert.That(entities.GetComponent<TransformComponent>(body).ParentUid, Is.EqualTo(pod));
            });

            entities.RemoveComponent<LuaMDeepCryoRestoreReservationComponent>(pod);
            Assert.That(sleep.EjectBody(pod, podComp, body), Is.True,
                "Once durable completion clears the exact reservation, normal ejection must work again.");
            Assert.That(podComp.BodyContainer.ContainedEntity, Is.Null);

            Assert.That(containers.Insert(body, podComp.BodyContainer), Is.True);
            entities.EnsureComponent<LuaMDeepCryoRestoreReservationComponent>(pod).LeaseId = Guid.NewGuid();
            entities.DeleteEntity(pod);
            Assert.That(entities.EntityExists(body), Is.False,
                "Server-side container shutdown must delete the fenced body rather than ejecting it into the world.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task UpstreamStoreSuccessFinalizesWhilePendingFenceIsHeld()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);
        var players = server.ResolveDependency<IPlayerManager>();
        var session = players.GetSessionById(clientSession!.UserId);
        var entities = server.ResolveDependency<IEntityManager>();
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var db = server.ResolveDependency<IServerDbManager>();
        var containers = entities.System<ContainerSystem>();
        var upstream = entities.System<CryostorageSystem>();
        var slot = preferences.GetPreferences(session.UserId).SelectedCharacterIndex;
        var profileId = await db.GetCharacterIdAsync(session.UserId, slot);
        Assert.That(profileId, Is.Not.Null);
        var playableAuthority = await EnsurePlayableAuthorityAsync(
            db, session.UserId, profileId!.Value, slot);

        EntityUid body = default;
        EntityUid pod = default;
        await server.WaitAssertion(() =>
        {
            pod = entities.SpawnEntity("CryogenicSleepUnit", MapCoordinates.Nullspace);
            var profile = (HumanoidCharacterProfile) preferences.GetPreferences(session.UserId).Characters[slot];
            var species = prototypes.Index<SpeciesPrototype>(profile.Species);
            body = entities.SpawnEntity(species.Prototype.Id, MapCoordinates.Nullspace);
            var identity = entities.EnsureComponent<LuaMDeepCryoIdentityComponent>(body);
            BindPlayableTestIdentity(identity, session.UserId, profileId!.Value, slot, playableAuthority);
            var contained = entities.EnsureComponent<CryostorageContainedComponent>(body);
            contained.Cryostorage = pod;
            contained.UserId = session.UserId;
            var cryostorage = entities.GetComponent<CryostorageComponent>(pod);
            Assert.That(containers.Insert(body, containers.GetContainer(pod, cryostorage.ContainerId)), Is.True);

            // Null suppresses reconnect handling; durable identity still comes
            // from the profile-bound marker above.
            upstream.HandleEnterCryostorage((body, contained), null);
        });

        var finalized = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!finalized && DateTime.UtcNow < deadline)
        {
            await server.WaitAssertion(() =>
            {
                finalized = entities.EntityExists(body) &&
                            entities.HasComponent<LuaMDeepCryoStoredComponent>(body) &&
                            !entities.HasComponent<LuaMDeepCryoPendingComponent>(body) &&
                            entities.GetComponent<TransformComponent>(body).ParentUid != pod;
            });
            if (!finalized)
            {
                await server.WaitIdleAsync();
                await pair.RunTicksSync(1);
                await Task.Yield();
            }
        }

        var snapshot = await db.GetLuaMDeepCryoSnapshotAsync(session.UserId, profileId.Value, slot);
        await server.WaitAssertion(() =>
        {
            var cryostorage = entities.GetComponent<CryostorageComponent>(pod);
            var container = containers.GetContainer(pod, cryostorage.ContainerId);
            Assert.Multiple(() =>
            {
                Assert.That(finalized, Is.True,
                    "Upstream finalization must be able to force-remove the body while its pending fence is held.");
                Assert.That(container.Contains(body), Is.False);
                Assert.That(cryostorage.StoredPlayers, Does.Contain(body));
                Assert.That(snapshot?.Status, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Stored));
            });
        });

        await pair.CleanReturnAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FrontierStoreFenceBlocksReturnAndOnlyProvenFailureRestoresPriorControl(
        bool beginWithReturnableVisit)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);
        var players = server.ResolveDependency<IPlayerManager>();
        var session = players.GetSessionById(clientSession!.UserId);
        var entities = server.ResolveDependency<IEntityManager>();
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var realDb = server.ResolveDependency<IServerDbManager>();
        var containers = entities.System<ContainerSystem>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();
        var sleep = entities.System<CryoSleepSystem>();
        var ghosts = entities.System<GhostSystem>();
        var minds = entities.System<MindSystem>();
        var testMap = await pair.CreateTestMap();
        var slot = preferences.GetPreferences(session.UserId).SelectedCharacterIndex;
        var profileId = await realDb.GetCharacterIdAsync(session.UserId, slot);
        Assert.That(profileId, Is.Not.Null);
        var profile = (HumanoidCharacterProfile) preferences.GetPreferences(session.UserId).Characters[slot];
        var speciesPrototypeId = prototypes.Index<SpeciesPrototype>(profile.Species).Prototype.Id;
        var playableAuthority = await EnsurePlayableAuthorityAsync(
            realDb, session.UserId, profileId!.Value, slot);

        EntityUid body = default;
        EntityUid pod = default;
        EntityUid mindId = default;
        EntityUid priorGhost = default;
        await server.WaitAssertion(() =>
        {
            pod = entities.SpawnEntity("LuaMDeepCryoTestSleepPod", testMap.GridCoords);
            body = entities.SpawnEntity(speciesPrototypeId, testMap.GridCoords);
            var identity = entities.EnsureComponent<LuaMDeepCryoIdentityComponent>(body);
            BindPlayableTestIdentity(identity, session.UserId, profileId!.Value, slot, playableAuthority);
            Assert.That(containers.Insert(body, entities.GetComponent<CryoSleepComponent>(pod).BodyContainer), Is.True);

            var attached = AttachSessionToBody(entities, players, minds, session, body,
                nameof(FrontierStoreFenceBlocksReturnAndOnlyProvenFailureRestoresPriorControl));
            mindId = attached.Id;
            var mind = attached.Component;
            if (!beginWithReturnableVisit)
                return;

            priorGhost = ghosts.SpawnGhost((mindId, mind), body, canReturn: true) ?? EntityUid.Invalid;
            Assert.That(priorGhost.Valid, Is.True);
            Assert.That(entities.GetComponent<GhostComponent>(priorGhost).CanReturnToBody, Is.True,
                "The regression requires a pre-existing returnable visit.");
        });

        var proxy = DispatchProxy.Create<IServerDbManager, PausedBeforeCryoStoreProxy>();
        var proxyState = (PausedBeforeCryoStoreProxy) (object) proxy;
        proxyState.Inner = realDb;
        proxyState.ForcedStatus = LuaMDeepCryoWriteStatus.InvalidRequest;
        proxyState.ReturnExpiringPresencePrecondition = true;
        SetPrivateField(cryo, "_db", proxy);
        var sawExpectedFailure = false;
        bool JudgeExpectedFailure(string sawmillName, LogEvent message)
        {
            if (!message.RenderMessage().Contains("database-store-InvalidRequest", StringComparison.Ordinal))
                return false;

            sawExpectedFailure = true;
            return true;
        }

        pair.ServerLogHandler.JudgeLog += JudgeExpectedFailure;
        try
        {
            await server.WaitAssertion(() => sleep.CryoStoreBody(body, pod));
            await DrainServerTaskAsync(pair, proxyState.StoreStarted.Task);

            EntityUid fencedGhost = default;
            await server.WaitAssertion(() =>
            {
                fencedGhost = AssertStoreControlFenced(entities, session, mindId, body);
                if (beginWithReturnableVisit)
                    Assert.That(fencedGhost, Is.EqualTo(priorGhost),
                        "An existing visit must be fenced in place rather than replaced.");
            });

            await server.WaitAssertion(() =>
            {
                Assert.That(entities.GetComponent<GhostComponent>(fencedGhost).CanReturnToBody, Is.False,
                    "The pending Store fence must reject return before any durable outcome is known.");
                Assert.That(entities.GetComponent<MindComponent>(mindId).CurrentEntity, Is.EqualTo(fencedGhost));
                Assert.That(session.AttachedEntity, Is.EqualTo(fencedGhost),
                    "The Store fence must retain control on the ghost while its outcome is unknown.");
            });

            proxyState.ReleaseStore();
            await DrainServerTaskAsync(pair, proxyState.StoreReturned.Task);
            await WaitForServerConditionAsync(
                pair,
                () => !cryo.IsStorePending(body) &&
                      entities.GetComponent<TransformComponent>(body).ParentUid != pod,
                "Frontier no-commit rollback did not release the pending body.");

            await server.WaitAssertion(() =>
            {
                var mind = entities.GetComponent<MindComponent>(mindId);
                if (beginWithReturnableVisit)
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(mind.OwnedEntity, Is.EqualTo(body));
                        Assert.That(mind.VisitingEntity, Is.EqualTo(priorGhost));
                        Assert.That(mind.CurrentEntity, Is.EqualTo(priorGhost));
                        Assert.That(session.AttachedEntity, Is.EqualTo(priorGhost));
                        Assert.That(entities.GetComponent<GhostComponent>(priorGhost).CanReturnToBody, Is.True,
                            "A proven no-commit result must restore the pre-existing visit's return flag.");
                    });
                }
                else
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(mind.OwnedEntity, Is.EqualTo(body));
                        Assert.That(mind.VisitingEntity, Is.Null);
                        Assert.That(mind.CurrentEntity, Is.EqualTo(body));
                        Assert.That(session.AttachedEntity, Is.EqualTo(body),
                            "A proven no-commit result must restore the body that was controlled before Store.");
                    });
                }
            });

            if (beginWithReturnableVisit)
            {
                await RequestGhostReturnAsync(pair);
                await server.WaitAssertion(() =>
                {
                    var mind = entities.GetComponent<MindComponent>(mindId);
                    Assert.Multiple(() =>
                    {
                        Assert.That(mind.CurrentEntity, Is.EqualTo(body));
                        Assert.That(mind.VisitingEntity, Is.Null);
                        Assert.That(session.AttachedEntity, Is.EqualTo(body),
                            "The original ghost return path must work again after proven no-commit rollback.");
                    });
                });
            }

            Assert.That(await realDb.GetLuaMDeepCryoSnapshotAsync(session.UserId, profileId.Value, slot), Is.Null);
            Assert.That(proxyState.PresenceRenewCalls, Is.GreaterThanOrEqualTo(1),
                "A proven no-commit Store inside the safety lead must renew its exact token before rollback.");
            Assert.That(sawExpectedFailure, Is.True);
        }
        finally
        {
            proxyState.ReleaseStore();
            pair.ServerLogHandler.JudgeLog -= JudgeExpectedFailure;
            SetPrivateField(cryo, "_db", realDb);
        }

        await pair.CleanReturnAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FrontierStoreCommitOrConflictNeverRestoresReturnableVisit(bool authoritativeConflict)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);
        var players = server.ResolveDependency<IPlayerManager>();
        var session = players.GetSessionById(clientSession!.UserId);
        var entities = server.ResolveDependency<IEntityManager>();
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var realDb = server.ResolveDependency<IServerDbManager>();
        var containers = entities.System<ContainerSystem>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();
        var sleep = entities.System<CryoSleepSystem>();
        var ghosts = entities.System<GhostSystem>();
        var minds = entities.System<MindSystem>();
        var testMap = await pair.CreateTestMap();
        var slot = preferences.GetPreferences(session.UserId).SelectedCharacterIndex;
        var profileId = await realDb.GetCharacterIdAsync(session.UserId, slot);
        Assert.That(profileId, Is.Not.Null);
        var selectedProfile = (HumanoidCharacterProfile) preferences.GetPreferences(session.UserId).Characters[slot];
        var speciesPrototypeId = prototypes.Index<SpeciesPrototype>(selectedProfile.Species).Prototype.Id;
        var playableAuthority = await EnsurePlayableAuthorityAsync(
            realDb, session.UserId, profileId!.Value, slot);

        EntityUid body = default;
        EntityUid pod = default;
        EntityUid mindId = default;
        EntityUid priorGhost = default;
        LuaMDeepCryoPayload payload = default;
        await server.WaitAssertion(() =>
        {
            pod = entities.SpawnEntity("LuaMDeepCryoTestSleepPod", testMap.GridCoords);
            var profile = (HumanoidCharacterProfile) preferences.GetPreferences(session.UserId).Characters[slot];
            var species = prototypes.Index<SpeciesPrototype>(profile.Species);
            body = entities.SpawnEntity(species.Prototype.Id, testMap.GridCoords);
            var identity = entities.EnsureComponent<LuaMDeepCryoIdentityComponent>(body);
            BindPlayableTestIdentity(identity, session.UserId, profileId!.Value, slot, playableAuthority);
            Assert.That(containers.Insert(body, entities.GetComponent<CryoSleepComponent>(pod).BodyContainer), Is.True);
            Assert.That(cryo.TryCapturePayload(body, out payload, out var reason), Is.True, reason);

            var attached = AttachSessionToBody(entities, players, minds, session, body,
                nameof(FrontierStoreCommitOrConflictNeverRestoresReturnableVisit));
            mindId = attached.Id;
            var mind = attached.Component;
            priorGhost = ghosts.SpawnGhost((mindId, mind), body, canReturn: true) ?? EntityUid.Invalid;
            Assert.That(priorGhost.Valid, Is.True);
        });

        LuaMDeepCryoWriteResult? seeded = null;
        if (authoritativeConflict)
        {
            var storePrecondition = await realDb.GetLuaMDeepCryoStorePreconditionAsync(
                session.UserId,
                profileId!.Value,
                slot);
            Assert.That(storePrecondition, Is.Not.Null);
            Assert.That(storePrecondition!.ActiveSnapshot, Is.Null);
            seeded = await realDb.StoreLuaMDeepCryoSnapshotAsync(new LuaMDeepCryoStoreRequest(
                Guid.NewGuid(), session.UserId, profileId!.Value, slot, 1,
                LuaMDeepCryoPersistenceSystem.PayloadFormatVersion,
                payload.Bytes, payload.Hash, payload.Bytes.Length, payload.EntityCount,
                "integration-test", payload.PrototypeManifestHash, DateTime.UtcNow,
                storePrecondition!.LifecycleRevision,
                playableAuthority.LeaseId));
            Assert.That(seeded.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
        }

        var proxy = DispatchProxy.Create<IServerDbManager, PausedBeforeCryoStoreProxy>();
        var proxyState = (PausedBeforeCryoStoreProxy) (object) proxy;
        proxyState.Inner = realDb;
        SetPrivateField(cryo, "_db", proxy);
        bool JudgeExpectedConflict(string sawmillName, LogEvent message)
        {
            return authoritativeConflict &&
                   message.RenderMessage().Contains("observed a durable newer lifecycle", StringComparison.Ordinal);
        }

        pair.ServerLogHandler.JudgeLog += JudgeExpectedConflict;
        try
        {
            await server.WaitAssertion(() => sleep.CryoStoreBody(body, pod));
            await DrainServerTaskAsync(pair, proxyState.StoreStarted.Task);
            await server.WaitAssertion(() =>
            {
                Assert.That(AssertStoreControlFenced(entities, session, mindId, body), Is.EqualTo(priorGhost));
            });

            proxyState.ReleaseStore();
            await DrainServerTaskAsync(pair, proxyState.StoreReturned.Task);
            await WaitForServerConditionAsync(
                pair,
                () => authoritativeConflict
                    ? !entities.EntityExists(body)
                    : entities.EntityExists(body) &&
                      entities.HasComponent<LuaMDeepCryoStoredComponent>(body) &&
                      !cryo.IsStorePending(body),
                authoritativeConflict
                    ? "The conflicting live Frontier body was not disposed fail-closed."
                    : "The committed Frontier body was not finalized.");

            await server.WaitAssertion(() =>
            {
                var mind = entities.GetComponent<MindComponent>(mindId);
                Assert.Multiple(() =>
                {
                    Assert.That(mind.CurrentEntity, Is.Not.EqualTo(body));
                    Assert.That(session.AttachedEntity, Is.Not.EqualTo(body));
                    Assert.That(mind.CurrentEntity, Is.EqualTo(priorGhost));
                    Assert.That(session.AttachedEntity, Is.EqualTo(priorGhost));
                    Assert.That(entities.GetComponent<GhostComponent>(priorGhost).CanReturnToBody, Is.False,
                        "Commit and authoritative conflict are points of no return for the old live body.");
                });
            });

            await RequestGhostReturnAsync(pair);
            await server.WaitAssertion(() =>
            {
                Assert.That(entities.GetComponent<MindComponent>(mindId).CurrentEntity, Is.EqualTo(priorGhost));
                Assert.That(session.AttachedEntity, Is.EqualTo(priorGhost));
            });

            var snapshot = await realDb.GetLuaMDeepCryoSnapshotAsync(session.UserId, profileId.Value, slot);
            Assert.That(snapshot, Is.Not.Null);
            Assert.That(snapshot!.Status, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Stored));
            if (authoritativeConflict)
                Assert.That(snapshot.Id, Is.EqualTo(seeded!.SnapshotId));
        }
        finally
        {
            proxyState.ReleaseStore();
            pair.ServerLogHandler.JudgeLog -= JudgeExpectedConflict;
            SetPrivateField(cryo, "_db", realDb);
        }

        await pair.CleanReturnAsync();
    }

    [TestCase(LuaMDeepCryoWriteStatus.InvalidRequest)]
    [TestCase(LuaMDeepCryoWriteStatus.LifecycleConflict)]
    public async Task ProoflessStoreFailureWithAbsentAuthorityReadRetainsSoleBodySuspended(
        LuaMDeepCryoWriteStatus forcedStatus)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);
        var players = server.ResolveDependency<IPlayerManager>();
        var session = players.GetSessionById(clientSession!.UserId);
        var entities = server.ResolveDependency<IEntityManager>();
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var realDb = server.ResolveDependency<IServerDbManager>();
        var containers = entities.System<ContainerSystem>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();
        var testMap = await pair.CreateTestMap();
        var slot = preferences.GetPreferences(session.UserId).SelectedCharacterIndex;
        var profileId = await realDb.GetCharacterIdAsync(session.UserId, slot);
        Assert.That(profileId, Is.Not.Null);
        var playableAuthority = await EnsurePlayableAuthorityAsync(
            realDb, session.UserId, profileId!.Value, slot);
        var canonicalProfile = (HumanoidCharacterProfile) preferences.GetPreferences(session.UserId).Characters[slot];
        var speciesPrototypeId = prototypes.Index<SpeciesPrototype>(canonicalProfile.Species).Prototype.Id;

        EntityUid body = default;
        EntityUid pod = default;
        await server.WaitAssertion(() =>
        {
            pod = entities.SpawnEntity("LuaMDeepCryoTestSleepPod", testMap.GridCoords);
            body = entities.SpawnEntity(speciesPrototypeId, testMap.GridCoords);
            BindPlayableTestIdentity(
                entities.EnsureComponent<LuaMDeepCryoIdentityComponent>(body),
                session.UserId,
                profileId!.Value,
                slot,
                playableAuthority);
            Assert.That(containers.Insert(
                    body,
                    entities.GetComponent<CryoSleepComponent>(pod).BodyContainer),
                Is.True);
        });

        var proxy = DispatchProxy.Create<IServerDbManager, PausedBeforeCryoStoreProxy>();
        var proxyState = (PausedBeforeCryoStoreProxy) (object) proxy;
        proxyState.Inner = realDb;
        proxyState.ForcedStatus = forcedStatus;
        proxyState.ReturnNullPresencePrecondition = true;
        SetPrivateField(cryo, "_db", proxy);
        var callbackCount = 0;
        try
        {
            await server.WaitAssertion(() => Assert.That(cryo.TryBeginStore(
                body,
                pod,
                session.UserId,
                LuaMDeepCryoSource.FrontierCryoSleep,
                _ => Interlocked.Increment(ref callbackCount),
                out var reason), Is.True, reason));
            await DrainServerTaskAsync(pair, proxyState.StoreStarted.Task);
            proxyState.ReleaseStore();
            await DrainServerTaskAsync(pair, proxyState.StoreReturned.Task);
            await pair.RunTicksSync(2);

            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(entities.EntityExists(body), Is.True,
                        "A bare null authority read must never delete the sole body.");
                    Assert.That(cryo.IsStorePending(body), Is.True);
                    Assert.That(cryo.IsPresenceSuspended(body), Is.True);
                    Assert.That(entities.GetComponent<TransformComponent>(body).MapID, Is.EqualTo(MapId.Nullspace));
                    Assert.That(callbackCount, Is.Zero);
                });
            });

            proxyState.ReturnNullPresencePrecondition = false;
            proxyState.ForcedStatus = null;
            proxyState.ReturnExpiringPresencePrecondition = true;
            await WaitForServerConditionAsync(
                pair,
                () => !cryo.IsStorePending(body) && Volatile.Read(ref callbackCount) == 1,
                "The retained no-commit Store did not recover after exact authority became readable.");
        }
        finally
        {
            proxyState.ReturnNullPresencePrecondition = false;
            proxyState.ReturnExpiringPresencePrecondition = false;
            proxyState.ReleaseStore();
            SetPrivateField(cryo, "_db", realDb);
        }

        await pair.CleanReturnAsync();
    }
    [Test]
    public async Task UpstreamPendingStoreRevokesReconnectAndProvenFailureRestoresControl()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);
        var players = server.ResolveDependency<IPlayerManager>();
        var session = players.GetSessionById(clientSession!.UserId);
        var entities = server.ResolveDependency<IEntityManager>();
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var realDb = server.ResolveDependency<IServerDbManager>();
        var containers = entities.System<ContainerSystem>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();
        var upstream = entities.System<CryostorageSystem>();
        var minds = entities.System<MindSystem>();
        var testMap = await pair.CreateTestMap();
        var slot = preferences.GetPreferences(session.UserId).SelectedCharacterIndex;
        var profileId = await realDb.GetCharacterIdAsync(session.UserId, slot);
        Assert.That(profileId, Is.Not.Null);
        var playableAuthority = await EnsurePlayableAuthorityAsync(
            realDb, session.UserId, profileId!.Value, slot);
        var canonicalProfile = (HumanoidCharacterProfile) preferences.GetPreferences(session.UserId).Characters[slot];
        var speciesPrototypeId = prototypes.Index<SpeciesPrototype>(canonicalProfile.Species).Prototype.Id;

        EntityUid body = default;
        EntityUid pod = default;
        EntityUid mindId = default;
        CryostorageContainedComponent contained = default!;
        await server.WaitAssertion(() =>
        {
            pod = entities.SpawnEntity("CryogenicSleepUnit", testMap.GridCoords);
            body = entities.SpawnEntity(speciesPrototypeId, testMap.GridCoords);
            var identity = entities.EnsureComponent<LuaMDeepCryoIdentityComponent>(body);
            BindPlayableTestIdentity(identity, session.UserId, profileId!.Value, slot, playableAuthority);
            (mindId, _) = AttachSessionToBody(entities, players, minds, session, body,
                nameof(UpstreamPendingStoreRevokesReconnectAndProvenFailureRestoresControl));

            var cryostorage = entities.GetComponent<CryostorageComponent>(pod);
            Assert.That(containers.Insert(body, containers.GetContainer(pod, cryostorage.ContainerId)), Is.True);
            contained = entities.GetComponent<CryostorageContainedComponent>(body);
            contained.Cryostorage = pod;
            contained.UserId = session.UserId;
        });

        var proxy = DispatchProxy.Create<IServerDbManager, PausedBeforeCryoStoreProxy>();
        var proxyState = (PausedBeforeCryoStoreProxy) (object) proxy;
        proxyState.Inner = realDb;
        proxyState.ForcedStatus = LuaMDeepCryoWriteStatus.InvalidRequest;
        proxyState.ReturnExpiringPresencePrecondition = true;
        SetPrivateField(cryo, "_db", proxy);
        var sawExpectedFailure = false;
        bool JudgeExpectedFailure(string sawmillName, LogEvent message)
        {
            if (!message.RenderMessage().Contains("database-store-InvalidRequest", StringComparison.Ordinal))
                return false;

            sawExpectedFailure = true;
            return true;
        }

        pair.ServerLogHandler.JudgeLog += JudgeExpectedFailure;
        try
        {
            await server.WaitAssertion(() => upstream.HandleEnterCryostorage((body, contained), session.UserId));
            await DrainServerTaskAsync(pair, proxyState.StoreStarted.Task);
            await server.WaitAssertion(() => AssertBodyControlRevoked(entities, session, mindId, body));

            await server.WaitAssertion(() =>
            {
                minds.ControlMob(session.UserId, body);
                Assert.That(entities.GetComponent<MindComponent>(mindId).CurrentEntity, Is.EqualTo(body),
                    "The reconnect race must first reproduce control being regained during Store.");
                Assert.That(session.AttachedEntity, Is.EqualTo(body));

                InvokeCryostorageReconnect(upstream, body, contained);
                AssertBodyControlRevoked(entities, session, mindId, body);
            });

            proxyState.ReleaseStore();
            await DrainServerTaskAsync(pair, proxyState.StoreReturned.Task);
            await WaitForServerConditionAsync(
                pair,
                () => !cryo.IsStorePending(body),
                "Upstream no-commit rollback did not release the Store fence.");

            await server.WaitAssertion(() =>
            {
                var mind = entities.GetComponent<MindComponent>(mindId);
                Assert.Multiple(() =>
                {
                    Assert.That(mind.OwnedEntity, Is.EqualTo(body));
                    Assert.That(mind.CurrentEntity, Is.EqualTo(body));
                    Assert.That(mind.VisitingEntity, Is.Null);
                    Assert.That(session.AttachedEntity, Is.EqualTo(body),
                        "Only a proven no-commit result may restore upstream body control.");
                    Assert.That(containers.GetContainer(pod, entities.GetComponent<CryostorageComponent>(pod).ContainerId)
                        .Contains(body), Is.True);
                });
            });

            Assert.That(await realDb.GetLuaMDeepCryoSnapshotAsync(session.UserId, profileId.Value, slot), Is.Null);
            Assert.That(sawExpectedFailure, Is.True);
        }
        finally
        {
            proxyState.ReleaseStore();
            pair.ServerLogHandler.JudgeLog -= JudgeExpectedFailure;
            SetPrivateField(cryo, "_db", realDb);
        }

        await pair.CleanReturnAsync();
    }
    [Test]
    public async Task UpstreamStoreFromReturnableVisitorCanonicalizesAndCanWakeDurably()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        await server.WaitPost(() => server.CfgMan.SetCVar(CCVars.GameCryoSleepRejoining, true));
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);
        var players = server.ResolveDependency<IPlayerManager>();
        var session = players.GetSessionById(clientSession!.UserId);
        var entities = server.ResolveDependency<IEntityManager>();
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var realDb = server.ResolveDependency<IServerDbManager>();
        var containers = entities.System<ContainerSystem>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();
        var upstream = entities.System<CryostorageSystem>();
        var ghosts = entities.System<GhostSystem>();
        var minds = entities.System<MindSystem>();
        var testMap = await pair.CreateTestMap();
        var slot = preferences.GetPreferences(session.UserId).SelectedCharacterIndex;
        var profileId = await realDb.GetCharacterIdAsync(session.UserId, slot);
        Assert.That(profileId, Is.Not.Null);
        var playableAuthority = await EnsurePlayableAuthorityAsync(
            realDb, session.UserId, profileId!.Value, slot);
        var canonicalProfile = (HumanoidCharacterProfile) preferences.GetPreferences(session.UserId).Characters[slot];
        var speciesPrototypeId = prototypes.Index<SpeciesPrototype>(canonicalProfile.Species).Prototype.Id;

        EntityUid body = default;
        EntityUid pod = default;
        EntityUid mindId = default;
        EntityUid visitor = default;
        CryostorageContainedComponent contained = default!;
        await server.WaitAssertion(() =>
        {
            pod = entities.SpawnEntity("CryogenicSleepUnit", testMap.GridCoords);
            body = entities.SpawnEntity(speciesPrototypeId, testMap.GridCoords);
            var identity = entities.EnsureComponent<LuaMDeepCryoIdentityComponent>(body);
            BindPlayableTestIdentity(identity, session.UserId, profileId!.Value, slot, playableAuthority);

            var attached = AttachSessionToBody(
                entities,
                players,
                minds,
                session,
                body,
                nameof(UpstreamStoreFromReturnableVisitorCanonicalizesAndCanWakeDurably));
            mindId = attached.Id;
            visitor = ghosts.SpawnGhost((mindId, attached.Component), body, canReturn: true) ?? EntityUid.Invalid;
            Assert.That(visitor.Valid, Is.True);
            Assert.That(entities.GetComponent<GhostComponent>(visitor).CanReturnToBody, Is.True,
                "The regression requires a pre-existing returnable visit before Store begins.");

            var cryostorage = entities.GetComponent<CryostorageComponent>(pod);
            Assert.That(containers.Insert(body, containers.GetContainer(pod, cryostorage.ContainerId)), Is.True);
            contained = entities.EnsureComponent<CryostorageContainedComponent>(body);
            contained.Cryostorage = pod;
            contained.UserId = session.UserId;
        });

        var proxy = DispatchProxy.Create<IServerDbManager, PausedBeforeCryoClaimProxy>();
        var proxyState = (PausedBeforeCryoClaimProxy) (object) proxy;
        proxyState.Inner = realDb;
        SetPrivateField(cryo, "_db", proxy);
        try
        {
            await server.WaitAssertion(() => upstream.HandleEnterCryostorage((body, contained), session.UserId));
            await WaitForServerConditionAsync(
                pair,
                () => entities.EntityExists(body) &&
                      entities.HasComponent<LuaMDeepCryoStoredComponent>(body) &&
                      !cryo.IsStorePending(body) &&
                      entities.GetComponent<TransformComponent>(body).ParentUid != pod,
                "Upstream Store did not reach its durable stored state.");

            await server.WaitAssertion(() =>
            {
                var mind = entities.GetComponent<MindComponent>(mindId);
                Assert.Multiple(() =>
                {
                    Assert.That(mind.OwnedEntity, Is.EqualTo(visitor),
                        "A successful Store must transfer durable ownership away from the stored body.");
                    Assert.That(mind.CurrentEntity, Is.EqualTo(visitor));
                    Assert.That(mind.VisitingEntity, Is.Null,
                        "The pre-existing visit must be canonicalized into ordinary ghost ownership.");
                    Assert.That(session.AttachedEntity, Is.EqualTo(visitor));
                    Assert.That(entities.GetComponent<GhostComponent>(visitor).CanReturnToBody, Is.False,
                        "A committed snapshot must never retain a return path to its old live body.");
                });

                if (!proxyState.ClaimStarted.Task.IsCompleted)
                    InvokeCryostorageReconnect(upstream, body, contained);
            });
            await DrainServerTaskAsync(pair, proxyState.ClaimStarted.Task);

            proxyState.ReleaseClaim();
            await WaitForServerConditionAsync(
                pair,
                () =>
                {
                    var mind = entities.GetComponent<MindComponent>(mindId);
                    return mind.OwnedEntity == body &&
                           mind.CurrentEntity == body &&
                           mind.VisitingEntity == null &&
                           session.AttachedEntity == body &&
                           !entities.HasComponent<LuaMDeepCryoRestoreReservationComponent>(pod);
                },
                "The canonicalized upstream ghost could not complete Claim/PREPARE/AUTH/ACK wake.");

            Assert.That(await realDb.GetLuaMDeepCryoSnapshotAsync(session.UserId, profileId.Value, slot), Is.Null,
                "A successful upstream wake must finish the exact ACK and remove durable snapshot authority.");
        }
        finally
        {
            proxyState.ReleaseClaim();
            SetPrivateField(cryo, "_db", realDb);
        }

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task UpstreamWakeKeepsControlFencedUntilExactAuthorization()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        await server.WaitPost(() => server.CfgMan.SetCVar(CCVars.GameCryoSleepRejoining, true));
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);
        var players = server.ResolveDependency<IPlayerManager>();
        var session = players.GetSessionById(clientSession!.UserId);
        var entities = server.ResolveDependency<IEntityManager>();
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var realDb = server.ResolveDependency<IServerDbManager>();
        var containers = entities.System<ContainerSystem>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();
        var upstream = entities.System<CryostorageSystem>();
        var ghosts = entities.System<GhostSystem>();
        var minds = entities.System<MindSystem>();
        var testMap = await pair.CreateTestMap();
        var slot = preferences.GetPreferences(session.UserId).SelectedCharacterIndex;
        var profileId = await realDb.GetCharacterIdAsync(session.UserId, slot);
        Assert.That(profileId, Is.Not.Null);
        var playableAuthority = await EnsurePlayableAuthorityAsync(
            realDb, session.UserId, profileId!.Value, slot);
        var canonicalProfile = (HumanoidCharacterProfile) preferences.GetPreferences(session.UserId).Characters[slot];
        var speciesPrototypeId = prototypes.Index<SpeciesPrototype>(canonicalProfile.Species).Prototype.Id;

        EntityUid body = default;
        EntityUid pod = default;
        EntityUid mindId = default;
        CryostorageContainedComponent contained = default!;
        await server.WaitAssertion(() =>
        {
            pod = entities.SpawnEntity("CryogenicSleepUnit", testMap.GridCoords);
            body = entities.SpawnEntity(speciesPrototypeId, testMap.GridCoords);
            var identity = entities.EnsureComponent<LuaMDeepCryoIdentityComponent>(body);
            BindPlayableTestIdentity(identity, session.UserId, profileId!.Value, slot, playableAuthority);
            var attached = AttachSessionToBody(
                entities,
                players,
                minds,
                session,
                body,
                nameof(UpstreamWakeKeepsControlFencedUntilExactAuthorization));
            mindId = attached.Id;
            Assert.That(ghosts.SpawnGhost((mindId, attached.Component), body, canReturn: true), Is.Not.Null,
                "The AUTH regression requires a reachable upstream stored-body control fence.");

            var cryostorage = entities.GetComponent<CryostorageComponent>(pod);
            Assert.That(containers.Insert(body, containers.GetContainer(pod, cryostorage.ContainerId)), Is.True);
            contained = entities.EnsureComponent<CryostorageContainedComponent>(body);
            contained.Cryostorage = pod;
            contained.UserId = session.UserId;
        });

        var proxy = DispatchProxy.Create<IServerDbManager, PausedBeforeCryoAuthorizationProxy>();
        var proxyState = (PausedBeforeCryoAuthorizationProxy) (object) proxy;
        proxyState.Inner = realDb;
        SetPrivateField(cryo, "_db", proxy);
        try
        {
            await server.WaitAssertion(() => upstream.HandleEnterCryostorage((body, contained), session.UserId));
            await WaitForServerConditionAsync(
                pair,
                () => entities.EntityExists(body) &&
                      entities.HasComponent<LuaMDeepCryoStoredComponent>(body) &&
                      !cryo.IsStorePending(body) &&
                      entities.GetComponent<TransformComponent>(body).ParentUid != pod,
                "Upstream Store did not reach its durable stored state before the wake regression.");

            await server.WaitAssertion(() =>
            {
                if (!proxyState.AuthorizationStarted.Task.IsCompleted)
                    InvokeCryostorageReconnect(upstream, body, contained);
            });
            await DrainServerTaskAsync(pair, proxyState.AuthorizationStarted.Task);

            var prepared = await realDb.GetLuaMDeepCryoSnapshotAsync(session.UserId, profileId.Value, slot);
            await server.WaitAssertion(() =>
            {
                var mind = entities.GetComponent<MindComponent>(mindId);
                Assert.Multiple(() =>
                {
                    Assert.That(mind.OwnedEntity, Is.Not.EqualTo(body));
                    Assert.That(mind.CurrentEntity, Is.Not.EqualTo(body),
                        "Mind control must remain behind the ghost fence while AUTH is paused.");
                    Assert.That(session.AttachedEntity, Is.Not.EqualTo(body),
                        "The session must not observe the stored body before exact AUTH.");
                    Assert.That(session.AttachedEntity, Is.EqualTo(mind.CurrentEntity));
                    Assert.That(prepared, Is.Not.Null);
                    Assert.That(prepared!.Status, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Consumed));
                    Assert.That(prepared.LeaseId, Is.Not.Null,
                        "PREPARE must retain its lease while publication authorization is pending.");
                });
            });

            proxyState.ReleaseAuthorization();
            var authorization = await proxyState.AuthorizationReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(authorization.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success),
                "The released AUTH call must return its exact durable proof.");

            await WaitForServerConditionAsync(
                pair,
                () =>
                {
                    var mind = entities.GetComponent<MindComponent>(mindId);
                    return mind.OwnedEntity == body &&
                           mind.CurrentEntity == body &&
                           session.AttachedEntity == body &&
                           !entities.HasComponent<LuaMDeepCryoRestoreReservationComponent>(pod);
                },
                "Upstream publication did not restore control after exact AUTH and ACK.");

            Assert.That(await realDb.GetLuaMDeepCryoSnapshotAsync(session.UserId, profileId.Value, slot), Is.Null,
                "The post-AUTH upstream wake must finish ACK and leave no active snapshot lease.");
        }
        finally
        {
            proxyState.ReleaseAuthorization();
            SetPrivateField(cryo, "_db", realDb);
        }

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task UpstreamAuthorizedWakeWithMissingExactContainerStaysNullspaceFencedAndQuarantinesPayload()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        await server.WaitPost(() => server.CfgMan.SetCVar(CCVars.GameCryoSleepRejoining, true));
        var session = server.ResolveDependency<IPlayerManager>()
            .GetSessionById(pair.Client.Session!.UserId);
        var players = server.ResolveDependency<IPlayerManager>();
        var entities = server.ResolveDependency<IEntityManager>();
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var realDb = server.ResolveDependency<IServerDbManager>();
        var containers = entities.System<ContainerSystem>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();
        var upstream = entities.System<CryostorageSystem>();
        var ghosts = entities.System<GhostSystem>();
        var minds = entities.System<MindSystem>();
        var testMap = await pair.CreateTestMap();
        var slot = preferences.GetPreferences(session.UserId).SelectedCharacterIndex;
        var profileId = await realDb.GetCharacterIdAsync(session.UserId, slot);
        Assert.That(profileId, Is.Not.Null);
        var playableAuthority = await EnsurePlayableAuthorityAsync(
            realDb, session.UserId, profileId!.Value, slot);
        var canonicalProfile = (HumanoidCharacterProfile) preferences.GetPreferences(session.UserId).Characters[slot];
        var speciesPrototypeId = prototypes.Index<SpeciesPrototype>(canonicalProfile.Species).Prototype.Id;

        EntityUid body = default;
        EntityUid pod = default;
        EntityUid mindId = default;
        CryostorageContainedComponent contained = default!;
        await server.WaitAssertion(() =>
        {
            pod = entities.SpawnEntity("CryogenicSleepUnit", testMap.GridCoords);
            body = entities.SpawnEntity(speciesPrototypeId, testMap.GridCoords);
            BindPlayableTestIdentity(
                entities.EnsureComponent<LuaMDeepCryoIdentityComponent>(body),
                session.UserId,
                profileId.Value,
                slot,
                playableAuthority);
            var attached = AttachSessionToBody(
                entities,
                players,
                minds,
                session,
                body,
                nameof(UpstreamAuthorizedWakeWithMissingExactContainerStaysNullspaceFencedAndQuarantinesPayload));
            mindId = attached.Id;
            Assert.That(ghosts.SpawnGhost((mindId, attached.Component), body, canReturn: true), Is.Not.Null);

            var storage = entities.GetComponent<CryostorageComponent>(pod);
            Assert.That(containers.Insert(body, containers.GetContainer(pod, storage.ContainerId)), Is.True);
            contained = entities.EnsureComponent<CryostorageContainedComponent>(body);
            contained.Cryostorage = pod;
            contained.UserId = session.UserId;
        });

        await server.WaitAssertion(() => upstream.HandleEnterCryostorage((body, contained), session.UserId));
        await WaitForServerConditionAsync(
            pair,
            () => entities.EntityExists(body) &&
                  entities.HasComponent<LuaMDeepCryoStoredComponent>(body) &&
                  !cryo.IsStorePending(body) &&
                  entities.GetComponent<TransformComponent>(body).ParentUid != pod,
            "Upstream Store did not reach its durable state before the containment regression.");
        var storedPayload = await realDb.GetLuaMDeepCryoSnapshotAsync(session.UserId, profileId.Value, slot);
        Assert.That(storedPayload, Is.Not.Null);

        var proxy = DispatchProxy.Create<IServerDbManager, PausedAuthorizedQuarantineProxy>();
        var proxyState = (PausedAuthorizedQuarantineProxy) (object) proxy;
        proxyState.Inner = realDb;
        SetPrivateField(cryo, "_db", proxy);
        try
        {
            await server.WaitAssertion(() =>
            {
                if (!proxyState.AuthorizationCommitted.Task.IsCompleted)
                    InvokeCryostorageReconnect(upstream, body, contained);
            });
            await DrainServerTaskAsync(pair, proxyState.AuthorizationCommitted.Task);
            await server.WaitAssertion(() =>
                entities.GetComponent<CryostorageComponent>(pod).ContainerId = "missing_exact_wake_container");
            proxyState.ReleaseAuthorizationResult();
            await DrainServerTaskAsync(pair, proxyState.QuarantineStarted.Task);

            await server.WaitAssertion(() =>
            {
                var mind = entities.GetComponent<MindComponent>(mindId);
                Assert.Multiple(() =>
                {
                    Assert.That(entities.EntityExists(body), Is.True);
                    Assert.That(entities.GetComponent<TransformComponent>(body).MapID, Is.EqualTo(MapId.Nullspace),
                        "Failed exact insertion after AUTH must immediately move the body out of the live map.");
                    Assert.That(containers.TryGetContainingContainer((body, null, null), out _), Is.False,
                        "The failed AUTH body must not remain in an unrelated or unverified container.");
                    Assert.That(cryo.IsPresenceSuspended(body), Is.True);
                    Assert.That(mind.CurrentEntity, Is.Not.EqualTo(body));
                    Assert.That(session.AttachedEntity, Is.Not.EqualTo(body));
                });
            });

            proxyState.ReleaseQuarantine();
            await DrainServerTaskAsync(pair, proxyState.QuarantineReturned.Task);
            await WaitForServerConditionAsync(
                pair,
                () => !entities.EntityExists(body),
                "Authorized containment failure did not dispose the exact fenced body after quarantine.");

            var quarantined = await realDb.GetLuaMDeepCryoSnapshotAsync(session.UserId, profileId.Value, slot);
            Assert.Multiple(() =>
            {
                Assert.That(quarantined, Is.Not.Null);
                Assert.That(quarantined!.Status, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Quarantined));
                Assert.That(quarantined.PayloadHash, Is.EqualTo(storedPayload!.PayloadHash));
                Assert.That(quarantined.Payload, Is.EqualTo(storedPayload.Payload));
                Assert.That(session.AttachedEntity, Is.Not.EqualTo(body));
            });
        }
        finally
        {
            proxyState.ReleaseAuthorizationResult();
            proxyState.ReleaseQuarantine();
            SetPrivateField(cryo, "_db", realDb);
        }

        await pair.CleanReturnAsync();
    }
    [Test]
    public async Task StaleExpiryCannotRemoveNewStorageEpisodeForSameBody()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var sleep = entities.System<CryoSleepSystem>();

        await server.WaitAssertion(() =>
        {
            var userId = new NetUserId(Guid.NewGuid());
            var body = entities.SpawnEntity("LuaMDeepCryoTestBody", MapCoordinates.Nullspace);
            var pod = entities.SpawnEntity("LuaMDeepCryoTestSleepPod", MapCoordinates.Nullspace);
            var track = typeof(CryoSleepSystem).GetMethod(
                "TrackStoredBody",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(track, Is.Not.Null);

            track!.Invoke(sleep, new object[] { userId, body, pod, 10L, 1L });
            track.Invoke(sleep, new object[] { userId, body, pod, 11L, 2L });

            sleep.ResetCryosleepState(userId, body, 10L, 1L);
            Assert.Multiple(() =>
            {
                Assert.That(sleep.HasCryosleepingBody(userId), Is.True,
                    "The old timer must not remove a later store episode of the same entity UID.");
                Assert.That(entities.EntityExists(body), Is.True);
            });

            sleep.ResetCryosleepState(userId, body, 11L, 2L);
            Assert.That(sleep.HasCryosleepingBody(userId), Is.False,
                "The exact current storage episode must still be removable.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task StoreReplayKeepsCallbackFenceAndActiveSnapshotFailsClosed()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);
        var players = server.ResolveDependency<IPlayerManager>();
        var session = players.GetSessionById(clientSession!.UserId);
        var entities = server.ResolveDependency<IEntityManager>();
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var realDb = server.ResolveDependency<IServerDbManager>();
        var containers = entities.System<ContainerSystem>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();
        var slot = preferences.GetPreferences(session.UserId).SelectedCharacterIndex;
        var profileId = await realDb.GetCharacterIdAsync(session.UserId, slot);
        Assert.That(profileId, Is.Not.Null);
        var playableAuthority = await EnsurePlayableAuthorityAsync(
            realDb, session.UserId, profileId!.Value, slot);
        var canonicalProfile = (HumanoidCharacterProfile) preferences.GetPreferences(session.UserId).Characters[slot];
        var speciesPrototypeId = prototypes.Index<SpeciesPrototype>(canonicalProfile.Species).Prototype.Id;

        EntityUid body = default;
        EntityUid pod = default;
        EntityUid reentrantBody = default;
        EntityUid reentrantPod = default;
        await server.WaitAssertion(() =>
        {
            pod = entities.SpawnEntity("LuaMDeepCryoTestSleepPod", MapCoordinates.Nullspace);
            body = entities.SpawnEntity(speciesPrototypeId, MapCoordinates.Nullspace);
            var identity = entities.EnsureComponent<LuaMDeepCryoIdentityComponent>(body);
            BindPlayableTestIdentity(identity, session.UserId, profileId!.Value, slot, playableAuthority);
            Assert.That(containers.Insert(body, entities.GetComponent<CryoSleepComponent>(pod).BodyContainer), Is.True);

            reentrantPod = entities.SpawnEntity("LuaMDeepCryoTestSleepPod", MapCoordinates.Nullspace);
            reentrantBody = entities.SpawnEntity(speciesPrototypeId, MapCoordinates.Nullspace);
            var reentrantIdentity = entities.EnsureComponent<LuaMDeepCryoIdentityComponent>(reentrantBody);
            BindPlayableTestIdentity(
                reentrantIdentity, session.UserId, profileId.Value, slot, playableAuthority);
            Assert.That(containers.Insert(
                reentrantBody,
                entities.GetComponent<CryoSleepComponent>(reentrantPod).BodyContainer), Is.True);
        });

        var proxy = DispatchProxy.Create<IServerDbManager, CommitThenThrowCryoStoreProxy>();
        var proxyState = (CommitThenThrowCryoStoreProxy) (object) proxy;
        proxyState.Inner = realDb;
        SetPrivateField(cryo, "_db", proxy);
        var completion = new TaskCompletionSource<LuaMDeepCryoStoreCompletion>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool? reentrantStoreAccepted = null;
        string? reentrantStoreReason = null;
        PausedBeforeCryoStoreProxy? conflictProxyState = null;
        bool JudgeExpectedStoreReplayFailure(string sawmillName, LogEvent message)
        {
            var rendered = message.RenderMessage();
            return rendered.Contains("replaying its exact operation proof", StringComparison.Ordinal) &&
                   rendered.Contains("simulated Store commit-then-throw", StringComparison.Ordinal) ||
                   rendered.Contains("observed a durable newer lifecycle; deleting the stale fenced body", StringComparison.Ordinal);
        }

        pair.ServerLogHandler.JudgeLog += JudgeExpectedStoreReplayFailure;
        try
        {
            await server.WaitAssertion(() =>
            {
                Assert.That(cryo.TryBeginStore(
                    body,
                    pod,
                    session.UserId,
                    LuaMDeepCryoSource.FrontierCryoSleep,
                    value =>
                    {
                        reentrantStoreAccepted = cryo.TryBeginStore(
                            reentrantBody,
                            reentrantPod,
                            session.UserId,
                            LuaMDeepCryoSource.FrontierCryoSleep,
                            _ => { },
                            out var callbackReason);
                        reentrantStoreReason = callbackReason;
                        completion.TrySetResult(value);
                    },
                    out var reason), Is.True, reason);
            });
            await DrainServerTaskAsync(pair, completion.Task);
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var snapshot = await realDb.GetLuaMDeepCryoSnapshotAsync(session.UserId, profileId.Value, slot);
            Assert.Multiple(() =>
            {
                Assert.That(result.Success, Is.True);
                Assert.That(proxyState.StoreCalls, Is.EqualTo(2));
                Assert.That(snapshot, Is.Not.Null);
                Assert.That(snapshot!.Status, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Stored));
                Assert.That(reentrantStoreAccepted, Is.False,
                    "The CharacterKey fence must remain held through the success callback.");
                Assert.That(reentrantStoreReason, Is.EqualTo("character-lifecycle-operation-pending"));
            });
            await server.WaitAssertion(() =>
            {
                Assert.That(entities.HasComponent<LuaMDeepCryoPendingComponent>(body), Is.False);
                Assert.That(entities.HasComponent<LuaMDeepCryoStoredComponent>(body), Is.True);
            });

            var conflictProxy = DispatchProxy.Create<IServerDbManager, PausedBeforeCryoStoreProxy>();
            conflictProxyState = (PausedBeforeCryoStoreProxy) (object) conflictProxy;
            conflictProxyState.Inner = realDb;
            SetPrivateField(cryo, "_db", conflictProxy);
            var conflictingCallbackCount = 0;
            await server.WaitAssertion(() =>
            {
                Assert.That(cryo.TryBeginStore(
                    reentrantBody,
                    reentrantPod,
                    session.UserId,
                    LuaMDeepCryoSource.FrontierCryoSleep,
                    _ => Interlocked.Increment(ref conflictingCallbackCount),
                    out var reason), Is.True, reason);
            });
            await DrainServerTaskAsync(pair, conflictProxyState.StoreStarted.Task);
            conflictProxyState.ReleaseStore();
            await DrainServerTaskAsync(pair, conflictProxyState.StoreReturned.Task);
            await pair.RunTicksSync(2);

            var retainedSnapshot = await realDb.GetLuaMDeepCryoSnapshotAsync(
                session.UserId,
                profileId.Value,
                slot);
            Assert.Multiple(() =>
            {
                Assert.That(retainedSnapshot?.Id, Is.EqualTo(snapshot!.Id));
                Assert.That(retainedSnapshot?.Status, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Stored));
                Assert.That(conflictingCallbackCount, Is.Zero,
                    "An active durable snapshot must suppress a failure callback that could expose a duplicate body.");
            });
            await server.WaitAssertion(() =>
            {
                Assert.That(entities.EntityExists(reentrantBody), Is.False,
                    "The conflicting live body must fail closed when another snapshot owns authority.");
            });
        }
        finally
        {
            conflictProxyState?.ReleaseStore();
            SetPrivateField(cryo, "_db", realDb);
            pair.ServerLogHandler.JudgeLog -= JudgeExpectedStoreReplayFailure;
        }

        await pair.CleanReturnAsync();
    }
    [Test]
    public async Task BodyEpochRejectsStoreCapturedBeforeCompetingWinnerAcknowledgement()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);
        var players = server.ResolveDependency<IPlayerManager>();
        var session = players.GetSessionById(clientSession!.UserId);
        var entities = server.ResolveDependency<IEntityManager>();
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var realDb = server.ResolveDependency<IServerDbManager>();
        var containers = entities.System<ContainerSystem>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();
        var slot = preferences.GetPreferences(session.UserId).SelectedCharacterIndex;
        var profileId = await realDb.GetCharacterIdAsync(session.UserId, slot);
        Assert.That(profileId, Is.Not.Null);
        var playableAuthority = await EnsurePlayableAuthorityAsync(
            realDb, session.UserId, profileId!.Value, slot);
        var canonicalProfile = (HumanoidCharacterProfile) preferences.GetPreferences(session.UserId).Characters[slot];
        var speciesPrototypeId = prototypes.Index<SpeciesPrototype>(canonicalProfile.Species).Prototype.Id;
        var initialPrecondition = await realDb.GetLuaMDeepCryoStorePreconditionAsync(
            session.UserId,
            profileId!.Value,
            slot);
        Assert.That(initialPrecondition, Is.Not.Null);
        Assert.That(initialPrecondition!.ActiveSnapshot, Is.Null);

        EntityUid body = default;
        EntityUid pod = default;
        await server.WaitAssertion(() =>
        {
            pod = entities.SpawnEntity("LuaMDeepCryoTestSleepPod", MapCoordinates.Nullspace);
            var profile = (HumanoidCharacterProfile) preferences.GetPreferences(session.UserId).Characters[slot];
            var species = prototypes.Index<SpeciesPrototype>(profile.Species);
            body = entities.SpawnEntity(species.Prototype.Id, MapCoordinates.Nullspace);
            var identity = entities.EnsureComponent<LuaMDeepCryoIdentityComponent>(body);
            BindPlayableTestIdentity(identity, session.UserId, profileId.Value, slot, playableAuthority);
            Assert.That(containers.Insert(
                    body,
                    entities.GetComponent<CryoSleepComponent>(pod).BodyContainer),
                Is.True);
        });

        var proxy = DispatchProxy.Create<IServerDbManager, CompetingAcknowledgedCryoStoreProxy>();
        var proxyState = (CompetingAcknowledgedCryoStoreProxy) (object) proxy;
        proxyState.Inner = realDb;
        SetPrivateField(cryo, "_db", proxy);
        var callbackCount = 0;

        bool JudgeExpectedConcurrentStoreConflict(string sawmillName, LogEvent message)
        {
            var rendered = message.RenderMessage();
            return rendered.Contains("proved an authoritative lifecycle conflict", StringComparison.Ordinal);
        }

        pair.ServerLogHandler.JudgeLog += JudgeExpectedConcurrentStoreConflict;
        try
        {
            await server.WaitAssertion(() =>
            {
                Assert.That(cryo.TryBeginStore(
                    body,
                    pod,
                    session.UserId,
                    LuaMDeepCryoSource.FrontierCryoSleep,
                    _ => Interlocked.Increment(ref callbackCount),
                    out var reason), Is.True, reason);
            });

            await DrainServerTaskAsync(pair, proxyState.WinnerAcknowledged.Task);
            await WaitForServerConditionAsync(
                pair,
                () => !entities.EntityExists(body),
                "The losing live body was not neutralized after the concurrent Store winner was proven.");

            var snapshot = await realDb.GetLuaMDeepCryoSnapshotAsync(session.UserId, profileId.Value, slot);
            var finalPrecondition = await realDb.GetLuaMDeepCryoStorePreconditionAsync(
                session.UserId,
                profileId.Value,
                slot);
            Task<bool>? hasStored = null;
            await server.WaitPost(() => hasStored = cryo.HasStoredSnapshotAsync(session.UserId));
            await DrainServerTaskAsync(pair, hasStored!);
            var hasStoredResult = await hasStored!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Multiple(() =>
            {
                Assert.That(proxyState.StoreCalls, Is.EqualTo(1),
                    "The immutable stale epoch must be rejected without rebasing or replaying a new Store lifecycle.");
                Assert.That(proxyState.WinnerResult, Is.Not.Null);
                Assert.That(proxyState.WinnerResult!.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
                Assert.That(proxyState.LoserResult, Is.Not.Null);
                Assert.That(proxyState.LoserResult!.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.LifecycleConflict));
                Assert.That(proxyState.LoserResult.SnapshotId, Is.EqualTo(proxyState.WinnerResult.SnapshotId));
                Assert.That(proxyState.LoserResult.SnapshotStatus, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Consumed));
                Assert.That(proxyState.LoserResult.LeaseId, Is.Not.Null,
                    "ACK rotates the restore lease into playable authority, so stale Store evidence must retain its exact lease proof.");
                Assert.That(proxyState.LoserResult.Authority?.Phase,
                    Is.EqualTo(DbLuaMCharacterPresencePhase.Playable));
                Assert.That(proxyState.LoserResult.Snapshot?.Status,
                    Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Consumed));
                Assert.That(snapshot, Is.Null,
                    "The competing winner completed ACK, so only immutable historical evidence should remain.");
                Assert.That(finalPrecondition, Is.Not.Null);
                Assert.That(finalPrecondition!.ActiveSnapshot, Is.Null);
                Assert.That(finalPrecondition.LifecycleRevision,
                    Is.EqualTo(initialPrecondition.LifecycleRevision + 5),
                    "Winner Store plus Claim/PREP/AUTH/ACK must be the only five lifecycle increments.");
                Assert.That(hasStoredResult, Is.False,
                    "The losing Store fence must release after its stale body is synchronously neutralized.");
                Assert.That(callbackCount, Is.Zero,
                    "A stale losing Store must never invoke the callback that can return its body to the world.");
            });
        }
        finally
        {
            pair.ServerLogHandler.JudgeLog -= JudgeExpectedConcurrentStoreConflict;
            SetPrivateField(cryo, "_db", realDb);
        }

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ThrowingSuccessfulStoreCallbackDeletesBodyBeforeCharacterKeyCanBeReacquired()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);
        var players = server.ResolveDependency<IPlayerManager>();
        var session = players.GetSessionById(clientSession!.UserId);
        var entities = server.ResolveDependency<IEntityManager>();
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var realDb = server.ResolveDependency<IServerDbManager>();
        var containers = entities.System<ContainerSystem>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();
        var slot = preferences.GetPreferences(session.UserId).SelectedCharacterIndex;
        var profileId = await realDb.GetCharacterIdAsync(session.UserId, slot);
        Assert.That(profileId, Is.Not.Null);
        var playableAuthority = await EnsurePlayableAuthorityAsync(
            realDb, session.UserId, profileId!.Value, slot);
        var canonicalProfile = (HumanoidCharacterProfile) preferences.GetPreferences(session.UserId).Characters[slot];
        var speciesPrototypeId = prototypes.Index<SpeciesPrototype>(canonicalProfile.Species).Prototype.Id;

        EntityUid body = default;
        EntityUid pod = default;
        EntityUid replacementBody = default;
        EntityUid replacementPod = default;
        await server.WaitAssertion(() =>
        {
            pod = entities.SpawnEntity("LuaMDeepCryoTestSleepPod", MapCoordinates.Nullspace);
            body = entities.SpawnEntity(speciesPrototypeId, MapCoordinates.Nullspace);
            var identity = entities.EnsureComponent<LuaMDeepCryoIdentityComponent>(body);
            BindPlayableTestIdentity(identity, session.UserId, profileId!.Value, slot, playableAuthority);
            Assert.That(containers.Insert(body, entities.GetComponent<CryoSleepComponent>(pod).BodyContainer), Is.True);

            replacementPod = entities.SpawnEntity("LuaMDeepCryoTestSleepPod", MapCoordinates.Nullspace);
            replacementBody = entities.SpawnEntity(speciesPrototypeId, MapCoordinates.Nullspace);
            var replacementIdentity = entities.EnsureComponent<LuaMDeepCryoIdentityComponent>(replacementBody);
            BindPlayableTestIdentity(
                replacementIdentity, session.UserId, profileId.Value, slot, playableAuthority);
            Assert.That(containers.Insert(
                replacementBody,
                entities.GetComponent<CryoSleepComponent>(replacementPod).BodyContainer), Is.True);
        });

        var callbackEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementCallbackCount = 0;
        bool JudgeExpectedFailClosedStore(string sawmillName, LogEvent message)
        {
            var rendered = message.RenderMessage();
            return rendered.Contains("post-store callback failed", StringComparison.Ordinal) ||
                   rendered.Contains("proved an authoritative lifecycle conflict", StringComparison.Ordinal) ||
                   rendered.Contains("observed a durable newer lifecycle; deleting the stale fenced body", StringComparison.Ordinal);
        }

        pair.ServerLogHandler.JudgeLog += JudgeExpectedFailClosedStore;
        try
        {
            await server.WaitAssertion(() =>
            {
                Assert.That(cryo.TryBeginStore(
                    body,
                    pod,
                    session.UserId,
                    LuaMDeepCryoSource.FrontierCryoSleep,
                    _ =>
                    {
                        callbackEntered.TrySetResult(true);
                        throw new InvalidOperationException("simulated post-Store world finalization failure");
                    },
                    out var reason), Is.True, reason);
            });
            await DrainServerTaskAsync(pair, callbackEntered.Task);

            var reacquired = false;
            var oldBodyExistedWhenReacquired = true;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!reacquired && DateTime.UtcNow < deadline)
            {
                await server.WaitAssertion(() =>
                {
                    var oldBodyExists = entities.EntityExists(body);
                    reacquired = cryo.TryBeginStore(
                        replacementBody,
                        replacementPod,
                        session.UserId,
                        LuaMDeepCryoSource.FrontierCryoSleep,
                        _ => Interlocked.Increment(ref replacementCallbackCount),
                        out var reason);
                    if (reacquired)
                    {
                        oldBodyExistedWhenReacquired = oldBodyExists;
                    }
                    else
                    {
                        Assert.That(reason, Is.EqualTo("character-lifecycle-operation-pending"));
                    }
                });

                if (!reacquired)
                {
                    await server.WaitIdleAsync();
                    await pair.RunTicksSync(1);
                    await Task.Yield();
                }
            }

            Assert.Multiple(() =>
            {
                Assert.That(reacquired, Is.True,
                    "The exact CharacterKey must be released after fail-closed body deletion is proven.");
                Assert.That(oldBodyExistedWhenReacquired, Is.False,
                    "The CharacterKey must not become available while the callback-failed live body still exists.");
            });

            await WaitForServerConditionAsync(
                pair,
                () => !entities.EntityExists(replacementBody),
                "The reentrant duplicate body did not fail closed against the retained snapshot.");
            var snapshot = await realDb.GetLuaMDeepCryoSnapshotAsync(session.UserId, profileId.Value, slot);
            Assert.Multiple(() =>
            {
                Assert.That(entities.EntityExists(body), Is.False);
                Assert.That(snapshot, Is.Not.Null);
                Assert.That(snapshot!.Status, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Stored));
                Assert.That(replacementCallbackCount, Is.Zero,
                    "The retained Stored snapshot must suppress rollback publication of the reentrant body.");
            });
        }
        finally
        {
            pair.ServerLogHandler.JudgeLog -= JudgeExpectedFailClosedStore;
        }

        await pair.CleanReturnAsync();
    }
    [Test]
    public async Task ThrowingNoCommitCallbackRetainsSoleBodyAndCharacterFence()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);
        var players = server.ResolveDependency<IPlayerManager>();
        var session = players.GetSessionById(clientSession!.UserId);
        var entities = server.ResolveDependency<IEntityManager>();
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var realDb = server.ResolveDependency<IServerDbManager>();
        var containers = entities.System<ContainerSystem>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();
        var selected = preferences.GetPreferences(session.UserId);
        var slot = selected.SelectedCharacterIndex;
        var profile = (HumanoidCharacterProfile) selected.SelectedCharacter;
        var profileId = await realDb.GetCharacterIdAsync(session.UserId, slot);
        Assert.That(profileId, Is.Not.Null);
        var playableAuthority = await EnsurePlayableAuthorityAsync(
            realDb, session.UserId, profileId!.Value, slot);
        var speciesPrototypeId = prototypes.Index<SpeciesPrototype>(profile.Species).Prototype.Id;
        var precondition = await realDb.GetLuaMDeepCryoStorePreconditionAsync(
            session.UserId,
            profileId!.Value,
            slot);
        Assert.That(precondition, Is.Not.Null);
        Assert.That(precondition!.ActiveSnapshot, Is.Null);

        EntityUid body = default;
        EntityUid pod = default;
        EntityUid secondBody = default;
        EntityUid secondPod = default;
        await server.WaitAssertion(() =>
        {
            pod = entities.SpawnEntity("LuaMDeepCryoTestSleepPod", MapCoordinates.Nullspace);
            body = entities.SpawnEntity(speciesPrototypeId, MapCoordinates.Nullspace);
            var identity = entities.EnsureComponent<LuaMDeepCryoIdentityComponent>(body);
            BindPlayableTestIdentity(identity, session.UserId, profileId.Value, slot, playableAuthority);
            Assert.That(containers.Insert(
                    body,
                    entities.GetComponent<CryoSleepComponent>(pod).BodyContainer),
                Is.True);

            secondPod = entities.SpawnEntity("LuaMDeepCryoTestSleepPod", MapCoordinates.Nullspace);
            secondBody = entities.SpawnEntity(speciesPrototypeId, MapCoordinates.Nullspace);
            var secondIdentity = entities.EnsureComponent<LuaMDeepCryoIdentityComponent>(secondBody);
            BindPlayableTestIdentity(secondIdentity, session.UserId, profileId.Value, slot, playableAuthority);
            Assert.That(containers.Insert(
                    secondBody,
                    entities.GetComponent<CryoSleepComponent>(secondPod).BodyContainer),
                Is.True);
        });

        var proxy = DispatchProxy.Create<IServerDbManager, PausedBeforeCryoStoreProxy>();
        var proxyState = (PausedBeforeCryoStoreProxy) (object) proxy;
        proxyState.Inner = realDb;
        proxyState.ForcedStatus = LuaMDeepCryoWriteStatus.InvalidRequest;
        SetPrivateField(cryo, "_db", proxy);
        var callbackCount = 0;
        var callbackEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        bool JudgeExpectedNoCommitCallbackFailure(string sawmillName, LogEvent message)
            => message.RenderMessage().Contains("no-commit rollback callback failed", StringComparison.Ordinal);

        pair.ServerLogHandler.JudgeLog += JudgeExpectedNoCommitCallbackFailure;
        try
        {
            await server.WaitAssertion(() =>
            {
                Assert.That(cryo.TryBeginStore(
                    body,
                    pod,
                    session.UserId,
                    LuaMDeepCryoSource.FrontierCryoSleep,
                    _ =>
                    {
                        Interlocked.Increment(ref callbackCount);
                        callbackEntered.TrySetResult(true);
                        throw new InvalidOperationException("simulated proven-no-commit control rollback failure");
                    },
                    out var reason), Is.True, reason);
            });
            await DrainServerTaskAsync(pair, proxyState.StoreStarted.Task);
            proxyState.ReleaseStore();
            await DrainServerTaskAsync(pair, callbackEntered.Task);
            await pair.RunTicksSync(2);

            var snapshot = await realDb.GetLuaMDeepCryoSnapshotAsync(session.UserId, profileId.Value, slot);
            var beforeSpawn = new PlayerBeforeSpawnEvent(
                session,
                profile,
                jobId: null,
                lateJoin: true,
                station: EntityUid.Invalid);
            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(entities.EntityExists(body), Is.True,
                        "A callback failure without durable authority must not delete the sole character body.");
                    Assert.That(cryo.IsStorePending(body), Is.True,
                        "The exact body fence must be restored after the one-shot callback throws.");
                    Assert.That(cryo.IsPresenceSuspended(body), Is.True,
                        "A throwing rollback callback must immediately leave the body fail-closed.");
                    Assert.That(callbackCount, Is.EqualTo(1),
                        "The partially executed rollback callback must never be invoked a second time.");
                    Assert.That(snapshot, Is.Null);
                });

                Assert.That(cryo.TryBeginStore(
                    secondBody,
                    secondPod,
                    session.UserId,
                    LuaMDeepCryoSource.FrontierCryoSleep,
                    _ => { },
                    out var secondReason), Is.False);
                Assert.That(secondReason, Is.EqualTo("character-lifecycle-operation-pending"));

                InvokePrivateEventHandler(cryo, "OnPlayerBeforeSpawn", beforeSpawn);
                Assert.That(beforeSpawn.Handled, Is.True,
                    "Fresh spawn must remain blocked while a failed no-commit rollback owns the character fence.");
            });
        }
        finally
        {
            pair.ServerLogHandler.JudgeLog -= JudgeExpectedNoCommitCallbackFailure;
            proxyState.ReleaseStore();
            SetPrivateField(cryo, "_db", realDb);
        }

        await pair.CleanReturnAsync();
    }
    [Test]
    public async Task NoCommitCallbackDeletingBodyReplaysCapturedStoreWithoutSecondCallback()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        var session = server.ResolveDependency<IPlayerManager>()
            .GetSessionById(pair.Client.Session!.UserId);
        var entities = server.ResolveDependency<IEntityManager>();
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var realDb = server.ResolveDependency<IServerDbManager>();
        var containers = entities.System<ContainerSystem>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();
        var testMap = await pair.CreateTestMap();
        var slot = preferences.GetPreferences(session.UserId).SelectedCharacterIndex;
        var profileId = await realDb.GetCharacterIdAsync(session.UserId, slot);
        Assert.That(profileId, Is.Not.Null);
        var authority = await EnsurePlayableAuthorityAsync(
            realDb, session.UserId, profileId!.Value, slot);
        var profile = (HumanoidCharacterProfile) preferences.GetPreferences(session.UserId).Characters[slot];
        var speciesPrototypeId = prototypes.Index<SpeciesPrototype>(profile.Species).Prototype.Id;

        EntityUid body = default;
        EntityUid pod = default;
        await server.WaitAssertion(() =>
        {
            pod = entities.SpawnEntity("LuaMDeepCryoTestSleepPod", testMap.GridCoords);
            body = entities.SpawnEntity(speciesPrototypeId, testMap.GridCoords);
            BindPlayableTestIdentity(
                entities.EnsureComponent<LuaMDeepCryoIdentityComponent>(body),
                session.UserId,
                profileId!.Value,
                slot,
                authority);
            Assert.That(containers.Insert(
                    body,
                    entities.GetComponent<CryoSleepComponent>(pod).BodyContainer),
                Is.True);
        });

        var proxy = DispatchProxy.Create<IServerDbManager, PausedBeforeCryoStoreProxy>();
        var proxyState = (PausedBeforeCryoStoreProxy) (object) proxy;
        proxyState.Inner = realDb;
        proxyState.ForcedStatus = LuaMDeepCryoWriteStatus.InvalidRequest;
        proxyState.ReturnExpiringPresencePrecondition = true;
        SetPrivateField(cryo, "_db", proxy);
        var callbackCount = 0;
        var callbackEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool JudgeExpectedCallbackFailure(string sawmillName, LogEvent message)
            => message.RenderMessage().Contains("no-commit rollback callback failed", StringComparison.Ordinal);

        pair.ServerLogHandler.JudgeLog += JudgeExpectedCallbackFailure;
        try
        {
            await server.WaitAssertion(() => Assert.That(cryo.TryBeginStore(
                body,
                pod,
                session.UserId,
                LuaMDeepCryoSource.FrontierCryoSleep,
                _ =>
                {
                    Interlocked.Increment(ref callbackCount);
                    entities.DeleteEntity(body);
                    callbackEntered.TrySetResult(true);
                    throw new InvalidOperationException("simulated deletion before rollback failure");
                },
                out var reason), Is.True, reason));
            await DrainServerTaskAsync(pair, proxyState.StoreStarted.Task);
            proxyState.ReleaseStore();
            await DrainServerTaskAsync(pair, callbackEntered.Task);
            await server.WaitAssertion(() => Assert.That(entities.EntityExists(body), Is.False));

            proxyState.ForcedStatus = null;
            await DrainServerTaskAsync(pair, proxyState.SuccessfulStoreReturned.Task);
            await pair.RunTicksSync(2);

            var snapshot = await realDb.GetLuaMDeepCryoSnapshotAsync(session.UserId, profileId.Value, slot);
            var requests = proxyState.Requests;
            Assert.Multiple(() =>
            {
                Assert.That(callbackCount, Is.EqualTo(1));
                Assert.That(proxyState.StoreCalls, Is.GreaterThanOrEqualTo(2));
                Assert.That(proxyState.LastRequest, Is.SameAs(proxyState.FirstRequest),
                    "The missing-body settlement must replay the captured immutable Store request.");
                Assert.That(requests.Count, Is.GreaterThanOrEqualTo(2));
                Assert.That(requests.All(request => ReferenceEquals(request, proxyState.FirstRequest)), Is.True,
                    "Every missing-body retry must use the same captured Store request object.");
                Assert.That(snapshot?.Status, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Stored));
                Assert.That(snapshot?.PayloadHash, Is.EqualTo(proxyState.FirstRequest!.PayloadHash).IgnoreCase);
            });
        }
        finally
        {
            pair.ServerLogHandler.JudgeLog -= JudgeExpectedCallbackFailure;
            proxyState.ForcedStatus = null;
            proxyState.ReleaseStore();
            SetPrivateField(cryo, "_db", realDb);
        }

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RoundCleanupDetachesPendingStoreButKeepsCharacterFenceUntilExactResult()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);
        var players = server.ResolveDependency<IPlayerManager>();
        var session = players.GetSessionById(clientSession!.UserId);
        var entities = server.ResolveDependency<IEntityManager>();
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var realDb = server.ResolveDependency<IServerDbManager>();
        var containers = entities.System<ContainerSystem>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();
        var slot = preferences.GetPreferences(session.UserId).SelectedCharacterIndex;
        var profileId = await realDb.GetCharacterIdAsync(session.UserId, slot);
        Assert.That(profileId, Is.Not.Null);
        var canonicalProfile = (HumanoidCharacterProfile) preferences.GetPreferences(session.UserId).Characters[slot];
        var speciesPrototypeId = prototypes.Index<SpeciesPrototype>(canonicalProfile.Species).Prototype.Id;
        var playableAuthority = await EnsurePlayableAuthorityAsync(
            realDb, session.UserId, profileId!.Value, slot);

        EntityUid oldBody = default;
        EntityUid oldPod = default;
        await server.WaitAssertion(() =>
        {
            oldPod = entities.SpawnEntity("LuaMDeepCryoTestSleepPod", MapCoordinates.Nullspace);
            oldBody = entities.SpawnEntity(speciesPrototypeId, MapCoordinates.Nullspace);
            var identity = entities.EnsureComponent<LuaMDeepCryoIdentityComponent>(oldBody);
            BindPlayableTestIdentity(identity, session.UserId, profileId!.Value, slot, playableAuthority);
            Assert.That(containers.Insert(oldBody, entities.GetComponent<CryoSleepComponent>(oldPod).BodyContainer), Is.True);
        });

        var proxy = DispatchProxy.Create<IServerDbManager, PausedBeforeCryoStoreProxy>();
        var proxyState = (PausedBeforeCryoStoreProxy) (object) proxy;
        proxyState.Inner = realDb;
        SetPrivateField(cryo, "_db", proxy);
        var callbackCount = 0;
        try
        {
            await server.WaitAssertion(() =>
            {
                Assert.That(cryo.TryBeginStore(
                    oldBody,
                    oldPod,
                    session.UserId,
                    LuaMDeepCryoSource.FrontierCryoSleep,
                    _ => Interlocked.Increment(ref callbackCount),
                    out var reason), Is.True, reason);
            });
            await DrainServerTaskAsync(pair, proxyState.StoreStarted.Task);

            EntityUid replacementBody = default;
            await server.WaitAssertion(() =>
            {
                entities.EventBus.RaiseEvent(EventSource.Local, new RoundRestartCleanupEvent());
                var replacementPod = entities.SpawnEntity("LuaMDeepCryoTestSleepPod", MapCoordinates.Nullspace);
                replacementBody = entities.SpawnEntity(speciesPrototypeId, MapCoordinates.Nullspace);
                var identity = entities.EnsureComponent<LuaMDeepCryoIdentityComponent>(replacementBody);
                BindPlayableTestIdentity(identity, session.UserId, profileId!.Value, slot, playableAuthority);
                Assert.That(containers.Insert(
                    replacementBody,
                    entities.GetComponent<CryoSleepComponent>(replacementPod).BodyContainer), Is.True);
                Assert.That(cryo.TryBeginStore(
                    replacementBody,
                    replacementPod,
                    session.UserId,
                    LuaMDeepCryoSource.FrontierCryoSleep,
                    _ => { },
                    out var reason), Is.False);
                Assert.That(reason, Is.EqualTo("character-lifecycle-operation-pending"));
            });

            proxyState.ReleaseStore();
            await DrainServerTaskAsync(pair, proxyState.StoreReturned.Task);
            await pair.RunTicksSync(2);
            var snapshot = await realDb.GetLuaMDeepCryoSnapshotAsync(session.UserId, profileId.Value, slot);
            Assert.Multiple(() =>
            {
                Assert.That(snapshot, Is.Not.Null);
                Assert.That(snapshot!.Status, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Stored));
                Assert.That(callbackCount, Is.Zero,
                    "A detached old-round store must never invoke its stale world callback.");
            });
            await server.WaitAssertion(() =>
            {
                Assert.That(entities.EntityExists(oldBody), Is.False);
                Assert.That(entities.HasComponent<LuaMDeepCryoPendingComponent>(replacementBody), Is.False);
            });
        }
        finally
        {
            proxyState.ReleaseStore();
            SetPrivateField(cryo, "_db", realDb);
        }

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DetachedProvenNoCommitRetainsCapturedStoreUntilExactRecovery()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);
        var players = server.ResolveDependency<IPlayerManager>();
        var session = players.GetSessionById(clientSession!.UserId);
        var entities = server.ResolveDependency<IEntityManager>();
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var realDb = server.ResolveDependency<IServerDbManager>();
        var containers = entities.System<ContainerSystem>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();
        var slot = preferences.GetPreferences(session.UserId).SelectedCharacterIndex;
        var profileId = await realDb.GetCharacterIdAsync(session.UserId, slot);
        Assert.That(profileId, Is.Not.Null);
        var selectedProfile = (HumanoidCharacterProfile) preferences.GetPreferences(session.UserId).Characters[slot];
        var speciesPrototypeId = prototypes.Index<SpeciesPrototype>(selectedProfile.Species).Prototype.Id;
        var playableAuthority = await EnsurePlayableAuthorityAsync(
            realDb, session.UserId, profileId!.Value, slot);
        var initial = await realDb.GetLuaMDeepCryoStorePreconditionAsync(
            session.UserId,
            profileId!.Value,
            slot);
        Assert.That(initial, Is.Not.Null);
        Assert.That(initial!.ActiveSnapshot, Is.Null);
        var lifecycleRevision = initial!.LifecycleRevision;

        EntityUid body = default;
        EntityUid pod = default;
        EntityUid replacementBody = default;
        EntityUid replacementPod = default;
        await server.WaitAssertion(() =>
        {
            pod = entities.SpawnEntity("LuaMDeepCryoTestSleepPod", MapCoordinates.Nullspace);
            body = entities.SpawnEntity(speciesPrototypeId, MapCoordinates.Nullspace);
            var identity = entities.EnsureComponent<LuaMDeepCryoIdentityComponent>(body);
            BindPlayableTestIdentity(identity, session.UserId, profileId.Value, slot, playableAuthority);
            Assert.That(
                containers.Insert(body, entities.GetComponent<CryoSleepComponent>(pod).BodyContainer),
                Is.True);
        });

        var proxy = DispatchProxy.Create<IServerDbManager, PausedBeforeCryoStoreProxy>();
        var proxyState = (PausedBeforeCryoStoreProxy) (object) proxy;
        proxyState.Inner = realDb;
        proxyState.ForcedStatus = LuaMDeepCryoWriteStatus.InvalidRequest;
        SetPrivateField(cryo, "_db", proxy);
        var callbackCount = 0;
        try
        {
            await server.WaitAssertion(() =>
            {
                Assert.That(cryo.TryBeginStore(
                    body,
                    pod,
                    session.UserId,
                    LuaMDeepCryoSource.FrontierCryoSleep,
                    _ => Interlocked.Increment(ref callbackCount),
                    out var reason), Is.True, reason);
            });
            await DrainServerTaskAsync(pair, proxyState.StoreStarted.Task);

            await server.WaitAssertion(() =>
            {
                entities.EventBus.RaiseEvent(EventSource.Local, new RoundRestartCleanupEvent());
                replacementPod = entities.SpawnEntity("LuaMDeepCryoTestSleepPod", MapCoordinates.Nullspace);
                replacementBody = entities.SpawnEntity(speciesPrototypeId, MapCoordinates.Nullspace);
                var replacementIdentity = entities.EnsureComponent<LuaMDeepCryoIdentityComponent>(replacementBody);
                BindPlayableTestIdentity(
                    replacementIdentity, session.UserId, profileId.Value, slot, playableAuthority);
                Assert.That(
                    containers.Insert(
                        replacementBody,
                        entities.GetComponent<CryoSleepComponent>(replacementPod).BodyContainer),
                    Is.True);
                Assert.That(cryo.TryBeginStore(
                    replacementBody,
                    replacementPod,
                    session.UserId,
                    LuaMDeepCryoSource.FrontierCryoSleep,
                    _ => { },
                    out var replacementReason), Is.False);
                Assert.That(replacementReason, Is.EqualTo("character-lifecycle-operation-pending"),
                    "A detached no-commit result must retain the exact CharacterKey fence.");
            });

            proxyState.ReleaseStore();
            await DrainServerTaskAsync(pair, proxyState.StoreReturned.Task);
            await pair.RunTicksSync(2);
            Assert.Multiple(() =>
            {
                Assert.That(proxyState.StoreCalls, Is.EqualTo(1));
                Assert.That(callbackCount, Is.Zero,
                    "A detached old-round operation must never invoke its stale world callback.");
            });
            Assert.That(
                await realDb.GetLuaMDeepCryoSnapshotAsync(session.UserId, profileId.Value, slot),
                Is.Null,
                "The forced first result must be a proven no-commit, not a hidden database write.");

            proxyState.ForcedStatus = null;
            await server.WaitAssertion(() =>
                entities.EventBus.RaiseEvent(EventSource.Local, new RoundRestartCleanupEvent()));
            await DrainServerTaskAsync(pair, proxyState.SuccessfulStoreReturned.Task);
            await pair.RunTicksSync(2);

            var snapshot = await realDb.GetLuaMDeepCryoSnapshotAsync(session.UserId, profileId.Value, slot);
            var firstRequest = proxyState.FirstRequest;
            var recoveredRequest = proxyState.LastRequest;
            Assert.That(firstRequest, Is.Not.Null);
            Assert.That(snapshot, Is.Not.Null);
            var capturedRequest = firstRequest!;
            var recoveredSnapshot = snapshot!;
            Assert.Multiple(() =>
            {
                Assert.That(proxyState.StoreCalls, Is.EqualTo(2));
                Assert.That(recoveredRequest, Is.SameAs(firstRequest),
                    "Detached recovery must replay the immutable captured Store request without rebasing its payload or epoch.");
                Assert.That(capturedRequest.ExpectedLifecycleRevision, Is.EqualTo(lifecycleRevision));
                Assert.That(capturedRequest.UserId, Is.EqualTo(session.UserId));
                Assert.That(capturedRequest.ProfileId, Is.EqualTo(profileId.Value));
                Assert.That(capturedRequest.Slot, Is.EqualTo(slot));
                Assert.That(recoveredSnapshot.Status, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Stored));
                Assert.That(recoveredSnapshot.PayloadHash, Is.EqualTo(capturedRequest.PayloadHash).IgnoreCase);
                Assert.That(recoveredSnapshot.Payload, Is.EqualTo(capturedRequest.Payload),
                    "The durable recovery must contain the sole payload captured before round cleanup.");
                Assert.That(callbackCount, Is.Zero);
                Assert.That(entities.EntityExists(body), Is.False,
                    "The old-round body may disappear only while its immutable payload remains retained for recovery.");
            });
        }
        finally
        {
            proxyState.ForcedStatus = null;
            proxyState.ReleaseStore();
            SetPrivateField(cryo, "_db", realDb);
        }

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

            Assert.That(cryo.TryCapturePayload(body, out var payload, out var reason), Is.True, reason);
            Assert.Multiple(() =>
            {
                Assert.That(entities.HasComponent<DoAfterComponent>(body), Is.True,
                    "A later database rejection must leave the live body able to retry cryo.");
                Assert.That(entities.GetComponent<DoAfterComponent>(body).DoAfters, Is.Empty,
                    "Durable cryo must discard round-local do-after operations before serialization.");
                Assert.That(entities.HasComponent<ActiveDoAfterComponent>(body), Is.False,
                    "Durable cryo must discard the round-local active do-after marker too.");
                var yaml = Encoding.UTF8.GetString(payload.Bytes);
                Assert.That(yaml, Does.Not.Contain("doAfters:"),
                    "Transient do-after operations must remain outside the durable payload.");
                Assert.That(yaml, Does.Not.Contain("ActiveDoAfter"),
                    "The active do-after marker must remain outside the durable payload.");
                Assert.That(entities.GetComponent<TransformComponent>(body).ParentUid, Is.EqualTo(pod));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task NestedPrototypeDoAfterCapabilitySurvivesWakeAndSecondCapture()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var containers = entities.System<ContainerSystem>();
        var transforms = entities.System<SharedTransformSystem>();
        var sleeping = entities.System<SleepingSystem>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();

        await server.WaitAssertion(() =>
        {
            var pod = entities.SpawnEntity("LuaMDeepCryoTestPod", MapCoordinates.Nullspace);
            var body = entities.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
            var nested = entities.SpawnEntity("LuaMDeepCryoTestDoAfterItem", MapCoordinates.Nullspace);
            Assert.That(containers.Insert(body, containers.GetContainer(pod, "body")), Is.True);
            transforms.SetParent(nested, body);

            var transient = entities.GetComponent<DoAfterComponent>(nested);
            var args = new DoAfterArgs(
                entities,
                nested,
                TimeSpan.FromSeconds(30),
                new SharedCryoSleepSystem.CryoStoreDoAfterEvent(),
                pod,
                nested,
                pod);
            transient.DoAfters.Add(0, new Content.Shared.DoAfter.DoAfter(0, args, TimeSpan.Zero));
            entities.EnsureComponent<ActiveDoAfterComponent>(nested);

            Assert.That(cryo.TryCapturePayload(body, out var payload, out var captureReason),
                Is.True, captureReason);
            Assert.Multiple(() =>
            {
                Assert.That(entities.HasComponent<DoAfterComponent>(nested), Is.True,
                    "A failed durable store must leave a nested item able to retry its own actions.");
                Assert.That(entities.GetComponent<DoAfterComponent>(nested).DoAfters, Is.Empty);
                Assert.That(entities.HasComponent<ActiveDoAfterComponent>(nested), Is.False);
            });

            Assert.That(cryo.TryLoadPayload(Snapshot(payload), out var restored, out var loadReason),
                Is.True, loadReason);
            var bodyPrototype = prototypes.Index<EntityPrototype>("MobHuman");
            Assert.That(cryo.TryApplyCanonicalAuthority(
                    restored,
                    HumanoidCharacterProfile.DefaultWithSpecies("Human"),
                    bodyPrototype,
                    preserveLiveAttachment: false,
                    out var authorityReason),
                Is.True, authorityReason);

            var restoredNested = CollectGraph(entities, restored)
                .Single(uid => Prototype(entities, uid) == "LuaMDeepCryoTestDoAfterItem");
            Assert.Multiple(() =>
            {
                Assert.That(entities.HasComponent<DoAfterComponent>(restoredNested), Is.True,
                    "Wake canonicalization must restore prototype-owned DoAfter capability across the graph.");
                Assert.That(entities.GetComponent<DoAfterComponent>(restoredNested).DoAfters, Is.Empty);
                Assert.That(entities.HasComponent<ActiveDoAfterComponent>(restoredNested), Is.False);
            });

            Assert.That(sleeping.TryWaking(restored, force: true), Is.True);
            var retryPod = entities.SpawnEntity("LuaMDeepCryoTestPod", MapCoordinates.Nullspace);
            Assert.That(containers.Insert(restored, containers.GetContainer(retryPod, "body")), Is.True);

            var retryState = entities.GetComponent<DoAfterComponent>(restoredNested);
            var retryArgs = new DoAfterArgs(
                entities,
                restoredNested,
                TimeSpan.FromSeconds(30),
                new SharedCryoSleepSystem.CryoStoreDoAfterEvent(),
                retryPod,
                restoredNested,
                retryPod);
            retryState.DoAfters.Add(0, new Content.Shared.DoAfter.DoAfter(0, retryArgs, TimeSpan.Zero));
            entities.EnsureComponent<ActiveDoAfterComponent>(restoredNested);

            Assert.That(cryo.TryCapturePayload(restored, out var retryPayload, out var retryReason),
                Is.True, retryReason);
            Assert.Multiple(() =>
            {
                Assert.That(retryPayload.EntityCount, Is.EqualTo(payload.EntityCount));
                Assert.That(entities.HasComponent<DoAfterComponent>(restoredNested), Is.True);
                Assert.That(entities.GetComponent<DoAfterComponent>(restoredNested).DoAfters, Is.Empty);
                Assert.That(entities.HasComponent<ActiveDoAfterComponent>(restoredNested), Is.False);
            });
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
                Assert.That(entities.HasComponent<DoAfterComponent>(restored), Is.True,
                    "Canonical player capability must be restored after deep-cryo sanitization.");
                Assert.That(entities.HasComponent<ActiveDoAfterComponent>(restored), Is.False);
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
                false,
                0));
            Assert.That(cryo.IsRestorePublicationActive(key), Is.True,
                "A stale handle must not release another restore's fresh-spawn fence.");

            cryo.FinishRestorePublication(new LuaMDeepCryoRestoreHandle(
                key,
                1,
                1,
                lease,
                EntityUid.Invalid,
                false,
                0));
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

    [Test]
    public async Task RoundCleanupBeforeClaimCommitAbortsTheLateLeaseImmediately()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);

        var players = server.ResolveDependency<IPlayerManager>();
        var session = players.GetSessionById(clientSession!.UserId);
        var entities = server.ResolveDependency<IEntityManager>();
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        var realDb = server.ResolveDependency<IServerDbManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();

        var slot = -1;
        var payload = default(LuaMDeepCryoPayload);
        await server.WaitAssertion(() =>
        {
            slot = preferences.GetPreferences(session.UserId).SelectedCharacterIndex;
            var sourcePod = entities.SpawnEntity("LuaMDeepCryoTestPod", MapCoordinates.Nullspace);
            var profile = (HumanoidCharacterProfile) preferences.GetPreferences(session.UserId)
                .Characters[slot];
            var species = prototypes.Index<SpeciesPrototype>(profile.Species);
            var sourceBody = entities.SpawnEntity(species.Prototype.Id, MapCoordinates.Nullspace);
            Assert.That(
                entities.System<ContainerSystem>().Insert(
                    sourceBody,
                    entities.System<ContainerSystem>().GetContainer(sourcePod, "body")),
                Is.True);
            Assert.That(cryo.TryCapturePayload(sourceBody, out payload, out var reason), Is.True, reason);
            entities.DeleteEntity(sourcePod);
        });

        var profileId = await realDb.GetCharacterIdAsync(session.UserId, slot);
        Assert.That(profileId, Is.Not.Null);
        var playableAuthority = await EnsurePlayableAuthorityAsync(
            realDb, session.UserId, profileId!.Value, slot);
        var storePrecondition = await realDb.GetLuaMDeepCryoStorePreconditionAsync(
            session.UserId,
            profileId!.Value,
            slot);
        Assert.That(storePrecondition, Is.Not.Null);
        Assert.That(storePrecondition!.ActiveSnapshot, Is.Null);
        var stored = await realDb.StoreLuaMDeepCryoSnapshotAsync(new LuaMDeepCryoStoreRequest(
            Guid.NewGuid(), session.UserId, profileId!.Value, slot, 1,
            LuaMDeepCryoPersistenceSystem.PayloadFormatVersion,
            payload.Bytes, payload.Hash, payload.Bytes.Length, payload.EntityCount,
            "integration-test", payload.PrototypeManifestHash, DateTime.UtcNow,
            storePrecondition!.LifecycleRevision,
            playableAuthority.LeaseId));
        Assert.That(stored.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
        Assert.That(stored.Revision, Is.Not.Null);
        var storedRevision = stored.Revision!.Value;

        var proxy = DispatchProxy.Create<IServerDbManager, PausedBeforeCryoClaimProxy>();
        var proxyState = (PausedBeforeCryoClaimProxy) (object) proxy;
        proxyState.Inner = realDb;
        SetPrivateField(cryo, "_db", proxy);

        Task<LuaMDeepCryoClaimResult>? claimTask = null;
        try
        {
            await server.WaitPost(() => claimTask = cryo.ClaimRestoreAsync(session.UserId));
            await DrainServerTaskAsync(pair, proxyState.ClaimStarted.Task);

            await server.WaitPost(() =>
                entities.EventBus.RaiseEvent(EventSource.Local, new RoundRestartCleanupEvent()));
            await DrainServerTaskAsync(pair, proxyState.PreCommitStoredRead.Task);
            var preCommit = await proxyState.PreCommitStoredRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Multiple(() =>
            {
                Assert.That(preCommit.Status, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Stored));
                Assert.That(preCommit.Revision, Is.EqualTo(storedRevision));
                Assert.That(preCommit.LeaseId, Is.Null);
                Assert.That(proxyState.AbortCalls, Is.GreaterThan(0),
                    "Cleanup must exercise the failed pre-commit Abort before observing Stored/no-lease.");
            });

            proxyState.ReleaseClaim();
            await DrainServerTaskAsync(pair, claimTask!);
            var claim = await claimTask!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Multiple(() =>
            {
                Assert.That(claim.Status, Is.EqualTo(LuaMDeepCryoClaimStatus.Busy));
                Assert.That(claim.Handle, Is.Null,
                    "The old-round claimant must never receive publication authority after cleanup.");
            });

            LuaMDeepCryoSnapshotRecord? settled = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                await server.WaitIdleAsync();
                await pair.RunTicksSync(1);
                settled = await realDb.GetLuaMDeepCryoSnapshotAsync(session.UserId, profileId.Value, slot);
                if (settled is
                    {
                        Status: DbLuaMDeepCryoSnapshotStatus.Stored,
                        LeaseId: null,
                    } && settled.Revision == storedRevision + 2)
                {
                    break;
                }

                await Task.Yield();
            }

            Assert.Multiple(() =>
            {
                Assert.That(settled, Is.Not.Null);
                Assert.That(settled!.Status, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Stored));
                Assert.That(settled.LeaseId, Is.Null,
                    "A Claim that commits after cleanup must be exactly aborted, not left for lease expiry.");
                Assert.That(settled.Revision, Is.EqualTo(storedRevision + 2),
                    "The final revision must contain the late Claim and its immediate exact Abort.");
            });
            await server.WaitAssertion(() =>
                Assert.That(cryo.IsRestorePublicationActive(new LuaMDeepCryoPersistenceSystem.CharacterKey(
                    session.UserId, profileId.Value, slot)), Is.False));
        }
        finally
        {
            proxyState.ReleaseClaim();
            SetPrivateField(cryo, "_db", realDb);
        }

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RoundCleanupDuringPreparedPublicationReopensSnapshotAndAllowsRetry()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);

        var players = server.ResolveDependency<IPlayerManager>();
        var session = players.GetSessionById(clientSession!.UserId);
        var entities = server.ResolveDependency<IEntityManager>();
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        var realDb = server.ResolveDependency<IServerDbManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();
        var cryoSleep = entities.System<CryoSleepSystem>();
        var minds = entities.System<MindSystem>();
        var testMap = await pair.CreateTestMap();

        var slot = -1;
        var payload = default(LuaMDeepCryoPayload);
        MindComponent mind = default!;
        await server.WaitAssertion(() =>
        {
            slot = preferences.GetPreferences(session.UserId).SelectedCharacterIndex;
            var sourcePod = entities.SpawnEntity("LuaMDeepCryoTestPod", MapCoordinates.Nullspace);
            var profile = (HumanoidCharacterProfile) preferences.GetPreferences(session.UserId)
                .Characters[slot];
            var species = prototypes.Index<SpeciesPrototype>(profile.Species);
            var sourceBody = entities.SpawnEntity(species.Prototype.Id, MapCoordinates.Nullspace);
            Assert.That(
                entities.System<ContainerSystem>().Insert(
                    sourceBody,
                    entities.System<ContainerSystem>().GetContainer(sourcePod, "body")),
                Is.True);
            Assert.That(cryo.TryCapturePayload(sourceBody, out payload, out var reason), Is.True, reason);
            entities.DeleteEntity(sourcePod);

            mind = minds.TryGetMind(session.UserId, out _, out var existingMind)
                ? existingMind
                : minds.CreateMind(session.UserId, nameof(RoundCleanupDuringPreparedPublicationReopensSnapshotAndAllowsRetry)).Comp;
        });

        var profileId = await realDb.GetCharacterIdAsync(session.UserId, slot);
        Assert.That(profileId, Is.Not.Null);
        var playableAuthority = await EnsurePlayableAuthorityAsync(
            realDb, session.UserId, profileId!.Value, slot);
        var storePrecondition = await realDb.GetLuaMDeepCryoStorePreconditionAsync(
            session.UserId,
            profileId!.Value,
            slot);
        Assert.That(storePrecondition, Is.Not.Null);
        Assert.That(storePrecondition!.ActiveSnapshot, Is.Null);
        var stored = await realDb.StoreLuaMDeepCryoSnapshotAsync(new LuaMDeepCryoStoreRequest(
            Guid.NewGuid(), session.UserId, profileId!.Value, slot, 1,
            LuaMDeepCryoPersistenceSystem.PayloadFormatVersion,
            payload.Bytes, payload.Hash, payload.Bytes.Length, payload.EntityCount,
            "integration-test", payload.PrototypeManifestHash, DateTime.UtcNow,
            storePrecondition!.LifecycleRevision,
            playableAuthority.LeaseId));
        Assert.That(stored.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));

        EntityUid firstPod = default;
        await server.WaitPost(() =>
            firstPod = entities.SpawnEntity("LuaMDeepCryoTestSleepPod", testMap.GridCoords));

        var proxy = DispatchProxy.Create<IServerDbManager, PausedCryoCompleteProxy>();
        var proxyState = (PausedCryoCompleteProxy) (object) proxy;
        proxyState.Inner = realDb;
        SetPrivateField(cryo, "_db", proxy);
        bool JudgeExpectedDetachedPreparedCompletion(string sawmillName, LogEvent message)
        {
            return message.RenderMessage().Contains(
                "without its internally retained publication receipt",
                StringComparison.Ordinal);
        }
        pair.ServerLogHandler.JudgeLog += JudgeExpectedDetachedPreparedCompletion;

        Task<SharedCryoSleepSystem.ReturnToBodyStatus>? firstReturn = null;
        try
        {
            await server.WaitPost(() => firstReturn = cryoSleep.TryReturnToBody(mind, force: true));
            await DrainServerTaskAsync(pair, proxyState.CompletionCommitted.Task);
            await server.WaitAssertion(() =>
            {
                entities.EventBus.RaiseEvent(EventSource.Local, new RoundRestartCleanupEvent());
                Assert.That(cryo.IsRestorePublicationActive(new LuaMDeepCryoPersistenceSystem.CharacterKey(
                    session.UserId, profileId.Value, slot)), Is.False,
                    "Round cleanup must synchronously release the old generation's same-key in-memory fence.");
                entities.DeleteEntity(firstPod);
                proxyState.ReleaseCompletionResult();
            });
            await DrainServerTaskAsync(pair, firstReturn!);

            Assert.That(await firstReturn!.WaitAsync(TimeSpan.FromSeconds(5)),
                Is.EqualTo(SharedCryoSleepSystem.ReturnToBodyStatus.BodyMissing));
            var reopened = await realDb.GetLuaMDeepCryoSnapshotAsync(session.UserId, profileId.Value, slot);
            Assert.Multiple(() =>
            {
                Assert.That(reopened, Is.Not.Null);
                Assert.That(reopened!.Status, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Stored));
                Assert.That(reopened.LeaseId, Is.Null);
            });
            await server.WaitAssertion(() =>
                Assert.That(cryo.IsRestorePublicationActive(new LuaMDeepCryoPersistenceSystem.CharacterKey(
                    session.UserId, profileId.Value, slot)), Is.False));
        }
        finally
        {
            pair.ServerLogHandler.JudgeLog -= JudgeExpectedDetachedPreparedCompletion;
            proxyState.ReleaseCompletionResult();
            SetPrivateField(cryo, "_db", realDb);
        }

        await server.WaitAssertion(() =>
        {
            mind = minds.TryGetMind(session.UserId, out _, out var currentMind)
                ? currentMind
                : minds.CreateMind(
                    session.UserId,
                    $"{nameof(RoundCleanupDuringPreparedPublicationReopensSnapshotAndAllowsRetry)}-retry").Comp;
        });

        Task<SharedCryoSleepSystem.ReturnToBodyStatus>? retry = null;
        await server.WaitPost(() =>
        {
            entities.SpawnEntity("LuaMDeepCryoTestSleepPod", testMap.GridCoords);
            retry = cryoSleep.TryReturnToBody(mind, force: true);
        });
        await DrainServerTaskAsync(pair, retry!);
        Assert.That(await retry!.WaitAsync(TimeSpan.FromSeconds(5)),
            Is.EqualTo(SharedCryoSleepSystem.ReturnToBodyStatus.Success));
        Assert.That(await realDb.GetLuaMDeepCryoSnapshotAsync(session.UserId, profileId.Value, slot), Is.Null);

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DetachedFrontierSettlementCannotEraseNewerSameUserEpisode()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);

        var players = server.ResolveDependency<IPlayerManager>();
        var session = players.GetSessionById(clientSession!.UserId);
        var entities = server.ResolveDependency<IEntityManager>();
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var realDb = server.ResolveDependency<IServerDbManager>();
        var containers = entities.System<ContainerSystem>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();
        var cryoSleep = entities.System<CryoSleepSystem>();
        var minds = entities.System<MindSystem>();
        var testMap = await pair.CreateTestMap();
        var slot = preferences.GetPreferences(session.UserId).SelectedCharacterIndex;
        var profileId = await realDb.GetCharacterIdAsync(session.UserId, slot);
        Assert.That(profileId, Is.Not.Null);
        var playableAuthority = await EnsurePlayableAuthorityAsync(
            realDb, session.UserId, profileId!.Value, slot);

        EntityUid oldBody = default;
        EntityUid oldPod = default;
        EntityUid mindId = default;
        await server.WaitAssertion(() =>
        {
            oldPod = entities.SpawnEntity("LuaMDeepCryoTestSleepPod", testMap.GridCoords);
            var profile = (HumanoidCharacterProfile) preferences.GetPreferences(session.UserId).Characters[slot];
            var species = prototypes.Index<SpeciesPrototype>(profile.Species);
            oldBody = entities.SpawnEntity(species.Prototype.Id, testMap.GridCoords);
            var identity = entities.EnsureComponent<LuaMDeepCryoIdentityComponent>(oldBody);
            BindPlayableTestIdentity(identity, session.UserId, profileId!.Value, slot, playableAuthority);
            (mindId, _) = AttachSessionToBody(
                entities,
                players,
                minds,
                session,
                oldBody,
                nameof(DetachedFrontierSettlementCannotEraseNewerSameUserEpisode));
            Assert.That(containers.Insert(
                    oldBody,
                    entities.GetComponent<CryoSleepComponent>(oldPod).BodyContainer),
                Is.True);
            cryoSleep.CryoStoreBody(oldBody, oldPod);
        });

        await WaitForServerConditionAsync(
            pair,
            () => entities.EntityExists(oldBody) &&
                  entities.HasComponent<LuaMDeepCryoStoredComponent>(oldBody) &&
                  !cryo.IsStorePending(oldBody) &&
                  cryoSleep.HasCryosleepingBody(session.UserId),
            "The old Frontier episode did not finish its durable Store.");

        long oldSnapshotId = default;
        long oldStoredRevision = default;
        MindComponent mind = default!;
        await server.WaitAssertion(() =>
        {
            var stored = entities.GetComponent<LuaMDeepCryoStoredComponent>(oldBody);
            oldSnapshotId = stored.SnapshotId;
            oldStoredRevision = stored.Revision;
            mind = entities.GetComponent<MindComponent>(mindId);
            Assert.Multiple(() =>
            {
                Assert.That(mind.CurrentEntity, Is.Not.EqualTo(oldBody));
                Assert.That(session.AttachedEntity, Is.EqualTo(mind.CurrentEntity));
            });
        });

        var proxy = DispatchProxy.Create<IServerDbManager, PausedAuthorizedQuarantineProxy>();
        var proxyState = (PausedAuthorizedQuarantineProxy) (object) proxy;
        proxyState.Inner = realDb;
        SetPrivateField(cryo, "_db", proxy);
        bool JudgeExpectedDetachedAuthorizationFence(string sawmillName, LogEvent message)
        {
            return message.RenderMessage().Contains("publication authorization remains unresolved", StringComparison.Ordinal);
        }

        Task<SharedCryoSleepSystem.ReturnToBodyStatus>? oldReturn = null;
        EntityUid newBody = default;
        EntityUid newPod = default;
        var newSnapshotId = oldSnapshotId + 1000;
        var newRevision = oldStoredRevision + 1000;
        var newLease = Guid.NewGuid();
        pair.ServerLogHandler.JudgeLog += JudgeExpectedDetachedAuthorizationFence;
        try
        {
            await server.WaitPost(() => oldReturn = cryoSleep.TryReturnToBody(mind, force: true));
            await DrainServerTaskAsync(pair, proxyState.AuthorizationCommitted.Task);

            Guid oldLease = default;
            await server.WaitAssertion(() =>
            {
                Assert.That(entities.GetComponent<CryoSleepComponent>(oldPod).BodyContainer.ContainedEntity,
                    Is.EqualTo(oldBody));
                var reservation = entities.GetComponent<LuaMDeepCryoRestoreReservationComponent>(oldPod);
                oldLease = reservation.LeaseId;
                Assert.That(oldLease, Is.Not.EqualTo(Guid.Empty));

                entities.EventBus.RaiseEvent(EventSource.Local, new RoundRestartCleanupEvent());
            });
            await DrainServerTaskAsync(pair, proxyState.QuarantineStarted.Task);

            await server.WaitAssertion(() =>
            {
                Assert.That(cryoSleep.HasCryosleepingBody(session.UserId), Is.False,
                    "Round cleanup must first forget the old process-local episode.");
                newPod = entities.SpawnEntity("LuaMDeepCryoTestSleepPod", testMap.GridCoords);
                newBody = entities.SpawnEntity("MobHuman", testMap.GridCoords);
                InvokeTrackStoredBody(
                    cryoSleep,
                    session.UserId,
                    newBody,
                    newPod,
                    newSnapshotId,
                    newRevision);
                entities.EnsureComponent<LuaMDeepCryoRestoreReservationComponent>(newPod).LeaseId = newLease;
                Assert.That(cryoSleep.HasCryosleepingBody(session.UserId), Is.True);
            });

            proxyState.ReleaseQuarantine();
            await DrainServerTaskAsync(pair, proxyState.QuarantineReturned.Task);
            await DrainServerTaskAsync(pair, oldReturn!);
            Assert.That(await oldReturn!.WaitAsync(TimeSpan.FromSeconds(5)),
                Is.EqualTo(SharedCryoSleepSystem.ReturnToBodyStatus.BodyMissing));

            await WaitForServerConditionAsync(
                pair,
                () => !entities.EntityExists(oldBody) &&
                      !entities.HasComponent<LuaMDeepCryoRestoreReservationComponent>(oldPod),
                "Detached terminal cleanup did not remove the exact old Frontier body and reservation.");

            var terminal = await realDb.GetLuaMDeepCryoSnapshotAsync(session.UserId, profileId.Value, slot);
            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(terminal, Is.Not.Null);
                    Assert.That(terminal!.Status, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Quarantined));
                    Assert.That(terminal.LeaseId, Is.Null);
                    Assert.That(cryoSleep.HasCryosleepingBody(session.UserId), Is.True,
                        "The delayed old callback must not erase a newer same-user storage episode.");
                    Assert.That(entities.EntityExists(newBody), Is.True);
                    Assert.That(entities.GetComponent<LuaMDeepCryoRestoreReservationComponent>(newPod).LeaseId,
                        Is.EqualTo(newLease),
                        "The old lease cleanup must not clear a newer pod reservation.");
                    Assert.That(InvokeRemoveStoredBodyEpisode(
                            cryoSleep,
                            session.UserId,
                            newBody,
                            newPod,
                            newSnapshotId,
                            newRevision),
                        Is.True,
                        "The retained dictionary entry must be the exact newer episode, not stale old state.");
                });
            });
        }
        finally
        {
            proxyState.ReleaseQuarantine();
            proxyState.ReleaseAuthorizationResult();
            if (proxyState.AuthorizationCommitted.Task.IsCompleted)
                await DrainServerTaskAsync(pair, proxyState.AuthorizationReturned.Task);
            SetPrivateField(cryo, "_db", realDb);
        }

        await pair.CleanReturnAsync();
        pair.ServerLogHandler.JudgeLog -= JudgeExpectedDetachedAuthorizationFence;
    }

    [Test]
    public async Task RoundCleanupDuringPendingAcknowledgementUsesOnlyDetachedTerminalCleanup()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);

        var players = server.ResolveDependency<IPlayerManager>();
        var session = players.GetSessionById(clientSession!.UserId);
        var entities = server.ResolveDependency<IEntityManager>();
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var realDb = server.ResolveDependency<IServerDbManager>();
        var containers = entities.System<ContainerSystem>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();
        var testMap = await pair.CreateTestMap();

        var slot = -1;
        var payload = default(LuaMDeepCryoPayload);
        await server.WaitAssertion(() =>
        {
            slot = preferences.GetPreferences(session.UserId).SelectedCharacterIndex;
            var sourcePod = entities.SpawnEntity("LuaMDeepCryoTestPod", MapCoordinates.Nullspace);
            var profile = (HumanoidCharacterProfile) preferences.GetPreferences(session.UserId).Characters[slot];
            var species = prototypes.Index<SpeciesPrototype>(profile.Species);
            var sourceBody = entities.SpawnEntity(species.Prototype.Id, MapCoordinates.Nullspace);
            Assert.That(containers.Insert(sourceBody, containers.GetContainer(sourcePod, "body")), Is.True);
            Assert.That(cryo.TryCapturePayload(sourceBody, out payload, out var reason), Is.True, reason);
            entities.DeleteEntity(sourcePod);
        });

        var profileId = await realDb.GetCharacterIdAsync(session.UserId, slot);
        Assert.That(profileId, Is.Not.Null);
        var playableAuthority = await EnsurePlayableAuthorityAsync(
            realDb, session.UserId, profileId!.Value, slot);
        var storePrecondition = await realDb.GetLuaMDeepCryoStorePreconditionAsync(
            session.UserId,
            profileId!.Value,
            slot);
        Assert.That(storePrecondition, Is.Not.Null);
        Assert.That(storePrecondition!.ActiveSnapshot, Is.Null);
        var stored = await realDb.StoreLuaMDeepCryoSnapshotAsync(new LuaMDeepCryoStoreRequest(
            Guid.NewGuid(), session.UserId, profileId!.Value, slot, 1,
            LuaMDeepCryoPersistenceSystem.PayloadFormatVersion,
            payload.Bytes, payload.Hash, payload.Bytes.Length, payload.EntityCount,
            "integration-test", payload.PrototypeManifestHash, DateTime.UtcNow,
            storePrecondition!.LifecycleRevision,
            playableAuthority.LeaseId));
        Assert.That(stored.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));

        Task<LuaMDeepCryoClaimResult>? claimTask = null;
        await server.WaitPost(() => claimTask = cryo.ClaimRestoreAsync(session.UserId));
        await DrainServerTaskAsync(pair, claimTask!);
        var claim = await claimTask!.WaitAsync(TimeSpan.FromSeconds(5));
        if (claim.Handle == null)
        {
            var failedClaimSnapshot = await realDb.GetLuaMDeepCryoSnapshotAsync(
                session.UserId,
                profileId.Value,
                slot);
            Assert.Fail($"Claim failed with {claim.Status}; snapshot status={failedClaimSnapshot?.Status}, " +
                        $"quarantine reason={failedClaimSnapshot?.QuarantineReason}.");
        }
        Assert.That(claim.Handle, Is.Not.Null);
        var handle = claim.Handle!;

        EntityUid reservationPod = default;
        await server.WaitAssertion(() =>
        {
            reservationPod = entities.SpawnEntity("LuaMDeepCryoTestSleepPod", testMap.GridCoords);
            entities.EnsureComponent<LuaMDeepCryoRestoreReservationComponent>(reservationPod).LeaseId = handle.LeaseId;
        });

        var detachedCallbackCount = 0;
        var ordinaryAcknowledgementCallbackCount = 0;
        var matchingReservationCleared = 0;
        var detachedTerminal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void FinalizeDetachedTerminal()
        {
            Interlocked.Increment(ref detachedCallbackCount);
            if (entities.EntityExists(reservationPod) &&
                entities.TryGetComponent<LuaMDeepCryoRestoreReservationComponent>(reservationPod, out var reservation) &&
                reservation.LeaseId == handle.LeaseId)
            {
                entities.RemoveComponent<LuaMDeepCryoRestoreReservationComponent>(reservationPod);
                Interlocked.Exchange(ref matchingReservationCleared, 1);
            }

            if (cryo.IsExactRestoreBody(handle) && entities.EntityExists(handle.Body))
                entities.DeleteEntity(handle.Body);

            detachedTerminal.TrySetResult(true);
        }

        Task<LuaMDeepCryoConsumeResult>? consumeTask = null;
        await server.WaitPost(() =>
            consumeTask = cryo.ConsumeRestoreAsync(handle, FinalizeDetachedTerminal));
        await DrainServerTaskAsync(pair, consumeTask!);
        var consume = await consumeTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(consume.Status, Is.EqualTo(LuaMDeepCryoConsumeStatus.Success));
        Assert.That(consume.Receipt, Is.Not.Null);
        var receipt = consume.Receipt!;

        Task<LuaMDeepCryoAuthorizationStatus>? authorizationTask = null;
        await server.WaitPost(() =>
            authorizationTask = cryo.AuthorizeRestorePublicationAsync(receipt));
        await DrainServerTaskAsync(pair, authorizationTask!);
        Assert.That(await authorizationTask!.WaitAsync(TimeSpan.FromSeconds(5)),
            Is.EqualTo(LuaMDeepCryoAuthorizationStatus.Authorized));

        var proxy = DispatchProxy.Create<IServerDbManager, PausedCryoAcknowledgementProxy>();
        var proxyState = (PausedCryoAcknowledgementProxy) (object) proxy;
        proxyState.Inner = realDb;
        proxyState.PauseFirstPresenceRenewal = true;
        proxyState.FailAcknowledgedQuarantineAttempts = 4;
        SetPrivateField(cryo, "_db", proxy);
        bool JudgeExpectedDetachedAcknowledgementCompensation(string sawmillName, LogEvent message)
        {
            return message.RenderMessage().Contains("Exact deep-cryo ACK", StringComparison.Ordinal);
        }
        pair.ServerLogHandler.JudgeLog += JudgeExpectedDetachedAcknowledgementCompensation;

        Task<bool>? acknowledgementTask = null;
        try
        {
            await server.WaitPost(() =>
                acknowledgementTask = cryo.AcknowledgeRestorePublicationAsync(
                    receipt,
                    () => Interlocked.Increment(ref ordinaryAcknowledgementCallbackCount)));
            await DrainServerTaskAsync(pair, proxyState.AcknowledgementCommitted.Task);

            var acknowledgedAuthority = await realDb.GetLuaMCharacterPresenceAuthorityAsync(
                session.UserId,
                profileId.Value,
                slot);
            Assert.That(acknowledgedAuthority, Is.Not.Null);
            await server.WaitAssertion(() =>
            {
                BindPlayableTestIdentity(
                    entities.GetComponent<LuaMDeepCryoIdentityComponent>(handle.Body),
                    session.UserId,
                    profileId.Value,
                    slot,
                    acknowledgedAuthority! with { ExpiresAtUtc = DateTime.UtcNow.AddMinutes(1) });
            });
            await pair.RunTicksSync(2);
            await DrainServerTaskAsync(pair, proxyState.RenewalCommitted.Task);

            await server.WaitPost(() =>
                entities.EventBus.RaiseEvent(EventSource.Local, new RoundRestartCleanupEvent()));
            proxyState.ReleaseRenewalResult();
            await DrainServerTaskAsync(pair, proxyState.RenewalReturned.Task);
            await DrainServerTaskAsync(pair, detachedTerminal.Task);

            await WaitForServerConditionAsync(
                pair,
                () => !entities.EntityExists(handle.Body) &&
                      !entities.HasComponent<LuaMDeepCryoRestoreReservationComponent>(reservationPod),
                "Detached pending-ACK cleanup did not remove the exact old body and lease reservation.");

            Assert.Multiple(() =>
            {
                Assert.That(Volatile.Read(ref detachedCallbackCount), Is.EqualTo(1));
                Assert.That(Volatile.Read(ref ordinaryAcknowledgementCallbackCount), Is.Zero,
                    "A detached exact replay must never invoke the ordinary ACK publication callback.");
                Assert.That(Volatile.Read(ref matchingReservationCleared), Is.EqualTo(1));
                Assert.That(cryo.IsRestorePublicationActive(handle.Key), Is.False);
            });
            var quarantined = await realDb.GetLuaMDeepCryoSnapshotAsync(
                session.UserId,
                profileId.Value,
                slot);
            var authorityAfterCleanup = await realDb.GetLuaMCharacterPresenceAuthorityAsync(
                session.UserId,
                profileId.Value,
                slot);
            Assert.Multiple(() =>
            {
                Assert.That(quarantined, Is.Not.Null);
                Assert.That(quarantined!.Status, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Quarantined));
                Assert.That(quarantined.PayloadHash, Is.EqualTo(payload.Hash).IgnoreCase);
                Assert.That(quarantined.Payload, Is.EqualTo(payload.Bytes),
                    "Detached ACK compensation must preserve the exact sole payload in Quarantined state.");
                Assert.That(authorityAfterCleanup, Is.Null);
                Assert.That(proxyState.PresenceReleaseCalls, Is.Zero,
                    "Round cleanup must not race acknowledged quarantine with generic presence Release.");
                Assert.That(proxyState.PresenceRenewalCalls, Is.EqualTo(2),
                    "After proven no-commit Q failures, bodyless settlement must renew to a new exact revision before retrying Q.");
            });

            proxyState.ReleaseAcknowledgementResult();
            await DrainServerTaskAsync(pair, acknowledgementTask!);
            Assert.That(await acknowledgementTask!.WaitAsync(TimeSpan.FromSeconds(5)), Is.False,
                "The stale old-generation ACK caller must not regain current publication authority.");
            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(Volatile.Read(ref detachedCallbackCount), Is.EqualTo(1));
                    Assert.That(Volatile.Read(ref ordinaryAcknowledgementCallbackCount), Is.Zero);
                    Assert.That(entities.EntityExists(handle.Body), Is.False);
                    Assert.That(cryo.IsRestorePublicationActive(handle.Key), Is.False);
                });
            });
        }
        finally
        {
            pair.ServerLogHandler.JudgeLog -= JudgeExpectedDetachedAcknowledgementCompensation;
            proxyState.ReleaseAcknowledgementResult();
            proxyState.ReleaseRenewalResult();
            if (proxyState.AcknowledgementCommitted.Task.IsCompleted)
                await DrainServerTaskAsync(pair, proxyState.AcknowledgementReturned.Task);
            SetPrivateField(cryo, "_db", realDb);
        }

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AcknowledgedQuarantineAttemptIsOneWayAcrossHiddenCommitAndLocallyValidRetry()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        var session = server.ResolveDependency<IPlayerManager>()
            .GetSessionById(pair.Client.Session!.UserId);
        var entities = server.ResolveDependency<IEntityManager>();
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        var realDb = server.ResolveDependency<IServerDbManager>();
        var containers = entities.System<ContainerSystem>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var slot = preferences.GetPreferences(session.UserId).SelectedCharacterIndex;

        var payload = default(LuaMDeepCryoPayload);
        await server.WaitAssertion(() =>
        {
            var sourcePod = entities.SpawnEntity("LuaMDeepCryoTestPod", MapCoordinates.Nullspace);
            var profile = (HumanoidCharacterProfile) preferences.GetPreferences(session.UserId)
                .Characters[slot];
            var species = prototypes.Index<SpeciesPrototype>(profile.Species);
            var sourceBody = entities.SpawnEntity(species.Prototype.Id, MapCoordinates.Nullspace);
            Assert.That(containers.Insert(sourceBody, containers.GetContainer(sourcePod, "body")), Is.True);
            Assert.That(cryo.TryCapturePayload(sourceBody, out payload, out var reason), Is.True, reason);
            entities.DeleteEntity(sourcePod);
        });

        var profileId = await realDb.GetCharacterIdAsync(session.UserId, slot);
        Assert.That(profileId, Is.Not.Null);
        var playable = await EnsurePlayableAuthorityAsync(realDb, session.UserId, profileId!.Value, slot);
        var precondition = await realDb.GetLuaMDeepCryoStorePreconditionAsync(
            session.UserId,
            profileId.Value,
            slot);
        Assert.That(precondition, Is.Not.Null);
        var stored = await realDb.StoreLuaMDeepCryoSnapshotAsync(new LuaMDeepCryoStoreRequest(
            Guid.NewGuid(), session.UserId, profileId.Value, slot, 1,
            LuaMDeepCryoPersistenceSystem.PayloadFormatVersion,
            payload.Bytes, payload.Hash, payload.Bytes.Length, payload.EntityCount,
            "ack-q-one-way-test", payload.PrototypeManifestHash, DateTime.UtcNow,
            precondition!.LifecycleRevision,
            playable.LeaseId));
        Assert.That(stored.Success, Is.True);

        Task<LuaMDeepCryoClaimResult>? claimTask = null;
        await server.WaitPost(() => claimTask = cryo.ClaimRestoreAsync(session.UserId));
        await DrainServerTaskAsync(pair, claimTask!);
        var claim = await claimTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(claim.Status, Is.EqualTo(LuaMDeepCryoClaimStatus.Success));
        Assert.That(claim.Handle, Is.Not.Null);
        var handle = claim.Handle!;

        Task<LuaMDeepCryoConsumeResult>? consumeTask = null;
        await server.WaitPost(() => consumeTask = cryo.ConsumeRestoreAsync(handle));
        await DrainServerTaskAsync(pair, consumeTask!);
        var consume = await consumeTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(consume.Receipt, Is.Not.Null);
        var receipt = consume.Receipt!;

        Task<LuaMDeepCryoAuthorizationStatus>? authorizationTask = null;
        await server.WaitPost(() => authorizationTask = cryo.AuthorizeRestorePublicationAsync(receipt));
        await DrainServerTaskAsync(pair, authorizationTask!);
        Assert.That(await authorizationTask!.WaitAsync(TimeSpan.FromSeconds(5)),
            Is.EqualTo(LuaMDeepCryoAuthorizationStatus.Authorized));

        var proxy = DispatchProxy.Create<IServerDbManager, PausedCryoAcknowledgementProxy>();
        var proxyState = (PausedCryoAcknowledgementProxy) (object) proxy;
        proxyState.Inner = realDb;
        proxyState.CommitQuarantineThenHideFirstOutcome = true;
        SetPrivateField(cryo, "_db", proxy);
        var publicationCallbackCount = 0;
        bool JudgeExpectedHiddenQuarantine(string sawmillName, LogEvent message)
        {
            var rendered = message.RenderMessage();
            return rendered.Contains("acknowledged quarantine", StringComparison.Ordinal) ||
                   rendered.Contains("Exact deep-cryo ACK", StringComparison.Ordinal);
        }
        pair.ServerLogHandler.JudgeLog += JudgeExpectedHiddenQuarantine;
        try
        {
            Task<bool>? firstAcknowledgement = null;
            await server.WaitPost(() =>
                firstAcknowledgement = cryo.AcknowledgeRestorePublicationAsync(
                    receipt,
                    () => Interlocked.Increment(ref publicationCallbackCount)));
            await DrainServerTaskAsync(pair, proxyState.AcknowledgementCommitted.Task);
            await server.WaitAssertion(() =>
                entities.RemoveComponent<LuaMDeepCryoStoredComponent>(handle.Body));
            proxyState.ReleaseAcknowledgementResult();
            await DrainServerTaskAsync(pair, firstAcknowledgement!);
            Assert.That(await firstAcknowledgement!.WaitAsync(TimeSpan.FromSeconds(5)), Is.False);

            await server.WaitAssertion(() =>
            {
                var marker = entities.EnsureComponent<LuaMDeepCryoStoredComponent>(handle.Body);
                marker.UserId = handle.Key.UserId;
                marker.ProfileId = handle.Key.ProfileId;
                marker.Slot = handle.Key.Slot;
                marker.SnapshotId = handle.SnapshotId;
                marker.Revision = handle.Revision;
                Assert.That(cryo.IsExactRestoreBody(handle), Is.True,
                    "The retry target must become locally valid before the Q-only replay.");
            });

            Task<bool>? retryAcknowledgement = null;
            await server.WaitPost(() =>
                retryAcknowledgement = cryo.AcknowledgeRestorePublicationAsync(
                    receipt,
                    () => Interlocked.Increment(ref publicationCallbackCount)));
            await DrainServerTaskAsync(pair, retryAcknowledgement!);
            Assert.That(await retryAcknowledgement!.WaitAsync(TimeSpan.FromSeconds(5)), Is.False);
            await WaitForServerConditionAsync(
                pair,
                () => !entities.EntityExists(handle.Body) && !cryo.IsRestorePublicationActive(handle.Key),
                "A hidden acknowledged-Q commit was not resolved through the irreversible Q-only settlement.");

            var quarantined = await realDb.GetLuaMDeepCryoSnapshotAsync(
                session.UserId,
                profileId.Value,
                slot);
            Assert.Multiple(() =>
            {
                Assert.That(publicationCallbackCount, Is.Zero,
                    "A target that becomes valid after the first Q attempt must never be published.");
                Assert.That(quarantined, Is.Not.Null);
                Assert.That(quarantined!.Status, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Quarantined));
                Assert.That(quarantined.PayloadHash, Is.EqualTo(payload.Hash).IgnoreCase);
                Assert.That(proxyState.PresenceReleaseCalls, Is.Zero);
            });
        }
        finally
        {
            proxyState.ReleaseAcknowledgementResult();
            pair.ServerLogHandler.JudgeLog -= JudgeExpectedHiddenQuarantine;
            SetPrivateField(cryo, "_db", realDb);
        }

        await pair.CleanReturnAsync();
    }

    [TestCase(AbortAmbiguityMode.NullPrecondition)]
    [TestCase(AbortAmbiguityMode.HistoricalConsumed)]
    [TestCase(AbortAmbiguityMode.AcknowledgedPlayable)]
    public async Task AmbiguousAbortEvidenceRetainsExactBodyAndRestoreFence(AbortAmbiguityMode mode)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        var session = server.ResolveDependency<IPlayerManager>()
            .GetSessionById(pair.Client.Session!.UserId);
        var entities = server.ResolveDependency<IEntityManager>();
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        var realDb = server.ResolveDependency<IServerDbManager>();
        var containers = entities.System<ContainerSystem>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var slot = preferences.GetPreferences(session.UserId).SelectedCharacterIndex;

        var payload = default(LuaMDeepCryoPayload);
        await server.WaitAssertion(() =>
        {
            var sourcePod = entities.SpawnEntity("LuaMDeepCryoTestPod", MapCoordinates.Nullspace);
            var profile = (HumanoidCharacterProfile) preferences.GetPreferences(session.UserId)
                .Characters[slot];
            var species = prototypes.Index<SpeciesPrototype>(profile.Species);
            var sourceBody = entities.SpawnEntity(species.Prototype.Id, MapCoordinates.Nullspace);
            Assert.That(containers.Insert(sourceBody, containers.GetContainer(sourcePod, "body")), Is.True);
            Assert.That(cryo.TryCapturePayload(sourceBody, out payload, out var reason), Is.True, reason);
            entities.DeleteEntity(sourcePod);
        });

        var profileId = await realDb.GetCharacterIdAsync(session.UserId, slot);
        Assert.That(profileId, Is.Not.Null);
        var playable = await EnsurePlayableAuthorityAsync(realDb, session.UserId, profileId!.Value, slot);
        var precondition = await realDb.GetLuaMDeepCryoStorePreconditionAsync(
            session.UserId,
            profileId.Value,
            slot);
        Assert.That(precondition, Is.Not.Null);
        var stored = await realDb.StoreLuaMDeepCryoSnapshotAsync(new LuaMDeepCryoStoreRequest(
            Guid.NewGuid(), session.UserId, profileId.Value, slot, 1,
            LuaMDeepCryoPersistenceSystem.PayloadFormatVersion,
            payload.Bytes, payload.Hash, payload.Bytes.Length, payload.EntityCount,
            "abort-ambiguity-test", payload.PrototypeManifestHash, DateTime.UtcNow,
            precondition!.LifecycleRevision,
            playable.LeaseId));
        Assert.That(stored.Success, Is.True);

        Task<LuaMDeepCryoClaimResult>? claimTask = null;
        await server.WaitPost(() => claimTask = cryo.ClaimRestoreAsync(session.UserId));
        await DrainServerTaskAsync(pair, claimTask!);
        var claim = await claimTask!.WaitAsync(TimeSpan.FromSeconds(5));
        if (claim.Handle == null)
        {
            var failedClaimSnapshot = await realDb.GetLuaMDeepCryoSnapshotAsync(
                session.UserId,
                profileId.Value,
                slot);
            Assert.Fail($"Claim failed with {claim.Status}; snapshot status={failedClaimSnapshot?.Status}, " +
                        $"quarantine reason={failedClaimSnapshot?.QuarantineReason}.");
        }
        Assert.That(claim.Handle, Is.Not.Null);
        var handle = claim.Handle!;

        var proxy = DispatchProxy.Create<IServerDbManager, AmbiguousAbortProxy>();
        var proxyState = (AmbiguousAbortProxy) (object) proxy;
        proxyState.Inner = realDb;
        proxyState.Mode = mode;
        SetPrivateField(cryo, "_db", proxy);
        bool JudgeExpectedAbortAmbiguity(string sawmillName, LogEvent message)
            => message.RenderMessage().Contains("Deep-cryo abort attempt", StringComparison.Ordinal);
        pair.ServerLogHandler.JudgeLog += JudgeExpectedAbortAmbiguity;
        try
        {
            Task? abort = null;
            await server.WaitPost(() => abort = cryo.AbortRestoreAsync(handle, $"ambiguous-{mode}"));
            await DrainServerTaskAsync(pair, abort!);
            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(entities.EntityExists(handle.Body), Is.True,
                        "Null, historical Consumed, and ACK-hidden Playable observations cannot delete the sole body.");
                    Assert.That(cryo.IsPresenceSuspended(handle.Body), Is.True);
                    Assert.That(cryo.IsRestorePublicationActive(handle.Key), Is.True,
                        "Ambiguous abort evidence must retain the exact CharacterKey publication fence.");
                });
            });

            proxyState.Mode = AbortAmbiguityMode.None;
            Task? recoveredAbort = null;
            await server.WaitPost(() => recoveredAbort = cryo.AbortRestoreAsync(handle, "exact-abort-recovery"));
            await DrainServerTaskAsync(pair, recoveredAbort!);
            await WaitForServerConditionAsync(
                pair,
                () => !cryo.IsRestorePublicationActive(handle.Key) && !entities.EntityExists(handle.Body),
                "The retained ambiguous abort did not recover through its exact immutable operation.");
        }
        finally
        {
            proxyState.Mode = AbortAmbiguityMode.None;
            pair.ServerLogHandler.JudgeLog -= JudgeExpectedAbortAmbiguity;
            SetPrivateField(cryo, "_db", realDb);
        }

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DuplicateConsumeCannotReplaceOwnersReceiptOrAbortPreparedRestore()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);

        var players = server.ResolveDependency<IPlayerManager>();
        var session = players.GetSessionById(clientSession!.UserId);
        var entities = server.ResolveDependency<IEntityManager>();
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var realDb = server.ResolveDependency<IServerDbManager>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();

        var slot = -1;
        var payload = default(LuaMDeepCryoPayload);
        await server.WaitAssertion(() =>
        {
            slot = preferences.GetPreferences(session.UserId).SelectedCharacterIndex;
            var sourcePod = entities.SpawnEntity("LuaMDeepCryoTestPod", MapCoordinates.Nullspace);
            var profile = (HumanoidCharacterProfile) preferences.GetPreferences(session.UserId).Characters[slot];
            var species = prototypes.Index<SpeciesPrototype>(profile.Species);
            var sourceBody = entities.SpawnEntity(species.Prototype.Id, MapCoordinates.Nullspace);
            Assert.That(
                entities.System<ContainerSystem>().Insert(
                    sourceBody,
                    entities.System<ContainerSystem>().GetContainer(sourcePod, "body")),
                Is.True);
            Assert.That(cryo.TryCapturePayload(sourceBody, out payload, out var reason), Is.True, reason);
            entities.DeleteEntity(sourcePod);
        });

        var profileId = await realDb.GetCharacterIdAsync(session.UserId, slot);
        Assert.That(profileId, Is.Not.Null);
        var playableAuthority = await EnsurePlayableAuthorityAsync(
            realDb, session.UserId, profileId!.Value, slot);
        var storePrecondition = await realDb.GetLuaMDeepCryoStorePreconditionAsync(
            session.UserId,
            profileId!.Value,
            slot);
        Assert.That(storePrecondition, Is.Not.Null);
        Assert.That(storePrecondition!.ActiveSnapshot, Is.Null);
        var stored = await realDb.StoreLuaMDeepCryoSnapshotAsync(new LuaMDeepCryoStoreRequest(
            Guid.NewGuid(), session.UserId, profileId!.Value, slot, 1,
            LuaMDeepCryoPersistenceSystem.PayloadFormatVersion,
            payload.Bytes, payload.Hash, payload.Bytes.Length, payload.EntityCount,
            "integration-test", payload.PrototypeManifestHash, DateTime.UtcNow,
            storePrecondition!.LifecycleRevision,
            playableAuthority.LeaseId));
        Assert.That(stored.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));

        Task<LuaMDeepCryoClaimResult>? claimTask = null;
        await server.WaitPost(() => claimTask = cryo.ClaimRestoreAsync(session.UserId));
        await DrainServerTaskAsync(pair, claimTask!);
        var claim = await claimTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(claim.Status, Is.EqualTo(LuaMDeepCryoClaimStatus.Success));
        Assert.That(claim.Handle, Is.Not.Null);
        var handle = claim.Handle!;

        var proxy = DispatchProxy.Create<IServerDbManager, PausedCryoCompleteProxy>();
        var proxyState = (PausedCryoCompleteProxy) (object) proxy;
        proxyState.Inner = realDb;
        proxyState.ThrowAfterFirstAuthorizationCommit = true;
        SetPrivateField(cryo, "_db", proxy);
        bool JudgeExpectedAuthorizationReplay(string sawmillName, LogEvent message)
        {
            var rendered = message.RenderMessage();
            return rendered.Contains("simulated AUTH commit-then-throw", StringComparison.Ordinal) ||
                   rendered.Contains("publication authorization remains unresolved", StringComparison.Ordinal);
        }

        pair.ServerLogHandler.JudgeLog += JudgeExpectedAuthorizationReplay;

        Task<LuaMDeepCryoConsumeResult>? first = null;
        try
        {
            await server.WaitPost(() => first = cryo.ConsumeRestoreAsync(handle));
            await DrainServerTaskAsync(pair, proxyState.CompletionCommitted.Task);

            Task<LuaMDeepCryoConsumeResult>? duplicate = null;
            await server.WaitPost(() => duplicate = cryo.ConsumeRestoreAsync(handle));
            await DrainServerTaskAsync(pair, duplicate!);
            var duplicateResult = await duplicate!.WaitAsync(TimeSpan.FromSeconds(5));
            var prepared = await realDb.GetLuaMDeepCryoSnapshotAsync(session.UserId, profileId.Value, slot);

            Assert.Multiple(() =>
            {
                Assert.That(duplicateResult.Status, Is.EqualTo(LuaMDeepCryoConsumeStatus.Busy));
                Assert.That(duplicateResult.Receipt, Is.Null,
                    "A duplicate caller must not receive the owner's compensation authority.");
                Assert.That(proxyState.CompletionCalls, Is.EqualTo(1));
                Assert.That(proxyState.AbortCalls, Is.Zero);
                Assert.That(prepared, Is.Not.Null);
                Assert.That(prepared!.Status, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Consumed));
                Assert.That(prepared.LeaseId, Is.EqualTo(handle.LeaseId));
                Assert.That(entities.EntityExists(handle.Body), Is.True);
            });
            await server.WaitAssertion(() =>
            {
                Assert.That(cryo.IsRestorePublicationActive(handle.Key), Is.True);
                Assert.That(cryo.TryResolvePublicationReceipt(handle, null, out _), Is.False,
                    "A null receipt must never recover the owner's retained proof.");
            });

            proxyState.ReleaseCompletionResult();
            await DrainServerTaskAsync(pair, first!);
            var ownerResult = await first!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(ownerResult.Status, Is.EqualTo(LuaMDeepCryoConsumeStatus.Success));
            Assert.That(ownerResult.Receipt, Is.Not.Null);

            Task<LuaMDeepCryoAuthorizationStatus>? authorized = null;
            await server.WaitPost(() =>
                authorized = cryo.AuthorizeRestorePublicationAsync(ownerResult.Receipt!));
            await DrainServerTaskAsync(pair, authorized!);
            Assert.That(await authorized!.WaitAsync(TimeSpan.FromSeconds(5)),
                Is.EqualTo(LuaMDeepCryoAuthorizationStatus.Authorized));
            Assert.That(proxyState.AuthorizationCalls, Is.EqualTo(4),
                "A commit-then-throw AUTH must settle through the bounded exact-operation replays.");

            Task<bool>? acknowledged = null;
            await server.WaitPost(() =>
                acknowledged = cryo.AcknowledgeRestorePublicationAsync(ownerResult.Receipt!));
            await DrainServerTaskAsync(pair, acknowledged!);
            Assert.That(await acknowledged!.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(await realDb.GetLuaMDeepCryoSnapshotAsync(session.UserId, profileId.Value, slot), Is.Null);
            await server.WaitAssertion(() =>
            {
                Assert.That(cryo.IsRestorePublicationActive(handle.Key), Is.False);
                Assert.That(entities.EntityExists(handle.Body), Is.True);
                Assert.That(entities.HasComponent<LuaMDeepCryoStoredComponent>(handle.Body), Is.False);
            });

            Task<LuaMDeepCryoConsumeResult>? staleConsume = null;
            await server.WaitPost(() => staleConsume = cryo.ConsumeRestoreAsync(handle));
            await DrainServerTaskAsync(pair, staleConsume!);
            var staleResult = await staleConsume!.WaitAsync(TimeSpan.FromSeconds(5));
            Task? staleAbort = null;
            await server.WaitPost(() => staleAbort = cryo.AbortRestoreAsync(handle, "late-stale-test"));
            await DrainServerTaskAsync(pair, staleAbort!);
            Assert.Multiple(() =>
            {
                Assert.That(staleResult.Status, Is.EqualTo(LuaMDeepCryoConsumeStatus.Failed));
                Assert.That(staleResult.Receipt, Is.Null);
                Assert.That(proxyState.CompletionCalls, Is.EqualTo(1));
                Assert.That(proxyState.AbortCalls, Is.Zero,
                    "A stale handle must not issue Abort after the owner's ACK.");
                Assert.That(entities.EntityExists(handle.Body), Is.True,
                    "A late duplicate must not delete the already-published deserialized body.");
            });
        }
        finally
        {
            proxyState.ReleaseCompletionResult();
            pair.ServerLogHandler.JudgeLog -= JudgeExpectedAuthorizationReplay;
            SetPrivateField(cryo, "_db", realDb);
        }

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ReturnedUnknownWithoutExactCompletionProofIsNotPublished()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);

        var players = server.ResolveDependency<IPlayerManager>();
        var session = players.GetSessionById(clientSession!.UserId);
        var entities = server.ResolveDependency<IEntityManager>();
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var realDb = server.ResolveDependency<IServerDbManager>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();
        var cryoSleep = entities.System<CryoSleepSystem>();
        var minds = entities.System<MindSystem>();
        var testMap = await pair.CreateTestMap();

        var slot = -1;
        var payload = default(LuaMDeepCryoPayload);
        MindComponent mind = default!;
        await server.WaitAssertion(() =>
        {
            slot = preferences.GetPreferences(session.UserId).SelectedCharacterIndex;
            var profile = (HumanoidCharacterProfile) preferences.GetPreferences(session.UserId).Characters[slot];
            var speciesPrototypeId = prototypes.Index<SpeciesPrototype>(profile.Species).Prototype.Id;
            var sourcePod = entities.SpawnEntity("LuaMDeepCryoTestPod", MapCoordinates.Nullspace);
            var sourceBody = entities.SpawnEntity(speciesPrototypeId, MapCoordinates.Nullspace);
            Assert.That(
                entities.System<ContainerSystem>().Insert(
                    sourceBody,
                    entities.System<ContainerSystem>().GetContainer(sourcePod, "body")),
                Is.True);
            Assert.That(cryo.TryCapturePayload(sourceBody, out payload, out var reason), Is.True, reason);
            entities.DeleteEntity(sourcePod);

            mind = minds.TryGetMind(session.UserId, out _, out var existingMind)
                ? existingMind
                : minds.CreateMind(session.UserId, nameof(ReturnedUnknownWithoutExactCompletionProofIsNotPublished)).Comp;
            entities.SpawnEntity("LuaMDeepCryoTestSleepPod", testMap.GridCoords);
        });

        var profileId = await realDb.GetCharacterIdAsync(session.UserId, slot);
        Assert.That(profileId, Is.Not.Null);
        var playableAuthority = await EnsurePlayableAuthorityAsync(
            realDb, session.UserId, profileId!.Value, slot);
        var storePrecondition = await realDb.GetLuaMDeepCryoStorePreconditionAsync(
            session.UserId,
            profileId!.Value,
            slot);
        Assert.That(storePrecondition, Is.Not.Null);
        Assert.That(storePrecondition!.ActiveSnapshot, Is.Null);
        var stored = await realDb.StoreLuaMDeepCryoSnapshotAsync(new LuaMDeepCryoStoreRequest(
            Guid.NewGuid(), session.UserId, profileId!.Value, slot, 1,
            LuaMDeepCryoPersistenceSystem.PayloadFormatVersion,
            payload.Bytes, payload.Hash, payload.Bytes.Length, payload.EntityCount,
            "integration-test", payload.PrototypeManifestHash, DateTime.UtcNow,
            storePrecondition!.LifecycleRevision,
            playableAuthority.LeaseId));
        Assert.That(stored.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));

        await server.WaitAssertion(() =>
        {
            mind = minds.TryGetMind(session.UserId, out _, out var currentMind)
                ? currentMind
                : minds.CreateMind(
                    session.UserId,
                    $"{nameof(ReturnedUnknownWithoutExactCompletionProofIsNotPublished)}-restore").Comp;
        });

        var proxy = DispatchProxy.Create<IServerDbManager, UnprovenUnknownCryoCompleteProxy>();
        var proxyState = (UnprovenUnknownCryoCompleteProxy) (object) proxy;
        proxyState.Inner = realDb;
        SetPrivateField(cryo, "_db", proxy);

        Task<SharedCryoSleepSystem.ReturnToBodyStatus>? attempt = null;
        try
        {
            await server.WaitPost(() => attempt = cryoSleep.TryReturnToBody(mind, force: true));
            await DrainServerTaskAsync(pair, attempt!);

            Assert.That(await attempt!.WaitAsync(TimeSpan.FromSeconds(5)),
                Is.EqualTo(SharedCryoSleepSystem.ReturnToBodyStatus.BodyMissing));
            Assert.That(proxyState.CompletionCalls, Is.EqualTo(2),
                "A returned UnknownOutcome must be replayed with the exact request before it can be trusted.");
            var reopened = await realDb.GetLuaMDeepCryoSnapshotAsync(session.UserId, profileId.Value, slot);
            Assert.Multiple(() =>
            {
                Assert.That(reopened, Is.Not.Null);
                Assert.That(reopened!.Status, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Stored));
                Assert.That(reopened.LeaseId, Is.Null);
            });
            await server.WaitAssertion(() =>
                Assert.That(cryo.IsRestorePublicationActive(new LuaMDeepCryoPersistenceSystem.CharacterKey(
                    session.UserId, profileId.Value, slot)), Is.False));
        }
        finally
        {
            SetPrivateField(cryo, "_db", realDb);
        }

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task UnresolvedStableIdentityBlocksFreshSpawnAndDeletesSpawnCompleteFallback()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);

        var players = server.ResolveDependency<IPlayerManager>();
        var session = players.GetSessionById(clientSession!.UserId);
        var entities = server.ResolveDependency<IEntityManager>();
        var realPreferences = server.ResolveDependency<IServerPreferencesManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var realDb = server.ResolveDependency<IServerDbManager>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();
        var minds = entities.System<MindSystem>();
        var selectedPreferences = realPreferences.GetPreferences(session.UserId);
        var slot = selectedPreferences.SelectedCharacterIndex;
        var profile = (HumanoidCharacterProfile) selectedPreferences.SelectedCharacter;
        var speciesPrototypeId = prototypes.Index<SpeciesPrototype>(profile.Species).Prototype.Id;
        var profileId = await realDb.GetCharacterIdAsync(session.UserId, slot);
        Assert.That(profileId, Is.Not.Null);
        var playableAuthority = await EnsurePlayableAuthorityAsync(
            realDb, session.UserId, profileId!.Value, slot);

        LuaMDeepCryoPayload payload = default;
        await server.WaitAssertion(() =>
        {
            var sourcePod = entities.SpawnEntity("LuaMDeepCryoTestPod", MapCoordinates.Nullspace);
            var sourceBody = entities.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
            Assert.That(
                entities.System<ContainerSystem>().Insert(
                    sourceBody,
                    entities.System<ContainerSystem>().GetContainer(sourcePod, "body")),
                Is.True);
            Assert.That(cryo.TryCapturePayload(sourceBody, out payload, out var reason), Is.True, reason);
            entities.DeleteEntity(sourcePod);
        });

        var storePrecondition = await realDb.GetLuaMDeepCryoStorePreconditionAsync(
            session.UserId,
            profileId!.Value,
            slot);
        Assert.That(storePrecondition, Is.Not.Null);
        Assert.That(storePrecondition!.ActiveSnapshot, Is.Null);
        var stored = await realDb.StoreLuaMDeepCryoSnapshotAsync(new LuaMDeepCryoStoreRequest(
            Guid.NewGuid(), session.UserId, profileId!.Value, slot, 1,
            LuaMDeepCryoPersistenceSystem.PayloadFormatVersion,
            payload.Bytes, payload.Hash, payload.Bytes.Length, payload.EntityCount,
            "integration-test", payload.PrototypeManifestHash, DateTime.UtcNow,
            storePrecondition!.LifecycleRevision,
            playableAuthority.LeaseId));
        Assert.That(stored.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
        var durableBeforeSpawn = await realDb.GetLuaMDeepCryoSnapshotAsync(
            session.UserId,
            profileId.Value,
            slot);
        Assert.Multiple(() =>
        {
            Assert.That(durableBeforeSpawn, Is.Not.Null);
            Assert.That(durableBeforeSpawn!.Status, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Stored));
            Assert.That(durableBeforeSpawn.LeaseId, Is.Null);
        });

        var proxy = DispatchProxy.Create<IServerPreferencesManager, UnresolvedIdentityPreferencesProxy>();
        var proxyState = (UnresolvedIdentityPreferencesProxy) (object) proxy;
        proxyState.Inner = realPreferences;
        SetPrivateField(cryo, "_preferences", proxy);
        var sawBlockedBeforeSpawn = false;
        var sawDeletedFallback = false;
        bool JudgeExpectedIdentityFailure(string sawmillName, LogEvent message)
        {
            var rendered = message.RenderMessage();
            if (rendered.Contains("stable deep-cryo identity is unresolved", StringComparison.Ordinal))
            {
                sawBlockedBeforeSpawn = true;
                return true;
            }

            if (rendered.Contains("Could not bind exact deep-cryo lifecycle authority", StringComparison.Ordinal))
            {
                sawDeletedFallback = true;
                return true;
            }

            return false;
        }

        pair.ServerLogHandler.JudgeLog += JudgeExpectedIdentityFailure;

        EntityUid fallbackBody = EntityUid.Invalid;
        try
        {
            var beforeSpawn = new PlayerBeforeSpawnEvent(
                session,
                profile,
                jobId: null,
                lateJoin: true,
                station: EntityUid.Invalid);
            await server.WaitAssertion(() =>
            {
                InvokePrivateEventHandler(cryo, "OnPlayerBeforeSpawn", beforeSpawn);
                Assert.That(beforeSpawn.Handled, Is.True,
                    "An unresolved stable character identity must fail closed before a fresh body is created.");
            });

            await server.WaitAssertion(() =>
            {
                fallbackBody = entities.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
                Assert.That(entities.EntityExists(fallbackBody), Is.True,
                    "The safety-net branch must exercise a body that was actually created despite the pre-spawn fence.");
                var attached = AttachSessionToBody(
                    entities,
                    players,
                    minds,
                    session,
                    fallbackBody,
                    nameof(UnresolvedStableIdentityBlocksFreshSpawnAndDeletesSpawnCompleteFallback));
                Assert.Multiple(() =>
                {
                    Assert.That(attached.Component.CurrentEntity, Is.EqualTo(fallbackBody));
                    Assert.That(session.AttachedEntity, Is.EqualTo(fallbackBody));
                });

                var spawnComplete = new PlayerSpawnCompleteEvent(
                    fallbackBody,
                    session,
                    jobId: null,
                    lateJoin: true,
                    silent: false,
                    joinOrder: 1,
                    station: EntityUid.Invalid,
                    profile: profile);
                InvokePrivateEventHandler(cryo, "OnPlayerSpawnComplete", spawnComplete);
                var detachedMind = entities.GetComponent<MindComponent>(attached.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(entities.EntityExists(fallbackBody), Is.False,
                        "The spawn-complete safety net must synchronously delete the unbound body.");
                    Assert.That(detachedMind.CurrentEntity, Is.Not.EqualTo(fallbackBody),
                        "Synchronous fallback deletion must clear the mind's current body before returning.");
                    Assert.That(session.AttachedEntity, Is.Not.EqualTo(fallbackBody),
                        "Synchronous fallback deletion must detach the session before returning.");
                });
            });

            var durableAfterFallback = await realDb.GetLuaMDeepCryoSnapshotAsync(
                session.UserId,
                profileId.Value,
                slot);
            Assert.Multiple(() =>
            {
                Assert.That(proxyState.FailedLookups, Is.EqualTo(1),
                    "SpawnComplete must reject the absent ticket before attempting another fallible identity lookup.");
                Assert.That(sawBlockedBeforeSpawn, Is.True);
                Assert.That(sawDeletedFallback, Is.True);
                Assert.That(durableAfterFallback?.Id, Is.EqualTo(durableBeforeSpawn!.Id));
                Assert.That(durableAfterFallback?.Status, Is.EqualTo(DbLuaMDeepCryoSnapshotStatus.Stored),
                    "Identity uncertainty must not discard the authoritative durable snapshot.");
                Assert.That(durableAfterFallback?.LeaseId, Is.Null);
            });

            proxyState.FailIdentityLookup = false;
            Task<bool>? recoveredLookup = null;
            await server.WaitPost(() => recoveredLookup = cryo.HasStoredSnapshotAsync(session.UserId));
            await DrainServerTaskAsync(pair, recoveredLookup!);
            Assert.That(await recoveredLookup!.WaitAsync(TimeSpan.FromSeconds(5)), Is.True,
                "The identity lookup failure is transient; the retained snapshot must remain discoverable after recovery.");
        }
        finally
        {
            pair.ServerLogHandler.JudgeLog -= JudgeExpectedIdentityFailure;
            SetPrivateField(cryo, "_preferences", realPreferences);
        }

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FreshSpawnReattachesExactLocalPlayableBodyAfterMindWipe()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);

        var players = server.ResolveDependency<IPlayerManager>();
        var session = players.GetSessionById(clientSession!.UserId);
        var entities = server.ResolveDependency<IEntityManager>();
        var preferences = server.ResolveDependency<IServerPreferencesManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var db = server.ResolveDependency<IServerDbManager>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();
        var minds = entities.System<MindSystem>();
        var testMap = await pair.CreateTestMap();
        var selected = preferences.GetPreferences(session.UserId);
        var slot = selected.SelectedCharacterIndex;
        var profile = (HumanoidCharacterProfile) selected.SelectedCharacter;
        var species = prototypes.Index<SpeciesPrototype>(profile.Species).Prototype.Id;
        var profileId = await db.GetCharacterIdAsync(session.UserId, slot);
        Assert.That(profileId, Is.Not.Null);
        var authority = await EnsurePlayableAuthorityAsync(db, session.UserId, profileId!.Value, slot);

        EntityUid body = default;
        await server.WaitAssertion(() =>
        {
            body = entities.SpawnEntity(species, testMap.GridCoords);
            var identity = entities.EnsureComponent<LuaMDeepCryoIdentityComponent>(body);
            BindPlayableTestIdentity(identity, session.UserId, profileId.Value, slot, authority);
            identity.SlotGeneration = preferences.GetCharacterSlotGeneration(session.UserId, slot);
            AttachSessionToBody(entities, players, minds, session, body,
                nameof(FreshSpawnReattachesExactLocalPlayableBodyAfterMindWipe));
            var key = new LuaMDeepCryoPersistenceSystem.CharacterKey(session.UserId, profileId.Value, slot);
            Assert.That(InvokePrivateMethod<bool>(cryo, "SuspendPresenceBody", body, key), Is.True);
            Assert.That(entities.GetComponent<TransformComponent>(body).MapID, Is.EqualTo(MapId.Nullspace));
            minds.WipeMind(session);
            Assert.That(session.AttachedEntity, Is.Not.EqualTo(body));
        });

        var beforeSpawn = new PlayerBeforeSpawnEvent(
            session,
            profile,
            jobId: null,
            lateJoin: true,
            station: EntityUid.Invalid);
        await server.WaitPost(() => InvokePrivateEventHandler(cryo, "OnPlayerBeforeSpawn", beforeSpawn));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (session.AttachedEntity != body && DateTime.UtcNow < deadline)
        {
            await server.WaitIdleAsync();
            await pair.RunTicksSync(1);
        }

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(beforeSpawn.Handled, Is.True);
                Assert.That(session.AttachedEntity, Is.EqualTo(body));
                Assert.That(entities.EntityExists(body), Is.True);
                Assert.That(entities.GetComponent<TransformComponent>(body).MapID, Is.Not.EqualTo(MapId.Nullspace));
            });
        });

        var after = await db.GetLuaMDeepCryoStorePreconditionAsync(session.UserId, profileId.Value, slot);
        Assert.Multiple(() =>
        {
            Assert.That(after?.Authority?.LeaseId, Is.EqualTo(authority.LeaseId));
            Assert.That(after?.Authority?.Phase, Is.EqualTo(DbLuaMCharacterPresencePhase.Playable));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SpawnLifecycleTicketIsConsumedBeforeFailedLookupAndCannotBeReused()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);

        var players = server.ResolveDependency<IPlayerManager>();
        var session = players.GetSessionById(clientSession!.UserId);
        var entities = server.ResolveDependency<IEntityManager>();
        var realPreferences = server.ResolveDependency<IServerPreferencesManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var realDb = server.ResolveDependency<IServerDbManager>();
        var cryo = entities.System<LuaMDeepCryoPersistenceSystem>();
        var selectedPreferences = realPreferences.GetPreferences(session.UserId);
        var slot = selectedPreferences.SelectedCharacterIndex;
        var profile = (HumanoidCharacterProfile) selectedPreferences.SelectedCharacter;
        var speciesPrototypeId = prototypes.Index<SpeciesPrototype>(profile.Species).Prototype.Id;
        var profileId = await realDb.GetCharacterIdAsync(session.UserId, slot);
        Assert.That(profileId, Is.Not.Null);
        var initial = await realDb.GetLuaMDeepCryoStorePreconditionAsync(
            session.UserId,
            profileId!.Value,
            slot);
        Assert.That(initial, Is.Not.Null);
        Assert.That(initial!.ActiveSnapshot, Is.Null);
        if (initial.Authority is { } authority)
        {
            var released = await realDb.ReleaseLuaMCharacterPresenceAsync(
                new LuaMCharacterPresenceReleaseRequest(
                    Guid.NewGuid(),
                    session.UserId,
                    profileId.Value,
                    slot,
                    authority.LeaseId,
                    authority.Phase,
                    authority.SnapshotId,
                    authority.Revision,
                    authority.AuthorityLifecycleRevision,
                    "runtime-integration-test-reset",
                    DateTime.UtcNow));
            Assert.That(released.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
        }

        var proxy = DispatchProxy.Create<IServerPreferencesManager, UnresolvedIdentityPreferencesProxy>();
        var proxyState = (UnresolvedIdentityPreferencesProxy) (object) proxy;
        proxyState.Inner = realPreferences;
        var dbProxy = DispatchProxy.Create<IServerDbManager, PausedPresenceReserveProxy>();
        var dbProxyState = (PausedPresenceReserveProxy) (object) dbProxy;
        dbProxyState.Inner = realDb;
        SetPrivateField(cryo, "_db", dbProxy);
        var freshSpawnReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetPrivateField(cryo, "_freshSpawnReadyHookForTests", (Func<bool>) (() =>
        {
            freshSpawnReady.TrySetResult(true);
            return true;
        }));
        var rejectedBodies = 0;
        bool JudgeExpectedTicketRejection(string sawmillName, LogEvent message)
        {
            if (!message.RenderMessage().Contains(
                    "Could not bind exact deep-cryo lifecycle authority",
                    StringComparison.Ordinal))
            {
                return false;
            }

            Interlocked.Increment(ref rejectedBodies);
            return true;
        }

        pair.ServerLogHandler.JudgeLog += JudgeExpectedTicketRejection;
        try
        {
            await server.WaitAssertion(() =>
            {
                var beforeSpawn = new PlayerBeforeSpawnEvent(
                    session,
                    profile,
                    jobId: null,
                    lateJoin: true,
                    station: EntityUid.Invalid);
                InvokePrivateEventHandler(cryo, "OnPlayerBeforeSpawn", beforeSpawn);
                Assert.That(beforeSpawn.Handled, Is.True,
                    "The first attempt must wait for durable FreshReserved authority.");
            });
            await DrainServerTaskAsync(pair, dbProxyState.ReserveStarted.Task);
            dbProxyState.ReleaseReserve();
            await DrainServerTaskAsync(pair, freshSpawnReady.Task);

            SetPrivateField(cryo, "_preferences", proxy);
            await server.WaitAssertion(() =>
            {
                var firstBody = entities.SpawnEntity(speciesPrototypeId, MapCoordinates.Nullspace);
                var firstComplete = new PlayerSpawnCompleteEvent(
                    firstBody,
                    session,
                    jobId: null,
                    lateJoin: true,
                    silent: false,
                    joinOrder: 1,
                    station: EntityUid.Invalid,
                    profile: profile);
                InvokePrivateEventHandler(cryo, "OnPlayerSpawnComplete", firstComplete);
                Assert.That(entities.EntityExists(firstBody), Is.False,
                    "The ticket-owning body must be rejected after its identity lookup fails.");
            });

            proxyState.FailIdentityLookup = false;
            await server.WaitAssertion(() =>
            {
                var secondBody = entities.SpawnEntity(speciesPrototypeId, MapCoordinates.Nullspace);
                var secondComplete = new PlayerSpawnCompleteEvent(
                    secondBody,
                    session,
                    jobId: null,
                    lateJoin: true,
                    silent: false,
                    joinOrder: 2,
                    station: EntityUid.Invalid,
                    profile: profile);
                InvokePrivateEventHandler(cryo, "OnPlayerSpawnComplete", secondComplete);
                Assert.That(entities.EntityExists(secondBody), Is.False,
                    "The consumed ticket must not authorize a later body after identity lookup recovers.");
            });

            Assert.Multiple(() =>
            {
                Assert.That(proxyState.FailedLookups, Is.EqualTo(1),
                    "Only the ticket-owning SpawnComplete may reach the injected identity failure.");
                Assert.That(rejectedBodies, Is.EqualTo(2),
                    "Both the failed ticket owner and the attempted ticket reuse must be rejected.");
            });
        }
        finally
        {
            dbProxyState.ReleaseReserve();
            pair.ServerLogHandler.JudgeLog -= JudgeExpectedTicketRejection;
            SetPrivateField(cryo, "_preferences", realPreferences);
            SetPrivateField(cryo, "_db", realDb);
            SetPrivateField(cryo, "_freshSpawnReadyHookForTests", null!);
        }

        await pair.CleanReturnAsync();
    }

    private static async Task<LuaMCharacterPresenceAuthorityRecord> EnsurePlayableAuthorityAsync(
        IServerDbManager db,
        NetUserId userId,
        int profileId,
        int slot,
        TimeSpan? playableDuration = null)
    {
        var precondition = await db.GetLuaMDeepCryoStorePreconditionAsync(userId, profileId, slot);
        Assert.That(precondition, Is.Not.Null);
        if (precondition!.Authority is { Phase: DbLuaMCharacterPresencePhase.Playable } playable)
            return playable;

        LuaMCharacterPresenceAuthorityRecord reserved;
        if (precondition.Authority is { Phase: DbLuaMCharacterPresencePhase.FreshReserved } existingReserved)
        {
            reserved = existingReserved;
        }
        else
        {
            Assert.Multiple(() =>
            {
                Assert.That(precondition.Authority, Is.Null);
                Assert.That(precondition.ActiveSnapshot, Is.Null);
            });
            var reservedAtUtc = DateTime.UtcNow;
            var reserve = await db.ReserveLuaMCharacterPresenceAsync(new LuaMCharacterPresenceReserveRequest(
                Guid.NewGuid(),
                userId,
                profileId,
                slot,
                Guid.NewGuid(),
                "runtime-integration-test",
                1,
                reservedAtUtc,
                reservedAtUtc.AddMinutes(10),
                precondition.LifecycleRevision));
            Assert.That(reserve.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
            Assert.That(reserve.Authority, Is.Not.Null);
            reserved = reserve.Authority!;
        }

        var publishedAtUtc = DateTime.UtcNow;
        if (publishedAtUtc < reserved.RenewedAtUtc)
            publishedAtUtc = reserved.RenewedAtUtc;
        var publish = await db.PublishLuaMCharacterPresenceAsync(new LuaMCharacterPresencePublishRequest(
            Guid.NewGuid(),
            userId,
            profileId,
            slot,
            reserved.LeaseId,
            reserved.Revision,
            reserved.AuthorityLifecycleRevision,
            publishedAtUtc,
            publishedAtUtc + (playableDuration ?? TimeSpan.FromMinutes(10))));
        Assert.That(publish.Status, Is.EqualTo(LuaMDeepCryoWriteStatus.Success));
        Assert.That(publish.Authority, Is.Not.Null);
        return publish.Authority!;
    }

    private static void BindPlayableTestIdentity(
        LuaMDeepCryoIdentityComponent identity,
        NetUserId userId,
        int profileId,
        int slot,
        LuaMCharacterPresenceAuthorityRecord authority)
    {
        identity.UserId = userId;
        identity.ProfileId = profileId;
        identity.Slot = slot;
        identity.LifecycleRevision = authority.AuthorityLifecycleRevision;
        identity.PresenceLeaseId = authority.LeaseId;
        identity.PresencePhase = authority.Phase;
        identity.PresenceSnapshotId = authority.SnapshotId;
        identity.PresenceLeaseRevision = authority.Revision;
        identity.PresenceLeaseExpiresAtUtc = authority.ExpiresAtUtc;
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

    private static (EntityUid Id, MindComponent Component) AttachSessionToBody(
        IEntityManager entities,
        IPlayerManager players,
        MindSystem minds,
        ICommonSession session,
        EntityUid body,
        string characterName)
    {
        EntityUid mindId;
        MindComponent mind;
        if (minds.TryGetMind(session.UserId, out var existingId, out var existingMind))
        {
            mindId = existingId.Value;
            mind = existingMind;
        }
        else
        {
            var created = minds.CreateMind(session.UserId, characterName);
            mindId = created.Owner;
            mind = created.Comp;
        }

        if (mind.VisitingEntity != null)
            minds.UnVisit(mindId, mind);
        if (mind.OwnedEntity != body)
            minds.TransferTo(mindId, body, ghostCheckOverride: true, mind: mind);
        players.SetAttachedEntity(session, body, true);
        return (mindId, entities.GetComponent<MindComponent>(mindId));
    }

    private static EntityUid AssertStoreControlFenced(
        IEntityManager entities,
        ICommonSession session,
        EntityUid mindId,
        EntityUid body)
    {
        var mind = entities.GetComponent<MindComponent>(mindId);
        var current = mind.CurrentEntity ?? throw new AssertionException("The Store control fence detached the mind without a replacement entity.");
        Assert.Multiple(() =>
        {
            Assert.That(current, Is.Not.EqualTo(body));
            Assert.That(session.AttachedEntity, Is.EqualTo(current));
            Assert.That(session.AttachedEntity, Is.Not.EqualTo(body));
            Assert.That(entities.HasComponent<GhostComponent>(current), Is.True);
            Assert.That(entities.GetComponent<GhostComponent>(current).CanReturnToBody, Is.False,
                "A Store control fence must reject return-to-body requests until the database outcome is definitive.");
        });
        return current;
    }

    private static void AssertBodyControlRevoked(
        IEntityManager entities,
        ICommonSession session,
        EntityUid mindId,
        EntityUid body)
    {
        var mind = entities.GetComponent<MindComponent>(mindId);
        Assert.Multiple(() =>
        {
            Assert.That(mind.CurrentEntity, Is.Not.EqualTo(body));
            Assert.That(session.AttachedEntity, Is.Not.EqualTo(body));
            if (mind.CurrentEntity is { } current)
                Assert.That(session.AttachedEntity, Is.EqualTo(current));
        });

        if (mind.CurrentEntity is { } ghost && entities.TryGetComponent<GhostComponent>(ghost, out var ghostComponent))
        {
            Assert.That(ghostComponent.CanReturnToBody, Is.False,
                "An upstream Store control ghost must not retain a return path to the captured body.");
        }
    }

    private static async Task RequestGhostReturnAsync(Content.IntegrationTests.Pair.TestPair pair)
    {
        await pair.Client.WaitPost(() =>
            pair.Client.EntMan.System<Content.Client.Ghost.GhostSystem>().ReturnToBody());
        await pair.RunTicksSync(2);
    }

    private static void InvokeCryostorageReconnect(
        CryostorageSystem upstream,
        EntityUid body,
        CryostorageContainedComponent component)
    {
        var method = typeof(CryostorageSystem).GetMethod(
            "HandleCryostorageReconnection",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);
        Entity<CryostorageContainedComponent> entity = (body, component);
        method!.Invoke(upstream, new object[] { entity });
    }

    private static void InvokeTrackStoredBody(
        CryoSleepSystem cryoSleep,
        NetUserId userId,
        EntityUid body,
        EntityUid pod,
        long snapshotId,
        long revision)
    {
        var method = typeof(CryoSleepSystem).GetMethod(
            "TrackStoredBody",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);
        method!.Invoke(cryoSleep, new object[] { userId, body, pod, snapshotId, revision });
    }

    private static bool InvokeRemoveStoredBodyEpisode(
        CryoSleepSystem cryoSleep,
        NetUserId userId,
        EntityUid body,
        EntityUid pod,
        long snapshotId,
        long revision)
    {
        var method = typeof(CryoSleepSystem).GetMethod(
            "RemoveStoredBodyEpisode",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);
        return (bool) method!.Invoke(
            cryoSleep,
            new object[] { userId, body, pod, snapshotId, revision })!;
    }

    private static async Task WaitForServerConditionAsync(
        Content.IntegrationTests.Pair.TestPair pair,
        Func<bool> condition,
        string failureMessage)
    {
        var completed = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!completed && DateTime.UtcNow < deadline)
        {
            await pair.Server.WaitAssertion(() => completed = condition());
            if (completed)
                break;

            await pair.Server.WaitIdleAsync();
            await pair.RunTicksSync(1);
            await Task.Yield();
        }

        Assert.That(completed, Is.True, failureMessage);
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

    private static async Task DrainServerTaskAsync(Content.IntegrationTests.Pair.TestPair pair, Task task)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            await pair.Server.WaitIdleAsync();
            await pair.RunTicksSync(1);
            await Task.Yield();
        }

        Assert.That(task.IsCompleted, Is.True, "Timed out while pumping the server continuation.");
    }

    private static void SetPrivateField(object instance, string fieldName, object value)
    {
        var field = instance.GetType().GetField(
            fieldName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(field, Is.Not.Null, $"Missing field {fieldName}");
        field!.SetValue(instance, value);
    }

    private static void InvokePrivateEventHandler(object instance, string methodName, object ev)
    {
        var method = instance.GetType().GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(method, Is.Not.Null, $"Missing event handler {methodName}");
        method!.Invoke(instance, new[] { ev });
    }

    private static T InvokePrivateMethod<T>(object instance, string methodName, params object[] args)
    {
        var method = instance.GetType().GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(method, Is.Not.Null, $"Missing method {methodName}");
        return (T) method!.Invoke(instance, args)!;
    }

    [Virtual]
    public class UnresolvedIdentityPreferencesProxy : DispatchProxy
    {
        private int _failedLookups;

        public IServerPreferencesManager Inner { get; set; } = default!;
        public bool FailIdentityLookup { get; set; } = true;
        public int FailedLookups => Volatile.Read(ref _failedLookups);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null)
                throw new InvalidOperationException("Preferences proxy received no target method.");
            if (FailIdentityLookup &&
                targetMethod.Name == nameof(IServerPreferencesManager.TryGetCachedPreferences))
            {
                Interlocked.Increment(ref _failedLookups);
                args![1] = null;
                return false;
            }

            return targetMethod.Invoke(Inner, args);
        }
    }

    [Virtual]
    public class CompetingAcknowledgedCryoStoreProxy : DispatchProxy
    {
        private int _storeCalls;

        public IServerDbManager Inner { get; set; } = default!;
        public int StoreCalls => Volatile.Read(ref _storeCalls);
        public LuaMDeepCryoWriteResult? WinnerResult { get; private set; }
        public LuaMDeepCryoWriteResult? LoserResult { get; private set; }
        public TaskCompletionSource<bool> WinnerAcknowledged { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null)
                throw new InvalidOperationException("Deep-cryo database proxy received no target method.");
            if (targetMethod.Name == nameof(IServerDbManager.StoreLuaMDeepCryoSnapshotAsync))
            {
                var call = Interlocked.Increment(ref _storeCalls);
                return call == 1
                    ? CompleteCompetingLifecycleBeforeStaleStoreAsync(args!)
                    : targetMethod.Invoke(Inner, args);
            }

            return targetMethod.Invoke(Inner, args);
        }

        private async Task<LuaMDeepCryoWriteResult> CompleteCompetingLifecycleBeforeStaleStoreAsync(
            object?[] args)
        {
            var staleRequest = (LuaMDeepCryoStoreRequest) args[0]!;
            var cancel = args.Length > 1 ? (CancellationToken) args[1]! : default;
            WinnerResult = await Inner.StoreLuaMDeepCryoSnapshotAsync(
                    staleRequest with { OperationId = Guid.NewGuid() },
                    cancel)
                .ConfigureAwait(false);
            RequireSuccess(WinnerResult, "competing Store");

            var preClaim = await Inner.GetLuaMDeepCryoStorePreconditionAsync(
                    staleRequest.UserId,
                    staleRequest.ProfileId,
                    staleRequest.Slot,
                    cancel)
                .ConfigureAwait(false);
            if (preClaim?.ActiveSnapshot == null)
                throw new InvalidOperationException("Competing Store produced no active snapshot proof.");

            var leaseId = Guid.NewGuid();
            var claimedAt = DateTime.UtcNow;
            var claim = await Inner.ClaimLuaMDeepCryoRestoreAsync(
                    new LuaMDeepCryoClaimRequest(
                        Guid.NewGuid(),
                        staleRequest.UserId,
                        staleRequest.ProfileId,
                        staleRequest.Slot,
                        preClaim.ActiveSnapshot.Id,
                        preClaim.ActiveSnapshot.Revision,
                        staleRequest.SourceRoundId + 1,
                        leaseId,
                        "competing-acknowledged-store-test",
                        claimedAt,
                        claimedAt.AddMinutes(5),
                        preClaim.LifecycleRevision),
                    cancel)
                .ConfigureAwait(false);
            RequireSuccess(claim, "competing Claim");

            var completionOperationId = Guid.NewGuid();
            var prepared = await Inner.CompleteLuaMDeepCryoRestoreAsync(
                    new LuaMDeepCryoCompleteRequest(
                        completionOperationId,
                        staleRequest.UserId,
                        staleRequest.ProfileId,
                        staleRequest.Slot,
                        WinnerResult.SnapshotId!.Value,
                        claim.Revision!.Value,
                        leaseId,
                        DateTime.UtcNow),
                    cancel)
                .ConfigureAwait(false);
            RequireSuccess(prepared, "competing PREPARE");

            var authorizationOperationId = Guid.NewGuid();
            var authorized = await Inner.AuthorizeLuaMDeepCryoPublicationAsync(
                    new LuaMDeepCryoAuthorizePublicationRequest(
                        authorizationOperationId,
                        completionOperationId,
                        staleRequest.UserId,
                        staleRequest.ProfileId,
                        staleRequest.Slot,
                        WinnerResult.SnapshotId.Value,
                        prepared.Revision!.Value,
                        leaseId,
                        DateTime.UtcNow),
                    cancel)
                .ConfigureAwait(false);
            RequireSuccess(authorized, "competing AUTH");

            var acknowledgedAtUtc = DateTime.UtcNow;
            var acknowledged = await Inner.AcknowledgeLuaMDeepCryoPublicationAsync(
                    new LuaMDeepCryoAcknowledgePublicationRequest(
                        Guid.NewGuid(),
                        authorizationOperationId,
                        staleRequest.UserId,
                        staleRequest.ProfileId,
                        staleRequest.Slot,
                        WinnerResult.SnapshotId.Value,
                        authorized.Revision!.Value,
                        leaseId,
                        acknowledgedAtUtc,
                        acknowledgedAtUtc.AddMinutes(10)),
                    cancel)
                .ConfigureAwait(false);
            RequireSuccess(acknowledged, "competing ACK");
            WinnerAcknowledged.TrySetResult(true);

            LoserResult = await Inner.StoreLuaMDeepCryoSnapshotAsync(staleRequest, cancel)
                .ConfigureAwait(false);
            return LoserResult;
        }

        private static void RequireSuccess(LuaMDeepCryoWriteResult result, string operation)
        {
            if (!result.Success)
                throw new InvalidOperationException($"{operation} failed with {result.Status}.");
        }
    }

    public enum AbortAmbiguityMode : byte
    {
        None,
        NullPrecondition,
        HistoricalConsumed,
        AcknowledgedPlayable,
    }

    [Virtual]
    public class AmbiguousAbortProxy : DispatchProxy
    {
        public IServerDbManager Inner { get; set; } = default!;
        public AbortAmbiguityMode Mode { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null)
                throw new InvalidOperationException("Deep-cryo database proxy received no target method.");
            if (Mode != AbortAmbiguityMode.None &&
                targetMethod.Name == nameof(IServerDbManager.AbortLuaMDeepCryoRestoreAsync))
            {
                return Task.FromResult(new LuaMDeepCryoWriteResult(LuaMDeepCryoWriteStatus.InvalidRequest));
            }

            if (Mode != AbortAmbiguityMode.None &&
                targetMethod.Name == nameof(IServerDbManager.GetLuaMDeepCryoStorePreconditionAsync))
            {
                return GetAmbiguousPreconditionAsync(args!);
            }

            return targetMethod.Invoke(Inner, args);
        }

        private async Task<LuaMDeepCryoStorePrecondition?> GetAmbiguousPreconditionAsync(object?[] args)
        {
            if (Mode == AbortAmbiguityMode.NullPrecondition)
                return null;

            var current = await Inner.GetLuaMDeepCryoStorePreconditionAsync(
                    (NetUserId) args[0]!,
                    (int) args[1]!,
                    (int) args[2]!,
                    args.Length > 3 ? (CancellationToken) args[3]! : default)
                .ConfigureAwait(false);
            if (current == null)
                return null;

            return Mode switch
            {
                AbortAmbiguityMode.HistoricalConsumed when current.ActiveSnapshot != null => current with
                {
                    ActiveSnapshot = current.ActiveSnapshot with
                    {
                        Status = DbLuaMDeepCryoSnapshotStatus.Consumed,
                        LeaseId = null,
                    },
                    Authority = null,
                },
                AbortAmbiguityMode.AcknowledgedPlayable when current.Authority != null => current with
                {
                    ActiveSnapshot = null,
                    Authority = current.Authority with
                    {
                        Phase = DbLuaMCharacterPresencePhase.Playable,
                        ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15),
                    },
                },
                _ => current,
            };
        }
    }

    [Virtual]
    public class CommitThenThrowCryoStoreProxy : DispatchProxy
    {
        private int _storeCalls;

        public IServerDbManager Inner { get; set; } = default!;
        public int StoreCalls => Volatile.Read(ref _storeCalls);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null)
                throw new InvalidOperationException("Deep-cryo database proxy received no target method.");
            if (targetMethod.Name == nameof(IServerDbManager.StoreLuaMDeepCryoSnapshotAsync))
                return StoreCommitThenThrowOnceAsync(args!);
            return targetMethod.Invoke(Inner, args);
        }

        private async Task<LuaMDeepCryoWriteResult> StoreCommitThenThrowOnceAsync(object?[] args)
        {
            var call = Interlocked.Increment(ref _storeCalls);
            var result = await Inner.StoreLuaMDeepCryoSnapshotAsync(
                    (LuaMDeepCryoStoreRequest) args[0]!,
                    args.Length > 1 ? (CancellationToken) args[1]! : default)
                .ConfigureAwait(false);
            if (call == 1)
                throw new InvalidOperationException("simulated Store commit-then-throw");
            return result;
        }
    }

    [Virtual]
    public class PausedBeforeCryoStoreProxy : DispatchProxy
    {
        private readonly TaskCompletionSource<bool> _releaseStore =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _requestsLock = new();
        private readonly List<LuaMDeepCryoStoreRequest> _requests = new();
        private int _storeCalls;
        private int _presenceRenewCalls;

        public IServerDbManager Inner = default!;
        public LuaMDeepCryoWriteStatus? ForcedStatus;
        public bool ReturnExpiringPresencePrecondition;
        public bool ReturnNullPresencePrecondition;
        public int StoreCalls => Volatile.Read(ref _storeCalls);
        public int PresenceRenewCalls => Volatile.Read(ref _presenceRenewCalls);
        public LuaMDeepCryoStoreRequest? FirstRequest { get; private set; }
        public LuaMDeepCryoStoreRequest? LastRequest { get; private set; }
        public IReadOnlyList<LuaMDeepCryoStoreRequest> Requests
        {
            get
            {
                lock (_requestsLock)
                {
                    return _requests.ToArray();
                }
            }
        }
        public TaskCompletionSource<bool> StoreStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> StoreReturned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<LuaMDeepCryoWriteResult> SuccessfulStoreReturned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseStore()
        {
            _releaseStore.TrySetResult(true);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null)
                throw new InvalidOperationException("Deep-cryo database proxy received no target method.");
            if (targetMethod.Name == nameof(IServerDbManager.StoreLuaMDeepCryoSnapshotAsync))
                return PauseBeforeStoreAsync(args!);
            if (targetMethod.Name == nameof(IServerDbManager.GetLuaMDeepCryoStorePreconditionAsync) &&
                (ReturnExpiringPresencePrecondition || ReturnNullPresencePrecondition))
            {
                return GetStorePreconditionAsync(args!);
            }
            if (targetMethod.Name == nameof(IServerDbManager.RenewLuaMCharacterPresenceAsync))
                Interlocked.Increment(ref _presenceRenewCalls);
            return targetMethod.Invoke(Inner, args);
        }

        private async Task<LuaMDeepCryoStorePrecondition?> GetStorePreconditionAsync(object?[] args)
        {
            if (ReturnNullPresencePrecondition)
                return null;

            var current = await Inner.GetLuaMDeepCryoStorePreconditionAsync(
                    (NetUserId) args[0]!,
                    (int) args[1]!,
                    (int) args[2]!,
                    args.Length > 3 ? (CancellationToken) args[3]! : default)
                .ConfigureAwait(false);
            if (current?.Authority == null)
                return current;

            return current with
            {
                Authority = current.Authority with
                {
                    ExpiresAtUtc = DateTime.UtcNow.AddMinutes(1),
                },
            };
        }

        private async Task<LuaMDeepCryoWriteResult> PauseBeforeStoreAsync(object?[] args)
        {
            var request = (LuaMDeepCryoStoreRequest) args[0]!;
            var call = Interlocked.Increment(ref _storeCalls);
            if (call == 1)
                FirstRequest = request;
            LastRequest = request;
            lock (_requestsLock)
            {
                _requests.Add(request);
            }
            StoreStarted.TrySetResult(true);
            await _releaseStore.Task.ConfigureAwait(false);
            var result = ForcedStatus is { } forced
                ? new LuaMDeepCryoWriteResult(forced)
                : await Inner.StoreLuaMDeepCryoSnapshotAsync(
                        request,
                        args.Length > 1 ? (CancellationToken) args[1]! : default)
                    .ConfigureAwait(false);
            StoreReturned.TrySetResult(true);
            if (result.Success)
                SuccessfulStoreReturned.TrySetResult(result);
            return result;
        }
    }

    [Virtual]
    public class PausedPresenceRenewProxy : DispatchProxy
    {
        private readonly TaskCompletionSource<bool> _releaseFirstRenew =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _requestsLock = new();
        private readonly List<LuaMCharacterPresenceRenewRequest> _requests = new();
        private int _renewCalls;
        private int _authorityReads;
        private int _allowCommit;

        public IServerDbManager Inner { get; set; } = default!;
        public int RenewCalls => Volatile.Read(ref _renewCalls);
        public int AuthorityReads => Volatile.Read(ref _authorityReads);
        public bool AllowCommit
        {
            get => Volatile.Read(ref _allowCommit) != 0;
            set => Volatile.Write(ref _allowCommit, value ? 1 : 0);
        }
        public LuaMCharacterPresenceRenewRequest? FirstRequest { get; private set; }
        public LuaMCharacterPresenceRenewRequest? LastRequest { get; private set; }
        public IReadOnlyList<LuaMCharacterPresenceRenewRequest> Requests
        {
            get
            {
                lock (_requestsLock)
                {
                    return _requests.ToArray();
                }
            }
        }
        public TaskCompletionSource<bool> RenewStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseFirstRenew()
        {
            _releaseFirstRenew.TrySetResult(true);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null)
                throw new InvalidOperationException("Deep-cryo database proxy received no target method.");
            if (targetMethod.Name == nameof(IServerDbManager.RenewLuaMCharacterPresenceAsync))
                return RenewAsync(args!);
            if (targetMethod.Name == nameof(IServerDbManager.GetLuaMCharacterPresenceAuthorityAsync) &&
                !AllowCommit)
            {
                Interlocked.Increment(ref _authorityReads);
                return Task.FromResult<LuaMCharacterPresenceAuthorityRecord?>(null);
            }

            return targetMethod.Invoke(Inner, args);
        }

        private async Task<LuaMCharacterPresenceWriteResult> RenewAsync(object?[] args)
        {
            var request = (LuaMCharacterPresenceRenewRequest) args[0]!;
            var call = Interlocked.Increment(ref _renewCalls);
            FirstRequest ??= request;
            LastRequest = request;
            lock (_requestsLock)
            {
                _requests.Add(request);
            }
            if (call == 1)
            {
                RenewStarted.TrySetResult(true);
                await _releaseFirstRenew.Task.ConfigureAwait(false);
            }

            if (!AllowCommit)
                return new LuaMCharacterPresenceWriteResult(LuaMDeepCryoWriteStatus.UnknownOutcome);

            return await Inner.RenewLuaMCharacterPresenceAsync(
                    request,
                    args.Length > 1 ? (CancellationToken) args[1]! : default)
                .ConfigureAwait(false);
        }
    }

    [Virtual]
    public class PausedBeforeCryoClaimProxy : DispatchProxy
    {
        private readonly TaskCompletionSource<bool> _releaseClaim =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _abortCalls;

        public IServerDbManager Inner { get; set; } = default!;
        public TaskCompletionSource<bool> ClaimStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<LuaMDeepCryoSnapshotRecord> PreCommitStoredRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int AbortCalls => Volatile.Read(ref _abortCalls);

        public void ReleaseClaim()
        {
            _releaseClaim.TrySetResult(true);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null)
                throw new InvalidOperationException("Deep-cryo database proxy received no target method.");
            if (targetMethod.Name == nameof(IServerDbManager.ClaimLuaMDeepCryoRestoreAsync))
                return PauseBeforeClaimAsync(args!);
            if (targetMethod.Name == nameof(IServerDbManager.GetLuaMDeepCryoSnapshotAsync))
                return ObserveSnapshotReadAsync(args!);
            if (targetMethod.Name == nameof(IServerDbManager.GetLuaMDeepCryoStorePreconditionAsync))
                return ObserveStorePreconditionReadAsync(args!);
            if (targetMethod.Name == nameof(IServerDbManager.AbortLuaMDeepCryoRestoreAsync))
                Interlocked.Increment(ref _abortCalls);

            return targetMethod.Invoke(Inner, args);
        }

        private async Task<LuaMDeepCryoWriteResult> PauseBeforeClaimAsync(object?[] args)
        {
            ClaimStarted.TrySetResult(true);
            await _releaseClaim.Task.ConfigureAwait(false);
            return await Inner.ClaimLuaMDeepCryoRestoreAsync(
                    (LuaMDeepCryoClaimRequest) args[0]!,
                    args.Length > 1 ? (CancellationToken) args[1]! : default)
                .ConfigureAwait(false);
        }

        private async Task<LuaMDeepCryoSnapshotRecord?> ObserveSnapshotReadAsync(object?[] args)
        {
            var snapshot = await Inner.GetLuaMDeepCryoSnapshotAsync(
                    (NetUserId) args[0]!,
                    (int) args[1]!,
                    (int) args[2]!,
                    args.Length > 3 ? (CancellationToken) args[3]! : default)
                .ConfigureAwait(false);
            if (ClaimStarted.Task.IsCompleted &&
                !_releaseClaim.Task.IsCompleted &&
                snapshot is
                {
                    Status: DbLuaMDeepCryoSnapshotStatus.Stored,
                    LeaseId: null,
                })
            {
                PreCommitStoredRead.TrySetResult(snapshot);
            }

            return snapshot;
        }

        private async Task<LuaMDeepCryoStorePrecondition?> ObserveStorePreconditionReadAsync(object?[] args)
        {
            var precondition = await Inner.GetLuaMDeepCryoStorePreconditionAsync(
                    (NetUserId) args[0]!,
                    (int) args[1]!,
                    (int) args[2]!,
                    args.Length > 3 ? (CancellationToken) args[3]! : default)
                .ConfigureAwait(false);
            if (ClaimStarted.Task.IsCompleted &&
                !_releaseClaim.Task.IsCompleted &&
                precondition?.ActiveSnapshot is
                {
                    Status: DbLuaMDeepCryoSnapshotStatus.Stored,
                    LeaseId: null,
                } snapshot)
            {
                PreCommitStoredRead.TrySetResult(snapshot);
            }

            return precondition;
        }
    }

    [Virtual]
    public class PausedBeforeCryoAuthorizationProxy : DispatchProxy
    {
        private readonly TaskCompletionSource<bool> _releaseAuthorization =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IServerDbManager Inner { get; set; } = default!;
        public TaskCompletionSource<bool> AuthorizationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<LuaMDeepCryoWriteResult> AuthorizationReturned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseAuthorization()
        {
            _releaseAuthorization.TrySetResult(true);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null)
                throw new InvalidOperationException("Deep-cryo database proxy received no target method.");
            if (targetMethod.Name == nameof(IServerDbManager.AuthorizeLuaMDeepCryoPublicationAsync))
                return PauseBeforeAuthorizationAsync(args!);

            return targetMethod.Invoke(Inner, args);
        }

        private async Task<LuaMDeepCryoWriteResult> PauseBeforeAuthorizationAsync(object?[] args)
        {
            AuthorizationStarted.TrySetResult(true);
            await _releaseAuthorization.Task.ConfigureAwait(false);
            var result = await Inner.AuthorizeLuaMDeepCryoPublicationAsync(
                    (LuaMDeepCryoAuthorizePublicationRequest) args[0]!,
                    args.Length > 1 ? (CancellationToken) args[1]! : default)
                .ConfigureAwait(false);
            AuthorizationReturned.TrySetResult(result);
            return result;
        }
    }

    [Virtual]
    public class PausedAuthorizedQuarantineProxy : DispatchProxy
    {
        private readonly TaskCompletionSource<bool> _releaseAuthorizationResult =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseQuarantine =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _authorizationCalls;

        public IServerDbManager Inner { get; set; } = default!;
        public TaskCompletionSource<bool> AuthorizationCommitted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> AuthorizationReturned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> QuarantineStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> QuarantineReturned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseAuthorizationResult()
        {
            _releaseAuthorizationResult.TrySetResult(true);
        }

        public void ReleaseQuarantine()
        {
            _releaseQuarantine.TrySetResult(true);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null)
                throw new InvalidOperationException("Deep-cryo database proxy received no target method.");
            if (targetMethod.Name == nameof(IServerDbManager.AuthorizeLuaMDeepCryoPublicationAsync))
            {
                var call = Interlocked.Increment(ref _authorizationCalls);
                return call == 1
                    ? CommitAuthorizationAndPauseResultAsync(args!)
                    : targetMethod.Invoke(Inner, args);
            }

            if (targetMethod.Name == nameof(IServerDbManager.QuarantineAuthorizedLuaMDeepCryoPublicationAsync))
                return PauseBeforeQuarantineAsync(args!);

            return targetMethod.Invoke(Inner, args);
        }

        private async Task<LuaMDeepCryoWriteResult> CommitAuthorizationAndPauseResultAsync(object?[] args)
        {
            var result = await Inner.AuthorizeLuaMDeepCryoPublicationAsync(
                    (LuaMDeepCryoAuthorizePublicationRequest) args[0]!,
                    args.Length > 1 ? (CancellationToken) args[1]! : default)
                .ConfigureAwait(false);
            AuthorizationCommitted.TrySetResult(true);
            await _releaseAuthorizationResult.Task.ConfigureAwait(false);
            AuthorizationReturned.TrySetResult(true);
            return result;
        }

        private async Task<LuaMDeepCryoWriteResult> PauseBeforeQuarantineAsync(object?[] args)
        {
            QuarantineStarted.TrySetResult(true);
            await _releaseQuarantine.Task.ConfigureAwait(false);
            var result = await Inner.QuarantineAuthorizedLuaMDeepCryoPublicationAsync(
                    (LuaMDeepCryoQuarantineAuthorizedPublicationRequest) args[0]!,
                    args.Length > 1 ? (CancellationToken) args[1]! : default)
                .ConfigureAwait(false);
            QuarantineReturned.TrySetResult(true);
            return result;
        }
    }

    [Virtual]
    public class PausedPresenceReserveProxy : DispatchProxy
    {
        private readonly TaskCompletionSource<bool> _releaseReserve =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IServerDbManager Inner { get; set; } = default!;
        public TaskCompletionSource<bool> ReserveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseReserve()
        {
            _releaseReserve.TrySetResult(true);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null)
                throw new InvalidOperationException("Deep-cryo database proxy received no target method.");
            if (targetMethod.Name == nameof(IServerDbManager.ReserveLuaMCharacterPresenceAsync))
                return ReserveAsync(args!);

            return targetMethod.Invoke(Inner, args);
        }

        private async Task<LuaMCharacterPresenceWriteResult> ReserveAsync(object?[] args)
        {
            ReserveStarted.TrySetResult(true);
            await _releaseReserve.Task.ConfigureAwait(false);
            return await Inner.ReserveLuaMCharacterPresenceAsync(
                    (LuaMCharacterPresenceReserveRequest) args[0]!,
                    args.Length > 1 ? (CancellationToken) args[1]! : default)
                .ConfigureAwait(false);
        }
    }

    [Virtual]
    public class PausedCryoAcknowledgementProxy : DispatchProxy
    {
        private readonly TaskCompletionSource<bool> _releaseAcknowledgementResult =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseRenewalResult =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _acknowledgementCalls;
        private int _presenceReleaseCalls;
        private int _presenceRenewalCalls;
        private int _quarantineCalls;
        private int _hiddenQuarantineSnapshotReads;

        public IServerDbManager Inner { get; set; } = default!;
        public bool CommitQuarantineThenHideFirstOutcome { get; set; }
        public bool PauseFirstPresenceRenewal { get; set; }
        public int FailAcknowledgedQuarantineAttempts { get; set; }
        public int PresenceReleaseCalls => Volatile.Read(ref _presenceReleaseCalls);
        public int PresenceRenewalCalls => Volatile.Read(ref _presenceRenewalCalls);
        public TaskCompletionSource<bool> AcknowledgementCommitted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> AcknowledgementReturned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> RenewalCommitted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> RenewalReturned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseAcknowledgementResult()
        {
            _releaseAcknowledgementResult.TrySetResult(true);
        }

        public void ReleaseRenewalResult()
        {
            _releaseRenewalResult.TrySetResult(true);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null)
                throw new InvalidOperationException("Deep-cryo database proxy received no target method.");
            if (targetMethod.Name == nameof(IServerDbManager.AcknowledgeLuaMDeepCryoPublicationAsync))
            {
                var call = Interlocked.Increment(ref _acknowledgementCalls);
                return call == 1
                    ? CommitAcknowledgementAndPauseResultAsync(args!)
                    : targetMethod.Invoke(Inner, args);
            }
            if (targetMethod.Name == nameof(IServerDbManager.ReleaseLuaMCharacterPresenceAsync))
                Interlocked.Increment(ref _presenceReleaseCalls);
            if (PauseFirstPresenceRenewal &&
                targetMethod.Name == nameof(IServerDbManager.RenewLuaMCharacterPresenceAsync))
            {
                var call = Interlocked.Increment(ref _presenceRenewalCalls);
                return call == 1
                    ? CommitRenewalAndPauseResultAsync(args!)
                    : targetMethod.Invoke(Inner, args);
            }
            if (FailAcknowledgedQuarantineAttempts > 0 &&
                targetMethod.Name == nameof(IServerDbManager.QuarantineAcknowledgedLuaMDeepCryoPublicationAsync))
            {
                var call = Interlocked.Increment(ref _quarantineCalls);
                return call <= FailAcknowledgedQuarantineAttempts
                    ? Task.FromResult(new LuaMDeepCryoWriteResult(LuaMDeepCryoWriteStatus.InvalidRequest))
                    : targetMethod.Invoke(Inner, args);
            }
            if (CommitQuarantineThenHideFirstOutcome &&
                targetMethod.Name == nameof(IServerDbManager.QuarantineAcknowledgedLuaMDeepCryoPublicationAsync))
            {
                var call = Interlocked.Increment(ref _quarantineCalls);
                return call == 1
                    ? CommitQuarantineThenThrowAsync(args!)
                    : call == 2
                        ? Task.FromException<LuaMDeepCryoWriteResult>(
                            new InvalidOperationException("simulated hidden acknowledged quarantine replay"))
                        : targetMethod.Invoke(Inner, args);
            }
            if (CommitQuarantineThenHideFirstOutcome &&
                Volatile.Read(ref _quarantineCalls) >= 2 &&
                targetMethod.Name == nameof(IServerDbManager.GetLuaMDeepCryoSnapshotAsync) &&
                Interlocked.CompareExchange(ref _hiddenQuarantineSnapshotReads, 1, 0) == 0)
            {
                return Task.FromException<LuaMDeepCryoSnapshotRecord?>(
                    new InvalidOperationException("simulated hidden acknowledged quarantine state"));
            }

            return targetMethod.Invoke(Inner, args);
        }

        private async Task<LuaMDeepCryoWriteResult> CommitQuarantineThenThrowAsync(object?[] args)
        {
            _ = await Inner.QuarantineAcknowledgedLuaMDeepCryoPublicationAsync(
                    (LuaMDeepCryoQuarantineAcknowledgedPublicationRequest) args[0]!,
                    args.Length > 1 ? (CancellationToken) args[1]! : default)
                .ConfigureAwait(false);
            throw new InvalidOperationException("simulated acknowledged quarantine commit-then-throw");
        }

        private async Task<LuaMCharacterPresenceWriteResult> CommitRenewalAndPauseResultAsync(object?[] args)
        {
            var result = await Inner.RenewLuaMCharacterPresenceAsync(
                    (LuaMCharacterPresenceRenewRequest) args[0]!,
                    args.Length > 1 ? (CancellationToken) args[1]! : default)
                .ConfigureAwait(false);
            RenewalCommitted.TrySetResult(true);
            await _releaseRenewalResult.Task.ConfigureAwait(false);
            RenewalReturned.TrySetResult(true);
            return result;
        }

        private async Task<LuaMDeepCryoWriteResult> CommitAcknowledgementAndPauseResultAsync(object?[] args)
        {
            var result = await Inner.AcknowledgeLuaMDeepCryoPublicationAsync(
                    (LuaMDeepCryoAcknowledgePublicationRequest) args[0]!,
                    args.Length > 1 ? (CancellationToken) args[1]! : default)
                .ConfigureAwait(false);
            AcknowledgementCommitted.TrySetResult(true);
            await _releaseAcknowledgementResult.Task.ConfigureAwait(false);
            AcknowledgementReturned.TrySetResult(true);
            return result;
        }
    }

    [Virtual]
    public class PausedCryoCompleteProxy : DispatchProxy
    {
        private readonly TaskCompletionSource<bool> _releaseCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _completionCalls;
        private int _abortCalls;
        private int _authorizationCalls;

        public IServerDbManager Inner { get; set; } = default!;
        public bool ThrowAfterFirstAuthorizationCommit { get; set; }
        public TaskCompletionSource<bool> CompletionCommitted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CompletionCalls => Volatile.Read(ref _completionCalls);
        public int AbortCalls => Volatile.Read(ref _abortCalls);
        public int AuthorizationCalls => Volatile.Read(ref _authorizationCalls);

        public void ReleaseCompletionResult()
        {
            _releaseCompletion.TrySetResult(true);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null)
                throw new InvalidOperationException("Deep-cryo database proxy received no target method.");
            if (targetMethod.Name == nameof(IServerDbManager.CompleteLuaMDeepCryoRestoreAsync))
                return CompleteAndPauseAsync(args!);
            if (targetMethod.Name == nameof(IServerDbManager.AuthorizeLuaMDeepCryoPublicationAsync) &&
                ThrowAfterFirstAuthorizationCommit)
            {
                return AuthorizeCommitThenThrowOnceAsync(args!);
            }
            if (targetMethod.Name == nameof(IServerDbManager.AbortLuaMDeepCryoRestoreAsync))
                Interlocked.Increment(ref _abortCalls);

            return targetMethod.Invoke(Inner, args);
        }

        private async Task<LuaMDeepCryoWriteResult> CompleteAndPauseAsync(object?[] args)
        {
            Interlocked.Increment(ref _completionCalls);
            var result = await Inner.CompleteLuaMDeepCryoRestoreAsync(
                    (LuaMDeepCryoCompleteRequest) args[0]!,
                    args.Length > 1 ? (CancellationToken) args[1]! : default)
                .ConfigureAwait(false);
            CompletionCommitted.TrySetResult(true);
            await _releaseCompletion.Task.ConfigureAwait(false);
            return result;
        }

        private async Task<LuaMDeepCryoWriteResult> AuthorizeCommitThenThrowOnceAsync(object?[] args)
        {
            var call = Interlocked.Increment(ref _authorizationCalls);
            var result = await Inner.AuthorizeLuaMDeepCryoPublicationAsync(
                    (LuaMDeepCryoAuthorizePublicationRequest) args[0]!,
                    args.Length > 1 ? (CancellationToken) args[1]! : default)
                .ConfigureAwait(false);
            if (call == 1)
                throw new InvalidOperationException("simulated AUTH commit-then-throw");
            return result;
        }
    }

    [Virtual]
    public class UnprovenUnknownCryoCompleteProxy : DispatchProxy
    {
        private int _completionCalls;

        public IServerDbManager Inner { get; set; } = default!;
        public int CompletionCalls => Volatile.Read(ref _completionCalls);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null)
                throw new InvalidOperationException("Deep-cryo database proxy received no target method.");
            if (targetMethod.Name == nameof(IServerDbManager.CompleteLuaMDeepCryoRestoreAsync))
                return ReturnUnprovenUnknownAsync(args!);

            return targetMethod.Invoke(Inner, args);
        }

        private async Task<LuaMDeepCryoWriteResult> ReturnUnprovenUnknownAsync(object?[] args)
        {
            Interlocked.Increment(ref _completionCalls);
            var request = (LuaMDeepCryoCompleteRequest) args[0]!;
            var current = await Inner.GetLuaMDeepCryoSnapshotAsync(
                    request.UserId,
                    request.ProfileId,
                    request.Slot,
                    args.Length > 1 ? (CancellationToken) args[1]! : default)
                .ConfigureAwait(false);
            if (current == null)
                return new LuaMDeepCryoWriteResult(LuaMDeepCryoWriteStatus.UnknownOutcome);

            var fabricated = current with
            {
                Status = DbLuaMDeepCryoSnapshotStatus.Consumed,
                Revision = request.ExpectedRevision + 1,
                UpdatedAtUtc = request.CompletedAtUtc,
                ConsumedAtUtc = request.CompletedAtUtc,
                LeaseId = request.LeaseId,
            };
            return new LuaMDeepCryoWriteResult(
                LuaMDeepCryoWriteStatus.UnknownOutcome,
                request.SnapshotId,
                fabricated.Revision,
                fabricated.Status,
                request.LeaseId,
                fabricated);
        }
    }
}
