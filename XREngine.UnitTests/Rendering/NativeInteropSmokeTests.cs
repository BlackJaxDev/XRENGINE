using System;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public class NativeInteropSmokeTests
{
    [Test]
    public void StreamlineLibrary_ExportsExpectedSymbols()
    {
        if (!TryFindNative("sl.interposer.dll", out string? path))
            Assert.Inconclusive("sl.interposer.dll was not found; Streamline DLSS cannot initialize. Place a recent Streamline redistributable next to the editor executable.");

        if (!NativeLibrary.TryLoad(path, out nint handle))
            Assert.Fail($"sl.interposer.dll located at '{path}' could not be loaded. Likely wrong architecture or missing dependencies.");

        try
        {
            bool hasSetOptions = TryGetExport(handle, "slDLSSSetOptions", out _);
            if (!hasSetOptions)
            {
                Assert.Fail("sl.interposer.dll is present but missing slDLSSSetOptions export. This usually means the Streamline version is too old for our binding.");
            }

            // Optional in older builds but required for auto-tuning; the check helps detect stale DLLs early.
            bool hasOptimalSettings = TryGetExport(handle, "slDLSSGetOptimalSettings", out _);
            Assert.IsTrue(hasOptimalSettings, "sl.interposer.dll is missing slDLSSGetOptimalSettings; update to a newer Streamline build if DLSS keeps failing.");
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    [Test]
    public void RestirNativeLibrary_ExportsExpectedSymbols()
    {
        if (!TryFindNative("RestirGI.Native.dll", out string? path))
            Assert.Inconclusive("RestirGI.Native.dll was not found. The NV ray tracing path will never activate without this binary built and deployed.");

        if (!NativeLibrary.TryLoad(path, out nint handle))
            Assert.Fail($"RestirGI.Native.dll located at '{path}' could not be loaded. Build the native project for the current platform and drop it next to the executable.");

        try
        {
            Assert.IsTrue(TryGetExport(handle, "InitReSTIRRayTracingNV", out _), "InitReSTIRRayTracingNV export is missing; rebuild the native ReSTIR bridge.");
            Assert.IsTrue(TryGetExport(handle, "BindReSTIRPipelineNV", out _), "BindReSTIRPipelineNV export is missing; rebuild the native ReSTIR bridge.");
            Assert.IsTrue(TryGetExport(handle, "TraceRaysNVWrapper", out _), "TraceRaysNVWrapper export is missing; rebuild the native ReSTIR bridge.");
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    private static bool TryFindNative(string fileName, out string? path)
    {
        string? current = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            string candidate = Path.Combine(current, fileName);
            if (File.Exists(candidate))
            {
                path = candidate;
                return true;
            }

            DirectoryInfo? parent = Directory.GetParent(current);
            current = parent?.FullName;
        }

        path = null;
        return false;
    }

    private static bool TryGetExport(nint handle, string name, out nint proc)
    {
        try
        {
            proc = NativeLibrary.GetExport(handle, name);
            return proc != IntPtr.Zero;
        }
        catch
        {
            proc = IntPtr.Zero;
            return false;
        }
    }
}