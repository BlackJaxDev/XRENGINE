using System.Runtime.InteropServices.JavaScript;

namespace XREngine.Browser;

/// <summary>Async fetch ownership and synchronous copy imports for cooked engine assets.</summary>
internal static partial class BrowserEngineAssetImports
{
    [JSImport("create", "xrengine.assets")]
    internal static partial int Create(string manifestUrl);
    [JSImport("open", "xrengine.assets")]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    internal static partial Task OpenAsync(int session);
    [JSImport("manifest", "xrengine.assets")]
    internal static partial string GetManifest(int session);
    [JSImport("beginRead", "xrengine.assets")]
    internal static partial int BeginRead(int session, string path);
    [JSImport("waitRead", "xrengine.assets")]
    [return: JSMarshalAs<JSType.Promise<JSType.Number>>]
    internal static partial Task<int> WaitReadAsync(int session, int ticket);
    [JSImport("copyRead", "xrengine.assets")]
    internal static partial void CopyRead(int session, int ticket, [JSMarshalAs<JSType.MemoryView>] Span<byte> destination);
    [JSImport("releaseRead", "xrengine.assets")]
    internal static partial void ReleaseRead(int session, int ticket);
    [JSImport("dispose", "xrengine.assets")]
    internal static partial void Dispose(int session);
}
