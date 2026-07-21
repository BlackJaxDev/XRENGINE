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
        int methodStart = componentSource.IndexOf("private bool TryGetGpuParticleRenderSource", StringComparison.Ordinal);
        int methodEnd = componentSource.IndexOf("private void EnsureGpuDebugResources", methodStart, StringComparison.Ordinal);
        methodStart.ShouldBeGreaterThanOrEqualTo(0);
        methodEnd.ShouldBeGreaterThan(methodStart);

        string method = componentSource[methodStart..methodEnd];
        method.ShouldContain("GPUPhysicsChainDispatcher.Instance.TryGetRenderParticleBuffers");
        method.ShouldNotContain("if (UseBatchedDispatcher)");
        method.ShouldNotContain("particleBuffer = _particlesBuffer;");

        string dispatcherSource = ReadWorkspaceFile("XRENGINE/Rendering/Compute/GPUPhysicsChainDispatcher.cs");
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
