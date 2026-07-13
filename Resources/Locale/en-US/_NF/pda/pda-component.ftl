comp-pda-ui-balance = Balance: [color=white]{ $balance }[/color]
comp-pda-ui-balance-payroll =
    Balance: [color=white]{ $balance }[/color]
    Payroll: [color=white]{ $hourly }/hour[/color], next payment in [color=white]{ $minutes } min[/color].
comp-pda-ui-shuttle-deed = Registered Ship: [color=white]{ $shipname }[/color]
comp-pda-ui-bank-id = Bank ID: [color=white]{ $id }[/color]
comp-pda-ui-bank-id-copied = [color=green]Bank ID { $id } copied.[/color]
comp-pda-ui-bank-transfer-recipient-placeholder = Recipient ID
comp-pda-ui-bank-transfer-amount-placeholder = Amount
comp-pda-ui-bank-transfer-send = Transfer
comp-pda-ui-bank-transfer-confirm = Confirm transfer
comp-pda-ui-bank-transfer-cancel = Cancel
comp-pda-ui-bank-transfer-acknowledge = Acknowledge result
comp-pda-ui-bank-transfer-confirmation = [color=yellow]Confirm transfer[/color]\nRecipient: [bold]{ $recipient }[/bold]\nBank ID: [bold]{ $id }[/bold]\nAmount: [bold]{ $amount }[/bold]\nOperation: { $operation }
comp-pda-ui-bank-transfer-recovered = [color=yellow]Recovered completed transfer[/color]\nRecipient: [bold]{ $recipient }[/bold] [{ $id }]\nAmount: [bold]{ $amount }[/bold]\nBalance after transfer: { $balance }\nOperation: { $operation }\nAcknowledge this durable result before creating another transfer.
comp-pda-ui-bank-transfer-confirmation-ready = Verify the recipient name, bank ID, and amount, then confirm the transfer.
comp-pda-ui-bank-transfer-confirmation-expired = [color=orange]The confirmation expired. Create a new transfer preview.[/color]
comp-pda-ui-bank-transfer-cancelled = Transfer cancelled before money was moved.
comp-pda-ui-bank-transfer-acknowledged = Durable transfer result acknowledged.
comp-pda-ui-bank-transfer-reconcile-required = [color=orange]Bank history is being reconciled. New transfers are disabled until it completes.[/color]
comp-pda-ui-bank-transfer-missing-recipient = [color=orange]Enter recipient bank ID.[/color]
comp-pda-ui-bank-transfer-invalid-amount = [color=orange]Enter a positive whole amount.[/color]
comp-pda-ui-bank-transfer-no-sender = [color=red]Transfer failed: PDA user not found.[/color]
comp-pda-ui-bank-transfer-recipient-not-found = [color=red]Transfer failed: bank ID { $id } was not found. Ask the recipient to open a PDA once so the ID is registered, then copy the ID again.[/color]
comp-pda-ui-bank-transfer-same-account = [color=orange]You cannot transfer money to your own account.[/color]
comp-pda-ui-bank-transfer-insufficient-funds = [color=red]Transfer failed: insufficient funds.[/color]
comp-pda-ui-bank-transfer-blocked = [color=red]Transfer blocked for this account.[/color]
comp-pda-ui-bank-transfer-recipient-overflow = [color=red]Transfer failed: the recipient account cannot accept this amount.[/color]
comp-pda-ui-bank-transfer-pending = [color=orange]A bank transfer is already being processed. Please wait.[/color]
comp-pda-ui-bank-transfer-conflict = [color=orange]The transfer could not be confirmed. Check your balance before trying again.[/color]
comp-pda-ui-bank-transfer-outcome-unknown = [color=red]The transfer result could not be verified. Repeating it is blocked for this server session; check your balance or contact an administrator.[/color]
comp-pda-ui-bank-transfer-internal-error = [color=red]Transfer status could not be confirmed. Check your balance before trying again.[/color]
comp-pda-ui-bank-transfer-failed = [color=red]Transfer failed ({ $reason }).[/color]
comp-pda-ui-bank-transfer-success = [color=green]Sent { $amount } to { $recipient } [{ $id }]. Balance: { $balance }. Operation: { $operation }.[/color]
comp-pda-ui-bank-transfer-received = Bank transfer received: { $amount } from { $sender }.
comp-pda-ui-donation-shop-title = LuaM support shop
comp-pda-ui-donation-shop-access = Support shop: [color=white]{ $balance } { $currency }[/color]. Access until: [color=white]{ $until }[/color].
comp-pda-ui-donation-shop-locked = Support shop locked. Balance: [color=white]{ $balance } { $currency }[/color]. Admin access required: 1 unit = 1 month.
comp-pda-ui-donation-shop-no-access-until = no active access
comp-pda-ui-donation-shop-copy = Donation balance: { $balance } { $currency }; access until: { $until }
comp-pda-ui-donation-shop-entry = [bold]{ $name }[/bold]: { $description } Price: { $price } { $currency }.
comp-pda-ui-donation-shop-buy = Buy
comp-pda-ui-donation-shop-owned = Owned
comp-pda-ui-donation-shop-insufficient = Need { $price } { $currency }
comp-pda-ui-donation-shop-locked-button = Locked
comp-pda-ui-donation-shop-status-no-user = [color=red]Donation shop user not found.[/color]
comp-pda-ui-donation-shop-status-unknown-item = [color=red]Donation item not found.[/color]
comp-pda-ui-donation-shop-status-locked = [color=orange]Donation shop access is inactive. Ask an admin to grant units; 1 unit = 1 month.[/color]
comp-pda-ui-donation-shop-status-owned = [color=orange]This reward is already owned.[/color]
comp-pda-ui-donation-shop-status-insufficient = [color=red]Not enough LC: { $balance }/{ $price } { $currency }.[/color]
comp-pda-ui-donation-shop-status-purchased = [color=green]Purchased { $item }. Balance: { $balance } { $currency }.[/color]
comp-pda-ui-donation-shop-certificate-nearby = Certificate printed nearby.
comp-pda-ui-donation-shop-certificate-content = LuaM sector support certificate. Player: { $player }. Account: { $user }. Reward: { $item }. Issued: { $date }. This certificate is cosmetic and grants no operational advantage.
comp-pda-ui-donation-shop-announcement = LuaM support signal registered for { $player }. Sector memory marks the contribution; no operational advantage assigned.
comp-pda-ui-donation-shop-item-supporter-badge = Supporter badge
comp-pda-ui-donation-shop-item-supporter-badge-desc = Permanent cosmetic support mark in LuaM records.
comp-pda-ui-donation-shop-item-pda-gold-frame = Gold PDA frame
comp-pda-ui-donation-shop-item-pda-gold-frame-desc = Cosmetic PDA-frame entitlement for later visual rollout.
comp-pda-ui-donation-shop-item-sector-certificate = Sector certificate
comp-pda-ui-donation-shop-item-sector-certificate-desc = Prints a paper support certificate into your hands.
comp-pda-ui-donation-shop-item-luam-announcement = LuaM announcement
comp-pda-ui-donation-shop-item-luam-announcement-desc = Sends a neutral sector support announcement with no gameplay advantage.
