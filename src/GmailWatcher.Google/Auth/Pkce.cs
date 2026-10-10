using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace GmailWatcher.Google.Auth;

/// <summary>RFC 7636 proof key for code exchange (S256), so a stolen authorization code is useless without the verifier.</summary>
public static class Pkce
{
    /// <summary>32 random bytes as unpadded base64url: 43 characters, inside the 43-128 range the RFC allows.</summary>
    public static string CreateVerifier() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string CreateChallenge(string verifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(verifier);
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }
}
