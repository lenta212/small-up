shuttle-console-designation = Designation:
shuttle-console-designation-unknown = Unknown
shuttle-console-maximum-iff-distance = Maximum IFF Distance (m)
shuttle-console-maximum-speed = Maximum Shuttle Speed (m/s)
shuttle-console-maximum-speed-placeholder = No limit
shuttle-console-maximum-speed-unlimited = ○ Speed limit disabled.
shuttle-console-maximum-speed-invalid = ⚠ Enter a finite non-negative speed in m/s.
shuttle-console-maximum-speed-valid = ✓ Speed limit: {$speed} m/s.

shuttle-console-target = Radar target
shuttle-console-set-target = Set target
shuttle-console-set-target-description = Sets a shared radar target at the entered sector coordinates.
shuttle-console-hide-target = Hide
shuttle-console-hide-target-description = Hides or shows the shared target on this shuttle's radar displays.
shuttle-console-target-name = Target
shuttle-console-target-feedback-empty = ○ No radar target selected.
shuttle-console-target-feedback-invalid = ⚠ Enter finite X and Y coordinates between -1000000 and 1000000.
shuttle-console-target-feedback-active = ⌖ Target: X {$x}, Y {$y}.
shuttle-console-map-track = ⌖
shuttle-console-map-track-tooltip = Set this shuttle as the shared radar target.

shuttle-console-iff-search = Search IFF
shuttle-console-shield-label = Shields
shuttle-console-inertia-dampener-off = Cruise
shuttle-console-inertia-dampener-dampen = Drive
shuttle-console-inertia-dampener-anchor = Park

# Mono
shuttle-console-force-anchored = You are not able to FTL an outpost.
shuttle-console-signature-infrared = Thermal Signature

# Mono - Unknowns
shuttle-console-signature-unknown =
    { $mass ->
        [small] Small Unknown
        [medium] Medium Unknown
        [large] Large Unknown
        [huge] Huge Unknown
        [supermassive] Supermassive Unknown
       *[other] Unknown
    }

# Network Port Buttons
shuttle-console-network-ports = Network Ports
shuttle-console-network-connect-tooltip = The buttons on the shuttle console send a signal when pressed, use a multitool on the console and connect it up to a device!

# Device Link Buttons
shuttle-console-device-button-1 = Port 1
shuttle-console-device-button-2 = Port 2
shuttle-console-device-button-3 = Port 3
shuttle-console-device-button-4 = Port 4
shuttle-console-device-button-5 = Port 5
shuttle-console-device-button-6 = Port 6
shuttle-console-device-button-7 = Port 7
shuttle-console-device-button-8 = Port 8

shuttle-console-inertia-dampener-status-unknown = ○ Selected mode: unknown
shuttle-console-inertia-dampener-status-off =
    ◇ Cruise — thrusters do not brake drift.
shuttle-console-inertia-dampener-status-dampen =
    → Drive — thrusters dampen drift.
shuttle-console-inertia-dampener-status-anchor =
    ■ Park — hold position when possible.
