using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private const int EngineResourceMaximumRequests = 4096;
    private const int EngineResourceReceiptBytes = 272;
    private readonly List<WebGpuResourceRequest> _engineResourceRequests = new(64);
    private readonly List<WebGpuResourceRequest> _engineResourceBatch = new(64);
    private readonly List<int> _engineResourceAcknowledgements = new(64);
    private readonly byte[] _engineResourceReceipts = new byte[EngineResourceMaximumRequests * EngineResourceReceiptBytes];
    private int _engineResourceIdentity;
    private int _engineResourceSnapshotBytes;
    private string _engineResourceDescriptions = "[]";
    private bool _engineResourceBatchPrepared;

    internal WebGpuResourceRequest RequestEngineResource(object owner, int kind, object descriptor, bool reuse = true)
    {
        RequireReady();
        if (descriptor is BrowserTextureDescription texture) ValidateTextureQuality(texture);
        if (owner is AbstractRenderAPIObject api && (api.IsRetired || api.OwnerGeneration != BackendGeneration))
            throw new InvalidOperationException("WebGPU.Resource.OwnerObsolete: physical preparation requires a live owner generation.");
        foreach (WebGpuResourceRequest request in _engineResourceRequests)
            if (reuse && ReferenceEquals(request.Owner, owner) && request.Kind == kind && request.Descriptor.Equals(descriptor) &&
                request.State != WebGpuResourceRequestState.Cancelled && !request.Claimed)
                return request;
        if (_engineResourceRequests.Count >= EngineResourceMaximumRequests || _engineResourceIdentity == int.MaxValue)
            throw new InvalidOperationException("WebGPU.Resource.RequestCapacity: retire obsolete owners before requesting more physical resources.");
        WebGpuResourceRequest created = new(owner, BackendGeneration, ++_engineResourceIdentity, kind, descriptor,
            DescribeEngineResource(kind, descriptor));
        long queuedCharacters = created.Json.Length;
        foreach (WebGpuResourceRequest request in _engineResourceRequests)
            if (request.State == WebGpuResourceRequestState.Queued) queuedCharacters += request.Json.Length;
        if (queuedCharacters > 15 * 1024 * 1024)
            throw new InvalidOperationException("WebGPU.Resource.DescriptorCapacity: pending physical descriptions exceed the bounded 15 MiB request journal.");
        created.Dependencies = DescribeEngineResourceDependencies(kind, descriptor);
        foreach (int dependency in created.Dependencies) RequireOwnedResource(dependency);
        _engineResourceRequests.Add(created);
        return created;
    }

    internal int RequireEngineResource(WebGpuResourceRequest request, bool claim = true)
    {
        if (request.State == WebGpuResourceRequestState.Ready)
        {
            RequireOwnedResource(request.Handle);
            if (claim) ClaimEngineResource(request);
            return request.Handle;
        }
        if (request.State == WebGpuResourceRequestState.Failed)
            throw new InvalidOperationException($"WebGPU.Resource.CreationFailed: {request.Failure}");
        if (request.State == WebGpuResourceRequestState.Cancelled || request.OwnerGeneration != BackendGeneration)
            throw new InvalidOperationException("WebGPU.Resource.RequestCancelled: the descriptor owner retired before publication.");
        throw new WebGpuResourcePreparationPendingException("WebGPU.Resource.CreationPending: physical storage is awaiting the engine acceptance receipt.");
    }

    internal void ClaimEngineResource(WebGpuResourceRequest request)
    {
        if (request.State != WebGpuResourceRequestState.Ready)
            throw new InvalidOperationException("WebGPU.Resource.PublicationPending: only a ready physical receipt can be published.");
        request.Claimed = true;
        _engineResourceRequests.Remove(request);
    }

    private async Task<int> AwaitEngineResource(WebGpuResourceRequest request)
    {
        if (request.State is WebGpuResourceRequestState.Queued or WebGpuResourceRequestState.Submitted)
            await (request.Completion ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        return RequireEngineResource(request);
    }

    internal int CreateEngineReplacement(object owner, ref WebGpuResourceRequest? retained, int kind, object descriptor)
    {
        if (retained is { } previous && !previous.Descriptor.Equals(descriptor))
        {
            CancelEngineResourceRequest(previous);
            retained = null;
        }
        retained ??= RequestEngineResource(owner, kind, descriptor);
        int handle = RequireEngineResource(retained);
        retained = null;
        return handle;
    }

    internal int CreateEngineBuffer(object owner, BrowserBufferDescription descriptor)
        => RequireEngineResource(RequestEngineResource(owner, 1, descriptor));
    internal int CreateEngineTexture(object owner, BrowserTextureDescription descriptor)
        => RequireEngineResource(RequestEngineResource(owner, 2, descriptor));
    internal int CreateEngineTextureView(object owner, BrowserTextureViewDescription descriptor)
        => RequireEngineResource(RequestEngineResource(owner, 3, descriptor));
    internal int CreateEngineSampler(object owner, BrowserSamplerDescription descriptor)
        => RequireEngineResource(RequestEngineResource(owner, 4, descriptor));
    internal int CreateEngineBindingGroup(object owner, string descriptor)
        => RequireEngineResource(RequestEngineResource(owner, 6, descriptor));
    internal int PrepareEngineCommands(object owner, string descriptor)
        => RequireEngineResource(RequestEngineResource(owner, 7, descriptor));
    internal Task<int> CreateEngineBufferAsync(object owner, BrowserBufferDescription descriptor)
        => AwaitEngineResource(RequestEngineResource(owner, 1, descriptor));
    internal Task<int> CreateEngineBindingLayoutAsync(object owner, string descriptor)
        => AwaitEngineResource(RequestEngineResource(owner, 5, descriptor));
    internal Task<int> CreateEngineBindingGroupAsync(object owner, string descriptor)
        => AwaitEngineResource(RequestEngineResource(owner, 6, descriptor));
    internal Task<int> PrepareEngineCommandsAsync(object owner, string descriptor)
        => AwaitEngineResource(RequestEngineResource(owner, 7, descriptor));

    /// <summary>Cancels unclaimed candidates; physical handles already published remain their wrapper's responsibility.</summary>
    internal void CancelEngineResourceRequests(object owner)
    {
        for (int index = _engineResourceRequests.Count - 1; index >= 0; index--)
        {
            if (index >= _engineResourceRequests.Count) continue;
            WebGpuResourceRequest request = _engineResourceRequests[index];
            if (!ReferenceEquals(request.Owner, owner)) continue;
            CancelEngineResourceRequest(request);
        }
    }

    internal void CancelEngineResourceRequest(WebGpuResourceRequest request)
    {
        if (request.State == WebGpuResourceRequestState.Cancelled) return;
        WebGpuResourceRequestState previous = request.State;
        request.State = WebGpuResourceRequestState.Cancelled;
        if (previous is WebGpuResourceRequestState.Ready or WebGpuResourceRequestState.Failed)
            _engineResourceRequests.Remove(request);
        if (previous == WebGpuResourceRequestState.Ready && !request.Claimed)
            RetireEngineResourceAfterFrame(request.Handle);
        ReleaseEngineResourceSnapshots(request);
        request.Completion?.TrySetCanceled();
        // A batch may already have reached JavaScript despite a thrown receipt import.
        if (previous == WebGpuResourceRequestState.Queued && !_engineResourceBatch.Contains(request)) _engineResourceRequests.Remove(request);
    }

    internal void ReplaceEngineResourceBufferImage(WebGpuResourceRequest request, ReadOnlySpan<byte> bytes)
    {
        if (request.Kind != 1 || request.State is not (WebGpuResourceRequestState.Queued or WebGpuResourceRequestState.Submitted or WebGpuResourceRequestState.Ready))
            throw new InvalidOperationException("WebGPU.Resource.SnapshotOwner: only an unpublished buffer image can be replaced.");
        int retainedLength = checked((bytes.Length + 3) & ~3);
        int previousLength = 0;
        if (request.Uploads is { } previous)
            foreach (WebGpuResourceUploadSnapshot upload in previous) previousLength += upload.Bytes.Length;
        if (retainedLength <= 0 || retainedLength > EnginePreparationCapacity - _engineResourceSnapshotBytes + previousLength)
            throw new InvalidOperationException("WebGPU.Resource.SnapshotCapacity: replacement initial images exceed the retained creation budget.");
        byte[] snapshot = new byte[retainedLength];
        bytes.CopyTo(snapshot);
        List<WebGpuResourceUploadSnapshot> replacement = [new(-1, 0, 0, 0, snapshot)];
        ReleaseEngineResourceSnapshots(request);
        request.Uploads = replacement;
        _engineResourceSnapshotBytes += retainedLength;
    }

    internal void CaptureEngineResourceUpload(WebGpuResourceRequest request, ReadOnlySpan<byte> bytes,
        int mip = -1, int layer = 0, int width = 0, int height = 0, int bufferOffset = 0)
    {
        int retainedLength = mip < 0 ? checked((bytes.Length + 3) & ~3) : bytes.Length;
        if (request.State is not (WebGpuResourceRequestState.Queued or WebGpuResourceRequestState.Submitted or WebGpuResourceRequestState.Ready) || request.Claimed || bytes.Length == 0 ||
            retainedLength > EnginePreparationCapacity - _engineResourceSnapshotBytes || (request.Uploads?.Count ?? 0) >= EngineMaximumPreparations)
            throw new InvalidOperationException("WebGPU.Resource.SnapshotCapacity: exact initial images exceed the retained 256 MiB creation budget.");
        byte[] snapshot = new byte[retainedLength];
        bytes.CopyTo(snapshot);
        (request.Uploads ??= []).Add(new(mip, layer, width, height, snapshot, bufferOffset));
        _engineResourceSnapshotBytes += snapshot.Length;
    }

    internal void StageEngineResourceInitialUploads(WebGpuResourceRequest request, int handle)
    {
        if (request.Uploads is not { } uploads) return;
        int size = 0;
        foreach (WebGpuResourceUploadSnapshot upload in uploads) size = checked(size + ((upload.Bytes.Length + 3) & ~3));
        if (uploads.Count > EngineMaximumPreparations - _enginePreparationCount || size > EnginePreparationCapacity - _enginePreparationBytes)
            throw new InvalidOperationException("WebGPU.Preparation.Capacity: initial images exceed the retained preparation journal.");
        foreach (WebGpuResourceUploadSnapshot upload in uploads)
            if (upload.Mip < 0) StageEngineBufferPreparation(handle, upload.BufferOffset, upload.Bytes);
            else StageEngineTextureLayerMip(handle, upload.Mip, upload.Layer, upload.Width, upload.Height, upload.Bytes);
        ReleaseEngineResourceSnapshots(request);
    }

    private void ReleaseEngineResourceSnapshots(WebGpuResourceRequest request)
    {
        if (request.Uploads is not { } uploads) return;
        foreach (WebGpuResourceUploadSnapshot upload in uploads) _engineResourceSnapshotBytes -= upload.Bytes.Length;
        request.Uploads = null;
    }

    private void CancelEngineResourceDependents(int handle)
    {
        for (int index = _engineResourceRequests.Count - 1; index >= 0; index--)
        {
            WebGpuResourceRequest request = _engineResourceRequests[index];
            if (!request.Claimed && Array.IndexOf(request.Dependencies, handle) >= 0)
                CancelEngineResourceRequest(request);
        }
    }

    private static int[] DescribeEngineResourceDependencies(int kind, object descriptor)
    {
        if (descriptor is BrowserTextureViewDescription view) return [view.TextureHandle];
        if (kind is not (6 or 7) || descriptor is not string json) return [];
        using JsonDocument document = JsonDocument.Parse(json);
        List<int> handles = [];
        Collect(document.RootElement, handles);
        return handles.ToArray();

        static void Collect(JsonElement element, List<int> handles)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray()) Collect(item, handles);
            }
            else if (element.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty property in element.EnumerateObject())
                    if (property.Value.ValueKind == JsonValueKind.Number && property.Name is
                        ("layout" or "resource" or "pipeline" or "buffer" or "source" or "destination" or "viewHandle" or "resolveTargetHandle") ||
                        property.Value.ValueKind == JsonValueKind.Number && property.Name == "group" && element.TryGetProperty("index", out _))
                    {
                        int handle = property.Value.GetInt32();
                        if (handle > 0 && !handles.Contains(handle)) handles.Add(handle);
                    }
                    else Collect(property.Value, handles);
        }
    }

    private void ForgetEngineResourceHandle(int handle)
    {
        for (int index = _engineResourceRequests.Count - 1; index >= 0; index--)
            if (_engineResourceRequests[index].Handle == handle)
            {
                ReleaseEngineResourceSnapshots(_engineResourceRequests[index]);
                _engineResourceRequests.RemoveAt(index);
            }
    }

    private void PrepareEngineResourceBatch()
    {
        if (_engineResourceBatchPrepared) return;
        _engineResourceBatch.Clear();
        ArrayBufferWriter<byte>? output = null;
        Utf8JsonWriter? writer = null;
        try
        {
            foreach (WebGpuResourceRequest request in _engineResourceRequests)
            {
                if (request.State is WebGpuResourceRequestState.Ready or WebGpuResourceRequestState.Failed) continue;
                if (request.State is WebGpuResourceRequestState.Queued or WebGpuResourceRequestState.Cancelled)
                {
                    if (writer is null) { output = new(); writer = new(output); writer.WriteStartArray(); }
                    writer.WriteStartObject();
                    writer.WriteNumber("id", request.Identity);
                    writer.WriteNumber("kind", request.State == WebGpuResourceRequestState.Cancelled ? 0 : request.Kind);
                    if (request.State != WebGpuResourceRequestState.Cancelled && !request.Claimed)
                    {
                        writer.WritePropertyName("args");
                        writer.WriteRawValue(request.Json, skipInputValidation: true);
                    }
                    writer.WriteEndObject();
                }
                Span<byte> receipt = _engineResourceReceipts.AsSpan(_engineResourceBatch.Count * EngineResourceReceiptBytes, EngineResourceReceiptBytes);
                receipt.Clear();
                BinaryPrimitives.WriteInt32LittleEndian(receipt, request.Identity);
                _engineResourceBatch.Add(request);
            }
            foreach (int identity in _engineResourceAcknowledgements)
            {
                if (writer is null) { output = new(); writer = new(output); writer.WriteStartArray(); }
                writer.WriteStartObject(); writer.WriteNumber("id", identity); writer.WriteNumber("kind", -1); writer.WriteEndObject();
            }
            if (writer is not null) { writer.WriteEndArray(); writer.Flush(); }
            _engineResourceDescriptions = output is null ? "[]" : Encoding.UTF8.GetString(output.WrittenSpan);
            _engineResourceBatchPrepared = true;
        }
        finally { writer?.Dispose(); }
    }

    private void AcceptEngineResourceReceipts()
    {
        _engineResourceAcknowledgements.Clear();
        // Resolve every receipt before completing tasks; task continuations may queue the next DAG level.
        for (int index = 0; index < _engineResourceBatch.Count; index++)
        {
            WebGpuResourceRequest request = _engineResourceBatch[index];
            ReadOnlySpan<byte> receipt = _engineResourceReceipts.AsSpan(index * EngineResourceReceiptBytes, EngineResourceReceiptBytes);
            int identity = BinaryPrimitives.ReadInt32LittleEndian(receipt);
            int state = BinaryPrimitives.ReadInt32LittleEndian(receipt[4..]);
            int handle = BinaryPrimitives.ReadInt32LittleEndian(receipt[8..]);
            int errorLength = BinaryPrimitives.ReadInt32LittleEndian(receipt[12..]);
            if (identity != request.Identity || state is < 1 or > 4 || errorLength is < 0 or > 256 || state == 2 && handle <= 0)
                throw new InvalidOperationException("WebGPU.Resource.InvalidReceipt: executor returned an invalid physical-resource result.");
            if (request.State == WebGpuResourceRequestState.Cancelled)
            {
                if (state == 2) RetireEngineResourceAfterFrame(Track(handle));
                if (state != 1) { _engineResourceAcknowledgements.Add(identity); _engineResourceRequests.Remove(request); }
                continue;
            }
            if (state == 1) { request.State = WebGpuResourceRequestState.Submitted; continue; }
            _engineResourceAcknowledgements.Add(identity);
            if (state == 2)
            {
                request.Handle = Track(handle);
                request.State = WebGpuResourceRequestState.Ready;
            }
            else
            {
                ReleaseEngineResourceSnapshots(request);
                request.Failure = Encoding.UTF8.GetString(receipt.Slice(16, errorLength));
                request.State = WebGpuResourceRequestState.Failed;
            }
        }
        _engineResourceBatchPrepared = false;
        _engineResourceDescriptions = "[]";
        foreach (WebGpuResourceRequest request in _engineResourceBatch)
            if (request.State == WebGpuResourceRequestState.Ready) request.Completion?.TrySetResult(request.Handle);
            else if (request.State == WebGpuResourceRequestState.Failed)
                request.Completion?.TrySetException(new InvalidOperationException($"WebGPU.Resource.CreationFailed: {request.Failure}"));
        _engineResourceBatch.Clear();
    }

    private void ResetEngineResourceRequests()
    {
        foreach (WebGpuResourceRequest request in _engineResourceRequests)
        {
            request.State = WebGpuResourceRequestState.Cancelled;
            request.Uploads = null;
            request.Completion?.TrySetCanceled();
        }
        _engineResourceSnapshotBytes = 0;
        _engineResourceRequests.Clear();
        _engineResourceBatch.Clear();
        _engineResourceAcknowledgements.Clear();
        _engineResourceBatchPrepared = false;
        _engineResourceDescriptions = "[]";
    }

    private static string DescribeEngineResource(int kind, object descriptor)
    {
        ArrayBufferWriter<byte> output = new();
        using Utf8JsonWriter writer = new(output);
        writer.WriteStartArray();
        switch (descriptor)
        {
            case BrowserBufferDescription d:
                writer.WriteNumberValue(d.Size); writer.WriteNumberValue((int)d.Usage); writer.WriteStringValue(d.Label); break;
            case BrowserTextureDescription d:
                writer.WriteNumberValue(d.Width); writer.WriteNumberValue(d.Height); writer.WriteNumberValue(d.MipLevelCount);
                writer.WriteNumberValue(d.SampleCount); writer.WriteStringValue(d.Format); writer.WriteNumberValue((int)d.Usage);
                writer.WriteStringValue(d.Label); writer.WriteNumberValue(d.ArrayLayerCount); writer.WriteBooleanValue(d.AllowSrgbView); break;
            case BrowserTextureViewDescription d:
                writer.WriteNumberValue(d.TextureHandle); writer.WriteNumberValue(d.BaseMip); writer.WriteNumberValue(d.MipCount);
                writer.WriteStringValue(d.Aspect); writer.WriteStringValue(d.Label); writer.WriteNumberValue(d.BaseArrayLayer);
                writer.WriteNumberValue(d.ArrayLayerCount); writer.WriteStringValue(d.Dimension); writer.WriteStringValue(d.Format); break;
            case BrowserSamplerDescription d:
                writer.WriteStringValue(d.AddressU); writer.WriteStringValue(d.AddressV);
                writer.WriteStringValue(d.MinFilter); writer.WriteStringValue(d.MagFilter); writer.WriteStringValue(d.MipmapFilter);
                writer.WriteStringValue(d.Label); writer.WriteNumberValue(d.LodMaxClamp); writer.WriteNumberValue(d.MaxAnisotropy);
                writer.WriteNumberValue(d.LodMinClamp); writer.WriteStringValue(d.Compare ?? ""); writer.WriteStringValue(d.AddressW); break;
            case string json when kind is 5 or 6 or 7:
                if (json.Length is 0 or > 262144) throw new ArgumentOutOfRangeException(nameof(descriptor));
                writer.WriteStringValue(json); break;
            default: throw new ArgumentException("Unknown engine physical descriptor.", nameof(descriptor));
        }
        writer.WriteEndArray(); writer.Flush();
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }
}
