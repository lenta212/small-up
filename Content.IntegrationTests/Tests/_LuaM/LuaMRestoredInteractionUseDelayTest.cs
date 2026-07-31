using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server._LuaM.ShipPersistence;
using Content.Server.DeviceLinking.Components;
using Content.Server.Power.Components;
using Content.Shared.DeviceLinking.Components;
using Content.Shared.Interaction;
using Content.Shared.Storage;
using Content.Shared.Timing;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMRestoredInteractionUseDelayTest : InteractionTest
{
    [Test]
    public async Task RestoredUseDelaysDoNotBlockStorageButtonsOrLevers()
    {
        var persistence = SEntMan.System<LuaMFullShipPersistenceSystem>();
        var useDelay = SEntMan.System<UseDelaySystem>();
        var ui = SEntMan.System<SharedUserInterfaceSystem>();
        var timing = Server.ResolveDependency<IGameTiming>();

        MapId sourceMap = default;
        var sourceMapCreated = false;

        try
        {
            await Server.WaitPost(() =>
            {
                MapSystem.CreateMap(out sourceMap);
                sourceMapCreated = true;

                var sourceGrid = MapMan.CreateGridEntity(sourceMap);
                MapSystem.SetTile(sourceGrid, sourceGrid, Vector2i.Zero, new Tile(1));
                MapSystem.SetTile(sourceGrid, sourceGrid, new Vector2i(1, 0), new Tile(1));

                var bag = SEntMan.SpawnEntity(
                    "ClothingBackpack",
                    new EntityCoordinates(sourceGrid, new Vector2(0.25f, 0.5f)));
                var button = SEntMan.SpawnEntity(
                    "SignalButton",
                    new EntityCoordinates(sourceGrid, new Vector2(0.5f, 0.5f)));
                var lever = SEntMan.SpawnEntity(
                    "TwoWayLever",
                    new EntityCoordinates(sourceGrid, new Vector2(0.75f, 0.5f)));
                var validSwitch = SEntMan.SpawnEntity(
                    "SignalSwitch",
                    new EntityCoordinates(sourceGrid, new Vector2(0.9f, 0.5f)));
                var staleRechargeGun = SEntMan.SpawnEntity(
                    "UllmanWeaponPulsePistol",
                    new EntityCoordinates(sourceGrid, new Vector2(1.1f, 0.5f)));
                var validRechargeGun = SEntMan.SpawnEntity(
                    "UllmanWeaponPulsePistol",
                    new EntityCoordinates(sourceGrid, new Vector2(1.3f, 0.5f)));

                PoisonUseDelay(bag);
                PoisonUseDelay(button);
                PoisonUseDelay(lever);
                SEntMan.GetComponent<BatterySelfRechargerComponent>(staleRechargeGun).NextAutoRecharge =
                    TimeSpan.FromDays(30);
                var validRecharge = SEntMan.GetComponent<BatterySelfRechargerComponent>(validRechargeGun);
                validRecharge.NextAutoRecharge = timing.CurTime + TimeSpan.FromSeconds(10);
                var validNextAutoRecharge = validRecharge.NextAutoRecharge;
                Assert.That(
                    useDelay.SetLength(validSwitch, TimeSpan.FromSeconds(30)),
                    Is.True);
                Assert.That(useDelay.TryResetDelay(validSwitch), Is.True);
                Assert.That(
                    useDelay.SetLength(validSwitch, TimeSpan.FromSeconds(1)),
                    Is.True,
                    "Shortening a configured delay must not rewrite its already-running end time.");
                Assert.That(
                    useDelay.TryGetDelayInfo(validSwitch, out var validDelayBefore),
                    Is.True);
                var validStartTime = validDelayBefore!.StartTime;
                var validEndTime = validDelayBefore.EndTime;
                var validLength = validDelayBefore.Length;

                Assert.That(
                    persistence.TryCaptureSnapshot(sourceGrid, 1, out var snapshot, out var captureReason),
                    Is.True,
                    captureReason);

                var capturedYaml = Encoding.UTF8.GetString(snapshot.Payload);
                Assert.That(
                    capturedYaml,
                    Does.Contain("startTime: 2592000"),
                    "Snapshot capture must retain timestamps so same-round active delays can survive park-and-call.");

                MapSystem.DeleteMap(sourceMap);
                sourceMapCreated = false;

                Assert.That(
                    persistence.TryRestoreSnapshot(snapshot, MapId, out var restoredGrid, out var restoreReason),
                    Is.True,
                    restoreReason);

                var restoredBag = RequirePrototypeDescendant(restoredGrid, "ClothingBackpack");
                var restoredButton = RequirePrototypeDescendant(restoredGrid, "SignalButton");
                var restoredLever = RequirePrototypeDescendant(restoredGrid, "TwoWayLever");
                var restoredValidSwitch = RequirePrototypeDescendant(restoredGrid, "SignalSwitch");
                var restoredRechargeGuns = RequirePrototypeDescendants(
                    restoredGrid,
                    "UllmanWeaponPulsePistol",
                    2);

                Transform.SetCoordinates(SPlayer, new EntityCoordinates(restoredGrid, new Vector2(0.5f, 0.5f)));

                Assert.Multiple(() =>
                {
                    Assert.That(useDelay.IsDelayed(restoredBag), Is.False);
                    Assert.That(useDelay.IsDelayed(restoredButton), Is.False);
                    Assert.That(useDelay.IsDelayed(restoredLever), Is.False);
                    Assert.That(
                        useDelay.IsDelayed(restoredValidSwitch),
                        Is.True,
                        "A valid active delay from the same server time base must remain active.");
                });

                Assert.That(
                    useDelay.TryGetDelayInfo(restoredValidSwitch, out var validDelayAfter),
                    Is.True);
                Assert.Multiple(() =>
                {
                    Assert.That(
                        Math.Abs((validDelayAfter!.StartTime - validStartTime).Ticks),
                        Is.LessThanOrEqualTo(1),
                        "Timestamp serialization may round by one 100-nanosecond tick.");
                    Assert.That(
                        Math.Abs((validDelayAfter.EndTime - validEndTime).Ticks),
                        Is.LessThanOrEqualTo(1),
                        "Timestamp serialization may round by one 100-nanosecond tick.");
                    Assert.That(validDelayAfter.Length, Is.EqualTo(validLength));
                });

                var restoredRechargeTimes = restoredRechargeGuns
                    .Select(uid => SEntMan.GetComponent<BatterySelfRechargerComponent>(uid).NextAutoRecharge)
                    .ToArray();
                Assert.Multiple(() =>
                {
                    Assert.That(
                        restoredRechargeTimes.Any(time =>
                            Math.Abs((time - validNextAutoRecharge).Ticks) <= 1),
                        Is.True,
                        "A legitimate same-round energy-weapon recharge pause must survive park-and-call; " +
                        "timestamp serialization may round by one 100-nanosecond tick.");
                    Assert.That(restoredRechargeTimes.Any(time => time <= timing.CurTime), Is.True,
                        "An impossible old-round recharge timestamp must expire so the restored weapon can recharge.");
                });

                Assert.That(InteractSys.InteractionActivate(SPlayer, restoredBag), Is.True);
                Assert.That(
                    ui.IsUiOpen(restoredBag, StorageComponent.StorageUiKey.Key, SPlayer),
                    Is.True,
                    "Ordinary activation must open a restored storage UI without using its verb.");
                ui.CloseUi(restoredBag, StorageComponent.StorageUiKey.Key, SPlayer);

                var signalSwitch = SEntMan.GetComponent<SignalSwitchComponent>(restoredButton);
                var previousSwitchState = signalSwitch.State;
                Assert.That(InteractSys.InteractionActivate(SPlayer, restoredButton), Is.True);
                Assert.That(signalSwitch.State, Is.Not.EqualTo(previousSwitchState));

                var twoWayLever = SEntMan.GetComponent<TwoWayLeverComponent>(restoredLever);
                var previousLeverState = twoWayLever.State;
                Assert.That(InteractSys.InteractionActivate(SPlayer, restoredLever), Is.True);
                Assert.That(twoWayLever.State, Is.Not.EqualTo(previousLeverState));
            });
        }
        finally
        {
            if (sourceMapCreated)
            {
                await Server.WaitPost(() =>
                {
                    if (MapSystem.MapExists(sourceMap))
                        MapSystem.DeleteMap(sourceMap);
                });
            }
        }

        void PoisonUseDelay(EntityUid uid)
        {
            var component = SEntMan.GetComponent<UseDelayComponent>(uid);
            foreach (var delay in component.Delays.Values)
            {
                delay.StartTime = TimeSpan.FromDays(30);
                delay.EndTime = TimeSpan.FromDays(30) + delay.Length;
            }
        }
    }

    private EntityUid RequirePrototypeDescendant(EntityUid root, string prototypeId)
    {
        var pending = new Stack<EntityUid>();
        var visited = new HashSet<EntityUid>();
        pending.Push(root);

        while (pending.TryPop(out var uid))
        {
            if (!visited.Add(uid) || !SEntMan.EntityExists(uid))
                continue;

            if (SEntMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == prototypeId)
                return uid;

            var children = SEntMan.GetComponent<TransformComponent>(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                pending.Push(child);
        }

        Assert.Fail($"Could not find restored descendant with prototype '{prototypeId}'.");
        return EntityUid.Invalid;
    }

    private IReadOnlyList<EntityUid> RequirePrototypeDescendants(
        EntityUid root,
        string prototypeId,
        int expectedCount)
    {
        var matches = new List<EntityUid>();
        var pending = new Stack<EntityUid>();
        var visited = new HashSet<EntityUid>();
        pending.Push(root);

        while (pending.TryPop(out var uid))
        {
            if (!visited.Add(uid) || !SEntMan.EntityExists(uid))
                continue;

            if (SEntMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == prototypeId)
                matches.Add(uid);

            var children = SEntMan.GetComponent<TransformComponent>(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                pending.Push(child);
        }

        Assert.That(matches, Has.Count.EqualTo(expectedCount),
            $"Expected {expectedCount} restored descendants with prototype '{prototypeId}'.");
        return matches;
    }
}
