using Content.Shared._LuaM.Stargate;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Utility;
using System.Linq;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMTaipanAsteroidBaseMapTest
{
    private static readonly object[] Maps =
    {
        new object[] { "/Maps/_LuaM/POI/StargateOriginal/BarrierGate2.yml", 1, 1, 0, 1, 0 },
        new object[] { "/Maps/_LuaM/POI/StargateOriginal/lodge.yml", 1, 1, 1, 1, 0 },
        new object[] { "/Maps/_LuaM/POI/StargateOriginal/ds_typan.yml", 1, 1, 0, 0, 1 },
        new object[] { "/Maps/_LuaM/POI/StargateOriginal/typancargodepot.yml", 0, 0, 0, 0, 0 },
        new object[] { "/Maps/_LuaM/POI/Taipan/bluespace_science_grid.yml", 0, 0, 0, 0, 0 },
        new object[] { "/Maps/_LuaM/POI/Taipan/colossal_asteroid.yml", 0, 0, 0, 0, 0 },
        new object[] { "/Maps/_LuaM/POI/Taipan/lincore_crush.yml", 0, 0, 0, 0, 0 },
    };

    [TestCaseSource(nameof(Maps))]
    public async Task AuthenticStargateStationAndTaipanBaseLoads(
        string path,
        int expectedGates,
        int expectedConsoles,
        int expectedEditors,
        int expectedPapers,
        int expectedDisks)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var maps = entities.System<SharedMapSystem>();
        var loader = entities.System<MapLoaderSystem>();

        await server.WaitAssertion(() =>
        {
            maps.CreateMap(out var mapId);
            try
            {
                var options = DeserializationOptions.Default with { InitializeMaps = true };
                Assert.That(loader.TryLoadGrid(mapId, new ResPath(path), out var grid, options), Is.True);
                Assert.That(grid, Is.Not.Null);

                var gates = entities.AllComponents<LuaMStargateComponent>()
                    .Where(entry => entities.GetComponent<TransformComponent>(entry.Uid).MapID == mapId)
                    .ToList();
                var consoles = entities.AllComponents<LuaMStargateConsoleComponent>()
                    .Count(entry => entities.GetComponent<TransformComponent>(entry.Uid).MapID == mapId);

                Assert.That(gates, Has.Count.EqualTo(expectedGates));
                foreach (var gate in gates)
                {
                    Assert.That(gate.Component.Address, Has.Length.EqualTo(6));
                    Assert.That(gate.Component.Address.Distinct().Count(), Is.EqualTo(6));
                }

                Assert.That(consoles, Is.EqualTo(expectedConsoles));
                Assert.That(CountPrototype(entities, mapId, "StargateAddressEditorConsole"), Is.EqualTo(expectedEditors));
                Assert.That(CountPrototype(entities, mapId, "StargateAddressPaper"), Is.EqualTo(expectedPapers));
                Assert.That(CountPrototype(entities, mapId, "StargateAddressDisk"), Is.EqualTo(expectedDisks));
            }
            finally
            {
                maps.DeleteMap(mapId);
            }
        });

        await pair.CleanReturnAsync();
    }

    private static int CountPrototype(IEntityManager entities, MapId mapId, string prototype)
    {
        var count = 0;
        var query = entities.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
        while (query.MoveNext(out _, out var metadata, out var transform))
        {
            if (transform.MapID == mapId && metadata.EntityPrototype?.ID == prototype)
                count++;
        }

        return count;
    }
}
