using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class CaptureLayerMaskTests
{
    [Test]
    public void ShadowCaptureCameras_ExcludeGizmoLayer()
    {
        string oneViewSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Lights/Types/OneViewLightComponent.cs");
        string pointLightSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Lights/Types/PointLightComponent.cs");

        oneViewSource.ShouldContain("cam.CullingMask = DefaultLayers.EverythingExceptGizmos;");
        pointLightSource.ShouldContain("cam.CullingMask = DefaultLayers.EverythingExceptGizmos;");
    }

    [Test]
    public void GlobalDebugShapes_HonorCameraGizmoMask()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeEngine.Rendering.Debug.cs");

        source.ShouldContain("RuntimeEngine.Rendering.State.RenderingCamera is { } camera");
        source.ShouldContain("!camera.CullingMask.Contains(XREngine.Components.Scene.Transforms.DefaultLayers.GizmosIndex)");
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
