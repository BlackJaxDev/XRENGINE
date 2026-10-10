using System.Runtime.InteropServices.JavaScript;

namespace XREngine.Browser;

public static partial class BrowserEngineExports
{
    /// <summary>Returns the current source ledger; GPU allocation estimates belong to the renderer's statistics.</summary>
    [JSExport]
    public static string GetAssetDeliveryStatisticsJson() => _source?.GetDeliverySnapshotJson() ?? BrowserEngineAssetImports.GetCurrentProgress();
}
