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

public static class LuaMDeepCryoPublication
{
    public const string AuthorizationReason = "publication-authorized-v1";
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
    /// <summary>
    /// Another durable character lifecycle advanced after this mutation read
    /// its optimistic-concurrency token. Retrying the stale mutation is unsafe
    /// even when the winning snapshot has already become terminal.
    /// </summary>
    LifecycleConflict,
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
    DateTime StoredAtUtc,
    long ExpectedLifecycleRevision,
    Guid ExpectedPresenceLeaseId);

public sealed record LuaMCharacterPresenceAuthorityRecord(
    int ProfileId,
    long? SnapshotId,
    Guid LeaseId,
    DbLuaMCharacterPresencePhase Phase,
    string ServerInstanceId,
    int RoundId,
    DateTime AcquiredAtUtc,
    DateTime RenewedAtUtc,
    DateTime ExpiresAtUtc,
    long Revision,
    long AuthorityLifecycleRevision);

public sealed record LuaMCharacterPresenceReserveRequest(
    Guid OperationId,
    NetUserId UserId,
    int ProfileId,
    int Slot,
    Guid LeaseId,
    string ServerInstanceId,
    int RoundId,
    DateTime ReservedAtUtc,
    DateTime LeaseExpiresAtUtc,
    long ExpectedLifecycleRevision);

public sealed record LuaMCharacterPresencePublishRequest(
    Guid OperationId,
    NetUserId UserId,
    int ProfileId,
    int Slot,
    Guid LeaseId,
    long ExpectedLeaseRevision,
    long ExpectedLifecycleRevision,
    DateTime PublishedAtUtc,
    DateTime LeaseExpiresAtUtc);

public sealed record LuaMCharacterPresenceRenewRequest(
    Guid OperationId,
    NetUserId UserId,
    int ProfileId,
    int Slot,
    Guid LeaseId,
    long ExpectedLeaseRevision,
    long ExpectedAuthorityLifecycleRevision,
    DateTime RenewedAtUtc,
    DateTime LeaseExpiresAtUtc);

public sealed record LuaMCharacterPresenceReleaseRequest(
    Guid OperationId,
    NetUserId UserId,
    int ProfileId,
    int Slot,
    Guid LeaseId,
    DbLuaMCharacterPresencePhase ExpectedPhase,
    long? ExpectedSnapshotId,
    long ExpectedLeaseRevision,
    long ExpectedAuthorityLifecycleRevision,
    string Reason,
    DateTime ReleasedAtUtc);

public sealed record LuaMCharacterPresenceReclaimRequest(
    Guid OperationId,
    NetUserId UserId,
    int ProfileId,
    int Slot,
    Guid ExpectedLeaseId,
    Guid NewLeaseId,
    DbLuaMCharacterPresencePhase ExpectedPhase,
    long? ExpectedSnapshotId,
    long ExpectedLeaseRevision,
    long ExpectedAuthorityLifecycleRevision,
    string NewServerInstanceId,
    int NewRoundId,
    DateTime ReclaimedAtUtc,
    DateTime LeaseExpiresAtUtc);

/// <summary>
/// Immutable optimistic-concurrency token acquired before a live body begins
/// its Store mutation. An active snapshot, when present, is authoritative proof
/// that the body must not enter a second storage lifecycle.
/// </summary>
public sealed record LuaMDeepCryoStorePrecondition(
    long LifecycleRevision,
    LuaMDeepCryoSnapshotRecord? ActiveSnapshot,
    LuaMCharacterPresenceAuthorityRecord? Authority = null);

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
    DateTime LeaseExpiresAtUtc,
    long ExpectedLifecycleRevision);

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

/// <summary>
/// Reopens a snapshot whose restore was durably completed but whose body was
/// never published to the player. The completion operation and lease token bind
/// this compensation to the exact unpublished restore attempt.
/// </summary>
public sealed record LuaMDeepCryoRollbackPublicationRequest(
    Guid OperationId,
    Guid CompletionOperationId,
    NetUserId UserId,
    int ProfileId,
    int Slot,
    long SnapshotId,
    long ExpectedCompletedRevision,
    Guid LeaseId,
    string Reason,
    DateTime RolledBackAtUtc);

/// <summary>
/// Durably authorizes the transition from a definitely-unpublished PREPARE to
/// a state that may be physically exposed. It retains the exact lease.
/// </summary>
public sealed record LuaMDeepCryoAuthorizePublicationRequest(
    Guid OperationId,
    Guid CompletionOperationId,
    NetUserId UserId,
    int ProfileId,
    int Slot,
    long SnapshotId,
    long ExpectedPreparedRevision,
    Guid LeaseId,
    DateTime AuthorizedAtUtc);

/// <summary>
/// Finalizes a prepared publication after the restored body has been exposed.
/// The exact authorization proof and retained lease prevent an unrelated consumed
/// snapshot from being acknowledged.
/// </summary>
public sealed record LuaMDeepCryoAcknowledgePublicationRequest(
    Guid OperationId,
    Guid AuthorizationOperationId,
    NetUserId UserId,
    int ProfileId,
    int Slot,
    long SnapshotId,
    long ExpectedAuthorizedRevision,
    Guid LeaseId,
    DateTime AcknowledgedAtUtc,
    DateTime PlayableLeaseExpiresAtUtc);

public sealed record LuaMDeepCryoQuarantineAcknowledgedPublicationRequest(
    Guid OperationId,
    Guid AcknowledgementOperationId,
    NetUserId UserId,
    int ProfileId,
    int Slot,
    long SnapshotId,
    long ExpectedAcknowledgedRevision,
    Guid LeaseId,
    long ExpectedPresenceRevision,
    long ExpectedLifecycleRevision,
    string Reason,
    DateTime QuarantinedAtUtc);

/// <summary>
/// Fail-closed terminal transition for an authorized publication that could not
/// prove a safe live attachment. The payload remains available for operators.
/// </summary>
public sealed record LuaMDeepCryoQuarantineAuthorizedPublicationRequest(
    Guid OperationId,
    Guid AuthorizationOperationId,
    NetUserId UserId,
    int ProfileId,
    int Slot,
    long SnapshotId,
    long ExpectedAuthorizedRevision,
    Guid LeaseId,
    string Reason,
    DateTime QuarantinedAtUtc);

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
    DateTime? LeaseExpiresAtUtc,
    LuaMCharacterPresenceAuthorityRecord? Authority = null);

public sealed record LuaMDeepCryoWriteResult(
    LuaMDeepCryoWriteStatus Status,
    long? SnapshotId = null,
    long? Revision = null,
    DbLuaMDeepCryoSnapshotStatus? SnapshotStatus = null,
    Guid? LeaseId = null,
    LuaMDeepCryoSnapshotRecord? Snapshot = null,
    LuaMCharacterPresenceAuthorityRecord? Authority = null)
{
    public bool Success => Status is LuaMDeepCryoWriteStatus.Success or LuaMDeepCryoWriteStatus.AlreadyProcessed;
}

public sealed record LuaMCharacterPresenceWriteResult(
    LuaMDeepCryoWriteStatus Status,
    LuaMCharacterPresenceAuthorityRecord? Authority = null,
    long? LifecycleRevision = null,
    LuaMDeepCryoSnapshotRecord? Snapshot = null)
{
    public bool Success => Status is LuaMDeepCryoWriteStatus.Success or LuaMDeepCryoWriteStatus.AlreadyProcessed;
}

public partial interface IServerDbManager
{
    Task<LuaMCharacterPresenceAuthorityRecord?> GetLuaMCharacterPresenceAuthorityAsync(
        NetUserId userId,
        int profileId,
        int slot,
        CancellationToken cancel = default);

    Task<LuaMCharacterPresenceWriteResult> ReserveLuaMCharacterPresenceAsync(
        LuaMCharacterPresenceReserveRequest request,
        CancellationToken cancel = default);

    Task<LuaMCharacterPresenceWriteResult> PublishLuaMCharacterPresenceAsync(
        LuaMCharacterPresencePublishRequest request,
        CancellationToken cancel = default);

    Task<LuaMCharacterPresenceWriteResult> RenewLuaMCharacterPresenceAsync(
        LuaMCharacterPresenceRenewRequest request,
        CancellationToken cancel = default);

    Task<LuaMCharacterPresenceWriteResult> ReleaseLuaMCharacterPresenceAsync(
        LuaMCharacterPresenceReleaseRequest request,
        CancellationToken cancel = default);

    Task<LuaMCharacterPresenceWriteResult> ReclaimLuaMCharacterPresenceAsync(
        LuaMCharacterPresenceReclaimRequest request,
        CancellationToken cancel = default);

    Task<LuaMDeepCryoStorePrecondition?> GetLuaMDeepCryoStorePreconditionAsync(
        NetUserId userId,
        int profileId,
        int slot,
        CancellationToken cancel = default);

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

    Task<LuaMDeepCryoWriteResult> RollbackLuaMDeepCryoPublicationAsync(
        LuaMDeepCryoRollbackPublicationRequest request,
        CancellationToken cancel = default);

    Task<LuaMDeepCryoWriteResult> AuthorizeLuaMDeepCryoPublicationAsync(
        LuaMDeepCryoAuthorizePublicationRequest request,
        CancellationToken cancel = default);

    Task<LuaMDeepCryoWriteResult> AcknowledgeLuaMDeepCryoPublicationAsync(
        LuaMDeepCryoAcknowledgePublicationRequest request,
        CancellationToken cancel = default);

    Task<LuaMDeepCryoWriteResult> QuarantineAuthorizedLuaMDeepCryoPublicationAsync(
        LuaMDeepCryoQuarantineAuthorizedPublicationRequest request,
        CancellationToken cancel = default);

    Task<LuaMDeepCryoWriteResult> QuarantineAcknowledgedLuaMDeepCryoPublicationAsync(
        LuaMDeepCryoQuarantineAcknowledgedPublicationRequest request,
        CancellationToken cancel = default);

    Task<LuaMDeepCryoWriteResult> DiscardLuaMDeepCryoSnapshotAsync(
        LuaMDeepCryoDiscardRequest request,
        CancellationToken cancel = default);

    Task<LuaMDeepCryoWriteResult> QuarantineLuaMDeepCryoSnapshotAsync(
        LuaMDeepCryoQuarantineRequest request,
        CancellationToken cancel = default);

    Task<int> RecoverExpiredLuaMDeepCryoLeasesAsync(
        DateTime nowUtc,
        Guid[]? protectedLeaseIds = null,
        int maxCount = 100,
        CancellationToken cancel = default);

    Task<bool> ValidateLuaMDeepCryoProfileAsync(
        NetUserId userId,
        int profileId,
        int slot,
        CancellationToken cancel = default);
}
