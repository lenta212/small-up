using System;
using System.Collections.Generic;

namespace Content.Server.Database;

public sealed record LuaMExpeditionRegionWrite(
    int RegionX,
    int RegionY,
    int MinX,
    int MinY,
    int MaxX,
    int MaxY,
    string Biome,
    string? ScenarioTag,
    int DangerBudget,
    int EdgeFormatVersion,
    byte[] EdgePayload,
    string EdgePayloadHash);

public sealed record LuaMExpeditionSiteWrite(
    string SiteId,
    int Kind,
    string PrototypeId,
    string UniqueScope,
    int PositionX,
    int PositionY,
    int MinX,
    int MinY,
    int MaxX,
    int MaxY,
    int StateFormatVersion,
    byte[] StatePayload,
    string StatePayloadHash);

public sealed record LuaMExpeditionCreateRequest(
    string CampaignId,
    string ExpeditionId,
    ulong Seed,
    int GeneratorVersion,
    string PlanHash,
    int MinX,
    int MinY,
    int MaxX,
    int MaxY,
    DbLuaMExpeditionStatus Status,
    DbLuaMExpeditionPreservationPolicy PreservationPolicy,
    IReadOnlyList<LuaMExpeditionRegionWrite> Regions,
    IReadOnlyList<LuaMExpeditionSiteWrite> Sites,
    DateTime CreatedAtUtc);

public enum LuaMExpeditionWriteStatus
{
    Success,
    AlreadyProcessed,
    NotFound,
    IdentityConflict,
    RevisionConflict,
    Quarantined,
    InvalidRequest,
    UnknownOutcome,
}

public sealed record LuaMExpeditionWriteResult(
    LuaMExpeditionWriteStatus Status,
    long? ManifestId = null,
    long? RegionRevision = null,
    long? CheckpointId = null)
{
    public bool Success => Status is LuaMExpeditionWriteStatus.Success or LuaMExpeditionWriteStatus.AlreadyProcessed;
}

public sealed record LuaMExpeditionSnapshotWrite(
    string StableEntityId,
    string SourceInstanceId,
    string IdempotencyKey,
    int FormatVersion,
    byte[] Payload,
    string PayloadHash,
    int? ActorProfileId = null);

public sealed record LuaMExpeditionTombstoneWrite(
    string StableObjectId,
    string SourceInstanceId,
    int Kind,
    long? SiteRowId = null,
    int? ActorProfileId = null);

public sealed record LuaMExpeditionMutationRequest(
    long ManifestId,
    long RegionId,
    long ExpectedRegionRevision,
    string SourceInstanceId,
    int DeltaKind,
    int DeltaFormatVersion,
    byte[] DeltaPayload,
    string DeltaPayloadHash,
    int? ActorProfileId,
    IReadOnlyList<LuaMExpeditionSnapshotWrite> EntitySnapshots,
    IReadOnlyList<LuaMExpeditionTombstoneWrite> Tombstones,
    Guid CheckpointOperationId,
    int CheckpointFormatVersion,
    byte[] CheckpointPayload,
    string CheckpointPayloadHash,
    DateTime CreatedAtUtc);

public sealed record LuaMExpeditionManifestRecord(
    long Id,
    string CampaignId,
    string ExpeditionId,
    ulong Seed,
    int GeneratorVersion,
    string PlanHash,
    DbLuaMExpeditionStatus Status,
    DbLuaMExpeditionPreservationPolicy PreservationPolicy,
    long Revision,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? ArchivedAtUtc,
    string? QuarantineReason);

public sealed record LuaMExpeditionRegionRecord(
    long Id,
    long ManifestId,
    int RegionX,
    int RegionY,
    long Revision,
    DateTime? DiscoveredAtUtc,
    bool IsDepleted,
    DateTime? QuarantinedAtUtc,
    string? QuarantineReason);

public sealed record LuaMExpeditionSiteRecord(
    long Id,
    long ManifestId,
    string SiteId,
    int Kind,
    string PrototypeId,
    string UniqueScope,
    long Revision,
    byte[] StatePayload,
    int StateFormatVersion,
    DateTime? CompletedAtUtc);

public sealed record LuaMExpeditionDeltaRecord(
    long Id,
    long RegionId,
    long RegionRevision,
    string SourceInstanceId,
    int Kind,
    int FormatVersion,
    byte[] Payload,
    string PayloadHash,
    DateTime CreatedAtUtc);

public sealed record LuaMExpeditionEntitySnapshotRecord(
    long Id,
    long RegionId,
    string StableEntityId,
    long RegionRevision,
    int FormatVersion,
    byte[] Payload,
    string PayloadHash,
    DbLuaMExpeditionSnapshotStatus Status,
    string? QuarantineReason);

public sealed record LuaMExpeditionTombstoneRecord(
    long Id,
    long RegionId,
    string StableObjectId,
    string SourceInstanceId,
    int Kind,
    long RegionRevision,
    DateTime CreatedAtUtc);

public sealed record LuaMExpeditionCheckpointRecord(
    long Id,
    long ManifestId,
    Guid OperationId,
    string OperationIdentityKey,
    int FormatVersion,
    byte[] StatePayload,
    string PayloadHash,
    DbLuaMExpeditionCheckpointStatus Status,
    DateTime? CommittedAtUtc);

public sealed record LuaMExpeditionStateRecord(
    LuaMExpeditionManifestRecord Manifest,
    IReadOnlyList<LuaMExpeditionRegionRecord> Regions,
    IReadOnlyList<LuaMExpeditionSiteRecord> Sites,
    IReadOnlyList<LuaMExpeditionDeltaRecord> Deltas,
    IReadOnlyList<LuaMExpeditionEntitySnapshotRecord> EntitySnapshots,
    IReadOnlyList<LuaMExpeditionTombstoneRecord> Tombstones,
    LuaMExpeditionCheckpointRecord? LatestCheckpoint);

public sealed record LuaMCampaignShiftCreateRequest(
    long ShiftPeriodId,
    DateTime StartsAtUtc,
    DateTime EndsAtUtc,
    int? RoundId,
    DateTime CreatedAtUtc);

public enum LuaMProgressionWriteStatus
{
    Success,
    AlreadyProcessed,
    NotFound,
    IdentityConflict,
    RevisionConflict,
    ShiftNotOpen,
    ProfileNotActive,
    InvalidRequest,
    UnknownOutcome,
}

public sealed record LuaMProgressionWriteResult(
    LuaMProgressionWriteStatus Status,
    long? LedgerId = null,
    string? IdempotencyKey = null,
    long? CurrentRevision = null)
{
    public bool Success => Status is LuaMProgressionWriteStatus.Success or LuaMProgressionWriteStatus.AlreadyProcessed;
}

public sealed record LuaMCareerAwardRequest(
    long ShiftPeriodId,
    int ProfileId,
    int? RoundId,
    DbLuaMProgressionCurrency Currency,
    string? TargetId,
    int Amount,
    string SourceType,
    string SourceInstanceId,
    string AwardCode,
    long? ReversesLedgerId,
    int RulesetVersion,
    string PayloadJson,
    bool IsCareerFocus,
    int ActiveMinutesDelta,
    int ResultCareerXpDelta,
    int DistinctResultCategoriesDelta,
    bool MarkEligible,
    DateTime CreatedAtUtc);

public sealed record LuaMCareerRecord(
    int ProfileId,
    DbLuaMCharacterCareerStatus Status,
    long TotalCareerXp,
    int CreditedShiftCount,
    int Level,
    long Revision,
    DateTime UpdatedAtUtc);

public sealed record LuaMCareerParticipationRecord(
    long ShiftPeriodId,
    int ProfileId,
    int PreferenceId,
    int PreliminaryCareerXp,
    int FinalCareerXp,
    bool IsEligible,
    bool IsCredited,
    long Revision);

public sealed record LuaMCareerLedgerRecord(
    long Id,
    long ShiftPeriodId,
    int ProfileId,
    DbLuaMProgressionCurrency Currency,
    int Amount,
    string SourceType,
    string SourceInstanceId,
    string AwardCode,
    string IdempotencyKey,
    string OperationIdentityKey,
    long? ReversesLedgerId,
    DateTime CreatedAtUtc);

public sealed record LuaMCareerStateRecord(
    LuaMCareerRecord Career,
    IReadOnlyList<LuaMCareerParticipationRecord> Participations,
    IReadOnlyList<LuaMCareerLedgerRecord> Ledger);
