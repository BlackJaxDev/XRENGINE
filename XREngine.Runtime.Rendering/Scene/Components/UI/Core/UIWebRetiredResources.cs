namespace XREngine.Rendering.UI;

/// <summary>Exact backend and upload buffers detached from one web lifetime.</summary>
internal sealed class UIWebRetiredResources(
    IWebRendererBackend backend,
    XRDataBuffer<byte>?[] buffers,
    long generation)
{
    private static readonly object QuarantineGate = new();
    private static readonly HashSet<UIWebRetiredResources> QuarantineRoots = [];

    public IWebRendererBackend Backend { get; } = backend;
    public XRDataBuffer<byte>?[] Buffers { get; } = buffers;
    public long Generation { get; } = generation;
    public bool IsQueued { get; set; }
    public bool IsClaimed { get; set; }
    public bool BackendDisposed { get; set; }
    public bool BuffersDisposed { get; set; }
    public bool IsQuarantined { get; set; }
    public string? Failure { get; set; }

    public void Quarantine()
    {
        lock (QuarantineGate)
            QuarantineRoots.Add(this);
        IsQuarantined = true;
    }
}
