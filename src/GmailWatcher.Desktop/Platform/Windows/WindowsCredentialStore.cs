using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using GmailWatcher.Core.Accounts;

namespace GmailWatcher.Desktop.Platform.Windows;

/// <summary>
/// Stores each account's refresh token in Windows Credential Manager as a generic credential for the current user on this
/// machine (not roaming). One entry per account: "GmailWatcher:google/&lt;address&gt;". Nothing else is ever stored there.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore(string targetPrefix = WindowsCredentialStore.DefaultPrefix) : ICredentialStore
{
    public const string DefaultPrefix = "GmailWatcher:google/";

    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaxBlobBytes = 5 * 512;

    public bool IsPersistent => true;

    public string Description => "Windows Credential Manager";

    public Task<StoredCredential?> ReadAsync(string accountEmail, CancellationToken cancellationToken = default)
    {
        if (!CredRead(Target(accountEmail), CredTypeGeneric, 0, out var handle))
        {
            var error = Marshal.GetLastWin32Error();
            return error == ErrorNotFound ? Task.FromResult<StoredCredential?>(null) : throw new Win32Exception(error, "Reading from Windows Credential Manager failed.");
        }

        try
        {
            var native = Marshal.PtrToStructure<NativeCredential>(handle);
            var blob = new byte[native.CredentialBlobSize];
            Marshal.Copy(native.CredentialBlob, blob, 0, blob.Length);
            try
            {
                var stored = JsonSerializer.Deserialize(blob, CredentialJsonContext.Default.CredentialBlob);
                return Task.FromResult(stored is { RefreshToken.Length: > 0 } ? new StoredCredential(stored.RefreshToken) : null);
            }
            catch (JsonException)
            {
                return Task.FromResult<StoredCredential?>(null); // unreadable entry: treat as signed out
            }
            finally
            {
                CryptographicOperations.ZeroMemory(blob);
            }
        }
        finally
        {
            CredFree(handle);
        }
    }

    public Task WriteAsync(string accountEmail, StoredCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var blob = JsonSerializer.SerializeToUtf8Bytes(new CredentialBlob(credential.RefreshToken), CredentialJsonContext.Default.CredentialBlob);
        var buffer = IntPtr.Zero;
        try
        {
            if (blob.Length > MaxBlobBytes)
            {
                throw new InvalidOperationException("The credential is too large for Windows Credential Manager.");
            }

            buffer = Marshal.AllocHGlobal(blob.Length);
            Marshal.Copy(blob, 0, buffer, blob.Length);
            var native = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = Target(accountEmail),
                Comment = "Google sign-in for Gmail Watcher (read-only access to one mailbox)",
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = buffer,
                Persist = CredPersistLocalMachine,
                UserName = accountEmail,
            };

            if (!CredWrite(ref native, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Writing to Windows Credential Manager failed.");
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                for (var i = 0; i < blob.Length; i++)
                {
                    Marshal.WriteByte(buffer, i, 0);
                }

                Marshal.FreeHGlobal(buffer);
            }

            CryptographicOperations.ZeroMemory(blob);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string accountEmail, CancellationToken cancellationToken = default)
    {
        if (!CredDelete(Target(accountEmail), CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(error, "Deleting from Windows Credential Manager failed.");
            }
        }

        return Task.CompletedTask;
    }

    private string Target(string email) => targetPrefix + email.Trim().ToLowerInvariant();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr buffer);
}

internal sealed record CredentialBlob(string RefreshToken)
{
    public override string ToString() => "CredentialBlob { redacted }";
}

[JsonSerializable(typeof(CredentialBlob))]
internal sealed partial class CredentialJsonContext : JsonSerializerContext;
