# General stuff
bounty-contracts-author = {$name} ({$job})
bounty-contracts-author-no-job = {$name}
bounty-contracts-unknown-author-name = Unknown
bounty-contracts-unknown-author-job = Unknown

# Categories
bounty-contracts-category-criminal = Wanted Criminal
bounty-contracts-category-vacancy = Job Vacancy
bounty-contracts-category-construction = Construction
bounty-contracts-category-service = Service
bounty-contracts-category-other = Other

# Cartridge
bounty-contracts-program-name = Bounty Contracts

## PDA/Radio Announcements
bounty-contracts-announcement-radio-name = Bounty Contracts Service
bounty-contracts-announcement-pda-name = Bounty placed

bounty-contracts-announcement-generic-create = New contract placed for {$target}. Reward: {$reward}.
bounty-contracts-announcement-criminal-create = New criminal bounty placed on {$target}. Reward: {$reward}.
bounty-contracts-announcement-vacancy-create = New job vacancy posted for {$target}. Reward: {$reward}.
bounty-contracts-announcement-construction-create = New construction contract placed for {$target}. Reward: {$reward}.
bounty-contracts-announcement-service-create = New service contract placed for {$target}. Reward: {$reward}.

## Contract collection names
bounty-contract-collection-name-command = Command
bounty-contract-collection-name-public = Public
bounty-contract-collection-name-distress = Distress Beacons

## UI - List contracts
bounty-contracts-ui-list-no-contracts = No contracts posted yet...
bounty-contracts-ui-list-no-description = No additional description provided...
bounty-contracts-ui-list-create = New Contract
bounty-contracts-ui-list-refresh = Refresh
bounty-contracts-ui-list-category = Category: {$category}
bounty-contracts-ui-list-vessel = Vessel: {$vessel}
bounty-contracts-ui-list-author = Posted by: {$author}
bounty-contracts-ui-list-unknown-author = Unknown
bounty-contracts-ui-list-remove = Delete
bounty-contracts-ui-list-accept = Take order
bounty-contracts-ui-list-release = Release
bounty-contracts-ui-list-accepted = Taken
bounty-contracts-ui-list-accept-tooltip = Mark this contract as accepted by your PDA.
bounty-contracts-ui-list-release-tooltip = Release this contract so another contractor can take it.
bounty-contracts-ui-list-accepted-tooltip = This contract is already accepted by {$name}.
bounty-contracts-ui-list-remove-tooltip = Delete this contract from the board.
bounty-contracts-ui-list-remove-disabled-tooltip = Only the author or authorized crew can delete this contract.
bounty-contracts-ui-list-accepted-by = Taken by: {$name}
bounty-contracts-ui-list-unaccepted = Open for work
bounty-contracts-ui-list-loading = Loading...
bounty-contracts-ui-list-route-in-description = Destination: coordinates, marker, or route are in the description.
bounty-contracts-ui-list-route-vessel = Destination: { $vessel }. If no marker is visible, check the sector map, GPS/beacons, and LuaM terminal.
bounty-contracts-ui-list-route-generic = Destination: sector map, GPS/beacons, and LuaM terminal. The exact point may appear as a beacon or process marker.
bounty-contracts-route-source-pda = PDA contract board
bounty-contracts-route-source-generated = LuaM sector memory
bounty-contracts-route-context-vessel = Navigation: fly to target/marker "{ $vessel }"; if no marker is visible, check the sector map, GPS/beacons, and LuaM terminal.
bounty-contracts-route-context-generic = Navigation: point is not tied to a shuttle; check the sector map, GPS/beacons, LuaM terminal, and latest dispatcher announcements.
bounty-contracts-route-context-source = Generation source: { $source }.
bounty-contracts-accepted-turn-in-hint = Turn-in: at a LuaM marker, click the marker and choose "Submit / close task". If the contract asks for an item, deliver it through the normal console or receiver named in the description.
bounty-contracts-accepted-message-header = Contract accepted: { $name }. Reward: { $reward }.
bounty-contracts-accepted-message-vessel = Target/vessel: { $vessel }.
bounty-contracts-accepted-message-empty-description = Description is empty. Where to search: check the sector map, active GPS/beacons, LuaM terminal, and dispatcher messages.
bounty-contracts-accepted-message-description = Route/description: { $description }
bounty-contracts-accepted-message-search-fallback = Where to search: check the sector map, active GPS/beacons, LuaM terminal, and dispatcher messages.
bounty-contracts-pinpointer-no-vessel = Route pinpointer not issued: the contract has no target/station. Use the description, sector map, GPS/beacons, and dispatcher messages.
bounty-contracts-pinpointer-target-not-found = Route pinpointer not issued: target/vessel "{ $vessel }" was not found in the sector. Use the contract description and sector map.
bounty-contracts-pinpointer-invalid-prototype = Route pinpointer not issued: the device prototype has no Pinpointer component.
bounty-contracts-pinpointer-given = Route pinpointer issued into your hands. Target marker: { $target }.
bounty-contracts-pinpointer-created-nearby = Route pinpointer created nearby. Target marker: { $target }; hands unavailable.

## UI - Create contract
bounty-contracts-ui-create-category = Category:{" "}
bounty-contracts-ui-create-name = Name:{" "}
bounty-contracts-ui-create-custom = Custom
bounty-contracts-ui-create-name-placeholder = Bounty name...
bounty-contracts-ui-create-dna = DNA:{" "}
bounty-contracts-ui-create-vessel = Vessel:{" "}
bounty-contracts-ui-create-vessel-unknown  = Unknown
bounty-contracts-ui-create-vessel-placeholder = Vessel name...
bounty-contracts-ui-create-reward = Reward:{" "}
bounty-contracts-ui-create-reward-currency = $
bounty-contracts-ui-create-description = Description:
bounty-contracts-ui-create-description-placeholder = Additional details...
bounty-contracts-ui-create-button-cancel = Cancel
bounty-contracts-ui-create-button-create = Create
bounty-contracts-ui-create-error-invalid-price = Error: reward must be from 30,000 to 100,000!
bounty-contracts-ui-create-error-no-name = Error: Invalid bounty name!
bounty-contracts-ui-create-error-name-too-long = Error: Name too long!
bounty-contracts-ui-create-error-vessel-too-long = Error: Vessel too long!
bounty-contracts-ui-create-error-description-too-long = Error: Description too long!
bounty-contracts-ui-create-ready = Your contract is ready to be published!


