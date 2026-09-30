using Silk.NET.OpenXR;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI : IOpenXrGraphicsCalls
{
    private readonly CompositionLayerProjectionView[] _stagedProjectionViews = new CompositionLayerProjectionView[RenderFrameViewSet.MaxViewCount];
    private readonly object _acquiredImageLedgerLock = new();
    private readonly Dictionary<ulong, uint> _runtimeAcquiredImages = new(8);

    private bool HasAcquiredImage(ulong swapchainHandle)
    {
        lock (_acquiredImageLedgerLock)
            return _runtimeAcquiredImages.ContainsKey(swapchainHandle);
    }

    private bool AreSwapchainImagesReleased(ReadOnlySpan<ulong> swapchainHandles)
    {
        lock (_acquiredImageLedgerLock)
        {
            foreach (ulong handle in swapchainHandles)
                if (handle != 0 && _runtimeAcquiredImages.ContainsKey(handle))
                    return false;
            return true;
        }
    }

    private void AbandonAcquiredImagesAfterDeviceLoss()
    {
        lock (_acquiredImageLedgerLock)
            _runtimeAcquiredImages.Clear();
    }

    private void AbandonActiveSwapchainsAfterDeviceLoss()
    {
        AbandonAcquiredImagesAfterDeviceLoss();
        for (int view = 0; view < _viewCount; view++)
        {
            _swapchains[view] = default;
            _neutralSwapchains[view] = 0;
            _swapchainImageCounts[view] = 0;
        }
    }

    private void StageProjectionView(uint viewIndex)
    {
        if (viewIndex >= _viewCount)
            throw new ArgumentOutOfRangeException(nameof(viewIndex));
        fixed (CompositionLayerProjectionView* views = _stagedProjectionViews)
            FillProjectionView(viewIndex, views);
    }

    int IOpenXrGraphicsCalls.CreateSession(nint graphicsBindingChain, out ulong sessionHandle)
    {
        SessionCreateInfo info = new()
        {
            Type = StructureType.SessionCreateInfo,
            Next = (void*)graphicsBindingChain,
            SystemId = _systemId,
        };
        Session session = default;
        Result result = Api.CreateSession(_instance, ref info, ref session);
        sessionHandle = session.Handle;
        return (int)result;
    }

    int IOpenXrGraphicsCalls.DestroySession(ulong sessionHandle)
    {
        int result = (int)Api.DestroySession(new Session(sessionHandle));
        if (result == OpenXrResultCodes.Success && _session.Handle == sessionHandle)
            _session = default;
        return result;
    }

    int IOpenXrGraphicsCalls.EnumerateSwapchainFormats(Span<long> formats, out uint count)
    {
        count = 0;
        fixed (long* ptr = formats)
            return (int)Api.EnumerateSwapchainFormats(
                _session, (uint)formats.Length, ref count, formats.IsEmpty ? null : ptr);
    }

    int IOpenXrGraphicsCalls.CreateSwapchain(in OpenXrSwapchainDescriptor descriptor, out ulong swapchainHandle)
    {
        SwapchainCreateInfo info = new()
        {
            Type = StructureType.SwapchainCreateInfo,
            CreateFlags = (SwapchainCreateFlags)descriptor.CreateFlags,
            UsageFlags = (SwapchainUsageFlags)descriptor.UsageFlags,
            Format = descriptor.Format,
            Width = descriptor.Width,
            Height = descriptor.Height,
            SampleCount = descriptor.SampleCount,
            FaceCount = descriptor.FaceCount,
            ArraySize = descriptor.ArraySize,
            MipCount = descriptor.MipCount,
        };
        Swapchain swapchain = default;
        Result result = Api.CreateSwapchain(_session, in info, &swapchain);
        swapchainHandle = swapchain.Handle;
        return (int)result;
    }

    int IOpenXrGraphicsCalls.EnumerateSwapchainImages(
        ulong swapchainHandle, uint capacity, nint pinnedRendererStorage, out uint count)
    {
        count = 0;
        return (int)Api.EnumerateSwapchainImages(
            new Swapchain(swapchainHandle), capacity, &count,
            (SwapchainImageBaseHeader*)pinnedRendererStorage);
    }

    int IOpenXrGraphicsCalls.DestroySwapchain(ulong swapchainHandle)
    {
        if (HasAcquiredImage(swapchainHandle))
            throw new InvalidOperationException("OpenXR swapchain destruction was requested while a runtime image is acquired.");
        return (int)Api.DestroySwapchain(new Swapchain(swapchainHandle));
    }

    int IOpenXrGraphicsCalls.BeginFrame()
    {
        FrameBeginInfo info = new() { Type = StructureType.FrameBeginInfo };
        return (int)Api.BeginFrame(_session, in info);
    }

    int IOpenXrGraphicsCalls.AcquireSwapchainImage(ulong swapchainHandle, out uint imageIndex)
    {
        SwapchainImageAcquireInfo info = new() { Type = StructureType.SwapchainImageAcquireInfo };
        imageIndex = 0;
        int result = (int)Api.AcquireSwapchainImage(new Swapchain(swapchainHandle), in info, ref imageIndex);
        if (result == OpenXrResultCodes.Success)
        {
            lock (_acquiredImageLedgerLock)
            {
                _runtimeAcquiredImages.TryGetValue(swapchainHandle, out uint count);
                _runtimeAcquiredImages[swapchainHandle] = checked(count + 1);
            }
        }
        return result;
    }

    int IOpenXrGraphicsCalls.WaitSwapchainImage(ulong swapchainHandle, long timeoutNs)
    {
        SwapchainImageWaitInfo info = new() { Type = StructureType.SwapchainImageWaitInfo, Timeout = timeoutNs };
        return (int)Api.WaitSwapchainImage(new Swapchain(swapchainHandle), in info);
    }

    int IOpenXrGraphicsCalls.ReleaseSwapchainImage(ulong swapchainHandle)
    {
        SwapchainImageReleaseInfo info = new() { Type = StructureType.SwapchainImageReleaseInfo };
        int result = (int)Api.ReleaseSwapchainImage(new Swapchain(swapchainHandle), in info);
        if (result == OpenXrResultCodes.Success)
        {
            lock (_acquiredImageLedgerLock)
            {
                if (_runtimeAcquiredImages.TryGetValue(swapchainHandle, out uint count))
                {
                    if (count <= 1)
                        _runtimeAcquiredImages.Remove(swapchainHandle);
                    else
                        _runtimeAcquiredImages[swapchainHandle] = count - 1;
                }
            }
        }
        return result;
    }

    int IOpenXrGraphicsCalls.EndFrame(bool submitLayer)
    {
        CompositionLayerProjectionView* views = null;
        CompositionLayerProjection layer = default;
        CompositionLayerBaseHeader* header = null;
        if (submitLayer)
        {
            fixed (CompositionLayerProjectionView* stagedViews = _stagedProjectionViews)
            {
                views = stagedViews;
                layer = new CompositionLayerProjection
                {
                    Type = StructureType.CompositionLayerProjection,
                    Space = _appSpace,
                    ViewCount = _viewCount,
                    Views = views,
                };
                header = (CompositionLayerBaseHeader*)&layer;
                FrameEndInfo frame = new()
                {
                    Type = StructureType.FrameEndInfo,
                    DisplayTime = _frameState.PredictedDisplayTime,
                    EnvironmentBlendMode = EnvironmentBlendMode.Opaque,
                    LayerCount = 1,
                    Layers = &header,
                };
                return (int)Api.EndFrame(_session, in frame);
            }
        }

        FrameEndInfo empty = new()
        {
            Type = StructureType.FrameEndInfo,
            DisplayTime = _frameState.PredictedDisplayTime,
            EnvironmentBlendMode = EnvironmentBlendMode.Opaque,
        };
        return (int)Api.EndFrame(_session, in empty);
    }
}
