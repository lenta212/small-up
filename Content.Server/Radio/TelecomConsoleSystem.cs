using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Content.Shared.Chat;
using Content.Shared.Radio;
using Content.Shared.Radio.Components;
using Content.Shared.Radio.EntitySystems;
using Content.Server.Radio.EntitySystems;
using Content.Shared.GameTicking;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Containers;
namespace Content.Server.Radio;

public sealed class TelecomConsoleSystem : EntitySystem
{
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private EncryptionKeySystem _keys = default!;
    [Dependency] private SharedContainerSystem _containers = default!;

    // Реестр занятых частот на раунд: частота -> владелец
    private readonly Dictionary<int, NetUserId> _frequencyOwners = new();
    private readonly Dictionary<char, int> _customKeyCodeFrequencies = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<TelecomLogConsoleComponent, BoundUIOpenedEvent>(OnLogOpened);
        SubscribeLocalEvent<TelecomKeyServiceConsoleComponent, BoundUIOpenedEvent>(OnKeyServiceOpened);
        SubscribeLocalEvent<TelecomLogConsoleComponent, TelecomLogQueryMessage>(OnLogQuery);
        SubscribeLocalEvent<TelecomKeyServiceConsoleComponent, TelecomCreateKeyMessage>(OnCreateKey);
        SubscribeLocalEvent<TelecomKeyServiceConsoleComponent, TelecomToggleLockMessage>(OnToggleLock);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        _frequencyOwners.Clear();
        _customKeyCodeFrequencies.Clear();
    }

    private void OnLogOpened(EntityUid uid, TelecomLogConsoleComponent component, BoundUIOpenedEvent args)
    {
        SetLogState(uid, null);
    }

    private void OnKeyServiceOpened(EntityUid uid, TelecomKeyServiceConsoleComponent component, BoundUIOpenedEvent args)
    {
        if (TryComp(uid, out EncryptionKeyHolderComponent? holder))
            SetKeyState(uid, holder, null);
    }

    private void OnLogQuery(EntityUid uid, TelecomLogConsoleComponent _, TelecomLogQueryMessage args)
    {
        SetLogState(uid, args);
    }

    private void SetLogState(EntityUid uid, TelecomLogQueryMessage? request)
    {
        var radio = EntityManager.System<RadioSystem>();
        if (!TryComp(uid, out TransformComponent? consoleTransform))
            return;

        var map = consoleTransform.MapID;
        DateTime? from = ParseDate(request?.From);
        DateTime? to = ParseDate(request?.To);
        int? frequency = int.TryParse(request?.Frequency, out var parsedFrequency) ? parsedFrequency : null;
        var filter = new TelecomRadioLogFilter(map, request?.Channel, frequency, request?.Speaker, from, to, request?.Words);
        var entries = radio.QueryTelecomLogs(filter);
        var state = new TelecomLogConsoleState();
        state.Maps.Add(map);
        foreach (var channel in _prototypes.EnumeratePrototypes<RadioChannelPrototype>()
                     .Where(channel => !IsRestrictedChannel(channel.ID))
                     .OrderBy(channel => channel.ID))
        {
            state.Channels.Add(new TelecomChannelInfo { Id = channel.ID, Name = channel.LocalizedName });
        }

        var serverQuery = EntityQueryEnumerator<TelecomServerComponent, TransformComponent>();
        while (serverQuery.MoveNext(out var server, out var serverComp, out var transform))
        {
            if (transform.MapID == map && serverComp.HasMode(TelecomServerMode.Password))
            {
                state.RequiresPassword = true;
                state.Authorized = request?.Password != null &&
                    TelecomPasswordService.VerifyPassword(serverComp, request.Password);
            }
        }

        if (state.RequiresPassword && !state.Authorized)
        {
            state.Error = request?.Password == null ? "telecom-console-password-required" : "telecom-console-invalid-password";
            _ui.SetUiState(uid, TelecomConsoleUiKey.Log, state);
            return;
        }

        foreach (var entry in entries)
        {
            state.Entries.Add(new TelecomLogEntryNet
            {
                Timestamp = entry.Timestamp,
                Map = entry.Map,
                Channel = entry.Channel,
                Frequency = entry.Frequency,
                Speaker = entry.Speaker,
                Message = entry.Message
            });
        }

        _ui.SetUiState(uid, TelecomConsoleUiKey.Log, state);
    }

    private void OnCreateKey(EntityUid uid, TelecomKeyServiceConsoleComponent _, TelecomCreateKeyMessage args)
    {
        var tag = args.Tag?.Trim();
        if (!string.IsNullOrEmpty(tag) && tag.Length != 1)
        {
            if (TryComp(uid, out EncryptionKeyHolderComponent? invalidHolder))
                SetKeyState(uid, invalidHolder, "telecom-key-console-invalid-tag");
            return;
        }

        if (args.Frequency < 1)
        {
            if (TryComp(uid, out EncryptionKeyHolderComponent? invalidHolder))
                SetKeyState(uid, invalidHolder, "telecom-key-console-invalid-frequency");
            return;
        }

        // Несущий канал для ВСЕХ кастомных ключей — больше не Common
        if (!_prototypes.TryIndex<RadioChannelPrototype>(RadioChannelPrototype.CustomChannelId, out var carrierChannel))
        {
            if (TryComp(uid, out EncryptionKeyHolderComponent? invalidHolder))
                SetKeyState(uid, invalidHolder, "telecom-key-console-invalid-frequency");
            return;
        }

        if (!TryComp(uid, out TransformComponent? transform) ||
            !TryComp(uid, out EncryptionKeyHolderComponent? holder) ||
            holder.KeysUnlocked == false)
            return;

        // Определяем игрока, создающего ключ
        if (!TryComp<ActorComponent>(args.Actor, out var actorComp))
            return;

        var requesterId = actorComp.PlayerSession.UserId;
        var customKeyCode = string.IsNullOrWhiteSpace(tag)
            ? (char?) null
            : char.ToLowerInvariant(tag[0]);

        if (customKeyCode is { } requestedCode &&
            (requestedCode == SharedChatSystem.DefaultChannelKey ||
             _prototypes.EnumeratePrototypes<RadioChannelPrototype>()
                 .Any(channel => char.ToLowerInvariant(channel.KeyCode) == requestedCode)))
        {
            SetKeyState(uid, holder, "telecom-key-console-invalid-tag");
            return;
        }

        if (customKeyCode is { } reservedCode &&
            _customKeyCodeFrequencies.TryGetValue(reservedCode, out var reservedFrequency) &&
            reservedFrequency != args.Frequency)
        {
            SetKeyState(uid, holder, "telecom-key-console-invalid-tag");
            return;
        }

        // Проверка занятости частоты
        var newFrequencyClaim = !_frequencyOwners.ContainsKey(args.Frequency);
        if (_frequencyOwners.TryGetValue(args.Frequency, out var owner) && owner != requesterId)
        {
            SetKeyState(uid, holder, "telecom-key-console-frequency-taken");
            return;
        }
        _frequencyOwners[args.Frequency] = requesterId;
        if (customKeyCode is { } claimedCode)
            _customKeyCodeFrequencies[claimedCode] = args.Frequency;

        var key = Spawn("EncryptionKeyCommon", transform.Coordinates);
        var component = EnsureComp<EncryptionKeyComponent>(key);
        component.Channels.Clear();
        component.Channels.Add(RadioChannelPrototype.CustomChannelId);
        component.DefaultChannel = null; // кастомный ключ не подменяет общий канал
        component.CustomFrequency = args.Frequency;
        component.ChannelName = NullIfEmpty(args.ChannelName) ?? $"Канал {args.Frequency}";
        component.CustomKeyCode = customKeyCode;
        component.Tag = NullIfEmpty(args.Tag);
        component.Color = args.Color != null && Color.TryParse(args.Color, out var color)
            ? color
            : Color.Green;
        component.OwnerUserId = requesterId;

        if (!_containers.Insert(key, holder.KeyContainer))
        {
            QueueDel(key);
            if (newFrequencyClaim)
                _frequencyOwners.Remove(args.Frequency);
            if (customKeyCode is { } failedCode &&
                newFrequencyClaim)
                _customKeyCodeFrequencies.Remove(failedCode);
            return;
        }
        _keys.UpdateChannels(uid, holder);
        SetKeyState(uid, holder, null);
    }

    private void OnToggleLock(EntityUid uid, TelecomKeyServiceConsoleComponent _, TelecomToggleLockMessage args)
    {
        if (!TryComp(uid, out EncryptionKeyHolderComponent? holder))
            return;
        holder.KeysUnlocked = !args.Locked;
        SetKeyState(uid, holder, null);
    }

    private void SetKeyState(EntityUid uid, EncryptionKeyHolderComponent holder, string? error)
    {
        var state = new TelecomKeyServiceState { KeysLocked = !holder.KeysUnlocked, Error = error };
        foreach (var channel in _prototypes.EnumeratePrototypes<RadioChannelPrototype>()
                     .Where(channel => !IsRestrictedChannel(channel.ID))
                     .OrderBy(channel => channel.ID))
            state.Channels.Add(new TelecomChannelInfo { Id = channel.ID, Name = channel.LocalizedName });

        if (state.Channels.Count == 0)
        {
            foreach (var channelId in holder.Channels.OrderBy(id => id))
            {
                if (!_prototypes.TryIndex<RadioChannelPrototype>(channelId, out var channel))
                    continue;

                state.Channels.Add(new TelecomChannelInfo { Id = channel.ID, Name = channel.LocalizedName });
            }
        }

        _ui.SetUiState(uid, TelecomConsoleUiKey.KeyService, state);
    }

    private static DateTime? ParseDate(string? value)
    {
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result)
            ? result.ToUniversalTime()
            : null;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool IsRestrictedChannel(string id) =>
        id.Equals("Syndicate", StringComparison.OrdinalIgnoreCase) ||
        id.Equals("Binary", StringComparison.OrdinalIgnoreCase) ||
        id.Equals("Chimera", StringComparison.OrdinalIgnoreCase) ||
        id.Equals(RadioChannelPrototype.CustomChannelId, StringComparison.OrdinalIgnoreCase) ||
        id.Contains("Collective", StringComparison.OrdinalIgnoreCase);
}