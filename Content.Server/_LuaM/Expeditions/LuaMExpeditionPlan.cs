using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Content.Server._LuaM.Expeditions;

/// <summary>
/// A tile coordinate in the server-only expedition plan.
/// </summary>
public readonly record struct LuaMExpeditionPoint(int X, int Y);

/// <summary>
/// An inclusive tile rectangle used by the macro planner.
/// </summary>
public readonly record struct LuaMExpeditionBounds(int MinX, int MinY, int MaxX, int MaxY)
{
    public int Width => MaxX - MinX + 1;
    public int Height => MaxY - MinY + 1;

    public bool Contains(LuaMExpeditionPoint point)
    {
        return point.X >= MinX && point.X <= MaxX && point.Y >= MinY && point.Y <= MaxY;
    }
}

public enum LuaMExpeditionSiteKind
{
    Entry,
    Objective,
    Extraction,
    Secondary,
}

public enum LuaMExpeditionConnectionKind
{
    SpanningTree,
    Loop,
}

public sealed record LuaMExpeditionRegionPlan(
    int RegionX,
    int RegionY,
    LuaMExpeditionBounds Bounds,
    string Biome,
    int DangerBudget);

public sealed record LuaMExpeditionSitePlan(
    string SiteId,
    LuaMExpeditionSiteKind Kind,
    string PrototypeId,
    string UniqueScope,
    LuaMExpeditionPoint Position,
    LuaMExpeditionBounds Footprint);

public sealed record LuaMExpeditionConnectionPlan(
    string FromSiteId,
    string ToSiteId,
    IReadOnlyList<LuaMExpeditionPoint> Path,
    LuaMExpeditionConnectionKind Kind)
{
    public LuaMExpeditionConnectionPlan(string fromSiteId, string toSiteId, IEnumerable<LuaMExpeditionPoint> path)
        : this(fromSiteId, toSiteId, path.ToArray(), LuaMExpeditionConnectionKind.SpanningTree)
    {
    }

    public LuaMExpeditionConnectionPlan(
        string fromSiteId,
        string toSiteId,
        IEnumerable<LuaMExpeditionPoint> path,
        LuaMExpeditionConnectionKind kind)
        : this(fromSiteId, toSiteId, path.ToArray(), kind)
    {
    }
}

/// <summary>
/// Server-only, ECS-independent description of one generated expedition.
/// </summary>
public sealed record LuaMExpeditionPlan(
    string CampaignId,
    string ExpeditionId,
    ulong Seed,
    int GeneratorVersion,
    LuaMExpeditionBounds Bounds,
    IReadOnlyList<LuaMExpeditionRegionPlan> Regions,
    IReadOnlyList<LuaMExpeditionSitePlan> Sites,
    IReadOnlyList<LuaMExpeditionConnectionPlan> Connections,
    string PlanHash);

internal static class LuaMExpeditionPlanHasher
{
    public static string Compute(LuaMExpeditionPlan plan)
    {
        var hash = new StableHash64();
        hash.AddString(plan.CampaignId);
        hash.AddString(plan.ExpeditionId);
        hash.AddUInt64(plan.Seed);
        hash.AddInt32(plan.GeneratorVersion);
        AddBounds(ref hash, plan.Bounds);

        foreach (var region in plan.Regions.OrderBy(region => region.RegionY).ThenBy(region => region.RegionX))
        {
            hash.AddInt32(region.RegionX);
            hash.AddInt32(region.RegionY);
            AddBounds(ref hash, region.Bounds);
            hash.AddString(region.Biome);
            hash.AddInt32(region.DangerBudget);
        }

        foreach (var site in plan.Sites.OrderBy(site => site.SiteId, StringComparer.Ordinal))
        {
            hash.AddString(site.SiteId);
            hash.AddInt32((int) site.Kind);
            hash.AddString(site.PrototypeId);
            hash.AddString(site.UniqueScope);
            hash.AddInt32(site.Position.X);
            hash.AddInt32(site.Position.Y);
            AddBounds(ref hash, site.Footprint);
        }

        foreach (var connection in plan.Connections
                     .OrderBy(connection => connection.FromSiteId, StringComparer.Ordinal)
                     .ThenBy(connection => connection.ToSiteId, StringComparer.Ordinal)
                     .ThenBy(connection => connection.Kind))
        {
            hash.AddString(connection.FromSiteId);
            hash.AddString(connection.ToSiteId);
            hash.AddInt32((int) connection.Kind);
            hash.AddInt32(connection.Path.Count);

            foreach (var point in connection.Path)
            {
                hash.AddInt32(point.X);
                hash.AddInt32(point.Y);
            }
        }

        return hash.ToString();
    }

    public static ulong DeriveSeed(string campaignId, string expeditionId, int generatorVersion, string scope)
    {
        var hash = new StableHash64();
        hash.AddString(campaignId);
        hash.AddString(expeditionId);
        hash.AddInt32(generatorVersion);
        hash.AddString(scope);
        return hash.Value;
    }

    private static void AddBounds(ref StableHash64 hash, LuaMExpeditionBounds bounds)
    {
        hash.AddInt32(bounds.MinX);
        hash.AddInt32(bounds.MinY);
        hash.AddInt32(bounds.MaxX);
        hash.AddInt32(bounds.MaxY);
    }

    private struct StableHash64
    {
        private const ulong Offset = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        public StableHash64()
        {
            Value = Offset;
        }

        public ulong Value { get; private set; }

        public void AddString(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            AddInt32(bytes.Length);
            foreach (var byteValue in bytes)
                AddByte(byteValue);
        }

        public void AddInt32(int value)
        {
            var unsigned = unchecked((uint) value);
            AddByte((byte) unsigned);
            AddByte((byte) (unsigned >> 8));
            AddByte((byte) (unsigned >> 16));
            AddByte((byte) (unsigned >> 24));
        }

        public void AddUInt64(ulong value)
        {
            for (var index = 0; index < sizeof(ulong); index++)
                AddByte((byte) (value >> (index * 8)));
        }

        public override string ToString()
        {
            return Value.ToString("X16", CultureInfo.InvariantCulture);
        }

        private void AddByte(byte value)
        {
            Value ^= value;
            Value *= Prime;
        }
    }
}
