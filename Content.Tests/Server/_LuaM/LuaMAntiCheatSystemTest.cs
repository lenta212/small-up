#nullable enable

using System;
using Content.Server._LuaM.AntiCheat;
using Content.Server._RMC14.Weapons.Ranged.Prediction;
using Content.Shared._LuaM.AntiCheat;
using NUnit.Framework;

namespace Content.Tests.Server._LuaM;

[TestFixture]
[TestOf(typeof(LuaMAntiCheatSystem))]
public sealed class LuaMAntiCheatSystemTest
{
    [Test]
    public void DecayScoreUsesElapsedMinutesAndNeverDropsBelowZero()
    {
        Assert.That(
            LuaMAntiCheatSystem.DecayScore(10f, TimeSpan.FromSeconds(90), 2f),
            Is.EqualTo(7f).Within(0.0001f));

        Assert.That(
            LuaMAntiCheatSystem.DecayScore(3f, TimeSpan.FromMinutes(2), 2f),
            Is.Zero);
    }

    [TestCase(8f, 0, 3f, 8f, TestName = "Zero elapsed time does not decay")]
    [TestCase(8f, -60, 3f, 8f, TestName = "Negative elapsed time does not decay")]
    [TestCase(8f, 60, 0f, 8f, TestName = "Zero decay rate preserves the score")]
    [TestCase(8f, 60, -3f, 8f, TestName = "Negative decay rate preserves the score")]
    [TestCase(-2f, 60, 3f, 0f, TestName = "Negative input score is normalized to zero")]
    public void DecayScoreHandlesBoundaryInputs(
        float score,
        int elapsedSeconds,
        float decayPerMinute,
        float expected)
    {
        Assert.That(
            LuaMAntiCheatSystem.DecayScore(score, TimeSpan.FromSeconds(elapsedSeconds), decayPerMinute),
            Is.EqualTo(expected).Within(0.0001f));
    }

    [TestCase(LuaMAntiCheatSignalKind.RemoteBoundUi, 5f)]
    [TestCase(LuaMAntiCheatSignalKind.BoundUiRateLimit, 3f)]
    [TestCase(LuaMAntiCheatSignalKind.InvalidShootCoordinates, 5f)]
    [TestCase(LuaMAntiCheatSignalKind.InvalidShootTarget, 4f)]
    [TestCase(LuaMAntiCheatSignalKind.PredictedHitFlood, 4f)]
    public void GetWeightAssignsExpectedSeverity(LuaMAntiCheatSignalKind kind, float expected)
    {
        Assert.That(LuaMAntiCheatSystem.GetWeight(kind), Is.EqualTo(expected));
    }

    [Test]
    public void GetWeightUsesConservativeFallbackForUnknownSignal()
    {
        Assert.That(LuaMAntiCheatSystem.GetWeight((LuaMAntiCheatSignalKind) byte.MaxValue), Is.EqualTo(1f));
    }

    [TestCase(1, 0, 16, 64, true)]
    [TestCase(16, 63, 16, 64, true)]
    [TestCase(17, 0, 16, 64, false)]
    [TestCase(1, 64, 16, 64, false)]
    [TestCase(0, 0, 0, 0, false)]
    public void PredictedHitReportsHaveBoundedCandidatesAndPerTickEvents(
        int candidates,
        int acceptedEvents,
        int candidateLimit,
        int eventLimit,
        bool expected)
    {
        Assert.That(
            GunPredictionSystem.IsPredictedHitRequestAllowed(
                candidates,
                acceptedEvents,
                candidateLimit,
                eventLimit),
            Is.EqualTo(expected));
    }
}
