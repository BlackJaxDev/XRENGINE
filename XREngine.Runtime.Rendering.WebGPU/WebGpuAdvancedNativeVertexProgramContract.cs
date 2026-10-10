using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

/// <summary>Validates the exact generated local-vertex producer before any native geometry is recorded.</summary>
internal static class WebGpuAdvancedNativeVertexProgramContract
{
    private static readonly string[] Providers =
    [
        "NativeVertexInput0", "NativeVertexInput1", "NativeVertexInput2", "NativeVertexInput3",
        "PreviousNativeVertexInput0", "PreviousNativeVertexInput1", "PreviousNativeVertexInput2", "PreviousNativeVertexInput3",
        "SourceCurrentStream", "SourceCurrentOffset", "SourcePreviousStream", "SourcePreviousOffset",
        "CurrentOutputOffset", "PreviousOutputOffset", "VertexCount", "PreviousValid",
    ];

    internal static void Validate(ShaderProgramArtifact artifact)
    {
        if (artifact.Target != ShaderCompileTarget.WebGPUWgsl || artifact.Pass != "native-material-vertex" ||
            artifact.ComputeEntryPoint != "nativeMaterialVertex" || artifact.VertexEntryPoint is not null ||
            artifact.FragmentEntryPoint is not null || artifact.ComputeWorkgroupSize != new ShaderComputeWorkgroupSize(64, 1, 1) ||
            artifact.Resources.Length != 3 || !artifact.VertexBuffers.IsDefaultOrEmpty)
            throw Invalid();
        for (uint binding = 0; binding < 3; binding++)
        {
            ShaderStageResourceLayout? found = null;
            foreach (ShaderStageResourceLayout resource in artifact.Resources)
                if (resource.Contract.Set == 0 && resource.Contract.Binding == binding) found = resource;
            if (found is not { } entry || entry.Visibility != ShaderStageVisibility.Compute) throw Invalid();
            if (binding < 2)
            {
                if (entry.BindingType != (binding == 0 ? "read-only-storage" : "storage") ||
                    entry.DynamicOffset || !entry.RuntimeArray || entry.Contract.ByteSize != 4 || !entry.Contract.Members.IsEmpty)
                    throw Invalid();
                continue;
            }
            if (entry.BindingType != "uniform" || !entry.DynamicOffset || entry.RuntimeArray ||
                entry.Contract.ByteSize != 160 || entry.Contract.Members.Length != 16) throw Invalid();
            for (int index = 0; index < Providers.Length; index++)
            {
                ShaderAbiMemberContract member = entry.Contract.Members[index];
                uint offset = checked((uint)(index < 8 ? index * 16 : 128 + (index - 8) * 4));
                if (member.ProviderName != Providers[index] || member.Offset != offset ||
                    member.Size != (index < 8 ? 16u : 4u) || member.PhysicalType != (index < 8 ? "vec4<f32>" : "u32") ||
                    member.ArrayCount != 0 || member.ArrayStride != 0 || member.MatrixOrder != ShaderAbiMatrixOrder.None)
                    throw Invalid();
            }
        }
    }

    private static NotSupportedException Invalid()
        => new("WebGPU.Advanced.NativeVertexAbiMismatch: recook the exact local position/normal packed-vertex companion.");
}
