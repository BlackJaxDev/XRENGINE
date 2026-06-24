using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GpuIndirectProgramContractTests
{
    [Test]
    public void IndirectProgramCache_ReissuesLinkRequests_And_SeesMeshVertexBuffers()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");

        source.ShouldContain("existing.Program.Link();");
        source.ShouldContain("renderer.Mesh?.Buffers is not null && renderer.Mesh.Buffers.TryGetValue(binding, out _)");
        source.ShouldContain("renderer.Mesh?.Buffers is IEventDictionary<string, XRDataBuffer> meshBuffers");
        source.ShouldContain("renderer.Buffers is IEventDictionary<string, XRDataBuffer> rendererBuffers");
    }

    [Test]
    public void IndirectProgramCache_KeepsLastKnownGoodUntilReplacementLinks()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");

        source.ShouldContain("private readonly Dictionary<(uint materialId, int rendererKey), MaterialProgramCache> _pendingMaterialPrograms = [];");
        source.ShouldContain("pending.ShaderStateRevision == shaderStateRevision");
        source.ShouldContain("if (IsProgramReadyForCurrentRenderer(pending.Program))");
        source.ShouldContain("return existing.Program;");
        source.ShouldContain("program.APIWrappers");
        source.ShouldContain("glProgram?.IsLinked == true");
    }

    [Test]
    public void OpenGlIndirectBinding_SkipsUnlinkedPrograms_And_UsePollsLinkState()
    {
        string rendererSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Bootstrap/OpenGLRenderer.cs");
        string programSource = ReadGlRenderProgramLinkingSources();

        rendererSource.ShouldContain("if (glProgram is null || glMesh is null || !glProgram.IsLinked)");
        programSource.ShouldContain("if (!Data.LinkReady || !Link())");
    }

    [Test]
    public void LargeIndirectPrograms_RouteAwayFromDriverParallelLinks()
    {
        string programSource = ReadGlRenderProgramLinkingSources();
        string selectorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Pipelines/OpenGLShaderLinkBackendSelector.cs");

        programSource.ShouldContain("DriverParallelLargeSourceSharedContextThresholdBytes");
        programSource.ShouldContain("ShouldPreferSharedContextForLargeSource(inputs)");
        selectorSource.ShouldContain("PreferSharedContextForLargeSource");
        selectorSource.ShouldContain("large source program routed to shared-context lane to avoid driver-parallel timeout");
    }

    [Test]
    public void ZeroReadbackProgramWarmup_UsesCpuSafetyNetWithoutForcedOpenGlLinks()
    {
        string managerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");
        string gpuPassSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection.Core.cs");
        string commandSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/MeshRendering/Traditional/VPRC_RenderMeshesPassTraditional.cs");

        managerSource.ShouldContain("EnsureZeroReadbackMaterialSlotProgramsReady(");
        managerSource.ShouldContain("EnsureZeroReadbackActiveBucketProgramsReady(");
        managerSource.ShouldContain("renderPasses.RecordZeroReadbackProgramPending();");
        managerSource.ShouldContain("WarnZeroReadbackProgramWarmup(");
        managerSource.ShouldNotContain("TryForceSynchronousOpenGLProgramLink");
        managerSource.ShouldNotContain("forceSynchronousLink");
        gpuPassSource.ShouldContain("ZeroReadbackProgramPendingThisFrame");
        commandSource.ShouldContain("ShouldUseOpenGLZeroReadbackProgramWarmupFallback");
        commandSource.ShouldContain("RenderCPUMeshOnly(command.RenderPass)");
    }

    [Test]
    public void IndirectVertexShaders_EmitWorldSpaceFragPos_ForForwardUberLighting()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");

        source.ShouldContain("FragPos = worldPos.xyz;");
        source.ShouldNotContain("FragPos = clipPos.xyz / max(clipPos.w, 1e-6);");
    }

    [Test]
    public void IndirectVertexShaders_ReconstructCpuMatricesLikeUniformUpload()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");

        source.ShouldContain("vec4 c0 = vec4(culled[base+0],  culled[base+1],  culled[base+2],  culled[base+3]);");
        source.ShouldContain("vec4 c3 = vec4(culled[base+12], culled[base+13], culled[base+14], culled[base+15]);");
        source.ShouldContain("vec4 c0 = vec4(instanceWorld[base+0],  instanceWorld[base+1],  instanceWorld[base+2],  instanceWorld[base+3]);");
        source.ShouldContain("vec4 c3 = vec4(instanceWorld[base+12], instanceWorld[base+13], instanceWorld[base+14], instanceWorld[base+15]);");
        source.ShouldNotContain("vec4 c0 = vec4(culled[base+0], culled[base+4], culled[base+8],  culled[base+12]);");
        source.ShouldNotContain("vec4 c0 = vec4(instanceWorld[base+0], instanceWorld[base+4], instanceWorld[base+8],  instanceWorld[base+12]);");
    }

    [Test]
    public void IndirectVertexShaders_PreserveForwardViewIndexSlot()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");

        source.ShouldContain("FragLodTransitionRoleLocation = 23;");
        source.ShouldContain("layout(location=22) out float");
        source.ShouldContain("layout(location = 22) out float");
        source.ShouldContain("FragViewIndexName} = 0.0;");
        source.ShouldContain("layout(location={FragLodTransitionRoleLocation}) flat out uint");
        source.ShouldContain("layout(location = {FragLodTransitionRoleLocation}) flat in uint");
        source.ShouldNotContain("layout(location=22) flat out uint");
        source.ShouldNotContain("layout(location = 22) flat in uint");
    }

    [Test]
    public void IndirectVertexShaders_EmitDefaultForwardVaryingsWhenOptionalMeshBuffersAreMissing()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");

        CountOccurrences(source, "layout(location=2) out vec3 FragTan;").ShouldBeGreaterThanOrEqualTo(2);
        CountOccurrences(source, "layout(location=3) out vec3 FragBinorm;").ShouldBeGreaterThanOrEqualTo(2);
        CountOccurrences(source, "layout(location=4) out vec2 {string.Format(DefaultVertexShaderGenerator.FragUVName, 0)};").ShouldBeGreaterThanOrEqualTo(2);
        CountOccurrences(source, "layout(location=12) out vec4 {string.Format(DefaultVertexShaderGenerator.FragColorName, 0)};").ShouldBeGreaterThanOrEqualTo(2);
        CountOccurrences(source, "{string.Format(DefaultVertexShaderGenerator.FragUVName, 0)} = vec2(0.0);").ShouldBeGreaterThanOrEqualTo(2);
        CountOccurrences(source, "{string.Format(DefaultVertexShaderGenerator.FragColorName, 0)} = vec4(1.0);").ShouldBeGreaterThanOrEqualTo(2);
    }

    [Test]
    public void IndirectLodAugmentation_GuardsPrepassOnlyTransformIdDeclarations()
    {
        string source = ReadWorkspaceFile("Build/CommonAssets/Shaders/Uber/UberShader.frag");
        string augmentedSource = InvokeTryAugmentIndirectFragmentShader(source);

        augmentedSource.ShouldContain("#if !");
        augmentedSource.ShouldContain("defined(XRENGINE_DEPTH_NORMAL_PREPASS)");
        augmentedSource.ShouldContain("layout(location = 21) in float FragTransformId;");
        augmentedSource.ShouldContain("XRE_ApplyLodTransitionDither();");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string fullPath = ResolveWorkspacePath(relativePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected file does not exist: {fullPath}");
        return File.ReadAllText(fullPath);
    }

    private static string ResolveWorkspacePath(string relativePath)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not resolve workspace path for '{relativePath}' from test base directory '{AppContext.BaseDirectory}'.");
    }

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

    private static string InvokeTryAugmentIndirectFragmentShader(string source)
    {
        Type type = Type.GetType("XREngine.Rendering.HybridRenderingManager, XREngine.Runtime.Rendering")
            ?? throw new TypeLoadException("Could not load XREngine.Rendering.HybridRenderingManager.");
        MethodInfo method = type.GetMethod(
            "TryAugmentIndirectFragmentShader",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(type.FullName, "TryAugmentIndirectFragmentShader");

        object?[] args = [source, null];
        ((bool)method.Invoke(null, args)!).ShouldBeTrue();
        return (string)args[1]!;
    }

    private static int CountOccurrences(string source, string value)
        => source.Split(value, StringSplitOptions.None).Length - 1;
}
