using System.Numerics;
using Content.Client.Movement.Systems;
using Content.Client.UserInterface.Systems.Ghost.Widgets;
using Content.Shared.Actions;
using Content.Shared.Ghost;
using Content.Shared.Mind;
using Content.Shared.Popups;
using Robust.Client.Console;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.GameStates;
using Robust.Shared.Timing;
using Robust.Shared.Player;
using Robust.Shared.Utility;
using Content.Client._Corvax.Respawn; // Frontier

namespace Content.Client.Ghost
{
    public sealed partial class GhostSystem : SharedGhostSystem
    {
        [Dependency] private IClientConsoleHost _console = default!;
        [Dependency] private IPlayerManager _playerManager = default!;
        [Dependency] private SharedActionsSystem _actions = default!;
        [Dependency] private PointLightSystem _pointLightSystem = default!;
        [Dependency] private ContentEyeSystem _contentEye = default!;
        [Dependency] private EyeSystem _eye = default!;
        [Dependency] private IUserInterfaceManager _uiManager = default!;
        [Dependency] private IGameTiming _gameTiming = default!;
        [Dependency] private RespawnSystem _respawn = default!;
        [Dependency] private SpriteSystem _spriteSystem = default!;

        private static readonly SpriteSpecifier.Rsi GuideCatSprite =
            new(new ResPath("_NF/Mobs/Pets/cat.rsi"), "cultcat");

        public override void Update(float frameTime)
        {
            foreach (var ghost in EntityManager.EntityQuery<GhostComponent, MindComponent>(true))
            {
                var ui = _uiManager.GetActiveUIWidgetOrNull<GhostGui>();
                if (ui != null && Player != null)
                    ui.UpdateRespawn(_respawn.RespawnResetTime);
            }
        }

        public int AvailableGhostRoleCount { get; private set; }

        private bool _ghostVisibility = true;
        private BoxContainer? _guideNotificationStack;

        private bool GhostVisibility
        {
            get => _ghostVisibility;
            set
            {
                if (_ghostVisibility == value)
                {
                    return;
                }

                _ghostVisibility = value;

                var query = AllEntityQuery<GhostComponent, SpriteComponent>();
                while (query.MoveNext(out var uid, out _, out var sprite))
                {
                    sprite.Visible = value || uid == _playerManager.LocalEntity;
                }
            }
        }

        public GhostComponent? Player => CompOrNull<GhostComponent>(_playerManager.LocalEntity);
        public bool IsGhost => Player != null;

        public event Action<GhostComponent>? PlayerRemoved;
        public event Action<GhostComponent>? PlayerUpdated;
        public event Action<GhostComponent>? PlayerAttached;
        public event Action? PlayerDetached;
        public event Action<GhostWarpsResponseEvent>? GhostWarpsResponse;
        public event Action<GhostUpdateGhostRoleCountEvent>? GhostRoleCountUpdated;

        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<GhostComponent, ComponentStartup>(OnStartup);
            SubscribeLocalEvent<GhostComponent, ComponentRemove>(OnGhostRemove);
            SubscribeLocalEvent<GhostComponent, AfterAutoHandleStateEvent>(OnGhostState);

            SubscribeLocalEvent<GhostComponent, LocalPlayerAttachedEvent>(OnGhostPlayerAttach);
            SubscribeLocalEvent<GhostComponent, LocalPlayerDetachedEvent>(OnGhostPlayerDetach);

            SubscribeNetworkEvent<GhostWarpsResponseEvent>(OnGhostWarpsResponse);
            SubscribeNetworkEvent<GhostUpdateGhostRoleCountEvent>(OnUpdateGhostRoleCount);

            SubscribeLocalEvent<EyeComponent, ToggleLightingActionEvent>(OnToggleLighting);
            SubscribeLocalEvent<EyeComponent, ToggleFoVActionEvent>(OnToggleFoV);
            SubscribeLocalEvent<GhostComponent, ToggleGhostsActionEvent>(OnToggleGhosts);
        }

        private void OnStartup(EntityUid uid, GhostComponent component, ComponentStartup args)
        {
            if (TryComp(uid, out SpriteComponent? sprite))
                sprite.Visible = GhostVisibility || uid == _playerManager.LocalEntity;
        }

        private void OnToggleLighting(EntityUid uid, EyeComponent component, ToggleLightingActionEvent args)
        {
            if (args.Handled)
                return;

            TryComp<PointLightComponent>(uid, out var light);

            if (!component.DrawLight)
            {
                // normal lighting
                Popup.PopupEntity(Loc.GetString("ghost-gui-toggle-lighting-manager-popup-normal"), args.Performer);
                _contentEye.RequestEye(component.DrawFov, true);
            }
            else if (!light?.Enabled ?? false) // skip this option if we have no PointLightComponent
            {
                // enable personal light
                Popup.PopupEntity(Loc.GetString("ghost-gui-toggle-lighting-manager-popup-personal-light"), args.Performer);
                _pointLightSystem.SetEnabled(uid, true, light);
            }
            else
            {
                // fullbright mode
                Popup.PopupEntity(Loc.GetString("ghost-gui-toggle-lighting-manager-popup-fullbright"), args.Performer);
                _contentEye.RequestEye(component.DrawFov, false);
                _pointLightSystem.SetEnabled(uid, false, light);
            }
            args.Handled = true;
        }

        private void OnToggleFoV(EntityUid uid, EyeComponent component, ToggleFoVActionEvent args)
        {
            if (args.Handled)
                return;

            Popup.PopupEntity(Loc.GetString("ghost-gui-toggle-fov-popup"), args.Performer);
            _contentEye.RequestToggleFov(uid, component);
            args.Handled = true;
        }

        private void OnToggleGhosts(EntityUid uid, GhostComponent component, ToggleGhostsActionEvent args)
        {
            if (args.Handled)
                return;

            var locId = GhostVisibility ? "ghost-gui-toggle-ghost-visibility-popup-off" : "ghost-gui-toggle-ghost-visibility-popup-on";
            Popup.PopupEntity(Loc.GetString(locId), args.Performer);
            if (uid == _playerManager.LocalEntity)
                ToggleGhostVisibility();

            args.Handled = true;
        }

        private void OnGhostRemove(EntityUid uid, GhostComponent component, ComponentRemove args)
        {
            _actions.RemoveAction(uid, component.ToggleLightingActionEntity);
            _actions.RemoveAction(uid, component.ToggleFoVActionEntity);
            _actions.RemoveAction(uid, component.ToggleGhostsActionEntity);
            _actions.RemoveAction(uid, component.ToggleGhostHearingActionEntity);

            if (uid != _playerManager.LocalEntity)
                return;

            GhostVisibility = false;
            PlayerRemoved?.Invoke(component);
        }

        private void OnGhostPlayerAttach(EntityUid uid, GhostComponent component, LocalPlayerAttachedEvent localPlayerAttachedEvent)
        {
            if (uid != _playerManager.LocalPlayer?.ControlledEntity)
                return;
            component.TimeOfDeath = _gameTiming.CurTime;
            GhostVisibility = true;
            ShowGuideCatNotification();
            PlayerAttached?.Invoke(component);
        }

        private void ShowGuideCatNotification()
        {
            if (_guideNotificationStack == null)
            {
                _guideNotificationStack = new BoxContainer
                {
                    Orientation = BoxContainer.LayoutOrientation.Vertical,
                    SeparationOverride = 8,
                    MouseFilter = Control.MouseFilterMode.Ignore,
                };

                _uiManager.PopupRoot.AddChild(_guideNotificationStack);
                LayoutContainer.SetAnchorAndMarginPreset(
                    _guideNotificationStack,
                    LayoutContainer.LayoutPreset.CenterRight,
                    margin: 18);
            }

            var background = new StyleBoxFlat
            {
                BackgroundColor = Color.FromHex("#11191EEA"),
                BorderColor = Color.FromHex("#497783"),
                BorderThickness = new Thickness(1),
            };
            background.SetContentMarginOverride(StyleBox.Margin.All, 10);

            var panel = new PanelContainer
            {
                MinSize = new Vector2(380, 88),
                MaxSize = new Vector2(380, 120),
                PanelOverride = background,
                MouseFilter = Control.MouseFilterMode.Ignore,
            };

            var content = new BoxContainer
            {
                Orientation = BoxContainer.LayoutOrientation.Horizontal,
                SeparationOverride = 10,
            };

            var catView = new TextureRect
            {
                SetSize = new Vector2(64, 64),
                Texture = _spriteSystem.Frame0(GuideCatSprite),
                Stretch = TextureRect.StretchMode.KeepAspectCentered,
                ModulateSelfOverride = Color.FromHex("#A85BB5")
            };
            content.AddChild(catView);

            var text = new BoxContainer
            {
                Orientation = BoxContainer.LayoutOrientation.Vertical,
                SeparationOverride = 4,
                HorizontalExpand = true,
            };
            text.AddChild(new Label
            {
                Text = Loc.GetString("ghost-guide-cat-title"),
                FontColorOverride = Color.FromHex("#8FD7E5"),
            });

            var hint = new RichTextLabel
            {
                MinSize = new Vector2(280, 48),
                HorizontalExpand = true,
            };
            hint.SetMessage(Loc.GetString("ghost-guide-cat-hint"));
            text.AddChild(hint);
            content.AddChild(text);
            panel.AddChild(content);
            _guideNotificationStack.AddChild(panel);

            Timer.Spawn(TimeSpan.FromSeconds(10), () => panel.Dispose());
        }

        private void OnGhostState(EntityUid uid, GhostComponent component, ref AfterAutoHandleStateEvent args)
        {
            if (TryComp<SpriteComponent>(uid, out var sprite))
                sprite.LayerSetColor(0, component.Color);

            if (uid != _playerManager.LocalEntity)
                return;

            PlayerUpdated?.Invoke(component);
        }

        private void OnGhostPlayerDetach(EntityUid uid, GhostComponent component, LocalPlayerDetachedEvent args)
        {
            GhostVisibility = false;
            PlayerDetached?.Invoke();
        }

        private void OnGhostWarpsResponse(GhostWarpsResponseEvent msg)
        {
            if (!IsGhost)
            {
                return;
            }

            GhostWarpsResponse?.Invoke(msg);
        }

        private void OnUpdateGhostRoleCount(GhostUpdateGhostRoleCountEvent msg)
        {
            AvailableGhostRoleCount = msg.AvailableGhostRoles;
            GhostRoleCountUpdated?.Invoke(msg);
        }

        public void RequestWarps()
        {
            RaiseNetworkEvent(new GhostWarpsRequestEvent());
        }

        public void ReturnToBody()
        {
            var msg = new GhostReturnToBodyRequest();
            RaiseNetworkEvent(msg);
        }

        public void OpenGhostRoles()
        {
            _console.RemoteExecuteCommand(null, "ghostroles");
        }

        public void ToggleGhostVisibility(bool? visibility = null)
        {
            GhostVisibility = visibility ?? !GhostVisibility;
        }
    }
}
