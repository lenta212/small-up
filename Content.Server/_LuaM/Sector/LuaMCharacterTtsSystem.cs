using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Shared._LuaM.Sector;
using Content.Shared.CCVar;
using Robust.Server.Player;
using Robust.Shared.Asynchronous;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server._LuaM.Sector;

public sealed class LuaMCharacterTtsSystem : EntitySystem
{
    private static readonly string[] FallbackVoices =
    [
        "ru_RU-irina-medium",
        "ru_RU-denis-medium",
        "ru_RU-dmitri-medium",
        "ru_RU-ruslan-medium",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPlayerManager _players = default!;
    [Dependency] private readonly ITaskManager _task = default!;
    [Dependency] private readonly ILogManager _log = default!;

    private readonly Queue<string> _queue = new();
    private readonly Dictionary<string, PendingCharacterTtsRequest> _pending = new();
    private readonly Dictionary<EntityUid, TimeSpan> _nextBySpeaker = new();
    private HttpClient _http = new();
    private CancellationTokenSource? _inFlightCancellation;
    private ISawmill _sawmill = default!;
    private bool _inFlight;

    public override void Initialize()
    {
        base.Initialize();

        _sawmill = _log.GetSawmill("luam.character_tts");
        SubscribeLocalEvent<EntityTerminatingEvent>(OnEntityTerminating);
    }

    public override void Shutdown()
    {
        _inFlightCancellation?.Cancel();
        CancelPendingRequests();
        _queue.Clear();
        base.Shutdown();

        _http.Dispose();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        UpdateQueue();
    }

    public void QueueLocalSpeech(
        EntityUid speaker,
        string rawText,
        IEnumerable<ICommonSession> recipients,
        float maxDistance)
    {
        QueueSpeech(speaker, "local", rawText, recipients);
    }

    public void QueueWhisper(
        EntityUid speaker,
        string rawText,
        IEnumerable<ICommonSession> recipients,
        float maxDistance)
    {
        QueueSpeech(speaker, "whisper", rawText, recipients);
    }

    public void QueueRadioSpeech(
        EntityUid speaker,
        string channelId,
        string rawText,
        IEnumerable<ICommonSession> recipients)
    {
        QueueSpeech(speaker, $"radio:{channelId}", rawText, recipients);
    }

    private void QueueSpeech(
        EntityUid speaker,
        string sourceKey,
        string rawText,
        IEnumerable<ICommonSession> recipients)
    {
        if (!_cfg.GetCVar(CCVars.LuaMCharacterTtsEnabled))
            return;

        if (string.IsNullOrWhiteSpace(_cfg.GetCVar(CCVars.LuaMAiDirectorGatewayUrl)))
            return;

        var text = NormalizeTtsText(rawText, GetCharacterMaxChars());
        if (string.IsNullOrWhiteSpace(text))
            return;

        var actor = NormalizeActorName(Name(speaker));
        var voice = SelectVoice(actor);
        var key = $"{sourceKey}:{GetNetEntity(speaker)}:{voice}:{text}";

        if (_pending.TryGetValue(key, out var existing))
        {
            AddRecipients(existing, recipients);
            return;
        }

        if (_queue.Count >= GetTtsMaxQueue())
        {
            _sawmill.Debug($"LuaM character TTS queue is full; dropped line from {Name(speaker)}.");
            return;
        }

        var pending = new PendingCharacterTtsRequest(
            key,
            text,
            actor,
            voice);

        AddRecipients(pending, recipients);
        if (pending.RecipientIds.Count == 0)
            return;

        if (!TryPassSpeakerCooldown(speaker))
            return;

        _pending[key] = pending;
        _queue.Enqueue(key);
    }

    private bool TryPassSpeakerCooldown(EntityUid speaker)
    {
        var now = _timing.CurTime;
        if (_nextBySpeaker.TryGetValue(speaker, out var next) && now < next)
            return false;

        _nextBySpeaker[speaker] = now + TimeSpan.FromSeconds(GetCharacterCooldown());
        return true;
    }

    private static void AddRecipients(PendingCharacterTtsRequest pending, IEnumerable<ICommonSession> recipients)
    {
        foreach (var recipient in recipients)
        {
            if (recipient.Status == SessionStatus.InGame)
                pending.RecipientIds.Add(recipient.UserId);
        }
    }

    private void UpdateQueue()
    {
        if (!_cfg.GetCVar(CCVars.LuaMCharacterTtsEnabled))
        {
            _queue.Clear();
            CancelPendingRequests();
            _inFlightCancellation?.Cancel();
            return;
        }

        if (_inFlight)
        {
            return;
        }

        while (_queue.Count > 0)
        {
            var key = _queue.Dequeue();
            if (!_pending.TryGetValue(key, out var pending) ||
                pending.RecipientIds.Count == 0)
            {
                _pending.Remove(key);
                continue;
            }

            _inFlight = true;
            _inFlightCancellation = new CancellationTokenSource();
            _ = RequestAndSendAsync(pending, _inFlightCancellation.Token);
            return;
        }
    }

    private async Task RequestAndSendAsync(PendingCharacterTtsRequest pending, CancellationToken cancel)
    {
        try
        {
            var response = await RequestGatewayTtsAsync(pending, cancel);
            await RunOnMainThread(() => SendTtsAudio(pending, response));
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // The runtime toggle is a kill switch: cancelled speech must stay silent.
        }
        catch (Exception e)
        {
            _sawmill.Debug($"LuaM character TTS request failed for {pending.Actor}: {e.Message}");
        }
        finally
        {
            await RunOnMainThread(() =>
            {
                _pending.Remove(pending.Key);
                _inFlight = false;
                _inFlightCancellation?.Dispose();
                _inFlightCancellation = null;
            });
        }
    }

    private async Task<LuaMTtsResponse?> RequestGatewayTtsAsync(
        PendingCharacterTtsRequest pending,
        CancellationToken cancel)
    {
        var gatewayUrl = _cfg.GetCVar(CCVars.LuaMAiDirectorGatewayUrl).Trim();
        if (string.IsNullOrWhiteSpace(gatewayUrl))
            return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(TimeSpan.FromSeconds(GetTimeout()));
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildGatewayTtsUri(gatewayUrl));
        var token = _cfg.GetCVar(CCVars.LuaMAiDirectorGatewayToken).Trim();
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        request.Content = JsonContent.Create(
            new LuaMTtsRequest
            {
                Version = 1,
                Text = pending.Text,
                Actor = pending.Actor,
                Voice = pending.Voice,
                Format = "wav",
            },
            options: JsonOptions);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"gateway returned {(int) response.StatusCode} {response.ReasonPhrase}");

        return await ReadGatewayTtsResponseAsync(response.Content, GetTtsMaxBytes(), cts.Token);
    }

    private void SendTtsAudio(PendingCharacterTtsRequest pending, LuaMTtsResponse? response)
    {
        if (pending.Cancelled ||
            !_cfg.GetCVar(CCVars.LuaMCharacterTtsEnabled) ||
            response == null ||
            string.IsNullOrWhiteSpace(response.AudioBase64))
        {
            return;
        }

        var maxBytes = GetTtsMaxBytes();
        if (!TryDecodeGatewayAudio(response.Format, response.AudioBase64, maxBytes, out var format, out var audio))
        {
            _sawmill.Warning("LuaM character TTS gateway returned invalid or oversized audio.");
            return;
        }

        var requestId = NormalizeGatewayRequestId(response.RequestId);
        var volume = GetTtsVolume();

        foreach (var userId in pending.RecipientIds)
        {
            if (!_players.TryGetSessionById(userId, out var session) ||
                session.Status != SessionStatus.InGame)
            {
                continue;
            }

            RaiseNetworkEvent(
                new LuaMAiTtsAudioEvent(requestId, format, volume, audio),
                session.Channel);
        }
    }

    private Task RunOnMainThread(Action action)
    {
        var source = new TaskCompletionSource();
        _task.RunOnMainThread(() =>
        {
            try
            {
                action();
                source.TrySetResult();
            }
            catch (Exception e)
            {
                source.TrySetException(e);
            }
        });

        return source.Task;
    }

    internal static Uri BuildGatewayTtsUri(string gatewayUrl)
    {
        var builder = new UriBuilder(gatewayUrl)
        {
            Path = "/tts",
            Query = string.Empty,
        };

        return builder.Uri;
    }

    private int GetTimeout()
    {
        return Math.Clamp(_cfg.GetCVar(CCVars.LuaMAiDirectorRequestTimeout), 2, 60);
    }

    private int GetTtsMaxQueue()
    {
        return Math.Clamp(_cfg.GetCVar(CCVars.LuaMAiDirectorTtsMaxQueue), 1, 32);
    }

    private int GetTtsMaxBytes()
    {
        return Math.Clamp(_cfg.GetCVar(CCVars.LuaMAiDirectorTtsMaxBytes), 16_384, 2_097_152);
    }

    private float GetTtsVolume()
    {
        return Math.Clamp(_cfg.GetCVar(CCVars.LuaMAiDirectorTtsVolume), -30f, 10f);
    }

    private int GetCharacterMaxChars()
    {
        return Math.Clamp(_cfg.GetCVar(CCVars.LuaMCharacterTtsMaxChars), 32, 400);
    }

    private float GetCharacterCooldown()
    {
        return Math.Clamp(_cfg.GetCVar(CCVars.LuaMCharacterTtsCooldown), 0.25f, 10f);
    }

    private string SelectVoice(string actor)
    {
        var voices = GetCharacterVoices();
        if (voices.Count == 0)
            return FallbackVoices[0];

        var hash = 2166136261u;
        foreach (var rune in actor)
        {
            hash ^= char.ToUpperInvariant(rune);
            hash *= 16777619u;
        }

        return voices[(int) (hash % voices.Count)];
    }

    private List<string> GetCharacterVoices()
    {
        var raw = _cfg.GetCVar(CCVars.LuaMCharacterTtsVoices);
        var voices = new List<string>();

        foreach (var item in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IsSafePiperVoiceName(item) && !voices.Contains(item))
                voices.Add(item);
        }

        if (voices.Count > 0)
            return voices;

        voices.AddRange(FallbackVoices);
        return voices;
    }

    internal static bool IsSafePiperVoiceName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        foreach (var rune in value)
        {
            if (!char.IsAsciiLetterOrDigit(rune) &&
                rune != '_' &&
                rune != '-')
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeActorName(string value)
    {
        return NormalizeTtsText(value, 80);
    }

    internal static string NormalizeTtsText(string value, int maxChars)
    {
        if (maxChars <= 0)
            return string.Empty;

        var text = value.ReplaceLineEndings(" ").Trim();

        try
        {
            text = FormattedMessage.RemoveMarkupOrThrow(text);
        }
        catch (Exception)
        {
            // TTS should fail closed on malformed markup without blocking the chat line itself.
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var rune in text)
        {
            if (char.IsControl(rune))
                continue;

            if (char.IsWhiteSpace(rune))
            {
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(rune);
            lastWasSpace = false;
        }

        text = builder.ToString().Trim();
        if (text.Length <= maxChars)
            return text;

        var truncateAt = maxChars;
        if (char.IsHighSurrogate(text[truncateAt - 1]) &&
            truncateAt < text.Length &&
            char.IsLowSurrogate(text[truncateAt]))
        {
            truncateAt--;
        }

        return text[..truncateAt].TrimEnd();
    }

    internal static bool TryDecodeGatewayAudio(
        string? rawFormat,
        string? audioBase64,
        int maxBytes,
        out string format,
        out byte[] audio)
    {
        format = rawFormat?.Trim().ToLowerInvariant() ?? string.Empty;
        audio = [];

        if (format != "wav" && format != "ogg" ||
            string.IsNullOrWhiteSpace(audioBase64) ||
            maxBytes <= 0)
        {
            return false;
        }

        var encodedLength = 0L;
        foreach (var character in audioBase64)
        {
            if (!char.IsWhiteSpace(character))
                encodedLength++;
        }

        var maximumEncodedLength = ((long) maxBytes + 2) / 3 * 4;
        if (encodedLength == 0 || encodedLength > maximumEncodedLength)
            return false;

        try
        {
            audio = Convert.FromBase64String(audioBase64);
        }
        catch (FormatException)
        {
            audio = [];
            return false;
        }

        if (audio.Length == 0 || audio.Length > maxBytes || !HasExpectedAudioHeader(format, audio))
        {
            audio = [];
            return false;
        }

        return true;
    }

    internal static string NormalizeGatewayRequestId(string? value)
    {
        var candidate = value?.Trim() ?? string.Empty;
        if (candidate.Length is > 0 and <= 64)
        {
            foreach (var character in candidate)
            {
                if (!char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-')
                    return Guid.NewGuid().ToString("N");
            }

            return candidate;
        }

        return Guid.NewGuid().ToString("N");
    }

    internal static async Task<LuaMTtsResponse?> ReadGatewayTtsResponseAsync(
        HttpContent content,
        int maxAudioBytes,
        CancellationToken cancel)
    {
        var maximumEncodedLength = ((long) maxAudioBytes + 2) / 3 * 4;
        var maximumResponseBytes = checked((int) maximumEncodedLength + 16_384);
        if (content.Headers.ContentLength is { } contentLength && contentLength > maximumResponseBytes)
            throw new InvalidOperationException("gateway TTS response is too large");

        var buffer = ArrayPool<byte>.Shared.Rent(maximumResponseBytes + 1);
        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancel);
            var totalRead = 0;
            while (totalRead <= maximumResponseBytes)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(totalRead, maximumResponseBytes + 1 - totalRead), cancel);
                if (read == 0)
                    break;

                totalRead += read;
            }

            if (totalRead > maximumResponseBytes)
                throw new InvalidOperationException("gateway TTS response is too large");

            return JsonSerializer.Deserialize<LuaMTtsResponse>(buffer.AsSpan(0, totalRead), JsonOptions);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void OnEntityTerminating(ref EntityTerminatingEvent args)
    {
        _nextBySpeaker.Remove(args.Entity.Owner);
    }

    private void CancelPendingRequests()
    {
        foreach (var pending in _pending.Values)
            pending.Cancelled = true;

        _pending.Clear();
    }

    private static bool HasExpectedAudioHeader(string format, ReadOnlySpan<byte> audio)
    {
        if (format == "ogg")
            return HasVorbisIdentificationPacket(audio);

        return HasValidWavStructure(audio);
    }

    private static bool HasValidWavStructure(ReadOnlySpan<byte> audio)
    {
        if (audio.Length < 44 ||
            !audio[..4].SequenceEqual("RIFF"u8) ||
            !audio.Slice(8, 4).SequenceEqual("WAVE"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(audio.Slice(4, 4)) != audio.Length - 8)
        {
            return false;
        }

        var hasFormat = false;
        var hasData = false;
        var offset = 12;
        while (offset <= audio.Length - 8)
        {
            var chunkId = audio.Slice(offset, 4);
            var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(audio.Slice(offset + 4, 4));
            var payloadStart = offset + 8;
            if (chunkLength > audio.Length - payloadStart)
                return false;

            if (chunkId.SequenceEqual("fmt "u8))
            {
                if (chunkLength < 16)
                    return false;

                var format = BinaryPrimitives.ReadUInt16LittleEndian(audio.Slice(payloadStart, 2));
                var channels = BinaryPrimitives.ReadUInt16LittleEndian(audio.Slice(payloadStart + 2, 2));
                var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(audio.Slice(payloadStart + 4, 4));
                var bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(audio.Slice(payloadStart + 14, 2));
                if (format is not 1 and not 3 and not 0xFFFE ||
                    channels is 0 or > 8 ||
                    sampleRate is < 8_000 or > 192_000 ||
                    bitsPerSample is not 8 and not 16 and not 24 and not 32)
                {
                    return false;
                }

                hasFormat = true;
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                if (chunkLength == 0)
                    return false;

                hasData = true;
            }

            var paddedLength = (long) chunkLength + (chunkLength & 1);
            var nextOffset = (long) payloadStart + paddedLength;
            if (nextOffset > audio.Length)
                return false;

            offset = (int) nextOffset;
        }

        return hasFormat && hasData && offset == audio.Length;
    }

    private static bool HasVorbisIdentificationPacket(ReadOnlySpan<byte> audio)
    {
        if (audio.Length < 58 ||
            !audio[..4].SequenceEqual("OggS"u8) ||
            audio[4] != 0 ||
            (audio[5] & 0x02) == 0)
        {
            return false;
        }

        var segmentCount = audio[26];
        var packetOffset = 27 + segmentCount;
        if (segmentCount == 0 || packetOffset > audio.Length)
            return false;

        var packetLength = 0;
        var packetComplete = false;
        for (var index = 0; index < segmentCount; index++)
        {
            var segmentLength = audio[27 + index];
            packetLength += segmentLength;
            if (segmentLength < byte.MaxValue)
            {
                packetComplete = true;
                break;
            }
        }

        if (!packetComplete || packetLength < 30 || packetLength > audio.Length - packetOffset)
            return false;

        var packet = audio.Slice(packetOffset, packetLength);
        if (packet[0] != 1 ||
            !packet.Slice(1, 6).SequenceEqual("vorbis"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(7, 4)) != 0 ||
            packet[11] is 0 or > 8)
        {
            return false;
        }

        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(12, 4));
        var smallBlockSize = packet[28] & 0x0F;
        var largeBlockSize = packet[28] >> 4;
        return sampleRate is >= 8_000 and <= 192_000 &&
               smallBlockSize is >= 6 and <= 13 &&
               largeBlockSize >= smallBlockSize &&
               largeBlockSize <= 13 &&
               (packet[29] & 0x01) != 0;
    }

    private sealed class PendingCharacterTtsRequest(
        string key,
        string text,
        string actor,
        string voice)
    {
        public string Key { get; } = key;
        public string Text { get; } = text;
        public string Actor { get; } = actor;
        public string Voice { get; } = voice;
        public bool Cancelled { get; set; }
        public HashSet<NetUserId> RecipientIds { get; } = new();
    }

    private sealed class LuaMTtsRequest
    {
        public int Version { get; set; }
        public string Text { get; set; } = string.Empty;
        public string Actor { get; set; } = string.Empty;
        public string Voice { get; set; } = string.Empty;
        public string Format { get; set; } = "wav";
    }

    internal sealed class LuaMTtsResponse
    {
        public string Format { get; set; } = "wav";
        public string AudioBase64 { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
    }
}
