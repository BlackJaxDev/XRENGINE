using NUnit.Framework;
using System.Numerics;
using Silk.NET.Vulkan;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
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
    public void PreparedBorderAuthority_TracksBothNativeAllocations()
    {
        VulkanRecordedBufferIdentity color = new(
            EVulkanRecordedBufferBindingKind.Descriptor, 3, 11, 12, 0, 16);
        VulkanRecordedBufferIdentity width = new(
            EVulkanRecordedBufferBindingKind.Descriptor, 4, 21, 22, 0, 16);
        VulkanPreparedCommandChainKey first = new(1, 2, 0, false,
            default, default, default, true)
        {
            StaticUiBorderColorBuffer = color,
            StaticUiBorderWidthBuffer = width,
        };
        Assert.That(first.Matches(first), Is.True);
        Assert.That(first.Matches(first with
            { StaticUiBorderColorBuffer = color with { AllocationGeneration = 13 } }), Is.False);
        Assert.That(first.Matches(first with
            { StaticUiBorderWidthBuffer = width with { BufferHandle = 23 } }), Is.False);
    }

    [Test]
    public void PreparedImageAuthority_TracksUvAndNativeImagePayload()
    {
        VulkanPreparedCommandChainKey first = new(1, 2, 0, false,
            default, default, default, true)
        {
            StaticUiImageUvBuffer = new(EVulkanRecordedBufferBindingKind.Descriptor,
                3, 11, 12, 0, 16),
            StaticUiImageResource = new(ObjectType.Image, 21, 22,
                ImageLayout.ShaderReadOnlyOptimal),
            StaticUiImagePayload = new(DescriptorType.CombinedImageSampler,
                31, 32, 41, 42, 0, 0, ImageLayout.ShaderReadOnlyOptimal),
        };
        Assert.That(first.Matches(first), Is.True);
        Assert.That(first.Matches(first with
            { StaticUiImageUvBuffer = first.StaticUiImageUvBuffer with
                { AllocationGeneration = 13 } }), Is.False);
        Assert.That(first.Matches(first with
            { StaticUiImageResource = first.StaticUiImageResource with
                { Generation = 23 } }), Is.False);
        Assert.That(first.Matches(first with
            { StaticUiImagePayload = first.StaticUiImagePayload with
                { SecondaryGeneration = 43 } }), Is.False);
    }

    [Test]
    public void ReusedImageSecondary_RejectsChangedCurrentNativeAuthority()
    {
        VulkanPreparedCommandChainKey recorded = new(1, 2, 0, false,
            default, default, default, true)
        {
            StaticUiContract = default(UIStaticDrawContract) with { IsImage = true },
            StaticUiImageUvBuffer = new(EVulkanRecordedBufferBindingKind.Descriptor,
                3, 11, 12, 0, 16),
            StaticUiImageResource = new(ObjectType.Image, 21, 22,
                ImageLayout.ShaderReadOnlyOptimal),
            StaticUiImagePayload = new(DescriptorType.CombinedImageSampler,
                31, 32, 41, 42, 0, 0, ImageLayout.ShaderReadOnlyOptimal),
            StaticUiImageSet = 1,
            StaticUiImageBinding = 2,
        };
        bool Current(VulkanRecordedBufferIdentity uv,
            VulkanRecordedDescriptorResourceIdentity image,
            VulkanDescriptorPayload payload, uint set = 1, uint binding = 2)
        {
            VkImageDescriptorSnapshot snapshot = recorded.StaticUiImageSnapshot;
            return VulkanCommandRuntime.MatchesStaticUiImageReuseAuthority(
                in recorded, in uv, in snapshot, in image, in payload, set, binding);
        }

        Assert.That(Current(recorded.StaticUiImageUvBuffer,
            recorded.StaticUiImageResource, recorded.StaticUiImagePayload), Is.True);
        Assert.That(Current(recorded.StaticUiImageUvBuffer with
                { AllocationGeneration = 13 },
            recorded.StaticUiImageResource, recorded.StaticUiImagePayload), Is.False);
        Assert.That(Current(recorded.StaticUiImageUvBuffer,
            recorded.StaticUiImageResource with { Generation = 23 },
            recorded.StaticUiImagePayload), Is.False);
        Assert.That(Current(recorded.StaticUiImageUvBuffer,
            recorded.StaticUiImageResource with
                { Layout = ImageLayout.TransferDstOptimal },
            recorded.StaticUiImagePayload), Is.False);
        Assert.That(Current(recorded.StaticUiImageUvBuffer,
            recorded.StaticUiImageResource,
            recorded.StaticUiImagePayload with { PrimaryHandle = 33 }), Is.False);
        Assert.That(Current(recorded.StaticUiImageUvBuffer,
            recorded.StaticUiImageResource,
            recorded.StaticUiImagePayload with { SecondaryGeneration = 43 }), Is.False);
        VkImageDescriptorSnapshot replacedSnapshot =
            recorded.StaticUiImageSnapshot with { Generation = 44 };
        VulkanRecordedBufferIdentity currentUv = recorded.StaticUiImageUvBuffer;
        VulkanRecordedDescriptorResourceIdentity currentImage =
            recorded.StaticUiImageResource;
        VulkanDescriptorPayload currentPayload = recorded.StaticUiImagePayload;
        Assert.That(VulkanCommandRuntime.MatchesStaticUiImageReuseAuthority(
            in recorded, in currentUv, in replacedSnapshot,
            in currentImage, in currentPayload, 1, 2), Is.False);
        Assert.That(Current(recorded.StaticUiImageUvBuffer,
            recorded.StaticUiImageResource, recorded.StaticUiImagePayload,
            set: 3), Is.False);
        Assert.That(Current(recorded.StaticUiImageUvBuffer,
            recorded.StaticUiImageResource, recorded.StaticUiImagePayload,
            binding: 4), Is.False);
    }

    [Test]
    public void BindfulImageProgram_RejectsExtraImagesAndBindlessBinding()
    {
        DescriptorBindingInfo image = new(1, 2,
            DescriptorType.CombinedImageSampler, ShaderStageFlags.FragmentBit,
            1, "Texture0");
        static bool Admits(DescriptorBindingInfo[] bindings)
            => VulkanCommandRuntime.TryFindBindfulStaticUiImageBinding(
                bindings, out _, out _);

        Assert.That(Admits([image]), Is.True);
        Assert.That(Admits([image, new(1, 3,
            DescriptorType.StorageBuffer, ShaderStageFlags.FragmentBit,
            1, "QuadUvBuffer")]), Is.True);
        Assert.That(Admits([image, new(1, 4,
            DescriptorType.CombinedImageSampler, ShaderStageFlags.FragmentBit,
            1, "OtherImage")]), Is.False);
        Assert.That(Admits([image, new(1, 4,
            DescriptorType.Sampler, ShaderStageFlags.FragmentBit,
            1, "OtherSampler")]), Is.False);
        Assert.That(Admits([image with { Count = 2 }]), Is.False);
        Assert.That(Admits([image, new(
            VulkanBindlessMaterialDescriptors.TextureArraySet,
            VulkanBindlessMaterialDescriptors.TextureArrayBinding,
            DescriptorType.CombinedImageSampler,
            ShaderStageFlags.FragmentBit, 1,
            VulkanBindlessMaterialDescriptors.TextureArrayBindingName)]), Is.False);
    }

    [Test]
    public void PreparedTextAuthority_TracksDrawOrderBufferAndAtlasPayload()
    {
        VulkanStaticUiTextBufferAuthority draw = new(
            new(EVulkanRecordedBufferBindingKind.Descriptor, 4, 11, 12, 0, 16),
            new(DescriptorType.StorageBuffer, 11, 12, 0, 0, 0, 16,
                ImageLayout.Undefined), 1, 4);
        VulkanStaticUiTextNativeAuthority text = new(draw, draw, draw,
            draw, draw, default(VkImageDescriptorSnapshot) with { IsReady = true },
            new(ObjectType.Image, 21, 22,
                ImageLayout.ShaderReadOnlyOptimal),
            new(DescriptorType.CombinedImageSampler, 31, 32,
                41, 42, 0, 0, ImageLayout.ShaderReadOnlyOptimal), 1, 5);
        VulkanPreparedCommandChainKey first = new(1, 2, 0, false,
            default, default, default, true)
        {
            StaticUiContract = default(UIStaticDrawContract) with { IsText = true },
            StaticUiText = text,
        };
        Assert.That(first.Matches(first), Is.True);
        Assert.That(VulkanCommandRuntime.MatchesStaticUiTextReuseAuthority(
            in first, in text), Is.True);
        VulkanStaticUiTextBufferAuthority moved = draw with
        {
            Buffer = draw.Buffer with { AllocationGeneration = 13 },
        };
        Assert.That(first.Matches(first with { StaticUiText = text with
            { GlyphTransforms = moved } }), Is.False);
        Assert.That(first.Matches(first with { StaticUiText = text with
            { GlyphTexCoords = moved } }), Is.False);
        Assert.That(first.Matches(first with { StaticUiText = text with
            { TextInstances = moved } }), Is.False);
        Assert.That(first.Matches(first with { StaticUiText = text with
            { GlyphIndices = moved } }), Is.False);
        Assert.That(first.Matches(first with { StaticUiText = text with
            { DrawIndices = moved } }), Is.False);
        Assert.That(first.Matches(first with { StaticUiText = text with
            { DrawIndices = draw with { Payload = draw.Payload with
                { Range = 8 } } } }), Is.False);
        Assert.That(first.Matches(first with { StaticUiText = text with
            { AtlasPayload = text.AtlasPayload with
                { SecondaryGeneration = 43 } } }), Is.False);
        Assert.That(first.Matches(first with { StaticUiText = text with
            { AtlasSet = 2 } }), Is.False);
        VulkanStaticUiTextNativeAuthority replacedAtlas = text with
        {
            AtlasSnapshot = text.AtlasSnapshot with { Generation = 2 },
        };
        Assert.That(VulkanCommandRuntime.MatchesStaticUiTextReuseAuthority(
            in first, in replacedAtlas), Is.False);
        VulkanStaticUiTextNativeAuthority replacedDraw = text with
        {
            DrawIndices = moved,
        };
        Assert.That(VulkanCommandRuntime.MatchesStaticUiTextReuseAuthority(
            in first, in replacedDraw), Is.False);
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
