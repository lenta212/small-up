using Robust.Shared.Serialization;

namespace Content.Shared.Shuttles.Events;

/// <summary>
/// Raised on the client when it wishes to not have 2 docking ports docked.
/// </summary>
[Serializable, NetSerializable]
public sealed class UndockRequestMessage : BoundUserInterfaceMessage
{
    /// <summary>
    /// Docking port on the shuttle controlled by the console.
    /// </summary>
    public NetEntity DockEntity;

    /// <summary>
    /// Exact reciprocal port currently connected to <see cref="DockEntity"/>.
    /// </summary>
    public NetEntity TargetDockEntity;
}
