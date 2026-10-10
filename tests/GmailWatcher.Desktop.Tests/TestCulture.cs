using System.Globalization;
using System.Runtime.CompilerServices;

namespace GmailWatcher.Desktop.Tests;

/// <summary>Tests assert on formatted numbers and dates, so they run in the invariant culture on every machine.</summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void Pin()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    }
}
