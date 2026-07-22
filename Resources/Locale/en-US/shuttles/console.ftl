shuttle-pilot-start = Piloting ship
shuttle-pilot-end = Stopped piloting

shuttle-console-in-ftl = Currently in FTL
shuttle-console-mass = Too large to FTL
shuttle-console-prevent = Unable to pilot this ship
shuttle-console-in-expedition = FTL is unavailable during expeditions
shuttle-console-no-powered-ftl-drive = No FTL drive detected
shuttle-console-ftl-drive-unpowered = FTL drive detected but not powered

# NAV

shuttle-console-display-label = Display

shuttle-console-position = Position:
shuttle-console-position-value = {$X}, {$Y} m
shuttle-console-orientation = Orientation:
shuttle-console-orientation-value = {$angle}°
shuttle-console-linear-velocity = Linear Velocity:
shuttle-console-linear-velocity-value = {$X}, {$Y} m/s
shuttle-console-angular-velocity = Angular Velocity:
shuttle-console-angular-velocity-value = {$angularVelocity}°/s

shuttle-console-active-mode-navigation = Mode: Navigation — flight controls and telemetry.
shuttle-console-active-mode-map = Mode: Sector map — scan and set a destination.
shuttle-console-active-mode-docking = Mode: Docking — port and connection controls.

shuttle-console-unknown = Unknown
shuttle-console-iff-label = {$name} ({$distance}m)
shuttle-console-exclusion = Exclusion Area

# Buttons
shuttle-console-strafing = Strafing Mode
shuttle-console-nav-settings = Settings
shuttle-console-iff-toggle = Show IFF
shuttle-console-dock-toggle = Show Docks
shuttle-console-iffshuttles-toggle = Show Shuttles

# MAP

shuttle-console-ftl-label = FTL Status
shuttle-console-ftl-state-Available = ✓ Available
shuttle-console-ftl-state-Starting = … Starting
shuttle-console-ftl-state-Travelling = → Travelling
shuttle-console-ftl-state-Arriving = ⌖ Arriving
shuttle-console-ftl-state-Cooldown = ◷ Cooldown
shuttle-console-ftl-state-Invalid = ⚠ Invalid

shuttle-console-map-settings = Settings
shuttle-console-ftl-button = FTL
shuttle-console-map-rebuild = Scan
shuttle-console-map-beacons = Show Beacons

shuttle-console-no-signal = No Signal

shuttle-console-map-objects = Sector Objects

shuttle-console-targeting-idle =
    Targeting: inactive.
    Select FTL or Autopilot.
shuttle-console-targeting-ftl =
    Targeting: FTL destination.
    Click map; wheel adjusts heading.
shuttle-console-targeting-autopilot =
    Targeting: autopilot destination.
    Click map or enter X and Y.
shuttle-console-targeting-cancel = Cancel targeting
shuttle-console-coordinate-feedback-hint =
    Enter X and Y in metres.
    Decimals and negatives are accepted.
shuttle-console-coordinate-feedback-no-map =
    ⚠ Map signal unavailable.
    Scan or wait for navigation data.
shuttle-console-coordinate-feedback-invalid =
    ⚠ Enter finite numbers for X and Y,
    for example 120.5 and -40.
shuttle-console-coordinate-feedback-set =
    ✓ Autopilot target sent:
    {$X}, {$Y} m.

# DOCK
shuttle-console-docked = Docked Objects

shuttle-console-view = View
shuttle-console-undock = Undock
shuttle-console-undock-all = Undock All
shuttle-console-ftl-lock = FTL Synchronization
shuttle-console-ftl-lock-enabled = Synchronize
shuttle-console-ftl-lock-disabled = Do not sync
shuttle-console-dock = Dock
shuttle-console-docks-label = Docks

shuttle-console-undock-fail = Undocking Failed
shuttle-console-dock-fail = Docking Failed

shuttle-console-ftl-lock-status-unknown = ○ Sync status: unknown
shuttle-console-ftl-lock-status-enabled = ✓ Sync status: synchronized
shuttle-console-ftl-lock-status-disabled = × Sync status: not synchronized
shuttle-console-ftl-lock-status-mixed = ◐ Sync status: partially synchronized
shuttle-console-ftl-lock-hint =
    Synchronized docked shuttles follow
    this FTL jump without a second
    powered FTL drive.

shuttle-console-dock-ports-status-unavailable = ○ Docking system unavailable.
shuttle-console-dock-ports-status-empty = ○ No docking ports detected.
shuttle-console-dock-ports-status = Ports: {$total}; connected: {$connected}.
shuttle-console-dock-ports-empty =
    This shuttle has no controllable
    docking ports.
shuttle-console-dock-port-state-connected = ● Connected
shuttle-console-dock-port-state-free = ○ Free
shuttle-console-undock-all-confirmation =
    ⚠ Undock every connected object?
    All active ports release immediately.
shuttle-console-undock-all-confirm = Confirm undock
shuttle-console-action-cancel = Cancel
