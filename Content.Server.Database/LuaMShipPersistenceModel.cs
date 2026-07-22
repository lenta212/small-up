using System;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public enum DbLuaMShipSnapshotStatus
{
    Stored,
    Restoring,
    Active,
    Quarantined,
    Retired,
}

/// <summary>
/// The latest complete, authoritative serialized state of one player ship.
/// Payload replacement and lifecycle transitions are fenced by <see cref="Revision"/>.
/// <see cref="PayloadRevision"/> advances only when the serialized payload is replaced.
/// </summary>
public sealed class LuaMShipSnapshot
{
    public Guid ShipId { get; set; }
    public Guid OwnerUserId { get; set; }
    public int OwnerPreferenceId { get; set; }
    /// <summary>
    /// Lifecycle/CAS revision for this registry row. This advances for every
    /// state transition, including claim and abort operations.
    /// </summary>
    public long Revision { get; set; }
    /// <summary>
    /// Revision embedded in the serialized full-ship envelope. A null value is
    /// reserved for rows created before this field was introduced and is
    /// backfilled by their next successful snapshot replacement.
    /// </summary>
    public long? PayloadRevision { get; set; }
    public DbLuaMShipSnapshotStatus Status { get; set; }
    public string VesselPrototypeId { get; set; } = string.Empty;
    public string ShipName { get; set; } = string.Empty;
    public string? ShipNameSuffix { get; set; }
    public int PurchasePrice { get; set; }
    public bool PurchasedWithVoucher { get; set; }
    public int SchemaVersion { get; set; }
    public int FormatVersion { get; set; }
    public byte[] Payload { get; set; } = Array.Empty<byte>();
    public string PayloadHash { get; set; } = string.Empty;
    public int PayloadSizeBytes { get; set; }
    public int EntityCount { get; set; }
    public string SourceBuildVersion { get; set; } = string.Empty;
    public string PrototypeManifestHash { get; set; } = string.Empty;
    public int SourceRoundId { get; set; }
    public int? LastRestoreRoundId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime StoredAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? LastRestoredAtUtc { get; set; }
    public DateTime? QuarantinedAtUtc { get; set; }
    public string? QuarantineReason { get; set; }
    public DateTime? RetiredAtUtc { get; set; }
    public string? RetirementReason { get; set; }
    public Guid? RetirementOperationId { get; set; }
}

/// <summary>
/// The one durable claim permitted to restore or host an active copy of a ship.
/// It intentionally survives restore completion and is released by the next
/// successful full snapshot.
/// </summary>
public sealed class LuaMShipPresenceLease
{
    public Guid ShipId { get; set; }
    public Guid LeaseId { get; set; }
    public string ServerInstanceId { get; set; } = string.Empty;
    public int RoundId { get; set; }
    public DateTime AcquiredAtUtc { get; set; }
    public DateTime RenewedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public long Revision { get; set; }
}

internal static class LuaMShipPersistenceModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LuaMShipSnapshot>(entity =>
        {
            entity.ToTable("luam_ship_snapshot", table =>
            {
                table.HasCheckConstraint("CK_luam_ship_snapshot_status", "status >= 0 AND status <= 4");
                table.HasCheckConstraint("CK_luam_ship_snapshot_revision", "revision >= 0");
                table.HasCheckConstraint("CK_luam_ship_snapshot_owner", "owner_preference_id > 0");
                table.HasCheckConstraint("CK_luam_ship_snapshot_purchase", "purchase_price >= 0");
                table.HasCheckConstraint(
                    "CK_luam_ship_snapshot_versions",
                    "schema_version > 0 AND format_version > 0 AND source_round_id >= 0");
                table.HasCheckConstraint(
                    "CK_luam_ship_snapshot_payload",
                    "payload_size_bytes > 0 AND entity_count > 0 AND length(payload) = payload_size_bytes");
                table.HasCheckConstraint(
                    "CK_luam_ship_snapshot_hashes",
                    "length(payload_hash) = 64 AND length(prototype_manifest_hash) = 64");
                table.HasCheckConstraint(
                    "CK_luam_ship_snapshot_dates",
                    "created_at_utc <= stored_at_utc AND stored_at_utc <= updated_at_utc");
                table.HasCheckConstraint(
                    "CK_luam_ship_snapshot_quarantine",
                    "(status = 3 AND quarantined_at_utc IS NOT NULL AND quarantine_reason IS NOT NULL) OR " +
                    "(status <> 3 AND quarantined_at_utc IS NULL AND quarantine_reason IS NULL)");
                table.HasCheckConstraint(
                    "CK_luam_ship_snapshot_retirement",
                    "(status = 4 AND retired_at_utc IS NOT NULL AND retirement_reason IS NOT NULL AND retirement_operation_id IS NOT NULL) OR " +
                    "(status <> 4 AND retired_at_utc IS NULL AND retirement_reason IS NULL AND retirement_operation_id IS NULL)");
            });

            entity.HasKey(value => value.ShipId);
            entity.Property(value => value.ShipId).ValueGeneratedNever();
            entity.Property(value => value.Revision).IsConcurrencyToken();
            entity.Property(value => value.VesselPrototypeId).HasMaxLength(128);
            entity.Property(value => value.ShipName).HasMaxLength(256);
            entity.Property(value => value.ShipNameSuffix).HasMaxLength(128);
            entity.Property(value => value.PayloadHash).HasMaxLength(64);
            entity.Property(value => value.SourceBuildVersion).HasMaxLength(128);
            entity.Property(value => value.PrototypeManifestHash).HasMaxLength(64);
            entity.Property(value => value.QuarantineReason).HasMaxLength(512);
            entity.Property(value => value.RetirementReason).HasMaxLength(512);
            entity.HasIndex(value => value.RetirementOperationId, "UX_luam_ship_retirement_operation")
                .IsUnique()
                .HasFilter("retirement_operation_id IS NOT NULL");
            entity.HasIndex(value => new { value.OwnerUserId, value.Status },
                "IX_luam_ship_snapshot_owner_status");
            entity.HasIndex(value => value.UpdatedAtUtc, "IX_luam_ship_snapshot_updated");
            entity.HasOne<Preference>()
                .WithMany()
                .HasForeignKey(value => value.OwnerPreferenceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LuaMShipPresenceLease>(entity =>
        {
            entity.ToTable("luam_ship_presence_lease", table =>
            {
                table.HasCheckConstraint("CK_luam_ship_lease_round", "round_id >= 0");
                table.HasCheckConstraint("CK_luam_ship_lease_revision", "revision >= 0");
                table.HasCheckConstraint(
                    "CK_luam_ship_lease_dates",
                    "acquired_at_utc <= renewed_at_utc AND renewed_at_utc < expires_at_utc");
            });

            entity.HasKey(value => value.ShipId);
            entity.Property(value => value.ShipId).ValueGeneratedNever();
            entity.Property(value => value.Revision).IsConcurrencyToken();
            entity.Property(value => value.ServerInstanceId).HasMaxLength(128);
            entity.HasIndex(value => value.LeaseId, "UX_luam_ship_lease_token").IsUnique();
            entity.HasIndex(value => value.ExpiresAtUtc, "IX_luam_ship_lease_expires");
            entity.HasOne<LuaMShipSnapshot>()
                .WithOne()
                .HasForeignKey<LuaMShipPresenceLease>(value => value.ShipId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
