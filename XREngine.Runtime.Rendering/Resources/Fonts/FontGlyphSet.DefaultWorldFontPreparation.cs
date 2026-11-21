namespace XREngine.Rendering;

public partial class FontGlyphSet
{
    private const string WorldFontPendingDetail = "Default world font preparation is pending.";
    private static readonly Lock DefaultWorldFontPreparationGate = new();
    private static DefaultWorldFontPreparationSnapshot _defaultWorldFontPreparation = new(
        null, null, 0, EDebugPrimitivePreparationStatus.Pending, null, WorldFontPendingDetail);

    /// <summary>
    /// Starts the default world font import on a worker. Debug text reads the result without waiting.
    /// </summary>
    public static void StartDefaultWorldFontPreparation()
    {
        string folder = RuntimeEngine.Rendering.Settings.DefaultFontFolder;
        string file = RuntimeEngine.Rendering.Settings.DefaultFontFileName;
        DefaultWorldFontPreparationSnapshot current = Volatile.Read(ref _defaultWorldFontPreparation);
        if (current.Epoch != 0 && current.Matches(folder, file))
            return;

        lock (DefaultWorldFontPreparationGate)
        {
            current = Volatile.Read(ref _defaultWorldFontPreparation);
            if (current.Epoch != 0 && current.Matches(folder, file))
                return;

            long epoch = current.Epoch + 1;
            DefaultWorldFontPreparationSnapshot pending = new(
                folder, file, epoch, EDebugPrimitivePreparationStatus.Pending, null, WorldFontPendingDetail);
            Volatile.Write(ref _defaultWorldFontPreparation, pending);
            try
            {
                _ = Task.Run(() => PrepareDefaultWorldFont(folder, file, epoch));
            }
            catch (Exception ex)
            {
                DefaultWorldFontPreparationSnapshot failed = new(
                    folder, file, epoch, EDebugPrimitivePreparationStatus.Failed, null,
                    $"Default world font preparation could not start: {ex.Message}");
                Volatile.Write(ref _defaultWorldFontPreparation, failed);
            }
        }
    }

    /// <summary>
    /// Returns the prepared default world font and its status without waiting for import work.
    /// </summary>
    public static EDebugPrimitivePreparationStatus TryGetPreparedDefaultWorldFont(
        out FontGlyphSet? font,
        out string detail)
    {
        StartDefaultWorldFontPreparation();
        DefaultWorldFontPreparationSnapshot snapshot = Volatile.Read(ref _defaultWorldFontPreparation);
        font = snapshot.Font;
        detail = snapshot.Detail;
        return snapshot.Status;
    }

    private static void PrepareDefaultWorldFont(string folder, string file, long epoch)
    {
        try
        {
            FontGlyphSet font = LoadEngineFont(folder, file, CreateWorldMtsdfImportOptions());
            if (font.Glyphs is not { Count: > 0 })
                throw new InvalidOperationException("The default world font has no glyphs.");
            if (TryGetLayoutResourceIssue(font, out string? issue))
                throw new InvalidOperationException($"The default world font has invalid layout resources: {issue}");

            PublishDefaultWorldFontPreparation(new DefaultWorldFontPreparationSnapshot(
                folder, file, epoch, EDebugPrimitivePreparationStatus.Ready, font,
                $"Default world font is ready ({font.AtlasType}, {font.Glyphs.Count} glyphs)."));
        }
        catch (Exception ex)
        {
            PublishDefaultWorldFontPreparation(new DefaultWorldFontPreparationSnapshot(
                folder, file, epoch, EDebugPrimitivePreparationStatus.Failed, null,
                $"Default world font preparation failed: {ex.Message}"));
        }
    }

    private static void PublishDefaultWorldFontPreparation(DefaultWorldFontPreparationSnapshot result)
    {
        lock (DefaultWorldFontPreparationGate)
        {
            if (Volatile.Read(ref _defaultWorldFontPreparation).Epoch != result.Epoch)
                return;
            Volatile.Write(ref _defaultWorldFontPreparation, result);
        }
    }
}
