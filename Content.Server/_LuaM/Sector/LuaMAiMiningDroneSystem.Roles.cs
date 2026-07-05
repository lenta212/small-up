using Robust.Shared.Maths;

namespace Content.Server._LuaM.Sector;

public sealed partial class LuaMAiMiningDroneSystem
{
    private static string NormalizeDroneRole(string role)
    {
        return role.Trim().ToLowerInvariant() switch
        {
            RoleGuard => RoleGuard,
            RoleScout => RoleScout,
            RoleLogistics => RoleLogistics,
            "builder" or "engineer" or "repairer" or "maintenance" => RoleRepair,
            "medical" or "doctor" => RoleMedic,
            "janitor" or "cleaner" => RoleService,
            "hauler" or "trader" or "cargo" => RoleLogistics,
            "security" => RoleGuard,
            "prospector" => RoleMiner,
            RoleRepair => RoleRepair,
            RoleMedic => RoleMedic,
            RoleService => RoleService,
            _ => RoleMiner,
        };
    }

    private static string PickSocialState(string role, bool closeContact)
    {
        if (closeContact)
            return "warning";

        return role switch
        {
            RoleGuard => "screening",
            RoleScout => "scanning",
            RoleLogistics => "guiding",
            RoleRepair => "repairing",
            RoleMedic => "triaging",
            RoleService => "servicing",
            _ => "observing",
        };
    }

    private static string PickSocialAction(string role, bool closeContact)
    {
        if (closeContact)
            return "safe_distance_warning";

        return role switch
        {
            RoleGuard => "security_screen",
            RoleScout => "route_mapping",
            RoleLogistics => "supply_route_hint",
            RoleRepair => "hull_integrity_check",
            RoleMedic => "medical_triage_hint",
            RoleService => "crew_support_hint",
            _ => "mining_route_scan",
        };
    }

    private static Color GetPatrolLightColor(string role)
    {
        return role switch
        {
            RoleGuard => GuardLightColor,
            RoleScout => ScoutLightColor,
            RoleLogistics => LogisticsLightColor,
            RoleRepair => Color.FromHex("#6ee7b7"),
            RoleMedic => Color.FromHex("#ff8cc6"),
            RoleService => Color.FromHex("#f7c66f"),
            _ => PatrolLightColor,
        };
    }

    private static Color GetContactLightColor(string role)
    {
        return role switch
        {
            RoleGuard => GuardLightColor,
            RoleScout => ScoutLightColor,
            RoleLogistics => LogisticsLightColor,
            RoleRepair => Color.FromHex("#6ee7b7"),
            RoleMedic => Color.FromHex("#ff8cc6"),
            RoleService => Color.FromHex("#f7c66f"),
            _ => ObservingLightColor,
        };
    }

    private static string PickContactTone(bool closeContact, int contactCount)
    {
        if (closeContact)
            return "close_contact";

        return contactCount <= 1 ? "first_contact" : "repeat_contact";
    }

    private static string BuildWorldTraceSummary(string role, string targetName, bool closeContact, int contactCount)
    {
        if (closeContact)
            return $"safe-distance warning #{contactCount} left for {targetName}";

        return role switch
        {
            RoleGuard => $"security perimeter mark #{contactCount} around {targetName}",
            RoleScout => $"route scan breadcrumb #{contactCount} near {targetName}",
            RoleLogistics => $"supply corridor hint #{contactCount} near {targetName}",
            RoleRepair => $"repair access mark #{contactCount} near {targetName}",
            RoleMedic => $"medical triage mark #{contactCount} near {targetName}",
            RoleService => $"service support mark #{contactCount} near {targetName}",
            _ => $"mining route scan #{contactCount} near {targetName}",
        };
    }
}
