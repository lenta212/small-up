using System.Numerics;
using Content.Server._Mono.Projectiles.TargetSeeking;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(TargetSeekingSystem))]
public sealed class LuaMTargetSeekingMapIsolationTest
{
    [Test]
    public async Task SeekerNeverAcquiresOrKeepsTargetOnAnotherMap()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var mapSystem = entManager.System<SharedMapSystem>();
        var transformSystem = entManager.System<SharedTransformSystem>();
        var seekingSystem = entManager.System<TargetSeekingSystem>();

        MapId firstMap = default;
        MapId secondMap = default;
        var mapsCreated = false;
        EntityUid seeker = default;
        EntityUid target = default;

        try
        {
            await server.WaitPost(() =>
            {
                mapSystem.CreateMap(out firstMap);
                mapSystem.CreateMap(out secondMap);
                mapsCreated = true;

                seeker = entManager.SpawnEntity(
                    "MissileProjectile50mmHE",
                    new MapCoordinates(Vector2.Zero, firstMap));
                var otherMapTarget = entManager.SpawnEntity(
                    "SparkFlare",
                    new MapCoordinates(new Vector2(10f, 0f), secondMap));

                var seeking = entManager.GetComponent<TargetSeekingComponent>(seeker);
                seeking.ScanArc = 360f;
                seekingSystem.AcquireTarget(
                    seeker,
                    seeking,
                    entManager.GetComponent<TransformComponent>(seeker));

                Assert.That(
                    seeking.CurrentTarget,
                    Is.Null,
                    "A target at identical sector coordinates on another map must not be acquired.");

                target = entManager.SpawnEntity(
                    "SparkFlare",
                    new MapCoordinates(new Vector2(10f, 0f), firstMap));
                seekingSystem.AcquireTarget(
                    seeker,
                    seeking,
                    entManager.GetComponent<TransformComponent>(seeker));

                Assert.That(seeking.CurrentTarget, Is.EqualTo(target));

                transformSystem.SetMapCoordinates(
                    target,
                    new MapCoordinates(new Vector2(10f, 0f), secondMap));
            });

            await pair.RunSeconds(0.25f);

            await server.WaitAssertion(() =>
            {
                var seeking = entManager.GetComponent<TargetSeekingComponent>(seeker);
                Assert.That(
                    seeking.CurrentTarget,
                    Is.Null,
                    "A tracked target must be released immediately after moving to another map.");
            });
        }
        finally
        {
            if (mapsCreated)
            {
                await server.WaitPost(() =>
                {
                    mapSystem.DeleteMap(firstMap);
                    mapSystem.DeleteMap(secondMap);
                });
            }

            await pair.CleanReturnAsync();
        }
    }
}
