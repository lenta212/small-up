namespace Content.Server._LuaM.ShipPersistence;

/// <summary>
/// Stable identity carried by a ship grid across full-grid save/load cycles.
/// Ownership remains authoritative in <c>ShipOwnershipComponent</c> and the
/// persistent registry; this component only identifies the physical vessel.
/// </summary>
[RegisterComponent]
public sealed partial class LuaMShipIdentityComponent : Component
{
    /// <summary>
    /// Stable vessel identity. An empty value means that the grid has not yet
    /// been registered by <see cref="LuaMFullShipPersistenceSystem"/>.
    /// </summary>
    [DataField(customTypeSerializer: typeof(LuaMShipGuidSerializer))]
    public Guid ShipId;

    /// <summary>
    /// Revision represented by this physical copy. The durable registry is the
    /// authority for committing and leasing revisions.
    /// </summary>
    [DataField]
    public long SnapshotRevision;
}
