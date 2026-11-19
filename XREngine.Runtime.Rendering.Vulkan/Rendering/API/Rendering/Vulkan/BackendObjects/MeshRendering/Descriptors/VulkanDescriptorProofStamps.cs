using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Switches and counters for mesh descriptor proof stamps. A stamp lets a draw skip the
/// per-frame proof that its descriptors are unchanged (see <see cref="DescriptorProofStamp"/>).
/// <c>XRE_VULKAN_DESCRIPTOR_PROOF_STAMP=0</c> turns stamps off. <c>XRE_VULKAN_DESCRIPTOR_PROOF_STAMP_VERIFY=1</c>
/// runs the full proof on every stamp hit and counts each case where the full proof would have
/// written a descriptor or selected another allocation; that count must stay 0.
/// MCP can read <see cref="Describe"/> and change the switches at run time for A/B checks.
/// </summary>
public static class VulkanDescriptorProofStamps
{
    /// <summary>True when draws may reuse descriptors on a matching stamp.</summary>
    public static volatile bool Enabled =
        XREnvironment.IsEnabled(XREngineEnvironmentVariables.VulkanDescriptorProofStamp, defaultValue: true);

    /// <summary>True when every stamp hit also runs the full proof and compares the result.</summary>
    public static volatile bool Verify =
        XREnvironment.IsEnabled(XREngineEnvironmentVariables.VulkanDescriptorProofStampVerify);

    private static long _allocationHits;
    private static long _allocationMisses;
    private static long _frameSourceHits;
    private static long _frameSourceMisses;
    private static long _verifyChecks;
    private static long _verifyMismatches;

    internal static void RecordAllocationHit() => Interlocked.Increment(ref _allocationHits);
    internal static void RecordAllocationMiss() => Interlocked.Increment(ref _allocationMisses);
    internal static void RecordFrameSourceHit() => Interlocked.Increment(ref _frameSourceHits);
    internal static void RecordFrameSourceMiss() => Interlocked.Increment(ref _frameSourceMisses);

    internal static void RecordVerify(bool mismatch)
    {
        Interlocked.Increment(ref _verifyChecks);
        if (mismatch)
            Interlocked.Increment(ref _verifyMismatches);
    }

    /// <summary>Number of verify checks where the full proof disagreed with a stamp hit.</summary>
    public static long VerifyMismatches => Interlocked.Read(ref _verifyMismatches);

    /// <summary>Sets the switches; used by MCP for A/B runs in one session.</summary>
    public static string Set(bool enabled, bool verify)
    {
        Enabled = enabled;
        Verify = verify;
        return Describe();
    }

    /// <summary>Clears the counters.</summary>
    public static string Reset()
    {
        Interlocked.Exchange(ref _allocationHits, 0);
        Interlocked.Exchange(ref _allocationMisses, 0);
        Interlocked.Exchange(ref _frameSourceHits, 0);
        Interlocked.Exchange(ref _frameSourceMisses, 0);
        Interlocked.Exchange(ref _verifyChecks, 0);
        Interlocked.Exchange(ref _verifyMismatches, 0);
        return Describe();
    }

    /// <summary>Returns the switches and counters as one line.</summary>
    public static string Describe()
        => $"enabled={Enabled} verify={Verify} allocationHits={Interlocked.Read(ref _allocationHits)} " +
           $"allocationMisses={Interlocked.Read(ref _allocationMisses)} frameSourceHits={Interlocked.Read(ref _frameSourceHits)} " +
           $"frameSourceMisses={Interlocked.Read(ref _frameSourceMisses)} verifyChecks={Interlocked.Read(ref _verifyChecks)} " +
           $"verifyMismatches={Interlocked.Read(ref _verifyMismatches)}";
}
