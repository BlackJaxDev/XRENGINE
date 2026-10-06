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
        FieldInfo resourceRuntime = typeof(VulkanRenderer)
            .GetField("_resourceRuntime", BindingFlags.Instance | BindingFlags.NonPublic)
            .ShouldNotBeNull();
        PropertyInfo descriptorManager = typeof(VulkanResourceRuntime)
            .GetProperty("Descriptors", BindingFlags.Instance | BindingFlags.NonPublic)
            .ShouldNotBeNull();
        PropertyInfo pipelineManager = typeof(VulkanResourceRuntime)
            .GetProperty("PipelineManager", BindingFlags.Instance | BindingFlags.NonPublic)
            .ShouldNotBeNull();

        resourceRuntime.FieldType.ShouldBe(typeof(VulkanResourceRuntime));
        resourceRuntime.IsInitOnly.ShouldBeTrue();
        descriptorManager.PropertyType.ShouldBe(typeof(VulkanDescriptorManager));
        pipelineManager.PropertyType.ShouldBe(typeof(VulkanPipelineManager));
        descriptorManager.GetSetMethod(nonPublic: true).ShouldBeNull();
        pipelineManager.GetSetMethod(nonPublic: true).ShouldBeNull();
    }

    [Test]
    public void Renderer_HasOneImGuiResourceAndTextureRegistryOwnerPerInstance()
    {
        FieldInfo outputRuntime = typeof(VulkanRenderer)
            .GetField("_outputRuntime", BindingFlags.Instance | BindingFlags.NonPublic)
            .ShouldNotBeNull();
        FieldInfo resources = typeof(VulkanOutputRuntime)
            .GetField("_imguiResources", BindingFlags.Instance | BindingFlags.NonPublic)
            .ShouldNotBeNull();
        FieldInfo textureRegistry = typeof(VulkanOutputRuntime)
            .GetField("_imguiTextureRegistry", BindingFlags.Instance | BindingFlags.NonPublic)
            .ShouldNotBeNull();
        FieldInfo drawData = typeof(VulkanOutputRuntime)
            .GetField("_imguiDrawData", BindingFlags.Instance | BindingFlags.NonPublic)
            .ShouldNotBeNull();

        outputRuntime.FieldType.ShouldBe(typeof(VulkanOutputRuntime));
        outputRuntime.IsInitOnly.ShouldBeTrue();
        resources.FieldType.ShouldBe(typeof(VulkanImGuiResources));
        textureRegistry.FieldType.ShouldBe(typeof(VulkanImGuiTextureRegistry));
        drawData.FieldType.ShouldBe(typeof(VulkanImGuiDrawDataCache));
    }

    [Test]
    public void PipelineManager_DeduplicatesPendingProgramLinksPerRenderer()
    {
        VulkanPipelineManager manager = new();
        VkRenderProgram program =
            (VkRenderProgram)RuntimeHelpers.GetUninitializedObject(
                typeof(VkRenderProgram));

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
            .. typeof(VkRenderProgram)
                .GetFields(
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Static |
                    BindingFlags.DeclaredOnly)
                .Where(static field =>
                    field.FieldType.IsGenericType &&
                    field.FieldType.GetGenericArguments()
                        .Contains(typeof(VkRenderProgram))),
        ];

        rendererGlobalCollections.ShouldBeEmpty(
            "Renderer-global program queues belong to VulkanPipelineManager.");
    }
}
