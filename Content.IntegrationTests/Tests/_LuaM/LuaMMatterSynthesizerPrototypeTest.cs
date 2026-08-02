using System.Linq;
using System.Numerics;
using Content.Shared._Goobstation.ItemMiner;
using Content.Shared.Construction.Components;
using Content.Shared.Maps;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMMatterSynthesizerPrototypeTest
{
    [TestPrototypes]
    private const string TestPrototypes = """
        - type: entity
          id: LuaMStaleMinerOutput
          components:
          - type: Transform

        - type: entity
          id: LuaMStaleMiner
          components:
          - type: Transform
            anchored: true
          - type: ItemMiner
            proto: LuaMStaleMinerOutput
            interval: 10
            needApcPower: false
            needsAnchored: false
        """;

    [TestCase("LaserDrill")]
    [TestCase("StationLaserDrill")]
    public async Task MatterSynthesizerCanBeUnanchoredAndAnchored(string prototypeId)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var components = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            var prototype = prototypes.Index<EntityPrototype>(prototypeId);
            Assert.That(
                prototype.TryGetComponent<AnchorableComponent>(out var anchorable, components),
                Is.True,
                $"{prototypeId} must use the standard wrench anchoring interaction.");
            Assert.That(
                (anchorable.Flags & AnchorableFlags.Unanchorable) != 0,
                Is.True,
                $"{prototypeId} must be removable with a wrench.");
            Assert.That(
                (anchorable.Flags & AnchorableFlags.Anchorable) != 0,
                Is.True,
                $"{prototypeId} must be installable again after moving it.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RestoredStaleDeadlineDoesNotReplayMissedProductionEveryTick()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var maps = entities.System<SharedMapSystem>();
        EntityUid miner = default;

        await server.WaitPost(() =>
        {
            maps.CreateMap(out var mapId);
            miner = entities.SpawnEntity("LuaMStaleMiner", new MapCoordinates(Vector2.Zero, mapId));
            var component = entities.GetComponent<ItemMinerComponent>(miner);
            component.NextAt = timing.CurTime - TimeSpan.FromMinutes(5);
        });

        await server.WaitRunTicks(5);
        await server.WaitAssertion(() =>
        {
            var outputs = entities.EntityQuery<MetaDataComponent>()
                .Count(meta => meta.EntityPrototype?.ID == "LuaMStaleMinerOutput");
            Assert.That(outputs, Is.Zero, "A restored miner must discard an old production backlog.");
            Assert.That(
                entities.GetComponent<ItemMinerComponent>(miner).NextAt,
                Is.GreaterThan(timing.CurTime),
                "The restored miner must resume from a future normal interval.");
        });

        await pair.CleanReturnAsync();
    }
}
