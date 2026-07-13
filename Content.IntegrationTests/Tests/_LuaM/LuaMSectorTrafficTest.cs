using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._LuaM.Sector;
using Content.Server._Mono.Radar;
using Content.Shared._LuaM.Sector;
using Content.Shared.GameTicking;
using Content.Shared.Shuttles.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(LuaMSectorTrafficSystem))]
public sealed class LuaMSectorTrafficTest
{
    [Test]
    public async Task TrafficContactsMoveAndRemainHardCapped()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var mapSystem = entManager.System<SharedMapSystem>();
        var traffic = entManager.System<LuaMSectorTrafficSystem>();

        MapId mapId = default;
        var mapCreated = false;
        IReadOnlyList<EntityUid> contacts = Array.Empty<EntityUid>();
        Vector2 startingPosition = default;

        try
        {
            await server.WaitPost(() =>
            {
                mapSystem.CreateMap(out mapId);
                mapCreated = true;
                contacts = traffic.EnsureTrafficForMap(
                    mapId,
                    new[] { Vector2.Zero },
                    LuaMSectorTrafficSystem.HardMaxContacts + 20,
                    spawnBudget: 1);

                Assert.That(contacts, Has.Count.EqualTo(1),
                    "Production maintenance must stagger new ambient contacts.");

                contacts = traffic.EnsureTrafficForMap(
                    mapId,
                    new[] { Vector2.Zero },
                    LuaMSectorTrafficSystem.HardMaxContacts + 20);

                Assert.That(contacts, Has.Count.EqualTo(LuaMSectorTrafficSystem.HardMaxContacts));
                Assert.That(
                    contacts.Select(uid => entManager.GetComponent<LuaMSectorTrafficContactComponent>(uid).Profile),
                    Is.EquivalentTo(Enum.GetValues<LuaMSectorTrafficProfile>()),
                    "A complete bounded batch must expose civilian, cargo, distress, and unknown signatures.");
                Assert.That(
                    contacts.Select(uid => entManager.GetComponent<LuaMSectorTrafficContactComponent>(uid).ContactCode),
                    Is.Unique,
                    "Every terminal entry needs an unambiguous round-local contact code.");
                Assert.That(
                    contacts.Select(uid => entManager.GetComponent<RadarBlipComponent>(uid).Config.Shape),
                    Is.Unique,
                    "The four contact profiles must be visually distinguishable on the existing radar.");
                var expectedTemplates = new Dictionary<LuaMSectorTrafficProfile, string>
                {
                    [LuaMSectorTrafficProfile.Civilian] = "navigation-drift",
                    [LuaMSectorTrafficProfile.Cargo] = "courier-handoff",
                    [LuaMSectorTrafficProfile.Distress] = "quiet-distress",
                    [LuaMSectorTrafficProfile.Unknown] = "black-box-echo",
                };
                foreach (var uid in contacts)
                {
                    var contact = entManager.GetComponent<LuaMSectorTrafficContactComponent>(uid);
                    Assert.That(contact.DynamicEventTemplateId, Is.EqualTo(expectedTemplates[contact.Profile]));
                }
                startingPosition = entManager.GetComponent<TransformComponent>(contacts[0]).LocalPosition;
            });

            await pair.RunSeconds(0.5f);

            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(CountContactsOnMap(), Is.EqualTo(LuaMSectorTrafficSystem.HardMaxContacts));
                    Assert.That(
                        entManager.GetComponent<TransformComponent>(contacts[0]).LocalPosition,
                        Is.Not.EqualTo(startingPosition),
                        "A traffic signature must physically move so the radar presents sector activity.");

                    foreach (var uid in contacts)
                    {
                        Assert.That(entManager.HasComponent<RadarBlipComponent>(uid), Is.True);
                        Assert.That(entManager.HasComponent<PhysicsComponent>(uid), Is.True);
                        var fixtures = entManager.GetComponent<FixturesComponent>(uid);
                        Assert.That(fixtures.Fixtures, Is.Not.Empty,
                            "The tiny fixture keeps the kinematic contact in the physics update set.");
                        foreach (var fixture in fixtures.Fixtures.Values)
                        {
                            Assert.That(fixture.Hard, Is.False,
                                "Traffic contacts must never block or push physical entities.");
                            Assert.That(fixture.CollisionLayer, Is.Zero);
                            Assert.That(fixture.CollisionMask, Is.Zero);
                        }

                        var marker = entManager.GetComponent<LuaMSectorTrafficContactComponent>(uid);
                        Assert.That(marker.DynamicEventTemplateId, Is.Not.Empty);
                        Assert.That(marker.RouteVelocity.Length(), Is.InRange(
                            LuaMSectorTrafficSystem.MinimumSpeed,
                            LuaMSectorTrafficSystem.MaximumSpeed));

                        var xform = entManager.GetComponent<TransformComponent>(uid);
                        Assert.That(xform.GridUid, Is.Null,
                            "Traffic profiles must remain radar entities, never physical shuttle grids.");
                        Assert.That(xform.GridTraversal, Is.False,
                            "A radar-only contact must stay map-parented while crossing the sector.");
                        Assert.That(xform.LocalPosition.Length(), Is.LessThanOrEqualTo(
                            SharedRadarConsoleSystem.DefaultMaxRange),
                            "New ambient traffic must be visible at the radar's initial zoom level.");
                        var routeClearance = MathF.Abs(
                            xform.LocalPosition.X * marker.RouteVelocity.Y -
                            xform.LocalPosition.Y * marker.RouteVelocity.X) /
                            marker.RouteVelocity.Length();
                        Assert.That(routeClearance, Is.GreaterThan(90f),
                            "Ambient traffic must pass the station instead of aiming through its grid.");
                    }
                });
            });

            await server.WaitPost(() =>
            {
                contacts = traffic.EnsureTrafficForMap(mapId, new[] { Vector2.Zero }, 2);
            });
            await pair.RunTicksSync(2);

            await server.WaitAssertion(() =>
            {
                Assert.That(contacts, Has.Count.EqualTo(2));
                Assert.That(CountContactsOnMap(), Is.EqualTo(2),
                    "Lowering the configured target must remove surplus contacts.");
            });

            await server.WaitPost(() =>
            {
                contacts = traffic.EnsureTrafficForMap(mapId, new[] { Vector2.Zero }, 0);
            });
            await pair.RunTicksSync(2);

            await server.WaitAssertion(() =>
            {
                Assert.That(contacts, Is.Empty);
                Assert.That(CountContactsOnMap(), Is.Zero,
                    "Disabling traffic must remove every lightweight contact.");
            });

            await server.WaitPost(() =>
            {
                traffic.EnsureTrafficForMap(mapId, new[] { Vector2.Zero }, 2);
                entManager.EventBus.RaiseEvent(EventSource.Local, new RoundRestartCleanupEvent());
            });
            await pair.RunTicksSync(2);

            await server.WaitAssertion(() =>
            {
                Assert.That(CountContactsOnMap(), Is.Zero,
                    "Round cleanup must leave no traffic contacts behind.");
            });
        }
        finally
        {
            if (mapCreated)
                await server.WaitPost(() => mapSystem.DeleteMap(mapId));

            await pair.CleanReturnAsync();
        }

        int CountContactsOnMap()
        {
            var count = 0;
            var query = entManager.EntityQueryEnumerator<LuaMSectorTrafficContactComponent, TransformComponent>();
            while (query.MoveNext(out _, out _, out var xform))
            {
                if (xform.MapID == mapId)
                    count++;
            }

            return count;
        }
    }
}
