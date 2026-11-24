using XREngine.Imaging;
using XREngine.Input.Devices;
using XREngine.Rendering;

namespace XREngine.Editor.WindowRecording;

/// <summary>
/// Owns the editor's global screenshot shortcut. <c>F12</c> in any editor window saves each
/// viewport of that window as a PNG file. The shortcut reads native window input, like
/// <see cref="EditorWindowVideoRecorder"/>, so it works with every pawn and game mode, also
/// when no pawn is possessed.
/// </summary>
public static class EditorWindowScreenshot
{
    private static bool _initialized;

    /// <summary>Subscribes the shortcut to native window key input. Call it once at editor startup.</summary>
    public static void Initialize()
    {
        if (_initialized)
            return;

        XRWindow.AnyWindowKeyDown += OnAnyWindowKeyDown;
        _initialized = true;
    }

    /// <summary>Removes the shortcut subscription.</summary>
    public static void Shutdown()
    {
        if (!_initialized)
            return;

        XRWindow.AnyWindowKeyDown -= OnAnyWindowKeyDown;
        _initialized = false;
    }

    /// <summary>
    /// Returns true when <paramref name="key"/> with the given modifier state is the screenshot
    /// shortcut. <c>Shift+F12</c> is the recording shortcut, so Shift must be up.
    /// </summary>
    internal static bool IsShortcut(EKey key, bool leftShiftDown, bool rightShiftDown)
        => key == EKey.F12 && !leftShiftDown && !rightShiftDown;

    /// <summary>
    /// Saves every viewport of <paramref name="window"/> as a PNG after the window renders its
    /// next frame. The files go to a new time-stamped folder on the desktop, which is named
    /// after the render pipeline of the first viewport.
    /// </summary>
    public static void Request(XRWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        // The renderer reads back pixels only on the render thread. The window raises
        // PostRenderViewportsCallback there, after all its viewports rendered, so a one-shot
        // handler on that event does the capture at the correct point in the frame.
        int started = 0;
        Action? handler = null;
        handler = () =>
        {
            if (Interlocked.Exchange(ref started, 1) != 0)
                return;

            window.PostRenderViewportsCallback -= handler;
            CaptureViewports(window);
        };
        window.PostRenderViewportsCallback += handler;
    }

    private static void OnAnyWindowKeyDown(XRWindow window, EKey key)
    {
        if (!IsShortcut(key, window.IsKeyPressed(EKey.ShiftLeft), window.IsKeyPressed(EKey.ShiftRight)))
            return;

        Request(window);
    }

    private static void CaptureViewports(XRWindow window)
    {
        AbstractRenderer? renderer = window.Renderer;
        if (renderer is null)
        {
            Debug.LogWarning("[Screenshot] The window has no renderer, so no screenshot was taken.");
            return;
        }

        int viewportCount = window.Viewports.Count;
        if (viewportCount == 0)
        {
            Debug.LogWarning("[Screenshot] The window has no viewports, so no screenshot was taken.");
            return;
        }

        string pipelineName = window.Viewports[0].RenderPipeline?.GetType().Name ?? "Screenshot";
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string folder = Path.Combine(desktop, $"{pipelineName}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}");

        for (int i = 0; i < viewportCount; i++)
        {
            XRViewport viewport = window.Viewports[i];
            string fileName = viewportCount == 1 ? "Screenshot.png" : $"Screenshot_{i}.png";
            string path = Path.Combine(folder, fileName);
            renderer.GetScreenshotAsync(viewport.Region, false, (image, _) => SaveImage(image, path));
        }
    }

    /// <summary>
    /// Encodes the readback image as PNG and writes it to <paramref name="path"/>. The renderer
    /// calls this when the readback completes, which can be on another thread. A failure goes
    /// to the log, because an exception here has no caller to catch it.
    /// </summary>
    private static void SaveImage(RuntimeImage? image, string path)
    {
        if (image is null)
        {
            Debug.LogWarning($"[Screenshot] The screenshot readback returned no image for '{path}'.");
            return;
        }

        try
        {
            using (image)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, RuntimeImageCodecs.Require().EncodePng(image));
            }

            Debug.Out($"[Screenshot] Saved '{path}'.");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Screenshot] Cannot save '{path}': {ex.Message}");
        }
    }
}
