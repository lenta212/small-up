using Robust.Shared.Serialization;

namespace Content.Shared._LuaM.Sector;

[Serializable, NetSerializable]
public sealed class LuaMAiTtsAudioEvent : EntityEventArgs
{
    public string RequestId = string.Empty;
    public string Format = "wav";
    public float Volume;
    public byte[] AudioData = [];

    public LuaMAiTtsAudioEvent()
    {
    }

    public LuaMAiTtsAudioEvent(string requestId, string format, float volume, byte[] audioData)
    {
        RequestId = requestId;
        Format = format;
        Volume = volume;
        AudioData = audioData;
    }
}
