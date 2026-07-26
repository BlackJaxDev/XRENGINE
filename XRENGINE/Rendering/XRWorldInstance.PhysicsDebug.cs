using System.Diagnostics;
using XREngine.Data.Colors;
using XREngine.Rendering.Physics.DebugVisualization;
using XREngine.Scene.Physics.DebugVisualization;

namespace XREngine.Rendering;

public partial class XRWorldInstance
{
    private PhysicsDebugFrameRenderer _physicsDebugFrameRenderer = new();
    private long _nextEditPhysicsDebugCollectionTimestamp;
    public PhysicsDebugFrameRenderer PhysicsDebugRenderer => _physicsDebugFrameRenderer;

    public object? TargetWorldObject => TargetWorld;
    public string? TargetWorldName => TargetWorld?.Name;
    public object? GameModeObject => GameMode;
    public IRuntimeAmbientSettings? AmbientSettings => _renderState.AmbientSettings;
    public bool PreviewOctrees => TargetWorld?.Settings?.PreviewOctrees ?? false;
    public bool PreviewQuadtrees => TargetWorld?.Settings?.PreviewQuadtrees ?? false;
    IReadOnlyList<XREngine.Scene.SceneNode> IRuntimeRenderWorld.RootNodes => RootNodes;
    public void DebugRenderPhysics(PhysicsDebugDepthMode depthMode)
    {
        if (depthMode == PhysicsDebugDepthMode.DepthTested &&
            RuntimeEngine.Rendering.State.RenderingCamera is { } camera)
            PhysicsScene.IncludeDebugRenderViewBounds(camera.WorldFrustum().GetAABB(false));

        if (depthMode == PhysicsDebugDepthMode.DepthTested && !PhysicsEnabled)
            CollectEditModePhysicsDebugFrame();

        _physicsDebugFrameRenderer.Render(PhysicsScene.DebugFrames, depthMode);
    }

    private void CollectEditModePhysicsDebugFrame()
    {
        long now = Stopwatch.GetTimestamp();
        if (now < _nextEditPhysicsDebugCollectionTimestamp)
            return;

        _nextEditPhysicsDebugCollectionTimestamp =
            now + Stopwatch.Frequency / 30;
        PhysicsScene.DebugRenderCollect();
    }

    private void ResetPhysicsDebugFrameRenderer()
    {
        _physicsDebugFrameRenderer.Dispose();
        _physicsDebugFrameRenderer = new PhysicsDebugFrameRenderer();
        _nextEditPhysicsDebugCollectionTimestamp = 0;
    }
}
