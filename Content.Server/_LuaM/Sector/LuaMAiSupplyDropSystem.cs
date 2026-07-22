using System;
using System.Linq;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._LuaM.Sector;

public sealed partial class LuaMAiSupplyDropSystem : EntitySystem
{
    private const string SupplyDropPrototype = "LuaMAiSupplyDrop";
    private const float DropRadiusMin = 1.0f;
    private const float DropRadiusMax = 2.75f;

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private LuaMSectorStorySystem _stories = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private MetaDataSystem _metaData = default!;
    [Dependency] private LuaMAiPhysicalBaseBudgetSystem _physical = default!;

    public bool TrySpawnForLatestTrade(string actor, out EntityUid dropUid, out string summary)
    {
        dropUid = EntityUid.Invalid;

        if (!_physical.Enabled)
        {
            summary = LuaMAiPhysicalBaseFeature.DisabledReason;
            return false;
        }

        if (!_prototypes.HasIndex<EntityPrototype>(SupplyDropPrototype))
        {
            summary = $"AI supply drop skipped: prototype {SupplyDropPrototype} is missing";
            return false;
        }

        if (!TryGetAiBaseDropCoordinates(out var coordinates))
        {
            summary = "AI supply drop skipped: no physical AI base beacon";
            return false;
        }

        var state = _stories.GetAiBaseState();
        var latestTrade = state.TradeLog
            .OrderByDescending(entry => entry.Cycle)
            .FirstOrDefault();
        if (latestTrade == null)
        {
            summary = "AI supply drop skipped: AI base has no logistics trade yet";
            return false;
        }

        if (TryFindExistingDrop(latestTrade.Cycle, out dropUid, out var existingDrop))
        {
            summary = $"AI supply drop already active: {existingDrop.Resource} x{existingDrop.Amount} from {Trim(existingDrop.Vessel, 96)} [{ToPrettyString(dropUid)}]";
            return true;
        }

        if (!_physical.CanSpawn(LuaMAiPhysicalEntityKind.Drop, coordinates.MapId, out summary))
            return false;

        var offset = _random.NextAngle().ToVec() * _random.NextFloat(DropRadiusMin, DropRadiusMax);
        dropUid = Spawn(SupplyDropPrototype, new MapCoordinates(coordinates.Position + offset, coordinates.MapId));

        var drop = EnsureComp<LuaMAiSupplyDropComponent>(dropUid);
        drop.BaseId = "LuaM-AI-Base";
        drop.TradeCycle = latestTrade.Cycle;
        drop.Resource = latestTrade.Resource;
        drop.Amount = latestTrade.Amount;
        drop.Vessel = latestTrade.Vessel;
        drop.Role = latestTrade.Role;
        drop.Actor = Trim(actor, 96);
        drop.Summary = Trim(latestTrade.Summary, 192);

        _metaData.SetEntityName(dropUid, $"AI supply drop: {drop.Resource} x{drop.Amount}");
        summary = $"AI supply drop created: {drop.Resource} x{drop.Amount} from {Trim(drop.Vessel, 96)} [{ToPrettyString(dropUid)}]";
        return true;
    }

    private bool TryGetAiBaseDropCoordinates(out MapCoordinates coordinates)
    {
        var query = EntityQueryEnumerator<LuaMAiBaseAnchorComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            if (TerminatingOrDeleted(uid))
                continue;

            coordinates = _transform.ToMapCoordinates(Transform(uid).Coordinates, logError: false);
            if (coordinates != MapCoordinates.Nullspace)
                return true;
        }

        coordinates = MapCoordinates.Nullspace;
        return false;
    }

    private bool TryFindExistingDrop(int tradeCycle, out EntityUid dropUid, out LuaMAiSupplyDropComponent drop)
    {
        var query = EntityQueryEnumerator<LuaMAiSupplyDropComponent>();
        while (query.MoveNext(out var uid, out var candidate))
        {
            if (TerminatingOrDeleted(uid) ||
                candidate.TradeCycle != tradeCycle ||
                !candidate.BaseId.Equals("LuaM-AI-Base", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            dropUid = uid;
            drop = candidate;
            return true;
        }

        dropUid = EntityUid.Invalid;
        drop = null!;
        return false;
    }

    private static string Trim(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        value = value.Trim();
        return value.Length <= maxLength
            ? value
            : value[..maxLength];
    }
}
