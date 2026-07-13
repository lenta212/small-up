using System;
using System.Collections.Generic;
using System.Linq;

namespace Content.Server._LuaM.Expeditions;

/// <summary>
/// First deterministic macro planner for a large expedition.
/// It produces data only; ECS materialization is a later director step.
/// </summary>
public sealed class LuaMExpeditionPlanner
{
    public const int CurrentGeneratorVersion = 2;
    public const int DefaultMacroRegionSize = 64;
    public const int DefaultWidth = 512;
    public const int DefaultHeight = 512;
    public const int MaximumDimension = 4096;
    public const int MaximumMacroRegionCount = 4096;

    public const int MaximumIdentifierLength = 128;

    private const int SiteFootprintRadius = 4;
    private const int SecondarySiteCount = 3;
    private const int RandomPlacementCandidateCount = 128;
    private const int LoopCandidateCount = 3;

    public LuaMExpeditionPlan Build(
        string campaignId,
        string expeditionId,
        ulong seed,
        int generatorVersion = CurrentGeneratorVersion,
        int width = DefaultWidth,
        int height = DefaultHeight,
        int macroRegionSize = DefaultMacroRegionSize)
    {
        ValidateIdentifier(campaignId, nameof(campaignId));
        ValidateIdentifier(expeditionId, nameof(expeditionId));

        if (generatorVersion != CurrentGeneratorVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generatorVersion),
                $"Only expedition generator version {CurrentGeneratorVersion} is supported.");
        }

        if (width < 64 || height < 64)
            throw new ArgumentOutOfRangeException(nameof(width), "The prototype requires at least a 64x64 world.");

        if (width > MaximumDimension || height > MaximumDimension)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                $"Expedition dimensions cannot exceed {MaximumDimension}x{MaximumDimension}.");
        }

        if (macroRegionSize < 16 || macroRegionSize > 256)
            throw new ArgumentOutOfRangeException(nameof(macroRegionSize));

        var regionCountX = ((long) width + macroRegionSize - 1) / macroRegionSize;
        var regionCountY = ((long) height + macroRegionSize - 1) / macroRegionSize;
        if (regionCountX * regionCountY > MaximumMacroRegionCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(macroRegionSize),
                $"Expedition plans are limited to {MaximumMacroRegionCount} macro regions.");
        }

        var bounds = new LuaMExpeditionBounds(0, 0, width - 1, height - 1);
        var regions = BuildRegions(campaignId, expeditionId, seed, generatorVersion, bounds, macroRegionSize);
        var random = new DeterministicRandom(seed ^ LuaMExpeditionPlanHasher.DeriveSeed(
            campaignId,
            expeditionId,
            generatorVersion,
            "site-placement"));

        var sites = BuildSites(bounds, ref random);
        var connections = BuildConnections(sites, ref random);

        var plan = new LuaMExpeditionPlan(
            campaignId,
            expeditionId,
            seed,
            generatorVersion,
            bounds,
            regions,
            sites,
            connections,
            string.Empty);

        plan = plan with { PlanHash = LuaMExpeditionPlanHasher.Compute(plan) };
        LuaMExpeditionPlanValidator.EnsureValid(plan);
        return plan;
    }

    private static void ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Expedition identifiers cannot be empty.", parameterName);

        if (value.Length > MaximumIdentifierLength)
            throw new ArgumentOutOfRangeException(parameterName, $"Expedition identifiers are limited to {MaximumIdentifierLength} characters.");

        if (value.Any(char.IsControl))
            throw new ArgumentException("Expedition identifiers cannot contain control characters.", parameterName);
    }

    private static IReadOnlyList<LuaMExpeditionRegionPlan> BuildRegions(
        string campaignId,
        string expeditionId,
        ulong seed,
        int generatorVersion,
        LuaMExpeditionBounds bounds,
        int regionSize)
    {
        var regions = new List<LuaMExpeditionRegionPlan>();
        var regionCountX = (bounds.Width + regionSize - 1) / regionSize;
        var regionCountY = (bounds.Height + regionSize - 1) / regionSize;

        for (var regionY = 0; regionY < regionCountY; regionY++)
        {
            for (var regionX = 0; regionX < regionCountX; regionX++)
            {
                var minX = regionX * regionSize;
                var minY = regionY * regionSize;
                var maxX = Math.Min(bounds.MaxX, minX + regionSize - 1);
                var maxY = Math.Min(bounds.MaxY, minY + regionSize - 1);
                var regionSeed = LuaMExpeditionPlanHasher.DeriveSeed(
                    campaignId,
                    expeditionId,
                    generatorVersion,
                    $"region:{seed}:{regionX}:{regionY}");

                var biome = (regionSeed % 3) switch
                {
                    0 => "desert",
                    1 => "canyon",
                    _ => "salt-flats",
                };

                regions.Add(new LuaMExpeditionRegionPlan(
                    regionX,
                    regionY,
                    new LuaMExpeditionBounds(minX, minY, maxX, maxY),
                    biome,
                    10 + (int) (regionSeed % 91)));
            }
        }

        return regions;
    }

    private static IReadOnlyList<LuaMExpeditionSitePlan> BuildSites(
        LuaMExpeditionBounds bounds,
        ref DeterministicRandom random)
    {
        var margin = Math.Max(8, Math.Min(bounds.Width, bounds.Height) / 32);
        var entry = new LuaMExpeditionPoint(bounds.MinX + margin, bounds.MinY + bounds.Height / 2);
        var objective = new LuaMExpeditionPoint(
            bounds.MinX + bounds.Width / 2 + random.NextInt(-bounds.Width / 8, bounds.Width / 8 + 1),
            bounds.MinY + bounds.Height / 2 + random.NextInt(-bounds.Height / 4, bounds.Height / 4 + 1));
        var extraction = new LuaMExpeditionPoint(bounds.MaxX - margin, bounds.MinY + bounds.Height / 2);

        var sites = new List<LuaMExpeditionSitePlan>
        {
            CreateSite("entry", LuaMExpeditionSiteKind.Entry, "LuaMExpeditionLanding", "expedition", entry),
            CreateSite("objective", LuaMExpeditionSiteKind.Objective, "LuaMExpeditionLaboratory", "expedition", objective),
            CreateSite("extraction", LuaMExpeditionSiteKind.Extraction, "LuaMExpeditionExtraction", "expedition", extraction),
        };

        for (var secondaryIndex = 0; secondaryIndex < SecondarySiteCount; secondaryIndex++)
        {
            var point = FindSecondarySitePosition(bounds, margin, sites, ref random);
            var siteId = $"secondary-{secondaryIndex + 1}";
            sites.Add(CreateSite(
                siteId,
                LuaMExpeditionSiteKind.Secondary,
                secondaryIndex == 0 ? "LuaMExpeditionMine" : "LuaMExpeditionRuin",
                $"expedition:{siteId}",
                point));
        }

        return sites;
    }

    private static IReadOnlyList<LuaMExpeditionConnectionPlan> BuildConnections(
        IReadOnlyList<LuaMExpeditionSitePlan> sites,
        ref DeterministicRandom random)
    {
        var candidates = new List<GraphEdgeCandidate>();
        for (var first = 0; first < sites.Count; first++)
        {
            for (var second = first + 1; second < sites.Count; second++)
            {
                candidates.Add(new GraphEdgeCandidate(
                    first,
                    second,
                    ManhattanDistance(sites[first].Position, sites[second].Position)));
            }
        }

        var orderedCandidates = candidates
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => sites[candidate.FirstIndex].SiteId, StringComparer.Ordinal)
            .ThenBy(candidate => sites[candidate.SecondIndex].SiteId, StringComparer.Ordinal)
            .ToArray();

        var disjointSets = new DisjointSets(sites.Count);
        var spanningTree = new List<GraphEdgeCandidate>(sites.Count - 1);
        var spanningTreeKeys = new HashSet<(int First, int Second)>();
        foreach (var candidate in orderedCandidates)
        {
            if (!disjointSets.Union(candidate.FirstIndex, candidate.SecondIndex))
                continue;

            spanningTree.Add(candidate);
            spanningTreeKeys.Add((candidate.FirstIndex, candidate.SecondIndex));
            if (spanningTree.Count == sites.Count - 1)
                break;
        }

        if (spanningTree.Count != sites.Count - 1)
            throw new InvalidOperationException("Could not build an expedition spanning tree.");

        // Add one short, seed-selected non-tree edge. Sorting before the random choice keeps
        // the result stable even if the candidate collection implementation changes later.
        var loopCandidates = orderedCandidates
            .Where(candidate => !spanningTreeKeys.Contains((candidate.FirstIndex, candidate.SecondIndex)))
            .Take(LoopCandidateCount)
            .ToArray();

        GraphEdgeCandidate? loop = null;
        if (loopCandidates.Length > 0)
            loop = loopCandidates[random.NextInt(0, loopCandidates.Length)];

        var selectedEdges = spanningTree
            .Select(candidate => (Candidate: candidate, Kind: LuaMExpeditionConnectionKind.SpanningTree))
            .Concat(loop is { } loopCandidate
                ? new[] { (Candidate: loopCandidate, Kind: LuaMExpeditionConnectionKind.Loop) }
                : Array.Empty<(GraphEdgeCandidate Candidate, LuaMExpeditionConnectionKind Kind)>())
            .OrderBy(edge => sites[edge.Candidate.FirstIndex].SiteId, StringComparer.Ordinal)
            .ThenBy(edge => sites[edge.Candidate.SecondIndex].SiteId, StringComparer.Ordinal)
            .ToArray();

        var connections = new List<LuaMExpeditionConnectionPlan>(selectedEdges.Length);
        foreach (var (candidate, kind) in selectedEdges)
        {
            var from = sites[candidate.FirstIndex];
            var to = sites[candidate.SecondIndex];
            if (string.CompareOrdinal(from.SiteId, to.SiteId) > 0)
                (from, to) = (to, from);

            connections.Add(CreateConnection(from, to, kind, ref random));
        }

        return connections;
    }

    private static LuaMExpeditionConnectionPlan CreateConnection(
        LuaMExpeditionSitePlan from,
        LuaMExpeditionSitePlan to,
        LuaMExpeditionConnectionKind kind,
        ref DeterministicRandom random)
    {
        var horizontalFirst = random.NextInt(0, 2) == 0;
        var path = new List<LuaMExpeditionPoint> { from.Position };
        var current = from.Position;

        if (horizontalFirst)
        {
            WalkAxis(path, ref current, to.Position.X, true);
            WalkAxis(path, ref current, to.Position.Y, false);
        }
        else
        {
            WalkAxis(path, ref current, to.Position.Y, false);
            WalkAxis(path, ref current, to.Position.X, true);
        }

        return new LuaMExpeditionConnectionPlan(from.SiteId, to.SiteId, path, kind);
    }

    private static LuaMExpeditionPoint FindSecondarySitePosition(
        LuaMExpeditionBounds bounds,
        int margin,
        IReadOnlyCollection<LuaMExpeditionSitePlan> existingSites,
        ref DeterministicRandom random)
    {
        LuaMExpeditionPoint? bestPoint = null;
        var bestDistance = -1;

        for (var attempt = 0; attempt < RandomPlacementCandidateCount; attempt++)
        {
            var candidate = new LuaMExpeditionPoint(
                random.NextInt(bounds.MinX + margin, bounds.MaxX - margin + 1),
                random.NextInt(bounds.MinY + margin, bounds.MaxY - margin + 1));

            ConsiderPlacementCandidate(candidate, existingSites, ref bestPoint, ref bestDistance);
        }

        if (bestPoint is { } selected)
            return selected;

        // The bounded random search should normally succeed on its first few candidates.
        // This deterministic scan makes placement total instead of relying on an unbounded
        // rejection loop for adversarial seeds or future footprint sizes.
        for (var y = bounds.MinY + margin; y <= bounds.MaxY - margin; y++)
        {
            for (var x = bounds.MinX + margin; x <= bounds.MaxX - margin; x++)
            {
                var candidate = new LuaMExpeditionPoint(x, y);
                if (!OverlapsAnyFootprint(CreateFootprint(candidate), existingSites))
                    return candidate;
            }
        }

        throw new InvalidOperationException("Could not place all expedition sites without overlapping footprints.");
    }

    private static void ConsiderPlacementCandidate(
        LuaMExpeditionPoint candidate,
        IReadOnlyCollection<LuaMExpeditionSitePlan> existingSites,
        ref LuaMExpeditionPoint? bestPoint,
        ref int bestDistance)
    {
        var footprint = CreateFootprint(candidate);
        if (OverlapsAnyFootprint(footprint, existingSites))
            return;

        var distance = existingSites.Min(site => ManhattanDistance(site.Position, candidate));
        if (distance < bestDistance)
            return;

        if (distance == bestDistance && bestPoint is { } previous &&
            (candidate.Y > previous.Y || candidate.Y == previous.Y && candidate.X >= previous.X))
        {
            return;
        }

        bestPoint = candidate;
        bestDistance = distance;
    }

    private static bool OverlapsAnyFootprint(
        LuaMExpeditionBounds footprint,
        IEnumerable<LuaMExpeditionSitePlan> sites)
    {
        return sites.Any(site => BoundsOverlap(footprint, site.Footprint));
    }

    private static bool BoundsOverlap(LuaMExpeditionBounds first, LuaMExpeditionBounds second)
    {
        return first.MinX <= second.MaxX && first.MaxX >= second.MinX &&
               first.MinY <= second.MaxY && first.MaxY >= second.MinY;
    }

    private static void WalkAxis(
        ICollection<LuaMExpeditionPoint> path,
        ref LuaMExpeditionPoint current,
        int target,
        bool horizontal)
    {
        while (horizontal ? current.X != target : current.Y != target)
        {
            current = horizontal
                ? current with { X = current.X + Math.Sign(target - current.X) }
                : current with { Y = current.Y + Math.Sign(target - current.Y) };
            path.Add(current);
        }
    }

    private static LuaMExpeditionSitePlan CreateSite(
        string siteId,
        LuaMExpeditionSiteKind kind,
        string prototypeId,
        string uniqueScope,
        LuaMExpeditionPoint position)
    {
        return new LuaMExpeditionSitePlan(
            siteId,
            kind,
            prototypeId,
            uniqueScope,
            position,
            CreateFootprint(position));
    }

    private static LuaMExpeditionBounds CreateFootprint(LuaMExpeditionPoint position)
    {
        return new LuaMExpeditionBounds(
            position.X - SiteFootprintRadius,
            position.Y - SiteFootprintRadius,
            position.X + SiteFootprintRadius,
            position.Y + SiteFootprintRadius);
    }

    private static int ManhattanDistance(LuaMExpeditionPoint first, LuaMExpeditionPoint second)
    {
        return Math.Abs(first.X - second.X) + Math.Abs(first.Y - second.Y);
    }

    private readonly record struct GraphEdgeCandidate(int FirstIndex, int SecondIndex, int Distance);

    private sealed class DisjointSets
    {
        private readonly int[] _parents;
        private readonly byte[] _ranks;

        public DisjointSets(int count)
        {
            _parents = Enumerable.Range(0, count).ToArray();
            _ranks = new byte[count];
        }

        public bool Union(int first, int second)
        {
            var firstRoot = Find(first);
            var secondRoot = Find(second);
            if (firstRoot == secondRoot)
                return false;

            if (_ranks[firstRoot] < _ranks[secondRoot])
                _parents[firstRoot] = secondRoot;
            else if (_ranks[firstRoot] > _ranks[secondRoot])
                _parents[secondRoot] = firstRoot;
            else
            {
                _parents[secondRoot] = firstRoot;
                _ranks[firstRoot]++;
            }

            return true;
        }

        private int Find(int item)
        {
            while (_parents[item] != item)
            {
                _parents[item] = _parents[_parents[item]];
                item = _parents[item];
            }

            return item;
        }
    }

    private struct DeterministicRandom
    {
        private ulong _state;

        public DeterministicRandom(ulong seed)
        {
            _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
        }

        public int NextInt(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive)
                throw new ArgumentOutOfRangeException(nameof(maxExclusive));

            var range = (ulong) (maxExclusive - minInclusive);
            return minInclusive + (int) (NextUInt64() % range);
        }

        private ulong NextUInt64()
        {
            _state ^= _state >> 12;
            _state ^= _state << 25;
            _state ^= _state >> 27;
            return _state * 2685821657736338717UL;
        }
    }
}
