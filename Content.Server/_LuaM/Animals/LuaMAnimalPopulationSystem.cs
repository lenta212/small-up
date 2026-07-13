using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Content.Server.Administration.Logs;
using Content.Server.Chat.Managers;
using Content.Server.NPC;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Shared.CCVar;
using Content.Shared.Database;
using Content.Shared.GameTicking;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.NameIdentifier;
using Content.Shared.Nutrition.AnimalHusbandry;
using Content.Shared.NPC.Components;
using Content.Shared.Prototypes;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._LuaM.Animals;

/// <summary>
/// Provides one population view for animal husbandry and the explicitly known
/// pest prototypes created by long-lived timed spawners and vent events.
/// </summary>
public sealed class LuaMAnimalPopulationSystem : EntitySystem
{
    public const int MinimumPestsPreservedPerSpecies = 2;
    public const float PlayerPetProtectionRadius = 5f;

    private static readonly TimeSpan AuditInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan WarningCooldown = TimeSpan.FromMinutes(10);

    private static readonly IReadOnlyDictionary<string, string> KnownPestSpecies =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MobMouse"] = "mouse",
            ["MobMouse1"] = "mouse",
            ["MobMouse2"] = "mouse",
            ["MobMouseCancer"] = "mouse",
            ["MobCockroach"] = "cockroach",
            ["MobMothroach"] = "mothroach",
            ["MobRosyMothroach"] = "mothroach",
            ["MobSnail"] = "snail",
            ["MobSnailSpeed"] = "snail",
            ["MobSnailMoth"] = "snail",
            ["MobSnailInstantDeath"] = "snail",
        };

    [Dependency] private readonly IAdminLogManager _adminLog = default!;
    [Dependency] private readonly IChatManager _chat = default!;
    [Dependency] private readonly IConfigurationManager _configuration = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly SharedContainerSystem _containers = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private readonly Dictionary<MapId, TimeSpan> _nextWarningByMap = new();
    private TimeSpan _nextAudit;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestartCleanup);
        _nextAudit = _timing.CurTime + AuditInterval;
    }

    private void OnRoundRestartCleanup(RoundRestartCleanupEvent ev)
    {
        _nextWarningByMap.Clear();
        _nextAudit = _timing.CurTime + AuditInterval;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_timing.CurTime < _nextAudit)
            return;

        _nextAudit = _timing.CurTime + AuditInterval;
        WarnAboutOverflow();
    }

    public bool IsKnownPestPrototype(string prototypeId)
    {
        return KnownPestSpecies.ContainsKey(prototypeId);
    }

    public bool IsPopulationControlledPrototype(EntProtoId prototypeId)
    {
        return IsKnownPestPrototype(prototypeId) ||
               _prototypes.TryIndex<EntityPrototype>(prototypeId, out var prototype) &&
               prototype.HasComponent<ReproductivePartnerComponent>();
    }

    public int GetPopulationLimit()
    {
        return Math.Max(0, _configuration.GetCVar(CCVars.LuaMAnimalHusbandryMaxPopulationPerMap));
    }

    public int CountPopulationUnits(MapId mapId)
    {
        // Capacity checks run from breeding and spawner hot paths. They only need
        // the unique count, so avoid the more expensive player/pet safety audit.
        return CollectEntries(mapId, assessCleanupSafety: false).Count;
    }

    public int GetRemainingPopulationSlots(MapId mapId)
    {
        return Math.Max(0, GetPopulationLimit() - CountPopulationUnits(mapId));
    }

    public IReadOnlyList<LuaMAnimalPopulationReport> GetPopulationReports()
    {
        return CollectEntries()
            .GroupBy(entry => entry.MapId)
            .OrderBy(group => (int) group.Key)
            .Select(group => BuildReport(group))
            .ToList();
    }

    public LuaMAnimalPopulationReport GetPopulationReport(MapId mapId)
    {
        return BuildReport(CollectEntries(mapId).GroupBy(entry => entry.MapId).FirstOrDefault(), mapId);
    }

    public LuaMAnimalCleanupPreview BuildCleanupPreview(MapId mapId, int keep)
    {
        keep = Math.Max(0, keep);
        var entries = CollectEntries(mapId);
        var excess = Math.Max(0, entries.Count - keep);
        var candidates = new List<LuaMAnimalPopulationEntry>();

        if (excess > 0)
        {
            foreach (var speciesGroup in entries
                         .Where(entry => entry.KnownPest)
                         .GroupBy(entry => entry.Species, StringComparer.Ordinal)
                         .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                var removableForSpecies = Math.Max(0, speciesGroup.Count() - MinimumPestsPreservedPerSpecies);
                candidates.AddRange(speciesGroup
                    .Where(entry => entry.CleanupEligible)
                    .OrderBy(entry => entry.State == MobState.Dead ? 0 : 1)
                    .ThenByDescending(entry => entry.Uid.Id)
                    .Take(removableForSpecies));
            }
        }

        var selected = candidates
            .OrderBy(entry => entry.State == MobState.Dead ? 0 : 1)
            .ThenByDescending(entry => entry.Uid.Id)
            .Take(excess)
            .ToList();
        var fingerprint = BuildFingerprint(mapId, keep, entries, selected);

        return new LuaMAnimalCleanupPreview(
            mapId,
            keep,
            entries.Count,
            excess,
            entries.Count(entry => entry.CleanupEligible),
            selected.Select(entry => entry.Uid).ToList(),
            Math.Max(0, entries.Count - selected.Count),
            fingerprint);
    }

    public bool TryExecuteCleanup(
        MapId mapId,
        int keep,
        string fingerprint,
        string actor,
        out LuaMAnimalCleanupResult result,
        out string error)
    {
        result = default;
        error = string.Empty;

        var preview = BuildCleanupPreview(mapId, keep);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(preview.Fingerprint),
                Encoding.ASCII.GetBytes(fingerprint.Trim().ToLowerInvariant())))
        {
            error = "Population changed or the confirmation fingerprint is invalid. Run the preview again.";
            return false;
        }

        if (preview.SelectedEntities.Count == 0)
        {
            result = new LuaMAnimalCleanupResult(mapId, 0, preview.PopulationAfterCleanup);
            return true;
        }

        foreach (var uid in preview.SelectedEntities)
        {
            if (!TerminatingOrDeleted(uid))
                QueueDel(uid);
        }

        _adminLog.Add(
            LogType.Action,
            LogImpact.High,
            $"{actor} confirmed LuaM animal overflow cleanup on map {mapId}: " +
            $"queued={preview.SelectedEntities.Count}, before={preview.TotalPopulation}, " +
            $"after={preview.PopulationAfterCleanup}, keep={preview.Keep}, fingerprint={preview.Fingerprint}.");

        result = new LuaMAnimalCleanupResult(
            mapId,
            preview.SelectedEntities.Count,
            preview.PopulationAfterCleanup);
        return true;
    }

    private List<LuaMAnimalPopulationEntry> CollectEntries(
        MapId? mapFilter = null,
        bool assessCleanupSafety = true)
    {
        var entries = new List<LuaMAnimalPopulationEntry>();
        var seen = new HashSet<EntityUid>();

        var pestQuery = EntityQueryEnumerator<MobStateComponent, MetaDataComponent, TransformComponent>();
        while (pestQuery.MoveNext(out var uid, out var mobState, out var metadata, out var transform))
        {
            if (mapFilter != null && transform.MapID != mapFilter.Value ||
                transform.MapID == MapId.Nullspace ||
                metadata.EntityPrototype?.ID is not { } prototypeId ||
                !KnownPestSpecies.TryGetValue(prototypeId, out var species))
            {
                continue;
            }

            entries.Add(new LuaMAnimalPopulationEntry(
                uid,
                transform.MapID,
                species,
                true,
                assessCleanupSafety && IsSafeCleanupCandidate(uid, metadata, mobState),
                mobState.CurrentState));
            seen.Add(uid);
        }

        var partnerQuery = EntityQueryEnumerator<ReproductivePartnerComponent, MetaDataComponent, TransformComponent>();
        while (partnerQuery.MoveNext(out var uid, out _, out var metadata, out var transform))
        {
            if (seen.Contains(uid) ||
                mapFilter != null && transform.MapID != mapFilter.Value ||
                transform.MapID == MapId.Nullspace)
            {
                continue;
            }

            entries.Add(new LuaMAnimalPopulationEntry(
                uid,
                transform.MapID,
                metadata.EntityPrototype?.ID ?? metadata.EntityName,
                false,
                false,
                TryComp<MobStateComponent>(uid, out var state) ? state.CurrentState : MobState.Invalid));
            seen.Add(uid);
        }

        var offspringQuery = EntityQueryEnumerator<AnimalHusbandryOffspringComponent, MetaDataComponent, TransformComponent>();
        while (offspringQuery.MoveNext(out var uid, out _, out var metadata, out var transform))
        {
            if (seen.Contains(uid) ||
                mapFilter != null && transform.MapID != mapFilter.Value ||
                transform.MapID == MapId.Nullspace)
            {
                continue;
            }

            entries.Add(new LuaMAnimalPopulationEntry(
                uid,
                transform.MapID,
                metadata.EntityPrototype?.ID ?? metadata.EntityName,
                false,
                false,
                TryComp<MobStateComponent>(uid, out var state) ? state.CurrentState : MobState.Invalid));
            seen.Add(uid);
        }

        return entries;
    }

    private bool IsSafeCleanupCandidate(
        EntityUid uid,
        MetaDataComponent metadata,
        MobStateComponent mobState)
    {
        if (HasComp<ActorComponent>(uid) ||
            TryComp<MindContainerComponent>(uid, out var mind) && mind.HasMind ||
            HasComp<ReproductivePartnerComponent>(uid) ||
            HasComp<AnimalHusbandryOffspringComponent>(uid) ||
            TryComp<FactionExceptionComponent>(uid, out var factionException) && factionException.Ignored.Count > 0 ||
            TryComp<NPCImprintingOnSpawnBehaviourComponent>(uid, out var imprinting) && imprinting.Friends.Count > 0 ||
            TryComp<HTNComponent>(uid, out var htn) && HasCompanionBlackboardState(htn) ||
            _containers.IsEntityInContainer(uid, metadata) ||
            mobState.CurrentState == MobState.Critical ||
            IsNearPlayer(uid))
        {
            return false;
        }

        var prototype = metadata.EntityPrototype;
        return prototype != null &&
               KnownPestSpecies.ContainsKey(prototype.ID) &&
               HasDefaultPrototypeName(uid, metadata, prototype.Name);
    }

    private bool HasDefaultPrototypeName(EntityUid uid, MetaDataComponent metadata, string prototypeName)
    {
        if (metadata.EntityName.Equals(prototypeName, StringComparison.Ordinal))
            return true;

        if (!TryComp<NameIdentifierComponent>(uid, out var identifier) ||
            string.IsNullOrEmpty(identifier.FullIdentifier))
        {
            return false;
        }

        return metadata.EntityName.Equals(identifier.FullIdentifier, StringComparison.Ordinal) ||
               metadata.EntityName.Equals(
                   $"{prototypeName} {identifier.FullIdentifier}",
                   StringComparison.Ordinal);
    }

    private static bool HasCompanionBlackboardState(HTNComponent htn)
    {
        // NPCBlackboard.Owner is always the NPC itself, and OwnerCoordinates is
        // transient pathfinding state. FollowTarget is the companion signal.
        return htn.Blackboard.ContainsKey(NPCBlackboard.FollowTarget);
    }

    private bool IsNearPlayer(EntityUid uid)
    {
        var transform = Transform(uid);
        var position = _transform.GetWorldPosition(transform);
        var maximumDistanceSquared = PlayerPetProtectionRadius * PlayerPetProtectionRadius;
        var players = EntityQueryEnumerator<ActorComponent, TransformComponent>();
        while (players.MoveNext(out var player, out _, out var playerTransform))
        {
            if (player == uid || playerTransform.MapID != transform.MapID)
                continue;

            if (Vector2.DistanceSquared(position, _transform.GetWorldPosition(playerTransform)) <= maximumDistanceSquared)
                return true;
        }

        return false;
    }

    private LuaMAnimalPopulationReport BuildReport(
        IGrouping<MapId, LuaMAnimalPopulationEntry>? group,
        MapId? emptyMapId = null)
    {
        var entries = group?.ToList() ?? [];
        var species = entries
            .GroupBy(entry => entry.Species, StringComparer.Ordinal)
            .ToDictionary(item => item.Key, item => item.Count(), StringComparer.Ordinal);
        var mapId = group?.Key ?? emptyMapId ?? MapId.Nullspace;
        var limit = GetPopulationLimit();

        return new LuaMAnimalPopulationReport(
            mapId,
            limit,
            entries.Count,
            Math.Max(0, entries.Count - limit),
            entries.Count(entry => entry.CleanupEligible),
            species);
    }

    private static string BuildFingerprint(
        MapId mapId,
        int keep,
        IReadOnlyList<LuaMAnimalPopulationEntry> entries,
        IReadOnlyList<LuaMAnimalPopulationEntry> selected)
    {
        var input = new StringBuilder()
            .Append((int) mapId)
            .Append('|')
            .Append(keep)
            .Append('|');

        foreach (var entry in entries.OrderBy(entry => entry.Uid.Id))
        {
            input.Append(entry.Uid.Id)
                .Append(':')
                .Append(entry.Species)
                .Append(':')
                .Append(entry.CleanupEligible ? '1' : '0')
                .Append(':')
                .Append((int) entry.State)
                .Append(',');
        }

        input.Append('|');
        foreach (var entry in selected.OrderBy(entry => entry.Uid.Id))
            input.Append(entry.Uid.Id).Append(',');

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input.ToString()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private void WarnAboutOverflow()
    {
        foreach (var report in GetPopulationReports())
        {
            if (report.Overflow <= 0)
            {
                _nextWarningByMap.Remove(report.MapId);
                continue;
            }

            if (_nextWarningByMap.TryGetValue(report.MapId, out var nextWarning) &&
                _timing.CurTime < nextWarning)
            {
                continue;
            }

            _nextWarningByMap[report.MapId] = _timing.CurTime + WarningCooldown;
            var message = $"LuaM animal population overflow on map {report.MapId}: " +
                          $"{report.Total}/{report.Limit} (+{report.Overflow}), " +
                          $"safe pest candidates={report.CleanupEligible}. " +
                          $"Run `luam_animal_population {report.MapId}` and preview " +
                          $"`luam_animal_cleanup {report.MapId} {report.Limit}`.";
            _chat.SendAdminAlert(message);
            Logger.Warning(message);
        }
    }
}

internal sealed record LuaMAnimalPopulationEntry(
    EntityUid Uid,
    MapId MapId,
    string Species,
    bool KnownPest,
    bool CleanupEligible,
    MobState State);

public sealed record LuaMAnimalPopulationReport(
    MapId MapId,
    int Limit,
    int Total,
    int Overflow,
    int CleanupEligible,
    IReadOnlyDictionary<string, int> Species);

public sealed record LuaMAnimalCleanupPreview(
    MapId MapId,
    int Keep,
    int TotalPopulation,
    int Overflow,
    int SafeCandidates,
    IReadOnlyList<EntityUid> SelectedEntities,
    int PopulationAfterCleanup,
    string Fingerprint);

public readonly record struct LuaMAnimalCleanupResult(
    MapId MapId,
    int QueuedForDeletion,
    int PopulationAfterCleanup);
