using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Vulkan.Extensions.NV;
using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    private bool _supportsAnisotropy;
    private Device device;
    private Queue graphicsQueue;
    private Queue presentQueue;
    private Queue computeQueue;
    private Queue transferQueue;

    public Device Device => device;
    public Queue GraphicsQueue => graphicsQueue;
    public Queue PresentQueue => presentQueue;
    public Queue ComputeQueue => computeQueue;
    public Queue TransferQueue => transferQueue;

    private void DestroyLogicalDevice()
    {
        DestroyCachedDescriptorSetLayouts();
        DestroyVulkanPipelineCache();
        Api!.DestroyDevice(device, null);
    }

    /// <summary>
    /// Checks if an optional device extension is supported by the physical device.
    /// </summary>
    private bool IsDeviceExtensionSupported(string extensionName)
    {
        uint extensionCount = 0;
        Api!.EnumerateDeviceExtensionProperties(_physicalDevice, (byte*)null, ref extensionCount, null);

        if (extensionCount == 0)
            return false;

        var availableExtensions = new ExtensionProperties[extensionCount];
        fixed (ExtensionProperties* availableExtensionsPtr = availableExtensions)
        {
            Api!.EnumerateDeviceExtensionProperties(_physicalDevice, (byte*)null, ref extensionCount, availableExtensionsPtr);
        }

        foreach (var ext in availableExtensions)
        {
            string name = SilkMarshal.PtrToString((nint)ext.ExtensionName) ?? string.Empty;
            if (name == extensionName)
                return true;
        }

        return false;
    }

    private unsafe void QueryDescriptorIndexingCapabilities()
    {
        _supportsRuntimeDescriptorArray = false;
        _supportsDescriptorBindingPartiallyBound = false;
        _supportsDescriptorBindingUpdateAfterBind = false;
        _supportsDescriptorBindingStorageImageUpdateAfterBind = false;

        PhysicalDeviceDescriptorIndexingFeatures descriptorIndexingFeatures = new()
        {
            SType = StructureType.PhysicalDeviceDescriptorIndexingFeatures,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &descriptorIndexingFeatures,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);

        _supportsRuntimeDescriptorArray = descriptorIndexingFeatures.RuntimeDescriptorArray;
        _supportsDescriptorBindingPartiallyBound = descriptorIndexingFeatures.DescriptorBindingPartiallyBound;
        _supportsDescriptorBindingStorageImageUpdateAfterBind = descriptorIndexingFeatures.DescriptorBindingStorageImageUpdateAfterBind;
        _supportsDescriptorBindingUpdateAfterBind = descriptorIndexingFeatures.DescriptorBindingSampledImageUpdateAfterBind ||
            descriptorIndexingFeatures.DescriptorBindingStorageImageUpdateAfterBind ||
            descriptorIndexingFeatures.DescriptorBindingStorageBufferUpdateAfterBind ||
            descriptorIndexingFeatures.DescriptorBindingUniformBufferUpdateAfterBind;
    }

    private unsafe void QueryNvMemoryDecompressionCapabilities(
        bool extensionEnabled,
        out bool featureSupported,
        out MemoryDecompressionMethodFlagsNV decompressionMethods,
        out ulong maxDecompressionIndirectCount)
    {
        featureSupported = false;
        decompressionMethods = 0;
        maxDecompressionIndirectCount = 0;

        if (!extensionEnabled)
            return;

        PhysicalDeviceMemoryDecompressionFeaturesNV memoryDecompressionFeatures = new()
        {
            SType = StructureType.PhysicalDeviceMemoryDecompressionFeaturesNV,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &memoryDecompressionFeatures,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        featureSupported = memoryDecompressionFeatures.MemoryDecompression;

        PhysicalDeviceMemoryDecompressionPropertiesNV memoryDecompressionProperties = new()
        {
            SType = StructureType.PhysicalDeviceMemoryDecompressionPropertiesNV,
            PNext = null,
        };

        PhysicalDeviceProperties2 properties2 = new()
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &memoryDecompressionProperties,
        };

        Api!.GetPhysicalDeviceProperties2(_physicalDevice, &properties2);
        decompressionMethods = (MemoryDecompressionMethodFlagsNV)memoryDecompressionProperties.DecompressionMethods;
        maxDecompressionIndirectCount = memoryDecompressionProperties.MaxDecompressionIndirectCount;
    }

    private unsafe void QueryNvCopyMemoryIndirectCapabilities(
        bool extensionEnabled,
        out bool featureSupported,
        out ulong supportedQueues)
    {
        featureSupported = false;
        supportedQueues = 0;

        if (!extensionEnabled)
            return;

        PhysicalDeviceCopyMemoryIndirectFeaturesNV copyMemoryIndirectFeatures = new()
        {
            SType = StructureType.PhysicalDeviceCopyMemoryIndirectFeaturesNV,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &copyMemoryIndirectFeatures,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        featureSupported = copyMemoryIndirectFeatures.IndirectCopy;

        PhysicalDeviceCopyMemoryIndirectPropertiesNV copyMemoryIndirectProperties = new()
        {
            SType = StructureType.PhysicalDeviceCopyMemoryIndirectPropertiesNV,
            PNext = null,
        };

        PhysicalDeviceProperties2 properties2 = new()
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &copyMemoryIndirectProperties,
        };

        Api!.GetPhysicalDeviceProperties2(_physicalDevice, &properties2);
        supportedQueues = (ulong)copyMemoryIndirectProperties.SupportedQueues;
    }

    private unsafe void QueryBufferDeviceAddressCapabilities(out bool featureSupported)
    {
        featureSupported = false;

        PhysicalDeviceBufferDeviceAddressFeatures bufferDeviceAddressFeatures = new()
        {
            SType = StructureType.PhysicalDeviceBufferDeviceAddressFeatures,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &bufferDeviceAddressFeatures,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        featureSupported = bufferDeviceAddressFeatures.BufferDeviceAddress;
    }

    private unsafe void QueryDynamicRenderingCapabilities(
        bool extensionEnabled,
        out bool featureSupported,
        out bool promotedToCore)
    {
        featureSupported = false;
        promotedToCore = false;

        Api!.GetPhysicalDeviceProperties(_physicalDevice, out PhysicalDeviceProperties properties);
        promotedToCore = properties.ApiVersion >= Vk.Version13;

        PhysicalDeviceDynamicRenderingFeatures dynamicRenderingFeatures = new()
        {
            SType = StructureType.PhysicalDeviceDynamicRenderingFeatures,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &dynamicRenderingFeatures,
        };

        Api.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);

        featureSupported = dynamicRenderingFeatures.DynamicRendering && (promotedToCore || extensionEnabled);
    }

    private unsafe void QueryShaderDrawParametersCapabilities(out bool featureSupported)
    {
        featureSupported = false;

        PhysicalDeviceVulkan11Features vulkan11Features = new()
        {
            SType = StructureType.PhysicalDeviceVulkan11Features,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &vulkan11Features,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        featureSupported = vulkan11Features.ShaderDrawParameters;
    }

    private unsafe void QueryIndexTypeUint8Capabilities(out bool featureSupported)
    {
        featureSupported = false;

        PhysicalDeviceIndexTypeUint8FeaturesEXT indexTypeUint8Features = new()
        {
            SType = StructureType.PhysicalDeviceIndexTypeUint8FeaturesExt,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &indexTypeUint8Features,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        featureSupported = indexTypeUint8Features.IndexTypeUint8;
    }

    /// <summary>
    /// Creates a logical device interface to the physical device with specific 
    /// queue families and extensions.
    /// </summary>
    /// <remarks>
    /// The logical device is the primary interface for an application to the GPU.
    /// This method:
    /// 1. Identifies required queue families (graphics and presentation)
    /// 2. Sets up queue creation information
    /// 3. Configures device features
    /// 4. Enables required device extensions
    /// 5. Enables validation layers if needed
    /// 6. Creates the device and obtains queue handles
    /// </remarks>
    private void CreateLogicalDevice()
    {
        // Get queue family indices required for rendering and presentation
        var indices = FamilyQueueIndices;

        // Create an array of queue family indices needed by this device
        var uniqueQueueFamilies = new[]
        {
            indices.GraphicsFamilyIndex!.Value,
            indices.PresentFamilyIndex!.Value,
            indices.ComputeFamilyIndex ?? indices.GraphicsFamilyIndex!.Value,
            indices.TransferFamilyIndex ?? indices.ComputeFamilyIndex ?? indices.GraphicsFamilyIndex!.Value
        };
        // Remove duplicates (graphics and present queue might be the same family)
        uniqueQueueFamilies = [.. uniqueQueueFamilies.Distinct()];

        // Allocate memory for queue create infos
        using var mem = GlobalMemory.Allocate(uniqueQueueFamilies.Length * sizeof(DeviceQueueCreateInfo));
        var queueCreateInfos = (DeviceQueueCreateInfo*)Unsafe.AsPointer(ref mem.GetPinnableReference());

        // Configure queue priority (1.0 = highest priority)
        float queuePriority = 1.0f;
        // Set up creation info for each queue family
        for (int i = 0; i < uniqueQueueFamilies.Length; i++)
            queueCreateInfos[i] = new()
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = uniqueQueueFamilies[i],
                QueueCount = 1, // Create one queue per family
                PQueuePriorities = &queuePriority
            };

        // Specify device features to enable (none specifically enabled here)
        PhysicalDeviceFeatures supportedFeatures = new();
        Api!.GetPhysicalDeviceFeatures(_physicalDevice, out supportedFeatures);

        PhysicalDeviceFeatures deviceFeatures = new();
        if (supportedFeatures.SamplerAnisotropy)
        {
            deviceFeatures.SamplerAnisotropy = Vk.True;
            _supportsAnisotropy = true;
        }

        if (supportedFeatures.FragmentStoresAndAtomics)
        {
            deviceFeatures.FragmentStoresAndAtomics = Vk.True;
            _supportsFragmentStoresAndAtomics = true;
        }

        // Build the list of extensions to enable (required + supported optional)
        var extensionsToEnable = new List<string>(deviceExtensions);
        foreach (var optionalExt in optionalDeviceExtensions)
        {
            if (IsDeviceExtensionSupported(optionalExt))
            {
                extensionsToEnable.Add(optionalExt);
                Debug.Vulkan($"[Vulkan] Enabling optional extension: {optionalExt}");
            }
            else
            {
                Debug.Vulkan($"[Vulkan] Optional extension not supported: {optionalExt}");
            }
        }

        var extensionsArray = extensionsToEnable.ToArray();

        bool descriptorIndexingExtensionEnabled = extensionsArray.Contains("VK_EXT_descriptor_indexing");
        bool descriptorIndexingRequestedByProfile = VulkanFeatureProfile.EnableDescriptorIndexing;
        QueryDescriptorIndexingCapabilities();

        bool descriptorIndexingCapabilityReady =
            _supportsRuntimeDescriptorArray &&
            _supportsDescriptorBindingPartiallyBound &&
            _supportsDescriptorBindingUpdateAfterBind;

        bool enableDescriptorIndexing = descriptorIndexingExtensionEnabled &&
            descriptorIndexingRequestedByProfile &&
            descriptorIndexingCapabilityReady;

        bool nvMemoryDecompressionExtensionEnabled = extensionsArray.Contains("VK_NV_memory_decompression");
        bool nvMemoryDecompressionRequestedByProfile = VulkanFeatureProfile.EnableRtxIoVulkanDecompression;

        QueryNvMemoryDecompressionCapabilities(
            nvMemoryDecompressionExtensionEnabled,
            out bool nvMemoryDecompressionFeatureSupported,
            out MemoryDecompressionMethodFlagsNV nvMemoryDecompressionMethods,
            out ulong nvMaxDecompressionIndirectCount);

        bool enableNvMemoryDecompression =
            nvMemoryDecompressionExtensionEnabled &&
            nvMemoryDecompressionRequestedByProfile &&
            nvMemoryDecompressionFeatureSupported;

        bool nvCopyMemoryIndirectExtensionEnabled = extensionsArray.Contains("VK_NV_copy_memory_indirect");
        bool nvCopyMemoryIndirectRequestedByProfile = VulkanFeatureProfile.EnableRtxIoVulkanCopyMemoryIndirect;

        QueryNvCopyMemoryIndirectCapabilities(
            nvCopyMemoryIndirectExtensionEnabled,
            out bool nvCopyMemoryIndirectFeatureSupported,
            out ulong nvCopyMemoryIndirectSupportedQueues);

        bool enableNvCopyMemoryIndirect =
            nvCopyMemoryIndirectExtensionEnabled &&
            nvCopyMemoryIndirectRequestedByProfile &&
            nvCopyMemoryIndirectFeatureSupported;

        QueryBufferDeviceAddressCapabilities(out bool bufferDeviceAddressFeatureSupported);
        bool enableBufferDeviceAddress = enableNvCopyMemoryIndirect && bufferDeviceAddressFeatureSupported;

        bool dynamicRenderingExtensionEnabled = extensionsArray.Contains("VK_KHR_dynamic_rendering");
        QueryDynamicRenderingCapabilities(
            dynamicRenderingExtensionEnabled,
            out bool dynamicRenderingFeatureSupported,
            out bool dynamicRenderingPromotedToCore);
        bool enableDynamicRenderingFeature = dynamicRenderingFeatureSupported;

        bool shaderDrawParametersExtensionEnabled = extensionsArray.Contains("VK_KHR_shader_draw_parameters");
        QueryShaderDrawParametersCapabilities(out bool shaderDrawParametersFeatureSupported);
        bool enableShaderDrawParametersFeature = shaderDrawParametersFeatureSupported;

        bool indexTypeUint8ExtensionEnabled =
            extensionsArray.Contains("VK_EXT_index_type_uint8") ||
            extensionsArray.Contains("VK_KHR_index_type_uint8");
        QueryIndexTypeUint8Capabilities(out bool indexTypeUint8FeatureSupported);
        bool enableIndexTypeUint8Feature = indexTypeUint8FeatureSupported;

        _nvMemoryDecompressionMethods = enableNvMemoryDecompression ? nvMemoryDecompressionMethods : 0;
        _nvMaxMemoryDecompressionIndirectCount = enableNvMemoryDecompression ? nvMaxDecompressionIndirectCount : 0;
        _nvCopyMemoryIndirectSupportedQueues = enableNvCopyMemoryIndirect ? nvCopyMemoryIndirectSupportedQueues : 0;

        PhysicalDeviceDescriptorIndexingFeatures descriptorIndexingFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceDescriptorIndexingFeatures,
            PNext = null,
            RuntimeDescriptorArray = enableDescriptorIndexing,
            DescriptorBindingPartiallyBound = enableDescriptorIndexing,
            DescriptorBindingSampledImageUpdateAfterBind = enableDescriptorIndexing,
            DescriptorBindingStorageImageUpdateAfterBind = enableDescriptorIndexing && _supportsDescriptorBindingStorageImageUpdateAfterBind,
            DescriptorBindingStorageBufferUpdateAfterBind = enableDescriptorIndexing,
            DescriptorBindingUniformBufferUpdateAfterBind = enableDescriptorIndexing,
        };

        PhysicalDeviceMemoryDecompressionFeaturesNV memoryDecompressionFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceMemoryDecompressionFeaturesNV,
            PNext = null,
            MemoryDecompression = enableNvMemoryDecompression,
        };

        PhysicalDeviceCopyMemoryIndirectFeaturesNV copyMemoryIndirectFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceCopyMemoryIndirectFeaturesNV,
            PNext = null,
            IndirectCopy = enableNvCopyMemoryIndirect,
        };

        PhysicalDeviceBufferDeviceAddressFeatures bufferDeviceAddressFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceBufferDeviceAddressFeatures,
            PNext = null,
            BufferDeviceAddress = enableBufferDeviceAddress,
        };

        PhysicalDeviceDynamicRenderingFeatures dynamicRenderingFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceDynamicRenderingFeatures,
            PNext = null,
            DynamicRendering = enableDynamicRenderingFeature,
        };

        PhysicalDeviceVulkan11Features vulkan11FeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceVulkan11Features,
            PNext = null,
            ShaderDrawParameters = enableShaderDrawParametersFeature,
        };

        PhysicalDeviceIndexTypeUint8FeaturesEXT indexTypeUint8FeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceIndexTypeUint8FeaturesExt,
            PNext = null,
            IndexTypeUint8 = enableIndexTypeUint8Feature,
        };

        void* enabledFeaturesPNext = null;
        if (enableDescriptorIndexing)
        {
            descriptorIndexingFeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &descriptorIndexingFeatureEnable;
        }

        if (enableNvMemoryDecompression)
        {
            memoryDecompressionFeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &memoryDecompressionFeatureEnable;
        }

        if (enableNvCopyMemoryIndirect)
        {
            copyMemoryIndirectFeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &copyMemoryIndirectFeatureEnable;
        }

        if (enableBufferDeviceAddress)
        {
            bufferDeviceAddressFeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &bufferDeviceAddressFeatureEnable;
        }

        if (enableDynamicRenderingFeature)
        {
            dynamicRenderingFeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &dynamicRenderingFeatureEnable;
        }

        if (enableShaderDrawParametersFeature)
        {
            vulkan11FeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &vulkan11FeatureEnable;
        }

        if (enableIndexTypeUint8Feature)
        {
            indexTypeUint8FeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &indexTypeUint8FeatureEnable;
        }

        PhysicalDeviceFeatures2 featureChain = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = enabledFeaturesPNext,
            Features = deviceFeatures,
        };

        // Configure the logical device creation
        DeviceCreateInfo createInfo = new()
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = (uint)uniqueQueueFamilies.Length,
            PQueueCreateInfos = queueCreateInfos,

            PNext = &featureChain,
            PEnabledFeatures = null,

            // Enable required device extensions (e.g., swapchain)
            EnabledExtensionCount = (uint)extensionsArray.Length,
            PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensionsArray)
        };

        // Enable validation layers if debugging is enabled
        if (EnableValidationLayers)
        {
            createInfo.EnabledLayerCount = (uint)validationLayers.Length;
            createInfo.PpEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(validationLayers);
        }
        else
            createInfo.EnabledLayerCount = 0;

        // Create the logical device
        if (Api!.CreateDevice(_physicalDevice, in createInfo, null, out device) != Result.Success)
            throw new Exception("Failed to create logical device.");

        _supportsDescriptorIndexing = enableDescriptorIndexing;
        _supportsNvMemoryDecompression = enableNvMemoryDecompression;
        _supportsNvCopyMemoryIndirect = enableNvCopyMemoryIndirect;
        _supportsBufferDeviceAddress = enableBufferDeviceAddress;
        _supportsDynamicRendering = dynamicRenderingFeatureSupported;
        _supportsIndexTypeUint8 = enableIndexTypeUint8Feature;

        if (descriptorIndexingExtensionEnabled && !enableDescriptorIndexing)
        {
            Debug.VulkanWarning(
                "[Vulkan] Descriptor indexing extension present but disabled (requested={0}, runtimeArray={1}, partiallyBound={2}, updateAfterBind={3}).",
                descriptorIndexingRequestedByProfile,
                _supportsRuntimeDescriptorArray,
                _supportsDescriptorBindingPartiallyBound,
                _supportsDescriptorBindingUpdateAfterBind);
        }
            else if (enableDescriptorIndexing && !_supportsDescriptorBindingStorageImageUpdateAfterBind)
            {
                Debug.VulkanWarning(
                "[Vulkan] Descriptor indexing enabled without storage-image update-after-bind support; storage image bindings will not use UPDATE_AFTER_BIND flags.");
            }

            if (!enableShaderDrawParametersFeature && !shaderDrawParametersExtensionEnabled)
            {
                Debug.VulkanWarning(
                    "[Vulkan] Draw parameters support unavailable (shaderDrawParametersFeature={0}, extensionEnabled={1}). Shaders using gl_BaseVertex/gl_BaseInstance may fail.",
                    shaderDrawParametersFeatureSupported,
                    shaderDrawParametersExtensionEnabled);
            }

            if (nvMemoryDecompressionExtensionEnabled && !enableNvMemoryDecompression)
            {
                Debug.VulkanWarning(
                "[Vulkan] VK_NV_memory_decompression present but disabled (requested={0}, featureSupported={1}).",
                nvMemoryDecompressionRequestedByProfile,
                nvMemoryDecompressionFeatureSupported);
            }

            if (nvCopyMemoryIndirectExtensionEnabled && !enableNvCopyMemoryIndirect)
            {
                Debug.VulkanWarning(
                    "[Vulkan] VK_NV_copy_memory_indirect present but disabled (requested={0}, featureSupported={1}).",
                    nvCopyMemoryIndirectRequestedByProfile,
                    nvCopyMemoryIndirectFeatureSupported);
            }

            if (enableNvCopyMemoryIndirect && !enableBufferDeviceAddress)
            {
                Debug.VulkanWarning(
                    "[Vulkan] VK_NV_copy_memory_indirect enabled but buffer device address is unavailable; indirect copy commands will be disabled.");
            }

            if (!enableIndexTypeUint8Feature)
            {
                Debug.VulkanWarning(
                    "[Vulkan] UINT8 index type unsupported or disabled (featureSupported={0}, extensionEnabled={1}). Byte-sized index buffers will be skipped.",
                    indexTypeUint8FeatureSupported,
                    indexTypeUint8ExtensionEnabled);
            }

        // Load optional extensions
        LoadOptionalDeviceExtensions(extensionsArray);

        // Retrieve handles to the queues we need
        Api!.GetDeviceQueue(device, indices.GraphicsFamilyIndex!.Value, 0, out graphicsQueue);
        Api!.GetDeviceQueue(device, indices.PresentFamilyIndex!.Value, 0, out presentQueue);
        Api!.GetDeviceQueue(device, indices.ComputeFamilyIndex ?? indices.GraphicsFamilyIndex!.Value, 0, out computeQueue);
        Api!.GetDeviceQueue(device, indices.TransferFamilyIndex ?? indices.ComputeFamilyIndex ?? indices.GraphicsFamilyIndex!.Value, 0, out transferQueue);

        // Clean up allocated memory for validation layer names
        if (EnableValidationLayers)
            SilkMarshal.Free((nint)createInfo.PpEnabledLayerNames);

        // Clean up allocated memory for extension names
        SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);
    }

    /// <summary>
    /// Loads optional device extension handles after device creation.
    /// </summary>
    private void LoadOptionalDeviceExtensions(string[] enabledExtensions)
    {
        bool descriptorIndexingExtensionLoaded = enabledExtensions.Contains("VK_EXT_descriptor_indexing");

        // Check if VK_KHR_draw_indirect_count was enabled
        if (enabledExtensions.Contains("VK_KHR_draw_indirect_count"))
        {
            if (Api!.TryGetDeviceExtension(instance, device, out _khrDrawIndirectCount))
            {
                _supportsDrawIndirectCount = true;
                Debug.Vulkan("[Vulkan] VK_KHR_draw_indirect_count extension loaded successfully.");
            }
            else
            {
                Debug.VulkanWarning("[Vulkan] Failed to load VK_KHR_draw_indirect_count extension handle.");
                _supportsDrawIndirectCount = false;
            }
        }

        if (enabledExtensions.Contains("VK_NV_memory_decompression") && _supportsNvMemoryDecompression)
        {
            if (Api!.TryGetDeviceExtension(instance, device, out _nvMemoryDecompression))
            {
                _supportsNvMemoryDecompression = true;
                Debug.Vulkan(
                    "[Vulkan] VK_NV_memory_decompression loaded successfully (methodsMask=0x{0:X}, maxIndirectCount={1}).",
                    _nvMemoryDecompressionMethods,
                    _nvMaxMemoryDecompressionIndirectCount);
            }
            else
            {
                Debug.VulkanWarning("[Vulkan] Failed to load VK_NV_memory_decompression extension handle.");
                _supportsNvMemoryDecompression = false;
                _nvMemoryDecompressionMethods = 0;
                _nvMaxMemoryDecompressionIndirectCount = 0;
            }
        }

        if (enabledExtensions.Contains("VK_NV_copy_memory_indirect") && _supportsNvCopyMemoryIndirect)
        {
            if (Api!.TryGetDeviceExtension(instance, device, out _nvCopyMemoryIndirect))
            {
                _supportsNvCopyMemoryIndirect = true;
                Debug.Vulkan(
                    "[Vulkan] VK_NV_copy_memory_indirect loaded successfully (supportedQueuesMask=0x{0:X}).",
                    _nvCopyMemoryIndirectSupportedQueues);
            }
            else
            {
                Debug.VulkanWarning("[Vulkan] Failed to load VK_NV_copy_memory_indirect extension handle.");
                _supportsNvCopyMemoryIndirect = false;
                _nvCopyMemoryIndirectSupportedQueues = 0;
            }
        }

        if (_supportsNvCopyMemoryIndirect && !_supportsBufferDeviceAddress)
        {
            _supportsNvCopyMemoryIndirect = false;
            _nvCopyMemoryIndirectSupportedQueues = 0;
        }

        if (_supportsDynamicRendering)
        {
            Debug.Vulkan(
                "[Vulkan] Dynamic rendering capability available; current renderer remains render-pass based pending compatibility migration.");
        }
        else
        {
            Debug.Vulkan(
                "[Vulkan] Dynamic rendering capability unavailable on this runtime/profile combination.");
        }

        CreateVulkanPipelineCache();

        Engine.Rendering.State.HasVulkanMemoryDecompression = SupportsNvMemoryDecompression;
        Engine.Rendering.State.HasVulkanCopyMemoryIndirect = SupportsNvCopyMemoryIndirect;
        Engine.Rendering.State.HasVulkanRtxIo = SupportsNvMemoryDecompression || SupportsNvCopyMemoryIndirect;

        if (descriptorIndexingExtensionLoaded && _supportsDescriptorIndexing)
            Debug.Vulkan("[Vulkan] VK_EXT_descriptor_indexing enabled for descriptor update-after-bind support.");
    }
}