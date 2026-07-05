using Content.Shared.Examine;
using System.Linq;

namespace Content.Server._LuaM.Sector;

public sealed partial class LuaMAiEcologyExamineSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<LuaMAiBaseZoneComponent, ExaminedEvent>(OnBaseZoneExamined);
        SubscribeLocalEvent<LuaMAiDroneTraceComponent, ExaminedEvent>(OnDroneTraceExamined);
        SubscribeLocalEvent<LuaMAiMiningDroneComponent, ExaminedEvent>(OnDroneExamined);
        SubscribeLocalEvent<LuaMAiLogisticsShipComponent, ExaminedEvent>(OnLogisticsShipExamined);
    }

    private void OnBaseZoneExamined(Entity<LuaMAiBaseZoneComponent> ent, ref ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;

        var zone = ent.Comp;
        args.PushText(Loc.GetString(
            "luam-ai-base-zone-examine",
            ("zone", ReadableZone(zone.ZoneType)),
            ("label", Readable(zone.Label, zone.ZoneType)),
            ("base", Readable(zone.BaseId, "LuaM-AI-Base")),
            ("purpose", ZonePurpose(zone.ZoneType))));

        if (!string.IsNullOrWhiteSpace(zone.ActiveCompensation))
        {
            args.PushText(Loc.GetString(
                "luam-ai-base-zone-compensation-examine",
                ("severity", zone.ActiveCompensationSeverity),
                ("weakness", Readable(zone.ActiveWeaknessTitle, zone.ActiveWeaknessId)),
                ("role", Readable(zone.SuggestedRole, "drone")),
                ("resource", Readable(zone.SuggestedResource, "base resource")),
                ("compensation", zone.ActiveCompensation)));
        }
    }

    private void OnDroneTraceExamined(Entity<LuaMAiDroneTraceComponent> ent, ref ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;

        var trace = ent.Comp;
        if (trace.TraceKind.Trim().ToLowerInvariant() == "ship_duty")
        {
            args.PushText(Loc.GetString(
                "luam-ai-drone-duty-trace-examine",
                ("role", ReadableRole(trace.DroneRole)),
                ("ship", Readable(trace.TargetName, "AI ship")),
                ("station", Readable(trace.WorkStation, "ship station")),
                ("cycle", trace.ContactCount),
                ("effect", ReadableEffect(trace.WorkEffect, trace.ContactTone)),
                ("summary", Readable(trace.Summary, "crew duty trace"))));
            return;
        }

        args.PushText(Loc.GetString(
            "luam-ai-drone-trace-examine",
            ("role", ReadableRole(trace.DroneRole)),
            ("target", Readable(trace.TargetName, "unknown contact")),
            ("count", trace.ContactCount),
            ("tone", ReadableTone(trace.ContactTone)),
            ("summary", Readable(trace.Summary, "route trace"))));
    }

    private void OnDroneExamined(Entity<LuaMAiMiningDroneComponent> ent, ref ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;

        var drone = ent.Comp;
        args.PushText(Loc.GetString(
            "luam-ai-drone-status-examine",
            ("id", Readable(drone.DroneId, ToPrettyString(ent.Owner))),
            ("role", ReadableRole(drone.DroneRole)),
            ("state", ReadableState(drone.State)),
            ("base", Readable(drone.BaseId, "LuaM-AI-Base"))));

        if (!string.IsNullOrWhiteSpace(drone.CrewAssignment) ||
            !string.IsNullOrWhiteSpace(drone.CrewDirective) ||
            !string.IsNullOrWhiteSpace(drone.VesselId))
        {
            args.PushText(Loc.GetString(
                "luam-ai-drone-crew-examine",
                ("origin", Readable(drone.DisplayName, Readable(drone.VesselId, "AI ship"))),
                ("assignment", Readable(drone.CrewAssignment, ReadableRole(drone.DroneRole))),
                ("station", Readable(drone.CrewStation, "unassigned station")),
                ("directive", Readable(drone.CrewDirective, "patrol and await AI base orders")),
                ("priority", drone.CrewPriority)));
        }

        if (!string.IsNullOrWhiteSpace(drone.LastCrewDutyReport))
        {
            args.PushText(Loc.GetString(
                "luam-ai-drone-duty-examine",
                ("cycles", drone.CrewDutyCycles),
                ("effect", Readable(drone.LastCrewDutyEffect, "crew duty")),
                ("report", drone.LastCrewDutyReport)));
        }

        if (TryComp<LuaMAiDroneTaskComponent>(ent.Owner, out var task))
        {
            args.PushText(Loc.GetString(
                "luam-ai-drone-task-examine",
                ("task", ReadableTask(task.TaskType)),
                ("zone", ReadableZone(task.ZoneType)),
                ("stage", ReadableStage(task.TaskStage)),
                ("cycles", task.CompletedCycles),
                ("report", Readable(task.LastReport, "no report yet"))));

            if (!string.IsNullOrWhiteSpace(task.Compensation))
            {
                args.PushText(Loc.GetString(
                    "luam-ai-drone-task-compensation-examine",
                    ("severity", task.CompensationSeverity),
                    ("weakness", Readable(task.WeaknessTitle, task.WeaknessId)),
                    ("role", Readable(task.CompensationRole, "drone")),
                    ("resource", Readable(task.CompensationResource, "base resource")),
                    ("compensation", task.Compensation)));
            }
        }
        else
        {
            args.PushText(Loc.GetString("luam-ai-drone-task-none-examine"));
        }

        if (drone.LastContactCount > 0 || !string.IsNullOrWhiteSpace(drone.LastSeenName))
        {
            args.PushText(Loc.GetString(
                "luam-ai-drone-contact-examine",
                ("target", Readable(drone.LastSeenName, "unknown contact")),
                ("count", drone.LastContactCount),
                ("tone", ReadableTone(drone.LastContactTone)),
                ("unique", drone.UniquePeopleSeen)));
        }

        if (!string.IsNullOrWhiteSpace(drone.LastWorldTraceSummary))
        {
            args.PushText(Loc.GetString(
                "luam-ai-drone-trace-last-examine",
                ("summary", drone.LastWorldTraceSummary)));
        }
    }

    private void OnLogisticsShipExamined(Entity<LuaMAiLogisticsShipComponent> ent, ref ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;

        var ship = ent.Comp;
        var desiredCrew = ship.CrewRoleManifest.Count;
        var activeCrew = CountActiveShipCrew(ent.Owner);
        var manifest = BuildReadableManifest(ship);

        args.PushText(Loc.GetString(
            "luam-ai-ship-status-examine",
            ("name", Readable(ship.DisplayName, Readable(ship.VesselId, ToPrettyString(ent.Owner)))),
            ("role", ReadableShipRole(ship.Role)),
            ("vessel", Readable(ship.VesselId, "unknown vessel")),
            ("base", Readable(ship.BaseId, "LuaM-AI-Base"))));

        args.PushText(Loc.GetString(
            "luam-ai-ship-crew-examine",
            ("manifest", manifest),
            ("active", activeCrew),
            ("desired", desiredCrew),
            ("source", Readable(ship.CrewManifestSource, "pending"))));

        if (!string.IsNullOrWhiteSpace(ship.CrewProfileId) ||
            !string.IsNullOrWhiteSpace(ship.CrewProfileSummary) ||
            ship.CrewStationPlan.Count > 0)
        {
            args.PushText(Loc.GetString(
                "luam-ai-ship-profile-examine",
                ("profile", Readable(ship.CrewProfileId, "pending profile")),
                ("summary", Readable(ship.CrewProfileSummary, "station profile pending")),
                ("stations", BuildReadableStationPlan(ship))));
        }

        if (!string.IsNullOrWhiteSpace(ship.LastCrewReport))
        {
            args.PushText(Loc.GetString(
                "luam-ai-ship-report-examine",
                ("report", ship.LastCrewReport)));
        }

        if (!string.IsNullOrWhiteSpace(ship.LastCrewDutyReport))
        {
            args.PushText(Loc.GetString(
                "luam-ai-ship-duty-examine",
                ("report", ship.LastCrewDutyReport)));
        }
    }

    private int CountActiveShipCrew(EntityUid shipUid)
    {
        var count = 0;
        var query = EntityQueryEnumerator<LuaMAiMiningDroneComponent>();
        while (query.MoveNext(out var uid, out var drone))
        {
            if (drone.ParentShip == shipUid && !TerminatingOrDeleted(uid))
                count++;
        }

        return count;
    }

    private string BuildReadableManifest(LuaMAiLogisticsShipComponent ship)
    {
        if (ship.CrewRoleManifest.Count == 0)
            return Loc.GetString("luam-ai-ship-manifest-pending");

        var roles = new string[ship.CrewRoleManifest.Count];
        for (var i = 0; i < ship.CrewRoleManifest.Count; i++)
        {
            roles[i] = ReadableRole(ship.CrewRoleManifest[i]);
        }

        return string.Join(", ", roles);
    }

    private static string BuildReadableStationPlan(LuaMAiLogisticsShipComponent ship)
    {
        if (ship.CrewStationPlan.Count == 0)
            return "pending stations";

        return string.Join("; ", ship.CrewStationPlan.Take(6));
    }

    private static string Readable(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private string ReadableShipRole(string role)
    {
        return role.Trim().ToLowerInvariant() switch
        {
            "builder" or "repair" or "engineer" => Loc.GetString("luam-ai-ship-role-builder"),
            "miner" or "prospector" => Loc.GetString("luam-ai-ship-role-miner"),
            "scout" => Loc.GetString("luam-ai-ship-role-scout"),
            "guard" or "security" => Loc.GetString("luam-ai-ship-role-guard"),
            "trader" => Loc.GetString("luam-ai-ship-role-trader"),
            "medic" or "medical" => Loc.GetString("luam-ai-ship-role-medic"),
            "service" => Loc.GetString("luam-ai-ship-role-service"),
            _ => Loc.GetString("luam-ai-ship-role-hauler"),
        };
    }

    private string ReadableRole(string role)
    {
        return role.Trim().ToLowerInvariant() switch
        {
            "guard" => Loc.GetString("luam-ai-drone-role-guard"),
            "scout" => Loc.GetString("luam-ai-drone-role-scout"),
            "logistics" => Loc.GetString("luam-ai-drone-role-logistics"),
            "repair" or "builder" or "engineer" => Loc.GetString("luam-ai-drone-role-repair"),
            "medic" or "medical" => Loc.GetString("luam-ai-drone-role-medic"),
            "service" or "janitor" => Loc.GetString("luam-ai-drone-role-service"),
            _ => Loc.GetString("luam-ai-drone-role-miner"),
        };
    }

    private string ReadableZone(string zone)
    {
        return zone.Trim().ToLowerInvariant() switch
        {
            LuaMAiBaseEcologySystem.ZoneDock => Loc.GetString("luam-ai-base-zone-dock"),
            LuaMAiBaseEcologySystem.ZoneStorage => Loc.GetString("luam-ai-base-zone-storage"),
            LuaMAiBaseEcologySystem.ZoneMining => Loc.GetString("luam-ai-base-zone-mining"),
            LuaMAiBaseEcologySystem.ZonePatrol => Loc.GetString("luam-ai-base-zone-patrol"),
            LuaMAiBaseEcologySystem.ZoneContact => Loc.GetString("luam-ai-base-zone-contact"),
            _ => Readable(zone, "unknown zone"),
        };
    }

    private string ZonePurpose(string zone)
    {
        return zone.Trim().ToLowerInvariant() switch
        {
            LuaMAiBaseEcologySystem.ZoneDock => Loc.GetString("luam-ai-base-zone-purpose-dock"),
            LuaMAiBaseEcologySystem.ZoneStorage => Loc.GetString("luam-ai-base-zone-purpose-storage"),
            LuaMAiBaseEcologySystem.ZoneMining => Loc.GetString("luam-ai-base-zone-purpose-mining"),
            LuaMAiBaseEcologySystem.ZonePatrol => Loc.GetString("luam-ai-base-zone-purpose-patrol"),
            LuaMAiBaseEcologySystem.ZoneContact => Loc.GetString("luam-ai-base-zone-purpose-contact"),
            _ => Loc.GetString("luam-ai-base-zone-purpose-unknown"),
        };
    }

    private string ReadableTask(string task)
    {
        return task.Trim().ToLowerInvariant() switch
        {
            "dock_wait" => Loc.GetString("luam-ai-drone-task-dock-wait"),
            "mine_route" => Loc.GetString("luam-ai-drone-task-mine-route"),
            "patrol_ring" => Loc.GetString("luam-ai-drone-task-patrol-ring"),
            "route_scan" => Loc.GetString("luam-ai-drone-task-route-scan"),
            "supply_run" => Loc.GetString("luam-ai-drone-task-supply-run"),
            "repair_watch" => Loc.GetString("luam-ai-drone-task-repair-watch"),
            "medical_watch" => Loc.GetString("luam-ai-drone-task-medical-watch"),
            "service_watch" => Loc.GetString("luam-ai-drone-task-service-watch"),
            _ => Readable(task, "unclassified task"),
        };
    }

    private string ReadableStage(string stage)
    {
        return stage.Trim().ToLowerInvariant() switch
        {
            "assigned" => Loc.GetString("luam-ai-drone-stage-assigned"),
            "moving" => Loc.GetString("luam-ai-drone-stage-moving"),
            "working" => Loc.GetString("luam-ai-drone-stage-working"),
            "reporting" => Loc.GetString("luam-ai-drone-stage-reporting"),
            _ => Readable(stage, "unknown stage"),
        };
    }

    private string ReadableState(string state)
    {
        return string.IsNullOrWhiteSpace(state)
            ? Loc.GetString("luam-ai-drone-state-unknown")
            : state.Trim().Replace('_', ' ');
    }

    private string ReadableTone(string tone)
    {
        return tone.Trim().ToLowerInvariant() switch
        {
            "first_contact" => Loc.GetString("luam-ai-drone-tone-first-contact"),
            "repeat_contact" => Loc.GetString("luam-ai-drone-tone-repeat-contact"),
            "close_contact" => Loc.GetString("luam-ai-drone-tone-close-contact"),
            "" => Loc.GetString("luam-ai-drone-tone-unknown"),
            _ => tone.Trim().Replace('_', ' '),
        };
    }

    private static string ReadableEffect(string effect, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(effect) ? fallback : effect;
        return string.IsNullOrWhiteSpace(value)
            ? "crew duty"
            : value.Trim().Replace('_', ' ');
    }
}
