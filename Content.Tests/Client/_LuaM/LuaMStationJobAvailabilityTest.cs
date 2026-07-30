using System.Collections.Generic;
using Content.Client._NF.LateJoin;
using Content.Shared.GameTicking;
using Content.Shared.Roles;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.UnitTesting;

namespace Content.Tests.Client._LuaM;

[TestFixture]
public sealed class LuaMStationJobAvailabilityTest : RobustUnitTest
{
    public override UnitTestProject Project => UnitTestProject.Client;

    [Test]
    public void FullStationsAreNotAdvertisedAsAvailable()
    {
        var jobs = new Dictionary<NetEntity, StationJobInformation>
        {
            [new NetEntity(1)] = Station(isLateJoinStation: true, slots: 0),
        };

        Assert.That(StationJobInformationExtensions.IsAnyStationAvailable(jobs), Is.False);
        Assert.That(StationJobInformationExtensions.IsAnyCrewJobAvailable(jobs), Is.False);
    }

    [TestCase(null)]
    [TestCase(1)]
    public void UnlimitedOrPositiveSlotsAreAvailable(int? slots)
    {
        var jobs = new Dictionary<NetEntity, StationJobInformation>
        {
            [new NetEntity(1)] = Station(isLateJoinStation: true, slots),
            [new NetEntity(2)] = Station(isLateJoinStation: false, slots),
        };

        Assert.That(StationJobInformationExtensions.IsAnyStationAvailable(jobs), Is.True);
        Assert.That(StationJobInformationExtensions.IsAnyCrewJobAvailable(jobs), Is.True);
    }

    private static StationJobInformation Station(bool isLateJoinStation, int? slots)
    {
        return new StationJobInformation(
            "Test",
            new Dictionary<ProtoId<JobPrototype>, int?>
            {
                [new ProtoId<JobPrototype>("Assistant")] = slots,
            },
            isLateJoinStation,
            null,
            null);
    }
}
