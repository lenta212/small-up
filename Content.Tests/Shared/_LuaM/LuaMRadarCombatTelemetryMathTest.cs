using System.Numerics;
using Content.Shared._Mono.Radar;
using NUnit.Framework;
using Robust.Shared.Maths;

namespace Content.Tests.Shared._LuaM;

[TestFixture]
public sealed class LuaMRadarCombatTelemetryMathTest
{
    private static readonly Box2 ShipBounds = new(-2f, -1f, 2f, 1f);

    [Test]
    public void IncomingIntersectionUsesShipBoundsAndRejectsNearMisses()
    {
        var incoming = RadarCombatTelemetryMath.TryGetIncomingIntersectionTime(
            new Vector2(-10f, 0f),
            new Vector2(2f, 0f),
            ShipBounds,
            10f,
            out var impactTime);
        var nearMiss = RadarCombatTelemetryMath.TryGetIncomingIntersectionTime(
            new Vector2(-10f, 2f),
            new Vector2(2f, 0f),
            ShipBounds,
            10f,
            out _);
        var receding = RadarCombatTelemetryMath.TryGetIncomingIntersectionTime(
            new Vector2(-10f, 0f),
            new Vector2(-2f, 0f),
            ShipBounds,
            10f,
            out _);

        Assert.Multiple(() =>
        {
            Assert.That(incoming, Is.True);
            Assert.That(impactTime, Is.EqualTo(4f).Within(0.001f));
            Assert.That(nearMiss, Is.False);
            Assert.That(receding, Is.False);
        });
    }

    [Test]
    public void HitSectionsFollowShipLocalAxes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                RadarCombatTelemetryMath.GetHitSection(new Vector2(0f, 0.9f), ShipBounds),
                Is.EqualTo(ShipHitSection.Bow));
            Assert.That(
                RadarCombatTelemetryMath.GetHitSection(new Vector2(0f, -0.9f), ShipBounds),
                Is.EqualTo(ShipHitSection.Stern));
            Assert.That(
                RadarCombatTelemetryMath.GetHitSection(new Vector2(-1.9f, 0f), ShipBounds),
                Is.EqualTo(ShipHitSection.Port));
            Assert.That(
                RadarCombatTelemetryMath.GetHitSection(new Vector2(1.9f, 0f), ShipBounds),
                Is.EqualTo(ShipHitSection.Starboard));
            Assert.That(
                RadarCombatTelemetryMath.GetHitSection(new Vector2(1.5f, 0.9f), ShipBounds),
                Is.EqualTo(ShipHitSection.Bow),
                "Section wedges must account for non-square hull bounds.");
        });
    }

    [Test]
    public void BrakingDistanceIsFiniteAndFailsClosedWithoutThrust()
    {
        var valid = RadarCombatTelemetryMath.TryCalculateBrakingDistance(10f, 2f, out var distance);
        var noThrust = RadarCombatTelemetryMath.TryCalculateBrakingDistance(10f, 0f, out _);
        var stationary = RadarCombatTelemetryMath.TryCalculateBrakingDistance(0f, 0f, out var stationaryDistance);
        var invalid = RadarCombatTelemetryMath.TryCalculateBrakingDistance(float.NaN, 2f, out _);

        Assert.Multiple(() =>
        {
            Assert.That(valid, Is.True);
            Assert.That(distance, Is.EqualTo(25f).Within(0.001f));
            Assert.That(noThrust, Is.False);
            Assert.That(stationary, Is.True);
            Assert.That(stationaryDistance, Is.Zero);
            Assert.That(invalid, Is.False);
        });
    }
}
