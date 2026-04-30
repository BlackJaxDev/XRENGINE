using XREngine.Data.Colors;

namespace XREngine.Rendering;

public partial class XRWorldInstance : IRuntimeRenderWorld
{
    public object? TargetWorldObject => TargetWorld;
    public string? TargetWorldName => TargetWorld?.Name;
    public object? GameModeObject => GameMode;
    public IRuntimeAmbientSettings? AmbientSettings => TargetWorld?.Settings as IRuntimeAmbientSettings;
    IReadOnlyList<XREngine.Scene.SceneNode> IRuntimeRenderWorld.RootNodes => RootNodes;
    public void DebugRenderPhysics()
        => PhysicsScene.DebugRender();
}