using System;
using System.Linq;
using System.Numerics;
using XREngine.Data.Profiling;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders.Generator;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        public partial class GLMeshRenderer
        {
            private bool _inlineGenerateFallbackLogged;
            private static readonly string[] s_directionalCascadeViewProjectionMatrixUniformNames = CreateDirectionalCascadeViewProjectionMatrixUniformNames();
            private static readonly string[] s_pointLightViewProjectionMatrixUniformNames = CreatePointLightViewProjectionMatrixUniformNames();

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

            /// <summary>
            /// Select the effective material, honoring global/pipeline overrides.
            /// </summary>
            public GLMaterial GetRenderMaterial(XRMaterial? localMaterialOverride = null, uint instances = 1)
            {
                var renderState = Engine.Rendering.State.RenderingPipelineState;
                var globalMaterialOverride = renderState?.GlobalMaterialOverride;
                var pipelineOverrideMaterial = renderState?.OverrideMaterial;

                if (renderState?.ShadowPass ?? false)
                {
                    XRMaterial? shadowSourceMaterial = localMaterialOverride ?? MeshRenderer.Material;
                    bool pointLightShadowOverride = globalMaterialOverride is not null
                        && UsesPointLightShadowDepthOutput(globalMaterialOverride);

                    if (globalMaterialOverride is not null &&
                        globalMaterialOverride.DirectionalCascadeShadowMaterialKind != EDirectionalCascadeShadowMaterialKind.None)
                    {
                        XRMaterial directionalCascadeMaterial = ResolveDirectionalCascadeShadowMaterial(
                            globalMaterialOverride,
                            shadowSourceMaterial,
                            instances);
                        return GetOrCreateShadowMaterial(directionalCascadeMaterial);
                    }

                    if (globalMaterialOverride is not null &&
                        globalMaterialOverride.PointShadowMaterialKind != EPointShadowMaterialKind.None)
                    {
                        XRMaterial pointShadowMaterial = ResolvePointLightShadowMaterial(
                            globalMaterialOverride,
                            shadowSourceMaterial,
                            instances);
                        return GetOrCreateShadowMaterial(pointShadowMaterial);
                    }

                    if (pointLightShadowOverride
                        && shadowSourceMaterial?.CanUseSharedOpaqueShadowMaterial() == false)
                    {
                        XRMaterial? pointShadowVariant = shadowSourceMaterial.GetPointShadowCasterVariant(
                            globalMaterialOverride!.GeometryShaders.Count > 0);
                        if (pointShadowVariant is not null)
                        {
                            pointShadowVariant.ShadowUniformSourceMaterial = globalMaterialOverride;
                            return (Renderer.GetOrCreateAPIRenderObject(pointShadowVariant) as GLMaterial)!;
                        }
                    }

                    // When the global override includes a geometry shader (e.g. point light
                    // cubemap shadow map), the GS must participate in rendering every mesh.
                    // Per-mesh shadow variants wouldn't include the required GS, so the
                    // override takes priority.
                    if (globalMaterialOverride is not null &&
                        (globalMaterialOverride.GeometryShaders.Count > 0 || UsesPointLightShadowDepthOutput(globalMaterialOverride)))
                    {
                        return (Renderer.GetOrCreateAPIRenderObject(globalMaterialOverride) as GLMaterial)!;
                    }

                    if (globalMaterialOverride is not null && shadowSourceMaterial?.CanUseSharedOpaqueShadowMaterial() == true)
                        return (Renderer.GetOrCreateAPIRenderObject(globalMaterialOverride) as GLMaterial)!;

                    XRMaterial? shadowVariant = shadowSourceMaterial?.ShadowCasterVariant;
                    if (shadowVariant is not null)
                    {
                        shadowVariant.ShadowUniformSourceMaterial = globalMaterialOverride;

                        return GetOrCreateShadowMaterial(shadowVariant);
                    }
                }

                if (renderState?.UseDepthNormalMaterialVariants ?? false)
                {
                    XRMaterial? depthNormalVariant = MeshRenderer.Material?.DepthNormalPrePassVariant;

                    if (depthNormalVariant is not null)
                        return (Renderer.GetOrCreateAPIRenderObject(depthNormalVariant) as GLMaterial)!;

                    if (pipelineOverrideMaterial is not null)
                        return (Renderer.GetOrCreateAPIRenderObject(pipelineOverrideMaterial) as GLMaterial)!;
                }

                var mat =
                    (globalMaterialOverride is null ? null : Renderer.GetOrCreateAPIRenderObject(globalMaterialOverride) as GLMaterial) ??
                    (pipelineOverrideMaterial is null ? null : Renderer.GetOrCreateAPIRenderObject(pipelineOverrideMaterial) as GLMaterial) ??
                    (localMaterialOverride is null ? null : Renderer.GetOrCreateAPIRenderObject(localMaterialOverride) as GLMaterial) ??
                    Material;

                if (mat is not null)
                    return mat;

                Debug.OpenGLWarning("No material found for mesh renderer, using invalid material.");
                return Renderer.GenericToAPI<GLMaterial>(Engine.Rendering.State.CurrentRenderingPipeline!.InvalidMaterial)!;
            }

            private XRMaterial ResolveDirectionalCascadeShadowMaterial(
                XRMaterial globalMaterialOverride,
                XRMaterial? shadowSourceMaterial,
                uint instances)
            {
                EDirectionalCascadeShadowMaterialKind overrideKind = globalMaterialOverride.DirectionalCascadeShadowMaterialKind;
                bool instancedLayeredOverride =
                    IsDirectionalCascadeInstancedMaterialKind(overrideKind) &&
                    Engine.Rendering.State.IsDirectionalCascadeInstancedLayeredShadowPass;

                if (instancedLayeredOverride && CanUseDirectionalCascadeInstancedMaterial(shadowSourceMaterial, instances))
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

            private XRMaterial ResolvePointLightShadowMaterial(
                XRMaterial globalMaterialOverride,
                XRMaterial? shadowSourceMaterial,
                uint instances)
            {
                EPointShadowMaterialKind overrideKind = globalMaterialOverride.PointShadowMaterialKind;
                bool instancedLayeredOverride =
                    IsPointLightInstancedMaterialKind(overrideKind) &&
                    Engine.Rendering.State.IsPointLightInstancedLayeredShadowPass;

                if (instancedLayeredOverride && CanUsePointLightInstancedMaterial(shadowSourceMaterial, instances))
                {
                    if (shadowSourceMaterial?.CanUseSharedOpaqueShadowMaterial() != false)
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

                XRMaterial? geometryVariant = shadowSourceMaterial?.GetPointShadowCasterVariant(GetPointLightGeometryFallbackKind(overrideKind));
                if (geometryVariant is not null)
                {
                    geometryVariant.ShadowUniformSourceMaterial = globalMaterialOverride;
                    return geometryVariant;
                }

                return globalMaterialOverride;
            }

            private bool CanUseDirectionalCascadeInstancedMaterial(XRMaterial? shadowSourceMaterial, uint instances)
            {
                if (instances != 1u)
                    return false;

                if (MeshRenderer.MeshDeformEnabled)
                    return false;

                return shadowSourceMaterial?.CanUseSharedOpaqueShadowMaterial() != false;
            }

            private static bool IsDirectionalCascadeInstancedMaterialKind(EDirectionalCascadeShadowMaterialKind kind)
                => kind is EDirectionalCascadeShadowMaterialKind.InstancedLayered or EDirectionalCascadeShadowMaterialKind.AtlasInstancedLayered;

            private static bool IsDirectionalCascadeGeometryMaterialKind(EDirectionalCascadeShadowMaterialKind kind)
                => kind is EDirectionalCascadeShadowMaterialKind.GeometryShader or EDirectionalCascadeShadowMaterialKind.AtlasGeometryShader;

            private static EDirectionalCascadeShadowMaterialKind GetDirectionalCascadeGeometryFallbackKind(EDirectionalCascadeShadowMaterialKind kind)
                => kind == EDirectionalCascadeShadowMaterialKind.AtlasInstancedLayered ||
                   kind == EDirectionalCascadeShadowMaterialKind.AtlasGeometryShader
                    ? EDirectionalCascadeShadowMaterialKind.AtlasGeometryShader
                    : EDirectionalCascadeShadowMaterialKind.GeometryShader;

            private bool CanUsePointLightInstancedMaterial(XRMaterial? shadowSourceMaterial, uint instances)
            {
                if (instances != 1u)
                    return false;

                if (MeshRenderer.MeshDeformEnabled)
                    return false;

                return shadowSourceMaterial?.CanUseSharedOpaqueShadowMaterial() != false;
            }

            private static bool IsPointLightInstancedMaterialKind(EPointShadowMaterialKind kind)
                => kind is EPointShadowMaterialKind.InstancedLayered or EPointShadowMaterialKind.AtlasInstancedLayered;

            private static bool IsPointLightGeometryMaterialKind(EPointShadowMaterialKind kind)
                => kind is EPointShadowMaterialKind.GeometryShader or EPointShadowMaterialKind.AtlasGeometryShader;

            private static EPointShadowMaterialKind GetPointLightGeometryFallbackKind(EPointShadowMaterialKind kind)
                => kind is EPointShadowMaterialKind.AtlasInstancedLayered or EPointShadowMaterialKind.AtlasGeometryShader
                    ? EPointShadowMaterialKind.AtlasGeometryShader
                    : EPointShadowMaterialKind.GeometryShader;

            private GLMaterial GetOrCreateShadowMaterial(XRMaterial shadowMaterial)
            {
                if (ReferenceEquals(shadowMaterial, _shadowVariantKey) && _shadowMaterialCache is not null)
                    return _shadowMaterialCache;

                var glMat = (Renderer.GetOrCreateAPIRenderObject(shadowMaterial) as GLMaterial)!;
                _shadowVariantKey = shadowMaterial;
                _shadowMaterialCache = glMat;
                return glMat;
            }

            private static bool UsesPointLightShadowCubemap(XRMaterial material)
                => material.Textures.Any(texture => texture is XRTextureCube { SamplerName: "ShadowMap" });

            private static bool UsesPointLightShadowDepthOutput(XRMaterial material)
                => UsesPointLightShadowCubemap(material) ||
                   material.FragmentShaders.Any(shader =>
                       shader.Source.FilePath?.EndsWith("PointLightShadowDepth.fs", StringComparison.OrdinalIgnoreCase) == true);

            private static bool IsPointLightShadowGeometryPass()
            {
                var renderState = Engine.Rendering.State.RenderingPipelineState;
                return renderState?.ShadowPass == true
                    && renderState.GlobalMaterialOverride is XRMaterial globalMaterialOverride
                    && globalMaterialOverride.GeometryShaders.Count > 0
                    && UsesPointLightShadowCubemap(globalMaterialOverride);
            }

            private static bool IsShadowGeometryPass()
            {
                var renderState = Engine.Rendering.State.RenderingPipelineState;
                return renderState?.ShadowPass == true
                    && (renderState.DirectionalCascadeLayeredShadowPass ||
                    renderState.PointLightLayeredShadowPass ||
                    (renderState.GlobalMaterialOverride is XRMaterial globalMaterialOverride
                    && globalMaterialOverride.GeometryShaders.Count > 0));
            }

            internal bool RequiresTriangleOnlyDrawsForCurrentPass()
                => IsShadowGeometryPass();

            /// <summary>
            /// Primary render entry point, handling shader selection, buffer binding, and uniforms.
            /// Sets per-mesh and per-camera uniforms required for rendering, including support for stereo (VR) passes,
            /// billboard rendering modes, and previous-frame transforms for motion vectors.
            /// </summary>
            /// <param name="modelMatrix">
            /// The current model transform matrix for the mesh. Used by the vertex and material programs.
            /// </param>
            /// <param name="prevModelMatrix">
            /// The previous frame's model transform matrix. If this is approximately identity while the current
            /// <paramref name="modelMatrix"/> is not, it is coerced to the current model matrix to avoid generating
            /// erroneous motion vectors for objects that were not tracked previously.
            /// </param>
            /// <param name="vertexProgram">
            /// The primary GL render program that receives required engine and camera uniforms (e.g., view/projection,
            /// VR mode, billboard mode, and model matrices).
            /// </param>
            /// <param name="materialProgram">
            /// An optional material GL render program that also receives shared mesh uniforms (model and previous model matrices).
            /// </param>
            /// <param name="billboardMode">
            /// The mesh billboard mode controlling how the mesh is oriented relative to the camera during rendering.
            /// </param>
            /// <remarks>
            /// - In stereo (VR) passes, both left and right eye camera uniforms (inverse view and projection) are set.
            /// - In non-stereo passes, standard inverse view and projection uniforms are set.
            /// - Model and previous model matrices are applied to both the vertex and material programs when available.
            /// - The VR mode flag and billboard mode are assigned to the vertex program.
            /// </remarks>
            public void Render(
                Matrix4x4 modelMatrix,
                Matrix4x4 prevModelMatrix,
                XRMaterial? materialOverride,
                RenderingParameters? renderOptionsOverride,
                uint instances,
                EMeshBillboardMode billboardMode)
            {
                Dbg($"Render request (instances={instances}, billboard={billboardMode})", "Render");
                LogBatchedTextDraw("Render request", instances, $"billboard={billboardMode}");

                if (Data is null || !Renderer.Active)
                {
                    Dbg("Render early-out: Data null or renderer inactive", "Render");
                    LogBatchedTextDraw("Render inactive", instances, $"dataNull={Data is null}, rendererActive={Renderer.Active}");
                    return;
                }

                using var prof = Engine.Profiler.Start("GLMeshRenderer.Render", ProfilerScopeKind.AlwaysOnHotPathLoop);

                if (!IsGenerated)
                {
                    bool shadowPass = Engine.Rendering.State.RenderingPipelineState?.ShadowPass ?? false;
                    bool isRenderPipelinePriority = MeshRenderer.GenerationPriority == EMeshGenerationPriority.RenderPipeline;
                    bool throttleRenderPipelineGeneration = isRenderPipelinePriority && Renderer.MeshGenerationQueue.ThrottlePriorityGeneration;

                    // Shadow passes never cold-start GL resources to avoid amplifying startup cost.
                    // Normal scene meshes defer to the frame-budgeted queue when enabled.
                    // Render-pipeline meshes normally generate inline because their output is consumed
                    // the same frame, but during startup budget boosts they also defer so cold-start
                    // warmup is spread across frames instead of stalling the render thread.
                    if (shadowPass || (Renderer.MeshGenerationQueue.Enabled && (!isRenderPipelinePriority || throttleRenderPipelineGeneration)))
                    {
                        Renderer.MeshGenerationQueue.EnqueueGeneration(this);
                        Dbg(shadowPass
                            ? "Not generated yet during shadow pass - queued for deferred generation"
                            : throttleRenderPipelineGeneration
                                ? "Not generated yet - startup throttling queued render-pipeline generation"
                                : "Not generated yet - queued for deferred generation", "Render");
                        LogBatchedTextDraw("Render queued-generation", instances, $"shadow={shadowPass}, priority={MeshRenderer.GenerationPriority}, queue={Renderer.MeshGenerationQueue.Enabled}");
                        return; // Skip rendering until generated
                    }

                    using (Engine.Profiler.Start("GLMeshRenderer.Render.Generate", ProfilerScopeKind.OneOffInvoke))
                    {
                        if (!_inlineGenerateFallbackLogged)
                        {
                            _inlineGenerateFallbackLogged = true;
                            Debug.OpenGLWarning(
                                $"[GLMeshRenderer] Inline Generate fallback for '{GetDescribingName()}' (priority={MeshRenderer.GenerationPriority}, shadowPass={shadowPass}, queueEnabled={Renderer.MeshGenerationQueue.Enabled}).");
                        }

                        Dbg("Not generated yet - calling Generate()", "Render");
                        Generate();
                    }
                }

                if (!IsPreparedForRendering)
                {
                    if (MeshRenderer.HasRenderDataPreparation)
                        _ = TryPrepareForRendering();
                }

                if (!IsPreparedForRendering)
                {
                    bool isRenderPipelinePriority = MeshRenderer.GenerationPriority == EMeshGenerationPriority.RenderPipeline;
                    bool throttleRenderPipelineGeneration = isRenderPipelinePriority && Renderer.MeshGenerationQueue.ThrottlePriorityGeneration;
                    if (Renderer.MeshGenerationQueue.Enabled && (!isRenderPipelinePriority || throttleRenderPipelineGeneration))
                    {
                        Renderer.MeshGenerationQueue.EnqueueGeneration(this);
                        Dbg("Generated but not render-ready - queued for deferred preparation", "Render");
                        LogBatchedTextDraw("Render queued-preparation", instances, $"priority={MeshRenderer.GenerationPriority}, queue={Renderer.MeshGenerationQueue.Enabled}");
                        return;
                    }
                }

                using (Engine.Profiler.Start("GLMeshRenderer.Render.ProgramSetup", ProfilerScopeKind.AlwaysOnHotPathLoop))
                {
                    EnsureProgramsMatchRenderSettings();
                    EnsureProgramsMatchMaterialShaderState();
                }

                GLMaterial material = GetRenderMaterial(materialOverride, instances);
                uint drawInstances = ResolveLayeredShadowInstanceCount(material.Data, instances);

                if (GetPrograms(material, out var vtx, out var mat))
                {
                    Dbg("Programs ready - binding SSBOs and uniforms", "Render");
                    ConfigureDrawTopology(vtx!, mat);

                    if (BuffersBound && VertexArrayBindingsStale())
                    {
                        BuffersBound = false;
                        LogBatchedTextDraw("Render vao-stale-rebind", instances);
                    }

                    if (!BuffersBound)
                        BindBuffers(vtx!);

                    if (!BuffersBound)
                    {
                        Renderer.MeshGenerationQueue.EnqueueGeneration(this);
                        LogBatchedTextDraw("Render buffers-not-bound", instances);
                        return;
                    }

                    BindSSBOs(mat!);
                    BindSSBOs(vtx!);

                    BindSkinnedVertexBuffers(vtx!);

                    MeshRenderer.PushBoneMatricesToGPU();
                    MeshRenderer.PushBlendshapeWeightsToGPU();

                    SetMeshUniforms(modelMatrix, prevModelMatrix, MeshRenderer, vtx!, mat, materialOverride?.BillboardMode ?? billboardMode);

                    using (Engine.Profiler.Start("GLMeshRenderer.Render.SetMaterialUniforms", ProfilerScopeKind.AlwaysOnHotPathLoop))
                    {
                        material.SetUniforms(mat);
                        if (renderOptionsOverride is not null)
                            Renderer.ApplyRenderParameters(renderOptionsOverride);
                    }

                    OnSettingUniforms(vtx!, mat!);
                    SetDirectionalCascadeLayeredVertexUniforms(vtx!, material.Data);
                    SetPointLightLayeredVertexUniforms(vtx!, material.Data);
                    material.FinalizeUniformBindings(mat);
                    GLRenderProgram materialProgram = mat!;
                    Renderer.SetDrawDebugContext(
                        materialProgram.Data.Name,
                        material.Data.Name,
                        Mesh?.Name,
                        materialProgram.GetBoundSamplerUnitsView());

                    try
                    {
                        using (Engine.Profiler.Start("GLMeshRenderer.Render.Draw", ProfilerScopeKind.AlwaysOnHotPathLoop))
                        {
                            LogBatchedTextDraw("Render draw-submit", drawInstances, $"program='{materialProgram.Data.Name}', material='{material.Data.Name}'");
                            Renderer.RenderMesh(this, false, drawInstances);
                        }
                    }
                    finally
                    {
                        Renderer.ClearDrawDebugContext();
                    }
                    Dbg("Render mesh submitted", "Render");
                }
                else
                {
                    Renderer.MeshGenerationQueue.EnqueueGeneration(this);
                    Dbg("GetPrograms failed - render skipped", "Render");
                    LogBatchedTextDraw("Render programs-missing", instances);
                }
            }

            private static uint ResolveLayeredShadowInstanceCount(XRMaterial material, uint instances)
            {
                uint directionalInstances = ResolveDirectionalCascadeLayeredInstanceCount(material, instances);
                return ResolvePointLightLayeredInstanceCount(material, directionalInstances);
            }

            private static uint ResolveDirectionalCascadeLayeredInstanceCount(XRMaterial material, uint instances)
            {
                if (!IsDirectionalCascadeInstancedMaterialKind(material.DirectionalCascadeShadowMaterialKind) ||
                    !Engine.Rendering.State.IsDirectionalCascadeInstancedLayeredShadowPass)
                {
                    return instances;
                }

                int layerCount = Math.Clamp(Engine.Rendering.State.DirectionalCascadeShadowLayerCount, 0, 8);
                if (layerCount <= 1)
                    return instances;

                ulong expanded = (ulong)instances * (ulong)layerCount;
                return expanded > uint.MaxValue ? uint.MaxValue : (uint)expanded;
            }

            private static uint ResolvePointLightLayeredInstanceCount(XRMaterial material, uint instances)
            {
                if (!IsPointLightInstancedMaterialKind(material.PointShadowMaterialKind) ||
                    !Engine.Rendering.State.IsPointLightInstancedLayeredShadowPass)
                {
                    return instances;
                }

                int faceCount = Math.Clamp(Engine.Rendering.State.PointLightShadowFaceCount, 0, 6);
                if (faceCount <= 1)
                    return instances;

                ulong expanded = (ulong)instances * (ulong)faceCount;
                return expanded > uint.MaxValue ? uint.MaxValue : (uint)expanded;
            }

            /// <summary>
            /// Sets all mesh-related shader uniforms for the current render pass, including camera matrices, model transforms,
            /// stereo rendering flags, and billboard mode handling.
            /// </summary>
            /// <param name="modelMatrix">The current model transformation matrix for the mesh.</param>
            /// <param name="prevModelMatrix">The previous model transformation matrix used for motion vector calculations.</param>
            /// <param name="vertexProgram">The vertex shader program to which required uniforms are applied.</param>
            /// <param name="materialProgram">An optional material shader program that also receives model transform uniforms.</param>
            /// <param name="billboardMode">The billboard mode used to orient the mesh relative to the camera.</param>
            private static void SetMeshUniforms(
                Matrix4x4 modelMatrix,
                Matrix4x4 prevModelMatrix,
                XRMeshRenderer meshRenderer,
                GLRenderProgram vertexProgram,
                GLRenderProgram? materialProgram,
                EMeshBillboardMode billboardMode)
            {
                bool stereoPass = Engine.Rendering.State.IsStereoPass;
                var cam = Engine.Rendering.State.RenderingCamera;
                if (stereoPass)
                {
                    var rightCam = Engine.Rendering.State.RenderingStereoRightEyeCamera;
                    PassCameraUniforms(vertexProgram, cam, EEngineUniform.LeftEyeViewMatrix, EEngineUniform.LeftEyeInverseViewMatrix, EEngineUniform.LeftEyeInverseProjMatrix, EEngineUniform.LeftEyeProjMatrix, EEngineUniform.LeftEyeViewProjectionMatrix);
                    PassCameraUniforms(vertexProgram, rightCam, EEngineUniform.RightEyeViewMatrix, EEngineUniform.RightEyeInverseViewMatrix, EEngineUniform.RightEyeInverseProjMatrix, EEngineUniform.RightEyeProjMatrix, EEngineUniform.RightEyeViewProjectionMatrix);
                }
                else
                {
                    PassCameraUniforms(vertexProgram, cam, EEngineUniform.ViewMatrix, EEngineUniform.InverseViewMatrix, EEngineUniform.InverseProjMatrix, EEngineUniform.ProjMatrix, EEngineUniform.ViewProjectionMatrix);
                }

                void SetUniformBoth(EEngineUniform uniform, Matrix4x4 value)
                {
                    vertexProgram.Uniform(uniform, value);
                    materialProgram?.Uniform(uniform, value);
                }

                // If previous transform was never captured, assume static to avoid fake motion vectors.
                if (IsApproximatelyIdentity(prevModelMatrix) && !IsApproximatelyIdentity(modelMatrix))
                    prevModelMatrix = modelMatrix;

                SetUniformBoth(EEngineUniform.ModelMatrix, modelMatrix);
                SetUniformBoth(EEngineUniform.PrevModelMatrix, prevModelMatrix);

                bool shadowGeometryPass = IsShadowGeometryPass();

                // CPU draw path has gl_BaseInstance==0; provide a per-draw TransformId uniform so
                // deferred shaders can write stable per-transform IDs into the GBuffer.
                uint transformId = Engine.Rendering.State.CurrentTransformId;
                vertexProgram.Uniform("TransformId", transformId);
                materialProgram?.Uniform("TransformId", transformId);
                vertexProgram.Uniform("boneMatrixBase", meshRenderer.ActiveBoneMatrixBase);
                materialProgram?.Uniform("boneMatrixBase", meshRenderer.ActiveBoneMatrixBase);
                SetDirectionalCascadeLayeredUniforms(vertexProgram);
                SetPointLightLayeredUniforms(vertexProgram);

                vertexProgram.Uniform(EEngineUniform.VRMode, stereoPass || shadowGeometryPass);
                vertexProgram.Uniform(EEngineUniform.BillboardMode, (int)billboardMode);
            }

            private static void SetDirectionalCascadeLayeredUniforms(GLRenderProgram vertexProgram)
            {
                var state = Engine.Rendering.State.RenderingPipelineState;
                if (state?.DirectionalCascadeInstancedLayeredShadowPass != true)
                    return;

                int layerCount = Math.Clamp(state.DirectionalCascadeShadowLayerCount, 0, s_directionalCascadeViewProjectionMatrixUniformNames.Length);
                vertexProgram.Uniform("CascadeLayerCount", layerCount);
                for (int i = 0; i < layerCount; i++)
                {
                    if (state.TryGetDirectionalCascadeShadowMatrix(i, out Matrix4x4 matrix))
                        vertexProgram.Uniform(s_directionalCascadeViewProjectionMatrixUniformNames[i], matrix);
                }
            }

            private static void SetDirectionalCascadeLayeredVertexUniforms(GLRenderProgram vertexProgram, XRMaterial material)
            {
                if (!IsDirectionalCascadeInstancedMaterialKind(material.DirectionalCascadeShadowMaterialKind) ||
                    !Engine.Rendering.State.IsDirectionalCascadeInstancedLayeredShadowPass)
                {
                    return;
                }

                if (material.HasSettingShadowUniformHandlers)
                    material.OnSettingShadowUniforms(vertexProgram.Data);
                else
                    SetDirectionalCascadeLayeredUniforms(vertexProgram);
            }

            private static void SetPointLightLayeredUniforms(GLRenderProgram vertexProgram)
            {
                var state = Engine.Rendering.State.RenderingPipelineState;
                if (state?.PointLightInstancedLayeredShadowPass != true)
                    return;

                int faceCount = Math.Clamp(state.PointLightShadowFaceCount, 0, s_pointLightViewProjectionMatrixUniformNames.Length);
                vertexProgram.Uniform("PointShadowFaceCount", faceCount);
                for (int i = 0; i < faceCount; i++)
                {
                    if (state.TryGetPointLightShadowFaceMatrix(i, out Matrix4x4 matrix))
                        vertexProgram.Uniform(s_pointLightViewProjectionMatrixUniformNames[i], matrix);
                    if (state.TryGetPointLightShadowFaceIndex(i, out int faceIndex))
                        vertexProgram.Uniform($"PointShadowFaceIndices[{i}]", faceIndex);
                }
            }

            private static void SetPointLightLayeredVertexUniforms(GLRenderProgram vertexProgram, XRMaterial material)
            {
                if (!IsPointLightInstancedMaterialKind(material.PointShadowMaterialKind) ||
                    !Engine.Rendering.State.IsPointLightInstancedLayeredShadowPass)
                {
                    return;
                }

                SetPointLightLayeredUniforms(vertexProgram);
            }

            /// <summary>
            /// Loose identity check to avoid precision noise when deciding motion vectors.
            /// </summary>
            private static bool IsApproximatelyIdentity(in Matrix4x4 m)
            {
                const float eps = 1e-4f;
                return MathF.Abs(m.M11 - 1f) < eps && MathF.Abs(m.M22 - 1f) < eps && MathF.Abs(m.M33 - 1f) < eps && MathF.Abs(m.M44 - 1f) < eps
                    && MathF.Abs(m.M12) < eps && MathF.Abs(m.M13) < eps && MathF.Abs(m.M14) < eps
                    && MathF.Abs(m.M21) < eps && MathF.Abs(m.M23) < eps && MathF.Abs(m.M24) < eps
                    && MathF.Abs(m.M31) < eps && MathF.Abs(m.M32) < eps && MathF.Abs(m.M34) < eps
                    && MathF.Abs(m.M41) < eps && MathF.Abs(m.M42) < eps && MathF.Abs(m.M43) < eps;
            }

            /// <summary>
            /// Pushes camera transforms to the vertex program, avoiding inverse in shader for precision.
            /// </summary>
            private static void PassCameraUniforms(GLRenderProgram vertexProgram, XRCamera? camera, EEngineUniform view, EEngineUniform invView, EEngineUniform invProj, EEngineUniform proj, EEngineUniform viewProj)
            {
                Matrix4x4 viewMatrix;
                Matrix4x4 inverseViewMatrix;
                Matrix4x4 inverseProjMatrix;
                Matrix4x4 projMatrix;
                Matrix4x4 viewProjectionMatrix;

                if (camera != null)
                {
                    viewMatrix = camera.Transform.InverseRenderMatrix;
                    inverseViewMatrix = camera.Transform.RenderMatrix;
                    bool useUnjittered = Engine.Rendering.State.RenderingPipelineState?.UseUnjitteredProjection ?? false;
                    projMatrix = useUnjittered ? camera.ProjectionMatrixUnjittered : camera.ProjectionMatrix;
                    inverseProjMatrix = useUnjittered ? camera.InverseProjectionMatrixUnjittered : camera.InverseProjectionMatrix;
                    viewProjectionMatrix = useUnjittered ? camera.ViewProjectionMatrixUnjittered : camera.ViewProjectionMatrix;
                }
                else
                {
                    viewMatrix = Matrix4x4.Identity;
                    inverseViewMatrix = Matrix4x4.Identity;
                    inverseProjMatrix = Matrix4x4.Identity;
                    projMatrix = Matrix4x4.Identity;
                    viewProjectionMatrix = Matrix4x4.Identity;
                }

                vertexProgram.Uniform(view.ToStringFast(), viewMatrix);
                vertexProgram.Uniform(invView.ToStringFast(), inverseViewMatrix);
                vertexProgram.Uniform(invProj.ToStringFast(), inverseProjMatrix);
                vertexProgram.Uniform(proj.ToStringFast(), projMatrix);
                vertexProgram.Uniform(viewProj.ToStringFast(), viewProjectionMatrix);

                // Use cached uniform names to avoid string allocations per call
                vertexProgram.Uniform(view.ToVertexUniformName(), viewMatrix);
                vertexProgram.Uniform(invView.ToVertexUniformName(), inverseViewMatrix);
                vertexProgram.Uniform(invProj.ToVertexUniformName(), inverseProjMatrix);
                vertexProgram.Uniform(proj.ToVertexUniformName(), projMatrix);
                vertexProgram.Uniform(viewProj.ToVertexUniformName(), viewProjectionMatrix);
            }

            /// <summary>
            /// Notify mesh renderer when setting uniforms for additional custom uniform handling.
            /// </summary>
            /// <param name="vertexProgram"></param>
            /// <param name="materialProgram"></param>
            private void OnSettingUniforms(GLRenderProgram vertexProgram, GLRenderProgram materialProgram)
                => MeshRenderer.OnSettingUniforms(vertexProgram.Data, materialProgram.Data);
        }
    }
}
