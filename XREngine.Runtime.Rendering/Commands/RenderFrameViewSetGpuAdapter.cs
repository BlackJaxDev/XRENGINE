using System.Numerics;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Converts the immutable logical frame-view set into the fixed GPU descriptor layout without allocations.
/// </summary>
public static class RenderFrameViewSetGpuAdapter
{
    public static void Write(
        in RenderFrameViewSet viewSet,
        uint renderPassMaskLo,
        uint renderPassMaskHi,
        uint visibleCapacity,
        Span<GPUViewDescriptor> descriptors,
        Span<GPUViewConstants> constants)
    {
        if (descriptors.Length < viewSet.ViewCount)
            throw new ArgumentException("GPU descriptor storage is smaller than the captured frame view set.", nameof(descriptors));
        if (constants.Length < viewSet.ViewCount)
            throw new ArgumentException("GPU constant storage is smaller than the captured frame view set.", nameof(constants));

        visibleCapacity = Math.Max(1u, visibleCapacity);
        for (int i = 0; i < viewSet.ViewCount; i++)
        {
            RenderFrameViewDescriptor view = viewSet.GetView(i);
            uint offset = checked((uint)i * visibleCapacity);
            descriptors[i] = CreateDescriptor(view, renderPassMaskLo, renderPassMaskHi, offset, visibleCapacity);
            constants[i] = CreateConstants(view);
        }
    }

    private static GPUViewDescriptor CreateDescriptor(
        in RenderFrameViewDescriptor view,
        uint renderPassMaskLo,
        uint renderPassMaskHi,
        uint visibleOffset,
        uint visibleCapacity)
    {
        GPUViewFlags flags = GPUViewFlags.UsesSharedVisibility;
        if (view.IsLeftEyeFamily)
            flags |= GPUViewFlags.StereoEyeLeft;
        if (view.IsRightEyeFamily)
            flags |= GPUViewFlags.StereoEyeRight;
        if (view.IsWideView || view.IsStereoEye)
            flags |= GPUViewFlags.FullRes;
        if (view.IsInsetView)
            flags |= GPUViewFlags.Foveated;
        if (view.Kind is EVrOutputViewKind.CyclopeanDesktop)
            flags |= GPUViewFlags.Mirror;
        if (view.ParentContainsView)
            flags |= GPUViewFlags.ParentContainsView;
        if (view.DepthZeroToOne)
            flags |= GPUViewFlags.DepthZeroToOne;

        ViewFoveationContext foveation = view.Foveation;
        return new GPUViewDescriptor
        {
            ViewId = view.ViewId,
            ParentViewId = view.ParentViewId,
            Flags = (uint)flags,
            RenderPassMaskLo = renderPassMaskLo,
            RenderPassMaskHi = renderPassMaskHi,
            OutputLayer = view.OutputLayer,
            ViewRectX = (uint)view.ViewRect.X,
            ViewRectY = (uint)view.ViewRect.Y,
            ViewRectW = view.ViewRect.Width,
            ViewRectH = view.ViewRect.Height,
            VisibleOffset = visibleOffset,
            VisibleCapacity = visibleCapacity,
            FoveationA = foveation.IsEnabled
                ? new Vector4(foveation.RenderTargetUvCenter, foveation.Regions.InnerRadius, foveation.Regions.OuterRadius)
                : Vector4.Zero,
            FoveationB = foveation.IsEnabled
                ? new Vector4(foveation.Regions.GuardRadius, foveation.Regions.MidRadius, 0.0f, 0.0f)
                : Vector4.Zero,
        };
    }

    private static GPUViewConstants CreateConstants(in RenderFrameViewDescriptor view)
    {
        Vector4 positionAndNear = view.CameraPositionAndNear;
        Vector4 forwardAndFar = view.CameraForwardAndFar;
        if (positionAndNear == Vector4.Zero || forwardAndFar == Vector4.Zero)
            DeriveCameraVectors(view.ViewMatrix, ref positionAndNear, ref forwardAndFar);

        return new GPUViewConstants
        {
            View = view.ViewMatrix,
            Projection = view.ProjectionMatrix,
            ViewProjection = view.ViewProjectionMatrix,
            PrevViewProjection = view.PreviousViewProjectionMatrix,
            CameraPositionAndNear = positionAndNear,
            CameraForwardAndFar = forwardAndFar,
        };
    }

    private static void DeriveCameraVectors(in Matrix4x4 viewMatrix, ref Vector4 positionAndNear, ref Vector4 forwardAndFar)
    {
        if (!Matrix4x4.Invert(viewMatrix, out Matrix4x4 worldMatrix))
            return;

        if (positionAndNear == Vector4.Zero)
            positionAndNear = new Vector4(worldMatrix.Translation, 0.0f);
        if (forwardAndFar == Vector4.Zero)
            forwardAndFar = new Vector4(Vector3.Normalize(new Vector3(-worldMatrix.M31, -worldMatrix.M32, -worldMatrix.M33)), 0.0f);
    }
}