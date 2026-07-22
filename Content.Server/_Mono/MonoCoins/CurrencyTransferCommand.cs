using System.Linq;
using Content.Server.Chat.Managers;
using Content.Server.Database;
using Content.Shared.Administration;
using Content.Shared.Chat;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.Player;

namespace Content.Server._Mono.MonoCoins;

/// <summary>
/// Player command for transferring MonoCoins to other players.
/// </summary>
[AnyCommand]
public sealed partial class CurrencyTransferCommand : LocalizedCommands
{
    [Dependency] private IPlayerManager _playerManager = default!;
    [Dependency] private MonoCoinsManager _coins = default!;
    [Dependency] private IChatManager _chatManager = default!;

    public override string Command => "currency:transfer";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 2)
        {
            shell.WriteError("Usage: currency:transfer <player> <amount>");
            return;
        }

        var targetPlayerName = args[0];

        if (!long.TryParse(args[1], out var amount))
        {
            shell.WriteError("Amount must be a valid integer.");
            return;
        }

        if (amount <= 0)
        {
            shell.WriteError("Amount must be positive.");
            return;
        }

        // Get the sender (the player executing the command)
        var senderSession = shell.Player as ICommonSession;
        if (senderSession == null)
        {
            shell.WriteError("This command can only be used by players.");
            return;
        }

        // Find the target player
        ICommonSession? targetSession = null;
        foreach (var session in _playerManager.Sessions)
        {
            if (session.Name.Equals(targetPlayerName, StringComparison.OrdinalIgnoreCase))
            {
                targetSession = session;
                break;
            }
        }

        if (targetSession == null)
        {
            shell.WriteError($"Player '{targetPlayerName}' not found or not online.");
            return;
        }

        // Prevent self-transfer
        if (senderSession.UserId == targetSession.UserId)
        {
            shell.WriteError("You cannot transfer currency to yourself.");
            return;
        }

        var senderUserId = senderSession.UserId;
        var targetUserId = targetSession.UserId;
        long committedSenderBalance;
        long committedTargetBalance;
        Guid committedOperationId;

        try
        {
            var result = await _coins.TransferMonoCoinsAsync(
                senderUserId,
                targetUserId,
                amount,
                Guid.NewGuid());

            if (result.Status == MonoCoinsTransferStatus.PendingOperation)
            {
                var pending = await _coins.GetUnacknowledgedMonoCoinsTransferAsync(senderUserId);
                if (pending is { } recovery)
                {
                    try
                    {
                        shell.WriteError(
                            $"A previous transfer of ${recovery.Amount} to {recovery.RecipientUserId} " +
                            $"already committed. This request was not executed; run it again if still desired.");
                    }
                    finally
                    {
                        // Observing the durable recovery record closes it. If this
                        // acknowledgement fails, the next invocation reports it again.
                        try
                        {
                            await _coins.AcknowledgeMonoCoinsTransferAsync(
                                senderUserId,
                                recovery.OperationId);
                        }
                        catch
                        {
                            // The committed transfer remains recoverable in the journal.
                        }
                    }
                }
                else
                {
                    shell.WriteError("Another currency transfer is pending reconciliation.");
                }

                return;
            }

            if (!result.Success)
            {
                var error = result.Status switch
                {
                    MonoCoinsTransferStatus.InsufficientFunds =>
                        $"Insufficient currency. You have ${result.SenderBalance}, cannot transfer ${amount}.",
                    MonoCoinsTransferStatus.RecipientOverflow =>
                        "The recipient balance cannot hold this transfer.",
                    MonoCoinsTransferStatus.SenderNotFound =>
                        "Your MonoCoins account no longer exists.",
                    MonoCoinsTransferStatus.RecipientNotFound =>
                        "The recipient MonoCoins account no longer exists.",
                    MonoCoinsTransferStatus.Blocked =>
                        "Transfer blocked pending MonoCoins reconciliation.",
                    MonoCoinsTransferStatus.UnknownOutcome =>
                        "Transfer outcome is unknown; both accounts are blocked pending reconciliation.",
                    _ => $"Transfer was rejected ({result.Status}).",
                };
                shell.WriteError(error);
                return;
            }

            // Nothing below this point in the mutation block may be a fallible
            // notification. The atomic DB operation is authoritative now, so a
            // UI/chat failure must never be reported as a failed transfer and
            // encourage the sender to repeat it.
            committedSenderBalance = result.SenderBalance;
            committedTargetBalance = result.RecipientBalance;
            committedOperationId = result.OperationId;
        }
        catch (Exception ex)
        {
            shell.WriteError($"Transfer failed due to database error: {ex.Message}");
            return;
        }

        try
        {
            shell.WriteLine(
                $"Successfully transferred ${amount} to {targetPlayerName}. " +
                $"New balance: ${committedSenderBalance}");
        }
        catch
        {
            // The transfer is committed. Console delivery is best effort only.
        }

        try
        {
            var notificationMessage =
                $"Received ${amount} from {senderSession.Name}. New balance: ${committedTargetBalance}";
            _chatManager.ChatMessageToOne(
                ChatChannel.Notifications,
                notificationMessage,
                notificationMessage,
                EntityUid.Invalid,
                false,
                targetSession.Channel);
        }
        catch
        {
            // The transfer is committed. Chat delivery is best effort only.
        }

        try
        {
            await _coins.AcknowledgeMonoCoinsTransferAsync(senderUserId, committedOperationId);
        }
        catch
        {
            // Retry recovery remains available through the durable journal.
        }
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        switch (args.Length)
        {
            case 1:
                var playerNames = _playerManager.Sessions.Select(s => s.Name).ToArray();
                return CompletionResult.FromOptions(playerNames);
            case 2:
                return CompletionResult.FromHint("Amount");
            default:
                return CompletionResult.Empty;
        }
    }
}
