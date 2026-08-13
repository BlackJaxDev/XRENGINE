namespace XREngine.Runtime.Automation.Profiling;

/// <summary>Work which must begin only after the MCP response has left the process.</summary>
public sealed record RenderProfileStartOperation(Func<Task> StartAfterResponse, Task Completion);
