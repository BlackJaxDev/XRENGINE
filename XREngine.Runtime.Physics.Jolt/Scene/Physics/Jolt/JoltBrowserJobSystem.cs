using System.Runtime.InteropServices;
using JoltPhysicsSharp;
using XREngine.Execution;

namespace XREngine.Scene.Physics.Jolt;

/// <summary>Runs native Jolt jobs synchronously on the browser simulation owner.</summary>
internal sealed class JoltBrowserJobSystem : JobSystem
{
    private JoltBrowserJobSystem(nint handle) : base(handle)
        => OwnsHandle = true;

    public static JoltBrowserJobSystem Create(uint maxJobs = 2048)
    {
        if (!OperatingSystem.IsBrowser() || !RuntimeWorkScheduler.IsCallerThread
            || !RuntimePhysicsServices.Current.IsPhysicsThread)
            throw new InvalidOperationException("The browser Jolt job system requires an explicitly configured caller-thread browser runtime.");
        if (maxJobs == 0)
            throw new ArgumentOutOfRangeException(nameof(maxJobs));

        nint handle = CreateNative(maxJobs);
        if (handle == 0)
            throw new InvalidOperationException("The browser Jolt single-threaded job system could not be created.");
        return new JoltBrowserJobSystem(handle);
    }

    [DllImport("joltc", EntryPoint = "XRE_JPH_JobSystemSingleThreaded_Create")]
    private static extern nint CreateNative(uint maxJobs);
}
