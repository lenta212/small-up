using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Content.Server._LuaM.Progression;

/// <summary>
/// Server-only progression invariants. No XP formula or award authority is exposed
/// through Content.Shared or client resources.
/// </summary>
public static class LuaMCareerProgressionRules
{
    public const int MaximumCareerLevel = 10;
    public const int MaximumCareerXpPerShift = 100;
    public const int CareerXpPerLevel = 700;
    public const int CreditedShiftsPerLevel = 7;

    public static int ClampShiftXp(int rawXp)
    {
        return Math.Clamp(rawXp, 0, MaximumCareerXpPerShift);
    }

    public static int CalculateLevel(long totalCareerXp, int creditedShiftCount)
    {
        if (totalCareerXp < 0)
            throw new ArgumentOutOfRangeException(nameof(totalCareerXp));

        if (creditedShiftCount < 0)
            throw new ArgumentOutOfRangeException(nameof(creditedShiftCount));

        var levelByXp = totalCareerXp / CareerXpPerLevel;
        var levelByShifts = creditedShiftCount / CreditedShiftsPerLevel;
        return (int) Math.Min(MaximumCareerLevel, Math.Min(levelByXp, levelByShifts));
    }
}

/// <summary>
/// Canonical source identity for an append-only career ledger entry.
/// Replaying the same confirmed event produces the same key and cannot pay twice
/// once the database adds its required unique constraint.
/// </summary>
public readonly record struct LuaMCareerAwardIdentity(
    LuaMCampaignShiftId ShiftId,
    int ProfileId,
    string SourceType,
    string SourceInstanceId,
    string AwardCode)
{
    public string CreateIdempotencyKey()
    {
        if (ProfileId <= 0)
            throw new ArgumentOutOfRangeException(nameof(ProfileId));

        ValidatePart(SourceType, nameof(SourceType));
        ValidatePart(SourceInstanceId, nameof(SourceInstanceId));
        ValidatePart(AwardCode, nameof(AwardCode));

        var canonical = new StringBuilder()
            .Append(ShiftId.Value.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(ProfileId.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(SourceType.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(SourceType).Append('\n')
            .Append(SourceInstanceId.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(SourceInstanceId).Append('\n')
            .Append(AwardCode.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(AwardCode)
            .ToString();

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void ValidatePart(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Award identity fields cannot be empty.", parameterName);

        if (value.Length > 256)
            throw new ArgumentOutOfRangeException(parameterName, "Award identity fields are limited to 256 characters.");
    }
}
