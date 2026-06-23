using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;

namespace XREngine.Rendering;

internal sealed class EngineBorderlessInteractiveResizeStrategy : InteractiveResizeStrategyBase
{
    private const int EdgeThicknessPixels = 8;
    private const int MinWidth = 320;
    private const int MinHeight = 200;

    private IMouse? _mouse;
    private ResizeEdges _hoverEdges;
    private ResizeEdges _dragEdges;
    private Vector2 _dragStartMouse;
    private Vector2D<int> _dragStartWindowSize;
    private Vector2D<int> _dragStartWindowPosition;

    public override EInteractiveWindowResizeStrategy Kind => EInteractiveWindowResizeStrategy.EngineBorderlessResize;

    public override void Install(XRWindow window)
    {
        base.Install(window);

        if (window.UseNativeTitleBar)
        {
            Debug.RenderingWarning(
                "[InteractiveResize] Engine borderless resize requested for native-title-bar window={0}; window chrome is fixed at creation time, so resize grips may overlap native chrome.",
                window.GetHashCode());
        }
    }

    public override void Uninstall()
    {
        DetachMouse();
        base.Uninstall();
    }

    public override void OnInputCreated(IInputContext input)
    {
        if (_mouse is not null || input.Mice.Count <= 0)
            return;

        _mouse = input.Mice[0];
        _mouse.MouseMove += OnMouseMove;
        _mouse.MouseDown += OnMouseDown;
        _mouse.MouseUp += OnMouseUp;

        Debug.Rendering(
            "[InteractiveResize] Engine borderless resize input attached window={0}.",
            Window?.GetHashCode() ?? 0);
    }

    private void DetachMouse()
    {
        if (_mouse is null)
            return;

        try
        {
            _mouse.MouseMove -= OnMouseMove;
            _mouse.MouseDown -= OnMouseDown;
            _mouse.MouseUp -= OnMouseUp;
            SetCursor(StandardCursor.Default);
        }
        catch
        {
        }

        _mouse = null;
        _hoverEdges = ResizeEdges.None;
        _dragEdges = ResizeEdges.None;
    }

    private void OnMouseDown(IMouse mouse, MouseButton button)
    {
        if (button != MouseButton.Left)
            return;

        XRWindow? window = Window;
        if (window is null)
            return;

        ResizeEdges edges = GetEdges(mouse.Position, window.Window.Size);
        if (edges == ResizeEdges.None)
            return;

        _dragEdges = edges;
        _dragStartMouse = mouse.Position;
        _dragStartWindowSize = window.Window.Size;
        _dragStartWindowPosition = window.Window.Position;
        window.InteractiveResizeDiagnostics.RecordCallback("engine-borderless-start");
    }

    private void OnMouseUp(IMouse mouse, MouseButton button)
    {
        if (button != MouseButton.Left || _dragEdges == ResizeEdges.None)
            return;

        _dragEdges = ResizeEdges.None;
        Window?.QueueCurrentFramebufferResize("engine-borderless-release");
        RecordCallbackAndRender("engine-borderless-release");
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        XRWindow? window = Window;
        if (window is null)
            return;

        if (_dragEdges != ResizeEdges.None)
        {
            ApplyDragResize(window, position);
            return;
        }

        ResizeEdges edges = GetEdges(position, window.Window.Size);
        if (edges == _hoverEdges)
            return;

        _hoverEdges = edges;
        SetCursor(GetCursor(edges));
    }

    private void ApplyDragResize(XRWindow window, Vector2 currentMouse)
    {
        int dx = (int)MathF.Round(currentMouse.X - _dragStartMouse.X);
        int dy = (int)MathF.Round(currentMouse.Y - _dragStartMouse.Y);

        int x = _dragStartWindowPosition.X;
        int y = _dragStartWindowPosition.Y;
        int width = _dragStartWindowSize.X;
        int height = _dragStartWindowSize.Y;

        if ((_dragEdges & ResizeEdges.Left) != 0)
        {
            x = _dragStartWindowPosition.X + dx;
            width = _dragStartWindowSize.X - dx;
            if (width < MinWidth)
            {
                x -= MinWidth - width;
                width = MinWidth;
            }
        }
        else if ((_dragEdges & ResizeEdges.Right) != 0)
        {
            width = Math.Max(MinWidth, _dragStartWindowSize.X + dx);
        }

        if ((_dragEdges & ResizeEdges.Top) != 0)
        {
            y = _dragStartWindowPosition.Y + dy;
            height = _dragStartWindowSize.Y - dy;
            if (height < MinHeight)
            {
                y -= MinHeight - height;
                height = MinHeight;
            }
        }
        else if ((_dragEdges & ResizeEdges.Bottom) != 0)
        {
            height = Math.Max(MinHeight, _dragStartWindowSize.Y + dy);
        }

        Vector2D<int> newPosition = new(x, y);
        Vector2D<int> newSize = new(width, height);
        if (newPosition == window.Window.Position && newSize == window.Window.Size)
            return;

        window.Window.Position = newPosition;
        window.Window.Size = newSize;
        window.QueueFramebufferResize(window.ConvertWindowSizeToFramebufferSize(newSize), "engine-borderless-drag");
        window.InteractiveResizeDiagnostics.RecordCallback("engine-borderless-drag");
        window.RenderInteractiveResizeFrame("engine-borderless-drag");
    }

    private static ResizeEdges GetEdges(Vector2 position, Vector2D<int> size)
    {
        ResizeEdges edges = ResizeEdges.None;

        if (position.X <= EdgeThicknessPixels)
            edges |= ResizeEdges.Left;
        else if (position.X >= size.X - EdgeThicknessPixels)
            edges |= ResizeEdges.Right;

        if (position.Y <= EdgeThicknessPixels)
            edges |= ResizeEdges.Top;
        else if (position.Y >= size.Y - EdgeThicknessPixels)
            edges |= ResizeEdges.Bottom;

        return edges;
    }

    private static StandardCursor GetCursor(ResizeEdges edges)
        => edges switch
        {
            ResizeEdges.Left or ResizeEdges.Right => StandardCursor.HResize,
            ResizeEdges.Top or ResizeEdges.Bottom => StandardCursor.VResize,
            ResizeEdges.TopLeft or ResizeEdges.BottomRight => StandardCursor.NwseResize,
            ResizeEdges.TopRight or ResizeEdges.BottomLeft => StandardCursor.NeswResize,
            _ => StandardCursor.Default,
        };

    private void SetCursor(StandardCursor cursor)
    {
        IMouse? mouse = _mouse;
        if (mouse?.Cursor is null)
            return;

        try
        {
            if (!mouse.Cursor.IsSupported(cursor))
                return;

            mouse.Cursor.Type = CursorType.Standard;
            mouse.Cursor.StandardCursor = cursor;
        }
        catch
        {
        }
    }

    [Flags]
    private enum ResizeEdges
    {
        None = 0,
        Left = 1 << 0,
        Right = 1 << 1,
        Top = 1 << 2,
        Bottom = 1 << 3,
        TopLeft = Top | Left,
        TopRight = Top | Right,
        BottomLeft = Bottom | Left,
        BottomRight = Bottom | Right,
    }
}
