using System;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Network;

namespace Content.Server.Database;

public abstract partial class ServerDbBase
{
    public async Task<MonoCoinsTransferResult> TransferMonoCoinsAsync(
        NetUserId senderUserId,
        NetUserId recipientUserId,
        long amount,
        Guid operationId,
        CancellationToken cancel = default)
    {
        if (operationId == Guid.Empty)
            return new MonoCoinsTransferResult(MonoCoinsTransferStatus.OperationConflict);

        if (amount <= 0L)
            return new MonoCoinsTransferResult(MonoCoinsTransferStatus.InvalidAmount, OperationId: operationId);

        if (senderUserId == recipientUserId)
            return new MonoCoinsTransferResult(MonoCoinsTransferStatus.SameAccount, OperationId: operationId);

        Exception? commitException = null;
        var commitCompleted = false;
        var senderBalanceBefore = 0L;
        var senderBalanceAfter = 0L;
        var recipientBalanceBefore = 0L;
        var recipientBalanceAfter = 0L;

        try
        {
            await using (var db = await GetDb(cancel))
            {
                try
                {
                    await using var transaction = await db.DbContext.Database.BeginTransactionAsync(cancel);

                    // Idempotency proof is authoritative even if an account was
                    // deleted after this transfer committed.
                    var recorded = await db.DbContext.MonoCoinsTransferJournal
                        .AsNoTracking()
                        .SingleOrDefaultAsync(entry => entry.OperationId == operationId, cancel);
                    if (recorded != null)
                    {
                        if (!MatchesRequest(recorded, senderUserId, recipientUserId, amount))
                        {
                            return new MonoCoinsTransferResult(
                                MonoCoinsTransferStatus.OperationConflict,
                                recorded.SenderBalanceAfter,
                                recorded.RecipientBalanceAfter,
                                recorded.OperationId,
                                true);
                        }

                        return await ReplayMonoCoinsTransferAsync(db.DbContext, recorded, cancel);
                    }

                    // A sender may have only one unacknowledged command outcome.
                    // This makes a retry after process death discover the original
                    // operation id instead of debiting again under a fresh id.
                    var pending = await db.DbContext.MonoCoinsTransferJournal
                        .AsNoTracking()
                        .Where(entry =>
                            entry.SenderUserId == senderUserId.UserId &&
                            entry.AcknowledgedAt == null)
                        .OrderBy(entry => entry.CreatedAt)
                        .FirstOrDefaultAsync(cancel);
                    if (pending != null)
                        return PendingMonoCoinsTransfer(pending);

                    var requestedUserIds = new[] { senderUserId.UserId, recipientUserId.UserId };
                    var accounts = await db.DbContext.Preference
                        .AsNoTracking()
                        .Where(preference => requestedUserIds.Contains(preference.UserId))
                        .Select(preference => new
                        {
                            preference.Id,
                            preference.UserId,
                            preference.MonoCoins,
                        })
                        .ToArrayAsync(cancel);

                    var sender = accounts.SingleOrDefault(account => account.UserId == senderUserId.UserId);
                    if (sender == null)
                    {
                        return new MonoCoinsTransferResult(
                            MonoCoinsTransferStatus.SenderNotFound,
                            OperationId: operationId);
                    }

                    var recipient = accounts.SingleOrDefault(account => account.UserId == recipientUserId.UserId);
                    if (recipient == null)
                    {
                        return new MonoCoinsTransferResult(
                            MonoCoinsTransferStatus.RecipientNotFound,
                            sender.MonoCoins,
                            OperationId: operationId);
                    }

                    senderBalanceBefore = sender.MonoCoins;
                    recipientBalanceBefore = recipient.MonoCoins;
                    if (senderBalanceBefore < amount)
                    {
                        return new MonoCoinsTransferResult(
                            MonoCoinsTransferStatus.InsufficientFunds,
                            senderBalanceBefore,
                            recipientBalanceBefore,
                            operationId);
                    }

                    if (recipientBalanceBefore > long.MaxValue - amount)
                    {
                        return new MonoCoinsTransferResult(
                            MonoCoinsTransferStatus.RecipientOverflow,
                            senderBalanceBefore,
                            recipientBalanceBefore,
                            operationId);
                    }

                    senderBalanceAfter = senderBalanceBefore - amount;
                    recipientBalanceAfter = recipientBalanceBefore + amount;

                    Task<int> DebitSenderAsync()
                    {
                        return db.DbContext.Preference
                            .Where(preference =>
                                preference.Id == sender.Id &&
                                preference.UserId == senderUserId.UserId &&
                                preference.MonoCoins == senderBalanceBefore)
                            .ExecuteUpdateAsync(
                                setters => setters.SetProperty(
                                    preference => preference.MonoCoins,
                                    senderBalanceAfter),
                                cancel);
                    }

                    Task<int> CreditRecipientAsync()
                    {
                        return db.DbContext.Preference
                            .Where(preference =>
                                preference.Id == recipient.Id &&
                                preference.UserId == recipientUserId.UserId &&
                                preference.MonoCoins == recipientBalanceBefore)
                            .ExecuteUpdateAsync(
                                setters => setters.SetProperty(
                                    preference => preference.MonoCoins,
                                    recipientBalanceAfter),
                                cancel);
                    }

                    // Stable row order prevents opposing transfers from deadlocking.
                    var senderFirst = sender.Id < recipient.Id;
                    var firstAffected = senderFirst
                        ? await DebitSenderAsync()
                        : await CreditRecipientAsync();
                    if (firstAffected != 1)
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                        return new MonoCoinsTransferResult(
                            MonoCoinsTransferStatus.Conflict,
                            senderBalanceBefore,
                            recipientBalanceBefore,
                            operationId);
                    }

                    var secondAffected = senderFirst
                        ? await CreditRecipientAsync()
                        : await DebitSenderAsync();
                    if (secondAffected != 1)
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                        return new MonoCoinsTransferResult(
                            MonoCoinsTransferStatus.Conflict,
                            senderBalanceBefore,
                            recipientBalanceBefore,
                            operationId);
                    }

                    db.DbContext.MonoCoinsTransferJournal.Add(new MonoCoinsTransferJournal
                    {
                        OperationId = operationId,
                        SenderUserId = senderUserId.UserId,
                        RecipientUserId = recipientUserId.UserId,
                        Amount = amount,
                        SenderBalanceBefore = senderBalanceBefore,
                        SenderBalanceAfter = senderBalanceAfter,
                        RecipientBalanceBefore = recipientBalanceBefore,
                        RecipientBalanceAfter = recipientBalanceAfter,
                        CreatedAt = DateTime.UtcNow,
                    });
                    await db.DbContext.SaveChangesAsync(cancel);

                    try
                    {
                        await transaction.CommitAsync(cancel);
                        commitCompleted = true;
                    }
                    catch (Exception exception)
                    {
                        // A COMMIT exception cannot classify the outcome. Dispose
                        // this context and resolve from the durable operation id.
                        commitException = exception;
                    }
                }
                catch (Exception exception) when (commitException != null || commitCompleted)
                {
                    _opsLog.Warning($"Atomic MonoCoins transfer cleanup failed after COMMIT: {exception.Message}");
                }
            }
        }
        catch (Exception exception) when (commitException != null || commitCompleted)
        {
            _opsLog.Warning($"Atomic MonoCoins transfer context cleanup failed after COMMIT: {exception.Message}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbUpdateException exception)
        {
            _opsLog.Warning($"Atomic MonoCoins transfer failed: {exception.Message}");
            return await ResolveMonoCoinsTransferOutcomeAsync(
                operationId,
                senderUserId,
                recipientUserId,
                amount);
        }
        catch (DbException exception)
        {
            _opsLog.Warning($"Atomic MonoCoins transfer failed: {exception.Message}");
            return await ResolveMonoCoinsTransferOutcomeAsync(
                operationId,
                senderUserId,
                recipientUserId,
                amount);
        }

        if (commitException != null)
            _opsLog.Warning($"Atomic MonoCoins transfer COMMIT outcome is uncertain: {commitException.Message}");

        if (commitCompleted)
        {
            return new MonoCoinsTransferResult(
                MonoCoinsTransferStatus.Success,
                senderBalanceAfter,
                recipientBalanceAfter,
                operationId);
        }

        if (commitException == null)
        {
            return new MonoCoinsTransferResult(
                MonoCoinsTransferStatus.UnknownOutcome,
                senderBalanceAfter,
                recipientBalanceAfter,
                operationId);
        }

        return await ResolveMonoCoinsTransferOutcomeAsync(
            operationId,
            senderUserId,
            recipientUserId,
            amount);
    }

    public async Task<MonoCoinsTransferJournalRecord?> GetUnacknowledgedMonoCoinsTransferAsync(
        NetUserId senderUserId,
        CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        var transfer = await db.DbContext.MonoCoinsTransferJournal
            .AsNoTracking()
            .Where(entry =>
                entry.SenderUserId == senderUserId.UserId &&
                entry.AcknowledgedAt == null)
            .OrderBy(entry => entry.CreatedAt)
            .FirstOrDefaultAsync(cancel);
        return transfer == null ? null : ToRecord(transfer);
    }

    public async Task<bool> AcknowledgeMonoCoinsTransferAsync(
        NetUserId senderUserId,
        Guid operationId,
        CancellationToken cancel = default)
    {
        if (operationId == Guid.Empty)
            return false;

        await using var db = await GetDb(cancel);
        var affected = await db.DbContext.MonoCoinsTransferJournal
            .Where(entry =>
                entry.OperationId == operationId &&
                entry.SenderUserId == senderUserId.UserId &&
                entry.AcknowledgedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(entry => entry.AcknowledgedAt, DateTime.UtcNow),
                cancel);
        if (affected == 1)
            return true;

        // A retry after an uncertain acknowledgement is idempotently successful.
        return await db.DbContext.MonoCoinsTransferJournal
            .AsNoTracking()
            .AnyAsync(entry =>
                    entry.OperationId == operationId &&
                    entry.SenderUserId == senderUserId.UserId &&
                    entry.AcknowledgedAt != null,
                cancel);
    }

    private async Task<MonoCoinsTransferResult> ResolveMonoCoinsTransferOutcomeAsync(
        Guid operationId,
        NetUserId senderUserId,
        NetUserId recipientUserId,
        long amount)
    {
        try
        {
            await using var db = await GetDb(CancellationToken.None);
            var recorded = await db.DbContext.MonoCoinsTransferJournal
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    entry => entry.OperationId == operationId,
                    CancellationToken.None);
            if (recorded != null)
            {
                if (!MatchesRequest(recorded, senderUserId, recipientUserId, amount))
                {
                    return new MonoCoinsTransferResult(
                        MonoCoinsTransferStatus.OperationConflict,
                        recorded.SenderBalanceAfter,
                        recorded.RecipientBalanceAfter,
                        recorded.OperationId,
                        true);
                }

                return await ReplayMonoCoinsTransferAsync(
                    db.DbContext,
                    recorded,
                    CancellationToken.None);
            }

            var pending = await db.DbContext.MonoCoinsTransferJournal
                .AsNoTracking()
                .Where(entry =>
                    entry.SenderUserId == senderUserId.UserId &&
                    entry.AcknowledgedAt == null)
                .OrderBy(entry => entry.CreatedAt)
                .FirstOrDefaultAsync(CancellationToken.None);
            return pending == null
                ? new MonoCoinsTransferResult(MonoCoinsTransferStatus.Conflict, OperationId: operationId)
                : PendingMonoCoinsTransfer(pending);
        }
        catch (Exception exception)
        {
            _opsLog.Warning($"Could not resolve atomic MonoCoins transfer outcome: {exception.Message}");
            return new MonoCoinsTransferResult(
                MonoCoinsTransferStatus.UnknownOutcome,
                OperationId: operationId);
        }
    }

    private static async Task<MonoCoinsTransferResult> ReplayMonoCoinsTransferAsync(
        ServerDbContext db,
        MonoCoinsTransferJournal recorded,
        CancellationToken cancel)
    {
        var requestedUserIds = new[] { recorded.SenderUserId, recorded.RecipientUserId };
        var balances = await db.Preference
            .AsNoTracking()
            .Where(preference => requestedUserIds.Contains(preference.UserId))
            .Select(preference => new { preference.UserId, preference.MonoCoins })
            .ToDictionaryAsync(preference => preference.UserId, preference => preference.MonoCoins, cancel);

        return new MonoCoinsTransferResult(
            MonoCoinsTransferStatus.Success,
            balances.GetValueOrDefault(recorded.SenderUserId, recorded.SenderBalanceAfter),
            balances.GetValueOrDefault(recorded.RecipientUserId, recorded.RecipientBalanceAfter),
            recorded.OperationId,
            true);
    }

    private static MonoCoinsTransferResult PendingMonoCoinsTransfer(MonoCoinsTransferJournal pending)
    {
        return new MonoCoinsTransferResult(
            MonoCoinsTransferStatus.PendingOperation,
            pending.SenderBalanceAfter,
            pending.RecipientBalanceAfter,
            pending.OperationId,
            true);
    }

    private static bool MatchesRequest(
        MonoCoinsTransferJournal recorded,
        NetUserId senderUserId,
        NetUserId recipientUserId,
        long amount)
    {
        return recorded.SenderUserId == senderUserId.UserId &&
               recorded.RecipientUserId == recipientUserId.UserId &&
               recorded.Amount == amount;
    }

    private static MonoCoinsTransferJournalRecord ToRecord(MonoCoinsTransferJournal transfer)
    {
        return new MonoCoinsTransferJournalRecord(
            transfer.OperationId,
            new NetUserId(transfer.SenderUserId),
            new NetUserId(transfer.RecipientUserId),
            transfer.Amount,
            transfer.SenderBalanceBefore,
            transfer.SenderBalanceAfter,
            transfer.RecipientBalanceBefore,
            transfer.RecipientBalanceAfter,
            transfer.CreatedAt,
            transfer.AcknowledgedAt);
    }
}
