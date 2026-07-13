using System;
using System.Numerics;
using Content.Shared._LuaM.Sector;

namespace Content.Server._LuaM.Sector;

/// <summary>
/// A lightweight, radar-only contact that gives an active sector visible traffic
/// without loading a shuttle grid, crew, collision-capable fixtures, or NPC logic.
/// </summary>
[RegisterComponent]
public sealed partial class LuaMSectorTrafficContactComponent : Component
{
    /// <summary>
    /// Radar signature family. Profiles only change presentation and the bounded
    /// dynamic-event hook; they never add a shuttle, grid, crew, or AI.
    /// </summary>
    [DataField]
    public LuaMSectorTrafficProfile Profile = LuaMSectorTrafficProfile.Civilian;

    /// <summary>
    /// Dynamic-event template selected for this contact when it was spawned.
    /// Kept on the entity so terminal output and audit tooling do not need to
    /// infer gameplay behavior from radar color.
    /// </summary>
    [DataField]
    public string DynamicEventTemplateId = string.Empty;

    /// <summary>
    /// Short round-local identifier shown in the sector terminal.
    /// </summary>
    [DataField]
    public string ContactCode = string.Empty;

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

/// <summary>
/// One recoverable item may be created by an intercepted contact. The item is
/// deleted after the task resolves or its safety TTL elapses, bounding leftovers
/// in long rounds.
/// </summary>
[RegisterComponent]
public sealed partial class LuaMSectorTrafficRecoveryComponent : Component
{
    [DataField]
    public string StoryId = string.Empty;

    [DataField]
    public TimeSpan ExpiresAt;
}
