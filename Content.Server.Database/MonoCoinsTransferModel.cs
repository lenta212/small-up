using System;
using System.ComponentModel.DataAnnotations;

namespace Content.Server.Database;

/// <summary>
/// Durable idempotency proof for an atomic MonoCoins transfer. The user ids are
/// intentionally not foreign keys so a committed transfer remains replayable
/// after either preference row is removed.
/// </summary>
public sealed class MonoCoinsTransferJournal
{
    [Key]
    public Guid OperationId { get; set; }
    public Guid SenderUserId { get; set; }
    public Guid RecipientUserId { get; set; }
    public long Amount { get; set; }
    public long SenderBalanceBefore { get; set; }
    public long SenderBalanceAfter { get; set; }
    public long RecipientBalanceBefore { get; set; }
    public long RecipientBalanceAfter { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
}
