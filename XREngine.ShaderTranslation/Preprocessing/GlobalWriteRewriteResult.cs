namespace XREngine.ShaderTranslation.Preprocessing;

/// <summary>
/// The result of <see cref="GlobalWriteRewriter.Rewrite"/>.
/// </summary>
/// <param name="Text">The rewritten translation unit.</param>
/// <param name="RewrittenGlobals">The names of the globals that have a private copy, in declaration order.</param>
public sealed record GlobalWriteRewriteResult(string Text, IReadOnlyList<string> RewrittenGlobals);
