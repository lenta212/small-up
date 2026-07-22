using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared._Mono.MonoCoins;
using Robust.Server.Player;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server._Mono.MonoCoins;

public sealed class MonoCoinsMutationRejectedException : InvalidOperationException
{
    public MonoCoinsBalanceUpdateResult Result { get; }

    public MonoCoinsMutationRejectedException(NetUserId userId, MonoCoinsBalanceUpdateResult result)
        : base(
            $"MonoCoins mutation was rejected for {userId}: {result.Status} " +
            $"(current: {result.CurrentBalance?.ToString() ?? "missing"}).",
            result.Failure)
    {
        Result = result;
    }
}

/// <summary>
/// System that handles MonoCoins balance for players.
/// </summary>
public sealed partial class MonoCoinsManager
{
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IServerDbManager _db = default!;
    [Dependency] private INetManager _net = default!;

    private readonly ConcurrentDictionary<NetUserId, long> _cachedBalance = new();
    private readonly ConcurrentDictionary<NetUserId, SemaphoreSlim> _mutationGates = new();
    private readonly ConcurrentDictionary<NetUserId, string> _blockedMutations = new();

    public void Initialize()
    {
        _net.RegisterNetMessage<MsgMonoCoins>();
        _net.RegisterNetMessage<MsgMonoCoinsRequest>(OnRequestBalance);
        _net.Connected += OnConnected;
    }

    private async void OnRequestBalance(MsgMonoCoinsRequest msg)
    {
        await SendBalanceAsync(msg.MsgChannel);
    }

    private async void OnConnected(object? sender, NetChannelArgs e)
    {
        await SendBalanceAsync(e.Channel);
    }

    private async Task SendBalanceAsync(INetChannel player)
    {
        var balance = await GetMonoCoinsBalanceAsync(player.UserId);
        SendBalance(player, balance);
    }

    private void SendBalance(INetChannel player, long balance)
    {
        try
        {
            _net.ServerSendMessage(new MsgMonoCoins { Coins = balance }, player);
        }
        catch (Exception exception)
        {
            // Runtime notification failures cannot change the authoritative
            // classification of a database commit.
            Logger.Error($"Could not send MonoCoins balance to {player.UserId}: {exception}");
        }
    }

    /// <summary>
    /// Gets the MonoCoins balance from a cache populated only by authoritative
    /// database reads and confirmed writes.
    /// </summary>
    public async Task<long> GetMonoCoinsBalanceAsync(NetUserId userId)
    {
        if (_cachedBalance.TryGetValue(userId, out var cached))
            return cached;

        var gate = GetMutationGate(userId);
        await gate.WaitAsync();
        try
        {
            return await GetMonoCoinsBalanceLockedAsync(userId);
        }
        finally
        {
            gate.Release();
        }
    }

    public long? GetMonoCoinsBalance(NetUserId userId)
    {
        return _cachedBalance.TryGetValue(userId, out var balance)
            ? balance
            : null;
    }

    /// <summary>
    /// Conditionally commits an exact expected balance. The result is never
    /// retried against a different balance, making this suitable for composite
    /// bank operations and their compensations.
    /// </summary>
    public async Task<MonoCoinsBalanceUpdateResult> UpdateMonoCoinsBalanceExactAsync(
        NetUserId userId,
        long expectedBalance,
        long newBalance)
    {
        var gate = GetMutationGate(userId);
        await gate.WaitAsync();
        try
        {
            if (_blockedMutations.TryGetValue(userId, out var reason))
            {
                return new MonoCoinsBalanceUpdateResult(
                    MonoCoinsBalanceUpdateStatus.Blocked,
                    _cachedBalance.TryGetValue(userId, out var blockedBalance) ? blockedBalance : null,
                    new InvalidOperationException(reason));
            }

            return await UpdateMonoCoinsBalanceLockedAsync(userId, expectedBalance, newBalance);
        }
        finally
        {
            gate.Release();
        }
    }

    public bool IsMonoCoinsMutationBlocked(NetUserId userId)
    {
        return _blockedMutations.ContainsKey(userId);
    }

    /// <summary>
    /// Stops all further writes for a user after an unknown composite outcome.
    /// Reads remain available so an operator can inspect and reconcile the value.
    /// </summary>
    public void BlockMonoCoinsMutations(NetUserId userId, string reason, Exception? failure = null)
    {
        var detail = failure == null ? reason : $"{reason}: {failure}";
        _blockedMutations.TryAdd(userId, detail);
    }

    /// <summary>
    /// Explicitly accepts the current durable value as authoritative and clears a
    /// fail-closed mutation block. This must not be called as an automatic retry of
    /// an operation whose outcome is still causally ambiguous.
    /// </summary>
    public async Task<bool> ReconcileMonoCoinsBalanceAsync(NetUserId userId)
    {
        var gate = GetMutationGate(userId);
        await gate.WaitAsync();
        try
        {
            var persisted = await _db.GetMonoCoinsOrNullAsync(userId, CancellationToken.None);
            if (persisted == null)
                return false;

            ProjectPersistedBalance(userId, persisted.Value);
            _blockedMutations.TryRemove(userId, out _);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Sets the MonoCoins balance using CAS. A contended cache is refreshed and
    /// retried, but an unknown outcome or fail-closed block is surfaced.
    /// </summary>
    public async Task SetMonoCoinsBalanceAsync(NetUserId userId, long balance)
    {
        if (balance < 0L)
        {
            throw new MonoCoinsMutationRejectedException(
                userId,
                new MonoCoinsBalanceUpdateResult(MonoCoinsBalanceUpdateStatus.InvalidBalance));
        }

        var gate = GetMutationGate(userId);
        await gate.WaitAsync();
        try
        {
            ThrowIfBlocked(userId);
            var expected = await GetMonoCoinsBalanceLockedAsync(userId);
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var result = await UpdateMonoCoinsBalanceLockedAsync(userId, expected, balance);
                if (result.Success)
                    return;

                if (CanRetryGenericMutation(result, out var current))
                {
                    expected = current;
                    continue;
                }

                throw new MonoCoinsMutationRejectedException(userId, result);
            }

            throw new MonoCoinsMutationRejectedException(
                userId,
                new MonoCoinsBalanceUpdateResult(
                    MonoCoinsBalanceUpdateStatus.BalanceConflict,
                    expected,
                    new InvalidOperationException("MonoCoins set remained contended.")));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Adds MonoCoins using checked CAS semantics. Debits that exceed the current
    /// durable balance fail instead of being clamped to zero.
    /// </summary>
    public async Task<long> AddMonoCoinsAsync(NetUserId userId, long amount)
    {
        var gate = GetMutationGate(userId);
        await gate.WaitAsync();
        try
        {
            ThrowIfBlocked(userId);
            var expected = await GetMonoCoinsBalanceLockedAsync(userId);
            for (var attempt = 0; attempt < 8; attempt++)
            {
                long newBalance;
                try
                {
                    newBalance = checked(expected + amount);
                }
                catch (OverflowException exception)
                {
                    throw new MonoCoinsMutationRejectedException(
                        userId,
                        new MonoCoinsBalanceUpdateResult(
                            MonoCoinsBalanceUpdateStatus.InvalidBalance,
                            expected,
                            exception));
                }

                if (newBalance < 0L)
                {
                    throw new MonoCoinsMutationRejectedException(
                        userId,
                        new MonoCoinsBalanceUpdateResult(
                            MonoCoinsBalanceUpdateStatus.InvalidBalance,
                            expected));
                }

                var result = await UpdateMonoCoinsBalanceLockedAsync(userId, expected, newBalance);
                if (result.Success)
                    return newBalance;

                if (CanRetryGenericMutation(result, out var current))
                {
                    expected = current;
                    continue;
                }

                throw new MonoCoinsMutationRejectedException(userId, result);
            }

            throw new MonoCoinsMutationRejectedException(
                userId,
                new MonoCoinsBalanceUpdateResult(
                    MonoCoinsBalanceUpdateStatus.BalanceConflict,
                    expected,
                    new InvalidOperationException("MonoCoins addition remained contended.")));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Transfers MonoCoins as one durable database transaction. The sender's one
    /// unacknowledged journal row is reused when the same command is retried after
    /// a process restart, so a fresh command invocation cannot debit twice.
    /// </summary>
    public async Task<MonoCoinsTransferResult> TransferMonoCoinsAsync(
        NetUserId senderUserId,
        NetUserId recipientUserId,
        long amount,
        Guid operationId)
    {
        if (senderUserId == recipientUserId)
        {
            return new MonoCoinsTransferResult(
                MonoCoinsTransferStatus.SameAccount,
                OperationId: operationId);
        }

        var senderFirst = senderUserId.UserId.CompareTo(recipientUserId.UserId) < 0;
        var firstGate = GetMutationGate(senderFirst ? senderUserId : recipientUserId);
        var secondGate = GetMutationGate(senderFirst ? recipientUserId : senderUserId);
        await firstGate.WaitAsync();
        try
        {
            await secondGate.WaitAsync();
            try
            {
                var senderBlocked = _blockedMutations.ContainsKey(senderUserId);
                var recipientBlocked = _blockedMutations.ContainsKey(recipientUserId);
                if (senderBlocked || recipientBlocked)
                {
                    return new MonoCoinsTransferResult(
                        MonoCoinsTransferStatus.Blocked,
                        GetCachedBalanceOrZero(senderUserId),
                        GetCachedBalanceOrZero(recipientUserId),
                        operationId);
                }

                var pending = await _db.GetUnacknowledgedMonoCoinsTransferAsync(senderUserId);
                if (pending is { } existing)
                {
                    if (existing.RecipientUserId != recipientUserId || existing.Amount != amount)
                    {
                        return new MonoCoinsTransferResult(
                            MonoCoinsTransferStatus.PendingOperation,
                            existing.SenderBalanceAfter,
                            existing.RecipientBalanceAfter,
                            existing.OperationId,
                            true);
                    }

                    operationId = existing.OperationId;
                }

                MonoCoinsTransferResult result;
                try
                {
                    result = await _db.TransferMonoCoinsAsync(
                        senderUserId,
                        recipientUserId,
                        amount,
                        operationId);
                }
                catch (Exception firstFailure)
                {
                    // Retrying the same operation id is the verification step: it
                    // either replays the journal proof or performs the transaction
                    // once if the first call never reached the database.
                    try
                    {
                        result = await _db.TransferMonoCoinsAsync(
                            senderUserId,
                            recipientUserId,
                            amount,
                            operationId,
                            CancellationToken.None);
                    }
                    catch (Exception retryFailure)
                    {
                        var unknown = new AggregateException(firstFailure, retryFailure);
                        BlockMonoCoinsMutations(
                            senderUserId,
                            "Atomic MonoCoins transfer outcome is unknown",
                            unknown);
                        BlockMonoCoinsMutations(
                            recipientUserId,
                            "Atomic MonoCoins transfer outcome is unknown",
                            unknown);
                        return new MonoCoinsTransferResult(
                            MonoCoinsTransferStatus.UnknownOutcome,
                            OperationId: operationId);
                    }
                }

                ProjectTransferResult(senderUserId, recipientUserId, result);
                if (result.Status == MonoCoinsTransferStatus.UnknownOutcome)
                {
                    BlockMonoCoinsMutations(
                        senderUserId,
                        "Database could not resolve atomic MonoCoins transfer outcome");
                    BlockMonoCoinsMutations(
                        recipientUserId,
                        "Database could not resolve atomic MonoCoins transfer outcome");
                }

                return result;
            }
            finally
            {
                secondGate.Release();
            }
        }
        finally
        {
            firstGate.Release();
        }
    }

    public Task<MonoCoinsTransferJournalRecord?> GetUnacknowledgedMonoCoinsTransferAsync(
        NetUserId senderUserId)
    {
        return _db.GetUnacknowledgedMonoCoinsTransferAsync(senderUserId);
    }

    public async Task<bool> AcknowledgeMonoCoinsTransferAsync(
        NetUserId senderUserId,
        Guid operationId)
    {
        var gate = GetMutationGate(senderUserId);
        await gate.WaitAsync();
        try
        {
            return await _db.AcknowledgeMonoCoinsTransferAsync(
                senderUserId,
                operationId,
                CancellationToken.None);
        }
        finally
        {
            gate.Release();
        }
    }

    private void ProjectTransferResult(
        NetUserId senderUserId,
        NetUserId recipientUserId,
        MonoCoinsTransferResult result)
    {
        if (result.Status is MonoCoinsTransferStatus.Success or
            MonoCoinsTransferStatus.InsufficientFunds or
            MonoCoinsTransferStatus.RecipientOverflow)
        {
            ProjectPersistedBalance(senderUserId, result.SenderBalance);
            ProjectPersistedBalance(recipientUserId, result.RecipientBalance);
        }
    }

    private long GetCachedBalanceOrZero(NetUserId userId)
    {
        return _cachedBalance.TryGetValue(userId, out var balance) ? balance : 0L;
    }

    private async Task<MonoCoinsBalanceUpdateResult> UpdateMonoCoinsBalanceLockedAsync(
        NetUserId userId,
        long expectedBalance,
        long newBalance)
    {
        if (expectedBalance < 0L || newBalance < 0L)
        {
            return new MonoCoinsBalanceUpdateResult(
                MonoCoinsBalanceUpdateStatus.InvalidBalance,
                _cachedBalance.TryGetValue(userId, out var invalidCurrent) ? invalidCurrent : null);
        }

        try
        {
            var result = await _db.UpdateMonoCoinsBalanceAsync(
                userId,
                expectedBalance,
                newBalance);
            ProjectUpdateResult(userId, result);
            if (result.Status == MonoCoinsBalanceUpdateStatus.UnknownOutcome)
            {
                BlockMonoCoinsMutations(
                    userId,
                    "Database returned an unknown MonoCoins mutation outcome",
                    result.Failure);
            }
            return result;
        }
        catch (Exception mutationFailure)
        {
            long? observed;
            try
            {
                observed = await _db.GetMonoCoinsOrNullAsync(userId, CancellationToken.None);
            }
            catch (Exception verificationFailure)
            {
                var unknown = new MonoCoinsBalanceUpdateResult(
                    MonoCoinsBalanceUpdateStatus.UnknownOutcome,
                    Failure: new AggregateException(mutationFailure, verificationFailure));
                BlockMonoCoinsMutations(userId, "MonoCoins write and verification both failed", unknown.Failure);
                return unknown;
            }

            if (observed == null)
            {
                _cachedBalance.TryRemove(userId, out _);
                return new MonoCoinsBalanceUpdateResult(
                    MonoCoinsBalanceUpdateStatus.AccountMissing,
                    Failure: mutationFailure);
            }

            ProjectPersistedBalance(userId, observed.Value);
            if (observed.Value == newBalance)
            {
                return new MonoCoinsBalanceUpdateResult(
                    MonoCoinsBalanceUpdateStatus.Success,
                    observed,
                    mutationFailure);
            }

            if (observed.Value == expectedBalance)
            {
                return new MonoCoinsBalanceUpdateResult(
                    MonoCoinsBalanceUpdateStatus.ConfirmedNoChange,
                    observed,
                    mutationFailure);
            }

            var ambiguous = new MonoCoinsBalanceUpdateResult(
                MonoCoinsBalanceUpdateStatus.UnknownOutcome,
                observed,
                mutationFailure);
            BlockMonoCoinsMutations(
                userId,
                $"MonoCoins outcome is ambiguous; observed {observed.Value}, expected {expectedBalance} or {newBalance}",
                mutationFailure);
            return ambiguous;
        }
    }

    private async Task<long> GetMonoCoinsBalanceLockedAsync(NetUserId userId)
    {
        if (_cachedBalance.TryGetValue(userId, out var cached))
            return cached;

        var balance = await _db.GetMonoCoinsOrNullAsync(userId) ?? 0L;
        ProjectPersistedBalance(userId, balance);
        return balance;
    }

    private void ProjectUpdateResult(NetUserId userId, MonoCoinsBalanceUpdateResult result)
    {
        if (result.CurrentBalance is { } balance)
        {
            ProjectPersistedBalance(userId, balance);
            return;
        }

        if (result.Status == MonoCoinsBalanceUpdateStatus.AccountMissing)
        {
            _cachedBalance.TryRemove(userId, out _);
            if (_player.TryGetSessionById(userId, out var session))
                SendBalance(session.Channel, 0L);
        }
    }

    private void ProjectPersistedBalance(NetUserId userId, long balance)
    {
        _cachedBalance[userId] = balance;
        if (_player.TryGetSessionById(userId, out var session))
            SendBalance(session.Channel, balance);
    }

    private void ThrowIfBlocked(NetUserId userId)
    {
        if (!_blockedMutations.TryGetValue(userId, out var reason))
            return;

        throw new MonoCoinsMutationRejectedException(
            userId,
            new MonoCoinsBalanceUpdateResult(
                MonoCoinsBalanceUpdateStatus.Blocked,
                _cachedBalance.TryGetValue(userId, out var balance) ? balance : null,
                new InvalidOperationException(reason)));
    }

    private static bool CanRetryGenericMutation(
        MonoCoinsBalanceUpdateResult result,
        out long currentBalance)
    {
        if (result.Status is (MonoCoinsBalanceUpdateStatus.BalanceConflict or
            MonoCoinsBalanceUpdateStatus.ConfirmedNoChange) &&
            result.CurrentBalance is { } current)
        {
            currentBalance = current;
            return true;
        }

        currentBalance = 0L;
        return false;
    }

    private SemaphoreSlim GetMutationGate(NetUserId userId)
    {
        return _mutationGates.GetOrAdd(userId, static _ => new SemaphoreSlim(1, 1));
    }
}
