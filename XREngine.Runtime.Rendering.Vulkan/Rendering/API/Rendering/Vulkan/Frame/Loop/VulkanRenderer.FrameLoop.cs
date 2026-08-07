using System;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
public unsafe partial class VulkanRenderer
{
        internal VulkanFrameTelemetry FrameTelemetry => _frameTelemetry;

        /// <summary>
        /// Coordinates one allocation-free desktop Vulkan frame attempt.
        /// </summary>
        private void RenderComposedFrame(double delta)
            => FrameLoop.Render(delta);

        public override bool IsBackendReplacementFrameReady
            => Volatile.Read(ref _outputRuntime._hasPresentedCompleteSceneFrame) != 0;

    }
}
