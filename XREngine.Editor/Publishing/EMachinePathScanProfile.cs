namespace XREngine.Editor.Publishing;

/// <summary>The rules that <see cref="CookedOutputPathGuard"/> uses to find machine paths in a file.</summary>
public enum EMachinePathScanProfile
{
    /// <summary>
    /// A cooked asset file: the exact roots, user profile paths, drive paths with three or more parts, and UNC paths.
    /// </summary>
    CookedAsset,

    /// <summary>
    /// A copied text file (JSON, YAML, shader, notes): the exact roots, drive paths with one or more parts, and UNC paths.
    /// </summary>
    Text,

    /// <summary>
    /// A copied binary file (images, audio, video, models): the exact roots, user profile paths, and file paths with two or
    /// more folder parts, one separator style, plain name characters, and a file name with an extension.
    /// </summary>
    BinaryMedia,
}
