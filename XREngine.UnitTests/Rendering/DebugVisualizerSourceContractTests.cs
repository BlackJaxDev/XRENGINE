using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class DebugVisualizerSourceContractTests
{
    [Test]
    public void LineInstanceRewrites_ForceFreshUploadWhenCountIsUnchanged()
    {
        string source = ReadWorkspaceFile("XRENGINE/Scene/Physics/Physx/InstancedDebugVisualizer.cs").Replace("\r\n", "\n");

        source.ShouldContain("private void MarkLinesDirty()\n            => _fullPushLines = true;");

        string setLineMethod = SliceMethod(source, "public unsafe void SetLineAt");
        setLineMethod.ShouldContain("MarkLinesDirty();");

        string directMemoryMethod = SliceMethod(source, "private void PopulateBuffersDirectMemory()");
        AssertContainsInOrder(
            directMemoryMethod,
            "bulkLn(_debugLinesBuffer.Address, lnCount);",
            "MarkLinesDirty();");
    }

    [Test]
    public void OpenGlRenderPath_PreparesDynamicRenderDataEveryDrawAfterBuffersAreBound()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Mesh Renderer/GLMeshRenderer.Rendering.cs").Replace("\r\n", "\n");

        AssertContainsInOrder(
            source,
            "if (!BuffersBound)\n                    {\n                        Renderer.MeshGenerationQueue.EnqueueGeneration(this);",
            "return;\n                    }",
            "PrepareDynamicRenderData();",
            "BindSSBOs(mat!);",
            "BindSSBOs(vtx!);");
    }

    private static void AssertContainsInOrder(string source, params string[] expected)
    {
        int previousIndex = -1;
        foreach (string text in expected)
        {
            int index = source.IndexOf(text, previousIndex + 1, StringComparison.Ordinal);
            index.ShouldBeGreaterThan(previousIndex, $"Expected '{text}' after index {previousIndex}.");
            previousIndex = index;
        }
    }

    private static string SliceMethod(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Could not find method signature '{signature}'.");

        int openBrace = source.IndexOf('{', start);
        openBrace.ShouldBeGreaterThanOrEqualTo(start, $"Could not find method body for '{signature}'.");

        int depth = 0;
        for (int i = openBrace; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}')
                depth--;

            if (depth == 0)
                return source[start..(i + 1)];
        }

        throw new InvalidOperationException($"Could not find method end for '{signature}'.");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string repoRoot = ResolveRepoRoot();
        string path = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"Expected workspace file '{path}' to exist.");
        return File.ReadAllText(path);
    }

    private static string ResolveRepoRoot()
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "XRENGINE.slnx")))
                return directory;

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test directory.");
    }
}