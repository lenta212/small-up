using System.Security.Cryptography;
using Content.Shared.Radio.Components;

namespace Content.Server.Radio;

public readonly record struct TelecomPasswordHash(byte[] Salt, byte[] Hash, int Iterations);

public static class TelecomPasswordService
{
    public const int DefaultIterations = 120_000;

    public static TelecomPasswordHash HashPassword(string password, int iterations = DefaultIterations)
    {
        ArgumentNullException.ThrowIfNull(password);
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        return new TelecomPasswordHash(salt, hash, iterations);
    }

    public static void SetPassword(TelecomServerComponent component, string password)
    {
        var result = HashPassword(password, component.PasswordIterations);
        component.PasswordSalt = result.Salt;
        component.PasswordHash = result.Hash;
        component.Modes.Add(TelecomServerMode.Password);
    }

    public static bool VerifyPassword(TelecomServerComponent component, string password) =>
        VerifyPassword(password, component.PasswordSalt, component.PasswordHash, component.PasswordIterations);

    public static bool VerifyPassword(string password, byte[]? salt, byte[]? hash, int iterations)
    {
        if (salt is not { Length: > 0 } || hash is not { Length: > 0 })
            return false;

        var candidate = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, hash.Length);
        return CryptographicOperations.FixedTimeEquals(candidate, hash);
    }
}
