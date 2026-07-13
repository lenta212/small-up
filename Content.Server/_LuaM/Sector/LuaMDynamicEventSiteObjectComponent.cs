using Content.Shared._LuaM.Sector;
using Robust.Shared.Prototypes;

namespace Content.Server._LuaM.Sector;

[RegisterComponent]
[Access(typeof(LuaMSectorDynamicEventSystem), typeof(LuaMSectorTrafficSystem))]
public sealed partial class LuaMDynamicEventSiteObjectComponent : Component
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
    public string SiteObjectKind = "field-note";

    [DataField]
    public string RouteCalibrationSource = string.Empty;

    [DataField]
    public bool RouteSurveyApplied;

    [DataField]
    public string RouteSurveySummary = string.Empty;

    /// <summary>
    /// Whether this site object itself can resolve the linked story. Recoverable
    /// radar-intercept cargo disables this and uses LuaMSectorEvidence instead.
    /// </summary>
    [DataField]
    public bool DirectSubmissionAllowed = true;
}
