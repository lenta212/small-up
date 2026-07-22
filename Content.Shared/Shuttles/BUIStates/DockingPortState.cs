using Robust.Shared.Map;
using Robust.Shared.Serialization;
using Content.Shared.Shuttles.Components; // Frontier

namespace Content.Shared.Shuttles.BUIStates;

/// <summary>
/// State of each individual docking port for interface purposes
/// </summary>
[Serializable, NetSerializable]
public sealed class DockingPortState
{
    public string Name = string.Empty;

    public NetCoordinates Coordinates;
    public Angle Angle;
    public NetEntity Entity;
    public bool Connected => GridDockedWith != null;

    public NetEntity? GridDockedWith;

    /// <summary>
    /// The exact reciprocal docking port, when connected.
    /// </summary>
    public NetEntity? DockedWith;

    // Frontier: label, colors, type, receive only
    public string? LabelName;
    public Color RadarColor;
    public Color HighlightedRadarColor;
    public bool ReceiveOnly;
    public DockType DockType;
    // End Frontier
}
