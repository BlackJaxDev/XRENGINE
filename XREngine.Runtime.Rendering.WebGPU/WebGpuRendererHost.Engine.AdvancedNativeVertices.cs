using System.Numerics;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private bool TryDispatchAdvancedNativeVertices(WebGpuAdvancedVisibilityFrame frame, out string reason)
    {
        WebGpuAdvancedNativeVertexFrame native = frame.NativeVertices;
        reason = string.Empty;
        if (!native.HasWork || native.Executed) return true;
        if (!HasAdvancedLimit("maxStorageBuffersPerShaderStage", 2) ||
            !HasAdvancedLimit("maxComputeWorkgroupSizeX", 64) || !HasAdvancedLimit("maxComputeInvocationsPerWorkgroup", 64))
        {
            reason = "WebGPU.Advanced.NativeVertexLimits: the selected device cannot run the exact local-vertex compute profile.";
            return false;
        }
        // Prepare every program first; no partially recorded companion family can
        // become valid history while another program is still pending.
        for (int index = 0; index < native.Count; index++)
        {
            ref readonly WebGpuAdvancedNativeVertexJob job = ref native.Jobs[index];
            WebGpuAdvancedNativeVertexProgramContract.Validate(job.Material.Program!);
            uint groups = BitOperations.RoundUpToPowerOf2(Math.Max(1u, (job.Source.VertexCount + 63u) / 64u));
            if (!HasAdvancedLimit("maxComputeWorkgroupsPerDimension", groups))
            {
                reason = "WebGPU.Advanced.NativeVertexDispatchCapacity: the exact authored draw exceeds the selected compute dispatch extent.";
                return false;
            }
            XRRenderProgram program = GetAdvancedStageProgram(job.Material.Program!);
            WebGpuRenderProgram api = (WebGpuRenderProgram)GetOrCreateAPIRenderObject(program)!;
            if (api.TryPrepareForCompute()) continue;
            MarkEngineDrawPending();
            reason = "WebGPU.Advanced.NativeVertexProgramPending: the exact authored local-vertex compute companion is preparing.";
            return false;
        }
        for (int index = 0; index < native.Count; index++)
        {
            ref readonly WebGpuAdvancedNativeVertexJob job = ref native.Jobs[index];
            XRRenderProgram program = GetAdvancedStageProgram(job.Material.Program!);
            WebGpuRenderProgram api = (WebGpuRenderProgram)GetOrCreateAPIRenderObject(program)!;
            try
            {
                api.SetNativeBindingCacheOwner(native.Geometry);
                api.BindStorageBuffer(0, frame.Scene!.GeometryArena);
                api.BindStorageBuffer(1, native.Geometry);
                AdvancedNativeVertexInputs current = job.Material.Inputs, previous = job.PreviousInputs;
                program.Uniform("NativeVertexInput0", current.Input0);
                program.Uniform("NativeVertexInput1", current.Input1);
                program.Uniform("NativeVertexInput2", current.Input2);
                program.Uniform("NativeVertexInput3", current.Input3);
                program.Uniform("PreviousNativeVertexInput0", previous.Input0);
                program.Uniform("PreviousNativeVertexInput1", previous.Input1);
                program.Uniform("PreviousNativeVertexInput2", previous.Input2);
                program.Uniform("PreviousNativeVertexInput3", previous.Input3);
                program.Uniform("SourceCurrentStream", job.Source.Skinned ? 2u : 0u);
                program.Uniform("SourceCurrentOffset", checked(job.Source.GeometryOffsets.VertexOffset * 64u));
                program.Uniform("SourcePreviousStream", job.Source.Skinned && job.PreviousValid ? 3u : job.Source.Skinned ? 2u : 0u);
                program.Uniform("SourcePreviousOffset", checked((job.Source.Skinned && job.PreviousValid
                    ? job.Source.GeometryOffsets.PreviousVertexOffset : job.Source.GeometryOffsets.VertexOffset) * 64u));
                program.Uniform("CurrentOutputOffset", job.CurrentOutputOffset);
                program.Uniform("PreviousOutputOffset", job.PreviousOutputOffset);
                program.Uniform("VertexCount", job.Source.VertexCount);
                program.Uniform("PreviousValid", job.PreviousValid ? 1u : 0u);
                uint groups = BitOperations.RoundUpToPowerOf2(Math.Max(1u, (job.Source.VertexCount + 63u) / 64u));
                ERendererComputeEnqueueStatus status = TryDispatchCompute(program, groups, 1, 1);
                if (status != ERendererComputeEnqueueStatus.Enqueued)
                {
                    reason = $"WebGPU.Advanced.NativeVertexDispatchRejected: {status}.";
                    return false;
                }
            }
            finally { api.ClearTransientComputeBindings(); }
        }
        native.Executed = true;
        return true;
    }
}
