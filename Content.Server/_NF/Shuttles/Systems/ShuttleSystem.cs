// New Frontiers - This file is licensed under AGPLv3
// Copyright (c) 2024 New Frontiers Contributors
// See AGPLv3.txt for details.
using System.Numerics;
using Content.Server._Mono.Shuttles.Components;
using Content.Server._NF.Station.Components;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Events;
using Content.Shared._NF.Shuttles.Events;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Physics; // Mono
using Robust.Shared.Physics.Components;

namespace Content.Server.Shuttles.Systems;

public sealed partial class ShuttleSystem
{
    private const float MaxRadarTargetCoordinate = 1_000_000f;
    private const float TrackedTargetPositionTolerance = 64f;
    private const float SpaceFrictionStrength = 0.0075f;
    private const float DampenDampingStrength = 0.25f;
    private const float AnchorDampingStrength = 2.5f;
    private void NfInitialize()
    {
        SubscribeLocalEvent<ShuttleConsoleComponent, SetInertiaDampeningRequest>(OnSetInertiaDampening);
        SubscribeLocalEvent<ShuttleConsoleComponent, SetMaxShuttleSpeedRequest>(OnSetMaxShuttleSpeed);
        SubscribeLocalEvent<ShuttleConsoleComponent, SetRadarTargetRequest>(OnSetRadarTarget);
        SubscribeLocalEvent<ShuttleConsoleComponent, SetRadarTargetVisibilityRequest>(OnSetRadarTargetVisibility);
    }

    public bool SetInertiaDampening(EntityUid uid, PhysicsComponent physicsComponent, ShuttleComponent shuttleComponent, TransformComponent transform, InertiaDampeningMode mode)
    {
        if (!transform.GridUid.HasValue)
        {
            return false;
        }

        if (mode == InertiaDampeningMode.Query)
        {
            _console.RefreshShuttleConsoles(transform.GridUid.Value);
            return false;
        }

        // Mono - remove shuttle deed requirement, kill StationDampening
        if ((physicsComponent.BodyType & BodyType.Static) != 0)
        {
            return false;
        }

        shuttleComponent.BodyModifier = mode switch
        {
            InertiaDampeningMode.Off => SpaceFrictionStrength,
            InertiaDampeningMode.Dampen => DampenDampingStrength,
            InertiaDampeningMode.Anchor => AnchorDampingStrength,
            _ => DampenDampingStrength, // other values: default to some sane behaviour (assume normal dampening)
        };

        if (shuttleComponent.DampingModifier == shuttleComponent.BodyModifier)
            return true;

        if (shuttleComponent.DampingModifier != 0)
            shuttleComponent.DampingModifier = shuttleComponent.BodyModifier;
        _console.RefreshShuttleConsoles(transform.GridUid.Value);
        return true;
    }

    private void OnSetInertiaDampening(EntityUid uid, ShuttleConsoleComponent component, SetInertiaDampeningRequest args)
    {
        // Ensure that the entity requested is a valid shuttle (stations should not be togglable)
        if (!EntityManager.TryGetComponent(uid, out TransformComponent? transform) ||
            !transform.GridUid.HasValue ||
            !EntityManager.TryGetComponent(transform.GridUid, out PhysicsComponent? physicsComponent) ||
            !EntityManager.TryGetComponent(transform.GridUid, out ShuttleComponent? shuttleComponent))
        {
            return;
        }

        if (SetInertiaDampening(uid, physicsComponent, shuttleComponent, transform, args.Mode) && args.Mode != InertiaDampeningMode.Query)
            component.DampeningMode = args.Mode;
    }

    private void OnSetMaxShuttleSpeed(EntityUid uid, ShuttleConsoleComponent component, SetMaxShuttleSpeedRequest args)
    {
        // Ensure that the entity requested is a valid shuttle
        var xform = Transform(uid);
        if (!xform.GridUid.HasValue ||
            !TryComp<ShuttleComponent>(xform.GridUid, out var shuttleComponent) ||
            !TryComp<PilotComponent>(args.Actor, out var pilot))
        {
            return;
        }

        var maxSpeed = args.MaxSpeed;
        if (maxSpeed is { } speed)
            maxSpeed = Math.Max(speed, 0f);

        pilot.SetMaxVelocity = maxSpeed;

        // Refresh the shuttle consoles to update the UI
        _console.RefreshShuttleConsoles(xform.GridUid.Value);
    }

    private void OnSetRadarTarget(EntityUid uid, ShuttleConsoleComponent component, SetRadarTargetRequest args)
    {
        if (!TryGetRadarTargetShuttle(uid, out var gridUid, out var shuttle) ||
            !IsValidRadarTarget(args.Position))
        {
            return;
        }

        var position = args.Position;
        EntityUid? trackedEntity = null;

        if (args.TargetEntity != NetEntity.Invalid &&
            EntityManager.TryGetEntity(args.TargetEntity, out var target) &&
            target is { } targetUid &&
            targetUid != gridUid &&
            TryComp<ShuttleComponent>(targetUid, out _) &&
            TryComp<TransformComponent>(targetUid, out var targetXform) &&
            targetXform.MapID == Transform(gridUid).MapID &&
            (!TryComp<IFFComponent>(targetUid, out var iff) ||
             (iff.Flags & (IFFFlags.Hide | IFFFlags.HideLabel | IFFFlags.HideLabelAlways)) == 0))
        {
            var actualPosition = _transform.GetMapCoordinates(targetUid, targetXform).Position;
            if (Vector2.DistanceSquared(position, actualPosition) <=
                TrackedTargetPositionTolerance * TrackedTargetPositionTolerance)
            {
                position = actualPosition;
                trackedEntity = targetUid;
            }
        }

        shuttle.RadarTarget = position;
        shuttle.RadarTargetEntity = trackedEntity;
        shuttle.RadarTargetHidden = false;
        RaiseLocalEvent(new RadarTargetChangedEvent(gridUid));
    }

    private void OnSetRadarTargetVisibility(
        EntityUid uid,
        ShuttleConsoleComponent component,
        SetRadarTargetVisibilityRequest args)
    {
        if (!TryGetRadarTargetShuttle(uid, out var gridUid, out var shuttle) ||
            shuttle.RadarTarget == null ||
            shuttle.RadarTargetHidden == args.Hidden)
        {
            return;
        }

        shuttle.RadarTargetHidden = args.Hidden;
        RaiseLocalEvent(new RadarTargetChangedEvent(gridUid));
    }

    private bool TryGetRadarTargetShuttle(
        EntityUid consoleUid,
        out EntityUid gridUid,
        out ShuttleComponent shuttle)
    {
        gridUid = default;
        shuttle = default!;

        if (!TryComp<TransformComponent>(consoleUid, out var consoleXform) ||
            consoleXform.GridUid is not { } grid ||
            !TryComp<ShuttleComponent>(grid, out var shuttleComponent))
        {
            return false;
        }

        gridUid = grid;
        shuttle = shuttleComponent;
        return true;
    }

    private static bool IsValidRadarTarget(Vector2 position)
    {
        return float.IsFinite(position.X) &&
               float.IsFinite(position.Y) &&
               MathF.Abs(position.X) <= MaxRadarTargetCoordinate &&
               MathF.Abs(position.Y) <= MaxRadarTargetCoordinate;
    }

    public InertiaDampeningMode NfGetInertiaDampeningMode(EntityUid entity)
    {
        if (!EntityManager.TryGetComponent<TransformComponent>(entity, out var xform))
            return InertiaDampeningMode.Dampen;

        // Not a shuttle, shouldn't be togglable // Mono - remove shuttle deed requirement, kill StationDampening
        if (TryComp<PhysicsComponent>(xform.GridUid, out var body) && (body.BodyType & BodyType.Static) != 0)
            return InertiaDampeningMode.Station;

        if (!EntityManager.TryGetComponent(xform.GridUid, out ShuttleComponent? shuttle))
            return InertiaDampeningMode.Dampen;

        if (shuttle.BodyModifier >= AnchorDampingStrength)
            return InertiaDampeningMode.Anchor;
        else if (shuttle.BodyModifier <= SpaceFrictionStrength)
            return InertiaDampeningMode.Off;
        else
            return InertiaDampeningMode.Dampen;
    }
}
