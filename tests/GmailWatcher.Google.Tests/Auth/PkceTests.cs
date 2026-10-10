using System.Text.RegularExpressions;
using GmailWatcher.Google.Auth;

namespace GmailWatcher.Google.Tests.Auth;

public sealed partial class PkceTests
{
    [Fact]
    public void ChallengeMatchesTheRfc7636AppendixBVector()
    {
        var challenge = Pkce.CreateChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk");

        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", challenge);
    }

    [Fact]
    public void VerifierIs43UrlSafeCharactersWithoutPadding()
    {
        var verifier = Pkce.CreateVerifier();

        Assert.Equal(43, verifier.Length);
        Assert.Matches(UrlSafe(), verifier);
    }

    [Fact]
    public void VerifiersDoNotRepeat()
    {
        var verifiers = Enumerable.Range(0, 200).Select(_ => Pkce.CreateVerifier()).ToHashSet();

        Assert.Equal(200, verifiers.Count);
    }

    [Fact]
    public void ChallengeIs43UrlSafeCharactersAndStaysTheSameForTheSameVerifier()
    {
        var verifier = Pkce.CreateVerifier();

        var challenge = Pkce.CreateChallenge(verifier);

        Assert.Equal(43, challenge.Length);
        Assert.Matches(UrlSafe(), challenge);
        Assert.Equal(challenge, Pkce.CreateChallenge(verifier));
        Assert.NotEqual(verifier, challenge);
    }

    [Fact]
    public void ChallengeRejectsAnEmptyVerifier()
    {
        Assert.Throws<ArgumentException>(() => Pkce.CreateChallenge(string.Empty));
    }

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex UrlSafe();
}
