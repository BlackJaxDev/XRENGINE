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

    private static readonly HashSet<nint> _latoLoadedContexts = [];

    public static unsafe bool TryUseLatoAsDefaultFont(ImGuiController controller, float sizePixels = 18.0f)
    {
        if (controller is null)
            return false;

        try
        {
            controller.MakeCurrent();

            nint context = ImGui.GetCurrentContext();
            if (context == 0)
                return false;

            if (_latoLoadedContexts.Contains(context))
                return true;

            string? fontPath = TryResolveLatoRegularFontPath();
            if (string.IsNullOrWhiteSpace(fontPath) || !File.Exists(fontPath))
                return false;

            var io = ImGui.GetIO();

            // ImGui.NET exposes FontDefault as read-only in this version.
            // To make Lato the default font for all widgets, ensure it's the first font in the atlas.
            io.Fonts.Clear();
            var font = io.Fonts.AddFontFromFileTTF(fontPath, sizePixels);
            if (font.NativePtr == null)
                return false;
            io.Fonts.Build();

            TryRecreateFontDeviceTexture(controller);

            _latoLoadedContexts.Add(context);
            Debug.Out($"ImGui font: Lato loaded ({Path.GetFileName(fontPath)}) @ {sizePixels:0.#}px");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed to load Lato ImGui font: {ex.Message}");
            return false;
        }
    }

    private static string? TryResolveLatoRegularFontPath()
    {
        const string fontFileName = "Lato-Regular.ttf";

        foreach (string candidate in GetFontPathCandidates(fontFileName))
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

        // Fallback: if we can find a Lato folder, pick a suitable TTF.
        string? latoDir = TryResolveLatoDirectory();
        if (string.IsNullOrWhiteSpace(latoDir) || !Directory.Exists(latoDir))
            return null;

        string regular = Path.Combine(latoDir, fontFileName);
        if (File.Exists(regular))
            return regular;

        return Directory.EnumerateFiles(latoDir, "*.ttf", SearchOption.TopDirectoryOnly)
            .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static IEnumerable<string> GetFontPathCandidates(string fontFileName)
    {
        // Typical editor working dir: <repo>/XREngine.Editor
        yield return Path.Combine(Environment.CurrentDirectory, "..", "Build", "CommonAssets", "Fonts", "Lato", fontFileName);

        // Typical runtime output dir: <repo>/Build/Editor/.../net*/
        yield return Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "CommonAssets", "Fonts", "Lato", fontFileName);

        // If launched from repo root
        yield return Path.Combine(Environment.CurrentDirectory, "Build", "CommonAssets", "Fonts", "Lato", fontFileName);
    }

    private static string? TryResolveLatoDirectory()
    {
        // Walk up from AppContext.BaseDirectory and look for a sibling CommonAssets/Fonts/Lato folder.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 16 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "CommonAssets", "Fonts", "Lato");
            if (Directory.Exists(candidate))
                return candidate;
        }

        // Walk up from current working directory as well (covers start tasks that set /D).
        dir = new DirectoryInfo(Environment.CurrentDirectory);
        for (int i = 0; i < 16 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "Build", "CommonAssets", "Fonts", "Lato");
            if (Directory.Exists(candidate))
                return candidate;

            candidate = Path.Combine(dir.FullName, "CommonAssets", "Fonts", "Lato");
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
