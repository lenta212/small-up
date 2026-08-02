using Content.Server.Gateway.Components;
using Content.Server.Gateway.Systems;
using Content.Server.Ghost;
using Content.Server.Light.Components;
using Content.Shared._LuaM.Stargate;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.GameTicking;
using Content.Shared.Light.Components;
using Content.Shared.Teleportation.Components;
using Content.Shared.Teleportation.Systems;
using Content.Shared.UserInterface;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using System.Linq;

namespace Content.Server._LuaM.Stargate;

public sealed class LuaMStargateSystem : EntitySystem
{
    [Dependency] private readonly GatewaySystem _gateway = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;
    [Dependency] private readonly ItemSlotsSystem _itemSlots = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly LinkedEntitySystem _linkedEntity = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly PointLightSystem _pointLight = default!;
    [Dependency] private readonly GhostSystem _ghost = default!;

    private readonly HashSet<string> _assignedAddresses = new();
    private readonly List<EntityUid> _dialingBuffer = new();
    private readonly List<EntityUid> _openingBuffer = new();
    private readonly List<EntityUid> _closingBuffer = new();
    private readonly List<EntityUid> _irisBuffer = new();
    private readonly List<EntityUid> _autoCloseBuffer = new();

    private const float IrisAnimationDuration = 1.344f;
    private const float GateLightFlickerRadius = 8f;
    private static readonly Color GatePortalLightColor = Color.FromHex("#88aaff");
    private static readonly SoundPathSpecifier IrisSound =
        new(
            "/Audio/_Lua/Effects/Stargate/iris_thud_2.ogg",
            AudioParams.Default.WithVolume(SharedAudioSystem.GainToVolume(0.25f)));
    private static readonly AudioParams GateSoundParams = AudioParams.Default
        .WithVolume(SharedAudioSystem.GainToVolume(0.25f));
    private static readonly AudioParams DhdPressSoundParams = AudioParams.Default
        .WithVolume(SharedAudioSystem.GainToVolume(0.2f));
    private static readonly AudioParams IdleSoundParams = AudioParams.Default
        .WithLoop(true)
        .WithVolume(SharedAudioSystem.GainToVolume(0.35f))
        .WithMaxDistance(10f);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<LuaMStargateComponent, ComponentStartup>(OnGateStartup);
        SubscribeLocalEvent<LuaMStargateComponent, ComponentShutdown>(OnGateShutdown);
        SubscribeLocalEvent<LuaMStargateComponent, EntityTerminatingEvent>(OnGateTerminating);
        SubscribeLocalEvent<LuaMStargateComponent, EntityTeleportedThroughPortalEvent>(OnEntityPassedThroughGate);
        SubscribeLocalEvent<LuaMStargateControllableComponent, ComponentStartup>(OnControllableStartup);
        SubscribeLocalEvent<LuaMStargateConsoleComponent, ComponentStartup>(OnConsoleStartup);
        SubscribeLocalEvent<LuaMStargateConsoleComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<LuaMStargateConsoleComponent, LuaMStargateInputMessage>(OnInput);
        SubscribeLocalEvent<LuaMStargateConsoleComponent, LuaMStargateClearMessage>(OnClear);
        SubscribeLocalEvent<LuaMStargateConsoleComponent, LuaMStargateDialMessage>(OnDial);
        SubscribeLocalEvent<LuaMStargateConsoleComponent, LuaMStargateCloseMessage>(OnClose);
        SubscribeLocalEvent<LuaMStargateConsoleComponent, LuaMStargateSaveDiskAddressMessage>(OnSaveDiskAddress);
        SubscribeLocalEvent<LuaMStargateConsoleComponent, LuaMStargateDeleteDiskAddressMessage>(OnDeleteDiskAddress);
        SubscribeLocalEvent<LuaMStargateConsoleComponent, LuaMStargateAutoDialFromDiskMessage>(OnAutoDialFromDisk);
        SubscribeLocalEvent<LuaMStargateConsoleComponent, LuaMStargateToggleIrisMessage>(OnToggleIris);
        SubscribeLocalEvent<LuaMStargateConsoleComponent, EntInsertedIntoContainerMessage>(OnDiskChanged);
        SubscribeLocalEvent<LuaMStargateConsoleComponent, EntRemovedFromContainerMessage>(OnDiskChanged);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundCleanup);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        UpdateDialing(frameTime);
        UpdateOpening(frameTime);
        UpdateClosing(frameTime);
        UpdateIrisAnimations(frameTime);
        UpdateAutoClose();
    }

    private void OnGateStartup(Entity<LuaMStargateComponent> entity, ref ComponentStartup args)
    {
        SetGateVisualState(entity.Owner, LuaMStargateVisualState.Off);
        var parsed = LuaMStargateGlyphs.ParseAddress(entity.Comp.AddressPreset);
        if (parsed != null && IsValidAddress(parsed) && _assignedAddresses.Add(AddressKey(parsed)))
        {
            entity.Comp.Address = parsed;
            return;
        }

        entity.Comp.Address = AllocateAddress();
        entity.Comp.AddressPreset = LuaMStargateGlyphs.ToGlyphString(entity.Comp.Address);
    }

    private void OnGateShutdown(Entity<LuaMStargateComponent> entity, ref ComponentShutdown args)
    {
        if (entity.Comp.Address.Length > 0)
            _assignedAddresses.Remove(AddressKey(entity.Comp.Address));
    }

    private void OnGateTerminating(Entity<LuaMStargateComponent> entity, ref EntityTerminatingEvent args)
    {
        CancelDialingForGate(entity.Owner, updateUi: false);
        ClosePortalForGate(entity.Owner, reason: GatewayPortalCloseReason.System, animate: false);
        // Refresh after termination completes so consoles formerly linked to this
        // endpoint publish Unlinked instead of retaining a stale busy/open state.
        Timer.Spawn(0, UpdateAllConsoles);
    }

    private void OnControllableStartup(Entity<LuaMStargateControllableComponent> entity, ref ComponentStartup args)
    {
        _appearance.SetData(
            entity.Owner,
            LuaMStargateVisuals.IrisState,
            entity.Comp.Enabled ? LuaMStargateIrisVisualState.Open : LuaMStargateIrisVisualState.Closed);
    }

    private void OnRoundCleanup(RoundRestartCleanupEvent args)
    {
        _assignedAddresses.Clear();
    }

    private byte[] AllocateAddress()
    {
        for (var attempt = 0; attempt < 1024; attempt++)
        {
            // Symbol 1 is the point-of-origin prefix reserved for seven-symbol
            // addresses. Ordinary six-symbol static gates use 2..40.
            var available = Enumerable.Range(2, LuaMStargateGlyphs.Count - 1)
                .Select(value => (byte) value)
                .ToList();
            var address = new byte[6];
            for (var i = 0; i < address.Length; i++)
            {
                var index = _random.Next(available.Count);
                address[i] = available[index];
                available.RemoveAt(index);
            }

            if (_assignedAddresses.Add(AddressKey(address)))
                return address;
        }

        throw new InvalidOperationException("Unable to allocate a unique Stargate address.");
    }

    private void OnConsoleStartup(Entity<LuaMStargateConsoleComponent> entity, ref ComponentStartup args)
    {
        TryLinkNearestGate(entity);
    }

    private void OnUiOpened(Entity<LuaMStargateConsoleComponent> entity, ref BoundUIOpenedEvent args)
    {
        TryLinkNearestGate(entity);
        UpdateUi(entity);
    }

    private void OnInput(Entity<LuaMStargateConsoleComponent> entity, ref LuaMStargateInputMessage args)
    {
        if (TryGetLinkedGate(entity, out var gateUid, out _) && IsGateBusy(gateUid))
            return;

        if (args.Symbol is < 1 or > LuaMStargateGlyphs.Count ||
            entity.Comp.CurrentInput.Count >= GetRequiredLength(entity.Comp.CurrentInput, args.Symbol) ||
            entity.Comp.CurrentInput.Contains(args.Symbol))
            return;

        entity.Comp.CurrentInput.Add(args.Symbol);
        entity.Comp.Status = "stargate-console-status-input";
        _audio.PlayPvs(entity.Comp.PressSound, entity.Owner, DhdPressSoundParams);
        UpdateUi(entity);
    }

    private void OnClear(Entity<LuaMStargateConsoleComponent> entity, ref LuaMStargateClearMessage args)
    {
        if (TryGetLinkedGate(entity, out var gateUid, out _) && IsGateBusy(gateUid))
            return;

        entity.Comp.CurrentInput.Clear();
        entity.Comp.Status = "stargate-console-status-idle";
        UpdateUi(entity);
    }

    private void OnDial(Entity<LuaMStargateConsoleComponent> entity, ref LuaMStargateDialMessage args)
    {
        Dial(entity, args.Actor);
    }

    private void Dial(Entity<LuaMStargateConsoleComponent> entity, EntityUid actor)
    {
        if (!TryGetLinkedGate(entity, out var source, out var sourceGate) ||
            !IsValidAddress(entity.Comp.CurrentInput))
        {
            entity.Comp.Status = "stargate-console-status-invalid";
            _audio.PlayPvs(entity.Comp.FailSound, entity.Owner, GateSoundParams);
            UpdateUi(entity);
            return;
        }

        if (IsGateBusy(source))
        {
            entity.Comp.Status = "stargate-console-status-busy";
            _audio.PlayPvs(entity.Comp.FailSound, source, GateSoundParams);
            UpdateUi(entity);
            return;
        }

        if (TryComp<LuaMStargateControllableComponent>(source, out var sourceControl) &&
            !sourceControl.Enabled)
        {
            entity.Comp.Status = "stargate-console-status-iris-closed";
            _audio.PlayPvs(entity.Comp.FailSound, source, GateSoundParams);
            UpdateUi(entity);
            return;
        }

        var address = entity.Comp.CurrentInput.ToArray();
        _audio.PlayPvs(entity.Comp.DialSound, entity.Owner, GateSoundParams);
        EntityUid? target = null;
        var query = EntityQueryEnumerator<LuaMStargateComponent, GatewayComponent>();
        while (query.MoveNext(out var uid, out var gate, out _))
        {
            if (uid != source &&
                gate.Address.SequenceEqual(address) &&
                (!TryComp<LuaMStargateControllableComponent>(uid, out var control) || control.Enabled) &&
                !IsGateBusy(uid))
            {
                target = uid;
                break;
            }
        }

        if (target is not { } destination)
        {
            entity.Comp.Status = "stargate-console-status-not-found";
            _audio.PlayPvs(entity.Comp.FailSound, source, GateSoundParams);
            UpdateUi(entity);
            return;
        }

        StartDialing(source, sourceGate, destination, entity.Owner, actor, address);
        entity.Comp.CurrentInput.Clear();
        entity.Comp.Status = "stargate-console-status-dialing";
        UpdateAllConsoles();
    }

    private void OnClose(Entity<LuaMStargateConsoleComponent> entity, ref LuaMStargateCloseMessage args)
    {
        if (TryGetLinkedGate(entity, out var source, out _))
        {
            if (!CancelDialingForGate(source))
            {
                ClosePortalForGate(
                    source,
                    args.Actor,
                    GatewayPortalCloseReason.Manual);
            }
        }

        entity.Comp.Status = "stargate-console-status-idle";
        UpdateAllConsoles();
    }

    private void OnToggleIris(Entity<LuaMStargateConsoleComponent> entity, ref LuaMStargateToggleIrisMessage args)
    {
        if (!TryGetLinkedGate(entity, out var gateUid, out _) ||
            !TryComp<LuaMStargateControllableComponent>(gateUid, out var controllable) ||
            HasComp<LuaMStargateIrisAnimatingComponent>(gateUid))
            return;

        if (controllable.Enabled)
        {
            CancelDialingForGate(gateUid);
            ClosePortalForGate(gateUid, args.Actor, GatewayPortalCloseReason.Manual);
        }

        controllable.Enabled = !controllable.Enabled;
        Dirty(gateUid, controllable);
        var animation = EnsureComp<LuaMStargateIrisAnimatingComponent>(gateUid);
        animation.Accumulator = 0f;
        animation.Opening = controllable.Enabled;
        _appearance.SetData(
            gateUid,
            LuaMStargateVisuals.IrisState,
            controllable.Enabled
                ? LuaMStargateIrisVisualState.Opening
                : LuaMStargateIrisVisualState.Closing);
        _audio.PlayPvs(IrisSound, gateUid, GateSoundParams);
        entity.Comp.Status = controllable.Enabled
            ? "stargate-console-status-iris-open"
            : "stargate-console-status-iris-closed";
        UpdateAllConsoles();
    }

    private void OnSaveDiskAddress(Entity<LuaMStargateConsoleComponent> entity, ref LuaMStargateSaveDiskAddressMessage args)
    {
        if (!TryGetLinkedGate(entity, out var gateUid, out var gate) ||
            IsGateBusy(gateUid) ||
            gate.Address.Length == 0 ||
            GetInsertedDisk(entity.Owner) is not { } diskUid ||
            !TryComp<LuaMStargateAddressDiskComponent>(diskUid, out var disk) ||
            disk.Addresses.Count >= LuaMStargateAddressDiskComponent.MaxAddresses ||
            disk.Addresses.Any(existing => existing.SequenceEqual(gate.Address)))
            return;

        disk.Addresses.Add(gate.Address.ToList());
        Dirty(diskUid, disk);
        UpdateUi(entity);
    }

    private void OnDeleteDiskAddress(Entity<LuaMStargateConsoleComponent> entity, ref LuaMStargateDeleteDiskAddressMessage args)
    {
        if (TryGetLinkedGate(entity, out var gateUid, out _) && IsGateBusy(gateUid))
            return;

        if (GetInsertedDisk(entity.Owner) is not { } diskUid ||
            !TryComp<LuaMStargateAddressDiskComponent>(diskUid, out var disk) ||
            args.Index < 0 || args.Index >= disk.Addresses.Count)
            return;

        disk.Addresses.RemoveAt(args.Index);
        Dirty(diskUid, disk);
        UpdateUi(entity);
    }

    private void OnAutoDialFromDisk(Entity<LuaMStargateConsoleComponent> entity, ref LuaMStargateAutoDialFromDiskMessage args)
    {
        var requestedAddress = args.Address;
        if (TryGetLinkedGate(entity, out var gateUid, out _) && IsGateBusy(gateUid))
            return;

        if (!IsValidAddress(requestedAddress) ||
            GetInsertedDisk(entity.Owner) is not { } diskUid ||
            !TryComp<LuaMStargateAddressDiskComponent>(diskUid, out var disk) ||
            !disk.Addresses.Any(existing => existing.SequenceEqual(requestedAddress)))
            return;

        entity.Comp.CurrentInput.Clear();
        entity.Comp.CurrentInput.AddRange(requestedAddress);
        Dial(entity, args.Actor);
    }

    private void OnDiskChanged(Entity<LuaMStargateConsoleComponent> entity, ref EntInsertedIntoContainerMessage args)
    {
        if (args.Container.ID == "disk_slot")
            UpdateUi(entity);
    }

    private void OnDiskChanged(Entity<LuaMStargateConsoleComponent> entity, ref EntRemovedFromContainerMessage args)
    {
        if (args.Container.ID == "disk_slot")
            UpdateUi(entity);
    }

    private EntityUid? GetInsertedDisk(EntityUid consoleUid)
    {
        if (!_itemSlots.TryGetSlot(consoleUid, "disk_slot", out var slot))
            return null;
        return slot.Item;
    }

    private void StartDialing(
        EntityUid source,
        LuaMStargateComponent sourceGate,
        EntityUid destination,
        EntityUid console,
        EntityUid actor,
        byte[] symbols)
    {
        var dialing = EnsureComp<LuaMStargateDialingComponent>(source);
        dialing.Symbols = symbols;
        dialing.Destination = destination;
        dialing.Console = console;
        dialing.Actor = actor;
        dialing.ChevronIndex = 0;
        dialing.Accumulator = 0f;
        dialing.InOpening = false;

        var reservation = EnsureComp<LuaMStargateDialReservationComponent>(destination);
        reservation.Source = source;

        RemComp<LuaMStargateClosingComponent>(source);
        SetGateVisualState(source, LuaMStargateVisualState.Starting);
    }

    private void UpdateDialing(float frameTime)
    {
        _dialingBuffer.Clear();
        var query = EntityQueryEnumerator<LuaMStargateDialingComponent, LuaMStargateComponent>();
        while (query.MoveNext(out var uid, out _, out _))
            _dialingBuffer.Add(uid);

        foreach (var uid in _dialingBuffer)
        {
            if (!TryComp<LuaMStargateDialingComponent>(uid, out var dialing) ||
                !TryComp<LuaMStargateComponent>(uid, out var gate) ||
                !Exists(dialing.Destination) ||
                !TryComp<LuaMStargateDialReservationComponent>(dialing.Destination, out var reservation) ||
                reservation.Source != uid ||
                TryComp<LuaMStargateControllableComponent>(dialing.Destination, out var control) && !control.Enabled)
            {
                FailDialing(uid, dialing, "stargate-console-status-not-found");
                continue;
            }

            dialing.Accumulator += frameTime;
            if (!dialing.InOpening)
            {
                while (dialing.Accumulator >= gate.ChevronDelay &&
                       dialing.ChevronIndex < dialing.Symbols.Length)
                {
                    dialing.Accumulator -= gate.ChevronDelay;
                    dialing.ChevronIndex++;
                    if (TryComp<LuaMStargateConsoleComponent>(dialing.Console, out var console))
                        _audio.PlayPvs(console.EngageSound, uid, GateSoundParams);
                }

                if (dialing.ChevronIndex < dialing.Symbols.Length)
                    continue;

                dialing.InOpening = true;
                dialing.Accumulator = 0f;
                SetGateVisualState(uid, LuaMStargateVisualState.Opening);
                if (TryComp<GatewayComponent>(uid, out var gateway))
                    _audio.PlayPvs(gateway.OpenSound, uid, GateSoundParams);
                continue;
            }

            if (dialing.Accumulator < gate.OpeningDelay)
                continue;

            FinishDialing(uid, gate, dialing);
        }
    }

    private void FinishDialing(
        EntityUid source,
        LuaMStargateComponent sourceGate,
        LuaMStargateDialingComponent dialing)
    {
        var destination = dialing.Destination;
        if (TryComp<LuaMStargateDialReservationComponent>(destination, out var reservation) &&
            reservation.Source == source)
        {
            RemComp<LuaMStargateDialReservationComponent>(destination);
        }

        var opened = Exists(destination) &&
                     _gateway.TryOpenPortal(
                         source,
                         destination,
                         dialing.Actor,
                         allowController: true,
                         playSound: false);

        if (!opened)
        {
            FailDialing(source, dialing, "stargate-console-status-busy");
            return;
        }

        _linkedEntity.TryUnlink(source, destination);
        _linkedEntity.OneWayLink(source, destination);
        ConfigureTraversalAudio(source, sourceGate);
        if (TryComp<LuaMStargateComponent>(destination, out var destinationGate))
        {
            ConfigureTraversalAudio(destination, destinationGate);
            if (TryComp<GatewayComponent>(destination, out var destinationGateway))
                _audio.PlayPvs(destinationGateway.OpenSound, destination, GateSoundParams);
            SetGateVisualState(destination, LuaMStargateVisualState.Opening);
            EnsureComp<LuaMStargateOpeningComponent>(destination).Accumulator = 0f;
            StartIdleSound(destination, destinationGate);
        }

        SetGateVisualState(source, LuaMStargateVisualState.Idle);
        StartIdleSound(source, sourceGate);
        RemComp<LuaMStargateDialingComponent>(source);

        if (TryComp<LuaMStargateConsoleComponent>(dialing.Console, out var console))
            console.Status = "stargate-console-status-connected";
        UpdateAllConsoles();
    }

    private void FailDialing(
        EntityUid source,
        LuaMStargateDialingComponent? dialing,
        string status)
    {
        if (dialing == null)
            return;

        var consoleUid = dialing.Console;
        CancelDialingForGate(source, updateUi: false);
        if (TryComp<LuaMStargateConsoleComponent>(consoleUid, out var console))
        {
            console.Status = status;
            _audio.PlayPvs(console.FailSound, source, GateSoundParams);
        }
        UpdateAllConsoles();
    }

    private bool CancelDialingForGate(EntityUid gate, bool updateUi = true)
    {
        var source = gate;
        if (TryComp<LuaMStargateDialReservationComponent>(gate, out var incoming))
            source = incoming.Source;

        if (!TryComp<LuaMStargateDialingComponent>(source, out var dialing))
            return false;

        var destination = dialing.Destination;
        var consoleUid = dialing.Console;
        if (Exists(destination) &&
            TryComp<LuaMStargateDialReservationComponent>(destination, out var reservation) &&
            reservation.Source == source)
        {
            RemComp<LuaMStargateDialReservationComponent>(destination);
        }

        RemComp<LuaMStargateDialingComponent>(source);
        if (Exists(source) && !TerminatingOrDeleted(source))
            SetGateVisualState(source, LuaMStargateVisualState.Off);
        if (TryComp<LuaMStargateConsoleComponent>(consoleUid, out var console))
            console.Status = "stargate-console-status-idle";
        if (updateUi)
            UpdateAllConsoles();
        return true;
    }

    private bool IsGateBusy(EntityUid gate)
    {
        return HasComp<PortalComponent>(gate) ||
               HasComp<LuaMStargateDialingComponent>(gate) ||
               HasComp<LuaMStargateDialReservationComponent>(gate) ||
               _linkedEntity.GetLink(gate, out _) ||
               FindSourceGate(gate) != null;
    }

    private EntityUid? FindSourceGate(EntityUid destination)
    {
        var query = EntityQueryEnumerator<LuaMStargateComponent, LinkedEntityComponent>();
        while (query.MoveNext(out var uid, out _, out var links))
        {
            if (uid != destination && links.LinkedEntities.Contains(destination))
                return uid;
        }

        return null;
    }

    private void ClosePortalForGate(
        EntityUid gate,
        EntityUid? actor = null,
        GatewayPortalCloseReason reason = GatewayPortalCloseReason.System,
        bool animate = true)
    {
        var source = gate;
        EntityUid? destination = null;
        if (_linkedEntity.GetLink(source, out var linked))
        {
            destination = linked;
        }
        else if (FindSourceGate(gate) is { } foundSource)
        {
            source = foundSource;
            _linkedEntity.GetLink(source, out destination);
        }

        var hadConnection = destination != null ||
                            HasComp<PortalComponent>(source) ||
                            HasComp<LuaMStargateOpenStateComponent>(source);
        if (!hadConnection)
            return;

        StopIdleSound(source);
        if (destination != null)
            StopIdleSound(destination.Value);

        _gateway.ClosePortal(source, actor: actor, reason: reason);

        if (animate)
        {
            if (Exists(source) && !TerminatingOrDeleted(source))
                StartClosingAnimation(source);
            if (destination != null &&
                Exists(destination.Value) &&
                !TerminatingOrDeleted(destination.Value))
            {
                StartClosingAnimation(destination.Value);
            }
        }
        else
        {
            if (Exists(source) && !TerminatingOrDeleted(source))
            {
                RemComp<LuaMStargateOpeningComponent>(source);
                RemComp<LuaMStargateClosingComponent>(source);
                SetGateVisualState(source, LuaMStargateVisualState.Off);
            }
            if (destination != null &&
                Exists(destination.Value) &&
                !TerminatingOrDeleted(destination.Value))
            {
                RemComp<LuaMStargateOpeningComponent>(destination.Value);
                RemComp<LuaMStargateClosingComponent>(destination.Value);
                SetGateVisualState(destination.Value, LuaMStargateVisualState.Off);
            }
        }
    }

    private void StartClosingAnimation(EntityUid gate)
    {
        if (!TryComp<LuaMStargateComponent>(gate, out _))
            return;

        RemComp<LuaMStargateOpeningComponent>(gate);
        SetGateVisualState(gate, LuaMStargateVisualState.Closing);
        EnsureComp<LuaMStargateClosingComponent>(gate).Accumulator = 0f;
    }

    private void UpdateOpening(float frameTime)
    {
        _openingBuffer.Clear();
        var query = EntityQueryEnumerator<LuaMStargateOpeningComponent, LuaMStargateComponent>();
        while (query.MoveNext(out var uid, out var opening, out var gate))
        {
            opening.Accumulator += frameTime;
            if (opening.Accumulator >= gate.OpeningDelay)
                _openingBuffer.Add(uid);
        }

        foreach (var uid in _openingBuffer)
        {
            if (!Exists(uid) || TerminatingOrDeleted(uid))
                continue;
            SetGateVisualState(uid, LuaMStargateVisualState.Idle);
            RemComp<LuaMStargateOpeningComponent>(uid);
        }
    }

    private void UpdateClosing(float frameTime)
    {
        _closingBuffer.Clear();
        var query = EntityQueryEnumerator<LuaMStargateClosingComponent, LuaMStargateComponent>();
        while (query.MoveNext(out var uid, out var closing, out var gate))
        {
            closing.Accumulator += frameTime;
            if (closing.Accumulator >= gate.ClosingDelay)
                _closingBuffer.Add(uid);
        }

        foreach (var uid in _closingBuffer)
        {
            if (!Exists(uid) || TerminatingOrDeleted(uid))
                continue;
            SetGateVisualState(uid, LuaMStargateVisualState.Off);
            RemComp<LuaMStargateClosingComponent>(uid);
        }
    }

    private void UpdateIrisAnimations(float frameTime)
    {
        _irisBuffer.Clear();
        var query = EntityQueryEnumerator<LuaMStargateIrisAnimatingComponent>();
        while (query.MoveNext(out var uid, out var animation))
        {
            animation.Accumulator += frameTime;
            if (animation.Accumulator >= IrisAnimationDuration)
                _irisBuffer.Add(uid);
        }

        foreach (var uid in _irisBuffer)
        {
            if (!TryComp<LuaMStargateIrisAnimatingComponent>(uid, out var animation))
                continue;
            _appearance.SetData(
                uid,
                LuaMStargateVisuals.IrisState,
                animation.Opening ? LuaMStargateIrisVisualState.Open : LuaMStargateIrisVisualState.Closed);
            RemComp<LuaMStargateIrisAnimatingComponent>(uid);
        }

        if (_irisBuffer.Count > 0)
            UpdateAllConsoles();
    }

    private void OnEntityPassedThroughGate(
        Entity<LuaMStargateComponent> entity,
        ref EntityTeleportedThroughPortalEvent args)
    {
        if (!TryComp<LuaMStargateOpenStateComponent>(entity.Owner, out var open))
            return;

        open.HasTraversal = true;
        open.LastTraversal = _timing.CurTime;
        if (args.TargetEntity is { } destination &&
            TryComp<LuaMStargateOpenStateComponent>(destination, out var destinationOpen))
        {
            destinationOpen.HasTraversal = true;
            destinationOpen.LastTraversal = open.LastTraversal;
        }
    }

    private void UpdateAutoClose()
    {
        _autoCloseBuffer.Clear();
        var query = EntityQueryEnumerator<LuaMStargateOpenStateComponent, LuaMStargateComponent, PortalComponent>();
        while (query.MoveNext(out var uid, out var open, out var gate, out _))
        {
            if (open.HasTraversal &&
                _timing.CurTime - open.LastTraversal >= TimeSpan.FromSeconds(gate.AutoCloseDelay))
            {
                _autoCloseBuffer.Add(uid);
            }
        }

        foreach (var uid in _autoCloseBuffer)
        {
            if (Exists(uid) && HasComp<PortalComponent>(uid))
                ClosePortalForGate(uid, reason: GatewayPortalCloseReason.AutomaticTimeout);
        }

        if (_autoCloseBuffer.Count > 0)
            UpdateAllConsoles();
    }

    private void ConfigureTraversalAudio(EntityUid gate, LuaMStargateComponent component)
    {
        if (!TryComp<PortalComponent>(gate, out var portal))
            return;

        portal.ArrivalSound = component.TraversalSound;
        portal.DepartureSound = component.TraversalSound;
        Dirty(gate, portal);
    }

    private void StartIdleSound(EntityUid gate, LuaMStargateComponent component)
    {
        var open = EnsureComp<LuaMStargateOpenStateComponent>(gate);
        open.HasTraversal = false;
        open.LastTraversal = default;
        open.IdleAudio = _audio.PlayPvs(component.IdleSound, gate, IdleSoundParams)?.Entity;
    }

    private void StopIdleSound(EntityUid gate)
    {
        if (!TryComp<LuaMStargateOpenStateComponent>(gate, out var open))
            return;

        open.IdleAudio = _audio.Stop(open.IdleAudio);
        RemComp<LuaMStargateOpenStateComponent>(gate);
    }

    private void SetGateVisualState(EntityUid gate, LuaMStargateVisualState state)
    {
        _appearance.SetData(gate, LuaMStargateVisuals.State, state);
        if (TryComp<PointLightComponent>(gate, out var light))
        {
            var enabled = state is LuaMStargateVisualState.Opening or
                LuaMStargateVisualState.Idle or
                LuaMStargateVisualState.Closing;
            _pointLight.SetEnabled(gate, enabled, light);
            if (enabled)
            {
                _pointLight.SetColor(gate, GatePortalLightColor, light);
                _pointLight.SetRadius(gate, 4f, light);
                _pointLight.SetEnergy(gate, 0.6f, light);
            }
        }

        if (state == LuaMStargateVisualState.Opening)
            FlickerNearbyLights(gate);
    }

    private void FlickerNearbyLights(EntityUid gate)
    {
        var lights = new HashSet<EntityUid>();
        _lookup.GetEntitiesInRange(gate, GateLightFlickerRadius, lights, LookupFlags.StaticSundries);
        var query = GetEntityQuery<PoweredLightComponent>();
        foreach (var uid in lights)
        {
            if (uid != gate && query.HasComponent(uid))
                _ghost.DoGhostBooEvent(uid);
        }
    }

    private void TryLinkNearestGate(Entity<LuaMStargateConsoleComponent> entity)
    {
        if (entity.Comp.LinkedGate is { } linked && Exists(linked))
            return;

        var consoleCoordinates = Transform(entity).Coordinates;
        var bestDistance = entity.Comp.AutoLinkRadius;
        EntityUid? best = null;
        var query = EntityQueryEnumerator<LuaMStargateComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var transform))
        {
            if (transform.MapID != Transform(entity).MapID ||
                !consoleCoordinates.TryDistance(EntityManager, transform.Coordinates, out var distance) ||
                distance > bestDistance)
                continue;

            bestDistance = distance;
            best = uid;
        }

        entity.Comp.LinkedGate = best;
    }

    private bool TryGetLinkedGate(
        Entity<LuaMStargateConsoleComponent> entity,
        out EntityUid gateUid,
        out LuaMStargateComponent gate)
    {
        TryLinkNearestGate(entity);
        if (entity.Comp.LinkedGate is { } linked &&
            TryComp<LuaMStargateComponent>(linked, out var linkedGate))
        {
            gateUid = linked;
            gate = linkedGate;
            return true;
        }

        gateUid = EntityUid.Invalid;
        gate = null!;
        return false;
    }

    private void UpdateAllConsoles()
    {
        var query = EntityQueryEnumerator<LuaMStargateConsoleComponent>();
        while (query.MoveNext(out var uid, out var console))
            UpdateUi((uid, console));
    }

    private void UpdateUi(Entity<LuaMStargateConsoleComponent> entity)
    {
        TryGetLinkedGate(entity, out var gateUid, out var gate);
        byte[][]? diskAddresses = null;
        if (GetInsertedDisk(entity.Owner) is { } diskUid &&
            TryComp<LuaMStargateAddressDiskComponent>(diskUid, out var disk))
            diskAddresses = disk.Addresses.Select(address => address.ToArray()).ToArray();

        LuaMStargateControllableComponent? controllable = null;
        var hasControllable = gateUid.IsValid() &&
            TryComp(gateUid, out controllable);
        var portalOpen = gateUid.IsValid() && HasComp<PortalComponent>(gateUid);
        var dialing = gateUid.IsValid() &&
            (HasComp<LuaMStargateDialingComponent>(gateUid) ||
             HasComp<LuaMStargateDialReservationComponent>(gateUid));
        var status = gateUid.IsValid()
            ? dialing
                ? "stargate-console-status-dialing"
                : portalOpen
                    ? "stargate-console-status-connected"
                    : entity.Comp.Status == "stargate-console-status-connected"
                        ? "stargate-console-status-idle"
                        : entity.Comp.Status
            : "stargate-console-status-unlinked";
        var state = new LuaMStargateConsoleUiState(
            entity.Comp.CurrentInput.ToArray(),
            gate?.Address ?? Array.Empty<byte>(),
            GetRequiredLength(entity.Comp.CurrentInput),
            portalOpen,
            dialing,
            status,
            diskAddresses,
            hasControllable,
            hasControllable && controllable!.Enabled,
            hasControllable && HasComp<LuaMStargateIrisAnimatingComponent>(gateUid));
        _ui.SetUiState(entity.Owner, LuaMStargateConsoleUiKey.Key, state);
    }

    private static bool IsValidAddress(IEnumerable<byte> symbols)
    {
        var address = symbols.ToArray();
        var expectedLength = address.Length > 0 && address[0] == 1 ? 7 : 6;
        return address.Length == expectedLength &&
               address.All(symbol => symbol is >= 1 and <= LuaMStargateGlyphs.Count) &&
               address.Distinct().Count() == address.Length;
    }

    private static int GetRequiredLength(IReadOnlyCollection<byte> currentInput, byte? nextSymbol = null)
    {
        if (currentInput.Count > 0)
            return currentInput.First() == 1 ? 7 : 6;
        return nextSymbol == 1 ? 7 : 6;
    }

    private static string AddressKey(IEnumerable<byte> address) => string.Join('-', address);
}
