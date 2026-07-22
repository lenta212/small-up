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
}

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
