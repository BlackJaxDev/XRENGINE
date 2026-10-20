using XREngine.Rendering.Commands;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

/// <summary>Validates the shared authored instance input without decoding GPU-owned contents.</summary>
internal static class WebGpuAuthoredInstanceContract
{
    internal const ulong MaximumCullChecks = 1 << 20;

    internal static AuthoredMeshInstanceSource? Resolve(XRMeshRenderer owner, XRMesh mesh,
        WebGpuRenderProgram program, uint instances, WebGpuMeshDeformation? deformation,
        GpuMeshSubmissionSourceBindings? sources)
    {
        if (deformation is not null && program.Artifact.SemanticSchemaIdentity == AuthoredMeshInstanceSource.TemporalSchemaIdentity)
            throw Invalid("PreviousDeformationUnavailable", "temporal instanced deformation requires an explicit previous vertex stream");
        AuthoredMeshInstanceSource? result = sources?.InstanceSource;
        if (sources is not null)
        {
            if (sources.HasAmbiguousInstanceSources) throw Invalid("AmbiguousSource", "one mesh has multiple instance producers");
            if (result is { HasValidLayout: false }) throw Invalid("SourceRange", "the instance publication contains an invalid storage layout");
            if (!sources.AreInstanceBindingsCurrent) throw Invalid("PublicationChanged", "instance membership or layout changed after publication");
        }
        else
            foreach (IRenderBindingPublisher publisher in owner.BindingPublishers.CaptureSnapshot())
            {
                if (publisher is not IAuthoredMeshInstanceProvider provider || !provider.TryGetInstanceSource(mesh, out var source)) continue;
                if (result.HasValue) throw Invalid("AmbiguousSource", "one mesh has multiple instance producers");
                result = source;
            }
        if (result is not { } input)
        {
            if (instances > 1 && deformation is not null)
                throw Invalid("DeformationSourceMissing", "multiple deformed instances require an explicit pre-instance source contract");
            return null;
        }
        if (!input.HasValidLayout || instances > input.Count)
            throw Invalid("SourceRange", "current/previous transforms and bounds must cover every authored instance");
        ValidateRaster(program.Artifact, in input, deformation is not null);
        if (!program.TryGetStorageBinding(input.BindingName, out WebGpuDataBuffer? bound) ||
            !ReferenceEquals(bound?.Data, input.Buffer))
            throw Invalid("RasterBindingMismatch", "culling and the selected raster must use the same declared instance storage");
        return input;
    }

    internal static void ValidateRaster(ShaderProgramArtifact artifact, in AuthoredMeshInstanceSource input, bool deformed)
    {
        if (!HasSchema(artifact, out bool temporal))
            throw Invalid("RasterContractMissing", "the selected raster must declare the versioned instance and engine-row/WGSL-column matrix contracts");
        bool declared = false;
        foreach (ShaderStageResourceLayout resource in artifact.Resources)
            if (resource.Contract.Name == input.BindingName && resource.Contract.Kind == ShaderAbiResourceKind.StorageBuffer &&
                resource.BindingType == "read-only-storage" && resource.RuntimeArray &&
                (resource.Visibility & ShaderStageVisibility.Vertex) != 0 && resource.Contract.ByteSize == input.StrideBytes)
            {
                bool current = false, previous = false, bounds = false;
                foreach (ShaderAbiMemberContract member in resource.Contract.Members)
                {
                    if (member.ProviderName == "CurrentTransform") current = Matrix(member, input.CurrentTransformOffsetBytes);
                    else if (member.ProviderName == "PreviousTransform") previous = Matrix(member, input.PreviousTransformOffsetBytes);
                    else if (member.ProviderName == "PreInstanceBounds") bounds = member.Offset == input.PreInstanceBoundsOffsetBytes &&
                        member.Size == 16 && member.PhysicalType == "vec4<f32>" && member.ArrayCount == 0;
                }
                declared = current && previous && bounds;
            }
        if (!declared)
            throw Invalid("RasterLayoutMismatch", "the selected raster must declare matching current/previous matrices, pre-instance bounds and row stride");
        if (deformed && !input.AcceptsSharedDeformedGeometry)
            throw Invalid("DeformationSpaceUnproven", "the producer must certify shared mesh-keyed deformation as pre-instance geometry");
        if (deformed && temporal)
            throw Invalid("PreviousDeformationUnavailable", "temporal instanced deformation requires an explicit previous vertex stream");
    }

    private static bool Matrix(ShaderAbiMemberContract member, uint offset)
        => member.Offset == offset && member.Size == 64 && member.PhysicalType == "mat4x4<f32>" &&
           member.MatrixOrder == ShaderAbiMatrixOrder.ColumnMajor && member.MatrixStride == 16 && member.ArrayCount == 0;

    internal static bool CanCull(uint instances, uint meshlets = 1)
        => instances != 0 && (ulong)instances * meshlets <= MaximumCullChecks;

    private static bool HasSchema(ShaderProgramArtifact artifact, out bool temporal)
    {
        temporal = artifact.SemanticSchemaIdentity == AuthoredMeshInstanceSource.TemporalSchemaIdentity;
        return !artifact.DescriptorBytes.IsDefaultOrEmpty &&
            (temporal || artifact.SemanticSchemaIdentity == AuthoredMeshInstanceSource.RasterSchemaIdentity);
    }

    internal static void SetCullParameters(XRRenderProgram program, uint instances, AuthoredMeshInstanceSource? source)
    {
        program.Uniform("InstanceCount", instances);
        program.Uniform("InstanceSourceEnabled", source.HasValue ? 1u : 0u);
        program.Uniform("InstanceStrideWords", source?.StrideBytes / 4 ?? 0);
        program.Uniform("InstanceTransformOffsetWords", source?.CurrentTransformOffsetBytes / 4 ?? 0);
        program.Uniform("InstanceBoundsOffsetWords", source?.PreInstanceBoundsOffsetBytes / 4 ?? 0);
        program.Uniform("InstanceWordCount", source?.Buffer.Length / 4 ?? 0);
    }

    private static NotSupportedException Invalid(string code, string detail)
        => new($"WebGPU.AuthoredInstances.{code}: {detail}.");
}
