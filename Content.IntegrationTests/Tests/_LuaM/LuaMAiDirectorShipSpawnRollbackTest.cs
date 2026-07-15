using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._LuaM.Sector;
using Robust.Server.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Serilog.Events;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMAiDirectorShipSpawnRollbackTest
{
    private const string InjectedFailure = "Injected AI admin ship post-load failure";

    [Test]
    public async Task PostLoadFailureRollsBackLoadedGridAndEveryCreatedEntity()
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

        var playerManager = server.ResolveDependency<IPlayerManager>();
        var admin = playerManager.GetSessionById(clientSession!.UserId);
        var entities = server.ResolveDependency<IEntityManager>();
        var maps = entities.System<SharedMapSystem>();
        var director = entities.System<LuaMSectorAiDirectorSystem>();
        var createdDuringSpawn = new HashSet<EntityUid>();
        var baselineEntities = new HashSet<EntityUid>();
        EntityUid? previousAttached = null;
        var anchor = EntityUid.Invalid;
        var loadedGrid = EntityUid.Invalid;
        MapId mapId = default;
        var mapCreated = false;
        var trackingCreatedEntities = false;
        var expectedRollbackLogs = 0;
        var logSync = new object();

        void TrackCreatedEntity(Entity<MetaDataComponent> entity)
            => createdDuringSpawn.Add(entity.Owner);

        void ThrowAfterGridLoad(EntityUid gridUid)
        {
            loadedGrid = gridUid;
            entities.EntityAdded -= TrackCreatedEntity;
            trackingCreatedEntities = false;
            throw new InvalidOperationException(InjectedFailure);
        }

        bool JudgeExpectedRollback(string sawmillName, LogEvent message)
        {
            if (sawmillName != "luam.ai_director" ||
                !message.RenderMessage().StartsWith(
                    $"AI admin-bypass ship rollback: InvalidOperationException: {InjectedFailure}; vessel=Baeg; grid=",
                    StringComparison.Ordinal))
            {
                return false;
            }

            lock (logSync)
                expectedRollbackLogs++;
            return true;
        }

        pair.ServerLogHandler.JudgeLog += JudgeExpectedRollback;
        try
        {
            await server.WaitPost(() =>
            {
                director.AdminSetEnabled(false);
                maps.CreateMap(out mapId);
                mapCreated = true;

                previousAttached = admin.AttachedEntity;
                anchor = entities.SpawnEntity("MobHuman", new MapCoordinates(Vector2.Zero, mapId));
                playerManager.SetAttachedEntity(admin, anchor, true);
            });

            await pair.RunTicksSync(5);

            await server.WaitPost(() =>
            {
                baselineEntities = EntitiesOnMap(entities, mapId);
                entities.EntityAdded += TrackCreatedEntity;
                trackingCreatedEntities = true;
                director.SetShipSpawnPostLoadHookForTests(ThrowAfterGridLoad);
            });

            var spawnTask = director.SpawnShipNearAdminAsync(admin, "spawn Baeg ship near me");
            for (var i = 0; i < 120 && !spawnTask.IsCompleted; i++)
                await pair.RunTicksSync(1);

            Assert.That(spawnTask.IsCompleted, Is.True,
                "The public AI Director ship-spawn request did not complete after queued server ticks.");
            var result = await spawnTask;

            Assert.Multiple(() =>
            {
                Assert.That(result, Does.Contain("ошибка после загрузки"));
                Assert.That(result, Does.Contain("сетка удалена"));
                Assert.That(loadedGrid.Valid, Is.True,
                    "The injected failure must run only after TryLoadGrid returns a real grid.");
                Assert.That(createdDuringSpawn, Does.Contain(loadedGrid),
                    "EntityAdded must observe the loaded grid before rollback.");
                Assert.That(createdDuringSpawn.Any(uid => uid != loadedGrid), Is.True,
                    "The selected Baeg map must load child entities so recursive rollback is exercised.");
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                var leakedEntities = createdDuringSpawn
                    .Where(entities.EntityExists)
                    .ToArray();
                var currentEntities = EntitiesOnMap(entities, mapId);

                Assert.Multiple(() =>
                {
                    Assert.That(entities.EntityExists(loadedGrid), Is.False,
                        "Rollback must delete the loaded grid root.");
                    Assert.That(leakedEntities, Is.Empty,
                        "Rollback must delete every entity created while loading the ship grid.");
                    Assert.That(currentEntities, Is.EquivalentTo(baselineEntities),
                        "The target map must return exactly to its pre-spawn entity set.");
                    lock (logSync)
                    {
                        Assert.That(expectedRollbackLogs, Is.EqualTo(1),
                            "The post-load failure must travel through the rollback path exactly once.");
                    }
                });
            });
        }
        finally
        {
            pair.ServerLogHandler.JudgeLog -= JudgeExpectedRollback;
            await server.WaitPost(() =>
            {
                director.SetShipSpawnPostLoadHookForTests(null);
                if (trackingCreatedEntities)
                    entities.EntityAdded -= TrackCreatedEntity;

                if (admin.AttachedEntity == anchor)
                {
                    var restored = previousAttached is { } previous && entities.EntityExists(previous)
                        ? previous
                        : (EntityUid?) null;
                    playerManager.SetAttachedEntity(admin, restored, true);
                }

                if (mapCreated)
                    maps.DeleteMap(mapId);
            });
            await pair.CleanReturnAsync();
        }
    }

    private static HashSet<EntityUid> EntitiesOnMap(IEntityManager entities, MapId mapId)
    {
        var result = new HashSet<EntityUid>();
        var query = entities.EntityQueryEnumerator<TransformComponent>();
        while (query.MoveNext(out var uid, out var transform))
        {
            if (transform.MapID == mapId)
                result.Add(uid);
        }

        return result;
    }
}
