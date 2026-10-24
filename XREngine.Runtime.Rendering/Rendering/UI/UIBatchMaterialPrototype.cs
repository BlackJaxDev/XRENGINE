using System.IO;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.UI;

/// <summary>
/// Immutable shader and raster definition for a shared UI primitive. Each retained
/// draw cohort creates its own material and render options from this definition;
/// those mutable objects must never be shared between deferred draws.
/// </summary>
internal sealed class UIBatchMaterialPrototype
{
    private readonly (string Path, EShaderType Type)[] _shaders;
    private readonly bool _desktopDepthWrite;

    private UIBatchMaterialPrototype(bool desktopDepthWrite,
        params (string Path, EShaderType Type)[] shaders)
    {
        _desktopDepthWrite = desktopDepthWrite;
        _shaders = shaders;
    }

    internal static readonly UIBatchMaterialPrototype Solid = new(true,
        ("UIQuadBatched.vs", EShaderType.Vertex),
        ("UIQuadBatched.fs", EShaderType.Fragment));

    internal static readonly UIBatchMaterialPrototype Border = new(true,
        ("UIBorderBatched.vs", EShaderType.Vertex),
        ("UIBorderBatched.fs", EShaderType.Fragment));

    internal static readonly UIBatchMaterialPrototype Image = new(false,
        ("UIImageBatched.vs", EShaderType.Vertex),
        ("UIImageBatched.fs", EShaderType.Fragment));

    internal static readonly UIBatchMaterialPrototype Backdrop = new(false,
        ("UIImageBatched.vs", EShaderType.Vertex),
        ("UIBackdropBatched.fs", EShaderType.Fragment));

    internal static readonly UIBatchMaterialPrototype Text = new(true,
        ("UITextBatched.vs", EShaderType.Vertex),
        ("UITextBatchedStereoMV2.vs", EShaderType.Vertex),
        ("UITextBatchedStereoNV.vs", EShaderType.Vertex),
        ("UITextBatched.fs", EShaderType.Fragment));

    internal XRShader[] CreateDesktopShaders()
    {
        XRShader[] shaders = new XRShader[_shaders.Length];
        for (int index = 0; index < shaders.Length; index++)
        {
            (string path, EShaderType type) = _shaders[index];
            shaders[index] = XRShader.EngineShader(Path.Combine("Common", path), type);
        }
        return shaders;
    }

    internal RenderingParameters CreateRenderOptions(bool webGpu)
        => new()
        {
            CullMode = ECullMode.None,
            DepthTest = new DepthTest
            {
                Enabled = ERenderParamUsage.Disabled,
                Function = EComparison.Always,
                UpdateDepth = _desktopDepthWrite && !webGpu,
            },
            StencilTest = new StencilTest { Enabled = ERenderParamUsage.Disabled },
            BlendModeAllDrawBuffers = BlendMode.EnabledTransparent(),
        };
}
