using System.IO;
using Content.Shared._LuaM.Sector;
using Robust.Client.Audio;
using Robust.Shared.Audio;
using Robust.Shared.Timing;

namespace Content.Client._LuaM.Sector;

public sealed class LuaMAiTtsAudioSystem : EntitySystem
{
    [Dependency] private readonly IAudioManager _audio = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private readonly List<PlayingTtsAudio> _playing = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<LuaMAiTtsAudioEvent>(OnTtsAudio);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        for (var i = _playing.Count - 1; i >= 0; i--)
        {
            var playing = _playing[i];
            if (now < playing.CleanupAt)
                continue;

            if (!Deleted(playing.Entity))
                QueueDel(playing.Entity);

            playing.Stream.Dispose();
            _playing.RemoveAt(i);
        }
    }

    private void OnTtsAudio(LuaMAiTtsAudioEvent message)
    {
        if (message.AudioData.Length == 0)
            return;

        var format = message.Format.Trim().ToLowerInvariant();
        if (format != "wav" && format != "ogg")
            return;

        try
        {
            using var bytes = new MemoryStream(message.AudioData, writable: false);
            var stream = format == "ogg"
                ? _audio.LoadAudioOggVorbis(bytes, $"luam-tts-{message.RequestId}")
                : _audio.LoadAudioWav(bytes, $"luam-tts-{message.RequestId}");

            var result = EntityManager.System<AudioSystem>()
                .PlayGlobal(stream, null, AudioParams.Default.WithVolume(message.Volume));

            if (result == null)
            {
                stream.Dispose();
                return;
            }

            _playing.Add(new PlayingTtsAudio(
                result.Value.Entity,
                stream,
                _timing.CurTime + stream.Length + TimeSpan.FromSeconds(1)));
        }
        catch (Exception e)
        {
            Log.Warning($"LuaM TTS audio playback failed: {e.Message}");
        }
    }

    private readonly record struct PlayingTtsAudio(
        EntityUid Entity,
        AudioStream Stream,
        TimeSpan CleanupAt);
}
