// XRENGINE block-compression shim.
//
// This file gives a small C ABI over the bc7enc_rdo encoders (bc7enc.cpp for BC7 and
// rgbcx.cpp for BC4 and BC5). Tools/Dependencies/Build-Bc7Enc.ps1 compiles it together
// with those sources at a pinned commit into one shared library. Only the MIT/Unlicense
// sources of bc7enc_rdo are compiled; bc7e.ispc (Apache 2.0) and LodePNG are not used.
//
// Every encode function takes a tightly packed RGBA8 image region and writes whole
// 4x4 blocks in row-major block order. A partial block at the right or bottom edge
// repeats the last column or row. The functions are thread safe after xre_bcn_init.

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <mutex>

#include "bc7enc.h"
#include "rgbcx.h"

#if defined(_WIN32)
#define XRE_BCN_API extern "C" __declspec(dllexport)
#else
#define XRE_BCN_API extern "C" __attribute__((visibility("default")))
#endif

namespace
{
    constexpr int kApiVersion = 1;

    // Result codes. Keep them in sync with the managed NativeBcnEncoder.
    constexpr int kOk = 0;
    constexpr int kInvalidArgument = -1;
    constexpr int kOutputTooSmall = -2;
    constexpr int kNotInitialized = -3;

    std::once_flag g_initOnce;
    bool g_initialized = false;

    void LoadBlock(const uint8_t* rgba, uint32_t width, uint32_t height, uint32_t blockX, uint32_t blockY, uint8_t* block)
    {
        for (uint32_t y = 0; y < 4; ++y)
        {
            uint32_t sourceY = blockY * 4 + y;
            if (sourceY >= height)
                sourceY = height - 1;

            for (uint32_t x = 0; x < 4; ++x)
            {
                uint32_t sourceX = blockX * 4 + x;
                if (sourceX >= width)
                    sourceX = width - 1;

                const uint8_t* pixel = rgba + (static_cast<size_t>(sourceY) * width + sourceX) * 4;
                std::memcpy(block + (y * 4 + x) * 4, pixel, 4);
            }
        }
    }

    bool ValidateArguments(const uint8_t* rgba, uint32_t width, uint32_t height, const uint8_t* output, size_t outputSize, uint32_t blockBytes, int& result)
    {
        if (!g_initialized)
        {
            result = kNotInitialized;
            return false;
        }

        if (rgba == nullptr || output == nullptr || width == 0 || height == 0)
        {
            result = kInvalidArgument;
            return false;
        }

        size_t blocks = static_cast<size_t>((width + 3) / 4) * ((height + 3) / 4);
        if (outputSize < blocks * blockBytes)
        {
            result = kOutputTooSmall;
            return false;
        }

        result = kOk;
        return true;
    }
}

/// Returns the version of this C ABI.
XRE_BCN_API int xre_bcn_api_version()
{
    return kApiVersion;
}

/// Builds the encoder tables. Call it once before any encode function. Later calls do nothing.
XRE_BCN_API void xre_bcn_init()
{
    std::call_once(g_initOnce, []()
    {
        bc7enc_compress_block_init();
        rgbcx::init();
        g_initialized = true;
    });
}

/// Encodes an RGBA8 region as BC7. A larger uber level gives better quality and takes
/// more time (0 to 4). Perceptual weights favor luma and suit color data; linear weights
/// treat all channels the same and suit normal and data maps.
XRE_BCN_API int xre_bcn_encode_bc7(
    const uint8_t* rgba,
    uint32_t width,
    uint32_t height,
    uint32_t uberLevel,
    int perceptual,
    uint8_t* output,
    size_t outputSize)
{
    int result;
    if (!ValidateArguments(rgba, width, height, output, outputSize, 16, result))
        return result;

    bc7enc_compress_block_params params;
    bc7enc_compress_block_params_init(&params);
    if (perceptual)
        bc7enc_compress_block_params_init_perceptual_weights(&params);
    else
        bc7enc_compress_block_params_init_linear_weights(&params);
    params.m_uber_level = uberLevel > BC7ENC_MAX_UBER_LEVEL ? BC7ENC_MAX_UBER_LEVEL : uberLevel;

    uint32_t blocksX = (width + 3) / 4;
    uint32_t blocksY = (height + 3) / 4;
    uint8_t block[16 * 4];
    for (uint32_t blockY = 0; blockY < blocksY; ++blockY)
    {
        for (uint32_t blockX = 0; blockX < blocksX; ++blockX)
        {
            LoadBlock(rgba, width, height, blockX, blockY, block);
            bc7enc_compress_block(output + (static_cast<size_t>(blockY) * blocksX + blockX) * 16, block, &params);
        }
    }

    return kOk;
}

/// Encodes one channel (0 to 3) of an RGBA8 region as BC4.
XRE_BCN_API int xre_bcn_encode_bc4(
    const uint8_t* rgba,
    uint32_t width,
    uint32_t height,
    uint32_t channel,
    uint8_t* output,
    size_t outputSize)
{
    int result;
    if (channel > 3)
        return kInvalidArgument;
    if (!ValidateArguments(rgba, width, height, output, outputSize, 8, result))
        return result;

    uint32_t blocksX = (width + 3) / 4;
    uint32_t blocksY = (height + 3) / 4;
    uint8_t block[16 * 4];
    for (uint32_t blockY = 0; blockY < blocksY; ++blockY)
    {
        for (uint32_t blockX = 0; blockX < blocksX; ++blockX)
        {
            LoadBlock(rgba, width, height, blockX, blockY, block);
            rgbcx::encode_bc4_hq(output + (static_cast<size_t>(blockY) * blocksX + blockX) * 8, block + channel, 4);
        }
    }

    return kOk;
}

/// Encodes two channels (0 to 3) of an RGBA8 region as BC5.
XRE_BCN_API int xre_bcn_encode_bc5(
    const uint8_t* rgba,
    uint32_t width,
    uint32_t height,
    uint32_t channel0,
    uint32_t channel1,
    uint8_t* output,
    size_t outputSize)
{
    int result;
    if (channel0 > 3 || channel1 > 3)
        return kInvalidArgument;
    if (!ValidateArguments(rgba, width, height, output, outputSize, 16, result))
        return result;

    uint32_t blocksX = (width + 3) / 4;
    uint32_t blocksY = (height + 3) / 4;
    uint8_t block[16 * 4];
    for (uint32_t blockY = 0; blockY < blocksY; ++blockY)
    {
        for (uint32_t blockX = 0; blockX < blocksX; ++blockX)
        {
            LoadBlock(rgba, width, height, blockX, blockY, block);
            rgbcx::encode_bc5_hq(output + (static_cast<size_t>(blockY) * blocksX + blockX) * 16, block, channel0, channel1, 4);
        }
    }

    return kOk;
}
