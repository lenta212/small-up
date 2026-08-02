using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Content.Server._LuaM.ShipPersistence;
using Content.Server.Materials;
using Content.Shared.Materials;
using Content.Shared.Materials.OreSilo;
using Content.Shared.Maps;
using Robust.Server.GameStates;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Player;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMOreSiloShipPersistenceRuntimeTest
{
    private const string SiloPrototype = "LuaMOreSiloPersistenceSilo";
    private const string ClientPrototype = "LuaMOreSiloPersistenceClient";
    private const string ActorPrototype = "LuaMOreSiloPersistenceActor";

    public enum PvsCacheInvalidation
    {
        RangeExit,
        GridChange,
        Unlink,
        Detach,
    }

    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  id: LuaMOreSiloPersistenceSilo
  name: LuaM persistence material silo
  components:
  - type: Transform
  - type: OreSilo
    range: 125
  - type: MaterialStorage
    storageLimit: 10000
  - type: MaterialStorageMagnetPickup

- type: entity
  id: LuaMOreSiloPersistenceClient
  name: LuaM persistence silo client
  components:
  - type: Transform
  - type: OreSiloClient
  - type: MaterialStorage
    storageLimit: 10000

- type: entity
  id: LuaMOreSiloPersistenceActor
  name: LuaM persistence silo actor
  components:
  - type: Transform
";

    [Test]
    public async Task LinkedMaterialsRemainUsableAfterFullShipRoundTrip()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var maps = entities.System<SharedMapSystem>();
        var materials = entities.System<SharedMaterialStorageSystem>();
        var persistence = entities.System<LuaMFullShipPersistenceSystem>();

        MapId sourceMap = default;
        MapId targetMap = default;

        try
        {
            await server.WaitPost(() =>
            {
                maps.CreateMap(out sourceMap);
                var sourceGrid = mapManager.CreateGridEntity(sourceMap).Owner;
                maps.SetTile(sourceGrid, entities.GetComponent<MapGridComponent>(sourceGrid), Vector2i.Zero, new Tile(1));

                var silo = entities.SpawnEntity(
                    SiloPrototype,
                    new EntityCoordinates(sourceGrid, new Vector2(0.5f, 0.5f)));
                var client = entities.SpawnEntity(
                    ClientPrototype,
                    new EntityCoordinates(sourceGrid, new Vector2(0.75f, 0.5f)));
                Link(entities, silo, client);
                entities.GetComponent<Content.Server.Storage.Components.MaterialStorageMagnetPickupComponent>(silo)
                    .MagnetEnabled = true;

                Assert.Multiple(() =>
                {
                    Assert.That(materials.TryChangeMaterialAmount(silo, "Steel", 1200, localOnly: true), Is.True);
                    Assert.That(materials.TryChangeMaterialAmount(silo, "Glass", 700, localOnly: true), Is.True);
                });

                Assert.That(
                    persistence.TryCaptureSnapshot(sourceGrid, 1, out var snapshot, out var captureReason),
                    Is.True,
                    captureReason);

                entities.DeleteEntity(sourceGrid);
                maps.CreateMap(out targetMap);
                Assert.That(
                    persistence.TryRestoreSnapshot(snapshot, targetMap, out var restoredGrid, out var restoreReason),
                    Is.True,
                    restoreReason);

                var restoredSilo = RequirePrototypeOnGrid(entities, restoredGrid, SiloPrototype);
                var restoredClient = RequirePrototypeOnGrid(entities, restoredGrid, ClientPrototype);
                var siloComp = entities.GetComponent<OreSiloComponent>(restoredSilo);
                var clientComp = entities.GetComponent<OreSiloClientComponent>(restoredClient);

                Assert.Multiple(() =>
                {
                    Assert.That(clientComp.Silo, Is.EqualTo(restoredSilo));
                    Assert.That(siloComp.Clients, Is.EquivalentTo(new[] { restoredClient }));
                    Assert.That(materials.GetMaterialAmount(restoredSilo, "Steel", localOnly: true), Is.EqualTo(1200));
                    Assert.That(materials.GetMaterialAmount(restoredSilo, "Glass", localOnly: true), Is.EqualTo(700));
                    Assert.That(materials.GetMaterialAmount(restoredClient, "Steel"), Is.EqualTo(1200));
                    Assert.That(materials.GetMaterialAmount(restoredClient, "Glass"), Is.EqualTo(700));
                    Assert.That(
                        entities.GetComponent<Content.Server.Storage.Components.MaterialStorageMagnetPickupComponent>(restoredSilo)
                            .MagnetEnabled,
                        Is.True,
                        "An enabled fabrication/storage magnet must remain enabled after a ship round-trip.");
                });

                Assert.That(materials.TryChangeMaterialAmount(restoredClient, "Steel", -200), Is.True);
                Assert.Multiple(() =>
                {
                    Assert.That(materials.GetMaterialAmount(restoredSilo, "Steel", localOnly: true), Is.EqualTo(1000));
                    Assert.That(materials.GetMaterialAmount(restoredSilo, "Glass", localOnly: true), Is.EqualTo(700));
                    Assert.That(materials.GetTotalMaterialAmount(restoredClient, localOnly: true), Is.Zero,
                        "A linked machine must consume from the restored silo instead of duplicating materials locally.");
                });
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                if (maps.MapExists(sourceMap))
                    maps.DeleteMap(sourceMap);
                if (maps.MapExists(targetMap))
                    maps.DeleteMap(targetMap);
            });
            pair.Kill();
        }
    }

    [Test]
    public async Task UpdateIgnoresActorWithoutAttachedSession()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var maps = entities.System<SharedMapSystem>();
        var siloSystem = entities.System<OreSiloSystem>();

        MapId mapId = default;

        try
        {
            await server.WaitPost(() =>
            {
                maps.CreateMap(out mapId);
                var grid = mapManager.CreateGridEntity(mapId).Owner;
                maps.SetTile(grid, entities.GetComponent<MapGridComponent>(grid), Vector2i.Zero, new Tile(1));

                var actor = entities.SpawnEntity(
                    ActorPrototype,
                    new EntityCoordinates(grid, new Vector2(0.25f, 0.5f)));
                var actorComp = entities.EnsureComponent<ActorComponent>(actor);
                Assert.That(actorComp.PlayerSession, Is.Null,
                    "The fixture must represent an ActorComponent before a session is attached.");

                Assert.DoesNotThrow(() => siloSystem.Update(1.1f),
                    "Unattached actors must not enter the session-keyed ore-silo PVS cache.");
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                if (maps.MapExists(mapId))
                    maps.DeleteMap(mapId);
            });
            pair.Kill();
        }
    }

    [TestCase(PvsCacheInvalidation.RangeExit)]
    [TestCase(PvsCacheInvalidation.GridChange)]
    [TestCase(PvsCacheInvalidation.Unlink)]
    [TestCase(PvsCacheInvalidation.Detach)]
    public async Task ExpandPvsCacheTracksTransitionsWithoutTouchingRawOverrides(
        PvsCacheInvalidation invalidation)
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
        });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var maps = entities.System<SharedMapSystem>();
        var transforms = entities.System<SharedTransformSystem>();
        var pvsOverrides = entities.System<PvsOverrideSystem>();
        var siloSystem = entities.System<OreSiloSystem>();
        var session = pair.Player;

        Assert.That(session, Is.Not.Null);
        Assert.That(session!.Status, Is.AnyOf(SessionStatus.Connected, SessionStatus.InGame));

        MapId mapId = default;
        EntityUid actor = EntityUid.Invalid;
        EntityUid silo = EntityUid.Invalid;
        EntityUid client = EntityUid.Invalid;

        try
        {
            await server.WaitPost(() =>
            {
                maps.CreateMap(out mapId);
                var grid = mapManager.CreateGridEntity(mapId).Owner;
                var externalGrid = mapManager.CreateGridEntity(mapId).Owner;
                maps.SetTile(grid, entities.GetComponent<MapGridComponent>(grid), Vector2i.Zero, new Tile(1));
                maps.SetTile(externalGrid, entities.GetComponent<MapGridComponent>(externalGrid), Vector2i.Zero, new Tile(1));
                transforms.SetLocalPosition(externalGrid, new Vector2(100f, 0f));

                silo = entities.SpawnEntity(
                    SiloPrototype,
                    new EntityCoordinates(grid, new Vector2(0.5f, 0.5f)));
                client = entities.SpawnEntity(
                    ClientPrototype,
                    new EntityCoordinates(grid, new Vector2(0.75f, 0.5f)));
                actor = entities.SpawnEntity(
                    ActorPrototype,
                    new EntityCoordinates(grid, new Vector2(0.25f, 0.5f)));
                Link(entities, silo, client);

                Assert.That(server.PlayerMan.SetAttachedEntity(session, actor, true), Is.True);
                Assert.Multiple(() =>
                {
                    Assert.That(session.AttachedEntity, Is.EqualTo(actor));
                    Assert.That(entities.HasComponent<ActorComponent>(actor), Is.True);
                    Assert.That(entities.GetComponent<TransformComponent>(actor).GridUid, Is.EqualTo(grid));
                    Assert.That(entities.GetComponent<TransformComponent>(client).GridUid, Is.EqualTo(grid));
                });

                // Seed a raw override before the ore-silo cache owns anything.
                pvsOverrides.AddSessionOverride(silo, session);
                siloSystem.Update(1.1f);
                var initialExpand = RaiseExpandPvs(entities, actor, session);
                Assert.That(
                    initialExpand.RecursiveEntities?.Contains(silo),
                    Is.True,
                    "A nearby attached actor must receive the linked silo through ExpandPvsEvent.");
                Assert.That(HasSessionOverride(pvsOverrides, session, silo), Is.True,
                    "Building the ore-silo cache must not remove a pre-existing raw override.");

                // The transient projection must keep working independently of the raw override.
                pvsOverrides.RemoveSessionOverride(silo, session);
                Assert.That(HasSessionOverride(pvsOverrides, session, silo), Is.False);
                var cacheOnlyExpand = RaiseExpandPvs(entities, actor, session);
                Assert.That(cacheOnlyExpand.RecursiveEntities?.Contains(silo), Is.True);

                // Now seed a foreign raw override after the ore-silo cache is active.
                pvsOverrides.AddSessionOverride(silo, session);

                switch (invalidation)
                {
                    case PvsCacheInvalidation.RangeExit:
                        transforms.SetLocalPosition(actor, new Vector2(40f, 0.5f));
                        break;
                    case PvsCacheInvalidation.GridChange:
                        transforms.SetParent(actor, externalGrid);
                        transforms.SetLocalPosition(actor, new Vector2(0.25f, 0.5f));
                        break;
                    case PvsCacheInvalidation.Unlink:
                        Unlink(entities, silo, client);
                        break;
                    case PvsCacheInvalidation.Detach:
                        Assert.That(server.PlayerMan.SetAttachedEntity(session, null, true), Is.True);
                        Assert.That(entities.HasComponent<ActorComponent>(actor), Is.False);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(invalidation), invalidation, null);
                }

                siloSystem.Update(1.1f);
                var afterInvalidation = RaiseExpandPvs(entities, actor, session);
                var cachedSilos = GetPrivateField<Dictionary<ICommonSession, HashSet<EntityUid>>>(
                    siloSystem,
                    "_cachedSilosBySession");
                Assert.Multiple(() =>
                {
                    Assert.That(afterInvalidation.RecursiveEntities?.Contains(silo) ?? false, Is.False,
                        $"The silo must stop being projected after {invalidation}.");
                    Assert.That(cachedSilos.ContainsKey(session), Is.False,
                        $"The session cache must be pruned after {invalidation}.");
                    Assert.That(HasSessionOverride(pvsOverrides, session, silo), Is.True,
                        "OreSiloSystem must never remove a foreign raw PVS override.");
                });

                pvsOverrides.RemoveSessionOverride(silo, session);
                Assert.That(HasSessionOverride(pvsOverrides, session, silo), Is.False,
                    "Only the foreign owner's final raw removal should clear the override.");
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                if (session.AttachedEntity is not null)
                    server.PlayerMan.SetAttachedEntity(session, null, true);
                if (maps.MapExists(mapId))
                    maps.DeleteMap(mapId);
            });
            pair.Kill();
        }
    }

    [Test]
    public async Task CrossGridLinkIsClearedSynchronouslyBeforeShipSerialization()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var maps = entities.System<SharedMapSystem>();
        var transforms = entities.System<SharedTransformSystem>();
        var materials = entities.System<SharedMaterialStorageSystem>();
        var persistence = entities.System<LuaMFullShipPersistenceSystem>();

        MapId sourceMap = default;
        MapId targetMap = default;

        try
        {
            await server.WaitPost(() =>
            {
                maps.CreateMap(out sourceMap);
                var sourceGrid = mapManager.CreateGridEntity(sourceMap).Owner;
                var externalGrid = mapManager.CreateGridEntity(sourceMap).Owner;
                maps.SetTile(sourceGrid, entities.GetComponent<MapGridComponent>(sourceGrid), Vector2i.Zero, new Tile(1));
                maps.SetTile(externalGrid, entities.GetComponent<MapGridComponent>(externalGrid), Vector2i.Zero, new Tile(1));
                transforms.SetLocalPosition(externalGrid, new Vector2(100f, 0f));

                var silo = entities.SpawnEntity(
                    SiloPrototype,
                    new EntityCoordinates(sourceGrid, new Vector2(0.5f, 0.5f)));
                var client = entities.SpawnEntity(
                    ClientPrototype,
                    new EntityCoordinates(sourceGrid, new Vector2(0.75f, 0.5f)));
                Link(entities, silo, client);
                Assert.That(materials.TryChangeMaterialAmount(silo, "Steel", 900, localOnly: true), Is.True);

                // Reparenting does not emit an ore-silo unlink. This reproduces
                // a stale reference naturally without writing protected fields.
                transforms.SetParent(silo, externalGrid);
                transforms.SetLocalPosition(silo, new Vector2(0.5f, 0.5f));
                Assert.Multiple(() =>
                {
                    Assert.That(entities.GetComponent<TransformComponent>(silo).GridUid, Is.EqualTo(externalGrid));
                    Assert.That(entities.GetComponent<TransformComponent>(client).GridUid, Is.EqualTo(sourceGrid));
                    Assert.That(entities.GetComponent<OreSiloClientComponent>(client).Silo, Is.EqualTo(silo));
                });

                Assert.That(
                    persistence.TryCaptureSnapshot(sourceGrid, 1, out var snapshot, out var captureReason),
                    Is.True,
                    captureReason);
                var yaml = Encoding.UTF8.GetString(snapshot.Payload);
                Assert.Multiple(() =>
                {
                    Assert.That(entities.GetComponent<OreSiloClientComponent>(client).Silo, Is.Null,
                        "The before-serialization pass must clear a cross-grid pointer synchronously.");
                    Assert.That(entities.GetComponent<OreSiloComponent>(silo).Clients, Is.Empty);
                    Assert.That(yaml, Does.Not.Contain("silo: invalid"));
                    Assert.That(materials.GetMaterialAmount(silo, "Steel", localOnly: true), Is.EqualTo(900));
                });

                entities.DeleteEntity(sourceGrid);
                maps.CreateMap(out targetMap);
                Assert.That(
                    persistence.TryRestoreSnapshot(snapshot, targetMap, out var restoredGrid, out var restoreReason),
                    Is.True,
                    restoreReason);
                var restoredClient = RequirePrototypeOnGrid(entities, restoredGrid, ClientPrototype);
                Assert.Multiple(() =>
                {
                    Assert.That(entities.GetComponent<OreSiloClientComponent>(restoredClient).Silo, Is.Null);
                    Assert.That(materials.GetTotalMaterialAmount(restoredClient, localOnly: true), Is.Zero);
                    Assert.That(materials.GetMaterialAmount(silo, "Steel", localOnly: true), Is.EqualTo(900));
                });
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                if (maps.MapExists(sourceMap))
                    maps.DeleteMap(sourceMap);
                if (maps.MapExists(targetMap))
                    maps.DeleteMap(targetMap);
            });
            pair.Kill();
        }
    }

    [Test]
    public async Task InvalidLegacyRestoredReferenceIsClearedByRuntimeReconciliation()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
        });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var maps = entities.System<SharedMapSystem>();
        var materials = entities.System<SharedMaterialStorageSystem>();
        var persistence = entities.System<LuaMFullShipPersistenceSystem>();
        var session = pair.Player;

        Assert.That(session, Is.Not.Null);

        MapId sourceMap = default;
        MapId targetMap = default;
        EntityUid actor = EntityUid.Invalid;
        EntityUid restoredGrid = EntityUid.Invalid;
        EntityUid restoredSilo = EntityUid.Invalid;
        EntityUid restoredClient = EntityUid.Invalid;

        try
        {
            await server.WaitPost(() =>
            {
                maps.CreateMap(out sourceMap);
                var sourceGrid = mapManager.CreateGridEntity(sourceMap).Owner;
                maps.SetTile(sourceGrid, entities.GetComponent<MapGridComponent>(sourceGrid), Vector2i.Zero, new Tile(1));

                var silo = entities.SpawnEntity(
                    SiloPrototype,
                    new EntityCoordinates(sourceGrid, new Vector2(0.5f, 0.5f)));
                var client = entities.SpawnEntity(
                    ClientPrototype,
                    new EntityCoordinates(sourceGrid, new Vector2(0.75f, 0.5f)));
                Link(entities, silo, client);
                Assert.That(materials.TryChangeMaterialAmount(silo, "Steel", 900, localOnly: true), Is.True);

                Assert.That(
                    persistence.TryCaptureSnapshot(sourceGrid, 1, out var snapshot, out var captureReason),
                    Is.True,
                    captureReason);
                var yaml = Encoding.UTF8.GetString(snapshot.Payload);
                var invalidReference = new Regex(@"(?m)^([ \t]+silo:[ \t]*)\d+[ \t]*\r?$")
                    .Replace(yaml, "${1}invalid", 1);
                Assert.That(invalidReference, Is.Not.EqualTo(yaml),
                    "The fixture must replace the persisted OreSiloClient reference.");
                var invalidPayload = Encoding.UTF8.GetBytes(invalidReference);
                var invalidSnapshot = snapshot with
                {
                    Payload = invalidPayload,
                    PayloadSizeBytes = invalidPayload.Length,
                    PayloadHash = Convert.ToHexString(SHA256.HashData(invalidPayload)).ToLowerInvariant(),
                };

                entities.DeleteEntity(sourceGrid);
                maps.CreateMap(out targetMap);
                Assert.That(
                    persistence.TryRestoreSnapshot(
                        invalidSnapshot,
                        targetMap,
                        out restoredGrid,
                        out var restoreReason),
                    Is.True,
                    restoreReason);

                restoredSilo = RequirePrototypeOnGrid(entities, restoredGrid, SiloPrototype);
                restoredClient = RequirePrototypeOnGrid(entities, restoredGrid, ClientPrototype);
                var unresolved = entities.GetComponent<OreSiloClientComponent>(restoredClient).Silo;
                Assert.That(unresolved, Is.Not.Null,
                    "This must reproduce the non-null invalid reference that previously entered PVS.");
                Assert.That(unresolved!.Value.IsValid(), Is.False);

                actor = entities.SpawnEntity(
                    ActorPrototype,
                    new EntityCoordinates(restoredGrid, new Vector2(0.25f, 0.5f)));
                Assert.That(server.PlayerMan.SetAttachedEntity(session!, actor, true), Is.True);
                var beforeReconciliation = RaiseExpandPvs(entities, actor, session!);
                Assert.That(beforeReconciliation.RecursiveEntities?.Contains(EntityUid.Invalid) ?? false, Is.False,
                    "A restored invalid reference must never be emitted while waiting for reconciliation.");
            });

            await server.WaitRunTicks(server.Timing.TickRate + 2);

            await server.WaitPost(() =>
            {
                var afterReconciliation = RaiseExpandPvs(entities, actor, session!);
                Assert.Multiple(() =>
                {
                    Assert.That(entities.GetComponent<OreSiloClientComponent>(restoredClient).Silo, Is.Null);
                    Assert.That(entities.GetComponent<OreSiloComponent>(restoredSilo).Clients, Is.Empty);
                    Assert.That(materials.GetMaterialAmount(restoredSilo, "Steel", localOnly: true), Is.EqualTo(900));
                    Assert.That(materials.GetTotalMaterialAmount(restoredClient, localOnly: true), Is.Zero);
                    Assert.That(afterReconciliation.RecursiveEntities?.Contains(EntityUid.Invalid) ?? false, Is.False,
                        "A connected actor's ExpandPvsEvent must never receive an invalid silo UID.");
                });
            });

            await server.WaitRunTicks(server.Timing.TickRate + 2);

            await server.WaitPost(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(entities.GetComponent<OreSiloClientComponent>(restoredClient).Silo, Is.Null,
                        "Link reconciliation must be idempotent.");
                    Assert.That(entities.GetComponent<OreSiloComponent>(restoredSilo).Clients, Is.Empty);
                    Assert.That(materials.GetMaterialAmount(restoredSilo, "Steel", localOnly: true), Is.EqualTo(900));
                });
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                if (session!.AttachedEntity is not null)
                    server.PlayerMan.SetAttachedEntity(session, null, true);
                if (maps.MapExists(sourceMap))
                    maps.DeleteMap(sourceMap);
                if (maps.MapExists(targetMap))
                    maps.DeleteMap(targetMap);
            });
            pair.Kill();
        }
    }

    private static void Link(IEntityManager entities, EntityUid silo, EntityUid client)
    {
        var netClient = entities.GetNetEntity(client);
        Assert.That(entities.GetEntity(netClient), Is.EqualTo(client));
        var siloSystem = entities.System<OreSiloSystem>();
        var siloXform = entities.GetComponent<TransformComponent>(silo);
        var clientXform = entities.GetComponent<TransformComponent>(client);
        Assert.That(
            siloSystem.CanTransmitMaterials((silo, null, siloXform), client),
            Is.True,
            $"The fixture must be linkable: siloGrid={siloXform.GridUid}, clientGrid={clientXform.GridUid}, " +
            $"siloMap={siloXform.MapID}, clientMap={clientXform.MapID}.");

        var message = new ToggleOreSiloClientMessage(netClient);
        entities.EventBus.RaiseLocalEvent(silo, message);
        Assert.Multiple(() =>
        {
            Assert.That(entities.GetComponent<OreSiloClientComponent>(client).Silo, Is.EqualTo(silo));
            Assert.That(entities.GetComponent<OreSiloComponent>(silo).Clients, Contains.Item(client));
        });
    }

    private static void Unlink(IEntityManager entities, EntityUid silo, EntityUid client)
    {
        var message = new ToggleOreSiloClientMessage(entities.GetNetEntity(client));
        entities.EventBus.RaiseLocalEvent(silo, message);
        Assert.Multiple(() =>
        {
            Assert.That(entities.GetComponent<OreSiloClientComponent>(client).Silo, Is.Null);
            Assert.That(entities.GetComponent<OreSiloComponent>(silo).Clients, Does.Not.Contain(client));
        });
    }

    private static ExpandPvsEvent RaiseExpandPvs(
        IEntityManager entities,
        EntityUid actor,
        ICommonSession session)
    {
        var expand = new ExpandPvsEvent(session, -1);
        entities.EventBus.RaiseLocalEvent(actor, ref expand, true);
        return expand;
    }

    private static bool HasSessionOverride(
        PvsOverrideSystem pvsOverrides,
        ICommonSession session,
        EntityUid uid)
    {
        var overrides = GetPrivateField<Dictionary<ICommonSession, HashSet<EntityUid>>>(
            pvsOverrides,
            "SessionOverrides");
        return overrides.TryGetValue(session, out var sessionOverrides) && sessionOverrides.Contains(uid);
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing field {instance.GetType().Name}.{fieldName}.");

        var value = field!.GetValue(instance);
        Assert.That(value, Is.InstanceOf<T>(), $"Unexpected type for {instance.GetType().Name}.{fieldName}.");
        return (T) value!;
    }

    private static EntityUid RequirePrototypeOnGrid(
        IEntityManager entities,
        EntityUid grid,
        string prototype)
    {
        foreach (var uid in entities.GetEntities())
        {
            if (!entities.TryGetComponent<TransformComponent>(uid, out var xform) ||
                xform.GridUid != grid ||
                entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID != prototype)
            {
                continue;
            }

            return uid;
        }

        Assert.Fail($"Prototype {prototype} was not restored on grid {grid}.");
        return EntityUid.Invalid;
    }
}
