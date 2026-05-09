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
        string rendererSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/OpenGLRenderer.cs");
        string programSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Meshes/GLRenderProgram.Linking.cs");

        rendererSource.ShouldContain("if (glProgram is null || glMesh is null || !glProgram.IsLinked)");
        programSource.ShouldContain("if (!Data.LinkReady || !Link())");
    }

    [Test]
    public void LargeIndirectPrograms_RouteAwayFromDriverParallelLinks()
    {
        string programSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Meshes/GLRenderProgram.Linking.cs");
        string selectorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/OpenGLShaderLinkBackendSelector.cs");

        programSource.ShouldContain("DriverParallelLargeSourceSharedContextThresholdBytes");
        programSource.ShouldContain("ShouldPreferSharedContextForLargeSource(inputs)");
        selectorSource.ShouldContain("PreferSharedContextForLargeSource");
        selectorSource.ShouldContain("large source program routed to shared-context lane to avoid driver-parallel timeout");
    }

    [Test]
    public void IndirectVertexShaders_EmitWorldSpaceFragPos_ForForwardUberLighting()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");

        source.ShouldContain("FragPos = worldPos.xyz;");
        source.ShouldNotContain("FragPos = clipPos.xyz / max(clipPos.w, 1e-6);");
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
}
