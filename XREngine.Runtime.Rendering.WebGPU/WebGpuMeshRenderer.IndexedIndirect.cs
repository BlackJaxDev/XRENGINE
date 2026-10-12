using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuMeshRenderer
{
    /// <summary>
    /// WebGPU does not consume retained GPU-authored indexed arguments yet. The call
    /// fails with a reason, and the caller reports it. No CPU route replaces the draw.
    /// </summary>
    public bool RenderIndexedIndirect(
        Matrix4x4 modelMatrix,
        Matrix4x4 previousModelMatrix,
        XRMaterial? materialOverride,
        RenderingParameters? renderOptionsOverride,
        XRDataBuffer arguments,
        nuint byteOffset,
        EPrimitiveType topology,
        EMeshBillboardMode billboardMode,
        bool forceNoStereo,
        IRenderResourceLeaseOwner? authoringLease,
        out string failureReason)
    {
        failureReason = "WebGpuMeshRenderer.IndexedIndirectUnsupported: WebGPU does not draw retained GPU-authored indexed arguments.";
        return false;
    }
}
