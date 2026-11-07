using System.Collections.ObjectModel;

namespace XREngine.Rendering.RenderGraph;

/// <summary>
/// One compiled batch of passes that share a stage and a compatible attachment signature.
/// Backends can record the passes of one batch inside one render pass scope.
/// </summary>
public sealed class RenderGraphPassBatch
{
    private readonly List<int> _passIndices = [];

    /// <summary>Initializes a batch with no passes.</summary>
    public RenderGraphPassBatch(int batchIndex, ERenderGraphPassStage stage, string attachmentSignature)
    {
        BatchIndex = batchIndex;
        Stage = stage;
        AttachmentSignature = attachmentSignature;
    }

    /// <summary>Gets the index of the batch in compilation order.</summary>
    public int BatchIndex { get; }

    /// <summary>Gets the stage that every pass of the batch shares.</summary>
    public ERenderGraphPassStage Stage { get; }

    /// <summary>Gets the compatibility signature of the attachment usages.</summary>
    public string AttachmentSignature { get; }

    /// <summary>Gets the pass indices of the batch in execution order.</summary>
    public ReadOnlyCollection<int> PassIndices => _passIndices.AsReadOnly();

    /// <summary>Appends a pass to the batch.</summary>
    internal void AddPass(int passIndex)
        => _passIndices.Add(passIndex);
}
