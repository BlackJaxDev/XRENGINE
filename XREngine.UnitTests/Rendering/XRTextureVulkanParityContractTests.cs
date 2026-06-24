using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class XRTextureVulkanParityContractTests
{
    [Test]
    public void VkTextureBase_SubscribeSymmetryMatchesGenericTextureEvents()
    {
        string glSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/Textures/GLTexture.cs");
        string vkSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTexture.cs");

        string[] eventBindings =
        [
            "AttachToFBORequested",
            "DetachFromFBORequested",
            "PushDataRequested",
            "BindRequested",
            "UnbindRequested",
            "ClearRequested",
            "GenerateMipmapsRequested",
            "PropertyChanged",
            "PropertyChanging",
        ];

        foreach (string eventBinding in eventBindings)
        {
            glSource.ShouldContain($"Data.{eventBinding} +=");
            glSource.ShouldContain($"Data.{eventBinding} -=");
            vkSource.ShouldContain($"Data.{eventBinding} +=");
            vkSource.ShouldContain($"Data.{eventBinding} -=");
        }

        vkSource.ShouldContain("protected virtual void LinkTextureData()");
        vkSource.ShouldContain("protected virtual void UnlinkTextureData()");
        vkSource.ShouldContain("EnsureDescriptorReadyForVulkanUse(\"BindRequested\")");
    }

    [Test]
    public void VkImageBackedTexture_SeparatesGeneratedUploadedLayoutAndDescriptorReadiness()
    {
        string baseSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTexture.cs");
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");

        source.ShouldContain("public override bool IsGenerated");
        source.ShouldContain("return _image.Handle != 0 || _view.Handle != 0 || _sampler.Handle != 0;");
        source.ShouldNotContain("public override bool IsGenerated { get; }");
        source.ShouldContain("public override bool IsDescriptorReady");
        source.ShouldContain("public bool IsLayoutReadyForSampling");
        source.ShouldContain("HasUploadedData = true;");
        source.ShouldContain("MarkDescriptorClean();");
        source.ShouldContain("InvalidateTextureData();");
        baseSource.ShouldContain("if (IsInvalidated)");
        baseSource.ShouldContain("PushData();");
    }

    [Test]
    public void VkImageBackedTexture_SamplerChangesRecreateSamplerAndDirtyDescriptors()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");
        string textureArraySource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/2D/XRTexture2DArray.cs");

        textureArraySource.ShouldContain("public float LodBias");
        textureArraySource.ShouldContain("texture.LodBias = value;");

        source.ShouldContain("lodBias = t.LodBias;");
        source.ShouldContain("ResolveSamplerLodRange()");
        source.ShouldContain("MinLod = minLod");
        source.ShouldContain("MaxLod = maxLod");
        source.ShouldContain("ReadCompareSettingsFromData()");
        source.ShouldContain("CompareEnable = compareEnable");
        source.ShouldContain("CompareOp = compareOp");
        source.ShouldContain("private void RecreateSamplerForPropertyChange()");
        source.ShouldContain("MarkDescriptorDirty();");
        source.ShouldContain("DestroySampler();");
        source.ShouldContain("CreateSamplerInternal();");
    }

    [Test]
    public void VkImageBackedTexture_TracksRectangleResizeAndChildArrayTextureChanges()
    {
        string vkSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");
        string rectangleSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Objects/Textures/Rectangle/XRTextureRectangle.cs");

        rectangleSource.ShouldContain("public event Action? Resized;");
        rectangleSource.ShouldContain("Resized?.Invoke();");
        vkSource.ShouldContain("case XRTextureRectangle rectangle:");
        vkSource.ShouldContain("rectangle.Resized += OnTextureResized;");
        vkSource.ShouldContain("rectangle.Resized -= OnTextureResized;");
        vkSource.ShouldContain("SubscribeChildTextureEvents()");
        vkSource.ShouldContain("texture.PropertyChanged += OnChildTexturePropertyChanged;");
        vkSource.ShouldContain("tex2D.Resized += OnChildTextureResized;");
        vkSource.ShouldContain("texCube.Resized += OnChildTextureResized;");
    }

    [Test]
    public void VkTextureBuffer_UploadsSourceBufferBeforeCreatingTexelBufferView()
    {
        string baseSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTexture.cs");
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTextureBuffer.cs");

        source.ShouldContain("public override bool IsGenerated => _view.Handle != 0;");
        source.ShouldContain("protected override void LinkTextureData()");
        source.ShouldContain("SubscribeSourceBufferEvents(Data.DataBuffer);");
        baseSource.ShouldContain("Data.PropertyChanging");
        source.ShouldContain("vkDataBuffer.PushData();");
        source.ShouldContain("public override void PushData()");
        source.ShouldContain("public override void Bind()");
        source.ShouldContain("MarkDescriptorClean();");
    }

    [Test]
    public void VkTexturePushData_ExposesOpenGlCompatiblePreAndPostCallbacks()
    {
        string baseSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTexture.cs");
        string imageSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");
        string bufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTextureBuffer.cs");
        string viewSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTextureView.cs");

        baseSource.ShouldContain("public class VulkanPrePushDataCallback");
        baseSource.ShouldContain("public bool ShouldPush { get; set; } = true;");
        baseSource.ShouldContain("public bool AllowPostPushCallback { get; set; } = true;");
        baseSource.ShouldContain("public XREvent<VulkanPrePushDataCallback>? PrePushData;");
        baseSource.ShouldContain("public XREvent<VkTexture<T>>? PostPushData;");
        baseSource.ShouldContain("protected virtual void OnPrePushData(out bool shouldPush, out bool allowPostPushCallback)");
        baseSource.ShouldContain("protected virtual void OnPostPushData()");
        baseSource.ShouldContain("protected bool TryBeginPushData(out bool allowPostPushCallback)");
        baseSource.ShouldContain("protected void CompletePushData(bool allowPostPushCallback)");

        foreach (string concreteSource in new[] { imageSource, bufferSource, viewSource })
        {
            concreteSource.ShouldContain("TryBeginPushData(out bool allowPostPushCallback)");
            concreteSource.ShouldContain("CompletePushData(allowPostPushCallback);");
        }
    }

    [Test]
    public void VkTextureView_InheritsTextureBaseAndTracksViewedTextureChanges()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTextureView.cs");

        source.ShouldContain(": VkTexture<XRTextureViewBase>(api, data)");
        source.ShouldContain("protected override void LinkTextureData()");
        source.ShouldContain("SubscribeViewedTextureEvents(Data.GetViewedTexture());");
        source.ShouldContain("texture.PropertyChanged += OnViewedTexturePropertyChanged;");
        source.ShouldContain("rectangle.Resized += OnViewedTextureResized;");
        source.ShouldContain("public override void PushData()");
        source.ShouldContain("viewedTexture?.PushData();");
        source.ShouldContain("public override void Bind()");
        source.ShouldContain("MarkDescriptorClean();");
    }

    [Test]
    public void VulkanTexturesExposeNativeReadinessUsageResidencyAndViewDiagnostics()
    {
        string baseSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTexture.cs");
        string imageSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");
        string texture2DSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTexture2D.cs");
        string viewSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTextureView.cs");
        string bufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTextureBuffer.cs");

        baseSource.ShouldContain("DescriptorGeneration");
        baseSource.ShouldContain("EnsureDescriptorReadyForVulkanUse");
        baseSource.ShouldContain("Debug.VulkanWarningEvery");

        imageSource.ShouldContain("protected virtual ImageUsageFlags DefaultUsage => ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit | ImageUsageFlags.ColorAttachmentBit;");
        imageSource.ShouldContain("Data.RequiresStorageUsage");
        imageSource.ShouldContain("ImageUsageFlags.StorageBit");
        imageSource.ShouldContain("RuntimeEngine.Rendering.Stats.Vram.AddTextureAllocation");
        imageSource.ShouldContain("RuntimeEngine.Rendering.Stats.Vram.RemoveTextureAllocation");
        imageSource.ShouldContain("Api!.GetPhysicalDeviceFormatProperties(PhysicalDevice, ResolvedFormat, out FormatProperties props);");
        imageSource.ShouldContain("FormatFeatureFlags.SampledImageFilterLinearBit");
        imageSource.ShouldContain("protected void GenerateMipmapsWithBlit()");
        imageSource.ShouldContain("QueueFamilyIndices queueFamilies = Renderer.FamilyQueueIndices;");
        imageSource.ShouldContain("SrcQueueFamilyIndex = graphicsFamily");
        imageSource.ShouldContain("DstQueueFamilyIndex = transferFamily");
        imageSource.ShouldContain("IVkImageDescriptorSource.GetDepthOnlyDescriptorView()");
        imageSource.ShouldContain("IVkImageDescriptorSource.GetStencilOnlyDescriptorView()");

        texture2DSource.ShouldContain("RuntimeManagedProgressiveUploadActive");
        texture2DSource.ShouldContain("SparseTextureStreamingTransitionRequested");
        texture2DSource.ShouldContain("UsedSparseResidency: false");
        texture2DSource.ShouldContain("dense resident mip upload");

        viewSource.ShouldContain("NormalizeAspectMaskForFormat");
        viewSource.ShouldContain("ImageAspectFlags.DepthBit");
        viewSource.ShouldContain("ImageAspectFlags.StencilBit");
        viewSource.ShouldContain("DescriptorMipLevels");

        bufferSource.ShouldContain("IVkTexelBufferDescriptorSource");
        bufferSource.ShouldContain("DescriptorBufferView");
        bufferSource.ShouldContain("DescriptorBufferFormat");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string path = Path.Combine(ResolveWorkspaceRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"Expected file does not exist: {path}");
        return File.ReadAllText(path);
    }

    private static string ResolveWorkspaceRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "XRENGINE.slnx")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException($"Could not resolve workspace root from test base directory '{AppContext.BaseDirectory}'.");
    }
}
