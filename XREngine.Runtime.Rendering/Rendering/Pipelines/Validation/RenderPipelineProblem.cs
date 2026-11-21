using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering.Pipelines.Validation;

/// <summary>One problem that the validator found in a render pipeline.</summary>
/// <param name="RuleId">The identifier of the rule or the source, such as <c>resource.unused</c>.</param>
/// <param name="Severity">The severity.</param>
/// <param name="Message">A description of the problem and of the fix.</param>
/// <param name="Target">The kind of object that the problem points to.</param>
/// <param name="Command">The command, when <paramref name="Target"/> is <see cref="ERenderPipelineProblemTarget.Command"/>.</param>
/// <param name="PassIndex">The pass index, when <paramref name="Target"/> is <see cref="ERenderPipelineProblemTarget.Pass"/>, otherwise -1.</param>
/// <param name="Name">The resource name or stage key, when the target has a name.</param>
public sealed record RenderPipelineProblem(
    string RuleId,
    ERenderPipelineProblemSeverity Severity,
    string Message,
    ERenderPipelineProblemTarget Target = ERenderPipelineProblemTarget.Pipeline,
    ViewportRenderCommand? Command = null,
    int PassIndex = -1,
    string? Name = null)
{
    /// <summary>Creates a problem that points to a command.</summary>
    public static RenderPipelineProblem ForCommand(string ruleId, ERenderPipelineProblemSeverity severity, string message, ViewportRenderCommand command)
        => new(ruleId, severity, message, ERenderPipelineProblemTarget.Command, command);

    /// <summary>Creates a problem that points to a pass.</summary>
    public static RenderPipelineProblem ForPass(string ruleId, ERenderPipelineProblemSeverity severity, string message, int passIndex)
        => new(ruleId, severity, message, ERenderPipelineProblemTarget.Pass, PassIndex: passIndex);

    /// <summary>Creates a problem that points to a resource.</summary>
    public static RenderPipelineProblem ForResource(string ruleId, ERenderPipelineProblemSeverity severity, string message, string resourceName)
        => new(ruleId, severity, message, ERenderPipelineProblemTarget.Resource, Name: resourceName);

    /// <summary>Creates a problem that points to a schema stage.</summary>
    public static RenderPipelineProblem ForStage(string ruleId, ERenderPipelineProblemSeverity severity, string message, string stageKey)
        => new(ruleId, severity, message, ERenderPipelineProblemTarget.Stage, Name: stageKey);
}
