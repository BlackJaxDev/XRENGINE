using System.Text;
using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Counts accepted buffer retirements by their existing owner label during a diagnostic run.
/// </summary>
internal static class VulkanBufferRetirementDiagnostics
{
    private const int ReportInterval = 256;
    private const int CategoryCount = 9;
    internal static bool Enabled { get; } = IsEnabled();
    private static readonly long[] Counts = Enabled ? new long[CategoryCount] : [];
    private static readonly long[] Bytes = Enabled ? new long[CategoryCount] : [];
    private static long _acceptedSinceReport;
    private static string? _otherOwnerSample;

    internal static void Record(string owner, ulong allocationBytes)
    {
        if (!Enabled)
            return;

        int category = Classify(owner);
        Interlocked.Increment(ref Counts[category]);
        Interlocked.Add(ref Bytes[category],
            allocationBytes > long.MaxValue ? long.MaxValue : (long)allocationBytes);
        if (category == 8)
            Interlocked.CompareExchange(ref _otherOwnerSample, owner, null);

        if (Interlocked.Increment(ref _acceptedSinceReport) < ReportInterval ||
            Interlocked.Exchange(ref _acceptedSinceReport, 0) < ReportInterval)
            return;

        StringBuilder report = new(320);
        report.Append("acceptedBufferRetirements");
        for (int index = 0; index < CategoryCount; index++)
        {
            long count = Interlocked.Exchange(ref Counts[index], 0);
            long bytes = Interlocked.Exchange(ref Bytes[index], 0);
            report.Append(' ').Append(CategoryName(index)).Append('=').Append(count)
                .Append('/').Append(bytes).Append('B');
        }

        string? otherOwnerSample = Interlocked.Exchange(ref _otherOwnerSample, null);
        if (otherOwnerSample is not null)
            report.Append(" otherSample='").Append(otherOwnerSample).Append("'");
        Debug.WriteAuxiliaryLog("vulkan-buffer-retirement-diagnostics.log", report.ToString());
    }

    private static bool IsEnabled()
    {
        string? setting = Environment.GetEnvironmentVariable(
            "XRE_VULKAN_BUFFER_RETIREMENT_DIAGNOSTICS");
        return string.Equals(setting, "1", StringComparison.Ordinal) ||
               string.Equals(setting, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static int Classify(string owner)
    {
        if (owner.StartsWith("ResourcePlanner.", StringComparison.Ordinal))
            return 0;
        if (owner.StartsWith("VkDataBuffer.PushData", StringComparison.Ordinal))
            return 1;
        if (owner.StartsWith("VkDataBuffer.DeleteObjectInternal", StringComparison.Ordinal))
            return 2;
        if (owner.StartsWith("CommandRuntime.ComputeTransient", StringComparison.Ordinal))
            return 3;
        if (owner.StartsWith("VkRenderProgram.ComputeUniformBuffer", StringComparison.Ordinal))
            return 4;
        if (owner.StartsWith("VulkanTextureUploadService.", StringComparison.Ordinal) ||
            owner.StartsWith("VkImageBackedTexture.ImportedUpload", StringComparison.Ordinal))
            return 5;
        if (owner.StartsWith("ImGui.", StringComparison.Ordinal))
            return 6;
        if (owner.StartsWith("AdvancedGeometry.", StringComparison.Ordinal))
            return 7;
        return 8;
    }

    private static string CategoryName(int category) => category switch
    {
        0 => "planner",
        1 => "dataPush",
        2 => "dataDelete",
        3 => "computeTransient",
        4 => "computeUniform",
        5 => "textureStaging",
        6 => "imgui",
        7 => "advancedGeometry",
        _ => "other",
    };
}
