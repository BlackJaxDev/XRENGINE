using System.Runtime.CompilerServices;
using XREngine.Rendering.Vulkan.RenderGraph;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private readonly record struct SwapchainRecordingTarget(
            Image Image,
            ImageView ImageView,
            Format ImageFormat,
            Extent2D Extent,
            Image DepthImage,
            ImageView DepthView,
            Format DepthFormat,
            ImageAspectFlags DepthAspect,
            ImageLayout InitialColorLayout,
            bool ImageEverPresentedAtRecordStart)
        {
            public bool IsValid =>
                Image.Handle != 0 &&
                ImageView.Handle != 0 &&
                Extent.Width != 0 &&
                Extent.Height != 0 &&
                DepthImage.Handle != 0 &&
                DepthView.Handle != 0;
        }

    }
}
