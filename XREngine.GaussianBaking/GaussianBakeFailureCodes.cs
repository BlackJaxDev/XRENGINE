namespace XREngine.Rendering.Models.Gaussian.Baking;

/// <summary>
/// Gives the stable failure codes that a bake reports, and the process exit code for each one.
/// </summary>
public static class GaussianBakeFailureCodes
{
    /// <summary>The command line arguments are not valid.</summary>
    public const string InvalidArguments = "invalid-arguments";

    /// <summary>The input archive or its asset cannot be read or has the wrong type.</summary>
    public const string InputLoadFailed = "input-load-failed";

    /// <summary>The input contains executable content or entry paths that are not safe.</summary>
    public const string ContentRejected = "content-rejected";

    /// <summary>The input has no content to bake.</summary>
    public const string ContentEmpty = "content-empty";

    /// <summary>The bake result is larger than a budget limit.</summary>
    public const string BudgetExceeded = "budget-exceeded";

    /// <summary>An option is known but this engine version does not support it.</summary>
    public const string UnsupportedOption = "unsupported-option";

    /// <summary>The engine cannot render the capture views.</summary>
    public const string CaptureFailed = "capture-failed";

    /// <summary>The cache or the video encoder cannot write the output.</summary>
    public const string EncodeFailed = "encode-failed";

    /// <summary>
    /// Returns the process exit code for a failure code. An unknown code gives 1.
    /// </summary>
    public static int ExitCodeFor(string failureCode)
        => failureCode switch
        {
            InvalidArguments => 2,
            InputLoadFailed => 3,
            ContentRejected => 4,
            ContentEmpty => 5,
            BudgetExceeded => 6,
            UnsupportedOption => 7,
            CaptureFailed => 8,
            EncodeFailed => 9,
            _ => 1,
        };
}
