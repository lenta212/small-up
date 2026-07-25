using Content.Shared.Actions;

namespace Content.Shared._DV.Harpy
{
    public partial class HarpySingerSystem : EntitySystem
    {
        [Dependency] private SharedActionsSystem _actionsSystem = default!;

        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<HarpySingerComponent, ComponentStartup>(OnStartup);
            SubscribeLocalEvent<HarpySingerComponent, MapInitEvent>(OnMapInit);
            SubscribeLocalEvent<HarpySingerComponent, ComponentShutdown>(OnShutdown);
        }

        private void OnStartup(EntityUid uid, HarpySingerComponent component, ComponentStartup args)
        {
            // Serialized action entities are not necessarily in their container
            // while MapLoader is still materializing the graph.
            if (MetaData(uid).EntityLifeStage < EntityLifeStage.MapInitialized)
                return;

            _actionsSystem.AddAction(uid, ref component.MidiAction, component.MidiActionId);
        }

        private void OnMapInit(EntityUid uid, HarpySingerComponent component, MapInitEvent args)
        {
            _actionsSystem.AddAction(uid, ref component.MidiAction, component.MidiActionId);
        }

        private void OnShutdown(EntityUid uid, HarpySingerComponent component, ComponentShutdown args)
        {
            _actionsSystem.RemoveAction(uid, component.MidiAction);
        }
    }
}
