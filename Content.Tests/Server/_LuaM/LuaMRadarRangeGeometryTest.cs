using System.Numerics;
using Content.Server._Mono.Radar;
using NUnit.Framework;

namespace Content.Tests.Server._LuaM;

[TestFixture]
public sealed class LuaMRadarRangeGeometryTest
{
    [Test]
    public void HitscanRangeUsesTheWholeSegment()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                RadarBlipSystem.IsSegmentWithinRange(
                    new Vector2(-20f, 0f),
                    new Vector2(20f, 0f),
                    Vector2.Zero,
                    5f),
                Is.True,
                "A beam crossing radar range must be reported even when both endpoints are outside.");
            Assert.That(
                RadarBlipSystem.IsSegmentWithinRange(
                    new Vector2(20f, 0f),
                    new Vector2(4f, 0f),
                    Vector2.Zero,
                    5f),
                Is.True,
                "A beam with one endpoint in range must be reported.");
            Assert.That(
                RadarBlipSystem.IsSegmentWithinRange(
                    new Vector2(20f, 10f),
                    new Vector2(30f, 10f),
                    Vector2.Zero,
                    5f),
                Is.False,
                "A beam wholly outside radar range must be rejected.");
            Assert.That(
                RadarBlipSystem.IsSegmentWithinRange(
                    new Vector2(3f, 4f),
                    new Vector2(3f, 4f),
                    Vector2.Zero,
                    5f),
                Is.True,
                "A zero-length signature exactly on the range boundary must be accepted.");
            Assert.That(
                RadarBlipSystem.IsSegmentWithinRange(
                    new Vector2(3f, 4f),
                    new Vector2(3f, 4f),
                    Vector2.Zero,
                    -1f),
                Is.False,
                "Negative configured range must be clamped to zero.");
        });
    }
}
