namespace XREngine.UnitTests.Headless;

/// <summary>Runs deterministic production-runtime tests without a window, VR runtime, or test-host IPC.</summary>
internal static class Program
{
    private static int Main(string[] args)
        => new NUnitLite.AutoRun().Execute(args);
}
