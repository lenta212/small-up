using System.Collections.Generic;

namespace Content.Server._LuaM.ShipPersistence;

/// <summary>
/// Complete serialized state of one ship grid. Persistence backends may store
/// this record as-is; the runtime system remains independent of any database.
/// </summary>
public sealed record LuaMFullShipSnapshot(
    int FormatVersion,
    Guid ShipId,
    long Revision,
    DateTime CreatedAtUtc,
    byte[] Payload,
    int PayloadSizeBytes,
    string PayloadHash,
    int EntityCount,
    string PrototypeManifestHash,
    string SourceBuildVersion,
    string SourceBuildHash);

/// <summary>
/// Privacy-bounded description passed to the local ship-generator sidecar.
/// It deliberately excludes ship identity, owner, coordinates, and raw entity state.
/// </summary>
public sealed record LuaMSavedShipManifest(
    int SnapshotFormatVersion,
    int EntityCount,
    int PayloadSizeBytes,
    string PrototypeManifestHash,
    IReadOnlyList<LuaMSavedShipPrototypeCount> Prototypes);

public sealed record LuaMSavedShipPrototypeCount(string Id, int Count);
