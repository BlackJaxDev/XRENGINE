using System.Threading;
using YamlDotNet.Serialization;

namespace XREngine.Rendering;

public partial class XRCamera
{
    private static long s_nextRenderIdentity;
    private readonly ulong _renderIdentity = AllocateRenderIdentity();

    /// <summary>
    /// Gets the stable, process-local identity for this camera's rendered views.
    /// This runtime-only value is never serialized and is not reused by another camera.
    /// </summary>
    [YamlIgnore]
    public ulong RenderIdentity => _renderIdentity;

    private static ulong AllocateRenderIdentity()
    {
        long identity = Interlocked.Increment(ref s_nextRenderIdentity);
        if (identity <= 0)
            throw new InvalidOperationException("Camera render identities exhausted.");

        return unchecked((ulong)identity);
    }
}
