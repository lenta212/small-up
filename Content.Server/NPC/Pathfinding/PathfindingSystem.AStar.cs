using Content.Shared.NPC;
using Robust.Shared.Map;
using Robust.Shared.Utility;

namespace Content.Server.NPC.Pathfinding;

public sealed partial class PathfindingSystem
{
    private static readonly TimeSpan GraphRebuildWaitLimit = TimeSpan.FromSeconds(2);

    private PathResult UpdateAStarPath(AStarPathRequest request)
    {
        if (request.Start.Equals(request.End))
        {
            return PathResult.Path;
        }

        if (request.CancellationToken.IsCancellationRequested)
        {
            return PathResult.NoPath;
        }

        // TODO: Need partial planning that uses best node.
        PathPoly? currentNode = null;

        var startNode = GetPoly(request.Start);
        var endNode = GetPoly(request.End);

        if (startNode == null || endNode == null)
        {
            // Tile and collision changes rebuild the graph after a short cooldown.
            // A request made in that window must remain queued instead of caching a
            // transient missing polygon as a terminal NoPath result.
            if (IsGraphUpdatePending(request.Start) || IsGraphUpdatePending(request.End))
            {
                var deadline = request.GraphWaitDeadline ??=
                    _timing.CurTime + GraphRebuildWaitLimit;
                if (_timing.CurTime < deadline)
                {
                    ResetPathSearch(request);
                    return PathResult.Continuing;
                }
            }

            return PathResult.NoPath;
        }

        request.GraphWaitDeadline = null;
        currentNode = startNode;

        // First run
        if (!request.Started)
        {
            request.Frontier = new PriorityQueue<(float, PathPoly)>(PathPolyComparer);
            request.Started = true;
            request.Frontier.Add((0.0f, startNode));
            request.CostSoFar[startNode] = 0.0f;
        }
        // Re-validate nodes
        else
        {
            // Theoretically this shouldn't be happening, but practically...
            if (request.Frontier.Count == 0)
            {
                return PathResult.NoPath;
            }

            (_, currentNode) = request.Frontier.Peek();

            if (!currentNode.IsValid())
            {
                return PathResult.NoPath;
            }

            // Re-validate parents too.
            if (request.CameFrom.TryGetValue(currentNode, out var parentNode) && !parentNode.IsValid())
            {
                return PathResult.NoPath;
            }
        }

        DebugTools.Assert(!request.Task.IsCompleted);
        request.Stopwatch.Restart();
        var sliceCount = 0;
        var arrived = false;

        while (request.Frontier.Count > 0 && request.ExpandedNodes < NodeLimit)
        {
            // Handle whether we need to pause if we've taken too long
            if (sliceCount % 20 == 0 && sliceCount > 0 &&
                (request.CancellationToken.IsCancellationRequested || request.Stopwatch.Elapsed > PathTime))
            {
                if (request.CancellationToken.IsCancellationRequested)
                    return PathResult.NoPath;

                // I had this happen once in testing but I don't think it should be possible?
                DebugTools.Assert(request.Frontier.Count > 0);
                return PathResult.Continuing;
            }

            sliceCount++;
            request.ExpandedNodes++;

            // Actual pathfinding here
            (_, currentNode) = request.Frontier.Take();

            // If we're inside the required distance OR we're at the end node.
            if ((request.Distance > 0f &&
                currentNode.Coordinates.TryDistance(EntityManager, request.End, out var distance) &&
                distance <= request.Distance) ||
                currentNode.Equals(endNode))
            {
                arrived = true;
                break;
            }

            foreach (var neighbor in currentNode.Neighbors)
            {
                var tileCost = GetTileCost(request, currentNode, neighbor);

                if (tileCost.Equals(0f))
                {
                    continue;
                }

                // f = g + h
                // gScore is distance to the start node
                // hScore is distance to the end node
                var gScore = request.CostSoFar[currentNode] + tileCost;
                if (request.CostSoFar.TryGetValue(neighbor, out var nextValue) && gScore >= nextValue)
                {
                    continue;
                }

                request.CameFrom[neighbor] = currentNode;
                request.CostSoFar[neighbor] = gScore;
                // pFactor is tie-breaker where the fscore is otherwise equal.
                // See http://theory.stanford.edu/~amitp/GameProgramming/Heuristics.html#breaking-ties
                // There's other ways to do it but future consideration
                // The closer the fScore is to the actual distance then the better the pathfinder will be
                // (i.e. somewhere between 1 and infinite)
                // Can use hierarchical pathfinder or whatever to improve the heuristic but this is fine for now.
                var hScore = OctileDistance(endNode, neighbor) * (1.0f + 1.0f / 1000.0f);
                var fScore = gScore + hScore;
                request.Frontier.Add((fScore, neighbor));
            }
        }

        if (!arrived)
        {
            return PathResult.NoPath;
        }

        var route = ReconstructPath(request.CameFrom, currentNode);
        var path = new Queue<EntityCoordinates>(route.Count);

        foreach (var node in route)
        {
            // Due to partial planning some nodes may have been invalidated.
            if (!node.IsValid())
            {
                return PathResult.NoPath;
            }

            path.Enqueue(node.Coordinates);
        }

        DebugTools.Assert(route.Count > 0);
        request.Polys = route;
        return PathResult.Path;
    }

    private bool IsGraphUpdatePending(EntityCoordinates coordinates)
    {
        if (_transform.GetGrid(coordinates) is not { } gridUid ||
            !TryComp<GridPathfindingComponent>(gridUid, out var grid))
        {
            return false;
        }

        return grid.DirtyChunks.Contains(GetOrigin(coordinates, gridUid));
    }

    private static void ResetPathSearch(PathRequest request)
    {
        if (!request.Started)
            return;

        request.Started = false;
        request.Frontier = default!;
        request.CostSoFar.Clear();
        request.CameFrom.Clear();
        request.Polys.Clear();
        request.ExpandedNodes = 0;
        request.AccessPolyDecisions.Clear();
        request.EncounteredAccessDenied = false;
    }
}
