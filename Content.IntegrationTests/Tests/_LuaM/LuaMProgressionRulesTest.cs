#nullable enable

using System;
using System.Globalization;
using Content.Server._LuaM.Progression;
using NUnit.Framework;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMProgressionRulesTest
{
    private static readonly DateTimeOffset Epoch = new(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public void TechnicalRestartsStayInsideTheSameCampaignShift()
    {
        var beforeRestart = LuaMCampaignShiftClock.Resolve(Epoch.AddHours(6), Epoch);
        var afterRestart = LuaMCampaignShiftClock.Resolve(Epoch.AddDays(6).AddHours(23), Epoch);

        Assert.That(afterRestart.Id, Is.EqualTo(beforeRestart.Id));
        Assert.That(beforeRestart.Contains(Epoch.AddDays(7)), Is.False);
    }

    [Test]
    public void CampaignShiftBoundaryIsExactlySevenDays()
    {
        var first = LuaMCampaignShiftClock.Resolve(Epoch, Epoch);
        var next = LuaMCampaignShiftClock.Resolve(Epoch.AddDays(7), Epoch);
        var prior = LuaMCampaignShiftClock.Resolve(Epoch.AddTicks(-1), Epoch);

        Assert.Multiple(() =>
        {
            Assert.That(first.Id.Value, Is.Zero);
            Assert.That(next.Id.Value, Is.EqualTo(1));
            Assert.That(prior.Id.Value, Is.EqualTo(-1));
            Assert.That(first.EndsAtUtc - first.StartsAtUtc, Is.EqualTo(TimeSpan.FromDays(7)));
        });
    }

    [TestCase(699L, 7, 0)]
    [TestCase(700L, 6, 0)]
    [TestCase(700L, 7, 1)]
    [TestCase(7000L, 70, 10)]
    [TestCase(14000L, 140, 10)]
    public void CareerLevelRequiresBothXpAndCreditedShifts(long xp, int shifts, int expected)
    {
        Assert.That(LuaMCareerProgressionRules.CalculateLevel(xp, shifts), Is.EqualTo(expected));
    }

    [TestCase(-1, 0)]
    [TestCase(0, 0)]
    [TestCase(60, 60)]
    [TestCase(100, 100)]
    [TestCase(105, 100)]
    public void ShiftXpHasHardBounds(int rawXp, int expected)
    {
        Assert.That(LuaMCareerProgressionRules.ClampShiftXp(rawXp), Is.EqualTo(expected));
    }

    [Test]
    public void AwardIdentityIsStableAndSeparatesSources()
    {
        var identity = new LuaMCareerAwardIdentity(
            new LuaMCampaignShiftId(42),
            123,
            "rescue",
            "incident-9",
            "patient-stabilized");
        var same = identity.CreateIdempotencyKey();
        var different = identity with { SourceInstanceId = "incident-10" };

        Assert.Multiple(() =>
        {
            Assert.That(same, Has.Length.EqualTo(64));
            Assert.That(identity.CreateIdempotencyKey(), Is.EqualTo(same));
            Assert.That(different.CreateIdempotencyKey(), Is.Not.EqualTo(same));
        });
    }

    [Test]
    [NonParallelizable]
    public void AwardIdentityDoesNotDependOnCurrentCulture()
    {
        var identity = new LuaMCareerAwardIdentity(
            new LuaMCampaignShiftId(-42),
            123,
            "contract",
            "result-7",
            "closed");
        var originalCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var invariant = identity.CreateIdempotencyKey();
            var custom = (CultureInfo) CultureInfo.InvariantCulture.Clone();
            custom.NumberFormat.NegativeSign = "~";
            CultureInfo.CurrentCulture = custom;
            Assert.That(identity.CreateIdempotencyKey(), Is.EqualTo(invariant));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Test]
    [NonParallelizable]
    public void CampaignShiftIdDoesNotDependOnCurrentCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;

        try
        {
            var custom = (CultureInfo) CultureInfo.InvariantCulture.Clone();
            custom.NumberFormat.NegativeSign = "~";
            CultureInfo.CurrentCulture = custom;
            Assert.That(new LuaMCampaignShiftId(-42).ToString(), Is.EqualTo("campaign-shift--00000042"));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
