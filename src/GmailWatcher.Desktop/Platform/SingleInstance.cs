using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace GmailWatcher.Desktop.Platform;

/// <summary>
/// One Gmail Watcher per user and data folder. A second launch asks the running one to show its widget
/// (over a named pipe, current user only) and exits. Different data folders (tests, demos) may run side by side.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string ShowMessage = "show";
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private bool _owned;

    public SingleInstance(string dataDirectory)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDirectory).ToUpperInvariant())))[..16];
        // The mutex is per session ("Local\"); the pipe name includes the session too, so the same user
        // signed in twice (console and remote desktop) gets two independent instances.
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        _pipeName = $"GmailWatcher-{Environment.UserName}-{process.SessionId}-{id}";
        _mutex = new Mutex(initiallyOwned: true, $"Local\\GmailWatcher-{id}", out _owned);
    }

    /// <summary>True for the first instance; false when another one already runs.</summary>
    public bool IsFirst => _owned;

    /// <summary>Raised (on a background thread) when another launch asked this instance to show itself.</summary>
    public event EventHandler? ActivationRequested;

    /// <summary>Second instance: ask the first to show its widget. Returns false if it couldn't be reached.</summary>
    public bool SignalFirst(TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)timeout.TotalMilliseconds);
            var bytes = Encoding.UTF8.GetBytes(ShowMessage);
            client.Write(bytes, 0, bytes.Length);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>First instance: listen for activation requests until disposed.</summary>
    public void Listen() => _ = Task.Run(async () =>
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                var buffer = new byte[16];
                var read = await server.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (Encoding.UTF8.GetString(buffer, 0, read) == ShowMessage)
                {
                    ActivationRequested?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A client that disconnected early, or the name is briefly busy: wait a moment instead of spinning.
                try
                {
                    await Task.Delay(500, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    });

    public void Dispose()
    {
        _stop.Cancel();

        // Closing the handle is enough: ownership is decided by whether the named mutex exists, never by
        // waiting on it, and ReleaseMutex would throw if Dispose runs on another thread than the constructor.
        _owned = false;
        _mutex.Dispose();
        _stop.Dispose();
    }
}
