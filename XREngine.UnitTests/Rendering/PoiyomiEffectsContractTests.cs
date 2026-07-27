using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Poiyomi;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class PoiyomiEffectsContractTests
{
    private IRuntimeShaderServices? _previousServices;

    [SetUp]
    public void SetUp()
    {
        _previousServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new PoiyomiRuntimeShaderServices();
    }

    [TearDown]
    public void TearDown()
        => RuntimeShaderServices.Current = _previousServices;

    [Test]
    public void Manifest_DeclaresEffectsFeatureFamilies()
    {
        ShaderUiManifest manifest = ShaderHelper.UberFragForward().GetUiManifest();

        string[] expected =
        [
            "poiyomi-special-effects",
            "poiyomi-audiolink",
            "poiyomi-environment-adapters",
            "poiyomi-view-context",
            "poiyomi-vertex-effects",
        ];
        foreach (string feature in expected)
            manifest.FeatureLookup.ShouldContainKey(feature);

        manifest.PropertyLookup["_PoiVertexEffectsEnabled"].FeatureId
            .ShouldBe("poiyomi-vertex-effects");
        manifest.PropertyLookup["_DissolveProgress"].FeatureId
            .ShouldBe("dissolve");
    }

    [Test]
    public void CanonicalShaders_KeepEffectsAndDeformationsPassEquivalent()
    {
        string fragment = ReadShader("UberShader.frag");
        fragment.ShouldContain("poiApplyCoverageEffects(mesh)");
        fragment.ShouldContain("poiApplySurfaceEffects(mesh");
        fragment.ShouldContain("poiApplyPostEffects(mesh");


        foreach (string vertexName in new[] { "UberShader.vert", "UberShader_OVR.vert", "UberShader_NV.vert" })
        {
            string vertex = ReadShader(vertexName);
            vertex.ShouldContain("poiApplyVertexEffects(pos, norm");
            vertex.ShouldContain("poiApplyOutlineLocal(pos, norm");
            vertex.ShouldContain("poiApplyOutlineClip(");
        }
    }

    [Test]
    public void ViewContextScopes_RestoreNestedStateWithoutHeapState()
    {
        using (PoiyomiRuntimeAdapters.PushViewContext(
            PoiyomiViewFlags.MainCamera,
            new System.Numerics.Vector4(1.0f)))
        {
            PoiyomiRuntimeAdapters.CurrentViewFlags.ShouldBe(PoiyomiViewFlags.MainCamera);
            using (PoiyomiRuntimeAdapters.PushViewContext(
                PoiyomiViewFlags.Mirror | PoiyomiViewFlags.Stereo,
                new System.Numerics.Vector4(0.5f)))
            {
                PoiyomiRuntimeAdapters.CurrentViewFlags
                    .ShouldBe(PoiyomiViewFlags.Mirror | PoiyomiViewFlags.Stereo);
            }
            PoiyomiRuntimeAdapters.CurrentViewFlags.ShouldBe(PoiyomiViewFlags.MainCamera);
        }
        PoiyomiRuntimeAdapters.CurrentViewFlags.ShouldBe(PoiyomiViewFlags.None);
    }

    private static string ReadShader(string fileName)
    {
        string root = TestContext.CurrentContext.TestDirectory;
        while (!File.Exists(Path.Combine(root, "XRENGINE.slnx")))
            root = Directory.GetParent(root)?.FullName
                ?? throw new DirectoryNotFoundException("Could not locate repository root.");

        return File.ReadAllText(Path.Combine(root, "Build", "CommonAssets", "Shaders", "Uber", fileName));
    }
}
