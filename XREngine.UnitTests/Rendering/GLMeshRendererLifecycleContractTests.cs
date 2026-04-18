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
        string source = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/OpenGL/Types/Mesh Renderer/GLMeshRenderer.Lifecycle.cs");

        source.ShouldContain("case nameof(XRMeshRenderer.Material):");
        source.ShouldContain("OnMaterialChanged();");
        source.ShouldContain("Data.ResetVertexShaderSource();");
        source.ShouldContain("Engine.EnqueueMainThreadTask(RegenerateProgramsAndBuffers, \"GLMeshRenderer.MaterialChanged\");");
        source.ShouldContain("_combinedProgram?.Destroy();");
        source.ShouldContain("_separatedVertexProgram?.Destroy();");
        source.ShouldContain("_forcedGeneratedVertexProgram?.Destroy();");
        source.ShouldContain("_pipeline?.Destroy();");
        source.ShouldContain("BuffersBound = false;");
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