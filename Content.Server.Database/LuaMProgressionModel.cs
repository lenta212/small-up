using System;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public enum DbLuaMCampaignShiftStatus
{
    Open,
    Sealing,
    Closed,
    Aborted,
}

public enum DbLuaMCharacterCareerStatus
{
    Playable,
    CryoStored,
    RecoveryPending,
    Retired,
    Memorialized,
    Archived,
    Quarantined,
}

public enum DbLuaMProgressionCurrency
{
    Career,
    Mastery,
    Reputation,
}

public sealed class LuaMCampaignShift
{
    public long ShiftPeriodId { get; set; }
    public DateTime StartsAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }
    public DbLuaMCampaignShiftStatus Status { get; set; }
    public long Revision { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? SealedAtUtc { get; set; }
}

public sealed class LuaMCampaignShiftRun
{
    public long ShiftPeriodId { get; set; }
    public int RoundId { get; set; }
    public DateTime AttachedAtUtc { get; set; }
}

public sealed class LuaMCharacterCareer
{
    public int ProfileId { get; set; }
    public DbLuaMCharacterCareerStatus Status { get; set; }
    public long TotalCareerXp { get; set; }
    public int CreditedShiftCount { get; set; }
    public int Level { get; set; }
    public long Revision { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class LuaMCareerShiftParticipation
{
    public long ShiftPeriodId { get; set; }
    public int ProfileId { get; set; }
    public int PreferenceId { get; set; }
    public bool IsCareerFocus { get; set; }
    public int PreliminaryCareerXp { get; set; }
    public int ResultCareerXp { get; set; }
    public int ActiveMinutes { get; set; }
    public int DistinctResultCategories { get; set; }
    public int FinalCareerXp { get; set; }
    public bool IsEligible { get; set; }
    public bool IsCredited { get; set; }
    public long Revision { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? SealedAtUtc { get; set; }
}

public sealed class LuaMCareerXpLedger
{
    public long Id { get; set; }
    public long ShiftPeriodId { get; set; }
    public int ProfileId { get; set; }
    public int? RoundId { get; set; }
    public DbLuaMProgressionCurrency Currency { get; set; }
    public string? TargetId { get; set; }
    public int Amount { get; set; }
    public string SourceType { get; set; } = string.Empty;
    public string SourceInstanceId { get; set; } = string.Empty;
    public string AwardCode { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string OperationIdentityKey { get; set; } = string.Empty;
    public long? ReversesLedgerId { get; set; }
    public int RulesetVersion { get; set; }
    public string PayloadJson { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

internal static class LuaMProgressionModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LuaMCampaignShift>(entity =>
        {
            entity.ToTable("luam_campaign_shift", table =>
            {
                table.HasCheckConstraint("CK_luam_shift_dates", "starts_at_utc < ends_at_utc");
                table.HasCheckConstraint("CK_luam_shift_status", "status >= 0 AND status <= 3");
                table.HasCheckConstraint("CK_luam_shift_revision", "revision >= 0");
            });
            entity.HasKey(value => value.ShiftPeriodId);
            entity.Property(value => value.ShiftPeriodId).ValueGeneratedNever();
            entity.Property(value => value.Revision).IsConcurrencyToken();
            entity.HasIndex(value => value.StartsAtUtc, "UX_luam_shift_start").IsUnique();
        });

        modelBuilder.Entity<LuaMCampaignShiftRun>(entity =>
        {
            entity.ToTable("luam_campaign_shift_run");
            entity.HasKey(value => new { value.ShiftPeriodId, value.RoundId });
            entity.HasOne<LuaMCampaignShift>().WithMany().HasForeignKey(value => value.ShiftPeriodId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(value => value.RoundId, "UX_luam_shift_run_round").IsUnique();
        });

        modelBuilder.Entity<LuaMCharacterCareer>(entity =>
        {
            entity.ToTable("luam_character_career", table =>
            {
                table.HasCheckConstraint("CK_luam_career_totals", "total_career_xp >= 0 AND credited_shift_count >= 0");
                table.HasCheckConstraint("CK_luam_career_level", "level >= 0 AND level <= 10");
                table.HasCheckConstraint("CK_luam_career_status", "status >= 0 AND status <= 6");
                table.HasCheckConstraint("CK_luam_career_revision", "revision >= 0");
            });
            entity.HasKey(value => value.ProfileId);
            entity.Property(value => value.ProfileId).ValueGeneratedNever();
            entity.Property(value => value.Revision).IsConcurrencyToken();
            entity.HasOne<Profile>().WithMany().HasForeignKey(value => value.ProfileId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LuaMCareerShiftParticipation>(entity =>
        {
            entity.ToTable("luam_career_shift_participation", table =>
            {
                table.HasCheckConstraint("CK_luam_participation_values", "preliminary_career_xp >= 0 AND result_career_xp >= 0 AND active_minutes >= 0 AND distinct_result_categories >= 0");
                table.HasCheckConstraint("CK_luam_participation_final", "final_career_xp >= 0 AND final_career_xp <= 100");
                table.HasCheckConstraint("CK_luam_participation_credit", "NOT is_credited OR is_eligible");
                table.HasCheckConstraint("CK_luam_participation_revision", "revision >= 0");
            });
            entity.HasKey(value => new { value.ShiftPeriodId, value.ProfileId });
            entity.Property(value => value.Revision).IsConcurrencyToken();
            entity.HasOne<LuaMCampaignShift>().WithMany().HasForeignKey(value => value.ShiftPeriodId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LuaMCharacterCareer>().WithMany().HasForeignKey(value => value.ProfileId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Preference>().WithMany().HasForeignKey(value => value.PreferenceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(value => new { value.ShiftPeriodId, value.PreferenceId }, "UX_luam_participation_focus")
                .IsUnique()
                .HasFilter("is_career_focus = TRUE");
            entity.HasIndex(value => new { value.ProfileId, value.ShiftPeriodId }, "IX_luam_participation_profile");
            entity.HasIndex(value => new { value.ShiftPeriodId, value.IsEligible }, "IX_luam_participation_eligible");
        });

        modelBuilder.Entity<LuaMCareerXpLedger>(entity =>
        {
            entity.ToTable("luam_career_xp_ledger", table =>
            {
                table.HasCheckConstraint("CK_luam_ledger_amount", "amount <> 0");
                table.HasCheckConstraint("CK_luam_ledger_currency", "currency >= 0 AND currency <= 2");
                table.HasCheckConstraint("CK_luam_ledger_ruleset", "ruleset_version > 0");
                table.HasCheckConstraint("CK_luam_ledger_key", "length(idempotency_key) = 64");
                table.HasCheckConstraint("CK_luam_ledger_operation_identity", "length(operation_identity_key) = 64");
                table.HasCheckConstraint("CK_luam_ledger_reversal", "reverses_ledger_id IS NULL OR reverses_ledger_id <> lua_m_career_xp_ledger_id");
            });
            entity.HasKey(value => value.Id);
            entity.Property(value => value.TargetId).HasMaxLength(256);
            entity.Property(value => value.SourceType).HasMaxLength(256);
            entity.Property(value => value.SourceInstanceId).HasMaxLength(256);
            entity.Property(value => value.AwardCode).HasMaxLength(256);
            entity.Property(value => value.IdempotencyKey).HasMaxLength(64);
            entity.Property(value => value.OperationIdentityKey).HasMaxLength(64);
            entity.HasOne<LuaMCareerShiftParticipation>().WithMany()
                .HasForeignKey(value => new { value.ShiftPeriodId, value.ProfileId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LuaMCareerXpLedger>().WithMany().HasForeignKey(value => value.ReversesLedgerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(value => value.IdempotencyKey, "UX_luam_ledger_idempotency").IsUnique();
            entity.HasIndex(value => value.ReversesLedgerId, "UX_luam_ledger_reversal").IsUnique();
            entity.HasIndex(value => new { value.ProfileId, value.CreatedAtUtc }, "IX_luam_ledger_profile_created");
            entity.HasIndex(value => new { value.ShiftPeriodId, value.ProfileId, value.Currency }, "IX_luam_ledger_shift_profile_currency");
        });
    }
}
