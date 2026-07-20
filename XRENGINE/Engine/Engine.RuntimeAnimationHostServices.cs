using System.Numerics;
using XREngine.Components.Animation;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Networking;

namespace XREngine;

internal sealed class EngineRuntimeAnimationHostServices : IRuntimeAnimationHostServices
{
    public float DilatedUpdateDeltaSeconds => Engine.Delta;
    public float TargetRenderFrequency => Engine.Time.Timer.TargetRenderFrequency;
    public long UpdateDeltaTicks => Engine.Time.Timer.Update.DeltaTicks;
    public long ElapsedTicks => Engine.ElapsedTicks;
    public bool IsShadowPass => Engine.Rendering.State.IsShadowPass;
    public ELoopType ChildRecalculationLoopType => Engine.Rendering.Settings.RecalcChildMatricesLoopType;
    public bool HumanoidPoseTransportAvailable => Engine.Networking is BaseNetworkingManager;

    public event Action<HumanoidPoseFrame>? HumanoidPoseFrameReceived
    {
        add
        {
            if (Engine.Networking is BaseNetworkingManager networking)
                networking.HumanoidPoseFrameReceived += value;
        }
        remove
        {
            if (Engine.Networking is BaseNetworkingManager networking)
                networking.HumanoidPoseFrameReceived -= value;
        }
    }

    public void AddAppThreadCoroutine(Func<bool> task)
        => Engine.AddAppThreadCoroutine(task);

    public IDisposable? StartProfileScope(string scopeName)
        => Engine.Profiler.Start(scopeName);

    public T LoadOrGenerateAsset<T>(Func<T>? generateFactory, string assetName, bool allowLoading, params string[] folderNames) where T : XRAsset, new()
        => Engine.LoadOrGenerateAsset(generateFactory, assetName, allowLoading, folderNames);

    public void RenderLine(Vector3 start, Vector3 end, ColorF4 color)
        => Engine.Rendering.Debug.RenderLine(start, end, color);

    public void RenderPoint(Vector3 position, ColorF4 color)
        => Engine.Rendering.Debug.RenderPoint(position, color);

    public void RenderText(Vector3 position, string text, ColorF4 color, float scale = 0.0012f)
        => Engine.Rendering.Debug.RenderText(position, text, color, scale);

    public bool BroadcastHumanoidPoseFrame(HumanoidPoseFrame frame, bool compress = false)
    {
        if (Engine.Networking is not BaseNetworkingManager networking)
            return false;

        networking.BroadcastHumanoidPoseFrame(frame, compress);
        return true;
    }
}
