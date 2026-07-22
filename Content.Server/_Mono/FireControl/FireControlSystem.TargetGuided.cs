using Content.Server._Mono.Projectiles.TargetGuided;
using Content.Shared._Mono.FireControl;
using Content.Shared.Projectiles;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Shuttles.Components;
using EntityCoordinates = Robust.Shared.Map.EntityCoordinates;

namespace Content.Server._Mono.FireControl;

public sealed partial class FireControlSystem
{
    [Dependency] private TargetGuidedSystem _targetGuided = null!;

    /// <summary>
    /// List of active guided missiles that need cursor position updates
    /// </summary>
    private readonly HashSet<EntityUid> _activeMissiles = new();

    /// <summary>
    /// Map of console entities to their current mouse positions
    /// </summary>
    private readonly Dictionary<EntityUid, EntityCoordinates> _consoleMousePositions = new();

    /// <summary>
    /// AmmoShotEvent is raised synchronously while a console fire message is handled.
    /// Keep that console as context so a missile is never bound to an arbitrary console
    /// connected to the same server.
    /// </summary>
    private EntityUid? _firingConsole;

    /// <summary>
    /// Registers handlers for events related to target guided projectiles.
    /// </summary>
    private void InitializeTargetGuided()
    {
        SubscribeLocalEvent<GunComponent, AmmoShotEvent>(OnTargetGuidedShot);
        SubscribeLocalEvent<TargetGuidedComponent, ComponentShutdown>(OnGuidedMissileShutdown);
        // Track fire messages to update cursor positions
        SubscribeLocalEvent<FireControlConsoleComponent, FireControlConsoleFireEvent>(OnConsoleFireEvent);
    }

    /// <summary>
    /// Track console fire events to update cursor positions
    /// </summary>
    private void OnConsoleFireEvent(EntityUid uid, FireControlConsoleComponent component, FireControlConsoleFireEvent args)
    {
        OnGuidanceUpdate(uid, GetCoordinates(args.Coordinates));
    }

    /// <summary>
    /// Subscribed to AmmoShotEvent to check for and configure guided projectiles.
    /// </summary>
    private void OnTargetGuidedShot(EntityUid uid, GunComponent component, AmmoShotEvent args)
    {
        if (args.FiredProjectiles.Count == 0)
            return;

        // Get the shooter entity
        EntityUid? shooter = null;
        if (TryComp<ProjectileComponent>(args.FiredProjectiles[0], out var projectileComp))
        {
            shooter = projectileComp.Shooter;
        }

        // We need to get the target coordinates from the gun component
        var targetCoords = component.ShootCoordinates;
        if (!targetCoords.HasValue || !targetCoords.Value.IsValid(EntityManager))
            return;

        // Bind guidance to the exact console currently firing. Choosing the first
        // console connected to a server mixes cursor streams on multi-console ships.
        EntityUid? controllingConsole = _firingConsole;
        if (TryComp<FireControllableComponent>(uid, out var fireControllable) &&
            fireControllable.ControllingServer != null &&
            controllingConsole is { } consoleUid &&
            TryComp<FireControlConsoleComponent>(consoleUid, out var console) &&
            console.ConnectedServer == fireControllable.ControllingServer)
        {
            _consoleMousePositions[consoleUid] = targetCoords.Value;
        }
        else
            controllingConsole = null;

        foreach (var projectileUid in args.FiredProjectiles)
        {
            if (!TryComp<TargetGuidedComponent>(projectileUid, out var guidedComp))
                continue;

            // If firing ship is in FTL, missile won't have guidance
            if (shooter.HasValue && Transform(shooter.Value).GridUid is { } shipGrid)
            {
                if (TryComp<FTLComponent>(shipGrid, out _))
                {
                    // Skip guidance setup if ship is in FTL
                    continue;
                }
            }

            // Set up initial target for guided missile
            _targetGuided.SetTargetPosition(projectileUid, targetCoords.Value, guidedComp);

            // Record the console this was fired from for position updates
            if (controllingConsole.HasValue)
            {
                guidedComp.ControllingConsole = controllingConsole;
                _activeMissiles.Add(projectileUid);
            }
        }
    }

    /// <summary>
    /// Cleanup guided missiles when they're destroyed
    /// </summary>
    private void OnGuidedMissileShutdown(EntityUid uid, TargetGuidedComponent component, ComponentShutdown args)
    {
        _activeMissiles.Remove(uid);
    }

    /// <summary>
    /// Updates the cursor position for any tracking missiles from a given console
    /// </summary>
    public void OnGuidanceUpdate(EntityUid consoleUid, EntityCoordinates targetCoordinates)
    {
        // Store the updated position for this console
        _consoleMousePositions[consoleUid] = targetCoordinates;

        // Update any active missiles being controlled by this console
        foreach (var missile in _activeMissiles)
        {
            if (!TryComp<TargetGuidedComponent>(missile, out var guidedComp))
                continue;

            if (guidedComp.ControllingConsole != consoleUid)
                continue;

            // Don't update position if the missile's ship is in FTL
            if (TryComp<ProjectileComponent>(missile, out var projectileComp) &&
                projectileComp.Shooter.HasValue &&
                Transform(projectileComp.Shooter.Value).GridUid is { } shipGrid &&
                TryComp<FTLComponent>(shipGrid, out _))
            {
                continue;
            }

            _targetGuided.SetTargetPosition(missile, targetCoordinates, guidedComp);
        }
    }

    /// <summary>
    /// Helper method to get the current position of a specific console
    /// </summary>
    public EntityCoordinates? GetConsolePosition(EntityUid consoleUid)
    {
        if (_consoleMousePositions.TryGetValue(consoleUid, out var coords))
            return coords;

        return null;
    }

    private void RemoveConsoleGuidance(EntityUid consoleUid)
    {
        _consoleMousePositions.Remove(consoleUid);
    }
}
