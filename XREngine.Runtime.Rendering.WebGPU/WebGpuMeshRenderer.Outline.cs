using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuMeshRenderer
{
    internal static void ValidateOutlineDrawBindings(XRMeshRenderer renderer, XRMaterial material,
        XRRenderPipelineInstance.RenderingState? state, XRRenderPipelineInstance.PipelineVariableStore? variables)
    {
        if (material.EngineSemantic != EngineMaterialSemanticIdentity.UberOutlineV1) return;
        if (material.GetType() != typeof(XRMaterial) || renderer.HasSettingUniformsHandlers || renderer.HasRenderDataPreparation ||
            renderer.BindingPublishers.Count != 0 || renderer.Material?.HasSettingVertexUniformHandlers == true ||
            material.HasSettingVertexUniformHandlers || material.HasSettingShadowUniformHandlers ||
            material.HasSettingUniformsHandlers && !material.HasOnlyStandardSurfaceUniformHandlers || material.BindingPublishers.Count != 0)
            throw Unsupported("the modeled outline requires canonical renderer/material uniform behavior without custom callbacks, render-data preparation, or binding publishers");
        if (state?.HasActiveScopedBindings == true)
            throw Unsupported("the modeled outline cannot admit scoped program, resource or shader-global writes without an exact binding contract");
        if (material.UberOutlineSourceMaterial is not { CookedOutlineProfile: { } profile } source ||
            !ReferenceEquals(material.Parameters, source.Parameters) || profile.TextureBindings is null || material.Textures.Count != profile.TextureBindings.Length)
            throw Unsupported("the modeled outline companion no longer borrows its exact admitted source parameters and compact texture table");
        for (int index = 0; index < material.Textures.Count; index++)
            if (profile.TextureBindings[index] is not { } binding || !ReferenceEquals(material.Textures[index], binding.Texture))
                throw Unsupported("the modeled outline companion texture table differs from its admitted source images");
        if (variables?.HasUniformValues != true) return;
        foreach (var member in UberOutlineProgramContract.SourceMembers)
            if (HasOutlineUniform(variables, member.ProviderName))
                throw Unsupported("a pipeline variable replaces a modeled outline source uniform");
        if (HasOutlineUniform(variables, "ViewProjection") || HasOutlineUniform(variables, "CameraPosition") ||
            HasOutlineUniform(variables, "ModelMatrix") || HasOutlineUniform(variables, "NormalMatrix") ||
            HasOutlineUniform(variables, "u_ScreenParams") || HasOutlineUniform(variables, "u_Time") || HasOutlineUniform(variables, "OutlineFeatures"))
            throw Unsupported("a pipeline variable replaces a coupled modeled outline view or transform uniform");
    }

    private static bool HasOutlineUniform(XRRenderPipelineInstance.PipelineVariableStore values, string name)
        => values.BoolVariables.ContainsKey(name) || values.IntVariables.ContainsKey(name) || values.UIntVariables.ContainsKey(name) ||
           values.FloatVariables.ContainsKey(name) || values.Vector2Variables.ContainsKey(name) || values.Vector3Variables.ContainsKey(name) ||
           values.Vector4Variables.ContainsKey(name) || values.Matrix4Variables.ContainsKey(name);

    private static bool HasOpaqueOutlineBlend(XRMaterial material, in WebGpuRasterState state)
        => material.EngineSemantic == EngineMaterialSemanticIdentity.UberOutlineV1 &&
           state.RgbEquation == EBlendEquationMode.FuncAdd &&
           state.SourceRgb == EBlendingFactor.One && state.DestinationRgb == EBlendingFactor.Zero &&
           (state.AlphaEquation is EBlendEquationMode.Min or EBlendEquationMode.Max ||
            state.AlphaEquation == EBlendEquationMode.FuncAdd &&
            state.SourceAlpha == EBlendingFactor.One && state.DestinationAlpha == EBlendingFactor.Zero);
}
