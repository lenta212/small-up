using Content.Client._Mono.Radar;
using NUnit.Framework;

namespace Content.Tests.Client._LuaM;

[TestFixture]
public sealed class LuaMRadarSequenceTest
{
    [Test]
    public void DelayedButNewResponseIsAccepted()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RadarBlipsSystem.ShouldAcceptResponse(10, 11, true, 9), Is.True);
            Assert.That(RadarBlipsSystem.ShouldAcceptResponse(9, 11, true, 10), Is.False);
            Assert.That(RadarBlipsSystem.ShouldAcceptResponse(12, 11, true, 10), Is.False);
            Assert.That(RadarBlipsSystem.ShouldAcceptResponse(10, 11, true, 10), Is.False);
        });
    }

    [Test]
    public void RequestSequenceComparisonHandlesWraparound()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RadarBlipsSystem.IsNewerRequestId(1, uint.MaxValue), Is.True);
            Assert.That(RadarBlipsSystem.ShouldAcceptResponse(uint.MaxValue, 1, true, uint.MaxValue - 1), Is.True);
            Assert.That(RadarBlipsSystem.ShouldAcceptResponse(uint.MaxValue - 1, 1, true, uint.MaxValue), Is.False);
        });
    }
}
