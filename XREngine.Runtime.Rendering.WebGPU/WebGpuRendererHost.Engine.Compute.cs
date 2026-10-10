namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    public override void ResetComputeProgramBindings(XRRenderProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (TryGetAPIRenderObject(program, out AbstractRenderAPIObject? value) && value is WebGpuRenderProgram api)
            api.ClearTransientComputeBindings();
    }

    /// <summary>Records one cooked compute dispatch in the same ordered, atomic frame as raster work.</summary>
    public override void DispatchCompute(XRRenderProgram program, int numGroupsX, int numGroupsY, int numGroupsZ)
    {
        if (numGroupsX < 1 || numGroupsY < 1 || numGroupsZ < 1)
        {
            if (State == BrowserRendererState.Ready && program is not null)
                ((WebGpuRenderProgram)GetOrCreateAPIRenderObject(program)!).ClearTransientComputeBindings();
            throw new ArgumentOutOfRangeException(nameof(numGroupsX), "Compute workgroup counts must be positive.");
        }
        ERendererComputeEnqueueStatus status = TryDispatchCompute(program,
            (uint)numGroupsX, (uint)numGroupsY, (uint)numGroupsZ);
        if (status is not (ERendererComputeEnqueueStatus.Enqueued or ERendererComputeEnqueueStatus.ProgramPending))
            throw new InvalidOperationException($"WebGPU.Compute.DispatchRejected: {status}.");
    }

    public override ERendererComputeEnqueueStatus TryDispatchCompute(
        XRRenderProgram program, uint groupsX, uint groupsY, uint groupsZ)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (State != BrowserRendererState.Ready)
            return ERendererComputeEnqueueStatus.DeviceLost;
        WebGpuRenderProgram api = (WebGpuRenderProgram)GetOrCreateAPIRenderObject(program)!;
        try
        {
            if (!_engineRecording)
                return ERendererComputeEnqueueStatus.NoPassContext;
            if (DeviceCapabilities is not { } capabilities ||
                !capabilities.Limits.TryGetValue("maxComputeWorkgroupsPerDimension", out long limit) ||
                groupsX is 0 || groupsY is 0 || groupsZ is 0 ||
                groupsX > limit || groupsY > limit || groupsZ > limit)
                return ERendererComputeEnqueueStatus.DescriptorInvalid;
            if (!api.TryPrepareForCompute())
            {
                MarkEngineDrawPending();
                return ERendererComputeEnqueueStatus.ProgramPending;
            }
            api.RecordCompute(groupsX, groupsY, groupsZ);
            return ERendererComputeEnqueueStatus.Enqueued;
        }
        finally
        {
            api.ClearTransientComputeBindings();
        }
    }
}
