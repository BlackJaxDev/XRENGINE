using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GLMeshRendererLifecycleContractTests
{
    [Test]
    public void GLMeshRenderer_RegeneratesProgramsWhenMaterialChanges()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Mesh Renderer/GLMeshRenderer.Lifecycle.cs");

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
        string lifecycleSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Mesh Renderer/GLMeshRenderer.Lifecycle.cs");
        string shaderSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Mesh Renderer/GLMeshRenderer.Shaders.cs");

        lifecycleSource.ShouldContain("MakeIndexBuffers();");
        shaderSource.ShouldNotContain("MakeIndexBuffers();");
    }

    [Test]
    public void GLMeshRenderer_UsesCombinedProgramsWithoutUberPipelineFallbackWhenPipelinesAreDisabled()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Mesh Renderer/GLMeshRenderer.Shaders.cs");

        source.ShouldContain("private bool UseShaderPipelinesForThisRenderer()");
        source.ShouldContain("=> RuntimeEngine.Rendering.Settings.AllowShaderPipelines && Data.AllowShaderPipelines;");
        source.ShouldContain("DestroyCombinedProgram();");
        source.ShouldContain("DestroySeparablePrograms();");
        source.ShouldContain("material.Data.EnsureShaderPipelineProgram();");
        source.ShouldContain("material.Data.DestroyShaderPipelineProgram();");
        source.ShouldContain("if (GetCombinedProgram(material, out vertexProgram, out materialProgram))");
        source.ShouldContain("ShouldUsePipelineFallbackForPendingCombinedProgram(material)");
        source.ShouldContain("!RuntimeEngine.Rendering.Settings.AllowShaderPipelines");
        source.ShouldContain("allowWhenShaderPipelinesDisabled: true");
        source.ShouldContain("material.Data.TryGetUberMaterialState(out _, out _)");
        source.ShouldContain("private void EnsureCombinedProgramForMaterial(GLMaterial material)");
        source.ShouldNotContain("ShouldForceSeparableUberProgram");
        source.ShouldNotContain("|| forceShaderPipelines");
        source.ShouldNotContain("|| materialDiffers");
    }

    [Test]
    public void GLMeshRenderer_RequiresCombinedProgramUseBeforeReportingProgramsReady()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Mesh Renderer/GLMeshRenderer.Shaders.cs");

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
        string shaderSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Mesh Renderer/GLMeshRenderer.Shaders.cs");
        string renderSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Mesh Renderer/GLMeshRenderer.Rendering.cs");

        shaderSource.ShouldContain("ShouldUsePipelineForPendingUberFallbackMaterial(material)");
        shaderSource.ShouldContain("allowWhenShaderPipelinesDisabled: true");
        shaderSource.ShouldContain("RuntimeEngine.Rendering.Settings.AllowShaderPipelines");
        shaderSource.ShouldContain("ReferenceEquals(material.Data, s_pendingUberFallbackMaterial)");

        renderSource.ShouldContain("material.ShaderProgramPriority = EProgramPriority.Interactive;");
        renderSource.ShouldContain("if (RuntimeEngine.Rendering.Settings.AllowShaderPipelines)");
        renderSource.ShouldContain("material.EnsureShaderPipelineProgram();");
    }

    [Test]
    public void GLMeshRenderer_UsesSharedShadowMaterialForColdUberShadowPass()
    {
        string renderSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Mesh Renderer/GLMeshRenderer.Rendering.cs");

        renderSource.ShouldContain("CanUseSharedUberShadowFallback(globalMaterialOverride, shadowSourceMaterial)");
        renderSource.ShouldContain("shadowSourceMaterial.TryGetUberMaterialState(out _, out _)");
        renderSource.ShouldContain("Prefer visible material/link progress over first-frame exact");
    }

    [Test]
    public void GLRenderProgram_UseDoesNotBindPendingAsyncProgramHandles()
    {
        string programSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Meshes/GLRenderProgram.cs");
        string linkSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Meshes/GLRenderProgram.Linking.cs");

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
        string queueSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/GLProgramCompileLinkQueue.cs");

        queueSource.ShouldContain("XRE_SHARED_CONTEXT_DISABLE_LINK_SERIALIZATION");
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
        string queueSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/GLProgramCompileLinkQueue.cs");
        string linkSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Meshes/GLRenderProgram.Linking.cs");
        string binaryCacheSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Meshes/GLRenderProgram.BinaryCache.cs");

        queueSource.ShouldContain("ProgramBinarySnapshot");
        queueSource.ShouldContain("CaptureProgramBinary");
        queueSource.ShouldContain("worker=source-link-binary-cache-capture");
        queueSource.ShouldContain("worker=deferred-source-link-binary-cache-capture");
        linkSource.ShouldContain("CacheBinary(pendingId2, compileResult.ProgramBinary);");
        binaryCacheSource.ShouldContain("QueueBinaryShaderCacheWrite");
        binaryCacheSource.ShouldContain("captured linked program binary on shared worker");
    }

    [Test]
    public void GLRenderProgram_DisablesSharedLinkedProgramReuseByDefault()
    {
        string linkSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Meshes/GLRenderProgram.Linking.cs");

        linkSource.ShouldContain("XRE_ENABLE_SHARED_LINKED_PROGRAM_REUSE");
        linkSource.ShouldContain("private static readonly bool SharedLinkedProgramReuseEnabled");
        linkSource.ShouldContain("if (!SharedLinkedProgramReuseEnabled)");
        linkSource.ShouldContain("if (!SharedLinkedProgramReuseEnabled ||");
    }

    [Test]
    public void GLRenderProgram_BlocksColdLargeSourceLinksByDefault()
    {
        string linkSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Meshes/GLRenderProgram.Linking.cs");
        string selectorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/OpenGLShaderLinkBackendSelector.cs");

        linkSource.ShouldContain("XRE_ENABLE_LARGE_OPENGL_SOURCE_LINKS");
        linkSource.ShouldContain("LargeSourceSourceLinkWatchdogThresholdBytes = 128 * 1024");
        linkSource.ShouldContain("ShouldBlockLargeSourceSourceLink(inputs)");
        linkSource.ShouldContain("BlockLargeSourceSourceLink: blockLargeSourceSourceLink");
        linkSource.ShouldContain("SOURCE_LARGE_BLOCKED");

        selectorSource.ShouldContain("BlockLargeSourceSourceLink");
        selectorSource.ShouldContain("large source compile/link is disabled for editor stability");
        selectorSource.ShouldContain("use a binary cache hit or set XRE_ENABLE_LARGE_OPENGL_SOURCE_LINKS=1");
    }

    [Test]
    public void GLRenderProgram_AbandonedSharedContextLinksAvoidDeferredCompletionPolling()
    {
        string linkSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Meshes/GLRenderProgram.Linking.cs");

        linkSource.ShouldContain("DriverParallelSourceTimeouts");
        linkSource.ShouldContain("programId={abandonedProgramId} leaked to avoid blocking GL cleanup calls");
        linkSource.ShouldContain("shared-context source link stalled; leaving fallback material active");
        linkSource.ShouldNotContain("RenderThreadDriverParallelRetryHashes");
        linkSource.ShouldNotContain("DeferredAsyncLinkCleanups.Enqueue(new DeferredAsyncLinkCleanup(Renderer, abandonedProgramId, []));");
    }

    [Test]
    public void XRMaterial_DisposesSeparableProgramWhenShaderPipelinesAreDisabled()
    {
        string materialSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Materials/XRMaterial.cs");
        string glMaterialSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Meshes/GLMaterial.cs");
        string engineSettingsSource = ReadWorkspaceFile("XREngine/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs");
        string runtimeSettingsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeEngineFacade.cs");

        materialSource.ShouldContain("public void DestroyShaderPipelineProgram()");
        materialSource.ShouldContain("public void SyncShaderPipelineProgramForCurrentSettings()");
        materialSource.ShouldContain("public static void DisposeShaderPipelineProgramsWhenDisabled()");
        materialSource.ShouldContain("EnsureShaderPipelineProgram(bool allowWhenShaderPipelinesDisabled = false)");
        materialSource.ShouldContain("if (!allowWhenShaderPipelinesDisabled && !RuntimeRenderingHostServices.Current.AllowShaderPipelines)");
        materialSource.ShouldContain("if (!RuntimeRenderingHostServices.Current.AllowShaderPipelines)");
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
        runtimeSettingsSource.ShouldContain("XRMaterial.DisposeShaderPipelineProgramsWhenDisabled();");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string fullPath = ResolveWorkspacePath(relativePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected workspace file does not exist: {fullPath}");
        return File.ReadAllText(fullPath);
    }

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
