using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Textures;
using XREngine.Scene;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanMeshBackendServices
{
    internal void SetEngineUniforms(XRRenderProgram program, XRCamera camera)
    {
        if (program is null)
            return;

        if (RuntimeEngine.Rendering.State.IsStereoPass)
        {
            PassCameraUniforms(
                program,
                camera,
                EEngineUniform.LeftEyeViewMatrix,
                EEngineUniform.LeftEyeInverseViewMatrix,
                EEngineUniform.LeftEyeInverseProjMatrix,
                EEngineUniform.LeftEyeProjMatrix,
                EEngineUniform.LeftEyeViewProjectionMatrix);
            PassCameraUniforms(
                program,
                RuntimeEngine.Rendering.State.RenderingStereoRightEyeCamera,
                EEngineUniform.RightEyeViewMatrix,
                EEngineUniform.RightEyeInverseViewMatrix,
                EEngineUniform.RightEyeInverseProjMatrix,
                EEngineUniform.RightEyeProjMatrix,
                EEngineUniform.RightEyeViewProjectionMatrix);
            return;
        }

        PassCameraUniforms(
            program,
            camera,
            EEngineUniform.ViewMatrix,
            EEngineUniform.InverseViewMatrix,
            EEngineUniform.InverseProjMatrix,
            EEngineUniform.ProjMatrix,
            EEngineUniform.ViewProjectionMatrix);
    }

    internal VulkanFixedFunctionStateSnapshot CaptureFixedFunctionState()
        => commandRuntime.StateTracker.CaptureFixedFunctionState();

    internal void RestoreFixedFunctionState(in VulkanFixedFunctionStateSnapshot snapshot)
        => commandRuntime.StateTracker.RestoreFixedFunctionState(snapshot);

    internal ComputeDispatchSnapshot? GetForwardLightingBindingSnapshotForArtifact(
        Lights3DCollection lights,
        XRRenderProgram programData,
        VkRenderProgram backendProgram)
        => GetForwardLightingBindingSnapshot(lights, programData, backendProgram);

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
            SetMaterialRuntimeUniforms(material, program, backendProgram, shadowState);
            return;
        }

        XRMaterial? shadowBindingSource = null;
        MaterialShadowBindingPlan? shadowBindingPlan = null;
        if (material.RenderOptions is not null)
            ApplyRenderParameters(material.RenderOptions);

        shadowBindingSource = material.ShadowBindingSourceMaterial;
        if (shadowBindingSource is not null)
            shadowBindingPlan = GetOrCreateShadowBindingPlan(program, shadowBindingSource);

        XRMaterialBase uniformSource = shadowBindingPlan is not null ? shadowBindingSource! : material;
        if (shadowBindingPlan is not null)
        {
            foreach (ShaderVar parameter in shadowBindingPlan.Parameters)
                parameter.SetUniform(program, forceUpdate: true);
            SetTextureUniforms(program, shadowBindingSource!, shadowBindingPlan.TextureIndices);
        }
        else
        {
            foreach (ShaderVar parameter in uniformSource.Parameters)
                parameter.SetUniform(program, forceUpdate: true);
            SetTextureUniforms(program, uniformSource);
        }

        PublishRequiredEngineBindings(
            material,
            program,
            backendProgram,
            includeDrawOwnedFrameUniforms: !shadowState.IsShadowPass);
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
        PublishRequiredEngineBindings(
            material,
            program,
            backendProgram,
            includeDrawOwnedFrameUniforms: true);
        ApplyMutableLegacyMaterialBindings(material, program, backendProgram);
    }

    private void PublishRequiredEngineBindings(
        XRMaterial material,
        XRRenderProgram program,
        VkRenderProgram? backendProgram,
        bool includeDrawOwnedFrameUniforms)
    {
        EUniformRequirements requirements =
            (material.RenderOptions?.RequiredEngineUniforms ?? EUniformRequirements.None) |
            program.GetActiveEngineUniformRequirements();

        if (includeDrawOwnedFrameUniforms &&
            (requirements & EUniformRequirements.Camera) != 0)
        {
            RuntimeEngine.Rendering.State.RenderingCamera?.SetUniforms(program, true);
            RuntimeEngine.Rendering.State.RenderingStereoRightEyeCamera?.SetUniforms(program, false);
        }

        bool lightingUniformsBound = false;
        if ((requirements & EUniformRequirements.Lights) != 0 &&
            RuntimeEngine.Rendering.State.RenderingWorld?.Lights is { } lights)
        {
            if (backendProgram is not null)
                SetForwardLightingUniformsCached(lights, program, backendProgram);
            else
                lights.SetForwardLightingUniforms(program);
            lightingUniformsBound = true;
        }

        if ((requirements & EUniformRequirements.AmbientOcclusion) != 0 && !lightingUniformsBound)
            Lights3DCollection.SetForwardAmbientOcclusionUniforms(program);

        if (includeDrawOwnedFrameUniforms &&
            (requirements & EUniformRequirements.RenderTime) != 0)
        {
            program.Uniform(EEngineUniform.RenderTime.ToStringFast(), MaterialUniformSeconds);
            program.Uniform(EEngineUniform.EngineTime.ToStringFast(), MaterialUniformSeconds);
            program.Uniform(EEngineUniform.DeltaTime.ToStringFast(), MaterialUniformRenderDelta);
        }

        if (includeDrawOwnedFrameUniforms &&
            (requirements & EUniformRequirements.ViewportDimensions) != 0)
        {
            var area = RuntimeEngine.Rendering.State.RenderArea;
            float width = area.Width;
            float height = area.Height;
            if (width <= 0f || height <= 0f)
            {
                XRFrameBuffer? target = ResolveCurrentDrawTarget();
                if (target is not null)
                {
                    width = target.Width;
                    height = target.Height;
                }
                else
                {
                    Silk.NET.Vulkan.Extent2D extent = commandRuntime.StateTracker.GetCurrentTargetExtent();
                    width = extent.Width;
                    height = extent.Height;
                }
            }

            program.Uniform(EEngineUniform.ScreenWidth.ToStringFast(), width);
            program.Uniform(EEngineUniform.ScreenHeight.ToStringFast(), height);
            program.Uniform(EEngineUniform.ScreenOrigin.ToStringFast(), new Vector2(area.X, area.Y));
        }

        if (includeDrawOwnedFrameUniforms &&
            (requirements & EUniformRequirements.ClipSpacePolicy) != 0)
        {
            program.Uniform(EEngineUniform.ClipSpaceYDirection.ToStringFast(), (int)RuntimeEngine.Rendering.Settings.ClipSpaceYDirection);
            program.Uniform(EEngineUniform.ClipDepthRange.ToStringFast(), (int)RuntimeEngine.Rendering.EffectiveClipDepthRange);
            program.Uniform(EEngineUniform.FramebufferTextureYDirection.ToStringFast(), (int)RenderClipSpacePolicy.FramebufferTextureYDirection(RuntimeGraphicsApiKind.Vulkan));
        }
    }

    internal void ApplyRenderParameters(RenderingParameters parameters)
    {
        VulkanStateTracker state = commandRuntime.StateTracker;
        state.SetColorMask(parameters.WriteRed, parameters.WriteGreen, parameters.WriteBlue, parameters.WriteAlpha);
        state.SetCullMode(VulkanMeshRenderingConventions.ToVulkanCullMode(VulkanMeshRenderingConventions.ResolveCullMode(parameters.CullMode)));
        state.SetFrontFace(VulkanMeshRenderingConventions.ToVulkanFrontFace(VulkanMeshRenderingConventions.ResolveWinding(parameters.Winding)));
        state.SetAlphaToCoverageEnabled(parameters.AlphaToCoverage == ERenderParamUsage.Enabled);

        DepthTest depthTest = parameters.DepthTest;
        if (depthTest.Enabled == ERenderParamUsage.Enabled)
        {
            state.SetDepthTestEnabled(true);
            state.SetDepthWriteEnabled(depthTest.UpdateDepth);
            state.SetDepthCompare(VulkanMeshRenderingConventions.ToVulkanCompareOp(RuntimeEngine.Rendering.State.MapDepthComparison(depthTest.Function)));
        }
        else if (depthTest.Enabled == ERenderParamUsage.Disabled)
        {
            state.SetDepthTestEnabled(false);
            state.SetDepthWriteEnabled(false);
        }

        StencilTest stencilTest = parameters.StencilTest;
        if (stencilTest.Enabled == ERenderParamUsage.Enabled)
        {
            state.SetStencilEnabled(true);
            state.SetStencilStates(
                VulkanMeshRenderingConventions.ToVulkanStencilState(stencilTest.FrontFace),
                VulkanMeshRenderingConventions.ToVulkanStencilState(stencilTest.BackFace));
            state.SetStencilWriteMask(stencilTest.FrontFace.WriteMask);
        }
        else if (stencilTest.Enabled == ERenderParamUsage.Disabled)
        {
            state.SetStencilEnabled(false);
            state.SetStencilStates(default, default);
            state.SetStencilWriteMask(0);
        }

        BlendMode? blend = VulkanMeshRenderingConventions.ResolveBlendMode(parameters);
        if (blend is not null && blend.Enabled == ERenderParamUsage.Enabled)
        {
            state.SetBlendState(
                true,
                VulkanMeshRenderingConventions.ToVulkanBlendOp(blend.RgbEquation),
                VulkanMeshRenderingConventions.ToVulkanBlendOp(blend.AlphaEquation),
                VulkanMeshRenderingConventions.ToVulkanBlendFactor(blend.RgbSrcFactor),
                VulkanMeshRenderingConventions.ToVulkanBlendFactor(blend.RgbDstFactor),
                VulkanMeshRenderingConventions.ToVulkanBlendFactor(blend.AlphaSrcFactor),
                VulkanMeshRenderingConventions.ToVulkanBlendFactor(blend.AlphaDstFactor));
            return;
        }

        state.SetBlendState(false, Silk.NET.Vulkan.BlendOp.Add, Silk.NET.Vulkan.BlendOp.Add,
            Silk.NET.Vulkan.BlendFactor.One, Silk.NET.Vulkan.BlendFactor.Zero,
            Silk.NET.Vulkan.BlendFactor.One, Silk.NET.Vulkan.BlendFactor.Zero);
    }

    private static void PassCameraUniforms(
        XRRenderProgram program,
        XRCamera? camera,
        EEngineUniform viewName,
        EEngineUniform inverseViewName,
        EEngineUniform inverseProjectionName,
        EEngineUniform projectionName,
        EEngineUniform viewProjectionName)
    {
        Matrix4x4 viewMatrix;
        Matrix4x4 inverseViewMatrix;
        Matrix4x4 inverseProjectionMatrix;
        Matrix4x4 projectionMatrix;
        Matrix4x4 viewProjectionMatrix;
        if (camera is not null)
        {
            viewMatrix = camera.Transform.InverseRenderMatrix;
            inverseViewMatrix = camera.Transform.RenderMatrix;
            bool useUnjittered = RuntimeEngine.Rendering.State.RenderingPipelineState?.UseUnjitteredProjection ?? false;
            projectionMatrix = useUnjittered ? camera.ProjectionMatrixUnjittered : camera.ProjectionMatrix;
            inverseProjectionMatrix = useUnjittered ? camera.InverseProjectionMatrixUnjittered : camera.InverseProjectionMatrix;
            viewProjectionMatrix = useUnjittered ? camera.ViewProjectionMatrixUnjittered : camera.ViewProjectionMatrix;
        }
        else
        {
            viewMatrix = Matrix4x4.Identity;
            inverseViewMatrix = Matrix4x4.Identity;
            inverseProjectionMatrix = Matrix4x4.Identity;
            projectionMatrix = Matrix4x4.Identity;
            viewProjectionMatrix = Matrix4x4.Identity;
        }

        program.Uniform(viewName.ToStringFast(), viewMatrix);
        program.Uniform(inverseViewName.ToStringFast(), inverseViewMatrix);
        program.Uniform(inverseProjectionName.ToStringFast(), inverseProjectionMatrix);
        program.Uniform(projectionName.ToStringFast(), projectionMatrix);
        program.Uniform(viewProjectionName.ToStringFast(), viewProjectionMatrix);

        program.Uniform(viewName.ToVertexUniformName(), viewMatrix);
        program.Uniform(inverseViewName.ToVertexUniformName(), inverseViewMatrix);
        program.Uniform(inverseProjectionName.ToVertexUniformName(), inverseProjectionMatrix);
        program.Uniform(projectionName.ToVertexUniformName(), projectionMatrix);
        program.Uniform(viewProjectionName.ToVertexUniformName(), viewProjectionMatrix);
    }

    private MaterialShadowBindingPlan GetOrCreateShadowBindingPlan(
        XRRenderProgram program,
        XRMaterial sourceMaterial)
    {
        VulkanCommandBufferState state = commandRuntime.CommandBuffers;
        ulong layoutVersion = sourceMaterial.BindingLayoutVersion;
        if (state.ShadowBindingPlan is not null &&
            ReferenceEquals(state.ShadowBindingSourceMaterial, sourceMaterial) &&
            ReferenceEquals(state.ShadowBindingProgram, program) &&
            state.ShadowBindingSourceLayoutVersion == layoutVersion)
        {
            return state.ShadowBindingPlan;
        }

        state.ShadowBindingPlan = MaterialTextureBindingResolver.BuildShadowBindingPlan(program, sourceMaterial);
        state.ShadowBindingSourceMaterial = sourceMaterial;
        state.ShadowBindingProgram = program;
        state.ShadowBindingSourceLayoutVersion = layoutVersion;
        return state.ShadowBindingPlan;
    }

    private static void ApplyMutableLegacyMaterialBindings(
        XRMaterial material,
        XRRenderProgram program,
        VkRenderProgram? backendProgram)
    {
        if (backendProgram is null)
        {
            material.OnSettingUniforms(program);
            RuntimeEngine.Rendering.State.RenderingPipelineState?.ApplyScopedProgramBindings(program);
            return;
        }

        using VkRenderProgram.MutableLegacyBindingPublicationScope publication =
            backendProgram.BeginMutableLegacyBindingPublication();
        material.OnSettingUniforms(program);
        RuntimeEngine.Rendering.State.RenderingPipelineState?.ApplyScopedProgramBindings(program);
    }

    private static void SetTextureUniforms(XRRenderProgram program, XRMaterialBase material)
    {
        for (int index = 0; index < material.Textures.Count; index++)
            SetTextureUniform(program, material, index);
    }

    private static void SetTextureUniforms(
        XRRenderProgram program,
        XRMaterialBase material,
        int[] textureIndices)
    {
        foreach (int index in textureIndices)
            SetTextureUniform(program, material, index);
    }

    private static void SetTextureUniform(XRRenderProgram program, XRMaterialBase material, int index)
    {
        if ((uint)index >= (uint)material.Textures.Count || material.Textures[index] is not { } texture)
            return;

        string samplerName = texture.ResolveSamplerName(index, null);
        program.Sampler(samplerName, texture, index);
        string indexedName = XRTexture.GetIndexedSamplerName(index);
        if (!string.Equals(samplerName, indexedName, StringComparison.Ordinal) &&
            !program.HasUniform(samplerName) && program.HasUniform(indexedName))
        {
            program.Sampler(indexedName, texture, index);
        }
    }

    internal void SetForwardLightingUniformsCached(
        Lights3DCollection lights,
        XRRenderProgram programData,
        VkRenderProgram backendProgram)
    {
        ComputeDispatchSnapshot? snapshot = GetForwardLightingBindingSnapshot(lights, programData, backendProgram);
        if (snapshot is null)
        {
            lights.SetForwardLightingUniforms(programData);
            return;
        }

        backendProgram.MergeBindingSnapshot(snapshot);
    }

    private ComputeDispatchSnapshot? GetForwardLightingBindingSnapshot(
        Lights3DCollection lights,
        XRRenderProgram programData,
        VkRenderProgram backendProgram)
    {
        ulong frameId = RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId;
        if (frameId == 0)
            return null;

        var renderingState = RuntimeEngine.Rendering.State.RenderingPipelineState;
        var renderArea = RuntimeEngine.Rendering.State.RenderArea;
        ForwardLightingBindingSnapshotCacheKey key = new(
            lights,
            RuntimeEngine.Rendering.State.CurrentRenderingPipeline,
            RuntimeEngine.Rendering.State.RenderingCamera,
            RuntimeEngine.Rendering.State.RenderingStereoRightEyeCamera,
            RuntimeEngine.Rendering.State.RenderingWorld,
            ResolveCurrentDrawTarget(),
            RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex,
            renderArea.X,
            renderArea.Y,
            renderArea.Width,
            renderArea.Height,
            RuntimeEngine.Rendering.State.IsStereoPass,
            renderingState?.UseUnjitteredProjection ?? false);

        VulkanCommandBufferState state = commandRuntime.CommandBuffers;
        lock (state.ForwardLightingGate)
        {
            if (state.ForwardLightingSnapshotFrame != frameId)
            {
                state.ForwardLightingSnapshotFrame = frameId;
                state.ForwardLightingSnapshots.Clear();
            }

            if (state.ForwardLightingSnapshots.TryGetValue(key, out ComputeDispatchSnapshot? cached))
                return cached;

            ComputeDispatchSnapshot captured;
            using (VkRenderProgram.BindingUpdateScope lightingCapture = backendProgram.BeginBindingUpdate())
            {
                backendProgram.ClearBindings();
                lights.SetForwardLightingUniforms(programData);
                captured = backendProgram.CaptureComputeSnapshot();
            }

            state.ForwardLightingSnapshots.Add(key, captured);
            return captured;
        }
    }
}
