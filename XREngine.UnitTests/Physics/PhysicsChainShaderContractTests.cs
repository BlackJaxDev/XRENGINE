using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainShaderContractTests
{
    [TestCase("PhysicsChain.comp")]
    [TestCase("SkipUpdateParticles.comp")]
    public void PhysicsChainDispatchShaders_GuardPaddedInvocationsBeforeBufferReads(string shaderFileName)
    {
        string source = ReadPhysicsChainShader(shaderFileName).Replace("\r\n", "\n");

        source.ShouldContain("uniform int ParticleCount;");
        int pidIndex = source.IndexOf("uint pid = gl_GlobalInvocationID.x;", StringComparison.Ordinal);
        int guardIndex = source.IndexOf("if (ParticleCount <= 0 || pid >= uint(ParticleCount))", StringComparison.Ordinal);
        int firstParticleStaticRead = source.IndexOf("ParticleStatics[pid]", StringComparison.Ordinal);
        int firstParticleRead = source.IndexOf("Particles[pid]", StringComparison.Ordinal);
        int firstTransformRead = source.IndexOf("TransformMatrices[pid]", StringComparison.Ordinal);

        pidIndex.ShouldBeGreaterThanOrEqualTo(0);
        guardIndex.ShouldBeGreaterThan(pidIndex);
        firstParticleStaticRead.ShouldBeGreaterThan(guardIndex);
        firstParticleRead.ShouldBeGreaterThan(guardIndex);
        firstTransformRead.ShouldBeGreaterThan(guardIndex);
    }

    [Test]
    public void PhysicsChainDispatchers_SetParticleCountUniformBeforeDispatch()
    {
        string componentSource = ReadWorkspaceFile("XRENGINE/Scene/Components/Physics/PhysicsChainComponent.GPU.cs");
        string dispatcherSource = ReadWorkspaceFile("XRENGINE/Rendering/Compute/GPUPhysicsChainDispatcher.cs");

        componentSource.ShouldContain("_mainPhysicsProgram.Uniform(\"ParticleCount\", _totalParticleCount);");
        componentSource.ShouldContain("_skipUpdateParticlesProgram.Uniform(\"ParticleCount\", _totalParticleCount);");
        dispatcherSource.ShouldContain("_mainPhysicsProgram.Uniform(\"ParticleCount\", TotalParticleCount);");
        dispatcherSource.ShouldContain("_skipUpdateProgram.Uniform(\"ParticleCount\", TotalParticleCount);");
    }

    [Test]
    public void OpenGlRenderProgram_GeneratesBuffersBeforeComputeBinding()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/OpenGL/Types/Meshes/GLRenderProgram.Linking.cs");

        source.ShouldContain("Renderer.GetOrCreateAPIRenderObject(buffer, generateNow: true)");
    }

    private static string ReadPhysicsChainShader(string fileName)
        => ReadWorkspaceFile($"Build/CommonAssets/Shaders/Compute/PhysicsChain/{fileName}");

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
