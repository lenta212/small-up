using System.Numerics;
using Content.Server.Nutrition.EntitySystems;
using Content.Shared.Nutrition.AnimalHusbandry;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[TestOf(typeof(AnimalHusbandrySystem))]
public sealed class LuaMAnimalHusbandryIntervalTest
{
    private static readonly TimeSpan ExpectedBreedInterval = TimeSpan.FromHours(1);

    [Test]
    public async Task AnimalsScheduleBreedingOncePerHourWithoutCatchUpBursts()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var mapSystem = entManager.System<SharedMapSystem>();

        MapId mapId = default;
        EntityUid chicken = default;

        await server.WaitPost(() =>
        {
            mapSystem.CreateMap(out mapId);
            chicken = entManager.SpawnEntity("MobChicken", new MapCoordinates(Vector2.Zero, mapId));
        });

        await pair.RunTicksSync(2);

        await server.WaitAssertion(() =>
        {
            var reproductive = entManager.GetComponent<ReproductiveComponent>(chicken);
            Assert.Multiple(() =>
            {
                Assert.That(reproductive.MinBreedAttemptInterval, Is.EqualTo(ExpectedBreedInterval));
                Assert.That(reproductive.MaxBreedAttemptInterval, Is.EqualTo(ExpectedBreedInterval));
                Assert.That(
                    (reproductive.NextBreedAttempt - timing.CurTime).TotalSeconds,
                    Is.InRange(ExpectedBreedInterval.TotalSeconds - 1, ExpectedBreedInterval.TotalSeconds));
            });
        });

        await server.WaitPost(() =>
        {
            var reproductive = entManager.GetComponent<ReproductiveComponent>(chicken);
            reproductive.NextBreedAttempt = timing.CurTime - TimeSpan.FromHours(24);
        });

        await pair.RunTicksSync(2);

        await server.WaitAssertion(() =>
        {
            var reproductive = entManager.GetComponent<ReproductiveComponent>(chicken);
            Assert.That(
                (reproductive.NextBreedAttempt - timing.CurTime).TotalSeconds,
                Is.InRange(ExpectedBreedInterval.TotalSeconds - 1, ExpectedBreedInterval.TotalSeconds),
                "An overdue animal must schedule from the current time instead of replaying every missed interval.");
        });

        await server.WaitPost(() => mapSystem.DeleteMap(mapId));
        await pair.CleanReturnAsync();
    }
}
