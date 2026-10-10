using XREngine.Rendering;

namespace XREngine.Runtime.Platform.Desktop;

/// <summary>Owns the desktop GL submit trace writer and its file stream.</summary>
internal sealed class DesktopGLSubmitTraceLog(FileStream stream, StreamWriter writer) : IRuntimeOwnedTextLog
{
    public TextWriter Writer => writer;

    public void Dispose()
    {
        try { writer.Dispose(); } catch { }
        try { stream.Dispose(); } catch { }
    }
}
