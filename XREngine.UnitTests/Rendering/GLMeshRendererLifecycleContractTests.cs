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
    public void GLMeshRenderer_UsesCombinedProgramsWithUberPipelineFallbackWhenPipelinesAreDisabled()
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
        source.ShouldContain("allowWhenShaderPipelinesDisabled: true");
        source.ShouldContain("material.Data.TryGetUberMaterialState(out _, out _)");
        source.ShouldContain("private void EnsureCombinedProgramForMaterial(GLMaterial material)");
        source.ShouldNotContain("ShouldForceSeparableUberProgram");
        source.ShouldNotContain("|| forceShaderPipelines");
        source.ShouldNotContain("|| materialDiffers");
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
