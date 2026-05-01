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

            private void EnsureProgramsMatchRenderSettings()
            {
                var settingsVersion = Engine.Rendering.Settings.ShaderConfigVersion;
                if (_shaderConfigVersion == settingsVersion)
                    return;

                _shaderConfigVersion = settingsVersion;

                _combinedProgram?.Destroy();
                _combinedProgram = null;

                _separatedVertexProgram?.Destroy();
                _separatedVertexProgram = null;

                _forcedGeneratedVertexProgram?.Destroy();
                _forcedGeneratedVertexProgram = null;

                if (!Engine.Rendering.Settings.CalculateSkinningInComputeShader
                    && !Engine.Rendering.Settings.CalculateBlendshapesInComputeShader)
                {
                    DestroySkinnedBuffers();
                }

                BuffersBound = false;
                GenProgramsAndBuffers();
            }

            public bool IsPreparedForRendering
                => IsGenerated
                && _shaderConfigVersion == Engine.Rendering.Settings.ShaderConfigVersion
                && BuffersBound
                && AreBuffersReadyForRendering();

            public bool TryPrepareForRendering()
            {
                using var prof = Engine.Profiler.Start("GLMeshRenderer.TryPrepareForRendering");

                if (Data is null)
                    return false;

                if (!IsGenerated)
                {
                    Generate();
                    if (!IsGenerated)
                        return false;
                }

                EnsureProgramsMatchRenderSettings();

                if (_combinedProgram is null && _separatedVertexProgram is null)
                    GenProgramsAndBuffers();

                GLMaterial? material = Material;
                if (material is null)
                    return false;

                if (!GetPrograms(material, out var vertexProgram, out var materialProgram))
                    return false;

                ConfigureDrawTopology(vertexProgram!, materialProgram);

                if (!BuffersBound)
                    BindBuffers(vertexProgram!);

                if (BuffersBound)
                    PrepareDynamicRenderData();

                return BuffersBound && AreBuffersReadyForRendering();
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
                bool forceShaderPipelines = Engine.Rendering.State.RenderingPipelineState?.ForceShaderPipelines ?? false;
                bool materialDiffers = !ReferenceEquals(material, Material);
                bool usePipelines = (Engine.Rendering.Settings.AllowShaderPipelines && Data.AllowShaderPipelines)
                    || forceShaderPipelines
                    || materialDiffers;
                
                return usePipelines
                    ? GetPipelinePrograms(material, out vertexProgram, out materialProgram)
                    : GetCombinedProgram(out vertexProgram, out materialProgram);
            }

            /// <summary>
            /// Get the combined program when pipeline mode is disabled.
            /// </summary>
            private bool GetCombinedProgram(out GLRenderProgram? vertexProgram, out GLRenderProgram? materialProgram)
            {
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

                // Unbind any stale program pipeline left from a previous force-pipeline pass
                // so the combined program takes full effect.
                Api.BindProgramPipeline(0);

                vertexProgram.Use();
                Dbg("GetCombinedProgram: linked & in use", "Programs");
                return true;
            }

            /// <summary>
            /// Get the separable vertex/material programs when pipeline mode is enabled.
            /// </summary>
            private bool GetPipelinePrograms(GLMaterial material, out GLRenderProgram? vertexProgram, out GLRenderProgram? materialProgram)
            {
                // OpenGL spec requires glUseProgram(0) before a program pipeline can take effect.
                // Without this, any previously active combined program overrides the pipeline.
                Api.UseProgram(0);

                _pipeline ??= Renderer.GenericToAPI<GLRenderProgramPipeline>(new XRRenderProgramPipeline())!;
                _pipeline.Bind();
                _pipeline.Clear(EProgramStageMask.AllShaderBits);

                // When AllowShaderPipelines is globally disabled, materials skip creating their
                // ShaderPipelineProgram. Create it on-demand when a pass force-enables pipelines.
                material.Data.EnsureShaderPipelineProgram();

                materialProgram = material.SeparableProgram;
                var mask = materialProgram?.Data?.GetShaderTypeMask() ?? EProgramStageMask.None;
                bool includesVertexShader = mask.HasFlag(EProgramStageMask.VertexShaderBit);

                bool result = includesVertexShader
                    ? UseSuppliedVertexShader(out vertexProgram, materialProgram, mask)
                    : GenerateVertexShader(out vertexProgram, materialProgram, mask);

                if (result)
                    _pipeline.Validate();

                return result;
            }

            /// <summary>
            /// Use the material-provided vertex shader when available.
            /// </summary>
            private bool UseSuppliedVertexShader(out GLRenderProgram? vertexProgram, GLRenderProgram? materialProgram, EProgramStageMask mask)
            {
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
                var renderState = Engine.Rendering.State.RenderingPipelineState;
                bool pointLightShadowPass = renderState?.ShadowPass == true
                    && renderState.GlobalMaterialOverride is XRMaterial globalMaterialOverride
                    && UsesPointLightShadowCubemap(globalMaterialOverride);
                bool forceGeneratedVertexProgram = (renderState?.ForceGeneratedVertexProgram ?? false) || pointLightShadowPass;
                vertexProgram = forceGeneratedVertexProgram
                    ? GetForcedGeneratedVertexProgram()
                    : GetOrCreateSeparatedVertexProgram();

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

            private GLRenderProgram? GetOrCreateSeparatedVertexProgram()
            {
                if (_separatedVertexProgram is not null)
                    return _separatedVertexProgram;

                var material = Material;
                if (material is null)
                    return null;

                bool hasNoVertexShaders = material.Data.VertexShaders.Count == 0;

                CreateSeparatedVertexProgram(
                    ref _separatedVertexProgram,
                    hasNoVertexShaders,
                    material.Data.VertexShaders,
                    Data.VertexShaderSelector,
                    () => Data.VertexShaderSource ?? string.Empty);

                return _separatedVertexProgram;
            }

            private GLRenderProgram? GetForcedGeneratedVertexProgram()
            {
                if (_forcedGeneratedVertexProgram is not null)
                    return _forcedGeneratedVertexProgram;

                CreateSeparatedVertexProgram(
                    ref _forcedGeneratedVertexProgram,
                    true,
                    Array.Empty<XRShader>(),
                    Data.VertexShaderSelector,
                    () => Data.VertexShaderSource ?? string.Empty);

                return _forcedGeneratedVertexProgram;
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

                var combinedData = new XRRenderProgram(false, false, shaders)
                {
                    Name = $"Combined:{MeshRenderer.Material?.Name ?? "unknown"}",
                };
                program = Renderer.GenericToAPI<GLRenderProgram>(combinedData)!;
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

                var separatedData = new XRRenderProgram(false, true, vertexShader)
                {
                    Name = $"SeparatedVertex:{MeshRenderer.Material?.Name ?? "unknown"}",
                };
                vertexProgram = Renderer.GenericToAPI<GLRenderProgram>(separatedData)!;
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
            /// Pre-computes hash + cache lookup before issuing GL work to minimize render-thread stalls.
            /// </summary>
            private void InitiateLink(GLRenderProgram vertexProgram)
            {
                using var prof = Engine.Profiler.Start("GLMeshRenderer.InitiateLink");
                vertexProgram.Data.AllowLink();
                vertexProgram.BeginPrepareLinkData();
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
                    Engine.EnqueueMainThreadTask(() => BindBuffers(program), "GLMeshRenderer.BindBuffers");
                else
                    BindBuffers(program);
            }
        }
    }
}
