using System;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[Explicit("Requires vendor native DLLs deployed in the local test runtime path")]
public class NativeInteropSmokeTests
{
    [Test]
    public void StreamlineLibrary_ExportsExpectedSymbols()
    {
        if (!TryFindNative("sl.interposer.dll", out string? path))
            Assert.Inconclusive("sl.interposer.dll was not found; Streamline DLSS cannot initialize. Place a recent Streamline redistributable next to the editor executable.");

        string resolvedPath = path ?? throw new AssertionException("Expected sl.interposer.dll path to be resolved.");

        if (!NativeLibrary.TryLoad(resolvedPath, out nint handle))
            Assert.Fail($"sl.interposer.dll located at '{resolvedPath}' could not be loaded. Likely wrong architecture or missing dependencies.");

        try
        {
            Assert.That(TryGetExport(handle, "slInit", out _), Is.True, "sl.interposer.dll is missing slInit; update to a newer Streamline build.");
            Assert.That(TryGetExport(handle, "slShutdown", out _), Is.True, "sl.interposer.dll is missing slShutdown; update to a newer Streamline build.");
            Assert.That(TryGetExport(handle, "slGetFeatureRequirements", out _), Is.True, "sl.interposer.dll is missing slGetFeatureRequirements; Vulkan bridge init cannot query DLSS requirements before creating the sidecar device.");
            Assert.That(TryGetExport(handle, "slSetVulkanInfo", out _), Is.True, "sl.interposer.dll is missing slSetVulkanInfo; Vulkan bridge DLSS cannot initialize.");
            Assert.That(TryGetExport(handle, "slEvaluateFeature", out _), Is.True, "sl.interposer.dll is missing slEvaluateFeature; DLSS dispatch cannot execute.");
            Assert.That(TryGetExport(handle, "slAllocateResources", out _), Is.True, "sl.interposer.dll is missing slAllocateResources; bridge-side DLSS resource allocation cannot execute.");
            Assert.That(TryGetExport(handle, "slFreeResources", out _), Is.True, "sl.interposer.dll is missing slFreeResources; bridge-side DLSS resource teardown cannot execute.");
            Assert.That(TryGetExport(handle, "slSetTagForFrame", out _), Is.True, "sl.interposer.dll is missing slSetTagForFrame; DLSS resource tagging cannot execute.");
            Assert.That(TryGetExport(handle, "slSetConstants", out _), Is.True, "sl.interposer.dll is missing slSetConstants; DLSS camera constants cannot be uploaded.");
            Assert.That(TryGetExport(handle, "slGetFeatureFunction", out _), Is.True, "sl.interposer.dll is missing slGetFeatureFunction; DLSS feature-function resolution cannot execute.");
            Assert.That(TryGetExport(handle, "slGetNewFrameToken", out _), Is.True, "sl.interposer.dll is missing slGetNewFrameToken; bridge-side DLSS frame token allocation cannot execute.");
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    [Test]
    public void XessLibrary_ExportsExpectedVulkanSymbols()
    {
        if (!TryFindNative("libxess.dll", out string? primaryPath) && !TryFindNative("xess.dll", out primaryPath))
            Assert.Inconclusive("libxess.dll/xess.dll was not found; XeSS cannot initialize. Place a recent XeSS redistributable next to the editor executable.");

        string resolvedPath = primaryPath ?? throw new AssertionException("Expected a XeSS library path to be resolved.");

        if (!NativeLibrary.TryLoad(resolvedPath, out nint handle))
            Assert.Fail($"XeSS library located at '{resolvedPath}' could not be loaded. Likely wrong architecture or missing dependencies.");

        try
        {
            Assert.That(TryGetExport(handle, "xessVKGetRequiredInstanceExtensions", out _), Is.True, "XeSS is missing xessVKGetRequiredInstanceExtensions; Vulkan bridge init cannot query instance requirements.");
            Assert.That(TryGetExport(handle, "xessVKGetRequiredDeviceExtensions", out _), Is.True, "XeSS is missing xessVKGetRequiredDeviceExtensions; Vulkan bridge init cannot query device requirements.");
            Assert.That(TryGetExport(handle, "xessVKGetRequiredDeviceFeatures", out _), Is.True, "XeSS is missing xessVKGetRequiredDeviceFeatures; Vulkan bridge init cannot query device feature requirements.");
            Assert.That(TryGetExport(handle, "xessVKCreateContext", out _), Is.True, "XeSS is missing xessVKCreateContext; Vulkan XeSS context creation cannot execute.");
            Assert.That(TryGetExport(handle, "xessVKInit", out _), Is.True, "XeSS is missing xessVKInit; Vulkan XeSS initialization cannot execute.");
            Assert.That(TryGetExport(handle, "xessVKExecute", out _), Is.True, "XeSS is missing xessVKExecute; Vulkan XeSS dispatch cannot execute.");
            Assert.That(TryGetExport(handle, "xessDestroyContext", out _), Is.True, "XeSS is missing xessDestroyContext; Vulkan XeSS contexts cannot be destroyed cleanly.");
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

        string resolvedPath = path ?? throw new AssertionException("Expected RestirGI.Native.dll path to be resolved.");

        if (!NativeLibrary.TryLoad(resolvedPath, out nint handle))
            Assert.Fail($"RestirGI.Native.dll located at '{resolvedPath}' could not be loaded. Build the native project for the current platform and drop it next to the executable.");

        try
        {
            Assert.That(TryGetExport(handle, "InitReSTIRRayTracingNV", out _), Is.True, "InitReSTIRRayTracingNV export is missing; rebuild the native ReSTIR bridge.");
            Assert.That(TryGetExport(handle, "BindReSTIRPipelineNV", out _), Is.True, "BindReSTIRPipelineNV export is missing; rebuild the native ReSTIR bridge.");
            Assert.That(TryGetExport(handle, "TraceRaysNVWrapper", out _), Is.True, "TraceRaysNVWrapper export is missing; rebuild the native ReSTIR bridge.");
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    [Test]
    public void FastGltfBridgeLibrary_ExportsExpectedSymbols()
    {
        if (!TryFindNative("FastGltfBridge.Native.dll", out string? path))
            Assert.Inconclusive("FastGltfBridge.Native.dll was not found. Build the native glTF bridge and ensure it is copied next to the test runtime.");

        string resolvedPath = path ?? throw new AssertionException("Expected FastGltfBridge.Native.dll path to be resolved.");

        if (!NativeLibrary.TryLoad(resolvedPath, out nint handle))
            Assert.Fail($"FastGltfBridge.Native.dll located at '{resolvedPath}' could not be loaded. Likely wrong architecture or missing dependencies.");

        try
        {
            Assert.That(TryGetExport(handle, "xre_fastgltf_open_asset_utf8", out _), Is.True, "FastGltfBridge.Native.dll is missing xre_fastgltf_open_asset_utf8.");
            Assert.That(TryGetExport(handle, "xre_fastgltf_close_asset", out _), Is.True, "FastGltfBridge.Native.dll is missing xre_fastgltf_close_asset.");
            Assert.That(TryGetExport(handle, "xre_fastgltf_copy_last_error_utf8", out _), Is.True, "FastGltfBridge.Native.dll is missing xre_fastgltf_copy_last_error_utf8.");
            Assert.That(TryGetExport(handle, "xre_fastgltf_get_buffer_view_byte_length", out _), Is.True, "FastGltfBridge.Native.dll is missing xre_fastgltf_get_buffer_view_byte_length.");
            Assert.That(TryGetExport(handle, "xre_fastgltf_copy_buffer_view_bytes", out _), Is.True, "FastGltfBridge.Native.dll is missing xre_fastgltf_copy_buffer_view_bytes.");
            Assert.That(TryGetExport(handle, "xre_fastgltf_copy_accessor", out _), Is.True, "FastGltfBridge.Native.dll is missing xre_fastgltf_copy_accessor.");
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