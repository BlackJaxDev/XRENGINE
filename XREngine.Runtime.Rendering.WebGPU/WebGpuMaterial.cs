using System.Numerics;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

/// <summary>Keeps an engine material's authored stages and callbacks attached to its cooked program.</summary>
public sealed class WebGpuMaterial(WebGpuRendererHost renderer, XRMaterial data)
    : WebGpuObject<XRMaterial>(renderer, data), IRenderPreparationState
{
    private XRRenderProgram? _program;
    private WebGpuRenderProgram? _apiProgram;
    private long _shaderRevision;
    private StandardLitColorSurfaceBinding? _litSurface;
    private bool _directionalShadowReceiver;
    private bool _opaqueShadowDepth;

    public WebGpuRenderProgram Program => _apiProgram
        ?? throw new InvalidOperationException("WebGPU.Material.ProgramPending: the material has not been prepared.");
    public override bool IsGenerated => _apiProgram?.IsGenerated == true;
    public bool IsPreparedForRendering => IsGenerated;

    public override void Generate()
    {
        ValidateOwnerGeneration();
        if (_program is not null && _shaderRevision != Data.ShaderStateRevision)
            throw new NotSupportedException($"WebGPU.Material.ShadersChanged: material '{Data.Name}' requires replacement at a resource-generation boundary.");
        if (_program is null)
        {
            ShaderProgramArtifact? artifact = null;
            if (Data.EngineSemantic == EngineMaterialSemanticIdentity.OpaqueShadowDepthV1)
            {
                if (Data.Shaders.Count != 0 || Data.Parameters.Length != 0)
                    throw new NotSupportedException("WebGPU.Material.ShadowDepthUnsupported: the shared opaque caster must be source-free and have no authored parameters.");
                EngineMaterialVariantKey key = new(Data.EngineSemantic, ShaderCompileTarget.WebGPUWgsl,
                    "depth", "static-position-v1", "depth-normal-v1");
                if (Renderer.MaterialVariants?.TryResolve(key, out artifact) != true)
                    throw new NotSupportedException($"WebGPU.Material.VariantMissing: '{Data.Name}' requires the declared {key} variant.");
                SetField(ref _opaqueShadowDepth, true);
            }
            else if (Data.EngineSemantic.Semantic != EngineMaterialSemantic.None)
            {
                if (!StandardLitColorSurfaceBinding.TryCreate(Data, out StandardLitColorSurfaceBinding? surface, out string? reason))
                    throw new NotSupportedException($"WebGPU.Material.SurfaceUnsupported: '{Data.Name}': {reason}");
                EngineMaterialVariantKey shadowKey = new(Data.EngineSemantic, ShaderCompileTarget.WebGPUWgsl,
                    "opaque-forward", "static-position-normal-v1", "linear-hdr-directional-shadow-v1");
                EngineMaterialVariantKey key = new(Data.EngineSemantic, ShaderCompileTarget.WebGPUWgsl,
                    "opaque-forward", "static-position-normal-v1", "linear-hdr-v1");
                bool shadowReceiver = Renderer.MaterialVariants?.TryResolve(shadowKey, out artifact) == true;
                if (!shadowReceiver && Renderer.MaterialVariants?.TryResolve(key, out artifact) != true)
                    throw new NotSupportedException($"WebGPU.Material.VariantMissing: '{Data.Name}' requires the declared {key} variant.");
                SetField(ref _litSurface, surface);
                SetField(ref _directionalShadowReceiver, shadowReceiver);
            }
            using IDisposable publication = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
            XRRenderProgram program = new(false, false, Data.Shaders) { Name = Data.Name, CookedArtifact = artifact };
            SetField(ref _program, program);
            SetField(ref _shaderRevision, Data.ShaderStateRevision);
            SetField(ref _apiProgram, (WebGpuRenderProgram)Renderer.GetOrCreateAPIRenderObject(program)!);
        }
        _apiProgram!.Generate();
    }

    public bool TryPrepareForRendering()
    {
        Generate();
        return IsGenerated;
    }

    /// <summary>Publishes the canonical surface without changing its authored parameters or render pass.</summary>
    internal void PublishSurface()
    {
        if (_opaqueShadowDepth)
        {
            WebGpuFrameBuffer? depthTarget = Renderer.GetBoundEngineFrameBuffer();
            if (depthTarget is null || depthTarget.HasColor || !depthTarget.HasDepth || depthTarget.SampleCount != 1)
                throw new NotSupportedException("WebGPU.Material.ShadowDepthOutputUnsupported: the opaque caster requires one depth-only single-sample attachment.");
            return;
        }
        if (_litSurface is null) return;
        if (!_litSurface.TryRead(out StandardLitColorSurface surface, out string? reason))
            throw new NotSupportedException($"WebGPU.Material.SurfaceUnsupported: '{Data.Name}': {reason}");
        WebGpuFrameBuffer? target = Renderer.GetBoundEngineFrameBuffer();
        if (target is null || target.ColorFormats.Length != 1 || target.ColorFormats[0] != "rgba16float")
            throw new NotSupportedException("WebGPU.Material.OutputUnsupported: standard lit surfaces require one linear RGBA16F color attachment and explicit presentation.");
        Program.SetVector4("StandardLitBaseColorOpacity", new Vector4(surface.BaseColor, surface.Opacity));
        Program.SetVector4("StandardLitRoughnessMetallicSpecularEmission",
            new Vector4(surface.Roughness, surface.Metallic, surface.Specular, surface.Emission));
        Renderer.PublishForwardLights(Program, _directionalShadowReceiver);
    }

    public override void Destroy()
    {
        _apiProgram?.Dispose();
        _program?.Destroy();
        SetField(ref _apiProgram, null);
        SetField(ref _program, null);
        SetField(ref _litSurface, null);
        SetField(ref _directionalShadowReceiver, false);
        SetField(ref _opaqueShadowDepth, false);
    }
}
