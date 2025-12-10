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
            /// <summary>
            /// Select the effective material, honoring global/pipeline overrides.
            /// </summary>
            public GLMaterial GetRenderMaterial(XRMaterial? localMaterialOverride = null)
            {
                var renderState = Engine.Rendering.State.RenderingPipelineState;
                var globalMaterialOverride = renderState?.GlobalMaterialOverride;
                var pipelineOverrideMaterial = renderState?.OverrideMaterial;

                var mat =
                    (globalMaterialOverride is null ? null : Renderer.GetOrCreateAPIRenderObject(globalMaterialOverride) as GLMaterial) ??
                    (pipelineOverrideMaterial is null ? null : Renderer.GetOrCreateAPIRenderObject(pipelineOverrideMaterial) as GLMaterial) ??
                    (localMaterialOverride is null ? null : Renderer.GetOrCreateAPIRenderObject(localMaterialOverride) as GLMaterial) ??
                    Material;

                if (mat is not null)
                    return mat;

                Debug.LogWarning("No material found for mesh renderer, using invalid material.");
                return Renderer.GenericToAPI<GLMaterial>(Engine.Rendering.State.CurrentRenderingPipeline!.InvalidMaterial)!;
            }

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
                    Dbg("Not generated yet - calling Generate()", "Render");
                    Generate();
                }

                var settingsVersion = Engine.Rendering.Settings.ShaderConfigVersion;
                // Recreate programs if global shader config (defines, skinning mode) changed since last draw.
                if (_shaderConfigVersion != settingsVersion)
                {
                    _shaderConfigVersion = settingsVersion;

                    _combinedProgram?.Destroy();
                    _combinedProgram = null;

                    _separatedVertexProgram?.Destroy();
                    _separatedVertexProgram = null;

                    if (!Engine.Rendering.Settings.CalculateSkinningInComputeShader)
                        DestroySkinnedBuffers();

                    BuffersBound = false;
                    GenProgramsAndBuffers();
                }

                GLMaterial material = GetRenderMaterial(materialOverride);
                if (GetPrograms(material, out var vtx, out var mat))
                {
                    Dbg("Programs ready - binding SSBOs and uniforms", "Render");

                    if (!BuffersBound)
                        BindBuffers(vtx!);

                    if (!BuffersBound)
                        return;

                    BindSSBOs(mat!);
                    BindSSBOs(vtx!);

                    BindSkinnedVertexBuffers(vtx!);

                    MeshRenderer.PushBoneMatricesToGPU();
                    MeshRenderer.PushBlendshapeWeightsToGPU();

                    SetMeshUniforms(modelMatrix, prevModelMatrix, vtx!, mat, materialOverride?.BillboardMode ?? billboardMode);
                    material.SetUniforms(mat);
                    OnSettingUniforms(vtx!, mat!);
                    Renderer.RenderMesh(this, false, instances);
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
                GLRenderProgram vertexProgram,
                GLRenderProgram? materialProgram,
                EMeshBillboardMode billboardMode)
            {
                bool stereoPass = Engine.Rendering.State.IsStereoPass;
                var cam = Engine.Rendering.State.RenderingCamera;
                if (stereoPass)
                {
                    var rightCam = Engine.Rendering.State.RenderingStereoRightEyeCamera;
                    PassCameraUniforms(vertexProgram, cam, EEngineUniform.LeftEyeInverseViewMatrix, EEngineUniform.LeftEyeProjMatrix);
                    PassCameraUniforms(vertexProgram, rightCam, EEngineUniform.RightEyeInverseViewMatrix, EEngineUniform.RightEyeProjMatrix);
                }
                else
                {
                    PassCameraUniforms(vertexProgram, cam, EEngineUniform.InverseViewMatrix, EEngineUniform.ProjMatrix);
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

                vertexProgram.Uniform(EEngineUniform.VRMode, stereoPass);
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
            private static void PassCameraUniforms(GLRenderProgram vertexProgram, XRCamera? camera, EEngineUniform invView, EEngineUniform proj)
            {
                Matrix4x4 viewMatrix;
                Matrix4x4 inverseViewMatrix;
                Matrix4x4 projMatrix;

                if (camera != null)
                {
                    viewMatrix = camera.Transform.InverseRenderMatrix;
                    inverseViewMatrix = camera.Transform.RenderMatrix;
                    bool useUnjittered = Engine.Rendering.State.RenderingPipelineState?.UseUnjitteredProjection ?? false;
                    projMatrix = useUnjittered ? camera.ProjectionMatrixUnjittered : camera.ProjectionMatrix;
                }
                else
                {
                    viewMatrix = Matrix4x4.Identity;
                    inverseViewMatrix = Matrix4x4.Identity;
                    projMatrix = Matrix4x4.Identity;
                }

                vertexProgram.Uniform($"{EEngineUniform.ViewMatrix}{DefaultVertexShaderGenerator.VertexUniformSuffix}", viewMatrix);
                vertexProgram.Uniform($"{invView}{DefaultVertexShaderGenerator.VertexUniformSuffix}", inverseViewMatrix);
                vertexProgram.Uniform($"{proj}{DefaultVertexShaderGenerator.VertexUniformSuffix}", projMatrix);
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
