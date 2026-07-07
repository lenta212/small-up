using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server.Administration;
using Content.Server.GameTicking;
using Content.Server.Gateway.Components;
using Content.Server.Gateway.Systems;
using Content.Shared.Administration;
using Content.Shared.Doors.Components;
using Content.Shared.Item;
using Content.Shared.Maps;
using Content.Shared.Physics;
using Robust.Shared.Console;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Server._LuaM.Administration;

[AdminCommand(AdminFlags.Server)]
public sealed class LuaMGatewayShipCommand : IConsoleCommand
{
    private const string DefaultGameMap = "Twilight";
    private const string GatewayPrototype = "Gateway";
    private const string PairHereFlag = "--pair-here";
    private const string NoJumpFlag = "--no-jump";
    private const CollisionGroup GatewayBlockMask =
        CollisionGroup.FullTileMask |
        CollisionGroup.Opaque |
        CollisionGroup.BulletImpassable |
        CollisionGroup.DoorPassable;
    private static readonly Vector2i[] CardinalOffsets =
    [
        new(0, 1),
        new(1, 0),
        new(0, -1),
        new(-1, 0),
    ];

    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public string Command => "luam_gateway_ship";
    public string Description => "Loads a game-map ship and places a gateway on its first grid.";
    public string Help => $"Usage: {Command} {LuaMAiConsoleConfirmation.ConfirmFlag} <mapId> [gameMap={DefaultGameMap}] [x=0] [y=0] [name...] [{PairHereFlag}] [{NoJumpFlag}]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!LuaMAiConsoleConfirmation.TryConsume(shell, Command, args, out var confirmedArgs))
            return;

        var cleanArgs = new List<string>(confirmedArgs);
        var pairHere = cleanArgs.RemoveAll(arg => arg.Equals(PairHereFlag, StringComparison.OrdinalIgnoreCase)) > 0;
        var noJump = cleanArgs.RemoveAll(arg => arg.Equals(NoJumpFlag, StringComparison.OrdinalIgnoreCase)) > 0;

        if (cleanArgs.Count < 1)
        {
            shell.WriteError(Help);
            return;
        }

        if (!int.TryParse(cleanArgs[0], out var mapIdValue))
        {
            shell.WriteError("mapId must be an integer.");
            return;
        }

        var gameMapId = cleanArgs.Count >= 2 ? cleanArgs[1] : DefaultGameMap;
        if (!_prototypes.TryIndex<GameMapPrototype>(gameMapId, out var gameMap))
        {
            shell.WriteError($"Unknown gameMap prototype: {gameMapId}.");
            return;
        }

        if (!_prototypes.HasIndex<EntityPrototype>(GatewayPrototype))
        {
            shell.WriteError($"Missing gateway prototype: {GatewayPrototype}.");
            return;
        }

        var offset = Vector2.Zero;
        if (cleanArgs.Count == 3)
        {
            shell.WriteError("Provide both x and y offsets, or neither.");
            return;
        }

        var offsetX = 0f;
        var offsetY = 0f;
        if (cleanArgs.Count >= 4 &&
            (!float.TryParse(cleanArgs[2], out offsetX) ||
             !float.TryParse(cleanArgs[3], out offsetY)))
        {
            shell.WriteError("x and y offsets must be numbers.");
            return;
        }
        if (cleanArgs.Count >= 4)
            offset = new Vector2(offsetX, offsetY);

        var displayName = cleanArgs.Count >= 5
            ? string.Join(' ', cleanArgs.Skip(4)).Trim()
            : $"{gameMap.MapName} Gateway Ship";
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = $"{gameMap.MapName} Gateway Ship";

        var gatewaySystem = _entities.System<GatewaySystem>();
        var mapSystem = _entities.System<SharedMapSystem>();
        var gameTicker = _entities.System<GameTicker>();
        var mapId = new MapId(mapIdValue);

        IReadOnlyList<EntityUid> grids;
        try
        {
            grids = LoadGameMapOrGrid(gameTicker, mapSystem, gameMap, mapId, displayName, offset);
        }
        catch (Exception e)
        {
            shell.WriteError($"Failed to load gameMap {gameMapId}: {e.Message}");
            return;
        }

        if (grids.Count == 0)
        {
            shell.WriteError($"Loaded gameMap {gameMapId}, but it did not return any grids.");
            return;
        }

        var shipGrid = grids[0];
        if (!_entities.TryGetComponent<MapGridComponent>(shipGrid, out var grid))
        {
            shell.WriteError($"Loaded grid {shipGrid} does not have a MapGridComponent.");
            return;
        }

        var shipGateway = SpawnGateway(
            gatewaySystem,
            PickGatewayCoordinates(
                mapSystem,
                _entities.System<TurfSystem>(),
                _entities.System<EntityLookupSystem>(),
                _entities,
                shipGrid,
                grid),
            $"{displayName} Gate",
            enabled: !noJump);

        EntityUid? localGateway = null;
        if (pairHere)
        {
            if (shell.Player?.AttachedEntity is not { Valid: true } attached)
            {
                shell.WriteError($"{PairHereFlag} requires an attached admin entity.");
            }
            else if (_entities.TryGetComponent<TransformComponent>(attached, out var xform))
            {
                localGateway = SpawnGateway(
                    gatewaySystem,
                    xform.Coordinates,
                    $"{displayName} Local Gate",
                    enabled: !noJump);
            }
        }

        gatewaySystem.UpdateAllGateways();
        var gatewayState = noJump ? "locked/no-jump" : "enabled";
        shell.WriteLine($"Loaded {gameMapId} on map {mapIdValue}; placed {gatewayState} Gateway {shipGateway} on grid {shipGrid}.");
        if (localGateway != null)
            shell.WriteLine($"Placed paired {gatewayState} local Gateway {localGateway} at your current position.");
        else
            shell.WriteLine(noJump
                ? $"Gateway jumping is disabled by {NoJumpFlag}; enable the Gateway later before using it as a destination."
                : $"Use {PairHereFlag} or place another enabled Gateway to create a selectable destination pair.");
    }

    private IReadOnlyList<EntityUid> LoadGameMapOrGrid(
        GameTicker gameTicker,
        SharedMapSystem mapSystem,
        GameMapPrototype gameMap,
        MapId mapId,
        string displayName,
        Vector2 offset)
    {
        if (gameMap.IsGrid || IsKnownGridBackedGameMap(gameMap))
        {
            if (TryLoadGridFallback(mapSystem, gameMap, mapId, displayName, offset, out var gridBackedMap, out var gridBackedError))
                return gridBackedMap;

            throw new Exception(gridBackedError);
        }

        try
        {
            return mapSystem.MapExists(mapId)
                ? gameTicker.MergeGameMap(gameMap, mapId, stationName: displayName, offset: offset)
                : gameTicker.LoadGameMapWithId(gameMap, mapId, stationName: displayName, offset: offset);
        }
        catch (Exception e)
        {
            if (TryLoadGridFallback(mapSystem, gameMap, mapId, displayName, offset, out var grids, out var error))
                return grids;

            throw new Exception($"{e.Message}; grid fallback failed: {error}", e);
        }
    }

    private static bool IsKnownGridBackedGameMap(GameMapPrototype gameMap)
    {
        return gameMap.MapPath.ToString().Contains("/Shuttles/", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryLoadGridFallback(
        SharedMapSystem mapSystem,
        GameMapPrototype gameMap,
        MapId mapId,
        string displayName,
        Vector2 offset,
        out IReadOnlyList<EntityUid> grids,
        out string error)
    {
        grids = [];
        error = string.Empty;
        var createdMap = false;

        try
        {
            EntityUid mapUid;
            if (mapSystem.MapExists(mapId))
            {
                mapUid = mapSystem.GetMap(mapId);
            }
            else
            {
                mapUid = mapSystem.CreateMap(mapId);
                createdMap = true;
            }

            var loader = _entities.System<MapLoaderSystem>();
            if (!loader.TryLoadGrid(mapId, gameMap.MapPath, out var grid, offset: offset))
            {
                error = $"failed to load {gameMap.MapPath} as a grid";
                if (createdMap)
                    mapSystem.DeleteMap(mapId);
                return false;
            }

            var metaData = _entities.System<MetaDataSystem>();
            metaData.SetEntityName(mapUid, gameMap.MapName);
            metaData.SetEntityName(grid.Value.Owner, displayName);
            grids = [grid.Value.Owner];
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            if (createdMap && mapSystem.MapExists(mapId))
                mapSystem.DeleteMap(mapId);
            return false;
        }
    }

    private EntityUid SpawnGateway(GatewaySystem gatewaySystem, EntityCoordinates coordinates, string name, bool enabled)
    {
        var gateway = _entities.SpawnEntity(GatewayPrototype, coordinates);
        var gatewayComp = _entities.GetComponent<GatewayComponent>(gateway);
        gatewaySystem.SetDestinationName(gateway, FormattedMessage.FromUnformatted(name), gatewayComp);
        gatewaySystem.SetEnabled(gateway, enabled, gatewayComp);
        return gateway;
    }

    private static EntityCoordinates PickGatewayCoordinates(
        SharedMapSystem mapSystem,
        TurfSystem turfSystem,
        EntityLookupSystem lookupSystem,
        IEntityManager entityManager,
        EntityUid gridUid,
        MapGridComponent grid)
    {
        var center = grid.LocalAABB.Center;
        TileRef? bestRoom = null;
        TileRef? bestClearFloor = null;
        TileRef? bestClearFallback = null;
        TileRef? bestFloorFallback = null;
        TileRef? bestAnyFallback = null;
        var bestRoomDistance = float.MaxValue;
        var bestClearFloorDistance = float.MaxValue;
        var bestClearFallbackDistance = float.MaxValue;
        var bestFloorFallbackDistance = float.MaxValue;
        var bestAnyFallbackDistance = float.MaxValue;

        foreach (var tile in mapSystem.GetAllTiles(gridUid, grid))
        {
            if (tile.Tile.IsEmpty)
                continue;

            var coordinates = mapSystem.GridTileToLocal(gridUid, grid, tile.GridIndices);
            var distance = Vector2.DistanceSquared(coordinates.Position, center);
            var isSpace = turfSystem.IsSpace(tile);
            var isBlocked = IsGatewayBlockedTile(mapSystem, turfSystem, lookupSystem, entityManager, grid, tile);

            if (distance < bestAnyFallbackDistance)
            {
                bestAnyFallback = tile;
                bestAnyFallbackDistance = distance;
            }

            if (!isSpace && distance < bestFloorFallbackDistance)
            {
                bestFloorFallback = tile;
                bestFloorFallbackDistance = distance;
            }

            if (isSpace || isBlocked)
                continue;

            if (distance < bestClearFallbackDistance)
            {
                bestClearFallback = tile;
                bestClearFallbackDistance = distance;
            }

            var openCardinals = CountOpenCardinals(mapSystem, turfSystem, lookupSystem, entityManager, grid, tile);
            var hasDoorNear = HasDoorNear(mapSystem, entityManager, tile.GridUid, grid, tile.GridIndices);
            var isStraightPassage = IsStraightPassage(mapSystem, turfSystem, lookupSystem, entityManager, grid, tile);

            if (!hasDoorNear &&
                openCardinals >= 3 &&
                !isStraightPassage &&
                distance < bestRoomDistance)
            {
                bestRoom = tile;
                bestRoomDistance = distance;
            }

            if (!hasDoorNear &&
                openCardinals >= 2 &&
                distance < bestClearFloorDistance)
            {
                bestClearFloor = tile;
                bestClearFloorDistance = distance;
            }
        }

        return (bestRoom ?? bestClearFloor ?? bestClearFallback ?? bestFloorFallback ?? bestAnyFallback) is { } tileRef
            ? mapSystem.GridTileToLocal(gridUid, grid, tileRef.GridIndices)
            : new EntityCoordinates(gridUid, center);
    }

    private static int CountOpenCardinals(
        SharedMapSystem mapSystem,
        TurfSystem turfSystem,
        EntityLookupSystem lookupSystem,
        IEntityManager entityManager,
        MapGridComponent grid,
        TileRef tile)
    {
        var open = 0;
        foreach (var offset in CardinalOffsets)
        {
            if (IsOpenFloor(mapSystem, turfSystem, lookupSystem, entityManager, grid, tile.GridUid, tile.GridIndices + offset))
                open++;
        }

        return open;
    }

    private static bool IsStraightPassage(
        SharedMapSystem mapSystem,
        TurfSystem turfSystem,
        EntityLookupSystem lookupSystem,
        IEntityManager entityManager,
        MapGridComponent grid,
        TileRef tile)
    {
        var north = IsOpenFloor(mapSystem, turfSystem, lookupSystem, entityManager, grid, tile.GridUid, tile.GridIndices + new Vector2i(0, 1));
        var south = IsOpenFloor(mapSystem, turfSystem, lookupSystem, entityManager, grid, tile.GridUid, tile.GridIndices + new Vector2i(0, -1));
        var east = IsOpenFloor(mapSystem, turfSystem, lookupSystem, entityManager, grid, tile.GridUid, tile.GridIndices + new Vector2i(1, 0));
        var west = IsOpenFloor(mapSystem, turfSystem, lookupSystem, entityManager, grid, tile.GridUid, tile.GridIndices + new Vector2i(-1, 0));

        return north && south && !east && !west ||
               east && west && !north && !south;
    }

    private static bool IsOpenFloor(
        SharedMapSystem mapSystem,
        TurfSystem turfSystem,
        EntityLookupSystem lookupSystem,
        IEntityManager entityManager,
        MapGridComponent grid,
        EntityUid gridUid,
        Vector2i indices)
    {
        if (!mapSystem.TryGetTileRef(gridUid, grid, indices, out var tile))
            return false;

        return !IsGatewayBlockedTile(mapSystem, turfSystem, lookupSystem, entityManager, grid, tile);
    }

    private static bool IsGatewayBlockedTile(
        SharedMapSystem mapSystem,
        TurfSystem turfSystem,
        EntityLookupSystem lookupSystem,
        IEntityManager entityManager,
        MapGridComponent grid,
        TileRef tile)
    {
        if (tile.Tile.IsEmpty || turfSystem.IsSpace(tile))
            return true;

        if (turfSystem.IsTileBlocked(tile, CollisionGroup.MobMask) ||
            turfSystem.IsTileBlocked(tile, GatewayBlockMask))
            return true;

        return HasPlacementBlocker(mapSystem, lookupSystem, entityManager, grid, tile);
    }

    private static bool HasDoorNear(
        SharedMapSystem mapSystem,
        IEntityManager entityManager,
        EntityUid gridUid,
        MapGridComponent grid,
        Vector2i indices)
    {
        if (HasDoorOnTile(mapSystem, entityManager, gridUid, grid, indices))
            return true;

        foreach (var offset in CardinalOffsets)
        {
            if (HasDoorOnTile(mapSystem, entityManager, gridUid, grid, indices + offset))
                return true;
        }

        return false;
    }

    private static bool HasDoorOnTile(
        SharedMapSystem mapSystem,
        IEntityManager entityManager,
        EntityUid gridUid,
        MapGridComponent grid,
        Vector2i indices)
    {
        foreach (var ent in mapSystem.GetAnchoredEntities(gridUid, grid, indices))
        {
            if (entityManager.HasComponent<DoorComponent>(ent))
                return true;
        }

        return false;
    }

    private static bool HasPlacementBlocker(
        SharedMapSystem mapSystem,
        EntityLookupSystem lookupSystem,
        IEntityManager entityManager,
        MapGridComponent grid,
        TileRef tile)
    {
        foreach (var ent in mapSystem.GetAnchoredEntities(tile.GridUid, grid, tile.GridIndices))
        {
            if (IsPlacementBlocker(entityManager, ent))
                return true;
        }

        foreach (var ent in lookupSystem.GetEntitiesInTile(tile, LookupFlags.All))
        {
            if (ent == tile.GridUid || entityManager.HasComponent<MapGridComponent>(ent))
                continue;

            if (IsPlacementBlocker(entityManager, ent))
                return true;
        }

        return false;
    }

    private static bool IsPlacementBlocker(IEntityManager entityManager, EntityUid ent)
    {
        if (entityManager.HasComponent<DoorComponent>(ent) ||
            entityManager.HasComponent<ItemComponent>(ent))
            return true;

        if (!entityManager.TryGetComponent<PhysicsComponent>(ent, out var physics))
            return false;

        if (physics.BodyType != BodyType.Static)
            return true;

        if (!entityManager.TryGetComponent<FixturesComponent>(ent, out var fixtures))
            return physics.Hard && (physics.CollisionLayer & (int) GatewayBlockMask) != 0;

        foreach (var fixture in fixtures.Fixtures.Values)
        {
            if (!fixture.Hard)
                continue;

            if ((fixture.CollisionLayer & (int) GatewayBlockMask) != 0)
                return true;
        }

        return false;
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length switch
        {
            1 => CompletionResult.FromHint("map id, or --confirm"),
            2 => CompletionResult.FromHintOptions(CompletionHelper.PrototypeIDs<GameMapPrototype>(), $"gameMap prototype, default {DefaultGameMap}"),
            3 => CompletionResult.FromHint("x offset"),
            4 => CompletionResult.FromHint("y offset"),
            _ => CompletionResult.FromHint($"optional name, {PairHereFlag}, or {NoJumpFlag}"),
        };
    }
}
