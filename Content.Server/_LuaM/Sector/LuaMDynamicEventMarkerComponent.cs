using Content.Shared._LuaM.Sector;
using Robust.Shared.Prototypes;

namespace Content.Server._LuaM.Sector;

[RegisterComponent]
[Access(typeof(LuaMSectorDynamicEventSystem))]
public sealed partial class LuaMDynamicEventMarkerComponent : Component
{
    [DataField]
    public ProtoId<LuaMSectorStoryPrototype> Story;

    [DataField]
    public string TemplateId = string.Empty;

    [DataField]
    public string CreatedBy = string.Empty;

    [DataField]
    public string MarkerLocation = string.Empty;

    [DataField]
    public string ConditionRiskSummary = string.Empty;

    [DataField]
    public int RoutePingCount;

    [DataField]
    public string LastRoutePingActor = string.Empty;

    [DataField]
    public string LastRoutePingSummary = string.Empty;

    [DataField]
    public string RoutePingBaseLabel = string.Empty;

    [DataField]
    public bool StabilizedFieldPacketPrinted;

    [DataField]
    public string RouteCalibrationSource = string.Empty;

    [DataField]
    public bool SiteSurveyApplied;

    [DataField]
    public string SiteSurveySummary = string.Empty;

    [DataField]
    public bool SiteSurveyCommsRelayAvailable;

    [DataField]
    public bool RouteCalibrationRelayInherited;

    [DataField]
    public string PaperPrototype = "Paper";
}
