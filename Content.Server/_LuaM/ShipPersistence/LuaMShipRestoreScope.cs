using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared.Station;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Server._LuaM.ShipPersistence;

/// <summary>
/// Owns every entity created by one successful ship deserialization until the
/// database restore transition is either committed or rolled back.
/// </summary>
public sealed class LuaMShipRestoreScope
{
    private HashSet<EntityUid>? _createdEntities;

    public Guid ShipId { get; }
    public EntityUid Grid { get; }
    public int? RepairedEntityCount { get; }
    public string? RepairedPrototypeManifestHash { get; }
    internal ProtoId<VesselPrototype>? VesselId { get; }
    internal StationConfig? VesselStationConfig { get; }

    internal LuaMShipRestoreScope(
        Guid shipId,
        EntityUid grid,
        HashSet<EntityUid> createdEntities,
        int? repairedEntityCount,
        string? repairedPrototypeManifestHash,
        ProtoId<VesselPrototype>? vesselId,
        StationConfig? vesselStationConfig)
    {
        ShipId = shipId;
        Grid = grid;
        _createdEntities = createdEntities;
        RepairedEntityCount = repairedEntityCount;
        RepairedPrototypeManifestHash = repairedPrototypeManifestHash;
        VesselId = vesselId;
        VesselStationConfig = vesselStationConfig;
    }

    internal bool TryTakeCreatedEntities(out IReadOnlySet<EntityUid> createdEntities)
    {
        if (_createdEntities == null)
        {
            createdEntities = default!;
            return false;
        }

        createdEntities = _createdEntities;
        _createdEntities = null;
        return true;
    }

    internal bool TryGetCreatedEntities(out IReadOnlySet<EntityUid> createdEntities)
    {
        if (_createdEntities == null)
        {
            createdEntities = default!;
            return false;
        }

        createdEntities = _createdEntities;
        return true;
    }
}
