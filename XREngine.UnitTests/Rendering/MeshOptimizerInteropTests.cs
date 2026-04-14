using NUnit.Framework;
using XREngine.Rendering.Meshlets;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class MeshOptimizerInteropTests
{
    [Test]
    public void OptimizeMeshletLevel_UsesAvailableNativeExportWithoutThrowing()
    {
        uint[] meshletVertices = [0u, 1u, 2u];
        byte[] meshletTriangles = [0, 1, 2];

        Assert.DoesNotThrow(() => MeshOptimizerNative.OptimizeMeshletLevel(meshletVertices.AsSpan(), meshletTriangles.AsSpan(), level: 4));
    }
}