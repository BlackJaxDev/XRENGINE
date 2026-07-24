using System.Collections.ObjectModel;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>One immutable queue submission and its structural synchronization contract.</summary>
internal sealed record RenderGraphPlanSubmission(
    int SubmissionIndex,
    ERenderGraphPassStage Queue,
    string AttachmentSignature,
    ReadOnlyCollection<int> PassIndices,
    ReadOnlyCollection<int> WaitSubmissionIndices,
    int SignalIndex);
