using Content.Server.Administration.Logs;
using Content.Server.Gateway.Components;
using Content.Server.Station.Systems;
using Content.Shared.Access.Systems;
using Content.Shared.CCVar;
using Content.Shared.Database;
using Content.Shared.Gateway;
using Content.Shared.Popups;
using Content.Shared.Teleportation.Components;
using Content.Shared.Teleportation.Systems;
using Content.Shared.UserInterface;
using Content.Shared.Verbs;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server.Gateway.Systems;

public sealed partial class GatewaySystem : EntitySystem
{
    [Dependency] private AccessReaderSystem _accessReader = default!;
    [Dependency] private IConfigurationManager _cfgManager = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IAdminLogManager _adminLogger = default!;
    [Dependency] private LinkedEntitySystem _linkedEntity = default!;
    [Dependency] private IPrototypeManager _protoManager = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private MetaDataSystem _metadata = default!;
    [Dependency] private StationSystem _stations = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<GatewayComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<GatewayComponent, ActivatableUIOpenAttemptEvent>(OnGatewayOpenAttempt);
        SubscribeLocalEvent<GatewayComponent, BoundUIOpenedEvent>(UpdateUserInterface);
        SubscribeLocalEvent<GatewayComponent, GatewayOpenPortalMessage>(OnOpenPortal);
    }

    public void SetEnabled(EntityUid uid, bool value, GatewayComponent? component = null)
    {
        if (!Resolve(uid, ref component) || component.Enabled == value)
            return;

        component.Enabled = value;
        UpdateAllGateways();
    }

    private void OnStartup(EntityUid uid, GatewayComponent comp, ComponentStartup args)
    {
        // no need to update ui since its just been created, just do portal
        UpdateAppearance(uid);
    }

    private void OnGatewayOpenAttempt(EntityUid uid, GatewayComponent component, ref ActivatableUIOpenAttemptEvent args)
    {
        if (!component.Enabled || !component.Interactable)
            args.Cancel();
    }

    private void UpdateUserInterface<T>(EntityUid uid, GatewayComponent comp, T args)
    {
        UpdateUserInterface(uid, comp);
    }

    public void UpdateAllGateways()
    {
        var query = AllEntityQuery<GatewayComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var comp, out var xform))
        {
            UpdateUserInterface(uid, comp, xform);
        }
    }

    private void UpdateUserInterface(EntityUid uid, GatewayComponent comp, TransformComponent? xform = null)
    {
        if (!Resolve(uid, ref xform))
            return;

        var destinations = new List<GatewayDestinationData>();
        var query = AllEntityQuery<GatewayComponent, TransformComponent>();

        var nextUnlock = TimeSpan.Zero;
        var unlockTime = TimeSpan.Zero;

        // Next unlock is based off of:
        // - Our station's unlock timer (if we have a station)
        // - If our map is a generated destination then use the generator that made it

        if (TryComp(_stations.GetOwningStation(uid), out GatewayGeneratorComponent? generatorComp) ||
            (TryComp(xform.MapUid, out GatewayGeneratorDestinationComponent? generatorDestination) &&
             TryComp(generatorDestination.Generator, out generatorComp)))
        {
            nextUnlock = generatorComp.NextUnlock;
            unlockTime = generatorComp.UnlockCooldown;
        }

        while (query.MoveNext(out var destUid, out var dest, out var destXform))
        {
            if (!dest.Enabled || destUid == uid)
                continue;

            // Show destination if either no destination comp on the map or it's ours.
            TryComp<GatewayGeneratorDestinationComponent>(destXform.MapUid, out var gatewayDestination);

            var destinationData = new GatewayDestinationData()
            {
                Entity = GetNetEntity(destUid),
                // Fallback to grid's ID if applicable.
                Name = dest.Name.IsEmpty && destXform.GridUid != null ? FormattedMessage.FromUnformatted(MetaData(destXform.GridUid.Value).EntityName) : dest.Name,
                Portal = HasComp<PortalComponent>(destUid),
                // If NextUnlock < CurTime it's unlocked, however
                // we'll always send the client if it's locked
                // It can just infer unlock times locally and not have to worry about it here.
                Locked = gatewayDestination != null && gatewayDestination.Locked
            };

            if (gatewayDestination != null &&
                !string.IsNullOrEmpty(gatewayDestination.Profile.Id) &&
                _protoManager.TryIndex(gatewayDestination.Profile, out GatewayWorldProfilePrototype? profile))
            {
                destinationData.HasIntel = true;
                destinationData.ProfileName = profile.Name;
                destinationData.ProfileDescription = profile.Description;
                destinationData.BiomeName = profile.BiomeName;
                destinationData.WeatherName = profile.WeatherName;
                destinationData.AtmosphereName = profile.AtmosphereName;
                destinationData.Resources = profile.Resources;
                destinationData.Hostiles = profile.Hostiles;
                destinationData.Threat = profile.Threat;
                destinationData.AccentColor = profile.AccentColor;
                destinationData.Address = gatewayDestination.Address;
                destinationData.Loaded = gatewayDestination.Loaded;
                destinationData.Orphaned = gatewayDestination.Orphaned;
                destinationData.GenerationState = gatewayDestination.GenerationState;
                destinationData.RotationState = gatewayDestination.RotationState;
                destinationData.RotationAt = GetRotationAt(gatewayDestination);
            }

            destinations.Add(destinationData);
        }

        _linkedEntity.GetLink(uid, out var current);

        var state = new GatewayBoundUserInterfaceState(
            destinations,
            GetNetEntity(current),
            comp.NextReady,
            comp.Cooldown,
            nextUnlock,
            unlockTime
        );

        _ui.SetUiState(uid, GatewayUiKey.Key, state);
    }

    private TimeSpan GetRotationAt(GatewayGeneratorDestinationComponent destination)
    {
        if (destination.RotationState == GatewayDestinationRotationState.Scheduled)
            return destination.RetireAt;

        if (destination.RotationState != GatewayDestinationRotationState.EmptyGracePeriod)
            return TimeSpan.Zero;

        var graceSeconds = _cfgManager.GetCVar(CCVars.GatewayGeneratorEmptyGrace);
        if (!float.IsFinite(graceSeconds) || graceSeconds <= 0f)
            return destination.EmptySince;

        var grace = TimeSpan.FromSeconds(
            Math.Min(graceSeconds, TimeSpan.MaxValue.TotalSeconds / 2d));
        return destination.EmptySince + grace;
    }

    private void UpdateAppearance(EntityUid uid)
    {
        _appearance.SetData(uid, GatewayVisuals.Active, HasComp<PortalComponent>(uid));
    }

    private void OnOpenPortal(EntityUid uid, GatewayComponent comp, GatewayOpenPortalMessage args)
    {
        if (GetNetEntity(uid) == args.Destination ||
            !comp.Enabled || !comp.Interactable)
        {
            return;
        }

        // if the gateway has an access reader check it before allowing opening
        var user = args.Actor;
        if (CheckAccess(user, uid, comp))
            return;

        // can't link if portal is already open on either side, the destination is invalid or on cooldown
        var desto = GetEntity(args.Destination);

        // If it's already open / not enabled / we're not ready DENY.
        if (!TryComp<GatewayComponent>(desto, out var dest) ||
            !dest.Enabled ||
            !TryComp(desto, out TransformComponent? destXform) ||
            destXform.MapUid == null ||
            HasComp<PortalComponent>(desto) ||
            _linkedEntity.GetLink(desto, out _) ||
            _timing.CurTime < _metadata.GetPauseTime(uid) + comp.NextReady ||
            _timing.CurTime < _metadata.GetPauseTime(desto) + dest.NextReady)
        {
            return;
        }

        var attempt = new AttemptGatewayOpenEvent(destXform.MapUid.Value, desto);
        RaiseLocalEvent(destXform.MapUid.Value, ref attempt);
        if (attempt.Cancelled)
            return;

        ClosePortal(
            uid,
            comp,
            false,
            user,
            GatewayPortalCloseReason.SwitchDestination);
        OpenPortal(uid, comp, desto, dest, user, destXform);
    }

    private bool OpenPortal(
        EntityUid uid,
        GatewayComponent comp,
        EntityUid dest,
        GatewayComponent destComp,
        EntityUid user,
        TransformComponent? destXform = null)
    {
        if (!Resolve(dest, ref destXform) || destXform.MapUid == null)
            return false;

        // Gateways are traversable in both directions. A symmetrical link also lets
        // safe world retirement close the source and return endpoints atomically.
        if (!_linkedEntity.TryLink(uid, dest))
            return false;

        var sourcePortal = EnsureComp<PortalComponent>(uid);
        var targetPortal = EnsureComp<PortalComponent>(dest);

        sourcePortal.CanTeleportToOtherMaps = true;
        targetPortal.CanTeleportToOtherMaps = true;

        sourcePortal.RandomTeleport = false;
        targetPortal.RandomTeleport = false;

        var openEv = new GatewayOpenEvent(destXform.MapUid.Value, dest);
        RaiseLocalEvent(destXform.MapUid.Value, ref openEv);

        // for ui
        comp.NextReady = _timing.CurTime + comp.Cooldown;

        _audio.PlayPvs(comp.OpenSound, uid);
        _audio.PlayPvs(comp.OpenSound, dest);

        UpdateUserInterface(uid, comp);
        UpdateAppearance(uid);
        UpdateAppearance(dest);

        _adminLogger.Add(
            LogType.Action,
            LogImpact.Medium,
            $"{ToPrettyString(user):player} opened gateway portal from {ToPrettyString(uid)} to {ToPrettyString(dest)}.");
        return true;
    }

    /// <summary>
    /// Closes a gateway and its linked endpoint. Used by safe generated-world retirement
    /// as well as normal gateway interaction.
    /// </summary>
    public void ClosePortal(
        EntityUid uid,
        GatewayComponent? comp = null,
        bool update = true,
        EntityUid? actor = null,
        GatewayPortalCloseReason reason = GatewayPortalCloseReason.System)
    {
        if (!Resolve(uid, ref comp))
            return;

        var hadPortal = HasComp<PortalComponent>(uid);
        RemComp<PortalComponent>(uid);
        if (!_linkedEntity.GetLink(uid, out var dest))
        {
            if (hadPortal)
                LogPortalClosed(uid, null, actor, reason);

            if (update)
            {
                UpdateUserInterface(uid, comp);
                UpdateAppearance(uid);
            }

            return;
        }

        if (TryComp<GatewayComponent>(dest, out var destComp))
        {
            // portals closed, put it on cooldown and let it eventually be opened again
            destComp.NextReady = _timing.CurTime + destComp.Cooldown;
        }

        _audio.PlayPvs(comp.CloseSound, uid);
        _audio.PlayPvs(comp.CloseSound, dest.Value);

        _linkedEntity.TryUnlink(uid, dest.Value);
        RemComp<PortalComponent>(dest.Value);
        LogPortalClosed(uid, dest, actor, reason);

        if (update)
        {
            UpdateUserInterface(uid, comp);
            UpdateAppearance(uid);
            UpdateAppearance(dest.Value);
        }
    }

    private void OnDestinationStartup(EntityUid uid, GatewayComponent comp, ComponentStartup args)
    {
        var query = AllEntityQuery<GatewayComponent>();
        while (query.MoveNext(out var gatewayUid, out var gateway))
        {
            UpdateUserInterface(gatewayUid, gateway);
        }

        UpdateAppearance(uid);
    }

    private void OnDestinationShutdown(EntityUid uid, GatewayComponent comp, ComponentShutdown args)
    {
        var query = AllEntityQuery<GatewayComponent>();
        while (query.MoveNext(out var gatewayUid, out var gateway))
        {
            UpdateUserInterface(gatewayUid, gateway);
        }
    }

    private void TryClose(EntityUid uid, EntityUid user)
    {
        // portal already closed so cant close it
        if (!_linkedEntity.GetLink(uid, out var source))
            return;

        // not allowed to close it
        if (CheckAccess(user, source.Value))
            return;

        ClosePortal(
            source.Value,
            actor: user,
            reason: GatewayPortalCloseReason.Manual);
    }

    private void LogPortalClosed(
        EntityUid source,
        EntityUid? destination,
        EntityUid? actor,
        GatewayPortalCloseReason reason)
    {
        var target = destination is { } dest && dest.IsValid()
            ? $" and {ToPrettyString(dest)}"
            : string.Empty;

        if (actor is { } user && user.IsValid())
        {
            _adminLogger.Add(
                LogType.Action,
                LogImpact.Medium,
                $"{ToPrettyString(user):player} closed gateway portal {ToPrettyString(source)}{target} ({reason}).");
        }
        else
        {
            _adminLogger.Add(
                LogType.Action,
                LogImpact.Medium,
                $"The gateway system closed gateway portal {ToPrettyString(source)}{target} ({reason}).");
        }
    }

    /// <summary>
    /// Checks the user's access. Makes popup and plays sound if missing access.
    /// Returns whether access was missing.
    /// </summary>
    private bool CheckAccess(EntityUid user, EntityUid uid, GatewayComponent? comp = null)
    {
        if (!Resolve(uid, ref comp))
            return false;

        if (_accessReader.IsAllowed(user, uid))
            return false;

        _popup.PopupEntity(Loc.GetString("gateway-access-denied"), user);
        _audio.PlayPvs(comp.AccessDeniedSound, uid);
        return true;
    }

    public void SetDestinationName(EntityUid gatewayUid, FormattedMessage gatewayName, GatewayComponent? gatewayComp = null)
    {
        if (!Resolve(gatewayUid, ref gatewayComp))
            return;

        gatewayComp.Name = gatewayName;
    }
}

/// <summary>
/// Raised directed on the target map when a GatewayDestination is attempted to be opened.
/// </summary>
[ByRefEvent]
public record struct AttemptGatewayOpenEvent(EntityUid MapUid, EntityUid GatewayDestinationUid)
{
    public readonly EntityUid MapUid = MapUid;
    public readonly EntityUid GatewayDestinationUid = GatewayDestinationUid;

    public bool Cancelled = false;
}

/// <summary>
/// Raised directed on the target map when a gateway is opened.
/// </summary>
[ByRefEvent]
public readonly record struct GatewayOpenEvent(EntityUid MapUid, EntityUid GatewayDestinationUid);

public enum GatewayPortalCloseReason : byte
{
    System,
    Manual,
    SwitchDestination,
    AutomaticRotation,
}
