using GmailWatcher.Core.Accounts;

namespace GmailWatcher.Google.Tests.Support;

internal sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, StoredCredential> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public bool IsPersistent => false;

    public string Description => "test memory";

    public int Reads { get; private set; }

    public int Writes { get; private set; }

    public Task<StoredCredential?> ReadAsync(string accountEmail, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            Reads++;
            return Task.FromResult(_items.GetValueOrDefault(accountEmail));
        }
    }

    public Task WriteAsync(string accountEmail, StoredCredential credential, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            Writes++;
            _items[accountEmail] = credential;
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string accountEmail, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _items.Remove(accountEmail);
        }

        return Task.CompletedTask;
    }

    public string? Peek(string accountEmail)
    {
        lock (_lock)
        {
            return _items.GetValueOrDefault(accountEmail)?.RefreshToken;
        }
    }

    public InMemoryCredentialStore With(string accountEmail, string refreshToken)
    {
        lock (_lock)
        {
            _items[accountEmail] = new StoredCredential(refreshToken);
        }

        return this;
    }
}
