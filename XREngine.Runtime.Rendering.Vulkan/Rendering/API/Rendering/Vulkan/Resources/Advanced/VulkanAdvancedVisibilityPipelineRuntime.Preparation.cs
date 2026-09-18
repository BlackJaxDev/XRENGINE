using System.Threading;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanAdvancedVisibilityPipelineRuntime
{
    private readonly object _preparationGate = new();
    private CancellationTokenSource? _preparationCancellation;
    private Task? _preparationTask;
    private PreparationSnapshot _preparation = new(
        VulkanAdvancedVisibilityPipelineReadiness.Missing,
        "Advanced visibility pipeline preparation has not been requested.");
    private int _completedPreparationIdentity;
    private bool _preparationStopped;
    private VulkanAdvancedNativeComputePipelines _preparedNativeComputePipelines;

    internal VulkanAdvancedVisibilityPipelineReadiness GetReadiness(out string reason)
    {
        if (!_resources.AdvancedSceneResources.IsReady ||
            !_resources.AdvancedVisibilityResources.IsReady)
        {
            reason = !_resources.AdvancedSceneResources.IsReady
                ? _resources.AdvancedSceneResources.AvailabilityReason
                : _resources.AdvancedVisibilityResources.AvailabilityReason;
            return VulkanAdvancedVisibilityPipelineReadiness.Missing;
        }

        RequestPreparation();
        PreparationSnapshot preparation = Volatile.Read(ref _preparation);
        reason = preparation.Reason;
        return preparation.State;
    }

    internal void StopPreparation()
    {
        Task? task;
        CancellationTokenSource? cancellation;
        lock (_preparationGate)
        {
            if (_preparationStopped)
                return;

            _preparationStopped = true;
            task = _preparationTask;
            cancellation = _preparationCancellation;
            cancellation?.Cancel();
        }

        try
        {
            task?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            WaitForPendingShaderCompiles();
            cancellation?.Dispose();
        }
    }

    private void RequestPreparation()
    {
        lock (_preparationGate)
        {
            RefreshGeneratedShaderSources();
            if (_preparationStopped || _preparationTask is { IsCompleted: false })
                return;

            VulkanAdvancedVisibilityPipelineReadiness state =
                Volatile.Read(ref _preparation).State;
            int identity = CapturePreparationIdentity();
            if (state == VulkanAdvancedVisibilityPipelineReadiness.Ready &&
                identity == Volatile.Read(ref _completedPreparationIdentity) &&
                AreRequiredProgramsCurrent())
            {
                return;
            }
            if (state == VulkanAdvancedVisibilityPipelineReadiness.Failed &&
                identity == Volatile.Read(ref _completedPreparationIdentity))
            {
                return;
            }

            _preparationCancellation?.Dispose();
            _preparationCancellation = new CancellationTokenSource();
            CancellationToken cancellationToken = _preparationCancellation.Token;
            PublishPreparationState(
                VulkanAdvancedVisibilityPipelineReadiness.Pending,
                "Advanced visibility pipeline preparation is pending.");
            _preparationTask = Task.Run(
                () => PrepareRequiredFamilyAsync(identity, cancellationToken),
                cancellationToken);
        }
    }

    private async Task PrepareRequiredFamilyAsync(
        int preparationIdentity,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                VulkanAdvancedVisibilityPipelineReadiness readiness =
                    PrepareRequiredFamilyOnce(out string reason);
                if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Pending)
                {
                    int currentIdentity = CapturePreparationIdentity();
                    if (currentIdentity != preparationIdentity)
                    {
                        preparationIdentity = currentIdentity;
                        PublishPreparationState(
                            VulkanAdvancedVisibilityPipelineReadiness.Pending,
                            "Advanced visibility shader identity changed during preparation.");
                        continue;
                    }

                    Volatile.Write(ref _completedPreparationIdentity, preparationIdentity);
                    PublishPreparationState(readiness, reason);
                    return;
                }

                PublishPreparationState(readiness, reason);
                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PublishPreparationState(
                VulkanAdvancedVisibilityPipelineReadiness.Failed,
                "Advanced visibility pipeline preparation was canceled during renderer shutdown.");
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _completedPreparationIdentity, preparationIdentity);
            PublishPreparationState(
                VulkanAdvancedVisibilityPipelineReadiness.Failed,
                exception.Message);
        }
    }

    private VulkanAdvancedVisibilityPipelineReadiness PrepareRequiredFamilyOnce(out string reason)
    {
        VulkanAdvancedVisibilityPipelineReadiness readiness =
            PrepareComputePipelines(out _, out _, out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
            return readiness;

        readiness = PrepareLateVisibilityComputePipelines(out _, out _, out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
            return readiness;

        readiness = PrepareNativeComputePipelines(out _preparedNativeComputePipelines, out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
            return readiness;

        readiness = PrepareRasterFamily(meshlet: false, multiview: false, out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
            return readiness;

        bool supportsMeshlets =
            _resources.BackendObjectContext?.DeviceContext.SupportsMeshTaskIndirectCount == true;
        if (supportsMeshlets)
        {
            readiness = PrepareRasterFamily(meshlet: true, multiview: false, out reason);
            if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
                return readiness;
        }

        bool supportsMultiview =
            _resources.BackendObjectContext?.DeviceContext.AdvancedMultiviewEnabled == true;
        if (supportsMultiview)
        {
            readiness = PrepareRasterFamily(meshlet: false, multiview: true, out reason);
            if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
                return readiness;
        }

        if (supportsMeshlets && supportsMultiview &&
            _resources.AdvancedVisibilityResources.SupportsMultiviewMeshRaster)
        {
            readiness = PrepareRasterFamily(meshlet: true, multiview: true, out reason);
            if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
                return readiness;
        }

        reason = "Ready";
        return VulkanAdvancedVisibilityPipelineReadiness.Ready;
    }

    private VulkanAdvancedVisibilityPipelineReadiness PrepareRasterFamily(
        bool meshlet,
        bool multiview,
        out string reason)
    {
        VulkanAdvancedVisibilityPipelineReadiness readiness = PrepareRasterProgram(
            EAdvancedMaterialCoverageMode.Opaque,
            meshlet,
            out _,
            out reason,
            multiview);
        return readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready
            ? readiness
            : PrepareRasterProgram(
                EAdvancedMaterialCoverageMode.Masked,
                meshlet,
                out _,
                out reason,
                multiview);
    }

    private void PublishPreparationState(
        VulkanAdvancedVisibilityPipelineReadiness state,
        string reason)
        => Volatile.Write(ref _preparation, new PreparationSnapshot(state, reason));

    private void WaitForPendingShaderCompiles()
    {
        WaitForPendingShaderCompiles(_earlyVisibilityProgram);
        WaitForPendingShaderCompiles(_buildIndirectProgram);
        WaitForPendingShaderCompiles(_buildDepthPyramidProgram);
        WaitForPendingShaderCompiles(_lateVisibilityProgram);
        WaitForPendingShaderCompiles(_opaqueRasterProgram);
        WaitForPendingShaderCompiles(_maskedRasterProgram);
        WaitForPendingShaderCompiles(_opaqueMeshRasterProgram);
        WaitForPendingShaderCompiles(_maskedMeshRasterProgram);
        WaitForPendingShaderCompiles(_opaqueMultiviewRasterProgram);
        WaitForPendingShaderCompiles(_maskedMultiviewRasterProgram);
        WaitForPendingShaderCompiles(_opaqueMultiviewMeshRasterProgram);
        WaitForPendingShaderCompiles(_maskedMultiviewMeshRasterProgram);
        for (int i = 0; i < _nativeComputePrograms.Length; i++)
            WaitForPendingShaderCompiles(_nativeComputePrograms[i]);
    }

    private void WaitForPendingShaderCompiles(XRRenderProgram? source)
    {
        if (source is not null &&
            _resources.WrapperLookup.GetOrCreate(source, generateNow: false) is VkRenderProgram program)
        {
            program.WaitForPendingShaderCompiles();
        }
    }

    private int CapturePreparationIdentity()
    {
        HashCode hash = new();
        hash.Add(RuntimeEngine.Rendering.Settings.ShaderConfigVersion);
        AddProgramIdentity(ref hash, _earlyVisibilityProgram);
        AddProgramIdentity(ref hash, _buildIndirectProgram);
        AddProgramIdentity(ref hash, _buildDepthPyramidProgram);
        AddProgramIdentity(ref hash, _lateVisibilityProgram);
        AddProgramIdentity(ref hash, _opaqueRasterProgram);
        AddProgramIdentity(ref hash, _maskedRasterProgram);
        AddProgramIdentity(ref hash, _opaqueMeshRasterProgram);
        AddProgramIdentity(ref hash, _maskedMeshRasterProgram);
        AddProgramIdentity(ref hash, _opaqueMultiviewRasterProgram);
        AddProgramIdentity(ref hash, _maskedMultiviewRasterProgram);
        AddProgramIdentity(ref hash, _opaqueMultiviewMeshRasterProgram);
        AddProgramIdentity(ref hash, _maskedMultiviewMeshRasterProgram);
        for (int i = 0; i < _nativeComputePrograms.Length; i++)
            AddProgramIdentity(ref hash, _nativeComputePrograms[i]);
        return hash.ToHashCode();
    }

    private static void AddProgramIdentity(ref HashCode hash, XRRenderProgram? program)
    {
        if (program is null)
            return;
        foreach (XRShader shader in program.Shaders)
            hash.Add(shader.SourceRevision);
    }

    private bool AreRequiredProgramsCurrent()
    {
        if (!IsProgramCurrent(_earlyVisibilityProgram, compute: true) ||
            !IsProgramCurrent(_buildIndirectProgram, compute: true) ||
            !IsProgramCurrent(_buildDepthPyramidProgram, compute: true) ||
            !IsProgramCurrent(_lateVisibilityProgram, compute: true))
        {
            return false;
        }
        for (int i = 0; i < _nativeComputePrograms.Length; i++)
            if (!IsProgramCurrent(_nativeComputePrograms[i], compute: true))
                return false;
        if (!IsProgramCurrent(_opaqueRasterProgram, compute: false) ||
            !IsProgramCurrent(_maskedRasterProgram, compute: false))
        {
            return false;
        }

        bool supportsMeshlets =
            _resources.BackendObjectContext?.DeviceContext.SupportsMeshTaskIndirectCount == true;
        if (supportsMeshlets &&
            (!IsProgramCurrent(_opaqueMeshRasterProgram, compute: false) ||
             !IsProgramCurrent(_maskedMeshRasterProgram, compute: false)))
        {
            return false;
        }

        bool supportsMultiview =
            _resources.BackendObjectContext?.DeviceContext.AdvancedMultiviewEnabled == true;
        if (supportsMultiview &&
            (!IsProgramCurrent(_opaqueMultiviewRasterProgram, compute: false) ||
             !IsProgramCurrent(_maskedMultiviewRasterProgram, compute: false)))
        {
            return false;
        }
        return !supportsMeshlets || !supportsMultiview ||
            !_resources.AdvancedVisibilityResources.SupportsMultiviewMeshRaster ||
            (IsProgramCurrent(_opaqueMultiviewMeshRasterProgram, compute: false) &&
             IsProgramCurrent(_maskedMultiviewMeshRasterProgram, compute: false));
    }

    private bool IsProgramCurrent(XRRenderProgram? source, bool compute)
        => source is not null &&
           _resources.WrapperLookup.GetOrCreate(source, generateNow: false) is VkRenderProgram program &&
           program.IsLinkConfigurationCurrent() &&
           program.PipelineLayout.Handle != 0 &&
           (!compute || program.ComputePipeline.Handle != 0);

    private sealed record PreparationSnapshot(
        VulkanAdvancedVisibilityPipelineReadiness State,
        string Reason);
}