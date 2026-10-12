using XREngine.Data.Rendering;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private readonly Dictionary<XRTexture2D, WebGpuAutoExposureHistory> _autoExposureHistories = [];

    /// <summary>Exposure uses core WebGPU compute, storage buffers and write-only storage images.</summary>
    public override bool SupportsGpuAutoExposure => State == BrowserRendererState.Ready &&
        HasAutoExposureLimit("maxComputeInvocationsPerWorkgroup", 256) &&
        HasAutoExposureLimit("maxComputeWorkgroupSizeX", 256) &&
        HasAutoExposureLimit("maxComputeWorkgroupStorageSize", 2048) &&
        HasAutoExposureLimit("maxStorageBuffersPerShaderStage", 1) &&
        HasAutoExposureLimit("maxStorageTexturesPerShaderStage", 1);

    private bool HasAutoExposureLimit(string name, long minimum)
        => DeviceCapabilities is { } capabilities && capabilities.Limits.TryGetValue(name, out long actual) && actual >= minimum;

    /// <inheritdoc />
    public override bool UpdateAutoExposureGpu(XRTexture sourceTex, XRTexture2D exposureTex,
        ColorGradingSettings settings, float deltaTime, bool generateMipmapsNow)
        => UpdateAutoExposureGpu(sourceTex, exposureTex, settings, deltaTime, generateMipmapsNow, "auto-exposure");

    /// <summary>Records the explicitly selected pipeline program after the current HDR producer, without CPU exposure readback.</summary>
    public override bool UpdateAutoExposureGpu(XRTexture sourceTex, XRTexture2D exposureTex,
        ColorGradingSettings settings, float deltaTime, bool generateMipmapsNow, string? gpuProgramBinding)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.RequiresAutoExposure)
            return false;
        RequireReady();
        if (!_engineRecording)
            throw new InvalidOperationException("WebGPU.Exposure.FrameRequired: exposure must follow HDR production in an active engine frame.");
        if (!SupportsGpuAutoExposure)
            throw new NotSupportedException("WebGPU.Exposure.DeviceUnsupported: the device cannot execute the bounded GPU metering workgroup.");
        string binding = gpuProgramBinding ?? "auto-exposure";
        if (RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.Pipeline is not { } pipeline ||
            !pipeline.TryGetWebPipelineArtifact(binding, out ShaderProgramArtifact? artifact))
            throw new NotSupportedException($"WebGPU.Exposure.ProgramMissing: the active pipeline must supply its exact selected '{binding}' compute program.");
        if (sourceTex is not XRTexture2D source || ReferenceEquals(source, exposureTex) ||
            source.Width == 0 || source.Height == 0 || source.MultiSampleCount != 1 || source.LargestMipmapLevel != 0 ||
            source.SizedInternalFormat is not (ESizedInternalFormat.Rgba16f or ESizedInternalFormat.Rgba32f or ESizedInternalFormat.Rgba8) ||
            exposureTex.Width != 1 || exposureTex.Height != 1 || exposureTex.MultiSampleCount != 1 ||
            exposureTex.SizedInternalFormat != ESizedInternalFormat.R32f || !exposureTex.RequiresStorageUsage)
            throw new NotSupportedException("WebGPU.Exposure.ResourceMismatch: a resolved linear HDR 2D source and distinct declared 1x1 R32F storage output are required.");
        if (!Enum.IsDefined(settings.AutoExposureMetering))
            throw new NotSupportedException("WebGPU.Exposure.MeteringUnsupported: the authored metering mode is not recognized.");
        // Earlier asynchronous producers already withheld this atomic frame.
        if (_engineDrawPending)
            return false;
        WebGpuTexture2D input = (WebGpuTexture2D)GetOrCreateAPIRenderObject(source, generateNow: true)!;
        if (!input.WasProducedInFrame(EngineFrameSequence))
            throw new InvalidOperationException("WebGPU.Exposure.HdrProducerMissing: the selected HDR source has no producer in this recorded frame.");
        WebGpuTexture2D output = (WebGpuTexture2D)GetOrCreateAPIRenderObject(exposureTex, generateNow: true)!;
        if (!_autoExposureHistories.TryGetValue(exposureTex, out WebGpuAutoExposureHistory? history) || !history.Matches(output, artifact))
        {
            if (history is null && _autoExposureHistories.Count >= 64)
                throw new NotSupportedException("WebGPU.Exposure.HistoryCapacity: the renderer exceeds 64 live exposure output generations.");
            WebGpuAutoExposureHistory replacement = new(this, output, artifact);
            history?.Dispose();
            _autoExposureHistories[exposureTex] = history = replacement;
        }
        XRRenderProgram program = history.Program;
        WebGpuRenderProgram api = (WebGpuRenderProgram)GetOrCreateAPIRenderObject(program)!;
        if (!api.TryPrepareForCompute())
        {
            MarkEngineDrawPending();
            return false;
        }
        // The canonical mipless path samples the current base mip directly; it
        // never regenerates stale/out-of-band mips or uses host luminance reads.
        _ = generateMipmapsNow;
        settings.GetResolvedExposureBounds(out float minimum, out float maximum);
        float exposureBase = settings.ExposureMode == ColorGradingSettings.ExposureControlMode.Physical
            ? settings.ComputePhysicalExposureMultiplier() : 1.0f;
        float fallback = settings.ExposureMode == ColorGradingSettings.ExposureControlMode.Physical
            ? exposureBase : settings.Exposure;
        float alpha = 1.0f - MathF.Exp(-settings.ExposureTransitionSpeed * ColorGradingSettings.SanitizeGpuAutoExposureDeltaSeconds(deltaTime));
        program.Uniform("LuminanceWeights", settings.AutoExposureLuminanceWeights);
        program.Uniform("AutoExposureBias", settings.AutoExposureBias);
        program.Uniform("AutoExposureScale", settings.AutoExposureScale);
        program.Uniform("ExposureDividend", settings.ExposureDividend);
        program.Uniform("MinExposure", minimum);
        program.Uniform("MaxExposure", maximum);
        program.Uniform("ExposureBase", exposureBase);
        program.Uniform("FallbackExposure", Math.Clamp(fallback, minimum, maximum));
        program.Uniform("ExposureTransitionSpeed", alpha);
        program.Uniform("MeteringMode", (uint)settings.AutoExposureMetering);
        program.Uniform("MeteringTargetSize", (uint)settings.AutoExposureMeteringTargetSize);
        program.Uniform("IgnoreTopPercent", settings.AutoExposureIgnoreTopPercent);
        program.Uniform("CenterWeightStrength", settings.AutoExposureCenterWeightStrength);
        program.Uniform("CenterWeightPower", settings.AutoExposureCenterWeightPower);
        try
        {
            program.Sampler("SourceTex", source, 0);
            program.BindImageTexture(1, exposureTex, 0, false, 0,
                XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.R32F);
            api.BindStorageBuffer(2, history.Storage);
            ERendererComputeEnqueueStatus status = TryDispatchCompute(program, 1, 1, 1);
            if (status == ERendererComputeEnqueueStatus.ProgramPending)
                return false;
            if (status != ERendererComputeEnqueueStatus.Enqueued)
                throw new InvalidOperationException($"WebGPU.Exposure.DispatchRejected: {status}.");
            return true;
        }
        finally { api.ClearTransientComputeBindings(); }
    }

    private void RetireObsoleteAutoExposureHistories()
    {
        // Removal during dictionary enumeration is supported; no steady-state
        // arrays, closures, GPU uploads or command descriptions are allocated.
        foreach ((XRTexture2D target, WebGpuAutoExposureHistory history) in _autoExposureHistories)
            if (history.IsObsolete)
            {
                _autoExposureHistories.Remove(target);
                history.Dispose();
            }
    }

    private void DestroyAutoExposureHistories()
    {
        foreach (WebGpuAutoExposureHistory history in _autoExposureHistories.Values)
            history.Dispose();
        _autoExposureHistories.Clear();
    }
}
