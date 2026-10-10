using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Shaders.Parameters;

namespace XREngine.Rendering;

/// <summary>Admits the built-in, texture-alpha deferred decal without custom surface behavior.</summary>
public static class DeferredDecalMaterialContract
{
    /// <summary>Reads only the source-free cooked decal carrier used by the browser renderer.</summary>
    public static bool TryRead(XRMaterial material, out XRTexture2D? image, out string reason)
    {
        image = null;
        if (material.GetType() != typeof(PublishedDeferredDecalMaterial) || material.Shaders.Count != 0 ||
            material.Parameters.Length != 0)
        {
            reason = "the runtime decal requires the source-free PublishedDeferredDecalMaterial carrier";
            return false;
        }
        return TryReadShape(material, out image, out reason);
    }

    /// <summary>Checks the default desktop decal's material state independently of shader provenance.</summary>
    public static bool TryReadShape(XRMaterial material, out XRTexture2D? image, out string reason)
    {
        image = null;
        reason = "the decal requires exactly four unassigned GBuffer slots and one static XRTexture2D image in slot four";
        if (material.Textures.Count != 5 || material.Textures[0] is not null || material.Textures[1] is not null ||
            material.Textures[2] is not null || material.Textures[3] is not null ||
            material.Textures[4] is not XRTexture2D texture || texture.GetType() != typeof(XRTexture2D))
            return false;

        reason = "the decal image in slot four must resolve to the canonical Texture4 sampler";
        if (texture.ResolveSamplerName(4) != "Texture4")
            return false;

        reason = "the decal requires only the default projection uniforms and no custom surface bindings or extensions";
        if (!HasDefaultProjectionParameters(material.Parameters) || material.SurfaceTextureBindings.Length != 0 ||
            material.EngineSemantic != EngineMaterialSemanticIdentity.None ||
            material.GetEffectiveTransparencyMode() != ETransparencyMode.Opaque ||
            material.TransparentTechniqueOverride is not null || material.AlphaCutoff != 0.5f ||
            material.BillboardMode != EMeshBillboardMode.None || material.TransparentSortPriority != 0 ||
            material.EmissiveColor.HasValue || material.EmissionStrength.HasValue ||
            material.Transmission != 0 || material.TransmissionColor != Vector3.One || material.NormalScale != 1 ||
            material.HasSettingUniformsHandlers || material.HasSettingVertexUniformHandlers ||
            material.HasSettingShadowUniformHandlers || material.BindingPublishers.Count != 0 ||
            material.PassSet.Passes.Length != 0 || material.PassSet.DisabledSourcePasses.Length != 0 ||
            material.PassSet.SourceRenderQueue != -1 || material.PassSet.QueuePriority != 0 ||
            material.PassSet.ForwardAddRenderOptions is not null ||
            material.PassSet.ForwardAddPolicy != EMaterialForwardAddPolicy.FoldedIntoForwardPlusBase ||
            material.UberAuthoredState.Features.Length != 0 || material.UberAuthoredState.Properties.Length != 0 ||
            !material.RequestedUberVariant.IsEmpty)
            return false;

        reason = "the decal requires the default DeferredDecals pass, front-face culling and disabled depth test";
        RenderingParameters options = material.RenderOptions;
        DepthTest? depth = options.DepthTest;
        StencilTest? stencil = options.StencilTest;
        if (material.RenderPass != (int)EDefaultRenderPass.DeferredDecals ||
            options.GetType() != typeof(RenderingParameters) ||
            options.CullMode != ECullMode.Front || options.RequiredEngineUniforms != EUniformRequirements.Camera ||
            depth is null || depth.GetType() != typeof(DepthTest) ||
            depth.Enabled != ERenderParamUsage.Disabled || !depth.UpdateDepth ||
            depth.Function != EComparison.Lequal || options.Winding != EWinding.CounterClockwise ||
            !options.WriteRed || !options.WriteGreen || !options.WriteBlue || !options.WriteAlpha ||
            options.AlphaToCoverage != ERenderParamUsage.Disabled || options.HasBlending ||
            options.BlendModeAllDrawBuffers is not null || options.BlendModesPerDrawBuffer is not null ||
            stencil is null || stencil.GetType() != typeof(StencilTest) ||
            stencil.Enabled != ERenderParamUsage.Unchanged ||
            !IsDefaultStencilFace(stencil.FrontFace) || !IsDefaultStencilFace(stencil.BackFace) ||
            options.ExcludeFromGpuIndirect || options.ExcludeFromCpuOcclusion ||
            options.TextureArrayPolicy != EMaterialTextureArrayPolicy.ArbitraryMaterialTextures ||
            options.MissingTextureFallback != EMissingTextureFallback.DiagnosticMagenta)
            return false;

        if (!AdvancedGpuResourceSourceEncoder.TryEncode(texture, EAdvancedResourceFallback.White,
                out AdvancedGpuResourceBindingSource source, out _, out string imageReason) ||
            !WebGpuAdvancedSceneAdmission.TryInspectTexture(in source, allowDepthComparison: false, out imageReason) ||
            source.TextureRecord.Dimension != EAdvancedTextureDimension.Texture2D)
        {
            reason = imageReason.Length != 0 ? imageReason : "the decal image requires an ordinary native 2D sampled texture";
            return false;
        }
        try
        {
            _ = PublishedStandardLitTextureSettings.Capture(texture);
        }
        catch (InvalidDataException error)
        {
            reason = error.Message;
            return false;
        }
        image = texture;
        reason = string.Empty;
        return true;
    }

    private static bool IsDefaultStencilFace(StencilTestFace? face)
        => face is not null && face.GetType() == typeof(StencilTestFace) && face.BothFailOp == EStencilOp.Keep &&
            face.StencilPassDepthFailOp == EStencilOp.Keep && face.BothPassOp == EStencilOp.Keep &&
            face.Function == default && face.Reference == 0 && face.ReadMask == 0 && face.WriteMask == 0;

    private static bool HasDefaultProjectionParameters(ShaderVar[] parameters)
        => parameters.Length == 0 || parameters.Length == 2 &&
            (IsWorldMatrix(parameters[0]) && IsHalfScale(parameters[1]) ||
             IsHalfScale(parameters[0]) && IsWorldMatrix(parameters[1]));

    private static bool IsWorldMatrix(ShaderVar? parameter)
        => parameter is ShaderMat4 matrix && matrix.GetType() == typeof(ShaderMat4) &&
            matrix.Name == "BoxWorldMatrix";

    private static bool IsHalfScale(ShaderVar? parameter)
        => parameter is ShaderVector3 halfScale && halfScale.GetType() == typeof(ShaderVector3) &&
            halfScale.Name == "BoxHalfScale";
}
