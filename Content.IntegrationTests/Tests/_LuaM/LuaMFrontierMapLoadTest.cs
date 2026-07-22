using Content.Server.GameTicking;
using Content.Shared.CCVar;
using Content.Shared.Maps;
using Robust.Server.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using Serilog.Events;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMFrontierMapLoadTest
{
    private const string ProductionMap = "Frontier";

    private static readonly string[] ProductionPoiMaps =
    {
        "/Maps/_NF/POI/medical.yml",
        "/Maps/_Mono/POI/pdvhelios.yml",
        "/Maps/_Mono/POI/tsfmchalcyon.yml",
    };

    [Test]
    public async Task ProductionFrontierMapLoadsWithRegisteredPrototypes()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
        });
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapLoader = entManager.System<MapLoaderSystem>();
        var mapSystem = entManager.System<SharedMapSystem>();
        var protoManager = server.ResolveDependency<IPrototypeManager>();
        var ticker = entManager.System<GameTicker>();
        var cfg = server.ResolveDependency<IConfigurationManager>();

        Assert.That(cfg.GetCVar(CCVars.GridFill), Is.False);

        await server.WaitPost(() =>
        {
            var options = DeserializationOptions.Default with { InitializeMaps = true };
            MapId mapId;
            try
            {
                ticker.LoadGameMap(protoManager.Index<GameMapPrototype>(ProductionMap), out mapId, options);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to load production map {ProductionMap}", ex);
            }

            try
            {
                Assert.That(mapManager.GetAllGrids(mapId), Is.Not.Empty,
                    "The production map must contain at least one grid.");
            }
            finally
            {
                mapSystem.DeleteMap(mapId);
            }
        });

        await pair.CleanReturnAsync();
    }

    [TestCaseSource(nameof(ProductionPoiMaps))]
    public async Task ProductionPoiMapLoadsWithRegisteredPrototypes(string mapPath)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
        });
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var mapLoader = entManager.System<MapLoaderSystem>();
        var mapSystem = entManager.System<SharedMapSystem>();

        bool JudgeKnownLegacyPoiNoise(string sawmillName, LogEvent message)
        {
            var rendered = message.RenderMessage();
            return sawmillName == "system.device_link" &&
                       rendered.Contains("links to invalid entity", StringComparison.Ordinal) ||
                   sawmillName == "entity" &&
                       rendered.StartsWith("Caught exception while raising event EntityTerminatingEvent", StringComparison.Ordinal) &&
                       rendered.Contains("DisposalUnit", StringComparison.Ordinal);
        }

        pair.ServerLogHandler.JudgeLog += JudgeKnownLegacyPoiNoise;
        try
        {
            await server.WaitPost(() =>
            {
                var options = DeserializationOptions.Default with { InitializeMaps = true };
                mapSystem.CreateMap(out var mapId);
                try
                {
                    Assert.That(
                        mapLoader.TryLoadGrid(mapId, new ResPath(mapPath), out _, options),
                        Is.True,
                        $"Failed to load production POI map {mapPath}");
                }
                finally
                {
                    mapSystem.DeleteMap(mapId);
                }
            });

            await pair.CleanReturnAsync();
        }
        finally
        {
            pair.ServerLogHandler.JudgeLog -= JudgeKnownLegacyPoiNoise;
        }
    }
}
