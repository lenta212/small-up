using System.Security.Cryptography;
using Content.Shared.Radio.Components;

namespace Content.Server.Radio;

public static class TelecomPasswordService
{
    public static void SetPassword(TelecomServerComponent component, string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        component.PasswordSalt = RandomNumberGenerator.GetBytes(16);
        component.PasswordHash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            component.PasswordSalt,
            component.PasswordIterations,
            HashAlgorithmName.SHA256,
            32);
        component.Modes.Add(TelecomServerMode.Password);
    }

    public static bool VerifyPassword(TelecomServerComponent component, string password)
    {
        if (component.PasswordSalt is not { Length: > 0 } salt ||
            component.PasswordHash is not { Length: > 0 } hash)
            return false;

        var candidate = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            component.PasswordIterations,
            HashAlgorithmName.SHA256,
            hash.Length);
        return CryptographicOperations.FixedTimeEquals(candidate, hash);
    }
}
