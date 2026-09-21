using System.Diagnostics;
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
    private long _foregroundPollTicks;
    private long _foregroundPollCount;
    private long _preparationAttemptTicks;
    private int _preparationAttemptCount;
    private long _preparationStartedTimestamp;
    private long _preparationCompletedTimestamp;
    private readonly VulkanPipelineForegroundWaitObserver _foregroundWaitObserver = new();

    internal VulkanAdvancedVisibilityPipelineReadiness GetReadiness(out string reason)
    {
        long pollStart = Stopwatch.GetTimestamp();
        try
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
        finally
        {
            Interlocked.Add(ref _foregroundPollTicks, Stopwatch.GetTimestamp() - pollStart);
            Interlocked.Increment(ref _foregroundPollCount);
        }
    }

    internal AdvancedVisibilityPreparationDiagnosticsSnapshot CapturePreparationDiagnostics()
    {
        double tickMilliseconds = 1000.0 / Stopwatch.Frequency;
        double sourceMilliseconds = 0.0;
        double linkMilliseconds = 0.0;
        double nativeMilliseconds = 0.0;
        AccumulateProgramTimings(ref sourceMilliseconds, ref linkMilliseconds, ref nativeMilliseconds, _earlyVisibilityProgram);
        AccumulateProgramTimings(ref sourceMilliseconds, ref linkMilliseconds, ref nativeMilliseconds, _buildIndirectProgram);
        AccumulateProgramTimings(ref sourceMilliseconds, ref linkMilliseconds, ref nativeMilliseconds, _buildDepthPyramidProgram);
        AccumulateProgramTimings(ref sourceMilliseconds, ref linkMilliseconds, ref nativeMilliseconds, _lateVisibilityProgram);
        AccumulateProgramTimings(ref sourceMilliseconds, ref linkMilliseconds, ref nativeMilliseconds, _opaqueRasterProgram);
        AccumulateProgramTimings(ref sourceMilliseconds, ref linkMilliseconds, ref nativeMilliseconds, _maskedRasterProgram);
        AccumulateProgramTimings(ref sourceMilliseconds, ref linkMilliseconds, ref nativeMilliseconds, _opaqueMeshRasterProgram);
        AccumulateProgramTimings(ref sourceMilliseconds, ref linkMilliseconds, ref nativeMilliseconds, _maskedMeshRasterProgram);
        AccumulateProgramTimings(ref sourceMilliseconds, ref linkMilliseconds, ref nativeMilliseconds, _opaqueMultiviewRasterProgram);
        AccumulateProgramTimings(ref sourceMilliseconds, ref linkMilliseconds, ref nativeMilliseconds, _maskedMultiviewRasterProgram);
        AccumulateProgramTimings(ref sourceMilliseconds, ref linkMilliseconds, ref nativeMilliseconds, _opaqueMultiviewMeshRasterProgram);
        AccumulateProgramTimings(ref sourceMilliseconds, ref linkMilliseconds, ref nativeMilliseconds, _maskedMultiviewMeshRasterProgram);
        for (int index = 0; index < _nativeComputePrograms.Length; index++)
            AccumulateProgramTimings(ref sourceMilliseconds, ref linkMilliseconds, ref nativeMilliseconds, _nativeComputePrograms[index]);

        PreparationSnapshot snapshot = Volatile.Read(ref _preparation);
        long startedTimestamp = Volatile.Read(ref _preparationStartedTimestamp);
        long completedTimestamp = Volatile.Read(ref _preparationCompletedTimestamp);
        return new(
            snapshot.State.ToString(),
            snapshot.Reason,
            Volatile.Read(ref _preparationAttemptCount),
            startedTimestamp == 0
                ? 0.0
                : Stopwatch.GetElapsedTime(
                    startedTimestamp,
                    completedTimestamp == 0 ? Stopwatch.GetTimestamp() : completedTimestamp).TotalMilliseconds,
            Volatile.Read(ref _preparationAttemptTicks) * tickMilliseconds,
            sourceMilliseconds,
            linkMilliseconds,
            nativeMilliseconds,
            _foregroundWaitObserver.Count,
            _foregroundWaitObserver.Ticks * tickMilliseconds,
            Volatile.Read(ref _foregroundPollCount),
            Volatile.Read(ref _foregroundPollTicks) * tickMilliseconds);
    }

    private void AccumulateProgramTimings(
        ref double sourceMilliseconds,
        ref double linkMilliseconds,
        ref double nativeMilliseconds,
        XRRenderProgram? source)
    {
        if (source is null ||
            _resources.WrapperLookup.GetOrCreate(source, generateNow: false) is not VkRenderProgram program)
            return;
        foreach (XRShader shader in source.Shaders)
            if (_resources.WrapperLookup.GetOrCreate(shader, generateNow: false) is VkShader backendShader)
                sourceMilliseconds += backendShader.LastArtifact?.CompilationMilliseconds ?? 0.0;
        XRRenderProgram.ShaderProgramBackendStatus status = source.ShaderMetadata.Backend;
        linkMilliseconds += status.LinkMilliseconds;
        nativeMilliseconds += program.LastComputePipelineCompileMilliseconds;
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
            _foregroundWaitObserver.Reset();
            Volatile.Write(ref _preparationAttemptCount, 0);
            Volatile.Write(ref _preparationAttemptTicks, 0);
            Volatile.Write(ref _foregroundPollCount, 0);
            Volatile.Write(ref _foregroundPollTicks, 0);
            Volatile.Write(ref _preparationStartedTimestamp, Stopwatch.GetTimestamp());
            Volatile.Write(ref _preparationCompletedTimestamp, 0);
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
                    Volatile.Write(ref _preparationCompletedTimestamp, Stopwatch.GetTimestamp());
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
            Volatile.Write(ref _preparationCompletedTimestamp, Stopwatch.GetTimestamp());
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _completedPreparationIdentity, preparationIdentity);
            PublishPreparationState(
                VulkanAdvancedVisibilityPipelineReadiness.Failed,
                exception.Message);
            Volatile.Write(ref _preparationCompletedTimestamp, Stopwatch.GetTimestamp());
        }
    }

    private VulkanAdvancedVisibilityPipelineReadiness PrepareRequiredFamilyOnce(out string reason)
    {
        long attemptStart = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _preparationAttemptCount);
        try
        {
            using var foregroundWaitObservation =
                new VulkanPipelineForegroundWaitObservationScope(_foregroundWaitObserver);
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
        finally
        {
            Interlocked.Add(ref _preparationAttemptTicks, Stopwatch.GetTimestamp() - attemptStart);
        }
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