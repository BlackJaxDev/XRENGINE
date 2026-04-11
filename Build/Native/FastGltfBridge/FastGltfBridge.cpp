#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <memory>
#include <mutex>
#include <new>
#include <string>
#include <string_view>
#include <utility>

#include <fastgltf/core.hpp>
#include <fastgltf/tools.hpp>

namespace {

struct XreFastGltfAssetHandle;
using AssetHandle = XreFastGltfAssetHandle;

enum class XreFastGltfStatus : std::int32_t {
    Success = 0,
    InvalidArgument = 1,
    ParseFailed = 2,
    NotFound = 3,
    BufferTooSmall = 4,
    CopyFailed = 5,
};

enum class XreFastGltfCopyFormat : std::int32_t {
    Float32Scalar = 0,
    Float32Vec2 = 1,
    Float32Vec3 = 2,
    Float32Vec4 = 3,
    Float32Mat4 = 4,
    UInt32Scalar = 5,
    UInt32Vec4 = 6,
};

struct XreFastGltfAssetHandle {
    fastgltf::Parser parser;
    fastgltf::Asset asset;
    std::filesystem::path sourcePath;
    std::filesystem::path directory;

    mutable std::mutex errorMutex;
    std::string lastError;

    XreFastGltfAssetHandle(fastgltf::Parser&& parserIn, fastgltf::Asset&& assetIn, std::filesystem::path sourcePathIn)
        : parser(std::move(parserIn))
        , asset(std::move(assetIn))
        , sourcePath(std::move(sourcePathIn))
        , directory(sourcePath.parent_path()) {
    }

    void setError(std::string message) const {
        std::lock_guard<std::mutex> lock(errorMutex);
        const_cast<XreFastGltfAssetHandle*>(this)->lastError = std::move(message);
    }

    void clearError() const {
        std::lock_guard<std::mutex> lock(errorMutex);
        const_cast<XreFastGltfAssetHandle*>(this)->lastError.clear();
    }
};

constexpr fastgltf::Extensions kEnabledExtensions =
    fastgltf::Extensions::KHR_texture_transform |
    fastgltf::Extensions::KHR_texture_basisu |
    fastgltf::Extensions::MSFT_texture_dds |
    fastgltf::Extensions::KHR_mesh_quantization |
    fastgltf::Extensions::EXT_meshopt_compression |
    fastgltf::Extensions::KHR_lights_punctual |
    fastgltf::Extensions::EXT_texture_webp |
    fastgltf::Extensions::KHR_materials_specular |
    fastgltf::Extensions::KHR_materials_ior |
    fastgltf::Extensions::KHR_materials_iridescence |
    fastgltf::Extensions::KHR_materials_volume |
    fastgltf::Extensions::KHR_materials_transmission |
    fastgltf::Extensions::KHR_materials_clearcoat |
    fastgltf::Extensions::KHR_materials_emissive_strength |
    fastgltf::Extensions::KHR_materials_sheen |
    fastgltf::Extensions::KHR_materials_unlit |
    fastgltf::Extensions::KHR_materials_anisotropy |
    fastgltf::Extensions::EXT_mesh_gpu_instancing |
    fastgltf::Extensions::MSFT_packing_normalRoughnessMetallic |
    fastgltf::Extensions::MSFT_packing_occlusionRoughnessMetallic |
    fastgltf::Extensions::KHR_materials_variants |
    fastgltf::Extensions::KHR_accessor_float64 |
    fastgltf::Extensions::KHR_draco_mesh_compression;

constexpr fastgltf::Options kLoadOptions =
    fastgltf::Options::LoadExternalBuffers |
    fastgltf::Options::DecomposeNodeMatrices |
    fastgltf::Options::GenerateMeshIndices;

std::filesystem::path makePathFromUtf8(const char* path) {
    if (path == nullptr) {
        return {};
    }

    return std::filesystem::path(std::u8string_view(reinterpret_cast<const char8_t*>(path)));
}

template <typename T>
XreFastGltfStatus copyAccessor(const XreFastGltfAssetHandle* handle, std::uint32_t accessorIndex, void* destination, std::size_t destinationLength, std::size_t destinationStride) {
    if (handle == nullptr || destination == nullptr)
        return XreFastGltfStatus::InvalidArgument;
    if (accessorIndex >= handle->asset.accessors.size()) {
        handle->setError("Accessor index is out of range.");
        return XreFastGltfStatus::NotFound;
    }

    const auto& accessor = handle->asset.accessors[accessorIndex];
    const std::size_t elementSize = sizeof(T);
    const std::size_t stride = destinationStride == 0 ? elementSize : destinationStride;
    if (stride < elementSize) {
        handle->setError("Destination stride is smaller than the element size.");
        return XreFastGltfStatus::InvalidArgument;
    }

    const std::size_t requiredLength = accessor.count == 0 ? 0 : ((accessor.count - 1) * stride) + elementSize;
    if (destinationLength < requiredLength) {
        handle->setError("Destination buffer is too small for the requested accessor copy.");
        return XreFastGltfStatus::BufferTooSmall;
    }

    try {
        if (stride == elementSize) {
            fastgltf::copyFromAccessor<T>(handle->asset, accessor, destination);
        } else {
            auto* destinationBytes = static_cast<std::byte*>(destination);
            fastgltf::iterateAccessorWithIndex<T>(handle->asset, accessor, [&](const T& value, std::size_t index) {
                std::memcpy(destinationBytes + (index * stride), &value, sizeof(T));
            });
        }

        handle->clearError();
        return XreFastGltfStatus::Success;
    } catch (const std::exception& exception) {
        handle->setError(exception.what());
        return XreFastGltfStatus::CopyFailed;
    } catch (...) {
        handle->setError("Unknown exception while copying accessor data.");
        return XreFastGltfStatus::CopyFailed;
    }
}

}  // namespace

extern "C" {

__declspec(dllexport) XreFastGltfStatus xre_fastgltf_open_asset_utf8(const char* path, AssetHandle** outHandle) {
    if (path == nullptr || outHandle == nullptr)
        return XreFastGltfStatus::InvalidArgument;

    *outHandle = nullptr;

    try {
        std::filesystem::path sourcePath = makePathFromUtf8(path);
        if (!std::filesystem::exists(sourcePath))
            return XreFastGltfStatus::NotFound;

        fastgltf::Parser parser(kEnabledExtensions);
        fastgltf::GltfFileStream stream(sourcePath);
        if (!stream.isOpen())
            return XreFastGltfStatus::NotFound;

        auto loadedAsset = parser.loadGltf(stream, sourcePath.parent_path(), kLoadOptions);
        if (loadedAsset.error() != fastgltf::Error::None) {
            auto* handle = new XreFastGltfAssetHandle(std::move(parser), fastgltf::Asset(), sourcePath);
            handle->setError(std::string(fastgltf::getErrorMessage(loadedAsset.error())));
            *outHandle = handle;
            return XreFastGltfStatus::ParseFailed;
        }

        auto* handle = new XreFastGltfAssetHandle(std::move(parser), std::move(loadedAsset.get()), std::move(sourcePath));
        handle->clearError();
        *outHandle = handle;
        return XreFastGltfStatus::Success;
    } catch (const std::exception& exception) {
        auto* handle = new XreFastGltfAssetHandle(fastgltf::Parser(kEnabledExtensions), fastgltf::Asset(), makePathFromUtf8(path));
        handle->setError(exception.what());
        *outHandle = handle;
        return XreFastGltfStatus::ParseFailed;
    } catch (...) {
        auto* handle = new XreFastGltfAssetHandle(fastgltf::Parser(kEnabledExtensions), fastgltf::Asset(), makePathFromUtf8(path));
        handle->setError("Unknown exception while opening glTF asset.");
        *outHandle = handle;
        return XreFastGltfStatus::ParseFailed;
    }
}

__declspec(dllexport) void xre_fastgltf_close_asset(AssetHandle* handle) {
    delete handle;
}

__declspec(dllexport) XreFastGltfStatus xre_fastgltf_copy_last_error_utf8(
    AssetHandle* handle,
    void* buffer,
    std::size_t bufferLength,
    std::size_t* writtenLength) {
    if (writtenLength != nullptr)
        *writtenLength = 0;

    if (handle == nullptr)
        return XreFastGltfStatus::InvalidArgument;

    std::string error;
    {
        std::lock_guard<std::mutex> lock(handle->errorMutex);
        error = handle->lastError;
    }

    if (buffer == nullptr || bufferLength == 0) {
        if (writtenLength != nullptr)
            *writtenLength = error.size();
        return error.empty() ? XreFastGltfStatus::Success : XreFastGltfStatus::BufferTooSmall;
    }

    const std::size_t bytesToWrite = std::min(bufferLength, error.size());
    std::memcpy(buffer, error.data(), bytesToWrite);
    if (bytesToWrite < bufferLength)
        static_cast<char*>(buffer)[bytesToWrite] = '\0';
    if (writtenLength != nullptr)
        *writtenLength = error.size();

    return bytesToWrite == error.size() ? XreFastGltfStatus::Success : XreFastGltfStatus::BufferTooSmall;
}

__declspec(dllexport) XreFastGltfStatus xre_fastgltf_get_buffer_view_byte_length(
    AssetHandle* handle,
    std::uint32_t bufferViewIndex,
    std::size_t* byteLength) {
    if (handle == nullptr || byteLength == nullptr)
        return XreFastGltfStatus::InvalidArgument;
    if (bufferViewIndex >= handle->asset.bufferViews.size()) {
        handle->setError("Buffer view index is out of range.");
        return XreFastGltfStatus::NotFound;
    }

    *byteLength = handle->asset.bufferViews[bufferViewIndex].byteLength;
    handle->clearError();
    return XreFastGltfStatus::Success;
}

__declspec(dllexport) XreFastGltfStatus xre_fastgltf_copy_buffer_view_bytes(
    AssetHandle* handle,
    std::uint32_t bufferViewIndex,
    void* destination,
    std::size_t destinationLength) {
    if (handle == nullptr || destination == nullptr)
        return XreFastGltfStatus::InvalidArgument;
    if (bufferViewIndex >= handle->asset.bufferViews.size()) {
        handle->setError("Buffer view index is out of range.");
        return XreFastGltfStatus::NotFound;
    }

    const auto& bufferView = handle->asset.bufferViews[bufferViewIndex];
    if (destinationLength < bufferView.byteLength) {
        handle->setError("Destination buffer is too small for the requested buffer view copy.");
        return XreFastGltfStatus::BufferTooSmall;
    }

    try {
        fastgltf::DefaultBufferDataAdapter adapter;
        auto bytes = adapter(handle->asset, bufferViewIndex);
        std::memcpy(destination, bytes.data(), bufferView.byteLength);
        handle->clearError();
        return XreFastGltfStatus::Success;
    } catch (const std::exception& exception) {
        handle->setError(exception.what());
        return XreFastGltfStatus::CopyFailed;
    } catch (...) {
        handle->setError("Unknown exception while copying buffer view bytes.");
        return XreFastGltfStatus::CopyFailed;
    }
}

__declspec(dllexport) XreFastGltfStatus xre_fastgltf_copy_accessor(
    AssetHandle* handle,
    std::uint32_t accessorIndex,
    XreFastGltfCopyFormat format,
    void* destination,
    std::size_t destinationLength,
    std::size_t destinationStride) {
    switch (format) {
        case XreFastGltfCopyFormat::Float32Scalar:
            return copyAccessor<float>(handle, accessorIndex, destination, destinationLength, destinationStride);
        case XreFastGltfCopyFormat::Float32Vec2:
            return copyAccessor<fastgltf::math::fvec2>(handle, accessorIndex, destination, destinationLength, destinationStride);
        case XreFastGltfCopyFormat::Float32Vec3:
            return copyAccessor<fastgltf::math::fvec3>(handle, accessorIndex, destination, destinationLength, destinationStride);
        case XreFastGltfCopyFormat::Float32Vec4:
            return copyAccessor<fastgltf::math::fvec4>(handle, accessorIndex, destination, destinationLength, destinationStride);
        case XreFastGltfCopyFormat::Float32Mat4:
            return copyAccessor<fastgltf::math::fmat4x4>(handle, accessorIndex, destination, destinationLength, destinationStride);
        case XreFastGltfCopyFormat::UInt32Scalar:
            return copyAccessor<std::uint32_t>(handle, accessorIndex, destination, destinationLength, destinationStride);
        case XreFastGltfCopyFormat::UInt32Vec4:
            return copyAccessor<fastgltf::math::u32vec4>(handle, accessorIndex, destination, destinationLength, destinationStride);
        default:
            if (handle != nullptr)
                handle->setError("Unsupported accessor copy format.");
            return XreFastGltfStatus::InvalidArgument;
    }
}

}  // extern "C"