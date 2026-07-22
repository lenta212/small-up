using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Client._Mono.Radar;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server._Mono.Radar;
using Content.Shared._Mono.Detection;
using Content.Shared._Mono.Radar;
using Content.Shared.Shuttles.BUIStates;
using Content.Shared.Weapons.Hitscan.Components;
using Content.Shared.Weapons.Hitscan.Events;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(RadarBlipSystem))]
[TestOf(typeof(RadarBlipsSystem))]
public sealed class LuaMRadarIsolationTest : InteractionTest
{
    private const string RadarPrototype = "LuaMRadarIsolationConsole";
    private const string HiddenBlipPrototype = "LuaMRadarHiddenBlip";

    [TestPrototypes]
    private const string TestPrototypes = @"
- type: entity
  id: LuaMRadarIsolationConsole
  components:
  - type: RadarConsole
    maxRange: 1
  - type: UserInterface
    interfaces:
      enum.RadarConsoleUiKey.Key:
        type: RadarConsoleBoundUserInterface
        interactionRange: 0
        requireInputValidation: false

- type: entity
  id: LuaMRadarHiddenBlip
  components:
  - type: RadarBlip
";

    [Test]
    public async Task RequestsRequireOpenUiAndSnapshotsStayIsolatedPerRadar()
    {
        EntityUid serverRadarA = default;
        EntityUid serverRadarB = default;
        EntityUid hitscanSourceA = default;
        EntityUid hitscanSourceB = default;
        NetEntity hiddenBlip = default;
        NetEntity netRadarA = default;
        NetEntity netRadarB = default;

        await Server.WaitPost(() =>
        {
            var coordinatesA = MapData.GridCoords.Offset(new Vector2(-4f, 0f));
            var coordinatesB = MapData.GridCoords.Offset(new Vector2(4f, 0f));

            serverRadarA = SEntMan.SpawnEntity(RadarPrototype, coordinatesA);
            serverRadarB = SEntMan.SpawnEntity(RadarPrototype, coordinatesB);
            netRadarA = SEntMan.GetNetEntity(serverRadarA);
            netRadarB = SEntMan.GetNetEntity(serverRadarB);

            hitscanSourceA = SEntMan.SpawnEntity(null, coordinatesA);
            var signatureA = SEntMan.AddComponent<HitscanRadarSignatureComponent>(hitscanSourceA);
            signatureA.RadarColor = Color.Red;
            signatureA.LifeTime = 0.1f;

            hitscanSourceB = SEntMan.SpawnEntity(null, coordinatesB);
            var signatureB = SEntMan.AddComponent<HitscanRadarSignatureComponent>(hitscanSourceB);
            signatureB.RadarColor = Color.Blue;
            signatureB.LifeTime = 0.1f;

            var hiddenGrid = MapMan.CreateGridEntity(MapId);
            MapSystem.SetTile(hiddenGrid, Vector2i.Zero, MapData.Tile.Tile);
            Transform.SetMapCoordinates(
                hiddenGrid.Owner,
                new MapCoordinates(new Vector2(-4f, 0f), MapId));
            var detectedAt = SEntMan.AddComponent<DetectedAtRangeMultiplierComponent>(hiddenGrid.Owner);
            detectedAt.VisualMultiplier = 0f;
            detectedAt.InfraredMultiplier = 0f;
            detectedAt.VisualBias = 0f;

            var hidden = SEntMan.SpawnEntity(
                HiddenBlipPrototype,
                new EntityCoordinates(hiddenGrid.Owner, new Vector2(0.5f, 0.5f)));
            hiddenBlip = SEntMan.GetNetEntity(hidden);
        });

        await RunTicks(5);

        var clientRadarA = ToClient(netRadarA);
        var clientRadarB = ToClient(netRadarB);
        var clientBlips = CEntMan.System<RadarBlipsSystem>();
        var eventProbe = CEntMan.System<LuaMRadarEventProbeSystem>();
        var serverProbe = SEntMan.System<LuaMRadarEventProbeSystem>();

        // A valid radar entity is not sufficient authorization: the actor must have
        // one of the supported radar interfaces open on that exact entity.
        await Client.WaitPost(() =>
        {
            clientBlips.RequestBlips(clientRadarA);
            clientBlips.RequestBlips(clientRadarB);
        });
        await RunSeconds(0.6f);

        await Server.WaitAssertion(() =>
        {
            Assert.That(serverProbe.Requests, Has.Count.EqualTo(2), serverProbe.Describe());
            serverProbe.Requests.Clear();
        });

        await Client.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(eventProbe.Responses, Is.Empty);
                Assert.That(clientBlips.GetHitscanLines(clientRadarA), Is.Empty);
                Assert.That(clientBlips.GetHitscanLines(clientRadarB), Is.Empty);
            });
        });

        await Server.WaitPost(() =>
        {
            var ui = SEntMan.System<UserInterfaceSystem>();
            ui.OpenUi(serverRadarA, RadarConsoleUiKey.Key, SPlayer);
            ui.OpenUi(serverRadarB, RadarConsoleUiKey.Key, SPlayer);

            Assert.Multiple(() =>
            {
                Assert.That(ui.IsUiOpen(serverRadarA, RadarConsoleUiKey.Key, SPlayer), Is.True);
                Assert.That(ui.IsUiOpen(serverRadarB, RadarConsoleUiKey.Key, SPlayer), Is.True);
            });

            var shotA = new HitscanRaycastFiredEvent
            {
                FromCoordinates = SEntMan.GetComponent<TransformComponent>(hitscanSourceA).Coordinates,
                ShotDirection = Vector2.UnitX,
                Gun = hitscanSourceA,
                DistanceTried = 0.5f,
            };
            SEntMan.EventBus.RaiseLocalEvent(hitscanSourceA, ref shotA);

            var shotB = new HitscanRaycastFiredEvent
            {
                FromCoordinates = SEntMan.GetComponent<TransformComponent>(hitscanSourceB).Coordinates,
                ShotDirection = Vector2.UnitX,
                Gun = hitscanSourceB,
                DistanceTried = 0.5f,
            };
            SEntMan.EventBus.RaiseLocalEvent(hitscanSourceB, ref shotB);
        });

        // Both prototype lifetimes are shorter than this delay. The server-side
        // bounded history must bridge the radar poll without keeping temp entities.
        await RunSeconds(0.2f);

        // Both requests happen in the same client frame. This guards both the client
        // request throttle and the server cooldown being scoped to the radar entity.
        await Client.WaitPost(() =>
        {
            clientBlips.RequestBlips(clientRadarA);
            clientBlips.RequestBlips(clientRadarB);
        });
        await RunTicks(10);

        await Server.WaitAssertion(() =>
        {
            Assert.That(serverProbe.Requests, Has.Count.GreaterThanOrEqualTo(2), serverProbe.Describe());
        });

        await Client.WaitAssertion(() =>
        {
            var linesA = clientBlips.GetHitscanLines(clientRadarA);
            var linesB = clientBlips.GetHitscanLines(clientRadarB);

            Assert.That(
                eventProbe.Responses,
                Has.Count.GreaterThanOrEqualTo(2),
                eventProbe.Describe());
            Assert.That(
                eventProbe.Responses.SelectMany(response => response.Blips),
                Does.Not.Contain(hiddenBlip),
                "An undetected grid contact must never be serialized to the radar client.");
            Assert.That(linesA, Has.Count.EqualTo(1));
            Assert.That(linesB, Has.Count.EqualTo(1));
            Assert.Multiple(() =>
            {
                Assert.That(linesA[0].Color, Is.EqualTo(Color.Red));
                Assert.That(linesB[0].Color, Is.EqualTo(Color.Blue));
                Assert.That(linesA[0].Start, Is.Not.EqualTo(linesB[0].Start));
            });
        });
    }
}

public sealed class LuaMRadarEventProbeSystem : EntitySystem
{
    public readonly List<(NetEntity Radar, uint RequestId)> Requests = new();
    public readonly List<(NetEntity Radar, uint RequestId, int HitscanCount, List<NetEntity> Blips)> Responses = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<RequestBlipsEvent>(OnRequestBlips);
        SubscribeNetworkEvent<GiveBlipsEvent>(OnGiveBlips);
    }

    public string Describe()
    {
        return $"Requests=[{string.Join(", ", Requests)}], Responses=[{string.Join(", ", Responses)}]";
    }

    private void OnRequestBlips(RequestBlipsEvent ev)
    {
        Requests.Add((ev.Radar, ev.RequestId));
    }

    private void OnGiveBlips(GiveBlipsEvent ev)
    {
        Responses.Add((ev.Radar, ev.RequestId, ev.HitscanLines.Count, ev.Blips.Select(blip => blip.Uid).ToList()));
    }
}
