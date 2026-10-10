using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Components.Scene.Mesh;

public partial class SkyboxComponent
{
    /// <summary>Identifies the exact cooked sky behavior without loading desktop shader sources.</summary>
    public EngineMaterialSemanticIdentity GetWebGpuSemantic() => Mode switch
    {
        ESkyboxMode.Gradient or ESkyboxMode.SolidColor => EngineMaterialSemanticIdentity.SkyboxGradientV1,
        ESkyboxMode.DynamicProcedural => EngineMaterialSemanticIdentity.SkyboxDynamicProceduralV1,
        ESkyboxMode.Texture => Projection switch
        {
            ESkyboxProjection.Equirectangular => EngineMaterialSemanticIdentity.SkyboxEquirectangularV1,
            ESkyboxProjection.Octahedral => EngineMaterialSemanticIdentity.SkyboxOctahedralV1,
            ESkyboxProjection.Cubemap => EngineMaterialSemanticIdentity.SkyboxCubemapV1,
            ESkyboxProjection.CubemapArray => throw new NotSupportedException(
                "WebGPU.Skybox.CubemapArrayUnsupported: layered sky selection requires an explicit cube-array texture and cooked material profile."),
            _ => throw new NotSupportedException("WebGPU.Skybox.ProjectionUnsupported: the authored projection has no cooked sky variant."),
        },
        _ => throw new NotSupportedException("WebGPU.Skybox.ModeUnsupported: the authored sky mode has no cooked variant."),
    };

    /// <summary>Checks authored sky requirements during publication and again before a live draw.</summary>
    public void ValidateWebGpuProfile()
    {
        _ = GetWebGpuSemantic();
        if (!float.IsFinite(Intensity) || Intensity < 0 || !float.IsFinite(Rotation))
            throw new NotSupportedException("WebGPU.Skybox.ParametersInvalid: intensity must be finite and nonnegative and rotation must be finite.");
        if (Mode is ESkyboxMode.Gradient or ESkyboxMode.SolidColor)
        {
            if (!Finite(TopColor) || Mode == ESkyboxMode.Gradient && !Finite(BottomColor))
                throw new NotSupportedException("WebGPU.Skybox.ParametersInvalid: sky colors must contain finite linear radiance values.");
        }
        else if (Mode == ESkyboxMode.DynamicProcedural)
        {
            if (!float.IsFinite(TimeOfDay) || !float.IsFinite(DayLengthSeconds) ||
                !float.IsFinite(CloudCoverage) || !float.IsFinite(CloudScale) ||
                !float.IsFinite(CloudSpeed) || !float.IsFinite(CloudSharpness) ||
                !float.IsFinite(StarIntensity) || !float.IsFinite(HorizonHaze) ||
                !float.IsFinite(SunDiscSize) || !float.IsFinite(MoonDiscSize))
                throw new NotSupportedException("WebGPU.Skybox.ParametersInvalid: procedural sky parameters must be finite.");
        }
        else
        {
            if (Texture is null)
                throw new NotSupportedException("WebGPU.Skybox.TextureMissing: texture mode requires its authored environment image.");
            bool matches = Projection == ESkyboxProjection.Cubemap
                ? Texture is XRTextureCube : Texture is XRTexture2D { MultiSampleCount: 1 };
            if (!matches)
                throw new NotSupportedException("WebGPU.Skybox.TextureTopologyMismatch: panorama and octahedral projections require a single-sample 2D texture; cubemap projection requires six cube faces.");
            ESizedInternalFormat format = Texture switch
            {
                XRTexture2D image => image.SizedInternalFormat,
                XRTextureCube cube => cube.SizedInternalFormat,
                _ => throw new NotSupportedException("WebGPU.Skybox.TextureTopologyMismatch: sky images require an admitted 2D or cube texture."),
            };
            if (format is not (ESizedInternalFormat.Rgba8 or ESizedInternalFormat.Srgb8Alpha8 or ESizedInternalFormat.Rgba16f))
                throw new NotSupportedException("WebGPU.Skybox.TextureFormatUnsupported: sky images require exact RGBA8, sRGB RGBA8 or linear RGBA16F storage.");
            if (Texture.AutoGenerateMipmaps)
                throw new NotSupportedException("WebGPU.Skybox.MipGenerationUnsupported: supply authored mip levels; runtime sky mip generation is not available.");
        }
    }

    private static bool Finite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
