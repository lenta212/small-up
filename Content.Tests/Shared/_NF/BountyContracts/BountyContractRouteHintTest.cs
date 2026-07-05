using Content.Shared._NF.BountyContracts;
using NUnit.Framework;

namespace Content.Tests.Shared._NF.BountyContracts;

[TestFixture]
public sealed class BountyContractRouteHintTest
{
    [TestCase("Fly to GPS 12, 44 and inspect the marker.")]
    [TestCase("Route marker is available on the sector map.")]
    [TestCase("Летите на координаты 12, 44 и проверьте сигнал.")]
    [TestCase("На карте сектора отмечена метка цели.")]
    [TestCase("Активный маяк показывает маршрут к цели.")]
    public void RouteHintsAreRecognized(string description)
    {
        Assert.That(SharedBountyContractSystem.HasRouteHint(description), Is.True);
    }

    [Test]
    public void EmptyRouteHintIsNotRecognized()
    {
        Assert.That(SharedBountyContractSystem.HasRouteHint(null), Is.False);
        Assert.That(SharedBountyContractSystem.HasRouteHint(string.Empty), Is.False);
        Assert.That(SharedBountyContractSystem.HasRouteHint("Deliver the sealed crate."), Is.False);
    }
}
