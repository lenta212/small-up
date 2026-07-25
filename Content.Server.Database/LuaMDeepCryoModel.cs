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

public enum DbLuaMCharacterPresencePhase
{
    RestoreClaim,
    FreshReserved,
    Playable,
}

public enum DbLuaMCharacterPresenceOperationKind
{
    Reserve,
    Publish,
    Release,
    Reclaim,
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
/// The single durable presence authority for a character. Restore PREPARE/AUTH
/// retain the RestoreClaim row; ACK changes that same row to Playable without a
/// presence-null gap. Store, exact release, quarantine, or restore recovery remove it.
/// </summary>
public sealed class LuaMCharacterPresenceLease
{
    public int ProfileId { get; set; }
    public long? SnapshotId { get; set; }
    public Guid LeaseId { get; set; }
    public DbLuaMCharacterPresencePhase Phase { get; set; }
    public string ServerInstanceId { get; set; } = string.Empty;
    public int RoundId { get; set; }
    public DateTime AcquiredAtUtc { get; set; }
    public DateTime RenewedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public long Revision { get; set; }
    public long AuthorityLifecycleRevision { get; set; }
    public Guid? LastRenewalOperationId { get; set; }
    public string? LastRenewalOperationIdentityKey { get; set; }
}

/// <summary>
/// Immutable replay evidence for fresh/playable presence-authority mutations.
/// Full result fields preserve an exact proof even after a later transition
/// rotates or removes the live lease row.
/// </summary>
public sealed class LuaMCharacterPresenceOperation
{
    public long Id { get; set; }
    public Guid OperationId { get; set; }
    public string OperationIdentityKey { get; set; } = string.Empty;
    public int ProfileId { get; set; }
    public DbLuaMCharacterPresenceOperationKind Kind { get; set; }
    public bool ResultHasAuthority { get; set; }
    public long? ResultSnapshotId { get; set; }
    public Guid? ResultLeaseId { get; set; }
    public DbLuaMCharacterPresencePhase? ResultPhase { get; set; }
    public string? ResultServerInstanceId { get; set; }
    public int? ResultRoundId { get; set; }
    public DateTime? ResultAcquiredAtUtc { get; set; }
    public DateTime? ResultRenewedAtUtc { get; set; }
    public DateTime? ResultExpiresAtUtc { get; set; }
    public long? ResultLeaseRevision { get; set; }
    public long? ResultAuthorityLifecycleRevision { get; set; }
    public long ResultLifecycleRevision { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAtUtc { get; set; }
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
                table.HasCheckConstraint("CK_luam_cryo_lease_round", "round_id >= 0");
                table.HasCheckConstraint("CK_luam_cryo_lease_phase", "phase >= 0 AND phase <= 2");
                table.HasCheckConstraint(
                    "CK_luam_cryo_lease_revision",
                    "revision >= 0 AND authority_lifecycle_revision >= 0");
                table.HasCheckConstraint(
                    "CK_luam_cryo_lease_snapshot_phase",
                    "(phase = 0 AND snapshot_id IS NOT NULL) OR " +
                    "(phase = 1 AND snapshot_id IS NULL) OR phase = 2");
                table.HasCheckConstraint(
                    "CK_luam_cryo_lease_dates",
                    "acquired_at_utc <= renewed_at_utc AND renewed_at_utc < expires_at_utc");
                table.HasCheckConstraint(
                    "CK_luam_cryo_lease_last_renewal",
                    "(last_renewal_operation_id IS NULL AND last_renewal_operation_identity_key IS NULL) OR " +
                    "(last_renewal_operation_id IS NOT NULL AND " +
                    "last_renewal_operation_identity_key IS NOT NULL AND " +
                    "length(last_renewal_operation_identity_key) = 64)");
            });

            entity.HasKey(value => value.ProfileId);
            entity.Property(value => value.ProfileId).ValueGeneratedNever();
            entity.Property(value => value.Revision).IsConcurrencyToken();
            entity.Property(value => value.ServerInstanceId).HasMaxLength(128);
            entity.Property(value => value.LastRenewalOperationIdentityKey).HasMaxLength(64);
            entity.HasIndex(value => value.SnapshotId, "UX_luam_cryo_lease_snapshot")
                .IsUnique()
                .HasFilter("snapshot_id IS NOT NULL");
            entity.HasIndex(value => value.LeaseId, "UX_luam_cryo_lease_token").IsUnique();
            entity.HasIndex(value => value.ExpiresAtUtc, "IX_luam_cryo_lease_expires");
            entity.HasIndex(value => new { value.Phase, value.ExpiresAtUtc },
                "IX_luam_cryo_lease_phase_expires");
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

        modelBuilder.Entity<LuaMCharacterPresenceOperation>(entity =>
        {
            entity.ToTable("luam_character_presence_operation", table =>
            {
                table.HasCheckConstraint("CK_luam_presence_operation_kind", "kind >= 0 AND kind <= 3");
                table.HasCheckConstraint(
                    "CK_luam_presence_operation_identity",
                    "length(operation_identity_key) = 64");
                table.HasCheckConstraint(
                    "CK_luam_presence_operation_revision",
                    "result_lifecycle_revision >= 0 AND " +
                    "(result_lease_revision IS NULL OR result_lease_revision >= 0) AND " +
                    "(result_authority_lifecycle_revision IS NULL OR result_authority_lifecycle_revision >= 0)");
                table.HasCheckConstraint(
                    "CK_luam_presence_operation_authority",
                    "(result_has_authority = FALSE AND result_snapshot_id IS NULL AND " +
                    "result_lease_id IS NULL AND result_phase IS NULL AND " +
                    "result_server_instance_id IS NULL AND result_round_id IS NULL AND " +
                    "result_acquired_at_utc IS NULL AND result_renewed_at_utc IS NULL AND " +
                    "result_expires_at_utc IS NULL AND result_lease_revision IS NULL AND " +
                    "result_authority_lifecycle_revision IS NULL) OR " +
                    "(result_has_authority = TRUE AND result_lease_id IS NOT NULL AND " +
                    "result_phase IS NOT NULL AND result_phase BETWEEN 0 AND 2 AND " +
                    "result_server_instance_id IS NOT NULL AND " +
                    "result_round_id IS NOT NULL AND result_round_id >= 0 AND " +
                    "result_acquired_at_utc IS NOT NULL AND " +
                    "result_renewed_at_utc IS NOT NULL AND result_expires_at_utc IS NOT NULL AND " +
                    "result_acquired_at_utc <= result_renewed_at_utc AND " +
                    "result_renewed_at_utc < result_expires_at_utc AND " +
                    "result_lease_revision IS NOT NULL AND result_lease_revision >= 0 AND " +
                    "result_authority_lifecycle_revision IS NOT NULL AND " +
                    "result_authority_lifecycle_revision >= 0)");
                table.HasCheckConstraint(
                    "CK_luam_presence_operation_snapshot_phase",
                    "result_has_authority = FALSE OR (result_phase <> 0 OR result_snapshot_id IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_luam_presence_operation_kind_result",
                    "(kind = 0 AND result_has_authority = TRUE AND result_phase = 1 AND " +
                    "result_snapshot_id IS NULL AND reason IS NULL) OR " +
                    "(kind = 1 AND result_has_authority = TRUE AND result_phase = 2 AND " +
                    "result_snapshot_id IS NULL AND reason IS NULL) OR " +
                    "(kind = 2 AND result_has_authority = FALSE AND reason IS NOT NULL) OR " +
                    "(kind = 3 AND result_has_authority = TRUE AND result_phase = 1 AND " +
                    "result_snapshot_id IS NULL AND reason IS NULL)");
                table.HasCheckConstraint(
                    "CK_luam_presence_operation_epoch",
                    "result_has_authority = FALSE OR " +
                    "result_authority_lifecycle_revision = result_lifecycle_revision");
            });

            entity.HasKey(value => value.Id);
            entity.Property(value => value.OperationIdentityKey).HasMaxLength(64);
            entity.Property(value => value.ResultServerInstanceId).HasMaxLength(128);
            entity.Property(value => value.Reason).HasMaxLength(512);
            entity.HasIndex(value => value.OperationId, "UX_luam_presence_operation_id").IsUnique();
            entity.HasIndex(value => new { value.ProfileId, value.CreatedAtUtc },
                "IX_luam_presence_operation_profile_created");
            entity.HasOne<Profile>()
                .WithMany()
                .HasForeignKey(value => value.ProfileId)
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
