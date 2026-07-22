using System;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public enum DbLuaMDeepCryoSnapshotStatus
{
    Stored,
    Restoring,
    Consumed,
    Quarantined,
}

public enum DbLuaMDeepCryoOperationKind
{
    Store,
    ClaimRestore,
    CompleteRestore,
    AbortRestore,
    Discard,
    Quarantine,
    RecoverExpiredLease,
}

/// <summary>
/// An immutable serialized body payload plus the mutable lifecycle fence for one
/// deep-cryo storage episode. Account and slot are captured authorization data;
/// <see cref="ProfileId"/> remains the durable character identity.
/// </summary>
public sealed class LuaMDeepCryoSnapshot
{
    public long Id { get; set; }
    public Guid PlayerUserId { get; set; }
    public int PreferenceId { get; set; }
    public int ProfileId { get; set; }
    public int Slot { get; set; }
    public int SourceRoundId { get; set; }
    public int? LastRestoreRoundId { get; set; }
    public DbLuaMDeepCryoSnapshotStatus Status { get; set; }
    public long Revision { get; set; }
    public int FormatVersion { get; set; }
    public byte[] Payload { get; set; } = Array.Empty<byte>();
    public string PayloadHash { get; set; } = string.Empty;
    public int PayloadSizeBytes { get; set; }
    public int EntityCount { get; set; }
    public string SourceBuildVersion { get; set; } = string.Empty;
    public string PrototypeManifestHash { get; set; } = string.Empty;
    public DateTime StoredAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }
    public DateTime? QuarantinedAtUtc { get; set; }
    public string? QuarantineReason { get; set; }
}

/// <summary>
/// The single durable restore claim for a character. The row is removed when a
/// restore is completed, aborted, discarded, quarantined, or recovered after TTL.
/// </summary>
public sealed class LuaMCharacterPresenceLease
{
    public int ProfileId { get; set; }
    public long SnapshotId { get; set; }
    public Guid LeaseId { get; set; }
    public string ServerInstanceId { get; set; } = string.Empty;
    public int RestoreRoundId { get; set; }
    public DateTime AcquiredAtUtc { get; set; }
    public DateTime RenewedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public long Revision { get; set; }
}

/// <summary>
/// Append-only idempotency proof for every committed cryo lifecycle mutation.
/// </summary>
public sealed class LuaMDeepCryoOperation
{
    public long Id { get; set; }
    public Guid OperationId { get; set; }
    public string OperationIdentityKey { get; set; } = string.Empty;
    public long SnapshotId { get; set; }
    public int ProfileId { get; set; }
    public DbLuaMDeepCryoOperationKind Kind { get; set; }
    public DbLuaMDeepCryoSnapshotStatus ResultStatus { get; set; }
    public long ResultRevision { get; set; }
    public Guid? LeaseId { get; set; }
    public int? RoundId { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

internal static class LuaMDeepCryoModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LuaMDeepCryoSnapshot>(entity =>
        {
            entity.ToTable("luam_deep_cryo_snapshot", table =>
            {
                table.HasCheckConstraint("CK_luam_cryo_snapshot_status", "status >= 0 AND status <= 3");
                table.HasCheckConstraint("CK_luam_cryo_snapshot_revision", "revision >= 0");
                table.HasCheckConstraint("CK_luam_cryo_snapshot_identity", "slot >= 0 AND source_round_id >= 0");
                table.HasCheckConstraint("CK_luam_cryo_snapshot_format", "format_version > 0");
                table.HasCheckConstraint("CK_luam_cryo_snapshot_payload", "payload_size_bytes > 0 AND entity_count > 0 AND length(payload) = payload_size_bytes");
                table.HasCheckConstraint("CK_luam_cryo_snapshot_hashes", "length(payload_hash) = 64 AND length(prototype_manifest_hash) = 64");
                table.HasCheckConstraint("CK_luam_cryo_snapshot_dates", "stored_at_utc <= updated_at_utc");
                table.HasCheckConstraint(
                    "CK_luam_cryo_snapshot_terminal",
                    "(status = 2 AND consumed_at_utc IS NOT NULL AND quarantined_at_utc IS NULL AND quarantine_reason IS NULL) OR " +
                    "(status = 3 AND consumed_at_utc IS NULL AND quarantined_at_utc IS NOT NULL AND quarantine_reason IS NOT NULL) OR " +
                    "(status IN (0, 1) AND consumed_at_utc IS NULL AND quarantined_at_utc IS NULL AND quarantine_reason IS NULL)");
            });

            entity.HasKey(value => value.Id);
            entity.HasAlternateKey(value => new { value.Id, value.ProfileId });
            entity.Property(value => value.Revision).IsConcurrencyToken();
            entity.Property(value => value.PayloadHash).HasMaxLength(64);
            entity.Property(value => value.SourceBuildVersion).HasMaxLength(128);
            entity.Property(value => value.PrototypeManifestHash).HasMaxLength(64);
            entity.Property(value => value.QuarantineReason).HasMaxLength(512);
            entity.HasIndex(value => value.ProfileId, "UX_luam_cryo_snapshot_active_profile")
                .IsUnique()
                .HasFilter("status <> 2");
            entity.HasIndex(value => new { value.PlayerUserId, value.Slot, value.Status },
                "IX_luam_cryo_snapshot_player_slot_status");
            entity.HasIndex(value => new { value.ProfileId, value.StoredAtUtc },
                "IX_luam_cryo_snapshot_profile_stored");
            entity.HasOne<Profile>()
                .WithMany()
                .HasForeignKey(value => value.ProfileId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Preference>()
                .WithMany()
                .HasForeignKey(value => value.PreferenceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LuaMCharacterPresenceLease>(entity =>
        {
            entity.ToTable("luam_character_presence_lease", table =>
            {
                table.HasCheckConstraint("CK_luam_cryo_lease_round", "restore_round_id >= 0");
                table.HasCheckConstraint("CK_luam_cryo_lease_revision", "revision >= 0");
                table.HasCheckConstraint(
                    "CK_luam_cryo_lease_dates",
                    "acquired_at_utc <= renewed_at_utc AND renewed_at_utc < expires_at_utc");
            });

            entity.HasKey(value => value.ProfileId);
            entity.Property(value => value.ProfileId).ValueGeneratedNever();
            entity.Property(value => value.Revision).IsConcurrencyToken();
            entity.Property(value => value.ServerInstanceId).HasMaxLength(128);
            entity.HasIndex(value => value.SnapshotId, "UX_luam_cryo_lease_snapshot").IsUnique();
            entity.HasIndex(value => value.LeaseId, "UX_luam_cryo_lease_token").IsUnique();
            entity.HasIndex(value => value.ExpiresAtUtc, "IX_luam_cryo_lease_expires");
            entity.HasOne<Profile>()
                .WithOne()
                .HasForeignKey<LuaMCharacterPresenceLease>(value => value.ProfileId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LuaMDeepCryoSnapshot>()
                .WithMany()
                .HasForeignKey(value => new { value.SnapshotId, value.ProfileId })
                .HasPrincipalKey(value => new { value.Id, value.ProfileId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LuaMDeepCryoOperation>(entity =>
        {
            entity.ToTable("luam_deep_cryo_operation", table =>
            {
                table.HasCheckConstraint("CK_luam_cryo_operation_kind", "kind >= 0 AND kind <= 6");
                table.HasCheckConstraint("CK_luam_cryo_operation_status", "result_status >= 0 AND result_status <= 3");
                table.HasCheckConstraint("CK_luam_cryo_operation_revision", "result_revision >= 0");
                table.HasCheckConstraint("CK_luam_cryo_operation_identity", "length(operation_identity_key) = 64");
                table.HasCheckConstraint("CK_luam_cryo_operation_round", "round_id IS NULL OR round_id >= 0");
            });

            entity.HasKey(value => value.Id);
            entity.Property(value => value.OperationIdentityKey).HasMaxLength(64);
            entity.Property(value => value.Reason).HasMaxLength(512);
            entity.HasIndex(value => value.OperationId, "UX_luam_cryo_operation_id").IsUnique();
            entity.HasIndex(value => value.LeaseId, "UX_luam_cryo_operation_claim_lease")
                .IsUnique()
                .HasFilter("kind = 1");
            entity.HasIndex(value => new { value.SnapshotId, value.CreatedAtUtc },
                "IX_luam_cryo_operation_snapshot_created");
            entity.HasIndex(value => new { value.ProfileId, value.CreatedAtUtc },
                "IX_luam_cryo_operation_profile_created");
            entity.HasOne<Profile>()
                .WithMany()
                .HasForeignKey(value => value.ProfileId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LuaMDeepCryoSnapshot>()
                .WithMany()
                .HasForeignKey(value => new { value.SnapshotId, value.ProfileId })
                .HasPrincipalKey(value => new { value.Id, value.ProfileId })
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
