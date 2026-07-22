#nullable enable

using System.Collections.Generic;
using System.Linq;
using Content.Server._LuaM.Progression;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMProgressionBuildRulesTest
{
    private static readonly LuaMCharacterParameters Baseline = new(5, 5, 5, 5, 5, 5, 5, 5);

    [Test]
    public void InitialAllocationKeepsFortyPointsAndFourPointTransferCap()
    {
        var valid = new LuaMCharacterParameters(7, 7, 3, 3, 5, 5, 5, 5);
        var tooManyTransfers = new LuaMCharacterParameters(7, 7, 6, 3, 3, 3, 6, 5);

        Assert.Multiple(() =>
        {
            Assert.That(LuaMCharacterBuildRules.IsValidInitialAllocation(Baseline), Is.True);
            Assert.That(LuaMCharacterBuildRules.IsValidInitialAllocation(valid), Is.True);
            Assert.That(LuaMCharacterBuildRules.IsValidInitialAllocation(tooManyTransfers), Is.False);
            Assert.That(LuaMCharacterBuildRules.IsValidInitialAllocation(valid with { Strength = 8, Endurance = 2 }), Is.False);
        });
    }

    [Test]
    public void OriginsAreZeroSumAndCannotEscapeCreationBounds()
    {
        var shipborn = new Dictionary<LuaMCharacterParameter, int>
        {
            [LuaMCharacterParameter.Agility] = 1,
            [LuaMCharacterParameter.Perception] = 1,
            [LuaMCharacterParameter.Strength] = -1,
            [LuaMCharacterParameter.Charisma] = -1,
        };

        var applied = LuaMCharacterBuildRules.TryApplyOrigin(Baseline, shipborn, out var result);
        var invalid = new Dictionary<LuaMCharacterParameter, int>
        {
            [LuaMCharacterParameter.Strength] = 1,
        };

        Assert.Multiple(() =>
        {
            Assert.That(applied, Is.True);
            Assert.That(result.Total, Is.EqualTo(40));
            Assert.That(result.Agility, Is.EqualTo(6));
            Assert.That(result.Strength, Is.EqualTo(4));
            Assert.That(LuaMCharacterBuildRules.TryApplyOrigin(Baseline, invalid, out _), Is.False);
        });
    }

    [Test]
    public void EveryPublishedOriginIsUniqueZeroSumAndNarrativeOnly()
    {
        Assert.That(LuaMOriginCatalog.All, Has.Count.EqualTo(12));
        Assert.That(
            LuaMOriginCatalog.All.Select(origin => origin.Id).Distinct(StringComparer.Ordinal).ToArray(),
            Has.Length.EqualTo(LuaMOriginCatalog.All.Count));

        foreach (var origin in LuaMOriginCatalog.All)
        {
            Assert.Multiple(() =>
            {
                Assert.That(origin.Modifiers.Values.Sum(), Is.Zero, origin.Id);
                Assert.That(origin.Modifiers.Values, Is.All.InRange(-1, 1), origin.Id);
                Assert.That(
                    LuaMCharacterBuildRules.TryApplyOrigin(Baseline, origin.Modifiers, out var applied),
                    Is.True,
                    origin.Id);
                Assert.That(applied.Total, Is.EqualTo(40), origin.Id);
                Assert.That(origin.KnowledgeTag, Is.Not.Empty, origin.Id);
                Assert.That(origin.ContactTag, Is.Not.Empty, origin.Id);
                Assert.That(origin.LimitationTag, Is.Not.Empty, origin.Id);
            });
        }
    }

    [TestCase(3, 3, 3, -400)]
    [TestCase(5, 5, 5, 0)]
    [TestCase(7, 6, 7, 400)]
    [TestCase(8, 8, 8, 500)]
    public void DerivedRatingsAndEffectsUseIntegerBasisPoints(
        int primary,
        int secondary,
        int expectedRating,
        int expectedBasisPoints)
    {
        var rating = LuaMCharacterBuildRules.CalculateDerivedRating(primary, secondary);
        Assert.Multiple(() =>
        {
            Assert.That(rating, Is.EqualTo(expectedRating));
            Assert.That(LuaMCharacterBuildRules.CalculateEffectBasisPoints(rating), Is.EqualTo(expectedBasisPoints));
        });
    }

    [Test]
    public void ChecksAreDeterministicAndLicenseGated()
    {
        var success = LuaMCharacterBuildRules.EvaluateDeterministicCheck(7, 3, -1, 9, true);
        var unlicensed = LuaMCharacterBuildRules.EvaluateDeterministicCheck(8, 5, 2, 5, false);
        var story = LuaMCharacterBuildRules.EvaluateOptionalStoryCheck(6, 6, 7, 3, 0, 16, true);

        Assert.Multiple(() =>
        {
            Assert.That(success.Success, Is.True);
            Assert.That(success.Score, Is.EqualTo(9));
            Assert.That(unlicensed.Success, Is.False);
            Assert.That(story.Success, Is.True);
            Assert.That(story.Score, Is.EqualTo(22));
        });
    }

    [TestCase(0, 2)]
    [TestCase(5, 2)]
    [TestCase(6, 3)]
    [TestCase(10, 3)]
    public void ActiveSkillSlotsAreHardCapped(int level, int expected)
    {
        Assert.That(LuaMCharacterBuildRules.GetActiveSkillSlotLimit(level), Is.EqualTo(expected));
    }

    [Test]
    public void FlawsRequireImplementedTriggersAndRespectAllCaps()
    {
        var minor = Flaw("minor", LuaMFlawSeverity.Minor);
        var significant = Flaw("significant", LuaMFlawSeverity.Significant);
        var valid = LuaMBackgroundBudgetRules.ValidateFlaws(new[] { minor, significant });
        var missingTrigger = LuaMBackgroundBudgetRules.ValidateFlaws(new[]
        {
            Flaw("placeholder", LuaMFlawSeverity.Minor, hasTrigger: false),
        });
        var severeOverflow = LuaMBackgroundBudgetRules.ValidateFlaws(new[]
        {
            Flaw("severe-a", LuaMFlawSeverity.Severe),
            Flaw("severe-b", LuaMFlawSeverity.Severe),
        });
        var incompatible = LuaMBackgroundBudgetRules.ValidateFlaws(new[]
        {
            Flaw("a", LuaMFlawSeverity.Minor, incompatible: new HashSet<string> { "b" }),
            Flaw("b", LuaMFlawSeverity.Minor),
        });

        Assert.Multiple(() =>
        {
            Assert.That(valid.Valid, Is.True);
            Assert.That(valid.EarnedBackgroundPoints, Is.EqualTo(3));
            Assert.That(missingTrigger.Reason, Is.EqualTo("missing-server-trigger"));
            Assert.That(severeOverflow.Reason, Is.EqualTo("too-many-severe-flaws"));
            Assert.That(incompatible.Reason, Is.EqualTo("incompatible-flaws"));
            Assert.That(LuaMBackgroundBudgetRules.CanPurchase(new[] { 1, 2 }, 3), Is.True);
            Assert.That(LuaMBackgroundBudgetRules.CanPurchase(new[] { 2, 2, 1 }, 4), Is.False);
        });
    }

    [Test]
    public void LicenseExamUsesExactAttemptCooldownAndCriticalFailureRules()
    {
        var policy = LuaMLicenseExamPolicy.Default;
        var now = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);

        Assert.Multiple(() =>
        {
            Assert.That(LuaMLicenseRules.CanScheduleExam(policy, 0, now, null), Is.True);
            Assert.That(LuaMLicenseRules.CanScheduleExam(policy, 1, now, now.AddHours(-23)), Is.False);
            Assert.That(LuaMLicenseRules.CanScheduleExam(policy, 1, now, now.AddHours(-24)), Is.True);
            Assert.That(LuaMLicenseRules.CanScheduleExam(policy, 2, now, now.AddDays(-2)), Is.False);
            Assert.That(LuaMLicenseRules.HasPassedExam(policy, 80, 100, false), Is.True);
            Assert.That(LuaMLicenseRules.HasPassedExam(policy, 79, 100, false), Is.False);
            Assert.That(LuaMLicenseRules.HasPassedExam(policy, 100, 100, true), Is.False);
        });
    }

    [Test]
    public void MasteryUnlocksRequireLevelXpPointsAndPreviousTier()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LuaMMasteryRules.CanUnlock(LuaMSkillTier.One, 1, 50, 1, false), Is.True);
            Assert.That(LuaMMasteryRules.CanUnlock(LuaMSkillTier.Two, 3, 150, 1, false), Is.False);
            Assert.That(LuaMMasteryRules.CanUnlock(LuaMSkillTier.Two, 3, 150, 1, true), Is.True);
            Assert.That(LuaMMasteryRules.CanUnlock(LuaMSkillTier.Five, 10, 749, 3, true), Is.False);
            Assert.That(LuaMMasteryRules.CanUnlock(LuaMSkillTier.Five, 10, 750, 3, true), Is.True);
        });
    }

    [Test]
    public void MasteryAndPostCapRewardsHaveHardWeeklyBounds()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LuaMMasteryRules.ClampShiftMasteryXp(100, 100, 100), Is.EqualTo(40));
            Assert.That(LuaMMasteryRules.ClampShiftMasteryXp(5, 30, 20), Is.EqualTo(30));
            Assert.That(LuaMMasteryRules.GetEarnedTalentPoints(10), Is.EqualTo(10));
            Assert.That(LuaMMasteryRules.GetEarnedParameterPoints(2), Is.Zero);
            Assert.That(LuaMMasteryRules.GetEarnedParameterPoints(9), Is.EqualTo(3));
            Assert.That(LuaMMasteryRules.CalculateServiceStars(7000, 70), Is.Zero);
            Assert.That(LuaMMasteryRules.CalculateServiceStars(7700, 77), Is.EqualTo(1));
            Assert.That(LuaMMasteryRules.CalculateServiceStars(8400, 77), Is.EqualTo(1));
        });
    }

    private static LuaMFlawDefinition Flaw(
        string id,
        LuaMFlawSeverity severity,
        bool hasTrigger = true,
        IReadOnlySet<string>? incompatible = null)
    {
        return new LuaMFlawDefinition(
            id,
            severity,
            hasTrigger,
            incompatible ?? new HashSet<string>());
    }
}
