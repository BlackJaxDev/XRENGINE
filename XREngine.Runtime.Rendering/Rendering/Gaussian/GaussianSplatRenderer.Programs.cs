using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Gaussian;

// Shaders, programs, and the quad material of GaussianSplatRenderer.
public sealed partial class GaussianSplatRenderer
{
    /// <summary>Depth key compute shader, relative to the engine shader folder.</summary>
    public const string KeysShaderPath = "Gaussian/GaussianSplatKeys.comp";
    /// <summary>Quad vertex shader, relative to the engine shader folder.</summary>
    public const string QuadVertexShaderPath = "Gaussian/GaussianSplatQuad.vs";
    /// <summary>Quad fragment shader, relative to the engine shader folder.</summary>
    public const string QuadFragmentShaderPath = "Gaussian/GaussianSplatQuad.fs";

    internal const string CapacityUniform = "SplatCapacity";
    internal const string ModelViewUniform = "ModelViewMatrix";
    internal const string SplatScaleUniform = "SplatScale";
    internal const string AllSlotsWrittenUniform = "AllSlotsWritten";

    private static readonly uint SplatStrideBytes = (uint)Unsafe.SizeOf<GaussianSplatGpuSplat>();

    private XRRenderProgram? _keysProgram;

    /// <summary>
    /// Creates the render options of the quad material: premultiplied "over" blending (One,
    /// OneMinusSrcAlpha), a Lequal depth test without depth writes, and no face culling.
    /// </summary>
    public static RenderingParameters CreateRenderOptions()
        => new()
        {
            CullMode = ECullMode.None,
            DepthTest = new DepthTest
            {
                Enabled = ERenderParamUsage.Enabled,
                Function = EComparison.Lequal,
                UpdateDepth = false,
            },
            BlendModeAllDrawBuffers = new BlendMode
            {
                Enabled = ERenderParamUsage.Enabled,
                RgbEquation = EBlendEquationMode.FuncAdd,
                AlphaEquation = EBlendEquationMode.FuncAdd,
                RgbSrcFactor = EBlendingFactor.One,
                AlphaSrcFactor = EBlendingFactor.One,
                RgbDstFactor = EBlendingFactor.OneMinusSrcAlpha,
                AlphaDstFactor = EBlendingFactor.OneMinusSrcAlpha,
            },
            RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.ViewportDimensions,
        };

    /// <summary>Creates the quad material for the TransparentForward pass.</summary>
    public static XRMaterial CreateQuadMaterial()
        => new(ShaderHelper.GaussianSplatQuadVertex(), ShaderHelper.GaussianSplatQuadFragment())
        {
            Name = "GaussianSplatQuad",
            RenderPass = (int)EDefaultRenderPass.TransparentForward,
            RenderOptions = CreateRenderOptions(),
        };

    /// <summary>
    /// Creates a quad mesh renderer for <see cref="Draw"/>. The quad corners have X and Y from -1 to 1.
    /// The caller owns the renderer, its mesh, and its material.
    /// </summary>
    /// <param name="material">Material to use. It must use the gaussian splat quad shaders. Null creates one with <see cref="CreateQuadMaterial"/>.</param>
    public static XRMeshRenderer CreateQuadRenderer(XRMaterial? material = null)
    {
        // Positions only: the quad vertex shader reads no other attribute.
        XRMesh mesh = XRMesh.Create(new VertexQuad(
            new Vertex(new Vector3(-1f, -1f, 0f)),
            new Vertex(new Vector3(1f, -1f, 0f)),
            new Vertex(new Vector3(1f, 1f, 0f)),
            new Vertex(new Vector3(-1f, 1f, 0f))));
        mesh.SupportsBillboarding = false;
        XRMeshRenderer renderer = new(mesh, material ?? CreateQuadMaterial())
        {
            GenerateAsync = false,
            GenerationPriority = EMeshGenerationPriority.RenderPipeline,
        };
        // The material has its own vertex shader that reads the splat buffers.
        renderer.GetDefaultVersion().AllowShaderPipelines = false;
        return renderer;
    }

    /// <summary>Creates the depth key program on the first call and polls its link state.</summary>
    private bool EnsureKeysProgramReady()
    {
        _keysProgram ??= new XRRenderProgram(true, false, ShaderHelper.GaussianSplatKeysCompute())
        {
            Name = "GaussianSplatKeys",
        };

        if (_keysProgram.IsLinked)
            return true;
        _keysProgram.Link();
        return _keysProgram.IsLinked;
    }

    private void DisposePrograms()
    {
        _keysProgram?.Destroy();
        _keysProgram = null;
    }
}
