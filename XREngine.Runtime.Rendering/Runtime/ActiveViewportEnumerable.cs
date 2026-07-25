using System.Collections;
using XREngine.Rendering;

namespace XREngine;

/// <summary>
/// Allocation-free enumeration over active engine viewports.
/// </summary>
public readonly struct ActiveViewportEnumerable : IEnumerable<XRViewport>
{
    private readonly EventList<XRWindow>? _windows;
    private readonly XRWindow? _singleWindow;
    private readonly RuntimeEngine.EViewportEnumerationMode _mode;
    private readonly bool _singleWindowOnly;

    internal ActiveViewportEnumerable(
        EventList<XRWindow> windows,
        RuntimeEngine.EViewportEnumerationMode mode)
    {
        _windows = windows;
        _singleWindow = null;
        _mode = mode;
        _singleWindowOnly = false;
    }

    internal ActiveViewportEnumerable(
        XRWindow? window,
        RuntimeEngine.EViewportEnumerationMode mode)
    {
        _windows = null;
        _singleWindow = window;
        _mode = mode;
        _singleWindowOnly = true;
    }

    public Enumerator GetEnumerator()
        => new(_windows, _singleWindow, _mode, _singleWindowOnly);

    IEnumerator<XRViewport> IEnumerable<XRViewport>.GetEnumerator()
        => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();

    public struct Enumerator : IEnumerator<XRViewport>
    {
        private readonly EventList<XRWindow>? _windows;
        private readonly XRWindow? _singleWindow;
        private readonly RuntimeEngine.EViewportEnumerationMode _mode;
        private readonly bool _singleWindowOnly;
        private int _windowIndex;
        private int _viewportIndex;
        private int _eyeIndex;
        private XRViewport? _current;

        internal Enumerator(
            EventList<XRWindow>? windows,
            XRWindow? singleWindow,
            RuntimeEngine.EViewportEnumerationMode mode,
            bool singleWindowOnly)
        {
            _windows = windows;
            _singleWindow = singleWindow;
            _mode = mode;
            _singleWindowOnly = singleWindowOnly;
            _windowIndex = 0;
            _viewportIndex = -1;
            _eyeIndex = -1;
            _current = null;
        }

        public readonly XRViewport Current => _current!;
        readonly object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (_eyeIndex < 0 && MoveNextWindowViewport())
                return true;

            if (_mode != RuntimeEngine.EViewportEnumerationMode.IncludeVrEyeViewports)
                return false;

            while (_eyeIndex < 2)
            {
                XRViewport? candidate = _eyeIndex++ == 0
                    ? RuntimeEngine.VRState.LeftEyeViewport
                    : RuntimeEngine.VRState.RightEyeViewport;
                if (candidate is not null && ShouldIncludeEyeViewport(candidate))
                {
                    _current = candidate;
                    return true;
                }
            }

            _current = null;
            return false;
        }

        private bool MoveNextWindowViewport()
        {
            if (_singleWindowOnly)
            {
                XRWindow? window = _singleWindow;
                int nextIndex = _viewportIndex + 1;
                if (window is not null && nextIndex < window.Viewports.Count)
                {
                    _viewportIndex = nextIndex;
                    _current = window.Viewports[nextIndex];
                    return true;
                }

                _eyeIndex = 0;
                return false;
            }

            EventList<XRWindow>? windows = _windows;
            if (windows is null)
            {
                _eyeIndex = 0;
                return false;
            }

            while (_windowIndex < windows.Count)
            {
                XRWindow window = windows[_windowIndex];
                int nextIndex = _viewportIndex + 1;
                if (nextIndex < window.Viewports.Count)
                {
                    _viewportIndex = nextIndex;
                    _current = window.Viewports[nextIndex];
                    return true;
                }

                _windowIndex++;
                _viewportIndex = -1;
            }

            _eyeIndex = 0;
            return false;
        }

        private readonly bool ShouldIncludeEyeViewport(XRViewport viewport)
        {
            if (_singleWindowOnly)
            {
                XRWindow? window = _singleWindow;
                return window is not null &&
                       ReferenceEquals(viewport.Window, window) &&
                       !window.Viewports.Contains(viewport);
            }

            EventList<XRWindow>? windows = _windows;
            if (windows is null)
                return true;

            for (int windowIndex = 0; windowIndex < windows.Count; windowIndex++)
            {
                EventList<XRViewport> viewports = windows[windowIndex].Viewports;
                for (int viewportIndex = 0; viewportIndex < viewports.Count; viewportIndex++)
                    if (ReferenceEquals(viewports[viewportIndex], viewport))
                        return false;
            }

            return true;
        }

        public void Reset()
        {
            _windowIndex = 0;
            _viewportIndex = -1;
            _eyeIndex = -1;
            _current = null;
        }

        public readonly void Dispose()
        {
        }
    }
}
