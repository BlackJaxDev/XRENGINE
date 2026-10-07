using System.Runtime.CompilerServices;
using XREngine.Runtime.Diagnostics.Native;

namespace XREngine.Benchmarks;

/// <summary>Registers native logging when a benchmark process loads this assembly.</summary>
internal static class NativeLoggingComposition
{
    [ModuleInitializer]
    internal static void Register() => NativeDebugBackendRegistration.EnsureRegistered();
}
