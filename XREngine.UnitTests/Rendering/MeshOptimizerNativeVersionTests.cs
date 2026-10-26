using System.Runtime.InteropServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Meshlets;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class MeshOptimizerNativeVersionTests
{
    [OneTimeSetUp]
    public void InstallMeshProcessing() => MeshOptimizerBackend.Register();

    [Test]
    public void NativeVersion_IsLockedRelease()
        => MeshOptimizerNative.VersionKey.ShouldStartWith("v1.3 ");

    [Test]
    public void EncodeMeshlet_EntryPointResolves()
    {
        uint[] vertices = [0u, 1u, 2u];
        byte[] triangles = [0, 1, 2];

        int encodedSize = 0;
        Should.NotThrow(() => encodedSize = MeshOptimizerNative.EncodeMeshlet(vertices, triangles, 64, 124));
        encodedSize.ShouldBeGreaterThan(0);
    }

    [Test]
    public void OptimizeMeshletLevel_EntryPointResolves()
    {
        NativeLibrary.TryLoad("meshoptimizer", typeof(MeshOptimizerBackend).Assembly, null, out nint handle).ShouldBeTrue();
        NativeLibrary.TryGetExport(handle, "meshopt_optimizeMeshletLevel", out nint export).ShouldBeTrue();
        export.ShouldNotBe(nint.Zero);
    }
}
