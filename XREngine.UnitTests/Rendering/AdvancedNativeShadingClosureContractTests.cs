using System.Runtime.CompilerServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedNativeShadingClosureContractTests
{
    private sealed class MockAOProvider : IAdvancedAmbientOcclusionProvider
    {
        public string ProviderName => "MockGTAO";
        public bool IsSupported { get; set; } = true;
        public bool IsHalfResolution => false;
        public bool SupportsStereo => true;
        public EPixelInternalFormat OutputFormat => EPixelInternalFormat.R8;
        public string? UnsupportedReason => null;
    }

    private sealed class MockGIProvider : IAdvancedGlobalIlluminationProvider
    {
        public EGlobalIlluminationMode ActiveMode => EGlobalIlluminationMode.SurfelGI;
        public string ProviderName => "MockSurfelGI";
        public bool IsSupported { get; set; } = true;
        public bool RequiresTemporalHistory => true;
        public string? OutputResourceName => "SurfelGITexture";
    }

    [Test]
    public void DecalRecord_MatchesByteLayout()
    {
        Unsafe.SizeOf<AdvancedFroxelDecalRecord>().ShouldBe(8);
        AdvancedFroxelDecalRecord record = new(offset: 12, count: 4, flags: 1u);
        record.DecalOffset.ShouldBe((ushort)12);
        record.DecalCount.ShouldBe((ushort)4);
        record.Flags.ShouldBe(1u);
    }

    [Test]
    public void AmbientOcclusion_MultiBounceApproximation_IsNonNegative()
    {
        float ao = 0.5f;
        float albedoR = 0.8f;
        float albedoG = 0.2f;
        float albedoB = 0.1f;

        (float r, float g, float b) = AdvancedAmbientOcclusionContract.EvaluateMultiBounce(ao, albedoR, albedoG, albedoB);
        r.ShouldBeGreaterThanOrEqualTo(0.0f);
        g.ShouldBeGreaterThanOrEqualTo(0.0f);
        b.ShouldBeGreaterThanOrEqualTo(0.0f);

        // AO = 1.0 (no occlusion) should yield approx 1.0
        (float rFull, _, _) = AdvancedAmbientOcclusionContract.EvaluateMultiBounce(1.0f, 0.5f, 0.5f, 0.5f);
        rFull.ShouldBeGreaterThan(0.9f);
    }

    [Test]
    public void AOProvider_ExposesRequiredCapabilities()
    {
        MockAOProvider provider = new();
        provider.ProviderName.ShouldBe("MockGTAO");
        provider.IsSupported.ShouldBeTrue();
        provider.SupportsStereo.ShouldBeTrue();
        provider.OutputFormat.ShouldBe(EPixelInternalFormat.R8);
    }

    [Test]
    public void GIProvider_ResolvesActiveModeAndFallbacks()
    {
        MockGIProvider supportedProvider = new() { IsSupported = true };
        AdvancedGlobalIlluminationContract.ResolveActiveMode(supportedProvider).ShouldBe(EGlobalIlluminationMode.SurfelGI);

        MockGIProvider unsupportedProvider = new() { IsSupported = false };
        AdvancedGlobalIlluminationContract.ResolveActiveMode(unsupportedProvider).ShouldBe(EGlobalIlluminationMode.LightProbesAndIbl);

        AdvancedGlobalIlluminationContract.ResolveActiveMode(null).ShouldBe(EGlobalIlluminationMode.LightProbesAndIbl);
    }

    [Test]
    public void SkyBackgroundContract_AlphaAndSentinelRules()
    {
        AdvancedSkyBackgroundContract.BackgroundAlpha.ShouldBe(0.0f);
        AdvancedSkyBackgroundContract.SceneOpaqueAlpha.ShouldBe(1.0f);

        AdvancedSkyBackgroundContract.IsBackgroundPixel(0u).ShouldBeTrue();
        AdvancedSkyBackgroundContract.IsBackgroundPixel(1u).ShouldBeFalse();
        AdvancedSkyBackgroundContract.IsBackgroundPixel(42u).ShouldBeFalse();
    }

    [Test]
    public void ResourceNames_ProduceConsistentSlotIdentifiers()
    {
        AdvancedClusteredLightingResourceNames.FroxelDecalGrid(0u).ShouldBe("AdvancedClusteredLighting.FroxelDecalGrid.Slot0");
        AdvancedClusteredLightingResourceNames.DecalIndexList(1u).ShouldBe("AdvancedClusteredLighting.DecalIndexList.Slot1");
        AdvancedAmbientOcclusionContract.ResourceName.ShouldBe("AdvancedShading.AmbientOcclusion");
    }
}
