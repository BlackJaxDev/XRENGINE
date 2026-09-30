using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XREngine.Rendering;

namespace XREngine.UnitTests.SoftwareVulkan;

/// <summary>Process-lifetime Vulkan diagnostic callback in the non-collectible executable.</summary>
internal sealed unsafe class SoftwareVulkanCallbackEntryPoints : IRendererNativeCallbackEntryPoints
{
    public nint StreamlineLog => throw new NotSupportedException("No Streamline integration in this offscreen host.");
    public nint GetClipboardText => throw new NotSupportedException("No clipboard in this offscreen host.");
    public nint SetClipboardText => throw new NotSupportedException("No clipboard in this offscreen host.");
    public nint VulkanDebug
        => (nint)(delegate* unmanaged[Stdcall]<uint, uint, nint, nint, uint>)&OnDebugMessage;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint OnDebugMessage(uint severity, uint types, nint data, nint userData)
        => RendererNativeCallbackBridge.DispatchVulkanDebug(severity, types, data, userData);
}
