using Content.Server._Mono.NPC.HTN.Operators;
using Content.Server.NPC.HTN;
using Content.Shared.Popups;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared._Mono.Shuttles;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;

namespace Content.Server._Mono.Shuttles;

public sealed partial class ShuttleConsoleAutopilotSystem : EntitySystem
{
    private const float MaxAutopilotCoordinate = 100000f;
    private const float MaxAutopilotDistance = 20000f;

    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private IMapManager _mapManager = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private ShuttleConsoleSystem _shuttleConsole = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ShuttleConsoleComponent, ShuttleConsoleAutopilotPositionMessage>(OnAutopilotMessage);
        SubscribeLocalEvent<ShuttleConsoleComponent, SteeringDoneEvent>(OnSteeringDone);
    }

    private void OnAutopilotMessage(Entity<ShuttleConsoleComponent> ent, ref ShuttleConsoleAutopilotPositionMessage args)
    {
        if (!TryComp<HTNComponent>(ent, out var htn))
            return;

        if (!ValidateTarget(ent, args.Coordinates))
        {
            _popup.PopupEntity(Loc.GetString("shuttle-console-autopilot-popup-invalid-target"), ent, args.Actor, PopupType.Medium);
            return;
        }

        var blackboard = htn.Blackboard;
        blackboard.SetValue(ent.Comp.AutopilotTargetKey, _transform.ToCoordinates(args.Coordinates));
        blackboard.SetValue(ent.Comp.AutopilotRotationKey, args.Angle + MathF.PI);
        RefreshConsole(ent);
    }

    private void OnSteeringDone(Entity<ShuttleConsoleComponent> ent, ref SteeringDoneEvent args)
    {
        _audio.PlayPvs(ent.Comp.AutopilotDoneSound, ent);
        _popup.PopupEntity(Loc.GetString("shuttle-console-autopilot-popup-done"), ent, PopupType.Medium);
        RefreshConsole(ent);
    }

    private bool ValidateTarget(EntityUid console, MapCoordinates target)
    {
        if (target.MapId == MapId.Nullspace ||
            !_mapManager.MapExists(target.MapId) ||
            !float.IsFinite(target.Position.X) ||
            !float.IsFinite(target.Position.Y) ||
            MathF.Abs(target.Position.X) > MaxAutopilotCoordinate ||
            MathF.Abs(target.Position.Y) > MaxAutopilotCoordinate)
        {
            return false;
        }

        if (!TryComp<TransformComponent>(console, out var consoleXform) ||
            consoleXform.GridUid is not { } grid ||
            !TryComp<TransformComponent>(grid, out var gridXform) ||
            gridXform.MapID != target.MapId)
        {
            return false;
        }

        var current = _transform.GetMapCoordinates(grid).Position;
        return (target.Position - current).LengthSquared() <= MaxAutopilotDistance * MaxAutopilotDistance;
    }

    private void RefreshConsole(EntityUid console)
    {
        if (TryComp<TransformComponent>(console, out var xform) && xform.GridUid is { } grid)
            _shuttleConsole.RefreshShuttleConsoles(grid);
    }
}
