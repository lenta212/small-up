using System;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Administration;
using Content.Server.Database;
using Content.Shared.Administration;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;

namespace Content.Server._LuaM.Donation;

[AdminCommand(AdminFlags.Admin)]
public sealed class LuaMDonationShopCommand : IConsoleCommand
{
    [Dependency] private IEntitySystemManager _systems = default!;
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IServerDbManager _db = default!;

    public string Command => "donateshop";

    public string Description => "Manual LuaM donation shop access and balance control.";

    public string Help =>
        "donateshop grant <username|userId> <units> [reason]  (1 unit = 1 month)\n" +
        "donateshop balance <username|userId> <amount> [reason]\n" +
        "donateshop setbalance <username|userId> <amount> [reason]\n" +
        "donateshop access <username|userId> <true|false> [reason]\n" +
        "donateshop info <username|userId>";

    public async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 2)
        {
            shell.WriteError("Wrong number of arguments.\nUsage:\n" + Help);
            return;
        }

        var action = args[0].ToLowerInvariant();
        var target = args[1];
        var shop = _systems.GetEntitySystem<LuaMDonationShopSystem>();

        var resolved = await TryResolveTarget(shell, target);
        if (!resolved.Success)
            return;
        var userId = resolved.UserId;
        var userName = resolved.UserName;

        var actor = shell.Player?.Name ?? "server-console";
        var reason = args.Length > 3
            ? string.Join(' ', args.Skip(3))
            : "manual";

        switch (action)
        {
            case "grant":
            {
                if (args.Length < 3 || !int.TryParse(args[2], out var units) || units <= 0)
                {
                    shell.WriteError("Usage: donateshop grant <username|userId> <units> [reason]. 1 unit = 1 month.");
                    return;
                }

                var record = shop.GrantAccessUnits(userId, userName, units, actor, reason);
                shell.WriteLine($"Granted {units} donation access unit(s) to {userName} ({userId}). Unit: 1 month. Access until: {record.AccessUntil:yyyy-MM-dd HH:mm 'UTC'}; balance: {record.Balance} {LuaMDonationShopSystem.CurrencyCode}.");
                return;
            }
            case "balance":
            {
                if (args.Length < 3 || !int.TryParse(args[2], out var amount))
                {
                    shell.WriteError("Usage: donateshop balance <username|userId> <amount> [reason]");
                    return;
                }

                var record = shop.GrantBalance(userId, userName, amount, actor, reason);
                shell.WriteLine($"Changed {userName} ({userId}) donation balance by {amount} {LuaMDonationShopSystem.CurrencyCode}. Balance: {record.Balance}; access until: {record.AccessUntil:yyyy-MM-dd HH:mm 'UTC'}.");
                return;
            }
            case "setbalance":
            {
                if (args.Length < 3 || !int.TryParse(args[2], out var amount) || amount < 0)
                {
                    shell.WriteError("Usage: donateshop setbalance <username|userId> <amount>=0+ [reason]");
                    return;
                }

                var record = shop.SetBalance(userId, userName, amount, actor, reason);
                shell.WriteLine($"Set {userName} ({userId}) donation balance to {record.Balance} {LuaMDonationShopSystem.CurrencyCode}; access until: {record.AccessUntil:yyyy-MM-dd HH:mm 'UTC'}.");
                return;
            }
            case "access":
            {
                if (args.Length < 3 || !bool.TryParse(args[2], out var access))
                {
                    shell.WriteError("Usage: donateshop access <username|userId> <true|false> [reason]");
                    return;
                }

                var record = shop.SetAccess(userId, userName, access, actor, reason);
                shell.WriteLine($"Set {userName} ({userId}) donation shop access to {record.Access}. Access until: {record.AccessUntil:yyyy-MM-dd HH:mm 'UTC'}; balance: {record.Balance} {LuaMDonationShopSystem.CurrencyCode}.");
                return;
            }
            case "info":
            {
                if (!shop.TryGetRecord(userId, out var record))
                {
                    shell.WriteLine($"{userName} ({userId}) has no donation shop record.");
                    return;
                }

                var purchases = record.Purchases.Count == 0
                    ? "none"
                    : string.Join(", ", record.Purchases.Select(entry => $"{entry.Key} x{entry.Value}"));
                shell.WriteLine($"{record.LastUserName} ({record.UserId}) access={record.Access}; accessUntil={record.AccessUntil:yyyy-MM-dd HH:mm 'UTC'}; balance={record.Balance} {LuaMDonationShopSystem.CurrencyCode}; purchases={purchases}; ledger={record.Ledger.Count} entries.");
                return;
            }
            default:
                shell.WriteError("Unknown action.\nUsage:\n" + Help);
                return;
        }
    }

    private async Task<(bool Success, NetUserId UserId, string UserName)> TryResolveTarget(
        IConsoleShell shell,
        string target)
    {
        foreach (var session in _players.Sessions)
        {
            if (session.Name.Equals(target, StringComparison.OrdinalIgnoreCase) ||
                session.AttachedEntity is { Valid: true } attached &&
                _entities.EntityExists(attached) &&
                _entities.GetComponent<MetaDataComponent>(attached).EntityName.Equals(target, StringComparison.OrdinalIgnoreCase))
            {
                return (true, session.UserId, session.Name);
            }
        }

        if (Guid.TryParse(target, out var guid))
        {
            var userId = new NetUserId(guid);
            var userName = target;
            if (_players.TryGetSessionById(userId, out var session))
                userName = session.Name;
            return (true, userId, userName);
        }

        var record = await _db.GetPlayerRecordByUserName(target);
        if (record != null)
        {
            return (true, record.UserId, target);
        }

        shell.WriteError($"Unable to find player '{target}'. Use online username, character name, account username, or UUID.");
        return (false, default, string.Empty);
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
            return CompletionResult.FromHintOptions(["grant", "balance", "setbalance", "access", "info"], "action");

        if (args.Length == 2)
            return CompletionResult.FromHintOptions(CompletionHelper.SessionNames(), "username or userId");

        if (args.Length == 3 && args[0].Equals("access", StringComparison.OrdinalIgnoreCase))
            return CompletionResult.FromHintOptions(["true", "false"], "access");

        if (args.Length == 3 && args[0].Equals("grant", StringComparison.OrdinalIgnoreCase))
            return CompletionResult.FromHint("units; 1 unit = 1 month");

        if (args.Length == 3 && (args[0].Equals("balance", StringComparison.OrdinalIgnoreCase) ||
                                args[0].Equals("setbalance", StringComparison.OrdinalIgnoreCase)))
            return CompletionResult.FromHint("balance amount");

        return CompletionResult.Empty;
    }
}
