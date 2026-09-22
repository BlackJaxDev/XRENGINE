using Silk.NET.Vulkan;
using Silk.NET.Maths;
using XREngine.Data.Geometry;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    /// <summary>
    /// Keeps layout and projection in the live presentation space while converting
    /// the final window composite to the fixed swapchain image that WSI will stretch.
    /// Scaling shared rectangle edges independently avoids gaps between split viewports.
    /// </summary>
    internal BoundingRectangle MapWindowPresentationRegionToBackbuffer(BoundingRectangle region)
    {
        VulkanDesktopWsiTargetDriver desktopWsiOutput =
            DesktopWsiOutput;
        bool interactiveResizeDispatchActive =
            desktopWsiOutput.IsInteractiveResizeInProgress ||
            RuntimeInteractiveResizeDispatchState.IsActive;
        if (!_outputRuntime.Desktop.PresentScalingActive ||
            !interactiveResizeDispatchActive)
            return region;

        // Recording must use the same surface snapshot that XRWindow latched for
        // this render dispatch. ResizeExtents remains live while Win32 is pumping
        // the modal sizing loop and can otherwise change between two commands in
        // the same primary buffer, producing incompatible viewport/scissor maps.
        Vector2D<int> presentationExtent = desktopWsiOutput.EffectiveFramebufferSize;
        if (presentationExtent.X <= 0 || presentationExtent.Y <= 0)
            presentationExtent = desktopWsiOutput.ResizeExtents.PresentationExtent;

        return ScalePresentationRegionToBackbuffer(
            region,
            presentationExtent,
            new Vector2D<int>((int)_outputRuntime.Desktop.Extent.Width, (int)_outputRuntime.Desktop.Extent.Height));
    }

    internal static BoundingRectangle ScalePresentationRegionToBackbuffer(
        BoundingRectangle region,
        Vector2D<int> presentationExtent,
        Vector2D<int> backbufferExtent)
    {
        if (presentationExtent.X <= 0 ||
            presentationExtent.Y <= 0 ||
            backbufferExtent.X <= 0 ||
            backbufferExtent.Y <= 0 ||
            (presentationExtent.X == backbufferExtent.X &&
             presentationExtent.Y == backbufferExtent.Y))
        {
            return region;
        }

        int left = ScalePresentationEdge(region.X, presentationExtent.X, backbufferExtent.X);
        int right = ScalePresentationEdge(
            checked(region.X + region.Width),
            presentationExtent.X,
            backbufferExtent.X);
        int bottom = ScalePresentationEdge(region.Y, presentationExtent.Y, backbufferExtent.Y);
        int top = ScalePresentationEdge(
            checked(region.Y + region.Height),
            presentationExtent.Y,
            backbufferExtent.Y);

        left = Math.Clamp(left, 0, backbufferExtent.X);
        right = Math.Clamp(right, left, backbufferExtent.X);
        bottom = Math.Clamp(bottom, 0, backbufferExtent.Y);
        top = Math.Clamp(top, bottom, backbufferExtent.Y);
        return new BoundingRectangle(left, bottom, right - left, top - bottom);
    }

    private static int ScalePresentationEdge(int edge, int presentationExtent, int backbufferExtent)
        => (int)Math.Round(
            edge * (double)backbufferExtent / presentationExtent,
            MidpointRounding.AwayFromZero);
}
