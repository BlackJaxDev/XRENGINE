using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Commands;

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

        source.ShouldContain("private readonly Dictionary<XRRenderProgramDescriptor, MaterialProgramCache> _pendingMaterialPrograms = [];");
        source.ShouldContain("!previousDescriptor.Equals(descriptor)");
        source.ShouldContain("_materialPrograms.TryGetValue(previousDescriptor, out MaterialProgramCache previousCache)");
        source.ShouldContain("if (IsProgramReadyForCurrentRenderer(pending.Program))");
        source.ShouldContain("return existing.Program;");
        source.ShouldContain("program.APIWrappers");
        source.ShouldContain("TryGetBackendCapability<IRenderProgramBackendCapability>");
        source.ShouldContain("return capability.IsProgramReady(program);");
    }

    [Test]
    public void OpenGlIndirectBinding_SkipsUnlinkedPrograms_And_UsePollsLinkState()
    {
        string rendererSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/Bootstrap/OpenGLRenderer.cs");
        string programSource = ReadGlRenderProgramLinkingSources();

        rendererSource.ShouldContain("if (glProgram is null || glMesh is null || !glProgram.IsLinked)");
        programSource.ShouldContain("if (!Data.LinkReady || !Link(nonBlocking: true))");
    }

    [Test]
    public void LargeIndirectPrograms_RouteAwayFromDriverParallelLinks()
    {
        string programSource = ReadGlRenderProgramLinkingSources();
        string selectorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/Pipelines/OpenGLShaderLinkBackendSelector.cs");

        programSource.ShouldContain("LargeSourceSharedContextPreferenceThresholdBytes");
        programSource.ShouldContain("ShouldPreferSharedContextForLargeSource(inputs)");
        selectorSource.ShouldContain("PreferSharedContextForLargeSource");
        selectorSource.ShouldContain("large source program routed to shared-context lane to avoid driver-parallel timeout");
    }

    [Test]
    public void ZeroReadbackProgramWarmup_UsesCpuSafetyNetWithoutForcedOpenGlLinks()
    {
        string managerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");
        string gpuPassSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.Core.cs");
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

        source.ShouldContain("CPU Matrix4x4 rows are intentionally reinterpreted as GLSL columns, matching uniform upload.");
        source.ShouldContain("vec4 c0 = vec4({transformAccess}[base+0],  {transformAccess}[base+1],  {transformAccess}[base+2],  {transformAccess}[base+3]);");
        source.ShouldContain("vec4 c3 = vec4({transformAccess}[base+12], {transformAccess}[base+13], {transformAccess}[base+14], {transformAccess}[base+15]);");
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
        CountOccurrences(source, "layout(location={4 + i}) out vec2 {string.Format(DefaultVertexShaderGenerator.FragUVName, i)};").ShouldBeGreaterThanOrEqualTo(2);
        CountOccurrences(source, "layout(location=12) out vec4 {string.Format(DefaultVertexShaderGenerator.FragColorName, 0)};").ShouldBeGreaterThanOrEqualTo(2);
        CountOccurrences(source, "string uv0Source = texCoordBindings.Count > 0 ? texCoordBindings[0] : \"vec2(0.0)\";").ShouldBeGreaterThanOrEqualTo(2);
        CountOccurrences(source, "{string.Format(DefaultVertexShaderGenerator.FragColorName, 0)} = vec4(1.0);").ShouldBeGreaterThanOrEqualTo(2);
    }

    [Test]
    public void IndirectLodAugmentation_GuardsPrepassOnlyTransformIdDeclarations()
    {
        string source = ReadWorkspaceFile("Build/CommonAssets/Shaders/Uber/UberShader.frag");
        string augmentedSource = InvokeTryAugmentIndirectFragmentShader(source);

        augmentedSource.ShouldContain("#if !");
        augmentedSource.ShouldContain("defined(XRENGINE_DEPTH_NORMAL_PREPASS)");
        augmentedSource.ShouldContain("layout(location = 21) flat in uint FragTransformId;");
        augmentedSource.ShouldContain("XRE_ApplyLodTransitionDither();");
    }

    [Test]
    public void DrawIndexAndRenderIdentityVaryings_UseFlatUintWithoutSubnormalFloatTransport()
    {
        string managerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");
        string defaultGeneratorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Shaders/Generator/DefaultVertexShaderGenerator.cs");
        string deformGeneratorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Shaders/Generator/MeshDeformVertexShaderGenerator.cs");
        string deferredSource = ReadWorkspaceFile("Build/CommonAssets/Shaders/Common/TexturedDeferred.fs");
        string uberSource = ReadWorkspaceFile("Build/CommonAssets/Shaders/Uber/UberShader.frag");
        string meshletSource = ReadWorkspaceFile("Build/CommonAssets/Shaders/Meshlets/MeshletRender.mesh");

        defaultGeneratorSource.ShouldContain("layout (location = 21) flat out uint {FragTransformIdName};");
        defaultGeneratorSource.ShouldContain("Line($\"{FragTransformIdName} = _xreTransformId;\");");
        defaultGeneratorSource.ShouldContain("layout (location = 27) flat out uint {FragRenderIdentityIdName};");
        defaultGeneratorSource.ShouldContain("Line($\"{FragRenderIdentityIdName} = TransformId;\");");
        deformGeneratorSource.ShouldContain("layout (location = 21) flat out uint {FragTransformIdName};");
        deformGeneratorSource.ShouldContain("Line($\"{FragTransformIdName} = _xreTransformId;\");");
        deformGeneratorSource.ShouldContain("layout (location = 27) flat out uint {FragRenderIdentityIdName};");
        deformGeneratorSource.ShouldContain("Line($\"{FragRenderIdentityIdName} = TransformId;\");");
        managerSource.ShouldContain("layout(location=21) flat out uint {DefaultVertexShaderGenerator.FragTransformIdName};");
        managerSource.ShouldContain("layout(location=21) flat in uint {DefaultVertexShaderGenerator.FragTransformIdName};");
        managerSource.ShouldContain("layout(location=27) flat out uint {DefaultVertexShaderGenerator.FragRenderIdentityIdName};");
        managerSource.ShouldContain("layout(location=27) flat in uint {DefaultVertexShaderGenerator.FragRenderIdentityIdName};");
        managerSource.ShouldContain("uint renderIdentityID = {DefaultVertexShaderGenerator.FragRenderIdentityIdName};");
        managerSource.ShouldContain("FragRenderIdentityIdName} = XRE_LoadDrawMetadata(commandIndex).RenderIdentityID;");
        deferredSource.ShouldContain("layout (location = 21) flat in uint FragTransformId;");
        deferredSource.ShouldContain("layout (location = 27) flat in uint FragRenderIdentityId;");
        deferredSource.ShouldContain("TransformId = FragRenderIdentityId;");
        uberSource.ShouldContain("layout(location = 21) flat in uint FragTransformId;");
        uberSource.ShouldContain("layout(location = 27) flat in uint FragRenderIdentityId;");
        meshletSource.ShouldContain("layout(location = 21) flat out uint FragTransformId[];");
        meshletSource.ShouldContain("layout(location = 27) flat out uint FragRenderIdentityId[];");
        meshletSource.ShouldContain("FragRenderIdentityId[tid] = draw.RenderIdentityID;");

        string combined = string.Join(
            '\n',
            managerSource,
            defaultGeneratorSource,
            deformGeneratorSource,
            deferredSource,
            uberSource,
            meshletSource);
        combined.ShouldNotContain("floatBitsToUint(FragTransformId)");
        combined.ShouldNotContain("uintBitsToFloat(_xreTransformId)");
        combined.ShouldNotContain("uintBitsToFloat(draw.DrawID)");
    }

    [Test]
    public void RenderIdentity_MetadataAbiRemains64BytesAndHasExplicitOverflowGuard()
    {
        Marshal.SizeOf<DrawMetadata>().ShouldBe(64);
        Marshal.OffsetOf<DrawMetadata>(nameof(DrawMetadata.RenderIdentityID)).ToInt32().ShouldBe(52);

        string renderCommandSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommand.cs");
        renderCommandSource.ShouldContain("value <= 0L || value > uint.MaxValue");
        renderCommandSource.ShouldContain("exhausted the 32-bit stable render-command identity space");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string fullPath = ResolveWorkspacePath(relativePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected file does not exist: {fullPath}");
        return global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType(relativePath);
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
            ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.LinkOrchestration.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.CompileInputs.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.BinaryCacheInteraction.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.AsyncResults.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.HazardDetection.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.LinkDiagnostics.cs"),
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
