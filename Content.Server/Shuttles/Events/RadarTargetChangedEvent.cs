namespace Content.Server.Shuttles.Events;

/// <summary>
/// Raised on a shuttle grid when its shared radar target changes.
/// </summary>
public sealed class RadarTargetChangedEvent : EntityEventArgs
{
    public EntityUid GridUid { get; }

    public RadarTargetChangedEvent(EntityUid gridUid)
    {
        GridUid = gridUid;
    }
}
