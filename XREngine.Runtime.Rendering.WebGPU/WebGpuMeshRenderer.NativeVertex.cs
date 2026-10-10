using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.Shaders.Generation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuMeshRenderer
{
    private static void RequireNativeVertexRasterSource(XRMesh mesh, XRMaterial? material)
    {
        if (AdvancedNativeVertexMaterialSource.IsRequested(material) &&
            AdvancedNativeVertexMaterialSource.RequiresCanonicalDeformationSource(mesh))
            throw new NotSupportedException("WebGPU.NativeVertex.GenericDeformationSourceUnsupported: the authored local function requires canonical aggregate geometry after morph/skin; the generic deformation source has a different normal domain and cannot be substituted.");
    }

    private static void RequireNativeVertexRasterSource(XRMesh mesh, ShaderProgramArtifact artifact)
    {
        if (artifact.NativeVertexCompanion?.Profile == EngineNativeVertexShaderGenerator.Profile &&
            AdvancedNativeVertexMaterialSource.RequiresCanonicalDeformationSource(mesh))
            throw new NotSupportedException("WebGPU.NativeVertex.GenericDeformationSourceUnsupported: the selected local-function raster program requires canonical aggregate geometry after morph/skin; the generic deformation source cannot be substituted.");
    }
}
