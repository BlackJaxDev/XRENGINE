using System.Runtime.InteropServices;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private static readonly string[] RequiredAdvancedMultisamplePrograms =
    [
        "advanced::visibility-pull-msaa", "advanced::visibility-msaa-resolve", "advanced::shade-classify-msaa",
        "advanced::shade-native-msaa", "advanced::shade-msaa-resolve", "advanced::shade-surface-exports-msaa",
        "advanced::shade-native-depth-msaa", "advanced::shade-surface-exports-depth-msaa",
        "advanced::shade-background-exports-msaa",
    ];
    private string? _advancedMultisampleProgramFailure = "WebGPU.Advanced.MultisampleCatalogMissing: the packed visibility family is not installed.";

    private string? ValidateAdvancedMultisamplePrograms(WebPipelineArtifactCatalog? artifacts)
    {
        foreach (string binding in RequiredAdvancedMultisamplePrograms)
        {
            if (artifacts is null || !artifacts.TryResolve(binding, out ShaderProgramArtifact? artifact))
                return $"WebGPU.Advanced.MultisampleProgramMissing: the selected four-sample output requires '{binding}'.";
            if (artifact.Pass == "visibility-pull-msaa")
                WebGpuAdvancedVisibilityProgramContract.Validate(artifact, artifact.Pass);
            else if (artifact.Pass == "visibility-msaa-resolve")
                WebGpuAdvancedMsaaProgramContract.Validate(artifact, artifact.Pass);
            else
                WebGpuAdvancedShadingProgramContract.Validate(artifact, artifact.Pass);
        }
        return null;
    }

    private string? GetAdvancedMultisampleRejection()
    {
        if (_advancedMultisampleProgramFailure is { } missing) return missing;
        if (!HasAdvancedLimit("maxColorAttachments", 3) || !HasAdvancedLimit("maxColorAttachmentBytesPerSample", 20) ||
            !HasAdvancedLimit("maxTextureArrayLayers", 4))
            return "WebGPU.Advanced.MultisampleLimits: packed visibility requires three attachments, 20 color bytes per sample, and four shading-result array layers.";
        foreach (string binding in RequiredAdvancedMultisamplePrograms)
        {
            if (!_advancedPipelineArtifacts!.TryResolve(binding, out ShaderProgramArtifact? artifact))
                return "WebGPU.Advanced.MultisampleCatalogChanged: the frozen four-sample program family is incomplete.";
            foreach ((string limit, int required) in artifact.RequiredLimits)
                if (!HasAdvancedLimit(limit, required))
                    return $"WebGPU.Advanced.MultisampleLimit: '{binding}' requires {limit}>={required}.";
        }
        return null;
    }

    private bool TryResolveAdvancedMultisample(in AdvancedVisibilityStageBackendRequest request,
        XRRenderPipelineInstance instance, WebGpuAdvancedVisibilityFrame frame, out string reason)
    {
        WebGpuRenderProgram resolve = GetAdvancedStageApi(instance.Pipeline!, "advanced::visibility-msaa-resolve");
        WebGpuAdvancedMsaaProgramContract.Validate(resolve.Artifact, "visibility-msaa-resolve");
        if (!resolve.TryPrepareForRendering())
        {
            reason = "WebGPU.Advanced.MultisampleResolvePending: the canonical tuple/depth resolve is preparing.";
            return false;
        }
        XRTexture2D identity = RequireAdvancedMultisampleTexture(instance, AdvancedVisibilityResourceNames.IdentityMultisample,
            request.Target.Width, request.Target.Height, ESizedInternalFormat.Rgba16ui);
        XRTexture2D metadata = RequireAdvancedMultisampleTexture(instance, AdvancedVisibilityResourceNames.MetadataSelectionMultisample,
            request.Target.Width, request.Target.Height, ESizedInternalFormat.Rgba16ui);
        XRTexture2D depth = RequireAdvancedMultisampleTexture(instance, AdvancedVisibilityResourceNames.DepthStencilMultisample,
            request.Target.Width, request.Target.Height, ESizedInternalFormat.Depth32fStencil8);
        WebGpuFrameBuffer? previousTarget = _boundEngineFrameBuffer;
        WebGpuRasterState previousState = _rasterState;
        var previousArea = _engineRenderArea;
        bool previousCropping = _engineCroppingEnabled;
        try
        {
            BindFrameBuffer(EFramebufferTarget.DrawFramebuffer, request.Target);
            SetField(ref _rasterState, WebGpuRasterState.Default with
            {
                DepthComparison = EComparison.Always, CullMode = ECullMode.None,
            }, publishNotifications: false);
            SetField(ref _engineRenderArea, new XREngine.Data.Geometry.BoundingRectangle(0, 0,
                checked((int)request.Target.Width), checked((int)request.Target.Height)), publishNotifications: false);
            SetField(ref _engineCroppingEnabled, false, publishNotifications: false);
            Span<uint> parameters = stackalloc uint[4];
            parameters.Clear(); parameters[0] = request.Target.Width; parameters[1] = request.Target.Height;
            parameters[2] = (frame.View.Flags & EAdvancedViewRecordFlags.ReversedDepth) != 0 || frame.View.DepthParams.W != 0 ? 1u : 0u;
            resolve.SetUniformBlock("Parameters", MemoryMarshal.AsBytes(parameters));
            resolve.Data.Sampler("RawVisibilityIdentity", identity, 0);
            resolve.Data.Sampler("RawVisibilityMetadataSelection", metadata, 1);
            resolve.Data.Sampler("RawVisibilityDepth", depth, 2);
            DrawVertexless(resolve.Data, 3);
        }
        finally
        {
            resolve.ClearTransientComputeBindings();
            SetField(ref _boundEngineFrameBuffer, previousTarget, publishNotifications: false);
            SetField(ref _rasterState, previousState, publishNotifications: false);
            SetField(ref _engineRenderArea, previousArea, publishNotifications: false);
            SetField(ref _engineCroppingEnabled, previousCropping, publishNotifications: false);
        }
        reason = _engineDrawPending ? "WebGPU.Advanced.MultisampleResolvePending: the canonical tuple/depth pipeline is preparing." : string.Empty;
        return !_engineDrawPending;
    }

    private static XRTexture2D RequireAdvancedMultisampleTexture(XRRenderPipelineInstance instance, string name,
        uint width, uint height, ESizedInternalFormat format)
    {
        if (!instance.Resources.TryGetTexture(name, out XRTexture? resource) || resource is not XRTexture2D texture ||
            texture.Width != width || texture.Height != height || texture.MultiSampleCount != 4 || texture.SizedInternalFormat != format)
            throw new NotSupportedException($"WebGPU.Advanced.MultisampleResourceMismatch: '{name}' must retain its exact packed four-sample storage.");
        return texture;
    }

    private static bool IsAdvancedStageTargetCurrent(XRRenderPipelineInstance owner, in AdvancedVisibilityStageBackendRequest request)
    {
        bool raw = request.MsaaSampleCount == 4 && (request.Stage is EAdvancedRenderStage.VisibilityPreparation or EAdvancedRenderStage.VisibilityRaster ||
            request.Stage == EAdvancedRenderStage.DepthPyramidAndLateVisibility && request.Phase != EAdvancedVisibilityStageBackendPhase.MultisampleResolve);
        string name = raw ? AdvancedVisibilityResourceNames.FrameBufferMultisample : AdvancedVisibilityResourceNames.FrameBuffer;
        return owner.Resources.TryGetFrameBuffer(name, out XRFrameBuffer? target) && ReferenceEquals(target, request.Target);
    }
}
