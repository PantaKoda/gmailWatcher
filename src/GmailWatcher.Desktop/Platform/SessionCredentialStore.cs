using System.Collections.Concurrent;
using GmailWatcher.Core.Accounts;

namespace GmailWatcher.Desktop.Platform;

/// <summary>
/// Keeps refresh tokens in memory only: used where the OS has no secure store, and in demo mode. Clearly not persistent:
/// the app says so, and an account then has to sign in again after a restart. Never writes a token to disk.
/// </summary>
public sealed class SessionCredentialStore : ICredentialStore
{
    private readonly ConcurrentDictionary<string, StoredCredential> _tokens = new(StringComparer.OrdinalIgnoreCase);

    public bool IsPersistent => false;

    public string Description => "memory only (lost when the app closes)";

    public Task<StoredCredential?> ReadAsync(string accountEmail, CancellationToken cancellationToken = default) =>
        Task.FromResult(_tokens.GetValueOrDefault(accountEmail));

    public Task WriteAsync(string accountEmail, StoredCredential credential, CancellationToken cancellationToken = default)
    {
        _tokens[accountEmail] = credential;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string accountEmail, CancellationToken cancellationToken = default)
    {
        _tokens.TryRemove(accountEmail, out _);
        return Task.CompletedTask;
    }
}
