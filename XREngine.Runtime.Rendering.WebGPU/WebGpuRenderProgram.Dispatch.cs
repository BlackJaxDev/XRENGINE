namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRenderProgram
{
    /// <summary>Routes the shared program event through its active output's ordered compute recorder.</summary>
    private void DispatchCompute(uint x, uint y, uint z,
        IEnumerable<(uint unit, IRenderTextureResource texture, int level, int? layer,
            XRRenderProgram.EImageAccess access, XRRenderProgram.EImageFormat format)>? textures)
    {
        if (!ReferenceEquals(AbstractRenderer.Current, Renderer)) return;
        try
        {
            if (textures is not null)
                foreach (var binding in textures)
                    SetImage(binding.unit, binding.texture, binding.level, binding.layer.HasValue, binding.layer ?? 0, binding.access, binding.format);
            ERendererComputeEnqueueStatus status = Renderer.TryDispatchCompute(Data, x, y, z);
            if (status is not (ERendererComputeEnqueueStatus.Enqueued or ERendererComputeEnqueueStatus.ProgramPending))
                throw new InvalidOperationException($"WebGPU.Compute.DispatchRejected: {status}.");
        }
        finally
        {
            ClearTransientComputeBindings();
        }
    }
}
