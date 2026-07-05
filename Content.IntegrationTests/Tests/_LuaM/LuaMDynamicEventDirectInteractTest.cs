using System.Linq;
using System.Numerics;
using Content.Server._LuaM.Sector;
using Content.Server._NF.SectorServices;
using Content.Shared.Interaction;
using Content.Shared.MassMedia.Components;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMDynamicEventDirectInteractTest
{
    private static readonly ResPath SectorMemoryDirectory = new("/luam");
    private static readonly ResPath SectorMemoryPath = SectorMemoryDirectory / "sector_memory.json";
    private static readonly ResPath SectorMemoryBackupPath = SectorMemoryDirectory / "admin-test-backup.json";

    [Test]
    public async Task DirectMarkerClickClosesActiveDynamicTask()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var mapSystem = entManager.System<SharedMapSystem>();
        var storySystem = entManager.System<LuaMSectorStorySystem>();
        var dynamicEvents = entManager.System<LuaMSectorDynamicEventSystem>();

        EntityUid markerUid = default;
        EntityUid user = default;
        MapId mapId = default;
        var handled = false;

        await server.WaitPost(() =>
        {
            ClearPersistedSectorMemory(resources);
            SectorNewsComponent.Articles.Clear();

            mapSystem.CreateMap(out mapId);
            var host = entManager.SpawnEntity(null, new MapCoordinates(Vector2.Zero, mapId));
            entManager.AddComponent<StationSectorServiceHostComponent>(host);
            entManager.AddComponent<SectorNewsComponent>(host);
            user = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(1, 1), mapId));
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            Assert.That(storySystem.TryResetMemory(deletePersisted: true), Is.True);
        });

        await server.WaitPost(() =>
        {
            Assert.That(dynamicEvents.TryGenerateDynamicEvent(
                "integration-test",
                out var record,
                out var error,
                templateId: "field-repair",
                ignorePlayerGate: true,
                markerCoordinates: new MapCoordinates(new Vector2(6, -4), mapId)), Is.True, error);
            Assert.That(record, Is.Not.Null);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var markers = entManager.AllComponents<LuaMDynamicEventMarkerComponent>().ToList();
            Assert.That(markers, Has.Count.EqualTo(1));
            markerUid = markers.Single().Uid;

            var meta = entManager.GetComponent<MetaDataComponent>(markerUid);
            Assert.That(meta.EntityName, Does.Contain("точка сдачи LuaM"));
            Assert.That(meta.EntityDescription, Does.Contain("Сдать / закрыть задание"));
        });

        await server.WaitPost(() =>
        {
            var ev = new InteractHandEvent(user, markerUid);
            entManager.EventBus.RaiseLocalEvent(markerUid, ev);
            handled = ev.Handled;
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(handled, Is.True);
            var status = storySystem.GetStatusSnapshot();
            Assert.That(status.Hazards.Single(hazard => hazard.Story == "LuaMSectorRuntimeDistress001").Resolved, Is.True);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DirectSiteNoteClickClosesActiveDynamicTask()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var mapSystem = entManager.System<SharedMapSystem>();
        var storySystem = entManager.System<LuaMSectorStorySystem>();
        var dynamicEvents = entManager.System<LuaMSectorDynamicEventSystem>();

        EntityUid siteNoteUid = default;
        EntityUid user = default;
        MapId mapId = default;
        var handled = false;

        await server.WaitPost(() =>
        {
            ClearPersistedSectorMemory(resources);
            SectorNewsComponent.Articles.Clear();

            mapSystem.CreateMap(out mapId);
            var host = entManager.SpawnEntity(null, new MapCoordinates(Vector2.Zero, mapId));
            entManager.AddComponent<StationSectorServiceHostComponent>(host);
            entManager.AddComponent<SectorNewsComponent>(host);
            user = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(1, 1), mapId));
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            Assert.That(storySystem.TryResetMemory(deletePersisted: true), Is.True);
        });

        await server.WaitPost(() =>
        {
            Assert.That(dynamicEvents.TryGenerateDynamicEvent(
                "integration-test",
                out var record,
                out var error,
                templateId: "field-repair",
                ignorePlayerGate: true,
                markerCoordinates: new MapCoordinates(new Vector2(6, -4), mapId)), Is.True, error);
            Assert.That(record, Is.Not.Null);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var siteNotes = entManager.AllComponents<LuaMDynamicEventSiteObjectComponent>().ToList();
            Assert.That(siteNotes, Has.Count.EqualTo(1));
            siteNoteUid = siteNotes.Single().Uid;

            var meta = entManager.GetComponent<MetaDataComponent>(siteNoteUid);
            Assert.That(meta.EntityName, Does.Contain("акт сдачи LuaM"));
            Assert.That(meta.EntityDescription, Does.Contain("Сдать / закрыть задание"));
        });

        await server.WaitPost(() =>
        {
            var ev = new InteractHandEvent(user, siteNoteUid);
            entManager.EventBus.RaiseLocalEvent(siteNoteUid, ev);
            handled = ev.Handled;
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(handled, Is.True);
            var status = storySystem.GetStatusSnapshot();
            Assert.That(status.Hazards.Single(hazard => hazard.Story == "LuaMSectorRuntimeDistress001").Resolved, Is.True);
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
}
