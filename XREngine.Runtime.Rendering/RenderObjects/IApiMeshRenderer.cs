using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

/// <summary>Submits one mesh draw to the explicitly selected render owner.</summary>
public interface IApiMeshRenderer
{
    /// <summary>Retains the draw's canonical identity where the backend queues its work.</summary>
    void Render(
        Matrix4x4 modelMatrix,
        Matrix4x4 previousModelMatrix,
        XRMaterial? materialOverride,
        RenderingParameters? renderOptionsOverride,
        uint instances,
        EMeshBillboardMode billboardMode,
        bool forceNoStereo,
        in AdvancedGpuSceneDrawIdentitySnapshot canonicalDrawIdentitySnapshot);
}
