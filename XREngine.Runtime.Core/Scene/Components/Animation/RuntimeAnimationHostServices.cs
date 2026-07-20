using System.Numerics;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Networking;

namespace XREngine.Components.Animation;

/// <summary>
/// Host-owned services used by runtime animation components without depending on the legacy engine facade.
/// </summary>
public interface IRuntimeAnimationHostServices
{
    float DilatedUpdateDeltaSeconds { get; }
    float TargetRenderFrequency { get; }
    long UpdateDeltaTicks { get; }
    long ElapsedTicks { get; }
    bool IsShadowPass { get; }
    ELoopType ChildRecalculationLoopType { get; }
    bool HumanoidPoseTransportAvailable { get; }

    void AddAppThreadCoroutine(Func<bool> task);
    IDisposable? StartProfileScope(string scopeName);
    T LoadOrGenerateAsset<T>(Func<T>? generateFactory, string assetName, bool allowLoading, params string[] folderNames) where T : XRAsset, new();
    void RenderLine(Vector3 start, Vector3 end, ColorF4 color);
    void RenderPoint(Vector3 position, ColorF4 color);
    void RenderText(Vector3 position, string text, ColorF4 color, float scale = 0.0012f);
    bool BroadcastHumanoidPoseFrame(HumanoidPoseFrame frame, bool compress = false);

    event Action<HumanoidPoseFrame>? HumanoidPoseFrameReceived;
}

/// <summary>Process-wide animation host boundary configured by the application composition root.</summary>
public static class RuntimeAnimationHostServices
{
    private static IRuntimeAnimationHostServices _current = new DefaultRuntimeAnimationHostServices();

    public static IRuntimeAnimationHostServices Current
    {
        get => _current;
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }

    private sealed class DefaultRuntimeAnimationHostServices : IRuntimeAnimationHostServices
    {
        public float DilatedUpdateDeltaSeconds => 0.0f;
        public float TargetRenderFrequency => 60.0f;
        public long UpdateDeltaTicks => 0L;
        public long ElapsedTicks => 0L;
        public bool IsShadowPass => false;
        public ELoopType ChildRecalculationLoopType => ELoopType.Sequential;
        public bool HumanoidPoseTransportAvailable => false;

        public event Action<HumanoidPoseFrame>? HumanoidPoseFrameReceived
        {
            add { }
            remove { }
        }

        public void AddAppThreadCoroutine(Func<bool> task)
        {
            ArgumentNullException.ThrowIfNull(task);
            while (!task())
            {
                // Standalone hosts have no frame scheduler, so complete finite startup work inline.
            }
        }

        public IDisposable? StartProfileScope(string scopeName) => null;
        public T LoadOrGenerateAsset<T>(Func<T>? generateFactory, string assetName, bool allowLoading, params string[] folderNames) where T : XRAsset, new()
        {
            T asset = generateFactory?.Invoke() ?? new T();
            asset.Name = assetName;
            return asset;
        }
        public void RenderLine(Vector3 start, Vector3 end, ColorF4 color) { }
        public void RenderPoint(Vector3 position, ColorF4 color) { }
        public void RenderText(Vector3 position, string text, ColorF4 color, float scale = 0.0012f) { }
        public bool BroadcastHumanoidPoseFrame(HumanoidPoseFrame frame, bool compress = false) => false;
    }
}
