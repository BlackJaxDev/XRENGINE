namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private sealed class FrameOpCapture
    {
        public FrameOpCapture? Previous { get; private set; }
        public bool ExcludeTextureUploads { get; private set; }
        public FrameOp[] Buffer { get; private set; } = new FrameOp[256];
        public int Count { get; private set; }

        public void Begin(FrameOpCapture? previous, bool excludeTextureUploads)
        {
            Previous = previous;
            ExcludeTextureUploads = excludeTextureUploads;
            Count = 0;
        }

        public void Add(FrameOp op)
        {
            int count = Count;
            if ((uint)count >= (uint)Buffer.Length)
                Grow();

            Buffer[count] = op;
            Count = count + 1;
        }

        private void Grow()
        {
            int newLength = Buffer.Length == 0 ? 256 : Buffer.Length * 2;
            FrameOp[] grown = new FrameOp[newLength];
            Array.Copy(Buffer, grown, Count);
            Buffer = grown;
        }
    }
}
