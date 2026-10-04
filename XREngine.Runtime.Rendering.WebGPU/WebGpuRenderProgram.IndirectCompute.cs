using System.Buffers;
using System.Text;
using System.Text.Json;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRenderProgram
{
    private readonly record struct IndirectComputeCommandKey(WebGpuBindingSet Bindings,
        AbstractRenderAPIObject Arguments, int Handle, uint Offset);
    private readonly Dictionary<IndirectComputeCommandKey, int> _indirectComputeCommands = [];

    internal void RecordComputeIndirect(WebGpuDataBuffer arguments, uint byteOffset)
        => RecordComputeIndirect(arguments, arguments.ResourceHandle, byteOffset);

    internal void RecordComputeIndirect(AbstractRenderAPIObject arguments, int argumentHandle, uint byteOffset)
    {
        if (!TrySnapshotBindings(false, out WebGpuBindingSet? bindings) || bindings is null)
            throw new InvalidOperationException("WebGPU.Compute.BindingsMissing: all cooked bindings must be published.");
        IndirectComputeCommandKey key = new(bindings, arguments, argumentHandle, byteOffset);
        if (!_indirectComputeCommands.TryGetValue(key, out int command))
        {
            int capacity = bindings.CacheOwner is null ? 128 : WebGpuAdvancedShadingFrame.MaximumRetainedCohorts;
            if (_indirectComputeCommands.Count >= capacity)
                throw new InvalidOperationException($"WebGPU.Compute.IndirectCapacity: the program exceeds {capacity} retained indirect variants.");
            command = Renderer.PrepareEngineCommands(this, DescribeIndirectComputeCommand(bindings, argumentHandle, byteOffset));
            _indirectComputeCommands.Add(key, command);
        }
        Span<uint> offsets = stackalloc uint[16];
        int count = SnapshotUniforms(offsets);
        Renderer.RecordEngineCommands(command, offsets[..count]);
        if (arguments is WebGpuOwnedStorageBuffer nativeArguments) nativeArguments.MarkRecorded();
        bindings.MarkRecorded();
    }

    private string DescribeIndirectComputeCommand(WebGpuBindingSet bindings, int arguments, uint byteOffset)
    {
        ArrayBufferWriter<byte> bytes = new();
        using (Utf8JsonWriter writer = new(bytes))
        {
            writer.WriteStartObject();
            writer.WriteString("label", Artifact.Name);
            writer.WriteStartArray("commands");
            writer.WriteStartObject();
            writer.WriteString("type", "compute");
            writer.WriteNumber("pipeline", _computePipeline);
            writer.WriteStartArray("bindings");
            for (int group = 0; group < bindings.GroupHandles.Length; group++)
            {
                writer.WriteStartObject();
                writer.WriteNumber("index", group);
                writer.WriteNumber("group", bindings.GroupHandles[group]);
                writer.WriteStartArray("dynamicOffsets");
                foreach (ShaderStageResourceLayout resource in Artifact.Resources)
                    if (resource.Contract.Set == group && resource.DynamicOffset) writer.WriteNumberValue(0);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartObject("indirect");
            writer.WriteNumber("buffer", arguments);
            writer.WriteNumber("offset", byteOffset);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(bytes.WrittenSpan);
    }

    internal void ReleaseIndirectComputeCommandsUsing(AbstractRenderAPIObject? resource = null, int handle = 0,
        WebGpuBindingSet? bindings = null)
    {
        List<IndirectComputeCommandKey>? obsolete = null;
        foreach ((IndirectComputeCommandKey key, int command) in _indirectComputeCommands)
        {
            bool remove = resource is null && bindings is null ||
                bindings is not null && ReferenceEquals(key.Bindings, bindings) ||
                resource is not null && ReferenceEquals(key.Arguments, resource) && (handle == 0 || key.Handle == handle);
            if (!remove) continue;
            Renderer.RetireEngineResourceAfterFrame(command);
            (obsolete ??= []).Add(key);
        }
        if (obsolete is not null)
            foreach (IndirectComputeCommandKey key in obsolete) _indirectComputeCommands.Remove(key);
    }
}
