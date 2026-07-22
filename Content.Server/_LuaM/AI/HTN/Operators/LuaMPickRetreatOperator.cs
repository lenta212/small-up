using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC;
using Content.Server.NPC.HTN.PrimitiveTasks;
using Content.Server.NPC.Pathfinding;
using Robust.Shared.Map;

namespace Content.Server._LuaM.AI.HTN.Operators;

/// <summary>
/// Selects a reachable destination that increases distance from the current
/// behavior threat. With no concrete threat it still selects a bounded escape
/// route for fire, atmosphere, and other area hazards.
/// </summary>
public sealed partial class LuaMPickRetreatOperator : HTNOperator
{
    [Dependency] private IEntityManager _entities = default!;

    private PathfindingSystem _pathfinding = default!;
    private SharedTransformSystem _transform = default!;

    [DataField]
    public string ThreatKey = "Target";

    [DataField]
    public string TargetCoordinatesKey = "TargetCoordinates";

    [DataField]
    public string PathfindKey = NPCBlackboard.PathfindKey;

    [DataField]
    public float Range = 7f;

    [DataField]
    public int CandidateCount = 6;

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _pathfinding = sysManager.GetEntitySystem<PathfindingSystem>();
        _transform = sysManager.GetEntitySystem<SharedTransformSystem>();
    }

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        if (!_entities.TryGetComponent<TransformComponent>(owner, out var ownerTransform))
            return (false, null);

        var ownerCoordinates = _transform.ToMapCoordinates(ownerTransform.Coordinates, logError: false);
        if (ownerCoordinates == MapCoordinates.Nullspace)
            return (false, null);

        MapCoordinates? threatCoordinates = null;
        if (blackboard.TryGetValue<EntityUid>(ThreatKey, out var threat, _entities) &&
            _entities.TryGetComponent<TransformComponent>(threat, out var threatTransform))
        {
            var mapped = _transform.ToMapCoordinates(threatTransform.Coordinates, logError: false);
            if (mapped != MapCoordinates.Nullspace && mapped.MapId == ownerCoordinates.MapId)
                threatCoordinates = mapped;
        }

        PathResultEvent? bestPath = null;
        EntityCoordinates bestDestination = default;
        var bestScore = float.MinValue;
        var attempts = Math.Clamp(CandidateCount, 1, 12);
        var range = Math.Clamp(Range, 1f, 30f);

        for (var i = 0; i < attempts; i++)
        {
            var path = await _pathfinding.GetRandomPath(
                owner,
                range,
                cancelToken,
                flags: _pathfinding.GetFlags(blackboard));
            if (path.Result != PathResult.Path || path.Path.Count == 0)
                continue;

            var candidate = path.Path.Last().Coordinates;
            var candidateMap = _transform.ToMapCoordinates(candidate, logError: false);
            if (candidateMap == MapCoordinates.Nullspace || candidateMap.MapId != ownerCoordinates.MapId)
                continue;

            var score = threatCoordinates is { } threatMap
                ? Vector2.DistanceSquared(candidateMap.Position, threatMap.Position)
                : Vector2.DistanceSquared(candidateMap.Position, ownerCoordinates.Position);
            if (score <= bestScore)
                continue;

            bestScore = score;
            bestPath = path;
            bestDestination = candidate;
        }

        if (bestPath == null)
            return (false, null);

        if (threatCoordinates is { } mappedThreat)
        {
            var currentDistance = Vector2.DistanceSquared(ownerCoordinates.Position, mappedThreat.Position);
            if (bestScore <= currentDistance + 0.25f)
                return (false, null);
        }

        return (true, new Dictionary<string, object>
        {
            { TargetCoordinatesKey, bestDestination },
            { PathfindKey, bestPath },
        });
    }
}
