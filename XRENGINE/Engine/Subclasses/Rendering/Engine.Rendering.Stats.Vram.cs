using System;
using System.Threading;
using XREngine.Data.Rendering;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            public static partial class Stats
            {
                public static class Vram
                {
                    // VRAM tracking fields
                    private static long _allocatedVRAMBytes;
                    private static long _allocatedBufferBytes;
                    private static long _allocatedTextureBytes;
                    private static long _allocatedRenderBufferBytes;

                    // FBO bandwidth tracking fields (per-frame)
                    private static long _fboBandwidthBytes;
                    private static int _fboBindCount;
                    private static long _lastFrameFBOBandwidthBytes;
                    private static int _lastFrameFBOBindCount;

                    /// <summary>
                    /// Total currently allocated GPU VRAM in bytes.
                    /// </summary>
                    public static long AllocatedVRAMBytes => Interlocked.Read(ref _allocatedVRAMBytes);

                    /// <summary>
                    /// Currently allocated GPU buffer memory in bytes.
                    /// </summary>
                    public static long AllocatedBufferBytes => Interlocked.Read(ref _allocatedBufferBytes);

                    /// <summary>
                    /// Currently allocated GPU texture memory in bytes.
                    /// </summary>
                    public static long AllocatedTextureBytes => Interlocked.Read(ref _allocatedTextureBytes);

                    /// <summary>
                    /// Currently allocated GPU render buffer memory in bytes.
                    /// </summary>
                    public static long AllocatedRenderBufferBytes => Interlocked.Read(ref _allocatedRenderBufferBytes);

                    /// <summary>
                    /// Total currently allocated GPU VRAM in megabytes.
                    /// </summary>
                    public static double AllocatedVRAMMB => AllocatedVRAMBytes / (1024.0 * 1024.0);

                    /// <summary>
                    /// Configured VRAM budget in bytes. Returns long.MaxValue when budgeting is disabled.
                    /// </summary>
                    public static long VramBudgetBytes
                        => Engine.Rendering.Settings.EnableVramBudget
                            ? Math.Max(1L, (long)Engine.Rendering.Settings.VramBudgetMB) * 1024L * 1024L
                            : long.MaxValue;

                    /// <summary>
                    /// Determines whether a tracked GPU allocation would fit inside the configured VRAM budget.
                    /// </summary>
                    public static bool CanAllocateVram(long requestedBytes, long existingAllocationBytes, out long projectedBytes, out long budgetBytes)
                    {
                        budgetBytes = VramBudgetBytes;
                        long currentBytes = AllocatedVRAMBytes;
                        long retainedBytes = Math.Max(0L, existingAllocationBytes);
                        projectedBytes = Math.Max(0L, currentBytes - retainedBytes) + Math.Max(0L, requestedBytes);
                        return projectedBytes <= budgetBytes;
                    }

                    /// <summary>
                    /// Total FBO render bandwidth in bytes for the last completed frame.
                    /// This represents the total size of all render targets written to during rendering.
                    /// </summary>
                    public static long FBOBandwidthBytes => _lastFrameFBOBandwidthBytes;

                    /// <summary>
                    /// Total FBO render bandwidth in megabytes for the last completed frame.
                    /// </summary>
                    public static double FBOBandwidthMB => _lastFrameFBOBandwidthBytes / (1024.0 * 1024.0);

                    /// <summary>
                    /// Number of times FBOs were bound for writing in the last completed frame.
                    /// </summary>
                    public static int FBOBindCount => _lastFrameFBOBindCount;

                    internal static void SnapshotAndReset()
                    {
                        _lastFrameFBOBandwidthBytes = Interlocked.Exchange(ref _fboBandwidthBytes, 0);
                        _lastFrameFBOBindCount = Interlocked.Exchange(ref _fboBindCount, 0);
                    }

                    /// <summary>
                    /// Record a GPU buffer memory allocation.
                    /// </summary>
                    /// <param name="bytes">The number of bytes allocated.</param>
                    public static void AddBufferAllocation(long bytes)
                    {
                        if (bytes <= 0) return;
                        Interlocked.Add(ref _allocatedBufferBytes, bytes);
                        Interlocked.Add(ref _allocatedVRAMBytes, bytes);
                    }

                    /// <summary>
                    /// Record a GPU buffer memory deallocation.
                    /// </summary>
                    /// <param name="bytes">The number of bytes deallocated.</param>
                    public static void RemoveBufferAllocation(long bytes)
                    {
                        if (bytes <= 0) return;
                        Interlocked.Add(ref _allocatedBufferBytes, -bytes);
                        Interlocked.Add(ref _allocatedVRAMBytes, -bytes);
                    }

                    /// <summary>
                    /// Record a GPU texture memory allocation.
                    /// </summary>
                    /// <param name="bytes">The number of bytes allocated.</param>
                    public static void AddTextureAllocation(long bytes)
                    {
                        if (bytes <= 0) return;
                        Interlocked.Add(ref _allocatedTextureBytes, bytes);
                        Interlocked.Add(ref _allocatedVRAMBytes, bytes);
                    }

                    /// <summary>
                    /// Record a GPU texture memory deallocation.
                    /// </summary>
                    /// <param name="bytes">The number of bytes deallocated.</param>
                    public static void RemoveTextureAllocation(long bytes)
                    {
                        if (bytes <= 0) return;
                        Interlocked.Add(ref _allocatedTextureBytes, -bytes);
                        Interlocked.Add(ref _allocatedVRAMBytes, -bytes);
                    }

                    /// <summary>
                    /// Record a GPU render buffer memory allocation.
                    /// </summary>
                    /// <param name="bytes">The number of bytes allocated.</param>
                    public static void AddRenderBufferAllocation(long bytes)
                    {
                        if (bytes <= 0) return;
                        Interlocked.Add(ref _allocatedRenderBufferBytes, bytes);
                        Interlocked.Add(ref _allocatedVRAMBytes, bytes);
                    }

                    /// <summary>
                    /// Record a GPU render buffer memory deallocation.
                    /// </summary>
                    /// <param name="bytes">The number of bytes deallocated.</param>
                    public static void RemoveRenderBufferAllocation(long bytes)
                    {
                        if (bytes <= 0) return;
                        Interlocked.Add(ref _allocatedRenderBufferBytes, -bytes);
                        Interlocked.Add(ref _allocatedVRAMBytes, -bytes);
                    }

                    /// <summary>
                    /// Record FBO render bandwidth when an FBO is bound for writing.
                    /// The bandwidth is calculated as the total size of all render target attachments.
                    /// </summary>
                    /// <param name="bytes">The total size of all render target attachments in bytes.</param>
                    public static void AddFBOBandwidth(long bytes)
                    {
                        if (bytes <= 0) return;
                        Interlocked.Add(ref _fboBandwidthBytes, bytes);
                        Interlocked.Increment(ref _fboBindCount);
                    }
                }
            }
        }
    }
}
