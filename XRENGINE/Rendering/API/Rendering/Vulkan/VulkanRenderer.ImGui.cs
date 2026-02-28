using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XREngine.Rendering.Models.Materials.Textures;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using Buffer = Silk.NET.Vulkan.Buffer;
using Format = Silk.NET.Vulkan.Format;
using Image = Silk.NET.Vulkan.Image;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal bool SamplerAnisotropyEnabled => _supportsAnisotropy;
    private VulkanImGuiBackend? _imguiBackend;
    private readonly ImGuiDrawDataCache _imguiDrawData = new();

    private ShaderModule _imguiVertShader;
    private ShaderModule _imguiFragShader;
    private PipelineLayout _imguiPipelineLayout;
    private Pipeline _imguiPipeline;
    private ulong _imguiPipelineSignature;

    private DescriptorSetLayout _imguiDescriptorSetLayout;
    private DescriptorPool _imguiDescriptorPool;
    private DescriptorSet _imguiFontDescriptorSet;
    private readonly Dictionary<nint, DescriptorSet> _imguiTextureDescriptorSets = [];
    private readonly Dictionary<XRTexture, nint> _imguiRegisteredTextureIds = [];
    private nint _nextImGuiTextureId = 2;

    private Image _imguiFontImage;
    private DeviceMemory _imguiFontImageMemory;
    private ImageView _imguiFontImageView;
    private Sampler _imguiFontSampler;
    private bool _imguiFontReady;

    private Buffer _imguiVertexBuffer;
    private DeviceMemory _imguiVertexBufferMemory;
    private ulong _imguiVertexBufferSize;

    private Buffer _imguiIndexBuffer;
    private DeviceMemory _imguiIndexBufferMemory;
    private ulong _imguiIndexBufferSize;

    protected override bool SupportsImGui => true;

    private sealed class VulkanImGuiBackend : IImGuiRendererBackend, IDisposable
    {
        private readonly VulkanRenderer _renderer;
        private readonly IntPtr _context;
        private readonly object _pendingInputLock = new();
        private readonly Queue<Action<ImGuiIOPtr>> _pendingInputEvents = new();
        private IMouse? _mouse;
        private readonly HashSet<IKeyboard> _keyboards = [];
        private bool _mouseSubscribed;
        private bool _disposed;

        // ── Win32 clipboard P/Invoke ─────────────────────────────────────

        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVEABLE = 0x0002;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetClipboardData(uint uFormat);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        // Buffer for GetClipboardText return — must stay valid until the next call.
        private static IntPtr s_clipboardReturnBuffer;

        /// <summary>
        /// ImGui callback: reads UTF-8 text from the Win32 clipboard.
        /// The returned pointer remains valid until the next invocation.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static byte* GetClipboardTextCallback(void* userData)
        {
            if (s_clipboardReturnBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(s_clipboardReturnBuffer);
                s_clipboardReturnBuffer = IntPtr.Zero;
            }

            try
            {
                if (!OpenClipboard(IntPtr.Zero))
                    return null;

                try
                {
                    IntPtr hData = GetClipboardData(CF_UNICODETEXT);
                    if (hData == IntPtr.Zero)
                        return null;

                    IntPtr pData = GlobalLock(hData);
                    if (pData == IntPtr.Zero)
                        return null;

                    try
                    {
                        string text = Marshal.PtrToStringUni(pData) ?? string.Empty;
                        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(text);
                        s_clipboardReturnBuffer = Marshal.AllocHGlobal(utf8.Length + 1);
                        Marshal.Copy(utf8, 0, s_clipboardReturnBuffer, utf8.Length);
                        Marshal.WriteByte(s_clipboardReturnBuffer, utf8.Length, 0);
                        return (byte*)s_clipboardReturnBuffer;
                    }
                    finally
                    {
                        GlobalUnlock(hData);
                    }
                }
                finally
                {
                    CloseClipboard();
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// ImGui callback: writes UTF-8 text to the Win32 clipboard.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void SetClipboardTextCallback(void* userData, byte* text)
        {
            try
            {
                if (text == null)
                    return;

                string str = Marshal.PtrToStringUTF8((IntPtr)text) ?? string.Empty;

                if (!OpenClipboard(IntPtr.Zero))
                    return;

                try
                {
                    EmptyClipboard();

                    byte[] unicodeBytes = System.Text.Encoding.Unicode.GetBytes(str);
                    IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(unicodeBytes.Length + 2));
                    if (hGlobal == IntPtr.Zero)
                        return;

                    IntPtr pGlobal = GlobalLock(hGlobal);
                    if (pGlobal == IntPtr.Zero)
                    {
                        GlobalFree(hGlobal);
                        return;
                    }

                    Marshal.Copy(unicodeBytes, 0, pGlobal, unicodeBytes.Length);
                    Marshal.WriteInt16(pGlobal, unicodeBytes.Length, 0); // null terminator
                    GlobalUnlock(hGlobal);

                    SetClipboardData(CF_UNICODETEXT, hGlobal);
                    // hGlobal ownership transfers to the clipboard — do not free.
                }
                finally
                {
                    CloseClipboard();
                }
            }
            catch
            {
                // Clipboard write failures are non-fatal; silently ignore.
            }
        }

        /// <summary>
        /// Installs Win32 clipboard callbacks on the ImGui IO so Ctrl+C/V/X
        /// work in all InputText fields under the Vulkan backend.
        /// </summary>
        private static void InstallClipboardCallbacks(ImGuiIOPtr io)
        {
            if (!OperatingSystem.IsWindows())
                return;

            io.GetClipboardTextFn = (IntPtr)(delegate* unmanaged[Cdecl]<void*, byte*>)&GetClipboardTextCallback;
            io.SetClipboardTextFn = (IntPtr)(delegate* unmanaged[Cdecl]<void*, byte*, void>)&SetClipboardTextCallback;
        }

        // ── End clipboard ────────────────────────────────────────────────

        public IntPtr ContextHandle => _context;

        public VulkanImGuiBackend(VulkanRenderer renderer)
        {
            _renderer = renderer;
            _context = ImGui.CreateContext();
            ImGuiContextTracker.Register(_context);
            MakeCurrent();

            // ImGui.NewFrame() asserts that the font atlas has been built.
            // The GPU texture upload happens later in EnsureImGuiFontResources(),
            // but the CPU-side atlas must be built now so NewFrame() doesn't AV.
            var io = ImGui.GetIO();
            if (!TryLoadLatoFont(io, 18.0f))
            {
                if (io.Fonts.Fonts.Size == 0)
                    io.Fonts.AddFontDefault();
            }
            io.Fonts.Build();

            InstallClipboardCallbacks(io);
            TryAttachInputHandlers();
        }

        /// <summary>
        /// Attempts to load the Lato font (matching the OpenGL backend appearance)
        /// without depending on ImGuiController. Returns false if the file isn't found.
        /// </summary>
        private static bool TryLoadLatoFont(ImGuiIOPtr io, float sizePixels)
        {
            try
            {
                string? fontPath = FindLatoFontPath();
                if (fontPath is null)
                    return false;

                io.Fonts.Clear();
                var font = io.Fonts.AddFontFromFileTTF(fontPath, sizePixels);
                return font.NativePtr != null;
            }
            catch
            {
                return false;
            }
        }

        private static string? FindLatoFontPath()
        {
            const string fontFileName = "Lato-Regular.ttf";
            string?[] candidates =
            [
                Path.Combine(Environment.CurrentDirectory, "..", "Build", "CommonAssets", "Fonts", "Lato", fontFileName),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "CommonAssets", "Fonts", "Lato", fontFileName),
                Path.Combine(Environment.CurrentDirectory, "Build", "CommonAssets", "Fonts", "Lato", fontFileName),
            ];

            foreach (string? candidate in candidates)
            {
                try
                {
                    if (candidate is not null && File.Exists(Path.GetFullPath(candidate)))
                        return Path.GetFullPath(candidate);
                }
                catch { }
            }

            return null;
        }

        public void MakeCurrent()
        {
            if (_disposed)
                return;

            ImGui.SetCurrentContext(_context);
        }

        public void Update(float deltaSeconds)
        {
            if (_disposed || !ImGuiContextTracker.IsAlive(_context))
                return;

            MakeCurrent();
            if (ImGui.GetCurrentContext() != _context)
                return;

            var io = ImGui.GetIO();
            io.DeltaTime = deltaSeconds > 0f ? deltaSeconds : 1f / 60f;

            uint width = Math.Max(_renderer.swapChainExtent.Width, 1u);
            uint height = Math.Max(_renderer.swapChainExtent.Height, 1u);
            io.DisplaySize = new Vector2(width, height);

            TryAttachInputHandlers();
            PushModifierKeyState(io);
            FlushPendingInputEvents(io);

            ImGui.NewFrame();
        }

        public void Render()
        {
            if (_disposed || !ImGuiContextTracker.IsAlive(_context))
                return;

            MakeCurrent();
            if (ImGui.GetCurrentContext() != _context)
                return;

            ImGui.Render();
            var drawData = ImGui.GetDrawData();
            if (drawData.NativePtr == null)
                return;

            _renderer._imguiDrawData.Store(drawData);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            DetachInputHandlers();
            if (ImGuiContextTracker.IsAlive(_context))
            {
                ImGui.SetCurrentContext(_context);
                ImGuiContextTracker.Unregister(_context);
                ImGui.DestroyContext(_context);
            }
        }

        private void TryAttachInputHandlers()
        {
            if (_disposed)
                return;

            var input = _renderer.XRWindow.Input;
            if (input is null)
                return;

            if (!_mouseSubscribed)
            {
                IMouse? mouse = null;
                if (input.Mice is { Count: > 0 })
                    mouse = input.Mice[0];

                if (mouse is not null)
                {
                    _mouse = mouse;
                    _mouse.MouseMove += OnMouseMove;
                    _mouse.MouseDown += OnMouseDown;
                    _mouse.MouseUp += OnMouseUp;
                    _mouse.Scroll += OnMouseScroll;
                    _mouseSubscribed = true;
                }
            }

            if (input.Keyboards is { Count: > 0 })
            {
                foreach (IKeyboard keyboard in input.Keyboards)
                {
                    if (!_keyboards.Add(keyboard))
                        continue;

                    keyboard.KeyDown += OnKeyDown;
                    keyboard.KeyUp += OnKeyUp;
                    keyboard.KeyChar += OnKeyChar;
                }
            }
        }

        private void DetachInputHandlers()
        {
            if (_mouse is not null)
            {
                try
                {
                    _mouse.MouseMove -= OnMouseMove;
                    _mouse.MouseDown -= OnMouseDown;
                    _mouse.MouseUp -= OnMouseUp;
                    _mouse.Scroll -= OnMouseScroll;
                }
                catch
                {
                }

                _mouse = null;
            }

            _mouseSubscribed = false;

            foreach (IKeyboard keyboard in _keyboards)
            {
                try
                {
                    keyboard.KeyDown -= OnKeyDown;
                    keyboard.KeyUp -= OnKeyUp;
                    keyboard.KeyChar -= OnKeyChar;
                }
                catch
                {
                }
            }

            _keyboards.Clear();

            lock (_pendingInputLock)
                _pendingInputEvents.Clear();
        }

        private void EnqueueInputEvent(Action<ImGuiIOPtr> inputEvent)
        {
            if (_disposed)
                return;

            lock (_pendingInputLock)
                _pendingInputEvents.Enqueue(inputEvent);
        }

        private void FlushPendingInputEvents(ImGuiIOPtr io)
        {
            while (true)
            {
                Action<ImGuiIOPtr>? next;
                lock (_pendingInputLock)
                {
                    if (_pendingInputEvents.Count == 0)
                        break;

                    next = _pendingInputEvents.Dequeue();
                }

                next(io);
            }
        }

        private void PushModifierKeyState(ImGuiIOPtr io)
        {
            bool ctrl = false;
            bool shift = false;
            bool alt = false;
            bool super = false;

            foreach (IKeyboard keyboard in _keyboards)
            {
                ctrl |= keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight);
                shift |= keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight);
                alt |= keyboard.IsKeyPressed(Key.AltLeft) || keyboard.IsKeyPressed(Key.AltRight);
                super |= keyboard.IsKeyPressed(Key.SuperLeft) || keyboard.IsKeyPressed(Key.SuperRight);
            }

            io.AddKeyEvent(ImGuiKey.ModCtrl, ctrl);
            io.AddKeyEvent(ImGuiKey.ModShift, shift);
            io.AddKeyEvent(ImGuiKey.ModAlt, alt);
            io.AddKeyEvent(ImGuiKey.ModSuper, super);
        }

        private void OnMouseMove(IMouse mouse, Vector2 position)
            => EnqueueInputEvent(io => io.AddMousePosEvent(position.X, position.Y));

        private void OnMouseDown(IMouse mouse, MouseButton button)
        {
            if (!TryConvertMouseButton(button, out int imguiButton))
                return;

            EnqueueInputEvent(io => io.AddMouseButtonEvent(imguiButton, true));
        }

        private void OnMouseUp(IMouse mouse, MouseButton button)
        {
            if (!TryConvertMouseButton(button, out int imguiButton))
                return;

            EnqueueInputEvent(io => io.AddMouseButtonEvent(imguiButton, false));
        }

        private void OnMouseScroll(IMouse mouse, ScrollWheel wheel)
        {
            float x = wheel.X;
            float y = wheel.Y;
            EnqueueInputEvent(io => io.AddMouseWheelEvent(x, y));
        }

        private void OnKeyDown(IKeyboard keyboard, Key key, int repeat)
        {
            if (!TryConvertKey(key, out ImGuiKey imguiKey))
                return;

            EnqueueInputEvent(io => io.AddKeyEvent(imguiKey, true));
        }

        private void OnKeyUp(IKeyboard keyboard, Key key, int repeat)
        {
            if (!TryConvertKey(key, out ImGuiKey imguiKey))
                return;

            EnqueueInputEvent(io => io.AddKeyEvent(imguiKey, false));
        }

        private void OnKeyChar(IKeyboard keyboard, char character)
        {
            if (character == '\0')
                return;

            EnqueueInputEvent(io => io.AddInputCharacter(character));
        }

        private static bool TryConvertMouseButton(MouseButton button, out int imguiButton)
        {
            imguiButton = button switch
            {
                MouseButton.Left => 0,
                MouseButton.Right => 1,
                MouseButton.Middle => 2,
                MouseButton.Button4 => 3,
                MouseButton.Button5 => 4,
                _ => -1
            };

            return imguiButton >= 0;
        }

        private static bool TryConvertKey(Key key, out ImGuiKey imguiKey)
        {
            imguiKey = key switch
            {
                Key.Tab => ImGuiKey.Tab,
                Key.Left => ImGuiKey.LeftArrow,
                Key.Right => ImGuiKey.RightArrow,
                Key.Up => ImGuiKey.UpArrow,
                Key.Down => ImGuiKey.DownArrow,
                Key.PageUp => ImGuiKey.PageUp,
                Key.PageDown => ImGuiKey.PageDown,
                Key.Home => ImGuiKey.Home,
                Key.End => ImGuiKey.End,
                Key.Insert => ImGuiKey.Insert,
                Key.Delete => ImGuiKey.Delete,
                Key.Backspace => ImGuiKey.Backspace,
                Key.Space => ImGuiKey.Space,
                Key.Enter => ImGuiKey.Enter,
                Key.Escape => ImGuiKey.Escape,
                Key.Apostrophe => ImGuiKey.Apostrophe,
                Key.Comma => ImGuiKey.Comma,
                Key.Minus => ImGuiKey.Minus,
                Key.Period => ImGuiKey.Period,
                Key.Slash => ImGuiKey.Slash,
                Key.Semicolon => ImGuiKey.Semicolon,
                Key.Equal => ImGuiKey.Equal,
                Key.LeftBracket => ImGuiKey.LeftBracket,
                Key.BackSlash => ImGuiKey.Backslash,
                Key.RightBracket => ImGuiKey.RightBracket,
                Key.GraveAccent => ImGuiKey.GraveAccent,
                Key.CapsLock => ImGuiKey.CapsLock,
                Key.ScrollLock => ImGuiKey.ScrollLock,
                Key.NumLock => ImGuiKey.NumLock,
                Key.PrintScreen => ImGuiKey.PrintScreen,
                Key.Pause => ImGuiKey.Pause,
                Key.Keypad0 => ImGuiKey.Keypad0,
                Key.Keypad1 => ImGuiKey.Keypad1,
                Key.Keypad2 => ImGuiKey.Keypad2,
                Key.Keypad3 => ImGuiKey.Keypad3,
                Key.Keypad4 => ImGuiKey.Keypad4,
                Key.Keypad5 => ImGuiKey.Keypad5,
                Key.Keypad6 => ImGuiKey.Keypad6,
                Key.Keypad7 => ImGuiKey.Keypad7,
                Key.Keypad8 => ImGuiKey.Keypad8,
                Key.Keypad9 => ImGuiKey.Keypad9,
                Key.KeypadDecimal => ImGuiKey.KeypadDecimal,
                Key.KeypadDivide => ImGuiKey.KeypadDivide,
                Key.KeypadMultiply => ImGuiKey.KeypadMultiply,
                Key.KeypadSubtract => ImGuiKey.KeypadSubtract,
                Key.KeypadAdd => ImGuiKey.KeypadAdd,
                Key.KeypadEnter => ImGuiKey.KeypadEnter,
                Key.KeypadEqual => ImGuiKey.KeypadEqual,
                Key.ShiftLeft => ImGuiKey.LeftShift,
                Key.ControlLeft => ImGuiKey.LeftCtrl,
                Key.AltLeft => ImGuiKey.LeftAlt,
                Key.SuperLeft => ImGuiKey.LeftSuper,
                Key.ShiftRight => ImGuiKey.RightShift,
                Key.ControlRight => ImGuiKey.RightCtrl,
                Key.AltRight => ImGuiKey.RightAlt,
                Key.SuperRight => ImGuiKey.RightSuper,
                Key.Menu => ImGuiKey.Menu,
                Key.Number0 => ImGuiKey._0,
                Key.Number1 => ImGuiKey._1,
                Key.Number2 => ImGuiKey._2,
                Key.Number3 => ImGuiKey._3,
                Key.Number4 => ImGuiKey._4,
                Key.Number5 => ImGuiKey._5,
                Key.Number6 => ImGuiKey._6,
                Key.Number7 => ImGuiKey._7,
                Key.Number8 => ImGuiKey._8,
                Key.Number9 => ImGuiKey._9,
                Key.A => ImGuiKey.A,
                Key.B => ImGuiKey.B,
                Key.C => ImGuiKey.C,
                Key.D => ImGuiKey.D,
                Key.E => ImGuiKey.E,
                Key.F => ImGuiKey.F,
                Key.G => ImGuiKey.G,
                Key.H => ImGuiKey.H,
                Key.I => ImGuiKey.I,
                Key.J => ImGuiKey.J,
                Key.K => ImGuiKey.K,
                Key.L => ImGuiKey.L,
                Key.M => ImGuiKey.M,
                Key.N => ImGuiKey.N,
                Key.O => ImGuiKey.O,
                Key.P => ImGuiKey.P,
                Key.Q => ImGuiKey.Q,
                Key.R => ImGuiKey.R,
                Key.S => ImGuiKey.S,
                Key.T => ImGuiKey.T,
                Key.U => ImGuiKey.U,
                Key.V => ImGuiKey.V,
                Key.W => ImGuiKey.W,
                Key.X => ImGuiKey.X,
                Key.Y => ImGuiKey.Y,
                Key.Z => ImGuiKey.Z,
                Key.F1 => ImGuiKey.F1,
                Key.F2 => ImGuiKey.F2,
                Key.F3 => ImGuiKey.F3,
                Key.F4 => ImGuiKey.F4,
                Key.F5 => ImGuiKey.F5,
                Key.F6 => ImGuiKey.F6,
                Key.F7 => ImGuiKey.F7,
                Key.F8 => ImGuiKey.F8,
                Key.F9 => ImGuiKey.F9,
                Key.F10 => ImGuiKey.F10,
                Key.F11 => ImGuiKey.F11,
                Key.F12 => ImGuiKey.F12,
                _ => ImGuiKey.None,
            };

            return imguiKey != ImGuiKey.None;
        }
    }

    private VulkanImGuiBackend GetOrCreateImGuiBackend()
    {
        if (_imguiBackend is not null && !ImGuiContextTracker.IsAlive(_imguiBackend.ContextHandle))
        {
            _imguiBackend.Dispose();
            _imguiBackend = null;
            _imguiDrawData.Clear();
        }

        return _imguiBackend ??= new VulkanImGuiBackend(this);
    }

    protected override IImGuiRendererBackend? GetImGuiBackend(XRViewport? viewport)
        => SupportsImGui ? GetOrCreateImGuiBackend() : null;

    private void DisposeImGuiResources()
    {
        DestroyImGuiPipelineResources();
        DestroyImGuiFontResources();
        DestroyImGuiDrawBuffers();

        _imguiBackend?.Dispose();
        _imguiBackend = null;
        _imguiDrawData.Clear();
        ResetImGuiFrameMarker();
    }

    private void DestroyImGuiDrawBuffers()
    {
        if (_imguiVertexBuffer.Handle != 0)
        {
            DestroyBuffer(_imguiVertexBuffer, _imguiVertexBufferMemory);
            _imguiVertexBuffer = default;
            _imguiVertexBufferMemory = default;
            _imguiVertexBufferSize = 0;
        }

        if (_imguiIndexBuffer.Handle != 0)
        {
            DestroyBuffer(_imguiIndexBuffer, _imguiIndexBufferMemory);
            _imguiIndexBuffer = default;
            _imguiIndexBufferMemory = default;
            _imguiIndexBufferSize = 0;
        }
    }

    private void DestroyImGuiPipelineResources()
    {
        if (Api is null)
            return;

        if (_imguiPipeline.Handle != 0)
            Api.DestroyPipeline(device, _imguiPipeline, null);
        _imguiPipeline = default;

        if (_imguiPipelineLayout.Handle != 0)
            Api.DestroyPipelineLayout(device, _imguiPipelineLayout, null);
        _imguiPipelineLayout = default;

        if (_imguiVertShader.Handle != 0)
            Api.DestroyShaderModule(device, _imguiVertShader, null);
        _imguiVertShader = default;

        if (_imguiFragShader.Handle != 0)
            Api.DestroyShaderModule(device, _imguiFragShader, null);
        _imguiFragShader = default;

        _imguiPipelineSignature = 0;
    }

    private void DestroyImGuiFontResources()
    {
        if (Api is null)
            return;

        if (_imguiFontSampler.Handle != 0)
            Api.DestroySampler(device, _imguiFontSampler, null);
        _imguiFontSampler = default;

        if (_imguiFontImageView.Handle != 0)
            Api.DestroyImageView(device, _imguiFontImageView, null);
        _imguiFontImageView = default;

        if (_imguiFontImage.Handle != 0)
            Api.DestroyImage(device, _imguiFontImage, null);
        _imguiFontImage = default;

        if (_imguiFontImageMemory.Handle != 0)
            Api.FreeMemory(device, _imguiFontImageMemory, null);
        _imguiFontImageMemory = default;

        if (_imguiDescriptorPool.Handle != 0)
            Api.DestroyDescriptorPool(device, _imguiDescriptorPool, null);
        _imguiDescriptorPool = default;

        if (_imguiDescriptorSetLayout.Handle != 0)
            Api.DestroyDescriptorSetLayout(device, _imguiDescriptorSetLayout, null);
        _imguiDescriptorSetLayout = default;

        _imguiFontDescriptorSet = default;
        _imguiTextureDescriptorSets.Clear();
        _imguiRegisteredTextureIds.Clear();
        _nextImGuiTextureId = 2;
        _imguiFontReady = false;
    }

    private void EnsureImGuiFontResources()
    {
        if (_imguiFontReady)
            return;

        ImGuiIOPtr io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out int width, out int height, out _);
        if (pixels == null || width <= 0 || height <= 0)
            throw new InvalidOperationException("Failed to get ImGui font atlas pixels.");

        ulong uploadSize = (ulong)(width * height * 4);

        (Buffer stagingBuffer, DeviceMemory stagingMemory) = CreateBufferRaw(
            uploadSize,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        try
        {
            UploadBufferMemory(stagingMemory, uploadSize, pixels);
            CreateImGuiFontImage((uint)width, (uint)height);

            using (var scope = NewCommandScope())
            {
                TransitionImGuiFontImage(scope.CommandBuffer, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);

                BufferImageCopy copyRegion = new()
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = 0,
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    },
                    ImageOffset = new Offset3D(0, 0, 0),
                    ImageExtent = new Extent3D((uint)width, (uint)height, 1)
                };

                Api!.CmdCopyBufferToImage(scope.CommandBuffer, stagingBuffer, _imguiFontImage, ImageLayout.TransferDstOptimal, 1, &copyRegion);
                TransitionImGuiFontImage(scope.CommandBuffer, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
            }

            CreateImGuiFontDescriptorResources();
            io.Fonts.SetTexID((IntPtr)1);
            _imguiFontReady = true;
        }
        finally
        {
            DestroyBuffer(stagingBuffer, stagingMemory);
        }
    }

    private void CreateImGuiFontImage(uint width, uint height)
    {
        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Format = Format.R8G8B8A8Unorm,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            Samples = SampleCountFlags.Count1Bit,
            SharingMode = SharingMode.Exclusive
        };

        if (Api!.CreateImage(device, ref imageInfo, null, out _imguiFontImage) != Result.Success)
            throw new InvalidOperationException("Failed to create ImGui font image.");

        Api.GetImageMemoryRequirements(device, _imguiFontImage, out MemoryRequirements memRequirements);
        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memRequirements.Size,
            MemoryTypeIndex = FindMemoryType(memRequirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
        };

        if (Api.AllocateMemory(device, ref allocInfo, null, out _imguiFontImageMemory) != Result.Success)
            throw new InvalidOperationException("Failed to allocate ImGui font image memory.");

        if (Api.BindImageMemory(device, _imguiFontImage, _imguiFontImageMemory, 0) != Result.Success)
            throw new InvalidOperationException("Failed to bind ImGui font image memory.");

        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _imguiFontImage,
            ViewType = ImageViewType.Type2D,
            Format = Format.R8G8B8A8Unorm,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            }
        };

        if (Api.CreateImageView(device, ref viewInfo, null, out _imguiFontImageView) != Result.Success)
            throw new InvalidOperationException("Failed to create ImGui font image view.");

        SamplerCreateInfo samplerInfo = new()
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.Repeat,
            AddressModeV = SamplerAddressMode.Repeat,
            AddressModeW = SamplerAddressMode.Repeat,
            MipLodBias = 0f,
            AnisotropyEnable = Vk.False,
            MaxAnisotropy = 1f,
            CompareEnable = Vk.False,
            CompareOp = CompareOp.Always,
            MinLod = 0f,
            MaxLod = 0f,
            BorderColor = BorderColor.FloatOpaqueWhite,
            UnnormalizedCoordinates = Vk.False
        };

        if (Api.CreateSampler(device, ref samplerInfo, null, out _imguiFontSampler) != Result.Success)
            throw new InvalidOperationException("Failed to create ImGui font sampler.");
    }

    private void CreateImGuiFontDescriptorResources()
    {
        DescriptorSetLayoutBinding samplerBinding = new()
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
            PImmutableSamplers = null
        };

        DescriptorSetLayoutCreateInfo layoutInfo = new()
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &samplerBinding
        };

        if (Api!.CreateDescriptorSetLayout(device, ref layoutInfo, null, out _imguiDescriptorSetLayout) != Result.Success)
            throw new InvalidOperationException("Failed to create ImGui descriptor set layout.");

        DescriptorPoolSize poolSize = new()
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1
        };

        DescriptorPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
            MaxSets = 256,
            Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit
        };

        if (Api.CreateDescriptorPool(device, ref poolInfo, null, out _imguiDescriptorPool) != Result.Success)
            throw new InvalidOperationException("Failed to create ImGui descriptor pool.");

        DescriptorSetLayout descriptorLayout = _imguiDescriptorSetLayout;
        DescriptorSetAllocateInfo allocInfo = new()
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _imguiDescriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &descriptorLayout
        };

        if (Api.AllocateDescriptorSets(device, ref allocInfo, out _imguiFontDescriptorSet) != Result.Success)
            throw new InvalidOperationException("Failed to allocate ImGui descriptor set.");

        _imguiTextureDescriptorSets[(nint)1] = _imguiFontDescriptorSet;

        DescriptorImageInfo imageInfo = new()
        {
            Sampler = _imguiFontSampler,
            ImageView = _imguiFontImageView,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal
        };

        WriteDescriptorSet write = new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _imguiFontDescriptorSet,
            DstBinding = 0,
            DstArrayElement = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = &imageInfo
        };

        Api.UpdateDescriptorSets(device, 1, &write, 0, null);
    }

    private void TransitionImGuiFontImage(CommandBuffer commandBuffer, ImageLayout oldLayout, ImageLayout newLayout)
    {
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _imguiFontImage,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            }
        };

        PipelineStageFlags srcStage;
        PipelineStageFlags dstStage;

        if (oldLayout == ImageLayout.Undefined && newLayout == ImageLayout.TransferDstOptimal)
        {
            barrier.SrcAccessMask = 0;
            barrier.DstAccessMask = AccessFlags.TransferWriteBit;
            srcStage = PipelineStageFlags.TopOfPipeBit;
            dstStage = PipelineStageFlags.TransferBit;
        }
        else if (oldLayout == ImageLayout.TransferDstOptimal && newLayout == ImageLayout.ShaderReadOnlyOptimal)
        {
            barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
            barrier.DstAccessMask = AccessFlags.ShaderReadBit;
            srcStage = PipelineStageFlags.TransferBit;
            dstStage = PipelineStageFlags.FragmentShaderBit;
        }
        else
        {
            throw new InvalidOperationException($"Unsupported ImGui image layout transition {oldLayout} -> {newLayout}.");
        }

        Api!.CmdPipelineBarrier(commandBuffer, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
    }

    private void EnsureImGuiPipeline()
    {
        HashCode pipelineKeyHash = new();
        pipelineKeyHash.Add(SupportsDynamicRendering);
        pipelineKeyHash.Add(_renderPass.Handle);
        pipelineKeyHash.Add((int)swapChainImageFormat);
        pipelineKeyHash.Add((int)_swapchainDepthFormat);
        ulong currentPipelineSignature = unchecked((ulong)pipelineKeyHash.ToHashCode());

        if (_imguiPipeline.Handle != 0 && _imguiPipelineSignature == currentPipelineSignature)
            return;

        DestroyImGuiPipelineResources();
        _imguiPipelineSignature = currentPipelineSignature;

        const string vertSource = "#version 450\n"
            + "layout(push_constant) uniform PushConstants { vec2 scale; vec2 translate; } pc;\n"
            + "layout(location = 0) in vec2 inPos;\n"
            + "layout(location = 1) in vec2 inUv;\n"
            + "layout(location = 2) in vec4 inColor;\n"
            + "layout(location = 0) out vec2 outUv;\n"
            + "layout(location = 1) out vec4 outColor;\n"
            + "void main()\n"
            + "{\n"
            + "    outUv = inUv;\n"
            + "    outColor = inColor;\n"
            + "    gl_Position = vec4(inPos * pc.scale + pc.translate, 0.0, 1.0);\n"
            + "}\n";

        const string fragSource = "#version 450\n"
            + "layout(set = 0, binding = 0) uniform sampler2D sTexture;\n"
            + "layout(location = 0) in vec2 inUv;\n"
            + "layout(location = 1) in vec4 inColor;\n"
            + "layout(location = 0) out vec4 outColor;\n"
            + "void main()\n"
            + "{\n"
            + "    outColor = inColor * texture(sTexture, inUv);\n"
            + "}\n";

        XRShader vs = new(EShaderType.Vertex, vertSource) { Name = "VkImGui.vs" };
        XRShader fs = new(EShaderType.Fragment, fragSource) { Name = "VkImGui.fs" };

        byte[] vsSpv = VulkanShaderCompiler.Compile(vs, out _, out _, out _);
        byte[] fsSpv = VulkanShaderCompiler.Compile(fs, out _, out _, out _);

        fixed (byte* vsPtr = vsSpv)
        fixed (byte* fsPtr = fsSpv)
        {
            ShaderModuleCreateInfo vsInfo = new()
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)vsSpv.Length,
                PCode = (uint*)vsPtr
            };

            ShaderModuleCreateInfo fsInfo = new()
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)fsSpv.Length,
                PCode = (uint*)fsPtr
            };

            if (Api!.CreateShaderModule(device, ref vsInfo, null, out _imguiVertShader) != Result.Success)
                throw new InvalidOperationException("Failed to create ImGui vertex shader module.");
            if (Api.CreateShaderModule(device, ref fsInfo, null, out _imguiFragShader) != Result.Success)
                throw new InvalidOperationException("Failed to create ImGui fragment shader module.");
        }

        PushConstantRange pushRange = new()
        {
            StageFlags = ShaderStageFlags.VertexBit,
            Offset = 0,
            Size = (uint)Marshal.SizeOf<ImGuiPushConstants>()
        };

        DescriptorSetLayout descriptorLayoutForPipeline = _imguiDescriptorSetLayout;
        PipelineLayoutCreateInfo layoutInfo = new()
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &descriptorLayoutForPipeline,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange
        };

        if (Api.CreatePipelineLayout(device, ref layoutInfo, null, out _imguiPipelineLayout) != Result.Success)
            throw new InvalidOperationException("Failed to create ImGui pipeline layout.");

        PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = _imguiVertShader,
            PName = (byte*)Silk.NET.Core.Native.SilkMarshal.StringToPtr("main"),
        };
        stages[1] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = _imguiFragShader,
            PName = (byte*)Silk.NET.Core.Native.SilkMarshal.StringToPtr("main"),
        };

        try
        {
            VertexInputBindingDescription binding = new()
            {
                Binding = 0,
                Stride = (uint)sizeof(ImDrawVert),
                InputRate = VertexInputRate.Vertex
            };

            VertexInputAttributeDescription* attributes = stackalloc VertexInputAttributeDescription[3];
            attributes[0] = new VertexInputAttributeDescription
            {
                Location = 0,
                Binding = 0,
                Format = Format.R32G32Sfloat,
                Offset = (uint)Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.pos))
            };
            attributes[1] = new VertexInputAttributeDescription
            {
                Location = 1,
                Binding = 0,
                Format = Format.R32G32Sfloat,
                Offset = (uint)Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.uv))
            };
            attributes[2] = new VertexInputAttributeDescription
            {
                Location = 2,
                Binding = 0,
                Format = Format.R8G8B8A8Unorm,
                Offset = (uint)Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.col))
            };

            PipelineVertexInputStateCreateInfo vertexInput = new()
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 3,
                PVertexAttributeDescriptions = attributes
            };

            PipelineInputAssemblyStateCreateInfo inputAssembly = new()
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
                PrimitiveRestartEnable = Vk.False,
            };

            PipelineViewportStateCreateInfo viewportState = new()
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1,
            };

            PipelineRasterizationStateCreateInfo rasterizer = new()
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                DepthClampEnable = Vk.False,
                RasterizerDiscardEnable = Vk.False,
                PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
                DepthBiasEnable = Vk.False,
                LineWidth = 1.0f
            };

            PipelineMultisampleStateCreateInfo multisampling = new()
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
                SampleShadingEnable = Vk.False
            };

            PipelineDepthStencilStateCreateInfo depthStencil = new()
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = Vk.False,
                DepthWriteEnable = Vk.False,
                DepthCompareOp = CompareOp.Always,
                DepthBoundsTestEnable = Vk.False,
                StencilTestEnable = Vk.False
            };

            PipelineColorBlendAttachmentState colorAttachment = new()
            {
                BlendEnable = Vk.True,
                SrcColorBlendFactor = BlendFactor.SrcAlpha,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit
            };

            const uint kDynRenderColorSlots = 1;
            PipelineColorBlendAttachmentState* blendSlots = stackalloc PipelineColorBlendAttachmentState[(int)kDynRenderColorSlots];
            blendSlots[0] = colorAttachment;

            uint imguiBlendCount = SupportsDynamicRendering ? kDynRenderColorSlots : 1;

            PipelineColorBlendStateCreateInfo colorBlendState = new()
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                LogicOpEnable = Vk.False,
                AttachmentCount = imguiBlendCount,
                PAttachments = SupportsDynamicRendering ? blendSlots : &colorAttachment
            };

            DynamicState* dynamicStates = stackalloc DynamicState[2];
            dynamicStates[0] = DynamicState.Viewport;
            dynamicStates[1] = DynamicState.Scissor;

            PipelineDynamicStateCreateInfo dynamicState = new()
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynamicStates
            };

            GraphicsPipelineCreateInfo pipelineInfo = new()
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &rasterizer,
                PMultisampleState = &multisampling,
                PDepthStencilState = &depthStencil,
                PColorBlendState = &colorBlendState,
                PDynamicState = &dynamicState,
                Layout = _imguiPipelineLayout,
                RenderPass = SupportsDynamicRendering ? default : _renderPass,
                Subpass = 0,
            };

            if (SupportsDynamicRendering)
            {
                Format* colorFormats = stackalloc Format[(int)kDynRenderColorSlots];
                colorFormats[0] = swapChainImageFormat;

                PipelineRenderingCreateInfo renderingInfo = new()
                {
                    SType = StructureType.PipelineRenderingCreateInfo,
                    ColorAttachmentCount = kDynRenderColorSlots,
                    PColorAttachmentFormats = colorFormats,
                    DepthAttachmentFormat = _swapchainDepthFormat,
                    StencilAttachmentFormat = _swapchainDepthFormat is Format.D16UnormS8Uint or Format.D24UnormS8Uint or Format.D32SfloatS8Uint
                        ? _swapchainDepthFormat
                        : Format.Undefined,
                };

                pipelineInfo.PNext = &renderingInfo;
            }

            if (Api.CreateGraphicsPipelines(device, default, 1, ref pipelineInfo, null, out _imguiPipeline) != Result.Success)
                throw new InvalidOperationException("Failed to create ImGui graphics pipeline.");
        }
        finally
        {
            Silk.NET.Core.Native.SilkMarshal.Free((nint)stages[0].PName);
            Silk.NET.Core.Native.SilkMarshal.Free((nint)stages[1].PName);
        }
    }

    private void EnsureImGuiDrawBuffers(ulong vertexBytes, ulong indexBytes)
    {
        ulong requiredVertexBytes = Math.Max(vertexBytes, 1UL);
        ulong requiredIndexBytes = Math.Max(indexBytes, 1UL);

        if (_imguiVertexBuffer.Handle == 0 || _imguiVertexBufferSize < requiredVertexBytes)
        {
            if (_imguiVertexBuffer.Handle != 0)
                RetireBuffer(_imguiVertexBuffer, _imguiVertexBufferMemory);

            (_imguiVertexBuffer, _imguiVertexBufferMemory) = CreateBufferRaw(
                requiredVertexBytes,
                BufferUsageFlags.VertexBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            _imguiVertexBufferSize = requiredVertexBytes;
        }

        if (_imguiIndexBuffer.Handle == 0 || _imguiIndexBufferSize < requiredIndexBytes)
        {
            if (_imguiIndexBuffer.Handle != 0)
                RetireBuffer(_imguiIndexBuffer, _imguiIndexBufferMemory);

            (_imguiIndexBuffer, _imguiIndexBufferMemory) = CreateBufferRaw(
                requiredIndexBytes,
                BufferUsageFlags.IndexBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            _imguiIndexBufferSize = requiredIndexBytes;
        }
    }

    private static void CopyImGuiSnapshot(ImGuiFrameSnapshot snapshot, void* vertexDst, void* indexDst)
    {
        byte* vertexWritePtr = (byte*)vertexDst;
        byte* indexWritePtr = (byte*)indexDst;

        for (int listIndex = 0; listIndex < snapshot.CommandLists.Count; listIndex++)
        {
            ImGuiCommandListSnapshot cmdList = snapshot.CommandLists[listIndex];

            nuint vertexBytes = (nuint)(cmdList.Vertices.Length * sizeof(ImDrawVert));
            nuint indexBytes = (nuint)(cmdList.Indices.Length * sizeof(ushort));

            fixed (ImDrawVert* verticesPtr = cmdList.Vertices)
                System.Buffer.MemoryCopy(verticesPtr, vertexWritePtr, (long)vertexBytes, (long)vertexBytes);
            fixed (ushort* indicesPtr = cmdList.Indices)
                System.Buffer.MemoryCopy(indicesPtr, indexWritePtr, (long)indexBytes, (long)indexBytes);

            vertexWritePtr += (int)vertexBytes;
            indexWritePtr += (int)indexBytes;
        }
    }

    private void RenderImGui(CommandBuffer commandBuffer, uint imageIndex)
    {
        _ = imageIndex;

        if (!_imguiDrawData.TryConsume(out ImGuiFrameSnapshot? drawData) || drawData is null)
            return;

        if (drawData.TotalVertexCount <= 0 || drawData.TotalIndexCount <= 0 || drawData.CommandLists.Count == 0)
            return;

        EnsureImGuiFontResources();
        EnsureImGuiPipeline();

        ulong vertexBytes = (ulong)(drawData.TotalVertexCount * sizeof(ImDrawVert));
        ulong indexBytes = (ulong)(drawData.TotalIndexCount * sizeof(ushort));
        EnsureImGuiDrawBuffers(vertexBytes, indexBytes);

        void* mappedVertex = null;
        void* mappedIndex = null;

        if (Api!.MapMemory(device, _imguiVertexBufferMemory, 0, vertexBytes, 0, &mappedVertex) != Result.Success)
            throw new InvalidOperationException("Failed to map ImGui vertex buffer.");

        if (Api.MapMemory(device, _imguiIndexBufferMemory, 0, indexBytes, 0, &mappedIndex) != Result.Success)
        {
            Api.UnmapMemory(device, _imguiVertexBufferMemory);
            throw new InvalidOperationException("Failed to map ImGui index buffer.");
        }

        try
        {
            CopyImGuiSnapshot(drawData, mappedVertex, mappedIndex);
        }
        finally
        {
            Api.UnmapMemory(device, _imguiIndexBufferMemory);
            Api.UnmapMemory(device, _imguiVertexBufferMemory);
        }

        Api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _imguiPipeline);

        DescriptorSet boundDescriptorSet = default;
        bool hasBoundDescriptorSet = false;

        Buffer vertexBuffer = _imguiVertexBuffer;
        ulong vertexOffset = 0;
        Api.CmdBindVertexBuffers(commandBuffer, 0, 1, &vertexBuffer, &vertexOffset);
        Api.CmdBindIndexBuffer(commandBuffer, _imguiIndexBuffer, 0, IndexType.Uint16);

        Vector2 clipOff = drawData.DisplayPos;
        Vector2 clipScale = drawData.FramebufferScale;
        Vector2 displaySize = drawData.DisplaySize;

        if (displaySize.X <= 0f || displaySize.Y <= 0f)
            return;

        ImGuiPushConstants pushConstants = new()
        {
            Scale = new Vector2(2.0f / displaySize.X, 2.0f / displaySize.Y),
            Translate = new Vector2(
                -1.0f - clipOff.X * (2.0f / displaySize.X),
                -1.0f - clipOff.Y * (2.0f / displaySize.Y))
        };

        Api.CmdPushConstants(commandBuffer, _imguiPipelineLayout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(ImGuiPushConstants), &pushConstants);

        uint fbWidth = (uint)(displaySize.X * clipScale.X);
        uint fbHeight = (uint)(displaySize.Y * clipScale.Y);
        if (fbWidth == 0 || fbHeight == 0)
            return;

        uint globalVtxOffset = 0;
        uint globalIdxOffset = 0;

        for (int listIndex = 0; listIndex < drawData.CommandLists.Count; listIndex++)
        {
            ImGuiCommandListSnapshot cmdList = drawData.CommandLists[listIndex];

            for (int cmdIndex = 0; cmdIndex < cmdList.Commands.Length; cmdIndex++)
            {
                ImGuiCommandSnapshot drawCmd = cmdList.Commands[cmdIndex];
                if (drawCmd.HasUserCallback)
                    continue;

                DescriptorSet drawDescriptorSet = ResolveImGuiDescriptorSet(drawCmd.TextureId);
                if (!hasBoundDescriptorSet || drawDescriptorSet.Handle != boundDescriptorSet.Handle)
                {
                    DescriptorSet setToBind = drawDescriptorSet;
                    Api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _imguiPipelineLayout, 0, 1, &setToBind, 0, null);
                    boundDescriptorSet = drawDescriptorSet;
                    hasBoundDescriptorSet = true;
                }

                Vector4 clipRect = drawCmd.ClipRect;
                float clipMinX = (clipRect.X - clipOff.X) * clipScale.X;
                float clipMinY = (clipRect.Y - clipOff.Y) * clipScale.Y;
                float clipMaxX = (clipRect.Z - clipOff.X) * clipScale.X;
                float clipMaxY = (clipRect.W - clipOff.Y) * clipScale.Y;

                if (clipMinX < 0f) clipMinX = 0f;
                if (clipMinY < 0f) clipMinY = 0f;
                if (clipMaxX > fbWidth) clipMaxX = fbWidth;
                if (clipMaxY > fbHeight) clipMaxY = fbHeight;

                if (clipMaxX <= clipMinX || clipMaxY <= clipMinY)
                    continue;

                Rect2D scissor = new()
                {
                    Offset = new Offset2D((int)clipMinX, (int)clipMinY),
                    Extent = new Extent2D((uint)(clipMaxX - clipMinX), (uint)(clipMaxY - clipMinY))
                };
                Api.CmdSetScissor(commandBuffer, 0, 1, &scissor);

                Api.CmdDrawIndexed(
                    commandBuffer,
                    drawCmd.ElemCount,
                    1,
                    drawCmd.IdxOffset + globalIdxOffset,
                    (int)(drawCmd.VtxOffset + globalVtxOffset),
                    0);
            }

            globalIdxOffset += (uint)cmdList.Indices.Length;
            globalVtxOffset += (uint)cmdList.Vertices.Length;
        }
    }

    private DescriptorSet ResolveImGuiDescriptorSet(nint textureId)
    {
        if (textureId == 0)
            return _imguiFontDescriptorSet;

        if (_imguiTextureDescriptorSets.TryGetValue(textureId, out DescriptorSet set) && set.Handle != 0)
            return set;

        return _imguiFontDescriptorSet;
    }

    public IntPtr RegisterImGuiTexture(XRTexture texture)
    {
        if (texture is null)
            return IntPtr.Zero;

        EnsureImGuiFontResources();

        if (_imguiRegisteredTextureIds.TryGetValue(texture, out nint existingId))
            return (IntPtr)existingId;

        if (GetOrCreateAPIRenderObject(texture, generateNow: true) is not IVkImageDescriptorSource source)
            return IntPtr.Zero;

        DescriptorSet descriptorSet = AllocateImGuiDescriptorSetForSource(source);
        if (descriptorSet.Handle == 0)
            return IntPtr.Zero;

        nint id = _nextImGuiTextureId++;
        _imguiRegisteredTextureIds[texture] = id;
        _imguiTextureDescriptorSets[id] = descriptorSet;
        return (IntPtr)id;
    }

    public bool UnregisterImGuiTexture(IntPtr textureId)
    {
        nint id = textureId;
        if (id <= 1)
            return false;

        if (!_imguiTextureDescriptorSets.TryGetValue(id, out DescriptorSet descriptorSet))
            return false;

        _imguiTextureDescriptorSets.Remove(id);

        XRTexture? keyToRemove = null;
        foreach (var entry in _imguiRegisteredTextureIds)
        {
            if (entry.Value == id)
            {
                keyToRemove = entry.Key;
                break;
            }
        }

        if (keyToRemove is not null)
            _imguiRegisteredTextureIds.Remove(keyToRemove);

        if (descriptorSet.Handle != 0)
            Api!.FreeDescriptorSets(device, _imguiDescriptorPool, 1, &descriptorSet);

        return true;
    }

    private DescriptorSet AllocateImGuiDescriptorSetForSource(IVkImageDescriptorSource source)
    {
        if (_imguiDescriptorPool.Handle == 0 || _imguiDescriptorSetLayout.Handle == 0)
            return default;

        DescriptorSetLayout layout = _imguiDescriptorSetLayout;
        DescriptorSetAllocateInfo allocInfo = new()
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _imguiDescriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout
        };

        if (Api!.AllocateDescriptorSets(device, ref allocInfo, out DescriptorSet descriptorSet) != Result.Success)
            return default;

        DescriptorImageInfo imageInfo = new()
        {
            Sampler = source.DescriptorSampler,
            ImageView = source.DescriptorView,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal
        };

        WriteDescriptorSet write = new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = 0,
            DstArrayElement = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = &imageInfo
        };

        Api.UpdateDescriptorSets(device, 1, &write, 0, null);
        return descriptorSet;
    }

    internal void DestroySwapchainImGuiResources()
        => DestroyImGuiPipelineResources();

    [StructLayout(LayoutKind.Sequential)]
    private struct ImGuiPushConstants
    {
        public Vector2 Scale;
        public Vector2 Translate;
    }

    private sealed class ImGuiDrawDataCache
    {
        private ImGuiFrameSnapshot? _snapshot;

        public void Store(ImDrawDataPtr drawData)
            => _snapshot = ImGuiFrameSnapshot.Create(drawData);

        public bool TryConsume(out ImGuiFrameSnapshot? snapshot)
        {
            snapshot = _snapshot;
            _snapshot = null;
            return snapshot is not null;
        }

        public void Clear()
            => _snapshot = null;
    }

    private sealed class ImGuiFrameSnapshot
    {
        public required Vector2 DisplayPos { get; init; }
        public required Vector2 DisplaySize { get; init; }
        public required Vector2 FramebufferScale { get; init; }
        public required int TotalVertexCount { get; init; }
        public required int TotalIndexCount { get; init; }
        public required List<ImGuiCommandListSnapshot> CommandLists { get; init; }

        public static ImGuiFrameSnapshot Create(ImDrawDataPtr drawData)
        {
            ImDrawData* native = drawData.NativePtr;
            ImDrawList** lists = (ImDrawList**)native->CmdLists.Data;

            List<ImGuiCommandListSnapshot> commandLists = new(drawData.CmdListsCount);
            int totalVertices = 0;
            int totalIndices = 0;

            for (int listIndex = 0; listIndex < drawData.CmdListsCount; listIndex++)
            {
                ImDrawListPtr cmdList = new(lists[listIndex]);
                ImDrawVert[] vertices = new ImDrawVert[cmdList.VtxBuffer.Size];
                ushort[] indices = new ushort[cmdList.IdxBuffer.Size];

                if (vertices.Length > 0)
                {
                    fixed (ImDrawVert* vertexDst = vertices)
                    {
                        nuint bytes = (nuint)(vertices.Length * sizeof(ImDrawVert));
                        System.Buffer.MemoryCopy(cmdList.VtxBuffer.Data.ToPointer(), vertexDst, (long)bytes, (long)bytes);
                    }
                }

                if (indices.Length > 0)
                {
                    fixed (ushort* indexDst = indices)
                    {
                        nuint bytes = (nuint)(indices.Length * sizeof(ushort));
                        System.Buffer.MemoryCopy(cmdList.IdxBuffer.Data.ToPointer(), indexDst, (long)bytes, (long)bytes);
                    }
                }

                ImGuiCommandSnapshot[] commands = new ImGuiCommandSnapshot[cmdList.CmdBuffer.Size];
                for (int cmdIndex = 0; cmdIndex < commands.Length; cmdIndex++)
                {
                    ImDrawCmdPtr drawCmd = cmdList.CmdBuffer[cmdIndex];
                    commands[cmdIndex] = new ImGuiCommandSnapshot
                    {
                        ClipRect = drawCmd.ClipRect,
                        TextureId = drawCmd.TextureId,
                        ElemCount = drawCmd.ElemCount,
                        IdxOffset = drawCmd.IdxOffset,
                        VtxOffset = drawCmd.VtxOffset,
                        HasUserCallback = drawCmd.UserCallback != IntPtr.Zero
                    };
                }

                commandLists.Add(new ImGuiCommandListSnapshot
                {
                    Vertices = vertices,
                    Indices = indices,
                    Commands = commands
                });

                totalVertices += vertices.Length;
                totalIndices += indices.Length;
            }

            return new ImGuiFrameSnapshot
            {
                DisplayPos = drawData.DisplayPos,
                DisplaySize = drawData.DisplaySize,
                FramebufferScale = drawData.FramebufferScale,
                TotalVertexCount = totalVertices,
                TotalIndexCount = totalIndices,
                CommandLists = commandLists
            };
        }
    }

    private sealed class ImGuiCommandListSnapshot
    {
        public required ImDrawVert[] Vertices { get; init; }
        public required ushort[] Indices { get; init; }
        public required ImGuiCommandSnapshot[] Commands { get; init; }
    }

    private struct ImGuiCommandSnapshot
    {
        public Vector4 ClipRect;
        public nint TextureId;
        public uint ElemCount;
        public uint IdxOffset;
        public uint VtxOffset;
        public bool HasUserCallback;
    }
}
