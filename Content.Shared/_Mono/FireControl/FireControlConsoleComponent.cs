namespace Content.Shared._Mono.FireControl;

/// <summary>
/// These are for the consoles that provide the user interface for fire control servers.
/// </summary>
[RegisterComponent]
public sealed partial class FireControlConsoleComponent : Component
{
    [ViewVariables]
    public EntityUid? ConnectedServer = null;

    /// <summary>
    /// When we last made an admin log of someone firing using this console.
    /// Used to not put too much strain on server performance.
    /// </summary>
    [ViewVariables]
    public TimeSpan? NextLog = null;

    [DataField]
    public TimeSpan LogSpacing = TimeSpan.FromSeconds(1);

    [DataField]
    public float LogGridLookupRange = 1024f;

    /// <summary>
    /// Server-side input limits. Client throttles are only a UX optimization and
    /// cannot be trusted for network messages.
    /// </summary>
    [ViewVariables]
    public TimeSpan NextCursorUpdate;

    [ViewVariables]
    public TimeSpan NextFireUpdate;

    [ViewVariables]
    public TimeSpan NextRefreshUpdate;

    [DataField]
    public TimeSpan CursorUpdateInterval = TimeSpan.FromMilliseconds(75);

    [DataField]
    public TimeSpan FireUpdateInterval = TimeSpan.FromMilliseconds(50);

    [DataField]
    public TimeSpan RefreshUpdateInterval = TimeSpan.FromSeconds(1);
}
