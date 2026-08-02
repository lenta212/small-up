namespace Content.Server._LuaM.Stargate;

[RegisterComponent]
public sealed partial class LuaMStargateDialingComponent : Component
{
    [ViewVariables]
    public byte[] Symbols = Array.Empty<byte>();

    [ViewVariables]
    public EntityUid Destination;

    [ViewVariables]
    public EntityUid Console;

    [ViewVariables]
    public EntityUid Actor;

    [ViewVariables]
    public int ChevronIndex;

    [ViewVariables]
    public float Accumulator;

    [ViewVariables]
    public bool InOpening;
}

[RegisterComponent]
public sealed partial class LuaMStargateDialReservationComponent : Component
{
    [ViewVariables]
    public EntityUid Source;
}

[RegisterComponent]
public sealed partial class LuaMStargateOpenStateComponent : Component
{
    [ViewVariables]
    public EntityUid? IdleAudio;

    [ViewVariables]
    public bool HasTraversal;

    [ViewVariables]
    public TimeSpan LastTraversal;
}

[RegisterComponent]
public sealed partial class LuaMStargateClosingComponent : Component
{
    [ViewVariables]
    public float Accumulator;
}

[RegisterComponent]
public sealed partial class LuaMStargateOpeningComponent : Component
{
    [ViewVariables]
    public float Accumulator;
}

[RegisterComponent]
public sealed partial class LuaMStargateIrisAnimatingComponent : Component
{
    [ViewVariables]
    public float Accumulator;

    [ViewVariables]
    public bool Opening;
}
