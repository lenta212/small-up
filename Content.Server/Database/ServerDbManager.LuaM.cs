using System;
using System.Threading;
using System.Threading.Tasks;

namespace Content.Server.Database;

public sealed partial class ServerDbManager
{
    public Task<LuaMExpeditionWriteResult> CreateOrGetLuaMExpeditionAsync(
        LuaMExpeditionCreateRequest request,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.CreateOrGetLuaMExpeditionAsync(request, cancel));

    public Task<LuaMExpeditionStateRecord?> LoadLuaMExpeditionAsync(
        string campaignId,
        string expeditionId,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.LoadLuaMExpeditionAsync(campaignId, expeditionId, cancel));

    public Task<LuaMExpeditionWriteResult> AppendLuaMExpeditionMutationAsync(
        LuaMExpeditionMutationRequest request,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.AppendLuaMExpeditionMutationAsync(request, cancel));

    public Task<LuaMExpeditionWriteResult> ArchiveLuaMExpeditionAsync(
        long manifestId,
        long expectedRevision,
        DateTime archivedAtUtc,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.ArchiveLuaMExpeditionAsync(manifestId, expectedRevision, archivedAtUtc, cancel));

    public Task<LuaMExpeditionWriteResult> QuarantineLuaMExpeditionAsync(
        long manifestId,
        long expectedRevision,
        string reason,
        DateTime quarantinedAtUtc,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.QuarantineLuaMExpeditionAsync(
            manifestId,
            expectedRevision,
            reason,
            quarantinedAtUtc,
            cancel));

    public Task<LuaMProgressionWriteResult> CreateOrGetLuaMCampaignShiftAsync(
        LuaMCampaignShiftCreateRequest request,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.CreateOrGetLuaMCampaignShiftAsync(request, cancel));

    public Task<LuaMProgressionWriteResult> RecordLuaMCareerAwardAsync(
        LuaMCareerAwardRequest request,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.RecordLuaMCareerAwardAsync(request, cancel));

    public Task<LuaMProgressionWriteResult> SealLuaMCampaignShiftAsync(
        long shiftPeriodId,
        long expectedRevision,
        DateTime sealedAtUtc,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.SealLuaMCampaignShiftAsync(shiftPeriodId, expectedRevision, sealedAtUtc, cancel));

    public Task<LuaMCareerStateRecord?> GetLuaMCareerStateAsync(
        int profileId,
        CancellationToken cancel = default)
        => RunDbCommand(() => _db.GetLuaMCareerStateAsync(profileId, cancel));
}
