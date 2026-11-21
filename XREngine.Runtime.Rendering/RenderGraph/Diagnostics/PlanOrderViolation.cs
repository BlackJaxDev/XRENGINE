namespace XREngine.Rendering.RenderGraph.Diagnostics;

/// <summary>
/// A synchronization edge that the plan order breaks: the consumer pass runs before the producer pass that it waits
/// for. A backend that executes passes in recorded order, such as OpenGL, then reads stale or undefined data.
/// </summary>
/// <param name="ProducerPassIndex">The pass that must run first.</param>
/// <param name="ConsumerPassIndex">The pass that ran first.</param>
/// <param name="ResourceName">The logical resource of the edge, or an empty string for an explicit dependency.</param>
/// <param name="Message">A description of the violation for tools.</param>
public sealed record PlanOrderViolation(
    int ProducerPassIndex,
    int ConsumerPassIndex,
    string ResourceName,
    string Message);
