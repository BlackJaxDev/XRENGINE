using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
                using var prof = RuntimeEngine.Profiler.Start("GLMeshRenderer.GenProgramsAndBuffers", ProfilerScopeKind.OneOffInvoke);
                BuffersBound = false;
                System.Threading.Interlocked.Increment(ref _programGenerationCount);

                PrepareUberVariantForCurrentMaterial();

                var material = Material;
                if (material is null)
                {
                    DestroyCombinedProgram();
                    DestroySeparablePrograms();
                    CaptureMaterialShaderState();
                    return;
                }

                CaptureMaterialShaderState();
                Dbg("GenProgramsAndBuffers start", "Programs");

                bool hasNoVertexShaders = (material.Data.VertexShaders.Count) == 0;

                EnsureRuntimeDeformationBuffers();
                CollectBuffers();
                Dbg($"Collected {_bufferCache.Count} buffer(s)", "Buffers");

                if (UseShaderPipelinesForThisRenderer())
                {
                    DestroyCombinedProgram();
                    material.Data.EnsureShaderPipelineProgram();

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
                    DestroySeparablePrograms();
                    material.Data.DestroyShaderPipelineProgram();
                    EnsureCombinedProgramForMaterial(material);

                    Dbg("GenProgramsAndBuffers: combined program initiated", "Programs");
                }
            }

            private bool UseShaderPipelinesForThisRenderer()
                => RuntimeEngine.Rendering.Settings.AllowShaderPipelines && Data.AllowShaderPipelines;

            private void DestroyCombinedProgram()
            {
                foreach (CombinedProgramCacheEntry entry in _combinedProgramCache.Values)
                {
                    GLRenderProgram? cachedProgram = entry.Program;
                    DestroyOwnedProgram(ref cachedProgram);
                }

                _combinedProgramCache.Clear();
                _combinedProgram = null;
                _combinedProgramMaterialKey = null;
                _combinedProgramMaterialShaderStateRevision = 0;
            }

            private void DestroySeparablePrograms()
            {
                DestroyOwnedPipeline(ref _pipeline);
                _pipeline = null;

                DestroyOwnedProgram(ref _separatedVertexProgram);
                _separatedVertexProgram = null;

                DestroyOwnedProgram(ref _forcedGeneratedVertexProgram);
                _forcedGeneratedVertexProgram = null;
            }

            private void DestroyOwnedProgram(ref GLRenderProgram? program)
            {
                GLRenderProgram? ownedProgram = program;
                if (ownedProgram is null)
                    return;

                ownedProgram.PropertyChanged -= CheckProgramLinked;
                program = null;
                ownedProgram.Data.Destroy();
            }

            private static void DestroyOwnedPipeline(ref GLRenderProgramPipeline? pipeline)
            {
                GLRenderProgramPipeline? ownedPipeline = pipeline;
                if (ownedPipeline is null)
                    return;

                pipeline = null;
                ownedPipeline.Data.Destroy();
            }

            private void EnsureRuntimeDeformationBuffers()
            {
                XRMesh? mesh = Mesh;
                if (mesh is null)
                    return;

                if (mesh.HasSkinning && RuntimeEngine.Rendering.Settings.AllowSkinning)
                    MeshRenderer.EnsureSkinningBuffers(logWarnings: false);

                if (mesh.HasBlendshapes && RuntimeEngine.Rendering.Settings.AllowBlendshapes)
                    MeshRenderer.EnsureBlendshapeBuffers(logWarnings: false);
            }

            private void EnsureProgramsMatchRenderSettings()
            {
                var settingsVersion = RuntimeEngine.Rendering.Settings.ShaderConfigVersion;
                if (_shaderConfigVersion == settingsVersion)
                    return;

                _shaderConfigVersion = settingsVersion;
                System.Threading.Interlocked.Increment(ref _programDestructionCount);

                DestroyCombinedProgram();
                DestroySeparablePrograms();
                MeshRenderer.Material?.SyncShaderPipelineProgramForCurrentSettings();

                if (!RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader
                    && !RuntimeEngine.Rendering.Settings.CalculateBlendshapesInComputeShader)
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

                DestroyCombinedProgram();
                DestroySeparablePrograms();

                Data.ResetVertexShaderSource();
                BuffersBound = false;
            }

            private void PrepareUberVariantForCurrentMaterial()
            {
                if (RuntimeEngine.Rendering.State.IsShadowPass)
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
                && _shaderConfigVersion == RuntimeEngine.Rendering.Settings.ShaderConfigVersion
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
                using var prof = RuntimeEngine.Profiler.Start("GLMeshRenderer.TryPrepareForRendering", ProfilerScopeKind.ConditionalLoop);
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
                int settingsVer = RuntimeEngine.Rendering.Settings.ShaderConfigVersion;
                bool forceShaderPipelines = RuntimeEngine.Rendering.State.RenderingPipelineState?.ForceShaderPipelines ?? false;
                bool allowPipelines = UseShaderPipelinesForThisRenderer();
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
                    " combMat=", _combinedProgramMaterialKey?.Name ?? "<null>",
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
                    $"buffersReady={AreBuffersReadyForRendering()}, shadowPass={RuntimeEngine.Rendering.State.IsShadowPass}, " +
                    $"directionalAtlasGrouped={RuntimeEngine.Rendering.State.IsDirectionalCascadeAtlasGroupedShadowPass}, " +
                    $"pointAtlasGrouped={RuntimeEngine.Rendering.State.IsPointLightAtlasGroupedShadowPass}, renderer='{GetDescribingName()}', " +
                    $"mesh='{Mesh?.Name ?? "<null>"}', material='{MeshRenderer.Material?.Name ?? "<null>"}'.");
            }

            private static double ElapsedMilliseconds(long startTimestamp)
                => (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;

            /// <summary>
            /// Get the appropriate vertex/material programs based on engine settings.
            /// Combined mode builds a monolithic program for the active material, including
            /// render-pass overrides, so disabling shader pipelines does not leave behind
            /// separable programs.
            /// </summary>
            private bool GetPrograms(
                GLMaterial material,
                [MaybeNullWhen(false)] out GLRenderProgram? vertexProgram,
                [MaybeNullWhen(false)] out GLRenderProgram? materialProgram)
            {
                bool usePipelines = UseShaderPipelinesForThisRenderer();
                
                return usePipelines
                    ? GetPipelinePrograms(material, out vertexProgram, out materialProgram)
                    : GetCombinedProgram(material, out vertexProgram, out materialProgram);
            }

            private bool ShouldSkipShadowDrawForProgramBuild(GLMaterial material)
            {
                var renderState = RuntimeEngine.Rendering.State.RenderingPipelineState;
                if (renderState?.ShadowPass != true)
                    return false;

                bool usePipelines = UseShaderPipelinesForThisRenderer();

                if (!usePipelines)
                {
                    XRMaterial xrMaterial = material.Data;
                    return _combinedProgramCache.TryGetValue(xrMaterial, out CombinedProgramCacheEntry cachedEntry) &&
                        cachedEntry.ShaderStateRevision == xrMaterial.ShaderStateRevision &&
                        cachedEntry.Program.IsAsyncBuildPending;
                }

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

            /// <summary>
            /// Get the combined program when pipeline mode is disabled.
            /// </summary>
            private bool GetCombinedProgram(GLMaterial material, out GLRenderProgram? vertexProgram, out GLRenderProgram? materialProgram)
            {
                EnsureCombinedProgramForMaterial(material);

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

            private void EnsureCombinedProgramForMaterial(GLMaterial material)
            {
                XRMaterial xrMaterial = material.Data;
                long shaderStateRevision = xrMaterial.ShaderStateRevision;
                if (_combinedProgramCache.TryGetValue(xrMaterial, out CombinedProgramCacheEntry cachedEntry) &&
                    cachedEntry.ShaderStateRevision == shaderStateRevision)
                {
                    _combinedProgram = cachedEntry.Program;
                    _combinedProgramMaterialKey = xrMaterial;
                    _combinedProgramMaterialShaderStateRevision = shaderStateRevision;
                    return;
                }

                if (cachedEntry.Program is not null)
                {
                    GLRenderProgram? staleProgram = cachedEntry.Program;
                    DestroyOwnedProgram(ref staleProgram);
                    _combinedProgramCache.Remove(xrMaterial);
                }

                xrMaterial.DestroyShaderPipelineProgram();

                bool hasNoVertexShaders = xrMaterial.VertexShaders.Count == 0;
                CreateCombinedProgram(
                    ref _combinedProgram,
                    xrMaterial,
                    hasNoVertexShaders,
                    xrMaterial.Shaders,
                    Data.VertexShaderSelector,
                    () => Data.VertexShaderSource ?? string.Empty);
                if (_combinedProgram is not null)
                    _combinedProgramCache[xrMaterial] = new CombinedProgramCacheEntry(_combinedProgram, shaderStateRevision);

                BuffersBound = false;
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

                // Materials only own a separable program while shader pipeline mode is active.
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
                var renderState = RuntimeEngine.Rendering.State.RenderingPipelineState;
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
                XRMaterial material,
                bool hasNoVertexShaders,
                IEnumerable<XRShader> shaders,
                Func<XRShader, bool> vertexShaderSelector,
                Func<string> vertexSourceGenerator)
            {
                using var prof = RuntimeEngine.Profiler.Start("GLMeshRenderer.CreateCombinedProgram", ProfilerScopeKind.OneOffInvoke);
                XRShader vertexShader = hasNoVertexShaders
                    ? GenerateVertexShader(vertexSourceGenerator)
                    : FindVertexShader(shaders, vertexShaderSelector) ?? GenerateVertexShader(vertexSourceGenerator);

                if (!hasNoVertexShaders)
                    shaders = shaders.Where(x => x.Type != EShaderType.Vertex);

                shaders = shaders.Append(vertexShader);

                var combinedData = new XRRenderProgram(false, false, shaders)
                {
                    Name = $"Combined:{material.Name ?? "unknown"}",
                    UsageTag = $"CombinedMeshProgram | variant={Data.VersionKindLabel} | material={material.Name ?? "<unnamed>"} | mesh={MeshRenderer.Name ?? "<unnamed>"}",
                    Priority = Data.ProgramPriority,
                };
                material.ApplyShaderProgramMetadata(combinedData);
                _combinedProgramMaterialKey = material;
                _combinedProgramMaterialShaderStateRevision = material.ShaderStateRevision;
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
                using var prof = RuntimeEngine.Profiler.Start("GLMeshRenderer.CreateSeparatedVertexProgram", ProfilerScopeKind.OneOffInvoke);
                XRShader vertexShader = hasNoVertexShaders
                    ? GenerateVertexShader(vertexSourceGenerator)
                    : FindVertexShader(vertexShaders, vertexShaderSelector) ?? GenerateVertexShader(vertexSourceGenerator);

                var separatedData = new XRRenderProgram(false, true, vertexShader)
                {
                    Name = $"SeparatedVertex:{MeshRenderer.Material?.Name ?? "unknown"}",
                    UsageTag = $"SeparableVertexProgram | variant={Data.VersionKindLabel} | material={MeshRenderer.Material?.Name ?? "<unnamed>"} | mesh={MeshRenderer.Name ?? "<unnamed>"}",
                    Priority = Data.ProgramPriority,
                };
                vertexProgram = Renderer.GenericToAPI<GLRenderProgram>(separatedData)!;
                vertexProgram.PropertyChanged += CheckProgramLinked;
                InitiateLink(vertexProgram);
            }

            /// <summary>
            /// Process-wide dedup cache for engine-generated vertex shaders, keyed by source text.
            /// Two BaseVersion instances that produce identical GLSL (same generator, mesh layout, and
            /// deform settings) share a single XRShader, which lets the shared GLShader/binary-cache
            /// path short-circuit redundant compiles. Lifetime equals the process; matches the existing
            /// UberShaderVariantBuilder.GeneratedShaderCache policy.
            /// </summary>
            private static readonly ConcurrentDictionary<string, XRShader> _generatedVertexShaderCache
                = new(StringComparer.Ordinal);

            /// <summary>
            /// Generate a simple default vertex shader, dedup'd by source text.
            /// </summary>
            private static XRShader GenerateVertexShader(Func<string> vertexSourceGenerator)
            {
                string source = vertexSourceGenerator() ?? string.Empty;
                return _generatedVertexShaderCache.GetOrAdd(source, static src => new XRShader(EShaderType.Vertex, src));
            }

            /// <summary>
            /// Start linking the provided program, either synchronously or asynchronously.
            /// Pre-computes hash + cache lookup before issuing GL work to minimize render-thread stalls.
            /// </summary>
            private void InitiateLink(GLRenderProgram vertexProgram)
            {
                using var prof = RuntimeEngine.Profiler.Start("GLMeshRenderer.InitiateLink", ProfilerScopeKind.OneOffInvoke);
                vertexProgram.Data.AllowLink();
                vertexProgram.BeginPrepareLinkData(registerPendingProgram: Data.Parent.GenerateAsync);
                if (!Data.Parent.GenerateAsync)
                    vertexProgram.Link();
            }

            /// <summary>
            /// Once a program links, bind attributes on the main thread if needed.
            /// </summary>
            private void CheckProgramLinked(object? sender, IXRPropertyChangedEventArgs e)
            {
                using var prof = RuntimeEngine.Profiler.Start("GLMeshRenderer.CheckProgramLinked", ProfilerScopeKind.OneOffInvoke);
                GLRenderProgram? program = sender as GLRenderProgram;
                if (e.PropertyName != nameof(GLRenderProgram.IsLinked) || !(program?.IsLinked ?? false))
                    return;

                Dbg("CheckProgramLinked: program linked - binding buffers", "Programs");
                program.PropertyChanged -= CheckProgramLinked;

                if (MeshRenderer.GenerateAsync)
                    RuntimeEngine.EnqueueMainThreadTask(() => BindBuffers(program), "GLMeshRenderer.BindBuffers");
                else
                    BindBuffers(program);
            }
        }
    }
}
