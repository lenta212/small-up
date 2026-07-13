#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server._LuaM.Expeditions;
using NUnit.Framework;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMExpeditionPlannerTest
{
    private static readonly (int Width, int Height, int RegionSize)[] PropertyWorldSizes =
    {
        (64, 64, 16),
        (65, 97, 16),
        (257, 513, 64),
        (512, 512, 64),
        (1024, 384, 128),
        (513, 257, 256),
    };

    [Test]
    public void SameSeedAndVersionProduceSamePlanHash()
    {
        var planner = new LuaMExpeditionPlanner();

        var first = planner.Build("campaign-alpha", "expedition-01", 0xA11CEUL);
        var second = planner.Build("campaign-alpha", "expedition-01", 0xA11CEUL);

        Assert.That(first.PlanHash, Is.EqualTo(second.PlanHash));
        Assert.That(first.Sites, Is.EqualTo(second.Sites));
        var firstConnections = first.Connections.Select(FormatConnection).ToArray();
        var secondConnections = second.Connections.Select(FormatConnection).ToArray();
        Assert.That(firstConnections, Is.EqualTo(secondConnections));
    }

    [Test]
    public void PlanHashDoesNotDependOnCollectionOrdering()
    {
        var planner = new LuaMExpeditionPlanner();
        var plan = planner.Build("campaign-alpha", "canonical-order", 0xC011EC7UL);
        var reordered = plan with
        {
            Regions = plan.Regions.Reverse().ToArray(),
            Sites = plan.Sites.Reverse().ToArray(),
            Connections = plan.Connections.Reverse().ToArray(),
        };

        Assert.That(LuaMExpeditionPlanValidator.Validate(reordered), Is.Empty);
        Assert.That(reordered.PlanHash, Is.EqualTo(plan.PlanHash));
    }

    [Test]
    public void DifferentSeedChangesPlanHashAndUnsupportedVersionIsRejected()
    {
        var planner = new LuaMExpeditionPlanner();

        var first = planner.Build("campaign-alpha", "expedition-01", 0xA11CEUL);
        var differentSeed = planner.Build("campaign-alpha", "expedition-01", 0xB22CEUL);

        Assert.That(differentSeed.PlanHash, Is.Not.EqualTo(first.PlanHash));
        Assert.That(
            () => planner.Build("campaign-alpha", "expedition-01", 0xA11CEUL, generatorVersion: 1),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void GeneratorVersionTwoHasStableGoldenHash()
    {
        var plan = new LuaMExpeditionPlanner().Build("campaign-alpha", "expedition-01", 0xA11CEUL);

        Assert.That(plan.PlanHash, Is.EqualTo("3ED407E160F56ED5"));
    }

    [Test]
    public void PlannerRejectsAmbiguousIdentifiersAndUnboundedWorlds()
    {
        var planner = new LuaMExpeditionPlanner();

        Assert.Multiple(() =>
        {
            Assert.That(() => planner.Build("campaign\0alpha", "expedition", 1),
                Throws.TypeOf<ArgumentException>());
            Assert.That(() => planner.Build("campaign", "expedition", 1, width: 4097),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => planner.Build("campaign", "expedition", 1, width: 4096, height: 4096, macroRegionSize: 16),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public void GeneratedPlanHasMinimumSpanningTreeLoopAndRequiredRoute()
    {
        var planner = new LuaMExpeditionPlanner();
        var plan = planner.Build("campaign-alpha", "expedition-01", 0xA11CEUL);

        AssertValid(plan);
        Assert.That(plan.Sites.Select(site => site.SiteId).Distinct(StringComparer.Ordinal).Count(),
            Is.EqualTo(plan.Sites.Count));
        Assert.That(plan.Connections.Count(connection => connection.Kind == LuaMExpeditionConnectionKind.SpanningTree),
            Is.EqualTo(plan.Sites.Count - 1));
        Assert.That(plan.Connections.Count(connection => connection.Kind == LuaMExpeditionConnectionKind.Loop),
            Is.EqualTo(1));
        Assert.That(plan.Connections.Count, Is.EqualTo(plan.Sites.Count));

        Assert.That(HasRoute(plan, LuaMExpeditionSiteKind.Entry, LuaMExpeditionSiteKind.Objective), Is.True);
        Assert.That(HasRoute(plan, LuaMExpeditionSiteKind.Objective, LuaMExpeditionSiteKind.Extraction), Is.True);
    }

    [TestCase(64, 64, 16)]
    [TestCase(65, 129, 16)]
    [TestCase(513, 257, 64)]
    [TestCase(1024, 384, 128)]
    public void MacroRegionsStayInsideWorldBounds(int width, int height, int regionSize)
    {
        var planner = new LuaMExpeditionPlanner();
        var plan = planner.Build("campaign-alpha", "sizing", 7UL, width: width, height: height, macroRegionSize: regionSize);

        Assert.That(plan.Regions, Is.Not.Empty);
        AssertValid(plan);
        Assert.That(plan.Bounds.Width, Is.EqualTo(width));
        Assert.That(plan.Bounds.Height, Is.EqualTo(height));
    }

    [Test]
    public void TwoThousandSeedsAcrossWorldSizesSatisfyPlannerInvariants()
    {
        var planner = new LuaMExpeditionPlanner();
        var hashes = new HashSet<string>(StringComparer.Ordinal);

        for (ulong seed = 0; seed < 2048; seed++)
        {
            var size = PropertyWorldSizes[(int) (seed % (ulong) PropertyWorldSizes.Length)];
            var plan = planner.Build(
                "property-campaign",
                "property-expedition",
                seed,
                width: size.Width,
                height: size.Height,
                macroRegionSize: size.RegionSize);

            AssertValid(plan, $"seed={seed}, size={size.Width}x{size.Height}, region={size.RegionSize}");
            Assert.That(plan.Connections.Count, Is.EqualTo(plan.Sites.Count), $"seed={seed}");
            Assert.That(plan.Connections.Count(connection => connection.Kind == LuaMExpeditionConnectionKind.Loop),
                Is.EqualTo(1), $"seed={seed}");
            Assert.That(hashes.Add(plan.PlanHash), Is.True, $"Duplicate PlanHash for seed={seed}.");

            if (seed % 257 == 0)
            {
                var repeated = planner.Build(
                    "property-campaign",
                    "property-expedition",
                    seed,
                    width: size.Width,
                    height: size.Height,
                    macroRegionSize: size.RegionSize);
                Assert.That(repeated.PlanHash, Is.EqualTo(plan.PlanHash), $"seed={seed}");
            }
        }
    }

    [Test]
    public void ValidatorRejectsDisconnectedGraphAndMissingLoop()
    {
        var plan = BuildTamperTarget();
        var connections = plan.Connections
            .Where(connection => connection.Kind == LuaMExpeditionConnectionKind.SpanningTree)
            .Skip(1)
            .ToArray();
        var tampered = plan with { Connections = connections };

        var errors = LuaMExpeditionPlanValidator.Validate(tampered);

        AssertHasError(errors, "graph is disconnected");
        AssertHasError(errors, "no loop connection");
    }

    [Test]
    public void ValidatorRejectsOverlappingFootprints()
    {
        var plan = BuildTamperTarget();
        var sites = plan.Sites.ToArray();
        var entry = sites.Single(site => site.Kind == LuaMExpeditionSiteKind.Entry);
        var secondaryIndex = Array.FindIndex(sites, site => site.Kind == LuaMExpeditionSiteKind.Secondary);
        sites[secondaryIndex] = sites[secondaryIndex] with
        {
            Position = entry.Position,
            Footprint = entry.Footprint,
        };
        var tampered = plan with { Sites = sites };

        var errors = LuaMExpeditionPlanValidator.Validate(tampered);

        AssertHasError(errors, "Site footprints overlap");
    }

    [Test]
    public void ValidatorRejectsDuplicatePrototypeWithinUniqueScope()
    {
        var plan = BuildTamperTarget();
        var sites = plan.Sites.ToArray();
        var ruins = sites
            .Select((site, index) => (site, index))
            .Where(pair => pair.site.PrototypeId == "LuaMExpeditionRuin")
            .ToArray();
        Assert.That(ruins, Has.Length.GreaterThanOrEqualTo(2));
        sites[ruins[1].index] = ruins[1].site with { UniqueScope = ruins[0].site.UniqueScope };
        var tampered = plan with { Sites = sites };

        var errors = LuaMExpeditionPlanValidator.Validate(tampered);

        AssertHasError(errors, "is duplicated in unique scope");
    }

    [Test]
    public void ValidatorRejectsPathOutsideBounds()
    {
        var plan = BuildTamperTarget();
        var connections = plan.Connections.ToArray();
        var original = connections[0];
        var path = original.Path.ToArray();
        path[1] = new LuaMExpeditionPoint(plan.Bounds.MinX - 1, plan.Bounds.MinY - 1);
        connections[0] = original with { Path = path };
        var tampered = plan with { Connections = connections };

        var errors = LuaMExpeditionPlanValidator.Validate(tampered);

        AssertHasError(errors, "leaves expedition bounds");
    }

    [Test]
    public void ValidatorRejectsReverseDuplicateConnection()
    {
        var plan = BuildTamperTarget();
        var original = plan.Connections[0];
        var reversed = new LuaMExpeditionConnectionPlan(
            original.ToSiteId,
            original.FromSiteId,
            original.Path.Reverse(),
            LuaMExpeditionConnectionKind.Loop);
        var tampered = plan with { Connections = plan.Connections.Append(reversed).ToArray() };

        var errors = LuaMExpeditionPlanValidator.Validate(tampered);

        AssertHasError(errors, "Duplicate connection");
    }

    [Test]
    public void ValidatorRejectsTreeThatIsNotMarkedAsTreeAndLoop()
    {
        var plan = BuildTamperTarget();
        var connections = plan.Connections
            .Select(connection => connection.Kind == LuaMExpeditionConnectionKind.Loop
                ? connection with { Kind = LuaMExpeditionConnectionKind.SpanningTree }
                : connection)
            .ToArray();
        var tampered = plan with { Connections = connections };

        var errors = LuaMExpeditionPlanValidator.Validate(tampered);

        AssertHasError(errors, "spanning-tree connections");
        AssertHasError(errors, "no loop connection");
    }

    [Test]
    public void ValidatorRejectsContentChangedWithoutNewPlanHash()
    {
        var plan = BuildTamperTarget();
        var tampered = plan with { CampaignId = "different-campaign" };

        var errors = LuaMExpeditionPlanValidator.Validate(tampered);

        AssertHasError(errors, "Plan hash does not match plan contents");
    }

    [Test]
    public void ValidatorRejectsUnknownSiteKind()
    {
        var plan = BuildTamperTarget();
        var sites = plan.Sites.ToArray();
        sites[0] = sites[0] with { Kind = (LuaMExpeditionSiteKind) 999 };
        var tampered = plan with { Sites = sites };

        var errors = LuaMExpeditionPlanValidator.Validate(tampered);

        AssertHasError(errors, "unknown kind");
    }

    [Test]
    public void ValidatorRejectsUnsupportedGeneratorVersion()
    {
        var plan = BuildTamperTarget();
        var tampered = plan with { GeneratorVersion = 1 };

        var errors = LuaMExpeditionPlanValidator.Validate(tampered);

        AssertHasError(errors, "Unsupported generator version");
    }

    [Test]
    public void ValidatorRejectsOversizedBoundsAndIdentifier()
    {
        var plan = BuildTamperTarget();
        var tampered = plan with
        {
            CampaignId = new string('x', LuaMExpeditionPlanner.MaximumIdentifierLength + 1),
            Bounds = new LuaMExpeditionBounds(0, 0, LuaMExpeditionPlanner.MaximumDimension, 511),
        };

        var errors = LuaMExpeditionPlanValidator.Validate(tampered);

        AssertHasError(errors, "Campaign id exceeds");
        AssertHasError(errors, "bounds exceed");
    }

    private static LuaMExpeditionPlan BuildTamperTarget()
    {
        return new LuaMExpeditionPlanner().Build("tamper-campaign", "tamper-expedition", 0x7A4E2UL);
    }

    private static bool HasRoute(
        LuaMExpeditionPlan plan,
        LuaMExpeditionSiteKind fromKind,
        LuaMExpeditionSiteKind toKind)
    {
        var from = plan.Sites.Single(site => site.Kind == fromKind).SiteId;
        var to = plan.Sites.Single(site => site.Kind == toKind).SiteId;
        var adjacency = plan.Sites.ToDictionary(
            site => site.SiteId,
            _ => new List<string>(),
            StringComparer.Ordinal);
        foreach (var connection in plan.Connections)
        {
            adjacency[connection.FromSiteId].Add(connection.ToSiteId);
            adjacency[connection.ToSiteId].Add(connection.FromSiteId);
        }

        var reached = new HashSet<string>(StringComparer.Ordinal) { from };
        var pending = new Queue<string>();
        pending.Enqueue(from);
        while (pending.TryDequeue(out var current))
        {
            foreach (var neighbor in adjacency[current])
            {
                if (reached.Add(neighbor))
                    pending.Enqueue(neighbor);
            }
        }

        return reached.Contains(to);
    }

    private static void AssertValid(LuaMExpeditionPlan plan, string? context = null)
    {
        var errors = LuaMExpeditionPlanValidator.Validate(plan);
        Assert.That(
            errors,
            Is.Empty,
            $"{context ?? "Generated plan is invalid."}{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
    }

    private static void AssertHasError(IEnumerable<string> errors, string fragment)
    {
        var materialized = errors.ToArray();
        Assert.That(
            materialized.Any(error => error.Contains(fragment, StringComparison.OrdinalIgnoreCase)),
            Is.True,
            $"Expected an error containing '{fragment}'. Actual:{Environment.NewLine}{string.Join(Environment.NewLine, materialized)}");
    }

    private static string FormatConnection(LuaMExpeditionConnectionPlan connection)
    {
        return $"{connection.Kind}:{connection.FromSiteId}->{connection.ToSiteId}:" +
               string.Join(",", connection.Path.Select(point => $"{point.X}:{point.Y}"));
    }
}
