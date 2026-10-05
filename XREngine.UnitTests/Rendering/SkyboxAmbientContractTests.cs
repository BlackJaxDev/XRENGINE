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
        source.ShouldContain("World.GetRenderWorld()?.AmbientSettings");
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
        string pipeline1 = ReadCSharpFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");
        string advancedPipeline = ReadCSharpFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.cs");

        pipeline1.ShouldContain("RenderingWorld?.GetEffectiveAmbientColor()");
        pipeline1.ShouldContain("program.Uniform(\"GlobalAmbient\", ResolveGlobalAmbient());");
        advancedPipeline.ShouldContain("RenderingWorld?.GetEffectiveAmbientColor()");
        advancedPipeline.ShouldContain("program.Uniform(\"GlobalAmbient\", ResolveGlobalAmbient());");
    }

    [Test]
    public void DeferredAdvancedPipeline_LightCombineFbosApplyAmbientAndProbeBindings()
    {
        string fboSource = ReadCSharpFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.FBOs.cs");

        fboSource.ShouldContain("lightCombineMat.SettingUniforms += (_, program) => ApplyLightCombineProgramBindings(program);");
        fboSource.ShouldContain("mat.SettingUniforms += (_, program) => ApplyLightCombineProgramBindings(program);");
    }

    [Test]
    public void DeferredPipeline_LightCombineDrawsPushAmbientAndProbeBindings()
    {
        string fboSource = ReadCSharpFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.FBOs.cs");
        string publisherSource = ReadCSharpFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.BindingPublishers.cs");

        fboSource.ShouldContain("lightCombineMat.BindingPublishers.Add(");
        fboSource.ShouldContain("mat.BindingPublishers.Add(");
        fboSource.ShouldContain("new LightCombineBindingPublisher(this)");
        publisherSource.ShouldContain("owner.ApplyLightCombineNumericBindings(materialProgram)");
        publisherSource.ShouldContain("owner.BindPbrLightingResources(materialProgram, deferredProbeBufferBindings: true)");
        publisherSource.ShouldContain("Vector3 GlobalAmbient");
        publisherSource.ShouldContain("XRDataBuffer? ProbePositionBuffer");
        publisherSource.ShouldContain("XRDataBuffer? ProbeGridIndexBuffer");
    }

    private static string ReadCSharpFile(string relativePath)
    {
        string repoRoot = ResolveRepoRoot();
        string path = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"Expected C# file '{path}' to exist.");
        return File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
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
