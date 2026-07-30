using System.Collections.Generic;
using System.Numerics;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared._NF.Shuttles.Events;
using Content.Shared.Shuttles.BUIStates;
using Content.Shared.Shuttles.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMShuttleRadarTargetTest : InteractionTest
{
    private const string ConsolePrototype = "LuaMShuttleRadarTargetConsole";

    [TestPrototypes]
    private const string TestPrototypes = @"
- type: entity
  id: LuaMShuttleRadarTargetConsole
  components:
  - type: ShuttleConsole
  - type: RadarConsole
  - type: UserInterface
    interfaces:
      enum.ShuttleConsoleUiKey.Key:
        type: ShuttleConsoleBoundUserInterface
        interactionRange: 0
        requireInputValidation: false
";

    [Test]
    public async Task TargetIsSharedValidatedAndTracksVisibleShuttles()
    {
        await Server.WaitPost(() =>
        {
            var ownGrid = MapData.Grid.Owner;
            var ownShuttle = SEntMan.EnsureComponent<ShuttleComponent>(ownGrid);
            var console = SEntMan.SpawnEntity(ConsolePrototype, MapData.GridCoords);

            RaiseTargetRequest(console, new Vector2(12.5f, -30f), NetEntity.Invalid);
            Assert.Multiple(() =>
            {
                Assert.That(ownShuttle.RadarTarget, Is.EqualTo(new Vector2(12.5f, -30f)));
                Assert.That(ownShuttle.RadarTargetEntity, Is.Null);
                Assert.That(ownShuttle.RadarTargetHidden, Is.False);
            });

            var targetGrid = MapMan.CreateGridEntity(MapId);
            MapSystem.SetTile(targetGrid, Vector2i.Zero, MapData.Tile.Tile);
            var initialTargetPosition = new Vector2(100f, 75f);
            Transform.SetMapCoordinates(targetGrid.Owner, new MapCoordinates(initialTargetPosition, MapId));
            SEntMan.EnsureComponent<ShuttleComponent>(targetGrid.Owner);
            SEntMan.EnsureComponent<IFFComponent>(targetGrid.Owner);
            var shuttleSystem = SEntMan.System<ShuttleSystem>();
            var targetNetEntity = SEntMan.GetNetEntity(targetGrid.Owner);

            RaiseTargetRequest(console, new Vector2(500f, 500f), targetNetEntity);
            Assert.Multiple(() =>
            {
                Assert.That(ownShuttle.RadarTarget, Is.EqualTo(new Vector2(500f, 500f)));
                Assert.That(
                    ownShuttle.RadarTargetEntity,
                    Is.Null,
                    "A spoofed entity/position pair must degrade to a static coordinate.");
            });

            shuttleSystem.AddIFFFlag(targetGrid.Owner, IFFFlags.HideLabel);
            RaiseTargetRequest(console, initialTargetPosition, targetNetEntity);
            Assert.That(
                ownShuttle.RadarTargetEntity,
                Is.Null,
                "A hidden IFF contact must never become a tracked entity.");

            shuttleSystem.RemoveIFFFlag(targetGrid.Owner, IFFFlags.HideLabel);
            RaiseTargetRequest(console, initialTargetPosition, targetNetEntity);
            Assert.That(ownShuttle.RadarTargetEntity, Is.EqualTo(targetGrid.Owner));

            var consoleSystem = SEntMan.System<ShuttleConsoleSystem>();
            var state = consoleSystem.GetNavState(
                console,
                new Dictionary<NetEntity, List<DockingPortState>>());
            Assert.Multiple(() =>
            {
                Assert.That(state.Target, Is.EqualTo(initialTargetPosition));
                Assert.That(state.TargetEntity, Is.EqualTo(targetNetEntity));
                Assert.That(state.HideTarget, Is.False);
            });

            var movedTargetPosition = new Vector2(120f, 90f);
            Transform.SetMapCoordinates(targetGrid.Owner, new MapCoordinates(movedTargetPosition, MapId));
            state = consoleSystem.GetNavState(
                console,
                new Dictionary<NetEntity, List<DockingPortState>>());
            Assert.Multiple(() =>
            {
                Assert.That(state.Target, Is.EqualTo(movedTargetPosition));
                Assert.That(ownShuttle.RadarTarget, Is.EqualTo(movedTargetPosition));
            });

            SEntMan.EventBus.RaiseLocalEvent(
                console,
                new SetRadarTargetVisibilityRequest { Hidden = true });
            Assert.That(ownShuttle.RadarTargetHidden, Is.True);

            RaiseTargetRequest(
                console,
                new Vector2(float.NaN, 0f),
                NetEntity.Invalid);
            Assert.Multiple(() =>
            {
                Assert.That(ownShuttle.RadarTarget, Is.EqualTo(movedTargetPosition));
                Assert.That(ownShuttle.RadarTargetEntity, Is.EqualTo(targetGrid.Owner));
                Assert.That(ownShuttle.RadarTargetHidden, Is.True);
            });
        });
    }

    private void RaiseTargetRequest(EntityUid console, Vector2 position, NetEntity target)
    {
        SEntMan.EventBus.RaiseLocalEvent(
            console,
            new SetRadarTargetRequest
            {
                Position = position,
                TargetEntity = target,
            });
    }
}
