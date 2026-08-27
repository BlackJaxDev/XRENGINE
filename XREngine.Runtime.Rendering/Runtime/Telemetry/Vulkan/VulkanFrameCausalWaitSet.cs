using System;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Fixed-capacity per-frame wait payload. Explicit fields keep successful-frame
/// capture allocation-free and make publication a bounded value copy.
/// </summary>
public struct VulkanFrameCausalWaitSet
{
    public const int Capacity = 12;

    private VulkanFrameCausalWait _wait0;
    private VulkanFrameCausalWait _wait1;
    private VulkanFrameCausalWait _wait2;
    private VulkanFrameCausalWait _wait3;
    private VulkanFrameCausalWait _wait4;
    private VulkanFrameCausalWait _wait5;
    private VulkanFrameCausalWait _wait6;
    private VulkanFrameCausalWait _wait7;
    private VulkanFrameCausalWait _wait8;
    private VulkanFrameCausalWait _wait9;
    private VulkanFrameCausalWait _wait10;
    private VulkanFrameCausalWait _wait11;

    public int Count { get; private set; }
    public int DroppedCount { get; private set; }

    public void Add(in VulkanFrameCausalWait wait)
    {
        if (!wait.IsValid)
            return;

        switch (Count)
        {
            case 0: _wait0 = wait; break;
            case 1: _wait1 = wait; break;
            case 2: _wait2 = wait; break;
            case 3: _wait3 = wait; break;
            case 4: _wait4 = wait; break;
            case 5: _wait5 = wait; break;
            case 6: _wait6 = wait; break;
            case 7: _wait7 = wait; break;
            case 8: _wait8 = wait; break;
            case 9: _wait9 = wait; break;
            case 10: _wait10 = wait; break;
            case 11: _wait11 = wait; break;
            default:
                DroppedCount++;
                return;
        }

        Count++;
    }

    /// <summary>
    /// Carries overflow from another fixed-capacity collector into the published
    /// set without manufacturing placeholder wait records.
    /// </summary>
    public void AddDropped(int count)
    {
        if (count > 0)
            DroppedCount += count;
    }

    public readonly VulkanFrameCausalWait Get(int index)
        => index switch
        {
            0 when index < Count => _wait0,
            1 when index < Count => _wait1,
            2 when index < Count => _wait2,
            3 when index < Count => _wait3,
            4 when index < Count => _wait4,
            5 when index < Count => _wait5,
            6 when index < Count => _wait6,
            7 when index < Count => _wait7,
            8 when index < Count => _wait8,
            9 when index < Count => _wait9,
            10 when index < Count => _wait10,
            11 when index < Count => _wait11,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };
}
