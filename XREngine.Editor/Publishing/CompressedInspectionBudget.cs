using System.Diagnostics;

namespace XREngine.Editor.Publishing;

/// <summary>
/// Limits the inflated size and the time that the publish spends on the compressed content of one file and of each
/// compressed item in it (a PNG text chunk, a ZIP entry, or GZIP data). A file that goes past a limit fails the publish
/// with the file and the limit named, so a decompression bomb cannot make a publish run without end.
/// </summary>
internal sealed class CompressedInspectionBudget
{
    private readonly string _label;
    private readonly long _maxItemBytes;
    private readonly long _maxFileBytes;
    private readonly TimeSpan _maxItemTime;
    private readonly TimeSpan _maxFileTime;
    private readonly Stopwatch _fileClock = Stopwatch.StartNew();
    private readonly Stopwatch _itemClock = new();
    private long _fileBytes;
    private long _itemBytes;
    private string _place = string.Empty;

    /// <summary>Makes a budget for one file.</summary>
    /// <param name="label">The name of the file in an error.</param>
    /// <param name="maxItemBytes">The largest inflated size of one compressed item.</param>
    /// <param name="maxFileBytes">The largest inflated size of all compressed items in the file.</param>
    /// <param name="maxItemTime">The longest time for one compressed item.</param>
    /// <param name="maxFileTime">The longest time for all compressed items in the file.</param>
    public CompressedInspectionBudget(string label, long maxItemBytes, long maxFileBytes, TimeSpan maxItemTime, TimeSpan maxFileTime)
    {
        _label = label;
        _maxItemBytes = maxItemBytes;
        _maxFileBytes = maxFileBytes;
        _maxItemTime = maxItemTime;
        _maxFileTime = maxFileTime;
    }

    /// <summary>Starts the count of one compressed item.</summary>
    /// <param name="place">The item in an error, for example a ZIP entry name.</param>
    public void BeginItem(string place)
    {
        _place = place;
        _itemBytes = 0;
        _itemClock.Restart();
    }

    /// <summary>Adds inflated bytes and fails when a size or time limit is exceeded.</summary>
    /// <param name="bytes">The bytes that were read from the decompression stream.</param>
    /// <exception cref="CookedSourcePathException">A limit is exceeded.</exception>
    public void Add(int bytes)
    {
        _itemBytes += bytes;
        _fileBytes += bytes;
        if (_itemBytes > _maxItemBytes)
            Fail($"the inflated size of one item is larger than {_maxItemBytes} bytes");
        if (_fileBytes > _maxFileBytes)
            Fail($"the inflated size of the file is larger than {_maxFileBytes} bytes");
        if (_itemClock.Elapsed > _maxItemTime)
            Fail($"the inspection of one item takes longer than {_maxItemTime.TotalSeconds:0} s");
        if (_fileClock.Elapsed > _maxFileTime)
            Fail($"the inspection of the file takes longer than {_maxFileTime.TotalSeconds:0} s");
    }

    private void Fail(string limit)
        => throw new CookedSourcePathException(
            $"Published file '{_label}' holds compressed data ({_place}) that the publish cannot inspect for machine paths: " +
            $"{limit}. Remove the file, or store its content unpacked in the asset folder.");
}
