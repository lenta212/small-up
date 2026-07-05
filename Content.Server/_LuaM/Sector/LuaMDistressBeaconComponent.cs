namespace Content.Server._LuaM.Sector;

[RegisterComponent]
[Access(typeof(LuaMDistressBeaconSystem))]
public sealed partial class LuaMDistressBeaconComponent : Component
{
    [DataField]
    public bool Seeded;

    [DataField]
    public string Title = "Сигнал аварийного маяка";

    [DataField]
    public string Vessel = "Незарегистрированное судно";

    [DataField]
    public int Reward = 30000;

    [DataField]
    public string Description = "Локальный аварийный маяк активирован. Проверьте точку, отметьте риск и оформите отчет восстановления или спасения.";

    [DataField]
    public string Hazard = "Открытая аварийная точка. Перед закрытием контракта проверьте атмосферу, пиратов, обломки и маршрут буксировки.";

    [DataField]
    public string ReputationTarget = "Distress";

    [DataField]
    public int ReputationDelta = 1;
}
