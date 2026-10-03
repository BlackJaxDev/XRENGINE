// Draw-device behavior adapted from Silk.NET ImGuiController v2.23.0.
// Copyright The .NET Foundation. Licensed under the MIT license.
// https://github.com/dotnet/Silk.NET/blob/v2.23.0/src/OpenGL/Extensions/Silk.NET.OpenGL.Extensions.ImGui/ImGuiController.cs
using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using Silk.NET.OpenGL;
using XREngine.Input.Devices;

namespace XREngine.Rendering.OpenGL;

/// <summary>Owns the OpenGL ImGui context, device objects, and independent editor input cursor.</summary>
internal sealed unsafe partial class OpenGLImGuiController : IDisposable
{
    private readonly GL _gl;
    private readonly XRWindow _window;
    private bool _frameBegun;
    private bool _disposed;
    private bool _leftCtrl, _rightCtrl, _leftShift, _rightShift;
    private bool _leftAlt, _rightAlt, _leftSuper, _rightSuper;
    private ulong _lastInputSequence;
    private readonly Dictionary<uint, ulong> _viewportInputSequences = [];
    private readonly List<WindowInputEvent> _orderedInputScratch = new(64);
    private uint _program;
    private uint _vertexBuffer;
    private uint _indexBuffer;
    private uint _fontTexture;
    private int _textureLocation;
    private int _projectionLocation;
    private int _positionLocation;
    private int _uvLocation;
    private int _colorLocation;

    public nint Context { get; }
    public bool IsFrameBegun => _frameBegun;

    public OpenGLImGuiController(GL gl, XRWindow window, Action configureIo, Action? rollbackConfiguration = null)
    {
        _gl = gl;
        _window = window;
        if (window.DesktopGlContext is null)
            throw new NotSupportedException("The OpenGL editor requires a registered desktop GL context service.");

        Context = ImGui.CreateContext();
        try
        {
            MakeCurrent();
            ImGui.StyleColorsDark();
            configureIo();
            ImGui.GetIO().BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
            CreateDeviceResources();
        }
        catch
        {
            // Platform callbacks and monitor storage must be released while this
            // ImGui context is still alive, even if GL resource creation failed.
            try
            {
                rollbackConfiguration?.Invoke();
                DestroyDeviceResources();
            }
            finally
            {
                ImGui.DestroyContext(Context);
            }
            throw;
        }
    }

    public void MakeCurrent() => ImGui.SetCurrentContext(Context);

    public void Update(float deltaSeconds, Action? queueAdditionalInput = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        MakeCurrent();
        if (_frameBegun)
        {
            ImGui.Render();
            _frameBegun = false;
        }

        var io = ImGui.GetIO();
        var surface = _window.LatestWindowSurfaceSnapshot;
        io.DisplaySize = new Vector2(surface.ClientWidth, surface.ClientHeight);
        io.DisplayFramebufferScale = new Vector2(
            surface.DpiScaleX > 0 ? surface.DpiScaleX : 1,
            surface.DpiScaleY > 0 ? surface.DpiScaleY : 1);
        io.DeltaTime = Math.Max(deltaSeconds, 1.0f / 1000.0f);
        bool multipleViewports = (io.ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0;
        var origin = _window.DesktopWindowBackend?.ClientScreenPosition ?? default;
        WindowInputSnapshot input = _window.ConsumeUiInputSnapshot(_orderedInputScratch);
        ReplayInput(io, input, CollectionsMarshal.AsSpan(_orderedInputScratch),
            multipleViewports ? origin.X : 0,
            multipleViewports ? origin.Y : 0,
            0);
        queueAdditionalInput?.Invoke();
        ImGui.NewFrame();
        _frameBegun = true;
    }

    public void Render()
    {
        if (!_frameBegun)
            return;

        MakeCurrent();
        ImGui.Render();
        _frameBegun = false;
        RenderDrawData(ImGui.GetDrawData(), _window.DesktopGlContext!);
    }

    public void RebuildFontTexture()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_frameBegun)
            throw new InvalidOperationException("The ImGui font atlas can only change between frames.");
        MakeCurrent();
        ImGui.GetIO().Fonts.GetTexDataAsRGBA32(out nint pixels, out int width, out int height, out _);
        _gl.GetInteger(GLEnum.TextureBinding2D, out int previousTexture);
        if (_fontTexture != 0)
            _gl.DeleteTexture(_fontTexture);
        _fontTexture = _gl.GenTexture();
        _gl.BindTexture(GLEnum.Texture2D, _fontTexture);
        _gl.TexImage2D(GLEnum.Texture2D, 0, (int)InternalFormat.Rgba, (uint)width, (uint)height,
            0, PixelFormat.Rgba, PixelType.UnsignedByte, (void*)pixels);
        int linear = (int)GLEnum.Linear;
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, linear);
        ImGui.GetIO().Fonts.SetTexID((nint)_fontTexture);
        _gl.BindTexture(GLEnum.Texture2D, (uint)previousTexture);
    }

    public void ReplayViewportInput(IRuntimeWindowBackend window, int screenOriginX, int screenOriginY, uint viewportId)
    {
        MakeCurrent();
        WindowInputSnapshot input = window.ConsumeUiInput(_orderedInputScratch);
        ReplayInput(ImGui.GetIO(), input, CollectionsMarshal.AsSpan(_orderedInputScratch), screenOriginX, screenOriginY, viewportId);
    }

    private void ReplayInput(ImGuiIOPtr io, WindowInputSnapshot input, ReadOnlySpan<WindowInputEvent> events, int originX, int originY, uint viewportId)
    {
        if (input.Sequence == 0)
            return;
        if (viewportId == 0)
        {
            if (input.Sequence == _lastInputSequence)
                return;
            _lastInputSequence = input.Sequence;
        }
        else
        {
            if (_viewportInputSequences.TryGetValue(viewportId, out ulong previous) && previous == input.Sequence)
                return;
            _viewportInputSequences[viewportId] = input.Sequence;
        }
        for (int i = 0; i < events.Length; i++)
        {
            WindowInputEvent item = events[i];
            switch (item.Kind)
            {
                case WindowInputEventKind.Key:
                    ImGuiKey key = TranslateKey(item.Key);
                    if (key != ImGuiKey.None)
                        io.AddKeyEvent(key, item.IsDown);
                    ReplayModifier(io, item.Key, item.IsDown);
                    break;
                case WindowInputEventKind.MouseButton:
                    io.AddMouseButtonEvent((int)item.MouseButton, item.IsDown);
                    if (viewportId != 0) io.AddMouseViewportEvent(viewportId);
                    break;
                case WindowInputEventKind.Text:
                    io.AddInputCharacter(item.Character);
                    break;
                case WindowInputEventKind.Pointer:
                    io.AddMousePosEvent(originX + item.X, originY + item.Y);
                    if (viewportId != 0) io.AddMouseViewportEvent(viewportId);
                    break;
                case WindowInputEventKind.Scroll:
                    io.AddMouseWheelEvent(item.X, item.Y);
                    if (viewportId != 0) io.AddMouseViewportEvent(viewportId);
                    break;
            }
        }

        if (viewportId == 0 || input.IsFocused)
            io.AddFocusEvent(input.IsFocused);
    }

    private void ReplayModifier(ImGuiIOPtr io, EKey key, bool down)
    {
        switch (key)
        {
            case EKey.ControlLeft: _leftCtrl = down; io.AddKeyEvent(ImGuiKey.ModCtrl, _leftCtrl || _rightCtrl); break;
            case EKey.ControlRight: _rightCtrl = down; io.AddKeyEvent(ImGuiKey.ModCtrl, _leftCtrl || _rightCtrl); break;
            case EKey.ShiftLeft: _leftShift = down; io.AddKeyEvent(ImGuiKey.ModShift, _leftShift || _rightShift); break;
            case EKey.ShiftRight: _rightShift = down; io.AddKeyEvent(ImGuiKey.ModShift, _leftShift || _rightShift); break;
            case EKey.AltLeft: _leftAlt = down; io.AddKeyEvent(ImGuiKey.ModAlt, _leftAlt || _rightAlt); break;
            case EKey.AltRight: _rightAlt = down; io.AddKeyEvent(ImGuiKey.ModAlt, _leftAlt || _rightAlt); break;
            case EKey.WinLeft: _leftSuper = down; io.AddKeyEvent(ImGuiKey.ModSuper, _leftSuper || _rightSuper); break;
            case EKey.WinRight: _rightSuper = down; io.AddKeyEvent(ImGuiKey.ModSuper, _leftSuper || _rightSuper); break;
        }
    }

    public static ImGuiKey TranslateKey(EKey key) => key switch
    {
        EKey.Tab => ImGuiKey.Tab,
        EKey.Left => ImGuiKey.LeftArrow,
        EKey.Right => ImGuiKey.RightArrow,
        EKey.Up => ImGuiKey.UpArrow,
        EKey.Down => ImGuiKey.DownArrow,
        EKey.PageUp => ImGuiKey.PageUp,
        EKey.PageDown => ImGuiKey.PageDown,
        EKey.Home => ImGuiKey.Home,
        EKey.End => ImGuiKey.End,
        EKey.Insert => ImGuiKey.Insert,
        EKey.Delete => ImGuiKey.Delete,
        EKey.Backspace => ImGuiKey.Backspace,
        EKey.Space => ImGuiKey.Space,
        EKey.Enter => ImGuiKey.Enter,
        EKey.Escape => ImGuiKey.Escape,
        EKey.Apostrophe => ImGuiKey.Apostrophe,
        EKey.Comma => ImGuiKey.Comma,
        EKey.Minus => ImGuiKey.Minus,
        EKey.Period => ImGuiKey.Period,
        EKey.Slash => ImGuiKey.Slash,
        EKey.Semicolon => ImGuiKey.Semicolon,
        EKey.Equal => ImGuiKey.Equal,
        EKey.BracketLeft => ImGuiKey.LeftBracket,
        EKey.BackSlash => ImGuiKey.Backslash,
        EKey.BracketRight => ImGuiKey.RightBracket,
        EKey.Tilde => ImGuiKey.GraveAccent,
        EKey.CapsLock => ImGuiKey.CapsLock,
        EKey.ScrollLock => ImGuiKey.ScrollLock,
        EKey.NumLock => ImGuiKey.NumLock,
        EKey.PrintScreen => ImGuiKey.PrintScreen,
        EKey.Pause => ImGuiKey.Pause,
        EKey.Keypad0 => ImGuiKey.Keypad0,
        EKey.Keypad1 => ImGuiKey.Keypad1,
        EKey.Keypad2 => ImGuiKey.Keypad2,
        EKey.Keypad3 => ImGuiKey.Keypad3,
        EKey.Keypad4 => ImGuiKey.Keypad4,
        EKey.Keypad5 => ImGuiKey.Keypad5,
        EKey.Keypad6 => ImGuiKey.Keypad6,
        EKey.Keypad7 => ImGuiKey.Keypad7,
        EKey.Keypad8 => ImGuiKey.Keypad8,
        EKey.Keypad9 => ImGuiKey.Keypad9,
        EKey.KeypadDecimal => ImGuiKey.KeypadDecimal,
        EKey.KeypadDivide => ImGuiKey.KeypadDivide,
        EKey.KeypadMultiply => ImGuiKey.KeypadMultiply,
        EKey.KeypadMinus => ImGuiKey.KeypadSubtract,
        EKey.KeypadAdd => ImGuiKey.KeypadAdd,
        EKey.KeypadEnter => ImGuiKey.KeypadEnter,
        EKey.ShiftLeft => ImGuiKey.LeftShift,
        EKey.ControlLeft => ImGuiKey.LeftCtrl,
        EKey.AltLeft => ImGuiKey.LeftAlt,
        EKey.WinLeft => ImGuiKey.LeftSuper,
        EKey.ShiftRight => ImGuiKey.RightShift,
        EKey.ControlRight => ImGuiKey.RightCtrl,
        EKey.AltRight => ImGuiKey.RightAlt,
        EKey.WinRight => ImGuiKey.RightSuper,
        EKey.Menu => ImGuiKey.Menu,
        >= EKey.Number0 and <= EKey.Number9 => ImGuiKey._0 + (key - EKey.Number0),
        >= EKey.A and <= EKey.Z => ImGuiKey.A + (key - EKey.A),
        >= EKey.F1 and <= EKey.F24 => ImGuiKey.F1 + (key - EKey.F1),
        _ => ImGuiKey.None,
    };

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        MakeCurrent();
        DestroyDeviceResources();
        ImGui.DestroyContext(Context);
    }
}
