using System.Numerics;
using System.Linq;
using Content.Client.Shuttles.UI;
using Content.Shared._NF.Shipyard.BUI;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Shared.Input;
using Robust.Shared.Map.Components;

namespace Content.Client._NF.Shipyard.UI;

public sealed class ShipyardDockMapControl : BaseShuttleControl
{
    private const float GateClickRadius = 18f;
    private const float GateMarkerRadius = 9f;

    private readonly List<ShipyardGateInfo> _gates = new();
    private NetEntity? _stationGrid;
    private NetEntity? _selectedGate;
    private EntityUid? _fittedGrid;

    public event Action<ShipyardGateInfo>? GateSelected;

    public ShipyardDockMapControl() : base(16f, 4096f, 128f)
    {
        Draggable = true;
    }

    public void SetState(
        NetEntity? stationGrid,
        IReadOnlyList<ShipyardGateInfo> gates,
        NetEntity? selectedGate)
    {
        if (_stationGrid != stationGrid)
            _fittedGrid = null;

        _stationGrid = stationGrid;
        _gates.Clear();
        _gates.AddRange(gates);
        _selectedGate = selectedGate is { } selected && _gates.Any(gate => gate.Entity == selected)
            ? selected
            : null;
    }

    public void SetSelectedGate(NetEntity? gate)
    {
        _selectedGate = gate;
    }

    protected override void KeyBindUp(GUIBoundKeyEventArgs args)
    {
        if (args.Function == EngineKeyFunctions.UIClick && TryFindGate(args.RelativePixelPosition, out var gate))
        {
            _selectedGate = gate.Entity;
            GateSelected?.Invoke(gate);
            args.Handle();
        }

        base.KeyBindUp(args);
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        DrawBacking(handle);

        if (_stationGrid is not { } stationGridNet)
        {
            DrawNoSignal(handle);
            return;
        }

        var stationGrid = EntManager.GetEntity(stationGridNet);
        if (!EntManager.TryGetComponent<MapGridComponent>(stationGrid, out var grid))
        {
            DrawNoSignal(handle);
            return;
        }

        FitGrid(stationGrid, grid);
        var localToView = Matrix3Helpers.CreateInverseTransform(Offset, Angle.Zero) *
                          Matrix3x2.CreateScale(MinimapScale, -MinimapScale) *
                          Matrix3x2.CreateTranslation(MidPointVector);

        DrawGrid(handle, localToView, (stationGrid, grid), Color.FromHex("#8A969C"), 0.06f);

        foreach (var gate in _gates)
            DrawGate(handle, gate, localToView);
    }

    private void FitGrid(EntityUid stationGrid, MapGridComponent grid)
    {
        if (_fittedGrid == stationGrid)
            return;

        var bounds = grid.LocalAABB;
        if (bounds.Width <= 0f || bounds.Height <= 0f)
            return;

        var desiredRange = MathF.Max(bounds.Width, bounds.Height) * 0.58f;
        desiredRange = Math.Clamp(desiredRange, WorldMinRange, WorldMaxRange);
        WorldRange = desiredRange;
        ActualRadarRange = desiredRange;
        Offset = bounds.Center;
        TargetOffset = bounds.Center;
        Recentering = false;
        _fittedGrid = stationGrid;
    }

    private void DrawGate(DrawingHandleScreen handle, ShipyardGateInfo gate, Matrix3x2 localToView)
    {
        var position = Vector2.Transform(gate.Position, localToView);
        var selected = _selectedGate == gate.Entity;
        var color = selected
            ? Color.FromHex("#57C7FF")
            : gate.Available
                ? Color.FromHex("#68C98B")
                : Color.FromHex("#C76B6B");
        var radius = (selected ? GateMarkerRadius + 2f : GateMarkerRadius) * UIScale;

        handle.DrawCircle(position, radius, color.WithAlpha(0.2f), true);
        handle.DrawCircle(position, radius, color, false);
        var symbolExtent = 4f * UIScale;
        if (gate.Available)
        {
            // Available gates use a plus-shaped beacon. Occupied gates use an X,
            // so status remains readable without relying on red/green alone.
            handle.DrawLine(
                position - new Vector2(symbolExtent, 0f),
                position + new Vector2(symbolExtent, 0f),
                color);
            handle.DrawLine(
                position - new Vector2(0f, symbolExtent),
                position + new Vector2(0f, symbolExtent),
                color);
        }
        else
        {
            handle.DrawLine(
                position - new Vector2(symbolExtent, symbolExtent),
                position + new Vector2(symbolExtent, symbolExtent),
                color);
            handle.DrawLine(
                position + new Vector2(-symbolExtent, symbolExtent),
                position + new Vector2(symbolExtent, -symbolExtent),
                color);
        }

        if (selected)
            handle.DrawCircle(position, radius + 4f * UIScale, color, false);

        var textSize = handle.GetDimensions(Font, gate.Name, UIScale * 0.8f);
        var requested = position + new Vector2(radius + 5f * UIScale, -textSize.Y / 2f);
        var max = new Vector2(
            MathF.Max(4f, PixelWidth - textSize.X - 4f),
            MathF.Max(4f, PixelHeight - textSize.Y - 4f));
        var labelPosition = Vector2.Clamp(requested, new Vector2(4f), max);
        handle.DrawString(Font, labelPosition + Vector2.One, gate.Name, UIScale * 0.8f, Color.Black.WithAlpha(0.8f));
        handle.DrawString(Font, labelPosition, gate.Name, UIScale * 0.8f, color);
    }

    private bool TryFindGate(Vector2 clickPosition, out ShipyardGateInfo gate)
    {
        gate = default!;
        if (_stationGrid is not { } stationGridNet)
            return false;

        var stationGrid = EntManager.GetEntity(stationGridNet);
        if (!EntManager.TryGetComponent<MapGridComponent>(stationGrid, out _))
            return false;

        var localToView = Matrix3Helpers.CreateInverseTransform(Offset, Angle.Zero) *
                          Matrix3x2.CreateScale(MinimapScale, -MinimapScale) *
                          Matrix3x2.CreateTranslation(MidPointVector);
        var clickRadiusSquared = MathF.Pow(GateClickRadius * UIScale, 2f);
        var bestDistanceSquared = float.MaxValue;

        foreach (var candidate in _gates)
        {
            var markerPosition = Vector2.Transform(candidate.Position, localToView);
            var distanceSquared = Vector2.DistanceSquared(markerPosition, clickPosition);
            if (distanceSquared > clickRadiusSquared || distanceSquared >= bestDistanceSquared)
                continue;

            gate = candidate;
            bestDistanceSquared = distanceSquared;
        }

        return bestDistanceSquared < float.MaxValue;
    }
}
