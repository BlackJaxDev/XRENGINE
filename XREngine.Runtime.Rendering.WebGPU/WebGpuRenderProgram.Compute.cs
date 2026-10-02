using System.Buffers;
using System.Text;
using System.Text.Json;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRenderProgram
{
    private readonly record struct ComputeCommandKey(WebGpuBindingSet Bindings, uint X, uint Y, uint Z);

    private readonly Dictionary<ComputeCommandKey, int> _computeCommands = [];
    private Task? _computePreparation;
    private int _computePipeline;

    /// <summary>Starts the immutable compute pipeline without blocking the browser owner thread.</summary>
    internal bool TryPrepareForCompute()
    {
        Generate();
        if (Artifact.ComputeEntryPoint is null || Artifact.ComputeWorkgroupSize is null)
            throw new NotSupportedException("WebGPU.Compute.ArtifactInvalid: a cooked compute entry and verified workgroup size are required.");
        Task preparation = _computePreparation ?? PrepareComputeAsync(_preparationEpoch);
        if (_computePreparation is null)
            SetField(ref _computePreparation, preparation, publishNotifications: false);
        if (preparation.IsFaulted || preparation.IsCanceled)
            preparation.GetAwaiter().GetResult();
        return _computePipeline != 0 && preparation.IsCompletedSuccessfully;
    }

    private async Task PrepareComputeAsync(int epoch)
    {
        Task modulePreparation = _preparation
            ?? throw new InvalidOperationException("WebGPU.Compute.ModuleMissing: prepare the cooked shader module first.");
        await modulePreparation;
        int pipeline = await Renderer.CreateComputePipelineAsync(DescribeComputePipeline());
        if (IsRetired || Data.IsDestroyed || !Renderer.AcceptsBackendWork || epoch != _preparationEpoch)
        {
            if (Renderer.State == BrowserRendererState.Ready)
                Renderer.RetireEngineResource(pipeline);
            throw new InvalidOperationException("WebGPU.Compute.Obsolete: pipeline preparation completed after its program retired.");
        }
        SetField(ref _computePipeline, pipeline);
    }

    private string DescribeComputePipeline()
    {
        ShaderProgramArtifact artifact = Artifact;
        ShaderComputeWorkgroupSize workgroup = artifact.ComputeWorkgroupSize
            ?? throw new NotSupportedException("WebGPU.Compute.WorkgroupMissing: the cooked workgroup contract is required.");
        ArrayBufferWriter<byte> bytes = new();
        using (Utf8JsonWriter writer = new(bytes))
        {
            writer.WriteStartObject();
            writer.WriteString("label", artifact.Name);
            writer.WriteStartArray("layouts");
            foreach (int layout in _layouts) writer.WriteNumberValue(layout);
            writer.WriteEndArray();
            writer.WriteStartObject("compute");
            writer.WriteNumber("shader", _shaderHandle);
            writer.WriteString("entryPoint", artifact.ComputeEntryPoint);
            writer.WriteStartArray("workgroupSize");
            writer.WriteNumberValue(workgroup.X);
            writer.WriteNumberValue(workgroup.Y);
            writer.WriteNumberValue(workgroup.Z);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(bytes.WrittenSpan);
    }

    /// <summary>Records one fully bound dispatch; retained commands are reused for stable workgroup counts.</summary>
    internal void RecordCompute(uint groupsX, uint groupsY, uint groupsZ)
    {
        if (!TryPrepareForCompute())
            throw new InvalidOperationException("WebGPU.Compute.PipelinePending: defer the complete frame until preparation finishes.");
        if (!TrySnapshotBindings(deferMissingResources: false, out WebGpuBindingSet? bindings) || bindings is null)
            throw new InvalidOperationException("WebGPU.Compute.BindingsMissing: all cooked bindings must be published.");
        ComputeCommandKey key = new(bindings, groupsX, groupsY, groupsZ);
        if (!_computeCommands.TryGetValue(key, out int command))
        {
            if (_computeCommands.Count >= 1024)
                throw new InvalidOperationException("WebGPU.Compute.CommandCapacity: the program exceeds 1024 retained binding/workgroup variants.");
            command = Renderer.PrepareCommands(DescribeComputeCommand(bindings, groupsX, groupsY, groupsZ));
            _computeCommands.Add(key, command);
        }
        Span<uint> offsets = stackalloc uint[16];
        int count = SnapshotUniforms(offsets);
        Renderer.RecordEngineCommands(command, offsets[..count]);
        bindings.MarkRecorded();
    }

    /// <summary>Prevents an incomplete dispatch from lending bindings to a later attempt.</summary>
    internal void ClearTransientComputeBindings()
    {
        Array.Clear(_resourceHandles);
        Array.Clear(_resourceSizes);
        Array.Clear(_resourceOwners);
    }

    private string DescribeComputeCommand(WebGpuBindingSet bindings, uint groupsX, uint groupsY, uint groupsZ)
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
            writer.WriteStartArray("workgroups");
            writer.WriteNumberValue(groupsX);
            writer.WriteNumberValue(groupsY);
            writer.WriteNumberValue(groupsZ);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(bytes.WrittenSpan);
    }

    internal void ReleaseComputeCommandsUsing(WebGpuBindingSet bindings)
    {
        List<ComputeCommandKey> obsolete = [];
        foreach ((ComputeCommandKey key, int command) in _computeCommands)
            if (ReferenceEquals(key.Bindings, bindings))
            {
                Renderer.RetireEngineResourceAfterFrame(command);
                obsolete.Add(key);
            }
        foreach (ComputeCommandKey key in obsolete)
            _computeCommands.Remove(key);
    }

    private void DestroyCompute()
    {
        foreach (int command in _computeCommands.Values)
            Renderer.RetireEngineResourceAfterFrame(command);
        _computeCommands.Clear();
        if (_computePipeline != 0 && Renderer.State != BrowserRendererState.Disposed)
            Renderer.RetireEngineResourceAfterFrame(_computePipeline);
        SetField(ref _computePipeline, 0);
        SetField(ref _computePreparation, null);
    }
}
