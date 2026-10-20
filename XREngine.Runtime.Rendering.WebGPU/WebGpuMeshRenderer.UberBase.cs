namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuMeshRenderer
{
    private static void ValidateUberBaseDrawBindings(XRMeshRenderer renderer, XRMaterial material,
        XRRenderPipelineInstance.RenderingState? state, XRRenderPipelineInstance.PipelineVariableStore? variables)
    {
        if (material.EngineSemantic != EngineMaterialSemanticIdentity.UberBaseV1) return;
        XRMaterial source = material.StandardLitColorSourceMaterial ?? material;
        if (source.CookedUberBaseProfile is null || renderer.HasSettingUniformsHandlers || renderer.HasRenderDataPreparation ||
            renderer.BindingPublishers.Count != 0 || renderer.Material?.HasSettingVertexUniformHandlers == true ||
            material.HasSettingVertexUniformHandlers || material.HasSettingShadowUniformHandlers ||
            material.HasSettingUniformsHandlers && !material.HasOnlyStandardSurfaceUniformHandlers || material.BindingPublishers.Count != 0)
            throw Unsupported("canonical Uber replay requires its exact source profile without unmodeled renderer/material callbacks or binding publishers");
        if (state?.HasActiveScopedBindings == true)
            throw Unsupported("canonical Uber replay cannot admit scoped writes without an exact source binding contract");
        if (variables?.HasUniformValues != true) return;
        foreach (var member in UberBaseParameterSchema.Members)
            if (HasOutlineUniform(variables, member.ProviderName))
                throw Unsupported("a pipeline variable replaces a prepared canonical Uber input");
        if (HasOutlineUniform(variables, "ViewProjection") || HasOutlineUniform(variables, "CameraPosition") ||
            HasOutlineUniform(variables, "ModelMatrix") || HasOutlineUniform(variables, "NormalMatrix") || HasOutlineUniform(variables, "RenderTime"))
            throw Unsupported("a pipeline variable replaces a coupled canonical Uber view or transform input");
    }
}
