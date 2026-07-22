using Content.Server.StationEvents.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMBluespaceCargoEventContractTest
{
    private static readonly (string EventId, string SpawnerId)[] Events =
    {
        ("BluespaceCargoCrate", "RandomCargoSpawner"),
        ("BluespaceMcCargoCrate", "CrateFoodMcCargo"),
        ("BluespaceSyndicateCrate", "CrateSyndicateSurplusBundle"),
    };

    [Test]
    public async Task CargoCrateEventsRepeatAtMostOncePerDay()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var componentFactory = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            foreach (var (eventId, spawnerId) in Events)
            {
                var prototype = prototypes.Index<EntityPrototype>(eventId);
                Assert.That(
                    prototype.TryGetComponent<StationEventComponent>(out var stationEvent, componentFactory),
                    Is.True,
                    $"{eventId} must have a station event component.");
                Assert.That(
                    prototype.TryGetComponent<BluespaceCargoRuleComponent>(out var cargoRule, componentFactory),
                    Is.True,
                    $"{eventId} must remain a bluespace cargo event.");

                Assert.Multiple(() =>
                {
                    Assert.That(stationEvent.ReoccurrenceDelay, Is.EqualTo(24 * 60));
                    Assert.That(cargoRule.SpawnerPrototype, Is.EqualTo(spawnerId));
                });
            }
        });

        await pair.CleanReturnAsync();
    }
}
