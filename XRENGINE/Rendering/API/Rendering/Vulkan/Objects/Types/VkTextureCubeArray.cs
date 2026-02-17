using Silk.NET.Vulkan;
using XREngine.Rendering.Models.Materials.Textures;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Vulkan wrapper for a cubemap array (<see cref="XRTextureCubeArray"/>).
    /// The total array-layer count equals <c>cubeCount × 6</c>. Each cube's six faces
    /// are packed contiguously, and attachment views can target individual layers.
    /// </summary>
    internal sealed class VkTextureCubeArray(VulkanRenderer api, XRTextureCubeArray data) : VkImageBackedTexture<XRTextureCubeArray>(api, data)
    {
        private const ImageCreateFlags CubeCompatibleFlag = (ImageCreateFlags)0x10;

        protected override ImageCreateFlags AdditionalImageFlags => CubeCompatibleFlag;
        protected override ImageViewType DefaultImageViewType => ImageViewType.TypeCubeArray;

        protected override TextureLayout DescribeTexture()
        {
            XRTextureCube[] cubes = Data.Cubes;
            uint extent = cubes.Length > 0 ? Math.Max(cubes[0].Extent, 1u) : 1u;
            uint layers = (uint)Math.Max(cubes.Length, 1) * 6u;
            uint mipLevels = (uint)Math.Max(
                cubes.Length > 0 ? cubes.Max(c => c?.Mipmaps?.Length ?? 1) : 1,
                1);
            return new TextureLayout(new Extent3D(extent, extent, 1), layers, mipLevels);
        }

        protected override AttachmentViewKey BuildAttachmentViewKey(int mipLevel, int layerIndex)
        {
            if (layerIndex < 0 && mipLevel <= 0)
                return default;

            uint baseLayer = (uint)Math.Max(layerIndex, 0);
            uint baseMip = (uint)Math.Max(mipLevel, 0);
            return new AttachmentViewKey(baseMip, 1, baseLayer, 1, ImageViewType.Type2D, AspectFlags);
        }

        protected override void PushTextureData()
        {
            Generate();

            XRTextureCube[] cubes = Data.Cubes;
            if (cubes is null || cubes.Length == 0)
            {
                Debug.VulkanWarning($"Cube array '{Data.Name ?? GetDescribingName()}' has no cube layers to upload.");
                return;
            }

            TransitionImageLayout(_currentImageLayout, ImageLayout.TransferDstOptimal);

            uint cubeCount = Math.Min((uint)cubes.Length, Math.Max(1u, ResolvedArrayLayers / 6u));
            for (uint cubeIndex = 0; cubeIndex < cubeCount; cubeIndex++)
            {
                XRTextureCube cube = cubes[cubeIndex];
                CubeMipmap[] mipmaps = cube.Mipmaps;
                if (mipmaps is null || mipmaps.Length == 0)
                    continue;

                uint levelCount = Math.Min((uint)mipmaps.Length, ResolvedMipLevels);
                for (uint level = 0; level < levelCount; level++)
                {
                    CubeMipmap? cubeMip = mipmaps[level];
                    if (cubeMip is null)
                        continue;

                    uint faceCount = Math.Min((uint)cubeMip.Sides.Length, 6u);
                    for (uint face = 0; face < faceCount; face++)
                    {
                        uint baseLayer = cubeIndex * 6u + face;
                        if (baseLayer >= ResolvedArrayLayers)
                            break;

                        Mipmap2D side = cubeMip.Sides[face];
                        if (!TryCreateStagingBuffer(side.Data, out Buffer stagingBuffer, out DeviceMemory stagingMemory))
                            continue;

                        try
                        {
                            Extent3D extent = new(Math.Max(side.Width, 1u), Math.Max(side.Height, 1u), 1);
                            CopyBufferToImage(stagingBuffer, level, baseLayer, 1, extent);
                        }
                        finally
                        {
                            DestroyStagingBuffer(stagingBuffer, stagingMemory);
                        }
                    }
                }
            }

            if (Data.AutoGenerateMipmaps && ResolvedMipLevels > 1)
                GenerateMipmapsGPU();
            else
                TransitionImageLayout(ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
        }
    }
}
