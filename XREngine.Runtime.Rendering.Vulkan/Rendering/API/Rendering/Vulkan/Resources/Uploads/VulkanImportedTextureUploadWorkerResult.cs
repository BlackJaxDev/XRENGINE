using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using Buffer = Silk.NET.Vulkan.Buffer;
using Format = Silk.NET.Vulkan.Format;
using Image = Silk.NET.Vulkan.Image;

namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanImportedTextureUploadWorkerResult(
    VulkanImportedTexturePendingUpload? pendingUpload,
    string? failureReason,
    bool canceled,
    double prepMilliseconds,
    Exception? exception)
{
    public VulkanImportedTexturePendingUpload? PendingUpload { get; } = pendingUpload;
    public string? FailureReason { get; } = failureReason;
    public bool Canceled { get; } = canceled;
    public double PrepMilliseconds { get; } = prepMilliseconds;
    public Exception? Exception { get; } = exception;
}

