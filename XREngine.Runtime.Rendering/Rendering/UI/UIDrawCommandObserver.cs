using XREngine.Data.Core;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.UI;

/// <summary>Keeps command eligibility observable when no draw payload is retained.</summary>
internal sealed class UIDrawCommandObserver : IDisposable
{
    private readonly RenderCommandMesh2D _command;
    private readonly Action _changed;
    private readonly Action _dispatch;
    private int _disposed;
    private int _queued;

    internal UIDrawCommandObserver(RenderCommandMesh2D command, Action changed)
    {
        _command = command;
        _changed = changed;
        _dispatch = Dispatch;
        command.PropertyChanged += PropertyChanged;
    }

    private void PropertyChanged(object? sender, IXRPropertyChangedEventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        if (RuntimeRenderingHostServices.FrameTiming.IsAppThread)
            Dispatch();
        else if (Interlocked.Exchange(ref _queued, 1) == 0)
            RuntimeEngine.EnqueueAppThreadTask(_dispatch, "UI.DrawCommand.SourceChanged");
    }

    private void Dispatch()
    {
        Volatile.Write(ref _queued, 0);
        if (Volatile.Read(ref _disposed) == 0)
            _changed();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _command.PropertyChanged -= PropertyChanged;
    }
}
