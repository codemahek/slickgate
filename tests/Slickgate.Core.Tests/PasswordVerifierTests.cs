using Slickgate.Core.Security;
using Xunit;

namespace Slickgate.Core.Tests;

public class PasswordVerifierTests
{
    // bcrypt hash of "secret" at cost 5 in both encodings. The salt+hash suffix is
    // identical; only the version marker differs. "sec" is wrong on purpose.
    private const string Password = "secret";
    private const string Hash2a = "$2a$05$6ogqkNnLz7PBozpBxfJayeuJgq1o3.UqzaG2Ep7yBo6DEOCxLzQ2W";
    private const string Hash2y = "$2y$05$6ogqkNnLz7PBozpBxfJayeuJgq1o3.UqzaG2Ep7yBo6DEOCxLzQ2W";

    [Fact]
    public void Verify_AcceptsCaddyStyle2yHash()
    {
        Assert.True(PasswordVerifier.Verify(Password, Hash2y));
    }

    [Fact]
    public void Verify_Accepts2aHash()
    {
        Assert.True(PasswordVerifier.Verify(Password, Hash2a));
    }

    [Fact]
    public void Verify_RejectsWrongPassword()
    {
        Assert.False(PasswordVerifier.Verify("wrong", Hash2y));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("$2$05$6ogqkNnLz7PBozpBxfJayeuJgq1o3.UqzaG2Ep7yBo6DEOCxLzQ2W")]
    public void Verify_ReturnsFalseForUnparseableHash(string hash)
    {
        Assert.False(PasswordVerifier.Verify(Password, hash));
    }

    [Fact]
    public void Verify_ReturnsFalseForMissingHash()
    {
        Assert.False(PasswordVerifier.Verify(Password, null));
        Assert.False(PasswordVerifier.Verify(Password, ""));
    }
}
