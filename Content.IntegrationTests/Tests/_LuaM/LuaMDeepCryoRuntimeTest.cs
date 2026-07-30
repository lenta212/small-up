using Content.Server.Ghost;
using Content.Server.Mind;
using Content.Server._LuaM.Cryo;
using Content.Server._NF.CryoSleep;
using Content.Shared.Ghost;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared._NF.CryoSleep;
using NUnit.Framework;
using Robust.Server.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Player;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMDeepCryoRuntimeTest
{
    [TestPrototypes]
    private const string Prototypes = @"
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
  name: deep cryo test pod
  components:
  - type: CryoSleep
  - type: CryoSleepFallback
  - type: ContainerContainer
    containers:
      body_container: !type:ContainerSlot
";

    [Test]
    public void DurableBodyPersistenceIsDisabled()
    {
        Assert.That(LuaMDeepCryoPersistenceSystem.PersistenceEnabled, Is.False);
    }

    [Test]
    public async Task EjectClosesEuiAndLateAcceptCannotStoreBody()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var players = server.ResolveDependency<IPlayerManager>();
        var minds = entities.System<MindSystem>();
        var sleep = entities.System<CryoSleepSystem>();
        var testMap = await pair.CreateTestMap();
        var session = GetSession(pair, players);

        await server.WaitAssertion(() =>
        {
            var (body, pod, episode) = StartConnectedEpisode(
                entities,
                players,
                minds,
                sleep,
                session,
                testMap.GridCoords,
                nameof(EjectClosesEuiAndLateAcceptCannotStoreBody));
            Assert.That(sleep.TryGetCryoStoreEui(pod, body, out var eui), Is.True);
            Assert.That(eui, Is.Not.Null);

            Assert.That(sleep.EjectBody(pod, body: body), Is.True);
            Assert.That(eui!.IsShutDown, Is.True, "Ejecting must close the stale confirmation EUI.");

            Assert.DoesNotThrow(() =>
                eui.HandleMessage(new AcceptCryoChoiceMessage(AcceptCryoUiButton.Accept)));
            Assert.Multiple(() =>
            {
                Assert.That(entities.EntityExists(body), Is.True);
                Assert.That(entities.GetComponent<TransformComponent>(body).ParentUid, Is.Not.EqualTo(pod));
                Assert.That(sleep.CryoStoreBody(body, pod, episode), Is.False);
            });

            var cryo = entities.GetComponent<CryoSleepComponent>(pod);
            Assert.That(sleep.InsertBody(body, cryo, false), Is.True);
            Assert.That(sleep.TryGetCryoStoreEpisode(pod, body, out var newEpisode), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(newEpisode, Is.Not.EqualTo(episode));
                Assert.That(sleep.EjectBody(pod, body: body, episodeId: episode), Is.False,
                    "A stale deny callback must not eject a newer stay of the same body.");
                Assert.That(cryo.BodyContainer.ContainedEntity, Is.EqualTo(body));
            });
            Assert.That(sleep.EjectBody(pod, body: body, episodeId: newEpisode), Is.True);
        });

        await pair.CleanReturnAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DeletedBodyOrPodMakesQueuedCallbackAClosedNoOp(bool deletePod)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var players = server.ResolveDependency<IPlayerManager>();
        var minds = entities.System<MindSystem>();
        var sleep = entities.System<CryoSleepSystem>();
        var testMap = await pair.CreateTestMap();
        var session = GetSession(pair, players);

        await server.WaitAssertion(() =>
        {
            var (body, pod, episode) = StartConnectedEpisode(
                entities,
                players,
                minds,
                sleep,
                session,
                testMap.GridCoords,
                nameof(DeletedBodyOrPodMakesQueuedCallbackAClosedNoOp));
            Assert.That(sleep.TryGetCryoStoreEui(pod, body, out var eui), Is.True);

            var deleted = deletePod ? pod : body;
            entities.DeleteEntity(deleted);

            Assert.Multiple(() =>
            {
                Assert.That(entities.EntityExists(deleted), Is.False);
                Assert.That(eui!.IsShutDown, Is.True, "Deleting either callback UID must invalidate its EUI.");
                Assert.That(sleep.CryoStoreBody(body, pod, episode), Is.False);
            });
            Assert.DoesNotThrow(() =>
                eui!.HandleMessage(new AcceptCryoChoiceMessage(AcceptCryoUiButton.Accept)));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ManualAndAutomaticCallbacksFinalizeOnlyOnce()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var sleep = entities.System<CryoSleepSystem>();
        var testMap = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var (body, pod, episode) = StartEpisode(entities, sleep, testMap.GridCoords);
            var manual = new CryoSleepEui(body, pod, episode, sleep);

            manual.HandleMessage(new AcceptCryoChoiceMessage(AcceptCryoUiButton.Accept));
            var storedParent = entities.GetComponent<TransformComponent>(body).ParentUid;

            Assert.Multiple(() =>
            {
                Assert.That(storedParent, Is.Not.EqualTo(pod), "The manual callback must finalize the episode.");
                Assert.That(sleep.CryoStoreBody(body, pod, episode), Is.False,
                    "The delayed automatic callback must observe the episode as already claimed.");
                Assert.That(entities.GetComponent<TransformComponent>(body).ParentUid, Is.EqualTo(storedParent));
                Assert.That(entities.GetComponent<CryoSleepComponent>(pod).BodyContainer.ContainedEntity, Is.Null);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task StaleExpiryCannotRemoveNewEpisodeForSameBody()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var players = server.ResolveDependency<IPlayerManager>();
        var minds = entities.System<MindSystem>();
        var sleep = entities.System<CryoSleepSystem>();
        var testMap = await pair.CreateTestMap();
        var session = GetSession(pair, players);

        await server.WaitAssertion(() =>
        {
            var (body, pod, firstEpisode) = StartConnectedEpisode(
                entities,
                players,
                minds,
                sleep,
                session,
                testMap.GridCoords,
                nameof(StaleExpiryCannotRemoveNewEpisodeForSameBody));
            Assert.That(sleep.CryoStoreBody(body, pod, firstEpisode), Is.True);

            var mindId = minds.GetMind(session.UserId);
            Assert.That(mindId, Is.Not.Null);
            var mind = entities.GetComponent<MindComponent>(mindId!.Value);
            Assert.That(
                sleep.TryReturnToBody(mind, force: true),
                Is.EqualTo(SharedCryoSleepSystem.ReturnToBodyStatus.Success));
            Assert.That(sleep.EjectBody(pod, body: body), Is.True);

            var cryo = entities.GetComponent<CryoSleepComponent>(pod);
            Assert.That(sleep.InsertBody(body, cryo, false), Is.True);
            Assert.That(sleep.TryGetCryoStoreEpisode(pod, body, out var secondEpisode), Is.True);
            Assert.That(secondEpisode, Is.Not.EqualTo(firstEpisode));
            Assert.That(sleep.CryoStoreBody(body, pod, secondEpisode), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(sleep.ResetCryosleepState(session.UserId, body, firstEpisode), Is.False);
                Assert.That(sleep.HasCryosleepingBody(session.UserId), Is.True);
                Assert.That(entities.EntityExists(body), Is.True);
            });

            Assert.That(sleep.ResetCryosleepState(session.UserId, body, secondEpisode), Is.True);
            Assert.That(sleep.HasCryosleepingBody(session.UserId), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task VisitingGhostStoresAndReturnsOwnedBodyNotCurrentEntity()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var players = server.ResolveDependency<IPlayerManager>();
        var minds = entities.System<MindSystem>();
        var ghosts = entities.System<GhostSystem>();
        var sleep = entities.System<CryoSleepSystem>();
        var testMap = await pair.CreateTestMap();
        var session = GetSession(pair, players);

        await server.WaitAssertion(() =>
        {
            var pod = entities.SpawnEntity("LuaMDeepCryoTestSleepPod", testMap.GridCoords);
            var body = entities.SpawnEntity("LuaMDeepCryoTestBody", testMap.GridCoords);
            var (mindId, mind) = AttachSessionToBody(
                entities,
                players,
                minds,
                session,
                body,
                nameof(VisitingGhostStoresAndReturnsOwnedBodyNotCurrentEntity));
            var visitingGhost = ghosts.SpawnGhost((mindId, mind), body, canReturn: true);
            Assert.That(visitingGhost, Is.Not.Null);

            var visitingMind = entities.GetComponent<MindComponent>(mindId);
            Assert.Multiple(() =>
            {
                Assert.That(visitingMind.OwnedEntity, Is.EqualTo(body));
                Assert.That(visitingMind.CurrentEntity, Is.EqualTo(visitingGhost));
            });

            var cryo = entities.GetComponent<CryoSleepComponent>(pod);
            Assert.That(sleep.InsertBody(body, cryo, false), Is.True);
            Assert.That(sleep.TryGetCryoStoreEpisode(pod, body, out var episode), Is.True);
            Assert.That(sleep.CryoStoreBody(body, pod, episode), Is.True);
            Assert.That(sleep.HasCryosleepingBody(session.UserId), Is.True);

            var storedMind = entities.GetComponent<MindComponent>(mindId);
            Assert.That(
                sleep.TryReturnToBody(storedMind, force: true),
                Is.EqualTo(SharedCryoSleepSystem.ReturnToBodyStatus.Success));

            var returnedMind = entities.GetComponent<MindComponent>(mindId);
            Assert.Multiple(() =>
            {
                Assert.That(returnedMind.OwnedEntity, Is.EqualTo(body));
                Assert.That(returnedMind.CurrentEntity, Is.EqualTo(body));
                Assert.That(entities.GetComponent<CryoSleepComponent>(pod).BodyContainer.ContainedEntity,
                    Is.EqualTo(body));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MismatchedMindOwnershipIsRejectedAndEjected()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var minds = entities.System<MindSystem>();
        var sleep = entities.System<CryoSleepSystem>();
        var testMap = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var ownedBody = entities.SpawnEntity("LuaMDeepCryoTestBody", testMap.GridCoords);
            var staleBody = entities.SpawnEntity("LuaMDeepCryoTestBody", testMap.GridCoords);
            var mind = minds.CreateMind(null, nameof(MismatchedMindOwnershipIsRejectedAndEjected));
            minds.TransferTo(mind.Owner, ownedBody, ghostCheckOverride: true, mind: mind.Comp);

            // Reproduce a stale MindContainer link without changing the mind's
            // authoritative OwnedEntity.
            var staleContainer = entities.GetComponent<MindContainerComponent>(staleBody);
            var mindProperty = typeof(MindContainerComponent).GetProperty(nameof(MindContainerComponent.Mind));
            Assert.That(mindProperty, Is.Not.Null);
            mindProperty!.SetValue(staleContainer, mind.Owner);

            var pod = entities.SpawnEntity("LuaMDeepCryoTestSleepPod", testMap.GridCoords);
            var cryo = entities.GetComponent<CryoSleepComponent>(pod);
            Assert.That(sleep.InsertBody(staleBody, cryo, false), Is.True);
            Assert.That(sleep.TryGetCryoStoreEpisode(pod, staleBody, out var episode), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(sleep.CryoStoreBody(staleBody, pod, episode), Is.False);
                Assert.That(entities.EntityExists(staleBody), Is.True);
                Assert.That(entities.GetComponent<TransformComponent>(staleBody).ParentUid, Is.Not.EqualTo(pod));
                Assert.That(sleep.TryGetCryoStoreEpisode(pod, staleBody, out _), Is.False);
                Assert.That(entities.GetComponent<MindComponent>(mind.Owner).OwnedEntity, Is.EqualTo(ownedBody));
            });
        });

        await pair.CleanReturnAsync();
    }

    private static (EntityUid Body, EntityUid Pod, Guid Episode) StartEpisode(
        IEntityManager entities,
        CryoSleepSystem sleep,
        EntityCoordinates coordinates)
    {
        var pod = entities.SpawnEntity("LuaMDeepCryoTestSleepPod", coordinates);
        var body = entities.SpawnEntity("LuaMDeepCryoTestBody", coordinates);
        var cryo = entities.GetComponent<CryoSleepComponent>(pod);
        Assert.That(sleep.InsertBody(body, cryo, false), Is.True);
        Assert.That(sleep.TryGetCryoStoreEpisode(pod, body, out var episode), Is.True);
        return (body, pod, episode);
    }

    private static (EntityUid Body, EntityUid Pod, Guid Episode) StartConnectedEpisode(
        IEntityManager entities,
        IPlayerManager players,
        MindSystem minds,
        CryoSleepSystem sleep,
        ICommonSession session,
        EntityCoordinates coordinates,
        string characterName)
    {
        var pod = entities.SpawnEntity("LuaMDeepCryoTestSleepPod", coordinates);
        var body = entities.SpawnEntity("LuaMDeepCryoTestBody", coordinates);
        AttachSessionToBody(entities, players, minds, session, body, characterName);
        var cryo = entities.GetComponent<CryoSleepComponent>(pod);
        Assert.That(sleep.InsertBody(body, cryo, false), Is.True);
        Assert.That(sleep.TryGetCryoStoreEpisode(pod, body, out var episode), Is.True);
        return (body, pod, episode);
    }

    private static ICommonSession GetSession(
        Content.IntegrationTests.Pair.TestPair pair,
        IPlayerManager players)
    {
        var clientSession = pair.Client.Session;
        Assert.That(clientSession, Is.Not.Null);
        return players.GetSessionById(clientSession!.UserId);
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
}
