using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.OpenXR;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    private IOpenXrNativeGraphicsBorrow BorrowNativeGraphicsDispatch()
    {
        lock (_nativeGraphicsBorrowLock)
        {
            if (_nativeGraphicsBorrowClosing || _instance.Handle == 0 || Api is null)
                throw new InvalidOperationException("OpenXR native graphics dispatch is unavailable during instance teardown.");

            _nativeGraphicsBorrowCount++;
            return new NativeGraphicsBorrow(this, Api, _instance);
        }
    }

    private void WaitForNativeGraphicsBorrows()
    {
        lock (_nativeGraphicsBorrowLock)
        {
            _nativeGraphicsBorrowClosing = true;
            while (_nativeGraphicsBorrowCount != 0)
                Monitor.Wait(_nativeGraphicsBorrowLock);
        }
    }

    private void ReopenNativeGraphicsBorrows()
    {
        lock (_nativeGraphicsBorrowLock)
        {
            if (_nativeGraphicsBorrowCount != 0)
                throw new InvalidOperationException("OpenXR instance generation cannot change while native graphics children are borrowed.");
            _nativeInstanceGeneration++;
            _nativeGraphicsBorrowClosing = false;
        }
    }

    private sealed class NativeGraphicsBorrow(OpenXRAPI owner, XR api, Instance instance) : IOpenXrNativeGraphicsBorrow
    {
        private int _disposed;

        public ulong InstanceHandle => instance.Handle;

        public nint GetInstanceProcAddress(string name)
        {
            lock (owner._nativeGraphicsBorrowLock)
            {
                if (_disposed != 0)
                    throw new ObjectDisposedException(nameof(NativeGraphicsBorrow));
                PfnVoidFunction function = default;
                Result result = api.GetInstanceProcAddr(instance, name, ref function);
                if (result != Result.Success || (nint)function == 0)
                    throw new InvalidOperationException($"OpenXR could not resolve {name}: {result}.");
                return (nint)function;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            lock (owner._nativeGraphicsBorrowLock)
            {
                owner._nativeGraphicsBorrowCount--;
                Monitor.PulseAll(owner._nativeGraphicsBorrowLock);
            }
        }
    }
}
