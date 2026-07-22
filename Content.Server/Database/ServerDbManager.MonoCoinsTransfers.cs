using System;
using System.Threading;
using System.Threading.Tasks;
using Robust.Shared.Network;

namespace Content.Server.Database;

public sealed partial class ServerDbManager
{
    public Task<MonoCoinsTransferResult> TransferMonoCoinsAsync(
        NetUserId senderUserId,
        NetUserId recipientUserId,
        long amount,
        Guid operationId,
        CancellationToken cancel = default)
    {
        return RunDbCommand(() => _db.TransferMonoCoinsAsync(
            senderUserId,
            recipientUserId,
            amount,
            operationId,
            cancel));
    }

    public Task<MonoCoinsTransferJournalRecord?> GetUnacknowledgedMonoCoinsTransferAsync(
        NetUserId senderUserId,
        CancellationToken cancel = default)
    {
        return RunDbCommand(() => _db.GetUnacknowledgedMonoCoinsTransferAsync(senderUserId, cancel));
    }

    public Task<bool> AcknowledgeMonoCoinsTransferAsync(
        NetUserId senderUserId,
        Guid operationId,
        CancellationToken cancel = default)
    {
        return RunDbCommand(() => _db.AcknowledgeMonoCoinsTransferAsync(
            senderUserId,
            operationId,
            cancel));
    }
}
