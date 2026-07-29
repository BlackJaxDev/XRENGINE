using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanRuntimeManagerOwnershipTests
{
    [Test]
    public void Renderer_HasOneDescriptorAndPipelineManagerPerInstance()
    {
        FieldInfo descriptorManager = typeof(VulkanRenderer)
            .GetField("_descriptorManager", BindingFlags.Instance | BindingFlags.NonPublic)
            .ShouldNotBeNull();
        FieldInfo pipelineManager = typeof(VulkanRenderer)
            .GetField("_pipelineManager", BindingFlags.Instance | BindingFlags.NonPublic)
            .ShouldNotBeNull();

        descriptorManager.FieldType.ShouldBe(typeof(VulkanDescriptorManager));
        pipelineManager.FieldType.ShouldBe(typeof(VulkanPipelineManager));
        descriptorManager.IsInitOnly.ShouldBeTrue();
        pipelineManager.IsInitOnly.ShouldBeTrue();
    }

    [Test]
    public void Renderer_HasOneImGuiResourceAndTextureRegistryOwnerPerInstance()
    {
        FieldInfo resources = typeof(VulkanRenderer)
            .GetField("_imguiResources", BindingFlags.Instance | BindingFlags.NonPublic)
            .ShouldNotBeNull();
        FieldInfo textureRegistry = typeof(VulkanRenderer)
            .GetField("_imguiTextureRegistry", BindingFlags.Instance | BindingFlags.NonPublic)
            .ShouldNotBeNull();
        FieldInfo drawData = typeof(VulkanRenderer)
            .GetField("_imguiDrawData", BindingFlags.Instance | BindingFlags.NonPublic)
            .ShouldNotBeNull();

        resources.FieldType.ShouldBe(typeof(VulkanImGuiResources));
        textureRegistry.FieldType.ShouldBe(typeof(VulkanImGuiTextureRegistry));
        drawData.FieldType.ShouldBe(typeof(VulkanImGuiDrawDataCache));
        resources.IsInitOnly.ShouldBeTrue();
        textureRegistry.IsInitOnly.ShouldBeTrue();
        drawData.IsInitOnly.ShouldBeTrue();
    }

    [Test]
    public void PipelineManager_DeduplicatesPendingProgramLinksPerRenderer()
    {
        VulkanPipelineManager manager = new();
        VulkanRenderer.VkRenderProgram program =
            (VulkanRenderer.VkRenderProgram)RuntimeHelpers.GetUninitializedObject(
                typeof(VulkanRenderer.VkRenderProgram));

        manager.QueueProgramLinkUntilDeviceReady(program);
        manager.QueueProgramLinkUntilDeviceReady(program);

        manager.FlushPendingDeviceReadyProgramLinks().ShouldBe(1);
        manager.FlushPendingDeviceReadyProgramLinks().ShouldBe(0);
    }

    [Test]
    public void VkRenderProgram_DoesNotOwnRendererGlobalProgramCollections()
    {
        FieldInfo[] rendererGlobalCollections =
        [
            .. typeof(VulkanRenderer.VkRenderProgram)
                .GetFields(
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Static |
                    BindingFlags.DeclaredOnly)
                .Where(static field =>
                    field.FieldType.IsGenericType &&
                    field.FieldType.GetGenericArguments()
                        .Contains(typeof(VulkanRenderer.VkRenderProgram))),
        ];

        rendererGlobalCollections.ShouldBeEmpty(
            "Renderer-global program queues belong to VulkanPipelineManager.");
    }
}
