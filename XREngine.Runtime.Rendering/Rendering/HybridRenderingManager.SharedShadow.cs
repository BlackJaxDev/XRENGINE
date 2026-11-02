using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

public partial class HybridRenderingManager
{
    private readonly record struct SharedShadowProgramKey(
        long ShaderRevision,
        uint LayoutMask,
        int ShaderSettingsVersion);

    private readonly record struct SharedShadowProgramEntry(
        SharedShadowProgramKey Key,
        MaterialProgramCache Cache);

    private sealed class SharedShadowProgramSlot
    {
        public SharedShadowProgramEntry? Current;
        public SharedShadowProgramEntry? Pending;
    }

    private readonly Dictionary<(XRMaterial Material, XRMeshRenderer Renderer), SharedShadowProgramSlot> _sharedShadowPrograms = [];
    private readonly XRMeshRenderer?[] _sharedShadowTierRenderers = new XRMeshRenderer?[(int)GPUBatchingBindings.MaterialTierCount];
    private readonly XRRenderProgram?[] _sharedShadowTierPrograms = new XRRenderProgram?[(int)GPUBatchingBindings.MaterialTierCount];

    private static bool IsSupportedSharedShadowOverride(
        XRMaterial material,
        in LayeredShadowUniformState shadowState)
    {
        if (!shadowState.IsShadowPass ||
            shadowState.PointLightInstancedLayeredShadowPass ||
            shadowState.DirectionalCascadeInstancedLayeredShadowPass ||
            MeshRenderMaterialResolver.IsPointLightInstancedMaterialKind(material.PointShadowMaterialKind) ||
            MeshRenderMaterialResolver.IsDirectionalCascadeInstancedMaterialKind(material.DirectionalCascadeShadowMaterialKind))
            return false;

        if (material.PointShadowMaterialKind != EPointShadowMaterialKind.None &&
            material.DirectionalCascadeShadowMaterialKind != EDirectionalCascadeShadowMaterialKind.None)
            return false;

        string? expectedGeometry = material.PointShadowMaterialKind switch
        {
            EPointShadowMaterialKind.GeometryShader => "PointLightShadowDepth.gs",
            EPointShadowMaterialKind.AtlasGeometryShader => "PointLightAtlasShadowDepth.gs",
            _ => material.DirectionalCascadeShadowMaterialKind switch
            {
                EDirectionalCascadeShadowMaterialKind.GeometryShader => "DirectionalCascadeShadowDepth.gs",
                EDirectionalCascadeShadowMaterialKind.AtlasGeometryShader => "DirectionalCascadeAtlasShadowDepth.gs",
                _ => null,
            },
        };

        if (expectedGeometry is null)
            return material.GeometryShaders.Count == 0 &&
                !shadowState.PointLightLayeredShadowPass &&
                !shadowState.DirectionalCascadeLayeredShadowPass;

        if (material.GeometryShaders.Count != 1)
            return false;

        return material.PointShadowMaterialKind != EPointShadowMaterialKind.None
            ? shadowState.PointLightLayeredShadowPass && !shadowState.DirectionalCascadeLayeredShadowPass
            : shadowState.DirectionalCascadeLayeredShadowPass && !shadowState.PointLightLayeredShadowPass;
    }

    private static bool HasTrustedSharedShadowGeometryShader(XRMaterial material)
    {
        string? expectedGeometry = material.PointShadowMaterialKind switch
        {
            EPointShadowMaterialKind.GeometryShader => "PointLightShadowDepth.gs",
            EPointShadowMaterialKind.AtlasGeometryShader => "PointLightAtlasShadowDepth.gs",
            _ => material.DirectionalCascadeShadowMaterialKind switch
            {
                EDirectionalCascadeShadowMaterialKind.GeometryShader => "DirectionalCascadeShadowDepth.gs",
                EDirectionalCascadeShadowMaterialKind.AtlasGeometryShader => "DirectionalCascadeAtlasShadowDepth.gs",
                _ => null,
            },
        };

        return expectedGeometry is null
            ? material.GeometryShaders.Count == 0
            : material.GeometryShaders.Count == 1 &&
                ReferenceEquals(material.GeometryShaders[0],
                    ShaderHelper.LoadEngineShader(expectedGeometry, EShaderType.Geometry));
    }

    private XRRenderProgram? EnsureSharedShadowProgram(XRMaterial material, XRMeshRenderer renderer)
    {
        uint layoutMask = (renderer.Buffers.ContainsKey("Normal") ? 1u : 0u) |
            (renderer.Buffers.ContainsKey("Tangent") ? 2u : 0u) |
            (renderer.Buffers.ContainsKey("TexCoord0") ? 4u : 0u);
        SharedShadowProgramKey key = new(material.ShaderStateRevision, layoutMask,
            RuntimeEngine.Rendering.Settings.ShaderConfigVersion);
        (XRMaterial, XRMeshRenderer) useKey = (material, renderer);
        SharedShadowProgramSlot? slot = _sharedShadowPrograms.GetValueOrDefault(useKey);
        if (slot is not null)
        {
            if (slot.Pending is { } pending && pending.Key == key)
            {
                pending.Cache.Program.Link();
                if (IsProgramReadyForCurrentRenderer(pending.Cache.Program))
                    PromoteSharedShadowProgram(slot);
                return pending.Cache.Program;
            }

            if (slot.Current is { } current && current.Key == key)
            {
                if (slot.Pending is { } stalePending)
                {
                    DestroyMaterialProgramCache(stalePending.Cache);
                    slot.Pending = null;
                }
                current.Cache.Program.Link();
                return current.Cache.Program;
            }
        }

        if (!HasTrustedSharedShadowGeometryShader(material))
            return null;

        List<XRShader> shaders = new(material.Shaders.Count);
        int fragmentCount = 0;
        foreach (XRShader shader in material.Shaders)
        {
            if (shader.Type == EShaderType.Vertex)
                continue;
            if (shader.Type == EShaderType.Fragment)
                fragmentCount++;
            if (shader.Type is not (EShaderType.Fragment or EShaderType.Geometry))
                return null;
            shaders.Add(shader);
        }
        if (fragmentCount != 1)
            return null;

        XRShader? generatedVertex = CreateGpuIndirectVertexShader(renderer, false, false);
        if (generatedVertex is null)
            return null;
        shaders.Add(generatedVertex);
        XRRenderProgramDescriptor descriptor = BuildGpuDrivenCombinedProgramDescriptor(
            material, shaders, generatedVertex, false, false, false);
        XRRenderProgram program = new(linkNow: false, separable: false, shaders)
        {
            Name = $"GpuIndirectSharedShadow:{material.Name ?? "unnamed"}",
            UsageTag = "GpuIndirectSharedShadow",
            ProgramDescriptor = descriptor,
            Priority = material.ShaderProgramPriority,
        };
        material.ApplyShaderProgramMetadata(program);
        program.SetShaderProgramDiagnosticMetadata(new XRRenderProgram.ShaderProgramDiagnosticMetadata(
            material.Name, renderer.Name, "GpuIndirectSharedShadow", "GpuDrivenSharedShadow",
            descriptor.StableKey, descriptor.VertexLayoutIdentity));
        program.AllowLink();
        program.Link();
        slot ??= new SharedShadowProgramSlot();
        if (slot.Pending is { } replacedPending)
            DestroyMaterialProgramCache(replacedPending.Cache);
        slot.Pending = new SharedShadowProgramEntry(key,
            new MaterialProgramCache(program, generatedVertex, material.ShaderStateRevision));
        _sharedShadowPrograms[useKey] = slot;
        if (IsProgramReadyForCurrentRenderer(program))
            PromoteSharedShadowProgram(slot);
        return program;
    }

    private static void PromoteSharedShadowProgram(SharedShadowProgramSlot slot)
    {
        if (slot.Pending is not { } pending)
            return;

        if (slot.Current is { } previous)
            DestroyMaterialProgramCache(previous.Cache);
        slot.Current = pending;
        slot.Pending = null;
    }

    private void RenderZeroReadbackSharedShadowBuckets(
        GPURenderPassCollection renderPasses,
        XRCamera camera,
        GPUScene scene,
        int currentRenderPass)
    {
        if (renderPasses.ZeroReadbackMaterialDrawPath is not
            (EZeroReadbackMaterialDrawPath.MaterialTable or EZeroReadbackMaterialDrawPath.BindlessMaterialTable))
        {
            RejectCompactMaterialTableSubmission(currentRenderPass,
                "Shared opaque shadows require the compact material-tier indirect layout.");
            throw new RenderResourcePreparationPendingException(
                "Shared opaque shadows require the compact material-tier indirect layout.");
        }

        XRMaterial? shadowMaterial = RuntimeEngine.Rendering.State.RenderingPipelineState?.GlobalMaterialOverride;
        LayeredShadowUniformState shadowState = LayeredShadowUniformState.CaptureFromCurrentRenderingState();
        if (shadowMaterial is null || !IsSupportedSharedShadowOverride(shadowMaterial, shadowState))
        {
            RejectCompactMaterialTableSubmission(currentRenderPass,
                "The shadow override or layered shadow mode is unsupported for shared indirect drawing.");
            throw new RenderResourcePreparationPendingException(
                "The shadow override or layered shadow mode is unsupported for shared indirect drawing.");
        }

        if (!renderPasses.TryGetGeneratedMaterialTableDispatchLayout(currentRenderPass, out _) ||
            renderPasses.MaterialTierBucketCount != GPUBatchingBindings.MaterialTierCount ||
            renderPasses.MaterialTierIndirectDrawBuffer is not { } indirectDrawBuffer ||
            renderPasses.MaterialTierDrawCountBuffer is not { } parameterBuffer ||
            renderPasses.CulledSceneToRenderBuffer is not { } culledCommandsBuffer ||
            scene.DrawMetadataBuffer is null)
        {
            RejectCompactMaterialTableSubmission(currentRenderPass,
                "The shared shadow indirect range or generated tier layout is unavailable.");
            throw new RenderResourcePreparationPendingException(
                "The shared shadow indirect range or generated tier layout is unavailable.");
        }

        uint stride = (uint)Marshal.SizeOf<DrawElementsIndirectCommand>();
        uint maxDrawsPerBucket = Math.Max(renderPasses.MaxDrawsPerMaterialTier, 1u);
        bool hasPreparedTier = false;
        for (uint tier = 0u; tier < GPUBatchingBindings.MaterialTierCount; ++tier)
        {
            if (scene.GetAtlasIndexCount((EAtlasTier)tier) == 0)
            {
                _sharedShadowTierRenderers[tier] = null;
                _sharedShadowTierPrograms[tier] = null;
                continue;
            }

            XRMeshRenderer? tierRenderer = ConfigureIndirectRendererForTier(scene, (EAtlasTier)tier);
            _sharedShadowTierRenderers[tier] = tierRenderer;
            _sharedShadowTierPrograms[tier] = null;
            if (tierRenderer is null)
                throw new RenderResourcePreparationPendingException(
                    "A nonempty shadow atlas tier has no ready indirect renderer.");

            hasPreparedTier = true;

            XRRenderProgram? program = EnsureSharedShadowProgram(shadowMaterial, tierRenderer);
            if (program is null)
            {
                RejectCompactMaterialTableSubmission(currentRenderPass,
                    "The shared shadow override has unsupported shader stages.");
                throw new RenderResourcePreparationPendingException(
                    "The shared shadow override has unsupported shader stages.");
            }
            if (!IsProgramReadyForCurrentRenderer(program))
            {
                renderPasses.RecordZeroReadbackProgramPending();
                throw new RenderResourcePreparationPendingException(
                    "The shared shadow indirect program is not ready for the current renderer.");
            }

            _sharedShadowTierPrograms[tier] = program;
        }

        if (!hasPreparedTier)
            throw new RenderResourcePreparationPendingException(
                "The shared shadow pass has no prepared atlas tier.");

        XRDataBuffer? instanceTransformBuffer = scene.TransformBuffer;
        XRDataBuffer? instanceSourceIndexBuffer = renderPasses.InstanceSourceIndexBuffer;
        AbstractRenderer? renderer = AbstractRenderer.Current;
        renderer?.ApplyRenderParameters(shadowMaterial.RenderOptions);
        renderer?.MemoryBarrier(EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);

        for (uint tier = 0u; tier < GPUBatchingBindings.MaterialTierCount; ++tier)
        {
            XRMeshRenderer? tierRenderer = _sharedShadowTierRenderers[tier];
            XRRenderProgram? program = _sharedShadowTierPrograms[tier];
            if (tierRenderer is null || program is null)
                continue;

            renderer?.SetMaterialUniforms(shadowMaterial, program);
            MeshRenderMaterialResolver.ApplyShadowUniforms(program, shadowMaterial, in shadowState);

            DispatchRenderIndirectCountBucket(
                indirectDrawBuffer, tierRenderer, culledCommandsBuffer, scene.DrawMetadataBuffer,
                scene.LodTransitionBuffer, instanceTransformBuffer, instanceSourceIndexBuffer,
                instanceTransformBuffer is not null, maxDrawsPerBucket,
                (nuint)(tier * maxDrawsPerBucket * stride), parameterBuffer,
                (nuint)(tier * sizeof(uint)), program, camera, Matrix4x4.Identity,
                material: shadowMaterial, allowMaxDrawFallback: false, emitBarrier: false,
                diagnosticRenderPass: currentRenderPass, requireSubmission: true);
        }
    }
}
