using System.Linq;
using Content.Server._Mono.Cleanup;
using Content.Server.Atmos.Components;
using Content.Server.GameTicking;
using Content.Server.Gateway.Components;
using Content.Server.Gateway.Systems;
using Content.Shared.CCVar;
using Content.Shared.Gateway;
using Content.Shared.Maps;
using Content.Shared.Parallax.Biomes;
using Content.Shared.Salvage;
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
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
        });
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var mapManager = server.ResolveDependency<IMapManager>();
        var mapSystem = entManager.System<SharedMapSystem>();
        var gatewaySystem = entManager.System<GatewaySystem>();
        var uiSystem = entManager.System<UserInterfaceSystem>();
        var protoManager = server.ResolveDependency<IPrototypeManager>();
        var ticker = entManager.System<GameTicker>();
        var cfg = server.ResolveDependency<IConfigurationManager>();

        Assert.That(cfg.GetCVar(CCVars.GridFill), Is.False);

        var oldGeneratorEnabled = false;
        var oldGeneratorMaxDestinations = 0;
        var mapId = MapId.Nullspace;
        var sourceGatewayNet = NetEntity.Invalid;
        var destinationGatewayNet = NetEntity.Invalid;
        var initialDestinationUid = EntityUid.Invalid;
        var removedGatewayUid = EntityUid.Invalid;
        var clientActor = EntityUid.Invalid;

        try
        {
            await server.WaitPost(() =>
            {
                oldGeneratorEnabled = cfg.GetCVar(CCVars.GatewayGeneratorEnabled);
                oldGeneratorMaxDestinations = cfg.GetCVar(CCVars.GatewayGeneratorMaxDestinations);
                cfg.SetCVar(CCVars.GatewayGeneratorMaxDestinations, 1);
                cfg.SetCVar(CCVars.GatewayGeneratorEnabled, true);

                // Match the real pre-round flow: load the map first, then initialize it
                // after station setup and the lobby preload have completed.
                var options = DeserializationOptions.Default with { InitializeMaps = false };
                try
                {
                    ticker.LoadGameMap(protoManager.Index<GameMapPrototype>(ProductionMap), out mapId, options);
                }
                catch (Exception ex)
                {
                    throw new Exception($"Failed to load production map {ProductionMap}", ex);
                }
            });

            // Production keeps the loaded station in the pre-round lobby before MapInit.
            // Let any gateway generation queued during station setup settle in that state.
            await pair.RunTicksSync(600);
            await server.WaitPost(() => mapSystem.InitializeMap(mapId));
            await server.WaitPost(() =>
            {
                var generators = entManager.AllComponents<GatewayGeneratorComponent>().ToList();
                Assert.That(generators, Has.Count.EqualTo(1));
                Assert.That(generators[0].Component.Generated, Has.Count.EqualTo(1));
                initialDestinationUid = generators[0].Component.Generated.Single();
            });
            var gatewayReady = false;
            for (var attempt = 0; attempt < 40 && !gatewayReady; attempt++)
            {
                await pair.RunTicksSync(100);
                await server.WaitPost(() =>
                {
                    gatewayReady = entManager.AllComponents<GatewayGeneratorComponent>()
                        .Any(entry =>
                            entry.Component.Generated.Count == 1 &&
                            entry.Component.Generated.All(destinationUid =>
                                entManager.TryGetComponent<GatewayGeneratorDestinationComponent>(
                                    destinationUid,
                                    out var destination) &&
                                destination.GenerationState == GatewayDestinationGenerationState.Ready));
                });
            }

            Assert.That(
                gatewayReady,
                Is.True,
                "The production gateway pool must converge to a ready destination.");
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
                Assert.That(
                    generator.Generated.Single(),
                    Is.EqualTo(initialDestinationUid),
                    "A valid dungeon must keep its original destination instead of flashing and being replaced.");

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
                sourceGatewayNet = entManager.GetNetEntity(sourceGateways[0].Uid);

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
                        entManager.GetComponent<RestrictedRangeComponent>(destinationUid).Range,
                        Is.LessThanOrEqualTo(160f),
                        "A generated dungeon must stay within the hard world-size safety bound.");
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
                Assert.That(profile.SuppressResourceLoot, Is.True,
                    "Frontier gateway expeditions must not be bulk-resource destinations.");
                Assert.That(profile.RewardCache, Is.Not.Null,
                    "Every offered expedition profile must declare its technology cache tier.");
                var rewardCaches = entManager.AllComponents<MetaDataComponent>()
                    .Where(entry =>
                        entry.Component.EntityPrototype?.ID == profile.RewardCache!.Value.Id &&
                        entManager.GetComponent<TransformComponent>(entry.Uid).MapID == destinationMapId)
                    .ToList();
                Assert.That(rewardCaches, Has.Count.EqualTo(1),
                    "Exactly one high-value cache must be placed inside the generated dungeon.");
                var rewardChildren = entManager.AllComponents<TransformComponent>()
                    .Where(entry => entry.Component.ParentUid == rewardCaches[0].Uid)
                    .Select(entry => entManager.GetComponent<MetaDataComponent>(entry.Uid).EntityPrototype?.ID)
                    .Where(id => id != null)
                    .ToList();
                Assert.Multiple(() =>
                {
                    Assert.That(rewardChildren.Any(id => id!.StartsWith("TechDisk")), Is.True,
                        "The expedition cache must contain a technology unavailable through ordinary R&D progression.");
                    Assert.That(rewardChildren.Any(id => id!.StartsWith("WeaponCase")), Is.True,
                        "The expedition cache must contain one rare ready-to-transport weapon case.");
                });
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
                    Assert.That(
                        entManager.HasComponent<CleanupImmuneComponent>(destination.Gateway),
                        Is.True,
                        "Automatic space cleanup must never delete a generated return gateway.");
                });
                destinationGatewayNet = entManager.GetNetEntity(destination.Gateway);

                gatewaySystem.UpdateAllGateways();
                Assert.That(
                    uiSystem.TryGetUiState<GatewayBoundUserInterfaceState>(
                        sourceGateways[0].Uid,
                        GatewayUiKey.Key,
                        out var gatewayState),
                    Is.True,
                    "The mapped gateway must publish a bound UI state.");
                Assert.That(
                    gatewayState!.Destinations.Select(entry => entry.Entity),
                    Does.Contain(destinationGatewayNet),
                    "The generated return gateway must be visible as a selectable destination.");
            });

            await server.WaitPost(() =>
            {
                var session = server.PlayerMan.Sessions.Single();
                var sourceGateway = entManager.GetEntity(sourceGatewayNet);
                clientActor = entManager.SpawnEntity(
                    "MobHuman",
                    entManager.GetComponent<TransformComponent>(sourceGateway).Coordinates);
                Assert.That(
                    server.PlayerMan.SetAttachedEntity(session, clientActor, true),
                    Is.True,
                    "The connected integration client must have an actor for the gateway UI.");
                uiSystem.OpenUi(
                    sourceGateway,
                    GatewayUiKey.Key,
                    clientActor);
            });
            await pair.RunTicksSync(10);
            await pair.Client.WaitAssertion(() =>
            {
                var clientEntManager = pair.Client.ResolveDependency<IEntityManager>();
                Assert.That(
                    clientEntManager.TryGetEntity(sourceGatewayNet, out var clientGateway),
                    Is.True,
                    "The client must receive the mapped gateway before its UI opens.");
                var clientUi = clientEntManager.System<Robust.Client.GameObjects.UserInterfaceSystem>();
                Assert.That(
                    clientUi.TryGetUiState<GatewayBoundUserInterfaceState>(
                        clientGateway!.Value,
                        GatewayUiKey.Key,
                        out var clientState),
                    Is.True,
                    "The connected client must receive the gateway UI state.");
                Assert.That(
                    clientState!.Destinations.Select(entry => entry.Entity),
                    Does.Contain(destinationGatewayNet),
                    "The connected client must receive the generated destination outside its PVS.");
            });

            // Reproduce the live failure mode: an external cleanup removed the endpoint
            // while the generator still counted its world against the hard cap.
            await server.WaitPost(() =>
            {
                var destination =
                    entManager.GetComponent<GatewayGeneratorDestinationComponent>(initialDestinationUid);
                removedGatewayUid = destination.Gateway;
                entManager.DeleteEntity(removedGatewayUid);
            });
            await pair.RunTicksSync(40);
            await server.WaitAssertion(() =>
            {
                var destination =
                    entManager.GetComponent<GatewayGeneratorDestinationComponent>(initialDestinationUid);
                Assert.Multiple(() =>
                {
                    Assert.That(
                        destination.Gateway,
                        Is.Not.EqualTo(removedGatewayUid),
                        "The periodic safety pass must restore a missing endpoint without replacing its world.");
                    Assert.That(entManager.EntityExists(destination.Gateway), Is.True);
                    Assert.That(entManager.GetComponent<GatewayComponent>(destination.Gateway).Enabled, Is.True);
                    Assert.That(entManager.HasComponent<CleanupImmuneComponent>(destination.Gateway), Is.True);
                });

                destinationGatewayNet = entManager.GetNetEntity(destination.Gateway);
                gatewaySystem.UpdateAllGateways();
                Assert.That(
                    uiSystem.TryGetUiState<GatewayBoundUserInterfaceState>(
                        entManager.GetEntity(sourceGatewayNet),
                        GatewayUiKey.Key,
                        out var repairedState),
                    Is.True);
                Assert.That(
                    repairedState!.Destinations.Select(entry => entry.Entity),
                    Does.Contain(destinationGatewayNet),
                    "The repaired endpoint must immediately return to the open gateway UI.");
            });
            await pair.RunTicksSync(10);
            await pair.Client.WaitAssertion(() =>
            {
                var clientEntManager = pair.Client.ResolveDependency<IEntityManager>();
                Assert.That(clientEntManager.TryGetEntity(sourceGatewayNet, out var clientGateway), Is.True);
                var clientUi = clientEntManager.System<Robust.Client.GameObjects.UserInterfaceSystem>();
                Assert.That(
                    clientUi.TryGetUiState<GatewayBoundUserInterfaceState>(
                        clientGateway!.Value,
                        GatewayUiKey.Key,
                        out var repairedState),
                    Is.True);
                Assert.That(
                    repairedState!.Destinations.Select(entry => entry.Entity),
                    Does.Contain(destinationGatewayNet),
                    "The connected client must receive the self-healed destination.");
            });
        }
        finally
        {
            await server.WaitPost(() =>
            {
                cfg.SetCVar(CCVars.GatewayGeneratorEnabled, false);
                if (sourceGatewayNet != NetEntity.Invalid &&
                    entManager.TryGetEntity(sourceGatewayNet, out var sourceGateway))
                {
                    uiSystem.CloseUi(
                        sourceGateway.Value,
                        GatewayUiKey.Key,
                        server.PlayerMan.Sessions.Single());
                }

                var session = server.PlayerMan.Sessions.Single();
                if (session.AttachedEntity == clientActor)
                    server.PlayerMan.SetAttachedEntity(session, null, true);
                if (clientActor.IsValid() && entManager.EntityExists(clientActor))
                    entManager.DeleteEntity(clientActor);

                foreach (var destination in entManager
                             .AllComponents<GatewayGeneratorDestinationComponent>()
                             .Select(entry => entry.Uid)
                             .ToArray())
                {
                    var destinationMapId =
                        entManager.GetComponent<TransformComponent>(destination).MapID;
                    if (destinationMapId != MapId.Nullspace && mapSystem.MapExists(destinationMapId))
                        mapSystem.DeleteMap(destinationMapId);
                }
            });

            await pair.RunTicksSync(100);
            await server.WaitPost(() =>
            {
                if (mapId != MapId.Nullspace && mapSystem.MapExists(mapId))
                    mapSystem.DeleteMap(mapId);

                cfg.SetCVar(CCVars.GatewayGeneratorMaxDestinations, oldGeneratorMaxDestinations);
                cfg.SetCVar(CCVars.GatewayGeneratorEnabled, oldGeneratorEnabled);
            });

            await pair.RunTicksSync(20);
            try
            {
                await pair.CleanReturnAsync();
            }
            catch (Exception)
            {
                if (server.UnhandledException is { } serverException)
                    throw new Exception("The gateway regression killed the integration server.", serverException);
                if (pair.Client.UnhandledException is { } clientException)
                    throw new Exception("The gateway regression killed the integration client.", clientException);

                throw;
            }
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
