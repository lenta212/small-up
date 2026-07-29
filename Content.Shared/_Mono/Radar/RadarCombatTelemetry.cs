using System.Numerics;
using Robust.Shared.Maths;
using Robust.Shared.Serialization;

namespace Content.Shared._Mono.Radar;

[Serializable, NetSerializable]
public enum RadarThreatKind : byte
{
    None,
    Incoming,
    MissileLock,
}

[Serializable, NetSerializable]
public enum ShipHitSection : byte
{
    Bow,
    Stern,
    Port,
    Starboard,
}

[Serializable, NetSerializable]
public enum ShipHitResult : byte
{
    Blocked,
    Damaged,
    Destroyed,
    Shielded,
}

[Serializable, NetSerializable]
public readonly record struct OwnshipTelemetryNetData(
    Vector2 Position,
    Vector2 Velocity,
    float BrakeAcceleration);

[Serializable, NetSerializable]
public readonly record struct ShipHitReportNetData(
    TimeSpan EventTime,
    Vector2 ImpactPosition,
    Vector2 IncomingDirection,
    string WeaponName,
    ShipHitSection Section,
    ShipHitResult Result,
    float Damage);

public static class RadarCombatTelemetryMath
{
    private const float Epsilon = 1e-6f;

    public static bool TryCalculateBrakingDistance(float speed, float acceleration, out float distance)
    {
        distance = 0f;
        if (!float.IsFinite(speed) ||
            !float.IsFinite(acceleration) ||
            speed < 0f ||
            acceleration < 0f)
        {
            return false;
        }

        if (speed <= Epsilon)
            return true;

        if (acceleration <= Epsilon)
            return false;

        distance = speed * speed / (2f * acceleration);
        return float.IsFinite(distance);
    }

    public static ShipHitSection GetHitSection(Vector2 localImpact, Box2 localBounds)
    {
        var offset = localImpact - localBounds.Center;
        var halfWidth = MathF.Max(localBounds.Width * 0.5f, Epsilon);
        var halfHeight = MathF.Max(localBounds.Height * 0.5f, Epsilon);
        var normalizedX = offset.X / halfWidth;
        var normalizedY = offset.Y / halfHeight;
        if (MathF.Abs(normalizedY) >= MathF.Abs(normalizedX))
            return offset.Y >= 0f ? ShipHitSection.Bow : ShipHitSection.Stern;

        return offset.X >= 0f ? ShipHitSection.Starboard : ShipHitSection.Port;
    }

    /// <summary>
    /// Finds when a constant-velocity point first enters a local-space ship bound.
    /// The caller is responsible for transforming relative position and velocity
    /// into the ship's frame.
    /// </summary>
    public static bool TryGetIncomingIntersectionTime(
        Vector2 localPosition,
        Vector2 localVelocity,
        Box2 localBounds,
        float maximumTime,
        out float time)
    {
        time = 0f;
        if (!IsFinite(localPosition) ||
            !IsFinite(localVelocity) ||
            !float.IsFinite(maximumTime) ||
            maximumTime <= 0f ||
            localVelocity.LengthSquared() <= Epsilon)
        {
            return false;
        }

        var entry = 0f;
        var exit = maximumTime;
        if (!ClipAxis(
                localPosition.X,
                localVelocity.X,
                localBounds.Left,
                localBounds.Right,
                ref entry,
                ref exit) ||
            !ClipAxis(
                localPosition.Y,
                localVelocity.Y,
                localBounds.Bottom,
                localBounds.Top,
                ref entry,
                ref exit))
        {
            return false;
        }

        time = MathF.Max(0f, entry);
        return time <= maximumTime;
    }

    private static bool ClipAxis(
        float origin,
        float velocity,
        float lower,
        float upper,
        ref float entry,
        ref float exit)
    {
        if (MathF.Abs(velocity) <= Epsilon)
            return origin >= lower && origin <= upper;

        var axisEntry = (lower - origin) / velocity;
        var axisExit = (upper - origin) / velocity;
        if (axisEntry > axisExit)
            (axisEntry, axisExit) = (axisExit, axisEntry);

        entry = MathF.Max(entry, axisEntry);
        exit = MathF.Min(exit, axisExit);
        return entry <= exit && exit >= 0f;
    }

    private static bool IsFinite(Vector2 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y);
}
