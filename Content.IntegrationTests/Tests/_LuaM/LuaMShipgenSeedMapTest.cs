using System.Linq;
using Content.Server.Atmos.Components;
using Content.Server.Shuttles.Components;
using Content.Shared.Atmos.Components;
using Content.Shared.Maps;
using Robust.Server.GameObjects;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMShipgenSeedMapTest
{
    private static readonly ResPath SeedPath = new("/Maps/_LuaM/ShipGen/shipgen_seed.yml");

    [Test]
    public async Task TrustedSeedLoadsAsOneTileShuttleGrid()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var loader = entities.System<MapLoaderSystem>();
        var maps = entities.System<MapSystem>();
        var sharedMaps = entities.System<SharedMapSystem>();
        var tiles = server.ResolveDependency<ITileDefinitionManager>();

        await server.WaitPost(() =>
        {
            maps.CreateMap(out var mapId);
            try
            {
                Assert.That(loader.TryLoadGrid(mapId, SeedPath, out var grid), Is.True);
                Assert.That(grid.HasValue, Is.True);
                Assert.That(entities.HasComponent<MapGridComponent>(grid.Value), Is.True);
                Assert.That(entities.HasComponent<ShuttleComponent>(grid.Value), Is.True);
                Assert.That(entities.HasComponent<GridAtmosphereComponent>(grid.Value), Is.True);
                Assert.That(entities.HasComponent<GasTileOverlayComponent>(grid.Value), Is.True);

                var populated = sharedMaps.GetAllTiles(grid.Value.Owner, grid.Value.Comp).ToArray();
                Assert.That(populated, Has.Length.EqualTo(1));
                Assert.That(populated[0].Tile.TypeId, Is.EqualTo(tiles["FloorHullReinforced"].TileId));
            }
            finally
            {
                maps.DeleteMap(mapId);
            }
        });

        await pair.CleanReturnAsync();
    }
}
