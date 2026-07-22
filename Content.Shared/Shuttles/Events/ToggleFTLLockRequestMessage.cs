using Robust.Shared.Serialization;

namespace Content.Shared.Shuttles.Events;

/// <summary>
/// Raised on the client when it wishes to set FTL lock state for docked shuttles.
/// </summary>
[Serializable, NetSerializable]
public sealed class ToggleFTLLockRequestMessage : BoundUserInterfaceMessage
{
    /// <summary>
    /// The desired state for the FTL lock (true to enable, false to disable)
    /// </summary>
    public bool Enabled { get; }

    public ToggleFTLLockRequestMessage(bool enabled)
    {
        Enabled = enabled;
    }
}
