using System.Numerics;

namespace XREngine.Rendering;

/// <summary>One immutable lighting record and its capture-owned GPU shadow image.</summary>
public sealed class SceneCaptureShadowSnapshot
{
    internal SceneCaptureShadowSnapshot(object light, int lightIndex, XRTexture texture,
        Matrix4x4 viewProjection, AdvancedShadowRecord record, int textureGenerationHandle, ulong productionTicket)
    {
        Light = light; LightIndex = lightIndex; Texture = texture; ViewProjection = viewProjection;
        Record = record; TextureGenerationHandle = textureGenerationHandle; ProductionTicket = productionTicket;
    }

    internal object Light { get; }
    internal int LightIndex { get; }
    internal XRTexture Texture { get; }
    internal Matrix4x4 ViewProjection { get; }
    internal AdvancedShadowRecord Record { get; }
    internal int TextureGenerationHandle { get; }
    internal ulong ProductionTicket { get; }
}
