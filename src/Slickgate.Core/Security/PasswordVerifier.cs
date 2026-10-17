namespace Slickgate.Core.Security;

/// <summary>
/// Verifies plaintext passwords against bcrypt hashes. Both the <c>$2a$</c> form
/// emitted by BCrypt.Net and the <c>$2y$</c> form emitted by Caddy/PHP are
/// accepted directly (they are behaviorally identical), so no normalization is
/// required. Invalid or unparseable hashes fail closed.
/// </summary>
public static class PasswordVerifier
{
    public static bool Verify(string password, string? hash)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return false;
        }

        try
        {
            return BCrypt.Net.BCrypt.Verify(password, hash);
        }
        catch
        {
            return false;
        }
    }
}
