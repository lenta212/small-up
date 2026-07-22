## UI
shipyard-console-invalid-vessel = Cannot purchase vessel:
shipyard-console-menu-title = Shipyard Menu
shipyard-console-menu-listing-free = Free
shipyard-console-menu-listing-voucher = Voucher
shipyard-console-docking = {$owner} shuttle {$vessel} en route.
shipyard-console-leaving = {$owner} shuttle {$vessel} sold by {$player}.
shipyard-console-docking-secret = Unregistered vessel detected entering your sector.
shipyard-console-leaving-secret = Unregistered vessel detected leaving your sector.
shipyard-commands-purchase-desc = Spawns and FTL docks a specified shuttle from a grid file.
shipyard-console-no-idcard = No ID card present.
shipyard-console-already-deeded = ID card already has a Deed.
shipyard-console-invalid-station = Not a valid station.
shipyard-console-no-bank = No bank account found.
shipyard-console-no-deed = No ship deed found.
shipyard-console-no-unassign = Card is restricted from unassigning deed.
shipyard-console-sale-reqs = Ship must be docked and all crew disembarked.
shipyard-console-sale-not-docked = Ship must be docked.
shipyard-console-sale-organic-aboard = All crew must disembark. {$name} is still aboard.
# This error message is bad, but if it happens, something awful's happened.
shipyard-console-sale-invalid-ship = Ship is invalid and cannot be sold.
shipyard-console-sale-unknown-reason = Ship cannot be sold: {reason}
shipyard-console-sale-bank-pending = Ship cannot be sold while another bank transaction is being processed. Please wait.
shipyard-console-sale-bank-failed = The sale payout could not be committed. The ship was not sold; please try again.
shipyard-console-sale-changed = The ship, deed, or appraisal changed while the payout was being processed. The payout was reversed and the ship was not sold.
shipyard-console-sale-recovery-required = The sale outcome requires administrator review. Further sale attempts are locked.
shipyard-console-purchase-bank-pending = A shuttle purchase or bank operation is already being processed. Please wait.
shipyard-console-purchase-bank-failed = The purchase debit could not be committed. No shuttle was issued; check your balance and try again.
shipyard-console-purchase-changed = The console, ID, voucher, station, or vessel changed while payment was being processed. The debit was reversed.
shipyard-console-purchase-creation-failed = The shuttle could not be prepared. The debit was reversed and no shuttle was issued.
shipyard-console-purchase-recovery-required = The purchase outcome requires administrator review. Further purchases are locked for this server process.
shipyard-console-deed-label = Registered Ship:
shipyard-console-appraisal-label = Shuttle Resale Value (Taxed):{" "}
shipyard-console-no-voucher-redemptions = All voucher redemptions have been used.
shipyard-console-invalid-voucher-type = This voucher cannot be used at this console.
shipyard-console-denied = You cannot purchase this ship at this time.
shipyard-console-limited = There are too many active shuttles of this type, try again later!

shipyard-console-contraband-onboard = Smuggled contraband detected onboard.
shipyard-console-station-resources = Vital station resources detected onboard.
shipyard-console-dangerous-materials = Dangerous materials detected onboard.
shipyard-console-fallback-prevent-sale = YML-class bugs detected onboard. Please file a bug report when possible.

shipyard-console-menu-size-label = Size:{" "}
shipyard-console-menu-class-label = Class:{" "}
shipyard-console-menu-engine-label = Engine:{" "}

shipyard-console-purchase-available = Purchase
shipyard-console-sell-button = Sell ship
shipyard-console-armament-default = Unspecified
shipyard-console-guidebook = Manual
shipyard-console-registered-none = None
shipyard-console-rename-button = Rename
shipyard-console-rename-placeholder = Ship name
shipyard-console-unassign-deed = Unassign
shipyard-console-deed-unassigned = Deed unassigned from ID card successfully.
shipyard-console-confirm-unassign = Are you sure?
shipyard-console-unassign-cooldown = Wait {$minutes} minute(s) before unassigning another deed.
shipyard-console-stored-ship-label = Stored ship:
shipyard-console-gate-label = Gate:
shipyard-console-purchase-gate-label = Purchase gate:
shipyard-console-park-button = Store ship
shipyard-console-call-button = Call ship
shipyard-console-call-map-title = Select arrival gate
shipyard-console-call-map-ship = Calling: { $ship }
shipyard-console-call-map-selected-label = Selected gate:
shipyard-console-call-map-no-selection = None
shipyard-console-gate-available = {$gate} — available
shipyard-console-gate-unavailable = {$gate} — occupied
shipyard-console-foreign-deed = {$ship} (owned by another account)
shipyard-console-owner-denied = Only the persistent owner can manage this ship.
shipyard-console-no-stored-ship = The selected stored ship is no longer available.
shipyard-console-call-pending = This ship or ID card is already being processed. Please wait.
shipyard-console-call-failed = Ship call rejected: {$reason}
shipyard-console-call-success = Ship called to gate “{$gate}”.
shipyard-console-gate-invalid = The selected gate is occupied or unavailable.
shipyard-console-park-docked = The ship must be docked to this station's gates.
shipyard-console-park-failed = Ship storage rejected: {$reason}
shipyard-console-park-success = Ship stored successfully.

# Frontier shipyard operations terminal
shipyard-console-terminal-kicker = FRONTIER HULL EXCHANGE // HARD-VACUUM OPERATIONS
shipyard-console-terminal-title = SHIPYARD CONTROL
shipyard-console-terminal-name = { $shipyard } // SHIPYARD CONTROL
shipyard-console-tab-catalog = Catalog
shipyard-console-tab-owned = My ships
shipyard-console-frontier-footer = FRONTIER NOTICE: beyond this airlock, every hull is your responsibility.

shipyard-console-credential-processing = CREDENTIAL LOCKED // a ship operation is being committed
shipyard-console-credential-missing = NO CREDENTIAL // insert a personal ID card or ship voucher
shipyard-console-credential-denied = ACCESS DENIED // your credentials do not authorize this terminal
shipyard-console-credential-foreign = FOREIGN DEED // this ship belongs to another account
shipyard-console-credential-ready = CREDENTIAL VERIFIED // owner channel secure
shipyard-console-gate-summary = GATES { $available }/{ $total } READY

shipyard-console-procurement-ready-title = PROCUREMENT CHANNEL READY
shipyard-console-procurement-locked-title = PROCUREMENT CHANNEL LOCKED
shipyard-console-purchase-ready = Ready: the selected gate is clear and the hull can be ordered.
shipyard-console-purchase-blocked-processing = Locked: another deed or bank operation is still being committed. Wait for completion.
shipyard-console-purchase-blocked-no-id = Locked: insert a personal ID card or valid ship voucher.
shipyard-console-purchase-blocked-access = Locked: your access does not authorize purchases at this terminal.
shipyard-console-purchase-blocked-existing-deed = Locked: the inserted ID already carries a ship deed. Store or unassign that hull first.
shipyard-console-purchase-blocked-no-link = Locked: the terminal has no verified link to a station grid.
shipyard-console-purchase-blocked-no-gate = Locked: every deployment gate is occupied. Clear a gate before ordering a hull.
shipyard-console-purchase-blocked-selected-gate = Locked: the selected deployment gate is occupied. Select a gate marked [+].
shipyard-console-purchase-blocked-listing = Restricted hull: your credential or voucher does not authorize this listing.
shipyard-console-purchase-action-hint = Two-step confirmation. Funds are charged only after server validation.
shipyard-console-confirm-purchase-generic = Confirm purchase?
shipyard-console-confirm-purchase = Buy { $ship } now?

shipyard-console-deployment-gate-title = DEPLOYMENT VECTOR
shipyard-console-deployment-gate-hint = A purchased hull materializes at the selected gate. Occupied airlocks cannot be used.
shipyard-console-filter-reset = Reset filters
shipyard-console-result-count = { $count ->
    [one] { $count } hull found
   *[other] { $count } hulls found
}
shipyard-console-catalog-warning = Hulls are delivered as-is. Inspect the manual before launch.
shipyard-console-catalog-empty-title = NO HULLS MATCH THE FILTER
shipyard-console-catalog-empty-hint = Clear the search or reset the size, class, and power filters.
shipyard-console-vessel-meta = SIZE { $size }  //  CLASS { $classes }  //  POWER { $power }

shipyard-console-active-deed-title = ACTIVE DEED
shipyard-console-active-deed-hint = This ID card controls the named hull. Renaming and storage require persistent ownership.
shipyard-console-stored-fleet-title = STORED FLEET
shipyard-console-stored-fleet-hint = Stored hulls remain off-grid until called through a clear arrival gate.
shipyard-console-stored-fleet-empty = No callable hulls are stored under your account.
shipyard-console-status-processing-title = OPERATION IN FLIGHT
shipyard-console-status-processing-hint = Do not remove the ID card. The registry is committing the current operation.
shipyard-console-status-no-card-title = NO OWNER CHANNEL
shipyard-console-status-no-card-hint = Insert your personal ID card to manage active or stored ships.
shipyard-console-status-foreign-title = OWNERSHIP MISMATCH
shipyard-console-status-foreign-hint = The inserted deed is registered to another persistent account. Management controls are locked.
shipyard-console-status-stored-title = HULL STORED
shipyard-console-status-stored-hint = The deed points to an off-grid hull. It may be called when an arrival gate is clear.
shipyard-console-status-active-title = HULL ACTIVE IN SECTOR
shipyard-console-status-active-hint = Dock the ship at this station before storing or selling it.
shipyard-console-status-fleet-title = STORED HULLS AVAILABLE
shipyard-console-status-fleet-hint = { $count } stored hulls are registered to your account. Select one below.
shipyard-console-status-empty-title = NO REGISTERED HULLS
shipyard-console-status-empty-hint = Purchase a hull from the catalog or insert an ID card carrying an owned deed.

shipyard-console-park-action-hint = Store the docked persistent ship off-grid. Crew aboard will be evacuated first.
shipyard-console-park-disabled-hint = Storage requires your active persistent ship to be docked at this station.
shipyard-console-call-action-hint = Opens the station map. Choose a clear [+] arrival gate.
shipyard-console-call-disabled-no-id = Insert your personal ID card before calling a stored ship.
shipyard-console-call-disabled-no-ships = No callable ships are stored under your account.
shipyard-console-call-disabled-no-link = This terminal has no verified station-grid link.
shipyard-console-call-disabled-no-gate = Every arrival gate is occupied. Clear a gate before calling a ship.
shipyard-console-call-disabled-active-deed = Store or unassign the active deed on this ID card before calling another hull.

shipyard-console-danger-zone-title = IRREVERSIBLE OPERATIONS
shipyard-console-danger-zone-hint = Sale retires a hull; unassigning removes the deed from this ID. Verify your target before confirming.
shipyard-console-danger-zone-ship-hint = Target: { $ship }. Sale retires this hull; unassigning removes its deed from this ID.
shipyard-console-confirm-sell-generic = Sell this ship permanently?
shipyard-console-confirm-sell = Sell { $ship } permanently?
shipyard-console-confirm-unassign-generic = Remove this deed from ID?
shipyard-console-confirm-unassign-ship = Unassign { $ship } from ID?

shipyard-console-call-map-hint = Select a deployment airlock on the station silhouette. The registry will reject any gate that becomes occupied.
shipyard-console-gate-legend-available = [+] CLEAR
shipyard-console-gate-legend-occupied = [×] OCCUPIED
shipyard-console-gate-legend-selected = [◎] SELECTED

# Keep these in enum order for ease of validation.
shipyard-console-category-All = All
shipyard-console-category-Micro = Micro
shipyard-console-category-Small = Small
shipyard-console-category-Medium = Medium
shipyard-console-category-Large = Large

shipyard-console-class-All = All
shipyard-console-class-Expedition = Expedition
shipyard-console-class-Scrapyard = Scrapyard
shipyard-console-class-Salvage = Salvage
shipyard-console-class-Science = Science
shipyard-console-class-Cargo = Cargo
shipyard-console-class-Chemistry = Chemistry
shipyard-console-class-Botany = Botany
shipyard-console-class-Engineering = Engineering
shipyard-console-class-Atmospherics = Atmospherics
shipyard-console-class-Medical = Medical
shipyard-console-class-Civilian = Civilian
shipyard-console-class-Kitchen = Kitchen
# Antag
shipyard-console-class-Syndicate = Syndicate
shipyard-console-class-Pirate = PDV
# NFSD
shipyard-console-class-Capital = Capital
shipyard-console-class-Detainment = Detainment
shipyard-console-class-Detective = Detective
shipyard-console-class-Fighter = Fighter
shipyard-console-class-Patrol = Patrol
shipyard-console-class-Pursuit = Pursuit
# Mono changes start
shipyard-console-class-Corvette = Corvette
shipyard-console-class-Frigate = Frigate
shipyard-console-class-Destroyer = Destroyer
shipyard-console-class-Cruiser = Cruiser
# Mono changes end

shipyard-console-engine-All = All
shipyard-console-engine-AME = AME
shipyard-console-engine-TEG = TEG
shipyard-console-engine-Supermatter = Supermatter
shipyard-console-engine-Tesla = Tesla
shipyard-console-engine-Singularity = Singularity
shipyard-console-engine-Solar = Solar
shipyard-console-engine-RTG = RTG
shipyard-console-engine-APU = APU
shipyard-console-engine-Welding = Welding Fuel
shipyard-console-engine-Plasma = Plasma
shipyard-console-engine-Uranium = Uranium
shipyard-console-engine-Bananium = Bananium
# Mono start
shipyard-console-engine-NFR = NFR
# Mono end
