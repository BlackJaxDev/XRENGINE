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
    public void PhysicsChainDispatchShaders_GuardTreeInvocationsBeforeBufferReads(string shaderFileName)
    {
        string source = ReadPhysicsChainShader(shaderFileName).Replace("\r\n", "\n");

        source.ShouldContain("uniform int ParticleCount;");
        source.ShouldContain("uniform int TreeCount;");
        int treeIndex = source.IndexOf("uint treeId = gl_GlobalInvocationID.x;", StringComparison.Ordinal);
        int guardIndex = source.IndexOf("if (ParticleCount <= 0 || TreeCount <= 0 || treeId >= uint(TreeCount))", StringComparison.Ordinal);
        int firstTreeRead = source.IndexOf("TreeParams[treeId]", StringComparison.Ordinal);
        int firstParticleStaticRead = source.IndexOf("ParticleStatics[pid]", StringComparison.Ordinal);
        int firstParticleRead = source.IndexOf("Particles[pid]", StringComparison.Ordinal);
        int firstTransformRead = source.IndexOf("TransformMatrices[pid]", StringComparison.Ordinal);

        treeIndex.ShouldBeGreaterThanOrEqualTo(0);
        guardIndex.ShouldBeGreaterThan(treeIndex);
        firstTreeRead.ShouldBeGreaterThan(guardIndex);
        firstParticleStaticRead.ShouldBeGreaterThan(guardIndex);
        firstParticleRead.ShouldBeGreaterThan(guardIndex);
        firstTransformRead.ShouldBeGreaterThan(guardIndex);
    }

    [Test]
    public void PhysicsChainDispatchers_SetParticleAndTreeCountUniformsBeforeDispatch()
    {
        string componentSource = ReadWorkspaceFile("XRENGINE/Scene/Components/Physics/PhysicsChainComponent.GPU.cs");
        string dispatcherSource = ReadWorkspaceFile("XRENGINE/Rendering/Compute/GPUPhysicsChainDispatcher.cs");

        componentSource.ShouldContain("_mainPhysicsProgram.Uniform(\"ParticleCount\", _totalParticleCount);");
        componentSource.ShouldContain("_skipUpdateParticlesProgram.Uniform(\"ParticleCount\", _totalParticleCount);");
        componentSource.ShouldContain("_mainPhysicsProgram.Uniform(\"TreeCount\", _particleTreesData.Count);");
        componentSource.ShouldContain("_skipUpdateParticlesProgram.Uniform(\"TreeCount\", _particleTreesData.Count);");
        dispatcherSource.ShouldContain("_mainPhysicsProgram.Uniform(\"ParticleCount\", TotalParticleCount);");
        dispatcherSource.ShouldContain("_mainPhysicsProgram.Uniform(\"TreeCount\", TotalTreeCount);");
    }

    [Test]
    public void MainPhysicsShader_OwnsEachTreeInOneInvocation()
    {
        string source = ReadPhysicsChainShader("PhysicsChain.comp").Replace("\r\n", "\n");

        source.ShouldContain("for (int pid = particleStart; pid < particleEnd; ++pid)");
        source.ShouldContain("parents precede children");
        source.ShouldContain("parentIndex >= particleStart && parentIndex < pid");
        source.ShouldNotContain("ParticleState parentStatic");
    }

    [Test]
    public void OpenGlRenderProgram_GeneratesBuffersBeforeComputeBinding()
    {
        string source = ReadGlRenderProgramLinkingSources();

        source.ShouldContain("Renderer.GetOrCreateAPIRenderObject(buffer, generateNow: true)");
    }

    private static string ReadPhysicsChainShader(string fileName)
        => ReadWorkspaceFile($"Build/CommonAssets/Shaders/Compute/PhysicsChain/{fileName}");

    private static string ReadGlRenderProgramLinkingSources()
        => string.Join('\n', new[]
        {
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.LinkOrchestration.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.CompileInputs.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.BinaryCacheInteraction.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.AsyncResults.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.HazardDetection.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.LinkDiagnostics.cs"),
        });

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
