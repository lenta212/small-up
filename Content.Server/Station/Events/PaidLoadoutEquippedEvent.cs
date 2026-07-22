namespace Content.Server.Station.Events;

/// <summary>
/// Raised exactly once after every item in a durably-paid spawn loadout has
/// been materialized and delivered. Delivery prefers the quoted equipment,
/// hand, or storage target and safely falls back to the player's location.
/// </summary>
[ByRefEvent]
public record struct PaidLoadoutEquippedEvent(EntityUid Entity)
{
    public readonly EntityUid Entity = Entity;
}
