namespace XREngine.Rendering.WebGPU;

/// <summary>Owns one immutable physical descriptor until the executor returns its real generation handle.</summary>
internal sealed class WebGpuResourceRequest(object owner, long ownerGeneration, int identity, int kind, object descriptor, string json)
{
    internal object Owner { get; } = owner;
    internal long OwnerGeneration { get; } = ownerGeneration;
    internal int Identity { get; } = identity;
    internal int Kind { get; } = kind;
    internal object Descriptor { get; } = descriptor;
    internal string Json { get; } = json;
    internal int[] Dependencies { get; set; } = [];
    internal WebGpuResourceRequestState State { get; set; }
    internal int Handle { get; set; }
    internal string? Failure { get; set; }
    internal List<WebGpuResourceUploadSnapshot>? Uploads { get; set; }
    internal bool Claimed { get; set; }
    internal TaskCompletionSource<int>? Completion { get; set; }
}
