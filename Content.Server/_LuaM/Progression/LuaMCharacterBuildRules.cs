using System;
using System.Collections.Generic;
using System.Linq;

namespace Content.Server._LuaM.Progression;

public enum LuaMCharacterParameter
{
    Strength,
    Agility,
    Endurance,
    Intelligence,
    Perception,
    Willpower,
    Charisma,
    Luck,
}

/// <summary>
/// Server-authoritative values for the eight non-combat progression parameters.
/// </summary>
public readonly record struct LuaMCharacterParameters(
    int Strength,
    int Agility,
    int Endurance,
    int Intelligence,
    int Perception,
    int Willpower,
    int Charisma,
    int Luck)
{
    public const int ParameterCount = 8;

    public int Total =>
        Strength + Agility + Endurance + Intelligence +
        Perception + Willpower + Charisma + Luck;

    public int this[LuaMCharacterParameter parameter] => parameter switch
    {
        LuaMCharacterParameter.Strength => Strength,
        LuaMCharacterParameter.Agility => Agility,
        LuaMCharacterParameter.Endurance => Endurance,
        LuaMCharacterParameter.Intelligence => Intelligence,
        LuaMCharacterParameter.Perception => Perception,
        LuaMCharacterParameter.Willpower => Willpower,
        LuaMCharacterParameter.Charisma => Charisma,
        LuaMCharacterParameter.Luck => Luck,
        _ => throw new ArgumentOutOfRangeException(nameof(parameter)),
    };

    public LuaMCharacterParameters With(LuaMCharacterParameter parameter, int value)
    {
        return parameter switch
        {
            LuaMCharacterParameter.Strength => this with { Strength = value },
            LuaMCharacterParameter.Agility => this with { Agility = value },
            LuaMCharacterParameter.Endurance => this with { Endurance = value },
            LuaMCharacterParameter.Intelligence => this with { Intelligence = value },
            LuaMCharacterParameter.Perception => this with { Perception = value },
            LuaMCharacterParameter.Willpower => this with { Willpower = value },
            LuaMCharacterParameter.Charisma => this with { Charisma = value },
            LuaMCharacterParameter.Luck => this with { Luck = value },
            _ => throw new ArgumentOutOfRangeException(nameof(parameter)),
        };
    }

    public IEnumerable<int> Values()
    {
        yield return Strength;
        yield return Agility;
        yield return Endurance;
        yield return Intelligence;
        yield return Perception;
        yield return Willpower;
        yield return Charisma;
        yield return Luck;
    }
}

public readonly record struct LuaMProgressionCheckResult(
    bool Success,
    int Score,
    int Difficulty,
    bool LicenseSatisfied);

public static class LuaMCharacterBuildRules
{
    public const int InitialParameterValue = 5;
    public const int InitialParameterTotal = 40;
    public const int InitialMinimumParameter = 3;
    public const int InitialMaximumParameter = 7;
    public const int MaximumInitialTransferredPoints = 4;
    public const int ProgressedMaximumParameter = 8;
    public const int MaximumEarnedParameterPoints = 3;
    public const int MinimumDerivedRating = 3;
    public const int MaximumDerivedRating = 8;
    public const int MinimumContextModifier = -2;
    public const int MaximumContextModifier = 2;
    public const int MaximumSkillTier = 5;

    public static bool IsValidInitialAllocation(LuaMCharacterParameters parameters)
    {
        if (parameters.Total != InitialParameterTotal ||
            parameters.Values().Any(value => value is < InitialMinimumParameter or > InitialMaximumParameter))
        {
            return false;
        }

        var movedPoints = parameters.Values()
            .Where(value => value > InitialParameterValue)
            .Sum(value => value - InitialParameterValue);
        var removedPoints = parameters.Values()
            .Where(value => value < InitialParameterValue)
            .Sum(value => InitialParameterValue - value);

        return movedPoints == removedPoints && movedPoints <= MaximumInitialTransferredPoints;
    }

    public static bool TryApplyOrigin(
        LuaMCharacterParameters allocation,
        IReadOnlyDictionary<LuaMCharacterParameter, int> modifiers,
        out LuaMCharacterParameters result)
    {
        result = allocation;
        if (!IsValidInitialAllocation(allocation) ||
            modifiers.Values.Sum() != 0 ||
            modifiers.Any(pair => pair.Value is < -1 or > 1))
        {
            return false;
        }

        foreach (var (parameter, modifier) in modifiers.OrderBy(pair => pair.Key))
            result = result.With(parameter, checked(result[parameter] + modifier));

        return result.Total == InitialParameterTotal &&
               result.Values().All(value => value is >= InitialMinimumParameter and <= InitialMaximumParameter);
    }

    public static bool IsValidProgressedAllocation(
        LuaMCharacterParameters parameters,
        int earnedParameterPoints)
    {
        return earnedParameterPoints is >= 0 and <= MaximumEarnedParameterPoints &&
               parameters.Total == InitialParameterTotal + earnedParameterPoints &&
               parameters.Values().All(value => value is >= InitialMinimumParameter and <= ProgressedMaximumParameter);
    }

    public static int CalculateDerivedRating(int primary, int secondary)
    {
        ValidateParameter(primary, nameof(primary));
        ValidateParameter(secondary, nameof(secondary));
        var rating = (2 * primary + secondary + 1) / 3;
        return Math.Clamp(rating, MinimumDerivedRating, MaximumDerivedRating);
    }

    public static int CalculateEffectBasisPoints(int rating)
    {
        if (rating is < MinimumDerivedRating or > MaximumDerivedRating)
            throw new ArgumentOutOfRangeException(nameof(rating));

        return Math.Clamp((rating - InitialParameterValue) * 200, -400, 500);
    }

    public static LuaMProgressionCheckResult EvaluateDeterministicCheck(
        int rating,
        int skillTier,
        int contextModifier,
        int difficulty,
        bool hasRequiredLicense)
    {
        ValidateCheckInputs(rating, skillTier, contextModifier, difficulty);
        var score = checked(rating + skillTier + contextModifier);
        return new LuaMProgressionCheckResult(
            hasRequiredLicense && score >= difficulty,
            score,
            difficulty,
            hasRequiredLicense);
    }

    public static LuaMProgressionCheckResult EvaluateOptionalStoryCheck(
        int firstDie,
        int secondDie,
        int rating,
        int skillTier,
        int contextModifier,
        int difficulty,
        bool hasRequiredLicense)
    {
        if (firstDie is < 1 or > 6)
            throw new ArgumentOutOfRangeException(nameof(firstDie));
        if (secondDie is < 1 or > 6)
            throw new ArgumentOutOfRangeException(nameof(secondDie));

        ValidateCheckInputs(rating, skillTier, contextModifier, difficulty);
        var score = checked(firstDie + secondDie + rating + skillTier + contextModifier);
        return new LuaMProgressionCheckResult(
            hasRequiredLicense && score >= difficulty,
            score,
            difficulty,
            hasRequiredLicense);
    }

    public static int GetActiveSkillSlotLimit(int careerLevel)
    {
        if (careerLevel is < 0 or > LuaMCareerProgressionRules.MaximumCareerLevel)
            throw new ArgumentOutOfRangeException(nameof(careerLevel));

        return careerLevel >= 6 ? 3 : 2;
    }

    private static void ValidateParameter(int value, string parameterName)
    {
        if (value is < InitialMinimumParameter or > ProgressedMaximumParameter)
            throw new ArgumentOutOfRangeException(parameterName);
    }

    private static void ValidateCheckInputs(
        int rating,
        int skillTier,
        int contextModifier,
        int difficulty)
    {
        if (rating is < MinimumDerivedRating or > MaximumDerivedRating)
            throw new ArgumentOutOfRangeException(nameof(rating));
        if (skillTier is < 0 or > MaximumSkillTier)
            throw new ArgumentOutOfRangeException(nameof(skillTier));
        if (contextModifier is < MinimumContextModifier or > MaximumContextModifier)
            throw new ArgumentOutOfRangeException(nameof(contextModifier));
        if (difficulty <= 0)
            throw new ArgumentOutOfRangeException(nameof(difficulty));
    }
}

public enum LuaMFlawSeverity
{
    Minor = 1,
    Significant = 2,
    Severe = 3,
}

public sealed record LuaMFlawDefinition(
    string Id,
    LuaMFlawSeverity Severity,
    bool HasServerTrigger,
    IReadOnlySet<string> IncompatibleIds);

public readonly record struct LuaMFlawSelectionResult(
    bool Valid,
    int EarnedBackgroundPoints,
    string? Reason);

public static class LuaMBackgroundBudgetRules
{
    public const int MaximumBackgroundPoints = 4;
    public const int MaximumFlawCount = 3;
    public const int MaximumSevereFlawCount = 1;

    public static LuaMFlawSelectionResult ValidateFlaws(
        IReadOnlyCollection<LuaMFlawDefinition> flaws)
    {
        if (flaws.Count > MaximumFlawCount)
            return new LuaMFlawSelectionResult(false, 0, "too-many-flaws");

        if (flaws.Select(flaw => flaw.Id).Distinct(StringComparer.Ordinal).Count() != flaws.Count)
            return new LuaMFlawSelectionResult(false, 0, "duplicate-flaw");

        if (flaws.Any(flaw => string.IsNullOrWhiteSpace(flaw.Id)))
            return new LuaMFlawSelectionResult(false, 0, "empty-flaw-id");

        if (flaws.Any(flaw => !flaw.HasServerTrigger))
            return new LuaMFlawSelectionResult(false, 0, "missing-server-trigger");

        if (flaws.Count(flaw => flaw.Severity == LuaMFlawSeverity.Severe) > MaximumSevereFlawCount)
            return new LuaMFlawSelectionResult(false, 0, "too-many-severe-flaws");

        var ids = flaws.Select(flaw => flaw.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var flaw in flaws)
        {
            if (flaw.IncompatibleIds.Any(ids.Contains))
                return new LuaMFlawSelectionResult(false, 0, "incompatible-flaws");
        }

        var points = flaws.Sum(flaw => (int) flaw.Severity);
        if (points > MaximumBackgroundPoints)
            return new LuaMFlawSelectionResult(false, 0, "background-point-cap");

        return new LuaMFlawSelectionResult(true, points, null);
    }

    public static bool CanPurchase(IReadOnlyCollection<int> costs, int earnedBackgroundPoints)
    {
        if (earnedBackgroundPoints is < 0 or > MaximumBackgroundPoints ||
            costs.Any(cost => cost is < 1 or > 2))
        {
            return false;
        }

        return costs.Sum() <= earnedBackgroundPoints;
    }
}

public readonly record struct LuaMLicenseExamPolicy(
    int PassingPercent,
    int MaximumAttemptsPerShift,
    TimeSpan MinimumAttemptInterval)
{
    public static LuaMLicenseExamPolicy Default => new(80, 2, TimeSpan.FromHours(24));
}

public static class LuaMLicenseRules
{
    public static bool CanScheduleExam(
        LuaMLicenseExamPolicy policy,
        int completedAttemptsThisShift,
        DateTimeOffset now,
        DateTimeOffset? lastAttemptAt)
    {
        ValidatePolicy(policy);
        if (completedAttemptsThisShift < 0)
            throw new ArgumentOutOfRangeException(nameof(completedAttemptsThisShift));

        if (completedAttemptsThisShift >= policy.MaximumAttemptsPerShift)
            return false;

        return lastAttemptAt == null ||
               now.ToUniversalTime() - lastAttemptAt.Value.ToUniversalTime() >= policy.MinimumAttemptInterval;
    }

    public static bool HasPassedExam(
        LuaMLicenseExamPolicy policy,
        int earnedPoints,
        int availablePoints,
        bool hasCriticalError)
    {
        ValidatePolicy(policy);
        if (earnedPoints < 0)
            throw new ArgumentOutOfRangeException(nameof(earnedPoints));
        if (availablePoints <= 0 || earnedPoints > availablePoints)
            throw new ArgumentOutOfRangeException(nameof(availablePoints));

        return !hasCriticalError &&
               (long) earnedPoints * 100 >= (long) availablePoints * policy.PassingPercent;
    }

    private static void ValidatePolicy(LuaMLicenseExamPolicy policy)
    {
        if (policy.PassingPercent is < 1 or > 100 ||
            policy.MaximumAttemptsPerShift <= 0 ||
            policy.MinimumAttemptInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }
    }
}

public enum LuaMSkillTier
{
    One = 1,
    Two = 2,
    Three = 3,
    Four = 4,
    Five = 5,
}

public readonly record struct LuaMSkillUnlockRequirement(
    int RequiredCareerLevel,
    int RequiredMasteryXp,
    int TalentPointCost,
    bool RequiresPreviousTier);

public static class LuaMMasteryRules
{
    public const int MaximumMasteryXpPerCareerPerShift = 40;
    public const int MaximumUniqueAchievementXpPerShift = 20;
    public const int MaximumMentoringXpPerShift = 5;

    public static LuaMSkillUnlockRequirement GetRequirement(LuaMSkillTier tier)
    {
        return tier switch
        {
            LuaMSkillTier.One => new LuaMSkillUnlockRequirement(1, 50, 1, false),
            LuaMSkillTier.Two => new LuaMSkillUnlockRequirement(3, 150, 1, true),
            LuaMSkillTier.Three => new LuaMSkillUnlockRequirement(5, 300, 2, true),
            LuaMSkillTier.Four => new LuaMSkillUnlockRequirement(7, 500, 2, true),
            LuaMSkillTier.Five => new LuaMSkillUnlockRequirement(10, 750, 3, true),
            _ => throw new ArgumentOutOfRangeException(nameof(tier)),
        };
    }

    public static bool CanUnlock(
        LuaMSkillTier tier,
        int careerLevel,
        int careerMasteryXp,
        int availableTalentPoints,
        bool hasPreviousTier)
    {
        if (careerLevel is < 0 or > LuaMCareerProgressionRules.MaximumCareerLevel)
            throw new ArgumentOutOfRangeException(nameof(careerLevel));
        if (careerMasteryXp < 0)
            throw new ArgumentOutOfRangeException(nameof(careerMasteryXp));
        if (availableTalentPoints < 0)
            throw new ArgumentOutOfRangeException(nameof(availableTalentPoints));

        var requirement = GetRequirement(tier);
        return careerLevel >= requirement.RequiredCareerLevel &&
               careerMasteryXp >= requirement.RequiredMasteryXp &&
               availableTalentPoints >= requirement.TalentPointCost &&
               (!requirement.RequiresPreviousTier || hasPreviousTier);
    }

    public static int ClampShiftMasteryXp(
        int taskXp,
        int uniqueAchievementXp,
        int mentoringXp)
    {
        if (taskXp < 0 || uniqueAchievementXp < 0 || mentoringXp < 0)
            throw new ArgumentOutOfRangeException(nameof(taskXp));

        var boundedUnique = Math.Min(uniqueAchievementXp, MaximumUniqueAchievementXpPerShift);
        var boundedMentoring = Math.Min(mentoringXp, MaximumMentoringXpPerShift);
        var total = (long) taskXp + boundedUnique + boundedMentoring;
        return (int) Math.Min(MaximumMasteryXpPerCareerPerShift, total);
    }

    public static int GetEarnedTalentPoints(int careerLevel)
    {
        if (careerLevel is < 0 or > LuaMCareerProgressionRules.MaximumCareerLevel)
            throw new ArgumentOutOfRangeException(nameof(careerLevel));

        return careerLevel;
    }

    public static int GetEarnedParameterPoints(int careerLevel)
    {
        if (careerLevel is < 0 or > LuaMCareerProgressionRules.MaximumCareerLevel)
            throw new ArgumentOutOfRangeException(nameof(careerLevel));

        return Math.Min(3, careerLevel / 3);
    }

    public static int CalculateServiceStars(long totalCareerXp, int creditedShiftCount)
    {
        if (totalCareerXp < 0)
            throw new ArgumentOutOfRangeException(nameof(totalCareerXp));
        if (creditedShiftCount < 0)
            throw new ArgumentOutOfRangeException(nameof(creditedShiftCount));

        var milestones = Math.Min(
            totalCareerXp / LuaMCareerProgressionRules.CareerXpPerLevel,
            creditedShiftCount / LuaMCareerProgressionRules.CreditedShiftsPerLevel);
        return checked((int) Math.Max(0, milestones - LuaMCareerProgressionRules.MaximumCareerLevel));
    }
}
