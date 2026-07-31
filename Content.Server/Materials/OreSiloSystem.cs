using System.Linq;
using Content.Server.Pinpointer;
using Content.Shared.IdentityManagement;
using Content.Shared.Materials.OreSilo;
using Robust.Server.GameStates;
using Robust.Shared.EntitySerialization;
using Robust.Shared.Map;
using Robust.Shared.Map.Events;
using Robust.Shared.Player;

namespace Content.Server.Materials;

/// <inheritdoc/>
public sealed partial class OreSiloSystem : SharedOreSiloSystem
{
    [Dependency] private EntityLookupSystem _entityLookup = default!;
    [Dependency] private NavMapSystem _navMap = default!;
    [Dependency] private SharedUserInterfaceSystem _userInterface = default!;

    private const float OreSiloPreloadRangeSquared = 225f; // ~1 screen

    private readonly HashSet<Entity<OreSiloClientComponent>> _clientLookup = new();
    private readonly HashSet<(NetEntity, string, string)> _clientInformation = new();
    private readonly HashSet<EntityUid> _silosToAdd = new();
    private readonly HashSet<EntityUid> _staleClients = new();
    private readonly Dictionary<ICommonSession, HashSet<EntityUid>> _cachedSilosBySession = new();
    private readonly Dictionary<ICommonSession, EntityUid> _cachedActorBySession = new();
    private readonly HashSet<ICommonSession> _activeSiloSessions = new();

    // Mono
    private readonly List<Entity<OreSiloClientComponent>> _silos = new();

    // Mono
    private float _updateAccumulator = 0f;
    private float _updateInterval = 1f;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<BeforeSerializationEvent>(OnBeforeSerialization);
        SubscribeLocalEvent<ActorComponent, ExpandPvsEvent>(OnExpandPvs);
    }

    private void OnBeforeSerialization(BeforeSerializationEvent ev)
    {
        if (ev.Category == FileCategory.Entity)
            return;

        ReconcileSiloLinks(ev.MapIds);
    }

    private void OnExpandPvs(Entity<ActorComponent> ent, ref ExpandPvsEvent args)
    {
        // Keep this projection transient. Persistent session overrides have no
        // ownership tracking, so removing one can clobber another system's entry.
        if (ent.Comp.PlayerSession != args.Session ||
            !_cachedActorBySession.TryGetValue(args.Session, out var cachedActor) ||
            cachedActor != ent.Owner ||
            !_cachedSilosBySession.TryGetValue(args.Session, out var cachedSilos))
        {
            return;
        }

        args.RecursiveEntities ??= new List<EntityUid>(cachedSilos.Count);
        foreach (var siloUid in cachedSilos)
        {
            if (Exists(siloUid) && !args.RecursiveEntities.Contains(siloUid))
                args.RecursiveEntities.Add(siloUid);
        }
    }

    protected override void UpdateOreSiloUi(Entity<OreSiloComponent> ent)
    {
        if (!_userInterface.IsUiOpen(ent.Owner, OreSiloUiKey.Key))
            return;
        _clientLookup.Clear();
        _clientInformation.Clear();

        var xform = Transform(ent);

        // Sneakily uses override with TComponent parameter
        _entityLookup.GetEntitiesInRange(xform.Coordinates, ent.Comp.Range, _clientLookup);

        foreach (var client in _clientLookup)
        {
            // don't show already-linked clients.
            if (client.Comp.Silo is not null)
                continue;

            // Don't show clients on the screen if we can't link them.
            if (!CanTransmitMaterials((ent, ent, xform), client))
                continue;

            var netEnt = GetNetEntity(client);
            var name = Identity.Name(client, EntityManager);
            var beacon = _navMap.GetNearestBeaconString(client.Owner, onlyName: true);
            var inRange = CanTransmitMaterials((ent, ent, xform), client);

            var txt = Loc.GetString("ore-silo-ui-itemlist-entry",
                ("name", name),
                ("beacon", beacon),
                ("linked", ent.Comp.Clients.Contains(client)),
                ("inRange", true));

            _clientInformation.Add((netEnt, txt, beacon));
        }

        // Get all clients of this silo, including those out of range.
        foreach (var client in ent.Comp.Clients)
        {
            if (!TryComp<OreSiloClientComponent>(client, out var clientComp) ||
                clientComp.Silo != ent.Owner)
            {
                continue;
            }

            var netEnt = GetNetEntity(client);
            var name = Identity.Name(client, EntityManager);
            var beacon = _navMap.GetNearestBeaconString(client, onlyName: true);
            var inRange = CanTransmitMaterials((ent, ent), client);

            var txt = Loc.GetString("ore-silo-ui-itemlist-entry",
                ("name", name),
                ("beacon", beacon),
                ("linked", ent.Comp.Clients.Contains(client)),
                ("inRange", inRange));

            _clientInformation.Add((netEnt, txt, beacon));
        }

        _userInterface.SetUiState(ent.Owner, OreSiloUiKey.Key, new OreSiloBuiState(_clientInformation));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _updateAccumulator += frameTime;
        if (_updateAccumulator < _updateInterval)
            return;
        _updateAccumulator -= _updateInterval;

        ReconcileSiloLinks();

        // Solving an annoying problem: we need to send the silo to people who are near the silo so that
        // Things don't start wildly mispredicting. We do this as cheaply as possible via grid-based local-pos checks.
        // Sloth okay-ed this in the interim until a better solution comes around.

        _silos.Clear();
        var clientQuery = EntityQueryEnumerator<OreSiloClientComponent>();
        while (clientQuery.MoveNext(out var uid, out var siloComp))
        {
            _silos.Add((uid, siloComp));
        }

        var actorQuery = EntityQueryEnumerator<ActorComponent>();
        while (actorQuery.MoveNext(out var actorUid, out var actorComp))
        {
            if (actorComp.PlayerSession is not { } session)
                continue;

            _silosToAdd.Clear();
            _activeSiloSessions.Add(session);

            var actorXform = Transform(actorUid);

            foreach (var (uid, clientComp) in _silos)
            {
                if (!TryGetValidSiloLink(uid, clientComp, out var siloUid, out _))
                    continue;

                var clientXform = Transform(uid);
                // We limit it to same-grid checks only for peak perf
                if (actorXform.GridUid != clientXform.GridUid)
                    continue;

                if ((actorXform.LocalPosition - clientXform.LocalPosition).LengthSquared() <= OreSiloPreloadRangeSquared)
                    _silosToAdd.Add(siloUid);
            }

            if (_silosToAdd.Count == 0)
            {
                _cachedSilosBySession.Remove(session);
                _cachedActorBySession.Remove(session);
            }
            else
            {
                if (!_cachedSilosBySession.TryGetValue(session, out var cachedSilos))
                {
                    cachedSilos = new HashSet<EntityUid>();
                    _cachedSilosBySession.Add(session, cachedSilos);
                }

                cachedSilos.Clear();
                cachedSilos.UnionWith(_silosToAdd);
                _cachedActorBySession[session] = actorUid;
            }
        }

        foreach (var inactive in _cachedSilosBySession.Keys
                     .Where(session => !_activeSiloSessions.Contains(session))
                     .ToArray())
        {
            _cachedSilosBySession.Remove(inactive);
            _cachedActorBySession.Remove(inactive);
        }

        _activeSiloSessions.Clear();
    }

    /// <summary>
    /// Keeps the two persisted sides of an ore-silo link consistent before the
    /// PVS preload pass consumes them. Map deserialization represents a missing
    /// referenced entity as a non-null <see cref="EntityUid.Invalid"/>; treating
    /// that value as a live silo caused a permanent PVS error loop after a ship
    /// with a stale link was restored.
    /// </summary>
    private void ReconcileSiloLinks(IReadOnlySet<MapId>? mapIds = null)
    {
        // The client pointer is authoritative. Repair its reciprocal entry when
        // valid, otherwise clear it before serialization or PVS can consume it.
        var clientQuery = EntityQueryEnumerator<OreSiloClientComponent>();
        while (clientQuery.MoveNext(out var clientUid, out var clientComp))
        {
            if (!IsInScope(clientUid, mapIds) || clientComp.Silo is not { })
                continue;

            if (!TryGetValidSiloLink(clientUid, clientComp, out var siloUid, out var siloComp, false))
            {
                clientComp.Silo = null;
                Dirty(clientUid, clientComp);
                continue;
            }

            if (siloComp.Clients.Add(clientUid))
                Dirty(siloUid, siloComp);
        }

        // Prune the derived reverse index. Never recreate a client pointer from
        // this set: more than one stale silo can claim the same client.
        var siloQuery = EntityQueryEnumerator<OreSiloComponent>();
        while (siloQuery.MoveNext(out var siloUid, out var siloComp))
        {
            if (!IsInScope(siloUid, mapIds))
                continue;

            _staleClients.Clear();
            foreach (var clientUid in siloComp.Clients)
            {
                if (!TryComp<OreSiloClientComponent>(clientUid, out var clientComp) ||
                    clientComp.Silo != siloUid ||
                    !OnSameGrid(clientUid, siloUid))
                {
                    _staleClients.Add(clientUid);
                }
            }

            if (_staleClients.Count == 0)
                continue;

            siloComp.Clients.ExceptWith(_staleClients);
            Dirty(siloUid, siloComp);
        }
    }

    private bool TryGetValidSiloLink(
        EntityUid clientUid,
        OreSiloClientComponent clientComp,
        out EntityUid siloUid,
        out OreSiloComponent siloComp,
        bool requireReciprocal = true)
    {
        siloUid = EntityUid.Invalid;
        siloComp = default!;
        if (clientComp.Silo is not { } candidate || !candidate.IsValid())
            return false;

        if (!TryComp<OreSiloComponent>(candidate, out var candidateComp) ||
            !OnSameGrid(clientUid, candidate) ||
            requireReciprocal && !candidateComp.Clients.Contains(clientUid))
            return false;

        siloUid = candidate;
        siloComp = candidateComp;
        return true;
    }

    private bool OnSameGrid(EntityUid first, EntityUid second)
        => TryComp<TransformComponent>(first, out var firstXform) &&
           TryComp<TransformComponent>(second, out var secondXform) &&
           firstXform.GridUid != null &&
           firstXform.GridUid == secondXform.GridUid;

    private bool IsInScope(EntityUid uid, IReadOnlySet<MapId>? mapIds)
        => mapIds == null ||
           TryComp<TransformComponent>(uid, out var xform) && mapIds.Contains(xform.MapID);
}
