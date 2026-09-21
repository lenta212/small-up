using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Content.Shared.Chat;
using Content.Shared.Radio;
using Content.Shared.Radio.Components;
using Content.Shared.Radio.EntitySystems;
using Content.Server.Radio.EntitySystems;
using Content.Server.Audio;
using Content.Shared.GameTicking;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Containers;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
namespace Content.Server.Radio;

public sealed class TelecomConsoleSystem : EntitySystem
{
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private EncryptionKeySystem _keys = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private ServerGlobalSoundSystem _globalSound = default!;
    [Dependency] private SharedAudioSystem _audio = default!;

    private readonly Dictionary<int, FrequencyProfile> _frequencyProfiles = new();

    private sealed class FrequencyProfile
    {
        public NetEntity Owner;
        public string ChannelName = string.Empty;
        public Color Color = Color.Green;
        public Color? GradientColor;
        public byte[]? PasswordSalt;
        public byte[]? PasswordHash;
        public int PasswordIterations = TelecomPasswordService.DefaultIterations;
    }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<TelecomLogConsoleComponent, BoundUIOpenedEvent>(OnLogOpened);
        SubscribeLocalEvent<TelecomKeyServiceConsoleComponent, BoundUIOpenedEvent>(OnKeyServiceOpened);
        SubscribeLocalEvent<TelecomLogConsoleComponent, TelecomLogQueryMessage>(OnLogQuery);
        SubscribeLocalEvent<TelecomKeyServiceConsoleComponent, TelecomCreateKeyMessage>(OnCreateKey);
        SubscribeLocalEvent<TelecomKeyServiceConsoleComponent, TelecomDeleteKeyMessage>(OnDeleteKey);
        SubscribeLocalEvent<TelecomKeyServiceConsoleComponent, TelecomDeleteAllKeysMessage>(OnDeleteAllKeys);
        SubscribeLocalEvent<TelecomKeyServiceConsoleComponent, TelecomDetachFrequencyMessage>(OnDetachFrequency);
        SubscribeLocalEvent<TelecomKeyServiceConsoleComponent, TelecomToggleLockMessage>(OnToggleLock);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        _frequencyProfiles.Clear();
    }

    private void OnLogOpened(EntityUid uid, TelecomLogConsoleComponent component, BoundUIOpenedEvent args)
    {
        SetLogState(uid, null);
    }

    private void OnKeyServiceOpened(EntityUid uid, TelecomKeyServiceConsoleComponent component, BoundUIOpenedEvent args)
    {
        if (TryComp(uid, out EncryptionKeyHolderComponent? holder) &&
            TryGetActorCharacter(args.Actor, out var requesterId))
            SetKeyState(uid, holder, null, requesterId);
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

        var hasFrequencyPassword = false;
        FrequencyProfile? frequencyProfile = null;
        if (frequency is { } requestedFrequency)
            hasFrequencyPassword = TryGetFrequencyProfile(requestedFrequency, out frequencyProfile);
        if (hasFrequencyPassword)
        {
            state.RequiresPassword = true;
            state.Authorized = request?.Password != null &&
                TelecomPasswordService.VerifyPassword(request.Password, frequencyProfile!.PasswordSalt,
                    frequencyProfile.PasswordHash, frequencyProfile.PasswordIterations);
        }
        else
        {
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
        }

        if (state.RequiresPassword && !state.Authorized)
        {
            state.Error = request?.Password == null
                ? hasFrequencyPassword ? "telecom-key-console-frequency-password-required" : "telecom-console-password-required"
                : hasFrequencyPassword ? "telecom-key-console-frequency-password-invalid" : "telecom-console-invalid-password";
            _ui.SetUiState(uid, TelecomConsoleUiKey.Log, state);
            return;
        }

        if (frequency is null &&
            request?.Channel?.Equals(RadioChannelPrototype.CustomChannelId, StringComparison.OrdinalIgnoreCase) == true)
        {
            state.Error = "telecom-key-console-frequency-required-for-logs";
            _ui.SetUiState(uid, TelecomConsoleUiKey.Log, state);
            return;
        }

        if (frequency is null && !state.Authorized)
            entries = entries
                .Where(entry => !entry.Channel.Equals(RadioChannelPrototype.CustomChannelId, StringComparison.OrdinalIgnoreCase))
                .ToArray();

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
        if (!TryComp(uid, out TransformComponent? transform) ||
            !TryComp(uid, out EncryptionKeyHolderComponent? holder) ||
            !holder.KeysUnlocked ||
            !TryGetActorCharacter(args.Actor, out var requesterId))
            return;

        var tag = args.Tag?.Trim();
        if (!string.IsNullOrEmpty(tag) && tag.Length != 1)
        {
            SetKeyState(uid, holder, "telecom-key-console-invalid-tag", requesterId);
            return;
        }

        if (args.Frequency < 1)
        {
            SetKeyState(uid, holder, "telecom-key-console-invalid-frequency", requesterId);
            return;
        }

        if (!_prototypes.TryIndex<RadioChannelPrototype>(RadioChannelPrototype.CustomChannelId, out var carrierChannel))
        {
            SetKeyState(uid, holder, "telecom-key-console-invalid-frequency", requesterId);
            return;
        }

        var requestedCode = string.IsNullOrWhiteSpace(tag) ? (char?) null : char.ToLowerInvariant(tag![0]);
        if (requestedCode is { } reservedCode &&
            (reservedCode == SharedChatSystem.DefaultChannelKey ||
             _prototypes.EnumeratePrototypes<RadioChannelPrototype>()
                 .Any(channel => char.ToLowerInvariant(channel.KeyCode) == reservedCode) ||
             TryGetCodeFrequency(reservedCode, out var reservedFrequency) && reservedFrequency != args.Frequency))
        {
            SetKeyState(uid, holder, "telecom-key-console-invalid-tag", requesterId);
            return;
        }

        var profileExists = TryGetFrequencyProfile(args.Frequency, out var profile);
        if (profileExists && profile!.Owner != requesterId)
        {
            SetKeyState(uid, holder, "telecom-key-console-frequency-taken", requesterId);
            return;
        }

        var ownedFrequencies = GetOwnedFrequencies(requesterId);
        if (!ownedFrequencies.Contains(args.Frequency) && ownedFrequencies.Count >= 3)
        {
            SetKeyState(uid, holder, "telecom-key-console-max-frequencies", requesterId);
            return;
        }

        var newProfile = false;
        if (!profileExists)
        {
            if (string.IsNullOrWhiteSpace(args.Password))
            {
                SetKeyState(uid, holder, "telecom-key-console-frequency-password-required", requesterId);
                return;
            }

            var password = TelecomPasswordService.HashPassword(args.Password);
            profile = new FrequencyProfile
            {
                Owner = requesterId,
                ChannelName = NullIfEmpty(args.ChannelName) ?? $"Канал {args.Frequency}",
                Color = ParseColor(args.Color, Color.Green),
                GradientColor = ParseNullableColor(args.GradientColor),
                PasswordSalt = password.Salt,
                PasswordHash = password.Hash,
                PasswordIterations = password.Iterations,
            };
            _frequencyProfiles[args.Frequency] = profile;
            newProfile = true;
        }
        else if (profile!.PasswordHash is null || profile.PasswordSalt is null)
        {
            if (string.IsNullOrWhiteSpace(args.Password))
            {
                SetKeyState(uid, holder, "telecom-key-console-frequency-password-required", requesterId);
                return;
            }

            var password = TelecomPasswordService.HashPassword(args.Password);
            profile.PasswordSalt = password.Salt;
            profile.PasswordHash = password.Hash;
            profile.PasswordIterations = password.Iterations;
        }
        else if (!TelecomPasswordService.VerifyPassword(args.Password ?? string.Empty, profile.PasswordSalt, profile.PasswordHash, profile.PasswordIterations))
        {
            SetKeyState(uid, holder, "telecom-key-console-frequency-password-invalid", requesterId);
            return;
        }

        var key = Spawn("EncryptionKeyCommon", transform.Coordinates);
        var component = EnsureComp<EncryptionKeyComponent>(key);
        component.Channels.Clear();
        component.Channels.Add(RadioChannelPrototype.CustomChannelId);
        component.DefaultChannel = null;
        component.CustomFrequency = args.Frequency;
        component.ChannelName = profile!.ChannelName;
        component.CustomKeyCode = requestedCode;
        component.Tag = NullIfEmpty(args.Tag);
        component.Color = profile.Color;
        component.GradientColor = profile.GradientColor;
        component.OwnerCharacter = requesterId;
        component.FrequencyPasswordSalt = profile.PasswordSalt;
        component.FrequencyPasswordHash = profile.PasswordHash;
        component.FrequencyPasswordIterations = profile.PasswordIterations;

        if (!_containers.Insert(key, holder.KeyContainer))
        {
            QueueDel(key);
            if (newProfile)
                _frequencyProfiles.Remove(args.Frequency);
            return;
        }

        Dirty(key, component);
        _keys.UpdateChannels(uid, holder);
        SetKeyState(uid, holder, null, requesterId);
    }

    private void OnToggleLock(EntityUid uid, TelecomKeyServiceConsoleComponent _, TelecomToggleLockMessage args)
    {
        if (!TryComp(uid, out EncryptionKeyHolderComponent? holder) ||
            !TryGetActorCharacter(args.Actor, out var requesterId))
            return;
        holder.KeysUnlocked = !args.Locked;
        SetKeyState(uid, holder, null, requesterId);
    }

    private void OnDeleteKey(EntityUid uid, TelecomKeyServiceConsoleComponent _, TelecomDeleteKeyMessage args)
    {
        if (!TryGetActorCharacter(args.Actor, out var requesterId) ||
            !TryGetEntity(args.Key, out var keyUid) ||
            keyUid is not { } resolvedKeyUid ||
            !TryComp(resolvedKeyUid, out EncryptionKeyComponent? key) ||
            key.OwnerCharacter != requesterId)
            return;

        var frequency = key.CustomFrequency;
        RemoveKey(resolvedKeyUid);
        if (frequency is { } deletedFrequency &&
            !GetOwnedKeys(requesterId).Any(entry => entry.Key.CustomFrequency == deletedFrequency && entry.Uid != resolvedKeyUid))
            _frequencyProfiles.Remove(deletedFrequency);

        RefreshKeyState(uid, requesterId);
        PlayKeyDeletionSound(uid);
    }

    private void OnDeleteAllKeys(EntityUid uid, TelecomKeyServiceConsoleComponent _, TelecomDeleteAllKeysMessage args)
    {
        if (!TryGetActorCharacter(args.Actor, out var requesterId))
            return;

        var frequencies = GetOwnedFrequencies(requesterId);
        foreach (var entry in GetOwnedKeys(requesterId))
            RemoveKey(entry.Uid);

        foreach (var frequency in frequencies)
            _frequencyProfiles.Remove(frequency);

        RefreshKeyState(uid, requesterId);
        PlayKeyDeletionSound(uid);
    }

    private void OnDetachFrequency(EntityUid uid, TelecomKeyServiceConsoleComponent _, TelecomDetachFrequencyMessage args)
    {
        if (!TryGetActorCharacter(args.Actor, out var requesterId) ||
            !TryGetFrequencyProfile(args.Frequency, out var profile) ||
            profile!.Owner != requesterId)
            return;

        foreach (var entry in GetOwnedKeys(requesterId)
                     .Where(entry => entry.Key.CustomFrequency == args.Frequency))
            RemoveKey(entry.Uid);

        _frequencyProfiles.Remove(args.Frequency);
        RefreshKeyState(uid, requesterId);
        PlayKeyDeletionSound(uid);
    }

    private void RefreshKeyState(EntityUid uid, NetEntity requesterId)
    {
        if (TryComp(uid, out EncryptionKeyHolderComponent? holder))
        {
            _keys.UpdateChannels(uid, holder);
            SetKeyState(uid, holder, null, requesterId);
        }
    }

    private void RemoveKey(EntityUid keyUid)
    {
        _containers.TryRemoveFromContainer(keyUid, force: true);
        QueueDel(keyUid);
    }

    private void PlayKeyDeletionSound(EntityUid uid)
    {
        if (TryComp(uid, out TransformComponent? _))
        {
            var sound = _audio.ResolveSound(new SoundPathSpecifier("/Audio/Effects/Emotes/clap-single.ogg"));
            _globalSound.PlayGlobalOnStation(uid, sound);
        }
    }

    private void SetKeyState(EntityUid uid, EncryptionKeyHolderComponent holder, string? error, NetEntity? requesterId = null)
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

        if (requesterId is { } userId)
            state.Keys.AddRange(GetOwnedKeyInfos(userId));

        _ui.SetUiState(uid, TelecomConsoleUiKey.KeyService, state);
    }

    private bool TryGetActorCharacter(EntityUid actor, out NetEntity character)
    {
        if (TryComp<ActorComponent>(actor, out _))
        {
            character = GetNetEntity(actor);
            return true;
        }

        character = default;
        return false;
    }

    private bool TryGetFrequencyProfile(int frequency, out FrequencyProfile? profile)
    {
        if (_frequencyProfiles.TryGetValue(frequency, out profile))
            return true;

        var keys = EntityQueryEnumerator<EncryptionKeyComponent>();
        while (keys.MoveNext(out var key))
        {
            if (key.CustomFrequency != frequency || key.OwnerCharacter is not { } owner)
                continue;

            profile = new FrequencyProfile
            {
                Owner = owner,
                ChannelName = key.ChannelName ?? $"Канал {frequency}",
                Color = key.Color ?? Color.Green,
                GradientColor = key.GradientColor,
                PasswordSalt = key.FrequencyPasswordSalt,
                PasswordHash = key.FrequencyPasswordHash,
                PasswordIterations = key.FrequencyPasswordIterations,
            };
            _frequencyProfiles[frequency] = profile;
            return true;
        }

        profile = null;
        return false;
    }

    private HashSet<int> GetOwnedFrequencies(NetEntity owner)
    {
        var frequencies = new HashSet<int>();
        foreach (var (frequency, profile) in _frequencyProfiles)
        {
            if (profile.Owner == owner)
                frequencies.Add(frequency);
        }

        foreach (var entry in GetOwnedKeys(owner))
        {
            if (entry.Key.CustomFrequency is { } frequency)
                frequencies.Add(frequency);
        }

        return frequencies;
    }

    private IEnumerable<(EntityUid Uid, EncryptionKeyComponent Key)> GetOwnedKeys(NetEntity owner)
    {
        var keys = EntityQueryEnumerator<EncryptionKeyComponent>();
        while (keys.MoveNext(out var uid, out var key))
        {
            if (key.OwnerCharacter == owner && key.CustomFrequency is not null)
                yield return (uid, key);
        }
    }

    private IEnumerable<TelecomOwnedKeyInfo> GetOwnedKeyInfos(NetEntity owner)
    {
        return GetOwnedKeys(owner)
            .OrderBy(entry => entry.Key.CustomFrequency)
            .ThenBy(entry => entry.Key.ChannelName)
            .Select(entry => new TelecomOwnedKeyInfo
            {
                Key = GetNetEntity(entry.Uid),
                Frequency = entry.Key.CustomFrequency!.Value,
                Name = entry.Key.ChannelName ?? string.Empty,
                Code = entry.Key.CustomKeyCode?.ToString() ?? string.Empty,
                Color = entry.Key.Color?.ToHex(),
                GradientColor = entry.Key.GradientColor?.ToHex(),
            })
            .ToList();
    }

    private bool TryGetCodeFrequency(char code, out int frequency)
    {
        var keys = EntityQueryEnumerator<EncryptionKeyComponent>();
        while (keys.MoveNext(out var key))
        {
            if (key.CustomKeyCode == code && key.CustomFrequency is { } keyFrequency)
            {
                frequency = keyFrequency;
                return true;
            }
        }

        frequency = default;
        return false;
    }

    private static Color ParseColor(string? value, Color fallback) =>
        value != null && Color.TryParse(value, out var color) ? color : fallback;

    private static Color? ParseNullableColor(string? value) =>
        value != null && Color.TryParse(value, out var color) ? color : null;

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