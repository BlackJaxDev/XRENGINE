using System.Numerics;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Textures;
using XREngine.Scene;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanCommandRuntime
{
    internal float MaterialUniformUpdateDelta => RuntimeEngine.Time.Timer.Update.Delta;
    internal float MaterialUniformSeconds => RuntimeEngine.ElapsedTime;
    internal float MaterialUniformRenderDelta => RuntimeEngine.Time.Timer.Render.Delta;

    internal void SetEngineUniforms(XRRenderProgram program, XRCamera camera)
    {
        if (program is null)
            return;

        if (RuntimeEngine.Rendering.State.IsStereoPass)
        {
            PassCameraUniforms(program, camera, EEngineUniform.LeftEyeViewMatrix, EEngineUniform.LeftEyeInverseViewMatrix, EEngineUniform.LeftEyeInverseProjMatrix, EEngineUniform.LeftEyeProjMatrix, EEngineUniform.LeftEyeViewProjectionMatrix);
            PassCameraUniforms(program, RuntimeEngine.Rendering.State.RenderingStereoRightEyeCamera, EEngineUniform.RightEyeViewMatrix, EEngineUniform.RightEyeInverseViewMatrix, EEngineUniform.RightEyeInverseProjMatrix, EEngineUniform.RightEyeProjMatrix, EEngineUniform.RightEyeViewProjectionMatrix);
            return;
        }

        PassCameraUniforms(program, camera, EEngineUniform.ViewMatrix, EEngineUniform.InverseViewMatrix, EEngineUniform.InverseProjMatrix, EEngineUniform.ProjMatrix, EEngineUniform.ViewProjectionMatrix);
    }

    internal VulkanFixedFunctionStateSnapshot CaptureFixedFunctionState()
        => StateTracker.CaptureFixedFunctionState();

    internal void RestoreFixedFunctionState(in VulkanFixedFunctionStateSnapshot snapshot)
        => StateTracker.RestoreFixedFunctionState(in snapshot);

    internal ComputeDispatchSnapshot? GetForwardLightingBindingSnapshotForArtifact(
        Lights3DCollection lights,
        XRRenderProgram programData,
        VkRenderProgram backendProgram)
    {
        using VkRenderProgram.BindingUpdateScope capture = backendProgram.BeginBindingUpdate();
        backendProgram.ClearBindings();
        lights.SetForwardLightingUniforms(programData);
        return backendProgram.CaptureComputeSnapshot();
    }

    internal void SetMaterialUniforms(
        XRMaterial material,
        XRRenderProgram program,
        VkRenderProgram? backendProgram,
        in LayeredShadowUniformState shadowState)
    {
        if (material is null || program is null)
            return;

        if (!shadowState.IsShadowPass)
        {
            VulkanMeshRenderingConventions.SetMaterialStaticUniforms(material, program);
            SetMaterialRuntimeUniforms(material, program, backendProgram, in shadowState);
            return;
        }

        if (material.RenderOptions is not null)
            ApplyRenderParameters(material.RenderOptions);
        XRMaterialBase uniformSource = material.ShadowBindingSourceMaterial ?? material;
        foreach (ShaderVar parameter in uniformSource.Parameters)
            parameter.SetUniform(program, forceUpdate: true);
        SetTextureUniforms(program, uniformSource);
        PublishRequiredEngineBindings(material, program, backendProgram, includeDrawOwnedFrameUniforms: false);
    }

    internal void SetMaterialRuntimeUniforms(
        XRMaterial material,
        XRRenderProgram program,
        VkRenderProgram? backendProgram,
        in LayeredShadowUniformState shadowState)
    {
        _ = shadowState;
        if (material.RenderOptions is not null)
            ApplyRenderParameters(material.RenderOptions);
        SetTextureUniforms(program, material);
        PublishRequiredEngineBindings(material, program, backendProgram, includeDrawOwnedFrameUniforms: true);
        if (backendProgram is null)
        {
            material.OnSettingUniforms(program);
            RuntimeEngine.Rendering.State.RenderingPipelineState?.ApplyScopedProgramBindings(program);
            return;
        }

        using VkRenderProgram.MutableLegacyBindingPublicationScope publication = backendProgram.BeginMutableLegacyBindingPublication();
        material.OnSettingUniforms(program);
        RuntimeEngine.Rendering.State.RenderingPipelineState?.ApplyScopedProgramBindings(program);
    }

    internal void ApplyRenderParameters(RenderingParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        StateTracker.SetColorMask(parameters.WriteRed, parameters.WriteGreen, parameters.WriteBlue, parameters.WriteAlpha);
        StateTracker.SetCullMode(VulkanMeshRenderingConventions.ToVulkanCullMode(VulkanMeshRenderingConventions.ResolveCullMode(parameters.CullMode)));
        StateTracker.SetFrontFace(VulkanMeshRenderingConventions.ToVulkanFrontFace(VulkanMeshRenderingConventions.ResolveWinding(parameters.Winding)));
        StateTracker.SetAlphaToCoverageEnabled(parameters.AlphaToCoverage == ERenderParamUsage.Enabled);
        DepthTest depth = parameters.DepthTest;
        if (depth.Enabled == ERenderParamUsage.Enabled)
        {
            StateTracker.SetDepthTestEnabled(true);
            StateTracker.SetDepthWriteEnabled(depth.UpdateDepth);
            StateTracker.SetDepthCompare(VulkanMeshRenderingConventions.ToVulkanCompareOp(RuntimeEngine.Rendering.State.MapDepthComparison(depth.Function)));
        }
        else if (depth.Enabled == ERenderParamUsage.Disabled)
        {
            StateTracker.SetDepthTestEnabled(false);
            StateTracker.SetDepthWriteEnabled(false);
        }

        BlendMode? blend = VulkanMeshRenderingConventions.ResolveBlendMode(parameters);
        StateTracker.SetBlendState(
            blend?.Enabled == ERenderParamUsage.Enabled,
            blend is null ? BlendOp.Add : VulkanMeshRenderingConventions.ToVulkanBlendOp(blend.RgbEquation),
            blend is null ? BlendOp.Add : VulkanMeshRenderingConventions.ToVulkanBlendOp(blend.AlphaEquation),
            blend is null ? BlendFactor.One : VulkanMeshRenderingConventions.ToVulkanBlendFactor(blend.RgbSrcFactor),
            blend is null ? BlendFactor.Zero : VulkanMeshRenderingConventions.ToVulkanBlendFactor(blend.RgbDstFactor),
            blend is null ? BlendFactor.One : VulkanMeshRenderingConventions.ToVulkanBlendFactor(blend.AlphaSrcFactor),
            blend is null ? BlendFactor.Zero : VulkanMeshRenderingConventions.ToVulkanBlendFactor(blend.AlphaDstFactor));
    }

    private void PublishRequiredEngineBindings(XRMaterial material, XRRenderProgram program, VkRenderProgram? backendProgram, bool includeDrawOwnedFrameUniforms)
    {
        EUniformRequirements requirements = (material.RenderOptions?.RequiredEngineUniforms ?? EUniformRequirements.None) | program.GetActiveEngineUniformRequirements();
        if (includeDrawOwnedFrameUniforms && (requirements & EUniformRequirements.Camera) != 0)
        {
            RuntimeEngine.Rendering.State.RenderingCamera?.SetUniforms(program, true);
            RuntimeEngine.Rendering.State.RenderingStereoRightEyeCamera?.SetUniforms(program, false);
        }
        if ((requirements & EUniformRequirements.Lights) != 0 && RuntimeEngine.Rendering.State.RenderingWorld?.Lights is { } lights)
        {
            if (backendProgram is null)
                lights.SetForwardLightingUniforms(program);
            else if (GetForwardLightingBindingSnapshotForArtifact(lights, program, backendProgram) is { } snapshot)
                backendProgram.MergeBindingSnapshot(snapshot);
        }
        else if ((requirements & EUniformRequirements.AmbientOcclusion) != 0)
            Lights3DCollection.SetForwardAmbientOcclusionUniforms(program);
        if (includeDrawOwnedFrameUniforms && (requirements & EUniformRequirements.RenderTime) != 0)
        {
            program.Uniform(EEngineUniform.RenderTime.ToStringFast(), MaterialUniformSeconds);
            program.Uniform(EEngineUniform.EngineTime.ToStringFast(), MaterialUniformSeconds);
            program.Uniform(EEngineUniform.DeltaTime.ToStringFast(), MaterialUniformRenderDelta);
        }
    }

    private static void SetTextureUniforms(XRRenderProgram program, XRMaterialBase material)
    {
        for (int index = 0; index < material.Textures.Count; index++)
        {
            if (material.Textures[index] is not { } texture)
                continue;
            string samplerName = texture.ResolveSamplerName(index, null);
            program.Sampler(samplerName, texture, index);
        }
    }

    private static void PassCameraUniforms(XRRenderProgram program, XRCamera? camera, EEngineUniform view, EEngineUniform inverseView, EEngineUniform inverseProjection, EEngineUniform projection, EEngineUniform viewProjection)
    {
        Matrix4x4 viewMatrix = camera?.Transform.InverseRenderMatrix ?? Matrix4x4.Identity;
        Matrix4x4 inverseViewMatrix = camera?.Transform.RenderMatrix ?? Matrix4x4.Identity;
        bool unjittered = RuntimeEngine.Rendering.State.RenderingPipelineState?.UseUnjitteredProjection ?? false;
        Matrix4x4 projectionMatrix = camera is null ? Matrix4x4.Identity : unjittered ? camera.ProjectionMatrixUnjittered : camera.ProjectionMatrix;
        Matrix4x4 inverseProjectionMatrix = camera is null ? Matrix4x4.Identity : unjittered ? camera.InverseProjectionMatrixUnjittered : camera.InverseProjectionMatrix;
        Matrix4x4 viewProjectionMatrix = camera is null ? Matrix4x4.Identity : unjittered ? camera.ViewProjectionMatrixUnjittered : camera.ViewProjectionMatrix;
        program.Uniform(view.ToStringFast(), viewMatrix);
        program.Uniform(inverseView.ToStringFast(), inverseViewMatrix);
        program.Uniform(inverseProjection.ToStringFast(), inverseProjectionMatrix);
        program.Uniform(projection.ToStringFast(), projectionMatrix);
        program.Uniform(viewProjection.ToStringFast(), viewProjectionMatrix);
        program.Uniform(view.ToVertexUniformName(), viewMatrix);
        program.Uniform(inverseView.ToVertexUniformName(), inverseViewMatrix);
        program.Uniform(inverseProjection.ToVertexUniformName(), inverseProjectionMatrix);
        program.Uniform(projection.ToVertexUniformName(), projectionMatrix);
        program.Uniform(viewProjection.ToVertexUniformName(), viewProjectionMatrix);
    }

    /// <summary>
    /// Resolves only command-visible draw-target state. Frame-loop context is
    /// intentionally not consulted here; producers carry that value in their
    /// immutable frame operation when it is required for planning.
    /// </summary>
    internal XRFrameBuffer? ResolveCurrentDrawTarget()
    {
        if (XRFrameBuffer.BoundForWriting is { } directlyBoundTarget)
            return directlyBoundTarget;

        XRRenderPipelineInstance.RenderingState.ScopedRenderTargetBinding? binding =
            RuntimeEngine.Rendering.State.CurrentRenderingPipeline?
                .RenderState
                .CurrentRenderTargetBinding;
        return binding is { Write: true, FrameBuffer: { } target }
            ? target
            : CommandBuffers.BoundDrawFrameBuffer;
    }

    internal VulkanMeshProducerSnapshot CaptureIndirectProducerSnapshot(XRFrameBuffer? target)
    {
        Extent2D extent = target is null
            ? StateTracker.GetCurrentTargetExtent()
            : new Extent2D(Math.Max(target.Width, 1u), Math.Max(target.Height, 1u));
        return new VulkanMeshProducerSnapshot(
            default,
            target,
            extent,
            StateTracker.GetViewport(extent),
            VulkanStateTracker.GetDefaultScissor(extent),
            StateTracker.GetIndexedViewportScissorSnapshot(extent),
            StateTracker.CaptureFixedFunctionState(),
            IsExternalSwapchainTarget: false,
            IsPrewarmingExternalSwapchainTarget: false);
    }
}
