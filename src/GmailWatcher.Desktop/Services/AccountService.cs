using GmailWatcher.Core.Accounts;
using Microsoft.Extensions.Logging;

namespace GmailWatcher.Desktop.Services;

public sealed record ConnectResult(AccountRecord? Account, string? Error, bool WasExisting)
{
    public bool Succeeded => Account is not null;
}

/// <summary>
/// Adding, reconnecting, disconnecting and deleting accounts. Disconnecting keeps the archive; only
/// <see cref="DeleteArchiveAsync"/> removes mail from this PC, and nothing here ever touches mail in Gmail.
/// </summary>
public sealed class AccountService(ArchiveHost archive, IAccountConnector connector, ILogger<AccountService> logger)
{
    /// <summary>Raised (on any thread) after accounts were added, changed or removed.</summary>
    public event EventHandler? AccountsChanged;

    public IAccountConnector Connector => connector;

    public IReadOnlyList<AccountRecord> List() => archive.IsOpen ? archive.Runtime.Accounts.List() : [];

    public AccountRecord? Get(int id) => archive.IsOpen ? archive.Runtime.Accounts.Get(id) : null;

    /// <summary>
    /// Signs in and adds the account (or reconnects it when <paramref name="expectedEmail"/> is given or the address is already
    /// archived). Asking for one account and signing in as another is refused, with nothing stored for the wrong one.
    /// </summary>
    public async Task<ConnectResult> ConnectAsync(string? expectedEmail, IProgress<Uri>? authorizationUrl, CancellationToken cancellationToken)
    {
        if (!archive.IsOpen)
        {
            return new(null, "Choose the archive folder first.", false);
        }

        string email;
        try
        {
            email = await connector.SignInAsync(expectedEmail, authorizationUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (ConnectException ex)
        {
            return new(null, ex.Message, false);
        }

        var accounts = archive.Runtime.Accounts;
        if (expectedEmail is not null && !string.Equals(email, expectedEmail, StringComparison.OrdinalIgnoreCase))
        {
            // The stored token belongs to a different Google account than the archive being reconnected.
            if (accounts.Find(email) is null)
            {
                await connector.SignOutAsync(email, cancellationToken).ConfigureAwait(false);
            }

            return new(null, $"You signed in as {email}, but this archive belongs to {expectedEmail}. Nothing was changed. Try again and choose {expectedEmail}.", false);
        }

        var existing = accounts.Find(email);
        var account = accounts.Add(email);
        logger.LogInformation("Account {Action}", existing is null ? "added" : "reconnected");
        AccountsChanged?.Invoke(this, EventArgs.Empty);
        return new(account, null, existing is not null);
    }

    /// <summary>Stops using Google for this account: access is revoked and the token removed, the archive stays.</summary>
    public async Task DisconnectAsync(AccountRecord account, CancellationToken cancellationToken)
    {
        await connector.SignOutAsync(account.Email, cancellationToken).ConfigureAwait(false);
        archive.Runtime.Accounts.SetConnection(account.Id, AccountConnection.ArchiveOnly);
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes this account's archive from this PC (and its sign-in). Gmail is untouched. Irreversible.</summary>
    public async Task DeleteArchiveAsync(AccountRecord account, CancellationToken cancellationToken)
    {
        await connector.SignOutAsync(account.Email, cancellationToken).ConfigureAwait(false);
        await Task.Run(() => archive.Runtime.Maintenance.DeleteAccountData(account), cancellationToken).ConfigureAwait(false);
        logger.LogWarning("The local archive of an account was deleted by the user");
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void NotifyChanged() => AccountsChanged?.Invoke(this, EventArgs.Empty);
}
