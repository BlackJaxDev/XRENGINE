using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Materials;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class UberEffectsContractTests
{
    private IRuntimeShaderServices? _previousServices;

    [SetUp]
    public void SetUp()
    {
        _previousServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new UberRuntimeShaderServices();
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
            "extended-effects",
            "audiolink",
            "environment-lighting",
            "view-context",
            "vertex-effects",
        ];
        foreach (string feature in expected)
            manifest.FeatureLookup.ShouldContainKey(feature);

        manifest.PropertyLookup["_VertexEffectsEnabled"].FeatureId
            .ShouldBe("vertex-effects");
        manifest.PropertyLookup["_DissolveProgress"].FeatureId
            .ShouldBe("dissolve");
    }

    [Test]
    public void CanonicalShaders_KeepEffectsAndDeformationsPassEquivalent()
    {
        string fragment = ReadShader("UberShader.frag");
        fragment.ShouldContain("uberApplyCoverageEffects(mesh)");
        fragment.ShouldContain("uberApplySurfaceEffects(mesh");
        fragment.ShouldContain("uberApplyPostEffects(mesh");


        foreach (string vertexName in new[] { "UberShader.vert", "UberShader_OVR.vert", "UberShader_NV.vert" })
        {
            string vertex = ReadShader(vertexName);
            vertex.ShouldContain("uberApplyVertexEffects(pos, norm");
            vertex.ShouldContain("uberApplyOutlineLocal(pos, norm");
            vertex.ShouldContain("uberApplyOutlineClip(");
        }
    }

    [Test]
    public void ViewContextScopes_RestoreNestedStateWithoutHeapState()
    {
        using (UberMaterialRuntimeAdapters.PushViewContext(
            MaterialViewFlags.MainCamera,
            new System.Numerics.Vector4(1.0f)))
        {
            UberMaterialRuntimeAdapters.CurrentViewFlags.ShouldBe(MaterialViewFlags.MainCamera);
            using (UberMaterialRuntimeAdapters.PushViewContext(
                MaterialViewFlags.Mirror | MaterialViewFlags.Stereo,
                new System.Numerics.Vector4(0.5f)))
            {
                UberMaterialRuntimeAdapters.CurrentViewFlags
                    .ShouldBe(MaterialViewFlags.Mirror | MaterialViewFlags.Stereo);
            }
            UberMaterialRuntimeAdapters.CurrentViewFlags.ShouldBe(MaterialViewFlags.MainCamera);
        }
        UberMaterialRuntimeAdapters.CurrentViewFlags.ShouldBe(MaterialViewFlags.None);
    }

    private static string ReadShader(string fileName)
    {
        string root = TestContext.CurrentContext.TestDirectory;
        while (!File.Exists(Path.Combine(root, "XRENGINE.slnx")))
            root = Directory.GetParent(root)?.FullName
                ?? throw new DirectoryNotFoundException("Could not locate repository root.");

        return File.ReadAllText(Path.Combine(root, "Build", "CommonAssets", "Shaders", "Uber", fileName)).Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
