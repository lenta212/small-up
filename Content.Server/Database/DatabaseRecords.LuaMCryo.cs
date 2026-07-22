using System;
using System.Threading;
using System.Threading.Tasks;
using Robust.Shared.Network;

namespace Content.Server.Database;

public static class LuaMDeepCryoLimits
{
    public const int MaxPayloadBytes = 16 * 1024 * 1024;
    public const int MaxEntityCount = 4096;
    public const int MaxBuildVersionLength = 128;
    public const int MaxServerInstanceIdLength = 128;
    public const int MaxReasonLength = 512;
}

public enum LuaMDeepCryoWriteStatus
{
    Success,
    AlreadyProcessed,
    NotFound,
    ProfileNotActive,
    IdentityConflict,
    RevisionConflict,
    InvalidState,
    LeaseConflict,
    InvalidRequest,
    Quarantined,
    UnknownOutcome,
}

public sealed record LuaMDeepCryoStoreRequest(
    Guid OperationId,
    NetUserId UserId,
    int ProfileId,
    int Slot,
    int SourceRoundId,
    int FormatVersion,
    byte[] Payload,
    string PayloadHash,
    int PayloadSizeBytes,
    int EntityCount,
    string SourceBuildVersion,
    string PrototypeManifestHash,
    DateTime StoredAtUtc);

public sealed record LuaMDeepCryoClaimRequest(
    Guid OperationId,
    NetUserId UserId,
    int ProfileId,
    int Slot,
    long SnapshotId,
    long ExpectedRevision,
    int RestoreRoundId,
    Guid LeaseId,
    string ServerInstanceId,
    DateTime ClaimedAtUtc,
    DateTime LeaseExpiresAtUtc);

public sealed record LuaMDeepCryoCompleteRequest(
    Guid OperationId,
    NetUserId UserId,
    int ProfileId,
    int Slot,
    long SnapshotId,
    long ExpectedRevision,
    Guid LeaseId,
    DateTime CompletedAtUtc);

public sealed record LuaMDeepCryoAbortRequest(
    Guid OperationId,
    NetUserId UserId,
    int ProfileId,
    int Slot,
    long SnapshotId,
    long ExpectedRevision,
    Guid LeaseId,
    string Reason,
    DateTime AbortedAtUtc);

public sealed record LuaMDeepCryoDiscardRequest(
    Guid OperationId,
    NetUserId UserId,
    int ProfileId,
    int Slot,
    long SnapshotId,
    long ExpectedRevision,
    Guid? LeaseId,
    DateTime DiscardedAtUtc);

public sealed record LuaMDeepCryoQuarantineRequest(
    Guid OperationId,
    NetUserId UserId,
    int ProfileId,
    int Slot,
    long SnapshotId,
    long ExpectedRevision,
    Guid? LeaseId,
    string Reason,
    DateTime QuarantinedAtUtc);

public sealed record LuaMDeepCryoSnapshotRecord(
    long Id,
    NetUserId UserId,
    int PreferenceId,
    int ProfileId,
    int Slot,
    int SourceRoundId,
    int? LastRestoreRoundId,
    DbLuaMDeepCryoSnapshotStatus Status,
    long Revision,
    int FormatVersion,
    byte[] Payload,
    string PayloadHash,
    int PayloadSizeBytes,
    int EntityCount,
    string SourceBuildVersion,
    string PrototypeManifestHash,
    DateTime StoredAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? ConsumedAtUtc,
    DateTime? QuarantinedAtUtc,
    string? QuarantineReason,
    Guid? LeaseId,
    DateTime? LeaseExpiresAtUtc);

public sealed record LuaMDeepCryoWriteResult(
    LuaMDeepCryoWriteStatus Status,
    long? SnapshotId = null,
    long? Revision = null,
    DbLuaMDeepCryoSnapshotStatus? SnapshotStatus = null,
    Guid? LeaseId = null,
    LuaMDeepCryoSnapshotRecord? Snapshot = null)
{
    public bool Success => Status is LuaMDeepCryoWriteStatus.Success or LuaMDeepCryoWriteStatus.AlreadyProcessed;
}

public partial interface IServerDbManager
{
    Task<LuaMDeepCryoWriteResult> StoreLuaMDeepCryoSnapshotAsync(
        LuaMDeepCryoStoreRequest request,
        CancellationToken cancel = default);

    Task<LuaMDeepCryoSnapshotRecord?> GetLuaMDeepCryoSnapshotAsync(
        NetUserId userId,
        int profileId,
        int slot,
        CancellationToken cancel = default);

    Task<LuaMDeepCryoWriteResult> ClaimLuaMDeepCryoRestoreAsync(
        LuaMDeepCryoClaimRequest request,
        CancellationToken cancel = default);

    Task<LuaMDeepCryoWriteResult> CompleteLuaMDeepCryoRestoreAsync(
        LuaMDeepCryoCompleteRequest request,
        CancellationToken cancel = default);

    Task<LuaMDeepCryoWriteResult> AbortLuaMDeepCryoRestoreAsync(
        LuaMDeepCryoAbortRequest request,
        CancellationToken cancel = default);

    Task<LuaMDeepCryoWriteResult> DiscardLuaMDeepCryoSnapshotAsync(
        LuaMDeepCryoDiscardRequest request,
        CancellationToken cancel = default);

    Task<LuaMDeepCryoWriteResult> QuarantineLuaMDeepCryoSnapshotAsync(
        LuaMDeepCryoQuarantineRequest request,
        CancellationToken cancel = default);

    Task<int> RecoverExpiredLuaMDeepCryoLeasesAsync(
        DateTime nowUtc,
        int maxCount = 100,
        CancellationToken cancel = default);

    Task<bool> ValidateLuaMDeepCryoProfileAsync(
        NetUserId userId,
        int profileId,
        int slot,
        CancellationToken cancel = default);
}
