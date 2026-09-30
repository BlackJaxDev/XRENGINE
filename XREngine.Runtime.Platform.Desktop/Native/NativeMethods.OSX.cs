using System.Runtime.InteropServices;

namespace XREngine.Native
{
    public static partial class NativeMethods
    {
        public static class OSX
        {
            const string iokit = "/System/Library/Frameworks/IOKit.framework/IOKit";

            [DllImport(iokit)]
            private static extern IntPtr IOHIDCreateSharedMemory(IntPtr allocator, int options);

            [DllImport(iokit)]
            private static extern IntPtr IOHIDManagerCreate(IntPtr allocator, int options);

            [DllImport(iokit)]
            private static extern void IOHIDManagerSetDeviceMatching(IntPtr manager, IntPtr matchingDictionary);

            [DllImport(iokit)]
            private static extern void IOHIDManagerScheduleWithRunLoop(IntPtr manager, IntPtr runLoop, IntPtr runLoopMode);

            [DllImport(iokit)]
            private static extern IntPtr IOHIDManagerCopyDevices(IntPtr manager);

            [DllImport(iokit)]
            private static extern IntPtr IOHIDDeviceGetProperty(IntPtr device, IntPtr key);

            [DllImport(iokit)]
            private static extern void CFRelease(IntPtr cf);

            const string kIOHIDCapsLockStateKey = "CapsLockState";

            const string coreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

            [DllImport(coreFoundation)]
            private static extern nint CFArrayGetCount(IntPtr array);

            [DllImport(coreFoundation)]
            private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);

            public static bool DetermineCapsLockState(out bool capsOn)
            {
                capsOn = false;

                // Create an HID Manager
                IntPtr hidManager = IOHIDManagerCreate(IntPtr.Zero, 0);
                if (hidManager == IntPtr.Zero)
                    return false;

                // Get the list of devices
                IntPtr devices = IOHIDManagerCopyDevices(hidManager);
                if (devices == IntPtr.Zero)
                {
                    CFRelease(hidManager);
                    return false;
                }

                // Iterate through devices to find the first keyboard with caps lock state
                foreach (IntPtr device in EnumerateCFArray(devices))
                {
                    IntPtr capsLockState = IOHIDDeviceGetProperty(device, CFStringCreateWithCString(kIOHIDCapsLockStateKey));
                    if (capsLockState != IntPtr.Zero)
                    {
                        capsOn = CFBooleanGetValue(capsLockState);
                        break;
                    }
                }

                // Clean up
                CFRelease(devices);
                CFRelease(hidManager);

                return true;
            }

            [DllImport(iokit)]
            private static extern IntPtr CFStringCreateWithCString(string cStr);

            [DllImport(iokit)]
            private static extern bool CFBooleanGetValue(IntPtr boolean);

            private static IEnumerable<IntPtr> EnumerateCFArray(IntPtr cfArray)
            {
                // Get the count of elements in the CFArray
                nint count = CFArrayGetCount(cfArray);
                for (nint i = 0; i < count; i++)
                {
                    // Retrieve each element as an IntPtr
                    yield return CFArrayGetValueAtIndex(cfArray, i);
                }
            }
        }
    }
}
