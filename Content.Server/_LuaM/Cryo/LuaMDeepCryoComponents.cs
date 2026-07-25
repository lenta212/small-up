using Content.Server.Database;
using Robust.Shared.Network;

namespace Content.Server._LuaM.Cryo;

/// <summary>
/// Stable database identity of the character currently inhabiting this body.
/// This is runtime-only on purpose: a restored snapshot must be rebound to the
/// authenticated player/profile and may never trust identity from its payload.
/// </summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class LuaMDeepCryoIdentityComponent : Component
{
    public NetUserId UserId;
    public int ProfileId;
    public int Slot;
    public long SlotGeneration;
    /// <summary>
    /// Exact profile lifecycle revision at which this body became the playable
    /// authority. The sentinel prevents a body that bypassed the spawn/wake
    /// handshake from accidentally inheriting legitimate epoch zero.
    /// </summary>
    public long LifecycleRevision = -1;
    /// <summary>
    /// Durable owner token for the playable body. A body without this exact
    /// database presence authority may never enter Store or regain control.
    /// </summary>
    public Guid PresenceLeaseId;
    public DbLuaMCharacterPresencePhase PresencePhase;
    public long? PresenceSnapshotId;
    /// <summary>
    /// Optimistic revision used only by renew/release. Store deliberately binds
    /// token + lifecycle epoch so an expiry-only renewal cannot rebase payload.
    /// </summary>
    public long PresenceLeaseRevision = -1;
    public DateTime PresenceLeaseExpiresAtUtc;
}

/// <summary>
/// Local fail-closed fence applied before a playable presence lease can expire.
/// The body remains the sole retained character state but is moved out of the
/// world and cannot be controlled until the same durable token is renewed.
/// </summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class LuaMDeepCryoPresenceSuspendedComponent : Component;

/// <summary>
/// Fences the asynchronous durable store. While present, the body must remain
/// in its pod and callers must reject eject, extraction and duplicate stores.
/// </summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class LuaMDeepCryoPendingComponent : Component
{
    public Guid OperationId;
    public EntityUid Pod;
    public LuaMDeepCryoSource Source;
}

/// <summary>
/// Links the in-memory body to its durable snapshot. The database remains the
/// authority; this marker only enables a no-deserialize same-process return.
/// </summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class LuaMDeepCryoStoredComponent : Component
{
    public NetUserId UserId;
    public int ProfileId;
    public int Slot;
    public long SnapshotId;
    public long Revision;
}

/// <summary>
/// Main-thread reservation held while a claimed snapshot is consumed. This
/// keeps the chosen empty pod from being occupied in the consume-to-insert gap.
/// </summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class LuaMDeepCryoRestoreReservationComponent : Component
{
    public Guid LeaseId;
}

public enum LuaMDeepCryoSource : byte
{
    FrontierCryoSleep,
    UpstreamCryostorage,
}
