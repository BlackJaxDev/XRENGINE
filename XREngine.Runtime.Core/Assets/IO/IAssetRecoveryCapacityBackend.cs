namespace XREngine.Core.Files;

/// <summary>Inspects recovery capacity through the host filesystem.</summary>
public interface IAssetRecoveryCapacityBackend
{
    /// <summary>
    /// Counts every retained file and its length, including unknown entries.
    /// Throws if the store cannot be inspected safely or totals plus reservations exceed a limit.
    /// A rejected inspection must not return partial totals or change retained files.
    /// </summary>
    (long Bytes, int Files) Count(string root, long additionalBytes, int additionalFiles,
        long maximumBytes, int maximumFiles);
}
