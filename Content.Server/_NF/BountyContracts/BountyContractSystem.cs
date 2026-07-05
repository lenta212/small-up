using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using Content.Server._NF.Access;
using Content.Server.Administration.Logs;
using Content.Server.CartridgeLoader;
using Content.Server.Chat.Managers;
using Content.Server.Chat.Systems;
using Content.Server.Pinpointer;
using Content.Server.StationRecords.Systems;
using Content.Shared._NF.Bank;
using Content.Shared._NF.BountyContracts;
using Content.Shared.Access.Systems;
using Content.Shared.CartridgeLoader;
using Content.Shared.Database;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.IdentityManagement;
using Content.Shared.Pinpointer;
using Content.Shared.Station.Components;
using Robust.Server.Player;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._NF.BountyContracts;

/// <summary>
///     Used to control all bounty contracts placed by players.
/// </summary>
public sealed partial class BountyContractSystem : SharedBountyContractSystem
{
    private const string ContractRoutePinpointerPrototype = "PinpointerUniversal";

    private ISawmill _sawmill = default!;

    [Dependency] private CartridgeLoaderSystem _cartridgeLoader = default!;
    [Dependency] private StationRecordsSystem _records = default!;
    [Dependency] private AccessReaderSystem _accessReader = default!;
    [Dependency] private NFAccessSystemUtilities _accessUtils = default!;
    [Dependency] private ChatSystem _chat = default!;
    [Dependency] private IChatManager _chatManager = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IPrototypeManager _proto = default!;
    [Dependency] private IAdminLogManager _adminLog = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private PinpointerSystem _pinpointer = default!;

    public override void Initialize()
    {
        base.Initialize();
        _sawmill = Logger.GetSawmill("bounty.contracts");

        SubscribeLocalEvent<BountyContractDataComponent, ComponentInit>(ContractInit);
        InitializeUi();
    }

    private void ContractInit(Entity<BountyContractDataComponent> ent, ref ComponentInit ev)
    {
        SortedList<int, ProtoId<BountyContractCollectionPrototype>> orderedCollections = new();
        Dictionary<ProtoId<BountyContractCollectionPrototype>, Dictionary<uint, BountyContract>> contracts = new();
        foreach (var proto in _proto.EnumeratePrototypes<BountyContractCollectionPrototype>())
        {
            contracts[proto.ID] = new();
            orderedCollections[proto.Order] = proto.ID;
        }
        ent.Comp.Contracts = contracts.ToFrozenDictionary();
        ent.Comp.OrderedCollections = orderedCollections.Values.ToList();
    }

    private BountyContractDataComponent? GetContracts()
    {
        TryComp(_sectorService.GetServiceEntity(), out BountyContractDataComponent? bountyContracts);
        return bountyContracts;
    }

    // Returns a list of all readable collections that a user can see.
    private List<ProtoId<BountyContractCollectionPrototype>> GetReadableCollections(EntityUid user, BountyContractDataComponent? bounties = null)
    {
        var returnList = new List<ProtoId<BountyContractCollectionPrototype>>();
        if (bounties == null)
        {
            bounties = GetContracts();
            // Nothing to read from, no read access
            if (bounties == null)
                return returnList;
        }

        if (bounties.Contracts == null)
            return returnList;

        var accessTags = _accessReader.FindAccessTags(user);
        foreach (var collection in bounties.OrderedCollections)
        {
            if (!_proto.TryIndex(collection, out var collectionProto))
                continue;

            if (_accessUtils.IsAllowed(accessTags, collectionProto.ReadAccess, collectionProto.ReadGroups))
                returnList.Add(collection);
        }
        return returnList;
    }

    private bool HasReadAccess(EntityUid user, ProtoId<BountyContractCollectionPrototype> collection, BountyContractDataComponent? bounties = null)
    {
        if (bounties == null)
        {
            bounties = GetContracts();
            // Nothing to read from, no read access
            if (bounties == null)
                return false;
        }

        if (!_proto.TryIndex(collection, out var collectionProto))
            return false;

        return _accessUtils.IsAllowed(_accessReader.FindAccessTags(user), collectionProto.ReadAccess, collectionProto.ReadGroups);
    }

    private bool HasWriteAccess(EntityUid user, ProtoId<BountyContractCollectionPrototype> collection, BountyContractDataComponent? bounties = null)
    {
        if (bounties == null)
        {
            bounties = GetContracts();
            // Nothing to write to, no write access
            if (bounties == null)
                return false;
        }

        if (!_proto.TryIndex(collection, out var collectionProto))
            return false;

        return _accessUtils.IsAllowed(_accessReader.FindAccessTags(user), collectionProto.WriteAccess, collectionProto.WriteGroups);
    }

    private bool HasDeleteAccess(EntityUid user, ProtoId<BountyContractCollectionPrototype> collection, BountyContractDataComponent? bounties = null)
    {
        if (bounties == null)
        {
            bounties = GetContracts();
            // Nothing to delete from, no write access
            if (bounties == null)
                return false;
        }

        if (!_proto.TryIndex(collection, out var collectionProto))
            return false;

        return _accessUtils.IsAllowed(_accessReader.FindAccessTags(user), collectionProto.DeleteAccess, collectionProto.DeleteGroups);
    }

    /// <summary>
    ///     Try to create a new bounty contract and put it in bounties list.
    /// </summary>
    /// <param name="collection">Bounty contract collection (command, public, etc.)</param>
    /// <param name="category">Bounty contract category (bounty head, construction, etc.)</param>
    /// <param name="name">IC name for the contract bounty head. Can be players IC name or custom string.</param>
    /// <param name="reward">Cash reward for completing bounty.</param>
    /// <param name="description">IC description of players crimes, details, etc.</param>
    /// <param name="vessel">IC name of last known bounty vessel. Can be station/ship name or custom string.</param>
    /// <param name="dna">Optional DNA of the bounty head.</param>
    /// <param name="author">Optional bounty poster IC name.</param>
    /// <param name="authorUid">Uid of the cartridge loader that created the bounty</param>
    /// <param name="pdaAlert">Should PDAs send a localized alert?</param>
    /// <param name="actor">The entity posting the bounty.</param>
    /// <returns>New bounty contract. Null if contract creation failed.</returns>
    public BountyContract? TryCreateBountyContract(ProtoId<BountyContractCollectionPrototype> collection,
        BountyContractCategory category,
        string name,
        int reward,
        EntityUid authorUid,
        EntityUid actor,
        string? description = null,
        string? vessel = null,
        string? dna = null,
        string? author = null)
    {
        var data = GetContracts();
        if (data == null
            || data.Contracts == null
            || !data.Contracts.TryGetValue(collection, out var contracts)
            || !HasWriteAccess(authorUid, collection))
        {
            return null;
        }

        if (!IsRewardValid(reward))
            return null;

        if (name.Length > MaxNameLength)
            name = name.Substring(0, MaxNameLength);
        if (vessel != null && vessel.Length > MaxVesselLength)
            vessel = vessel.Substring(0, MaxVesselLength);
        description = BuildContractDescriptionWithRouteContext(
            description,
            vessel,
            string.IsNullOrWhiteSpace(author)
                ? Loc.GetString("bounty-contracts-route-source-generated")
                : author);
        if (description != null && description.Length > MaxDescriptionLength)
            description = description.Substring(0, MaxDescriptionLength);

        // create a new contract
        var contractId = data.LastId++;
        var contract = new BountyContract(contractId, category, name, reward, GetNetEntity(authorUid),
            dna, vessel, description, author);

        // try to save it
        if (!contracts.TryAdd(contractId, contract))
        {
            _sawmill.Error($"Failed to create bounty contract with {contractId}! LastId: {data.LastId}.");
            return null;
        }

        var notificationType = BountyContractNotificationType.None;
        if (_proto.TryIndex(collection, out var bountyCollection))
            notificationType = bountyCollection.NotificationType;

        LocId announcement = "bounty-contracts-announcement-generic-create";
        if (CategoriesMeta.TryGetValue(category, out var categoryMeta) && categoryMeta.Announcement != null)
            announcement = categoryMeta.Announcement.Value;

        // Generate a notification
        if (notificationType == BountyContractNotificationType.PDA)
        {
            var sender = Loc.GetString("bounty-contracts-announcement-pda-name");
            var target = !string.IsNullOrEmpty(contract.Vessel) && contract.Vessel != Loc.GetString("bounty-contracts-ui-create-vessel-unknown")
                ? $"{contract.Name} ({contract.Vessel})"
                : contract.Name;
            var msg = Loc.GetString(announcement,
                ("target", target), ("reward", BankSystemExtensions.ToSpesoString(contract.Reward)));

            var pdaList = EntityQueryEnumerator<CartridgeLoaderComponent>();
            while (pdaList.MoveNext(out var loaderUid, out var loaderComp))
            {
                if (_cartridgeLoader.TryGetProgram<BountyContractsCartridgeComponent>(loaderUid, out _, out var cartComp, true, loaderComp)
                    && cartComp.NotificationsEnabled)
                {
                    _cartridgeLoader.SendNotification(loaderUid, sender, msg, loaderComp);
                }
            }
        }
        else if (notificationType == BountyContractNotificationType.Radio)
        {
            var sender = Loc.GetString("bounty-contracts-announcement-radio-name");
            var target = !string.IsNullOrEmpty(contract.Vessel) && contract.Vessel != Loc.GetString("bounty-contracts-ui-create-vessel-unknown")
                ? $"{contract.Name} ({contract.Vessel})"
                : contract.Name;
            var msg = Loc.GetString(announcement,
                ("target", target), ("reward", BankSystemExtensions.ToSpesoString(contract.Reward)));
            var color = Color.FromHex("#D7D7BE");
            _chat.DispatchGlobalAnnouncement(msg, sender, false, colorOverride: color);
        }

        _adminLog.Add(LogType.BountyContractCreated, $"{ToPrettyString(actor):actor} posted a {category} bounty with ID {contractId} in the {collection} collection for ${reward}: {description ?? ""}");

        return contract;
    }

    public BountyContract? TryCreateGeneratedBountyContract(ProtoId<BountyContractCollectionPrototype> collection,
        BountyContractCategory category,
        string name,
        int reward,
        EntityUid authorUid,
        string? description = null,
        string? vessel = null,
        string? dna = null,
        string? author = null)
    {
        var data = GetContracts();
        if (data == null ||
            data.Contracts == null ||
            !data.Contracts.TryGetValue(collection, out var contracts))
        {
            return null;
        }

        reward = Math.Clamp(reward, MinReward, MaxReward);

        if (name.Length > MaxNameLength)
            name = name.Substring(0, MaxNameLength);
        if (vessel != null && vessel.Length > MaxVesselLength)
            vessel = vessel.Substring(0, MaxVesselLength);
        description = BuildContractDescriptionWithRouteContext(
            description,
            vessel,
            Loc.GetString("bounty-contracts-route-source-pda"));
        if (description != null && description.Length > MaxDescriptionLength)
            description = description.Substring(0, MaxDescriptionLength);

        var contractId = data.LastId++;
        var contract = new BountyContract(contractId, category, name, reward, GetNetEntity(authorUid),
            dna, vessel, description, author);

        if (!contracts.TryAdd(contractId, contract))
        {
            _sawmill.Error($"Failed to create generated bounty contract with {contractId}! LastId: {data.LastId}.");
            return null;
        }

        _adminLog.Add(LogType.BountyContractCreated, $"Generated sector bounty with ID {contractId} in the {collection} collection for ${reward}: {description ?? ""}");

        return contract;
    }

    public IReadOnlyList<BountyContract> GetContracts(ProtoId<BountyContractCollectionPrototype> collection)
    {
        var data = GetContracts();
        if (data == null ||
            data.Contracts == null ||
            !data.Contracts.TryGetValue(collection, out var contracts))
        {
            return [];
        }

        return contracts.Values.ToList();
    }

    /// <summary>
    ///     Try to get a bounty contract by its id.
    /// </summary>
    public bool TryGetContract(uint contractId, [NotNullWhen(true)] out BountyContract? contract)
    {
        contract = null;
        var data = GetContracts();
        if (data == null || data.Contracts == null)
            return false;

        // Linear over # collections, should be a small set
        foreach (var collection in data.Contracts.Values)
        {
            if (collection.TryGetValue(contractId, out contract))
                return true;
        }

        return false;
    }

    public bool TrySetBountyContractAccepted(EntityUid loaderUid, EntityUid actor, uint contractId, bool accepted)
    {
        var data = GetContracts();
        if (data == null || data.Contracts == null)
            return false;

        var loaderNet = GetNetEntity(loaderUid);
        foreach (var collection in data.Contracts.Values)
        {
            if (!collection.TryGetValue(contractId, out var contract))
                continue;

            var alreadyAccepted = contract.AcceptedByUid != NetEntity.Invalid;
            var acceptedByThisLoader = contract.AcceptedByUid == loaderNet;

            if (accepted)
            {
                if (alreadyAccepted && !acceptedByThisLoader)
                    return false;

                contract.AcceptedByUid = loaderNet;
                contract.AcceptedBy = Identity.Name(actor, EntityManager);
                if (_player.TryGetSessionByEntity(actor, out var session))
                {
                    _chatManager.DispatchServerMessage(session, BuildAcceptedContractMessage(contract));
                    if (!acceptedByThisLoader)
                        _chatManager.DispatchServerMessage(session, TryGiveAcceptedContractPinpointer(actor, contract));
                }
            }
            else
            {
                if (alreadyAccepted && !acceptedByThisLoader)
                    return false;

                contract.AcceptedByUid = NetEntity.Invalid;
                contract.AcceptedBy = null;
            }

            _adminLog.Add(LogType.BountyContractCreated, $"{ToPrettyString(actor):actor} {(accepted ? "accepted" : "released")} bounty contract ID {contractId}: {contract.Name}");
            return true;
        }

        return false;
    }

    private string TryGiveAcceptedContractPinpointer(EntityUid actor, BountyContract contract)
    {
        if (string.IsNullOrWhiteSpace(contract.Vessel) ||
            contract.Vessel.Equals(Loc.GetString("bounty-contracts-ui-create-vessel-unknown"), StringComparison.OrdinalIgnoreCase))
        {
            return Loc.GetString("bounty-contracts-pinpointer-no-vessel");
        }

        if (!TryFindContractRouteTarget(contract.Vessel, out var targetUid, out var targetName))
        {
            return Loc.GetString(
                "bounty-contracts-pinpointer-target-not-found",
                ("vessel", contract.Vessel));
        }

        var pinpointerUid = Spawn(ContractRoutePinpointerPrototype, Transform(actor).Coordinates);
        if (!TryComp<PinpointerComponent>(pinpointerUid, out var pinpointer))
        {
            QueueDel(pinpointerUid);
            return Loc.GetString("bounty-contracts-pinpointer-invalid-prototype");
        }

        _pinpointer.SetTarget(pinpointerUid, targetUid, pinpointer);
        if (!pinpointer.IsActive)
            _pinpointer.TogglePinpointer(pinpointerUid, pinpointer);

        var pickedUp = _hands.TryForcePickupAnyHand(actor, pinpointerUid, checkActionBlocker: false);
        return pickedUp
            ? Loc.GetString("bounty-contracts-pinpointer-given", ("target", targetName))
            : Loc.GetString("bounty-contracts-pinpointer-created-nearby", ("target", targetName));
    }

    private bool TryFindContractRouteTarget(string vessel, out EntityUid targetUid, out string targetName)
    {
        targetUid = default;
        targetName = string.Empty;
        var routeName = vessel.Trim();

        if (routeName.Length == 0)
            return false;

        var stationQuery = EntityQueryEnumerator<StationDataComponent, MetaDataComponent>();
        while (stationQuery.MoveNext(out var stationUid, out var stationData, out var stationMeta))
        {
            if (!IsRouteNameMatch(stationMeta.EntityName, routeName))
                continue;

            targetUid = TryGetStationGridTarget(stationData, out var gridUid)
                ? gridUid
                : stationUid;
            targetName = stationMeta.EntityName;
            return true;
        }

        EntityUid? partialGrid = null;
        string? partialGridName = null;
        var gridQuery = EntityQueryEnumerator<MapGridComponent, MetaDataComponent>();
        while (gridQuery.MoveNext(out var gridUid, out _, out var gridMeta))
        {
            if (gridMeta.EntityName.Equals(routeName, StringComparison.OrdinalIgnoreCase))
            {
                targetUid = gridUid;
                targetName = gridMeta.EntityName;
                return true;
            }

            if (partialGrid == null && IsRouteNameMatch(gridMeta.EntityName, routeName))
            {
                partialGrid = gridUid;
                partialGridName = gridMeta.EntityName;
            }
        }

        if (partialGrid == null || partialGridName == null)
            return false;

        targetUid = partialGrid.Value;
        targetName = partialGridName;
        return true;
    }

    private bool TryGetStationGridTarget(StationDataComponent stationData, out EntityUid gridUid)
    {
        foreach (var grid in stationData.Grids)
        {
            if (!Exists(grid))
                continue;

            gridUid = grid;
            return true;
        }

        gridUid = default;
        return false;
    }

    private static bool IsRouteNameMatch(string candidate, string routeName)
    {
        return candidate.Equals(routeName, StringComparison.OrdinalIgnoreCase) ||
               candidate.Contains(routeName, StringComparison.OrdinalIgnoreCase) ||
               routeName.Contains(candidate, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildAcceptedContractMessage(BountyContract contract)
    {
        var output = new StringBuilder();
        output.Append(Robust.Shared.Localization.Loc.GetString(
            "bounty-contracts-accepted-message-header",
            ("name", contract.Name),
            ("reward", BankSystemExtensions.ToSpesoString(contract.Reward))));

        if (!string.IsNullOrWhiteSpace(contract.Vessel))
        {
            output.Append(' ');
            output.Append(Robust.Shared.Localization.Loc.GetString("bounty-contracts-accepted-message-vessel", ("vessel", contract.Vessel)));
        }

        if (string.IsNullOrWhiteSpace(contract.Description))
        {
            output.Append(' ');
            output.Append(Robust.Shared.Localization.Loc.GetString("bounty-contracts-accepted-message-empty-description"));
            AppendAcceptedTurnInHint(output);
            return output.ToString();
        }

        output.Append(' ');
        output.Append(Robust.Shared.Localization.Loc.GetString("bounty-contracts-accepted-message-description", ("description", contract.Description)));
        if (!SharedBountyContractSystem.HasRouteHint(contract.Description))
        {
            output.Append(' ');
            output.Append(Robust.Shared.Localization.Loc.GetString("bounty-contracts-accepted-message-search-fallback"));
        }

        AppendAcceptedTurnInHint(output);
        return output.ToString();
    }

    private static void AppendAcceptedTurnInHint(StringBuilder output)
    {
        output.Append(' ');
        output.Append(Robust.Shared.Localization.Loc.GetString("bounty-contracts-accepted-turn-in-hint"));
    }

    private static bool HasRouteHint(string description)
    {
        return description.Contains("GPS", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("координ", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("маркер", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("маяк", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("карта", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("route", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildContractDescriptionWithRouteContext(string? description, string? vessel, string source)
    {
        var output = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(description))
            output.Append(description.Trim());

        if (!SharedBountyContractSystem.HasRouteHint(description))
        {
            AppendSentenceSeparator(output);
            if (!string.IsNullOrWhiteSpace(vessel) &&
                !vessel.Equals(Robust.Shared.Localization.Loc.GetString("bounty-contracts-ui-create-vessel-unknown"), StringComparison.OrdinalIgnoreCase))
            {
                output.Append(Robust.Shared.Localization.Loc.GetString("bounty-contracts-route-context-vessel", ("vessel", vessel)));
            }
            else
            {
                output.Append(Robust.Shared.Localization.Loc.GetString("bounty-contracts-route-context-generic"));
            }
        }

        if (!HasSourceHint(description))
        {
            AppendSentenceSeparator(output);
            output.Append(Robust.Shared.Localization.Loc.GetString("bounty-contracts-route-context-source", ("source", source)));
        }

        return output.ToString();
    }

    private static void AppendSentenceSeparator(StringBuilder output)
    {
        if (output.Length == 0)
            return;

        if (output[^1] != ' ')
            output.Append(' ');
    }

    private static bool HasSourceHint(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return false;

        return description.Contains("source", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("generated", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("сгенер", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("источник", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("сгенер", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("источник", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Try to get all bounty contracts available within a particular collection.
    /// </summary>
    public IEnumerable<BountyContract> GetPermittedContracts(Entity<BountyContractsCartridgeComponent> cartridge, EntityUid loader, out ProtoId<BountyContractCollectionPrototype>? newCollection)
    {
        newCollection = null;
        var data = GetContracts();

        if (data == null || data.Contracts == null)
            return Enumerable.Empty<BountyContract>();

        if (cartridge.Comp.Collection != null)
        {
            if (data.Contracts.TryGetValue(cartridge.Comp.Collection.Value, out var contracts)
                && HasReadAccess(loader, cartridge.Comp.Collection.Value, data))
            {
                newCollection = cartridge.Comp.Collection.Value;
                return contracts.Values;
            }
        }

        foreach (var collection in data.Contracts.Keys)
        {
            if (HasReadAccess(loader, collection, data))
            {
                newCollection = collection;
                return data.Contracts[collection].Values;
            }
        }

        // No valid permitted contracts to get
        return Enumerable.Empty<BountyContract>();
    }

    /// <summary>
    ///     Try to remove bounty contract by its id.
    /// </summary>
    /// <returns>True if contract was found and removed.</returns>
    public bool TryRemoveBountyContract(EntityUid authorUid, EntityUid actor, uint contractId)
    {
        var data = GetContracts();
        if (data == null || data.Contracts == null)
            return false;

        foreach (var (collectionId, collection) in data.Contracts)
        {
            if (!collection.TryGetValue(contractId, out var contract))
                continue;

            if (!HasDeleteAccess(authorUid, collectionId, data) && authorUid != GetEntity(contract.AuthorUid))
                return false;

            collection.Remove(contractId);
            _adminLog.Add(LogType.BountyContractRemoved, $"{ToPrettyString(actor):actor} deleted bounty with ID {contractId}");
            return true;
        }

        _sawmill.Warning($"Failed to remove bounty contract with {contractId}!");
        return false;
    }

    public override void Update(float frameTime)
    {
        var cartList = EntityQueryEnumerator<BountyContractsCartridgeComponent>();
        while (cartList.MoveNext(out var loaderUid, out var cartComponent))
        {
            if (cartComponent.CreateEnabled)
                continue;

            if (_timing.CurTime >= cartComponent.NextCreate)
            {
                cartComponent.CreateEnabled = true;
                // TODO: update UI if on the create menu
            }
        }
    }
}
