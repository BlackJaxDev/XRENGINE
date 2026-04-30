using Silk.NET.OpenGL;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using XREngine.Rendering.Shaders;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        /// <summary>
        /// Dispatches shader compilation and program linking to a shared GL context thread.
        /// This eliminates main-thread stalls on drivers that lack
        /// <c>GL_ARB_parallel_shader_compile</c> by performing all blocking GL compiler work
        /// on the background thread, then synchronizing via <c>glFinish()</c>.
        /// <para/>
        /// Shader objects are created, sourced, compiled, attached, and destroyed entirely
        /// on the shared context (they share the same object namespace via GLFW context sharing).
        /// The program object (identified by <paramref name="programId"/>) must already exist —
        /// it was created on the main thread.
        /// </summary>
        public sealed class GLProgramCompileLinkQueue(GLSharedContext sharedContext)
        {
            private readonly GLSharedContext _sharedContext = sharedContext;
            private readonly ConcurrentDictionary<uint, CompileResult> _completed = new();
            private int _inFlight;

            /// <summary>
            /// Compilation is heavier than binary upload; keep in-flight count lower
            /// to avoid GPU memory pressure and driver contention.
            /// </summary>
            public const int MaxInFlight = 4;

            public bool IsAvailable => _sharedContext.IsRunning;
            public bool CanEnqueue => Volatile.Read(ref _inFlight) < MaxInFlight;
            public int InFlightCount => Volatile.Read(ref _inFlight);

            public enum CompileStatus : byte
            {
                Success,
                CompileFailed,
                LinkFailed,
            }

            public readonly record struct ShaderInput(string ResolvedSource, ShaderType Type);

            public readonly record struct CompileResult(
                CompileStatus Status,
                string? ErrorLog,
                double CompileMilliseconds,
                double LinkMilliseconds);

            /// <summary>
            /// Queues a full compile → attach → link pipeline on the shared context thread.
            /// The program object <paramref name="programId"/> must already be created on the main thread.
            /// Shader objects are created and destroyed on the shared context; only the linked
            /// program survives.
            /// </summary>
            public void EnqueueCompileAndLink(uint programId, ShaderInput[] shaders)
            {
                Interlocked.Increment(ref _inFlight);
                _sharedContext.Enqueue(gl =>
                {
                    long compileStartTimestamp = Stopwatch.GetTimestamp();
                    uint[] shaderIds = new uint[shaders.Length];
                    bool allCompiled = true;
                    string? errorLog = null;

                    for (int i = 0; i < shaders.Length; i++)
                    {
                        ref readonly ShaderInput input = ref shaders[i];
                        uint sid = gl.CreateShader(input.Type);
                        shaderIds[i] = sid;

                        gl.ShaderSource(sid, input.ResolvedSource);
                        gl.CompileShader(sid);

                        gl.GetShader(sid, ShaderParameterName.CompileStatus, out int status);
                        if (status == 0)
                        {
                            gl.GetShaderInfoLog(sid, out string? info);
                            errorLog = info;
                            allCompiled = false;

                            // Clean up all created shaders so far.
                            for (int j = 0; j <= i; j++)
                                gl.DeleteShader(shaderIds[j]);
                            break;
                        }
                    }

                    if (!allCompiled)
                    {
                        double compileMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - compileStartTimestamp);
                        _completed[programId] = new CompileResult(CompileStatus.CompileFailed, errorLog, compileMilliseconds, 0.0);
                        return;
                    }

                    double compileMillisecondsCompleted = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - compileStartTimestamp);

                    // Attach all compiled shaders to the program.
                    for (int i = 0; i < shaderIds.Length; i++)
                        gl.AttachShader(programId, shaderIds[i]);

                    long linkStartTimestamp = Stopwatch.GetTimestamp();
                    gl.LinkProgram(programId);
                    gl.GetProgram(programId, ProgramPropertyARB.LinkStatus, out int linkStatus);

                    string? linkError = null;
                    if (linkStatus == 0)
                        gl.GetProgramInfoLog(programId, out linkError);

                    // Detach and delete shader objects — no longer needed after linking.
                    for (int i = 0; i < shaderIds.Length; i++)
                    {
                        gl.DetachShader(programId, shaderIds[i]);
                        gl.DeleteShader(shaderIds[i]);
                    }

                    // Synchronize so the linked program is usable on the main context.
                    gl.Finish();

                    double linkMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - linkStartTimestamp);

                    _completed[programId] = new CompileResult(
                        linkStatus != 0 ? CompileStatus.Success : CompileStatus.LinkFailed,
                        linkError,
                        compileMillisecondsCompleted,
                        linkMilliseconds);
                });
            }

            /// <summary>
            /// Checks whether an async compile+link has completed for the given program.
            /// The result is consumed (removed) on retrieval, freeing an in-flight slot.
            /// </summary>
            public bool TryGetResult(uint programId, out CompileResult result)
            {
                if (_completed.TryRemove(programId, out result))
                {
                    Interlocked.Decrement(ref _inFlight);
                    return true;
                }
                return false;
            }

            /// <summary>
            /// Maps engine shader types to Silk.NET GL shader types.
            /// Duplicated from GLShader to avoid coupling the background queue to GL wrapper objects.
            /// </summary>
            public static ShaderType ToGLShaderType(EShaderType mode)
                => mode switch
                {
                    EShaderType.Vertex => ShaderType.VertexShader,
                    EShaderType.Fragment => ShaderType.FragmentShader,
                    EShaderType.Geometry => ShaderType.GeometryShader,
                    EShaderType.TessControl => ShaderType.TessControlShader,
                    EShaderType.TessEvaluation => ShaderType.TessEvaluationShader,
                    EShaderType.Compute => ShaderType.ComputeShader,
                    EShaderType.Task => (ShaderType)0x955A,
                    EShaderType.Mesh => (ShaderType)0x9559,
                    _ => ShaderType.FragmentShader
                };

            private static double StopwatchTicksToMilliseconds(long ticks)
                => ticks <= 0L ? 0.0 : ticks * 1000.0 / Stopwatch.Frequency;
        }
    }
}
