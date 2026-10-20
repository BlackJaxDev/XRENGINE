namespace XREngine.UnitTests.Headless;

using XREngine.Runtime.Diagnostics.Native;

/// <summary>Runs deterministic production-runtime tests without a window, VR runtime, or test-host IPC.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        NativeDebugBackendRegistration.EnsureRegistered();
        return new NUnitLite.AutoRun().Execute(args);
    }
}
