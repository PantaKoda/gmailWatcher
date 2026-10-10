using System.Runtime.CompilerServices;
using System.Text;

namespace GmailWatcher.Archive.Ingest;

/// <summary>
/// .NET itself only knows UTF-8, UTF-16/32, ASCII and Latin-1. Mail labelled iso-8859-7 or windows-1253 (Greek),
/// windows-1251 (Cyrillic), shift_jis, gb2312, windows-1250 and the like needs the extra code pages registered first;
/// without them MimeKit falls back to Latin letters and the text comes out as gibberish ("Êáëçìýñá" for "Καλημέρα").
/// </summary>
internal static class EncodingSupport
{
    // A module initializer, not a call from the classes that parse mail: it holds for every MimeKit use in this assembly,
    // including ones added later, and it runs before any of them.
#pragma warning disable CA2255 // The 'ModuleInitializer' attribute should not be used in libraries: this library is the app's own engine.
    [ModuleInitializer]
    internal static void RegisterCodePages() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#pragma warning restore CA2255
}
