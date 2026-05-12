using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using XREngine.Data.Core;
using XREngine.Data.Profiling;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        public partial class GLMeshRenderer
        {
            private const double SlowTryPrepareLogThresholdMs = 50.0;

            /// <summary>
            /// Rebuild shader programs and attribute bindings when material or settings change.
            /// </summary>
            private void GenProgramsAndBuffers()
            {
                using var prof = Engine.Profiler.Start("GLMeshRenderer.GenProgramsAndBuffers", ProfilerScopeKind.OneOffInvoke);
                BuffersBound = false;
                System.Threading.Interlocked.Increment(ref _programGenerationCount);

                PrepareUberVariantForCurrentMaterial();

                var material = Material;
                if (material is null)
                {
                    _combinedProgram?.Destroy();
                    _combinedProgram = null;

                    _separatedVertexProgram?.Destroy();
                    _separatedVertexProgram = null;
                    CaptureMaterialShaderState();
                    return;
                }

                CaptureMaterialShaderState();
                Dbg("GenProgramsAndBuffers start", "Programs");

                bool hasNoVertexShaders = (material.Data.VertexShaders.Count) == 0;

                EnsureRuntimeDeformationBuffers();
                CollectBuffers();
                Dbg($"Collected {_bufferCache.Count} buffer(s)", "Buffers");

                bool forceSeparableUber = ShouldForceSeparableUberProgram(material);
                if ((Engine.Rendering.Settings.AllowShaderPipelines && Data.AllowShaderPipelines)
                    || forceSeparableUber)
                {
                    _combinedProgram?.Destroy();
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
                    _separatedVertexProgram?.Destroy();
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

            private void EnsureRuntimeDeformationBuffers()
            {
                XRMesh? mesh = Mesh;
                if (mesh is null)
                    return;

                if (mesh.HasSkinning && Engine.Rendering.Settings.AllowSkinning)
                    MeshRenderer.EnsureSkinningBuffers(logWarnings: false);

                if (mesh.HasBlendshapes && Engine.Rendering.Settings.AllowBlendshapes)
                    MeshRenderer.EnsureBlendshapeBuffers(logWarnings: false);
            }

            private void EnsureProgramsMatchRenderSettings()
            {
                var settingsVersion = Engine.Rendering.Settings.ShaderConfigVersion;
                if (_shaderConfigVersion == settingsVersion)
                    return;

                _shaderConfigVersion = settingsVersion;
                System.Threading.Interlocked.Increment(ref _programDestructionCount);

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
                System.Threading.Interlocked.Increment(ref _genCallSiteEnsureSettings);
                GenProgramsAndBuffers();
            }

            private void EnsureProgramsMatchMaterialShaderState()
            {
                PrepareUberVariantForCurrentMaterial();

                XRMaterial? material = MeshRenderer.Material;
                long shaderStateRevision = material?.ShaderStateRevision ?? 0;
                if (ReferenceEquals(_programMaterialStateKey, material) &&
                    _programMaterialShaderStateRevision == shaderStateRevision)
                {
                    return;
                }

                _programMaterialStateKey = material;
                _programMaterialShaderStateRevision = shaderStateRevision;
                System.Threading.Interlocked.Increment(ref _programDestructionCount);

                _combinedProgram?.Destroy();
                _combinedProgram = null;

                _separatedVertexProgram?.Destroy();
                _separatedVertexProgram = null;

                _forcedGeneratedVertexProgram?.Destroy();
                _forcedGeneratedVertexProgram = null;

                _pipeline?.Destroy();
                _pipeline = null;

                Data.ResetVertexShaderSource();
                BuffersBound = false;
            }

            private void PrepareUberVariantForCurrentMaterial()
            {
                if (Engine.Rendering.State.IsShadowPass)
                    return;

                MeshRenderer.Material?.EnsureUberVariantPreparedForRendering();
            }

            private void CaptureMaterialShaderState()
            {
                XRMaterial? material = MeshRenderer.Material;
                _programMaterialStateKey = material;
                _programMaterialShaderStateRevision = material?.ShaderStateRevision ?? 0;
            }

            public bool IsPreparedForRendering
                => IsGenerated
                && _shaderConfigVersion == Engine.Rendering.Settings.ShaderConfigVersion
                && BuffersBound
                && !VertexArrayBindingsStale()
                && AreBuffersReadyForRendering();

            /// <inheritdoc />
            public bool TryPrepareForRendering(out string reason)
            {
                bool ok = TryPrepareForRendering(0.0);
                reason = _lastPrepareResult;
                return ok;
            }

            public bool TryPrepareForRendering()
                => TryPrepareForRendering(0.0);

            /// <summary>
            /// Prepares this renderer with an optional defer-on-overrun budget.
            /// When <paramref name="deferOverrunBudgetMs"/> is greater than zero,
            /// the method bails out early with <c>_lastPrepareResult = "DeferredOverrun"</c>
            /// once the accumulated stage timers exceed the budget, so the caller can
            /// requeue the renderer for the next frame instead of compounding
            /// expensive first-use shader work into a single render-thread stall.
            /// A budget of <c>0.0</c> disables the check and matches historical behaviour.
            /// </summary>
            public bool TryPrepareForRendering(double deferOverrunBudgetMs)
            {
                using var prof = Engine.Profiler.Start("GLMeshRenderer.TryPrepareForRendering", ProfilerScopeKind.ConditionalLoop);
                long methodStart = Stopwatch.GetTimestamp();
                double generateMs = 0.0;
                double ensureRenderSettingsMs = 0.0;
                double ensureMaterialStateMs = 0.0;
                double genProgramsAndBuffersMs = 0.0;
                double materialLookupMs = 0.0;
                double getProgramsMs = 0.0;
                double configureDrawTopologyMs = 0.0;
                double bindBuffersMs = 0.0;
                double dynamicRenderDataMs = 0.0;
                bool budgetActive = deferOverrunBudgetMs > 0.0;
                _lastDeferOverrunMs = 0.0;

                if (Data is null)
                {
                    _lastPrepareResult = "NoData";
                    return false;
                }

                if (!IsGenerated)
                {
                    long generateStart = Stopwatch.GetTimestamp();
                    Generate();
                    generateMs = ElapsedMilliseconds(generateStart);
                    if (!IsGenerated)
                    {
                        LogSlowTryPrepare(
                            "GenerateFailed",
                            ElapsedMilliseconds(methodStart),
                            generateMs,
                            ensureRenderSettingsMs,
                            ensureMaterialStateMs,
                            genProgramsAndBuffersMs,
                            materialLookupMs,
                            getProgramsMs,
                            configureDrawTopologyMs,
                            bindBuffersMs,
                            dynamicRenderDataMs);
                        _lastPrepareResult = "GenerateFailed";
                        return false;
                    }
                }

                long stageStart = Stopwatch.GetTimestamp();
                EnsureProgramsMatchRenderSettings();
                ensureRenderSettingsMs = ElapsedMilliseconds(stageStart);
                stageStart = Stopwatch.GetTimestamp();
                EnsureProgramsMatchMaterialShaderState();
                ensureMaterialStateMs = ElapsedMilliseconds(stageStart);

                if (_combinedProgram is null && _separatedVertexProgram is null)
                {
                    stageStart = Stopwatch.GetTimestamp();
                    System.Threading.Interlocked.Increment(ref _genCallSiteTryPrepareNull);
                    GenProgramsAndBuffers();
                    genProgramsAndBuffersMs = ElapsedMilliseconds(stageStart);
                }

                if (budgetActive)
                {
                    double accumulated = ensureRenderSettingsMs + ensureMaterialStateMs + genProgramsAndBuffersMs;
                    if (accumulated > deferOverrunBudgetMs)
                    {
                        _lastDeferOverrunMs = accumulated;
                        LogSlowTryPrepare(
                            "DeferredOverrun",
                            ElapsedMilliseconds(methodStart),
                            generateMs,
                            ensureRenderSettingsMs,
                            ensureMaterialStateMs,
                            genProgramsAndBuffersMs,
                            materialLookupMs,
                            getProgramsMs,
                            configureDrawTopologyMs,
                            bindBuffersMs,
                            dynamicRenderDataMs);
                        _lastPrepareResult = "DeferredOverrun";
                        _lastPrepareDetail = string.Empty;
                        return false;
                    }
                }

                stageStart = Stopwatch.GetTimestamp();
                GLMaterial? material = Material;
                materialLookupMs = ElapsedMilliseconds(stageStart);
                if (material is null)
                {
                    LogSlowTryPrepare(
                        "MaterialMissing",
                        ElapsedMilliseconds(methodStart),
                        generateMs,
                        ensureRenderSettingsMs,
                        ensureMaterialStateMs,
                        genProgramsAndBuffersMs,
                        materialLookupMs,
                        getProgramsMs,
                        configureDrawTopologyMs,
                        bindBuffersMs,
                        dynamicRenderDataMs);
                    _lastPrepareResult = "MaterialMissing";
                    return false;
                }

                stageStart = Stopwatch.GetTimestamp();
                if (!GetPrograms(material, out var vertexProgram, out var materialProgram))
                {
                    getProgramsMs = ElapsedMilliseconds(stageStart);
                    LogSlowTryPrepare(
                        "ProgramsPending",
                        ElapsedMilliseconds(methodStart),
                        generateMs,
                        ensureRenderSettingsMs,
                        ensureMaterialStateMs,
                        genProgramsAndBuffersMs,
                        materialLookupMs,
                        getProgramsMs,
                        configureDrawTopologyMs,
                        bindBuffersMs,
                        dynamicRenderDataMs);
                    _lastPrepareResult = "ProgramsPending";
                    _lastPrepareDetail = BuildProgramsPendingDetail(material);
                    return false;
                }
                getProgramsMs = ElapsedMilliseconds(stageStart);

                stageStart = Stopwatch.GetTimestamp();
                ConfigureDrawTopology(vertexProgram!, materialProgram);
                configureDrawTopologyMs = ElapsedMilliseconds(stageStart);

                if (BuffersBound && VertexArrayBindingsStale())
                    BuffersBound = false;

                if (!BuffersBound)
                {
                    stageStart = Stopwatch.GetTimestamp();
                    BindBuffers(vertexProgram!);
                    bindBuffersMs = ElapsedMilliseconds(stageStart);
                }

                if (BuffersBound)
                {
                    stageStart = Stopwatch.GetTimestamp();
                    PrepareDynamicRenderData();
                    dynamicRenderDataMs = ElapsedMilliseconds(stageStart);
                }

                bool buffersBound = BuffersBound;
                bool buffersReady = buffersBound && AreBuffersReadyForRendering();
                bool ready = buffersReady;
                _lastPrepareResult = ready
                    ? "Ready"
                    : (buffersBound ? "BuffersNotReady" : "BuffersPending");
                _lastPrepareDetail = string.Empty;
                LogSlowTryPrepare(
                    ready ? "Ready" : "BuffersPending",
                    ElapsedMilliseconds(methodStart),
                    generateMs,
                    ensureRenderSettingsMs,
                    ensureMaterialStateMs,
                    genProgramsAndBuffersMs,
                    materialLookupMs,
                    getProgramsMs,
                    configureDrawTopologyMs,
                    bindBuffersMs,
                    dynamicRenderDataMs);
                return ready;
            }

            private string BuildProgramsPendingDetail(GLMaterial material)
            {
                var xrMaterial = MeshRenderer.Material;
                int vsCount = xrMaterial?.VertexShaders?.Count ?? 0;
                int shCount = xrMaterial?.Shaders?.Count ?? 0;
                long matRev = xrMaterial?.ShaderStateRevision ?? 0;
                int settingsVer = Engine.Rendering.Settings.ShaderConfigVersion;
                bool forceShaderPipelines = Engine.Rendering.State.RenderingPipelineState?.ForceShaderPipelines ?? false;
                bool allowPipelines = Engine.Rendering.Settings.AllowShaderPipelines && Data.AllowShaderPipelines;
                string versionType = Data?.GetType()?.Name ?? "<null>";
                string vsSel = Data?.VertexShaderSelector?.Method?.Name ?? "<null>";

                return string.Concat(
                    "mat='", xrMaterial?.Name ?? "<null>", "'",
                    " inst#", _instanceId.ToString(),
                    " progGen=", _programGenerationCount.ToString(),
                    " progDestroy=", _programDestructionCount.ToString(),
                    " genSites[settings=", _genCallSiteEnsureSettings.ToString(),
                    ",tryPrepNull=", _genCallSiteTryPrepareNull.ToString(),
                    ",postGen=", _genCallSitePostGenerated.ToString(),
                    ",regen=", _genCallSiteRegenerate.ToString(), "]",
                    " meshChg=", _meshChangedCount.ToString(),
                    " matChg=", _materialChangedCount.ToString(),
                    " vsCount=", vsCount.ToString(),
                    " shCount=", shCount.ToString(),
                    " matRev=", matRev.ToString(),
                    " capRev=", _programMaterialShaderStateRevision.ToString(),
                    " settingsVer=", settingsVer.ToString(),
                    " capVer=", _shaderConfigVersion.ToString(),
                    " combNull=", (_combinedProgram is null).ToString(),
                    " sepNull=", (_separatedVertexProgram is null).ToString(),
                    " forcedNull=", (_forcedGeneratedVertexProgram is null).ToString(),
                    " pipelineMode=", allowPipelines.ToString(),
                    " forcePipelines=", forceShaderPipelines.ToString(),
                    " ver=", versionType,
                    " vsSelector=", vsSel);
            }

            private void LogSlowTryPrepare(
                string result,
                double totalMs,
                double generateMs,
                double ensureRenderSettingsMs,
                double ensureMaterialStateMs,
                double genProgramsAndBuffersMs,
                double materialLookupMs,
                double getProgramsMs,
                double configureDrawTopologyMs,
                double bindBuffersMs,
                double dynamicRenderDataMs)
            {
                if (totalMs < SlowTryPrepareLogThresholdMs &&
                    generateMs < SlowTryPrepareLogThresholdMs &&
                    genProgramsAndBuffersMs < SlowTryPrepareLogThresholdMs &&
                    materialLookupMs < SlowTryPrepareLogThresholdMs &&
                    getProgramsMs < SlowTryPrepareLogThresholdMs &&
                    bindBuffersMs < SlowTryPrepareLogThresholdMs)
                {
                    return;
                }

                Debug.OpenGLWarning(
                    $"[GLMeshRenderer] Slow TryPrepareForRendering: result={result}, totalMs={totalMs:F2}, " +
                    $"generateMs={generateMs:F2}, ensureSettingsMs={ensureRenderSettingsMs:F2}, ensureMaterialMs={ensureMaterialStateMs:F2}, " +
                    $"genProgramsAndBuffersMs={genProgramsAndBuffersMs:F2}, materialLookupMs={materialLookupMs:F2}, getProgramsMs={getProgramsMs:F2}, " +
                    $"configureDrawTopologyMs={configureDrawTopologyMs:F2}, bindBuffersMs={bindBuffersMs:F2}, " +
                    $"dynamicRenderDataMs={dynamicRenderDataMs:F2}, generated={IsGenerated}, buffersBound={BuffersBound}, " +
                    $"buffersReady={AreBuffersReadyForRendering()}, shadowPass={Engine.Rendering.State.IsShadowPass}, " +
                    $"directionalAtlasGrouped={Engine.Rendering.State.IsDirectionalCascadeAtlasGroupedShadowPass}, " +
                    $"pointAtlasGrouped={Engine.Rendering.State.IsPointLightAtlasGroupedShadowPass}, renderer='{GetDescribingName()}', " +
                    $"mesh='{Mesh?.Name ?? "<null>"}', material='{MeshRenderer.Material?.Name ?? "<null>"}'.");
            }

            private static double ElapsedMilliseconds(long startTimestamp)
                => (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;

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
                bool materialDiffers = !ReferenceEquals(material.Data, MeshRenderer.Material);
                bool forceSeparableUber = ShouldForceSeparableUberProgram(material);
                bool usePipelines = (Engine.Rendering.Settings.AllowShaderPipelines && Data.AllowShaderPipelines)
                    || forceShaderPipelines
                    || materialDiffers
                    || forceSeparableUber;
                
                return usePipelines
                    ? GetPipelinePrograms(material, out vertexProgram, out materialProgram)
                    : GetCombinedProgram(out vertexProgram, out materialProgram);
            }

            private bool ShouldSkipShadowDrawForProgramBuild(GLMaterial material)
            {
                var renderState = Engine.Rendering.State.RenderingPipelineState;
                if (renderState?.ShadowPass != true)
                    return false;

                bool forceShaderPipelines = renderState.ForceShaderPipelines;
                bool materialDiffers = !ReferenceEquals(material.Data, MeshRenderer.Material);
                bool usePipelines = (Engine.Rendering.Settings.AllowShaderPipelines && Data.AllowShaderPipelines)
                    || forceShaderPipelines
                    || materialDiffers;

                if (!usePipelines)
                    return _combinedProgram?.IsAsyncBuildPending ?? false;

                material.Data.EnsureShaderPipelineProgram();
                GLRenderProgram? materialProgram = material.SeparableProgram;
                if (materialProgram?.IsAsyncBuildPending ?? false)
                    return true;

                EProgramStageMask mask = materialProgram?.Data?.GetShaderTypeMask() ?? EProgramStageMask.None;
                if (mask.HasFlag(EProgramStageMask.VertexShaderBit))
                    return false;

                bool pointLightShadowPass = renderState.GlobalMaterialOverride is XRMaterial globalMaterialOverride
                    && UsesPointLightShadowDepthOutput(globalMaterialOverride);
                bool forceGeneratedVertexProgram = renderState.ForceGeneratedVertexProgram || pointLightShadowPass;
                GLRenderProgram? vertexProgram = forceGeneratedVertexProgram
                    ? _forcedGeneratedVertexProgram
                    : _separatedVertexProgram;

                return vertexProgram?.IsAsyncBuildPending ?? false;
            }

            private static bool ShouldForceSeparableUberProgram(GLMaterial material)
            {
                if (Engine.Rendering.State.IsShadowPass)
                    return false;

                XRMaterial xrMaterial = material.Data;
                if (!xrMaterial.ActiveUberVariant.IsEmpty)
                    return true;

                IReadOnlyList<XRShader> fragmentShaders = xrMaterial.FragmentShaders;
                bool hasUberFragment = false;
                for (int i = 0; i < fragmentShaders.Count; i++)
                {
                    if (IsUberFragmentShader(fragmentShaders[i]))
                    {
                        hasUberFragment = true;
                        break;
                    }
                }

                if (!hasUberFragment)
                    return false;

                // CPU-direct main passes may have shader pipelines globally disabled, but
                // monolithic Uber fragment + generated vertex programs are large enough to
                // wedge shared-context linking on imported scenes. Keep CPU mesh submission;
                // split the GL programs so the lit pass uses the same stable separable route
                // already used by prepass and override passes.
                return true;
            }

            private static bool IsUberFragmentShader(XRShader shader)
            {
                if (shader.Type != EShaderType.Fragment)
                    return false;

                string? path = shader.Source.FilePath ?? shader.FilePath;
                return string.Equals(Path.GetFileName(path), "UberShader.frag", StringComparison.OrdinalIgnoreCase);
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

                if (!vertexProgram.Link(nonBlocking: true))
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
                if (materialProgram?.Link(nonBlocking: true) ?? false)
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
                    && UsesPointLightShadowDepthOutput(globalMaterialOverride);
                bool forceGeneratedVertexProgram = (renderState?.ForceGeneratedVertexProgram ?? false) || pointLightShadowPass;
                vertexProgram = forceGeneratedVertexProgram
                    ? GetForcedGeneratedVertexProgram()
                    : GetOrCreateSeparatedVertexProgram();

                if (materialProgram?.Link(nonBlocking: true) ?? false)
                    _pipeline!.Set(mask, materialProgram);
                else
                {
                    Dbg("GenerateVertexShader: material program link failed", "Programs");
                    return false;
                }

                if (vertexProgram?.Link(nonBlocking: true) ?? false)
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
                using var prof = Engine.Profiler.Start("GLMeshRenderer.CreateCombinedProgram", ProfilerScopeKind.OneOffInvoke);
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
                using var prof = Engine.Profiler.Start("GLMeshRenderer.CreateSeparatedVertexProgram", ProfilerScopeKind.OneOffInvoke);
                XRShader vertexShader = hasNoVertexShaders
                    ? GenerateVertexShader(vertexSourceGenerator)
                    : FindVertexShader(vertexShaders, vertexShaderSelector) ?? GenerateVertexShader(vertexSourceGenerator);

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
                using var prof = Engine.Profiler.Start("GLMeshRenderer.InitiateLink", ProfilerScopeKind.OneOffInvoke);
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
                using var prof = Engine.Profiler.Start("GLMeshRenderer.CheckProgramLinked", ProfilerScopeKind.OneOffInvoke);
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
