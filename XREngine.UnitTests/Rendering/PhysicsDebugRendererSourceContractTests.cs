using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class PhysicsDebugRendererSourceContractTests
{
    [Test]
    public void WorldRenderer_UploadsEachGenerationOnceAndReusesItAcrossViews()
    {
        string renderer = ReadWorkspaceFile(
            "XRENGINE/Scene/Physics/Debug/PhysicsDebugFrameRenderer.cs");
        renderer.ShouldContain("if (_uploadedGeneration != frame.Generation)");
        renderer.ShouldContain("_uploadedGeneration = frame.Generation;");
        renderer.ShouldContain("ReusedViewCount++;");

        string world = ReadWorkspaceFile(
            "XRENGINE/Rendering/XRWorldInstance.RuntimeRenderWorld.cs");
        world.ShouldContain("private PhysicsDebugFrameRenderer _physicsDebugFrameRenderer = new();");
        world.ShouldContain("_physicsDebugFrameRenderer.Render(PhysicsScene.DebugFrames, depthMode);");
    }

    [Test]
    public void PersistentVisualizer_GrowsGeometricallyAndShrinksOnlyAfterHysteresis()
    {
        string visualizer = ReadWorkspaceFile(
            "XRENGINE/Scene/Physics/Physx/InstancedDebugVisualizer.cs");
        visualizer.ShouldContain("private const int ShrinkHysteresisFrames = 600;");
        visualizer.ShouldContain("grown = checked(grown + Math.Max(grown >> 1, 1u));");
        visualizer.ShouldContain("if (++underuseFrames < ShrinkHysteresisFrames)");
        visualizer.ShouldContain("CommitDirtyBytes(0u, _lineDirtyBytes)");
    }

    [Test]
    public void PhysxAndJolt_PublishFramesWithoutPerPrimitiveRuntimeServiceCalls()
    {
        string physx = ReadWorkspaceFile(
            "XREngine.Runtime.Core/Scene/Physics/Physx/PhysxScene.cs");
        string joltRenderer = ReadWorkspaceFile(
            "XREngine.Runtime.Core/Scene/Physics/Jolt/JoltEngineDebugRenderer.cs");

        physx.ShouldContain("PhysxDebugFrameAdapter.Copy(");
        physx.ShouldNotContain("services.RenderPoint");
        physx.ShouldNotContain("services.RenderLine");
        joltRenderer.ShouldContain("writer.AddLine(new PhysicsDebugLine");
        joltRenderer.ShouldNotContain("RuntimePhysicsServices.Current.RenderLine");
    }

    [Test]
    public void PhysicsCommand_ContainsNoBackendExtractionOrPopulation()
    {
        string command = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_RenderDebugPhysics.cs");
        command.ShouldContain("World?.DebugRenderPhysics(DepthMode);");
        command.ShouldNotContain("DebugRenderCollect");
        command.ShouldNotContain("PopulateBuffers");
        command.ShouldNotContain("Task.Run");
    }

    [Test]
    public void WorldReplacement_DisposesAndRecreatesRetainedDebugResources()
    {
        string world = ReadWorkspaceFile(
            "XRENGINE/Rendering/XRWorldInstance.RuntimeRenderWorld.cs");
        string lifecycle = ReadWorkspaceFile(
            "XRENGINE/Rendering/XRWorldInstance.cs");
        world.ShouldContain("_physicsDebugFrameRenderer.Dispose();");
        world.ShouldContain("_physicsDebugFrameRenderer = new PhysicsDebugFrameRenderer();");
        lifecycle.ShouldContain("ResetPhysicsDebugFrameRenderer();");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string? current = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            string candidate = Path.Combine(current, relativePath);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate).Replace("\r\n", "\n");
            current = Directory.GetParent(current)?.FullName;
        }

        throw new FileNotFoundException($"Could not locate repository file '{relativePath}'.");
    }
}
