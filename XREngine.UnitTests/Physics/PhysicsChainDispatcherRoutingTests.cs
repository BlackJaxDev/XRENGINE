using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainDispatcherRoutingTests
{
    [Test]
    public void IsolatedModeUsesDispatcherBuffersInsteadOfLegacyComponentBuffers()
    {
        string componentSource = ReadWorkspaceFile("XRENGINE/Scene/Components/Physics/PhysicsChainComponent.GPU.cs")
            .Replace("\r\n", "\n");
        componentSource.ShouldContain("SubmitToBatchedDispatcher(loop, timeVar)");
        componentSource.ShouldNotContain("private bool TryGetGpuParticleRenderSource");
        componentSource.ShouldNotContain("XRDataBuffer<GPUParticleData>? _particlesBuffer");

        string dispatcherSource = ReadWorkspaceFile("XRENGINE/Rendering/Compute/GPUPhysicsChainDispatcher.cs");
        dispatcherSource.ShouldContain("public bool TryGetRenderParticleBuffers(");
        dispatcherSource.ShouldContain("component.UseBatchedDispatcher ? 0 : request.RequestId");
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
