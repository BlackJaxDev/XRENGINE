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
    public bool IsShadowPass => RuntimeEngine.Rendering.State.IsShadowPass;
    public ELoopType ChildRecalculationLoopType => Engine.EffectiveSettings.RecalcChildMatricesLoopType;
    public bool HumanoidPoseTransportAvailable => Engine.Networking is BaseNetworkingManager;

    public event HumanoidPosePacketHandler? HumanoidPosePacketReceived
    {
        add
        {
            if (Engine.Networking is BaseNetworkingManager networking)
                networking.HumanoidPosePacketReceived += value;
        }
        remove
        {
            if (Engine.Networking is BaseNetworkingManager networking)
                networking.HumanoidPosePacketReceived -= value;
        }
    }

    public void AddAppThreadCoroutine(Func<bool> task)
        => Engine.AddAppThreadCoroutine(task);

    public IDisposable? StartProfileScope(string scopeName)
        => Engine.Profiler.Start(scopeName);

    public T LoadOrGenerateAsset<T>(Func<T>? generateFactory, string assetName, bool allowLoading, params string[] folderNames) where T : XRAsset, new()
        => Engine.LoadOrGenerateAsset(generateFactory, assetName, allowLoading, folderNames);

    public void RenderLine(Vector3 start, Vector3 end, ColorF4 color)
        => RuntimeEngine.Rendering.Debug.RenderLine(start, end, color);

    public void RenderPoint(Vector3 position, ColorF4 color)
        => RuntimeEngine.Rendering.Debug.RenderPoint(position, color);

    public void RenderText(Vector3 position, string text, ColorF4 color, float scale = 0.0012f)
        => RuntimeEngine.Rendering.Debug.RenderText(position, text, color, scale);

    public bool BroadcastHumanoidPose(HumanoidPosePacketKind kind, ushort baselineSequence, int avatarCount, Span<byte> avatarPayload)
        => Engine.Networking is BaseNetworkingManager networking
            && networking.BroadcastHumanoidPose(kind, baselineSequence, avatarCount, avatarPayload);
}
