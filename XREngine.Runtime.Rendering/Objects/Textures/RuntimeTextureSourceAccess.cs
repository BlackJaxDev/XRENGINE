using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Execution;

namespace XREngine.Rendering;

/// <summary>Separates host-file texture import from resident and cooked texture consumption.</summary>
internal static class RuntimeTextureSourceAccess
{
    internal static bool CanAccessHostFiles
    {
        get
        {
            IRuntimeAssetReadSource? source = RuntimeAssetReadServices.Source;
            return !OperatingSystem.IsBrowser()
                && !RuntimeWorkScheduler.IsCallerThread
                && source is not { SupportsHostFileAccess: false }
                && source is not { SupportsSynchronousReads: false }
                && DirectStorageIO.Source is not IRuntimeAssetCatalog
                && DirectStorageIO.Source is not { SupportsSynchronousReads: false }
                && (!RuntimeRenderingHostServices.HasConcreteHost ||
                    RuntimeRenderingHostServices.Assets.SupportsSynchronousTextureSourceWork);
        }
    }

    internal static void RequireHostFiles()
    {
        if (!CanAccessHostFiles)
            throw new InvalidOperationException(
                "TextureSource.HostFileImportUnavailable: preload a cooked texture through its asynchronous asset owner, or supply decoded image data directly.");
    }
}
