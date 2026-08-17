using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Robust.Shared.Network;

namespace Content.Server.Database;

public static class LuaMShipPersistenceLimits
{
    /// <summary>
    /// Maximum size of the durable JSON envelope stored in the database.
    /// </summary>
    public const int MaxPayloadBytes = 128 * 1024 * 1024;

    /// <summary>
    /// Maximum UTF-8 size of the grid YAML inside the JSON envelope.
    /// JSON represents the byte array as base64, so this leaves bounded room
    /// for the remaining snapshot metadata while staying below
    /// <see cref="MaxPayloadBytes"/>.
    /// </summary>
    public const int MaxSnapshotPayloadBytes = 95 * 1024 * 1024;

    public const int MaxEntityCount = 131_072;
    public const int MaxVesselPrototypeIdLength = 128;
    public const int MaxShipNameLength = 256;
    public const int MaxShipNameSuffixLength = 128;
    public const int MaxBuildVersionLength = 128;
    public const int MaxServerInstanceIdLength = 128;
    public const int MaxReasonLength = 512;
}

public enum LuaMShipPersistenceWriteStatus
{
    Success,
    AlreadyProcessed,
    NotFound,
    OwnerNotFound,
    OwnerMismatch,
    RevisionConflict,
    InvalidState,
    LeaseConflict,
    InvalidRequest,
    Quarantined,
    Retired,
    UnknownOutcome,
}

public sealed record LuaMShipSnapshotStoreRequest(
    Guid ShipId,
    NetUserId OwnerUserId,
    long? ExpectedRevision,
    long PayloadRevision,
    Guid? LeaseId,
    string VesselPrototypeId,
    string ShipName,
    string? ShipNameSuffix,
    int PurchasePrice,
    bool PurchasedWithVoucher,
    int SourceRoundId,
    int SchemaVersion,
    int FormatVersion,
    byte[] Payload,
    string PayloadHash,
    int PayloadSizeBytes,
    int EntityCount,
    string SourceBuildVersion,
    string PrototypeManifestHash,
    DateTime StoredAtUtc);

public sealed record LuaMShipRestoreClaimRequest(
    Guid ShipId,
    NetUserId OwnerUserId,
    long ExpectedRevision,
    int RestoreRoundId,
    Guid LeaseId,
    string ServerInstanceId,
    DateTime ClaimedAtUtc,
    DateTime LeaseExpiresAtUtc);

public sealed record LuaMShipRestoreCompleteRequest(
    Guid ShipId,
    NetUserId OwnerUserId,
    long ExpectedRevision,
    Guid LeaseId,
    DateTime CompletedAtUtc);

public sealed record LuaMShipRestoreAbortRequest(
    Guid ShipId,
    NetUserId OwnerUserId,
    long ExpectedRevision,
    Guid LeaseId,
    string Reason,
    DateTime AbortedAtUtc);

/// <summary>
/// Releases an active presence lease without replacing the last durable
/// payload. This is the fail-safe used when a live grid disappears or cannot
/// be captured during irreversible round cleanup.
/// </summary>
public sealed record LuaMShipPresenceReleaseRequest(
    Guid ShipId,
    NetUserId OwnerUserId,
    long ExpectedRevision,
    Guid LeaseId,
    string Reason,
    DateTime ReleasedAtUtc);

public sealed record LuaMShipLeaseRenewRequest(
    Guid ShipId,
    NetUserId OwnerUserId,
    long ExpectedSnapshotRevision,
    Guid LeaseId,
    long ExpectedLeaseRevision,
    DateTime RenewedAtUtc,
    DateTime LeaseExpiresAtUtc);

public sealed record LuaMShipSnapshotQuarantineRequest(
    Guid ShipId,
    NetUserId OwnerUserId,
    long ExpectedRevision,
    Guid? LeaseId,
    string Reason,
    DateTime QuarantinedAtUtc);

/// <summary>
/// Repairs the metadata of a quarantined snapshot after its payload was proven
/// to restore successfully. The payload bytes themselves are never rewritten;
/// only the entity count, prototype manifest and quarantine marker change.
/// </summary>
public sealed record LuaMShipSnapshotRepairRequest(
    Guid ShipId,
    NetUserId OwnerUserId,
    long ExpectedRevision,
    int EntityCount,
    string PrototypeManifestHash,
    string Reason,
    DateTime RepairedAtUtc);

public sealed record LuaMShipSnapshotRetireRequest(
    Guid OperationId,
    Guid ShipId,
    NetUserId OwnerUserId,
    long ExpectedRevision,
    Guid? LeaseId,
    string Reason,
    DateTime RetiredAtUtc);

public sealed record LuaMShipSnapshotRecord(
    Guid ShipId,
    NetUserId OwnerUserId,
    long Revision,
    long? PayloadRevision,
    DbLuaMShipSnapshotStatus Status,
    string VesselPrototypeId,
    string ShipName,
    string? ShipNameSuffix,
    int PurchasePrice,
    bool PurchasedWithVoucher,
    int SchemaVersion,
    int FormatVersion,
    byte[] Payload,
    string PayloadHash,
    int PayloadSizeBytes,
    int EntityCount,
    string SourceBuildVersion,
    string PrototypeManifestHash,
    int SourceRoundId,
    int? LastRestoreRoundId,
    DateTime CreatedAtUtc,
    DateTime StoredAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? LastRestoredAtUtc,
    DateTime? QuarantinedAtUtc,
    string? QuarantineReason,
    DateTime? RetiredAtUtc,
    string? RetirementReason,
    Guid? RetirementOperationId,
    Guid? LeaseId,
    string? LeaseServerInstanceId,
    int? LeaseRoundId,
    long? LeaseRevision,
    DateTime? LeaseExpiresAtUtc,
    DateTime? LeaseRenewedAtUtc = null);

public sealed record LuaMShipRegistryRecord(
    Guid ShipId,
    NetUserId OwnerUserId,
    long Revision,
    long? PayloadRevision,
    DbLuaMShipSnapshotStatus Status,
    string VesselPrototypeId,
    string ShipName,
    string? ShipNameSuffix,
    int PurchasePrice,
    bool PurchasedWithVoucher,
    int SchemaVersion,
    int FormatVersion,
    string PayloadHash,
    int PayloadSizeBytes,
    int EntityCount,
    string SourceBuildVersion,
    string PrototypeManifestHash,
    int SourceRoundId,
    int? LastRestoreRoundId,
    DateTime CreatedAtUtc,
    DateTime StoredAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? LastRestoredAtUtc,
    DateTime? QuarantinedAtUtc,
    string? QuarantineReason,
    DateTime? RetiredAtUtc,
    string? RetirementReason,
    Guid? RetirementOperationId,
    Guid? LeaseId,
    string? LeaseServerInstanceId,
    int? LeaseRoundId,
    long? LeaseRevision,
    DateTime? LeaseExpiresAtUtc);

public sealed record LuaMShipPersistenceWriteResult(
    LuaMShipPersistenceWriteStatus Status,
    Guid? ShipId = null,
    long? Revision = null,
    DbLuaMShipSnapshotStatus? SnapshotStatus = null,
    Guid? LeaseId = null,
    long? LeaseRevision = null,
    LuaMShipSnapshotRecord? Snapshot = null)
{
    public bool Success => Status is LuaMShipPersistenceWriteStatus.Success or
        LuaMShipPersistenceWriteStatus.AlreadyProcessed;
}

public partial interface IServerDbManager
{
    Task<LuaMShipPersistenceWriteResult> StoreLuaMShipSnapshotAsync(
        LuaMShipSnapshotStoreRequest request,
        CancellationToken cancel = default);

    Task<LuaMShipSnapshotRecord?> GetLuaMShipSnapshotAsync(
        Guid shipId,
        NetUserId ownerUserId,
        CancellationToken cancel = default);

    Task<IReadOnlyList<LuaMShipRegistryRecord>> GetLuaMShipSnapshotsByOwnerAsync(
        NetUserId ownerUserId,
        bool includeRetired = false,
        CancellationToken cancel = default);

    Task<LuaMShipPersistenceWriteResult> ClaimLuaMShipRestoreAsync(
        LuaMShipRestoreClaimRequest request,
        CancellationToken cancel = default);

    Task<LuaMShipPersistenceWriteResult> CompleteLuaMShipRestoreAsync(
        LuaMShipRestoreCompleteRequest request,
        CancellationToken cancel = default);

    Task<LuaMShipPersistenceWriteResult> AbortLuaMShipRestoreAsync(
        LuaMShipRestoreAbortRequest request,
        CancellationToken cancel = default);

    Task<LuaMShipPersistenceWriteResult> ReleaseLuaMShipPresenceAsync(
        LuaMShipPresenceReleaseRequest request,
        CancellationToken cancel = default);

    Task<LuaMShipPersistenceWriteResult> RenewLuaMShipLeaseAsync(
        LuaMShipLeaseRenewRequest request,
        CancellationToken cancel = default);

    Task<LuaMShipPersistenceWriteResult> QuarantineLuaMShipSnapshotAsync(
        LuaMShipSnapshotQuarantineRequest request,
        CancellationToken cancel = default);

    Task<LuaMShipPersistenceWriteResult> RepairQuarantinedLuaMShipSnapshotAsync(
        LuaMShipSnapshotRepairRequest request,
        CancellationToken cancel = default);

    Task<LuaMShipPersistenceWriteResult> RetireLuaMShipSnapshotAsync(
        LuaMShipSnapshotRetireRequest request,
        CancellationToken cancel = default);

    Task<int> RecoverExpiredLuaMShipLeasesAsync(
        DateTime nowUtc,
        int maxCount = 100,
        CancellationToken cancel = default);
}
