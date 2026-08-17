using Content.Server.Gateway.Components;
using Content.Server.Gateway.Systems;
using Content.Server.Teleportation;
using Content.Server._LuaM.Stargate;
using Content.Server._NF.GameRule;
using Content.Shared._LuaM.Stargate;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Teleportation.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using System.Collections.Generic;
using System.Linq;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
[NonParallelizable]
public sealed class LuaMStargateRuntimeTest
{
    private static readonly ResPath TraversalSound =
        new("/Audio/_Lua/Effects/Stargate/wormhole_enter.ogg");
    private static readonly ResPath OpeningSound =
        new("/Audio/_Lua/Effects/Stargate/pegasus_wormhole_open.ogg");
    private static readonly ResPath ClosingSound =
        new("/Audio/_Lua/Effects/Stargate/wormhole_close.ogg");

    private static readonly (string Id, string Path, bool Belt)[] GateStations =
    {
        ("LuaMStargateRelayColossus", "/Maps/_LuaM/POI/StargateOriginal/BarrierGate2.yml", false),
        ("LuaMStargateRelayBeltAlpha", "/Maps/_LuaM/POI/StargateOriginal/lodge.yml", true),
        ("LuaMStargateRelayBeltBeta", "/Maps/_LuaM/POI/StargateOriginal/ds_typan.yml", true),
    };

    [Test]
    public async Task DhdStagesDialingReservesDestinationAndManuallyClosesOneWayPortal()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var maps = entities.System<SharedMapSystem>();
        var appearance = entities.System<SharedAppearanceSystem>();

        EntityUid source = default;
        EntityUid destination = default;
        EntityUid alternateDestination = default;
        EntityUid console = default;
        EntityUid actor = default;
        MapId sourceMap = default;
        MapId destinationMap = default;
        MapId alternateMap = default;
        float chevronDelay = default;
        float completeDialDuration = default;
        float closingDelay = default;

        await server.WaitAssertion(() =>
        {
            maps.CreateMap(out sourceMap);
            maps.CreateMap(out destinationMap);
            maps.CreateMap(out alternateMap);
            source = entities.SpawnEntity("Stargate", new MapCoordinates(0, 0, sourceMap));
            console = entities.SpawnEntity("StargateConsole", new MapCoordinates(2, 0, sourceMap));
            actor = entities.SpawnEntity("MobHuman", new MapCoordinates(3, 0, sourceMap));
            destination = entities.SpawnEntity("Stargate", new MapCoordinates(0, 0, destinationMap));
            alternateDestination = entities.SpawnEntity("Stargate", new MapCoordinates(0, 0, alternateMap));
        });
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var sourceGate = entities.GetComponent<LuaMStargateComponent>(source);
            var sourceGateway = entities.GetComponent<GatewayComponent>(source);
            var destinationAddress = entities.GetComponent<LuaMStargateComponent>(destination).Address;
            var alternateAddress = entities.GetComponent<LuaMStargateComponent>(alternateDestination).Address;
            AssertValidAddress(sourceGate.Address, 6);
            AssertValidAddress(destinationAddress, 6);
            AssertValidAddress(alternateAddress, 6);
            Assert.That(sourceGate.ChevronDelay, Is.GreaterThan(0f));
            Assert.That(sourceGate.OpeningDelay, Is.GreaterThan(0f));
            AssertSound(sourceGateway.OpenSound, OpeningSound, SharedAudioSystem.GainToVolume(0.25f));
            AssertSound(sourceGateway.CloseSound, ClosingSound, SharedAudioSystem.GainToVolume(0.25f));

            chevronDelay = sourceGate.ChevronDelay;
            completeDialDuration = sourceGate.ChevronDelay * destinationAddress.Length + sourceGate.OpeningDelay;
            closingDelay = sourceGate.ClosingDelay;

            DialAddress(entities, console, actor, destinationAddress);
            AssertGateVisualState(
                entities,
                appearance,
                source,
                LuaMStargateVisualState.Starting,
                lightEnabled: false);

            var dialing = entities.GetComponent<LuaMStargateDialingComponent>(source);
            var reservation = entities.GetComponent<LuaMStargateDialReservationComponent>(destination);
            Assert.Multiple(() =>
            {
                Assert.That(dialing.Destination, Is.EqualTo(destination));
                Assert.That(dialing.Symbols, Is.EqualTo(destinationAddress));
                Assert.That(dialing.ChevronIndex, Is.Zero);
                Assert.That(dialing.InOpening, Is.False);
                Assert.That(reservation.Source, Is.EqualTo(source));
                Assert.That(entities.HasComponent<PortalComponent>(source), Is.False,
                    "The route must not open in the same tick as the dial request.");
                Assert.That(entities.HasComponent<PortalComponent>(destination), Is.False);
            });

            // A forged second request while the staged route owns the gate must report busy
            // without replacing its destination reservation.
            var consoleComponent = entities.GetComponent<LuaMStargateConsoleComponent>(console);
            consoleComponent.CurrentInput.AddRange(alternateAddress);
            entities.EventBus.RaiseLocalEvent(console, new LuaMStargateDialMessage { Actor = actor });
            Assert.Multiple(() =>
            {
                Assert.That(consoleComponent.Status, Is.EqualTo("stargate-console-status-busy"));
                Assert.That(entities.GetComponent<LuaMStargateDialingComponent>(source).Destination,
                    Is.EqualTo(destination));
                Assert.That(entities.GetComponent<LuaMStargateDialReservationComponent>(destination).Source,
                    Is.EqualTo(source));
                Assert.That(entities.HasComponent<LuaMStargateDialReservationComponent>(alternateDestination),
                    Is.False);
            });
        });

        await pair.RunSeconds(chevronDelay / 2f);
        await server.WaitAssertion(() =>
        {
            AssertGateVisualState(
                entities,
                appearance,
                source,
                LuaMStargateVisualState.Starting,
                lightEnabled: false);
            var dialing = entities.GetComponent<LuaMStargateDialingComponent>(source);
            Assert.Multiple(() =>
            {
                Assert.That(dialing.ChevronIndex, Is.Zero,
                    "A chevron must not engage before its configured delay elapses.");
                Assert.That(entities.HasComponent<PortalComponent>(source), Is.False);
                Assert.That(entities.HasComponent<PortalComponent>(destination), Is.False);
            });
        });

        await pair.RunSeconds(completeDialDuration + 0.2f);
        await server.WaitAssertion(() =>
        {
            AssertGateVisualState(
                entities,
                appearance,
                source,
                LuaMStargateVisualState.Idle,
                lightEnabled: true);
            AssertGateVisualState(
                entities,
                appearance,
                destination,
                LuaMStargateVisualState.Opening,
                lightEnabled: true);
            Assert.Multiple(() =>
            {
                Assert.That(entities.HasComponent<LuaMStargateDialingComponent>(source), Is.False);
                Assert.That(entities.HasComponent<LuaMStargateDialReservationComponent>(destination), Is.False);
                Assert.That(entities.HasComponent<PortalComponent>(source), Is.True);
                Assert.That(entities.HasComponent<PortalComponent>(destination), Is.True);
                Assert.That(entities.GetComponent<GatewayComponent>(source).NextReady,
                    Is.GreaterThan(server.Timing.CurTime));
                Assert.That(entities.GetComponent<LuaMStargateConsoleComponent>(console).Status,
                    Is.EqualTo("stargate-console-status-connected"));
            });

            var sourceLinks = entities.GetComponent<LinkedEntityComponent>(source).LinkedEntities;
            var destinationLinks = entities.GetComponent<LinkedEntityComponent>(destination).LinkedEntities;
            Assert.Multiple(() =>
            {
                Assert.That(sourceLinks, Is.EquivalentTo(new[] { destination }));
                Assert.That(destinationLinks, Is.Empty,
                    "The receiving gate must not provide a return traversal link.");
            });

            entities.EventBus.RaiseLocalEvent(console, new LuaMStargateCloseMessage { Actor = actor });
            AssertGateVisualState(
                entities,
                appearance,
                source,
                LuaMStargateVisualState.Closing,
                lightEnabled: true);
            AssertGateVisualState(
                entities,
                appearance,
                destination,
                LuaMStargateVisualState.Closing,
                lightEnabled: true);
            Assert.Multiple(() =>
            {
                Assert.That(entities.HasComponent<PortalComponent>(source), Is.False);
                Assert.That(entities.HasComponent<PortalComponent>(destination), Is.False);
                Assert.That(entities.HasComponent<LuaMStargateOpenStateComponent>(source), Is.False);
                Assert.That(entities.HasComponent<LuaMStargateOpenStateComponent>(destination), Is.False);
                Assert.That(entities.HasComponent<LuaMStargateClosingComponent>(source), Is.True);
                Assert.That(entities.HasComponent<LuaMStargateClosingComponent>(destination), Is.True);
                Assert.That(entities.GetComponent<LinkedEntityComponent>(source).LinkedEntities, Is.Empty);
            });
        });

        await pair.RunSeconds(closingDelay + 0.1f);
        await server.WaitAssertion(() =>
        {
            AssertGateVisualState(
                entities,
                appearance,
                source,
                LuaMStargateVisualState.Off,
                lightEnabled: false);
            AssertGateVisualState(
                entities,
                appearance,
                destination,
                LuaMStargateVisualState.Off,
                lightEnabled: false);
            Assert.Multiple(() =>
            {
                Assert.That(entities.HasComponent<LuaMStargateClosingComponent>(source), Is.False);
                Assert.That(entities.HasComponent<LuaMStargateClosingComponent>(destination), Is.False);
            });
            maps.DeleteMap(sourceMap);
            maps.DeleteMap(destinationMap);
            maps.DeleteMap(alternateMap);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task TraversalUsesStargateAudioAndStartsAutoCloseOnlyAfterPortalEvent()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var maps = entities.System<SharedMapSystem>();
        var portals = entities.System<PortalSystem>();

        EntityUid source = default;
        EntityUid destination = default;
        EntityUid console = default;
        EntityUid actor = default;
        MapId sourceMap = default;
        MapId destinationMap = default;
        float dialDuration = default;
        const float autoCloseDelay = 0.2f;

        await server.WaitAssertion(() =>
        {
            maps.CreateMap(out sourceMap);
            maps.CreateMap(out destinationMap);
            source = entities.SpawnEntity("Stargate", new MapCoordinates(0, 0, sourceMap));
            console = entities.SpawnEntity("StargateConsole", new MapCoordinates(2, 0, sourceMap));
            actor = entities.SpawnEntity("MobHuman", new MapCoordinates(1, 0, sourceMap));
            destination = entities.SpawnEntity("Stargate", new MapCoordinates(0, 0, destinationMap));
        });
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var sourceGate = entities.GetComponent<LuaMStargateComponent>(source);
            var destinationAddress = entities.GetComponent<LuaMStargateComponent>(destination).Address;
            Assert.That(sourceGate.AutoCloseDelay, Is.EqualTo(10f),
                "The authentic gate default is ten seconds after the first traversal.");
            sourceGate.AutoCloseDelay = autoCloseDelay;
            dialDuration = sourceGate.ChevronDelay * destinationAddress.Length + sourceGate.OpeningDelay;
            DialAddress(entities, console, actor, destinationAddress);
        });
        await pair.RunSeconds(dialDuration + 0.2f);

        await server.WaitAssertion(() =>
        {
            var sourcePortal = entities.GetComponent<PortalComponent>(source);
            var destinationPortal = entities.GetComponent<PortalComponent>(destination);
            AssertTraversalSound(sourcePortal.ArrivalSound);
            AssertTraversalSound(sourcePortal.DepartureSound);
            AssertTraversalSound(destinationPortal.ArrivalSound);
            AssertTraversalSound(destinationPortal.DepartureSound);

            var open = entities.GetComponent<LuaMStargateOpenStateComponent>(source);
            Assert.Multiple(() =>
            {
                Assert.That(open.HasTraversal, Is.False);
                Assert.That(entities.HasComponent<PortalComponent>(source), Is.True);
                Assert.That(entities.HasComponent<PortalComponent>(destination), Is.True);
            });
        });

        // Remaining open longer than AutoCloseDelay is intentional until a traversal event occurs.
        await pair.RunSeconds(autoCloseDelay + 0.1f);
        await server.WaitAssertion(() =>
        {
            Assert.That(entities.HasComponent<PortalComponent>(source), Is.True);
            Assert.That(portals.TryTeleportThroughLinkedPortal(source, actor), Is.True);
            Assert.That(entities.GetComponent<TransformComponent>(actor).MapID, Is.EqualTo(destinationMap));

            var open = entities.GetComponent<LuaMStargateOpenStateComponent>(source);
            Assert.Multiple(() =>
            {
                Assert.That(open.HasTraversal, Is.True,
                    "PortalSystem must raise EntityTeleportedThroughPortalEvent on the source gate.");
                Assert.That(open.LastTraversal, Is.EqualTo(server.Timing.CurTime));
                Assert.That(portals.TryTeleportThroughLinkedPortal(destination, actor, ignoreTimeout: true), Is.False,
                    "The one-way receiving endpoint must not traverse back to the source.");
            });
        });

        await pair.RunSeconds(autoCloseDelay / 2f);
        await server.WaitAssertion(() =>
        {
            Assert.That(entities.HasComponent<PortalComponent>(source), Is.True,
                "The route must remain open until the post-traversal timeout expires.");
        });

        await pair.RunSeconds(autoCloseDelay);
        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(entities.HasComponent<PortalComponent>(source), Is.False);
                Assert.That(entities.HasComponent<PortalComponent>(destination), Is.False);
                Assert.That(entities.HasComponent<LuaMStargateOpenStateComponent>(source), Is.False);
                Assert.That(entities.HasComponent<LuaMStargateOpenStateComponent>(destination), Is.False);
            });
            maps.DeleteMap(sourceMap);
            maps.DeleteMap(destinationMap);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DeletingEndpointCancelsReservationAndCleansOpenSource()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var maps = entities.System<SharedMapSystem>();

        EntityUid source = default;
        EntityUid destination = default;
        EntityUid replacement = default;
        EntityUid console = default;
        EntityUid actor = default;
        MapId sourceMap = default;
        MapId destinationMap = default;
        float dialDuration = default;

        await server.WaitAssertion(() =>
        {
            maps.CreateMap(out sourceMap);
            maps.CreateMap(out destinationMap);
            source = entities.SpawnEntity("Stargate", new MapCoordinates(0, 0, sourceMap));
            console = entities.SpawnEntity("StargateConsole", new MapCoordinates(2, 0, sourceMap));
            actor = entities.SpawnEntity("MobHuman", new MapCoordinates(3, 0, sourceMap));
            destination = entities.SpawnEntity("Stargate", new MapCoordinates(0, 0, destinationMap));
        });
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var address = entities.GetComponent<LuaMStargateComponent>(destination).Address;
            DialAddress(entities, console, actor, address);
            Assert.That(entities.HasComponent<LuaMStargateDialingComponent>(source), Is.True);
            Assert.That(entities.HasComponent<LuaMStargateDialReservationComponent>(destination), Is.True);
            entities.DeleteEntity(destination);
        });
        await pair.RunTicksSync(2);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(entities.EntityExists(destination), Is.False);
                Assert.That(entities.HasComponent<LuaMStargateDialingComponent>(source), Is.False,
                    "Deleting a reserved endpoint must cancel its source's staged dial.");
                Assert.That(entities.HasComponent<PortalComponent>(source), Is.False);
                Assert.That(entities.GetComponent<LuaMStargateConsoleComponent>(console).Status,
                    Is.EqualTo("stargate-console-status-idle"));
            });

            replacement = entities.SpawnEntity("Stargate", new MapCoordinates(0, 0, destinationMap));
        });
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var sourceGate = entities.GetComponent<LuaMStargateComponent>(source);
            var replacementAddress = entities.GetComponent<LuaMStargateComponent>(replacement).Address;
            dialDuration = sourceGate.ChevronDelay * replacementAddress.Length + sourceGate.OpeningDelay;
            DialAddress(entities, console, actor, replacementAddress);
        });
        await pair.RunSeconds(dialDuration + 0.2f);

        await server.WaitAssertion(() =>
        {
            Assert.That(entities.HasComponent<PortalComponent>(source), Is.True);
            Assert.That(entities.GetComponent<LinkedEntityComponent>(source).LinkedEntities,
                Does.Contain(replacement));
            entities.DeleteEntity(replacement);
        });
        await pair.RunTicksSync(2);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(entities.EntityExists(replacement), Is.False);
                Assert.That(entities.HasComponent<PortalComponent>(source), Is.False,
                    "Deleting an open receiving endpoint must close the surviving source.");
                Assert.That(entities.HasComponent<LuaMStargateOpenStateComponent>(source), Is.False);
                Assert.That(entities.GetComponent<LinkedEntityComponent>(source).LinkedEntities, Is.Empty);
            });
            maps.DeleteMap(sourceMap);
            maps.DeleteMap(destinationMap);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ClosedIrisDeniesDialToggleAllowsReservationAndLuaTechKeepsSevenSymbolPreset()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var maps = entities.System<SharedMapSystem>();

        EntityUid source = default;
        EntityUid destination = default;
        EntityUid luaTech = default;
        EntityUid sourceConsole = default;
        EntityUid destinationConsole = default;
        EntityUid sourceActor = default;
        EntityUid destinationActor = default;
        MapId sourceMap = default;
        MapId destinationMap = default;
        MapId luaTechMap = default;

        await server.WaitAssertion(() =>
        {
            maps.CreateMap(out sourceMap);
            maps.CreateMap(out destinationMap);
            maps.CreateMap(out luaTechMap);
            source = entities.SpawnEntity("Stargate", new MapCoordinates(0, 0, sourceMap));
            sourceConsole = entities.SpawnEntity("StargateConsole", new MapCoordinates(2, 0, sourceMap));
            sourceActor = entities.SpawnEntity("MobHuman", new MapCoordinates(3, 0, sourceMap));
            destination = entities.SpawnEntity(
                "StargateControllableSyndicate",
                new MapCoordinates(0, 0, destinationMap));
            destinationConsole = entities.SpawnEntity("StargateConsole", new MapCoordinates(2, 0, destinationMap));
            destinationActor = entities.SpawnEntity("MobHuman", new MapCoordinates(3, 0, destinationMap));
            luaTech = entities.SpawnEntity("StargateControllableLuaTech", new MapCoordinates(0, 0, luaTechMap));
        });
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var destinationAddress = entities.GetComponent<LuaMStargateComponent>(destination).Address;
            var controllable = entities.GetComponent<LuaMStargateControllableComponent>(destination);
            Assert.That(controllable.Enabled, Is.False);

            DialAddress(entities, sourceConsole, sourceActor, destinationAddress);
            Assert.Multiple(() =>
            {
                Assert.That(entities.HasComponent<LuaMStargateDialingComponent>(source), Is.False);
                Assert.That(entities.HasComponent<LuaMStargateDialReservationComponent>(destination), Is.False);
                Assert.That(entities.GetComponent<LuaMStargateConsoleComponent>(sourceConsole).Status,
                    Is.EqualTo("stargate-console-status-not-found"),
                    "A closed destination iris must not be discoverable as a dial target.");
            });

            entities.EventBus.RaiseLocalEvent(
                destinationConsole,
                new LuaMStargateToggleIrisMessage { Actor = destinationActor });
            Assert.Multiple(() =>
            {
                Assert.That(controllable.Enabled, Is.True);
                Assert.That(entities.HasComponent<LuaMStargateIrisAnimatingComponent>(destination), Is.True);
                Assert.That(entities.GetComponent<LuaMStargateIrisAnimatingComponent>(destination).Opening, Is.True);
            });

            var luaTechGate = entities.GetComponent<LuaMStargateComponent>(luaTech);
            Assert.Multiple(() =>
            {
                Assert.That(luaTechGate.AddressPreset, Is.EqualTo("ALUaTeh"));
                Assert.That(LuaMStargateGlyphs.ToGlyphString(luaTechGate.Address), Is.EqualTo("ALUaTeh"));
                Assert.That(luaTechGate.Address[0], Is.EqualTo((byte) 1));
            });
            AssertValidAddress(luaTechGate.Address, 7);
        });

        await pair.RunSeconds(1.5f);
        await server.WaitAssertion(() =>
        {
            Assert.That(entities.HasComponent<LuaMStargateIrisAnimatingComponent>(destination), Is.False);

            // The denied address remains in the DHD input, so retrying Dial now must reserve the opened iris.
            entities.EventBus.RaiseLocalEvent(sourceConsole, new LuaMStargateDialMessage { Actor = sourceActor });
            Assert.Multiple(() =>
            {
                Assert.That(entities.HasComponent<LuaMStargateDialingComponent>(source), Is.True);
                Assert.That(entities.GetComponent<LuaMStargateDialReservationComponent>(destination).Source,
                    Is.EqualTo(source));
            });

            entities.EventBus.RaiseLocalEvent(sourceConsole, new LuaMStargateCloseMessage { Actor = sourceActor });
            Assert.Multiple(() =>
            {
                Assert.That(entities.HasComponent<LuaMStargateDialingComponent>(source), Is.False);
                Assert.That(entities.HasComponent<LuaMStargateDialReservationComponent>(destination), Is.False);
            });

            entities.EventBus.RaiseLocalEvent(
                destinationConsole,
                new LuaMStargateToggleIrisMessage { Actor = destinationActor });
            var controllable = entities.GetComponent<LuaMStargateControllableComponent>(destination);
            Assert.Multiple(() =>
            {
                Assert.That(controllable.Enabled, Is.False);
                Assert.That(entities.GetComponent<LuaMStargateIrisAnimatingComponent>(destination).Opening, Is.False);
            });
        });

        await pair.RunSeconds(1.5f);
        await server.WaitAssertion(() =>
        {
            Assert.That(entities.HasComponent<LuaMStargateIrisAnimatingComponent>(destination), Is.False);
            maps.DeleteMap(sourceMap);
            maps.DeleteMap(destinationMap);
            maps.DeleteMap(luaTechMap);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AddressMediaPopulateAndEnforceDhdAndEditorOperations()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var maps = entities.System<SharedMapSystem>();
        var itemSlots = entities.System<ItemSlotsSystem>();

        EntityUid source = default;
        EntityUid destination = default;
        EntityUid console = default;
        EntityUid editor = default;
        EntityUid actor = default;
        EntityUid dhdDisk = default;
        EntityUid leftDisk = default;
        EntityUid rightDisk = default;
        MapId sourceMap = default;
        MapId destinationMap = default;

        await server.WaitAssertion(() =>
        {
            maps.CreateMap(out sourceMap);
            maps.CreateMap(out destinationMap);

            // The disk intentionally initializes before its local gate. The zero-delay retry
            // must discover the gate created later in this same map-init pass.
            dhdDisk = entities.SpawnEntity("StargateAddressDisk", new MapCoordinates(1, 1, sourceMap));
            source = entities.SpawnEntity("Stargate", new MapCoordinates(0, 0, sourceMap));
            console = entities.SpawnEntity("StargateConsole", new MapCoordinates(2, 0, sourceMap));
            editor = entities.SpawnEntity("StargateAddressEditorConsole", new MapCoordinates(3, 0, sourceMap));
            actor = entities.SpawnEntity("MobHuman", new MapCoordinates(4, 0, sourceMap));
            destination = entities.SpawnEntity("Stargate", new MapCoordinates(0, 0, destinationMap));

            // These initialize after the gate and exercise the immediate local population path.
            leftDisk = entities.SpawnEntity("StargateAddressDisk", new MapCoordinates(3, 1, sourceMap));
            rightDisk = entities.SpawnEntity("StargateAddressDisk", new MapCoordinates(4, 1, sourceMap));
        });
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var sourceAddress = entities.GetComponent<LuaMStargateComponent>(source).Address;
            var destinationAddress = entities.GetComponent<LuaMStargateComponent>(destination).Address;
            var dhdMedia = entities.GetComponent<LuaMStargateAddressDiskComponent>(dhdDisk);
            var leftMedia = entities.GetComponent<LuaMStargateAddressDiskComponent>(leftDisk);
            var rightMedia = entities.GetComponent<LuaMStargateAddressDiskComponent>(rightDisk);

            AssertDiskAddresses(dhdMedia, sourceAddress);
            AssertDiskAddresses(leftMedia, sourceAddress);
            AssertDiskAddresses(rightMedia, sourceAddress);

            // Keep the population assertion independent from the following UI operations.
            dhdMedia.Addresses.Clear();
            leftMedia.Addresses.Clear();
            rightMedia.Addresses.Clear();

            Assert.Multiple(() =>
            {
                Assert.That(itemSlots.TryInsert(
                    console,
                    "disk_slot",
                    dhdDisk,
                    actor,
                    excludeUserAudio: true), Is.True);
                Assert.That(itemSlots.TryInsert(
                    editor,
                    "left_disk_slot",
                    leftDisk,
                    actor,
                    excludeUserAudio: true), Is.True);
                Assert.That(itemSlots.TryInsert(
                    editor,
                    "right_disk_slot",
                    rightDisk,
                    actor,
                    excludeUserAudio: true), Is.True);
            });
            Assert.That(itemSlots.TryGetSlot(console, "disk_slot", out var dhdSlot), Is.True);
            Assert.That(dhdSlot!.Item, Is.EqualTo(dhdDisk));

            entities.EventBus.RaiseLocalEvent(
                console,
                new LuaMStargateSaveDiskAddressMessage { Actor = actor });
            AssertDiskAddresses(dhdMedia, sourceAddress);

            entities.EventBus.RaiseLocalEvent(
                console,
                new LuaMStargateDeleteDiskAddressMessage(0) { Actor = actor });
            Assert.That(dhdMedia.Addresses, Is.Empty, "The DHD delete action must remove the selected address.");

            dhdMedia.Addresses.Add(destinationAddress.ToList());
            entities.EventBus.RaiseLocalEvent(
                console,
                new LuaMStargateAutoDialFromDiskMessage(destinationAddress) { Actor = actor });
            Assert.Multiple(() =>
            {
                Assert.That(entities.GetComponent<LuaMStargateDialingComponent>(source).Destination,
                    Is.EqualTo(destination));
                Assert.That(entities.GetComponent<LuaMStargateDialReservationComponent>(destination).Source,
                    Is.EqualTo(source));
            });

            // Client buttons are locked while dialing, but the server must also reject
            // forged save, delete, and disk-dial requests without disturbing the route.
            entities.EventBus.RaiseLocalEvent(
                console,
                new LuaMStargateSaveDiskAddressMessage { Actor = actor });
            entities.EventBus.RaiseLocalEvent(
                console,
                new LuaMStargateDeleteDiskAddressMessage(0) { Actor = actor });
            entities.EventBus.RaiseLocalEvent(
                console,
                new LuaMStargateAutoDialFromDiskMessage(destinationAddress) { Actor = actor });
            AssertDiskAddresses(dhdMedia, destinationAddress);
            Assert.Multiple(() =>
            {
                Assert.That(entities.GetComponent<LuaMStargateConsoleComponent>(console).CurrentInput, Is.Empty);
                Assert.That(entities.GetComponent<LuaMStargateDialingComponent>(source).Destination,
                    Is.EqualTo(destination));
                Assert.That(entities.GetComponent<LuaMStargateDialReservationComponent>(destination).Source,
                    Is.EqualTo(source));
            });

            entities.EventBus.RaiseLocalEvent(console, new LuaMStargateCloseMessage { Actor = actor });
            Assert.Multiple(() =>
            {
                Assert.That(entities.HasComponent<LuaMStargateDialingComponent>(source), Is.False);
                Assert.That(entities.HasComponent<LuaMStargateDialReservationComponent>(destination), Is.False);
            });

            dhdMedia.Addresses.Clear();
            dhdMedia.Addresses.AddRange(CreateCapacityAddresses(sourceAddress));
            Assert.That(dhdMedia.Addresses, Has.Count.EqualTo(LuaMStargateAddressDiskComponent.MaxAddresses));
            entities.EventBus.RaiseLocalEvent(
                console,
                new LuaMStargateSaveDiskAddressMessage { Actor = actor });
            Assert.Multiple(() =>
            {
                Assert.That(dhdMedia.Addresses, Has.Count.EqualTo(LuaMStargateAddressDiskComponent.MaxAddresses),
                    "The DHD must enforce the server-side 64-address media cap.");
                Assert.That(dhdMedia.Addresses.Any(address => address.SequenceEqual(sourceAddress)), Is.False);
            });

            var addressA = new byte[] { 2, 3, 4, 5, 6, 7 };
            var addressB = new byte[] { 8, 9, 10, 11, 12, 13 };
            var addressC = new byte[] { 14, 15, 16, 17, 18, 19 };

            EnterEditorAddress(entities, editor, actor, addressA);
            entities.EventBus.RaiseLocalEvent(
                editor,
                new LuaMStargateAddressEditorSaveLeftMessage { Actor = actor });
            ClearEditorAddress(entities, editor, actor);
            EnterEditorAddress(entities, editor, actor, addressB);
            entities.EventBus.RaiseLocalEvent(
                editor,
                new LuaMStargateAddressEditorSaveLeftMessage { Actor = actor });
            ClearEditorAddress(entities, editor, actor);
            EnterEditorAddress(entities, editor, actor, addressC);
            entities.EventBus.RaiseLocalEvent(
                editor,
                new LuaMStargateAddressEditorSaveRightMessage { Actor = actor });

            AssertDiskAddresses(leftMedia, addressA, addressB);
            AssertDiskAddresses(rightMedia, addressC);

            entities.EventBus.RaiseLocalEvent(
                editor,
                new LuaMStargateAddressEditorCopyLeftToRightMessage(0) { Actor = actor });
            AssertDiskAddresses(leftMedia, addressA, addressB);
            AssertDiskAddresses(rightMedia, addressC, addressA);

            entities.EventBus.RaiseLocalEvent(
                editor,
                new LuaMStargateAddressEditorMoveLeftToRightMessage(1) { Actor = actor });
            AssertDiskAddresses(leftMedia, addressA);
            AssertDiskAddresses(rightMedia, addressC, addressA, addressB);

            entities.EventBus.RaiseLocalEvent(
                editor,
                new LuaMStargateAddressEditorCloneRightToLeftMessage { Actor = actor });
            AssertDiskAddresses(leftMedia, addressA, addressC, addressB);

            entities.EventBus.RaiseLocalEvent(
                editor,
                new LuaMStargateAddressEditorDeleteRightMessage(0) { Actor = actor });
            AssertDiskAddresses(rightMedia, addressA, addressB);

            ClearEditorAddress(entities, editor, actor);
            var sevenSymbolAddress = new byte[] { 1, 2, 3, 4, 5, 6, 7 };
            EnterEditorAddress(entities, editor, actor, sevenSymbolAddress);
            rightMedia.Addresses.Clear();
            rightMedia.Addresses.AddRange(CreateCapacityAddresses(sevenSymbolAddress));
            var leftBeforeCappedMove = leftMedia.Addresses.Select(address => address.ToArray()).ToArray();

            entities.EventBus.RaiseLocalEvent(
                editor,
                new LuaMStargateAddressEditorSaveRightMessage { Actor = actor });
            entities.EventBus.RaiseLocalEvent(
                editor,
                new LuaMStargateAddressEditorMoveLeftToRightMessage(0) { Actor = actor });
            entities.EventBus.RaiseLocalEvent(
                editor,
                new LuaMStargateAddressEditorCloneLeftToRightMessage { Actor = actor });
            Assert.Multiple(() =>
            {
                Assert.That(rightMedia.Addresses, Has.Count.EqualTo(LuaMStargateAddressDiskComponent.MaxAddresses),
                    "Editor save/copy/move/clone operations must share the server media cap.");
                Assert.That(rightMedia.Addresses.Any(address => address.SequenceEqual(sevenSymbolAddress)), Is.False);
                Assert.That(leftMedia.Addresses.Select(address => address.ToArray()),
                    Is.EqualTo(leftBeforeCappedMove),
                    "A capped move must not remove the source address.");
            });

            maps.DeleteMap(sourceMap);
            maps.DeleteMap(destinationMap);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task StockGatewayCannotBypassDhdOnlyEndpoints()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var maps = entities.System<SharedMapSystem>();
        var gateways = entities.System<GatewaySystem>();

        MapId sourceMap = default;
        MapId destinationMap = default;
        EntityUid stockGateway = default;
        EntityUid stargate = default;
        EntityUid actor = default;

        await server.WaitAssertion(() =>
        {
            maps.CreateMap(out sourceMap);
            maps.CreateMap(out destinationMap);
            stockGateway = entities.SpawnEntity("Gateway", new MapCoordinates(0, 0, sourceMap));
            stargate = entities.SpawnEntity("Stargate", new MapCoordinates(0, 0, destinationMap));
            actor = entities.SpawnEntity("MobHuman", new MapCoordinates(1, 0, sourceMap));
        });
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(gateways.TryOpenPortal(stockGateway, stargate, actor), Is.False,
                    "A stock gateway must not select a DHD-only destination.");
                Assert.That(gateways.TryOpenPortal(stargate, stockGateway, actor), Is.False,
                    "A DHD-only source must require the controller opt-in path.");
                Assert.That(entities.HasComponent<PortalComponent>(stockGateway), Is.False);
                Assert.That(entities.HasComponent<PortalComponent>(stargate), Is.False);
            });
            maps.DeleteMap(sourceMap);
            maps.DeleteMap(destinationMap);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task OriginalGateStationsAreSplitOneMainAndTwoBelt()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();

        Assert.That(GateStations.Count(entry => entry.Belt), Is.EqualTo(2));
        Assert.That(GateStations.Count(entry => !entry.Belt), Is.EqualTo(1));

        foreach (var station in GateStations)
        {
            var prototype = prototypes.Index<PointOfInterestPrototype>(station.Id);
            Assert.That(prototype.SpawnGroup, Is.EqualTo("Required"));
            Assert.That(prototype.AsteroidBelt, Is.EqualTo(station.Belt));
            Assert.That(prototype.GridPath, Is.EqualTo(new ResPath(station.Path)));
            Assert.That(prototype.AddComponents.ContainsKey("LuaMStargateStation"), Is.False,
                "The authentic map already contains its gate and must not receive a second installer gate.");
        }

        var cargoDepot = prototypes.Index<PointOfInterestPrototype>("LuaMTypanCargoDepot");
        Assert.That(cargoDepot.AsteroidBelt, Is.True);
        Assert.That(cargoDepot.GridPath,
            Is.EqualTo(new ResPath("/Maps/_LuaM/POI/StargateOriginal/typancargodepot.yml")));

        await pair.CleanReturnAsync();
    }

    private static void DialAddress(
        IEntityManager entities,
        EntityUid console,
        EntityUid actor,
        IReadOnlyCollection<byte> address)
    {
        foreach (var symbol in address)
        {
            entities.EventBus.RaiseLocalEvent(
                console,
                new LuaMStargateInputMessage(symbol) { Actor = actor });
        }

        entities.EventBus.RaiseLocalEvent(console, new LuaMStargateDialMessage { Actor = actor });
    }

    private static void EnterEditorAddress(
        IEntityManager entities,
        EntityUid editor,
        EntityUid actor,
        IReadOnlyCollection<byte> address)
    {
        foreach (var symbol in address)
        {
            entities.EventBus.RaiseLocalEvent(
                editor,
                new LuaMStargateAddressEditorInputMessage(symbol) { Actor = actor });
        }
    }

    private static void ClearEditorAddress(IEntityManager entities, EntityUid editor, EntityUid actor)
    {
        entities.EventBus.RaiseLocalEvent(
            editor,
            new LuaMStargateAddressEditorClearMessage { Actor = actor });
    }

    private static List<List<byte>> CreateCapacityAddresses(IReadOnlyCollection<byte> excluded)
    {
        var addresses = new List<List<byte>>(LuaMStargateAddressDiskComponent.MaxAddresses);
        for (byte first = 2; first <= 10 && addresses.Count < LuaMStargateAddressDiskComponent.MaxAddresses; first++)
        {
            for (byte second = 11; second <= 18 && addresses.Count < LuaMStargateAddressDiskComponent.MaxAddresses; second++)
            {
                var address = new List<byte> { first, second, 20, 21, 22, 23 };
                if (!address.SequenceEqual(excluded))
                    addresses.Add(address);
            }
        }

        Assert.That(addresses, Has.Count.EqualTo(LuaMStargateAddressDiskComponent.MaxAddresses));
        return addresses;
    }

    private static void AssertDiskAddresses(
        LuaMStargateAddressDiskComponent disk,
        params IReadOnlyCollection<byte>[] expected)
    {
        Assert.That(disk.Addresses, Has.Count.EqualTo(expected.Length));
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.That(disk.Addresses[index], Is.EqualTo(expected[index]),
                $"Unexpected address at media index {index}.");
        }
    }

    private static void AssertTraversalSound(SoundSpecifier sound)
    {
        AssertSound(sound, TraversalSound, -8f);
    }

    private static void AssertSound(SoundSpecifier sound, ResPath expected, float expectedVolume)
    {
        Assert.That(sound, Is.TypeOf<SoundPathSpecifier>());
        var path = (SoundPathSpecifier) sound;
        Assert.Multiple(() =>
        {
            Assert.That(path.Path, Is.EqualTo(expected));
            Assert.That(path.Params.Volume, Is.EqualTo(expectedVolume).Within(0.001f));
        });
    }

    private static void AssertGateVisualState(
        IEntityManager entities,
        SharedAppearanceSystem appearance,
        EntityUid gate,
        LuaMStargateVisualState expectedState,
        bool lightEnabled)
    {
        var appearanceComponent = entities.GetComponent<AppearanceComponent>(gate);
        Assert.That(
            appearance.TryGetData(
                gate,
                LuaMStargateVisuals.State,
                out LuaMStargateVisualState state,
                appearanceComponent),
            Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(state, Is.EqualTo(expectedState));
            Assert.That(entities.GetComponent<PointLightComponent>(gate).Enabled, Is.EqualTo(lightEnabled));
        });
    }

    private static void AssertValidAddress(IReadOnlyCollection<byte> address, int expectedLength)
    {
        Assert.That(address, Has.Count.EqualTo(expectedLength));
        Assert.That(address.All(symbol => symbol is >= 1 and <= LuaMStargateGlyphs.Count), Is.True);
        Assert.That(address.Distinct().Count(), Is.EqualTo(expectedLength));
    }

    [Test]
    public async Task RandomDiskAddressesAreUniqueAndReferenceRealGates()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var maps = entities.System<SharedMapSystem>();

        var gateAddresses = new List<byte[]>();
        var acquired = new List<byte[]>();

        await server.WaitAssertion(() =>
        {
            for (var i = 0; i < 4; i++)
            {
                maps.CreateMap(out var mapId);
                var gate = entities.SpawnEntity("Stargate", new MapCoordinates(0, 0, mapId));
                gateAddresses.Add(entities.GetComponent<LuaMStargateComponent>(gate).Address.ToArray());
            }

            var stargate = entities.System<LuaMStargateSystem>();
            for (var i = 0; i < 4; i++)
            {
                Assert.That(stargate.TryAcquireUniqueRandomAddress(out var address), Is.True);
                acquired.Add(address!);
            }

            Assert.That(
                stargate.TryAcquireUniqueRandomAddress(out _),
                Is.False,
                "No address should remain after every network gate has been reserved.");
        });

        await server.WaitAssertion(() =>
        {
            var keys = acquired.Select(address => string.Join('-', address)).ToHashSet();
            Assert.That(keys, Has.Count.EqualTo(acquired.Count), "Random disks must not share addresses.");

            foreach (var address in acquired)
            {
                Assert.That(
                    gateAddresses.Any(existing => existing.SequenceEqual(address)),
                    Is.True,
                    "Random disk addresses must reference real network gates.");
            }
        });

        await pair.CleanReturnAsync();
    }
}
