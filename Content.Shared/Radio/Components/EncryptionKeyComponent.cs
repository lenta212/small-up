using Robust.Shared.GameObjects;
using Content.Shared.Chat;
using Robust.Shared.Maths;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom.Prototype;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom.Prototype.Set;
using Robust.Shared.Network;

namespace Content.Shared.Radio.Components;

/// <summary>
///     This component is currently used for providing access to channels for "HeadsetComponent"s.
///     It should be used for intercoms and other radios in future.
/// </summary>

[RegisterComponent, AutoGenerateComponentState]
public sealed partial class EncryptionKeyComponent : Component
{
    [DataField("frame")]
    public string? Frame;

    [DataField("icon")]
    public string? Icon;

    [DataField("tag")]
    [AutoNetworkedField]
    public string? Tag;

    [DataField("color")]
    [AutoNetworkedField]
    public Color? Color;

    [DataField("gradientColor")]
    [AutoNetworkedField]
    public Color? GradientColor;

    [DataField("frequencyPasswordSalt")]
    public byte[]? FrequencyPasswordSalt;

    [DataField("frequencyPasswordHash")]
    public byte[]? FrequencyPasswordHash;

    [DataField("frequencyPasswordIterations")]
    public int FrequencyPasswordIterations = 120_000;

    [DataField("customFrequency")]
    [AutoNetworkedField]
    public int? CustomFrequency;

    [DataField("channelName")]
    [AutoNetworkedField]
    public string? ChannelName;

    [DataField("customKeyCode")]
    [AutoNetworkedField]
    public char? CustomKeyCode;

    [DataField("channels", customTypeSerializer: typeof(PrototypeIdHashSetSerializer<RadioChannelPrototype>))]
    [AutoNetworkedField]
    public HashSet<string> Channels = new();

    /// <summary>
    ///     This is the channel that will be used when using the default/department prefix (<see cref="SharedChatSystem.DefaultChannelKey"/>).
    /// </summary>
    [DataField("defaultChannel", customTypeSerializer: typeof(PrototypeIdSerializer<RadioChannelPrototype>))]
    [AutoNetworkedField]
    public string? DefaultChannel;

    [DataField("ownerCharacter")]
    public NetEntity? OwnerCharacter;

    [DataField("ownerUserId")]
    public NetUserId? OwnerUserId;

}
