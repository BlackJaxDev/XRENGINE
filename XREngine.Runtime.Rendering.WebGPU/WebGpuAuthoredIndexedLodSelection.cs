using System.Numerics;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

/// <summary>Completion-owned GPU mesh/LOD selection for one frozen source and view.</summary>
internal sealed class WebGpuAuthoredIndexedLodSelection(WebGpuRendererHost renderer) : IDisposable
{
    internal WebGpuOwnedStorageBuffer Selected { get; } = new(renderer, "Authored indexed selected mesh and LOD");

    internal void Record(in GpuMeshSubmissionRecord source, XRCamera camera, WebGpuRenderProgram program)
    {
        Selected.EnsureCapacity(16);
        RenderFrameViewSelection view = renderer.RequireFrozenView();
        Matrix4x4 projection = view.ProjectionMatrix;
        GPUScene.LODTableEntry lod = source.LodMetadata;
        try
        {
            program.SetNativeBindingCacheOwner(Selected);
            program.BindStorageBuffer(0, Selected);
            program.Data.Uniform("BoundsSphere", source.Bounds.BoundingSphere);
            Vector4 position = view.View.CameraPositionAndNear;
            program.Data.Uniform("CameraPosition", new Vector4(position.X, position.Y, position.Z, 1));
            program.Data.Uniform("ProjectionAndViewport", new Vector4(projection.M11, projection.M22, view.ViewportSize.X, view.ViewportSize.Y));
            program.Data.Uniform("Lod0MeshId", lod.LOD0_MeshDataID);
            program.Data.Uniform("Lod1MeshId", lod.LOD1_MeshDataID);
            program.Data.Uniform("Lod2MeshId", lod.LOD2_MeshDataID);
            program.Data.Uniform("Lod3MeshId", lod.LOD3_MeshDataID);
            program.Data.Uniform("Lod0MinRadius", lod.LOD0_MinProjectedRadiusPixels);
            program.Data.Uniform("Lod1MinRadius", lod.LOD1_MinProjectedRadiusPixels);
            program.Data.Uniform("Lod2MinRadius", lod.LOD2_MinProjectedRadiusPixels);
            program.Data.Uniform("Lod3MinRadius", lod.LOD3_MinProjectedRadiusPixels);
            program.Data.Uniform("LodCount", source.LodCount);
            program.Data.Uniform("CurrentMeshId", source.Metadata.MeshID);
            program.Data.Uniform("CurrentLod", source.Metadata.LodPolicy);
            program.Data.Uniform("Flags", source.Metadata.Flags);
            program.RecordCompute(1, 1, 1);
        }
        finally { program.ClearTransientComputeBindings(); }
    }

    public void Dispose() => Selected.Dispose();
}
