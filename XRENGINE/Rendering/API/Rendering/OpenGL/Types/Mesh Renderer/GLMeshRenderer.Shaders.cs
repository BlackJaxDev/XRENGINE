using System.Diagnostics.CodeAnalysis;
using XREngine.Data.Core;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        public partial class GLMeshRenderer
        {
            /// <summary>
            /// Rebuild shader programs and attribute bindings when material or settings change.
            /// </summary>
            private void GenProgramsAndBuffers()
            {
                using var prof = Engine.Profiler.Start("GLMeshRenderer.GenProgramsAndBuffers");
                BuffersBound = false;

                var material = Material;
                if (material is null)
                {
                    _combinedProgram?.Destroy();
                    _combinedProgram = null;

                    _separatedVertexProgram?.Destroy();
                    _separatedVertexProgram = null;
                    return;
                }

                Dbg("GenProgramsAndBuffers start", "Programs");
                MakeIndexBuffers();

                bool hasNoVertexShaders = (material.Data.VertexShaders.Count) == 0;

                CollectBuffers();
                Dbg($"Collected {_bufferCache.Count} buffer(s)", "Buffers");

                if (Engine.Rendering.Settings.AllowShaderPipelines && Data.AllowShaderPipelines)
                {
                    _combinedProgram = null;

                    IEnumerable<XRShader> shaders = material.Data.VertexShaders;
                    CreateSeparatedVertexProgram(
                        ref _separatedVertexProgram,
                        hasNoVertexShaders,
                        shaders,
                        Data.VertexShaderSelector,
                        () => Data.VertexShaderSource ?? string.Empty);

                    Dbg("GenProgramsAndBuffers: pipeline mode - separated vertex program initiated", "Programs");
                }
                else
                {
                    _separatedVertexProgram = null;

                    IEnumerable<XRShader> shaders = material.Data.Shaders;
                    CreateCombinedProgram(
                        ref _combinedProgram,
                        hasNoVertexShaders,
                        shaders,
                        Data.VertexShaderSelector,
                        () => Data.VertexShaderSource ?? string.Empty);

                    Dbg("GenProgramsAndBuffers: combined program initiated", "Programs");
                }
            }

            /// <summary>
            /// Get the appropriate vertex/material programs based on engine settings.
            /// When ForceShaderPipelines is enabled (e.g., during motion vectors pass with material override),
            /// pipeline mode is used regardless of global settings to ensure override materials work correctly.
            /// </summary>
            private bool GetPrograms(
                GLMaterial material,
                [MaybeNullWhen(false)] out GLRenderProgram? vertexProgram,
                [MaybeNullWhen(false)] out GLRenderProgram? materialProgram)
            {
                using var prof = Engine.Profiler.Start("GLMeshRenderer.GetPrograms");
                bool forceShaderPipelines = Engine.Rendering.State.RenderingPipelineState?.ForceShaderPipelines ?? false;
                bool usePipelines = (Engine.Rendering.Settings.AllowShaderPipelines && Data.AllowShaderPipelines) || forceShaderPipelines;
                
                return usePipelines
                    ? GetPipelinePrograms(material, out vertexProgram, out materialProgram)
                    : GetCombinedProgram(out vertexProgram, out materialProgram);
            }

            /// <summary>
            /// Get the combined program when pipeline mode is disabled.
            /// </summary>
            private bool GetCombinedProgram(out GLRenderProgram? vertexProgram, out GLRenderProgram? materialProgram)
            {
                using var prof = Engine.Profiler.Start("GLMeshRenderer.GetCombinedProgram");
                if ((vertexProgram = materialProgram = _combinedProgram) is null)
                {
                    Dbg("GetCombinedProgram: program null", "Programs");
                    return false;
                }

                if (!vertexProgram.Link())
                {
                    vertexProgram = null;
                    Dbg("GetCombinedProgram: link failed", "Programs");
                    return false;
                }

                vertexProgram.Use();
                Dbg("GetCombinedProgram: linked & in use", "Programs");
                return true;
            }

            /// <summary>
            /// Get the separable vertex/material programs when pipeline mode is enabled.
            /// </summary>
            private bool GetPipelinePrograms(GLMaterial material, out GLRenderProgram? vertexProgram, out GLRenderProgram? materialProgram)
            {
                using var prof = Engine.Profiler.Start("GLMeshRenderer.GetPipelinePrograms");
                _pipeline ??= Renderer.GenericToAPI<GLRenderProgramPipeline>(new XRRenderProgramPipeline())!;
                _pipeline.Bind();
                _pipeline.Clear(EProgramStageMask.AllShaderBits);

                materialProgram = material.SeparableProgram;
                var mask = materialProgram?.Data?.GetShaderTypeMask() ?? EProgramStageMask.None;
                bool includesVertexShader = mask.HasFlag(EProgramStageMask.VertexShaderBit);

                return includesVertexShader
                    ? UseSuppliedVertexShader(out vertexProgram, materialProgram, mask)
                    : GenerateVertexShader(out vertexProgram, materialProgram, mask);
            }

            /// <summary>
            /// Use the material-provided vertex shader when available.
            /// </summary>
            private bool UseSuppliedVertexShader(out GLRenderProgram? vertexProgram, GLRenderProgram? materialProgram, EProgramStageMask mask)
            {
                using var prof = Engine.Profiler.Start("GLMeshRenderer.UseSuppliedVertexShader");
                vertexProgram = materialProgram;
                if (materialProgram?.Link() ?? false)
                {
                    _pipeline!.Set(mask, materialProgram);
                    Dbg("UseSuppliedVertexShader: material vertex shader linked & set", "Programs");
                    return true;
                }

                Dbg("UseSuppliedVertexShader: link failed", "Programs");
                return false;
            }

            /// <summary>
            /// Generate a default vertex shader when the material does not provide one.
            /// </summary>
            /// <param name="vertexProgram"></param>
            /// <param name="materialProgram"></param>
            /// <param name="mask"></param>
            /// <returns></returns>
            private bool GenerateVertexShader(out GLRenderProgram? vertexProgram, GLRenderProgram? materialProgram, EProgramStageMask mask)
            {
                using var prof = Engine.Profiler.Start("GLMeshRenderer.GenerateVertexShader");
                vertexProgram = _separatedVertexProgram;

                if (materialProgram?.Link() ?? false)
                    _pipeline!.Set(mask, materialProgram);
                else
                {
                    Dbg("GenerateVertexShader: material program link failed", "Programs");
                    return false;
                }

                if (vertexProgram?.Link() ?? false)
                    _pipeline!.Set(EProgramStageMask.VertexShaderBit, vertexProgram);
                else
                {
                    Dbg("GenerateVertexShader: vertex program link failed", "Programs");
                    return false;
                }

                Dbg("GenerateVertexShader: success", "Programs");
                return true;
            }

            /// <summary>
            /// Build a single combined program when pipeline mode is disabled.
            /// </summary>
            private void CreateCombinedProgram(
                ref GLRenderProgram? program,
                bool hasNoVertexShaders,
                IEnumerable<XRShader> shaders,
                Func<XRShader, bool> vertexShaderSelector,
                Func<string> vertexSourceGenerator)
            {
                using var prof = Engine.Profiler.Start("GLMeshRenderer.CreateCombinedProgram");
                XRShader vertexShader = hasNoVertexShaders
                    ? GenerateVertexShader(vertexSourceGenerator)
                    : FindVertexShader(shaders, vertexShaderSelector) ?? GenerateVertexShader(vertexSourceGenerator);

                if (!hasNoVertexShaders)
                    shaders = shaders.Where(x => x.Type != EShaderType.Vertex);

                shaders = shaders.Append(vertexShader);

                program = Renderer.GenericToAPI<GLRenderProgram>(new XRRenderProgram(false, false, shaders))!;
                program.PropertyChanged += CheckProgramLinked;
                InitiateLink(program);
            }

            /// <summary>
            /// Find a vertex shader in the provided collection using the given selector.
            /// </summary>
            private static XRShader? FindVertexShader(IEnumerable<XRShader> shaders, Func<XRShader, bool> vertexShaderSelector)
                => shaders.FirstOrDefault(x => x.Type == EShaderType.Vertex && vertexShaderSelector(x));

            /// <summary>
            /// Build the separable vertex program when pipeline mode is enabled.
            /// </summary>
            private void CreateSeparatedVertexProgram(
                ref GLRenderProgram? vertexProgram,
                bool hasNoVertexShaders,
                IEnumerable<XRShader> vertexShaders,
                Func<XRShader, bool> vertexShaderSelector,
                Func<string> vertexSourceGenerator)
            {
                using var prof = Engine.Profiler.Start("GLMeshRenderer.CreateSeparatedVertexProgram");
                XRShader vertexShader = hasNoVertexShaders
                    ? GenerateVertexShader(vertexSourceGenerator)
                    : vertexShaders.FirstOrDefault(vertexShaderSelector) ?? GenerateVertexShader(vertexSourceGenerator);

                vertexProgram = Renderer.GenericToAPI<GLRenderProgram>(new XRRenderProgram(false, true, vertexShader))!;
                vertexProgram.PropertyChanged += CheckProgramLinked;
                InitiateLink(vertexProgram);
            }

            /// <summary>
            /// Generate a simple default vertex shader.
            /// </summary>
            private static XRShader GenerateVertexShader(Func<string> vertexSourceGenerator)
                => new(EShaderType.Vertex, vertexSourceGenerator());

            /// <summary>
            /// Start linking the provided program, either synchronously or asynchronously.
            /// </summary>
            private void InitiateLink(GLRenderProgram vertexProgram)
            {
                using var prof = Engine.Profiler.Start("GLMeshRenderer.InitiateLink");
                vertexProgram.Data.AllowLink();
                if (!Data.Parent.GenerateAsync)
                    vertexProgram.Link();
            }

            /// <summary>
            /// Once a program links, bind attributes on the main thread if needed.
            /// </summary>
            private void CheckProgramLinked(object? sender, IXRPropertyChangedEventArgs e)
            {
                using var prof = Engine.Profiler.Start("GLMeshRenderer.CheckProgramLinked");
                GLRenderProgram? program = sender as GLRenderProgram;
                if (e.PropertyName != nameof(GLRenderProgram.IsLinked) || !(program?.IsLinked ?? false))
                    return;

                Dbg("CheckProgramLinked: program linked - binding buffers", "Programs");
                program.PropertyChanged -= CheckProgramLinked;

                if (MeshRenderer.GenerateAsync)
                    Engine.EnqueueMainThreadTask(() => BindBuffers(program));
                else
                    BindBuffers(program);
            }
        }
    }
}
