using System.Numerics;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

/// <summary>Supplies the exact frozen billboard transform to the existing GPU bounds producers.</summary>
internal static class WebGpuImpostorBounds
{
    internal static Matrix4x4 CullMatrix(in GpuMeshSubmissionRecord record, XRMaterial material,
        in RenderFrameViewSelection view)
    {
        Matrix4x4 model = record.CurrentWorld;
        if (material.EngineSemantic != EngineMaterialSemanticIdentity.OctahedralImpostorV1) return model;
        if (!Matrix4x4.Invert(view.View.ViewMatrix, out Matrix4x4 inverseView))
            throw new NotSupportedException("WebGPU.Impostor.SingularView: billboard bounds require the same invertible frozen view used by raster.");
        Vector3 right = Vector3.Normalize(new(inverseView.M11, inverseView.M12, inverseView.M13));
        Vector3 up = Vector3.Normalize(new(inverseView.M21, inverseView.M22, inverseView.M23));
        right *= new Vector3(model.M11, model.M12, model.M13).Length();
        up *= new Vector3(model.M21, model.M22, model.M23).Length();
        // Engine matrices are row vectors; WGSL receives their column-vector image.
        // Z contributes nothing in the canonical vertex stage. Plane-space sphere
        // tests remain conservative under this singular affine transform, including
        // nonuniform model scale and meshlets whose local sphere center is not zero.
        return new(right.X, right.Y, right.Z, 0, up.X, up.Y, up.Z, 0,
            0, 0, 0, 0, model.M41, model.M42, model.M43, 1);
    }
}
