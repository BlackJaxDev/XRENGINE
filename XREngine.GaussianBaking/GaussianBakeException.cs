namespace XREngine.Rendering.Models.Gaussian.Baking;

/// <summary>
/// Stops a bake with a stable failure code. The code is one of the values in
/// <see cref="GaussianBakeFailureCodes"/>, and it sets the process exit code.
/// </summary>
public sealed class GaussianBakeException(string failureCode, string message) : Exception(message)
{
    /// <summary>Gets the stable failure code, for example <c>content-rejected</c>.</summary>
    public string FailureCode { get; } = failureCode;
}
