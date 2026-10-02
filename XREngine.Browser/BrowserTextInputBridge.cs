using System.Numerics;
using XREngine.Rendering;
using XREngine.Rendering.UI;

namespace XREngine.Browser;

/// <summary>
/// Keeps a browser text service bound to the engine's focused text widget. Geometry
/// is normalized to the rendered canvas so CSS layout and backing DPR stay separate.
/// </summary>
internal sealed class BrowserTextInputBridge
{
    private const int MaximumTextLength = 16_384;
    private static int _nextGeneration;
    private UITextInputComponent? _target;
    private string? _observedValue;
    private int _generation;
    private int _contentVersion;

    public int Generation => _generation;
    public int ContentVersion => _contentVersion;
    public string Value => _target?.Text ?? string.Empty;
    public string Label => _target?.Name ?? _target?.SceneNode?.Name ?? "Engine text input";
    public int Cursor => _target?.CursorPosition ?? 0;
    public bool SingleLine => _target?.SingleLineMode ?? true;
    public bool ReadOnly => _target?.RegisterInputsOnFocus == false;
    public float X { get; private set; } = -1;
    public float Y { get; private set; } = -1;
    public float Width { get; private set; }
    public float Height { get; private set; }

    public int Refresh(object? focusedInteractable, XRViewport? viewport)
    {
        UITextInputComponent? target = focusedInteractable as UITextInputComponent;
        if (target is not { IsActiveInHierarchy: true, IsFocused: true })
            target = null;
        if (!ReferenceEquals(target, _target))
        {
            _target = target;
            _generation = target is null ? 0 : Interlocked.Increment(ref _nextGeneration);
            _observedValue = null;
        }

        if (target is not null && !ReferenceEquals(target.Text, _observedValue))
        {
            _observedValue = target.Text;
            _contentVersion++;
        }

        UpdateGeometry(viewport);
        return _generation;
    }

    public void Clear()
    {
        _target = null;
        _observedValue = null;
        _generation = 0;
        X = Y = -1;
        Width = Height = 0;
    }

    public bool Edit(int generation, object? focusedInteractable, string value, int selectionStart, int selectionEnd)
    {
        if (!Owns(generation, focusedInteractable) || ReadOnly || value is null || value.Length > MaximumTextLength)
            return false;
        return _target!.UserReplaceText(value, selectionStart, selectionEnd);
    }

    public bool Select(int generation, object? focusedInteractable, int cursor)
    {
        if (!Owns(generation, focusedInteractable) || cursor < 0 || cursor > _target!.Text.Length)
            return false;
        _target.CursorPosition = cursor;
        return true;
    }

    public bool Action(int generation, object? focusedInteractable, bool submit)
    {
        if (!Owns(generation, focusedInteractable) || ReadOnly || !_target!.SingleLineMode)
            return false;
        if (submit)
            _target.UserSubmit();
        else
            _target.UserCancel();
        return true;
    }

    private bool Owns(int generation, object? focusedInteractable)
        => generation != 0 && generation == _generation && _target is { IsActiveInHierarchy: true, IsFocused: true } &&
           ReferenceEquals(_target, focusedInteractable);

    private void UpdateGeometry(XRViewport? viewport)
    {
        X = Y = -1;
        Width = Height = 0;
        UITextInputComponent? target = _target;
        if (target is null || viewport is null || target.UserInterfaceCanvas is not { } canvas)
            return;

        UIBoundableTransform transform = target.BoundableTransform;
        Vector2 size = transform.ActualSize;
        if (size.X <= 0 || size.Y <= 0)
            return;

        Vector2 p0, p1, p2, p3;
        if (canvas.CanvasTransform.DrawSpace == ECanvasDrawSpace.Screen)
        {
            Vector2 canvasSize = canvas.CanvasTransform.ActualSize;
            if (canvasSize.X <= 0 || canvasSize.Y <= 0)
                return;
            p0 = transform.LocalToCanvas(Vector2.Zero) / canvasSize;
            p1 = transform.LocalToCanvas(new Vector2(size.X, 0)) / canvasSize;
            p2 = transform.LocalToCanvas(new Vector2(0, size.Y)) / canvasSize;
            p3 = transform.LocalToCanvas(size) / canvasSize;
        }
        else
        {
            XRCamera? camera = canvas.CanvasTransform.DrawSpace == ECanvasDrawSpace.Camera
                ? canvas.CanvasTransform.CameraSpaceCamera ?? viewport.ActiveCamera
                : viewport.ActiveCamera;
            if (camera is null)
                return;
            Vector3 v0 = camera.WorldToNormalizedViewportCoordinate(transform.LocalToWorld(new Vector3(0, 0, 0)), true);
            Vector3 v1 = camera.WorldToNormalizedViewportCoordinate(transform.LocalToWorld(new Vector3(size.X, 0, 0)), true);
            Vector3 v2 = camera.WorldToNormalizedViewportCoordinate(transform.LocalToWorld(new Vector3(0, size.Y, 0)), true);
            Vector3 v3 = camera.WorldToNormalizedViewportCoordinate(transform.LocalToWorld(new Vector3(size.X, size.Y, 0)), true);
            if (v0.Z is < 0 or > 1 || v1.Z is < 0 or > 1 || v2.Z is < 0 or > 1 || v3.Z is < 0 or > 1)
                return;
            p0 = new Vector2(v0.X, v0.Y);
            p1 = new Vector2(v1.X, v1.Y);
            p2 = new Vector2(v2.X, v2.Y);
            p3 = new Vector2(v3.X, v3.Y);
        }

        float left = MathF.Min(MathF.Min(p0.X, p1.X), MathF.Min(p2.X, p3.X));
        float right = MathF.Max(MathF.Max(p0.X, p1.X), MathF.Max(p2.X, p3.X));
        float bottom = MathF.Min(MathF.Min(p0.Y, p1.Y), MathF.Min(p2.Y, p3.Y));
        float top = MathF.Max(MathF.Max(p0.Y, p1.Y), MathF.Max(p2.Y, p3.Y));
        if (!float.IsFinite(left) || !float.IsFinite(right) || !float.IsFinite(bottom) || !float.IsFinite(top) ||
            right <= left || top <= bottom)
            return;
        X = left;
        Y = 1 - top;
        Width = right - left;
        Height = top - bottom;
    }
}
