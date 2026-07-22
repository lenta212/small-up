using System.Threading;
using System.Threading.Tasks;
using Content.Shared.Access;
using Content.Shared.NPC;
using Content.Shared.StationRecords;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server.NPC.Pathfinding;

/// <summary>
/// Stores the in-progress data of a pathfinding request.
/// </summary>
public abstract class PathRequest
{
    public EntityCoordinates Start;

    /// <summary>
    /// Entity whose collision shape and real access credentials are used for this request.
    /// Coordinate-only requests leave this null and cannot opt into credentialed doors.
    /// </summary>
    public readonly EntityUid? Requester;

    /// <summary>
    /// Immutable credentials captured on the server main thread before this request
    /// is processed. AccessReader may raise ECS events while gathering credentials,
    /// so that work must never happen inside the parallel pathfinding loop.
    /// </summary>
    public PathAccessSnapshot? AccessSnapshot;
    public bool AccessSnapshotCaptured;
    public readonly Dictionary<PathPoly, bool> AccessPolyDecisions = new();

    public bool EncounteredAccessDenied;

    public readonly CancellationToken CancellationToken;

    /// <summary>
    /// Bounds how long a request may wait for a temporarily missing navmesh chunk.
    /// </summary>
    public TimeSpan? GraphWaitDeadline;

    public Task<PathResult> Task => Tcs.Task;
    public readonly TaskCompletionSource<PathResult> Tcs;

    public List<PathPoly> Polys = new();

    public bool Started = false;

    #region Pathfinding state

    public readonly Stopwatch Stopwatch = new();
    public PriorityQueue<ValueTuple<float, PathPoly>> Frontier = default!;
    public readonly Dictionary<PathPoly, float> CostSoFar = new();
    public readonly Dictionary<PathPoly, PathPoly> CameFrom = new();
    public int ExpandedNodes;

    #endregion

    #region Data

    public readonly PathFlags Flags;
    public readonly int CollisionLayer;
    public readonly int CollisionMask;

    #endregion

    public PathRequest(
        EntityCoordinates start,
        PathFlags flags,
        int layer,
        int mask,
        CancellationToken cancelToken,
        EntityUid? requester = null)
    {
        Start = start;
        Requester = requester;
        Flags = flags;
        CollisionLayer = layer;
        CollisionMask = mask;
        CancellationToken = cancelToken;
        Tcs = new TaskCompletionSource<PathResult>();
    }
}

public sealed class PathAccessSnapshot
{
    public readonly ProtoId<AccessLevelPrototype>[] Tags;
    public readonly StationRecordKey[] StationKeys;

    public PathAccessSnapshot(
        ProtoId<AccessLevelPrototype>[] tags,
        StationRecordKey[] stationKeys)
    {
        Tags = tags;
        StationKeys = stationKeys;
    }
}

public sealed class AStarPathRequest : PathRequest
{
    public EntityCoordinates End;

    /// <summary>
    /// How close we need to be to the end node to be considered as arrived.
    /// </summary>
    public float Distance;

    public AStarPathRequest(
        EntityCoordinates start,
        EntityCoordinates end,
        PathFlags flags,
        float distance,
        int layer,
        int mask,
        CancellationToken cancelToken,
        EntityUid? requester = null) : base(start, flags, layer, mask, cancelToken, requester)
    {
        Distance = distance;
        End = end;
    }
}

public sealed class BFSPathRequest : PathRequest
{
    /// <summary>
    /// How far away we're allowed to expand in distance.
    /// </summary>
    public float ExpansionRange;

    /// <summary>
    /// How many nodes we're allowed to expand
    /// </summary>
    public int ExpansionLimit;

    public BFSPathRequest(
        float expansionRange,
        int expansionLimit,
        EntityCoordinates start,
        PathFlags flags,
        int layer,
        int mask,
        CancellationToken cancelToken,
        EntityUid? requester = null) : base(start, flags, layer, mask, cancelToken, requester)
        {
            ExpansionRange = expansionRange;
            ExpansionLimit = expansionLimit;
        }
}

/// <summary>
/// Stores the final result of a pathfinding request
/// </summary>
public sealed class PathResultEvent
{
    public PathResult Result;
    public readonly List<PathPoly> Path;
    public readonly bool AccessDenied;

    public PathResultEvent(PathResult result, List<PathPoly> path, bool accessDenied = false)
    {
        Result = result;
        Path = path;
        AccessDenied = accessDenied;
    }
}
