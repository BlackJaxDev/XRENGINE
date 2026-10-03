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
        if (frame.ClassifiedSequence == visibility.FrameSequence && frame.PreparationGeneration == visibility.PreparationGeneration)
        { reason = string.Empty; return true; }
        WebGpuRenderProgram classify = Program(instance, "shade-classify");
        WebGpuRenderProgram finalize = Program(instance, "shade-finalize");
        if (!(Prepare(classify) & Prepare(finalize))) return Pending(out reason);
        XRTexture2D identity = Texture(instance, request.IdentityTargetName, request.Target.Width, request.Target.Height);
        XRTexture2D metadata = Texture(instance, request.MetadataTargetName, request.Target.Width, request.Target.Height);
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
            classify.Data.Sampler("VisibilityIdentity", identity, 0);
            classify.Data.Sampler("VisibilityMetadata", metadata, 1);
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
            frame.Width != request.Target.Width || frame.Height != request.Target.Height)
        { reason = "WebGPU.Advanced.ClassificationMissing: shade requires the same frozen view, scene slot, and GPU classification generation."; return false; }
        WebGpuRenderProgram native = Program(instance, "shade-native");
        WebGpuRenderProgram background = Program(instance, "shade-background");
        WebGpuRenderProgram? exports = request.RequiresMaterialSurfaceExports ? Program(instance, "shade-surface-exports") : null;
        WebGpuRenderProgram? exportBackground = request.RequiresMaterialSurfaceExports ? Program(instance, "shade-background-exports") : null;
        bool ready = Prepare(native) & Prepare(background);
        if (exports is not null) ready &= Prepare(exports) & Prepare(exportBackground!);
        if (!ready) return Pending(out reason);
        _padding.Generate();
        XRTexture2D identity = Texture(instance, request.IdentityTargetName, frame.Width, frame.Height);
        XRTexture2D metadata = Texture(instance, request.MetadataTargetName, frame.Width, frame.Height);
        XRTexture2D depth = Texture(instance, request.DepthTargetName, frame.Width, frame.Height);
        XRTexture2D ao = Texture(instance, request.AmbientOcclusionTargetName, frame.Width, frame.Height);
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
            Span<uint> parameters = stackalloc uint[40];
            for (uint cohort = 0; cohort < frame.CohortCount; cohort++)
            {
                WebGpuAdvancedShadingParameters.Write(parameters, in request, visibility, frame, cohort);
                BindNative(native, visibility, frame, cohort, parameters, identity, metadata, depth, ao);
                BindOutputs(native, hdr, velocity, reactive, diagnostics, 0);
                _renderer.DispatchComputeIndirect(native.Data, frame.Arguments, (nuint)cohort * 16);
                if (exports is null) continue;
                BindNative(exports, visibility, frame, cohort, parameters, identity, metadata, depth, ao);
                BindExports(exports, emission!, albedo!, normal!, rmse!);
                _renderer.DispatchComputeIndirect(exports.Data, frame.Arguments, (nuint)cohort * 16);
            }
            reason = string.Empty;
            return true;
        }
        finally
        {
            native.ClearTransientComputeBindings(); background.ClearTransientComputeBindings();
            exports?.ClearTransientComputeBindings(); exportBackground?.ClearTransientComputeBindings();
        }
    }

    private void BindNative(WebGpuRenderProgram api, WebGpuAdvancedVisibilityFrame visibility,
        WebGpuAdvancedShadingFrame frame, uint index, ReadOnlySpan<uint> parameters,
        XRTexture2D identity, XRTexture2D metadata, XRTexture2D depth, XRTexture2D ao)
    {
        WebGpuAdvancedShadingCohort cohort = frame.Cohorts[index]!;
        api.SetNativeBindingCacheOwner(cohort.Bindings);
        api.BindStorageBuffer(0, visibility.Scene!.SceneArena);
        api.BindStorageBuffer(1, visibility.Scene.GeometryArena);
        api.BindStorageBuffer(2, visibility.PreparedDeformations);
        api.BindStorageBuffer(3, frame.Materials);
        api.BindStorageBuffer(4, frame.Tiles);
        api.BindStorageBuffer(5, frame.Counts);
        api.BindStorageBuffer(6, cohort.Bindings);
        api.SetUniformBlock("FrozenView", MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in visibility.View, 1)));
        api.SetUniformBlock("Parameters", MemoryMarshal.AsBytes(parameters));
        api.Data.Sampler("VisibilityIdentity", identity, 0); api.Data.Sampler("VisibilityMetadata", metadata, 1);
        api.Data.Sampler("VisibilityDepth", depth, 2); api.Data.Sampler("AmbientOcclusion", ao, 3);
        for (int slot = 0; slot < TextureNames.Length; slot++)
        {
            AbstractRenderAPIObject? texture = cohort.TextureOwners[slot];
            WebGpuAdvancedSampler? sampler = cohort.SamplerOwners[slot];
            api.BindAdvancedTexture(TextureNames[slot], texture ?? _padding, texture is null ? _padding.View(slot) : cohort.Views[slot],
                sampler is null ? _padding : sampler, sampler?.ResourceHandle ?? _padding.Sampler);
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
    private WebGpuRenderProgram Program(XRRenderPipelineInstance instance, string pass)
    {
        WebGpuRenderProgram api = _renderer.GetAdvancedStageApi(instance.Pipeline!, pass switch { "shade-classify" => "advanced::shade-classify", "shade-finalize" => "advanced::shade-finalize", "shade-native" => "advanced::shade-native", "shade-surface-exports" => "advanced::shade-surface-exports", "shade-background" => "advanced::shade-background", "shade-background-exports" => "advanced::shade-background-exports", _ => throw Invalid("ShadingProgram", "unknown native program") });
        WebGpuAdvancedShadingProgramContract.Validate(api.Artifact, pass);
        return api;
    }
    private static bool Prepare(WebGpuRenderProgram api) => api.TryPrepareForCompute();
    private bool Pending(out string reason)
    {
        _renderer.MarkEngineDrawPending();
        reason = "WebGPU.Advanced.ShadingPending: the complete native shading program family is preparing.";
        return false;
    }
    private static XRTexture2D Texture(XRRenderPipelineInstance instance, string name, uint width, uint height)
    {
        if (!instance.Resources.TryGetTexture(name, out XRTexture? value) || value is not XRTexture2D texture ||
            texture.Width != width || texture.Height != height || texture.MultiSampleCount != 1)
            throw Invalid("ShadingResourceMismatch", "the frozen output requires matching single-sample 2D shading resources");
        return texture;
    }
    public void Dispose()
    {
        foreach (WebGpuAdvancedShadingFrame frame in _frames) frame.Dispose();
        foreach (WebGpuAdvancedSampler sampler in _samplers.Values) sampler.Dispose();
        _samplers.Clear(); _padding.Dispose();
    }
}
