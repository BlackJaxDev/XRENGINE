using System.IO;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;
using static XREngine.Rendering.XRRenderProgram;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    /// <summary>
    /// Copies native highlight flags and logical draw identity before late draws update them.
    /// </summary>
    private void AppendEditorHighlightSeed(ViewportRenderCommandContainer commands)
    {
        if (EditorHighlightModule is null)
            return;

        if (Shaders.Compilation.WebPipelineRasterProgram.IsActive)
            throw new NotSupportedException("The editor highlight mask seed is unavailable on this backend.");

        XRShader shader = XRShader.EngineShader(
            Path.Combine("Advanced", "EditorHighlight", "SeedMask.comp"), EShaderType.Compute);
        if (Stereo)
            shader = ShaderHelper.CreateDefinedShaderVariant(shader, "XR_ADV_VISIBILITY_ARRAY")
                ?? throw new InvalidOperationException("The stereo editor highlight seed shader is unavailable.");

        commands.Add<VPRC_MemoryBarrier>().Mask =
            EMemoryBarrierMask.Framebuffer | EMemoryBarrierMask.ShaderImageAccess;
        VPRC_SeedEditorHighlightMask seed = commands.Add<VPRC_SeedEditorHighlightMask>();
        seed.ComputeProgram = new XRRenderProgram(true, false, shader)
        {
            Name = "EditorHighlightSeed"
        };
        seed.X = () => (GetDesiredFBOSizeInternal().x + 7u) / 8u;
        seed.Y = () => (GetDesiredFBOSizeInternal().y + 7u) / 8u;
        seed.Z = () => Stereo ? 2u : 1u;
        seed.Textures =
        [
            new ComputeTextureBinding(0u,
                () => GetTexture<XRTexture>(AdvancedVisibilityResourceNames.Metadata)
                    ?? throw new InvalidOperationException("Visibility metadata is unavailable for the editor highlight seed."),
                0, Stereo ? 0 : null, EImageAccess.ReadOnly, EImageFormat.R32UI),
            new ComputeTextureBinding(1u,
                () => GetTexture<XRTexture>(EditorHighlightMaskTextureName)
                    ?? throw new InvalidOperationException("The editor highlight mask is unavailable."),
                0, Stereo ? 0 : null, EImageAccess.WriteOnly, EImageFormat.RGBA8),
            new ComputeTextureBinding(2u,
                () => GetTexture<XRTexture>(AdvancedVisibilityResourceNames.Identity)
                    ?? throw new InvalidOperationException("Visibility identity is unavailable for the editor picking seed."),
                0, Stereo ? 0 : null, EImageAccess.ReadOnly, EImageFormat.RG32UI),
            new ComputeTextureBinding(3u,
                () => GetTexture<XRTexture>(EditorPickingIdentityTextureName)
                    ?? throw new InvalidOperationException("The editor picking identity output is unavailable."),
                0, Stereo ? 0 : null, EImageAccess.WriteOnly, EImageFormat.RGBA16),
        ];
    }
}
