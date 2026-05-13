using System.Security.Cryptography;

namespace SecurityAnalyzer.Web;

// PBKDF2-HMAC-SHA256 password hashing.
//
// Stored form: "{iterations}.{salt-b64}.{hash-b64}".  The iteration
// count is part of the value so we can raise the work factor later
// without breaking rows seeded at an older count.  Verify() runs in
// fixed time relative to the candidate hash length.
internal static class PasswordHash
{
    private const int SaltBytes  = 16;
    private const int HashBytes  = 32;
    private const int Iterations = 100_000;
    private const char Sep       = '.';

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return string.Concat(
            Iterations.ToString(), Sep,
            Convert.ToBase64String(salt), Sep,
            Convert.ToBase64String(hash));
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split(Sep);
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out var iterations) || iterations < 1) return false;

        byte[] salt, expected;
        try
        {
            salt     = Convert.FromBase64String(parts[1]);
            expected = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
