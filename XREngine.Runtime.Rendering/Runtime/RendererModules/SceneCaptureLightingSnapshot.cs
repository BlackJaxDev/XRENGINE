namespace XREngine.Rendering;

/// <summary>Request-owned frozen shadow content with the matching accepted projection/native records.</summary>
public sealed class SceneCaptureLightingSnapshot : IDisposable
{
    private Action? _release;
    private readonly AdvancedShadowCaptureRow[] _rows;
    private readonly AdvancedShadowRecord[] _records;

    internal SceneCaptureLightingSnapshot(object owner, int session, long backendGeneration,
        SceneCaptureShadowSnapshot[] shadows, Action release, float elapsedTime = 0)
    {
        Owner = owner; Session = session; BackendGeneration = backendGeneration; Shadows = shadows; _release = release;
        ElapsedTime = elapsedTime;
        _rows = new AdvancedShadowCaptureRow[shadows.Length];
        _records = new AdvancedShadowRecord[shadows.Length];
        for (int index = 0; index < shadows.Length; index++)
        {
            SceneCaptureShadowSnapshot shadow = shadows[index];
            _rows[index] = new(shadow.LightIndex, shadow.Record, shadow.Texture);
            _records[index] = shadow.Record;
        }
    }

    internal object Owner { get; }
    internal int Session { get; }
    internal long BackendGeneration { get; }
    internal SceneCaptureShadowSnapshot[] Shadows { get; }
    internal float ElapsedTime { get; }
    public bool IsDisposed => _release is null;

    internal AdvancedGlobalResourceCapture ApplyTo(in AdvancedGlobalResourceCapture globals)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return globals with { Shadows = _records, ShadowRows = _rows };
    }

    internal SceneCaptureShadowSnapshot? Find(object light)
    {
        foreach (SceneCaptureShadowSnapshot shadow in Shadows)
            if (ReferenceEquals(shadow.Light, light)) return shadow;
        return null;
    }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
