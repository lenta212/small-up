using System.Collections.Immutable;
using Content.Shared.Preferences.Loadouts;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server.Station.Components;

/// <summary>
/// A paid spawn loadout that has been selected, but must not be materialized or
/// charged until the exact player session is attached to this entity.
/// </summary>
[RegisterComponent]
public sealed partial class PendingPaidLoadoutComponent : Component
{
    internal ICommonSession? ExpectedSession;

    internal int ExpectedSlot = -1;

    internal int ExpectedProfileId;

    internal int Cost;

    /// <summary>
    /// Process-local prototype generation captured with <see cref="Gear"/>.
    /// Any prototype reload invalidates the pending purchase, including reloads
    /// of entity prototypes referenced by a loadout snapshot.
    /// </summary>
    internal long PrototypeRevision;

    internal readonly List<PendingPaidLoadoutEntry> Gear = new();

    /// <summary>
    /// Zero-price mandatory-category fallbacks to deliver if the durable debit
    /// is definitely rejected while this exact spawn context is still current.
    /// </summary>
    internal readonly List<PendingPaidLoadoutEntry> FallbackGear = new();

    /// <summary>
    /// Prevents duplicate attach events from starting the same durable debit.
    /// </summary>
    internal bool Started;
}

/// <summary>
/// Immutable price and materialization quote captured while the loadout is
/// selected. The fingerprint binds the loadout and referenced starting-gear
/// contents; the owning component's prototype revision additionally fences all
/// referenced entity-prototype reloads.
/// </summary>
public readonly record struct PendingPaidLoadoutEntry(
    ProtoId<LoadoutPrototype> Prototype,
    int Price,
    string ValueFingerprint,
    ImmutableArray<PendingPaidLoadoutMaterialization> Materialization);

/// <summary>
/// One ordered materialization pass. Loadout starting gear is captured as the
/// first pass and inline loadout gear as the second, preserving replacement
/// semantics without consulting mutable prototypes after commit.
/// </summary>
public readonly record struct PendingPaidLoadoutMaterialization(
    ImmutableArray<PendingPaidLoadoutEquipment> Equipment,
    ImmutableArray<EntProtoId> Inhand,
    ImmutableArray<PendingPaidLoadoutStorage> Storage);

public readonly record struct PendingPaidLoadoutEquipment(
    string Slot,
    EntProtoId Entity);

public readonly record struct PendingPaidLoadoutStorage(
    string Slot,
    EntProtoId Entity);
