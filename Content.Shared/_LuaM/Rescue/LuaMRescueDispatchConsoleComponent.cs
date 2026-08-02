using Content.Shared.Mobs;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared._LuaM.Rescue;

[RegisterComponent, NetworkedComponent]
public sealed partial class LuaMRescueDispatchConsoleComponent : Component
{
    [DataField]
    public string LastActionResult = string.Empty;
}

[Serializable, NetSerializable]
public enum LuaMRescueDispatchConsoleAction : byte
{
    Retry,
    Assign,
    Release,
}

[Serializable, NetSerializable]
public enum LuaMRescueDispatchConsoleUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed class LuaMRescueDispatchRefreshMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class LuaMRescueDispatchActionMessage(
    LuaMRescueDispatchConsoleAction action,
    NetEntity? target,
    NetEntity? agent) : BoundUserInterfaceMessage
{
    public LuaMRescueDispatchConsoleAction Action = action;
    public NetEntity? Target = target;
    public NetEntity? Agent = agent;
}

[Serializable, NetSerializable]
public sealed record LuaMRescueDispatchConsoleEntry(
    NetEntity Target,
    string TargetName,
    string Kind,
    bool Terminal,
    bool RequiredHandoff,
    int Attempts,
    int AgeSeconds,
    string LastStatus);

[Serializable, NetSerializable]
public sealed record LuaMRescueDispatchAgentEntry(
    NetEntity Agent,
    string AgentName,
    MobState MobState,
    bool Operational,
    NetEntity? Patient,
    string PatientName,
    string Activity,
    string LastStatus,
    int MedicalUnits,
    int EffectiveMedicalUnits,
    bool Resupplying,
    string SupplyStatus,
    bool InternalsActive,
    bool LifeSupportEmergency,
    float OxygenPressureKpa,
    int ReserveTankCount,
    float BestReservePressureKpa,
    int AutomaticSwapCount,
    int LifeSupportEmergencyCount,
    string LifeSupportStatus);

[Serializable, NetSerializable]
public sealed class LuaMRescueDispatchConsoleState(
    List<LuaMRescueDispatchConsoleEntry> dispatches,
    List<LuaMRescueDispatchAgentEntry> agents,
    int activeMissions,
    int pendingMissions,
    int terminalMissions,
    int operationalAgents,
    int incapacitatedAgents,
    string actionResult) : BoundUserInterfaceState
{
    public List<LuaMRescueDispatchConsoleEntry> Dispatches = dispatches;
    public List<LuaMRescueDispatchAgentEntry> Agents = agents;
    public int ActiveMissions = activeMissions;
    public int PendingMissions = pendingMissions;
    public int TerminalMissions = terminalMissions;
    public int OperationalAgents = operationalAgents;
    public int IncapacitatedAgents = incapacitatedAgents;
    public string ActionResult = actionResult;
}
