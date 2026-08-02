using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.GameStates;

namespace Content.Shared._LuaM.Stargate;

[RegisterComponent, NetworkedComponent]
public sealed partial class LuaMStargateComponent : Component
{
    [DataField]
    public string AddressPreset = string.Empty;

    [ViewVariables]
    public byte[] Address = Array.Empty<byte>();

    [DataField]
    public float ChevronDelay = 0.6f;

    [DataField]
    public float OpeningDelay = 2f;

    [DataField]
    public float ClosingDelay = 1.35f;

    [DataField]
    public float AutoCloseDelay = 10f;

    [DataField]
    public SoundSpecifier IdleSound = new SoundPathSpecifier("/Audio/_Lua/Effects/Stargate/wormhole_idle.ogg");

    [DataField]
    public SoundSpecifier TraversalSound = new SoundPathSpecifier("/Audio/_Lua/Effects/Stargate/wormhole_enter.ogg")
    {
        Params = AudioParams.Default.WithVolume(-8f),
    };
}

/// <summary>
/// Marks a gate whose iris may be opened and closed from its DHD.
/// The original Taipan gate starts sealed.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class LuaMStargateControllableComponent : Component
{
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public bool Enabled;
}

[RegisterComponent]
public sealed partial class LuaMStargateConsoleComponent : Component
{
    [DataField]
    public float AutoLinkRadius = 10f;

    [DataField]
    public SoundSpecifier PressSound = new SoundCollectionSpecifier(
        "StargateDhdPress",
        AudioParams.Default.WithVolume(SharedAudioSystem.GainToVolume(0.2f)));

    [DataField]
    public SoundSpecifier DialSound = new SoundPathSpecifier(
        "/Audio/_Lua/Effects/Stargate/pegasus_dhd_enter.ogg",
        AudioParams.Default.WithVolume(SharedAudioSystem.GainToVolume(0.25f)));

    [DataField]
    public SoundSpecifier EngageSound = new SoundCollectionSpecifier(
        "StargateChevronEngage",
        AudioParams.Default.WithVolume(SharedAudioSystem.GainToVolume(0.25f)));

    [DataField]
    public SoundSpecifier FailSound = new SoundCollectionSpecifier(
        "StargateDialFail",
        AudioParams.Default.WithVolume(SharedAudioSystem.GainToVolume(0.25f)));

    [ViewVariables]
    public EntityUid? LinkedGate;

    [ViewVariables]
    public readonly List<byte> CurrentInput = new();

    [ViewVariables]
    public string Status = "stargate-console-status-idle";
}
