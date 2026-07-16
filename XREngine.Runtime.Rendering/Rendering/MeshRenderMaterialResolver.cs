using System;
using System.Numerics;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Textures;

namespace XREngine.Rendering;

public static class MeshRenderMaterialResolver
{
    private static readonly string[] DirectionalCascadeViewProjectionMatrixUniformNames = CreateDirectionalCascadeViewProjectionMatrixUniformNames();
    private static readonly string[] PointLightViewProjectionMatrixUniformNames = CreatePointLightViewProjectionMatrixUniformNames();
    private static XRMaterial? s_lastResortInvalidMaterial;

    public static ResolvedMeshRenderMaterial Resolve(
        XRMeshRenderer meshRenderer,
        XRMaterial? localMaterialOverride,
        uint instances,
        XRMaterial? invalidMaterial = null)
    {
        var renderState = RuntimeEngine.Rendering.State.RenderingPipelineState;
        XRMaterial? globalMaterialOverride = renderState?.GlobalMaterialOverride;
        XRMaterial? pipelineOverrideMaterial = renderState?.OverrideMaterial;

        if (renderState?.ShadowPass ?? false)
        {
            XRMaterial? shadowSourceMaterial = localMaterialOverride ?? meshRenderer.Material;
            bool pointLightShadowOverride = globalMaterialOverride is not null &&
                UsesPointLightShadowDepthOutput(globalMaterialOverride);

            if (globalMaterialOverride is not null &&
                globalMaterialOverride.DirectionalCascadeShadowMaterialKind != EDirectionalCascadeShadowMaterialKind.None)
            {
                XRMaterial directionalCascadeMaterial = ResolveDirectionalCascadeShadowMaterial(
                    meshRenderer,
                    globalMaterialOverride,
                    shadowSourceMaterial,
                    instances);
                return new(directionalCascadeMaterial, directionalCascadeMaterial.ShadowUniformSourceMaterial, true, false, "DirectionalCascadeShadow");
            }

            if (globalMaterialOverride is not null &&
                globalMaterialOverride.PointShadowMaterialKind != EPointShadowMaterialKind.None)
            {
                XRMaterial pointShadowMaterial = ResolvePointLightShadowMaterial(
                    meshRenderer,
                    globalMaterialOverride,
                    shadowSourceMaterial,
                    instances);
                return new(pointShadowMaterial, pointShadowMaterial.ShadowUniformSourceMaterial, true, false, "PointLightShadow");
            }

            if (pointLightShadowOverride && shadowSourceMaterial?.CanUseSharedOpaqueShadowMaterial() == false)
            {
                XRMaterial? pointShadowVariant = shadowSourceMaterial.GetPointShadowCasterVariant(
                    globalMaterialOverride!.GeometryShaders.Count > 0);
                if (pointShadowVariant is not null)
                {
                    pointShadowVariant.ShadowUniformSourceMaterial = globalMaterialOverride;
                    return new(pointShadowVariant, globalMaterialOverride, true, false, "PointLightShadowDepthVariant");
                }
            }

            if (globalMaterialOverride is not null &&
                (globalMaterialOverride.GeometryShaders.Count > 0 || UsesPointLightShadowDepthOutput(globalMaterialOverride)))
            {
                return new(globalMaterialOverride, null, true, false, "GlobalShadowOverride");
            }

            if (globalMaterialOverride is not null && shadowSourceMaterial?.CanUseSharedOpaqueShadowMaterial() == true)
                return new(globalMaterialOverride, null, true, false, "SharedOpaqueShadowOverride");

            XRMaterial? shadowVariant = shadowSourceMaterial?.ShadowCasterVariant;
            if (shadowVariant is not null)
            {
                shadowVariant.ShadowUniformSourceMaterial = globalMaterialOverride;
                return new(shadowVariant, globalMaterialOverride, true, false, "ShadowCasterVariant");
            }
        }

        if (renderState?.UseDepthNormalMaterialVariants ?? false)
        {
            XRMaterial? depthNormalVariant = meshRenderer.Material?.DepthNormalPrePassVariant;
            if (depthNormalVariant is not null)
                return new(depthNormalVariant, null, false, true, "DepthNormalVariant");

            if (pipelineOverrideMaterial is not null)
                return new(pipelineOverrideMaterial, null, false, true, "DepthNormalPipelineOverride");
        }

        XRMaterial material =
            globalMaterialOverride ??
            pipelineOverrideMaterial ??
            localMaterialOverride ??
            meshRenderer.Material ??
            invalidMaterial ??
            RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.InvalidMaterial ??
            XRMaterial.InvalidMaterial ??
            GetLastResortInvalidMaterial();

        string reason =
            globalMaterialOverride is not null ? "GlobalOverride" :
            pipelineOverrideMaterial is not null ? "PipelineOverride" :
            localMaterialOverride is not null ? "LocalOverride" :
            meshRenderer.Material is not null ? "RendererMaterial" :
            "InvalidMaterial";

        return new(material, null, false, false, reason);
    }

    public static bool RequiresTriangleOnlyDrawsForCurrentPass()
        => IsShadowGeometryPass();

    public static uint ResolveLayeredShadowInstanceCount(XRMaterial material, uint instances)
    {
        uint directionalInstances = ResolveDirectionalCascadeLayeredInstanceCount(material, instances);
        return ResolvePointLightLayeredInstanceCount(material, directionalInstances);
    }

    public static void ApplyShadowUniforms(XRRenderProgram program, XRMaterial material)
        => ApplyShadowUniforms(program, material, LayeredShadowUniformState.CaptureFromCurrentRenderingState());

    public static void ApplyShadowUniforms(XRRenderProgram program, XRMaterial material, in LayeredShadowUniformState shadowState)
    {
        if (!shadowState.IsShadowPass)
            return;

        XRMaterial? shadowUniformSource = material.ShadowUniformSourceMaterial;
        if (shadowUniformSource?.HasSettingShadowUniformHandlers == true)
            shadowUniformSource.OnSettingShadowUniforms(program);

        XRMaterial? shadowBindingSource = material.ShadowBindingSourceMaterial;
        bool selectedMaterialHandlerCalled = false;
        if (shadowBindingSource is not null)
        {
            if (shadowBindingSource.HasSettingShadowUniformHandlers)
                shadowBindingSource.OnSettingShadowUniforms(program);
            else if (shadowBindingSource.HasSettingUniformsHandlers)
                shadowBindingSource.OnSettingUniforms(program);

            selectedMaterialHandlerCalled = true;
        }

        if (IsDirectionalCascadeInstancedMaterialKind(material.DirectionalCascadeShadowMaterialKind) &&
            shadowState.DirectionalCascadeInstancedLayeredShadowPass)
        {
            if (shadowBindingSource is null && material.HasSettingShadowUniformHandlers)
            {
                material.OnSettingShadowUniforms(program);
                selectedMaterialHandlerCalled = true;
            }
            else
            {
                SetDirectionalCascadeLayeredUniforms(program, shadowState);
            }
        }

        if (IsPointLightInstancedMaterialKind(material.PointShadowMaterialKind) &&
            shadowState.PointLightInstancedLayeredShadowPass)
        {
            SetPointLightLayeredUniforms(program, shadowState);
        }

        if (!selectedMaterialHandlerCalled && material.HasSettingShadowUniformHandlers)
            material.OnSettingShadowUniforms(program);

        RuntimeEngine.Rendering.State.RenderingPipelineState?.ApplyScopedProgramBindings(program);
    }

    public static bool IsDirectionalCascadeInstancedMaterialKind(EDirectionalCascadeShadowMaterialKind kind)
        => kind is EDirectionalCascadeShadowMaterialKind.InstancedLayered or EDirectionalCascadeShadowMaterialKind.AtlasInstancedLayered;

    public static bool IsPointLightInstancedMaterialKind(EPointShadowMaterialKind kind)
        => kind is EPointShadowMaterialKind.InstancedLayered or EPointShadowMaterialKind.AtlasInstancedLayered;

    public static bool UsesPointLightShadowDepthOutput(XRMaterial material)
        => UsesPointLightShadowCubemap(material) || HasPointLightShadowDepthShader(material);

    private static XRMaterial ResolveDirectionalCascadeShadowMaterial(
        XRMeshRenderer meshRenderer,
        XRMaterial globalMaterialOverride,
        XRMaterial? shadowSourceMaterial,
        uint instances)
    {
        EDirectionalCascadeShadowMaterialKind overrideKind = globalMaterialOverride.DirectionalCascadeShadowMaterialKind;
        bool instancedLayeredOverride =
            IsDirectionalCascadeInstancedMaterialKind(overrideKind) &&
            RuntimeEngine.Rendering.State.IsDirectionalCascadeInstancedLayeredShadowPass;

        if (instancedLayeredOverride && CanUseDirectionalCascadeInstancedMaterial(meshRenderer, shadowSourceMaterial, instances))
            return globalMaterialOverride;

        if (CanUseSharedUberShadowFallback(globalMaterialOverride, shadowSourceMaterial))
            return globalMaterialOverride;

        if (IsDirectionalCascadeGeometryMaterialKind(overrideKind) &&
            shadowSourceMaterial?.CanUseSharedOpaqueShadowMaterial() == true)
        {
            return globalMaterialOverride;
        }

        XRMaterial? directionalVariant = shadowSourceMaterial?.GetDirectionalCascadeShadowCasterVariant(
            GetDirectionalCascadeGeometryFallbackKind(overrideKind));
        if (directionalVariant is not null)
        {
            directionalVariant.ShadowUniformSourceMaterial = globalMaterialOverride;
            return directionalVariant;
        }

        return globalMaterialOverride;
    }

    private static XRMaterial ResolvePointLightShadowMaterial(
        XRMeshRenderer meshRenderer,
        XRMaterial globalMaterialOverride,
        XRMaterial? shadowSourceMaterial,
        uint instances)
    {
        EPointShadowMaterialKind overrideKind = globalMaterialOverride.PointShadowMaterialKind;
        bool instancedLayeredOverride =
            IsPointLightInstancedMaterialKind(overrideKind) &&
            RuntimeEngine.Rendering.State.IsPointLightInstancedLayeredShadowPass;

        if (instancedLayeredOverride && CanUsePointLightInstancedMaterial(meshRenderer, shadowSourceMaterial, instances))
        {
            if (shadowSourceMaterial?.CanUseSharedOpaqueShadowMaterial() != false)
                return globalMaterialOverride;

            if (CanUseSharedUberShadowFallback(globalMaterialOverride, shadowSourceMaterial))
                return globalMaterialOverride;

            XRMaterial? instancedVariant = shadowSourceMaterial?.GetPointShadowCasterVariant(overrideKind);
            if (instancedVariant is not null)
            {
                instancedVariant.ShadowUniformSourceMaterial = globalMaterialOverride;
                return instancedVariant;
            }
        }

        if (IsPointLightGeometryMaterialKind(overrideKind) &&
            shadowSourceMaterial?.CanUseSharedOpaqueShadowMaterial() == true)
        {
            return globalMaterialOverride;
        }

        if (CanUseSharedUberShadowFallback(globalMaterialOverride, shadowSourceMaterial))
            return globalMaterialOverride;

        XRMaterial? geometryVariant = shadowSourceMaterial?.GetPointShadowCasterVariant(GetPointLightGeometryFallbackKind(overrideKind));
        if (geometryVariant is not null)
        {
            geometryVariant.ShadowUniformSourceMaterial = globalMaterialOverride;
            return geometryVariant;
        }

        return globalMaterialOverride;
    }

    private static bool CanUseDirectionalCascadeInstancedMaterial(XRMeshRenderer meshRenderer, XRMaterial? shadowSourceMaterial, uint instances)
    {
        if (instances != 1u)
            return false;

        if (meshRenderer.MeshDeformEnabled)
            return false;

        return shadowSourceMaterial?.CanUseSharedOpaqueShadowMaterial() != false;
    }

    private static bool IsDirectionalCascadeGeometryMaterialKind(EDirectionalCascadeShadowMaterialKind kind)
        => kind is EDirectionalCascadeShadowMaterialKind.GeometryShader or EDirectionalCascadeShadowMaterialKind.AtlasGeometryShader;

    private static EDirectionalCascadeShadowMaterialKind GetDirectionalCascadeGeometryFallbackKind(EDirectionalCascadeShadowMaterialKind kind)
        => kind == EDirectionalCascadeShadowMaterialKind.AtlasInstancedLayered ||
           kind == EDirectionalCascadeShadowMaterialKind.AtlasGeometryShader
            ? EDirectionalCascadeShadowMaterialKind.AtlasGeometryShader
            : EDirectionalCascadeShadowMaterialKind.GeometryShader;

    private static bool CanUsePointLightInstancedMaterial(XRMeshRenderer meshRenderer, XRMaterial? shadowSourceMaterial, uint instances)
    {
        if (instances != 1u)
            return false;

        if (meshRenderer.MeshDeformEnabled)
            return false;

        return shadowSourceMaterial?.CanUseSharedOpaqueShadowMaterial() != false;
    }

    private static bool CanUseSharedUberShadowFallback(XRMaterial globalMaterialOverride, XRMaterial? shadowSourceMaterial)
    {
        if (shadowSourceMaterial is null || ReferenceEquals(shadowSourceMaterial, globalMaterialOverride))
            return false;

        return shadowSourceMaterial.TryGetUberMaterialState(out _, out _);
    }

    private static bool IsPointLightGeometryMaterialKind(EPointShadowMaterialKind kind)
        => kind is EPointShadowMaterialKind.GeometryShader or EPointShadowMaterialKind.AtlasGeometryShader;

    private static EPointShadowMaterialKind GetPointLightGeometryFallbackKind(EPointShadowMaterialKind kind)
        => kind is EPointShadowMaterialKind.AtlasInstancedLayered or EPointShadowMaterialKind.AtlasGeometryShader
            ? EPointShadowMaterialKind.AtlasGeometryShader
            : EPointShadowMaterialKind.GeometryShader;

    private static bool UsesPointLightShadowCubemap(XRMaterial material)
    {
        foreach (XRTexture? texture in material.Textures)
        {
            if (texture is XRTextureCube { SamplerName: "ShadowMap" })
                return true;
        }

        return false;
    }

    private static bool HasPointLightShadowDepthShader(XRMaterial material)
    {
        foreach (var shader in material.FragmentShaders)
        {
            if (shader.Source.FilePath?.EndsWith("PointLightShadowDepth.fs", StringComparison.OrdinalIgnoreCase) == true)
                return true;
        }

        return false;
    }

    private static bool IsShadowGeometryPass()
    {
        var renderState = RuntimeEngine.Rendering.State.RenderingPipelineState;
        return renderState?.ShadowPass == true &&
            (renderState.DirectionalCascadeLayeredShadowPass ||
             renderState.PointLightLayeredShadowPass ||
             (renderState.GlobalMaterialOverride is XRMaterial globalMaterialOverride &&
              globalMaterialOverride.GeometryShaders.Count > 0));
    }

    private static uint ResolveDirectionalCascadeLayeredInstanceCount(XRMaterial material, uint instances)
    {
        if (!IsDirectionalCascadeInstancedMaterialKind(material.DirectionalCascadeShadowMaterialKind) ||
            !RuntimeEngine.Rendering.State.IsDirectionalCascadeInstancedLayeredShadowPass)
        {
            return instances;
        }

        int layerCount = Math.Clamp(RuntimeEngine.Rendering.State.DirectionalCascadeShadowLayerCount, 0, 8);
        if (layerCount <= 1)
            return instances;

        ulong expanded = (ulong)instances * (ulong)layerCount;
        return expanded > uint.MaxValue ? uint.MaxValue : (uint)expanded;
    }

    private static uint ResolvePointLightLayeredInstanceCount(XRMaterial material, uint instances)
    {
        if (!IsPointLightInstancedMaterialKind(material.PointShadowMaterialKind) ||
            !RuntimeEngine.Rendering.State.IsPointLightInstancedLayeredShadowPass)
        {
            return instances;
        }

        int faceCount = Math.Clamp(RuntimeEngine.Rendering.State.PointLightShadowFaceCount, 0, 6);
        if (faceCount <= 1)
            return instances;

        ulong expanded = (ulong)instances * (ulong)faceCount;
        return expanded > uint.MaxValue ? uint.MaxValue : (uint)expanded;
    }

    private static void SetDirectionalCascadeLayeredUniforms(XRRenderProgram program, in LayeredShadowUniformState shadowState)
    {
        if (!shadowState.DirectionalCascadeInstancedLayeredShadowPass)
            return;

        int layerCount = Math.Clamp(shadowState.DirectionalCascadeShadowLayerCount, 0, DirectionalCascadeViewProjectionMatrixUniformNames.Length);
        program.Uniform("CascadeLayerCount", layerCount);
        for (int i = 0; i < layerCount; i++)
        {
            if (shadowState.TryGetDirectionalCascadeShadowMatrix(i, out Matrix4x4 matrix))
                program.Uniform(DirectionalCascadeViewProjectionMatrixUniformNames[i], matrix);
        }
    }

    private static void SetPointLightLayeredUniforms(XRRenderProgram program, in LayeredShadowUniformState shadowState)
    {
        if (!shadowState.PointLightInstancedLayeredShadowPass)
            return;

        int faceCount = Math.Clamp(shadowState.PointLightShadowFaceCount, 0, PointLightViewProjectionMatrixUniformNames.Length);
        program.Uniform("PointShadowFaceCount", faceCount);
        for (int i = 0; i < faceCount; i++)
        {
            if (shadowState.TryGetPointLightShadowFaceMatrix(i, out Matrix4x4 matrix))
                program.Uniform(PointLightViewProjectionMatrixUniformNames[i], matrix);
            if (shadowState.TryGetPointLightShadowFaceIndex(i, out int faceIndex))
                program.Uniform($"PointShadowFaceIndices[{i}]", faceIndex);
        }
    }

    private static string[] CreateDirectionalCascadeViewProjectionMatrixUniformNames()
    {
        string[] names = new string[8];
        for (int i = 0; i < names.Length; i++)
            names[i] = $"CascadeViewProjectionMatrices[{i}]";
        return names;
    }

    private static string[] CreatePointLightViewProjectionMatrixUniformNames()
    {
        string[] names = new string[6];
        for (int i = 0; i < names.Length; i++)
            names[i] = $"PointShadowViewProjectionMatrices[{i}]";
        return names;
    }

    private static XRMaterial GetLastResortInvalidMaterial()
        => s_lastResortInvalidMaterial ??= new XRMaterial { Name = "LastResortInvalidMaterial" };
}
