using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuFrameBuffer
{
    private const int MaximumColorResolves = 32;
    private readonly List<WebGpuColorResolve> _colorResolves = new(4);

    internal int GetSingleDrawColorSlot()
    {
        if (Data.DrawBuffers is not { Length: 1 } buffers ||
            buffers[0] is < EDrawBuffersAttachment.ColorAttachment0 or > EDrawBuffersAttachment.ColorAttachment7)
            throw Unsupported("Resolve", "an implicit destination requires exactly one enabled color draw buffer");
        return (int)buffers[0] - (int)EDrawBuffersAttachment.ColorAttachment0;
    }

    internal (AbstractRenderAPIObject Owner, int View, string Format) GetColorAttachment(int slot)
    {
        if ((uint)slot >= 8u)
            throw Unsupported("Resolve", "the color attachment slot must be between zero and seven");
        for (int i = 0; i < _targets.Length; i++)
            if (ColorSlot(_targets[i].Attachment) == slot)
                return (_textures[i], _views[i], _colorFormats[slot]!);
        throw Unsupported("Resolve", "the selected color attachment is not present");
    }

    internal int GetColorResolveCommand(WebGpuFrameBuffer destination, int sourceSlot, int destinationSlot)
    {
        var source = GetColorAttachment(sourceSlot);
        var target = destination.GetColorAttachment(destinationSlot);
        if (SampleCount != 4 || destination.SampleCount != 1 || Width != destination.Width ||
            Height != destination.Height || source.Format != target.Format || ReferenceEquals(source.Owner, target.Owner))
            throw Unsupported("Resolve", "distinct same-format, same-extent color attachments with four source samples and one destination sample are required");

        for (int i = 0; i < _colorResolves.Count; i++)
        {
            WebGpuColorResolve resolve = _colorResolves[i];
            if (!ReferenceEquals(resolve.Destination, destination) || resolve.SourceSlot != sourceSlot ||
                resolve.DestinationSlot != destinationSlot) continue;
            if (resolve.DestinationRevision == destination.Revision) return resolve.Command;
            Renderer.RetireEngineResourceAfterFrame(resolve.Command);
            _colorResolves.RemoveAt(i);
            break;
        }
        if (_colorResolves.Count == MaximumColorResolves)
            throw Unsupported("ResolveCapacity", "a framebuffer may retain at most thirty-two distinct color resolve routes");

        // WebGPU resolves at pass end even without draws. Load preserves the producing
        // pass's samples; store preserves them for later consumers or repeated resolves.
        // The destination is the resolve target, never an independently loaded attachment.
        BrowserFrameBufferPlan plan = new([
            new BrowserColorAttachmentPlan(source.View, clear: false, store: true, default,
                resolveTargetHandle: target.View)]);
        int command = Renderer.PrepareCommands(
            "{\"label\":\"Engine color MSAA resolve\",\"commands\":[{\"type\":\"clear\",\"pass\":" + plan.ToJson() + "}]}");
        _colorResolves.Add(new(destination, destination.Revision, sourceSlot, destinationSlot,
            source.Owner, target.Owner, command));
        return command;
    }

    internal void ReleaseColorResolvesUsing(AbstractRenderAPIObject resource)
    {
        for (int i = _colorResolves.Count - 1; i >= 0; i--)
        {
            WebGpuColorResolve resolve = _colorResolves[i];
            if (!ReferenceEquals(this, resource) && !ReferenceEquals(resolve.Destination, resource) &&
                !ReferenceEquals(resolve.SourceOwner, resource) && !ReferenceEquals(resolve.DestinationOwner, resource))
                continue;
            Renderer.RetireEngineResourceAfterFrame(resolve.Command);
            _colorResolves.RemoveAt(i);
        }
    }

    internal static void MarkAttachmentRecorded(AbstractRenderAPIObject attachment)
    {
        switch (attachment)
        {
            case WebGpuRenderBuffer renderbuffer: renderbuffer.MarkRecorded(); break;
            case WebGpuTexture2D texture: texture.MarkRecorded(); break;
            case WebGpuTexture2DArray array: array.MarkRecorded(); break;
            case WebGpuTextureCube cube: cube.MarkRecorded(); break;
            default: throw Unsupported("Resolve", "the attachment cannot track recorded GPU use");
        }
    }
}
