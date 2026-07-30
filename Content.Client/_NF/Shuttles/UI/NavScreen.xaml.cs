// New Frontiers - This file is licensed under AGPLv3
// Copyright (c) 2024 New Frontiers Contributors
// See AGPLv3.txt for details.
using System.Numerics;
using Content.Shared._NF.Shuttles.Events;
using Content.Shared.Shuttles.BUIStates;
using Robust.Client.UserInterface.Controls;
using System;
using System.Globalization;

namespace Content.Client.Shuttles.UI
{
    public sealed partial class NavScreen
    {
        private readonly ButtonGroup _buttonGroup = new();
        public event Action<NetEntity?, InertiaDampeningMode>? OnInertiaDampeningModeChanged;
        public event Action<float?>? OnMaxShuttleSpeedChanged;
        public event Action<string, string>? OnNetworkPortButtonPressed;
        public event Action<Vector2>? OnSetRadarTarget;
        public event Action<bool>? OnSetRadarTargetVisibility;

        private const float MaxRadarTargetCoordinate = 1_000_000f;
        private bool _updatingRadarTargetControls;

        private void NfInitialize()
        {
            // Frontier - IFF search
            IffSearchCriteria.OnTextChanged += args => OnIffSearchChanged(args.Text);

            // Frontier - Maximum IFF Distance
            MaximumIFFDistanceValue.GetChild(0).GetChild(1).Margin = new Thickness(10, 0, 0, 0);
            MaximumIFFDistanceValue.OnValueChanged += args => OnRangeFilterChanged(args);

            // Frontier - Maximum Shuttle Speed
            MaximumShuttleSpeedValue.OnTextChanged += args => OnMaxSpeedChanged(args);

            DampenerOff.OnPressed += _ => SetDampenerMode(InertiaDampeningMode.Off);
            DampenerOn.OnPressed += _ => SetDampenerMode(InertiaDampeningMode.Dampen);
            AnchorOn.OnPressed += _ => SetDampenerMode(InertiaDampeningMode.Anchor);

            DampenerOff.Group = _buttonGroup;
            DampenerOn.Group = _buttonGroup;
            AnchorOn.Group = _buttonGroup;

            TargetSet.OnPressed += _ => SetRadarTarget();
            TargetX.OnTextEntered += _ => SetRadarTarget();
            TargetY.OnTextEntered += _ => SetRadarTarget();
            TargetX.OnTextChanged += _ => ResetRadarTargetFeedback();
            TargetY.OnTextChanged += _ => ResetRadarTargetFeedback();
            TargetHide.OnToggled += args =>
            {
                if (!_updatingRadarTargetControls)
                    OnSetRadarTargetVisibility?.Invoke(args.Pressed);
            };

            // Network Port Buttons
            DeviceButton1.OnPressed += _ => OnPortButtonPressed("device-button-1", "button-1");
            DeviceButton2.OnPressed += _ => OnPortButtonPressed("device-button-2", "button-2");
            DeviceButton3.OnPressed += _ => OnPortButtonPressed("device-button-3", "button-3");
            DeviceButton4.OnPressed += _ => OnPortButtonPressed("device-button-4", "button-4");
            DeviceButton5.OnPressed += _ => OnPortButtonPressed("device-button-5", "button-5");
            DeviceButton6.OnPressed += _ => OnPortButtonPressed("device-button-6", "button-6");
            DeviceButton7.OnPressed += _ => OnPortButtonPressed("device-button-7", "button-7");
            DeviceButton8.OnPressed += _ => OnPortButtonPressed("device-button-8", "button-8");

            // Send off a request to get the current dampening mode.
            _entManager.TryGetNetEntity(_shuttleEntity, out var shuttle);
            OnInertiaDampeningModeChanged?.Invoke(shuttle, InertiaDampeningMode.Query);
        }

        private void OnPortButtonPressed(string sourcePort, string targetPort)
        {
            OnNetworkPortButtonPressed?.Invoke(sourcePort, targetPort);
        }

        private void SetDampenerMode(InertiaDampeningMode mode)
        {
            NavRadar.DampeningMode = mode;
            _entManager.TryGetNetEntity(_shuttleEntity, out var shuttle);
            OnInertiaDampeningModeChanged?.Invoke(shuttle, mode);
        }

        private void NfUpdateState(NavInterfaceState state)
        {
            if (NavRadar.DampeningMode == InertiaDampeningMode.Station)
            {
                DampenerModeButtons.Visible = false;
                DampenerModeStatus.Visible = false;
            }
            else
            {
                DampenerModeButtons.Visible = true;
                DampenerModeStatus.Visible = true;
                DampenerOff.Pressed = NavRadar.DampeningMode == InertiaDampeningMode.Off;
                DampenerOn.Pressed = NavRadar.DampeningMode == InertiaDampeningMode.Dampen;
                AnchorOn.Pressed = NavRadar.DampeningMode == InertiaDampeningMode.Anchor;
                DampenerModeStatus.Text = Loc.GetString(NfGetDampenerStatusLocId(NavRadar.DampeningMode));

                // Disable the Park button (AnchorOn) while in FTL, but keep other dampener buttons enabled
                if (NavRadar.InFtl)
                {
                    AnchorOn.Disabled = true;
                    // If the AnchorOn button is pressed while it gets disabled, we need to switch to another mode
                    if (AnchorOn.Pressed)
                    {
                        DampenerOn.Pressed = true;
                        SetDampenerMode(InertiaDampeningMode.Dampen);
                    }
                }
                else
                {
                    AnchorOn.Disabled = false;
                }
            }

            UpdateRadarTargetState(state);
        }

        private void SetRadarTarget()
        {
            if (!TryParseRadarTarget(TargetX.Text, out var x) ||
                !TryParseRadarTarget(TargetY.Text, out var y))
            {
                TargetX.ModulateSelfOverride = TryParseRadarTarget(TargetX.Text, out _) ? null : Color.LightCoral;
                TargetY.ModulateSelfOverride = TryParseRadarTarget(TargetY.Text, out _) ? null : Color.LightCoral;
                TargetFeedback.Text = Loc.GetString("shuttle-console-target-feedback-invalid");
                TargetFeedback.FontColorOverride = Color.FromHex("#E6B86A");
                return;
            }

            TargetX.ModulateSelfOverride = null;
            TargetY.ModulateSelfOverride = null;
            TargetFeedback.Text = Loc.GetString(
                "shuttle-console-target-feedback-active",
                ("x", x.ToString("0.0", CultureInfo.InvariantCulture)),
                ("y", y.ToString("0.0", CultureInfo.InvariantCulture)));
            TargetFeedback.FontColorOverride = Color.FromHex("#A9E3C7");
            OnSetRadarTarget?.Invoke(new Vector2(x, y));
        }

        private void UpdateRadarTargetState(NavInterfaceState state)
        {
            _updatingRadarTargetControls = true;
            TargetHide.Disabled = state.Target == null;
            TargetHide.Pressed = state.HideTarget;
            _updatingRadarTargetControls = false;

            if (state.Target is not { } target)
            {
                TargetFeedback.Text = Loc.GetString("shuttle-console-target-feedback-empty");
                TargetFeedback.FontColorOverride = Color.FromHex("#829597");
                return;
            }

            if (!TargetX.HasKeyboardFocus())
                TargetX.Text = target.X.ToString("0.0", CultureInfo.InvariantCulture);
            if (!TargetY.HasKeyboardFocus())
                TargetY.Text = target.Y.ToString("0.0", CultureInfo.InvariantCulture);

            TargetX.ModulateSelfOverride = null;
            TargetY.ModulateSelfOverride = null;
            TargetFeedback.Text = Loc.GetString(
                "shuttle-console-target-feedback-active",
                ("x", target.X.ToString("0.0", CultureInfo.InvariantCulture)),
                ("y", target.Y.ToString("0.0", CultureInfo.InvariantCulture)));
            TargetFeedback.FontColorOverride = state.HideTarget
                ? Color.FromHex("#829597")
                : Color.FromHex("#A9E3C7");
        }

        private void ResetRadarTargetFeedback()
        {
            if (_updatingRadarTargetControls)
                return;

            TargetX.ModulateSelfOverride = null;
            TargetY.ModulateSelfOverride = null;
        }

        private static bool TryParseRadarTarget(string text, out float coordinate)
        {
            return float.TryParse(
                       text.Trim().Replace(',', '.'),
                       NumberStyles.Float,
                       CultureInfo.InvariantCulture,
                       out coordinate) &&
                   float.IsFinite(coordinate) &&
                   MathF.Abs(coordinate) <= MaxRadarTargetCoordinate;
        }


        private static string NfGetDampenerStatusLocId(InertiaDampeningMode mode)
        {
            return mode switch
            {
                InertiaDampeningMode.Off => "shuttle-console-inertia-dampener-status-off",
                InertiaDampeningMode.Dampen => "shuttle-console-inertia-dampener-status-dampen",
                InertiaDampeningMode.Anchor => "shuttle-console-inertia-dampener-status-anchor",
                _ => "shuttle-console-inertia-dampener-status-unknown",
            };
        }

        // Frontier - Maximum IFF Distance
        private void OnRangeFilterChanged(int value)
        {
            NavRadar.MaximumIFFDistance = (float) value;
        }

        // Frontier - Maximum Shuttle Speed
        private void OnMaxSpeedChanged(LineEdit.LineEditEventArgs value)
        {
            var text = value.Text.Trim();
            if (text.Length == 0)
            {
                MaximumShuttleSpeedValue.ModulateSelfOverride = null;
                MaximumShuttleSpeedFeedback.Text = Loc.GetString("shuttle-console-maximum-speed-unlimited");
                MaximumShuttleSpeedFeedback.FontColorOverride = Color.FromHex("#829597");
                OnMaxShuttleSpeedChanged?.Invoke(null);
                return;
            }

            if (!TryParseMaximumSpeed(text, out var speed))
            {
                MaximumShuttleSpeedValue.ModulateSelfOverride = Color.LightCoral;
                MaximumShuttleSpeedFeedback.Text = Loc.GetString("shuttle-console-maximum-speed-invalid");
                MaximumShuttleSpeedFeedback.FontColorOverride = Color.FromHex("#E6B86A");
                return;
            }

            MaximumShuttleSpeedValue.ModulateSelfOverride = null;
            MaximumShuttleSpeedFeedback.Text = Loc.GetString("shuttle-console-maximum-speed-valid",
                ("speed", speed.ToString("0.##", CultureInfo.InvariantCulture)));
            MaximumShuttleSpeedFeedback.FontColorOverride = Color.FromHex("#A9E3C7");
            OnMaxShuttleSpeedChanged?.Invoke(speed);
        }

        private static bool TryParseMaximumSpeed(string text, out float speed)
        {
            return float.TryParse(
                       text.Replace(',', '.'),
                       NumberStyles.Float,
                       CultureInfo.InvariantCulture,
                       out speed) &&
                   float.IsFinite(speed) &&
                   speed >= 0f;
        }

        private void NfAddShuttleDesignation(EntityUid? shuttle)
        {
            // Frontier - PR #1284 Add Shuttle Designation
            if (_entManager.TryGetComponent<MetaDataComponent>(shuttle, out var metadata))
            {
                var shipName = metadata.EntityName;

                // Try to find a designation in the format XXX-### (like CIV-748)
                // by checking each word in the ship name
                var shipNameParts = shipName.Split(' ');

                foreach (var part in shipNameParts)
                {
                    // Check if this part matches the designation format (e.g., CIV-748)
                    // The format is 2+ characters, followed by a dash, followed by more characters
                    if (part.Length > 3 && part.Contains('-'))
                    {
                        var dashIndex = part.IndexOf('-');
                        if (dashIndex >= 2 && dashIndex < part.Length - 1)
                        {
                            // This part looks like a designation
                            NavDisplayLabel.Text = shipName.Replace(part, "").Trim();
                            ShuttleDesignation.Text = part;
                            return;
                        }
                    }
                }

                // If we get here, no designation was found, so just show the full name
                NavDisplayLabel.Text = shipName;
                // Leave ShuttleDesignation.Text as "Unknown" (the default)
            }
            // End Frontier - PR #1284
        }

    }
}
