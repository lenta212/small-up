using System;
using System.Globalization;

namespace Content.Server._LuaM.Progression;

/// <summary>
/// Stable identifier for a seven-day campaign shift. It is deliberately independent
/// from technical round identifiers so process restarts do not reset progression.
/// </summary>
public readonly record struct LuaMCampaignShiftId(long Value)
{
    public override string ToString()
    {
        return "campaign-shift-" + Value.ToString("D8", CultureInfo.InvariantCulture);
    }
}

public readonly record struct LuaMCampaignShiftPeriod(
    LuaMCampaignShiftId Id,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc)
{
    public bool Contains(DateTimeOffset timestamp)
    {
        var utc = timestamp.ToUniversalTime();
        return utc >= StartsAtUtc && utc < EndsAtUtc;
    }
}

/// <summary>
/// Pure deterministic clock for grouping any number of technical rounds into the
/// same seven-day campaign shift.
/// </summary>
public static class LuaMCampaignShiftClock
{
    public static readonly TimeSpan ShiftDuration = TimeSpan.FromDays(7);

    public static LuaMCampaignShiftPeriod Resolve(DateTimeOffset timestamp, DateTimeOffset epoch)
    {
        var timestampUtc = timestamp.ToUniversalTime();
        var epochUtc = epoch.ToUniversalTime();
        var deltaTicks = timestampUtc.Ticks - epochUtc.Ticks;
        var sequence = FloorDivide(deltaTicks, ShiftDuration.Ticks);
        var startsAt = epochUtc.AddTicks(checked(sequence * ShiftDuration.Ticks));

        return new LuaMCampaignShiftPeriod(
            new LuaMCampaignShiftId(sequence),
            startsAt,
            startsAt.Add(ShiftDuration));
    }

    private static long FloorDivide(long value, long divisor)
    {
        var quotient = value / divisor;
        var remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }
}
