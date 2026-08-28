using System.Threading;

namespace XREngine.Runtime.InputIntegration;

/// <summary>
/// InputIntegration-owned capture state used to gate gameplay dispatch while a
/// UI surface owns local input. The state is resettable with the adapter lease.
/// </summary>
public interface IRuntimeInputCaptureServices
{
    bool IsUIInputCaptured { get; }
    void SetUIInputCaptured(bool captured);
}

/// <summary>Installs the input-capture state for the active local-input profile.</summary>
public static class RuntimeInputCaptureServices
{
    private sealed class EmptyInputCaptureServices : IRuntimeInputCaptureServices
    {
        public bool IsUIInputCaptured => false;
        public void SetUIInputCaptured(bool captured) { }
    }

    private static IRuntimeInputCaptureServices _current = new EmptyInputCaptureServices();

    public static IRuntimeInputCaptureServices Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value ?? throw new ArgumentNullException(nameof(value)));
    }
}

/// <summary>Thread-safe mutable implementation installed by Bootstrap for local input profiles.</summary>
public sealed class RuntimeInputCaptureState : IRuntimeInputCaptureServices
{
    private int _isUIInputCaptured;

    public bool IsUIInputCaptured => Volatile.Read(ref _isUIInputCaptured) != 0;
    public void SetUIInputCaptured(bool captured) => Volatile.Write(ref _isUIInputCaptured, captured ? 1 : 0);
}
