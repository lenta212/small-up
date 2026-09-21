namespace Content.Shared.Radio.Components;

[Flags]
public enum TelecomServerMode : byte
{
    Password = 1,
    Logs_Save = 2,
}

/// <summary>
/// Entities with <see cref="TelecomServerComponent"/> are needed to transmit messages using headsets.
/// They also need to be powered by <see cref="ApcPowerReceiverComponent"/>
/// have <see cref="EncryptionKeyHolderComponent"/> and filled with encryption keys
/// of channels in order for them to work on the same map as server.
/// </summary>
[RegisterComponent]
public sealed partial class TelecomServerComponent : Component
{
    /// <summary>Optional server features enabled by the prototype.</summary>
    [DataField("modes")]
    public HashSet<TelecomServerMode> Modes = new();

    /// <summary>
    /// PBKDF2 salt and hash. These are deliberately not component state fields, so they
    /// are never sent to clients. Server code owns password generation and verification.
    /// </summary>
    [DataField("passwordSalt")]
    public byte[]? PasswordSalt;

    [DataField("passwordHash")]
    public byte[]? PasswordHash;

    [DataField("passwordIterations")]
    public int PasswordIterations = 120_000;

    [DataField("heatGeneration")]
    public float HeatGeneration = 0.0f;

    [DataField("interferenceThreshold")]
    public float InterferenceThreshold = 0.0f;

    [DataField("criticalTemperature")]
    public float CriticalTemperature = 430.0f;

    [DataField("serviceKeyRequired")]
    public bool ServiceKeyRequired;

    [DataField("serviceKeyChannel")]
    public string? ServiceKeyChannel;

    public bool HasMode(TelecomServerMode mode) => Modes.Contains(mode);
}
