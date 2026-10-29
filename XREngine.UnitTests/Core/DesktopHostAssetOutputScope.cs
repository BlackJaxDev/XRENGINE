using XREngine.Data;
using XREngine.Runtime.Platform.Desktop;

namespace XREngine.UnitTests.Core;

internal sealed class DesktopHostAssetOutputScope : IDisposable
{
    private readonly IHostAssetFileOutput? _previous = HostAssetFileOutputServices.Current;

    public DesktopHostAssetOutputScope()
        => HostAssetFileOutputServices.Current = new DesktopHostAssetFileOutput();

    public void Dispose() => HostAssetFileOutputServices.Current = _previous;
}
