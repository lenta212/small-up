using Content.Shared.Access.Components;
using Content.Shared.Shuttles.Components;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.UserInterface;
using Content.Shared.Popups;
using Robust.Shared.Timing;
using Content.Shared.Examine;

namespace Content.Shared.Shuttles.Systems;

/// <summary>
/// System that handles locking and unlocking shuttle consoles based on shuttle deeds.
/// </summary>
public abstract partial class SharedShuttleConsoleLockSystem : EntitySystem
{
    [Dependency] protected SharedAppearanceSystem Appearance = default!;
    [Dependency] protected SharedPopupSystem Popup = default!;
    [Dependency] protected IGameTiming Timing = default!;

    /// <inheritdoc/>
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ShuttleConsoleLockComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<ShuttleConsoleLockComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<ShuttleConsoleLockComponent, ActivatableUIOpenAttemptEvent>(OnUIOpenAttempt);
    }

    private void OnStartup(EntityUid uid, ShuttleConsoleLockComponent component, ComponentStartup args)
    {
        UpdateAppearance(uid, component);
    }

    private void OnExamined(EntityUid uid, ShuttleConsoleLockComponent component, ExaminedEvent args)
    {
        var effectiveLocked = GetEffectiveLockState(uid, component);
        args.PushMarkup(effectiveLocked ? Loc.GetString("shuttle-console-locked-examine") : Loc.GetString("shuttle-console-unlocked-examine"));
    }

    /// <summary>
    /// Gets the effective lock state for a console, using grid-level locks when available
    /// </summary>
    public bool GetEffectiveLockState(EntityUid console, ShuttleConsoleLockComponent component)
    {
        // Get the grid this console is on
        var transform = Transform(console);
        if (transform.GridUid == null)
            return component.Locked;

        var gridUid = transform.GridUid.Value;

        // If the grid has a grid lock component, use grid lock state
        if (TryComp<ShipGridLockComponent>(gridUid, out var gridLock))
        {
            // Grid lock state takes complete precedence over individual console state
            return gridLock.Locked;
        }

        // No grid lock, use individual console lock state (fallback for legacy consoles)
        return component.Locked;
    }

    /// <summary>
    /// Prevents using the console UI if it's locked
    /// </summary>
    protected virtual void OnUIOpenAttempt(EntityUid uid,
        ShuttleConsoleLockComponent component,
        ActivatableUIOpenAttemptEvent args)
    {
        if (GetEffectiveLockState(uid, component))
            args.Cancel();
    }

    protected void UpdateAppearance(EntityUid uid, ShuttleConsoleLockComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        if (!TryComp<AppearanceComponent>(uid, out var appearance))
            return;

        var effectiveLocked = GetEffectiveLockState(uid, component);
        Appearance.SetData(uid, ShuttleConsoleLockVisuals.Locked, effectiveLocked, appearance);
    }

    /// <summary>
    /// Sets the lock state for a ship grid
    /// </summary>
    protected void SetGridLockState(EntityUid gridUid, bool locked, string? shuttleId = null)
    {
        if (!TryComp<ShipGridLockComponent>(gridUid, out var gridLock))
        {
            // Create the component if it doesn't exist
            gridLock = AddComp<ShipGridLockComponent>(gridUid);
        }

        gridLock.Locked = locked;
        if (shuttleId != null)
            gridLock.ShuttleId = shuttleId;

        Dirty(gridUid, gridLock);
    }



    /// <summary>
    /// Ensures a grid has a ShipGridLockComponent if it has a deed
    /// </summary>
    protected void EnsureGridLockComponent(EntityUid gridUid, string? shuttleId = null)
    {
        // Only add to grids that have deeds
        if (!TryComp<ShuttleDeedComponent>(gridUid, out var deed))
            return;

        shuttleId ??= GetDeedShipKey(deed);

        // Add the component if it doesn't exist
        if (!TryComp<ShipGridLockComponent>(gridUid, out var gridLock))
        {
            gridLock = AddComp<ShipGridLockComponent>(gridUid);
            gridLock.Locked = true; // Ships start locked by default
            gridLock.ShuttleId = shuttleId;
            Dirty(gridUid, gridLock);
            return;
        }

        if (shuttleId == null || string.Equals(gridLock.ShuttleId, shuttleId, StringComparison.Ordinal))
            return;

        gridLock.ShuttleId = shuttleId;
        Dirty(gridUid, gridLock);
    }

    /// <summary>
    /// Returns the stable persistent key declared by a deed, or its runtime UID
    /// for an old deed that predates persistent ship identities.
    /// </summary>
    public static string? GetDeedShipKey(ShuttleDeedComponent deed)
    {
        if (!string.IsNullOrWhiteSpace(deed.PersistentShipId))
        {
            return Guid.TryParse(deed.PersistentShipId, out var shipId)
                ? shipId.ToString("D")
                : null;
        }

        return deed.ShuttleUid?.ToString();
    }

    /// <summary>
    /// Matches a deed against a lock key. Runtime UID matching is deliberately
    /// limited to legacy deeds and locks that do not declare a persistent ID.
    /// </summary>
    public static bool DeedMatchesShipKey(ShuttleDeedComponent deed, string? shipKey)
    {
        if (string.IsNullOrWhiteSpace(shipKey))
            return false;

        var lockHasPersistentId = Guid.TryParse(shipKey, out var lockShipId);
        var deedDeclaresPersistentId = !string.IsNullOrWhiteSpace(deed.PersistentShipId);
        if (lockHasPersistentId || deedDeclaresPersistentId)
        {
            return lockHasPersistentId &&
                   Guid.TryParse(deed.PersistentShipId, out var deedShipId) &&
                   deedShipId == lockShipId;
        }

        return deed.ShuttleUid is { } shuttleUid &&
               string.Equals(shuttleUid.ToString(), shipKey, StringComparison.Ordinal);
    }

    /// <summary>
    /// Determines whether two deeds refer to the same ship. If either deed
    /// declares a persistent identity, both must contain the same valid GUID.
    /// </summary>
    public static bool DeedsReferToSameShip(ShuttleDeedComponent first, ShuttleDeedComponent second)
    {
        var firstDeclaresPersistentId = !string.IsNullOrWhiteSpace(first.PersistentShipId);
        var secondDeclaresPersistentId = !string.IsNullOrWhiteSpace(second.PersistentShipId);
        if (firstDeclaresPersistentId || secondDeclaresPersistentId)
        {
            return Guid.TryParse(first.PersistentShipId, out var firstShipId) &&
                   Guid.TryParse(second.PersistentShipId, out var secondShipId) &&
                   firstShipId == secondShipId;
        }

        return first.ShuttleUid is { } firstUid &&
               second.ShuttleUid is { } secondUid &&
               firstUid == secondUid;
    }

    /// <summary>
    /// Updates the runtime and persistent identities carried by a deed.
    /// </summary>
    public void SetDeedShipIdentity(
        EntityUid deedUid,
        EntityUid? shuttleUid,
        Guid? persistentShipId,
        ShuttleDeedComponent? deed = null)
    {
        if (!Resolve(deedUid, ref deed))
            return;

        deed.ShuttleUid = shuttleUid;
        deed.PersistentShipId = persistentShipId is { } shipId && shipId != Guid.Empty
            ? shipId.ToString("D")
            : null;
        Dirty(deedUid, deed);
    }

    /// <summary>
    /// Attempts to unlock a console with the given ID card
    /// </summary>
    public virtual bool TryUnlock(EntityUid console, EntityUid idCard, ShuttleConsoleLockComponent? lockComp = null, IdCardComponent? idComp = null, EntityUid? user = null)
    {
        // Implemented in client and server separately
        return false;
    }
}
