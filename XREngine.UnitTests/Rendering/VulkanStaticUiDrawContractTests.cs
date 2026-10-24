using NUnit.Framework;
using Silk.NET.Vulkan;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanStaticUiDrawContractTests
{
    private XRMesh _mesh = null!;
    private XRMeshRenderer _renderer = null!;

    [SetUp]
    public void SetUp()
    {
        _mesh = new XRMesh();
        _renderer = new XRMeshRenderer();
    }

    [TearDown]
    public void TearDown()
    {
        _renderer.Destroy(now: true);
        _mesh.Destroy(now: true);
    }

    private static bool Matches(UIIndexedDrawIntent intent,
        VulkanPreparedMeshDrawState prepared)
        => VulkanCommandRuntime.MatchesPreparedStaticUiPrimitive(
            in intent, in prepared);

    [Test]
    public void PreparedAuthority_RejectsChangedSegmentOrRun()
    {
        UIIndexedDrawIntent intent = new(_renderer, _mesh.GeometryRevision,
            EPrimitiveType.Triangles, IndexSize.TwoBytes, 6, 1, 0);
        UIStaticDrawContract contract = new(1,
            new UICompositionOwner(new UIElementIdentity(2, 1),
                EUICompositionLayerKind.StaticChrome),
            3, 4, 5, 6, intent)
        {
            CapturedMesh = _mesh,
        };
        VulkanPreparedCommandChainKey first = new(1, 2, 0, false,
            default, default, default, true)
        {
            StaticUiContract = contract,
        };

        Assert.That(first.Matches(first with
            { StaticUiContract = contract with { SegmentRevision = 7 } }),
            Is.False);
        Assert.That(first.Matches(first with
            { StaticUiContract = contract with { SegmentIdentity = 8 } }),
            Is.False);
        Assert.That(first.Matches(first with
            { StaticUiContract = contract with { RunIdentity = 9 } }),
            Is.False);
        Assert.That(first.Matches(first with
            { StaticUiContract = contract }), Is.True);
    }

    [Test]
    public void PreparedPrimitive_RequiresExactIndexedTriangleDraw()
    {
        UIIndexedDrawIntent intent = new(_renderer, 1,
            EPrimitiveType.Triangles, IndexSize.TwoBytes, 6, 1, 0);
        VulkanPreparedMeshPrimitive primitive = new(default,
            PrimitiveTopology.TriangleList,
            new Silk.NET.Vulkan.Buffer { Handle = 7 },
            IndexType.Uint16, 6, true);
        VulkanPreparedMeshDrawState prepared = default(VulkanPreparedMeshDrawState) with
        {
            Primitive0 = primitive,
            PrimitiveCount = 1,
            InstanceCount = 1,
        };

        Assert.That(Matches(intent, prepared), Is.True);
        Assert.That(Matches(intent, prepared with { PrimitiveCount = 2 }), Is.False);
        Assert.That(Matches(intent, prepared with { InstanceCount = 2 }), Is.False);
        Assert.That(Matches(intent, prepared with
            { Primitive0 = primitive with { ElementCount = 3 } }), Is.False);
        Assert.That(Matches(intent, prepared with
            { Primitive0 = primitive with { IndexType = IndexType.Uint32 } }), Is.False);
    }

    [TestCase(ERenderPassSecondaryCachePolicy.Stable, true)]
    [TestCase(ERenderPassSecondaryCachePolicy.DynamicUi, true)]
    [TestCase(ERenderPassSecondaryCachePolicy.DynamicText, true)]
    [TestCase(ERenderPassSecondaryCachePolicy.DynamicUi |
        ERenderPassSecondaryCachePolicy.DynamicText, true)]
    [TestCase(ERenderPassSecondaryCachePolicy.DynamicDebug, false)]
    [TestCase(ERenderPassSecondaryCachePolicy.DynamicResource, false)]
    [TestCase(ERenderPassSecondaryCachePolicy.OutputSensitive, false)]
    [TestCase(ERenderPassSecondaryCachePolicy.DynamicUi |
        ERenderPassSecondaryCachePolicy.DynamicResource, false)]
    [TestCase(ERenderPassSecondaryCachePolicy.DynamicUi |
        ERenderPassSecondaryCachePolicy.OutputSensitive, false)]
    [TestCase(ERenderPassSecondaryCachePolicy.DynamicText |
        ERenderPassSecondaryCachePolicy.OutputSensitive, false)]
    [TestCase(ERenderPassSecondaryCachePolicy.DynamicText |
        ERenderPassSecondaryCachePolicy.DynamicResource, false)]
    public void StaticUiAdmission_OnlyOverridesUiPolicyBits(
        ERenderPassSecondaryCachePolicy policy, bool expected)
        => Assert.That(VulkanCommandRuntime.AllowsStaticUiSecondaryCachePolicy(
            policy), Is.EqualTo(expected));

    [TestCase(EDefaultRenderPass.OpaqueForward, true)]
    [TestCase(EDefaultRenderPass.MaskedForward, true)]
    [TestCase(EDefaultRenderPass.TransparentForward, true)]
    [TestCase(EDefaultRenderPass.OnTopForward, true)]
    [TestCase(EDefaultRenderPass.Background, false)]
    [TestCase(EDefaultRenderPass.PreRender, false)]
    [TestCase(EDefaultRenderPass.PostRender, false)]
    public void StaticUiAdmission_OnlyUsesUiRasterPasses(
        EDefaultRenderPass pass, bool expected)
        => Assert.That(VulkanCommandRuntime.IsSupportedStaticUiRasterPass(
            (int)pass), Is.EqualTo(expected));
}
