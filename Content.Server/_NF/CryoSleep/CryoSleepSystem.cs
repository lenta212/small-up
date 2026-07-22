using System.Numerics;
using Content.Server.DoAfter;
using Content.Server.EUI;
using Content.Server.GameTicking;
using Content.Server.Interaction;
using Content.Server.Mind;
using Content.Server.Popups;
using Content.Server._NF.Shipyard.Systems;
using Content.Server.Radio.EntitySystems;
using Content.Server.Roles.Jobs;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared.ActionBlocker;
using Content.Shared.Bed.Sleep;
using Content.Shared.Chat;
using Content.Shared.Climbing.Systems;
using Content.Shared._NF.CryoSleep;
using Content.Shared.Destructible;
using Content.Shared.DoAfter;
using Content.Shared.DragDrop;
using Content.Shared.Examine;
using Content.Shared.GameTicking;
using Content.Shared.Interaction;
using Content.Shared.Ghost;
using Content.Shared.Interaction.Events;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared._NF.CCVar;
using Content.Shared.Popups;
using Content.Shared.Radio;
using Content.Shared.Station.Components;
using Content.Shared.Verbs;
using Robust.Server.Containers;
using Robust.Server.Player;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Content.Server.Ghost;
using Content.Server.Ghost.Components;
using Content.Shared.Roles;
using Content.Server._NF.Shuttles.Components;
using Content.Server._LuaM.Cryo;

namespace Content.Server._NF.CryoSleep;

public sealed partial class CryoSleepSystem : SharedCryoSleepSystem
{
    [Dependency] private EntityManager _entityManager = default!;
    [Dependency] private ActionBlockerSystem _actionBlocker = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private ContainerSystem _container = default!;
    [Dependency] private ClimbSystem _climb = default!;
    [Dependency] private GameTicker _gameTicker = default!;
    [Dependency] private IMapManager _mapManager = default!;
    [Dependency] private EuiManager _euiManager = null!;
    [Dependency] private MindSystem _mind = default!;
    [Dependency] private InteractionSystem _interaction = default!;
    [Dependency] private DoAfterSystem _doAfter = default!;
    [Dependency] private MobStateSystem _mobSystem = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private ShipyardSystem _shipyard = default!; // For the FoundOrganics method
    [Dependency] private GhostSystem _ghost = default!;
    [Dependency] private RadioSystem _radioSystem = default!;
    [Dependency] private IPrototypeManager _prototypeManager = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IConfigurationManager _configurationManager = default!;
    [Dependency] private JobSystem _jobs = default!;
    [Dependency] private StationJobsSystem _stationJobs = default!;
    [Dependency] private StationSystem _station = default!;
    [Dependency] private LuaMDeepCryoPersistenceSystem _deepCryo = default!;

    private readonly Dictionary<NetUserId, StoredBody?> _storedBodies = new();
    private EntityUid? _storageMap;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CryoSleepComponent, ComponentStartup>(OnInit);
        SubscribeLocalEvent<CryoSleepComponent, GetVerbsEvent<InteractionVerb>>(AddInsertOtherVerb);
        SubscribeLocalEvent<CryoSleepComponent, GetVerbsEvent<AlternativeVerb>>(AddAlternativeVerbs);
        SubscribeLocalEvent<CryoSleepComponent, InteractHandEvent>(OnInteractHand);
        SubscribeLocalEvent<CryoSleepComponent, SuicideEvent>(OnSuicide);
        SubscribeLocalEvent<CryoSleepComponent, ExaminedEvent>(OnExamine);
        SubscribeLocalEvent<CryoSleepComponent, DestructionEventArgs>((e,c,_) => EjectBody(e, c));
        SubscribeLocalEvent<CryoSleepComponent, CryoStoreDoAfterEvent>(OnAutoCryoSleep);
        SubscribeLocalEvent<CryoSleepComponent, DragDropTargetEvent>(OnEntityDragDropped);
        SubscribeLocalEvent<RoundEndedEvent>(OnRoundEnded);
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
        SubscribeLocalEvent<RoleAddedEvent>(OnRoleAdded);

        InitReturning();
    }

    private Vector2 _cryoCoords = Vector2.Zero; // Mono - Initial cryo body location in the cryo map.
    private readonly Vector2 _cryoDistance = Vector2.Create(0, 1); // Mono - Amount to increment body location after each cryo.
    private EntityUid GetStorageMap()
    {
        if (Deleted(_storageMap))
        {
            var map = _mapManager.CreateMap();
            _storageMap = _mapManager.GetMapEntityId(map);
            _mapManager.SetMapPaused(map, true);
        }

        return _storageMap.Value;
    }

    private void OnInit(EntityUid uid, CryoSleepComponent component, ComponentStartup args)
    {
        component.BodyContainer = _container.EnsureContainer<ContainerSlot>(uid, "body_container");
    }

    private void AddInsertOtherVerb(EntityUid uid, CryoSleepComponent component, GetVerbsEvent<InteractionVerb> args)
    {
        if (!args.CanAccess)
            return;

        // If the user is currently holding/pulling an entity that can be cryo-sleeped, add a verb for that.
        if (args.CanInteract &&
            args.Using is { Valid: true } @using &&
            !IsOccupied(component) &&
            _interaction.InRangeUnobstructed(@using, args.Target) &&
            _actionBlocker.CanMove(@using) &&
            HasComp<MindContainerComponent>(@using))
        {
            var name = "Unknown";
            if (TryComp<MetaDataComponent>(args.Using.Value, out var metadata))
                name = metadata.EntityName;

            InteractionVerb verb = new()
            {
                Act = () => InsertBody(@using, component, false),
                Category = VerbCategory.Insert,
                Text = name
            };
            args.Verbs.Add(verb);
        }

        if (CanSelfEnterCryo(args.User, component, args.CanInteract))
        {
            InteractionVerb verb = new()
            {
                Act = () => InsertBody(args.User, component, false),
                Category = VerbCategory.Insert,
                Text = Loc.GetString("medical-scanner-verb-enter"),
                Priority = 1
            };
            args.Verbs.Add(verb);
        }
    }

    private void AddAlternativeVerbs(EntityUid uid, CryoSleepComponent component, GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess)
            return;

        // Eject verb
        if (args.CanInteract && IsOccupied(component))
        {
            AlternativeVerb verb = new()
            {
                Act = () => EjectBody(uid, component),
                Category = VerbCategory.Eject,
                Text = Loc.GetString("medical-scanner-verb-noun-occupant")
            };
            args.Verbs.Add(verb);
        }

        // Self-insert verb
        if (CanSelfEnterCryo(args.User, component, args.CanInteract))
        {
            AlternativeVerb verb = new()
            {
                Act = () => InsertBody(args.User, component, false),
                Category = VerbCategory.Insert,
                Text = Loc.GetString("medical-scanner-verb-enter"),
                Priority = 1
            };
            args.Verbs.Add(verb);
        }
    }

    private bool CanSelfEnterCryo(EntityUid user, CryoSleepComponent component, bool canInteract)
    {
        if (IsOccupied(component))
            return false;

        if (!canInteract && !HasComp<SleepingComponent>(user))
            return false;

        if (!TryComp<MobStateComponent>(user, out var mob) || !_mobSystem.IsAlive(user, mob))
            return false;

        return HasComp<MindContainerComponent>(user);
    }

    private void OnInteractHand(EntityUid uid, CryoSleepComponent component, InteractHandEvent args)
    {
        if (args.Handled || !CanSelfEnterCryo(args.User, component, true))
            return;

        if (InsertBody(args.User, component, false))
            args.Handled = true;
    }

    private void OnSuicide(EntityUid uid, CryoSleepComponent component, SuicideEvent args)
    {
        if (args.Handled)
            return;

        if (args.Victim != component.BodyContainer.ContainedEntity)
            return;

        QueueDel(args.Victim);
        _audio.PlayPvs(component.LeaveSound, uid);
        args.Handled = true;
    }

    private void OnExamine(EntityUid uid, CryoSleepComponent component, ExaminedEvent args)
    {
        var message = component.BodyContainer.ContainedEntity == null
            ? "cryopod-examine-empty"
            : "cryopod-examine-occupied";

        args.PushMarkup(Loc.GetString(message));
    }

    private void OnAutoCryoSleep(EntityUid uid, CryoSleepComponent component, CryoStoreDoAfterEvent args)
    {
        if (args.Cancelled || args.Handled)
        {
            ClearCryoStoreDoAfter(component);
            return;
        }

        var pod = args.Used;
        var body = args.Target;
        if (body is not { Valid: true } || pod is not { Valid: true })
        {
            ClearCryoStoreDoAfter(component);
            return;
        }

        args.Handled = true;
        ClearCryoStoreDoAfter(component);
        // Let SharedDoAfterSystem finish and remove its pod/body references before
        // the DB-first snapshot walks the entity graph.
        Timer.Spawn(TimeSpan.Zero, () =>
        {
            if (Exists(body.Value) && Exists(pod.Value))
                CryoStoreBody(body.Value, pod.Value);
        });
    }

    private void OnEntityDragDropped(EntityUid uid, CryoSleepComponent component, DragDropTargetEvent args)
    {
        if (InsertBody(args.Dragged, component, false))
        {
            args.Handled = true;
        }
    }

    public bool InsertBody(EntityUid? toInsert, CryoSleepComponent component, bool force)
    {
        var cryopod = component.Owner;
        if (toInsert == null)
            return false;
        if (IsOccupied(component) && !force)
            return false;

        var mobQuery = GetEntityQuery<MobStateComponent>();
        var xformQuery = GetEntityQuery<TransformComponent>();
        // Refuse to accept "passengers" (e.g. pet felinids in bags)
        string? name = _shipyard.FoundOrganics(toInsert.Value, mobQuery, xformQuery);
        if (name is not null)
        {
            _popup.PopupEntity(Loc.GetString("cryopod-refuse-organic", ("cryopod", cryopod), ("name", name)), cryopod, PopupType.SmallCaution);
            return false;
        }

        // Refuse to accept dead or crit bodies, as well as non-mobs
        if (!TryComp<MobStateComponent>(toInsert, out var mob) || !_mobSystem.IsAlive(toInsert.Value, mob))
        {
            _popup.PopupEntity(Loc.GetString("cryopod-refuse-dead", ("cryopod", cryopod)), cryopod, PopupType.SmallCaution);
            return false;
        }

        var success = _container.Insert(toInsert.Value, component.BodyContainer);
        if (!success)
            return false;

        if (_player.TryGetSessionByEntity(toInsert.Value, out var session) &&
            session.Status != SessionStatus.Disconnected)
        {
            _euiManager.OpenEui(new CryoSleepEui(toInsert.Value, cryopod, this), session);
            StartAutomaticCryoStore(toInsert.Value, cryopod, component);
        }
        else
        {
            CryoStoreBody(toInsert.Value, cryopod);
        }

        return true;
    }

    private void StartAutomaticCryoStore(EntityUid body, EntityUid cryopod, CryoSleepComponent component)
    {
        if (component.CryosleepDoAfter is { } existingDoAfter)
        {
            if (_doAfter.GetStatus(existingDoAfter) == DoAfterStatus.Running)
                return;

            ClearCryoStoreDoAfter(component);
        }

        var ev = new CryoStoreDoAfterEvent();
        var args = new DoAfterArgs(
            _entityManager,
            body,
            TimeSpan.FromSeconds(30),
            ev,
            cryopod,
            body,
            cryopod)
        {
            BreakOnMove = true,
            BreakOnWeightlessMove = true,
        };

        if (_doAfter.TryStartDoAfter(args))
        {
            component.CryosleepDoAfter = ev.DoAfter.Id;
        }
        else
        {
            ClearCryoStoreDoAfter(component);
        }
    }

    private void ClearCryoStoreDoAfter(CryoSleepComponent component)
    {
        component.CryosleepDoAfter = null;
    }

    private void CancelCryoStoreDoAfter(CryoSleepComponent component)
    {
        if (component.CryosleepDoAfter is not { } doAfter)
            return;

        if (_doAfter.GetStatus(doAfter) == DoAfterStatus.Running)
            _doAfter.Cancel(doAfter);

        ClearCryoStoreDoAfter(component);
    }

    public void CryoStoreBody(EntityUid bodyId, EntityUid cryopod)
    {
        // Manual confirmation and the 30-second fallback may race. The first
        // durable store owns the body; a duplicate must not unghost or eject it.
        if (_deepCryo.IsStorePending(bodyId))
            return;

        if (!TryComp<CryoSleepComponent>(cryopod, out var cryo) ||
            cryo.BodyContainer.ContainedEntity != bodyId)
            return;

        NetUserId? userId = null;
        if (_deepCryo.TryGetIdentity(bodyId, out var stableUserId))
            userId = stableUserId;
        else if (_mind.TryGetMind(bodyId, out _, out var mind))
            userId = mind.UserId;

        if (!TryStartImmediateCryoGhost(bodyId, out var temporaryGhostVisit))
        {
            Log.Error($"Refused deep-cryo store for {ToPrettyString(bodyId)}: ghost-spawn-failed");
            EjectBody(cryopod, body: bodyId);
            return;
        }

        if (!_deepCryo.TryBeginStore(
                bodyId,
                cryopod,
                userId,
                LuaMDeepCryoSource.FrontierCryoSleep,
                completion =>
                {
                    if (!completion.Success)
                    {
                        Log.Error($"Deep-cryo store failed for {ToPrettyString(bodyId)}: {completion.FailureReason}");
                        RollbackImmediateCryoGhost(bodyId, cryopod, temporaryGhostVisit);
                        return;
                    }

                    if (!Exists(bodyId) || !Exists(cryopod) ||
                        !TryComp<CryoSleepComponent>(cryopod, out var currentCryo) ||
                        currentCryo.BodyContainer.ContainedEntity != bodyId)
                    {
                        Log.Error($"Deep-cryo body/pod changed after durable store for {bodyId}.");
                        return;
                    }

                    FinalizeCryoStoreBody(bodyId, cryopod, currentCryo);
                },
                out var reason))
        {
            Log.Error($"Refused deep-cryo store for {ToPrettyString(bodyId)}: {reason}");
            RollbackImmediateCryoGhost(bodyId, cryopod, temporaryGhostVisit);
        }
    }

    private bool TryStartImmediateCryoGhost(
        EntityUid bodyId,
        out (EntityUid Mind, EntityUid Ghost)? temporaryVisit)
    {
        temporaryVisit = null;
        if (!_mind.TryGetMind(bodyId, out var mindId, out var mind) || mind.VisitingEntity != null)
            return true;

        if (_ghost.SpawnGhost((mindId, mind), bodyId, canReturn: true) is { } ghost)
        {
            temporaryVisit = (mindId, ghost);
            return true;
        }

        // SpawnGhost detaches the mind when no valid observer position exists.
        // Restore control before returning the body to the player.
        if (Exists(bodyId) && mind.OwnedEntity != bodyId)
            _mind.TransferTo(mindId, bodyId, createGhost: false, mind: mind);

        return false;
    }

    private void RollbackImmediateCryoGhost(
        EntityUid bodyId,
        EntityUid cryopod,
        (EntityUid Mind, EntityUid Ghost)? temporaryVisit)
    {
        if (temporaryVisit is { } visit &&
            TryComp<MindComponent>(visit.Mind, out var mind) &&
            mind.OwnedEntity == bodyId &&
            mind.VisitingEntity == visit.Ghost)
        {
            _mind.UnVisit(visit.Mind, mind);
        }

        EjectBody(cryopod, body: bodyId);
    }

    private void FinalizeCryoStoreBody(EntityUid bodyId, EntityUid cryopod, CryoSleepComponent cryo)
    {

        NetUserId? id = null;
        var characterName = "Unknown";
        string? jobTitle = null;

        if (_mind.TryGetMind(bodyId, out var mindEntity, out var mind) && mind.OwnedEntity == bodyId)
        {
            var argMind = mind;
            RaiseLocalEvent(bodyId, new CryosleepBeforeMindRemovedEvent(cryopod, argMind?.UserId), true);

            if (mind.VisitingEntity is { } ghost && TryComp<GhostComponent>(ghost, out var ghostComponent))
            {
                _ghost.SetCanReturnToBody(ghost, false, ghostComponent);
                _mind.TransferTo(mindEntity, ghost, mind: mind);
            }
            else
            {
                _ghost.OnGhostAttempt(mindEntity, false, true, mind: mind);
            }

            id = mind.UserId;
            if (id != null)
            {
                _storedBodies[id.Value] = new StoredBody() { Body = bodyId, Cryopod = cryopod };

                // Get the player's current job prototype, first from mind, then from component
                string? currentJobPrototype = null;

                // Try to get the job from the mind first
                if (_jobs.MindTryGetJobId(mindEntity, out var jobId) && jobId != null)
                {
                    currentJobPrototype = jobId;
                }
                // If no job in mind (e.g. disconnected player), try to get from the entity component
                else if (TryComp<PlayerJobComponent>(bodyId, out var playerJob) && playerJob.JobPrototype != null)
                {
                    currentJobPrototype = playerJob.JobPrototype;
                }

                // Only reopen the job slot for the player's current job
                if (currentJobPrototype != null)
                {
                    // Get the spawn station from the player job component
                    EntityUid? playerStation = null;
                    if (TryComp<PlayerJobComponent>(bodyId, out var playerJob))
                    {
                        playerStation = playerJob.SpawnStation;
                    }

                    // Check if any of the station's grids have ForceAnchor
                    bool stationHasForceAnchor = false;

                    if (playerStation != null && EntityManager.EntityExists(playerStation.Value) &&
                        _entityManager.TryGetComponent<StationDataComponent>(playerStation.Value, out var stationData))
                    {
                        foreach (var gridUid in stationData.Grids)
                        {
                            if (HasComp<ForceAnchorComponent>(gridUid))
                            {
                                stationHasForceAnchor = true;
                                //Log.Info($"Found ForceAnchor on grid {ToPrettyString(gridUid)} for station {ToPrettyString(playerStation.Value)}");
                                break;
                            }
                        }
                        //Log.Info($"Station {ToPrettyString(playerStation.Value)} has ForceAnchor: {stationHasForceAnchor} (checked {stationData.Grids.Count} grids)");
                    }
                    else
                    {
                        //Log.Info($"Could not get StationDataComponent for station {(playerStation != null ? ToPrettyString(playerStation.Value) : "null")}");
                    }

                    // Only proceed if we found a valid station for this player and it has ForceAnchor
                    if (playerStation != null && EntityManager.EntityExists(playerStation.Value) &&
                        _entityManager.TryGetComponent<StationJobsComponent>(playerStation.Value, out var stationJobs) &&
                        stationHasForceAnchor)
                    {
                        // For connected players, we check their job assignments
                        if (id != null && _stationJobs.TryGetPlayerJobs(playerStation.Value, id.Value, out var jobs, stationJobs))
                        {
                            // Only adjust the slot for their current job - increasing the available slots by 1
                            if (jobs.Contains(currentJobPrototype))
                            {
                                _stationJobs.TryAdjustJobSlot(playerStation.Value, currentJobPrototype, 1, clamp: true);
                                Log.Debug($"Reopened job slot '{currentJobPrototype}' on station {ToPrettyString(playerStation.Value)} after {characterName} entered cryosleep");
                            }

                            // Still need to remove the player from all job assignments
                            _stationJobs.TryRemovePlayerJobs(playerStation.Value, id.Value, stationJobs);
                        }
                        // For disconnected players or other cases, we just try to reopen the job slot directly
                        else
                        {
                            _stationJobs.TryAdjustJobSlot(playerStation.Value, currentJobPrototype, 1, clamp: true, createSlot: true);
                            Log.Debug($"Reopened job slot '{currentJobPrototype}' on station {ToPrettyString(playerStation.Value)} after {characterName} entered cryosleep (direct adjustment)");
                        }
                    }
                }
            }

            if (mind.CharacterName != null)
                characterName = mind.CharacterName;

            // Get the job title if available
            jobTitle = _jobs.MindTryGetJobName(mindEntity);
        }
        else if (TryComp<MetaDataComponent>(bodyId, out var metadata))
        {
            characterName = metadata.EntityName;
        }

        var storage = GetStorageMap();
        var bodyTransform = Transform(bodyId);
        _container.Remove(bodyId, cryo.BodyContainer, reparent: false, force: true);
        bodyTransform.Coordinates = new EntityCoordinates(storage, _cryoCoords); // Mono, replaced Vector.Zero with _cryoCoords. Sets body to this location on cryomap.
        _cryoCoords = Vector2.Add(_cryoCoords, _cryoDistance); // Mono - Increments for next body.

        RaiseLocalEvent(bodyId, new CryosleepEnterEvent(cryopod, mind?.UserId), true);

        CancelCryoStoreDoAfter(cryo);

        // Get the pod's location information for the radio message
        string message;
        var podTransform = Transform(cryopod);
        var coordinates = _entityManager.GetComponent<TransformComponent>(cryopod).Coordinates;
        var mapPos = coordinates.ToMap(_entityManager, EntityManager.System<SharedTransformSystem>());

        // Check if it's at a named location (like a station or outpost)
        if (podTransform.GridUid != null && _entityManager.TryGetComponent<MetaDataComponent>(podTransform.GridUid.Value, out var gridMetadata))
        {
            message = Loc.GetString("cryopod-radio-location",
                ("character", characterName),
                ("location", gridMetadata.EntityName)); // Mono: They don't tell coords now
        }
        else
        {
            // If not at a named location, use coordinates
            message = Loc.GetString("cryopod-radio-coordinates",
                ("character", characterName),
                ("x", Math.Round(mapPos.Position.X)),
                ("y", Math.Round(mapPos.Position.Y)));
        }

        // Check if character is a pirate, and if so, use Freelancer radio instead of Common
        bool isPirate = false;
        if (jobTitle != null)
        {
            // Check if job is one of the pirate jobs
            isPirate = jobTitle.Equals(Loc.GetString("job-name-pirate"), StringComparison.OrdinalIgnoreCase) ||
                       jobTitle.Equals(Loc.GetString("job-name-pirate-captain"), StringComparison.OrdinalIgnoreCase) ||
                       jobTitle.Equals(Loc.GetString("job-name-pirate-first-mate"), StringComparison.OrdinalIgnoreCase);
        }

        // Check if character is TSF, and if so, use TSF radio instead of Common
        bool isTSF = false;
        if (jobTitle != null)
        {
            isTSF = jobTitle.Equals(Loc.GetString("job-name-bailiff"), StringComparison.OrdinalIgnoreCase) ||
                    jobTitle.Equals(Loc.GetString("job-name-brigmedic"), StringComparison.OrdinalIgnoreCase) ||
                    jobTitle.Equals(Loc.GetString("job-name-cadet-nf"), StringComparison.OrdinalIgnoreCase) ||
                    jobTitle.Equals(Loc.GetString("job-name-deputy"), StringComparison.OrdinalIgnoreCase) ||
                    jobTitle.Equals(Loc.GetString("job-name-nf-detective"), StringComparison.OrdinalIgnoreCase) ||
                    jobTitle.Equals(Loc.GetString("job-name-sheriff"), StringComparison.OrdinalIgnoreCase) ||
                    jobTitle.Equals(Loc.GetString("job-name-stc"), StringComparison.OrdinalIgnoreCase) ||
                    jobTitle.Equals(Loc.GetString("job-name-sr"), StringComparison.OrdinalIgnoreCase) ||
                    jobTitle.Equals(Loc.GetString("job-name-pal"), StringComparison.OrdinalIgnoreCase);
        }

        // Send radio message on appropriate channel
        if (isPirate)
        {
            // Use Freelancer channel for pirates
            if (_prototypeManager.TryIndex<RadioChannelPrototype>("Freelance", out var freelanceChannel))
            {
                _radioSystem.SendRadioMessage(cryopod, message, freelanceChannel, cryopod);
            }
        }
        else if (isTSF)
        {
            // Use TSF channel for TSF - Mono
            if (_prototypeManager.TryIndex<RadioChannelPrototype>("Nfsd", out var nfsdChannel))
            {
                _radioSystem.SendRadioMessage(cryopod, message, nfsdChannel, cryopod);
            }
        }
        else
        {
            // Use Common channel for everyone else
            if (_prototypeManager.TryIndex<RadioChannelPrototype>(SharedChatSystem.CommonChannel, out var commonChannel))
            {
                _radioSystem.SendRadioMessage(cryopod, message, commonChannel, cryopod);
            }
        }

        // Start a timer. When it ends, the body needs to be deleted.
        Timer.Spawn(TimeSpan.FromSeconds(_configurationManager.GetCVar(NFCCVars.CryoExpirationTime)), () =>
        {
            if (id != null)
                ResetCryosleepState(id.Value);

            if (!Deleted(bodyId) && Transform(bodyId).ParentUid == _storageMap)
                QueueDel(bodyId);
        });
    }

    /// <param name="body">If not null, will not eject if the stored body is different from that parameter.</param>
    public bool EjectBody(EntityUid pod, CryoSleepComponent? component = null, EntityUid? body = null)
    {
        if (!Resolve(pod, ref component))
            return false;

        if (!IsOccupied(component) || (body != null && component.BodyContainer.ContainedEntity != body))
            return false;

        var toEject = component.BodyContainer.ContainedEntity;
        if (toEject == null)
            return false;

        // Once serialization has started, ejecting would invalidate the body that
        // is being committed. Failure clears the fence and leaves it in the pod.
        if (_deepCryo.IsStorePending(toEject.Value))
            return false;

        _container.Remove(toEject.Value, component.BodyContainer, force: true);
        //_climb.ForciblySetClimbing(toEject.Value, pod);

        CancelCryoStoreDoAfter(component);

        return true;
    }

    private bool IsOccupied(CryoSleepComponent component)
    {
        return component.BodyContainer.ContainedEntity != null ||
               HasComp<LuaMDeepCryoRestoreReservationComponent>(component.Owner);
    }

    private void OnRoundEnded(RoundEndedEvent args)
    {
        _storedBodies.Clear();
    }

    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent ev)
    {
        // Store the job prototype and spawn station on the player's entity
        if (!string.IsNullOrEmpty(ev.JobId))
        {
            var jobComp = EnsureComp<PlayerJobComponent>(ev.Mob);
            jobComp.JobPrototype = ev.JobId;
            jobComp.SpawnStation = ev.Station;
            Log.Debug($"Stored job '{ev.JobId}' on station {ToPrettyString(ev.Station)} for player {MetaData(ev.Mob).EntityName}");
        }
    }

    private void OnRoleAdded(RoleAddedEvent args)
    {
        // In this event, we don't have direct access to the role information
        // We need to check if a JobRoleComponent was added to any of the mind's roles

        // Get the entity owned by this mind
        var mindEntity = args.MindId;
        if (!_player.TryGetSessionById(args.Mind.UserId, out var session) ||
            session.AttachedEntity is not { Valid: true } playerEntity)
            return;

        // Check if this mind has a job role
        if (_jobs.MindTryGetJobId(mindEntity, out var jobId) && jobId != null)
        {
            // Get the existing component if it exists
            EntityUid? spawnStation = null;
            if (TryComp<PlayerJobComponent>(playerEntity, out var existingJob))
            {
                // Preserve the original spawn station
                spawnStation = existingJob.SpawnStation;
            }

            // Update the PlayerJobComponent with the job ID while preserving station
            var jobComp = EnsureComp<PlayerJobComponent>(playerEntity);
            jobComp.JobPrototype = jobId;

            // Only set the station if we didn't have one before
            if (spawnStation != null && jobComp.SpawnStation == null)
            {
                jobComp.SpawnStation = spawnStation;
            }
        }
    }

    private struct StoredBody
    {
        public EntityUid Body;
        public EntityUid Cryopod;
    }
}

