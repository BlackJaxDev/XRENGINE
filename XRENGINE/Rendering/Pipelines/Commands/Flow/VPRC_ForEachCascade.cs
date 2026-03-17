using System;
using XREngine.Components.Lights;
using XREngine.Data.Geometry;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Iterates the active cascades of a directional light, publishing per-cascade state and executing a nested command chain.
/// </summary>
public sealed class VPRC_ForEachCascade : ViewportRenderCommand
{
    private ViewportRenderCommandContainer? _body;

    public string? DirectionalLightName { get; set; }
    public int DirectionalLightIndex { get; set; }
    public bool BindCascadeFrameBuffer { get; set; }
    public bool BindForWriting { get; set; } = true;
    public bool ClearColor { get; set; }
    public bool ClearDepth { get; set; } = true;
    public bool ClearStencil { get; set; }
    public bool PushCascadeViewportRegion { get; set; } = true;

    public string CascadeIndexVariableName { get; set; } = "CascadeIndex";
    public string CascadeCountVariableName { get; set; } = "CascadeCount";
    public string CascadeSplitVariableName { get; set; } = "CascadeSplitDistance";
    public string CascadeMatrixVariableName { get; set; } = "CascadeMatrix";
    public string CascadeCenterVariableName { get; set; } = "CascadeCenter";
    public string CascadeHalfExtentsVariableName { get; set; } = "CascadeHalfExtents";
    public string CascadeFrameBufferVariableName { get; set; } = "CascadeFrameBuffer";
    public string CascadeTextureVariableName { get; set; } = "CascadeShadowTexture";

    public ViewportRenderCommandContainer? Body
    {
        get => _body;
        set
        {
            _body = value;
            AttachPipeline(_body);
        }
    }

    protected override void Execute()
    {
        if (Body is null)
            return;

        DirectionalLightComponent? light = ResolveLight();
        if (light is null)
            return;

        int cascadeCount = light.ActiveCascadeCount;
        if (cascadeCount <= 0)
            return;

        var variables = ActivePipelineInstance.Variables;
        variables.Set(CascadeCountVariableName, cascadeCount);

        for (int cascadeIndex = 0; cascadeIndex < cascadeCount; cascadeIndex++)
        {
            variables.Set(CascadeIndexVariableName, cascadeIndex);
            variables.Set(CascadeSplitVariableName, light.GetCascadeSplit(cascadeIndex));
            variables.Set(CascadeMatrixVariableName, light.GetCascadeMatrix(cascadeIndex));
            variables.Set(CascadeCenterVariableName, light.GetCascadeCenter(cascadeIndex));
            variables.Set(CascadeHalfExtentsVariableName, light.GetCascadeHalfExtents(cascadeIndex));

            XRFrameBuffer? cascadeFbo = light.GetCascadeFrameBuffer(cascadeIndex);
            XRCamera? cascadeCamera = light.GetCascadeCamera(cascadeIndex);
            XRViewport? cascadeViewport = light.GetCascadeViewport(cascadeIndex);

            if (cascadeFbo is not null)
                variables.SetFrameBuffer(CascadeFrameBufferVariableName, cascadeFbo);
            else
                variables.Remove(CascadeFrameBufferVariableName);

            if (light.CascadedShadowMapTexture is not null)
                variables.SetTexture(CascadeTextureVariableName, light.CascadedShadowMapTexture);
            else
                variables.Remove(CascadeTextureVariableName);

            using var renderingCamera = ActivePipelineInstance.RenderState.PushRenderingCamera(cascadeCamera);
            using var renderArea = PushCascadeViewportRegion && cascadeViewport is not null
                ? ActivePipelineInstance.RenderState.PushRenderArea(cascadeViewport.Region)
                : default(StateObject);

            if (BindCascadeFrameBuffer && cascadeFbo is not null)
            {
                if (BindForWriting)
                    cascadeFbo.BindForWriting();
                else
                    cascadeFbo.BindForReading();

                try
                {
                    if (ClearColor || ClearDepth || ClearStencil)
                        Engine.Rendering.State.ClearByBoundFBO(ClearColor, ClearDepth, ClearStencil);

                    Body.Execute();
                }
                finally
                {
                    if (BindForWriting)
                        cascadeFbo.UnbindFromWriting();
                    else
                        cascadeFbo.UnbindFromReading();
                }
            }
            else
            {
                Body.Execute();
            }
        }
    }

    internal override void OnAttachedToContainer()
    {
        base.OnAttachedToContainer();
        AttachPipeline(_body);
    }

    internal override void OnParentPipelineAssigned()
    {
        base.OnParentPipelineAssigned();
        AttachPipeline(_body);
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);
        Body?.BuildRenderPassMetadata(context);
    }

    private DirectionalLightComponent? ResolveLight()
    {
        var lights = ActivePipelineInstance.RenderState.WindowViewport?.World?.Lights?.DynamicDirectionalLights;
        if (lights is null || lights.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(DirectionalLightName))
        {
            foreach (DirectionalLightComponent light in lights)
            {
                if (string.Equals(light.Name, DirectionalLightName, StringComparison.OrdinalIgnoreCase))
                    return light;
            }
        }

        return DirectionalLightIndex >= 0 && DirectionalLightIndex < lights.Count
            ? lights[DirectionalLightIndex]
            : null;
    }

    private void AttachPipeline(ViewportRenderCommandContainer? container)
    {
        var pipeline = CommandContainer?.ParentPipeline;
        if (container is not null && pipeline is not null && !ReferenceEquals(container.ParentPipeline, pipeline))
            container.ParentPipeline = pipeline;
    }
}