using System.Linq;
using Content.Client.Gameplay;
using Content.Client.Guidebook;
using Content.Client.Guidebook.Controls;
using Content.Client.Lobby;
using Content.Client.Players.PlayTimeTracking;
using Content.Client.Station;
using Content.Client.UserInterface.Controls;
using Content.Shared.CCVar;
using Content.Shared._NF.CCVar;
using Content.Shared.Ghost;
using Content.Shared.Guidebook;
using Content.Shared.Input;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Client.Player;
using Robust.Client.State;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controllers;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using static Robust.Client.UserInterface.Controls.BaseButton;
using Robust.Shared.Input.Binding;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Client.UserInterface.Systems.Guidebook;

public sealed partial class GuidebookUIController : UIController, IOnStateEntered<LobbyState>, IOnStateEntered<GameplayState>, IOnStateExited<LobbyState>, IOnStateExited<GameplayState>, IOnSystemChanged<GuidebookSystem>
{
    [UISystemDependency] private readonly GuidebookSystem _guidebookSystem = default!;
    [UISystemDependency] private readonly StationSystem _stationSystem = default!;
    [Dependency] private IPrototypeManager _prototypeManager = default!;
    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private IPlayerManager _playerManager = default!;
    [Dependency] private JobRequirementsManager _jobRequirements = default!;

    private static readonly TimeSpan FrontierTutorialNewPlayerWindow = TimeSpan.FromMinutes(180);

    private GuidebookWindow? _guideWindow;
    private FrontierTutorialOfferWindow? _tutorialOffer;
    private MenuButton? GuidebookButton => UIManager.GetActiveUIWidgetOrNull<MenuBar.Widgets.GameTopMenuBar>()?.GuidebookButton;
    private ProtoId<GuideEntryPrototype>? _lastEntry;

    private FrontierTutorialFlow? _tutorialFlow;
    private EntityUid? _tutorialEntity;
    private bool _inGameplay;
    private bool _tutorialPromptedThisGameplay;
    private bool _waitingForTutorialLesson;
    private bool _suppressTutorialOfferClose;
    private int _stationCheckGeneration;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<LocalPlayerAttachedEvent>(OnLocalPlayerAttached);
        SubscribeLocalEvent<LocalPlayerDetachedEvent>(OnLocalPlayerDetached);
        SubscribeLocalEvent<EntParentChangedMessage>(OnParentChanged);
        SubscribeLocalEvent<MobStateChangedEvent>(OnMobStateChanged);
    }

    public void OnStateEntered(LobbyState state)
    {
        _inGameplay = false;
        HandleStateEntered(state);
    }

    public void OnStateEntered(GameplayState state)
    {
        _inGameplay = true;
        HandleStateEntered(state);
        TryStartFrontierTutorialForLocalPlayer();
    }

    private void HandleStateEntered(State state)
    {
        DebugTools.Assert(_guideWindow == null);

        // setup window
        _guideWindow = UIManager.CreateWindow<GuidebookWindow>();
        _guideWindow.OnClose += OnWindowClosed;
        _guideWindow.OnOpen += OnWindowOpen;
        _guideWindow.TutorialCompleteButton.OnPressed += _ => CompleteFrontierTutorialLesson();
        _guideWindow.TutorialBackButton.OnPressed += _ => ReturnToFrontierTutorialQuestion();

        // setup keybinding
        CommandBinds.Builder
            .Bind(ContentKeyFunctions.OpenGuidebook,
                InputCmdHandler.FromDelegate(_ => ToggleGuidebook()))
            .Register<GuidebookUIController>();
    }

    public void OnStateExited(LobbyState state)
    {
        HandleStateExited();
    }

    public void OnStateExited(GameplayState state)
    {
        _inGameplay = false;
        CancelFrontierTutorial(closeLesson: true);
        _tutorialPromptedThisGameplay = false;
        HandleStateExited();
    }

    private void HandleStateExited()
    {
        if (_guideWindow == null)
            return;

        _guideWindow.OnClose -= OnWindowClosed;
        _guideWindow.OnOpen -= OnWindowOpen;

        // shutdown
        _guideWindow.Dispose();
        _guideWindow = null;
        _tutorialOffer?.Dispose();
        _tutorialOffer = null;
        CommandBinds.Unregister<GuidebookUIController>();
    }

    private void OnLocalPlayerAttached(LocalPlayerAttachedEvent args)
    {
        ScheduleFrontierTutorialStationChecks(args.Entity);
    }

    private void OnLocalPlayerDetached(LocalPlayerDetachedEvent args)
    {
        _stationCheckGeneration++;
        if (_tutorialEntity == args.Entity)
            CancelFrontierTutorial(closeLesson: true);
    }

    private void OnParentChanged(ref EntParentChangedMessage args)
    {
        if (_playerManager.LocalEntity == args.Entity)
            ScheduleFrontierTutorialStationChecks(args.Entity);
    }

    private void OnMobStateChanged(MobStateChangedEvent args)
    {
        if (_playerManager.LocalEntity != args.Target)
            return;

        if (args.Component.CurrentState == MobState.Alive)
        {
            ScheduleFrontierTutorialStationChecks(args.Target);
            return;
        }

        if (_tutorialEntity == args.Target)
            CancelFrontierTutorial(closeLesson: true);
    }

    private void TryStartFrontierTutorialForLocalPlayer()
    {
        if (_playerManager.LocalEntity is { } local)
            ScheduleFrontierTutorialStationChecks(local);
    }

    private void ScheduleFrontierTutorialStationChecks(EntityUid entity)
    {
        var generation = ++_stationCheckGeneration;
        TryStartFrontierTutorial(entity);

        // Attachment can precede the station-grid component in the first client state.
        Timer.Spawn(250, () => RetryFrontierTutorialStationCheck(entity, generation));
        Timer.Spawn(1000, () => RetryFrontierTutorialStationCheck(entity, generation));
    }

    private void RetryFrontierTutorialStationCheck(EntityUid entity, int generation)
    {
        if (generation != _stationCheckGeneration ||
            !_inGameplay ||
            _playerManager.LocalEntity != entity)
        {
            return;
        }

        TryStartFrontierTutorial(entity);
    }

    private void TryStartFrontierTutorial(EntityUid entity)
    {
        if (!_inGameplay ||
            _guideWindow == null ||
            _tutorialFlow != null ||
            _waitingForTutorialLesson ||
            _playerManager.LocalEntity != entity ||
            _tutorialPromptedThisGameplay ||
            EntityManager.HasComponent<GhostComponent>(entity) ||
            !EntityManager.TryGetComponent(entity, out MobStateComponent? mobState) ||
            mobState.CurrentState != MobState.Alive ||
            _stationSystem.GetOwningStation(entity) is not { Valid: true } ||
            !TryMigrateFrontierTutorialProgress(out var completedMask))
        {
            return;
        }

        var flow = new FrontierTutorialFlow(FrontierTutorialCatalog.Topics.Count, completedMask);
        if (flow.IsComplete ||
            !FrontierTutorialFlow.ShouldOfferForPlaytime(
                completedMask,
                FrontierTutorialCatalog.Topics.Count,
                _jobRequirements.FetchOverallPlaytime(),
                FrontierTutorialNewPlayerWindow))
        {
            return;
        }

        // One readiness check per gameplay session. Respawning or changing bodies must not reopen it.
        _tutorialPromptedThisGameplay = true;
        _tutorialEntity = entity;
        _tutorialFlow = flow;
        OpenCurrentFrontierTutorialQuestion();
    }

    private bool TryMigrateFrontierTutorialProgress(out int completedMask)
    {
        var legacyChoice = _configuration.GetCVar(NFCCVars.FrontierTutorialChoice);
        var storedMask = _configuration.GetCVar(NFCCVars.FrontierTutorialCompletedTopics);
        var storedVersion = _configuration.GetCVar(NFCCVars.FrontierTutorialProgressVersion);
        var migration = FrontierTutorialFlow.Migrate(
            legacyChoice,
            storedMask,
            storedVersion,
            FrontierTutorialCatalog.Topics.Count);

        completedMask = migration.CompletedMask;
        if (!migration.Compatible)
            return false;

        // Write the version last so an interrupted migration is safely retried.
        if (migration.CompletedMask != storedMask)
            _configuration.SetCVar(NFCCVars.FrontierTutorialCompletedTopics, migration.CompletedMask);
        if (migration.LegacyChoice != legacyChoice)
            _configuration.SetCVar(NFCCVars.FrontierTutorialChoice, migration.LegacyChoice);
        if (migration.Version != storedVersion)
            _configuration.SetCVar(NFCCVars.FrontierTutorialProgressVersion, migration.Version);

        return true;
    }

    private void OpenCurrentFrontierTutorialQuestion()
    {
        if (_tutorialFlow == null || _tutorialFlow.IsComplete)
        {
            FinishFrontierTutorial();
            return;
        }

        if (_tutorialOffer == null)
        {
            _tutorialOffer = UIManager.CreateWindow<FrontierTutorialOfferWindow>();
            _tutorialOffer.KnowButton.OnPressed += _ => CompleteCurrentFrontierTutorialTopic();
            _tutorialOffer.ExplainButton.OnPressed += _ => OpenCurrentFrontierTutorialLesson();
            _tutorialOffer.LaterButton.OnPressed += _ => CancelFrontierTutorial(closeLesson: false);
            _tutorialOffer.TopicSelected += SelectFrontierTutorialTopic;
            _tutorialOffer.OnClose += OnFrontierTutorialOfferClosed;
        }

        _tutorialOffer.SetChecklist(
            FrontierTutorialCatalog.Topics
                .Select(topic => Loc.GetString(topic.NameLocId))
                .ToList(),
            _tutorialFlow.CompletedMask,
            _tutorialFlow.CurrentIndex);
        if (!_tutorialOffer.IsOpen)
            _tutorialOffer.OpenCentered();
    }

    private void SelectFrontierTutorialTopic(int index)
    {
        if (_tutorialFlow == null || !_tutorialFlow.SelectTopic(index))
            return;

        OpenCurrentFrontierTutorialQuestion();
    }

    private void OnFrontierTutorialOfferClosed()
    {
        if (!_suppressTutorialOfferClose)
            CancelFrontierTutorial(closeLesson: false, closeOffer: false);
    }

    private void OpenCurrentFrontierTutorialLesson()
    {
        if (_tutorialFlow == null || _tutorialFlow.IsComplete || _waitingForTutorialLesson)
            return;

        var topic = FrontierTutorialCatalog.Topics[_tutorialFlow.CurrentIndex];
        _waitingForTutorialLesson = true;
        CloseFrontierTutorialOffer();

        var guides = new List<ProtoId<GuideEntryPrototype>> { topic.Guide };
        OpenGuidebook(
            guides,
            rootEntries: guides,
            includeChildren: false,
            selected: topic.Guide);
        if (_guideWindow != null)
            _guideWindow.TutorialActionContainer.Visible = true;
    }

    private void CompleteFrontierTutorialLesson()
    {
        if (!_waitingForTutorialLesson || _tutorialFlow == null)
            return;

        _waitingForTutorialLesson = false;
        if (_guideWindow != null)
        {
            _guideWindow.TutorialActionContainer.Visible = false;
            _guideWindow.Close();
        }

        CompleteCurrentFrontierTutorialTopic();
    }

    private void ReturnToFrontierTutorialQuestion()
    {
        if (!_waitingForTutorialLesson || _tutorialFlow == null)
            return;

        _waitingForTutorialLesson = false;
        if (_guideWindow != null)
        {
            _guideWindow.TutorialActionContainer.Visible = false;
            _guideWindow.Close();
        }

        OpenCurrentFrontierTutorialQuestion();
    }

    private void CompleteCurrentFrontierTutorialTopic()
    {
        if (_tutorialFlow == null || !_tutorialFlow.CompleteCurrent())
            return;

        _configuration.SetCVar(
            NFCCVars.FrontierTutorialCompletedTopics,
            _tutorialFlow.CompletedMask);

        if (_tutorialFlow.IsComplete)
        {
            FinishFrontierTutorial();
            return;
        }

        OpenCurrentFrontierTutorialQuestion();
    }

    private void FinishFrontierTutorial()
    {
        _tutorialFlow = null;
        _tutorialEntity = null;
        _waitingForTutorialLesson = false;
        CloseFrontierTutorialOffer();
    }

    private void CancelFrontierTutorial(bool closeLesson, bool closeOffer = true)
    {
        var shouldCloseLesson = closeLesson && _waitingForTutorialLesson && _guideWindow?.IsOpen == true;
        _waitingForTutorialLesson = false;
        _tutorialFlow = null;
        _tutorialEntity = null;

        if (_guideWindow != null)
            _guideWindow.TutorialActionContainer.Visible = false;

        if (closeOffer)
            CloseFrontierTutorialOffer();
        if (shouldCloseLesson)
            _guideWindow?.Close();
    }

    private void CloseFrontierTutorialOffer()
    {
        if (_tutorialOffer?.IsOpen != true)
            return;

        _suppressTutorialOfferClose = true;
        try
        {
            _tutorialOffer.Close();
        }
        finally
        {
            _suppressTutorialOfferClose = false;
        }
    }

    public void OnSystemLoaded(GuidebookSystem system)
    {
        _guidebookSystem.OnGuidebookOpen += OpenGuidebook;
    }

    public void OnSystemUnloaded(GuidebookSystem system)
    {
        _guidebookSystem.OnGuidebookOpen -= OpenGuidebook;
    }

    internal void UnloadButton()
    {
        if (GuidebookButton == null)
            return;

        GuidebookButton.OnPressed -= GuidebookButtonOnPressed;
    }

    internal void LoadButton()
    {
        if (GuidebookButton == null)
            return;

        GuidebookButton.OnPressed += GuidebookButtonOnPressed;
    }

    private void GuidebookButtonOnPressed(ButtonEventArgs obj)
    {
        ToggleGuidebook();
    }

    public void ToggleGuidebook()
    {
        if (_guideWindow == null)
            return;

        if (_guideWindow.IsOpen)
        {
            UIManager.ClickSound();
            _guideWindow.Close();
        }
        else
        {
            OpenGuidebook();
        }
    }

    private void OnWindowClosed()
    {
        var interruptedTutorialLesson = _waitingForTutorialLesson;
        _waitingForTutorialLesson = false;

        if (GuidebookButton != null)
            GuidebookButton.Pressed = false;

        if (_guideWindow != null)
        {
            _guideWindow.ReturnContainer.Visible = false;
            _guideWindow.TutorialActionContainer.Visible = false;
            _lastEntry = _guideWindow.LastEntry;
        }

        if (interruptedTutorialLesson && _inGameplay && _tutorialFlow != null)
            OpenCurrentFrontierTutorialQuestion();
    }

    private void OnWindowOpen()
    {
        if (GuidebookButton != null)
            GuidebookButton.Pressed = true;
    }

    /// <summary>
    ///     Opens or closes the guidebook.
    /// </summary>
    /// <param name="guides">What guides should be shown. If not specified, this will instead list all the entries</param>
    /// <param name="rootEntries">A list of guides that should form the base of the table of contents. If not specified,
    /// this will automatically simply be a list of all guides that have no parent.</param>
    /// <param name="forceRoot">This forces a singular guide to contain all other guides. This guide will
    /// contain its own children, in addition to what would normally be the root guides if this were not
    /// specified.</param>
    /// <param name="includeChildren">Whether or not to automatically include child entries. If false, this will ONLY
    /// show the specified entries</param>
    /// <param name="selected">The guide whose contents should be displayed when the guidebook is opened</param>
    public void OpenGuidebook(
        Dictionary<ProtoId<GuideEntryPrototype>, GuideEntry>? guides = null,
        List<ProtoId<GuideEntryPrototype>>? rootEntries = null,
        ProtoId<GuideEntryPrototype>? forceRoot = null,
        bool includeChildren = true,
        ProtoId<GuideEntryPrototype>? selected = null)
    {
        if (_guideWindow == null)
            return;

        if (GuidebookButton != null)
            GuidebookButton.SetClickPressed(!_guideWindow.IsOpen);

        if (guides == null)
        {
            guides = _prototypeManager.EnumeratePrototypes<GuideEntryPrototype>()
                .ToDictionary(x => new ProtoId<GuideEntryPrototype>(x.ID), x => (GuideEntry) x);
        }
        else if (includeChildren)
        {
            var oldGuides = guides;
            guides = new(oldGuides);
            foreach (var guide in oldGuides.Values)
            {
                RecursivelyAddChildren(guide, guides);
            }
        }

        if (selected == null)
        {
            if (_lastEntry is { } lastEntry && guides.ContainsKey(lastEntry))
            {
                selected = _lastEntry;
            }
            else
            {
                selected = _configuration.GetCVar(CCVars.DefaultGuide);
            }
        }
        _guideWindow.UpdateGuides(guides, rootEntries, forceRoot, selected);

        // Expand up to depth-2.
        _guideWindow.Tree.SetAllExpanded(false);
        _guideWindow.Tree.SetAllExpanded(true, 0); // Frontier: 1->0 (too many entries at depth 2)

        _guideWindow.OpenCenteredRight();
    }

    public void OpenGuidebook(
        List<ProtoId<GuideEntryPrototype>> guideList,
        List<ProtoId<GuideEntryPrototype>>? rootEntries = null,
        ProtoId<GuideEntryPrototype>? forceRoot = null,
        bool includeChildren = true,
        ProtoId<GuideEntryPrototype>? selected = null)
    {
        Dictionary<ProtoId<GuideEntryPrototype>, GuideEntry> guides = new();
        foreach (var guideId in guideList)
        {
            if (!_prototypeManager.TryIndex(guideId, out var guide))
            {
                Logger.Error($"Encountered unknown guide prototype: {guideId}");
                continue;
            }
            guides.Add(guideId, guide);
        }

        OpenGuidebook(guides, rootEntries, forceRoot, includeChildren, selected);
    }

    public void CloseGuidebook()
    {
        if (_guideWindow == null)
            return;

        if (_guideWindow.IsOpen)
        {
            UIManager.ClickSound();
            _guideWindow.Close();
        }
    }

    private void RecursivelyAddChildren(GuideEntry guide, Dictionary<ProtoId<GuideEntryPrototype>, GuideEntry> guides)
    {
        foreach (var childId in guide.Children)
        {
            if (guides.ContainsKey(childId))
                continue;

            if (!_prototypeManager.TryIndex(childId, out var child))
            {
                Logger.Error($"Encountered unknown guide prototype: {childId} as a child of {guide.Id}. If the child is not a prototype, it must be directly provided.");
                continue;
            }

            guides.Add(childId, child);
            RecursivelyAddChildren(child, guides);
        }
    }
}
