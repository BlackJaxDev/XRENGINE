using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Rendering.Vulkan;
using XREngine.Rendering.Vulkan.DeviceBootstrap;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanDeviceContextCapabilityTests
{
    [Test]
    public void PhysicalSelection_PublishesOneImmutableAuthority()
    {
        VulkanDeviceContext context = CreateContext(["VK_EXT_transform_feedback"]);

        context.PhysicalDevice.Handle.ShouldBe((nint)0x101);
        context.PhysicalDeviceCapabilities.ShouldNotBeNull();
        context.AvailableDeviceExtensions.ShouldContain("VK_EXT_transform_feedback");
        context.QueueFamilies.GraphicsFamilyIndex.ShouldBe(0U);
        context.NonCoherentAtomSize.ShouldBe(256UL);
        context.MinUniformBufferOffsetAlignment.ShouldBe(64UL);

        Should.Throw<InvalidOperationException>(() => context.AttachPhysicalDevice(
            new PhysicalDevice((nint)0x102),
            CreateSnapshot([]),
            CreateQueueFamilies()));
    }

    [Test]
    public void NativeLifetime_RequiresInstanceBeforePhysicalDeviceAndPublishesItOnce()
    {
        VulkanDeviceContext context = new();

        Should.Throw<InvalidOperationException>(() => context.AttachPhysicalDevice(
            new PhysicalDevice((nint)0x101),
            CreateSnapshot([]),
            CreateQueueFamilies()));

        context.AttachInstance(
            new Instance((nint)0x51),
            ["VK_EXT_debug_utils"],
            Vk.Version13,
            createdThroughOpenXr: false,
            openXrBootstrapContext: null);

        context.HasInstance.ShouldBeTrue();
        context.EnabledInstanceExtensions.ShouldContain("VK_EXT_debug_utils");
        context.InstanceApiVersion.ShouldBe(Vk.Version13);
        Should.Throw<InvalidOperationException>(() => context.AttachInstance(
            new Instance((nint)0x52),
            [],
            Vk.Version13,
            createdThroughOpenXr: false,
            openXrBootstrapContext: null));
    }

    [Test]
    public void RequiredDeviceExtensions_AreContextOwnedAdmissionPolicy()
    {
        VulkanDeviceContextConfiguration configuration = new(
            requirePresentQueue: false,
            requireSwapchainOutput: false,
            requiredDeviceExtensions: ["VK_KHR_required"]);
        VulkanDeviceContext context = CreateContext(
            ["VK_KHR_required", "VK_EXT_optional"],
            configuration);

        context.SupportsRequiredDeviceExtensions(context.AvailableDeviceExtensions).ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() =>
            context.ValidateEnabledDeviceExtensions(["VK_EXT_optional"]));
        context.ValidateEnabledDeviceExtensions(["VK_KHR_required"])
            .ShouldContain("VK_KHR_required");
    }

    [Test]
    public void DeviceFaultFacility_PublishesAndResetsPerDevicePolicy()
    {
        VulkanDeviceFaultFacility facility = new();

        facility.PublishKhrSupport(
            supportsDeviceFault: true,
            supportsVendorBinary: true,
            supportsReportMasked: true,
            supportsDeviceLostOnMasked: false,
            maxReportCount: 4);
        facility.PublishExtSupport(
            supportsDeviceFault: true,
            supportsVendorBinary: false);
        facility.PublishKhrCommandTable(null, null);

        facility.SupportsKhrDeviceFault.ShouldBeTrue();
        facility.SupportsKhrDeviceFaultVendorBinary.ShouldBeTrue();
        facility.SupportsKhrDeviceFaultReportMasked.ShouldBeTrue();
        facility.KhrDeviceFaultMaxReportCount.ShouldBe(4U);
        facility.SupportsExtDeviceFault.ShouldBeTrue();
        facility.IsUsingKhrDeviceFault.ShouldBeFalse();

        facility.Reset();

        facility.SupportsKhrDeviceFault.ShouldBeFalse();
        facility.SupportsExtDeviceFault.ShouldBeFalse();
        facility.KhrDeviceFaultMaxReportCount.ShouldBe(0U);
        facility.GetDeviceFaultReportsKhr.ShouldBeNull();
        facility.GetDeviceFaultDebugInfoKhr.ShouldBeNull();
    }

    [Test]
    public void RendererFacade_DoesNotRetainMovedNativeLifetimeState()
    {
        string instance = SourceContractWorkspace.ReadExactFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Instance.cs");
        string contextInstance = SourceContractWorkspace.ReadExactFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanDeviceContext.Instance.cs");
        string validation = SourceContractWorkspace.ReadExactFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanValidationDiagnostics.cs");
        string fault = SourceContractWorkspace.ReadExactFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Diagnostics/KhrDeviceFault/VulkanRenderer.KhrDeviceFault.cs");

        instance.ShouldNotContain("private Instance _instance");
        instance.ShouldNotContain("private ExtDebugUtils");
        contextInstance.ShouldContain("public Instance Instance { get; private set; }");
        contextInstance.ShouldContain("RendererNativeCallbackBridge.RegisterVulkanDebugHandler(HandleDebugMessage)");
        validation.ShouldNotContain("VulkanRenderer");
        fault.ShouldContain("_deviceContext.DeviceFaultFacility.SupportsKhrDeviceFault;");
        fault.ShouldContain("_deviceContext.DeviceFaultFacility.GetDeviceFaultReportsKhr;");
        fault.ShouldNotContain("private bool _supportsKhrDeviceFault;");
        fault.ShouldNotContain("private VkGetDeviceFaultReportsKhrDelegate? _vkGetDeviceFaultReportsKHR;");
    }

    [Test]
    public void EnabledExtensions_AreValidatedWithoutPrepublishingNativeSuccess()
    {
        VulkanDeviceContext context = CreateContext(["VK_EXT_transform_feedback"]);
        VulkanDeviceExtensionSet validated = context.ValidateEnabledDeviceExtensions(
            ["VK_EXT_transform_feedback"]);

        context.EnabledDeviceExtensions.ShouldBeEmpty();
        context.HasLogicalDevice.ShouldBeFalse();

        VulkanDeviceExtensionSet invalid = new(["VK_EXT_not_advertised"]);
        Should.Throw<InvalidOperationException>(() => context.AttachDevice(
            new Device((nint)0x201),
            createdThroughOpenXr: false,
            invalid));
        context.HasLogicalDevice.ShouldBeFalse();

        context.AttachDevice(
            new Device((nint)0x202),
            createdThroughOpenXr: false,
            validated);
        context.HasLogicalDevice.ShouldBeTrue();
        context.EnabledDeviceExtensions.ShouldContain("VK_EXT_transform_feedback");
        context.IsReady.ShouldBeFalse("queues and final capabilities are not published yet");
    }

    [Test]
    public void CoreOnlyCapabilities_HaveExplicitExactlyOncePublication()
    {
        VulkanDeviceContext context = CreateContext([]);
        VulkanDeviceExtensionSet enabled = context.ValidateEnabledDeviceExtensions([]);
        context.AttachDevice(
            new Device((nint)0x301),
            createdThroughOpenXr: false,
            enabled);
        VulkanDeviceCapabilities capabilities = new(
            context.AvailableDeviceExtensions,
            VulkanDeviceExtensionSet.Empty,
            context.EnabledDeviceExtensions,
            EVulkanDeviceCapability.DynamicRendering,
            EVulkanDeviceFallback.None);

        context.PublishCapabilities(capabilities);

        context.CapabilitiesPublished.ShouldBeTrue();
        context.Capabilities.Supports(EVulkanDeviceCapability.DynamicRendering).ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => context.PublishCapabilities(capabilities));
    }

    [Test]
    public void RendererFacade_DoesNotRetainMovedCapabilityState()
    {
        string physical = SourceContractWorkspace.ReadExactFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.PhysicalDevice.cs");
        string logical = SourceContractWorkspace.ReadExactFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.LogicalDevice.cs");
        string context = SourceContractWorkspace.ReadExactFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanDeviceContext.cs");
        string queueFamilies = SourceContractWorkspace.ReadExactFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Types/QueueFamilyIndices.cs");

        physical.ShouldNotContain("private PhysicalDevice _physicalDevice;");
        physical.ShouldNotContain("private VulkanPhysicalDeviceCapabilitySnapshot? _physicalDeviceCapabilitySnapshot;");
        logical.ShouldNotContain("private string[] _availableDeviceExtensions");
        logical.ShouldNotContain("private string[] _enabledDeviceExtensions");
        logical.ShouldNotContain("private VulkanDeviceCapabilities _deviceCapabilities");
        context.ShouldContain("public VulkanPhysicalDeviceCapabilitySnapshot? PhysicalDeviceCapabilities");
        context.ShouldContain("public VulkanDeviceExtensionSet AvailableDeviceExtensions");
        context.ShouldContain("public bool QueuesPublished");
        context.ShouldContain("private static void ValidateQueueFamily(");
        queueFamilies.ShouldNotContain("partial class VulkanRenderer");
    }

    private static VulkanDeviceContext CreateContext(
        string[] availableExtensions,
        VulkanDeviceContextConfiguration? configuration = null)
    {
        VulkanDeviceContext context = new(configuration);
        context.AttachInstance(
            new Instance((nint)0x51),
            [],
            Vk.Version13,
            createdThroughOpenXr: false,
            openXrBootstrapContext: null);
        context.AttachPhysicalDevice(
            new PhysicalDevice((nint)0x101),
            CreateSnapshot(availableExtensions),
            CreateQueueFamilies());
        return context;
    }

    private static VulkanPhysicalDeviceCapabilitySnapshot CreateSnapshot(
        string[] availableExtensions)
    {
        PhysicalDeviceProperties properties = default;
        properties.Limits.NonCoherentAtomSize = 256UL;
        properties.Limits.MinUniformBufferOffsetAlignment = 64UL;
        QueueFamilyProperties[] queueFamilies =
        [
            new QueueFamilyProperties
            {
                QueueFlags = QueueFlags.GraphicsBit |
                    QueueFlags.ComputeBit |
                    QueueFlags.TransferBit,
                QueueCount = 2,
            },
        ];
        return new VulkanPhysicalDeviceCapabilitySnapshot(
            default,
            properties,
            queueFamilies,
            new VulkanDeviceExtensionSet(availableExtensions));
    }

    private static QueueFamilyIndices CreateQueueFamilies()
        => new()
        {
            GraphicsFamilyIndex = 0,
            ComputeFamilyIndex = 0,
            TransferFamilyIndex = 0,
            GraphicsFamilySupportsCompute = true,
            GraphicsFamilySupportsTransfer = true,
        };
}
