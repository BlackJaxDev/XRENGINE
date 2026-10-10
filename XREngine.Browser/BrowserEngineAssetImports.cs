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
    [JSImport("verifiedWorldPackage", "xrengine.assets")]
    internal static partial string GetVerifiedWorldPackage(int session);
    [JSImport("beginRead", "xrengine.assets")]
    internal static partial int BeginRead(int session, string path);
    [JSImport("waitRead", "xrengine.assets")]
    [return: JSMarshalAs<JSType.Promise<JSType.Number>>]
    internal static partial Task<int> WaitReadAsync(int session, int ticket);
    [JSImport("copyRead", "xrengine.assets")]
    internal static partial void CopyRead(int session, int ticket, [JSMarshalAs<JSType.MemoryView>] Span<byte> destination);
    [JSImport("releaseRead", "xrengine.assets")]
    internal static partial void ReleaseRead(int session, int ticket);
    [JSImport("progress", "xrengine.assets")]
    internal static partial string GetProgress(int session);
    [JSImport("currentProgress", "xrengine.assets")]
    internal static partial string GetCurrentProgress();
    [JSImport("adjustManagedStaging", "xrengine.assets")]
    internal static partial void AdjustManagedStaging(int session, int bytes);
    [JSImport("beginIntegration", "xrengine.assets")]
    internal static partial int BeginIntegration(int session, string path, int byteLength, string companionPath);
    [JSImport("waitIntegration", "xrengine.assets")]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    internal static partial Task WaitIntegrationAsync(int session, int ticket);
    [JSImport("finishIntegration", "xrengine.assets")]
    internal static partial void FinishIntegration(int session, int ticket);
    [JSImport("retain", "xrengine.assets")]
    internal static partial void Retain(int session, string path, int serializedBytes, int objects, double managedBytes, double nativeBytes);
    [JSImport("releaseAsset", "xrengine.assets")]
    internal static partial void ReleaseAsset(int session, string path, double nativeBytes);
    [JSImport("dispose", "xrengine.assets")]
    internal static partial void Dispose(int session);
}
