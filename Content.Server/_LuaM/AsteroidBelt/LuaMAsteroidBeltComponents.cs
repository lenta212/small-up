namespace Content.Server._LuaM.AsteroidBelt;

[RegisterComponent]
public sealed partial class LuaMAsteroidBeltMapComponent : Component
{
    [ViewVariables]
    public EntityUid? EntryBeacon;
}

[RegisterComponent]
public sealed partial class LuaMAsteroidBeltCoordinatesDiskComponent : Component;
