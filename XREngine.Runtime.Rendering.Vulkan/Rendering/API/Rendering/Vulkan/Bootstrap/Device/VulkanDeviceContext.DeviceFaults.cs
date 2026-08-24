using System;
using System.IO;
using System.Text;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Device-fault retrieval and persistence operations owned by the native-device
/// context. Renderer composition code supplies policy and log correlation, but
/// does not own native fault commands or artifacts.
/// </summary>
internal sealed partial class VulkanDeviceContext
{
    internal VulkanKhrDeviceFaultCapabilityQuery QueryKhrDeviceFaultCapabilities(
        Vk api,
        bool extensionEnabled)
        => DeviceFaultFacility.QueryKhrCapabilities(api, PhysicalDevice, extensionEnabled);

    internal bool TryLoadKhrDeviceFaultCommandTable(
        Vk api,
        out nint reportsAddress,
        out nint debugInfoAddress)
        => DeviceFaultFacility.TryLoadKhrCommandTable(
            api,
            Device,
            out reportsAddress,
            out debugInfoAddress);

    internal void ReleaseKhrDeviceFaultCommandTable()
        => DeviceFaultFacility.ResetKhrCommandTable();

    /// <summary>
    /// Retrieves, formats, and persists a bounded native device-fault capture.
    /// </summary>
    internal bool TryAppendPersistedKhrDeviceFaultSummary(
        StringBuilder builder,
        in VulkanDiagnosticOptions options,
        bool includeVendorBinary)
    {
        ArgumentNullException.ThrowIfNull(builder);
        VulkanDeviceFaultCapture? capture = DeviceFaultFacility.CaptureKhr(
            Device,
            options,
            includeVendorBinary);
        if (capture is null)
            return false;

        PersistDeviceFaultCapture(capture);
        capture.AppendSummary(builder);
        return true;
    }

    /// <summary>
    /// Retrieves, formats, and persists the EXT compatibility device-fault
    /// capture when the KHR command path is unavailable.
    /// </summary>
    internal bool TryAppendPersistedExtDeviceFaultSummary(
        StringBuilder builder,
        ExtDeviceFault extension,
        in VulkanDiagnosticOptions options,
        bool khrExposed,
        bool vendorBinarySupported)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(extension);
        VulkanDeviceFaultCapture capture = DeviceFaultFacility.CaptureExt(
            extension,
            Device,
            options,
            khrExposed,
            vendorBinarySupported);
        PersistDeviceFaultCapture(capture);
        capture.AppendSummary(builder);
        return true;
    }

    private static void PersistDeviceFaultCapture(VulkanDeviceFaultCapture capture)
    {
        foreach (ref readonly VulkanDeviceFaultArtifact artifact in capture.Artifacts)
        {
            try
            {
                if (artifact.IsBinary)
                {
                    string path = Path.Combine(
                        Debug.EnsureLogRunDirectory(),
                        artifact.FileName);
                    File.WriteAllBytes(path, artifact.Content);
                    continue;
                }

                Debug.WriteAuxiliaryLog(
                    artifact.FileName,
                    Encoding.UTF8.GetString(artifact.Content));
            }
            catch (Exception exception)
            {
                Debug.VulkanWarning(
                    "[VulkanDiag] Device-fault artifact persistence failed file={0} error={1}:{2}.",
                    artifact.FileName,
                    exception.GetType().Name,
                    exception.Message);
            }
        }
    }
}
