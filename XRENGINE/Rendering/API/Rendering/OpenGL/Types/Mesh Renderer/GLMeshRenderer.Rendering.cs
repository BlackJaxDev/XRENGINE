using System.Linq;
using System.Numerics;
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

            /// <summary>
            /// Select the effective material, honoring global/pipeline overrides.
            /// </summary>
            public GLMaterial GetRenderMaterial(XRMaterial? localMaterialOverride = null)
            {
                var renderState = Engine.Rendering.State.RenderingPipelineState;
                var globalMaterialOverride = renderState?.GlobalMaterialOverride;
                var pipelineOverrideMaterial = renderState?.OverrideMaterial;

                if (renderState?.ShadowPass ?? false)
                {
                    // When the global override includes a geometry shader (e.g. point light
                    // cubemap shadow map), the GS must participate in rendering every mesh.
                    // Per-mesh shadow variants wouldn't include the required GS, so the
                    // override takes priority.
                    if (globalMaterialOverride is not null &&
                        (globalMaterialOverride.GeometryShaders.Count > 0 || UsesPointLightShadowCubemap(globalMaterialOverride)))
                    {
                        return (Renderer.GetOrCreateAPIRenderObject(globalMaterialOverride) as GLMaterial)!;
                    }

                    XRMaterial? shadowSourceMaterial = localMaterialOverride ?? MeshRenderer.Material;
                    XRMaterial? shadowVariant = shadowSourceMaterial?.ShadowCasterVariant;
                    if (shadowVariant is not null)
                    {
                        // Fast path: reuse cached GLMaterial when the shadow variant hasn't changed.
                        if (ReferenceEquals(shadowVariant, _shadowVariantKey) && _shadowMaterialCache is not null)
                            return _shadowMaterialCache;

                        var glMat = (Renderer.GetOrCreateAPIRenderObject(shadowVariant) as GLMaterial)!;
                        _shadowVariantKey = shadowVariant;
                        _shadowMaterialCache = glMat;
                        return glMat;
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

            private static bool UsesPointLightShadowCubemap(XRMaterial material)
                => material.Textures.Any(texture => texture is XRTextureCube { SamplerName: "ShadowMap" });

            private static bool IsPointLightShadowGeometryPass()
            {
                var renderState = Engine.Rendering.State.RenderingPipelineState;
                return renderState?.ShadowPass == true
                    && renderState.GlobalMaterialOverride is XRMaterial globalMaterialOverride
                    && globalMaterialOverride.GeometryShaders.Count > 0
                    && UsesPointLightShadowCubemap(globalMaterialOverride);
            }

            internal bool RequiresTriangleOnlyDrawsForCurrentPass()
                => IsPointLightShadowGeometryPass();

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
                uint instances,
                EMeshBillboardMode billboardMode)
            {
                Dbg($"Render request (instances={instances}, billboard={billboardMode})", "Render");

                if (Data is null || !Renderer.Active)
                {
                    Dbg("Render early-out: Data null or renderer inactive", "Render");
                    return;
                }

                using var prof = Engine.Profiler.Start("GLMeshRenderer.Render");

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
                        return; // Skip rendering until generated
                    }

                    using (Engine.Profiler.Start("GLMeshRenderer.Render.Generate"))
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

                using (Engine.Profiler.Start("GLMeshRenderer.Render.ProgramSetup"))
                {
                    var settingsVersion = Engine.Rendering.Settings.ShaderConfigVersion;
                    // Recreate programs if global shader config (defines, skinning mode) changed since last draw.
                    if (_shaderConfigVersion != settingsVersion)
                    {
                        _shaderConfigVersion = settingsVersion;

                        _combinedProgram?.Destroy();
                        _combinedProgram = null;

                        _separatedVertexProgram?.Destroy();
                        _separatedVertexProgram = null;

                        _forcedGeneratedVertexProgram?.Destroy();
                        _forcedGeneratedVertexProgram = null;

                        if (!Engine.Rendering.Settings.CalculateSkinningInComputeShader)
                            DestroySkinnedBuffers();

                        BuffersBound = false;
                        GenProgramsAndBuffers();
                    }
                }

                GLMaterial material = GetRenderMaterial(materialOverride);

                if (GetPrograms(material, out var vtx, out var mat))
                {
                    Dbg("Programs ready - binding SSBOs and uniforms", "Render");
                    ConfigureDrawTopology(vtx!, mat);

                    if (!BuffersBound)
                        BindBuffers(vtx!);

                    if (!BuffersBound)
                        return;

                    BindSSBOs(mat!);
                    BindSSBOs(vtx!);

                    BindSkinnedVertexBuffers(vtx!);

                    MeshRenderer.PushBoneMatricesToGPU();
                    MeshRenderer.PushBlendshapeWeightsToGPU();

                    SetMeshUniforms(modelMatrix, prevModelMatrix, MeshRenderer, vtx!, mat, materialOverride?.BillboardMode ?? billboardMode);

                    using (Engine.Profiler.Start("GLMeshRenderer.Render.SetMaterialUniforms"))
                    {
                        material.SetUniforms(mat);
                    }

                    OnSettingUniforms(vtx!, mat!);
                    material.FinalizeUniformBindings(mat);
                    GLRenderProgram materialProgram = mat!;
                    Renderer.SetDrawDebugContext(
                        materialProgram.Data.Name,
                        material.Data.Name,
                        Mesh?.Name,
                        materialProgram.GetBoundSamplerUnitsSnapshot());

                    try
                    {
                        using (Engine.Profiler.Start("GLMeshRenderer.Render.Draw"))
                        {
                            Renderer.RenderMesh(this, false, instances);
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
                    Dbg("GetPrograms failed - render skipped", "Render");
                }
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

                bool pointLightShadowGeometryPass = IsPointLightShadowGeometryPass();

                // CPU draw path has gl_BaseInstance==0; provide a per-draw TransformId uniform so
                // deferred shaders can write stable per-transform IDs into the GBuffer.
                uint transformId = Engine.Rendering.State.CurrentTransformId;
                vertexProgram.Uniform("TransformId", transformId);
                materialProgram?.Uniform("TransformId", transformId);
                vertexProgram.Uniform("boneMatrixBase", meshRenderer.ActiveBoneMatrixBase);
                materialProgram?.Uniform("boneMatrixBase", meshRenderer.ActiveBoneMatrixBase);

                vertexProgram.Uniform(EEngineUniform.VRMode, stereoPass || pointLightShadowGeometryPass);
                vertexProgram.Uniform(EEngineUniform.BillboardMode, (int)billboardMode);
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
