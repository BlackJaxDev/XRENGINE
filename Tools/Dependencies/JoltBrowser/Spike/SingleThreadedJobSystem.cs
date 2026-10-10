using System.Runtime.InteropServices;
using JoltPhysicsSharp;

internal sealed class SingleThreadedJobSystem : JobSystem
{
    private SingleThreadedJobSystem(nint handle) : base(handle)
        => OwnsHandle = true;

    public static SingleThreadedJobSystem Create(uint maxJobs = 2048)
    {
        nint handle = CreateNative(maxJobs);
        if (handle == 0)
            throw new InvalidOperationException("The browser Jolt single-threaded job system could not be created.");
        return new SingleThreadedJobSystem(handle);
    }

    [DllImport("joltc", EntryPoint = "XRE_JPH_JobSystemSingleThreaded_Create")]
    private static extern nint CreateNative(uint maxJobs);
}
