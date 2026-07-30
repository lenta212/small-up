using Content.Shared.Parallax.Biomes;
using Content.Shared.Parallax.Biomes.Markers;
using Content.Shared.Procedural;
using Content.Shared.Salvage.Expeditions.Modifiers;
using Content.Shared.Weather;
using Robust.Shared.Prototypes;

namespace Content.Shared.Gateway;

/// <summary>
/// A deterministic bundle of world-generation settings and player-facing reconnaissance data
/// for a generated gateway destination.
/// </summary>
[Prototype("gatewayWorldProfile")]
public sealed partial class GatewayWorldProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name = string.Empty;

    [DataField(required: true)]
    public LocId Description = string.Empty;

    [DataField(required: true)]
    public LocId BiomeName = string.Empty;

    [DataField(required: true)]
    public LocId WeatherName = string.Empty;

    [DataField(required: true)]
    public LocId AtmosphereName = string.Empty;

    [DataField(required: true)]
    public LocId Resources = string.Empty;

    [DataField(required: true)]
    public LocId Hostiles = string.Empty;

    [DataField(required: true)]
    public ProtoId<BiomeTemplatePrototype> Biome;

    [DataField(required: true)]
    public ProtoId<DungeonConfigPrototype> Dungeon;

    [DataField]
    public ProtoId<WeatherPrototype>? Weather;

    [DataField(required: true)]
    public ProtoId<SalvageAirMod> Air;

    [DataField]
    public float Temperature = 293.15f;

    [DataField]
    public GatewayThreatLevel Threat = GatewayThreatLevel.Minimal;

    [DataField]
    public Color AccentColor = Color.FromHex("#D381C9");

    [DataField]
    public Color LightColor = Color.FromHex("#D8B059");

    [DataField]
    public int DungeonDistanceMin = 8;

    [DataField]
    public int DungeonDistanceMax = 16;

    [DataField]
    public int LootLayerCount = 3;

    [DataField]
    public List<ProtoId<BiomeMarkerLayerPrototype>> LootLayers = new();

    [DataField]
    public int MobLayerCount = 1;

    [DataField]
    public List<ProtoId<BiomeMarkerLayerPrototype>> MobLayers = new();
}
