using System.Linq;
using System.Numerics;
using System.Text;
using Content.Server.Chat.Systems;
using Content.Server.GameTicking;
using Content.Server.Pinpointer;
using Content.Server.Popups;
using Content.Server._NF.SectorServices;
using Content.Shared.CCVar;
using Content.Shared._LuaM.Sector;
using Content.Shared.GameTicking;
using Content.Shared.Interaction;
using Content.Shared.Paper;
using Content.Shared.Pinpointer;
using Content.Shared.Radiation.Components;
using Content.Shared.Verbs;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._LuaM.Sector;

public sealed partial class LuaMSectorDynamicEventSystem : EntitySystem
{
    public const int InitialDelaySeconds = 2700;
    public const int MinCooldownSeconds = 3600;
    public const int MaxCooldownSeconds = 7200;
    public const int MarkerSweepSeconds = 300;
    public const int PressureAnnouncementInitialDelaySeconds = 45;
    public const int PressureAnnouncementMinIntervalSeconds = 240;
    public const int PressureAnnouncementMaxIntervalSeconds = 720;
    public const int DynamicRewardMin = 30000;
    public const int DynamicRewardMax = 100000;
    public const int ConditionRewardStep = 250;
    public const int ConditionRewardCap = 2500;
    public const int AiWorldPressureRewardStep = 500;
    public const int AiWorldPressureRewardCap = 2500;
    public const int RouteCalibrationRewardStep = 500;
    public const int RouteCalibrationRewardCap = 2000;
    public const int RouteCalibrationClosureRewardStep = 250;
    public const int RouteCalibrationClosureRewardCap = 1000;
    public const int RouteCalibrationRadiationDampingStep = 1;
    public const int RouteCalibrationRadiationMinimumIntensity = 1;
    public const int PreferredProcessRequiredReputation = 1;
    public const int RouteStabilizationPingThreshold = 2;
    public const int DispatchCooldownReductionPerReputation = 2;
    public const int DispatchCooldownReductionCap = 20;
    public const string MarkerPrototype = "LuaMDynamicEventMarker";
    public const string SiteNotePrototype = "Paper";
    public const string ConditionRadiationHazardPrototype = "LuaMDynamicEventConditionRadiationHazard";
    public const string ConditionSensorDriftMarkerPrototype = "LuaMDynamicEventConditionSensorDriftMarker";
    public const string DynamicQuestDebrisPrototype = "LuaMDynamicQuestDebrisSmall";
    public const string MonolithArtifactTemplateId = "monolith-artifact";
    public const string MonolithResearchReportPrototype = "PaperLuaMMonolithResearchReport";
    public const int DynamicDebrisHostileInterval = 5;
    private const int MaxRouteCalibrationCredits = 3;
    private const int MaxRouteCalibrationSourceLength = 180;
    private const string StabilizedRouteEvidenceNotePrefix = "stabilized route field packet filed";
    private const int PressureAnnouncementSustainStart = 8;

    private static readonly string[] PressureAnnouncementLocIds =
    [
        "luam-sector-pressure-announcement-01",
        "luam-sector-pressure-announcement-02",
        "luam-sector-pressure-announcement-03",
        "luam-sector-pressure-announcement-04",
        "luam-sector-pressure-announcement-05",
        "luam-sector-pressure-announcement-06",
        "luam-sector-pressure-announcement-07",
        "luam-sector-pressure-announcement-08",
        "luam-sector-pressure-announcement-09",
        "luam-sector-pressure-announcement-10",
        "luam-sector-pressure-announcement-11",
        "luam-sector-pressure-announcement-12",
    ];

    private static readonly SectorConditionPreset[] AllHazardPresets =
    [
        new(
            "comms-blackout",
            "Comms blackout",
            4,
            "Long-range radio and remote updates are unreliable across the sector."),
        new(
            "radiation-lane",
            "Radiation lane",
            4,
            "A salvage lane is reporting elevated radiation. Use shielding and short exposure windows."),
        new(
            "dust-cloud",
            "Dust cloud",
            3,
            "Sensor drift and poor visual fixes are likely near active route markers."),
        new(
            "unstable-trade-route",
            "Unstable trade route",
            3,
            "Courier and cargo traffic is slowed by route instability and reroute pressure."),
    ];

    private static readonly Vector2[] SiteObjectOffsets =
    [
        new(1.0f, 0.0f),
        new(-1.0f, 0.0f),
        new(0.0f, 1.0f),
        new(0.0f, -1.0f),
        new(1.0f, 1.0f),
        new(-1.0f, -1.0f),
    ];

    private static readonly Vector2[] DebrisHostileOffsets =
    [
        new(2.5f, 0.5f),
        new(-2.5f, -0.5f),
        new(0.5f, 2.5f),
    ];

    private static readonly string[] DebrisHostilePrototypes =
    [
        "MobRogueSiliconDroneNonLethals",
        "MobRogueSiliconViscerator",
        "MobCarpSalvage",
    ];

    [Dependency] private LuaMSectorStorySystem _stories = default!;
    [Dependency] private ChatSystem _chat = default!;
    [Dependency] private SectorServiceSystem _sectorService = default!;
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private MetaDataSystem _metaData = default!;
    [Dependency] private PaperSystem _paper = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private NavMapSystem _navMap = default!;

    private TimeSpan _nextAutomaticEvent = TimeSpan.Zero;
    private TimeSpan _nextMarkerSweep = TimeSpan.Zero;
    private int _scheduledDispatchReductionPercent;
    private bool _allHazardsSeededForRound;
    private TimeSpan _nextPressureAnnouncement = TimeSpan.Zero;
    private int _pressureAnnouncementPhase;
    private string? _pressureAnnouncementConditionId;
    private int _generatedDebrisSites;
    private readonly Queue<string> _routeCalibrationSources = new();

    private static readonly DynamicEventTemplate[] Templates =
    [
        new(
            "quiet-distress",
            "Тихий аварийный маркер",
            "Неотвеченный спасательный сигнал",
            31200,
            "На краю сектора повторяется тихий аварийный маркер. Проверьте место, оставьте заметку маяка и оформите отчет закрытия.",
            "Возможен молчащий обломок. Перед признанием точки безопасной проверьте давление, питание, пиратов и маршрут буксировки.",
            "Distress",
            1,
            1,
            9,
            "LuaMDynamicEventMarkerDistress"),
        new(
            "field-repair",
            "Запрос полевого ремонта",
            "Поврежденный отвод ретранслятора",
            30400,
            "Отвод ретранслятора выдает короткие сервисные всплески. Осмотрите маршрут, отметьте поврежденное оборудование и оформите ремонтную заметку для следующего подрядчика.",
            "Рядом с корпусом ретранслятора возможны электрические повреждения и локальная разгерметизация.",
            "Salvage",
            1,
            1,
            7,
            "LuaMDynamicEventMarkerRepair"),
        new(
            "black-box-echo",
            "Эхо черного ящика",
            "Холодный сигнал регистратора",
            32200,
            "Сигнал регистратора появился достаточно долго, чтобы построить поисковую полосу. Верните черный ящик или оставьте письменный акт утраты.",
            "Через полосу могут дрейфовать старые обломки аварии. Подходите медленно и держите обратный вектор.",
            "Salvage",
            1,
            1,
            6,
            "LuaMDynamicEventMarkerSalvage"),
        new(
            MonolithArtifactTemplateId,
            "Обзор артефакта Монолита",
            "Фиолетовый контакт разлома",
            32600,
            "Фиолетовый фрагмент Монолита передает стабильное резонансное окно. Просканируйте осколок, подготовьте контейнер, запишите поведение резонатора и оформите исследовательский отчет.",
            "Неизвестное резонансное поле. Держите осколок в контейнере, избегайте длительного воздействия и остановите тест, если оборудование начнет повторять устаревшую телеметрию.",
            "Research",
            1,
            1,
            5,
            "LuaMDynamicEventMarkerMonolith",
            new[]
            {
                "LuaMMonolithShard",
                "LuaMArtifactContainmentCase",
                "LuaMAnomalyScanner",
                "LuaMMonolithResonator",
                MonolithResearchReportPrototype,
            }),
        new(
            "navigation-drift",
            "Пакет навигационного дрейфа",
            "Нестабильный маршрутный маркер",
            30000,
            "Маршрутный маркер ушел с публичной карты. Подтвердите GPS-полосу, опубликуйте исправленную заметку и предупредите следующих пилотов.",
            "Навигационные данные устарели. Не доверяйте смещениям автопилота, пока маршрут не проверен.",
            "Station Records",
            1,
            1,
            5,
            "LuaMDynamicEventMarkerNavigation"),
        new(
            "courier-handoff",
            "Проверка курьерской передачи",
            "Задержанная передача груза",
            30800,
            "Запечатанная передача ждет малую команду. Проверьте точку получения, сохраните пломбу целой и оформите квитанцию.",
            "За точкой передачи могут наблюдать. Не открывайте контейнер, если место не скомпрометировано.",
            "Trade",
            1,
            1,
            5,
            "LuaMDynamicEventMarkerTrade"),
        new(
            "ledger-audit",
            "Аудит призрачного реестра",
            "Устаревшая запись станции",
            30000,
            "Устаревший станционный реестр конфликтует с текущим трафиком. Проверьте, запись устарела или подделана, затем оформите выписку.",
            "Несовпадение записи может скрывать ложный сигнал бедствия или заброшенный долг станции.",
            "Station Records",
            1,
            1,
            4,
            "LuaMDynamicEventMarkerRecords")
    ];

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<LuaMSectorStoryResolvedEvent>(OnStoryResolved);
        SubscribeLocalEvent<LuaMSectorMemoryResetEvent>(OnMemoryReset);
        SubscribeLocalEvent<LuaMSectorReputationChangedEvent>(OnReputationChanged);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestartCleanup);
        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnGameRunLevelChanged);
        SubscribeLocalEvent<LuaMDynamicEventMarkerComponent, GetVerbsEvent<InteractionVerb>>(OnGetMarkerVerbs);
        SubscribeLocalEvent<LuaMDynamicEventSiteObjectComponent, GetVerbsEvent<InteractionVerb>>(OnGetSiteObjectVerbs);
        SubscribeLocalEvent<LuaMDynamicEventMarkerComponent, InteractHandEvent>(OnMarkerInteractHand);
        SubscribeLocalEvent<LuaMDynamicEventSiteObjectComponent, InteractHandEvent>(OnSiteObjectInteractHand);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_ticker.RunLevel != GameRunLevel.InRound)
        {
            _nextAutomaticEvent = TimeSpan.Zero;
            _nextMarkerSweep = TimeSpan.Zero;
            _scheduledDispatchReductionPercent = 0;
            _allHazardsSeededForRound = false;
            _generatedDebrisSites = 0;
            ResetPressureAnnouncementCycle();
            return;
        }

        EnsureAllHazardsSeeded();
        UpdatePressureAnnouncementCycle();

        if (_nextMarkerSweep == TimeSpan.Zero ||
            _timing.CurTime >= _nextMarkerSweep)
        {
            CleanupStaleDynamicMarkers();
            _nextMarkerSweep = _timing.CurTime + TimeSpan.FromSeconds(MarkerSweepSeconds);
        }

        if (!_cfg.GetCVar(CCVars.LuaMDynamicEventsEnabled))
        {
            _nextAutomaticEvent = TimeSpan.Zero;
            return;
        }

        if (_nextAutomaticEvent == TimeSpan.Zero)
        {
            _nextAutomaticEvent = _timing.CurTime + TimeSpan.FromSeconds(InitialDelaySeconds);
            return;
        }

        if (_timing.CurTime < _nextAutomaticEvent)
            return;

        if (TryGenerateDynamicEvent("динамический генератор LuaM", out _, out _))
            ScheduleNextAutomaticEvent();
        else
            _nextAutomaticEvent = _timing.CurTime + TimeSpan.FromMinutes(10);
    }

    public bool TryGenerateDynamicEvent(
        string actor,
        out LuaMSectorStoryRecord? record,
        out string error,
        string? templateId = null,
        bool ignoreOpenRuntimeLead = false,
        bool ignorePlayerGate = false,
        MapCoordinates? markerCoordinates = null)
    {
        record = null;
        error = string.Empty;

        var playerCount = CountActivePlayers();
        if (!ignorePlayerGate && playerCount <= 0)
        {
            error = "Нет активных игроков для динамического события сектора LuaM.";
            return false;
        }

        var status = _stories.GetStatusSnapshot();
        if (!CanCreateDynamicEvent(status, out error))
            return false;

        if (!ignoreOpenRuntimeLead && HasOpenRuntimeLead(status))
        {
            error = "Открытая активная зацепка сектора LuaM уже существует.";
            return false;
        }

        var template = string.IsNullOrWhiteSpace(templateId)
            ? PickTemplate(status, playerCount)
            : Templates.FirstOrDefault(template => template.Id.Equals(templateId, StringComparison.OrdinalIgnoreCase));

        if (template == null)
        {
            error = string.IsNullOrWhiteSpace(templateId)
                ? "Нет доступного шаблона динамического события LuaM."
                : $"Шаблон динамического события LuaM не существует: {templateId}";
            return false;
        }

        var resolvedMarkerCoordinates = ResolveMarkerCoordinates(markerCoordinates);
        var markerLocation = FormatMarkerLocation(resolvedMarkerCoordinates);
        var debrisPlan = BuildDebrisSitePlan(resolvedMarkerCoordinates);
        var conditionRewardBonus = GetConditionRewardBonus(status);
        var queuedRouteCalibrationSource = GetQueuedRouteCalibrationSource();
        var routeCalibrationChainDepth = GetRouteCalibrationChainDepth(queuedRouteCalibrationSource);
        var routeCalibrationRewardBonus = GetRouteCalibrationRewardBonus(queuedRouteCalibrationSource);
        var routeCalibrationClosureRewardBonus = GetRouteCalibrationClosureRewardBonus(queuedRouteCalibrationSource);
        var conditionRiskSummary = BuildConditionRiskSummary(status, conditionRewardBonus);
        var conditionSeverity = GetPrimaryConditionSeverity(status);
        var description = BuildDescription(
            template,
            status,
            playerCount,
            markerLocation,
            conditionRewardBonus,
            routeCalibrationRewardBonus,
            routeCalibrationClosureRewardBonus,
            routeCalibrationChainDepth);
        var hazard = BuildHazard(
            template,
            status,
            playerCount,
            conditionRewardBonus,
            routeCalibrationRewardBonus,
            routeCalibrationClosureRewardBonus,
            routeCalibrationChainDepth);
        hazard = AppendDebrisHazardContext(hazard, debrisPlan);
        var reward = Math.Clamp(
            template.Reward + conditionRewardBonus + routeCalibrationRewardBonus,
            DynamicRewardMin,
            DynamicRewardMax);

        if (!_stories.TrySeedDistressStory(
                template.Title,
                template.Vessel,
                reward,
                description,
                hazard,
                template.ReputationTarget,
                template.ReputationDelta,
                actor,
                out record,
                out error,
                hazardRewardBonusExtra: routeCalibrationClosureRewardBonus))
        {
            return false;
        }

        SpawnDebrisSite(template, record, actor, resolvedMarkerCoordinates, markerLocation, debrisPlan);
        var routeCalibrationApplied = SpawnWorldMarker(template, record, actor, resolvedMarkerCoordinates, markerLocation, conditionRiskSummary, conditionSeverity, status, out var routeCalibrationSource);
        SpawnSensorDriftMarker(template, record, actor, resolvedMarkerCoordinates, status, routeCalibrationApplied);
        SpawnSiteNote(template, record, actor, resolvedMarkerCoordinates, markerLocation, conditionRiskSummary, routeCalibrationSource);
        SpawnSiteObjects(template, record, actor, resolvedMarkerCoordinates, conditionRiskSummary, routeCalibrationSource);
        SpawnConditionHazards(template, record, actor, resolvedMarkerCoordinates, markerLocation, status, routeCalibrationSource);
        ScheduleNextAutomaticEvent();
        return true;
    }

    public IReadOnlyList<string> GetTemplateIds()
    {
        return Templates.Select(template => template.Id).ToList();
    }

    public LuaMSectorMapNodeUiEntry[] BuildSectorMapUiEntries()
    {
        var nodes = new List<LuaMSectorMapNodeUiEntry>();
        var status = _stories.GetStatusSnapshot();
        _stories.TryGetOpenRuntimeDistressStory(out var activeRuntimeStory);

        foreach (var condition in GetActiveConditions(status))
        {
            nodes.Add(new LuaMSectorMapNodeUiEntry
            {
                NodeId = $"condition:{condition.ConditionId}",
                Kind = "условие",
                Title = condition.Title,
                State = $"SC-{condition.Severity} активно",
                StoryId = string.Empty,
                TemplateId = condition.ConditionId,
                Location = "весь сектор",
                Detail = condition.Summary,
                Risk = GetConditionEffectText(condition),
                Active = true,
                RoutePingCount = 0,
                SortOrder = 30 + Math.Clamp(condition.Severity, 1, 5),
            });
        }

        var markerQuery = EntityQueryEnumerator<LuaMDynamicEventMarkerComponent>();
        while (markerQuery.MoveNext(out var uid, out var marker))
        {
            if (Terminating(uid))
                continue;

            var story = GetStoryRecord(marker.Story.ToString());
            var activeRoute = activeRuntimeStory != null && marker.Story == activeRuntimeStory.Story;
            var routeDetail = marker.RoutePingCount > 0
                ? marker.LastRoutePingSummary
                : $"Оформил: {marker.CreatedBy}";
            var state = activeRoute ? "активный маршрут" : "сгенерированный маршрут";
            if (!string.IsNullOrWhiteSpace(marker.RouteCalibrationSource))
            {
                var chainDepth = GetRouteCalibrationChainDepth(marker.RouteCalibrationSource);
                state = chainDepth > 1
                    ? activeRoute ? "active chained route" : "chained calibration inherited"
                    : activeRoute ? "active calibrated route" : "calibration inherited";
                routeDetail = $"{routeDetail}; route calibration source: {marker.RouteCalibrationSource}";
                if (chainDepth > 0)
                    routeDetail = $"{routeDetail}; route calibration chain depth: {chainDepth}";
            }

            if (marker.StabilizedFieldPacketPrinted)
            {
                state = Loc.GetString("luam-sector-terminal-sector-map-state-field-packet-printed");
                routeDetail = $"{routeDetail}; {Loc.GetString("luam-sector-terminal-sector-map-detail-field-packet-printed")}";
            }

            if (marker.RoutePingCount >= RouteStabilizationPingThreshold)
                routeDetail = $"{routeDetail}; route calibration handoff: {BuildStabilizedRouteEvidenceNote(marker)}";

            if (TryComp<NavMapBeaconComponent>(uid, out var nav) && !nav.Enabled)
                state = "трансляция подавлена";

            if (activeRoute)
                routeDetail = AppendRouteClosureInstruction(routeDetail);

            nodes.Add(new LuaMSectorMapNodeUiEntry
            {
                NodeId = $"route:{marker.Story}:{marker.TemplateId}",
                Kind = "маршрут",
                Title = story?.Title ?? "Сгенерированный маршрут сектора",
                State = state,
                StoryId = marker.Story.ToString(),
                TemplateId = marker.TemplateId,
                Location = marker.MarkerLocation,
                Detail = routeDetail,
                Risk = string.IsNullOrWhiteSpace(marker.ConditionRiskSummary)
                    ? story?.Hazard ?? string.Empty
                    : marker.ConditionRiskSummary,
                Active = activeRoute,
                RoutePingCount = marker.RoutePingCount,
                SortOrder = activeRoute ? 0 : 10,
            });
        }

        var siteQuery = EntityQueryEnumerator<LuaMDynamicEventSiteObjectComponent>();
        while (siteQuery.MoveNext(out var uid, out var site))
        {
            if (Terminating(uid))
                continue;

            var story = GetStoryRecord(site.Story.ToString());
            var detail = string.IsNullOrWhiteSpace(site.RouteSurveySummary)
                ? $"Оформил: {site.CreatedBy}"
                : site.RouteSurveySummary;
            var activeSite = activeRuntimeStory != null && site.Story == activeRuntimeStory.Story;
            if (activeSite)
                detail = AppendRouteClosureInstruction(detail);

            nodes.Add(new LuaMSectorMapNodeUiEntry
            {
                NodeId = $"site:{site.Story}:{site.TemplateId}:{site.SiteObjectKind}",
                Kind = "точка",
                Title = $"{story?.Title ?? "Сгенерированная зацепка сектора"}: заметка места",
                State = site.SiteObjectKind,
                StoryId = site.Story.ToString(),
                TemplateId = site.TemplateId,
                Location = site.MarkerLocation,
                Detail = detail,
                Risk = BuildSiteMapRisk(site),
                Active = activeSite,
                RoutePingCount = site.RouteSurveyApplied ? 1 : 0,
                SortOrder = 20,
            });
        }

        var driftQuery = EntityQueryEnumerator<LuaMDynamicEventSensorDriftComponent>();
        while (driftQuery.MoveNext(out var uid, out var drift))
        {
            if (Terminating(uid))
                continue;

            var story = GetStoryRecord(drift.Story.ToString());
            nodes.Add(new LuaMSectorMapNodeUiEntry
            {
                NodeId = $"drift:{drift.Story}:{drift.ConditionId}",
                Kind = "дрейф сенсоров",
                Title = $"{story?.Title ?? "Сгенерированная зацепка сектора"}: дрейф сенсоров",
                State = "сенсорное эхо",
                StoryId = drift.Story.ToString(),
                TemplateId = drift.TemplateId,
                Location = drift.DriftLocation,
                Detail = $"Условие {drift.ConditionId}; оформил: {drift.CreatedBy}",
                Risk = "вероятен дрейф сенсоров",
                Active = activeRuntimeStory != null && drift.Story == activeRuntimeStory.Story,
                RoutePingCount = 0,
                SortOrder = 40,
            });
        }

        var hazardQuery = EntityQueryEnumerator<LuaMDynamicEventConditionHazardComponent>();
        while (hazardQuery.MoveNext(out var uid, out var hazard))
        {
            if (Terminating(uid))
                continue;

            var story = GetStoryRecord(hazard.Story.ToString());
            nodes.Add(new LuaMSectorMapNodeUiEntry
            {
                NodeId = $"hazard:{hazard.Story}:{hazard.ConditionId}",
                Kind = "опасность условия",
                Title = $"{story?.Title ?? "Сгенерированная зацепка сектора"}: опасность условия",
                State = "локальная опасность",
                StoryId = hazard.Story.ToString(),
                TemplateId = hazard.TemplateId,
                Location = hazard.MarkerLocation,
                Detail = BuildConditionHazardMapDetail(hazard),
                Risk = "локальная физическая опасность условия",
                Active = activeRuntimeStory != null && hazard.Story == activeRuntimeStory.Story,
                RoutePingCount = 0,
                SortOrder = 35,
            });
        }

        return nodes
            .OrderBy(node => node.SortOrder)
            .ThenBy(node => node.Kind)
            .ThenBy(node => node.Title)
            .Take(24)
            .ToArray();
    }

    private static string BuildSiteMapRisk(LuaMDynamicEventSiteObjectComponent site)
    {
        if (string.IsNullOrWhiteSpace(site.RouteCalibrationSource))
            return site.ConditionRiskSummary;

        var source = $"source packet: {site.RouteCalibrationSource}";
        var chainDepth = GetRouteCalibrationChainDepth(site.RouteCalibrationSource);
        if (chainDepth > 0)
            source = $"{source}; route calibration chain depth: {chainDepth}";

        return string.IsNullOrWhiteSpace(site.ConditionRiskSummary)
            ? source
            : $"{site.ConditionRiskSummary}; {source}";
    }

    private static string BuildConditionHazardMapDetail(LuaMDynamicEventConditionHazardComponent hazard)
    {
        var detail = $"Условие {hazard.ConditionId}; оформил: {hazard.CreatedBy}";
        if (hazard.RouteCalibrationRadiationDamping <= 0)
            return detail;

        detail = $"{detail}; route calibration radiation damping: -{hazard.RouteCalibrationRadiationDamping}";
        if (hazard.RouteCalibrationChainDepth > 0)
            detail = $"{detail}; chain depth {hazard.RouteCalibrationChainDepth}";

        return detail;
    }

    public bool TryGeneratePreferredDynamicEvent(
        string actor,
        string templateId,
        out LuaMSectorStoryRecord? record,
        out string error,
        bool ignoreOpenRuntimeLead = false,
        bool ignorePlayerGate = false,
        MapCoordinates? markerCoordinates = null)
    {
        record = null;
        error = string.Empty;

        if (!TryFindTemplate(templateId, out var template))
        {
            error = $"Шаблон динамического события LuaM не существует: {templateId}";
            return false;
        }

        var status = _stories.GetStatusSnapshot();
        var reputation = GetReputationValue(template.ReputationTarget, status.ReputationLedger);
        if (reputation < PreferredProcessRequiredReputation)
        {
            error = BuildPreferredReputationBlockReason(template, reputation);
            return false;
        }

        return TryGenerateDynamicEvent(
            actor,
            out record,
            out error,
            template.Id,
            ignoreOpenRuntimeLead,
            ignorePlayerGate,
            markerCoordinates);
    }

    public LuaMSectorPreferredProcessUiEntry[] BuildPreferredProcessUiEntries()
    {
        var status = _stories.GetStatusSnapshot();
        var playerCount = CountActivePlayers();
        var inRound = _ticker.RunLevel == GameRunLevel.InRound;
        var dynamicEventsEnabled = _cfg.GetCVar(CCVars.LuaMDynamicEventsEnabled);
        var atSiteCapacity = IsAtDynamicEventSiteCapacity(status);
        var hasOpenRuntimeLead = HasOpenRuntimeLead(status);
        var requestBlockReason = BuildRequestBlockReason(
            inRound,
            playerCount,
            hasOpenRuntimeLead,
            dynamicEventsEnabled,
            atSiteCapacity);

        return Templates
            .OrderBy(template => template.ReputationTarget)
            .ThenBy(template => template.Title)
            .Select(template =>
            {
                var reputation = GetReputationValue(template.ReputationTarget, status.ReputationLedger);
                var unlocked = reputation >= PreferredProcessRequiredReputation;
                var canRequestNow = unlocked &&
                                    inRound &&
                                    playerCount > 0 &&
                                    !hasOpenRuntimeLead &&
                                    dynamicEventsEnabled &&
                                    !atSiteCapacity;
                return new LuaMSectorPreferredProcessUiEntry
                {
                    TemplateId = template.Id,
                    Title = template.Title,
                    Vessel = template.Vessel,
                    Description = template.Description,
                    ReputationTarget = template.ReputationTarget,
                    CurrentReputation = reputation,
                    RequiredReputation = PreferredProcessRequiredReputation,
                    Tier = GetReputationTier(reputation),
                    BaseReward = template.Reward,
                    ReputationBonus = GetReputationRewardBonus(template.ReputationTarget, status.ReputationLedger),
                    Unlocked = unlocked,
                    CanRequestNow = canRequestNow,
                    BlockReason = canRequestNow
                        ? string.Empty
                        : unlocked
                            ? requestBlockReason
                            : BuildPreferredReputationBlockReason(template, reputation),
                };
            })
            .ToArray();
    }

    public LuaMSectorAutomationUiEntry BuildAutomationUiEntry()
    {
        var status = _stories.GetStatusSnapshot();
        var playerCount = CountActivePlayers();
        _stories.TryGetOpenRuntimeDistressStory(out var activeRuntimeStory);
        LuaMDynamicEventMarkerComponent? activeRouteMarker = null;
        var hasActiveRouteMarker = activeRuntimeStory != null &&
                                   TryFindDynamicMarker(activeRuntimeStory, out _, out activeRouteMarker);
        var hasCommsBlackout = HasCommsBlackoutCondition(status);
        var activeRouteHasBeacon = hasActiveRouteMarker &&
                                   FindNavMapBeacon(activeRouteMarker!, out _);
        var activeRouteStabilized = activeRouteMarker?.RoutePingCount >= RouteStabilizationPingThreshold;
        var activeRouteFieldPacketPrinted = activeRouteMarker?.StabilizedFieldPacketPrinted == true;
        var activeRouteHasSurveyCommsRelay = activeRouteMarker?.SiteSurveyCommsRelayAvailable == true;
        var openRuntimeLead = status.Hazards.FirstOrDefault(hazard =>
            !hazard.Resolved &&
            hazard.Story.ToString().StartsWith(LuaMSectorStorySystem.RuntimeDistressStoryPrefix, StringComparison.Ordinal));
        var inRound = _ticker.RunLevel == GameRunLevel.InRound;
        var dynamicEventsEnabled = _cfg.GetCVar(CCVars.LuaMDynamicEventsEnabled);
        var atSiteCapacity = IsAtDynamicEventSiteCapacity(status);
        var hasOpenRuntimeLead = openRuntimeLead != null;
        var canRequest = inRound &&
                         playerCount > 0 &&
                         !hasOpenRuntimeLead &&
                         dynamicEventsEnabled &&
                         !atSiteCapacity;
        var dispatchReputation = GetDispatchReputationScore(status);
        var dispatchReduction = GetDispatchCooldownReductionPercent(dispatchReputation);
        var (dispatchMin, dispatchMax) = GetAdjustedCooldownRange(dispatchReduction);
        var hasRouteCalibrationSource = _routeCalibrationSources.TryPeek(out var routeCalibrationSource);
        var queuedRouteCalibrationSource = hasRouteCalibrationSource
            ? routeCalibrationSource ?? string.Empty
            : string.Empty;

        return new LuaMSectorAutomationUiEntry
        {
            State = inRound ? "смена идет" : $"ожидание смены ({_ticker.RunLevel})",
            NextAutomaticEvent = BuildNextAutomaticEventText(inRound),
            ActivePlayers = playerCount,
            DispatchTier = GetDispatchTier(dispatchReputation),
            DispatchReputationScore = dispatchReputation,
            DispatchCooldownReductionPercent = dispatchReduction,
            DispatchCooldownMinSeconds = dispatchMin,
            DispatchCooldownMaxSeconds = dispatchMax,
            HasOpenRuntimeLead = hasOpenRuntimeLead,
            OpenRuntimeLead = openRuntimeLead?.Title ?? string.Empty,
            CanRequestDynamicEvent = canRequest,
            RequestBlockReason = BuildRequestBlockReason(
                inRound,
                playerCount,
                hasOpenRuntimeLead,
                dynamicEventsEnabled,
                atSiteCapacity),
            CanPingRoute = activeRouteHasBeacon && !activeRouteStabilized && (!hasCommsBlackout || activeRouteHasSurveyCommsRelay),
            RoutePingBlockReason = BuildRoutePingBlockReason(hasOpenRuntimeLead, hasActiveRouteMarker, activeRouteHasBeacon, activeRouteStabilized, activeRouteFieldPacketPrinted, hasCommsBlackout, activeRouteHasSurveyCommsRelay),
            ActiveRouteStory = activeRuntimeStory?.Story.ToString() ?? openRuntimeLead?.Story.ToString() ?? string.Empty,
            ActiveRouteMarker = activeRouteMarker?.MarkerLocation ?? string.Empty,
            LastRoutePing = activeRouteMarker?.LastRoutePingSummary ?? string.Empty,
            RoutePingCount = activeRouteMarker?.RoutePingCount ?? 0,
            RouteCalibrationCredits = _routeCalibrationSources.Count,
            RouteCalibrationSource = queuedRouteCalibrationSource,
            RouteCalibrationSourceChainDepth = GetRouteCalibrationChainDepth(queuedRouteCalibrationSource),
            RouteCalibrationRewardBonus = GetRouteCalibrationRewardBonus(queuedRouteCalibrationSource),
            RouteCalibrationClosureRewardBonus = GetRouteCalibrationClosureRewardBonus(queuedRouteCalibrationSource),
            RouteCalibrationRadiationDampingPreview = GetRouteCalibrationRadiationDampingPreview(status, queuedRouteCalibrationSource),
            RouteCalibrationSensorDriftSuppressionPreview = GetRouteCalibrationSensorDriftSuppressionPreview(status, queuedRouteCalibrationSource),
            RouteCalibrationSources = _routeCalibrationSources.ToArray(),
            RouteCalibrationHandoffReady = activeRouteStabilized,
            RouteCalibrationHandoffSource = activeRouteStabilized && activeRouteMarker != null
                ? BuildStabilizedRouteEvidenceNote(activeRouteMarker)
                : string.Empty,
            ActiveRouteCalibrationSource = activeRouteMarker?.RouteCalibrationSource ?? string.Empty,
            ActiveRouteCalibrationChainDepth = GetRouteCalibrationChainDepth(activeRouteMarker?.RouteCalibrationSource ?? string.Empty),
            ActiveRouteCalibrationRelayInherited = activeRouteMarker?.RouteCalibrationRelayInherited == true,
            ActiveMarkers = CountComponents<LuaMDynamicEventMarkerComponent>(),
            SiteNotes = CountComponents<LuaMDynamicEventSiteObjectComponent>(),
            ConditionHazards = CountComponents<LuaMDynamicEventConditionHazardComponent>(),
            SensorDriftMarkers = CountComponents<LuaMDynamicEventSensorDriftComponent>(),
            TemplateIds = Templates.Select(template => template.Id).ToArray(),
        };
    }

    public bool TryPingActiveRouteMarker(EntityUid user, out string result)
    {
        result = string.Empty;

        if (!_stories.TryGetOpenRuntimeDistressStory(out var story) || story == null)
        {
            result = Loc.GetString("luam-sector-terminal-route-ping-no-open");
            _popup.PopupEntity(result, user, user);
            return false;
        }

        if (!TryFindDynamicMarker(story, out var markerUid, out var marker))
        {
            result = Loc.GetString("luam-sector-terminal-route-ping-no-marker");
            _popup.PopupEntity(result, user, user);
            return false;
        }

        if (marker.RoutePingCount >= RouteStabilizationPingThreshold)
        {
            result = Loc.GetString(marker.StabilizedFieldPacketPrinted
                ? "luam-sector-terminal-route-ping-stabilized-file-printed"
                : "luam-sector-terminal-route-ping-already-stabilized");
            _popup.PopupEntity(result, markerUid, user);
            return false;
        }

        var status = _stories.GetStatusSnapshot();
        var hasCommsBlackout = HasCommsBlackoutCondition(status);
        var siteSurveyRelayUsed = hasCommsBlackout && marker.SiteSurveyCommsRelayAvailable;
        if (hasCommsBlackout && !siteSurveyRelayUsed)
        {
            result = Loc.GetString("luam-sector-terminal-route-ping-comms-blackout");
            _popup.PopupEntity(result, user, user);
            return false;
        }

        if (!TryComp<NavMapBeaconComponent>(markerUid, out var nav))
        {
            result = Loc.GetString("luam-sector-terminal-route-ping-no-beacon");
            _popup.PopupEntity(result, user, user);
            return false;
        }

        var actor = Name(user);
        marker.RoutePingCount++;
        marker.LastRoutePingActor = actor;
        var driftMarkersCleared = ClearSensorDriftMarkersForStory(story.Story.ToString());
        var conditionHazardsCleared = marker.RoutePingCount >= RouteStabilizationPingThreshold
            ? ClearConditionHazardsForStory(story.Story.ToString())
            : 0;
        marker.LastRoutePingSummary = BuildRoutePingSummary(
            marker.RoutePingCount,
            actor,
            marker.MarkerLocation,
            driftMarkersCleared,
            conditionHazardsCleared);
        if (siteSurveyRelayUsed)
        {
            marker.LastRoutePingSummary = $"{marker.LastRoutePingSummary}; site survey field relay used";
            if (marker.RouteCalibrationRelayInherited)
                marker.LastRoutePingSummary = $"{marker.LastRoutePingSummary}; inherited field relay used";

            marker.SiteSurveyCommsRelayAvailable = false;
        }

        if (!string.IsNullOrWhiteSpace(marker.RouteCalibrationSource))
        {
            marker.LastRoutePingSummary =
                $"{marker.LastRoutePingSummary}; route calibration source carried forward: {marker.RouteCalibrationSource}";
        }

        if (string.IsNullOrWhiteSpace(marker.RoutePingBaseLabel))
            marker.RoutePingBaseLabel = GetRoutePingBaseLabel(markerUid, nav);

        _navMap.SetBeaconText(markerUid, $"{marker.RoutePingBaseLabel} [PING {marker.RoutePingCount}]", nav);
        _navMap.SetBeaconColor(markerUid,
            marker.RoutePingCount >= RouteStabilizationPingThreshold
                ? Color.FromHex("#7CFF6BFF")
                : Color.FromHex("#00E5FFFF"),
            nav);
        _navMap.SetBeaconEnabled(markerUid, true, nav);

        result = Loc.GetString("luam-sector-terminal-route-ping-sent",
            ("count", marker.RoutePingCount),
            ("title", story.Title),
            ("location", marker.MarkerLocation));
        if (conditionHazardsCleared > 0)
        {
            result = $"{result} {Loc.GetString("luam-sector-terminal-route-ping-stabilized",
                ("hazards", conditionHazardsCleared))}";
        }
        if (marker.RoutePingCount >= RouteStabilizationPingThreshold)
            result = AppendRouteClosureInstruction(result);
        if (!string.IsNullOrWhiteSpace(marker.RouteCalibrationSource))
        {
            result = $"{result} {Loc.GetString("luam-sector-terminal-route-ping-calibrated-source",
                ("source", marker.RouteCalibrationSource))}";
        }

        _popup.PopupEntity(result, markerUid, user);
        return true;
    }

    public bool TrySurveySiteObject(
        EntityUid uid,
        EntityUid user,
        out string result,
        LuaMDynamicEventSiteObjectComponent? site = null)
    {
        result = string.Empty;

        if (!Resolve(uid, ref site, false))
            return false;

        if (site.RouteSurveyApplied)
        {
            result = "Site survey is already recorded.";
            _popup.PopupEntity(result, uid, user);
            return false;
        }

        if (!_stories.TryGetOpenRuntimeDistressStory(out var story) ||
            story == null ||
            story.Story != site.Story)
        {
            result = "No matching active route is open for this site.";
            _popup.PopupEntity(result, uid, user);
            return false;
        }

        if (!TryFindDynamicMarker(story, out var markerUid, out var marker))
        {
            result = "Active route marker is unavailable for this site.";
            _popup.PopupEntity(result, uid, user);
            return false;
        }

        if (marker.SiteSurveyApplied)
        {
            result = "Site survey is already recorded for this route.";
            _popup.PopupEntity(result, markerUid, user);
            return false;
        }

        if (marker.RoutePingCount >= RouteStabilizationPingThreshold)
        {
            result = "Route is already stabilized.";
            _popup.PopupEntity(result, markerUid, user);
            return false;
        }

        if (!TryComp<NavMapBeaconComponent>(markerUid, out var nav))
        {
            result = "Active route marker has no nav beacon.";
            _popup.PopupEntity(result, markerUid, user);
            return false;
        }

        var status = _stories.GetStatusSnapshot();
        var hasCommsBlackout = HasCommsBlackoutCondition(status);
        var inheritedRelayAvailable = marker.RouteCalibrationRelayInherited && marker.SiteSurveyCommsRelayAvailable;
        var actor = Name(user);
        marker.RoutePingCount++;
        marker.LastRoutePingActor = actor;

        var driftMarkersCleared = ClearSensorDriftMarkersForStory(story.Story.ToString());
        var conditionHazardsCleared = marker.RoutePingCount >= RouteStabilizationPingThreshold
            ? ClearConditionHazardsForStory(story.Story.ToString())
            : 0;

        marker.LastRoutePingSummary = BuildSiteSurveyRoutePingSummary(
            marker.RoutePingCount,
            actor,
            site,
            driftMarkersCleared,
            conditionHazardsCleared,
            hasCommsBlackout,
            inheritedRelayAvailable);
        var routeCalibrationChainDepth = GetRouteCalibrationChainDepth(marker.RouteCalibrationSource);
        if (!string.IsNullOrWhiteSpace(marker.RouteCalibrationSource))
        {
            marker.LastRoutePingSummary =
                $"{marker.LastRoutePingSummary}; route calibration source carried forward by site survey: {marker.RouteCalibrationSource}";
            if (routeCalibrationChainDepth > 0)
                marker.LastRoutePingSummary = $"{marker.LastRoutePingSummary}; route calibration chain depth: {routeCalibrationChainDepth}";
        }

        marker.SiteSurveyApplied = true;
        marker.SiteSurveySummary = marker.LastRoutePingSummary;
        marker.SiteSurveyCommsRelayAvailable = hasCommsBlackout &&
                                               marker.RoutePingCount < RouteStabilizationPingThreshold &&
                                               !inheritedRelayAvailable;

        MarkSiteSurveyAppliedForStory(story.Story.ToString(), marker.LastRoutePingSummary);
        AppendSiteSurveyToPaper(uid, marker.LastRoutePingSummary);

        if (string.IsNullOrWhiteSpace(marker.RoutePingBaseLabel))
            marker.RoutePingBaseLabel = GetRoutePingBaseLabel(markerUid, nav);

        _navMap.SetBeaconText(markerUid, $"{marker.RoutePingBaseLabel} [PING {marker.RoutePingCount}]", nav);
        _navMap.SetBeaconColor(markerUid,
            marker.RoutePingCount >= RouteStabilizationPingThreshold
                ? Color.FromHex("#7CFF6BFF")
                : Color.FromHex("#00E5FFFF"),
            nav);
        _navMap.SetBeaconEnabled(markerUid, true, nav);

        result = $"Site survey recorded for {story.Title}: {site.MarkerLocation}";
        if (routeCalibrationChainDepth > 0)
            result = $"{result} Calibration chain depth: {routeCalibrationChainDepth}.";
        if (marker.RoutePingCount >= RouteStabilizationPingThreshold)
            result = AppendRouteClosureInstruction(result);

        _popup.PopupEntity(result, markerUid, user);
        return true;
    }

    public bool TryPrintMarkerFieldPacket(
        EntityUid uid,
        EntityUid user,
        out EntityUid report,
        LuaMDynamicEventMarkerComponent? marker = null)
    {
        report = default;

        if (!Resolve(uid, ref marker, false))
            return false;

        var stabilizedPacket = marker.RoutePingCount >= RouteStabilizationPingThreshold;
        if (stabilizedPacket && marker.StabilizedFieldPacketPrinted)
        {
            _popup.PopupEntity(Loc.GetString("luam-sector-terminal-popup-marker-stabilized-already-printed"), uid, user);
            return false;
        }

        report = Spawn(marker.PaperPrototype, Transform(uid).Coordinates);
        if (!TryComp<PaperComponent>(report, out var paper))
        {
            QueueDel(report);
            _popup.PopupEntity(Loc.GetString("luam-sector-terminal-popup-marker-printer-failed"), uid, user);
            report = default;
            return false;
        }

        _paper.SetContent((report, paper), BuildMarkerFieldPacket(marker));
        AddStabilizedRouteEvidence(report, marker);
        if (stabilizedPacket)
            marker.StabilizedFieldPacketPrinted = true;
        _popup.PopupEntity(Loc.GetString("luam-sector-terminal-popup-marker-printed"), uid, user);
        return true;
    }

    public bool TrySubmitMarkerTask(
        EntityUid uid,
        EntityUid user,
        out string result,
        LuaMDynamicEventMarkerComponent? marker = null)
    {
        result = string.Empty;

        if (!Resolve(uid, ref marker, false))
            return false;

        if (!TryGetOpenRuntimeMarkerStory(marker.Story, out var story) ||
            story == null)
        {
            result = BuildInactiveMarkerSubmitResult(marker.Story);
            _popup.PopupEntity(result, uid, user);
            return false;
        }

        var actor = Name(user);
        var storyId = story.Story.ToString();
        var driftMarkersCleared = ClearSensorDriftMarkersForStory(storyId);
        var conditionHazardsCleared = ClearConditionHazardsForStory(storyId);

        marker.RoutePingCount = Math.Max(marker.RoutePingCount, RouteStabilizationPingThreshold);
        marker.LastRoutePingActor = actor;
        marker.LastRoutePingSummary = Loc.GetString("luam-sector-terminal-marker-submit-summary",
            ("actor", actor),
            ("location", marker.MarkerLocation),
            ("drift", driftMarkersCleared),
            ("hazards", conditionHazardsCleared));
        marker.SiteSurveyApplied = true;
        marker.SiteSurveySummary = marker.LastRoutePingSummary;

        if (TryComp<NavMapBeaconComponent>(uid, out var nav))
        {
            if (string.IsNullOrWhiteSpace(marker.RoutePingBaseLabel))
                marker.RoutePingBaseLabel = GetRoutePingBaseLabel(uid, nav);

            _navMap.SetBeaconText(uid, $"{marker.RoutePingBaseLabel} [CLOSED]", nav);
            _navMap.SetBeaconColor(uid, Color.FromHex("#7CFF6BFF"), nav);
            _navMap.SetBeaconEnabled(uid, true, nav);
        }

        var note = BuildStabilizedRouteEvidenceNote(marker);
        var changed = _stories.TryResolveStory(marker.Story, actor, note);
        result = Loc.GetString(changed
                ? "luam-sector-terminal-marker-submit-done"
                : "luam-sector-terminal-marker-submit-already",
            ("title", story.Title),
            ("location", marker.MarkerLocation));
        _popup.PopupEntity(result, user, user);
        return changed;
    }

    public bool TrySubmitSiteTask(
        EntityUid uid,
        EntityUid user,
        out string result,
        LuaMDynamicEventSiteObjectComponent? site = null)
    {
        result = string.Empty;

        if (!Resolve(uid, ref site, false))
            return false;

        if (!TryGetOpenRuntimeMarkerStory(site.Story, out var story) ||
            story == null)
        {
            result = BuildInactiveMarkerSubmitResult(site.Story);
            _popup.PopupEntity(result, uid, user);
            return false;
        }

        if (TryFindDynamicMarker(story, out var markerUid, out var marker))
            return TrySubmitMarkerTask(markerUid, user, out result, marker);

        var actor = Name(user);
        var note = Loc.GetString("luam-sector-terminal-site-submit-summary",
            ("actor", actor),
            ("location", site.MarkerLocation),
            ("kind", site.SiteObjectKind));
        var changed = _stories.TryResolveStory(site.Story, actor, note);
        result = Loc.GetString(changed
                ? "luam-sector-terminal-marker-submit-done"
                : "luam-sector-terminal-marker-submit-already",
            ("title", story.Title),
            ("location", site.MarkerLocation));
        _popup.PopupEntity(result, user, user);
        return changed;
    }

    private void ScheduleNextAutomaticEvent()
    {
        var status = _stories.GetStatusSnapshot();
        _scheduledDispatchReductionPercent = GetDispatchCooldownReductionPercent(GetDispatchReputationScore(status));
        var (minCooldown, maxCooldown) = GetAdjustedCooldownRange(_scheduledDispatchReductionPercent);
        _nextAutomaticEvent = _timing.CurTime +
                              TimeSpan.FromSeconds(_random.Next(minCooldown, maxCooldown + 1));
    }

    private string BuildNextAutomaticEventText(bool inRound)
    {
        if (!inRound)
            return Loc.GetString("luam-sector-terminal-next-round-inactive");

        if (!_cfg.GetCVar(CCVars.LuaMDynamicEventsEnabled))
            return Loc.GetString("luam-sector-terminal-next-disabled");

        if (_nextAutomaticEvent == TimeSpan.Zero)
            return Loc.GetString("luam-sector-terminal-next-initial-delay", ("minutes", InitialDelaySeconds / 60));

        var remaining = _nextAutomaticEvent - _timing.CurTime;
        if (remaining <= TimeSpan.Zero)
            return Loc.GetString("luam-sector-terminal-next-due-now");

        return Loc.GetString("luam-sector-terminal-next-minutes", ("minutes", (int) Math.Ceiling(remaining.TotalMinutes)));
    }

    private string BuildRequestBlockReason(
        bool inRound,
        int playerCount,
        bool hasOpenRuntimeLead,
        bool dynamicEventsEnabled,
        bool atSiteCapacity)
    {
        if (!inRound)
            return Loc.GetString("luam-sector-terminal-request-block-round-inactive");

        if (!dynamicEventsEnabled)
            return Loc.GetString("luam-sector-terminal-request-block-disabled");

        if (playerCount <= 0)
            return Loc.GetString("luam-sector-terminal-request-block-no-operators");

        if (hasOpenRuntimeLead)
            return Loc.GetString("luam-sector-terminal-request-block-open-lead");

        if (atSiteCapacity)
            return Loc.GetString("luam-sector-terminal-request-block-site-capacity");

        return string.Empty;
    }

    private string BuildRoutePingBlockReason(
        bool hasOpenRuntimeLead,
        bool hasActiveRouteMarker,
        bool activeRouteHasBeacon,
        bool activeRouteStabilized,
        bool activeRouteFieldPacketPrinted,
        bool hasCommsBlackout,
        bool activeRouteHasSurveyCommsRelay)
    {
        if (!hasOpenRuntimeLead)
            return Loc.GetString("luam-sector-terminal-route-block-no-open");

        if (!hasActiveRouteMarker)
            return Loc.GetString("luam-sector-terminal-route-block-no-marker");

        if (!activeRouteHasBeacon)
            return Loc.GetString("luam-sector-terminal-route-block-no-beacon");

        if (activeRouteStabilized && activeRouteFieldPacketPrinted)
            return Loc.GetString("luam-sector-terminal-route-block-stabilized-file-printed");

        if (activeRouteStabilized)
            return Loc.GetString("luam-sector-terminal-route-block-stabilized");

        if (hasCommsBlackout && !activeRouteHasSurveyCommsRelay)
            return Loc.GetString("luam-sector-terminal-route-block-comms-blackout");

        return string.Empty;
    }

    private static string AppendRouteClosureInstruction(string text)
    {
        var instruction = Robust.Shared.Localization.Loc.GetString("luam-sector-terminal-route-closure-instruction");
        if (string.IsNullOrWhiteSpace(text))
            return instruction;

        return text.Contains(instruction, StringComparison.Ordinal)
            ? text
            : $"{text} {instruction}";
    }

    private int CountComponents<T>() where T : IComponent
    {
        var count = 0;
        var query = EntityQueryEnumerator<T>();
        while (query.MoveNext(out _, out _))
        {
            count++;
        }

        return count;
    }

    public bool TryFindDynamicMarker(LuaMSectorStoryRecord story, out EntityUid markerUid)
    {
        return TryFindDynamicMarker(story, out markerUid, out _);
    }

    private bool TryFindDynamicMarker(
        LuaMSectorStoryRecord story,
        out EntityUid markerUid,
        out LuaMDynamicEventMarkerComponent marker)
    {
        var query = EntityQueryEnumerator<LuaMDynamicEventMarkerComponent>();
        while (query.MoveNext(out var uid, out var component))
        {
            if (Terminating(uid) || component.Story != story.Story)
                continue;

            markerUid = uid;
            marker = component;
            return true;
        }

        markerUid = default;
        marker = default!;
        return false;
    }

    private bool TryGetOpenRuntimeMarkerStory(
        ProtoId<LuaMSectorStoryPrototype> storyId,
        out LuaMSectorStoryRecord? story)
    {
        story = GetStoryRecord(storyId.ToString());
        return story != null &&
               !story.Resolved &&
               story.Story.ToString().StartsWith(LuaMSectorStorySystem.RuntimeDistressStoryPrefix, StringComparison.Ordinal);
    }

    private string BuildInactiveMarkerSubmitResult(ProtoId<LuaMSectorStoryPrototype> storyId)
    {
        var story = GetStoryRecord(storyId.ToString());
        if (story != null)
        {
            if (story.Resolved)
            {
                return Loc.GetString("luam-sector-terminal-marker-submit-closed",
                    ("title", story.Title));
            }

            if (!story.Story.ToString().StartsWith(LuaMSectorStorySystem.RuntimeDistressStoryPrefix, StringComparison.Ordinal))
            {
                return Loc.GetString("luam-sector-terminal-marker-submit-not-runtime",
                    ("title", story.Title));
            }
        }

        if (_stories.TryGetOpenRuntimeDistressStory(out var activeStory) && activeStory != null)
        {
            return Loc.GetString("luam-sector-terminal-marker-submit-different-active",
                ("title", activeStory.Title));
        }

        return Loc.GetString("luam-sector-terminal-marker-submit-no-active");
    }

    private int ClearSensorDriftMarkersForStory(string storyId)
    {
        var removed = 0;
        var driftQuery = EntityQueryEnumerator<LuaMDynamicEventSensorDriftComponent>();
        while (driftQuery.MoveNext(out var uid, out var drift))
        {
            if (Terminating(uid) || drift.Story.ToString() != storyId)
                continue;

            QueueDel(uid);
            removed++;
        }

        return removed;
    }

    private int ClearConditionHazardsForStory(string storyId)
    {
        var removed = 0;
        var hazardQuery = EntityQueryEnumerator<LuaMDynamicEventConditionHazardComponent>();
        while (hazardQuery.MoveNext(out var uid, out var hazard))
        {
            if (Terminating(uid) || hazard.Story.ToString() != storyId)
                continue;

            QueueDel(uid);
            removed++;
        }

        return removed;
    }

    private string BuildRoutePingSummary(
        int count,
        string actor,
        string location,
        int driftMarkersCleared,
        int conditionHazardsCleared)
    {
        var summary = Loc.GetString("luam-sector-terminal-route-ping-summary",
            ("count", count),
            ("actor", actor),
            ("location", location));

        if (driftMarkersCleared > 0)
        {
            summary = $"{summary}; {Loc.GetString("luam-sector-terminal-route-ping-drift-cleared",
                ("markers", driftMarkersCleared))}";
        }

        if (count >= RouteStabilizationPingThreshold)
        {
            summary = $"{summary}; {Loc.GetString("luam-sector-terminal-route-ping-stabilized-summary",
                ("hazards", conditionHazardsCleared))}";
        }

        return summary;
    }

    private string BuildSiteSurveyRoutePingSummary(
        int count,
        string actor,
        LuaMDynamicEventSiteObjectComponent site,
        int driftMarkersCleared,
        int conditionHazardsCleared,
        bool hasCommsBlackout,
        bool inheritedRelayAvailable)
    {
        var summary = BuildRoutePingSummary(
            count,
            actor,
            site.MarkerLocation,
            driftMarkersCleared,
            conditionHazardsCleared);

        summary = $"{summary}; site survey: {site.SiteObjectKind}";
        if (hasCommsBlackout)
        {
            summary = $"{summary}; local survey bypassed comms blackout";
            if (inheritedRelayAvailable)
                summary = $"{summary}; inherited field relay used by site survey";
            else if (count < RouteStabilizationPingThreshold)
                summary = $"{summary}; site survey field relay armed";
        }

        return summary;
    }

    private void MarkSiteSurveyAppliedForStory(string storyId, string summary)
    {
        var siteQuery = EntityQueryEnumerator<LuaMDynamicEventSiteObjectComponent>();
        while (siteQuery.MoveNext(out var uid, out var site))
        {
            if (Terminating(uid) || site.Story.ToString() != storyId)
                continue;

            site.RouteSurveyApplied = true;
            site.RouteSurveySummary = summary;
        }
    }

    private void AppendSiteSurveyToPaper(EntityUid uid, string summary)
    {
        if (!TryComp<PaperComponent>(uid, out var paper))
            return;

        if (paper.Content.Contains("Site survey:", StringComparison.Ordinal))
            return;

        _paper.SetContent((uid, paper), $"{paper.Content}\nSite survey: {summary}");
    }

    private bool FindNavMapBeacon(LuaMDynamicEventMarkerComponent marker, out NavMapBeaconComponent nav)
    {
        var query = EntityQueryEnumerator<LuaMDynamicEventMarkerComponent, NavMapBeaconComponent>();
        while (query.MoveNext(out _, out var component, out var beacon))
        {
            if (component == marker)
            {
                nav = beacon;
                return true;
            }
        }

        nav = default!;
        return false;
    }

    private string GetRoutePingBaseLabel(EntityUid markerUid, NavMapBeaconComponent nav)
    {
        var label = !string.IsNullOrWhiteSpace(nav.Text)
            ? nav.Text
            : nav.DefaultText == null
                ? Name(markerUid)
                : Loc.GetString(nav.DefaultText);

        var pingIndex = label.IndexOf(" [PING ", StringComparison.Ordinal);
        return pingIndex > 0 ? label[..pingIndex] : label;
    }

    private void OnReputationChanged(LuaMSectorReputationChangedEvent ev)
    {
        TightenScheduledAutomaticEventForDispatchProfile();
    }

    private void TightenScheduledAutomaticEventForDispatchProfile()
    {
        if (_ticker.RunLevel != GameRunLevel.InRound ||
            _nextAutomaticEvent == TimeSpan.Zero ||
            _nextAutomaticEvent <= _timing.CurTime)
        {
            return;
        }

        var status = _stories.GetStatusSnapshot();
        var nextReduction = GetDispatchCooldownReductionPercent(GetDispatchReputationScore(status));
        if (nextReduction <= _scheduledDispatchReductionPercent ||
            _scheduledDispatchReductionPercent >= 100)
        {
            return;
        }

        var remaining = _nextAutomaticEvent - _timing.CurTime;
        var previousScale = 100 - _scheduledDispatchReductionPercent;
        var nextScale = 100 - nextReduction;
        var tightenedTicks = remaining.Ticks * nextScale / previousScale;
        _nextAutomaticEvent = _timing.CurTime + TimeSpan.FromTicks(Math.Max(TimeSpan.FromMinutes(1).Ticks, tightenedTicks));
        _scheduledDispatchReductionPercent = nextReduction;
    }

    private void OnStoryResolved(LuaMSectorStoryResolvedEvent ev)
    {
        TryEnqueueRouteCalibrationSource(ev.Note);

        var query = EntityQueryEnumerator<LuaMDynamicEventMarkerComponent>();
        while (query.MoveNext(out var uid, out var marker))
        {
            if (marker.Story == ev.Story && !Terminating(uid))
                QueueDel(uid);
        }

        var siteQuery = EntityQueryEnumerator<LuaMDynamicEventSiteObjectComponent>();
        while (siteQuery.MoveNext(out var uid, out var site))
        {
            if (site.Story == ev.Story && !Terminating(uid))
                RemCompDeferred<LuaMDynamicEventSiteObjectComponent>(uid);
        }

        var hazardQuery = EntityQueryEnumerator<LuaMDynamicEventConditionHazardComponent>();
        while (hazardQuery.MoveNext(out var uid, out var hazard))
        {
            if (hazard.Story == ev.Story && !Terminating(uid))
                QueueDel(uid);
        }

        var driftQuery = EntityQueryEnumerator<LuaMDynamicEventSensorDriftComponent>();
        while (driftQuery.MoveNext(out var uid, out var drift))
        {
            if (drift.Story == ev.Story && !Terminating(uid))
                QueueDel(uid);
        }

        var debrisQuery = EntityQueryEnumerator<LuaMDynamicEventDebrisComponent>();
        while (debrisQuery.MoveNext(out var uid, out var debris))
        {
            if (debris.Story == ev.Story && !Terminating(uid))
                QueueDeleteDebrisSite(uid, debris);
        }
    }

    private void QueueDeleteDebrisSite(EntityUid uid, LuaMDynamicEventDebrisComponent debris)
    {
        foreach (var hostileUid in debris.HostileUids)
        {
            if (!hostileUid.IsValid() ||
                !EntityManager.EntityExists(hostileUid) ||
                Terminating(hostileUid))
                continue;

            QueueDel(hostileUid);
        }

        if (!Terminating(uid))
            QueueDel(uid);
    }

    private void OnGetMarkerVerbs(EntityUid uid, LuaMDynamicEventMarkerComponent component, GetVerbsEvent<InteractionVerb> args)
    {
        if (!args.CanInteract || !args.CanAccess)
            return;

        args.Verbs.Add(new InteractionVerb
        {
            IconEntity = GetNetEntity(uid),
            Text = Loc.GetString("luam-sector-terminal-marker-submit-verb"),
            Priority = 3,
            Act = () => TrySubmitMarkerTask(uid, args.User, out _, component),
        });

        args.Verbs.Add(new InteractionVerb
        {
            IconEntity = GetNetEntity(uid),
            Text = Loc.GetString("luam-sector-terminal-marker-print-verb"),
            Priority = 1,
            Act = () => TryPrintMarkerFieldPacket(uid, args.User, out _, component),
        });
    }

    private void OnMarkerInteractHand(EntityUid uid, LuaMDynamicEventMarkerComponent component, InteractHandEvent args)
    {
        if (args.Handled)
            return;

        TrySubmitMarkerTask(uid, args.User, out _, component);
        args.Handled = true;
    }

    private void OnGetSiteObjectVerbs(EntityUid uid, LuaMDynamicEventSiteObjectComponent component, GetVerbsEvent<InteractionVerb> args)
    {
        if (!args.CanInteract || !args.CanAccess)
            return;

        args.Verbs.Add(new InteractionVerb
        {
            IconEntity = GetNetEntity(uid),
            Text = Loc.GetString("luam-sector-terminal-marker-submit-verb"),
            Priority = 3,
            Act = () => TrySubmitSiteTask(uid, args.User, out _, component),
        });

        args.Verbs.Add(new InteractionVerb
        {
            IconEntity = GetNetEntity(uid),
            Text = Loc.GetString("luam-sector-terminal-site-survey-verb"),
            Priority = 1,
            Act = () => TrySurveySiteObject(uid, args.User, out _, component),
        });
    }

    private void OnSiteObjectInteractHand(EntityUid uid, LuaMDynamicEventSiteObjectComponent component, InteractHandEvent args)
    {
        if (args.Handled)
            return;

        TrySubmitSiteTask(uid, args.User, out _, component);
        args.Handled = true;
    }

    private void OnMemoryReset(LuaMSectorMemoryResetEvent ev)
    {
        _nextAutomaticEvent = TimeSpan.Zero;
        _nextMarkerSweep = TimeSpan.Zero;
        _scheduledDispatchReductionPercent = 0;
        _allHazardsSeededForRound = false;
        ResetPressureAnnouncementCycle();
        _routeCalibrationSources.Clear();
        _generatedDebrisSites = 0;
        CleanupAllDynamicMarkers();
    }

    private void OnRoundRestartCleanup(RoundRestartCleanupEvent ev)
    {
        _nextAutomaticEvent = TimeSpan.Zero;
        _nextMarkerSweep = TimeSpan.Zero;
        _scheduledDispatchReductionPercent = 0;
        _allHazardsSeededForRound = false;
        ResetPressureAnnouncementCycle();
        _routeCalibrationSources.Clear();
        _generatedDebrisSites = 0;
        CleanupAllDynamicMarkers();
    }

    private void OnGameRunLevelChanged(GameRunLevelChangedEvent ev)
    {
        if (ev.Old == GameRunLevel.InRound && ev.New != GameRunLevel.InRound)
        {
            _nextAutomaticEvent = TimeSpan.Zero;
            _nextMarkerSweep = TimeSpan.Zero;
            _scheduledDispatchReductionPercent = 0;
            _allHazardsSeededForRound = false;
            ResetPressureAnnouncementCycle();
            _routeCalibrationSources.Clear();
            _generatedDebrisSites = 0;
            CleanupAllDynamicMarkers();
        }
    }

    private void EnsureAllHazardsSeeded()
    {
        if (_allHazardsSeededForRound ||
            !_cfg.GetCVar(CCVars.LuaMSectorAllHazardsEnabled))
        {
            return;
        }

        foreach (var preset in AllHazardPresets)
        {
            if (!_stories.TrySeedSectorCondition(
                    preset.Id,
                    preset.Title,
                    preset.Severity,
                    preset.Summary,
                    "LuaM all hazards",
                    out _,
                    out _))
            {
                return;
            }
        }

        _allHazardsSeededForRound = true;
    }

    private void UpdatePressureAnnouncementCycle()
    {
        var status = _stories.GetStatusSnapshot();
        var pressureCondition = GetActiveConditions(status).FirstOrDefault(IsAiWorldPressureCondition);
        if (pressureCondition == null || CountActivePlayers() <= 0)
        {
            ResetPressureAnnouncementCycle();
            return;
        }

        var conditionId = pressureCondition.ConditionId.Trim();
        if (!string.Equals(_pressureAnnouncementConditionId, conditionId, StringComparison.OrdinalIgnoreCase))
        {
            _pressureAnnouncementConditionId = conditionId;
            _pressureAnnouncementPhase = 0;
            _nextPressureAnnouncement = _timing.CurTime + TimeSpan.FromSeconds(PressureAnnouncementInitialDelaySeconds);
            return;
        }

        if (_nextPressureAnnouncement == TimeSpan.Zero)
        {
            _nextPressureAnnouncement = _timing.CurTime + TimeSpan.FromSeconds(PressureAnnouncementInitialDelaySeconds);
            return;
        }

        if (_timing.CurTime < _nextPressureAnnouncement)
            return;

        DispatchPressureAnnouncement(pressureCondition);
        _pressureAnnouncementPhase++;
        _nextPressureAnnouncement = _timing.CurTime + GetPressureAnnouncementInterval(pressureCondition.Severity);
    }

    private void DispatchPressureAnnouncement(LuaMSectorConditionStatus condition)
    {
        if (PressureAnnouncementLocIds.Length == 0)
            return;

        var severity = Math.Clamp(condition.Severity, 1, 5);
        var locId = GetPressureAnnouncementLocId(severity);
        var message = Loc.GetString(
            locId,
            ("condition", condition.Title),
            ("severity", severity));
        var sender = Loc.GetString("luam-sector-pressure-announcer");
        var filter = Filter.Empty().AddWhere(_ticker.UserHasJoinedGame);

        _chat.DispatchFilteredAnnouncement(
            filter,
            message,
            sender: sender,
            playSound: false,
            colorOverride: GetPressureAnnouncementColor(severity));
    }

    private string GetPressureAnnouncementLocId(int severity)
    {
        var phase = _pressureAnnouncementPhase + Math.Max(0, severity - 3);
        if (phase < PressureAnnouncementSustainStart)
            return PressureAnnouncementLocIds[Math.Clamp(phase, 0, PressureAnnouncementLocIds.Length - 1)];

        var sustainCount = PressureAnnouncementLocIds.Length - PressureAnnouncementSustainStart;
        if (sustainCount <= 0)
            return PressureAnnouncementLocIds[^1];

        var sustainPhase = Math.Abs(phase - PressureAnnouncementSustainStart) % sustainCount;
        return PressureAnnouncementLocIds[PressureAnnouncementSustainStart + sustainPhase];
    }

    private TimeSpan GetPressureAnnouncementInterval(int severity)
    {
        severity = Math.Clamp(severity, 1, 5);
        var step = (PressureAnnouncementMaxIntervalSeconds - PressureAnnouncementMinIntervalSeconds) / 4;
        var seconds = PressureAnnouncementMaxIntervalSeconds - (severity - 1) * step;
        var jitter = _random.Next(-30, 31);
        seconds = Math.Clamp(seconds + jitter, PressureAnnouncementMinIntervalSeconds, PressureAnnouncementMaxIntervalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private void ResetPressureAnnouncementCycle()
    {
        _nextPressureAnnouncement = TimeSpan.Zero;
        _pressureAnnouncementPhase = 0;
        _pressureAnnouncementConditionId = null;
    }

    private static Color GetPressureAnnouncementColor(int severity)
    {
        return severity switch
        {
            >= 5 => Color.FromHex("#B985FFFF"),
            4 => Color.FromHex("#C29AFFFF"),
            3 => Color.FromHex("#D0B8FFFF"),
            _ => Color.FromHex("#B6C6FFFF"),
        };
    }

    public int CleanupAllDynamicMarkers()
    {
        var removed = 0;
        var query = EntityQueryEnumerator<LuaMDynamicEventMarkerComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            if (Terminating(uid))
                continue;

            QueueDel(uid);
            removed++;
        }

        var siteQuery = EntityQueryEnumerator<LuaMDynamicEventSiteObjectComponent>();
        while (siteQuery.MoveNext(out var uid, out _))
        {
            if (Terminating(uid))
                continue;

            RemCompDeferred<LuaMDynamicEventSiteObjectComponent>(uid);
            removed++;
        }

        var hazardQuery = EntityQueryEnumerator<LuaMDynamicEventConditionHazardComponent>();
        while (hazardQuery.MoveNext(out var uid, out _))
        {
            if (Terminating(uid))
                continue;

            QueueDel(uid);
            removed++;
        }

        var driftQuery = EntityQueryEnumerator<LuaMDynamicEventSensorDriftComponent>();
        while (driftQuery.MoveNext(out var uid, out _))
        {
            if (Terminating(uid))
                continue;

            QueueDel(uid);
            removed++;
        }

        var debrisQuery = EntityQueryEnumerator<LuaMDynamicEventDebrisComponent>();
        while (debrisQuery.MoveNext(out var uid, out var debris))
        {
            if (Terminating(uid))
                continue;

            QueueDeleteDebrisSite(uid, debris);
            removed++;
        }

        return removed;
    }

    public int CleanupStaleDynamicMarkers()
    {
        var status = _stories.GetStatusSnapshot();
        var openRuntimeStories = status.Hazards
            .Where(hazard => !hazard.Resolved &&
                             hazard.Story.ToString().StartsWith(
                                 LuaMSectorStorySystem.RuntimeDistressStoryPrefix,
                                 StringComparison.Ordinal))
            .Select(hazard => hazard.Story.ToString())
            .ToHashSet(StringComparer.Ordinal);

        var removed = 0;
        var query = EntityQueryEnumerator<LuaMDynamicEventMarkerComponent>();
        while (query.MoveNext(out var uid, out var marker))
        {
            if (Terminating(uid) ||
                openRuntimeStories.Contains(marker.Story.ToString()))
            {
                continue;
            }

            QueueDel(uid);
            removed++;
        }

        var siteQuery = EntityQueryEnumerator<LuaMDynamicEventSiteObjectComponent>();
        while (siteQuery.MoveNext(out var uid, out var site))
        {
            if (Terminating(uid) ||
                openRuntimeStories.Contains(site.Story.ToString()))
            {
                continue;
            }

            RemCompDeferred<LuaMDynamicEventSiteObjectComponent>(uid);
            removed++;
        }

        var hazardQuery = EntityQueryEnumerator<LuaMDynamicEventConditionHazardComponent>();
        while (hazardQuery.MoveNext(out var uid, out var hazard))
        {
            if (Terminating(uid) ||
                openRuntimeStories.Contains(hazard.Story.ToString()))
            {
                continue;
            }

            QueueDel(uid);
            removed++;
        }

        var driftQuery = EntityQueryEnumerator<LuaMDynamicEventSensorDriftComponent>();
        while (driftQuery.MoveNext(out var uid, out var drift))
        {
            if (Terminating(uid) ||
                openRuntimeStories.Contains(drift.Story.ToString()))
            {
                continue;
            }

            QueueDel(uid);
            removed++;
        }

        var debrisQuery = EntityQueryEnumerator<LuaMDynamicEventDebrisComponent>();
        while (debrisQuery.MoveNext(out var uid, out var debris))
        {
            if (Terminating(uid) ||
                openRuntimeStories.Contains(debris.Story.ToString()))
            {
                continue;
            }

            QueueDeleteDebrisSite(uid, debris);
            removed++;
        }

        return removed;
    }

    private MapCoordinates? ResolveMarkerCoordinates(MapCoordinates? markerCoordinates)
    {
        if (markerCoordinates != null)
            return markerCoordinates;

        var service = _sectorService.GetServiceEntity();
        if (!service.IsValid())
            return null;

        return _transform.ToMapCoordinates(Transform(service).Coordinates, logError: false);
    }

    private string FormatMarkerLocation(MapCoordinates? coordinates)
    {
        if (coordinates == null || coordinates == MapCoordinates.Nullspace)
            return "GPS недоступен";

        return FormattableString.Invariant(
            $"GPS карта {coordinates.Value.MapId} x {coordinates.Value.Position.X:0.0} y {coordinates.Value.Position.Y:0.0}");
    }

    private DebrisSitePlan BuildDebrisSitePlan(MapCoordinates? markerCoordinates)
    {
        if (markerCoordinates == null ||
            markerCoordinates == MapCoordinates.Nullspace)
        {
            return new DebrisSitePlan(false, 0, false);
        }

        var maxActiveSerial = _generatedDebrisSites;
        var debrisQuery = EntityQueryEnumerator<LuaMDynamicEventDebrisComponent>();
        while (debrisQuery.MoveNext(out var uid, out var debris))
        {
            if (TerminatingOrDeleted(uid))
                continue;

            maxActiveSerial = Math.Max(maxActiveSerial, debris.DebrisSerial);
        }

        var serial = maxActiveSerial + 1;
        return new DebrisSitePlan(
            true,
            serial,
            serial % DynamicDebrisHostileInterval == 0);
    }

    private static string AppendDebrisContext(string description, DebrisSitePlan debrisPlan)
    {
        if (!debrisPlan.Enabled)
            return description;

        var context = debrisPlan.HostileContact
            ? $"Цель вынесена на отдельный малый обломок #{debrisPlan.Serial}; подтверждены легкие враждебные контакты."
            : $"Цель вынесена на отдельный малый обломок #{debrisPlan.Serial}; штатных врагов на точке не высажено.";

        return string.IsNullOrWhiteSpace(description)
            ? context
            : $"{description.Trim()} {context}";
    }

    private static string AppendDebrisHazardContext(string hazard, DebrisSitePlan debrisPlan)
    {
        if (!debrisPlan.Enabled)
            return hazard;

        var context = debrisPlan.HostileContact
            ? $"Каждый {DynamicDebrisHostileInterval}-й обломок LuaM получает контактную группу: на этой точке ожидаются легкие враги."
            : "Точка создана как изолированный малый обломок LuaM без штатной враждебной группы.";

        return string.IsNullOrWhiteSpace(hazard)
            ? context
            : $"{hazard.Trim()} {context}";
    }

    private string BuildMarkerFieldPacket(LuaMDynamicEventMarkerComponent marker)
    {
        var output = new StringBuilder();
        output.AppendLine("# Полевой пакет динамического события");
        output.AppendLine();
        output.AppendLine($"Сюжет: {marker.Story}");
        output.AppendLine($"Шаблон: {marker.TemplateId}");
        output.AppendLine($"Маркер: {marker.MarkerLocation}");
        output.AppendLine($"Оформил: {marker.CreatedBy}");
        if (!string.IsNullOrWhiteSpace(marker.ConditionRiskSummary))
            output.AppendLine($"Риск условия: {marker.ConditionRiskSummary}");

        var story = GetStoryRecord(marker.Story.ToString());
        if (story != null)
        {
            output.AppendLine($"Зацепка: {story.Title}");
            output.AppendLine($"Судно: {story.ContractVessel}");
            output.AppendLine($"Награда: {story.ContractReward + story.HazardRewardBonus}");
        }

        output.AppendLine();
        output.AppendLine("## Сдача");
        output.AppendLine(Loc.GetString("luam-sector-terminal-route-closure-instruction"));
        output.AppendLine();
        output.AppendLine("## Маршрут");
        output.AppendLine(story?.ContractDescription ?? "Для этого маркера сейчас нет активной записи сюжета сектора.");
        output.AppendLine();
        output.AppendLine("## Полевые проверки");
        AppendLocalEvidenceChecklist(output, marker.TemplateId);
        output.AppendLine();
        AppendRouteStabilizationPacket(output, marker);
        output.AppendLine();
        output.AppendLine("## Опасность");
        output.AppendLine(story == null || string.IsNullOrWhiteSpace(story.Hazard)
            ? "Опасность не записана."
            : story.Hazard);

        return output.ToString();
    }

    private static void AppendRouteStabilizationPacket(StringBuilder output, LuaMDynamicEventMarkerComponent marker)
    {
        output.AppendLine("## Навигационная стабилизация");
        if (marker.RoutePingCount <= 0)
        {
            output.AppendLine("Пинги маршрута еще не проводились.");
            return;
        }

        output.AppendLine($"Пингов маршрута: {marker.RoutePingCount}.");
        if (!string.IsNullOrWhiteSpace(marker.LastRoutePingSummary))
            output.AppendLine(marker.LastRoutePingSummary);

        if (!string.IsNullOrWhiteSpace(marker.RouteCalibrationSource))
        {
            output.AppendLine($"Route calibration source: {marker.RouteCalibrationSource}");
            var chainDepth = GetRouteCalibrationChainDepth(marker.RouteCalibrationSource);
            if (chainDepth > 0)
                output.AppendLine($"Route calibration chain depth: {chainDepth}");
        }

        output.AppendLine(marker.RoutePingCount >= RouteStabilizationPingThreshold
            ? "Коридор маршрута стабилизирован; этот пакет можно подать как закрывающее доказательство."
            : $"Для закрывающего доказательства нужно пингов: {RouteStabilizationPingThreshold}.");
        if (marker.RoutePingCount >= RouteStabilizationPingThreshold)
            output.AppendLine($"Evidence filing note: {BuildStabilizedRouteEvidenceNote(marker)}");
    }

    private void AddStabilizedRouteEvidence(EntityUid report, LuaMDynamicEventMarkerComponent marker)
    {
        if (marker.RoutePingCount < RouteStabilizationPingThreshold)
            return;

        var evidence = AddComp<LuaMSectorEvidenceComponent>(report);
        evidence.Story = marker.Story;
        evidence.AcknowledgeHazard = true;
        evidence.ResolveStory = true;
        evidence.Note = BuildStabilizedRouteEvidenceNote(marker);
    }

    public static string BuildStabilizedRouteEvidenceNote(LuaMDynamicEventMarkerComponent marker)
    {
        var source = $"{marker.TemplateId}; {marker.MarkerLocation}";
        if (marker.SiteSurveyApplied)
        {
            var surveySource = marker.LastRoutePingSummary.Contains("inherited field relay used", StringComparison.Ordinal)
                ? "site survey; inherited field relay used"
                : marker.LastRoutePingSummary.Contains("site survey field relay used", StringComparison.Ordinal)
                ? "site survey field relay used"
                : "site survey";
            source = $"{source}; {surveySource}";
        }
        else if (marker.RouteCalibrationRelayInherited &&
                 marker.LastRoutePingSummary.Contains("site survey field relay used", StringComparison.Ordinal))
        {
            source = $"{source}; inherited field relay used";
        }

        var handoffChainDepth = GetRouteCalibrationChainDepth(marker.RouteCalibrationSource) + 1;
        source = $"{source}; handoff route calibration chain depth: {handoffChainDepth}";

        if (!string.IsNullOrWhiteSpace(marker.RouteCalibrationSource))
            source = $"{source}; calibrated from {marker.RouteCalibrationSource}";

        return $"{StabilizedRouteEvidenceNotePrefix}: {TrimRouteCalibrationSource(source)}";
    }

    private bool TryEnqueueRouteCalibrationSource(string note)
    {
        if (_routeCalibrationSources.Count >= MaxRouteCalibrationCredits)
            return false;

        if (!TryBuildRouteCalibrationSource(note, out var source))
            return false;

        _routeCalibrationSources.Enqueue(TrimRouteCalibrationSource(source));
        return true;
    }

    private string GetQueuedRouteCalibrationSource()
    {
        return _routeCalibrationSources.TryPeek(out var source)
            ? source ?? string.Empty
            : string.Empty;
    }

    private static bool TryBuildRouteCalibrationSource(string note, out string source)
    {
        source = string.Empty;
        var prefixIndex = note.IndexOf(StabilizedRouteEvidenceNotePrefix, StringComparison.Ordinal);
        if (prefixIndex < 0)
            return false;

        source = note[(prefixIndex + StabilizedRouteEvidenceNotePrefix.Length)..].Trim();
        if (source.StartsWith(":", StringComparison.Ordinal))
            source = source[1..].Trim();

        if (string.IsNullOrWhiteSpace(source))
            source = "stabilized route field packet";

        return true;
    }

    private static string TrimRouteCalibrationSource(string source)
    {
        return source.Length <= MaxRouteCalibrationSourceLength
            ? source
            : source[..MaxRouteCalibrationSourceLength];
    }

    public static int GetRouteCalibrationChainDepth(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return 0;

        var depth = 1;
        var start = 0;
        const string ChainMarker = "calibrated from";
        while (true)
        {
            var index = source.IndexOf(ChainMarker, start, StringComparison.Ordinal);
            if (index < 0)
                return Math.Max(depth, GetRouteCalibrationChainDepthHint(source));

            depth++;
            start = index + ChainMarker.Length;
        }
    }

    private static int GetRouteCalibrationChainDepthHint(string source)
    {
        return Math.Max(
            GetRouteCalibrationChainDepthHint(source, "handoff route calibration chain depth:"),
            GetRouteCalibrationChainDepthHint(source, "route calibration chain depth:"));
    }

    private static int GetRouteCalibrationChainDepthHint(string source, string marker)
    {
        var maxDepth = 0;
        var start = 0;
        while (true)
        {
            var index = source.IndexOf(marker, start, StringComparison.Ordinal);
            if (index < 0)
                return maxDepth;

            var numberStart = index + marker.Length;
            while (numberStart < source.Length && source[numberStart] == ' ')
                numberStart++;

            var numberEnd = numberStart;
            while (numberEnd < source.Length && char.IsDigit(source[numberEnd]))
                numberEnd++;

            if (numberEnd > numberStart &&
                int.TryParse(source[numberStart..numberEnd], out var depth) &&
                depth > maxDepth)
            {
                maxDepth = depth;
            }

            start = numberEnd > index ? numberEnd : index + marker.Length;
        }
    }

    private static int GetRouteCalibrationRewardBonus(string source)
    {
        var chainDepth = GetRouteCalibrationChainDepth(source);
        if (chainDepth <= 0)
            return 0;

        return Math.Min(chainDepth * RouteCalibrationRewardStep, RouteCalibrationRewardCap);
    }

    private static int GetRouteCalibrationClosureRewardBonus(string source)
    {
        var chainDepth = GetRouteCalibrationChainDepth(source);
        if (chainDepth <= 0)
            return 0;

        return Math.Min(chainDepth * RouteCalibrationClosureRewardStep, RouteCalibrationClosureRewardCap);
    }

    private static int GetRouteCalibrationRadiationIntensity(int severity, int routeCalibrationChainDepth)
    {
        var normalizedSeverity = Math.Clamp(severity, 1, 5);
        if (routeCalibrationChainDepth <= 0)
            return normalizedSeverity;

        return Math.Max(
            RouteCalibrationRadiationMinimumIntensity,
            normalizedSeverity - routeCalibrationChainDepth * RouteCalibrationRadiationDampingStep);
    }

    private static int GetRouteCalibrationRadiationDampingPreview(LuaMSectorStatusSnapshot status, string source)
    {
        var chainDepth = GetRouteCalibrationChainDepth(source);
        if (chainDepth <= 0)
            return 0;

        var radiationCondition = GetConditionHazardCondition(status);
        if (radiationCondition == null)
            return 0;

        var severity = Math.Clamp(radiationCondition.Severity, 1, 5);
        return severity - GetRouteCalibrationRadiationIntensity(severity, chainDepth);
    }

    private static bool GetRouteCalibrationSensorDriftSuppressionPreview(LuaMSectorStatusSnapshot status, string source)
    {
        return GetRouteCalibrationChainDepth(source) > 0 &&
               GetSensorDriftCondition(status) != null;
    }

    private static Color GetRouteCalibrationBeaconColor(string source)
    {
        return GetRouteCalibrationChainDepth(source) > 1
            ? Color.FromHex("#B985FFFF")
            : Color.FromHex("#00E5FFFF");
    }

    private static void AppendLocalEvidenceChecklist(StringBuilder output, string templateId)
    {
        output.AppendLine("[ ] позиция маркера достигнута");
        output.AppendLine("[ ] локальная опасность проверена");

        if (templateId == MonolithArtifactTemplateId)
        {
            output.AppendLine("[ ] осколок Монолита просканирован");
            output.AppendLine("[ ] контейнер подготовлен");
            output.AppendLine("[ ] показание резонатора записано");
            output.AppendLine("[ ] исследовательский отчет оформлен на месте артефакта");
            return;
        }

        output.AppendLine("[ ] результат по спасению, грузу, буксировке, выжившим или реестру записан");
        output.AppendLine("[ ] отчет закрытия оформлен через доску сектора");
    }

    private LuaMSectorStoryRecord? GetStoryRecord(string storyId)
    {
        if (!_stories.TryGetSectorMemory(out var records, out _, out _))
            return null;

        return records.FirstOrDefault(record => record.Story.ToString() == storyId);
    }

    private void SpawnDebrisSite(
        DynamicEventTemplate template,
        LuaMSectorStoryRecord? record,
        string actor,
        MapCoordinates? markerCoordinates,
        string markerLocation,
        DebrisSitePlan debrisPlan)
    {
        if (record == null ||
            !debrisPlan.Enabled ||
            markerCoordinates == null ||
            markerCoordinates == MapCoordinates.Nullspace)
        {
            return;
        }

        if (!_prototypes.TryIndex<EntityPrototype>(DynamicQuestDebrisPrototype, out _))
            return;

        var debris = Spawn(DynamicQuestDebrisPrototype, markerCoordinates.Value);
        var debrisComponent = AddComp<LuaMDynamicEventDebrisComponent>(debris);
        debrisComponent.Story = record.Story;
        debrisComponent.TemplateId = template.Id;
        debrisComponent.CreatedBy = actor;
        debrisComponent.MarkerLocation = markerLocation;
        debrisComponent.DebrisSerial = debrisPlan.Serial;
        debrisComponent.HostileContact = debrisPlan.HostileContact;

        _generatedDebrisSites = Math.Max(_generatedDebrisSites, debrisPlan.Serial);

        if (debrisPlan.HostileContact)
        {
            debrisComponent.HostileUids.AddRange(SpawnDebrisHostiles(markerCoordinates.Value, debrisPlan.Serial));
            debrisComponent.HostileCount = debrisComponent.HostileUids.Count;
        }
    }

    private List<EntityUid> SpawnDebrisHostiles(MapCoordinates markerCoordinates, int debrisSerial)
    {
        var spawned = new List<EntityUid>();
        for (var i = 0; i < 2; i++)
        {
            var prototype = DebrisHostilePrototypes[(debrisSerial + i) % DebrisHostilePrototypes.Length];
            if (!_prototypes.TryIndex<EntityPrototype>(prototype, out _))
                continue;

            var offset = DebrisHostileOffsets[i % DebrisHostileOffsets.Length];
            spawned.Add(Spawn(prototype, new MapCoordinates(markerCoordinates.Position + offset, markerCoordinates.MapId)));
        }

        return spawned;
    }

    private bool SpawnWorldMarker(
        DynamicEventTemplate template,
        LuaMSectorStoryRecord? record,
        string actor,
        MapCoordinates? markerCoordinates,
        string markerLocation,
        string conditionRiskSummary,
        int conditionSeverity,
        LuaMSectorStatusSnapshot status,
        out string routeCalibrationSource)
    {
        routeCalibrationSource = string.Empty;

        if (record == null)
            return false;

        var markerPrototype = string.IsNullOrWhiteSpace(template.MarkerPrototype)
            ? MarkerPrototype
            : template.MarkerPrototype;

        var marker = markerCoordinates == null
            ? Spawn(markerPrototype, MapCoordinates.Nullspace)
            : Spawn(markerPrototype, markerCoordinates.Value);

        var markerComponent = AddComp<LuaMDynamicEventMarkerComponent>(marker);
        markerComponent.Story = record.Story;
        markerComponent.TemplateId = template.Id;
        markerComponent.CreatedBy = actor;
        markerComponent.MarkerLocation = markerLocation;
        markerComponent.ConditionRiskSummary = conditionRiskSummary;
        SetTaskMarkerMetadata(marker, record, markerLocation);

        if (!string.IsNullOrWhiteSpace(conditionRiskSummary) &&
            TryComp<NavMapBeaconComponent>(marker, out var nav))
        {
            var label = nav.DefaultText == null
                ? $"динамический маркер сектора [{GetConditionNavSuffix(conditionRiskSummary)}]"
                : $"{Loc.GetString(nav.DefaultText)} [{GetConditionNavSuffix(conditionRiskSummary)}]";
            _navMap.SetBeaconText(marker, label, nav);
            _navMap.SetBeaconColor(marker, GetConditionMarkerColor(conditionSeverity), nav);

            if (HasCommsBlackoutCondition(status))
                _navMap.SetBeaconEnabled(marker, false, nav);
        }

        var routeCalibrationApplied = TryConsumeRouteCalibrationCredit(marker, markerComponent, markerLocation, out routeCalibrationSource);
        if (routeCalibrationApplied && GetActiveConditions(status).Any(IsSensorDriftCondition))
        {
            markerComponent.LastRoutePingSummary =
                $"{markerComponent.LastRoutePingSummary}; эхо дрейфа сенсоров подавлено калибровкой";
        }

        return routeCalibrationApplied;
    }

    private bool TryConsumeRouteCalibrationCredit(
        EntityUid markerUid,
        LuaMDynamicEventMarkerComponent marker,
        string markerLocation,
        out string source)
    {
        source = string.Empty;

        if (_routeCalibrationSources.Count <= 0 || marker.RoutePingCount > 0)
            return false;

        source = _routeCalibrationSources.Dequeue();
        marker.RoutePingCount = 1;
        marker.LastRoutePingActor = "route calibration";
        marker.RouteCalibrationSource = source;
        marker.LastRoutePingSummary = $"Калибровка маршрута из закрытого пакета: {markerLocation}; source packet: {source}";
        var inheritedRelayArmed = false;
        if (source.Contains("site survey field relay used", StringComparison.Ordinal) ||
            source.Contains("inherited field relay used", StringComparison.Ordinal))
        {
            marker.SiteSurveyCommsRelayAvailable = true;
            marker.RouteCalibrationRelayInherited = true;
            marker.LastRoutePingSummary = $"{marker.LastRoutePingSummary}; inherited field relay armed";
            inheritedRelayArmed = true;
        }

        if (!TryComp<NavMapBeaconComponent>(markerUid, out var nav))
            return true;

        if (string.IsNullOrWhiteSpace(marker.RoutePingBaseLabel))
            marker.RoutePingBaseLabel = GetRoutePingBaseLabel(markerUid, nav);

        _navMap.SetBeaconText(markerUid, $"{marker.RoutePingBaseLabel} [PING {marker.RoutePingCount}]", nav);
        _navMap.SetBeaconColor(markerUid, GetRouteCalibrationBeaconColor(source), nav);
        if (inheritedRelayArmed)
            _navMap.SetBeaconEnabled(markerUid, true, nav);

        return true;
    }

    private void SpawnSiteNote(
        DynamicEventTemplate template,
        LuaMSectorStoryRecord? record,
        string actor,
        MapCoordinates? markerCoordinates,
        string markerLocation,
        string conditionRiskSummary,
        string routeCalibrationSource)
    {
        if (record == null)
            return;

        var siteNote = markerCoordinates == null
            ? Spawn(SiteNotePrototype, MapCoordinates.Nullspace)
            : Spawn(SiteNotePrototype, markerCoordinates.Value);

        var siteComponent = AddComp<LuaMDynamicEventSiteObjectComponent>(siteNote);
        siteComponent.Story = record.Story;
        siteComponent.TemplateId = template.Id;
        siteComponent.CreatedBy = actor;
        siteComponent.MarkerLocation = markerLocation;
        siteComponent.ConditionRiskSummary = conditionRiskSummary;
        siteComponent.RouteCalibrationSource = routeCalibrationSource;
        SetTaskSiteNoteMetadata(siteNote, record, markerLocation);

        var evidence = AddComp<LuaMSectorEvidenceComponent>(siteNote);
        evidence.Story = record.Story;
        evidence.AcknowledgeHazard = true;
        evidence.ResolveStory = true;
        evidence.Note = Loc.GetString("luam-sector-terminal-site-note-evidence-summary",
            ("title", record.Title),
            ("location", markerLocation));

        if (!TryComp<PaperComponent>(siteNote, out var paper))
        {
            QueueDel(siteNote);
            return;
        }

        _paper.SetContent((siteNote, paper), BuildSiteNote(siteComponent, record));
    }

    private void SetTaskMarkerMetadata(EntityUid uid, LuaMSectorStoryRecord record, string markerLocation)
    {
        _metaData.SetEntityName(uid, $"точка сдачи LuaM: {record.Title}");
        _metaData.SetEntityDescription(
            uid,
            $"Активный маркер задания LuaM. Кликните по маркеру или выберите \"Сдать / закрыть задание\", чтобы завершить зацепку. Координаты: {markerLocation}.");
    }

    private void SetTaskSiteNoteMetadata(EntityUid uid, LuaMSectorStoryRecord record, string markerLocation)
    {
        _metaData.SetEntityName(uid, $"акт сдачи LuaM: {record.Title}");
        _metaData.SetEntityDescription(
            uid,
            $"Полевой акт задания LuaM. Кликните по нему или выберите \"Сдать / закрыть задание\", чтобы закрыть активную зацепку. Координаты: {markerLocation}.");
    }

    private void SpawnSiteObjects(
        DynamicEventTemplate template,
        LuaMSectorStoryRecord? record,
        string actor,
        MapCoordinates? markerCoordinates,
        string conditionRiskSummary,
        string routeCalibrationSource)
    {
        if (record == null ||
            template.SiteObjectPrototypes == null ||
            template.SiteObjectPrototypes.Count == 0)
        {
            return;
        }

        for (var i = 0; i < template.SiteObjectPrototypes.Count; i++)
        {
            var prototype = template.SiteObjectPrototypes[i];
            var objectCoordinates = GetSiteObjectCoordinates(markerCoordinates, i);
            var spawned = objectCoordinates == null
                ? Spawn(prototype, MapCoordinates.Nullspace)
                : Spawn(prototype, objectCoordinates.Value);

            var siteComponent = AddComp<LuaMDynamicEventSiteObjectComponent>(spawned);
            siteComponent.Story = record.Story;
            siteComponent.TemplateId = template.Id;
            siteComponent.CreatedBy = actor;
            siteComponent.MarkerLocation = FormatMarkerLocation(objectCoordinates);
            siteComponent.ConditionRiskSummary = conditionRiskSummary;
            siteComponent.RouteCalibrationSource = routeCalibrationSource;
            siteComponent.SiteObjectKind = GetSiteObjectKind(prototype);

            ConfigureSiteObjectPaper(spawned, prototype, siteComponent, record);
        }
    }

    private void ConfigureSiteObjectPaper(
        EntityUid spawned,
        string prototype,
        LuaMDynamicEventSiteObjectComponent site,
        LuaMSectorStoryRecord record)
    {
        if (!TryComp<PaperComponent>(spawned, out var paper))
            return;

        if (prototype != MonolithResearchReportPrototype)
            return;

        _paper.SetContent((spawned, paper), BuildMonolithResearchReport(site, record));
        var evidence = AddComp<LuaMSectorEvidenceComponent>(spawned);
        evidence.Story = record.Story;
        evidence.AcknowledgeHazard = true;
        evidence.ResolveStory = true;
        evidence.Note = $"исследовательский отчет по артефакту Монолита оформлен: {record.Title}";
    }

    private static MapCoordinates? GetSiteObjectCoordinates(MapCoordinates? markerCoordinates, int index)
    {
        if (markerCoordinates == null ||
            markerCoordinates == MapCoordinates.Nullspace)
        {
            return markerCoordinates;
        }

        var offset = SiteObjectOffsets[index % SiteObjectOffsets.Length];
        return new MapCoordinates(markerCoordinates.Value.Position + offset, markerCoordinates.Value.MapId);
    }

    private static string GetSiteObjectKind(string prototype)
    {
        return prototype switch
        {
            "LuaMMonolithShard" => "осколок Монолита",
            "LuaMArtifactContainmentCase" => "контейнер",
            "LuaMAnomalyScanner" => "сканер аномалий",
            "LuaMMonolithResonator" => "резонатор Монолита",
            MonolithResearchReportPrototype => "исследовательский отчет",
            _ => "объект точки",
        };
    }

    private void SpawnSensorDriftMarker(
        DynamicEventTemplate template,
        LuaMSectorStoryRecord? record,
        string actor,
        MapCoordinates? markerCoordinates,
        LuaMSectorStatusSnapshot status,
        bool suppressCalibratedEcho = false)
    {
        if (record == null ||
            markerCoordinates == null ||
            markerCoordinates == MapCoordinates.Nullspace)
        {
            return;
        }

        var driftCondition = GetSensorDriftCondition(status);
        if (driftCondition == null)
            return;

        if (suppressCalibratedEcho)
            return;

        var severity = Math.Clamp(driftCondition.Severity, 1, 5);
        var driftOffset = new Vector2(severity * 1.5f, -severity);
        var driftCoordinates = new MapCoordinates(markerCoordinates.Value.Position + driftOffset, markerCoordinates.Value.MapId);
        var driftLocation = FormatMarkerLocation(driftCoordinates);
        var isWorldPressure = IsAiWorldPressureCondition(driftCondition);

        var driftMarker = Spawn(ConditionSensorDriftMarkerPrototype, driftCoordinates);
        var driftComponent = AddComp<LuaMDynamicEventSensorDriftComponent>(driftMarker);
        driftComponent.Story = record.Story;
        driftComponent.TemplateId = template.Id;
        driftComponent.ConditionId = driftCondition.ConditionId;
        driftComponent.CreatedBy = actor;
        driftComponent.DriftLocation = driftLocation;

        if (TryComp<NavMapBeaconComponent>(driftMarker, out var nav))
        {
            var text = isWorldPressure
                ? $"AI pressure echo [SC-{severity}]"
                : $"эхо дрейфа сенсоров [SC-{severity}]";
            var color = isWorldPressure
                ? Color.FromHex("#B985FFFF")
                : Color.FromHex("#FF9F1C99");
            _navMap.SetBeaconText(driftMarker, text, nav);
            _navMap.SetBeaconColor(driftMarker, color, nav);
        }
    }

    private void SpawnConditionHazards(
        DynamicEventTemplate template,
        LuaMSectorStoryRecord? record,
        string actor,
        MapCoordinates? markerCoordinates,
        string markerLocation,
        LuaMSectorStatusSnapshot status,
        string routeCalibrationSource = "")
    {
        if (record == null)
            return;

        var hazardCondition = GetConditionHazardCondition(status);
        if (hazardCondition == null)
            return;

        var hazard = markerCoordinates == null
            ? Spawn(ConditionRadiationHazardPrototype, MapCoordinates.Nullspace)
            : Spawn(ConditionRadiationHazardPrototype, markerCoordinates.Value);

        var hazardComponent = AddComp<LuaMDynamicEventConditionHazardComponent>(hazard);
        hazardComponent.Story = record.Story;
        hazardComponent.TemplateId = template.Id;
        hazardComponent.ConditionId = hazardCondition.ConditionId;
        hazardComponent.CreatedBy = actor;
        hazardComponent.MarkerLocation = markerLocation;
        hazardComponent.RouteCalibrationSource = routeCalibrationSource;
        hazardComponent.RouteCalibrationChainDepth = GetRouteCalibrationChainDepth(routeCalibrationSource);

        var severity = Math.Clamp(hazardCondition.Severity, 1, 5);
        var intensity = GetRouteCalibrationRadiationIntensity(severity, hazardComponent.RouteCalibrationChainDepth);
        hazardComponent.RouteCalibrationRadiationDamping = severity - intensity;
        if (TryComp<RadiationSourceComponent>(hazard, out var radiation))
        {
            radiation.Intensity = intensity;
        }

        ConfigureConditionHazardBeacon(hazard, severity, intensity, hazardComponent, hazardCondition);
    }

    private void ConfigureConditionHazardBeacon(
        EntityUid hazard,
        int severity,
        int intensity,
        LuaMDynamicEventConditionHazardComponent hazardComponent,
        LuaMSectorConditionStatus condition)
    {
        if (!TryComp<NavMapBeaconComponent>(hazard, out var nav))
            return;

        var label = IsAiWorldPressureCondition(condition) && !IsRadiationCondition(condition)
            ? "AI pressure hazard"
            : "radiation hazard";
        var text = hazardComponent.RouteCalibrationRadiationDamping > 0
            ? $"{label} [SC-{severity}->RAD-{intensity}] [DAMPED -{hazardComponent.RouteCalibrationRadiationDamping}]"
            : $"{label} [SC-{severity}]";
        var color = hazardComponent.RouteCalibrationRadiationDamping > 0
            ? GetRouteCalibrationBeaconColor(hazardComponent.RouteCalibrationSource)
            : GetConditionMarkerColor(severity);

        _navMap.SetBeaconText(hazard, text, nav);
        _navMap.SetBeaconColor(hazard, color, nav);
    }

    private static string BuildSiteNote(
        LuaMDynamicEventSiteObjectComponent site,
        LuaMSectorStoryRecord record)
    {
        var output = new StringBuilder();
        output.AppendLine("# Заметка места динамического события");
        output.AppendLine();
        output.AppendLine($"Сюжет: {site.Story}");
        output.AppendLine($"Шаблон: {site.TemplateId}");
        output.AppendLine($"Точка: {site.MarkerLocation}");
        output.AppendLine($"Оформил: {site.CreatedBy}");
        if (!string.IsNullOrWhiteSpace(site.RouteCalibrationSource))
        {
            output.AppendLine($"Route calibration source: {site.RouteCalibrationSource}");
            var chainDepth = GetRouteCalibrationChainDepth(site.RouteCalibrationSource);
            if (chainDepth > 0)
                output.AppendLine($"Route calibration chain depth: {chainDepth}");
        }
        if (!string.IsNullOrWhiteSpace(site.ConditionRiskSummary))
            output.AppendLine($"Риск условия: {site.ConditionRiskSummary}");
        output.AppendLine($"Зацепка: {record.Title}");
        output.AppendLine($"Судно: {record.ContractVessel}");
        output.AppendLine($"Награда: {record.ContractReward + record.HazardRewardBonus}");
        output.AppendLine();
        output.AppendLine("## Сдача");
        output.AppendLine(Robust.Shared.Localization.Loc.GetString("luam-sector-terminal-route-closure-instruction"));
        output.AppendLine();
        output.AppendLine("## Маршрут");
        output.AppendLine(record.ContractDescription);
        output.AppendLine();
        output.AppendLine("## Локальные доказательства для поиска");
        AppendLocalEvidenceChecklist(output, site.TemplateId);
        output.AppendLine();
        output.AppendLine("## Опасность");
        output.AppendLine(string.IsNullOrWhiteSpace(record.Hazard) ? "Опасность не записана." : record.Hazard);

        return output.ToString();
    }

    private static string BuildMonolithResearchReport(
        LuaMDynamicEventSiteObjectComponent site,
        LuaMSectorStoryRecord record)
    {
        var output = new StringBuilder();
        output.AppendLine("# Исследовательский отчет LuaM по Монолиту");
        output.AppendLine();
        output.AppendLine($"Сюжет: {site.Story}");
        output.AppendLine($"Шаблон: {site.TemplateId}");
        output.AppendLine($"Точка: {site.MarkerLocation}");
        output.AppendLine($"Оформил: {site.CreatedBy}");
        if (!string.IsNullOrWhiteSpace(site.RouteCalibrationSource))
        {
            output.AppendLine($"Route calibration source: {site.RouteCalibrationSource}");
            var chainDepth = GetRouteCalibrationChainDepth(site.RouteCalibrationSource);
            if (chainDepth > 0)
                output.AppendLine($"Route calibration chain depth: {chainDepth}");
        }
        if (!string.IsNullOrWhiteSpace(site.ConditionRiskSummary))
            output.AppendLine($"Риск условия: {site.ConditionRiskSummary}");
        output.AppendLine($"Зацепка: {record.Title}");
        output.AppendLine($"Судно: {record.ContractVessel}");
        output.AppendLine($"Награда: {record.ContractReward + record.HazardRewardBonus}");
        output.AppendLine();
        output.AppendLine("## Процедура артефакта");
        output.AppendLine("[ ] осколок визуально опознан");
        output.AppendLine("[ ] контейнер подготовлен");
        output.AppendLine("[ ] показание сканера аномалий записано");
        output.AppendLine("[ ] отклик резонатора проверен");
        output.AppendLine("[ ] симптомы воздействия на экипаж проверены");
        output.AppendLine("[ ] отчет закрытия или этот исследовательский отчет оформлен");
        output.AppendLine();
        output.AppendLine("## Опасность");
        output.AppendLine(string.IsNullOrWhiteSpace(record.Hazard) ? "Опасность не записана." : record.Hazard);

        return output.ToString();
    }

    private int CountActivePlayers()
    {
        return _players.Sessions.Count(session => session.Status == SessionStatus.InGame);
    }

    private DynamicEventTemplate? PickTemplate(LuaMSectorStatusSnapshot status, int playerCount)
    {
        var weighted = Templates
            .Select(template => (Template: template, Weight: GetWeight(template, status, playerCount)))
            .Where(entry => entry.Weight > 0)
            .ToList();

        if (weighted.Count == 0)
            return null;

        var total = weighted.Sum(entry => entry.Weight);
        var pick = _random.Next(total);
        foreach (var (template, weight) in weighted)
        {
            if (pick < weight)
                return template;

            pick -= weight;
        }

        return weighted[^1].Template;
    }

    private static int GetWeight(DynamicEventTemplate template, LuaMSectorStatusSnapshot status, int playerCount)
    {
        var weight = template.BaseWeight;

        if (playerCount <= 2 && template.SoloFriendly)
            weight += 4;

        if (playerCount >= 4 && template.GroupFriendly)
            weight += 3;

        if (status.ReputationLedger.TryGetValue(template.ReputationTarget, out var reputation))
            weight += Math.Min(reputation, 4);

        weight += GetConditionWeightBonus(template, status);

        var unresolvedSameTarget = status.Hazards.Count(hazard =>
            !hazard.Resolved &&
            hazard.Story.ToString().StartsWith(LuaMSectorStorySystem.RuntimeDistressStoryPrefix, StringComparison.Ordinal) &&
            hazard.Hazard.Contains(template.ReputationTarget, StringComparison.OrdinalIgnoreCase));

        return Math.Max(weight - unresolvedSameTarget * 3, 1);
    }

    private static bool HasOpenRuntimeLead(LuaMSectorStatusSnapshot status)
    {
        return status.Hazards.Any(hazard =>
            !hazard.Resolved &&
            hazard.Story.ToString().StartsWith(LuaMSectorStorySystem.RuntimeDistressStoryPrefix, StringComparison.Ordinal));
    }

    private bool CanCreateDynamicEvent(LuaMSectorStatusSnapshot status, out string error)
    {
        if (!_cfg.GetCVar(CCVars.LuaMDynamicEventsEnabled))
        {
            error = "Динамические события сектора LuaM отключены конфигурацией.";
            return false;
        }

        var maxActiveSites = Math.Max(0, _cfg.GetCVar(CCVars.LuaMDynamicEventsMaxActiveSites));
        var activeSites = CountActiveDynamicEventSites(status);
        if (activeSites >= maxActiveSites)
        {
            error = $"Достигнут лимит активных точек LuaM: {activeSites}/{maxActiveSites}.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private bool IsAtDynamicEventSiteCapacity(LuaMSectorStatusSnapshot status)
    {
        var maxActiveSites = Math.Max(0, _cfg.GetCVar(CCVars.LuaMDynamicEventsMaxActiveSites));
        return CountActiveDynamicEventSites(status) >= maxActiveSites;
    }

    private static int CountActiveDynamicEventSites(LuaMSectorStatusSnapshot status)
    {
        return status.Hazards
            .Where(hazard =>
                !hazard.Resolved &&
                hazard.Story.ToString().StartsWith(LuaMSectorStorySystem.RuntimeDistressStoryPrefix, StringComparison.Ordinal))
            .Select(hazard => hazard.Story)
            .Distinct()
            .Count();
    }

    private static bool TryFindTemplate(string templateId, out DynamicEventTemplate template)
    {
        template = Templates.FirstOrDefault(template =>
            template.Id.Equals(templateId, StringComparison.OrdinalIgnoreCase))!;

        return template != null;
    }

    private static int GetReputationValue(string target, IReadOnlyDictionary<string, int> reputationLedger)
    {
        if (string.IsNullOrWhiteSpace(target) ||
            !reputationLedger.TryGetValue(target, out var reputation))
        {
            return 0;
        }

        return reputation;
    }

    private static int GetReputationRewardBonus(string target, IReadOnlyDictionary<string, int> reputationLedger)
    {
        var reputation = GetReputationValue(target, reputationLedger);
        if (reputation <= 0)
            return 0;

        return Math.Min(reputation * LuaMSectorStorySystem.ReputationRewardStep, LuaMSectorStorySystem.ReputationRewardCap);
    }

    private static int GetDispatchReputationScore(LuaMSectorStatusSnapshot status)
    {
        return status.ReputationLedger.Values
            .Where(reputation => reputation > 0)
            .Sum();
    }

    private static int GetDispatchCooldownReductionPercent(int dispatchReputation)
    {
        if (dispatchReputation <= 0)
            return 0;

        return Math.Min(dispatchReputation * DispatchCooldownReductionPerReputation, DispatchCooldownReductionCap);
    }

    private static (int MinSeconds, int MaxSeconds) GetAdjustedCooldownRange(int reductionPercent)
    {
        return (
            ApplyDispatchCooldownReduction(MinCooldownSeconds, reductionPercent),
            ApplyDispatchCooldownReduction(MaxCooldownSeconds, reductionPercent));
    }

    private static int ApplyDispatchCooldownReduction(int seconds, int reductionPercent)
    {
        if (reductionPercent <= 0)
            return seconds;

        return Math.Max(60, (int) Math.Ceiling(seconds * (100 - reductionPercent) / 100f));
    }

    private static string GetDispatchTier(int dispatchReputation)
    {
        if (dispatchReputation >= 12)
            return "priority";

        if (dispatchReputation >= 6)
            return "preferred";

        if (dispatchReputation >= 3)
            return "trusted";

        if (dispatchReputation > 0)
            return "known";

        return "standard";
    }

    private static string GetReputationTier(int reputation)
    {
        if (reputation >= 6)
            return "preferred";

        if (reputation >= 3)
            return "trusted";

        if (reputation > 0)
            return "known";

        return "unrated";
    }

    private string BuildPreferredReputationBlockReason(DynamicEventTemplate template, int reputation)
    {
        return Loc.GetString("luam-sector-terminal-preferred-block-reputation",
            ("target", template.ReputationTarget),
            ("current", reputation),
            ("required", PreferredProcessRequiredReputation));
    }

    private static string BuildDescription(
        DynamicEventTemplate template,
        LuaMSectorStatusSnapshot status,
        int playerCount,
        string markerLocation,
        int conditionRewardBonus,
        int routeCalibrationRewardBonus,
        int routeCalibrationClosureRewardBonus,
        int routeCalibrationChainDepth)
    {
        var reputation = status.ReputationLedger.TryGetValue(template.ReputationTarget, out var value)
            ? value
            : 0;

        var output = new StringBuilder();
        output.Append($"Координаты маркера: {markerLocation}.");
        if (routeCalibrationRewardBonus > 0)
            output.Append($" Route calibration handoff bonus: +{routeCalibrationRewardBonus}; chain depth {routeCalibrationChainDepth}.");
        if (routeCalibrationClosureRewardBonus > 0)
            output.Append($" Route calibration closure bonus: +{routeCalibrationClosureRewardBonus}; chain depth {routeCalibrationChainDepth}.");

        var activeConditions = GetActiveConditions(status);
        if (activeConditions.Count > 0)
        {
            var conditionText = string.Join("; ", activeConditions
                .Take(2)
                .Select(condition => $"SC-{condition.Severity} {condition.Title} [{condition.ConditionId}]"));

            output.Append($" Активные условия сектора: {conditionText}.");
            output.Append($" Эффект условия: {GetConditionEffectText(activeConditions[0])}.");

            if (conditionRewardBonus > 0)
                output.Append($" Модификатор награды за условия: +{conditionRewardBonus}.");
        }

        output.Append($" Сгенерировано для активных операторов: {playerCount}. Репутация сектора {template.ReputationTarget}: {reputation}. {template.Description}");
        output.Append($" {Robust.Shared.Localization.Loc.GetString("luam-sector-terminal-route-closure-instruction")}");

        return output.ToString();
    }

    private static string BuildHazard(
        DynamicEventTemplate template,
        LuaMSectorStatusSnapshot status,
        int playerCount,
        int conditionRewardBonus,
        int routeCalibrationRewardBonus,
        int routeCalibrationClosureRewardBonus,
        int routeCalibrationChainDepth)
    {
        var openHazards = status.Hazards.Count(hazard => !hazard.Resolved);
        var output = new StringBuilder();

        if (routeCalibrationRewardBonus > 0)
            output.Append($"Route calibration handoff bonus: +{routeCalibrationRewardBonus}; chain depth {routeCalibrationChainDepth}. ");
        if (routeCalibrationClosureRewardBonus > 0)
            output.Append($"Route calibration closure bonus: +{routeCalibrationClosureRewardBonus}; chain depth {routeCalibrationChainDepth}. ");

        var activeConditions = GetActiveConditions(status);
        if (activeConditions.Count > 0)
        {
            var primary = activeConditions[0];
            output.Append($"Риск условия: SC-{primary.Severity} {primary.Title} [{primary.ConditionId}]; ");
            if (conditionRewardBonus > 0)
                output.Append($"модификатор награды за условия: +{conditionRewardBonus}; ");
            output.Append($"{GetConditionEffectText(primary)}. ");
        }

        output.Append($"Класс динамического события: {template.ReputationTarget}; активные условия сектора: {status.ActiveConditions}; активные опасности сектора: {openHazards}; активные операторы: {playerCount}. {template.Hazard}");

        if (activeConditions.Count > 1)
        {
            output.Append(" Дополнительные эффекты условий:");
            foreach (var condition in activeConditions.Skip(1).Take(2))
            {
                output.Append($" SC-{condition.Severity} {condition.Title}: {GetConditionEffectText(condition)}.");
            }
        }

        return output.ToString();
    }

    private static string BuildConditionRiskSummary(LuaMSectorStatusSnapshot status, int conditionRewardBonus)
    {
        var activeConditions = GetActiveConditions(status);
        if (activeConditions.Count == 0)
            return string.Empty;

        var primary = activeConditions[0];
        var output = new StringBuilder();
        output.Append($"SC-{primary.Severity} {primary.Title} [{primary.ConditionId}]");
        if (conditionRewardBonus > 0)
            output.Append($"; +{conditionRewardBonus}");
        output.Append($"; {GetConditionEffectText(primary)}");

        if (activeConditions.Count > 1)
            output.Append($"; еще +{activeConditions.Count - 1}");

        return output.ToString();
    }

    private static string GetConditionNavSuffix(string conditionRiskSummary)
    {
        var separator = conditionRiskSummary.IndexOf(' ', StringComparison.Ordinal);
        if (separator <= 0)
            return "SC";

        return conditionRiskSummary[..separator];
    }

    private static int GetPrimaryConditionSeverity(LuaMSectorStatusSnapshot status)
    {
        return GetActiveConditions(status).FirstOrDefault()?.Severity ?? 0;
    }

    private static Color GetConditionMarkerColor(int severity)
    {
        return severity switch
        {
            >= 4 => Color.FromHex("#FF4F4FCC"),
            3 => Color.FromHex("#FF9F1CCC"),
            > 0 => Color.FromHex("#FFD166CC"),
            _ => Color.FromHex("#6EC7FF96"),
        };
    }

    private static int GetConditionRewardBonus(LuaMSectorStatusSnapshot status)
    {
        var conditions = GetActiveConditions(status);
        var regularBonus = conditions
            .Sum(condition => Math.Clamp(condition.Severity, 1, 5) * ConditionRewardStep);
        var worldPressureBonus = conditions
            .Where(IsAiWorldPressureCondition)
            .Sum(condition => Math.Clamp(condition.Severity, 1, 5) * AiWorldPressureRewardStep);

        return Math.Min(regularBonus, ConditionRewardCap) +
               Math.Min(worldPressureBonus, AiWorldPressureRewardCap);
    }

    private static int GetConditionWeightBonus(DynamicEventTemplate template, LuaMSectorStatusSnapshot status)
    {
        var bonus = 0;
        var worldPressureActive = false;
        foreach (var condition in GetActiveConditions(status))
        {
            if (IsAiWorldPressureCondition(condition))
            {
                worldPressureActive = true;
                bonus += condition.Severity * 3;
                continue;
            }

            bonus += ConditionMatchesTemplate(condition, template)
                ? condition.Severity * 2
                : Math.Max(condition.Severity / 2, 1);
        }

        return Math.Min(bonus, worldPressureActive ? 18 : 10);
    }

    private static List<LuaMSectorConditionStatus> GetActiveConditions(LuaMSectorStatusSnapshot status)
    {
        return status.Conditions
            .Where(condition => condition.Active)
            .OrderByDescending(condition => condition.Severity)
            .ThenBy(condition => condition.Title)
            .ToList();
    }

    private static LuaMSectorConditionStatus? GetConditionHazardCondition(LuaMSectorStatusSnapshot status)
    {
        var activeConditions = GetActiveConditions(status);
        return activeConditions.FirstOrDefault(IsRadiationCondition) ??
               activeConditions.FirstOrDefault(IsAiWorldPressureCondition);
    }

    private static LuaMSectorConditionStatus? GetSensorDriftCondition(LuaMSectorStatusSnapshot status)
    {
        var activeConditions = GetActiveConditions(status);
        return activeConditions.FirstOrDefault(IsSensorDriftCondition) ??
               activeConditions.FirstOrDefault(IsAiWorldPressureCondition);
    }

    private static bool ConditionMatchesTemplate(LuaMSectorConditionStatus condition, DynamicEventTemplate template)
    {
        var conditionText = $"{condition.ConditionId} {condition.Title} {condition.Summary}";

        if (IsAiWorldPressureCondition(condition))
            return true;

        if (ContainsAny(conditionText, "comms", "radio", "signal", "blackout", "relay"))
        {
            return template.Id is "field-repair" or "navigation-drift" or "ledger-audit" ||
                   template.ReputationTarget == "Station Records";
        }

        if (ContainsAny(conditionText, "radiation", "dust", "debris", "salvage", "wreck"))
        {
            return template.Id is "quiet-distress" or "black-box-echo" or "field-repair" or MonolithArtifactTemplateId ||
                   template.ReputationTarget is "Salvage" or "Distress" or "Research";
        }

        if (ContainsAny(conditionText, "artifact", "monolith", "anomaly", "research", "resonance"))
        {
            return template.Id == MonolithArtifactTemplateId ||
                   template.ReputationTarget == "Research";
        }

        if (ContainsAny(conditionText, "pirate", "raider", "hostile", "ambush", "boarding"))
        {
            return template.Id is "quiet-distress" or "black-box-echo" or "courier-handoff" ||
                   template.ReputationTarget is "Distress" or "Trade";
        }

        if (ContainsAny(conditionText, "trade", "route", "courier", "cargo", "unstable"))
        {
            return template.Id is "courier-handoff" or "navigation-drift" ||
                   template.ReputationTarget is "Trade" or "Station Records";
        }

        return false;
    }

    private static string GetConditionEffectText(LuaMSectorConditionStatus condition)
    {
        var conditionText = $"{condition.ConditionId} {condition.Title} {condition.Summary}";

        if (IsAiWorldPressureCondition(condition))
            return "AI pressure raises event priority, rewards, hazards, and sensor echoes";

        if (ContainsAny(conditionText, "comms", "radio", "signal", "blackout", "relay"))
            return "удаленные обновления ненадежны";

        if (ContainsAny(conditionText, "radiation", "radstorm", "reactor"))
            return "рекомендована радиационная защита";

        if (ContainsAny(conditionText, "artifact", "monolith", "anomaly", "research", "resonance"))
            return "резонанс артефакта нестабилен";

        if (ContainsAny(conditionText, "dust", "storm", "nebula", "sensor"))
            return "вероятен дрейф сенсоров";

        if (ContainsAny(conditionText, "pirate", "raider", "hostile", "ambush", "boarding"))
            return "ожидается агрессивное давление на маршрутах";

        if (ContainsAny(conditionText, "trade", "route", "courier", "cargo", "unstable"))
            return "риск маршрута повышен";

        return "локальный риск маршрута повышен";
    }

    private static bool HasCommsBlackoutCondition(LuaMSectorStatusSnapshot status)
    {
        return GetActiveConditions(status).Any(IsCommsBlackoutCondition);
    }

    private static bool IsAiWorldPressureCondition(LuaMSectorConditionStatus condition)
    {
        var conditionText = $"{condition.ConditionId} {condition.Title} {condition.Summary}";
        return ContainsAny(conditionText, "ai-world-pressure", "ai-admin-will", "world pressure", "admin will", "maximum danger", "ai influence");
    }

    private static bool IsCommsBlackoutCondition(LuaMSectorConditionStatus condition)
    {
        var conditionText = $"{condition.ConditionId} {condition.Title} {condition.Summary}";
        return ContainsAny(conditionText, "comms", "radio", "signal", "blackout", "relay");
    }

    private static bool IsRadiationCondition(LuaMSectorConditionStatus condition)
    {
        var conditionText = $"{condition.ConditionId} {condition.Title} {condition.Summary}";
        return ContainsAny(conditionText, "radiation", "radstorm", "reactor");
    }

    private static bool IsSensorDriftCondition(LuaMSectorConditionStatus condition)
    {
        var conditionText = $"{condition.ConditionId} {condition.Title} {condition.Summary}";
        return ContainsAny(conditionText, "dust", "storm", "nebula", "sensor");
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        return needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private readonly record struct DebrisSitePlan(
        bool Enabled,
        int Serial,
        bool HostileContact);

    private sealed record DynamicEventTemplate(
        string Id,
        string Title,
        string Vessel,
        int Reward,
        string Description,
        string Hazard,
        string ReputationTarget,
        int ReputationDelta,
        int MinPlayers,
        int BaseWeight,
        string MarkerPrototype,
        IReadOnlyList<string>? SiteObjectPrototypes = null)
    {
        public bool SoloFriendly => MinPlayers <= 1;
        public bool GroupFriendly => MinPlayers >= 2 || ReputationTarget is "Trade" or "Station Records" or "Research";
    }

    private sealed record SectorConditionPreset(
        string Id,
        string Title,
        int Severity,
        string Summary);
}
