#include <cstdint>
#include <cstring>
#include <limits>
#include <mutex>
#include <type_traits>
#include <unordered_map>

#include <vulkan/vulkan.h>

#define VMA_IMPLEMENTATION
#define VMA_STATIC_VULKAN_FUNCTIONS 1
#define VMA_DYNAMIC_VULKAN_FUNCTIONS 0
#define VMA_STATS_STRING_ENABLED 1
#include "vk_mem_alloc.h"

namespace {

template <typename THandle>
THandle from_u64(std::uint64_t handle) {
    if constexpr (std::is_pointer_v<THandle>) {
        return reinterpret_cast<THandle>(static_cast<std::uintptr_t>(handle));
    } else {
        return static_cast<THandle>(handle);
    }
}

template <typename THandle>
std::uint64_t to_u64(THandle handle) {
    if constexpr (std::is_pointer_v<THandle>) {
        return static_cast<std::uint64_t>(reinterpret_cast<std::uintptr_t>(handle));
    } else {
        return static_cast<std::uint64_t>(handle);
    }
}

VmaAllocationCreateInfo makeAllocationCreateInfo(std::uint32_t requiredProperties) {
    VmaAllocationCreateInfo createInfo = {};
    createInfo.usage = VMA_MEMORY_USAGE_UNKNOWN;
    createInfo.requiredFlags = static_cast<VkMemoryPropertyFlags>(requiredProperties);

    if ((requiredProperties & VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT) != 0) {
        createInfo.preferredFlags |= VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT;
    } else if ((requiredProperties & VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT) != 0) {
        createInfo.preferredFlags |= VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT;
    }

    if ((requiredProperties & VK_MEMORY_PROPERTY_HOST_CACHED_BIT) != 0) {
        createInfo.preferredFlags |= VK_MEMORY_PROPERTY_HOST_CACHED_BIT;
    }

    if ((requiredProperties & VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT) != 0) {
        createInfo.flags |= VMA_ALLOCATION_CREATE_HOST_ACCESS_RANDOM_BIT;
    }

    if ((requiredProperties & VK_MEMORY_PROPERTY_LAZILY_ALLOCATED_BIT) != 0) {
        createInfo.flags |= VMA_ALLOCATION_CREATE_DEDICATED_MEMORY_BIT;
    }

    return createInfo;
}

} // namespace

namespace {

std::mutex g_allocationMapCountsMutex;
std::unordered_map<void*, std::uint32_t> g_allocationMapCounts;

void recordAllocationMap(void* allocation) {
    if (allocation == nullptr)
        return;

    std::lock_guard<std::mutex> lock(g_allocationMapCountsMutex);
    std::uint32_t& count = g_allocationMapCounts[allocation];
    if (count < std::numeric_limits<std::uint32_t>::max())
        ++count;
}

bool consumeAllocationMap(void* allocation) {
    if (allocation == nullptr)
        return false;

    std::lock_guard<std::mutex> lock(g_allocationMapCountsMutex);
    auto it = g_allocationMapCounts.find(allocation);
    if (it == g_allocationMapCounts.end() || it->second == 0)
        return false;

    --it->second;
    return true;
}

std::uint32_t takeAllocationMapCount(void* allocation) {
    if (allocation == nullptr)
        return 0;

    std::lock_guard<std::mutex> lock(g_allocationMapCountsMutex);
    auto it = g_allocationMapCounts.find(allocation);
    if (it == g_allocationMapCounts.end())
        return 0;

    std::uint32_t count = it->second;
    g_allocationMapCounts.erase(it);
    return count;
}

} // namespace

extern "C" {

struct XreVmaAllocatorCreateInfo {
    std::uint64_t instance;
    std::uint64_t physicalDevice;
    std::uint64_t device;
    std::uint32_t vulkanApiVersion;
    std::uint32_t allocatorFlags;
};

struct XreVmaAllocationInfo {
    void* allocation;
    std::uint64_t memory;
    std::uint64_t offset;
    std::uint64_t size;
    std::uint32_t memoryTypeIndex;
    std::uint32_t memoryPropertyFlags;
};

struct XreVmaBudget {
    std::uint64_t blockCount;
    std::uint64_t allocationCount;
    std::uint64_t blockBytes;
    std::uint64_t allocationBytes;
    std::uint64_t usage;
    std::uint64_t budget;
};

__declspec(dllexport) std::uint32_t xre_vma_get_version() {
    return 0x00030300u;
}

__declspec(dllexport) VkResult xre_vma_create_allocator(
    const XreVmaAllocatorCreateInfo* createInfo,
    VmaAllocator* outAllocator) {
    if (createInfo == nullptr || outAllocator == nullptr)
        return VK_ERROR_INITIALIZATION_FAILED;

    *outAllocator = nullptr;

    VmaAllocatorCreateInfo allocatorInfo = {};
    allocatorInfo.instance = from_u64<VkInstance>(createInfo->instance);
    allocatorInfo.physicalDevice = from_u64<VkPhysicalDevice>(createInfo->physicalDevice);
    allocatorInfo.device = from_u64<VkDevice>(createInfo->device);
    allocatorInfo.vulkanApiVersion = createInfo->vulkanApiVersion;
    allocatorInfo.flags = static_cast<VmaAllocatorCreateFlags>(createInfo->allocatorFlags);

    return vmaCreateAllocator(&allocatorInfo, outAllocator);
}

__declspec(dllexport) void xre_vma_destroy_allocator(VmaAllocator allocator) {
    if (allocator != nullptr)
        vmaDestroyAllocator(allocator);
}

__declspec(dllexport) VkResult xre_vma_allocate_for_buffer(
    VmaAllocator allocator,
    std::uint64_t bufferHandle,
    std::uint32_t requiredProperties,
    XreVmaAllocationInfo* outAllocationInfo) {
    if (allocator == nullptr || bufferHandle == 0 || outAllocationInfo == nullptr)
        return VK_ERROR_INITIALIZATION_FAILED;

    std::memset(outAllocationInfo, 0, sizeof(XreVmaAllocationInfo));

    VmaAllocationCreateInfo createInfo = makeAllocationCreateInfo(requiredProperties);
    VmaAllocation allocation = nullptr;
    VmaAllocationInfo allocationInfo = {};

    VkResult result = vmaAllocateMemoryForBuffer(
        allocator,
        from_u64<VkBuffer>(bufferHandle),
        &createInfo,
        &allocation,
        &allocationInfo);

    if (result != VK_SUCCESS)
        return result;

    VkMemoryPropertyFlags memoryProperties = 0;
    vmaGetAllocationMemoryProperties(allocator, allocation, &memoryProperties);

    outAllocationInfo->allocation = allocation;
    outAllocationInfo->memory = to_u64(allocationInfo.deviceMemory);
    outAllocationInfo->offset = static_cast<std::uint64_t>(allocationInfo.offset);
    outAllocationInfo->size = static_cast<std::uint64_t>(allocationInfo.size);
    outAllocationInfo->memoryTypeIndex = allocationInfo.memoryType;
    outAllocationInfo->memoryPropertyFlags = static_cast<std::uint32_t>(memoryProperties);
    return VK_SUCCESS;
}

__declspec(dllexport) VkResult xre_vma_allocate_for_image(
    VmaAllocator allocator,
    std::uint64_t imageHandle,
    std::uint32_t requiredProperties,
    XreVmaAllocationInfo* outAllocationInfo) {
    if (allocator == nullptr || imageHandle == 0 || outAllocationInfo == nullptr)
        return VK_ERROR_INITIALIZATION_FAILED;

    std::memset(outAllocationInfo, 0, sizeof(XreVmaAllocationInfo));

    VmaAllocationCreateInfo createInfo = makeAllocationCreateInfo(requiredProperties);
    VmaAllocation allocation = nullptr;
    VmaAllocationInfo allocationInfo = {};

    VkResult result = vmaAllocateMemoryForImage(
        allocator,
        from_u64<VkImage>(imageHandle),
        &createInfo,
        &allocation,
        &allocationInfo);

    if (result != VK_SUCCESS)
        return result;

    VkMemoryPropertyFlags memoryProperties = 0;
    vmaGetAllocationMemoryProperties(allocator, allocation, &memoryProperties);

    outAllocationInfo->allocation = allocation;
    outAllocationInfo->memory = to_u64(allocationInfo.deviceMemory);
    outAllocationInfo->offset = static_cast<std::uint64_t>(allocationInfo.offset);
    outAllocationInfo->size = static_cast<std::uint64_t>(allocationInfo.size);
    outAllocationInfo->memoryTypeIndex = allocationInfo.memoryType;
    outAllocationInfo->memoryPropertyFlags = static_cast<std::uint32_t>(memoryProperties);
    return VK_SUCCESS;
}

__declspec(dllexport) VkResult xre_vma_bind_buffer_memory(
    VmaAllocator allocator,
    void* allocation,
    std::uint64_t bufferHandle) {
    if (allocator == nullptr || allocation == nullptr || bufferHandle == 0)
        return VK_ERROR_INITIALIZATION_FAILED;

    return vmaBindBufferMemory(
        allocator,
        static_cast<VmaAllocation>(allocation),
        from_u64<VkBuffer>(bufferHandle));
}

__declspec(dllexport) VkResult xre_vma_bind_image_memory(
    VmaAllocator allocator,
    void* allocation,
    std::uint64_t imageHandle) {
    if (allocator == nullptr || allocation == nullptr || imageHandle == 0)
        return VK_ERROR_INITIALIZATION_FAILED;

    return vmaBindImageMemory(
        allocator,
        static_cast<VmaAllocation>(allocation),
        from_u64<VkImage>(imageHandle));
}

__declspec(dllexport) void xre_vma_free(VmaAllocator allocator, void* allocation) {
    if (allocator != nullptr && allocation != nullptr) {
        const std::uint32_t mapCount = takeAllocationMapCount(allocation);
        for (std::uint32_t i = 0; i < mapCount; ++i)
            vmaUnmapMemory(allocator, static_cast<VmaAllocation>(allocation));

        vmaFreeMemory(allocator, static_cast<VmaAllocation>(allocation));
    }
}

__declspec(dllexport) VkResult xre_vma_map_memory(
    VmaAllocator allocator,
    void* allocation,
    void** outData) {
    if (allocator == nullptr || allocation == nullptr || outData == nullptr)
        return VK_ERROR_INITIALIZATION_FAILED;

    *outData = nullptr;
    VkResult result = vmaMapMemory(allocator, static_cast<VmaAllocation>(allocation), outData);
    if (result == VK_SUCCESS)
        recordAllocationMap(allocation);

    return result;
}

__declspec(dllexport) void xre_vma_unmap_memory(VmaAllocator allocator, void* allocation) {
    if (allocator != nullptr && allocation != nullptr && consumeAllocationMap(allocation))
        vmaUnmapMemory(allocator, static_cast<VmaAllocation>(allocation));
}

__declspec(dllexport) VkResult xre_vma_flush_allocation(
    VmaAllocator allocator,
    void* allocation,
    std::uint64_t offset,
    std::uint64_t size) {
    if (allocator == nullptr || allocation == nullptr)
        return VK_ERROR_INITIALIZATION_FAILED;

    return vmaFlushAllocation(
        allocator,
        static_cast<VmaAllocation>(allocation),
        static_cast<VkDeviceSize>(offset),
        static_cast<VkDeviceSize>(size));
}

__declspec(dllexport) VkResult xre_vma_invalidate_allocation(
    VmaAllocator allocator,
    void* allocation,
    std::uint64_t offset,
    std::uint64_t size) {
    if (allocator == nullptr || allocation == nullptr)
        return VK_ERROR_INITIALIZATION_FAILED;

    return vmaInvalidateAllocation(
        allocator,
        static_cast<VmaAllocation>(allocation),
        static_cast<VkDeviceSize>(offset),
        static_cast<VkDeviceSize>(size));
}

__declspec(dllexport) void xre_vma_get_allocation_info(
    VmaAllocator allocator,
    void* allocation,
    XreVmaAllocationInfo* outAllocationInfo) {
    if (allocator == nullptr || allocation == nullptr || outAllocationInfo == nullptr)
        return;

    VmaAllocationInfo allocationInfo = {};
    vmaGetAllocationInfo(allocator, static_cast<VmaAllocation>(allocation), &allocationInfo);

    VkMemoryPropertyFlags memoryProperties = 0;
    vmaGetAllocationMemoryProperties(allocator, static_cast<VmaAllocation>(allocation), &memoryProperties);

    outAllocationInfo->allocation = allocation;
    outAllocationInfo->memory = to_u64(allocationInfo.deviceMemory);
    outAllocationInfo->offset = static_cast<std::uint64_t>(allocationInfo.offset);
    outAllocationInfo->size = static_cast<std::uint64_t>(allocationInfo.size);
    outAllocationInfo->memoryTypeIndex = allocationInfo.memoryType;
    outAllocationInfo->memoryPropertyFlags = static_cast<std::uint32_t>(memoryProperties);
}

__declspec(dllexport) std::uint32_t xre_vma_get_heap_budgets(
    VmaAllocator allocator,
    XreVmaBudget* budgets,
    std::uint32_t capacity) {
    if (allocator == nullptr || budgets == nullptr || capacity == 0)
        return 0;

    VmaBudget nativeBudgets[VK_MAX_MEMORY_HEAPS] = {};
    vmaGetHeapBudgets(allocator, nativeBudgets);

    const std::uint32_t count = capacity < VK_MAX_MEMORY_HEAPS ? capacity : VK_MAX_MEMORY_HEAPS;
    for (std::uint32_t i = 0; i < count; ++i) {
        budgets[i].blockCount = nativeBudgets[i].statistics.blockCount;
        budgets[i].allocationCount = nativeBudgets[i].statistics.allocationCount;
        budgets[i].blockBytes = static_cast<std::uint64_t>(nativeBudgets[i].statistics.blockBytes);
        budgets[i].allocationBytes = static_cast<std::uint64_t>(nativeBudgets[i].statistics.allocationBytes);
        budgets[i].usage = static_cast<std::uint64_t>(nativeBudgets[i].usage);
        budgets[i].budget = static_cast<std::uint64_t>(nativeBudgets[i].budget);
    }

    return count;
}

__declspec(dllexport) const char* xre_vma_build_stats_string(
    VmaAllocator allocator,
    int detailedMap) {
    if (allocator == nullptr)
        return nullptr;

    char* statsString = nullptr;
    vmaBuildStatsString(allocator, &statsString, detailedMap != 0 ? VK_TRUE : VK_FALSE);
    return statsString;
}

__declspec(dllexport) void xre_vma_free_stats_string(
    VmaAllocator allocator,
    const char* statsString) {
    if (allocator != nullptr && statsString != nullptr)
        vmaFreeStatsString(allocator, const_cast<char*>(statsString));
}

} // extern "C"
