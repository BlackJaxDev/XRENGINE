using System.Runtime.InteropServices;
using XREngine.Rendering.Shaders.Compilation;
using static XREngine.Rendering.XRRenderProgram;

namespace XREngine.Rendering.WebGPU;

/// <summary>Exact frozen material cohorts and GPU tile classification/indirect native shading for one output family.</summary>
internal sealed partial class WebGpuAdvancedShadingOutput : IDisposable
{
    private static readonly string[] TextureNames = ["NativeTexture0", "NativeTexture1", "NativeTexture2", "NativeTexture3",
        "NativeTexture4", "NativeTexture5", "NativeTexture6", "NativeTexture7", "NativeTexture8", "NativeTexture9", "NativeTexture10", "NativeTexture11"];
    private readonly WebGpuRendererHost _renderer;
    private readonly WebGpuAdvancedShadingFrame[] _frames;
    private readonly WebGpuAdvancedBankPadding _padding;
    private readonly Action<WebGpuAdvancedSampler> _onSamplerUnused;

    internal WebGpuAdvancedShadingOutput(WebGpuRendererHost renderer)
    {
        _renderer = renderer;
        _padding = new(renderer);
        _onSamplerUnused = ReleaseSampler;
        _frames = new WebGpuAdvancedShadingFrame[AdvancedFrameSlotContract.DefaultSlotCount];
        for (int index = 0; index < _frames.Length; index++) _frames[index] = new(renderer);
    }

    internal bool TryClassify(in AdvancedVisibilityStageBackendRequest request, XRRenderPipelineInstance instance,
        WebGpuAdvancedVisibilityFrame visibility, out string reason)
    {
        WebGpuAdvancedShadingFrame frame = _frames[visibility.Scene!.SlotIndex];
        if (frame.ClassifiedSequence == visibility.FrameSequence && frame.PreparationGeneration == visibility.PreparationGeneration &&
            frame.AuthoredDecalsEnabled == request.EnableAuthoredDecals &&
            HasCurrentAuthoredDecalCount(in request, frame) &&
            frame.AuthoredDecalCommandSignature == AuthoredDecalCommandSignature(in request))
        { reason = string.Empty; return true; }
        bool multisample = request.MsaaSampleCount == 4;
        WebGpuRenderProgram classify = Program(instance, multisample ? "shade-classify-msaa" : "shade-classify");
        WebGpuRenderProgram finalize = Program(instance, "shade-finalize");
        if (!(Prepare(classify) & Prepare(finalize))) return Pending(out reason);
        XRTexture2D identity = Texture(instance, multisample ? AdvancedVisibilityResourceNames.IdentityMultisample : request.IdentityTargetName,
            request.Target.Width, request.Target.Height, request.MsaaSampleCount);
        XRTexture2D metadata = Texture(instance, multisample ? AdvancedVisibilityResourceNames.MetadataSelectionMultisample : request.MetadataTargetName,
            request.Target.Width, request.Target.Height, request.MsaaSampleCount);
        ReadOnlySpan<AdvancedShadowRecord> shadows = visibility.Scene.Snapshot.GlobalResources.Shadows.PhysicalRecords;
        for (int index = 0; index < shadows.Length; index++)
        {
            ref readonly AdvancedShadowRecord shadow = ref shadows[index];
            if (!visibility.Scene.Snapshot.GlobalResources.Shadows.TryGetDenseIndex(new(shadow.StableShadowId, shadow.Generation), out uint dense) ||
                dense != (uint)index || (shadow.Flags & EAdvancedShadowRecordFlags.BrowserStandalonePcss) == 0 ||
                !ViewMatches(shadow.ViewMaskLo, shadow.ViewMaskHi, request.NativeViewIndex)) continue;
            if (!visibility.Scene.Snapshot.ResourcePayloads.TryGetTextureSource(shadow.Texture.Handle, out XRTexture source, out _))
            {
                _renderer.MarkEngineDrawPending();
                reason = "WebGPU.Advanced.ShadowPending: the standalone shadow candidate is awaiting its exact retained texture source.";
                return false;
            }
            if (!_renderer.TryValidateBrowserStandaloneShadow(in shadow, source, out reason))
            {
                _renderer.MarkEngineDrawPending();
                return false;
            }
        }
        try
        {
            PrepareCohorts(in request, visibility, frame);
            Span<uint> parameters = stackalloc uint[8];
            parameters.Clear();
            parameters[0] = frame.Width; parameters[1] = frame.Height;
            parameters[2] = (frame.Width + 15) / 16; parameters[3] = (frame.Height + 15) / 16;
            parameters[4] = request.NativeViewIndex; parameters[5] = (uint)frame.CohortCount; parameters[6] = frame.TileCapacity;
            classify.BindStorageBuffer(0, visibility.Scene.SceneArena);
            classify.BindStorageBuffer(1, frame.Materials);
            classify.BindStorageBuffer(2, frame.Tiles);
            classify.BindStorageBuffer(3, frame.Counts);
            classify.SetUniformBlock("Parameters", MemoryMarshal.AsBytes(parameters));
            classify.Data.Sampler(multisample ? "RawVisibilityIdentity" : "VisibilityIdentity", identity, 0);
            classify.Data.Sampler(multisample ? "RawVisibilityMetadataSelection" : "VisibilityMetadata", metadata, 1);
            classify.RecordCompute(parameters[2], parameters[3], 1);
            parameters.Clear(); parameters[0] = (uint)frame.CohortCount; parameters[1] = frame.TileCapacity;
            finalize.BindStorageBuffer(0, frame.Counts);
            finalize.BindStorageBuffer(1, frame.Arguments);
            finalize.SetUniformBlock("Parameters", MemoryMarshal.AsBytes(parameters[..4]));
            finalize.RecordCompute(((uint)frame.CohortCount + 63) / 64, 1, 1);
            frame.ClassifiedSequence = visibility.FrameSequence;
            frame.PreparationGeneration = visibility.PreparationGeneration;
            reason = string.Empty;
            return true;
        }
        finally { classify.ClearTransientComputeBindings(); finalize.ClearTransientComputeBindings(); }
    }

    internal bool TryShade(in AdvancedVisibilityStageBackendRequest request, XRRenderPipelineInstance instance,
        WebGpuAdvancedVisibilityFrame visibility, out string reason)
    {
        WebGpuAdvancedShadingFrame frame = _frames[visibility.Scene!.SlotIndex];
        if (frame.ClassifiedSequence != visibility.FrameSequence || frame.PreparationGeneration != visibility.PreparationGeneration ||
            frame.Width != request.Target.Width || frame.Height != request.Target.Height ||
            frame.AuthoredDecalsEnabled != request.EnableAuthoredDecals ||
            !HasCurrentAuthoredDecalCount(in request, frame) ||
            frame.AuthoredDecalCommandSignature != AuthoredDecalCommandSignature(in request))
        { reason = "WebGPU.Advanced.ClassificationMissing: shade requires the same frozen view, scene slot, and GPU classification generation."; return false; }
        if (request.MsaaSampleCount == 4) return TryShadeMultisample(in request, instance, visibility, frame, out reason);
        EWebGpuAdvancedNativeShadingFamily nativeFamily = SelectNativeFamily(instance, in request, frame);
        WebGpuRenderProgram native = Program(instance, NativePass(frame, nativeFamily, false, false));
        WebGpuRenderProgram? uberNative = frame.HasUberRaster ? UberProgram(instance, frame.DepthComparisonBank, false, false) : null;
        WebGpuRenderProgram? uberExports = frame.HasUberRaster && request.RequiresMaterialSurfaceExports ? UberProgram(instance, frame.DepthComparisonBank, false, true) : null;
        WebGpuRenderProgram background = Program(instance, "shade-background");
        WebGpuRenderProgram? exports = request.RequiresMaterialSurfaceExports ? Program(instance, NativePass(frame, nativeFamily, false, true)) : null;
        WebGpuRenderProgram? exportBackground = request.RequiresMaterialSurfaceExports ? Program(instance, "shade-background-exports") : null;
        bool ready = Prepare(native) & Prepare(background);
        if (exports is not null) ready &= Prepare(exports) & Prepare(exportBackground!);
        if (uberNative is not null) ready &= Prepare(uberNative);
        if (uberExports is not null) ready &= Prepare(uberExports);
        if (!ready) return Pending(out reason);
        _padding.Generate();
        XRTexture2D identity = Texture(instance, request.IdentityTargetName, frame.Width, frame.Height);
        XRTexture2D metadata = Texture(instance, request.MetadataTargetName, frame.Width, frame.Height);
        XRTexture2D depth = Texture(instance, request.DepthTargetName, frame.Width, frame.Height);
        XRTexture2D ao = AmbientOcclusionTexture(instance, in request, frame);
        // Resolve the complete writable family before recording its first initialization command.
        XRTexture2D hdr = Texture(instance, AdvancedRenderPipeline.HDRSceneTextureName, frame.Width, frame.Height);
        XRTexture2D velocity = Texture(instance, AdvancedRenderPipeline.VelocityTextureName, frame.Width, frame.Height);
        XRTexture2D reactive = Texture(instance, AdvancedShadingResourceNames.ReactiveMask, frame.Width, frame.Height);
        XRTexture2D diagnostics = Texture(instance, AdvancedShadingResourceNames.ShadingDiagnostics, frame.Width, frame.Height);
        XRTexture2D? emission = null, albedo = null, normal = null, rmse = null;
        if (exports is not null)
        {
            emission = Texture(instance, AdvancedRenderPipeline.EmissionColorTextureName, frame.Width, frame.Height);
            albedo = Texture(instance, AdvancedRenderPipeline.AlbedoOpacityTextureName, frame.Width, frame.Height);
            normal = Texture(instance, AdvancedRenderPipeline.NormalTextureName, frame.Width, frame.Height);
            rmse = Texture(instance, AdvancedRenderPipeline.RMSETextureName, frame.Width, frame.Height);
        }
        try
        {
            Span<uint> backgroundParameters = stackalloc uint[8];
            backgroundParameters.Clear(); backgroundParameters[0] = frame.Width; backgroundParameters[1] = frame.Height;
            System.Numerics.Vector4 clear = RuntimeEngine.StartupPresentationClearColor;
            MemoryMarshal.Cast<uint, System.Numerics.Vector4>(backgroundParameters[4..])[0] = clear;
            background.SetUniformBlock("Parameters", MemoryMarshal.AsBytes(backgroundParameters));
            background.Data.Sampler("VisibilityIdentity", identity, 0);
            BindOutputs(background, hdr, velocity, reactive, diagnostics, 1);
            background.RecordCompute((frame.Width + 15) / 16, (frame.Height + 15) / 16, 1);
            if (exportBackground is not null)
            {
                exportBackground.SetUniformBlock("Parameters", MemoryMarshal.AsBytes(backgroundParameters[..4]));
                BindExports(exportBackground, emission!, albedo!, normal!, rmse!);
                exportBackground.RecordCompute((frame.Width + 15) / 16, (frame.Height + 15) / 16, 1);
            }
            if (frame.HasUberRaster && !_renderer.TryEnqueueAdvancedUberSample(in request, instance, visibility, 0u, out reason)) return false;
            Span<uint> parameters = stackalloc uint[40];
            for (uint cohort = 0; cohort < frame.CohortCount; cohort++)
            {
                WebGpuRenderProgram selectedNative = frame.Cohorts[cohort]!.UberRaster ? uberNative! : native;
                WebGpuRenderProgram? selectedExports = frame.Cohorts[cohort]!.UberRaster ? uberExports : exports;
                WebGpuAdvancedShadingParameters.Write(parameters, in request, visibility, frame, cohort, _renderer.RequireFrozenView().ElapsedTime);
                BindNative(selectedNative, visibility, frame, cohort, parameters, identity, metadata, depth, ao);
                BindOutputs(selectedNative, hdr, velocity, reactive, diagnostics, 0);
                _renderer.DispatchComputeIndirect(selectedNative.Data, frame.Arguments, (nuint)cohort * 16);
                if (selectedExports is null) continue;
                BindNative(selectedExports, visibility, frame, cohort, parameters, identity, metadata, depth, ao);
                BindExports(selectedExports, emission!, albedo!, normal!, rmse!);
                _renderer.DispatchComputeIndirect(selectedExports.Data, frame.Arguments, (nuint)cohort * 16);
            }
            if (frame.HasUberRaster) return _renderer.TryCompleteAdvancedUberConsumers(out reason);
            reason = string.Empty;
            return true;
        }
        finally
        {
            uberNative?.ClearTransientComputeBindings(); uberExports?.ClearTransientComputeBindings();
            native.ClearTransientComputeBindings(); background.ClearTransientComputeBindings();
            exports?.ClearTransientComputeBindings(); exportBackground?.ClearTransientComputeBindings();
        }
    }

    private void BindNative(WebGpuRenderProgram api, WebGpuAdvancedVisibilityFrame visibility,
        WebGpuAdvancedShadingFrame frame, uint index, ReadOnlySpan<uint> parameters,
        XRTexture2D identity, XRTexture2D metadata, XRTexture2D depth, XRTexture2D ao, bool multisample = false)
    {
        WebGpuAdvancedShadingCohort cohort = frame.Cohorts[index]!;
        api.SetNativeBindingCacheOwner(cohort.Bindings);
        api.BindStorageBuffer(0, visibility.Scene!.SceneArena);
        api.BindStorageBuffer(1, visibility.GeometryArena);
        api.BindStorageBuffer(2, visibility.PreparedDeformations);
        api.BindStorageBuffer(3, frame.Materials);
        api.BindStorageBuffer(4, frame.Tiles);
        api.BindStorageBuffer(5, frame.Counts);
        api.BindStorageBuffer(6, cohort.Bindings);
        api.SetUniformBlock("FrozenView", MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in visibility.View, 1)));
        api.SetUniformBlock("Parameters", MemoryMarshal.AsBytes(parameters));
        api.Data.Sampler(multisample ? "RawVisibilityIdentity" : "VisibilityIdentity", identity, 0);
        api.Data.Sampler(multisample ? "RawVisibilityMetadataSelection" : "VisibilityMetadata", metadata, 1);
        api.Data.Sampler(multisample ? "RawVisibilityDepth" : "VisibilityDepth", depth, 2); api.Data.Sampler("AmbientOcclusion", ao, 3);
        if (cohort.UberRaster)
        {
            if (!visibility.UberRaster.IsCurrent(visibility.FrameSequence, visibility.PreparationGeneration, (parameters[7] >> 24) & 3u))
                throw Invalid("UberRasterProducerMissing", "native consumption requires the same frame and preparation generation's completed full-float fragment exports");
            api.Data.Sampler("UberRasterSurface", visibility.UberRaster.Surface!, 12);
        }
        for (int slot = 0; slot < TextureNames.Length; slot++)
        {
            if (cohort.UberRaster && slot == 8) continue;
            AbstractRenderAPIObject? texture = cohort.TextureOwners[slot];
            WebGpuAdvancedSampler? sampler = cohort.SamplerOwners[slot];
            api.BindAdvancedTexture(TextureNames[slot], texture ?? _padding, texture is null ? _padding.View(slot, frame.DepthComparisonBank) : cohort.Views[slot],
                sampler is null ? _padding : sampler, sampler?.ResourceHandle ?? _padding.Sampler(frame.DepthComparisonBank && slot == 9));
        }
    }

    private static void BindOutputs(WebGpuRenderProgram api, XRTexture2D hdr, XRTexture2D velocity,
        XRTexture2D reactive, XRTexture2D diagnostics, uint first)
    {
        api.Data.BindImageTexture(first, hdr, 0, false, 0, EImageAccess.WriteOnly, EImageFormat.RGBA16F);
        api.Data.BindImageTexture(first + 1, velocity, 0, false, 0, EImageAccess.WriteOnly, EImageFormat.RGBA16F);
        api.Data.BindImageTexture(first + 2, reactive, 0, false, 0, EImageAccess.WriteOnly, EImageFormat.R32F);
        api.Data.BindImageTexture(first + 3, diagnostics, 0, false, 0, EImageAccess.WriteOnly, EImageFormat.R32UI);
    }
    private static void BindExports(WebGpuRenderProgram api, XRTexture2D emission, XRTexture2D albedo, XRTexture2D normal, XRTexture2D rmse)
    {
        api.Data.BindImageTexture(0, emission, 0, false, 0, EImageAccess.WriteOnly, EImageFormat.RGBA16F);
        api.Data.BindImageTexture(1, albedo, 0, false, 0, EImageAccess.WriteOnly, EImageFormat.RGBA16F);
        api.Data.BindImageTexture(2, normal, 0, false, 0, EImageAccess.WriteOnly, EImageFormat.RGBA16F);
        api.Data.BindImageTexture(3, rmse, 0, false, 0, EImageAccess.WriteOnly, EImageFormat.RGBA16F);
    }
    private WebGpuRenderProgram UberProgram(XRRenderPipelineInstance instance, bool depth, bool multisample, bool exports)
        => Program(instance, exports ? multisample ? depth ? "shade-uber-surface-exports-depth-msaa" : "shade-uber-surface-exports-msaa"
            : depth ? "shade-uber-surface-exports-depth" : "shade-uber-surface-exports"
            : multisample ? depth ? "shade-uber-native-depth-msaa" : "shade-uber-native-msaa"
            : depth ? "shade-uber-native-depth" : "shade-uber-native");

    private WebGpuRenderProgram Program(XRRenderPipelineInstance instance, string pass)
    {
        WebGpuRenderProgram api = _renderer.GetAdvancedStageApi(instance.Pipeline!, pass switch
        {
            "shade-native-no-modifiers" => "advanced::shade-native-no-modifiers",
            "shade-native-no-modifiers-msaa" => "advanced::shade-native-no-modifiers-msaa",
            "shade-surface-exports-no-modifiers" => "advanced::shade-surface-exports-no-modifiers",
            "shade-surface-exports-no-modifiers-msaa" => "advanced::shade-surface-exports-no-modifiers-msaa",
            "shade-native-no-decals" => "advanced::shade-native-no-decals",
            "shade-native-depth-no-decals" => "advanced::shade-native-depth-no-decals",
            "shade-native-no-decals-msaa" => "advanced::shade-native-no-decals-msaa",
            "shade-native-depth-no-decals-msaa" => "advanced::shade-native-depth-no-decals-msaa",
            "shade-surface-exports-no-decals" => "advanced::shade-surface-exports-no-decals",
            "shade-surface-exports-depth-no-decals" => "advanced::shade-surface-exports-depth-no-decals",
            "shade-surface-exports-no-decals-msaa" => "advanced::shade-surface-exports-no-decals-msaa",
            "shade-surface-exports-depth-no-decals-msaa" => "advanced::shade-surface-exports-depth-no-decals-msaa",
            "shade-uber-native" => "advanced::shade-uber-native",
            "shade-uber-native-depth" => "advanced::shade-uber-native-depth",
            "shade-uber-native-msaa" => "advanced::shade-uber-native-msaa",
            "shade-uber-native-depth-msaa" => "advanced::shade-uber-native-depth-msaa",
            "shade-uber-surface-exports" => "advanced::shade-uber-surface-exports",
            "shade-uber-surface-exports-depth" => "advanced::shade-uber-surface-exports-depth",
            "shade-uber-surface-exports-msaa" => "advanced::shade-uber-surface-exports-msaa",
            "shade-uber-surface-exports-depth-msaa" => "advanced::shade-uber-surface-exports-depth-msaa",
            "shade-classify" => "advanced::shade-classify",
            "shade-classify-msaa" => "advanced::shade-classify-msaa",
            "shade-finalize" => "advanced::shade-finalize",
            "shade-native" => "advanced::shade-native",
            "shade-native-depth" => "advanced::shade-native-depth",
            "shade-native-msaa" => "advanced::shade-native-msaa",
            "shade-native-depth-msaa" => "advanced::shade-native-depth-msaa",
            "shade-surface-exports" => "advanced::shade-surface-exports",
            "shade-surface-exports-depth" => "advanced::shade-surface-exports-depth",
            "shade-surface-exports-msaa" => "advanced::shade-surface-exports-msaa",
            "shade-surface-exports-depth-msaa" => "advanced::shade-surface-exports-depth-msaa",
            "shade-background" => "advanced::shade-background",
            "shade-background-exports" => "advanced::shade-background-exports",
            "shade-background-exports-msaa" => "advanced::shade-background-exports-msaa",
            "shade-msaa-resolve" => "advanced::shade-msaa-resolve",
            _ => throw Invalid("ShadingProgram", "unknown native program"),
        });
        WebGpuAdvancedShadingProgramContract.Validate(api.Artifact, pass);
        return api;
    }
    private static bool Prepare(WebGpuRenderProgram api) => api.TryPrepareForCompute();
    private static XRTexture2D AmbientOcclusionTexture(XRRenderPipelineInstance instance,
        in AdvancedVisibilityStageBackendRequest request, WebGpuAdvancedShadingFrame frame)
    {
        bool fullSize = request.AmbientOcclusionTargetName == AdvancedAmbientOcclusionContract.ResourceName;
        if (request.EnableBuiltInAmbientOcclusion && !fullSize)
            throw Invalid("AmbientOcclusionResourceMismatch", "enabled AO requires its full-size output before native shading may sample it");
        return Texture(instance, request.AmbientOcclusionTargetName,
            fullSize ? frame.Width : 1u, fullSize ? frame.Height : 1u);
    }

    private bool Pending(out string reason)
    {
        _renderer.MarkEngineDrawPending();
        reason = "WebGPU.Advanced.ShadingPending: the complete native shading program family is preparing.";
        return false;
    }
    private static XRTexture2D Texture(XRRenderPipelineInstance instance, string name, uint width, uint height, uint samples = 1)
    {
        if (!instance.Resources.TryGetTexture(name, out XRTexture? value) || value is not XRTexture2D texture ||
            texture.Width != width || texture.Height != height || texture.MultiSampleCount != samples)
            throw Invalid("ShadingResourceMismatch", "the frozen output requires matching 2D shading resources and exact sample counts");
        return texture;
    }
    public void Dispose()
    {
        foreach (WebGpuAdvancedShadingFrame frame in _frames) frame.Dispose();
        foreach (WebGpuAdvancedSampler sampler in _samplers.Values) sampler.Dispose();
        _samplers.Clear(); _padding.Dispose();
    }
}
