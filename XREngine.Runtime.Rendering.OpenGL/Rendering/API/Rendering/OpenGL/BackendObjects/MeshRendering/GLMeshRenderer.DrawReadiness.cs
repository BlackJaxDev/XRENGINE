using static XREngine.Rendering.XRRenderProgram;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        public partial class GLMeshRenderer
        {
            /// <summary>
            /// Gives the draw readiness of this renderer with its own material. It runs the same preparation as
            /// <see cref="TryPrepareForRendering()"/>, which never waits for a link: the program link goes through the
            /// compile and link queue, and with GL_KHR_parallel_shader_compile or GL_ARB_parallel_shader_compile the queue
            /// polls GL_COMPLETION_STATUS. A draw that would use a temporary fallback program while the real program links
            /// is not ready. A program whose backend build failed gives <see cref="ERenderDrawReadiness.Failed"/>.
            /// </summary>
            /// <remarks>
            /// OpenGL links one program set for the material of the renderer. A draw with another material (a material
            /// override) links its programs at the first draw, so this check gives
            /// <see cref="ERenderDrawReadiness.NotSupported"/> for it and does not report a state it cannot see.
            /// </remarks>
            public ERenderDrawReadiness QueryDrawReadiness(XRMaterial? material, out string reason)
            {
                if (material is not null && !ReferenceEquals(material, MeshRenderer.Material))
                {
                    reason = "the OpenGL check covers the material of the renderer only, not a material override";
                    return ERenderDrawReadiness.NotSupported;
                }

                if (TryPrepareForRendering(0.0))
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

                    reason = _lastPrepareResult;
                    return ERenderDrawReadiness.Ready;
                }

                if (TryGetFailedProgramReason(out string failureReason))
                {
                    reason = failureReason;
                    return ERenderDrawReadiness.Failed;
                }

                reason = _lastPrepareResult;
                return _lastPrepareResult switch
                {
                    "GenerateFailed" or "MaterialMissing" or "NoData" => ERenderDrawReadiness.Failed,
                    _ => ERenderDrawReadiness.Compiling,
                };
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
