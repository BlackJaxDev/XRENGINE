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
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Physics/DebugVisualization/InstancedDebugVisualizer.cs").Replace("\r\n", "\n");

        source.ShouldContain("private void MarkLinesDirty()\n            => _lineDirtyBytes = checked(");

        string setLineMethod = SliceMethod(source, "public unsafe void SetLineAt");
        setLineMethod.ShouldContain("MarkLinesDirty();");

        string directMemoryMethod = SliceMethod(source, "private void PopulateBuffersDirectMemory()");
        AssertContainsInOrder(
            directMemoryMethod,
            "bulkLn(_debugLinesBuffer.Address, lnCount);",
            "MarkLinesDirty();");

        string pushLineMethod = SliceMethod(source, "public void PushLinesBuffer()");
        pushLineMethod.ShouldContain("_debugLinesBuffer.CommitDirtyBytes(0u, _lineDirtyBytes);");
    }

    [Test]
    public void OpenGlRenderPath_PreparesDynamicRenderDataEveryDrawAfterBuffersAreBound()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/MeshRendering/GLMeshRenderer.Rendering.cs").Replace("\r\n", "\n");

        AssertContainsInOrder(
            source,
            "if (!BuffersBound)\n                {\n                    Renderer.MeshGenerationQueue.EnqueueGeneration(this);",
            "return indirectArguments is null;\n                }",
            "PrepareDynamicRenderData();",
            "BindSSBOs(mat!);",
            "BindSSBOs(vtx!);");
    }

    [Test]
    public void OpenGlUploadQueue_PredictiveSkipCannotPreventFirstChunkProgress()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/Resources/Uploads/GLUploadQueue.cs").Replace("\r\n", "\n");

        AssertContainsInOrder(
            source,
            "while (!_renderer._oomDetectedThisFrame && sw.Elapsed.TotalMilliseconds < budgetMs)",
            "if (LastDequeuedItems > 0 && _recentMaxChunkMs > 0.0 && elapsedMs + _recentMaxChunkMs > budgetMs)",
            "LastPredictiveSkipCount++;",
            "if (!_pendingUploads.TryDequeue(out var upload))",
            "LastDequeuedItems++;");
    }

    [Test]
    public void DebugPrimitiveQueues_AreScopedToTheActiveVisualScene()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeEngine.Rendering.Debug.cs").Replace("\r\n", "\n");

        source.ShouldContain("private sealed class DebugPrimitiveSceneState");
        source.ShouldContain("private static readonly DebugPrimitiveSceneState _debug3D = new();");
        source.ShouldContain("private static readonly DebugPrimitiveSceneState _debug2D = new();");
        source.ShouldContain("RuntimeEngine.Rendering.State.RenderingScene is VisualScene2D");

        string renderShapes = SliceMethod(source, "public static void RenderShapes(bool depthTested)");
        renderShapes.ShouldContain("DebugPrimitiveSceneState scene = ResolveDebugPrimitiveSceneState();");
        renderShapes.ShouldContain("scene.Visualizer.Render();");

        string renderPoint = SliceMethod(source, "public static void RenderPoint");
        AssertContainsInOrder(
            renderPoint,
            "DebugPrimitiveSceneState scene = ResolveDebugPrimitiveSceneState();",
            "scene.Points.Add((position, color));",
            "scene.PointQueue.Enqueue((position, color));");

        string renderLine = SliceMethod(source, "public static unsafe void RenderLine");
        AssertContainsInOrder(
            renderLine,
            "DebugPrimitiveSceneState scene = ResolveDebugPrimitiveSceneState();",
            "scene.Lines.Add((start, end, color));",
            "scene.LineQueue.Enqueue((start, end, color));");

        string renderTriangle = SliceMethod(source, "public static void RenderTriangle(\n                    Vector3 A,");
        AssertContainsInOrder(
            renderTriangle,
            "DebugPrimitiveSceneState scene = ResolveDebugPrimitiveSceneState();",
            "scene.Triangles.Add((A, B, C, color));",
            "scene.TriangleQueue.Enqueue((A, B, C, color));");
    }

    [Test]
    public void ScreenSpaceUiTransformDebug_OnlyRunsInThe2DVisualScene()
    {
        string uiTransform = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/UI/Core/Transforms/UITransform.cs").Replace("\r\n", "\n");
        string uiBoundableTransform = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/UI/Core/Transforms/UIBoundableTransform.cs").Replace("\r\n", "\n");

        uiTransform.ShouldContain("DebugRenderInfo2D.PreCollectCommandsCallback = ShouldRenderDebug2D;");
        uiTransform.ShouldContain("=> IsScreenSpaceCanvas();");
        uiTransform.ShouldContain("=> !IsScreenSpaceCanvas() || RuntimeEngine.Rendering.State.RenderingScene is VisualScene2D;");

        string uiRenderDebug = SliceMethod(uiTransform, "protected override void RenderDebug()");
        AssertContainsInOrder(
            uiRenderDebug,
            "if (!ShouldRenderDebugInCurrentScene())",
            "return;",
            "base.RenderDebug();");

        string boundableRenderDebug = SliceMethod(uiBoundableTransform, "protected override void RenderDebug()");
        AssertContainsInOrder(
            boundableRenderDebug,
            "if (!ShouldRenderDebugInCurrentScene())",
            "return;",
            "base.RenderDebug();");
    }

    [Test]
    public void InstancedDebugRenderers_ExpandPrimitivesInTheVertexStage()
    {
        string[] rendererPaths =
        [
            "XREngine.Runtime.Rendering/Rendering/Physics/DebugVisualization/InstancedDebugVisualizer.cs",
            "XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhDebugLineRenderer.cs",
            "XREngine.Runtime.Rendering/Rendering/Compute/GpuBoundsDebugLineRenderer.cs",
            "XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.Debug.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/GI/DDGIProbeDebugResources.cs",
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_RenderDebugGpuBvh.cs",
        ];

        foreach (string path in rendererPaths)
        {
            string source = ReadWorkspaceFile(path);
            source.ShouldContain("DebugPrimitiveShaders.LoadVertexShaders(", customMessage: path);
            source.ShouldNotContain("EShaderType.Geometry", customMessage: path);
            source.ShouldNotContain("forceNoStereo: true", customMessage: path);
        }

        string expansion = ReadWorkspaceFile("Build/CommonAssets/Shaders/Common/Debug/helper/DebugInstanceExpansion.glslinc");
        expansion.ShouldContain("gl_InstanceID");
        expansion.ShouldNotContain("EmitVertex");
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

    private static int CountOccurrences(string source, string text)
    {
        int count = 0;
        int index = 0;

        while ((index = source.IndexOf(text, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += text.Length;
        }

        return count;
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
        return File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
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
