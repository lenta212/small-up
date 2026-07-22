using Robust.Shared.Player;

namespace Content.Shared._LuaM.AntiCheat;

/// <summary>
/// High-confidence server-side validation failures that may be correlated by the anti-cheat tracker.
/// </summary>
public enum LuaMAntiCheatSignalKind : byte
{
    RemoteBoundUi,
    BoundUiRateLimit,
    InvalidShootCoordinates,
    InvalidShootTarget,
    PredictedHitFlood,
}

/// <summary>
/// Local, non-networked signal emitted after the server rejects an invalid client request.
/// </summary>
public sealed class LuaMAntiCheatSignalEvent(
    ICommonSession session,
    LuaMAntiCheatSignalKind kind,
    EntityUid? subject = null) : EntityEventArgs
{
    public readonly ICommonSession Session = session;
    public readonly LuaMAntiCheatSignalKind Kind = kind;
    public readonly EntityUid? Subject = subject;
}
