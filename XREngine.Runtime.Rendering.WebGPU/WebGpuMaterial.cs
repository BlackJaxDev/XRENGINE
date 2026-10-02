using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.Shaders.Generation;

namespace XREngine.Rendering.WebGPU;

/// <summary>Keeps an engine material's authored stages and callbacks attached to its cooked program.</summary>
public sealed partial class WebGpuMaterial(WebGpuRendererHost renderer, XRMaterial data)
    : WebGpuObject<XRMaterial>(renderer, data), IRenderPreparationState
{
    private XRRenderProgram? _program;
    private WebGpuRenderProgram? _apiProgram;
    private long _shaderRevision;
    private StandardLitColorSurfaceBinding? _litSurface;
    private bool _directionalShadowReceiver;
    private bool _localShadowReceiver;
    private bool _opaqueShadowDepth;
    private bool _opaquePointShadowDepth;
    private bool _opaqueSpotShadowDepth;
    private EStandardLitColorAuxiliaryPass _litAuxiliaryPass;
    private EngineMaterialSemantic _debugPrimitive;
    private EngineMaterialSemantic _uiSemantic;

    internal WebGpuInstanceStorageContract? InstanceStorageContract => (_uiSemantic, _debugPrimitive) switch
    {
        (EngineMaterialSemantic.UIQuadBatched, _) => new("QuadTransformBuffer", 64, 65536),
        (EngineMaterialSemantic.UITextBatchedBitmap, _) => new("GlyphTransformsBuffer", 16, 65536),
        (_, EngineMaterialSemantic.DebugPoint) => new("PointsBuffer", 16, 65536),
        (_, EngineMaterialSemantic.DebugLine) => new("LinesBuffer", 28, 65536),
        (_, EngineMaterialSemantic.DebugTriangle) => new("TrianglesBuffer", 40, 65536),
        _ => null,
    };

    internal EngineMaterialSemantic UISemantic => _uiSemantic;

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
            if (Data.EngineSemantic == EngineMaterialSemanticIdentity.OpaqueSpotShadowDepthV1)
            {
                if (Data.Shaders.Count != 0 || Data.Parameters.Length != 0)
                    throw new NotSupportedException("WebGPU.Material.SpotShadowDepthUnsupported: the projected caster must be source-free and have no authored parameters.");
                EngineMaterialVariantKey key = new(Data.EngineSemantic, ShaderCompileTarget.WebGPUWgsl,
                    "spot-shadow-depth", "static-position-v1", "projected-r16f-v1");
                if (Renderer.MaterialVariants?.TryResolve(key, out artifact) != true)
                    throw new NotSupportedException($"WebGPU.Material.VariantMissing: '{Data.Name}' requires the declared {key} variant.");
                SetField(ref _opaqueSpotShadowDepth, true);
            }
            else if (Data.EngineSemantic == EngineMaterialSemanticIdentity.OpaquePointShadowDepthV1)
            {
                if (Data.Shaders.Count != 0 || Data.Parameters.Length != 0)
                    throw new NotSupportedException("WebGPU.Material.PointShadowDepthUnsupported: the radial caster must be source-free and have no authored parameters.");
                EngineMaterialVariantKey key = new(Data.EngineSemantic, ShaderCompileTarget.WebGPUWgsl,
                    "point-shadow-depth", "static-position-v1", "radial-r16f-v1");
                if (Renderer.MaterialVariants?.TryResolve(key, out artifact) != true)
                    throw new NotSupportedException($"WebGPU.Material.VariantMissing: '{Data.Name}' requires the declared {key} variant.");
                SetField(ref _opaquePointShadowDepth, true);
            }
            else if (Data.EngineSemantic == EngineMaterialSemanticIdentity.OpaqueShadowDepthV1)
            {
                if (Data.Shaders.Count != 0 || Data.Parameters.Length != 0)
                    throw new NotSupportedException("WebGPU.Material.ShadowDepthUnsupported: the shared opaque caster must be source-free and have no authored parameters.");
                EngineMaterialVariantKey key = new(Data.EngineSemantic, ShaderCompileTarget.WebGPUWgsl,
                    "depth", "static-position-v1", "depth-normal-v1");
                if (Renderer.MaterialVariants?.TryResolve(key, out artifact) != true)
                    throw new NotSupportedException($"WebGPU.Material.VariantMissing: '{Data.Name}' requires the declared {key} variant.");
                SetField(ref _opaqueShadowDepth, true);
            }
            else if (Data.EngineSemantic.Semantic is EngineMaterialSemantic.DebugPoint or EngineMaterialSemantic.DebugLine or EngineMaterialSemantic.DebugTriangle)
            {
                if (Data.Shaders.Count != 0 ||
                    (Data.EngineSemantic.Semantic == EngineMaterialSemantic.DebugTriangle
                        ? Data.Parameters.Length != 0
                        : Data.Parameters.Length != 1 || Data.Parameters[0] is not Rendering.Models.Materials.ShaderVector4))
                    throw new NotSupportedException("WebGPU.Material.DebugUnsupported: the debug material must be source-free and have its exact numeric parameters.");
                string vertexProfile = Data.EngineSemantic.Semantic switch
                {
                    EngineMaterialSemantic.DebugPoint => "instanced-debug-point-v1",
                    EngineMaterialSemantic.DebugLine => "instanced-debug-line-v1",
                    _ => "instanced-debug-triangle-v1",
                };
                EngineMaterialVariantKey key = new(Data.EngineSemantic, ShaderCompileTarget.WebGPUWgsl,
                    "debug-overlay", vertexProfile, "display-rgba-v1");
                if (Renderer.MaterialVariants?.TryResolve(key, out artifact) != true)
                    throw new NotSupportedException($"WebGPU.Material.VariantMissing: '{Data.Name}' requires the declared {key} variant.");
                SetField(ref _debugPrimitive, Data.EngineSemantic.Semantic);
            }
            else if (Data.EngineSemantic.Semantic is EngineMaterialSemantic.UIQuadBatched or EngineMaterialSemantic.UITextBatchedBitmap)
            {
                bool text = Data.EngineSemantic.Semantic == EngineMaterialSemantic.UITextBatchedBitmap;
                if (Data.Shaders.Count != 0 ||
                    (text ? Data.Textures.Count != 1 || Data.Textures[0] is not XRTexture2D atlas ||
                        atlas.SizedInternalFormat != ESizedInternalFormat.R8 : Data.Textures.Count != 0) ||
                    (text ? !HasBitmapTextParameters() : Data.Parameters.Length != 0))
                    throw new NotSupportedException("WebGPU.Material.UIUnsupported: screen UI requires the exact source-free quad or bitmap-text material profile.");
                EngineMaterialVariantKey key = new(Data.EngineSemantic, ShaderCompileTarget.WebGPUWgsl,
                    "screen-ui", text ? "instanced-ui-bitmap-text-v1" : "instanced-ui-quad-v1", "display-rgba-v1");
                if (Renderer.MaterialVariants?.TryResolve(key, out artifact) != true)
                    throw new NotSupportedException($"WebGPU.Material.VariantMissing: '{Data.Name}' requires the declared {key} variant.");
                SetField(ref _uiSemantic, Data.EngineSemantic.Semantic);
            }
            else if (Data.EngineSemantic.IsSkybox())
                artifact = ResolveSkyboxArtifact();
            else if (Data.EngineSemantic == EngineMaterialSemanticIdentity.StandardLitTextureV1)
                artifact = ResolveLitTextureArtifact();
            else if (Data.EngineSemantic == EngineMaterialSemanticIdentity.AuthoredLitV1)
                artifact = ResolveAuthoredLitArtifact();
            else if (Data.EngineSemantic.Semantic != EngineMaterialSemantic.None)
            {
                XRMaterial source = Data.StandardLitColorSourceMaterial ?? Data;
                if (source.Shaders.Count != 0 || Data.Shaders.Count != 0)
                    throw new NotSupportedException("WebGPU.Material.SourceUnsupported: semantic variants require source-free engine materials.");
                if (!StandardLitColorSurfaceBinding.TryCreate(source, out StandardLitColorSurfaceBinding? surface, out string? reason))
                    throw new NotSupportedException($"WebGPU.Material.SurfaceUnsupported: '{Data.Name}': {reason}");
                EStandardLitColorAuxiliaryPass auxiliary = Data.StandardLitColorAuxiliaryPass;
                if (auxiliary != EStandardLitColorAuxiliaryPass.None)
                {
                    if (Data.EngineSemantic != EngineMaterialSemanticIdentity.StandardLitColorV2 ||
                        source.GetEffectiveTransparencyMode() is not (Rendering.Models.Materials.ETransparencyMode.Opaque or Rendering.Models.Materials.ETransparencyMode.Masked))
                        throw new NotSupportedException("WebGPU.Material.CoverageUnsupported: auxiliary coverage requires an opaque or masked V2 surface.");
                    EngineMaterialVariantKey auxiliaryKey = new(Data.EngineSemantic, ShaderCompileTarget.WebGPUWgsl,
                        auxiliary == EStandardLitColorAuxiliaryPass.SpotShadowDepth ? "spot-shadow-depth" :
                            auxiliary == EStandardLitColorAuxiliaryPass.PointShadowDepth ? "point-shadow-depth" :
                            auxiliary == EStandardLitColorAuxiliaryPass.ShadowDepth ? "depth" : "depth-normal",
                        auxiliary == EStandardLitColorAuxiliaryPass.DepthNormal ? "static-position-normal-v1" : "static-position-v1",
                        auxiliary == EStandardLitColorAuxiliaryPass.SpotShadowDepth ? "projected-r16f-v1" :
                            auxiliary == EStandardLitColorAuxiliaryPass.PointShadowDepth ? "radial-r16f-v1" :
                            auxiliary == EStandardLitColorAuxiliaryPass.ShadowDepth ? "depth-normal-v1" : "normal-rgba16f-v1");
                    if (Renderer.MaterialVariants?.TryResolve(auxiliaryKey, out artifact) != true)
                        throw new NotSupportedException($"WebGPU.Material.VariantMissing: '{Data.Name}' requires the declared {auxiliaryKey} variant.");
                    SetField(ref _litAuxiliaryPass, auxiliary);
                }
                else
                {
                    string pass = Data.EngineSemantic == EngineMaterialSemanticIdentity.StandardLitColorV2
                        ? "forward-coverage" : "opaque-forward";
                    EngineMaterialVariantKey shadowKey = new(Data.EngineSemantic, ShaderCompileTarget.WebGPUWgsl,
                        pass, "static-position-normal-v1", "linear-hdr-directional-shadow-v1");
                    EngineMaterialVariantKey localShadowKey = new(Data.EngineSemantic, ShaderCompileTarget.WebGPUWgsl,
                        pass, "static-position-normal-v1", "linear-hdr-local-shadows-v1");
                    EngineMaterialVariantKey key = new(Data.EngineSemantic, ShaderCompileTarget.WebGPUWgsl,
                        pass, "static-position-normal-v1", "linear-hdr-v1");
                    bool localShadowReceiver = Renderer.MaterialVariants?.TryResolve(localShadowKey, out artifact) == true;
                    bool shadowReceiver = localShadowReceiver || Renderer.MaterialVariants?.TryResolve(shadowKey, out artifact) == true;
                    if (!shadowReceiver && Renderer.MaterialVariants?.TryResolve(key, out artifact) != true)
                        throw new NotSupportedException($"WebGPU.Material.VariantMissing: '{Data.Name}' requires the declared {key} variant.");
                    SetField(ref _directionalShadowReceiver, shadowReceiver);
                    SetField(ref _localShadowReceiver, localShadowReceiver);
                }
                SetField(ref _litSurface, surface);
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

    private bool HasBitmapTextParameters()
    {
        if (Data.Parameters.Length != 7) return false;
        string[] names = ["TextAtlasType", "MsdfDistanceRange", "MsdfDistanceRangeMiddle", "MsdfFillBias",
            "TextDebugMode", "TextRenderLayer", "TextRenderLayer_VTX"];
        for (int index = 0; index < names.Length; index++)
        {
            if (!string.Equals(Data.Parameters[index].Name, names[index], StringComparison.Ordinal)) return false;
            if (index is 0 or 4 or 5 or 6)
            {
                if (Data.Parameters[index] is not Rendering.Models.Materials.ShaderInt) return false;
            }
            else if (Data.Parameters[index] is not Rendering.Models.Materials.ShaderFloat) return false;
        }
        return true;
    }

    /// <summary>Publishes the canonical surface without changing its authored parameters or render pass.</summary>
    internal void PublishSurface()
    {
        if (TryPublishSkybox())
            return;
        if (TryPublishLitTexture())
            return;
        if (_uiSemantic != EngineMaterialSemantic.None)
        {
            if (_uiSemantic == EngineMaterialSemantic.UITextBatchedBitmap &&
                (Data.Textures.Count != 1 || Data.Textures[0] is not XRTexture2D { SizedInternalFormat: ESizedInternalFormat.R8 } ||
                 Data.Parameters.Length != 7 ||
                 Data.Parameters[0] is not Rendering.Models.Materials.ShaderInt { Value: 0 } ||
                 Data.Parameters[4] is not Rendering.Models.Materials.ShaderInt { Value: 0 } ||
                 Data.Parameters[5] is not Rendering.Models.Materials.ShaderInt { Value: 0 } ||
                 Data.Parameters[6] is not Rendering.Models.Materials.ShaderInt { Value: 0 }))
                throw new NotSupportedException("WebGPU.Material.UITextProfileUnsupported: only a normal-shaded R8 bitmap atlas with combined fill and outline is admitted.");
            return;
        }
        if (_debugPrimitive != EngineMaterialSemantic.None)
        {
            XRCamera camera = RuntimeEngine.Rendering.State.RenderingCamera
                ?? throw new InvalidOperationException("WebGPU.Material.DebugCameraMissing: debug primitives require a rendering camera.");
            if (_debugPrimitive == EngineMaterialSemantic.DebugPoint)
                Program.SetMatrix("InverseViewMatrix", camera.Transform.RenderMatrix);
            else if (_debugPrimitive == EngineMaterialSemantic.DebugLine)
            {
                var area = RuntimeEngine.Rendering.State.RenderArea;
                if (area.Width <= 0 || area.Height <= 0)
                    throw new InvalidOperationException("WebGPU.Material.DebugViewportMissing: debug line width requires the output dimensions.");
                Program.SetVector4("DebugViewport", new Vector4(area.Width, area.Height, 0, 0));
            }
            if (Renderer.GetBoundEngineFrameBuffer() is not null)
                throw new NotSupportedException("WebGPU.Material.DebugOutputUnsupported: overlay primitives require the post-tonemap output target.");
            return;
        }
        if (_opaquePointShadowDepth || _opaqueSpotShadowDepth)
        {
            RequirePointShadowOutput(Renderer.GetBoundEngineFrameBuffer());
            return;
        }
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
        if (Data.EngineSemantic == EngineMaterialSemanticIdentity.StandardLitColorV2)
        {
            if (target is null || !target.HasDepth || target.SampleCount != 1)
                throw new NotSupportedException("WebGPU.Material.CoverageOutputUnsupported: coverage surfaces require a single-sample depth attachment.");
            ValidateCoverageRasterState(surface);
            Program.SetVector4("StandardLitCoverage", new Vector4(
                surface.TransparencyMode == Rendering.Models.Materials.ETransparencyMode.Masked ? 1 : 0,
                surface.AlphaCutoff,
                surface.TransparencyMode == Rendering.Models.Materials.ETransparencyMode.PremultipliedAlpha ? 1 : 0, 0));
        }
        if (_litAuxiliaryPass is EStandardLitColorAuxiliaryPass.PointShadowDepth or EStandardLitColorAuxiliaryPass.SpotShadowDepth)
        {
            RequirePointShadowOutput(target);
            Program.SetVector4("StandardLitBaseColorOpacity", new Vector4(surface.BaseColor, surface.Opacity));
            return;
        }
        if (_litAuxiliaryPass == EStandardLitColorAuxiliaryPass.ShadowDepth)
        {
            if (target is null || target.HasColor || !target.HasDepth || target.SampleCount != 1)
                throw new NotSupportedException("WebGPU.Material.ShadowDepthOutputUnsupported: coverage casters require one depth-only single-sample attachment.");
            Program.SetVector4("StandardLitBaseColorOpacity", new Vector4(surface.BaseColor, surface.Opacity));
            return;
        }
        if (target is null || target.ColorFormats.Length != 1 || target.ColorFormats[0] != "rgba16float")
            throw new NotSupportedException("WebGPU.Material.OutputUnsupported: standard lit surfaces require one linear RGBA16F color attachment and explicit presentation.");
        Program.SetVector4("StandardLitBaseColorOpacity", new Vector4(surface.BaseColor, surface.Opacity));
        if (_litAuxiliaryPass == EStandardLitColorAuxiliaryPass.DepthNormal)
            return;
        Program.SetVector4("StandardLitRoughnessMetallicSpecularEmission",
            new Vector4(surface.Roughness, surface.Metallic, surface.Specular, surface.Emission));
        Renderer.PublishForwardLights(Program, _directionalShadowReceiver, _localShadowReceiver);
        Renderer.PublishAmbientOcclusion(Program);
    }

    public override void Destroy()
    {
        _apiProgram?.Dispose();
        _program?.Destroy();
        SetField(ref _apiProgram, null);
        SetField(ref _program, null);
        SetField(ref _litSurface, null);
        SetField(ref _litTextureSurface, null);
        SetField(ref _litTextureVertexProfile, null);
        SetField(ref _directionalShadowReceiver, false);
        SetField(ref _localShadowReceiver, false);
        SetField(ref _opaqueShadowDepth, false);
        SetField(ref _opaquePointShadowDepth, false);
        SetField(ref _opaqueSpotShadowDepth, false);
        SetField(ref _litAuxiliaryPass, EStandardLitColorAuxiliaryPass.None);
        SetField(ref _debugPrimitive, EngineMaterialSemantic.None);
        SetField(ref _uiSemantic, EngineMaterialSemantic.None);
    }
}
