using Content.Server.Popups;
using Content.Shared.Interaction.Events;
using Robust.Shared.Map;

namespace Content.Server._LuaM.Sector;

public sealed partial class LuaMDistressBeaconSystem : EntitySystem
{
    [Dependency] private LuaMSectorStorySystem _stories = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<LuaMDistressBeaconComponent, UseInHandEvent>(OnUseInHand);
    }

    private void OnUseInHand(EntityUid uid, LuaMDistressBeaconComponent component, UseInHandEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = true;

        if (component.Seeded)
        {
            _popup.PopupEntity("Этот аварийный маяк уже передал контракт сектора.", uid, args.User);
            return;
        }

        var actor = MetaData(args.User).EntityName;
        var location = FormatBeaconLocation(Transform(uid).Coordinates);
        var description = $"Координаты маяка: {location}. Где искать: отметьте эту точку на карте сектора или в GPS. {component.Description}";

        if (!_stories.TrySeedDistressStory(
                component.Title,
                component.Vessel,
                component.Reward,
                description,
                component.Hazard,
                component.ReputationTarget,
                component.ReputationDelta,
                actor,
                out var record,
                out var error))
        {
            _popup.PopupEntity(error, uid, args.User);
            return;
        }

        component.Seeded = true;
        _popup.PopupEntity($"Аварийный контракт передан: {record!.Title}", uid, args.User);
    }

    public string FormatBeaconLocation(EntityCoordinates coordinates)
    {
        var mapCoordinates = _transform.ToMapCoordinates(coordinates, logError: false);
        if (mapCoordinates == MapCoordinates.Nullspace)
            return "GPS недоступен";

        return FormattableString.Invariant(
            $"GPS карта {mapCoordinates.MapId} x {mapCoordinates.Position.X:0.0} y {mapCoordinates.Position.Y:0.0}");
    }
}
