using System;
using System.Numerics;

namespace Content.Server._LuaM.Sector;

/// <summary>
/// A lightweight, radar-only contact that gives an active sector visible traffic
/// without loading a shuttle grid, crew, collision-capable fixtures, or NPC logic.
/// </summary>
[RegisterComponent]
public sealed partial class LuaMSectorTrafficContactComponent : Component
{
    /// <summary>
    /// Absolute server time at which the contact is replaced.
    /// </summary>
    [DataField]
    public TimeSpan ExpiresAt;

    /// <summary>
    /// Map-space velocity assigned to the contact. Stored for auditability and tests;
    /// the physics component is authoritative for movement.
    /// </summary>
    [DataField]
    public Vector2 RouteVelocity;
}
