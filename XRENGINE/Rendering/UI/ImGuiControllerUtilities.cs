using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.OpenGL.Extensions.ImGui;
using XREngine;

namespace XREngine.Rendering.UI;

internal static class ImGuiControllerUtilities
{
    private static readonly BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;

    private static readonly HashSet<nint> _fontLoadedContexts = [];

    // ── Primary font glyph ranges ────────────────────────────────────────
    // Covers Latin (Basic + Extended-A/B + Additional), Greek, Cyrillic,
    // general punctuation, currency, and Vietnamese tone marks.
    // These are codepoints the primary font (Roboto / Lato) actually contains.
    private static readonly ushort[] PrimaryGlyphRanges =
    [
        0x0020, 0x00FF, // Basic Latin + Latin-1 Supplement
        0x0100, 0x024F, // Latin Extended-A + Extended-B
        0x0370, 0x03FF, // Greek and Coptic
        0x0400, 0x052F, // Cyrillic + Cyrillic Supplement
        0x1E00, 0x1EFF, // Latin Extended Additional (Vietnamese, etc.)
        0x2000, 0x206F, // General Punctuation (smart quotes, dashes, ellipsis, bullets)
        0x20A0, 0x20CF, // Currency Symbols
        0,
    ];

    // ── Symbol merge font glyph ranges ───────────────────────────────────
    // Ranges loaded from a symbol fallback font (e.g. Segoe UI Symbol on Windows).
    // Covers arrows, math, box drawing, geometric shapes, dingbats, etc.
    private static readonly ushort[] SymbolGlyphRanges =
    [
        0x2100, 0x214F, // Letterlike Symbols
        0x2190, 0x21FF, // Arrows
        0x2200, 0x22FF, // Mathematical Operators
        0x2300, 0x23FF, // Miscellaneous Technical
        0x2460, 0x24FF, // Enclosed Alphanumerics (circled numbers/letters)
        0x2500, 0x257F, // Box Drawing
        0x2580, 0x259F, // Block Elements
        0x25A0, 0x25FF, // Geometric Shapes
        0x2600, 0x26FF, // Miscellaneous Symbols
        0x2700, 0x27BF, // Dingbats (check marks, crosses, stars, etc.)
        0x27C0, 0x27EF, // Miscellaneous Mathematical Symbols-A
        0x2900, 0x297F, // Supplemental Arrows-B
        0x2B00, 0x2BFF, // Miscellaneous Symbols and Arrows
        0xFFFD, 0xFFFD, // Replacement character
        0,
    ];

    /// <summary>
    /// Load the editor default font (Roboto with Lato fallback) and optionally merge
    /// a Windows symbol font for broad Unicode coverage. Call once per ImGui context.
    /// </summary>
    public static unsafe bool TryUseDefaultEditorFont(ImGuiController controller, float sizePixels = 18.0f)
    {
        if (controller is null)
            return false;

        try
        {
            controller.MakeCurrent();

            nint context = ImGui.GetCurrentContext();
            if (context == 0)
                return false;

            if (_fontLoadedContexts.Contains(context))
                return true;

            var io = ImGui.GetIO();

            if (!TryLoadEditorFont(io, sizePixels, out string? loadedFontPath))
                return false;

            TryRecreateFontDeviceTexture(controller);

            _fontLoadedContexts.Add(context);
            Debug.Out($"ImGui font: {Path.GetFileName(loadedFontPath)} loaded @ {sizePixels:0.#}px");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed to load editor ImGui font: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Overload for Vulkan / headless paths that receive an <see cref="ImGuiIOPtr"/> directly.
    /// </summary>
    public static unsafe bool TryUseDefaultEditorFont(ImGuiIOPtr io, float sizePixels = 18.0f)
        => TryLoadEditorFont(io, sizePixels, out _);

    // ── Keep old names as forwarding shims so existing callers compile. ──
    [Obsolete("Use TryUseDefaultEditorFont instead.")]
    public static unsafe bool TryUseLatoAsDefaultFont(ImGuiController controller, float sizePixels = 18.0f)
        => TryUseDefaultEditorFont(controller, sizePixels);

    [Obsolete("Use TryUseDefaultEditorFont instead.")]
    public static unsafe bool TryUseLatoAsDefaultFont(ImGuiIOPtr io, float sizePixels = 18.0f)
        => TryUseDefaultEditorFont(io, sizePixels);

    private static unsafe bool TryLoadEditorFont(ImGuiIOPtr io, float sizePixels, out string? loadedFontPath)
    {
        // Try Roboto first (broader symbol hints), then Lato as fallback.
        loadedFontPath = TryResolveFontPath("Roboto", "Roboto-Regular.ttf")
                      ?? TryResolveFontPath("Lato", "Lato-Regular.ttf");

        if (string.IsNullOrWhiteSpace(loadedFontPath) || !File.Exists(loadedFontPath))
            return false;

        io.Fonts.Clear();

        // ── Primary font ─────────────────────────────────────────────────
        fixed (ushort* primaryRanges = PrimaryGlyphRanges)
        {
            var font = io.Fonts.AddFontFromFileTTF(loadedFontPath, sizePixels, null, (nint)primaryRanges);
            if (font.NativePtr == null)
                return false;
        }

        // ── Merge symbol fallback font (Windows: Segoe UI Symbol) ────────
        TryMergeSymbolFont(io, sizePixels);

        io.Fonts.Build();
        return true;
    }

    private static unsafe void TryMergeSymbolFont(ImGuiIOPtr io, float sizePixels)
    {
        // On Windows, Segoe UI Symbol provides excellent coverage for
        // arrows, math, box drawing, geometric shapes, dingbats, etc.
        string? symbolFontPath = TryResolveWindowsSymbolFont();
        if (symbolFontPath is null)
            return;

        try
        {
            var mergeConfig = ImGuiNative.ImFontConfig_ImFontConfig();
            mergeConfig->MergeMode = 1;          // merge into previous font
            mergeConfig->PixelSnapH = 1;
            mergeConfig->OversampleH = 1;         // symbols don't need heavy oversampling
            mergeConfig->OversampleV = 1;

            fixed (ushort* symRanges = SymbolGlyphRanges)
            {
                io.Fonts.AddFontFromFileTTF(symbolFontPath, sizePixels, mergeConfig, (nint)symRanges);
            }

            ImGuiNative.ImFontConfig_destroy(mergeConfig);
            Debug.Out($"ImGui font: merged symbol fallback from {Path.GetFileName(symbolFontPath)}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed to merge symbol font: {ex.Message}");
        }
    }

    private static string? TryResolveWindowsSymbolFont()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        // Prefer Segoe UI Symbol for broadest symbol coverage.
        // Fall back to Segoe UI Emoji, then Arial Unicode MS.
        string fontsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts));
        string[] candidates =
        [
            Path.Combine(fontsDir, "seguisym.ttf"),  // Segoe UI Symbol
            Path.Combine(fontsDir, "seguiemj.ttf"),   // Segoe UI Emoji
        ];

        foreach (string path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    /// <summary>
    /// Resolves the path to a font file inside <c>Build/CommonAssets/Fonts/{folder}/</c>.
    /// </summary>
    private static string? TryResolveFontPath(string folder, string fileName)
    {
        foreach (string candidate in GetFontPathCandidates(folder, fileName))
        {
            try
            {
                string full = Path.GetFullPath(candidate);
                if (File.Exists(full))
                    return full;
            }
            catch
            {
                // Ignore invalid paths; keep searching.
            }
        }

        // Fallback: if we can find the font folder, pick any suitable TTF.
        string? fontDir = TryResolveFontDirectory(folder);
        if (string.IsNullOrWhiteSpace(fontDir) || !Directory.Exists(fontDir))
            return null;

        string specific = Path.Combine(fontDir, fileName);
        if (File.Exists(specific))
            return specific;

        return Directory.EnumerateFiles(fontDir, "*.ttf", SearchOption.TopDirectoryOnly)
            .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static IEnumerable<string> GetFontPathCandidates(string folder, string fontFileName)
    {
        // Typical editor working dir: <repo>/XREngine.Editor
        yield return Path.Combine(Environment.CurrentDirectory, "..", "Build", "CommonAssets", "Fonts", folder, fontFileName);

        // Typical runtime output dir: <repo>/Build/Editor/.../net*/
        yield return Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "CommonAssets", "Fonts", folder, fontFileName);

        // If launched from repo root
        yield return Path.Combine(Environment.CurrentDirectory, "Build", "CommonAssets", "Fonts", folder, fontFileName);
    }

    private static string? TryResolveFontDirectory(string folder)
    {
        // Walk up from AppContext.BaseDirectory and look for CommonAssets/Fonts/{folder}.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 16 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "CommonAssets", "Fonts", folder);
            if (Directory.Exists(candidate))
                return candidate;
        }

        // Walk up from current working directory as well (covers start tasks that set /D).
        dir = new DirectoryInfo(Environment.CurrentDirectory);
        for (int i = 0; i < 16 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "Build", "CommonAssets", "Fonts", folder);
            if (Directory.Exists(candidate))
                return candidate;

            candidate = Path.Combine(dir.FullName, "CommonAssets", "Fonts", folder);
            if (Directory.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static void TryRecreateFontDeviceTexture(ImGuiController controller)
    {
        // Silk.NET's ImGuiController typically exposes a method to rebuild the font texture.
        // Use reflection to tolerate version differences.
        try
        {
            var type = typeof(ImGuiController);

            var method =
                type.GetMethod("RecreateFontDeviceTexture", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? type.GetMethod("CreateFontTexture", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? type.GetMethod("RebuildFontAtlas", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            method?.Invoke(controller, null);
        }
        catch
        {
            // If the controller doesn't support it, it may rebuild internally on its next update/render.
        }
    }

    public static void DetachInputHandlers(ImGuiController? controller)
    {
        if (controller is null)
            return;

        try
        {
            var keyboard = GetKeyboard(controller);
            if (keyboard is null)
                return;

            var keyDown = CreateStaticDelegate<Action<IKeyboard, Key, int>>("OnKeyDown");
            var keyUp = CreateStaticDelegate<Action<IKeyboard, Key, int>>("OnKeyUp");
            var keyChar = CreateInstanceDelegate<Action<IKeyboard, char>>(controller, "OnKeyChar");

            if (keyDown is not null)
                keyboard.KeyDown -= keyDown;
            if (keyUp is not null)
                keyboard.KeyUp -= keyUp;
            if (keyChar is not null)
                keyboard.KeyChar -= keyChar;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed detaching ImGuiController input hooks: {ex.Message}");
        }
    }

    private static IKeyboard? GetKeyboard(ImGuiController controller)
    {
        var controllerType = typeof(ImGuiController);
        var field = controllerType.GetField("_keyboard", InstancePrivate);
        return field?.GetValue(controller) as IKeyboard;
    }

    private static TDelegate? CreateStaticDelegate<TDelegate>(string methodName) where TDelegate : Delegate
    {
        var controllerType = typeof(ImGuiController);
        var method = controllerType.GetMethod(methodName, StaticPrivate);
        return method is null ? null : (TDelegate)Delegate.CreateDelegate(typeof(TDelegate), method);
    }

    private static TDelegate? CreateInstanceDelegate<TDelegate>(ImGuiController controller, string methodName) where TDelegate : Delegate
    {
        var controllerType = typeof(ImGuiController);
        var method = controllerType.GetMethod(methodName, InstancePrivate);
        return method is null ? null : (TDelegate)Delegate.CreateDelegate(typeof(TDelegate), controller, method);
    }
}
