using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class SkyboxAmbientContractTests
{
    [Test]
    public void DynamicProceduralSkybox_DrivesWorldAmbient()
    {
        string source = ReadCSharpFile("XREngine.Runtime.Rendering/Scene/Components/Misc/SkyboxComponent.cs");

        source.ShouldContain("private bool _syncGlobalAmbientLighting = true;");
        source.ShouldContain("public bool SyncGlobalAmbientLighting");
        source.ShouldContain("ApplyGlobalAmbientSync(sun, moon, sunDirection, moonDirection, sunKelvin, moonKelvin);");
        source.ShouldContain("WorldAs<IRuntimeRenderWorld>()?.AmbientSettings");
        source.ShouldContain("settings.AmbientLightColor = color;");
        source.ShouldContain("settings.AmbientLightIntensity = intensity;");
    }

    [Test]
    public void ForwardLighting_GlobalAmbientComesFromWorldSettings()
    {
        string source = ReadCSharpFile("XREngine.Runtime.Rendering/Rendering/Lights3DCollection.ForwardLighting.cs");

        source.ShouldContain("program.Uniform(\"GlobalAmbient\", (Vector3)World.GetEffectiveAmbientColor());");
        source.ShouldNotContain("program.Uniform(\"GlobalAmbient\", new Vector3(0.1f, 0.1f, 0.1f));");
    }

    [Test]
    public void DeferredPipelines_BindGlobalAmbientFromRenderingWorld()
    {
        string pipeline1 = ReadCSharpFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.cs");
        string pipeline2 = ReadCSharpFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs");

        pipeline1.ShouldContain("RenderingWorld?.GetEffectiveAmbientColor()");
        pipeline1.ShouldContain("program.Uniform(\"GlobalAmbient\", ResolveGlobalAmbient());");
        pipeline2.ShouldContain("RenderingWorld?.GetEffectiveAmbientColor()");
        pipeline2.ShouldContain("program.Uniform(\"GlobalAmbient\", ResolveGlobalAmbient());");
    }

    [Test]
    public void DeferredPipeline2_LightCombineFbosApplyAmbientAndProbeBindings()
    {
        string fboSource = ReadCSharpFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline2.FBOs.cs");

        fboSource.ShouldContain("lightCombineMat.SettingUniforms += (_, program) => ApplyLightCombineProgramBindings(program);");
        fboSource.ShouldContain("mat.SettingUniforms += (_, program) => ApplyLightCombineProgramBindings(program);");
    }

    [Test]
    public void DeferredPipeline_LightCombineDrawsPushAmbientAndProbeBindings()
    {
        string fboSource = ReadCSharpFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.FBOs.cs");
        string commandSource = ReadCSharpFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.CommandChain.cs");

        fboSource.ShouldContain("lightCombineMat.SettingUniforms += (_, program) => ApplyLightCombineProgramBindings(program);");
        fboSource.ShouldContain("mat.SettingUniforms += (_, program) => ApplyLightCombineProgramBindings(program);");
        commandSource.ShouldContain("x.ApplyUniforms = ApplyLightCombineProgramBindings");
        commandSource.ShouldContain("x.BindingLocation = DeferredLightProbePositionBufferBinding;");
        commandSource.ShouldContain("x.BindingLocation = DeferredLightProbeTetraBufferBinding;");
        commandSource.ShouldContain("x.BindingLocation = DeferredLightProbeParamBufferBinding;");
        commandSource.ShouldContain("x.BindingLocation = DeferredLightProbeGridCellBufferBinding;");
        commandSource.ShouldContain("x.BindingLocation = DeferredLightProbeGridIndexBufferBinding;");
    }

    private static string ReadCSharpFile(string relativePath)
    {
        string repoRoot = ResolveRepoRoot();
        string path = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"Expected C# file '{path}' to exist.");
        return File.ReadAllText(path);
    }

    private static string ResolveRepoRoot()
    {
        string? dir = TestContext.CurrentContext.TestDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "XRENGINE.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }

        Assert.Fail("Could not find repo root (XRENGINE.slnx).");
        return string.Empty;
    }
}
