// SPDX-FileCopyrightText: 2025 GoobBot <uristmchands@proton.me>
// SPDX-FileCopyrightText: 2025 ImHoks <142083149+ImHoks@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 ImHoks <imhokzzzz@gmail.com>
// SPDX-FileCopyrightText: 2025 KillanGenifer <killangenifer@gmail.com>
// SPDX-FileCopyrightText: 2025 gluesniffler <159397573+gluesniffler@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.Radio.Components;
using Content.Server.Silicons.Laws;
using Content.Shared._CorvaxNext.Silicons.Borgs;
using Content.Shared._CorvaxNext.Silicons.Borgs.Components;
using Content.Shared.Actions;
using Content.Shared.Mind;
using Content.Shared.Silicons.Borgs.Components;
using Content.Shared.Silicons.Laws.Components;
using Content.Shared.Silicons.StationAi;
using Content.Shared.StationAi;
using Content.Shared.Tag;
using Content.Shared.Verbs;
using Robust.Server.GameObjects;
using Robust.Shared.Player;

namespace Content.Server._CorvaxNext.Silicons.Borgs;

public sealed partial class AiRemoteControlSystem : SharedAiRemoteControlSystem
{
    [Dependency] private SharedActionsSystem _actions = default!;
    [Dependency] private SiliconLawSystem _lawSystem = default!;
    [Dependency] private SharedStationAiSystem _stationAiSystem = default!;
    [Dependency] private SharedMindSystem _mind = default!;
    [Dependency] private UserInterfaceSystem _userInterface = default!;
    [Dependency] private SharedTransformSystem _xformSystem = default!;
    [Dependency] private TagSystem _tag = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<AiRemoteControllerComponent, ReturnMindIntoAiEvent>(OnReturnMindIntoAi);
        SubscribeLocalEvent<AiRemoteControllerComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<AiRemoteControllerComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<AiRemoteControllerComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<AiRemoteControllerComponent, GetVerbsEvent<AlternativeVerb>>(OnGetVerbs);
        SubscribeLocalEvent<StationAiHeldComponent, AiRemoteControllerComponent.RemoteDeviceActionMessage>(OnUiRemoteAction);
        SubscribeLocalEvent<StationAiHeldComponent, ToggleRemoteDevicesScreenEvent>(OnToggleRemoteDevicesScreen);
    }

    private void OnMapInit(Entity<AiRemoteControllerComponent> entity, ref MapInitEvent args)
    {
        EnsureRemoteControllerSetup(entity);
    }

    private void OnStartup(Entity<AiRemoteControllerComponent> entity, ref ComponentStartup args)
    {
        EnsureRemoteControllerSetup(entity);
    }

    private void EnsureRemoteControllerSetup(Entity<AiRemoteControllerComponent> entity)
    {
        EnsureComp<StationAiVisionComponent>(entity.Owner);
        if (entity.Comp.BackToAiActionEntity is { } existingAction &&
            !TerminatingOrDeleted(existingAction))
        {
            return;
        }

        EntityUid? actionEnt = null;
        _actions.AddAction(entity.Owner, ref actionEnt, entity.Comp.BackToAiAction);

        if (actionEnt != null)
            entity.Comp.BackToAiActionEntity = actionEnt.Value;
    }

    private void OnShutdown(Entity<AiRemoteControllerComponent> entity, ref ComponentShutdown args)
    {
        _actions.RemoveAction(entity.Owner, entity.Comp.BackToAiActionEntity);

        RestoreRadioChannels(entity);

        ReturnMindIntoAi(entity);

        if (entity.Comp.TemporaryRemoteController && !TerminatingOrDeleted(entity.Owner))
            RemCompDeferred<StationAiVisionComponent>(entity.Owner);
    }

    private void OnGetVerbs(Entity<AiRemoteControllerComponent> entity, ref GetVerbsEvent<AlternativeVerb> args)
    {
        var user = args.User;

        if (!TryComp<StationAiHeldComponent>(user, out var stationAiHeldComp))
            return;

        var verb = new AlternativeVerb
        {
            Text = Loc.GetString("ai-remote-control"),
            Act = () => AiTakeControl(user, entity)
        };
        args.Verbs.Add(verb);
    }

    private void OnReturnMindIntoAi(Entity<AiRemoteControllerComponent> entity, ref ReturnMindIntoAiEvent args) =>
        ReturnMindIntoAiAndCleanup(entity);

    public void AiTakeControl(EntityUid ai, EntityUid entity)
    {
        if (!_mind.TryGetMind(ai, out var mindId, out var mind))
            return;

        if (_mind.TryGetMind(entity, out _, out _))
            return;

        if (!TryComp<StationAiHeldComponent>(ai, out var stationAiHeldComp))
            return;

        if (!CanRemoteControlTarget(entity))
            return;

        var hadRemoteController = TryComp(entity, out AiRemoteControllerComponent? aiRemoteComp);
        aiRemoteComp ??= EnsureComp<AiRemoteControllerComponent>(entity);
        aiRemoteComp.TemporaryRemoteController |= !hadRemoteController;
        EnsureRemoteControllerSetup((entity, aiRemoteComp));

        if (TryComp(entity, out IntrinsicRadioTransmitterComponent? transmitter))
        {
            aiRemoteComp.PreviouslyTransmitterChannels = [.. transmitter.Channels];

            if (TryComp(ai, out IntrinsicRadioTransmitterComponent? stationAiTransmitter))
                transmitter.Channels = [.. stationAiTransmitter.Channels];
        }

        if (TryComp(entity, out ActiveRadioComponent? activeRadio))
        {
            aiRemoteComp.PreviouslyActiveRadioChannels = [.. activeRadio.Channels];

            if (TryComp(ai, out ActiveRadioComponent? stationAiActiveRadio))
                activeRadio.Channels = [.. stationAiActiveRadio.Channels];
        }

        _mind.ControlMob(ai, entity);
        aiRemoteComp.AiHolder = ai;
        aiRemoteComp.LinkedMind = mindId;

        stationAiHeldComp.CurrentConnectedEntity = entity;

        if (!_stationAiSystem.TryGetCore(ai, out var stationAiCore))
            return;

        _stationAiSystem.SwitchRemoteEntityMode(stationAiCore, false);

        RewriteLaws(ai, entity);
    }

    private void OnToggleRemoteDevicesScreen(EntityUid uid, StationAiHeldComponent component, ToggleRemoteDevicesScreenEvent args)
    {
        if (args.Handled || !TryComp<ActorComponent>(args.Performer, out var actor))
            return;
        args.Handled = true;

        _userInterface.TryToggleUi(uid, RemoteDeviceUiKey.Key, actor.PlayerSession);

        var remoteDevices = new List<RemoteDevicesData>();
        var seen = new HashSet<EntityUid>();

        var remoteQuery = EntityManager.EntityQueryEnumerator<AiRemoteControllerComponent>();
        while (remoteQuery.MoveNext(out var queryUid, out _))
            TryAddRemoteDevice(queryUid, remoteDevices, seen);

        var borgQuery = EntityManager.EntityQueryEnumerator<BorgChassisComponent>();
        while (borgQuery.MoveNext(out var queryUid, out _))
            TryAddRemoteDevice(queryUid, remoteDevices, seen);

        var tagQuery = EntityManager.EntityQueryEnumerator<TagComponent>();
        while (tagQuery.MoveNext(out var queryUid, out var tags))
        {
            if (_tag.HasTag(tags, "Bot"))
                TryAddRemoteDevice(queryUid, remoteDevices, seen);
        }

        var state = new RemoteDevicesBuiState(remoteDevices);
        _userInterface.SetUiState(uid, RemoteDeviceUiKey.Key, state);
    }

    private void TryAddRemoteDevice(EntityUid queryUid, List<RemoteDevicesData> remoteDevices, HashSet<EntityUid> seen)
    {
        if (!seen.Add(queryUid) || !CanRemoteControlTarget(queryUid))
            return;

        var data = new RemoteDevicesData
        {
            NetEntityUid = GetNetEntity(queryUid),
            DisplayName = Comp<MetaDataComponent>(queryUid).EntityName
        };

        remoteDevices.Add(data);
    }

    private void OnUiRemoteAction(EntityUid uid, StationAiHeldComponent component, AiRemoteControllerComponent.RemoteDeviceActionMessage msg)
    {
        if (msg.RemoteAction == null)
            return;

        var target = GetEntity(msg.RemoteAction?.Target);

        if (target == null || !CanRemoteControlTarget(target.Value))
            return;

        switch (msg.RemoteAction?.ActionType)
        {
            case RemoteDeviceActionEvent.RemoteDeviceActionType.MoveToDevice:
                if (!_stationAiSystem.TryGetCore(uid, out var stationAiCore)
                    || stationAiCore.Comp?.RemoteEntity == null)
                    return;
                _xformSystem.SetCoordinates(stationAiCore.Comp.RemoteEntity.Value, Transform(target.Value).Coordinates);
                break;

            case RemoteDeviceActionEvent.RemoteDeviceActionType.TakeControl:
                AiTakeControl(uid, target.Value);
                break;
        }
    }

    private bool CanRemoteControlTarget(EntityUid entity)
    {
        if (TerminatingOrDeleted(entity))
            return false;

        if (_mind.TryGetMind(entity, out _, out _))
            return false;

        return HasComp<AiRemoteControllerComponent>(entity) ||
               HasComp<BorgChassisComponent>(entity) ||
               _tag.HasTag(entity, "Bot");
    }

    private void ReturnMindIntoAiAndCleanup(Entity<AiRemoteControllerComponent> entity)
    {
        var hadLinkedMind = entity.Comp.AiHolder != null && entity.Comp.LinkedMind != null;

        ReturnMindIntoAi(entity);

        if (!hadLinkedMind || entity.Comp.AiHolder != null || entity.Comp.LinkedMind != null)
            return;

        RestoreRadioChannels(entity);

        if (entity.Comp.TemporaryRemoteController)
            RemCompDeferred<AiRemoteControllerComponent>(entity.Owner);
    }

    private void RestoreRadioChannels(Entity<AiRemoteControllerComponent> entity)
    {
        if (TryComp(entity, out IntrinsicRadioTransmitterComponent? transmitter)
            && entity.Comp.PreviouslyTransmitterChannels != null)
        {
            transmitter.Channels = [.. entity.Comp.PreviouslyTransmitterChannels];
            entity.Comp.PreviouslyTransmitterChannels = null;
        }

        if (TryComp(entity, out ActiveRadioComponent? activeRadio)
            && entity.Comp.PreviouslyActiveRadioChannels != null)
        {
            activeRadio.Channels = [.. entity.Comp.PreviouslyActiveRadioChannels];
            entity.Comp.PreviouslyActiveRadioChannels = null;
        }
    }

    private void RewriteLaws(EntityUid from, EntityUid to)
    {
        if (!TryComp<SiliconLawProviderComponent>(from, out var fromLawsComp))
            return;

        if (!TryComp<SiliconLawProviderComponent>(to, out var toLawsComp))
            return;

        if (fromLawsComp.Lawset == null)
            return;

        var fromLaws = _lawSystem.GetLaws(from);
        _lawSystem.SetLaws(fromLaws.Laws, to, silent: true);
    }
}
