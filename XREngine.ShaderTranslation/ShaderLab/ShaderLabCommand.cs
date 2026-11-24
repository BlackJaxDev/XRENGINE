namespace XREngine.ShaderTranslation.ShaderLab;

/// <summary>
/// Holds one ShaderLab render state command, such as <c>Cull [_Cull]</c> or a <c>Stencil</c> block.
/// </summary>
/// <param name="Name">The command name as written.</param>
/// <param name="Arguments">The argument tokens on the command line. A property reference keeps its brackets, for example <c>[_Cull]</c>.</param>
/// <param name="Children">The nested commands of a block command. It is empty for a single-line command.</param>
/// <param name="Line">The one-based source line of the command name.</param>
public sealed record ShaderLabCommand(string Name, IReadOnlyList<string> Arguments, IReadOnlyList<ShaderLabCommand> Children, int Line)
{
    /// <summary>Returns the arguments joined with single spaces.</summary>
    public string ArgumentText => string.Join(' ', Arguments);
}
