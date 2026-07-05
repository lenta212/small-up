using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Content.Server._LuaM.Sector;
using Content.Server._NF.SectorServices;
using Content.Shared.MassMedia.Components;
using Content.Shared.Paper;
using Content.Shared._LuaM.Sector;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMDynamicEventDebrisTest
{
    private static readonly ResPath SectorMemoryDirectory = new("/luam");
    private static readonly ResPath SectorMemoryPath = SectorMemoryDirectory / "sector_memory.json";
    private static readonly ResPath SectorMemoryBackupPath = SectorMemoryDirectory / "admin-test-backup.json";
    private const float SpawnedObjectPositionTolerance = 1.0f;

    [Test]
    public async Task GeneratedDynamicEventsCreateIsolatedDebrisAndHostilesOnFifth()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var mapSystem = entManager.System<SharedMapSystem>();
        var transformSystem = entManager.System<SharedTransformSystem>();
        var storySystem = entManager.System<LuaMSectorStorySystem>();
        var dynamicEvents = entManager.System<LuaMSectorDynamicEventSystem>();

        MapId mapId = default;
        var expectedCoordinates = Enumerable.Range(0, LuaMSectorDynamicEventSystem.DynamicDebrisHostileInterval)
            .Select(i => new Vector2(10 + i * 8, 20))
            .ToArray();
        EntityUid resolvedDebrisUid = default;
        ProtoId<LuaMSectorStoryPrototype> resolvedStory = default;
        var resolvedHostileUids = new List<EntityUid>();
        var remainingDebrisUids = new List<EntityUid>();

        await server.WaitPost(() =>
        {
            ClearPersistedSectorMemory(resources);
            SectorNewsComponent.Articles.Clear();

            mapSystem.CreateMap(out mapId);
            var host = entManager.SpawnEntity(null, new MapCoordinates(Vector2.Zero, mapId));
            entManager.AddComponent<StationSectorServiceHostComponent>(host);
            entManager.AddComponent<SectorNewsComponent>(host);
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            Assert.That(storySystem.TryResetMemory(deletePersisted: true), Is.True);
        });

        await pair.RunTicksSync(5);

        await server.WaitPost(() =>
        {
            for (var i = 0; i < LuaMSectorDynamicEventSystem.DynamicDebrisHostileInterval; i++)
            {
                var targetCoordinates = new MapCoordinates(expectedCoordinates[i], mapId);
                var generated = dynamicEvents.TryGenerateDynamicEvent(
                    $"integration-test-{i}",
                    out var record,
                    out var error,
                    templateId: "quiet-distress",
                    ignoreOpenRuntimeLead: true,
                    ignorePlayerGate: true,
                    markerCoordinates: targetCoordinates);

                Assert.That(generated, Is.True, error);
                Assert.That(record, Is.Not.Null);
            }
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            var debrisSites = new List<(EntityUid Uid, LuaMDynamicEventDebrisComponent Debris, TransformComponent Xform)>();
            var debrisQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventDebrisComponent, MapGridComponent, TransformComponent>();
            while (debrisQuery.MoveNext(out var uid, out var debris, out _, out var xform))
                debrisSites.Add((uid, debris, xform));

            Assert.That(debrisSites, Has.Count.EqualTo(LuaMSectorDynamicEventSystem.DynamicDebrisHostileInterval));
            Assert.That(
                debrisSites.Select(entry => entry.Debris.DebrisSerial),
                Is.EquivalentTo(Enumerable.Range(1, LuaMSectorDynamicEventSystem.DynamicDebrisHostileInterval)));
            Assert.That(
                debrisSites.Where(entry => entry.Debris.HostileContact).Select(entry => entry.Debris.DebrisSerial),
                Is.EquivalentTo(new[] { LuaMSectorDynamicEventSystem.DynamicDebrisHostileInterval }));
            Assert.That(
                debrisSites.Single(entry => entry.Debris.DebrisSerial == LuaMSectorDynamicEventSystem.DynamicDebrisHostileInterval).Debris.HostileCount,
                Is.EqualTo(2));
            Assert.That(
                debrisSites.Single(entry => entry.Debris.DebrisSerial == LuaMSectorDynamicEventSystem.DynamicDebrisHostileInterval).Debris.HostileUids,
                Has.Count.EqualTo(2));
            Assert.That(
                debrisSites.Where(entry => entry.Debris.DebrisSerial < LuaMSectorDynamicEventSystem.DynamicDebrisHostileInterval)
                    .All(entry => entry.Debris.HostileCount == 0),
                Is.True);

            foreach (var (_, debris, xform) in debrisSites)
            {
                var expected = expectedCoordinates[debris.DebrisSerial - 1];
                Assert.That(debris.TemplateId, Is.EqualTo("quiet-distress"));
                Assert.That(debris.CreatedBy, Does.StartWith("integration-test-"));
                Assert.That(debris.MarkerLocation, Does.Contain($"x {FormatCoordinate(expected.X)}"));
                Assert.That(debris.MarkerLocation, Does.Contain($"y {FormatCoordinate(expected.Y)}"));
                var debrisCoordinates = transformSystem.ToMapCoordinates(xform.Coordinates);
                Assert.That(debrisCoordinates.MapId, Is.EqualTo(mapId));
                Assert.That(debrisCoordinates.Position.X, Is.EqualTo(expected.X).Within(SpawnedObjectPositionTolerance));
                Assert.That(debrisCoordinates.Position.Y, Is.EqualTo(expected.Y).Within(SpawnedObjectPositionTolerance));
            }

            var markers = new List<(LuaMDynamicEventMarkerComponent Marker, TransformComponent Xform)>();
            var markerQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventMarkerComponent, TransformComponent>();
            while (markerQuery.MoveNext(out _, out var marker, out var xform))
                markers.Add((marker, xform));

            Assert.That(markers, Has.Count.EqualTo(LuaMSectorDynamicEventSystem.DynamicDebrisHostileInterval));
            foreach (var (marker, xform) in markers)
            {
                var matchingDebris = debrisSites.Single(entry => entry.Debris.Story == marker.Story);
                Assert.That(marker.TemplateId, Is.EqualTo("quiet-distress"));
                Assert.That(marker.CreatedBy, Is.EqualTo(matchingDebris.Debris.CreatedBy));
                Assert.That(marker.MarkerLocation, Is.EqualTo(matchingDebris.Debris.MarkerLocation));
                var markerCoordinates = transformSystem.ToMapCoordinates(xform.Coordinates);
                var debrisCoordinates = transformSystem.ToMapCoordinates(matchingDebris.Xform.Coordinates);
                Assert.That(markerCoordinates.MapId, Is.EqualTo(mapId));
                Assert.That(markerCoordinates.Position.X, Is.EqualTo(debrisCoordinates.Position.X).Within(SpawnedObjectPositionTolerance));
                Assert.That(markerCoordinates.Position.Y, Is.EqualTo(debrisCoordinates.Position.Y).Within(SpawnedObjectPositionTolerance));
            }

            var siteNotes = new List<(LuaMDynamicEventSiteObjectComponent Site, PaperComponent Paper, TransformComponent Xform)>();
            var siteQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventSiteObjectComponent, PaperComponent, TransformComponent>();
            while (siteQuery.MoveNext(out _, out var site, out var paper, out var xform))
                siteNotes.Add((site, paper, xform));

            Assert.That(siteNotes, Has.Count.EqualTo(LuaMSectorDynamicEventSystem.DynamicDebrisHostileInterval));
            foreach (var (site, paper, xform) in siteNotes)
            {
                var matchingDebris = debrisSites.Single(entry => entry.Debris.Story == site.Story);
                Assert.That(site.TemplateId, Is.EqualTo("quiet-distress"));
                Assert.That(site.CreatedBy, Is.EqualTo(matchingDebris.Debris.CreatedBy));
                Assert.That(site.MarkerLocation, Is.EqualTo(matchingDebris.Debris.MarkerLocation));
                Assert.That(site.SiteObjectKind, Is.EqualTo("field-note"));
                var siteCoordinates = transformSystem.ToMapCoordinates(xform.Coordinates);
                var debrisCoordinates = transformSystem.ToMapCoordinates(matchingDebris.Xform.Coordinates);
                Assert.That(siteCoordinates.MapId, Is.EqualTo(mapId));
                Assert.That(siteCoordinates.Position.X, Is.EqualTo(debrisCoordinates.Position.X).Within(SpawnedObjectPositionTolerance));
                Assert.That(siteCoordinates.Position.Y, Is.EqualTo(debrisCoordinates.Position.Y).Within(SpawnedObjectPositionTolerance));
                Assert.That(paper.Content, Does.Contain("GPS карта"));
                Assert.That(paper.Content, Does.Contain("Сдать / закрыть"));
                Assert.That(paper.Content, Does.Contain(site.MarkerLocation));
            }
        });

        await server.WaitPost(() =>
        {
            var hostileDebris = new List<(EntityUid Uid, LuaMDynamicEventDebrisComponent Debris)>();
            var debrisQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventDebrisComponent>();
            while (debrisQuery.MoveNext(out var uid, out var debris))
            {
                if (debris.HostileContact)
                    hostileDebris.Add((uid, debris));
            }

            Assert.That(hostileDebris, Has.Count.EqualTo(1));
            var selected = hostileDebris.Single();
            resolvedDebrisUid = selected.Uid;
            resolvedStory = selected.Debris.Story;
            resolvedHostileUids = selected.Debris.HostileUids.ToList();
            Assert.That(resolvedHostileUids, Has.Count.EqualTo(2));
            Assert.That(resolvedHostileUids.All(entManager.EntityExists), Is.True);
            Assert.That(storySystem.TryResolveStory(resolvedStory, "integration-test", "Debris site checked."), Is.True);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(entManager.EntityExists(resolvedDebrisUid), Is.False);
            foreach (var hostileUid in resolvedHostileUids)
            {
                Assert.That(entManager.EntityExists(hostileUid), Is.False);
            }

            var remainingDebris = 0;
            var debrisQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventDebrisComponent>();
            while (debrisQuery.MoveNext(out _, out var debris))
            {
                Assert.That(debris.Story, Is.Not.EqualTo(resolvedStory));
                remainingDebris++;
            }

            Assert.That(remainingDebris, Is.EqualTo(LuaMSectorDynamicEventSystem.DynamicDebrisHostileInterval - 1));
        });

        await server.WaitPost(() =>
        {
            remainingDebrisUids.Clear();
            var debrisQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventDebrisComponent>();
            while (debrisQuery.MoveNext(out var uid, out _))
                remainingDebrisUids.Add(uid);

            Assert.That(remainingDebrisUids, Has.Count.EqualTo(LuaMSectorDynamicEventSystem.DynamicDebrisHostileInterval - 1));
            Assert.That(dynamicEvents.CleanupAllDynamicMarkers(), Is.GreaterThanOrEqualTo(remainingDebrisUids.Count));
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            foreach (var uid in remainingDebrisUids)
                Assert.That(entManager.EntityExists(uid), Is.False);

            var debrisQuery = entManager.EntityQueryEnumerator<LuaMDynamicEventDebrisComponent>();
            Assert.That(debrisQuery.MoveNext(out _, out _), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    private static void ClearPersistedSectorMemory(IResourceManager resources)
    {
        if (!resources.UserData.Exists(SectorMemoryDirectory))
            return;

        if (resources.UserData.Exists(SectorMemoryBackupPath))
            resources.UserData.Delete(SectorMemoryBackupPath);

        if (resources.UserData.Exists(SectorMemoryPath))
            resources.UserData.Delete(SectorMemoryPath);
    }

    private static string FormatCoordinate(float value)
    {
        return value.ToString("0.0", CultureInfo.InvariantCulture);
    }
}
