using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

/// <summary>Checks the binding and uniform ABI used by the native compact visibility executor.</summary>
internal static class WebGpuAdvancedVisibilityProgramContract
{
    private static readonly string[] CompactParameters = ["ViewProjectionUnjittered", "PayloadCount", "TriangleCapacity", "CullMode", "Coverage", "RasterStateClass", "Producer", "ViewId", "TriangleBase", "ArgumentBase", "BucketIndex", "Reserved1", "Reserved2"];
    private static readonly string[] RasterParameters = ["ViewProjection", "ViewProjectionUnjittered", "PreviousViewProjectionUnjittered", "TriangleBase", "ViewIndex", "ViewFlags", "Origin", "BaseColorWord", "AlphaCutoffWord", "MaterialFlagsWord", "Masked", "DirectPayloadIndex", "Direct", "OpacityCoverage", "RenderTimeBits"];
    private static readonly string[] FinalizeParameters = ["ArgumentBase", "Reserved0", "Reserved1", "Reserved2"];

    internal static void Validate(ShaderProgramArtifact artifact, string pass)
    {
        bool raster = pass is "visibility-pull" or "visibility-pull-msaa";
        bool compact = pass == "compact-triangles";
        if (!raster && !compact && pass != "finalize-triangles") throw Invalid();
        int count = raster ? 9 : compact ? 8 : 2;
        uint uniformBinding = raster ? 4u : compact ? 7u : 1u;
        uint uniformSize = raster ? 240u : compact ? 112u : 16u;
        int matrixCount = raster ? 3 : compact ? 1 : 0;
        string[] providers = raster ? RasterParameters : compact ? CompactParameters : FinalizeParameters;
        if (artifact.Target != ShaderCompileTarget.WebGPUWgsl || artifact.Pass != pass || artifact.Resources.Length != count ||
            raster && artifact.SemanticSchemaIdentity != "xrengine.engine.advanced-visibility.v3" ||
            !artifact.VertexBuffers.IsDefaultOrEmpty ||
            (raster ? artifact.VertexEntryPoint is null || artifact.FragmentEntryPoint is null || artifact.ComputeEntryPoint is not null
                : artifact.ComputeEntryPoint is null || artifact.VertexEntryPoint is not null || artifact.FragmentEntryPoint is not null ||
                  artifact.ComputeWorkgroupSize != new ShaderComputeWorkgroupSize(compact ? 64u : 1u, 1, 1)))
            throw Invalid();
        for (uint binding = 0; binding < count; binding++)
        {
            ShaderStageResourceLayout resource = Binding(artifact, binding);
            if (binding == uniformBinding)
            {
                if (resource.BindingType != "uniform" || !resource.DynamicOffset || resource.Contract.ByteSize != uniformSize ||
                    resource.Contract.Members.Length != providers.Length) throw Invalid();
                for (int memberIndex = 0; memberIndex < providers.Length; memberIndex++)
                {
                    ShaderAbiMemberContract member = resource.Contract.Members[memberIndex];
                    bool matrix = memberIndex < matrixCount;
                    uint offset = matrix ? checked((uint)memberIndex * 64u) : checked((uint)(matrixCount * 64 + (memberIndex - matrixCount) * 4));
                    if (member.ProviderName != providers[memberIndex] || member.Offset != offset || member.ArrayCount != 0 ||
                        member.Size != (matrix ? 64u : 4u) || member.PhysicalType != (matrix ? "mat4x4<f32>" : "u32") ||
                        matrix && (member.MatrixOrder != ShaderAbiMatrixOrder.ColumnMajor || member.MatrixStride != 16))
                        throw Invalid();
                }
                continue;
            }
            string expected = raster && binding >= 5 ? ((binding & 1u) != 0 ? "texture-2d-float" : "filtering-sampler")
                : compact && binding >= 5 || !raster && !compact ? "storage" : "read-only-storage";
            if (resource.BindingType != expected || resource.DynamicOffset ||
                (expected is "storage" or "read-only-storage") && (!resource.RuntimeArray || resource.Contract.ByteSize != 4 || !resource.Contract.Members.IsEmpty))
                throw Invalid();
        }
    }

    private static ShaderStageResourceLayout Binding(ShaderProgramArtifact artifact, uint binding)
    {
        foreach (ShaderStageResourceLayout resource in artifact.Resources)
            if (resource.Contract.Set == 0 && resource.Contract.Binding == binding) return resource;
        throw Invalid();
    }

    private static NotSupportedException Invalid()
        => new("WebGPU.Advanced.VisibilityAbiMismatch: the cooked program does not implement the exact native compact visibility contract.");
}
