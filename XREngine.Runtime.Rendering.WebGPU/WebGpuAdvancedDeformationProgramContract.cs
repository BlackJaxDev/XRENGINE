using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

/// <summary>Validates the packed aggregate producer and ordered geometry-copy ABIs.</summary>
internal static class WebGpuAdvancedDeformationProgramContract
{
    private static readonly string[] AggregateParameters = ["FirstGroupedJob", "GroupedJobCount", "BatchVertexCount", "Reserved0"];
    private static readonly string[] CopyParameters = ["CurrentWordCount", "PreviousWordCount", "Reserved0", "Reserved1"];

    internal static void Validate(ShaderProgramArtifact artifact, bool copy)
    {
        uint uniformBinding = copy ? 5u : 3u;
        string[] parameters = copy ? CopyParameters : AggregateParameters;
        if (artifact.SourceLanguage != "WGSL" || artifact.SemanticSchemaIdentity != "xrengine.engine.compute.v1" ||
            artifact.Target != ShaderCompileTarget.WebGPUWgsl || artifact.Pass != (copy ? "deformation-copy" : "aggregate-deformation") ||
            artifact.ComputeEntryPoint != (copy ? "advancedDeformationCopy" : "advancedAggregateDeformation") ||
            artifact.VertexEntryPoint is not null || artifact.FragmentEntryPoint is not null ||
            artifact.ComputeWorkgroupSize != new ShaderComputeWorkgroupSize(256, 1, 1) ||
            artifact.Resources.Length != uniformBinding + 1 || !artifact.VertexBuffers.IsDefaultOrEmpty)
            throw Invalid();
        for (uint binding = 0; binding <= uniformBinding; binding++)
        {
            ShaderStageResourceLayout? selected = null;
            foreach (ShaderStageResourceLayout resource in artifact.Resources)
                if (resource.Contract.Set == 0 && resource.Contract.Binding == binding)
                    selected = resource;
            if (selected is not { } entry) throw Invalid();
            if (binding != uniformBinding)
            {
                string type = (copy ? binding == 2u : binding is 1u or 2u) ? "storage" : "read-only-storage";
                if (entry.BindingType != type || entry.DynamicOffset || !entry.RuntimeArray ||
                    entry.Contract.ByteSize != 4 || !entry.Contract.Members.IsEmpty) throw Invalid();
                if ((!copy && binding == 2u || copy && binding == 3u) && entry.Contract.Name != "CurrentAuthoredBasisV1" ||
                    copy && binding == 4u && entry.Contract.Name != "PreviousAuthoredBasisV1") throw Invalid();
                continue;
            }
            if (entry.BindingType != "uniform" || !entry.DynamicOffset || entry.RuntimeArray ||
                entry.Contract.ByteSize != 16 || entry.Contract.Members.Length != 4) throw Invalid();
            for (int member = 0; member < 4; member++)
            {
                ShaderAbiMemberContract value = entry.Contract.Members[member];
                if (value.ProviderName != parameters[member] || value.Offset != member * 4 ||
                    value.Size != 4 || value.PhysicalType != "u32" || value.ArrayCount != 0) throw Invalid();
            }
        }
    }

    private static NotSupportedException Invalid()
        => new("WebGPU.Advanced.DeformationAbiMismatch: recook the canonical aggregate and geometry-copy programs with authored-basis schema 1 and their three/five storage bindings.");
}
