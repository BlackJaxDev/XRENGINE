using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering.Pipelines.Validation;

/// <summary>Shared text helpers for validation messages.</summary>
public static class RenderPipelineProblemText
{
    /// <summary>Returns the display name of the command type, with the label of a branch when it has one.</summary>
    public static string CommandName(ViewportRenderCommand command)
    {
        string name = RenderCommandInfo.Get(command).DisplayName;
        string? label = command switch
        {
            VPRC_IfElse ifElse => ifElse.Label,
            VPRC_Switch switchCommand => switchCommand.Label,
            _ => null,
        };

        return string.IsNullOrWhiteSpace(label) ? name : string.Concat(name, " '", label, "'");
    }
}
