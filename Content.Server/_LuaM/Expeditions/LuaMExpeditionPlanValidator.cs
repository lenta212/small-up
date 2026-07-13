#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Content.Server._LuaM.Expeditions;

public static class LuaMExpeditionPlanValidator
{
    public static IReadOnlyList<string> Validate(LuaMExpeditionPlan plan)
    {
        var errors = new List<string>();
        if (plan is null)
        {
            errors.Add("Plan is null.");
            return errors;
        }

        var canComputeHash = true;

        if (string.IsNullOrWhiteSpace(plan.CampaignId))
            errors.Add("Campaign id is empty.");
        else if (plan.CampaignId.Length > LuaMExpeditionPlanner.MaximumIdentifierLength ||
                 plan.CampaignId.Any(char.IsControl))
            errors.Add("Campaign id exceeds the supported format limits.");
        if (plan.CampaignId is null)
            canComputeHash = false;

        if (string.IsNullOrWhiteSpace(plan.ExpeditionId))
            errors.Add("Expedition id is empty.");
        else if (plan.ExpeditionId.Length > LuaMExpeditionPlanner.MaximumIdentifierLength ||
                 plan.ExpeditionId.Any(char.IsControl))
            errors.Add("Expedition id exceeds the supported format limits.");
        if (plan.ExpeditionId is null)
            canComputeHash = false;

        if (plan.GeneratorVersion != LuaMExpeditionPlanner.CurrentGeneratorVersion)
        {
            errors.Add(
                $"Unsupported generator version {plan.GeneratorVersion}; expected {LuaMExpeditionPlanner.CurrentGeneratorVersion}.");
        }

        var validPlanBounds = HasPositiveDimensions(plan.Bounds);
        if (!validPlanBounds)
            errors.Add("Expedition bounds must have positive dimensions.");
        else
        {
            var width = (long) plan.Bounds.MaxX - plan.Bounds.MinX + 1;
            var height = (long) plan.Bounds.MaxY - plan.Bounds.MinY + 1;
            if (width > LuaMExpeditionPlanner.MaximumDimension ||
                height > LuaMExpeditionPlanner.MaximumDimension)
            {
                errors.Add("Expedition bounds exceed the supported dimension limit.");
            }
        }

        IReadOnlyList<LuaMExpeditionRegionPlan> regions;
        if (plan.Regions is null)
        {
            errors.Add("Expedition region collection is null.");
            regions = Array.Empty<LuaMExpeditionRegionPlan>();
            canComputeHash = false;
        }
        else
        {
            regions = plan.Regions;
        }

        if (regions.Count == 0)
            errors.Add("Expedition has no macro regions.");
        else if (regions.Count > LuaMExpeditionPlanner.MaximumMacroRegionCount)
            errors.Add("Expedition has too many macro regions.");

        var validRegions = new List<LuaMExpeditionRegionPlan>(regions.Count);
        var regionKeys = new HashSet<(int X, int Y)>();
        foreach (var region in regions)
        {
            if (region is null)
            {
                errors.Add("Expedition contains a null macro region.");
                canComputeHash = false;
                continue;
            }

            validRegions.Add(region);
            if (!regionKeys.Add((region.RegionX, region.RegionY)))
                errors.Add($"Duplicate macro region {region.RegionX}:{region.RegionY}.");

            if (region.RegionX < 0 || region.RegionY < 0)
                errors.Add($"Macro region {region.RegionX}:{region.RegionY} has a negative index.");

            if (!HasPositiveDimensions(region.Bounds))
                errors.Add($"Macro region {region.RegionX}:{region.RegionY} has invalid bounds.");
            else if (validPlanBounds && !Contains(plan.Bounds, region.Bounds))
                errors.Add($"Macro region {region.RegionX}:{region.RegionY} is outside expedition bounds.");

            if (string.IsNullOrWhiteSpace(region.Biome))
                errors.Add($"Macro region {region.RegionX}:{region.RegionY} has an empty biome.");
            if (region.Biome is null)
                canComputeHash = false;

            if (region.DangerBudget < 0)
                errors.Add($"Macro region {region.RegionX}:{region.RegionY} has a negative danger budget.");
        }

        if (validPlanBounds && validRegions.Count == regions.Count)
            ValidateRegionPartition(validRegions, plan.Bounds, errors);

        IReadOnlyList<LuaMExpeditionSitePlan> sites;
        if (plan.Sites is null)
        {
            errors.Add("Expedition site collection is null.");
            sites = Array.Empty<LuaMExpeditionSitePlan>();
            canComputeHash = false;
        }
        else
        {
            sites = plan.Sites;
        }

        var validSites = new List<LuaMExpeditionSitePlan>(sites.Count);
        var sitesById = new Dictionary<string, LuaMExpeditionSitePlan>(StringComparer.Ordinal);
        var uniquePrototypeScopes = new HashSet<(string Scope, string Prototype)>();
        foreach (var site in sites)
        {
            if (site is null)
            {
                errors.Add("Expedition contains a null site.");
                canComputeHash = false;
                continue;
            }

            validSites.Add(site);
            if (site.Kind is not LuaMExpeditionSiteKind.Entry and
                not LuaMExpeditionSiteKind.Objective and
                not LuaMExpeditionSiteKind.Extraction and
                not LuaMExpeditionSiteKind.Secondary)
            {
                errors.Add($"Site {site.SiteId} has an unknown kind.");
            }

            if (string.IsNullOrWhiteSpace(site.SiteId))
                errors.Add("Expedition contains a site with an empty id.");
            if (site.SiteId is null)
            {
                canComputeHash = false;
            }
            else if (!sitesById.TryAdd(site.SiteId, site))
            {
                errors.Add($"Duplicate site id {site.SiteId}.");
            }

            if (string.IsNullOrWhiteSpace(site.PrototypeId))
                errors.Add($"Site {site.SiteId} has an empty prototype id.");
            if (site.PrototypeId is null)
                canComputeHash = false;

            if (string.IsNullOrWhiteSpace(site.UniqueScope))
                errors.Add($"Site {site.SiteId} has an empty unique scope.");
            if (site.UniqueScope is null)
                canComputeHash = false;

            if (site.PrototypeId is not null && site.UniqueScope is not null &&
                !string.IsNullOrWhiteSpace(site.PrototypeId) &&
                !string.IsNullOrWhiteSpace(site.UniqueScope) &&
                !uniquePrototypeScopes.Add((site.UniqueScope, site.PrototypeId)))
            {
                errors.Add($"Prototype {site.PrototypeId} is duplicated in unique scope {site.UniqueScope}.");
            }

            if (validPlanBounds && !plan.Bounds.Contains(site.Position))
                errors.Add($"Site {site.SiteId} is outside expedition bounds.");

            if (!HasPositiveDimensions(site.Footprint))
            {
                errors.Add($"Site {site.SiteId} has an invalid footprint.");
            }
            else
            {
                if (!site.Footprint.Contains(site.Position))
                    errors.Add($"Site {site.SiteId} position is outside its footprint.");

                if (validPlanBounds && !Contains(plan.Bounds, site.Footprint))
                    errors.Add($"Site {site.SiteId} footprint is outside expedition bounds.");
            }
        }

        for (var first = 0; first < validSites.Count; first++)
        {
            if (!HasPositiveDimensions(validSites[first].Footprint))
                continue;

            for (var second = first + 1; second < validSites.Count; second++)
            {
                if (!HasPositiveDimensions(validSites[second].Footprint))
                    continue;

                if (Overlaps(validSites[first].Footprint, validSites[second].Footprint))
                {
                    errors.Add(
                        $"Site footprints overlap: {validSites[first].SiteId} and {validSites[second].SiteId}.");
                }
            }
        }

        ValidateRequiredSite(validSites, LuaMExpeditionSiteKind.Entry, errors);
        ValidateRequiredSite(validSites, LuaMExpeditionSiteKind.Objective, errors);
        ValidateRequiredSite(validSites, LuaMExpeditionSiteKind.Extraction, errors);

        IReadOnlyList<LuaMExpeditionConnectionPlan> connections;
        if (plan.Connections is null)
        {
            errors.Add("Expedition connection collection is null.");
            connections = Array.Empty<LuaMExpeditionConnectionPlan>();
            canComputeHash = false;
        }
        else
        {
            connections = plan.Connections;
        }

        var adjacency = sitesById.Keys.ToDictionary(
            siteId => siteId,
            _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        var connectionKeys = new HashSet<(string First, string Second)>();
        var validEdges = new List<ValidatedEdge>();

        foreach (var connection in connections)
        {
            if (connection is null)
            {
                errors.Add("Expedition contains a null connection.");
                canComputeHash = false;
                continue;
            }

            var edgeValid = true;
            if (connection.FromSiteId is null || connection.ToSiteId is null)
                canComputeHash = false;

            LuaMExpeditionSitePlan? from = null;
            LuaMExpeditionSitePlan? to = null;
            var fromKnown = connection.FromSiteId is not null &&
                            sitesById.TryGetValue(connection.FromSiteId, out from);
            var toKnown = connection.ToSiteId is not null &&
                          sitesById.TryGetValue(connection.ToSiteId, out to);

            if (!fromKnown)
            {
                errors.Add($"Connection starts at unknown site {connection.FromSiteId ?? "<null>"}.");
                edgeValid = false;
            }

            if (!toKnown)
            {
                errors.Add($"Connection ends at unknown site {connection.ToSiteId ?? "<null>"}.");
                edgeValid = false;
            }

            if (fromKnown && toKnown && string.Equals(connection.FromSiteId, connection.ToSiteId, StringComparison.Ordinal))
            {
                errors.Add($"Connection {connection.FromSiteId}->{connection.ToSiteId} is a self-loop.");
                edgeValid = false;
            }

            if (connection.FromSiteId is not null && connection.ToSiteId is not null)
            {
                var key = CanonicalEdge(connection.FromSiteId, connection.ToSiteId);
                if (!connectionKeys.Add(key))
                {
                    errors.Add($"Duplicate connection {key.First}<->{key.Second}.");
                    edgeValid = false;
                }
            }

            if (connection.Kind is not LuaMExpeditionConnectionKind.SpanningTree and
                not LuaMExpeditionConnectionKind.Loop)
            {
                errors.Add($"Connection {connection.FromSiteId}->{connection.ToSiteId} has an unknown kind.");
                edgeValid = false;
            }

            if (connection.Path is null)
            {
                errors.Add($"Connection {connection.FromSiteId}->{connection.ToSiteId} has a null path.");
                canComputeHash = false;
                edgeValid = false;
            }
            else if (connection.Path.Count == 0)
            {
                errors.Add($"Connection {connection.FromSiteId}->{connection.ToSiteId} has no path.");
                edgeValid = false;
            }
            else
            {
                if (fromKnown && toKnown &&
                    (connection.Path[0] != from!.Position || connection.Path[^1] != to!.Position))
                {
                    errors.Add($"Connection {connection.FromSiteId}->{connection.ToSiteId} has invalid endpoints.");
                    edgeValid = false;
                }

                var pathPoints = new HashSet<LuaMExpeditionPoint>();
                for (var index = 0; index < connection.Path.Count; index++)
                {
                    var current = connection.Path[index];
                    if (validPlanBounds && !plan.Bounds.Contains(current))
                    {
                        errors.Add($"Connection {connection.FromSiteId}->{connection.ToSiteId} leaves expedition bounds at {index}.");
                        edgeValid = false;
                    }

                    if (!pathPoints.Add(current))
                    {
                        errors.Add($"Connection {connection.FromSiteId}->{connection.ToSiteId} repeats a path point at {index}.");
                        edgeValid = false;
                    }

                    if (index == 0)
                        continue;

                    var previous = connection.Path[index - 1];
                    if (ManhattanDistance(previous, current) != 1)
                    {
                        errors.Add($"Connection {connection.FromSiteId}->{connection.ToSiteId} has a non-adjacent step at {index}.");
                        edgeValid = false;
                    }
                }
            }

            if (!edgeValid || !fromKnown || !toKnown)
                continue;

            var fromSiteId = connection.FromSiteId!;
            var toSiteId = connection.ToSiteId!;
            adjacency[fromSiteId].Add(toSiteId);
            adjacency[toSiteId].Add(fromSiteId);
            validEdges.Add(new ValidatedEdge(fromSiteId, toSiteId, connection.Kind));
        }

        ValidateGraph(sitesById, adjacency, validEdges, errors);

        if (string.IsNullOrWhiteSpace(plan.PlanHash))
        {
            errors.Add("Plan hash is empty.");
        }
        else if (canComputeHash &&
                 !string.Equals(plan.PlanHash, LuaMExpeditionPlanHasher.Compute(plan), StringComparison.Ordinal))
        {
            errors.Add("Plan hash does not match plan contents.");
        }

        return errors;
    }

    public static void EnsureValid(LuaMExpeditionPlan plan)
    {
        var errors = Validate(plan);
        if (errors.Count > 0)
            throw new InvalidOperationException($"Invalid expedition plan: {string.Join("; ", errors)}");
    }

    private static void ValidateGraph(
        IReadOnlyDictionary<string, LuaMExpeditionSitePlan> sitesById,
        IReadOnlyDictionary<string, HashSet<string>> adjacency,
        IReadOnlyCollection<ValidatedEdge> edges,
        ICollection<string> errors)
    {
        if (sitesById.Count == 0)
        {
            errors.Add("Expedition graph has no sites.");
            return;
        }

        var reached = ReachableFrom(sitesById.Keys.OrderBy(siteId => siteId, StringComparer.Ordinal).First(), adjacency);
        if (reached.Count != sitesById.Count)
        {
            var disconnected = sitesById.Keys
                .Where(siteId => !reached.Contains(siteId))
                .OrderBy(siteId => siteId, StringComparer.Ordinal);
            errors.Add($"Expedition graph is disconnected; unreachable sites: {string.Join(", ", disconnected)}.");
        }

        var treeEdges = edges.Where(edge => edge.Kind == LuaMExpeditionConnectionKind.SpanningTree).ToArray();
        var expectedTreeEdges = sitesById.Count - 1;
        if (treeEdges.Length != expectedTreeEdges)
        {
            errors.Add($"Expected {expectedTreeEdges} spanning-tree connections, got {treeEdges.Length}.");
        }

        var orderedSiteIds = sitesById.Keys.OrderBy(siteId => siteId, StringComparer.Ordinal).ToArray();
        var siteIndexes = orderedSiteIds
            .Select((siteId, index) => (siteId, index))
            .ToDictionary(pair => pair.siteId, pair => pair.index, StringComparer.Ordinal);
        var sets = new DisjointSets(orderedSiteIds.Length);
        var treeHasCycle = false;
        long treeWeight = 0;
        foreach (var edge in treeEdges)
        {
            if (!sets.Union(siteIndexes[edge.FromSiteId], siteIndexes[edge.ToSiteId]))
                treeHasCycle = true;

            treeWeight += ManhattanDistance(
                sitesById[edge.FromSiteId].Position,
                sitesById[edge.ToSiteId].Position);
        }

        if (treeHasCycle)
            errors.Add("Spanning-tree connections contain a cycle.");

        var treeIsSpanning = treeEdges.Length == expectedTreeEdges &&
                             !treeHasCycle &&
                             orderedSiteIds.All(siteId =>
                                 sets.Find(siteIndexes[siteId]) == sets.Find(siteIndexes[orderedSiteIds[0]]));
        if (!treeIsSpanning)
        {
            errors.Add("Spanning-tree connections do not connect every site.");
        }
        else
        {
            var minimumWeight = ComputeMinimumSpanningTreeWeight(orderedSiteIds, sitesById);
            if (treeWeight != minimumWeight)
                errors.Add($"Spanning-tree weight {treeWeight} is not minimal; expected {minimumWeight}.");
        }

        var loopCount = edges.Count(edge => edge.Kind == LuaMExpeditionConnectionKind.Loop);
        if (sitesById.Count >= 3 && loopCount == 0)
            errors.Add("Expedition graph has no loop connection.");
    }

    private static long ComputeMinimumSpanningTreeWeight(
        IReadOnlyList<string> orderedSiteIds,
        IReadOnlyDictionary<string, LuaMExpeditionSitePlan> sitesById)
    {
        var candidates = new List<WeightedEdge>();
        for (var first = 0; first < orderedSiteIds.Count; first++)
        {
            for (var second = first + 1; second < orderedSiteIds.Count; second++)
            {
                candidates.Add(new WeightedEdge(
                    first,
                    second,
                    ManhattanDistance(
                        sitesById[orderedSiteIds[first]].Position,
                        sitesById[orderedSiteIds[second]].Position)));
            }
        }

        var sets = new DisjointSets(orderedSiteIds.Count);
        long total = 0;
        var accepted = 0;
        foreach (var candidate in candidates
                     .OrderBy(candidate => candidate.Weight)
                     .ThenBy(candidate => orderedSiteIds[candidate.FirstIndex], StringComparer.Ordinal)
                     .ThenBy(candidate => orderedSiteIds[candidate.SecondIndex], StringComparer.Ordinal))
        {
            if (!sets.Union(candidate.FirstIndex, candidate.SecondIndex))
                continue;

            total += candidate.Weight;
            accepted++;
            if (accepted == orderedSiteIds.Count - 1)
                break;
        }

        return total;
    }

    private static HashSet<string> ReachableFrom(
        string start,
        IReadOnlyDictionary<string, HashSet<string>> adjacency)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal) { start };
        var pending = new Queue<string>();
        pending.Enqueue(start);

        while (pending.TryDequeue(out var current))
        {
            foreach (var neighbor in adjacency[current])
            {
                if (reached.Add(neighbor))
                    pending.Enqueue(neighbor);
            }
        }

        return reached;
    }

    private static void ValidateRegionPartition(
        IReadOnlyCollection<LuaMExpeditionRegionPlan> regions,
        LuaMExpeditionBounds bounds,
        ICollection<string> errors)
    {
        var expectedRegionY = 0;
        long expectedMinY = bounds.MinY;

        foreach (var row in regions.GroupBy(region => region.RegionY).OrderBy(row => row.Key))
        {
            if (row.Key != expectedRegionY)
                errors.Add($"Macro region row index {row.Key} is not contiguous; expected {expectedRegionY}.");

            var orderedRow = row.OrderBy(region => region.RegionX).ToArray();
            var rowMinY = orderedRow[0].Bounds.MinY;
            var rowMaxY = orderedRow[0].Bounds.MaxY;
            if (rowMinY != expectedMinY)
                errors.Add($"Macro region row {row.Key} starts at Y={rowMinY}; expected {expectedMinY}.");

            long expectedMinX = bounds.MinX;
            var expectedRegionX = 0;
            foreach (var region in orderedRow)
            {
                if (region.RegionX != expectedRegionX)
                    errors.Add($"Macro region column index {region.RegionX} in row {row.Key} is not contiguous; expected {expectedRegionX}.");

                if (region.Bounds.MinY != rowMinY || region.Bounds.MaxY != rowMaxY)
                    errors.Add($"Macro region {region.RegionX}:{region.RegionY} does not share its row's Y bounds.");

                if (region.Bounds.MinX != expectedMinX)
                    errors.Add($"Macro region {region.RegionX}:{region.RegionY} starts at X={region.Bounds.MinX}; expected {expectedMinX}.");

                expectedMinX = (long) region.Bounds.MaxX + 1;
                expectedRegionX++;
            }

            if (expectedMinX != (long) bounds.MaxX + 1)
                errors.Add($"Macro region row {row.Key} does not cover the expedition width.");

            expectedMinY = (long) rowMaxY + 1;
            expectedRegionY++;
        }

        if (expectedMinY != (long) bounds.MaxY + 1)
            errors.Add("Macro region rows do not cover the expedition height.");
    }

    private static void ValidateRequiredSite(
        IReadOnlyCollection<LuaMExpeditionSitePlan> sites,
        LuaMExpeditionSiteKind kind,
        ICollection<string> errors)
    {
        var count = sites.Count(site => site.Kind == kind);
        if (count != 1)
            errors.Add($"Expected exactly one {kind} site, got {count}.");
    }

    private static bool HasPositiveDimensions(LuaMExpeditionBounds bounds)
    {
        return bounds.MaxX >= bounds.MinX && bounds.MaxY >= bounds.MinY;
    }

    private static bool Contains(LuaMExpeditionBounds outer, LuaMExpeditionBounds inner)
    {
        return inner.MinX >= outer.MinX && inner.MaxX <= outer.MaxX &&
               inner.MinY >= outer.MinY && inner.MaxY <= outer.MaxY;
    }

    private static bool Overlaps(LuaMExpeditionBounds first, LuaMExpeditionBounds second)
    {
        return first.MinX <= second.MaxX && first.MaxX >= second.MinX &&
               first.MinY <= second.MaxY && first.MaxY >= second.MinY;
    }

    private static (string First, string Second) CanonicalEdge(string first, string second)
    {
        return string.CompareOrdinal(first, second) <= 0 ? (first, second) : (second, first);
    }

    private static long ManhattanDistance(LuaMExpeditionPoint first, LuaMExpeditionPoint second)
    {
        return Math.Abs((long) first.X - second.X) + Math.Abs((long) first.Y - second.Y);
    }

    private readonly record struct ValidatedEdge(
        string FromSiteId,
        string ToSiteId,
        LuaMExpeditionConnectionKind Kind);

    private readonly record struct WeightedEdge(int FirstIndex, int SecondIndex, long Weight);

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

        public int Find(int item)
        {
            while (_parents[item] != item)
            {
                _parents[item] = _parents[_parents[item]];
                item = _parents[item];
            }

            return item;
        }
    }
}
