using System.Collections;
using XREngine.Rendering;

namespace XREngine;

/// <summary>
/// Allocation-free enumeration over active window and viewport pairs.
/// </summary>
public readonly struct ActiveWindowViewportEnumerable
    : IEnumerable<(XRWindow Window, XRViewport Viewport)>
{
    private readonly EventList<XRWindow> _windows;
    private readonly RuntimeEngine.EViewportEnumerationMode _mode;

    internal ActiveWindowViewportEnumerable(
        EventList<XRWindow> windows,
        RuntimeEngine.EViewportEnumerationMode mode)
    {
        _windows = windows;
        _mode = mode;
    }

    public Enumerator GetEnumerator()
        => new(_windows, _mode);

    IEnumerator<(XRWindow Window, XRViewport Viewport)>
        IEnumerable<(XRWindow Window, XRViewport Viewport)>.GetEnumerator()
        => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();

    public struct Enumerator : IEnumerator<(XRWindow Window, XRViewport Viewport)>
    {
        private readonly EventList<XRWindow> _windows;
        private readonly RuntimeEngine.EViewportEnumerationMode _mode;
        private int _windowIndex;
        private int _viewportIndex;
        private int _eyeIndex;
        private (XRWindow Window, XRViewport Viewport) _current;

        internal Enumerator(
            EventList<XRWindow> windows,
            RuntimeEngine.EViewportEnumerationMode mode)
        {
            _windows = windows;
            _mode = mode;
            _windowIndex = 0;
            _viewportIndex = -1;
            _eyeIndex = -1;
            _current = default;
        }

        public readonly (XRWindow Window, XRViewport Viewport) Current => _current;
        readonly object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (_eyeIndex < 0 && MoveNextWindowViewport())
                return true;

            if (_mode != RuntimeEngine.EViewportEnumerationMode.IncludeVrEyeViewports)
                return false;

            while (_eyeIndex < 2)
            {
                XRViewport? viewport = _eyeIndex++ == 0
                    ? RuntimeEngine.VRState.LeftEyeViewport
                    : RuntimeEngine.VRState.RightEyeViewport;
                if (viewport?.Window is not XRWindow window ||
                    window.Viewports.Contains(viewport))
                {
                    continue;
                }

                _current = (window, viewport);
                return true;
            }

            _current = default;
            return false;
        }

        private bool MoveNextWindowViewport()
        {
            while (_windowIndex < _windows.Count)
            {
                XRWindow window = _windows[_windowIndex];
                int nextIndex = _viewportIndex + 1;
                if (nextIndex < window.Viewports.Count)
                {
                    _viewportIndex = nextIndex;
                    _current = (window, window.Viewports[nextIndex]);
                    return true;
                }

                _windowIndex++;
                _viewportIndex = -1;
            }

            _eyeIndex = 0;
            return false;
        }

        public void Reset()
        {
            _windowIndex = 0;
            _viewportIndex = -1;
            _eyeIndex = -1;
            _current = default;
        }

        public readonly void Dispose()
        {
        }
    }
}
