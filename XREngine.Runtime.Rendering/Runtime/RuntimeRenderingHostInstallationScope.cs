using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Data.Transforms.Rotations;
using XREngine.Input;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.Occlusion;
using XREngine.Rendering.Shadows;
using XREngine.Scene;
namespace XREngine.Rendering;

internal sealed class RuntimeRenderingHostInstallationScope(
    IRuntimeRenderingHostServices installed,
    IRuntimeRenderingHostServices previous) : IDisposable
{
    private IRuntimeRenderingHostServices? _installed = installed;

    public void Dispose()
    {
        IRuntimeRenderingHostServices? expected = Interlocked.Exchange(ref _installed, null);
        if (expected is not null && ReferenceEquals(RuntimeRenderingHostServices.Current, expected))
            RuntimeRenderingHostServices.Current = previous;
    }
}
