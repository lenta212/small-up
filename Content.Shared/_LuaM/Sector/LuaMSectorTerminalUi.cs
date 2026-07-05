using Robust.Shared.Serialization;

namespace Content.Shared._LuaM.Sector;

[Serializable, NetSerializable]
public enum LuaMSectorTerminalUiKey : byte
{
    Key
}

[Serializable, NetSerializable]
public enum LuaMSectorTerminalAction : byte
{
    Refresh,
    RequestDynamicEvent,
    PingActiveRouteMarker,
    PrintLeadReport,
    PrintRuntimeCoordinatePacket,
    PrintRuntimeClosureReport,
    PrintInsuranceDocket,
    PrintInsuranceClaimVoucher,
    PrintRegistryDocket,
    PrintCharterVoucher
}

[Serializable, NetSerializable]
public sealed class LuaMSectorTerminalActionMessage : BoundUserInterfaceMessage
{
    public LuaMSectorTerminalAction Action { get; }
    public string StoryId { get; }
    public string TemplateId { get; }

    public LuaMSectorTerminalActionMessage(LuaMSectorTerminalAction action, string storyId = "", string templateId = "")
    {
        Action = action;
        StoryId = storyId;
        TemplateId = templateId;
    }
}
