using ImGuiNET;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.UI;

/// <summary>
/// Backend-neutral editor font-atlas setup shared by renderer leaves.
/// Device-texture creation remains the responsibility of each backend.
/// </summary>
internal static class ImGuiFontAtlasUtilities
{
    private static readonly HashSet<nint> LoadedContexts = [];
    private static readonly ushort[] PrimaryGlyphRanges =
    [
        0x0020, 0x00FF,
        0x0100, 0x024F,
        0x0370, 0x03FF,
        0x0400, 0x052F,
        0x1E00, 0x1EFF,
        0x2000, 0x206F,
        0x20A0, 0x20CF,
        0,
    ];
    private static readonly ushort[] SymbolGlyphRanges =
    [
        0x2100, 0x214F,
        0x2190, 0x21FF,
        0x2200, 0x22FF,
        0x2300, 0x23FF,
        0x2460, 0x24FF,
        0x2500, 0x257F,
        0x2580, 0x259F,
        0x25A0, 0x25FF,
        0x2600, 0x26FF,
        0x2700, 0x27BF,
        0x27C0, 0x27EF,
        0x2900, 0x297F,
        0x2B00, 0x2BFF,
        0xFFFD, 0xFFFD,
        0,
    ];

    public static unsafe bool TryUseDefaultEditorFont(
        ImGuiIOPtr io,
        float sizePixels = 18.0f,
        bool forceReload = false)
    {
        nint context = ImGui.GetCurrentContext();
        if (context == 0)
            return false;

        lock (LoadedContexts)
        {
            if (forceReload)
                LoadedContexts.Remove(context);
            else if (LoadedContexts.Contains(context))
                return true;
        }

        string? loadedFontPath = TryResolveFontPath("Roboto", "Roboto-Regular.ttf")
            ?? TryResolveFontPath("Lato", "Lato-Regular.ttf");
        if (string.IsNullOrWhiteSpace(loadedFontPath) || !File.Exists(loadedFontPath))
            return false;

        io.Fonts.Clear();
        GCHandle primaryPin = GCHandle.Alloc(PrimaryGlyphRanges, GCHandleType.Pinned);
        GCHandle symbolPin = GCHandle.Alloc(SymbolGlyphRanges, GCHandleType.Pinned);
        try
        {
            ImFontPtr font = io.Fonts.AddFontFromFileTTF(
                loadedFontPath,
                sizePixels,
                null,
                primaryPin.AddrOfPinnedObject());
            if (font.NativePtr is null)
                return false;

            TryMergeSymbolFont(io, sizePixels, symbolPin.AddrOfPinnedObject());
            io.Fonts.Build();
        }
        finally
        {
            primaryPin.Free();
            symbolPin.Free();
        }

        lock (LoadedContexts)
            LoadedContexts.Add(context);

        Debug.Textures($"ImGui font: {Path.GetFileName(loadedFontPath)} loaded @ {sizePixels:0.#}px");
        return true;
    }

    public static void MarkContextDestroyed(nint context)
    {
        if (context == 0)
            return;

        lock (LoadedContexts)
            LoadedContexts.Remove(context);
    }

    private static unsafe void TryMergeSymbolFont(
        ImGuiIOPtr io,
        float sizePixels,
        nint symbolRanges)
    {
        string? symbolFontPath = TryResolveWindowsSymbolFont();
        if (symbolFontPath is null)
            return;

        ImFontConfig* mergeConfig = ImGuiNative.ImFontConfig_ImFontConfig();
        try
        {
            mergeConfig->MergeMode = 1;
            mergeConfig->PixelSnapH = 1;
            mergeConfig->OversampleH = 1;
            mergeConfig->OversampleV = 1;
            io.Fonts.AddFontFromFileTTF(
                symbolFontPath,
                sizePixels,
                mergeConfig,
                symbolRanges);
        }
        finally
        {
            ImGuiNative.ImFontConfig_destroy(mergeConfig);
        }
    }

    private static string? TryResolveWindowsSymbolFont()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        string fontsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string symbolPath = Path.Combine(fontsDirectory, "seguisym.ttf");
        if (File.Exists(symbolPath))
            return symbolPath;

        string emojiPath = Path.Combine(fontsDirectory, "seguiemj.ttf");
        return File.Exists(emojiPath) ? emojiPath : null;
    }

    private static string? TryResolveFontPath(string folder, string fileName)
    {
        foreach (string candidate in GetFontPathCandidates(folder, fileName))
        {
            try
            {
                string fullPath = Path.GetFullPath(candidate);
                if (File.Exists(fullPath))
                    return fullPath;
            }
            catch
            {
                // Ignore invalid path candidates and keep searching.
            }
        }

        string? directory = TryResolveFontDirectory(folder);
        if (directory is null)
            return null;

        string preferredPath = Path.Combine(directory, fileName);
        return File.Exists(preferredPath)
            ? preferredPath
            : Directory.EnumerateFiles(directory, "*.ttf", SearchOption.TopDirectoryOnly)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
    }

    private static IEnumerable<string> GetFontPathCandidates(string folder, string fileName)
    {
        yield return Path.Combine(
            Environment.CurrentDirectory,
            "..",
            "Build",
            "CommonAssets",
            "Fonts",
            folder,
            fileName);
        yield return Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "CommonAssets",
            "Fonts",
            folder,
            fileName);
        yield return Path.Combine(
            Environment.CurrentDirectory,
            "Build",
            "CommonAssets",
            "Fonts",
            folder,
            fileName);
    }

    private static string? TryResolveFontDirectory(string folder)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int i = 0; i < 16 && directory is not null; i++, directory = directory.Parent)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "CommonAssets",
                "Fonts",
                folder);
            if (Directory.Exists(candidate))
                return candidate;
        }

        directory = new DirectoryInfo(Environment.CurrentDirectory);
        for (int i = 0; i < 16 && directory is not null; i++, directory = directory.Parent)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "Build",
                "CommonAssets",
                "Fonts",
                folder);
            if (Directory.Exists(candidate))
                return candidate;

            candidate = Path.Combine(
                directory.FullName,
                "CommonAssets",
                "Fonts",
                folder);
            if (Directory.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
