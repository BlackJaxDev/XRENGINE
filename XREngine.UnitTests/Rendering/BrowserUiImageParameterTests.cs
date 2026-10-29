using System.Numerics;
using NUnit.Framework;
using XREngine.Editor.Publishing;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class BrowserUiImageParameterTests
{
    private IDisposable _wrappers = null!;
    private IDisposable _target = null!;

    [SetUp]
    public void SetUp()
    {
        _wrappers = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        _target = RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(EngineMaterialConstructionTarget.WebGpuCooked);
    }

    [TearDown]
    public void TearDown()
    {
        _target.Dispose();
        _wrappers.Dispose();
    }

    private static ShaderVar[] DesktopParameters() =>
    [
        new ShaderUInt(0, "PpllMaxNodes"),
        new ShaderInt(0, "DepthPeelLayerIndex"),
        new ShaderFloat(0, "DepthPeelEpsilon"),
        new ShaderBool(false, "DepthPeelReversedDepth"),
        new ShaderVector4(new Vector4(0.75f, 0.5f, 0.875f, 0.625f), "MatColor"),
    ];

    [TestCase(false)]
    [TestCase(true)]
    public void TintOnlyRetainsTheBorrowedParameter(bool canonicalStage)
    {
        ShaderVector4 tint = new(new Vector4(0.75f, 0.5f, 0.875f, 0.625f), "MatColor");
        using XRMaterial material = new([tint]);
        Assert.That(BrowserMaterialCookProjection.ReadUiImageTint(material, canonicalStage), Is.SameAs(tint));
        Assert.That(material.Parameters, Is.EqualTo(new ShaderVar[] { tint }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CanonicalDesktopDefaultsPreserveSourceOrderAndTint(bool reverse)
    {
        ShaderVar[] parameters = DesktopParameters();
        if (reverse) Array.Reverse(parameters);
        ShaderVar[] before = [.. parameters];
        ShaderVector4 tint = parameters.OfType<ShaderVector4>().Single();
        Vector4 value = tint.Value;
        using XRMaterial material = new(parameters);
        ShaderVar[] stored = material.Parameters;
        Assert.That(BrowserMaterialCookProjection.ReadUiImageTint(material, true), Is.SameAs(tint));
        Assert.That(material.Parameters, Is.SameAs(stored));
        Assert.That(material.Parameters, Is.EqualTo(before));
        Assert.That(tint.Value, Is.EqualTo(value));
    }

    [Test]
    public void SourceFreeMaterialCannotDiscardDesktopParameters()
    {
        using XRMaterial material = new(DesktopParameters());
        Assert.Throws<NotSupportedException>(() => BrowserMaterialCookProjection.ReadUiImageTint(material, false));
    }

    [TestCase("ppll-active")]
    [TestCase("layer-active")]
    [TestCase("epsilon-active")]
    [TestCase("reverse-active")]
    [TestCase("epsilon-nan")]
    [TestCase("epsilon-infinity")]
    [TestCase("ppll-type")]
    [TestCase("layer-type")]
    [TestCase("epsilon-type")]
    [TestCase("reverse-type")]
    [TestCase("tint-type")]
    [TestCase("unknown")]
    [TestCase("duplicate")]
    [TestCase("missing")]
    [TestCase("extra")]
    [TestCase("empty")]
    public void UnsupportedParametersFailWithoutChangingTheSource(string change)
    {
        ShaderVar[] parameters = DesktopParameters();
        switch (change)
        {
            case "ppll-active": parameters[0] = new ShaderUInt(1, "PpllMaxNodes"); break;
            case "layer-active": parameters[1] = new ShaderInt(1, "DepthPeelLayerIndex"); break;
            case "epsilon-active": parameters[2] = new ShaderFloat(0.001f, "DepthPeelEpsilon"); break;
            case "reverse-active": parameters[3] = new ShaderBool(true, "DepthPeelReversedDepth"); break;
            case "epsilon-nan": parameters[2] = new ShaderFloat(float.NaN, "DepthPeelEpsilon"); break;
            case "epsilon-infinity": parameters[2] = new ShaderFloat(float.PositiveInfinity, "DepthPeelEpsilon"); break;
            case "ppll-type": parameters[0] = new ShaderInt(0, "PpllMaxNodes"); break;
            case "layer-type": parameters[1] = new ShaderUInt(0, "DepthPeelLayerIndex"); break;
            case "epsilon-type": parameters[2] = new ShaderInt(0, "DepthPeelEpsilon"); break;
            case "reverse-type": parameters[3] = new ShaderInt(0, "DepthPeelReversedDepth"); break;
            case "tint-type": parameters[4] = new ShaderFloat(0, "MatColor"); break;
            case "unknown": parameters[0] = new ShaderUInt(0, "CustomNodes"); break;
            case "duplicate": parameters[0] = new ShaderInt(0, "DepthPeelLayerIndex"); break;
            case "missing": parameters = parameters[1..]; break;
            case "extra": parameters = [.. parameters, new ShaderFloat(0, "Custom")]; break;
            case "empty": parameters = []; break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }
        ShaderVar[] before = [.. parameters];
        using XRMaterial material = new(parameters);
        ShaderVar[] stored = material.Parameters;
        NotSupportedException? error = Assert.Throws<NotSupportedException>(() => BrowserMaterialCookProjection.ReadUiImageTint(material, true));
        Assert.That(error!.Message, Does.StartWith("BrowserCook.UiImageProjectionUnsupported:"));
        Assert.That(material.Parameters, Is.SameAs(stored));
        Assert.That(material.Parameters, Is.EqualTo(before));
    }
}
