using System;
using System.Threading;
using System.Threading.Tasks;
using Robust.Shared.Network;

namespace Content.Server.Database;

public enum MonoCoinsTransferStatus
{
    Success,
    InvalidAmount,
    SameAccount,
    SenderNotFound,
    RecipientNotFound,
    InsufficientFunds,
    RecipientOverflow,
    PendingOperation,
    OperationConflict,
    Conflict,
    UnknownOutcome,
    Blocked,
}

public readonly record struct MonoCoinsTransferResult(
    MonoCoinsTransferStatus Status,
    long SenderBalance = 0,
    long RecipientBalance = 0,
    Guid OperationId = default,
    bool AlreadyProcessed = false)
{
    public bool Success => Status == MonoCoinsTransferStatus.Success;

    public string Error => Status switch
    {
        MonoCoinsTransferStatus.Success => string.Empty,
        MonoCoinsTransferStatus.InvalidAmount => "amount-invalid",
        MonoCoinsTransferStatus.SameAccount => "same-account",
        MonoCoinsTransferStatus.SenderNotFound => "sender-no-account",
        MonoCoinsTransferStatus.RecipientNotFound => "recipient-not-found",
        MonoCoinsTransferStatus.InsufficientFunds => "insufficient-funds",
        MonoCoinsTransferStatus.RecipientOverflow => "recipient-overflow",
        MonoCoinsTransferStatus.PendingOperation => "transfer-pending",
        MonoCoinsTransferStatus.OperationConflict => "operation-conflict",
        MonoCoinsTransferStatus.UnknownOutcome => "outcome-unknown",
        MonoCoinsTransferStatus.Blocked => "account-blocked",
        _ => "conflict",
    };
}

public readonly record struct MonoCoinsTransferJournalRecord(
    Guid OperationId,
    NetUserId SenderUserId,
    NetUserId RecipientUserId,
    long Amount,
    long SenderBalanceBefore,
    long SenderBalanceAfter,
    long RecipientBalanceBefore,
    long RecipientBalanceAfter,
    DateTime CreatedAt,
    DateTime? AcknowledgedAt);

public partial interface IServerDbManager
{
    /// <summary>
    /// Atomically debits and credits two MonoCoins accounts and records the
    /// operation id in the same transaction. Reusing an operation id with the
    /// same request replays the committed result; changing the request conflicts.
    /// </summary>
    Task<MonoCoinsTransferResult> TransferMonoCoinsAsync(
        NetUserId senderUserId,
        NetUserId recipientUserId,
        long amount,
        Guid operationId,
        CancellationToken cancel = default);

    Task<MonoCoinsTransferJournalRecord?> GetUnacknowledgedMonoCoinsTransferAsync(
        NetUserId senderUserId,
        CancellationToken cancel = default);

    Task<bool> AcknowledgeMonoCoinsTransferAsync(
        NetUserId senderUserId,
        Guid operationId,
        CancellationToken cancel = default);
}
