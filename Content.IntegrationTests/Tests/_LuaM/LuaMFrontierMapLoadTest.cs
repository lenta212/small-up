using System.Linq;
using Content.Server.Atmos.Components;
using Content.Server.GameTicking;
using Content.Server.Gateway.Components;
using Content.Shared.CCVar;
using Content.Shared.Gateway;
using Content.Shared.Maps;
using Content.Shared.Parallax.Biomes;
using Content.Shared.Salvage.Expeditions.Modifiers;
using Content.Shared.Station.Components;
using Content.Shared.Weather;
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
    public async Task ProductionFrontierMapLoadsWithWorkingProceduralGateway()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
        });
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = entManager.System<SharedMapSystem>();
        var protoManager = server.ResolveDependency<IPrototypeManager>();
        var ticker = entManager.System<GameTicker>();
        var cfg = server.ResolveDependency<IConfigurationManager>();

        Assert.That(cfg.GetCVar(CCVars.GridFill), Is.False);

        var oldGeneratorEnabled = false;
        var oldGeneratorMaxDestinations = 0;
        var mapId = MapId.Nullspace;

        try
        {
            await server.WaitPost(() =>
            {
                oldGeneratorEnabled = cfg.GetCVar(CCVars.GatewayGeneratorEnabled);
                oldGeneratorMaxDestinations = cfg.GetCVar(CCVars.GatewayGeneratorMaxDestinations);
                cfg.SetCVar(CCVars.GatewayGeneratorMaxDestinations, 1);
                cfg.SetCVar(CCVars.GatewayGeneratorEnabled, true);

                var options = DeserializationOptions.Default with { InitializeMaps = true };
                try
                {
                    ticker.LoadGameMap(protoManager.Index<GameMapPrototype>(ProductionMap), out mapId, options);
                }
                catch (Exception ex)
                {
                    throw new Exception($"Failed to load production map {ProductionMap}", ex);
                }
            });

            await pair.RunTicksSync(600);
            await server.WaitAssertion(() =>
            {
                var stationGrids = mapManager.GetAllGrids(mapId)
                    .Where(grid => entManager.HasComponent<StationMemberComponent>(grid.Owner))
                    .ToList();
                Assert.That(stationGrids, Has.Count.EqualTo(1),
                    "The production map must contain exactly one station grid.");

                var stationGrid = stationGrids.Single().Owner;
                var station = entManager.GetComponent<StationMemberComponent>(stationGrid).Station;
                Assert.That(
                    entManager.TryGetComponent<GatewayGeneratorComponent>(station, out var generator),
                    Is.True,
                    "The Frontier station prototype must own the procedural gateway generator.");
                Assert.That(generator!.Generated, Has.Count.EqualTo(1),
                    "The enabled generator must create a destination up to the configured test cap.");

                var sourceGateways = entManager.AllComponents<GatewayComponent>()
                    .Where(entry =>
                        entManager.TryGetComponent<TransformComponent>(entry.Uid, out var xform) &&
                        xform.MapID == mapId)
                    .ToList();
                Assert.That(sourceGateways, Has.Count.EqualTo(1),
                    "Colossus must contain one physical gateway.");
                Assert.Multiple(() =>
                {
                    Assert.That(sourceGateways[0].Component.Enabled, Is.True,
                        "The mapped gateway must be interactable.");
                    Assert.That(
                        entManager.GetComponent<TransformComponent>(sourceGateways[0].Uid).GridUid,
                        Is.EqualTo(stationGrid),
                        "The mapped gateway must be anchored to the Frontier station grid.");
                });

                var destinationUid = generator.Generated.Single();
                Assert.That(
                    entManager.TryGetComponent<GatewayGeneratorDestinationComponent>(destinationUid, out var destination),
                    Is.True);
                Assert.That(destination!.Generator, Is.EqualTo(station));
                var profile = protoManager.Index(destination.Profile);
                var air = protoManager.Index<SalvageAirMod>(profile.Air);
                Assert.That(
                    entManager.TryGetComponent<BiomeComponent>(destinationUid, out var biome),
                    Is.True,
                    "A generated gateway destination must be a procedural biome.");
                Assert.Multiple(() =>
                {
                    Assert.That(biome!.Template?.Id, Is.EqualTo(profile.Biome.Id));
                    Assert.That(destination.Address, Does.Match("^GW-(?:[0-9A-F]{2}-){3}[0-9A-F]{2}$"));
                    Assert.That(profile.Threat, Is.InRange(GatewayThreatLevel.Minimal, GatewayThreatLevel.Extreme));
                    Assert.That(
                        destination.GenerationState,
                        Is.EqualTo(GatewayDestinationGenerationState.Ready),
                        "The generated gateway world must complete its asynchronous dungeon transaction.");
                    Assert.That(destination.DungeonBoundsValidated, Is.True,
                        "A ready gateway world must have all dungeon tiles inside its restricted range.");
                    Assert.That(
                        entManager.GetComponent<MapAtmosphereComponent>(destinationUid).Space,
                        Is.EqualTo(air.Space));
                });

                if (!air.Space && profile.Weather is { } weatherId)
                {
                    Assert.That(
                        entManager.GetComponent<WeatherComponent>(destinationUid).Weather.ContainsKey(weatherId),
                        Is.True,
                        "The generated planet must apply its advertised weather.");
                }

                var destinationMapId = entManager.GetComponent<TransformComponent>(destinationUid).MapID;
                var destinationGateways = entManager.AllComponents<GatewayComponent>()
                    .Where(entry =>
                        entManager.TryGetComponent<TransformComponent>(entry.Uid, out var xform) &&
                        xform.MapID == destinationMapId)
                    .ToList();
                Assert.That(destinationGateways, Has.Count.EqualTo(1));
                Assert.Multiple(() =>
                {
                    Assert.That(destinationGateways[0].Component.Enabled, Is.True,
                        "The generated planet must contain an enabled return gateway.");
                    Assert.That(destination.Gateway, Is.EqualTo(destinationGateways[0].Uid));
                });
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                cfg.SetCVar(CCVars.GatewayGeneratorEnabled, false);
                if (mapId != MapId.Nullspace && mapSystem.MapExists(mapId))
                    mapSystem.DeleteMap(mapId);

                cfg.SetCVar(CCVars.GatewayGeneratorMaxDestinations, oldGeneratorMaxDestinations);
                cfg.SetCVar(CCVars.GatewayGeneratorEnabled, oldGeneratorEnabled);
            });

            await pair.RunTicksSync(5);
            await pair.CleanReturnAsync();
        }
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
