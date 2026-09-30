using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

[StructLayout(LayoutKind.Sequential)]
public struct IPLSceneSettings
{
    public IPLSceneType type;
    public IntPtr closestHitCallback;
    public IntPtr anyHitCallback;
    public IntPtr batchedClosestHitCallback;
    public IntPtr batchedAnyHitCallback;
    public IntPtr userData;
    public IPLEmbreeDevice embreeDevice;
    public IPLRadeonRaysDevice radeonRaysDevice;

    public IPLClosestHitCallback? ClosestHitCallback
    {
        readonly get => GetClosestHitCallback();
        set => SetClosestHitCallback(value);
    }
    public IPLAnyHitCallback? AnyHitCallback
    {
        readonly get => GetAnyHitCallback();
        set => SetAnyHitCallback(value);
    }
    public IPLBatchedClosestHitCallback? BatchedClosestHitCallback
    {
        readonly get => GetBatchedClosestHitCallback();
        set => SetBatchedClosestHitCallback(value);
    }
    public IPLBatchedAnyHitCallback? BatchedAnyHitCallback
    {
        readonly get => GetBatchedAnyHitCallback();
        set => SetBatchedAnyHitCallback(value);
    }

    private readonly IPLClosestHitCallback? GetClosestHitCallback()
        => closestHitCallback == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<IPLClosestHitCallback>(closestHitCallback);
    private void SetClosestHitCallback(IPLClosestHitCallback? value)
        => closestHitCallback = value is null ? IntPtr.Zero : Marshal.GetFunctionPointerForDelegate(value);

    private readonly IPLAnyHitCallback? GetAnyHitCallback()
        => anyHitCallback == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<IPLAnyHitCallback>(anyHitCallback);
    private void SetAnyHitCallback(IPLAnyHitCallback? value)
        => anyHitCallback = value is null ? IntPtr.Zero : Marshal.GetFunctionPointerForDelegate(value);

    private readonly IPLBatchedClosestHitCallback? GetBatchedClosestHitCallback()
        => batchedClosestHitCallback == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<IPLBatchedClosestHitCallback>(batchedClosestHitCallback);
    private void SetBatchedClosestHitCallback(IPLBatchedClosestHitCallback? value)
        => batchedClosestHitCallback = value is null ? IntPtr.Zero : Marshal.GetFunctionPointerForDelegate(value);

    private readonly IPLBatchedAnyHitCallback? GetBatchedAnyHitCallback()
        => batchedAnyHitCallback == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<IPLBatchedAnyHitCallback>(batchedAnyHitCallback);
    private void SetBatchedAnyHitCallback(IPLBatchedAnyHitCallback? value)
        => batchedAnyHitCallback = value is null ? IntPtr.Zero : Marshal.GetFunctionPointerForDelegate(value);
}