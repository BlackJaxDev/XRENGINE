using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;

namespace XREngine.Runtime.Platform.Desktop.Windowing;

internal sealed class DesktopBorderlessResizeHook : IDesktopInteractiveResizeHook
{
    private const int Grip = 8;
    private const int MinimumWidth = 320;
    private const int MinimumHeight = 200;
    private DesktopSilkWindowBackend? _window;
    private IMouse? _mouse;
    private Edges _hover;
    private Edges _drag;
    private Vector2 _dragStartPointer;
    private Vector2D<int> _dragStartSize;
    private Vector2D<int> _dragStartPosition;

    public void Install(DesktopSilkWindowBackend window) => _window = window;

    public void OnInputCreated(IInputContext input)
    {
        if (_mouse is not null || input.Mice.Count == 0)
            return;
        _mouse = input.Mice[0];
        _mouse.MouseDown += OnMouseDown;
        _mouse.MouseUp += OnMouseUp;
        _mouse.MouseMove += OnMouseMove;
    }

    private void OnMouseDown(IMouse mouse, MouseButton button)
    {
        if (button != MouseButton.Left || _window is not { } window)
            return;
        Edges edges = GetEdges(mouse.Position, window.NativeWindow.Size);
        if (edges == Edges.None)
            return;
        _drag = edges;
        _dragStartPointer = mouse.Position;
        _dragStartSize = window.NativeWindow.Size;
        _dragStartPosition = window.NativeWindow.Position;
        window.BeginNativeResize();
    }

    private void OnMouseUp(IMouse _, MouseButton button)
    {
        if (button != MouseButton.Left || _drag == Edges.None)
            return;
        _drag = Edges.None;
        _window?.EndNativeResize();
    }

    private void OnMouseMove(IMouse _, Vector2 pointer)
    {
        if (_window is not { } window)
            return;
        if (_drag != Edges.None)
        {
            ApplyDrag(window, pointer);
            return;
        }
        Edges edges = GetEdges(pointer, window.NativeWindow.Size);
        if (edges == _hover)
            return;
        _hover = edges;
        SetCursor(edges);
    }

    private void ApplyDrag(DesktopSilkWindowBackend window, Vector2 pointer)
    {
        int dx = (int)MathF.Round(pointer.X - _dragStartPointer.X);
        int dy = (int)MathF.Round(pointer.Y - _dragStartPointer.Y);
        int x = _dragStartPosition.X;
        int y = _dragStartPosition.Y;
        int width = _dragStartSize.X;
        int height = _dragStartSize.Y;

        if ((_drag & Edges.Left) != 0)
        {
            x += dx;
            width -= dx;
            if (width < MinimumWidth) { x -= MinimumWidth - width; width = MinimumWidth; }
        }
        else if ((_drag & Edges.Right) != 0)
            width = Math.Max(MinimumWidth, width + dx);

        if ((_drag & Edges.Top) != 0)
        {
            y += dy;
            height -= dy;
            if (height < MinimumHeight) { y -= MinimumHeight - height; height = MinimumHeight; }
        }
        else if ((_drag & Edges.Bottom) != 0)
            height = Math.Max(MinimumHeight, height + dy);

        Vector2D<int> position = new(x, y);
        Vector2D<int> size = new(width, height);
        if (position == window.NativeWindow.Position && size == window.NativeWindow.Size)
            return;
        window.NativeWindow.Position = position;
        window.NativeWindow.Size = size;
        window.UpdateNativeResize();
    }

    private static Edges GetEdges(Vector2 point, Vector2D<int> size)
    {
        Edges edges = Edges.None;
        if (point.X <= Grip) edges |= Edges.Left;
        else if (point.X >= size.X - Grip) edges |= Edges.Right;
        if (point.Y <= Grip) edges |= Edges.Top;
        else if (point.Y >= size.Y - Grip) edges |= Edges.Bottom;
        return edges;
    }

    private void SetCursor(Edges edges)
    {
        if (_mouse?.Cursor is not { } cursor)
            return;
        StandardCursor target = edges switch
        {
            Edges.Left or Edges.Right => StandardCursor.HResize,
            Edges.Top or Edges.Bottom => StandardCursor.VResize,
            Edges.TopLeft or Edges.BottomRight => StandardCursor.NwseResize,
            Edges.TopRight or Edges.BottomLeft => StandardCursor.NeswResize,
            _ => StandardCursor.Default,
        };
        if (cursor.IsSupported(target))
        {
            cursor.Type = CursorType.Standard;
            cursor.StandardCursor = target;
        }
    }

    public void Dispose()
    {
        if (_mouse is { } mouse)
        {
            mouse.MouseDown -= OnMouseDown;
            mouse.MouseUp -= OnMouseUp;
            mouse.MouseMove -= OnMouseMove;
            SetCursor(Edges.None);
        }
        _mouse = null;
        _window = null;
        _drag = Edges.None;
    }

    [Flags]
    private enum Edges
    {
        None = 0,
        Left = 1,
        Right = 2,
        Top = 4,
        Bottom = 8,
        TopLeft = Top | Left,
        TopRight = Top | Right,
        BottomLeft = Bottom | Left,
        BottomRight = Bottom | Right,
    }
}
