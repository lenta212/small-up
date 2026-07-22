using System;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public enum DbLuaMExpeditionStatus
{
    Discovered,
    Provisioning,
    Active,
    Dormant,
    Depleted,
    Archived,
    Quarantined,
}

public enum DbLuaMExpeditionPreservationPolicy
{
    Ephemeral,
    BeaconPreserved,
    CampaignPermanent,
}

public enum DbLuaMExpeditionCheckpointStatus
{
    Committed,
    Quarantined,
}

public enum DbLuaMExpeditionSnapshotStatus
{
    Active,
    Quarantined,
}

public sealed class LuaMExpeditionManifest
{
    public long Id { get; set; }
    public string CampaignId { get; set; } = string.Empty;
    public string ExpeditionId { get; set; } = string.Empty;
    public long SeedBits { get; set; }
    public int GeneratorVersion { get; set; }
    public string PlanHash { get; set; } = string.Empty;
    public int MinX { get; set; }
    public int MinY { get; set; }
    public int MaxX { get; set; }
    public int MaxY { get; set; }
    public DbLuaMExpeditionStatus Status { get; set; }
    public DbLuaMExpeditionPreservationPolicy PreservationPolicy { get; set; }
    public long Revision { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? ArchivedAtUtc { get; set; }
    public string? QuarantineReason { get; set; }
}

public sealed class LuaMExpeditionRegion
{
    public long Id { get; set; }
    public long ManifestId { get; set; }
    public int RegionX { get; set; }
    public int RegionY { get; set; }
    public int MinX { get; set; }
    public int MinY { get; set; }
    public int MaxX { get; set; }
    public int MaxY { get; set; }
    public string Biome { get; set; } = string.Empty;
    public string? ScenarioTag { get; set; }
    public int DangerBudget { get; set; }
    public DateTime? DiscoveredAtUtc { get; set; }
    public bool IsDepleted { get; set; }
    public int EdgeFormatVersion { get; set; }
    public byte[] EdgePayload { get; set; } = Array.Empty<byte>();
    public string EdgePayloadHash { get; set; } = string.Empty;
    public long Revision { get; set; }
    public DateTime? QuarantinedAtUtc { get; set; }
    public string? QuarantineReason { get; set; }
}

public sealed class LuaMExpeditionSite
{
    public long Id { get; set; }
    public long ManifestId { get; set; }
    public string SiteId { get; set; } = string.Empty;
    public int Kind { get; set; }
    public string PrototypeId { get; set; } = string.Empty;
    public string UniqueScope { get; set; } = string.Empty;
    public int PositionX { get; set; }
    public int PositionY { get; set; }
    public int MinX { get; set; }
    public int MinY { get; set; }
    public int MaxX { get; set; }
    public int MaxY { get; set; }
    public int StateFormatVersion { get; set; }
    public byte[] StatePayload { get; set; } = Array.Empty<byte>();
    public string StatePayloadHash { get; set; } = string.Empty;
    public long Revision { get; set; }
    public DateTime? DiscoveredAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

public sealed class LuaMExpeditionDelta
{
    public long Id { get; set; }
    public long RegionId { get; set; }
    public long RegionRevision { get; set; }
    public string SourceInstanceId { get; set; } = string.Empty;
    public int Kind { get; set; }
    public int FormatVersion { get; set; }
    public byte[] Payload { get; set; } = Array.Empty<byte>();
    public string PayloadHash { get; set; } = string.Empty;
    public int? ActorProfileId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class LuaMExpeditionEntitySnapshot
{
    public long Id { get; set; }
    public long RegionId { get; set; }
    public string StableEntityId { get; set; } = string.Empty;
    public string SourceInstanceId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public long RegionRevision { get; set; }
    public int FormatVersion { get; set; }
    public byte[] Payload { get; set; } = Array.Empty<byte>();
    public string PayloadHash { get; set; } = string.Empty;
    public DbLuaMExpeditionSnapshotStatus Status { get; set; }
    public int? ActorProfileId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? QuarantineReason { get; set; }
}

public sealed class LuaMExpeditionTombstone
{
    public long Id { get; set; }
    public long RegionId { get; set; }
    public long? SiteRowId { get; set; }
    public string StableObjectId { get; set; } = string.Empty;
    public string SourceInstanceId { get; set; } = string.Empty;
    public int Kind { get; set; }
    public long RegionRevision { get; set; }
    public int? ActorProfileId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class LuaMExpeditionCheckpoint
{
    public long Id { get; set; }
    public long ManifestId { get; set; }
    public Guid OperationId { get; set; }
    public string OperationIdentityKey { get; set; } = string.Empty;
    public int FormatVersion { get; set; }
    public byte[] StatePayload { get; set; } = Array.Empty<byte>();
    public string PayloadHash { get; set; } = string.Empty;
    public DbLuaMExpeditionCheckpointStatus Status { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CommittedAtUtc { get; set; }
    public string? QuarantineReason { get; set; }
}

internal static class LuaMExpeditionModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LuaMExpeditionManifest>(entity =>
        {
            entity.ToTable("luam_expedition_manifest", table =>
            {
                table.HasCheckConstraint("CK_luam_exp_manifest_version", "generator_version > 0");
                table.HasCheckConstraint("CK_luam_exp_manifest_bounds", "min_x <= max_x AND min_y <= max_y");
                table.HasCheckConstraint("CK_luam_exp_manifest_revision", "revision >= 0");
                table.HasCheckConstraint("CK_luam_exp_manifest_status", "status >= 0 AND status <= 6");
                table.HasCheckConstraint("CK_luam_exp_manifest_policy", "preservation_policy >= 0 AND preservation_policy <= 2");
            });
            entity.HasKey(value => value.Id);
            entity.Property(value => value.CampaignId).HasMaxLength(128);
            entity.Property(value => value.ExpeditionId).HasMaxLength(128);
            entity.Property(value => value.PlanHash).HasMaxLength(64);
            entity.Property(value => value.QuarantineReason).HasMaxLength(512);
            entity.Property(value => value.Revision).IsConcurrencyToken();
            entity.HasIndex(value => new { value.CampaignId, value.ExpeditionId }, "UX_luam_exp_manifest_identity").IsUnique();
            entity.HasIndex(value => new { value.Status, value.UpdatedAtUtc }, "IX_luam_exp_manifest_status_updated");
        });

        modelBuilder.Entity<LuaMExpeditionRegion>(entity =>
        {
            entity.ToTable("luam_expedition_region", table =>
            {
                table.HasCheckConstraint("CK_luam_exp_region_bounds", "min_x <= max_x AND min_y <= max_y");
                table.HasCheckConstraint("CK_luam_exp_region_version", "edge_format_version > 0");
                table.HasCheckConstraint("CK_luam_exp_region_revision", "revision >= 0");
            });
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Biome).HasMaxLength(128);
            entity.Property(value => value.ScenarioTag).HasMaxLength(128);
            entity.Property(value => value.EdgePayloadHash).HasMaxLength(64);
            entity.Property(value => value.QuarantineReason).HasMaxLength(512);
            entity.Property(value => value.Revision).IsConcurrencyToken();
            entity.HasOne<LuaMExpeditionManifest>().WithMany().HasForeignKey(value => value.ManifestId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(value => new { value.ManifestId, value.RegionX, value.RegionY }, "UX_luam_exp_region_coordinates").IsUnique();
            entity.HasIndex(value => new { value.ManifestId, value.Revision }, "IX_luam_exp_region_revision");
            entity.HasIndex(value => new { value.ManifestId, value.DiscoveredAtUtc }, "IX_luam_exp_region_discovered");
        });

        modelBuilder.Entity<LuaMExpeditionSite>(entity =>
        {
            entity.ToTable("luam_expedition_site", table =>
            {
                table.HasCheckConstraint("CK_luam_exp_site_bounds", "min_x <= max_x AND min_y <= max_y");
                table.HasCheckConstraint("CK_luam_exp_site_version", "state_format_version > 0");
                table.HasCheckConstraint("CK_luam_exp_site_revision", "revision >= 0");
            });
            entity.HasKey(value => value.Id);
            entity.Property(value => value.SiteId).HasMaxLength(128);
            entity.Property(value => value.PrototypeId).HasMaxLength(128);
            entity.Property(value => value.UniqueScope).HasMaxLength(128);
            entity.Property(value => value.StatePayloadHash).HasMaxLength(64);
            entity.Property(value => value.Revision).IsConcurrencyToken();
            entity.HasOne<LuaMExpeditionManifest>().WithMany().HasForeignKey(value => value.ManifestId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(value => new { value.ManifestId, value.SiteId }, "UX_luam_exp_site_identity").IsUnique();
            entity.HasIndex(value => new { value.ManifestId, value.UniqueScope, value.PrototypeId }, "UX_luam_exp_site_scope_proto").IsUnique();
            entity.HasIndex(value => new { value.ManifestId, value.Kind }, "IX_luam_exp_site_kind");
        });

        modelBuilder.Entity<LuaMExpeditionDelta>(entity =>
        {
            entity.ToTable("luam_expedition_delta", table =>
            {
                table.HasCheckConstraint("CK_luam_exp_delta_revision", "region_revision > 0");
                table.HasCheckConstraint("CK_luam_exp_delta_version", "format_version > 0");
            });
            entity.HasKey(value => value.Id);
            entity.Property(value => value.SourceInstanceId).HasMaxLength(256);
            entity.Property(value => value.PayloadHash).HasMaxLength(64);
            entity.HasOne<LuaMExpeditionRegion>().WithMany().HasForeignKey(value => value.RegionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Profile>().WithMany().HasForeignKey(value => value.ActorProfileId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(value => new { value.RegionId, value.RegionRevision }, "UX_luam_exp_delta_revision").IsUnique();
            entity.HasIndex(value => new { value.RegionId, value.SourceInstanceId }, "UX_luam_exp_delta_source").IsUnique();
        });

        modelBuilder.Entity<LuaMExpeditionEntitySnapshot>(entity =>
        {
            entity.ToTable("luam_expedition_entity_snapshot", table =>
            {
                table.HasCheckConstraint("CK_luam_exp_snapshot_revision", "region_revision > 0");
                table.HasCheckConstraint("CK_luam_exp_snapshot_version", "format_version > 0");
                table.HasCheckConstraint("CK_luam_exp_snapshot_status", "status >= 0 AND status <= 1");
                table.HasCheckConstraint("CK_luam_exp_snapshot_key", "length(idempotency_key) = 64");
            });
            entity.HasKey(value => value.Id);
            entity.Property(value => value.StableEntityId).HasMaxLength(256);
            entity.Property(value => value.SourceInstanceId).HasMaxLength(256);
            entity.Property(value => value.IdempotencyKey).HasMaxLength(64);
            entity.Property(value => value.PayloadHash).HasMaxLength(64);
            entity.Property(value => value.QuarantineReason).HasMaxLength(512);
            entity.HasOne<LuaMExpeditionRegion>().WithMany().HasForeignKey(value => value.RegionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Profile>().WithMany().HasForeignKey(value => value.ActorProfileId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(value => new { value.RegionId, value.StableEntityId }, "UX_luam_exp_snapshot_entity").IsUnique();
            entity.HasIndex(value => value.IdempotencyKey, "UX_luam_exp_snapshot_idempotency").IsUnique();
        });

        modelBuilder.Entity<LuaMExpeditionTombstone>(entity =>
        {
            entity.ToTable("luam_expedition_tombstone", table =>
                table.HasCheckConstraint("CK_luam_exp_tombstone_revision", "region_revision > 0"));
            entity.HasKey(value => value.Id);
            entity.Property(value => value.StableObjectId).HasMaxLength(256);
            entity.Property(value => value.SourceInstanceId).HasMaxLength(256);
            entity.HasOne<LuaMExpeditionRegion>().WithMany().HasForeignKey(value => value.RegionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LuaMExpeditionSite>().WithMany().HasForeignKey(value => value.SiteRowId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Profile>().WithMany().HasForeignKey(value => value.ActorProfileId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(value => new { value.RegionId, value.StableObjectId }, "UX_luam_exp_tombstone_object").IsUnique();
            entity.HasIndex(value => new { value.RegionId, value.SourceInstanceId }, "IX_luam_exp_tombstone_source");
        });

        modelBuilder.Entity<LuaMExpeditionCheckpoint>(entity =>
        {
            entity.ToTable("luam_expedition_checkpoint", table =>
            {
                table.HasCheckConstraint("CK_luam_exp_checkpoint_version", "format_version > 0");
                table.HasCheckConstraint("CK_luam_exp_checkpoint_status", "status >= 0 AND status <= 1");
                table.HasCheckConstraint("CK_luam_exp_checkpoint_identity", "length(operation_identity_key) = 64");
            });
            entity.HasKey(value => value.Id);
            entity.Property(value => value.OperationIdentityKey).HasMaxLength(64);
            entity.Property(value => value.PayloadHash).HasMaxLength(64);
            entity.Property(value => value.QuarantineReason).HasMaxLength(512);
            entity.HasOne<LuaMExpeditionManifest>().WithMany().HasForeignKey(value => value.ManifestId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(value => value.OperationId, "UX_luam_exp_checkpoint_operation").IsUnique();
            entity.HasIndex(value => new { value.ManifestId, value.CommittedAtUtc }, "IX_luam_exp_checkpoint_committed");
        });
    }
}
