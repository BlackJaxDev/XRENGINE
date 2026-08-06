using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;
using XREngine.Rendering.Vulkan.DeviceBootstrap;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanBackendObjectRegistryTests
{
    [Test]
    public void Registries_IsolateBindingSlotsAndPublishedWrappers()
    {
        VulkanBackendObjectRegistry firstRegistry = new();
        VulkanBackendObjectRegistry secondRegistry = new();
        VkObject<XRShader> firstWrapper = CreateUninitializedShaderWrapper();
        VkObject<XRShader> secondWrapper = CreateUninitializedShaderWrapper();

        uint firstBinding = firstRegistry.Cache(firstWrapper);
        uint secondBinding = secondRegistry.Cache(secondWrapper);
        firstRegistry.Publish(firstBinding, firstWrapper);
        secondRegistry.Publish(secondBinding, secondWrapper);

        firstBinding.ShouldBe(1u);
        secondBinding.ShouldBe(1u);
        firstRegistry.Get<XRShader>(firstBinding).ShouldBeSameAs(firstWrapper);
        secondRegistry.Get<XRShader>(secondBinding).ShouldBeSameAs(secondWrapper);
        firstRegistry.Snapshot<XRShader>().ShouldBe([firstWrapper]);
        secondRegistry.Snapshot<XRShader>().ShouldBe([secondWrapper]);

        firstRegistry.Remove<XRShader>(firstBinding);

        firstRegistry.Get<XRShader>(firstBinding).ShouldBeNull();
        firstRegistry.Snapshot<XRShader>().ShouldBeEmpty();
        secondRegistry.Get<XRShader>(secondBinding).ShouldBeSameAs(secondWrapper);
        secondRegistry.Snapshot<XRShader>().ShouldBe([secondWrapper]);
    }

    [Test]
    public void VkObjectGenericBase_HasNoStaticMutableCacheState()
    {
        FieldInfo[] staticMutableFields =
        [
            .. typeof(VkObject<>)
                .GetFields(
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Static |
                    BindingFlags.DeclaredOnly)
                .Where(static field => !field.IsLiteral),
        ];

        staticMutableFields.ShouldBeEmpty(
            "VkObject<T> cache state must remain owned by a VulkanBackendObjectRegistry instance.");
    }

    [Test]
    public void BindingAllocators_IsolateDeviceContextsAndReuseReleasedSlotsLocally()
    {
        VulkanBackendObjectRegistry firstRegistry = new();
        VulkanBackendObjectRegistry secondRegistry = new();
        VkObject<XRShader> firstWrapper = CreateUninitializedShaderWrapper();
        VkObject<XRShader> secondWrapper = CreateUninitializedShaderWrapper();

        uint firstBinding = firstRegistry.Cache(firstWrapper);
        uint secondBinding = secondRegistry.Cache(secondWrapper);
        firstRegistry.Remove<XRShader>(firstBinding);

        uint reusedFirstBinding =
            firstRegistry.Cache(CreateUninitializedShaderWrapper());

        reusedFirstBinding.ShouldBe(firstBinding);
        secondBinding.ShouldBe(1u);
        firstRegistry.BindingAllocator.ActiveCount<XRShader>().ShouldBe(1);
        secondRegistry.BindingAllocator.ActiveCount<XRShader>().ShouldBe(1);
    }

    [Test]
    public void BackendContexts_IsolateDeviceAndPhysicalDeviceIdentity()
    {
        VulkanDeviceContext firstDevice = CreateDeviceContext(11, 101, createdThroughOpenXr: false);
        VulkanDeviceContext secondDevice = CreateDeviceContext(22, 202, createdThroughOpenXr: true);

        VulkanBackendObjectContext firstContext =
            new(null!, firstDevice, new VulkanBackendObjectRegistry(), new(), new(), new());
        VulkanBackendObjectContext secondContext =
            new(null!, secondDevice, new VulkanBackendObjectRegistry(), new(), new(), new());

        firstContext.Device.Handle.ShouldBe((nint)101);
        secondContext.Device.Handle.ShouldBe((nint)202);
        firstContext.PhysicalDevice.Handle.ShouldBe((nint)11);
        secondContext.PhysicalDevice.Handle.ShouldBe((nint)22);
        firstContext.Registry.ShouldNotBeSameAs(secondContext.Registry);
        firstContext.BindingAllocator.ShouldNotBeSameAs(secondContext.BindingAllocator);
    }

    private static VulkanDeviceContext CreateDeviceContext(
        nint physicalDeviceHandle,
        nint deviceHandle,
        bool createdThroughOpenXr)
    {
        QueueFamilyProperties[] queues =
        [
            new QueueFamilyProperties
            {
                QueueFlags = QueueFlags.GraphicsBit |
                    QueueFlags.ComputeBit |
                    QueueFlags.TransferBit,
                QueueCount = 1,
            },
        ];
        VulkanPhysicalDeviceCapabilitySnapshot snapshot = new(
            default,
            default,
            queues,
            VulkanDeviceExtensionSet.Empty);
        QueueFamilyIndices families = new()
        {
            GraphicsFamilyIndex = 0,
            ComputeFamilyIndex = 0,
            TransferFamilyIndex = 0,
            GraphicsFamilySupportsCompute = true,
            GraphicsFamilySupportsTransfer = true,
        };
        VulkanDeviceContext context = new();
        context.AttachInstance(
            new Instance((nint)0x301),
            [],
            Vk.Version13,
            createdThroughOpenXr: false,
            openXrBootstrapContext: null);
        context.AttachPhysicalDevice(
            new PhysicalDevice(physicalDeviceHandle),
            snapshot,
            families);
        context.AttachDevice(
            new Device(deviceHandle),
            createdThroughOpenXr,
            VulkanDeviceExtensionSet.Empty);
        return context;
    }

    private static VkObject<XRShader> CreateUninitializedShaderWrapper()
        => (VkObject<XRShader>)RuntimeHelpers.GetUninitializedObject(
            typeof(VkShader));
}
