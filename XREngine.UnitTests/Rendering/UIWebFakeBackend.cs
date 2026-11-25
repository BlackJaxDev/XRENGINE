using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

internal class UIWebFakeBackend : IWebRendererBackend
{
    public bool IsInitialized { get; private set; }
    public bool SupportsFramebuffer => false;
    public int InitializeCount { get; private set; }
    public int UpdateCount { get; private set; }
    public int RenderCount { get; private set; }
    public int DisposeCount { get; private set; }
    public Action? OnUpdate { get; set; }
    public Action? OnInitialize { get; set; }
    public Action? OnDispose { get; set; }
    public bool ThrowOnDispose { get; set; }

    public void Initialize(uint width, uint height, bool transparentBackground)
    {
        InitializeCount++;
        OnInitialize?.Invoke();
        IsInitialized = true;
    }

    public void Resize(uint width, uint height) { }
    public void LoadUrl(string url) { }
    public void Update(double deltaSeconds)
    {
        UpdateCount++;
        OnUpdate?.Invoke();
    }
    public void Render() => RenderCount++;
    public bool TryGetFrame(out WebFrame frame)
    {
        frame = default;
        return false;
    }
    public void SetTargetFramebuffer(uint framebufferId) { }
    public void SendMouseMove(int x, int y) { }
    public void SendMouseButton(bool pressed, WebMouseButton button, int x, int y) { }
    public void SendScroll(int deltaX, int deltaY) { }
    public void Dispose()
    {
        DisposeCount++;
        IsInitialized = false;
        OnDispose?.Invoke();
        if (ThrowOnDispose)
            throw new InvalidOperationException("Fake web backend disposal failed.");
    }
}
