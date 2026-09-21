using Content.Server._NF.Radio; // Frontier
using Content.Server.Atmos.EntitySystems;
using Content.Server.Administration.Logs;
using Content.Server.Chat.Systems;
using Content.Server._EinsteinEngines.Language;
using Content.Server.Power.Components;
using Content.Server.Radio.Components;
using Content.Shared._Mono.Radio;
using Content.Shared.Chat;
using Content.Shared.Corvax.TTS; // Corvax-TTS
using Content.Shared.Database;
using Content.Shared.Examine;
using Content.Shared._EinsteinEngines.Language;
using Content.Shared._EinsteinEngines.Language.Systems;
using Content.Shared.Atmos;
using Content.Shared.Radio;
using Content.Shared.Radio.Components;
using Content.Shared.Speech;
using Content.Shared.Ghost; // Nuclear-14
using Robust.Shared.Map;
using Robust.Server.GameObjects;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Replays;
using Robust.Shared.Utility;
using System.Linq;
using System.Text;

namespace Content.Server.Radio.EntitySystems;

/// <summary>
///     This system handles intrinsic radios and the general process of converting radio messages into chat messages.
/// </summary>
public sealed partial class RadioSystem : EntitySystem
{
    [Dependency] private INetManager _netMan = default!;
    [Dependency] private IReplayRecordingManager _replay = default!;
    [Dependency] private IAdminLogManager _adminLogger = default!;
    [Dependency] private IPrototypeManager _prototype = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private ChatSystem _chat = default!;
    [Dependency] private LanguageSystem _language = default!; // Einstein Engines - Language
    [Dependency] private AtmosphereSystem _atmosphere = default!;
    [Dependency] private TransformSystem _transform = default!;

    // set used to prevent radio feedback loops.
    private readonly HashSet<string> _messages = new();
    private readonly List<TelecomRadioLogEntry> _telecomLogs = new();
    private const int MaxTelecomLogEntries = 4096;

    private EntityQuery<TelecomExemptComponent> _exemptQuery;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<IntrinsicRadioReceiverComponent, RadioReceiveEvent>(OnIntrinsicReceive);
        SubscribeLocalEvent<IntrinsicRadioTransmitterComponent, EntitySpokeEvent>(OnIntrinsicSpeak);

        _exemptQuery = GetEntityQuery<TelecomExemptComponent>();
    }


    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var servers = EntityQueryEnumerator<TelecomServerComponent, ApcPowerReceiverComponent, TransformComponent>();
        while (servers.MoveNext(out var uid, out var telecom, out var power, out var transform))
        {
            var mixture = _atmosphere.GetTileMixture(transform.GridUid, transform.MapUid,
                _transform.GetGridTilePositionOrDefault((uid, transform)), true);
            if (mixture == null)
                continue;

            if (power.PowerDisabled && telecom.InterferenceThreshold > 0 &&
                mixture.Temperature < telecom.InterferenceThreshold)
                power.PowerDisabled = false;

            if (power.Powered && telecom.HeatGeneration > 0)
            {
                var heatCapacity = _atmosphere.GetHeatCapacity(mixture, true);
                if (heatCapacity > Atmospherics.MinimumHeatCapacity)
                    mixture.Temperature += telecom.HeatGeneration * frameTime / heatCapacity;

                if (telecom.CriticalTemperature > 0 && mixture.Temperature >= telecom.CriticalTemperature)
                    power.PowerDisabled = true;
            }
        }
    }

    private void OnIntrinsicSpeak(EntityUid uid, IntrinsicRadioTransmitterComponent component, EntitySpokeEvent args)
    {
        if (args.Channel != null && component.Channels.Contains(args.Channel.ID))
        {
            SendRadioMessage(uid, args.Message, args.Channel, uid, language: args.Language); // Einstein Engines - Language
            args.Channel = null; // prevent duplicate messages from other listeners.
        }

    }

    //Nuclear-14
    /// <summary>
    /// Gets the message frequency, if there is no such frequency, returns the standard channel frequency.
    /// </summary>
    public int GetFrequency(EntityUid source, RadioChannelPrototype channel)
    {
        if (channel.ID == RadioChannelPrototype.CustomChannelId &&
            channel.RuntimeName != null &&
            channel.Frequency > 0)
            return channel.Frequency;

        if (TryComp<RadioMicrophoneComponent>(source, out var radioMicrophone))
            return radioMicrophone.Frequency;

        if (TryComp<EncryptionKeyHolderComponent>(source, out var holder))
        {
            foreach (var keyUid in holder.KeyContainer.ContainedEntities)
            {
                if (TryComp<EncryptionKeyComponent>(keyUid, out var key) &&
                    key.Channels.Contains(channel.ID) &&
                    key.CustomFrequency is { } customFrequency)
                    return customFrequency;
            }
        }

        return channel.Frequency;
    }

    private void OnIntrinsicReceive(EntityUid uid, IntrinsicRadioReceiverComponent component, ref RadioReceiveEvent args)
    {
        if (TryComp(uid, out ActorComponent? actor))
        {
            // Einstein Engines - Languages begin
            var listener = component.Owner;
            var msg = args.OriginalChatMsg;

            if (listener != null && !_language.CanUnderstand(listener, args.Language.ID))
                msg = args.LanguageObfuscatedChatMsg;

            _netMan.ServerSendMessage(new MsgChatMessage { Message = msg }, actor.PlayerSession.Channel);
            // Einstein Engines - Languages end

            // Send radio noise event to client for IPCs
            var radioNoiseEvent = new RadioNoiseEvent(GetNetEntity(uid), args.Channel.ID);
            RaiseNetworkEvent(radioNoiseEvent, actor.PlayerSession);
// Corvax-TTS-start:

            if (uid != args.MessageSource &&
                HasComp<TTSComponent>(args.MessageSource) &&
                !args.Receivers.Contains(uid))
            {
                args.Receivers.Add(uid);
            }
// Corvax-TTS-end.
        }
    }

    /// <summary>
    /// Send radio message to all active radio listeners
    /// </summary>
    public void SendRadioMessage(
        EntityUid messageSource,
        string message,
        ProtoId<RadioChannelPrototype> channel,
        EntityUid radioSource,
        int? frequency = null,
        LanguagePrototype? language = null,
        bool escapeMarkup = true) // Frontier: added frequency
    {
        SendRadioMessage(messageSource, message, _prototype.Index(channel), radioSource, frequency: frequency, escapeMarkup: escapeMarkup, language: language); // Frontier: added frequency / Einstein Engines - Language
    }

    /// <summary>
    /// Send radio message to all active radio listeners
    /// </summary>
    /// <param name="messageSource">Entity that spoke the message</param>
    /// <param name="radioSource">Entity that picked up the message and will send it, e.g. headset</param>
    public void SendRadioMessage(
        EntityUid messageSource,
        string message,
        RadioChannelPrototype channel,
        EntityUid radioSource,
        int? frequency = null,
        LanguagePrototype? language = null,
        bool escapeMarkup = true) // Nuclear-14: add frequency
    {
        // Einstein Engines - Language begin
        if (language == null)
            language = _language.GetLanguage(messageSource);

        if (!language.SpeechOverride.AllowRadio)
            return;
        // Einstein Engines - Language end

        // TODO if radios ever garble / modify messages, feedback-prevention needs to be handled better than this.
        if (!_messages.Add(message))
            return;

        var evt = new TransformSpeakerNameEvent(messageSource, MetaData(messageSource).EntityName);
        RaiseLocalEvent(messageSource, evt);

        // Frontier: add name transform event
        var transformEv = new RadioTransformMessageEvent(channel, radioSource, evt.VoiceName, message, messageSource);
        RaiseLocalEvent(radioSource, ref transformEv);
        message = transformEv.Message;
        messageSource = transformEv.MessageSource;
        // End Frontier

        var name = transformEv.Name; // Frontier: evt.VoiceName<transformEv.Name
        name = FormattedMessage.EscapeText(name);

        SpeechVerbPrototype speech;
        if (evt.SpeechVerb != null && _prototype.TryIndex(evt.SpeechVerb, out var evntProto))
            speech = evntProto;
        else
            speech = _chat.GetSpeechVerb(messageSource, message);

        var content = escapeMarkup
            ? FormattedMessage.EscapeText(message)
            : message;
        var sourceMapId = Transform(radioSource).MapID;
        var interference = GetInterferenceLevel(sourceMapId, channel.ID);
        if (interference > 0)
            message = AddRadioInterference(message, interference);

        // Frontier: append frequency if the channel requests it
        string channelText;
        if (channel.ShowFrequency)
            channelText = $"\\[{channel.LocalizedName} ({frequency})\\]";
        else
            channelText = $"\\[{channel.LocalizedName}\\]";
        // End Frontier

        // var wrappedMessage = Loc.GetString(speech.Bold ? "chat-radio-message-wrap-bold" : "chat-radio-message-wrap",
        //     ("color", channel.Color),
        //     ("fontType", speech.FontId),
        //     ("fontSize", speech.FontSize),
        //     ("verb", Loc.GetString(_random.Pick(speech.SpeechVerbStrings))),
        //     ("channel", channelText), // Frontier: $"\\[{channel.LocalizedName}\\]"<channelText
        //     ("name", name),
        //     ("message", content));
        var wrappedMessage = WrapRadioMessage(messageSource, radioSource, channel, name, content, language); // Einstein Engines - Language

        // most radios are relayed to chat, so lets parse the chat message beforehand
        // var chat = new ChatMessage(
        //     ChatChannel.Radio,
        //     message,
        //     wrappedMessage,
        //     NetEntity.Invalid,
        //     null);
        // var chatMsg = new MsgChatMessage { Message = chat };
        // var ev = new RadioReceiveEvent(message, messageSource, channel, radioSource, chatMsg);

        var msg = new ChatMessage(ChatChannel.Radio, content, wrappedMessage, NetEntity.Invalid, null); // Einstein Engines - Language

        // Einstein Engines - Language begin
        var obfuscated = _language.ObfuscateSpeech(content, language);
        var obfuscatedWrapped = WrapRadioMessage(messageSource, radioSource, channel, name, obfuscated, language);
        var notUdsMsg = new ChatMessage(ChatChannel.Radio, obfuscated, obfuscatedWrapped, NetEntity.Invalid, null);
        var ev = new RadioReceiveEvent(messageSource, channel, msg, notUdsMsg, language, radioSource, []);
        // Einstein Engines - Language end

        var sendAttemptEv = new RadioSendAttemptEvent(channel, radioSource);
        RaiseLocalEvent(ref sendAttemptEv);
        RaiseLocalEvent(radioSource, ref sendAttemptEv);
        var canSend = !sendAttemptEv.Cancelled;

        var sourceServerExempt = _exemptQuery.HasComp(radioSource);

        var radioQuery = EntityQueryEnumerator<ActiveRadioComponent, TransformComponent>();
        var delivered = false;

        if (frequency == null) // Nuclear-14
            frequency = GetFrequency(radioSource, channel); // Nuclear-14

        var hasActiveServer = HasActiveServer(sourceMapId, channel.ID, frequency.Value);

        while (canSend && radioQuery.MoveNext(out var receiver, out var radio, out var transform))
        {
            if (!radio.ReceiveAllChannels)
            {
                if (!radio.Channels.Contains(channel.ID) || (TryComp<IntercomComponent>(receiver, out var intercom) &&
                                                             !intercom.SupportedChannels.Contains(channel.ID)))
                    continue;
            }

            if (!HasComp<GhostComponent>(receiver) && GetFrequency(receiver, channel) != frequency) // Nuclear-14
                continue; // Nuclear-14

            // if (!channel.LongRange && transform.MapID != sourceMapId && !radio.GlobalReceive)
            //     continue;

            // Check if within range for range-limited channels
            if (channel.MaxRange.HasValue && channel.MaxRange.Value > 0)
            {
                var sourcePos = Transform(radioSource).Coordinates;
                var targetPos = transform.Coordinates;

                // Check distance between sender and receiver
                if (!sourcePos.TryDistance(EntityManager, targetPos, out var distance) || distance > channel.MaxRange.Value)
                    continue;
            }

            // don't need telecom server for long range channels or handheld radios and intercoms
            var needServer = !channel.LongRange && !sourceServerExempt;
            if (needServer && !hasActiveServer)
                continue;

            // check if message can be sent to specific receiver
            var attemptEv = new RadioReceiveAttemptEvent(channel, radioSource, receiver);
            RaiseLocalEvent(ref attemptEv);
            RaiseLocalEvent(receiver, ref attemptEv);
            if (attemptEv.Cancelled)
                continue;

            // send the message
            RaiseLocalEvent(receiver, ref ev);
            delivered = true;
        }

        if (delivered && HasLoggingServer(sourceMapId, channel.ID))
        {
            _telecomLogs.Add(new TelecomRadioLogEntry(
                DateTime.UtcNow, sourceMapId, channel.ID, frequency.Value, transformEv.Name, message));
            if (_telecomLogs.Count > MaxTelecomLogEntries)
                _telecomLogs.RemoveRange(0, _telecomLogs.Count - MaxTelecomLogEntries);
        }

        RaiseLocalEvent(new RadioSpokeEvent(messageSource, content, obfuscated, language, ev.Receivers.ToArray())); // Corvax-TTS

        if (name != Name(messageSource))
            _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Radio message from {ToPrettyString(messageSource):user} as {name} on {channel.LocalizedName}: {message}");
        else
            _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Radio message from {ToPrettyString(messageSource):user} on {channel.LocalizedName}: {message}");

        _replay.RecordServerMessage(msg); // Einstein Engines - Language
        _messages.Remove(message);
    }

    // Einstein Engines - Language begin
    private string WrapRadioMessage(
        EntityUid source,
        EntityUid radioSource,
        RadioChannelPrototype channel,
        string name,
        string message,
        LanguagePrototype language)
    {
        // TODO: code duplication with ChatSystem.WrapMessage
        var speech = _chat.GetSpeechVerb(source, message);
        var customKey = FindCustomKey(radioSource, channel);
        var channelName = customKey?.ChannelName ?? channel.LocalizedName;
        var channelColor = customKey?.Color ?? channel.Color;
        var languageColor = channelColor;

        if (language.SpeechOverride.Color is { } colorOverride)
            languageColor = Color.InterpolateBetween(Color.White, colorOverride, colorOverride.A); // Changed first param to Color.White so it shows color correctly.

        var languageDisplay = language.IsVisibleLanguage
            ? Loc.GetString("chat-manager-language-prefix", ("language", language.ChatName))
            : "";

        return Loc.GetString(speech.Bold ? "chat-radio-message-wrap-bold" : "chat-radio-message-wrap",
            ("color", channelColor),
            ("languageColor", languageColor),
            ("fontType", language.SpeechOverride.FontId ?? speech.FontId),
            ("fontSize", language.SpeechOverride.FontSize ?? speech.FontSize),
            ("verb", Loc.GetString(_random.Pick(speech.SpeechVerbStrings))),
            ("channel", $"\\[{channelName}\\]"),
            ("name", name),
            ("message", message),
            ("language", languageDisplay));
    }

    private EncryptionKeyComponent? FindCustomKey(EntityUid radioSource, RadioChannelPrototype channel)
    {
        if (!TryComp<EncryptionKeyHolderComponent>(radioSource, out var holder))
            return null;

        foreach (var keyUid in holder.KeyContainer.ContainedEntities)
        {
            if (TryComp<EncryptionKeyComponent>(keyUid, out var key) &&
                key.Channels.Contains(channel.ID) &&
                key.ChannelName != null &&
                (!key.CustomFrequency.HasValue || key.CustomFrequency == channel.Frequency))
                return key;
        }

        return null;
    }
    // Einstein Engines - Language end

    /// <inheritdoc cref="TelecomServerComponent"/>
    private bool HasActiveServer(MapId mapId, string channelId, int frequency)
    {
        if (IsRestrictedChannel(channelId))
            return false;
        if (!_prototype.TryIndex<RadioChannelPrototype>(channelId, out var channel))
            return false;

        var servers = EntityQueryEnumerator<TelecomServerComponent, EncryptionKeyHolderComponent, ApcPowerReceiverComponent, TransformComponent>();
        while (servers.MoveNext(out var uid, out var telecom, out var keys, out var power, out var transform))
        {
            if (transform.MapID == mapId &&
                power.Powered &&
                keys.Channels.Contains(channelId) &&
                GetFrequency(uid, channel) == frequency &&
                IsBelowInterferenceThreshold(uid, telecom) &&
                (!telecom.ServiceKeyRequired ||
                 (telecom.ServiceKeyChannel != null && keys.Channels.Contains(telecom.ServiceKeyChannel))))
            {
                return true;
            }
        }
        return false;
    }

    private bool HasLoggingServer(MapId mapId, string channelId)
    {
        if (IsRestrictedChannel(channelId))
            return false;

        var servers = EntityQueryEnumerator<TelecomServerComponent, EncryptionKeyHolderComponent, ApcPowerReceiverComponent, TransformComponent>();
        while (servers.MoveNext(out var uid, out var telecom, out var keys, out var power, out var transform))
        {
            if (transform.MapID == mapId && power.Powered &&
                telecom.HasMode(TelecomServerMode.Logs_Save) &&
                keys.Channels.Contains(channelId) &&
                IsBelowInterferenceThreshold(uid, telecom) &&
                (!telecom.ServiceKeyRequired ||
                 (telecom.ServiceKeyChannel != null && keys.Channels.Contains(telecom.ServiceKeyChannel))))
                return true;
        }

        return false;
    }

    private bool IsBelowInterferenceThreshold(EntityUid uid, TelecomServerComponent telecom)
    {
        if (telecom.InterferenceThreshold <= 0)
            return true;

        if (!TryComp(uid, out TransformComponent? transform))
            return true;

        var mixture = _atmosphere.GetTileMixture(transform.GridUid, transform.MapUid,
            _transform.GetGridTilePositionOrDefault((uid, transform)), true);
        return mixture == null || telecom.CriticalTemperature <= 0 ||
               mixture.Temperature < telecom.CriticalTemperature;
    }

    private int GetInterferenceLevel(MapId mapId, string channelId)
    {
        var level = 0;
        var servers = EntityQueryEnumerator<TelecomServerComponent, EncryptionKeyHolderComponent,
            ApcPowerReceiverComponent, TransformComponent>();
        while (servers.MoveNext(out var uid, out var telecom, out var keys, out var power, out var transform))
        {
            if (transform.MapID != mapId || !power.Powered || !keys.Channels.Contains(channelId) ||
                telecom.InterferenceThreshold <= 0 || !TryGetAtmosphereTemperature(uid, out var temperature))
                continue;

            if (temperature >= telecom.CriticalTemperature)
                return 3;

            var step = (telecom.CriticalTemperature - telecom.InterferenceThreshold) / 3f;
            if (temperature >= telecom.InterferenceThreshold + step * 2)
                level = Math.Max(level, 3);
            else if (temperature >= telecom.InterferenceThreshold + step)
                level = Math.Max(level, 2);
            else if (temperature >= telecom.InterferenceThreshold)
                level = Math.Max(level, 1);
        }

        return level;
    }

    private bool TryGetAtmosphereTemperature(EntityUid uid, out float temperature)
    {
        temperature = 0;
        if (!TryComp(uid, out TransformComponent? transform))
            return false;

        var mixture = _atmosphere.GetTileMixture(transform.GridUid, transform.MapUid,
            _transform.GetGridTilePositionOrDefault((uid, transform)), true);
        if (mixture == null)
            return false;

        temperature = mixture.Temperature;
        return true;
    }

    private static string AddRadioInterference(string message, int level)
    {
        var interval = level switch
        {
            1 => 10,
            2 => 5,
            _ => 1
        };
        var result = new StringBuilder(message.Length + message.Length / interval + 1);
        for (var i = 0; i < message.Length; i++)
        {
            if (i > 0 && i % interval == 0)
                result.Append('#');
            result.Append(message[i]);
        }
        return result.ToString();
    }

    /// <summary>Returns radio records saved by telecom servers, applying all supplied filters.</summary>
    public IReadOnlyList<TelecomRadioLogEntry> QueryTelecomLogs(TelecomRadioLogFilter filter)
    {
        return _telecomLogs.Where(entry =>
                !IsRestrictedChannel(entry.Channel) &&
                (!filter.Map.HasValue || entry.Map == filter.Map.Value) &&
                (string.IsNullOrWhiteSpace(filter.Channel) ||
                 entry.Channel.Equals(filter.Channel, StringComparison.OrdinalIgnoreCase)) &&
                (!filter.Frequency.HasValue || entry.Frequency == filter.Frequency.Value) &&
                (string.IsNullOrWhiteSpace(filter.Speaker) ||
                 entry.Speaker.Contains(filter.Speaker, StringComparison.OrdinalIgnoreCase)) &&
                (!filter.From.HasValue || entry.Timestamp >= filter.From.Value) &&
                (!filter.To.HasValue || entry.Timestamp <= filter.To.Value) &&
                (string.IsNullOrWhiteSpace(filter.Words) ||
                 filter.Words.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                     .All(word => entry.Message.Contains(word, StringComparison.OrdinalIgnoreCase))))
            .ToArray();
    }

    private static bool IsRestrictedChannel(string id) =>
        id.Equals("Syndicate", StringComparison.OrdinalIgnoreCase) ||
        id.Equals("Binary", StringComparison.OrdinalIgnoreCase) ||
        id.Equals("Chimera", StringComparison.OrdinalIgnoreCase) ||
        id.Contains("Collective", StringComparison.OrdinalIgnoreCase);
}
