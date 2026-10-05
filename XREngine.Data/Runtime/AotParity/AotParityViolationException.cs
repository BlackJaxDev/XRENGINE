namespace XREngine.Data.Runtime.AotParity;

/// <summary>Thrown in <see cref="EAotParityMode.Error"/> mode when a player-path reflective fallback is observed.</summary>
public sealed class AotParityViolationException : InvalidOperationException
{
    public AotParityViolationException(AotParityDiagnostic diagnostic)
        : base(diagnostic.Format())
        => Diagnostic = diagnostic;

    public AotParityDiagnostic Diagnostic { get; }
}
