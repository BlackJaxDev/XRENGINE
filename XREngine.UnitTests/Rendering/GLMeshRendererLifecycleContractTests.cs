using System;
using System.IO;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.OpenGL;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GLMeshRendererLifecycleContractTests
{
    [Test]
    public void GLMeshRenderer_RegeneratesProgramsWhenMaterialChanges()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/MeshRendering/GLMeshRenderer.Lifecycle.cs");

        source.ShouldContain("case nameof(XRMeshRenderer.Material):");
        source.ShouldContain("OnMaterialChanged();");
        source.ShouldContain("Data.ResetVertexShaderSource();");
        source.ShouldContain("MeshRenderer.Material?.SyncShaderPipelineProgramForCurrentSettings();");
        source.ShouldContain("Engine.EnqueueMainThreadTask(RegenerateProgramsAndBuffers, \"GLMeshRenderer.MaterialChanged\");");
        source.ShouldContain("DestroyCombinedProgram();");
        source.ShouldContain("DestroySeparablePrograms();");
        source.ShouldContain("BuffersBound = false;");
    }

    [Test]
    public void GLMeshRenderer_BuildsIndexBuffersOnlyWhenMeshRendererIsGenerated()
    {
        string lifecycleSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/MeshRendering/GLMeshRenderer.Lifecycle.cs");
        string shaderSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/MeshRendering/GLMeshRenderer.Shaders.cs");

        lifecycleSource.ShouldContain("MakeIndexBuffers();");
        shaderSource.ShouldNotContain("MakeIndexBuffers();");
    }

    [Test]
    public void GLMeshRenderer_UsesCombinedProgramsWithoutDuplicatingPendingUberCompiles()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/MeshRendering/GLMeshRenderer.Shaders.cs");

        source.ShouldContain("private bool UseShaderPipelinesForThisRenderer()");
        source.ShouldContain("=> RuntimeEngine.Rendering.Settings.AllowShaderPipelines && Data.AllowShaderPipelines;");
        source.ShouldContain("DestroyCombinedProgram();");
        source.ShouldContain("DestroySeparablePrograms();");
        source.ShouldContain("material.Data.EnsureShaderPipelineProgram();");
        source.ShouldContain("material.Data.DestroyShaderPipelineProgram();");
        source.ShouldContain("if (GetCombinedProgram(material, out vertexProgram, out materialProgram))");
        source.ShouldContain("ShouldUsePipelineFallbackForPendingCombinedProgram(material)");
        source.ShouldContain("allowWhenShaderPipelinesDisabled: true");
        source.ShouldContain("_combinedProgram is not { IsAsyncBuildPending: true }");
        source.ShouldContain("!Data.AllowShaderPipelines");
        source.ShouldNotContain("!RuntimeEngine.Rendering.Settings.AllowShaderPipelines ||");
        source.ShouldContain("if (IsUberMaterial(material.Data) ||");
        source.ShouldContain("!material.RequestedUberVariant.IsEmpty");
        source.ShouldContain("!material.ActiveUberVariant.IsEmpty");
        source.ShouldContain("material.UberVariantStatus.Stage != EUberMaterialVariantStage.None");
        source.ShouldContain("combinedProgram.PreparedCompileSourceBytes >= GLProgramCompileLinkQueue.LargeSourceLinkDeferralThresholdBytes");
        source.ShouldContain("private void EnsureCombinedProgramForMaterial(GLMaterial material)");
        source.ShouldNotContain("ShouldForceSeparableUberProgram");
        source.ShouldNotContain("|| forceShaderPipelines");
        source.ShouldNotContain("|| materialDiffers");
    }

    [Test]
    public void GLMeshRenderer_RequiresCombinedProgramUseBeforeReportingProgramsReady()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/MeshRendering/GLMeshRenderer.Shaders.cs");

        source.ShouldContain("if (!vertexProgram.Use())");
        source.ShouldContain("vertexProgram = materialProgram = null;");
        source.ShouldContain("Dbg(\"GetCombinedProgram: use failed\", \"Programs\");");
        source.ShouldContain("return false;");
        source.ShouldNotContain("vertexProgram.Use();\r\n                Dbg(\"GetCombinedProgram: linked & in use\", \"Programs\");");
        source.ShouldNotContain("vertexProgram.Use();\n                Dbg(\"GetCombinedProgram: linked & in use\", \"Programs\");");
    }

    [Test]
    public void GLMeshRenderer_UsesCheapPipelineFallbackWhileUberProgramsArePending()
    {
        string shaderSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/MeshRendering/GLMeshRenderer.Shaders.cs");
        string renderSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/MeshRendering/GLMeshRenderer.Rendering.cs");

        shaderSource.ShouldContain("ShouldUsePipelineForPendingUberFallbackMaterial(material)");
        shaderSource.ShouldContain("allowWhenShaderPipelinesDisabled: true");
        shaderSource.ShouldContain("RuntimeEngine.Rendering.Settings.AllowShaderPipelines");
        shaderSource.ShouldContain("IsPendingUberFallbackMaterial(material.Data)");
        shaderSource.ShouldContain("IsPendingUberFallbackMaterial(material.Data))");
        shaderSource.ShouldContain("ResolveCombinedProgramPriority(Data.ProgramPriority, material.ShaderProgramPriority)");

        renderSource.ShouldContain("uniformSourceMaterial: material");
        renderSource.ShouldContain("GLMaterial bindingMaterial = uniformSourceMaterial ?? material;");
        renderSource.ShouldContain("material.SetUniforms(mat);");
        renderSource.ShouldContain("Renderer.ApplyRenderParameters(bindingMaterial.Data.RenderOptions);");
        renderSource.ShouldContain("BindPendingUberFallbackTextures(bindingMaterial, mat);");
        renderSource.ShouldContain("sourceMaterial.SetTextureUniform(fallbackProgram, _pendingUberFallbackTextureIndex, \"Texture0\");");
        renderSource.ShouldContain("ShaderHelper.PendingUberTextureFragForward()");
        renderSource.ShouldContain("FallbackHasTexture");
        renderSource.ShouldContain("FallbackForceOpaque");
        renderSource.ShouldContain("FallbackUseAlphaCutoff");
        renderSource.ShouldContain("material.ShaderProgramPriority = EProgramPriority.Interactive;");
        renderSource.ShouldContain("if (RuntimeEngine.Rendering.Settings.AllowShaderPipelines)");
        renderSource.ShouldContain("material.EnsureShaderPipelineProgram();");
        renderSource.ShouldNotContain("\"FallbackAlphaMode\"");

        string fallbackShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Common/PendingUberTexturedForward.fs");
        fallbackShader.ShouldContain("FallbackBaseColor");
        fallbackShader.ShouldContain("FallbackHasTexture");
        fallbackShader.ShouldContain("FallbackUseAlphaCutoff");
        fallbackShader.ShouldNotContain("#pragma snippet \"ForwardLighting\"");

        int primeIndex = shaderSource.IndexOf("PrimePendingUberFallbackPrograms(material);", StringComparison.Ordinal);
        primeIndex.ShouldBeGreaterThanOrEqualTo(0);
        int uberProgramIndex = shaderSource.IndexOf("EnsureCombinedProgramForMaterial(material);", primeIndex, StringComparison.Ordinal);
        uberProgramIndex.ShouldBeGreaterThan(primeIndex);
    }

    [Test]
    public void GLMeshRenderer_PendingUberFallbackPrefersMainTextureSampler()
    {
        XRTexture2D secondaryTexture = new() { SamplerName = "_BumpMap" };
        XRTexture2D mainTexture = new() { SamplerName = "_MainTex" };
        XRMaterial material = new([secondaryTexture, mainTexture]);

        OpenGLRenderer.GLMeshRenderer.ResolvePendingUberFallbackTextureIndex(material).ShouldBe(1);
    }

    [Test]
    public void GLMeshRenderer_PendingUberFallbackUsesFirstTextureWhenMainTextureIsUnavailable()
    {
        XRTexture2D firstTexture = new() { SamplerName = "_BaseMap" };
        XRTexture2D secondTexture = new() { SamplerName = "_EmissionMap" };
        XRMaterial material = new([null, firstTexture, secondTexture]);

        OpenGLRenderer.GLMeshRenderer.ResolvePendingUberFallbackTextureIndex(material).ShouldBe(1);
    }

    [Test]
    public void GLMeshRenderer_PendingUberFallbackPreservesImportedColorTint()
    {
        Vector4 expected = new(0.25f, 0.5f, 0.75f, 0.8f);
        XRMaterial material = new([new ShaderVector4(expected, "_Color")]);

        OpenGLRenderer.GLMeshRenderer.ResolvePendingUberFallbackBaseColor(material).ShouldBe(expected);
    }

    [TestCase(EDefaultRenderPass.OpaqueForward, ETransparencyMode.Opaque)]
    [TestCase(EDefaultRenderPass.MaskedForward, ETransparencyMode.Masked)]
    [TestCase(EDefaultRenderPass.TransparentForward, ETransparencyMode.AlphaBlend)]
    [TestCase(EDefaultRenderPass.WeightedBlendedOitForward, ETransparencyMode.WeightedBlendedOit)]
    [TestCase(EDefaultRenderPass.PerPixelLinkedListForward, ETransparencyMode.PerPixelLinkedList)]
    [TestCase(EDefaultRenderPass.DepthPeelingForward, ETransparencyMode.DepthPeeling)]
    public void GLMeshRenderer_PendingUberFallbackUsesPassCompatibleTransparencyShader(
        EDefaultRenderPass renderPass,
        ETransparencyMode expectedMode)
    {
        XRMaterial material = new() { RenderPass = (int)renderPass };

        OpenGLRenderer.GLMeshRenderer.ResolvePendingUberFallbackTransparencyMode(material).ShouldBe(expectedMode);
    }

    [TestCase(EProgramPriority.Main, EProgramPriority.Interactive, EProgramPriority.Interactive)]
    [TestCase(EProgramPriority.Shadow, EProgramPriority.Interactive, EProgramPriority.Interactive)]
    [TestCase(EProgramPriority.Shadow, EProgramPriority.Main, EProgramPriority.Shadow)]
    public void GLMeshRenderer_CombinedProgramsHonorExplicitInteractiveMaterialPriority(
        EProgramPriority meshPriority,
        EProgramPriority materialPriority,
        EProgramPriority expectedPriority)
        => OpenGLRenderer.GLMeshRenderer.ResolveCombinedProgramPriority(meshPriority, materialPriority)
            .ShouldBe(expectedPriority);

    [Test]
    public void GLMeshRenderer_UsesSharedShadowMaterialForColdUberShadowPass()
    {
        string renderSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/MeshRendering/GLMeshRenderer.Rendering.cs");

        renderSource.ShouldContain("CanUseSharedUberShadowFallback(globalMaterialOverride, shadowSourceMaterial)");
        renderSource.ShouldContain("shadowSourceMaterial.TryGetUberMaterialState(out _, out _)");
        renderSource.ShouldContain("Prefer visible material/link progress over first-frame exact");
    }

    [Test]
    public void GLRenderProgram_UseDoesNotBindPendingAsyncProgramHandles()
    {
        string programSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.cs");
        string linkSource = ReadGlRenderProgramLinkingSources();

        programSource.ShouldContain("public bool Use()");
        programSource.ShouldContain("Link(nonBlocking: true)");
        programSource.ShouldContain("if (!IsLinked || IsAsyncBuildPending)");
        programSource.ShouldContain("Api.UseProgram(BindingId);");

        linkSource.ShouldContain("private void UseRequested(XRRenderProgram program)");
        linkSource.ShouldContain("Use();");
        linkSource.ShouldNotContain("Api.UseProgram(BindingId);");
    }

    [Test]
    public void GLProgramCompileLinkQueue_SerializesMultiWorkerProgramLinkDriverCalls()
    {
        string queueSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/Pipelines/GLProgramCompileLinkQueue.cs");

        queueSource.ShouldContain("XREngineEnvironmentVariables.SharedContextDisableLinkSerialization");
        queueSource.ShouldContain("private readonly SemaphoreSlim _programLinkGate;");
        queueSource.ShouldContain("LargeSourceLinkDeferralThresholdBytes");
        queueSource.ShouldContain("_programLinkGate.Wait();");
        queueSource.ShouldContain("_programLinkGate.Release();");
        queueSource.ShouldContain("serialized shared-context program link/status");
        queueSource.ShouldContain("bool allowLinkDeferral = ShouldAllowLinkDeferral(");
        queueSource.ShouldContain("summary.SourceBytes < LargeSourceLinkDeferralThresholdBytes");
        queueSource.ShouldContain("allowDeferred: allowLinkDeferral");
        queueSource.ShouldContain("publishing a failed async result without querying final status");
        queueSource.ShouldContain("deferring completion polling at background priority so faster shader programs can link first.");
        queueSource.ShouldContain("SharedContextAbandonedLinkMarker");
        queueSource.ShouldContain("setBinaryRetrievableHint");
        queueSource.ShouldContain("worker=source-link-binary-retrievable-hint");
        queueSource.ShouldContain("worker=source-link-handoff-flush");
        queueSource.ShouldContain("worker=deferred-source-link-handoff-flush");
        queueSource.ShouldNotContain("glFinish");
    }

    [Test]
    public void GLRenderProgram_CapturesSharedContextSourceBinariesOffRenderThread()
    {
        string queueSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/Pipelines/GLProgramCompileLinkQueue.cs");
        string linkSource = ReadGlRenderProgramLinkingSources();
        string binaryCacheSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.BinaryCache.cs");

        queueSource.ShouldContain("ProgramBinarySnapshot");
        queueSource.ShouldContain("CaptureProgramBinary");
        queueSource.ShouldContain("worker=source-link-binary-cache-capture");
        queueSource.ShouldContain("worker=deferred-source-link-binary-cache-capture");
        linkSource.ShouldContain("CacheBinary(pendingId2, compileResult.ProgramBinary);");
        binaryCacheSource.ShouldContain("QueueBinaryShaderCacheWrite");
        binaryCacheSource.ShouldContain("captured linked program binary on shared worker");
    }

    [Test]
    public void GLRenderProgram_EnablesSharedLinkedProgramReuseUnlessExplicitlyDisabled()
    {
        string linkSource = ReadGlRenderProgramLinkingSources();

        linkSource.ShouldContain("XREngineEnvironmentVariables.DisableSharedLinkedProgramReuse");
        linkSource.ShouldContain("private static bool SharedLinkedProgramReuseEnabled");
        linkSource.ShouldContain("if (!SharedLinkedProgramReuseEnabled)");
        linkSource.ShouldContain("if (!SharedLinkedProgramReuseEnabled ||");
    }

    [Test]
    public void GLRenderProgram_RoutesColdLargeSourceLinksOffTheRenderThread()
    {
        string linkSource = ReadGlRenderProgramLinkingSources();
        string selectorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/Pipelines/OpenGLShaderLinkBackendSelector.cs");

        linkSource.ShouldContain("LargeSourceSharedContextPreferenceThresholdBytes = 128 * 1024");
        linkSource.ShouldContain("ShouldPreferSharedContextForLargeSource(Hash, inputs)");
        linkSource.ShouldContain("PreferSharedContextForLargeSource: preferSharedContextForLargeSource");

        selectorSource.ShouldContain("PreferSharedContextForLargeSource");
        selectorSource.ShouldContain("large source program routed to shared-context lane to avoid driver-parallel timeout");
    }

    [Test]
    public void GLRenderProgram_AbandonedSharedContextLinksAvoidDeferredCompletionPolling()
    {
        string linkSource = ReadGlRenderProgramLinkingSources();

        linkSource.ShouldContain("DriverParallelSourceTimeouts");
        linkSource.ShouldContain("programId={abandonedProgramId} leaked to avoid blocking GL cleanup calls");
        linkSource.ShouldContain("shared-context source link stalled; leaving fallback material active");
        linkSource.ShouldNotContain("RenderThreadDriverParallelRetryHashes");
        linkSource.ShouldNotContain("DeferredAsyncLinkCleanups.Enqueue(new DeferredAsyncLinkCleanup(Renderer, abandonedProgramId, []));");
    }

    [Test]
    public void XRMeshRenderer_DefersInactiveVrVariantsBehindOtherProgramWork()
    {
        XRMeshRenderer meshRenderer = new();

        meshRenderer.GetDefaultVersion().ProgramPriority.ShouldBe(EProgramPriority.Main);
        meshRenderer.GetOVRMultiViewVersion().ProgramPriority.ShouldBe(EProgramPriority.Deferred);
        meshRenderer.GetNVStereoVersion().ProgramPriority.ShouldBe(EProgramPriority.Deferred);
        meshRenderer.GetMeshDeformOVRMultiViewVersion().ProgramPriority.ShouldBe(EProgramPriority.Deferred);
        meshRenderer.GetMeshDeformNVStereoVersion().ProgramPriority.ShouldBe(EProgramPriority.Deferred);
    }

    [Test]
    public void XRMeshRenderer_ShaderPipelineOverrideDoesNotMaterializeStereoVersions()
    {
        XRMeshRenderer meshRenderer = new();

        meshRenderer.SetShaderPipelinesAllowedForAllVersions(false);

        meshRenderer.GeneratedVertexShaderVersions.ShouldBeEmpty();

        XRMeshRenderer.BaseVersion defaultVersion = meshRenderer.GetDefaultVersion();

        defaultVersion.AllowShaderPipelines.ShouldBeFalse();
        meshRenderer.GeneratedVertexShaderVersions.Count.ShouldBe(1);
        meshRenderer.GeneratedVertexShaderVersions.ContainsKey(0).ShouldBeTrue();
        meshRenderer.GeneratedVertexShaderVersions.ContainsKey(1).ShouldBeFalse();
        meshRenderer.GeneratedVertexShaderVersions.ContainsKey(2).ShouldBeFalse();
    }

    [Test]
    public void XRMeshRenderer_MonoVersionPathSkipsStereoMaterialShaderScans()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/XRMeshRenderer.cs").Replace("\r\n", "\n");
        int monoFastPath = source.IndexOf(
            "if (!stereoPass)\n                return useMeshDeform",
            StringComparison.Ordinal);
        int nvShaderScan = source.IndexOf(
            "bool hasNvMaterialVertexShader = MaterialHasMatchingVertexShader",
            StringComparison.Ordinal);
        int multiviewShaderScan = source.IndexOf(
            "bool hasMultiViewMaterialVertexShader = MaterialHasMatchingVertexShader",
            StringComparison.Ordinal);

        monoFastPath.ShouldBeGreaterThanOrEqualTo(0);
        nvShaderScan.ShouldBeGreaterThan(monoFastPath);
        multiviewShaderScan.ShouldBeGreaterThan(monoFastPath);
    }

    [Test]
    public void XRMaterial_DisposesSeparableProgramWhenShaderPipelinesAreDisabled()
    {
        string materialSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering/Objects/Materials/XRMaterial.cs");
        string glMaterialSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Materials/GLMaterial.cs");
        string engineSettingsSource = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/EngineRenderingSettingsApplication.cs");

        materialSource.ShouldContain("public void DestroyShaderPipelineProgram()");
        materialSource.ShouldContain("public void SyncShaderPipelineProgramForCurrentSettings()");
        materialSource.ShouldContain("public static void DisposeShaderPipelineProgramsWhenDisabled()");
        materialSource.ShouldContain("EnsureShaderPipelineProgram(bool allowWhenShaderPipelinesDisabled = false)");
        materialSource.ShouldContain("if (!ShouldCreateShaderPipelineProgram(allowWhenShaderPipelinesDisabled))");
        materialSource.ShouldContain("if (ShouldCreateShaderPipelineProgram())");
        materialSource.ShouldContain("return allowWhenShaderPipelinesDisabled || RuntimeRenderingHostServices.Settings.AllowShaderPipelines;");
        materialSource.ShouldContain("EnsureShaderPipelineUberSourceReady()");
        materialSource.ShouldContain("HasShaderPipelineRenderableUberSource()");
        materialSource.ShouldContain("EnsureUberVariantPreparedForRendering();");
        materialSource.ShouldContain("if (!HasShaderPipelineRenderableUberSource())");
        materialSource.ShouldContain("Name = BuildShaderPipelineProgramName()");
        materialSource.ShouldContain("ShaderPipelineProgram.Destroy();");
        materialSource.ShouldContain("ShaderPipelineProgram = null;");
        glMaterialSource.ShouldContain("bool usePipelines = RuntimeEngine.Rendering.Settings.AllowShaderPipelines;");
        glMaterialSource.ShouldNotContain("|| (RuntimeEngine.Rendering.State.RenderingPipelineState?.ForceShaderPipelines ?? false)");
        engineSettingsSource.ShouldContain("global::XREngine.Rendering.XRMaterial.DisposeShaderPipelineProgramsWhenDisabled();");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string fullPath = ResolveWorkspacePath(relativePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected workspace file does not exist: {fullPath}");
        return global::XREngine.UnitTests.SourceContractWorkspace.ReadFile(relativePath);
    }

    private static string ReadGlRenderProgramLinkingSources()
        => string.Join('\n', new[]
        {
            ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.LinkOrchestration.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.CompileInputs.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.BinaryCacheInteraction.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.AsyncResults.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.HazardDetection.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.LinkDiagnostics.cs"),
        });

    private static string ResolveWorkspacePath(string relativePath)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not resolve workspace path for '{relativePath}' from test base directory '{AppContext.BaseDirectory}'.");
    }
}
