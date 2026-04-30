using NUnit.Framework;
using Shouldly;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Shadows;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class ShadowMapResourceFactoryPhase1Tests
{
    [Test]
    public void LightDefaults_PreserveDepthEncodingAndInertMomentSettings()
    {
        SpotLightComponent light = new();

        light.ShadowMapEncoding.ShouldBe(EShadowMapEncoding.Depth);
        light.ShadowMomentMinVariance.ShouldBe(ShadowMapResourceFactory.DefaultMomentMinVariance);
        light.ShadowMomentLightBleedReduction.ShouldBe(ShadowMapResourceFactory.DefaultMomentLightBleedReduction);
        light.ShadowMomentPositiveExponent.ShouldBe(ShadowMapResourceFactory.DefaultEvsmPositiveExponent);
        light.ShadowMomentNegativeExponent.ShouldBe(ShadowMapResourceFactory.DefaultEvsmNegativeExponent);
        light.ShadowMomentBlurRadiusTexels.ShouldBe(0);
        light.ShadowMomentBlurPasses.ShouldBe(0);
        light.ShadowMomentMipBias.ShouldBe(0.0f);
        light.ShadowMomentUseMipmaps.ShouldBeFalse();
    }

    [Test]
    public void LightMomentSettings_ClampToStablePublicRanges()
    {
        SpotLightComponent light = new()
        {
            ShadowMomentMinVariance = -1.0f,
            ShadowMomentLightBleedReduction = 2.0f,
            ShadowMomentPositiveExponent = -5.0f,
            ShadowMomentNegativeExponent = -6.0f,
            ShadowMomentBlurRadiusTexels = 999,
            ShadowMomentBlurPasses = 999,
            ShadowMomentMipBias = float.NaN,
            ShadowMomentUseMipmaps = true,
        };

        light.ShadowMomentMinVariance.ShouldBe(0.0f);
        light.ShadowMomentLightBleedReduction.ShouldBe(0.999f);
        light.ShadowMomentPositiveExponent.ShouldBe(0.0f);
        light.ShadowMomentNegativeExponent.ShouldBe(0.0f);
        light.ShadowMomentBlurRadiusTexels.ShouldBe(64);
        light.ShadowMomentBlurPasses.ShouldBe(8);
        light.ShadowMomentMipBias.ShouldBe(0.0f);
        light.ShadowMomentUseMipmaps.ShouldBeTrue();
    }

    [Test]
    public void SelectFormat_UsesMomentStorageAndClearSentinels()
    {
        ShadowMapFormatSelection selection = ShadowMapResourceFactory.SelectFormat(
            EShadowMapEncoding.ExponentialVariance4,
            positiveExponent: 4.0f,
            negativeExponent: 3.0f);

        selection.WasDemoted.ShouldBeFalse();
        selection.Encoding.ShouldBe(EShadowMapEncoding.ExponentialVariance4);
        selection.Format.StorageFormat.ShouldBe(EShadowMapStorageFormat.RGBA16Float);
        selection.Format.ChannelCount.ShouldBe(4);
        selection.Format.RequiresLinearFiltering.ShouldBeTrue();
        selection.Format.RequiresSignedFloat.ShouldBeTrue();
        selection.ClearSentinel.ChannelCount.ShouldBe(4);
        selection.ClearSentinel[0].ShouldBe(MathF.Exp(4.0f), 0.001f);
        selection.ClearSentinel[1].ShouldBe(MathF.Exp(8.0f), 0.01f);
        selection.ClearSentinel[2].ShouldBe(-MathF.Exp(-3.0f), 0.001f);
        selection.ClearSentinel[3].ShouldBe(MathF.Exp(-6.0f), 0.001f);
    }

    [Test]
    public void SelectFormat_ClampsEvsmExponentsBySelectedFormat()
    {
        ShadowMapFormatSelection halfFloatSelection = ShadowMapResourceFactory.SelectFormat(
            EShadowMapEncoding.ExponentialVariance2,
            positiveExponent: 20.0f,
            negativeExponent: 20.0f);

        halfFloatSelection.Format.StorageFormat.ShouldBe(EShadowMapStorageFormat.RG16Float);
        halfFloatSelection.PositiveExponent.ShouldBe(ShadowMapResourceFactory.HalfFloatEvsmExponentClamp);
        halfFloatSelection.NegativeExponent.ShouldBe(ShadowMapResourceFactory.HalfFloatEvsmExponentClamp);

        ShadowMapFormatSelection floatSelection = ShadowMapResourceFactory.SelectFormat(
            EShadowMapEncoding.ExponentialVariance2,
            positiveExponent: 20.0f,
            negativeExponent: 20.0f,
            preferredStorageFormat: EShadowMapStorageFormat.RG32Float);

        floatSelection.Format.StorageFormat.ShouldBe(EShadowMapStorageFormat.RG32Float);
        floatSelection.PositiveExponent.ShouldBe(ShadowMapResourceFactory.FloatEvsmExponentClamp);
        floatSelection.NegativeExponent.ShouldBe(ShadowMapResourceFactory.FloatEvsmExponentClamp);
    }

    [Test]
    public void SelectFormat_DemotesDeterministicallyWhenCapabilityProbeRejectsMomentFormats()
    {
        FakeShadowMapCapabilities capabilities = new(
            renderTargetFormats: [EShadowMapStorageFormat.R16Float],
            linearFilterFormats: []);

        ShadowMapFormatSelection selection = ShadowMapResourceFactory.SelectFormat(
            EShadowMapEncoding.ExponentialVariance4,
            capabilities);

        selection.WasDemoted.ShouldBeTrue();
        selection.RequestedEncoding.ShouldBe(EShadowMapEncoding.ExponentialVariance4);
        selection.Encoding.ShouldBe(EShadowMapEncoding.Depth);
        selection.Format.StorageFormat.ShouldBe(EShadowMapStorageFormat.R16Float);
    }

    [Test]
    public void SelectFormat_DemotesUnsupportedMomentEncodingDirectlyToDepth()
    {
        FakeShadowMapCapabilities capabilities = new(
            renderTargetFormats: [EShadowMapStorageFormat.RG16Float, EShadowMapStorageFormat.R16Float],
            linearFilterFormats: [EShadowMapStorageFormat.RG16Float]);

        ShadowMapFormatSelection selection = ShadowMapResourceFactory.SelectFormat(
            EShadowMapEncoding.ExponentialVariance4,
            capabilities);

        selection.WasDemoted.ShouldBeTrue();
        selection.Encoding.ShouldBe(EShadowMapEncoding.Depth);
        selection.Format.StorageFormat.ShouldBe(EShadowMapStorageFormat.R16Float);
    }

    [Test]
    public void Create_BuildsPublicResourceWrapperForProjectionLayout()
    {
        ShadowMapResource resource = ShadowMapResourceFactory.Create(new ShadowMapResourceCreateInfo(
            EShadowProjectionLayout.TextureCube,
            EShadowMapEncoding.Variance2,
            128u,
            128u));

        resource.Encoding.ShouldBe(EShadowMapEncoding.Variance2);
        resource.Format.StorageFormat.ShouldBe(EShadowMapStorageFormat.RG16Float);
        resource.Layout.ShouldBe(EShadowProjectionLayout.TextureCube);
        resource.SamplingTexture.ShouldBeOfType<XRTextureCube>();
        resource.RasterDepthTexture.ShouldBeOfType<XRTextureCube>();
        resource.FrameBuffers.Length.ShouldBe(6);
        resource.LayerCount.ShouldBe(6);
    }

    private sealed class FakeShadowMapCapabilities : IShadowMapFormatCapabilities
    {
        private readonly HashSet<EShadowMapStorageFormat> _renderTargetFormats;
        private readonly HashSet<EShadowMapStorageFormat> _linearFilterFormats;

        public FakeShadowMapCapabilities(
            IEnumerable<EShadowMapStorageFormat> renderTargetFormats,
            IEnumerable<EShadowMapStorageFormat> linearFilterFormats)
        {
            _renderTargetFormats = new HashSet<EShadowMapStorageFormat>(renderTargetFormats);
            _linearFilterFormats = new HashSet<EShadowMapStorageFormat>(linearFilterFormats);
        }

        public bool SupportsRenderTarget(EShadowMapStorageFormat format)
            => _renderTargetFormats.Contains(format);

        public bool SupportsLinearFiltering(EShadowMapStorageFormat format)
            => _linearFilterFormats.Contains(format);
    }
}