using Silk.NET.Vulkan;
using XREngine.Rendering.Vulkan;

namespace XREngine.RenderBench;

/// <summary>
/// Allocation-free synthetic camera/animation/random input used by the Phase 2
/// control fixture. The clear value is its deterministic rendered projection.
/// </summary>
public sealed class RenderBenchDeterministicInputs
{
    private readonly RenderBenchOptions _options;
    private readonly float _seedRed;
    private readonly float _seedGreen;
    private readonly float _seedBlue;
    private ClearColorValue _clear;

    public RenderBenchDeterministicInputs(RenderBenchOptions options)
    {
        _options = options;
        uint random = unchecked((uint)options.RandomSeed);
        _seedRed = NextUnit(ref random) * 0.5f + 0.125f;
        _seedGreen = NextUnit(ref random) * 0.5f + 0.125f;
        _seedBlue = NextUnit(ref random) * 0.5f + 0.125f;
        Advance(0);
    }

    public double SimulationTimeSeconds { get; private set; }

    public void Advance(int frameIndex)
    {
        SimulationTimeSeconds = _options.FrozenWorld ? 0.0 : frameIndex * _options.FixedStepSeconds;
        float animation = _options.FrozenWorld
            ? 0.0f
            : (float)((Math.Sin(SimulationTimeSeconds * Math.Tau) + 1.0) * 0.0625);
        _clear = new ClearColorValue(
            Math.Clamp(_seedRed + animation, 0.0f, 1.0f),
            Math.Clamp(_seedGreen + animation, 0.0f, 1.0f),
            Math.Clamp(_seedBlue + animation, 0.0f, 1.0f),
            1.0f);
    }

    public RenderBenchInputManifest CaptureManifest()
        => new(
            WorldLoaded: false,
            WorldIdentity: "synthetic-clear:no-world",
            CameraIdentity: "synthetic-fixed-camera:identity",
            AnimationIdentity: _options.FrozenWorld ? "frozen" : "fixed-step-sine",
            SimulationTimeSeconds,
            _options.FixedStepSeconds,
            _options.RandomSeed,
            _options.FrozenWorld);

    public unsafe void RecordFrame(
        Vk api,
        CommandBuffer commandBuffer,
        VulkanRenderFrameTarget target)
    {
        ImageMemoryBarrier toTransferDestination = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = target.InitialColorLayout,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcAccessMask = target.InitialColorLayout == ImageLayout.Undefined ? 0 : AccessFlags.TransferReadBit,
            DstAccessMask = AccessFlags.TransferWriteBit,
            Image = target.ColorImage,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, target.Layers),
        };
        api.CmdPipelineBarrier(
            commandBuffer,
            target.InitialColorLayout == ImageLayout.Undefined ? PipelineStageFlags.TopOfPipeBit : PipelineStageFlags.TransferBit,
            PipelineStageFlags.TransferBit,
            0,
            0,
            null,
            0,
            null,
            1,
            in toTransferDestination);

        ImageSubresourceRange range = toTransferDestination.SubresourceRange;
        api.CmdClearColorImage(commandBuffer, target.ColorImage, ImageLayout.TransferDstOptimal, in _clear, 1, in range);

        ImageMemoryBarrier toFinal = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = target.RequiredFinalColorLayout,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = target.RequiredFinalColorLayout == ImageLayout.TransferSrcOptimal ? AccessFlags.TransferReadBit : 0,
            Image = target.ColorImage,
            SubresourceRange = range,
        };
        api.CmdPipelineBarrier(
            commandBuffer,
            PipelineStageFlags.TransferBit,
            target.RequiredFinalColorLayout == ImageLayout.TransferSrcOptimal ? PipelineStageFlags.TransferBit : PipelineStageFlags.BottomOfPipeBit,
            0,
            0,
            null,
            0,
            null,
            1,
            in toFinal);
    }

    private static float NextUnit(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return (state & 0x00FFFFFFu) / 16777215.0f;
    }
}
