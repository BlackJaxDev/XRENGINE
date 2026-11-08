using static XREngine.Rendering.XRRenderProgram;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        public partial class GLMeshRenderer
        {
            private const string PrepareResultGenerateFailed = "GenerateFailed";
            private const string PrepareResultMaterialMissing = "MaterialMissing";
            private const string PrepareResultNoData = "NoData";

            /// <summary>
            /// Gives the draw readiness of this renderer with a material. With the material of the renderer, it runs the
            /// same preparation as <see cref="TryPrepareForRendering()"/>. With another material (a material override), it
            /// runs the program step of the draw path for that material (<see cref="GetPrograms"/>), which links the
            /// programs of the override as the draw would. Neither step waits for a link: the program link goes through the
            /// compile and link queue, and with GL_KHR_parallel_shader_compile or GL_ARB_parallel_shader_compile the queue
            /// polls GL_COMPLETION_STATUS.
            /// </summary>
            /// <remarks>
            /// A draw that would use a temporary fallback program while the real program links is not ready. A program
            /// whose backend build failed gives <see cref="ERenderDrawReadiness.Failed"/>. OpenGL programs do not depend on
            /// the render pass, so the view of the query does not change the result.
            /// </remarks>
            public ERenderDrawReadiness QueryDrawReadiness(in RenderDrawReadinessQuery query, out string reason)
            {
                XRMaterial? material = query.Material;
                if (material is not null && !ReferenceEquals(material, MeshRenderer.Material))
                    return QueryOverrideDrawReadiness(material, out reason);

                if (TryPrepareForRendering(0.0))
                    return ReadProgramFallbackState(out reason);

                if (TryGetFailedProgramReason(out string failureReason))
                {
                    reason = failureReason;
                    return ERenderDrawReadiness.Failed;
                }

                reason = _lastPrepareResult;
                return ClassifyPrepareResult(_lastPrepareResult);
            }

            /// <summary>Maps a not-ready preparation result to a draw readiness state.</summary>
            internal static ERenderDrawReadiness ClassifyPrepareResult(string prepareResult)
                => prepareResult switch
                {
                    PrepareResultGenerateFailed or PrepareResultMaterialMissing or PrepareResultNoData => ERenderDrawReadiness.Failed,
                    _ => ERenderDrawReadiness.Compiling,
                };

            private ERenderDrawReadiness QueryOverrideDrawReadiness(XRMaterial material, out string reason)
            {
                if (Data is null)
                {
                    reason = PrepareResultNoData;
                    return ERenderDrawReadiness.Failed;
                }

                // The readiness check never generates inline on the render thread: the generation queue does it, with its
                // budget and its priority, as for a queued draw.
                if (!IsGenerated)
                {
                    Renderer.MeshGenerationQueue.EnqueueGeneration(this);
                    reason = "the renderer waits in the mesh generation queue";
                    return ERenderDrawReadiness.Compiling;
                }

                EnsureProgramsMatchRenderSettings();
                EnsureProgramsMatchMaterialShaderState();
                GLMaterial glMaterial = GetRenderMaterial(material);
                if (!GetPrograms(glMaterial, out _, out _))
                {
                    if (TryGetFailedProgramReason(out string failureReason))
                    {
                        reason = failureReason;
                        return ERenderDrawReadiness.Failed;
                    }

                    reason = "the programs of the material override link";
                    return ERenderDrawReadiness.Compiling;
                }

                if (!AreBuffersReadyForRendering())
                {
                    reason = "BuffersNotReady";
                    return ERenderDrawReadiness.Compiling;
                }

                return ReadProgramFallbackState(out reason);
            }

            private ERenderDrawReadiness ReadProgramFallbackState(out string reason)
            {
                if (_combinedProgram is { IsAsyncBuildPending: true })
                {
                    reason = "the combined program links; a temporary program draws in its place";
                    return ERenderDrawReadiness.Compiling;
                }

                if (_combinedProgramMaterialKey is { } programMaterial && IsPendingUberFallbackMaterial(programMaterial))
                {
                    reason = "the uber variant builds; a fallback material draws in its place";
                    return ERenderDrawReadiness.Compiling;
                }

                reason = "Ready";
                return ERenderDrawReadiness.Ready;
            }

            /// <summary>Gets the reason when a program of this renderer has a failed backend build.</summary>
            private bool TryGetFailedProgramReason(out string reason)
            {
                if (TryGetFailedProgramReason(_combinedProgram, out reason))
                    return true;
                return TryGetFailedProgramReason(_separatedVertexProgram, out reason);
            }

            private static bool TryGetFailedProgramReason(GLRenderProgram? program, out string reason)
            {
                if (program is not null)
                {
                    ShaderProgramBackendStatus status = program.Data.ShaderMetadata.Backend;
                    if (status.Stage is EShaderProgramBackendStage.Failed)
                    {
                        reason = status.FailureReason ?? "the OpenGL program build failed";
                        return true;
                    }
                }

                reason = string.Empty;
                return false;
            }
        }
    }
}
