using System.Numerics;
using System.Linq;
using Content.Server._Mono.GridClaimer;
using Content.Server._NF.StationEvents.Components;
using Content.Server._NF.StationEvents.Events;
using Content.Server.Salvage.Expeditions;
using Content.Server.StationEvents;
using Content.Server.StationEvents.Components;
using Content.Shared._NF.Bank.Components;
using Content.Shared.Maps;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMBluespacePlanetoidLifecycleTest
{
    private static readonly string[] PlanetoidEvents =
    {
        "BluespaceDungeonBasalt",
        "BluespaceDungeonChromite",
        "BluespaceDungeonSnow",
        "BluespaceDungeonCave",
        "BluespaceDungeonScrap",
    };

    [Test]
    public async Task HeavyPlanetoidsRespectEntityCeilingAndSalvageExpeditions()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var componentFactory = server.ResolveDependency<IComponentFactory>();
        var eventManager = entities.System<EventManagerSystem>();
        EntityUid salvageExpedition = default;

        try
        {
            await server.WaitAssertion(() =>
            {
                foreach (var eventId in PlanetoidEvents)
                {
                    var prototype = prototypes.Index<EntityPrototype>(eventId);
                    Assert.That(
                        prototype.TryGetComponent<StationEventComponent>(out var stationEvent, componentFactory),
                        Is.True,
                        $"{eventId} must remain a station event.");

                    Assert.Multiple(() =>
                    {
                        Assert.That(stationEvent.MinimumPlayers, Is.EqualTo(2), eventId);
                        Assert.That(stationEvent.MaximumTotalEntities, Is.EqualTo(200_000), eventId);
                        Assert.That(stationEvent.BlockDuringSalvageExpedition, Is.True, eventId);
                    });
                }

                AssertPlanetoidAvailability(eventManager, expected: true, totalEntities: 199_999);
                AssertPlanetoidAvailability(eventManager, expected: false, totalEntities: 200_000);
            });

            await server.WaitPost(() =>
            {
                salvageExpedition = entities.SpawnEntity(null, MapCoordinates.Nullspace);
                entities.AddComponent<SalvageExpeditionComponent>(salvageExpedition);
                AssertPlanetoidAvailability(eventManager, expected: false, totalEntities: 199_999);
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                if (entities.EntityExists(salvageExpedition))
                    entities.DeleteEntity(salvageExpedition);
            });

            await pair.RunTicksSync(5);
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task CleanupPreservesClaimedMapAndContinuesThroughRemainingGrids()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var maps = entities.System<SharedMapSystem>();
        var ruleSystem = entities.System<BluespaceErrorRule>();

        EntityUid ruleUid = default;
        EntityUid claimedGrid = default;
        EntityUid occupiedGrid = default;
        EntityUid emptyDisposableGrid = default;
        EntityUid player = default;
        EntityUid carriedItem = default;
        MapId claimedMap = default;
        MapId occupiedMap = default;
        MapId emptyDisposableMap = default;
        MapId orphanMap = default;

        try
        {
            await server.WaitPost(() =>
            {
                ruleUid = entities.SpawnEntity(null, MapCoordinates.Nullspace);
                var rule = entities.AddComponent<BluespaceErrorRuleComponent>(ruleUid);

                maps.CreateMap(out claimedMap);
                claimedGrid = mapManager.CreateGridEntity(claimedMap).Owner;
                var claimable = entities.AddComponent<ClaimableGridComponent>(claimedGrid);
                claimable.ClaimedBy.Add(ruleUid);

                maps.CreateMap(out occupiedMap);
                var occupiedGridEntity = mapManager.CreateGridEntity(occupiedMap);
                occupiedGrid = occupiedGridEntity.Owner;
                maps.SetTile(occupiedGridEntity.Owner, occupiedGridEntity.Comp, Vector2i.Zero, new Tile(1));

                var playerCoordinates = new EntityCoordinates(occupiedGrid, new Vector2(0.5f, 0.5f));
                player = entities.SpawnEntity("MobHuman", playerCoordinates);
                entities.EnsureComponent<BankAccountComponent>(player);
                carriedItem = entities.SpawnEntity("Crowbar", new EntityCoordinates(player, Vector2.Zero));

                maps.CreateMap(out emptyDisposableMap);
                emptyDisposableGrid = mapManager.CreateGridEntity(emptyDisposableMap).Owner;
                maps.CreateMap(out orphanMap);

                // Keep the claimed grid first: the old early return leaked everything after this entry.
                ruleSystem.TrackGrid(rule, claimedGrid);
                ruleSystem.TrackGrid(rule, occupiedGrid);
                ruleSystem.TrackGrid(rule, emptyDisposableGrid);
                ruleSystem.TrackTemporaryMap(rule, claimedMap);
                ruleSystem.TrackTemporaryMap(rule, occupiedMap);
                ruleSystem.TrackTemporaryMap(rule, emptyDisposableMap);
                ruleSystem.TrackTemporaryMap(rule, orphanMap);

                ruleSystem.CleanupTrackedGrids(rule, awardRewards: false);
                ruleSystem.CleanupTrackedGrids(rule, awardRewards: false);
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                var rule = entities.GetComponent<BluespaceErrorRuleComponent>(ruleUid);
                Assert.Multiple(() =>
                {
                    Assert.That(entities.EntityExists(claimedGrid), Is.True);
                    Assert.That(maps.MapExists(claimedMap), Is.True);
                    Assert.That(entities.EntityExists(occupiedGrid), Is.False);
                    Assert.That(entities.EntityExists(player), Is.True);
                    Assert.That(entities.EntityExists(carriedItem), Is.True);
                    Assert.That(entities.GetComponent<TransformComponent>(player).MapID, Is.EqualTo(occupiedMap));
                    Assert.That(maps.MapExists(occupiedMap), Is.True);
                    Assert.That(entities.EntityExists(emptyDisposableGrid), Is.False);
                    Assert.That(maps.MapExists(emptyDisposableMap), Is.False);
                    Assert.That(maps.MapExists(orphanMap), Is.False);
                    Assert.That(rule.GridsUid, Is.Empty);
                    Assert.That(rule.MapsUid, Is.Empty);
                    Assert.That(rule.CleanupCompleted, Is.True);
                    Assert.That(rule.CleanupInProgress, Is.False);
                });
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                if (entities.EntityExists(ruleUid))
                    entities.DeleteEntity(ruleUid);

                if (maps.MapExists(claimedMap))
                    maps.DeleteMap(claimedMap);

                if (maps.MapExists(occupiedMap))
                    maps.DeleteMap(occupiedMap);
            });

            await pair.RunTicksSync(5);
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task ComponentShutdownCleansTrackedGridAndMap()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var maps = entities.System<SharedMapSystem>();
        var ruleSystem = entities.System<BluespaceErrorRule>();

        EntityUid ruleUid = default;
        EntityUid disposableGrid = default;
        MapId disposableMap = default;

        try
        {
            await server.WaitPost(() =>
            {
                ruleUid = entities.SpawnEntity(null, MapCoordinates.Nullspace);
                var rule = entities.AddComponent<BluespaceErrorRuleComponent>(ruleUid);
                maps.CreateMap(out disposableMap);
                disposableGrid = mapManager.CreateGridEntity(disposableMap).Owner;
                ruleSystem.TrackGrid(rule, disposableGrid);
                ruleSystem.TrackTemporaryMap(rule, disposableMap);

                // Deleting the rule bypasses normal Ended cleanup and must still reclaim its assets.
                entities.DeleteEntity(ruleUid);
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(entities.EntityExists(ruleUid), Is.False);
                    Assert.That(entities.EntityExists(disposableGrid), Is.False);
                    Assert.That(maps.MapExists(disposableMap), Is.False);
                });
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                if (entities.EntityExists(ruleUid))
                    entities.DeleteEntity(ruleUid);

                if (maps.MapExists(disposableMap))
                    maps.DeleteMap(disposableMap);
            });

            await pair.RunTicksSync(5);
            await pair.CleanReturnAsync();
        }
    }

    private static void AssertPlanetoidAvailability(
        EventManagerSystem eventManager,
        bool expected,
        int totalEntities)
    {
        var available = eventManager.AvailableEvents(
            ignoreEarliestStart: true,
            playerCountOverride: 2,
            totalEntityCountOverride: totalEntities);

        foreach (var eventId in PlanetoidEvents)
        {
            Assert.That(
                available.Keys.Any(prototype => prototype.ID == eventId),
                Is.EqualTo(expected),
                $"Unexpected availability for {eventId} with {totalEntities} entities.");
        }
    }
}
