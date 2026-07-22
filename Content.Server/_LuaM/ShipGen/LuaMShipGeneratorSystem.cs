using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Server._LuaM.ShipPersistence;
using Content.Shared.Atmos.Components;
using Content.Shared.CCVar;
using Robust.Shared.Asynchronous;
using Robust.Shared.Configuration;
using Robust.Shared.Console;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Utility;

namespace Content.Server._LuaM.ShipGen;

/// <summary>
/// Requests a bounded logical ship blueprint from the LuaM gateway and realizes it from a trusted seed grid.
/// The gateway never supplies entity prototype IDs or resource paths.
/// </summary>
public sealed partial class LuaMShipGeneratorSystem : EntitySystem
{
    private const int SchemaVersion = 1;
    private const int MaxWidth = 25;
    private const int MaxHeight = 25;
    private const int MaxTiles = 400;
    private const int MaxEntities = 512;
    private const int MaxCableEntities = 800;
    private const int MaxAtmosPipeEntities = 800;
    private const int MaxResponseBytes = 256 * 1024;
    private const int RequestTimeoutSeconds = 5;
    private const int SpawnAttempts = 20;
    private const float SpawnGridPadding = 6f;
    private const int MaxNameLength = 32;
    private const int MaxRequestNameLength = 32;
    private const int MaxGeneratorVersionLength = 64;
    private const string FloorTileId = "FloorShuttleBlue";
    private const string SupplyPipePrototype = "GasPipeFourway";
    private const string WastePipePrototype = "GasPipeFourwayAlt1";
    private const string GasPortPrototype = "GasPort";
    private const string VacuumMarkerPrototype = "AtmosFixBlockerMarker";
    private static readonly ResPath SeedGridPath = new("/Maps/_LuaM/ShipGen/shipgen_seed.yml");
    private static readonly string[] InternalCablePrototypes = ["CableHV", "CableMV", "CableApcExtension"];

    private static readonly HashSet<string> AllowedPresets = new(StringComparer.OrdinalIgnoreCase)
    {
        "expedition",
        "fighter",
        "salvage",
    };

    private static readonly HashSet<string> AllowedSizes = new(StringComparer.OrdinalIgnoreCase)
    {
        "small",
        "medium",
        "large",
    };

    private static readonly IReadOnlyDictionary<string, Vector2i> SizeDimensions =
        new Dictionary<string, Vector2i>(StringComparer.OrdinalIgnoreCase)
        {
            ["small"] = new(11, 13),
            ["medium"] = new(15, 19),
            ["large"] = new(19, 23),
        };

    private static readonly HashSet<string> HullKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "wall",
        "window",
        "airlock",
        "dock",
    };

    private static readonly HashSet<string> PoweredKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "airlock",
        "console",
        "light",
        "vent",
        "scrubber",
        "thruster",
        "gyro",
        "dock",
        "weapon",
        "gunnery_server",
        "gunnery_console",
        "research_server",
        "medical",
        "salvage",
        "science",
    };

    private static readonly HashSet<string> PathBlockingKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "wall",
        "window",
        "airlock",
        "dock",
        "thruster",
        "weapon",
        "bulkhead",
        "chair",
        "gunnery_console",
        "gunnery_server",
        "gyro",
        "medical",
        "research_server",
        "salvage",
        "science",
        "air_storage",
        "waste_storage",
        "substation",
    };

    // Wall-mounted and under-floor fixtures may deliberately share a tile with a solid object.
    private static readonly HashSet<string> SolidKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "wall",
        "window",
        "airlock",
        "chair",
        "console",
        "substation",
        "bulkhead",
        "thruster",
        "gyro",
        "dock",
        "weapon",
        "gunnery_server",
        "gunnery_console",
        "research_server",
        "medical",
        "salvage",
        "science",
        "air_storage",
        "waste_storage",
    };

    private static readonly Vector2i[] CardinalOffsets =
    [
        new(1, 0),
        new(-1, 0),
        new(0, 1),
        new(0, -1),
    ];

    private static readonly Regex SafeSeedPattern = new(
        "^[a-z0-9][a-z0-9_-]{0,63}$",
        RegexOptions.Compiled);

    private static readonly Regex WhitespacePattern = new(
        "\\s+",
        RegexOptions.Compiled);

    // Logical gateway kinds are deliberately mapped server-side. Never accept a prototype ID from the provider.
    private static readonly IReadOnlyDictionary<string, KindDefinition> KindDefinitions =
        new Dictionary<string, KindDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["wall"] = new("WallReinforced", MaxTiles),
            ["window"] = new("ReinforcedWindow", 96),
            ["airlock"] = new("Airlock", 1),
            ["chair"] = new("ChairPilotSeat", 32),
            ["console"] = new("ComputerShuttle", 8),
            ["light"] = new("PoweredLEDSmallLight", 64),
            ["vent"] = new("GasVentPump", 8),
            ["scrubber"] = new("GasVentScrubber", 8),
            ["apu"] = new("GeneratorWallmountAPU", 4),
            ["apc"] = new("APCBasic", 1),
            ["substation"] = new("SubstationBasic", 1),
            ["bulkhead"] = new("WallReinforced", 32),
            ["thruster"] = new("ThrusterNfsd", 32),
            ["gyro"] = new("SmallGyroscopeNfsd", 8),
            ["dock"] = new("AirlockShuttle", 1),
            ["weapon"] = new("WeaponLaserTurretL1Phalanx", 2, PresetRestriction.Fighter),
            ["gunnery_server"] = new("GunneryServerLow", 1, PresetRestriction.Fighter),
            ["gunnery_console"] = new("ComputerGunneryConsole", 1, PresetRestriction.Fighter),
            ["research_server"] = new("ResearchAndDevelopmentServer", 1, PresetRestriction.Expedition),
            ["medical"] = new("StasisBed", 1, PresetRestriction.Expedition),
            ["salvage"] = new("OreProcessor", 1, PresetRestriction.ExpeditionOrSalvage),
            ["science"] = new("ComputerTabletopResearchAndDevelopment", 1, PresetRestriction.Expedition),
            ["air_storage"] = new("AirCanister", 1),
            ["waste_storage"] = new("StorageCanister", 1),
        };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private IHttpClientHolder _http = default!;
    [Dependency] private ITaskManager _tasks = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ITileDefinitionManager _tileDefinitions = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private IConsoleHost _console = default!;
    [Dependency] private IMapManager _mapManager = default!;
    [Dependency] private MapLoaderSystem _loader = default!;
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private MetaDataSystem _metadata = default!;
    [Dependency] private AtmosPipeLayersSystem _atmosPipeLayers = default!;
    [Dependency] private ILogManager _logs = default!;

    private ISawmill _sawmill = default!;
    private int _requestInFlight;

    public override void Initialize()
    {
        base.Initialize();
        _sawmill = _logs.GetSawmill("luam.shipgen");
    }

    public async Task<LuaMShipGenerationResult> GenerateNearAsync(
        ICommonSession admin,
        LuaMShipGenerationRequest request)
    {
        if (Interlocked.CompareExchange(ref _requestInFlight, 1, 0) != 0)
            return LuaMShipGenerationResult.Failed("Генератор уже выполняет другой запрос. Дождитесь его завершения.");

        try
        {
            if (!TryValidateRequest(request, out var requestError))
                return LuaMShipGenerationResult.Failed(requestError);

            var anchor = await RunOnMainThread(() => ResolveAdminAnchor(admin));
            if (!anchor.Success)
                return LuaMShipGenerationResult.Failed(anchor.Error);

            LuaMShipBlueprintResponse response;
            try
            {
                response = await RequestBlueprintAsync(request);
            }
            catch (Exception e)
            {
                _sawmill.Warning($"Ship blueprint request failed: {e.GetType().Name}: {e.Message}");
                return LuaMShipGenerationResult.Failed($"Не удалось получить чертёж корабля: {SafeError(e.Message)}");
            }

            if (!TryValidateBlueprint(request, response, out var blueprint, out var blueprintError))
                return LuaMShipGenerationResult.Failed($"Чертёж отклонён валидатором: {blueprintError}");

            return await RunOnMainThread(() => BuildShip(anchor.Coordinates, request, blueprint));
        }
        finally
        {
            Volatile.Write(ref _requestInFlight, 0);
        }
    }

    private async Task<LuaMShipBlueprintResponse> RequestBlueprintAsync(LuaMShipGenerationRequest request)
    {
        var configuredUrl = _configuration.GetCVar(CCVars.LuaMAiDirectorGatewayUrl).Trim();
        if (string.IsNullOrWhiteSpace(configuredUrl))
            throw new InvalidOperationException("luam.ai_director.gateway_url is empty");

        var endpoint = BuildGeneratorUri(configuredUrl);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(RequestTimeoutSeconds));
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);

        var token = _configuration.GetCVar(CCVars.LuaMAiDirectorGatewayToken).Trim();
        if (!string.IsNullOrWhiteSpace(token))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        message.Content = JsonContent.Create(
            new LuaMShipBlueprintRequest
            {
                SchemaVersion = SchemaVersion,
                Preset = request.Preset,
                Size = request.Size,
                Seed = request.Seed,
                Name = request.Name,
            },
            options: JsonOptions);

        using var httpResponse = await _http.Client.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellation.Token);

        if (!httpResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"gateway returned HTTP {(int) httpResponse.StatusCode}");

        if (httpResponse.Content.Headers.ContentLength is > MaxResponseBytes)
            throw new InvalidOperationException($"gateway response exceeds {MaxResponseBytes} bytes");

        var bytes = await ReadBoundedAsync(httpResponse.Content, cancellation.Token);
        try
        {
            return JsonSerializer.Deserialize<LuaMShipBlueprintResponse>(bytes, JsonOptions)
                   ?? throw new InvalidOperationException("gateway returned an empty JSON body");
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException("gateway returned invalid ship JSON", e);
        }
    }

    /// <summary>
    /// Sends a verified, aggregate-only saved-ship manifest to the local Python
    /// ship program. Analysis is advisory and can never block or alter persistence.
    /// </summary>
    public async Task AnalyzeSavedShipAsync(LuaMSavedShipManifest manifest)
    {
        try
        {
            var configuredUrl = _configuration.GetCVar(CCVars.LuaMAiDirectorGatewayUrl).Trim();
            if (string.IsNullOrWhiteSpace(configuredUrl))
                return;

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(RequestTimeoutSeconds));
            using var message = new HttpRequestMessage(HttpMethod.Post, BuildSavedShipAnalyzerUri(configuredUrl));
            var token = _configuration.GetCVar(CCVars.LuaMAiDirectorGatewayToken).Trim();
            if (!string.IsNullOrWhiteSpace(token))
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            message.Content = JsonContent.Create(
                new LuaMSavedShipAnalysisRequest
                {
                    SchemaVersion = 1,
                    SnapshotFormatVersion = manifest.SnapshotFormatVersion,
                    EntityCount = manifest.EntityCount,
                    PayloadSizeBytes = manifest.PayloadSizeBytes,
                    PrototypeManifestHash = manifest.PrototypeManifestHash,
                    Prototypes = manifest.Prototypes
                        .Select(entry => new LuaMSavedShipPrototypeRequest
                        {
                            Id = entry.Id,
                            Count = entry.Count,
                        })
                        .ToArray(),
                },
                options: JsonOptions);

            using var response = await _http.Client.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellation.Token);
            if (!response.IsSuccessStatusCode)
            {
                _sawmill.Warning($"Saved ship analysis returned HTTP {(int) response.StatusCode}; persistence remains unaffected.");
                return;
            }

            var bytes = await ReadBoundedAsync(response.Content, cancellation.Token);
            var analysis = JsonSerializer.Deserialize<LuaMSavedShipAnalysisResponse>(bytes, JsonOptions);
            if (analysis == null ||
                analysis.SchemaVersion != 1 ||
                analysis.EntityCount != manifest.EntityCount ||
                !string.Equals(
                    analysis.PrototypeManifestHash,
                    manifest.PrototypeManifestHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                _sawmill.Warning("Saved ship analysis response failed its manifest identity check; persistence remains unaffected.");
                return;
            }

            _sawmill.Info(
                $"Saved ship manifest analyzed: entities={analysis.EntityCount}, prototypes={analysis.PrototypeCount}, " +
                $"suggestedPreset={analysis.SuggestedPreset}, capabilities={string.Join(',', analysis.Capabilities.Where(pair => pair.Value > 0).Select(pair => $"{pair.Key}:{pair.Value}"))}");
        }
        catch (Exception exception)
        {
            _sawmill.Warning($"Saved ship analysis unavailable ({exception.GetType().Name}); persistence remains unaffected.");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];

        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;

            if (output.Length + read > MaxResponseBytes)
                throw new InvalidOperationException($"gateway response exceeds {MaxResponseBytes} bytes");

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private LuaMShipGenerationResult BuildShip(
        MapCoordinates anchor,
        LuaMShipGenerationRequest request,
        ValidatedBlueprint blueprint)
    {
        if (!_map.MapExists(anchor.MapId))
            return LuaMShipGenerationResult.Failed("Карта администратора больше не существует.");

        if (!_tileDefinitions.TryGetDefinition(FloorTileId, out var floorDefinition))
            return LuaMShipGenerationResult.Failed($"Не найден обязательный tile prototype {FloorTileId}.");

        foreach (var entity in blueprint.Entities)
        {
            if (!_prototypes.HasIndex<EntityPrototype>(entity.Prototype))
                return LuaMShipGenerationResult.Failed($"Не найден разрешённый prototype {entity.Prototype} для kind={entity.Kind}.");
        }
        foreach (var cablePrototype in InternalCablePrototypes)
        {
            if (!_prototypes.HasIndex<EntityPrototype>(cablePrototype))
                return LuaMShipGenerationResult.Failed($"Не найден внутренний cable prototype {cablePrototype}.");
        }

        var internalAtmosPrototypes = blueprint.AtmosPipes
            .Select(pipe => pipe.Prototype)
            .Concat(blueprint.GasPorts.Select(port => port.Prototype))
            .Append(VacuumMarkerPrototype)
            .Distinct(StringComparer.Ordinal);
        foreach (var prototype in internalAtmosPrototypes)
        {
            if (!_prototypes.HasIndex<EntityPrototype>(prototype))
                return LuaMShipGenerationResult.Failed($"Missing trusted atmosphere prototype {prototype}.");
        }

        if (!TryPickSpawnCoordinates(anchor, request.Size, blueprint.Tiles, out var spawnCoordinates))
        {
            return LuaMShipGenerationResult.Failed(
                $"Не удалось найти свободное место для size={request.Size} после {SpawnAttempts} попыток.");
        }
        EntityUid? gridUid = null;

        try
        {
            if (!_loader.TryLoadGrid(
                    anchor.MapId,
                    SeedGridPath,
                    out var loadedGrid,
                    offset: spawnCoordinates.Position))
            {
                return LuaMShipGenerationResult.Failed($"Не удалось загрузить seed-grid {SeedGridPath}.");
            }

            gridUid = loadedGrid.Value.Owner;
            var grid = loadedGrid.Value.Comp;
            _metadata.SetEntityName(gridUid.Value, blueprint.Name);

            var requestedTiles = blueprint.Tiles.ToHashSet();
            var changes = new List<(Vector2i GridIndices, Tile Tile)>(requestedTiles.Count + 4);
            var floorTile = new Tile(floorDefinition.TileId);
            foreach (var tile in requestedTiles)
                changes.Add((tile, floorTile));

            var existing = _map.GetAllTiles(gridUid.Value, grid)
                .Select(tile => tile.GridIndices)
                .Where(tile => !requestedTiles.Contains(tile))
                .ToArray();
            foreach (var tile in existing)
                changes.Add((tile, Tile.Empty));

            _map.SetTiles(gridUid.Value, grid, changes);

            EntityUid SpawnAnchored(string prototype, Vector2i tile, int rotation, string label)
            {
                var coordinates = new EntityCoordinates(
                    gridUid.Value,
                    new Vector2(tile.X + 0.5f, tile.Y + 0.5f));
                var spawned = SpawnAttachedTo(
                    prototype,
                    coordinates,
                    rotation: Angle.FromDegrees(rotation));
                var transform = Transform(spawned);
                if (transform.GridUid != gridUid.Value)
                    throw new InvalidOperationException($"{label} did not attach to the generated grid");
                if (!transform.Anchored &&
                    !_transform.AnchorEntity((spawned, transform), (gridUid.Value, grid), tile))
                {
                    throw new InvalidOperationException($"{label} could not be anchored at {tile.X},{tile.Y}");
                }
                return spawned;
            }

            void SetAtmosLayer(EntityUid uid, AtmosPipeLayer layer, string label)
            {
                if (!TryComp<AtmosPipeLayersComponent>(uid, out var layers))
                    throw new InvalidOperationException($"{label} has no atmosphere pipe-layer component");
                _atmosPipeLayers.SetPipeLayer((uid, layers), layer);
                if (layers.CurrentPipeLayer != layer)
                    throw new InvalidOperationException($"{label} refused trusted pipe layer {layer}");
            }

            foreach (var plannedCable in blueprint.Cables)
            {
                SpawnAnchored(
                    plannedCable.Prototype,
                    plannedCable.Position,
                    0,
                    plannedCable.Prototype);
            }

            // Ports must exist before their portable canisters are anchored. All atmosphere
            // endpoints get their final layers before crossing pipe networks are spawned.
            foreach (var port in blueprint.GasPorts
                         .OrderBy(port => port.Position.Y)
                         .ThenBy(port => port.Position.X)
                         .ThenBy(port => port.Layer))
            {
                var spawned = SpawnAnchored(port.Prototype, port.Position, port.Rotation, port.Prototype);
                SetAtmosLayer(spawned, port.Layer, port.Prototype);
            }

            foreach (var entity in blueprint.Entities
                         .Where(entity => IsAtmosEndpointKind(entity.Kind))
                         .OrderBy(entity => entity.Y)
                         .ThenBy(entity => entity.X)
                         .ThenBy(entity => entity.Kind, StringComparer.Ordinal))
            {
                var spawned = SpawnAnchored(
                    entity.Prototype,
                    EntityPosition(entity),
                    entity.Rotation,
                    $"kind={entity.Kind}");
                if (entity.Kind == "vent")
                    SetAtmosLayer(spawned, AtmosPipeLayer.Primary, $"kind={entity.Kind}");
                else if (entity.Kind == "scrubber")
                    SetAtmosLayer(spawned, AtmosPipeLayer.Secondary, $"kind={entity.Kind}");
            }

            foreach (var pipe in blueprint.AtmosPipes
                         .OrderBy(pipe => pipe.Position.Y)
                         .ThenBy(pipe => pipe.Position.X)
                         .ThenBy(pipe => pipe.Layer))
            {
                var spawned = SpawnAnchored(pipe.Prototype, pipe.Position, 0, pipe.Prototype);
                SetAtmosLayer(spawned, pipe.Layer, pipe.Prototype);
            }

            foreach (var entity in blueprint.Entities
                         .Where(entity => !IsAtmosEndpointKind(entity.Kind))
                         .OrderBy(entity => SpawnPriority(entity.Kind))
                         .ThenBy(entity => entity.Y)
                         .ThenBy(entity => entity.X)
                         .ThenBy(entity => entity.Kind, StringComparer.Ordinal))
            {
                SpawnAnchored(
                    entity.Prototype,
                    EntityPosition(entity),
                    entity.Rotation,
                    $"kind={entity.Kind}");
            }

            foreach (var tile in blueprint.VacuumTiles)
            {
                var marker = SpawnAnchored(VacuumMarkerPrototype, tile, 0, VacuumMarkerPrototype);
                EnsureComp<AirtightComponent>(marker);
            }

            EnsureComp<GridAtmosphereComponent>(gridUid.Value);
            EnsureComp<GasTileOverlayComponent>(gridUid.Value);
            _console.ExecuteCommand(null, $"fixgridatmos {GetNetEntity(gridUid.Value)}");

            var kindSummary = string.Join(
                ", ",
                blueprint.Entities
                    .GroupBy(entity => entity.Kind, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(group => $"{group.Key}={group.Count()}"));
            _sawmill.Info(
                $"Generated ship '{blueprint.Name}' preset={request.Preset} size={request.Size} " +
                $"seed={blueprint.Seed} tiles={blueprint.Tiles.Count} entities={blueprint.Entities.Count} " +
                $"cables={blueprint.Cables.Count} atmosPipes={blueprint.AtmosPipes.Count} " +
                $"vacuumTiles={blueprint.VacuumTiles.Count} grid={gridUid.Value}");

            return LuaMShipGenerationResult.Succeeded(
                gridUid.Value,
                $"Создан корабль «{blueprint.Name}»: tiles={blueprint.Tiles.Count}, entities={blueprint.Entities.Count}" +
                (string.IsNullOrWhiteSpace(kindSummary) ? "." : $" ({kindSummary})."));
        }
        catch (Exception e)
        {
            if (gridUid is { } failedGrid && Exists(failedGrid))
                Del(failedGrid);

            _sawmill.Error($"Generated ship rollback: {e.GetType().Name}: {e.Message}");
            return LuaMShipGenerationResult.Failed($"Сборка корабля отменена и полностью удалена: {SafeError(e.Message)}");
        }
    }

    private AnchorResult ResolveAdminAnchor(ICommonSession admin)
    {
        if (admin.AttachedEntity is not { Valid: true } attached || !Exists(attached))
            return AnchorResult.Failed("Для генерации администратор должен быть прикреплён к сущности в игре.");

        var coordinates = _transform.ToMapCoordinates(Transform(attached).Coordinates, logError: false);
        if (coordinates == MapCoordinates.Nullspace || !_map.MapExists(coordinates.MapId))
            return AnchorResult.Failed("Не удалось определить текущую карту администратора.");

        return AnchorResult.Succeeded(coordinates);
    }

    private bool TryPickSpawnCoordinates(
        MapCoordinates anchor,
        string size,
        List<Vector2i> tiles,
        out MapCoordinates coordinates)
    {
        var baseRadius = size.ToLowerInvariant() switch
        {
            "small" => 32f,
            "medium" => 48f,
            "large" => 68f,
            _ => 48f,
        };
        var localBounds = new Box2(
            tiles.Min(tile => tile.X),
            tiles.Min(tile => tile.Y),
            tiles.Max(tile => tile.X) + 1,
            tiles.Max(tile => tile.Y) + 1);
        var shipSpan = MathF.Max(localBounds.Width, localBounds.Height);
        var occupiedBounds = _mapManager.GetAllGrids(anchor.MapId)
            .Select(grid => _transform.GetWorldMatrix(grid.Owner).TransformBox(grid.Comp.LocalAABB))
            .ToArray();

        for (var attempt = 0; attempt < SpawnAttempts; attempt++)
        {
            var radius = baseRadius + shipSpan * 0.5f + attempt * (shipSpan + SpawnGridPadding * 2f);
            var center = anchor.Position + _random.NextAngle().ToVec() * radius;
            var origin = center - localBounds.Center;
            var proposedBounds = localBounds.Translated(origin).Enlarged(SpawnGridPadding);
            if (occupiedBounds.Any(existing => existing.Intersects(proposedBounds)))
                continue;

            coordinates = new MapCoordinates(origin, anchor.MapId);
            return true;
        }

        coordinates = MapCoordinates.Nullspace;
        return false;
    }

    private static bool TryValidateRequest(LuaMShipGenerationRequest request, out string error)
    {
        if (!AllowedPresets.Contains(request.Preset))
        {
            error = "preset должен быть expedition, fighter или salvage.";
            return false;
        }

        if (!AllowedSizes.Contains(request.Size))
        {
            error = "size должен быть small, medium или large.";
            return false;
        }

        if (!request.Seed.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
            !SafeSeedPattern.IsMatch(request.Seed))
        {
            error = "seed должен быть auto либо коротким идентификатором из букв, цифр, '_' и '-'.";
            return false;
        }

        if (request.Name.Length > MaxRequestNameLength || request.Name.Any(char.IsControl))
        {
            error = "Имя корабля содержит недопустимые символы или слишком длинное.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryValidateBlueprint(
        LuaMShipGenerationRequest request,
        LuaMShipBlueprintResponse response,
        out ValidatedBlueprint blueprint,
        out string error)
    {
        blueprint = default!;

        if (response.SchemaVersion != SchemaVersion)
            return Fail($"schemaVersion={response.SchemaVersion}, ожидался {SchemaVersion}", out error);
        if (string.IsNullOrWhiteSpace(response.GeneratorVersion) ||
            response.GeneratorVersion.Length > MaxGeneratorVersionLength)
            return Fail("generatorVersion отсутствует или слишком длинный", out error);
        if (!string.Equals(response.Preset, request.Preset, StringComparison.OrdinalIgnoreCase))
            return Fail("response.preset не совпадает с request.preset", out error);
        if (!TryValidateResponseSeed(request.Seed, response.Seed))
            return Fail("response.seed не совпадает с request.seed или небезопасен", out error);
        if (response.Width is < 1 or > MaxWidth || response.Height is < 1 or > MaxHeight)
            return Fail($"размер должен быть в пределах 1..{MaxWidth} x 1..{MaxHeight}", out error);
        if (!SizeDimensions.TryGetValue(request.Size, out var dimensions) ||
            response.Width != dimensions.X || response.Height != dimensions.Y)
        {
            return Fail(
                $"размер {response.Width}x{response.Height} не совпадает с size={request.Size} " +
                $"({dimensions.X}x{dimensions.Y})",
                out error);
        }
        if (response.Tiles is not { Count: > 0 } || response.Tiles.Count > MaxTiles)
            return Fail($"tiles должен содержать 1..{MaxTiles} записей", out error);
        if (response.Entities == null || response.Entities.Count > MaxEntities)
            return Fail($"entities должен содержать 0..{MaxEntities} записей", out error);
        if (response.Summary.ValueKind != JsonValueKind.Object)
            return Fail("summary должен быть JSON object", out error);
        if (response.Preview.ValueKind != JsonValueKind.Array ||
            response.Preview.GetArrayLength() != response.Height ||
            response.Preview.EnumerateArray().Any(line =>
                line.ValueKind != JsonValueKind.String || line.GetString()!.Length != response.Width))
        {
            return Fail("preview должен быть массивом строк размером height x width", out error);
        }

        var tiles = new HashSet<Vector2i>();
        foreach (var tile in response.Tiles)
        {
            if (tile == null)
                return Fail("tiles содержит null", out error);
            if (!WithinBounds(tile.X, tile.Y, response.Width, response.Height))
                return Fail($"tile {tile.X},{tile.Y} выходит за границы", out error);
            if (!tiles.Add(new Vector2i(tile.X, tile.Y)))
                return Fail($"tile {tile.X},{tile.Y} продублирован", out error);
        }

        if (!FloorIsConnected(tiles))
            return Fail("floor tiles должны образовывать одну cardinal-связную область", out error);

        var kindCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var entityKeys = new HashSet<(string Kind, int X, int Y)>();
        var solidTiles = new HashSet<Vector2i>();
        var systemTiles = new HashSet<Vector2i>();
        var hullCounts = new Dictionary<Vector2i, int>();
        var bulkheadTiles = new HashSet<Vector2i>();
        var thrusterPods = new Dictionary<Vector2i, Vector2i>();
        var weaponPods = new Dictionary<Vector2i, Vector2i>();
        var podTiles = new HashSet<Vector2i>();
        var entities = new List<ValidatedEntity>(response.Entities.Count);
        foreach (var entity in response.Entities)
        {
            if (entity == null || string.IsNullOrWhiteSpace(entity.Kind))
                return Fail("entities содержит null или пустой kind", out error);
            var kind = entity.Kind.Trim().ToLowerInvariant();
            if (!KindDefinitions.TryGetValue(kind, out var definition))
                return Fail($"неизвестный logical kind '{kind}'", out error);
            if (!KindAllowedForPreset(definition.Restriction, request.Preset))
                return Fail($"kind '{kind}' запрещён для preset={request.Preset}", out error);
            var position = new Vector2i(entity.X, entity.Y);
            if (!WithinBounds(entity.X, entity.Y, response.Width, response.Height) ||
                !tiles.Contains(position))
                return Fail($"entity {kind} at {entity.X},{entity.Y} находится вне floor tiles", out error);
            if (entity.Rotation is not (0 or 90 or 180 or 270))
                return Fail($"entity {kind} имеет не cardinal rotation={entity.Rotation}", out error);
            if (!entityKeys.Add((kind, entity.X, entity.Y)))
                return Fail($"entity {kind} at {entity.X},{entity.Y} продублирован", out error);
            if (SolidKinds.Contains(kind) && !solidTiles.Add(position))
                return Fail($"несколько solid entities занимают tile {entity.X},{entity.Y}", out error);
            if (!HullKinds.Contains(kind) && !systemTiles.Add(position))
                return Fail($"несколько non-hull systems занимают tile {entity.X},{entity.Y}", out error);

            var boundary = IsBoundaryFloorTile(position, tiles);
            if (HullKinds.Contains(kind))
            {
                if (!boundary)
                    return Fail($"hull entity {kind} at {entity.X},{entity.Y} находится не на границе", out error);
                if (kind.Equals("dock", StringComparison.OrdinalIgnoreCase) &&
                    tiles.Contains(position + DockingNormal(entity.Rotation)))
                {
                    return Fail($"dock at {entity.X},{entity.Y} направлен внутрь floor tiles", out error);
                }
                hullCounts[position] = hullCounts.GetValueOrDefault(position) + 1;
            }
            else if (kind.Equals("bulkhead", StringComparison.OrdinalIgnoreCase))
            {
                if (boundary)
                    return Fail($"bulkhead at {entity.X},{entity.Y} должен находиться на interior tile", out error);
                bulkheadTiles.Add(position);
            }
            else if (kind.Equals("thruster", StringComparison.OrdinalIgnoreCase))
            {
                var outward = ThrusterOutward(entity.Rotation);
                if (!boundary || tiles.Contains(position + outward))
                {
                    return Fail(
                        $"thruster at {entity.X},{entity.Y} должен быть boundary pod с nozzle в space",
                        out error);
                }
                thrusterPods.Add(position, outward);
                podTiles.Add(position);
            }
            else if (kind.Equals("weapon", StringComparison.OrdinalIgnoreCase))
            {
                var outward = DockingNormal(entity.Rotation);
                if (!boundary || tiles.Contains(position + outward))
                    return Fail($"weapon at {entity.X},{entity.Y} должен быть направленным наружу boundary pod", out error);
                weaponPods.Add(position, outward);
                podTiles.Add(position);
            }
            else if (boundary)
            {
                return Fail($"system {kind} at {entity.X},{entity.Y} должен находиться на interior tile", out error);
            }

            var count = kindCounts.GetValueOrDefault(kind) + 1;
            if (count > definition.MaxCount)
                return Fail($"kind '{kind}' превышает лимит {definition.MaxCount}", out error);
            kindCounts[kind] = count;
            entities.Add(new ValidatedEntity(kind, definition.Prototype, entity.X, entity.Y, entity.Rotation));
        }

        var usedPodBulkheads = new HashSet<Vector2i>();
        foreach (var (thruster, outward) in thrusterPods)
        {
            var inward = thruster - outward;
            if (!bulkheadTiles.Contains(inward))
                return Fail($"thruster pod at {thruster.X},{thruster.Y} не имеет inward bulkhead", out error);
            if (!usedPodBulkheads.Add(inward))
                return Fail($"bulkhead at {inward.X},{inward.Y} обслуживает несколько pods", out error);
        }

        foreach (var (weapon, outward) in weaponPods)
        {
            var inward = weapon - outward;
            if (!bulkheadTiles.Contains(inward))
                return Fail($"weapon pod at {weapon.X},{weapon.Y} не имеет inward bulkhead", out error);
            if (!usedPodBulkheads.Add(inward))
            {
                return Fail(
                    $"bulkhead at {inward.X},{inward.Y} обслуживает несколько pods",
                    out error);
            }
        }

        var thrusterNormals = thrusterPods.Values.ToHashSet();
        foreach (var cardinal in CardinalOffsets)
        {
            if (!thrusterNormals.Contains(cardinal))
                return Fail($"thruster layout не покрывает outward normal {cardinal.X},{cardinal.Y}", out error);
        }

        var forwardY = tiles.Min(tile => tile.Y);
        var aftY = tiles.Max(tile => tile.Y);
        foreach (var thruster in entities.Where(entity => entity.Kind == "thruster"))
        {
            if (thruster.Rotation == 0 && thruster.Y != aftY)
                return Fail($"thruster at {thruster.X},{thruster.Y} rotation=0 должен быть на aft boundary", out error);
            if (thruster.Rotation == 180 && thruster.Y != forwardY)
                return Fail($"thruster at {thruster.X},{thruster.Y} rotation=180 должен быть на forward boundary", out error);
            if (thruster.Rotation == 90 &&
                thruster.X != tiles.Where(tile => tile.Y == thruster.Y).Min(tile => tile.X))
            {
                return Fail($"thruster at {thruster.X},{thruster.Y} rotation=90 должен быть на left boundary", out error);
            }
            if (thruster.Rotation == 270 &&
                thruster.X != tiles.Where(tile => tile.Y == thruster.Y).Max(tile => tile.X))
            {
                return Fail($"thruster at {thruster.X},{thruster.Y} rotation=270 должен быть на right boundary", out error);
            }
        }

        foreach (var weapon in entities.Where(entity => entity.Kind == "weapon"))
        {
            if (weapon.Rotation is not (90 or 270) || weapon.Y == forwardY || weapon.Y == aftY)
                return Fail($"weapon at {weapon.X},{weapon.Y} должен быть ориентирован наружу с side boundary", out error);
            var rowEdge = weapon.Rotation == 270
                ? tiles.Where(tile => tile.Y == weapon.Y).Min(tile => tile.X)
                : tiles.Where(tile => tile.Y == weapon.Y).Max(tile => tile.X);
            if (weapon.X != rowEdge)
                return Fail($"weapon at {weapon.X},{weapon.Y} должен находиться на side boundary", out error);
        }

        if (bulkheadTiles.Count != usedPodBulkheads.Count)
            return Fail("каждый bulkhead должен соответствовать ровно одному exposed pod", out error);

        foreach (var tile in tiles.Where(tile => IsBoundaryFloorTile(tile, tiles)))
        {
            var coverCount = hullCounts.GetValueOrDefault(tile) + (podTiles.Contains(tile) ? 1 : 0);
            if (coverCount != 1)
            {
                return Fail(
                    $"boundary tile {tile.X},{tile.Y} должен иметь ровно один hull или thruster/weapon pod",
                    out error);
            }
        }

        if (kindCounts.GetValueOrDefault("console") != 1)
            return Fail("чертёж должен содержать ровно один console", out error);
        foreach (var accessKind in new[] { "airlock", "dock" })
        {
            if (kindCounts.GetValueOrDefault(accessKind) != 1)
                return Fail($"чертёж должен содержать ровно один {accessKind}", out error);
        }

        foreach (var storageKind in new[] { "air_storage", "waste_storage" })
        {
            if (kindCounts.GetValueOrDefault(storageKind) != 1)
                return Fail($"blueprint must contain exactly one {storageKind}", out error);
        }

        var expectedAtmosDevices = request.Size.ToLowerInvariant() switch
        {
            "small" => 1,
            "medium" or "large" => 2,
            _ => 0,
        };
        foreach (var deviceKind in new[] { "vent", "scrubber" })
        {
            if (kindCounts.GetValueOrDefault(deviceKind) != expectedAtmosDevices)
            {
                return Fail(
                    $"size={request.Size} must contain exactly {expectedAtmosDevices} {deviceKind} entities",
                    out error);
            }
        }

        var dock = entities.Single(entity => entity.Kind == "dock");
        var dockPosition = EntityPosition(dock);
        var dockInward = dockPosition + new Vector2i(0, -1);
        if (dock.Y != aftY || dock.Rotation != 180)
            return Fail("dock должен находиться на aft boundary с rotation=180", out error);
        if (tiles.Contains(dockPosition + new Vector2i(0, 1)) ||
            !tiles.Contains(dockInward) || IsBoundaryFloorTile(dockInward, tiles))
        {
            return Fail("dock должен смотреть в space и иметь interior inward tile", out error);
        }

        var console = entities.Single(entity => entity.Kind == "console");
        var pathBlocked = entities
            .Where(entity => PathBlockingKinds.Contains(entity.Kind))
            .Select(EntityPosition)
            .ToHashSet();
        if (!HasWalkablePath(tiles, dockInward, EntityPosition(console), pathBlocked))
            return Fail("dock inward tile должен иметь walkable path до console", out error);

        foreach (var requiredKind in new[] { "apu", "substation", "apc", "thruster", "gyro" })
        {
            if (kindCounts.GetValueOrDefault(requiredKind) < 1)
                return Fail($"чертёж должен содержать хотя бы один {requiredKind}", out error);
        }
        var fighter = request.Preset.Equals("fighter", StringComparison.OrdinalIgnoreCase);
        var expectedThrusters = request.Size.ToLowerInvariant() switch
        {
            "small" => 4,
            "medium" => 5,
            "large" => 6,
            _ => 0,
        } + (fighter ? 2 : 0);
        if (kindCounts.GetValueOrDefault("thruster") != expectedThrusters)
            return Fail($"preset={request.Preset} size={request.Size} должен содержать thruster={expectedThrusters}", out error);

        var thrusterRotations = entities
            .Where(entity => entity.Kind == "thruster")
            .GroupBy(entity => entity.Rotation)
            .ToDictionary(group => group.Key, group => group.Count());
        if (thrusterRotations.GetValueOrDefault(0) != expectedThrusters - 3 ||
            thrusterRotations.GetValueOrDefault(180) != 1 ||
            thrusterRotations.GetValueOrDefault(90) != 1 ||
            thrusterRotations.GetValueOrDefault(270) != 1)
        {
            return Fail("thruster rotations должны содержать по одному 180/90/270, остальные 0", out error);
        }

        var expectedWeapons = fighter ? 2 : 0;
        if (kindCounts.GetValueOrDefault("weapon") != expectedWeapons)
            return Fail($"preset={request.Preset} должен содержать weapon={expectedWeapons}", out error);
        if (fighter)
        {
            var weaponRotations = entities
                .Where(entity => entity.Kind == "weapon")
                .GroupBy(entity => entity.Rotation)
                .ToDictionary(group => group.Key, group => group.Count());
            if (weaponRotations.GetValueOrDefault(90) != 1 || weaponRotations.GetValueOrDefault(270) != 1)
                return Fail("fighter должен содержать по одному weapon rotation=90 и rotation=270", out error);
        }
        foreach (var gunneryKind in new[] { "gunnery_server", "gunnery_console" })
        {
            var expectedCount = fighter ? 1 : 0;
            if (kindCounts.GetValueOrDefault(gunneryKind) != expectedCount)
                return Fail($"preset={request.Preset} должен содержать {gunneryKind}={expectedCount}", out error);
        }

        var expedition = request.Preset.Equals("expedition", StringComparison.OrdinalIgnoreCase);
        var salvage = request.Preset.Equals("salvage", StringComparison.OrdinalIgnoreCase);
        var expectedMissionKinds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["medical"] = expedition ? 1 : 0,
            ["science"] = expedition ? 1 : 0,
            ["salvage"] = expedition || salvage ? 1 : 0,
        };
        foreach (var (missionKind, expectedCount) in expectedMissionKinds)
        {
            if (kindCounts.GetValueOrDefault(missionKind) != expectedCount)
            {
                return Fail(
                    $"preset={request.Preset} должен содержать {missionKind}={expectedCount}",
                    out error);
            }
        }
        var expectedResearchServers = expedition ? 1 : 0;
        if (kindCounts.GetValueOrDefault("research_server") != expectedResearchServers)
        {
            return Fail(
                $"preset={request.Preset} должен содержать research_server={expectedResearchServers}",
                out error);
        }

        var apuSupply = kindCounts.GetValueOrDefault("apu") * 6000;
        var minimumDemand =
            kindCounts.GetValueOrDefault("thruster") * 1500 +
            kindCounts.GetValueOrDefault("gyro") * 1500 +
            kindCounts.GetValueOrDefault("gunnery_server") * 250 +
            kindCounts.GetValueOrDefault("gunnery_console") * 200 +
            kindCounts.GetValueOrDefault("research_server") * 200 +
            kindCounts.GetValueOrDefault("medical") * 1000 +
            4000;
        if (apuSupply < minimumDemand)
            return Fail($"power budget недостаточен: APU={apuSupply}W, minimum demand={minimumDemand}W", out error);

        if (!TryBuildCablePlan(tiles, entities, out var cables, out var cableError))
            return Fail(cableError, out error);
        if (!TryBuildAtmosPlan(tiles, entities, out var atmosPipes, out var gasPorts, out var atmosError))
            return Fail(atmosError, out error);

        var requestedName = string.IsNullOrWhiteSpace(request.Name) ? response.Name ?? string.Empty : request.Name;
        var name = SanitizeName(requestedName, request.Preset, response.Seed!);
        blueprint = new ValidatedBlueprint(
            response.Seed!,
            name,
            response.Width,
            response.Height,
            tiles.OrderBy(tile => tile.Y).ThenBy(tile => tile.X).ToList(),
            entities,
            cables,
            atmosPipes,
            gasPorts,
            podTiles.OrderBy(tile => tile.Y).ThenBy(tile => tile.X).ToList());
        error = string.Empty;
        return true;
    }

    private static bool TryValidateResponseSeed(string requestSeed, string? responseSeed)
    {
        if (string.IsNullOrWhiteSpace(responseSeed) || !SafeSeedPattern.IsMatch(responseSeed))
            return false;

        return requestSeed.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
               responseSeed.Equals(requestSeed, StringComparison.OrdinalIgnoreCase);
    }

    private static bool KindAllowedForPreset(PresetRestriction restriction, string preset)
    {
        return restriction switch
        {
            PresetRestriction.None => true,
            PresetRestriction.Fighter => preset.Equals("fighter", StringComparison.OrdinalIgnoreCase),
            PresetRestriction.Expedition => preset.Equals("expedition", StringComparison.OrdinalIgnoreCase),
            PresetRestriction.ExpeditionOrSalvage =>
                preset.Equals("expedition", StringComparison.OrdinalIgnoreCase) ||
                preset.Equals("salvage", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static bool WithinBounds(int x, int y, int width, int height)
        => x >= 0 && y >= 0 && x < width && y < height;

    private static Vector2i DockingNormal(int rotation)
    {
        return rotation switch
        {
            0 => new Vector2i(0, -1),
            90 => new Vector2i(1, 0),
            180 => new Vector2i(0, 1),
            270 => new Vector2i(-1, 0),
            _ => Vector2i.Zero,
        };
    }

    private static Vector2i ThrusterOutward(int rotation)
    {
        return rotation switch
        {
            0 => new Vector2i(0, 1),
            90 => new Vector2i(-1, 0),
            180 => new Vector2i(0, -1),
            270 => new Vector2i(1, 0),
            _ => Vector2i.Zero,
        };
    }

    private static bool IsBoundaryFloorTile(Vector2i tile, HashSet<Vector2i> tiles)
        => CardinalOffsets.Any(offset => !tiles.Contains(tile + offset));

    private static bool FloorIsConnected(HashSet<Vector2i> tiles)
    {
        var visited = new HashSet<Vector2i>();
        var pending = new Queue<Vector2i>();
        var first = tiles.First();
        visited.Add(first);
        pending.Enqueue(first);

        while (pending.TryDequeue(out var current))
        {
            foreach (var offset in CardinalOffsets)
            {
                var neighbor = current + offset;
                if (tiles.Contains(neighbor) && visited.Add(neighbor))
                    pending.Enqueue(neighbor);
            }
        }

        return visited.Count == tiles.Count;
    }

    private static bool HasWalkablePath(
        HashSet<Vector2i> tiles,
        Vector2i start,
        Vector2i goal,
        HashSet<Vector2i> blocked)
    {
        if (!tiles.Contains(start) || !tiles.Contains(goal) || blocked.Contains(start))
            return false;

        var visited = new HashSet<Vector2i> { start };
        var pending = new Queue<Vector2i>();
        pending.Enqueue(start);
        while (pending.TryDequeue(out var current))
        {
            if (current == goal)
                return true;

            foreach (var offset in CardinalOffsets)
            {
                var neighbor = current + offset;
                if (tiles.Contains(neighbor) && !blocked.Contains(neighbor) && visited.Add(neighbor))
                    pending.Enqueue(neighbor);
            }
        }

        return false;
    }

    private static bool TryBuildAtmosPlan(
        HashSet<Vector2i> floorTiles,
        List<ValidatedEntity> entities,
        out List<ValidatedAtmosPipe> pipes,
        out List<ValidatedGasPort> ports,
        out string error)
    {
        pipes = new List<ValidatedAtmosPipe>();
        ports = new List<ValidatedGasPort>();

        if (!TryBuildAtmosNetwork(
                floorTiles,
                entities,
                "supply",
                "vent",
                "air_storage",
                SupplyPipePrototype,
                AtmosPipeLayer.Primary,
                pipes,
                ports,
                out error))
        {
            return false;
        }

        if (!TryBuildAtmosNetwork(
                floorTiles,
                entities,
                "waste",
                "scrubber",
                "waste_storage",
                WastePipePrototype,
                AtmosPipeLayer.Secondary,
                pipes,
                ports,
                out error))
        {
            pipes.Clear();
            ports.Clear();
            return false;
        }

        if (pipes.Count > MaxAtmosPipeEntities)
        {
            error = $"atmosphere pipe plan exceeds hard limit {MaxAtmosPipeEntities}: {pipes.Count}";
            pipes.Clear();
            ports.Clear();
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryBuildAtmosNetwork(
        HashSet<Vector2i> floorTiles,
        List<ValidatedEntity> entities,
        string networkName,
        string deviceKind,
        string storageKind,
        string pipePrototype,
        AtmosPipeLayer layer,
        List<ValidatedAtmosPipe> pipes,
        List<ValidatedGasPort> ports,
        out string error)
    {
        var endpoints = entities
            .Where(entity => entity.Kind == deviceKind || entity.Kind == storageKind)
            .OrderBy(entity => entity.Y)
            .ThenBy(entity => entity.X)
            .ToArray();
        if (endpoints.Length == 0)
        {
            error = $"{networkName} atmosphere network has no endpoints";
            return false;
        }

        var blockedEndpoints = endpoints.Select(EntityPosition).ToHashSet();
        var routeTiles = floorTiles
            .Where(tile => !IsBoundaryFloorTile(tile, floorTiles) && !blockedEndpoints.Contains(tile))
            .ToHashSet();
        var connections = new Dictionary<ValidatedEntity, Vector2i>();
        foreach (var endpoint in endpoints)
        {
            var connection = EntityPosition(endpoint) + DockingNormal(endpoint.Rotation);
            if (!routeTiles.Contains(connection))
            {
                error =
                    $"{networkName} atmosphere endpoint {endpoint.Kind} at {endpoint.X},{endpoint.Y} " +
                    "does not face a safe interior pipe tile";
                return false;
            }
            connections.Add(endpoint, connection);
        }

        var storage = endpoints.Single(entity => entity.Kind == storageKind);
        var hub = connections[storage];
        var consumers = endpoints
            .Where(entity => entity != storage)
            .Select(entity => connections[entity]);
        if (!TryBuildCableNetwork(routeTiles, hub, consumers, out var network))
        {
            error = $"{networkName} atmosphere endpoints are disconnected";
            return false;
        }

        foreach (var position in network.OrderBy(position => position.Y).ThenBy(position => position.X))
            pipes.Add(new ValidatedAtmosPipe(pipePrototype, layer, position));
        ports.Add(new ValidatedGasPort(
            GasPortPrototype,
            layer,
            EntityPosition(storage),
            storage.Rotation));

        error = string.Empty;
        return true;
    }

    private static bool TryBuildCablePlan(
        HashSet<Vector2i> floorTiles,
        List<ValidatedEntity> entities,
        out List<ValidatedCable> cables,
        out string error)
    {
        cables = new List<ValidatedCable>();
        var substation = EntityPosition(entities.Single(entity => entity.Kind == "substation"));
        var apus = entities.Where(entity => entity.Kind == "apu").Select(EntityPosition).ToArray();
        var apcs = entities.Where(entity => entity.Kind == "apc").Select(EntityPosition).ToArray();
        var primaryApc = apcs.OrderBy(position => position.Y).ThenBy(position => position.X).First();
        var poweredConsumers = entities
            .Where(entity => PoweredKinds.Contains(entity.Kind))
            .Select(EntityPosition)
            .Concat(apcs.Where(position => position != primaryApc))
            .ToArray();

        if (!TryBuildCableNetwork(floorTiles, substation, apus, out var highVoltage))
        {
            error = "не удалось проложить CableHV от APU к substation";
            return false;
        }
        if (!TryBuildCableNetwork(floorTiles, substation, apcs, out var mediumVoltage))
        {
            error = "не удалось проложить CableMV от substation к APC";
            return false;
        }
        if (!TryBuildCableNetwork(floorTiles, primaryApc, poweredConsumers, out var apcNetwork))
        {
            error = "не удалось проложить CableApcExtension от APC к потребителям";
            return false;
        }

        AddPlannedCables(cables, "CableHV", highVoltage);
        AddPlannedCables(cables, "CableMV", mediumVoltage);
        AddPlannedCables(cables, "CableApcExtension", apcNetwork);
        if (cables.Count > MaxCableEntities)
        {
            error = $"cable plan превышает hard limit {MaxCableEntities}: {cables.Count}";
            cables.Clear();
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryBuildCableNetwork(
        HashSet<Vector2i> floorTiles,
        Vector2i hub,
        IEnumerable<Vector2i> endpoints,
        out HashSet<Vector2i> network)
    {
        network = new HashSet<Vector2i> { hub };
        foreach (var endpoint in endpoints.Distinct().OrderBy(position => position.Y).ThenBy(position => position.X))
        {
            if (network.Contains(endpoint))
                continue;

            var pending = new Queue<Vector2i>();
            var visited = new HashSet<Vector2i> { endpoint };
            var parents = new Dictionary<Vector2i, Vector2i>();
            pending.Enqueue(endpoint);
            Vector2i? connection = null;

            while (pending.TryDequeue(out var current) && connection == null)
            {
                foreach (var offset in CardinalOffsets)
                {
                    var neighbor = current + offset;
                    if (!floorTiles.Contains(neighbor) || !visited.Add(neighbor))
                        continue;

                    parents[neighbor] = current;
                    if (network.Contains(neighbor))
                    {
                        connection = neighbor;
                        break;
                    }
                    pending.Enqueue(neighbor);
                }
            }

            if (connection == null)
                return false;

            var cursor = connection.Value;
            network.Add(cursor);
            while (cursor != endpoint)
            {
                cursor = parents[cursor];
                network.Add(cursor);
            }
        }

        return true;
    }

    private static void AddPlannedCables(
        List<ValidatedCable> cables,
        string prototype,
        HashSet<Vector2i> positions)
    {
        foreach (var position in positions.OrderBy(position => position.Y).ThenBy(position => position.X))
            cables.Add(new ValidatedCable(prototype, position));
    }

    private static bool IsAtmosEndpointKind(string kind)
        => kind is "vent" or "scrubber" or "air_storage" or "waste_storage";

    private static Vector2i EntityPosition(ValidatedEntity entity) => new(entity.X, entity.Y);

    private static int SpawnPriority(string kind)
    {
        return kind switch
        {
            "apu" or "substation" or "apc" => 0,
            "gunnery_server" or "research_server" => 1,
            "gunnery_console" or "weapon" or "science" => 2,
            _ => 3,
        };
    }

    private static string SanitizeName(string value, string preset, string seed)
    {
        var printable = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        printable = WhitespacePattern.Replace(printable, " ");
        if (string.IsNullOrWhiteSpace(printable))
            printable = $"LuaM {preset} {seed}";
        return printable.Length <= MaxNameLength ? printable : printable[..MaxNameLength].TrimEnd();
    }

    private static Uri BuildGeneratorUri(string gatewayUrl)
    {
        var builder = new UriBuilder(gatewayUrl)
        {
            Path = "/generate_ship",
            Query = string.Empty,
        };
        if (builder.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("gateway URL must use http or https");
        return builder.Uri;
    }

    private static Uri BuildSavedShipAnalyzerUri(string gatewayUrl)
    {
        var builder = new UriBuilder(gatewayUrl)
        {
            Path = "/analyze_saved_ship",
            Query = string.Empty,
        };
        if (builder.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("gateway URL must use http or https");
        return builder.Uri;
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private static string SafeError(string message)
    {
        var sanitized = message.ReplaceLineEndings(" ").Trim();
        return sanitized.Length <= 180 ? sanitized : sanitized[..180];
    }

    private Task<T> RunOnMainThread<T>(Func<T> action)
    {
        var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tasks.RunOnMainThread(() =>
        {
            try
            {
                source.TrySetResult(action());
            }
            catch (Exception e)
            {
                source.TrySetException(e);
            }
        });
        return source.Task;
    }

    private sealed record KindDefinition(
        string Prototype,
        int MaxCount,
        PresetRestriction Restriction = PresetRestriction.None);

    private enum PresetRestriction : byte
    {
        None,
        Fighter,
        Expedition,
        ExpeditionOrSalvage,
    }

    private readonly record struct AnchorResult(bool Success, MapCoordinates Coordinates, string Error)
    {
        public static AnchorResult Succeeded(MapCoordinates coordinates) => new(true, coordinates, string.Empty);
        public static AnchorResult Failed(string error) => new(false, MapCoordinates.Nullspace, error);
    }

    private sealed record ValidatedBlueprint(
        string Seed,
        string Name,
        int Width,
        int Height,
        List<Vector2i> Tiles,
        List<ValidatedEntity> Entities,
        List<ValidatedCable> Cables,
        List<ValidatedAtmosPipe> AtmosPipes,
        List<ValidatedGasPort> GasPorts,
        List<Vector2i> VacuumTiles);

    private sealed record ValidatedEntity(string Kind, string Prototype, int X, int Y, int Rotation);
    private sealed record ValidatedCable(string Prototype, Vector2i Position);
    private sealed record ValidatedAtmosPipe(string Prototype, AtmosPipeLayer Layer, Vector2i Position);
    private sealed record ValidatedGasPort(
        string Prototype,
        AtmosPipeLayer Layer,
        Vector2i Position,
        int Rotation);
}

public sealed record LuaMShipGenerationRequest(string Preset, string Size, string Seed, string Name);

public readonly record struct LuaMShipGenerationResult(bool Success, string Message, EntityUid GridUid)
{
    public static LuaMShipGenerationResult Succeeded(EntityUid gridUid, string message) => new(true, message, gridUid);
    public static LuaMShipGenerationResult Failed(string message) => new(false, message, EntityUid.Invalid);
}

internal sealed class LuaMShipBlueprintRequest
{
    public int SchemaVersion { get; init; }
    public string Preset { get; init; } = string.Empty;
    public string Size { get; init; } = string.Empty;
    public string Seed { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}

internal sealed class LuaMSavedShipAnalysisRequest
{
    public int SchemaVersion { get; init; }
    public int SnapshotFormatVersion { get; init; }
    public int EntityCount { get; init; }
    public int PayloadSizeBytes { get; init; }
    public string PrototypeManifestHash { get; init; } = string.Empty;
    public LuaMSavedShipPrototypeRequest[] Prototypes { get; init; } = [];
}

internal sealed class LuaMSavedShipPrototypeRequest
{
    public string Id { get; init; } = string.Empty;
    public int Count { get; init; }
}

internal sealed class LuaMSavedShipAnalysisResponse
{
    [JsonRequired]
    public int SchemaVersion { get; init; }
    [JsonRequired]
    public string GeneratorVersion { get; init; } = string.Empty;
    [JsonRequired]
    public int SnapshotFormatVersion { get; init; }
    [JsonRequired]
    public int EntityCount { get; init; }
    [JsonRequired]
    public int PayloadSizeBytes { get; init; }
    [JsonRequired]
    public int PrototypeCount { get; init; }
    [JsonRequired]
    public string PrototypeManifestHash { get; init; } = string.Empty;
    [JsonRequired]
    public Dictionary<string, int> Capabilities { get; init; } = new();
    [JsonRequired]
    public int ClassifiedEntityCount { get; init; }
    [JsonRequired]
    public int UnclassifiedEntityCount { get; init; }
    [JsonRequired]
    public string SuggestedPreset { get; init; } = string.Empty;
    [JsonRequired]
    public LuaMSavedShipPrototypeRequest[] TopPrototypes { get; init; } = [];
}

internal sealed class LuaMShipBlueprintResponse
{
    [JsonRequired]
    public int SchemaVersion { get; init; }
    [JsonRequired]
    public string? GeneratorVersion { get; init; }
    [JsonRequired]
    public string? Seed { get; init; }
    [JsonRequired]
    public string? Name { get; init; }
    [JsonRequired]
    public string? Preset { get; init; }
    [JsonRequired]
    public int Width { get; init; }
    [JsonRequired]
    public int Height { get; init; }
    [JsonRequired]
    public List<LuaMShipTile?>? Tiles { get; init; }
    [JsonRequired]
    public List<LuaMShipEntity?>? Entities { get; init; }
    [JsonRequired]
    public JsonElement Summary { get; init; }
    [JsonRequired]
    public JsonElement Preview { get; init; }
}

internal sealed class LuaMShipTile
{
    [JsonRequired]
    public int X { get; init; }
    [JsonRequired]
    public int Y { get; init; }
}

internal sealed class LuaMShipEntity
{
    [JsonRequired]
    public string? Kind { get; init; }
    [JsonRequired]
    public int X { get; init; }
    [JsonRequired]
    public int Y { get; init; }
    [JsonRequired]
    public int Rotation { get; init; }
}
