using System;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Content.Server._LuaM.Progression;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public abstract partial class ServerDbBase
{
    public async Task<LuaMProgressionWriteResult> CreateOrGetLuaMCampaignShiftAsync(
        LuaMCampaignShiftCreateRequest request,
        CancellationToken cancel = default)
    {
        if (request.StartsAtUtc.Kind != DateTimeKind.Utc ||
            request.EndsAtUtc.Kind != DateTimeKind.Utc ||
            request.CreatedAtUtc.Kind != DateTimeKind.Utc ||
            request.EndsAtUtc - request.StartsAtUtc != LuaMCampaignShiftClock.ShiftDuration ||
            request.EndsAtUtc <= request.StartsAtUtc ||
            request.RoundId is <= 0)
        {
            return new(LuaMProgressionWriteStatus.InvalidRequest);
        }

        try
        {
            await using var db = await GetDb(cancel);
            await using var transaction = await db.DbContext.Database.BeginTransactionAsync(cancel);
            var shift = await db.DbContext.LuaMCampaignShifts
                .SingleOrDefaultAsync(value => value.ShiftPeriodId == request.ShiftPeriodId, cancel);
            var created = shift == null;
            if (shift == null)
            {
                shift = new LuaMCampaignShift
                {
                    ShiftPeriodId = request.ShiftPeriodId,
                    StartsAtUtc = AsUtc(request.StartsAtUtc),
                    EndsAtUtc = AsUtc(request.EndsAtUtc),
                    Status = DbLuaMCampaignShiftStatus.Open,
                    Revision = 0,
                    CreatedAtUtc = AsUtc(request.CreatedAtUtc),
                };
                db.DbContext.LuaMCampaignShifts.Add(shift);
            }
            else if (shift.StartsAtUtc != AsUtc(request.StartsAtUtc) ||
                     shift.EndsAtUtc != AsUtc(request.EndsAtUtc))
            {
                return new(LuaMProgressionWriteStatus.IdentityConflict, CurrentRevision: shift.Revision);
            }

            var runCreated = false;
            if (request.RoundId is { } roundId)
            {
                var run = await db.DbContext.LuaMCampaignShiftRuns
                    .SingleOrDefaultAsync(value => value.RoundId == roundId, cancel);
                if (run != null && run.ShiftPeriodId != request.ShiftPeriodId)
                    return new(LuaMProgressionWriteStatus.IdentityConflict, CurrentRevision: shift.Revision);
                if (run == null)
                {
                    if (shift.Status != DbLuaMCampaignShiftStatus.Open)
                    {
                        return new(LuaMProgressionWriteStatus.ShiftNotOpen,
                            CurrentRevision: shift.Revision);
                    }

                    if (!created)
                    {
                        if (shift.Revision == long.MaxValue)
                        {
                            return new(LuaMProgressionWriteStatus.InvalidRequest,
                                CurrentRevision: shift.Revision);
                        }

                        // Serialize run attachment against sealing and other attachments.
                        // Both providers enforce Revision as a concurrency token.
                        shift.Revision++;
                    }

                    db.DbContext.LuaMCampaignShiftRuns.Add(new LuaMCampaignShiftRun
                    {
                        ShiftPeriodId = request.ShiftPeriodId,
                        RoundId = roundId,
                        AttachedAtUtc = AsUtc(request.CreatedAtUtc),
                    });
                    runCreated = true;
                }
            }

            await db.DbContext.SaveChangesAsync(cancel);
            await transaction.CommitAsync(cancel);
            return new(created || runCreated
                ? LuaMProgressionWriteStatus.Success
                : LuaMProgressionWriteStatus.AlreadyProcessed,
                CurrentRevision: shift.Revision);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            _opsLog.Warning($"LuaM campaign shift create outcome requires re-read: {exception.Message}");
            await using var db = await GetDb(CancellationToken.None);
            var shift = await db.DbContext.LuaMCampaignShifts.AsNoTracking()
                .SingleOrDefaultAsync(value => value.ShiftPeriodId == request.ShiftPeriodId);
            if (shift == null)
                return new(LuaMProgressionWriteStatus.UnknownOutcome);
            if (shift.StartsAtUtc != AsUtc(request.StartsAtUtc) || shift.EndsAtUtc != AsUtc(request.EndsAtUtc))
                return new(LuaMProgressionWriteStatus.IdentityConflict, CurrentRevision: shift.Revision);
            if (request.RoundId is { } roundId)
            {
                var mappedShift = await db.DbContext.LuaMCampaignShiftRuns.AsNoTracking()
                    .Where(value => value.RoundId == roundId)
                    .Select(value => (long?) value.ShiftPeriodId)
                    .SingleOrDefaultAsync();
                if (mappedShift != request.ShiftPeriodId)
                {
                    if (mappedShift == null && shift.Status != DbLuaMCampaignShiftStatus.Open)
                        return new(LuaMProgressionWriteStatus.ShiftNotOpen, CurrentRevision: shift.Revision);
                    return mappedShift == null
                        ? new(LuaMProgressionWriteStatus.UnknownOutcome, CurrentRevision: shift.Revision)
                        : new(LuaMProgressionWriteStatus.IdentityConflict, CurrentRevision: shift.Revision);
                }
            }
            return new(LuaMProgressionWriteStatus.AlreadyProcessed, CurrentRevision: shift.Revision);
        }
    }

    public async Task<LuaMProgressionWriteResult> RecordLuaMCareerAwardAsync(
        LuaMCareerAwardRequest request,
        CancellationToken cancel = default)
    {
        string idempotencyKey;
        try
        {
            idempotencyKey = new LuaMCareerAwardIdentity(
                new LuaMCampaignShiftId(request.ShiftPeriodId),
                request.ProfileId,
                request.SourceType,
                request.SourceInstanceId,
                request.AwardCode).CreateIdempotencyKey();
        }
        catch (ArgumentException)
        {
            return new(LuaMProgressionWriteStatus.InvalidRequest);
        }

        if (request.Amount == 0 || request.Amount == int.MinValue || request.RulesetVersion <= 0 ||
            !Enum.IsDefined(request.Currency) || request.RoundId is <= 0 ||
            request.CreatedAtUtc.Kind != DateTimeKind.Utc ||
            request.Amount < 0 && request.ReversesLedgerId == null ||
            request.Amount > 0 && request.ReversesLedgerId != null ||
            request.PayloadJson == null ||
            request.TargetId?.Length > 256 || request.ActiveMinutesDelta < 0 ||
            request.ResultCareerXpDelta < 0 || request.DistinctResultCategoriesDelta < 0)
        {
            return new(LuaMProgressionWriteStatus.InvalidRequest, IdempotencyKey: idempotencyKey);
        }

        var operationIdentityKey = CreateCareerAwardOperationIdentityKey(request, idempotencyKey);
        var replay = await FindLuaMLedgerAsync(idempotencyKey, cancel);
        if (replay != null)
            return AwardMatches(replay, operationIdentityKey)
                ? new(LuaMProgressionWriteStatus.AlreadyProcessed, replay.Id, idempotencyKey)
                : new(LuaMProgressionWriteStatus.IdentityConflict, replay.Id, idempotencyKey);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await using var db = await GetDb(cancel);
                await using var transaction = await db.DbContext.Database.BeginTransactionAsync(cancel);

                replay = await db.DbContext.LuaMCareerXpLedger.AsNoTracking()
                    .SingleOrDefaultAsync(value => value.IdempotencyKey == idempotencyKey, cancel);
                if (replay != null)
                    return AwardMatches(replay, operationIdentityKey)
                        ? new(LuaMProgressionWriteStatus.AlreadyProcessed, replay.Id, idempotencyKey)
                        : new(LuaMProgressionWriteStatus.IdentityConflict, replay.Id, idempotencyKey);

                var shift = await db.DbContext.LuaMCampaignShifts
                    .SingleOrDefaultAsync(value => value.ShiftPeriodId == request.ShiftPeriodId, cancel);
                if (shift == null)
                    return new(LuaMProgressionWriteStatus.NotFound, IdempotencyKey: idempotencyKey);
                if (shift.Status != DbLuaMCampaignShiftStatus.Open)
                    return new(LuaMProgressionWriteStatus.ShiftNotOpen, IdempotencyKey: idempotencyKey,
                        CurrentRevision: shift.Revision);

                var profile = await db.DbContext.Profile.AsNoTracking()
                    .SingleOrDefaultAsync(value => value.Id == request.ProfileId &&
                                                   !value.IsArchived && value.Slot.HasValue, cancel);
                if (profile == null)
                    return new(LuaMProgressionWriteStatus.ProfileNotActive, IdempotencyKey: idempotencyKey);

                if (request.RoundId is { } roundId)
                {
                    var mapped = await db.DbContext.LuaMCampaignShiftRuns.AsNoTracking()
                        .Where(value => value.RoundId == roundId)
                        .Select(value => (long?) value.ShiftPeriodId)
                        .SingleOrDefaultAsync(cancel);
                    if (mapped != request.ShiftPeriodId)
                        return new(LuaMProgressionWriteStatus.IdentityConflict, IdempotencyKey: idempotencyKey);
                }

                var now = AsUtc(request.CreatedAtUtc);
                var career = await db.DbContext.LuaMCharacterCareers
                    .SingleOrDefaultAsync(value => value.ProfileId == request.ProfileId, cancel);
                if (career == null)
                {
                    career = new LuaMCharacterCareer
                    {
                        ProfileId = request.ProfileId,
                        Status = DbLuaMCharacterCareerStatus.Playable,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                    };
                    db.DbContext.LuaMCharacterCareers.Add(career);
                }
                else if (career.Status != DbLuaMCharacterCareerStatus.Playable)
                {
                    return new(LuaMProgressionWriteStatus.ProfileNotActive, IdempotencyKey: idempotencyKey,
                        CurrentRevision: career.Revision);
                }

                var participation = await db.DbContext.LuaMCareerShiftParticipations
                    .SingleOrDefaultAsync(value => value.ShiftPeriodId == request.ShiftPeriodId &&
                                                   value.ProfileId == request.ProfileId, cancel);
                if (participation == null)
                {
                    participation = new LuaMCareerShiftParticipation
                    {
                        ShiftPeriodId = request.ShiftPeriodId,
                        ProfileId = request.ProfileId,
                        PreferenceId = profile.PreferenceId,
                        IsCareerFocus = request.IsCareerFocus,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                    };
                    db.DbContext.LuaMCareerShiftParticipations.Add(participation);
                }
                else if (participation.PreferenceId != profile.PreferenceId)
                {
                    return new(LuaMProgressionWriteStatus.IdentityConflict, IdempotencyKey: idempotencyKey,
                        CurrentRevision: participation.Revision);
                }

                LuaMCareerXpLedger? reversed = null;
                if (request.ReversesLedgerId is { } reversedId)
                {
                    reversed = await db.DbContext.LuaMCareerXpLedger.AsNoTracking()
                        .SingleOrDefaultAsync(value => value.Id == reversedId, cancel);
                    if (reversed == null || reversed.ProfileId != request.ProfileId ||
                        reversed.ShiftPeriodId != request.ShiftPeriodId || reversed.Amount <= 0 ||
                        reversed.ReversesLedgerId != null ||
                        reversed.Currency != request.Currency || reversed.TargetId != request.TargetId ||
                        (long) reversed.Amount + request.Amount != 0)
                    {
                        return new(LuaMProgressionWriteStatus.IdentityConflict, IdempotencyKey: idempotencyKey);
                    }

                    var alreadyReversed = await db.DbContext.LuaMCareerXpLedger.AsNoTracking()
                        .AnyAsync(value => value.ReversesLedgerId == reversedId, cancel);
                    if (alreadyReversed)
                        return new(LuaMProgressionWriteStatus.IdentityConflict, IdempotencyKey: idempotencyKey);
                }

                var nextPreliminaryCareerXp = participation.PreliminaryCareerXp;
                if (request.Currency == DbLuaMProgressionCurrency.Career)
                {
                    var candidate = (long) participation.PreliminaryCareerXp + request.Amount;
                    if (candidate is < 0 or > int.MaxValue)
                        return new(LuaMProgressionWriteStatus.InvalidRequest, IdempotencyKey: idempotencyKey);
                    nextPreliminaryCareerXp = (int) candidate;
                }

                var nextActiveMinutes = (long) participation.ActiveMinutes + request.ActiveMinutesDelta;
                var nextResultCareerXp = (long) participation.ResultCareerXp + request.ResultCareerXpDelta;
                var nextDistinctCategories = (long) participation.DistinctResultCategories +
                                             request.DistinctResultCategoriesDelta;
                if (nextActiveMinutes > int.MaxValue || nextResultCareerXp > int.MaxValue ||
                    nextDistinctCategories > int.MaxValue || participation.Revision == long.MaxValue ||
                    career.Revision == long.MaxValue || shift.Revision == long.MaxValue)
                {
                    return new(LuaMProgressionWriteStatus.InvalidRequest, IdempotencyKey: idempotencyKey);
                }

                var ledger = new LuaMCareerXpLedger
                {
                    ShiftPeriodId = request.ShiftPeriodId,
                    ProfileId = request.ProfileId,
                    RoundId = request.RoundId,
                    Currency = request.Currency,
                    TargetId = request.TargetId,
                    Amount = request.Amount,
                    SourceType = request.SourceType,
                    SourceInstanceId = request.SourceInstanceId,
                    AwardCode = request.AwardCode,
                    IdempotencyKey = idempotencyKey,
                    OperationIdentityKey = operationIdentityKey,
                    ReversesLedgerId = request.ReversesLedgerId,
                    RulesetVersion = request.RulesetVersion,
                    PayloadJson = request.PayloadJson,
                    CreatedAtUtc = now,
                };
                db.DbContext.LuaMCareerXpLedger.Add(ledger);

                participation.PreliminaryCareerXp = nextPreliminaryCareerXp;
                participation.ActiveMinutes = (int) nextActiveMinutes;
                participation.ResultCareerXp = (int) nextResultCareerXp;
                participation.DistinctResultCategories = (int) nextDistinctCategories;
                participation.IsCareerFocus |= request.IsCareerFocus;
                participation.IsEligible |= request.MarkEligible;
                participation.Revision++;
                participation.UpdatedAtUtc = now;
                career.Revision++;
                career.UpdatedAtUtc = now;
                shift.Revision++;

                await db.DbContext.SaveChangesAsync(cancel);
                await transaction.CommitAsync(cancel);
                return new(LuaMProgressionWriteStatus.Success, ledger.Id, idempotencyKey,
                    shift.Revision);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 2)
            {
                replay = await FindLuaMLedgerAsync(idempotencyKey, CancellationToken.None);
                if (replay != null)
                    return AwardMatches(replay, operationIdentityKey)
                        ? new(LuaMProgressionWriteStatus.AlreadyProcessed, replay.Id, idempotencyKey)
                        : new(LuaMProgressionWriteStatus.IdentityConflict, replay.Id, idempotencyKey);
            }
            catch (Exception exception) when (exception is DbUpdateException or DbException)
            {
                _opsLog.Warning($"LuaM career award outcome requires re-read: {exception.Message}");
                replay = await FindLuaMLedgerAsync(idempotencyKey, CancellationToken.None);
                if (replay != null)
                    return AwardMatches(replay, operationIdentityKey)
                        ? new(LuaMProgressionWriteStatus.AlreadyProcessed, replay.Id, idempotencyKey)
                        : new(LuaMProgressionWriteStatus.IdentityConflict, replay.Id, idempotencyKey);
                return exception is DbUpdateConcurrencyException
                    ? new(LuaMProgressionWriteStatus.RevisionConflict, IdempotencyKey: idempotencyKey)
                    : new(LuaMProgressionWriteStatus.UnknownOutcome, IdempotencyKey: idempotencyKey);
            }
        }

        return new(LuaMProgressionWriteStatus.RevisionConflict, IdempotencyKey: idempotencyKey);
    }

    public async Task<LuaMProgressionWriteResult> SealLuaMCampaignShiftAsync(
        long shiftPeriodId,
        long expectedRevision,
        DateTime sealedAtUtc,
        CancellationToken cancel = default)
    {
        if (expectedRevision < 0 || sealedAtUtc.Kind != DateTimeKind.Utc)
            return new(LuaMProgressionWriteStatus.InvalidRequest);

        try
        {
            await using var db = await GetDb(cancel);
            await using var transaction = await db.DbContext.Database.BeginTransactionAsync(cancel);
            var shift = await db.DbContext.LuaMCampaignShifts
                .SingleOrDefaultAsync(value => value.ShiftPeriodId == shiftPeriodId, cancel);
            if (shift == null)
                return new(LuaMProgressionWriteStatus.NotFound);
            if (shift.Status == DbLuaMCampaignShiftStatus.Closed)
                return new(LuaMProgressionWriteStatus.AlreadyProcessed, CurrentRevision: shift.Revision);
            if (shift.Status is DbLuaMCampaignShiftStatus.Aborted or DbLuaMCampaignShiftStatus.Sealing)
                return new(LuaMProgressionWriteStatus.ShiftNotOpen, CurrentRevision: shift.Revision);
            if (shift.Revision != expectedRevision)
                return new(LuaMProgressionWriteStatus.RevisionConflict, CurrentRevision: shift.Revision);

            var participations = await db.DbContext.LuaMCareerShiftParticipations
                .Where(value => value.ShiftPeriodId == shiftPeriodId)
                .ToArrayAsync(cancel);
            var careerAmounts = await db.DbContext.LuaMCareerXpLedger.AsNoTracking()
                .Where(value => value.ShiftPeriodId == shiftPeriodId &&
                                value.Currency == DbLuaMProgressionCurrency.Career)
                .GroupBy(value => value.ProfileId)
                .Select(group => new { ProfileId = group.Key, Amount = group.Sum(value => value.Amount) })
                .ToDictionaryAsync(value => value.ProfileId, value => value.Amount, cancel);
            var profileIds = participations.Select(value => value.ProfileId).ToArray();
            var careers = await db.DbContext.LuaMCharacterCareers
                .Where(value => profileIds.Contains(value.ProfileId))
                .ToDictionaryAsync(value => value.ProfileId, cancel);
            var now = sealedAtUtc;

            if (shift.Revision == long.MaxValue ||
                participations.Any(value => value.Revision == long.MaxValue))
            {
                return new(LuaMProgressionWriteStatus.InvalidRequest, CurrentRevision: shift.Revision);
            }

            foreach (var participation in participations)
            {
                var raw = careerAmounts.GetValueOrDefault(participation.ProfileId);
                participation.FinalCareerXp = LuaMCareerProgressionRules.ClampShiftXp(raw);
                participation.IsCredited = participation.IsCareerFocus && participation.IsEligible;
                participation.SealedAtUtc = now;
                participation.UpdatedAtUtc = now;
                participation.Revision = checked(participation.Revision + 1);

                if (!participation.IsCredited)
                    continue;
                if (!careers.TryGetValue(participation.ProfileId, out var career))
                    return new(LuaMProgressionWriteStatus.IdentityConflict);
                if (career.Status != DbLuaMCharacterCareerStatus.Playable)
                    return new(LuaMProgressionWriteStatus.ProfileNotActive, CurrentRevision: career.Revision);
                if (career.Revision == long.MaxValue || career.CreditedShiftCount == int.MaxValue ||
                    career.TotalCareerXp > long.MaxValue - participation.FinalCareerXp)
                {
                    return new(LuaMProgressionWriteStatus.InvalidRequest, CurrentRevision: career.Revision);
                }

                career.TotalCareerXp += participation.FinalCareerXp;
                career.CreditedShiftCount++;
                career.Level = LuaMCareerProgressionRules.CalculateLevel(
                    career.TotalCareerXp,
                    career.CreditedShiftCount);
                career.Revision++;
                career.UpdatedAtUtc = now;
            }

            shift.Status = DbLuaMCampaignShiftStatus.Closed;
            shift.SealedAtUtc = now;
            shift.Revision++;
            await db.DbContext.SaveChangesAsync(cancel);
            await transaction.CommitAsync(cancel);
            return new(LuaMProgressionWriteStatus.Success, CurrentRevision: shift.Revision);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            await using var db = await GetDb(CancellationToken.None);
            var shift = await db.DbContext.LuaMCampaignShifts.AsNoTracking()
                .SingleOrDefaultAsync(value => value.ShiftPeriodId == shiftPeriodId);
            if (shift?.Status == DbLuaMCampaignShiftStatus.Closed)
                return new(LuaMProgressionWriteStatus.AlreadyProcessed, CurrentRevision: shift.Revision);
            return new(LuaMProgressionWriteStatus.RevisionConflict, CurrentRevision: shift?.Revision);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            _opsLog.Warning($"LuaM campaign shift seal outcome requires re-read: {exception.Message}");
            await using var db = await GetDb(CancellationToken.None);
            var shift = await db.DbContext.LuaMCampaignShifts.AsNoTracking()
                .SingleOrDefaultAsync(value => value.ShiftPeriodId == shiftPeriodId);
            return shift?.Status == DbLuaMCampaignShiftStatus.Closed
                ? new(LuaMProgressionWriteStatus.AlreadyProcessed, CurrentRevision: shift.Revision)
                : new(LuaMProgressionWriteStatus.UnknownOutcome, CurrentRevision: shift?.Revision);
        }
    }

    public async Task<LuaMCareerStateRecord?> GetLuaMCareerStateAsync(
        int profileId,
        CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        var career = await db.DbContext.LuaMCharacterCareers.AsNoTracking()
            .SingleOrDefaultAsync(value => value.ProfileId == profileId, cancel);
        if (career == null)
            return null;
        var participations = await db.DbContext.LuaMCareerShiftParticipations.AsNoTracking()
            .Where(value => value.ProfileId == profileId)
            .OrderBy(value => value.ShiftPeriodId)
            .ToArrayAsync(cancel);
        var ledger = await db.DbContext.LuaMCareerXpLedger.AsNoTracking()
            .Where(value => value.ProfileId == profileId)
            .OrderBy(value => value.Id)
            .ToArrayAsync(cancel);

        return new LuaMCareerStateRecord(
            new LuaMCareerRecord(career.ProfileId, career.Status, career.TotalCareerXp,
                career.CreditedShiftCount, career.Level, career.Revision, career.UpdatedAtUtc),
            participations.Select(value => new LuaMCareerParticipationRecord(
                value.ShiftPeriodId, value.ProfileId, value.PreferenceId, value.PreliminaryCareerXp,
                value.FinalCareerXp, value.IsEligible, value.IsCredited, value.Revision)).ToArray(),
            ledger.Select(value => new LuaMCareerLedgerRecord(
                value.Id, value.ShiftPeriodId, value.ProfileId, value.Currency, value.Amount,
                value.SourceType, value.SourceInstanceId, value.AwardCode, value.IdempotencyKey,
                value.OperationIdentityKey, value.ReversesLedgerId, value.CreatedAtUtc)).ToArray());
    }

    private async Task<LuaMCareerXpLedger?> FindLuaMLedgerAsync(string idempotencyKey, CancellationToken cancel)
    {
        await using var db = await GetDb(cancel);
        return await db.DbContext.LuaMCareerXpLedger.AsNoTracking()
            .SingleOrDefaultAsync(value => value.IdempotencyKey == idempotencyKey, cancel);
    }

    private static bool AwardMatches(LuaMCareerXpLedger ledger, string operationIdentityKey)
        => ledger.OperationIdentityKey == operationIdentityKey;

    private static string CreateCareerAwardOperationIdentityKey(
        LuaMCareerAwardRequest request,
        string idempotencyKey)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(idempotencyKey);
            writer.Write(request.ShiftPeriodId);
            writer.Write(request.ProfileId);
            WriteNullableInt(writer, request.RoundId);
            writer.Write((int) request.Currency);
            writer.Write(request.TargetId != null);
            if (request.TargetId != null)
                writer.Write(request.TargetId);
            writer.Write(request.Amount);
            writer.Write(request.SourceType);
            writer.Write(request.SourceInstanceId);
            writer.Write(request.AwardCode);
            WriteNullableLong(writer, request.ReversesLedgerId);
            writer.Write(request.RulesetVersion);
            writer.Write(request.PayloadJson);
            writer.Write(request.IsCareerFocus);
            writer.Write(request.ActiveMinutesDelta);
            writer.Write(request.ResultCareerXpDelta);
            writer.Write(request.DistinctResultCategoriesDelta);
            writer.Write(request.MarkEligible);
            writer.Write(request.CreatedAtUtc.Ticks);
        }

        return Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int) buffer.Length))));
    }
}
