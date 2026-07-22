using Content.Server.Access.Systems;
using Content.Server.Humanoid;
using Content.Server.IdentityManagement;
using Content.Server.Mind.Commands;
using Content.Server.PDA;
using Content.Server.Station.Components;
using Content.Server.Station.Events;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.CCVar;
using Content.Shared.Clothing;
using Content.Shared.DetailExaminable;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.PDA;
using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Random;
using Content.Shared.Random.Helpers;
using Content.Shared.Roles;
using Content.Shared.Station;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using JetBrains.Annotations;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Utility;
using Content.Server.Spawners.Components;
using Content.Shared._NF.Bank.Components; // DeltaV
using Content.Server._Mono.MonoCoins; // Mono
using Content.Server._NF.Bank; // Frontier
using Content.Server.Preferences.Managers; // Frontier
using System.Linq;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Content.Shared.NameIdentifier; // Frontier
using System.Threading.Tasks;
using Content.Server._EinsteinEngines.Silicon.IPC;
using Content.Shared.Radio.Components; // Goobstation

namespace Content.Server.Station.Systems;

/// <summary>
/// Manages spawning into the game, tracking available spawn points.
/// Also provides helpers for spawning in the player's mob.
/// </summary>
[PublicAPI]
public sealed partial class StationSpawningSystem : SharedStationSpawningSystem
{
    [Dependency] private SharedAccessSystem _accessSystem = default!;
    [Dependency] private ActorSystem _actors = default!;
    [Dependency] private IdCardSystem _cardSystem = default!;
    [Dependency] private IConfigurationManager _configurationManager = default!;
    [Dependency] private HumanoidAppearanceSystem _humanoidSystem = default!;
    [Dependency] private IdentitySystem _identity = default!;
    [Dependency] private MetaDataSystem _metaSystem = default!;
    [Dependency] private PdaSystem _pdaSystem = default!;
    [Dependency] private IPrototypeManager _prototypeManager = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private IDependencyCollection _dependencyCollection = default!; // Frontier
    [Dependency] private IServerPreferencesManager _preferences = default!; // Frontier
    [Dependency] private InternalEncryptionKeySpawner _internalEncryption = default!; // Goobstation
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedStorageSystem _storage = default!;

    [Dependency] private BankSystem _bank = default!; // Frontier
    [Dependency] private MonoCoinsManager _coins = default!; // Mono
    private bool _randomizeCharacters;
    private long _paidLoadoutPrototypeRevision;

    /// <inheritdoc/>
    public override void Initialize()
    {
        base.Initialize();
        Subs.CVar(_configurationManager, CCVars.ICRandomCharacters, e => _randomizeCharacters = e, true);
        SubscribeLocalEvent<PendingPaidLoadoutComponent, PlayerAttachedEvent>(OnPendingPaidLoadoutPlayerAttached);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPaidLoadoutPrototypesReloaded);
    }

    private void OnPaidLoadoutPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        // This is a change token, not a quantity. Deliberate wrap preserves the
        // equality fence at the numeric limit.
        _paidLoadoutPrototypeRevision = unchecked(_paidLoadoutPrototypeRevision + 1);
    }

    /// <summary>
    /// Attempts to spawn a player character onto the given station.
    /// </summary>
    /// <param name="station">Station to spawn onto.</param>
    /// <param name="job">The job to assign, if any.</param>
    /// <param name="profile">The character profile to use, if any.</param>
    /// <param name="stationSpawning">Resolve pattern, the station spawning component for the station.</param>
    /// <param name="spawnPointType">Delta-V: Set desired spawn point type.</param>
    /// <param name="session">Frontier: The session associated with the character, if any.</param>
    /// <returns>The resulting player character, if any.</returns>
    /// <exception cref="ArgumentException">Thrown when the given station is not a station.</exception>
    /// <remarks>
    /// This only spawns the character, and does none of the mind-related setup you'd need for it to be playable.
    /// </remarks>
    public EntityUid? SpawnPlayerCharacterOnStation(EntityUid? station, ProtoId<JobPrototype>? job, HumanoidCharacterProfile? profile, StationSpawningComponent? stationSpawning = null, SpawnPointType spawnPointType = SpawnPointType.Unset, ICommonSession? session = null) // Frontier: add session
    {
        if (station != null && !Resolve(station.Value, ref stationSpawning))
            throw new ArgumentException("Tried to use a non-station entity as a station!", nameof(station));

        // Delta-V: Set desired spawn point type.
        // Frontier: add session
        var ev = new PlayerSpawningEvent(job, profile, station, spawnPointType, session);

        RaiseLocalEvent(ev);
        DebugTools.Assert(ev.SpawnResult is { Valid: true } or null);

        return ev.SpawnResult;
    }

    //TODO: Figure out if everything in the player spawning region belongs somewhere else.
    #region Player spawning helpers

    /// <summary>
    /// Spawns in a player's mob according to their job and character information at the given coordinates.
    /// Used by systems that need to handle spawning players.
    /// </summary>
    /// <param name="coordinates">Coordinates to spawn the character at.</param>
    /// <param name="job">Job to assign to the character, if any.</param>
    /// <param name="profile">Appearance profile to use for the character.</param>
    /// <param name="station">The station this player is being spawned on.</param>
    /// <param name="entity">The entity to use, if one already exists.</param>
    /// <param name="session">Frontier: The session associated with the entity, if one exists.</param>
    /// <returns>The spawned entity</returns>
    public EntityUid SpawnPlayerMob(
        EntityCoordinates coordinates,
        ProtoId<JobPrototype>? job,
        HumanoidCharacterProfile? profile,
        EntityUid? station,
        EntityUid? entity = null,
        ICommonSession? session = null) // Frontier
    {
        _prototypeManager.TryIndex(job ?? string.Empty, out var prototype);
        RoleLoadout? loadout = null;

        // Need to get the loadout up-front to handle names if we use an entity spawn override.
        var jobLoadout = LoadoutSystem.GetJobPrototype(prototype?.ID);

        if (_prototypeManager.TryIndex(jobLoadout, out RoleLoadoutPrototype? roleProto))
        {
            profile?.Loadouts.TryGetValue(jobLoadout, out loadout);

            // Set to default if not present
            if (loadout == null)
            {
                loadout = new RoleLoadout(jobLoadout);
                loadout.SetDefault(profile, _actors.GetSession(entity), _prototypeManager);
                loadout.EnsureValid(profile!, session, _dependencyCollection); // Frontier - profile must not be null, but if it was, TryGetValue above should fail
            }
        }

        // If we're not spawning a humanoid, we're gonna exit early without doing all the humanoid stuff.
        if (prototype?.JobEntity != null)
        {
            DebugTools.Assert(entity is null);
            var jobEntity = EntityManager.SpawnEntity(prototype.JobEntity, coordinates);
            MakeSentientCommand.MakeSentient(jobEntity, EntityManager);

            // Make sure custom names get handled, what is gameticker control flow whoopy.
            if (loadout != null)
            {
                EquipRoleName(jobEntity, loadout, roleProto!);
            }

            DoJobSpecials(job, jobEntity);
            _identity.QueueIdentityUpdate(jobEntity);
            return jobEntity;
        }

        string speciesId;
        if (_randomizeCharacters)
        {
            var weightId = _configurationManager.GetCVar(CCVars.ICRandomSpeciesWeights);
            var weights = _prototypeManager.Index<WeightedRandomSpeciesPrototype>(weightId);
            speciesId = weights.Pick(_random);
        }
        else if (profile != null)
        {
            speciesId = profile.Species;
        }
        else
        {
            speciesId = SharedHumanoidAppearanceSystem.DefaultSpecies;
        }

        if (!_prototypeManager.TryIndex<SpeciesPrototype>(speciesId, out var species))
            throw new ArgumentException($"Invalid species prototype was used: {speciesId}");

        entity ??= Spawn(species.Prototype, coordinates);

        if (_randomizeCharacters)
        {
            profile = HumanoidCharacterProfile.RandomWithSpecies(speciesId);
        }



        if (loadout != null)
        {
            /// Frontier: overwriting EquipRoleLoadout
            //EquipRoleLoadout(entity.Value, loadout, roleProto!);
            long initialBankBalance = profile!.BankBalance; //Frontier
            var cachedLongTermBalance = session == null
                ? 0L
                : _coins.GetMonoCoinsBalance(session.UserId) ?? 0L;
            if (cachedLongTermBalance > 0L)
            {
                initialBankBalance = cachedLongTermBalance > long.MaxValue - initialBankBalance
                    ? long.MaxValue
                    : initialBankBalance + cachedLongTermBalance;
            }
            var bankBalance = initialBankBalance; //Frontier
            bool hasBalance = false; // Frontier
            var paidLoadoutGear = new List<PendingPaidLoadoutEntry>();
            var paidLoadoutFallbackGear = new List<PendingPaidLoadoutEntry>();

            // Note: since this is stored per character, we don't have a cached
            //       reference for randomly generated characters.
            PlayerPreferences? prefs = null;
            var paidLoadoutSlot = -1;
            var paidLoadoutProfileId = 0;
            if (session != null &&
                _preferences.TryGetCachedPreferences(session.UserId, out prefs) &&
                (paidLoadoutSlot = prefs.IndexOfCharacter(profile)) >= 0 &&
                _preferences.TryGetCharacterProfileId(
                    session.UserId,
                    paidLoadoutSlot,
                    out paidLoadoutProfileId))
            {
                hasBalance = true;
            }

            // Order loadout selections by the order they appear on the prototype.
            foreach (var group in loadout.SelectedLoadouts.OrderBy(x => roleProto!.Groups.FindIndex(e => e == x.Key)))
            {
                List<ProtoId<LoadoutPrototype>> equippedItems = new(); //Frontier - track purchased items (list: few items)
                List<ProtoId<LoadoutPrototype>> materializedItems = new();
                var paidItemCount = 0;
                foreach (var items in group.Value)
                {
                    if (!_prototypeManager.TryIndex(items.Prototype, out var loadoutProto))
                    {
                        Log.Error($"Unable to find loadout prototype for {items.Prototype}");
                        continue;
                    }

                    // Handle any extra data here.

                    //Frontier - we handle bank stuff so we are wrapping each item spawn inside our own cached check.
                    //If the user's preferences haven't been loaded, only give them free items or fallbacks.
                    //This way, we will spawn every item we can afford in the order that they were originally sorted.
                    if (loadoutProto.Price < 0)
                    {
                        // A malformed negative price must never be interpreted as
                        // free value. Price zero is the explicit free-loadout value.
                        Log.Warning($"Rejected negative loadout price {loadoutProto.Price} for {loadoutProto.ID}.");
                        continue;
                    }

                    if (loadoutProto.Price <= bankBalance && (loadoutProto.Price == 0 || hasBalance))
                    {
                        if (loadoutProto.Price > 0)
                        {
                            if (!TryCreatePendingPaidLoadoutEntry(loadoutProto, out var pendingEntry))
                            {
                                Log.Error($"Could not capture an immutable paid-loadout quote for {loadoutProto.ID}.");
                                continue;
                            }

                            // Paid value is prepared but not materialized until the
                            // exact durable profile debit commits below.
                            bankBalance -= loadoutProto.Price;
                            equippedItems.Add(loadoutProto.ID);
                            paidLoadoutGear.Add(pendingEntry);
                            paidItemCount++;
                        }
                        else
                        {
                            equippedItems.Add(loadoutProto.ID);
                            materializedItems.Add(loadoutProto.ID);
                            EquipStartingGear(entity.Value, loadoutProto, raiseEvent: false);

                            // Add support for IPC encryption keys from loadout headsets
                            if (HasComp<EncryptionKeyHolderComponent>(entity.Value))
                                _internalEncryption.TryInsertEncryptionKey(entity.Value, loadoutProto); // Removed EntityManager
                        }
                    }
                }

                // If a character cannot afford their current job loadout, ensure they have fallback items for mandatory categories.
                if (_prototypeManager.TryIndex(group.Key, out var groupPrototype))
                {
                    if (equippedItems.Count < groupPrototype.MinLimit)
                    {
                        foreach (var fallback in groupPrototype.Fallbacks)
                        {
                            // Do not duplicate items in loadout
                            if (equippedItems.Contains(fallback))
                                continue;

                            if (!_prototypeManager.TryIndex(fallback, out var loadoutProto))
                            {
                                Log.Error($"Unable to find loadout prototype for fallback {fallback}");
                                continue;
                            }

                            // Fallbacks are granted without a durable debit. Only an
                            // explicit zero price is therefore safe to materialize.
                            if (loadoutProto.Price != 0)
                            {
                                Log.Warning(
                                    $"Rejected non-free loadout fallback {loadoutProto.ID} " +
                                    $"with price {loadoutProto.Price}.");
                                continue;
                            }

                            // Validate effects against the current character.
                            if (!loadout.IsValid(profile!, session, fallback, _dependencyCollection, out var _))
                                continue;

                            EquipStartingGear(entity.Value, loadoutProto, raiseEvent: false);
                            equippedItems.Add(fallback);
                            materializedItems.Add(fallback);

                            // Add support for IPC encryption keys from loadout headsets
                            if (HasComp<EncryptionKeyHolderComponent>(entity.Value))
                                _internalEncryption.TryInsertEncryptionKey(entity.Value, loadoutProto); // Removed EntityManager

                            // Minimum number of items equipped, no need to load more prototypes.
                            if (equippedItems.Count >= groupPrototype.MinLimit)
                                break;
                        }
                    }

                    // A staged paid item satisfies MinLimit only if its debit and
                    // delivery both succeed. Capture enough zero-price fallbacks to
                    // restore the mandatory category after a definite rejection.
                    if (paidItemCount > 0 && materializedItems.Count < groupPrototype.MinLimit)
                    {
                        foreach (var fallback in groupPrototype.Fallbacks)
                        {
                            if (materializedItems.Contains(fallback))
                                continue;

                            if (!_prototypeManager.TryIndex(fallback, out var fallbackProto))
                            {
                                Log.Error($"Unable to find loadout prototype for deferred fallback {fallback}");
                                continue;
                            }

                            if (fallbackProto.Price != 0)
                            {
                                Log.Warning(
                                    $"Rejected non-free deferred loadout fallback {fallbackProto.ID} " +
                                    $"with price {fallbackProto.Price}.");
                                continue;
                            }

                            if (!loadout.IsValid(profile!, session, fallback, _dependencyCollection, out var _) ||
                                !TryCreatePendingPaidLoadoutEntry(fallbackProto, out var fallbackEntry))
                            {
                                continue;
                            }

                            paidLoadoutFallbackGear.Add(fallbackEntry);
                            materializedItems.Add(fallback);
                            if (materializedItems.Count >= groupPrototype.MinLimit)
                                break;
                        }
                    }
                }
            }

            // Frontier: do not re-equip roleLoadout, make sure we equip job startingGear,
            // and deduct loadout costs from a bank account if we have one.
            if (prototype?.StartingGear is not null)
            {
                EquipStartingGear(entity.Value, prototype.StartingGear, raiseEvent: false);

                // Add support for IPC encryption keys from job starting gear headsets
                if (HasComp<EncryptionKeyHolderComponent>(entity.Value) &&
                    _prototypeManager.TryIndex(prototype.StartingGear, out var startingGearProto))
                {
                    _internalEncryption.TryInsertEncryptionKey(entity.Value, startingGearProto);
                }
            }

            var bankComp = EnsureComp<BankAccountComponent>(entity.Value);

            if (hasBalance)
            {
                var paidLoadoutCost = initialBankBalance - bankBalance;
                if (paidLoadoutCost > 0 &&
                    paidLoadoutCost <= int.MaxValue &&
                    paidLoadoutGear.Count > 0)
                {
                    var spawned = entity.Value;
                    if (HasComp<PendingPaidLoadoutComponent>(spawned))
                    {
                        Log.Error($"Refusing to overwrite an existing pending paid loadout on {ToPrettyString(spawned)}.");
                    }
                    else
                    {
                        var pending = AddComp<PendingPaidLoadoutComponent>(spawned);
                        pending.ExpectedSession = session;
                        pending.ExpectedSlot = paidLoadoutSlot;
                        pending.ExpectedProfileId = paidLoadoutProfileId;
                        pending.Cost = (int) paidLoadoutCost;
                        pending.PrototypeRevision = _paidLoadoutPrototypeRevision;
                        pending.Gear.AddRange(paidLoadoutGear);
                        pending.FallbackGear.AddRange(paidLoadoutFallbackGear);

                        // Normally PlayerAttachedEvent is raised after this method
                        // returns. Handle callers that supplied an already-attached
                        // entity without weakening the exact-attachment check.
                        if (IsExpectedPaidLoadoutAttachment(spawned, pending))
                            StartPendingPaidLoadout(spawned, pending);
                    }
                }
                else if (paidLoadoutGear.Count > 0)
                {
                    Log.Error(
                        $"Rejected paid loadout on {ToPrettyString(entity.Value)} because its aggregate cost " +
                        $"{paidLoadoutCost} cannot be represented as a durable debit.");
                }
            }
            /// End Frontier: overwriting EquipRoleLoadout
        }

        var gearEquippedEv = new StartingGearEquippedEvent(entity.Value);
        RaiseLocalEvent(entity.Value, ref gearEquippedEv);

        if (profile != null)
        {
            // Frontier: allow pseudonyms
            var name = loadout != null && !string.IsNullOrEmpty(loadout.EntityName) ? loadout.EntityName : profile.Name;
            // Janky hack for borgs
            if (TryComp<NameIdentifierComponent>(entity.Value, out var identifier))
            {
                // Append our name identifier (why have a pseudonym for a role that has a complete name identifier group?)
                name = $"{name} {identifier.FullIdentifier}";
            }
            // End Frontier
            if (prototype != null)
                SetPdaAndIdCardData(entity.Value, name, prototype, station); // Frontier: profile.Name<name

            _humanoidSystem.LoadProfile(entity.Value, profile);
            _metaSystem.SetEntityName(entity.Value, name); // Frontier: profile.Name<name
            if (profile.FlavorText != "" && _configurationManager.GetCVar(CCVars.FlavorText))
            {
                AddComp<DetailExaminableComponent>(entity.Value).Content = profile.FlavorText;
            }
        }

        DoJobSpecials(job, entity.Value);
        _identity.QueueIdentityUpdate(entity.Value);
        return entity.Value;
    }

    private void OnPendingPaidLoadoutPlayerAttached(
        EntityUid entity,
        PendingPaidLoadoutComponent component,
        PlayerAttachedEvent args)
    {
        if (!ReferenceEquals(component.ExpectedSession, args.Player))
            return;

        StartPendingPaidLoadout(entity, component);
    }

    private void StartPendingPaidLoadout(EntityUid entity, PendingPaidLoadoutComponent component)
    {
        // Bind every bank argument before the first await. The immutable quote
        // captured by ProcessPendingPaidLoadoutAsync will independently verify
        // that this component still describes the same operation at finalization.
        var expectedSession = component.ExpectedSession;
        var expectedSlot = component.ExpectedSlot;
        var expectedProfileId = component.ExpectedProfileId;
        var expectedCost = component.Cost;
        var debitTask = ProcessPendingPaidLoadoutAsync(
            entity,
            component,
            finalizeAfterCommit => _bank.TryBankWithdrawProfileAsync(
                expectedSession!,
                entity,
                expectedSlot,
                expectedProfileId,
                expectedCost,
                spendLongTerm: true,
                finalizeAfterCommit));
        _ = ObservePaidLoadoutDebitAsync(debitTask, entity, expectedSession!);
    }

    private readonly record struct PendingPaidLoadoutQuote(
        ICommonSession Session,
        int Slot,
        int ProfileId,
        int Cost,
        long PrototypeRevision,
        ImmutableArray<PendingPaidLoadoutEntry> Gear,
        ImmutableArray<PendingPaidLoadoutEntry> FallbackGear);

    /// <summary>
    /// Starts a pending paid loadout once, only from its exact attached session
    /// and exact selected ProfileId. The injectable debit boundary keeps the
    /// attachment/finalizer state machine directly testable for both synchronous
    /// and delayed durable stores.
    /// </summary>
    internal async Task<bool> ProcessPendingPaidLoadoutAsync(
        EntityUid entity,
        PendingPaidLoadoutComponent component,
        Func<Func<bool>, Task<bool>> debit)
    {
        ArgumentNullException.ThrowIfNull(debit);

        if (component.Started || !IsExpectedPaidLoadoutAttachment(entity, component))
            return false;

        if (component.ExpectedSession is not { } expectedSession)
            return false;

        var quote = new PendingPaidLoadoutQuote(
            expectedSession,
            component.ExpectedSlot,
            component.ExpectedProfileId,
            component.Cost,
            component.PrototypeRevision,
            component.Gear.ToImmutableArray(),
            component.FallbackGear.ToImmutableArray());
        component.Started = true;
        var finalizerInvoked = false;
        try
        {
            if (!IsExpectedPaidLoadoutProfile(component, quote))
                return false;

            var debited = await debit(() =>
            {
                if (finalizerInvoked)
                    return false;

                finalizerInvoked = true;
                return IsExpectedPaidLoadoutContext(entity, component, quote) &&
                       FinalizePaidLoadout(entity, quote);
            });

            if (!debited &&
                !quote.FallbackGear.IsEmpty &&
                IsExpectedPaidLoadoutContext(entity, component, quote))
            {
                FinalizePaidLoadoutFallbacks(entity, quote);
            }

            return debited;
        }
        finally
        {
            if (Exists(entity) &&
                TryComp<PendingPaidLoadoutComponent>(entity, out var current) &&
                ReferenceEquals(current, component))
            {
                RemCompDeferred<PendingPaidLoadoutComponent>(entity);
            }
        }
    }

    private bool IsExpectedPaidLoadoutAttachment(
        EntityUid entity,
        PendingPaidLoadoutComponent component)
    {
        return Exists(entity) &&
               component.ExpectedSession is { } expectedSession &&
               expectedSession.AttachedEntity == entity &&
               _actors.TryGetSession(entity, out var actorSession) &&
               ReferenceEquals(actorSession, expectedSession) &&
               TryComp<PendingPaidLoadoutComponent>(entity, out var current) &&
               ReferenceEquals(current, component);
    }

    private static bool IsPendingPaidLoadoutQuoteCurrent(
        PendingPaidLoadoutComponent component,
        PendingPaidLoadoutQuote quote)
    {
        return component.Started &&
               ReferenceEquals(component.ExpectedSession, quote.Session) &&
               component.ExpectedSlot == quote.Slot &&
               component.ExpectedProfileId == quote.ProfileId &&
               component.Cost == quote.Cost &&
               component.PrototypeRevision == quote.PrototypeRevision &&
               component.Gear.SequenceEqual(quote.Gear) &&
               component.FallbackGear.SequenceEqual(quote.FallbackGear);
    }

    private bool IsExpectedPaidLoadoutProfile(
        PendingPaidLoadoutComponent component,
        PendingPaidLoadoutQuote quote)
    {
        if (!IsPendingPaidLoadoutQuoteCurrent(component, quote) ||
            quote.Slot < 0 ||
            quote.ProfileId <= 0 ||
            quote.Cost <= 0 ||
            quote.Gear.IsEmpty ||
            quote.PrototypeRevision != _paidLoadoutPrototypeRevision ||
            !_preferences.TryGetCharacterProfileId(
                quote.Session.UserId,
                quote.Slot,
                out var currentProfileId) ||
            currentProfileId != quote.ProfileId ||
            !_preferences.TryGetCachedPreferences(quote.Session.UserId, out var preferences) ||
            preferences.SelectedCharacterIndex != quote.Slot ||
            !preferences.Characters.TryGetValue(quote.Slot, out var character) ||
            character is not HumanoidCharacterProfile)
        {
            return false;
        }

        long quotedCost = 0;
        foreach (var entry in quote.Gear)
        {
            if (entry.Price <= 0 || !IsPendingPaidLoadoutEntryCurrent(entry))
                return false;

            quotedCost += entry.Price;
            if (quotedCost > int.MaxValue)
                return false;
        }

        if (quotedCost != quote.Cost)
            return false;

        foreach (var fallback in quote.FallbackGear)
        {
            if (fallback.Price != 0 || !IsPendingPaidLoadoutEntryCurrent(fallback))
                return false;
        }

        return true;
    }

    private bool IsExpectedPaidLoadoutContext(
        EntityUid entity,
        PendingPaidLoadoutComponent component,
        PendingPaidLoadoutQuote quote)
    {
        return IsExpectedPaidLoadoutAttachment(entity, component) &&
               IsExpectedPaidLoadoutProfile(component, quote);
    }

    private bool FinalizePaidLoadout(EntityUid entity, PendingPaidLoadoutQuote quote)
    {
        if (Deleted(entity))
            return false;

        if (quote.PrototypeRevision != _paidLoadoutPrototypeRevision ||
            quote.Gear.Any(entry => entry.Price <= 0 || !IsPendingPaidLoadoutEntryCurrent(entry)))
        {
            return false;
        }

        var retainDebit = TryMaterializePendingLoadouts(
            entity,
            quote.Gear,
            "paid loadout",
            out var complete);
        if (!retainDebit)
            return false;

        // A rollback could theoretically be incomplete after a deep entity-spawn
        // failure. Retain the debit conservatively for surviving paid value, but do
        // not publish a false completion event.
        if (!complete)
            return true;

        if (HasComp<EncryptionKeyHolderComponent>(entity))
        {
            foreach (var entry in quote.Gear)
            {
                try
                {
                    var loadoutProto = _prototypeManager.Index(entry.Prototype);
                    _internalEncryption.TryInsertEncryptionKey(entity, loadoutProto);
                }
                catch (Exception exception)
                {
                    // All quoted value is already delivered. Encryption projection
                    // cannot make the debit retryable.
                    Log.Error($"Could not project paid IPC encryption keys for {ToPrettyString(entity)}: {exception}");
                }
            }
        }

        // Existing starting-gear listeners need a post-paid pass because the
        // initial event necessarily ran before this durable async finalizer.
        try
        {
            var gearEquipped = new StartingGearEquippedEvent(entity);
            RaiseLocalEvent(entity, ref gearEquipped);
        }
        catch (Exception exception)
        {
            // Paid gear already exists. Listener delivery cannot make the debit
            // retryable or refund materialized value.
            Log.Error($"Could not project post-paid starting gear for {ToPrettyString(entity)}: {exception}");
        }

        try
        {
            var paidEquipped = new PaidLoadoutEquippedEvent(entity);
            RaiseLocalEvent(entity, ref paidEquipped);
        }
        catch (Exception exception)
        {
            Log.Error($"Could not publish paid loadout completion for {ToPrettyString(entity)}: {exception}");
        }

        return true;
    }

    private void FinalizePaidLoadoutFallbacks(EntityUid entity, PendingPaidLoadoutQuote quote)
    {
        if (quote.PrototypeRevision != _paidLoadoutPrototypeRevision ||
            quote.FallbackGear.Any(entry => entry.Price != 0 || !IsPendingPaidLoadoutEntryCurrent(entry)))
        {
            return;
        }

        var delivered = TryMaterializePendingLoadouts(
            entity,
            quote.FallbackGear,
            "zero-price paid-loadout fallback",
            out var complete);
        if (!delivered || !complete)
            return;

        try
        {
            var gearEquipped = new StartingGearEquippedEvent(entity);
            RaiseLocalEvent(entity, ref gearEquipped);
        }
        catch (Exception exception)
        {
            Log.Error($"Could not project paid-loadout fallback gear for {ToPrettyString(entity)}: {exception}");
        }
    }

    /// <summary>
    /// Test and lifecycle helper for capturing the exact quote used by the real
    /// spawn path. It intentionally throws rather than manufacturing an invalid
    /// quote.
    /// </summary>
    internal PendingPaidLoadoutEntry CapturePendingPaidLoadoutEntry(ProtoId<LoadoutPrototype> prototypeId)
    {
        var prototype = _prototypeManager.Index(prototypeId);
        if (!TryCreatePendingPaidLoadoutEntry(prototype, out var entry))
            throw new InvalidOperationException($"Could not capture paid-loadout quote {prototypeId}.");

        return entry;
    }

    internal long PaidLoadoutPrototypeRevision => _paidLoadoutPrototypeRevision;

    private bool TryCreatePendingPaidLoadoutEntry(
        LoadoutPrototype prototype,
        out PendingPaidLoadoutEntry entry)
    {
        entry = default;
        if (prototype.Price < 0)
            return false;

        var materialization = ImmutableArray.CreateBuilder<PendingPaidLoadoutMaterialization>(2);
        if (prototype.StartingGear is { } startingGearId)
        {
            if (!_prototypeManager.TryIndex(startingGearId, out StartingGearPrototype? startingGear))
                return false;

            materialization.Add(CapturePaidLoadoutMaterialization(startingGear));
        }

        materialization.Add(CapturePaidLoadoutMaterialization(prototype));
        var immutableMaterialization = materialization.ToImmutable();
        if (!immutableMaterialization.Any(HasPaidLoadoutValue))
            return false;

        entry = new PendingPaidLoadoutEntry(
            prototype.ID,
            prototype.Price,
            ComputePaidLoadoutValueFingerprint(prototype.ID, prototype.Price, immutableMaterialization),
            immutableMaterialization);
        return true;
    }

    private static PendingPaidLoadoutMaterialization CapturePaidLoadoutMaterialization(
        IEquipmentLoadout loadout)
    {
        var equipment = loadout.Equipment
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new PendingPaidLoadoutEquipment(pair.Key, pair.Value))
            .ToImmutableArray();
        var storage = loadout.Storage
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SelectMany(pair => pair.Value.Select(entity => new PendingPaidLoadoutStorage(pair.Key, entity)))
            .ToImmutableArray();

        return new PendingPaidLoadoutMaterialization(
            equipment,
            loadout.Inhand.ToImmutableArray(),
            storage);
    }

    private static bool HasPaidLoadoutValue(PendingPaidLoadoutMaterialization materialization)
    {
        return !materialization.Equipment.IsEmpty ||
               !materialization.Inhand.IsEmpty ||
               !materialization.Storage.IsEmpty;
    }

    private static string ComputePaidLoadoutValueFingerprint(
        string prototype,
        int price,
        ImmutableArray<PendingPaidLoadoutMaterialization> materialization)
    {
        var canonical = new StringBuilder();
        AppendPaidLoadoutFingerprintToken(canonical, prototype);
        AppendPaidLoadoutFingerprintToken(canonical, price.ToString(CultureInfo.InvariantCulture));
        AppendPaidLoadoutFingerprintToken(canonical, materialization.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var pass in materialization)
        {
            AppendPaidLoadoutFingerprintToken(canonical, pass.Equipment.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var equipment in pass.Equipment)
            {
                AppendPaidLoadoutFingerprintToken(canonical, equipment.Slot);
                AppendPaidLoadoutFingerprintToken(canonical, equipment.Entity.Id);
            }

            AppendPaidLoadoutFingerprintToken(canonical, pass.Inhand.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var inhand in pass.Inhand)
                AppendPaidLoadoutFingerprintToken(canonical, inhand.Id);

            AppendPaidLoadoutFingerprintToken(canonical, pass.Storage.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var storage in pass.Storage)
            {
                AppendPaidLoadoutFingerprintToken(canonical, storage.Slot);
                AppendPaidLoadoutFingerprintToken(canonical, storage.Entity.Id);
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void AppendPaidLoadoutFingerprintToken(StringBuilder target, string value)
    {
        target.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        target.Append(':');
        target.Append(value);
    }

    private bool IsPendingPaidLoadoutEntryCurrent(PendingPaidLoadoutEntry entry)
    {
        var capturedFingerprint = ComputePaidLoadoutValueFingerprint(
            entry.Prototype.Id,
            entry.Price,
            entry.Materialization);
        if (!string.Equals(entry.ValueFingerprint, capturedFingerprint, StringComparison.Ordinal))
            return false;

        if (!_prototypeManager.TryIndex(entry.Prototype, out LoadoutPrototype? prototype) ||
            prototype.Price != entry.Price ||
            !TryCreatePendingPaidLoadoutEntry(prototype, out var current))
        {
            return false;
        }

        return string.Equals(entry.ValueFingerprint, current.ValueFingerprint, StringComparison.Ordinal);
    }

    private bool TryMaterializePendingLoadouts(
        EntityUid entity,
        IReadOnlyCollection<PendingPaidLoadoutEntry> entries,
        string operation,
        out bool complete)
    {
        complete = false;
        if (!Exists(entity) || Deleted(entity) || entries.Count == 0)
            return false;

        foreach (var entry in entries)
        {
            foreach (var pass in entry.Materialization)
            {
                foreach (var equipment in pass.Equipment)
                {
                    if (!_prototypeManager.TryIndex<EntityPrototype>(equipment.Entity, out _))
                        return false;
                }

                foreach (var inhand in pass.Inhand)
                {
                    if (!_prototypeManager.TryIndex<EntityPrototype>(inhand, out _))
                        return false;
                }

                foreach (var storage in pass.Storage)
                {
                    if (!_prototypeManager.TryIndex<EntityPrototype>(storage.Entity, out _))
                        return false;
                }
            }
        }

        var coordinates = Transform(entity).Coordinates;
        var spawned = new List<(EntityUid Entity, EntProtoId Prototype)>();
        try
        {
            EntityUid SpawnTracked(EntProtoId prototype)
            {
                var spawnedEntity = Spawn(prototype, coordinates);
                spawned.Add((spawnedEntity, prototype));
                if (!Exists(spawnedEntity) || Deleted(spawnedEntity) ||
                    MetaData(spawnedEntity).EntityPrototype?.ID != prototype.Id)
                {
                    throw new InvalidOperationException($"Spawned value {prototype} did not remain authoritative.");
                }

                return spawnedEntity;
            }

            foreach (var entry in entries)
            {
                foreach (var pass in entry.Materialization)
                {
                    foreach (var equipment in pass.Equipment)
                    {
                        var item = SpawnTracked(equipment.Entity);
                        if (!InventorySystem.TryEquip(entity, item, equipment.Slot, silent: true, force: true))
                        {
                            Log.Warning(
                                $"Could not equip quoted {operation} item {equipment.Entity} in {equipment.Slot}; " +
                                "it was delivered at the player's location.");
                        }
                    }

                    foreach (var inhand in pass.Inhand)
                    {
                        var item = SpawnTracked(inhand);
                        if (!_hands.TryPickupAnyHand(
                                entity,
                                item,
                                checkActionBlocker: false,
                                animate: false))
                        {
                            Log.Warning(
                                $"Could not place quoted {operation} item {inhand} in a hand; " +
                                "it was delivered at the player's location.");
                        }
                    }

                    foreach (var storage in pass.Storage)
                    {
                        var item = SpawnTracked(storage.Entity);
                        if (!InventorySystem.TryGetSlotEntity(entity, storage.Slot, out var storageEntity) ||
                            !TryComp<StorageComponent>(storageEntity, out var storageComponent) ||
                            !_storage.Insert(
                                storageEntity.Value,
                                item,
                                out _,
                                storageComp: storageComponent,
                                playSound: false,
                                stackAutomatically: false))
                        {
                            Log.Warning(
                                $"Could not insert quoted {operation} item {storage.Entity} into {storage.Slot}; " +
                                "it was delivered at the player's location.");
                        }
                    }
                }
            }

            if (spawned.Any(value =>
                    !Exists(value.Entity) ||
                    Deleted(value.Entity) ||
                    MetaData(value.Entity).EntityPrototype?.ID != value.Prototype.Id))
            {
                throw new InvalidOperationException($"A quoted {operation} entity disappeared during delivery.");
            }

            complete = true;
            return true;
        }
        catch (Exception exception)
        {
            Log.Error($"Could not materialize {operation} for {ToPrettyString(entity)}: {exception}");
            foreach (var value in spawned.AsEnumerable().Reverse())
            {
                try
                {
                    if (Exists(value.Entity) && !Deleted(value.Entity))
                        EntityManager.DeleteEntity(value.Entity);
                }
                catch (Exception rollbackException)
                {
                    Log.Error($"Could not remove failed {operation} value {value.Entity}: {rollbackException}");
                }
            }

            var rollbackComplete = spawned.All(value => !Exists(value.Entity) || Deleted(value.Entity));
            return !rollbackComplete;
        }
    }

    private async Task ObservePaidLoadoutDebitAsync(
        Task<bool> debitTask,
        EntityUid entity,
        ICommonSession session)
    {
        try
        {
            if (!await debitTask)
                Log.Warning($"Paid loadout was not equipped for {session.UserId} on {ToPrettyString(entity)} because its durable debit failed.");
        }
        catch (Exception exception)
        {
            // The bank system has already attached a stable fail-closed block to
            // ambiguous outcomes. Paid gear remains unmaterialized.
            Log.Error($"Paid loadout debit failed for {session.UserId} on {ToPrettyString(entity)}: {exception}");
        }
    }

    private void DoJobSpecials(ProtoId<JobPrototype>? job, EntityUid entity)
    {
        if (!_prototypeManager.TryIndex(job ?? string.Empty, out JobPrototype? prototype))
            return;

        foreach (var jobSpecial in prototype.Special)
        {
            jobSpecial.AfterEquip(entity);
        }
    }

    /// <summary>
    /// Sets the ID card and PDA name, job, and access data.
    /// </summary>
    /// <param name="entity">Entity to load out.</param>
    /// <param name="characterName">Character name to use for the ID.</param>
    /// <param name="jobPrototype">Job prototype to use for the PDA and ID.</param>
    /// <param name="station">The station this player is being spawned on.</param>
    public void SetPdaAndIdCardData(EntityUid entity, string characterName, JobPrototype jobPrototype, EntityUid? station)
    {
        if (!InventorySystem.TryGetSlotEntity(entity, "id", out var idUid))
            return;

        var cardId = idUid.Value;
        if (TryComp<PdaComponent>(idUid, out var pdaComponent) && pdaComponent.ContainedId != null)
            cardId = pdaComponent.ContainedId.Value;

        if (!TryComp<IdCardComponent>(cardId, out var card))
            return;

        _cardSystem.TryChangeFullName(cardId, characterName, card);
        _cardSystem.TryChangeJobTitle(cardId, jobPrototype.LocalizedName, card);

        if (_prototypeManager.TryIndex(jobPrototype.Icon, out var jobIcon))
            _cardSystem.TryChangeJobIcon(cardId, jobIcon, card);

        var extendedAccess = false;
        if (station != null)
        {
            var data = Comp<StationJobsComponent>(station.Value);
            extendedAccess = data.ExtendedAccess;
        }

        _accessSystem.SetAccessToJob(cardId, jobPrototype, extendedAccess);

        if (pdaComponent != null)
            _pdaSystem.SetOwner(idUid.Value, pdaComponent, entity, characterName);
    }


    #endregion Player spawning helpers
}

/// <summary>
/// Ordered broadcast event fired on any spawner eligible to attempt to spawn a player.
/// This event's success is measured by if SpawnResult is not null.
/// You should not make this event's success rely on random chance.
/// This event is designed to use ordered handling. You probably want SpawnPointSystem to be the last handler.
/// </summary>
[PublicAPI]
public sealed class PlayerSpawningEvent : EntityEventArgs
{
    /// <summary>
    /// The entity spawned, if any. You should set this if you succeed at spawning the character, and leave it alone if it's not null.
    /// </summary>
    public EntityUid? SpawnResult;
    /// <summary>
    /// The job to use, if any.
    /// </summary>
    public readonly ProtoId<JobPrototype>? Job;
    /// <summary>
    /// The profile to use, if any.
    /// </summary>
    public readonly HumanoidCharacterProfile? HumanoidCharacterProfile;
    /// <summary>
    /// The target station, if any.
    /// </summary>
    public readonly EntityUid? Station;
    /// <summary>
    /// Delta-V: Desired SpawnPointType, if any.
    /// </summary>
    public readonly SpawnPointType DesiredSpawnPointType;
    /// <summary>
    /// Frontier: The session associated with the entity, if any.
    /// </summary>
    public readonly ICommonSession? Session;

    public PlayerSpawningEvent(ProtoId<JobPrototype>? job, HumanoidCharacterProfile? humanoidCharacterProfile, EntityUid? station, SpawnPointType spawnPointType = SpawnPointType.Unset, ICommonSession? session = null) // Frontier: added session
    {
        Job = job;
        HumanoidCharacterProfile = humanoidCharacterProfile;
        Station = station;
        DesiredSpawnPointType = spawnPointType;
        Session = session; // Frontier
    }
}
