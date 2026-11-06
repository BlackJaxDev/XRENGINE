using System.Text;

namespace XREngine.Editor.Cutscenes;

/// <summary>
/// The T0 cost of a baked manifest: the estimated size of the proxy data that stays resident while the cutscene is open.
/// A bake whose total is above the budget fails. The system does not remove assets from T0 to fit the budget.
/// The report counts texture previews and mesh proxies. It does not count audio proxies, because no audio proxy exists yet.
/// </summary>
public sealed class CutsceneT0Report
{
    /// <summary>Creates a report.</summary>
    /// <param name="totalBytes">The sum of the T0 sizes of all manifest assets, in bytes.</param>
    /// <param name="budgetBytes">The T0 budget, in bytes.</param>
    /// <param name="assetCount">The number of manifest assets.</param>
    /// <param name="largestAssets">The largest assets, largest first.</param>
    public CutsceneT0Report(long totalBytes, long budgetBytes, int assetCount, IReadOnlyList<CutsceneT0ReportEntry> largestAssets)
    {
        ArgumentNullException.ThrowIfNull(largestAssets);
        TotalBytes = totalBytes;
        BudgetBytes = budgetBytes;
        AssetCount = assetCount;
        LargestAssets = largestAssets;
    }

    /// <summary>The sum of the T0 sizes of all manifest assets, in bytes.</summary>
    public long TotalBytes { get; }

    /// <summary>The T0 budget, in bytes.</summary>
    public long BudgetBytes { get; }

    /// <summary>The number of manifest assets.</summary>
    public int AssetCount { get; }

    /// <summary>True if <see cref="TotalBytes"/> is above <see cref="BudgetBytes"/>.</summary>
    public bool IsOverBudget => TotalBytes > BudgetBytes;

    /// <summary>The largest assets, largest first.</summary>
    public IReadOnlyList<CutsceneT0ReportEntry> LargestAssets { get; }

    /// <summary>Writes the totals and the largest assets as text.</summary>
    public string Describe()
    {
        StringBuilder builder = new();
        builder.Append("T0 total ").Append(FormatBytes(TotalBytes))
            .Append(" for ").Append(AssetCount).Append(" texture and mesh assets, budget ").Append(FormatBytes(BudgetBytes))
            .Append(". Audio proxies are not counted, because no audio proxy exists yet.");
        if (LargestAssets.Count == 0)
            return builder.ToString();

        builder.Append(" Largest:");
        for (int i = 0; i < LargestAssets.Count; i++)
        {
            CutsceneT0ReportEntry entry = LargestAssets[i];
            builder.Append(i == 0 ? " " : ", ").Append(entry.Kind).Append(' ').Append(entry.AssetId).Append(" (").Append(FormatBytes(entry.ProxyBytes)).Append(')');
        }
        return builder.ToString();
    }

    private static string FormatBytes(long bytes)
        => bytes >= 1024L * 1024L ? $"{bytes / (1024.0 * 1024.0):0.##} MiB" : $"{bytes / 1024.0:0.##} KiB";
}
