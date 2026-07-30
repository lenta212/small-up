using System.Numerics;
using Robust.Shared.Map;
using Robust.Shared.Serialization;

namespace Content.Shared._Mono.Radar;

[Serializable, NetSerializable]
public enum RadarBlipShape
{
    Circle,
    Square,
    GridAlignedBox,
    Triangle,
    Star,
    Diamond,
    Hexagon,
    Arrow,
    Ring
}

[Serializable, NetSerializable]
public sealed class GiveBlipsEvent : EntityEventArgs
{
    /// <summary>
    /// Radar console that produced this snapshot.
    /// </summary>
    public readonly NetEntity Radar;

    /// <summary>
    /// Client-generated request identifier echoed by the server.
    /// </summary>
    public readonly uint RequestId;

    /// <summary>
    /// Synchronized simulation time at which the server sampled this snapshot.
    /// The client uses this instead of receive time so network latency is included
    /// when extrapolating moving contacts.
    /// </summary>
    public readonly TimeSpan SampleTime;

    /// <summary>
    /// Palette of blip configs, basically an int->config map.
    /// </summary>
    public readonly List<BlipConfig> ConfigPalette;

    /// <summary>
    /// Blips are now (position, velocity, scale, color, shape).
    /// </summary>
    public readonly List<BlipNetData> Blips;

    /// <summary>
    /// Vectors for missile stuff like arcs, current target, etc
    /// </summary>
    public readonly List<MissileVectorNetData> Missiles;

    /// <summary>
    /// Hitscan lines to display on the radar as (start position, end position, thickness, color).
    /// </summary>
    public readonly List<HitscanNetData> HitscanLines;

    /// <summary>
    /// Authoritative flight telemetry for the shuttle carrying this radar.
    /// </summary>
    public readonly OwnshipTelemetryNetData? OwnshipTelemetry;

    /// <summary>
    /// Recent impacts on the grid carrying this radar, newest first.
    /// </summary>
    public readonly List<ShipHitReportNetData> HitReports;

    public GiveBlipsEvent(
        NetEntity radar,
        uint requestId,
        TimeSpan sampleTime,
        List<BlipConfig> configPalette,
        List<BlipNetData> blips,
        List<MissileVectorNetData> missiles,
        List<HitscanNetData> hitscans,
        OwnshipTelemetryNetData? ownshipTelemetry,
        List<ShipHitReportNetData> hitReports)
    {
        Radar = radar;
        RequestId = requestId;
        SampleTime = sampleTime;
        ConfigPalette = configPalette;
        Blips = blips;
        Missiles = missiles;
        HitscanLines = hitscans;
        OwnshipTelemetry = ownshipTelemetry;
        HitReports = hitReports;
    }
}

[Serializable, NetSerializable]
public sealed class RequestBlipsEvent : EntityEventArgs
{
    public readonly NetEntity Radar;
    public readonly uint RequestId;

    public RequestBlipsEvent(NetEntity radar, uint requestId)
    {
        Radar = radar;
        RequestId = requestId;
    }
}

[Serializable, NetSerializable]
public record struct BlipNetData
(
    NetEntity Uid,
    NetCoordinates Position,
    Vector2 Vel,
    Angle Rotation,
    ushort ConfigIndex,
    ushort? OnGridConfigIndex,
    bool IsWeaponProjectile,
    RadarThreatKind Threat,
    float? TimeToImpact
);

[Serializable, NetSerializable]
public record struct MissileVectorNetData
(
    NetEntity Uid,
    float Range,
    Angle ScanArc
);

[Serializable, NetSerializable]
public record struct HitscanNetData(
    Vector2 Start,
    Vector2 End,
    float Thickness,
    Color Color,
    NetEntity? OriginGrid);

[Serializable, NetSerializable, DataDefinition]
public partial record struct BlipConfig
{
    [DataField]
    public Box2 Bounds = new Box2(-0.5f, -0.5f, 0.5f, 0.5f);

    [DataField]
    public Color Color = Color.OrangeRed;

    [DataField]
    public RadarBlipShape Shape = RadarBlipShape.Circle;

    [DataField]
    public bool RespectZoom = false;

    [DataField]
    public bool Rotate = false;

    public BlipConfig() { }
}
